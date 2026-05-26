// =====================================================================
//  NATO C2 RTS Hybrid — IncomingRequestPanel.cs
//  ---------------------------------------------------------------------
//  Surfaces inbound CoT requests (fire missions, MEDEVACs) from external
//  TAK clients as modal HUD cards with ACCEPT / DENY buttons.
//
//  Why this matters: the moment we listen on a TAK Server, a JTAC with
//  an ATAK phone, a drone operator at another EUD, or a federated
//  partner can shove fire requests at us. Without an operator gate, we'd
//  either ignore them (bad) or auto-execute (worse — civilian + Rules of
//  Engagement liability). This panel makes the human accountable for
//  every external fires/medevac call.
//
//  Filters:
//      • Own-prefix UIDs (NATO-C2-*) are skipped — those are our own
//        outbound echoes coming back via federation.
//      • Each CoT uid is shown once until the operator chooses or it
//        passes its stale timestamp.
//
//  CoT types handled:
//      b-r-f-h-c   Call For Fire   → ACCEPT issues IssueCommand(Attack)
//                                    at the target's lat/lon-derived world pos
//                                    DENY publishes a b-r-f-h-c with how="m-r"
//                                    (recall) + ack remarks back to sender.
//      b-r-c-m     MEDEVAC request → ACCEPT routes the closest friendly
//                                    Medic-type infantry to the patient
//                                    location AND publishes a confirm.
//                                    DENY publishes a deny ack.
//
//  Future extension: hook into ROE / authorization matrix — some
//  requests should auto-approve for the highest-precedence sender, and
//  some should escalate to a second-person confirm.
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NATO.C2.Net;

namespace NATO.C2.UI
{
    [DefaultExecutionOrder(160)]
    [AddComponentMenu("NATO C2/Incoming Request Panel")]
    public class IncomingRequestPanel : MonoBehaviour
    {
        [Header("Projection (must match TakServerCotAdapter)")]
        public double originLat = 38.7400;
        public double originLon = 22.2540;
        public float  metresPerUnit = 50f;

        [Header("Filter")]
        [Tooltip("UID prefix that identifies our own outbound events — skipped to avoid echo loops.")]
        public string ownPrefix = "NATO-C2-";

        [Header("UI")]
        [Min(280)] public int cardWidth  = 360;
        [Min(140)] public int cardHeight = 170;
        public float topMargin = 50f;
        [Tooltip("Vertical gap between stacked cards.")]
        public float cardGap   = 10f;

        [Header("Audio cue")]
        [Tooltip("Play a short procedural beep when a new request arrives. Distinct tone per type.")]
        public bool playAudioCue = true;
        [Range(0f, 1f)] public float audioVolume = 0.45f;
        [Tooltip("Frequency in Hz for CFF beep (b-r-f-h-c).")]
        public float cffToneHz = 880f;        // A5 — alarm-style high
        [Tooltip("Frequency in Hz for MEDEVAC beep (b-r-c-m).")]
        public float medevacToneHz = 660f;    // E5 — softer alert
        public float beepLengthSeconds = 0.6f;

        // ---------- runtime ------------------------------------------
        private FeedHub _hub;
        private TakServerCotAdapter _tak;
        private NATO_C2_Manager _mgr;
        private Canvas _canvas;
        private RectTransform _root;
        private readonly Dictionary<string, RequestCard> _cards = new Dictionary<string, RequestCard>();
        private AudioSource _audio;
        private AudioClip _cffClip;
        private AudioClip _medevacClip;

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            BuildContainer();
            BuildAudio();
        }

        private void Start()
        {
            _hub = FeedHub.Instance;
            _tak = FindAnyObjectByType<TakServerCotAdapter>();
            _mgr = NATO_C2_Manager.Instance;
            if (_hub != null) _hub.OnCot += HandleCot;
        }

        private void OnDestroy()
        {
            if (_hub != null) _hub.OnCot -= HandleCot;
        }

        // ---------- inbound handler ----------------------------------
        private void HandleCot(CotEvent ev)
        {
            if (string.IsNullOrEmpty(ev.uid)) return;

            // Federation sync: if another operator has ACCEPTed or DENYed a
            // request we have open, they publish a b-m-p-w marker whose
            // callsign starts with ACK-FIRES- / ACK-MEDEVAC- / DENY-FIRES-
            // etc. We dismiss any matching open card here so the queue stays
            // consistent across operators.
            if (ev.type != null && ev.type.StartsWith("b-m-p"))
            {
                string ackCs = ExtractCallsign(ev.xmlDetail);
                if (!string.IsNullOrEmpty(ackCs) && IsAckOrDeny(ackCs, out string requesterCallsign))
                {
                    TryDismissByRequester(requesterCallsign, ackCs);
                }
                return;
            }

            // Use the operator-specific prefix when available so two stations
            // on the same TAK Server filter out their OWN echoes but DO see
            // each other's requests (proper co-op behaviour).
            string effectiveOwn = NATO.C2.Net.OperatorIdentity.Instance != null
                ? NATO.C2.Net.OperatorIdentity.Instance.CotPrefix()
                : ownPrefix;
            if (!string.IsNullOrEmpty(effectiveOwn) && ev.uid.StartsWith(effectiveOwn)) return;
            if (_cards.ContainsKey(ev.uid)) return;

            bool isCFF     = ev.type != null && ev.type.StartsWith("b-r-f");
            bool isMedevac = ev.type != null && ev.type.StartsWith("b-r-c-m");
            if (!isCFF && !isMedevac) return;

            var card = BuildCard(ev, isCFF);
            _cards[ev.uid] = card;
            RelayoutCards();

            if (playAudioCue && _audio != null)
            {
                var clip = isCFF ? _cffClip : _medevacClip;
                if (clip != null) _audio.PlayOneShot(clip, audioVolume);
            }
        }

        // ---------- audio --------------------------------------------
        //  Generates two short procedural beeps at runtime so we don't
        //  ship an .ogg/.wav file with the package.
        //    CFF     — pulsing high tone (alarm-style, two short bursts).
        //    MEDEVAC — single softer tone (less urgent, more identifying).
        private void BuildAudio()
        {
            var go = new GameObject("RequestAudio", typeof(AudioSource));
            go.transform.SetParent(transform, false);
            _audio = go.GetComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f; // 2D — same volume regardless of camera position.

            _cffClip     = MakeBeepClip("CFFBeep",     cffToneHz,     beepLengthSeconds, pulses: 3);
            _medevacClip = MakeBeepClip("MedevacBeep", medevacToneHz, beepLengthSeconds, pulses: 1);
        }

        private static AudioClip MakeBeepClip(string name, float toneHz, float lengthSec, int pulses)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(lengthSec * sampleRate));
            var samples = new float[sampleCount];

            // Square-wave envelope: divide the clip into `pulses` segments,
            // each "on" for 60% and "off" for 40% — gives a recognisable
            // alert-pattern rather than a flat tone.
            float pulseSeconds = lengthSec / pulses;
            int pulseSamples = sampleCount / pulses;
            int onSamples = Mathf.RoundToInt(pulseSamples * 0.6f);

            for (int i = 0; i < sampleCount; i++)
            {
                int pulseIdx = (i / pulseSamples);
                int withinPulse = i - pulseIdx * pulseSamples;
                bool gateOn = withinPulse < onSamples;
                if (!gateOn) { samples[i] = 0f; continue; }

                float t = i / (float)sampleRate;
                float carrier = Mathf.Sin(2f * Mathf.PI * toneHz * t);

                // Gentle attack/decay inside each pulse so we don't click.
                float env;
                int fadeSamples = Mathf.Min(800, onSamples / 4);
                if (withinPulse < fadeSamples)
                    env = withinPulse / (float)fadeSamples;
                else if (withinPulse > onSamples - fadeSamples)
                    env = (onSamples - withinPulse) / (float)fadeSamples;
                else
                    env = 1f;

                samples[i] = carrier * env * 0.6f;
            }

            var clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // ---------- container + cards --------------------------------
        private void BuildContainer()
        {
            var go = new GameObject("IncomingRequests",
                typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(transform, false);
            _root = go.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(0.5f, 1f);
            _root.anchorMax = new Vector2(0.5f, 1f);
            _root.pivot     = new Vector2(0.5f, 1f);
            _root.anchoredPosition = new Vector2(0f, -topMargin);
            _root.sizeDelta = new Vector2(cardWidth, 0f);
        }

        private RequestCard BuildCard(CotEvent ev, bool isCFF)
        {
            var go = new GameObject("Request_" + ev.uid,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_root, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(cardWidth, cardHeight);

            var bg = go.GetComponent<Image>();
            bg.color = isCFF
                ? new Color(0.30f, 0.06f, 0.06f, 0.95f)
                : new Color(0.30f, 0.18f, 0.04f, 0.95f);
            bg.raycastTarget = true;

            // Type tag (red CFF / amber MEDEVAC bar on top).
            var tag = MakeText(go.transform, "Tag",
                isCFF ? "▲ FIRE MISSION REQUEST" : "✚ MEDEVAC REQUEST",
                17, FontStyle.Bold,
                isCFF ? new Color(1f, 0.45f, 0.45f) : new Color(1f, 0.78f, 0.30f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-cardWidth * 0.5f + 14, -28), new Vector2(cardWidth * 0.5f - 14, -8));
            tag.alignment = TextAnchor.UpperLeft;

            // Sender callsign.
            string callsign = ExtractCallsign(ev.xmlDetail) ?? ev.uid;
            var sender = MakeText(go.transform, "Sender",
                "from <b>" + callsign + "</b>",
                14, FontStyle.Normal, new Color(0.85f, 0.95f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-cardWidth * 0.5f + 14, -52), new Vector2(cardWidth * 0.5f - 14, -32));
            sender.alignment = TextAnchor.UpperLeft;

            // Grid coords.
            var grid = MakeText(go.transform, "Grid",
                $"grid {ev.latitude:F5}, {ev.longitude:F5}   hae {ev.hae:F0} m",
                12, FontStyle.Normal, new Color(0.70f, 0.85f, 1f, 0.9f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-cardWidth * 0.5f + 14, -74), new Vector2(cardWidth * 0.5f - 14, -56));
            grid.alignment = TextAnchor.UpperLeft;

            // Buttons.
            float btnH = 36f;
            float btnW = (cardWidth - 14 * 3) * 0.5f;
            var accept = MakeButton(go.transform, "ACCEPT",
                new Vector2(-btnW * 0.5f - 4, -cardHeight + btnH * 0.5f + 14),
                new Vector2(btnW, btnH),
                new Color(0.30f, 0.95f, 0.45f));
            accept.GetComponent<Image>().color = new Color(0.30f, 0.95f, 0.45f, 0.25f);
            accept.onClick.AddListener(() => OnAccept(ev, isCFF, callsign));

            var deny = MakeButton(go.transform, "DENY",
                new Vector2( btnW * 0.5f + 4, -cardHeight + btnH * 0.5f + 14),
                new Vector2(btnW, btnH),
                new Color(0.95f, 0.40f, 0.40f));
            deny.GetComponent<Image>().color = new Color(0.95f, 0.40f, 0.40f, 0.25f);
            deny.onClick.AddListener(() => OnDeny(ev, isCFF, callsign));

            return new RequestCard { uid = ev.uid, root = rt, ev = ev };
        }

        private void RelayoutCards()
        {
            float y = 0f;
            foreach (var c in _cards.Values)
            {
                if (c.root == null) continue;
                c.root.anchoredPosition = new Vector2(0f, y);
                y -= cardHeight + cardGap;
            }
        }

        // Parse callsign of the form "ACK-FIRES-{requester}" / "DENY-MEDEVAC-{requester}"
        // and return the requester. Returns false if it's not an ACK/DENY marker.
        private static bool IsAckOrDeny(string callsign, out string requester)
        {
            requester = null;
            string[] prefixes = {
                "ACK-FIRES-", "ACK-MEDEVAC-", "DENY-FIRES-", "DENY-MEDEVAC-"
            };
            foreach (var p in prefixes)
            {
                if (callsign.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    requester = callsign.Substring(p.Length);
                    return true;
                }
            }
            return false;
        }

        // Dismiss the first card whose original requester matches.
        private void TryDismissByRequester(string requesterCallsign, string ackCs)
        {
            string match = null;
            foreach (var kv in _cards)
            {
                string cardRequester = ExtractCallsign(kv.Value.ev.xmlDetail);
                if (cardRequester != null &&
                    cardRequester.Equals(requesterCallsign, StringComparison.OrdinalIgnoreCase))
                {
                    match = kv.Key;
                    break;
                }
            }
            if (match != null)
            {
                _hub?.PublishRadio(new RadioMessage
                {
                    net = "TANGO-6", timestampUtc = DateTime.UtcNow,
                    fromCallsign = "C2-AI",
                    text = $"federation: request from {requesterCallsign} resolved by peer — dismissing card ({ackCs})",
                    severity = RadioSeverity.System
                });
                Dismiss(match);
            }
        }

        private void Dismiss(string uid)
        {
            if (_cards.TryGetValue(uid, out var c))
            {
                if (c.root != null) Destroy(c.root.gameObject);
                _cards.Remove(uid);
                RelayoutCards();
            }
        }

        // ---------- action handlers ----------------------------------
        private void OnAccept(CotEvent ev, bool isCFF, string callsign)
        {
            Vector3 world = LatLonToWorld(ev.latitude, ev.longitude);
            _mgr ??= NATO_C2_Manager.Instance;

            if (isCFF)
            {
                // CFF: pick the closest Artillery, fall back to closest Tank, then
                // any friendly. Auto-select that one unit and issue Attack — the
                // operator can override but the default ROE-aligned action runs.
                var fires = PickClosest(world, UnitType.Artillery, UnitType.Tank);
                string dispatched = "current selection";
                if (fires != null)
                {
                    _mgr?.SetSelection(new[] { fires });
                    dispatched = $"{fires.unitType} {fires.callsign}";
                    DispatchTrace.Create(fires.transform.position, world,
                        new Color(1f, 0.45f, 0.30f, 1f), lifetime: 5f);
                }
                _mgr?.IssueCommand(CommandOrder.Attack, world);
                _tak?.PublishMarker(world, label: "ACK-FIRES-" + callsign,
                                    cotType: "b-m-p-w", staleSec: 120);
                _hub?.PublishRadio(new RadioMessage
                {
                    net = "TANGO-6", timestampUtc = DateTime.UtcNow,
                    fromCallsign = "OPS-6",
                    text = $"<color=#6f6>ACCEPT</color> fire mission from {callsign} at " +
                           $"{ev.latitude:F4},{ev.longitude:F4} — dispatching {dispatched}",
                    severity = RadioSeverity.Warning
                });
            }
            else
            {
                // MEDEVAC: pick closest Medic, fall back to closest Infantry, then any.
                var medic = PickClosest(world, UnitType.Medic, UnitType.Infantry);
                string dispatched = "current selection";
                if (medic != null)
                {
                    _mgr?.SetSelection(new[] { medic });
                    dispatched = $"{medic.unitType} {medic.callsign}";
                    DispatchTrace.Create(medic.transform.position, world,
                        new Color(0.95f, 0.60f, 0.20f, 1f), lifetime: 5f);
                }
                _mgr?.IssueCommand(CommandOrder.Move, world);
                _tak?.PublishMarker(world, label: "ACK-MEDEVAC-" + callsign,
                                    cotType: "b-m-p-w", staleSec: 300);
                _hub?.PublishRadio(new RadioMessage
                {
                    net = "MEDEVAC", timestampUtc = DateTime.UtcNow,
                    fromCallsign = "OPS-6",
                    text = $"<color=#6f6>ACCEPT</color> MEDEVAC from {callsign} — dispatching {dispatched}",
                    severity = RadioSeverity.Warning
                });
            }
            Dismiss(ev.uid);
        }

        /// <summary>
        /// Pick the closest friendly to <paramref name="target"/> that matches one of the
        /// preferred unit types, in order. Falls through to any friendly. Skips units with
        /// zero health.
        /// </summary>
        private Agent PickClosest(Vector3 target, params UnitType[] preferredTypes)
        {
            if (_mgr == null) return null;
            var agents = _mgr.Agents;

            foreach (var preferType in preferredTypes)
            {
                Agent best = null;
                float bestSq = float.MaxValue;
                for (int i = 0; i < agents.Count; i++)
                {
                    var a = agents[i];
                    if (a == null || a.health <= 0f) continue;
                    if (a.affiliation != Affiliation.Friendly) continue;
                    if (a.unitType != preferType) continue;
                    float d = (a.transform.position - target).sqrMagnitude;
                    if (d < bestSq) { bestSq = d; best = a; }
                }
                if (best != null) return best;
            }

            // Final fallback: any live friendly.
            Agent any = null;
            float anySq = float.MaxValue;
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null || a.health <= 0f) continue;
                if (a.affiliation != Affiliation.Friendly) continue;
                float d = (a.transform.position - target).sqrMagnitude;
                if (d < anySq) { anySq = d; any = a; }
            }
            return any;
        }

        private void OnDeny(CotEvent ev, bool isCFF, string callsign)
        {
            Vector3 world = LatLonToWorld(ev.latitude, ev.longitude);
            // Publish a marker with a denial label so the requester sees a "DENIED"
            // pin appear at their requested grid in their ATAK app.
            _tak?.PublishMarker(world, label: (isCFF ? "DENY-FIRES-" : "DENY-MEDEVAC-") + callsign,
                                cotType: "b-m-p-w", staleSec: 120);
            _hub?.PublishRadio(new RadioMessage
            {
                net = isCFF ? "TANGO-6" : "MEDEVAC", timestampUtc = DateTime.UtcNow,
                fromCallsign = "OPS-6",
                text = $"<color=#f66>DENY</color> {(isCFF ? "fire" : "medevac")} request from {callsign}",
                severity = RadioSeverity.Warning
            });
            Dismiss(ev.uid);
        }

        // ---------- helpers ------------------------------------------
        private Vector3 LatLonToWorld(double lat, double lon)
        {
            const double EarthRadiusM = 6_378_137d;
            double dz = (lat - originLat) * Math.PI / 180.0 * EarthRadiusM / metresPerUnit;
            double dx = (lon - originLon) * Math.PI / 180.0 * EarthRadiusM *
                        Math.Cos(originLat * Math.PI / 180.0) / metresPerUnit;
            return new Vector3((float)dx, 0f, (float)dz);
        }

        private static string ExtractCallsign(string xmlDetail)
        {
            if (string.IsNullOrEmpty(xmlDetail)) return null;
            int i = xmlDetail.IndexOf("callsign=\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i += "callsign=\"".Length;
            int j = xmlDetail.IndexOf('"', i);
            return j > i ? xmlDetail.Substring(i, j - i) : null;
        }

        private Text MakeText(Transform parent, string name, string text, int size, FontStyle style,
                              Color colour, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = colour;
            t.alignment = TextAnchor.UpperLeft; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = offMin; rt.offsetMax = offMax;
            return t;
        }

        private Button MakeButton(Transform parent, string text, Vector2 pos, Vector2 size, Color colour)
        {
            var go = new GameObject(text, typeof(RectTransform), typeof(CanvasRenderer),
                                    typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            var lblRt = labelGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = lblRt.offsetMax = Vector2.zero;
            var t = labelGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = 16; t.fontStyle = FontStyle.Bold;
            t.color = colour; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        // ---------- bookkeeping --------------------------------------
        private class RequestCard
        {
            public string uid;
            public RectTransform root;
            public CotEvent ev;
        }
    }
}
