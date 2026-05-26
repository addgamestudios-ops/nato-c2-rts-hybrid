// =====================================================================
//  NATO C2 RTS Hybrid — LatticeSettingsPanel.cs
//  ---------------------------------------------------------------------
//  Floating settings panel that the ⚙ Settings button on the Lattice
//  top bar opens. Live-applies values to the corresponding components:
//
//      • Sensor radius    → MavenBrackets.sensingRadius
//      • Audio cue volume → IncomingRequestPanel.audioVolume
//      • Draw distance    → Camera.main.farClipPlane
//      • Dispatch ghost   → IncomingRequestPanel default dispatch lifetime
//                            (read by future DispatchTrace.Create calls)
//
//  Closed by default; toggled via OpenClose().
// =====================================================================

using System;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(170)]
    [AddComponentMenu("NATO C2/Lattice Settings Panel")]
    public class LatticeSettingsPanel : MonoBehaviour
    {
        public int width = 360, height = 380;

        private RectTransform _root;
        private bool _open;

        private MavenBrackets _brackets;
        private IncomingRequestPanel _requests;
        private Camera _cam;

        public bool IsOpen => _open;

        private void Awake() { BuildPanel(); SetOpen(false); }

        private void Start()
        {
            _brackets = FindAnyObjectByType<MavenBrackets>();
            _requests = FindAnyObjectByType<IncomingRequestPanel>();
            _cam = Camera.main;
        }

        public void Toggle() => SetOpen(!_open);
        public void SetOpen(bool v)
        {
            _open = v;
            if (_root != null) _root.gameObject.SetActive(v);
        }

        // ---------- UI build -----------------------------------------
        private void BuildPanel()
        {
            var go = new GameObject("LatticeSettings",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.sizeDelta = new Vector2(width, height);
            go.GetComponent<Image>().color = new Color(0.04f, 0.08f, 0.15f, 0.97f);

            // Cyan top border (1 px).
            var border = new GameObject("Border",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            border.transform.SetParent(_root, false);
            var brt = border.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0, 1); brt.anchorMax = new Vector2(1, 1);
            brt.sizeDelta = new Vector2(0, 1);
            border.GetComponent<Image>().color = new Color(0.063f, 0.878f, 1f, 0.5f);

            // Header
            var hdr = MakeText(_root, "Header", "⚙ SETTINGS", 18, FontStyle.Bold,
                new Color(0.063f, 0.878f, 1f),
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(16, -40), new Vector2(-16, -10));
            hdr.alignment = TextAnchor.MiddleLeft;

            // Close button (top-right)
            var closeBtn = MakeButton(_root, "✕", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-40, -40), new Vector2(-8, -8), new Color(0.95f, 0.45f, 0.45f, 0.9f));
            closeBtn.onClick.AddListener(() => SetOpen(false));

            // Sliders — each is a labelled row with a Slider and a live value text.
            // The PlayerPrefs key prefix lets multiple deployments coexist on
            // the same workstation without stepping on each other's settings.
            float y = -60f;
            BuildSlider("Sensor radius (m)",   20f,   200f,  80f,   "sensorRadius", ref y,
                v => { if (_brackets != null) _brackets.sensingRadius = v; });
            BuildSlider("Audio cue volume",    0f,    1f,    0.45f, "audioVolume",  ref y,
                v => { if (_requests != null) _requests.audioVolume = v; });
            BuildSlider("Draw distance (m)",   200f,  4000f, 800f,  "drawDistance", ref y,
                v => { if (_cam == null) _cam = Camera.main;
                       if (_cam != null) _cam.farClipPlane = v; });
            BuildSlider("Pulse rate (Hz)",     0.5f,  4f,    1.25f, "pulseHz",      ref y,
                v => { if (_brackets != null) _brackets.pulseHz = v; });
            BuildSlider("Ghost persistence (s)", 0f,  30f,   8f,    "ghostLifetime", ref y,
                v => { if (_brackets != null) _brackets.ghostLifetime = v; });

            // Reset-to-defaults button.
            var resetBtn = MakeButton(_root, "Reset to defaults",
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(16, 38), new Vector2(-16, 64),
                new Color(0.95f, 0.62f, 0.30f, 1f));
            resetBtn.onClick.AddListener(ResetToDefaults);

            // Footer hint.
            var footer = MakeText(_root, "Footer",
                "Changes apply live — close to dismiss.",
                11, FontStyle.Italic, new Color(0.55f, 0.65f, 0.78f),
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(16, 12), new Vector2(-16, 28));
            footer.alignment = TextAnchor.MiddleLeft;
        }

        // ---------- PlayerPrefs ---------------------------------------
        private const string PrefPrefix = "NATO_C2_";
        private readonly System.Collections.Generic.List<System.Action> _resetters
            = new System.Collections.Generic.List<System.Action>();

        public void ResetToDefaults()
        {
            for (int i = 0; i < _resetters.Count; i++) _resetters[i]?.Invoke();
        }

        // ---------- slider row ---------------------------------------
        //  Reads PlayerPrefs.GetFloat(prefKey, defaultVal) on build so the
        //  slider starts at the user's saved value; writes back on every
        //  change so settings survive the next Play / app restart.
        private void BuildSlider(string label, float min, float max, float defaultVal,
                                 string prefKey, ref float y, Action<float> apply)
        {
            string fullKey = PrefPrefix + prefKey;
            float startVal = PlayerPrefs.GetFloat(fullKey, defaultVal);
            startVal = Mathf.Clamp(startVal, min, max);
            const float rowHeight = 50f;
            // Label
            var lblRow = MakeText(_root, label + "_Label", label,
                13, FontStyle.Normal, new Color(0.85f, 0.92f, 1f),
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(16, y - 20), new Vector2(-90, y));
            lblRow.alignment = TextAnchor.LowerLeft;

            // Value readout (right side)
            var valTxt = MakeText(_root, label + "_Val", defaultVal.ToString("F2"),
                13, FontStyle.Bold, new Color(0.063f, 0.878f, 1f),
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(-86, y - 20), new Vector2(-16, y));
            valTxt.alignment = TextAnchor.LowerRight;

            // Slider track
            var sgo = new GameObject(label + "_Slider",
                typeof(RectTransform), typeof(Slider), typeof(CanvasRenderer), typeof(Image));
            sgo.transform.SetParent(_root, false);
            var srt = sgo.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 1); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(16, y - rowHeight + 6);
            srt.offsetMax = new Vector2(-16, y - 24);
            sgo.GetComponent<Image>().color = new Color(0.07f, 0.12f, 0.235f);

            var slider = sgo.GetComponent<Slider>();
            slider.minValue = min; slider.maxValue = max;
            slider.value = startVal;
            slider.targetGraphic = sgo.GetComponent<Image>();

            // Fill
            var fillGo = new GameObject("Fill",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fillGo.transform.SetParent(sgo.transform, false);
            var frt = fillGo.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(0, 1);
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
            fillGo.GetComponent<Image>().color = new Color(0.063f, 0.878f, 1f, 0.6f);

            var fillAreaGo = new GameObject("FillArea", typeof(RectTransform));
            fillAreaGo.transform.SetParent(sgo.transform, false);
            var faRt = fillAreaGo.GetComponent<RectTransform>();
            faRt.anchorMin = Vector2.zero; faRt.anchorMax = Vector2.one;
            faRt.offsetMin = new Vector2(8, 4); faRt.offsetMax = new Vector2(-8, -4);
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            slider.fillRect = fillGo.GetComponent<RectTransform>();

            // Handle
            var handleAreaGo = new GameObject("HandleArea", typeof(RectTransform));
            handleAreaGo.transform.SetParent(sgo.transform, false);
            var haRt = handleAreaGo.GetComponent<RectTransform>();
            haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one;
            haRt.offsetMin = new Vector2(8, 0); haRt.offsetMax = new Vector2(-8, 0);
            var handle = new GameObject("Handle",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            handle.transform.SetParent(handleAreaGo.transform, false);
            var hRt = handle.GetComponent<RectTransform>();
            hRt.sizeDelta = new Vector2(14, 14);
            handle.GetComponent<Image>().color = Color.white;
            slider.handleRect = hRt;
            slider.direction = Slider.Direction.LeftToRight;

            slider.onValueChanged.AddListener(v => {
                valTxt.text = v.ToString("F2");
                apply?.Invoke(v);
                PlayerPrefs.SetFloat(fullKey, v);
                PlayerPrefs.Save();
            });

            // Apply the persisted start value, and update the readout text.
            valTxt.text = startVal.ToString("F2");
            apply?.Invoke(startVal);

            // Reset action for this slider — restores default + clears the key.
            _resetters.Add(() => {
                slider.value = defaultVal;
                PlayerPrefs.DeleteKey(fullKey);
                PlayerPrefs.Save();
            });

            y -= rowHeight + 4f;
        }

        // ---------- helpers ------------------------------------------
        private Text MakeText(Transform parent, string name, string text, int size,
                              FontStyle style, Color colour,
                              Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = colour;
            t.alignment = TextAnchor.UpperLeft; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return t;
        }

        private Button MakeButton(Transform parent, string label,
                                  Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax,
                                  Color colour)
        {
            var go = new GameObject("Btn_" + label,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            go.GetComponent<Image>().color = new Color(colour.r, colour.g, colour.b, 0.25f);
            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            lblGo.transform.SetParent(go.transform, false);
            var lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = lblRt.offsetMax = Vector2.zero;
            var t = lblGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = label; t.fontSize = 16; t.fontStyle = FontStyle.Bold;
            t.color = colour; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            return go.GetComponent<Button>();
        }
    }
}
