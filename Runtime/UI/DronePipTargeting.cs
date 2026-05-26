// =====================================================================
//  NATO C2 RTS Hybrid — DronePipTargeting.cs
//  ---------------------------------------------------------------------
//  Lets a human operator look at the drone's PIP video feed and ALT+CLICK
//  any pixel to "ping" the real-world coordinates of what the drone is
//  seeing. The ping then offers four CoT-typed actions:
//
//      🎯 STRIKE      → b-r-f-h-c   (Call For Fire)
//      🏥 MEDEVAC     → b-r-c-m     (9-line MEDEVAC)
//      🚁 INSERT LZ   → b-m-p-w-GH  (helo landing zone)
//      📍 MARK        → b-m-p-w     (generic waypoint)
//
//  Each action publishes a CoT event with the WGS-84 lat/lon of the
//  pinged pixel so every connected ATAK client, TAK Server peer, or
//  fires-cell workstation sees the pin on its own map — and can act on
//  it (route artillery, dispatch medevac helo, etc.).
//
//  Math: clicked screen pixel → PIP RectTransform local coords →
//  normalised 0..1 → drone-camera ViewportPointToRay → raycast against
//  the ground (a Y=0 plane fallback if no MeshCollider hit) → world XZ.
//
//  Why this matters operationally:
//      A soldier with eyes on the screen + a drone overhead is the same
//      sensor-shooter loop used by USMC + USAF JTACs. Real systems
//      (ATAK Targeting plugin, Persistent Systems MPU5, FalconView)
//      do exactly this — pick a pixel, get a grid, push to fires.
//      We're not simulating; we're emitting the same CoT events those
//      systems consume.
// =====================================================================

using System;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [RequireComponent(typeof(DronePipPanel))]
    [AddComponentMenu("NATO C2/Drone PIP Targeting")]
    public class DronePipTargeting : MonoBehaviour
    {
        [Header("Targeting")]
        [Tooltip("Y-plane (world units) we raycast against if no MeshCollider hit. 0 = ground.")]
        public float groundPlaneY = 0f;
        [Tooltip("Lifetime of the world-space ping marker (seconds).")]
        public float pingLifetime = 12f;
        [Tooltip("Optional crosshair overlay rendered over the PIP — set to false if you want a clean feed.")]
        public bool showCrosshair = true;

        [Header("Origin (must match TAK adapter)")]
        public double originLat = 38.7400;
        public double originLon = 22.2540;
        public float  metresPerUnit = 50f;

        // ---------- runtime ------------------------------------------
        private DronePipPanel _pip;
        private RectTransform _videoRect;
        private TakServerCotAdapter _tak;
        private FeedHub _hub;
        private Canvas _canvas;
        private GameObject _actionPanel;
        private Vector3 _pendingWorldHit;
        private Image _crosshair;

        private void Awake()
        {
            _pip = GetComponent<DronePipPanel>();
        }

        private void Start()
        {
            _hub = FeedHub.Instance;
            _tak = FindAnyObjectByType<TakServerCotAdapter>();
            _canvas = GetComponentInParent<Canvas>();
            if (_pip.VideoImage != null) _videoRect = _pip.VideoImage.rectTransform;
            BuildCrosshair();
            BuildActionPanel();
        }

        private void Update()
        {
            if (_pip == null || _pip.VideoImage == null || _pip.DroneCamera == null) return;

            // Crosshair / action-panel reflect targeting eligibility
            bool active = _pip.ActiveDrone != null;
            if (_crosshair != null) _crosshair.enabled = active && showCrosshair;

            // Two triggers, both gated on click being INSIDE the PIP video rect:
            //   • Alt + left-click  → ping (keeps left-click free to cycle drones)
            //   • Right-click       → ping (universal fallback for any input system)
            bool altLmb = Input.GetMouseButtonDown(0)
                          && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
            bool rmb    = Input.GetMouseButtonDown(1);
            if (active && (altLmb || rmb))
            {
                TryPingClick();
            }

            // Escape closes the action panel.
            if (_actionPanel != null && _actionPanel.activeSelf && Input.GetKeyDown(KeyCode.Escape))
                CloseActionPanel();
        }

        // ---------- click → world ------------------------------------
        private void TryPingClick()
        {
            if (_videoRect == null) return;

            // 1. Screen point → local point in the PIP RawImage rect.
            Vector2 screenPos = Input.mousePosition;
            Camera uiCam = (_canvas != null && _canvas.renderMode == RenderMode.ScreenSpaceCamera)
                            ? _canvas.worldCamera : null;
            if (!RectTransformUtility.RectangleContainsScreenPoint(_videoRect, screenPos, uiCam))
                return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_videoRect, screenPos, uiCam,
                                                                    out Vector2 local);

            // 2. Local point → normalised 0..1 within the video rect.
            Rect r = _videoRect.rect;
            float u = Mathf.InverseLerp(r.xMin, r.xMax, local.x);
            float v = Mathf.InverseLerp(r.yMin, r.yMax, local.y);

            // 3. Drone camera ray.
            Ray ray = _pip.DroneCamera.ViewportPointToRay(new Vector3(u, v, 0f));

            // 4. Raycast — prefer ground colliders, fall back to math plane.
            Vector3 world;
            if (Physics.Raycast(ray, out var hit, 5000f))
            {
                world = hit.point;
            }
            else
            {
                // Plane y = groundPlaneY
                float denom = ray.direction.y;
                if (Mathf.Abs(denom) < 1e-4f) return; // parallel — can't hit
                float t = (groundPlaneY - ray.origin.y) / denom;
                if (t < 0f) return; // behind camera
                world = ray.origin + ray.direction * t;
            }

            _pendingWorldHit = world;
            ShowPing(world);
            ShowActionPanel(screenPos);

            // Log to radio so the operator knows the drone painted a target.
            var drone = _pip.ActiveDrone;
            string cs = drone != null ? drone.callsign : "DRONE";
            _hub?.PublishRadio(new RadioMessage
            {
                net = "TANGO-6",
                timestampUtc = DateTime.UtcNow,
                fromCallsign = cs,
                text = $"<color=#6cf>painting</color> grid {WorldToMgrsish(world)} — choose action",
                severity = RadioSeverity.System
            });
        }

        // ---------- world ping marker --------------------------------
        private void ShowPing(Vector3 world)
        {
            // Quick disposable visual: a glowing cylinder + text label.
            var go = new GameObject("DronePing");
            go.transform.position = world + Vector3.up * 0.1f;
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            Destroy(ring.GetComponent<Collider>());
            ring.transform.SetParent(go.transform, false);
            ring.transform.localScale = new Vector3(4f, 0.05f, 4f);
            var mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = new Color(0.10f, 0.95f, 1.00f, 1f);
            ring.GetComponent<Renderer>().sharedMaterial = mat;
            Destroy(go, pingLifetime);
        }

        // ---------- action panel UI ----------------------------------
        private void BuildActionPanel()
        {
            if (_canvas == null) return;
            _actionPanel = new GameObject("DronePingActions",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _actionPanel.transform.SetParent(transform, false);
            var rt = _actionPanel.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(260f, 240f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            var bg = _actionPanel.GetComponent<Image>();
            bg.color = new Color(0.04f, 0.08f, 0.15f, 0.95f);
            bg.raycastTarget = true;

            // Header: 28 px tall, top-anchored inside the 240 px panel.
            // Buttons: 36 px tall, 240 px wide, stacked with 8 px gaps.
            var title = MakeButton(_actionPanel.transform, "DRONE TARGET",
                new Vector2(0, 92), new Vector2(240, 28),
                colour: new Color(0.10f, 0.95f, 1.00f, 1f), bold: true);
            title.GetComponent<Button>().enabled = false;
            title.GetComponentInChildren<Text>().fontSize = 16;
            title.GetComponentInChildren<Text>().alignment = TextAnchor.MiddleCenter;

            MakeAction(_actionPanel.transform, "🎯 STRIKE",
                       new Vector2(0,  44), new Color(1f, 0.35f, 0.30f), DoStrike);
            MakeAction(_actionPanel.transform, "🏥 MEDEVAC",
                       new Vector2(0,   0), new Color(0.95f, 0.60f, 0.20f), DoMedevac);
            MakeAction(_actionPanel.transform, "🚁 INSERT LZ",
                       new Vector2(0, -44), new Color(0.30f, 1.00f, 0.55f), DoInsert);
            MakeAction(_actionPanel.transform, "📍 MARK / CANCEL",
                       new Vector2(0, -88), new Color(0.40f, 0.55f, 0.70f), DoMark);

            _actionPanel.SetActive(false);
        }

        private void ShowActionPanel(Vector2 screenPos)
        {
            if (_actionPanel == null) return;
            _actionPanel.SetActive(true);
            // Place the panel above-right of click but clamped to viewport.
            var rt = _actionPanel.GetComponent<RectTransform>();
            float w = Screen.width, h = Screen.height;
            float scale = _canvas != null ? _canvas.scaleFactor : 1f;
            if (scale <= 0f) scale = 1f;
            Vector2 anchored = (screenPos + new Vector2(110f, 90f)) / scale;
            // Clamp inside canvas
            anchored.x = Mathf.Min(anchored.x, (w / scale) - 100f);
            anchored.y = Mathf.Min(anchored.y, (h / scale) -  90f);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.anchoredPosition = anchored;
        }

        private void CloseActionPanel()
        {
            if (_actionPanel != null) _actionPanel.SetActive(false);
        }

        // ---------- action handlers ----------------------------------
        private void DoStrike()
        {
            var drone = _pip.ActiveDrone;
            string cs = drone != null ? drone.callsign : "DRONE";
            _tak?.PublishCallForFire(_pendingWorldHit, requester: cs,
                                     remarks: $"Painted by drone {cs} via PIP feed");
            _hub?.PublishRadio(new RadioMessage
            {
                net = "TANGO-6", timestampUtc = DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"<color=#f63>FIRE MISSION</color> requested from {cs} at {WorldToMgrsish(_pendingWorldHit)} — CoT b-r-f-h-c emitted",
                severity = RadioSeverity.Warning
            });
            CloseActionPanel();
        }

        private void DoMedevac()
        {
            var drone = _pip.ActiveDrone;
            string cs = drone != null ? drone.callsign : "DRONE";
            _tak?.PublishMedevac(_pendingWorldHit, requester: cs,
                                 patientCallsign: "", precedence: 'A',
                                 patientsLitter: 1, patientsAmbulatory: 0,
                                 remarks: $"Painted by drone {cs} via PIP feed");
            _hub?.PublishRadio(new RadioMessage
            {
                net = "MEDEVAC", timestampUtc = DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"<color=#fc6>MEDEVAC</color> requested from {cs} at {WorldToMgrsish(_pendingWorldHit)} — CoT b-r-c-m emitted (precedence A)",
                severity = RadioSeverity.Warning
            });
            CloseActionPanel();
        }

        private void DoInsert()
        {
            var drone = _pip.ActiveDrone;
            string cs = drone != null ? drone.callsign : "DRONE";
            // b-m-p-w-GH (-GH = generic helo LZ subtype in CoT taxonomy)
            _tak?.PublishMarker(_pendingWorldHit, label: $"LZ-{cs}", cotType: "b-m-p-w-GH");
            _hub?.PublishRadio(new RadioMessage
            {
                net = "TANGO-6", timestampUtc = DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"<color=#6f9>INSERT LZ</color> from {cs} at {WorldToMgrsish(_pendingWorldHit)} — CoT b-m-p-w-GH emitted",
                severity = RadioSeverity.System
            });
            CloseActionPanel();
        }

        private void DoMark()
        {
            var drone = _pip.ActiveDrone;
            string cs = drone != null ? drone.callsign : "DRONE";
            _tak?.PublishMarker(_pendingWorldHit, label: $"MARK-{cs}", cotType: "b-m-p-w");
            _hub?.PublishRadio(new RadioMessage
            {
                net = "TANGO-6", timestampUtc = DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"mark from {cs} at {WorldToMgrsish(_pendingWorldHit)}",
                severity = RadioSeverity.System
            });
            CloseActionPanel();
        }

        // ---------- crosshair overlay --------------------------------
        private void BuildCrosshair()
        {
            if (_videoRect == null) return;
            var go = new GameObject("Crosshair", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_videoRect, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(16f, 16f);
            rt.anchoredPosition = Vector2.zero;
            _crosshair = go.GetComponent<Image>();
            _crosshair.color = new Color(0.10f, 0.95f, 1.00f, 0.55f);
            _crosshair.raycastTarget = false;
            _crosshair.sprite = WhiteSprite();
        }

        // ---------- helpers ------------------------------------------
        private static Sprite _whiteSprite;
        private static Sprite WhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = new Texture2D(2, 2);
            var px = new Color[4]; for (int i = 0; i < 4; i++) px[i] = Color.white;
            tex.SetPixels(px); tex.Apply();
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
            return _whiteSprite;
        }

        private Button MakeButton(Transform parent, string text, Vector2 pos, Vector2 size,
                                  Color colour, bool bold = false)
        {
            var go = new GameObject(text, typeof(RectTransform), typeof(CanvasRenderer),
                                    typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            var lblRt = labelGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = lblRt.offsetMax = Vector2.zero;
            var t = labelGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text;
            t.fontSize = 13;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.color = colour;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        private void MakeAction(Transform parent, string text, Vector2 pos, Color colour, Action onClick)
        {
            var btn = MakeButton(parent, text, pos, new Vector2(240, 36), colour, bold: true);
            btn.GetComponentInChildren<Text>().fontSize = 16;
            // Subtle border via Image colour.
            btn.GetComponent<Image>().color = new Color(colour.r, colour.g, colour.b, 0.25f);
            btn.onClick.AddListener(() => { try { onClick?.Invoke(); } catch (Exception e) { Debug.LogException(e); } });
        }

        private string WorldToMgrsish(Vector3 world)
        {
            // Quick "MGRS-ish" stamp — same projection as elsewhere, formatted as
            // a 10-digit easting/northing pair. Real MGRS requires a full WGS84
            // band/square lookup; this is the demo abbreviation.
            var (lat, lon) = WorldToLatLon(world);
            return $"{lat:F5},{lon:F5}";
        }

        private (double lat, double lon) WorldToLatLon(Vector3 world)
        {
            const double EarthRadiusM = 6_378_137d;
            double dx = world.x * metresPerUnit;
            double dz = world.z * metresPerUnit;
            double lat = originLat + (dz / EarthRadiusM) * (180d / Math.PI);
            double lon = originLon + (dx / EarthRadiusM) * (180d / Math.PI) /
                                     Math.Cos(originLat * Math.PI / 180d);
            return (lat, lon);
        }
    }
}
