#!/usr/bin/env bash
# Build the Coax plugin against the Jellyfin 10.11 (net9.0) assemblies and refresh the
# drop-in bundle under dist/Coax_1.0.0.0/. Run from anywhere: ./build.sh
set -euo pipefail

# .NET 10 SDK (installed via `brew install dotnet`) — can target net9.0.
# It's shadowed on PATH by dotnet@8, so we invoke it by full path.
DOTNET_BIN="/opt/homebrew/opt/dotnet/bin/dotnet"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/Jellyfin.Plugin.Coax"
OUT="$PROJ/bin/Release/net9.0"
DIST="$ROOT/dist/Coax_1.0.0.0"

"$DOTNET_BIN" build "$PROJ/Jellyfin.Plugin.Coax.csproj" -c Release "$@"

mkdir -p "$DIST"
cp "$OUT/Jellyfin.Plugin.Coax.dll" "$DIST/"
cp "$OUT/Jellyfin.Plugin.Coax.pdb" "$DIST/" 2>/dev/null || true
cp "$PROJ/meta.json" "$DIST/"
echo "bundle: $DIST"
ls -la "$DIST"
