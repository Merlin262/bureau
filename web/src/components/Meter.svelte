<script lang="ts">
  /** Barra segmentada, estilo "HUD". value: 0–100 */
  let { value = 0, hot = 85, height = 10 }: { value?: number; hot?: number; height?: number } = $props()
  const v = $derived(Math.max(0, Math.min(100, value || 0)))
</script>

<div class="meter" class:hot={v >= hot} style="--v:{v}%; height:{height}px" role="meter" aria-valuenow={Math.round(v)} aria-valuemin={0} aria-valuemax={100}>
  <i></i>
</div>

<style>
  .meter {
    position: relative;
    width: 100%;
    background: repeating-linear-gradient(90deg, rgba(236, 235, 230, 0.13) 0 5px, transparent 5px 7px);
  }
  i {
    position: absolute;
    inset: 0 auto 0 0;
    width: var(--v);
    background: repeating-linear-gradient(90deg, var(--fg) 0 5px, transparent 5px 7px);
    transition: width 0.45s var(--ease);
  }
  .hot i { background: repeating-linear-gradient(90deg, var(--red) 0 5px, transparent 5px 7px); }
</style>
