export interface SystemSnapshot {
  ts: number; cpu: number; cores: number[]; load: number[]
  memTotal: number; memUsed: number; swapTotal: number; swapUsed: number
  tempC?: number; battery?: { percent: number; status: string }
  rxRate: number; txRate: number; uptime: number
}
export interface DiskInfo { mount: string; fs: string; total: number; used: number }
export interface HostInfo {
  hostname: string; os: string; kernel: string; cpuModel: string; cores: number
  addresses: string[]; disks: DiskInfo[]; version: string
}
export interface History { cpu: number[]; mem: number[]; rx: number[]; tx: number[] }
export interface ServiceInfo { unit: string; description: string; active: string; sub: string; pid: number; memory: number; since: number }
export interface UnitEntry { unit: string; description: string; active: string }
export interface ContainerInfo { id: string; name: string; image: string; state: string; status: string; ports: string; created: number }
export interface ContainersState { available: boolean; error?: string; items: ContainerInfo[] }
export interface PiHoleState {
  configured: boolean; online: boolean; error?: string; blocking: boolean; timer?: number
  total: number; blocked: number; percent: number; gravity: number; clients: number
  topBlocked: { name: string; count: number }[]; history: { t: number; total: number; blocked: number }[]
}
export interface DeviceInfo { ip: string; mac: string; name?: string; vendor?: string; online: boolean; iface?: string; alias?: string; randomMac: boolean; firstSeen: number; dnsQueries: number }
export interface OkResponse { ok: boolean; message?: string }
export interface Me { authenticated: boolean; hostname?: string; version: string; allowPower: boolean; terminal: boolean }
export interface AlertEvent { ts: number; kind: string; level: 'crit' | 'warn' | 'ok'; title: string; body: string; view: string }
export interface DeviceSummary { id: string; name: string; kind: 'app' | 'fcm' | 'apns' | 'webpush'; created: number; current: boolean }
export interface PushConfig { webPushKey: string; fcm: boolean; ntfy: boolean; deviceId?: string; apns: boolean }
export interface UpdateStatus { current: string; latest?: string; status: 'atualizado' | 'atualizando' | 'erro' | 'nunca'; message?: string; checkedAt: number; auto: boolean; supported: boolean }
export interface AptState { supported: boolean; total: number; security: number; packages: string[]; rebootRequired: boolean; rebootPackages: string[]; checkedAt: number; upgrading: boolean; lastResult?: string; lastRunAt: number; phased: number }
export interface BackupInfo { name: string; unit: string; status: 'ok' | 'running' | 'failed' | 'stale' | 'unknown'; lastRun: number; lastSuccess: number; nextRun: number; message?: string }
export interface NamedCount { name: string; count: number }
export interface HaEntity { id: string; name: string; domain: string; state: string; unit?: string }
export interface HaState { configured: boolean; online: boolean; error?: string; version?: string; location?: string; total: number; unavailable: number; domains: NamedCount[]; entities: HaEntity[] }
export interface JfSession { user: string; title: string; client?: string; device?: string; paused: boolean; progress: number; transcoding: boolean }
export interface JfState { configured: boolean; online: boolean; error?: string; name?: string; version?: string; library: NamedCount[]; sessions: JfSession[] }
export interface MetricsSeries { range: string; step: number; t: number[]; cpu: (number | null)[]; mem: (number | null)[]; temp: (number | null)[]; rx: (number | null)[]; tx: (number | null)[]; battery: (number | null)[] }
export interface MonitorDef { id: string; name: string; target: string }
export interface MonitorState { id: string; name: string; target: string; up: boolean; latencyMs?: number; error?: string; uptime24h: number; since: number; recent: number[]; builtin: boolean }
export interface DnsQuery { ts: number; domain: string; type: string; client: string; clientName?: string; status: string; blocked: boolean }
export interface BlockRule { mac: string; label?: string; manual: boolean; days: number[]; start?: string | null; end?: string | null }
export interface BlockState extends BlockRule { active: boolean; error?: string }
export interface AccessEntry { ts: number; ip: string; country?: string; method: 'web' | 'app' | 'terminal'; ok: boolean; device: string }
