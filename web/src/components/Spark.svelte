<script lang="ts">
  /** Gráfico de linha mínimo em SVG. Os pontos ficam alinhados à direita (o mais recente na borda). */
  let {
    values,
    max,
    slots = 120,
    height = 48,
    red = false,
  }: { values: number[]; max?: number; slots?: number; height?: number; red?: boolean } = $props()

  const W = 240
  const path = $derived.by(() => {
    if (values.length < 2) return { line: '', area: '' }
    const top = max ?? Math.max(1, ...values) * 1.15
    const dx = W / (slots - 1)
    const x0 = W - (values.length - 1) * dx
    let line = ''
    values.forEach((v, i) => {
      const y = height - (Math.min(v, top) / top) * (height - 2) - 1
      line += `${i ? 'L' : 'M'}${(x0 + i * dx).toFixed(1)},${y.toFixed(1)}`
    })
    return { line, area: `${line}L${W},${height}L${x0.toFixed(1)},${height}Z` }
  })
</script>

<svg viewBox="0 0 {W} {height}" preserveAspectRatio="none" style="height:{height}px" class:red aria-hidden="true">
  <path class="area" d={path.area} />
  <path class="line" d={path.line} />
</svg>

<style>
  svg { display: block; width: 100%; overflow: visible; color: var(--fg); }
  svg.red { color: var(--red); }
  .line { fill: none; stroke: currentColor; stroke-width: 1.25; vector-effect: non-scaling-stroke; }
  .area { fill: currentColor; opacity: 0.08; }
</style>
