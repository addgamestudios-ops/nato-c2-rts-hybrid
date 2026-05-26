// =====================================================================
//  NATO C2 RTS Hybrid — IntentParser.cs
//  ---------------------------------------------------------------------
//  Turns typed operator radio messages into real CommandOrder calls.
//
//      "TANGO-6 move to TRP-1"        → MOVE selection={callsign==TANGO-6}
//                                         target=Centroid(TRP-1)
//      "Group 2 attack EA SLEDGE"     → ATTACK selection=ControlGroup(2)
//                                         target=Centroid(EA SLEDGE)
//      "All units move to PL BRAVO"   → MOVE selection=Friendly target=PL BRAVO
//      "FIRES on grid 34S DH 12345 67890" → ATTACK MGRS→world
//      "MEDEVAC at TANGO-6"           → MEDEVAC ability on TANGO-6
//      "RTB"                          → RTB current selection
//
//  Design:
//      • Rule-based first pass. Cheap, deterministic, runs in 0ms,
//        good enough for SOP-style operator vocabulary.
//      • Pluggable IIntentBackend interface so we can swap to a real
//        LLM (Claude, GPT, on-prem Llama) when we want fuzzier intent
//        resolution. The LLM would return the SAME ParsedIntent struct.
//      • Resolves three classes of "where":
//          - Named mission feature   ("PL ALAMO", "EA SLEDGE", "TRP-1")
//          - MGRS grid               ("MGRS 34S DH 12345 67890")
//          - Direct callsign target  ("on TANGO-6")
//      • Resolves three classes of "who":
//          - Callsign     ("TANGO-6", "BRAVO-3")
//          - Control group("group 2", "team 2", "ctrl-2")
//          - Universal    ("all units", "everyone", "all friendly")
//      • Anything not parseable falls through as a normal chat message
//        — the operator never loses their text.
//
//  Threading: called from RadioChatPanel.OnOperatorSubmit on Unity main
//  thread. Returns immediately, no IO.
// =====================================================================

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using NATO.C2.Net;
using NATO.C2.UI;

namespace NATO.C2.AI
{
    public enum IntentVerb
    {
        Unknown,
        Move,
        Attack,
        Hold,
        Loiter,
        RTB,
        FireMission,
        Medevac,
        Casevac,
        LaunchDrones
    }

    public struct ParsedIntent
    {
        public bool       success;
        public IntentVerb verb;
        public List<Agent> who;        // resolved units. Empty = use current selection.
        public Vector3    where;       // world target (if applicable)
        public bool       hasWhere;
        public string     rationale;   // human-readable trace for the radio echo
    }

    /// <summary>Pluggable backend so a future LLM swap is one-line.</summary>
    public interface IIntentBackend
    {
        ParsedIntent Parse(string operatorText, IntentParserContext ctx);
    }

    /// <summary>Snapshot of the world the parser can lean on without holding live refs.</summary>
    public class IntentParserContext
    {
        public NATO_C2_Manager manager;
        public MissionOverlay  mission;
        public Dictionary<int, List<Agent>> controlGroups; // optional
    }

    // =====================================================================
    //  Rule-based default backend
    // =====================================================================
    public class RuleBasedIntentBackend : IIntentBackend
    {
        // Verb lexicon → IntentVerb. Order matters: more specific first.
        private static readonly (string token, IntentVerb verb)[] Verbs =
        {
            ("medevac",        IntentVerb.Medevac),
            ("casevac",        IntentVerb.Casevac),
            ("fire mission",   IntentVerb.FireMission),
            ("fires on",       IntentVerb.FireMission),
            ("fires",          IntentVerb.FireMission),
            ("launch",         IntentVerb.LaunchDrones),
            ("rtb",            IntentVerb.RTB),
            ("return to base", IntentVerb.RTB),
            ("hold",           IntentVerb.Hold),
            ("loiter",         IntentVerb.Loiter),
            ("attack",         IntentVerb.Attack),
            ("engage",         IntentVerb.Attack),
            ("move to",        IntentVerb.Move),
            ("move",           IntentVerb.Move),
            ("advance to",     IntentVerb.Move),
            ("advance",        IntentVerb.Move),
            ("push to",        IntentVerb.Move),
            ("push",           IntentVerb.Move),
        };

        // MGRS-ish: "34S DH 12345 67890" — we won't do full WGS-84 conversion
        // here; we map the easting/northing pair to a position relative to
        // world origin in a 1m-per-digit fashion which is plenty for the demo.
        // Real MGRS→WGS84→Unity needs ProjNet or equivalent.
        private static readonly Regex Mgrs = new Regex(
            @"(?:mgrs\s+)?\d{1,2}[a-z]\s+[a-z]{2}\s+(\d{4,5})\s+(\d{4,5})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "group 2", "team 2", "ctrl-2", "ctrl 2"
        private static readonly Regex ControlGroup = new Regex(
            @"\b(?:group|team|ctrl|ctl|control(?:\s+group)?)[\s-]*([0-9])\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Callsigns we know about: ALPHA, BRAVO, CHARLIE, DELTA, ECHO, FOXTROT,
        // GOLF, HOTEL, INDIA, JULIET, KILO, LIMA, MIKE, NOVEMBER, OSCAR, PAPA,
        // QUEBEC, ROMEO, SIERRA, TANGO, UNIFORM, VICTOR, WHISKEY, XRAY, YANKEE, ZULU
        // followed by -N.
        private static readonly Regex CallsignToken = new Regex(
            @"\b(ALPHA|BRAVO|CHARLIE|DELTA|ECHO|FOXTROT|GOLF|HOTEL|INDIA|JULIET|KILO|LIMA|MIKE|NOVEMBER|OSCAR|PAPA|QUEBEC|ROMEO|SIERRA|TANGO|UNIFORM|VICTOR|WHISKEY|XRAY|YANKEE|ZULU)[\s-]*\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ParsedIntent Parse(string raw, IntentParserContext ctx)
        {
            var result = new ParsedIntent { who = new List<Agent>() };
            if (string.IsNullOrWhiteSpace(raw)) { result.rationale = "empty"; return result; }
            string text = " " + raw.Trim() + " "; // pad for word-boundary safety
            string lower = text.ToLowerInvariant();

            // ---- VERB ----
            foreach (var (token, verb) in Verbs)
            {
                if (lower.Contains(" " + token + " ") || lower.Contains(" " + token))
                {
                    result.verb = verb;
                    break;
                }
            }
            if (result.verb == IntentVerb.Unknown) { result.rationale = "no verb"; return result; }

            // ---- WHO ----
            // Universal first.
            if (lower.Contains("all units") || lower.Contains("everyone") ||
                lower.Contains("all friendly") || lower.Contains("all callsigns"))
            {
                if (ctx.manager != null)
                {
                    foreach (var a in ctx.manager.EnumerateByAffiliation(Affiliation.Friendly))
                        result.who.Add(a);
                }
            }
            else
            {
                // Control group: "group 2"
                var gm = ControlGroup.Match(text);
                if (gm.Success && ctx.controlGroups != null)
                {
                    if (int.TryParse(gm.Groups[1].Value, out int g) &&
                        ctx.controlGroups.TryGetValue(g, out var list))
                    {
                        foreach (var a in list) if (a != null) result.who.Add(a);
                    }
                }

                // Callsigns: TANGO-6 etc. May appear multiple times.
                if (result.who.Count == 0 && ctx.manager != null)
                {
                    var matches = CallsignToken.Matches(text);
                    if (matches.Count > 0)
                    {
                        var wanted = new HashSet<string>();
                        foreach (Match m in matches)
                        {
                            // normalize "TANGO 6" / "TANGO-6" / "tango6" → "TANGO-6"
                            string token = Regex.Replace(m.Value.ToUpperInvariant(), @"[\s-]+", "-");
                            // collapse multiple dashes
                            token = Regex.Replace(token, "-+", "-");
                            wanted.Add(token);
                        }
                        foreach (var a in ctx.manager.Agents)
                        {
                            if (a == null || string.IsNullOrEmpty(a.callsign)) continue;
                            if (wanted.Contains(a.callsign.ToUpperInvariant()))
                                result.who.Add(a);
                        }
                    }
                }
            }
            // If no "who" resolved, default to current selection — operator was
            // probably commanding what they already had clicked.

            // ---- WHERE ----
            // 1) MGRS
            var mg = Mgrs.Match(text);
            if (mg.Success)
            {
                // 5-digit easting/northing → meters. We don't have a true MGRS
                // origin here; treat the centered 5-digit window as offsets
                // from world (0,0,0) scaled into the [-100,+100] AO range.
                if (int.TryParse(mg.Groups[1].Value, out int east) &&
                    int.TryParse(mg.Groups[2].Value, out int north))
                {
                    // Normalize 5-digit to [-1..+1] then scale to world half-size.
                    float ex = (east  - 50000f) / 50000f;
                    float nz = (north - 50000f) / 50000f;
                    result.where    = new Vector3(ex * 100f, 0f, nz * 100f);
                    result.hasWhere = true;
                    result.rationale = "MGRS→world";
                }
            }

            // 2) Named feature ("PL ALAMO", "TRP 1", "EA SLEDGE", "BP 1", "NFA HILLCREST")
            if (!result.hasWhere && ctx.mission != null)
            {
                // The mission overlay stores names like "PL ALAMO". Try common patterns
                // by extracting the chunk after "to "/"on "/"at " — that's usually the place.
                string place = ExtractPlace(lower);
                if (!string.IsNullOrEmpty(place))
                {
                    var m = ctx.mission.FindByName(place);
                    if (m != null)
                    {
                        result.where    = MissionOverlay.CentroidOf(m);
                        result.hasWhere = true;
                        result.rationale = "FEATURE " + m.name;
                    }
                }
            }

            // 3) Direct callsign target ("attack on TANGO-6") — fallback
            //    when no MGRS and no feature.
            if (!result.hasWhere && ctx.manager != null)
            {
                // Look for "on TANGO-6" / "at TANGO-6"
                var em = Regex.Match(text,
                    @"\b(?:on|at|to)\s+((?:[A-Z]+)[\s-]*\d+)\b",
                    RegexOptions.IgnoreCase);
                if (em.Success)
                {
                    string token = Regex.Replace(em.Groups[1].Value.ToUpperInvariant(),
                        @"[\s-]+", "-");
                    foreach (var a in ctx.manager.Agents)
                    {
                        if (a == null) continue;
                        if (!string.IsNullOrEmpty(a.callsign) &&
                            a.callsign.ToUpperInvariant() == token)
                        {
                            result.where    = a.transform.position;
                            result.hasWhere = true;
                            result.rationale = "ON " + a.callsign;
                            break;
                        }
                    }
                }
            }

            // Move/Attack/Fires/Launch all require a "where". RTB/Hold/Loiter
            // do not. Medevac/Casevac use the unit's own position if no target.
            bool needsWhere = result.verb == IntentVerb.Move
                           || result.verb == IntentVerb.Attack
                           || result.verb == IntentVerb.FireMission
                           || result.verb == IntentVerb.LaunchDrones;

            if (needsWhere && !result.hasWhere)
            {
                result.rationale = "no target resolved";
                return result; // success stays false
            }

            result.success = true;
            return result;
        }

        private static string ExtractPlace(string lower)
        {
            // Find " to|on|at <REST>"
            var m = Regex.Match(lower, @"\b(?:to|on|at)\s+(.+?)(?:[.!?]|$)");
            if (!m.Success) return null;
            string place = m.Groups[1].Value.Trim();
            // Strip trailing junk like "now", "asap"
            place = Regex.Replace(place, @"\b(now|asap|copy|over|out)\b\.?\s*$",
                "", RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(place) ? null : place.ToUpperInvariant();
        }
    }

    // =====================================================================
    //  Façade — RadioChatPanel calls this single method.
    // =====================================================================
    public static class IntentParser
    {
        public static IIntentBackend Backend { get; set; } = new RuleBasedIntentBackend();

        /// <summary>
        /// Parse and (on success) execute the intent. Returns the rationale
        /// string for the radio echo. If parse fails, returns null and the
        /// chat panel publishes the raw text as a normal message.
        /// </summary>
        public static string TryExecute(string operatorText,
                                        NATO_C2_Manager manager,
                                        MissionOverlay mission,
                                        Dictionary<int, List<Agent>> controlGroups,
                                        string operatorCallsign,
                                        string net)
        {
            var ctx = new IntentParserContext
            {
                manager       = manager,
                mission       = mission,
                controlGroups = controlGroups
            };

            var intent = Backend.Parse(operatorText, ctx);
            if (!intent.success) return null;

            // Apply selection: if intent named units, use those. Otherwise keep
            // the current selection (operator was speaking to what they had clicked).
            if (intent.who != null && intent.who.Count > 0)
                manager.SetSelection(intent.who);

            // Translate verb → CommandOrder where applicable.
            switch (intent.verb)
            {
                case IntentVerb.Move:
                case IntentVerb.Attack:
                case IntentVerb.Hold:
                case IntentVerb.Loiter:
                case IntentVerb.RTB:
                {
                    CommandOrder order =
                        intent.verb == IntentVerb.Move   ? CommandOrder.Move :
                        intent.verb == IntentVerb.Attack ? CommandOrder.Attack :
                        intent.verb == IntentVerb.Hold   ? CommandOrder.Hold :
                        intent.verb == IntentVerb.Loiter ? CommandOrder.Loiter :
                                                          CommandOrder.RTB;
                    Vector3 where = intent.hasWhere ? intent.where : Vector3.zero;
                    manager.IssueCommand(order, where);
                    break;
                }
                case IntentVerb.LaunchDrones:
                {
                    // Mirror B-key behaviour: launch selected drones at the target.
                    // DroneAutopilot.LaunchSelectedDrones is static.
                    NATO.C2.DroneAutopilot.LaunchSelectedDrones(intent.where);
                    break;
                }
                case IntentVerb.FireMission:
                {
                    // 1. Local effect: aim selected units at the target.
                    manager.IssueCommand(CommandOrder.Attack, intent.where);
                    // 2. Publish a typed CoT Call-For-Fire — if the TAK adapter
                    //    is connected, this hits ATAK / TAK Server / federated peers
                    //    with proper lat/lon so they see the pin on their map.
                    var tak = Object.FindAnyObjectByType<NATO.C2.Net.TakServerCotAdapter>();
                    tak?.PublishCallForFire(intent.where, operatorCallsign,
                                            remarks: $"Requested via {net} net");
                    // 3. Radio echo so the operator sees the request go out.
                    FeedHub.Instance?.PublishRadio(new RadioMessage
                    {
                        net = net,
                        timestampUtc = System.DateTime.UtcNow,
                        fromCallsign = operatorCallsign,
                        text = $"CALL FOR FIRE — grid ({intent.rationale}) — CoT b-r-f-h-c emitted",
                        severity = RadioSeverity.Warning
                    });
                    break;
                }
                case IntentVerb.Medevac:
                case IntentVerb.Casevac:
                {
                    // CASEVAC = combat casualty evacuation (less protected),
                    // MEDEVAC = dedicated medical evac with red-cross protection.
                    // Both encode as CoT b-r-c-m with different precedence labels.
                    char precedence = intent.verb == IntentVerb.Medevac ? 'A' : 'B';
                    string patientCs = "";
                    // If the intent targeted a callsign, use that as the patient.
                    if (intent.who != null && intent.who.Count > 0 && intent.who[0] != null)
                        patientCs = intent.who[0].callsign;

                    var tak = Object.FindAnyObjectByType<NATO.C2.Net.TakServerCotAdapter>();
                    tak?.PublishMedevac(intent.where, operatorCallsign,
                                        patientCallsign: patientCs,
                                        precedence: precedence,
                                        patientsLitter: 1,
                                        patientsAmbulatory: 0,
                                        remarks: $"Requested via {net} net");

                    FeedHub.Instance?.PublishRadio(new RadioMessage
                    {
                        net = "MEDEVAC",
                        timestampUtc = System.DateTime.UtcNow,
                        fromCallsign = operatorCallsign,
                        text = $"{intent.verb.ToString().ToUpperInvariant()} — patient {patientCs} — precedence {precedence} — CoT b-r-c-m emitted",
                        severity = RadioSeverity.Warning
                    });
                    break;
                }
            }

            return $"{intent.verb} · {intent.rationale}";
        }
    }
}
