import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/svelte'
import App from './App.svelte'
import { store } from './lib/store.svelte'
import { router } from './lib/router.svelte'
import { http, json } from './test/http'
import { flush, pihole } from './test/util'

/** Stream SSE que fica aberto sem mandar nada (a interface só precisa saber que conectou). */
const idleStream = () => new Response(new ReadableStream(), { status: 200 })

function authedRoutes() {
  http.on('GET /api/auth/me', { authenticated: true, hostname: 'merlin-server', version: '0.4.8', allowPower: false, terminal: false })
  http.on('GET /api/stream', idleStream)
  http.on('GET /api/metrics', { range: '24h', step: 240, t: [], cpu: [], mem: [], temp: [], rx: [], tx: [], battery: [] })
  http.on('GET /api/pihole/queries', [])
  for (const r of ['GET /api/auth/access', 'GET /api/auth/tokens', 'GET /api/push/devices']) http.on(r, [])
  http.on('GET /api/push/config', { webPushKey: 'k', fcm: false, ntfy: false })
  http.on('GET /api/update', { current: 'v1', status: 'atualizado', message: 'ok', checkedAt: 0, auto: true, supported: true })
}

describe('App', () => {
  it('sem sessão mostra o login e entra', async () => {
    http.on('GET /api/auth/me', { authenticated: false, version: '0.4.8', allowPower: false })
    http.on('POST /api/auth/login', { ok: true })
    render(App)
    expect(screen.getByText(/ESTABELECENDO CONTATO/)).toBeTruthy()
    await flush()
    expect(screen.getByLabelText('CHAVE DE ACESSO')).toBeTruthy()
    expect(screen.getByText(/v0.4.8/)).toBeTruthy()

    authedRoutes()
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'senha' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush(5)
    expect(screen.getByText('BUREAU')).toBeTruthy()
    expect(screen.getByText('// merlin-server')).toBeTruthy()
    expect(store.connected).toBe(true)
    expect(screen.getByText('SINAL ESTÁVEL')).toBeTruthy()
  })

  it('servidor fora do ar cai no login', async () => {
    http.on('GET /api/auth/me', new Error('offline'))
    render(App)
    await flush()
    expect(store.authed).toBe(false)
  })

  it('navegação por clique e teclado, avisos nas seções e tela de bloqueio', async () => {
    authedRoutes()
    location.hash = '#/painel'
    window.dispatchEvent(new HashChangeEvent('hashchange'))
    render(App)
    await flush(5)

    await fireEvent.click(screen.getByRole('button', { name: /Rede/ }))
    expect(router.current).toBe('rede')
    await fireEvent.click(screen.getByRole('button', { name: /Rede/ })) // mesma seção: nada muda
    await fireEvent.mouseEnter(screen.getByRole('button', { name: /Docker/ }))

    await fireEvent.keyDown(document.body, { key: '3' })
    expect(router.current).toBe('docker')
    await fireEvent.keyDown(document.body, { key: 'e' })
    expect(router.current).toBe('pihole')
    await fireEvent.keyDown(document.body, { key: 'Q' })
    expect(router.current).toBe('docker')
    await fireEvent.keyDown(document.body, { key: '1', ctrlKey: true })
    await fireEvent.keyDown(document.body, { key: '9' })
    expect(router.current).toBe('docker')
    const input = document.createElement('input')
    document.body.append(input)
    await fireEvent.keyDown(input, { key: '1' })
    expect(router.current).toBe('docker')
    input.remove()

    for (const k of ['2', '4', '5', '6', '7', '1']) {
      await fireEvent.keyDown(document.body, { key: k })
      await flush(1)
    }
    expect(router.current).toBe('painel')

    store.services = [{ unit: 'x.service', description: '', active: 'failed', sub: '', pid: 0, memory: 0, since: 0 }]
    store.pihole = pihole({ blocking: false })
    store.homeassistant = { configured: true, online: false, total: 0, unavailable: 0, domains: [], entities: [] }
    store.backups = [{ name: 'b', unit: 'b', status: 'stale', lastRun: 0, lastSuccess: 0, nextRun: 0 }]
    await flush()
    expect(document.querySelectorAll('.badge')).toHaveLength(4)

    store.locked = true
    await flush()
    expect(screen.getByText('Desbloquear ▸')).toBeTruthy()

    // Sessão expirou: volta ao login e para o stream.
    store.authed = false
    await flush()
    expect(screen.getByLabelText('CHAVE DE ACESSO')).toBeTruthy()
    expect(store.connected).toBe(false)
  })

  it('sem sinal', async () => {
    authedRoutes()
    http.on('GET /api/stream', json({}, 500))
    render(App)
    await flush(5)
    expect(screen.getByText('SEM SINAL')).toBeTruthy()
  })
})
