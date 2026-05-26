// =====================================================================
//  NATO C2 RTS Hybrid — Link16PackingModeTests.cs  (EditMode)
//  ---------------------------------------------------------------------
//  EditMode-safe Link 16 packing tests. The behavioural tests that need
//  real frame ticks (P2DpEnvelopeDensity, ArqRecovery_NoEnvelopeLeaks)
//  live in Tests/PlayMode/Link16PackingModeTests.cs.
//
//  This file only carries deterministic checks that don't need
//  Update to fire — namely the ModeFor heuristic which routes Drone /
//  Infantry / Tank to their default packing modes.
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

            var infGo = new GameObject("Inf");
            var inf   = infGo.AddComponent<Agent>();
            inf.callsign    = "ALPHA-1";
            inf.affiliation = Affiliation.Friendly;
            inf.unitType    = UnitType.Infantry;
            inf.layer       = AltitudeLayer.Ground;

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
