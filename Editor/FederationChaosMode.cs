// =====================================================================
//  NATO C2 RTS Hybrid — FederationChaosMode.cs
//  ---------------------------------------------------------------------
//  Editor scenario driver for repeatable federation stress tests.
//
//  The operator builds a script of timed events:
//      • [0  s] DROPS  0%       (baseline)
//      • [5  s] DROPS  30%
//      • [15 s] DROPS  80% + PEER-DROP "W1"
//      • [20 s] DROPS  0% + PEER-RESTORE "W1"  (recovery)
//      • [30 s] END
//
//  When Play mode is active, hit RUN — the script ticks at the right
//  wall-clock moments, calls into Stanag5066ArqRetry.SimulateRandomDrops,
//  and simulates peer drop-out by force-aging the peer's lastRxTime.
//
//  At END, the chaos mode collects a forensic bundle into:
//
//      <persistentDataPath>/Captures/chaos-{yyyyMMdd-HHmmss}/
//          ├── capture.dpdu       (latest from Stanag5066Capture)
//          ├── telemetry.csv      (latest from Link16TelemetrySink)
//          ├── dashboard.png      (Game-view screenshot)
//          └── scenario.txt       (the script that ran)
//
//  Trigger: menu  NATO C2 → Link 16 → Federation Chaos Mode
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2.EditorTools
{
    public class FederationChaosMode : EditorWindow
    {
        // ===== Scenario model =====
        public enum StepKind
        {
            SetDropRate,    // arg = % (0..100)
            PeerDrop,       // arg = peer op string in label
            PeerRestore,    // arg = peer op string in label
            End,
        }

        [System.Serializable]
        public struct Step
        {
            public float    atSec;
            public StepKind kind;
            public float    valueFloat;  // drop %
            public string   valueText;   // peer op for PeerDrop/PeerRestore
        }

        /// <summary>
        /// JSON wire form for a saved scenario file. JsonUtility can't
        /// serialize List&lt;Step&gt; directly when the list lives on
        /// EditorWindow, so we use this thin DTO for load/save.
        /// </summary>
        [System.Serializable]
        public class ScenarioFile
        {
            public string name;
            public string description;
            /// <summary>
            /// Optional wall-clock time scaling factor applied at load.
            /// e.g. timeScale=10 makes a 40 s scenario run in 4 s by
            /// dividing every step's atSec by 10. Use for CI soak runs
            /// where 40 s of real time is too long. Defaults to 1
            /// (no scaling) when missing or &lt;= 0.
            /// </summary>
            public float timeScale;
            public Step[] steps;
            /// <summary>
            /// Optional acceptance criteria. When present, headless test
            /// runners (ChaosScenarioSmokeTest, JamStormScenarioTest)
            /// assert the run's actual counters against these caps.
            /// Missing or zero-init means "no assertion".
            /// </summary>
            public ExpectedOutcome expectedOutcome;
        }

        [System.Serializable]
        public class ExpectedOutcome
        {
            public int   minTransmitted;     // 0 = no floor
            public float maxRetryRate;       // 0 = no cap. ratio retried/sent
            public float maxFailRate;        // 0 = no cap. ratio failed/sent
            public int   minAcked;           // 0 = no floor
        }

        public struct OutcomeResult
        {
            public bool   passed;
            public string detail;            // human-readable; empty when passed
        }

        /// <summary>
        /// Evaluate a finished run against a scenario's expectedOutcome.
        /// Returns (passed, detail). Pure function — runners call this
        /// directly in their Assert.IsTrue.
        /// </summary>
        public static OutcomeResult CheckExpectedOutcome(ExpectedOutcome eo,
                                                         int sent, int acked, int failed, int retried)
        {
            if (eo == null) return new OutcomeResult { passed = true };
            var failures = new System.Collections.Generic.List<string>();
            if (eo.minTransmitted > 0 && sent < eo.minTransmitted)
                failures.Add($"sent={sent} < minTransmitted={eo.minTransmitted}");
            if (eo.minAcked > 0 && acked < eo.minAcked)
                failures.Add($"acked={acked} < minAcked={eo.minAcked}");
            if (eo.maxRetryRate > 0f && sent > 0)
            {
                float rr = retried / (float)sent;
                if (rr > eo.maxRetryRate) failures.Add($"retryRate={rr:F2} > maxRetryRate={eo.maxRetryRate:F2}");
            }
            if (eo.maxFailRate > 0f && sent > 0)
            {
                float fr = failed / (float)sent;
                if (fr > eo.maxFailRate) failures.Add($"failRate={fr:F2} > maxFailRate={eo.maxFailRate:F2}");
            }
            if (failures.Count == 0) return new OutcomeResult { passed = true };
            return new OutcomeResult { passed = false, detail = string.Join("; ", failures) };
        }

        /// <summary>Default folder for saved scenarios — repo-relative.</summary>
        public static string ScenariosDir =>
            System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName,
                                   "Tools", "chaos-scenarios");

        private readonly List<Step> _steps = new List<Step>
        {
            new Step { atSec =  0f, kind = StepKind.SetDropRate, valueFloat =  0f  },
            new Step { atSec =  5f, kind = StepKind.SetDropRate, valueFloat = 30f  },
            new Step { atSec = 15f, kind = StepKind.SetDropRate, valueFloat = 80f  },
            new Step { atSec = 20f, kind = StepKind.SetDropRate, valueFloat =  0f  },
            new Step { atSec = 30f, kind = StepKind.End                          },
        };

        // ===== runtime =====
        private bool   _running;
        private float  _startedAt;
        private int    _nextStepIdx;
        private float  _currentDropPct;
        private float  _nextDropAt;
        private const float DropIntervalSec = 0.5f;
        private string _bundleDir;

        // Public read-only status surface — the Unity MCP bridge's
        // chaos.status method returns this snapshot so a polling MCP
        // client can wait for the run to finish and read the artifact
        // paths without further round-trips.
        public static bool   IsRunning            { get; private set; }
        public static string LastBundleDir        { get; private set; }
        public static string LastZipLogPath       { get; private set; }
        public static string CurrentScenarioName  { get; private set; }
        public static float  LastStartedAtRealtime { get; private set; }
        public static float  LastFinishedAtRealtime { get; private set; }
        private Link16TdmaSimulator _sim;
        private Stanag5066ArqRetry  _arq;
        private Stanag5066FederationBridge _bridge;
        private Vector2 _scroll;

        // ====================================================================
        [MenuItem("NATO C2/Link 16/Federation Chaos Mode", priority = 62)]
        public static void Open()
        {
            var w = GetWindow<FederationChaosMode>("L16 Chaos Mode");
            w.minSize = new Vector2(540, 420);
        }

        /// <summary>
        /// Bundle-only menu item: pick any existing chaos directory and
        /// archive it as a .ziplog. Lets you ship past runs as
        /// compliance-grade attachments without re-running the chaos.
        /// </summary>
        [MenuItem("NATO C2/Link 16/Archive existing chaos bundle as .ziplog...", priority = 65)]
        public static void ArchiveExistingBundle()
        {
            string captures = System.IO.Path.Combine(Application.persistentDataPath, "Captures");
            string dir = EditorUtility.OpenFolderPanel("Pick chaos-{timestamp}/ directory",
                                                       captures, "");
            if (string.IsNullOrEmpty(dir)) return;
            string zip = ArchiveBundleAsZipLog(dir);
            if (!string.IsNullOrEmpty(zip))
            {
                Debug.Log($"[ChaosMode] archived {dir} → {zip}");
                EditorUtility.RevealInFinder(zip);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Federation Chaos Mode", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Build a scenario, hit Play in Unity, then RUN. At END, a forensic " +
                "bundle (capture + CSV + screenshot + scenario) lands under " +
                "Captures/chaos-{timestamp}/.", MessageType.Info);

            DrawScenarioTable();
            EditorGUILayout.Space(6);
            DrawControls();
            EditorGUILayout.Space(6);
            DrawStatus();
        }

        private void OnEnable()  { EditorApplication.update += Tick; }
        private void OnDisable() { EditorApplication.update -= Tick; }

        // ====================================================================
        //  UI panels
        // ====================================================================
        private void DrawScenarioTable()
        {
            // --- Saved scenarios picker ---
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Library", GUILayout.Width(60));
                string[] files = ListScenarioFiles();
                if (files.Length == 0)
                {
                    EditorGUILayout.LabelField("(no .json files in " + ScenariosDir + ")",
                                               EditorStyles.miniLabel);
                }
                else
                {
                    int idx = EditorGUILayout.Popup(-1, files, GUILayout.Width(200));
                    if (idx >= 0) LoadScenarioFromFile(System.IO.Path.Combine(ScenariosDir, files[idx]));
                }
                if (GUILayout.Button("Save as...", GUILayout.Width(90))) SaveScenarioPrompt();
                if (GUILayout.Button("Open folder", GUILayout.Width(90))) EditorUtility.RevealInFinder(ScenariosDir);
            }
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Scenario", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("at (s)",   GUILayout.Width(80));
                EditorGUILayout.LabelField("kind",     GUILayout.Width(140));
                EditorGUILayout.LabelField("value",    GUILayout.Width(120));
                EditorGUILayout.LabelField("",         GUILayout.Width(80));
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(220));
            int removeIdx = -1;
            for (int i = 0; i < _steps.Count; i++)
            {
                Step s = _steps[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    s.atSec = EditorGUILayout.FloatField(s.atSec, GUILayout.Width(80));
                    s.kind  = (StepKind)EditorGUILayout.EnumPopup(s.kind, GUILayout.Width(140));
                    switch (s.kind)
                    {
                        case StepKind.SetDropRate:
                            s.valueFloat = EditorGUILayout.Slider(s.valueFloat, 0f, 100f, GUILayout.Width(120));
                            break;
                        case StepKind.PeerDrop:
                        case StepKind.PeerRestore:
                            s.valueText = EditorGUILayout.TextField(s.valueText ?? "", GUILayout.Width(120));
                            break;
                        case StepKind.End:
                            EditorGUILayout.LabelField("—", GUILayout.Width(120));
                            break;
                    }
                    if (GUILayout.Button("✕", GUILayout.Width(28))) removeIdx = i;
                    _steps[i] = s;
                }
            }
            EditorGUILayout.EndScrollView();
            if (removeIdx >= 0) _steps.RemoveAt(removeIdx);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Add Step", GUILayout.Width(120)))
                    _steps.Add(new Step { atSec = LastAt() + 5f, kind = StepKind.SetDropRate, valueFloat = 0f });
                if (GUILayout.Button("Reset Default Script", GUILayout.Width(180)))
                    ResetDefault();
            }
        }

        private void DrawControls()
        {
            EditorGUI.BeginDisabledGroup(!Application.isPlaying || _running);
            if (GUILayout.Button("▶ RUN", GUILayout.Height(36))) StartRun();
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!_running);
            if (GUILayout.Button("■ STOP", GUILayout.Height(24))) StopRun(collectBundle: false);
            EditorGUI.EndDisabledGroup();
        }

        private void DrawStatus()
        {
            if (!_running)
            {
                EditorGUILayout.LabelField("Status: idle");
                return;
            }
            float t = Time.realtimeSinceStartup - _startedAt;
            EditorGUILayout.LabelField($"Status: RUNNING  t={t:F1}s  drop%={_currentDropPct:F0}");
            if (_arq != null)
                EditorGUILayout.LabelField($"  ARQ sent={_arq.TotalTransmitted} acked={_arq.TotalAcked} " +
                                           $"failed={_arq.TotalFailed} retried={_arq.TotalRetried}");
            Repaint();
        }

        // ====================================================================
        //  Run / tick
        // ====================================================================
        private void StartRun()
        {
            _sim    = UnityEngine.Object.FindAnyObjectByType<Link16TdmaSimulator>();
            if (_sim == null) { Debug.LogWarning("[ChaosMode] no Link16TdmaSimulator in scene"); return; }
            _arq    = _sim.GetComponent<Stanag5066ArqRetry>();
            if (_arq == null) _arq = _sim.gameObject.AddComponent<Stanag5066ArqRetry>();
            _bridge = UnityEngine.Object.FindAnyObjectByType<Stanag5066FederationBridge>();
            _sim.useArqRetry = true;

            // Bundle path.
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _bundleDir = Path.Combine(Application.persistentDataPath, "Captures", "chaos-" + stamp);
            Directory.CreateDirectory(_bundleDir);
            File.WriteAllText(Path.Combine(_bundleDir, "scenario.txt"), SerializeScenario());

            _running        = true;
            _startedAt      = Time.realtimeSinceStartup;
            _nextStepIdx    = 0;
            _currentDropPct = 0f;
            _nextDropAt     = _startedAt;

            // Publish status for chaos.status pollers.
            IsRunning              = true;
            LastBundleDir          = _bundleDir;
            LastZipLogPath         = null;       // filled in once the run ends
            CurrentScenarioName    = System.IO.Path.GetFileName(_bundleDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            LastStartedAtRealtime  = _startedAt;
            LastFinishedAtRealtime = 0f;

            // Sort scenario by atSec ascending.
            _steps.Sort((a, b) => a.atSec.CompareTo(b.atSec));
            Debug.Log($"[ChaosMode] RUN — bundle dir: {_bundleDir}");
        }

        private void Tick()
        {
            if (!_running) return;
            float now = Time.realtimeSinceStartup;
            float t   = now - _startedAt;

            // Fire any due steps.
            while (_nextStepIdx < _steps.Count && _steps[_nextStepIdx].atSec <= t)
            {
                ApplyStep(_steps[_nextStepIdx]);
                _nextStepIdx++;
            }

            // Drive drops at DropIntervalSec cadence at the current pct.
            if (_currentDropPct > 0f && now >= _nextDropAt && _arq != null)
            {
                _arq.SimulateRandomDrops(_currentDropPct);
                _nextDropAt = now + DropIntervalSec;
            }
        }

        private void ApplyStep(Step s)
        {
            switch (s.kind)
            {
                case StepKind.SetDropRate:
                    _currentDropPct = Mathf.Clamp(s.valueFloat, 0f, 100f);
                    Debug.Log($"[ChaosMode] t={Time.realtimeSinceStartup - _startedAt:F1}s  SetDropRate {_currentDropPct:F0}%");
                    break;
                case StepKind.PeerDrop:
                    Debug.Log($"[ChaosMode] PeerDrop {s.valueText} — note: requires manual disconnection of that peer's Unity instance");
                    break;
                case StepKind.PeerRestore:
                    Debug.Log($"[ChaosMode] PeerRestore {s.valueText} — reconnect the peer's Unity instance");
                    break;
                case StepKind.End:
                    StopRun(collectBundle: true);
                    break;
            }
        }

        private void StopRun(bool collectBundle)
        {
            if (!_running) return;
            _running = false;
            if (_arq != null)
                Debug.Log($"[ChaosMode] STOP — final ARQ counters: sent={_arq.TotalTransmitted} " +
                          $"acked={_arq.TotalAcked} failed={_arq.TotalFailed} retried={_arq.TotalRetried}");
            if (collectBundle) CollectBundle();
            // CollectBundle also flips IsRunning=false + sets LastZipLogPath.
            // For a manual STOP (no bundle), make sure IsRunning still clears.
            if (!collectBundle)
            {
                IsRunning = false;
                LastFinishedAtRealtime = Time.realtimeSinceStartup;
            }
            _bundleDir = null;
        }

        // ====================================================================
        //  Bundle collection at END
        // ====================================================================
        private void CollectBundle()
        {
            if (string.IsNullOrEmpty(_bundleDir)) return;

            // 1) Latest .dpdu under Captures/.
            try
            {
                string capturesDir = Path.Combine(Application.persistentDataPath, "Captures");
                if (Directory.Exists(capturesDir))
                {
                    var dpdus = Directory.GetFiles(capturesDir, "L16-*.dpdu");
                    if (dpdus.Length > 0)
                    {
                        Array.Sort(dpdus, (a, b) => string.Compare(b, a, StringComparison.Ordinal));
                        File.Copy(dpdus[0], Path.Combine(_bundleDir, "capture.dpdu"), overwrite: true);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[ChaosMode] capture copy failed: {e.Message}"); }

            // 2) Latest CSV under Logs/.
            try
            {
                string logsDir = Path.Combine(Application.persistentDataPath, "Logs");
                if (Directory.Exists(logsDir))
                {
                    var csvs = Directory.GetFiles(logsDir, "L16-decisions-*.csv");
                    if (csvs.Length > 0)
                    {
                        Array.Sort(csvs, (a, b) => string.Compare(b, a, StringComparison.Ordinal));
                        File.Copy(csvs[0], Path.Combine(_bundleDir, "telemetry.csv"), overwrite: true);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[ChaosMode] csv copy failed: {e.Message}"); }

            // 3) Game-view screenshot.
            //    ScreenCapture writes relative to project root unless given
            //    an absolute path. We pass absolute → goes straight to bundle.
            try
            {
                string shot = Path.Combine(_bundleDir, "dashboard.png");
                ScreenCapture.CaptureScreenshot(shot);
                Debug.Log($"[ChaosMode] screenshot queued → {shot} (renders next frame)");
            }
            catch (Exception e) { Debug.LogWarning($"[ChaosMode] screenshot failed: {e.Message}"); }

            // 4) Final counters file.
            try
            {
                if (_arq != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("metric,value");
                    sb.AppendLine($"arq_sent,{_arq.TotalTransmitted}");
                    sb.AppendLine($"arq_acked,{_arq.TotalAcked}");
                    sb.AppendLine($"arq_failed,{_arq.TotalFailed}");
                    sb.AppendLine($"arq_retried,{_arq.TotalRetried}");
                    sb.AppendLine($"arq_outstanding,{_arq.OutstandingCount}");
                    if (_bridge != null)
                        foreach (var kv in _bridge.Peers)
                        {
                            sb.AppendLine($"peer_{kv.Key}_rx,{kv.Value.rxCount}");
                            sb.AppendLine($"peer_{kv.Key}_gaps,{kv.Value.gapsDetected}");
                            sb.AppendLine($"peer_{kv.Key}_srej_rx,{kv.Value.srejReceived}");
                        }
                    File.WriteAllText(Path.Combine(_bundleDir, "final-counters.csv"), sb.ToString());
                }
            }
            catch (Exception e) { Debug.LogWarning($"[ChaosMode] counters dump failed: {e.Message}"); }

            // 4b) OPLAN .ziplog archive — single immutable file with a
            //     SHA-256 manifest. Compliance-grade ticket attachment.
            string zipPath = ArchiveBundleAsZipLog(_bundleDir);
            if (!string.IsNullOrEmpty(zipPath))
            {
                Debug.Log($"[ChaosMode] .ziplog archive → {zipPath}");
                LastZipLogPath = zipPath;
            }
            LastFinishedAtRealtime = Time.realtimeSinceStartup;
            IsRunning = false;

            Debug.Log($"[ChaosMode] BUNDLE READY → {_bundleDir}");
            EditorUtility.RevealInFinder(_bundleDir);

            // 5) Optional: notify Slack via Tools/chaos-notify.py. Triggered
            //    only if SLACK_CHAOS_WEBHOOK_URL is set in the operator's
            //    environment. Runs out-of-process so the Editor stays
            //    responsive while the HTTP POST is in-flight.
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SLACK_CHAOS_WEBHOOK_URL")))
                TryRunSlackNotify(_bundleDir);
        }

        // ====================================================================
        //  .ziplog archiving — compliance-grade single-file output.
        // ====================================================================
        /// <summary>
        /// Bundle the chaos directory into a single immutable .ziplog
        /// archive with a SHA-256 manifest of every contained file plus
        /// the signing public-key fingerprint. Returns the absolute
        /// archive path, or null on failure.
        /// </summary>
        public static string ArchiveBundleAsZipLog(string bundleDir)
        {
            try
            {
                if (string.IsNullOrEmpty(bundleDir) || !System.IO.Directory.Exists(bundleDir)) return null;

                // 1. Build manifest: file → sha256 hex.
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# OPLAN .ziplog manifest");
                sb.AppendLine("# format: <sha256-hex>  <relative-path>");
                sb.AppendLine($"# generated: {System.DateTime.UtcNow:O}");
                sb.AppendLine($"# signingKeyId: {ResolveSigningKeyId()}");
                sb.AppendLine($"# pubKeyFingerprint: {ResolvePubKeyFingerprint()}");
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    foreach (var f in System.IO.Directory.GetFiles(bundleDir))
                    {
                        // Skip the manifest itself (we haven't written it yet, but be defensive).
                        string name = System.IO.Path.GetFileName(f);
                        if (name == "manifest.txt") continue;
                        byte[] bytes = System.IO.File.ReadAllBytes(f);
                        byte[] hash  = sha.ComputeHash(bytes);
                        sb.Append(BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
                        sb.Append("  ");
                        sb.AppendLine(name);
                    }
                }
                string manifestPath = System.IO.Path.Combine(bundleDir, "manifest.txt");
                System.IO.File.WriteAllText(manifestPath, sb.ToString());

                // 2. Zip everything (including the just-written manifest) into a .ziplog.
                string zipName = System.IO.Path.GetFileName(bundleDir.TrimEnd(System.IO.Path.DirectorySeparatorChar)) + ".ziplog";
                string zipPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(bundleDir) ?? Application.persistentDataPath,
                    zipName);
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(
                    bundleDir, zipPath,
                    System.IO.Compression.CompressionLevel.Optimal,
                    includeBaseDirectory: false);
                return zipPath;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChaosMode] .ziplog archive failed: {e.Message}");
                return null;
            }
        }

        private static string ResolveSigningKeyId()
        {
            try
            {
                string pub = System.IO.Path.Combine(ScenariosDir, ScenarioSigner.PubKeyFile);
                string kid = ScenarioSigner.ReadKeyId(pub);
                return kid ?? "(unknown — legacy / unsigned project)";
            }
            catch { return "(error)"; }
        }

        private static string ResolvePubKeyFingerprint()
        {
            try
            {
                string pub = System.IO.Path.Combine(ScenariosDir, ScenarioSigner.PubKeyFile);
                if (!System.IO.File.Exists(pub)) return "(no public key)";
                byte[] bytes = System.IO.File.ReadAllBytes(pub);
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha.ComputeHash(bytes);
                // First 8 bytes hex = 16 chars, plenty distinctive for an audit log.
                var sb = new System.Text.StringBuilder(20);
                for (int i = 0; i < 8 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
            catch { return "(error)"; }
        }

        private static void TryRunSlackNotify(string bundleDir)
        {
            try
            {
                string repoRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
                string script   = System.IO.Path.Combine(repoRoot, "Tools", "chaos-notify.py");
                if (!System.IO.File.Exists(script))
                {
                    Debug.LogWarning($"[ChaosMode] chaos-notify.py not found at {script}");
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/env", $"python3 \"{script}\" \"{bundleDir}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow = true,
                };
                var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(8000);
                if (p.ExitCode == 0)
                    Debug.Log("[ChaosMode] Slack notify ok: " + p.StandardOutput.ReadToEnd().Trim());
                else
                    Debug.LogWarning($"[ChaosMode] Slack notify rc={p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChaosMode] Slack notify failed: {e.Message}");
            }
        }

        // ====================================================================
        //  Helpers
        // ====================================================================
        private string SerializeScenario()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Federation Chaos Mode scenario");
            sb.AppendLine($"# generated {DateTime.UtcNow:O}");
            foreach (var s in _steps)
            {
                switch (s.kind)
                {
                    case StepKind.SetDropRate:  sb.AppendLine($"{s.atSec:F1}s  SetDropRate {s.valueFloat:F0}%"); break;
                    case StepKind.PeerDrop:     sb.AppendLine($"{s.atSec:F1}s  PeerDrop {s.valueText}");          break;
                    case StepKind.PeerRestore:  sb.AppendLine($"{s.atSec:F1}s  PeerRestore {s.valueText}");       break;
                    case StepKind.End:          sb.AppendLine($"{s.atSec:F1}s  End");                              break;
                }
            }
            return sb.ToString();
        }

        private float LastAt() => _steps.Count == 0 ? 0f : _steps[_steps.Count - 1].atSec;

        // ===== Scenario library load / save =====
        private static string[] ListScenarioFiles()
        {
            if (!System.IO.Directory.Exists(ScenariosDir)) return new string[0];
            var files = System.IO.Directory.GetFiles(ScenariosDir, "*.json");
            for (int i = 0; i < files.Length; i++) files[i] = System.IO.Path.GetFileName(files[i]);
            System.Array.Sort(files, System.StringComparer.Ordinal);
            return files;
        }

        public void LoadScenarioFromFile(string path)
        {
            try
            {
                // ---- Signature gate ----
                // NATO_REQUIRE_SIGNED_SCENARIOS=1 (in env) → reject unsigned/bad.
                //  Otherwise: warn and proceed.
                var verifyResult = ScenarioSigner.VerifyScenarioFile(path);
                bool strict =
                    System.Environment.GetEnvironmentVariable("NATO_REQUIRE_SIGNED_SCENARIOS") == "1";

                if (verifyResult != ScenarioSigner.VerifyResult.Ok)
                {
                    string msg = $"[ChaosMode] scenario '{System.IO.Path.GetFileName(path)}' " +
                                 $"signature check: {verifyResult}";
                    if (strict)
                    {
                        Debug.LogError(msg + "  — REJECTED (NATO_REQUIRE_SIGNED_SCENARIOS=1)");
                        return;
                    }
                    Debug.LogWarning(msg);
                }

                string json = System.IO.File.ReadAllText(path);
                var dto = JsonUtility.FromJson<ScenarioFile>(json);
                if (dto == null || dto.steps == null)
                {
                    Debug.LogWarning($"[ChaosMode] {path}: empty or bad JSON.");
                    return;
                }
                _steps.Clear();
                // Apply timeScale (if set) by dividing every step's atSec.
                float scale = dto.timeScale > 0f ? dto.timeScale : 1f;
                for (int i = 0; i < dto.steps.Length; i++)
                {
                    var s = dto.steps[i];
                    s.atSec /= scale;
                    _steps.Add(s);
                }
                Debug.Log($"[ChaosMode] loaded scenario '{dto.name}' ({_steps.Count} steps, " +
                          $"timeScale={scale:F1}x) from {path}  sig={verifyResult}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChaosMode] load failed: {e.Message}");
            }
        }

        private void SaveScenarioPrompt()
        {
            string defaultPath = System.IO.Path.Combine(ScenariosDir,
                $"chaos-{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            string path = EditorUtility.SaveFilePanel("Save scenario as JSON",
                                                      ScenariosDir,
                                                      System.IO.Path.GetFileName(defaultPath),
                                                      "json");
            if (string.IsNullOrEmpty(path)) return;
            var dto = new ScenarioFile
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path),
                description = "custom scenario",
                steps = _steps.ToArray()
            };
            try
            {
                if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)))
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(dto, prettyPrint: true));
                Debug.Log($"[ChaosMode] saved scenario → {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChaosMode] save failed: {e.Message}");
            }
        }

        private void ResetDefault()
        {
            _steps.Clear();
            _steps.Add(new Step { atSec =  0f, kind = StepKind.SetDropRate, valueFloat =  0f });
            _steps.Add(new Step { atSec =  5f, kind = StepKind.SetDropRate, valueFloat = 30f });
            _steps.Add(new Step { atSec = 15f, kind = StepKind.SetDropRate, valueFloat = 80f });
            _steps.Add(new Step { atSec = 20f, kind = StepKind.SetDropRate, valueFloat =  0f });
            _steps.Add(new Step { atSec = 30f, kind = StepKind.End });
        }
    }
}
#endif
