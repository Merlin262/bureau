import type { AlertEvent, DeviceSummary, Me, OkResponse, PushConfig, UnitEntry } from './types'
import { store } from './store.svelte'
import { conn } from './platform'

export function headers(json = false): Record<string, string> {
  const h: Record<string, string> = { 'X-Bureau': '1' }
  if (conn.token) h.Authorization = `Bearer ${conn.token}`
  if (json) h['Content-Type'] = 'application/json'
  return h
}

async function req<T>(method: string, url: string, body?: unknown): Promise<T> {
  let r: Response
  try {
    r = await fetch(conn.base + url, {
      method,
      headers: headers(body !== undefined),
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new Error('Servidor inacessível')
  }
  if (r.status === 401 && !url.endsWith('/login') && !url.endsWith('/token')) {
    store.authed = false
    throw new Error('Sessão expirada')
  }
  if (r.status === 429) throw new Error('Muitas tentativas. Aguarde um minuto.')
  const data = await r.json().catch(() => null)
  if (!r.ok && !(data && 'ok' in data)) throw new Error(`Falha HTTP ${r.status}`)
  return data as T
}

const enc = encodeURIComponent

export const api = {
  me: () => req<Me>('GET', '/api/auth/me'),
  login: (password: string) => req<OkResponse>('POST', '/api/auth/login', { password }),
  logout: () => req<OkResponse>('POST', '/api/auth/logout'),
  token: (password: string, name: string) =>
    req<{ token?: string; id?: string; ok?: boolean; message?: string }>('POST', '/api/auth/token', { password, name }),
  tokens: () => req<DeviceSummary[]>('GET', '/api/auth/tokens'),
  revoke: (id: string) => req<OkResponse>('DELETE', `/api/auth/tokens/${enc(id)}`),

  availableUnits: () => req<UnitEntry[]>('GET', '/api/services/available'),
  watch: (units: string[]) => req<OkResponse>('PUT', '/api/services/watch', { units }),
  service: (unit: string, action: string) => req<OkResponse>('POST', `/api/services/${enc(unit)}/${action}`),
  serviceLogs: (unit: string) => req<{ text: string }>('GET', `/api/services/${enc(unit)}/logs`),

  container: (id: string, action: string) => req<OkResponse>('POST', `/api/containers/${enc(id)}/${action}`),
  containerLogs: (id: string) => req<{ text: string }>('GET', `/api/containers/${enc(id)}/logs`),

  blocking: (enabled: boolean, timer?: number) => req<OkResponse>('POST', '/api/pihole/blocking', { enabled, timer }),
  wol: (mac: string) => req<OkResponse>('POST', '/api/network/wol', { mac }),
  power: (action: 'reboot' | 'poweroff') => req<OkResponse>('POST', `/api/power/${action}`),

  pushConfig: () => req<PushConfig>('GET', '/api/push/config'),
  pushDevices: () => req<DeviceSummary[]>('GET', '/api/push/devices'),
  pushRegister: (body: { kind: 'fcm' | 'webpush'; name: string; token?: string; endpoint?: string; p256dh?: string; auth?: string }) =>
    req<OkResponse>('POST', '/api/push/register', body),
  pushRemove: (id: string) => req<OkResponse>('DELETE', `/api/push/${enc(id)}`),
  pushTest: () => req<OkResponse>('POST', '/api/push/test'),
}

export type { AlertEvent }
