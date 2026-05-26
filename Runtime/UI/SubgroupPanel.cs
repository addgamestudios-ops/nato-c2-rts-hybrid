// =====================================================================
//  NATO C2 RTS Hybrid — SubgroupPanel.cs
//  ---------------------------------------------------------------------
//  StarCraft-2-style "command card" / subgroup panel. When more than
//  one unit type is selected, this panel renders a row of portraits
//  bucketed by UnitType. Clicking a portrait subselects only that
//  type (the classic SC2 pattern). The count badge shows how many
//  units of that type are in the current selection.
//
//      ┌──────────┐  ┌──────────┐  ┌──────────┐
//      │ ARMOUR   │  │ DRONE    │  │ HELI     │
//      │ NATO sym │  │ NATO sym │  │ NATO sym │
//      │ ×7       │  │ ×12      │  │ ×3       │
//      └──────────┘  └──────────┘  └──────────┘
// =====================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(115)]
    [AddComponentMenu("NATO C2/Subgroup Panel")]
    public class SubgroupPanel : MonoBehaviour
    {
        [Header("Layout")]
        [Min(80)] public int portraitSize = 84;
        [Min(60)] public int panelHeight  = 110;
        public float bottomMargin = 72f;
        public float horizontalCentreOffset = 90f; // offset from canvas centre so the panel sits left of the radio chat
        [Tooltip("Max number of distinct unit types shown (rest scroll off).")]
        [Min(2)] public int maxTypes = 8;

        // ---------- runtime --------------------------------------------
        private RectTransform _root;
        private RectTransform _row;
        private MilsymbolBridge _bridge;
        private readonly List<PortraitCard> _cards = new List<PortraitCard>(8);

        private class PortraitCard
        {
            public RectTransform root;
            public RawImage symbol;
            public Text count;
            public Image bg;
            public Button button;
            public UnitType type;
        }

        private void Awake()
        {
            _bridge = GetComponent<MilsymbolBridge>();
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

            // Bucket by UnitType.
            var byType = new Dictionary<UnitType, List<Agent>>();
            foreach (var a in sel)
            {
                if (a == null) continue;
                if (!byType.TryGetValue(a.unitType, out var list))
                {
                    list = new List<Agent>();
                    byType[a.unitType] = list;
                }
                list.Add(a);
            }

            // Single-type selection — no need to show the panel; the unit
            // details sidebar already covers it.
            if (byType.Count < 2) { Hide(); return; }

            Show();

            int i = 0;
            foreach (var kvp in byType)
            {
                if (i >= maxTypes) break;
                if (i >= _cards.Count) AddCard();
                var card = _cards[i];
                card.type = kvp.Key;
                card.count.text = "×" + kvp.Value.Count;

                // Synthesize a representative symbol from a stand-in Agent —
                // use the first agent of this type for SIDC + affiliation.
                var sample = kvp.Value[0];
                if (_bridge != null && card.symbol != null)
                {
                    var tex = _bridge.ResolveSymbol(sample);
                    if (tex != null) card.symbol.texture = tex;
                }

                card.bg.color = new Color(0.05f, 0.11f, 0.18f, 0.92f);
                card.root.gameObject.SetActive(true);

                // Click handler: subselect this type only.
                var captured = new List<Agent>(kvp.Value);
                card.button.onClick.RemoveAllListeners();
                card.button.onClick.AddListener(() =>
                {
                    if (NATO_C2_Manager.Instance != null)
                        NATO_C2_Manager.Instance.SetSelection(captured);
                });
                i++;
            }
            for (int k = i; k < _cards.Count; k++)
                _cards[k].root.gameObject.SetActive(false);
        }

        private void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }
        private void Show()
        {
            if (_root != null) _root.gameObject.SetActive(true);
        }

        // =================================================================
        //  UI scaffold
        // =================================================================
        private void BuildUi()
        {
            var parent = transform;
            var rootGo = new GameObject("SubgroupPanel",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rootGo.transform.SetParent(parent, false);
            _root = rootGo.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0.5f, 0); _root.anchorMax = new Vector2(0.5f, 0);
            _root.pivot = new Vector2(0.5f, 0);
            _root.sizeDelta = new Vector2(maxTypes * (portraitSize + 8) + 16, panelHeight);
            _root.anchoredPosition = new Vector2(-horizontalCentreOffset, bottomMargin);
            var bg = rootGo.GetComponent<Image>();
            bg.color = new Color(0.025f, 0.07f, 0.13f, 0.85f);
            bg.raycastTarget = false;

            var rowGo = new GameObject("Row", typeof(RectTransform));
            rowGo.transform.SetParent(_root, false);
            _row = rowGo.GetComponent<RectTransform>();
            _row.anchorMin = Vector2.zero; _row.anchorMax = Vector2.one;
            _row.offsetMin = new Vector2(8, 8); _row.offsetMax = new Vector2(-8, -8);
        }

        private void AddCard()
        {
            int i = _cards.Count;
            var go = new GameObject($"Card_{i}",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(_row, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0.5f); rt.anchorMax = new Vector2(0, 0.5f);
            rt.pivot = new Vector2(0, 0.5f);
            rt.sizeDelta = new Vector2(portraitSize, portraitSize);
            rt.anchoredPosition = new Vector2(i * (portraitSize + 8), 0);

            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.05f, 0.11f, 0.18f, 0.92f);
            var btn = go.GetComponent<Button>();

            // Symbol thumb fills 80% of the card.
            var symGo = new GameObject("Symbol", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            symGo.transform.SetParent(go.transform, false);
            var sym = symGo.GetComponent<RawImage>();
            sym.raycastTarget = false;
            var symRt = symGo.GetComponent<RectTransform>();
            symRt.anchorMin = Vector2.zero; symRt.anchorMax = Vector2.one;
            symRt.offsetMin = new Vector2(6, 18); symRt.offsetMax = new Vector2(-6, -6);

            // Count badge at the bottom (×N).
            var countGo = new GameObject("Count", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            countGo.transform.SetParent(go.transform, false);
            var t = countGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 13;
            t.fontStyle = FontStyle.Bold;
            t.color = NATOPalette.AccentCyan;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            var tRt = countGo.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0, 0); tRt.anchorMax = new Vector2(1, 0);
            tRt.pivot = new Vector2(0.5f, 0);
            tRt.sizeDelta = new Vector2(0, 16);
            tRt.anchoredPosition = new Vector2(0, 2);

            _cards.Add(new PortraitCard { root = rt, symbol = sym, count = t, bg = bg, button = btn });
        }
    }
}
