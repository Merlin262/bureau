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
export interface DeviceInfo { ip: string; mac: string; name?: string; vendor?: string; online: boolean; iface?: string; alias?: string; randomMac: boolean }
export interface OkResponse { ok: boolean; message?: string }
export interface Me { authenticated: boolean; hostname?: string; version: string; allowPower: boolean }
export interface AlertEvent { ts: number; kind: string; level: 'crit' | 'warn' | 'ok'; title: string; body: string; view: string }
export interface DeviceSummary { id: string; name: string; kind: 'app' | 'fcm' | 'webpush'; created: number; current: boolean }
export interface PushConfig { webPushKey: string; fcm: boolean; ntfy: boolean; deviceId?: string }
export interface UpdateStatus { current: string; latest?: string; status: 'atualizado' | 'atualizando' | 'erro' | 'nunca'; message?: string; checkedAt: number; auto: boolean; supported: boolean }
