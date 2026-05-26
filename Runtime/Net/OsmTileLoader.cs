// =====================================================================
//  NATO C2 RTS Hybrid — OsmTileLoader.cs
//  ---------------------------------------------------------------------
//  Fetches a grid of OpenStreetMap raster tiles (slippy-map XYZ format),
//  stitches them into one Texture2D, and applies it to a target
//  Renderer's material. Free, no API key, used by countless GIS and
//  military C2 demos as a baseline raster basemap.
//
//  Real NATO/coalition systems (TAK, SitaWare HQ, FalconView, etc.) use
//  the SAME WMTS / XYZ tile protocol — they just point it at NGA's
//  classified tile server (NGA Map, MGCP) or commercial Maxar feeds
//  instead of tile.openstreetmap.org. Swapping endpoints is a one-line
//  config change. The math, projection (Web Mercator EPSG:3857), and
//  z/x/y addressing are identical across providers.
//
//  Threading: tile downloads run on Unity's UnityWebRequest worker;
//  blit + Apply happens on the main thread inside the coroutine.
//
//  OSM tile usage policy (must comply):
//      • Set a descriptive User-Agent identifying this app.
//      • Don't hammer the server. We fetch up to 9 tiles once on startup.
//      • For sustained / production use, host your own tile server or use
//        a paid provider (MapTiler, Mapbox, Stadia, ESRI).
//
//  Production TODO: classified deployment swaps the URL template to
//  NGA Map (https://nga.map/x/{z}/{x}/{y}.png with smart-card auth) and
//  enables STANAG 4774 confidentiality labels on the cached tiles.
// =====================================================================

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/OSM Tile Loader")]
    public class OsmTileLoader : MonoBehaviour
    {
        [Header("Geographic anchor")]
        [Tooltip("Center latitude in decimal degrees (matches LocalSimFeed origin by default).")]
        public double centerLat = 38.7400;
        [Tooltip("Center longitude in decimal degrees.")]
        public double centerLon = 22.2540;
        [Tooltip("OSM zoom level (0 = whole earth, 19 = building level). 15-17 is good for tactical AOs.")]
        [Range(0, 19)]
        public int zoom = 16;
        [Tooltip("Tiles per side. 3 = 3x3 = 9 tiles. Keep odd so the center tile is centered.")]
        public int gridSize = 3;

        [Header("Tile provider")]
        [Tooltip("Pre-baked basemap styles. Satellite is what Anduril Lattice uses (Mapbox Satellite); ESRI is the free equivalent. OSM is street-map. Custom = use urlTemplate below.")]
        public BasemapStyle style = BasemapStyle.SatelliteEsri;
        [Tooltip("Override URL template when style==Custom. Tokens: {z}/{x}/{y} (some providers use {z}/{y}/{x} — see CustomYBeforeX).")]
        public string urlTemplate = "";
        [Tooltip("Some providers (ESRI) use {z}/{y}/{x} order — flip if your custom URL needs that.")]
        public bool customYBeforeX = false;
        [Tooltip("Required by OSM ToS. Identify your app, contact, version.")]
        public string userAgent = "NATO-C2-RTS-Hybrid/0.1 (demo; +https://github.com/anthropic)";

        public enum BasemapStyle
        {
            /// <summary>Mapbox / ATAK-style satellite imagery (free via ESRI World Imagery — best Lattice look).</summary>
            SatelliteEsri,
            /// <summary>ESRI Topographic — terrain contours + minor roads. Sits between sat + street.</summary>
            TopoEsri,
            /// <summary>OpenStreetMap street map. Good for urban ops where the roads matter.</summary>
            StreetOsm,
            /// <summary>Carto Dark — minimal dark-mode street map. Highlights tracks/markers cleanly.</summary>
            DarkCarto,
            /// <summary>Stamen Terrain — shaded relief + topo. Good for rural ops.</summary>
            Terrain,
            /// <summary>NGA Map / Maxar / any private XYZ. Set urlTemplate + customYBeforeX manually.</summary>
            Custom,
        }

        // Map style → URL template. ESRI's tile order is {z}/{y}/{x} (different from OSM!).
        private string EffectiveUrlTemplate()
        {
            switch (style)
            {
                case BasemapStyle.SatelliteEsri:
                    // ESRI World Imagery — free, no key, high-res satellite (Maxar/Vivid/Pléiades)
                    return "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
                case BasemapStyle.TopoEsri:
                    return "https://services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}";
                case BasemapStyle.StreetOsm:
                    return "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
                case BasemapStyle.DarkCarto:
                    return "https://a.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png";
                case BasemapStyle.Terrain:
                    return "https://tiles.stadiamaps.com/tiles/stamen_terrain/{z}/{x}/{y}.png";
                case BasemapStyle.Custom:
                default:
                    return string.IsNullOrEmpty(urlTemplate)
                        ? "https://tile.openstreetmap.org/{z}/{x}/{y}.png"
                        : urlTemplate;
            }
        }

        // ESRI uses {z}/{y}/{x}. OSM and Carto use {z}/{x}/{y}.
        private bool YBeforeX()
        {
            return style == BasemapStyle.SatelliteEsri ||
                   style == BasemapStyle.TopoEsri ||
                   (style == BasemapStyle.Custom && customYBeforeX);
        }

        [Header("Target")]
        [Tooltip("Renderer whose material's mainTexture will be set once stitching completes.")]
        public Renderer target;
        [Tooltip("If true, on any failure fall back to whatever the material had before (procedural texture).")]
        public bool fallbackOnFailure = true;

        public event Action<Texture2D> OnLoaded;
        public event Action<string>    OnFailed;

        private const int TileSize = 256;

        public void Begin()
        {
            if (target == null)
            {
                Debug.LogError("[OsmTileLoader] No target Renderer assigned.");
                OnFailed?.Invoke("no target");
                return;
            }
            StartCoroutine(FetchAndStitch());
        }

        /// <summary>Hot-swap the basemap style at runtime and re-fetch.</summary>
        public void SetStyle(BasemapStyle s)
        {
            style = s;
            Begin();
        }

        private void Start()
        {
            if (target != null) Begin();
        }

        // ---------- Web Mercator slippy-map math ------------------------
        // Reference: https://wiki.openstreetmap.org/wiki/Slippy_map_tilenames

        public static (int x, int y) LatLonToTile(double lat, double lon, int z)
        {
            double n = Math.Pow(2.0, z);
            int xtile = (int)Math.Floor((lon + 180.0) / 360.0 * n);
            double latRad = lat * Math.PI / 180.0;
            int ytile = (int)Math.Floor(
                (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n
            );
            return (xtile, ytile);
        }

        // ---------- Coroutine -------------------------------------------

        private IEnumerator FetchAndStitch()
        {
            int half = gridSize / 2;
            var (cx, cy) = LatLonToTile(centerLat, centerLon, zoom);

            int atlasSize = TileSize * gridSize;
            var atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false);
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.filterMode = FilterMode.Trilinear;
            atlas.name = $"OSM_z{zoom}_x{cx}_y{cy}_g{gridSize}";

            int fetched = 0, failed = 0;

            for (int gy = 0; gy < gridSize; gy++)
            {
                for (int gx = 0; gx < gridSize; gx++)
                {
                    int tx = cx - half + gx;
                    // OSM tile Y increases southward — invert so north is up in Unity (+Z).
                    int ty = cy - half + (gridSize - 1 - gy);

                    string url = EffectiveUrlTemplate()
                        .Replace("{z}", zoom.ToString())
                        .Replace("{x}", tx.ToString())
                        .Replace("{y}", ty.ToString());

                    using (var req = UnityWebRequestTexture.GetTexture(url))
                    {
                        req.SetRequestHeader("User-Agent", userAgent);
                        req.timeout = 8; // seconds

                        yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                        bool ok = req.result == UnityWebRequest.Result.Success;
#else
                        bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                        if (!ok)
                        {
                            failed++;
                            Debug.LogWarning($"[OsmTileLoader] Tile fetch failed {url} → {req.error}");
                            continue;
                        }

                        var tile = DownloadHandlerTexture.GetContent(req);
                        if (tile == null || tile.width != TileSize || tile.height != TileSize)
                        {
                            failed++;
                            Debug.LogWarning($"[OsmTileLoader] Tile bad size from {url}");
                            continue;
                        }

                        var pixels = tile.GetPixels32();
                        atlas.SetPixels32(gx * TileSize, gy * TileSize, TileSize, TileSize, pixels);
                        fetched++;
                        Destroy(tile);
                    }
                }
            }

            if (failed > 0 && fallbackOnFailure)
            {
                Debug.LogWarning($"[OsmTileLoader] {failed}/{fetched + failed} tiles failed — keeping fallback texture.");
                OnFailed?.Invoke($"{failed} tiles failed");
                Destroy(atlas);
                yield break;
            }

            atlas.Apply(true, false);

            // Swap the texture in. We keep the same material so all other
            // properties (gloss, color, repeat) stay intact.
            var mat = target.material; // instances per-renderer; safe to mutate.
            mat.mainTexture = atlas;
            // One atlas covers the whole ground plane → tile once, no repeat.
            mat.mainTextureScale = Vector2.one;
            mat.mainTextureOffset = Vector2.zero;

            Debug.Log($"[OsmTileLoader] Stitched {gridSize}x{gridSize} OSM tiles at z={zoom} center=({centerLat:F4},{centerLon:F4})");
            OnLoaded?.Invoke(atlas);
        }
    }
}
