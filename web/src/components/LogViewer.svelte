<script lang="ts">
  import { onDestroy } from 'svelte'

  let { load }: { load: () => Promise<{ text: string }> } = $props()

  let text = $state('')
  let loading = $state(true)
  let live = $state(false)
  let filter = $state('')
  let pre: HTMLPreElement | undefined = $state()
  let timer: ReturnType<typeof setInterval> | undefined

  const shown = $derived.by(() => {
    if (!filter) return text
    const f = filter.toLowerCase()
    return text.split('\n').filter((l) => l.toLowerCase().includes(f)).join('\n')
  })

  async function refresh() {
    try {
      const r = await load()
      const atBottom = !pre || pre.scrollHeight - pre.scrollTop - pre.clientHeight < 40
      text = r.text || '(vazio)'
      loading = false
      if (atBottom) requestAnimationFrame(() => pre && (pre.scrollTop = pre.scrollHeight))
    } catch (e) {
      text = String(e)
      loading = false
    }
  }

  $effect(() => {
    clearInterval(timer)
    if (live) timer = setInterval(refresh, 2500)
  })

  refresh()
  onDestroy(() => clearInterval(timer))
</script>

<div class="logs">
  <div class="bar">
    <input class="field" placeholder="Filtrar…" bind:value={filter} />
    <button class="btn" class:on={live} onclick={() => (live = !live)}>{live ? '● AO VIVO' : '○ AO VIVO'}</button>
    <button class="btn" onclick={refresh}>ATUALIZAR</button>
  </div>
  <pre bind:this={pre}>{loading ? 'CARREGANDO REGISTROS…' : shown}</pre>
</div>

<style>
  .logs { padding: 0 14px 14px; border-bottom: 1px solid var(--line); background: rgba(0, 0, 0, 0.35); }
  .bar { display: flex; gap: 6px; padding: 10px 0; }
  .bar .field { height: 32px; }
  pre {
    max-height: 360px;
    overflow: auto;
    padding: 10px 12px;
    border-left: 2px solid var(--red);
    background: #060607;
    font-family: var(--mono);
    font-size: 11.5px;
    line-height: 1.55;
    color: #c9c7c0;
    white-space: pre-wrap;
    word-break: break-all;
  }
</style>
