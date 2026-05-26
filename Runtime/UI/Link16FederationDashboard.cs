// =====================================================================
//  NATO C2 RTS Hybrid — Link16FederationDashboard.cs
//  ---------------------------------------------------------------------
//  Lattice-style top-bar widget showing per-peer S5066 federation
//  health. One row per peer (operator station prefix) with:
//
//      • Status dot   — green if RX < 5s ago, amber 5–30s, red >30s
//      • Callsign     — peer's op prefix (e.g. "W1", "F3")
//      • Last RX      — seconds since most recent D_PDU
//      • RTT          — most recent ACK round-trip in ms
//      • Retries      — local ARQ retries (own counter)
//      • SREJ-rx      — SREJs this peer has sent us
//      • Gaps→        — gaps WE detected from them (SREJs we emitted)
//
//  Reads directly from Stanag5066FederationBridge.Peers, which the
//  bridge updates every inbound D_PDU.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(45)]
    [AddComponentMenu("NATO C2/Link 16 Federation Dashboard")]
    public class Link16FederationDashboard : MonoBehaviour
    {
        [Tooltip("If null, found on Start via FindAnyObjectByType.")]
        public Stanag5066FederationBridge bridge;

        [Header("Autoresolver — mode-flap storm detection")]
        [Tooltip("If null, found at Start.")]
        public LearnedModeAdvisor advisor;
        [Tooltip("Decisions/sec above which we declare a flap storm.")]
        [Range(0.5f, 20f)] public float flapDecisionsPerSecThreshold = 3f;
        [Tooltip("How long the high rate must persist before showing the auto-tune CTA.")]
        [Range(1f, 30f)] public float flapSustainedSec = 5f;

        [Header("Symptom detection (playbook integration)")]
        [Tooltip("SREJ-RX/sec from a single peer above which we surface the playbook §2 link.")]
        [Range(0.1f, 10f)] public float srejStormPerSecThreshold = 1f;
        [Tooltip("Peers simultaneously red within this window → mass drop, playbook §5.")]
        [Range(2f, 30f)] public float massDropWindowSec = 5f;
        [Tooltip("Peers with growing Gaps→ count in last window → gap storm, playbook §3.")]
        [Range(2, 10)] public int gapStormPeerCount = 3;

        [Tooltip("Anchor in the parent Canvas. (1,1) = top-right.")]
        public Vector2 anchor = new Vector2(1f, 1f);
        [Tooltip("Pixel offset from anchor (negative x = left, negative y = down).")]
        public Vector2 offset = new Vector2(-12f, -244f);

        [Tooltip("Width of the dashboard panel.")]
        public float panelWidth = 280f;
        [Tooltip("Row height per peer.")]
        public float rowHeight = 18f;

        [Tooltip("Refresh interval in seconds. Cheap so we can go fast.")]
        [Range(0.1f, 2f)] public float refreshEverySec = 0.5f;

        // ----- runtime -----
        private RectTransform _root;
        private Text          _header;
        private RectTransform _rowsHost;
        private float         _nextRefreshAt;
        // One row UI per peer. We pool / reuse them rather than re-creating.
        private readonly Dictionary<string, PeerRow> _rows = new Dictionary<string, PeerRow>();
        // Used to detect peers gone-away so we can hide their rows.
        private readonly HashSet<string> _seenThisTick = new HashSet<string>();

        // Flap detector — rolling 1 s decision count.
        private readonly Queue<float> _recentDecisionTimes = new Queue<float>(64);
        private float _flapHotSince = -1f;   // -1 = not currently above threshold
        private Button _autoTuneBtn;
        private RectTransform _autoTuneRow;

        // Per-symptom helper buttons (each shows when its symptom is active).
        private RectTransform _symptomBar;
        private Button _btnSrejStorm, _btnMassDrop, _btnGapStorm;
        // Edge-triggered last-snapshot cache for delta detection.
        private readonly Dictionary<string, (int srej, int gaps)> _lastPeerSnap =
            new Dictionary<string, (int srej, int gaps)>();
        private float _lastSymptomScanAt;

        /// <summary>
        /// Static hook the Editor wires up — when the runtime detects a
        /// symptom, it calls this. The Editor implementation pops the
        /// FederationPlaybookWindow pre-scrolled to the matching section.
        /// Runtime doesn't take an Editor dependency.
        /// </summary>
        public static System.Action<string> OnOpenPlaybookRequested;

        private struct PeerRow
        {
            public RectTransform root;
            public Image  dot;
            public Text   callsign;
            public Text   lastRx;
            public Text   rtt;
            public Text   srejs;
            public Text   gaps;
        }

        // ====================================================================
        private void Start()
        {
            if (bridge  == null) bridge  = Object.FindAnyObjectByType<Stanag5066FederationBridge>();
            if (advisor == null) advisor = Object.FindAnyObjectByType<LearnedModeAdvisor>();
            BuildUi();
            if (advisor != null) advisor.OnDecision += OnAdvisorDecision;
        }

        private void OnDestroy()
        {
            if (advisor != null) advisor.OnDecision -= OnAdvisorDecision;
        }

        private void OnAdvisorDecision(Agent a,
                                       Link16TdmaSimulator.PackingMode from,
                                       Link16TdmaSimulator.PackingMode to,
                                       float missRate, int samples)
        {
            _recentDecisionTimes.Enqueue(Time.unscaledTime);
        }

        private void Update()
        {
            if (_root == null) return;
            // Flap detector ticks every frame so the CTA can disappear quickly
            // after the rate falls.
            UpdateFlapDetector();
            if (Time.unscaledTime < _nextRefreshAt) return;
            _nextRefreshAt = Time.unscaledTime + refreshEverySec;
            Refresh();
        }

        // ====================================================================
        //  UI construction
        // ====================================================================
        private void BuildUi()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[Link16FederationDashboard] needs a parent Canvas — disabling.");
                enabled = false;
                return;
            }

            _root = MakePanel(transform, "L16FederationDashboard", anchor, offset,
                              size: new Vector2(panelWidth, 30f),     // grows with rows
                              bg: new Color(0.05f, 0.07f, 0.09f, 0.78f));

            _header = MakeText(_root, "Title", "FEDERATION (S5066)", 11, FontStyle.Bold,
                               new Color(0.55f, 0.85f, 1f, 1f),
                               new Vector2(0, 1), new Vector2(1, 1),
                               new Vector2(8, -16), new Vector2(-8, -2));

            // Column header strip.
            var hdr = new GameObject("ColHeader", typeof(RectTransform)).GetComponent<RectTransform>();
            hdr.SetParent(_root, false);
            hdr.anchorMin = new Vector2(0, 1); hdr.anchorMax = new Vector2(1, 1);
            hdr.pivot = new Vector2(0.5f, 1);
            hdr.anchoredPosition = new Vector2(0, -18f);
            hdr.sizeDelta = new Vector2(0, 14f);
            MakeText(hdr, "h_call",   "PEER",     9, FontStyle.Normal, new Color(0.75f, 0.85f, 1f, 0.8f),
                     new Vector2(0,0), new Vector2(1,1), new Vector2(20, 0), new Vector2(-200, 0));
            MakeText(hdr, "h_lastrx", "LAST RX",  9, FontStyle.Normal, new Color(0.75f, 0.85f, 1f, 0.8f),
                     new Vector2(0,0), new Vector2(1,1), new Vector2(70, 0), new Vector2(-150, 0));
            MakeText(hdr, "h_rtt",    "RTT",      9, FontStyle.Normal, new Color(0.75f, 0.85f, 1f, 0.8f),
                     new Vector2(0,0), new Vector2(1,1), new Vector2(125, 0), new Vector2(-105, 0));
            MakeText(hdr, "h_srej",   "SREJ-RX",  9, FontStyle.Normal, new Color(0.75f, 0.85f, 1f, 0.8f),
                     new Vector2(0,0), new Vector2(1,1), new Vector2(170, 0), new Vector2(-60, 0));
            MakeText(hdr, "h_gaps",   "GAPS→",    9, FontStyle.Normal, new Color(0.75f, 0.85f, 1f, 0.8f),
                     new Vector2(0,0), new Vector2(1,1), new Vector2(225, 0), new Vector2(-8, 0));

            _rowsHost = new GameObject("Rows", typeof(RectTransform)).GetComponent<RectTransform>();
            _rowsHost.SetParent(_root, false);
            _rowsHost.anchorMin = new Vector2(0, 1); _rowsHost.anchorMax = new Vector2(1, 1);
            _rowsHost.pivot = new Vector2(0.5f, 1);
            _rowsHost.anchoredPosition = new Vector2(0, -32f);
            _rowsHost.sizeDelta = new Vector2(0, 0);

            BuildAutoTuneCta();
            BuildSymptomBar();
        }

        private void BuildSymptomBar()
        {
            _symptomBar = new GameObject("SymptomBar", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            _symptomBar.SetParent(_root, false);
            _symptomBar.anchorMin = new Vector2(0, 0); _symptomBar.anchorMax = new Vector2(1, 0);
            _symptomBar.pivot = new Vector2(0.5f, 0);
            _symptomBar.anchoredPosition = new Vector2(0, 36f);
            _symptomBar.sizeDelta = new Vector2(0, 22f);
            _symptomBar.GetComponent<Image>().color = new Color(0, 0, 0, 0);

            _btnSrejStorm = MakeSymptomButton(_symptomBar, "SrejStormBtn",  "§2 SREJ storm",
                                              new Vector2(8, 0),    new Vector2(98, 0),
                                              () => OnOpenPlaybookRequested?.Invoke("SREJ-RX"));
            _btnMassDrop  = MakeSymptomButton(_symptomBar, "MassDropBtn",   "§5 mass drop",
                                              new Vector2(106, 0),  new Vector2(196, 0),
                                              () => OnOpenPlaybookRequested?.Invoke("all peers"));
            _btnGapStorm  = MakeSymptomButton(_symptomBar, "GapStormBtn",   "§3 gap storm",
                                              new Vector2(204, 0),  new Vector2(294, 0),
                                              () => OnOpenPlaybookRequested?.Invoke("Gap-storm"));

            // All start hidden — UpdateSymptomDetectors enables individually.
            _btnSrejStorm.gameObject.SetActive(false);
            _btnMassDrop.gameObject.SetActive(false);
            _btnGapStorm.gameObject.SetActive(false);
        }

        private static Button MakeSymptomButton(RectTransform parent, string name, string label,
                                                Vector2 offMin, Vector2 offMax,
                                                System.Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 0.5f);
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            go.GetComponent<Image>().color = new Color(1f, 0.55f, 0.25f, 0.65f);
            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            var lblGo = new GameObject("Lbl", typeof(RectTransform), typeof(Text));
            lblGo.transform.SetParent(go.transform, false);
            var lblRt = (RectTransform)lblGo.transform;
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;
            var t = lblGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 10;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.black;
            t.text = label;
            return btn;
        }

        private void BuildAutoTuneCta()
        {
            _autoTuneRow = new GameObject("AutoTune", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            _autoTuneRow.SetParent(_root, false);
            _autoTuneRow.anchorMin = new Vector2(0, 0); _autoTuneRow.anchorMax = new Vector2(1, 0);
            _autoTuneRow.pivot = new Vector2(0.5f, 0);
            _autoTuneRow.anchoredPosition = new Vector2(0, 6f);
            _autoTuneRow.sizeDelta = new Vector2(0, 28f);
            _autoTuneRow.GetComponent<Image>().color = new Color(1f, 0.55f, 0.25f, 0.16f);
            _autoTuneRow.gameObject.SetActive(false);

            // Label.
            var lbl = MakeText(_autoTuneRow, "Label",
                               "⚠ Mode-flap storm detected",
                               11, FontStyle.Bold,
                               new Color(1f, 0.78f, 0.30f, 1f),
                               new Vector2(0,0), new Vector2(1,1),
                               new Vector2(8, 0), new Vector2(-110, 0));
            _ = lbl;

            // Button.
            var btnGo = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(_autoTuneRow, false);
            var btnRt = (RectTransform)btnGo.transform;
            btnRt.anchorMin = new Vector2(1, 0); btnRt.anchorMax = new Vector2(1, 1);
            btnRt.pivot = new Vector2(1, 0.5f);
            btnRt.anchoredPosition = new Vector2(-6, 0);
            btnRt.sizeDelta = new Vector2(96, -6);
            btnGo.GetComponent<Image>().color = new Color(1f, 0.55f, 0.25f, 0.85f);
            _autoTuneBtn = btnGo.GetComponent<Button>();
            _autoTuneBtn.onClick.AddListener(OnAutoTuneClicked);

            var btnLbl = new GameObject("Lbl", typeof(RectTransform), typeof(Text));
            btnLbl.transform.SetParent(btnGo.transform, false);
            var bl = (RectTransform)btnLbl.transform;
            bl.anchorMin = Vector2.zero; bl.anchorMax = Vector2.one;
            bl.offsetMin = Vector2.zero; bl.offsetMax = Vector2.zero;
            var t = btnLbl.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 11;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.black;
            t.text = "Auto-tune";
        }

        private void OnAutoTuneClicked()
        {
            if (advisor == null)
            {
                Debug.LogWarning("[Link16FederationDashboard] no LearnedModeAdvisor — can't auto-tune.");
                return;
            }
            advisor.ApplyFlapStormPreset();
            // Reset the detector so we don't immediately re-show.
            _recentDecisionTimes.Clear();
            _flapHotSince = -1f;
            _autoTuneRow.gameObject.SetActive(false);
        }

        private void UpdateFlapDetector()
        {
            // Drop entries older than 1 s.
            float now = Time.unscaledTime;
            while (_recentDecisionTimes.Count > 0 && now - _recentDecisionTimes.Peek() > 1f)
                _recentDecisionTimes.Dequeue();
            int rate = _recentDecisionTimes.Count;       // decisions in the last 1 s
            bool over = rate > flapDecisionsPerSecThreshold;
            if (over)
            {
                if (_flapHotSince < 0f) _flapHotSince = now;
            }
            else
            {
                _flapHotSince = -1f;
            }

            bool show = over && (now - _flapHotSince) >= flapSustainedSec;
            if (_autoTuneRow != null) _autoTuneRow.gameObject.SetActive(show);

            // Symptom scan runs every ~1 s to keep the per-peer math cheap.
            if (now - _lastSymptomScanAt >= 1f)
            {
                _lastSymptomScanAt = now;
                ScanPeerSymptoms(now);
            }
        }

        private void ScanPeerSymptoms(float now)
        {
            if (bridge == null) return;
            int redPeers = 0, totalPeers = 0;
            bool srejStorm = false;
            int gapsGrowingPeers = 0;

            foreach (var kv in bridge.Peers)
            {
                totalPeers++;
                var ps = kv.Value;
                if (now - ps.lastRxTime > 30f) redPeers++;

                // Δ since last scan ≈ Δ per second (scan runs at 1 Hz).
                _lastPeerSnap.TryGetValue(kv.Key, out var prev);
                int dSrej = ps.srejReceived - prev.srej;
                int dGaps = ps.gapsDetected - prev.gaps;
                if (dSrej > srejStormPerSecThreshold) srejStorm = true;
                if (dGaps > 0) gapsGrowingPeers++;
                _lastPeerSnap[kv.Key] = (ps.srejReceived, ps.gapsDetected);
            }

            bool massDrop = totalPeers >= 2 && redPeers >= totalPeers;
            bool gapStorm = gapsGrowingPeers >= gapStormPeerCount;

            if (_btnSrejStorm != null) _btnSrejStorm.gameObject.SetActive(srejStorm);
            if (_btnMassDrop  != null) _btnMassDrop.gameObject.SetActive(massDrop);
            if (_btnGapStorm  != null) _btnGapStorm.gameObject.SetActive(gapStorm);
        }

        // ====================================================================
        //  Refresh
        // ====================================================================
        private void Refresh()
        {
            if (bridge == null) return;
            _seenThisTick.Clear();
            int visible = 0;

            float now = Time.unscaledTime;
            foreach (var kv in bridge.Peers)
            {
                _seenThisTick.Add(kv.Key);
                if (!_rows.TryGetValue(kv.Key, out var row))
                {
                    row = MakeRow(kv.Key, visible);
                    _rows[kv.Key] = row;
                }
                // Position the row.
                row.root.anchoredPosition = new Vector2(0, -visible * rowHeight);
                row.root.gameObject.SetActive(true);
                FillRow(row, kv.Key, kv.Value, now);
                visible++;
            }

            // Hide peers that disappeared this tick.
            foreach (var pair in _rows)
                if (!_seenThisTick.Contains(pair.Key))
                    pair.Value.root.gameObject.SetActive(false);

            // Resize panel to fit rows + header chrome.
            if (visible == 0)
            {
                _header.text = "FEDERATION (S5066)  —  no peers";
                _root.sizeDelta = new Vector2(panelWidth, 32f);
            }
            else
            {
                _header.text = $"FEDERATION (S5066)  {visible} peer{(visible == 1 ? "" : "s")}";
                _root.sizeDelta = new Vector2(panelWidth, 36f + visible * rowHeight);
            }
        }

        private void FillRow(PeerRow row, string callsign,
                             Stanag5066FederationBridge.PeerStats ps, float now)
        {
            float sinceRx = now - ps.lastRxTime;
            Color dotCol;
            if (sinceRx < 5f)       dotCol = new Color(0.20f, 0.95f, 0.45f, 1f);   // green
            else if (sinceRx < 30f) dotCol = new Color(1.00f, 0.78f, 0.30f, 1f);   // amber
            else                    dotCol = new Color(1.00f, 0.30f, 0.35f, 1f);   // red

            row.dot.color = dotCol;
            row.callsign.text = callsign;
            row.lastRx.text   = sinceRx < 0.1f ? "live" : $"{sinceRx:F0}s";
            row.rtt.text      = ps.lastAckRttSec > 0f ? $"{(int)(ps.lastAckRttSec * 1000)}ms" : "—";
            row.srejs.text    = ps.srejReceived.ToString();
            row.gaps.text     = ps.gapsDetected.ToString();
        }

        private PeerRow MakeRow(string callsign, int idx)
        {
            var rowGo = new GameObject($"Row_{callsign}", typeof(RectTransform)).GetComponent<RectTransform>();
            rowGo.SetParent(_rowsHost, false);
            rowGo.anchorMin = new Vector2(0, 1); rowGo.anchorMax = new Vector2(1, 1);
            rowGo.pivot = new Vector2(0.5f, 1);
            rowGo.sizeDelta = new Vector2(0, rowHeight);

            // Status dot.
            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(rowGo, false);
            var dotRt = (RectTransform)dotGo.transform;
            dotRt.anchorMin = new Vector2(0, 0.5f); dotRt.anchorMax = new Vector2(0, 0.5f);
            dotRt.pivot = new Vector2(0, 0.5f);
            dotRt.anchoredPosition = new Vector2(8, 0);
            dotRt.sizeDelta = new Vector2(8, 8);
            var dot = dotGo.GetComponent<Image>();
            dot.color = Color.gray;

            var row = new PeerRow
            {
                root     = rowGo,
                dot      = dot,
                callsign = ColText(rowGo, "callsign", 20,  -200),
                lastRx   = ColText(rowGo, "lastrx",   70,  -150),
                rtt      = ColText(rowGo, "rtt",     125,  -105),
                srejs    = ColText(rowGo, "srejs",   170,   -60),
                gaps     = ColText(rowGo, "gaps",    225,    -8),
            };
            return row;
        }

        private static Text ColText(Transform parent, string name, float offMinX, float offMaxX)
        {
            return MakeText(parent, name, "—", 10, FontStyle.Normal,
                            new Color(0.92f, 0.96f, 1f, 0.95f),
                            new Vector2(0, 0), new Vector2(1, 1),
                            new Vector2(offMinX, 0), new Vector2(offMaxX, 0));
        }

        private static RectTransform MakePanel(Transform parent, string name,
                                               Vector2 anchor, Vector2 offset,
                                               Vector2 size, Color bg)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot     = new Vector2(1f, 1f);
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = bg;
            return rt;
        }

        private static Text MakeText(Transform parent, string name, string text, int size,
                                     FontStyle style, Color colour,
                                     Vector2 aMin, Vector2 aMax,
                                     Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = size;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleLeft;
            t.color     = colour;
            t.text      = text;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }
    }
}
