// =====================================================================
//  NATO C2 RTS Hybrid — UnitSymbolRenderer.cs
//  ---------------------------------------------------------------------
//  World-space billboard that displays a unit's APP-6E symbol above the
//  3D mesh, with the standard amplifier text:
//
//      Q     ECHELON      F
//      ┌──────────┐
//      │   ICON   │       ← MilsymbolBridge texture
//      └──────────┘
//          CALLSIGN
//
//      Q  = Field C (quantity)         shown when > 1
//      F  = Field F (reinforced/-d)    + / - / ±
//      T  = Field T (callsign)         under the frame
//      ECH= echelon dots/bars/X        rendered as part of the texture
//
//  Auto-attached by DemoSceneBootstrap and easily added to any Agent
//  in production via `gameObject.AddComponent<UnitSymbolRenderer>()`.
// =====================================================================

using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [RequireComponent(typeof(Agent))]
    [AddComponentMenu("NATO C2/Unit Symbol Renderer")]
    public class UnitSymbolRenderer : MonoBehaviour
    {
        [Header("Layout")]
        [Tooltip("World-space height above the unit's transform origin.")]
        public float verticalOffset = 5.0f;
        [Tooltip("Pixel size of the symbol image inside the world-space canvas.")]
        public int symbolPixelSize = 128;
        [Tooltip("World-space canvas scale (controls the overall on-screen footprint).")]
        public float canvasScale = 0.05f;

        [Header("Refresh")]
        [Tooltip("How often the renderer asks MilsymbolBridge for a fresh texture (seconds). Use 0 to refresh every frame (expensive).")]
        public float refreshInterval = 0.5f;

        // ---------- private ----------
        private Agent _agent;
        private Canvas _canvas;
        private RawImage _symbolImage;
        private Text _callsignLabel;
        private Text _amplifierLabel;
        private Image _hpBarBg;
        private Image _hpBarFill;
        private Text _ctrlGroupBadge;
        private Camera _camera;
        private float _nextRefresh;
        private string _lastCacheKey;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            _camera = Camera.main;
            BuildCanvas();
        }

        private void OnEnable()
        {
            _agent = _agent != null ? _agent : GetComponent<Agent>();
        }

        private void LateUpdate()
        {
            if (_agent == null || _canvas == null) return;

            // Position above the unit + face the camera (billboard).
            _canvas.transform.position = transform.position + Vector3.up * verticalOffset;
            if (_camera == null) _camera = Camera.main;
            if (_camera != null)
            {
                _canvas.transform.rotation = Quaternion.LookRotation(
                    _canvas.transform.position - _camera.transform.position,
                    Vector3.up);
            }

            // Periodic refresh — only redraws if the underlying cache key changed.
            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + Mathf.Max(0.05f, refreshInterval);
                RefreshSymbol();
            }

            // HP bar visible only when selected, OR when damaged.
            bool hpVisible = _agent.isSelected || _agent.health < _agent.maxHealth - 0.01f;
            if (_hpBarBg != null && _hpBarBg.gameObject.activeSelf != hpVisible)
                _hpBarBg.gameObject.SetActive(hpVisible);
            if (hpVisible && _hpBarFill != null)
            {
                float pct = _agent.maxHealth > 0 ? Mathf.Clamp01(_agent.health / _agent.maxHealth) : 0f;
                _hpBarFill.fillAmount = pct;
                _hpBarFill.color = pct > 0.5f ? NATOPalette.FriendlyGreen
                                  : pct > 0.25f ? NATOPalette.NeutralYellow
                                                : NATOPalette.HostileRed;
            }

            // Control-group badge: show number when assigned.
            if (_ctrlGroupBadge != null)
            {
                bool show = _agent.controlGroup > 0;
                if (_ctrlGroupBadge.gameObject.activeSelf != show)
                    _ctrlGroupBadge.gameObject.SetActive(show);
                if (show) _ctrlGroupBadge.text = _agent.controlGroup.ToString();
            }
        }

        // =================================================================
        //  Build a world-space canvas with the three text elements.
        // =================================================================
        private void BuildCanvas()
        {
            var go = new GameObject($"Symbol_{name}");
            go.transform.SetParent(transform, worldPositionStays: false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<CanvasScaler>();
            go.transform.localScale = Vector3.one * canvasScale;

            // Symbol image (centre)
            var imgGo = new GameObject("Symbol");
            imgGo.transform.SetParent(go.transform, false);
            _symbolImage = imgGo.AddComponent<RawImage>();
            var imgRt = _symbolImage.rectTransform;
            imgRt.sizeDelta = new Vector2(symbolPixelSize, symbolPixelSize);
            imgRt.anchoredPosition = Vector2.zero;

            // Callsign (below frame, Field T)
            var csGo = new GameObject("Callsign");
            csGo.transform.SetParent(go.transform, false);
            _callsignLabel = csGo.AddComponent<Text>();
            _callsignLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _callsignLabel.fontSize = 18;
            _callsignLabel.alignment = TextAnchor.MiddleCenter;
            _callsignLabel.color = NATOPalette.AccentCyan;
            _callsignLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            _callsignLabel.verticalOverflow   = VerticalWrapMode.Overflow;
            var csRt = _callsignLabel.rectTransform;
            csRt.sizeDelta = new Vector2(140, 22);
            csRt.anchoredPosition = new Vector2(0, -symbolPixelSize * 0.5f - 14);

            // Amplifiers (Field C quantity left + Field F reinforced right) on a single line above the frame
            var amGo = new GameObject("Amplifiers");
            amGo.transform.SetParent(go.transform, false);
            _amplifierLabel = amGo.AddComponent<Text>();
            _amplifierLabel.font = _callsignLabel.font;
            _amplifierLabel.fontSize = 14;
            _amplifierLabel.alignment = TextAnchor.MiddleCenter;
            _amplifierLabel.color = NATOPalette.AccentCyan;
            _amplifierLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            _amplifierLabel.verticalOverflow   = VerticalWrapMode.Overflow;
            var amRt = _amplifierLabel.rectTransform;
            amRt.sizeDelta = new Vector2(160, 18);
            amRt.anchoredPosition = new Vector2(0, symbolPixelSize * 0.5f + 10);

            // HP bar (visible only when selected). Slim, sits just above the symbol frame.
            var hpBgGo = new GameObject("HpBarBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hpBgGo.transform.SetParent(go.transform, false);
            _hpBarBg = hpBgGo.GetComponent<Image>();
            _hpBarBg.color = new Color(0.04f, 0.08f, 0.13f, 0.85f);
            var hpBgRt = _hpBarBg.rectTransform;
            hpBgRt.sizeDelta = new Vector2(symbolPixelSize, 6);
            hpBgRt.anchoredPosition = new Vector2(0, symbolPixelSize * 0.5f + 30);

            var hpFillGo = new GameObject("HpBarFill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hpFillGo.transform.SetParent(hpBgGo.transform, false);
            _hpBarFill = hpFillGo.GetComponent<Image>();
            _hpBarFill.color = NATOPalette.FriendlyGreen;
            _hpBarFill.type = Image.Type.Filled;
            _hpBarFill.fillMethod = Image.FillMethod.Horizontal;
            _hpBarFill.fillAmount = 1f;
            var hpFillRt = _hpBarFill.rectTransform;
            hpFillRt.anchorMin = Vector2.zero; hpFillRt.anchorMax = Vector2.one;
            hpFillRt.offsetMin = Vector2.zero; hpFillRt.offsetMax = Vector2.zero;

            // Control-group badge (small number to the LEFT of the symbol, SC2 style).
            var ctlGo = new GameObject("CtrlGroupBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ctlGo.transform.SetParent(go.transform, false);
            _ctrlGroupBadge = ctlGo.GetComponent<Text>();
            _ctrlGroupBadge.font = _callsignLabel.font;
            _ctrlGroupBadge.fontSize = 22;
            _ctrlGroupBadge.fontStyle = FontStyle.Bold;
            _ctrlGroupBadge.color = NATOPalette.NeutralYellow;
            _ctrlGroupBadge.alignment = TextAnchor.MiddleCenter;
            _ctrlGroupBadge.horizontalOverflow = HorizontalWrapMode.Overflow;
            _ctrlGroupBadge.verticalOverflow = VerticalWrapMode.Overflow;
            var ctlRt = _ctrlGroupBadge.rectTransform;
            ctlRt.sizeDelta = new Vector2(20, 20);
            ctlRt.anchoredPosition = new Vector2(-symbolPixelSize * 0.5f - 14, symbolPixelSize * 0.5f - 4);
            _ctrlGroupBadge.gameObject.SetActive(false);

            _hpBarBg.gameObject.SetActive(false);
        }

        // =================================================================
        //  Refresh — pull symbol texture + amplifier strings from the Agent.
        // =================================================================
        private void RefreshSymbol()
        {
            if (_agent == null) return;

            // Texture from MilsymbolBridge (cached server-side).
            var bridge = MilsymbolBridge.Instance;
            if (bridge != null && _symbolImage != null)
            {
                var tex = bridge.ResolveSymbol(_agent);
                if (tex != null) _symbolImage.texture = tex;
            }

            // Callsign tint follows affiliation.
            Color tint = NATOPalette.For(_agent.affiliation);
            if (_callsignLabel != null)
            {
                _callsignLabel.text = _agent.callsign ?? "";
                _callsignLabel.color = tint;
            }

            // Q  (Field C) + F (Field F)
            if (_amplifierLabel != null)
            {
                string q = (_agent.quantity > 1) ? $"{_agent.quantity}x" : "";
                string f = string.IsNullOrEmpty(_agent.reinforcedDetached)
                    ? ""
                    : $"({_agent.reinforcedDetached})";
                string amplifiers = (q.Length > 0 && f.Length > 0) ? $"{q}   {f}"
                                  : (q.Length > 0 ? q : f);
                _amplifierLabel.text = amplifiers;
                // Colour the F field semantically (green=+, yellow=-, cyan=±)
                if (f.Length > 0 && q.Length == 0)
                {
                    _amplifierLabel.color = _agent.reinforcedDetached switch
                    {
                        "+" => NATOPalette.FriendlyGreen,
                        "-" => NATOPalette.NeutralYellow,
                        "±" => NATOPalette.AccentCyan,
                        _   => tint
                    };
                }
                else
                {
                    _amplifierLabel.color = tint;
                }
            }
        }
    }
}
