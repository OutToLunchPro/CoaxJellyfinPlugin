#!/usr/bin/env bash
# Merge one version entry into the committed root manifest.json (the file clients add
# under Dashboard → Plugins → Repositories). Prepends the new version and drops any
# pre-existing entry with the same version, so re-runs are idempotent and history is kept.
#
# Usage: scripts/update-manifest.sh <version> <targetAbi> <sourceUrl> <zipPath> [changelog]
set -euo pipefail

VERSION="$1"
ABI="$2"
SOURCE="$3"
ZIP="$4"
CHANGELOG="${5:-}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANIFEST="$ROOT/manifest.json"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# macOS ships `md5 -q`; Linux/CI ships `md5sum`. Must match the published zip exactly.
if command -v md5 >/dev/null 2>&1; then
  CHECKSUM="$(md5 -q "$ZIP")"
else
  CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
fi

tmp="$(mktemp)"
jq \
  --arg version   "$VERSION" \
  --arg abi       "$ABI" \
  --arg source    "$SOURCE" \
  --arg checksum  "$CHECKSUM" \
  --arg changelog "$CHANGELOG" \
  --arg timestamp "$TIMESTAMP" \
  '.[0].versions = ([{
      version: $version, targetAbi: $abi, sourceUrl: $source,
      checksum: $checksum, changelog: $changelog, timestamp: $timestamp
    }] + (.[0].versions | map(select(.version != $version))))' \
  "$MANIFEST" > "$tmp"
mv "$tmp" "$MANIFEST"

echo "manifest: added $VERSION (md5 $CHECKSUM)"
