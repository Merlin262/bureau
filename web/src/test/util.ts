import { tick } from 'svelte'
import { store } from '../lib/store.svelte'
import type { DeviceInfo, HostInfo, PiHoleState, SystemSnapshot } from '../lib/types'

/** Estado limpo entre testes (o store é um singleton do módulo). */
export function resetStore() {
  Object.assign(store, {
    authed: null, me: null, connected: false, host: null, sys: null,
    hist: { cpu: [], mem: [], rx: [], tx: [] }, services: null, containers: null, pihole: null, network: null,
    alerts: [], outbox: [], apt: null, backups: null, homeassistant: null, jellyfin: null, monitors: null, blocks: [], wol: [],
    locked: false, lockError: '', pending: {}, toasts: [],
  })
}

/** Espera promessas pendentes (fetch falso, act) e a atualização do DOM. */
export async function flush(times = 3) {
  for (let i = 0; i < times; i++) {
    await new Promise((r) => setTimeout(r, 0))
    await tick()
  }
}

export const sys = (over: Partial<SystemSnapshot> = {}): SystemSnapshot => ({
  ts: 0, cpu: 12, cores: [10, 14], load: [0.5, 0.4, 0.3], memTotal: 8 * 2 ** 30, memUsed: 2 * 2 ** 30,
  swapTotal: 2 ** 30, swapUsed: 2 ** 28, tempC: 55, battery: { percent: 90, status: 'Charging' },
  rxRate: 2048, txRate: 512, uptime: 90061, ...over,
})

export const host = (over: Partial<HostInfo> = {}): HostInfo => ({
  hostname: 'merlin-server', os: 'Ubuntu 26.04', kernel: '6.20', cpuModel: 'Intel i5', cores: 4,
  addresses: ['wlp2s0 192.168.100.100'], disks: [{ mount: '/', fs: 'ext4', total: 100 * 2 ** 30, used: 95 * 2 ** 30 }],
  version: '0.4.8', ...over,
})

export const pihole = (over: Partial<PiHoleState> = {}): PiHoleState => ({
  configured: true, online: true, blocking: true, total: 12345, blocked: 2345, percent: 19.04, gravity: 150000, clients: 7,
  topBlocked: [{ name: 'ads.example', count: 50 }, { name: 'track.example', count: 10 }],
  history: [{ t: 1700000000, total: 40, blocked: 10 }, { t: 1700000600, total: 20, blocked: 2 }], ...over,
})

export const device = (over: Partial<DeviceInfo> = {}): DeviceInfo => ({
  ip: '192.168.100.172', mac: '30:56:0f:86:a7:dd', name: 'merlinpc', vendor: 'Realtek', online: true, iface: 'wlp2s0',
  randomMac: false, firstSeen: 0, dnsQueries: 100, ...over,
})
