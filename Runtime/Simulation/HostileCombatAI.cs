// =====================================================================
//  NATO C2 RTS Hybrid — HostileCombatAI.cs
//  ---------------------------------------------------------------------
//  Per-unit combat AI for hostile Agents. Three behaviours, ordered by
//  priority:
//
//      1. BREAK CONTACT  — HP < retreatHpFraction → run away from the
//         nearest threat, find a position that breaks line-of-sight by
//         placing terrain or a friendly between us and the threat.
//
//      2. ENGAGE         — friendly within engageRange → close distance
//         to engageStandoffRange, face the target. Optionally maneuver
//         in a flanking arc rather than a straight-line approach so the
//         hostiles don't all come in on the same axis.
//
//      3. PATROL         — no contact → continue current Move/Loiter
//         order, or random-walk inside the AO if idle.
//
//  Design choices:
//      • Pure C# MonoBehaviour, ticks at 4 Hz (not 60Hz). Combat decisions
//        don't need per-frame fidelity and we save CPU.
//      • Issues orders through Agent.IssueOrder + writes path entries
//        through NATO_C2_Manager.pathfinder so HPA* + ORCA do the heavy
//        lifting. We don't move the unit ourselves.
//      • Sensing is by straight Euclidean distance — no fog of war from
//        the hostile's perspective (which is realistic-enough for a
//        training-grade sim; production swaps this for a real sensor model).
//
//  Plug-in: DemoSceneBootstrap.BuildAgents() adds this component to
//  every hostile spawn after Agent is configured.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    [RequireComponent(typeof(Agent))]
    [AddComponentMenu("NATO C2/Hostile Combat AI")]
    public class HostileCombatAI : MonoBehaviour
    {
        [Header("Sensing")]
        [Tooltip("Maximum distance at which this unit notices a friendly.")]
        public float sensingRange = 60f;
        [Tooltip("Distance at which we'll commit to engaging — closer than this and we treat them as a fight target.")]
        public float engageRange = 45f;

        [Header("Engagement")]
        [Tooltip("Standoff range we try to maintain when engaging — close enough to fight, far enough to maneuver.")]
        public float engageStandoffRange = 18f;
        [Tooltip("Angle (deg) offset from straight-line approach — gives a flanking arc instead of a head-on rush.")]
        [Range(0f, 80f)] public float flankAngle = 25f;

        [Header("Break contact")]
        [Tooltip("If HP drops below this fraction, switch from engage to break-contact.")]
        [Range(0.05f, 0.95f)] public float retreatHpFraction = 0.35f;
        [Tooltip("Retreat distance in metres — drives how far behind cover we attempt to go.")]
        public float retreatDistance = 35f;

        [Header("Cadence")]
        [Tooltip("AI decision frequency. 4 Hz keeps it responsive without spamming the pathfinder.")]
        [Range(0.5f, 30f)] public float tickHz = 4f;

        [Header("Idle wander")]
        [Tooltip("If true, when no contact and no order, drift toward random points in the AO.")]
        public bool idleWander = true;
        public float aoHalfExtent = 90f;

        // ---------- runtime state ---------------------------------
        private Agent _self;
        private NATO_C2_Manager _mgr;
        private float _nextTick;
        private Vector3 _wanderTarget;
        private float  _wanderUntil;
        private enum Mode { Patrol, Engage, BreakContact }
        private Mode _mode = Mode.Patrol;
        private Agent _currentTarget;
        private Vector3 _lastIssuedTarget;

        private void Awake()
        {
            _self = GetComponent<Agent>();
        }

        private void Start()
        {
            _mgr = NATO_C2_Manager.Instance;
            // Stagger ticks so hostiles don't all decide on the same frame.
            _nextTick = Time.time + Random.Range(0f, 1f / Mathf.Max(0.1f, tickHz));
        }

        private void Update()
        {
            if (_self == null || _self.affiliation != Affiliation.Hostile) return;
            if (_self.health <= 0f) return;
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 1f / Mathf.Max(0.1f, tickHz);

            if (_mgr == null) _mgr = NATO_C2_Manager.Instance;
            if (_mgr == null) return;

            // ----- 1. Sense closest threat ------------------------
            Agent threat = ClosestFriendly(out float threatDist);

            // ----- 2. Decide mode ---------------------------------
            float hpFrac = _self.maxHealth > 0f ? _self.health / _self.maxHealth : 1f;
            Mode want;
            if (hpFrac < retreatHpFraction && threat != null) want = Mode.BreakContact;
            else if (threat != null && threatDist <= engageRange) want = Mode.Engage;
            else want = Mode.Patrol;

            // Hysteresis: if we were already engaging and target is just past
            // engageRange (but still within sensing), keep pursuing.
            if (_mode == Mode.Engage && threat != null && threatDist <= sensingRange)
                want = Mode.Engage;

            _mode = want;
            _currentTarget = threat;

            // ----- 3. Execute -------------------------------------
            switch (_mode)
            {
                case Mode.Engage:       ExecuteEngage(threat); break;
                case Mode.BreakContact: ExecuteBreak(threat);  break;
                default:                ExecutePatrol();        break;
            }
        }

        // ---------- behaviours --------------------------------------
        private void ExecuteEngage(Agent threat)
        {
            if (threat == null) return;

            // Compute a flanking-arc approach: stand off from the threat,
            // offset along the right-hand normal by `flankAngle`.
            Vector3 toThreat = threat.transform.position - transform.position;
            float dist = toThreat.magnitude;
            if (dist < 0.001f) return;
            Vector3 dir = toThreat / dist;

            // We want to approach within engageStandoffRange.
            float approach = Mathf.Max(0f, dist - engageStandoffRange);
            // Flank offset: rotate the approach vector around Y by flankAngle.
            // Sign per-unit so half flank left, half flank right (controlled by entity hash).
            float sign = ((_self.GetEntityId().GetHashCode() & 1) == 0) ? 1f : -1f;
            Quaternion rot = Quaternion.Euler(0f, flankAngle * sign, 0f);
            Vector3 approachDir = rot * dir;
            Vector3 target = transform.position + approachDir * approach;

            // Only re-issue path if the target moved meaningfully — otherwise
            // we'd reset the path every tick and stutter the unit.
            if ((target - _lastIssuedTarget).sqrMagnitude > 4f)
            {
                IssueMoveTo(target);
                _lastIssuedTarget = target;
            }

            _self.IssueOrder(CommandOrder.Attack);
            _self.desiredFacing = dir;
        }

        private void ExecuteBreak(Agent threat)
        {
            // Run directly away from the threat by `retreatDistance`. If a
            // friendly hostile is between us and the threat, even better —
            // we treat the friendly as cover and run toward a point slightly
            // past it.
            Vector3 awayDir = (transform.position - threat.transform.position).normalized;
            Vector3 target = transform.position + awayDir * retreatDistance;

            // Try to find another hostile to put between us and the threat.
            Agent cover = ClosestHostileBlocker(threat);
            if (cover != null)
            {
                Vector3 coverDir = (cover.transform.position - threat.transform.position).normalized;
                target = cover.transform.position + coverDir * 4f; // hide behind it
            }

            if ((target - _lastIssuedTarget).sqrMagnitude > 4f)
            {
                IssueMoveTo(target);
                _lastIssuedTarget = target;
            }
            _self.IssueOrder(CommandOrder.Move);
        }

        private void ExecutePatrol()
        {
            // If we already have a path, leave it alone.
            if (_self.path != null && _self.path.Count > 0 && _self.pathCursor < _self.path.Count) return;
            if (!idleWander) return;

            if (Time.time > _wanderUntil)
            {
                _wanderTarget = new Vector3(
                    Random.Range(-aoHalfExtent, aoHalfExtent), 0f,
                    Random.Range(-aoHalfExtent, aoHalfExtent));
                _wanderUntil = Time.time + Random.Range(8f, 20f);
                IssueMoveTo(_wanderTarget);
                _lastIssuedTarget = _wanderTarget;
                _self.IssueOrder(CommandOrder.Loiter);
            }
        }

        // ---------- helpers -----------------------------------------
        private void IssueMoveTo(Vector3 target)
        {
            _self.path?.Clear();
            _self.pathCursor = 0;
            if (_mgr != null && _mgr.pathfinder != null)
                _mgr.pathfinder.RequestPath(transform.position, target, _self.layer, _self.path);
        }

        private Agent ClosestFriendly(out float bestDist)
        {
            bestDist = float.MaxValue;
            Agent best = null;
            if (_mgr == null) return null;
            var list = _mgr.Agents;
            float r2 = sensingRange * sensingRange;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null || a == _self) continue;
                if (a.affiliation != Affiliation.Friendly) continue;
                if (a.health <= 0f) continue;
                float d2 = (a.transform.position - transform.position).sqrMagnitude;
                if (d2 > r2) continue;
                if (d2 < bestDist) { bestDist = d2; best = a; }
            }
            bestDist = best != null ? Mathf.Sqrt(bestDist) : float.MaxValue;
            return best;
        }

        private Agent ClosestHostileBlocker(Agent threat)
        {
            // Find a hostile (other than us) that's roughly between us and the threat —
            // i.e. dot product of (toBlocker, toThreat) > 0 and within a reasonable distance.
            if (_mgr == null) return null;
            Vector3 toThreat = threat.transform.position - transform.position;
            float toThreatDist = toThreat.magnitude;
            if (toThreatDist < 0.001f) return null;
            Vector3 toThreatDir = toThreat / toThreatDist;

            Agent best = null;
            float bestScore = 0f;
            var list = _mgr.Agents;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null || a == _self) continue;
                if (a.affiliation != Affiliation.Hostile) continue;
                if (a.health <= 0f) continue;
                Vector3 toBlocker = a.transform.position - transform.position;
                float d = toBlocker.magnitude;
                if (d < 1f || d > toThreatDist + 5f) continue;
                float dot = Vector3.Dot(toBlocker.normalized, toThreatDir);
                if (dot < 0.6f) continue; // not "between" enough
                float score = dot - (d / toThreatDist) * 0.3f;
                if (score > bestScore) { bestScore = score; best = a; }
            }
            return best;
        }
    }
}
