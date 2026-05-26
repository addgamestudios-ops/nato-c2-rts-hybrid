#!/usr/bin/env node
// =====================================================================
//  NATO C2 RTS Hybrid — unity-mcp/server.js
//  ---------------------------------------------------------------------
//  An MCP server that bridges Claude to the UnityMcpBridge running
//  inside Unity Editor (Editor/UnityMcpBridge.cs). Each MCP tool here
//  translates into a JSON-RPC POST against http://127.0.0.1:7400/.
//
//  Communicates with Claude via stdio per the MCP spec. Register it in
//  ~/Library/Application Support/Claude/claude_desktop_config.json:
//
//      {
//        "mcpServers": {
//          "unity": {
//            "command": "node",
//            "args": ["/Users/<you>/path/to/server.js"]
//          }
//        }
//      }
//
//  Then restart Claude desktop.
// =====================================================================

const { Server } = require("@modelcontextprotocol/sdk/server/index.js");
const { StdioServerTransport } = require("@modelcontextprotocol/sdk/server/stdio.js");
const {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} = require("@modelcontextprotocol/sdk/types.js");
const http = require("http");

const UNITY_HOST = process.env.UNITY_MCP_HOST || "127.0.0.1";
const UNITY_PORT = Number(process.env.UNITY_MCP_PORT || 7400);
const UNITY_TIMEOUT_MS = Number(process.env.UNITY_MCP_TIMEOUT_MS || 20000);

function callUnity(method, params = {}) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ method, params });
    const req = http.request(
      {
        hostname: UNITY_HOST,
        port: UNITY_PORT,
        path: "/",
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
        },
        timeout: UNITY_TIMEOUT_MS,
      },
      (res) => {
        let data = "";
        res.on("data", (chunk) => (data += chunk));
        res.on("end", () => resolve(data));
      }
    );
    req.on("error", (err) => reject(err));
    req.on("timeout", () => {
      req.destroy(new Error(`Unity bridge timeout after ${UNITY_TIMEOUT_MS}ms`));
    });
    req.write(body);
    req.end();
  });
}

// ---- tool registry ----------------------------------------------------
const TOOLS = [
  {
    name: "unity_refresh",
    description:
      "Refresh the Unity AssetDatabase so it picks up any file changes made outside the Editor.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.refresh",
  },
  {
    name: "unity_play",
    description: "Enter Play mode in the Unity Editor.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.play",
  },
  {
    name: "unity_stop",
    description: "Exit Play mode in the Unity Editor.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.stop",
  },
  {
    name: "unity_is_playing",
    description: "Return whether the Editor is currently in Play mode.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.isPlaying",
  },
  {
    name: "unity_console",
    description:
      "Return the most recent Unity Console log lines (up to 256). Each line is prefixed with severity, e.g. '[Error] …'.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.console",
  },
  {
    name: "unity_clear_console",
    description: "Clear the Unity Console log buffer.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "editor.clearConsole",
  },
  {
    name: "unity_scene_list",
    description:
      "List the root GameObject names in the currently-active scene.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "scene.list",
  },
  {
    name: "unity_reimport_sample",
    description:
      "Re-import the NATO C2 RTS Hybrid DemoScene sample, overriding any user edits. Use this after package source files change.",
    inputSchema: { type: "object", properties: {} },
    unityMethod: "package.reimportSample",
  },
];

const server = new Server(
  { name: "nato-c2-unity-mcp", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: TOOLS.map(({ name, description, inputSchema }) => ({
    name,
    description,
    inputSchema,
  })),
}));

server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const tool = TOOLS.find((t) => t.name === req.params.name);
  if (!tool) {
    return {
      isError: true,
      content: [{ type: "text", text: `Unknown tool: ${req.params.name}` }],
    };
  }
  try {
    const text = await callUnity(tool.unityMethod, req.params.arguments || {});
    return { content: [{ type: "text", text }] };
  } catch (err) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text:
            `Failed to reach Unity Editor bridge at http://${UNITY_HOST}:${UNITY_PORT}/. ` +
            `Make sure Unity is open with the NATO C2 RTS Hybrid package installed.\n\n` +
            `Underlying error: ${err.message}`,
        },
      ],
    };
  }
});

(async () => {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  // Log to stderr (stdout is the MCP transport).
  console.error("[unity-mcp] server ready, connected via stdio");
  console.error(`[unity-mcp] target Editor bridge: http://${UNITY_HOST}:${UNITY_PORT}/`);
})();
