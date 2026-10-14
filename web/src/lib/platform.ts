import { Capacitor } from '@capacitor/core'

/** true dentro do app Android/iOS (Capacitor); false no navegador/PWA. */
export const isNative = Capacitor.isNativePlatform()
export const platform = Capacitor.getPlatform() as 'web' | 'android' | 'ios'

/**
 * Conexão com o servidor. No navegador a interface é servida pelo próprio BUREAU (mesma origem, cookie).
 * No app ela vem embutida: guarda o endereço do servidor e um token de aparelho.
 */
export const conn = { base: '', token: '' }

const KEY_BASE = 'bureau.server'
const KEY_TOKEN = 'bureau.token'

async function prefs() {
  return (await import('@capacitor/preferences')).Preferences
}

export async function loadConnection() {
  if (!isNative) return
  const p = await prefs()
  conn.base = (await p.get({ key: KEY_BASE })).value ?? ''
  conn.token = (await p.get({ key: KEY_TOKEN })).value ?? ''
}

export async function saveConnection(base: string, token: string) {
  conn.base = base
  conn.token = token
  if (!isNative) return
  const p = await prefs()
  await p.set({ key: KEY_BASE, value: base })
  await p.set({ key: KEY_TOKEN, value: token })
}

export async function clearToken() {
  conn.token = ''
  if (isNative) await (await prefs()).remove({ key: KEY_TOKEN })
}

/** Aceita "192.168.0.5", "servidor:8420", "https://x.ts.net" … e devolve uma URL base limpa. */
export function normalizeServer(input: string): string {
  let s = input.trim().replace(/\/+$/, '')
  if (!s) return ''
  if (!/^https?:\/\//i.test(s)) {
    // IP, nome sem ponto ("merlin-server") ou com porta → rede local (http, porta 8420).
    // Domínio (bureau.merlin.api.br, *.ts.net) → https.
    const host = s.split('/')[0]
    const local = /^\d{1,3}(\.\d{1,3}){3}(:\d+)?$/.test(host) || !host.includes('.') || /:\d+$/.test(host) || /\.local$/i.test(host)
    s = (local ? 'http://' : 'https://') + s
    if (local && !/:\d+$/.test(host)) s += ':8420'
  }
  return s
}

export function deviceName(): string {
  if (platform === 'android') return 'Android'
  if (platform === 'ios') return 'iPhone'
  const ua = navigator.userAgent
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Navegador'
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : ''
  return os ? `${browser} · ${os}` : browser
}

/** Feedback tátil leve nas ações (só no app). */
export async function tap() {
  if (!isNative) return
  try {
    const { Haptics, ImpactStyle } = await import('@capacitor/haptics')
    await Haptics.impact({ style: ImpactStyle.Light })
  } catch { /* sem vibração */ }
}
