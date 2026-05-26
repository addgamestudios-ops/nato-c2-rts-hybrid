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

logging.basicConfig(
    level=logging.INFO,
    format="[unity-mcp] %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("unity-mcp")

# ----- tool registry -------------------------------------------------
TOOLS = [
    ("unity_refresh",          "editor.refresh",         "Refresh Unity AssetDatabase to pick up file changes."),
    ("unity_play",             "editor.play",            "Enter Play mode in the Unity Editor."),
    ("unity_stop",             "editor.stop",            "Exit Play mode in the Unity Editor."),
    ("unity_is_playing",       "editor.isPlaying",       "Returns whether the Editor is in Play mode."),
    ("unity_console",          "editor.console",         "Return the most recent Unity Console log lines (max 256)."),
    ("unity_clear_console",    "editor.clearConsole",    "Clear the Unity Console log buffer."),
    ("unity_scene_list",       "scene.list",             "List root GameObject names in the active scene."),
    ("unity_reimport_sample",  "package.reimportSample", "Re-import the NATO C2 RTS Hybrid DemoScene sample."),
]
TOOL_BY_NAME = {t[0]: t for t in TOOLS}

# ----- Unity HTTP call ----------------------------------------------
def call_unity(method: str, params: dict | None = None) -> str:
    body = json.dumps({"method": method, "params": params or {}}).encode("utf-8")
    req = urllib.request.Request(
        f"http://{UNITY_HOST}:{UNITY_PORT}/",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=UNITY_TIMEOUT) as resp:
            return resp.read().decode("utf-8")
    except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
        raise RuntimeError(
            f"Unity bridge unreachable at http://{UNITY_HOST}:{UNITY_PORT}/ "
            f"({e}). Is Unity open with the NATO C2 package installed?"
        )

# ----- MCP protocol -------------------------------------------------
SERVER_INFO  = {"name": "nato-c2-unity-mcp", "version": "0.1.0"}
CAPABILITIES = {"tools": {"listChanged": False}}

def handle_request(req: dict) -> dict | None:
    method = req.get("method")
    msg_id = req.get("id")
    params = req.get("params") or {}

    if method == "initialize":
        return _result(msg_id, {
            "protocolVersion": params.get("protocolVersion", "2025-06-18"),
            "capabilities": CAPABILITIES,
            "serverInfo": SERVER_INFO,
        })

    if method == "notifications/initialized":
        return None  # notification, no response

    if method == "ping":
        return _result(msg_id, {})

    if method == "tools/list":
        return _result(msg_id, {
            "tools": [
                {
                    "name": name,
                    "description": desc,
                    "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
                }
                for (name, _, desc) in TOOLS
            ]
        })

    if method == "tools/call":
        name = params.get("name", "")
        if name not in TOOL_BY_NAME:
            return _error(msg_id, -32601, f"Unknown tool: {name}")
        _, unity_method, _ = TOOL_BY_NAME[name]
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

if __name__ == "__main__":
    main()
