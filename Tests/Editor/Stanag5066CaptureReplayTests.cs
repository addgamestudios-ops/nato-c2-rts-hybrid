// =====================================================================
//  NATO C2 RTS Hybrid — Stanag5066CaptureReplayTests.cs
//  ---------------------------------------------------------------------
//  Replay-driven test: build a synthetic .dpdu capture covering every
//  D_PDU type we emit, write it to a temp file, read it back through
//  Stanag5066Capture.Read, and assert each frame TryParseBytes
//  succeeds with the original fields intact.
//
//  Why this exists: locks the binary wire format down regression-proof.
//  If anyone changes the byte layout in a way that breaks compatibility,
//  this test fails with a precise frame-index pointer.
// =====================================================================

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NATO.C2.Net;

namespace NATO.C2.Tests
{
    public class Stanag5066CaptureReplayTests
    {
        // Reference fixture — one of each D_PDU type the bridge emits.
        private static List<Stanag5066DPdu> BuildFixture()
        {
            return new List<Stanag5066DPdu>
            {
                Stanag5066DPdu.Data(0x0001, Link16TdmaSimulator.PackingMode.StdDp, 6, "W1", "tx"),
                Stanag5066DPdu.Data(0x0002, Link16TdmaSimulator.PackingMode.P2Dp, 12, "W1", "tx"),
                Stanag5066DPdu.DataWithAck(0x0003, 0x0001, Link16TdmaSimulator.PackingMode.StdDp, 6, "W1"),
                Stanag5066DPdu.DataWithAck(0x0000, 0x0002, Link16TdmaSimulator.PackingMode.StdDp, 0, "W1"), // pure ACK
                Stanag5066DPdu.SelectiveReject(0x00AA, "F3", "gap"),
                Stanag5066DPdu.SelectiveRejectRange(0x00B0, 0x00B7, "F3", "range"),
                Stanag5066DPdu.NonArq(0x00CC, Link16TdmaSimulator.PackingMode.P4Sp, 3, "X9", "beacon"),
            };
        }

        [Test]
        public void CaptureFile_RoundTrips_EveryFrame()
        {
            string path = Path.Combine(Path.GetTempPath(), $"S5CP_test_{System.Guid.NewGuid():N}.dpdu");
            try
            {
                // ----- Write (v2 — header + records + chain-hash trailers) -----
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(Stanag5066Capture.Magic, 0, 4);
                    fs.WriteByte(Stanag5066Capture.FormatVersion);
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);

                    byte[] prev = new byte[Stanag5066Capture.HashSize];
                    var fixture = BuildFixture();
                    for (int i = 0; i < fixture.Count; i++)
                    {
                        byte[] frame = fixture[i].ToBytes();
                        Stanag5066Capture.Direction dir = (i % 2 == 0)
                            ? Stanag5066Capture.Direction.Tx
                            : Stanag5066Capture.Direction.Rx;
                        var hdr = new byte[8];
                        hdr[0] = (byte)dir;
                        hdr[4] = (byte)((frame.Length >> 24) & 0xFF);
                        hdr[5] = (byte)((frame.Length >> 16) & 0xFF);
                        hdr[6] = (byte)((frame.Length >>  8) & 0xFF);
                        hdr[7] = (byte)( frame.Length        & 0xFF);
                        fs.Write(hdr,   0, hdr.Length);
                        fs.Write(frame, 0, frame.Length);
                        byte[] chain = Stanag5066Capture.ComputeChainHash(prev, hdr, frame);
                        fs.Write(chain, 0, chain.Length);
                        prev = chain;
                    }
                }

                // ----- Read back -----
                var records = Stanag5066Capture.Read(path);
                var fixture2 = BuildFixture();
                Assert.AreEqual(fixture2.Count, records.Count, "record count must match");

                for (int i = 0; i < records.Count; i++)
                {
                    Assert.IsTrue(Stanag5066DPdu.TryParseBytes(records[i].frame, out var pdu),
                        $"frame {i} (type={fixture2[i].type}) failed to parse");
                    Assert.AreEqual(fixture2[i].type,         pdu.type,         $"frame {i} type");
                    Assert.AreEqual(fixture2[i].esn,          pdu.esn,          $"frame {i} esn");
                    Assert.AreEqual(fixture2[i].ackEsn,       pdu.ackEsn,       $"frame {i} ackEsn");
                    Assert.AreEqual(fixture2[i].srejRangeEnd, pdu.srejRangeEnd, $"frame {i} srejRangeEnd");
                    Assert.AreEqual(fixture2[i].senderOp,     pdu.senderOp,     $"frame {i} senderOp");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Verify_DetectsTamperedFrame()
        {
            string path = Path.Combine(Path.GetTempPath(), $"S5CP_tamper_{System.Guid.NewGuid():N}.dpdu");
            try
            {
                WriteV2Capture(path, BuildFixture(), out _);
                var records = Stanag5066Capture.Read(path);
                Assert.AreEqual(-1, Stanag5066Capture.Verify(records),
                    "freshly-written chain must verify intact");

                // Flip a bit in the middle frame's bytes (in-memory list, not file).
                records[2].frame[5] ^= 0x01;
                int badIdx = Stanag5066Capture.Verify(records);
                Assert.AreEqual(2, badIdx,
                    $"Verify should pinpoint the tampered record (got idx {badIdx})");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Read_AcceptsLegacyV1File_NoChainHash()
        {
            string path = Path.Combine(Path.GetTempPath(), $"S5CP_v1_{System.Guid.NewGuid():N}.dpdu");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(Stanag5066Capture.Magic, 0, 4);
                    fs.WriteByte(1);                  // legacy v1
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);
                    var frame = Stanag5066DPdu.Data(0x0001, Link16TdmaSimulator.PackingMode.StdDp,
                                                    6, "W1").ToBytes();
                    fs.WriteByte(0);                  // direction
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);
                    fs.WriteByte((byte)((frame.Length >> 24) & 0xFF));
                    fs.WriteByte((byte)((frame.Length >> 16) & 0xFF));
                    fs.WriteByte((byte)((frame.Length >>  8) & 0xFF));
                    fs.WriteByte((byte)( frame.Length        & 0xFF));
                    fs.Write(frame, 0, frame.Length);
                    // No chain hash — this is v1.
                }
                var records = Stanag5066Capture.Read(path);
                Assert.AreEqual(1, records.Count);
                Assert.IsNull(records[0].chainHash, "v1 records carry no chainHash");
                Assert.Throws<System.ArgumentException>(() => Stanag5066Capture.Verify(records),
                    "Verify should refuse to chain-check v1 records");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void WriteV2Capture(string path,
                                           System.Collections.Generic.List<Stanag5066DPdu> fixture,
                                           out int frameCount)
        {
            frameCount = fixture.Count;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(Stanag5066Capture.Magic, 0, 4);
            fs.WriteByte(Stanag5066Capture.FormatVersion);
            fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);
            byte[] prev = new byte[Stanag5066Capture.HashSize];
            for (int i = 0; i < fixture.Count; i++)
            {
                byte[] frame = fixture[i].ToBytes();
                var hdr = new byte[8];
                hdr[0] = (byte)Stanag5066Capture.Direction.Tx;
                hdr[4] = (byte)((frame.Length >> 24) & 0xFF);
                hdr[5] = (byte)((frame.Length >> 16) & 0xFF);
                hdr[6] = (byte)((frame.Length >>  8) & 0xFF);
                hdr[7] = (byte)( frame.Length        & 0xFF);
                fs.Write(hdr,   0, hdr.Length);
                fs.Write(frame, 0, frame.Length);
                byte[] chain = Stanag5066Capture.ComputeChainHash(prev, hdr, frame);
                fs.Write(chain, 0, chain.Length);
                prev = chain;
            }
        }

        [Test]
        public void Read_RejectsBadMagic()
        {
            string path = Path.Combine(Path.GetTempPath(), $"S5CP_bad_{System.Guid.NewGuid():N}.dpdu");
            try
            {
                File.WriteAllBytes(path, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0, 0, 0 });
                Assert.Throws<InvalidDataException>(() => Stanag5066Capture.Read(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Read_RejectsTruncatedRecord()
        {
            string path = Path.Combine(Path.GetTempPath(), $"S5CP_trunc_{System.Guid.NewGuid():N}.dpdu");
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(Stanag5066Capture.Magic, 0, 4);
                    fs.WriteByte(Stanag5066Capture.FormatVersion);
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);
                    // Direction + reserved.
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0);
                    // Claim length = 100 but only write 4 bytes.
                    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(100);
                    fs.Write(new byte[4], 0, 4);
                }
                Assert.Throws<InvalidDataException>(() => Stanag5066Capture.Read(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
