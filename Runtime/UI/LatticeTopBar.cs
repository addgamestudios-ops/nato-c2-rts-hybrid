// =====================================================================
//  NATO C2 RTS Hybrid — LatticeTopBar.cs
//  ---------------------------------------------------------------------
//  Anduril-Lattice-inspired top navigation strip.  Layout (left→right):
//
//      [▲] Lattice  |  ⌖ Tracks  ⬡ Assets  ◴ Layers  🔍 Search  ⚙ Settings
//                                       ▎ Mission: {mission}  ▾  📡  ✈ Asset View
//                                                                ✉ ops@…
//                                                          ⊕ {lat°, lon°} [Go To]
//                                                                            🔴 LIVE
//
//  Visual rules cribbed from the Lattice screenshots:
//    • Background        — deep navy `#0B1430` with a 1-px bottom border
//    • Active icon       — cyan `#10E0FF`
//    • Inactive icon     — soft slate `#A6B3C9`
//    • Mission ribbon    — slightly lighter navy pill with a left vertical
//                          accent line; ribbon icon ◴ in cyan
//    • Live pill         — green `#3FCE6E` rectangle with white timestamp
//    • Coord widget      — slate text + a small "Go To" button (cyan border)
//    • Asset View        — airplane icon, opens the Asset View panel
//
//  Everything is built with legacy uGUI so we don't depend on TextMeshPro.
//  Icons are rendered with single-character Unicode (sufficient for the
//  monochrome look). Click handlers point at our existing panels by name.
//
//  This panel REPLACES the previous TacticalHUD top status text by sitting
//  on top of it at a higher sort-order. No edits needed to existing code.
// =====================================================================

using System;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(115)]
    [AddComponentMenu("NATO C2/Lattice Top Bar")]
    public class LatticeTopBar : MonoBehaviour
    {
        [Header("Mission")]
        public string missionName = "OP THUNDERSTRIKE";
        public string opsEmail    = "usmc-support@anduril.com";

        [Header("Layout")]
        public int barHeight = 56;

        // Palette — mirrors Anduril Lattice (eyeballed from screenshots).
        private static readonly Color NavyBg     = new Color(0.043f, 0.078f, 0.188f, 0.95f);
        private static readonly Color NavyPill   = new Color(0.070f, 0.118f, 0.235f, 1.00f);
        private static readonly Color Cyan       = new Color(0.063f, 0.878f, 1.000f, 1.00f);
        private static readonly Color Slate      = new Color(0.65f, 0.70f, 0.79f, 1.00f);
        private static readonly Color Green      = new Color(0.247f, 0.808f, 0.431f, 1.00f);
        private static readonly Color White      = Color.white;

        // ---------- runtime state ------------------------------------
        private RectTransform _root;
        private Text _coordText;
        private Text _liveTime;
        private RectTransform _missionRibbon;

        private void Awake()
        {
            BuildBar();
        }

        private void Update()
        {
            // Live UTC clock in the green pill.
            if (_liveTime != null)
                _liveTime.text = DateTime.UtcNow.ToString("HH:mm:ss") + " Z";

            // Camera-center coordinates from the main camera looking down.
            if (_coordText != null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var ray = cam.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
                    if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float t))
                    {
                        Vector3 world = ray.GetPoint(t);
                        var (lat, lon) = WorldToLatLon(world);
                        _coordText.text = $"⊕ {lat:F4}°, {lon:F4}°";
                    }
                }
            }
        }

        // ---------- builders -----------------------------------------
        private void BuildBar()
        {
            var go = new GameObject("LatticeTopBar",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0f, 1f);
            _root.anchorMax = new Vector2(1f, 1f);
            _root.pivot     = new Vector2(0.5f, 1f);
            _root.anchoredPosition = Vector2.zero;
            _root.sizeDelta = new Vector2(0f, barHeight);
            go.GetComponent<Image>().color = NavyBg;

            // 1-pixel cyan bottom border.
            var bord = new GameObject("Border",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bord.transform.SetParent(_root, false);
            var brt = bord.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(1f, 0f);
            brt.sizeDelta = new Vector2(0f, 1f);
            bord.GetComponent<Image>().color = new Color(Cyan.r, Cyan.g, Cyan.b, 0.4f);

            // ▲ Lattice brand
            MakeIconLabel(_root, "Lattice",    "▲", isBrand: true, x: 20f, w: 110f);

            // Main nav buttons. Tracks toggles the LatticeTracksPanel.
            // Layers cycles through basemap styles. Others are stubs for now.
            var tracksLbl = MakeIconLabel(_root, "Tracks",     "⌖", x: 150f);
            var tracksBtn = tracksLbl.transform.parent.GetComponent<Button>();
            if (tracksBtn != null) tracksBtn.onClick.AddListener(ToggleTracksPanel);

            MakeIconLabel(_root, "Assets",     "⬢", x: 250f);

            var layersLbl = MakeIconLabel(_root, "Layers",     "▤", x: 340f);
            var layersBtn = layersLbl.transform.parent.GetComponent<Button>();
            if (layersBtn != null) layersBtn.onClick.AddListener(CycleBasemap);

            MakeIconLabel(_root, "Search",     "🔍", x: 430f);
            var settingsLbl = MakeIconLabel(_root, "Settings",   "⚙", x: 540f);
            var settingsBtn = settingsLbl.transform.parent.GetComponent<Button>();
            if (settingsBtn != null) settingsBtn.onClick.AddListener(ToggleSettings);

            // Mission ribbon (pill with a left accent line).
            BuildMissionRibbon(_root, x: 660f, w: 260f);

            // 📡 small radio icon between ribbon and Asset View.
            MakeIconOnly(_root, "📡", x: 940f, color: Cyan);

            // ✈ Asset View button.
            var assetViewLabel = MakeIconLabel(_root, "Asset View", "✈", x: 980f, w: 140f);
            // Hook the click on the parent button.
            var assetBtn = assetViewLabel.transform.parent.GetComponent<Button>();
            if (assetBtn != null) assetBtn.onClick.AddListener(ToggleAssetView);

            // Live pill (top-center) — built last so it draws over the
            // mission ribbon if they overlap on narrow screens.
            BuildLivePill();

            // Right-side: coord widget + ops email pill (anchored to right edge).
            BuildCoordWidget();
            BuildOpsEmail();
        }

        private Text MakeIconLabel(RectTransform parent, string label, string icon,
                                   bool isBrand = false, float x = 0f, float w = 90f)
        {
            var go = new GameObject(label,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot     = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, 36f);
            rt.anchoredPosition = new Vector2(x, 0f);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.0f);

            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            iconGo.transform.SetParent(go.transform, false);
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot     = new Vector2(0f, 0.5f);
            iconRt.sizeDelta = new Vector2(24f, 24f);
            iconRt.anchoredPosition = new Vector2(0f, 0f);
            var iconT = iconGo.GetComponent<Text>();
            iconT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            iconT.text = icon;
            iconT.fontSize = 20;
            iconT.color = isBrand ? White : Cyan;
            iconT.alignment = TextAnchor.MiddleCenter;
            iconT.raycastTarget = false;

            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            lblGo.transform.SetParent(go.transform, false);
            var lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = new Vector2(0f, 0f);
            lblRt.anchorMax = new Vector2(1f, 1f);
            lblRt.offsetMin = new Vector2(30f, 0f);
            lblRt.offsetMax = new Vector2(-4f, 0f);
            var lblT = lblGo.GetComponent<Text>();
            lblT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            lblT.text = label;
            lblT.fontSize = 15;
            lblT.fontStyle = isBrand ? FontStyle.Bold : FontStyle.Normal;
            lblT.color = isBrand ? White : Slate;
            lblT.alignment = TextAnchor.MiddleLeft;
            lblT.raycastTarget = false;
            return lblT;
        }

        private void MakeIconOnly(RectTransform parent, string icon, float x, Color color)
        {
            var go = new GameObject("Icon_" + icon, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot     = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(28f, 28f);
            rt.anchoredPosition = new Vector2(x, 0f);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = icon; t.fontSize = 20; t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
        }

        private void BuildMissionRibbon(RectTransform parent, float x, float w)
        {
            var go = new GameObject("MissionRibbon",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            _missionRibbon = go.GetComponent<RectTransform>();
            _missionRibbon.anchorMin = new Vector2(0f, 0.5f);
            _missionRibbon.anchorMax = new Vector2(0f, 0.5f);
            _missionRibbon.pivot     = new Vector2(0f, 0.5f);
            _missionRibbon.sizeDelta = new Vector2(w, 38f);
            _missionRibbon.anchoredPosition = new Vector2(x, 0f);
            go.GetComponent<Image>().color = NavyPill;

            // Left accent strip (1 px cyan).
            var accent = new GameObject("Accent",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            accent.transform.SetParent(go.transform, false);
            var ar = accent.GetComponent<RectTransform>();
            ar.anchorMin = new Vector2(0f, 0f); ar.anchorMax = new Vector2(0f, 1f);
            ar.pivot = new Vector2(0f, 0.5f);
            ar.sizeDelta = new Vector2(2f, 0f);
            accent.GetComponent<Image>().color = Cyan;

            // ⌖ ribbon icon — looks like an objective marker.
            var icon = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            icon.transform.SetParent(go.transform, false);
            var iRt = icon.GetComponent<RectTransform>();
            iRt.anchorMin = new Vector2(0f, 0.5f); iRt.anchorMax = new Vector2(0f, 0.5f);
            iRt.pivot = new Vector2(0f, 0.5f);
            iRt.sizeDelta = new Vector2(26f, 26f);
            iRt.anchoredPosition = new Vector2(12f, 0f);
            var iT = icon.GetComponent<Text>();
            iT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            iT.text = "◴"; iT.fontSize = 20; iT.color = Cyan;
            iT.alignment = TextAnchor.MiddleCenter; iT.raycastTarget = false;

            var label = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            label.transform.SetParent(go.transform, false);
            var lRt = label.GetComponent<RectTransform>();
            lRt.anchorMin = new Vector2(0f, 0f); lRt.anchorMax = new Vector2(1f, 1f);
            lRt.offsetMin = new Vector2(42f, 0f); lRt.offsetMax = new Vector2(-22f, 0f);
            var lT = label.GetComponent<Text>();
            lT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            lT.text = "Mission: " + missionName;
            lT.fontSize = 14; lT.color = White; lT.fontStyle = FontStyle.Bold;
            lT.alignment = TextAnchor.MiddleLeft; lT.raycastTarget = false;
            lT.horizontalOverflow = HorizontalWrapMode.Overflow;

            // ▾ dropdown chevron on the right
            var chev = new GameObject("Chevron", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            chev.transform.SetParent(go.transform, false);
            var cRt = chev.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(1f, 0.5f); cRt.anchorMax = new Vector2(1f, 0.5f);
            cRt.pivot = new Vector2(1f, 0.5f);
            cRt.sizeDelta = new Vector2(16f, 16f);
            cRt.anchoredPosition = new Vector2(-8f, 0f);
            var cT = chev.GetComponent<Text>();
            cT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            cT.text = "▾"; cT.fontSize = 14; cT.color = Slate;
            cT.alignment = TextAnchor.MiddleCenter; cT.raycastTarget = false;
        }

        private void BuildLivePill()
        {
            var go = new GameObject("LivePill",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_root, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(132f, 32f);
            rt.anchoredPosition = new Vector2(0f, 0f);
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            // Green badge "Live". A GameObject can only host one Graphic
            // (Image or Text), so the green background and the "Live" label
            // live on separate children — earlier versions of this method
            // tried to stack Image + Text on the same GO, which Unity
            // refused and produced an NRE at scene warmup.
            var liveBg = new GameObject("LiveBg",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            liveBg.transform.SetParent(go.transform, false);
            var lRt = liveBg.GetComponent<RectTransform>();
            lRt.anchorMin = new Vector2(0f, 0f); lRt.anchorMax = new Vector2(0f, 1f);
            lRt.pivot = new Vector2(0f, 0.5f);
            lRt.sizeDelta = new Vector2(44f, 0f);
            lRt.anchoredPosition = new Vector2(2f, 0f);
            liveBg.GetComponent<Image>().color = Green;

            var liveLbl = new GameObject("LiveLbl",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            liveLbl.transform.SetParent(liveBg.transform, false);
            var llRt = liveLbl.GetComponent<RectTransform>();
            llRt.anchorMin = Vector2.zero; llRt.anchorMax = Vector2.one;
            llRt.offsetMin = Vector2.zero; llRt.offsetMax = Vector2.zero;
            var liveTxt = liveLbl.GetComponent<Text>();
            liveTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            liveTxt.text = "Live"; liveTxt.fontSize = 13; liveTxt.color = new Color(0.05f, 0.18f, 0.10f);
            liveTxt.fontStyle = FontStyle.Bold; liveTxt.alignment = TextAnchor.MiddleCenter;
            liveTxt.raycastTarget = false;

            // White timestamp
            var ts = new GameObject("Ts", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ts.transform.SetParent(go.transform, false);
            var tRt = ts.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0f, 0f); tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(50f, 0f); tRt.offsetMax = new Vector2(-6f, 0f);
            _liveTime = ts.GetComponent<Text>();
            _liveTime.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _liveTime.text = "00:00:00 Z";
            _liveTime.fontSize = 14;
            _liveTime.color = White;
            _liveTime.fontStyle = FontStyle.Bold;
            _liveTime.alignment = TextAnchor.MiddleCenter;
            _liveTime.raycastTarget = false;
        }

        private void BuildCoordWidget()
        {
            var go = new GameObject("CoordWidget",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_root, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(260f, 32f);
            rt.anchoredPosition = new Vector2(-310f, 0f);
            go.GetComponent<Image>().color = NavyPill;

            var txt = new GameObject("Coord", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            txt.transform.SetParent(go.transform, false);
            var tRt = txt.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0f, 0f); tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(10f, 0f); tRt.offsetMax = new Vector2(-78f, 0f);
            _coordText = txt.GetComponent<Text>();
            _coordText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _coordText.text = "⊕ —.——°, —.——°";
            _coordText.fontSize = 13;
            _coordText.color = White;
            _coordText.alignment = TextAnchor.MiddleLeft;
            _coordText.raycastTarget = false;

            // Go To button — Image (background + raycast target) on parent,
            // Text label on a child. Same Graphic-on-GO constraint as the
            // LivePill: Image and Text can't share a GameObject.
            var btn = new GameObject("GoTo",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            btn.transform.SetParent(go.transform, false);
            var bRt = btn.GetComponent<RectTransform>();
            bRt.anchorMin = new Vector2(1f, 0f); bRt.anchorMax = new Vector2(1f, 1f);
            bRt.pivot = new Vector2(1f, 0.5f);
            bRt.sizeDelta = new Vector2(70f, 0f);
            bRt.anchoredPosition = new Vector2(-3f, 0f);
            btn.GetComponent<Image>().color = Cyan;

            var btnLbl = new GameObject("GoToLbl",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            btnLbl.transform.SetParent(btn.transform, false);
            var blRt = btnLbl.GetComponent<RectTransform>();
            blRt.anchorMin = Vector2.zero; blRt.anchorMax = Vector2.one;
            blRt.offsetMin = Vector2.zero; blRt.offsetMax = Vector2.zero;
            var bT = btnLbl.GetComponent<Text>();
            bT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            bT.text = "Go To"; bT.fontSize = 13; bT.fontStyle = FontStyle.Bold;
            bT.color = new Color(0.04f, 0.08f, 0.15f);
            bT.alignment = TextAnchor.MiddleCenter; bT.raycastTarget = false;
        }

        private void BuildOpsEmail()
        {
            var go = new GameObject("OpsEmail",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(_root, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(280f, 32f);
            rt.anchoredPosition = new Vector2(-12f, 0f);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = "✉ " + opsEmail;
            t.fontSize = 13; t.color = Slate;
            t.alignment = TextAnchor.MiddleRight;
            t.raycastTarget = false;
        }

        // ---------- Settings toggle ----------------------------------
        private LatticeSettingsPanel _settingsCache;
        private void ToggleSettings()
        {
            _settingsCache ??= FindAnyObjectByType<LatticeSettingsPanel>();
            if (_settingsCache == null) return;
            _settingsCache.Toggle();
        }

        // ---------- Tracks toggle ------------------------------------
        private LatticeTracksPanel _tracksPanelCache;
        private bool _tracksOpen = true;
        private void ToggleTracksPanel()
        {
            _tracksPanelCache ??= FindAnyObjectByType<LatticeTracksPanel>();
            if (_tracksPanelCache == null) return;
            _tracksOpen = !_tracksOpen;
            // Toggle child visibility (the container the panel built lives at
            // index 0 under its own transform).
            for (int i = 0; i < _tracksPanelCache.transform.childCount; i++)
            {
                _tracksPanelCache.transform.GetChild(i).gameObject.SetActive(_tracksOpen);
            }
        }

        // ---------- Layers basemap cycler ---------------------------
        private void CycleBasemap()
        {
            var loader = FindAnyObjectByType<NATO.C2.Net.OsmTileLoader>();
            if (loader == null) return;
            var styles = (NATO.C2.Net.OsmTileLoader.BasemapStyle[])
                System.Enum.GetValues(typeof(NATO.C2.Net.OsmTileLoader.BasemapStyle));
            // Skip Custom — only useful when set up manually in Inspector.
            int idx = System.Array.IndexOf(styles, loader.style);
            for (int tries = 0; tries < styles.Length; tries++)
            {
                idx = (idx + 1) % styles.Length;
                if (styles[idx] != NATO.C2.Net.OsmTileLoader.BasemapStyle.Custom) break;
            }
            loader.SetStyle(styles[idx]);

            // Surface a brief radio echo so the operator knows which layer
            // we switched to (also confirms the request fired).
            var hub = NATO.C2.Net.FeedHub.Instance;
            hub?.PublishRadio(new NATO.C2.Net.RadioMessage
            {
                net = "TANGO-6",
                timestampUtc = System.DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"basemap → <b>{styles[idx]}</b>",
                severity = NATO.C2.Net.RadioSeverity.System
            });
        }

        // ---------- Asset View ---------------------------------------
        //  Toggle the Main Camera between overhead (default) and a chase
        //  POV behind the currently selected unit. Mirrors the Anduril
        //  Lattice "Asset View" toolbar button.
        private bool _assetViewActive;
        private Vector3   _savedCamPos;
        private Quaternion _savedCamRot;
        private float     _savedFov;
        private NATO.C2.Agent _chasedAgent;

        private void ToggleAssetView()
        {
            var cam = Camera.main;
            if (cam == null) return;

            if (!_assetViewActive)
            {
                // Pick whichever agent is currently selected (first one).
                var mgr = NATO.C2.NATO_C2_Manager.Instance;
                if (mgr == null) return;
                NATO.C2.Agent target = null;
                foreach (var a in mgr.Agents)
                {
                    if (a == null) continue;
                    // For selected agents we'd need to expose Selected; the
                    // demo's NATO_C2_Manager already has Selected. Loop the
                    // first one in the selection.
                    if (a.affiliation == NATO.C2.Affiliation.Friendly && a.isSelected)
                    {
                        target = a; break;
                    }
                }
                if (target == null) return;

                _savedCamPos = cam.transform.position;
                _savedCamRot = cam.transform.rotation;
                _savedFov    = cam.fieldOfView;
                _chasedAgent = target;
                _assetViewActive = true;
            }
            else
            {
                cam.transform.position = _savedCamPos;
                cam.transform.rotation = _savedCamRot;
                cam.fieldOfView = _savedFov;
                _chasedAgent = null;
                _assetViewActive = false;
            }
        }

        private void LateUpdate()
        {
            if (!_assetViewActive || _chasedAgent == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            // Chase-cam: 8 m behind and 3 m above the unit, looking just above its head.
            Vector3 fwd = _chasedAgent.desiredFacing.sqrMagnitude > 0.01f
                ? _chasedAgent.desiredFacing.normalized
                : _chasedAgent.transform.forward;
            fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 desiredPos = _chasedAgent.transform.position - fwd * 8f + Vector3.up * 3.0f;
            cam.transform.position = Vector3.Lerp(cam.transform.position, desiredPos, 0.18f);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation,
                Quaternion.LookRotation((_chasedAgent.transform.position + Vector3.up * 1.0f)
                                        - cam.transform.position, Vector3.up), 0.18f);
            cam.fieldOfView = 55f;
        }

        // ---------- helpers ------------------------------------------
        private (double lat, double lon) WorldToLatLon(Vector3 world)
        {
            // Same projection used by TakServerCotAdapter and CotTrackPanel.
            const double EarthRadiusM = 6_378_137d;
            const double originLat = 38.7400, originLon = 22.2540;
            const double metresPerUnit = 50d;
            double dx = world.x * metresPerUnit;
            double dz = world.z * metresPerUnit;
            double lat = originLat + (dz / EarthRadiusM) * (180d / Math.PI);
            double lon = originLon + (dx / EarthRadiusM) * (180d / Math.PI) /
                                     Math.Cos(originLat * Math.PI / 180d);
            return (lat, lon);
        }
    }
}
