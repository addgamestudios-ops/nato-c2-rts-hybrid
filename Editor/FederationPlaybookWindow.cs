// =====================================================================
//  NATO C2 RTS Hybrid — FederationPlaybookWindow.cs
//  ---------------------------------------------------------------------
//  Renders docs/federation-playbook.md inside an EditorWindow split:
//
//      [ section list ]  |  [ selected section body ]
//
//  Sections are detected by markdown ## headers. The operator can
//  jump directly to "Mode-flap storm" or "Peer dot stays red" when
//  the live dashboard surfaces a symptom, instead of context-switching
//  to a text editor.
//
//  Trigger: menu  NATO C2 → Link 16 → Federation Playbook
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NATO.C2.EditorTools
{
    public class FederationPlaybookWindow : EditorWindow
    {
        private struct Section
        {
            public string heading;   // text of the ## line (without "## ")
            public string body;      // everything from after the heading to the next ## or EOF
        }

        private List<Section> _sections;
        private int _selected;
        private Vector2 _listScroll;
        private Vector2 _bodyScroll;
        private string  _loadError;
        private GUIStyle _bodyStyle;

        [MenuItem("NATO C2/Link 16/Federation Playbook", priority = 64)]
        public static void Open()
        {
            var w = GetWindow<FederationPlaybookWindow>("L16 Playbook");
            w.minSize = new Vector2(720, 460);
            w.LoadMarkdown();
        }

        /// <summary>
        /// Opens the panel pre-scrolled to the section whose heading
        /// best matches a free-text symptom hint. Called from runtime
        /// glue when the live dashboard wants to surface the playbook.
        /// </summary>
        public static void OpenForSymptom(string symptomHint)
        {
            var w = GetWindow<FederationPlaybookWindow>("L16 Playbook");
            w.minSize = new Vector2(720, 460);
            w.LoadMarkdown();
            if (!string.IsNullOrEmpty(symptomHint) && w._sections != null)
            {
                for (int i = 0; i < w._sections.Count; i++)
                {
                    if (w._sections[i].heading.IndexOf(symptomHint, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        w._selected = i;
                        break;
                    }
                }
            }
        }

        private void OnEnable() => LoadMarkdown();

        private void LoadMarkdown()
        {
            _loadError = null;
            _sections  = new List<Section>();
            string path = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "docs", "federation-playbook.md");
            if (!File.Exists(path))
            {
                _loadError = "docs/federation-playbook.md not found at " + path;
                return;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { _loadError = "read failed: " + e.Message; return; }

            // Split on top-level ## headers (not ### or higher).
            var lines = text.Split('\n');
            string heading = "Preamble";
            var body = new StringBuilder();
            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("## ") && !line.StartsWith("### "))
                {
                    if (body.Length > 0 || _sections.Count == 0)
                        _sections.Add(new Section { heading = heading, body = body.ToString() });
                    heading = line.Substring(3).Trim();
                    body.Clear();
                }
                else
                {
                    body.AppendLine(line);
                }
            }
            if (body.Length > 0)
                _sections.Add(new Section { heading = heading, body = body.ToString() });

            // Skip the preamble entry if it's empty / just file-level intro.
            if (_sections.Count > 0 && string.IsNullOrWhiteSpace(_sections[0].body))
                _sections.RemoveAt(0);
        }

        private void OnGUI()
        {
            if (_bodyStyle == null)
            {
                _bodyStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = true,
                };
            }

            EditorGUILayout.LabelField("Federation Playbook", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload", GUILayout.Width(80))) LoadMarkdown();
                EditorGUILayout.LabelField("docs/federation-playbook.md", EditorStyles.miniLabel);
            }
            if (_loadError != null)
            {
                EditorGUILayout.HelpBox(_loadError, MessageType.Warning);
                return;
            }
            if (_sections == null || _sections.Count == 0)
            {
                EditorGUILayout.HelpBox("No sections found.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                // Left: section list.
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(220)))
                {
                    EditorGUILayout.LabelField("Sections", EditorStyles.boldLabel);
                    _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
                    for (int i = 0; i < _sections.Count; i++)
                    {
                        bool isSel = (i == _selected);
                        var style = isSel ? EditorStyles.boldLabel : EditorStyles.label;
                        if (GUILayout.Button(_sections[i].heading, style)) _selected = i;
                    }
                    EditorGUILayout.EndScrollView();
                }

                // Right: section body.
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(_sections[_selected].heading, EditorStyles.boldLabel);
                    _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);
                    EditorGUILayout.LabelField(FormatBody(_sections[_selected].body), _bodyStyle);
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        // Light markdown → Unity rich-text conversion. Handles **bold**,
        // *italic*, `code` spans, and bullet list markers. Headings inside
        // sections (### lines) become bold lines.
        private static string FormatBody(string md)
        {
            var sb = new StringBuilder(md.Length);
            foreach (var rawLine in md.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                string converted = ConvertInline(line);
                if (converted.StartsWith("### ")) sb.AppendLine("<b>" + converted.Substring(4) + "</b>");
                else                              sb.AppendLine(converted);
            }
            return sb.ToString();
        }

        private static string ConvertInline(string line)
        {
            // Run inline conversions left-to-right.
            line = ReplacePairs(line, "**", "<b>", "</b>");
            line = ReplacePairs(line, "`",  "<color=#80c4ff>", "</color>");
            return line;
        }

        private static string ReplacePairs(string s, string marker, string openTag, string closeTag)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            bool open = false;
            while (i < s.Length)
            {
                if (i + marker.Length <= s.Length && s.Substring(i, marker.Length) == marker)
                {
                    sb.Append(open ? closeTag : openTag);
                    open = !open;
                    i += marker.Length;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            // If we ended with an unmatched open marker, close it so the rich-text parser doesn't choke.
            if (open) sb.Append(closeTag);
            return sb.ToString();
        }
    }
}
#endif
