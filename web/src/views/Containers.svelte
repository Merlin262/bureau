<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import LogViewer from '../components/LogViewer.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { actionLabel } from '../lib/format'

  let open = $state<string | null>(null)
  const c = $derived(store.containers)

  const sqState = (state: string, key: string) =>
    store.pending[key] ? 'busy' : state === 'running' ? 'on' : state === 'restarting' ? 'busy' : state === 'dead' ? 'fail' : 'off'

  function run(id: string, name: string, action: string, verb: string) {
    act(`ctr:${id}`, action, () => api.container(id, action), `${name}: ${verb}`)
  }
</script>

<Title index="03" code="DOCKER" label="Contêineres" />

{#if c && !c.available}
  <Panel alert label="DOCKER INDISPONÍVEL">
    <p class="mono">{c.error}</p>
    <p class="dim hint">Verifique se o Docker está instalado e se o usuário <b>bureau</b> pertence ao grupo <b>docker</b>.</p>
  </Panel>
{:else}
  <div class="rows">
    {#each c?.items ?? [] as k (k.id)}
      {@const key = `ctr:${k.id}`}
      {@const busy = store.pending[key]}
      <div class="row ctr" class:alert={k.state === 'dead'}>
        <Sq state={sqState(k.state, key)} />
        <div class="info">
          <div class="name">{k.name}</div>
          <div class="sub">{k.image}</div>
        </div>
        <div class="mono state">{busy ? `${actionLabel(busy)}…` : k.status.toUpperCase()}</div>
        <div class="mono dim ports">{k.ports}</div>
        <div class="actions">
          {#if k.state === 'running'}
            <button class="btn" class:busy={busy === 'restart'} disabled={!!busy} onclick={() => run(k.id, k.name, 'restart', 'reiniciado')}>Reiniciar</button>
            <button class="btn danger" class:busy={busy === 'stop'} disabled={!!busy} onclick={() => run(k.id, k.name, 'stop', 'parado')}>Parar</button>
          {:else}
            <button class="btn" class:busy={busy === 'start'} disabled={!!busy} onclick={() => run(k.id, k.name, 'start', 'iniciado')}>Iniciar</button>
          {/if}
          <button class="btn ghost" class:on={open === k.id} onclick={() => (open = open === k.id ? null : k.id)}>Logs</button>
        </div>
      </div>
      {#if open === k.id}
        <LogViewer load={() => api.containerLogs(k.id)} />
      {/if}
    {:else}
      <p class="empty">{c ? 'Nenhum contêiner.' : 'Estabelecendo contato…'}</p>
    {/each}
  </div>
{/if}

<style>
  .ctr { grid-template-columns: 10px minmax(0, 1fr) 190px 150px auto; }
  .info { min-width: 0; }
  .state, .ports { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .hint { margin-top: 8px; font-size: 14px; }

  @media (max-width: 1100px) {
    .ctr { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .ports { display: none; }
    .ctr .actions { grid-column: 2 / -1; }
  }
  @media (max-width: 560px) {
    .ctr { grid-template-columns: 10px minmax(0, 1fr); }
    .ctr .state { grid-column: 2; }
  }
</style>
