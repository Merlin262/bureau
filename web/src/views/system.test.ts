import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/svelte'
import System from './System.svelte'
import TerminalView from '../components/TerminalView.svelte'
import { store } from '../lib/store.svelte'
import { http } from '../test/http'
import { flush, host, sys } from '../test/util'
import type { AptState, Me } from '../lib/types'

// xterm é carregado sob demanda pelo terminal: aqui, uma versão mínima que registra o que acontece.
const term = vi.hoisted(() => ({
  written: [] as Uint8Array[],
  onData: null as ((d: string) => void) | null,
  disposed: false,
}))
vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    cols = 100
    rows = 30
    loadAddon() {}
    open() {}
    focus() {}
    write(d: Uint8Array) { term.written.push(d) }
    onData(cb: (d: string) => void) { term.onData = cb; return { dispose() {} } }
    dispose() { term.disposed = true }
  },
}))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }))
vi.mock('@xterm/xterm/css/xterm.css', () => ({}))

class FakeWS {
  static OPEN = 1
  static last: FakeWS
  readyState = 0
  binaryType = ''
  sent: unknown[] = []
  onopen?: () => void
  onmessage?: (e: { data: ArrayBuffer }) => void
  onclose?: () => void
  onerror?: () => void
  constructor(public url: string) { FakeWS.last = this }
  send(d: unknown) { this.sent.push(d) }
  close() { this.readyState = 3; this.onclose?.() }
  open() { this.readyState = 1; this.onopen?.() }
}

class FakeRO { observe() {} disconnect() {} constructor(public cb: () => void) {} }

const me = (over: Partial<Me> = {}): Me => ({ authenticated: true, hostname: 'srv', version: '0.4.8', allowPower: true, terminal: true, ...over })
const apt = (over: Partial<AptState> = {}): AptState => ({
  supported: true, total: 3, security: 1, packages: ['curl', 'libssl3', 'git'], rebootRequired: true, rebootPackages: ['linux-image'],
  checkedAt: 1700000000, upgrading: false, lastResult: 'success', lastRunAt: 1700000000, phased: 2, ...over,
})

function routes() {
  http.on('GET /api/push/config', { webPushKey: 'k', fcm: true, ntfy: true, apns: false })
  http.on('GET /api/auth/tokens', [{ id: 't1', name: 'Pixel', kind: 'app', created: 1700000000, current: true }])
  http.on('GET /api/push/devices', [{ id: 'p1', name: 'Chrome', kind: 'webpush', created: 1700000000, current: false }, { id: 'p2', name: 'X', kind: 'outro', created: 0, current: false }])
  http.on('GET /api/update', { current: 'v0.4.8', latest: 'v0.4.8', status: 'atualizado', message: 'Em dia', checkedAt: Date.now() / 1000 - 600, auto: true, supported: true })
  http.on('GET /api/auth/access', [
    { ts: 1700000000, ip: '203.0.113.9', country: 'BR', method: 'web', ok: true, device: 'Chrome · Windows' },
    { ts: 1700000001, ip: '10.0.0.2', method: 'ssh', ok: false, device: '?' },
  ])
}

const btn = (name: string | RegExp) => screen.getByRole('button', { name })
async function hold(el: HTMLElement, ms = 1300) {
  await fireEvent.pointerDown(el)
  await new Promise((r) => setTimeout(r, ms))
}

describe('Sistema', () => {
  it('identificação, discos, pacotes, backups, aparelhos e acessos', async () => {
    routes()
    http.on('POST /api/apt/upgrade', { ok: true })
    http.on('POST /api/apt/check', { ok: true })
    http.on('GET /api/apt/logs', { text: 'apt log' })
    http.on('POST /api/backups/fotos.service/run', { ok: true })
    http.on('GET /api/backups/fotos.service/logs', { text: 'restic ok' })
    http.on('DELETE /api/auth/tokens/t1', { ok: true })
    http.on('DELETE /api/push/p1', { ok: true })
    http.on('POST /api/push/test', { ok: false, message: 'Nada foi entregue' })
    store.me = me()
    store.host = host()
    store.sys = sys()
    store.apt = apt()
    store.backups = [
      { name: 'Fotos', unit: 'fotos.service', status: 'failed', lastRun: 1, lastSuccess: 1700000000, nextRun: 0, message: 'falhou' },
      { name: 'Docs', unit: 'docs.service', status: 'running', lastRun: 0, lastSuccess: 0, nextRun: 0 },
      { name: 'A', unit: 'a.service', status: 'ok', lastRun: 0, lastSuccess: 0, nextRun: 0 },
      { name: 'B', unit: 'b.service', status: 'unknown', lastRun: 0, lastSuccess: 0, nextRun: 0 },
    ]
    localStorage.setItem('bureau.pushId', 'p1')
    const { container } = render(System)
    await flush()

    expect(container.textContent).toContain('merlin-server')
    expect(container.textContent).toContain('Intel i5 · 4 núcleos')
    expect(container.textContent).toContain('ADIADOS')
    expect(container.textContent).toContain('REINICIALIZAÇÃO NECESSÁRIA (linux-image)')
    expect(container.textContent).toContain('(ok)')
    expect(container.textContent).toMatch(/exige HTTPS|não suporta notificações push/) // jsdom: sem Web Push
    expect(container.textContent).toContain('e o ntfy')
    expect(container.textContent).toContain('SESSÃO DO APP')
    expect(container.textContent).toContain('PUSH · NAVEGADOR')
    expect(container.textContent).toContain('🇧🇷 203.0.113.9')
    expect(container.textContent).toContain('ssh')
    expect(container.textContent).toContain('há 10 min')

    await fireEvent.click(screen.getByText('▸ VER PACOTES'))
    expect(container.textContent).toContain('curl · libssl3 · git')
    await fireEvent.click(screen.getByText('▾ VER PACOTES'))

    await hold(btn('Atualizar pacotes'))
    await fireEvent.click(btn('Reverificar'))
    const logsBtns = screen.getAllByText('Logs')
    await fireEvent.click(logsBtns[0])
    await flush()
    expect(container.textContent).toContain('apt log')
    await fireEvent.click(logsBtns[0])
    expect(http.sent('POST /api/apt/upgrade')).toHaveLength(1)
    expect(http.sent('POST /api/apt/check')).toHaveLength(1)

    const bk = container.querySelectorAll('.bk')[0] as HTMLElement
    await fireEvent.click(within(bk).getByText('Executar'))
    await fireEvent.click(within(bk).getByText('Logs'))
    await flush()
    expect(container.textContent).toContain('restic ok')
    await fireEvent.click(within(bk).getByText('Logs'))
    expect(http.sent('POST /api/backups/fotos.service/run')).toHaveLength(1)

    await fireEvent.click(btn('Testar'))
    await flush()
    expect(store.toasts.at(-1)!.text).toBe('Nada foi entregue')

    await fireEvent.click(btn('Revogar'))
    await fireEvent.click(screen.getAllByText('Remover')[0])
    await flush()
    expect(http.sent('DELETE /api/auth/tokens/t1')).toHaveLength(1)
    expect(http.sent('DELETE /api/push/p1')).toHaveLength(1)
    expect(localStorage.getItem('bureau.pushId')).toBeNull()

    await fireEvent.click(btn('Atualizar'))
    await flush()
    expect(http.sent('GET /api/auth/access')).toHaveLength(2)
  })

  it('estados vazios e desativados', async () => {
    http.on('GET /api/push/config', new Error('offline'))
    http.on('GET /api/update', new Error('offline'))
    http.on('GET /api/auth/access', new Error('offline'))
    store.me = me({ terminal: false, allowPower: false })
    store.host = host({ disks: [] })
    const { container } = render(System)
    await flush()
    expect(container.textContent).toContain('Sem discos detectados.')
    expect(container.textContent).toContain('Consultando…')
    expect(container.textContent).toContain('Nenhum backup configurado')
    expect(container.textContent).toContain('Nenhum aparelho conectado.')
    expect(container.textContent).toContain('Nenhum acesso registrado.')
    expect(container.textContent).toContain('"Terminal"')
    expect(container.textContent).toContain('DESATIVADO NA CONFIGURAÇÃO (AllowPower)')

    store.apt = apt({ supported: false, lastResult: undefined })
    await flush()
    expect(container.textContent).toContain('DESATIVADO NA CONFIGURAÇÃO (SystemUpdates)')
    store.apt = apt({ upgrading: true, total: 0, phased: 0, rebootRequired: false, lastRunAt: 0, lastResult: 'exit-code' })
    await flush()
    expect(container.textContent).toContain('Atualizando pacotes agora…')
    store.apt = apt({ lastResult: 'exit-code', rebootPackages: [] })
    await flush()
    expect(container.textContent).toContain('(exit-code)')
  })

  it('atualizações: verificar agora acompanha até terminar', async () => {
    vi.useFakeTimers()
    routes()
    let checks = 0
    http.on('GET /api/update', () => ({
      current: 'v0.4.8', latest: 'v0.4.9', status: checks++ < 2 ? 'atualizando' : 'erro', message: checks > 2 ? 'falhou' : 'Atualizando…',
      checkedAt: checks > 2 ? Date.now() / 1000 : 0, auto: false, supported: true,
    }))
    http.on('POST /api/update/run', { ok: true, message: 'Verificando atualizações…' })
    store.me = me()
    const { container } = render(System)
    await vi.advanceTimersByTimeAsync(10)
    expect(container.textContent).toContain('Automática desligada.')
    await fireEvent.click(btn('Verificar agora'))
    await vi.advanceTimersByTimeAsync(8000)
    expect(container.textContent).toContain('falhou')
    const n = http.sent('GET /api/update').length
    await vi.advanceTimersByTimeAsync(6000)
    expect(http.sent('GET /api/update').length).toBe(n) // parou de acompanhar
  })

  it('atualizador não instalado e "agora"/"há horas"', async () => {
    routes()
    http.on('GET /api/update', { current: 'v1', status: 'nunca', message: 'Ainda não verificado', checkedAt: 0, auto: false, supported: false })
    store.me = me()
    const { container } = render(System)
    await flush()
    expect(container.textContent).toContain('Atualizador não instalado')
    expect(container.textContent).toContain('nunca')
    http.on('GET /api/update', { current: 'v1', status: 'atualizado', message: 'ok', checkedAt: Date.now() / 1000 - 3 * 3600, auto: true, supported: true })
    const again = render(System)
    await flush()
    expect(again.container.textContent).toContain('há 3 h')
  })

  it('energia, som, sair e backup do BUREAU', { timeout: 20_000 }, async () => {
    routes()
    http.on('POST /api/power/reboot', { ok: true })
    http.on('POST /api/power/poweroff', { ok: true })
    http.on('POST /api/auth/logout', { ok: true })
    http.on('GET /api/backup/export', { kind: 'bureau-backup', files: {} })
    http.on('POST /api/backup/import', { ok: true, message: 'Restaurado' })
    const createObjectURL = vi.fn(() => 'blob:x')
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }))
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    store.me = me()
    store.authed = true
    const { container } = render(System)
    await flush()

    await hold(btn('Reiniciar servidor'))
    await flush()
    expect(http.sent('POST /api/power/reboot')).toHaveLength(1)
    await hold(btn('Desligar servidor'), 2200)
    await flush()
    expect(http.sent('POST /api/power/poweroff')).toHaveLength(1)

    await fireEvent.click(btn('Desligado'))
    expect(localStorage.getItem('bureau.sfx')).toBe('1')
    await fireEvent.click(btn('Ligado'))

    await fireEvent.click(btn('Exportar'))
    await flush()
    expect(createObjectURL).toHaveBeenCalled()
    expect(click).toHaveBeenCalled()

    // Importar: arquivo que não é backup → erro; backup válido → confirmação segurando.
    const input = container.querySelector('input[type=file]') as HTMLInputElement
    const pick = async (content: string) => {
      const file = new File([content], 'b.json', { type: 'application/json' })
      Object.defineProperty(input, 'files', { configurable: true, value: [file] })
      await fireEvent.change(input)
      await flush()
    }
    await pick('{"kind":"outro"}')
    expect(store.toasts.at(-1)!.text).toBe('Arquivo não é um backup do BUREAU')
    await pick('não é json')
    expect(store.toasts.at(-1)!.kind).toBe('err')
    Object.defineProperty(input, 'files', { configurable: true, value: [] })
    await fireEvent.change(input)
    await pick(JSON.stringify({ kind: 'bureau-backup', host: 'velho', created: 1700000000, appVersion: '0.4.0', files: { 'aliases.json': '{}' } }))
    const dlg = screen.getByRole('dialog')
    expect(dlg.textContent).toContain('aliases.json')
    await hold(within(dlg).getByRole('button', { name: 'Restaurar' }))
    await flush()
    expect(http.sent('POST /api/backup/import')).toHaveLength(1)

    await fireEvent.click(btn('Sair'))
    await flush()
    expect(http.sent('POST /api/auth/logout')).toHaveLength(1)
    expect(store.authed).toBe(false)
  })

  it('abre e fecha o terminal', async () => {
    routes()
    http.on('POST /api/terminal/ticket', { ok: false, message: 'Terminal desativado na configuração' })
    store.me = me()
    vi.stubGlobal('ResizeObserver', FakeRO)
    render(System)
    await flush()
    await fireEvent.click(btn('Abrir terminal ▸'))
    await flush(5)
    expect(screen.getByRole('dialog', { name: 'Terminal' }).textContent).toContain('ERRO · Terminal desativado na configuração')
    await fireEvent.click(btn('✕ Fechar'))
    expect(screen.queryByRole('dialog', { name: 'Terminal' })).toBeNull()
  })
})

describe('Terminal', () => {
  it('conecta, envia teclas e tamanho, recebe e encerra', async () => {
    http.on('POST /api/terminal/ticket', { ok: true, message: 'tkt' })
    vi.stubGlobal('WebSocket', FakeWS)
    vi.stubGlobal('ResizeObserver', FakeRO)
    term.written = []
    const onclose = vi.fn()
    const { unmount } = render(TerminalView, { onclose })
    await flush(5)
    const ws = FakeWS.last
    expect(ws.url).toBe(`${location.origin.replace(/^http/, 'ws')}/api/terminal/ws?ticket=tkt`)
    expect(screen.getByText('CONECTANDO')).toBeTruthy()

    ws.open()
    await flush()
    expect(screen.getByText('CONECTADO')).toBeTruthy()
    expect(ws.sent[0]).toBe('{"cols":100,"rows":30}')

    ws.onmessage!({ data: new Uint8Array([104, 105]).buffer })
    expect(term.written).toHaveLength(1)

    term.onData!('ls')
    await fireEvent.click(btn('CTRL'))
    term.onData!('c') // Ctrl+C
    await fireEvent.click(btn('CTRL'))
    term.onData!('1') // fora de A–_: envia normal
    await fireEvent.click(btn('ESC'))
    const dec = (u: unknown) => new TextDecoder().decode(u as Uint8Array)
    expect(ws.sent.slice(1).map(dec)).toEqual(['ls', '\x03', '1', '\x1b'])

    ws.onerror!()
    await flush()
    expect(screen.getByText('ERRO')).toBeTruthy()
    ws.onclose!()
    await flush()
    expect(screen.getByText('ERRO')).toBeTruthy() // erro não é sobrescrito

    unmount()
    expect(term.disposed).toBe(true)
  })

  it('servidor encerra a sessão e falha no tíquete', async () => {
    http.on('POST /api/terminal/ticket', { ok: true, message: 't' })
    vi.stubGlobal('WebSocket', FakeWS)
    vi.stubGlobal('ResizeObserver', FakeRO)
    render(TerminalView, { onclose: () => {} })
    await flush(5)
    FakeWS.last.onclose!()
    await flush()
    expect(screen.getByText('ENCERRADO')).toBeTruthy()
    FakeWS.last.readyState = 3
    await fireEvent.click(btn('TAB')) // fechado: não envia

    http.on('POST /api/terminal/ticket', new Error('offline'))
    const other = render(TerminalView, { onclose: () => {} })
    await flush(5)
    expect(other.container.textContent).toContain('Servidor inacessível')
  })
})
