// =====================================================================
//  NATO C2 RTS Hybrid — NATO_C2_EditorTools.cs
//  ---------------------------------------------------------------------
//  Editor menu items for one-click scene setup, asset validation,
//  and HPA* grid visualisation.
// =====================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.EditorTools
{
    public static class NATO_C2_EditorTools
    {
        [MenuItem("NATO C2/Create Manager in Scene", priority = 100)]
        public static void CreateManager()
        {
            var go = new GameObject("NATO_C2_Manager",
                typeof(NATO_C2_Manager),
                typeof(ORCA),
                typeof(HPAStar),
                typeof(FormationController),
                typeof(AIAutonomousMode));
            Undo.RegisterCreatedObjectUndo(go, "Create NATO_C2_Manager");
            Selection.activeGameObject = go;
        }

        [MenuItem("NATO C2/Create HUD Canvas", priority = 110)]
        public static void CreateHud()
        {
            var canvasGo = new GameObject("NATO_C2_HUD",
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(UI.TacticalHUD),
                typeof(MilsymbolBridge));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create NATO_C2_HUD");
            Selection.activeGameObject = canvasGo;
        }

        [MenuItem("NATO C2/Validate Package", priority = 200)]
        public static void Validate()
        {
            int errors = 0;
            if (Object.FindAnyObjectByType<NATO_C2_Manager>() == null)
            { Debug.LogWarning("[NATO C2] No NATO_C2_Manager in scene."); errors++; }
            if (Object.FindAnyObjectByType<UI.TacticalHUD>() == null)
            { Debug.LogWarning("[NATO C2] No TacticalHUD in scene."); errors++; }
            if (errors == 0) Debug.Log("[NATO C2] Package validated — all systems present.");
        }

        // -----------------------------------------------------------------
        //  CoT interop quick-test menu items. Only meaningful during Play
        //  mode with the TAK adapter connected. They publish typed events
        //  with proper lat/lon so we can verify the wire format end-to-end
        //  against the mock TAK server (or a real ATAK client).
        // -----------------------------------------------------------------

        [MenuItem("NATO C2/CoT Test/Send Call-For-Fire at origin", priority = 300)]
        public static void TestCallForFire()
        {
            var tak = Object.FindAnyObjectByType<Net.TakServerCotAdapter>();
            if (tak == null) { Debug.LogWarning("[NATO C2] No TAK adapter in scene."); return; }
            tak.PublishCallForFire(Vector3.zero, requester: "TEST-OPS",
                                   remarks: "Editor-menu test fire mission");
            Debug.Log("[NATO C2] CoT b-r-f-h-c emitted at (0,0,0).");
        }

        [MenuItem("NATO C2/CoT Test/Send MEDEVAC at origin", priority = 301)]
        public static void TestMedevac()
        {
            var tak = Object.FindAnyObjectByType<Net.TakServerCotAdapter>();
            if (tak == null) { Debug.LogWarning("[NATO C2] No TAK adapter in scene."); return; }
            tak.PublishMedevac(Vector3.zero, requester: "TEST-OPS",
                               patientCallsign: "ALPHA-3", precedence: 'A',
                               patientsLitter: 1, patientsAmbulatory: 0,
                               remarks: "Editor-menu test medevac");
            Debug.Log("[NATO C2] CoT b-r-c-m emitted at (0,0,0), precedence A.");
        }

        [MenuItem("NATO C2/CoT Test/Send LZ Marker at origin", priority = 302)]
        public static void TestMarker()
        {
            var tak = Object.FindAnyObjectByType<Net.TakServerCotAdapter>();
            if (tak == null) { Debug.LogWarning("[NATO C2] No TAK adapter in scene."); return; }
            tak.PublishMarker(Vector3.zero, "LZ-FALCON", cotType: "b-m-p-w");
            Debug.Log("[NATO C2] LZ-FALCON CoT marker emitted at (0,0,0).");
        }
    }
}
#endif
