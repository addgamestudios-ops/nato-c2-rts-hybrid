// =====================================================================
//  NATO C2 RTS Hybrid — CotTrackPanel.cs
//  ---------------------------------------------------------------------
//  Renders inbound CoT tracks (from TakServerCotAdapter) as world-space
//  markers on the ground. Each track gets:
//      • An affiliation-coloured billboard quad (friendly cyan, hostile
//        red, neutral green, unknown yellow — APP-6E-ish frame palette)
//      • A floating callsign + UID label
//      • A ground decal ring tied to ALT (height above ellipsoid)
//
//  When a CoT event arrives, we update the existing marker (by uid) or
//  spawn a new one. Markers are pruned when their `stale` timestamp
//  passes — same behaviour ATAK / SitaWare implement.
//
//  Filters out events whose uid starts with "NATO-C2-" — those are OUR
//  outbound events echoed back by the server. Without this filter you'd
//  see two markers per friendly, one local and one echoed.
//
//  WGS-84 → world XZ uses the same equirectangular projection as
//  TakServerCotAdapter / LocalSimFeed (originLat/originLon, metres-per-unit).
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(150)]
    [AddComponentMenu("NATO C2/CoT Track Panel")]
    public class CotTrackPanel : MonoBehaviour
    {
        [Header("Projection (must match TakServerCotAdapter)")]
        public double originLat = 38.7400;
        public double originLon = 22.2540;
        public float  metresPerUnit = 50f;

        [Header("Marker")]
        [Tooltip("World-space marker height above ground in metres.")]
        public float markerHeight = 1.2f;
        [Tooltip("Diameter of the marker quad in world units.")]
        public float markerSize = 2.0f;
        [Tooltip("Filter out uids starting with this prefix — those are our own outbound CoT echoed back.")]
        public string ownPrefix = "NATO-C2-";

        // ---------- runtime state ---------------------------------
        private FeedHub _hub;
        private readonly Dictionary<string, TrackMarker> _markers = new Dictionary<string, TrackMarker>(64);
        private Material _markerMat;

        private void Awake()
        {
            // Shared transparent unlit material so all markers render cheaply.
            _markerMat = new Material(Shader.Find("Unlit/Color"));
            _markerMat.color = Color.white;
        }

        private void OnEnable()
        {
            _hub = FeedHub.Instance;
            if (_hub != null) _hub.OnCot += HandleCot;
        }
        private void OnDisable()
        {
            if (_hub != null) _hub.OnCot -= HandleCot;
        }

        private void HandleCot(CotEvent ev)
        {
            if (string.IsNullOrEmpty(ev.uid)) return;
            // Skip echoes of our own outbound events.
            if (!string.IsNullOrEmpty(ownPrefix) && ev.uid.StartsWith(ownPrefix)) return;

            if (!_markers.TryGetValue(ev.uid, out var m))
            {
                m = SpawnMarker(ev);
                _markers[ev.uid] = m;
            }
            UpdateMarker(m, ev);
        }

        // ---------- marker lifecycle ---------------------------------
        //  Symbol dispatch by CoT type:
        //      b-r-f-h-c   → flashing red 5-point STAR  (Call For Fire)
        //      b-r-c-m     → red CROSS                  (MEDEVAC)
        //      b-m-p-w*    → flat green/blue FLAG       (waypoint / LZ)
        //      a-*-*       → existing QUAD              (unit position)
        //
        //  Each shape is a tiny composite of Unity primitives so we can
        //  read it on the OSM ground at a glance — no shaders needed.

        private enum SymbolKind { Quad, Star, Cross, Flag }

        private static SymbolKind KindFor(string cotType)
        {
            if (string.IsNullOrEmpty(cotType)) return SymbolKind.Quad;
            if (cotType.StartsWith("b-r-f"))  return SymbolKind.Star;   // request fires
            if (cotType.StartsWith("b-r-c-m")) return SymbolKind.Cross; // request medical
            if (cotType.StartsWith("b-m-p"))  return SymbolKind.Flag;   // marker point
            return SymbolKind.Quad;
        }

        private TrackMarker SpawnMarker(CotEvent ev)
        {
            var go = new GameObject("CoT_" + ev.uid);
            go.transform.SetParent(transform, false);

            var kind = KindFor(ev.type);
            var color = ColourForCotType(ev.type);
            var matInst = new Material(_markerMat);
            matInst.color = color;

            Transform shape = kind switch
            {
                SymbolKind.Star  => BuildStar (go.transform, matInst),
                SymbolKind.Cross => BuildCross(go.transform, matInst),
                SymbolKind.Flag  => BuildFlag (go.transform, matInst),
                _                => BuildQuad (go.transform, matInst),
            };

            // Floating label.
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2f, 0f);
            var mesh = labelGo.AddComponent<TextMesh>();
            mesh.alignment = TextAlignment.Center;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.characterSize = 0.2f;
            mesh.fontSize = 32;
            mesh.color = color;
            mesh.text = LabelPrefixFor(kind);
            labelGo.AddComponent<Billboard>();

            return new TrackMarker
            {
                root    = go.transform,
                quad    = shape,
                label   = mesh,
                quadMat = matInst,
                kind    = kind
            };
        }

        private Transform BuildQuad(Transform parent, Material mat)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Frame";
            quad.transform.SetParent(parent, false);
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            quad.transform.localScale = new Vector3(markerSize, markerSize, 1f);
            Destroy(quad.GetComponent<Collider>());
            quad.GetComponent<Renderer>().sharedMaterial = mat;
            return quad.transform;
        }

        private Transform BuildStar(Transform parent, Material mat)
        {
            // 5-point star = 5 long thin boxes rotated around Y, lying flat.
            var star = new GameObject("Star");
            star.transform.SetParent(parent, false);
            for (int i = 0; i < 5; i++)
            {
                var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
                arm.name = "Arm" + i;
                arm.transform.SetParent(star.transform, false);
                arm.transform.localScale = new Vector3(0.35f, 0.12f, markerSize * 1.3f);
                arm.transform.localRotation = Quaternion.Euler(0f, i * 72f, 0f);
                Destroy(arm.GetComponent<Collider>());
                arm.GetComponent<Renderer>().sharedMaterial = mat;
            }
            star.AddComponent<FlashPulse>().mat = mat; // flashing red
            return star.transform;
        }

        private Transform BuildCross(Transform parent, Material mat)
        {
            // Red plus sign — two long thin boxes lying flat, perpendicular.
            var cross = new GameObject("Cross");
            cross.transform.SetParent(parent, false);
            for (int i = 0; i < 2; i++)
            {
                var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
                arm.name = "Arm" + i;
                arm.transform.SetParent(cross.transform, false);
                arm.transform.localScale = new Vector3(0.45f, 0.14f, markerSize * 1.4f);
                arm.transform.localRotation = Quaternion.Euler(0f, i * 90f, 0f);
                Destroy(arm.GetComponent<Collider>());
                arm.GetComponent<Renderer>().sharedMaterial = mat;
            }
            return cross.transform;
        }

        private Transform BuildFlag(Transform parent, Material mat)
        {
            // Flag = vertical pole + small rectangular flag at the top.
            var flag = new GameObject("Flag");
            flag.transform.SetParent(parent, false);

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pole.name = "Pole";
            pole.transform.SetParent(flag.transform, false);
            pole.transform.localScale = new Vector3(0.10f, 1.6f, 0.10f);
            pole.transform.localPosition = new Vector3(0f, 0.8f, 0f);
            Destroy(pole.GetComponent<Collider>());
            pole.GetComponent<Renderer>().sharedMaterial = mat;

            var cloth = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cloth.name = "Cloth";
            cloth.transform.SetParent(flag.transform, false);
            cloth.transform.localScale = new Vector3(0.8f, 0.4f, 0.05f);
            cloth.transform.localPosition = new Vector3(0.45f, 1.4f, 0f);
            Destroy(cloth.GetComponent<Collider>());
            cloth.GetComponent<Renderer>().sharedMaterial = mat;
            return flag.transform;
        }

        private static string LabelPrefixFor(SymbolKind k) => k switch
        {
            SymbolKind.Star  => "FIRE MISSION",
            SymbolKind.Cross => "MEDEVAC",
            SymbolKind.Flag  => "MARK",
            _                => "TAK"
        };

        // Cheap shader-free flash: scale UnlitColor material brightness in a sine wave.
        private class FlashPulse : MonoBehaviour
        {
            public Material mat;
            private Color _base;
            private bool _gotBase;
            private void Update()
            {
                if (mat == null) return;
                if (!_gotBase) { _base = mat.color; _gotBase = true; }
                float t = (Mathf.Sin(Time.time * 6f) + 1f) * 0.5f; // 0..1, ~1 Hz
                var c = _base;
                c.a = Mathf.Lerp(0.35f, 1.0f, t);
                mat.color = c;
            }
        }

        private void UpdateMarker(TrackMarker m, CotEvent ev)
        {
            // Project lat/lon → world XZ.
            Vector3 world = LatLonToWorld(ev.latitude, ev.longitude);
            world.y = markerHeight;
            m.root.position = world;
            m.stale = ev.stale;
            m.quadMat.color = ColourForCotType(ev.type);
            m.label.color = m.quadMat.color;

            // Callsign is parsed out of <detail><contact callsign="..."/></detail>.
            string callsign = ExtractCallsign(ev.xmlDetail);
            m.label.text = string.IsNullOrEmpty(callsign)
                ? ev.uid
                : callsign + "\n<color=#888>" + ev.uid + "</color>";
        }

        private void Update()
        {
            // Prune stale markers. CoT events carry their own stale timestamp;
            // once UtcNow passes it we drop the marker.
            DateTime now = DateTime.UtcNow;
            List<string> toRemove = null;
            foreach (var kv in _markers)
            {
                if (kv.Value.stale != default && now > kv.Value.stale)
                {
                    (toRemove ??= new List<string>()).Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var uid in toRemove)
                {
                    if (_markers[uid].root != null) Destroy(_markers[uid].root.gameObject);
                    _markers.Remove(uid);
                }
            }
        }

        // ---------- helpers ---------------------------------------
        private Vector3 LatLonToWorld(double lat, double lon)
        {
            // Inverse of TakServerCotAdapter.WorldToLatLon — equirectangular.
            const double EarthRadiusM = 6_378_137d;
            double dLatDeg = lat - originLat;
            double dLonDeg = lon - originLon;
            double dz = (dLatDeg * Math.PI / 180.0) * EarthRadiusM / metresPerUnit;
            double dx = (dLonDeg * Math.PI / 180.0) * EarthRadiusM *
                        Math.Cos(originLat * Math.PI / 180.0) / metresPerUnit;
            return new Vector3((float)dx, 0f, (float)dz);
        }

        private static Color ColourForCotType(string cotType)
        {
            if (string.IsNullOrEmpty(cotType) || cotType.Length < 3) return new Color(1f, 0.9f, 0.2f); // unknown = yellow
            // a-{f|h|n|u}-…
            char ident = cotType[2];
            return ident switch
            {
                'f' => new Color(0.10f, 0.78f, 1.00f),   // cyan friendly
                'h' => new Color(1.00f, 0.30f, 0.30f),   // red hostile
                'n' => new Color(0.30f, 1.00f, 0.40f),   // green neutral
                _   => new Color(1.00f, 0.92f, 0.20f),   // yellow unknown
            };
        }

        private static string ExtractCallsign(string xmlDetail)
        {
            if (string.IsNullOrEmpty(xmlDetail)) return null;
            int i = xmlDetail.IndexOf("callsign=\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i += "callsign=\"".Length;
            int j = xmlDetail.IndexOf('"', i);
            return j > i ? xmlDetail.Substring(i, j - i) : null;
        }

        // ---------- per-marker bookkeeping ------------------------
        private class TrackMarker
        {
            public Transform   root;
            public Transform   quad;
            public TextMesh    label;
            public Material    quadMat;
            public DateTime    stale;
            public SymbolKind  kind;
        }

        // ---------- billboarding helper ---------------------------
        // Cheap face-the-camera component for the label. Avoids importing
        // TextMeshPro / a billboard shader for this single use.
        private class Billboard : MonoBehaviour
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
