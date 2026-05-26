// =====================================================================
//  NATO C2 RTS Hybrid — Link16BurstModeHud.cs
//  ---------------------------------------------------------------------
//  Tiny diagnostic readout (top-right corner, under the live indicator)
//  showing live PPLI envelope density per burst mode. Updates once per
//  second from Link16TdmaSimulator.{StdDp,P2Dp,P4Sp}EnvelopesPerSec.
//
//  Layout (~180×72 px):
//
//      ╭───────────────── L16 PPLI ─────────────────╮
//      │  STD-DP   ████████░░░░░░  12 env / 64 msg  │
//      │  P2DP     ████████████░░  18 env / 184 msg │
//      │  P4SP     ██░░░░░░░░░░░░   2 env /  4 msg  │
//      ╰────────────────────────────────────────────╯
//
//  The bar fills relative to the highest envelope rate seen in the last
//  ~5 s so the chart auto-rescales as traffic ebbs/flows. No allocations
//  per Update — we only repaint the labels once per second.
// =====================================================================

using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(40)]
    [AddComponentMenu("NATO C2/Link 16 Burst-Mode HUD")]
    public class Link16BurstModeHud : MonoBehaviour
    {
        [Tooltip("If null, the HUD finds the first Link16TdmaSimulator in the scene at Start.")]
        public Link16TdmaSimulator simulator;

        [Tooltip("Anchor in the parent Canvas. (1,1) = top-right.")]
        public Vector2 anchor = new Vector2(1f, 1f);
        [Tooltip("Pixel offset from the anchor corner (negative X = left, negative Y = down).")]
        public Vector2 offset = new Vector2(-12f, -148f);

        // ----- runtime -----
        private RectTransform _root;
        private Text _stdDpLabel, _p2DpLabel, _p4SpLabel;
        private Image _stdDpBar,  _p2DpBar,  _p4SpBar;
        private float _autoScalePeak = 8f; // floor so empty nets don't zero-divide

        private void Start()
        {
            if (simulator == null) simulator = Object.FindAnyObjectByType<Link16TdmaSimulator>();
            BuildUi();
        }

        private void Update()
        {
            if (simulator == null) return;

            int eStd = simulator.StdDpEnvelopesPerSec;
            int e2dp = simulator.P2DpEnvelopesPerSec;
            int e4sp = simulator.P4SpEnvelopesPerSec;
            int mStd = simulator.StdDpMsgsPerSec;
            int m2dp = simulator.P2DpMsgsPerSec;
            int m4sp = simulator.P4SpMsgsPerSec;

            // Auto-scale peak with slow decay so the chart stays expressive
            // even when traffic drops.
            int hi = Mathf.Max(eStd, Mathf.Max(e2dp, e4sp));
            if (hi > _autoScalePeak) _autoScalePeak = hi;
            else _autoScalePeak = Mathf.Max(8f, _autoScalePeak * 0.995f);

            if (_stdDpBar != null) _stdDpBar.fillAmount = eStd / _autoScalePeak;
            if (_p2DpBar  != null) _p2DpBar.fillAmount  = e2dp / _autoScalePeak;
            if (_p4SpBar  != null) _p4SpBar.fillAmount  = e4sp / _autoScalePeak;

            if (_stdDpLabel != null) _stdDpLabel.text = $"STD-DP  {eStd,3} env / {mStd,4} msg";
            if (_p2DpLabel  != null) _p2DpLabel.text  = $"P2DP    {e2dp,3} env / {m2dp,4} msg";
            if (_p4SpLabel  != null) _p4SpLabel.text  = $"P4SP    {e4sp,3} env / {m4sp,4} msg";
        }

        // ---------------- UI construction ---------------------------
        private void BuildUi()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[Link16BurstModeHud] needs a parent Canvas — disabling.");
                enabled = false;
                return;
            }

            _root = MakePanel(transform, "Link16BurstModeHud", anchor, offset,
                              size: new Vector2(220, 86),
                              bg: new Color(0.05f, 0.07f, 0.09f, 0.78f));

            MakeText(_root, "Title", "L16 PPLI", 11, FontStyle.Bold,
                     new Color(0.55f, 0.85f, 1f, 1f),
                     new Vector2(0, 1), new Vector2(1, 1),
                     new Vector2(8, -16), new Vector2(-8, -2));

            float rowH = 18f;
            float y0 = -22f;
            (_stdDpLabel, _stdDpBar) = MakeRow(_root, "Row_StdDp", y0,           rowH, new Color(0.40f, 0.85f, 1.00f));
            (_p2DpLabel,  _p2DpBar)  = MakeRow(_root, "Row_P2Dp",  y0 - rowH,    rowH, new Color(1.00f, 0.78f, 0.30f));
            (_p4SpLabel,  _p4SpBar)  = MakeRow(_root, "Row_P4Sp",  y0 - rowH*2f, rowH, new Color(0.75f, 0.55f, 1.00f));
        }

        private static (Text label, Image bar) MakeRow(Transform parent, string name, float y, float h, Color barColour)
        {
            var row = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(parent, false);
            row.anchorMin = new Vector2(0, 1);
            row.anchorMax = new Vector2(1, 1);
            row.pivot     = new Vector2(0.5f, 1);
            row.anchoredPosition = new Vector2(0, y);
            row.sizeDelta = new Vector2(0, h);

            // Background bar (dim).
            var barBgGo = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBgGo.transform.SetParent(row, false);
            var barBgRt = (RectTransform)barBgGo.transform;
            barBgRt.anchorMin = new Vector2(0, 0.5f);
            barBgRt.anchorMax = new Vector2(0, 0.5f);
            barBgRt.pivot     = new Vector2(0, 0.5f);
            barBgRt.anchoredPosition = new Vector2(8, 0);
            barBgRt.sizeDelta = new Vector2(90, 6);
            barBgGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.08f);

            // Filled bar.
            var barGo = new GameObject("Bar", typeof(RectTransform), typeof(Image));
            barGo.transform.SetParent(barBgRt, false);
            var barRt = (RectTransform)barGo.transform;
            barRt.anchorMin = new Vector2(0, 0);
            barRt.anchorMax = new Vector2(1, 1);
            barRt.offsetMin = Vector2.zero;
            barRt.offsetMax = Vector2.zero;
            var bar = barGo.GetComponent<Image>();
            bar.color = barColour;
            bar.type  = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillAmount = 0f;

            // Label (right of bar).
            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            lblGo.transform.SetParent(row, false);
            var lblRt = (RectTransform)lblGo.transform;
            lblRt.anchorMin = new Vector2(0, 0);
            lblRt.anchorMax = new Vector2(1, 1);
            lblRt.offsetMin = new Vector2(108, 0);
            lblRt.offsetMax = new Vector2(-6, 0);
            var lbl = lblGo.GetComponent<Text>();
            lbl.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            lbl.fontSize = 10;
            lbl.alignment = TextAnchor.MiddleLeft;
            lbl.color = new Color(0.85f, 0.92f, 1f, 0.95f);
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
            lbl.text = name + "  --";

            return (lbl, bar);
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
            return t;
        }
    }
}
