import type { OkResponse } from './types'
import { api } from './api'
import { deviceName, isNative, platform } from './platform'
import { go, type ViewId } from './router.svelte'

const KEY = 'bureau.pushId'

/**
 * Android: só com Firebase no build (google-services.json + VITE_FCM=1); sem ele, register() derruba o app.
 * iPhone: APNs direto (precisa da capacidade "Push Notifications" no Xcode e da chave .p8 no servidor).
 */
export const nativePushAvailable = isNative && ((platform === 'android' && import.meta.env.VITE_FCM === '1') || platform === 'ios')
const nativeKind = platform === 'ios' ? 'apns' : 'fcm'
export const webPushAvailable =
  !isNative && typeof window !== 'undefined' && window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window

export const pushAvailable = nativePushAvailable || webPushAvailable

export function pushRegisteredId(): string | null {
  try { return localStorage.getItem(KEY) } catch { return null }
}

function remember(r: OkResponse): OkResponse {
  try {
    if (r.ok && r.message) localStorage.setItem(KEY, r.message)
  } catch { /* modo privado */ }
  return r.ok ? { ok: true, message: 'Notificações ativadas neste aparelho' } : r
}

export async function enablePush(): Promise<OkResponse> {
  if (nativePushAvailable) return remember(await enableNative())
  if (webPushAvailable) return remember(await enableWeb())
  return { ok: false, message: 'Este aparelho não suporta notificações push' }
}

export async function disablePush(): Promise<OkResponse> {
  const id = pushRegisteredId()
  try { localStorage.removeItem(KEY) } catch { /* ok */ }
  if (webPushAvailable) {
    const reg = await navigator.serviceWorker.getRegistration()
    await (await reg?.pushManager.getSubscription())?.unsubscribe()
  }
  if (id) await api.pushRemove(id).catch(() => {})
  return { ok: true, message: 'Notificações desativadas neste aparelho' }
}

// ── Android (Firebase Cloud Messaging) ──────────────────
async function enableNative(): Promise<OkResponse> {
  const { PushNotifications } = await import('@capacitor/push-notifications')
  let perm = await PushNotifications.checkPermissions()
  if (perm.receive !== 'granted') perm = await PushNotifications.requestPermissions()
  if (perm.receive !== 'granted') return { ok: false, message: 'Permissão de notificação negada' }

  if (platform === 'android') await PushNotifications.createChannel({
    id: 'alerts',
    name: 'Alertas do servidor',
    description: 'Serviço caiu, queda de energia, disco cheio…',
    importance: 5,
    visibility: 1,
    vibration: true,
    lights: true,
    lightColor: '#FF2B2B',
  })

  return new Promise<OkResponse>((resolve) => {
    let done = false
    const finish = (r: OkResponse) => {
      if (done) return
      done = true
      ok.then((h) => h.remove())
      fail.then((h) => h.remove())
      resolve(r)
    }
    const ok = PushNotifications.addListener('registration', async (t) => {
      try { finish(await api.pushRegister({ kind: nativeKind, name: deviceName(), token: t.value })) }
      catch (e) { finish({ ok: false, message: String(e) }) }
    })
    const fail = PushNotifications.addListener('registrationError', (e) =>
      finish({ ok: false, message: `Falha no registro: ${e.error}` }))
    PushNotifications.register()
    setTimeout(() => finish({ ok: false, message: platform === 'ios' ? 'Tempo esgotado ao registrar no APNs' : 'Tempo esgotado ao registrar no Firebase' }), 20_000)
  })
}

/** Toque numa notificação abre a tela correspondente; com o app aberto, o aviso já chega pelo stream. */
export async function initNativePush() {
  if (!nativePushAvailable) return
  const { PushNotifications } = await import('@capacitor/push-notifications')
  PushNotifications.addListener('pushNotificationActionPerformed', (a) => {
    const view = a.notification.data?.view as ViewId | undefined
    if (view) go(view)
  })
  // Renova o token silenciosamente se as notificações já estavam ativas.
  if (pushRegisteredId() && (await PushNotifications.checkPermissions()).receive === 'granted') {
    PushNotifications.addListener('registration', (t) =>
      api.pushRegister({ kind: nativeKind, name: deviceName(), token: t.value }).then(remember).catch(() => {}))
    PushNotifications.register()
  }
}

// ── Navegador / PWA (Web Push padrão; iPhone: iOS 16.4+ com o app na tela inicial) ──
async function enableWeb(): Promise<OkResponse> {
  if ((await Notification.requestPermission()) !== 'granted') return { ok: false, message: 'Permissão de notificação negada' }
  const reg = await navigator.serviceWorker.ready
  const cfg = await api.pushConfig()
  const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64url(cfg.webPushKey) })
  const j = sub.toJSON()
  return api.pushRegister({ kind: 'webpush', name: deviceName(), endpoint: j.endpoint!, p256dh: j.keys!.p256dh, auth: j.keys!.auth })
}

function b64url(s: string): Uint8Array<ArrayBuffer> {
  const b = atob(s.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(s.length / 4) * 4, '='))
  return Uint8Array.from(b, (c) => c.charCodeAt(0))
}
