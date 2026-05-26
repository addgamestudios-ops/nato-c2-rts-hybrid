#!/usr/bin/env bash
# =====================================================================
#  NATO C2 RTS Hybrid — reimport-sample.sh
#  ---------------------------------------------------------------------
#  Copies the current Samples~/DemoScene/ source into the user's live
#  Unity project at ~/Desktop/NATO_C2_Local/Assets/Samples/...
#
#  Why we need this: the Runtime/ files are picked up automatically by
#  Unity because the package is linked via file:// in manifest.json.
#  Sample files are NOT auto-linked — Unity COPIES them into Assets/
#  on first import, so updates to the source don't propagate until we
#  do this copy (or the user clicks "Reimport" in the Package Manager).
#
#  After this runs, switch to Unity. The Editor will detect the file
#  changes and recompile within a few seconds.
# =====================================================================

set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/Samples~/DemoScene"
DST_BASE="$HOME/Desktop/NATO_C2_Local/Assets/Samples/NATO C2 RTS Hybrid"

if [ ! -d "$SRC" ]; then
  echo "ERROR: source dir not found: $SRC" >&2
  exit 1
fi
if [ ! -d "$DST_BASE" ]; then
  echo "ERROR: Unity project not found at $DST_BASE" >&2
  echo "Open the package via Package Manager first to populate Assets/Samples/." >&2
  exit 1
fi

# Find the version folder (e.g. 0.1.0). Glob picks the first match.
shopt -s nullglob
VERSIONS=( "$DST_BASE"/*/ )
if [ ${#VERSIONS[@]} -eq 0 ]; then
  echo "ERROR: no version folder under $DST_BASE" >&2
  exit 1
fi
DST_VER="${VERSIONS[0]}"
echo "[reimport] version dir:   $DST_VER"

# Find the DemoScene subfolder (Unity decorates it: "DemoScene (12 Drones + 7 Tanks)").
DEMOS=( "$DST_VER"DemoScene* "$DST_VER"Demo\ Scene* )
DST=""
for d in "${DEMOS[@]}"; do
  if [ -d "$d" ]; then DST="$d"; break; fi
done
if [ -z "$DST" ]; then
  echo "ERROR: no DemoScene folder under $DST_VER" >&2
  exit 1
fi
echo "[reimport] destination:   $DST"
echo "[reimport] source:        $SRC"

# Copy every .cs file. Use rsync to preserve mtime + only update changed.
rsync -av --include="*.cs" --exclude="*" "$SRC/" "$DST/"

echo
echo "[reimport] Done. Switch to Unity — it will recompile within a few seconds."
echo "[reimport] If the Console shows red, ask Claude to read it via the MCP."

# Tests are now auto-discovered from the package via Packages/manifest.json
# `testables`. We used to copy Tests/Editor into Assets/Tests/Editor but that
# created a duplicate-asmdef compile error ("Assembly with name
# 'NATO.C2.Tests.Editor' already exists"). If you have an old Assets/Tests
# folder lingering from a prior run, the block below removes it.
OLD_TESTS_DST="$HOME/Desktop/NATO_C2_Local/Assets/Tests"
if [ -d "$OLD_TESTS_DST" ]; then
  rm -rf "$OLD_TESTS_DST" "$OLD_TESTS_DST.meta"
  echo "[reimport] Removed legacy Assets/Tests duplicate (package now auto-discovers tests)"
fi
echo "[reimport]   Open Unity → Window → General → Test Runner → EditMode tab → Run All"
