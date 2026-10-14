<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { duration, int } from '../lib/format'

  const p = $derived(store.pihole)
  const busy = $derived(!!store.pending['ph'])
  const topMax = $derived(Math.max(1, ...(p?.topBlocked.map((t) => t.count) ?? [1])))
  const histMax = $derived(Math.max(1, ...(p?.history.map((h) => h.total) ?? [1])))

  const pause = (sec: number | undefined, label: string) =>
    act('ph', '', () => api.blocking(false, sec), `Bloqueio suspenso ${label}`)
  const resume = () => act('ph', '', () => api.blocking(true), 'Bloqueio reativado')

  let hover = $state<number | null>(null)
  const hp = $derived(hover != null ? p?.history[hover] : null)
  const hhmm = (t: number) => new Date(t * 1000).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' })
</script>

<Title index="04" code="DNS" label="Pi-hole" />

{#if p && !p.configured}
  <Panel label="NÃO CONFIGURADO">
    <p>Ative a integração em <code>/etc/bureau/bureau.json</code>:</p>
    <pre class="cfg">"PiHole": {'{'} "Enabled": true, "Url": "http://127.0.0.1", "Password": "app-password" {'}'}</pre>
    <p class="dim">Crie uma <i>app password</i> no Pi-hole em Settings → Web interface / API. Depois: <code>sudo systemctl restart bureau</code>.</p>
  </Panel>
{:else if p && !p.online}
  <Panel alert label="SEM SINAL">
    <p class="mono">{p.error ?? 'O Pi-hole não respondeu.'}</p>
  </Panel>
{:else if p}
  <Panel label="PROTEÇÃO" alert={!p.blocking} class="guard">
    <div class="guard-row">
      <div class="status">
        <Sq state={p.blocking ? 'on' : 'fail'} />
        <div>
          <div class="state-big" class:red={!p.blocking}>{p.blocking ? 'Ativa' : 'Suspensa'}</div>
          <div class="mono dim">{p.blocking ? 'Filtragem DNS em operação' : p.timer ? `Retorno automático em ${duration(p.timer)}` : 'Sem retorno automático'}</div>
        </div>
      </div>
      <div class="actions">
        {#if p.blocking}
          <button class="btn" class:busy disabled={busy} onclick={() => pause(300, 'por 5 min')}>Pausar 5 min</button>
          <button class="btn" disabled={busy} onclick={() => pause(1800, 'por 30 min')}>30 min</button>
          <button class="btn" disabled={busy} onclick={() => pause(3600, 'por 1 h')}>1 h</button>
          <button class="btn danger" disabled={busy} onclick={() => pause(undefined, 'indefinidamente')}>Indefinido</button>
        {:else}
          <button class="btn on" class:busy disabled={busy} onclick={resume}>Reativar proteção</button>
        {/if}
      </div>
    </div>
  </Panel>

  <div class="grid stats">
    <Panel label="BLOQUEADO / 24H"><div class="big red">{p.percent.toFixed(1)}<small>%</small></div></Panel>
    <Panel label="CONSULTAS"><div class="big">{int(p.total)}</div></Panel>
    <Panel label="INTERCEPTADAS"><div class="big">{int(p.blocked)}</div></Panel>
    <Panel label="CLIENTES ATIVOS"><div class="big">{p.clients}</div><div class="mono dim">{int(p.gravity)} DOMÍNIOS NA LISTA</div></Panel>
  </div>

  <div class="grid lower">
    <Panel label="ATIVIDADE · 24H">
      {#snippet aside()}
        <span class="mono dim num">{hp ? `${hhmm(hp.t)} · ${hp.total} CONSULTAS · ${hp.blocked} BLOQ.` : 'INTERVALOS DE 10 MIN'}</span>
      {/snippet}
      <svg class="hist" viewBox="0 0 {p.history.length * 4} 100" preserveAspectRatio="none" role="img" aria-label="Consultas DNS nas últimas 24 horas" onmouseleave={() => (hover = null)}>
        {#each p.history as h, i}
          {@const ht = (h.total / histMax) * 100}
          {@const hb = (h.blocked / histMax) * 100}
          <rect x={i * 4} y={100 - ht} width="3" height={ht} class="tot" class:hl={hover === i} />
          <rect x={i * 4} y={100 - hb} width="3" height={hb} class="blk" />
          <rect x={i * 4} y="0" width="4" height="100" class="hit" role="presentation" onmouseenter={() => (hover = i)} />
        {/each}
      </svg>
      <div class="legend mono dim"><span><i class="lg tot"></i>PERMITIDAS</span><span><i class="lg blk"></i>BLOQUEADAS</span></div>
    </Panel>

    <Panel label="MAIS BLOQUEADOS">
      <ol class="top">
        {#each p.topBlocked as t, i}
          <li>
            <span class="mono mute">{String(i + 1).padStart(2, '0')}</span>
            <span class="dom">{t.name}</span>
            <span class="mono num">{int(t.count)}</span>
            <i class="bar" style="width:{(t.count / topMax) * 100}%"></i>
          </li>
        {:else}
          <p class="empty">Sem dados.</p>
        {/each}
      </ol>
    </Panel>
  </div>
{:else}
  <p class="empty">Estabelecendo contato…</p>
{/if}

<style>
  :global(.guard) { margin-bottom: 14px; }
  .guard-row { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
  .status { display: flex; align-items: center; gap: 14px; }
  .state-big { font-stretch: 66%; font-weight: 800; font-size: 40px; line-height: 0.9; text-transform: uppercase; }

  .stats { grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); margin-bottom: 14px; }
  .lower { grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr); }

  .hist { display: block; width: 100%; height: 170px; }
  .hist .tot { fill: rgba(236, 235, 230, 0.32); }
  .hist .tot.hl { fill: var(--fg); }
  .hist .blk { fill: var(--red); pointer-events: none; }
  .hist .hit { fill: transparent; }
  .legend { display: flex; gap: 18px; margin-top: 10px; }
  .lg { display: inline-block; width: 8px; height: 8px; margin-right: 6px; }
  .lg.tot { background: rgba(236, 235, 230, 0.4); }
  .lg.blk { background: var(--red); }

  .top { list-style: none; display: flex; flex-direction: column; }
  .top li { position: relative; display: grid; grid-template-columns: 22px 1fr auto; gap: 10px; align-items: center; padding: 7px 0; border-bottom: 1px solid var(--line); }
  .dom { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 14px; }
  .bar { position: absolute; left: 0; bottom: -1px; height: 1px; background: var(--red); }

  .cfg { margin: 10px 0; padding: 10px; background: #060607; font-family: var(--mono); font-size: 12px; overflow: auto; }
  code { font-family: var(--mono); font-size: 13px; }

  @media (max-width: 900px) {
    .lower { grid-template-columns: 1fr; }
  }
</style>
