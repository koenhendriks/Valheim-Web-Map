#!/bin/sh
# Builds the plugin and packs:
#   dist/ValheimWebMap-<version>.zip          plain BepInEx plugin folder layout (GitHub release)
#   dist/thunderstore/koenhendriks-ValheimWebMap-<version>.zip   Thunderstore package (needs tcli)
# Point VALHEIM_INSTALL at a Valheim client or dedicated server folder when it is not auto-detected.
set -eu
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ValheimWebMap/ValheimWebMap.csproj)
for arg in "$@"; do case "$arg" in -p:Version=*) VERSION="${arg#-p:Version=}" ;; esac; done
dotnet build src/ValheimWebMap/ValheimWebMap.csproj -c Release -nologo -v q "$@"

rm -rf dist/ValheimWebMap dist/thunderstore
mkdir -p dist/ValheimWebMap/plugins/ValheimWebMap
cp src/ValheimWebMap/bin/Release/ValheimWebMap.dll dist/ValheimWebMap/plugins/ValheimWebMap/
cp README.md LICENSE dist/ValheimWebMap/

(cd dist/ValheimWebMap && rm -f "../ValheimWebMap-$VERSION.zip" && zip -qr "../ValheimWebMap-$VERSION.zip" .)
echo "dist/ValheimWebMap-$VERSION.zip"

# Release notes double as the package changelog. Outside a git checkout there is nothing to list.
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  { printf '# %s\n\n' "$VERSION"; .github/changelog.sh "$VERSION" "https://github.com/koenhendriks/Valheim-Web-Map"; } > dist/CHANGELOG.md
else
  printf '# %s\n\nSee https://github.com/koenhendriks/Valheim-Web-Map/releases\n' "$VERSION" > dist/CHANGELOG.md
fi

if command -v tcli >/dev/null 2>&1; then
  tcli build --package-version "$VERSION" >/dev/null
  ls dist/thunderstore/*.zip
else
  echo "tcli not found; skipping the Thunderstore package (dotnet tool install -g tcli)" >&2
fi
