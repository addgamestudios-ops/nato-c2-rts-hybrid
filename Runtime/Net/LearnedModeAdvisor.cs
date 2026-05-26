// =====================================================================
//  NATO C2 RTS Hybrid — LearnedModeAdvisor.cs
//  ---------------------------------------------------------------------
//  Adaptive PPLI packing-mode picker. Subscribes to
//  Link16TdmaSimulator.OnPpliScheduled to track per-Agent hit/miss
//  rates in a rolling window. Periodically re-evaluates each agent
//  WITHOUT an explicit override and pushes a new mode via
//  Link16TdmaSimulator.SetPackingMode().
//
//  Policy:
//      Let m = misses / (hits+misses) for the agent in the rolling window.
//
//      • m > demoteThreshold (default 0.20)   → demote one rung
//      • m < promoteThreshold (default 0.02) + ground unit → promote
//      • else → leave as-is
//
//      Mode rungs by density (high → low):
//          P2DP   (12 msg/slot, 150 nm) — highest density, shortest range
//          STD-DP ( 6 msg/slot, 300 nm) — middle ground
//          P4SP   ( 3 msg/slot, 500 nm) — sparsest, longest range
//
//      Demote means "move toward the sparser end" (P2DP → STD-DP → P4SP).
//      Promote means "move toward the denser end" (P4SP → STD-DP → P2DP),
//      but only for ground units — aircraft stay on P4SP because their
//      range requirement is intrinsic to their mission profile.
//
//  Why it works:
//      A "miss" means the agent was scheduled to PPLI on this slot but
//      its mode's per-slot envelope cap was full. Sustained high miss
//      rate ⇒ the agent is contending with other terminals on the same
//      mode. Moving to a sparser mode frees up capacity at the cost of
//      report cadence; moving to a denser mode does the opposite.
//
//  Feature-flagged via `enabled` on the MonoBehaviour itself — drop the
//  component on the simulator GO to turn it on, disable to turn it off.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-45)]   // run after the simulator's event subscription wires up
    [RequireComponent(typeof(Link16TdmaSimulator))]
    [AddComponentMenu("NATO C2/Learned Mode Advisor")]
    public class LearnedModeAdvisor : MonoBehaviour
    {
        [Tooltip("Sample window in seconds — older hits/misses decay out.")]
        [Range(2f, 60f)] public float windowSec = 8f;

        [Tooltip("How often to re-evaluate every agent's mode.")]
        [Range(0.5f, 10f)] public float evaluateEverySec = 2f;

        [Tooltip("Miss rate above which we demote to a sparser mode.")]
        [Range(0.05f, 0.5f)] public float demoteThreshold = 0.20f;

        [Tooltip("Miss rate below which we may promote a ground unit to a denser mode.")]
        [Range(0f, 0.1f)] public float promoteThreshold = 0.02f;

        [Tooltip("Log advisor decisions to the console.")]
        public bool logDecisions = false;

        // ----- runtime -----
        private Link16TdmaSimulator _sim;
        private float _nextEvalAt;

        private struct Stat
        {
            public int hits;
            public int misses;
            public float lastUpdate;   // unscaledTime
        }
        private readonly Dictionary<Agent, Stat> _stats = new Dictionary<Agent, Stat>(128);

        // Public counters for tests + HUD.
        public int DecisionsMadeTotal { get; private set; }
        public int DemotionsTotal     { get; private set; }
        public int PromotionsTotal    { get; private set; }

        // Fired once per advisor decision. Telemetry sink + dashboard
        // autoresolver consume this.
        //   (agent, fromMode, toMode, missRate, samples)
        public event System.Action<Agent, Link16TdmaSimulator.PackingMode,
                                          Link16TdmaSimulator.PackingMode, float, int> OnDecision;

        /// <summary>
        /// Apply the playbook's "mode-flap storm" hysteresis preset
        /// (federation-playbook.md §4). Widens the demote/promote
        /// thresholds so an agent that gets bumped doesn't immediately
        /// bounce back. Lengthens the rolling window for stability.
        /// </summary>
        public void ApplyFlapStormPreset()
        {
            demoteThreshold  = 0.30f;
            promoteThreshold = 0.005f;
            windowSec        = 16f;
            // Reset all per-agent stats so the new thresholds get a clean window.
            _stats.Clear();
            Debug.Log("[LearnedMode] applied flap-storm preset (demote=0.30, promote=0.005, window=16s)");
        }

        // ====================================================================
        private void Awake()
        {
            _sim = GetComponent<Link16TdmaSimulator>();
        }
        private void OnEnable()
        {
            if (_sim == null) _sim = GetComponent<Link16TdmaSimulator>();
            if (_sim != null) _sim.OnPpliScheduled += OnScheduled;
        }
        private void OnDisable()
        {
            if (_sim != null) _sim.OnPpliScheduled -= OnScheduled;
        }

        private void OnScheduled(Agent a, Link16TdmaSimulator.PackingMode mode, bool packed)
        {
            if (a == null) return;
            if (!_stats.TryGetValue(a, out var s))
                s = new Stat { hits = 0, misses = 0, lastUpdate = Time.unscaledTime };
            if (packed) s.hits++;
            else        s.misses++;
            s.lastUpdate = Time.unscaledTime;
            _stats[a] = s;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextEvalAt) return;
            _nextEvalAt = Time.unscaledTime + evaluateEverySec;
            DecayAndEvaluate();
        }

        private void DecayAndEvaluate()
        {
            if (_sim == null) return;
            float cutoff = Time.unscaledTime - windowSec;

            // Walk a snapshot to allow mutation of _stats during iteration.
            _evalScratch.Clear();
            foreach (var kv in _stats) _evalScratch.Add(kv.Key);

            for (int i = 0; i < _evalScratch.Count; i++)
            {
                var a = _evalScratch[i];
                if (a == null) { _stats.Remove(a); continue; }
                var s = _stats[a];

                // Decay: if no updates in the window, drop the entry.
                if (s.lastUpdate < cutoff)
                {
                    _stats.Remove(a);
                    continue;
                }

                int total = s.hits + s.misses;
                if (total < 6) continue;  // not enough samples to decide

                float missRate = (float)s.misses / total;
                var current = _sim.ModeFor(a);
                var target  = Decide(current, missRate, a);

                if (target != current)
                {
                    _sim.SetPackingMode(a, target);
                    DecisionsMadeTotal++;
                    if (IsDemotion(current, target)) DemotionsTotal++;
                    else                              PromotionsTotal++;
                    if (logDecisions)
                        Debug.Log($"[LearnedMode] {a.callsign ?? a.name}: " +
                                  $"miss={missRate:P0} ({s.misses}/{total})  {current} → {target}");
                    OnDecision?.Invoke(a, current, target, missRate, total);

                    // Reset stats for this agent so the new mode gets a clean window.
                    _stats[a] = new Stat { hits = 0, misses = 0, lastUpdate = Time.unscaledTime };
                }
            }
        }
        private readonly List<Agent> _evalScratch = new List<Agent>(64);

        private Link16TdmaSimulator.PackingMode Decide(
            Link16TdmaSimulator.PackingMode current, float missRate, Agent a)
        {
            // Demote — move toward sparser mode for robustness.
            if (missRate > demoteThreshold)
            {
                switch (current)
                {
                    case Link16TdmaSimulator.PackingMode.P2Dp: return Link16TdmaSimulator.PackingMode.StdDp;
                    case Link16TdmaSimulator.PackingMode.StdDp: return Link16TdmaSimulator.PackingMode.P4Sp;
                    default: return current;       // already at sparsest
                }
            }
            // Promote — only for ground units. Aircraft stay on whatever
            // they're on (P4SP almost always, by the heuristic).
            if (missRate < promoteThreshold && a.layer == AltitudeLayer.Ground)
            {
                switch (current)
                {
                    case Link16TdmaSimulator.PackingMode.P4Sp: return Link16TdmaSimulator.PackingMode.StdDp;
                    case Link16TdmaSimulator.PackingMode.StdDp: return Link16TdmaSimulator.PackingMode.P2Dp;
                    default: return current;       // already at densest
                }
            }
            return current;
        }

        private static bool IsDemotion(Link16TdmaSimulator.PackingMode from, Link16TdmaSimulator.PackingMode to)
        {
            // Density rank: P2Dp(0) > StdDp(1) > P4Sp(2). Higher rank = sparser = demotion.
            return Rank(to) > Rank(from);
        }
        private static int Rank(Link16TdmaSimulator.PackingMode m)
        {
            switch (m)
            {
                case Link16TdmaSimulator.PackingMode.P2Dp: return 0;
                case Link16TdmaSimulator.PackingMode.P4Sp: return 2;
                default: return 1; // StdDp
            }
        }
    }
}
