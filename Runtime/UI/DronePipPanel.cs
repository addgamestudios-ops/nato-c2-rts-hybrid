// =====================================================================
//  NATO C2 RTS Hybrid — DronePipPanel.cs
//  ---------------------------------------------------------------------
//  Picture-in-picture window showing what one of the friendly drones is
//  "seeing" via a second Camera rendered to a RenderTexture.
//  Mimics what a STANAG 4609 / MISB 0601 EO/IR feed looks like in a real
//  C2 system: live video plus a KLV-style metadata overlay with sensor
//  Az/El, FOV, platform altitude, target lat/lon, and UTC time.
//
//  Auto-builds at runtime. Toggle visibility with the V key.
//  Click anywhere on the panel to cycle to the next friendly drone.
//
//  In production this is the seam where SimulatedStanag4609Adapter is
//  replaced with a real STANAG 4609 RTP/UDP demuxer + KLV parser.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(118)]
    [AddComponentMenu("NATO C2/Drone PIP Panel")]
    public class DronePipPanel : MonoBehaviour, IPointerClickHandler
    {
        [Header("Layout")]
        [Min(220)] public int panelWidth  = 320;
        [Min(140)] public int panelHeight = 200;
        public float leftMargin = 16f;
        public float topMargin  = 80f;

        [Header("Toggle")]
        public KeyCode toggleKey = KeyCode.V;
        public bool startVisible = true;

        [Header("Camera")]
        [Range(8f, 90f)] public float sensorHFovDeg = 25f;
        [Tooltip("Gimbal pitch downward from horizontal, degrees.")]
        [Range(0f, 89f)] public float gimbalPitchDeg = 55f;
        [Tooltip("Slight follow offset behind the drone (world units).")]
        public Vector3 followOffset = new Vector3(0f, 0.6f, -1.5f);

        // ---------- private state ---------------------------------------
        private RectTransform _root;
        private CanvasGroup   _cg;
        private RawImage      _videoImage;
        private Text          _callsignText;
        private Text          _altText;
        private Text          _gimbalText;
        private Text          _mgrsText;
        private Text          _clockText;
        private Image         _recDot;
        private Camera        _dronCam;
        private RenderTexture _rt;
        private int  _droneIndex = 0;
        private bool _visible;
        private Agent _trackedDrone;

        /// <summary>The drone camera — exposed so targeting code can build world rays from PIP clicks.</summary>
        public Camera DroneCamera => _dronCam;
        /// <summary>The RawImage rendering the drone feed — exposed for hit-tests in PIP rect space.</summary>
        public RawImage VideoImage => _videoImage;
        /// <summary>Currently tracked drone Agent (null if no drones in scene).</summary>
        public Agent ActiveDrone => _trackedDrone;

        private void Awake()
        {
            BuildUi();
            BuildCamera();
            _visible = startVisible;
            ApplyVisibility();
        }

        private void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
            if (_dronCam != null) Object.Destroy(_dronCam.gameObject);
        }

        // =================================================================
        //  Tick — pick the active drone, drive the camera, refresh overlay.
        // =================================================================
        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                _visible = !_visible;
                ApplyVisibility();
            }
            if (!_visible) return;

            EnsureTrackedDrone();
            DriveCamera();
            RefreshOverlay();
        }

        private void ApplyVisibility()
        {
            if (_root != null) _root.gameObject.SetActive(_visible);
            if (_cg != null) _cg.alpha = _visible ? 1f : 0f;
            if (_dronCam != null) _dronCam.enabled = _visible;
        }

        private void EnsureTrackedDrone()
        {
            var mgr = NATO_C2_Manager.Instance;
            if (mgr == null) { _trackedDrone = null; return; }
            // Build the rolling list of friendly drones.
            var drones = new List<Agent>(8);
            foreach (var a in mgr.Agents)
            {
                if (a == null) continue;
                if (a.affiliation != Affiliation.Friendly) continue;
                if (a.unitType   != UnitType.Drone)        continue;
                drones.Add(a);
            }
            if (drones.Count == 0) { _trackedDrone = null; return; }
            _droneIndex = ((_droneIndex % drones.Count) + drones.Count) % drones.Count;
            _trackedDrone = drones[_droneIndex];
        }

        private void DriveCamera()
        {
            if (_dronCam == null || _trackedDrone == null) return;
            // Place the camera above & slightly behind the drone, pitched
            // forward-down by gimbalPitchDeg. The drone's facing drives
            // azimuth; gimbal pitch is fixed (could be slewed later).
            var dt = _trackedDrone.transform;
            float yaw = dt.rotation.eulerAngles.y;
            _dronCam.transform.position = dt.position
                + (dt.right * followOffset.x)
                + (Vector3.up * followOffset.y)
                + (dt.forward * followOffset.z);
            _dronCam.transform.rotation = Quaternion.Euler(gimbalPitchDeg, yaw, 0f);
            _dronCam.fieldOfView = sensorHFovDeg;
        }

        private void RefreshOverlay()
        {
            if (_trackedDrone == null) return;
            var a = _trackedDrone;
            if (_callsignText != null) _callsignText.text = $"UAV  {a.callsign}";
            if (_altText != null)
                _altText.text = $"ALT  {Mathf.RoundToInt(a.transform.position.y * 10f)}m  ·  HFOV  {sensorHFovDeg:0}°";
            if (_gimbalText != null)
            {
                float az = a.desiredFacing == Vector3.zero ? 0f : Mathf.Atan2(a.desiredFacing.x, a.desiredFacing.z) * Mathf.Rad2Deg;
                if (az < 0f) az += 360f;
                _gimbalText.text = $"AZ  {Mathf.RoundToInt(az):000}°   EL  -{gimbalPitchDeg:0}°";
            }
            if (_mgrsText != null)
            {
                int east  = Mathf.RoundToInt((a.transform.position.x + 1000f) * 10f);
                int north = Mathf.RoundToInt((a.transform.position.z + 1000f) * 10f);
                _mgrsText.text = $"MGRS  33TWN {east:D5} {north:D5}";
            }
            if (_clockText != null) _clockText.text = System.DateTime.UtcNow.ToString("HH:mm:ss") + "Z";

            // Animate the REC dot.
            if (_recDot != null)
            {
                var c = _recDot.color;
                c.a = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(Time.time * 2.5f));
                _recDot.color = c;
            }
        }

        // =================================================================
        //  IPointerClickHandler — click panel to cycle drones.
        // =================================================================
        public void OnPointerClick(PointerEventData eventData)
        {
            // Alt+click is reserved for the DronePipTargeting overlay
            // (it handles the click itself; we just don't want to ALSO
            // cycle drones in that case).
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) return;
            _droneIndex++;
            EnsureTrackedDrone();
        }

        // =================================================================
        //  Build camera + RenderTexture
        // =================================================================
        private void BuildCamera()
        {
            _rt = new RenderTexture(panelWidth, panelHeight, 16, RenderTextureFormat.ARGB32)
            {
                name = "DronePipRT",
                antiAliasing = 2,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();

            var camGo = new GameObject("DronePipCamera", typeof(Camera));
            camGo.transform.SetParent(transform, false);
            _dronCam = camGo.GetComponent<Camera>();
            _dronCam.clearFlags = CameraClearFlags.SolidColor;
            _dronCam.backgroundColor = new Color(0.025f, 0.06f, 0.11f);
            _dronCam.targetTexture = _rt;
            _dronCam.fieldOfView = sensorHFovDeg;
            _dronCam.farClipPlane = 500f;
            _dronCam.nearClipPlane = 0.1f;
            _dronCam.depth = -10; // render before main camera
            // Don't render UI in the drone feed.
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) _dronCam.cullingMask &= ~(1 << uiLayer);

            if (_videoImage != null) _videoImage.texture = _rt;
        }

        // =================================================================
        //  Build the UI panel
        // =================================================================
        private void BuildUi()
        {
            var parent = transform;
            var go = new GameObject("DronePipPanel",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0, 1); _root.anchorMax = new Vector2(0, 1);
            _root.pivot = new Vector2(0, 1);
            _root.sizeDelta = new Vector2(panelWidth, panelHeight + 30); // +30 for header strip
            _root.anchoredPosition = new Vector2(leftMargin, -topMargin);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.02f, 0.04f, 0.07f, 0.95f);
            bg.raycastTarget = true; // catch clicks for cycle
            _cg = go.GetComponent<CanvasGroup>();

            // Header strip (callsign + REC dot + UTC clock).
            var header = MakeImage(_root, "Header", new Color(0.06f, 0.13f, 0.22f, 1f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -22), Vector2.zero);
            header.raycastTarget = false;

            _recDot = MakeImage(_root, "Rec", NATOPalette.HostileRed,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -16), new Vector2(16, -6));
            _recDot.raycastTarget = false;

            _callsignText = MakeText(_root, "Cs", "UAV", 12, FontStyle.Bold, NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(22, -22), new Vector2(-100, -2));

            _clockText = MakeText(_root, "Clk", "00:00:00Z", 11, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(-100, -22), new Vector2(-8, -2));
            _clockText.alignment = TextAnchor.MiddleRight;

            // Live video frame.
            var vidGo = new GameObject("Video", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            vidGo.transform.SetParent(_root, false);
            _videoImage = vidGo.GetComponent<RawImage>();
            _videoImage.color = Color.white;
            _videoImage.raycastTarget = false;
            var vRt = vidGo.GetComponent<RectTransform>();
            vRt.anchorMin = new Vector2(0, 0); vRt.anchorMax = new Vector2(1, 1);
            vRt.offsetMin = new Vector2(4, 32); vRt.offsetMax = new Vector2(-4, -26);

            // Crosshair (centre)
            var cross = MakeImage(_root, "CrossH", new Color(0f, 1f, 0.55f, 0.7f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-12, 0), new Vector2(12, 1));
            cross.raycastTarget = false;
            var crossV = MakeImage(_root, "CrossV", new Color(0f, 1f, 0.55f, 0.7f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -12), new Vector2(1, 12));
            crossV.raycastTarget = false;

            // Footer overlay rows (Az/El, ALT/FOV, MGRS).
            _gimbalText = MakeText(_root, "Gim", "AZ 000° EL -55°", 10, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(8, 4), new Vector2(-4, 18));
            _altText = MakeText(_root, "Alt", "ALT 0m  HFOV 25°", 10, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0.5f, 0), new Vector2(1, 0), new Vector2(4, 4), new Vector2(-8, 18));
            _altText.alignment = TextAnchor.MiddleRight;

            _mgrsText = MakeText(_root, "Mgrs", "MGRS  —", 10, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(8, 16), new Vector2(-8, 30));
            _mgrsText.alignment = TextAnchor.MiddleCenter;
        }

        private Text MakeText(Transform parent, string name, string text, int size, FontStyle style, Color colour,
                              Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = colour;
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return t;
        }

        private Image MakeImage(Transform parent, string name, Color c, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return img;
        }
    }
}
