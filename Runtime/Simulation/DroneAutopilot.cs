// =====================================================================
//  NATO C2 RTS Hybrid — DroneAutopilot.cs
//  ---------------------------------------------------------------------
//  Launch-and-forget mode for friendly drones. Operator arms a target
//  point or radius, then any selected drone with autopilotActive=true
//  takes itself there, scans for hostiles in sensor cone, auto-engages
//  the closest, and RTBs when ammo or HP drops below thresholds. The
//  operator can now move to safety; the CCC AI is in control.
//
//  In production this is where the auto-targeting AI lives — currently
//  it's a simple closest-threat picker. Swap the AcquireTarget() body
//  for any model you trust (YOLO+ROS, a hand-tuned BT, a learned PPO,
//  whatever clears human oversight).
// =====================================================================

using System;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2
{
    [RequireComponent(typeof(Agent))]
    [AddComponentMenu("NATO C2/Drone Autopilot")]
    public class DroneAutopilot : MonoBehaviour
    {
        [Header("Mission")]
        public bool autopilotActive = false;
        public Vector3 targetPoint;
        public float searchRadius = 35f;

        [Header("Engagement")]
        [Tooltip("Engagement range (m). Drone closes to this distance from a hostile.")]
        public float engageRange = 12f;
        [Tooltip("Damage per second delivered to a hostile within range.")]
        public float damagePerSecond = 12f;
        [Tooltip("Ammo consumption per second of engagement.")]
        public float ammoPerSecond = 8f;

        [Header("RTB triggers")]
        [Range(0f, 1f)] public float rtbHpThreshold   = 0.30f;
        [Range(0f, 1f)] public float rtbAmmoThreshold = 0.15f;
        public Vector3 homeBase = new Vector3(-60f, 14f, 0f);

        private Agent _agent;
        private enum State { Cruise, Engage, RTB }
        private State _state = State.Cruise;
        private Agent _engagedTarget;
        private float _arrivedAtAt = -1f;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
        }

        private void Update()
        {
            if (_agent == null || !autopilotActive) return;
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return;

            // RTB triggers — overrides everything.
            float hp   = _agent.maxHealth > 0 ? _agent.health / _agent.maxHealth : 0f;
            float ammo = _agent.maxAmmo   > 0 ? _agent.ammo   / _agent.maxAmmo   : 0f;
            if ((hp <= rtbHpThreshold || ammo <= rtbAmmoThreshold) && _state != State.RTB)
            {
                SetState(State.RTB);
                FeedHub.Instance?.PublishRadio(new RadioMessage
                {
                    net = "TANGO-6", timestampUtc = DateTime.UtcNow, fromCallsign = _agent.callsign,
                    text = hp <= rtbHpThreshold ? "BINGO. RTB" : "WINCHESTER. RTB",
                    severity = RadioSeverity.Warning
                });
            }

            switch (_state)
            {
                case State.Cruise: TickCruise(mgr); break;
                case State.Engage: TickEngage(mgr); break;
                case State.RTB:    TickRtb(mgr);    break;
            }
        }

        // =================================================================
        //  States
        // =================================================================
        private void TickCruise(NATO_C2_Manager mgr)
        {
            // Fly to target point.
            Vector3 to = targetPoint - transform.position;
            to.y = 0f;
            if (to.magnitude < 3f)
            {
                if (_arrivedAtAt < 0f) _arrivedAtAt = Time.time;
                // Look for hostiles in radius.
                var t = AcquireTarget(mgr);
                if (t != null)
                {
                    _engagedTarget = t;
                    SetState(State.Engage);
                    FeedHub.Instance?.PublishRadio(new RadioMessage
                    {
                        net = "TANGO-6", timestampUtc = DateTime.UtcNow, fromCallsign = _agent.callsign,
                        text = $"TARGET ACQUIRED — {t.callsign}, engaging",
                        severity = RadioSeverity.Critical
                    });
                }
                _agent.preferredVelocity = Vector3.zero;
            }
            else
            {
                _agent.preferredVelocity = to.normalized * _agent.maxSpeed;
            }
        }

        private void TickEngage(NATO_C2_Manager mgr)
        {
            if (_engagedTarget == null || _engagedTarget.health <= 0f)
            {
                _engagedTarget = AcquireTarget(mgr);
                if (_engagedTarget == null)
                {
                    // No more contacts in radius — loiter, then RTB after a moment.
                    if (Time.time - _arrivedAtAt > 12f) SetState(State.RTB);
                    _agent.preferredVelocity = Vector3.zero;
                    return;
                }
            }

            Vector3 to = _engagedTarget.transform.position - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d > engageRange)
            {
                _agent.preferredVelocity = to.normalized * _agent.maxSpeed;
            }
            else
            {
                // In range — deliver effect.
                _agent.preferredVelocity = to.normalized * 0.2f * _agent.maxSpeed;
                _engagedTarget.TakeDamage(damagePerSecond * Time.deltaTime);
                _agent.ammo = Mathf.Max(0f, _agent.ammo - ammoPerSecond * Time.deltaTime);
            }
        }

        private void TickRtb(NATO_C2_Manager mgr)
        {
            Vector3 to = homeBase - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d < 2f)
            {
                // "Recovered" — disable autopilot.
                autopilotActive = false;
                _arrivedAtAt = -1f;
                SetState(State.Cruise);
                _agent.preferredVelocity = Vector3.zero;
                FeedHub.Instance?.PublishRadio(new RadioMessage
                {
                    net = "TANGO-6", timestampUtc = DateTime.UtcNow, fromCallsign = _agent.callsign,
                    text = "RECOVERED. Autopilot disengaged.",
                    severity = RadioSeverity.System
                });
                return;
            }
            _agent.preferredVelocity = to.normalized * _agent.maxSpeed;
        }

        // =================================================================
        //  Target acquisition — swap with a real classifier in production.
        // =================================================================
        private Agent AcquireTarget(NATO_C2_Manager mgr)
        {
            Agent best = null;
            float bestSq = searchRadius * searchRadius;
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation != Affiliation.Hostile) continue;
                Vector3 d = a.transform.position - transform.position;
                d.y = 0f;
                float sq = d.sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = a; }
            }
            return best;
        }

        private void SetState(State s)
        {
            _state = s;
            if (s != State.Engage) _engagedTarget = null;
        }

        // =================================================================
        //  Static launcher — call this from TacticalHUD when the operator
        //  arms a "Launch & Forget" target. Engages every selected drone.
        // =================================================================
        public static int LaunchSelectedDrones(Vector3 target)
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return 0;
            int launched = 0;
            foreach (var a in mgr.Selected)
            {
                if (a == null || a.unitType != UnitType.Drone) continue;
                var pilot = a.GetComponent<DroneAutopilot>();
                if (pilot == null) pilot = a.gameObject.AddComponent<DroneAutopilot>();
                pilot.autopilotActive = true;
                pilot.targetPoint = target;
                pilot._arrivedAtAt = -1f;
                pilot.SetState(State.Cruise);
                launched++;
                FeedHub.Instance?.PublishRadio(new RadioMessage
                {
                    net = "TANGO-6", timestampUtc = DateTime.UtcNow, fromCallsign = a.callsign,
                    text = "AUTOPILOT engaged — launching to target",
                    severity = RadioSeverity.System
                });
            }
            return launched;
        }
    }
}
