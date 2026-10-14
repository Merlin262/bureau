import { NativeBiometric } from '@capgo/capacitor-native-biometric'
import type { OkResponse } from './types'
import { isNative } from './platform'
import { store } from './store.svelte'

// Bloqueio do app por biometria (digital/rosto; o PIN do aparelho serve de alternativa).
const KEY = 'bureau.bio'
const RELOCK_MS = 60_000
let hiddenAt = 0

export function bioEnabled(): boolean {
  try { return isNative && localStorage.getItem(KEY) === '1' } catch { return false }
}

export async function bioAvailable(): Promise<boolean> {
  if (!isNative) return false
  try { return (await NativeBiometric.isAvailable({ useFallback: true })).isAvailable } catch { return false }
}

async function verify(reason: string): Promise<boolean> {
  try {
    await NativeBiometric.verifyIdentity({
      reason, title: 'BUREAU', subtitle: 'Confirme que é você', negativeButtonText: 'Cancelar', useFallback: true, maxAttempts: 5,
    })
    return true
  } catch {
    return false
  }
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

/** Trava ao abrir e ao voltar depois de 1 min em segundo plano. */
export async function initBioLock() {
  if (!isNative) return
  const { App } = await import('@capacitor/app')
  App.addListener('appStateChange', ({ isActive }) => {
    if (!isActive) hiddenAt = Date.now()
    else if (bioEnabled() && hiddenAt && Date.now() - hiddenAt > RELOCK_MS) {
      store.locked = true
      unlock()
    }
  })
  if (bioEnabled()) {
    store.locked = true
    unlock()
  }
}
