#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
#  BUREAU — instalador
#
#  curl -fsSL https://raw.githubusercontent.com/Merlin262/bureau/main/install.sh | sudo bash
#  ou, a partir de um build local:   sudo ./install.sh --local dist/linux-x64
#
#  Rodar de novo = atualizar (a configuração existente é preservada).
# ──────────────────────────────────────────────────────────────
set -euo pipefail

REPO="${BUREAU_REPO:-Merlin262/bureau}"
PREFIX=/opt/bureau
CONF_DIR=/etc/bureau
CONF="$CONF_DIR/bureau.json"
DATA_DIR=/var/lib/bureau
SVC_USER=bureau
PORT=8420
LOCAL_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --local) LOCAL_DIR="$2"; shift 2 ;;
    --repo)  REPO="$2"; shift 2 ;;
    --port)  PORT="$2"; shift 2 ;;
    *) echo "Opção desconhecida: $1"; exit 1 ;;
  esac
done

R=$'\e[38;5;196m'; W=$'\e[1;97m'; D=$'\e[2m'; N=$'\e[0m'
say()  { printf '%s■%s %s\n' "$R" "$N" "$*"; }
ok()   { printf '  %s✓%s %s\n' "$W" "$N" "$*"; }
die()  { printf '%s▲ ERRO:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }
ask()  { local a=""; read -r -p "  $1 " a </dev/tty || true; printf '%s' "${a:-${2:-}}"; }
confirm() { [[ "$(ask "$1" "${2:-n}")" =~ ^[sSyY] ]]; }
ask_secret() {
  local a=""
  read -r -s -p "  $1 " a </dev/tty || true
  echo >/dev/tty
  printf '%s' "$a"
}
json_escape() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "$s"; }

cat <<EOF

${W}  ██████  ██    ██ ██████  ███████  █████  ██    ██
  ██   ██ ██    ██ ██   ██ ██      ██   ██ ██    ██
  ██████  ██    ██ ██████  █████   ███████ ██    ██
  ██   ██ ██    ██ ██   ██ ██      ██   ██ ██    ██
  ██████   ██████  ██   ██ ███████ ██   ██  ██████${N}
  ${D}sistema de controle doméstico // instalação${N}

EOF

[[ $EUID -eq 0 ]] || die "Rode como root (use sudo)."
command -v systemctl >/dev/null || die "Este instalador requer systemd."

case "$(uname -m)" in
  x86_64)        RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "Arquitetura não suportada: $(uname -m)" ;;
esac

# ── 1. Binário ────────────────────────────────────────────────
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

if [[ -n "$LOCAL_DIR" ]]; then
  SRC="$LOCAL_DIR"
  VERSION="local"
  say "Usando build local: $SRC"
else
  VERSION=$(curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/$REPO/releases/latest" \
    | grep -m1 '"tag_name"' | cut -d'"' -f4 || true)
  [[ -n "$VERSION" ]] || die "Nenhuma release encontrada em github.com/$REPO (--repo usuario/bureau ou --local)."
  say "Baixando $VERSION ($RID) de github.com/$REPO"
  curl -fsSL "https://github.com/$REPO/releases/download/$VERSION/bureau-$RID.tar.gz" -o "$TMP/bureau.tgz" \
    || die "Falha no download de $VERSION."
  mkdir -p "$TMP/b" && tar -xzf "$TMP/bureau.tgz" -C "$TMP/b"
  SRC="$TMP/b"
fi
[[ -f "$SRC/bureau" && -d "$SRC/wwwroot" ]] || die "Pacote inválido em $SRC (esperado: bureau + wwwroot/)."

UPGRADE=0
if systemctl is-active --quiet bureau 2>/dev/null; then
  UPGRADE=1
  systemctl stop bureau
fi
install -d -m 755 "$PREFIX"
rm -rf "$PREFIX/wwwroot"
cp -a "$SRC/." "$PREFIX/"
chmod 755 "$PREFIX/bureau"
echo "$VERSION" >"$PREFIX/VERSION"
# Atualizador automático (vem no pacote; em pacotes antigos, baixa do repositório)
if [[ ! -f "$PREFIX/update.sh" ]]; then
  curl -fsSL "https://raw.githubusercontent.com/$REPO/main/deploy/bureau-update.sh" -o "$PREFIX/update.sh" || true
fi
[[ -f "$PREFIX/update.sh" ]] && chmod 755 "$PREFIX/update.sh"
ok "Binário $VERSION instalado em $PREFIX"

# ── 2. Usuário de serviço ─────────────────────────────────────
if ! id "$SVC_USER" &>/dev/null; then
  useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
fi
for g in docker systemd-journal adm; do
  getent group "$g" >/dev/null && usermod -aG "$g" "$SVC_USER"
done
install -d -o "$SVC_USER" -g "$SVC_USER" -m 750 "$DATA_DIR"
ok "Usuário '$SVC_USER' (grupos: $(id -nG "$SVC_USER" | tr ' ' ','))"

# ── 3. Configuração (somente na primeira instalação) ──────────
if [[ ! -f "$CONF" ]]; then
  say "Configuração inicial"

  # Serviços conhecidos que existem nesta máquina
  SERVICES=()
  for s in pihole-FTL docker ssh tailscaled cloudflared nginx caddy smbd nfs-server cron bureau; do
    systemctl list-unit-files "$s.service" --no-legend 2>/dev/null | grep -q . && SERVICES+=("\"$s\"")
  done
  SERVICES_JSON=$(IFS=,; echo "${SERVICES[*]:-}")

  PH_ENABLED=false; PH_URL="http://127.0.0.1"; PH_PASS=""
  if systemctl list-unit-files pihole-FTL.service --no-legend 2>/dev/null | grep -q .; then
    if [[ -f /etc/pihole/pihole.toml ]]; then
      PH_PORT=$(grep -E '^\s*port\s*=' /etc/pihole/pihole.toml | head -1 | grep -oE '[0-9]+' | head -1 || true)
      [[ -n "${PH_PORT:-}" && "$PH_PORT" != "80" ]] && PH_URL="http://127.0.0.1:$PH_PORT"
    fi
    ok "Pi-hole detectado ($PH_URL)"
    if confirm "Integrar com o Pi-hole? [S/n]" s; then
      PH_ENABLED=true
      echo "  ${D}Recomendado: crie uma 'app password' em Pi-hole → Settings → Web interface / API${N}"
      PH_PASS=$(ask_secret "Senha (ou app password) do Pi-hole:")
    fi
  fi

  echo "  ${D}Notificações push: o Web Push (navegador/iPhone) exige um e-mail de contato.${N}"
  PUSH_EMAIL=$(ask "E-mail para o Web Push [Enter para pular]:" "")
  [[ -n "$PUSH_EMAIL" ]] || PUSH_EMAIL="bureau@example.com"

  install -d -m 750 -o root -g "$SVC_USER" "$CONF_DIR"
  cat >"$CONF" <<EOF
{
  "Port": $PORT,
  "DataDir": "$DATA_DIR",
  "AllowPower": true,
  "Services": [$SERVICES_JSON],
  "Docker": { "Enabled": true, "Socket": "/var/run/docker.sock" },
  "PiHole": { "Enabled": $PH_ENABLED, "Url": "$PH_URL", "Password": "$(json_escape "$PH_PASS")" },
  "Alerts": { "Enabled": true, "DiskPercent": 90, "TempCelsius": 85, "BatteryCritical": 20 },
  "Push": {
    "WebPushSubject": "mailto:$(json_escape "$PUSH_EMAIL")",
    "FcmServiceAccount": "$CONF_DIR/firebase.json",
    "NtfyUrl": ""
  },
  "Logging": { "LogLevel": { "Default": "Warning", "bureau": "Information", "Microsoft.Hosting.Lifetime": "Information" } }
}
EOF
  chown root:"$SVC_USER" "$CONF"
  chmod 640 "$CONF"
  ok "Configuração criada em $CONF"

  say "Defina a senha de acesso ao BUREAU (mínimo 8 caracteres)"
  until BUREAU_CONFIG="$CONF" "$PREFIX/bureau" set-password </dev/tty; do echo "  Tente novamente."; done
  chown root:"$SVC_USER" "$CONF"; chmod 640 "$CONF"
else
  ok "Configuração existente preservada ($CONF)"
fi

# ── 4. Permissões (polkit): gerenciar serviços e energia sem root ──
ACTIONS="org.freedesktop.systemd1.manage-units;org.freedesktop.login1.reboot;org.freedesktop.login1.reboot-multiple-sessions;org.freedesktop.login1.power-off;org.freedesktop.login1.power-off-multiple-sessions"
if [[ -d /etc/polkit-1/rules.d ]]; then
  cat >/etc/polkit-1/rules.d/50-bureau.rules <<'EOF'
// BUREAU: permite ao usuário de serviço gerenciar unidades systemd e reiniciar/desligar.
polkit.addRule(function (action, subject) {
  if (subject.user !== "bureau") return polkit.Result.NOT_HANDLED;
  if (action.id === "org.freedesktop.systemd1.manage-units" ||
      action.id.indexOf("org.freedesktop.login1.reboot") === 0 ||
      action.id.indexOf("org.freedesktop.login1.power-off") === 0) {
    return polkit.Result.YES;
  }
  return polkit.Result.NOT_HANDLED;
});
EOF
fi
if [[ -d /etc/polkit-1/localauthority ]]; then # polkit antigo (< 0.106)
  install -d /etc/polkit-1/localauthority/50-local.d
  cat >/etc/polkit-1/localauthority/50-local.d/50-bureau.pkla <<EOF
[BUREAU]
Identity=unix-user:$SVC_USER
Action=$ACTIONS
ResultAny=yes
ResultInactive=yes
ResultActive=yes
EOF
fi
ok "Regras polkit instaladas"

# ── 5. Serviço systemd ────────────────────────────────────────
cat >/etc/systemd/system/bureau.service <<EOF
[Unit]
Description=BUREAU - painel de controle do servidor
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=$PREFIX
ExecStart=$PREFIX/bureau
Environment=BUREAU_CONFIG=$CONF
Restart=always
RestartSec=2

NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$DATA_DIR
PrivateTmp=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes
LockPersonality=yes

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now bureau >/dev/null 2>&1
sleep 1
systemctl is-active --quiet bureau || die "O serviço não iniciou. Veja: journalctl -u bureau -n 50"
ok "Serviço bureau ativo"

# ── 5a. Atualização de pacotes do sistema (disparada pela interface) ──
if [[ -x /usr/bin/apt-get ]]; then
  cat >/etc/systemd/system/bureau-apt-upgrade.service <<'UNIT'
[Unit]
Description=BUREAU - atualização de pacotes do sistema (apt)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
Environment=DEBIAN_FRONTEND=noninteractive
ExecStart=/usr/bin/apt-get update
ExecStart=/usr/bin/apt-get -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold --with-new-pkgs upgrade
TimeoutStartSec=1h
UNIT
  systemctl daemon-reload
  ok "Atualização de pacotes pela interface disponível"
fi

# ── 5b. Atualização automática ────────────────────────────────
if [[ -f "$PREFIX/update.sh" ]]; then
  cat >/etc/systemd/system/bureau-update.service <<UNIT
[Unit]
Description=BUREAU - atualização automática (releases do GitHub)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
Environment=BUREAU_REPO=$REPO
ExecStart=$PREFIX/update.sh
UNIT
  cat >/etc/systemd/system/bureau-update.timer <<'UNIT'
[Unit]
Description=BUREAU - verifica novas versões

[Timer]
OnBootSec=10min
OnUnitActiveSec=30min
RandomizedDelaySec=5min
Persistent=true

[Install]
WantedBy=timers.target
UNIT
  systemctl daemon-reload
  if systemctl is-enabled --quiet bureau-update.timer 2>/dev/null; then
    ok "Atualização automática ativa"
  elif confirm "Atualizar o BUREAU automaticamente a cada nova release? [S/n]" s; then
    systemctl enable --now bureau-update.timer >/dev/null 2>&1
    ok "Atualização automática ativa (a cada ~30 min)"
  fi
fi

# ── 6. Acesso externo (Tailscale) ─────────────────────────────
if [[ $UPGRADE -eq 0 ]]; then
  if ! command -v tailscale >/dev/null; then
    echo
    say "Acesso de fora de casa"
    echo "  ${D}O Tailscale cria uma VPN privada entre seus aparelhos: sem abrir portas no roteador,"
    echo "  com HTTPS automático (necessário para instalar o BUREAU como app no celular).${N}"
    if confirm "Instalar o Tailscale agora? [s/N]"; then
      curl -fsSL https://tailscale.com/install.sh | sh
      tailscale up </dev/tty || true
    fi
  fi
  if command -v tailscale >/dev/null && tailscale status >/dev/null 2>&1; then
    if confirm "Publicar o BUREAU via HTTPS na sua tailnet (tailscale serve)? [S/n]" s; then
      tailscale serve --bg "$PORT" >/dev/null && ok "tailscale serve configurado"
    fi
  fi
fi

# ── Fim ───────────────────────────────────────────────────────
echo
say "${W}BUREAU operacional.${N}"
for ip in $(hostname -I 2>/dev/null); do
  [[ "$ip" == *:* || "$ip" == 172.1[7-9].* || "$ip" == 172.2* ]] && continue
  echo "    → http://$ip:$PORT"
done
if command -v tailscale >/dev/null; then
  TS=$(tailscale status --self --json 2>/dev/null | grep -m1 -oE '"DNSName": *"[^"]+"' | cut -d'"' -f4 | sed 's/\.$//' || true)
  [[ -n "${TS:-}" ]] && echo "    → https://$TS   ${D}(qualquer lugar, via Tailscale)${N}"
fi
echo
echo "  ${D}Configuração: $CONF   ·   Logs: journalctl -u bureau -f"
echo "  Push no app Android: copie a conta de serviço do Firebase para $CONF_DIR/firebase.json"
echo "    (sudo chown root:$SVC_USER $CONF_DIR/firebase.json && sudo chmod 640 $CONF_DIR/firebase.json)"
echo "  Trocar senha: sudo BUREAU_CONFIG=$CONF $PREFIX/bureau set-password && sudo systemctl restart bureau${N}"
echo
