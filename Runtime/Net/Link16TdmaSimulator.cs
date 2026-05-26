// =====================================================================
//  NATO C2 RTS Hybrid — Link16TdmaSimulator.cs
//  ---------------------------------------------------------------------
//  A TDMA-faithful Link 16 (JTIDS / MIDS) network simulator.
//
//  Standards reference:
//      • MIL-STD-6016E — Tactical Digital Information Link J (TADIL J)
//      • MIL-STD-3011  — JREAP appendix (encapsulated J-series over IP)
//      • STANAG 5516   — NATO equivalent of MIL-STD-6016
//
//  TDMA structure (this is what makes Link 16 different from CoT/JREAP):
//      • Epoch         = 12.8 minutes = 768 seconds
//      • Frame         =  12 seconds  (64 frames per epoch)
//      • Time slot     =  7.8125 ms   (1536 slots per frame, 98304 / epoch)
//      • Slot grouping = A/B/C interleave; we collapse to a single index.
//
//  Each terminal subscribes to a TIME SLOT BLOCK (TSB) using a
//  recurrence pattern (1/N over the epoch). For the demo we use simple
//  fractional patterns:
//      • PPLI       — J2.2  — 1/64 over the epoch (≈ every 12 s)
//      • Air track  — J3.2  — 1/16 over the epoch (≈ every 3 s)
//      • Land track — J3.5  — 1/32 over the epoch (≈ every 6 s)
//
//  Network Time Reference (NTR):
//      One terminal is designated NTR — its clock is authoritative.
//      All other terminals slew their TDMA timing to the NTR. We
//      simulate this with a single MonoBehaviour Update() driving the
//      slot index from Unity's high-precision time.
//
//  Source Track Numbers (STN):
//      Each terminal has an octal STN like "00377" (5 octal digits = 15
//      bits = 32 767 max). We assign STNs sequentially from the Agent's
//      hash so they're stable across sessions.
//
//  Output:
//      Publishes RadarTrack + BftPosition messages onto FeedHub at the
//      EXACT timeslot each terminal "owns". A consumer can subscribe to
//      see the same staggered cadence a real Link 16 NPG produces.
//      Production-deploy step is replacing this simulator with a real
//      JREAP-C gateway (CoT ↔ J-series translator) — same wire shape.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-50)]
    [AddComponentMenu("NATO C2/Link 16 TDMA Simulator")]
    public class Link16TdmaSimulator : MonoBehaviour
    {
        // -------------- TDMA constants (MIL-STD-6016) ---------------
        public const float  SlotDurationSec     = 0.0078125f;      // 7.8125 ms
        public const int    SlotsPerFrame       = 1536;
        public const int    FramesPerEpoch      = 64;
        public const int    SlotsPerEpoch       = SlotsPerFrame * FramesPerEpoch; // 98 304
        public const float  EpochDurationSec    = SlotsPerEpoch * SlotDurationSec; // ≈ 768 s

        // -------------- Inspector knobs -----------------------------
        [Header("Network")]
        [Tooltip("If true, drives the entire net's clock — sets the Network Time Reference (NTR). One terminal per net is NTR; rest slew.")]
        public bool isNetworkTimeReference = true;
        [Tooltip("J-series Net Number (decimal). 0–127. Used as the lower 7 bits of every track ID.")]
        [Range(0, 127)] public int netNumber = 1;

        [Header("Cadence (fraction of epoch)")]
        [Tooltip("Number of PPLI (J2.2) reports per epoch per terminal. 64 = every ~12 s.")]
        public int ppliPerEpoch = 64;
        [Tooltip("Number of J3.2 (Air surveillance) reports per epoch per air track.")]
        public int j32PerEpoch = 256;
        [Tooltip("Number of J3.5 (Land surveillance) reports per epoch per ground track.")]
        public int j35PerEpoch = 128;
        [Tooltip("Number of J6.0 (Indirect Interface Unit PPLI) reports per epoch — network-time / NTR broadcasts.")]
        public int j60PerEpoch = 16;
        [Tooltip("Number of J6.1 (Terminal status) reports per epoch per terminal.")]
        public int j61PerEpoch = 32;
        [Tooltip("Number of J7.x (Information Control) messages per epoch — slot reassignments, alerts, track-mgmt commands.")]
        public int j7xPerEpoch = 8;

        [Header("Verbosity")]
        public bool logSlots = false;

        // -------------- PPLI packing modes (real Link 16) -----------
        //  Link 16 supports several burst-mode packing strategies that
        //  trade messages-per-slot against radio range. The terminal
        //  picks one based on mission profile:
        //
        //      • STD-DP (Standard Double Pulse) — 6 msg/slot, ~300 nm.
        //        Default for ground / surface assets in friendly LOS.
        //      • P2DP   (Packed-2 Double Pulse) — 12 msg/slot, ~150 nm.
        //        Dense urban / close-formation ops where many terminals
        //        share a small area and need higher report cadence.
        //      • P4SP   (Packed-4 Single Pulse) — 3 msg/slot, ~500 nm.
        //        Long-haul / over-the-horizon. Fewer messages per slot
        //        but each pulse is longer, extending propagation range.
        //
        //  Each mode runs its OWN envelope. A slot can carry up to one
        //  envelope per mode (real terminals can only transmit one mode
        //  at a time, but different terminals scheduled on the same
        //  slot can be on different modes — that's what we model here).
        public enum PackingMode { StdDp, P2Dp, P4Sp }

        [Header("PPLI burst-mode (STD-DP / P2DP / P4SP)")]
        [Tooltip("STD-DP max messages per slot (real value: 6).")]
        [Range(1, 16)] public int stdDpMaxPerSlot = 6;
        [Tooltip("P2DP max messages per slot (real value: 12, half range).")]
        [Range(1, 16)] public int p2DpMaxPerSlot = 12;
        [Tooltip("P4SP max messages per slot (real value: 3, extended range).")]
        [Range(1, 16)] public int p4SpMaxPerSlot = 3;
        [Tooltip("Default packing mode for newly-discovered terminals.")]
        public PackingMode defaultPackingMode = PackingMode.StdDp;

        [Header("STANAG 5066 ARQ Retry (optional)")]
        [Tooltip("If true, every PPLI envelope is registered with a Stanag5066ArqRetry " +
                 "sibling component (added on demand). Lets dropped envelopes get " +
                 "retransmitted within retryTimeoutSec on the next slot. " +
                 "Default OFF — existing regression tests assume non-ARQ semantics.")]
        public bool useArqRetry = false;
        [Tooltip("Logged retries are counted on the simulator so the HUD can show them.")]
        public int RetriesObserved { get; private set; }
        public int AcksObserved    { get; private set; }
        public int FailuresObserved { get; private set; }
        private Stanag5066ArqRetry _arq;

        /// <summary>
        /// Idempotently attach a Stanag5066ArqRetry sibling component if
        /// one isn't already present. Called automatically from Update
        /// when useArqRetry flips on at runtime, but also exposed so
        /// EditMode tests (where Update doesn't tick) can guarantee the
        /// ARQ is attached after AddComponent + useArqRetry=true.
        /// Returns the attached/discovered component.
        /// </summary>
        public Stanag5066ArqRetry EnsureArqRetryAttached()
        {
            if (_arq != null) return _arq;
            _arq = GetComponent<Stanag5066ArqRetry>();
            if (_arq == null) _arq = gameObject.AddComponent<Stanag5066ArqRetry>();
            return _arq;
        }

        // Fired once per (Agent, slot-they-were-scheduled-on). `packed=true`
        // means the agent fit into its mode's envelope; `packed=false`
        // means the mode's cap was already full this slot and the agent
        // got bumped (counted as a miss). LearnedModeAdvisor subscribes
        // to drive the adaptive picker. Keep this as a simple Action so
        // tests can listen without taking a dependency on the advisor.
        public event System.Action<Agent, PackingMode, bool> OnPpliScheduled;

        // -------------- runtime state -------------------------------
        private FeedHub _hub;
        private NATO_C2_Manager _mgr;
        private float _epochStartTime;
        private int _lastSlot = -1;

        // STN allocation: stable per-Agent across sessions via hash.
        // (We don't actually need a monotonic counter — STNs are derived from
        //  the Agent's EntityId hash so callsign↔STN remains stable across
        //  restarts. The dict is just a hot cache to skip the hash mod.)
        private readonly Dictionary<Agent, ushort> _stns = new Dictionary<Agent, ushort>(256);

        // Per-terminal packing-mode override. If absent, we fall through to
        // ModeFor(agent) which derives a sensible default from unit type.
        private readonly Dictionary<Agent, PackingMode> _modes = new Dictionary<Agent, PackingMode>(256);

        // Reused scratch lists — one per mode — so per-slot PPLI packing
        // doesn't churn GC. Each mode gets its own envelope per slot.
        private readonly List<(Agent agent, ushort stn)> _ppliStdDp = new List<(Agent, ushort)>(8);
        private readonly List<(Agent agent, ushort stn)> _ppliP2Dp  = new List<(Agent, ushort)>(16);
        private readonly List<(Agent agent, ushort stn)> _ppliP4Sp  = new List<(Agent, ushort)>(4);

        // -------------- Public counters (HUD readout) ---------------
        // Rolling 1-second windows. EnvelopesThisSecond[mode] is the
        // count of envelopes published in the last 1 s rolling window.
        // MessagesThisSecond[mode] is total PPLI messages in those
        // envelopes (envelope × per-slot fill).
        public int StdDpEnvelopesPerSec { get; private set; }
        public int P2DpEnvelopesPerSec  { get; private set; }
        public int P4SpEnvelopesPerSec  { get; private set; }
        public int StdDpMsgsPerSec      { get; private set; }
        public int P2DpMsgsPerSec       { get; private set; }
        public int P4SpMsgsPerSec       { get; private set; }
        private int _stdDpEnvAcc, _p2DpEnvAcc, _p4SpEnvAcc;
        private int _stdDpMsgAcc, _p2DpMsgAcc, _p4SpMsgAcc;
        private float _counterRollAt;

        private void Awake()
        {
            _hub = FeedHub.Instance;
            _mgr = NATO_C2_Manager.Instance;
            _epochStartTime = Time.unscaledTime;
            // If the ARQ feature flag is already set on the prefab / inspector,
            // attach immediately. Tests that flip the flag AFTER AddComponent
            // can still call EnsureArqRetryAttached() explicitly, or rely on
            // the lazy-attach near the top of Update() below.
            if (useArqRetry) EnsureArqRetryAttached();
        }

        /// <summary>
        /// Public test hook — does what Update() does for ONE frame, plus
        /// optionally advances the internal epoch clock by `advanceSec` so
        /// the slot counter ticks forward without the test needing real
        /// wall-clock time. EditMode tests use this because MonoBehaviour
        /// Update doesn't fire without [ExecuteAlways].
        /// </summary>
        public void TickForTest(float advanceSec = 0f)
        {
            // Roll the epoch start backward so the slot counter looks like
            // the requested time has elapsed since last Update.
            if (advanceSec > 0f) _epochStartTime -= advanceSec;
            UpdateImpl();
        }

        private void Update() => UpdateImpl();

        private void UpdateImpl()
        {
            // ARQ lazy-attach happens BEFORE the FeedHub/Manager null-check
            // so EditMode tests can `yield return null` once and find the
            // ARQ sibling, even when no FeedHub exists in the test scene.
            if (useArqRetry && _arq == null) EnsureArqRetryAttached();

            if (_hub == null) _hub = FeedHub.Instance;
            if (_mgr == null) _mgr = NATO_C2_Manager.Instance;
            if (_hub == null || _mgr == null) return;

            // Drain any envelopes ARQ wants retransmitted. We don't re-emit
            // the BftPositions (positions are stale by retry-time anyway);
            // we just log the retry. Subscribers that care about deliver-
            // guarantee would consult the ARQ counters directly.
            if (useArqRetry && _arq != null)
            {
                while (_arq.TryDequeueRetry(out ushort esn, out var mode, out int msgs))
                {
                    RetriesObserved++;
                    if (logSlots)
                        Debug.Log($"[L16] ARQ retry esn={esn} mode={ModeTag(mode)} msgs={msgs}");
                }
            }

            // Roll 1-second envelope counters for the HUD.
            if (Time.unscaledTime >= _counterRollAt)
            {
                StdDpEnvelopesPerSec = _stdDpEnvAcc; _stdDpEnvAcc = 0;
                P2DpEnvelopesPerSec  = _p2DpEnvAcc;  _p2DpEnvAcc  = 0;
                P4SpEnvelopesPerSec  = _p4SpEnvAcc;  _p4SpEnvAcc  = 0;
                StdDpMsgsPerSec      = _stdDpMsgAcc; _stdDpMsgAcc = 0;
                P2DpMsgsPerSec       = _p2DpMsgAcc;  _p2DpMsgAcc  = 0;
                P4SpMsgsPerSec       = _p4SpMsgAcc;  _p4SpMsgAcc  = 0;
                _counterRollAt = Time.unscaledTime + 1f;
            }

            // Compute the current slot within the epoch.
            float epochOffset = (Time.unscaledTime - _epochStartTime) % EpochDurationSec;
            int slot = (int)(epochOffset / SlotDurationSec) % SlotsPerEpoch;
            if (slot == _lastSlot) return;

            // For each new slot we crossed since last frame, fire the
            // appropriate J-series messages for any terminal that owns it.
            int from = (_lastSlot + 1) % SlotsPerEpoch;
            int span = ((slot - _lastSlot + SlotsPerEpoch) % SlotsPerEpoch);
            // span can be a large number if framerate stalls; cap it to a
            // small upper bound so we never spam infinite messages.
            if (span > 128) { from = slot; span = 1; }
            for (int s = 0; s < span; s++)
            {
                int slotIdx = (from + s) % SlotsPerEpoch;
                ProcessSlot(slotIdx);
            }
            _lastSlot = slot;
        }

        // ----------------------- per-slot work ----------------------
        private void ProcessSlot(int slot)
        {
            var agents = _mgr.Agents;

            // === J6.0 — NTR network-time broadcast =====================
            //  Only the Network Time Reference terminal sends J6.0. We
            //  drive this off slot index regardless of agent set so the
            //  net stays time-synchronised even with zero terminals.
            if (isNetworkTimeReference)
            {
                int j60Interval = SlotsPerEpoch / Mathf.Max(1, j60PerEpoch);
                if ((slot % j60Interval) == 0)
                    PublishJ60(slot);
            }

            // === J7.x — NPG manager control messages ===================
            //  The NPG manager (the NTR terminal) periodically issues
            //  slot reassignments, network alerts, and track-mgmt commands.
            //  Real fleets receive J7.1 alerts before J3.x updates so
            //  consumers know which slots are about to carry surveillance.
            if (isNetworkTimeReference)
            {
                int j7xInterval = SlotsPerEpoch / Mathf.Max(1, j7xPerEpoch);
                if ((slot % j7xInterval) == 0)
                    PublishJ7x(slot);
            }

            // === J2.2 PPLI (STD-DP / P2DP / P4SP packed envelopes) ======
            //  Real Link 16 PPLI packing puts up to N messages in a slot
            //  where N depends on the burst mode (STD-DP=6, P2DP=12,
            //  P4SP=3). Terminals on different modes don't share a slot
            //  envelope — they each get their own. We group scheduled
            //  terminals by their PackingMode, cap each group at that
            //  mode's max, then emit ONE envelope per (slot, mode).
            //  Anyone scheduled but not packed (rare — only when the net
            //  is oversubscribed) gets bumped to their next interval.
            int ppliIntervalSlots = SlotsPerEpoch / Mathf.Max(1, ppliPerEpoch);
            _ppliStdDp.Clear();
            _ppliP2Dp.Clear();
            _ppliP4Sp.Clear();
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                ushort stn = StnFor(a);
                if (((stn + slot) % ppliIntervalSlots) != 0) continue;

                var mode = ModeFor(a);
                bool packed = false;
                switch (mode)
                {
                    case PackingMode.P2Dp:
                        if (_ppliP2Dp.Count < p2DpMaxPerSlot) { _ppliP2Dp.Add((a, stn)); packed = true; }
                        break;
                    case PackingMode.P4Sp:
                        if (_ppliP4Sp.Count < p4SpMaxPerSlot) { _ppliP4Sp.Add((a, stn)); packed = true; }
                        break;
                    default: // StdDp
                        if (_ppliStdDp.Count < stdDpMaxPerSlot) { _ppliStdDp.Add((a, stn)); packed = true; }
                        break;
                }
                // Notify any LearnedModeAdvisor (or other listener) so it
                // can track per-Agent hit / miss rate without us hard-
                // depending on the advisor class.
                OnPpliScheduled?.Invoke(a, mode, packed);
            }
            if (_ppliStdDp.Count > 0) PublishPpliBatch(_ppliStdDp, slot, PackingMode.StdDp, stdDpMaxPerSlot);
            if (_ppliP2Dp.Count  > 0) PublishPpliBatch(_ppliP2Dp,  slot, PackingMode.P2Dp,  p2DpMaxPerSlot);
            if (_ppliP4Sp.Count  > 0) PublishPpliBatch(_ppliP4Sp,  slot, PackingMode.P4Sp,  p4SpMaxPerSlot);

            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                ushort stn = StnFor(a);

                // === J6.1 Terminal status =================================
                //  Every terminal reports its own status (signal strength,
                //  battery, EW conditions). Real terminals send this once
                //  per few seconds.
                int j61Interval = SlotsPerEpoch / Mathf.Max(1, j61PerEpoch);
                if (((stn * 7 + slot) % j61Interval) == 0)
                    PublishJ61(a, stn, slot);

                // === J3.2 / J3.5 Surveillance =============================
                if (a.affiliation == Affiliation.Friendly)
                {
                    Agent hostile = ClosestHostile(a, range: 80f);
                    if (hostile == null) continue;
                    ushort hostileStn = StnFor(hostile);
                    bool isAir = hostile.layer != AltitudeLayer.Ground;
                    int interval = SlotsPerEpoch / Mathf.Max(1, isAir ? j32PerEpoch : j35PerEpoch);
                    if (((hostileStn + slot) % interval) == 0)
                        PublishSurveillance(a, hostile, stn, hostileStn, slot, isAir);
                }
            }
        }

        // ----------------------- publishers -------------------------

        // === J2.2 PPLI batch — STD-DP / P2DP / P4SP packed envelope ====
        //  One slot, one mode, up to (mode-specific cap) terminals. Real
        //  Link 16 packing encodes messages into the burst pulses of a
        //  slot — STD-DP uses 3 of 5 pulses for messages, leaving 2 for
        //  repetition coding (most robust, ~300 nm); P2DP doubles the
        //  message count at the cost of half the range; P4SP sacrifices
        //  density for extended single-pulse range. We don't simulate the
        //  pulse-level encoding here; we just batch the publishes and
        //  emit one envelope log line per (slot, mode) so the operator
        //  can see packing density per burst mode.
        private void PublishPpliBatch(List<(Agent agent, ushort stn)> batch, int slot, PackingMode mode, int cap)
        {
            string modeTag = ModeTag(mode);
            // HUD counters — accumulate envelopes + messages per mode.
            switch (mode)
            {
                case PackingMode.P2Dp: _p2DpEnvAcc++; _p2DpMsgAcc += batch.Count; break;
                case PackingMode.P4Sp: _p4SpEnvAcc++; _p4SpMsgAcc += batch.Count; break;
                default:               _stdDpEnvAcc++; _stdDpMsgAcc += batch.Count; break;
            }

            // STANAG 5066 ARQ: register this envelope so dropped ones can
            // be retransmitted. The ESN we get back gets embedded in the
            // sourceNet tag so receivers can correlate ACK/NAK by ESN.
            ushort arqEsn = 0;
            bool arqOn = useArqRetry && _arq != null;
            if (arqOn) arqEsn = _arq.Track(mode, batch.Count);
            // Envelope header — one line per (slot, mode) summarising the packed set.
            if (logSlots)
            {
                var sb = new System.Text.StringBuilder(80);
                sb.Append("[L16] slot ").Append(slot)
                  .Append("  J2.2 ").Append(modeTag).Append(" envelope ")
                  .Append(batch.Count).Append("/").Append(cap)
                  .Append("  [");
                for (int i = 0; i < batch.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("STN").Append(ToOct(batch[i].stn));
                }
                sb.Append("]");
                Debug.Log(sb.ToString());
            }
            // Per-terminal BftPosition publishes. Subscribers see N
            // distinct positions arrive within the same Update tick, which
            // matches what they'd see decoding a packed slot.
            string envelopeTag = arqOn
                ? $"Link16/J2.2 {modeTag} NET{netNumber:D3} SLOT{slot} batch={batch.Count} esn={arqEsn:X4}"
                : $"Link16/J2.2 {modeTag} NET{netNumber:D3} SLOT{slot} batch={batch.Count}";
            for (int i = 0; i < batch.Count; i++)
            {
                var a = batch[i].agent;
                var stn = batch[i].stn;
                _hub.PublishBft(new BftPosition
                {
                    unitId         = a.callsign,
                    timestampUtc   = DateTime.UtcNow,
                    latitude       = 0d, longitude = 0d,
                    altitudeMeters = a.transform.position.y,
                    headingDeg     = HeadingFromVec(a.currentVelocity.sqrMagnitude > 0.01f
                                        ? a.currentVelocity.normalized : a.desiredFacing),
                    speedMs        = a.currentVelocity.magnitude,
                    healthPct      = a.maxHealth > 0 ? a.health / a.maxHealth : 0f,
                    ammoPct        = a.maxAmmo   > 0 ? a.ammo   / a.maxAmmo   : 0f,
                    // Affiliation is implicit (only Friendlies broadcast PPLI); we
                    // tag the envelope so log scrapers can group by slot.
                    sourceNet      = $"{envelopeTag} STN{ToOct(stn)} aff={a.affiliation}",
                    confidence     = 0.95f
                });
            }
        }

        // === J6.0 — Indirect Interface Unit PPLI (NTR network time) ====
        //  The NTR (Network Time Reference) terminal broadcasts the
        //  authoritative clock. Other terminals slew their local TDMA
        //  timing to this. We publish it as a Radio message because
        //  there's no NPG-channel struct on FeedHub; consumers can pick
        //  it up by parsing the sourceSensor field.
        private void PublishJ60(int slot)
        {
            float epochSec = slot * SlotDurationSec;
            _hub.PublishRadio(new RadioMessage
            {
                net = "LINK16-NPG",
                timestampUtc = DateTime.UtcNow,
                fromCallsign = $"NTR-NET{netNumber:D3}",
                text = $"J6.0 net-time epoch+{epochSec:F3}s slot={slot} (NTR pulse)",
                severity = RadioSeverity.System,
            });
        }

        // === J6.1 — Terminal status ====================================
        //  Real systems carry: signal strength, BIT result, battery,
        //  EW jammer-detected flags, GPS health. We synthesise a sane
        //  default; production reads from the radio hardware.
        private void PublishJ61(Agent a, ushort stn, int slot)
        {
            // Cheap hash → pretend-RF strength so the simulation looks
            // alive without inventing a full RF propagation model.
            float bit = (stn * 0.0173f) % 1.0f; // 0..1
            int rssi = -90 + (int)(bit * 40f);  // -90..-50 dBm
            _hub.PublishRadio(new RadioMessage
            {
                net = "LINK16-NPG",
                timestampUtc = DateTime.UtcNow,
                fromCallsign = $"STN{ToOct(stn)}",
                text = $"J6.1 terminal-status RSSI={rssi}dBm  BIT=PASS  GPS=3D  EW=clear",
                severity = RadioSeverity.System,
            });
        }

        // === J7.x — Information Control (NPG manager commands) =========
        //  The NTR rotates through 3 representative subtypes so the demo
        //  shows what a real fleet would see during a 10 s window:
        //    J7.0  Network management — slot block reassignment.
        //    J7.1  Network alerts     — degraded link warnings.
        //    J7.2  Track management   — drop / merge track commands.
        private int _j7xPhase = 0;
        private void PublishJ7x(int slot)
        {
            string body;
            switch (_j7xPhase % 3)
            {
                case 0:
                    // Reassign 64 slots to terminal STN00037 starting at slot N+128.
                    body = $"J7.0 slot-block-reassign  STN00037 → block(slot={slot+128} count=64)";
                    break;
                case 1:
                    // Network alert — terminal STN00214 reports JAM detection.
                    body = $"J7.1 network-alert  STN00214 JAM-DETECT  freq=969MHz  bearing=075°";
                    break;
                default:
                    // Track management — drop a stale track.
                    body = $"J7.2 track-mgmt  DROP  TN=0x{(slot ^ 0xBEEF) & 0xFFFF:X4}  reason=stale";
                    break;
            }
            _j7xPhase++;
            _hub.PublishRadio(new RadioMessage
            {
                net = "LINK16-NPG",
                timestampUtc = DateTime.UtcNow,
                fromCallsign = $"NPG-MGR-NET{netNumber:D3}",
                text = body,
                severity = RadioSeverity.Warning,
            });
        }

        private void PublishSurveillance(Agent reporter, Agent target,
                                         ushort reporterStn, ushort targetStn,
                                         int slot, bool isAir)
        {
            string msgName = isAir ? "J3.2 Air" : "J3.5 Land";
            var track = new RadarTrack
            {
                trackId = $"L16-STN{ToOct(targetStn)}-NET{netNumber:D3}",
                timestampUtc = DateTime.UtcNow,
                latitude = 0d, longitude = 0d,
                altitudeMeters = target.transform.position.y,
                courseDeg = HeadingFromVec(target.currentVelocity.sqrMagnitude > 0.01f
                                ? target.currentVelocity.normalized : target.desiredFacing),
                speedMs = target.currentVelocity.magnitude,
                affiliation = target.affiliation,
                classifiedType = target.unitType,
                confidence = 0.85f,
                sourceSensor = $"Link16/{msgName} NET{netNumber:D3} REP-STN{ToOct(reporterStn)} SLOT{slot}",
            };
            _hub.PublishRadar(track);
            if (logSlots) Debug.Log($"[L16] slot {slot}  {msgName} STN{ToOct(targetStn)} ← reported by STN{ToOct(reporterStn)}");
        }

        // ----------------------- helpers ----------------------------
        private ushort StnFor(Agent a)
        {
            if (_stns.TryGetValue(a, out var s)) return s;
            ushort assigned;
            // Try to derive from entity hash for stability. If the hash
            // would collide with an already-assigned STN, increment.
            int hash = a.GetEntityId().GetHashCode() & 0x7FFF;
            assigned = (ushort)Mathf.Clamp(hash, 0x0010, 0x7FFF);
            while (StnInUse(assigned))
                assigned = (ushort)(((assigned + 1) & 0x7FFF) | 0x0010);
            _stns[a] = assigned;
            return assigned;
        }

        private bool StnInUse(ushort candidate)
        {
            foreach (var v in _stns.Values) if (v == candidate) return true;
            return false;
        }

        private Agent ClosestHostile(Agent friend, float range)
        {
            float bestSq = range * range;
            Agent best = null;
            for (int i = 0; i < _mgr.Agents.Count; i++)
            {
                var h = _mgr.Agents[i];
                if (h == null) continue;
                if (h.affiliation != Affiliation.Hostile) continue;
                if (h.health <= 0f) continue;
                float d = (h.transform.position - friend.transform.position).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = h; }
            }
            return best;
        }

        private static string ToOct(ushort stn) => Convert.ToString(stn, 8).PadLeft(5, '0');
        private static float HeadingFromVec(Vector3 v)
        {
            float h = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            return (h + 360f) % 360f;
        }

        // -------------- PPLI burst-mode helpers ---------------------

        /// <summary>
        /// Resolve the current PackingMode for an Agent. Honors any
        /// explicit override set via <see cref="SetPackingMode"/>; falls
        /// back to a heuristic by unit type so the demo "just works":
        ///   • Drones / aircraft  → P4SP (long-range over-the-horizon)
        ///   • Infantry           → P2DP (dense formations)
        ///   • Everything else    → defaultPackingMode (STD-DP usually)
        /// </summary>
        public PackingMode ModeFor(Agent a)
        {
            if (a == null) return defaultPackingMode;
            if (_modes.TryGetValue(a, out var explicitMode)) return explicitMode;
            if (a.layer != AltitudeLayer.Ground) return PackingMode.P4Sp;
            if (a.unitType == UnitType.Infantry)  return PackingMode.P2Dp;
            return defaultPackingMode;
        }

        /// <summary>
        /// Override the packing mode for a specific terminal. Pass null
        /// to clear the override and fall back to the heuristic.
        /// </summary>
        public void SetPackingMode(Agent a, PackingMode? mode)
        {
            if (a == null) return;
            if (mode.HasValue) _modes[a] = mode.Value;
            else               _modes.Remove(a);
        }

        /// <summary>Short label used in envelope logs and sourceNet tags.</summary>
        private static string ModeTag(PackingMode m)
        {
            switch (m)
            {
                case PackingMode.P2Dp: return "P2DP";
                case PackingMode.P4Sp: return "P4SP";
                default:               return "STD-DP";
            }
        }
    }
}
