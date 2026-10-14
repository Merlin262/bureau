import { mount } from 'svelte'
import { Capacitor } from '@capacitor/core'
import './app.css'
import App from './App.svelte'

// Erro fatal visível na tela: no celular não há console para ver o que aconteceu.
function fatal(reason: unknown) {
  const text = reason instanceof Error ? `${reason.message}\n\n${reason.stack ?? ''}` : String(reason)
  let el = document.getElementById('fatal')
  if (!el) {
    el = document.createElement('pre')
    el.id = 'fatal'
    el.title = 'Toque para fechar'
    el.onclick = () => el!.remove()
    document.body.append(el)
  }
  el.textContent = `▲ FALHA\n\n${text}`
}
window.addEventListener('error', (e) => fatal(e.error ?? e.message))
window.addEventListener('unhandledrejection', (e) => fatal(e.reason))

mount(App, { target: document.getElementById('app')! })

if (Capacitor.isNativePlatform()) {
  // No app a interface já vem embutida: service worker não serve para nada e pode atrapalhar.
  navigator.serviceWorker?.getRegistrations().then((regs) => regs.forEach((r) => r.unregister())).catch(() => {})
} else if (import.meta.env.PROD && 'serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => { /* sem HTTPS: segue sem cache offline */ })
}
