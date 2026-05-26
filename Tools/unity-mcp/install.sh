#!/usr/bin/env bash
# =====================================================================
#  NATO C2 Unity MCP — installer
#  ---------------------------------------------------------------------
#  Registers the Unity MCP server with Claude desktop.
#
#  macOS TCC sandboxing prevents Claude's subprocesses from reading
#  ~/Documents without an explicit grant in System Settings → Privacy &
#  Security → Files and Folders. To avoid forcing the user to fiddle
#  with that, we COPY server.py into ~/Library/Application Support/
#  Claude/unity-mcp/ — a location Claude already has access to — and
#  register THAT path in the Claude config.
#
#  Re-run this script any time you edit server.py in the source tree.
# =====================================================================

set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_SERVER="$SRC_DIR/server.py"

CONFIG_DIR="$HOME/Library/Application Support/Claude"
CONFIG_FILE="$CONFIG_DIR/claude_desktop_config.json"
INSTALL_DIR="$CONFIG_DIR/unity-mcp"
INSTALLED_SERVER="$INSTALL_DIR/server.py"

if [ ! -f "$SRC_SERVER" ]; then
  echo "ERROR: server.py not found at $SRC_SERVER" >&2
  exit 1
fi

# Find python3. macOS guarantees /usr/bin/python3 on every modern release.
PYTHON_BIN=""
for candidate in /usr/bin/python3 /opt/homebrew/bin/python3 /usr/local/bin/python3; do
  if [ -x "$candidate" ]; then PYTHON_BIN="$candidate"; break; fi
done
if [ -z "$PYTHON_BIN" ]; then PYTHON_BIN="$(command -v python3 || true)"; fi
if [ -z "$PYTHON_BIN" ] || [ ! -x "$PYTHON_BIN" ]; then
  echo "ERROR: could not locate python3. Install macOS Command Line Tools and re-run." >&2
  exit 1
fi
echo "[unity-mcp] python3:        $PYTHON_BIN"

# Copy the server script into a TCC-accessible location.
mkdir -p "$INSTALL_DIR"
cp "$SRC_SERVER" "$INSTALLED_SERVER"
chmod +x "$INSTALLED_SERVER"
echo "[unity-mcp] source:         $SRC_SERVER"
echo "[unity-mcp] installed copy: $INSTALLED_SERVER"

# Patch the Claude desktop config.
"$PYTHON_BIN" - "$CONFIG_FILE" "$INSTALLED_SERVER" "$PYTHON_BIN" <<'PY'
import json, os, sys

path, server, python_bin = sys.argv[1], sys.argv[2], sys.argv[3]

try:
    with open(path, "r") as f:
        cfg = json.load(f)
except (FileNotFoundError, json.JSONDecodeError):
    cfg = {}

cfg.setdefault("mcpServers", {})
existing = cfg["mcpServers"].get("unity")
new_entry = {"command": python_bin, "args": [server]}

if existing == new_entry:
    print(f"[unity-mcp] already registered (no changes) at {path}")
    sys.exit(0)

# Optional env vars — pull through from the operator's shell so a
# single-line set in their .zshrc / .bash_profile is enough to
# enable multi-instance routing or token auth.
env_passthrough = {}
for var in ("UNITY_MCP_INSTANCES", "UNITY_MCP_TOKEN",
            "SLACK_CHAOS_WEBHOOK_URL", "OTLP_ENDPOINT"):
    v = os.environ.get(var)
    if v: env_passthrough[var] = v
if env_passthrough:
    new_entry["env"] = env_passthrough

cfg["mcpServers"]["unity"] = new_entry

tmp = path + ".tmp"
with open(tmp, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
os.replace(tmp, path)

print(f"[unity-mcp] registered at {path}")
print(f"[unity-mcp] command:  {python_bin}")
print(f"[unity-mcp] server:   {server}")
PY

echo
echo "[unity-mcp] running self-test..."
if "$PYTHON_BIN" "$INSTALLED_SERVER" --self-test; then
  echo "[unity-mcp] self-test PASSED"
else
  echo "[unity-mcp] self-test FAILED — server is broken; do NOT relaunch Claude until fixed." >&2
  exit 1
fi
echo
echo "[unity-mcp] running doctor (install diagnostic)..."
"$PYTHON_BIN" "$INSTALLED_SERVER" --doctor || true
echo
echo "[unity-mcp] Done. Cmd+Q Claude desktop and reopen to activate."
echo "[unity-mcp] To re-check anytime:"
echo "             $PYTHON_BIN $INSTALLED_SERVER --doctor"
