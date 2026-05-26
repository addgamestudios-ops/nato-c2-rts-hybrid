// =====================================================================
//  NATO C2 RTS Hybrid — DemoSceneBootstrap.cs
//  ---------------------------------------------------------------------
//  Drop this on an empty GameObject and press Play. Builds the full
//  scene at runtime:
//
//      • NATO_C2_Manager + ORCA + HPA* + Formation + Mythos AI
//      • Cinematic camera (WASD pan + mousewheel zoom + MMB orbit)
//      • Tactical grid ground with cyan grid lines + distance fade
//      • Atmospheric fog + sun + fill light + ambient
//      • 12 friendly drones (quadcopter composite mesh)
//      • 7 friendly tanks (hull + turret + cannon composite mesh)
//      • 5 hostile tanks (same mesh, red palette + patrol AI)
//      • Dynamic obstacles cycling in the AO
//      • Full HUD: top/bottom bars, drag-select, radial command menu,
//        threat heatmap, per-unit APP-6E symbol billboards
//
//  Zero Inspector wiring required.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2;
using NATO.C2.UI;
using NATO.C2.Net;

namespace NATO.C2.Sample
{
    [AddComponentMenu("NATO C2/Demo Scene Bootstrap")]
    public class DemoSceneBootstrap : MonoBehaviour
    {
        [Header("Counts")]
        public int friendlyDrones   = 10;
        public int friendlyTanks    = 6;
        public int friendlyInfantry = 6;
        public int hostileTanks     = 5;

        [Header("Scene")]
        public Vector2 worldSize = new Vector2(200f, 200f);
        public bool spawnDynamicObstacles = true;

        [Header("Basemap")]
        [Tooltip("If true, attempt to fetch real raster tiles at startup. Falls back to procedural topographic if offline.")]
        public bool useRealBasemap = true;
        [Tooltip("Default basemap style. Matches Anduril Lattice when set to SatelliteEsri (free, no API key).")]
        public NATO.C2.Net.OsmTileLoader.BasemapStyle basemapStyle =
            NATO.C2.Net.OsmTileLoader.BasemapStyle.SatelliteEsri;
        [Tooltip("Lat/Lon of map center. Default = LocalSimFeed origin (Lamia, Greece).")]
        public double basemapLat = 38.7400;
        public double basemapLon = 22.2540;
        [Range(10, 19)] public int basemapZoom = 16;

        [Header("TAK Server (CoT interop)")]
        [Tooltip("If true, attempt to connect to a TAK Server / FreeTAKServer and publish our friendlies as CoT tracks. Disabled = local simulation only.")]
        public bool connectToTakServer = false;
        [Tooltip("TAK Server host. 127.0.0.1 if running TAK locally; otherwise your team's deployment.")]
        public string takHost = "127.0.0.1";
        [Tooltip("TAK Server CoT TCP port. 8087 = plain TCP (TAK Server / FreeTAKServer default).")]
        public int takPort = 8087;

        // ---------- cached refs (built at Start) ------------------------
        private TacticalHUD _hud;
        private CommandRadialMenu _radialMenu;
        private Material _threatMaterial;
        private Camera _cam;

        private void Start()
        {
            BuildManager();
            BuildLighting();
            BuildCamera();
            BuildGround();
            BuildHud();
            BuildAgents();
            if (spawnDynamicObstacles) BuildObstacles();
        }

        // ---------- Manager + Subsystems --------------------------------
        private void BuildManager()
        {
            var go = new GameObject("NATO_C2_Manager",
                typeof(NATO_C2_Manager),
                typeof(ORCA),
                typeof(HPAStar),
                typeof(FormationController),
                typeof(AIAutonomousMode),
                typeof(EngagementTracker),
                typeof(FeedHub),
                typeof(LocalSimFeed),
                typeof(OperatorIdentity),
                typeof(Link16TdmaSimulator),
                typeof(CotSigner),
                typeof(OperatorPresenceBroadcaster));
            var hpa = go.GetComponent<HPAStar>();
            hpa.worldSize   = worldSize;
            hpa.worldOrigin = new Vector2(-worldSize.x * 0.5f, -worldSize.y * 0.5f);

            // Real TAK Server interop if enabled. If the server is offline the
            // adapter quietly retries in the background — the rest of the
            // simulation is unaffected.
            if (connectToTakServer)
            {
                var tak = go.AddComponent<TakServerCotAdapter>();
                tak.host = takHost;
                tak.port = takPort;
                tak.originLat = basemapLat;
                tak.originLon = basemapLon;

                // Render foreign tracks the server pushes back to us. Same
                // projection as the adapter so positions match the OSM tiles.
                var cot = new GameObject("CoT_Tracks", typeof(NATO.C2.UI.CotTrackPanel));
                var panel = cot.GetComponent<NATO.C2.UI.CotTrackPanel>();
                panel.originLat = basemapLat;
                panel.originLon = basemapLon;
                panel.metresPerUnit = tak.metresPerUnit;
            }
        }

        // ---------- Lighting + atmosphere -------------------------------
        private void BuildLighting()
        {
            // Key light — warm sun from upper-left.
            var sunGo = new GameObject("Sun", typeof(Light));
            var sun = sunGo.GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(0.95f, 0.93f, 0.85f);
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.55f;
            sunGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            // Cool fill light — cyan rim from opposite direction.
            var fillGo = new GameObject("Fill", typeof(Light));
            var fill = fillGo.GetComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = NATOPalette.AccentCyan * 0.4f;
            fill.intensity = 0.35f;
            fill.shadows = LightShadows.None;
            fillGo.transform.rotation = Quaternion.Euler(30f, 145f, 0f);

            // Ambient + fog tune the whole tactical mood.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor    = new Color(0.20f, 0.30f, 0.42f);
            RenderSettings.ambientEquatorColor = new Color(0.10f, 0.18f, 0.28f);
            RenderSettings.ambientGroundColor = new Color(0.03f, 0.06f, 0.10f);

            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.04f, 0.09f, 0.16f);
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 220f;
        }

        // ---------- Camera ----------------------------------------------
        private void BuildCamera()
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                go.tag = "MainCamera";
                _cam = go.GetComponent<Camera>();
            }
            _cam.transform.position = new Vector3(0f, 55f, -55f);
            _cam.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            _cam.backgroundColor = new Color(0.025f, 0.06f, 0.11f);
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.farClipPlane = 800f;
            _cam.fieldOfView = 50f;
            _cam.gameObject.AddComponent<DemoCameraController>();
        }

        // ---------- Tactical grid ground --------------------------------
        private void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(worldSize.x / 10f, 1f, worldSize.y / 10f);

            var mr = ground.GetComponent<Renderer>();
            // Use Standard with our procedural grid texture for the diffuse channel.
            var mat = new Material(Shader.Find("Standard"));
            mat.color = Color.white;
            mat.SetFloat("_Glossiness", 0.05f);
            mat.SetFloat("_Metallic", 0.0f);
            mat.mainTexture = BuildTacticalGridTexture();
            // Stretch the texture across the world plane.
            mat.mainTextureScale = new Vector2(worldSize.x / 10f, worldSize.y / 10f);
            mr.material = mat;

            // Optional: swap to real OSM raster basemap. If any tile fails the
            // procedural texture above stays as the fallback, so we never end
            // up with a blank plane offline.
            if (useRealBasemap)
            {
                var loader = ground.AddComponent<OsmTileLoader>();
                loader.target = mr;
                loader.centerLat = basemapLat;
                loader.centerLon = basemapLon;
                loader.style = basemapStyle;
                loader.zoom = basemapZoom;
                loader.gridSize = 3;
                loader.fallbackOnFailure = true;
                // Begin() is auto-called in OsmTileLoader.Start() since target is set.
            }
        }

        private Texture2D BuildTacticalGridTexture()
        {
            // Clean military map: lerp-blended elevation, soft contour rings, a
            // strong 100m / 1km MGRS hierarchy and crisp anti-aliased grid lines.
            const int size = 1024;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;

            var px = new Color32[size * size];
            // Palette: tactical sand / olive (USGS Topo style).
            Color valley  = new Color(0.38f, 0.42f, 0.30f);
            Color slope   = new Color(0.52f, 0.54f, 0.36f);
            Color ridge   = new Color(0.70f, 0.68f, 0.46f);
            Color contour = new Color(0.22f, 0.18f, 0.10f, 0.55f);
            Color minorG  = new Color(0.00f, 0.83f, 1.00f, 0.13f);
            Color majorG  = new Color(0.00f, 0.83f, 1.00f, 0.35f);

            const float noiseScale = 0.006f;  // much smoother than before
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Two octaves only — smoother than 3 octaves at high freq.
                    float e =
                        Mathf.PerlinNoise(x * noiseScale,        y * noiseScale)        * 0.75f +
                        Mathf.PerlinNoise(x * noiseScale * 3.2f, y * noiseScale * 3.2f) * 0.25f;
                    e = Mathf.Clamp01(e);

                    // Lerp between valley → slope → ridge based on elevation.
                    Color c = e < 0.5f
                        ? Color.Lerp(valley, slope, e * 2f)
                        : Color.Lerp(slope, ridge, (e - 0.5f) * 2f);

                    // Soft contour lines at 0.10 elevation intervals.
                    float band = e * 10f;
                    float frac = Mathf.Abs(band - Mathf.Round(band));
                    if (frac < 0.025f) c = Color.Lerp(c, contour, contour.a);

                    // MGRS grid: minor 100m (every 16 px), major 1km (every 160 px).
                    bool minorX = (x % 16) == 0;
                    bool minorY = (y % 16) == 0;
                    bool majorX = (x % 160) == 0;
                    bool majorY = (y % 160) == 0;
                    if (majorX || majorY)      c = Color.Lerp(c, majorG, majorG.a);
                    else if (minorX || minorY) c = Color.Lerp(c, minorG, minorG.a);

                    px[y * size + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(true);
            tex.name = "TopographicMap";
            return tex;
        }

        private static Color32 BlendOver(Color32 baseCol, Color32 overCol)
        {
            float a = overCol.a / 255f;
            float ia = 1f - a;
            return new Color32(
                (byte)(overCol.r * a + baseCol.r * ia),
                (byte)(overCol.g * a + baseCol.g * ia),
                (byte)(overCol.b * a + baseCol.b * ia),
                255);
        }

        // ---------- HUD --------------------------------------------------
        private void BuildHud()
        {
            var canvasGo = new GameObject("NATO_C2_HUD",
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(TacticalHUD),
                typeof(MilsymbolBridge),
                typeof(UnitDetailsPanel),
                typeof(RadioChatPanel),
                typeof(SubgroupPanel),
                typeof(MissionOverlay),
                typeof(HandheldDevicePanel),
                typeof(DronePipPanel),
                typeof(DronePipTargeting),
                typeof(IncomingRequestPanel),
                typeof(LatticeTopBar),
                typeof(LatticeTracksPanel),
                typeof(MavenBrackets),
                typeof(LatticeSettingsPanel));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            }

            _hud = canvasGo.GetComponent<TacticalHUD>();
            _hud.symbolBridge = canvasGo.GetComponent<MilsymbolBridge>();
            _hud.viewCamera = _cam;
            _hud.selectionBox = BuildSelectionBox(canvasGo.transform);

            // Legacy top status strip is replaced by the Anduril-Lattice-style
            // LatticeTopBar (already added as a component above). Bottom strip
            // stays — it carries formation + altitude-layer toggles.
            // BuildTopBar(canvasGo.transform);   // suppressed in favour of LatticeTopBar
            BuildBottomBar(canvasGo.transform);

            _hud.radialMenu = BuildRadialMenu(canvasGo.transform);
            _radialMenu = _hud.radialMenu;

            _threatMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            _threatMaterial.hideFlags = HideFlags.HideAndDontSave;
            _threatMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _threatMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _threatMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _threatMaterial.SetInt("_ZWrite", 0);
            _hud.threatMaterial = _threatMaterial;
        }

        private RectTransform BuildSelectionBox(Transform parent)
        {
            // Container so the outline edges sit cleanly inside.
            var go = new GameObject("SelectionBox", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0.83f, 1f, 0.18f);
            img.raycastTarget = false;   // critical — never intercept clicks
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            // Outline border (1px frame using four child images).
            AddBorderEdge(go.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1)); // bottom
            AddBorderEdge(go.transform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -1), new Vector2(0, 0)); // top
            AddBorderEdge(go.transform, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0)); // left
            AddBorderEdge(go.transform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-1, 0), new Vector2(0, 0)); // right
            go.SetActive(false);
            return rt;
        }

        private void AddBorderEdge(Transform parent, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject("Edge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0.83f, 1f, 0.95f);
            img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
        }

        private void BuildTopBar(Transform parent)
        {
            var bar = NewPanel(parent, "TopBar", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -56), new Vector2(0, 0));
            var bg = bar.GetComponent<Image>();
            bg.color = new Color(0.025f, 0.07f, 0.13f, 0.95f);
            bg.raycastTarget = false;

            _hud.missionLabel     = AddText(bar.transform, "Mission",      "OP THUNDERSTRIKE", 22, FontStyle.Bold, NATOPalette.AccentCyan,   new Vector2(0, 0), new Vector2(0, 1), new Vector2(24, -28));
            _hud.swarmCountLabel  = AddText(bar.transform, "SwarmCount",   "SELECTED: 0",      16, FontStyle.Normal, Color.white,            new Vector2(0.25f, 0), new Vector2(0.25f, 1), new Vector2(0, -28));
            _hud.threatLevelLabel = AddText(bar.transform, "ThreatLevel",  "THREATS: 0",       16, FontStyle.Normal, NATOPalette.FriendlyGreen, new Vector2(0.45f, 0), new Vector2(0.45f, 1), new Vector2(0, -28));
            _hud.aiStatusLabel    = AddText(bar.transform, "AIStatus",     "MYTHOS: ADVISORY", 16, FontStyle.Bold, NATOPalette.AccentCyan,    new Vector2(0.65f, 0), new Vector2(0.65f, 1), new Vector2(0, -28));
            _hud.hotkeyHintLabel  = AddText(bar.transform, "Hotkey",       "",                 14, FontStyle.Bold,  NATOPalette.NeutralYellow, new Vector2(0.85f, 0), new Vector2(0.85f, 1), new Vector2(0, -28));
        }

        private void BuildBottomBar(Transform parent)
        {
            var bar = NewPanel(parent, "BottomBar", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 64));
            var bg = bar.GetComponent<Image>();
            bg.color = new Color(0.025f, 0.07f, 0.13f, 0.95f);
            bg.raycastTarget = false;

            AddText(bar.transform, "FormLbl", "FORMATION", 13, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(28, 16));
            var ddGo = new GameObject("FormationDropdown",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Dropdown));
            ddGo.transform.SetParent(bar.transform, false);
            var ddRt = ddGo.GetComponent<RectTransform>();
            ddRt.anchorMin = ddRt.anchorMax = new Vector2(0, 0.5f);
            ddRt.pivot = new Vector2(0, 0.5f);
            ddRt.sizeDelta = new Vector2(140, 32);
            ddRt.anchoredPosition = new Vector2(28, -8);
            ddGo.GetComponent<Image>().color = new Color(0.08f, 0.16f, 0.28f, 1f);
            var dd = ddGo.GetComponent<Dropdown>();
            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            lblGo.transform.SetParent(ddGo.transform, false);
            var lblTxt = lblGo.GetComponent<Text>();
            lblTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            lblTxt.fontSize = 14;
            lblTxt.color = Color.white;
            lblTxt.alignment = TextAnchor.MiddleLeft;
            var lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = new Vector2(8, 0); lblRt.offsetMax = new Vector2(-20, 0);
            dd.captionText = lblTxt;
            _hud.formationDropdown = dd;

            _hud.autonomousToggle = AddToggle(bar.transform, "AutonomousToggle", "MYTHOS AUTONOMOUS",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(200, -8));

            _hud.layerGroundToggle = AddToggle(bar.transform, "LayerGround", "GROUND",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-360, -8));
            _hud.layerGroundToggle.isOn = true;
            _hud.layerLowToggle = AddToggle(bar.transform, "LayerLow", "LOW",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-240, -8));
            _hud.layerLowToggle.isOn = true;
            _hud.layerHighToggle = AddToggle(bar.transform, "LayerHigh", "HIGH",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-140, -8));
            _hud.layerHighToggle.isOn = true;
        }

        private CommandRadialMenu BuildRadialMenu(Transform parent)
        {
            var menuGo = new GameObject("RadialMenu",
                typeof(RectTransform), typeof(CanvasGroup), typeof(CommandRadialMenu));
            menuGo.transform.SetParent(parent, false);
            var menuRt = menuGo.GetComponent<RectTransform>();
            menuRt.sizeDelta = new Vector2(360, 360);
            menuRt.pivot = new Vector2(0.5f, 0.5f);
            menuRt.anchorMin = menuRt.anchorMax = Vector2.zero;

            var menu = menuGo.GetComponent<CommandRadialMenu>();
            menu.canvasGroup = menuGo.GetComponent<CanvasGroup>();
            menu.root = menuRt;

            // Central donut hole (dark background ring) for visual cohesion.
            var hub = new GameObject("Hub", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hub.transform.SetParent(menuRt, false);
            var hubImg = hub.GetComponent<Image>();
            hubImg.color = new Color(0.02f, 0.06f, 0.12f, 0.85f);
            hubImg.raycastTarget = false;
            var hubRt = hub.GetComponent<RectTransform>();
            hubRt.anchorMin = hubRt.anchorMax = new Vector2(0.5f, 0.5f);
            hubRt.pivot = new Vector2(0.5f, 0.5f);
            hubRt.sizeDelta = new Vector2(280, 280);
            hubImg.sprite = BuildCircleSprite(64);
            hubImg.type = Image.Type.Sliced;

            menu.wedgeImages = new Image[6];
            menu.wedgeLabels = new Text[6];
            string[] labels = { "MOVE", "ATTACK", "LOITER", "SWARM", "RTB", "HOLD" };
            // Counter-clockwise from top matches CommandRadialMenu's hover-detection math.
            for (int i = 0; i < 6; i++)
            {
                float deg = -90f - i * 60f;
                float rad = deg * Mathf.Deg2Rad;
                Vector2 pos = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad)) * 110f;

                var wGo = new GameObject($"Wedge_{labels[i]}",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                wGo.transform.SetParent(menuRt, false);
                var wRt = wGo.GetComponent<RectTransform>();
                wRt.anchorMin = wRt.anchorMax = new Vector2(0.5f, 0.5f);
                wRt.pivot = new Vector2(0.5f, 0.5f);
                wRt.sizeDelta = new Vector2(96, 96);
                wRt.anchoredPosition = pos;
                var wImg = wGo.GetComponent<Image>();
                wImg.color = new Color(1, 1, 1, 0.20f);
                wImg.raycastTarget = false;
                wImg.sprite = BuildCircleSprite(48);
                menu.wedgeImages[i] = wImg;

                var lblGo = new GameObject("Label",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                lblGo.transform.SetParent(wGo.transform, false);
                var lblTxt = lblGo.GetComponent<Text>();
                lblTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                lblTxt.text = labels[i];
                lblTxt.fontSize = 18;
                lblTxt.fontStyle = FontStyle.Bold;
                lblTxt.color = NATOPalette.AccentCyan;
                lblTxt.alignment = TextAnchor.MiddleCenter;
                lblTxt.raycastTarget = false;
                var lblRt = lblGo.GetComponent<RectTransform>();
                lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
                lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;
                menu.wedgeLabels[i] = lblTxt;
            }

            menuGo.SetActive(false);
            return menu;
        }

        private static Sprite _circleSpriteCache;
        private static Sprite BuildCircleSprite(int radius)
        {
            if (_circleSpriteCache != null) return _circleSpriteCache;
            int size = radius * 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[size * size];
            int r2 = radius * radius;
            int cx = radius, cy = radius;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    px[y * size + x] = d2 <= r2 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply(false);
            _circleSpriteCache = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _circleSpriteCache;
        }

        // ---------- UI primitive helpers --------------------------------
        private RectTransform NewPanel(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 off1, Vector2 off2)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = off1; rt.offsetMax = off2;
            return rt;
        }

        private Text AddText(Transform parent, string name, string text, int size, FontStyle style, Color colour,
                              Vector2 aMin, Vector2 aMax, Vector2 anchoredPos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = colour;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.pivot = new Vector2(0, 0.5f);
            rt.sizeDelta = new Vector2(320, 28);
            rt.anchoredPosition = anchoredPos;
            return t;
        }

        private Toggle AddToggle(Transform parent, string name, string label,
                                  Vector2 aMin, Vector2 aMax, Vector2 anchoredPos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Toggle));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.pivot = new Vector2(0, 0.5f);
            rt.sizeDelta = new Vector2(220, 24);
            rt.anchoredPosition = anchoredPos;
            var toggle = go.GetComponent<Toggle>();

            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bgGo.transform.SetParent(go.transform, false);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = new Color(0.06f, 0.13f, 0.22f, 1f);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.sizeDelta = new Vector2(20, 20);
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0, 0.5f);
            bgRt.pivot = new Vector2(0, 0.5f);
            bgRt.anchoredPosition = new Vector2(0, 0);
            toggle.targetGraphic = bgImg;

            var ckGo = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            ckGo.transform.SetParent(bgGo.transform, false);
            var ckImg = ckGo.GetComponent<Image>();
            ckImg.color = NATOPalette.AccentCyan;
            var ckRt = ckGo.GetComponent<RectTransform>();
            ckRt.anchorMin = Vector2.zero; ckRt.anchorMax = Vector2.one;
            ckRt.offsetMin = new Vector2(4, 4); ckRt.offsetMax = new Vector2(-4, -4);
            toggle.graphic = ckImg;

            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            lblGo.transform.SetParent(go.transform, false);
            var t = lblGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = label;
            t.fontSize = 13;
            t.color = NATOPalette.AccentCyan;
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            var lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = lblRt.anchorMax = new Vector2(0, 0.5f);
            lblRt.pivot = new Vector2(0, 0.5f);
            lblRt.sizeDelta = new Vector2(180, 20);
            lblRt.anchoredPosition = new Vector2(28, 0);

            return toggle;
        }

        // =================================================================
        //  AGENTS — composite meshes (tank: hull + turret + cannon,
        //  drone: central disc + 4 prop arms)
        // =================================================================
        private void BuildAgents()
        {
            int n = friendlyDrones + friendlyTanks + friendlyInfantry + hostileTanks;
            int infantryStart = friendlyDrones + friendlyTanks;
            int hostileStart  = infantryStart + friendlyInfantry;
            for (int i = 0; i < n; i++)
            {
                bool isFriendlyDrone    = i < friendlyDrones;
                bool isFriendlyTank     = i >= friendlyDrones && i < infantryStart;
                bool isFriendlyInfantry = i >= infantryStart   && i < hostileStart;
                UnitType type;
                Affiliation aff;
                AltitudeLayer layer;
                Vector3 pos;
                if (isFriendlyDrone)
                {
                    type = UnitType.Drone; aff = Affiliation.Friendly; layer = AltitudeLayer.Low;
                    pos = new Vector3(Random.Range(-50f, -20f), 14f, Random.Range(-30f, 30f));
                }
                else if (isFriendlyTank)
                {
                    // Last 2 tanks reclassified as Artillery — picked first by the
                    // IncomingRequestPanel CFF auto-routing. Same composite mesh
                    // (production swaps to an SP155 / Caesar / Archer model).
                    int tankIndex = i - friendlyDrones;
                    bool isArty = tankIndex >= friendlyTanks - 2;
                    type = isArty ? UnitType.Artillery : UnitType.Tank;
                    aff = Affiliation.Friendly; layer = AltitudeLayer.Ground;
                    // Artillery sits further back (rear of the formation).
                    pos = isArty
                        ? new Vector3(Random.Range(-75f, -60f), 0.4f, Random.Range(-15f, 15f))
                        : new Vector3(Random.Range(-55f, -25f), 0.4f, Random.Range(-30f, 30f));
                }
                else if (isFriendlyInfantry)
                {
                    // Last 2 infantry reclassified as Medic — picked first by MEDEVAC
                    // auto-routing. Same composite mesh; production adds a red-cross
                    // brassard / red helmet stripe.
                    int infIndex = i - infantryStart;
                    bool isMedic = infIndex >= friendlyInfantry - 2;
                    type = isMedic ? UnitType.Medic : UnitType.Infantry;
                    aff = Affiliation.Friendly; layer = AltitudeLayer.Ground;
                    pos = new Vector3(Random.Range(-45f, -15f), 0.4f, Random.Range(-20f, 20f));
                }
                else
                {
                    type = UnitType.Tank; aff = Affiliation.Hostile; layer = AltitudeLayer.Ground;
                    pos = new Vector3(Random.Range(25f, 55f), 0.4f, Random.Range(-30f, 30f));
                }

                GameObject unit;
                if (type == UnitType.Drone)             unit = BuildDroneMesh   ($"{aff}_Drone_{i}", aff);
                else if (type == UnitType.Infantry)     unit = BuildInfantryMesh($"{aff}_Inf_{i}",   aff, isMedic: false);
                else if (type == UnitType.Medic)        unit = BuildInfantryMesh($"{aff}_Medic_{i}", aff, isMedic: true);
                else if (type == UnitType.Artillery)    unit = BuildTankMesh    ($"{aff}_Arty_{i}",  aff, isArtillery: true);
                else                                    unit = BuildTankMesh    ($"{aff}_Tank_{i}",  aff, isArtillery: false);
                unit.transform.position = pos;

                var agent = unit.AddComponent<Agent>();
                agent.unitType = type;
                agent.affiliation = aff;
                agent.layer = layer;
                agent.callsign = BuildCallsign(aff, type, i);
                agent.echelon = type == UnitType.Drone ? 12 : 14;
                agent.quantity = 1;
                if (i % 7 == 0) agent.reinforcedDetached = "+";
                else if (i % 11 == 0) agent.reinforcedDetached = "-";
                agent.maxSpeed = type == UnitType.Drone ? 14f : type == UnitType.Infantry ? 4f : 9f;
                agent.radius   = type == UnitType.Drone ? 0.7f : type == UnitType.Infantry ? 0.5f : 1.6f;
                agent.hullRenderer = unit.GetComponentInChildren<Renderer>();
                // Personnel + sensor metadata for the details sidebar.
                agent.rank = PickRank(type, i);
                agent.commandingOfficer = PickOfficer(aff, i);
                agent.sensors = SensorHealth.GPS | SensorHealth.Radio | SensorHealth.BFT
                              | ((i % 3 == 0) ? SensorHealth.Radar : 0)
                              | ((i % 5 == 0) ? SensorHealth.EW    : 0);

                // BoxCollider on the root so click-raycasts hit the whole unit.
                var col = unit.AddComponent<BoxCollider>();
                col.size = type == UnitType.Drone
                    ? new Vector3(3.4f, 1.0f, 3.4f)
                    : type == UnitType.Infantry
                        ? new Vector3(1.2f, 2.0f, 1.2f)
                        : new Vector3(3.0f, 2.0f, 4.5f);
                col.center = type == UnitType.Drone ? Vector3.zero
                             : type == UnitType.Infantry ? new Vector3(0, 1f, 0)
                                                          : new Vector3(0, 1f, 0);

                unit.AddComponent<UnitSymbolRenderer>();
                unit.AddComponent<SelectionRingRenderer>();

                // Friendly drones get a 3D loiter racetrack + [CALLSIGN] HAE label
                // when they're orbiting — mirrors the Anduril Lattice ALTIUS view.
                if (aff == Affiliation.Friendly && type == UnitType.Drone)
                {
                    unit.AddComponent<LoiterPatternRenderer>();
                }

                if (aff == Affiliation.Hostile)
                {
                    // HostilePatrol kicks in only while no friendlies are in sight.
                    // The new HostileCombatAI takes over on contact and drives
                    // engagement / break-contact behaviour.
                    var patrol = unit.AddComponent<HostilePatrol>();
                    patrol.areaCenter = new Vector3(40f, pos.y, 0f);
                    patrol.areaRadius = 28f;

                    var combat = unit.AddComponent<HostileCombatAI>();
                    combat.aoHalfExtent = Mathf.Max(worldSize.x, worldSize.y) * 0.45f;
                }
            }
        }

        // ---------- Tank composite mesh ---------------------------------
        private GameObject BuildTankMesh(string name, Affiliation aff, bool isArtillery = false)
        {
            // ABRAMS-ish silhouette (or SP-howitzer if isArtillery): sloped glacis,
            // low boxy turret with bustle, long cannon, side skirts, commander
            // cupola, antenna whip. Artillery variant gets a green bustle band
            // (NATO friendly artillery marking) and a longer howitzer barrel.
            var root = new GameObject(name);
            var bodyMat   = MakeUnitMaterial(aff, 0.35f, 0.18f);
            var detailMat = MakeUnitMaterial(aff, 0.45f, 0.32f);
            var trackMat  = MakeUnitMaterial(Affiliation.Unknown, 0.05f, 0.02f);
            trackMat.color = new Color(0.10f, 0.10f, 0.11f);
            var optMat    = MakeUnitMaterial(Affiliation.Unknown, 0.85f, 0.55f);
            optMat.color = new Color(0.05f, 0.06f, 0.08f);

            // Hull (lower box).
            var hull = NewBox("Hull", root.transform, bodyMat);
            hull.transform.localScale    = new Vector3(2.5f, 0.55f, 3.6f);
            hull.transform.localPosition = new Vector3(0f, 0.60f, 0f);

            // Sloped glacis — wedge in front, rotated forward.
            var glacis = NewBox("Glacis", root.transform, bodyMat);
            glacis.transform.localScale    = new Vector3(2.5f, 0.55f, 0.85f);
            glacis.transform.localPosition = new Vector3(0f, 0.62f, 1.95f);
            glacis.transform.localRotation = Quaternion.Euler(-35f, 0f, 0f);

            // Sloped rear plate.
            var rear = NewBox("Rear", root.transform, bodyMat);
            rear.transform.localScale    = new Vector3(2.5f, 0.55f, 0.55f);
            rear.transform.localPosition = new Vector3(0f, 0.60f, -1.85f);
            rear.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);

            // Turret — wider low box with chamfered rear bustle stacked behind.
            var turret = NewBox("Turret", root.transform, detailMat);
            turret.transform.localScale    = new Vector3(2.0f, 0.42f, 2.1f);
            turret.transform.localPosition = new Vector3(0f, 1.15f, -0.10f);

            var bustle = NewBox("TurretBustle", root.transform, detailMat);
            bustle.transform.localScale    = new Vector3(1.8f, 0.35f, 0.85f);
            bustle.transform.localPosition = new Vector3(0f, 1.10f, -1.10f);

            // Cannon: thin barrel forward of turret, with thermal sleeve mid-bulge.
            var mantlet = NewBox("Mantlet", turret.transform, detailMat);
            mantlet.transform.localScale    = new Vector3(0.55f, 0.34f, 0.55f);
            mantlet.transform.localPosition = new Vector3(0f, 0f, 1.05f);

            // Artillery: longer howitzer barrel + muzzle brake. Tank: standard
            // tank-gun length with thermal sleeve.
            float barrelLen   = isArtillery ? 1.8f : 1.2f;
            float barrelZ     = isArtillery ? 2.85f : 2.25f;
            float barrelThick = isArtillery ? 0.12f : 0.10f;
            var cannon = NewCylinder("Cannon", turret.transform, detailMat);
            cannon.transform.localScale    = new Vector3(barrelThick, barrelLen, barrelThick);
            cannon.transform.localPosition = new Vector3(0f, 0f, barrelZ);
            cannon.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            if (isArtillery)
            {
                // Muzzle brake (chunky ring at the end of the barrel).
                var muzzle = NewCylinder("MuzzleBrake", turret.transform, detailMat);
                muzzle.transform.localScale    = new Vector3(0.22f, 0.10f, 0.22f);
                muzzle.transform.localPosition = new Vector3(0f, 0f, barrelZ + barrelLen);
                muzzle.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                var sleeve = NewCylinder("ThermalSleeve", turret.transform, detailMat);
                sleeve.transform.localScale    = new Vector3(0.14f, 0.35f, 0.14f);
                sleeve.transform.localPosition = new Vector3(0f, 0f, 1.85f);
                sleeve.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }

            // Green NATO friendly-artillery band on the bustle (only friendly arty —
            // hostile artillery doesn't get the marking).
            if (isArtillery && aff == Affiliation.Friendly)
            {
                var bandMat = new Material(Shader.Find("Standard"));
                bandMat.color = new Color(0.20f, 0.85f, 0.30f);
                bandMat.SetFloat("_Glossiness", 0.1f);
                var band = NewBox("ArtyBand", root.transform, bandMat);
                band.transform.localScale    = new Vector3(1.85f, 0.10f, 0.30f);
                band.transform.localPosition = new Vector3(0f, 1.30f, -0.95f);
            }

            // Commander's cupola (low cylinder on top).
            var cupola = NewCylinder("Cupola", turret.transform, detailMat);
            cupola.transform.localScale    = new Vector3(0.45f, 0.10f, 0.45f);
            cupola.transform.localPosition = new Vector3(-0.55f, 0.30f, 0.30f);

            // Coax/optic block (small black box on top-front of turret).
            var optic = NewBox("Optic", turret.transform, optMat);
            optic.transform.localScale    = new Vector3(0.35f, 0.18f, 0.30f);
            optic.transform.localPosition = new Vector3(0.55f, 0.25f, 0.55f);

            // Antenna whip — thin tall cylinder, slight backward tilt.
            var antenna = NewCylinder("Antenna", root.transform, optMat);
            antenna.transform.localScale    = new Vector3(0.04f, 0.85f, 0.04f);
            antenna.transform.localPosition = new Vector3(0.85f, 1.95f, -0.90f);
            antenna.transform.localRotation = Quaternion.Euler(-6f, 0f, 0f);

            // Tracks (left + right) — taller boxes with skirts over them.
            for (int side = -1; side <= 1; side += 2)
            {
                var track = NewBox(side < 0 ? "TrackL" : "TrackR", root.transform, trackMat);
                track.transform.localScale    = new Vector3(0.42f, 0.55f, 3.8f);
                track.transform.localPosition = new Vector3(1.28f * side, 0.30f, 0f);

                var skirt = NewBox(side < 0 ? "SkirtL" : "SkirtR", root.transform, bodyMat);
                skirt.transform.localScale    = new Vector3(0.10f, 0.42f, 3.4f);
                skirt.transform.localPosition = new Vector3(1.42f * side, 0.55f, 0f);
            }

            return root;
        }

        // ---------- Infantry composite mesh -----------------------------
        private GameObject BuildInfantryMesh(string name, Affiliation aff, bool isMedic = false)
        {
            // Stylised dismount: ACH helmet, plate carrier over fatigues, arms,
            // legs, slung M4. Everything primitives.
            var root = new GameObject(name);
            var fatigueMat = MakeUnitMaterial(aff, 0.20f, 0.05f);          // uniform colour
            var armorMat   = MakeUnitMaterial(aff, 0.22f, 0.08f);          // slightly darker plate
            {
                var c = NATOPalette.For(aff) * 0.80f;
                armorMat.color = new Color(c.r, c.g, c.b, 1f);
            }
            var gearMat    = MakeUnitMaterial(Affiliation.Unknown, 0.05f, 0.0f);
            gearMat.color  = new Color(0.12f, 0.13f, 0.14f);
            var helmetMat  = MakeUnitMaterial(Affiliation.Unknown, 0.12f, 0.05f);
            helmetMat.color = new Color(0.20f, 0.22f, 0.18f);              // dark olive helmet

            // Legs (two stout cubes).
            for (int side = -1; side <= 1; side += 2)
            {
                var leg = NewBox(side < 0 ? "LegL" : "LegR", root.transform, fatigueMat);
                leg.transform.localScale    = new Vector3(0.20f, 0.85f, 0.22f);
                leg.transform.localPosition = new Vector3(0.13f * side, 0.42f, 0f);
            }

            // Torso (fatigue base) + plate carrier on top (slightly larger).
            var torso = NewBox("Torso", root.transform, fatigueMat);
            torso.transform.localScale    = new Vector3(0.50f, 0.70f, 0.32f);
            torso.transform.localPosition = new Vector3(0f, 1.15f, 0f);

            var carrier = NewBox("PlateCarrier", root.transform, armorMat);
            carrier.transform.localScale    = new Vector3(0.56f, 0.55f, 0.34f);
            carrier.transform.localPosition = new Vector3(0f, 1.20f, 0.01f);

            // Mag pouches across the front of the carrier (3 small bumps).
            for (int i = -1; i <= 1; i++)
            {
                var pouch = NewBox($"Mag{i}", root.transform, gearMat);
                pouch.transform.localScale    = new Vector3(0.14f, 0.16f, 0.06f);
                pouch.transform.localPosition = new Vector3(0.18f * i, 1.10f, 0.20f);
            }

            // Arms (two cubes, slight forward angle to suggest carrying a rifle).
            for (int side = -1; side <= 1; side += 2)
            {
                var arm = NewBox(side < 0 ? "ArmL" : "ArmR", root.transform, fatigueMat);
                arm.transform.localScale    = new Vector3(0.16f, 0.65f, 0.16f);
                arm.transform.localPosition = new Vector3(0.36f * side, 1.20f, 0.05f);
                arm.transform.localRotation = Quaternion.Euler(15f * side, 0f, -5f * side);
            }

            // Head + helmet (helmet is a slightly squashed sphere over the head).
            var head = NewSphere("Head", root.transform, gearMat);
            head.transform.localScale    = new Vector3(0.24f, 0.26f, 0.24f);
            head.transform.localPosition = new Vector3(0f, 1.65f, 0.02f);

            var helmet = NewSphere("Helmet", root.transform, helmetMat);
            helmet.transform.localScale    = new Vector3(0.32f, 0.22f, 0.32f);
            helmet.transform.localPosition = new Vector3(0f, 1.74f, 0.01f);

            // Medic markings — red helmet cross + red brassard on the left arm.
            // Strictly NATO Geneva Convention practice (red on white field for
            // medical personnel). Hostile medics still get a marker because the
            // simulation respects ROE for both sides.
            if (isMedic)
            {
                var redMat = new Material(Shader.Find("Standard"));
                redMat.color = new Color(0.95f, 0.20f, 0.20f);
                redMat.SetFloat("_Glossiness", 0.05f);
                var whiteMat = new Material(Shader.Find("Standard"));
                whiteMat.color = Color.white;
                whiteMat.SetFloat("_Glossiness", 0.05f);

                // White patch on the front of the helmet…
                var helmPatch = NewBox("MedicHelmetPatch", root.transform, whiteMat);
                helmPatch.transform.localScale    = new Vector3(0.18f, 0.10f, 0.05f);
                helmPatch.transform.localPosition = new Vector3(0f, 1.78f, 0.18f);
                // …with a red cross laid over it (two thin perpendicular bars).
                var crossV = NewBox("MedicCrossV", root.transform, redMat);
                crossV.transform.localScale    = new Vector3(0.05f, 0.09f, 0.06f);
                crossV.transform.localPosition = new Vector3(0f, 1.78f, 0.20f);
                var crossH = NewBox("MedicCrossH", root.transform, redMat);
                crossH.transform.localScale    = new Vector3(0.14f, 0.04f, 0.06f);
                crossH.transform.localPosition = new Vector3(0f, 1.78f, 0.20f);

                // Red brassard on the left upper arm.
                var brassard = NewBox("MedicBrassard", root.transform, redMat);
                brassard.transform.localScale    = new Vector3(0.20f, 0.18f, 0.20f);
                brassard.transform.localPosition = new Vector3(-0.38f, 1.36f, 0.05f);
            }

            // Slung rifle across the chest — long thin box with a stock cube and a foregrip.
            var rifle = new GameObject("Rifle");
            rifle.transform.SetParent(root.transform, false);
            rifle.transform.localPosition = new Vector3(0.05f, 1.10f, 0.20f);
            rifle.transform.localRotation = Quaternion.Euler(0f, 0f, -20f);

            var receiver = NewBox("Receiver", rifle.transform, gearMat);
            receiver.transform.localScale    = new Vector3(0.06f, 0.10f, 0.85f);
            receiver.transform.localPosition = Vector3.zero;

            var stock = NewBox("Stock", rifle.transform, gearMat);
            stock.transform.localScale    = new Vector3(0.06f, 0.14f, 0.22f);
            stock.transform.localPosition = new Vector3(0f, -0.01f, -0.50f);

            var foregrip = NewBox("Foregrip", rifle.transform, gearMat);
            foregrip.transform.localScale    = new Vector3(0.06f, 0.12f, 0.06f);
            foregrip.transform.localPosition = new Vector3(0f, -0.10f, 0.18f);

            return root;
        }

        // ---------- Drone (quadcopter) composite mesh -------------------
        private GameObject BuildDroneMesh(string name, Affiliation aff)
        {
            // Group 1/2 ISR quadcopter: flat hex-ish body, 4 booms with
            // motor caps, wide thin rotor discs, EO/IR gimbal sphere
            // underslung, two skids for clean landing pose.
            var root = new GameObject(name);
            var bodyMat   = MakeUnitMaterial(aff, 0.4f, 0.25f);
            var motorMat  = MakeUnitMaterial(Affiliation.Unknown, 0.20f, 0.45f);
            motorMat.color = new Color(0.10f, 0.10f, 0.11f);
            var rotorMat  = MakeUnitMaterial(Affiliation.Unknown, 0.05f, 0.05f);
            rotorMat.color = new Color(0.06f, 0.06f, 0.06f, 1f);
            var gimbalMat = MakeUnitMaterial(Affiliation.Unknown, 0.85f, 0.6f);
            gimbalMat.color = new Color(0.05f, 0.05f, 0.06f);
            var lensMat   = MakeUnitMaterial(Affiliation.Unknown, 0.95f, 0.0f);
            lensMat.color = new Color(0.0f, 0.65f, 0.85f);

            // Central body — flattened sphere chamfered with a cap.
            var body = NewSphere("Body", root.transform, bodyMat);
            body.transform.localScale = new Vector3(1.2f, 0.45f, 1.2f);

            var cap = NewBox("BodyCap", root.transform, bodyMat);
            cap.transform.localScale = new Vector3(0.85f, 0.18f, 0.85f);
            cap.transform.localPosition = new Vector3(0f, 0.18f, 0f);

            // Underslung gimbal ball + lens dot.
            var gimbal = NewSphere("Gimbal", root.transform, gimbalMat);
            gimbal.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            gimbal.transform.localPosition = new Vector3(0f, -0.35f, 0.15f);

            var lens = NewSphere("Lens", gimbal.transform, lensMat);
            lens.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.45f);

            // 4 booms with motor caps + rotor discs.
            float arm = 1.4f;
            for (int i = 0; i < 4; i++)
            {
                float ang = (Mathf.PI / 4f) + i * (Mathf.PI / 2f);
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));

                var armGo = NewBox($"Arm{i}", root.transform, bodyMat);
                armGo.transform.localScale = new Vector3(0.12f, 0.10f, arm);
                armGo.transform.localPosition = dir * (arm * 0.5f);
                armGo.transform.localRotation = Quaternion.LookRotation(dir, Vector3.up);

                var motor = NewCylinder($"Motor{i}", root.transform, motorMat);
                motor.transform.localScale = new Vector3(0.22f, 0.06f, 0.22f);
                motor.transform.localPosition = dir * arm + new Vector3(0f, 0.10f, 0f);

                var rotor = NewCylinder($"Rotor{i}", root.transform, rotorMat);
                rotor.transform.localScale = new Vector3(0.95f, 0.012f, 0.95f);
                rotor.transform.localPosition = dir * arm + new Vector3(0f, 0.18f, 0f);
                rotor.AddComponent<SpinAround>().rpm = 3000f;
            }

            // Landing skids — two parallel rails under the body.
            for (int side = -1; side <= 1; side += 2)
            {
                var skid = NewBox(side < 0 ? "SkidL" : "SkidR", root.transform, motorMat);
                skid.transform.localScale = new Vector3(0.05f, 0.05f, 1.2f);
                skid.transform.localPosition = new Vector3(0.45f * side, -0.55f, 0f);

                // Vertical struts joining skid to body (one fore, one aft).
                for (int z = -1; z <= 1; z += 2)
                {
                    var strut = NewBox($"Strut{side}{z}", root.transform, motorMat);
                    strut.transform.localScale = new Vector3(0.04f, 0.35f, 0.04f);
                    strut.transform.localPosition = new Vector3(0.42f * side, -0.35f, 0.40f * z);
                }
            }

            return root;
        }

        // ---------- Shared material factory -----------------------------
        // ---------- Primitive helpers ----------------------------------
        //  Strip colliders and apply a shared material in one line so the
        //  composite mesh builders above stay readable.

        private static GameObject NewBox(string name, Transform parent, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static GameObject NewSphere(string name, Transform parent, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static GameObject NewCylinder(string name, Transform parent, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static Material MakeUnitMaterial(Affiliation aff, float smoothness, float metallic)
        {
            var mat = new Material(Shader.Find("Standard"));
            mat.color = NATOPalette.For(aff);
            mat.SetFloat("_Glossiness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            return mat;
        }

        private static string PickRank(UnitType type, int i)
        {
            string[] tankRanks  = { "2LT", "1LT", "CPT", "SFC", "SSG" };
            string[] droneRanks = { "SFC", "SSG", "SGT", "CPL" };
            var list = type == UnitType.Drone ? droneRanks : tankRanks;
            return list[i % list.Length];
        }

        private static string PickOfficer(Affiliation aff, int i)
        {
            string[] f = { "MAYER", "RIOS", "BENOIT", "OKAFOR", "PETERS", "DUBOIS", "HAYES", "KOHL" };
            string[] h = { "VOLKOV", "TARASOV", "KALININ", "PETROV", "MOROZOV" };
            return aff == Affiliation.Hostile ? h[i % h.Length] : f[i % f.Length];
        }

        private static string BuildCallsign(Affiliation aff, UnitType type, int i)
        {
            string[] friendlyDronesC = { "SWIFT", "HAWK", "RAVEN", "FALCON" };
            string[] friendlyTanksC  = { "ALPHA", "BRAVO", "CHARLIE", "DELTA" };
            string[] hostiles        = { "BEAR",  "WOLF",  "VLK",     "TIGER" };
            if (aff == Affiliation.Hostile) return $"{hostiles[i % hostiles.Length]}-{(i % 9) + 1}";
            if (type == UnitType.Drone)    return $"{friendlyDronesC[i % friendlyDronesC.Length]}-{(i % 9) + 1}";
            return $"{friendlyTanksC[i % friendlyTanksC.Length]}-{(i % 9) + 1}";
        }

        // ---------- Obstacles --------------------------------------------
        private void BuildObstacles()
        {
            var prefabGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prefabGo.name = "ObstacleTemplate";
            prefabGo.transform.localScale = new Vector3(4f, 2f, 4f);
            DestroyImmediate(prefabGo.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(0.85f, 0.55f, 0.10f);
            mat.SetFloat("_Glossiness", 0.1f);
            prefabGo.GetComponent<Renderer>().sharedMaterial = mat;
            var obs = prefabGo.AddComponent<DynamicObstacle>();
            obs.localPolygon = new[] {
                new Vector2(-2f, -2f), new Vector2( 2f, -2f),
                new Vector2( 2f,  2f), new Vector2(-2f,  2f)
            };
            prefabGo.SetActive(false);

            var spawnerGo = new GameObject("ObstacleSpawner");
            var spawner = spawnerGo.AddComponent<DynamicObstacleSpawner>();
            spawner.obstaclePrefab = obs;
            spawner.areaSize = worldSize * 0.55f;
            spawner.interval = 3f;
            spawner.maxAlive = 8;
            spawner.movingObstacles = true;
        }
    }

    // =====================================================================
    //  HostilePatrol — gives each hostile a wandering target inside a radius.
    // =====================================================================
    [RequireComponent(typeof(Agent))]
    [AddComponentMenu("NATO C2/Hostile Patrol (Demo)")]
    public class HostilePatrol : MonoBehaviour
    {
        public Vector3 areaCenter = Vector3.zero;
        public float   areaRadius = 25f;
        [Min(0.5f)] public float arriveDistance = 3f;

        private Agent _agent;
        private Vector3 _target;
        private float _retargetTime;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            PickTarget();
        }

        private void Update()
        {
            if (_agent == null) return;
            Vector3 to = _target - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d < arriveDistance || Time.time > _retargetTime)
            {
                PickTarget();
                return;
            }
            Vector3 step = to.normalized * _agent.maxSpeed * 0.4f;
            _agent.preferredVelocity = step;
            transform.position += step * Time.deltaTime;
            // Face direction of motion.
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(step.normalized, Vector3.up), Time.deltaTime * 4f);
        }

        private void PickTarget()
        {
            Vector2 r = Random.insideUnitCircle * areaRadius;
            _target = new Vector3(areaCenter.x + r.x, transform.position.y, areaCenter.z + r.y);
            _retargetTime = Time.time + Random.Range(4f, 9f);
        }
    }

    // =====================================================================
    //  SpinAround — purely cosmetic, spins drone rotors.
    // =====================================================================
    [AddComponentMenu("NATO C2/Spin Around (Demo)")]
    public class SpinAround : MonoBehaviour
    {
        public float rpm = 1500f;
        public Vector3 axis = Vector3.up;
        private void Update() => transform.Rotate(axis, rpm * 6f * Time.deltaTime, Space.Self);
    }

    // =====================================================================
    //  DemoCameraController — WASD pan, mousewheel zoom, MMB orbit.
    // =====================================================================
    [AddComponentMenu("NATO C2/Demo Camera Controller")]
    public class DemoCameraController : MonoBehaviour
    {
        [Header("Pan (m/s)")]
        public float panSpeed = 90f;
        public float fastMultiplier = 2.5f;
        [Header("Zoom")]
        public float zoomSpeed = 90f;
        public float minHeight = 12f;
        public float maxHeight = 260f;
        [Header("Orbit")]
        public float orbitSpeed = 220f;
        [Header("Edge scrolling")]
        public bool  enableEdgeScroll = true;
        [Tooltip("Pixels from screen edge that trigger edge-scroll.")]
        public float edgeMargin = 18f;

        private float _pitch = 50f;
        private float _yaw   = 0f;
        private Vector3 _panVelocity;

        private void Start()
        {
            var e = transform.rotation.eulerAngles;
            _pitch = e.x; _yaw = e.y;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float speed = panSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);

            Vector3 fwd = transform.forward; fwd.y = 0; fwd.Normalize();
            Vector3 right = transform.right; right.y = 0; right.Normalize();

            Vector3 wishDir = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    wishDir += fwd;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  wishDir -= fwd;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  wishDir -= right;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) wishDir += right;

            // Edge scroll — only counts if the cursor is INSIDE the game window.
            if (enableEdgeScroll && Application.isFocused)
            {
                Vector3 m = Input.mousePosition;
                if (m.x >= 0 && m.x <= Screen.width && m.y >= 0 && m.y <= Screen.height)
                {
                    if (m.x <= edgeMargin)              wishDir -= right;
                    if (m.x >= Screen.width - edgeMargin)  wishDir += right;
                    if (m.y <= edgeMargin)              wishDir -= fwd;
                    if (m.y >= Screen.height - edgeMargin) wishDir += fwd;
                }
            }

            // Smooth pan velocity so direction changes don't snap.
            Vector3 target = wishDir.sqrMagnitude > 0.01f ? wishDir.normalized * speed : Vector3.zero;
            _panVelocity = Vector3.Lerp(_panVelocity, target, dt * 12f);
            transform.position += _panVelocity * dt;

            // Mousewheel zoom — speed scales with current altitude for parallax that feels right.
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.001f)
            {
                Vector3 p = transform.position;
                float altFactor = Mathf.Clamp01((p.y - minHeight) / (maxHeight - minHeight));
                float dy = wheel * zoomSpeed * (0.4f + altFactor * 1.6f);
                p.y = Mathf.Clamp(p.y - dy, minHeight, maxHeight);
                transform.position = p;
            }

            if (Input.GetMouseButton(2))
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSpeed * dt;
                _pitch  = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * orbitSpeed * dt, 20f, 85f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }
        }
    }
}
