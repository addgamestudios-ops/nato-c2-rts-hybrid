// =====================================================================
//  NATO C2 RTS Hybrid — OperatorIdentityWindow.cs
//  ---------------------------------------------------------------------
//  Small Editor window for setting the per-station OperatorIdentity.
//  Two devs running side-by-side just open this once, pick distinct
//  callsigns + station prefixes, hit Save — and the two Unity clients
//  show up as separate operators on the shared TAK Server.
//
//  Quick presets at the top map common roles to consistent prefixes
//  so you don't have to think about it.
// =====================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2.EditorTools
{
    public class OperatorIdentityWindow : EditorWindow
    {
        [MenuItem("NATO C2/Operator Identity...", priority = 40)]
        public static void Open()
        {
            var win = GetWindow<OperatorIdentityWindow>("Operator Identity");
            win.minSize = new Vector2(360, 280);
        }

        private string _callsign = "WATCH-1";
        private string _role     = "JTAC";
        private string _station  = "W1";

        private void OnEnable()
        {
            // Pull current identity from PlayerPrefs so we start with what's saved.
            _callsign = PlayerPrefs.GetString("NATO_C2_OPERATOR_CALLSIGN", _callsign);
            _role     = PlayerPrefs.GetString("NATO_C2_OPERATOR_ROLE",     _role);
            _station  = PlayerPrefs.GetString("NATO_C2_OPERATOR_STATION",  _station);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Operator identity for this Unity instance",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Picks the per-station identity for collaborative TAK sessions. " +
                "Two operators on the same TAK Server MUST have different station " +
                "prefixes — otherwise their CoT UIDs collide on the wire.",
                MessageType.Info);

            EditorGUILayout.Space();

            // Presets row
            EditorGUILayout.LabelField("Quick presets", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Watch-1 (JTAC)"))   { _callsign = "WATCH-1"; _role = "JTAC";    _station = "W1"; }
                if (GUILayout.Button("Fires-3 (FDC)"))    { _callsign = "FIRES-3"; _role = "FDC";     _station = "F3"; }
                if (GUILayout.Button("Sky-7 (UAV pilot)")){ _callsign = "SKY-7";   _role = "UAV-OP";  _station = "S7"; }
                if (GUILayout.Button("Doc-9 (MEDIC)"))    { _callsign = "DOC-9";   _role = "MEDIC";   _station = "D9"; }
            }

            EditorGUILayout.Space();
            _callsign = EditorGUILayout.TextField("Callsign", _callsign);
            _role     = EditorGUILayout.TextField("Role",     _role);
            _station  = EditorGUILayout.TextField("Station prefix (2-3 chars)", _station);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("CoT UID preview:",
                $"NATO-C2-{_station}-{_callsign}",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save", GUILayout.Height(28)))
                {
                    PlayerPrefs.SetString("NATO_C2_OPERATOR_CALLSIGN", _callsign);
                    PlayerPrefs.SetString("NATO_C2_OPERATOR_ROLE",     _role);
                    PlayerPrefs.SetString("NATO_C2_OPERATOR_STATION",  _station);
                    PlayerPrefs.Save();
                    // If a live instance exists in Play mode, reload it.
                    if (OperatorIdentity.Instance != null) OperatorIdentity.Instance.Load();
                    Debug.Log($"[OperatorIdentity] Saved {_station} — {_callsign} ({_role})");
                }
                if (GUILayout.Button("Reset to defaults"))
                {
                    PlayerPrefs.DeleteKey("NATO_C2_OPERATOR_CALLSIGN");
                    PlayerPrefs.DeleteKey("NATO_C2_OPERATOR_ROLE");
                    PlayerPrefs.DeleteKey("NATO_C2_OPERATOR_STATION");
                    _callsign = "WATCH-1"; _role = "JTAC"; _station = "W1";
                }
            }
        }
    }
}
#endif
