// =====================================================================
//  NATO C2 RTS Hybrid — DispatchTrace.cs
//  ---------------------------------------------------------------------
//  Renders a brief animated dashed line in world space from a dispatched
//  unit to the target grid. Visual confirmation of WHO is responding to
//  an ACCEPT.  Uses GL.LINES in OnRenderObject — no Renderer/Mesh, no
//  garbage. Lives for `lifetime` seconds then self-destructs.
//
//  This is the same rendering grammar SitaWare uses for "task in flight"
//  visualisations and ATAK uses for routed strike packages.
// =====================================================================

using UnityEngine;

namespace NATO.C2.UI
{
    [AddComponentMenu("NATO C2/Dispatch Trace")]
    public class DispatchTrace : MonoBehaviour
    {
        public Vector3 from;
        public Vector3 to;
        public Color   color = new Color(0.30f, 0.95f, 0.45f, 1f);
        public float   lifetime = 4f;
        public float   dashLength = 2.2f;   // metres of "on" per dash
        public float   gapLength  = 1.8f;   // metres of "off" between dashes
        public float   scrollSpeed = 6f;    // dashes drift toward the target

        private Material _mat;
        private float    _born;

        public static DispatchTrace Create(Vector3 from, Vector3 to, Color color,
                                           float lifetime = 4f)
        {
            var go = new GameObject($"DispatchTrace_{Time.frameCount}");
            var dt = go.AddComponent<DispatchTrace>();
            dt.from = from + Vector3.up * 0.4f;
            dt.to   = to   + Vector3.up * 0.4f;
            dt.color = color;
            dt.lifetime = lifetime;
            return dt;
        }

        private void Awake()
        {
            _born = Time.time;
            _mat = new Material(Shader.Find("Hidden/Internal-Colored"));
            _mat.hideFlags = HideFlags.HideAndDontSave;
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            _mat.SetInt("_ZWrite",   0);
        }

        private void Update()
        {
            if (Time.time - _born > lifetime) Destroy(gameObject);
        }

        private void OnRenderObject()
        {
            if (_mat == null) return;
            _mat.SetPass(0);

            float age = Time.time - _born;
            // Fade out in the last quarter of the lifetime.
            float fade = age < lifetime * 0.75f ? 1f : Mathf.Clamp01((lifetime - age) / (lifetime * 0.25f));
            var c = color;
            c.a *= fade;

            float total = Vector3.Distance(from, to);
            if (total < 0.001f) return;
            Vector3 dir = (to - from) / total;
            float step = dashLength + gapLength;
            float offset = (Time.time * scrollSpeed) % step;

            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);
            GL.Color(c);

            // First partial dash (the leading "head" of the trail).
            float t = -offset;
            while (t < total)
            {
                float a = Mathf.Max(0f, t);
                float b = Mathf.Min(total, t + dashLength);
                if (b > a)
                {
                    GL.Vertex(from + dir * a);
                    GL.Vertex(from + dir * b);
                }
                t += step;
            }

            GL.End();
            GL.PopMatrix();
        }
    }
}
