// =====================================================================
//  NATO C2 RTS Hybrid — TacticalHUD.cs
//  ---------------------------------------------------------------------
//  Top-level UI orchestrator. Responsibilities:
//
//      • Single-click on a unit  → select just that unit
//      • Double-click on a unit  → select all same UnitType on screen
//      • LMB-drag                → drag-box selection
//      • Shift / Ctrl + click    → additive selection
//      • RMB                     → opens CommandRadialMenu
//      • Hotkeys                 → A=Attack-move, M=Move, H=Hold, S=Stop,
//                                  F=Patrol, Ctrl+A=Select all friendlies,
//                                  Space=center camera on selection,
//                                  Esc=deselect, Ctrl+1..9 store control
//                                  group, 1..9 recall
//      • Top/bottom bar text     → mission, selection count, threats, AI
//      • Threat heatmap          → pulsing rings around hostile forecasts
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("NATO C2/Tactical HUD")]
    public class TacticalHUD : MonoBehaviour
    {
        [Header("Bindings")]
        public Camera viewCamera;
        public CommandRadialMenu radialMenu;
        public MilsymbolBridge symbolBridge;

        [Header("Top Bar")]
        public Text missionLabel;
        public Text swarmCountLabel;
        public Text threatLevelLabel;
        public Text aiStatusLabel;
        public string missionName = "OP THUNDERSTRIKE";

        [Header("Bottom Bar")]
        public Dropdown formationDropdown;
        public Toggle autonomousToggle;
        public Toggle layerGroundToggle;
        public Toggle layerLowToggle;
        public Toggle layerHighToggle;

        [Header("Selection")]
        public RectTransform selectionBox;
        public Color boxFill = new Color(0f, 0.83f, 1f, 0.15f);
        public Color boxLine = new Color(0f, 0.83f, 1f, 0.95f);
        [Tooltip("Pixel threshold below which an LMB release is treated as a click (not a drag).")]
        public float dragThreshold = 6f;
        [Tooltip("Seconds within which two clicks count as a double-click.")]
        public float doubleClickWindow = 0.45f;
        [Tooltip("World-space radius for double-click-select-same-type.")]
        public float sameTypeRadius = 60f;

        [Header("Threat Overlay")]
        public bool drawThreatField = true;
        public Material threatMaterial;

        [Header("Hotkey Hint (optional)")]
        public Text hotkeyHintLabel;

        // ---------- runtime state ---------------------------------------
        // Drag origin in SCREEN PIXELS (matches Input.mousePosition and
        // viewCamera.WorldToScreenPoint output, used for agent picking).
        private Vector2 _dragStart;
        // Cached canvas hosting the selection box, for scaleFactor lookup.
        private Canvas  _selectionCanvas;
        private bool _dragging;
        private float _lastClickTime = -1f;
        private Agent _lastClickedAgent;
        private readonly Dictionary<int, List<Agent>> _controlGroups = new Dictionary<int, List<Agent>>();
        /// <summary>Exposed so other panels (RadioChatPanel intent parser) can resolve "group N" callouts.</summary>
        public IReadOnlyDictionary<int, List<Agent>> ControlGroups => _controlGroups;
        private bool[] _layerVisible = { true, true, true };
        // Active-mode hotkey state: after the user presses A or M, the next LMB
        // click on the ground issues that order at the click point.
        private CommandOrder _pendingHotkeyOrder = CommandOrder.Hold;
        private bool _hasPendingHotkey;

        private void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (autonomousToggle != null)
            {
                autonomousToggle.onValueChanged.AddListener(v => {
                    if (NATO_C2_Manager.Instance != null) NATO_C2_Manager.Instance.AutonomousMode = v;
                });
            }
            if (formationDropdown != null)
            {
                formationDropdown.ClearOptions();
                formationDropdown.AddOptions(new List<string> { "Wedge", "Line", "Circle", "Column" });
                formationDropdown.onValueChanged.AddListener(i => {
                    if (NATO_C2_Manager.Instance != null && NATO_C2_Manager.Instance.formations != null)
                        NATO_C2_Manager.Instance.formations.active = (FormationType)i;
                });
            }
            if (layerGroundToggle != null) layerGroundToggle.onValueChanged.AddListener(v => _layerVisible[0] = v);
            if (layerLowToggle    != null) layerLowToggle   .onValueChanged.AddListener(v => _layerVisible[1] = v);
            if (layerHighToggle   != null) layerHighToggle  .onValueChanged.AddListener(v => _layerVisible[2] = v);
        }

        private void Update()
        {
            HandleSelection();
            HandleRightClick();
            HandleControlGroups();
            HandleHotkeys();
            UpdateTopBar();
        }

        // =================================================================
        //  Selection — distinguishes click vs drag, single vs double-click.
        // =================================================================
        private void HandleSelection()
        {
            if (radialMenu != null && radialMenu.IsOpen)
            {
                _dragging = false;
                if (selectionBox != null) selectionBox.gameObject.SetActive(false);
                return;
            }

            // Cache the canvas once so we can read scaleFactor each frame
            // — CanvasScaler turns screen pixels into smaller canvas units
            // on HiDPI displays (Mac Retina = 2×). Without this conversion
            // the drag rect renders offset and at the wrong size.
            if (_selectionCanvas == null && selectionBox != null)
                _selectionCanvas = selectionBox.GetComponentInParent<Canvas>();
            float scale = _selectionCanvas != null ? _selectionCanvas.scaleFactor : 1f;
            if (scale <= 0f) scale = 1f;

            if (Input.GetMouseButtonDown(0))
            {
                _dragStart = Input.mousePosition;
                _dragging = true;
                if (selectionBox != null)
                {
                    selectionBox.gameObject.SetActive(true);
                    selectionBox.anchoredPosition = _dragStart / scale;
                    selectionBox.sizeDelta = new Vector2(2, 2);
                }
            }

            if (_dragging && selectionBox != null)
            {
                Vector2 cur = Input.mousePosition;
                Vector2 origin = new Vector2(
                    Mathf.Min(_dragStart.x, cur.x),
                    Mathf.Min(_dragStart.y, cur.y));
                Vector2 size = new Vector2(
                    Mathf.Abs(cur.x - _dragStart.x),
                    Mathf.Abs(cur.y - _dragStart.y));
                // anchoredPosition + sizeDelta are in CANVAS units, not screen pixels.
                selectionBox.anchoredPosition = origin / scale;
                selectionBox.sizeDelta = new Vector2(
                    Mathf.Max(2f, size.x / scale),
                    Mathf.Max(2f, size.y / scale));
            }

            if (Input.GetMouseButtonUp(0) && _dragging)
            {
                _dragging = false;
                if (selectionBox != null) selectionBox.gameObject.SetActive(false);
                if (NATO_C2_Manager.Instance == null || viewCamera == null) return;

                Vector2 cur = Input.mousePosition;
                Vector2 delta = cur - _dragStart;
                bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                             || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

                // Pending hotkey order? Consume it instead of selecting.
                if (_hasPendingHotkey)
                {
                    Ray ray = viewCamera.ScreenPointToRay(cur);
                    Vector3 target;
                    if (Physics.Raycast(ray, out var hit, 1000f)) target = hit.point;
                    else if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) target = ray.GetPoint(enter);
                    else { _hasPendingHotkey = false; SetHotkeyHint(null); return; }

                    // Special path: LAUNCH & FORGET arms DroneAutopilot, not IssueCommand.
                    if (ConsumePendingHotkeyAsAutopilot(target)) return;

                    _hasPendingHotkey = false;
                    SetHotkeyHint(null);
                    NATO_C2_Manager.Instance.IssueCommand(_pendingHotkeyOrder, target);
                    CommandPing.Spawn(target, _pendingHotkeyOrder == CommandOrder.Attack
                        ? CommandPing.Kind.Attack : CommandPing.Kind.Move);
                    return;
                }

                if (delta.magnitude < dragThreshold)
                {
                    // ---- CLICK (not drag) — raycast for a specific unit ----
                    Agent clickedAgent = RaycastAgent(cur);
                    bool isDoubleClick = (clickedAgent != null
                        && clickedAgent == _lastClickedAgent
                        && (Time.unscaledTime - _lastClickTime) <= doubleClickWindow);

                    if (clickedAgent != null)
                    {
                        if (isDoubleClick)
                        {
                            SelectAllSameType(clickedAgent, additive);
                        }
                        else if (additive)
                        {
                            ToggleInSelection(clickedAgent);
                        }
                        else
                        {
                            NATO_C2_Manager.Instance.SetSelection(new[] { clickedAgent });
                        }
                        _lastClickedAgent = clickedAgent;
                        _lastClickTime = Time.unscaledTime;
                    }
                    else if (!additive)
                    {
                        NATO_C2_Manager.Instance.ClearSelection();
                        _lastClickedAgent = null;
                    }
                }
                else
                {
                    // ---- DRAG — rectangular selection ----
                    Rect rect = Rect.MinMaxRect(
                        Mathf.Min(_dragStart.x, cur.x),
                        Mathf.Min(_dragStart.y, cur.y),
                        Mathf.Max(_dragStart.x, cur.x),
                        Mathf.Max(_dragStart.y, cur.y));
                    var pick = new List<Agent>();
                    foreach (var a in NATO_C2_Manager.Instance.Agents)
                    {
                        if (a == null || a.affiliation != Affiliation.Friendly) continue;
                        if (!_layerVisible[(int)a.layer]) continue;
                        var sp = viewCamera.WorldToScreenPoint(a.transform.position);
                        if (sp.z < 0f) continue;
                        if (rect.Contains((Vector2)sp)) pick.Add(a);
                    }
                    if (additive)
                    {
                        var combined = new List<Agent>(NATO_C2_Manager.Instance.Selected);
                        foreach (var p in pick) if (!combined.Contains(p)) combined.Add(p);
                        NATO_C2_Manager.Instance.SetSelection(combined);
                    }
                    else
                    {
                        NATO_C2_Manager.Instance.SetSelection(pick);
                    }
                }
            }
        }

        // Raycast for the topmost Agent under the cursor.
        private Agent RaycastAgent(Vector2 screenPos)
        {
            var ray = viewCamera.ScreenPointToRay(screenPos);
            var hits = Physics.RaycastAll(ray, 1000f);
            Agent best = null;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < hits.Length; i++)
            {
                var a = hits[i].collider.GetComponentInParent<Agent>();
                if (a == null) continue;
                if (a.affiliation != Affiliation.Friendly) continue;
                if (!_layerVisible[(int)a.layer]) continue;
                if (hits[i].distance < bestD) { best = a; bestD = hits[i].distance; }
            }
            return best;
        }

        private void ToggleInSelection(Agent a)
        {
            var current = new List<Agent>(NATO_C2_Manager.Instance.Selected);
            if (current.Remove(a)) NATO_C2_Manager.Instance.SetSelection(current);
            else { current.Add(a); NATO_C2_Manager.Instance.SetSelection(current); }
        }

        private void SelectAllSameType(Agent seed, bool additive)
        {
            var pick = new List<Agent>();
            foreach (var a in NATO_C2_Manager.Instance.Agents)
            {
                if (a == null || a.affiliation != seed.affiliation) continue;
                if (a.unitType != seed.unitType) continue;
                if (!_layerVisible[(int)a.layer]) continue;
                if ((a.transform.position - seed.transform.position).sqrMagnitude > sameTypeRadius * sameTypeRadius) continue;
                pick.Add(a);
            }
            if (additive)
            {
                var combined = new List<Agent>(NATO_C2_Manager.Instance.Selected);
                foreach (var p in pick) if (!combined.Contains(p)) combined.Add(p);
                NATO_C2_Manager.Instance.SetSelection(combined);
            }
            else
            {
                NATO_C2_Manager.Instance.SetSelection(pick);
            }
        }

        // =================================================================
        //  Right-click — SMART ACTION (SC2/AoE pattern):
        //
        //      RMB on hostile         → ATTACK at hostile position
        //      RMB on friendly        → MOVE to that unit's position
        //      RMB on empty ground    → MOVE to point
        //      Shift+RMB / Alt+RMB    → open the radial verb menu
        //                                (LOITER, SWARM, RTB, HOLD, …)
        //
        //  Selection ALWAYS persists through the command. A coloured ground
        //  ping confirms where the order landed (green = move, red = attack).
        // =================================================================
        private void HandleRightClick()
        {
            // While the verb menu is open, RMB cancels — handled inside the menu.
            if (radialMenu != null && radialMenu.IsOpen) return;
            if (!Input.GetMouseButtonDown(1)) return;
            if (viewCamera == null) return;
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null || mgr.Selected.Count == 0) return;

            if (_hasPendingHotkey) { _hasPendingHotkey = false; SetHotkeyHint(null); }

            bool wantsVerbMenu = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                              || Input.GetKey(KeyCode.LeftAlt)   || Input.GetKey(KeyCode.RightAlt);

            // Resolve the cursor → unit and/or ground point.
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            Vector3 groundPoint = Vector3.zero;
            bool haveGround = false;
            Agent targetUnit = null;
            if (Physics.Raycast(ray, out var hit, 1000f))
            {
                groundPoint = hit.point;
                haveGround = true;
                targetUnit = hit.collider.GetComponentInParent<Agent>();
            }
            else if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter))
            {
                groundPoint = ray.GetPoint(enter);
                haveGround = true;
            }
            if (!haveGround) return;

            if (wantsVerbMenu && radialMenu != null)
            {
                radialMenu.Open(groundPoint);
                return;
            }

            // Smart action — selection PERSISTS through this call (Manager
            // doesn't touch SetSelection from IssueCommand).
            if (targetUnit != null && targetUnit.affiliation == Affiliation.Hostile)
            {
                LogTargetEcho(targetUnit.transform.position, "ATTACK");
                mgr.IssueCommand(CommandOrder.Attack, targetUnit.transform.position);
                CommandPing.Spawn(targetUnit.transform.position, CommandPing.Kind.Attack);
                return;
            }
            if (targetUnit != null && targetUnit.affiliation == Affiliation.Friendly)
            {
                // Move-to-ally for now. Hook FOLLOW behaviour here later.
                LogTargetEcho(targetUnit.transform.position, "MOVE-TO-ALLY");
                mgr.IssueCommand(CommandOrder.Move, targetUnit.transform.position);
                CommandPing.Spawn(targetUnit.transform.position, CommandPing.Kind.Move);
                return;
            }
            LogTargetEcho(groundPoint, "MOVE");
            mgr.IssueCommand(CommandOrder.Move, groundPoint);
            CommandPing.Spawn(groundPoint, CommandPing.Kind.Move);
        }

        // Diagnostic: log every right-click target to the Console + radio chat so
        // the operator can verify the destination coordinates Unity computed.
        private void LogTargetEcho(Vector3 target, string verb)
        {
            Debug.Log($"[TacticalHUD] RMB {verb} → world=({target.x:F2}, {target.y:F2}, {target.z:F2})");
            NATO.C2.Net.FeedHub.Instance?.PublishRadio(new NATO.C2.Net.RadioMessage
            {
                net = "TANGO-6", timestampUtc = System.DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"<color=#6cf>{verb}</color> → X={target.x:F1} Z={target.z:F1}",
                severity = NATO.C2.Net.RadioSeverity.System
            });
        }

        // =================================================================
        //  Hotkeys — Starcraft-style direct command keys.
        // =================================================================
        private void HandleHotkeys()
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return;

            // Escape — cancel any pending order, then clear selection.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_hasPendingHotkey) { _hasPendingHotkey = false; SetHotkeyHint(null); }
                else mgr.ClearSelection();
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            // Ctrl+A — select all friendlies on visible layers.
            if (Input.GetKeyDown(KeyCode.A) && ctrl)
            {
                var all = new List<Agent>();
                foreach (var a in mgr.Agents)
                {
                    if (a == null || a.affiliation != Affiliation.Friendly) continue;
                    if (!_layerVisible[(int)a.layer]) continue;
                    all.Add(a);
                }
                mgr.SetSelection(all);
                return;
            }

            // Space — center camera on the current selection.
            if (Input.GetKeyDown(KeyCode.Space) && mgr.Selected.Count > 0 && viewCamera != null)
            {
                Vector3 sum = Vector3.zero;
                foreach (var a in mgr.Selected) sum += a.transform.position;
                Vector3 centre = sum / mgr.Selected.Count;
                Vector3 cam = viewCamera.transform.position;
                Vector3 dir = (cam - centre).normalized;
                viewCamera.transform.position = centre + dir * Mathf.Max(40f, cam.y * 0.9f);
                viewCamera.transform.LookAt(centre);
                return;
            }

            // Order keys arm a pending command — next LMB click on the ground commits it.
            if (mgr.Selected.Count > 0 && !ctrl)
            {
                if (Input.GetKeyDown(KeyCode.A)) ArmHotkey(CommandOrder.Attack, "ATTACK-MOVE");
                if (Input.GetKeyDown(KeyCode.M)) ArmHotkey(CommandOrder.Move,   "MOVE");
                if (Input.GetKeyDown(KeyCode.F)) ArmHotkey(CommandOrder.Swarm,  "SWARM");
                if (Input.GetKeyDown(KeyCode.H)) { mgr.IssueCommand(CommandOrder.Hold,   GetSelectionCentre()); _hasPendingHotkey = false; SetHotkeyHint(null); }
                if (Input.GetKeyDown(KeyCode.S)) { mgr.IssueCommand(CommandOrder.Hold,   GetSelectionCentre()); _hasPendingHotkey = false; SetHotkeyHint(null); }
                if (Input.GetKeyDown(KeyCode.L)) ArmHotkey(CommandOrder.Loiter, "LOITER");
                if (Input.GetKeyDown(KeyCode.R)) { mgr.IssueCommand(CommandOrder.RTB,    Vector3.zero); _hasPendingHotkey = false; SetHotkeyHint(null); }

                // Soldier abilities — only available if at least one Infantry unit is in the selection.
                bool hasInfantry = false;
                bool hasDrone    = false;
                foreach (var a in mgr.Selected)
                {
                    if (a == null) continue;
                    if (a.unitType == UnitType.Infantry) hasInfantry = true;
                    if (a.unitType == UnitType.Drone)    hasDrone = true;
                }
                if (hasInfantry)
                {
                    if (Input.GetKeyDown(KeyCode.Q)) FireMissionRequest();
                    if (Input.GetKeyDown(KeyCode.W)) CasualtyEvac(CasualtyKind.Medevac);
                    if (Input.GetKeyDown(KeyCode.E)) CasualtyEvac(CasualtyKind.Casevac);
                }
                if (hasDrone && Input.GetKeyDown(KeyCode.B)) ArmHotkey(CommandOrder.Move, "LAUNCH & FORGET");
                if (_hasPendingHotkey && _pendingHotkeyOrder == CommandOrder.Move
                    && Input.GetKeyDown(KeyCode.B))
                {
                    // (no-op — placeholder so the hint shows; LMB commits)
                }
            }
        }

        // Hook the LMB-commit path so when LAUNCH & FORGET is pending, the
        // commit calls DroneAutopilot.LaunchSelectedDrones instead of IssueCommand.
        private bool ConsumePendingHotkeyAsAutopilot(Vector3 target)
        {
            if (!_hasPendingHotkey) return false;
            // The hint label is our cue: "LAUNCH & FORGET" → autopilot path.
            if (hotkeyHintLabel != null && hotkeyHintLabel.text != null
                && hotkeyHintLabel.text.StartsWith("LAUNCH"))
            {
                int n = DroneAutopilot.LaunchSelectedDrones(target);
                _hasPendingHotkey = false;
                SetHotkeyHint(null);
                CommandPing.Spawn(target, CommandPing.Kind.Other);
                if (n == 0) return false;
                return true;
            }
            return false;
        }

        private enum CasualtyKind { Medevac, Casevac }

        private void FireMissionRequest()
        {
            _pendingHotkeyOrder = CommandOrder.Attack;
            _hasPendingHotkey = true;
            SetHotkeyHint("FIRE MISSION  – LMB on target  ·  Esc to cancel");
            // PRODUCTION-TODO: emit a NATO Call-For-Fire (CFF) NATO MIP/JFOPS message on
            // the FIRES net via FeedHub.PublishRadio + a CoT casevac request.
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return;
            var caller = FirstInfantryInSelection(mgr);
            if (caller == null) return;
            NATO.C2.Net.FeedHub.Instance?.PublishRadio(new NATO.C2.Net.RadioMessage
            {
                net = "HQ",
                timestampUtc = System.DateTime.UtcNow,
                fromCallsign = caller.callsign,
                text = "FIRE MISSION inbound — designate target",
                severity = NATO.C2.Net.RadioSeverity.Warning
            });
        }

        private void CasualtyEvac(CasualtyKind kind)
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return;
            var caller = FirstInfantryInSelection(mgr);
            if (caller == null) return;
            string label = kind == CasualtyKind.Medevac ? "MEDEVAC" : "CASEVAC";
            CommandPing.Spawn(caller.transform.position, CommandPing.Kind.Other);
            NATO.C2.Net.FeedHub.Instance?.PublishRadio(new NATO.C2.Net.RadioMessage
            {
                net = "MEDEVAC",
                timestampUtc = System.DateTime.UtcNow,
                fromCallsign = caller.callsign,
                text = $"{label} REQUEST at my position — HP {Mathf.RoundToInt(caller.health)}/{Mathf.RoundToInt(caller.maxHealth)}",
                severity = NATO.C2.Net.RadioSeverity.Critical
            });
            // PRODUCTION-TODO: emit a NATO 9-line MEDEVAC request as a CoT
            // <__casevac> detail block; HQ casevac dispatcher consumes via OnCot.
        }

        private static Agent FirstInfantryInSelection(NATO_C2_Manager mgr)
        {
            foreach (var a in mgr.Selected)
                if (a != null && a.unitType == UnitType.Infantry) return a;
            return null;
        }

        private void ArmHotkey(CommandOrder order, string label)
        {
            _pendingHotkeyOrder = order;
            _hasPendingHotkey = true;
            SetHotkeyHint(label + "  – LMB on target  ·  Esc to cancel");
        }

        private void SetHotkeyHint(string text)
        {
            if (hotkeyHintLabel == null) return;
            if (string.IsNullOrEmpty(text)) { hotkeyHintLabel.text = ""; return; }
            hotkeyHintLabel.text = text;
        }

        private Vector3 GetSelectionCentre()
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null || mgr.Selected.Count == 0) return Vector3.zero;
            Vector3 sum = Vector3.zero;
            foreach (var a in mgr.Selected) sum += a.transform.position;
            return sum / mgr.Selected.Count;
        }

        // =================================================================
        //  Control groups: Ctrl+1..9 store, 1..9 recall.
        // =================================================================
        private void HandleControlGroups()
        {
            for (int k = 1; k <= 9; k++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha0 + k)) continue;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                {
                    var mgr = NATO_C2_Manager.Instance;
                    if (mgr == null) continue;
                    // Strip the new selection out of any previous group, then assign.
                    foreach (var a in mgr.Agents)
                    {
                        if (a == null) continue;
                        if (a.controlGroup == k) a.controlGroup = 0;
                    }
                    foreach (var a in mgr.Selected)
                    {
                        if (a != null) a.controlGroup = k;
                    }
                    _controlGroups[k] = new List<Agent>(mgr.Selected);
                }
                else if (_controlGroups.TryGetValue(k, out var grp))
                {
                    NATO_C2_Manager.Instance?.SetSelection(grp);
                }
            }
        }

        // =================================================================
        //  Top bar text
        // =================================================================
        private void UpdateTopBar()
        {
            var mgr = NATO_C2_Manager.Instance;
            if (missionLabel    != null) missionLabel.text    = missionName;
            if (swarmCountLabel != null) swarmCountLabel.text = mgr != null ? $"SELECTED: {mgr.Selected.Count}" : "SELECTED: 0";
            if (threatLevelLabel != null && mgr != null && mgr.mythos != null)
            {
                int t = mgr.mythos.ThreatField.Count;
                threatLevelLabel.text = $"THREATS: {t}";
                threatLevelLabel.color = t > 30 ? NATOPalette.HostileRed
                                       : t > 10 ? NATOPalette.NeutralYellow
                                       : NATOPalette.FriendlyGreen;
            }
            if (aiStatusLabel != null && mgr != null)
            {
                aiStatusLabel.text = mgr.AutonomousMode ? "MYTHOS: AUTONOMOUS" : "MYTHOS: ADVISORY";
                aiStatusLabel.color = mgr.AutonomousMode ? NATOPalette.HostileRed : NATOPalette.AccentCyan;
            }
        }

        // =================================================================
        //  Threat heatmap — draw pulsing rings + countdown labels.
        // =================================================================
        private void OnRenderObject()
        {
            if (!drawThreatField || threatMaterial == null) return;
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null || mgr.mythos == null) return;

            threatMaterial.SetPass(0);
            GL.PushMatrix();
            foreach (var t in mgr.mythos.ThreatField)
            {
                // Subtler than before — these are FORECASTS, they shouldn't visually
                // dominate the actual contact rings drawn by EngagementTracker.
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 3f - t.timeToImpact);
                float alpha = 0.18f + pulse * 0.22f;
                GL.Begin(GL.LINE_STRIP);
                GL.Color(new Color(1f, 0.30f, 0.30f, alpha));
                int seg = 28;
                for (int i = 0; i <= seg; i++)
                {
                    float a = (i / (float)seg) * Mathf.PI * 2f;
                    GL.Vertex(t.centre + new Vector3(Mathf.Cos(a), 0.05f, Mathf.Sin(a)) * t.radius);
                }
                GL.End();
            }
            GL.PopMatrix();
        }
    }
}
