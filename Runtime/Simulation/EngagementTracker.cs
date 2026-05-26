// =====================================================================
//  NATO C2 RTS Hybrid — EngagementTracker.cs
//  ---------------------------------------------------------------------
//  Scans the agent set each tick for friendly-hostile pairs within
//  contact range. Active engagements are rendered as flashing red rings
//  on the ground (distinct from Mythos's forecast threat field) and
//  generate "contact" radio messages on the FIRES net.
//
//  Different from Mythos: Mythos forecasts where threats WILL be in
//  8-12 seconds. EngagementTracker reports where combat is happening
//  RIGHT NOW.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/Engagement Tracker")]
    public class EngagementTracker : MonoBehaviour
    {
        [Header("Detection")]
        [Tooltip("Distance under which a friendly-hostile pair is considered in contact.")]
        [Min(2f)] public float contactRange = 18f;
        [Tooltip("Cool-down before a re-contact between the same two units re-fires a radio call.")]
        [Min(1f)] public float radioCooldown = 6f;

        [Header("Render")]
        public Material lineMaterial;
        public float ringRadius = 12f;
        public float pulseHz    = 3f;

        public readonly List<EngagementEvent> Active = new List<EngagementEvent>(32);

        public struct EngagementEvent
        {
            public Vector3 centre;
            public float startedAt;
            public string friendlyCallsign;
            public string hostileCallsign;
        }

        private struct PairKey : IEquatable<PairKey>
        {
            public int a; public int b;
            public bool Equals(PairKey o) => a == o.a && b == o.b;
            public override int GetHashCode() => (a * 397) ^ b;
        }

        private readonly Dictionary<PairKey, float> _lastRadioByPair = new Dictionary<PairKey, float>(64);

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

        private void Update()
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) return;

            Active.Clear();

            // O(F×H) scan. With F+H typically < 60 this is fine.
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var f = mgr.Agents[i];
                if (f == null || f.affiliation != Affiliation.Friendly) continue;
                for (int j = 0; j < mgr.Agents.Count; j++)
                {
                    var h = mgr.Agents[j];
                    if (h == null || h.affiliation != Affiliation.Hostile) continue;
                    Vector3 d = h.transform.position - f.transform.position;
                    d.y = 0f;
                    float sq = d.sqrMagnitude;
                    if (sq > contactRange * contactRange) continue;

                    // GetEntityId().GetHashCode() returns a stable int per Object instance —
                    // we use it purely as a dictionary key for our pair tracking.
                    var key = new PairKey { a = f.GetEntityId().GetHashCode(), b = h.GetEntityId().GetHashCode() };
                    Active.Add(new EngagementEvent
                    {
                        centre = (f.transform.position + h.transform.position) * 0.5f,
                        startedAt = _lastRadioByPair.TryGetValue(key, out var st) ? st : Time.time,
                        friendlyCallsign = f.callsign,
                        hostileCallsign  = h.callsign
                    });

                    // Throttle the radio call per pair so the operator isn't spammed.
                    float prev;
                    if (!_lastRadioByPair.TryGetValue(key, out prev) || Time.time - prev > radioCooldown)
                    {
                        _lastRadioByPair[key] = Time.time;
                        FeedHub.Instance?.PublishRadio(new RadioMessage
                        {
                            net = "TANGO-6",
                            timestampUtc = DateTime.UtcNow,
                            fromCallsign = f.callsign,
                            text = $"CONTACT — {h.callsign} {SimpleBearing(f.transform.position, h.transform.position)}",
                            severity = RadioSeverity.Critical
                        });
                    }
                }
            }
        }

        private static string SimpleBearing(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from; d.y = 0f;
            float h = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            int idx = Mathf.RoundToInt(h / 45f) % 8;
            return $"{dirs[idx]} {d.magnitude:0}m";
        }

        // ---------- render ----------
        private void OnRenderObject()
        {
            if (lineMaterial == null || Active.Count == 0) return;
            lineMaterial.SetPass(0);
            GL.PushMatrix();
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseHz * 2f * Mathf.PI);
            foreach (var e in Active)
            {
                GL.Begin(GL.LINE_STRIP);
                GL.Color(new Color(1f, 0.15f, 0.15f, 0.65f + pulse * 0.35f));
                int seg = 48;
                for (int i = 0; i <= seg; i++)
                {
                    float t = (i / (float)seg) * Mathf.PI * 2f;
                    GL.Vertex(e.centre + new Vector3(Mathf.Cos(t), 0.08f, Mathf.Sin(t)) * ringRadius);
                }
                GL.End();
            }
            GL.PopMatrix();
        }
    }
}
