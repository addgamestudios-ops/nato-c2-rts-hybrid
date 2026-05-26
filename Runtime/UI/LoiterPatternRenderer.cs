// =====================================================================
//  NATO C2 RTS Hybrid — LoiterPatternRenderer.cs
//  ---------------------------------------------------------------------
//  Renders a 3D cyan racetrack loiter pattern under each loitering drone
//  plus a floating altitude HAE label. Mirrors the Anduril Lattice
//  ALTIUS 600 ISR view in the screenshots:
//
//      [ALTIUS 600 ISR] 3109 ft HAE
//          ╭───────╮
//          │       │   ← cyan racetrack ground-projection
//          ╰───────╯
//
//  Stays drawn while:
//      • The host Agent's `currentOrder == CommandOrder.Loiter`, OR
//      • The host has a DroneAutopilot in Cruise/Engage state with a
//        non-empty path (it's effectively orbiting / patrolling).
//
//  Pattern shape: a rounded-rectangle racetrack centered on the drone's
//  current position, oriented along its current heading. We sample N
//  points around the racetrack and emit GL.LINE_STRIP at y = ground
//  altitude (slightly above terrain) so it reads as a ground projection.
//
//  Why ground projection rather than at-altitude: real tactical displays
//  draw the orbit on the map plane with an altitude *label* attached —
//  controllers think in 2D plus AGL. Anduril's screenshot does the same.
// =====================================================================

using UnityEngine;

namespace NATO.C2.UI
{
    [RequireComponent(typeof(NATO.C2.Agent))]
    [AddComponentMenu("NATO C2/Loiter Pattern Renderer")]
    public class LoiterPatternRenderer : MonoBehaviour
    {
        [Header("Racetrack")]
        public float length = 18f;          // along-heading
        public float width  = 8f;           // across-heading
        [Range(8, 64)] public int segments = 32;
        public float groundLift = 0.4f;     // metres above ground

        [Header("Visual")]
        public Color color = new Color(0.10f, 0.90f, 1.00f, 0.85f);
        public float lineThicknessPx = 2f;

        [Header("Label")]
        public bool showLabel = true;
        public float labelLift = 5.5f;
        [Tooltip("Multiplier from Unity Y units to feet HAE for the label. Tune to match your altitude layer scale.")]
        public float feetPerUnit = 25f;

        // ---------- runtime ------------------------------------------
        private NATO.C2.Agent _agent;
        private Material _mat;
        private TextMesh  _label;
        private GameObject _labelGo;

        private void Awake()
        {
            _agent = GetComponent<NATO.C2.Agent>();
            _mat = new Material(Shader.Find("Hidden/Internal-Colored"));
            _mat.hideFlags = HideFlags.HideAndDontSave;
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            _mat.SetInt("_ZWrite",   0);
            BuildLabel();
        }

        private void BuildLabel()
        {
            _labelGo = new GameObject("LoiterLabel");
            _labelGo.transform.SetParent(transform, false);
            _labelGo.transform.localPosition = new Vector3(0f, labelLift, 0f);
            _label = _labelGo.AddComponent<TextMesh>();
            _label.alignment = TextAlignment.Center;
            _label.anchor = TextAnchor.MiddleCenter;
            _label.characterSize = 0.18f;
            _label.fontSize = 32;
            _label.color = Color.white;
            _label.text  = "";
            _label.fontStyle = FontStyle.Bold;
            // Built-in Billboard from DronePipPanel isn't accessible here — use
            // a tiny inline one.
            _labelGo.AddComponent<LabelBillboard>();
        }

        private bool IsLoitering()
        {
            if (_agent == null) return false;
            if (_agent.currentOrder == NATO.C2.CommandOrder.Loiter) return true;
            // Drones with an autopilot that hasn't transitioned to RTB are
            // continually orbiting their AO — treat as loitering for display.
            if (_agent.unitType == NATO.C2.UnitType.Drone &&
                _agent.layer != NATO.C2.AltitudeLayer.Ground &&
                _agent.currentOrder != NATO.C2.CommandOrder.RTB &&
                _agent.currentVelocity.sqrMagnitude < 0.5f * 0.5f)
                return true;
            return false;
        }

        private void Update()
        {
            bool show = IsLoitering();
            if (_labelGo != null) _labelGo.SetActive(show && showLabel);
            if (show && _label != null)
            {
                float hae = transform.position.y * feetPerUnit + 1000f; // offset so demo reads like real HAE
                _label.text = $"[{_agent.callsign}] {hae:F0} ft HAE";
                _label.color = color;
            }
        }

        private void OnRenderObject()
        {
            if (!IsLoitering()) return;
            if (_mat == null) return;
            _mat.SetPass(0);

            // Build the racetrack in local space around the drone's XZ position,
            // oriented to its current facing (or world Z if not moving).
            Vector3 origin = transform.position;
            origin.y += groundLift;

            Vector3 fwd = _agent != null && _agent.desiredFacing.sqrMagnitude > 0.01f
                          ? _agent.desiredFacing.normalized : Vector3.forward;
            fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x); // perpendicular on ground plane

            // Half-extents minus the rounded corner radius so the straight
            // sides are clean.
            float r = width * 0.5f;
            float halfLen = Mathf.Max(0.01f, length * 0.5f - r);

            int side = Mathf.Max(2, segments / 2);
            for (int passI = 0; passI < Mathf.Max(1, (int)lineThicknessPx); passI++)
            {
                float pxOff = (passI - (lineThicknessPx - 1f) * 0.5f) * 0.05f;
                GL.Begin(GL.LINE_STRIP); GL.Color(color);

                // Right semicircle (rear of racetrack)
                for (int i = 0; i <= side; i++)
                {
                    float t = (float)i / side;
                    float ang = Mathf.PI * -0.5f + Mathf.PI * t; // -90° → +90°
                    Vector3 p = origin + fwd * (halfLen + r * Mathf.Sin(ang)) + right * (r * Mathf.Cos(ang) + pxOff);
                    GL.Vertex(p);
                }
                // Left semicircle (front of racetrack)
                for (int i = 0; i <= side; i++)
                {
                    float t = (float)i / side;
                    float ang = Mathf.PI * 0.5f + Mathf.PI * t; // 90° → 270°
                    Vector3 p = origin + fwd * (-halfLen + r * Mathf.Sin(ang)) + right * (r * Mathf.Cos(ang) + pxOff);
                    GL.Vertex(p);
                }
                // Close the loop back to the starting point of the right semicircle.
                {
                    Vector3 start = origin + fwd * halfLen + right * (-r + pxOff);
                    GL.Vertex(start);
                }
                GL.End();
            }
        }

        private void OnDestroy()
        {
            if (_mat != null) DestroyImmediate(_mat);
            if (_labelGo != null) Destroy(_labelGo);
        }

        // Cheap face-the-camera component for the label.
        private class LabelBillboard : MonoBehaviour
        {
            private Camera _cam;
            private void LateUpdate()
            {
                if (_cam == null) _cam = Camera.main;
                if (_cam == null) return;
                var p = transform.position;
                var c = _cam.transform.position;
                transform.rotation = Quaternion.LookRotation(p - c, Vector3.up);
            }
        }
    }
}
