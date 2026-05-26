// =====================================================================
//  NATO C2 RTS Hybrid — RadioChatPanel.cs
//  ---------------------------------------------------------------------
//  Bottom-right radio/chat panel. Subscribes to FeedHub.OnRadio and
//  renders a rolling buffer per tactical net (TANGO-6, HQ, MEDEVAC).
//
//  Severity drives the message colour:
//      Info     — neutral cyan-grey
//      Warning  — amber
//      Critical — red
//      System   — accent cyan (Mythos/HQ system messages)
// =====================================================================

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;
using NATO.C2.AI;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(120)]
    [AddComponentMenu("NATO C2/Radio Chat Panel")]
    public class RadioChatPanel : MonoBehaviour
    {
        [Header("Layout")]
        [Min(220)] public int panelWidth  = 360;
        [Min(120)] public int panelHeight = 200;
        public float bottomMargin = 72f;
        public float rightMargin  = 320f;     // sits to the left of the UnitDetailsPanel sidebar
        [Min(8)] public int maxLinesPerNet = 60;

        [Header("Nets")]
        public string[] netNames = { "TANGO-6", "HQ", "MEDEVAC" };

        // ---------- private state ---------------------------------------
        [Header("Operator")]
        [Tooltip("Operator callsign that appears on outbound messages typed into the input box.")]
        public string operatorCallsign = "OPS-6";

        private RectTransform _root;
        private Text _bodyText;
        private Text _headerText;
        private Image[] _tabBgs;
        private Text[]  _tabLabels;
        private string _activeNet;
        private readonly Dictionary<string, Queue<RadioMessage>> _bufferByNet = new Dictionary<string, Queue<RadioMessage>>();
        private InputField _inputField;

        private void Awake()
        {
            foreach (var n in netNames) _bufferByNet[n] = new Queue<RadioMessage>(maxLinesPerNet);
            _activeNet = netNames.Length > 0 ? netNames[0] : "TANGO-6";
            BuildUi();
        }

        private void OnEnable()
        {
            if (FeedHub.Instance != null)
            {
                FeedHub.Instance.OnRadio += OnRadio;
                FeedHub.Instance.OnCot   += OnCot;
            }
        }
        private void OnDisable()
        {
            if (FeedHub.Instance != null)
            {
                FeedHub.Instance.OnRadio -= OnRadio;
                FeedHub.Instance.OnCot   -= OnCot;
            }
        }
        private void Start()
        {
            // Late hook in case FeedHub awoke after this panel.
            if (FeedHub.Instance != null)
            {
                FeedHub.Instance.OnRadio += OnRadio;
                FeedHub.Instance.OnCot   += OnCot;
            }
        }

        // ---------- inbound CoT chat (GeoChat / b-t-f) ---------------
        //  Surface peer operator chat in the local radio panel with a
        //  [TAK] tag so the operator can tell it came from the network
        //  vs the local sim.
        private void OnCot(NATO.C2.Net.CotEvent ev)
        {
            if (ev.type == null || !ev.type.StartsWith("b-t-f")) return;

            // Skip our own echoes (per-operator UID prefix).
            string ownPrefix = NATO.C2.Net.OperatorIdentity.Instance != null
                ? NATO.C2.Net.OperatorIdentity.Instance.CotPrefix()
                : "NATO-C2-";
            if (!string.IsNullOrEmpty(ev.uid) && ev.uid.StartsWith(ownPrefix)) return;

            // Parse sender + body out of the GeoChat detail block.
            string sender = ExtractAttr(ev.xmlDetail, "senderCallsign") ?? "PEER";
            string room   = ExtractAttr(ev.xmlDetail, "chatroom") ?? "TANGO-6";
            string body   = ExtractRemarks(ev.xmlDetail) ?? ev.uid;

            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = room,
                timestampUtc = ev.start == default ? System.DateTime.UtcNow : ev.start,
                fromCallsign = "[TAK] " + sender,
                text = body,
                severity = RadioSeverity.System
            });

            // -----------------------------------------------------------
            //  JTAC-on-phone loop: if the peer's first word is a known
            //  verb ("fires", "medevac", "move on the village", …) route
            //  the body through IntentParser so the local operator's
            //  station executes the command. This is what lets a JTAC
            //  on an ATAK phone issue commands by chat.
            //
            //  Loop prevention: IntentParser.TryExecute publishes its
            //  ACK via FeedHub.PublishRadio (local only), NOT via the
            //  TAK adapter, so the executed command does NOT get echoed
            //  back over the wire. We also strip our own UID-prefixed
            //  events at the top of this handler.
            // -----------------------------------------------------------
            if (IsCommandLikeBody(body))
                TryExecutePeerCommand(body, sender, room);
        }

        /// <summary>True if the body starts with one of our known intent verbs.</summary>
        private static bool IsCommandLikeBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            // Cheap first-token check. The IntentParser still owns full
            // disambiguation — this is just a gate to avoid running the
            // parser on every "roger that" or "wilco" peer chat.
            string trimmed = body.TrimStart();
            // Find first whitespace.
            int sp = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsWhiteSpace(trimmed[i])) { sp = i; break; }
            }
            string head = (sp < 0 ? trimmed : trimmed.Substring(0, sp)).ToLowerInvariant();
            // Whitelist of verbs IntentParser recognises (keep in sync with IntentVerb).
            switch (head)
            {
                case "move":
                case "go":
                case "advance":
                case "attack":
                case "engage":
                case "fire":
                case "fires":
                case "shoot":
                case "hold":
                case "stop":
                case "halt":
                case "loiter":
                case "orbit":
                case "rtb":
                case "return":
                case "medevac":
                case "casevac":
                case "launch":
                case "drone":
                case "drones":
                    return true;
                default:
                    return false;
            }
        }

        private void TryExecutePeerCommand(string body, string sender, string room)
        {
            var mgr     = NATO_C2_Manager.Instance ?? Object.FindAnyObjectByType<NATO_C2_Manager>();
            if (mgr == null) return;
            var mission = Object.FindAnyObjectByType<MissionOverlay>();
            var hud     = Object.FindAnyObjectByType<TacticalHUD>();

            Dictionary<int, List<Agent>> groups = null;
            if (hud != null && hud.ControlGroups != null)
            {
                groups = new Dictionary<int, List<Agent>>(hud.ControlGroups.Count);
                foreach (var kv in hud.ControlGroups) groups[kv.Key] = kv.Value;
            }

            // operatorCallsign passed as the PEER's callsign so any
            // CFF / MEDEVAC CoT the parser emits is attributed to them,
            // not to the local operator who's just relaying.
            string rationale = NATO.C2.AI.IntentParser.TryExecute(
                body, mgr, mission, groups,
                operatorCallsign: sender,
                net: room);

            if (string.IsNullOrEmpty(rationale)) return;

            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = room,
                timestampUtc = System.DateTime.UtcNow,
                fromCallsign = "C2-AI",
                text = $"<color=#6cf>ack</color> ({sender}) {rationale}",
                severity = RadioSeverity.System
            });
        }

        private static string ExtractAttr(string xml, string attr)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            int i = xml.IndexOf(attr + "=\"", System.StringComparison.Ordinal);
            if (i < 0) return null;
            i += attr.Length + 2;
            int j = xml.IndexOf('"', i);
            return j > i ? xml.Substring(i, j - i) : null;
        }

        private static string ExtractRemarks(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            int o = xml.IndexOf("<remarks", System.StringComparison.Ordinal);
            if (o < 0) return null;
            int gt = xml.IndexOf('>', o); if (gt < 0) return null;
            int c = xml.IndexOf("</remarks>", gt, System.StringComparison.Ordinal);
            if (c < 0) return null;
            return xml.Substring(gt + 1, c - gt - 1);
        }

        private void OnRadio(RadioMessage msg)
        {
            if (string.IsNullOrEmpty(msg.net)) msg.net = "TANGO-6";
            if (!_bufferByNet.TryGetValue(msg.net, out var q))
            {
                q = new Queue<RadioMessage>(maxLinesPerNet);
                _bufferByNet[msg.net] = q;
            }
            if (q.Count >= maxLinesPerNet) q.Dequeue();
            q.Enqueue(msg);
            if (msg.net == _activeNet) RefreshBody();
        }

        // =================================================================
        //  UI construction
        // =================================================================
        private void BuildUi()
        {
            var parent = transform; // HUD canvas root
            var root = new GameObject("RadioChatPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            root.transform.SetParent(parent, false);
            _root = root.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(1, 0); _root.anchorMax = new Vector2(1, 0);
            _root.pivot = new Vector2(1, 0);
            _root.sizeDelta = new Vector2(panelWidth, panelHeight);
            _root.anchoredPosition = new Vector2(-rightMargin, bottomMargin);
            var bg = root.GetComponent<Image>();
            bg.color = new Color(0.025f, 0.07f, 0.13f, 0.95f);
            bg.raycastTarget = false;

            // Accent strip (left edge).
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            accent.transform.SetParent(_root, false);
            var aImg = accent.GetComponent<Image>();
            aImg.color = NATOPalette.AccentCyan;
            aImg.raycastTarget = false;
            var aRt = accent.GetComponent<RectTransform>();
            aRt.anchorMin = new Vector2(0, 0); aRt.anchorMax = new Vector2(0, 1);
            aRt.pivot = new Vector2(0, 0.5f);
            aRt.sizeDelta = new Vector2(3, 0);

            // Header label.
            _headerText = MakeText(_root, "Header", "RADIO  ·  TANGO-6", 13, FontStyle.Bold, NATOPalette.AccentCyan,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(12, -22), new Vector2(0, 18));

            // Tab row.
            _tabBgs = new Image[netNames.Length];
            _tabLabels = new Text[netNames.Length];
            for (int i = 0; i < netNames.Length; i++)
            {
                int captured = i;
                var tab = new GameObject($"Tab_{netNames[i]}",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                tab.transform.SetParent(_root, false);
                var tabRt = tab.GetComponent<RectTransform>();
                tabRt.anchorMin = new Vector2(0, 1); tabRt.anchorMax = new Vector2(0, 1);
                tabRt.pivot = new Vector2(0, 1);
                tabRt.sizeDelta = new Vector2(90, 22);
                tabRt.anchoredPosition = new Vector2(12 + i * 96, -42);
                var img = tab.GetComponent<Image>();
                img.color = new Color(0.06f, 0.13f, 0.22f, 1f);
                _tabBgs[i] = img;
                var btn = tab.GetComponent<Button>();
                btn.onClick.AddListener(() => SetActiveNet(netNames[captured]));

                var lbl = MakeText(tab.transform, "Lbl", netNames[i], 11, FontStyle.Bold, NATOPalette.AccentCyan,
                    Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                lbl.alignment = TextAnchor.MiddleCenter;
                _tabLabels[i] = lbl;
            }

            // Scroll body — leaves room for the input field at the bottom.
            _bodyText = MakeText(_root, "Body", "", 11, FontStyle.Normal, Color.white,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(12, 32), new Vector2(-12, -72));
            _bodyText.alignment = TextAnchor.LowerLeft;
            _bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _bodyText.verticalOverflow   = VerticalWrapMode.Truncate;

            // Operator input field at the bottom.
            var inGo = new GameObject("OperatorInput",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            inGo.transform.SetParent(_root, false);
            var inRt = inGo.GetComponent<RectTransform>();
            inRt.anchorMin = new Vector2(0, 0); inRt.anchorMax = new Vector2(1, 0);
            inRt.pivot = new Vector2(0.5f, 0);
            inRt.sizeDelta = new Vector2(-16, 22);
            inRt.anchoredPosition = new Vector2(0, 6);
            var inBg = inGo.GetComponent<Image>();
            inBg.color = new Color(0.05f, 0.12f, 0.20f, 1f);
            _inputField = inGo.GetComponent<InputField>();

            // Input text element.
            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            txtGo.transform.SetParent(inGo.transform, false);
            var inTxt = txtGo.GetComponent<Text>();
            inTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            inTxt.fontSize = 11;
            inTxt.color = Color.white;
            inTxt.alignment = TextAnchor.MiddleLeft;
            inTxt.supportRichText = false;
            inTxt.raycastTarget = false;
            var inTxtRt = txtGo.GetComponent<RectTransform>();
            inTxtRt.anchorMin = Vector2.zero; inTxtRt.anchorMax = Vector2.one;
            inTxtRt.offsetMin = new Vector2(8, 0); inTxtRt.offsetMax = new Vector2(-6, 0);
            _inputField.textComponent = inTxt;

            // Placeholder.
            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            phGo.transform.SetParent(inGo.transform, false);
            var phTxt = phGo.GetComponent<Text>();
            phTxt.font = inTxt.font;
            phTxt.fontSize = 11;
            phTxt.fontStyle = FontStyle.Italic;
            phTxt.color = new Color(0.55f, 0.67f, 0.82f, 0.7f);
            phTxt.alignment = TextAnchor.MiddleLeft;
            phTxt.text = $"e.g. \"TANGO-6 move to TRP-1\"  ·  \"group 2 attack EA SLEDGE\"  ·  \"MEDEVAC at TANGO-6\"";
            phTxt.raycastTarget = false;
            var phRt = phGo.GetComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero; phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(8, 0); phRt.offsetMax = new Vector2(-6, 0);
            _inputField.placeholder = phTxt;

            _inputField.onSubmit.AddListener(OnOperatorSubmit);

            RefreshTabs();
        }

        private void OnOperatorSubmit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string body = text.Trim();
            // @CALLSIGN prefix routes to that unit.
            string toUnit = null;
            if (body.StartsWith("@"))
            {
                int sp = body.IndexOf(' ');
                if (sp > 1)
                {
                    toUnit = body.Substring(1, sp - 1).ToUpperInvariant();
                    body = body.Substring(sp + 1).Trim();
                }
            }
            string display = toUnit != null ? $"@{toUnit} {body}" : body;

            // Always echo the operator's raw message so the chat reads naturally.
            FeedHub.Instance?.PublishRadio(new RadioMessage
            {
                net = _activeNet,
                timestampUtc = System.DateTime.UtcNow,
                fromCallsign = operatorCallsign,
                text = display,
                severity = RadioSeverity.System
            });

            // Mirror the message over CoT GeoChat so peers on the TAK Server
            // (other Unity instances + ATAK phones) see it persisted in the
            // server's history. Skip if the chat is intent (commands handled
            // by IntentParser get echoed as ACK lines from the bot anyway).
            var takAdapter = Object.FindAnyObjectByType<NATO.C2.Net.TakServerCotAdapter>();
            takAdapter?.PublishChat(display, operatorCallsign, room: _activeNet);

            // Try to resolve the message as an intent and execute it.
            // If parsing fails, the chat message above is the only effect — no harm.
            var mgr     = NATO_C2_Manager.Instance ?? Object.FindAnyObjectByType<NATO_C2_Manager>();
            var mission = Object.FindAnyObjectByType<MissionOverlay>();
            var hud     = Object.FindAnyObjectByType<TacticalHUD>();
            Dictionary<int, List<Agent>> groups = null;
            if (hud != null && hud.ControlGroups != null)
            {
                groups = new Dictionary<int, List<Agent>>(hud.ControlGroups.Count);
                foreach (var kv in hud.ControlGroups) groups[kv.Key] = kv.Value;
            }
            if (mgr != null)
            {
                string rationale = IntentParser.TryExecute(body, mgr, mission, groups,
                                                           operatorCallsign, _activeNet);
                if (!string.IsNullOrEmpty(rationale))
                {
                    // Surface a brief ACK so the operator sees the system understood them.
                    FeedHub.Instance?.PublishRadio(new RadioMessage
                    {
                        net = _activeNet,
                        timestampUtc = System.DateTime.UtcNow,
                        fromCallsign = "C2-AI",
                        text = $"<color=#6cf>ack</color> {rationale}",
                        severity = RadioSeverity.System
                    });
                }
            }

            _inputField.text = "";
            _inputField.ActivateInputField();
        }

        private Text MakeText(Transform parent, string name, string text, int size, FontStyle style, Color colour,
                              Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = colour;
            t.alignment = TextAnchor.UpperLeft;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return t;
        }

        private void SetActiveNet(string net)
        {
            _activeNet = net;
            RefreshTabs();
            RefreshBody();
        }

        private void RefreshTabs()
        {
            for (int i = 0; i < netNames.Length; i++)
            {
                bool active = netNames[i] == _activeNet;
                if (_tabBgs[i] != null)
                    _tabBgs[i].color = active ? NATOPalette.AccentCyan : new Color(0.06f, 0.13f, 0.22f, 1f);
                if (_tabLabels[i] != null)
                    _tabLabels[i].color = active ? NATOPalette.BackgroundBlue : NATOPalette.AccentCyan;
            }
            if (_headerText != null) _headerText.text = "RADIO  ·  " + _activeNet;
        }

        private void RefreshBody()
        {
            if (_bodyText == null) return;
            if (!_bufferByNet.TryGetValue(_activeNet, out var q) || q.Count == 0)
            {
                _bodyText.text = "<color=#5d6f80>—  no traffic  —</color>";
                return;
            }
            var sb = new StringBuilder(2048);
            foreach (var m in q)
            {
                string colour = ColorForSeverity(m.severity);
                string ts = m.timestampUtc.ToLocalTime().ToString("HH:mm:ss");
                sb.Append("<color=#6c7e92>").Append(ts).Append("</color> ")
                  .Append("<color=").Append(colour).Append(">")
                  .Append(string.IsNullOrEmpty(m.fromCallsign) ? "—" : m.fromCallsign)
                  .Append(":</color> ")
                  .Append(m.text).Append('\n');
            }
            _bodyText.text = sb.ToString();
        }

        private static string ColorForSeverity(RadioSeverity s) => s switch
        {
            RadioSeverity.Warning  => "#F2C24A",
            RadioSeverity.Critical => "#FF5B5B",
            RadioSeverity.System   => "#00D4FF",
            _                       => "#7eff9a"
        };
    }
}
