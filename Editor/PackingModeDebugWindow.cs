// =====================================================================
//  NATO C2 RTS Hybrid — PackingModeDebugWindow.cs
//  ---------------------------------------------------------------------
//  In-Editor live debug surface for the Link 16 PPLI burst-mode system.
//  Shows every Agent currently registered with the
//  Link16TdmaSimulator, the mode it would resolve to via ModeFor() —
//  including whether the mode is an explicit override or the heuristic
//  fall-through — and three buttons (STD-DP / P2DP / P4SP / clear)
//  per row so an operator can flip terminals on the fly while the
//  scene is in Play mode.
//
//  Useful for:
//      • Verifying P2DP density actually doubles envelope counts
//      • Stress-testing the auto-scale in Link16BurstModeHud
//      • Reproducing a specific mode mix during regression dev
// =====================================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using NATO.C2;
using NATO.C2.Net;

namespace NATO.C2.EditorTools
{
    public class PackingModeDebugWindow : EditorWindow
    {
        private Vector2 _scroll;
        private Link16TdmaSimulator _sim;

        [MenuItem("NATO C2/Link 16/Packing Mode Debug...", priority = 51)]
        public static void Open()
        {
            var w = GetWindow<PackingModeDebugWindow>("L16 Packing Mode");
            w.minSize = new Vector2(480, 320);
        }

        private void OnEnable() { EditorApplication.update += Repaint; }
        private void OnDisable() { EditorApplication.update -= Repaint; }

        private void OnGUI()
        {
            if (_sim == null) _sim = Object.FindAnyObjectByType<Link16TdmaSimulator>();

            EditorGUILayout.LabelField("Link 16 PPLI Packing Mode", EditorStyles.boldLabel);
            if (_sim == null)
            {
                EditorGUILayout.HelpBox(
                    "No Link16TdmaSimulator in the current scene. Open the demo or any scene with a Bootstrap that spawns the simulator and re-open this window.",
                    MessageType.Info);
                return;
            }

            // Live rate read-out — matches the in-game HUD.
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"STD-DP {_sim.StdDpEnvelopesPerSec,3} env / {_sim.StdDpMsgsPerSec,4} msg", GUILayout.Width(180));
                EditorGUILayout.LabelField($"P2DP   {_sim.P2DpEnvelopesPerSec,3} env / {_sim.P2DpMsgsPerSec,4} msg", GUILayout.Width(180));
                EditorGUILayout.LabelField($"P4SP   {_sim.P4SpEnvelopesPerSec,3} env / {_sim.P4SpMsgsPerSec,4} msg", GUILayout.Width(180));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Default mode for new terminals:");
            _sim.defaultPackingMode = (Link16TdmaSimulator.PackingMode)
                EditorGUILayout.EnumPopup(_sim.defaultPackingMode);

            EditorGUILayout.Space(8);

            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null || mgr.Agents == null || mgr.Agents.Count == 0)
            {
                EditorGUILayout.HelpBox("No agents in scene yet — enter Play mode and let the Bootstrap spawn units.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("Callsign",      GUILayout.Width(140));
                EditorGUILayout.LabelField("Type",          GUILayout.Width(80));
                EditorGUILayout.LabelField("Layer",         GUILayout.Width(60));
                EditorGUILayout.LabelField("Mode (now)",    GUILayout.Width(90));
                EditorGUILayout.LabelField("Override",      GUILayout.Width(280));
            }

            // Iterate over a snapshot to avoid issues if agents spawn/despawn mid-render.
            var snapshot = new List<Agent>(mgr.Agents);
            foreach (var a in snapshot)
            {
                if (a == null) continue;
                var mode = _sim.ModeFor(a);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(a.callsign ?? a.name, GUILayout.Width(140));
                    EditorGUILayout.LabelField(a.unitType.ToString(),  GUILayout.Width(80));
                    EditorGUILayout.LabelField(a.layer.ToString(),     GUILayout.Width(60));
                    EditorGUILayout.LabelField(ModeTag(mode),          GUILayout.Width(90));
                    if (GUILayout.Button("STD-DP", GUILayout.Width(60))) _sim.SetPackingMode(a, Link16TdmaSimulator.PackingMode.StdDp);
                    if (GUILayout.Button("P2DP",   GUILayout.Width(60))) _sim.SetPackingMode(a, Link16TdmaSimulator.PackingMode.P2Dp);
                    if (GUILayout.Button("P4SP",   GUILayout.Width(60))) _sim.SetPackingMode(a, Link16TdmaSimulator.PackingMode.P4Sp);
                    if (GUILayout.Button("Clear",  GUILayout.Width(60))) _sim.SetPackingMode(a, null);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string ModeTag(Link16TdmaSimulator.PackingMode m)
        {
            switch (m)
            {
                case Link16TdmaSimulator.PackingMode.P2Dp: return "P2DP";
                case Link16TdmaSimulator.PackingMode.P4Sp: return "P4SP";
                default:                                   return "STD-DP";
            }
        }
    }
}
#endif
