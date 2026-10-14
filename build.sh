#!/usr/bin/env bash
# Compila o BUREAU a partir do código-fonte (requer .NET 10 SDK, Node 20+, clang e zlib1g-dev).
# Uso: ./build.sh [linux-x64|linux-arm64]   → depois: sudo ./install.sh --local dist/<rid>
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-$( [[ "$(uname -m)" == "aarch64" ]] && echo linux-arm64 || echo linux-x64 )}"

(cd web && npm ci && npm run build)
dotnet publish server/Bureau.csproj -c Release -r "$RID" -o "dist/$RID"
rm -f "dist/$RID"/*.dbg "dist/$RID"/*.pdb

echo
echo "■ Pronto: dist/$RID  ($(du -sh "dist/$RID" | cut -f1))"
echo "  Instale com: sudo ./install.sh --local dist/$RID"
