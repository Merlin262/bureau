import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/svelte'
import Network from './Network.svelte'
import { store } from '../lib/store.svelte'
import { http } from '../test/http'
import { device, flush, pihole } from '../test/util'
import type { MonitorState } from '../lib/types'

const btn = (name: string | RegExp) => screen.getByRole('button', { name })
const mon = (over: Partial<MonitorState>): MonitorState => ({
  id: 'x', name: 'Site', target: 'https://site', up: true, latencyMs: 120, uptime24h: 100, since: 0, recent: [100, -1, 900], builtin: false, ...over,
})

async function hold(el: HTMLElement) {
  await fireEvent.pointerDown(el)
  await new Promise((r) => setTimeout(r, 1300))
}

describe('Rede · monitores', () => {
  it('estados e edição', async () => {
    http.on('GET /api/monitors', [{ id: 'a', name: 'Site', target: 'https://site' }])
    http.on('PUT /api/monitors', { ok: true, message: 'Monitores salvos' })
    store.monitors = [
      mon({ id: 'internet', name: 'Internet', builtin: true }),
      mon({ id: 'a', up: false, error: undefined, since: Date.now() / 1000 - 120, latencyMs: undefined, uptime24h: 97.5 }),
    ]
    const { container } = render(Network)
    expect(container.textContent).toContain('Conexão de casa com a internet')
    expect(container.textContent).toContain('Sem resposta · há 2m')
    expect(container.textContent).toContain('97.50%')
    expect(container.textContent).toContain('100%')

    await fireEvent.click(btn('Editar'))
    await flush()
    const dlg = screen.getByRole('dialog')
    await fireEvent.click(within(dlg).getByText('+ Adicionar'))
    const targets = within(dlg).getAllByPlaceholderText('https://bureau.merlin.api.br')
    await fireEvent.input(targets[1], { target: { value: '192.168.100.1:80' } })
    await fireEvent.click(within(dlg).getAllByLabelText('Remover')[0])
    await fireEvent.click(within(dlg).getByText('+ Adicionar')) // vazio: ignorado ao salvar
    await fireEvent.click(within(dlg).getByText('Salvar'))
    await flush()
    expect(http.sent('PUT /api/monitors')[0].body).toEqual([{ id: '', name: '', target: '192.168.100.1:80' }])
    expect(screen.queryByRole('dialog')).toBeNull()

    http.on('GET /api/monitors', new Error('offline'))
    http.on('PUT /api/monitors', { ok: false, message: 'Alvo inválido' })
    await fireEvent.click(btn('Editar'))
    await flush()
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Salvar'))
    await flush()
    expect(store.toasts.at(-1)).toMatchObject({ kind: 'err', text: 'Alvo inválido' })
    await fireEvent.keyDown(window, { key: 'Escape' })
  })

  it('iniciando', () => {
    const { container } = render(Network)
    expect(container.textContent).toContain('Iniciando verificações…')
    expect(container.textContent).toContain('Varrendo a rede…')
  })
})

describe('Rede · aparelhos', () => {
  it('lista, filtro, apelidos e etiquetas', async () => {
    http.on('PUT /api/network/alias', { ok: true })
    store.network = [
      device({ name: undefined, vendor: undefined, randomMac: true, mac: '02:00:00:00:00:01', ip: '192.168.100.150', firstSeen: Date.now() / 1000 - 60, dnsQueries: 0 }),
      device({ alias: 'PC do quarto', name: 'merlinpc' }),
      device({ name: undefined, vendor: undefined, mac: 'aa:00:00:00:00:02', ip: '192.168.100.151', online: false }),
    ]
    const { container } = render(Network)
    expect(container.textContent).toContain('2 ONLINE / 3 CONHECIDOS')
    expect(container.textContent).toContain('Celular / tablet?')
    expect(container.textContent).toContain('NOVO')
    expect(container.textContent).toContain('ALEATÓRIO')
    expect(container.textContent).toContain('merlinpc · Realtek')
    expect(container.textContent).toContain('100 consultas/24h')
    expect(container.textContent).toContain('Desconhecido')

    await fireEvent.input(screen.getByPlaceholderText(/Filtrar por nome/), { target: { value: 'QUARTO' } })
    expect(container.querySelectorAll('.dev')).toHaveLength(1)
    await fireEvent.input(screen.getByPlaceholderText(/Filtrar por nome/), { target: { value: 'zzz' } })
    expect(container.textContent).toContain('Nenhum dispositivo.')
    await fireEvent.input(screen.getByPlaceholderText(/Filtrar por nome/), { target: { value: '' } })

    // Renomear: Enter salva, Escape cancela, mesmo nome não chama a API, sair do campo salva.
    await fireEvent.click(screen.getByLabelText('PC do quarto — renomear'))
    let input = screen.getByPlaceholderText('Apelido (vazio remove)')
    await fireEvent.input(input, { target: { value: 'Escritório' } })
    await fireEvent.keyDown(input, { key: 'Enter' })
    await flush()
    expect(http.sent('PUT /api/network/alias')[0].body).toEqual({ mac: '30:56:0f:86:a7:dd', name: 'Escritório' })

    await fireEvent.click(screen.getByLabelText('Desconhecido — renomear'))
    input = screen.getByPlaceholderText('Apelido (vazio remove)')
    await fireEvent.keyDown(input, { key: 'x' })
    await fireEvent.keyDown(input, { key: 'Escape' })
    expect(screen.queryByPlaceholderText('Apelido (vazio remove)')).toBeNull()

    await fireEvent.click(screen.getByLabelText('PC do quarto — renomear'))
    await fireEvent.blur(screen.getByPlaceholderText('Apelido (vazio remove)'))
    await flush()
    expect(http.sent('PUT /api/network/alias')).toHaveLength(1)

    await fireEvent.click(screen.getByLabelText('PC do quarto — renomear'))
    input = screen.getByPlaceholderText('Apelido (vazio remove)')
    await fireEvent.input(input, { target: { value: '  ' } })
    await fireEvent.blur(input)
    await flush()
    expect(http.sent('PUT /api/network/alias')[1].body).toEqual({ mac: '30:56:0f:86:a7:dd', name: '' })
    expect(store.toasts.at(-1)!.text).toBe('Apelido removido')
  })

  it('bloqueio por aparelho e horário', async () => {
    http.on('PUT /api/network/block', { ok: true })
    http.on('DELETE /api/network/block/30%3A56%3A0f%3A86%3Aa7%3Add', { ok: true })
    store.pihole = pihole()
    store.network = [device()]
    const { container } = render(Network)

    await fireEvent.click(btn('Bloquear'))
    let dlg = screen.getByRole('dialog')
    await fireEvent.click(within(dlg).getByText('Bloquear por horário'))
    await fireEvent.click(within(dlg).getByText('Bloquear agora'))
    expect(within(dlg).getByText('TODOS OS DIAS')).toBeTruthy()
    const days = within(dlg).getAllByText(/^[DSTQ]$/)
    await fireEvent.click(days[1])
    await fireEvent.click(days[5])
    await fireEvent.click(days[1])
    await fireEvent.input(within(dlg).getByLabelText('DAS'), { target: { value: '22:00' } })
    await fireEvent.click(within(dlg).getByText('Salvar'))
    await flush()
    expect(http.sent('PUT /api/network/block')[0].body).toEqual({
      mac: '30:56:0f:86:a7:dd', label: 'merlinpc', manual: false, days: [5], start: '22:00', end: '07:00',
    })

    // Regra ativa: "Liberar" rápido e modal "Regras".
    store.blocks = [{ mac: '30:56:0f:86:a7:dd', manual: true, days: [], start: '22:00', end: '07:00', active: true }]
    await flush()
    expect(container.textContent).toContain('BLOQUEADO')
    await fireEvent.click(btn('Liberar'))
    await flush()
    expect(http.sent('PUT /api/network/block')[1].body).toMatchObject({ manual: false, start: '22:00' })

    store.blocks = [{ mac: '30:56:0f:86:a7:dd', manual: true, days: [], active: true }]
    await flush()
    await fireEvent.click(btn('Liberar'))
    await flush()
    expect(http.sent('DELETE /api/network/block/30%3A56%3A0f%3A86%3Aa7%3Add')).toHaveLength(1)

    store.blocks = [{ mac: '30:56:0f:86:a7:dd', manual: false, days: [1], start: '23:00', end: '06:00', active: false }]
    await flush()
    expect(container.textContent).toContain('AGENDADO 23:00–06:00')
    await fireEvent.click(btn('Regras'))
    dlg = screen.getByRole('dialog')
    await fireEvent.click(within(dlg).getByText('Remover regras'))
    await flush()
    expect(http.sent('DELETE /api/network/block/30%3A56%3A0f%3A86%3Aa7%3Add')).toHaveLength(2)

    await fireEvent.click(btn('Regras'))
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Bloquear por horário')) // desliga o horário
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Salvar'))
    await flush()
    expect(store.toasts.at(-1)!.text).toBe('Bloqueio removido')
    await fireEvent.click(btn('Regras'))
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Bloquear agora'))
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Salvar'))
    await flush()
    expect(store.toasts.at(-1)!.text).toBe('merlinpc: internet bloqueada')
  })

  it('o que o aparelho está acessando', async () => {
    vi.useFakeTimers()
    http.on('GET /api/pihole/queries', [
      { ts: 1700000000, domain: 'i.ytimg.com', type: 'A', client: '192.168.100.172', status: 'FORWARDED', blocked: false },
      { ts: 1700000009, domain: 'www.youtube.com', type: 'A', client: '192.168.100.172', status: 'FORWARDED', blocked: false },
      { ts: 1700000005, domain: 'ads.mercadolivre.com.br', type: 'A', client: '192.168.100.172', status: 'GRAVITY', blocked: true },
      { ts: 1700000001, domain: 'localhost', type: 'A', client: '192.168.100.172', status: 'FORWARDED', blocked: false },
    ])
    http.on('POST /api/pihole/domain', { ok: true })
    store.pihole = pihole()
    store.network = [device()]
    render(Network)
    await fireEvent.click(btn('Acessos'))
    await vi.advanceTimersByTimeAsync(10)
    const dlg = screen.getByRole('dialog')
    expect(http.sent('GET /api/pihole/queries')[0].query).toBe('?client=192.168.100.172')
    expect(within(dlg).getByText('MAIS ACESSADOS (ÚLTIMAS 4 CONSULTAS)')).toBeTruthy()
    expect(dlg.textContent).toContain('youtube.com 1')
    expect(dlg.textContent).toContain('mercadolivre.com.br 1') // domínio .com.br agrupado com 3 partes
    expect(dlg.querySelectorAll('.q')[0].textContent).toContain('www.youtube.com') // mais recente primeiro

    await fireEvent.click(within(dlg).getByText('Só bloqueadas'))
    expect(dlg.querySelectorAll('.q')).toHaveLength(1)
    await fireEvent.click(within(dlg.querySelector('.q') as HTMLElement).getByText('Liberar'))
    await vi.advanceTimersByTimeAsync(10)
    expect(http.sent('POST /api/pihole/domain')[0].body).toEqual({ domain: 'ads.mercadolivre.com.br', list: 'allow' })

    await vi.advanceTimersByTimeAsync(3100)
    expect(http.sent('GET /api/pihole/queries').length).toBeGreaterThanOrEqual(2)
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true })
    const n = http.sent('GET /api/pihole/queries').length
    await vi.advanceTimersByTimeAsync(3100)
    expect(http.sent('GET /api/pihole/queries').length).toBe(n) // aba escondida: não consulta
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false })

    await fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('sem consultas do aparelho', async () => {
    http.on('GET /api/pihole/queries', new Error('offline'))
    store.pihole = pihole()
    store.network = [device()]
    render(Network)
    await fireEvent.click(btn('Acessos'))
    await flush()
    expect(screen.getByText(/Nenhuma consulta recente/)).toBeTruthy()
  })
})

describe('Rede · Wake-on-LAN', () => {
  it('liga, salva endereços, configura e desliga só os da lista', async () => {
    http.on('POST /api/network/wol', { ok: true })
    http.on('GET /api/network/wol', { targets: [], publicKey: 'ssh-ed25519 AAAA bureau@srv' })
    http.on('PUT /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add', { ok: true })
    http.on('DELETE /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add', { ok: true })
    http.on('POST /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add/shutdown', { ok: true, message: 'Desligando…' })
    http.on('POST /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add/restart', { ok: true })
    const writeText = vi.fn(async () => {})
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })

    store.network = [device({ online: false }), device({ mac: '02:aa:00:00:00:01', randomMac: true, online: false, name: 'celular' })]
    const { container } = render(Network)

    // Offline e fora da lista: "Ligar (WoL)" (MAC aleatório não ganha o botão).
    expect(screen.getAllByText('Ligar (WoL)')).toHaveLength(1)
    await fireEvent.click(btn('Ligar (WoL)'))
    await flush()
    expect(http.sent('POST /api/network/wol')[0].body).toEqual({ mac: '30:56:0f:86:a7:dd' })

    // Formulário com nome.
    await fireEvent.input(screen.getByPlaceholderText('AA:BB:CC:DD:EE:FF'), { target: { value: ' AA-BB-CC-DD-EE-FF ' } })
    await fireEvent.input(screen.getByPlaceholderText('Nome (opcional)'), { target: { value: 'Notebook' } })
    await fireEvent.click(btn('Enviar pacote'))
    await flush()
    expect(http.sent('POST /api/network/wol')[1].body).toEqual({ mac: 'AA-BB-CC-DD-EE-FF', name: 'Notebook' })
    expect((screen.getByPlaceholderText('AA:BB:CC:DD:EE:FF') as HTMLInputElement).value).toBe('')
    await fireEvent.submit(screen.getByPlaceholderText('AA:BB:CC:DD:EE:FF').closest('form')!)
    expect(http.sent('POST /api/network/wol')).toHaveLength(2) // vazio não envia

    // Na lista e ligado, sem usuário: "Ativar desligar" abre a configuração.
    store.wol = [{ mac: '30:56:0f:86:a7:dd', name: 'MerlinPC', lastWake: Date.now() / 1000 - 300 }]
    store.network = [device()]
    await flush()
    expect(container.querySelector('.wt')!.textContent).toContain('PACOTE HÁ 5M')
    await fireEvent.click(btn('Ativar desligar'))
    await flush()
    let dlg = screen.getByRole('dialog')
    expect(dlg.textContent).toContain("Add-Content -Path $f -Value 'ssh-ed25519 AAAA bureau@srv'")
    expect(dlg.textContent).toContain('$f = "$env:ProgramData\\ssh\\administrators_authorized_keys"')
    expect(dlg.textContent).toContain('Start-Process powershell -Verb RunAs') // abre sozinho como Administrador
    await fireEvent.click(within(dlg).getByText('Copiar comandos'))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('Add-WindowsCapability'))
    await fireEvent.input(within(dlg).getByPlaceholderText('Usuário do Windows (ex.: joao)'), { target: { value: ' joao ' } })
    await fireEvent.click(within(dlg).getByText('Salvar'))
    await flush()
    expect(http.sent('PUT /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add')[0].body).toEqual({ name: 'MerlinPC', user: 'joao' })
    expect(screen.queryByRole('dialog')).toBeNull()

    // Sem usuário, segurar "Desligar" na linha do aparelho também leva à configuração.
    await hold(within(container.querySelector('.dev') as HTMLElement).getByRole('button', { name: 'Desligar' }))
    expect(screen.getByRole('dialog')).toBeTruthy()
    await fireEvent.keyDown(window, { key: 'Escape' })

    // Com usuário: reiniciar e desligar (segurando).
    store.wol = [{ mac: '30:56:0f:86:a7:dd', name: 'MerlinPC', user: 'joao', lastWake: 0 }]
    await flush()
    // Mesmo parecendo ligado (cache ARP), "Ligar" continua disponível.
    expect(within(container.querySelector('.wt') as HTMLElement).getByText('Ligar')).toBeTruthy()
    await fireEvent.click(within(container.querySelector('.wt') as HTMLElement).getByText('Reiniciar'))
    await hold(within(container.querySelector('.wt') as HTMLElement).getByRole('button', { name: 'Desligar' }))
    await flush()
    expect(http.sent('POST /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add/restart')).toHaveLength(1)
    expect(http.sent('POST /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add/shutdown')).toHaveLength(1)
    expect(store.toasts.some((t) => t.text === 'Desligando…')).toBe(true)

    // Desligado: botão "Ligar" do painel; engrenagem → remover da lista; falha ao salvar.
    store.network = [device({ online: false })]
    await flush()
    await fireEvent.click(within(container.querySelector('.wt') as HTMLElement).getByText('Ligar'))
    await flush()
    expect(http.sent('POST /api/network/wol')).toHaveLength(3)

    http.on('GET /api/network/wol', new Error('offline'))
    await fireEvent.click(screen.getByLabelText('Configurar'))
    await flush()
    dlg = screen.getByRole('dialog')
    expect(dlg.textContent).toContain('Gerando a chave do BUREAU…')
    http.on('PUT /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add', { ok: false, message: 'Usuário inválido' })
    await fireEvent.click(within(dlg).getByText('Salvar'))
    await flush()
    expect(store.toasts.at(-1)).toMatchObject({ kind: 'err', text: 'Usuário inválido' })
    await fireEvent.click(within(dlg).getByText('Remover da lista'))
    await flush()
    expect(http.sent('DELETE /api/network/wol/30%3A56%3A0f%3A86%3Aa7%3Add')).toHaveLength(1)

    writeText.mockRejectedValueOnce(new Error('negado'))
    http.on('GET /api/network/wol', { targets: [], publicKey: 'k' })
    store.wol = [{ mac: '30:56:0f:86:a7:dd', lastWake: 0 }]
    await flush()
    await fireEvent.click(screen.getByLabelText('Configurar'))
    await flush()
    await fireEvent.click(within(screen.getByRole('dialog')).getByText('Copiar comandos'))
    await flush()
    expect(store.toasts.at(-1)!.text).toContain('Não foi possível copiar')
  })
})
