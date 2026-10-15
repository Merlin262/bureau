import { beforeEach, describe, expect, it, vi } from 'vitest'
import { http } from '../test/http'
import { flush } from '../test/util'

/**
 * Caminhos do app Android/iOS: os plugins do Capacitor são simulados e os módulos recarregados
 * para cada cenário (isNative/platform são lidos na importação).
 */
type Listener = (...a: never[]) => unknown

/** Os mocks leem o cenário atual em tempo de chamada (o registro de mocks sobrevive ao resetModules). */
const h = vi.hoisted(() => ({ platform: 'android' as string, m: null as unknown as ReturnType<typeof fresh> }))

function fresh() {
  return {
    prefs: new Map<string, string>(),
    appListeners: {} as Record<string, Listener>,
    pushListeners: {} as Record<string, Listener>,
    haptics: vi.fn((..._a: unknown[]) => {}),
    statusStyle: vi.fn((..._a: unknown[]) => {}),
    statusBg: vi.fn((..._a: unknown[]) => {}),
    minimize: vi.fn(),
    perm: { receive: 'granted' as string },
    requestPerm: vi.fn(async () => ({ receive: 'granted' })),
    createChannel: vi.fn((..._a: unknown[]) => {}),
    register: vi.fn(),
    removed: vi.fn(),
    bioAvailable: vi.fn(async (..._a: unknown[]) => ({ isAvailable: true })),
    verify: vi.fn(async (..._a: unknown[]) => {}),
  }
}

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => h.platform !== 'web', getPlatform: () => h.platform },
}))
vi.mock('@capacitor/preferences', () => ({
  Preferences: {
    get: async ({ key }: { key: string }) => ({ value: h.m.prefs.get(key) ?? null }),
    set: async ({ key, value }: { key: string; value: string }) => void h.m.prefs.set(key, value),
    remove: async ({ key }: { key: string }) => void h.m.prefs.delete(key),
  },
}))
vi.mock('@capacitor/haptics', () => ({ Haptics: { impact: (...a: unknown[]) => h.m.haptics(...a) }, ImpactStyle: { Light: 'LIGHT' } }))
vi.mock('@capacitor/status-bar', () => ({
  StatusBar: { setStyle: (...a: unknown[]) => h.m.statusStyle(...a), setBackgroundColor: (...a: unknown[]) => h.m.statusBg(...a) },
  Style: { Dark: 'DARK' },
}))
vi.mock('@capacitor/app', () => ({
  App: { addListener: (ev: string, cb: Listener) => void (h.m.appListeners[ev] = cb), minimizeApp: () => h.m.minimize() },
}))
vi.mock('@capacitor/push-notifications', () => ({
  PushNotifications: {
    checkPermissions: async () => h.m.perm,
    requestPermissions: () => h.m.requestPerm(),
    createChannel: (...a: unknown[]) => h.m.createChannel(...a),
    register: () => h.m.register(),
    addListener: (ev: string, cb: Listener) => {
      h.m.pushListeners[ev] = cb
      return Promise.resolve({ remove: h.m.removed })
    },
  },
}))
vi.mock('@capgo/capacitor-native-biometric', () => ({
  NativeBiometric: { isAvailable: (...a: unknown[]) => h.m.bioAvailable(...a), verifyIdentity: (...a: unknown[]) => h.m.verify(...a) },
}))

async function load(platform: 'android' | 'ios' | 'web', fcm = true) {
  vi.resetModules()
  vi.stubEnv('VITE_FCM', fcm ? '1' : '')
  h.platform = platform
  const m = (h.m = fresh())
  const [platformMod, push, bio, native, store, router] = await Promise.all([
    import('./platform'), import('./push'), import('./biometric'), import('./native'), import('./store.svelte'), import('./router.svelte'),
  ])
  return { m, platform: platformMod, push, bio, native, store: store.store, router }
}

beforeEach(() => {
  vi.unstubAllEnvs()
})

describe('platform no app', () => {
  it('guarda servidor e token nas preferências', async () => {
    const { m, platform } = await load('android')
    m.prefs.set('bureau.server', 'http://srv:8420')
    m.prefs.set('bureau.token', 'tok')
    await platform.loadConnection()
    expect(platform.conn).toEqual({ base: 'http://srv:8420', token: 'tok' })
    await platform.saveConnection('https://b.example', 't2')
    expect(m.prefs.get('bureau.server')).toBe('https://b.example')
    await platform.clearToken()
    expect(m.prefs.has('bureau.token')).toBe(false)
    expect(platform.deviceName()).toBe('Android')
    await platform.tap()
    expect(m.haptics).toHaveBeenCalled()

    m.prefs.clear()
    await platform.loadConnection()
    expect(platform.conn).toEqual({ base: '', token: '' })
  })

  it('iPhone', async () => {
    const { platform } = await load('ios')
    expect(platform.deviceName()).toBe('iPhone')
  })

  it('vibração indisponível não quebra', async () => {
    const { m, platform } = await load('android')
    m.haptics.mockRejectedValueOnce(new Error('sem motor'))
    await platform.tap()
  })
})

describe('integração nativa', () => {
  it('barra de status, botão voltar e toque em notificação', async () => {
    const { m, native, router, push } = await load('android')
    localStorage.setItem('bureau.pushId', 'id1')
    http.on('POST /api/push/register', { ok: true, message: 'id2' })
    await native.initNative()
    expect(document.documentElement.classList.contains('native')).toBe(true)
    expect(m.statusBg).toHaveBeenCalledWith({ color: '#0b0b0c' })
    expect(m.register).toHaveBeenCalled() // renova o token se já estava ativo

    m.pushListeners.registration({ value: 'novo-token' } as never)
    await flush()
    expect(http.sent('POST /api/push/register')[0].body).toMatchObject({ kind: 'fcm', token: 'novo-token' })
    expect(push.pushRegisteredId()).toBe('id2')

    m.pushListeners.pushNotificationActionPerformed({ notification: { data: { view: 'sistema' } } } as never)
    expect(router.router.current).toBe('sistema')
    m.pushListeners.pushNotificationActionPerformed({ notification: { data: {} } } as never)

    // Voltar: fecha modal → volta ao painel → minimiza.
    const dlg = document.createElement('div')
    dlg.setAttribute('role', 'dialog')
    document.body.append(dlg)
    const esc = vi.fn()
    window.addEventListener('keydown', esc)
    m.appListeners.backButton()
    expect(esc).toHaveBeenCalled()
    dlg.remove()
    m.appListeners.backButton()
    expect(router.router.current).toBe('painel')
    m.appListeners.backButton()
    expect(m.minimize).toHaveBeenCalled()
  })

  it('iOS sem barra de status configurável e no navegador não faz nada', async () => {
    const ios = await load('ios')
    ios.m.statusStyle.mockRejectedValueOnce(new Error('x'))
    await ios.native.initNative()
    expect(ios.m.statusBg).not.toHaveBeenCalled()

    const web = await load('web')
    await web.native.initNative()
    expect(web.m.statusStyle).not.toHaveBeenCalled()
  })
})

describe('push nativo', () => {
  it('Android: canal, registro e inscrição no servidor', async () => {
    const { m, push } = await load('android')
    expect(push.pushAvailable).toBe(true)
    http.on('POST /api/push/register', { ok: true, message: 'dev1' })
    const p = push.enablePush()
    await flush()
    expect(m.createChannel).toHaveBeenCalledWith(expect.objectContaining({ id: 'alerts' }))
    m.pushListeners.registration({ value: 'fcm-tok' } as never)
    expect(await p).toEqual({ ok: true, message: 'Notificações ativadas neste aparelho' })
    expect(push.pushRegisteredId()).toBe('dev1')
    await flush()
    expect(m.removed).toHaveBeenCalled()

    http.on('DELETE /api/push/dev1', { ok: true })
    expect((await push.disablePush()).ok).toBe(true)
    expect(push.pushRegisteredId()).toBeNull()
    expect(http.sent('DELETE /api/push/dev1')).toHaveLength(1)
  })

  it('permissão negada, erro de registro, falha no servidor e tempo esgotado', async () => {
    const { m, push } = await load('ios')
    m.perm = { receive: 'prompt' }
    m.requestPerm.mockResolvedValueOnce({ receive: 'denied' })
    expect((await push.enablePush()).message).toBe('Permissão de notificação negada')
    m.perm = { receive: 'granted' }

    let p = push.enablePush()
    await flush()
    expect(m.createChannel).not.toHaveBeenCalled() // canal é coisa do Android
    m.pushListeners.registrationError({ error: 'sem APNs' } as never)
    expect((await p).message).toBe('Falha no registro: sem APNs')

    http.on('POST /api/push/register', new Error('offline'))
    p = push.enablePush()
    await flush()
    m.pushListeners.registration({ value: 'x' } as never)
    expect((await p).ok).toBe(false)

    vi.useFakeTimers()
    p = push.enablePush()
    await vi.advanceTimersByTimeAsync(20_001)
    expect((await p).message).toBe('Tempo esgotado ao registrar no APNs')
  })

  it('Android sem Firebase no build: indisponível', async () => {
    const { push } = await load('android', false)
    expect(push.pushAvailable).toBe(false)
    expect((await push.enablePush()).message).toBe('Este aparelho não suporta notificações push')
    await push.initNativePush()
  })
})

describe('web push', () => {
  it('assina com a chave VAPID e registra; desativar cancela a assinatura', async () => {
    const unsubscribe = vi.fn()
    const subscribe = vi.fn(async () => ({ toJSON: () => ({ endpoint: 'https://push/x', keys: { p256dh: 'P', auth: 'A' } }) }))
    const reg = { pushManager: { subscribe, getSubscription: async () => ({ unsubscribe }) } }
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: true })
    Object.defineProperty(navigator, 'serviceWorker', { configurable: true, value: { ready: Promise.resolve(reg), getRegistration: async () => reg } })
    vi.stubGlobal('PushManager', class {})
    vi.stubGlobal('Notification', { requestPermission: vi.fn(async () => 'granted') })
    try {
      const { push } = await load('web')
      expect(push.webPushAvailable).toBe(true)
      http.on('GET /api/push/config', { webPushKey: 'AQID_-8', fcm: false, ntfy: false })
      http.on('POST /api/push/register', { ok: true, message: 'w1' })
      expect((await push.enablePush()).ok).toBe(true)
      expect(Array.from((subscribe.mock.calls[0] as unknown as [{ applicationServerKey: Uint8Array }])[0].applicationServerKey)).toEqual([1, 2, 3, 255, 239])
      expect(http.sent('POST /api/push/register')[0].body).toMatchObject({ kind: 'webpush', endpoint: 'https://push/x', p256dh: 'P', auth: 'A' })

      http.on('DELETE /api/push/w1', new Error('offline')) // falha ao avisar o servidor não impede
      await push.disablePush()
      expect(unsubscribe).toHaveBeenCalled()

      ;(Notification.requestPermission as ReturnType<typeof vi.fn>).mockResolvedValueOnce('denied')
      expect((await push.enablePush()).message).toBe('Permissão de notificação negada')
    } finally {
      Object.defineProperty(window, 'isSecureContext', { configurable: true, value: false })
      delete (navigator as unknown as Record<string, unknown>).serviceWorker
    }
  })
})

describe('biometria', () => {
  it('só no app e só liga depois de confirmar', async () => {
    const web = await load('web')
    expect(web.bio.bioEnabled()).toBe(false)
    expect(await web.bio.bioAvailable()).toBe(false)
    await web.bio.initBioLock()

    const { m, bio, store } = await load('android')
    expect(await bio.bioAvailable()).toBe(true)
    m.bioAvailable.mockRejectedValueOnce(new Error('sem sensor'))
    expect(await bio.bioAvailable()).toBe(false)

    m.verify.mockRejectedValueOnce(new Error('cancelado'))
    expect(await bio.setBio(true)).toEqual({ ok: false, message: 'Biometria não confirmada' })
    expect(bio.bioEnabled()).toBe(false)
    expect((await bio.setBio(true)).ok).toBe(true)
    expect(bio.bioEnabled()).toBe(true)

    store.locked = true
    await bio.unlock()
    expect(store.locked).toBe(false)
    expect((await bio.setBio(false)).message).toBe('Biometria desativada')
  })

  it('trava ao abrir e ao voltar depois de 1 min, sem abrir a biometria sozinha', async () => {
    const { m, bio, store } = await load('ios')
    localStorage.setItem('bureau.bio', '1')
    await bio.initBioLock()
    expect(store.locked).toBe(true)
    expect(m.verify).not.toHaveBeenCalled() // só abre com o toque em "Desbloquear"
    await bio.unlock()
    expect(store.locked).toBe(false)

    const now = vi.spyOn(Date, 'now')
    now.mockReturnValue(1_000_000)
    m.appListeners.appStateChange({ isActive: false } as never)
    now.mockReturnValue(1_030_000)
    m.appListeners.appStateChange({ isActive: true } as never)
    expect(store.locked).toBe(false) // só 30 s
    m.appListeners.appStateChange({ isActive: false } as never)
    now.mockReturnValue(1_100_000)
    m.appListeners.appStateChange({ isActive: true } as never)
    expect(store.locked).toBe(true)
    expect(m.verify).toHaveBeenCalledTimes(1)

    m.verify.mockRejectedValueOnce(new Error('não'))
    await bio.unlock()
    expect(store.locked).toBe(true)
    expect(store.lockError).toBe('não')
  })

  it('a tela de biometria do Android (pausa/retoma o app) não cria loop', async () => {
    const { m, bio, store } = await load('android')
    localStorage.setItem('bureau.bio', '1')
    await bio.initBioLock()
    expect(store.locked).toBe(true)

    let release!: () => void
    m.verify.mockImplementationOnce(() => new Promise<void>((r) => (release = r)))
    const now = vi.spyOn(Date, 'now')
    now.mockReturnValue(1_000_000)
    const first = bio.unlock()

    // Durante a verificação o Android pausa e retoma o app (inclusive depois de muito tempo).
    m.appListeners.appStateChange({ isActive: false } as never)
    now.mockReturnValue(1_300_000)
    m.appListeners.appStateChange({ isActive: true } as never)
    const second = bio.unlock() // segundo toque enquanto já verifica: não abre outra
    release()
    await Promise.all([first, second])
    await flush()
    expect(m.verify).toHaveBeenCalledTimes(1)
    expect(store.locked).toBe(false)

    // O "retomar" que chega depois da verificação não trava de novo.
    m.appListeners.appStateChange({ isActive: true } as never)
    expect(store.locked).toBe(false)
  })

  it('cancelar não mostra erro; entrar com a senha desliga a biometria', async () => {
    const { m, bio, store } = await load('android')
    localStorage.setItem('bureau.bio', '1')
    await bio.initBioLock()
    m.verify.mockRejectedValueOnce(new Error('User cancelled'))
    await bio.unlock()
    expect(store.locked).toBe(true)
    expect(store.lockError).toBe('')

    store.authed = true
    bio.unlockWithPassword()
    expect(store.locked).toBe(false)
    expect(store.authed).toBe(false)
    expect(bio.bioEnabled()).toBe(false)
  })

  it('biometria removida do aparelho: não tranca', async () => {
    const { m, bio, store } = await load('android')
    localStorage.setItem('bureau.bio', '1')
    m.bioAvailable.mockResolvedValueOnce({ isAvailable: false })
    await bio.initBioLock()
    expect(store.locked).toBe(false)
    expect(m.verify).not.toHaveBeenCalled()
  })
})
