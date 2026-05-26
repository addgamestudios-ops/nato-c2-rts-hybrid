# NATO C2 Unity MCP server

Bridges Claude desktop to the Unity Editor so Claude can refresh the
AssetDatabase, enter/exit Play mode, read the Console, list scene objects,
and re-import the NATO C2 sample — without you clicking.

## Architecture

```
Claude desktop  ──stdio MCP──▶  server.py  (Python, stdlib only)
                                       │
                                       │ HTTP JSON-RPC
                                       ▼
                                Unity Editor
                                UnityMcpBridge.cs
                                (listens on 127.0.0.1:7400)
```

The Unity-side bridge ships inside the NATO C2 RTS Hybrid package at
`Editor/UnityMcpBridge.cs`. It auto-starts whenever you open the Editor via
`[InitializeOnLoad]`. No setup inside Unity.

The MCP server is pure Python using only the standard library, launched via
`/usr/bin/python3` (which macOS guarantees on every release ≥ 12.3).
Deliberately avoids Node so we don't depend on a shell-PATH-inherited binary
— Claude desktop launches stdio MCP servers from the GUI environment which
has a minimal PATH.

## Install

```bash
bash /Users/alex/Documents/Claude/Projects/Nato/Tools/unity-mcp/install.sh
```

Or double-click `install.command` in Finder.

The installer:
1. Finds `python3` (defaults to `/usr/bin/python3`).
2. Adds a `unity` entry to `~/Library/Application Support/Claude/claude_desktop_config.json`,
   merging with any existing `mcpServers` block. Idempotent.
3. Prints the resolved paths so you can verify them.

Then **fully quit** Claude desktop (Cmd+Q, not just close the window) and
reopen it.

## Tools registered

| Tool name              | What it does |
| --- | --- |
| `unity_refresh`        | `AssetDatabase.Refresh()` — picks up file changes made outside the Editor |
| `unity_play`           | Enter Play mode |
| `unity_stop`           | Exit Play mode |
| `unity_is_playing`     | Returns whether the Editor is in Play mode |
| `unity_console`        | Returns the most recent 256 Console log lines, prefixed with severity |
| `unity_clear_console`  | Clears the Console log buffer |
| `unity_scene_list`     | Lists root GameObject names in the active scene |
| `unity_reimport_sample`| Re-imports the NATO C2 RTS Hybrid DemoScene sample |

## Verify

With Unity open AND Claude desktop restarted, ask Claude:

> Are you connected to Unity? List the scene roots.

Claude should call `unity_scene_list` and return something like:

```
{"scene":"SampleScene","roots":["Main Camera","Directional Light","Bootstrap",...]}
```

## Troubleshooting

### "Could not attach to MCP server unity"

Check the Claude logs:

```bash
tail -n 100 ~/Library/Logs/Claude/mcp*.log
```

Common causes:

| Symptom in log | Fix |
| --- | --- |
| `python3: command not found` | Re-run `install.sh` — it should now write the absolute path `/usr/bin/python3` |
| `No module named …` | Server uses only stdlib — if this appears it's not our server. Check Python version: `/usr/bin/python3 --version` should be ≥ 3.9 |
| `server.py: not found` | File was moved. Re-run `install.sh` from the new location |
| `Connection refused 127.0.0.1:7400` | Unity Editor is closed or the package isn't installed. Open Unity with the project loaded; Console should show `[UnityMcpBridge] Listening on http://127.0.0.1:7400/` |

### Port 7400 is in use by another process

Edit `Editor/UnityMcpBridge.cs` (change the `Port` constant) AND set
`UNITY_MCP_PORT` in the Claude config:

```json
{
  "mcpServers": {
    "unity": {
      "command": "/usr/bin/python3",
      "args": ["/path/to/server.py"],
      "env": { "UNITY_MCP_PORT": "7401" }
    }
  }
}
```

### Claude doesn't see the tools after editing the config

The desktop app reads MCP config on startup only. Cmd+Q (full quit) then
reopen. Closing just the window is not enough.

## Security

- Bridge listens on `127.0.0.1` only — not reachable from other machines.
- There is no authentication. Anyone with access to your loopback interface
  can drive your Editor. Fine for a workstation; harden before any
  networked deployment.
- The bridge can enter Play mode and modify scene state. Do not point it
  at a project with unsaved work.

## Extending

To add a new RPC method, two changes:

1. **`Editor/UnityMcpBridge.cs`** → add a new `case "your.method":` in
   `Dispatch(...)` returning a JSON string.
2. **`server.py`** → append an entry to the `TOOLS` list with a matching
   `unityMethod`.

Restart Unity (so `[InitializeOnLoad]` re-runs) and restart Claude desktop.
