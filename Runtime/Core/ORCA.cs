// =====================================================================
//  NATO C2 RTS Hybrid — ORCA.cs
//  ---------------------------------------------------------------------
//  Nebukam ORCA bridge. Nebukam ORCA (com.nebukam.orca) is a Job-System
//  + Burst-compiled reciprocal collision avoidance package. We wrap it
//  with the smallest possible surface so the rest of the codebase only
//  sees a tidy Solve(...) entry point.
//
//  We deliberately do NOT `using Nebukam.ORCA;` here: Nebukam's
//  top-level class is also named ORCA, and we have a class by the same
//  name. Fully qualifying every Nebukam type avoids ambiguity for any
//  future caller that imports both namespaces.
// =====================================================================

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/ORCA")]
    public class ORCA : MonoBehaviour
    {
        [Header("Tuning")]
        [Tooltip("Multiplier applied to dynamic-obstacle repulsion. Higher = more cautious agents.")]
        [Range(0.5f, 4f)] public float dynamicObstacleWeight = 2.0f;

        [Tooltip("Hard floor on agent radius reported to ORCA (catches mis-set Inspector values).")]
        [Min(0.05f)] public float minAgentRadius = 0.2f;

        [Header("Diagnostics")]
        public bool drawDebug = false;

        // ---------- Nebukam runtime objects -----------------------------
        //  ORCABundle ships agents/raycasts/static-obstacles/dynamic-obstacles
        //  groups as properties and a single Schedule()/TryComplete() loop.
        private Nebukam.ORCA.ORCABundle<Nebukam.ORCA.Agent> _bundle;

        // Index from our Agent → Nebukam Agent so we can route results back.
        private readonly Dictionary<Agent, Nebukam.ORCA.Agent> _bridgeAgents = new Dictionary<Agent, Nebukam.ORCA.Agent>(256);
        // Dynamic obstacle bridge.
        private readonly Dictionary<DynamicObstacle, Nebukam.ORCA.Obstacle> _bridgeObstacles = new Dictionary<DynamicObstacle, Nebukam.ORCA.Obstacle>(64);

        private bool _initialized;
        private bool _scheduled;

        // =================================================================
        //  Unity lifecycle
        // =================================================================
        private void OnEnable()  => EnsureInitialized();
        private void OnDisable()
        {
            if (_bundle != null)
            {
                if (_scheduled) { try { _bundle.orca.TryComplete(); } catch { /* ignore on teardown */ } }
                _bundle.Dispose();
                _bundle = null;
            }
            _bridgeAgents.Clear();
            _bridgeObstacles.Clear();
            _initialized = false;
            _scheduled = false;
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _bundle = new Nebukam.ORCA.ORCABundle<Nebukam.ORCA.Agent>();
            // CRITICAL: tell Nebukam ORCA to operate on the X-Z plane (Y up),
            // matching Unity's convention. Without this it defaults to X-Y
            // (Z up, like Godot), which would mean ORCA only resolves X and Y
            // velocities — Z would never get touched and ground units would
            // only move along X (the symptom we hit). Set on both the bundle
            // and the underlying ORCA solver so dynamic obstacles / static
            // obstacles project to the same plane.
            try
            {
                _bundle.plane = Nebukam.Common.AxisPair.XZ;
                _bundle.orca.plane = Nebukam.Common.AxisPair.XZ;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ORCA] Could not set AxisPair.XZ: {e.Message}");
            }
            _initialized = true;
        }

        // =================================================================
        //  Public — called by NATO_C2_Manager each Tick.
        //  ORCABundle pipeline is async/pipelined: TryComplete() blocks on
        //  the previously-scheduled job, then Schedule() kicks off the next.
        // =================================================================
        public void Solve(IReadOnlyList<Agent> agents, float dt, bool layerAware)
        {
            EnsureInitialized();

            // 1) Wait for the previously-scheduled solve to finish (if any).
            if (_scheduled)
            {
                try { _bundle.orca.TryComplete(); }
                catch (Exception ex) { Debug.LogException(ex, this); }
                _scheduled = false;

                // 2) Write resolved velocities back to our Agents.
                foreach (var kvp in _bridgeAgents)
                {
                    if (kvp.Key == null) continue;
                    float3 v = kvp.Value.velocity;
                    kvp.Key.currentVelocity = new Vector3(v.x, v.y, v.z);
                }
            }

            // 3) Sync the bridge dictionary with the live Agent set.
            SyncAgentBridges(agents, layerAware);

            // 4) Schedule next ORCA step (Burst jobs).
            try
            {
                _bundle.orca.Schedule(dt);
                _scheduled = true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                _scheduled = false;
            }
        }

        // =================================================================
        //  Bridge sync
        // =================================================================
        private void SyncAgentBridges(IReadOnlyList<Agent> agents, bool layerAware)
        {
            // Add new agents.
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                if (!_bridgeAgents.TryGetValue(a, out var ia))
                {
                    ia = _bundle.agents.Add((float3)a.transform.position) as Nebukam.ORCA.Agent;
                    _bridgeAgents[a] = ia;
                }
                if (ia == null) continue;
                ia.pos              = (float3)a.transform.position;
                ia.radius           = Mathf.Max(minAgentRadius, a.radius);
                ia.maxSpeed         = a.maxSpeed;
                ia.maxNeighbors     = a.maxNeighbours;
                ia.neighborDist     = a.neighbourDistance;
                ia.timeHorizon      = a.timeHorizon;
                ia.timeHorizonObst  = a.timeHorizonObstacle;
                ia.prefVelocity     = (float3)a.preferredVelocity;
                // Layer mask: each AltitudeLayer maps to a single bit. Layer-aware
                // mode means agent only sees neighbours sharing its layer.
                ia.layerOccupation  = (Nebukam.ORCA.ORCALayer)LayerBitFor(a.layer);
                ia.layerIgnore      = layerAware
                    ? (Nebukam.ORCA.ORCALayer)(~LayerBitFor(a.layer))
                    : Nebukam.ORCA.ORCALayer.NONE;
            }
            // Drop disposed agents.
            if (_bridgeAgents.Count > agents.Count)
            {
                _scratchToRemove.Clear();
                foreach (var kvp in _bridgeAgents)
                    if (kvp.Key == null || !ContainsRef(agents, kvp.Key))
                        _scratchToRemove.Add(kvp.Key);
                foreach (var dead in _scratchToRemove)
                {
                    if (_bridgeAgents.TryGetValue(dead, out var ia)) _bundle.agents.Remove(ia);
                    _bridgeAgents.Remove(dead);
                }
            }
        }

        private static readonly List<Agent> _scratchToRemove = new List<Agent>(8);
        private static bool ContainsRef(IReadOnlyList<Agent> list, Agent a)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], a)) return true;
            return false;
        }

        // =================================================================
        //  Dynamic obstacle plumbing — used by DynamicObstacle.OnEnable/Disable.
        // =================================================================
        public void RegisterDynamic(DynamicObstacle obs)
        {
            EnsureInitialized();
            if (obs == null || _bridgeObstacles.ContainsKey(obs)) return;
            var poly = ToFloat3Array(obs.GetWorldPolygon());
            var bridge = _bundle.dynamicObstacles.Add(poly) as Nebukam.ORCA.Obstacle;
            _bridgeObstacles[obs] = bridge;
        }

        public void UnregisterDynamic(DynamicObstacle obs)
        {
            if (obs == null) return;
            if (_bridgeObstacles.TryGetValue(obs, out var b)) _bundle.dynamicObstacles.Remove(b);
            _bridgeObstacles.Remove(obs);
        }

        public void RegisterStatic(IEnumerable<Vector3> worldPolygon)
        {
            EnsureInitialized();
            var poly = ToFloat3Array(worldPolygon);
            _bundle.staticObstacles.Add(poly);
        }

        // =================================================================
        //  Helpers
        // =================================================================
        private static float3[] ToFloat3Array(IEnumerable<Vector3> verts)
        {
            // Materialise to float3[] — Nebukam Add(...) overloads accept IList<float3>.
            var tmp = new List<float3>(8);
            foreach (var v in verts) tmp.Add((float3)v);
            return tmp.ToArray();
        }

        public static int LayerBitFor(AltitudeLayer l) => 1 << (int)l;

        private void OnDrawGizmosSelected()
        {
            if (!drawDebug || _bridgeAgents == null) return;
            Gizmos.color = Color.cyan;
            foreach (var kvp in _bridgeAgents)
            {
                if (kvp.Key == null) continue;
                Gizmos.DrawWireSphere(kvp.Key.transform.position, kvp.Key.radius);
                Gizmos.DrawLine(kvp.Key.transform.position,
                                kvp.Key.transform.position + kvp.Key.preferredVelocity);
            }
        }
    }
}
