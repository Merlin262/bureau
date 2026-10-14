<script lang="ts">
  import type { Snippet } from 'svelte'

  let { title, onclose, children }: { title: string; onclose: () => void; children: Snippet } = $props()

  function onkey(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.stopPropagation()
      onclose()
    }
  }
</script>

<svelte:window onkeydown={onkey} />

<div class="backdrop" role="presentation" onclick={(e) => e.target === e.currentTarget && onclose()}>
  <div class="modal" role="dialog" aria-modal="true" aria-label={title}>
    <header>
      <h2>{title}</h2>
      <button class="btn ghost" onclick={onclose}>[ESC] FECHAR</button>
    </header>
    <div class="body">{@render children()}</div>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 50;
    display: grid;
    place-items: center;
    padding: 16px;
    background: rgba(5, 5, 6, 0.82);
    animation: fade 120ms ease-out;
  }
  .modal {
    display: flex;
    flex-direction: column;
    width: min(680px, 100%);
    max-height: min(80vh, 760px);
    border: 1px solid var(--line-2);
    border-left: 3px solid var(--fg);
    background: var(--bg-2);
    animation: slide 160ms var(--ease);
  }
  header { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 16px 16px 12px; border-bottom: 1px solid var(--line); }
  h2 { font-stretch: 66%; font-weight: 800; font-size: 34px; line-height: 0.9; text-transform: uppercase; }
  .body { overflow: auto; padding: 14px 16px 16px; }
  @keyframes fade { from { opacity: 0; } }
  @keyframes slide { from { opacity: 0; transform: translateX(-14px); } }
</style>
