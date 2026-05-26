// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066FederationBridge.cs
//  ---------------------------------------------------------------------
//  Bridges the local Stanag5066ArqRetry layer to the TAK Server
//  federation by emitting / consuming STANAG 5066 D_PDU frames inside
//  CoT events.
//
//  Wire format:
//      • BINARY (default) — canonical S5066 Annex C 16-byte header +
//        variable payload, base64-encoded inside
//            <s5066 enc="b64">BASE64</s5066>
//        A Wireshark dissector with the S5066 plugin loaded can decode
//        the bytes verbatim.
//      • XML (debug) — when `debugXmlWire = true`, emits the legacy
//            <s5066 type="0" esn="…" .../>
//        attribute form. Useful when you want human-readable wire dumps
//        in CoT XML.
//
//  Outbound flow:
//      • OnEnvelopeTracked / OnEnvelopeRetried → emit D_PDU type 0
//        (DATA-ONLY). If there are pending ACKs from peers, the OLDEST
//        pending ACK is piggybacked into a type-2 (DATA-WITH-ACK)
//        instead — cooperative federation ARQ per S5066 Annex C.
//      • OnEnvelopeAcked → emit pure type-2 (esn=0, ack=acked-esn) so
//        peers know we acked their envelope.
//
//  Inbound flow (FeedHub.OnCot):
//      • type-2 with ack≠0  →  arq.NotifyAck(ack)  (peer acked us)
//      • type-0 / type-4    →  enqueue the ESN into _pendingAcksToPeers
//                              so our next outbound frame piggybacks
//                              an ACK back to the originator.
//
//  Filtering:
//      Every D_PDU carries the senderOp prefix. We skip any inbound
//      D_PDU whose senderOp matches our own — those are echoes of
//      our emissions coming back through the federation.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-40)]
    [RequireComponent(typeof(Stanag5066ArqRetry))]
    [AddComponentMenu("NATO C2/STANAG 5066 Federation Bridge")]
    public class Stanag5066FederationBridge : MonoBehaviour
    {
        [Tooltip("If null, found on Start via FindAnyObjectByType.")]
        public TakServerCotAdapter takAdapter;

        [Tooltip("CoT stale-time for emitted D_PDU events (seconds).")]
        [Range(2f, 60f)] public float staleSec = 10f;

        [Tooltip("If true, emit the legacy XML attribute form instead of " +
                 "the canonical S5066 binary header. Useful for human-readable " +
                 "wire dumps; otherwise leave OFF.")]
        public bool debugXmlWire = false;

        [Tooltip("Cap on pending ACKs awaiting piggyback. Drops oldest beyond this.")]
        [Range(8, 256)] public int pendingAckCap = 64;

        [Tooltip("Echo D_PDU encodings to the console for debugging.")]
        public bool logPdus = false;

        [Tooltip("Emit S5066 SREJ frames when an ESN gap is detected from a peer. " +
                 "Disable to fall back to pure timeout-driven retry.")]
        public bool emitSrejOnGap = true;

        [Tooltip("How long to keep a per-peer record after the last RX before it's pruned.")]
        [Range(5f, 600f)] public float peerForgetSec = 120f;

        // ----- runtime -----
        private Stanag5066ArqRetry _arq;
        private string _ourOp;

        // FIFO of ESNs we owe ACKs for to remote peers. Drained one per
        // outbound type-0; if we run out of outbound traffic the oldest
        // ACKs eventually fall off the cap (matches HF-radio reality —
        // ACKs are best-effort once the channel goes idle).
        private readonly Queue<ushort> _pendingAcksToPeers = new Queue<ushort>(32);

        [Header("Health alarm")]
        [Tooltip("Seconds of silence before a peer is declared DROPPED and a critical " +
                 "RadioMessage is emitted on the local radio panel.")]
        [Range(5f, 300f)] public float dropOutThresholdSec = 30f;
        [Tooltip("If true, emit a System-severity 'recovered' message when a previously " +
                 "dropped peer's traffic resumes.")]
        public bool emitRecoveryMessages = true;

        // Per-sender (peer op) state for gap detection + dashboard stats.
        public struct PeerStats
        {
            public ushort lastSeenEsn;     // highest ESN observed from this peer
            public bool   haveBaseline;    // false until first RX
            public float  lastRxTime;      // unscaledTime of most recent RX
            public int    rxCount;
            public int    gapsDetected;    // SREJs we have emitted for this peer
            public int    srejReceived;    // SREJs this peer has sent us
            public float  lastAckRttSec;   // most recent ACK round-trip we measured
            public bool   alarmed;         // true while peer is past dropOutThresholdSec
        }
        private readonly Dictionary<string, PeerStats> _peers = new Dictionary<string, PeerStats>(8);

        // Sent-at timestamp per outbound ESN — used to compute ACK RTT
        // when a peer ACKs back. Bounded by ARQ window size so this stays small.
        private readonly Dictionary<ushort, float> _sentAt = new Dictionary<ushort, float>(64);

        /// <summary>Read-only snapshot of per-peer stats — used by the federation dashboard.</summary>
        public IReadOnlyDictionary<string, PeerStats> Peers => _peers;
        public string OurOp => _ourOp;

        // ====================================================================
        private void OnEnable()
        {
            _arq = GetComponent<Stanag5066ArqRetry>();
            if (takAdapter == null) takAdapter = UnityEngine.Object.FindAnyObjectByType<TakServerCotAdapter>();
            _ourOp = OperatorIdentity.Instance != null
                ? OperatorIdentity.Instance.stationPrefix
                : "LOCAL";

            if (_arq != null)
            {
                _arq.OnEnvelopeTracked += OnTracked;
                _arq.OnEnvelopeAcked   += OnAcked;
                _arq.OnEnvelopeRetried += OnRetried;
            }
            if (FeedHub.Instance != null) FeedHub.Instance.OnCot += OnCot;
        }

        private void OnDisable()
        {
            if (_arq != null)
            {
                _arq.OnEnvelopeTracked -= OnTracked;
                _arq.OnEnvelopeAcked   -= OnAcked;
                _arq.OnEnvelopeRetried -= OnRetried;
            }
            if (FeedHub.Instance != null) FeedHub.Instance.OnCot -= OnCot;
        }

        // ====================================================================
        //  Outbound: ARQ events → CoT b-l-s5066
        // ====================================================================
        private void OnTracked(ushort esn, Link16TdmaSimulator.PackingMode mode, int msgs)
        {
            // Track send time for ACK-RTT measurement (capped at window size).
            _sentAt[esn] = Time.unscaledTime;
            if (_sentAt.Count > 128) PruneOldSentAt();

            // Cooperative federation ACK — piggyback the oldest pending ACK
            // (if any) into a type-2 frame instead of emitting a pure type-0.
            if (_pendingAcksToPeers.Count > 0)
            {
                ushort ackEsn = _pendingAcksToPeers.Dequeue();
                EmitDPdu(Stanag5066DPdu.DataWithAck(esn, ackEsn, mode, msgs, _ourOp));
            }
            else
            {
                EmitDPdu(Stanag5066DPdu.Data(esn, mode, msgs, _ourOp, note: "tx"));
            }
        }

        // Health-alarm sweep — runs every Update tick. If a peer hasn't
        // been heard from in `dropOutThresholdSec`, fire ONE critical
        // RadioMessage and latch `alarmed=true`. Recovery message fires
        // on the next inbound D_PDU from that peer.
        private void Update()
        {
            if (_peers.Count == 0) return;
            float now = Time.unscaledTime;
            _alarmScratch.Clear();
            foreach (var kv in _peers) _alarmScratch.Add(kv.Key);
            for (int i = 0; i < _alarmScratch.Count; i++)
            {
                string peerOp = _alarmScratch[i];
                var ps = _peers[peerOp];
                if (ps.alarmed) continue;
                if (now - ps.lastRxTime > dropOutThresholdSec)
                {
                    ps.alarmed = true;
                    _peers[peerOp] = ps;
                    FeedHub.Instance?.PublishRadio(new RadioMessage
                    {
                        net = "FED",
                        timestampUtc = System.DateTime.UtcNow,
                        fromCallsign = "S5066-BRIDGE",
                        text = $"peer {peerOp} dropped ({(int)(now - ps.lastRxTime)}s silent)",
                        severity = RadioSeverity.Critical,
                    });
                }
            }
        }
        private readonly List<string> _alarmScratch = new List<string>(8);

        private void PruneOldSentAt()
        {
            // Drop entries older than 30 s — they're past any RTT we care about.
            float cutoff = Time.unscaledTime - 30f;
            var toRemove = new List<ushort>(_sentAt.Count - 64);
            foreach (var kv in _sentAt)
                if (kv.Value < cutoff) toRemove.Add(kv.Key);
            for (int i = 0; i < toRemove.Count; i++) _sentAt.Remove(toRemove[i]);
        }
        private void OnRetried(ushort esn, int attempt)
        {
            // Re-emit type-0 with the same ESN. Receivers idempotently
            // process by ESN so the duplicate doesn't double-count.
            EmitDPdu(Stanag5066DPdu.Data(esn, Link16TdmaSimulator.PackingMode.StdDp,
                                         0, _ourOp, note: "retry-" + attempt));
        }
        private void OnAcked(ushort esn)
        {
            // Pure ACK frame — type-2 with esn=0 (no new data), ack=esn.
            // This is what tells peers their envelope succeeded.
            EmitDPdu(Stanag5066DPdu.DataWithAck(esn: 0, ackEsn: esn,
                                                Link16TdmaSimulator.PackingMode.StdDp,
                                                messageCount: 0, _ourOp));
        }

        private void EmitDPdu(Stanag5066DPdu pdu)
        {
            if (takAdapter == null) return;

            string detailFragment;
            if (debugXmlWire)
            {
                detailFragment = pdu.ToXmlFragment();
            }
            else
            {
                byte[] bytes = pdu.ToBytes();
                string b64 = Convert.ToBase64String(bytes);
                detailFragment = $"<s5066 enc=\"b64\">{b64}</s5066>";
            }
            if (logPdus) Debug.Log($"[S5066-Bridge] TX  {detailFragment}");

            string uid = $"NATO-C2-{_ourOp}-S5066-{pdu.esn:X4}";
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string stale = DateTime.UtcNow.AddSeconds(staleSec).ToString("o", CultureInfo.InvariantCulture);
            string xml =
                "<?xml version='1.0' encoding='UTF-8' standalone='yes'?>" +
                $"<event version=\"2.0\" uid=\"{uid}\" type=\"b-l-s5066\" " +
                $"how=\"m-g\" time=\"{now}\" start=\"{now}\" stale=\"{stale}\">" +
                  "<point lat=\"0\" lon=\"0\" hae=\"0\" ce=\"9999999\" le=\"9999999\"/>" +
                  "<detail>" + detailFragment + "</detail>" +
                "</event>";
            takAdapter.EnqueueRawEvent(xml);
        }

        // ====================================================================
        //  Inbound: CoT b-l-s5066 → ARQ
        // ====================================================================
        private void OnCot(CotEvent ev)
        {
            if (ev.type == null || !ev.type.StartsWith("b-l-s5066")) return;

            // Try binary form first (the canonical wire), then fall back to XML.
            Stanag5066DPdu pdu = default;
            bool parsed = false;
            string detail = ev.xmlDetail ?? "";

            int b64Open = detail.IndexOf("<s5066 enc=\"b64\">", StringComparison.Ordinal);
            if (b64Open >= 0)
            {
                int contentStart = b64Open + "<s5066 enc=\"b64\">".Length;
                int close = detail.IndexOf("</s5066>", contentStart, StringComparison.Ordinal);
                if (close > contentStart)
                {
                    string b64 = detail.Substring(contentStart, close - contentStart);
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(b64);
                        parsed = Stanag5066DPdu.TryParseBytes(bytes, out pdu);
                    }
                    catch (FormatException) { parsed = false; }
                }
            }
            if (!parsed) parsed = Stanag5066DPdu.TryParse(detail, out pdu);
            if (!parsed) return;

            // Drop own echoes.
            if (!string.IsNullOrEmpty(pdu.senderOp) && pdu.senderOp == _ourOp) return;
            if (logPdus) Debug.Log($"[S5066-Bridge] RX  from {pdu.senderOp}: type={pdu.type} esn={pdu.esn:X4} ack={pdu.ackEsn:X4}");

            // ----- Per-peer bookkeeping (dashboard + gap detection) -----
            string peerOp = string.IsNullOrEmpty(pdu.senderOp) ? "?" : pdu.senderOp;
            _peers.TryGetValue(peerOp, out var ps);
            // Health alarm: if this peer was previously alarmed, emit recovery.
            if (ps.alarmed && emitRecoveryMessages)
            {
                FeedHub.Instance?.PublishRadio(new RadioMessage
                {
                    net = "FED",
                    timestampUtc = System.DateTime.UtcNow,
                    fromCallsign = "S5066-BRIDGE",
                    text = $"peer {peerOp} reconnected",
                    severity = RadioSeverity.System,
                });
                ps.alarmed = false;
            }
            ps.lastRxTime = Time.unscaledTime;
            ps.rxCount++;

            switch (pdu.type)
            {
                case Stanag5066DPdu.DPduType.DataWithAck:
                {
                    // Peer is acking esn=ackEsn. Apply locally + record RTT.
                    if (_arq != null && pdu.ackEsn != 0)
                    {
                        if (_sentAt.TryGetValue(pdu.ackEsn, out float sentAt))
                        {
                            ps.lastAckRttSec = Time.unscaledTime - sentAt;
                            _sentAt.Remove(pdu.ackEsn);
                        }
                        _arq.NotifyAck(pdu.ackEsn);
                    }
                    // Type-2 also carries data (esn!=0). Track gaps + owe an ACK.
                    if (pdu.esn != 0)
                    {
                        DetectGapAndMaybeSrej(ref ps, peerOp, pdu.esn);
                        ps.lastSeenEsn = pdu.esn;
                        ps.haveBaseline = true;
                        EnqueueAckForPeer(pdu.esn);
                    }
                    break;
                }

                case Stanag5066DPdu.DPduType.DataOnly:
                {
                    // Receive-side ESN-gap detection: if this peer skipped
                    // an ESN, emit an SREJ asking them to retransmit.
                    DetectGapAndMaybeSrej(ref ps, peerOp, pdu.esn);
                    ps.lastSeenEsn = pdu.esn;
                    ps.haveBaseline = true;
                    EnqueueAckForPeer(pdu.esn);
                    break;
                }

                case Stanag5066DPdu.DPduType.SelectiveReject:
                {
                    // Peer is telling us "your ESN X never arrived; resend it".
                    // If srejRangeEnd is set and > ackEsn, expand and drop
                    // each ESN in [ackEsn..srejRangeEnd] (inclusive, wrapping).
                    ps.srejReceived++;
                    if (_arq != null)
                    {
                        ushort first = pdu.ackEsn;
                        ushort last  = pdu.srejRangeEnd;
                        if (last == 0 || last == first)
                        {
                            if (first != 0) _arq.NotifyDrop(first);
                        }
                        else
                        {
                            // Expand inclusive range with wrap. Cap at 32 to
                            // avoid pathological full-window NAK storms.
                            int span = ((last - first) & 0xFFFF) + 1;
                            if (span > 32) span = 32;
                            for (int i = 0; i < span; i++)
                                _arq.NotifyDrop((ushort)((first + i) & 0xFFFF));
                        }
                    }
                    break;
                }

                case Stanag5066DPdu.DPduType.NonArqData:
                    // Best-effort; no ACK needed and gap detection is meaningless.
                    break;
            }
            _peers[peerOp] = ps;

            // Prune peers we haven't seen in `peerForgetSec`.
            if (Time.unscaledTime - _lastPruneAt > 10f)
            {
                PrunePeers();
                _lastPruneAt = Time.unscaledTime;
            }
        }
        private float _lastPruneAt;

        // SREJ-range coalescing: when a gap of >= this many ESNs is
        // detected, emit ONE range-SREJ instead of N individual ones.
        // Saves wire bytes on a degraded link.
        private const int SrejRangeCoalesceMin = 5;

        private void DetectGapAndMaybeSrej(ref PeerStats ps, string peerOp, ushort esn)
        {
            if (!ps.haveBaseline) return;
            // ESN is a wrapping uint16. We consider "next" = lastSeen + 1
            // (mod 65536). Anything else is a gap. We only SREJ if the
            // observed ESN is "ahead" of expected by 1..16 (small window).
            ushort expected = (ushort)((ps.lastSeenEsn + 1) & 0xFFFF);
            if (esn == expected) return;        // in-order, no gap

            // Compute forward distance modulo 65536. If distance > 16
            // we treat it as a re-sync (peer restarted) rather than a gap.
            ushort dist = (ushort)((esn - expected) & 0xFFFF);
            if (dist == 0 || dist > 16) return;

            ps.gapsDetected += dist;
            if (!emitSrejOnGap) return;

            // Coalesce: ≥ SrejRangeCoalesceMin → emit one range PDU.
            if (dist >= SrejRangeCoalesceMin)
            {
                ushort firstMissing = expected;
                ushort lastMissing  = (ushort)((expected + dist - 1) & 0xFFFF);
                EmitDPdu(Stanag5066DPdu.SelectiveRejectRange(
                    firstMissing, lastMissing, _ourOp,
                    note: $"range-gap-from-{peerOp}-x{dist}"));
            }
            else
            {
                for (int i = 0; i < dist; i++)
                {
                    ushort missing = (ushort)((expected + i) & 0xFFFF);
                    EmitDPdu(Stanag5066DPdu.SelectiveReject(missing, _ourOp,
                        note: "gap-from-" + peerOp));
                }
            }
        }

        private void PrunePeers()
        {
            float cutoff = Time.unscaledTime - peerForgetSec;
            var toRemove = new List<string>();
            foreach (var kv in _peers)
                if (kv.Value.lastRxTime < cutoff) toRemove.Add(kv.Key);
            for (int i = 0; i < toRemove.Count; i++) _peers.Remove(toRemove[i]);
        }

        private void EnqueueAckForPeer(ushort esn)
        {
            // Cap the queue — if peers are sending faster than we emit, the
            // oldest unsent ACKs fall off (HF-realistic; matches what an
            // actual S5066 stack does when ACK_TIMER expires).
            while (_pendingAcksToPeers.Count >= pendingAckCap) _pendingAcksToPeers.Dequeue();
            _pendingAcksToPeers.Enqueue(esn);
        }

        // Test / inspector helpers.
        public int PendingAcksCount => _pendingAcksToPeers.Count;
    }
}
