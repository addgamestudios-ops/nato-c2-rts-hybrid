// =====================================================================
//  NATO C2 RTS Hybrid — HandheldDevicePanel.cs
//  ---------------------------------------------------------------------
//  A phone-form-factor display modelled on ATAK-EUD (End User Device)
//  running on a Samsung S22 Tactical Edition / Sentry phone. Toggle
//  with the T key. Shows the currently-selected unit's:
//
//      • Callsign + APP-6 symbol thumbnail
//      • Live HP / ammo bars
//      • Mini-map (top-down camera render or a procedural radar plot)
//      • Three big touch-friendly buttons:
//            FIRE MISSION  →  arm Q hotkey (artillery at next LMB)
//            MEDEVAC       →  W (medical evac at own position)
//            REQUEST EVAC  →  E (casualty evac at own position)
//
//  This is what an individual soldier sees on their phone. The CCC
//  operator sees the full HUD. Both share the same FeedHub channel.
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(125)]
    [AddComponentMenu("NATO C2/Handheld Device Panel")]
    public class HandheldDevicePanel : MonoBehaviour
    {
        [Header("Form factor")]
        [Min(160)] public int phoneWidth  = 280;
        [Min(280)] public int phoneHeight = 360;
        public float leftMargin   = 16f;
        public float bottomMargin = 72f;

        [Header("Toggle")]
        public KeyCode toggleKey = KeyCode.T;
        public bool startVisible = false;

        // ---------- private state ---------------------------------------
        private RectTransform _root;
        private CanvasGroup _cg;
        private Text  _headerCallsign;
        private RawImage _sidcImage;
        private Image _hpFill;
        private Image _ammoFill;
        private Text  _hpText;
        private Text  _ammoText;
        private Text  _mgrsText;
        private Text  _identityText;
        private Text  _statusText;
        private Button _btnFire;
        private Button _btnMedevac;
        private Button _btnEvac;
        private Agent _tracked;
        private bool _visible;

        private void Awake()
        {
            BuildUi();
            _visible = startVisible;
            ApplyVisibility();
            if (NATO_C2_Manager.Instance != null)
                NATO_C2_Manager.Instance.OnSelectionChanged += OnSelectionChanged;
        }

        private void OnEnable()
        {
            if (NATO_C2_Manager.Instance != null)
                NATO_C2_Manager.Instance.OnSelectionChanged += OnSelectionChanged;
        }
        private void OnDisable()
        {
            if (NATO_C2_Manager.Instance != null)
                NATO_C2_Manager.Instance.OnSelectionChanged -= OnSelectionChanged;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                _visible = !_visible;
                ApplyVisibility();
            }
            if (!_visible || _tracked == null) return;
            RefreshFromAgent();
        }

        private void ApplyVisibility()
        {
            if (_root == null) return;
            _root.gameObject.SetActive(_visible);
            if (_cg != null) _cg.alpha = _visible ? 1f : 0f;
        }

        private void OnSelectionChanged(IReadOnlyList<Agent> sel)
        {
            _tracked = (sel != null && sel.Count > 0) ? sel[0] : null;
            if (_tracked != null && _visible) RefreshFromAgent();
        }

        // =================================================================
        //  Render
        // =================================================================
        private void RefreshFromAgent()
        {
            var a = _tracked;
            if (a == null) return;

            // Header callsign.
            if (_headerCallsign != null) _headerCallsign.text = string.IsNullOrEmpty(a.callsign) ? "—" : a.callsign;

            // SIDC thumbnail — only flip alpha visible once the texture lands.
            var bridge = MilsymbolBridge.Instance;
            if (bridge != null && _sidcImage != null)
            {
                var tex = bridge.ResolveSymbol(a);
                if (tex != null)
                {
                    _sidcImage.texture = tex;
                    _sidcImage.color = Color.white;
                }
            }

            // Identity sub-row: type · echelon · rank · CO.
            if (_identityText != null)
            {
                string rank = string.IsNullOrEmpty(a.rank) ? "—" : a.rank;
                string co   = string.IsNullOrEmpty(a.commandingOfficer) ? "—" : a.commandingOfficer;
                _identityText.text = $"{a.unitType} · {EchelonShort(a.echelon)}  ·  {rank} {co}";
            }

            // HP + ammo bars and text.
            float hp = a.maxHealth > 0 ? a.health / a.maxHealth : 0f;
            float am = a.maxAmmo   > 0 ? a.ammo   / a.maxAmmo   : 0f;
            if (_hpFill   != null)
            {
                _hpFill.fillAmount = hp;
                _hpFill.color = hp > 0.5f  ? NATOPalette.FriendlyGreen
                              : hp > 0.25f ? NATOPalette.NeutralYellow
                                           : NATOPalette.HostileRed;
            }
            if (_ammoFill != null) _ammoFill.fillAmount = am;
            if (_hpText   != null) _hpText.text   = $"HP    {Mathf.RoundToInt(a.health)}/{Mathf.RoundToInt(a.maxHealth)}";
            if (_ammoText != null) _ammoText.text = $"AMMO  {Mathf.RoundToInt(a.ammo)}/{Mathf.RoundToInt(a.maxAmmo)}";

            // Order + speed + heading.
            if (_statusText != null)
            {
                float speed = a.currentVelocity.magnitude;
                float heading = Mathf.Atan2(a.desiredFacing.x, a.desiredFacing.z) * Mathf.Rad2Deg;
                if (heading < 0f) heading += 360f;
                _statusText.text = $"{a.currentOrder}   ·   {speed:0.0} m/s   ·   HDG {Mathf.RoundToInt(heading):000}°";
            }

            // MGRS.
            if (_mgrsText != null)
            {
                int east  = Mathf.RoundToInt((a.transform.position.x + 1000f) * 10f);
                int north = Mathf.RoundToInt((a.transform.position.z + 1000f) * 10f);
                _mgrsText.text = $"MGRS  33TWN {east:D5} {north:D5}";
            }
        }

        private static string EchelonShort(int e) => e switch
        {
            11 => "TM", 12 => "SQD", 13 => "SEC",
            14 => "PLT", 15 => "COY", 16 => "BN",
            17 => "BDE", 18 => "DIV",
            _ => "ECH —"
        };

        // =================================================================
        //  Build phone UI scaffold — vertical layout, dense, text-first.
        // =================================================================
        private void BuildUi()
        {
            var parent = transform;
            var go = new GameObject("HandheldDevice",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0, 0); _root.anchorMax = new Vector2(0, 0);
            _root.pivot = new Vector2(0, 0);
            _root.sizeDelta = new Vector2(phoneWidth, phoneHeight);
            _root.anchoredPosition = new Vector2(leftMargin, bottomMargin);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.02f, 0.04f, 0.07f, 0.96f);
            bg.raycastTarget = true;
            _cg = go.GetComponent<CanvasGroup>();

            // Thin cyan top bezel.
            var bezel = MakeImage(_root, "Bezel", new Color(0f, 0.83f, 1f, 0.95f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -3), Vector2.zero);
            bezel.raycastTarget = false;

            // "EUD" device tag in the top-right (looks like an ATAK EUD).
            var devTag = MakeText(_root, "DevTag", "ATAK-EUD  ·  S22 TE", 9, FontStyle.Normal,
                new Color(0.55f, 0.7f, 0.85f, 0.8f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -16), new Vector2(-12, -3));
            devTag.alignment = TextAnchor.MiddleRight;

            // ── Identity row (top, callsign + small APP-6 thumb) ──────────
            _headerCallsign = MakeText(_root, "Callsign", "—", 20, FontStyle.Bold, NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(64, -42), new Vector2(-12, -18));

            _identityText = MakeText(_root, "Identity", "—", 11, FontStyle.Normal,
                new Color(0.78f, 0.86f, 0.94f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(64, -60), new Vector2(-12, -42));

            // Small APP-6 symbol thumb (44×44) at top-left, with a transparent
            // initial color so it doesn't flash a solid colour before the
            // texture finishes resolving.
            var symGo = new GameObject("Sidc", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            symGo.transform.SetParent(_root, false);
            _sidcImage = symGo.GetComponent<RawImage>();
            _sidcImage.raycastTarget = false;
            _sidcImage.color = new Color(1, 1, 1, 0f);
            var sRt = symGo.GetComponent<RectTransform>();
            sRt.anchorMin = new Vector2(0, 1); sRt.anchorMax = new Vector2(0, 1);
            sRt.pivot = new Vector2(0, 1);
            sRt.sizeDelta = new Vector2(44, 44);
            sRt.anchoredPosition = new Vector2(12, -28);

            // Divider.
            MakeImage(_root, "Hr1", new Color(0f, 0.83f, 1f, 0.18f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -82), new Vector2(-12, -81)).raycastTarget = false;

            // ── HP row ───────────────────────────────────────────────────
            _hpText = MakeText(_root, "HpLbl", "HP", 11, FontStyle.Bold,
                new Color(0.78f, 0.86f, 0.94f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -102), new Vector2(-12, -88));
            _hpFill = MakeBar(_root, "HpBar", NATOPalette.FriendlyGreen,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -114), new Vector2(-12, -106));

            // ── Ammo row ─────────────────────────────────────────────────
            _ammoText = MakeText(_root, "AmLbl", "AMMO", 11, FontStyle.Bold,
                new Color(0.78f, 0.86f, 0.94f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -132), new Vector2(-12, -118));
            _ammoFill = MakeBar(_root, "AmBar", NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -144), new Vector2(-12, -136));

            // ── Status (order + speed/heading) ───────────────────────────
            _statusText = MakeText(_root, "Status", "—", 11, FontStyle.Normal, Color.white,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -166), new Vector2(-12, -150));

            // ── MGRS ─────────────────────────────────────────────────────
            _mgrsText = MakeText(_root, "Mgrs", "MGRS  —", 11, FontStyle.Normal, NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -184), new Vector2(-12, -168));

            // Divider before buttons.
            MakeImage(_root, "Hr2", new Color(0f, 0.83f, 1f, 0.18f),
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -198), new Vector2(-12, -197)).raycastTarget = false;

            // ── Three big touch-friendly action buttons (stack at bottom) ──
            _btnFire    = MakeBigButton(_root, "BtnFire",    "Q · FIRE MISSION",  NATOPalette.HostileRed,    -214, () => OnPressFire());
            _btnMedevac = MakeBigButton(_root, "BtnMedevac", "W · MEDEVAC",       NATOPalette.NeutralYellow, -252, () => OnPressMedevac());
            _btnEvac    = MakeBigButton(_root, "BtnEvac",    "E · REQUEST EVAC",  NATOPalette.FriendlyGreen, -290, () => OnPressEvac());
        }

        // ---- button handlers (also publish to the radio so the CCC sees them) ----
        public void OnPressFire()
        {
            if (_tracked == null) return;
            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = "HQ", timestampUtc = System.DateTime.UtcNow, fromCallsign = _tracked.callsign,
                text = "FIRE MISSION REQUEST — designating target via tablet",
                severity = RadioSeverity.Warning
            });
        }
        public void OnPressMedevac()
        {
            if (_tracked == null) return;
            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = "MEDEVAC", timestampUtc = System.DateTime.UtcNow, fromCallsign = _tracked.callsign,
                text = $"MEDEVAC at my position — HP {Mathf.RoundToInt(_tracked.health)}/{Mathf.RoundToInt(_tracked.maxHealth)}",
                severity = RadioSeverity.Critical
            });
            CommandPing.Spawn(_tracked.transform.position, CommandPing.Kind.Other);
        }
        public void OnPressEvac()
        {
            if (_tracked == null) return;
            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = "MEDEVAC", timestampUtc = System.DateTime.UtcNow, fromCallsign = _tracked.callsign,
                text = "CASEVAC at my position — request extraction",
                severity = RadioSeverity.Critical
            });
            CommandPing.Spawn(_tracked.transform.position, CommandPing.Kind.Other);
        }

        // ---- helpers ----
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
        private Image MakeBar(Transform parent, string name, Color fillColour, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var bg = MakeImage(parent, name + "_bg", new Color(0.05f, 0.10f, 0.16f, 1f), aMin, aMax, offMin, offMax);
            var fGo = new GameObject(name + "_fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fGo.transform.SetParent(bg.transform, false);
            var fImg = fGo.GetComponent<Image>();
            fImg.color = fillColour;
            fImg.type = Image.Type.Filled;
            fImg.fillMethod = Image.FillMethod.Horizontal;
            fImg.fillAmount = 1f;
            fImg.raycastTarget = false;
            var fRt = fGo.GetComponent<RectTransform>();
            fRt.anchorMin = Vector2.zero; fRt.anchorMax = Vector2.one;
            fRt.offsetMin = Vector2.zero; fRt.offsetMax = Vector2.zero;
            return fImg;
        }
        private Button MakeBigButton(RectTransform parent, string name, string label, Color accent, float yOff, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(10, yOff - 32);
            rt.offsetMax = new Vector2(-10, yOff);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.07f, 0.13f, 0.20f, 1f);
            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(onClick);

            // Accent strip on the left.
            var strip = MakeImage(rt, "Accent", accent, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(4, 0));
            strip.raycastTarget = false;

            // Label.
            var lbl = MakeText(rt, "Lbl", label, 14, FontStyle.Bold, Color.white,
                Vector2.zero, Vector2.one, new Vector2(12, 0), new Vector2(-12, 0));
            lbl.alignment = TextAnchor.MiddleLeft;
            return btn;
        }
    }
}
