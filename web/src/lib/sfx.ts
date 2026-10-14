// Bipes sintetizados (WebAudio) — nenhum arquivo de áudio para baixar.
let ctx: AudioContext | null = null
let enabled = (() => {
  try { return localStorage.getItem('bureau.sfx') === '1' } catch { return false }
})()

function tone(freq: number, dur: number, type: OscillatorType = 'square', gain = 0.03, slide = 0) {
  if (!enabled) return
  try {
    ctx ??= new AudioContext()
    const t = ctx.currentTime
    const o = ctx.createOscillator()
    const g = ctx.createGain()
    o.type = type
    o.frequency.setValueAtTime(freq, t)
    if (slide) o.frequency.exponentialRampToValueAtTime(freq * slide, t + dur)
    g.gain.setValueAtTime(gain, t)
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur)
    o.connect(g).connect(ctx.destination)
    o.start(t)
    o.stop(t + dur)
  } catch { /* sem áudio */ }
}

export const sfx = {
  get enabled() { return enabled },
  set enabled(v: boolean) {
    enabled = v
    try { localStorage.setItem('bureau.sfx', v ? '1' : '0') } catch { /* modo privado */ }
  },
  move: () => tone(1760, 0.025, 'square', 0.015),
  select: () => tone(880, 0.06, 'triangle', 0.04, 1.5),
  confirm: () => tone(660, 0.09, 'triangle', 0.04, 2),
  error: () => tone(140, 0.22, 'sawtooth', 0.04, 0.6),
}
