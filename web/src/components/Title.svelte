<script lang="ts">
  import type { Snippet } from 'svelte'

  let { index, code, label, children }: { index: string; code: string; label: string; children?: Snippet } = $props()
</script>

<header class="title">
  <div class="meta mono">
    <span class="red">{index}</span>
    <span class="dim">// {code}</span>
    <span class="rule"></span>
  </div>
  <div class="line">
    <h1 data-text={label}>{label}</h1>
    {#if children}<div class="tools">{@render children()}</div>{/if}
  </div>
</header>

<style>
  .title { margin-bottom: 26px; }
  .meta { display: flex; align-items: center; gap: 10px; margin-bottom: 6px; }
  .rule { flex: 1; height: 1px; background: var(--line); }

  .line { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; flex-wrap: wrap; }

  h1 {
    position: relative;
    font-stretch: 62%;
    font-weight: 800;
    font-size: clamp(52px, 9vw, 116px);
    line-height: 0.8;
    letter-spacing: -0.012em;
    text-transform: uppercase;
    animation: glitch 260ms steps(1) both;
  }

  /* Entrada com "falha de sinal": deslocamento RGB + recortes, ~quarto de segundo */
  @keyframes glitch {
    0%   { opacity: 0; }
    10%  { opacity: 1; transform: translateX(-8px); clip-path: inset(0 0 62% 0); text-shadow: 4px 0 var(--red), -4px 0 var(--cyan); }
    30%  { transform: translateX(5px); clip-path: inset(48% 0 0 0); text-shadow: -3px 0 var(--red), 3px 0 var(--cyan); }
    50%  { transform: translateX(-2px); clip-path: inset(18% 0 30% 0); text-shadow: 2px 0 var(--red), -2px 0 var(--cyan); }
    70%  { transform: none; clip-path: inset(0); text-shadow: 1px 0 var(--red); }
    100% { transform: none; clip-path: none; text-shadow: none; }
  }

  .tools { display: flex; gap: 8px; flex-wrap: wrap; padding-bottom: 4px; }
</style>
