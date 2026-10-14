<script lang="ts">
  import type { Snippet } from 'svelte'

  let {
    label,
    aside,
    children,
    alert = false,
    class: cls = '',
  }: { label?: string; aside?: Snippet; children: Snippet; alert?: boolean; class?: string } = $props()
</script>

<section class="panel {cls}" class:alert>
  {#if label || aside}
    <header>
      {#if label}<span class="mono">{label}</span>{/if}
      {#if aside}<span class="aside">{@render aside()}</span>{/if}
    </header>
  {/if}
  {@render children()}
</section>

<style>
  .panel {
    position: relative;
    min-width: 0;
    padding: 16px;
    border: 1px solid var(--line);
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(255, 255, 255, 0.008));
  }
  /* cantoneiras */
  .panel::before,
  .panel::after {
    content: '';
    position: absolute;
    width: 9px;
    height: 9px;
    pointer-events: none;
  }
  .panel::before { top: -1px; left: -1px; border-top: 1px solid var(--fg); border-left: 1px solid var(--fg); }
  .panel::after { bottom: -1px; right: -1px; border-bottom: 1px solid var(--fg); border-right: 1px solid var(--fg); }

  .panel.alert { border-color: rgba(255, 43, 43, 0.55); background: var(--red-dim); }
  .panel.alert::before, .panel.alert::after { border-color: var(--red); }

  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    min-height: 20px;
    margin-bottom: 14px;
    color: var(--fg-dim);
  }
  .aside { display: flex; align-items: center; gap: 6px; }
</style>
