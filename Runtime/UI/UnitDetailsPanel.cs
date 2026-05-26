// =====================================================================
//  NATO C2 RTS Hybrid — UnitDetailsPanel.cs
//  ---------------------------------------------------------------------
//  Right-side telemetry sidebar. Shows real-time unit details when ONE
//  unit is selected; collapses to an aggregate roster when multiple are
//  selected; hides when nothing is selected.
//
//  Auto-built at runtime — no Inspector wiring required. Just attach
//  this MonoBehaviour to the same GameObject as the HUD canvas root
//  and it constructs its own RectTransforms on Awake.
//
//  Data sources:
//      Identity      Agent.callsign / sidc / unitType / affiliation
//      Echelon       Agent.echelon
//      Personnel     Agent.rank, Agent.commandingOfficer
//      Status        Agent.health / ammo / currentOrder
//      Motion        Agent.currentVelocity (speed + heading)
//      Position      transform.position → synthetic MGRS string
//      Sensors       Agent.sensors (GPS / Radio / Radar / BFT / EW)
// =====================================================================

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(110)]
    [AddComponentMenu("NATO C2/Unit Details Panel")]
    public class UnitDetailsPanel : MonoBehaviour
    {
        [Header("Layout")]
        [Min(180)] public int panelWidth = 300;
        public float topMargin = 64f;
        public float bottomMargin = 72f;

        [Header("Synthetic MGRS")]
        [Tooltip("MGRS Grid Zone Designator (GZD) prefix shown in the position field.")]
        public string mgrsGZD = "33T";
        [Tooltip("MGRS 100km square ID (two letters).")]
        public string mgrsSquare = "WN";

        // ---------- live state ------------------------------------------
        private RectTransform _root;
        private CanvasGroup _cg;
        private Text _headerCallsign;
        private Text _headerSidc;
        private Text _identityType;
        private Text _identityEchelon;
        private Text _identityRank;
        private Text _identityOfficer;
        private Image _hpFill;
        private Image _ammoFill;
        private Text _hpText;
        private Text _ammoText;
        private Text _statusOrder;
        private Text _statusSpeed;
        private Text _statusHeading;
        private Text _statusPosition;
        private Image[] _sensorDots;
        private Text[]  _sensorLabels;
        private Text _multiSelectSummary;
        private RectTransform _detailGroup;
        private RectTransform _multiGroup;

        private Agent _trackedAgent;

        private void Awake()
        {
            BuildUi();
            if (NATO_C2_Manager.Instance != null)
                NATO_C2_Manager.Instance.OnSelectionChanged += OnSelectionChanged;
            Hide();
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

        // =================================================================
        //  Selection routing
        // =================================================================
        private void OnSelectionChanged(IReadOnlyList<Agent> sel)
        {
            if (sel == null || sel.Count == 0) { Hide(); return; }
            if (sel.Count == 1) { ShowSingle(sel[0]); return; }
            ShowMulti(sel);
        }

        private void Hide()
        {
            _trackedAgent = null;
            if (_cg != null) _cg.alpha = 0f;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void ShowSingle(Agent a)
        {
            _trackedAgent = a;
            if (_root != null) _root.gameObject.SetActive(true);
            if (_cg != null) _cg.alpha = 1f;
            if (_detailGroup != null) _detailGroup.gameObject.SetActive(true);
            if (_multiGroup  != null) _multiGroup .gameObject.SetActive(false);
        }

        private void ShowMulti(IReadOnlyList<Agent> sel)
        {
            _trackedAgent = null;
            if (_root != null) _root.gameObject.SetActive(true);
            if (_cg != null) _cg.alpha = 1f;
            if (_detailGroup != null) _detailGroup.gameObject.SetActive(false);
            if (_multiGroup  != null) _multiGroup .gameObject.SetActive(true);

            var sb = new StringBuilder(256);
            int countByType, totalHp = 0, totalAmmo = 0, totalMaxHp = 0, totalMaxAmmo = 0;
            var typeCounts = new Dictionary<UnitType, int>();
            foreach (var a in sel)
            {
                if (a == null) continue;
                if (!typeCounts.TryGetValue(a.unitType, out countByType)) countByType = 0;
                typeCounts[a.unitType] = countByType + 1;
                totalHp += Mathf.RoundToInt(a.health);
                totalMaxHp += Mathf.RoundToInt(a.maxHealth);
                totalAmmo += Mathf.RoundToInt(a.ammo);
                totalMaxAmmo += Mathf.RoundToInt(a.maxAmmo);
            }
            sb.Append("ROSTER  ").Append(sel.Count).Append(" units\n\n");
            foreach (var kvp in typeCounts) sb.Append("  ").Append(kvp.Value.ToString("00")).Append("× ").Append(kvp.Key).Append('\n');
            sb.Append("\nAGGREGATE HP    ").Append(totalHp).Append('/').Append(totalMaxHp).Append('\n');
            sb.Append("AGGREGATE AMMO  ").Append(totalAmmo).Append('/').Append(totalMaxAmmo).Append('\n');
            if (_multiSelectSummary != null) _multiSelectSummary.text = sb.ToString();
        }

        // =================================================================
        //  Live tick — refresh single-unit fields every frame.
        // =================================================================
        private void Update()
        {
            if (_trackedAgent == null) return;
            var a = _trackedAgent;

            if (_headerCallsign != null) _headerCallsign.text = string.IsNullOrEmpty(a.callsign) ? "—" : a.callsign;
            if (_headerSidc     != null) _headerSidc.text     = a.ResolveSIDC();

            if (_identityType    != null) _identityType.text    = $"{a.unitType}  ·  {a.affiliation}";
            if (_identityEchelon != null) _identityEchelon.text = $"ECH {EchelonLabel(a.echelon)}  ·  LAYER {a.layer}";
            if (_identityRank    != null) _identityRank.text    = string.IsNullOrEmpty(a.rank) ? "RANK —" : ("RANK " + a.rank);
            if (_identityOfficer != null) _identityOfficer.text = string.IsNullOrEmpty(a.commandingOfficer) ? "CO —" : ("CO " + a.commandingOfficer);

            float hpPct   = a.maxHealth > 0 ? Mathf.Clamp01(a.health / a.maxHealth) : 0f;
            float ammoPct = a.maxAmmo   > 0 ? Mathf.Clamp01(a.ammo   / a.maxAmmo)   : 0f;
            if (_hpFill   != null) _hpFill.fillAmount   = hpPct;
            if (_ammoFill != null) _ammoFill.fillAmount = ammoPct;
            if (_hpText   != null) _hpText.text   = $"HP   {Mathf.RoundToInt(a.health)}/{Mathf.RoundToInt(a.maxHealth)}";
            if (_ammoText != null) _ammoText.text = $"AMMO {Mathf.RoundToInt(a.ammo)}/{Mathf.RoundToInt(a.maxAmmo)}";
            if (_hpFill   != null) _hpFill.color   = hpPct   > 0.5f ? NATOPalette.FriendlyGreen
                                                  : hpPct   > 0.25f ? NATOPalette.NeutralYellow
                                                                    : NATOPalette.HostileRed;
            if (_ammoFill != null) _ammoFill.color = ammoPct > 0.4f ? NATOPalette.AccentCyan
                                                  : ammoPct > 0.15f ? NATOPalette.NeutralYellow
                                                                    : NATOPalette.HostileRed;

            if (_statusOrder    != null) _statusOrder.text    = "ORDER  " + a.currentOrder;
            float speed = a.currentVelocity.magnitude;
            float heading = Mathf.Atan2(a.desiredFacing.x, a.desiredFacing.z) * Mathf.Rad2Deg;
            if (heading < 0f) heading += 360f;
            if (_statusSpeed    != null) _statusSpeed.text    = "SPEED  " + speed.ToString("0.0") + " m/s";
            if (_statusHeading  != null) _statusHeading.text  = "HDG    " + Mathf.RoundToInt(heading).ToString("000") + "°";
            if (_statusPosition != null) _statusPosition.text = "MGRS   " + BuildMgrs(a.transform.position);

            // Sensor health badges.
            if (_sensorDots != null)
            {
                for (int i = 0; i < _sensorDots.Length; i++)
                {
                    bool on = ((int)a.sensors & (1 << i)) != 0;
                    if (_sensorDots[i] != null)
                        _sensorDots[i].color = on ? NATOPalette.FriendlyGreen : new Color(0.4f, 0.4f, 0.4f, 0.6f);
                    if (_sensorLabels[i] != null)
                        _sensorLabels[i].color = on ? Color.white : new Color(0.55f, 0.55f, 0.6f);
                }
            }
        }

        // =================================================================
        //  Helpers
        // =================================================================
        private string BuildMgrs(Vector3 worldPos)
        {
            // Synthetic MGRS: easting + northing derived from world (x, z) so the
            // sidebar updates as units move. In production swap for a real
            // MGRSConverter that takes lat/lon from a BFT message.
            int easting  = Mathf.RoundToInt((worldPos.x + 1000f) * 10f);
            int northing = Mathf.RoundToInt((worldPos.z + 1000f) * 10f);
            return $"{mgrsGZD}{mgrsSquare} {easting:D5} {northing:D5}";
        }

        private static Sprite _whiteSpriteCache;
        private static Sprite WhiteSprite()
        {
            if (_whiteSpriteCache != null) return _whiteSpriteCache;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            tex.Apply(false);
            _whiteSpriteCache = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            return _whiteSpriteCache;
        }

        private static string EchelonLabel(int e) => e switch
        {
            11 => "TEAM", 12 => "SQUAD", 13 => "SECTION",
            14 => "PLT", 15 => "COY", 16 => "BN",
            17 => "BDE", 18 => "DIV", 19 => "CORPS",
            _ => "—"
        };

        // =================================================================
        //  Build the sidebar UI tree (one-time, on Awake).
        // =================================================================
        private void BuildUi()
        {
            var parent = transform; // expected to be the HUD canvas root
            var root = new GameObject("UnitDetailsPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            root.transform.SetParent(parent, false);
            _root = root.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(1, 0); _root.anchorMax = new Vector2(1, 1);
            _root.pivot = new Vector2(1, 0.5f);
            _root.sizeDelta = new Vector2(panelWidth, -(topMargin + bottomMargin));
            _root.anchoredPosition = new Vector2(0, (bottomMargin - topMargin) * 0.5f);
            var bgImg = root.GetComponent<Image>();
            bgImg.color = new Color(0.025f, 0.07f, 0.13f, 0.96f);
            bgImg.raycastTarget = false;
            _cg = root.GetComponent<CanvasGroup>();

            // Left accent strip.
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            accent.transform.SetParent(_root, false);
            var aImg = accent.GetComponent<Image>();
            aImg.color = NATOPalette.AccentCyan;
            aImg.raycastTarget = false;
            var aRt = accent.GetComponent<RectTransform>();
            aRt.anchorMin = new Vector2(0, 0); aRt.anchorMax = new Vector2(0, 1);
            aRt.pivot = new Vector2(0, 0.5f);
            aRt.sizeDelta = new Vector2(3, 0);
            aRt.anchoredPosition = Vector2.zero;

            // ----- Single-unit detail group -----
            var detail = new GameObject("Detail", typeof(RectTransform));
            detail.transform.SetParent(_root, false);
            _detailGroup = detail.GetComponent<RectTransform>();
            _detailGroup.anchorMin = Vector2.zero; _detailGroup.anchorMax = Vector2.one;
            _detailGroup.offsetMin = new Vector2(12, 12); _detailGroup.offsetMax = new Vector2(-12, -12);

            float y = 0f;
            _headerCallsign = Section(_detailGroup, "Callsign", 24, FontStyle.Bold, NATOPalette.AccentCyan, ref y, 32);
            _headerSidc     = Section(_detailGroup, "Sidc",     11, FontStyle.Normal, new Color(0.6f, 0.78f, 0.92f), ref y, 16);
            y += 8;
            _identityType    = Section(_detailGroup, "Type",     13, FontStyle.Bold,   Color.white, ref y, 18);
            _identityEchelon = Section(_detailGroup, "Echelon",  12, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 18);
            _identityRank    = Section(_detailGroup, "Rank",     12, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 18);
            _identityOfficer = Section(_detailGroup, "CO",       12, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 18);
            y += 8;
            HrLine(_detailGroup, ref y);
            _hpText = Section(_detailGroup, "HpLabel",  11, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 14);
            _hpFill = Bar(_detailGroup, NATOPalette.FriendlyGreen, ref y);
            _ammoText = Section(_detailGroup, "AmmoLabel", 11, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 14);
            _ammoFill = Bar(_detailGroup, NATOPalette.AccentCyan, ref y);
            y += 4;
            HrLine(_detailGroup, ref y);
            _statusOrder    = Section(_detailGroup, "Order",    13, FontStyle.Bold,   Color.white, ref y, 18);
            _statusSpeed    = Section(_detailGroup, "Speed",    12, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 18);
            _statusHeading  = Section(_detailGroup, "Heading",  12, FontStyle.Normal, new Color(0.7f, 0.85f, 0.95f), ref y, 18);
            _statusPosition = Section(_detailGroup, "Position", 12, FontStyle.Normal, NATOPalette.AccentCyan, ref y, 18);
            y += 4;
            HrLine(_detailGroup, ref y);
            Section(_detailGroup, "SensorHeading", 11, FontStyle.Bold, NATOPalette.AccentCyan, ref y, 16).text = "SENSORS";
            BuildSensorBadges(_detailGroup, ref y);

            // ----- Multi-select roster group -----
            var multi = new GameObject("Multi", typeof(RectTransform));
            multi.transform.SetParent(_root, false);
            _multiGroup = multi.GetComponent<RectTransform>();
            _multiGroup.anchorMin = Vector2.zero; _multiGroup.anchorMax = Vector2.one;
            _multiGroup.offsetMin = new Vector2(12, 12); _multiGroup.offsetMax = new Vector2(-12, -12);
            _multiSelectSummary = new GameObject("Summary", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)).GetComponent<Text>();
            _multiSelectSummary.transform.SetParent(_multiGroup, false);
            _multiSelectSummary.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _multiSelectSummary.fontSize = 13;
            _multiSelectSummary.color = Color.white;
            _multiSelectSummary.alignment = TextAnchor.UpperLeft;
            _multiSelectSummary.raycastTarget = false;
            _multiSelectSummary.horizontalOverflow = HorizontalWrapMode.Overflow;
            _multiSelectSummary.verticalOverflow = VerticalWrapMode.Overflow;
            var msRt = _multiSelectSummary.GetComponent<RectTransform>();
            msRt.anchorMin = Vector2.zero; msRt.anchorMax = Vector2.one;
            msRt.offsetMin = Vector2.zero; msRt.offsetMax = Vector2.zero;
        }

        private Text Section(RectTransform parent, string name, int size, FontStyle style, Color colour, ref float y, float h)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size; t.fontStyle = style; t.color = colour;
            t.alignment = TextAnchor.UpperLeft;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(0, h);
            rt.anchoredPosition = new Vector2(0, -y);
            y += h + 2;
            return t;
        }

        private void HrLine(RectTransform parent, ref float y)
        {
            var hr = new GameObject("Hr", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hr.transform.SetParent(parent, false);
            var img = hr.GetComponent<Image>();
            img.color = new Color(NATOPalette.AccentCyan.r, NATOPalette.AccentCyan.g, NATOPalette.AccentCyan.b, 0.20f);
            img.raycastTarget = false;
            var rt = hr.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(0, -y);
            y += 8;
        }

        private Image Bar(RectTransform parent, Color fill, ref float y)
        {
            var go = new GameObject("Bar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.05f, 0.10f, 0.18f, 1f);
            bg.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(0, 8);
            rt.anchoredPosition = new Vector2(0, -y);

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fillGo.transform.SetParent(go.transform, false);
            var fImg = fillGo.GetComponent<Image>();
            fImg.color = fill;
            fImg.raycastTarget = false;
            fImg.type = Image.Type.Filled;
            fImg.fillMethod = Image.FillMethod.Horizontal;
            fImg.fillAmount = 1f;
            var fRt = fillGo.GetComponent<RectTransform>();
            fRt.anchorMin = Vector2.zero; fRt.anchorMax = Vector2.one;
            fRt.offsetMin = Vector2.zero; fRt.offsetMax = Vector2.zero;
            // 1×1 white sprite (Unity 6 removed UI/Skin/UISprite.psd from builtin resources).
            fImg.sprite = WhiteSprite();
            bg.sprite   = fImg.sprite;
            y += 12;
            return fImg;
        }

        private void BuildSensorBadges(RectTransform parent, ref float y)
        {
            string[] names = { "GPS", "RADIO", "RADAR", "BFT", "EW" };
            _sensorDots = new Image[names.Length];
            _sensorLabels = new Text[names.Length];
            float perRow = 28f;
            for (int i = 0; i < names.Length; i++)
            {
                int col = i % 5;
                float x = col * 52f;

                var dotGo = new GameObject($"Dot{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                dotGo.transform.SetParent(parent, false);
                var img = dotGo.GetComponent<Image>();
                img.color = new Color(0.4f, 0.4f, 0.4f, 0.6f);
                img.raycastTarget = false;
                var rt = dotGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0, 1);
                rt.sizeDelta = new Vector2(8, 8);
                rt.anchoredPosition = new Vector2(x, -y);
                _sensorDots[i] = img;

                var lblGo = new GameObject($"Lbl{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                lblGo.transform.SetParent(parent, false);
                var lbl = lblGo.GetComponent<Text>();
                lbl.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                lbl.text = names[i];
                lbl.fontSize = 10;
                lbl.fontStyle = FontStyle.Bold;
                lbl.color = Color.white;
                lbl.alignment = TextAnchor.UpperLeft;
                lbl.raycastTarget = false;
                var lblRt = lblGo.GetComponent<RectTransform>();
                lblRt.anchorMin = new Vector2(0, 1); lblRt.anchorMax = new Vector2(0, 1);
                lblRt.pivot = new Vector2(0, 1);
                lblRt.sizeDelta = new Vector2(40, 12);
                lblRt.anchoredPosition = new Vector2(x + 12, -y + 1);
                _sensorLabels[i] = lbl;
            }
            y += perRow;
        }
    }
}
