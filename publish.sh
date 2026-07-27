#!/bin/bash
# Stages a clean copy of the mod and, optionally, uploads it to Steam Workshop and GitHub.
#
# Why this exists: RimWorld's in-game uploader does not package a curated file list. Verse.Steam.
# Workshop.SetWorkshopItemDataFrom ends with SteamUGC.SetItemContent(handle, hook.Directory.FullName)
# — the mod's directory root, recursively, with no filter and no opt-out. This repo *is* that
# directory (the RimWorld Mods entry is a symlink to it), so an in-game upload publishes Source/,
# Tests/, Tools/, TestMod/, DESIGN.md, the .pdb's absolute build paths, and .git/ to every
# subscriber. Staging into dist/ and pointing Steam's contentfolder there instead is the only way to
# control what actually ships.
set -euo pipefail
cd "$(dirname "$0")"

APPID=294100
DIST=dist/CelestialLighting
VDF=dist/workshop.vdf

# The whole mod. Everything else in this repo is development scaffolding.
#
# LICENSE ships even though RimWorld never reads it: MIT's one obligation is that the notice
# accompany copies of the software, and a subscriber's mod folder IS their copy. About.xml only
# *names* MIT and links the repo, which is discoverability, not the notice itself.
#
# Deliberately absent:
#   1.6/Assemblies/CelestialLighting.pdb — a portable PDB embeds source file *paths* (not source),
#       so shipping it publishes /home/deck/Developer/... to subscribers for no user benefit.
#   About/PreviewBig.png — 2.1MB, and RimWorld only ever reads About/Preview.png. It's the source
#       art for the Steam page, uploaded through the browser, not mod content.
#   0Harmony.dll — never built (the csproj sets ExcludeAssets="runtime"); we hard-depend on the
#       standalone brrainz.harmony mod rather than bundling a second copy into the AppDomain.
MANIFEST=(
  "About/About.xml"
  "About/Preview.png"
  "About/PublishedFileId.txt"
  "1.6/Assemblies/CelestialLighting.dll"
  "LICENSE"
)

# Directory names RimWorld treats as loadable mod content. If one of these appears in the repo but
# no MANIFEST entry draws from it, someone added real content and did not update the whitelist —
# which would silently publish a mod missing its defs or textures. Cheap guard against a failure
# mode that is invisible until users report it.
CONTENT_DIRS=(Defs Textures Sounds Patches Languages Sprites AssetBundles)

STEAM=0
GITHUB=0
BUILD=1
DRYRUN=0
TAG=""
NOTE=""

usage() {
  cat <<'EOF'
Usage: ./publish.sh [options]

  (no options)      Build and stage into dist/ only. Uploads nothing.
  --steam           Push dist/ to the Steam Workshop item in About/PublishedFileId.txt.
                    Requires STEAM_USER; run `steamcmd +login "$STEAM_USER"` by hand once first so
                    Steam Guard caches credentials, otherwise this hangs on a prompt.
  --github <tag>    Zip dist/ and attach it to a GitHub release for <tag> (e.g. v1.2.0).
  --note <text>     Steam change note. Defaults to the current commit subject.
  --no-build        Stage the existing 1.6/Assemblies/CelestialLighting.dll instead of rebuilding.
  --dry-run         Do all the staging and write dist/workshop.vdf, but print the upload commands
                    instead of running them. Both uploads are hard to walk back, so this is the
                    intended way to inspect exactly what would ship.
  -h, --help        This.
EOF
}

fail() { echo "publish: $1" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --steam)    STEAM=1; shift ;;
    --github)   GITHUB=1; TAG="${2:-}"; [ -n "$TAG" ] || fail "--github needs a tag"; shift 2 ;;
    --note)     NOTE="${2:-}"; [ -n "$NOTE" ] || fail "--note needs text"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --dry-run)  DRYRUN=1; shift ;;
    -h|--help)  usage; exit 0 ;;
    *)          usage >&2; fail "unknown option: $1" ;;
  esac
done

# --- checks -----------------------------------------------------------------------------------

# A version directory in the repo that About.xml does not declare (or vice versa) means the mod
# either ships assemblies RimWorld will not load, or claims support for a version it has no
# assemblies for. Both are silent at upload time and loud in the user's log.
check_versions() {
  local declared shipped d
  declared=$(grep -oP '(?<=<li>)[0-9]+\.[0-9]+(?=</li>)' About/About.xml | sort -u | tr '\n' ' ')
  # Plain globbing rather than `find -regex`, whose dialect varies by findutils build. The -d test
  # also swallows the unmatched-glob literal when there are no version directories at all.
  shipped=$(for d in [0-9]*.[0-9]*/; do
    if [ -d "$d" ]; then echo "${d%/}"; fi
  done | sort -u | tr '\n' ' ')
  if [ "$declared" != "$shipped" ]; then
    fail "About.xml declares versions [${declared% }] but the repo has assembly dirs [${shipped% }]"
  fi
}

# See CONTENT_DIRS above.
check_content_dirs() {
  local dir
  for dir in "${CONTENT_DIRS[@]}"; do
    if [ -d "$dir" ] && ! printf '%s\n' "${MANIFEST[@]}" | grep -q "^$dir/"; then
      fail "$dir/ exists but no MANIFEST entry ships it — update MANIFEST in this script"
    fi
  done
}

check_content_dirs

# --- build and stage --------------------------------------------------------------------------

if [ "$BUILD" -eq 1 ]; then
  echo "publish: building Release"
  ./build.sh >/dev/null
fi

# After the build, not before: 1.6/Assemblies/ is gitignored, so a fresh worktree has no version
# directory at all until the compiler emits one.
check_versions

rm -rf dist
mkdir -p "$DIST"

for entry in "${MANIFEST[@]}"; do
  [ -f "$entry" ] || fail "missing from the working tree: $entry"
  [ -s "$entry" ] || fail "empty, refusing to ship: $entry"
  mkdir -p "$DIST/$(dirname "$entry")"
  cp "$entry" "$DIST/$entry"
done

echo "publish: staged $(find "$DIST" -type f | wc -l) files, $(du -sh "$DIST" | cut -f1)"
(cd dist && find CelestialLighting -type f | sort | sed 's/^/  /')

# --- steam ---------------------------------------------------------------------------------------

push_steam() {
  local id note user
  id=$(tr -d '[:space:]' < About/PublishedFileId.txt)
  [ -n "$id" ] || fail "About/PublishedFileId.txt is empty — publish once in-game to mint an item id"

  # Only the real push needs the account name. A dry-run that demanded it would make the one safe way
  # to inspect a push harder to reach than the push itself, so the placeholder stands in and the
  # printed command shows the substitution rather than pretending a name was supplied.
  user="${STEAM_USER:-}"
  if [ -z "$user" ]; then
    [ "$DRYRUN" -eq 1 ] || fail "set STEAM_USER to your Steam account name"
    user='$STEAM_USER'
  fi

  note="${NOTE:-$(git log -1 --pretty=%s)}"

  # Only contentfolder and previewfile are set. SteamUGC applies exactly the fields set on an update
  # handle, so title, description and the "1.6" version tags RimWorld's own uploader wrote are left
  # untouched by this push. The flip side: nothing here ever *writes* tags, so when a new RimWorld
  # version ships, the version tag needs one in-game upload or a manual edit on the Workshop page.
  # Escape any quote in the note rather than let it terminate the VDF string early.
  cat > "$VDF" <<EOF
"workshopitem"
{
	"appid" "$APPID"
	"publishedfileid" "$id"
	"contentfolder" "$PWD/$DIST"
	"previewfile" "$PWD/$DIST/About/Preview.png"
	"changenote" "$(printf '%s' "$note" | sed 's/"/\\"/g')"
}
EOF

  if [ "$DRYRUN" -eq 1 ]; then
    echo "publish: [dry-run] wrote $VDF:"
    sed 's/^/  /' "$VDF"
    echo "publish: [dry-run] would run: steamcmd +login \"$user\" +workshop_build_item $PWD/$VDF +quit"
    return 0
  fi

  echo "publish: pushing item $id to Steam as $user"
  steamcmd +login "$user" +workshop_build_item "$PWD/$VDF" +quit
}

# --- github --------------------------------------------------------------------------------------

push_github() {
  local zip
  zip="$PWD/dist/CelestialLighting-$TAG.zip"
  (cd dist && zip -qr "$zip" CelestialLighting)

  # --generate-notes fills the body from commits since the previous tag. Creating the release also
  # creates the tag at HEAD if it does not exist yet.
  if [ "$DRYRUN" -eq 1 ]; then
    echo "publish: [dry-run] built $(basename "$zip") ($(du -h "$zip" | cut -f1))"
    echo "publish: [dry-run] would run: gh release create $TAG $zip --title $TAG --generate-notes"
    return 0
  fi

  echo "publish: creating GitHub release $TAG"
  gh release create "$TAG" "$zip" --title "$TAG" --generate-notes
}

if [ "$STEAM" -eq 1 ]; then
  push_steam
fi

if [ "$GITHUB" -eq 1 ]; then
  push_github
fi

if [ "$STEAM" -eq 0 ] && [ "$GITHUB" -eq 0 ]; then
  echo "publish: staged only — nothing uploaded. Pass --steam and/or --github <tag>."
fi
