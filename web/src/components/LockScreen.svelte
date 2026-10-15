<script lang="ts">
  import { unlock, unlockWithPassword } from '../lib/biometric'
  import { store } from '../lib/store.svelte'
</script>

<div class="lock">
  <div class="mono dim"><span class="red">■</span> SISTEMA DE CONTROLE DOMÉSTICO</div>
  <h1>Bureau</h1>
  <p class="mono">BLOQUEADO // CONFIRME SUA IDENTIDADE</p>
  <button class="btn on" onclick={unlock}>Desbloquear ▸</button>
  {#if store.lockError}<p class="mono err">▲ {store.lockError}</p>{/if}
  <button class="btn ghost alt" onclick={unlockWithPassword}>Entrar com a senha</button>
</div>

<style>
  .lock {
    position: fixed;
    inset: 0;
    z-index: 90;
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 18px;
    padding: var(--gutter);
    background: var(--bg);
  }
  h1 { font-stretch: 62%; font-weight: 800; font-size: clamp(88px, 22vw, 180px); line-height: 0.78; text-transform: uppercase; }
  .btn { height: 44px; align-self: flex-start; padding: 0 20px; }
  .alt { height: 36px; font-size: 11px; }
  .err { color: var(--red); font-size: 12px; max-width: 520px; }
</style>
