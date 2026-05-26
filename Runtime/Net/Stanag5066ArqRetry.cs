// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066ArqRetry.cs
//  ---------------------------------------------------------------------
//  Lightweight selective-repeat ARQ retry layer sitting on top of the
//  Link 16 PPLI envelopes published by Link16TdmaSimulator.
//
//  Standards reference:
//      • STANAG 5066 — HF Subnetwork Profile, Annex C (ARQ Service)
//        Defines selective-repeat ARQ with up to 127 outstanding PDUs,
//        sliding-window flow control, and the D_PDU/C_PDU types.
//
//  Why this exists for L16:
//      Real Link 16 has its own slot-level repetition coding (each
//      message is transmitted in N pulses across a slot, with FEC).
//      But when a whole slot envelope is dropped (jamming, terrain
//      mask, terminal off-net), the data is gone. STANAG 5066-style
//      ARQ tracks envelope-sequence-numbers (ESNs), lets receivers
//      NAK missing ESNs, and lets senders retransmit on the next
//      available slot.
//
//      This is the layer that turns "best-effort PPLI" into
//      "guaranteed delivery within N retries", which is what
//      operators expect when they say "the network is up".
//
//  What this MonoBehaviour does:
//      • Wraps every published PPLI batch with an envelope sequence
//        number (esn, monotonic 0..65535).
//      • Tracks each esn in an outstanding-window for `retryTimeoutSec`.
//      • On simulated drop (`Simulate Drop` action, or external
//        NAK via NotifyDrop()), re-publishes the envelope on the
//        next slot in the same packing mode.
//      • Gives up after `maxRetries` and logs a delivery failure.
//
//      This is intentionally NOT wired into Link16TdmaSimulator yet
//      — the simulator publishes BftPositions directly. We treat
//      this component as a "shadow accountant" that records ESNs and
//      lets the operator drive simulated drops/retries from the
//      Editor for testing. The next iteration will refactor the
//      simulator to publish through the ARQ layer.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/STANAG 5066 ARQ Retry")]
    public class Stanag5066ArqRetry : MonoBehaviour
    {
        // ----- Public knobs -----
        [Tooltip("How long to wait for implicit ACK (next-slot pickup) before scheduling a retry.")]
        [Range(0.5f, 30f)] public float retryTimeoutSec = 4f;

        [Tooltip("Maximum number of retransmissions before declaring delivery failure.")]
        [Range(0, 8)] public int maxRetries = 3;

        [Tooltip("Sliding-window size (per S5066 Annex C, up to 127).")]
        [Range(8, 127)] public int windowSize = 64;

        [Tooltip("If > 0, envelopes that go this many seconds without being NAK'd " +
                 "are auto-ACK'd (models Link 16 'no NAK heard = delivered' semantics). " +
                 "Must be less than retryTimeoutSec or retries will fire first.")]
        [Range(0f, 30f)] public float implicitAckSec = 0f;

        [Tooltip("If true, log each ARQ event (transmit, retry, fail) to the console.")]
        public bool logEvents = false;

        // Listeners that need a per-envelope hook (the D_PDU codec wires
        // here to emit type-0 DATA-ONLY on the wire).
        public event System.Action<ushort, Link16TdmaSimulator.PackingMode, int> OnEnvelopeTracked;
        public event System.Action<ushort> OnEnvelopeAcked;
        public event System.Action<ushort> OnEnvelopeFailed;
        public event System.Action<ushort, int> OnEnvelopeRetried;   // (esn, attempt)

        // ----- Public counters -----
        public int OutstandingCount => _outstanding.Count;
        public int TotalTransmitted { get; private set; }
        public int TotalRetried     { get; private set; }
        public int TotalAcked       { get; private set; }
        public int TotalFailed      { get; private set; }

        // ----- Internal state -----
        private struct Envelope
        {
            public ushort esn;                     // envelope sequence number
            public Link16TdmaSimulator.PackingMode mode;
            public int messageCount;
            public float sentAt;                   // unscaledTime
            public int retryCount;
        }

        private readonly Dictionary<ushort, Envelope> _outstanding = new Dictionary<ushort, Envelope>(128);
        private readonly Queue<ushort> _retryQueue = new Queue<ushort>(16);
        private ushort _nextEsn = 0;

        // ====================================================================
        //  Public API — used by Link16TdmaSimulator (or any other producer)
        //  to register an envelope it just published.
        // ====================================================================

        /// <summary>
        /// Register an envelope that was just transmitted. Returns the ESN
        /// that should be embedded in the envelope header so receivers can
        /// NAK by ESN.
        /// </summary>
        public ushort Track(Link16TdmaSimulator.PackingMode mode, int messageCount)
        {
            if (_outstanding.Count >= windowSize)
            {
                // Window is full — drop the oldest unacknowledged one and
                // count it as a failure (real S5066 stalls the sender; we
                // prefer fail-open for the demo).
                TimeoutOldest();
            }

            ushort esn = _nextEsn;
            _nextEsn = unchecked((ushort)(_nextEsn + 1));
            _outstanding[esn] = new Envelope
            {
                esn = esn,
                mode = mode,
                messageCount = messageCount,
                sentAt = Time.unscaledTime,
                retryCount = 0
            };
            TotalTransmitted++;
            if (logEvents) Debug.Log($"[S5066-ARQ] TX esn={esn} mode={mode} msgs={messageCount}");
            OnEnvelopeTracked?.Invoke(esn, mode, messageCount);
            return esn;
        }

        /// <summary>
        /// Receiver-side NAK. Mark an ESN as dropped — schedule a retry on
        /// the next slot if we still have retry budget.
        /// </summary>
        public void NotifyDrop(ushort esn)
        {
            if (!_outstanding.TryGetValue(esn, out var env)) return;
            ScheduleRetry(env);
        }

        /// <summary>
        /// Receiver-side ACK. Drop the envelope from the outstanding window.
        /// </summary>
        public void NotifyAck(ushort esn)
        {
            if (_outstanding.Remove(esn))
            {
                TotalAcked++;
                if (logEvents) Debug.Log($"[S5066-ARQ] ACK esn={esn}");
                OnEnvelopeAcked?.Invoke(esn);
            }
        }

        /// <summary>
        /// Dequeue the next ESN that needs to be retransmitted. Returns
        /// false if nothing is pending. Callers retransmit, then call
        /// Track() again (or pass the same esn back via Retransmit()).
        /// </summary>
        public bool TryDequeueRetry(out ushort esn, out Link16TdmaSimulator.PackingMode mode, out int messageCount)
        {
            while (_retryQueue.Count > 0)
            {
                ushort candidate = _retryQueue.Dequeue();
                if (_outstanding.TryGetValue(candidate, out var env))
                {
                    esn = env.esn;
                    mode = env.mode;
                    messageCount = env.messageCount;
                    return true;
                }
            }
            esn = 0;
            mode = Link16TdmaSimulator.PackingMode.StdDp;
            messageCount = 0;
            return false;
        }

        // ====================================================================
        //  Internal: retry / timeout machinery
        // ====================================================================

        private void Update()
        {
            if (_outstanding.Count == 0) return;
            float now = Time.unscaledTime;

            // ----- Implicit ACK sweep (success path) ------------------
            //  Envelopes that have sat for `implicitAckSec` without being
            //  NAK'd are auto-ACK'd. This models real Link 16 semantics:
            //  if you haven't heard a NAK by the slot turn-around window,
            //  the receiver got it.
            if (implicitAckSec > 0f)
            {
                _timeoutScratch.Clear();
                foreach (var kv in _outstanding)
                {
                    if (now - kv.Value.sentAt > implicitAckSec)
                        _timeoutScratch.Add(kv.Key);
                }
                for (int i = 0; i < _timeoutScratch.Count; i++)
                    NotifyAck(_timeoutScratch[i]);
            }

            // ----- Retry-timeout sweep (failure path) -----------------
            //  Anything still outstanding past retryTimeoutSec gets a
            //  retry scheduled. Only reaches this branch if implicit-ACK
            //  is off OR retryTimeoutSec < implicitAckSec (mis-config).
            if (_outstanding.Count == 0) return;
            _timeoutScratch.Clear();
            foreach (var kv in _outstanding)
            {
                if (now - kv.Value.sentAt > retryTimeoutSec)
                    _timeoutScratch.Add(kv.Key);
            }
            for (int i = 0; i < _timeoutScratch.Count; i++)
            {
                if (_outstanding.TryGetValue(_timeoutScratch[i], out var env))
                    ScheduleRetry(env);
            }
        }
        private readonly List<ushort> _timeoutScratch = new List<ushort>(16);

        private void ScheduleRetry(Envelope env)
        {
            if (env.retryCount >= maxRetries)
            {
                _outstanding.Remove(env.esn);
                TotalFailed++;
                if (logEvents) Debug.LogWarning($"[S5066-ARQ] FAIL esn={env.esn} after {env.retryCount} retries (delivery aborted)");
                OnEnvelopeFailed?.Invoke(env.esn);
                return;
            }
            env.retryCount++;
            env.sentAt = Time.unscaledTime;       // reset timer
            _outstanding[env.esn] = env;
            _retryQueue.Enqueue(env.esn);
            TotalRetried++;
            if (logEvents) Debug.Log($"[S5066-ARQ] RETRY esn={env.esn} attempt={env.retryCount}/{maxRetries}");
            OnEnvelopeRetried?.Invoke(env.esn, env.retryCount);
        }

        private void TimeoutOldest()
        {
            // Find the oldest entry and force-fail it. Linear scan is fine
            // — windowSize ≤ 127.
            ushort oldestEsn = 0;
            float oldestAt = float.MaxValue;
            bool found = false;
            foreach (var kv in _outstanding)
            {
                if (kv.Value.sentAt < oldestAt)
                {
                    oldestAt = kv.Value.sentAt;
                    oldestEsn = kv.Key;
                    found = true;
                }
            }
            if (!found) return;
            _outstanding.Remove(oldestEsn);
            TotalFailed++;
            if (logEvents) Debug.LogWarning($"[S5066-ARQ] WINDOW-FULL drop esn={oldestEsn}");
        }

        /// <summary>
        /// Debug helper — randomly drop `percent`% of the currently-outstanding
        /// envelopes to simulate jamming / terrain mask. Used by the Editor
        /// debug window.
        /// </summary>
        public void SimulateRandomDrops(float percent)
        {
            if (percent <= 0f) return;
            _timeoutScratch.Clear();
            foreach (var k in _outstanding.Keys) _timeoutScratch.Add(k);
            for (int i = 0; i < _timeoutScratch.Count; i++)
            {
                if (Random.value < percent / 100f)
                    NotifyDrop(_timeoutScratch[i]);
            }
        }
    }
}
