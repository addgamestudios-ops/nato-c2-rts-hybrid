// =====================================================================
//  NATO C2 RTS Hybrid — PeerCertProvisioner.cs
//  ---------------------------------------------------------------------
//  One-click trust-store provisioning. Point this Editor window at:
//
//      • a TAK Server Data Package .zip (the bundle FTS-UI generates
//        when you click "Data Package Generator → Generate"),
//      • OR a loose .p12 / .pfx / .cer / .crt file,
//
//  …and it extracts each X.509 certificate, computes the SHA-1
//  thumbprint, and writes a DER copy to:
//
//      Assets/Resources/PeerTrustStore/{THUMBPRINT}.cer
//
//  That's exactly where `CotSigner.LookupPeerCert` looks them up, so
//  the next inbound CoT event signed by that peer verifies without any
//  further wiring.
//
//  Notes:
//      • For .p12 / .pfx files, we DO NOT extract the private key —
//        only the public cert is needed for verification. The private
//        key would never leave the operator's machine even if it did
//        live in the bundle.
//      • Multi-cert .zip (typical FTS data package): all certs are
//        extracted, each gets its own .cer in the trust store.
// =====================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using UnityEditor;
using UnityEngine;

namespace NATO.C2.EditorTools
{
    public class PeerCertProvisioner : EditorWindow
    {
        private string _lastPath = "";
        private string _password = "";
        private readonly List<string> _log = new List<string>();
        private Vector2 _scroll;

        [MenuItem("NATO C2/Peer Cert Provisioner...", priority = 41)]
        public static void Open()
        {
            var w = GetWindow<PeerCertProvisioner>("Peer Cert Provisioner");
            w.minSize = new Vector2(440, 360);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Peer Cert Provisioner", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drop a TAK Data Package .zip here (or pick a loose .p12 / .pfx / .cer / .crt). " +
                "Each public certificate is extracted and saved to " +
                "Assets/Resources/PeerTrustStore/{SHA1_THUMBPRINT}.cer so " +
                "CotSigner can verify inbound STANAG 4778 signatures from that peer.",
                MessageType.Info);

            EditorGUILayout.Space();
            _password = EditorGUILayout.PasswordField("Password (.p12/.pfx only)", _password);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import Data Package .zip…", GUILayout.Height(28)))
                    PickAndImport(filterZip: true);
                if (GUILayout.Button("Import loose cert / p12…", GUILayout.Height(28)))
                    PickAndImport(filterZip: false);
            }

            if (GUILayout.Button("Open trust store folder", GUILayout.Height(22)))
            {
                EnsureTrustStoreFolder();
                EditorUtility.RevealInFinder(TrustStoreAbsolutePath());
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (var line in _log) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        // ---------- file picker + dispatch --------------------------
        private void PickAndImport(bool filterZip)
        {
            string path = filterZip
                ? EditorUtility.OpenFilePanel("Open TAK Data Package", _lastPath, "zip")
                : EditorUtility.OpenFilePanelWithFilters("Open peer cert / p12", _lastPath,
                    new[] { "Certs / keystores", "p12,pfx,cer,crt,der,pem", "All files", "*" });
            if (string.IsNullOrEmpty(path)) return;
            _lastPath = Path.GetDirectoryName(path);
            _log.Clear();
            try { ImportFile(path); AssetDatabase.Refresh(); }
            catch (Exception e) { _log.Add("FATAL: " + e.Message); Debug.LogException(e); }
        }

        // ---------- handlers per file type --------------------------
        private void ImportFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".zip":  ImportZip(path); break;
                case ".p12":
                case ".pfx":  ImportPkcs12(path); break;
                case ".cer":
                case ".crt":
                case ".der":
                case ".pem":  ImportCertFile(path); break;
                default:
                    _log.Add($"Unrecognised extension {ext}. Treating as DER.");
                    ImportCertFile(path); break;
            }
        }

        private void ImportZip(string zipPath)
        {
            EnsureTrustStoreFolder();
            using var archive = ZipFile.OpenRead(zipPath);
            int imported = 0;
            foreach (var entry in archive.Entries)
            {
                string ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (ext != ".p12" && ext != ".pfx" && ext != ".cer" && ext != ".crt" && ext != ".pem" && ext != ".der")
                    continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                imported += ImportBytes(bytes, ext, originPath: entry.FullName);
            }
            _log.Add(imported > 0
                ? $"✅ {imported} cert(s) imported from {Path.GetFileName(zipPath)}"
                : $"⚠️  No certs found inside {Path.GetFileName(zipPath)}");
        }

        private void ImportPkcs12(string path)
        {
            EnsureTrustStoreFolder();
            byte[] bytes = File.ReadAllBytes(path);
            int n = ImportBytes(bytes, ".p12", originPath: path);
            _log.Add(n > 0
                ? $"✅ {n} cert(s) imported from {Path.GetFileName(path)}"
                : $"❌ Could not extract a cert from {Path.GetFileName(path)} (wrong password?)");
        }

        private void ImportCertFile(string path)
        {
            EnsureTrustStoreFolder();
            byte[] bytes = File.ReadAllBytes(path);
            int n = ImportBytes(bytes, Path.GetExtension(path).ToLowerInvariant(), originPath: path);
            _log.Add(n > 0
                ? $"✅ Imported {Path.GetFileName(path)}"
                : $"❌ Could not parse {Path.GetFileName(path)} as a certificate");
        }

        // ---------- core import: bytes → DER on disk ----------------
        private int ImportBytes(byte[] bytes, string ext, string originPath)
        {
            try
            {
                X509Certificate2Collection coll;
                if (ext == ".p12" || ext == ".pfx")
                {
                    coll = new X509Certificate2Collection();
                    coll.Import(bytes, _password ?? "",
                        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                }
                else
                {
                    var cert = new X509Certificate2(bytes);
                    coll = new X509Certificate2Collection(cert);
                }

                int wrote = 0;
                foreach (var c in coll)
                {
                    if (c == null) continue;
                    // Skip self-signed CA roots? — for STANAG 4778 we want
                    // the OPERATOR's leaf cert. We export both anyway; the
                    // signer code only matches by thumbprint so dead certs
                    // never get used.
                    byte[] der = c.Export(X509ContentType.Cert);
                    string thumb = c.Thumbprint.Replace(" ", "").ToUpperInvariant();
                    string outPath = Path.Combine(TrustStoreAbsolutePath(), thumb + ".cer");
                    File.WriteAllBytes(outPath, der);
                    _log.Add($"  • {thumb}  CN={ShortSubject(c.Subject)}  origin={Path.GetFileName(originPath)}");
                    wrote++;
                }
                return wrote;
            }
            catch (Exception e)
            {
                _log.Add($"  ⚠️ {Path.GetFileName(originPath)}: {e.Message}");
                return 0;
            }
        }

        private static string ShortSubject(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "?";
            int i = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return subject;
            int end = subject.IndexOf(',', i);
            return subject.Substring(i + 3, (end < 0 ? subject.Length : end) - (i + 3));
        }

        // ---------- trust-store folder management -------------------
        private const string TrustStoreRelative = "Assets/Resources/PeerTrustStore";

        private static string TrustStoreAbsolutePath()
            => Path.Combine(Path.GetDirectoryName(Application.dataPath), TrustStoreRelative);

        private static void EnsureTrustStoreFolder()
        {
            string p = TrustStoreAbsolutePath();
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
        }
    }
}
#endif
