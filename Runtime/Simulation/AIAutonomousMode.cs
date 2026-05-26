// =====================================================================
//  NATO C2 RTS Hybrid — AIAutonomousMode.cs   (Mythos AI)
//  ---------------------------------------------------------------------
//  Real-time advisor + optional autonomous executor. Each tick:
//
//      • Threat prediction — projects every hostile forward 8–12 s and
//        rasterises a soft "danger field" against friendly positions.
//      • Opportunity detection — scans for clusters of hostiles with
//        no friendly cover and flags them as engageable.
//      • Evasion vectors — if an agent is inside a danger field, the
//        AI biases its preferredVelocity along the safe gradient.
//
//  When AutonomousMode is OFF, this component still publishes threat
//  data to the HUD (TacticalHUD reads ThreatField/Opportunities). When
//  ON, it also rewrites preferredVelocity for friendly agents.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/Mythos AI (Autonomous Mode)")]
    public class AIAutonomousMode : MonoBehaviour
    {
        [Header("Prediction")]
        [Tooltip("Lookahead window for projecting hostile trajectories (seconds).")]
        [Range(4f, 20f)] public float lookaheadSeconds = 10f;
        [Tooltip("Radius around a hostile inside which an agent is considered at risk.")]
        [Min(1f)] public float threatRadius = 7f;
        [Range(0f, 1f)] public float evasionWeight = 0.6f;

        [Header("Opportunity Detection")]
        [Tooltip("Minimum hostile cluster size to count as an opportunity.")]
        [Min(2)] public int opportunityClusterMin = 3;
        [Tooltip("Maximum spread (metres) between hostiles in one cluster.")]
        [Min(2f)] public float opportunityClusterRadius = 8f;

        // ---------- Public read-outs (HUD subscribes) -------------------
        public readonly List<ThreatBubble> ThreatField = new List<ThreatBubble>(64);
        public readonly List<OpportunityCluster> Opportunities = new List<OpportunityCluster>(16);

        public struct ThreatBubble
        {
            public Vector3 centre;
            public float   radius;
            public float   timeToImpact;
            public Agent   source;
        }

        public struct OpportunityCluster
        {
            public Vector3 centre;
            public int     count;
            public float   spread;
        }

        // =================================================================
        //  Called by the Manager whether or not Autonomous Mode is on.
        // =================================================================
        public void AdviseAgents(IReadOnlyList<Agent> agents, float dt)
        {
            BuildThreatField(agents);
            BuildOpportunities(agents);

            // Only rewrite velocities if user explicitly handed over control.
            if (NATO_C2_Manager.Instance == null || !NATO_C2_Manager.Instance.AutonomousMode) return;

            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                Vector3 evade = ComputeEvasion(a);
                if (evade.sqrMagnitude > 0.01f)
                {
                    a.preferredVelocity = Vector3.Lerp(a.preferredVelocity, evade, evasionWeight);
                }
            }
        }

        // =================================================================
        //  Threat field — project hostiles forward in time and mark each
        //  forecasted position as a soft circular threat.
        // =================================================================
        private void BuildThreatField(IReadOnlyList<Agent> agents)
        {
            ThreatField.Clear();
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null || a.affiliation != Affiliation.Hostile) continue;

                // Project forward at current velocity. We sample at 25%, 50%,
                // 75%, 100% of the lookahead window for graceful HUD pulses.
                Vector3 p0 = a.transform.position;
                Vector3 v  = a.currentVelocity.sqrMagnitude > 0.01f ? a.currentVelocity : a.preferredVelocity;
                for (int k = 1; k <= 4; k++)
                {
                    float t = lookaheadSeconds * (k / 4f);
                    ThreatField.Add(new ThreatBubble
                    {
                        centre = p0 + v * t,
                        radius = threatRadius * (1f + 0.1f * k),
                        timeToImpact = t,
                        source = a
                    });
                }
            }
        }

        // =================================================================
        //  Opportunities — naive DBSCAN-style cluster pass on hostiles.
        // =================================================================
        private void BuildOpportunities(IReadOnlyList<Agent> agents)
        {
            Opportunities.Clear();
            var hostiles = new List<Agent>();
            for (int i = 0; i < agents.Count; i++)
                if (agents[i] != null && agents[i].affiliation == Affiliation.Hostile)
                    hostiles.Add(agents[i]);

            var visited = new bool[hostiles.Count];
            for (int i = 0; i < hostiles.Count; i++)
            {
                if (visited[i]) continue;
                var cluster = new List<int> { i };
                visited[i] = true;
                for (int j = i + 1; j < hostiles.Count; j++)
                {
                    if (visited[j]) continue;
                    if ((hostiles[j].transform.position - hostiles[i].transform.position).sqrMagnitude
                        < opportunityClusterRadius * opportunityClusterRadius)
                    {
                        cluster.Add(j);
                        visited[j] = true;
                    }
                }
                if (cluster.Count >= opportunityClusterMin)
                {
                    Vector3 sum = Vector3.zero;
                    float spread = 0f;
                    foreach (var idx in cluster) sum += hostiles[idx].transform.position;
                    Vector3 c = sum / cluster.Count;
                    foreach (var idx in cluster)
                        spread = Mathf.Max(spread, (hostiles[idx].transform.position - c).magnitude);
                    Opportunities.Add(new OpportunityCluster
                    {
                        centre = c,
                        count  = cluster.Count,
                        spread = spread
                    });
                }
            }
        }

        // =================================================================
        //  Evasion vector — sum of inverse-distance pushes from each threat.
        // =================================================================
        private Vector3 ComputeEvasion(Agent a)
        {
            Vector3 result = Vector3.zero;
            for (int i = 0; i < ThreatField.Count; i++)
            {
                var t = ThreatField[i];
                Vector3 d = a.transform.position - t.centre;
                d.y = 0f;
                float distSq = d.sqrMagnitude;
                if (distSq > t.radius * t.radius) continue;
                float w = 1f - Mathf.Sqrt(distSq) / t.radius;
                result += d.normalized * (w * a.maxSpeed);
            }
            if (result.sqrMagnitude > a.maxSpeed * a.maxSpeed)
                result = result.normalized * a.maxSpeed;
            return result;
        }
    }
}
