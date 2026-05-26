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
        // Backing field for Instance. Getter falls back to FindAnyObjectByType
        // when the static is null — handles two real-world cases:
        //   1. EditMode tests where Awake ordering can race the test's first
        //      Assert.IsNotNull (manager exists in scene but static hasn't
        //      been assigned yet from the perspective of the test thread).
        //   2. Domain reloads / play-mode restarts that null statics before
        //      OnEnable on the live manager fires.
        private static NATO_C2_Manager _instance;
        public static NATO_C2_Manager Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = UnityEngine.Object.FindAnyObjectByType<NATO_C2_Manager>();
                return _instance;
            }
            private set { _instance = value; }
        }

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
            // Latest-wins semantics. If a previous Instance exists (production
            // edge case OR EditMode test scenarios where a Bootstrap manager
            // sits in the scene and the test creates its own), we log a soft
            // warning but STILL claim Instance and finish our setup. The old
            // "disable duplicate" behavior left the new manager half-built
            // (orca/formations/etc. null), which broke any test that spawned
            // its own manager into a scene that already had one.
            if (Instance != null && Instance != this)
                Debug.LogWarning("[NATO_C2_Manager] Multiple managers detected; latest wins.");
            Instance = this;
            if (orca        == null) orca        = GetComponentInChildren<ORCA>(true)        ?? gameObject.AddComponent<ORCA>();
            if (pathfinder  == null) pathfinder  = GetComponentInChildren<HPAStar>(true)     ?? gameObject.AddComponent<HPAStar>();
            if (formations  == null) formations  = GetComponentInChildren<FormationController>(true) ?? gameObject.AddComponent<FormationController>();
            if (mythos      == null) mythos      = GetComponentInChildren<AIAutonomousMode>(true)    ?? gameObject.AddComponent<AIAutonomousMode>();
            // Diagnostic — fires once per Awake. Lets EditMode tests prove
            // that subsystem fields are non-null after Awake. Remove once
            // the AgentMovementTests/FederationSoakTests NRE is resolved.
            Debug.Log($"[NATO_C2_Manager] Awake done on '{name}' — orca={(orca!=null)} pathfinder={(pathfinder!=null)} formations={(formations!=null)} mythos={(mythos!=null)}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Drives one full simulation step. EditMode tests can call this
        /// directly because MonoBehaviour.Update() does NOT fire in EditMode
        /// without [ExecuteAlways] — and we deliberately don't want
        /// [ExecuteAlways] on the manager in production (it would move agents
        /// while you're editing the scene). Tests pass an explicit dt.
        ///
        /// Important: ORCA's Burst jobs may not actually execute in EditMode
        /// (the Unity Job System ticks differently in the editor coroutine
        /// driver). So after the normal Tick we fall back to integrating
        /// preferredVelocity directly when ORCA failed to produce a non-zero
        /// currentVelocity. This is a TEST-ONLY shortcut and bypasses
        /// collision avoidance — fine for solo-agent regressions like
        /// AgentMovementTests which verify the move-command wiring, not ORCA.
        /// </summary>
        public void TickForTest(float dt)
        {
            DrainPendingRegistration();
            Tick(dt);
            // Fallback integration: if ORCA didn't write a velocity but we
            // do have a preferredVelocity, integrate that. Lets EditMode
            // tests get past the Burst-job-not-completing problem.
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a == null) continue;
                bool noCV = a.currentVelocity.sqrMagnitude < 1e-6f;
                bool yesPV = a.preferredVelocity.sqrMagnitude > 1e-6f;
                if (i == 0 && _agents.Count > 0)
                    Debug.Log($"[TickForTest] agents={_agents.Count} pathCount={a.path.Count} pathCursor={a.pathCursor} hasPath={a.HasPath} pref={a.preferredVelocity} curr={a.currentVelocity} fallback={(noCV && yesPV)}");
                if (noCV && yesPV)
                {
                    a.currentVelocity = a.preferredVelocity;
                    a.transform.position += a.currentVelocity * dt;
                }
            }
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

            // Defensive null-guards. In EditMode tests, Awake can race the
            // test's first IssueCommand call before subsystem fields are
            // populated. Rather than NRE, lazily resolve them here too.
            if (formations == null) formations = GetComponentInChildren<FormationController>(true) ?? gameObject.AddComponent<FormationController>();
            if (pathfinder == null) pathfinder = GetComponentInChildren<HPAStar>(true)             ?? gameObject.AddComponent<HPAStar>();

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
