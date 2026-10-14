import { isNative, platform } from './platform'
import { initNativePush } from './push'
import { go, router } from './router.svelte'

/** Integração com o sistema no app: barra de status, botão voltar do Android e toques em notificações. */
export async function initNative() {
  if (!isNative) return
  document.documentElement.classList.add('native', platform)

  try {
    const { StatusBar, Style } = await import('@capacitor/status-bar')
    await StatusBar.setStyle({ style: Style.Dark })
    if (platform === 'android') await StatusBar.setBackgroundColor({ color: '#0b0b0c' })
  } catch { /* sem barra de status */ }

  const { App } = await import('@capacitor/app')
  App.addListener('backButton', () => {
    // Modal aberto → fecha (o componente escuta Escape); fora do painel → volta ao painel; senão, minimiza.
    if (document.querySelector('[role=dialog]')) {
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    } else if (router.current !== 'painel') {
      go('painel')
    } else {
      App.minimizeApp()
    }
  })

  await initNativePush()
}
