// =====================================================================
//  NATO C2 RTS Hybrid — Link16OtlpExporter.cs
//  ---------------------------------------------------------------------
//  Periodically pushes federation metrics to an OpenTelemetry OTLP/HTTP
//  collector endpoint as serialised JSON. Drops into Prometheus /
//  Grafana / Datadog without us shipping a vendor-specific dashboard.
//
//  Metrics exported (gauges):
//      nato_c2.l16.arq.sent          (cumulative)
//      nato_c2.l16.arq.acked         (cumulative)
//      nato_c2.l16.arq.failed        (cumulative)
//      nato_c2.l16.arq.retried       (cumulative)
//      nato_c2.l16.arq.outstanding   (instant)
//      nato_c2.l16.envelopes.{mode}.per_sec    — STD-DP / P2DP / P4SP
//      nato_c2.l16.messages.{mode}.per_sec
//      nato_c2.l16.advisor.decisions (cumulative)
//      nato_c2.l16.peer.rx_count{peer=…}   (cumulative)
//      nato_c2.l16.peer.srej_rx{peer=…}    (cumulative)
//      nato_c2.l16.peer.gaps{peer=…}       (cumulative)
//
//  Endpoint configuration:
//      • Inspector field `endpoint` (default http://localhost:4318/v1/metrics)
//        — the standard OTLP/HTTP path the OTel Collector listens on.
//      • $OTLP_ENDPOINT env override at runtime.
//      • $OTLP_HEADER_AUTHORIZATION env adds an Authorization header
//        (cloud-vendor collectors need this).
//
//  This sits ALONGSIDE Link16TelemetrySink (CSV) — both can run at
//  once. The CSV is for forensic post-mortems; OTLP is for live ops
//  dashboards.
// =====================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/Link 16 OTLP Exporter")]
    public class Link16OtlpExporter : MonoBehaviour
    {
        [Tooltip("If null, finds the first simulator in the scene at Start.")]
        public Link16TdmaSimulator simulator;
        [Tooltip("If null, finds the first advisor in the scene at Start.")]
        public LearnedModeAdvisor advisor;
        [Tooltip("If null, found via simulator.GetComponent at Start.")]
        public Stanag5066ArqRetry arq;
        [Tooltip("If null, found at Start.")]
        public Stanag5066FederationBridge bridge;

        [Tooltip("OTLP/HTTP metrics endpoint. Env OTLP_ENDPOINT overrides at runtime.")]
        public string endpoint = "http://localhost:4318/v1/metrics";

        [Tooltip("Push interval in seconds. OTLP collectors typically expect 10–60s.")]
        [Range(2f, 60f)] public float pushEverySec = 15f;

        [Tooltip("If true, log each push outcome to the console.")]
        public bool logPushes = false;

        // ----- runtime -----
        private float _nextPushAt;
        private string _resolvedEndpoint;
        private string _authHeader;

        private void Start()
        {
            if (simulator == null) simulator = UnityEngine.Object.FindAnyObjectByType<Link16TdmaSimulator>();
            if (advisor   == null) advisor   = UnityEngine.Object.FindAnyObjectByType<LearnedModeAdvisor>();
            if (arq       == null && simulator != null) arq = simulator.GetComponent<Stanag5066ArqRetry>();
            if (bridge    == null) bridge    = UnityEngine.Object.FindAnyObjectByType<Stanag5066FederationBridge>();

            string env = Environment.GetEnvironmentVariable("OTLP_ENDPOINT");
            _resolvedEndpoint = string.IsNullOrEmpty(env) ? endpoint : env;
            _authHeader = Environment.GetEnvironmentVariable("OTLP_HEADER_AUTHORIZATION");
            Debug.Log($"[L16OTLP] pushing to {_resolvedEndpoint} every {pushEverySec}s");
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPushAt) return;
            _nextPushAt = Time.unscaledTime + pushEverySec;
            StartCoroutine(PushOnce());
        }

        // ====================================================================
        //  OTLP/HTTP JSON push
        // ====================================================================
        private IEnumerator PushOnce()
        {
            string payload = BuildPayload();
            using var req = new UnityWebRequest(_resolvedEndpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 8,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_authHeader))
                req.SetRequestHeader("Authorization", _authHeader);
            yield return req.SendWebRequest();

            if (logPushes)
            {
                if (req.result == UnityWebRequest.Result.Success)
                    Debug.Log($"[L16OTLP] push ok ({payload.Length} bytes)");
                else
                    Debug.LogWarning($"[L16OTLP] push failed: {req.result} {req.error}");
            }
        }

        // ====================================================================
        //  OTLP/HTTP payload — minimal hand-rolled JSON. The OTel Collector
        //  accepts standard JSON over HTTP at /v1/metrics. We emit a single
        //  ResourceMetrics → ScopeMetrics → Metric tree.
        // ====================================================================
        private string BuildPayload()
        {
            long now_ns = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            var sb = new StringBuilder(2048);
            sb.Append("{\"resourceMetrics\":[{\"resource\":{\"attributes\":[")
              .Append(Attr("service.name", "nato-c2-rts-hybrid"))
              .Append(",")
              .Append(Attr("service.namespace", "link16"))
              .Append("]},\"scopeMetrics\":[{\"scope\":{\"name\":\"nato.c2.l16\"},\"metrics\":[");

            bool first = true;
            void AddSum(string name, long v)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":\"").Append(name).Append("\",\"sum\":{")
                  .Append("\"aggregationTemporality\":2,\"isMonotonic\":true,")
                  .Append("\"dataPoints\":[{\"timeUnixNano\":\"").Append(now_ns)
                  .Append("\",\"asInt\":\"").Append(v).Append("\"}]}}");
            }
            void AddGauge(string name, long v)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":\"").Append(name).Append("\",\"gauge\":{")
                  .Append("\"dataPoints\":[{\"timeUnixNano\":\"").Append(now_ns)
                  .Append("\",\"asInt\":\"").Append(v).Append("\"}]}}");
            }
            void AddPeerSum(string name, string peer, long v)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":\"").Append(name).Append("\",\"sum\":{")
                  .Append("\"aggregationTemporality\":2,\"isMonotonic\":true,")
                  .Append("\"dataPoints\":[{\"timeUnixNano\":\"").Append(now_ns)
                  .Append("\",\"asInt\":\"").Append(v)
                  .Append("\",\"attributes\":[").Append(Attr("peer", peer)).Append("]}]}}");
            }

            if (arq != null)
            {
                AddSum  ("nato_c2.l16.arq.sent",        arq.TotalTransmitted);
                AddSum  ("nato_c2.l16.arq.acked",       arq.TotalAcked);
                AddSum  ("nato_c2.l16.arq.failed",      arq.TotalFailed);
                AddSum  ("nato_c2.l16.arq.retried",     arq.TotalRetried);
                AddGauge("nato_c2.l16.arq.outstanding", arq.OutstandingCount);
            }
            if (simulator != null)
            {
                AddGauge("nato_c2.l16.envelopes.stddp.per_sec", simulator.StdDpEnvelopesPerSec);
                AddGauge("nato_c2.l16.envelopes.p2dp.per_sec",  simulator.P2DpEnvelopesPerSec);
                AddGauge("nato_c2.l16.envelopes.p4sp.per_sec",  simulator.P4SpEnvelopesPerSec);
                AddGauge("nato_c2.l16.messages.stddp.per_sec",  simulator.StdDpMsgsPerSec);
                AddGauge("nato_c2.l16.messages.p2dp.per_sec",   simulator.P2DpMsgsPerSec);
                AddGauge("nato_c2.l16.messages.p4sp.per_sec",   simulator.P4SpMsgsPerSec);
            }
            if (advisor != null)
            {
                AddSum("nato_c2.l16.advisor.decisions",  advisor.DecisionsMadeTotal);
                AddSum("nato_c2.l16.advisor.demotions",  advisor.DemotionsTotal);
                AddSum("nato_c2.l16.advisor.promotions", advisor.PromotionsTotal);
            }
            if (bridge != null)
            {
                foreach (var kv in bridge.Peers)
                {
                    AddPeerSum("nato_c2.l16.peer.rx_count", kv.Key, kv.Value.rxCount);
                    AddPeerSum("nato_c2.l16.peer.srej_rx",  kv.Key, kv.Value.srejReceived);
                    AddPeerSum("nato_c2.l16.peer.gaps",     kv.Key, kv.Value.gapsDetected);
                }
            }

            sb.Append("]}]}]}");
            return sb.ToString();
        }

        private static string Attr(string key, string val)
            => $"{{\"key\":\"{Esc(key)}\",\"value\":{{\"stringValue\":\"{Esc(val)}\"}}}}";
        private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
