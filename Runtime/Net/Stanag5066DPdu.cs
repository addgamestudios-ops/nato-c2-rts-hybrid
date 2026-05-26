// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066DPdu.cs
//  ---------------------------------------------------------------------
//  STANAG 5066 (HF Subnetwork Profile) Data Transfer Sublayer (DTS)
//  D_PDU encode / decode for the three types we actually need to
//  federate ARQ state across the TAK Server:
//
//      TYPE 0  — DATA-ONLY              (sender → receiver, ARQ-tracked)
//      TYPE 2  — DATA-WITH-ACK          (sender includes ACK for prior)
//      TYPE 4  — NON-ARQ DATA           (sender → receiver, best-effort)
//
//  Real STANAG 5066 puts a 16-byte D_PDU header on a binary frame.
//  We serialize the *logical* fields as compact XML so:
//      • The whole thing fits inside a CoT <detail><s5066/></detail>
//        block and rides the existing TAK Server transport.
//      • A human can read it in Wireshark / pcap.
//      • The receiver can parse it deterministically.
//
//  When production swaps in a real S5066-PDU stack (over HF radio or
//  TCP per RFC 5066), the FIELD SET stays the same — only the wire
//  encoding changes from XML to the binary D_PDU header.
// =====================================================================

using System;
using System.Globalization;
using System.Text;

namespace NATO.C2.Net
{
    /// <summary>
    /// Compact in-memory representation of a STANAG 5066 D_PDU.
    /// Only the fields we federate are modelled — the full S5066 header
    /// has many more (DRC, EOW, hop, etc.) which are not yet wired.
    /// </summary>
    public struct Stanag5066DPdu
    {
        public DPduType type;        // 0 / 2 / 3 / 4
        public ushort   esn;         // envelope sequence number (the "C_PDU ID")
        public ushort   ackEsn;      // type-2 = ACK ESN; type-3 = first/lowest rejected ESN
        public ushort   srejRangeEnd; // type-3 only: highest rejected ESN, inclusive.
                                     // 0 or == ackEsn means single-ESN SREJ. Otherwise
                                     // [ackEsn..srejRangeEnd] are all rejected (coalesced).
        public byte     mode;        // 0=STD-DP, 1=P2DP, 2=P4SP — matches PackingMode
        public int      messageCount;
        public string   senderOp;    // OperatorIdentity station prefix ("W1", "F3")
        public string   note;        // optional payload-ref or human note

        public enum DPduType : byte
        {
            DataOnly    = 0,
            DataWithAck = 2,
            // S5066 Annex C Selective REJect — receiver tells sender
            // "I noticed a gap at ESN X; please retransmit it". We carry
            // the rejected ESN in the type-specific tail (bytes 14-15).
            SelectiveReject = 3,
            NonArqData  = 4,
        }

        // =====================================================================
        //  XML wire format
        //  <s5066 type="0|2|4" esn="XXXX" ack="XXXX" mode="0|1|2" cnt="N" op="W1" note="..."/>
        // =====================================================================

        /// <summary>Serialise to the inner-XML fragment that goes inside CoT &lt;detail&gt;.</summary>
        public string ToXmlFragment()
        {
            var sb = new StringBuilder(96);
            sb.Append("<s5066 type=\"").Append((byte)type).Append('"');
            sb.Append(" esn=\"").Append(esn.ToString("X4")).Append('"');
            if (type == DPduType.DataWithAck || type == DPduType.SelectiveReject)
                sb.Append(" ack=\"").Append(ackEsn.ToString("X4")).Append('"');
            sb.Append(" mode=\"").Append(mode).Append('"');
            sb.Append(" cnt=\"").Append(messageCount).Append('"');
            if (!string.IsNullOrEmpty(senderOp))
                sb.Append(" op=\"").Append(XmlEscape(senderOp)).Append('"');
            if (!string.IsNullOrEmpty(note))
                sb.Append(" note=\"").Append(XmlEscape(note)).Append('"');
            sb.Append("/>");
            return sb.ToString();
        }

        /// <summary>
        /// Best-effort parse from an XML fragment. Returns false if the input
        /// doesn't contain an &lt;s5066&gt; tag. Throws on malformed numeric fields.
        /// </summary>
        public static bool TryParse(string xml, out Stanag5066DPdu pdu)
        {
            pdu = default;
            if (string.IsNullOrEmpty(xml)) return false;
            int i = xml.IndexOf("<s5066", StringComparison.Ordinal);
            if (i < 0) return false;
            int end = xml.IndexOf("/>", i, StringComparison.Ordinal);
            if (end < 0) end = xml.IndexOf('>', i);
            if (end < 0) return false;
            string body = xml.Substring(i, end - i + 1);

            pdu.type         = (DPduType)byte.Parse(Attr(body, "type") ?? "0", CultureInfo.InvariantCulture);
            pdu.esn          = ushort.Parse(Attr(body, "esn") ?? "0", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string ack       = Attr(body, "ack");
            pdu.ackEsn       = string.IsNullOrEmpty(ack) ? (ushort)0
                                  : ushort.Parse(ack, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pdu.mode         = byte.Parse(Attr(body, "mode") ?? "0", CultureInfo.InvariantCulture);
            pdu.messageCount = int.Parse(Attr(body, "cnt")  ?? "0", CultureInfo.InvariantCulture);
            pdu.senderOp     = XmlUnescape(Attr(body, "op")) ?? "";
            pdu.note         = XmlUnescape(Attr(body, "note")) ?? "";
            return true;
        }

        // ---------- helpers ----------
        private static string Attr(string xml, string name)
        {
            int idx = xml.IndexOf(" " + name + "=\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += name.Length + 3;
            int closeQuote = xml.IndexOf('"', idx);
            return closeQuote > idx ? xml.Substring(idx, closeQuote - idx) : null;
        }

        private static string XmlEscape(string s)
            => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        private static string XmlUnescape(string s)
            => s?.Replace("&quot;", "\"").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&amp;", "&");

        // =====================================================================
        //  Convenience constructors
        // =====================================================================
        public static Stanag5066DPdu Data(ushort esn, Link16TdmaSimulator.PackingMode mode,
                                          int messageCount, string senderOp, string note = null)
            => new Stanag5066DPdu
            {
                type = DPduType.DataOnly,
                esn = esn,
                mode = (byte)mode,
                messageCount = messageCount,
                senderOp = senderOp,
                note = note,
            };

        public static Stanag5066DPdu DataWithAck(ushort esn, ushort ackEsn,
                                                 Link16TdmaSimulator.PackingMode mode,
                                                 int messageCount, string senderOp)
            => new Stanag5066DPdu
            {
                type = DPduType.DataWithAck,
                esn = esn,
                ackEsn = ackEsn,
                mode = (byte)mode,
                messageCount = messageCount,
                senderOp = senderOp,
            };

        public static Stanag5066DPdu SelectiveReject(ushort rejectedEsn, string senderOp, string note = null)
            => new Stanag5066DPdu
            {
                type = DPduType.SelectiveReject,
                esn = 0,                  // SREJ carries no new C_PDU
                ackEsn = rejectedEsn,     // reuse ackEsn field to carry the NAK'd ESN
                srejRangeEnd = 0,         // single-ESN SREJ
                mode = (byte)Link16TdmaSimulator.PackingMode.StdDp,
                messageCount = 0,
                senderOp = senderOp,
                note = note,
            };

        /// <summary>
        /// Range SREJ — a single PDU rejecting every ESN in
        /// [startEsn .. endEsn] inclusive. Saves channel time when a gap
        /// of more than a few ESNs is detected. Wraps correctly across
        /// the uint16 boundary.
        /// </summary>
        public static Stanag5066DPdu SelectiveRejectRange(ushort startEsn, ushort endEsn,
                                                          string senderOp, string note = null)
            => new Stanag5066DPdu
            {
                type = DPduType.SelectiveReject,
                esn = 0,
                ackEsn = startEsn,
                srejRangeEnd = endEsn,
                mode = (byte)Link16TdmaSimulator.PackingMode.StdDp,
                messageCount = 0,
                senderOp = senderOp,
                note = note,
            };

        public static Stanag5066DPdu NonArq(ushort esn, Link16TdmaSimulator.PackingMode mode,
                                            int messageCount, string senderOp, string note = null)
            => new Stanag5066DPdu
            {
                type = DPduType.NonArqData,
                esn = esn,
                mode = (byte)mode,
                messageCount = messageCount,
                senderOp = senderOp,
                note = note,
            };

        // =====================================================================
        //  Binary wire format — canonical STANAG 5066 Annex C D_PDU layout.
        //
        //  This is what a Wireshark dissector with the S5066 plugin sees.
        //  Real radios put this on the wire byte-for-byte over HF; we
        //  carry it inside base64 in a CoT detail block so the existing
        //  TAK Server federation transport is unchanged.
        //
        //  Layout (16-byte header + variable payload):
        //
        //    Offset  Size  Field
        //    ------  ----  -----
        //       0      2   Sync sequence  0x90 0xEB (S5066 maritime sync)
        //       2      1   D_PDU type (high nibble) + EOW type (low nibble)
        //       3      1   EOW data
        //       4      1   Header size in bytes (always 16 here)
        //       5      2   Payload size (big-endian uint16)
        //       7      1   Hop count
        //       8      2   Source address  (big-endian uint16, hash of senderOp)
        //      10      2   Destination address (0xFFFF = broadcast)
        //      12      2   C_PDU ID = ESN (big-endian uint16)
        //      14      2   Type-specific:
        //                    type=2 → ACK ESN (big-endian uint16)
        //                    type=0/4 → [mode (1B)] [messageCount clamped 0..255 (1B)]
        //
        //  Payload (variable):
        //      [u16 op_len][op_utf8][u16 note_len][note_utf8]
        // =====================================================================

        public const byte SyncByte0 = 0x90;
        public const byte SyncByte1 = 0xEB;
        public const byte HeaderSize = 16;
        public const ushort BroadcastAddr = 0xFFFF;
        public const int CrcSize = 4;     // IEEE 802.3 CRC-32 trailer

        // ---- CRC-32 (IEEE 802.3, poly 0xEDB88320, reflected) -------
        //  Real STANAG 5066 D_PDUs include a CRC-32 over the header and
        //  payload so radios can reject corrupted frames. Our federation
        //  transport is TCP (so corruption is rare) but having the trailer
        //  in place means a Wireshark dissector with the S5066 plugin
        //  will validate frames out of the box.
        private static readonly uint[] _crc32Table = BuildCrc32Table();
        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (poly ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }
        private static uint Crc32(byte[] data, int offset, int length)
        {
            uint c = 0xFFFFFFFFu;
            int end = offset + length;
            for (int i = offset; i < end; i++) c = _crc32Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        public byte[] ToBytes()
        {
            byte[] opBytes   = string.IsNullOrEmpty(senderOp) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(senderOp);
            byte[] noteBytes = string.IsNullOrEmpty(note)     ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(note);
            // Optional 2-byte srejRangeEnd field, only for type-3 SREJ.
            int srejExtra    = (type == DPduType.SelectiveReject) ? 2 : 0;
            int payloadSize  = srejExtra + 2 + opBytes.Length + 2 + noteBytes.Length;
            if (payloadSize > ushort.MaxValue)
                throw new InvalidOperationException($"D_PDU payload too large: {payloadSize}");

            byte[] frame = new byte[HeaderSize + payloadSize + CrcSize];

            // Sync.
            frame[0] = SyncByte0;
            frame[1] = SyncByte1;
            // Type nibble + EOW type nibble (we always emit EOW=0).
            frame[2] = (byte)((((byte)type) & 0x0F) << 4);
            frame[3] = 0;                                   // EOW data
            frame[4] = HeaderSize;
            // Payload size BE.
            frame[5] = (byte)(payloadSize >> 8);
            frame[6] = (byte)(payloadSize & 0xFF);
            frame[7] = 0;                                   // hop count
            // Source addr BE.
            ushort src = HashAddr(senderOp);
            frame[8] = (byte)(src >> 8);
            frame[9] = (byte)(src & 0xFF);
            // Dest addr BE.
            frame[10] = (byte)(BroadcastAddr >> 8);
            frame[11] = (byte)(BroadcastAddr & 0xFF);
            // ESN BE.
            frame[12] = (byte)(esn >> 8);
            frame[13] = (byte)(esn & 0xFF);
            // Type-specific tail.
            //   type 2 (DataWithAck) and type 3 (SelectiveReject) both
            //   carry a 16-bit ESN in the tail (ackEsn / rejectedEsn).
            if (type == DPduType.DataWithAck || type == DPduType.SelectiveReject)
            {
                frame[14] = (byte)(ackEsn >> 8);
                frame[15] = (byte)(ackEsn & 0xFF);
            }
            else
            {
                frame[14] = mode;
                frame[15] = (byte)Math.Min(messageCount, 255);
            }

            // Payload.
            int off = HeaderSize;
            // For type-3 SREJ, the range-end ESN goes first (2 bytes BE).
            if (type == DPduType.SelectiveReject)
            {
                frame[off++] = (byte)(srejRangeEnd >> 8);
                frame[off++] = (byte)(srejRangeEnd & 0xFF);
            }
            // Length-prefixed UTF-8 op then note.
            frame[off++] = (byte)(opBytes.Length >> 8);
            frame[off++] = (byte)(opBytes.Length & 0xFF);
            if (opBytes.Length > 0) { Buffer.BlockCopy(opBytes, 0, frame, off, opBytes.Length); off += opBytes.Length; }
            frame[off++] = (byte)(noteBytes.Length >> 8);
            frame[off++] = (byte)(noteBytes.Length & 0xFF);
            if (noteBytes.Length > 0) { Buffer.BlockCopy(noteBytes, 0, frame, off, noteBytes.Length); off += noteBytes.Length; }

            // CRC-32 over [0 .. HeaderSize + payloadSize) — header + payload.
            uint crc = Crc32(frame, 0, HeaderSize + payloadSize);
            int crcOff = HeaderSize + payloadSize;
            frame[crcOff    ] = (byte)((crc >> 24) & 0xFF);
            frame[crcOff + 1] = (byte)((crc >> 16) & 0xFF);
            frame[crcOff + 2] = (byte)((crc >>  8) & 0xFF);
            frame[crcOff + 3] = (byte)( crc        & 0xFF);

            return frame;
        }

        public static bool TryParseBytes(byte[] frame, out Stanag5066DPdu pdu)
        {
            pdu = default;
            if (frame == null || frame.Length < HeaderSize + CrcSize) return false;
            if (frame[0] != SyncByte0 || frame[1] != SyncByte1) return false;
            byte hdrSize = frame[4];
            if (hdrSize != HeaderSize) return false;
            int payloadSize = (frame[5] << 8) | frame[6];
            if (frame.Length < HeaderSize + payloadSize + CrcSize) return false;

            // CRC-32 verify — reject corrupted frames before deserialising.
            int crcOff = HeaderSize + payloadSize;
            uint expected = ((uint)frame[crcOff    ] << 24)
                          | ((uint)frame[crcOff + 1] << 16)
                          | ((uint)frame[crcOff + 2] <<  8)
                          | ((uint)frame[crcOff + 3]);
            uint actual = Crc32(frame, 0, HeaderSize + payloadSize);
            if (expected != actual) return false;

            pdu.type   = (DPduType)((frame[2] >> 4) & 0x0F);
            ushort src = (ushort)((frame[8] << 8) | frame[9]);
            // dst is read but unused locally (always 0xFFFF in our emissions).
            pdu.esn    = (ushort)((frame[12] << 8) | frame[13]);
            if (pdu.type == DPduType.DataWithAck || pdu.type == DPduType.SelectiveReject)
            {
                pdu.ackEsn = (ushort)((frame[14] << 8) | frame[15]);
                pdu.mode = 0;
                pdu.messageCount = 0;
            }
            else
            {
                pdu.mode = frame[14];
                pdu.messageCount = frame[15];
            }

            // Payload.
            int off = HeaderSize;
            int endOfFrame = HeaderSize + payloadSize;
            // For type-3 SREJ, the range-end ESN comes first.
            if (pdu.type == DPduType.SelectiveReject && off + 2 <= endOfFrame)
            {
                pdu.srejRangeEnd = (ushort)((frame[off] << 8) | frame[off + 1]);
                off += 2;
            }
            if (off + 2 <= endOfFrame)
            {
                int opLen = (frame[off] << 8) | frame[off + 1]; off += 2;
                if (opLen > 0 && off + opLen <= endOfFrame)
                {
                    pdu.senderOp = Encoding.UTF8.GetString(frame, off, opLen);
                    off += opLen;
                }
                else pdu.senderOp = "";
            }
            if (off + 2 <= endOfFrame)
            {
                int noteLen = (frame[off] << 8) | frame[off + 1]; off += 2;
                if (noteLen > 0 && off + noteLen <= endOfFrame)
                    pdu.note = Encoding.UTF8.GetString(frame, off, noteLen);
                else pdu.note = "";
            }

            // src is recorded only for completeness — could be exposed later.
            _ = src;
            return true;
        }

        private static ushort HashAddr(string s)
        {
            // FNV-1a 32-bit folded to 16-bit so the same op string hashes
            // deterministically across runs (so Wireshark can correlate flows).
            if (string.IsNullOrEmpty(s)) return 0;
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return (ushort)((h & 0xFFFF) ^ (h >> 16));
        }
    }
}
