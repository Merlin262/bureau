<script lang="ts">
  import Panel from '../components/Panel.svelte'
  import Title from '../components/Title.svelte'
  import Meter from '../components/Meter.svelte'
  import Spark from '../components/Spark.svelte'
  import Sq from '../components/Sq.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { go, type ViewId } from '../lib/router.svelte'
  import { bytes, bytesParts, duration, int, pct, rate, shortUnit } from '../lib/format'

  const s = $derived(store.sys)
  const memPct = $derived(s ? pct(s.memUsed, s.memTotal) : 0)
  const failed = $derived(store.services?.filter((x) => x.active === 'failed') ?? [])
  const svcActive = $derived(store.services?.filter((x) => x.active === 'active').length ?? 0)
  const running = $derived(store.containers?.items.filter((c) => c.state === 'running').length ?? 0)
  const online = $derived(store.network?.filter((d) => d.online).length ?? 0)
  const onBattery = $derived(s?.battery?.status === 'Discharging')
  const ph = $derived(store.pihole)

  const when = (ts: number) =>
    new Date(ts).toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })

  const alerts = $derived.by(() => {
    const a: string[] = []
    if (onBattery) a.push(`Energia externa interrompida — bateria em ${s!.battery!.percent}%`)
    if (failed.length) a.push(`${failed.length} serviço(s) em falha: ${failed.map((f) => shortUnit(f.unit)).join(', ')}`)
    if (ph?.configured && !ph.online) a.push('Pi-hole sem resposta')
    if (ph?.online && !ph.blocking) a.push('Bloqueio de anúncios suspenso')
    if ((s?.tempC ?? 0) >= 85) a.push(`Temperatura crítica: ${s!.tempC}°C`)
    return a
  })
</script>

<Title index="01" code="VISÃO GERAL" label="Painel" />

{#if alerts.length}
  <Panel alert label="ALERTA" class="alerts">
    {#each alerts as a}
      <p class="alert-line"><span class="red blink">▲</span> {a}</p>
    {/each}
  </Panel>
{/if}

<div class="grid metrics">
  <Panel label="PROCESSADOR">
    {#snippet aside()}<span class="mono dim num">LOAD {s?.load.map((l) => l.toFixed(2)).join(' ') ?? '—'}</span>{/snippet}
    <div class="big">{s ? Math.round(s.cpu) : '—'}<small>%</small></div>
    <Spark values={store.hist.cpu} max={100} red={(s?.cpu ?? 0) > 85} />
    <div class="cores">
      {#each s?.cores ?? [] as c, i}
        <div class="core"><span class="mono mute">C{i}</span><Meter value={c} height={6} /></div>
      {/each}
    </div>
  </Panel>

  <Panel label="MEMÓRIA">
    {#snippet aside()}<span class="mono dim num">{s ? `${bytes(s.memUsed)} / ${bytes(s.memTotal)}` : '—'}</span>{/snippet}
    <div class="big">{Math.round(memPct)}<small>%</small></div>
    <Spark values={store.hist.mem} max={100} red={memPct > 90} />
    {#if s?.swapTotal}
      <div class="kv"><span class="mono mute">SWAP</span><Meter value={pct(s.swapUsed, s.swapTotal)} height={6} /><span class="mono dim num">{bytes(s.swapUsed)}</span></div>
    {/if}
  </Panel>

  <Panel label="TRÁFEGO">
    {#snippet aside()}<span class="mono dim">INTERFACES FÍSICAS</span>{/snippet}
    {@const rx = bytesParts(s?.rxRate ?? 0)}
    <div class="big">{rx[0]}<small>{rx[1]}/S ↓</small></div>
    <Spark values={store.hist.rx} />
    <div class="kv"><span class="mono mute">ENVIO ↑</span><span class="mono num">{rate(s?.txRate ?? 0)}</span></div>
    <Spark values={store.hist.tx} height={22} />
  </Panel>

  <Panel label="ESTADO FÍSICO">
    <div class="phys">
      <div>
        <span class="mono mute">TEMPERATURA</span>
        <div class="mid num" class:red={(s?.tempC ?? 0) >= 80}>{s?.tempC != null ? `${Math.round(s.tempC)}°C` : '—'}</div>
      </div>
      <div>
        <span class="mono mute">EM OPERAÇÃO</span>
        <div class="mid num">{s ? duration(s.uptime) : '—'}</div>
      </div>
      <div class="wide">
        <span class="mono mute">ENERGIA DE RESERVA {s?.battery ? `· ${s.battery.status.toUpperCase()}` : ''}</span>
        {#if s?.battery}
          <div class="kv"><Meter value={s.battery.percent} hot={101} /><span class="mono num" class:red={onBattery}>{s.battery.percent}%</span></div>
        {:else}
          <div class="mono dim">SEM BATERIA</div>
        {/if}
      </div>
    </div>
  </Panel>
</div>

<div class="grid tiles">
  <button class="tile" onclick={() => go('servicos')}>
    <span class="mono dim">02 · SERVIÇOS</span>
    <span class="mid num">{svcActive}<small>/{store.services?.length ?? 0}</small></span>
    <span class="mono" class:red={failed.length > 0}>{failed.length ? `${failed.length} EM FALHA` : 'NOMINAL'}</span>
  </button>
  <button class="tile" onclick={() => go('docker')}>
    <span class="mono dim">03 · CONTÊINERES</span>
    <span class="mid num">{running}<small>/{store.containers?.items.length ?? 0}</small></span>
    <span class="mono dim">{store.containers?.available === false ? 'INDISPONÍVEL' : 'EM EXECUÇÃO'}</span>
  </button>
  <div class="tile">
    <button class="cover" aria-label="Abrir Pi-hole" onclick={() => go('pihole')}></button>
    <span class="mono dim">04 · PI-HOLE</span>
    <span class="mid num">{ph?.online ? ph.percent.toFixed(1) : '—'}<small>% BLOQ.</small></span>
    {#if ph?.online}
      <span class="row-inline">
        <Sq state={ph.blocking ? 'on' : 'fail'} />
        <span class="mono">{ph.blocking ? 'PROTEÇÃO ATIVA' : 'SUSPENSA'}</span>
        <button
          class="btn mini"
          class:busy={!!store.pending['ph']}
          onclick={() => act('ph', '', () => api.blocking(!ph.blocking, ph.blocking ? 300 : undefined), ph.blocking ? 'Bloqueio suspenso por 5 min' : 'Bloqueio reativado')}
        >{ph.blocking ? 'PAUSAR 5M' : 'REATIVAR'}</button>
      </span>
    {:else}
      <span class="mono dim">{ph?.configured ? 'SEM SINAL' : 'NÃO CONFIGURADO'}</span>
    {/if}
  </div>
  <button class="tile" onclick={() => go('rede')}>
    <span class="mono dim">05 · REDE</span>
    <span class="mid num">{online}<small> ONLINE</small></span>
    <span class="mono dim">{int(ph?.total ?? 0)} CONSULTAS DNS / 24H</span>
  </button>
</div>

<section class="log">
  <header class="log-head mono"><span class="red">■</span> REGISTRO DE OCORRÊNCIAS <span class="rule"></span></header>
  {#each store.alerts.slice(0, 8) as a (a.ts + a.title)}
    <button class="entry" onclick={() => go(a.view as ViewId)}>
      <span class="mono dim num">{when(a.ts)}</span>
      <Sq state={a.level === 'ok' ? 'on' : a.level === 'crit' ? 'fail' : 'busy'} />
      <span class="what"><b>{a.title}</b> <span class="dim">— {a.body}</span></span>
    </button>
  {:else}
    <p class="empty">Nenhuma ocorrência registrada.</p>
  {/each}
</section>

<style>
  .log { margin-top: 28px; }
  .log-head { display: flex; align-items: center; gap: 10px; margin-bottom: 6px; color: var(--fg-dim); }
  .log-head .rule { flex: 1; height: 1px; background: var(--line); }
  .entry {
    display: grid;
    grid-template-columns: 96px 10px minmax(0, 1fr);
    align-items: center;
    gap: 12px;
    width: 100%;
    padding: 9px 4px;
    border-bottom: 1px solid var(--line);
    text-align: left;
    transition: background 80ms;
  }
  .entry:hover { background: rgba(255, 255, 255, 0.03); }
  .what { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 14px; }
  .what b { font-stretch: 80%; font-weight: 600; font-size: 15px; }
  @media (max-width: 560px) {
    .entry { grid-template-columns: 10px minmax(0, 1fr); }
    .entry > .num { grid-column: 1 / -1; }
    .what { white-space: normal; }
  }
  :global(.alerts) { margin-bottom: 14px; }
  .alert-line { font-family: var(--mono); font-size: 13px; letter-spacing: 0.03em; text-transform: uppercase; padding: 2px 0; }

  .metrics { grid-template-columns: repeat(auto-fit, minmax(235px, 1fr)); margin-bottom: 14px; }
  .metrics :global(.big) { margin-bottom: 10px; }

  .cores { display: grid; grid-template-columns: repeat(auto-fill, minmax(90px, 1fr)); gap: 6px 12px; margin-top: 12px; }
  .core { display: grid; grid-template-columns: 22px 1fr; align-items: center; gap: 6px; }

  .kv { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 10px; margin-top: 10px; }
  .kv:has(> :global(.meter):first-child) { grid-template-columns: 1fr auto; }

  .mid { font-stretch: 70%; font-weight: 700; font-size: 34px; line-height: 1; margin-top: 4px; }
  .mid small { font-family: var(--mono); font-size: 12px; font-weight: 400; color: var(--fg-dim); margin-left: 2px; }

  .phys { display: grid; grid-template-columns: 1fr 1fr; gap: 18px 14px; }
  .phys .wide { grid-column: 1 / -1; }

  .tiles { grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); }
  .tile {
    position: relative;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 6px;
    padding: 16px;
    border: 1px solid var(--line);
    text-align: left;
    transition: background 90ms, border-color 90ms;
  }
  button.tile:hover, .tile:has(.cover:hover) { border-color: var(--fg); background: rgba(255, 255, 255, 0.04); }
  .cover { position: absolute; inset: 0; }
  .row-inline { position: relative; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .btn.mini { height: 24px; padding: 0 8px; font-size: 10px; }
</style>
