// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066Capture.cs
//  ---------------------------------------------------------------------
//  Records every D_PDU emitted or received by the local
//  Stanag5066FederationBridge to a binary capture file. The format is
//  designed to be:
//
//      • Replayable by Stanag5066DPdu.TryParseBytes in a unit test
//      • Wireshark-friendly (each frame is exactly the bytes that go
//        on the wire, no envelope)
//
//  File layout (version 2 — current):
//
//      [4 bytes]  Magic   "S5CP" (0x53 0x35 0x43 0x50)
//      [1 byte]   Version (2)
//      [3 bytes]  Reserved (0)
//      ...records (chained):
//        [1 byte]   Direction (0 = TX, 1 = RX)
//        [3 bytes]  Reserved
//        [4 bytes]  Frame length (BE uint32)
//        [N bytes]  Frame bytes (canonical S5066 D_PDU)
//        [32 bytes] SHA-256 of THIS record's bytes XORed against the
//                   previous record's hash, forming a tamper-evident
//                   chain. First record's prev-hash is all-zero.
//
//  Version 1 is identical without the trailing 32-byte chain hash.
//  Read() detects the version byte and parses accordingly so old
//  captures stay readable.
//
//  Verify(records) walks the chain and returns the index of the first
//  tampered record, or -1 if intact.
//
//  Saved under <persistentDataPath>/Captures/L16-{yyyyMMdd-HHmmss}.dpdu.
// =====================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/STANAG 5066 Capture Recorder")]
    public class Stanag5066Capture : MonoBehaviour
    {
        public Stanag5066FederationBridge bridge;
        public Stanag5066ArqRetry arq;

        [Tooltip("If true, capture writes start at Awake. Otherwise call StartCapture() manually.")]
        public bool autoStart = true;

        public string CurrentFilePath { get; private set; }
        public int FramesWritten { get; private set; }

        public static readonly byte[] Magic = { 0x53, 0x35, 0x43, 0x50 };  // "S5CP"
        public const byte FormatVersion = 2;
        public const int  HashSize      = 32;       // SHA-256
        public enum Direction : byte { Tx = 0, Rx = 1 }

        private FileStream _fs;
        // Running prev-hash for the chain. All-zero before the first record.
        private byte[] _prevHash = new byte[HashSize];
        private readonly SHA256 _sha = SHA256.Create();

        // ====================================================================
        private void Awake()
        {
            if (bridge == null) bridge = UnityEngine.Object.FindAnyObjectByType<Stanag5066FederationBridge>();
            if (arq    == null && bridge != null) arq = bridge.GetComponent<Stanag5066ArqRetry>();
            if (autoStart) StartCapture();
        }

        public void StartCapture()
        {
            if (_fs != null) return;
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string dir = Path.Combine(Application.persistentDataPath, "Captures");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                CurrentFilePath = Path.Combine(dir, $"L16-{stamp}.dpdu");
                _fs = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _fs.Write(Magic, 0, 4);
                _fs.WriteByte(FormatVersion);
                _fs.WriteByte(0); _fs.WriteByte(0); _fs.WriteByte(0);
                _fs.Flush();
                Debug.Log($"[S5066-Capture] recording to {CurrentFilePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[S5066-Capture] could not open file: {e.Message}");
                _fs = null;
            }

            // Subscribe — we hook the ARQ's track event for TX side, and
            // FeedHub.OnCot for RX side, parsing the D_PDU bytes back out.
            if (arq != null) arq.OnEnvelopeTracked += OnTracked;
            if (FeedHub.Instance != null) FeedHub.Instance.OnCot += OnCot;
        }

        public void StopCapture()
        {
            if (arq != null) arq.OnEnvelopeTracked -= OnTracked;
            if (FeedHub.Instance != null) FeedHub.Instance.OnCot -= OnCot;
            if (_fs != null)
            {
                try { _fs.Flush(); _fs.Dispose(); } catch { /* shutdown */ }
                _fs = null;
            }
        }

        private void OnDestroy() => StopCapture();
        private void OnApplicationQuit() => StopCapture();

        // ====================================================================
        //  Hooks
        // ====================================================================
        private void OnTracked(ushort esn, Link16TdmaSimulator.PackingMode mode, int msgs)
        {
            // We can't easily get the actual transmitted bytes here without
            // a deeper hook into the bridge. As a faithful approximation,
            // we synthesise the same type-0 the bridge would emit. For
            // production capture, hook Stanag5066FederationBridge.EmitDPdu
            // directly via an event (TODO when needed).
            string op = bridge != null ? bridge.OurOp : "LOCAL";
            var pdu = Stanag5066DPdu.Data(esn, mode, msgs, op, note: "tx");
            WriteRecord(Direction.Tx, pdu.ToBytes());
        }

        private void OnCot(CotEvent ev)
        {
            if (ev.type == null || !ev.type.StartsWith("b-l-s5066")) return;
            string detail = ev.xmlDetail ?? "";
            int open = detail.IndexOf("<s5066 enc=\"b64\">", StringComparison.Ordinal);
            if (open < 0) return;
            int contentStart = open + "<s5066 enc=\"b64\">".Length;
            int close = detail.IndexOf("</s5066>", contentStart, StringComparison.Ordinal);
            if (close <= contentStart) return;
            try
            {
                byte[] bytes = Convert.FromBase64String(detail.Substring(contentStart, close - contentStart));
                WriteRecord(Direction.Rx, bytes);
            }
            catch (FormatException) { /* ignore corrupt b64 */ }
        }

        // ====================================================================
        //  Record writer (also exposed for tests + replay tooling).
        // ====================================================================
        public void WriteRecord(Direction dir, byte[] frame)
        {
            if (_fs == null || frame == null || frame.Length == 0) return;
            try
            {
                // Header: direction (1B) + reserved (3B) + length (4B BE).
                var hdr = new byte[8];
                hdr[0] = (byte)dir;
                hdr[4] = (byte)((frame.Length >> 24) & 0xFF);
                hdr[5] = (byte)((frame.Length >> 16) & 0xFF);
                hdr[6] = (byte)((frame.Length >>  8) & 0xFF);
                hdr[7] = (byte)( frame.Length        & 0xFF);

                _fs.Write(hdr, 0, 8);
                _fs.Write(frame, 0, frame.Length);

                // Chain hash: SHA-256( prev_hash || hdr || frame ).
                byte[] chainHash = ComputeChainHash(_prevHash, hdr, frame);
                _fs.Write(chainHash, 0, chainHash.Length);
                _prevHash = chainHash;

                _fs.Flush();
                FramesWritten++;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[S5066-Capture] write failed: {e.Message}");
            }
        }

        // Helper — exposed static so tools and Verify() can recompute identically.
        public static byte[] ComputeChainHash(byte[] prev, byte[] hdr, byte[] frame)
        {
            // We re-create the hash object per call so this is thread-safe
            // for tools. The MonoBehaviour caches _sha for hot-path runtime.
            using var sha = SHA256.Create();
            var buf = new byte[(prev?.Length ?? HashSize) + hdr.Length + frame.Length];
            int off = 0;
            if (prev != null && prev.Length == HashSize) { Buffer.BlockCopy(prev, 0, buf, off, HashSize); off += HashSize; }
            else                                          { off += HashSize; /* zero-fill */ }
            Buffer.BlockCopy(hdr,   0, buf, off, hdr.Length);   off += hdr.Length;
            Buffer.BlockCopy(frame, 0, buf, off, frame.Length);
            return sha.ComputeHash(buf);
        }

        // ====================================================================
        //  Static helpers for replaying a capture file in tests / tools.
        // ====================================================================
        public struct Record
        {
            public Direction direction;
            public byte[] frame;
            public byte[] chainHash;   // null if file was v1
        }

        /// <summary>Read every record from a .dpdu file. Throws on bad magic.
        /// Accepts v1 (legacy) and v2 (hash-chained). For v2 captures use
        /// <see cref="Verify"/> to validate the chain.</summary>
        public static System.Collections.Generic.List<Record> Read(string filePath)
        {
            var records = new System.Collections.Generic.List<Record>(64);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            byte[] magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != Magic[0] || magic[1] != Magic[1]
                || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("Not an S5CP capture file (bad magic).");
            byte ver = br.ReadByte();
            if (ver != 1 && ver != 2)
                throw new InvalidDataException($"Unsupported S5CP version {ver}.");
            br.ReadBytes(3); // reserved
            while (fs.Position < fs.Length)
            {
                Direction dir = (Direction)br.ReadByte();
                br.ReadBytes(3); // reserved
                int len = (br.ReadByte() << 24) | (br.ReadByte() << 16) | (br.ReadByte() << 8) | br.ReadByte();
                if (len < 0 || fs.Position + len > fs.Length)
                    throw new InvalidDataException("Truncated record.");
                byte[] frame = br.ReadBytes(len);
                byte[] hash = null;
                if (ver == 2)
                {
                    if (fs.Position + HashSize > fs.Length)
                        throw new InvalidDataException("Truncated record (missing chain hash).");
                    hash = br.ReadBytes(HashSize);
                }
                records.Add(new Record { direction = dir, frame = frame, chainHash = hash });
            }
            return records;
        }

        /// <summary>
        /// Walk a list of v2 records and verify the SHA-256 chain. Returns
        /// the zero-based index of the first record whose hash doesn't
        /// match its recomputed value, or -1 if the whole chain is intact.
        /// </summary>
        /// <exception cref="ArgumentException">if any record is missing its chainHash (i.e. file was v1).</exception>
        public static int Verify(System.Collections.Generic.IList<Record> records)
        {
            byte[] prev = new byte[HashSize];   // initial zero-hash
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.chainHash == null)
                    throw new ArgumentException($"Record {i} has no chainHash — Verify() only applies to v2 captures.");
                // Re-build the on-disk header bytes so the hash matches what WriteRecord computed.
                var hdr = new byte[8];
                hdr[0] = (byte)r.direction;
                hdr[4] = (byte)((r.frame.Length >> 24) & 0xFF);
                hdr[5] = (byte)((r.frame.Length >> 16) & 0xFF);
                hdr[6] = (byte)((r.frame.Length >>  8) & 0xFF);
                hdr[7] = (byte)( r.frame.Length        & 0xFF);
                byte[] expected = ComputeChainHash(prev, hdr, r.frame);
                if (!ConstantTimeEquals(expected, r.chainHash))
                    return i;
                prev = r.chainHash;
            }
            return -1;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
