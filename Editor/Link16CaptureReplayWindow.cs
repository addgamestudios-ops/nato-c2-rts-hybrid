// =====================================================================
//  NATO C2 RTS Hybrid — Link16CaptureReplayWindow.cs
//  ---------------------------------------------------------------------
//  EditorWindow that loads a .dpdu capture produced by
//  Stanag5066Capture and lets the operator scrub through it frame by
//  frame. Useful for post-hoc debug of a federation problem without
//  rerunning the scenario.
//
//  Panels:
//      • File picker — lists .dpdu files under persistentDataPath/Captures
//      • Frame table — direction (TX/RX), type, ESN, sender, ack/srej target
//      • Scrubber    — slider over frame index; live updates derived state
//      • Per-peer state — replays frames 0..currentIndex inclusive,
//        builds a synthetic lastSeenEsn / rxCount / srejReceived /
//        gapsDetected per peer (mirrors what Stanag5066FederationBridge
//        does at runtime), and shows the snapshot.
//
//  Trigger: menu  NATO C2 → Link 16 → Capture Replay
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using NATO.C2.Net;

namespace NATO.C2.EditorTools
{
    public class Link16CaptureReplayWindow : EditorWindow
    {
        private string[] _files = Array.Empty<string>();
        private int      _selectedFile = -1;
        private List<Stanag5066Capture.Record> _records;
        private List<Stanag5066DPdu>           _parsed;
        private int     _scrub;
        private Vector2 _frameScroll;
        private Vector2 _peerScroll;

        [MenuItem("NATO C2/Link 16/Capture Replay", priority = 61)]
        public static void Open()
        {
            var w = GetWindow<Link16CaptureReplayWindow>("L16 Capture Replay");
            w.minSize = new Vector2(720, 480);
            w.RefreshFiles();
        }

        private void OnEnable() => RefreshFiles();

        private void RefreshFiles()
        {
            string dir = Path.Combine(Application.persistentDataPath, "Captures");
            if (!Directory.Exists(dir)) { _files = Array.Empty<string>(); return; }
            var files = Directory.GetFiles(dir, "L16-*.dpdu");
            Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal));
            _files = files;
        }

        // ====================================================================
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Link 16 Capture Replay", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Captures dir: {Path.Combine(Application.persistentDataPath, "Captures")}", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) RefreshFiles();
                if (_files.Length == 0)
                {
                    EditorGUILayout.LabelField("(no L16-*.dpdu files found)");
                }
                else
                {
                    var labels = new string[_files.Length];
                    for (int i = 0; i < _files.Length; i++) labels[i] = Path.GetFileName(_files[i]);
                    int newSel = EditorGUILayout.Popup(_selectedFile < 0 ? 0 : _selectedFile, labels);
                    if (newSel != _selectedFile) { _selectedFile = newSel; LoadFile(_files[_selectedFile]); }
                }
            }

            if (_records == null) { EditorGUILayout.HelpBox("Pick a capture file above.", MessageType.Info); return; }

            EditorGUILayout.Space(4);
            DrawScrubber();
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                // Left: frame list, right: per-peer state.
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(420)))
                {
                    EditorGUILayout.LabelField($"Frames ({_records.Count})", EditorStyles.boldLabel);
                    DrawFrameList();
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField($"Per-Peer State @ frame {_scrub}", EditorStyles.boldLabel);
                    DrawPeerState();
                }
            }
        }

        // ====================================================================
        //  File loading
        // ====================================================================
        private void LoadFile(string path)
        {
            try
            {
                _records = Stanag5066Capture.Read(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CaptureReplay] {e.Message}");
                _records = null;
                return;
            }
            _parsed = new List<Stanag5066DPdu>(_records.Count);
            for (int i = 0; i < _records.Count; i++)
            {
                Stanag5066DPdu.TryParseBytes(_records[i].frame, out var p);
                _parsed.Add(p);
            }
            _scrub = Mathf.Max(0, _records.Count - 1);
        }

        // ====================================================================
        //  Panels
        // ====================================================================
        private void DrawScrubber()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Index", GUILayout.Width(40));
                _scrub = EditorGUILayout.IntSlider(_scrub, 0, Mathf.Max(0, _records.Count - 1));
                if (GUILayout.Button("⏪", GUILayout.Width(36))) _scrub = Mathf.Max(0, _scrub - 1);
                if (GUILayout.Button("⏩", GUILayout.Width(36))) _scrub = Mathf.Min(_records.Count - 1, _scrub + 1);
                if (GUILayout.Button("⏮ START", GUILayout.Width(80))) _scrub = 0;
                if (GUILayout.Button("⏭ END",   GUILayout.Width(80))) _scrub = _records.Count - 1;
            }
        }

        private void DrawFrameList()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("Dir", GUILayout.Width(34));
                EditorGUILayout.LabelField("#",   GUILayout.Width(48));
                EditorGUILayout.LabelField("Type", GUILayout.Width(76));
                EditorGUILayout.LabelField("ESN", GUILayout.Width(60));
                EditorGUILayout.LabelField("Ack/SREJ", GUILayout.Width(80));
                EditorGUILayout.LabelField("Op", GUILayout.Width(60));
            }
            _frameScroll = EditorGUILayout.BeginScrollView(_frameScroll);
            for (int i = 0; i < _records.Count; i++)
            {
                var pdu = _parsed[i];
                bool current = (i == _scrub);
                var style = current ? EditorStyles.boldLabel : EditorStyles.label;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_records[i].direction.ToString(), style, GUILayout.Width(34));
                    EditorGUILayout.LabelField(i.ToString(),                     style, GUILayout.Width(48));
                    EditorGUILayout.LabelField(ShortType(pdu.type),              style, GUILayout.Width(76));
                    EditorGUILayout.LabelField("0x" + pdu.esn.ToString("X4"),    style, GUILayout.Width(60));
                    EditorGUILayout.LabelField(AckLabel(pdu),                    style, GUILayout.Width(80));
                    EditorGUILayout.LabelField(pdu.senderOp ?? "?",              style, GUILayout.Width(60));
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawPeerState()
        {
            // Replay frames 0..scrub and aggregate per-peer state.
            var peers = new Dictionary<string, PeerSnap>(8);
            for (int i = 0; i <= _scrub && i < _records.Count; i++)
            {
                if (_records[i].direction != Stanag5066Capture.Direction.Rx) continue;
                var p = _parsed[i];
                string op = string.IsNullOrEmpty(p.senderOp) ? "?" : p.senderOp;
                peers.TryGetValue(op, out var snap);
                snap.rxCount++;
                if (p.type == Stanag5066DPdu.DPduType.DataOnly ||
                    p.type == Stanag5066DPdu.DPduType.DataWithAck && p.esn != 0)
                {
                    if (snap.haveBaseline)
                    {
                        ushort expected = (ushort)((snap.lastSeenEsn + 1) & 0xFFFF);
                        ushort dist = (ushort)((p.esn - expected) & 0xFFFF);
                        if (p.esn != expected && dist > 0 && dist <= 16) snap.gapsDetected += dist;
                    }
                    snap.lastSeenEsn = p.esn;
                    snap.haveBaseline = true;
                }
                if (p.type == Stanag5066DPdu.DPduType.SelectiveReject) snap.srejSeen++;
                peers[op] = snap;
            }

            _peerScroll = EditorGUILayout.BeginScrollView(_peerScroll);
            if (peers.Count == 0)
            {
                EditorGUILayout.LabelField("(no RX frames up to this index)");
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    EditorGUILayout.LabelField("Peer",       GUILayout.Width(80));
                    EditorGUILayout.LabelField("RX",         GUILayout.Width(50));
                    EditorGUILayout.LabelField("LastESN",    GUILayout.Width(80));
                    EditorGUILayout.LabelField("Gaps",       GUILayout.Width(50));
                    EditorGUILayout.LabelField("SREJ-seen",  GUILayout.Width(80));
                }
                foreach (var kv in peers)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(kv.Key,                                       GUILayout.Width(80));
                        EditorGUILayout.LabelField(kv.Value.rxCount.ToString(),                   GUILayout.Width(50));
                        EditorGUILayout.LabelField("0x" + kv.Value.lastSeenEsn.ToString("X4"),    GUILayout.Width(80));
                        EditorGUILayout.LabelField(kv.Value.gapsDetected.ToString(),              GUILayout.Width(50));
                        EditorGUILayout.LabelField(kv.Value.srejSeen.ToString(),                  GUILayout.Width(80));
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        // ====================================================================
        private struct PeerSnap
        {
            public int    rxCount;
            public ushort lastSeenEsn;
            public bool   haveBaseline;
            public int    gapsDetected;
            public int    srejSeen;
        }

        private static string ShortType(Stanag5066DPdu.DPduType t)
        {
            switch (t)
            {
                case Stanag5066DPdu.DPduType.DataOnly:        return "DATA";
                case Stanag5066DPdu.DPduType.DataWithAck:     return "DATA+ACK";
                case Stanag5066DPdu.DPduType.SelectiveReject: return "SREJ";
                case Stanag5066DPdu.DPduType.NonArqData:      return "NON-ARQ";
                default: return t.ToString();
            }
        }

        private static string AckLabel(Stanag5066DPdu p)
        {
            if (p.type == Stanag5066DPdu.DPduType.DataWithAck) return "0x" + p.ackEsn.ToString("X4");
            if (p.type == Stanag5066DPdu.DPduType.SelectiveReject)
            {
                if (p.srejRangeEnd != 0 && p.srejRangeEnd != p.ackEsn)
                    return $"{p.ackEsn:X4}-{p.srejRangeEnd:X4}";
                return "0x" + p.ackEsn.ToString("X4");
            }
            return "—";
        }
    }
}
#endif
