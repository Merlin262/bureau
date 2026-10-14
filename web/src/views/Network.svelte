<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'

  let query = $state('')
  let mac = $state('')

  const list = $derived(
    (store.network ?? []).filter((d) => {
      if (!query) return true
      const q = query.toLowerCase()
      return [d.name, d.ip, d.mac, d.vendor].some((v) => v?.toLowerCase().includes(q))
    }),
  )
  const online = $derived(store.network?.filter((d) => d.online).length ?? 0)

  const wake = (m: string, label: string) => act(`wol:${m}`, '', () => api.wol(m), `Sinal de despertar enviado → ${label}`)
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
        <div class="name">{d.name ?? 'Desconhecido'}</div>
        <div class="sub">{d.vendor ?? '—'}</div>
      </div>
      <div class="mono num">{d.ip}</div>
      <div class="mono dim mac">{d.mac}</div>
      <div class="actions">
        <button class="btn ghost" class:busy={!!store.pending[`wol:${d.mac}`]} onclick={() => wake(d.mac, d.name ?? d.ip)}>Despertar</button>
      </div>
    </div>
  {:else}
    <p class="empty">{store.network ? 'Nenhum dispositivo.' : 'Varrendo a rede…'}</p>
  {/each}
</div>

<Panel label="WAKE-ON-LAN MANUAL" class="wol">
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
  :global(.wol) { margin-top: 24px; max-width: 520px; }
  .wol-form { display: flex; gap: 8px; }

  @media (max-width: 900px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto auto; }
    .mac { display: none; }
  }
  @media (max-width: 560px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .dev .actions { display: none; }
  }
</style>
