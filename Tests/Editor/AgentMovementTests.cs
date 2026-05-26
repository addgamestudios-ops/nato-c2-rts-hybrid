// =====================================================================
//  NATO C2 RTS Hybrid — AgentMovementTests.cs
//  ---------------------------------------------------------------------
//  Regression test for the ORCA AxisPair bug:
//
//      "Right-click move only travels along X, not Z."
//
//  Root cause was Nebukam ORCA defaulting to AxisPair.XY (Z-up) while
//  Unity is Y-up. We forced AxisPair.XZ in ORCA.EnsureInitialized.
//
//  This test creates a NATO_C2_Manager + ORCA + HPAStar + Agent at the
//  origin, issues a Move command to (20, 0, 20), ticks the simulation
//  in Play mode for 4 simulated seconds, and asserts:
//
//      • Final X moved meaningfully toward +20
//      • Final Z moved meaningfully toward +20   ← the regression
//
//  If anyone breaks the AxisPair wiring again, this test will fail
//  with "agent ended at X=20, Z=0".
//
//  Why Play mode and not Edit mode: ORCA + the manager run inside
//  MonoBehaviour Update(); we need the Unity test runner to drive the
//  game loop. UnityTest yield WaitForSeconds does exactly that.
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
        // We want a forgiving threshold — Agent might not arrive perfectly
        // by frame N; we only assert MEANINGFUL motion on both axes.
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

            // Force HPAStar to rebuild its grid AFTER we set worldSize etc.
            // (its Awake fired during AddComponent with default values).
            hpa.RebuildAll();

            // Wait one frame so OnEnable → Register fires.
            yield return null;

            // 3. Issue the move command.
            var mgr = NATO_C2_Manager.Instance;
            Assert.IsNotNull(mgr, "NATO_C2_Manager.Instance should be alive after Awake.");
            mgr.SetSelection(new[] { agent });

            var target = new Vector3(20f, 0f, 20f);
            mgr.IssueCommand(CommandOrder.Move, target);

            // Safety net: if HPAStar produced no path (e.g. grid not built or
            // start/goal out of bounds), seed the path directly with the
            // target so the integration step has somewhere to move toward.
            // This isolates the test from pathfinding correctness — the
            // movement-on-both-axes assertion only cares that the manager's
            // tick wiring + ORCA produce XZ motion.
            if (agent.path.Count == 0)
            {
                agent.path.Add(target);
                agent.pathCursor = 0;
            }

            // 4. Drive the sim for 4 simulated seconds. EditMode UnityTest does
            // NOT tick MonoBehaviour.Update() on components without
            // [ExecuteAlways] (we deliberately don't have it on the manager —
            // we don't want agents moving while you edit the scene). So we
            // explicitly drive the public TickForTest helper.
            const float dt = 1f / 60f;
            for (float t = 0f; t < 4.0f; t += dt)
            {
                mgr.TickForTest(dt);
                yield return null;
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
