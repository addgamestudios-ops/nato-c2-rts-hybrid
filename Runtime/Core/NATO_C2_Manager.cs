// =====================================================================
//  NATO C2 RTS Hybrid — NATO_C2_Manager.cs
//  ---------------------------------------------------------------------
//  Central orchestrator. Every Agent registers here on enable. Every
//  tick the manager:
//      1) Walks active paths & writes preferredVelocity per Agent.
//      2) Hands the agent set to ORCA, which writes back a velocity
//         that resolves all reciprocal collisions.
//      3) Integrates position / heading.
//      4) Notifies the Mythos AIAutonomousMode + HUD.
//
//  This file is intentionally framework-style: most behaviour is in
//  ORCA.cs, HPAStar.cs, FormationController.cs, AIAutonomousMode.cs.
//  The Manager exists to give the HUD a single API surface.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("NATO C2/Manager")]
    public class NATO_C2_Manager : MonoBehaviour
    {
        public static NATO_C2_Manager Instance { get; private set; }

        // ---------- Inspector configuration -----------------------------
        [Header("Subsystems (assign in Inspector or auto-created on Awake)")]
        public ORCA orca;
        public HPAStar pathfinder;
        public FormationController formations;
        public AIAutonomousMode mythos;

        [Header("Simulation")]
        [Range(0.01f, 0.1f)] public float simulationStep = 1f / 60f;
        [Tooltip("Cap on Agents the manager will accept. Sizes the compact arrays.")]
        public int maxAgents = 1024;

        [Header("Cross-layer rules")]
        [Tooltip("If true, agents on different altitude layers ignore each other for ORCA. Recommended.")]
        public bool layerAwareAvoidance = true;

        // ---------- Public collections (read-only externally) -----------
        public IReadOnlyList<Agent> Agents => _agents;
        public IReadOnlyList<Agent> Selected => _selected;

        // ---------- Events ----------------------------------------------
        public event Action<IReadOnlyList<Agent>> OnSelectionChanged;
        public event Action<CommandOrder, Vector3> OnCommandIssued;
        public event Action<bool> OnAutonomousModeChanged;

        // ---------- Private state ---------------------------------------
        private readonly List<Agent> _agents = new List<Agent>(256);
        private readonly List<Agent> _selected = new List<Agent>(64);
        private static readonly Queue<Agent> _pendingRegister = new Queue<Agent>();
        private static readonly Queue<Agent> _pendingUnregister = new Queue<Agent>();
        private float _accumulator;
        private bool _autonomous;

        // =================================================================
        //  Unity lifecycle
        // =================================================================
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[NATO_C2_Manager] Multiple managers detected; disabling duplicate.");
                enabled = false;
                return;
            }
            Instance = this;
            if (orca        == null) orca        = GetComponentInChildren<ORCA>(true)        ?? gameObject.AddComponent<ORCA>();
            if (pathfinder  == null) pathfinder  = GetComponentInChildren<HPAStar>(true)     ?? gameObject.AddComponent<HPAStar>();
            if (formations  == null) formations  = GetComponentInChildren<FormationController>(true) ?? gameObject.AddComponent<FormationController>();
            if (mythos      == null) mythos      = GetComponentInChildren<AIAutonomousMode>(true)    ?? gameObject.AddComponent<AIAutonomousMode>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            DrainPendingRegistration();

            _accumulator += Time.deltaTime;
            // Fixed-step simulation for stable ORCA + Burst jobs.
            int steps = 0;
            while (_accumulator >= simulationStep && steps < 4)
            {
                Tick(simulationStep);
                _accumulator -= simulationStep;
                steps++;
            }
        }

        // =================================================================
        //  Simulation tick
        // =================================================================
        private void Tick(float dt)
        {
            // 1) Per-Agent: refresh preferredVelocity from active path/formation.
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a == null) continue;

                if (a.HasPath)
                {
                    Vector3 to = a.CurrentWaypoint - a.transform.position;
                    to.y = 0f;
                    float d = to.magnitude;
                    if (d < 0.5f) { a.pathCursor++; }
                    a.preferredVelocity = d > 0.001f
                        ? to.normalized * Mathf.Min(a.maxSpeed, d / dt)
                        : Vector3.zero;
                }
                else
                {
                    // No path: bleed to zero unless Mythos is in charge.
                    a.preferredVelocity = Vector3.Lerp(a.preferredVelocity, Vector3.zero, dt * 4f);
                }
            }

            // 2) Mythos AI hook — ALWAYS runs so the HUD threat heatmap stays
            //    populated. AdviseAgents internally checks AutonomousMode before
            //    mutating preferredVelocity (evasion is autonomous-only).
            if (mythos != null)
                mythos.AdviseAgents(_agents, dt);

            // 3) ORCA solves the velocity that minimises collisions while
            //    staying closest to preferredVelocity. Layer-aware filtering
            //    is applied inside ORCA when layerAwareAvoidance is true.
            if (orca != null) orca.Solve(_agents, dt, layerAwareAvoidance);

            // 4) Integrate.
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a == null) continue;
                a.transform.position += a.currentVelocity * dt;
                if (a.currentVelocity.sqrMagnitude > 0.01f)
                {
                    a.desiredFacing = a.currentVelocity.normalized;
                    a.transform.rotation = Quaternion.Slerp(
                        a.transform.rotation,
                        Quaternion.LookRotation(a.desiredFacing, Vector3.up),
                        dt * 8f);
                }
            }
        }

        // =================================================================
        //  Registration (called by Agent.OnEnable / OnDisable)
        // =================================================================
        internal static void Register(Agent a)
        {
            if (a == null) return;
            _pendingRegister.Enqueue(a);
        }

        internal static void Unregister(Agent a)
        {
            if (a == null) return;
            _pendingUnregister.Enqueue(a);
        }

        private void DrainPendingRegistration()
        {
            while (_pendingRegister.Count > 0)
            {
                var a = _pendingRegister.Dequeue();
                if (a == null || _agents.Contains(a)) continue;
                if (_agents.Count >= maxAgents)
                {
                    Debug.LogWarning($"[NATO_C2_Manager] maxAgents={maxAgents} exceeded; ignoring {a.name}");
                    continue;
                }
                a.simIndex = _agents.Count;
                _agents.Add(a);
            }
            while (_pendingUnregister.Count > 0)
            {
                var a = _pendingUnregister.Dequeue();
                if (a == null) continue;
                int idx = _agents.IndexOf(a);
                if (idx >= 0)
                {
                    _agents.RemoveAt(idx);
                    for (int i = idx; i < _agents.Count; i++) _agents[i].simIndex = i;
                }
                _selected.Remove(a);
            }
        }

        // =================================================================
        //  Selection API (drag-box & control groups bind to this)
        // =================================================================
        public void SetSelection(IEnumerable<Agent> next)
        {
            foreach (var a in _selected) if (a != null) a.SetSelected(false);
            _selected.Clear();
            foreach (var a in next)
            {
                if (a == null) continue;
                a.SetSelected(true);
                _selected.Add(a);
            }
            OnSelectionChanged?.Invoke(_selected);
        }

        public void ClearSelection() => SetSelection(Array.Empty<Agent>());

        // =================================================================
        //  Command API (Radial Menu binds here)
        // =================================================================
        /// <summary>Issue a command to the current selection at the given world target.</summary>
        public void IssueCommand(CommandOrder order, Vector3 worldTarget)
        {
            if (_selected.Count == 0) return;

            // Formation slots are computed once for the whole selection.
            formations.AssignSlots(_selected, worldTarget);

            for (int i = 0; i < _selected.Count; i++)
            {
                var a = _selected[i];
                if (a == null) continue;
                a.IssueOrder(order);

                if (order == CommandOrder.Move || order == CommandOrder.Attack || order == CommandOrder.Swarm)
                {
                    Vector3 slotTarget = worldTarget + a.formationSlot;
                    a.path.Clear();
                    a.pathCursor = 0;
                    pathfinder.RequestPath(a.transform.position, slotTarget, a.layer, a.path);
                }
                else if (order == CommandOrder.Hold || order == CommandOrder.Loiter)
                {
                    a.path.Clear();
                    a.pathCursor = 0;
                    a.preferredVelocity = Vector3.zero;
                }
                else if (order == CommandOrder.RTB)
                {
                    // RTB = return to base. We assume world origin for now; bind to a "Home"
                    // marker via the Editor in production.
                    a.path.Clear();
                    a.pathCursor = 0;
                    pathfinder.RequestPath(a.transform.position, Vector3.zero, a.layer, a.path);
                }
            }
            OnCommandIssued?.Invoke(order, worldTarget);
        }

        // =================================================================
        //  Autonomous mode
        // =================================================================
        public bool AutonomousMode
        {
            get => _autonomous;
            set
            {
                if (_autonomous == value) return;
                _autonomous = value;
                OnAutonomousModeChanged?.Invoke(value);
            }
        }

        // =================================================================
        //  Convenience queries (used by HUD overlays + Mythos)
        // =================================================================
        public IEnumerable<Agent> EnumerateByAffiliation(Affiliation aff)
        {
            for (int i = 0; i < _agents.Count; i++)
                if (_agents[i] != null && _agents[i].affiliation == aff)
                    yield return _agents[i];
        }

        public IEnumerable<Agent> EnumerateByLayer(AltitudeLayer layer)
        {
            for (int i = 0; i < _agents.Count; i++)
                if (_agents[i] != null && _agents[i].layer == layer)
                    yield return _agents[i];
        }
    }
}
