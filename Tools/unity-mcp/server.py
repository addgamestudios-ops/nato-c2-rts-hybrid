#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — unity-mcp/server.py
#  ---------------------------------------------------------------------
#  Pure-Python MCP server (no external dependencies). Bridges Claude
#  desktop to the UnityMcpBridge running inside the Editor over a
#  loopback HTTP/JSON-RPC channel.
#
#  Implements the minimal MCP 2025-06-18 stdio framing:
#      • LSP-style Content-Length headers, then a JSON body, OR
#      • newline-delimited JSON objects (NDJSON), depending on what the
#        client sends. We auto-detect.
#  Methods implemented: initialize, tools/list, tools/call, ping,
#  notifications/initialized (no-op).
# =====================================================================

import json
import sys
import urllib.request
import urllib.error
import os
import logging

UNITY_HOST = os.environ.get("UNITY_MCP_HOST", "127.0.0.1")
UNITY_PORT = int(os.environ.get("UNITY_MCP_PORT", "7400"))
UNITY_TIMEOUT = float(os.environ.get("UNITY_MCP_TIMEOUT", "20"))
# When set on both sides, the Unity bridge rejects requests whose
# Authorization: Bearer header doesn't match. Stops co-tenant
# processes from driving the Editor over loopback.
UNITY_TOKEN = os.environ.get("UNITY_MCP_TOKEN", "")

# Multi-instance routing — UNITY_MCP_INSTANCES is a csv of "name=host:port"
# entries. e.g. "alpha=127.0.0.1:7400,bravo=127.0.0.1:7401"
# If unset, we fall back to a single "default" instance built from
# UNITY_MCP_HOST/PORT above.
def _parse_instances_env() -> dict:
    raw = os.environ.get("UNITY_MCP_INSTANCES", "")
    out = {}
    for piece in raw.split(","):
        piece = piece.strip()
        if not piece: continue
        if "=" not in piece or ":" not in piece:
            log.warning("ignoring malformed instance entry: %s", piece)
            continue
        name, target = piece.split("=", 1)
        host, port = target.rsplit(":", 1)
        try:
            out[name.strip()] = (host.strip(), int(port.strip()))
        except ValueError:
            log.warning("ignoring instance with bad port: %s", piece)
    return out

_INSTANCES: dict = _parse_instances_env()
if not _INSTANCES:
    _INSTANCES = {"default": (UNITY_HOST, UNITY_PORT)}
_ACTIVE_INSTANCE: str = next(iter(_INSTANCES))

logging.basicConfig(
    level=logging.INFO,
    format="[unity-mcp] %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("unity-mcp")

# ----- tool registry -------------------------------------------------
#   Schema: (mcpName, unityMethod, description, inputSchema)
EMPTY_SCHEMA = {"type": "object", "properties": {}, "additionalProperties": False}
RUN_CHAOS_SCHEMA = {
    "type": "object",
    "properties": {
        "scenario": {
            "type": "string",
            "description": "Filename (without .json) under Tools/chaos-scenarios/. e.g. 'smoke', 'jam-storm-ci'.",
        }
    },
    "required": ["scenario"],
    "additionalProperties": False,
}
SELECT_INSTANCE_SCHEMA = {
    "type": "object",
    "properties": {
        "name": {
            "type": "string",
            "description": "Instance name to make active (from UNITY_MCP_INSTANCES env).",
        }
    },
    "required": ["name"],
    "additionalProperties": False,
}
TOOLS = [
    ("unity_list_instances",   None,                     "List the Unity bridges this server knows about. Shows name → host:port and which one is currently active.", EMPTY_SCHEMA),
    ("unity_select_instance",  None,                     "Switch which Unity bridge subsequent tool calls route to. Argument: name (must match one from unity_list_instances).", SELECT_INSTANCE_SCHEMA),
    ("unity_ping",             None,                     "Check whether the Unity Editor bridge is reachable. Returns ok=true or a diagnostic string.", EMPTY_SCHEMA),
    ("unity_refresh",          "editor.refresh",         "Refresh Unity AssetDatabase to pick up file changes.", EMPTY_SCHEMA),
    ("unity_play",             "editor.play",            "Enter Play mode in the Unity Editor.", EMPTY_SCHEMA),
    ("unity_stop",             "editor.stop",            "Exit Play mode in the Unity Editor.", EMPTY_SCHEMA),
    ("unity_is_playing",       "editor.isPlaying",       "Returns whether the Editor is in Play mode.", EMPTY_SCHEMA),
    ("unity_console",          "editor.console",         "Return the most recent Unity Console log lines (max 256).", EMPTY_SCHEMA),
    ("unity_clear_console",    "editor.clearConsole",    "Clear the Unity Console log buffer.", EMPTY_SCHEMA),
    ("unity_scene_list",       "scene.list",             "List root GameObject names in the active scene.", EMPTY_SCHEMA),
    ("unity_reimport_sample",  "package.reimportSample", "Re-import the NATO C2 RTS Hybrid DemoScene sample.", EMPTY_SCHEMA),
    ("unity_run_chaos",        "chaos.run",              "Run a chaos scenario by name (must already be signed). Returns the .ziplog bundle path when complete. Requires Unity to be in Play mode.", RUN_CHAOS_SCHEMA),
]
TOOL_BY_NAME = {t[0]: t for t in TOOLS}

# ----- Unity HTTP call ----------------------------------------------
def call_unity(method: str, params: dict | None = None) -> str:
    host, port = _INSTANCES[_ACTIVE_INSTANCE]
    body = json.dumps({"method": method, "params": params or {}}).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if UNITY_TOKEN:
        headers["Authorization"] = "Bearer " + UNITY_TOKEN
    req = urllib.request.Request(
        f"http://{host}:{port}/",
        data=body,
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=UNITY_TIMEOUT) as resp:
            return resp.read().decode("utf-8")
    except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
        raise RuntimeError(
            f"Unity bridge '{_ACTIVE_INSTANCE}' unreachable at http://{host}:{port}/ "
            f"({e}). Is Unity open with the NATO C2 package installed?"
        )

# ----- unity_run_chaos orchestration --------------------------------
#  Kicks off the chaos run via the Unity bridge, then polls chaos.status
#  every second until the run completes (running:false AND lastZipLogPath
#  populated). Total wait capped at 5 minutes — most scenarios finish
#  inside 10–60 s, but jam-storm at full speed is ~40 s and a custom
#  long scenario could be much longer.
import time, subprocess
RUN_CHAOS_POLL_INTERVAL_SEC = 1.0
RUN_CHAOS_MAX_WAIT_SEC = 300

def _list_instances_result() -> dict:
    lines = [f"  active: {_ACTIVE_INSTANCE}", "  instances:"]
    for inst, (h, p) in _INSTANCES.items():
        mark = "*" if inst == _ACTIVE_INSTANCE else " "
        lines.append(f"   {mark} {inst} → http://{h}:{p}/")
    return {"content": [{"type": "text", "text": "\n".join(lines)}]}

def _select_instance_result(target: str) -> dict:
    global _ACTIVE_INSTANCE
    if not target or target not in _INSTANCES:
        return {"isError": True, "content": [{"type": "text",
            "text": f"unknown instance '{target}'. Known: {list(_INSTANCES)}"}]}
    _ACTIVE_INSTANCE = target
    host, port = _INSTANCES[target]
    return {"content": [{"type": "text",
        "text": f"active instance is now '{target}' → http://{host}:{port}/"}]}


def _maybe_post_to_slack(bundle_dir: str | None) -> str:
    """If SLACK_CHAOS_WEBHOOK_URL is set, spawn chaos-notify.py against
       the bundle. Returns a human status line for the MCP result.
       Silent (returns '') if env unset or bundle missing."""
    if not bundle_dir or not os.environ.get("SLACK_CHAOS_WEBHOOK_URL"):
        return ""
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "..", "chaos-notify.py")
    script = os.path.normpath(script)
    if not os.path.exists(script):
        return f"slack: chaos-notify.py not found at {script}"
    try:
        p = subprocess.run(
            ["python3", script, bundle_dir],
            capture_output=True, text=True, timeout=10,
        )
        if p.returncode == 0:
            return f"slack:       posted ({p.stdout.strip()})"
        return f"slack:       FAILED rc={p.returncode}: {p.stderr.strip()[:200]}"
    except Exception as e:
        return f"slack:       error: {e}"

def run_chaos_with_polling(args: dict) -> dict:
    scenario = (args or {}).get("scenario", "")
    if not scenario:
        return {"isError": True, "content": [{"type": "text",
                "text": "missing required argument 'scenario'"}]}

    # Step 1 — kick off the run.
    try:
        kick = call_unity("chaos.run", {"scenario": scenario})
    except Exception as e:
        return {"isError": True, "content": [{"type": "text", "text": str(e)}]}

    # If the bridge returned an error envelope ({"error":"..."}) bail
    # out without polling.
    try:
        kick_obj = json.loads(kick)
    except json.JSONDecodeError:
        kick_obj = {}
    if "error" in kick_obj:
        return {"isError": True, "content": [{"type": "text",
                "text": f"chaos.run rejected: {kick_obj['error']}"}]}

    # Step 2 — poll chaos.status until the run completes.
    started = time.monotonic()
    last_status = None
    while time.monotonic() - started < RUN_CHAOS_MAX_WAIT_SEC:
        time.sleep(RUN_CHAOS_POLL_INTERVAL_SEC)
        try:
            status_text = call_unity("chaos.status", {})
            last_status = json.loads(status_text)
        except Exception as e:
            log.warning("chaos.status poll failed: %s", e)
            continue
        if not last_status.get("running") and last_status.get("lastZipLogPath"):
            elapsed = time.monotonic() - started
            bundle_dir = last_status.get("lastBundleDir")
            zip_path   = last_status.get("lastZipLogPath")
            slack_line = _maybe_post_to_slack(bundle_dir)
            return {"content": [{"type": "text", "text":
                f"scenario '{scenario}' completed in {elapsed:.1f}s\n"
                f"bundle dir:  {bundle_dir}\n"
                f".ziplog:     {zip_path}\n"
                f"verify CLI:  python3 Tools/verify-dpdu.py {bundle_dir}/capture.dpdu"
                + (f"\n{slack_line}" if slack_line else "")
            }]}
    # Timed out — return what we know so the caller can still inspect
    # the partial bundle.
    return {"isError": True, "content": [{"type": "text", "text":
        f"chaos.run for '{scenario}' did not complete within {RUN_CHAOS_MAX_WAIT_SEC}s. "
        f"Last status: {json.dumps(last_status) if last_status else 'no status'}"}]}


# ----- MCP protocol -------------------------------------------------
SERVER_INFO  = {"name": "nato-c2-unity-mcp", "version": "0.1.0"}
CAPABILITIES = {"tools": {"listChanged": False}}

def handle_request(req: dict) -> dict | None:
    method = req.get("method")
    msg_id = req.get("id")
    params = req.get("params") or {}
    is_notification = msg_id is None

    # JSON-RPC notifications must never get a response. We swallow every
    # method that starts with "notifications/" (the standard MCP namespace)
    # AND any request that lacks an id.
    if method and method.startswith("notifications/"):
        return None
    if is_notification:
        return None

    if method == "initialize":
        return _result(msg_id, {
            "protocolVersion": params.get("protocolVersion", "2025-06-18"),
            "capabilities": CAPABILITIES,
            "serverInfo": SERVER_INFO,
        })

    if method == "ping":
        return _result(msg_id, {})

    if method == "tools/list":
        return _result(msg_id, {
            "tools": [
                {
                    "name": name,
                    "description": desc,
                    "inputSchema": schema,
                }
                for (name, _, desc, schema) in TOOLS
            ]
        })

    if method == "tools/call":
        name = params.get("name", "")
        if name not in TOOL_BY_NAME:
            return _error(msg_id, -32601, f"Unknown tool: {name}")
        _, unity_method, _, _ = TOOL_BY_NAME[name]
        # Multi-instance management — pure local server state, no Unity call.
        if name == "unity_list_instances":
            return _result(msg_id, _list_instances_result())
        if name == "unity_select_instance":
            return _result(msg_id, _select_instance_result((params.get("arguments") or {}).get("name", "")))
        # unity_run_chaos is multi-step: kick off chaos.run, then poll
        # chaos.status until the run completes (or we hit the cap).
        if name == "unity_run_chaos":
            return _result(msg_id, run_chaos_with_polling(params.get("arguments") or {}))
        # unity_ping is special — it MUST always succeed if the server
        # itself is alive, even when Unity is down. The error path returns
        # the helpful diagnostic so the client knows what to fix.
        if name == "unity_ping":
            try:
                text = call_unity("editor.isPlaying", {})
                return _result(msg_id, {"content": [{"type": "text",
                    "text": f"ok bridge=http://{UNITY_HOST}:{UNITY_PORT}/ response={text.strip()}"}]})
            except Exception as e:
                return _result(msg_id, {
                    "isError": True,
                    "content": [{"type": "text", "text":
                        f"Unity Editor unreachable. {e}\n\n"
                        f"Checklist:\n"
                        f"  1. Is the Unity Editor open with the NATO C2 package loaded?\n"
                        f"  2. Did you see '[UnityMcpBridge] Listening on http://127.0.0.1:7400/' in the Editor console?\n"
                        f"  3. Try Editor menu: NATO C2 / MCP Bridge / Restart (added by the install)."
                    }],
                })
        try:
            text = call_unity(unity_method, params.get("arguments") or {})
            return _result(msg_id, {"content": [{"type": "text", "text": text}]})
        except Exception as e:
            return _result(msg_id, {
                "isError": True,
                "content": [{"type": "text", "text": str(e)}],
            })

    return _error(msg_id, -32601, f"Method not found: {method}")

def _result(msg_id, result):
    return {"jsonrpc": "2.0", "id": msg_id, "result": result}

def _error(msg_id, code, message):
    return {"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": message}}

# ----- stdio framing -------------------------------------------------
def write_message(msg: dict) -> None:
    data = json.dumps(msg, separators=(",", ":"))
    out = sys.stdout.buffer
    out.write(data.encode("utf-8") + b"\n")
    out.flush()

def read_message() -> dict | None:
    line = sys.stdin.buffer.readline()
    if not line:
        return None
    line = line.strip()
    if not line:
        return read_message()
    # Detect Content-Length header form vs NDJSON form.
    if line.lower().startswith(b"content-length:"):
        length = int(line.split(b":", 1)[1].strip())
        # consume blank line
        while True:
            sep = sys.stdin.buffer.readline()
            if sep in (b"\r\n", b"\n", b""):
                break
        body = sys.stdin.buffer.read(length)
        return json.loads(body.decode("utf-8"))
    return json.loads(line.decode("utf-8"))

def main():
    log.info("server ready (python %s)", sys.version.split()[0])
    log.info("target Editor bridge: http://%s:%d/", UNITY_HOST, UNITY_PORT)
    while True:
        try:
            msg = read_message()
        except Exception as e:
            log.exception("read error: %s", e)
            continue
        if msg is None:
            break
        try:
            resp = handle_request(msg)
            if resp is not None:
                write_message(resp)
        except Exception as e:
            log.exception("dispatch error: %s", e)
            if "id" in msg:
                write_message(_error(msg.get("id"), -32603, str(e)))


# ====================================================================
#  CLI: --self-test runs the handshake in-process and prints PASS/FAIL.
#       --doctor diagnoses install state (config path, Unity bridge).
# ====================================================================
def self_test() -> int:
    """Drive the dispatcher with synthetic requests; print PASS/FAIL.
       Does NOT require Unity to be running."""
    failures = []

    def assert_ok(label, cond, detail=""):
        if cond: print(f"  PASS  {label}")
        else:
            print(f"  FAIL  {label}  {detail}")
            failures.append(label)

    r = handle_request({"jsonrpc":"2.0","id":1,"method":"initialize",
                        "params":{"protocolVersion":"2025-06-18"}})
    assert_ok("initialize returns serverInfo",
              r and r.get("result", {}).get("serverInfo", {}).get("name") == "nato-c2-unity-mcp",
              str(r)[:200])

    r = handle_request({"jsonrpc":"2.0","method":"notifications/initialized"})
    assert_ok("notifications/initialized is silent", r is None)
    r = handle_request({"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}})
    assert_ok("notifications/cancelled is silent", r is None)

    r = handle_request({"jsonrpc":"2.0","id":2,"method":"ping"})
    assert_ok("ping returns empty result", r and r.get("result") == {})

    r = handle_request({"jsonrpc":"2.0","id":3,"method":"tools/list"})
    tools = (r or {}).get("result", {}).get("tools", [])
    names = [t["name"] for t in tools]
    assert_ok("tools/list contains unity_ping", "unity_ping" in names)
    assert_ok("tools/list contains unity_refresh", "unity_refresh" in names)
    assert_ok("tools/list contains unity_console", "unity_console" in names)

    r = handle_request({"jsonrpc":"2.0","id":4,"method":"tools/call",
                        "params":{"name":"unity_nonexistent"}})
    assert_ok("unknown tool returns error -32601",
              r and r.get("error", {}).get("code") == -32601)

    r = handle_request({"jsonrpc":"2.0","id":5,"method":"bogus"})
    assert_ok("unknown method returns error -32601",
              r and r.get("error", {}).get("code") == -32601)

    # unity_ping: with Unity down this should return isError=true + diagnostic.
    r = handle_request({"jsonrpc":"2.0","id":6,"method":"tools/call",
                        "params":{"name":"unity_ping"}})
    res = (r or {}).get("result", {})
    if "Unity Editor unreachable" in (res.get("content", [{}])[0].get("text", "")):
        print("  PASS  unity_ping returns helpful diagnostic when Unity is down")
    elif res.get("content", [{}])[0].get("text", "").startswith("ok bridge="):
        print("  PASS  unity_ping returns ok (Unity Editor IS reachable)")
    else:
        print(f"  FAIL  unity_ping unexpected response: {r}")
        failures.append("unity_ping")

    print()
    if failures:
        print(f"SELF-TEST FAILED — {len(failures)} issue(s): {failures}")
        return 2
    print("SELF-TEST PASSED")
    return 0


def doctor() -> int:
    """Inspect install state without launching Unity. Tells the operator
       exactly which step in the setup failed."""
    import os as _os
    print("=== unity-mcp doctor ===")
    print(f"  python:       {sys.executable}  ({sys.version.split()[0]})")
    print(f"  this script:  {__file__}")
    print(f"  instances:    {len(_INSTANCES)} (active: {_ACTIVE_INSTANCE})")
    for inst, (h, p) in _INSTANCES.items():
        mark = "*" if inst == _ACTIVE_INSTANCE else " "
        print(f"   {mark} {inst} → http://{h}:{p}/")

    cfg_path = _os.path.expanduser(
        "~/Library/Application Support/Claude/claude_desktop_config.json")
    if _os.path.exists(cfg_path):
        try:
            cfg = json.loads(open(cfg_path).read())
            entry = cfg.get("mcpServers", {}).get("unity")
            if entry:
                print(f"  config:       {cfg_path}")
                print(f"    command:    {entry.get('command')}")
                print(f"    args:       {entry.get('args')}")
                installed = (entry.get("args") or [""])[0]
                if installed and _os.path.exists(installed):
                    print(f"    installed:  ok ({installed})")
                else:
                    print(f"    installed:  ⚠ MISSING — re-run Tools/unity-mcp/install.command")
            else:
                print(f"  config:       {cfg_path} present, but no 'unity' entry")
                print("                run Tools/unity-mcp/install.command to register")
        except Exception as e:
            print(f"  config:       FAILED to parse {cfg_path}: {e}")
    else:
        print(f"  config:       MISSING at {cfg_path}")
        print("                run Tools/unity-mcp/install.command to create it")

    # Try the bridge.
    try:
        text = call_unity("editor.isPlaying", {})
        print(f"  unity bridge: REACHABLE at http://{UNITY_HOST}:{UNITY_PORT}/  → {text.strip()}")
    except Exception as e:
        print(f"  unity bridge: UNREACHABLE — {e}")
        print("                open the Unity Editor with the NATO C2 package loaded")

    print()
    print("Next step: restart Claude Desktop (Cmd+Q, reopen) to pick up config changes.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1:
        if sys.argv[1] == "--self-test":
            sys.exit(self_test())
        if sys.argv[1] == "--doctor":
            sys.exit(doctor())
        if sys.argv[1] in ("-h", "--help"):
            print("usage: server.py             # run as stdio MCP server (default)")
            print("       server.py --self-test # run in-process handshake checks")
            print("       server.py --doctor    # diagnose install state")
            sys.exit(0)
    main()
