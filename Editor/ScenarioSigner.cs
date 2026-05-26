// =====================================================================
//  NATO C2 RTS Hybrid — ScenarioSigner.cs
//  ---------------------------------------------------------------------
//  Editor tool that signs chaos scenarios with RSA-2048 SHA-256 so the
//  Editor can refuse to run tampered scripts in CI / release builds.
//
//  Why RSA instead of Ed25519:
//      Unity 6's Mono runtime ships RSA in System.Security.Cryptography.
//      Ed25519 would require bundling a third-party crypto lib, which
//      is overkill for "is this scenario the one we shipped".
//
//  Wire format:
//      • For each scenario.json, a sidecar scenario.json.sig is
//        produced. It contains base64-encoded RSA-SHA256 of the raw
//        JSON bytes (signature is over the whole file, byte-for-byte,
//        so no canonicalisation is needed).
//      • The public key is bundled at
//          Tools/chaos-scenarios/scenarios-pub.xml
//        (Unity-compatible RSA XML, via RSA.ToXmlString(false)).
//      • The private key is written to
//          Tools/chaos-scenarios/scenarios-priv.xml
//        which the project's .gitignore explicitly excludes.
//
//  Operator workflow:
//      1. NATO C2 → Link 16 → Scenario Signer
//      2. "Generate keypair" — overwrites pub + priv files. Do this
//         exactly once for the project, then back up scenarios-priv.xml
//         OUT OF the repo (e.g. into 1Password).
//      3. "Sign all scenarios" — walks Tools/chaos-scenarios/*.json
//         and emits a fresh .sig sidecar for each.
//      4. After editing a scenario, click "Sign all" again so the .sig
//         stays in lockstep. CI rejects any unsigned scenario.
// =====================================================================

#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NATO.C2.EditorTools
{
    public class ScenarioSigner : EditorWindow
    {
        public const string PubKeyFile  = "scenarios-pub.xml";
        public const string PrivKeyFile = "scenarios-priv.xml";
        public const string SigExt      = ".sig";
        public const int    RsaKeySize  = 2048;
        // Folder under ScenariosDir where archived (rotated-out) public
        // keys go, so an old .sig can still be traced to its origin even
        // after the active key has changed.
        public const string KeyArchiveSubdir = "key-archive";
        /// <summary>Sub-folder holding per-cosigner public keys ({keyId}.xml).</summary>
        public const string CosignersSubdir = "cosigners";

        [MenuItem("NATO C2/Link 16/Scenario Signer", priority = 63)]
        public static void Open()
        {
            var w = GetWindow<ScenarioSigner>("Scenario Signer");
            w.minSize = new Vector2(520, 280);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Scenario Signer", EditorStyles.boldLabel);
            string dir = FederationChaosMode.ScenariosDir;
            EditorGUILayout.LabelField("Folder:", dir, EditorStyles.miniLabel);

            string pubPath  = Path.Combine(dir, PubKeyFile);
            string privPath = Path.Combine(dir, PrivKeyFile);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Status:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Public key:  " + (File.Exists(pubPath)  ? "FOUND" : "MISSING"));
            EditorGUILayout.LabelField("Private key: " + (File.Exists(privPath) ? "FOUND (do NOT commit!)" : "missing"));

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "Scenarios are signed offline with the project's private key. CI verifies them " +
                "against the public key bundled in the repo. Generate the keypair ONCE, back up " +
                "the private key OUT of the repo, then sign every scenario you edit.",
                MessageType.Info);

            // Show current keyId if available.
            string kid = ReadKeyId(pubPath);
            EditorGUILayout.LabelField("keyId:       " + (kid ?? "(none — pre-rotation legacy key)"));

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate keypair", GUILayout.Height(28))) GenerateKeypair(dir);
                if (GUILayout.Button("Sign all scenarios", GUILayout.Height(28))) SignAll(dir);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(!File.Exists(privPath));
                if (GUILayout.Button("Rotate keypair (key leak response)", GUILayout.Height(28)))
                {
                    if (EditorUtility.DisplayDialog("Rotate keypair",
                        "Archive the current public key, generate a fresh keypair, and re-sign " +
                        "every scenario. Old .sig files made with the rotated-out key will no longer verify. " +
                        "Proceed?", "Rotate", "Cancel"))
                    {
                        RotateKeypair(dir);
                    }
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Verify all scenarios (read-only check)", GUILayout.Height(24)))
                VerifyAll(dir);
        }

        // ====================================================================
        //  Operations
        // ====================================================================
        public static void GenerateKeypair(string dir, string keyId = null)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (string.IsNullOrEmpty(keyId)) keyId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            using var rsa = new RSACryptoServiceProvider(RsaKeySize);
            // Wrap with a keyId comment so verifiers can log which key
            // they're trusting without rebuilding the trust store.
            string pubXml  = "<!-- keyId: " + keyId + " -->\n" + rsa.ToXmlString(false);
            string privXml = "<!-- keyId: " + keyId + " -->\n" + rsa.ToXmlString(true);
            File.WriteAllText(Path.Combine(dir, PubKeyFile),  pubXml);
            File.WriteAllText(Path.Combine(dir, PrivKeyFile), privXml);
            Debug.Log($"[ScenarioSigner] generated RSA-{RsaKeySize} keypair (keyId={keyId}) in {dir}\n" +
                      $"  pub  → {PubKeyFile}\n  priv → {PrivKeyFile}\n" +
                      "  ⚠ MOVE THE PRIVATE KEY OUT OF THE REPO before committing.");
            EditorUtility.RevealInFinder(dir);
        }

        /// <summary>
        /// Leak-response: archive the current public key into key-archive/
        /// with its keyId, generate a fresh keypair (new keyId), and
        /// re-sign every scenario so old .sig files made with the
        /// compromised key stop verifying.
        /// </summary>
        public static void RotateKeypair(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string pubPath  = Path.Combine(dir, PubKeyFile);
            string privPath = Path.Combine(dir, PrivKeyFile);
            if (!File.Exists(privPath))
            {
                EditorUtility.DisplayDialog("Rotate failed",
                    "Need scenarios-priv.xml in the folder to re-sign during rotation. " +
                    "Restore the current private key first, then rotate.", "OK");
                return;
            }

            // 1. Archive the current public key under a timestamped name.
            string archiveDir = Path.Combine(dir, KeyArchiveSubdir);
            if (!Directory.Exists(archiveDir)) Directory.CreateDirectory(archiveDir);
            string currentKeyId = ReadKeyId(pubPath) ?? "unknown";
            string archived = Path.Combine(archiveDir,
                $"scenarios-pub.{currentKeyId}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.xml");
            if (File.Exists(pubPath)) File.Copy(pubPath, archived, overwrite: true);
            Debug.Log($"[ScenarioSigner] archived old pub key ({currentKeyId}) → {archived}");

            // 2. Generate the new keypair (new keyId).
            string newKeyId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            GenerateKeypair(dir, newKeyId);

            // 3. Re-sign every scenario with the new key.
            SignAll(dir);

            Debug.Log($"[ScenarioSigner] rotation complete — active keyId={newKeyId}, " +
                      $"every .sig refreshed. Old .sig files (if any survive in another " +
                      "branch) will no longer verify against the new public key.");
        }

        /// <summary>
        /// Load an RSA key from a file that may have a leading XML
        /// comment (containing keyId). Mono's FromXmlString chokes on
        /// pre-element comments, so we strip them first.
        /// </summary>
        public static RSACryptoServiceProvider LoadRsaFromFile(string path)
        {
            string xml = File.ReadAllText(path);
            int rsaStart = xml.IndexOf("<RSAKeyValue", StringComparison.Ordinal);
            if (rsaStart > 0) xml = xml.Substring(rsaStart);
            var rsa = new RSACryptoServiceProvider(RsaKeySize);
            rsa.FromXmlString(xml);
            return rsa;
        }

        /// <summary>
        /// Extracts the keyId embedded in the public XML's leading
        /// HTML comment. Returns null if the file is missing or has
        /// no keyId comment (pre-rotation legacy file).
        /// </summary>
        public static string ReadKeyId(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath)) return null;
                using var sr = new StreamReader(xmlPath);
                // Read up to the first non-empty line that should be the comment.
                for (int i = 0; i < 4; i++)
                {
                    string line = sr.ReadLine();
                    if (line == null) break;
                    int idx = line.IndexOf("keyId:", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    int start = idx + "keyId:".Length;
                    int end   = line.IndexOf("-->", start, StringComparison.Ordinal);
                    if (end < 0) end = line.Length;
                    return line.Substring(start, end - start).Trim();
                }
            }
            catch { /* swallow */ }
            return null;
        }

        public static void SignAll(string dir)
        {
            string privPath = Path.Combine(dir, PrivKeyFile);
            if (!File.Exists(privPath))
            {
                EditorUtility.DisplayDialog("Missing private key",
                    "Generate the keypair first, then move scenarios-priv.xml back here temporarily " +
                    "to sign new scenarios.", "OK");
                return;
            }
            using var rsa = LoadRsaFromFile(privPath);

            int signed = 0;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                byte[] data = File.ReadAllBytes(f);
                byte[] sig  = rsa.SignData(data, SHA256.Create());
                File.WriteAllText(f + SigExt, Convert.ToBase64String(sig));
                signed++;
            }
            Debug.Log($"[ScenarioSigner] signed {signed} scenario(s) in {dir}");
        }

        public static void VerifyAll(string dir)
        {
            string pubPath = Path.Combine(dir, PubKeyFile);
            if (!File.Exists(pubPath))
            {
                EditorUtility.DisplayDialog("Missing public key",
                    $"Expected {PubKeyFile} in {dir}.", "OK");
                return;
            }
            using var rsa = LoadRsaFromFile(pubPath);

            int ok = 0, bad = 0, missing = 0;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                string sigPath = f + SigExt;
                if (!File.Exists(sigPath)) { missing++; Debug.LogWarning($"  · {Path.GetFileName(f)} — NO SIGNATURE"); continue; }
                byte[] sig  = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
                byte[] data = File.ReadAllBytes(f);
                if (rsa.VerifyData(data, SHA256.Create(), sig))
                {
                    ok++;
                    Debug.Log($"  ✓ {Path.GetFileName(f)}");
                }
                else
                {
                    bad++;
                    Debug.LogError($"  ✗ {Path.GetFileName(f)} — SIGNATURE INVALID");
                }
            }
            Debug.Log($"[ScenarioSigner] verify result — ok={ok} bad={bad} unsigned={missing}");
        }

        // ====================================================================
        //  Public API consumed by FederationChaosMode.
        // ====================================================================

        public enum VerifyResult { Ok, NoSidecar, NoPublicKey, BadSignature, BelowThreshold, Exception }

        [System.Serializable]
        public class SignatureEntry
        {
            public string keyId;
            public string sig;          // base64
        }

        [System.Serializable]
        private class SignatureSidecar
        {
            // JsonUtility can't serialise top-level arrays, so we wrap.
            public SignatureEntry[] signatures;
        }

        /// <summary>
        /// Verify a scenario JSON against its sidecar .sig. Sidecar
        /// formats supported:
        ///   • legacy — raw base64 of an RSA-SHA256 sig (single signer,
        ///              verified against scenarios-pub.xml).
        ///   • multi  — JSON {"signatures":[{"keyId":"jtac","sig":"..."}, …]}.
        ///              Each entry verified against cosigners/{keyId}.xml.
        ///              Threshold (NATO_SIGNATURE_THRESHOLD env, default 1)
        ///              is the minimum number of DISTINCT valid signers
        ///              required for Ok. Below threshold → BelowThreshold.
        /// </summary>
        public static VerifyResult VerifyScenarioFile(string scenarioJsonPath)
        {
            try
            {
                string sigPath = scenarioJsonPath + SigExt;
                string dir     = Path.GetDirectoryName(scenarioJsonPath) ?? "";
                if (!File.Exists(sigPath)) return VerifyResult.NoSidecar;
                byte[] data = File.ReadAllBytes(scenarioJsonPath);
                string sigText = File.ReadAllText(sigPath).Trim();

                // ----- multi-sig (JSON) -----
                if (sigText.StartsWith("{"))
                {
                    return VerifyMultiSig(data, sigText, dir);
                }

                // ----- legacy single-sig -----
                string pubPath = Path.Combine(dir, PubKeyFile);
                if (!File.Exists(pubPath)) return VerifyResult.NoPublicKey;
                using var rsa = LoadRsaFromFile(pubPath);
                byte[] sig = Convert.FromBase64String(sigText);
                return rsa.VerifyData(data, SHA256.Create(), sig)
                    ? VerifyResult.Ok
                    : VerifyResult.BadSignature;
            }
            catch (Exception)
            {
                return VerifyResult.Exception;
            }
        }

        private static VerifyResult VerifyMultiSig(byte[] data, string sidecarJson, string scenariosDir)
        {
            var dto = JsonUtility.FromJson<SignatureSidecar>(sidecarJson);
            if (dto == null || dto.signatures == null || dto.signatures.Length == 0)
                return VerifyResult.BadSignature;

            int threshold = ReadThreshold();
            string cosignersDir = Path.Combine(scenariosDir, CosignersSubdir);
            if (!Directory.Exists(cosignersDir)) return VerifyResult.NoPublicKey;

            var distinctValidSigners = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in dto.signatures)
            {
                if (entry == null || string.IsNullOrEmpty(entry.keyId) || string.IsNullOrEmpty(entry.sig))
                    continue;
                string pubFile = Path.Combine(cosignersDir, entry.keyId + ".xml");
                if (!File.Exists(pubFile)) continue;
                try
                {
                    using var rsa = LoadRsaFromFile(pubFile);
                    byte[] rawSig = Convert.FromBase64String(entry.sig);
                    if (rsa.VerifyData(data, SHA256.Create(), rawSig))
                        distinctValidSigners.Add(entry.keyId);
                }
                catch { /* this cosigner's verify blew up — count as invalid */ }
            }

            if (distinctValidSigners.Count == 0)            return VerifyResult.BadSignature;
            if (distinctValidSigners.Count <  threshold)    return VerifyResult.BelowThreshold;
            return VerifyResult.Ok;
        }

        /// <summary>k in k-of-N cosigners. Honored only in multi-sig path.</summary>
        public static int ReadThreshold()
        {
            string raw = Environment.GetEnvironmentVariable("NATO_SIGNATURE_THRESHOLD");
            if (int.TryParse(raw, out int n) && n > 0) return n;
            return 1;
        }
    }
}
#endif
