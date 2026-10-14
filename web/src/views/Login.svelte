<script lang="ts">
  import { api } from '../lib/api'
  import { sfx } from '../lib/sfx'
  import { conn, deviceName, isNative, normalizeServer, saveConnection } from '../lib/platform'

  let { onsuccess, version }: { onsuccess: () => void; version?: string } = $props()

  let password = $state('')
  let error = $state('')
  let busy = $state(false)
  let shake = $state(0)
  let server = $state(conn.base)

  async function submit(e: SubmitEvent) {
    e.preventDefault()
    if (!password || busy) return
    busy = true
    error = ''
    try {
      let r: { ok?: boolean; message?: string }
      if (isNative) {
        // App: troca a senha por um token deste aparelho.
        const base = normalizeServer(server)
        if (!base) throw new Error('Informe o endereço do servidor')
        conn.base = base
        const t = await api.token(password, deviceName())
        if (t.token) await saveConnection(base, t.token)
        r = { ok: !!t.token, message: t.message }
      } else {
        r = await api.login(password)
      }
      if (r.ok) {
        sfx.confirm()
        onsuccess()
      } else {
        error = r.message ?? 'ACESSO NEGADO'
        shake++
        sfx.error()
        password = ''
      }
    } catch (err) {
      error = err instanceof Error ? err.message : String(err)
      shake++
    } finally {
      busy = false
    }
  }
</script>

<div class="login">
  <div class="frame">
    <div class="meta mono">
      <span class="red">■</span> SISTEMA DE CONTROLE DOMÉSTICO <span class="dim">· v{version ?? '—'}</span>
    </div>

    <h1>Bureau</h1>

    <p class="type mono">ACESSO RESTRITO // IDENTIFIQUE-SE</p>

    {#key shake}
      <form onsubmit={submit} class:shake={shake > 0}>
        <input type="text" name="username" value="admin" autocomplete="username" hidden readonly />
        {#if isNative}
          <label class="mono dim" for="srv">SERVIDOR</label>
          <input id="srv" class="field srv" type="url" inputmode="url" autocomplete="url" autocapitalize="off" spellcheck="false"
            placeholder="192.168.0.5  ou  servidor.tailnet.ts.net" bind:value={server} disabled={busy} />
        {/if}
        <label class="mono dim" for="pw">CHAVE DE ACESSO</label>
        <div class="input-row">
          <!-- svelte-ignore a11y_autofocus -->
          <input id="pw" class="field" type="password" autocomplete="current-password" autofocus bind:value={password} disabled={busy} />
          <button class="btn on" type="submit" class:busy disabled={!password}>Acessar ▸</button>
        </div>
        {#if error}<p class="err mono">▲ {error}</p>{/if}
      </form>
    {/key}
  </div>

  <div class="deco" aria-hidden="true">
    <div class="sq-red"></div>
    <div class="lines"></div>
    <div class="stamp mono">CLASSIFICADO<br />NÍVEL 1</div>
  </div>
</div>

<style>
  .login {
    min-height: 100dvh;
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 0.8fr);
    align-items: center;
    padding: calc(var(--gutter) + var(--safe-t)) var(--gutter) calc(var(--gutter) + var(--safe-b));
    gap: 40px;
  }
  .frame { max-width: 560px; padding-left: clamp(0px, 4vw, 60px); }
  .meta { margin-bottom: 14px; }

  h1 {
    font-stretch: 62%;
    font-weight: 800;
    font-size: clamp(88px, 17vw, 220px);
    line-height: 0.78;
    letter-spacing: -0.02em;
    text-transform: uppercase;
    margin-left: -0.04em;
    animation: flicker 900ms steps(1) both;
  }
  @keyframes flicker {
    0% { opacity: 0; } 8% { opacity: 1; } 12% { opacity: 0.2; } 18% { opacity: 1; text-shadow: 5px 0 var(--red), -5px 0 var(--cyan); }
    24% { text-shadow: none; } 60% { opacity: 1; } 63% { opacity: 0.5; } 66% { opacity: 1; }
  }

  .type {
    display: block;
    width: fit-content;
    margin: 22px 0 36px;
    font-size: 13px;
    white-space: nowrap;
    clip-path: inset(0 100% 0 0);
    animation: type 900ms steps(36) 400ms forwards;
  }
  .type::after { content: '_'; color: var(--red); animation: caret 0.8s steps(2) infinite; }
  @keyframes type { to { clip-path: inset(0 0 0 0); } }
  @keyframes caret { 50% { opacity: 0; } }

  label { display: block; margin-bottom: 8px; }
  .srv { height: 44px; margin-bottom: 18px; font-size: 14px; }
  .input-row { display: flex; gap: 8px; }
  .input-row .field { height: 44px; font-size: 16px; letter-spacing: 0.3em; }
  .input-row .btn { height: 44px; padding: 0 18px; }
  .err { margin-top: 12px; color: var(--red); font-size: 12px; }
  .shake { animation: shake 260ms steps(6); }
  @keyframes shake { 0%, 100% { transform: none; } 20% { transform: translateX(-8px); } 40% { transform: translateX(6px); } 60% { transform: translateX(-4px); } 80% { transform: translateX(2px); } }

  .deco { position: relative; height: min(70vh, 560px); border-left: 1px solid var(--line); }
  .sq-red { position: absolute; left: 18%; top: 22%; width: 34%; aspect-ratio: 1; background: var(--red); mix-blend-mode: screen; }
  .lines {
    position: absolute; inset: 0;
    background: repeating-linear-gradient(0deg, var(--line) 0 1px, transparent 1px 44px);
    mask-image: linear-gradient(90deg, black, transparent);
  }
  .stamp {
    position: absolute; right: 10%; bottom: 10%;
    padding: 8px 12px;
    border: 1px solid var(--red);
    color: var(--red);
    transform: rotate(-4deg);
    line-height: 1.5;
  }

  @media (max-width: 820px) {
    .login { grid-template-columns: 1fr; }
    .deco { display: none; }
    .frame { padding-left: 0; }
  }
</style>
