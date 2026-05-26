// =====================================================================
//  NATO C2 RTS Hybrid — MilsymbolBridge.cs
//  ---------------------------------------------------------------------
//  C# ↔ JavaScript bridge to the Milsymbol library
//  (https://github.com/spatialillusions/milsymbol) for rendering true
//  APP-6E / MIL-STD-2525E symbols on WebGL. On every other platform —
//  and inside the Editor — the bridge falls back to a HIGH-FIDELITY
//  procedural renderer that draws the standard NATO frame + unit
//  pictogram + echelon amplifier with 2× supersampling for crisp edges.
//
//  Pictograms implemented (APP-6E unit type icons inside the frame):
//      Tank         — filled lozenge oval (Land Armor)
//      UGV          — wheeled vehicle silhouette
//      Drone (UAV)  — quadcopter with rotor circles
//      Helicopter   — 3-blade rotor cross
//      Infantry     — X
//      Vehicle      — wheeled vehicle outline
//      Ship         — surface hull
//      Unknown      — ?
// =====================================================================

using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace NATO.C2
{
    [AddComponentMenu("NATO C2/Milsymbol Bridge")]
    public class MilsymbolBridge : MonoBehaviour
    {
        public static MilsymbolBridge Instance { get; private set; }

        [Header("Rendering")]
        [Tooltip("Pixel size of generated symbol textures. 128 is the recommended baseline.")]
        [Min(64)] public int symbolPixelSize = 128;
        [Tooltip("Supersampling multiplier applied during rendering — output is downsampled to symbolPixelSize for anti-aliased edges.")]
        [Range(1, 4)] public int supersampling = 2;

        private readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>(256);

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern string MilsymbolRender(string sidc, string optsJson);
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy() { if (Instance == this) Instance = null; }

        // =================================================================
        //  Public — resolve a Texture2D for the given Agent. Cached.
        // =================================================================
        public Texture2D ResolveSymbol(Agent a)
        {
            if (a == null) return null;
            string sidc = a.ResolveSIDC();
            string key  = BuildCacheKey(sidc, a);
            if (_cache.TryGetValue(key, out var tex) && tex != null) return tex;

            tex = RenderForKey(sidc, a);
            _cache[key] = tex;
            return tex;
        }

        // ---------- cache key ------------------------------------------
        private static string BuildCacheKey(string sidc, Agent a)
        {
            var sb = new StringBuilder(64);
            sb.Append(sidc).Append('|')
              .Append((int)a.affiliation).Append('|')
              .Append((int)a.unitType).Append('|')
              .Append(a.echelon).Append('|')
              .Append(a.quantity).Append('|')
              .Append(a.reinforcedDetached).Append('|')
              .Append(a.callsign);
            return sb.ToString();
        }

        // =================================================================
        //  Renderer — JS path for WebGL, placeholder elsewhere.
        // =================================================================
        private Texture2D RenderForKey(string sidc, Agent a)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                string opts = JsonUtility.ToJson(new MilsymbolOptions
                {
                    size = symbolPixelSize,
                    quantity = a.quantity,
                    reinforcedReduced = a.reinforcedDetached,
                    uniqueDesignation = a.callsign,
                    higherFormation = a.higherFormation,
                    echelon = a.echelon
                });
                string base64 = MilsymbolRender(sidc, opts);
                if (!string.IsNullOrEmpty(base64))
                {
                    var data = System.Convert.FromBase64String(base64);
                    var tex2 = new Texture2D(symbolPixelSize, symbolPixelSize, TextureFormat.RGBA32, false);
                    if (tex2.LoadImage(data)) return tex2;
                }
            }
            catch (System.Exception ex) { Debug.LogException(ex, this); }
#endif
            return BuildPlaceholder(sidc, a);
        }

        [System.Serializable] private struct MilsymbolOptions
        {
            public int size;
            public int quantity;
            public string reinforcedReduced;
            public string uniqueDesignation;
            public string higherFormation;
            public int echelon;
        }

        // =================================================================
        //  Placeholder renderer with proper APP-6E pictograms + 2× SS.
        // =================================================================
        private Texture2D BuildPlaceholder(string sidc, Agent a)
        {
            int outSize = symbolPixelSize;
            int ss = Mathf.Max(1, supersampling);
            int s = outSize * ss;

            var px = new Color32[s * s];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);

            Color32 line   = NATOPalette.For(a.affiliation);
            // ~20% alpha interior fill — subtle enough not to dominate when the
            // symbol is shown as a thumbnail on a UI panel, strong enough to
            // read affiliation at battle-map zoom.
            Color32 fill   = new Color32(line.r, line.g, line.b, (byte)52);
            Color32 lineSoft = new Color32(line.r, line.g, line.b, (byte)170);

            // -- 1. FRAME (affiliation shape) ---------------------------
            switch (a.affiliation)
            {
                case Affiliation.Friendly: DrawRectFrame(px, s, line, fill); break;
                case Affiliation.Hostile:  DrawDiamondFrame(px, s, line, fill); break;
                case Affiliation.Neutral:  DrawSquareFrame(px, s, line, fill); break;
                default:                    DrawCircleFrame(px, s, line, fill); break;
            }

            // -- 2. UNIT PICTOGRAM (APP-6E unit type icon, centered) ----
            switch (a.unitType)
            {
                case UnitType.Tank:       DrawArmorPictogram(px, s, line); break;
                case UnitType.UGV:        DrawUGVPictogram(px, s, line); break;
                case UnitType.Drone:      DrawUAVPictogram(px, s, line); break;
                case UnitType.Helicopter: DrawHelicopterPictogram(px, s, line); break;
                case UnitType.Infantry:   DrawInfantryPictogram(px, s, line); break;
                case UnitType.Vehicle:    DrawVehiclePictogram(px, s, line); break;
                case UnitType.Ship:       DrawShipPictogram(px, s, line); break;
                default:                  DrawUnknownPictogram(px, s, line); break;
            }

            // -- 3. ECHELON AMPLIFIER (top edge) ------------------------
            DrawEchelonAmplifier(px, s, line, a.echelon);

            // -- 4. DOWNSAMPLE (box filter average) ---------------------
            Color32[] outPx = (ss == 1) ? px : Downsample(px, s, outSize);

            var tex = new Texture2D(outSize, outSize, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(outPx);
            tex.Apply(false);
            tex.name = $"MS_{sidc}";
            return tex;
        }

        // =================================================================
        //  FRAMES
        // =================================================================
        private static void DrawRectFrame(Color32[] px, int s, Color32 line, Color32 fill)
        {
            int inset = s / 8;
            FillRect(px, s, inset, inset, s - inset, s - inset, fill);
            DrawRectOutline(px, s, inset, inset, s - inset, s - inset, line, Mathf.Max(1, s / 64));
        }
        private static void DrawSquareFrame(Color32[] px, int s, Color32 line, Color32 fill) => DrawRectFrame(px, s, line, fill);

        private static void DrawDiamondFrame(Color32[] px, int s, Color32 line, Color32 fill)
        {
            int cx = s / 2, cy = s / 2, r = s * 7 / 16;
            // Filled diamond via manhattan distance.
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    if (Mathf.Abs(x - cx) + Mathf.Abs(y - cy) <= r) px[y * s + x] = fill;
            // Outline: 4 thick line segments.
            int t = Mathf.Max(1, s / 64);
            DrawThickLine(px, s, cx, cy - r, cx + r, cy, line, t);
            DrawThickLine(px, s, cx + r, cy, cx, cy + r, line, t);
            DrawThickLine(px, s, cx, cy + r, cx - r, cy, line, t);
            DrawThickLine(px, s, cx - r, cy, cx, cy - r, line, t);
        }

        private static void DrawCircleFrame(Color32[] px, int s, Color32 line, Color32 fill)
        {
            int cx = s / 2, cy = s / 2, r = s * 7 / 16;
            FillCircle(px, s, cx, cy, r, fill);
            DrawCircleOutline(px, s, cx, cy, r, line, Mathf.Max(1, s / 64));
        }

        // =================================================================
        //  PICTOGRAMS (APP-6E unit type icons inside the frame)
        // =================================================================
        private static void DrawArmorPictogram(Color32[] px, int s, Color32 c)
        {
            // Land Armor: filled lozenge oval, horizontal.
            int cx = s / 2, cy = s / 2;
            int rx = s * 5 / 16;
            int ry = s / 8;
            FillEllipse(px, s, cx, cy, rx, ry, c);
        }

        private static void DrawUGVPictogram(Color32[] px, int s, Color32 c)
        {
            // Vehicle hull + tracks.
            int cx = s / 2, cy = s / 2;
            int hw = s / 4, hh = s / 10;
            int t = Mathf.Max(2, s / 50);
            DrawRectOutline(px, s, cx - hw, cy - hh, cx + hw, cy + hh, c, t);
            // Tracks (parallel lines top + bottom).
            DrawThickLine(px, s, cx - hw - s/40, cy - hh - s/24, cx + hw + s/40, cy - hh - s/24, c, t);
            DrawThickLine(px, s, cx - hw - s/40, cy + hh + s/24, cx + hw + s/40, cy + hh + s/24, c, t);
        }

        private static void DrawUAVPictogram(Color32[] px, int s, Color32 c)
        {
            // Quadcopter: X-arms with rotor circles at the four tips.
            int cx = s / 2, cy = s / 2;
            int arm = s / 4;
            int t   = Mathf.Max(2, s / 60);
            int rot = s / 14;
            int rotT = Mathf.Max(1, s / 80);

            int x1 = cx - arm, y1 = cy - arm;
            int x2 = cx + arm, y2 = cy + arm;
            int x3 = cx + arm, y3 = cy - arm;
            int x4 = cx - arm, y4 = cy + arm;

            DrawThickLine(px, s, x1, y1, x2, y2, c, t);
            DrawThickLine(px, s, x3, y3, x4, y4, c, t);

            FillCircle(px, s, cx, cy, s / 22, c);

            DrawCircleOutline(px, s, x1, y1, rot, c, rotT);
            DrawCircleOutline(px, s, x2, y2, rot, c, rotT);
            DrawCircleOutline(px, s, x3, y3, rot, c, rotT);
            DrawCircleOutline(px, s, x4, y4, rot, c, rotT);
        }

        private static void DrawHelicopterPictogram(Color32[] px, int s, Color32 c)
        {
            // 3-blade rotor + hub.
            int cx = s / 2, cy = s / 2;
            int len = s / 4;
            int t   = Mathf.Max(2, s / 50);
            for (int i = 0; i < 3; i++)
            {
                float ang = (Mathf.PI * 2f / 3f) * i - Mathf.PI / 2f;
                int ex = cx + Mathf.RoundToInt(Mathf.Cos(ang) * len);
                int ey = cy + Mathf.RoundToInt(Mathf.Sin(ang) * len);
                DrawThickLine(px, s, cx, cy, ex, ey, c, t);
                FillCircle(px, s, ex, ey, s / 28, c);
            }
            FillCircle(px, s, cx, cy, s / 20, c);
        }

        private static void DrawInfantryPictogram(Color32[] px, int s, Color32 c)
        {
            // X across interior.
            int i = s * 5 / 16;
            int t = Mathf.Max(3, s / 40);
            DrawThickLine(px, s, i, i, s - i, s - i, c, t);
            DrawThickLine(px, s, i, s - i, s - i, i, c, t);
        }

        private static void DrawVehiclePictogram(Color32[] px, int s, Color32 c)
        {
            // Wheeled vehicle: hull rectangle + two wheels underneath.
            int cx = s / 2, cy = s / 2;
            int hw = s / 4, hh = s / 12;
            int t  = Mathf.Max(2, s / 50);
            DrawRectOutline(px, s, cx - hw, cy - hh, cx + hw, cy + hh, c, t);
            FillCircle(px, s, cx - hw + s / 20, cy + hh + s / 30, s / 28, c);
            FillCircle(px, s, cx + hw - s / 20, cy + hh + s / 30, s / 28, c);
        }

        private static void DrawShipPictogram(Color32[] px, int s, Color32 c)
        {
            // Surface hull: trapezoid pointing up.
            int cx = s / 2, cy = s / 2;
            int hw = s / 4, hh = s / 10;
            int t  = Mathf.Max(2, s / 50);
            DrawThickLine(px, s, cx - hw, cy - hh, cx + hw, cy - hh, c, t); // deck
            DrawThickLine(px, s, cx + hw, cy - hh, cx + hw / 2, cy + hh, c, t); // stbd
            DrawThickLine(px, s, cx + hw / 2, cy + hh, cx - hw / 2, cy + hh, c, t); // bow
            DrawThickLine(px, s, cx - hw / 2, cy + hh, cx - hw, cy - hh, c, t); // port
        }

        private static void DrawUnknownPictogram(Color32[] px, int s, Color32 c)
        {
            // Single dot in center.
            FillCircle(px, s, s / 2, s / 2, s / 22, c);
        }

        // =================================================================
        //  ECHELON AMPLIFIER (top edge): dots (Team/Squad/Section),
        //  bars (Platoon/Company/Battalion), X-marks (Brigade/Division).
        // =================================================================
        private static void DrawEchelonAmplifier(Color32[] px, int s, Color32 c, int echelon)
        {
            int top = s / 32;
            int gap = s / 14;
            int baseX = s / 2;
            int n; bool dots; bool xMark = false;
            switch (echelon)
            {
                case 11: n = 1; dots = true; break;
                case 12: n = 2; dots = true; break;
                case 13: n = 3; dots = true; break;
                case 14: n = 1; dots = false; break;
                case 15: n = 2; dots = false; break;
                case 16: n = 3; dots = false; break;
                case 17: n = 1; dots = false; xMark = true; break;
                case 18: n = 2; dots = false; xMark = true; break;
                default: n = 0; dots = false; break;
            }
            int total = (n - 1) * gap;
            int dotR  = Mathf.Max(2, s / 50);
            int barH  = s / 18;
            int barW  = Mathf.Max(2, s / 50);

            for (int i = 0; i < n; i++)
            {
                int x = baseX - total / 2 + i * gap;
                if (dots)
                {
                    FillCircle(px, s, x, top + dotR, dotR, c);
                }
                else if (xMark)
                {
                    int xt = Mathf.Max(2, s / 60);
                    DrawThickLine(px, s, x - barW * 2, top, x + barW * 2, top + barH, c, xt);
                    DrawThickLine(px, s, x + barW * 2, top, x - barW * 2, top + barH, c, xt);
                }
                else
                {
                    FillRect(px, s, x - barW / 2, top, x + barW / 2 + 1, top + barH, c);
                }
            }
        }

        // =================================================================
        //  PRIMITIVE RASTERIZERS
        // =================================================================
        private static void FillRect(Color32[] px, int s, int x0, int y0, int x1, int y1, Color32 c)
        {
            int xa = Mathf.Max(0, x0), xb = Mathf.Min(s, x1);
            int ya = Mathf.Max(0, y0), yb = Mathf.Min(s, y1);
            for (int y = ya; y < yb; y++)
                for (int x = xa; x < xb; x++)
                    px[y * s + x] = c;
        }

        private static void DrawRectOutline(Color32[] px, int s, int x0, int y0, int x1, int y1, Color32 c, int t)
        {
            for (int k = 0; k < t; k++)
            {
                DrawLine(px, s, x0 + k, y0 + k, x1 - k, y0 + k, c);
                DrawLine(px, s, x0 + k, y1 - k, x1 - k, y1 - k, c);
                DrawLine(px, s, x0 + k, y0 + k, x0 + k, y1 - k, c);
                DrawLine(px, s, x1 - k, y0 + k, x1 - k, y1 - k, c);
            }
        }

        private static void FillCircle(Color32[] px, int s, int cx, int cy, int r, Color32 c)
        {
            int r2 = r * r;
            int xa = Mathf.Max(0, cx - r), xb = Mathf.Min(s - 1, cx + r);
            int ya = Mathf.Max(0, cy - r), yb = Mathf.Min(s - 1, cy + r);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2) px[y * s + x] = c;
        }

        private static void DrawCircleOutline(Color32[] px, int s, int cx, int cy, int r, Color32 c, int t = 1)
        {
            // Stroked annulus.
            int rInner = r - t;
            int r2 = r * r, ri2 = rInner * rInner;
            int xa = Mathf.Max(0, cx - r), xb = Mathf.Min(s - 1, cx + r);
            int ya = Mathf.Max(0, cy - r), yb = Mathf.Min(s - 1, cy + r);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 <= r2 && d2 >= ri2) px[y * s + x] = c;
                }
        }

        private static void FillEllipse(Color32[] px, int s, int cx, int cy, int rx, int ry, Color32 c)
        {
            int rx2 = rx * rx, ry2 = ry * ry;
            long denom = (long)rx2 * ry2;
            int xa = Mathf.Max(0, cx - rx), xb = Mathf.Min(s - 1, cx + rx);
            int ya = Mathf.Max(0, cy - ry), yb = Mathf.Min(s - 1, cy + ry);
            for (int y = ya; y <= yb; y++)
            {
                int dy = y - cy;
                for (int x = xa; x <= xb; x++)
                {
                    int dx = x - cx;
                    if ((long)dx * dx * ry2 + (long)dy * dy * rx2 <= denom) px[y * s + x] = c;
                }
            }
        }

        private static void DrawLine(Color32[] px, int s, int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx =  Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && x0 < s && y0 >= 0 && y0 < s) px[y0 * s + x0] = c;
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static void DrawThickLine(Color32[] px, int s, int x0, int y0, int x1, int y1, Color32 c, int thickness)
        {
            int half = thickness / 2;
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++)
                    DrawLine(px, s, x0 + dx, y0 + dy, x1 + dx, y1 + dy, c);
        }

        // =================================================================
        //  SUPERSAMPLE → DOWNSAMPLE (box filter)
        // =================================================================
        private static Color32[] Downsample(Color32[] src, int srcSize, int dstSize)
        {
            int ss = srcSize / dstSize;
            var dst = new Color32[dstSize * dstSize];
            for (int y = 0; y < dstSize; y++)
            {
                for (int x = 0; x < dstSize; x++)
                {
                    int rSum = 0, gSum = 0, bSum = 0, aSum = 0;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        for (int sx = 0; sx < ss; sx++)
                        {
                            var p = src[(y * ss + sy) * srcSize + (x * ss + sx)];
                            rSum += p.r; gSum += p.g; bSum += p.b; aSum += p.a;
                        }
                    }
                    int n = ss * ss;
                    dst[y * dstSize + x] = new Color32(
                        (byte)(rSum / n), (byte)(gSum / n),
                        (byte)(bSum / n), (byte)(aSum / n));
                }
            }
            return dst;
        }
    }
}
