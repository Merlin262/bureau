<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import Modal from '../components/Modal.svelte'
  import { store, act, toast } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { duration, int } from '../lib/format'
  import type { BlockState, DeviceInfo, MonitorDef } from '../lib/types'

  let query = $state('')
  let mac = $state('')
  let editing = $state<string | null>(null)
  let draft = $state('')

  const now = () => Date.now() / 1000
  const label = (d: DeviceInfo) => d.alias ?? d.name ?? (d.randomMac ? 'Celular / tablet?' : 'Desconhecido')
  const detail = (d: DeviceInfo) =>
    [d.alias && d.name ? d.name : null, d.vendor ?? (d.randomMac ? 'MAC aleatório (privacidade do Wi-Fi)' : null)]
      .filter(Boolean).join(' · ') || '—'
  const isNew = (d: DeviceInfo) => d.firstSeen > 0 && now() - d.firstSeen < 86400

  const list = $derived(
    (store.network ?? []).filter((d) => {
      if (!query) return true
      const q = query.toLowerCase()
      return [d.alias, d.name, d.ip, d.mac, d.vendor].some((v) => v?.toLowerCase().includes(q))
    }),
  )
  const online = $derived(store.network?.filter((d) => d.online).length ?? 0)
  const maxDns = $derived(Math.max(1, ...(store.network ?? []).map((d) => d.dnsQueries)))
  const piholeOn = $derived(!!store.pihole?.configured)
  const blockOf = (m: string) => store.blocks.find((b) => b.mac === m.toLowerCase())

  const wake = (m: string, name: string) => act(`wol:${m}`, '', () => api.wol(m), `Sinal de despertar enviado → ${name}`)

  // ── Apelidos ──
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

  // ── Bloqueio por aparelho e horário ──
  const DAYS = ['D', 'S', 'T', 'Q', 'Q', 'S', 'S']
  let blocking = $state<DeviceInfo | null>(null)
  let bManual = $state(false)
  let bSchedule = $state(false)
  let bDays = $state<number[]>([])
  let bStart = $state('23:00')
  let bEnd = $state('07:00')

  function openBlock(d: DeviceInfo) {
    const b = blockOf(d.mac)
    blocking = d
    bManual = b?.manual ?? true
    bSchedule = !!b?.start
    bDays = b?.days ?? []
    bStart = b?.start ?? '23:00'
    bEnd = b?.end ?? '07:00'
  }
  async function saveBlock() {
    const d = blocking!
    blocking = null
    await act(`blk:${d.mac}`, '', () => api.block({
      mac: d.mac, label: label(d), manual: bManual, days: bDays,
      start: bSchedule ? bStart : null, end: bSchedule ? bEnd : null,
    }), bManual ? `${label(d)}: internet bloqueada` : bSchedule ? `${label(d)}: horário salvo` : 'Bloqueio removido')
  }
  async function quickUnblock(d: DeviceInfo, b: BlockState) {
    await act(`blk:${d.mac}`, '', () => (b.start ? api.block({ ...b, manual: false }) : api.unblock(d.mac)), `${label(d)}: liberado`)
  }
  const blockText = (b: BlockState) =>
    b.active ? 'BLOQUEADO' : b.start ? `AGENDADO ${b.start}–${b.end}` : ''

  // ── Monitores (uptime) ──
  let editingMon = $state(false)
  let monDraft = $state<MonitorDef[]>([])
  async function openMonitors() {
    try { monDraft = (await api.monitors()).map((m) => ({ ...m })) } catch { monDraft = [] }
    editingMon = true
  }
  async function saveMonitors() {
    const defs = monDraft.filter((m) => m.target.trim())
    const r = await api.saveMonitors(defs)
    if (r.ok) { editingMon = false; toast(r.message ?? 'Salvo', 'ok') } else toast(r.message ?? 'Falha', 'err')
  }
  const since = (unix: number) => duration(now() - unix)
</script>

<Title index="05" code="LAN" label="Rede">
  <span class="mono dim count"><b class="num">{online}</b> ONLINE / {store.network?.length ?? 0} CONHECIDOS</span>
</Title>

<!-- ── Sites e serviços ─────────────────────────────── -->
<Panel label="SITES E SERVIÇOS" class="mon-panel">
  {#snippet aside()}<button class="btn mini" onclick={openMonitors}>Editar</button>{/snippet}
  {#each store.monitors ?? [] as m (m.id)}
    <div class="mon" class:alert={!m.up}>
      <Sq state={m.up ? 'on' : 'fail'} />
      <div class="info">
        <div class="name">{m.name}</div>
        <div class="sub">{m.up ? (m.builtin ? 'Conexão de casa com a internet' : m.target) : `${m.error ?? 'Sem resposta'} · há ${since(m.since)}`}</div>
      </div>
      <div class="bars" aria-hidden="true">
        {#each m.recent as r}<i class:down={r < 0} style="height:{r < 0 ? 100 : Math.max(12, Math.min(100, r / 6))}%"></i>{/each}
      </div>
      <span class="mono num lat">{m.latencyMs != null ? `${m.latencyMs} ms` : '—'}</span>
      <span class="mono num up" class:red={m.uptime24h < 99}>{m.uptime24h.toFixed(m.uptime24h === 100 ? 0 : 2)}%</span>
    </div>
  {:else}
    <p class="empty">Iniciando verificações…</p>
  {/each}
  <p class="hint mono dim">TESTE A CADA MINUTO · BARRAS = ÚLTIMA HORA · % = DISPONIBILIDADE EM 24 H</p>
</Panel>

<div class="bar">
  <input class="field" placeholder="Filtrar por nome, IP, MAC ou fabricante…" bind:value={query} />
</div>

<div class="rows">
  {#each list as d (d.mac)}
    {@const b = blockOf(d.mac)}
    <div class="row dev" class:alert={b?.active}>
      <Sq state={b?.active ? 'fail' : d.online ? 'on' : 'off'} />
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
          {#if isNew(d)}<span class="tag mono new">NOVO</span>{/if}
          {#if d.randomMac}<span class="tag mono">ALEATÓRIO</span>{/if}
          {#if b && blockText(b)}<span class="tag mono blk">{blockText(b)}</span>{/if}
          {detail(d)}
        </div>
        {#if d.dnsQueries > 0}
          <div class="act" title="Consultas DNS nas últimas 24 h (Pi-hole)">
            <span class="track"><i style="width:{(d.dnsQueries / maxDns) * 100}%"></i></span><span class="mono">{int(d.dnsQueries)} consultas/24h</span>
          </div>
        {/if}
      </div>
      <div class="mono num">{d.ip}</div>
      <div class="mono dim mac">{d.mac}</div>
      <div class="actions">
        {#if b?.active}
          <button class="btn danger" class:busy={!!store.pending[`blk:${d.mac}`]} onclick={() => quickUnblock(d, b)}>Liberar</button>
        {/if}
        {#if piholeOn}
          <button class="btn ghost" onclick={() => openBlock(d)}>{b ? 'Regras' : 'Bloquear'}</button>
        {/if}
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

<p class="hint mono dim">TOQUE NO NOME PARA DAR UM APELIDO · "ALEATÓRIO" = CELULAR/TABLET COM MAC PRIVADO · "NOVO" = VISTO PELA 1ª VEZ NAS ÚLTIMAS 24 H</p>

<Panel label="LIGAR REMOTAMENTE (WAKE-ON-LAN)" class="wol">
  <p class="dim small wol-help">Envia um "pacote mágico" que liga um computador desligado da rede — útil para PCs ligados por cabo com Wake-on-LAN ativado na BIOS.</p>
  <form class="wol-form" onsubmit={(e) => { e.preventDefault(); if (mac) wake(mac, mac) }}>
    <input class="field" placeholder="AA:BB:CC:DD:EE:FF" bind:value={mac} autocomplete="off" spellcheck="false" />
    <button class="btn" type="submit" disabled={!mac}>Enviar pacote</button>
  </form>
</Panel>

{#if blocking}
  <Modal title={`Internet · ${label(blocking)}`} onclose={() => (blocking = null)}>
    <p class="dim small">O bloqueio é feito pelo Pi-hole (DNS): funciona se o aparelho usa o Pi-hole e não tem "DNS privado/seguro" ligado.</p>
    <label class="opt"><input type="checkbox" bind:checked={bManual} /><span class="box"></span> <b>Bloquear agora</b> <span class="dim">(até liberar)</span></label>
    <label class="opt"><input type="checkbox" bind:checked={bSchedule} /><span class="box"></span> <b>Bloquear por horário</b></label>
    {#if bSchedule}
      <div class="sched">
        <div class="days">
          {#each DAYS as dl, i}
            <button class="btn day" class:on={bDays.includes(i)} onclick={() => (bDays = bDays.includes(i) ? bDays.filter((x) => x !== i) : [...bDays, i])}>{dl}</button>
          {/each}
          <span class="mono dim">{bDays.length === 0 ? 'TODOS OS DIAS' : ''}</span>
        </div>
        <div class="times">
          <label class="mono dim">DAS <input class="field time" type="time" bind:value={bStart} /></label>
          <label class="mono dim">ATÉ <input class="field time" type="time" bind:value={bEnd} /></label>
        </div>
      </div>
    {/if}
    <div class="actions modal-actions">
      <button class="btn on" onclick={saveBlock}>Salvar</button>
      {#if blockOf(blocking.mac)}
        <button class="btn danger" onclick={() => { const d = blocking!; blocking = null; act(`blk:${d.mac}`, '', () => api.unblock(d.mac)) }}>Remover regras</button>
      {/if}
    </div>
  </Modal>
{/if}

{#if editingMon}
  <Modal title="Sites e serviços" onclose={() => (editingMon = false)}>
    <p class="dim small">Endereço <code>https://site</code> (testa HTTP) ou <code>host:porta</code> (testa TCP, ex.: <code>192.168.100.1:80</code>). A internet já é monitorada.</p>
    {#each monDraft as m, i}
      <div class="mon-edit">
        <input class="field" placeholder="Nome" bind:value={m.name} />
        <input class="field" placeholder="https://bureau.merlin.api.br" bind:value={m.target} autocapitalize="off" spellcheck="false" />
        <button class="btn ghost danger" onclick={() => (monDraft = monDraft.filter((_, j) => j !== i))} aria-label="Remover">✕</button>
      </div>
    {/each}
    <div class="actions modal-actions">
      <button class="btn" onclick={() => (monDraft = [...monDraft, { id: '', name: '', target: '' }])}>+ Adicionar</button>
      <button class="btn on" onclick={saveMonitors}>Salvar</button>
    </div>
  </Modal>
{/if}

<style>
  .count b { color: var(--fg); font-size: 14px; }
  :global(.mon-panel) { margin-bottom: 18px; }
  .mon { display: grid; grid-template-columns: 10px minmax(0, 1fr) 140px 64px 60px; align-items: center; gap: 12px; padding: 9px 0; border-bottom: 1px solid var(--line); }
  .mon.alert .name { color: var(--red); }
  .bars { display: flex; align-items: flex-end; gap: 1px; height: 22px; }
  .bars i { flex: 1; min-width: 1px; background: rgba(236, 235, 230, 0.45); }
  .bars i.down { background: var(--red); }
  .lat, .up { text-align: right; font-size: 11px; }
  .bar { margin-bottom: 10px; max-width: 520px; }
  .dev { grid-template-columns: 10px minmax(0, 1fr) 130px 160px auto; }
  .info { min-width: 0; }
  .name { font-stretch: 80%; font-weight: 600; font-size: 17px; line-height: 1.15; }
  .sub { font-family: var(--mono); font-size: 11px; color: var(--fg-dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .editable { display: inline-flex; align-items: baseline; gap: 8px; max-width: 100%; text-align: left; }
  .pen { font-size: 11px; color: var(--fg-mute); opacity: 0; transition: opacity 80ms; }
  .editable:hover .pen, .editable:focus-visible .pen { opacity: 1; }
  .rename { height: 30px; max-width: 320px; font-family: var(--sans); font-size: 16px; letter-spacing: 0; }
  .tag { display: inline-block; margin-right: 6px; padding: 0 4px; border: 1px solid var(--fg-mute); font-size: 9px; color: var(--fg-dim); vertical-align: 1px; }
  .tag.new { border-color: var(--fg); color: var(--bg); background: var(--fg); }
  .tag.blk { border-color: var(--red); color: var(--red); }
  .act { display: flex; align-items: center; gap: 8px; margin-top: 4px; }
  .track { width: 90px; height: 3px; background: var(--line); flex: none; }
  .track i { display: block; height: 100%; background: var(--fg-dim); }
  .act .mono { font-size: 9.5px; color: var(--fg-mute); }
  .hint { margin-top: 10px; font-size: 10px; }
  :global(.wol) { margin-top: 24px; max-width: 520px; }
  .wol-form { display: flex; gap: 8px; }
  .small { font-size: 13px; margin-bottom: 10px; }
  .opt { display: flex; align-items: center; gap: 10px; padding: 10px 0; cursor: pointer; }
  .opt input { position: absolute; opacity: 0; pointer-events: none; }
  .box { width: 14px; height: 14px; border: 1px solid var(--fg-dim); flex: none; }
  .opt:has(input:checked) .box { background: var(--fg); border-color: var(--fg); }
  .opt:has(input:focus-visible) { outline: 1px solid var(--red); }
  .sched { padding: 4px 0 10px 24px; display: flex; flex-direction: column; gap: 10px; }
  .days { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; }
  .day { width: 34px; justify-content: center; padding: 0; }
  .times { display: flex; gap: 14px; flex-wrap: wrap; }
  .times label { display: flex; align-items: center; gap: 8px; }
  .time { width: 120px; height: 34px; }
  .modal-actions { margin-top: 14px; }
  .mon-edit { display: grid; grid-template-columns: 1fr 1.6fr auto; gap: 6px; margin-bottom: 6px; }
  code { font-family: var(--mono); font-size: 12px; }

  @media (hover: none) { .pen { opacity: 1; } }
  @media (max-width: 900px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto auto; }
    .mac { display: none; }
    .mon { grid-template-columns: 10px minmax(0, 1fr) 80px 56px; }
    .mon .lat { display: none; }
  }
  @media (max-width: 560px) {
    .dev { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .dev .actions { grid-column: 2 / -1; }
    .mon { grid-template-columns: 10px minmax(0, 1fr) 56px; }
    .mon .bars { display: none; }
    .mon-edit { grid-template-columns: 1fr auto; }
    .mon-edit input:first-child { grid-column: 1 / -1; }
  }
</style>
