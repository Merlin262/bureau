export const views = [
  { id: 'painel', label: 'Painel', short: 'Painel', code: 'VISÃO GERAL' },
  { id: 'servicos', label: 'Serviços', short: 'Serviços', code: 'SYSTEMD' },
  { id: 'docker', label: 'Contêineres', short: 'Docker', code: 'DOCKER' },
  { id: 'pihole', label: 'Pi-hole', short: 'Pi-hole', code: 'DNS' },
  { id: 'rede', label: 'Rede', short: 'Rede', code: 'LAN' },
  { id: 'sistema', label: 'Sistema', short: 'Sistema', code: 'HOST' },
] as const

export type ViewId = (typeof views)[number]['id']

function fromHash(): ViewId {
  const id = location.hash.replace(/^#\/?/, '')
  return (views.find((v) => v.id === id)?.id ?? 'painel') as ViewId
}

class Router {
  current = $state<ViewId>(fromHash())
}

export const router = new Router()

export function go(id: ViewId) {
  if (router.current === id) return
  history.replaceState(null, '', `#/${id}`)
  router.current = id
}

export function step(delta: number) {
  const i = views.findIndex((v) => v.id === router.current)
  go(views[(i + delta + views.length) % views.length].id)
}

window.addEventListener('hashchange', () => (router.current = fromHash()))
