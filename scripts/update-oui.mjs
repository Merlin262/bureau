// Gera server/Data/oui.tsv.gz: prefixo de MAC (24 bits) → fabricante, a partir do registro público do IEEE.
// Uso: node scripts/update-oui.mjs            (baixa https://standards-oui.ieee.org/oui/oui.csv)
//      node scripts/update-oui.mjs oui.csv    (usa um arquivo já baixado)
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { gzipSync } from 'node:zlib'

const src = process.argv[2]
const csv = src
  ? readFileSync(src, 'utf8')
  : await (await fetch('https://standards-oui.ieee.org/oui/oui.csv', { headers: { 'User-Agent': 'Mozilla/5.0' } })).text()

function parseLine(line) {
  const out = []
  let cur = '', q = false
  for (let i = 0; i < line.length; i++) {
    const c = line[i]
    if (q) {
      if (c === '"' && line[i + 1] === '"') { cur += '"'; i++ }
      else if (c === '"') q = false
      else cur += c
    } else if (c === '"') q = true
    else if (c === ',') { out.push(cur); cur = '' }
    else cur += c
  }
  out.push(cur)
  return out
}

// "Samsung Electronics Co.,Ltd" → "Samsung Electronics"; "TP-LINK TECHNOLOGIES CO.,LTD." → "TP-Link Technologies"
const SUFFIX = /[\s,.]*\b(co\.?,?\s*ltd|co\.?|ltd|limited|inc|incorporated|corp|corporation|llc|l\.l\.c|gmbh|ag|s\.?a\.?|s\.?r\.?l|b\.?v|oy|ab|a\/s|plc|pte|pty|kg|sas|s\.p\.a|company|\(.*?\))\.?\s*$/i
function clean(name) {
  let s = name.replace(/\s+/g, ' ').trim()
  for (let k = 0; k < 4; k++) s = s.replace(SUFFIX, '').trim().replace(/[,.\s]+$/, '')
  if (s === s.toUpperCase() && /[A-Z]{4,}/.test(s)) {
    s = s.toLowerCase().replace(/(^|[\s-])(\p{L})/gu, (_, a, b) => a + b.toUpperCase())
  }
  return s.slice(0, 40) || name.trim().slice(0, 40)
}

const rows = new Map()
for (const line of csv.split(/\r?\n/).slice(1)) {
  if (!line.startsWith('MA-L,')) continue
  const [, prefix, org] = parseLine(line)
  if (/^[0-9A-F]{6}$/.test(prefix) && org) rows.set(prefix, clean(org))
}

const tsv = [...rows].sort(([a], [b]) => (a < b ? -1 : 1)).map(([p, n]) => `${p}\t${n}`).join('\n') + '\n'
mkdirSync('server/Data', { recursive: true })
const gz = gzipSync(Buffer.from(tsv), { level: 9 })
writeFileSync('server/Data/oui.tsv.gz', gz)
console.log(`${rows.size} fabricantes → server/Data/oui.tsv.gz (${(gz.length / 1024).toFixed(0)} KB)`)
