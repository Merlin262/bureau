#!/usr/bin/env bash
# BUREAU — desinstalador. Uso: sudo ./uninstall.sh
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Rode como root (sudo)."; exit 1; }

systemctl disable --now bureau bureau-update.timer 2>/dev/null || true
rm -f /etc/systemd/system/bureau.service /etc/systemd/system/bureau-update.service /etc/systemd/system/bureau-update.timer /etc/systemd/system/bureau-apt-upgrade.service
systemctl daemon-reload
rm -f /etc/polkit-1/rules.d/50-bureau.rules /etc/polkit-1/localauthority/50-local.d/50-bureau.pkla
rm -rf /opt/bureau
echo "■ Serviço e binário removidos."

read -r -p "  Apagar também configuração (/etc/bureau) e dados (/var/lib/bureau)? [s/N] " a </dev/tty || true
if [[ "${a:-n}" =~ ^[sSyY] ]]; then
  rm -rf /etc/bureau /var/lib/bureau
  userdel bureau 2>/dev/null || true
  echo "■ Configuração, dados e usuário removidos."
fi
command -v tailscale >/dev/null && echo "  Se usou 'tailscale serve', desative com: sudo tailscale serve reset"
