// =====================================================================
//  NATO C2 RTS Hybrid — MissionOverlay.cs
//  ---------------------------------------------------------------------
//  Renders NATO tactical control measures on the battlefield as ground
//  decals + GL line draws:
//
//      Phase Line (PL)        — solid line, named (PL ALAMO, PL BRAVO…)
//      Boundary               — dashed line separating unit AORs
//      No-Fire Area (NFA)     — red outlined polygon, hatched fill
//      Restricted Fire Area   — amber outlined polygon
//      Free-Fire Area (FFA)   — green outlined polygon
//      Target Reference Point — small cross with label (TRP 1, TRP 2…)
//      Battle Position (BP)   — diamond marker with label
//      Engagement Area (EA)   — orange irregular polygon (named EA …)
//
//  All references are NATO standard APP-6 / FM 1-02.2 graphic control
//  measures. In production the layer is populated from a NATO Mission
//  Order XML (NMOX) ingested via the FeedHub.OnCot pipeline. For now we
//  author a sample mission graphic procedurally so the user can see the
//  vocabulary on the map.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.UI
{
    [AddComponentMenu("NATO C2/Mission Overlay")]
    public class MissionOverlay : MonoBehaviour
    {
        public enum MeasureKind { PhaseLine, Boundary, NoFireArea, RestrictedFire, FreeFire, EngagementArea, TargetRefPoint, BattlePosition }

        [System.Serializable]
        public class Measure
        {
            public MeasureKind kind;
            public string name;
            public Vector3[] points;
        }

        [Header("Materials")]
        public Material lineMaterial;

        [Header("Procedural sample mission")]
        public bool buildSampleOnStart = true;

        private readonly List<Measure> _measures = new List<Measure>(32);

        private void Awake()
        {
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
                lineMaterial.hideFlags = HideFlags.HideAndDontSave;
                lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                lineMaterial.SetInt("_ZWrite", 0);
            }
        }

        private void Start()
        {
            if (buildSampleOnStart) BuildSampleMissionGraphic();
        }

        // =================================================================
        //  Public — add measures from code (or from a NATO Mission Order
        //  loader plugged into FeedHub).
        // =================================================================
        public void Add(Measure m) => _measures.Add(m);
        public void Clear()       => _measures.Clear();

        /// <summary>Read-only access used by the intent parser to resolve names like "PL ALAMO" → centroid.</summary>
        public IReadOnlyList<Measure> Measures => _measures;

        /// <summary>Find a measure by case-insensitive name match (exact or substring). Returns null if no hit.</summary>
        public Measure FindByName(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = query.Trim().ToUpperInvariant();
            // Pass 1: exact match.
            for (int i = 0; i < _measures.Count; i++)
            {
                var m = _measures[i];
                if (m != null && !string.IsNullOrEmpty(m.name) && m.name.ToUpperInvariant() == q) return m;
            }
            // Pass 2: substring.
            for (int i = 0; i < _measures.Count; i++)
            {
                var m = _measures[i];
                if (m != null && !string.IsNullOrEmpty(m.name) && m.name.ToUpperInvariant().Contains(q)) return m;
            }
            return null;
        }

        /// <summary>Average of all points on a measure — usable as a "go here" target.</summary>
        public static Vector3 CentroidOf(Measure m)
        {
            if (m == null || m.points == null || m.points.Length == 0) return Vector3.zero;
            Vector3 s = Vector3.zero;
            for (int i = 0; i < m.points.Length; i++) s += m.points[i];
            return s / m.points.Length;
        }

        // =================================================================
        //  Procedural sample — a stylised company assault.
        // =================================================================
        private void BuildSampleMissionGraphic()
        {
            // Phase Lines — two horizontal control lines.
            _measures.Add(new Measure
            {
                kind = MeasureKind.PhaseLine, name = "PL ALAMO",
                points = new[] { new Vector3(-90f, 0.1f, -10f), new Vector3(90f, 0.1f, -10f) }
            });
            _measures.Add(new Measure
            {
                kind = MeasureKind.PhaseLine, name = "PL BRAVO",
                points = new[] { new Vector3(-90f, 0.1f, 25f), new Vector3(90f, 0.1f, 25f) }
            });

            // Boundary between two AORs (dashed).
            _measures.Add(new Measure
            {
                kind = MeasureKind.Boundary, name = "1/A — 2/A",
                points = new[] { new Vector3(0f, 0.1f, -45f), new Vector3(0f, 0.1f, 45f) }
            });

            // No-Fire Area (village).
            _measures.Add(new Measure
            {
                kind = MeasureKind.NoFireArea, name = "NFA HILLCREST",
                points = new[]
                {
                    new Vector3(-18f, 0.1f, 8f), new Vector3(-4f, 0.1f, 10f),
                    new Vector3(2f,   0.1f, 0f), new Vector3(-4f, 0.1f, -8f),
                    new Vector3(-18f, 0.1f, -6f)
                }
            });

            // Engagement Area (kill box).
            _measures.Add(new Measure
            {
                kind = MeasureKind.EngagementArea, name = "EA SLEDGE",
                points = new[]
                {
                    new Vector3(30f, 0.1f, -5f),  new Vector3(60f, 0.1f, -10f),
                    new Vector3(70f, 0.1f, 15f),  new Vector3(45f, 0.1f, 30f),
                    new Vector3(28f, 0.1f, 18f)
                }
            });

            // Target reference points.
            _measures.Add(new Measure { kind = MeasureKind.TargetRefPoint, name = "TRP 1", points = new[] { new Vector3(40f, 0.1f, 5f) } });
            _measures.Add(new Measure { kind = MeasureKind.TargetRefPoint, name = "TRP 2", points = new[] { new Vector3(55f, 0.1f, 20f) } });

            // Battle position (friendly defensive).
            _measures.Add(new Measure { kind = MeasureKind.BattlePosition, name = "BP 1", points = new[] { new Vector3(-40f, 0.1f, 0f) } });
        }

        // =================================================================
        //  Draw — GL line draw from a single rendering hook.
        // =================================================================
        private void OnRenderObject()
        {
            if (lineMaterial == null) return;
            lineMaterial.SetPass(0);
            GL.PushMatrix();
            foreach (var m in _measures)
            {
                Color col = ColorFor(m.kind);
                switch (m.kind)
                {
                    case MeasureKind.PhaseLine:        DrawPolyline(m.points, col, false); break;
                    case MeasureKind.Boundary:         DrawDashed(m.points, col); break;
                    case MeasureKind.NoFireArea:
                    case MeasureKind.RestrictedFire:
                    case MeasureKind.FreeFire:
                    case MeasureKind.EngagementArea:   DrawPolyline(m.points, col, true); break;
                    case MeasureKind.TargetRefPoint:   DrawCross(m.points[0], 2.4f, col); break;
                    case MeasureKind.BattlePosition:   DrawDiamond(m.points[0], 4f, col); break;
                }
            }
            GL.PopMatrix();
        }

        private static void DrawPolyline(Vector3[] pts, Color c, bool closed)
        {
            GL.Begin(GL.LINE_STRIP);
            GL.Color(c);
            for (int i = 0; i < pts.Length; i++) GL.Vertex(pts[i]);
            if (closed && pts.Length > 0) GL.Vertex(pts[0]);
            GL.End();
        }

        private static void DrawDashed(Vector3[] pts, Color c)
        {
            GL.Begin(GL.LINES);
            GL.Color(c);
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                Vector3 d = b - a;
                float len = d.magnitude;
                int dashes = Mathf.Max(1, Mathf.RoundToInt(len / 3f));
                for (int j = 0; j < dashes; j++)
                {
                    if ((j & 1) == 1) continue; // skip every other slot = dashes
                    Vector3 p0 = a + d * (j      / (float)dashes);
                    Vector3 p1 = a + d * ((j + 1) / (float)dashes);
                    GL.Vertex(p0); GL.Vertex(p1);
                }
            }
            GL.End();
        }

        private static void DrawCross(Vector3 p, float arm, Color c)
        {
            GL.Begin(GL.LINES);
            GL.Color(c);
            GL.Vertex(p + Vector3.left  * arm); GL.Vertex(p + Vector3.right * arm);
            GL.Vertex(p + Vector3.forward * arm); GL.Vertex(p + Vector3.back  * arm);
            GL.End();
        }

        private static void DrawDiamond(Vector3 p, float r, Color c)
        {
            GL.Begin(GL.LINE_STRIP);
            GL.Color(c);
            GL.Vertex(p + Vector3.forward * r);
            GL.Vertex(p + Vector3.right   * r);
            GL.Vertex(p + Vector3.back    * r);
            GL.Vertex(p + Vector3.left    * r);
            GL.Vertex(p + Vector3.forward * r);
            GL.End();
        }

        private static Color ColorFor(MeasureKind k) => k switch
        {
            MeasureKind.PhaseLine        => new Color(0.85f, 0.85f, 0.85f, 0.85f),
            MeasureKind.Boundary         => new Color(1f, 1f, 1f, 0.80f),
            MeasureKind.NoFireArea       => new Color(1f, 0.30f, 0.30f, 0.90f),
            MeasureKind.RestrictedFire   => new Color(1f, 0.78f, 0.20f, 0.85f),
            MeasureKind.FreeFire         => new Color(0.20f, 1.00f, 0.50f, 0.85f),
            MeasureKind.EngagementArea   => new Color(1f, 0.55f, 0.10f, 0.85f),
            MeasureKind.TargetRefPoint   => new Color(0f, 0.85f, 1f, 1f),
            MeasureKind.BattlePosition   => new Color(0f, 1f, 0.55f, 1f),
            _                            => Color.white
        };

        /// <summary>Number of mission measures currently displayed (for HUD readout).</summary>
        public int Count => _measures.Count;
    }
}
