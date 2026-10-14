#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
#  BUREAU — atualização automática
#  Executado como root pelo bureau-update.timer (a cada ~30 min) ou pelo botão
#  "Verificar agora" da interface. Instala a última release do GitHub se for
#  diferente da atual; se a nova versão não responder, restaura a anterior.
#
#  Manual:  sudo systemctl start bureau-update     (ou: sudo /opt/bureau/update.sh --force)
#  Desligar: sudo systemctl disable --now bureau-update.timer
# ──────────────────────────────────────────────────────────────
set -euo pipefail

REPO="${BUREAU_REPO:-Merlin262/bureau}"
PREFIX="${BUREAU_PREFIX:-/opt/bureau}"
DATA="${BUREAU_DATA:-/var/lib/bureau}"
CONF="${BUREAU_CONFIG:-/etc/bureau/bureau.json}"
STATE="$DATA/update.json"
FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1
# "Verificar agora" na interface cria este arquivo: tenta de novo mesmo uma versão que já falhou.
RETRY=0
[[ -f "$DATA/update.retry" ]] && { RETRY=1; rm -f "$DATA/update.retry"; }

case "$(uname -m)" in
  x86_64)        RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) echo "Arquitetura não suportada"; exit 1 ;;
esac

# Instalações antigas criaram um timer que podia nunca disparar (só OnUnitActiveSec): corrige uma vez.
TIMER=/etc/systemd/system/bureau-update.timer
if [[ -f "$TIMER" ]] && ! grep -q '^OnCalendar=' "$TIMER"; then
  cat >"$TIMER" <<'UNIT'
[Unit]
Description=BUREAU - verifica novas versões

[Timer]
# Horário fixo (a cada 30 min): não depende de execução anterior para começar a contar.
OnBootSec=5min
OnCalendar=*:0/30
RandomizedDelaySec=5min
Persistent=true

[Install]
WantedBy=timers.target
UNIT
  systemctl daemon-reload
  systemctl restart bureau-update.timer 2>/dev/null || true
  echo "Timer de atualização corrigido."
fi

PORT=$(grep -m1 -E '"Port"' "$CONF" 2>/dev/null | grep -oE '[0-9]+' || true)
PORT=${PORT:-8420}

# Estado lido pela interface (Sistema → Atualizações).
state() { # status mensagem última
  local cur
  cur=$(cat "$PREFIX/VERSION" 2>/dev/null || echo "")
  printf '{"current":"%s","latest":"%s","status":"%s","message":"%s","checkedAt":%s}\n' \
    "$cur" "${3:-}" "$1" "$2" "$(date +%s)" >"$STATE.tmp"
  chown bureau:bureau "$STATE.tmp" 2>/dev/null || true
  mv "$STATE.tmp" "$STATE"
  echo "[$1] $2"
}

# Qualquer falha inesperada: garante uma versão instalada e registra o erro.
on_error() {
  trap - ERR
  if [[ ! -d "$PREFIX" && -d "$PREFIX.old" ]]; then
    mv "$PREFIX.old" "$PREFIX"
    systemctl start bureau || true
  fi
  state erro "Falha inesperada durante a atualização" "${LATEST:-}"
}
trap on_error ERR

LATEST=$(curl -fsSL --max-time 20 -H "Accept: application/vnd.github+json" \
  "https://api.github.com/repos/$REPO/releases/latest" | grep -m1 '"tag_name"' | cut -d'"' -f4) || true
if [[ -z "${LATEST:-}" ]]; then
  state erro "Não foi possível consultar as releases no GitHub"
  exit 0
fi

CURRENT=$(cat "$PREFIX/VERSION" 2>/dev/null || echo "")
if [[ "$LATEST" == "$CURRENT" && $FORCE -eq 0 ]]; then
  state atualizado "Você está na versão mais recente" "$LATEST"
  exit 0
fi

# Não insiste numa versão que já falhou ao iniciar (evita derrubar o serviço a cada 30 min).
FAILED=""
if grep -q '"status":"erro"' "$STATE" 2>/dev/null && grep -q 'não iniciou' "$STATE" 2>/dev/null; then
  FAILED=$(grep -o '"latest":"[^"]*"' "$STATE" | cut -d'"' -f4)
fi
if [[ "$FAILED" == "$LATEST" && $FORCE -eq 0 && $RETRY -eq 0 ]]; then
  state erro "$LATEST não iniciou; aguardando a próxima release (ou use Verificar agora)" "$LATEST"
  exit 0
fi

state atualizando "Instalando $LATEST…" "$LATEST"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

if ! curl -fsSL --max-time 600 "https://github.com/$REPO/releases/download/$LATEST/bureau-$RID.tar.gz" -o "$TMP/pkg.tgz"; then
  state erro "Falha ao baixar $LATEST" "$LATEST"
  exit 0
fi
mkdir "$TMP/new"
tar -xzf "$TMP/pkg.tgz" -C "$TMP/new"
if [[ ! -f "$TMP/new/bureau" || ! -d "$TMP/new/wwwroot" ]]; then
  state erro "Pacote de $LATEST inválido" "$LATEST"
  exit 0
fi
chmod 755 "$TMP/new/bureau"
[[ -f "$TMP/new/update.sh" ]] && chmod 755 "$TMP/new/update.sh"
echo "$LATEST" >"$TMP/new/VERSION"

# Troca: guarda a versão atual para um eventual rollback.
rm -rf "$PREFIX.old"
cp -a "$PREFIX" "$PREFIX.old"
systemctl stop bureau
rm -rf "$PREFIX"          # o bash continua lendo este script pelo descritor já aberto
cp -a "$TMP/new" "$PREFIX"
systemctl start bureau

for _ in $(seq 1 30); do
  if curl -fs -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/api/auth/me"; then
    rm -rf "$PREFIX.old"
    state atualizado "Atualizado para $LATEST" "$LATEST"
    exit 0
  fi
  sleep 1
done

# Não subiu: volta para a versão anterior.
systemctl stop bureau || true
rm -rf "$PREFIX"
mv "$PREFIX.old" "$PREFIX"
systemctl start bureau
state erro "$LATEST não iniciou; versão anterior restaurada" "$LATEST"
exit 1
