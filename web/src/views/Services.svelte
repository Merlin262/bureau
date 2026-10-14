<script lang="ts">
  import Title from '../components/Title.svelte'
  import Sq from '../components/Sq.svelte'
  import Modal from '../components/Modal.svelte'
  import LogViewer from '../components/LogViewer.svelte'
  import { store, act, toast } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { actionLabel, bytes, since, shortUnit } from '../lib/format'
  import type { UnitEntry } from '../lib/types'

  const LABEL: Record<string, string> = {
    active: 'ATIVO', inactive: 'INATIVO', failed: 'FALHA', activating: 'INICIANDO',
    deactivating: 'PARANDO', reloading: 'RECARREGANDO', 'not-found': 'NÃO ENCONTRADO',
  }

  let open = $state<string | null>(null)
  let picking = $state(false)
  let available = $state<UnitEntry[]>([])
  let selected = $state<string[]>([])
  let query = $state('')

  const sqState = (active: string, key: string) =>
    store.pending[key] ? 'busy' : active === 'active' ? 'on' : active === 'failed' ? 'fail' : active.endsWith('ing') ? 'busy' : 'off'

  const filtered = $derived(
    available.filter((u) => !query || u.unit.toLowerCase().includes(query.toLowerCase()) || u.description.toLowerCase().includes(query.toLowerCase())),
  )

  async function openPicker() {
    picking = true
    selected = store.services?.map((s) => s.unit) ?? []
    try { available = await api.availableUnits() } catch (e) { toast(String(e), 'err') }
  }

  async function saveWatch() {
    picking = false
    await act('watch', '', () => api.watch(selected), 'Lista de monitoramento atualizada')
  }

  const toggle = (u: string) => (selected = selected.includes(u) ? selected.filter((x) => x !== u) : [...selected, u])

  function run(unit: string, action: string, verb: string) {
    act(`svc:${unit}`, action, () => api.service(unit, action), `${shortUnit(unit)}: ${verb}`)
  }
</script>

<Title index="02" code="SYSTEMD" label="Serviços">
  <button class="btn" onclick={openPicker}>+ Monitorar</button>
</Title>

<div class="rows">
  {#each store.services ?? [] as s (s.unit)}
    {@const key = `svc:${s.unit}`}
    {@const busy = store.pending[key]}
    <div class="row svc" class:alert={s.active === 'failed'}>
      <Sq state={sqState(s.active, key)} />
      <div class="info">
        <div class="name">{shortUnit(s.unit)}</div>
        <div class="sub">{s.description}</div>
      </div>
      <div class="state mono" class:red={s.active === 'failed'}>
        {busy ? `${actionLabel(busy)}…` : (LABEL[s.active] ?? s.active.toUpperCase())}
        <span class="dim">{s.active === 'active' ? `· ${since(s.since)}` : ''}</span>
      </div>
      <div class="mem mono dim num">{s.memory ? bytes(s.memory) : ''}</div>
      <div class="actions">
        {#if s.active === 'active'}
          <button class="btn" class:busy={busy === 'restart'} disabled={!!busy} onclick={() => run(s.unit, 'restart', 'reiniciado')}>Reiniciar</button>
          <button class="btn danger" class:busy={busy === 'stop'} disabled={!!busy} onclick={() => run(s.unit, 'stop', 'parado')}>Parar</button>
        {:else if s.active !== 'not-found'}
          <button class="btn" class:busy={busy === 'start'} disabled={!!busy} onclick={() => run(s.unit, 'start', 'iniciado')}>Iniciar</button>
        {/if}
        <button class="btn ghost" class:on={open === s.unit} onclick={() => (open = open === s.unit ? null : s.unit)}>Logs</button>
      </div>
    </div>
    {#if open === s.unit}
      <LogViewer load={() => api.serviceLogs(s.unit)} />
    {/if}
  {:else}
    <p class="empty">{store.services ? 'Nenhum serviço monitorado. Use "+ Monitorar".' : 'Estabelecendo contato…'}</p>
  {/each}
</div>

{#if picking}
  <Modal title="Monitorar serviços" onclose={() => (picking = false)}>
    <div class="pick-bar">
      <input class="field" placeholder="Buscar unidade…" bind:value={query} />
      <button class="btn on" onclick={saveWatch}>Salvar ({selected.length})</button>
    </div>
    <div class="pick">
      {#each filtered as u (u.unit)}
        <label class="opt" class:sel={selected.includes(u.unit)}>
          <input type="checkbox" checked={selected.includes(u.unit)} onchange={() => toggle(u.unit)} />
          <span class="box"></span>
          <span class="u">{shortUnit(u.unit)}</span>
          <span class="d">{u.description}</span>
          <span class="mono" class:red={u.active === 'failed'}>{LABEL[u.active] ?? u.active}</span>
        </label>
      {:else}
        <p class="empty">{available.length ? 'Nada encontrado.' : 'Consultando systemd…'}</p>
      {/each}
    </div>
  </Modal>
{/if}

<style>
  .svc { grid-template-columns: 10px minmax(0, 1fr) 190px 80px auto; }
  .info { min-width: 0; }
  .state { white-space: nowrap; }
  .mem { text-align: right; }

  .pick-bar { display: flex; gap: 8px; margin-bottom: 10px; position: sticky; top: -14px; background: var(--bg-2); padding: 4px 0 8px; z-index: 1; }
  .opt {
    display: grid;
    grid-template-columns: 14px 160px 1fr auto;
    align-items: center;
    gap: 12px;
    padding: 9px 8px;
    border-bottom: 1px solid var(--line);
    cursor: pointer;
  }
  .opt:hover { background: rgba(255, 255, 255, 0.04); }
  .opt input { position: absolute; opacity: 0; pointer-events: none; }
  .box { width: 12px; height: 12px; border: 1px solid var(--fg-dim); }
  .opt.sel .box { background: var(--fg); border-color: var(--fg); }
  .opt:has(input:focus-visible) { outline: 1px solid var(--red); }
  .u { font-stretch: 80%; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .d { color: var(--fg-dim); font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

  @media (max-width: 1100px) {
    .svc { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .mem { display: none; }
    .svc .actions { grid-column: 2 / -1; }
  }
  @media (max-width: 560px) {
    .svc { grid-template-columns: 10px minmax(0, 1fr); }
    .svc .state { grid-column: 2; }
    .opt { grid-template-columns: 14px 1fr auto; }
    .opt .d { display: none; }
  }
</style>
