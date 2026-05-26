// =====================================================================
//  NATO C2 RTS Hybrid — MavenBrackets.cs
//  ---------------------------------------------------------------------
//  Animated yellow corner brackets around detected hostile / unknown
//  units — directly mirrors the Project Maven UI grammar.
//
//      ┌─               ─┐
//                          ▲   (target inside)
//                          │
//      └─               ─┘
//
//  Trigger logic:
//      • Hostile or Unknown affiliation.
//      • Within `sensingRadius` of at least one friendly with line-of-sight
//        (proxied by simple distance for the demo).
//      • Visible in the camera frustum.
//
//  Animation: brackets pulse in/out by 10% over 0.8 s — same "alive,
//  tracking" feel Project Maven uses.
//
//  Rendering: GL.LINES in OnRenderObject (no Canvas raycast cost) so we
//  can draw an unlimited number of brackets without GC pressure. Each
//  bracket is 8 line segments (2 per corner × 4 corners).
// =====================================================================

using UnityEngine;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(140)]
    [AddComponentMenu("NATO C2/Maven Brackets")]
    public class MavenBrackets : MonoBehaviour
    {
        [Header("Detection rule")]
        [Tooltip("Friendlies within this radius (m) will 'spot' a hostile, painting brackets on it.")]
        public float sensingRadius = 80f;
        [Tooltip("Brackets stay drawn this many seconds after the friendly leaves sensing range — gives a 'last known' feel.")]
        public float persistence = 4f;
        [Tooltip("Once contact is lost, draw a dimmer ghost bracket at the LAST KNOWN world position for this many seconds.")]
        public float ghostLifetime = 8f;
        [Tooltip("Ghost bracket opacity multiplier (0..1).")]
        [Range(0f, 1f)] public float ghostAlpha = 0.45f;

        [Header("Visual")]
        public Color color = new Color(1f, 0.93f, 0.30f, 1f);
        public float bracketSize = 26f;       // half-width in screen pixels
        public float cornerLength = 8f;       // pixels of each corner stroke
        public float lineThicknessPx = 2f;
        public float pulseHz = 1.25f;
        public float pulseAmp = 0.15f;

        // ---------- state --------------------------------------------
        private Material _lineMat;
        private NATO_C2_Manager _mgr;
        private Camera _cam;
        private float[]   _lastSeenAt;       // parallel to _mgr.Agents
        private Vector3[] _lastKnownPos;     // last world XZ when spotted

        private void Awake()
        {
            _lineMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            _lineMat.hideFlags = HideFlags.HideAndDontSave;
            _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            _lineMat.SetInt("_ZWrite",   0);
        }

        private void OnRenderObject()
        {
            _mgr ??= NATO_C2_Manager.Instance;
            if (_mgr == null) return;
            _cam ??= Camera.main;
            if (_cam == null) return;
            if (_lineMat == null) return;

            // Grow tracking arrays to match agent count.
            int n = _mgr.Agents.Count;
            if (_lastSeenAt == null || _lastSeenAt.Length < n)
                _lastSeenAt = new float[Mathf.Max(n, _lastSeenAt?.Length ?? 0)];
            if (_lastKnownPos == null || _lastKnownPos.Length < n)
                _lastKnownPos = new Vector3[Mathf.Max(n, _lastKnownPos?.Length ?? 0)];

            // Update spotted timestamps.
            float now = Time.time;
            float r2 = sensingRadius * sensingRadius;
            for (int i = 0; i < n; i++)
            {
                var h = _mgr.Agents[i];
                if (h == null) continue;
                if (h.affiliation != Affiliation.Hostile && h.affiliation != Affiliation.Unknown) continue;
                if (h.health <= 0f) continue;

                // Spotted if any friendly is within sensingRadius.
                bool spotted = false;
                for (int j = 0; j < _mgr.Agents.Count && !spotted; j++)
                {
                    var f = _mgr.Agents[j];
                    if (f == null || f.affiliation != Affiliation.Friendly) continue;
                    if (f.health <= 0f) continue;
                    if ((f.transform.position - h.transform.position).sqrMagnitude < r2)
                        spotted = true;
                }
                if (spotted)
                {
                    _lastSeenAt[i] = now;
                    _lastKnownPos[i] = h.transform.position; // record while we have eyes
                }
            }

            // Draw brackets.
            _lineMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            // Pulse: a sine wave bumping size up/down by `pulseAmp` over the cycle.
            float pulse = 1f + pulseAmp * Mathf.Sin(now * pulseHz * 2f * Mathf.PI);
            float halfW = bracketSize * pulse;
            float seg = cornerLength * pulse;

            for (int i = 0; i < n; i++)
            {
                if (_lastSeenAt == null || i >= _lastSeenAt.Length) continue;
                if (_lastSeenAt[i] <= 0f) continue;
                float age = now - _lastSeenAt[i];

                // === LIVE bracket — follows the actual agent transform ===
                if (age <= persistence)
                {
                    var h = _mgr.Agents[i];
                    if (h == null) continue;
                    Vector3 sp = _cam.WorldToScreenPoint(h.transform.position + Vector3.up * 1.2f);
                    if (sp.z < 0f) continue;
                    if (sp.x < -50 || sp.x > Screen.width + 50) continue;
                    if (sp.y < -50 || sp.y > Screen.height + 50) continue;
                    float fade = age < persistence * 0.6f ? 1f
                               : Mathf.Clamp01((persistence - age) / (persistence * 0.4f));
                    Color c = color; c.a *= fade;
                    DrawBrackets(sp.x, sp.y, halfW, seg, c);
                }
                // === GHOST bracket — frozen at last-known XZ ============
                else if (age <= persistence + ghostLifetime)
                {
                    Vector3 sp = _cam.WorldToScreenPoint(_lastKnownPos[i] + Vector3.up * 1.2f);
                    if (sp.z < 0f) continue;
                    if (sp.x < -50 || sp.x > Screen.width + 50) continue;
                    if (sp.y < -50 || sp.y > Screen.height + 50) continue;
                    float ghostAge = age - persistence;
                    float fade = ghostAge < ghostLifetime * 0.7f ? 1f
                               : Mathf.Clamp01((ghostLifetime - ghostAge) / (ghostLifetime * 0.3f));
                    Color c = color;
                    c.a *= fade * ghostAlpha;
                    // Smaller bracket (60% size) so it visually reads as
                    // "stale" vs. the bigger live bracket.
                    DrawBrackets(sp.x, sp.y, halfW * 0.60f, seg * 0.60f, c);
                }
            }

            GL.PopMatrix();
        }

        private void DrawBrackets(float cx, float cy, float halfW, float seg, Color c)
        {
            // Each bracket = an L-shape made of 2 line segments.
            // Draw thicker by stacking lineThicknessPx parallel passes.
            for (int p = 0; p < Mathf.Max(1, (int)lineThicknessPx); p++)
            {
                float off = p - (lineThicknessPx - 1f) * 0.5f;
                GL.Begin(GL.LINES); GL.Color(c);

                // Top-left
                GL.Vertex3(cx - halfW + off, cy + halfW, 0);
                GL.Vertex3(cx - halfW + seg + off, cy + halfW, 0);
                GL.Vertex3(cx - halfW, cy + halfW - off, 0);
                GL.Vertex3(cx - halfW, cy + halfW - seg - off, 0);

                // Top-right
                GL.Vertex3(cx + halfW - off, cy + halfW, 0);
                GL.Vertex3(cx + halfW - seg - off, cy + halfW, 0);
                GL.Vertex3(cx + halfW, cy + halfW - off, 0);
                GL.Vertex3(cx + halfW, cy + halfW - seg - off, 0);

                // Bottom-left
                GL.Vertex3(cx - halfW + off, cy - halfW, 0);
                GL.Vertex3(cx - halfW + seg + off, cy - halfW, 0);
                GL.Vertex3(cx - halfW, cy - halfW + off, 0);
                GL.Vertex3(cx - halfW, cy - halfW + seg + off, 0);

                // Bottom-right
                GL.Vertex3(cx + halfW - off, cy - halfW, 0);
                GL.Vertex3(cx + halfW - seg - off, cy - halfW, 0);
                GL.Vertex3(cx + halfW, cy - halfW + off, 0);
                GL.Vertex3(cx + halfW, cy - halfW + seg + off, 0);

                GL.End();
            }
        }
    }
}
