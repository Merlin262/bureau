<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import type { DeviceInfo } from '../lib/types'

  let query = $state('')
  let mac = $state('')
  let editing = $state<string | null>(null)
  let draft = $state('')

  const label = (d: DeviceInfo) => d.alias ?? d.name ?? (d.randomMac ? 'Celular / tablet?' : 'Desconhecido')
  const detail = (d: DeviceInfo) =>
    [d.alias && d.name ? d.name : null, d.vendor ?? (d.randomMac ? 'MAC aleatório (privacidade do Wi-Fi)' : null)]
      .filter(Boolean).join(' · ') || '—'

  const list = $derived(
    (store.network ?? []).filter((d) => {
      if (!query) return true
      const q = query.toLowerCase()
      return [d.alias, d.name, d.ip, d.mac, d.vendor].some((v) => v?.toLowerCase().includes(q))
    }),
  )
  const online = $derived(store.network?.filter((d) => d.online).length ?? 0)

  const wake = (m: string, name: string) => act(`wol:${m}`, '', () => api.wol(m), `Sinal de despertar enviado → ${name}`)

  function edit(d: DeviceInfo) {
    editing = d.mac
    draft = d.alias ?? ''
  }

  async function save(d: DeviceInfo) {
    if (editing !== d.mac) return
    editing = null
    const name = draft.trim()
    if (name === (d.alias ?? '')) return
    await act(`alias:${d.mac}`, '', () => api.alias(d.mac, name), name ? `Renomeado: ${name}` : 'Apelido removido')
  }

  function onkey(e: KeyboardEvent, d: DeviceInfo) {
    if (e.key === 'Enter') save(d)
    else if (e.key === 'Escape') { e.stopPropagation(); editing = null }
  }

  const focus = (el: HTMLInputElement) => { el.focus(); el.select() }
</script>

<Title index="05" code="LAN" label="Rede">
  <span class="mono dim count"><b class="num">{online}</b> ONLINE / {store.network?.length ?? 0} CONHECIDOS</span>
</Title>

<div class="bar">
  <input class="field" placeholder="Filtrar por nome, IP, MAC ou fabricante…" bind:value={query} />
</div>

<div class="rows">
  {#each list as d (d.mac)}
    <div class="row dev">
      <Sq state={d.online ? 'on' : 'off'} />
      <div class="info">
        {#if editing === d.mac}
          <input class="field rename" placeholder="Apelido (vazio remove)" maxlength="40" bind:value={draft}
            onkeydown={(e) => onkey(e, d)} onblur={() => save(d)} use:focus />
        {:else}
          <button class="name editable" aria-label={`${label(d)} — renomear`} onclick={() => edit(d)}>
            {label(d)}<span class="pen mono">✎</span>
          </button>
        {/if}
        <div class="sub">
          {#if d.randomMac}<span class="tag mono">ALEATÓRIO</span>{/if}
          {detail(d)}
        </div>
      </div>
      <div class="mono num">{d.ip}</div>
      <div class="mono dim mac">{d.mac}</div>
      <div class="actions">
        {#if !d.online && !d.randomMac}
          <button class="btn ghost" title="Wake-on-LAN: liga remotamente um PC desligado (precisa estar ativado na BIOS/placa de rede)"
            class:busy={!!store.pending[`wol:${d.mac}`]} onclick={() => wake(d.mac, label(d))}>Ligar (WoL)</button>
        {/if}
      </div>
    </div>
  {:else}
    <p class="empty">{store.network ? 'Nenhum dispositivo.' : 'Varrendo a rede…'}</p>
  {/each}
</div>

<p class="hint mono dim">TOQUE NO NOME PARA DAR UM APELIDO · "ALEATÓRIO" = CELULAR/TABLET COM MAC PRIVADO · "LIGAR (WOL)" APARECE EM APARELHOS DESLIGADOS</p>

<Panel label="LIGAR REMOTAMENTE (WAKE-ON-LAN)" class="wol">
  <p class="dim small wol-help">Envia um "pacote mágico" que liga um computador desligado da rede — útil para PCs ligados por cabo com Wake-on-LAN ativado na BIOS.</p>
  <form class="wol-form" onsubmit={(e) => { e.preventDefault(); if (mac) wake(mac, mac) }}>
    <input class="field" placeholder="AA:BB:CC:DD:EE:FF" bind:value={mac} autocomplete="off" spellcheck="false" />
    <button class="btn" type="submit" disabled={!mac}>Enviar pacote</button>
  </form>
</Panel>

<style>
  .count b { color: var(--fg); font-size: 14px; }
  .bar { margin-bottom: 10px; max-width: 520px; }
  .dev { grid-template-columns: 10px minmax(0, 1fr) 130px 160px auto; }
  .info { min-width: 0; }
  .editable { display: inline-flex; align-items: baseline; gap: 8px; max-width: 100%; text-align: left; }
  .pen { font-size: 11px; color: var(--fg-mute); opacity: 0; transition: opacity 80ms; }
  .editable:hover .pen, .editable:focus-visible .pen { opacity: 1; }
  .rename { height: 30px; max-width: 320px; font-family: var(--sans); font-size: 16px; letter-spacing: 0; }
  .tag { display: inline-block; margin-right: 6px; padding: 0 4px; border: 1px solid var(--fg-mute); font-size: 9px; color: var(--fg-dim); vertical-align: 1px; }
  .hint { margin-top: 10px; font-size: 10px; }
  :global(.wol) { margin-top: 24px; max-width: 520px; }
  .wol-form { display: flex; gap: 8px; }
  .wol-help { font-size: 13px; margin-bottom: 10px; }

  @media (hover: none) { .pen { opacity: 1; } }
  @media (max-width: 900px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto auto; }
    .mac { display: none; }
  }
  @media (max-width: 560px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .dev .actions { display: none; }
  }
</style>
