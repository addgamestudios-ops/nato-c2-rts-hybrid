// =====================================================================
//  NATO C2 RTS Hybrid — ScenarioSignerTests.cs
//  ---------------------------------------------------------------------
//  Locks down the RSA-2048-SHA256 sign/verify pipeline used by
//  ScenarioSigner. The full Editor tool drives File I/O; these tests
//  exercise the same primitives in-memory so a Mono / .NET runtime
//  change (or an accidental algorithm swap) fails fast in CI.
// =====================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using NATO.C2.EditorTools;

namespace NATO.C2.Tests
{
    public class ScenarioSignerTests
    {
        [Test]
        public void SignVerify_RoundTrip_Succeeds()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(
                "{\"name\":\"smoke\",\"steps\":[{\"atSec\":0,\"kind\":0}]}");
            using var rsa = new RSACryptoServiceProvider(ScenarioSigner.RsaKeySize);
            byte[] sig = rsa.SignData(data, SHA256.Create());
            Assert.IsTrue(rsa.VerifyData(data, SHA256.Create(), sig),
                "RSA sign/verify round-trip must succeed");
        }

        [Test]
        public void SignVerify_RejectsTamperedData()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("{\"name\":\"original\"}");
            using var rsa = new RSACryptoServiceProvider(ScenarioSigner.RsaKeySize);
            byte[] sig = rsa.SignData(data, SHA256.Create());

            byte[] tampered = (byte[])data.Clone();
            tampered[5] ^= 1;
            Assert.IsFalse(rsa.VerifyData(tampered, SHA256.Create(), sig),
                "tampered data must fail verification");
        }

        [Test]
        public void VerifyScenarioFile_ReturnsNoSidecar_WhenSigMissing()
        {
            string tmpJson = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tmpJson, "{\"name\":\"x\",\"steps\":[]}");
                var result = ScenarioSigner.VerifyScenarioFile(tmpJson);
                Assert.AreEqual(ScenarioSigner.VerifyResult.NoSidecar, result);
            }
            finally
            {
                if (File.Exists(tmpJson)) File.Delete(tmpJson);
            }
        }

        [Test]
        public void VerifyScenarioFile_MultiSig_RequiresThreshold()
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), $"scenario-multi-{Guid.NewGuid():N}");
            string cosignersDir = Path.Combine(tmpDir, ScenarioSigner.CosignersSubdir);
            Directory.CreateDirectory(cosignersDir);
            string jsonPath = Path.Combine(tmpDir, "smoke.json");
            string sigPath  = jsonPath + ScenarioSigner.SigExt;

            // Save current threshold env so we can restore it.
            string priorThreshold = Environment.GetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD");

            try
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(
                    "{\"name\":\"smoke\",\"steps\":[]}");
                File.WriteAllBytes(jsonPath, data);

                // Generate 3 distinct cosigner keypairs.
                string[] cosignerIds = { "jtac", "s6", "cac2" };
                var cosignerSigs = new System.Collections.Generic.List<ScenarioSigner.SignatureEntry>();
                foreach (var id in cosignerIds)
                {
                    using var rsa = new RSACryptoServiceProvider(ScenarioSigner.RsaKeySize);
                    File.WriteAllText(Path.Combine(cosignersDir, id + ".xml"), rsa.ToXmlString(false));
                    byte[] sig = rsa.SignData(data, SHA256.Create());
                    cosignerSigs.Add(new ScenarioSigner.SignatureEntry
                    {
                        keyId = id,
                        sig = Convert.ToBase64String(sig)
                    });
                }

                // ----- Case 1: 3 valid signatures, threshold=1 → Ok -----
                WriteMultiSigSidecar(sigPath, cosignerSigs.ToArray());
                Environment.SetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD", "1");
                Assert.AreEqual(ScenarioSigner.VerifyResult.Ok,
                                ScenarioSigner.VerifyScenarioFile(jsonPath),
                                "3 sigs vs threshold=1 should be Ok");

                // ----- Case 2: 3 valid signatures, threshold=2 → Ok -----
                Environment.SetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD", "2");
                Assert.AreEqual(ScenarioSigner.VerifyResult.Ok,
                                ScenarioSigner.VerifyScenarioFile(jsonPath),
                                "3 sigs vs threshold=2 should be Ok");

                // ----- Case 3: only 1 valid signature, threshold=2 → BelowThreshold -----
                WriteMultiSigSidecar(sigPath, new[] { cosignerSigs[0] });
                Assert.AreEqual(ScenarioSigner.VerifyResult.BelowThreshold,
                                ScenarioSigner.VerifyScenarioFile(jsonPath),
                                "1 sig vs threshold=2 should be BelowThreshold");

                // ----- Case 4: tamper data → no valid sigs → BadSignature -----
                data[0] = (byte)'X';
                File.WriteAllBytes(jsonPath, data);
                WriteMultiSigSidecar(sigPath, cosignerSigs.ToArray());
                Environment.SetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD", "1");
                Assert.AreEqual(ScenarioSigner.VerifyResult.BadSignature,
                                ScenarioSigner.VerifyScenarioFile(jsonPath),
                                "all sigs invalid after tamper");
            }
            finally
            {
                Environment.SetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD", priorThreshold);
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        private static void WriteMultiSigSidecar(string path, ScenarioSigner.SignatureEntry[] sigs)
        {
            // JsonUtility doesn't pretty-print arrays nicely — hand-build.
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"signatures\":[");
            for (int i = 0; i < sigs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"keyId\":\"").Append(sigs[i].keyId)
                  .Append("\",\"sig\":\"").Append(sigs[i].sig).Append("\"}");
            }
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString());
        }

        [Test]
        public void VerifyScenarioFile_FullRoundTrip_WithGeneratedKey()
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), $"scenario-sign-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            string jsonPath = Path.Combine(tmpDir, "smoke.json");
            string pubPath  = Path.Combine(tmpDir, ScenarioSigner.PubKeyFile);
            string sigPath  = jsonPath + ScenarioSigner.SigExt;
            try
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(
                    "{\"name\":\"smoke\",\"steps\":[{\"atSec\":0,\"kind\":0,\"valueFloat\":0,\"valueText\":\"\"}]}");
                File.WriteAllBytes(jsonPath, data);

                using (var rsa = new RSACryptoServiceProvider(ScenarioSigner.RsaKeySize))
                {
                    File.WriteAllText(pubPath, rsa.ToXmlString(false));
                    byte[] sig = rsa.SignData(data, SHA256.Create());
                    File.WriteAllText(sigPath, Convert.ToBase64String(sig));
                }

                Assert.AreEqual(ScenarioSigner.VerifyResult.Ok,
                                ScenarioSigner.VerifyScenarioFile(jsonPath));

                // Tamper the JSON, re-verify → BadSignature.
                data[6] ^= 1;
                File.WriteAllBytes(jsonPath, data);
                Assert.AreEqual(ScenarioSigner.VerifyResult.BadSignature,
                                ScenarioSigner.VerifyScenarioFile(jsonPath));
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }
}
