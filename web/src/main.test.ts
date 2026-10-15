import { expect, it } from 'vitest'
import { http } from './test/http'
import { flush } from './test/util'

it('monta a interface e mostra erros fatais na tela', async () => {
  http.on('GET /api/auth/me', { authenticated: false, version: '1', allowPower: false })
  const root = document.createElement('div')
  root.id = 'app'
  document.body.append(root)
  await import('./main')
  await flush()
  expect(root.textContent).toContain('IDENTIFIQUE-SE')

  window.dispatchEvent(new ErrorEvent('error', { error: new Error('quebrou') }))
  const fatal = document.getElementById('fatal')!
  expect(fatal.textContent).toContain('▲ FALHA')
  expect(fatal.textContent).toContain('quebrou')

  const rejection = new Event('unhandledrejection') as Event & { reason: unknown }
  rejection.reason = 'promessa recusada'
  window.dispatchEvent(rejection)
  expect(document.getElementById('fatal')!.textContent).toContain('promessa recusada') // reaproveita o mesmo aviso

  window.dispatchEvent(new ErrorEvent('error', { message: 'sem objeto de erro' }))
  expect(document.getElementById('fatal')!.textContent).toContain('sem objeto de erro')
  fatal.click()
  expect(document.getElementById('fatal')).toBeNull()
})
