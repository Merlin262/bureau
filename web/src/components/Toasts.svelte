<script lang="ts">
  import { store } from '../lib/store.svelte'
</script>

<div class="toasts" aria-live="polite">
  {#each store.toasts as t (t.id)}
    <div class="toast {t.kind}">
      <span class="tag">{t.kind === 'err' ? '[FALHA]' : t.kind === 'ok' ? '[OK]' : '[INFO]'}</span>
      <span>{t.text}</span>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    z-index: 60;
    right: 16px;
    bottom: calc(16px + var(--safe-b));
    display: flex;
    flex-direction: column;
    gap: 6px;
    max-width: min(420px, calc(100vw - 32px));
    pointer-events: none;
  }
  .toast {
    display: flex;
    gap: 10px;
    padding: 10px 14px;
    border: 1px solid var(--line-2);
    border-left: 3px solid var(--fg);
    background: var(--bg-2);
    font-family: var(--mono);
    font-size: 12px;
    letter-spacing: 0.03em;
    animation: in 160ms var(--ease);
  }
  .toast.err { border-left-color: var(--red); }
  .toast.err .tag { color: var(--red); }
  .tag { flex: none; color: var(--fg-dim); }
  @keyframes in { from { opacity: 0; transform: translateX(16px); } }
  @media (max-width: 820px) {
    .toasts { bottom: calc(76px + var(--safe-b)); }
  }
</style>
