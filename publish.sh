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

# The parts of the mod that have to be named one by one, because nothing about their path marks
# them as shippable. Loadable *content* is not listed here — see CONTENT_DIRS below, which picks it
# up automatically. Everything else in this repo is development scaffolding.
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

# Directory names RimWorld treats as loadable mod content, whether they sit at the repo root or
# under a version directory (Defs/… and 1.6/Defs/… are both loaded). Every *tracked* file beneath
# one of these ships, automatically, whether or not it exists today — so adding a texture, a patch,
# or a second def file is just `git add`, with no edit here.
#
# It used to be the other way round: content had to be named in MANIFEST, and a guard was supposed
# to catch anyone who forgot. The guard tested `[ -d "$dir" ]` against these bare names at the repo
# root, but our content lives at 1.6/Defs/, so it matched nothing, passed vacuously, and v1.0.0
# shipped to the Workshop with no Defs/ at all. Subscribers got "Failed to find
# RimWorld.MapMeshFlagDef named CL_SunShadowAxis" and silently lost sun-shadow mesh invalidation —
# MapMeshFlagDef's implicit ulong cast is `def?.mask ?? 0`, so the null DefOf produced no exception
# to point at the cause. Nothing caught it locally either, because the dev install is a symlink to
# this repo and the running game therefore always sees the full tree, never the staged package.
#
# A whitelist you must remember to update is the wrong shape for content whose only job is to be
# loaded. Inclusion is now the default and the remaining guard (check_untracked_content) covers the
# one way content can still be missed: existing on disk but never committed.
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

# Files under a CONTENT_DIRS directory, at the repo root or under a version directory. $1 selects
# which git listing to draw from: --cached for tracked files (what ships), or
# "--others --exclude-standard" for files present but never committed.
#
# The trailing /* on each pathspec is required. A pathspec containing a wildcard is fnmatch'd
# against the whole path, which drops git's usual "naming a directory means everything under it"
# shorthand — `*/Defs` matches literally nothing, and any check built on it passes while seeing no
# files at all. That is a quieter version of the same bug the old guard had, so it is spelled out
# here rather than left to be rediscovered.
content_files() {
  local dir
  for dir in "${CONTENT_DIRS[@]}"; do
    git ls-files $1 -- "$dir/*" "*/$dir/*"
  done | sort -u
}

# Content is shipped from git, so a file that exists on disk but was never committed is invisible to
# this script and would ship as a missing def or a missing texture — the same class of silent
# breakage as the manifest omission, arriving by a different route. Untracked content is far more
# likely to be a forgotten `git add` than a deliberate exclusion, so it stops the release.
check_untracked_content() {
  local untracked
  untracked=$(content_files "--others --exclude-standard")
  if [ -n "$untracked" ]; then
    fail "untracked mod content — git add it or ignore it, it cannot ship as-is:
$(printf '%s\n' "$untracked" | sed 's/^/  /')"
  fi
}

check_untracked_content

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

stage() {
  local entry="$1"
  [ -f "$entry" ] || fail "missing from the working tree: $entry"
  [ -s "$entry" ] || fail "empty, refusing to ship: $entry"
  mkdir -p "$DIST/$(dirname "$entry")"
  cp "$entry" "$DIST/$entry"
}

for entry in "${MANIFEST[@]}"; do
  stage "$entry"
done

# Content is discovered rather than listed (see CONTENT_DIRS), and staged file by file rather than
# by copying whole directories, so that anything sitting in a content directory untracked — a
# scratch texture, an editor backup — cannot ride along into a release. check_untracked_content has
# already refused the run if any such file exists, but staging from the same tracked listing means
# the two can never disagree about what "content" means.
while read -r entry; do
  if [ -n "$entry" ]; then
    stage "$entry"
  fi
done <<< "$(content_files --cached)"

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
