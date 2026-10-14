<script lang="ts">
  import Title from '../components/Title.svelte'
  import Panel from '../components/Panel.svelte'
  import Meter from '../components/Meter.svelte'
  import HoldButton from '../components/HoldButton.svelte'
  import { store, act, disconnect } from '../lib/store.svelte'
  import { api } from '../lib/api'
  import { sfx } from '../lib/sfx'
  import { bytes, duration, pct } from '../lib/format'
  import { clearToken, isNative, platform } from '../lib/platform'
  import { disablePush, enablePush, nativePushAvailable, pushAvailable, pushRegisteredId } from '../lib/push'
  import type { DeviceSummary, PushConfig } from '../lib/types'

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
    platform === 'ios' ? 'No iPhone, instale o BUREAU como app pelo Safari (Compartilhar → Adicionar à Tela de Início) para receber push.'
    : isNative ? 'Este build do app não inclui o Firebase (google-services.json). Veja o README.'
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

  const KIND: Record<string, string> = { app: 'SESSÃO DO APP', fcm: 'PUSH · ANDROID', webpush: 'PUSH · NAVEGADOR' }
  const date = (unix: number) => new Date(unix * 1000).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: '2-digit' })

  const power = (a: 'reboot' | 'poweroff') =>
    act('power', a, () => api.power(a), a === 'reboot' ? 'Reiniciando o servidor…' : 'Desligando o servidor…')
</script>

<Title index="06" code="HOST" label="Sistema" />

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
  <Panel label="NOTIFICAÇÕES">
    {#if pushAvailable}
      <div class="pref">
        <div>
          <div class="name">Neste aparelho</div>
          <div class="dim small">{nativePushAvailable ? 'Push nativo (Firebase)' : 'Web Push do navegador'} · serviço caiu, queda de energia, disco cheio…</div>
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
  <Panel label="PREFERÊNCIAS">
    <div class="pref">
      <div><div class="name">Efeitos sonoros</div><div class="dim small">Bipes de interface ao navegar e confirmar.</div></div>
      <button class="btn" class:on={sound} onclick={toggleSound}>{sound ? 'Ligado' : 'Desligado'}</button>
    </div>
    <div class="pref">
      <div><div class="name">Encerrar sessão</div><div class="dim small">Sai deste dispositivo.</div></div>
      <button class="btn" onclick={logout}>Sair</button>
    </div>
    <div class="keys mono dim">
      <span><kbd>1</kbd>–<kbd>6</kbd> SEÇÕES</span>
      <span><kbd>Q</kbd> <kbd>E</kbd> ANTERIOR / PRÓXIMA</span>
      <span><kbd>ESC</kbd> FECHAR</span>
    </div>
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
  .chans { display: flex; gap: 18px; margin-top: 14px; flex-wrap: wrap; }
  .chans b { color: var(--fg); font-weight: 400; }
  .chans b.off { color: var(--fg-mute); }
  .dev { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 0; border-bottom: 1px solid var(--line); }
  .cur { font-size: 11px; }
</style>
