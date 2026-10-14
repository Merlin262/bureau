const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

export function bytes(n: number, digits = 1): string {
  if (!n || n < 0) return '0 B'
  const i = Math.min(UNITS.length - 1, Math.floor(Math.log(n) / Math.log(1024)))
  const v = n / 1024 ** i
  return `${v.toFixed(i === 0 || v >= 100 ? 0 : digits)} ${UNITS[i]}`
}

export function bytesParts(n: number): [string, string] {
  const [v, u] = bytes(n).split(' ')
  return [v, u]
}

export const rate = (n: number) => `${bytes(n)}/s`

export function duration(seconds: number): string {
  if (!seconds || seconds < 0) return '—'
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return `${Math.floor(seconds)}s`
}

export const since = (unix: number) => (unix ? duration(Date.now() / 1000 - unix) : '—')

export const pct = (v: number, total: number) => (total ? (100 * v) / total : 0)

export const int = (n: number) => n.toLocaleString('pt-BR')

export const shortUnit = (u: string) => u.replace(/\.service$/, '')

const ACTIONS: Record<string, string> = { start: 'INICIANDO', stop: 'PARANDO', restart: 'REINICIANDO' }
export const actionLabel = (a: string) => ACTIONS[a] ?? a.toUpperCase()
