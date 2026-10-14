#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

# Builds both game versions, from two different sources:
#
#   1.7/Assemblies  <- this working tree, net10.0 (RimWorld 1.7, Waylith on .NET 10).
#   1.6/Assemblies  <- the release/1.6 branch, net481 (RimWorld 1.6, Unity on Mono).
#
# 1.6 is FROZEN: main no longer compiles for net481, so the 1.6 DLL is built from the branch that
# still does rather than committed as a binary. That keeps the shipped 1.6 assembly reproducible from
# source, and a backport is "commit to release/1.6, rerun this" with no copying by hand.
#
# The branch is exported with `git archive` into .build-1.6/ (gitignored) rather than checked out as
# a worktree: an archive is exactly the committed tree, so an uncommitted edit lying around in some
# release/1.6 worktree can never leak into what ships, and nothing registers in `git worktree list`.
# A stamp file holds the exported commit so an unchanged branch rebuilds incrementally.
#
# Arguments are passed to the 1.7 build only. SKIP_1_6=1 skips the 1.6 build (e.g. the pre-commit
# hook's inner loop, where 1.6 cannot have changed).
#
# Needs the .NET 10 SDK, kept in its own ~/.dotnet10 rather than ~/.dotnet so it does not become the
# default SDK for every other mod's build. SDK 10 still builds net481 against the Mono reference
# assemblies via FrameworkPathOverride.
DOTNET=/home/deck/.dotnet10/dotnet

(cd Source && "$DOTNET" build -c Release "$@")

if [ "${SKIP_1_6:-0}" = "1" ]; then
  exit 0
fi

# Local branch first, then the remote one, so a fresh clone that never checked release/1.6 out
# still builds.
ref=""
for candidate in release/1.6 origin/release/1.6; do
  if [ -z "$ref" ] && git rev-parse --verify --quiet "$candidate^{commit}" >/dev/null; then
    ref=$candidate
  fi
done
if [ -z "$ref" ]; then
  echo "build: no release/1.6 branch (local or origin) to build the frozen 1.6 assembly from" >&2
  exit 1
fi

frozen=.build-1.6
commit=$(git rev-parse "$ref^{commit}")
if [ "$(cat "$frozen/.commit" 2>/dev/null)" != "$commit" ]; then
  rm -rf "$frozen"
  mkdir -p "$frozen"
  git archive "$commit" | tar -x -C "$frozen"
  echo "$commit" > "$frozen/.commit"
fi

(cd "$frozen/Source" && FrameworkPathOverride=/usr/lib/mono/4.8-api "$DOTNET" build -c Release -f net481)

mkdir -p 1.6/Assemblies
cp "$frozen/1.6/Assemblies/CelestialLighting.dll" "$frozen/1.6/Assemblies/CelestialLighting.pdb" 1.6/Assemblies/
echo "build: 1.6/Assemblies from $ref ($(git rev-parse --short "$commit"))"
