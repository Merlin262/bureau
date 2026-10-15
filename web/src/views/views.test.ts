import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/svelte'
import Dashboard from './Dashboard.svelte'
import Services from './Services.svelte'
import Containers from './Containers.svelte'
import PiHole from './PiHole.svelte'
import Apps from './Apps.svelte'
import Login from './Login.svelte'
import { store } from '../lib/store.svelte'
import { router } from '../lib/router.svelte'
import { http, json } from '../test/http'
import { flush, host, pihole, sys } from '../test/util'

const btn = (name: string | RegExp) => screen.getByRole('button', { name })

describe('Login (navegador)', () => {
  it('entra com a senha certa e mostra o erro com a errada', async () => {
    const onsuccess = vi.fn()
    render(Login, { onsuccess, version: '0.4.8' })
    expect(screen.getByText(/v0.4.8/)).toBeTruthy()
    expect(screen.queryByLabelText('SERVIDOR')).toBeNull() // só no app

    http.on('POST /api/auth/login', json({ ok: false, message: 'ACESSO NEGADO' }, 401))
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'errada' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(screen.getByText('▲ ACESSO NEGADO')).toBeTruthy()
    expect(onsuccess).not.toHaveBeenCalled()

    http.on('POST /api/auth/login', { ok: true })
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'certa' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(onsuccess).toHaveBeenCalled()
    expect(http.sent('POST /api/auth/login').at(-1)!.body).toEqual({ password: 'certa' })
  })

  it('erro de rede e senha vazia', async () => {
    render(Login, { onsuccess: () => {} })
    const form = screen.getByLabelText('CHAVE DE ACESSO').closest('form')!
    await fireEvent.submit(form)
    expect(http.calls).toHaveLength(0)
    http.on('POST /api/auth/login', new Error('offline'))
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'x' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(screen.getByText('▲ Servidor inacessível')).toBeTruthy()

    http.on('POST /api/auth/login', { ok: false })
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'x' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(screen.getByText('▲ ACESSO NEGADO')).toBeTruthy()
  })
})

describe('Painel', () => {
  it('métricas, alertas, atalhos, Pi-hole e histórico', async () => {
    const series = { range: '24h', step: 240, t: [1, 2, 3], cpu: [1, 2, 3], mem: [4, 5, 6], temp: [null, 50, 51], rx: [1, 2, 3], tx: [1, 2, 3], battery: [null, null, null] }
    http.on('GET /api/metrics', (_: unknown, url: URL) => ({ ...series, range: url.searchParams.get('range') }))
    http.on('POST /api/pihole/blocking', { ok: true })
    store.sys = sys({ cpu: 95, tempC: 90, battery: { percent: 40, status: 'Discharging' } })
    store.hist = { cpu: [1, 2], mem: [3, 4], rx: [5, 6], tx: [7, 8] }
    store.services = [
      { unit: 'nginx.service', description: '', active: 'failed', sub: 'failed', pid: 0, memory: 0, since: 0 },
      { unit: 'cron.service', description: '', active: 'active', sub: 'running', pid: 1, memory: 0, since: 0 },
    ]
    store.containers = { available: true, items: [{ id: 'a', name: 'jf', image: '', state: 'running', status: '', ports: '', created: 0 }] }
    store.network = [{ ip: '1', mac: 'm', online: true, randomMac: false, firstSeen: 0, dnsQueries: 0 }]
    store.pihole = pihole({ blocking: false })
    store.alerts = [
      { ts: 1700000000000, kind: 'disk', level: 'warn', title: 'Disco', body: 'cheio', view: 'sistema' },
      { ts: 1700000001000, kind: 'power', level: 'crit', title: 'Energia', body: 'caiu', view: 'painel' },
      { ts: 1700000002000, kind: 'x', level: 'ok', title: 'Ok', body: 'voltou', view: 'rede' },
    ]
    const { container } = render(Dashboard)
    await flush()

    const alerts = container.querySelector('.alerts')!.textContent!
    expect(alerts).toContain('bateria em 40%')
    expect(alerts).toContain('1 serviço(s) em falha: nginx')
    expect(alerts).toContain('Bloqueio de anúncios suspenso')
    expect(alerts).toContain('Temperatura crítica: 90°C')
    expect(container.querySelectorAll('.chart')).toHaveLength(4)
    expect(http.sent('GET /api/metrics')[0].query).toBe('?range=24h')

    await fireEvent.click(btn('7D'))
    await flush()
    expect(http.sent('GET /api/metrics').at(-1)!.query).toBe('?range=7d')
    expect(localStorage.getItem('bureau.range')).toBe('7d')

    await fireEvent.click(btn('REATIVAR'))
    await flush()
    expect(http.sent('POST /api/pihole/blocking')[0].body).toEqual({ enabled: true })

    await fireEvent.click(screen.getByText('Disco').closest('button')!)
    expect(router.current).toBe('sistema')
    for (const [label, view] of [[/SERVIÇOS/, 'servicos'], [/CONTÊINERES/, 'docker'], [/REDE/, 'rede']] as const) {
      await fireEvent.click(screen.getByText(label).closest('button')!)
      expect(router.current).toBe(view)
    }
    await fireEvent.click(screen.getByLabelText('Abrir Pi-hole'))
    expect(router.current).toBe('pihole')
  })

  it('sem dados ainda e Pi-hole fora', async () => {
    http.on('GET /api/metrics', { range: '24h', step: 240, t: [], cpu: [], mem: [], temp: [], rx: [], tx: [], battery: [] })
    store.pihole = pihole({ online: false })
    store.containers = { available: false, items: [] }
    store.sys = sys({ battery: undefined, tempC: undefined, swapTotal: 0 })
    const { container } = render(Dashboard)
    await flush()
    expect(container.textContent).toContain('Coletando dados')
    expect(container.textContent).toContain('SEM SINAL')
    expect(container.textContent).toContain('INDISPONÍVEL')
    expect(container.textContent).toContain('SEM BATERIA')
    expect(container.textContent).toContain('Pi-hole sem resposta')
    expect(container.textContent).toContain('Nenhuma ocorrência registrada.')
  })

  it('carregando e Pi-hole ativo pausa por 5 min', async () => {
    http.on('GET /api/metrics', new Error('offline'))
    http.on('POST /api/pihole/blocking', { ok: true })
    store.pihole = pihole()
    const { container } = render(Dashboard)
    await flush()
    expect(container.textContent).toContain('Carregando…')
    await fireEvent.click(btn('PAUSAR 5M'))
    await flush()
    expect(http.sent('POST /api/pihole/blocking')[0].body).toEqual({ enabled: false, timer: 300 })
    store.pihole = pihole({ configured: false, online: false })
    await flush()
    expect(container.textContent).toContain('NÃO CONFIGURADO')
  })
})

describe('Serviços', () => {
  const svc = (unit: string, active: string) => ({ unit, description: `desc ${unit}`, active, sub: '', pid: 1, memory: active === 'active' ? 1048576 : 0, since: 1 })

  it('ações, logs e escolher o que monitorar', async () => {
    http.on('POST /api/services/nginx.service/restart', { ok: true })
    http.on('POST /api/services/nginx.service/stop', { ok: true })
    http.on('POST /api/services/cups.service/start', { ok: false, message: 'negado' })
    http.on('GET /api/services/nginx.service/logs', { text: 'log do nginx' })
    http.on('GET /api/services/available', [
      { unit: 'nginx.service', description: 'web', active: 'active' },
      { unit: 'ssh.service', description: 'OpenSSH', active: 'failed' },
      { unit: 'odd.service', description: 'x', active: 'weird' },
    ])
    http.on('PUT /api/services/watch', { ok: true })
    store.services = [svc('nginx.service', 'active'), svc('cups.service', 'failed'), svc('ghost.service', 'not-found'), svc('x.service', 'activating')]
    const { container } = render(Services)

    expect(container.textContent).toContain('NÃO ENCONTRADO')
    expect(container.textContent).toContain('1.0 MB')
    await fireEvent.click(within(container.querySelectorAll('.svc')[0] as HTMLElement).getByText('Reiniciar'))
    await fireEvent.click(within(container.querySelectorAll('.svc')[0] as HTMLElement).getByText('Parar'))
    await fireEvent.click(within(container.querySelectorAll('.svc')[1] as HTMLElement).getByText('Iniciar'))
    await flush()
    expect(http.sent('POST /api/services/nginx.service/restart')).toHaveLength(1)
    expect(store.toasts.some((t) => t.text === 'negado')).toBe(true)

    await fireEvent.click(within(container.querySelectorAll('.svc')[0] as HTMLElement).getByText('Logs'))
    await flush()
    expect(container.textContent).toContain('log do nginx')
    await fireEvent.click(within(container.querySelectorAll('.svc')[0] as HTMLElement).getByText('Logs'))

    await fireEvent.click(btn('+ Monitorar'))
    await flush()
    const dlg = screen.getByRole('dialog')
    expect(within(dlg).getByText('Salvar (4)')).toBeTruthy()
    await fireEvent.input(within(dlg).getByPlaceholderText('Buscar unidade…'), { target: { value: 'openssh' } })
    expect(within(dlg).getAllByRole('checkbox')).toHaveLength(1)
    await fireEvent.click(within(dlg).getByRole('checkbox'))
    await fireEvent.input(within(dlg).getByPlaceholderText('Buscar unidade…'), { target: { value: 'nada-disso' } })
    expect(within(dlg).getByText('Nada encontrado.')).toBeTruthy()
    await fireEvent.input(within(dlg).getByPlaceholderText('Buscar unidade…'), { target: { value: '' } })
    await fireEvent.click(within(dlg).getAllByRole('checkbox')[0]) // desmarca nginx
    await fireEvent.click(within(dlg).getByText('Salvar (4)'))
    await flush()
    expect((http.sent('PUT /api/services/watch')[0].body as { units: string[] }).units).toEqual(['cups.service', 'ghost.service', 'x.service', 'ssh.service'])
  })

  it('vazio, carregando e falha ao listar', async () => {
    http.on('GET /api/services/available', new Error('offline'))
    const { container } = render(Services)
    expect(container.textContent).toContain('Estabelecendo contato…')
    store.services = []
    await flush()
    expect(container.textContent).toContain('Nenhum serviço monitorado')
    await fireEvent.click(btn('+ Monitorar'))
    await flush()
    expect(store.toasts.at(-1)!.kind).toBe('err')
    expect(screen.getByText('Consultando systemd…')).toBeTruthy()
    await fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).toBeNull()
  })
})

describe('Contêineres', () => {
  it('lista, ações e logs', async () => {
    http.on('POST /api/containers/abc/restart', { ok: true })
    http.on('POST /api/containers/abc/stop', { ok: true })
    http.on('POST /api/containers/def/start', { ok: true })
    http.on('GET /api/containers/abc/logs', { text: 'log do contêiner' })
    store.containers = {
      available: true,
      items: [
        { id: 'abc', name: 'jellyfin', image: 'jellyfin/jellyfin', state: 'running', status: 'Up 2h', ports: '8096→8096/tcp', created: 0 },
        { id: 'def', name: 'pihole', image: 'pihole', state: 'exited', status: 'Exited', ports: '', created: 0 },
        { id: 'ghi', name: 'morto', image: 'x', state: 'dead', status: 'Dead', ports: '', created: 0 },
        { id: 'jkl', name: 'loop', image: 'x', state: 'restarting', status: 'Restarting', ports: '', created: 0 },
      ],
    }
    const { container } = render(Containers)
    expect(container.querySelector('.sq.fail')).toBeTruthy()
    await fireEvent.click(btn('Reiniciar'))
    await fireEvent.click(btn('Parar'))
    await fireEvent.click(screen.getAllByText('Iniciar')[0])
    await flush()
    expect(http.sent('POST /api/containers/abc/restart')).toHaveLength(1)
    expect(http.sent('POST /api/containers/def/start')).toHaveLength(1)
    await fireEvent.click(screen.getAllByText('Logs')[0])
    await flush()
    expect(container.textContent).toContain('log do contêiner')
    await fireEvent.click(screen.getAllByText('Logs')[0])
  })

  it('Docker indisponível e vazio', async () => {
    store.containers = { available: false, error: 'Sem permissão', items: [] }
    const { container } = render(Containers)
    expect(container.textContent).toContain('Sem permissão')
    store.containers = { available: true, items: [] }
    await flush()
    expect(container.textContent).not.toContain('Sem permissão')
    store.containers = null
    await flush()
  })
})

describe('Pi-hole', () => {
  it('não configurado, sem sinal e carregando', async () => {
    http.on('GET /api/pihole/queries', [])
    const { container } = render(PiHole)
    expect(container.textContent).toContain('Estabelecendo contato…')
    store.pihole = pihole({ configured: false })
    await flush()
    expect(container.textContent).toContain('NÃO CONFIGURADO')
    store.pihole = pihole({ online: false, error: 'Senha do Pi-hole inválida' })
    await flush()
    expect(container.textContent).toContain('Senha do Pi-hole inválida')
    store.pihole = pihole({ online: false })
    await flush()
    expect(container.textContent).toContain('O Pi-hole não respondeu.')
  })

  it('pausar, reativar, consultas ao vivo e histórico', async () => {
    vi.useFakeTimers()
    const queries = [
      { ts: 1700000000, domain: 'ads.example', type: 'A', client: '192.168.0.5', clientName: 'pc', status: 'GRAVITY', blocked: true },
      { ts: 1700000005, domain: 'ok.example', type: 'A', client: '192.168.0.6', status: 'FORWARDED', blocked: false },
      { ts: 1700000003, domain: 'raro.example', type: 'A', client: '192.168.0.6', status: 'RETRIED', blocked: false },
    ]
    http.on('GET /api/pihole/queries', queries)
    http.on('POST /api/pihole/blocking', { ok: true })
    http.on('POST /api/pihole/domain', { ok: true })
    store.pihole = pihole({ topBlocked: [] })
    const { container } = render(PiHole)
    await vi.advanceTimersByTimeAsync(10)

    expect(container.textContent).toContain('Sem dados.')
    const rows = container.querySelectorAll('.q')
    expect(rows[0].textContent).toContain('ok.example') // mais recente primeiro
    expect(container.textContent).toContain('ENCAMINHADO')
    expect(container.textContent).toContain('RETRIED')

    for (const [label, timer] of [['Pausar 5 min', 300], ['30 min', 1800], ['1 h', 3600], ['Indefinido', undefined]] as const) {
      await fireEvent.click(btn(label))
      await vi.advanceTimersByTimeAsync(10)
      expect(http.sent('POST /api/pihole/blocking').at(-1)!.body).toEqual(timer ? { enabled: false, timer } : { enabled: false })
    }

    await fireEvent.click(btn('Só bloqueadas'))
    expect(container.querySelectorAll('.q')).toHaveLength(1)
    await fireEvent.click(btn('Só bloqueadas'))
    await fireEvent.input(screen.getByPlaceholderText('Filtrar por domínio ou aparelho…'), { target: { value: 'PC' } })
    expect(container.querySelectorAll('.q')).toHaveLength(1)
    await fireEvent.click(within(container.querySelector('.q') as HTMLElement).getByText('Liberar'))
    await vi.advanceTimersByTimeAsync(10)
    expect(http.sent('POST /api/pihole/domain')[0].body).toEqual({ domain: 'ads.example', list: 'allow' })
    await fireEvent.input(screen.getByPlaceholderText('Filtrar por domínio ou aparelho…'), { target: { value: 'nada' } })
    expect(container.textContent).toContain('Nenhuma consulta.')

    const before = http.sent('GET /api/pihole/queries').length
    await fireEvent.click(btn('❚❚ Pausar'))
    await vi.advanceTimersByTimeAsync(4100)
    expect(http.sent('GET /api/pihole/queries').length).toBe(before)
    await fireEvent.click(btn('▶ Retomar'))
    await vi.advanceTimersByTimeAsync(2100)
    expect(http.sent('GET /api/pihole/queries').length).toBeGreaterThan(before)

    const hit = container.querySelectorAll('.hit')[0]
    await fireEvent.mouseEnter(hit)
    expect(container.textContent).toContain('40 CONSULTAS · 10 BLOQ.')
    await fireEvent.mouseLeave(container.querySelector('svg.hist')!)
    expect(container.textContent).toContain('INTERVALOS DE 10 MIN')

    store.pihole = pihole({ blocking: false, timer: 120 })
    await vi.advanceTimersByTimeAsync(10)
    expect(container.textContent).toContain('Retorno automático em 2m')
    await fireEvent.click(btn('Reativar proteção'))
    await vi.advanceTimersByTimeAsync(10)
    expect(http.sent('POST /api/pihole/blocking').at(-1)!.body).toEqual({ enabled: true })
    store.pihole = pihole({ blocking: false })
    await vi.advanceTimersByTimeAsync(10)
    expect(container.textContent).toContain('Sem retorno automático')
  })
})

describe('Apps', () => {
  it('Home Assistant e Jellyfin em todos os estados', async () => {
    http.on('POST /api/homeassistant/toggle', { ok: true })
    const { container } = render(Apps)
    expect(container.textContent!.match(/Estabelecendo contato…/g)).toHaveLength(2)

    store.homeassistant = { configured: false, online: false, total: 0, unavailable: 0, domains: [], entities: [] }
    store.jellyfin = { configured: false, online: false, library: [], sessions: [] }
    await flush()
    expect(container.textContent!.match(/Não configurado/g)).toHaveLength(2)

    store.homeassistant = { configured: true, online: false, error: 'Token inválido', total: 0, unavailable: 0, domains: [], entities: [] }
    store.jellyfin = { configured: true, online: false, library: [], sessions: [] }
    await flush()
    expect(container.textContent).toContain('Token inválido')
    expect(container.textContent).toContain('Sem resposta')

    store.homeassistant = {
      configured: true, online: true, version: '2026.10', location: 'Casa', total: 120, unavailable: 2,
      domains: [{ name: 'light', count: 5 }, { name: 'zwave', count: 1 }],
      entities: [
        { id: 'light.sala', name: 'Sala', domain: 'light', state: 'on' },
        { id: 'switch.tv', name: 'TV', domain: 'switch', state: 'off' },
        { id: 'sensor.t', name: 'Temp', domain: 'sensor', state: '22', unit: '°C' },
        { id: 'fan.q', name: 'Ventilador', domain: 'fan', state: 'unavailable' },
      ],
    }
    store.jellyfin = {
      configured: true, online: true, name: 'Bureau', version: '10.11', library: [{ name: 'Filmes', count: 12 }],
      sessions: [
        { user: 'joao', title: 'Dark', client: 'TV', device: 'Sala', paused: true, progress: 25, transcoding: true },
        { user: 'ana', title: 'Filme', paused: false, progress: 50, transcoding: false },
      ],
    }
    await flush()
    expect(container.textContent).toContain('luzes 5')
    expect(container.textContent).toContain('zwave 1')
    expect(container.textContent).toContain('22 °C')
    expect(container.textContent).toContain('INDISPONÍVEL')
    expect(container.textContent).toContain('TRANSCODIFICANDO')
    expect(container.textContent).toContain('ana · —')
    await fireEvent.click(btn('Desligar'))
    await fireEvent.click(btn('Ligar'))
    await flush()
    expect(http.sent('POST /api/homeassistant/toggle').map((c) => c.body)).toEqual([{ entityId: 'light.sala' }, { entityId: 'switch.tv' }])
    expect(store.toasts.map((t) => t.text)).toContain('Sala: desligado')

    store.homeassistant = { ...store.homeassistant, entities: [] }
    store.jellyfin = { ...store.jellyfin, sessions: [] }
    await flush()
    expect(container.textContent).toContain('Nenhuma luz ou interruptor')
    expect(container.textContent).toContain('Ninguém assistindo.')
  })
})

describe('host e métricas de exemplo', () => {
  it('helpers de teste são coerentes', () => {
    expect(host().disks[0].mount).toBe('/')
  })
})
