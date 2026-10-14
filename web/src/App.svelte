<script lang="ts">
  import { onMount } from 'svelte'
  import Login from './views/Login.svelte'
  import Dashboard from './views/Dashboard.svelte'
  import Services from './views/Services.svelte'
  import Containers from './views/Containers.svelte'
  import PiHole from './views/PiHole.svelte'
  import Network from './views/Network.svelte'
  import System from './views/System.svelte'
  import Apps from './views/Apps.svelte'
  import Toasts from './components/Toasts.svelte'
  import LockScreen from './components/LockScreen.svelte'
  import { initBioLock } from './lib/biometric'
  import { store, connect, disconnect } from './lib/store.svelte'
  import { router, views, go, step, type ViewId } from './lib/router.svelte'
  import { api } from './lib/api'
  import { sfx } from './lib/sfx'
  import { conn, isNative, loadConnection } from './lib/platform'
  import { initNative } from './lib/native'

  const VIEWS = { painel: Dashboard, servicos: Services, docker: Containers, pihole: PiHole, rede: Network, apps: Apps, sistema: System }
  const View = $derived(VIEWS[router.current])

  let clock = $state('')
  const tickClock = () => (clock = new Date().toLocaleTimeString('pt-BR', { hour12: false }))

  async function boot() {
    try {
      await loadConnection()
    } catch (e) {
      console.error('Falha ao ler a conexão salva', e)
    }
    if (isNative && !conn.token) {
      store.authed = false
      return
    }
    try {
      const me = await api.me()
      store.me = me
      store.authed = me.authenticated
      if (me.authenticated) connect()
    } catch {
      store.authed = false
    }
  }

  onMount(() => {
    initBioLock()
    boot()
    initNative()
    tickClock()
    const t = setInterval(tickClock, 1000)
    return () => clearInterval(t)
  })

  $effect(() => {
    if (store.authed === false) disconnect()
  })

  const badges = $derived<Partial<Record<ViewId, string>>>({
    servicos: store.services?.some((s) => s.active === 'failed') ? '!' : undefined,
    pihole: store.pihole?.configured && (!store.pihole.online || !store.pihole.blocking) ? '!' : undefined,
    apps: (store.homeassistant?.configured && !store.homeassistant.online) || (store.jellyfin?.configured && !store.jellyfin.online) ? '!' : undefined,
    sistema: store.backups?.some((b) => b.status === 'failed' || b.status === 'stale') || store.apt?.rebootRequired ? '!' : undefined,
  })

  function nav(id: ViewId) {
    if (id === router.current) return
    sfx.select()
    go(id)
    document.querySelector('main')?.scrollTo({ top: 0 })
    window.scrollTo({ top: 0 })
  }

  function onkey(e: KeyboardEvent) {
    if (!store.authed || e.ctrlKey || e.metaKey || e.altKey) return
    const el = e.target as HTMLElement
    if (el.closest('input, textarea, select, [role=dialog]')) return
    const k = e.key.toLowerCase()
    if (k >= '1' && k <= String(views.length)) nav(views[Number(k) - 1].id)
    else if (k === 'q') { sfx.select(); step(-1) }
    else if (k === 'e') { sfx.select(); step(1) }
  }
</script>

<svelte:window onkeydown={onkey} />

{#if store.authed === false}
  <Login version={store.me?.version} onsuccess={boot} />
{:else if store.authed}
  <div class="shell">
    <header class="top">
      <div class="brand">
        <span class="logo" aria-hidden="true"></span>
        <span class="word">BUREAU</span>
        <span class="mono dim host">// {store.me?.hostname ?? store.host?.hostname ?? '…'}</span>
      </div>
      <div class="status mono">
        <span class="sig" class:lost={!store.connected}>
          <i></i><span class="sig-txt">{store.connected ? 'SINAL ESTÁVEL' : 'SEM SINAL'}</span>
        </span>
        <span class="clock num">{clock}</span>
      </div>
    </header>

    <nav aria-label="Seções">
      <ul>
        {#each views as v, i (v.id)}
          <li>
            <button
              class="nav-item"
              class:active={router.current === v.id}
              aria-current={router.current === v.id ? 'page' : undefined}
              onclick={() => nav(v.id)}
              onmouseenter={() => sfx.move()}
            >
              <span class="idx">{String(i + 1).padStart(2, '0')}</span>
              <span class="lbl full">{v.label}</span>
              <span class="lbl short">{v.short}</span>
              {#if badges[v.id]}<span class="badge">{badges[v.id]}</span>{/if}
            </button>
          </li>
        {/each}
      </ul>
      <div class="nav-foot mono">
        <div class="mute">[Q] [E] NAVEGAR · [1–7] SALTAR</div>
        <div class="mute">BUREAU v{store.me?.version}</div>
      </div>
    </nav>

    <main>
      {#key router.current}
        <div class="view"><View /></div>
      {/key}
    </main>
  </div>
{:else}
  <div class="booting mono"><span class="red">■</span> ESTABELECENDO CONTATO…</div>
{/if}

{#if store.locked}<LockScreen />{/if}

<Toasts />

<style>
  .booting { min-height: 100dvh; display: grid; place-items: center; color: var(--fg-dim); animation: blink 1.2s steps(2) infinite; }
  .shell {
    display: grid;
    grid-template-columns: var(--nav-w) minmax(0, 1fr);
    grid-template-rows: calc(var(--head-h) + var(--safe-t)) minmax(0, 1fr);
    grid-template-areas: 'top top' 'nav main';
    height: 100dvh;
  }

  /* ── Cabeçalho ───────────────────────────────────── */
  .top {
    grid-area: top;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: var(--safe-t) var(--gutter) 0;
    border-bottom: 1px solid var(--line);
    background: rgba(11, 11, 12, 0.85);
    backdrop-filter: blur(8px);
    z-index: 10;
  }
  .brand { display: flex; align-items: center; gap: 12px; min-width: 0; }
  .logo { width: 18px; height: 18px; border: 2px solid var(--fg); position: relative; flex: none; }
  .logo::after { content: ''; position: absolute; inset: 3px; background: var(--red); }
  .word { font-stretch: 112%; font-weight: 800; font-size: 15px; letter-spacing: 0.32em; }
  .host { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .status { display: flex; align-items: center; gap: 18px; }
  .sig { display: flex; align-items: center; gap: 8px; }
  .sig i { width: 7px; height: 7px; background: var(--fg); }
  .sig.lost { color: var(--red); }
  .sig.lost i { background: var(--red); animation: blink 0.6s steps(2) infinite; }
  .clock { color: var(--fg-dim); }

  /* ── Navegação (estilo menu de pausa) ────────────── */
  nav {
    grid-area: nav;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    padding: 28px 0 20px var(--gutter);
    border-right: 1px solid var(--line);
    overflow: auto;
  }
  ul { list-style: none; display: flex; flex-direction: column; gap: 2px; }
  .nav-item {
    position: relative;
    display: flex;
    align-items: baseline;
    gap: 12px;
    width: 100%;
    padding: 9px 14px 7px;
    text-align: left;
    color: var(--fg-dim);
    transition: color 80ms;
  }
  .nav-item::before {
    content: '';
    position: absolute;
    inset: 0;
    background: rgba(236, 235, 230, 0.07);
    transform: scaleX(0);
    transform-origin: left;
    transition: transform 140ms var(--ease);
  }
  .nav-item:hover { color: var(--fg); }
  .nav-item:hover::before { transform: scaleX(1); }
  .nav-item.active { color: var(--bg); }
  .nav-item.active::before { background: var(--fg); transform: scaleX(1); transition-duration: 90ms; }
  .nav-item > * { position: relative; }
  .idx { font-family: var(--mono); font-size: 11px; color: var(--red); width: 18px; }
  .lbl { font-stretch: 70%; font-weight: 700; font-size: 26px; line-height: 1; text-transform: uppercase; transition: transform 140ms var(--ease); }
  .nav-item:hover .lbl { transform: translateX(4px); }
  .nav-item.active .lbl { transform: none; }
  .short { display: none; }
  .badge {
    margin-left: auto;
    align-self: center;
    min-width: 18px;
    height: 18px;
    display: grid;
    place-items: center;
    background: var(--red);
    color: var(--bg);
    font-family: var(--mono);
    font-size: 11px;
    font-weight: 700;
    animation: blink 1.2s steps(2) infinite;
  }
  .nav-foot { display: flex; flex-direction: column; gap: 4px; padding: 20px 14px 0; font-size: 10px; }

  /* ── Conteúdo ────────────────────────────────────── */
  main { grid-area: main; overflow: auto; padding: 30px var(--gutter) 60px; scrollbar-gutter: stable; }
  .view { max-width: 1400px; animation: enter 180ms var(--ease); }
  @keyframes enter { from { opacity: 0; transform: translateX(-10px); } }

  /* ── Celular: barra inferior ─────────────────────── */
  @media (max-width: 820px) {
    .shell {
      grid-template-columns: 1fr;
      grid-template-rows: calc(var(--head-h) + var(--safe-t)) minmax(0, 1fr) auto;
      grid-template-areas: 'top' 'main' 'nav';
    }
    .clock, .sig-txt { display: none; }
    nav {
      padding: 0 0 var(--safe-b);
      border-right: 0;
      border-top: 1px solid var(--line);
      background: var(--bg);
      overflow: visible;
    }
    ul { flex-direction: row; gap: 0; }
    li { flex: 1; min-width: 0; }
    .nav-item { flex-direction: column; align-items: center; gap: 2px; padding: 9px 2px 8px; }
    .idx { width: auto; font-size: 9px; }
    .full { display: none; }
    .short { display: block; font-size: 13px; letter-spacing: 0; }
    .nav-item:hover .lbl { transform: none; }
    .badge { position: absolute; top: 4px; right: 6px; min-width: 14px; height: 14px; font-size: 9px; }
    .nav-foot { display: none; }
    main { padding: 22px 16px 40px; }
  }
</style>
