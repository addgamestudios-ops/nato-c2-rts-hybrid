// =====================================================================
//  NATO C2 RTS Hybrid — LocalSimFeed.cs
//  ---------------------------------------------------------------------
//  Wires the in-process simulation (NATO_C2_Manager + Agents + Mythos)
//  into the FeedHub so every consumer downstream sees data in the same
//  shape it would arrive in production via Link 16 / JBC-P / SAPIENT.
//
//  In production this component is replaced with — or runs alongside —
//  real adapters (MqttBftAdapter, CotXmlAdapter, etc.). The HUD never
//  needs to know whether a position came from a real BFT modem or from
//  the in-engine simulation.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(FeedHub))]
    [AddComponentMenu("NATO C2/Local Sim Feed")]
    public class LocalSimFeed : MonoBehaviour
    {
        [Header("Tick rate")]
        [Tooltip("BFT updates per second (real systems run 0.2-5 Hz).")]
        [Range(0.5f, 10f)] public float bftHz = 2f;
        [Tooltip("Radar track refresh per second.")]
        [Range(0.5f, 10f)] public float radarHz = 2f;

        [Header("Radio chatter")]
        [Tooltip("How often Mythos generates ambient radio events when nothing is happening.")]
        [Range(2f, 30f)] public float radioAmbientPeriod = 12f;

        [Header("Geo Origin (for synthetic lat/lon)")]
        public double originLatitude  = 38.7400d;
        public double originLongitude = 22.2540d;
        [Tooltip("Metres per world unit (used by the synthetic lat/lon converter).")]
        public float metresPerUnit = 1f;

        private FeedHub _hub;
        private float _nextBft, _nextRadar, _nextAmbient;
        private readonly Dictionary<Agent, CommandOrder> _lastOrder = new Dictionary<Agent, CommandOrder>(64);
        private bool _lastAutonomous;
        private int _lastThreatCount;

        private static readonly string[] _ambientFiller =
        {
            "SITREP nominal.",
            "Holding position.",
            "Clear to my front.",
            "Awaiting orders.",
            "Tracking nominal.",
            "Sensors green.",
            "Comms check, lima charlie."
        };

        private void Awake()
        {
            _hub = GetComponent<FeedHub>();
        }

        private void Update()
        {
            if (_hub == null || NATO_C2_Manager.Instance == null) return;
            var mgr = NATO_C2_Manager.Instance;

            float t = Time.unscaledTime;
            if (t >= _nextBft)   { _nextBft   = t + 1f / Mathf.Max(0.1f, bftHz);   PublishBftSnapshot(mgr); }
            if (t >= _nextRadar) { _nextRadar = t + 1f / Mathf.Max(0.1f, radarHz); PublishRadarSnapshot(mgr); }
            if (t >= _nextAmbient) { _nextAmbient = t + radioAmbientPeriod;        PublishAmbientChatter(mgr); }

            ObserveOrderChanges(mgr);
            ObserveMythosState(mgr);
        }

        // =================================================================
        //  BFT — every Friendly publishes a position update.
        // =================================================================
        private void PublishBftSnapshot(NATO_C2_Manager mgr)
        {
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                var (lat, lon) = WorldToLatLon(a.transform.position);
                _hub.PublishBft(new BftPosition
                {
                    unitId         = a.callsign,
                    timestampUtc   = DateTime.UtcNow,
                    latitude       = lat,
                    longitude      = lon,
                    altitudeMeters = a.transform.position.y * metresPerUnit,
                    headingDeg     = ToHeadingDeg(a.desiredFacing),
                    speedMs        = a.currentVelocity.magnitude,
                    healthPct      = a.maxHealth > 0 ? a.health / a.maxHealth : 0f,
                    ammoPct        = a.maxAmmo   > 0 ? a.ammo   / a.maxAmmo   : 0f,
                    sourceNet      = "LocalSim/BFT",
                    confidence     = 1.0f
                });
            }
        }

        // =================================================================
        //  RADAR — every Hostile gets a track. Confidence scales with the
        //  sensor noise we synthesize on top of position.
        // =================================================================
        private void PublishRadarSnapshot(NATO_C2_Manager mgr)
        {
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation == Affiliation.Friendly) continue;
                var (lat, lon) = WorldToLatLon(a.transform.position);
                _hub.PublishRadar(new RadarTrack
                {
                    trackId         = $"TRK-{a.GetEntityId().GetHashCode() & 0xFFFF:X4}",
                    timestampUtc    = DateTime.UtcNow,
                    latitude        = lat,
                    longitude       = lon,
                    altitudeMeters  = a.transform.position.y * metresPerUnit,
                    courseDeg       = ToHeadingDeg(a.currentVelocity.sqrMagnitude > 0.01f ? a.currentVelocity.normalized : a.desiredFacing),
                    speedMs         = a.currentVelocity.magnitude,
                    affiliation     = a.affiliation,
                    classifiedType  = a.unitType,
                    confidence      = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 0.5f, a.GetEntityId().GetHashCode() * 0.001f),
                    sourceSensor    = "LocalSim/Radar-Composite"
                });
            }
        }

        // =================================================================
        //  RADIO — react to in-game events with realistic chatter.
        // =================================================================
        private void ObserveOrderChanges(NATO_C2_Manager mgr)
        {
            // Detect order transitions on friendlies and publish radio messages.
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                if (!_lastOrder.TryGetValue(a, out var prev)) { _lastOrder[a] = a.currentOrder; continue; }
                if (prev != a.currentOrder)
                {
                    _lastOrder[a] = a.currentOrder;
                    _hub.PublishRadio(new RadioMessage
                    {
                        net           = "TANGO-6",
                        timestampUtc  = DateTime.UtcNow,
                        fromCallsign  = a.callsign,
                        text          = OrderTransitionLine(a, prev, a.currentOrder),
                        severity      = RadioSeverity.Info
                    });
                }
            }
        }

        private void ObserveMythosState(NATO_C2_Manager mgr)
        {
            if (mgr.AutonomousMode != _lastAutonomous)
            {
                _lastAutonomous = mgr.AutonomousMode;
                _hub.PublishRadio(new RadioMessage
                {
                    net           = "HQ",
                    timestampUtc  = DateTime.UtcNow,
                    fromCallsign  = "MYTHOS",
                    text          = mgr.AutonomousMode ? "Autonomous mode engaged. Evasion routing active." : "Returning to advisory mode.",
                    severity      = RadioSeverity.System
                });
            }
            if (mgr.mythos == null) return;
            int n = mgr.mythos.ThreatField.Count;
            if (Mathf.Abs(n - _lastThreatCount) >= 8)
            {
                _lastThreatCount = n;
                _hub.PublishRadio(new RadioMessage
                {
                    net           = "HQ",
                    timestampUtc  = DateTime.UtcNow,
                    fromCallsign  = "MYTHOS",
                    text          = $"Threat field updated. {n} forecast contacts.",
                    severity      = n > 30 ? RadioSeverity.Warning : RadioSeverity.Info
                });
            }
        }

        private void PublishAmbientChatter(NATO_C2_Manager mgr)
        {
            // Pick a random friendly and publish a filler line.
            if (mgr.Agents.Count == 0) return;
            int picks = 0;
            for (int t = 0; t < 8 && picks == 0; t++)
            {
                int idx = UnityEngine.Random.Range(0, mgr.Agents.Count);
                var a = mgr.Agents[idx];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                string line = _ambientFiller[UnityEngine.Random.Range(0, _ambientFiller.Length)];
                _hub.PublishRadio(new RadioMessage
                {
                    net           = "TANGO-6",
                    timestampUtc  = DateTime.UtcNow,
                    fromCallsign  = a.callsign,
                    text          = line,
                    severity      = RadioSeverity.Info
                });
                picks = 1;
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================
        private static string OrderTransitionLine(Agent a, CommandOrder prev, CommandOrder cur)
        {
            switch (cur)
            {
                case CommandOrder.Move:   return "Moving to objective.";
                case CommandOrder.Attack: return "Engaging. Target acquired.";
                case CommandOrder.Loiter: return "Loitering on station.";
                case CommandOrder.Swarm:  return "Swarming. Net-coordinated.";
                case CommandOrder.RTB:    return "RTB. Returning to base.";
                case CommandOrder.Hold:   return prev == CommandOrder.Move ? "Holding position." : "Stop, holding.";
                default:                  return "Order acknowledged.";
            }
        }

        private static float ToHeadingDeg(Vector3 facing)
        {
            float h = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }

        private (double lat, double lon) WorldToLatLon(Vector3 worldPos)
        {
            // Synthetic equirectangular projection — 1 world unit = 1 m at the
            // origin. Production: swap for a real geodesy library (e.g. GeographicLib).
            const double mPerDegLat = 111_320.0;
            double dLat = (worldPos.z * metresPerUnit) / mPerDegLat;
            double mPerDegLon = mPerDegLat * Math.Cos(originLatitude * Math.PI / 180.0);
            double dLon = (worldPos.x * metresPerUnit) / mPerDegLon;
            return (originLatitude + dLat, originLongitude + dLon);
        }
    }
}
