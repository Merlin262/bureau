import { describe, expect, it, vi } from 'vitest'
import { actionLabel, bytes, bytesParts, duration, int, pct, rate, shortUnit, since } from './format'
import { go, router, step, views } from './router.svelte'
import { openStream, type StreamState } from './sse'
import { api, headers } from './api'
import { act, connect, disconnect, store, toast } from './store.svelte'
import { conn, deviceName, normalizeServer, saveConnection, clearToken, loadConnection, tap, isNative } from './platform'
import { sfx } from './sfx'
import { http, json } from '../test/http'
import { flush, host, sys } from '../test/util'

describe('format', () => {
  it('bytes e taxas', () => {
    expect(bytes(0)).toBe('0 B')
    expect(bytes(-5)).toBe('0 B')
    expect(bytes(512)).toBe('512 B')
    expect(bytes(1536)).toBe('1.5 KB')
    expect(bytes(150 * 2 ** 20)).toBe('150 MB')
    expect(bytes(2 ** 50)).toBe('1024 TB')
    expect(bytesParts(2048)).toEqual(['2.0', 'KB'])
    expect(rate(1024)).toBe('1.0 KB/s')
  })

  it('durações', () => {
    expect(duration(0)).toBe('—')
    expect(duration(-1)).toBe('—')
    expect(duration(42)).toBe('42s')
    expect(duration(125)).toBe('2m')
    expect(duration(3 * 3600 + 120)).toBe('3h 2m')
    expect(duration(2 * 86400 + 3600)).toBe('2d 1h')
    expect(since(0)).toBe('—')
    expect(since(Date.now() / 1000 - 90)).toBe('1m')
  })

  it('miscelânea', () => {
    expect(pct(1, 4)).toBe(25)
    expect(pct(1, 0)).toBe(0)
    expect(int(1234567)).toMatch(/1.234.567/)
    expect(shortUnit('nginx.service')).toBe('nginx')
    expect(shortUnit('docker.socket')).toBe('docker.socket')
    expect(actionLabel('restart')).toBe('REINICIANDO')
    expect(actionLabel('reload')).toBe('RELOAD')
  })
})

describe('router', () => {
  it('navega, circula e segue o hash', () => {
    go('rede')
    expect(router.current).toBe('rede')
    expect(location.hash).toBe('#/rede')
    go('rede')
    step(1)
    expect(router.current).toBe('apps')
    step(2)
    expect(router.current).toBe('painel') // dá a volta
    step(-1)
    expect(router.current).toBe(views[views.length - 1].id)

    location.hash = '#/docker'
    window.dispatchEvent(new HashChangeEvent('hashchange'))
    expect(router.current).toBe('docker')
    location.hash = '#/inexistente'
    window.dispatchEvent(new HashChangeEvent('hashchange'))
    expect(router.current).toBe('painel')
  })
})

/** Corpo SSE que entrega os pedaços e fica aberto até ser abortado. */
function sseResponse(chunks: string[], signal?: AbortSignal | null) {
  const enc = new TextEncoder()
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      for (const ch of chunks) c.enqueue(enc.encode(ch))
      signal?.addEventListener('abort', () => c.error(new DOMException('abort', 'AbortError')))
    },
  })
  return new Response(body, { status: 200 })
}

describe('sse', () => {
  it('interpreta eventos em pedaços, envia cabeçalhos e para ao fechar', async () => {
    const events: [string, string][] = []
    const states: StreamState[] = []
    let seen: RequestInit | undefined
    vi.stubGlobal('fetch', async (_: string, init: RequestInit) => {
      seen = init
      return sseResponse(['retry: 1500\n\nevent: sys\ndata: {"a":', '1}\n\nevent: x\ndata: \n\n', 'data: semnome\n\n'], init.signal)
    })
    const stop = openStream('/api/stream', { Authorization: 'Bearer t' }, (e, d) => events.push([e, d]), (s) => states.push(s))
    await flush(5)
    expect(events).toEqual([['sys', '{"a":1}'], ['message', 'semnome']])
    expect(states).toEqual(['open'])
    expect((seen!.headers as Record<string, string>).Authorization).toBe('Bearer t')
    stop()
    await flush()
    expect(states).toEqual(['open'])
  })

  it('401 avisa e não reconecta', async () => {
    const states: StreamState[] = []
    const f = vi.fn(async () => new Response('', { status: 401 }))
    vi.stubGlobal('fetch', f)
    openStream('/s', {}, () => {}, (s) => states.push(s))
    await flush()
    expect(states).toEqual(['unauthorized'])
    expect(f).toHaveBeenCalledTimes(1)
  })

  it('erro de rede reconecta com espera crescente', async () => {
    vi.useFakeTimers()
    const states: StreamState[] = []
    let n = 0
    vi.stubGlobal('fetch', async () => {
      n++
      if (n === 1) throw new TypeError('rede caiu')
      if (n === 2) return new Response('', { status: 500 })
      return new Response(null, { status: 200 }) // sem corpo: também é falha
    })
    const stop = openStream('/s', {}, () => {}, (s) => states.push(s))
    await vi.advanceTimersByTimeAsync(500)
    await vi.advanceTimersByTimeAsync(1000)
    await vi.advanceTimersByTimeAsync(2000)
    expect(n).toBeGreaterThanOrEqual(3)
    expect(states.every((s) => s === 'closed')).toBe(true)
    stop()
  })

  it('watchdog derruba conexão sem dados', async () => {
    vi.useFakeTimers()
    const states: StreamState[] = []
    vi.stubGlobal('fetch', async (_: string, init: RequestInit) => sseResponse([], init.signal))
    const stop = openStream('/s', {}, () => {}, (s) => states.push(s))
    await vi.advanceTimersByTimeAsync(10_001)
    expect(states).toEqual(['open', 'closed'])
    stop()
  })

  it('fim normal do stream também reconecta', async () => {
    vi.useFakeTimers()
    const states: StreamState[] = []
    vi.stubGlobal('fetch', async () => new Response('event: a\ndata: 1\n\n', { status: 200 }))
    const stop = openStream('/s', {}, () => {}, (s) => states.push(s))
    await vi.advanceTimersByTimeAsync(10)
    expect(states.slice(0, 2)).toEqual(['open', 'closed'])
    stop()
    await vi.advanceTimersByTimeAsync(1000)
  })
})

describe('api', () => {
  it('cabeçalhos com CSRF, token e JSON', () => {
    expect(headers()).toEqual({ 'X-Bureau': '1' })
    conn.token = 'abc'
    expect(headers(true)).toEqual({ 'X-Bureau': '1', Authorization: 'Bearer abc', 'Content-Type': 'application/json' })
    conn.token = ''
  })

  it('erros: servidor fora, 401, 429 e HTTP sem corpo', async () => {
    http.on('GET /api/auth/me', new Error('offline'))
    await expect(api.me()).rejects.toThrow('Servidor inacessível')
    conn.base = 'http://srv:8420'
    await expect(api.me()).rejects.toThrow('Servidor inacessível: http://srv:8420')
    conn.base = ''

    store.authed = true
    http.on('GET /api/monitors', json({}, 401))
    await expect(api.monitors()).rejects.toThrow('Sessão expirada')
    expect(store.authed).toBe(false)

    http.on('POST /api/auth/login', json({ ok: false, message: 'ACESSO NEGADO' }, 401))
    expect(await api.login('x')).toEqual({ ok: false, message: 'ACESSO NEGADO' })

    http.on('POST /api/auth/token', json({}, 429))
    await expect(api.token('x', 'y')).rejects.toThrow('Muitas tentativas')

    http.on('GET /api/update', new Response('erro', { status: 500 }))
    await expect(api.updateStatus()).rejects.toThrow('Falha HTTP 500')
  })

  it('todas as rotas chamam o endereço certo', async () => {
    const ok = { ok: true }
    const calls: [() => Promise<unknown>, string][] = [
      [api.logout, 'POST /api/auth/logout'],
      [api.tokens, 'GET /api/auth/tokens'],
      [() => api.revoke('a/b'), 'DELETE /api/auth/tokens/a%2Fb'],
      [api.availableUnits, 'GET /api/services/available'],
      [() => api.watch(['x']), 'PUT /api/services/watch'],
      [() => api.service('nginx.service', 'restart'), 'POST /api/services/nginx.service/restart'],
      [() => api.serviceLogs('nginx.service'), 'GET /api/services/nginx.service/logs'],
      [() => api.container('abc', 'stop'), 'POST /api/containers/abc/stop'],
      [() => api.containerLogs('abc'), 'GET /api/containers/abc/logs'],
      [() => api.blocking(false, 300), 'POST /api/pihole/blocking'],
      [() => api.wol('aa'), 'POST /api/network/wol'],
      [api.wolState, 'GET /api/network/wol'],
      [() => api.wolUpdate('aa', 'n', 'u'), 'PUT /api/network/wol/aa'],
      [() => api.wolRemove('aa'), 'DELETE /api/network/wol/aa'],
      [() => api.wolPower('aa', 'shutdown'), 'POST /api/network/wol/aa/shutdown'],
      [() => api.alias('aa', 'TV'), 'PUT /api/network/alias'],
      [() => api.power('reboot'), 'POST /api/power/reboot'],
      [api.updateStatus, 'GET /api/update'],
      [() => api.metrics('7d'), 'GET /api/metrics'],
      [api.aptUpgrade, 'POST /api/apt/upgrade'],
      [api.aptCheck, 'POST /api/apt/check'],
      [api.aptLogs, 'GET /api/apt/logs'],
      [() => api.backupRun('b.service'), 'POST /api/backups/b.service/run'],
      [() => api.backupLogs('b.service'), 'GET /api/backups/b.service/logs'],
      [() => api.haToggle('light.x'), 'POST /api/homeassistant/toggle'],
      [api.monitors, 'GET /api/monitors'],
      [() => api.saveMonitors([]), 'PUT /api/monitors'],
      [() => api.queries(), 'GET /api/pihole/queries'],
      [() => api.queries('1.2.3.4'), 'GET /api/pihole/queries'],
      [() => api.domain('a.com', 'deny'), 'POST /api/pihole/domain'],
      [() => api.block({ mac: 'aa', manual: true, days: [], start: null, end: null }), 'PUT /api/network/block'],
      [() => api.unblock('aa'), 'DELETE /api/network/block/aa'],
      [api.access, 'GET /api/auth/access'],
      [api.terminalTicket, 'POST /api/terminal/ticket'],
      [api.backupExport, 'GET /api/backup/export'],
      [() => api.backupImport({}), 'POST /api/backup/import'],
      [api.updateRun, 'POST /api/update/run'],
      [api.pushConfig, 'GET /api/push/config'],
      [api.pushDevices, 'GET /api/push/devices'],
      [() => api.pushRegister({ kind: 'fcm', name: 'x', token: 't' }), 'POST /api/push/register'],
      [() => api.pushRemove('id'), 'DELETE /api/push/id'],
      [api.pushTest, 'POST /api/push/test'],
    ]
    for (const [fn, route] of calls) {
      http.on(route, ok)
      await fn()
      const last = http.calls[http.calls.length - 1]
      expect(`${last.method} ${last.path}`).toBe(route)
    }
    expect(http.calls.find((c) => c.query === '?client=1.2.3.4')).toBeTruthy()
    expect(http.calls.find((c) => c.query === '?range=7d')).toBeTruthy()
    expect(http.sent('POST /api/pihole/blocking')[0].body).toEqual({ enabled: false, timer: 300 })
  })
})

describe('store', () => {
  it('toast some sozinho (erro fica mais tempo)', () => {
    vi.useFakeTimers()
    toast('oi', 'ok')
    toast('falhou', 'err')
    expect(store.toasts.map((t) => t.text)).toEqual(['oi', 'falhou'])
    vi.advanceTimersByTime(3300)
    expect(store.toasts.map((t) => t.text)).toEqual(['falhou'])
    vi.advanceTimersByTime(3000)
    expect(store.toasts).toEqual([])
  })

  it('act: pendente, sucesso, falha, exceção e chave repetida', async () => {
    let release!: (v: { ok: boolean; message?: string }) => void
    const p = act('k', 'restart', () => new Promise((r) => (release = r)), 'Feito')
    expect(store.pending.k).toBe('restart')
    const dup = vi.fn()
    await act('k', '', dup)
    expect(dup).not.toHaveBeenCalled()
    release({ ok: true })
    await p
    expect(store.pending.k).toBeUndefined()
    expect(store.toasts.at(-1)).toMatchObject({ kind: 'ok', text: 'Feito' })

    await act('a', '', async () => ({ ok: true, message: 'msg do servidor' }))
    expect(store.toasts.at(-1)!.text).toBe('msg do servidor')
    await act('b', '', async () => ({ ok: true }))
    expect(store.toasts.at(-1)!.text).toBe('Concluído')
    await act('c', '', async () => ({ ok: false }))
    expect(store.toasts.at(-1)).toMatchObject({ kind: 'err', text: 'Falha' })
    await act('d', '', async () => { throw new Error('boom') })
    expect(store.toasts.at(-1)!.text).toBe('boom')
    await act('e', '', async () => { throw 'texto' })
    expect(store.toasts.at(-1)!.text).toBe('texto')
  })

  it('connect distribui os eventos do stream para o estado', async () => {
    const enc = new TextEncoder()
    let ctl!: ReadableStreamDefaultController<Uint8Array>
    vi.stubGlobal('fetch', async (_: string, init: RequestInit) => {
      const body = new ReadableStream<Uint8Array>({ start(c) { ctl = c; init.signal?.addEventListener('abort', () => c.error(new Error('abort'))) } })
      return new Response(body, { status: 200 })
    })
    const send = (event: string, data: unknown) => ctl.enqueue(enc.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`))
    conn.token = 'tok'
    connect()
    await flush()
    expect(store.connected).toBe(true)

    send('sys', sys({ cpu: 33 }))
    send('history', { cpu: [1], mem: [2], rx: [3], tx: [4] })
    send('sys', sys({ cpu: 44, memTotal: 0 }))
    send('host', host())
    send('services', [{ unit: 'a' }, { unit: 'a' }, { unit: 'b' }])
    for (const t of ['containers', 'pihole', 'network', 'apt', 'backups', 'homeassistant', 'jellyfin', 'monitors', 'blocks', 'wol'])
      send(t, t === 'containers' || t === 'pihole' || t === 'apt' || t === 'homeassistant' || t === 'jellyfin' ? { x: t } : [t])
    send('alerts', [{ ts: 5, title: 'velho', body: '', level: 'crit' }])
    send('alerts', [{ ts: 9, title: 'novo', body: 'b', level: 'ok' }, { ts: 8, title: 'outro', body: 'c', level: 'crit' }, { ts: 5, title: 'velho', body: '', level: 'crit' }])
    send('desconhecido', {})
    await flush()

    expect(store.sys!.cpu).toBe(44)
    expect(store.hist.cpu).toEqual([1, 44])
    expect(store.hist.mem).toEqual([2, 0])
    expect(store.services!.map((s) => s.unit)).toEqual(['a', 'b'])
    expect(store.wol).toEqual(['wol'])
    expect(store.alerts).toHaveLength(3)
    expect(store.toasts.map((t) => t.text)).toEqual(['outro — c', 'novo — b']) // só os alertas novos viram aviso

    disconnect()
    expect(store.connected).toBe(false)
    conn.token = ''
  })

  it('histórico de 1 s mantém no máximo 120 pontos', async () => {
    const enc = new TextEncoder()
    const blocks = Array.from({ length: 125 }, (_, i) => `event: sys\ndata: ${JSON.stringify(sys({ cpu: i }))}\n\n`).join('')
    vi.stubGlobal('fetch', async (_: string, init: RequestInit) =>
      new Response(new ReadableStream({ start(c) { c.enqueue(enc.encode(blocks)); init.signal?.addEventListener('abort', () => c.error(new Error('x'))) } }), { status: 200 }))
    connect()
    await flush()
    expect(store.hist.cpu).toHaveLength(120)
    expect(store.hist.cpu.at(-1)).toBe(124)
    disconnect()
  })

  it('stream 401 desloga', async () => {
    vi.stubGlobal('fetch', async () => new Response('', { status: 401 }))
    store.authed = true
    connect()
    await flush()
    expect(store.authed).toBe(false)
  })

  it('servidor atualizado recarrega a página', async () => {
    vi.useFakeTimers()
    const reload = vi.fn()
    vi.stubGlobal('location', { ...location, reload })
    const enc = new TextEncoder()
    vi.stubGlobal('fetch', async () => new Response(new ReadableStream({
      start(c) {
        c.enqueue(enc.encode(`event: host\ndata: ${JSON.stringify(host({ version: '1.0' }))}\n\n`))
        c.enqueue(enc.encode(`event: host\ndata: ${JSON.stringify(host({ version: '1.1' }))}\n\n`))
      },
    }), { status: 200 }))
    connect()
    await vi.advanceTimersByTimeAsync(1600)
    expect(store.toasts.some((t) => t.text === 'Servidor atualizado para v1.1')).toBe(true)
    expect(reload).toHaveBeenCalled()
    disconnect()
  })

  it('aba em segundo plano fecha o stream e volta ao reabrir', async () => {
    vi.useFakeTimers()
    const f = vi.fn(async (_: string, init: RequestInit) =>
      new Response(new ReadableStream({ start(c) { init.signal?.addEventListener('abort', () => c.error(new Error('x'))) } }), { status: 200 }))
    vi.stubGlobal('fetch', f)
    store.authed = true
    connect()
    await vi.advanceTimersByTimeAsync(1)
    expect(store.connected).toBe(true)

    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true })
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(30_001)
    expect(store.connected).toBe(false)
    const before = f.mock.calls.length

    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false })
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(1)
    expect(f).toHaveBeenCalledTimes(before + 1)
    disconnect()
  })
})

describe('platform (navegador)', () => {
  it('normaliza o endereço do servidor', () => {
    expect(normalizeServer('  ')).toBe('')
    expect(normalizeServer('192.168.0.5')).toBe('http://192.168.0.5:8420')
    expect(normalizeServer('192.168.0.5:9000/')).toBe('http://192.168.0.5:9000')
    expect(normalizeServer('merlin-server')).toBe('http://merlin-server:8420')
    expect(normalizeServer('casa.local')).toBe('http://casa.local:8420')
    expect(normalizeServer('bureau.merlin.api.br')).toBe('https://bureau.merlin.api.br')
    expect(normalizeServer('HTTP://x.ts.net//')).toBe('HTTP://x.ts.net')
  })

  it('nome do aparelho pelo navegador', () => {
    const ua = (s: string) => vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(s)
    ua('Mozilla/5.0 (Windows NT 10.0) Chrome/130 Edg/130')
    expect(deviceName()).toBe('Edge · Windows')
    ua('Mozilla/5.0 (Linux; Android 14) Chrome/130')
    expect(deviceName()).toBe('Chrome · Android')
    ua('Mozilla/5.0 (iPhone) Safari/604')
    expect(deviceName()).toBe('Safari · iOS')
    ua('Mozilla/5.0 (Macintosh; Mac OS X) Firefox/130')
    expect(deviceName()).toBe('Firefox · macOS')
    ua('Mozilla/5.0 (X11; Linux) Firefox/130')
    expect(deviceName()).toBe('Firefox · Linux')
    ua('curl')
    expect(deviceName()).toBe('Navegador')
  })

  it('conexão no navegador fica só em memória', async () => {
    expect(isNative).toBe(false)
    await loadConnection()
    await saveConnection('http://x', 'tok')
    expect(conn).toEqual({ base: 'http://x', token: 'tok' })
    await clearToken()
    expect(conn.token).toBe('')
    conn.base = ''
    await tap()
  })
})

describe('sfx', () => {
  it('liga, desliga, persiste e toca sem quebrar', () => {
    const osc = { type: '', frequency: { setValueAtTime: vi.fn(), exponentialRampToValueAtTime: vi.fn() }, connect: vi.fn(() => ({ connect: vi.fn() })), start: vi.fn(), stop: vi.fn() }
    class FakeAudio {
      currentTime = 0
      destination = {}
      createOscillator = () => osc
      createGain = () => ({ gain: { setValueAtTime: vi.fn(), exponentialRampToValueAtTime: vi.fn() } })
    }
    vi.stubGlobal('AudioContext', FakeAudio)
    sfx.enabled = false
    sfx.confirm()
    expect(osc.start).not.toHaveBeenCalled()
    sfx.enabled = true
    expect(localStorage.getItem('bureau.sfx')).toBe('1')
    sfx.move(); sfx.select(); sfx.confirm(); sfx.error()
    expect(osc.start).toHaveBeenCalledTimes(4)
    sfx.enabled = false
    expect(sfx.enabled).toBe(false)
  })
})
