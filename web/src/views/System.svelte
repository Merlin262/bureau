<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Meter from '../components/Meter.svelte'
  import HoldButton from '../components/HoldButton.svelte'
  import Sq from '../components/Sq.svelte'
  import LogViewer from '../components/LogViewer.svelte'
  import TerminalView from '../components/TerminalView.svelte'
  import Modal from '../components/Modal.svelte'
  import { bioAvailable, bioEnabled, setBio } from '../lib/biometric'
  import { store, act, disconnect, toast } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { sfx } from '../lib/sfx'
  import { bytes, duration, pct } from '../lib/format'
  import { clearToken, isNative, platform } from '../lib/platform'
  import { disablePush, enablePush, nativePushAvailable, pushAvailable, pushRegisteredId } from '../lib/push'
  import type { AccessEntry, DeviceSummary, PushConfig, UpdateStatus } from '../lib/types'
  import { onDestroy } from 'svelte'

  const h = $derived(store.host)
  const s = $derived(store.sys)
  let sound = $state(sfx.enabled)

  function toggleSound() {
    sfx.enabled = sound = !sound
    sfx.select()
  }

  async function logout() {
    if (isNative) {
      await api.revoke('self').catch(() => {})
      await clearToken()
      try { localStorage.removeItem('bureau.pushId') } catch { /* ok */ }
    } else {
      await api.logout().catch(() => {})
    }
    disconnect()
    store.authed = false
  }

  // ── Notificações e aparelhos ──
  let cfg = $state<PushConfig | null>(null)
  let devices = $state<DeviceSummary[]>([])
  let pushOn = $state(!!pushRegisteredId())

  const unavailable = $derived(
    isNative ? 'Este build do app não inclui o Firebase (google-services.json). Veja o README.'
    : !window.isSecureContext ? 'Push no navegador exige HTTPS — abra o BUREAU pelo endereço do Tailscale.'
    : 'Este navegador não suporta notificações push.',
  )

  async function loadDevices() {
    try {
      const [c, tokens, subs] = await Promise.all([api.pushConfig(), api.tokens(), api.pushDevices()])
      cfg = c
      devices = [...tokens, ...subs]
    } catch { /* offline */ }
  }
  loadDevices()

  async function togglePush() {
    await act('push', '', async () => {
      const r = pushOn ? await disablePush() : await enablePush()
      if (r.ok) pushOn = !pushOn
      return r
    })
    loadDevices()
  }

  async function removeDevice(d: DeviceSummary) {
    await act(`dev:${d.id}`, '', () => (d.kind === 'app' ? api.revoke(d.id) : api.pushRemove(d.id)), 'Aparelho removido')
    if (d.id === pushRegisteredId()) { pushOn = false; try { localStorage.removeItem('bureau.pushId') } catch { /* ok */ } }
    loadDevices()
  }

  // ── Atualizações ──
  let upd = $state<UpdateStatus | null>(null)
  let poll: ReturnType<typeof setInterval> | undefined
  const loadUpdate = async () => { try { upd = await api.updateStatus() } catch { /* servidor reiniciando */ } }
  loadUpdate()
  onDestroy(() => clearInterval(poll))

  async function checkUpdate() {
    const before = upd?.checkedAt ?? 0
    await act('upd', '', api.updateRun)
    clearInterval(poll)
    let n = 0
    // Acompanha até o script terminar (o servidor pode reiniciar no meio).
    poll = setInterval(async () => {
      await loadUpdate()
      if ((upd && upd.checkedAt > before && upd.status !== 'atualizando') || ++n > 60) clearInterval(poll)
    }, 2000)
  }

  const ago = (unix: number) => {
    if (!unix) return 'nunca'
    const m = Math.round((Date.now() / 1000 - unix) / 60)
    return m < 1 ? 'agora' : m < 60 ? `há ${m} min` : `há ${Math.round(m / 60)} h`
  }

  // ── Terminal, acessos, backup do BUREAU, biometria ──
  let terminal = $state(false)
  let access = $state<AccessEntry[]>([])
  const loadAccess = async () => { try { access = await api.access() } catch { /* offline */ } }
  loadAccess()
  const METHOD: Record<string, string> = { web: 'NAVEGADOR', app: 'APP', terminal: 'TERMINAL' }
  const flag = (c?: string) => (c && c.length === 2 ? String.fromCodePoint(...[...c.toUpperCase()].map((ch) => 0x1f1a5 + ch.charCodeAt(0))) : '')

  let restoring = $state<{ host: string; created: number; appVersion: string; files: Record<string, string>; [k: string]: unknown } | null>(null)
  async function exportBackup() {
    if (isNative) return toast('Exporte pelo navegador (o arquivo contém segredos e é baixado direto).', 'info')
    await act('bexp', '', async () => {
      const data = await api.backupExport()
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })
      const a = document.createElement('a')
      a.href = URL.createObjectURL(blob)
      a.download = `bureau-backup-${new Date().toISOString().slice(0, 10)}.json`
      a.click()
      setTimeout(() => URL.revokeObjectURL(a.href), 5000)
      return { ok: true, message: 'Backup exportado — guarde em local seguro' }
    })
  }
  async function pickBackup(e: Event) {
    const file = (e.target as HTMLInputElement).files?.[0]
    ;(e.target as HTMLInputElement).value = ''
    if (!file) return
    try {
      const data = JSON.parse(await file.text())
      if (data.kind !== 'bureau-backup') throw new Error('Arquivo não é um backup do BUREAU')
      restoring = data
    } catch (err) { toast(err instanceof Error ? err.message : String(err), 'err') }
  }
  async function restore() {
    const b = restoring!
    restoring = null
    await act('bimp', '', () => api.backupImport(b))
  }

  let bioOn = $state(bioEnabled())
  let bioOk = $state(false)
  bioAvailable().then((v) => (bioOk = v))
  async function toggleBio() {
    const r = await setBio(!bioOn)
    if (r.ok) bioOn = !bioOn
    toast(r.message ?? '', r.ok ? 'ok' : 'err')
  }

  // ── Pacotes e backups ──
  const apt = $derived(store.apt)
  let showPkgs = $state(false)
  let aptLogs = $state(false)
  let bkLogs = $state<string | null>(null)
  const when = (unix: number) =>
    unix ? new Date(unix * 1000).toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—'
  const BK: Record<string, string> = { ok: 'EM DIA', running: 'EXECUTANDO', failed: 'FALHOU', stale: 'ATRASADO', unknown: 'SEM DADOS' }
  const bkSq = (st: string) => (st === 'ok' ? 'on' : st === 'running' ? 'busy' : st === 'failed' || st === 'stale' ? 'fail' : 'off')

  const KIND: Record<string, string> = { app: 'SESSÃO DO APP', fcm: 'PUSH · ANDROID', apns: 'PUSH · IPHONE', webpush: 'PUSH · NAVEGADOR' }
  const date = (unix: number) => new Date(unix * 1000).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: '2-digit' })

  const power = (a: 'reboot' | 'poweroff') =>
    act('power', a, () => api.power(a), a === 'reboot' ? 'Reiniciando o servidor…' : 'Desligando o servidor…')
</script>

<Title index="07" code="HOST" label="Sistema" />

<div class="grid cols">
  <Panel label="IDENTIFICAÇÃO">
    <dl>
      <dt>HOST</dt><dd>{h?.hostname ?? '—'}</dd>
      <dt>SISTEMA</dt><dd>{h?.os ?? '—'}</dd>
      <dt>KERNEL</dt><dd>{h?.kernel ?? '—'}</dd>
      <dt>CPU</dt><dd>{h?.cpuModel ?? '—'} · {h?.cores ?? 0} núcleos</dd>
      <dt>ONLINE HÁ</dt><dd>{s ? duration(s.uptime) : '—'}</dd>
      <dt>ENDEREÇOS</dt><dd>{#each h?.addresses ?? [] as a}<div class="mono">{a}</div>{/each}</dd>
      <dt>BUREAU</dt><dd class="mono">v{h?.version ?? store.me?.version}</dd>
    </dl>
  </Panel>

  <Panel label="ARMAZENAMENTO">
    {#each h?.disks ?? [] as d}
      {@const p = pct(d.used, d.total)}
      <div class="disk">
        <div class="disk-head">
          <span class="name">{d.mount}</span>
          <span class="mono dim num">{bytes(d.used)} / {bytes(d.total)} · {d.fs}</span>
        </div>
        <Meter value={p} hot={90} />
      </div>
    {:else}
      <p class="empty">Sem discos detectados.</p>
    {/each}
  </Panel>
</div>

<div class="grid cols">
  <Panel label="PACOTES DO SISTEMA" alert={!!apt?.rebootRequired}>
    {#if !apt}
      <p class="empty">Consultando…</p>
    {:else if !apt.supported}
      <p class="mono dim">{apt.lastResult ?? 'DESATIVADO NA CONFIGURAÇÃO (SystemUpdates)'}</p>
    {:else}
      <div class="apt-stats">
        <div><div class="mid num">{apt.total}</div><span class="mono dim">PENDENTES</span></div>
        <div><div class="mid num" class:red={apt.security > 0}>{apt.security}</div><span class="mono dim">SEGURANÇA</span></div>
      </div>
      {#if apt.rebootRequired}
        <p class="mono red warn">▲ REINICIALIZAÇÃO NECESSÁRIA{apt.rebootPackages.length ? ` (${apt.rebootPackages.slice(0, 3).join(', ')})` : ''}</p>
      {/if}
      <p class="dim small">
        {apt.upgrading ? 'Atualizando pacotes agora…' : `Verificado ${when(apt.checkedAt)}`}
        {#if apt.lastRunAt} · última atualização {when(apt.lastRunAt)} ({apt.lastResult === 'success' ? 'ok' : apt.lastResult}){/if}
      </p>
      {#if apt.total > 0}
        <button class="link mono" onclick={() => (showPkgs = !showPkgs)}>{showPkgs ? '▾' : '▸'} VER PACOTES</button>
        {#if showPkgs}<p class="pkgs mono">{apt.packages.join(' · ')}</p>{/if}
      {/if}
      <div class="actions top">
        <HoldButton label={apt.upgrading ? 'Atualizando…' : 'Atualizar pacotes'} disabled={apt.upgrading || !!store.pending.apt}
          onconfirm={() => act('apt', '', api.aptUpgrade)} />
        <button class="btn" class:busy={!!store.pending.aptc} onclick={() => act('aptc', '', api.aptCheck)}>Reverificar</button>
        <button class="btn ghost" class:on={aptLogs} onclick={() => (aptLogs = !aptLogs)}>Logs</button>
      </div>
      {#if aptLogs}<div class="logwrap"><LogViewer load={api.aptLogs} /></div>{/if}
    {/if}
  </Panel>

  <Panel label="BACKUPS">
    {#each store.backups ?? [] as b (b.unit)}
      <div class="bk">
        <Sq state={bkSq(b.status)} />
        <div class="bk-info">
          <div class="name">{b.name}</div>
          <div class="mono dim tiny">ÚLTIMO SUCESSO {when(b.lastSuccess)} · PRÓXIMO {when(b.nextRun)}</div>
          <div class="small" class:red={b.status === 'failed' || b.status === 'stale'}>{b.message}</div>
        </div>
        <span class="mono st" class:red={b.status === 'failed' || b.status === 'stale'}>{BK[b.status]}</span>
        <div class="actions">
          <button class="btn" class:busy={!!store.pending[`bk:${b.unit}`]} disabled={b.status === 'running'}
            onclick={() => act(`bk:${b.unit}`, '', () => api.backupRun(b.unit))}>Executar</button>
          <button class="btn ghost" class:on={bkLogs === b.unit} onclick={() => (bkLogs = bkLogs === b.unit ? null : b.unit)}>Logs</button>
        </div>
      </div>
      {#if bkLogs === b.unit}<div class="logwrap"><LogViewer load={() => api.backupLogs(b.unit)} /></div>{/if}
    {:else}
      <p class="dim small">Nenhum backup configurado. Em <code>/etc/bureau/bureau.json</code>:</p>
      <pre class="cfg">"Backups": [{'{'} "Name": "Fotos", "Unit": "restic-fotos.service", "MaxAgeHours": 26 {'}'}]</pre>
      <p class="dim small">Qualquer serviço systemd serve (restic, borg, rsync…); se houver um <code>.timer</code> de mesmo nome, mostro a próxima execução.</p>
    {/each}
  </Panel>
</div>

<div class="grid cols">
  <Panel label="NOTIFICAÇÕES">
    {#if pushAvailable}
      <div class="pref">
        <div>
          <div class="name">Neste aparelho</div>
          <div class="dim small">{nativePushAvailable ? (platform === 'ios' ? 'Push nativo (Apple)' : 'Push nativo (Firebase)') : 'Web Push do navegador'} · serviço caiu, queda de energia, disco cheio…</div>
        </div>
        <button class="btn" class:on={pushOn} class:busy={!!store.pending.push} onclick={togglePush}>{pushOn ? 'Ativadas' : 'Ativar'}</button>
      </div>
    {:else}
      <p class="dim small warn">{unavailable}</p>
    {/if}
    <div class="pref">
      <div><div class="name">Enviar teste</div><div class="dim small">Para todos os aparelhos inscritos{cfg?.ntfy ? ' e o ntfy' : ''}.</div></div>
      <button class="btn" class:busy={!!store.pending.ptest} onclick={() => act('ptest', '', api.pushTest)}>Testar</button>
    </div>
    <div class="chans mono dim">
      <span>WEB PUSH <b class:off={!cfg}>{cfg ? '●' : '○'}</b></span>
      <span>FIREBASE <b class:off={!cfg?.fcm}>{cfg?.fcm ? '●' : '○'}</b></span>
      <span>APPLE <b class:off={!cfg?.apns}>{cfg?.apns ? '●' : '○'}</b></span>
      <span>NTFY <b class:off={!cfg?.ntfy}>{cfg?.ntfy ? '●' : '○'}</b></span>
    </div>
  </Panel>

  <Panel label="APARELHOS">
    {#each devices as d (d.kind + d.id)}
      <div class="dev">
        <div>
          <div class="name">{d.name}{#if d.current}<span class="red mono cur"> · ESTE</span>{/if}</div>
          <div class="mono dim">{KIND[d.kind] ?? d.kind} · {date(d.created)}</div>
        </div>
        <button class="btn ghost danger" class:busy={!!store.pending[`dev:${d.id}`]} onclick={() => removeDevice(d)}>{d.kind === 'app' ? 'Revogar' : 'Remover'}</button>
      </div>
    {:else}
      <p class="empty">Nenhum aparelho conectado.</p>
    {/each}
  </Panel>
</div>

<div class="grid cols">
  <Panel label="TERMINAL">
    {#if store.me?.terminal}
      <p class="dim small warn">Shell do servidor via SSH no seu usuário — o Linux pede a sua senha. Cada abertura fica no registro de acessos.</p>
      <button class="btn on" onclick={() => (terminal = true)}>Abrir terminal ▸</button>
    {:else}
      <p class="dim small">Desativado. Para ligar (pede a senha do Linux a cada sessão), em <code>/etc/bureau/bureau.json</code>:</p>
      <pre class="cfg">"Terminal": {'{'} "Enabled": true, "User": "seu-usuario-linux" {'}'}</pre>
      <p class="dim small">Requer o servidor SSH (<code>sudo apt install openssh-server</code>).</p>
    {/if}
  </Panel>

  <Panel label="BACKUP DO BUREAU">
    <p class="dim small warn">Apelidos, monitores, bloqueios, aparelhos conectados, alertas, chaves de push e configuração — para reinstalar sem perder nada. <b>Contém segredos</b>: guarde com cuidado.</p>
    <div class="actions">
      <button class="btn" class:busy={!!store.pending.bexp} onclick={exportBackup}>Exportar</button>
      <label class="btn">Importar…<input type="file" accept="application/json,.json" hidden onchange={pickBackup} /></label>
    </div>
  </Panel>
</div>

<Panel label="REGISTRO DE ACESSOS" class="access">
  {#snippet aside()}<button class="btn mini" onclick={loadAccess}>Atualizar</button>{/snippet}
  {#each access.slice(0, 25) as a}
    <div class="acc" class:fail={!a.ok}>
      <span class="mono dim num">{when(a.ts)}</span>
      <span class="mono">{a.ok ? '✓' : '✗'} {METHOD[a.method] ?? a.method}</span>
      <span class="mono num">{flag(a.country)} {a.ip}</span>
      <span class="dev-name">{a.device}</span>
    </div>
  {:else}
    <p class="empty">Nenhum acesso registrado.</p>
  {/each}
</Panel>

{#if terminal}<TerminalView onclose={() => (terminal = false)} />{/if}

{#if restoring}
  <Modal title="Restaurar backup" onclose={() => (restoring = null)}>
    <dl>
      <dt>ORIGEM</dt><dd>{restoring.host}</dd>
      <dt>CRIADO</dt><dd>{when(restoring.created)}</dd>
      <dt>VERSÃO</dt><dd class="mono">v{restoring.appVersion}</dd>
      <dt>ARQUIVOS</dt><dd class="mono small">{Object.keys(restoring.files ?? {}).join(', ')}</dd>
    </dl>
    <p class="dim small warn">Os dados atuais serão substituídos e o BUREAU reinicia em seguida.</p>
    <HoldButton label="Restaurar" onconfirm={restore} />
  </Modal>
{/if}

<div class="grid cols">
  <Panel label="PREFERÊNCIAS">
    <div class="pref">
      <div><div class="name">Efeitos sonoros</div><div class="dim small">Bipes de interface ao navegar e confirmar.</div></div>
      <button class="btn" class:on={sound} onclick={toggleSound}>{sound ? 'Ligado' : 'Desligado'}</button>
    </div>
    {#if isNative && bioOk}
      <div class="pref">
        <div><div class="name">Biometria</div><div class="dim small">Exigir digital/rosto ao abrir o app.</div></div>
        <button class="btn" class:on={bioOn} onclick={toggleBio}>{bioOn ? 'Ligada' : 'Desligada'}</button>
      </div>
    {/if}
    <div class="pref">
      <div><div class="name">Encerrar sessão</div><div class="dim small">Sai deste dispositivo.</div></div>
      <button class="btn" onclick={logout}>Sair</button>
    </div>
    <div class="keys mono dim">
      <span><kbd>1</kbd>–<kbd>7</kbd> SEÇÕES</span>
      <span><kbd>Q</kbd> <kbd>E</kbd> ANTERIOR / PRÓXIMA</span>
      <span><kbd>ESC</kbd> FECHAR</span>
    </div>
  </Panel>

  <Panel label="ATUALIZAÇÕES">
    {#if upd}
      <dl>
        <dt>INSTALADA</dt><dd class="mono">{upd.current}</dd>
        <dt>NO GITHUB</dt><dd class="mono">{upd.latest ?? '—'}</dd>
        <dt>ESTADO</dt>
        <dd class:red={upd.status === 'erro'} class:blink={upd.status === 'atualizando'}>{upd.message}</dd>
        <dt>VERIFICADO</dt><dd class="mono dim">{ago(upd.checkedAt)}</dd>
      </dl>
      <div class="pref">
        <div class="dim small">{upd.auto ? 'Automática: verifica novas releases a cada ~30 min.' : upd.supported ? 'Automática desligada.' : 'Atualizador não instalado — rode o install.sh novamente.'}</div>
        <button class="btn" class:busy={!!store.pending.upd || upd.status === 'atualizando'} disabled={!upd.supported} onclick={checkUpdate}>Verificar agora</button>
      </div>
    {:else}
      <p class="empty">Consultando…</p>
    {/if}
  </Panel>

  <Panel label="ENERGIA" alert>
    {#if store.me?.allowPower}
      <p class="dim small warn">Ações irreversíveis. Mantenha pressionado para confirmar.</p>
      <div class="actions">
        <HoldButton label="Reiniciar servidor" onconfirm={() => power('reboot')} disabled={!!store.pending.power} />
        <HoldButton label="Desligar servidor" ms={2000} onconfirm={() => power('poweroff')} disabled={!!store.pending.power} />
      </div>
    {:else}
      <p class="mono dim">DESATIVADO NA CONFIGURAÇÃO (AllowPower)</p>
    {/if}
  </Panel>
</div>

<style>
  .cols { grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); margin-bottom: 14px; }
  dl { display: grid; grid-template-columns: 96px 1fr; gap: 9px 14px; }
  dt { font-family: var(--mono); font-size: 11px; letter-spacing: 0.08em; color: var(--fg-mute); padding-top: 2px; }
  dd { font-size: 14px; min-width: 0; overflow-wrap: anywhere; }
  .disk { margin-bottom: 16px; }
  .disk-head { display: flex; justify-content: space-between; gap: 10px; margin-bottom: 6px; flex-wrap: wrap; }
  .name { font-stretch: 80%; font-weight: 600; font-size: 17px; }
  .small { font-size: 13px; }
  .pref { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 0; border-bottom: 1px solid var(--line); }
  .keys { display: flex; flex-wrap: wrap; gap: 8px 18px; margin-top: 14px; }
  kbd { display: inline-block; min-width: 18px; padding: 0 4px; border: 1px solid var(--line-2); text-align: center; color: var(--fg); font-family: var(--mono); }
  .warn { margin-bottom: 12px; }
  .apt-stats { display: flex; gap: 28px; margin-bottom: 10px; }
  .mid { font-stretch: 70%; font-weight: 700; font-size: 34px; line-height: 1; }
  .link { color: var(--fg-dim); font-size: 10px; margin-top: 8px; }
  .link:hover { color: var(--fg); }
  .pkgs { margin-top: 6px; font-size: 10.5px; color: var(--fg-dim); line-height: 1.6; }
  .top { margin-top: 14px; }
  .logwrap { margin: 10px -16px 0; }
  .bk { display: grid; grid-template-columns: 10px minmax(0, 1fr) auto; gap: 8px 12px; align-items: center; padding: 10px 0; border-bottom: 1px solid var(--line); }
  .bk .actions { grid-column: 2 / -1; }
  .bk-info { min-width: 0; }
  .tiny { font-size: 10px; margin: 2px 0; }
  .st { font-size: 11px; white-space: nowrap; }
  .cfg { margin: 10px 0; padding: 10px; background: #060607; font-family: var(--mono); font-size: 12px; overflow: auto; }
  code { font-family: var(--mono); font-size: 12px; }
  .chans { display: flex; gap: 18px; margin-top: 14px; flex-wrap: wrap; }
  .chans b { color: var(--fg); font-weight: 400; }
  .chans b.off { color: var(--fg-mute); }
  .dev { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 0; border-bottom: 1px solid var(--line); }
  .cur { font-size: 11px; }
  :global(.access) { margin-bottom: 14px; }
  .acc { display: grid; grid-template-columns: 96px 110px 170px minmax(0, 1fr); gap: 10px; padding: 7px 0; border-bottom: 1px solid var(--line); align-items: center; font-size: 13px; }
  .acc .mono { font-size: 11px; }
  .acc.fail .mono:nth-child(2) { color: var(--red); }
  .dev-name { color: var(--fg-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .btn.mini { height: 24px; padding: 0 8px; font-size: 10px; }
  @media (max-width: 700px) {
    .acc { grid-template-columns: 86px 1fr; }
    .acc .dev-name { grid-column: 1 / -1; }
  }
</style>
