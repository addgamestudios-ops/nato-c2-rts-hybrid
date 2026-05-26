// =====================================================================
//  NATO C2 RTS Hybrid — Link16TelemetryVisualizer.cs
//  ---------------------------------------------------------------------
//  EditorWindow that loads a CSV produced by Link16TelemetrySink and
//  renders three visual panels:
//
//      • Summary             — total decisions, demote / promote ratio,
//                              ARQ sent / acked / failed / retried.
//      • Per-agent mode flaps — one row per agent with vertical tick
//                              marks where the advisor changed its
//                              mode. Bright ticks = demotion (toward
//                              sparser / more robust), dimmer ticks =
//                              promotion (toward denser).
//      • ARQ retry rate      — line chart of (TotalRetried delta /
//                              snapshot-interval) sampled from
//                              SNAPSHOT rows. Spikes flag sustained
//                              congestion or jamming.
//
//  Spots calibration issues at a glance:
//      • A single agent flapping every few seconds → demoteThreshold
//        too aggressive; raise it.
//      • Sustained ARQ retry rate > 50% → net is over-subscribed for
//        the mode mix; lower ppliPerEpoch or push more terminals to
//        P4SP.
//
//  Trigger: menu  NATO C2 → Link 16 → Telemetry Visualizer
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NATO.C2.EditorTools
{
    public class Link16TelemetryVisualizer : EditorWindow
    {
        // ----- model -----
        private struct Row
        {
            public DateTime ts;
            public string  evt;      // "DECISION" | "SNAPSHOT"
            public string  agent;
            public string  fromMode;
            public string  toMode;
            public float   missRate;
            public int     samples;
            public int     arqSent, arqAcked, arqFailed, arqRetried, arqOutstanding;
            public int     advDecisions, advDemotions, advPromotions;
        }

        private string[] _csvFiles = Array.Empty<string>();
        private int      _selected = -1;
        private List<Row> _rows;
        private Vector2  _scroll;
        private DateTime _earliestTs, _latestTs;

        // Cached aggregations.
        private readonly List<string> _agents = new List<string>();
        private readonly Dictionary<string, List<Row>> _decisionsByAgent = new Dictionary<string, List<Row>>();
        private List<Row> _snapshots = new List<Row>();

        // ====================================================================
        [MenuItem("NATO C2/Link 16/Telemetry Visualizer", priority = 60)]
        public static void Open()
        {
            var w = GetWindow<Link16TelemetryVisualizer>("L16 Telemetry");
            w.minSize = new Vector2(740, 460);
            w.RefreshFiles();
        }

        private void OnEnable()
        {
            RefreshFiles();
        }

        private void RefreshFiles()
        {
            string logsDir = Path.Combine(Application.persistentDataPath, "Logs");
            if (!Directory.Exists(logsDir))
            {
                _csvFiles = Array.Empty<string>();
                return;
            }
            var files = Directory.GetFiles(logsDir, "L16-decisions-*.csv");
            Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal));   // newest first
            _csvFiles = files;
        }

        // ====================================================================
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Link 16 Telemetry Visualizer", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Logs dir: {Path.Combine(Application.persistentDataPath, "Logs")}", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) RefreshFiles();
                if (_csvFiles.Length == 0)
                {
                    EditorGUILayout.LabelField("(no L16-decisions-*.csv files found)");
                }
                else
                {
                    var labels = new string[_csvFiles.Length];
                    for (int i = 0; i < _csvFiles.Length; i++) labels[i] = Path.GetFileName(_csvFiles[i]);
                    int newSel = EditorGUILayout.Popup(_selected < 0 ? 0 : _selected, labels);
                    if (newSel != _selected) { _selected = newSel; LoadCsv(_csvFiles[_selected]); }
                }
            }
            if (_rows == null) return;

            EditorGUILayout.Space(6);
            DrawSummary();
            EditorGUILayout.Space(6);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawModeFlapChart();
            EditorGUILayout.Space(12);
            DrawRetryRateChart();
            EditorGUILayout.EndScrollView();
        }

        // ====================================================================
        //  CSV parsing
        // ====================================================================
        private void LoadCsv(string path)
        {
            _rows = new List<Row>(1024);
            _snapshots.Clear();
            _decisionsByAgent.Clear();
            _agents.Clear();
            try
            {
                using var rdr = new StreamReader(path);
                string header = rdr.ReadLine();    // skip header
                string line;
                while ((line = rdr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = SplitCsv(line);
                    if (parts.Count < 21) continue;
                    var r = new Row
                    {
                        ts = DateTime.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        evt = parts[1],
                        agent = parts[2],
                        fromMode = parts[3],
                        toMode = parts[4],
                        missRate = ParseFloat(parts[5]),
                        samples = ParseInt(parts[6]),
                        arqSent        = ParseInt(parts[13]),
                        arqAcked       = ParseInt(parts[14]),
                        arqFailed      = ParseInt(parts[15]),
                        arqRetried     = ParseInt(parts[16]),
                        arqOutstanding = ParseInt(parts[17]),
                        advDecisions   = ParseInt(parts[18]),
                        advDemotions   = ParseInt(parts[19]),
                        advPromotions  = ParseInt(parts[20]),
                    };
                    _rows.Add(r);
                    if (r.evt == "SNAPSHOT")
                    {
                        _snapshots.Add(r);
                    }
                    else if (r.evt == "DECISION")
                    {
                        if (!_decisionsByAgent.TryGetValue(r.agent, out var list))
                        {
                            list = new List<Row>();
                            _decisionsByAgent[r.agent] = list;
                            _agents.Add(r.agent);
                        }
                        list.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[L16TelemetryVisualizer] failed to load {path}: {e.Message}");
                _rows = null;
                return;
            }
            if (_rows.Count > 0)
            {
                _earliestTs = _rows[0].ts;
                _latestTs   = _rows[_rows.Count - 1].ts;
            }
            _agents.Sort(StringComparer.Ordinal);
        }

        private static int ParseInt(string s)
            => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static float ParseFloat(string s)
            => float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static List<string> SplitCsv(string line)
        {
            var result = new List<string>(24);
            int i = 0;
            var sb = new System.Text.StringBuilder();
            bool inQuote = false;
            while (i < line.Length)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    if (c == '"') { inQuote = false; i++; continue; }
                    sb.Append(c); i++;
                }
                else
                {
                    if (c == '"') { inQuote = true; i++; continue; }
                    if (c == ',') { result.Add(sb.ToString()); sb.Clear(); i++; continue; }
                    sb.Append(c); i++;
                }
            }
            result.Add(sb.ToString());
            return result;
        }

        // ====================================================================
        //  Panels
        // ====================================================================
        private void DrawSummary()
        {
            var last = _rows[_rows.Count - 1];
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"Span: {_earliestTs:HH:mm:ss}–{_latestTs:HH:mm:ss}", GUILayout.Width(200));
                EditorGUILayout.LabelField($"Decisions: {last.advDecisions}  ({last.advDemotions}↓ / {last.advPromotions}↑)", GUILayout.Width(220));
                EditorGUILayout.LabelField($"ARQ sent={last.arqSent} acked={last.arqAcked} failed={last.arqFailed} retried={last.arqRetried}", GUILayout.Width(360));
            }
        }

        private void DrawModeFlapChart()
        {
            EditorGUILayout.LabelField("Per-Agent Mode Flaps", EditorStyles.boldLabel);
            if (_agents.Count == 0)
            {
                EditorGUILayout.HelpBox("No DECISION rows in this log.", MessageType.Info);
                return;
            }

            double span = (_latestTs - _earliestTs).TotalSeconds;
            if (span < 1) span = 1;

            const float labelW = 120f;
            const float rowH = 18f;
            float chartW = position.width - labelW - 36f;
            if (chartW < 200f) chartW = 200f;

            var rect = GUILayoutUtility.GetRect(position.width - 30f, rowH * _agents.Count + 8f);
            // background.
            EditorGUI.DrawRect(new Rect(rect.x + labelW, rect.y, chartW, rect.height), new Color(0.10f, 0.12f, 0.15f, 1f));

            for (int i = 0; i < _agents.Count; i++)
            {
                string agent = _agents[i];
                float rowY = rect.y + i * rowH;
                // label.
                GUI.Label(new Rect(rect.x, rowY, labelW - 6, rowH), agent);
                // row separator.
                EditorGUI.DrawRect(new Rect(rect.x + labelW, rowY + rowH - 1, chartW, 1), new Color(1, 1, 1, 0.06f));

                foreach (var d in _decisionsByAgent[agent])
                {
                    double t = (d.ts - _earliestTs).TotalSeconds;
                    float x = rect.x + labelW + (float)(t / span) * chartW;
                    bool demotion = IsDemotion(d.fromMode, d.toMode);
                    Color c = demotion ? new Color(1f, 0.55f, 0.25f, 0.95f)   // bright orange = demote
                                       : new Color(0.55f, 0.85f, 1f, 0.75f); // pale blue = promote
                    EditorGUI.DrawRect(new Rect(x - 1, rowY + 3, 2, rowH - 6), c);
                }
            }
            EditorGUILayout.LabelField("Legend:  ", EditorStyles.miniLabel, GUILayout.Width(60));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("⬇ demote (toward sparser/robust)", EditorStyles.miniLabel, GUILayout.Width(240));
                EditorGUILayout.LabelField("⬆ promote (toward denser)", EditorStyles.miniLabel, GUILayout.Width(220));
            }
        }

        private void DrawRetryRateChart()
        {
            EditorGUILayout.LabelField("ARQ Retry Rate (retries between snapshots)", EditorStyles.boldLabel);
            if (_snapshots.Count < 2)
            {
                EditorGUILayout.HelpBox("Need at least 2 SNAPSHOT rows to draw a retry-rate trend.", MessageType.Info);
                return;
            }
            float chartW = position.width - 36f;
            const float chartH = 120f;
            var rect = GUILayoutUtility.GetRect(chartW, chartH);
            EditorGUI.DrawRect(rect, new Color(0.10f, 0.12f, 0.15f, 1f));

            // Compute delta retries per snapshot interval.
            int n = _snapshots.Count;
            int[] deltas = new int[n];
            deltas[0] = 0;
            int maxDelta = 1;
            for (int i = 1; i < n; i++)
            {
                deltas[i] = Mathf.Max(0, _snapshots[i].arqRetried - _snapshots[i - 1].arqRetried);
                if (deltas[i] > maxDelta) maxDelta = deltas[i];
            }
            // Draw line.
            Handles.color = new Color(1f, 0.55f, 0.25f, 1f);
            Vector3 prev = Vector3.zero;
            bool havePrev = false;
            for (int i = 0; i < n; i++)
            {
                float fx = i / (float)(n - 1);
                float fy = 1f - deltas[i] / (float)maxDelta;
                Vector3 pt = new Vector3(rect.x + fx * rect.width, rect.y + fy * (rect.height - 8f) + 4f, 0);
                if (havePrev) Handles.DrawLine(prev, pt);
                prev = pt; havePrev = true;
            }
            Handles.color = Color.white;

            EditorGUILayout.LabelField($"Snapshots: {n}    peak Δ-retries per snapshot: {maxDelta}", EditorStyles.miniLabel);
        }

        // ====================================================================
        //  Mode-rank — lower rank = denser (more risky)
        //  P2DP (0) > STD-DP (1) > P4SP (2). target>from → demote.
        // ====================================================================
        private static bool IsDemotion(string from, string to)
            => Rank(to) > Rank(from);

        private static int Rank(string m)
        {
            if (m == "P2Dp") return 0;
            if (m == "P4Sp") return 2;
            return 1; // StdDp or anything unknown
        }
    }
}
#endif
