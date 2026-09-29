#!/bin/sh
# Builds the plugin and packs dist/ValheimWebMap-<version>.zip (BepInEx plugin folder layout).
# Point VALHEIM_INSTALL at a Valheim client or dedicated server folder when it is not auto-detected.
set -eu
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ValheimWebMap/ValheimWebMap.csproj)
for arg in "$@"; do case "$arg" in -p:Version=*) VERSION="${arg#-p:Version=}" ;; esac; done
dotnet build src/ValheimWebMap/ValheimWebMap.csproj -c Release -nologo -v q "$@"

rm -rf dist/ValheimWebMap
mkdir -p dist/ValheimWebMap/plugins/ValheimWebMap
cp src/ValheimWebMap/bin/Release/ValheimWebMap.dll dist/ValheimWebMap/plugins/ValheimWebMap/
cp README.md LICENSE dist/ValheimWebMap/

(cd dist/ValheimWebMap && rm -f "../ValheimWebMap-$VERSION.zip" && zip -qr "../ValheimWebMap-$VERSION.zip" .)
echo "dist/ValheimWebMap-$VERSION.zip"
