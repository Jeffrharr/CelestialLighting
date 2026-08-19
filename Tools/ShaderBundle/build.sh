#!/usr/bin/env bash
# Builds the mod's shader AssetBundle with the Unity version RimWorld itself was built in.
#
# WHY THE SHADER SOURCE LIVES INSIDE THE UNITY PROJECT rather than in a tidy Shaders/ directory with
# a copy taken at build time: RimWorld resolves a mod shader by its path INSIDE the bundle, which
# must be Assets/Data/<packageId>/Materials/<name>.shader, so Unity has to see it at exactly that
# path anyway. A canonical copy elsewhere would be a second file to keep in step with the one that
# actually ships, for no gain — and the failure mode of letting them drift is that the game keeps
# drawing the old shader while every test passes on the new one.
#
# The bundle is COMMITTED to the repo (1.6/AssetBundles/) rather than built on demand: subscribers
# get a zip, not a toolchain, and building it needs a 6 GB editor install this repo has no business
# assuming. Rebuild it whenever the shader changes and commit the result in the same change.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
unity="$HOME/Unity/Hub/Editor/2022.3.35f1/Editor/Unity"

if [[ ! -x "$unity" ]]; then
  echo "Unity 2022.3.35f1 not found at $unity" >&2
  echo "RimWorld 1.6 bundles must be built in that exact version; install it through Unity Hub." >&2
  exit 1
fi

# SteamOS ships libxml2 with soname .16; Unity wants the legacy .2. Do NOT symlink one to the other
# — the soname bump dropped symbols — point at a copy that still provides .2 instead.
legacy_xml2=/nix/store/pa303d9qsmx8g4gymcjbk931i8cwfrm7-libxml2-2.13.8/lib
if [[ -e "$legacy_xml2/libxml2.so.2" ]]; then
  export LD_LIBRARY_PATH="$legacy_xml2:${LD_LIBRARY_PATH:-}"
fi

# The Hub keeps its licence inside the flatpak sandbox, where the standalone editor cannot see it.
license_dir="$HOME/.config/unity3d/Unity/licenses"
flatpak_license="$HOME/.var/app/com.unity.UnityHub/config/unity3d/Unity/licenses/UnityEntitlementLicense.xml"
if [[ ! -e "$license_dir/UnityEntitlementLicense.xml" && -e "$flatpak_license" ]]; then
  mkdir -p "$license_dir"
  cp "$flatpak_license" "$license_dir/"
fi

log="$here/build.log"
rm -f "$log"

echo "Building shader bundles for linux, win and mac (about a minute cold)..."
"$unity" -batchmode -nographics -quit \
  -projectPath "$here/Project" \
  -executeMethod BuildShaderBundles.Build \
  -logFile "$log"

# All three or none. A missing platform is not a partial success: it is a silent fallback to the
# baked cloud for everybody on that OS, with nothing logged anywhere to say so.
mkdir -p "$repo/1.6/AssetBundles"
for suffix in linux win mac; do
  built="$here/Build/$suffix/celestiallighting_shaders_$suffix"
  if [[ ! -f "$built" ]]; then
    echo "Unity exited cleanly but produced no $suffix bundle; see $log" >&2
    echo "If the log says the target is not installed, add that platform's module through Unity Hub." >&2
    exit 1
  fi
  cp "$built" "$repo/1.6/AssetBundles/celestiallighting_shaders_$suffix"
  echo "Installed $(du -h "$repo/1.6/AssetBundles/celestiallighting_shaders_$suffix" | cut -f1) -> 1.6/AssetBundles/celestiallighting_shaders_$suffix"
done
