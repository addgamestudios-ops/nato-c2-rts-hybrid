// =====================================================================
//  NATO C2 RTS Hybrid — UnityMcpBridge.cs
//  ---------------------------------------------------------------------
//  Editor-only HTTP/JSON-RPC bridge that lets an external process (the
//  MCP server in Tools/unity-mcp/server.js) drive the Unity Editor:
//      • refresh AssetDatabase
//      • enter/exit Play mode
//      • read the Console log
//      • list scene root objects
//      • re-import the NATO C2 sample
//
//  Auto-starts when the Editor loads. Listens on 127.0.0.1:7400.
//  All Unity API calls are marshalled to the main thread via an Editor-
//  update-driven action queue — HttpListener accepts requests on a
//  background thread.
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace NATO.C2.EditorTools.Mcp
{
    [InitializeOnLoad]
    public static class UnityMcpBridge
    {
        private const int Port = 7400;
        private const int ConsoleBufferSize = 256;
        // Token gate — if NATO_MCP_TOKEN is set, requests without a
        // matching Authorization: Bearer header are 403'd. Keeps a
        // co-tenant process on the same machine from driving the Editor
        // over loopback.
        private static readonly string ExpectedToken =
            System.Environment.GetEnvironmentVariable("NATO_MCP_TOKEN") ?? "";

        private static HttpListener _listener;
        private static Thread _serverThread;
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private static readonly List<string> _consoleBuffer = new List<string>(ConsoleBufferSize);

        static UnityMcpBridge()
        {
            EditorApplication.update += ProcessMainThreadQueue;
            Application.logMessageReceivedThreaded += OnLogMessage;
            EditorApplication.quitting += StopServer;
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            StartServer();
        }

        // =================================================================
        //  Menu actions — operator triage paths
        // =================================================================
        [MenuItem("NATO C2/MCP Bridge/Restart")]
        public static void MenuRestart()
        {
            StopServer();
            StartServer();
        }

        [MenuItem("NATO C2/MCP Bridge/Status")]
        public static void MenuStatus()
        {
            bool listening = _listener != null && _listener.IsListening;
            Debug.Log($"[UnityMcpBridge] status: listening={listening}  port={Port}  buffer={_consoleBuffer.Count}/{ConsoleBufferSize}");
        }

        // =================================================================
        //  Lifecycle
        // =================================================================
        private static void StartServer()
        {
            if (_listener != null) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _serverThread = new Thread(ServerLoop) { IsBackground = true };
                _serverThread.Start();
                Debug.Log($"[UnityMcpBridge] Listening on http://127.0.0.1:{Port}/");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityMcpBridge] Failed to start: {ex.Message}");
                _listener = null;
            }
        }

        private static void StopServer()
        {
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { /* swallow during teardown */ }
            _listener = null;
        }

        // =================================================================
        //  HTTP server loop (background thread)
        // =================================================================
        private static void ServerLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                if (ctx == null) continue;

                // ---- Token gate (when configured) ----
                if (!string.IsNullOrEmpty(ExpectedToken))
                {
                    string auth = ctx.Request.Headers["Authorization"] ?? "";
                    const string prefix = "Bearer ";
                    string presented = auth.StartsWith(prefix, StringComparison.Ordinal)
                        ? auth.Substring(prefix.Length) : "";
                    if (!ConstantTimeEquals(presented, ExpectedToken))
                    {
                        try
                        {
                            var msg = Encoding.UTF8.GetBytes("{\"error\":\"unauthorized\"}");
                            ctx.Response.StatusCode = 403;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.ContentLength64 = msg.Length;
                            ctx.Response.OutputStream.Write(msg, 0, msg.Length);
                            ctx.Response.OutputStream.Close();
                        }
                        catch { /* client gone */ }
                        continue;
                    }
                }

                string body = "";
                try
                {
                    using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = sr.ReadToEnd();
                }
                catch { }

                HandleRequest(body, ctx.Response);
            }
        }

        // Constant-time string compare so a co-tenant can't time the
        // first-byte-difference and recover the token bit by bit.
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void HandleRequest(string body, HttpListenerResponse resp)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                string result = null;
                _mainThreadQueue.Enqueue(() =>
                {
                    try { result = Dispatch(body); }
                    catch (Exception ex) { result = JsonError(ex.Message); }
                    finally { done.Set(); }
                });

                if (!done.Wait(20000)) result = JsonError("timeout");
                if (result == null) result = JsonError("no result");

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(result);
                    resp.ContentType = "application/json";
                    resp.StatusCode = 200;
                    resp.ContentLength64 = bytes.Length;
                    resp.OutputStream.Write(bytes, 0, bytes.Length);
                    resp.OutputStream.Close();
                }
                catch { /* client disconnected */ }
            }
        }

        // =================================================================
        //  Main-thread tick — drains the queue.
        // =================================================================
        private static void ProcessMainThreadQueue()
        {
            int budget = 32;
            while (budget-- > 0 && _mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        // =================================================================
        //  Console log capture (any thread)
        // =================================================================
        private static void OnLogMessage(string text, string stack, LogType type)
        {
            lock (_consoleBuffer)
            {
                _consoleBuffer.Add($"[{type}] {text}");
                if (_consoleBuffer.Count > ConsoleBufferSize)
                    _consoleBuffer.RemoveAt(0);
            }
        }

        // =================================================================
        //  Dispatch — JSON-RPC method router. All Unity API calls inside
        //  this function are safe because Dispatch is invoked from the
        //  main-thread queue.
        // =================================================================
        [Serializable] private class RpcRequest    { public string method; }
        [Serializable] private class RpcChaosArgs  { public string scenario; }
        [Serializable] private class RpcChaosOuter { public RpcChaosArgs @params; }

        private static string Dispatch(string body)
        {
            var req = string.IsNullOrEmpty(body) ? new RpcRequest() : JsonUtility.FromJson<RpcRequest>(body);
            if (req == null || string.IsNullOrEmpty(req.method)) return JsonError("missing method");

            switch (req.method)
            {
                case "editor.refresh":
                    AssetDatabase.Refresh();
                    return "{\"ok\":true}";

                case "editor.play":
                    EditorApplication.isPlaying = true;
                    return "{\"ok\":true}";

                case "editor.stop":
                    EditorApplication.isPlaying = false;
                    return "{\"ok\":true}";

                case "editor.isPlaying":
                    return "{\"playing\":" + (EditorApplication.isPlaying ? "true" : "false") + "}";

                case "editor.console":
                {
                    string[] copy;
                    lock (_consoleBuffer) copy = _consoleBuffer.ToArray();
                    return "{\"logs\":" + JsonStringArray(copy) + "}";
                }

                case "editor.clearConsole":
                {
                    lock (_consoleBuffer) _consoleBuffer.Clear();
                    try
                    {
                        var t = Type.GetType("UnityEditor.LogEntries,UnityEditor.dll");
                        t?.GetMethod("Clear")?.Invoke(null, null);
                    }
                    catch { }
                    return "{\"ok\":true}";
                }

                case "scene.list":
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    var roots = scene.GetRootGameObjects();
                    var names = new string[roots.Length];
                    for (int i = 0; i < roots.Length; i++) names[i] = roots[i].name;
                    return "{\"scene\":\"" + EscapeJson(scene.name) + "\",\"roots\":" + JsonStringArray(names) + "}";
                }

                case "package.reimportSample":
                    return ReimportNatoSample();

                case "chaos.run":
                    return RunChaosScenario(body);

                case "chaos.status":
                    return ChaosStatus();

                case "instance.name":
                    return "{\"name\":\"" + EscapeJson(
                        System.Environment.GetEnvironmentVariable("NATO_MCP_INSTANCE_NAME") ?? "") + "\"}";

                default:
                    return JsonError("unknown method: " + req.method);
            }
        }

        private static string ChaosStatus()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"running\":").Append(NATO.C2.EditorTools.FederationChaosMode.IsRunning ? "true" : "false");
            sb.Append(",\"scenario\":\"").Append(EscapeJson(NATO.C2.EditorTools.FederationChaosMode.CurrentScenarioName ?? "")).Append('"');
            sb.Append(",\"lastBundleDir\":\"").Append(EscapeJson(NATO.C2.EditorTools.FederationChaosMode.LastBundleDir ?? "")).Append('"');
            sb.Append(",\"lastZipLogPath\":\"").Append(EscapeJson(NATO.C2.EditorTools.FederationChaosMode.LastZipLogPath ?? "")).Append('"');
            sb.Append(",\"startedAt\":").Append(NATO.C2.EditorTools.FederationChaosMode.LastStartedAtRealtime.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"finishedAt\":").Append(NATO.C2.EditorTools.FederationChaosMode.LastFinishedAtRealtime.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("}");
            return sb.ToString();
        }

        // =================================================================
        //  chaos.run — load + execute a scenario by name from
        //  Tools/chaos-scenarios/{name}.json. Refuses if Unity isn't in
        //  Play mode (the simulator only publishes envelopes during Play).
        // =================================================================
        private static string RunChaosScenario(string rawBody)
        {
            try
            {
                if (!EditorApplication.isPlaying)
                    return JsonError("not in play mode — call editor.play first");

                var outer = JsonUtility.FromJson<RpcChaosOuter>(rawBody);
                string scenario = outer?.@params?.scenario;
                if (string.IsNullOrEmpty(scenario))
                    return JsonError("missing 'scenario' parameter");

                // Find the JSON file.
                string scenariosDir = NATO.C2.EditorTools.FederationChaosMode.ScenariosDir;
                string jsonPath = System.IO.Path.Combine(scenariosDir, scenario + ".json");
                if (!System.IO.File.Exists(jsonPath))
                    return JsonError("scenario not found: " + jsonPath);

                // Open the chaos-mode window, load the scenario, and start the run.
                var w = EditorWindow.GetWindow<NATO.C2.EditorTools.FederationChaosMode>();
                w.LoadScenarioFromFile(jsonPath);
                // The window exposes a Run() public method — invoke via reflection so we
                // don't need to break the existing public surface.
                var run = typeof(NATO.C2.EditorTools.FederationChaosMode)
                    .GetMethod("StartRun",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (run == null) return JsonError("StartRun() not found via reflection");
                run.Invoke(w, null);

                return "{\"ok\":true,\"scenario\":\"" + EscapeJson(scenario) +
                       "\",\"note\":\"run started; bundle path will be logged at end via Editor console\"}";
            }
            catch (Exception ex)
            {
                return JsonError("chaos.run failed: " + ex.Message);
            }
        }

        // =================================================================
        //  Sample reimport — finds the NATO package and re-imports its
        //  DemoScene sample (overrides previous user changes).
        // =================================================================
        private static string ReimportNatoSample()
        {
            try
            {
                var samples = Sample.FindByPackage("com.nato.c2-rts-hybrid", null);
                int n = 0;
                foreach (var s in samples) { s.Import(Sample.ImportOptions.OverridePreviousImports); n++; }
                AssetDatabase.Refresh();
                return "{\"ok\":true,\"imported\":" + n + "}";
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // =================================================================
        //  JSON helpers
        // =================================================================
        private static string JsonError(string msg) => "{\"error\":\"" + EscapeJson(msg) + "\"}";

        private static string JsonStringArray(string[] items)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(items[i])).Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
