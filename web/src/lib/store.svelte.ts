import type { AlertEvent, AptState, BackupInfo, HaState, JfState, ContainersState, DeviceInfo, History, HostInfo, Me, OkResponse, PiHoleState, ServiceInfo, SystemSnapshot } from './types'
import { sfx } from './sfx'
import { openStream } from './sse'
import { conn, isNative, tap } from './platform'

export interface Toast { id: number; kind: 'ok' | 'err' | 'info'; text: string }

const HIST = 120

class Store {
  authed = $state<boolean | null>(null)
  me = $state.raw<Me | null>(null)
  connected = $state(false)

  host = $state.raw<HostInfo | null>(null)
  sys = $state.raw<SystemSnapshot | null>(null)
  hist = $state.raw<History>({ cpu: [], mem: [], rx: [], tx: [] })
  services = $state.raw<ServiceInfo[] | null>(null)
  containers = $state.raw<ContainersState | null>(null)
  pihole = $state.raw<PiHoleState | null>(null)
  network = $state.raw<DeviceInfo[] | null>(null)
  alerts = $state.raw<AlertEvent[]>([])
  apt = $state.raw<AptState | null>(null)
  backups = $state.raw<BackupInfo[] | null>(null)
  homeassistant = $state.raw<HaState | null>(null)
  jellyfin = $state.raw<JfState | null>(null)

  /** Ações em andamento: chave → rótulo (UI otimista). */
  pending = $state<Record<string, string>>({})
  toasts = $state<Toast[]>([])
}

export const store = new Store()

const push = (arr: number[], v: number) => (arr.length >= HIST ? [...arr.slice(1), v] : [...arr, v])

// ── Conexão em tempo real (SSE) ─────────────────────────
let close: (() => void) | null = null
let hideTimer: ReturnType<typeof setTimeout> | undefined
let lastAlertTs = 0

const handlers: Record<string, (v: never) => void> = {
  sys: (s: SystemSnapshot) => {
    store.sys = s
    const h = store.hist
    store.hist = {
      cpu: push(h.cpu, s.cpu),
      mem: push(h.mem, s.memTotal ? (100 * s.memUsed) / s.memTotal : 0),
      rx: push(h.rx, s.rxRate),
      tx: push(h.tx, s.txRate),
    }
  },
  history: (h: History) => (store.hist = h),
  host: (h: HostInfo) => {
    // Servidor atualizado enquanto a página estava aberta → recarrega a interface nova (no app ela é embutida).
    const before = store.host?.version
    store.host = h
    if (before && before !== h.version) {
      toast(`Servidor atualizado para v${h.version}`, 'ok')
      if (!isNative) setTimeout(() => location.reload(), 1500)
    }
  },
  // Sem duplicatas (a lista usa o nome como chave; repetido derrubaria a tela).
  services: (v: ServiceInfo[]) => (store.services = v.filter((s, i) => v.findIndex((x) => x.unit === s.unit) === i)),
  containers: (v: ContainersState) => (store.containers = v),
  pihole: (v: PiHoleState) => (store.pihole = v),
  network: (v: DeviceInfo[]) => (store.network = v),
  apt: (v: AptState) => (store.apt = v),
  backups: (v: BackupInfo[]) => (store.backups = v),
  homeassistant: (v: HaState) => (store.homeassistant = v),
  jellyfin: (v: JfState) => (store.jellyfin = v),
  alerts: (v: AlertEvent[]) => {
    // Alerta novo enquanto a interface está aberta → aviso na tela também.
    const fresh = v.filter((a) => a.ts > lastAlertTs)
    if (lastAlertTs && fresh.length) for (const a of fresh.reverse()) toast(`${a.title} — ${a.body}`, a.level === 'ok' ? 'ok' : 'err')
    lastAlertTs = Math.max(lastAlertTs, ...v.map((a) => a.ts), 1)
    store.alerts = v
  },
}

export function connect() {
  close?.()
  const headers: Record<string, string> = conn.token ? { Authorization: `Bearer ${conn.token}` } : {}
  close = openStream(
    conn.base + '/api/stream',
    headers,
    (event, data) => handlers[event]?.(JSON.parse(data) as never),
    (state) => {
      store.connected = state === 'open'
      if (state === 'unauthorized') {
        close = null
        store.authed = false
      }
    },
  )
}

export function disconnect() {
  close?.()
  close = null
  store.connected = false
}

// Economiza bateria no celular: fecha o stream após 30s em segundo plano; reabre ao voltar.
if (typeof document !== 'undefined') {
  document.addEventListener('visibilitychange', () => {
    clearTimeout(hideTimer)
    if (document.hidden) hideTimer = setTimeout(disconnect, 30_000)
    else if (store.authed && !close) connect()
  })
}

// ── Notificações e ações ────────────────────────────────
let toastId = 0
export function toast(text: string, kind: Toast['kind'] = 'info') {
  const id = ++toastId
  store.toasts = [...store.toasts, { id, kind, text }]
  if (kind === 'err') sfx.error()
  setTimeout(() => (store.toasts = store.toasts.filter((t) => t.id !== id)), kind === 'err' ? 6000 : 3200)
}

export async function act(key: string, label: string, fn: () => Promise<OkResponse>, okText?: string) {
  if (store.pending[key]) return
  store.pending[key] = label
  sfx.confirm()
  tap()
  try {
    const r = await fn()
    if (r.ok) toast(okText ?? r.message ?? 'Concluído', 'ok')
    else toast(r.message || 'Falha', 'err')
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e), 'err')
  } finally {
    delete store.pending[key]
  }
}
