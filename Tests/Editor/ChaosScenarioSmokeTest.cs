// =====================================================================
//  NATO C2 RTS Hybrid — ChaosScenarioSmokeTest.cs
//  ---------------------------------------------------------------------
//  Headless runner for the smoke chaos scenario. Reads
//  Tools/chaos-scenarios/smoke.json from disk, replays each step on a
//  live Stanag5066ArqRetry, and asserts the run produces sane stats:
//
//      • some envelopes transmitted
//      • some retries (because the scenario hits 20% drops)
//      • envelope conservation (sent == acked + failed + outstanding)
//      • the scenario JSON itself parsed (catches accidental schema breaks)
//
//  This is a separate test from the broader FederationSoakTests so a
//  scenario-library regression fails by itself with a clear pointer at
//  the canonical chaos scripts.
// =====================================================================

using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using NATO.C2;
using NATO.C2.Net;

namespace NATO.C2.Tests
{
    public class ChaosScenarioSmokeTest
    {
        [UnityTest]
        public IEnumerator Smoke_LoadsAndRunsToCompletion()
        {
            // -------- 1. Locate + parse the scenario JSON. --------
            // Walk up from Application.dataPath to repo root.
            string repoRoot = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(repoRoot, "Tools", "chaos-scenarios", "smoke.json");
            // For sample-mode (test runs from Assets/Samples/...), fall back to the package source.
            if (!File.Exists(path))
            {
                // sample location: ../Library/PackageCache/<pkg>@<hash>/Tools/chaos-scenarios/smoke.json
                // we can't easily resolve that; just skip if not found.
                Assert.Ignore($"smoke.json not found at {path} — running from sample mode without source tree.");
            }
            string json = File.ReadAllText(path);
            var dto = JsonUtility.FromJson<NATO.C2.EditorTools.FederationChaosMode.ScenarioFile>(json);
            Assert.IsNotNull(dto, "JsonUtility returned null — bad JSON?");
            Assert.IsNotNull(dto.steps, "ScenarioFile.steps was null");
            Assert.Greater(dto.steps.Length, 0, "smoke scenario should have at least one step");
            Assert.AreEqual("smoke", dto.name);

            // -------- 2. Stand up the full stack. --------
            var manager = new GameObject("Smoke-Manager").AddComponent<NATO_C2_Manager>();
            var simGo   = new GameObject("Smoke-L16");
            var sim     = simGo.AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            sim.ppliPerEpoch = 4096;
            sim.useArqRetry  = true;

            for (int i = 0; i < 12; i++)
            {
                var go = new GameObject($"Smoke-Agent-{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"SMK-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;
            }
            yield return null;

            var arq = sim.GetComponent<Stanag5066ArqRetry>();
            Assert.IsNotNull(arq);
            arq.retryTimeoutSec = 1.0f;
            arq.implicitAckSec  = 0.5f;
            arq.maxRetries      = 2;

            // -------- 3. Replay the scenario. --------
            // Apply timeScale (if set on the JSON) so CI can run a 40 s scenario in 4 s.
            float scale = dto.timeScale > 0f ? dto.timeScale : 1f;
            for (int i = 0; i < dto.steps.Length; i++) dto.steps[i].atSec /= scale;
            // Sort steps by atSec just in case.
            System.Array.Sort(dto.steps, (a, b) => a.atSec.CompareTo(b.atSec));
            float startedAt = Time.realtimeSinceStartup;
            float currentDropPct = 0f;
            float nextDropAt = startedAt;
            int   nextStepIdx = 0;
            float endSec      = dto.steps[dto.steps.Length - 1].atSec + 0.1f;

            while (Time.realtimeSinceStartup - startedAt < endSec)
            {
                float t = Time.realtimeSinceStartup - startedAt;
                while (nextStepIdx < dto.steps.Length && dto.steps[nextStepIdx].atSec <= t)
                {
                    var s = dto.steps[nextStepIdx];
                    if (s.kind == NATO.C2.EditorTools.FederationChaosMode.StepKind.SetDropRate)
                        currentDropPct = Mathf.Clamp(s.valueFloat, 0f, 100f);
                    nextStepIdx++;
                }
                if (currentDropPct > 0f && Time.realtimeSinceStartup >= nextDropAt)
                {
                    arq.SimulateRandomDrops(currentDropPct);
                    nextDropAt = Time.realtimeSinceStartup + 0.5f;
                }
                yield return null;
            }

            // Settle.
            yield return new WaitForSeconds(arq.retryTimeoutSec * (arq.maxRetries + 1) + 0.5f);

            Debug.Log($"[ChaosScenarioSmokeTest] sent={arq.TotalTransmitted} acked={arq.TotalAcked} " +
                      $"failed={arq.TotalFailed} retried={arq.TotalRetried} outstanding={arq.OutstandingCount}");

            // -------- 4. Assertions. --------
            Assert.Greater(arq.TotalTransmitted, 0, "smoke scenario should have published envelopes");
            int accounted = arq.TotalAcked + arq.TotalFailed + arq.OutstandingCount;
            Assert.AreEqual(arq.TotalTransmitted, accounted,
                $"conservation: sent={arq.TotalTransmitted} != acked+failed+outstanding={accounted}");

            // Acceptance criteria from the scenario JSON, if declared.
            var outcome = NATO.C2.EditorTools.FederationChaosMode.CheckExpectedOutcome(
                dto.expectedOutcome,
                arq.TotalTransmitted, arq.TotalAcked, arq.TotalFailed, arq.TotalRetried);
            Assert.IsTrue(outcome.passed, $"expectedOutcome failed: {outcome.detail}");

            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(simGo);
            Object.DestroyImmediate(manager.gameObject);
        }
    }
}
