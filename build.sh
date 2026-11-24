#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"
VERSION="1.0.0.0"
ZIP_NAME="imdb-smart-lookup_${VERSION}.zip"
STAGE_DIR="dist/stage"

echo "==> Building Jellyfin.Plugin.ImdbSmartLookup (${VERSION})..."
dotnet build -c Release

rm -rf "${STAGE_DIR}" "dist/${ZIP_NAME}"
mkdir -p "${STAGE_DIR}"
cp "bin/Release/net10.0/Jellyfin.Plugin.ImdbSmartLookup.dll" "${STAGE_DIR}/"
cp "meta.json" "${STAGE_DIR}/"

(cd "${STAGE_DIR}" && zip -q -r "../${ZIP_NAME}" Jellyfin.Plugin.ImdbSmartLookup.dll meta.json)
MD5=$(md5 -q "dist/${ZIP_NAME}" 2>/dev/null || md5sum "dist/${ZIP_NAME}" | awk '{print $1}')
echo "==> Built: dist/${ZIP_NAME}"
echo "==> MD5:   ${MD5}"
