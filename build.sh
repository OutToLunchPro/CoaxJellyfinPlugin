#!/usr/bin/env bash
# Build the Coax plugin against the Jellyfin 10.11 (net9.0) assemblies and refresh the
# drop-in bundle under dist/Coax_1.0.0.0/. Run from anywhere: ./build.sh
set -euo pipefail

# Prefer an explicit DOTNET_BIN, then Homebrew's .NET (local dev on macOS, where the
# Homebrew SDK is shadowed on PATH by dotnet@8), then whatever `dotnet` is on PATH (CI).
DOTNET_BIN="${DOTNET_BIN:-}"
if [ -z "$DOTNET_BIN" ]; then
  if [ -x "/opt/homebrew/opt/dotnet/bin/dotnet" ]; then
    DOTNET_BIN="/opt/homebrew/opt/dotnet/bin/dotnet"
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
  else
    DOTNET_BIN="dotnet"
  fi
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/Jellyfin.Plugin.Coax"
OUT="$PROJ/bin/Release/net9.0"
META="$PROJ/meta.json"

VERSION="$(jq -r .version "$META")"
DIST="$ROOT/dist/Coax_$VERSION"
ZIP="$ROOT/dist/Coax_$VERSION.zip"

# Base URL where Coax_<version>.zip will be hosted. Override per environment:
#   COAX_SOURCE_BASE_URL=https://my.host/jellyfin ./build.sh
# manifest.json's sourceUrl must resolve to the published zip, or installs fail.
SOURCE_BASE_URL="${COAX_SOURCE_BASE_URL:-https://CHANGE_ME.example/jellyfin}"

"$DOTNET_BIN" build "$PROJ/Jellyfin.Plugin.Coax.csproj" -c Release "$@"

mkdir -p "$DIST"
cp "$OUT/Jellyfin.Plugin.Coax.dll" "$DIST/"
cp "$OUT/Jellyfin.Plugin.Coax.pdb" "$DIST/" 2>/dev/null || true
cp "$META" "$DIST/"

# Repository zip: Jellyfin extracts its contents straight into the plugin dir,
# so the dll/meta.json must sit at the zip root (no enclosing folder).
rm -f "$ZIP"
( cd "$DIST" && zip -q -r -X "$ZIP" . )
# macOS ships `md5 -q`; Linux/CI ships `md5sum`.
if command -v md5 >/dev/null 2>&1; then
  CHECKSUM="$(md5 -q "$ZIP")"
else
  CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
fi

# manifest.json: a single-plugin repository so the dashboard resolves the plugin
# by GUID (kills the "error getting plugin details from the repository" message)
# and offers in-app updates. Add this file's URL under Dashboard → Plugins → Repositories.
jq -n \
  --arg guid       "$(jq -r .guid "$META")" \
  --arg name       "$(jq -r .name "$META")" \
  --arg desc       "$(jq -r .description "$META")" \
  --arg overview   "$(jq -r .overview "$META")" \
  --arg owner      "$(jq -r .owner "$META")" \
  --arg category   "$(jq -r .category "$META")" \
  --arg version    "$VERSION" \
  --arg abi        "$(jq -r .targetAbi "$META")" \
  --arg source     "$SOURCE_BASE_URL/Coax_$VERSION.zip" \
  --arg checksum   "$CHECKSUM" \
  --arg timestamp  "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '[{
     guid: $guid, name: $name, description: $desc, overview: $overview,
     owner: $owner, category: $category, imageUrl: "",
     versions: [{
       version: $version, targetAbi: $abi, sourceUrl: $source,
       checksum: $checksum, changelog: "", timestamp: $timestamp
     }]
   }]' > "$ROOT/dist/manifest.json"

echo "bundle:   $DIST"
echo "zip:      $ZIP  (md5 $CHECKSUM)"
echo "manifest: $ROOT/dist/manifest.json  (sourceUrl base: $SOURCE_BASE_URL)"
ls -la "$DIST"
