// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066DPduTests.cs
//  ---------------------------------------------------------------------
//  Round-trip tests for the D_PDU XML wire format. If the codec breaks,
//  federation peers stop seeing each other's ARQ state — so we want
//  these tests locked in.
// =====================================================================

using NUnit.Framework;
using NATO.C2.Net;

namespace NATO.C2.Tests
{
    public class Stanag5066DPduTests
    {
        [Test]
        public void Type0_RoundTrip_PreservesAllFields()
        {
            var orig = Stanag5066DPdu.Data(
                esn: 0xABCD,
                mode: Link16TdmaSimulator.PackingMode.P2Dp,
                messageCount: 12,
                senderOp: "W1",
                note: "tx");

            string xml = orig.ToXmlFragment();
            Assert.IsTrue(Stanag5066DPdu.TryParse(xml, out var parsed),
                $"TryParse should succeed on emitted XML: {xml}");

            Assert.AreEqual(Stanag5066DPdu.DPduType.DataOnly, parsed.type);
            Assert.AreEqual((ushort)0xABCD,                    parsed.esn);
            Assert.AreEqual((byte)Link16TdmaSimulator.PackingMode.P2Dp, parsed.mode);
            Assert.AreEqual(12,                                parsed.messageCount);
            Assert.AreEqual("W1",                              parsed.senderOp);
            Assert.AreEqual("tx",                              parsed.note);
        }

        [Test]
        public void Type2_DataWithAck_RoundTrip_PreservesAckEsn()
        {
            var orig = Stanag5066DPdu.DataWithAck(
                esn: 0x0001,
                ackEsn: 0xFFFE,
                mode: Link16TdmaSimulator.PackingMode.StdDp,
                messageCount: 0,
                senderOp: "F3");

            string xml = orig.ToXmlFragment();
            Assert.IsTrue(Stanag5066DPdu.TryParse(xml, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.DataWithAck, parsed.type);
            Assert.AreEqual((ushort)0xFFFE, parsed.ackEsn);
            Assert.AreEqual("F3",           parsed.senderOp);
        }

        [Test]
        public void Type4_NonArq_RoundTrip()
        {
            var orig = Stanag5066DPdu.NonArq(
                esn: 0x0042,
                mode: Link16TdmaSimulator.PackingMode.P4Sp,
                messageCount: 3,
                senderOp: "X9",
                note: "beacon");

            string xml = orig.ToXmlFragment();
            Assert.IsTrue(Stanag5066DPdu.TryParse(xml, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.NonArqData, parsed.type);
            Assert.AreEqual((ushort)0x0042, parsed.esn);
            Assert.AreEqual((byte)Link16TdmaSimulator.PackingMode.P4Sp, parsed.mode);
        }

        [Test]
        public void TryParse_ReturnsFalse_OnUnrelatedXml()
        {
            Assert.IsFalse(Stanag5066DPdu.TryParse("<detail><contact callsign=\"X\"/></detail>", out _));
            Assert.IsFalse(Stanag5066DPdu.TryParse(null, out _));
            Assert.IsFalse(Stanag5066DPdu.TryParse("", out _));
        }

        // ====================================================================
        //  Binary wire format tests — these lock in the on-wire bytes so
        //  a Wireshark dissector keeps decoding traffic correctly.
        // ====================================================================

        [Test]
        public void Binary_Type0_HeaderFieldsCorrect()
        {
            var pdu = Stanag5066DPdu.Data(
                esn: 0x1234,
                mode: Link16TdmaSimulator.PackingMode.P2Dp,
                messageCount: 12,
                senderOp: "W1",
                note: "tx");
            byte[] frame = pdu.ToBytes();

            // Sync sequence.
            Assert.AreEqual(Stanag5066DPdu.SyncByte0, frame[0], "sync byte 0");
            Assert.AreEqual(Stanag5066DPdu.SyncByte1, frame[1], "sync byte 1");
            // Type nibble (0) shifted into high nibble of byte 2.
            Assert.AreEqual((byte)0x00, frame[2], "type nibble for DataOnly");
            // Header size.
            Assert.AreEqual(Stanag5066DPdu.HeaderSize, frame[4]);
            // ESN big-endian.
            Assert.AreEqual(0x12, frame[12], "ESN high byte");
            Assert.AreEqual(0x34, frame[13], "ESN low byte");
            // Mode + msg count in type-specific tail.
            Assert.AreEqual((byte)Link16TdmaSimulator.PackingMode.P2Dp, frame[14], "mode byte");
            Assert.AreEqual((byte)12, frame[15], "messageCount byte");
        }

        [Test]
        public void Binary_RoundTrip_Type0()
        {
            var orig = Stanag5066DPdu.Data(0xCAFE, Link16TdmaSimulator.PackingMode.StdDp,
                                           7, "F3", "round-trip");
            byte[] frame = orig.ToBytes();
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.DataOnly, parsed.type);
            Assert.AreEqual((ushort)0xCAFE, parsed.esn);
            Assert.AreEqual((byte)Link16TdmaSimulator.PackingMode.StdDp, parsed.mode);
            Assert.AreEqual(7, parsed.messageCount);
            Assert.AreEqual("F3", parsed.senderOp);
            Assert.AreEqual("round-trip", parsed.note);
        }

        [Test]
        public void Binary_RoundTrip_Type2_AckEsnPreserved()
        {
            var orig = Stanag5066DPdu.DataWithAck(esn: 0x0007, ackEsn: 0xBEEF,
                                                  Link16TdmaSimulator.PackingMode.P4Sp,
                                                  messageCount: 0, senderOp: "X9");
            byte[] frame = orig.ToBytes();
            Assert.AreEqual(0x20, frame[2], "type nibble for DataWithAck shifted to high nibble of byte 2");
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.DataWithAck, parsed.type);
            Assert.AreEqual((ushort)0x0007, parsed.esn);
            Assert.AreEqual((ushort)0xBEEF, parsed.ackEsn);
            Assert.AreEqual("X9", parsed.senderOp);
        }

        [Test]
        public void Binary_RejectsCorruptSync()
        {
            var orig = Stanag5066DPdu.Data(1, Link16TdmaSimulator.PackingMode.StdDp, 1, "OP");
            byte[] frame = orig.ToBytes();
            frame[0] = 0xAA;   // corrupt sync
            Assert.IsFalse(Stanag5066DPdu.TryParseBytes(frame, out _));
        }

        [Test]
        public void Binary_RejectsTruncatedFrame()
        {
            var orig = Stanag5066DPdu.Data(1, Link16TdmaSimulator.PackingMode.StdDp, 1, "OP", "note");
            byte[] frame = orig.ToBytes();
            byte[] truncated = new byte[frame.Length - 3];
            System.Buffer.BlockCopy(frame, 0, truncated, 0, truncated.Length);
            Assert.IsFalse(Stanag5066DPdu.TryParseBytes(truncated, out _));
        }

        [Test]
        public void Binary_RoundTripIncludesCrc32_AndTrailerIsFourBytes()
        {
            var orig = Stanag5066DPdu.Data(0x0042, Link16TdmaSimulator.PackingMode.StdDp,
                                           1, "W1", "crc");
            byte[] frame = orig.ToBytes();
            // Frame must end with 4 bytes of CRC trailer beyond header + payload.
            int payloadSize = (frame[5] << 8) | frame[6];
            Assert.AreEqual(Stanag5066DPdu.HeaderSize + payloadSize + Stanag5066DPdu.CrcSize, frame.Length);
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual((ushort)0x0042, parsed.esn);
        }

        [Test]
        public void Binary_TamperedPayload_FailsCrcAndRejects()
        {
            var orig = Stanag5066DPdu.Data(0x0042, Link16TdmaSimulator.PackingMode.StdDp,
                                           1, "W1", "original-note");
            byte[] frame = orig.ToBytes();
            // Flip a bit somewhere in the payload region. Anywhere in the
            // op/note bytes (offset ≥ HeaderSize+2) will do.
            int payloadByte = Stanag5066DPdu.HeaderSize + 3;
            frame[payloadByte] ^= 0x01;
            Assert.IsFalse(Stanag5066DPdu.TryParseBytes(frame, out _),
                "CRC check should reject the tampered frame.");
        }

        [Test]
        public void Binary_RoundTrip_SelectiveReject_CarriesRejectedEsn()
        {
            var orig = Stanag5066DPdu.SelectiveReject(rejectedEsn: 0xDEAD, senderOp: "F3", note: "gap");
            byte[] frame = orig.ToBytes();
            Assert.AreEqual(0x30, frame[2] & 0xF0, "type nibble for SelectiveReject is 3");
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.SelectiveReject, parsed.type);
            Assert.AreEqual((ushort)0xDEAD, parsed.ackEsn, "rejected ESN carried in ackEsn field");
            Assert.AreEqual("F3", parsed.senderOp);
        }

        [Test]
        public void Binary_SelectiveRejectRange_RoundTrip_PreservesBothEsns()
        {
            var orig = Stanag5066DPdu.SelectiveRejectRange(
                startEsn: 0x0010, endEsn: 0x0014, senderOp: "F3", note: "range");
            byte[] frame = orig.ToBytes();
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.SelectiveReject, parsed.type);
            Assert.AreEqual((ushort)0x0010, parsed.ackEsn,       "range start carried in ackEsn");
            Assert.AreEqual((ushort)0x0014, parsed.srejRangeEnd, "range end carried in srejRangeEnd");
            Assert.AreEqual("F3", parsed.senderOp);
        }

        [Test]
        public void Binary_SingleSREJ_HasZeroRangeEnd_AfterRoundTrip()
        {
            var orig = Stanag5066DPdu.SelectiveReject(rejectedEsn: 0x0099, senderOp: "OP");
            byte[] frame = orig.ToBytes();
            Assert.IsTrue(Stanag5066DPdu.TryParseBytes(frame, out var parsed));
            Assert.AreEqual((ushort)0x0099, parsed.ackEsn);
            Assert.AreEqual((ushort)0x0000, parsed.srejRangeEnd, "single-SREJ has rangeEnd=0");
        }

        [Test]
        public void Xml_SelectiveReject_RoundTrip()
        {
            var orig = Stanag5066DPdu.SelectiveReject(rejectedEsn: 0x00AA, senderOp: "OP");
            string xml = orig.ToXmlFragment();
            StringAssert.Contains("type=\"3\"", xml);
            StringAssert.Contains("ack=\"00AA\"", xml);
            Assert.IsTrue(Stanag5066DPdu.TryParse(xml, out var parsed));
            Assert.AreEqual(Stanag5066DPdu.DPduType.SelectiveReject, parsed.type);
            Assert.AreEqual((ushort)0x00AA, parsed.ackEsn);
        }

        [Test]
        public void Binary_TamperedHeader_FailsCrcAndRejects()
        {
            var orig = Stanag5066DPdu.Data(0x0042, Link16TdmaSimulator.PackingMode.StdDp,
                                           1, "W1");
            byte[] frame = orig.ToBytes();
            frame[13] ^= 0x01;   // flip a bit in the ESN field
            Assert.IsFalse(Stanag5066DPdu.TryParseBytes(frame, out _),
                "CRC check should reject a header tamper.");
        }

        [Test]
        public void XmlEscape_SurvivesQuotesAndSpecialChars()
        {
            var orig = Stanag5066DPdu.Data(
                esn: 1,
                mode: Link16TdmaSimulator.PackingMode.StdDp,
                messageCount: 1,
                senderOp: "OP",
                note: "has \"quote\" & <angle>");

            string xml = orig.ToXmlFragment();
            // The escaped form must not contain raw special chars in attribute values.
            Assert.IsTrue(Stanag5066DPdu.TryParse(xml, out var parsed));
            Assert.AreEqual("has \"quote\" & <angle>", parsed.note);
        }
    }
}
