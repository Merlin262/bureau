<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { duration, int } from '../lib/format'
  import { onDestroy } from 'svelte'
  import type { DnsQuery, DohState } from '../lib/types'

  // ── Consultas ao vivo (só enquanto esta tela está aberta) ──
  let queries = $state.raw<DnsQuery[]>([])
  let paused = $state(false)
  let onlyBlocked = $state(false)
  let qfilter = $state('')
  async function loadQueries() {
    if (paused || document.hidden) return
    try { queries = (await api.queries()).sort((x, y) => y.ts - x.ts) } catch { /* offline */ }
  }
  loadQueries()
  const qtimer = setInterval(loadQueries, 2000)
  onDestroy(() => clearInterval(qtimer))
  const shownQ = $derived(queries.filter((q) =>
    (!onlyBlocked || q.blocked) &&
    (!qfilter || q.domain.includes(qfilter.toLowerCase()) || (q.clientName ?? q.client).toLowerCase().includes(qfilter.toLowerCase()))))
  const hms = (t: number) => new Date(t * 1000).toLocaleTimeString('pt-BR', { hour12: false })
  const STATUS: Record<string, string> = { GRAVITY: 'LISTA', REGEX: 'REGEX', DENYLIST: 'NEGADO', FORWARDED: 'ENCAMINHADO', CACHE: 'CACHE', CACHE_STALE: 'CACHE' }
  const domainAct = (q: DnsQuery) =>
    act(`dom:${q.domain}`, '', () => api.domain(q.domain, q.blocked ? 'allow' : 'deny'))

  const p = $derived(store.pihole)
  const busy = $derived(!!store.pending['ph'])
  const topMax = $derived(Math.max(1, ...(p?.topBlocked.map((t) => t.count) ?? [1])))
  const histMax = $derived(Math.max(1, ...(p?.history.map((h) => h.total) ?? [1])))

  const pause = (sec: number | undefined, label: string) =>
    act('ph', '', () => api.blocking(false, sec), `Bloqueio suspenso ${label}`)
  const resume = () => act('ph', '', () => api.blocking(true), 'Bloqueio reativado')

  // ── DNS seguro (DoH): aparelhos que usam um DNS próprio passam por fora do Pi-hole ──
  let doh = $state<DohState | null>(null)
  const loadDoh = async () => { try { doh = await api.doh() } catch { /* offline */ } }
  loadDoh()
  async function toggleDoh() {
    await act('doh', '', () => api.setDoh(!doh?.enabled))
    loadDoh()
  }

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

  {#if doh?.available}
    <Panel label="DNS SEGURO (DoH)" class="doh">
      <div class="doh-row">
        <div>
          <div class="name">{doh.enabled ? 'Bloqueado' : doh.blocked > 0 ? `Parcial (${doh.blocked}/${doh.total})` : 'Permitido'}</div>
          <p class="dim small">
            Chrome, Firefox, o "DNS privado" do Android e algumas TVs usam um DNS criptografado próprio e passam por fora do
            Pi-hole: nada aparece em "Acessos" e os anúncios não são bloqueados. Bloquear {doh.total} provedores conhecidos faz os
            aparelhos voltarem ao DNS da rede (serviços acessados direto por IP continuam passando).
          </p>
        </div>
        <button class="btn" class:on={doh.enabled} class:busy={!!store.pending['doh']} onclick={toggleDoh}>{doh.enabled ? 'Liberar' : 'Bloquear'}</button>
      </div>
    </Panel>
  {/if}

  <Panel label="CONSULTAS AO VIVO" class="live">
    {#snippet aside()}
      <button class="btn mini" class:on={onlyBlocked} onclick={() => (onlyBlocked = !onlyBlocked)}>Só bloqueadas</button>
      <button class="btn mini" class:on={paused} onclick={() => (paused = !paused)}>{paused ? '▶ Retomar' : '❚❚ Pausar'}</button>
    {/snippet}
    <input class="field qf" placeholder="Filtrar por domínio ou aparelho…" bind:value={qfilter} />
    <div class="qlist">
      {#each shownQ as q, i (q.ts + q.domain + q.client + i)}
        <div class="q" class:blk={q.blocked}>
          <span class="mono dim num">{hms(q.ts)}</span>
          <span class="mono cli">{q.clientName ?? q.client}</span>
          <span class="dom">{q.domain}</span>
          <span class="mono st">{q.blocked ? 'BLOQUEADO' : (STATUS[q.status] ?? q.status)}</span>
          <button class="btn mini" class:danger={!q.blocked} class:busy={!!store.pending[`dom:${q.domain}`]} onclick={() => domainAct(q)}>
            {q.blocked ? 'Liberar' : 'Bloquear'}
          </button>
        </div>
      {:else}
        <p class="empty">Nenhuma consulta.</p>
      {/each}
    </div>
  </Panel>
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

  :global(.live) { margin-top: 14px; }
  :global(.doh) { margin-top: 14px; }
  .doh-row { display: flex; justify-content: space-between; align-items: center; gap: 16px; }
  .doh-row .name { font-stretch: 80%; font-weight: 600; font-size: 17px; }
  .doh-row .small { font-size: 13px; margin-top: 4px; max-width: 760px; }
  @media (max-width: 560px) { .doh-row { flex-direction: column; align-items: flex-start; } }
  .qf { margin-bottom: 8px; max-width: 420px; }
  .qlist { max-height: 460px; overflow: auto; }
  .q { display: grid; grid-template-columns: 70px 130px minmax(0, 1fr) 100px 76px; gap: 10px; align-items: center; padding: 5px 0; border-bottom: 1px solid var(--line); font-size: 13px; }
  .q .cli { font-size: 11px; color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .q .st { font-size: 10px; color: var(--fg-mute); }
  .q.blk .st, .q.blk .dom { color: var(--red); }
  .q .btn.mini { height: 22px; padding: 0 6px; font-size: 9.5px; justify-content: center; }
  .btn.mini { height: 24px; padding: 0 8px; font-size: 10px; }
  @media (max-width: 900px) {
    .lower { grid-template-columns: 1fr; }
    .q { grid-template-columns: 62px minmax(0, 1fr) 70px; }
    .q .cli, .q .st { display: none; }
  }
</style>
