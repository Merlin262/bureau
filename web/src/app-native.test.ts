import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/svelte'
import App from './App.svelte'
import Login from './views/Login.svelte'
import System from './views/System.svelte'
import { store } from './lib/store.svelte'
import { conn } from './lib/platform'
import { http } from './test/http'
import { flush } from './test/util'

// Este arquivo inteiro roda "dentro do app Android" (Capacitor simulado).
const prefs = vi.hoisted(() => new Map<string, string>())
const bio = vi.hoisted(() => ({ available: true }))
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true, getPlatform: () => 'android' } }))
vi.mock('@capacitor/preferences', () => ({
  Preferences: {
    get: async ({ key }: { key: string }) => ({ value: prefs.get(key) ?? null }),
    set: async ({ key, value }: { key: string; value: string }) => void prefs.set(key, value),
    remove: async ({ key }: { key: string }) => void prefs.delete(key),
  },
}))
vi.mock('@capacitor/haptics', () => ({ Haptics: { impact: async () => {} }, ImpactStyle: { Light: 'LIGHT' } }))
vi.mock('@capacitor/status-bar', () => ({ StatusBar: { setStyle: async () => {}, setBackgroundColor: async () => {} }, Style: { Dark: 'DARK' } }))
vi.mock('@capacitor/app', () => ({ App: { addListener: () => {}, minimizeApp: () => {} } }))
vi.mock('@capacitor/push-notifications', () => ({
  PushNotifications: { checkPermissions: async () => ({ receive: 'denied' }), addListener: async () => ({ remove() {} }), register() {} },
}))
vi.mock('@capgo/capacitor-native-biometric', () => ({
  NativeBiometric: { isAvailable: async () => ({ isAvailable: bio.available }), verifyIdentity: async () => {} },
}))

describe('app nativo', () => {
  it('sem token salvo vai direto ao login, sem chamar o servidor', async () => {
    prefs.clear()
    render(App)
    await flush(5)
    expect(screen.getByLabelText('SERVIDOR')).toBeTruthy()
    expect(http.calls.filter((c) => c.path === '/api/auth/me')).toHaveLength(0)
    expect(document.documentElement.classList.contains('android')).toBe(true)
  })

  it('login troca a senha por um token do aparelho', async () => {
    const onsuccess = vi.fn()
    render(Login, { onsuccess })
    const pw = screen.getByLabelText('CHAVE DE ACESSO')
    const form = pw.closest('form')!

    await fireEvent.input(pw, { target: { value: 'senha' } })
    await fireEvent.submit(form)
    await flush()
    expect(screen.getByText('▲ Informe o endereço do servidor')).toBeTruthy()

    http.on('POST /api/auth/token', { ok: false, message: 'ACESSO NEGADO' })
    await fireEvent.input(screen.getByLabelText('SERVIDOR'), { target: { value: '192.168.100.100' } })
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'errada' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(screen.getByText('▲ ACESSO NEGADO')).toBeTruthy()

    http.on('POST /api/auth/token', { token: 'TOKEN', id: 'd1' })
    await fireEvent.input(screen.getByLabelText('CHAVE DE ACESSO'), { target: { value: 'certa' } })
    await fireEvent.submit(screen.getByLabelText('CHAVE DE ACESSO').closest('form')!)
    await flush()
    expect(onsuccess).toHaveBeenCalled()
    expect(http.sent('POST /api/auth/token').at(-1)!.body).toEqual({ password: 'certa', name: 'Android' })
    expect(prefs.get('bureau.server')).toBe('http://192.168.100.100:8420')
    expect(prefs.get('bureau.token')).toBe('TOKEN')
    expect(conn.token).toBe('TOKEN')
  })

  it('sistema: biometria, exportar pelo navegador e sair revoga o aparelho', async () => {
    http.on('GET /api/push/config', { webPushKey: 'k', fcm: false, ntfy: false })
    http.on('GET /api/auth/tokens', [])
    http.on('GET /api/push/devices', [])
    http.on('GET /api/update', new Error('offline'))
    http.on('GET /api/auth/access', [])
    http.on('DELETE /api/auth/tokens/self', { ok: true })
    store.me = { authenticated: true, version: '1', allowPower: false, terminal: false }
    store.authed = true
    prefs.set('bureau.token', 'TOKEN')
    localStorage.setItem('bureau.pushId', 'p1')
    const { container } = render(System)
    await flush()

    expect(container.textContent).toContain('não inclui o Firebase')
    await fireEvent.click(screen.getByRole('button', { name: 'Desligada' }))
    await flush()
    expect(localStorage.getItem('bureau.bio')).toBe('1')
    expect(screen.getByRole('button', { name: 'Ligada' })).toBeTruthy()

    await fireEvent.click(screen.getByRole('button', { name: 'Exportar' }))
    expect(store.toasts.at(-1)!.text).toContain('Exporte pelo navegador')

    await fireEvent.click(screen.getByRole('button', { name: 'Sair' }))
    await flush()
    expect(http.sent('DELETE /api/auth/tokens/self')).toHaveLength(1)
    expect(prefs.has('bureau.token')).toBe(false)
    expect(localStorage.getItem('bureau.pushId')).toBeNull()
    expect(store.authed).toBe(false)
  })
})
