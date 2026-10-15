import { afterEach, beforeEach, vi } from 'vitest'
import { http } from './http'

// APIs do navegador que o jsdom não tem.
if (!window.matchMedia) {
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia
}
window.scrollTo = (() => {}) as typeof window.scrollTo
Element.prototype.scrollTo = function () {} as typeof Element.prototype.scrollTo
// O rAF do jsdom usa outro relógio que o performance.now(); aqui os dois batem, como no navegador.
globalThis.requestAnimationFrame = ((cb: FrameRequestCallback) => setTimeout(() => cb(performance.now()), 16) as unknown as number)
globalThis.cancelAnimationFrame = ((id: number) => clearTimeout(id)) as typeof cancelAnimationFrame

beforeEach(async () => {
  http.reset()
  vi.stubGlobal('fetch', http.fetch)
  // Import tardio: os vi.mock do arquivo de teste (ex.: Capacitor nativo) já valem para o store/platform.
  const { resetStore } = await import('./util')
  resetStore()
  localStorage.clear()
  location.hash = ''
})

afterEach(() => {
  vi.useRealTimers()
})
