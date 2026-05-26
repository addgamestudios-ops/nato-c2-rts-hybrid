// =====================================================================
//  NATO C2 RTS Hybrid — JamStormScenarioTest.cs  (PlayMode)
//  ---------------------------------------------------------------------
//  Nightly accelerated soak: loads jam-storm-ci.json (timeScale=10),
//  runs the scenario headlessly, asserts envelope conservation under
//  sustained high drops. Gated by NATO_RUN_JAM_STORM=1 so it only runs
//  in the nightly cron (or via manual workflow_dispatch).
//
//  Why PlayMode: the simulator's slot scheduler and the ARQ retry
//  windows both rely on MonoBehaviour.Update firing at real frame
//  cadence. EditMode coroutines don't drive that. Same reason
//  FederationSoakTests moved here.
//
//  Why inline DTOs: the production scenario file is loaded by
//  NATO.C2.EditorTools.FederationChaosMode which is Editor-only.
//  Rather than make Tests.PlayMode depend on Editor assemblies (which
//  would break PlayMode standalone runs), we declare a parallel set of
//  serializable DTOs here. JsonUtility matches by field name, not by
//  class identity, so the same JSON file loads cleanly into either.
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
    public class JamStormScenarioTest
    {
        // Mirror of NATO.C2.EditorTools.FederationChaosMode.StepKind /
        // .Step / .ExpectedOutcome / .ScenarioFile. Field names MUST
        // match the editor-side DTO so JsonUtility deserializes the
        // same scenario JSON identically.
        public enum StepKind { SetDropRate, PeerDrop, PeerRestore, End }

        [System.Serializable]
        public class Step
        {
            public float    atSec;
            public StepKind kind;
            public float    valueFloat;
            public string   valueText;
        }

        [System.Serializable]
        public class ExpectedOutcome
        {
            public int   minTransmitted;
            public float maxRetryRate;
            public float maxFailRate;
            public int   minAcked;
        }

        [System.Serializable]
        public class ScenarioFile
        {
            public string           name;
            public string           description;
            public float            timeScale;
            public Step[]           steps;
            public ExpectedOutcome  expectedOutcome;
        }

        // Pure-function copy of FederationChaosMode.CheckExpectedOutcome.
        // Same semantics; lives here so we don't need an Editor reference.
        private static string CheckExpectedOutcome(ExpectedOutcome eo, int sent, int acked, int failed, int retried)
        {
            if (eo == null) return null;
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
            return failures.Count == 0 ? null : string.Join("; ", failures);
        }

        [UnityTest]
        public IEnumerator JamStormCi_RunsAndConserves()
        {
            // Gate: only run when explicitly requested (nightly cron sets this).
            if (System.Environment.GetEnvironmentVariable("NATO_RUN_JAM_STORM") != "1")
            {
                Assert.Ignore("NATO_RUN_JAM_STORM not set — this test is for the nightly job.");
            }

            string repoRoot = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(repoRoot, "Tools", "chaos-scenarios", "jam-storm-ci.json");
            if (!File.Exists(path))
                Assert.Ignore($"jam-storm-ci.json not at {path} (sample/package context?)");

            var dto = JsonUtility.FromJson<ScenarioFile>(File.ReadAllText(path));
            Assert.IsNotNull(dto, "JsonUtility returned null");
            Assert.GreaterOrEqual(dto.timeScale, 1f, "jam-storm-ci should declare timeScale");

            // Apply timeScale to compress wall-clock.
            float scale = dto.timeScale > 0f ? dto.timeScale : 1f;
            for (int i = 0; i < dto.steps.Length; i++) dto.steps[i].atSec /= scale;
            System.Array.Sort(dto.steps, (a, b) => a.atSec.CompareTo(b.atSec));

            // Stand up the full stack.
            var manager = new GameObject("Jam-Manager").AddComponent<NATO_C2_Manager>();
            var simGo   = new GameObject("Jam-L16");
            var sim     = simGo.AddComponent<Link16TdmaSimulator>();
            sim.isNetworkTimeReference = true;
            sim.logSlots = false;
            sim.ppliPerEpoch = 4096;
            sim.useArqRetry = true;
            for (int i = 0; i < 14; i++)
            {
                var go = new GameObject($"Jam-{i:D2}");
                go.transform.position = new Vector3(i, 0, 0);
                var a = go.AddComponent<Agent>();
                a.callsign    = $"JAM-{i:D2}";
                a.affiliation = Affiliation.Friendly;
                a.unitType    = UnitType.Tank;
            }
            yield return null;
            var arq = sim.GetComponent<Stanag5066ArqRetry>();
            Assert.IsNotNull(arq);
            arq.retryTimeoutSec = 0.6f;
            arq.implicitAckSec  = 0.25f;
            arq.maxRetries      = 2;

            // Replay.
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
                    if (s.kind == StepKind.SetDropRate)
                        currentDropPct = Mathf.Clamp(s.valueFloat, 0f, 100f);
                    nextStepIdx++;
                }
                if (currentDropPct > 0f && Time.realtimeSinceStartup >= nextDropAt)
                {
                    arq.SimulateRandomDrops(currentDropPct);
                    nextDropAt = Time.realtimeSinceStartup + 0.2f;
                }
                yield return null;
            }
            yield return new WaitForSeconds(arq.retryTimeoutSec * (arq.maxRetries + 1) + 0.5f);

            Debug.Log($"[JamStormCi] sent={arq.TotalTransmitted} acked={arq.TotalAcked} " +
                      $"failed={arq.TotalFailed} retried={arq.TotalRetried} outstanding={arq.OutstandingCount}");

            Assert.Greater(arq.TotalTransmitted, 0);
            Assert.Greater(arq.TotalRetried, 0, "expected retries under sustained 80-95% drops");
            int accounted = arq.TotalAcked + arq.TotalFailed + arq.OutstandingCount;
            Assert.AreEqual(arq.TotalTransmitted, accounted, "envelope conservation");

            string outcomeDetail = CheckExpectedOutcome(
                dto.expectedOutcome,
                arq.TotalTransmitted, arq.TotalAcked, arq.TotalFailed, arq.TotalRetried);
            Assert.IsNull(outcomeDetail, $"expectedOutcome failed: {outcomeDetail}");

            foreach (var a in manager.Agents) if (a != null) Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(simGo);
            Object.DestroyImmediate(manager.gameObject);
        }
    }
}
