#!/usr/bin/env bash
# Build the Coax plugin against the Jellyfin 12 (net10.0) assemblies and refresh the
# drop-in bundle under dist/Coax_<version>/. Run from anywhere: ./build.sh
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
# The bin/ subfolder follows the csproj's TargetFramework, so a framework bump can't leave
# the bundle copying a stale dll out of the previous framework's folder.
TFM="$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' "$PROJ/Jellyfin.Plugin.Coax.csproj")"
OUT="$PROJ/bin/Release/$TFM"
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
# The installed plugin's page in the dashboard shows the file meta.json's imagePath names.
cp "$ROOT/image.png" "$DIST/"

# Repository zip: Jellyfin extracts its contents straight into the plugin dir,
# so the dll, meta.json and image.png must sit at the zip root (no enclosing folder).
rm -f "$ZIP"
( cd "$DIST" && zip -q -r -X "$ZIP" . )

# Packaging guard: fail the build if the zip lacks the dll, meta.json, or the image that
# meta.json's imagePath names, so the artwork can't silently drop out of a release.
ENTRIES="$(unzip -Z1 "$ZIP")"
require_entry() {
  grep -qxF "$1" <<<"$ENTRIES" || { echo "error: $ZIP has no $1" >&2; exit 1; }
}
require_entry Jellyfin.Plugin.Coax.dll
require_entry meta.json
IMAGE_PATH="$(unzip -p "$ZIP" meta.json | jq -r '.imagePath // empty')"
[ -n "$IMAGE_PATH" ] || { echo "error: meta.json in $ZIP has no imagePath" >&2; exit 1; }
require_entry "$IMAGE_PATH"

# macOS ships `md5 -q`; Linux/CI ships `md5sum`.
if command -v md5 >/dev/null 2>&1; then
  CHECKSUM="$(md5 -q "$ZIP")"
else
  CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
fi

# manifest.json: a single-plugin repository so the dashboard resolves the plugin
# by GUID (kills the "error getting plugin details from the repository" message)
# and offers in-app updates. Add this file's URL under Dashboard → Plugins → Repositories.
# The catalog card's picture is the same imageUrl the committed root manifest.json publishes.
IMAGE_URL="$(jq -r '.[0].imageUrl // ""' "$ROOT/manifest.json")"
jq -n \
  --arg guid       "$(jq -r .guid "$META")" \
  --arg name       "$(jq -r .name "$META")" \
  --arg desc       "$(jq -r .description "$META")" \
  --arg overview   "$(jq -r .overview "$META")" \
  --arg owner      "$(jq -r .owner "$META")" \
  --arg category   "$(jq -r .category "$META")" \
  --arg image      "$IMAGE_URL" \
  --arg version    "$VERSION" \
  --arg abi        "$(jq -r .targetAbi "$META")" \
  --arg source     "$SOURCE_BASE_URL/Coax_$VERSION.zip" \
  --arg checksum   "$CHECKSUM" \
  --arg timestamp  "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '[{
     guid: $guid, name: $name, description: $desc, overview: $overview,
     owner: $owner, category: $category, imageUrl: $image,
     versions: [{
       version: $version, targetAbi: $abi, sourceUrl: $source,
       checksum: $checksum, changelog: "", timestamp: $timestamp
     }]
   }]' > "$ROOT/dist/manifest.json"

echo "bundle:   $DIST"
echo "zip:      $ZIP  (md5 $CHECKSUM)"
echo "manifest: $ROOT/dist/manifest.json  (sourceUrl base: $SOURCE_BASE_URL)"
ls -la "$DIST"
