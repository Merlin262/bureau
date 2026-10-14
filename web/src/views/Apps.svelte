<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Sq from '../components/Sq.svelte'
  import Meter from '../components/Meter.svelte'
  import { store, act } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { int } from '../lib/format'
  import type { HaEntity } from '../lib/types'

  const ha = $derived(store.homeassistant)
  const jf = $derived(store.jellyfin)

  const TOGGLE = new Set(['light', 'switch', 'fan', 'input_boolean', 'automation', 'siren'])
  const DOMAIN: Record<string, string> = {
    light: 'luzes', switch: 'interruptores', sensor: 'sensores', binary_sensor: 'sensores binários', automation: 'automações',
    fan: 'ventiladores', climate: 'climatização', media_player: 'mídia', camera: 'câmeras', person: 'pessoas', script: 'scripts',
    input_boolean: 'chaves', cover: 'cortinas', device_tracker: 'rastreadores', weather: 'clima', update: 'atualizações',
  }

  const isOn = (e: HaEntity) => e.state === 'on' || e.state === 'open' || e.state === 'playing'
  const sqFor = (e: HaEntity) => (store.pending[`ha:${e.id}`] ? 'busy' : e.state === 'unavailable' ? 'fail' : isOn(e) ? 'on' : 'off')
  const stateText = (e: HaEntity) =>
    e.state === 'on' ? 'LIGADO' : e.state === 'off' ? 'DESLIGADO' : e.state === 'unavailable' ? 'INDISPONÍVEL'
    : `${e.state}${e.unit ? ` ${e.unit}` : ''}`.toUpperCase()

  const toggle = (e: HaEntity) =>
    act(`ha:${e.id}`, '', () => api.haToggle(e.id), `${e.name}: ${isOn(e) ? 'desligado' : 'ligado'}`)
</script>

<Title index="06" code="INTEGRAÇÕES" label="Apps" />

<div class="grid cols">
  <!-- ── Home Assistant ───────────────────────────── -->
  <Panel label="HOME ASSISTANT" alert={!!ha?.configured && !ha.online}>
    {#snippet aside()}
      {#if ha?.online}<span class="mono dim">{ha.location ?? ''} · v{ha.version}</span>{/if}
    {/snippet}
    {#if !ha}
      <p class="empty">Estabelecendo contato…</p>
    {:else if !ha.configured}
      <p class="dim">Não configurado. Em <code>/etc/bureau/bureau.json</code>:</p>
      <pre class="cfg">"HomeAssistant": {'{'} "Enabled": true, "Url": "http://127.0.0.1:8123", "Token": "…" {'}'}</pre>
      <p class="dim small">Token: Home Assistant → seu perfil → Segurança → Tokens de acesso de longa duração.</p>
    {:else if !ha.online}
      <p class="mono red">▲ {ha.error ?? 'Sem resposta'}</p>
    {:else}
      <div class="stats">
        <div><div class="mid num">{int(ha.total)}</div><span class="mono dim">ENTIDADES</span></div>
        <div><div class="mid num" class:red={ha.unavailable > 0}>{ha.unavailable}</div><span class="mono dim">INDISPONÍVEIS</span></div>
      </div>
      <div class="chips mono">
        {#each ha.domains as d}<span>{DOMAIN[d.name] ?? d.name} <b>{d.count}</b></span>{/each}
      </div>
      <div class="ents">
        {#each ha.entities as e (e.id)}
          <div class="ent">
            <Sq state={sqFor(e)} />
            <div class="info"><div class="name">{e.name}</div><div class="sub">{e.id}</div></div>
            <span class="mono st" class:dim={!isOn(e)}>{stateText(e)}</span>
            {#if TOGGLE.has(e.domain) && e.state !== 'unavailable'}
              <button class="btn" class:on={isOn(e)} class:busy={!!store.pending[`ha:${e.id}`]} onclick={() => toggle(e)}>
                {isOn(e) ? 'Desligar' : 'Ligar'}
              </button>
            {/if}
          </div>
        {:else}
          <p class="empty">Nenhuma luz ou interruptor. Escolha entidades em "Entities" na configuração.</p>
        {/each}
      </div>
    {/if}
  </Panel>

  <!-- ── Jellyfin ─────────────────────────────────── -->
  <Panel label="JELLYFIN" alert={!!jf?.configured && !jf.online}>
    {#snippet aside()}
      {#if jf?.online}<span class="mono dim">{jf.name} · v{jf.version}</span>{/if}
    {/snippet}
    {#if !jf}
      <p class="empty">Estabelecendo contato…</p>
    {:else if !jf.configured}
      <p class="dim">Não configurado. Em <code>/etc/bureau/bureau.json</code>:</p>
      <pre class="cfg">"Jellyfin": {'{'} "Enabled": true, "Url": "http://127.0.0.1:8096", "ApiKey": "…" {'}'}</pre>
      <p class="dim small">Chave: Jellyfin → Painel → Avançado → Chaves de API → +.</p>
    {:else if !jf.online}
      <p class="mono red">▲ {jf.error ?? 'Sem resposta'}</p>
    {:else}
      <div class="stats">
        {#each jf.library as l}
          <div><div class="mid num">{int(l.count)}</div><span class="mono dim">{l.name.toUpperCase()}</span></div>
        {/each}
      </div>
      <div class="mono dim sect">ASSISTINDO AGORA · {jf.sessions.length}</div>
      {#each jf.sessions as s}
        <div class="play">
          <div class="play-head">
            <span class="name">{s.title}</span>
            <span class="mono" class:dim={s.paused}>{s.paused ? '❚❚ PAUSADO' : '▶ REPRODUZINDO'}</span>
          </div>
          <div class="mono dim sub">{s.user} · {s.device ?? s.client ?? '—'}{s.transcoding ? ' · TRANSCODIFICANDO' : ''}</div>
          <Meter value={s.progress} hot={101} height={6} />
        </div>
      {:else}
        <p class="empty">Ninguém assistindo.</p>
      {/each}
    {/if}
  </Panel>
</div>

<style>
  .cols { grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); align-items: start; }
  .stats { display: flex; gap: 28px; flex-wrap: wrap; margin-bottom: 12px; }
  .mid { font-stretch: 70%; font-weight: 700; font-size: 34px; line-height: 1; }
  .chips { display: flex; flex-wrap: wrap; gap: 6px 14px; margin-bottom: 14px; font-size: 10px; color: var(--fg-dim); text-transform: uppercase; }
  .chips b { color: var(--fg); font-weight: 400; }
  .ents { display: flex; flex-direction: column; }
  .ent { display: grid; grid-template-columns: 10px minmax(0, 1fr) auto auto; align-items: center; gap: 12px; padding: 9px 0; border-bottom: 1px solid var(--line); }
  .ent .btn { height: 28px; min-width: 86px; justify-content: center; }
  .info { min-width: 0; }
  .name { font-stretch: 80%; font-weight: 600; font-size: 16px; line-height: 1.15; }
  .sub { font-family: var(--mono); font-size: 10.5px; color: var(--fg-mute); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .st { font-size: 11px; white-space: nowrap; }
  .sect { margin: 6px 0 8px; }
  .play { padding: 10px 0; border-bottom: 1px solid var(--line); display: flex; flex-direction: column; gap: 6px; }
  .play-head { display: flex; justify-content: space-between; gap: 10px; align-items: baseline; }
  .play-head .mono { font-size: 10px; white-space: nowrap; }
  .cfg { margin: 10px 0; padding: 10px; background: #060607; font-family: var(--mono); font-size: 12px; overflow: auto; }
  code { font-family: var(--mono); font-size: 12px; }
  .small { font-size: 13px; }
  @media (max-width: 560px) {
    .cols { grid-template-columns: 1fr; }
    .ent { grid-template-columns: 10px minmax(0, 1fr) auto; }
    .ent .st { display: none; }
  }
</style>
