// =====================================================================
//  NATO C2 RTS Hybrid — AgentMovementTests.cs  (PlayMode)
//  ---------------------------------------------------------------------
//  Regression test for the ORCA AxisPair bug:
//
//      "Right-click move only travels along X, not Z."
//
//  Root cause was Nebukam ORCA defaulting to AxisPair.XY (Z-up) while
//  Unity is Y-up. We forced AxisPair.XZ in ORCA.EnsureInitialized.
//
//  This test lives in PlayMode because:
//      • ORCA uses Burst jobs (Schedule/TryComplete) that only execute
//        under PlayMode's full JobSystem lifecycle.
//      • The manager's MonoBehaviour.Update() fires at real frame
//        cadence in PlayMode; in EditMode it doesn't fire at all
//        without [ExecuteAlways] (which we deliberately avoid in prod
//        so agents don't move while you edit the scene).
// =====================================================================

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using NATO.C2;

namespace NATO.C2.Tests
{
    public class AgentMovementTests
    {
        // Forgiving threshold — agent might not arrive perfectly by frame N;
        // we only assert MEANINGFUL motion on both axes.
        private const float MinPerAxisDisplacement = 5f;

        [UnityTest]
        public IEnumerator Move_Command_Travels_Both_X_And_Z()
        {
            // 1. Build a self-contained manager with the same components the
            //    demo bootstrap uses.
            var managerGo = new GameObject("TestManager",
                typeof(NATO_C2_Manager),
                typeof(ORCA),
                typeof(HPAStar),
                typeof(FormationController),
                typeof(AIAutonomousMode));
            var hpa = managerGo.GetComponent<HPAStar>();
            hpa.worldSize   = new Vector2(120f, 120f);
            hpa.worldOrigin = new Vector2(-60f, -60f);
            hpa.cellSize    = 1f;
            // Rebuild grids now that worldSize/cellSize are set (Awake
            // fired during AddComponent with default values).
            hpa.RebuildAll();

            // 2. Spawn an Agent. We DON'T need a mesh — just position + Agent.
            var agentGo = new GameObject("TestAgent", typeof(Agent));
            agentGo.transform.position = Vector3.zero;
            var agent = agentGo.GetComponent<Agent>();
            agent.affiliation = Affiliation.Friendly;
            agent.unitType    = UnitType.Tank;
            agent.layer       = AltitudeLayer.Ground;
            agent.maxSpeed    = 9f;
            agent.radius      = 1.0f;
            agent.callsign    = "TEST-1";

            // Wait one frame so OnEnable → Register fires.
            yield return null;

            // 3. Issue the move command.
            var mgr = NATO_C2_Manager.Instance;
            Assert.IsNotNull(mgr, "NATO_C2_Manager.Instance should be alive after Awake.");
            mgr.SetSelection(new[] { agent });

            var target = new Vector3(20f, 0f, 20f);
            mgr.IssueCommand(CommandOrder.Move, target);

            // 4. Tick the sim for 4 seconds (PlayMode drives Update at real
            // frame cadence, so we just yield).
            float t = 0f;
            while (t < 4.0f)
            {
                yield return null;
                t += Time.deltaTime;
            }

            // 5. Assert. The agent should have moved meaningfully on BOTH
            //    axes. The exact endpoint doesn't matter — we're guarding
            //    against the regression where Z = 0.
            Vector3 end = agentGo.transform.position;
            Debug.Log($"[AgentMovementTests] Agent ended at {end}");

            Assert.GreaterOrEqual(end.x, MinPerAxisDisplacement,
                $"Agent X displacement insufficient. Position={end}. " +
                "Did the move command actually fire?");
            Assert.GreaterOrEqual(end.z, MinPerAxisDisplacement,
                $"Agent Z displacement insufficient — likely an ORCA AxisPair regression. " +
                $"Position={end}. Expected Z near 20.");

            // 6. Clean up.
            Object.DestroyImmediate(agentGo);
            Object.DestroyImmediate(managerGo);
        }
    }
}
