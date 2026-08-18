#!/usr/bin/env bash
# Builds §27's custom shader into the three per-OS AssetBundles RimWorld can load, and drops them
# straight into 1.6/AssetBundles/ where ModAssetBundlesHandler looks.
#
# This is NOT part of ./build.sh and is not run by the pre-commit hook. The bundles are committed
# artifacts, rebuilt only when the .shader changes, because the alternative is making every clone of
# this repo need a 20 GB Unity install to compile a mod. See DESIGN.md §27.
#
# THREE THINGS FAIL SILENTLY HERE, all of them learned the hard way:
#
#  1. The bundle file must have NO EXTENSION and must end in _linux/_mac/_win. ModAssetBundlesHandler
#     .IsAcceptableExtension accepts only extensionless files, and GetBundleNameWithoutOsSpecifier
#     drops any bundle whose suffix does not match the running OS. A ".bundle" on the end means the
#     file is skipped with no log line at all.
#  2. The in-bundle asset path must be Assets/Data/joof.celestiallighting/Materials/<name>.shader.
#     ContentFinder tries the mod's FolderName first and its PackageIdPlayerFacing second — and
#     FolderName is "CelestialLighting" in this dev checkout but a numeric Workshop ID for every
#     subscriber, so only the packageId form works for both.
#  3. All three targets have to ship. Compiled shader variants are per graphics API, so building only
#     the host OS produces a mod that works here and is broken for nearly everyone else.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

unity="${UNITY:-$HOME/Unity/Hub/Editor/2022.3.35f1/Editor/Unity}"

if [[ ! -x "$unity" ]]; then
    echo "Unity 2022.3.35f1 not found at $unity — set UNITY=<path to the Unity binary>." >&2
    echo "The version is not a preference: RimWorld 1.6 runs 2022.3.35f1, and a bundle built by a" >&2
    echo "different editor loads as null with no error." >&2
    exit 1
fi

# Unity's headless mode dlopen()s libxml2.so.2, which this box has only inside the nix store rather
# than on the default loader path. Without it the editor dies during startup with a bare exit code
# and an empty log, which reads exactly like a licensing failure.
if ! ldconfig -p 2>/dev/null | grep -q 'libxml2\.so\.2'; then
    libxml2dir="$(dirname "$(ls -1 /nix/store/*/lib/libxml2.so.2 2>/dev/null | head -1 || true)" 2>/dev/null || true)"

    if [[ -n "$libxml2dir" && -d "$libxml2dir" ]]; then
        export LD_LIBRARY_PATH="$libxml2dir:${LD_LIBRARY_PATH:-}"
    fi
fi

out="$repo/1.6/AssetBundles"
log="${BUNDLE_LOG:-$(mktemp -t shaderbundle-XXXXXX.log)}"

export BUNDLE_OUT="$out"
export BUNDLE_ASSET="Assets/Data/joof.celestiallighting/Materials/VectorLightMax.shader"
export BUNDLE_NAME="celestiallighting"

mkdir -p "$out"

"$unity" -batchmode -nographics -quit \
    -projectPath "$here" \
    -executeMethod BuildShaderBundles.Build \
    -logFile "$log"

if ! grep -q ALL_BUNDLES_OK "$log"; then
    echo "Unity exited without reporting ALL_BUNDLES_OK — see $log" >&2
    exit 1
fi

# BuildAssetBundles also writes a manifest per bundle plus one named after the output directory.
# None of it is read by RimWorld, and a stray .manifest sitting in AssetBundles/ is an extensioned
# file that ModAssetBundlesHandler skips, so it is noise rather than a hazard — but it is committed
# noise, so it goes.
rm -f "$out"/*.manifest "$out/AssetBundles"

for suffix in _linux _win _mac; do
    if [[ ! -s "$out/$BUNDLE_NAME$suffix" ]]; then
        echo "Missing or empty bundle: $out/$BUNDLE_NAME$suffix — see $log" >&2
        exit 1
    fi
done

ls -l "$out"
echo "Built ${BUNDLE_NAME}_linux/_win/_mac from $BUNDLE_ASSET"
