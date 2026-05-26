// =====================================================================
//  NATO C2 RTS Hybrid — Link16TelemetrySink.cs
//  ---------------------------------------------------------------------
//  Writes Link 16 packing-mode + ARQ telemetry to a CSV file under:
//
//      <persistentDataPath>/Logs/L16-decisions-{yyyyMMdd-HHmmss}.csv
//
//  Two row types:
//      DECISION  — one row per LearnedModeAdvisor mode change
//      SNAPSHOT  — one row every snapshotEverySec (default 10s) with
//                  the current ARQ counters + advisor totals
//
//  The CSV is useful for post-mission analysis — operators can spot:
//      • Agents that flapped between modes (instability → tune thresholds)
//      • Sustained high ARQ retry rates (net congestion or jamming)
//      • Mode-distribution drift over time
//
//  The file is opened append-mode + flushed after each write so a
//  Unity crash leaves the partial log intact.
// =====================================================================

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/Link 16 Telemetry Sink")]
    public class Link16TelemetrySink : MonoBehaviour
    {
        [Tooltip("If null, finds the first simulator in the scene at Start.")]
        public Link16TdmaSimulator simulator;
        [Tooltip("If null, finds the first advisor in the scene at Start.")]
        public LearnedModeAdvisor advisor;
        [Tooltip("If null, found via simulator.GetComponent at Start.")]
        public Stanag5066ArqRetry arq;

        [Tooltip("How often to write a SNAPSHOT row.")]
        [Range(1f, 120f)] public float snapshotEverySec = 10f;

        [Tooltip("If true, also echo each row to the Unity console.")]
        public bool echoToConsole = false;

        // ----- runtime -----
        private string _filePath;
        private StreamWriter _writer;
        private float _nextSnapshotAt;
        private DateTime _startedAt;

        private void Start()
        {
            if (simulator == null) simulator = UnityEngine.Object.FindAnyObjectByType<Link16TdmaSimulator>();
            if (advisor   == null) advisor   = UnityEngine.Object.FindAnyObjectByType<LearnedModeAdvisor>();
            if (arq       == null && simulator != null) arq = simulator.GetComponent<Stanag5066ArqRetry>();

            OpenLog();
            if (advisor != null) advisor.OnDecision += OnDecision;
            _nextSnapshotAt = Time.unscaledTime + snapshotEverySec;
        }

        private void OnDestroy()
        {
            if (advisor != null) advisor.OnDecision -= OnDecision;
            CloseLog();
        }

        private void OnApplicationQuit()
        {
            CloseLog();
        }

        private void Update()
        {
            if (_writer == null) return;
            if (Time.unscaledTime < _nextSnapshotAt) return;
            _nextSnapshotAt = Time.unscaledTime + snapshotEverySec;
            WriteSnapshot();
        }

        // ====================================================================
        //  File management
        // ====================================================================
        private void OpenLog()
        {
            _startedAt = DateTime.UtcNow;
            string stamp = _startedAt.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(Application.persistentDataPath, "Logs");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir, $"L16-decisions-{stamp}.csv");
                _writer = new StreamWriter(_filePath, append: false, encoding: Encoding.UTF8);
                _writer.WriteLine("ts_iso,event,agent,from_mode,to_mode,miss_rate,samples," +
                                  "stddp_env_per_sec,p2dp_env_per_sec,p4sp_env_per_sec," +
                                  "stddp_msg_per_sec,p2dp_msg_per_sec,p4sp_msg_per_sec," +
                                  "arq_sent,arq_acked,arq_failed,arq_retried,arq_outstanding," +
                                  "advisor_decisions,advisor_demotions,advisor_promotions");
                _writer.Flush();
                Debug.Log($"[L16Telemetry] writing to {_filePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[L16Telemetry] could not open log: {e.Message}");
                _writer = null;
            }
        }

        private void CloseLog()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); } catch { /* swallow on shutdown */ }
            _writer = null;
        }

        // ====================================================================
        //  Row writers
        // ====================================================================
        private void OnDecision(Agent a,
                                Link16TdmaSimulator.PackingMode from,
                                Link16TdmaSimulator.PackingMode to,
                                float missRate,
                                int samples)
        {
            if (_writer == null) return;
            WriteRow(
                eventType: "DECISION",
                agent: a != null ? (a.callsign ?? a.name) : "?",
                fromMode: from.ToString(),
                toMode:   to.ToString(),
                missRate: missRate,
                samples:  samples);
        }

        private void WriteSnapshot()
        {
            WriteRow(
                eventType: "SNAPSHOT",
                agent: "",
                fromMode: "",
                toMode: "",
                missRate: 0f,
                samples: 0);
        }

        private void WriteRow(string eventType, string agent,
                              string fromMode, string toMode,
                              float missRate, int samples)
        {
            try
            {
                int eStd = simulator != null ? simulator.StdDpEnvelopesPerSec : 0;
                int e2dp = simulator != null ? simulator.P2DpEnvelopesPerSec  : 0;
                int e4sp = simulator != null ? simulator.P4SpEnvelopesPerSec  : 0;
                int mStd = simulator != null ? simulator.StdDpMsgsPerSec      : 0;
                int m2dp = simulator != null ? simulator.P2DpMsgsPerSec       : 0;
                int m4sp = simulator != null ? simulator.P4SpMsgsPerSec       : 0;
                int aqS  = arq != null ? arq.TotalTransmitted : 0;
                int aqA  = arq != null ? arq.TotalAcked       : 0;
                int aqF  = arq != null ? arq.TotalFailed      : 0;
                int aqR  = arq != null ? arq.TotalRetried     : 0;
                int aqO  = arq != null ? arq.OutstandingCount : 0;
                int adD  = advisor != null ? advisor.DecisionsMadeTotal : 0;
                int adDe = advisor != null ? advisor.DemotionsTotal     : 0;
                int adPr = advisor != null ? advisor.PromotionsTotal    : 0;

                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5:F4},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20}",
                    DateTime.UtcNow.ToString("O"),
                    eventType,
                    Esc(agent), fromMode, toMode,
                    missRate, samples,
                    eStd, e2dp, e4sp, mStd, m2dp, m4sp,
                    aqS, aqA, aqF, aqR, aqO,
                    adD, adDe, adPr);

                _writer.WriteLine(row);
                _writer.Flush();
                if (echoToConsole) Debug.Log("[L16Telemetry] " + row);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[L16Telemetry] write failed: {e.Message}");
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
