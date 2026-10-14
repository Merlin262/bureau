<script lang="ts">
  import { onDestroy, onMount } from 'svelte'
  import { api } from '../lib/api'
  import { conn } from '../lib/platform'

  let { onclose }: { onclose: () => void } = $props()

  let host: HTMLDivElement | undefined = $state()
  let status = $state<'conectando' | 'conectado' | 'encerrado' | 'erro'>('conectando')
  let message = $state('')
  let ctrl = $state(false)
  let ws: WebSocket | undefined
  let cleanup: (() => void) | undefined
  const enc = new TextEncoder()

  function send(data: string) {
    if (ws?.readyState === WebSocket.OPEN) ws.send(enc.encode(data))
  }

  // Teclas que o teclado do celular não tem.
  const KEYS: [string, string][] = [['ESC', '\x1b'], ['TAB', '\t'], ['↑', '\x1b[A'], ['↓', '\x1b[B'], ['←', '\x1b[D'], ['→', '\x1b[C'], ['|', '|'], ['~', '~'], ['/', '/']]

  onMount(async () => {
    // xterm só é baixado quando o terminal é aberto (não pesa no resto do app).
    const [{ Terminal }, { FitAddon }] = await Promise.all([import('@xterm/xterm'), import('@xterm/addon-fit'), import('@xterm/xterm/css/xterm.css')])
    const term = new Terminal({
      fontFamily: '"JetBrains Mono Variable", ui-monospace, monospace',
      fontSize: matchMedia('(max-width: 600px)').matches ? 12 : 14,
      cursorBlink: true,
      scrollback: 5000,
      theme: { background: '#060607', foreground: '#ecebe6', cursor: '#ff2b2b', selectionBackground: 'rgba(255,43,43,.35)', red: '#ff2b2b' },
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.open(host!)
    fit.fit()
    term.focus()

    const t = await api.terminalTicket().catch((e) => ({ ok: false, message: String(e) }))
    if (!t.ok) {
      status = 'erro'
      message = t.message ?? 'Terminal indisponível'
      return
    }
    const base = conn.base || location.origin
    ws = new WebSocket(`${base.replace(/^http/, 'ws')}/api/terminal/ws?ticket=${encodeURIComponent(t.message!)}`)
    ws.binaryType = 'arraybuffer'
    const sendSize = () => ws?.readyState === WebSocket.OPEN && ws.send(JSON.stringify({ cols: term.cols, rows: term.rows }))
    ws.onopen = () => { status = 'conectado'; sendSize() }
    ws.onmessage = (e) => term.write(new Uint8Array(e.data as ArrayBuffer))
    ws.onclose = () => { if (status !== 'erro') status = 'encerrado' }
    ws.onerror = () => { status = 'erro'; message = 'Falha na conexão' }

    const sub = term.onData((d) => {
      if (ctrl && d.length === 1) {
        ctrl = false
        const c = d.toUpperCase().charCodeAt(0)
        if (c >= 64 && c <= 95) return send(String.fromCharCode(c - 64)) // Ctrl+letra
      }
      send(d)
    })
    const ro = new ResizeObserver(() => { fit.fit(); sendSize() })
    ro.observe(host!)
    cleanup = () => { ro.disconnect(); sub.dispose(); term.dispose() }
  })

  onDestroy(() => {
    ws?.close()
    cleanup?.()
  })
</script>

<div class="term-overlay" role="dialog" aria-label="Terminal">
  <header>
    <span class="mono"><span class="red">■</span> TERMINAL · <span class:red={status === 'erro'} class:dim={status !== 'conectado'}>{status.toUpperCase()}</span>{message ? ` · ${message}` : ''}</span>
    <button class="btn ghost" onclick={onclose}>✕ Fechar</button>
  </header>
  <div class="host" bind:this={host}></div>
  <div class="keys">
    <button class="btn mini" class:on={ctrl} onclick={() => (ctrl = !ctrl)}>CTRL</button>
    {#each KEYS as [k, seq]}<button class="btn mini" onclick={() => send(seq)}>{k}</button>{/each}
  </div>
</div>

<style>
  .term-overlay {
    position: fixed;
    inset: 0;
    z-index: 80;
    display: flex;
    flex-direction: column;
    background: #060607;
    padding: var(--safe-t) 0 var(--safe-b);
  }
  header { display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; border-bottom: 1px solid var(--line); }
  .host { flex: 1; min-height: 0; padding: 6px 4px 0 8px; }
  .keys { display: flex; gap: 4px; padding: 6px 8px; border-top: 1px solid var(--line); overflow-x: auto; }
  .keys .btn { height: 30px; min-width: 40px; justify-content: center; font-size: 11px; }
  @media (hover: hover) and (min-width: 820px) { .keys { display: none; } }
</style>
