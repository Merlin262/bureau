#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
#  Jellyfin (Docker) para o servidor do BUREAU
#
#  1) Instalar:   curl -fsSL https://raw.githubusercontent.com/Merlin262/bureau/main/scripts/jellyfin.sh | sudo bash
#  2) Conectar ao BUREAU (depois do assistente e de criar a chave de API):
#                 curl -fsSL https://raw.githubusercontent.com/Merlin262/bureau/main/scripts/jellyfin.sh | sudo bash -s -- --key SUA_CHAVE
#
#  Pastas: /srv/media/{filmes,series,musicas} (seus arquivos) · /srv/jellyfin (config e cache)
# ──────────────────────────────────────────────────────────────
set -euo pipefail

R=$'\e[38;5;196m'; W=$'\e[1;97m'; D=$'\e[2m'; N=$'\e[0m'
say() { printf '%s■%s %s\n' "$R" "$N" "$*"; }
ok()  { printf '  %s✓%s %s\n' "$W" "$N" "$*"; }
die() { printf '%s▲ ERRO:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Rode com sudo."

KEY=""
[[ "${1:-}" == "--key" ]] && KEY="${2:-}"

# ── Modo 2: conectar ao BUREAU ────────────────────────────────
if [[ -n "$KEY" ]]; then
  CONF=/etc/bureau/bureau.json
  [[ -f "$CONF" ]] || die "BUREAU não encontrado ($CONF)."
  KEY=$(printf '%s' "$KEY" | tr -d '[:space:]"')
  # Formato atual (Authorization: MediaBrowser Token=…); versões novas não aceitam mais o X-Emby-Token.
  code=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: MediaBrowser Token=\"$KEY\"" http://127.0.0.1:8096/System/Info || true)
  [[ "$code" == "200" ]] || code=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $KEY" http://127.0.0.1:8096/System/Info || true)
  [[ "$code" == "200" ]] || die "O Jellyfin recusou a chave (HTTP $code). Confira em Painel → Chaves de API (copie a chave inteira)."
  JF_KEY="$KEY" python3 - "$CONF" <<'PY'
import json, os, sys
p = sys.argv[1]
c = json.load(open(p))
c["Jellyfin"] = {"Enabled": True, "Url": "http://127.0.0.1:8096", "ApiKey": os.environ["JF_KEY"]}
json.dump(c, open(p, "w"), indent=2)
PY
  systemctl restart bureau
  ok "Jellyfin conectado ao BUREAU — veja em Apps."
  exit 0
fi

# ── Modo 1: instalar ──────────────────────────────────────────
command -v docker >/dev/null || die "Docker não encontrado (sudo apt install -y docker.io)."

OWNER="${SUDO_USER:-root}"
UID_N=$(id -u "$OWNER"); GID_N=$(id -g "$OWNER")
BASE=/srv/jellyfin
MEDIA=/srv/media
IP=$(hostname -I | awk '{print $1}')

say "Preparando pastas (dono: $OWNER)"
install -d -o "$UID_N" -g "$GID_N" "$BASE/config" "$BASE/cache" "$MEDIA" "$MEDIA/filmes" "$MEDIA/series" "$MEDIA/musicas"
ok "$MEDIA/{filmes,series,musicas}"

# Aceleração de vídeo (Intel Quick Sync / VAAPI), se o processador tiver.
HW=()
if [[ -d /dev/dri ]]; then
  HW+=(--device /dev/dri:/dev/dri)
  for g in render video; do
    gid=$(getent group "$g" | cut -d: -f3 || true)
    [[ -n "$gid" ]] && HW+=(--group-add "$gid")
  done
  ok "Aceleração de vídeo por hardware disponível (/dev/dri)"
fi

say "Baixando e iniciando o Jellyfin (pode levar alguns minutos)"
docker rm -f jellyfin >/dev/null 2>&1 || true
docker run -d --name jellyfin --restart unless-stopped \
  --user "$UID_N:$GID_N" \
  -p 8096:8096 -p 7359:7359/udp \
  -v "$BASE/config:/config" -v "$BASE/cache:/cache" -v "$MEDIA:/media" \
  "${HW[@]}" \
  -e "JELLYFIN_PublishedServerUrl=http://$IP:8096" \
  jellyfin/jellyfin:latest >/dev/null

for _ in $(seq 1 60); do
  curl -fs -o /dev/null http://127.0.0.1:8096/health && break
  sleep 2
done
curl -fs -o /dev/null http://127.0.0.1:8096/health || die "O Jellyfin não respondeu. Veja: docker logs jellyfin"
ok "Jellyfin no ar"

cat <<EOF

${W}Próximos passos:${N}
  1. Abra ${W}http://$IP:8096${N} e siga o assistente (crie seu usuário).
     Em "Bibliotecas", adicione as pastas: /media/filmes, /media/series, /media/musicas
     Em "Codificação" (Painel → Reprodução), escolha ${W}Intel QuickSync (QSV)${N} ou ${W}VAAPI${N}.
  2. Crie a chave para o BUREAU: Painel → Chaves de API → + (nome: BUREAU).
  3. Conecte ao BUREAU:
     curl -fsSL https://raw.githubusercontent.com/Merlin262/bureau/main/scripts/jellyfin.sh | sudo bash -s -- --key SUA_CHAVE

  ${D}Coloque seus arquivos em $MEDIA (ex.: pelo scp ou WinSCP, como usuário $OWNER).${N}
EOF
