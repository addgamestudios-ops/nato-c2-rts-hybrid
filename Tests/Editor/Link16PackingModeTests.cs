// =====================================================================
//  NATO C2 RTS Hybrid — Link16PackingModeTests.cs
//  ---------------------------------------------------------------------
//  Regression tests for the PPLI burst-mode packing (STD-DP/P2DP/P4SP)
//  added in Link16TdmaSimulator. The key behavioural invariant we want
//  to lock in:
//
//      P2DP envelope cap is ~2× STD-DP cap. When the net is saturated
//      with P2DP-mode terminals, the envelope message count per second
//      should be ~2× the STD-DP count under the same load.
//
//  Strategy:
//      1. Create NATO_C2_Manager + Link16TdmaSimulator
//      2. Spawn N=24 minimal Agents
//      3. RUN A: leave them on STD-DP default, advance time, record
//         StdDpMsgsPerSec.
//      4. RUN B: flip all 24 to P2DP via SetPackingMode, reset,
//         advance same window, record P2DpMsgsPerSec.
//      5. Assert P2DpMsgsPerSec ≥ 1.5 × StdDpMsgsPerSec
//         (Use 1.5× not 2× because the per-mode cap is 12 vs 6 in
//         the inspector defaults, but the saturation behaviour
//         depends on slot-schedule alignment — we want a robust
//         threshold that won't false-positive under jitter.)
// =====================================================================

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using NATO.C2;
using NATO.C2.Net;

namespace NATO.C2.Tests
{
    public class Link16PackingModeTests
    {
        private const int AgentCount = 24;
        // Window over which we accumulate envelope counts. 3 s lets the
        // simulator's once-per-second roll fire ≥2 times so we get stable
        // sampling even under frame-time jitter.
        private const float SampleWindowSec = 3f;
        // Minimum "double-ish" ratio we require. Real-world P2DP carries
        // 2× messages per slot but slot-schedule alignment cuts that —
        // 1.5× is a robust floor.
        private const float MinDensityRatio = 1.5f;

        [UnityTest]
        public IEnumerator P2DpEnvelopeDensity_IsAtLeast_1p5x_StdDp()
        {
            var manager  = new GameObject("Manager").AddComponent<NATO_C2_Manager>();
            var sim      = new GameObject("L16").AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            // Crank ppliPerEpoch up high so every agent is on a slot
            // every few frames — keeps the sample window tight.
            sim.ppliPerEpoch = 2048;

            // Spawn minimal agents.
            for (int i = 0; i < AgentCount; i++)
            {
                var go = new GameObject($"Agent_{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"T-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;   // Ground → falls through to defaultPackingMode
                // Agent.OnEnable auto-registers with the manager.
            }

            // ===== Run A: STD-DP default =====
            sim.defaultPackingMode = Link16TdmaSimulator.PackingMode.StdDp;
            // Clear any per-agent overrides (defensive).
            foreach (var a in manager.Agents) sim.SetPackingMode(a, null);

            // Drain transients.
            yield return new WaitForSeconds(0.5f);

            int stdDpMsgs = 0;
            float t = 0f;
            while (t < SampleWindowSec)
            {
                stdDpMsgs = Mathf.Max(stdDpMsgs, sim.StdDpMsgsPerSec);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            // ===== Run B: force every terminal to P2DP =====
            foreach (var a in manager.Agents) sim.SetPackingMode(a, Link16TdmaSimulator.PackingMode.P2Dp);

            yield return new WaitForSeconds(0.5f);

            int p2DpMsgs = 0;
            t = 0f;
            while (t < SampleWindowSec)
            {
                p2DpMsgs = Mathf.Max(p2DpMsgs, sim.P2DpMsgsPerSec);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            Debug.Log($"[L16PackingModeTests] peak STD-DP msgs/sec = {stdDpMsgs}; peak P2DP msgs/sec = {p2DpMsgs}");

            // Assert P2DP density ≥ 1.5× STD-DP density.
            float ratio = stdDpMsgs > 0 ? (float)p2DpMsgs / stdDpMsgs : 0f;
            Assert.GreaterOrEqual(ratio, MinDensityRatio,
                $"Expected P2DP density to be at least {MinDensityRatio}× STD-DP density; got ratio={ratio:F2} " +
                $"(STD-DP={stdDpMsgs}, P2DP={p2DpMsgs})");

            // Cleanup so other tests don't see leftover GameObjects.
            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(sim.gameObject);
            Object.DestroyImmediate(manager.gameObject);
        }

        // ====================================================================
        //  ARQ recovery / no-leak test under sustained 50% drops.
        // ====================================================================
        [UnityTest]
        public IEnumerator ArqRecovery_NoEnvelopeLeaks_With50PercentDrops()
        {
            var manager  = new GameObject("Manager-arq").AddComponent<NATO_C2_Manager>();
            var sim      = new GameObject("L16-arq").AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            sim.ppliPerEpoch = 4096;     // saturate the slot schedule
            sim.useArqRetry  = true;

            // Spawn 12 agents.
            for (int i = 0; i < 12; i++)
            {
                var go = new GameObject($"ArqAgent_{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"ARQ-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;
            }

            // One frame so the simulator's Awake / lazy-attach has run.
            yield return null;
            var arq = sim.GetComponent<Stanag5066ArqRetry>();
            Assert.IsNotNull(arq, "Simulator should have lazy-attached a Stanag5066ArqRetry when useArqRetry=true.");
            arq.retryTimeoutSec = 2f;
            arq.implicitAckSec  = 1f;   // < retryTimeout so success path wins for non-dropped
            arq.maxRetries      = 2;
            arq.logEvents       = false;

            // Run 5 s, periodically NAK 50% of outstanding envelopes.
            float endTime = Time.realtimeSinceStartup + 5f;
            float nextDropAt = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup < endTime)
            {
                if (Time.realtimeSinceStartup >= nextDropAt)
                {
                    arq.SimulateRandomDrops(50f);
                    nextDropAt += 0.5f;
                }
                yield return null;
            }

            // Settle window: give in-flight envelopes time to either auto-ACK
            // or hit the retry cap and fail. Need > retryTimeoutSec * maxRetries
            // plus a small margin.
            yield return new WaitForSeconds(arq.retryTimeoutSec * (arq.maxRetries + 1) + 1f);

            Debug.Log($"[ArqRecoveryTest] sent={arq.TotalTransmitted} acked={arq.TotalAcked} " +
                      $"failed={arq.TotalFailed} retried={arq.TotalRetried} outstanding={arq.OutstandingCount}");

            // --- Assertions ---
            Assert.Greater(arq.TotalTransmitted, 0,
                "ARQ should have tracked envelopes — the simulator publishes PPLI every slot.");
            Assert.Greater(arq.TotalRetried, 0,
                "With 50% periodic drops, ARQ should have retried at least once.");
            // Conservation: every transmitted envelope is either acked, failed, or still outstanding.
            int accountedFor = arq.TotalAcked + arq.TotalFailed + arq.OutstandingCount;
            Assert.AreEqual(arq.TotalTransmitted, accountedFor,
                $"Envelope conservation broken — sent={arq.TotalTransmitted} but " +
                $"acked+failed+outstanding={accountedFor}");
            // ≥95% resolved.
            float resolvedFrac = (arq.TotalAcked + arq.TotalFailed) / (float)arq.TotalTransmitted;
            Assert.GreaterOrEqual(resolvedFrac, 0.95f,
                $"Expected ≥95% of envelopes to resolve in the settle window — only {resolvedFrac:P0} did.");

            // Cleanup.
            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(sim.gameObject);
            Object.DestroyImmediate(manager.gameObject);
        }

        [UnityTest]
        public IEnumerator ModeFor_HeuristicAssignsP4SpToDrones()
        {
            var manager = new GameObject("Manager2").AddComponent<NATO_C2_Manager>();
            var sim     = new GameObject("L16-2").AddComponent<Link16TdmaSimulator>();

            var droneGo = new GameObject("Drone");
            var drone   = droneGo.AddComponent<Agent>();
            drone.callsign    = "RAVEN-1";
            drone.affiliation = Affiliation.Friendly;
            drone.unitType    = UnitType.Drone;
            drone.layer       = AltitudeLayer.High;
            // Agent.OnEnable auto-registers with the manager.

            var infGo = new GameObject("Inf");
            var inf   = infGo.AddComponent<Agent>();
            inf.callsign    = "ALPHA-1";
            inf.affiliation = Affiliation.Friendly;
            inf.unitType    = UnitType.Infantry;
            inf.layer       = AltitudeLayer.Ground;
            // Agent.OnEnable auto-registers with the manager.

            yield return null;

            Assert.AreEqual(Link16TdmaSimulator.PackingMode.P4Sp, sim.ModeFor(drone),
                "Aircraft / drone (AltitudeLayer != Ground) should default to P4SP (extended range).");
            Assert.AreEqual(Link16TdmaSimulator.PackingMode.P2Dp, sim.ModeFor(inf),
                "Infantry should default to P2DP (dense formation).");

            Object.DestroyImmediate(droneGo);
            Object.DestroyImmediate(infGo);
            Object.DestroyImmediate(sim.gameObject);
            Object.DestroyImmediate(manager.gameObject);
        }
    }
}
