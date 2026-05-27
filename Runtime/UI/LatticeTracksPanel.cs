// =====================================================================
//  NATO C2 RTS Hybrid — LatticeTracksPanel.cs
//  ---------------------------------------------------------------------
//  Anduril-Lattice-inspired left-side detection feed. Renders one row per
//  recent detection / contact / inbound CoT track:
//
//      ┌──────────────────────────────────────────┐
//      │  Filters ▾   Last 24 Hours · 7 Assets    │
//      ├──────────────────────────────────────────┤
//      │ [thumb] 13:25 EDT                ⭐  Hide│
//      │         Person                            │
//      ├──────────────────────────────────────────┤
//      │ [thumb] 13:18 EDT                ⭐  Hide│
//      │         Person                            │
//      └──────────────────────────────────────────┘
//
//  Sources:
//      • EngagementTracker.Active     — friendly↔hostile contacts.
//      • CotTrackPanel (FeedHub.OnCot) — foreign inbound tracks.
//
//  We don't render real thumbnail textures here (the drone-camera frame
//  grabber is a future PR — Anduril uses captured EO/IR frames at the
//  moment of detection). Each row gets a placeholder thumbnail tinted
//  by the detection class (cyan for friendly, red for hostile, etc).
//
//  Yellow ⭐ flips a "starred" boolean (favourite / send-to-collection).
//  Hide drops the entry from the feed. Both are local-only; production
//  syncs via CoT marker classes with `_flow-tags_` extensions.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(118)]
    [AddComponentMenu("NATO C2/Lattice Tracks Panel")]
    public class LatticeTracksPanel : MonoBehaviour
    {
        [Header("Layout")]
        public int   panelWidth   = 320;
        public float topOffset    = 60f;   // sits below the LatticeTopBar
        public float bottomOffset = 80f;   // leaves room for radio chat
        public int   maxRows      = 12;

        // ---------- runtime ------------------------------------------
        private RectTransform _root;
        private RectTransform _list;
        private Text _filterChip;
        private FeedHub _hub;
        private NATO.C2.EngagementTracker _engage;
        private readonly Dictionary<string, TrackRow> _rows = new Dictionary<string, TrackRow>();
        private readonly Queue<string> _order = new Queue<string>();

        private void Awake() { BuildContainer(); }

        private void Start()
        {
            _hub = FeedHub.Instance;
            _engage = FindAnyObjectByType<NATO.C2.EngagementTracker>();
            if (_hub != null) _hub.OnCot += HandleCot;
        }

        private void OnDestroy()
        {
            if (_hub != null) _hub.OnCot -= HandleCot;
        }

        private void Update()
        {
            // Poll engagement tracker for new contacts (every frame is fine —
            // EngagementTracker.Active is tiny).
            if (_engage != null)
            {
                for (int i = 0; i < _engage.Active.Count; i++)
                {
                    var ev = _engage.Active[i];
                    string uid = "CONTACT-" + ev.hostileCallsign + "@" + Mathf.FloorToInt(ev.startedAt / 10f);
                    if (_rows.ContainsKey(uid)) continue;

                    // Snap a thumbnail from the drone PIP RenderTexture so the
                    // row shows real EO/IR imagery rather than a tinted block.
                    var thumb = SnapshotPipFrame();
                    AddRow(uid, ev.hostileCallsign, "Ground Vehicle",
                           detectedBy: "Camera",
                           tint: new Color(1f, 0.42f, 0.42f),
                           thumbnail: thumb);
                }
            }

            if (_filterChip != null)
                _filterChip.text = $"Last 24 Hours · {_rows.Count} Asset" + (_rows.Count == 1 ? "" : "s");
        }

        // ---------- inbound CoT → row -------------------------------
        private void HandleCot(CotEvent ev)
        {
            if (string.IsNullOrEmpty(ev.uid)) return;
            if (ev.uid.StartsWith("NATO-C2-")) return; // skip own
            if (_rows.ContainsKey(ev.uid)) return;

            string callsign = ExtractCallsign(ev.xmlDetail) ?? ev.uid;
            string label = ClassifyCoT(ev.type);
            Color tint = ColourFor(ev.type);
            AddRow(ev.uid, callsign, label, detectedBy: "Datalink", tint,
                   thumbnail: SnapshotPipFrame());
        }

        // ---------- thumbnail grabber --------------------------------
        //  Asynchronously read the active drone PIP's RenderTexture into a
        //  small Texture2D, return it as a Sprite. Returns null if the PIP
        //  isn't built yet (e.g. earliest contact happens before the camera
        //  spins up). Cheap — one ReadPixels per detection, not per frame.
        private DronePipPanel _pip;
        private Sprite SnapshotPipFrame()
        {
            _pip ??= FindAnyObjectByType<DronePipPanel>();
            if (_pip == null || _pip.VideoImage == null) return null;
            var rt = _pip.VideoImage.texture as RenderTexture;
            if (rt == null) return null;

            // Downsample to 128×96 for memory friendliness.
            const int W = 128, H = 96;
            var snap = new Texture2D(W, H, TextureFormat.RGB24, false);
            var prev = RenderTexture.active;
            // Blit the PIP RT down to a tmp RT at our target size first so
            // ReadPixels grabs the resized frame.
            var tmp = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            snap.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            snap.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);

            return Sprite.Create(snap, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f));
        }

        private static string ClassifyCoT(string cotType)
        {
            if (string.IsNullOrEmpty(cotType)) return "Unknown";
            if (cotType.StartsWith("a-h-A")) return "Air Track";
            if (cotType.StartsWith("a-h-G")) return "Ground Vehicle";
            if (cotType.StartsWith("a-n-"))  return "Civilian";
            if (cotType.StartsWith("a-f-A")) return "Friendly Air";
            if (cotType.StartsWith("a-f-G")) return "Friendly Ground";
            if (cotType.StartsWith("b-r-f")) return "Fire Request";
            if (cotType.StartsWith("b-r-c")) return "Medical Request";
            if (cotType.StartsWith("b-m-p")) return "Waypoint";
            return "Track";
        }

        private static Color ColourFor(string cotType)
        {
            if (string.IsNullOrEmpty(cotType)) return Color.yellow;
            char ident = cotType.Length > 2 ? cotType[2] : 'u';
            return ident switch
            {
                'f' => new Color(0.30f, 0.85f, 1.00f),
                'h' => new Color(1.00f, 0.40f, 0.40f),
                'n' => new Color(0.40f, 1.00f, 0.55f),
                _   => new Color(1.00f, 0.92f, 0.30f)
            };
        }

        // ---------- container + row builders ------------------------
        private void BuildContainer()
        {
            var go = new GameObject("LatticeTracks",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0f, 0f);
            _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 0.5f);
            _root.sizeDelta = new Vector2(panelWidth, -(topOffset + bottomOffset));
            _root.anchoredPosition = new Vector2(0f, -(topOffset - bottomOffset) * 0.5f);
            go.GetComponent<Image>().color = new Color(0.043f, 0.078f, 0.188f, 0.88f);

            // 1-px cyan right border.
            var bord = new GameObject("Border",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bord.transform.SetParent(_root, false);
            var brt = bord.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(1f, 0f); brt.anchorMax = new Vector2(1f, 1f);
            brt.sizeDelta = new Vector2(1f, 0f);
            bord.GetComponent<Image>().color = new Color(0.063f, 0.878f, 1.0f, 0.30f);

            // Header: Filters ▾  + chip
            var header = new GameObject("Header",
                typeof(RectTransform), typeof(CanvasRenderer));
            header.transform.SetParent(_root, false);
            var hRt = header.GetComponent<RectTransform>();
            hRt.anchorMin = new Vector2(0f, 1f); hRt.anchorMax = new Vector2(1f, 1f);
            hRt.pivot = new Vector2(0.5f, 1f);
            hRt.sizeDelta = new Vector2(0f, 44f);

            var fil = new GameObject("Filters", typeof(RectTransform), typeof(Text));
            fil.transform.SetParent(header.transform, false);
            var fRt = fil.GetComponent<RectTransform>();
            fRt.anchorMin = new Vector2(0f, 0f); fRt.anchorMax = new Vector2(0f, 1f);
            fRt.pivot = new Vector2(0f, 0.5f); fRt.sizeDelta = new Vector2(96f, 0f);
            fRt.anchoredPosition = new Vector2(12f, 0f);
            var ft = fil.GetComponent<Text>();
            ft.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            ft.text = "Filters ▾"; ft.fontSize = 14;
            ft.color = new Color(0.85f, 0.92f, 1.0f);
            ft.alignment = TextAnchor.MiddleLeft;

            // Filter chip = colored background + Text label on a child GO.
            // (Image + Text on the same GameObject is rejected by Unity —
            // they both inherit Graphic and a GameObject can host only one.)
            var chipBg = new GameObject("FilterChipBg",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            chipBg.transform.SetParent(header.transform, false);
            var cRt = chipBg.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(0f, 0.5f); cRt.anchorMax = new Vector2(0f, 0.5f);
            cRt.pivot = new Vector2(0f, 0.5f);
            cRt.sizeDelta = new Vector2(180f, 26f);
            cRt.anchoredPosition = new Vector2(110f, 0f);
            chipBg.GetComponent<Image>().color = new Color(0.07f, 0.12f, 0.235f);

            var chipLbl = new GameObject("FilterChipLbl",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            chipLbl.transform.SetParent(chipBg.transform, false);
            var clRt = chipLbl.GetComponent<RectTransform>();
            clRt.anchorMin = Vector2.zero; clRt.anchorMax = Vector2.one;
            clRt.offsetMin = Vector2.zero; clRt.offsetMax = Vector2.zero;
            _filterChip = chipLbl.GetComponent<Text>();
            _filterChip.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _filterChip.text = "Last 24 Hours · 0 Assets";
            _filterChip.fontSize = 12;
            _filterChip.color = new Color(0.85f, 0.92f, 1.0f);
            _filterChip.alignment = TextAnchor.MiddleCenter;
            _filterChip.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Scrollable list area below the header.
            var list = new GameObject("List", typeof(RectTransform), typeof(VerticalLayoutGroup));
            list.transform.SetParent(_root, false);
            _list = list.GetComponent<RectTransform>();
            _list.anchorMin = new Vector2(0f, 0f); _list.anchorMax = new Vector2(1f, 1f);
            _list.offsetMin = new Vector2(8f, 8f);
            _list.offsetMax = new Vector2(-8f, -50f);
            var vlg = list.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.spacing = 6f;
            vlg.childControlWidth = true; vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        }

        private void AddRow(string uid, string callsign, string label,
                            string detectedBy, Color tint, Sprite thumbnail = null)
        {
            // Enforce row cap.
            while (_order.Count >= maxRows)
            {
                string oldest = _order.Dequeue();
                if (_rows.TryGetValue(oldest, out var r))
                {
                    if (r.root != null) Destroy(r.root.gameObject);
                    _rows.Remove(oldest);
                }
            }

            var go = new GameObject("Row_" + uid,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(_list, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(0f, 70f);
            go.GetComponent<Image>().color = new Color(0.06f, 0.10f, 0.22f, 0.85f);
            go.GetComponent<LayoutElement>().preferredHeight = 70f;

            // Thumbnail block (left, square) — placeholder tint.
            var thumb = new GameObject("Thumb",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            thumb.transform.SetParent(go.transform, false);
            var tRt = thumb.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0f, 0f); tRt.anchorMax = new Vector2(0f, 1f);
            tRt.pivot = new Vector2(0f, 0.5f); tRt.sizeDelta = new Vector2(76f, 0f);
            tRt.anchoredPosition = new Vector2(6f, 0f);
            // Outer tinted border…
            thumb.GetComponent<Image>().color = new Color(tint.r, tint.g, tint.b, 0.35f);
            // …with an inner fill (3 px inset).
            var thumbInner = new GameObject("ThumbInner",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            thumbInner.transform.SetParent(thumb.transform, false);
            var tiRt = thumbInner.GetComponent<RectTransform>();
            tiRt.anchorMin = Vector2.zero; tiRt.anchorMax = Vector2.one;
            tiRt.offsetMin = new Vector2(3f, 3f); tiRt.offsetMax = new Vector2(-3f, -3f);
            var thumbImg = thumbInner.GetComponent<Image>();
            if (thumbnail != null)
            {
                thumbImg.sprite = thumbnail;
                thumbImg.color = Color.white;
            }
            else
            {
                thumbImg.color = new Color(tint.r * 0.40f, tint.g * 0.40f, tint.b * 0.40f, 0.95f);
            }

            // "Detected using" overlay label — only shown when there's no
            // real thumbnail (otherwise the EO/IR frame speaks for itself).
            if (thumbnail == null)
            {
                var detected = new GameObject("DetectedUsing",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                detected.transform.SetParent(thumb.transform, false);
                var dRt = detected.GetComponent<RectTransform>();
                dRt.anchorMin = Vector2.zero; dRt.anchorMax = Vector2.one;
                dRt.offsetMin = Vector2.zero; dRt.offsetMax = Vector2.zero;
                var dT = detected.GetComponent<Text>();
                dT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                dT.text = "Detected\nusing:\n" + detectedBy;
                dT.fontSize = 10; dT.color = new Color(1f, 1f, 1f, 0.85f);
                dT.fontStyle = FontStyle.Bold;
                dT.alignment = TextAnchor.MiddleCenter; dT.raycastTarget = false;
                dT.horizontalOverflow = HorizontalWrapMode.Overflow;
                dT.verticalOverflow = VerticalWrapMode.Overflow;
            }

            // Time + label (centre)
            var info = new GameObject("Info",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            info.transform.SetParent(go.transform, false);
            var iRt = info.GetComponent<RectTransform>();
            iRt.anchorMin = new Vector2(0f, 0f); iRt.anchorMax = new Vector2(1f, 1f);
            iRt.offsetMin = new Vector2(94f, 8f);
            iRt.offsetMax = new Vector2(-70f, -8f);
            var iT = info.GetComponent<Text>();
            iT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            string time = DateTime.Now.ToString("HH:mm");
            iT.text = $"<b>{time}</b>  {label}\n<color=#9ab1c9>{callsign}</color>";
            iT.fontSize = 13; iT.color = Color.white;
            iT.supportRichText = true;
            iT.alignment = TextAnchor.MiddleLeft; iT.raycastTarget = false;

            // Star + Hide on the right.
            var star = new GameObject("Star", typeof(RectTransform), typeof(Text), typeof(Button));
            star.transform.SetParent(go.transform, false);
            var sRt = star.GetComponent<RectTransform>();
            sRt.anchorMin = new Vector2(1f, 1f); sRt.anchorMax = new Vector2(1f, 1f);
            sRt.pivot = new Vector2(1f, 1f); sRt.sizeDelta = new Vector2(22f, 22f);
            sRt.anchoredPosition = new Vector2(-10f, -8f);
            var sT = star.GetComponent<Text>();
            sT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            sT.text = "☆"; sT.fontSize = 18;
            sT.color = new Color(1f, 0.93f, 0.30f);
            sT.alignment = TextAnchor.MiddleCenter;
            bool starred = false;
            star.GetComponent<Button>().onClick.AddListener(() => {
                starred = !starred;
                sT.text = starred ? "★" : "☆";
            });

            var hide = new GameObject("Hide", typeof(RectTransform), typeof(Text), typeof(Button));
            hide.transform.SetParent(go.transform, false);
            var hRt = hide.GetComponent<RectTransform>();
            hRt.anchorMin = new Vector2(1f, 0f); hRt.anchorMax = new Vector2(1f, 0f);
            hRt.pivot = new Vector2(1f, 0f); hRt.sizeDelta = new Vector2(48f, 22f);
            hRt.anchoredPosition = new Vector2(-6f, 8f);
            var hT = hide.GetComponent<Text>();
            hT.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hT.text = "Hide"; hT.fontSize = 13;
            hT.color = new Color(1f, 0.45f, 0.55f);
            hT.fontStyle = FontStyle.Bold;
            hT.alignment = TextAnchor.MiddleRight;
            hide.GetComponent<Button>().onClick.AddListener(() => {
                Destroy(go);
                _rows.Remove(uid);
            });

            _rows[uid] = new TrackRow { uid = uid, root = rt };
            _order.Enqueue(uid);
            go.transform.SetAsFirstSibling(); // newest at top
        }

        // ---------- helpers ------------------------------------------
        private static string ExtractCallsign(string xmlDetail)
        {
            if (string.IsNullOrEmpty(xmlDetail)) return null;
            int i = xmlDetail.IndexOf("callsign=\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i += "callsign=\"".Length;
            int j = xmlDetail.IndexOf('"', i);
            return j > i ? xmlDetail.Substring(i, j - i) : null;
        }

        private class TrackRow
        {
            public string uid;
            public RectTransform root;
        }
    }
}
