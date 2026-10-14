<script lang="ts">
  /** Gráfico de linha do histórico persistido: falhas de dados (null) viram lacunas; toque/mouse mostra o valor. */
  let {
    label,
    t,
    values,
    format,
    max,
    hot,
  }: {
    label: string
    t: number[]
    values: (number | null)[]
    format: (v: number) => string
    max?: number
    hot?: number
  } = $props()

  const W = 300
  const H = 80
  let hover = $state<number | null>(null)
  let svg: SVGSVGElement | undefined = $state()

  const present = $derived(values.filter((v): v is number => v != null))
  const top = $derived(max ?? Math.max(1, ...present) * 1.15)
  const x = (i: number) => (values.length <= 1 ? W : (i / (values.length - 1)) * W)
  const y = (v: number) => H - (Math.min(v, top) / top) * (H - 2) - 1

  const path = $derived.by(() => {
    let d = ''
    let pen = false
    values.forEach((v, i) => {
      if (v == null) { pen = false; return }
      d += `${pen ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`
      pen = true
    })
    return d
  })

  const avg = $derived(present.length ? present.reduce((a, b) => a + b, 0) / present.length : null)
  const peak = $derived(present.length ? Math.max(...present) : null)
  const isHot = $derived(hot != null && peak != null && peak >= hot)

  const span = $derived(t.length > 1 ? t[t.length - 1] - t[0] : 0)
  const time = (unix: number) =>
    new Date(unix * 1000).toLocaleString('pt-BR', span > 2 * 86400
      ? { day: '2-digit', month: '2-digit', hour: '2-digit' }
      : { hour: '2-digit', minute: '2-digit' })

  function move(e: PointerEvent) {
    if (!svg || values.length === 0) return
    const r = svg.getBoundingClientRect()
    hover = Math.max(0, Math.min(values.length - 1, Math.round(((e.clientX - r.left) / r.width) * (values.length - 1))))
  }
</script>

<div class="chart">
  <div class="head">
    <span class="mono dim">{label}</span>
    <span class="mono num" class:red={isHot}>
      {#if hover != null && values[hover] != null}
        {format(values[hover]!)} <span class="dim">· {time(t[hover])}</span>
      {:else if avg != null}
        MÉD {format(avg)} <span class="dim">· PICO {format(peak!)}</span>
      {:else}
        <span class="dim">SEM DADOS</span>
      {/if}
    </span>
  </div>
  <svg bind:this={svg} viewBox="0 0 {W} {H}" preserveAspectRatio="none" class:red={isHot} role="img" aria-label={label}
    onpointermove={move} onpointerdown={move} onpointerleave={() => (hover = null)}>
    <line x1="0" x2={W} y1={H - 0.5} y2={H - 0.5} class="base" />
    <path d={path} class="line" />
    {#if hover != null}
      <line x1={x(hover)} x2={x(hover)} y1="0" y2={H} class="cursor" />
    {/if}
  </svg>
  {#if t.length > 1}
    <div class="axis mono"><span>{time(t[0])}</span><span>{time(t[t.length - 1])}</span></div>
  {/if}
</div>

<style>
  .chart { min-width: 0; }
  .head { display: flex; justify-content: space-between; gap: 8px; margin-bottom: 6px; font-size: 11px; }
  .head .num { font-family: var(--mono); font-size: 11px; white-space: nowrap; }
  svg { display: block; width: 100%; height: 80px; color: var(--fg); touch-action: pan-y; cursor: crosshair; }
  svg.red { color: var(--red); }
  .line { fill: none; stroke: currentColor; stroke-width: 1.25; vector-effect: non-scaling-stroke; }
  .base { stroke: var(--line); vector-effect: non-scaling-stroke; }
  .cursor { stroke: var(--red); stroke-width: 1; vector-effect: non-scaling-stroke; }
  .axis { display: flex; justify-content: space-between; margin-top: 4px; font-size: 9.5px; color: var(--fg-mute); }
</style>
