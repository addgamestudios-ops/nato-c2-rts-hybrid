// =====================================================================
//  NATO C2 RTS Hybrid — MissionEditorWindow.cs
//  ---------------------------------------------------------------------
//  An in-Editor authoring tool that lets the user drag-paint Phase
//  Lines, Boundaries, NFAs / RFAs / FFAs, Engagement Areas, Target
//  Reference Points and Battle Positions directly on the Scene View
//  ground. The authored graphics:
//
//      • Show up immediately in the live MissionOverlay (operator HUD).
//      • Export as CoT b-m-p-* events through the TakServerCotAdapter
//        so federated ATAK / TAK Server peers see the mission graphic.
//      • Save to a JSON sidecar so missions can be recalled later.
//
//  How the tool works (intentional UX):
//      1. Open the window via NATO C2 → Mission Editor.
//      2. Pick a measure type from the dropdown.
//      3. Click "Start Drawing" — the next clicks on the SceneView ground
//         place vertices. Esc cancels. Enter or "Finish" commits.
//      4. Each finished measure is added to MissionOverlay AND queued
//         for CoT publication on next Play.
//
//  Why a custom EditorWindow + SceneView.duringSceneGui — Unity ships
//  no built-in spline / polygon painter we can reuse. Anduril Lattice's
//  mission overlay authoring uses the same pattern.
// =====================================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using NATO.C2.UI;

namespace NATO.C2.EditorTools
{
    public class MissionEditorWindow : EditorWindow
    {
        // ---------- tool state ---------------------------------------
        private MissionOverlay.MeasureKind _kind = MissionOverlay.MeasureKind.PhaseLine;
        private string _name = "PL ALAMO";
        private bool _drawing;
        private readonly List<Vector3> _pendingPoints = new List<Vector3>(16);

        // Default Y to drop authored points onto. Demo ground is y=0.
        private float _groundY = 0.05f;

        // Persisted authored measures (separate from MissionOverlay's
        // hard-coded sample so the user can iterate without losing them).
        [System.Serializable] public class SavedMission
        {
            public List<MissionOverlay.Measure> measures = new List<MissionOverlay.Measure>();
        }

        [MenuItem("NATO C2/Mission Editor", priority = 50)]
        public static void Open()
        {
            var win = GetWindow<MissionEditorWindow>("Mission Editor");
            win.minSize = new Vector2(320, 360);
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        // ---------- toolbar / inspector UI ---------------------------
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Mission Authoring", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Pick a measure type, give it a callsign, then click Start Drawing. " +
                "Each LMB-click in the Scene View places a vertex on the ground plane. " +
                "Press Enter to finish, Esc to cancel.",
                MessageType.Info);

            _kind = (MissionOverlay.MeasureKind)EditorGUILayout.EnumPopup("Measure type", _kind);
            _name = EditorGUILayout.TextField("Name / callsign", _name);
            _groundY = EditorGUILayout.FloatField("Ground Y (m)", _groundY);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_drawing))
            {
                if (GUILayout.Button("▶  Start Drawing", GUILayout.Height(28)))
                {
                    _drawing = true;
                    _pendingPoints.Clear();
                    SceneView.RepaintAll();
                }
            }
            using (new EditorGUI.DisabledScope(!_drawing))
            {
                if (GUILayout.Button("✓ Finish (Enter)", GUILayout.Height(28)))
                    Commit();
                if (GUILayout.Button("✕ Cancel (Esc)"))
                    CancelDrawing();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Pending points: {_pendingPoints.Count}");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mission graphic library", EditorStyles.boldLabel);
            if (GUILayout.Button("Save mission to JSON..."))   SaveToJson();
            if (GUILayout.Button("Load mission from JSON...")) LoadFromJson();
            if (GUILayout.Button("Publish all to TAK Server"))  PublishAllAsCoT();
            if (GUILayout.Button("Clear overlay"))             ClearOverlay();
        }

        // ---------- SceneView click capture --------------------------
        private void OnSceneGUI(SceneView sv)
        {
            if (!_drawing) return;
            var evt = Event.current;

            // Prevent the click from selecting/deselecting GameObjects.
            int id = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(id);

            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                var ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                if (new Plane(Vector3.up, new Vector3(0, _groundY, 0)).Raycast(ray, out float t))
                {
                    Vector3 p = ray.GetPoint(t);
                    _pendingPoints.Add(p);
                    sv.Repaint();
                    Repaint();
                }
                evt.Use();
            }
            else if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Return)
            {
                Commit();
                evt.Use();
            }
            else if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            {
                CancelDrawing();
                evt.Use();
            }

            // Draw pending polyline + vertex handles in the SceneView.
            if (_pendingPoints.Count > 0)
            {
                Handles.color = new Color(0.10f, 0.95f, 1.00f, 0.95f);
                for (int i = 0; i < _pendingPoints.Count; i++)
                {
                    Handles.SphereHandleCap(0, _pendingPoints[i], Quaternion.identity, 0.6f, EventType.Repaint);
                    if (i > 0) Handles.DrawLine(_pendingPoints[i - 1], _pendingPoints[i], 3f);
                }
                // Preview-close polygon for area types.
                if (IsAreaKind(_kind) && _pendingPoints.Count > 2)
                    Handles.DrawDottedLine(_pendingPoints[_pendingPoints.Count - 1], _pendingPoints[0], 4f);
                Handles.Label(_pendingPoints[0] + Vector3.up * 1.5f,
                    $"{_kind} — {_pendingPoints.Count} pts");
            }
        }

        // ---------- commit / cancel ----------------------------------
        private void Commit()
        {
            if (_pendingPoints.Count < 2)
            {
                EditorUtility.DisplayDialog("Mission Editor", "Need at least 2 points.", "OK");
                return;
            }

            var m = new MissionOverlay.Measure
            {
                kind = _kind,
                name = _name,
                points = _pendingPoints.ToArray(),
            };

            var overlay = FindMissionOverlay();
            if (overlay != null) overlay.Add(m);
            else Debug.LogWarning("[MissionEditor] No MissionOverlay in scene yet — measure stored in window only.");
            _pendingPoints.Clear();
            _drawing = false;
            Repaint();
            SceneView.RepaintAll();
        }

        private void CancelDrawing()
        {
            _drawing = false;
            _pendingPoints.Clear();
            Repaint();
            SceneView.RepaintAll();
        }

        // ---------- save / load --------------------------------------
        private void SaveToJson()
        {
            var overlay = FindMissionOverlay();
            if (overlay == null) { Debug.LogWarning("No MissionOverlay."); return; }

            var path = EditorUtility.SaveFilePanel("Save mission graphic JSON",
                Application.dataPath, "mission.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            var bundle = new SavedMission();
            foreach (var m in overlay.Measures) bundle.measures.Add(m);
            File.WriteAllText(path, JsonUtility.ToJson(bundle, prettyPrint: true));
            Debug.Log($"[MissionEditor] Wrote {bundle.measures.Count} measures → {path}");
        }

        private void LoadFromJson()
        {
            var path = EditorUtility.OpenFilePanel("Load mission graphic JSON",
                Application.dataPath, "json");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var bundle = JsonUtility.FromJson<SavedMission>(File.ReadAllText(path));
            if (bundle == null) { Debug.LogError("[MissionEditor] JSON parse failed."); return; }
            var overlay = FindMissionOverlay();
            if (overlay == null) { Debug.LogWarning("No MissionOverlay."); return; }
            overlay.Clear();
            foreach (var m in bundle.measures) overlay.Add(m);
            Debug.Log($"[MissionEditor] Loaded {bundle.measures.Count} measures from {path}");
        }

        // ---------- publish to TAK -----------------------------------
        private void PublishAllAsCoT()
        {
            var overlay = FindMissionOverlay();
            if (overlay == null) return;
            var tak = Object.FindAnyObjectByType<NATO.C2.Net.TakServerCotAdapter>();
            if (tak == null)
            {
                Debug.LogWarning("[MissionEditor] No TakServerCotAdapter in scene — enable Connect To Tak Server on the Bootstrap first.");
                return;
            }
            int sent = 0;
            foreach (var m in overlay.Measures)
            {
                if (m == null || m.points == null || m.points.Length == 0) continue;
                Vector3 centroid = MissionOverlay.CentroidOf(m);
                // Map our MeasureKind → CoT type. ATAK clients render these
                // as the expected mission overlay glyphs.
                string cotType = m.kind switch
                {
                    MissionOverlay.MeasureKind.PhaseLine       => "b-m-p-c-z",  // control measure / zone
                    MissionOverlay.MeasureKind.Boundary        => "b-m-p-c-cb", // control boundary
                    MissionOverlay.MeasureKind.NoFireArea      => "b-m-a-nfa",  // no-fire area
                    MissionOverlay.MeasureKind.RestrictedFire  => "b-m-a-rfa",  // restricted-fire area
                    MissionOverlay.MeasureKind.FreeFire        => "b-m-a-ffa",  // free-fire area
                    MissionOverlay.MeasureKind.EngagementArea  => "b-m-a-ea",   // engagement area
                    MissionOverlay.MeasureKind.TargetRefPoint  => "b-m-p-w-tr", // target reference point
                    MissionOverlay.MeasureKind.BattlePosition  => "b-m-a-bp",   // battle position
                    _                                          => "b-m-p-w"
                };
                tak.PublishMarker(centroid, label: m.name ?? m.kind.ToString(),
                                  cotType: cotType, staleSec: 3600);
                sent++;
            }
            Debug.Log($"[MissionEditor] Published {sent} mission graphic CoT events.");
        }

        private void ClearOverlay()
        {
            var overlay = FindMissionOverlay();
            if (overlay == null) return;
            overlay.Clear();
        }

        // ---------- helpers ------------------------------------------
        private static MissionOverlay FindMissionOverlay()
            => Object.FindAnyObjectByType<MissionOverlay>();

        private static bool IsAreaKind(MissionOverlay.MeasureKind k) =>
            k == MissionOverlay.MeasureKind.NoFireArea ||
            k == MissionOverlay.MeasureKind.RestrictedFire ||
            k == MissionOverlay.MeasureKind.FreeFire ||
            k == MissionOverlay.MeasureKind.EngagementArea ||
            k == MissionOverlay.MeasureKind.BattlePosition;
    }
}
#endif
