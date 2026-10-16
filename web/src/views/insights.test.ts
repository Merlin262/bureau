import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/svelte'
import Network from './Network.svelte'
import PiHole from './PiHole.svelte'
import { store } from '../lib/store.svelte'
import { http } from '../test/http'
import { device, flush, pihole } from '../test/util'
import type { DnsQuery } from '../lib/types'

const DOH = { available: true, enabled: false, blocked: 0, total: 3, domains: ['dns.google', 'cloudflare-dns.com', 'dns.quad9.net'] }
const q = (domain: string, agoHours: number, blocked = false): DnsQuery => ({
  ts: Math.floor(Date.now() / 1000) - agoHours * 3600, domain, type: 'A', client: '192.168.100.172', status: blocked ? 'GRAVITY' : 'FORWARDED', blocked,
})

describe('Pi-hole · DNS seguro', () => {
  it('mostra o estado e alterna o bloqueio', async () => {
    http.on('GET /api/pihole/queries', [])
    http.on('GET /api/pihole/doh', DOH)
    http.on('PUT /api/pihole/doh', { ok: true, message: 'DNS seguro bloqueado' })
    store.pihole = pihole()
    const { container } = render(PiHole)
    await flush()
    const panel = container.querySelector('.doh') as HTMLElement
    expect(panel.textContent).toContain('Permitido')
    expect(panel.textContent).toContain('Bloquear 3 provedores')

    http.on('GET /api/pihole/doh', { ...DOH, enabled: true, blocked: 3 })
    await fireEvent.click(within(panel).getByRole('button', { name: 'Bloquear' }))
    await flush()
    expect(http.sent('PUT /api/pihole/doh')[0].body).toEqual({ enabled: true })
    expect(panel.textContent).toContain('Bloqueado')
    expect(within(panel).getByRole('button', { name: 'Liberar' })).toBeTruthy()

    http.on('GET /api/pihole/doh', { ...DOH, blocked: 1 })
    await fireEvent.click(within(panel).getByRole('button', { name: 'Liberar' }))
    await flush()
    expect(http.sent('PUT /api/pihole/doh')[1].body).toEqual({ enabled: false })
    expect(panel.textContent).toContain('Parcial (1/3)')
  })

  it('sem Pi-hole ou servidor fora: não mostra o painel', async () => {
    http.on('GET /api/pihole/queries', [])
    http.on('GET /api/pihole/doh', new Error('offline'))
    store.pihole = pihole()
    const { container } = render(PiHole)
    await flush()
    expect(container.querySelector('.doh')).toBeNull()
  })
})

describe('Rede · acessos das últimas 24 h', () => {
  it('atividade por hora, categorias com filtro, mais acessados e aviso de DNS seguro', async () => {
    const day = [
      q('rr1.googlevideo.com', 1), q('rr2.googlevideo.com', 1), q('www.youtube.com', 2),
      q('teams.microsoft.com', 3), q('telemetry.microsoft.com', 3),
      q('stats.g.doubleclick.net', 5, true), q('dns.google', 6), q('algo.xyz', 30), // 30 h atrás: fora do gráfico
    ]
    http.on('GET /api/pihole/queries', (_: unknown, url: URL) => (url.searchParams.get('hours') === '24' ? day : [q('www.instagram.com', 0)]))
    http.on('GET /api/pihole/doh', DOH)
    store.pihole = pihole()
    store.network = [device()]
    render(Network)

    await fireEvent.click(screen.getByRole('button', { name: 'Acessos' }))
    await flush()
    const dlg = screen.getByRole('dialog')
    expect(dlg.textContent).not.toContain('DNS SEGURO (') // ao vivo ainda não viu DoH

    await fireEvent.click(within(dlg).getByRole('button', { name: 'Últimas 24 h' }))
    await flush()
    expect(http.sent('GET /api/pihole/queries').some((c) => c.query === '?client=192.168.100.172&hours=24')).toBe(true)
    expect(dlg.textContent).toContain('8 CONSULTAS · 13% BLOQUEADAS')
    expect(dlg.textContent).toContain('TENTOU USAR DNS SEGURO (dns.google)')
    expect(dlg.querySelectorAll('.hcol')).toHaveLength(24)
    expect(dlg.querySelector('.hcol[title*="bloqueadas"]')).toBeTruthy()

    const cats = [...dlg.querySelectorAll('.cat')].map((c) => c.textContent!.replace(/\s+/g, ' ').trim())
    expect(cats[0]).toMatch(/^Vídeo e música .*38%$/)
    expect(cats.some((c) => c.startsWith('DNS seguro'))).toBe(true)

    const rows = () => [...dlg.querySelectorAll('.drow')].map((r) => r.textContent!.replace(/\s+/g, ' ').trim())
    expect(rows()[0]).toBe('googlevideo.com Vídeo e música 2')
    expect(rows()).toContain('microsoft.com Sistema e atualizações 2') // etiqueta pelo domínio principal

    await fireEvent.click(within(dlg).getByRole('button', { name: /^Anúncios e rastreamento/ }))
    expect(rows()).toEqual(['doubleclick.net Anúncios e rastreamento 1'])
    expect(dlg.textContent).toContain('MAIS ACESSADOS · ANÚNCIOS E RASTREAMENTO')
    await fireEvent.click(within(dlg).getByRole('button', { name: /^Anúncios e rastreamento/ }))
    expect(rows().length).toBeGreaterThan(1)

    await fireEvent.click(within(dlg).getByRole('button', { name: 'Ao vivo' }))
    expect(dlg.textContent).toContain('www.instagram.com')
  })

  it('carregando, vazio e falha', async () => {
    let release!: (v: DnsQuery[]) => void
    http.on('GET /api/pihole/queries', (_: unknown, url: URL) =>
      url.searchParams.get('hours') ? new Promise<DnsQuery[]>((r) => (release = r)) : [])
    http.on('GET /api/pihole/doh', new Error('offline'))
    store.pihole = pihole()
    store.network = [device()]
    render(Network)
    await fireEvent.click(screen.getByRole('button', { name: 'Acessos' }))
    await fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Últimas 24 h' }))
    expect(screen.getByText('Carregando as últimas 24 h…')).toBeTruthy()
    release([])
    await flush()
    expect(screen.getByText('Nenhuma consulta nas últimas 24 h.')).toBeTruthy()

    http.on('GET /api/pihole/queries', new Error('offline'))
    await fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Últimas 24 h' }))
    await flush()
    expect(screen.getByText('Nenhuma consulta nas últimas 24 h.')).toBeTruthy()
  })
})
