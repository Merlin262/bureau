<script lang="ts">
  /** Indicador de estado: cheio = ativo, vazado = parado, vermelho piscando = falha, listrado = em transição */
  let { state }: { state: 'on' | 'off' | 'fail' | 'busy' } = $props()
</script>

<i class="sq {state}" aria-label={state}></i>

<style>
  .sq { display: inline-block; flex: none; width: 10px; height: 10px; border: 1px solid var(--fg); }
  .on { background: var(--fg); }
  .off { border-color: var(--fg-mute); }
  .fail { background: var(--red); border-color: var(--red); animation: blink 1s steps(2) infinite; }
  .busy {
    border-color: var(--fg-dim);
    background: repeating-linear-gradient(45deg, var(--fg-dim) 0 2px, transparent 2px 4px);
    animation: blink 0.5s steps(2) infinite;
  }
</style>
