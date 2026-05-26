// =====================================================================
//  NATO C2 RTS Hybrid — Link16PackingModeTests.cs  (PlayMode)
//  ---------------------------------------------------------------------
//  PlayMode tests for the PPLI burst-mode packing (STD-DP/P2DP/P4SP).
//  Live here because they depend on:
//      • Link16TdmaSimulator.Update() firing at real frame cadence
//        (slot scheduler advances against Time.unscaledTime).
//      • The simulator's rolling 1-second counters incrementing across
//        actual wall-clock seconds.
//      • Stanag5066ArqRetry's retryTimeoutSec / implicitAckSec windows
//        elapsing against Time.realtimeSinceStartup.
//
//  The "ModeFor heuristic" test stays in Tests/Editor/ because it's a
//  pure deterministic check that doesn't need Update to tick.
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
        private const float SampleWindowSec = 3f;
        private const float MinDensityRatio = 1.5f;

        [UnityTest]
        public IEnumerator P2DpEnvelopeDensity_IsAtLeast_1p5x_StdDp()
        {
            var manager  = new GameObject("Manager").AddComponent<NATO_C2_Manager>();
            var sim      = new GameObject("L16").AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            sim.ppliPerEpoch = 2048;

            // Spawn minimal agents.
            for (int i = 0; i < AgentCount; i++)
            {
                var go = new GameObject($"Agent_{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"T-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;
            }

            // ===== Run A: STD-DP default =====
            sim.defaultPackingMode = Link16TdmaSimulator.PackingMode.StdDp;
            foreach (var a in manager.Agents) sim.SetPackingMode(a, null);
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

            float ratio = stdDpMsgs > 0 ? (float)p2DpMsgs / stdDpMsgs : 0f;
            Assert.GreaterOrEqual(ratio, MinDensityRatio,
                $"Expected P2DP density to be at least {MinDensityRatio}× STD-DP density; got ratio={ratio:F2} " +
                $"(STD-DP={stdDpMsgs}, P2DP={p2DpMsgs})");

            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(sim.gameObject);
            Object.DestroyImmediate(manager.gameObject);
        }

        [UnityTest]
        public IEnumerator ArqRecovery_NoEnvelopeLeaks_With50PercentDrops()
        {
            var manager  = new GameObject("Manager-arq").AddComponent<NATO_C2_Manager>();
            var sim      = new GameObject("L16-arq").AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            sim.ppliPerEpoch = 4096;
            sim.useArqRetry  = true;

            for (int i = 0; i < 12; i++)
            {
                var go = new GameObject($"ArqAgent_{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"ARQ-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;
            }

            yield return null;
            var arq = sim.GetComponent<Stanag5066ArqRetry>();
            Assert.IsNotNull(arq, "Simulator should have lazy-attached a Stanag5066ArqRetry when useArqRetry=true.");
            arq.retryTimeoutSec = 2f;
            arq.implicitAckSec  = 1f;
            arq.maxRetries      = 2;
            arq.logEvents       = false;

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

            yield return new WaitForSeconds(arq.retryTimeoutSec * (arq.maxRetries + 1) + 1f);

            Debug.Log($"[ArqRecoveryTest] sent={arq.TotalTransmitted} acked={arq.TotalAcked} " +
                      $"failed={arq.TotalFailed} retried={arq.TotalRetried} outstanding={arq.OutstandingCount}");

            Assert.Greater(arq.TotalTransmitted, 0,
                "ARQ should have tracked envelopes — the simulator publishes PPLI every slot.");
            Assert.Greater(arq.TotalRetried, 0,
                "With 50% periodic drops, ARQ should have retried at least once.");
            int accountedFor = arq.TotalAcked + arq.TotalFailed + arq.OutstandingCount;
            Assert.AreEqual(arq.TotalTransmitted, accountedFor,
                $"Envelope conservation broken — sent={arq.TotalTransmitted} but " +
                $"acked+failed+outstanding={accountedFor}");
            float resolvedFrac = (arq.TotalAcked + arq.TotalFailed) / (float)arq.TotalTransmitted;
            Assert.GreaterOrEqual(resolvedFrac, 0.95f,
                $"Expected ≥95% of envelopes to resolve in the settle window — only {resolvedFrac:P0} did.");

            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(sim.gameObject);
            Object.DestroyImmediate(manager.gameObject);
        }
    }
}
