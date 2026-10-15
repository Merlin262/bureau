import { NativeBiometric } from '@capgo/capacitor-native-biometric'
import type { OkResponse } from './types'
import { isNative } from './platform'
import { store } from './store.svelte'

// Bloqueio do app por biometria (digital/rosto; o PIN do aparelho serve de alternativa).
const KEY = 'bureau.bio'
const RELOCK_MS = 60_000
let hiddenAt = 0
// No Android a verificação abre uma tela própria: o app "sai" e "volta" durante ela. Esses eventos
// não podem travar de novo (era o loop), e nunca roda mais de uma verificação ao mesmo tempo.
let verifying: Promise<boolean> | null = null

export function bioEnabled(): boolean {
  try { return isNative && localStorage.getItem(KEY) === '1' } catch { return false }
}

export async function bioAvailable(): Promise<boolean> {
  if (!isNative) return false
  try { return (await NativeBiometric.isAvailable({ useFallback: true })).isAvailable } catch { return false }
}

function verify(reason: string): Promise<boolean> {
  verifying ??= (async () => {
    try {
      await NativeBiometric.verifyIdentity({
        reason, title: 'BUREAU', subtitle: 'Confirme que é você', negativeButtonText: 'Cancelar', useFallback: true, maxAttempts: 5,
      })
      store.lockError = ''
      return true
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e)
      store.lockError = /cancel/i.test(msg) ? '' : msg
      return false
    } finally {
      verifying = null
      hiddenAt = 0
    }
  })()
  return verifying
}

export async function setBio(on: boolean): Promise<OkResponse> {
  // Liga só depois de confirmar que a biometria funciona (evita se trancar fora do app).
  if (on && !(await verify('Ativar o desbloqueio por biometria'))) return { ok: false, message: 'Biometria não confirmada' }
  try { localStorage.setItem(KEY, on ? '1' : '0') } catch { /* sem armazenamento */ }
  return { ok: true, message: on ? 'Biometria exigida ao abrir o app' : 'Biometria desativada' }
}

export async function unlock() {
  if (await verify('Desbloquear o BUREAU')) store.locked = false
}

/** Saída de emergência na tela de bloqueio: desliga a biometria e volta ao login com senha. */
export function unlockWithPassword() {
  try { localStorage.setItem(KEY, '0') } catch { /* ok */ }
  store.lockError = ''
  store.locked = false
  store.authed = false
}

function lock() {
  store.locked = true
  unlock()
}

/** Trava ao abrir e ao voltar depois de 1 min em segundo plano. */
export async function initBioLock() {
  if (!isNative) return
  const { App } = await import('@capacitor/app')
  App.addListener('appStateChange', ({ isActive }) => {
    if (verifying) return // a própria tela de biometria pausa e retoma o app
    if (!isActive) {
      if (!store.locked) hiddenAt = Date.now()
      return
    }
    const away = hiddenAt ? Date.now() - hiddenAt : 0
    hiddenAt = 0
    if (bioEnabled() && !store.locked && away > RELOCK_MS) lock()
  })
  // Biometria removida do aparelho depois de ativada: não tranca (senão não haveria como entrar).
  if (bioEnabled() && (await bioAvailable())) lock()
}
