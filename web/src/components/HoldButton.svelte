<script lang="ts">
  /** Botão "segure para confirmar" — evita toques acidentais em ações destrutivas. */
  let {
    label,
    onconfirm,
    ms = 1200,
    disabled = false,
  }: { label: string; onconfirm: () => void; ms?: number; disabled?: boolean } = $props()

  let p = $state(0)
  let raf = 0

  function start() {
    if (disabled) return
    const t0 = performance.now()
    const tick = (t: number) => {
      p = Math.min(1, (t - t0) / ms)
      if (p >= 1) {
        reset()
        onconfirm()
        return
      }
      raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)
  }

  function reset() {
    cancelAnimationFrame(raf)
    p = 0
  }
</script>

<button
  class="btn danger hold"
  style="--p:{p}"
  {disabled}
  onpointerdown={start}
  onpointerup={reset}
  onpointerleave={reset}
  onpointercancel={reset}
  onkeydown={(e) => (e.key === ' ' || e.key === 'Enter') && !e.repeat && (e.preventDefault(), start())}
  onkeyup={reset}
  oncontextmenu={(e) => e.preventDefault()}
>
  <span class="fill"></span>
  <span class="txt">{p > 0 ? `SEGURE… ${Math.round(p * 100)}%` : label}</span>
</button>

<style>
  .hold { overflow: hidden; touch-action: none; -webkit-touch-callout: none; user-select: none; border-color: rgba(255, 43, 43, 0.6); }
  .fill {
    position: absolute;
    inset: 0;
    background: var(--red);
    transform: scaleX(var(--p));
    transform-origin: left;
  }
  .txt { position: relative; }
  .hold:hover .txt { color: var(--bg); }
</style>
