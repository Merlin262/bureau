// Gera os ícones do BUREAU (PNG sem dependências): PWA, badge de notificação e fontes do app nativo.
// Uso: node scripts/icons.mjs   → depois, para o app: npx @capacitor/assets generate
import { deflateSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'

const BG = [11, 11, 12, 255]
const FG = [236, 235, 230, 255]
const RED = [255, 43, 43, 255]
const CLEAR = [0, 0, 0, 0]
const WHITE = [255, 255, 255, 255]

// Logo em coordenadas 0–512: moldura, núcleo vermelho e "braços" laterais.
const LOGO = [
  [96, 96, 320, 28, 'f'], [96, 388, 320, 28, 'f'], [96, 96, 28, 320, 'f'], [388, 96, 28, 320, 'f'],
  [176, 176, 160, 160, 'r'], [96, 232, 80, 48, 'f'], [336, 232, 80, 48, 'f'],
]

const crcTable = new Uint32Array(256).map((_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
const crc = (b) => { let c = ~0; for (const x of b) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return ~c >>> 0 }
const chunk = (type, data) => {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length)
  const td = Buffer.concat([Buffer.from(type), data])
  const c = Buffer.alloc(4); c.writeUInt32BE(crc(td))
  return Buffer.concat([len, td, c])
}

/** scale: fração do canvas ocupada pelo logo (1 = ícone cheio). colors: { bg, f, r } */
function png(file, size, { scale = 1, bg = BG, f = FG, r = RED } = {}) {
  const row = size * 4 + 1
  const px = Buffer.alloc(size * row)
  const k = (size * scale) / 512
  const off = (size - size * scale) / 2
  for (let y = 0; y < size; y++) {
    px[y * row] = 0
    for (let x = 0; x < size; x++) {
      const X = (x - off) / k, Y = (y - off) / k
      let c = bg
      for (const [rx, ry, rw, rh, t] of LOGO) if (X >= rx && X < rx + rw && Y >= ry && Y < ry + rh) c = t === 'r' ? r : f
      px.set(c, y * row + 1 + x * 4)
    }
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6
  writeFileSync(file, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(px, { level: 9 })), chunk('IEND', Buffer.alloc(0))]))
}

mkdirSync('assets', { recursive: true })
png('public/icon-192.png', 192)
png('public/icon-512.png', 512)
png('public/badge-96.png', 96, { bg: CLEAR, f: WHITE, r: WHITE })   // monocromático (barra de status)
png('assets/icon-only.png', 1024)
png('assets/icon-foreground.png', 1024, { scale: 0.62, bg: CLEAR })  // zona segura do ícone adaptativo
png('assets/icon-background.png', 1024, { scale: 0, f: BG, r: BG })
png('assets/splash.png', 2732, { scale: 0.22 })
png('assets/splash-dark.png', 2732, { scale: 0.22 })
console.log('Ícones gerados.')
