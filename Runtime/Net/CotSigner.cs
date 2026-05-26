// =====================================================================
//  NATO C2 RTS Hybrid — CotSigner.cs
//  ---------------------------------------------------------------------
//  STANAG 4778 binding-information signer. Computes a SHA-256/RSA-2048
//  signature over the canonicalised CoT event payload using the
//  operator's PIV/CAC X.509 certificate loaded from the platform
//  keychain.
//
//  Lookup paths in order of preference:
//      1. Inspector-set `keychainThumbprint` (most explicit).
//      2. Inspector-set `subjectContains` substring match against
//         X509Certificate2.Subject.
//      3. First cert with a private key in CurrentUser/My (macOS = login
//         keychain). Useful for dev.
//
//  Why the platform keychain (not a .p12 on disk):
//      • The PIV/CAC card exposes the private key via the OS smart-card
//        provider; .NET X509Store(CurrentUser) automatically delegates
//        signing operations to the card's PKCS#11 module.
//      • Operators can't accidentally leak the .p12 because it never
//        leaves the card.
//      • macOS Keychain Access + the SmartCard daemon handle the
//         PIN prompt UX.
//
//  Signed binding block format (per STANAG 4778 §4.3):
//      <bindingInformation xmlns="urn:nato:stanag:4778:bindinginformation:1:0">
//        <status>signed</status>
//        <signatureAlgorithm>SHA-256/RSA-2048</signatureAlgorithm>
//        <signerCertSubject>CN=...,OU=...,O=DoD,C=US</signerCertSubject>
//        <signerCertThumbprint>SHA1 hex</signerCertThumbprint>
//        <signatureValue>base64...</signatureValue>
//        <signedAt>2026-05-26T11:00:00Z</signedAt>
//      </bindingInformation>
//
//  Canonicalisation: we hash the LITERAL bytes of the CoT event with
//  the bindingInformation block STRIPPED — this prevents the signature
//  from signing itself. A production-grade implementation would use
//  full XML C14N (RFC 3076), which is significantly more code and
//  marginally more secure. For interop tests this byte-level approach
//  is sufficient and matches what FreeTAKServer's verification path
//  does with its own placeholder signer.
// =====================================================================

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-250)]
    [AddComponentMenu("NATO C2/CoT Signer (STANAG 4778)")]
    public class CotSigner : MonoBehaviour
    {
        public static CotSigner Instance { get; private set; }

        [Header("Certificate lookup")]
        [Tooltip("Exact SHA-1 thumbprint of the cert in the macOS login keychain (hex, no colons or spaces). Most reliable lookup.")]
        public string keychainThumbprint = "";
        [Tooltip("Substring of the cert Subject CN to match if thumbprint is empty (e.g. \"WATCH-1\" or your operator name).")]
        public string subjectContains = "";

        [Header("Outbound signing")]
        [Tooltip("If true, every outbound CoT event gets a STANAG 4778 signature. If false, fall back to the placeholder block in TakServerCotAdapter.")]
        public bool signOutbound = false;
        [Tooltip("Log loaded cert info on Awake() so you can confirm which cert is being used.")]
        public bool logCertOnAwake = true;

        [Header("Inbound verification")]
        [Tooltip("If true, reject inbound CoT events whose STANAG 4778 signature is missing or invalid.")]
        public bool requireSignatures = false;
        [Tooltip("Path under Resources/ to look for peer .cer/.crt trust-store entries (one DER cert per file, filename = SHA-1 thumbprint).")]
        public string peerTrustStorePath = "PeerTrustStore";

        // ---------- cached state -----------------------------------
        private X509Certificate2 _cert;
        private RSA _rsa;
        private string _thumbprintCache;
        private string _subjectCache;

        public bool IsLoaded => _cert != null && _rsa != null;
        public string LoadedSubject    => _subjectCache;
        public string LoadedThumbprint => _thumbprintCache;

        // ---------- lifecycle --------------------------------------
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            if (signOutbound) LoadCert();
        }

        private void OnDestroy()
        {
            if (_cert != null) _cert.Dispose();
            _cert = null; _rsa = null;
        }

        // ---------- cert loading ------------------------------------
        public bool LoadCert()
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2 hit = null;

                if (!string.IsNullOrEmpty(keychainThumbprint))
                {
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint,
                        keychainThumbprint.Replace(":", "").Replace(" ", ""),
                        validOnly: false);
                    if (found.Count > 0) hit = found[0];
                }
                if (hit == null && !string.IsNullOrEmpty(subjectContains))
                {
                    foreach (var c in store.Certificates)
                    {
                        if (c.Subject != null && c.Subject.IndexOf(subjectContains,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hit = c; break;
                        }
                    }
                }
                if (hit == null)
                {
                    // Last-resort: pick the first cert in the user's store
                    // that has a private key.
                    foreach (var c in store.Certificates)
                    {
                        if (c.HasPrivateKey) { hit = c; break; }
                    }
                }

                if (hit == null)
                {
                    Debug.LogWarning("[CotSigner] No usable cert in CurrentUser/My — STANAG 4778 will fall back to placeholder.");
                    return false;
                }
                if (!hit.HasPrivateKey)
                {
                    Debug.LogWarning($"[CotSigner] Cert {hit.Subject} matched but has no accessible private key.");
                    return false;
                }

                _cert = hit;
                _rsa = hit.GetRSAPrivateKey();
                _subjectCache    = hit.Subject;
                _thumbprintCache = hit.Thumbprint;
                if (logCertOnAwake)
                {
                    Debug.Log($"[CotSigner] Loaded signing cert  subject={_subjectCache}  thumbprint={_thumbprintCache}  validUntil={hit.NotAfter:u}");
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CotSigner] Cert load failed: {e.Message}");
                return false;
            }
        }

        // ---------- signing -----------------------------------------
        /// <summary>
        /// Replace the placeholder bindingInformation block in <paramref name="cotXml"/>
        /// with a real signed one. The signature covers the literal bytes of
        /// the event with the bindingInformation block stripped.
        /// </summary>
        public string SignEvent(string cotXml)
        {
            if (!signOutbound || _rsa == null || string.IsNullOrEmpty(cotXml))
                return cotXml;

            // 1) Strip ANY existing bindingInformation block so the digest is
            //    computed over a canonical, unsigned form.
            string stripped = Regex.Replace(cotXml,
                @"<bindingInformation\b[^>]*>.*?</bindingInformation>", "",
                RegexOptions.Singleline);

            // 2) Hash + sign.
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stripped));
            byte[] sig = _rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string sigB64 = Convert.ToBase64String(sig);

            // 3) Build the signed binding block.
            string nowS = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder(384);
            sb.Append("<bindingInformation xmlns=\"urn:nato:stanag:4778:bindinginformation:1:0\">");
            sb.Append("<status>signed</status>");
            sb.Append("<signatureAlgorithm>SHA-256/RSA-").Append(_rsa.KeySize).Append("</signatureAlgorithm>");
            sb.Append("<signerCertSubject>").Append(XmlEscape(_subjectCache ?? "")).Append("</signerCertSubject>");
            sb.Append("<signerCertThumbprint>").Append(_thumbprintCache ?? "").Append("</signerCertThumbprint>");
            sb.Append("<signatureValue>").Append(sigB64).Append("</signatureValue>");
            sb.Append("<signedAt>").Append(nowS).Append("</signedAt>");
            sb.Append("</bindingInformation>");
            string newBinding = sb.ToString();

            // 4) Insert it just before </detail>. If we couldn't find that
            //    tag, append at the end — better to ship a malformed event
            //    than to drop signing entirely.
            int idx = stripped.LastIndexOf("</detail>", StringComparison.Ordinal);
            if (idx < 0) return stripped + newBinding;
            return stripped.Substring(0, idx) + newBinding + stripped.Substring(idx);
        }

        private static string XmlEscape(string s)
            => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;") ?? "";

        // ---------- inbound verification ----------------------------
        public enum VerifyResult { Ok, NoSignature, UnknownSigner, BadSignature, ParseError }

        private readonly System.Collections.Generic.Dictionary<string, X509Certificate2> _peerCertCache
            = new System.Collections.Generic.Dictionary<string, X509Certificate2>(8, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Validate the STANAG 4778 signature on an inbound CoT event.  The
        /// signing cert is matched against the peer trust store loaded from
        /// <c>Resources/{peerTrustStorePath}/{thumbprint}.cer</c>.
        /// Production swaps the file-based trust store for an OCSP-checked
        /// X.509 chain build against a CA — same VerifyResult enum, just
        /// different lookup.
        /// </summary>
        public VerifyResult VerifyEvent(string cotXml)
        {
            if (string.IsNullOrEmpty(cotXml)) return VerifyResult.ParseError;

            // Pull out the binding block.
            var bindMatch = Regex.Match(cotXml,
                @"<bindingInformation\b[^>]*>(?<inner>.*?)</bindingInformation>",
                RegexOptions.Singleline);
            if (!bindMatch.Success) return VerifyResult.NoSignature;
            string inner = bindMatch.Groups["inner"].Value;

            // Reject the placeholder block — that means the sender hasn't
            // actually signed anything yet.
            if (inner.IndexOf("<status>placeholder</status>", StringComparison.Ordinal) >= 0)
                return VerifyResult.NoSignature;

            string thumbprint = MatchOnce(inner, @"<signerCertThumbprint>(.*?)</signerCertThumbprint>");
            string sigB64     = MatchOnce(inner, @"<signatureValue>(.*?)</signatureValue>");
            if (string.IsNullOrEmpty(thumbprint) || string.IsNullOrEmpty(sigB64))
                return VerifyResult.ParseError;

            // Find the peer cert in the trust store.
            var cert = LookupPeerCert(thumbprint);
            if (cert == null) return VerifyResult.UnknownSigner;

            // Re-derive the canonical bytes the sender signed (strip the
            // whole bindingInformation block to match what SignEvent does).
            string stripped = Regex.Replace(cotXml,
                @"<bindingInformation\b[^>]*>.*?</bindingInformation>", "",
                RegexOptions.Singleline);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stripped));

            byte[] sig;
            try { sig = Convert.FromBase64String(sigB64); }
            catch { return VerifyResult.ParseError; }

            try
            {
                using var rsa = cert.GetRSAPublicKey();
                bool ok = rsa.VerifyHash(hash, sig,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return ok ? VerifyResult.Ok : VerifyResult.BadSignature;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CotSigner] Verify threw: {e.Message}");
                return VerifyResult.BadSignature;
            }
        }

        private X509Certificate2 LookupPeerCert(string thumbprint)
        {
            string norm = thumbprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (_peerCertCache.TryGetValue(norm, out var hit)) return hit;

            // Look under Resources/{peerTrustStorePath}/{norm}
            var ta = Resources.Load<TextAsset>($"{peerTrustStorePath}/{norm}");
            if (ta == null) return null;
            try
            {
                var loaded = new X509Certificate2(ta.bytes);
                // Cache for next inbound event.
                _peerCertCache[norm] = loaded;
                return loaded;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CotSigner] Failed to load peer cert {norm}: {e.Message}");
                return null;
            }
        }

        private static string MatchOnce(string input, string pattern)
        {
            var m = Regex.Match(input, pattern, RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
    }
}
