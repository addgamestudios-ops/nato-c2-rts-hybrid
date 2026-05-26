// =====================================================================
//  NATO C2 RTS Hybrid — TakServerCotAdapter.cs
//  ---------------------------------------------------------------------
//  REAL CoT XML interop with a TAK Server (or any CoT-compatible
//  endpoint like FreeTAKServer, TAK Server 5.x, ATAK direct peer).
//
//  TAK Server canonical ports (all CoT XML over TCP, framed as one XML
//  doc per message, terminated by close tag — no length prefix):
//      8087   plain TCP   ← we default here. Same as FreeTAKServer.
//      8089   TLS-1.2 with mTLS client cert  (production-TODO)
//      8088   "anonymous" TLS without client cert
//
//  Wire format (one CoT event):
//      <?xml version="1.0"?>
//      <event version="2.0" uid="..." type="a-f-G-U-C-A"
//             time="2026-05-25T22:00:00Z"
//             start="2026-05-25T22:00:00Z"
//             stale="2026-05-25T22:00:30Z"
//             how="m-g">
//        <point lat="38.7400" lon="22.2540" hae="100" ce="9999999" le="9999999"/>
//        <detail>
//          <contact callsign="TANGO-6"/>
//          <__group name="Cyan" role="HQ"/>
//          <status battery="100" readiness="true"/>
//          <track course="045" speed="3.0"/>
//          <takv version="0.1.0" platform="NATO-C2-RTS-Hybrid"/>
//        </detail>
//      </event>
//
//  Threading model:
//      • Main thread (Unity Update) calls TakSocket.Enqueue() to send.
//      • Background thread does the TCP write + read.
//      • Inbound parsed CoT events are pushed onto a ConcurrentQueue
//        and fanned out on the main thread via FeedHub.PublishCot.
//
//  Reconnect policy: exponential backoff to a 30s ceiling. If the
//  server is unreachable, we fall back to a no-op state (the
//  SimulatedCotAdapter can still run alongside to keep the demo lively).
//
//  PRODUCTION-TODO for NATO STANAG 4774/4778 deployment:
//      1. Replace TcpClient with SslStream + load X.509 client cert from
//         the user's smart card / PIV / CAC.
//      2. Pin the server cert and validate against the issuing CA.
//      3. Add STANAG 4774 confidentiality labels in the <detail> block
//         (release marking, classification, originator).
//      4. Rate-limit outbound to match TAK Server's documented 100 Hz
//         ceiling per uid.
//      5. Surface server-side ROL / mission packages as Unity events.
//
//  External dependency: NONE. Uses only .NET standard library (System.Net,
//  System.Xml, System.Threading). No Newtonsoft, no protobuf, no Unity
//  HTTP. Works in IL2CPP builds too.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace NATO.C2.Net
{
    [AddComponentMenu("NATO C2/TAK Server CoT Adapter")]
    public class TakServerCotAdapter : MonoBehaviour, ICotAdapter
    {
        [Header("Server")]
        [Tooltip("TAK Server / FreeTAKServer host. Use 127.0.0.1 when running locally.")]
        public string host = "127.0.0.1";
        [Tooltip("CoT TCP port. 8087 = plain TCP, 8088 = TLS without client cert, 8089 = TLS + mTLS.")]
        public int port = 8087;
        [Tooltip("Connection timeout in seconds.")]
        public int connectTimeoutSeconds = 5;
        [Tooltip("If true, keep retrying connection forever with exponential backoff.")]
        public bool autoReconnect = true;

        [Header("TLS / mTLS (production TAK Server)")]
        [Tooltip("Wrap the TCP stream in SslStream. Required for TAK Server ports 8088 (anonymous TLS) and 8089 (mTLS).")]
        public bool useTls = false;
        [Tooltip("Path to a PKCS#12 client certificate (.p12 / .pfx). REQUIRED for mTLS (8089). Leave blank for anonymous TLS (8088). Smart-card / PIV / CAC integration is a production TODO — load the cert from the user's hardware token instead of a file.")]
        public string clientCertPath = "";
        [Tooltip("Password for the PKCS#12 file. PRODUCTION-TODO: replace with macOS Keychain / Windows Credential Manager lookup.")]
        public string clientCertPassword = "";
        [Tooltip("If non-empty, the server certificate's SHA-256 thumbprint must match (hex, no colons). Pins against MITM. Leave blank to accept any cert chained to a trusted CA.")]
        public string pinServerThumbprint = "";
        [Tooltip("If true, accept the server cert unconditionally — DEMO/DEV ONLY. Production must validate against a CA + pinned thumbprint.")]
        public bool insecureAcceptAnyServerCert = false;

        [Header("Outbound")]
        [Tooltip("CoT events per second per friendly unit. ATAK clients normally tick at 1Hz.")]
        [Range(0.2f, 10f)] public float updateHz = 1f;
        [Tooltip("Seconds a CoT event remains 'fresh' on the server before staling.")]
        public float staleSeconds = 30f;
        [Tooltip("Origin lat/lon — world (0,0,0) maps here. Default = LocalSimFeed.")]
        public double originLat = 38.7400;
        public double originLon = 22.2540;
        [Tooltip("Metres per Unity unit. Default = LocalSimFeed.")]
        public float metresPerUnit = 50f;

        [Header("Inbound")]
        [Tooltip("If true, foreign CoT events received from the server are republished on FeedHub.")]
        public bool republishInbound = true;

        // -------------------------------------------------------------
        //  STANAG 4774 confidentiality label config.
        //  ---------------------------------------------------------
        //  Every outbound CoT event embeds a <confidentialityLabel> block
        //  inside <detail> when emitClassificationLabel is true. The label
        //  is structured per the STANAG 4774 schema:
        //
        //      <confidentialityLabel xmlns="urn:nato:stanag:4774:bindinginformation:1:0">
        //        <originator>
        //          <ownerCountry>NLD</ownerCountry>
        //          <ownerOrg>NATO/JFC NAPLES</ownerOrg>
        //        </originator>
        //        <confidentialityInformation>
        //          <policyIdentifier>NATO</policyIdentifier>
        //          <classification>UNCLASSIFIED</classification>
        //          <category type="permissive">REL TO USA,GBR,CAN,AUS,NZL</category>
        //        </confidentialityInformation>
        //        <created>2026-05-26T11:00:00Z</created>
        //      </confidentialityLabel>
        //
        //  STANAG 4778 is the binding-information envelope that ties the
        //  label cryptographically to the payload. A production deployment
        //  signs the CoT event hash with the originator's smart-card key
        //  and includes the signature here. We leave a TODO marker — wiring
        //  PKCS#7 signing needs the same X.509 cert load path the TLS code
        //  uses, plus a Bouncy Castle / OpenSSL invocation.
        // -------------------------------------------------------------
        [Header("STANAG 4774 confidentiality label")]
        [Tooltip("Emit a <confidentialityLabel> block in every outbound CoT event. Required for NATO classified deployment; safe to leave on for exercises (defaults to UNCLASSIFIED).")]
        public bool emitClassificationLabel = true;
        public enum Classification { UNCLASSIFIED, RESTRICTED, NATO_RESTRICTED, CONFIDENTIAL, NATO_CONFIDENTIAL, SECRET, NATO_SECRET, COSMIC_TOP_SECRET }
        public Classification classification = Classification.UNCLASSIFIED;
        [Tooltip("ISO 3166-1 alpha-3 country code of the originator (e.g. USA, GBR, NLD, FRA, DEU).")]
        public string originatorCountry = "USA";
        [Tooltip("Originating organization. Free text.")]
        public string originatorOrg = "USMC / III MEF";
        [Tooltip("Policy authority. \"NATO\" for NATO traffic, \"US-DOD\" for US national, etc.")]
        public string policyIdentifier = "NATO";
        [Tooltip("Releasability caveat. ATPN syntax. Example: \"REL TO USA, GBR, CAN, AUS, NZL\" (Five Eyes).")]
        public string releasabilityCaveat = "REL TO USA, GBR, CAN, AUS, NZL";
        [Tooltip("Optional ACCM (Alternative Compensatory Control Measures) caveat. Leave empty if none.")]
        public string accmCaveat = "";
        [Tooltip("STANAG 4778 binding signature — PRODUCTION-TODO. We currently emit a placeholder hash; real deployments sign with the operator's PIV/CAC key.")]
        public bool emitBindingPlaceholder = true;

        // ---------- runtime state -------------------------------------
        private FeedHub _hub;
        private TcpClient _tcp;
        private Stream _stream; // NetworkStream OR SslStream — both inherit System.IO.Stream.
        private Thread _readThread;
        private Thread _connectThread;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueueShim<string> _outQ = new ConcurrentQueueShim<string>();
        private readonly ConcurrentQueueShim<CotEvent> _inQ = new ConcurrentQueueShim<CotEvent>();
        private volatile bool _connected;
        private float _nextTick;
        private int _reconnectBackoffMs = 1000;

        public bool IsConnected => _connected;

        private void Awake() { _hub = FeedHub.Instance; }
        public void Open(FeedHub hub) { _hub = hub; Connect(); }
        public void Close() { Disconnect(); _hub = null; }

        private void OnEnable()  { Connect(); }
        private void OnDisable() { Disconnect(); }

        // ---------- connection lifecycle -------------------------------
        private void Connect()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _connectThread = new Thread(ConnectLoop) { IsBackground = true, Name = "TAK-Connect" };
            _connectThread.Start();
        }

        private void Disconnect()
        {
            _connected = false;
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
            _cts = null;
        }

        // Server cert validation. Three policies, evaluated in order:
        //   1. insecureAcceptAnyServerCert — accept everything (DEMO/DEV).
        //   2. pinServerThumbprint set — must match (SHA-256, hex, no colons).
        //   3. Otherwise — fall back to system CA chain validation.
        private bool ValidateServerCert(object sender, X509Certificate cert,
                                        X509Chain chain, SslPolicyErrors errors)
        {
            if (insecureAcceptAnyServerCert)
            {
                Debug.LogWarning("[TAK] insecureAcceptAnyServerCert=true — accepting any cert (DEV ONLY).");
                return true;
            }
            if (!string.IsNullOrEmpty(pinServerThumbprint))
            {
                using var hash = System.Security.Cryptography.SHA256.Create();
                var raw = cert.GetRawCertData();
                var digest = hash.ComputeHash(raw);
                var sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) sb.Append(digest[i].ToString("X2"));
                string actual = sb.ToString();
                bool ok = string.Equals(actual, pinServerThumbprint.Replace(":", "").Replace(" ", ""),
                                        StringComparison.OrdinalIgnoreCase);
                if (!ok)
                    Debug.LogError($"[TAK] Server cert thumbprint mismatch. expected={pinServerThumbprint} actual={actual}");
                return ok;
            }
            if (errors == SslPolicyErrors.None) return true;
            Debug.LogError($"[TAK] Server cert validation failed: {errors}");
            return false;
        }

        private void ConnectLoop()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var c = new TcpClient();
                    var connectTask = c.ConnectAsync(host, port);
                    if (!connectTask.Wait(connectTimeoutSeconds * 1000, ct))
                        throw new TimeoutException("connect timeout");
                    _tcp = c;

                    // Pick raw network stream or wrap it in an SslStream for
                    // production TAK Server (TLS / mTLS). The rest of the read+
                    // write loops talk to _stream — they don't care which one
                    // it is.
                    if (useTls)
                    {
                        var raw = c.GetStream();
                        var ssl = new SslStream(raw, leaveInnerStreamOpen: false,
                                                userCertificateValidationCallback: ValidateServerCert,
                                                userCertificateSelectionCallback: null);
                        var clientCerts = new X509CertificateCollection();
                        if (!string.IsNullOrEmpty(clientCertPath))
                        {
                            // PRODUCTION-TODO: load from hardware token (PIV/CAC/YubiKey
                            // PKCS#11 module) instead of a password-protected file.
                            var cert = new X509Certificate2(clientCertPath, clientCertPassword,
                                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                            clientCerts.Add(cert);
                            Debug.Log($"[TAK] Loaded client cert subject={cert.Subject} thumbprint={cert.Thumbprint}");
                        }
                        // Unity's .NET runtime supports TLS 1.2 via the legacy overload.
                        // TLS 1.3 isn't exposed in Unity 6's BCL today; revisit if/when
                        // Unity bumps to .NET 8+ — flip EnabledSslProtocols accordingly.
                        ssl.AuthenticateAsClient(host, clientCerts, SslProtocols.Tls12,
                                                 checkCertificateRevocation: false);
                        _stream = ssl;
                        Debug.Log($"[TAK] TLS handshake OK — protocol={ssl.SslProtocol}");
                    }
                    else
                    {
                        _stream = c.GetStream();
                    }

                    _connected = true;
                    _reconnectBackoffMs = 1000;
                    Debug.Log($"[TAK] Connected to {host}:{port}{(useTls ? " (TLS)" : "")}");

                    _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "TAK-Read" };
                    _readThread.Start();

                    // Send-and-wait loop runs on this thread.
                    WriteLoop(ct);
                }
                catch (Exception e)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogWarning($"[TAK] {host}:{port} unreachable ({e.GetType().Name}: {e.Message}). " +
                                         $"Retry in {_reconnectBackoffMs/1000f:F1}s.");
                }
                finally
                {
                    _connected = false;
                    try { _stream?.Close(); } catch { }
                    try { _tcp?.Close(); } catch { }
                    _stream = null;
                    _tcp = null;
                }

                if (!autoReconnect || ct.IsCancellationRequested) break;
                try { Thread.Sleep(_reconnectBackoffMs); } catch { }
                _reconnectBackoffMs = Mathf.Min(_reconnectBackoffMs * 2, 30_000);
            }
        }

        private void WriteLoop(CancellationToken ct)
        {
            var enc = new UTF8Encoding(false);
            while (_connected && !ct.IsCancellationRequested)
            {
                while (_outQ.TryDequeue(out var xml))
                {
                    // STANAG 4778 signing — if a CotSigner is in the scene and
                    // configured, swap the placeholder binding block for a
                    // real RSA-2048 signature over the canonical payload.
                    if (CotSigner.Instance != null && CotSigner.Instance.IsLoaded)
                    {
                        try { xml = CotSigner.Instance.SignEvent(xml); }
                        catch (Exception e) { Debug.LogWarning($"[TAK] Sign skipped: {e.Message}"); }
                    }
                    var bytes = enc.GetBytes(xml);
                    _stream.Write(bytes, 0, bytes.Length);
                }
                _stream.Flush();
                try { Thread.Sleep(20); } catch { break; }
            }
        }

        // ---------- inbound parsing -----------------------------------
        private void ReadLoop()
        {
            // TAK / FreeTAKServer streams events back-to-back with their own
            // <?xml?> declarations. XmlReader can't handle multiple
            // declarations even in Fragment mode, so we do the framing
            // ourselves: buffer raw bytes, hunt for "<event " ... "</event>"
            // boundaries, parse each chunk with XmlDocument.
            var buf = new StringBuilder(8192);
            var read = new byte[8192];
            try
            {
                while (_connected)
                {
                    int n = _stream.Read(read, 0, read.Length);
                    if (n <= 0) break;
                    string text = Encoding.UTF8.GetString(read, 0, n);
                    // Strip any inline <?xml ...?> declarations — XmlDocument
                    // refuses chunks where they appear mid-document.
                    int decl;
                    while ((decl = text.IndexOf("<?xml", StringComparison.Ordinal)) >= 0)
                    {
                        int end = text.IndexOf("?>", decl, StringComparison.Ordinal);
                        if (end < 0) break;
                        text = text.Remove(decl, end - decl + 2);
                    }
                    buf.Append(text);

                    while (true)
                    {
                        string chunk = ExtractNextEventChunk(buf);
                        if (chunk == null) break;
                        // Belt and suspenders: nuke any straggling declarations
                        // inside the chunk and trim whitespace before parse.
                        chunk = System.Text.RegularExpressions.Regex.Replace(
                            chunk, @"<\?xml[^?]*\?>", "").Trim();
                        if (chunk.Length == 0) continue;
                        try
                        {
                            var doc = new System.Xml.XmlDocument();
                            doc.LoadXml(chunk);
                            // STANAG 4778 signature verification — only when
                            // explicitly required by the operator's policy. The
                            // signer's trust store is loaded lazily on first hit.
                            if (CotSigner.Instance != null && CotSigner.Instance.requireSignatures)
                            {
                                var verdict = CotSigner.Instance.VerifyEvent(chunk);
                                if (verdict != CotSigner.VerifyResult.Ok)
                                {
                                    // Drop the event but log so the operator can
                                    // diagnose trust-store gaps.
                                    Debug.LogWarning($"[TAK] Inbound CoT REJECTED — {verdict}. " +
                                                     $"uid={(System.Text.RegularExpressions.Regex.Match(chunk, @"uid=\""([^\""]+)\""").Groups[1].Value)}");
                                    continue;
                                }
                            }
                            var evt = ReadEvent(doc.DocumentElement);
                            _inQ.Enqueue(evt);
                        }
                        catch (Exception ex)
                        {
                            // Drop the bad event but keep the connection alive.
                            Debug.LogWarning($"[TAK] Drop malformed event ({ex.Message.Substring(0, Mathf.Min(80, ex.Message.Length))}): " +
                                             chunk.Substring(0, Mathf.Min(80, chunk.Length)) + "…");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_connected) Debug.LogWarning($"[TAK] Read loop ended: {e.Message}");
            }
            finally
            {
                _connected = false;
            }
        }

        // Pull a complete <event …>…</event> (or self-closing) chunk out of
        // the buffer and remove it. Returns null when no complete chunk yet.
        private static string ExtractNextEventChunk(StringBuilder buf)
        {
            // Find next "<event"
            string s = buf.ToString();
            int start = s.IndexOf("<event", StringComparison.Ordinal);
            if (start < 0)
            {
                // Discard anything before a possible future <event so the
                // buffer doesn't grow unboundedly when we drop noise.
                if (s.Length > 4096) buf.Clear();
                return null;
            }
            // Look for either "</event>" (paired) or "/>" before "<event" recurs (self-closing).
            int closeTag = s.IndexOf("</event>", start, StringComparison.Ordinal);
            // Detect self-closing root form: <event ... />  (rare for CoT, but valid).
            int firstClose = s.IndexOf('>', start);
            bool selfClosing = firstClose > 0 && s[firstClose - 1] == '/';

            int end;
            if (closeTag > 0)
                end = closeTag + "</event>".Length;
            else if (selfClosing)
                end = firstClose + 1;
            else
                return null; // not yet a complete event

            string chunk = s.Substring(start, end - start);
            // Drop everything up to and including this event from the buffer.
            buf.Remove(0, end);
            return chunk;
        }

        private static CotEvent ReadEvent(System.Xml.XmlElement root)
        {
            var ev = new CotEvent();
            ev.uid   = root.GetAttribute("uid")  ?? "";
            ev.type  = root.GetAttribute("type") ?? "";
            ev.start = ParseTime(root.GetAttribute("start"));
            if (ev.start == default) ev.start = ParseTime(root.GetAttribute("time"));
            ev.stale = ParseTime(root.GetAttribute("stale"));

            var pt = root.SelectSingleNode("point") as System.Xml.XmlElement;
            if (pt != null)
            {
                ev.latitude  = ParseDouble(pt.GetAttribute("lat"));
                ev.longitude = ParseDouble(pt.GetAttribute("lon"));
                ev.hae       = (float)ParseDouble(pt.GetAttribute("hae"));
            }
            var detail = root.SelectSingleNode("detail") as System.Xml.XmlElement;
            ev.xmlDetail = detail != null ? detail.OuterXml : "";
            return ev;
        }

        private static DateTime ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.UtcNow;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var t) ? t : DateTime.UtcNow;
        }
        private static double ParseDouble(string s)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
        }

        // ---------- Unity main thread ---------------------------------
        private void Update()
        {
            // Drain inbound queue → FeedHub on main thread.
            while (republishInbound && _inQ.TryDequeue(out var evt))
                _hub?.PublishCot(evt);

            // Periodic outbound publish.
            if (!_connected || NATO_C2_Manager.Instance == null) return;
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 1f / Mathf.Max(0.1f, updateHz);

            var mgr = NATO_C2_Manager.Instance;
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                _outQ.Enqueue(BuildEventXml(a));
            }
        }

        public void Send(CotEvent evt)
        {
            // Manual one-off send. The struct's xmlDetail must already be a
            // full <detail>…</detail> block. We wrap it in the <event>.
            _outQ.Enqueue(BuildEventXml(evt));
        }

        // =================================================================
        //  Typed mission events — these are what real C2 operators care
        //  about. Every call carries a WGS-84 lat/lon so ATAK / SitaWare /
        //  any TAK-federated client renders the pin on its own map.
        // =================================================================

        /// <summary>
        /// Publish a Call-For-Fire request at the given world target.
        /// CoT type "b-r-f-h-c" — bit/request/fire/hostile/contact. ATAK shows
        /// this as a red call-for-fire pin with the requester's callsign.
        /// </summary>
        public void PublishCallForFire(Vector3 worldTarget, string requester, string remarks = null)
        {
            var (lat, lon) = WorldToLatLon(worldTarget);
            float hae = worldTarget.y * metresPerUnit;
            string uid = $"{OpPrefix()}CFF-{System.Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()}";

            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(300) // stale after 5 minutes — fire requests are time-critical
                               .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(384);
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"b-r-f-h-c\"")
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"h-g-i-g-o\">"); // human-derived, ground-truth-observed
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"10\" le=\"10\"/>"); // 10 m circular error
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"CFF-").Append(requester ?? "OPS").Append("\"/>");
            sb.Append("<__group name=\"Cyan\" role=\"FO\"/>");           // forward observer
            sb.Append("<fire_mission requester=\"").Append(requester ?? "OPS").Append("\"/>");
            if (!string.IsNullOrEmpty(remarks))
                sb.Append("<remarks>").Append(SafeXml(remarks)).Append("</remarks>");
            sb.Append("<takv version=\"0.1.0\" platform=\"NATO-C2-RTS-Hybrid\"/>");
            AppendSecurityLabel(sb);
            sb.Append("</detail>");
            sb.Append("</event>");
            _outQ.Enqueue(sb.ToString());
        }

        /// <summary>
        /// Publish a 9-line MEDEVAC request at the given world target.
        /// CoT type "b-r-c-m" — bit/request/casualty/medical. Precedence:
        /// A=urgent, B=urgent-surgical, C=priority, D=routine, E=convenience.
        /// </summary>
        public void PublishMedevac(Vector3 worldTarget, string requester, string patientCallsign,
                                   char precedence = 'C', int patientsLitter = 0, int patientsAmbulatory = 0,
                                   string remarks = null)
        {
            var (lat, lon) = WorldToLatLon(worldTarget);
            float hae = worldTarget.y * metresPerUnit;
            string uid = $"{OpPrefix()}MEDEVAC-{System.Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()}";

            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(900) // stale after 15 minutes
                               .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(512);
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"b-r-c-m\"")
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"h-g-i-g-o\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"10\" le=\"10\"/>");
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"MEDEVAC-").Append(requester ?? "OPS").Append("\"/>");
            sb.Append("<__group name=\"Cyan\" role=\"MEDIC\"/>");
            // 9-line MEDEVAC fields encoded as sub-elements (ATAK Medevac plugin reads these):
            sb.Append("<medevac")
              .Append(" precedence=\"").Append(precedence).Append("\"")
              .Append(" zone_prot_marking=\"smoke\"")
              .Append(" terrain=\"open\"")
              .Append(" nbc_contamination=\"none\"")
              .Append(" patients_litter=\"").Append(patientsLitter).Append("\"")
              .Append(" patients_ambulatory=\"").Append(patientsAmbulatory).Append("\"")
              .Append(" patient_callsign=\"").Append(SafeXml(patientCallsign ?? "")).Append("\"")
              .Append("/>");
            if (!string.IsNullOrEmpty(remarks))
                sb.Append("<remarks>").Append(SafeXml(remarks)).Append("</remarks>");
            sb.Append("<takv version=\"0.1.0\" platform=\"NATO-C2-RTS-Hybrid\"/>");
            AppendSecurityLabel(sb);
            sb.Append("</detail>");
            sb.Append("</event>");
            _outQ.Enqueue(sb.ToString());
        }

        /// <summary>
        /// Publish a GeoChat-style operator chat message (CoT type b-t-f).
        /// ATAK renders these as chat bubbles tied to the operator's
        /// callsign. Persists via the TAK Server's message history so
        /// peers who join late still see prior traffic.
        /// </summary>
        public void PublishChat(string body, string fromCallsign, string room = "All Chat Rooms")
        {
            if (string.IsNullOrEmpty(body)) return;
            // Anchor the chat to current camera-center so the chat bubble has
            // a geo position (ATAK Connection requirement for b-t-f).
            Vector3 world = Vector3.zero;
            var cam = Camera.main;
            if (cam != null)
            {
                var ray = cam.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
                if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float t))
                    world = ray.GetPoint(t);
            }
            var (lat, lon) = WorldToLatLon(world);
            string uid = $"{OpPrefix()}CHAT-{System.Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()}";
            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(86400) // 24h — chat history sticks around
                              .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(384);
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"b-t-f\"")              // CoT chat / free text
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"h-g-i-g-o\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"0\" ce=\"9999999\" le=\"9999999\"/>");
            sb.Append("<detail>");
            // ATAK GeoChat schema — __chat with id + senderCallsign + chatroom.
            sb.Append("<__chat")
              .Append(" id=\"").Append(uid).Append("\"")
              .Append(" chatroom=\"").Append(SafeXml(room)).Append("\"")
              .Append(" senderCallsign=\"").Append(SafeXml(fromCallsign)).Append("\"")
              .Append(" parent=\"RootContactGroup\">");
            sb.Append("<chatgrp id=\"").Append(SafeXml(room)).Append("\" uid0=\"").Append(SafeXml(fromCallsign)).Append("\"/>");
            sb.Append("</__chat>");
            sb.Append("<remarks source=\"").Append(SafeXml(fromCallsign)).Append("\" time=\"").Append(nowS).Append("\" to=\"")
              .Append(SafeXml(room)).Append("\">").Append(SafeXml(body)).Append("</remarks>");
            sb.Append("<contact callsign=\"").Append(SafeXml(fromCallsign)).Append("\"/>");
            sb.Append("<takv version=\"0.1.0\" platform=\"NATO-C2-RTS-Hybrid\"/>");
            AppendSecurityLabel(sb);
            sb.Append("</detail>");
            sb.Append("</event>");
            _outQ.Enqueue(sb.ToString());
        }

        /// <summary>Publish a generic "point of interest" marker (e.g., extraction LZ, rally point).</summary>
        public void PublishMarker(Vector3 worldTarget, string label, string cotType = "b-m-p-w",
                                  int staleSec = 600)
        {
            var (lat, lon) = WorldToLatLon(worldTarget);
            float hae = worldTarget.y * metresPerUnit;
            string uid = $"{OpPrefix()}MARK-{System.Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()}";

            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(staleSec).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(256);
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"").Append(cotType).Append("\"")
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"h-g-i-g-o\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"10\" le=\"10\"/>");
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"").Append(SafeXml(label ?? "MARK")).Append("\"/>");
            sb.Append("<__group name=\"Cyan\" role=\"\"/>");
            sb.Append("<takv version=\"0.1.0\" platform=\"NATO-C2-RTS-Hybrid\"/>");
            AppendSecurityLabel(sb);
            sb.Append("</detail>");
            sb.Append("</event>");
            _outQ.Enqueue(sb.ToString());
        }

        // ---------- public helpers for sibling components -----------
        /// <summary>Enqueue a hand-built CoT XML event for outbound dispatch.
        /// Used by OperatorPresenceBroadcaster which needs custom detail
        /// blocks that PublishMarker / PublishCallForFire don't cover.</summary>
        public void EnqueueRawEvent(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return;
            _outQ.Enqueue(xml);
        }

        /// <summary>WGS-84 projection helper exposed for peer components
        /// (e.g. the operator-presence broadcaster) that need the same
        /// lat/lon convention as outbound CoT events.</summary>
        public (double lat, double lon) WorldToLatLonPublic(Vector3 world) => WorldToLatLon(world);

        // ---------- per-operator UID namespace -----------------------
        //  Each Unity instance has its own OperatorIdentity (e.g. "W1",
        //  "F3"). UIDs become "NATO-C2-W1-CFF-XXXX" so two operators
        //  pushing to the same TAK Server never collide on the wire.
        private string OpPrefix()
        {
            var ident = OperatorIdentity.Instance;
            if (ident != null && !string.IsNullOrEmpty(ident.stationPrefix))
                return ident.CotPrefix();
            return "NATO-C2-";
        }

        // ---------- STANAG 4774 label helper ------------------------
        //  Appends a <confidentialityLabel> block + optional STANAG 4778
        //  binding placeholder. Inlined into every BuildEventXml path so
        //  every outbound CoT event carries the operator's classification.
        private void AppendSecurityLabel(StringBuilder sb)
        {
            if (!emitClassificationLabel) return;

            // Map enum to STANAG 4774 string. NATO_* prefixes encode the
            // "NATO domain" variant; the schema represents this via the
            // policy identifier on the same line.
            string clsStr = classification switch
            {
                Classification.UNCLASSIFIED         => "UNCLASSIFIED",
                Classification.RESTRICTED           => "RESTRICTED",
                Classification.NATO_RESTRICTED      => "NATO RESTRICTED",
                Classification.CONFIDENTIAL         => "CONFIDENTIAL",
                Classification.NATO_CONFIDENTIAL    => "NATO CONFIDENTIAL",
                Classification.SECRET               => "SECRET",
                Classification.NATO_SECRET          => "NATO SECRET",
                Classification.COSMIC_TOP_SECRET    => "COSMIC TOP SECRET",
                _                                   => "UNCLASSIFIED"
            };
            string nowS = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            sb.Append("<confidentialityLabel xmlns=\"urn:nato:stanag:4774:bindinginformation:1:0\">");
            sb.Append("<originator>");
            sb.Append("<ownerCountry>").Append(SafeXml(originatorCountry)).Append("</ownerCountry>");
            sb.Append("<ownerOrg>").Append(SafeXml(originatorOrg)).Append("</ownerOrg>");
            sb.Append("</originator>");
            sb.Append("<confidentialityInformation>");
            sb.Append("<policyIdentifier>").Append(SafeXml(policyIdentifier)).Append("</policyIdentifier>");
            sb.Append("<classification>").Append(clsStr).Append("</classification>");
            if (!string.IsNullOrEmpty(releasabilityCaveat))
                sb.Append("<category type=\"permissive\">").Append(SafeXml(releasabilityCaveat)).Append("</category>");
            if (!string.IsNullOrEmpty(accmCaveat))
                sb.Append("<category type=\"restrictive\">").Append(SafeXml(accmCaveat)).Append("</category>");
            sb.Append("</confidentialityInformation>");
            sb.Append("<created>").Append(nowS).Append("</created>");
            sb.Append("</confidentialityLabel>");

            // STANAG 4778 binding placeholder.  Real deployments compute a
            // SHA-256 over the canonicalised <event> with the label removed,
            // sign it with the operator's CAC/PIV PKCS#11 key, and embed a
            // <bindingInformation> block here with the signature + cert ref.
            if (emitBindingPlaceholder)
            {
                sb.Append("<bindingInformation xmlns=\"urn:nato:stanag:4778:bindinginformation:1:0\">");
                sb.Append("<status>placeholder</status>");
                sb.Append("<signatureAlgorithm>SHA-256/RSA-2048</signatureAlgorithm>");
                sb.Append("<signatureValue>PRODUCTION-TODO-sign-with-PIV-CAC</signatureValue>");
                sb.Append("</bindingInformation>");
            }
        }

        private static string SafeXml(string s)
        {
            return s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;") ?? "";
        }

        // ---------- XML build -----------------------------------------
        private string BuildEventXml(Agent a)
        {
            var (lat, lon) = WorldToLatLon(a.transform.position);
            float hae = a.transform.position.y * metresPerUnit;
            string type = CotTypeFor(a);
            string uid  = OpPrefix() + a.callsign;

            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(staleSeconds)
                               .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(384);
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"").Append(type).Append("\"")
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"m-g\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"9999999\" le=\"9999999\"/>");
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"").Append(a.callsign).Append("\"/>");
            sb.Append("<__group name=\"")
              .Append(a.affiliation == Affiliation.Friendly ? "Cyan" : "Red")
              .Append("\" role=\"").Append(a.commandingOfficer ?? "").Append("\"/>");
            sb.Append("<status battery=\"100\" readiness=\"true\"/>");
            sb.Append("<track course=\"")
              .Append(HeadingFromVec(a.currentVelocity.sqrMagnitude > 0.01f
                                     ? a.currentVelocity.normalized : a.desiredFacing)
                       .ToString("F0", CultureInfo.InvariantCulture))
              .Append("\" speed=\"")
              .Append(a.currentVelocity.magnitude.ToString("F1", CultureInfo.InvariantCulture))
              .Append("\"/>");
            sb.Append("<takv version=\"0.1.0\" platform=\"NATO-C2-RTS-Hybrid\"/>");
            AppendSecurityLabel(sb);
            sb.Append("</detail>");
            sb.Append("</event>");
            return sb.ToString();
        }

        private string BuildEventXml(CotEvent evt)
        {
            string nowS   = evt.start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = evt.stale.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var sb = new StringBuilder(384);
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(evt.uid).Append("\"")
              .Append(" type=\"").Append(evt.type).Append("\"")
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"m-g\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(evt.latitude.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(evt.longitude.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(evt.hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"9999999\" le=\"9999999\"/>");
            sb.Append(string.IsNullOrEmpty(evt.xmlDetail) ? "<detail/>" : evt.xmlDetail);
            sb.Append("</event>");
            return sb.ToString();
        }

        private static string CotTypeFor(Agent a)
        {
            char ident = a.affiliation switch
            {
                Affiliation.Friendly => 'f',
                Affiliation.Hostile  => 'h',
                Affiliation.Neutral  => 'n',
                _                    => 'u'
            };
            string dim = a.layer == AltitudeLayer.High ? "A"
                       : a.unitType == UnitType.Drone ? "A"
                       : "G";
            string fn  = a.unitType switch
            {
                UnitType.Tank       => "U-C-A",
                UnitType.Infantry   => "U-C-I",
                UnitType.Drone      => "U-A-S-F",
                UnitType.Helicopter => "U-A-V-H",
                _                   => "U-C"
            };
            return $"a-{ident}-{dim}-{fn}";
        }

        // ---------- Web Mercator helpers (match LocalSimFeed) ---------
        private (double lat, double lon) WorldToLatLon(Vector3 worldPos)
        {
            // Plane-Carrée-ish around the origin. Same as LocalSimFeed.
            const double EarthRadiusM = 6_378_137d;
            double dx = worldPos.x * metresPerUnit;
            double dz = worldPos.z * metresPerUnit;
            double lat = originLat + (dz / EarthRadiusM) * (180d / Math.PI);
            double lon = originLon + (dx / EarthRadiusM) * (180d / Math.PI) /
                                     Math.Cos(originLat * Math.PI / 180d);
            return (lat, lon);
        }

        private static float HeadingFromVec(Vector3 v)
        {
            float h = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            return (h + 360f) % 360f;
        }

        // ---------- ConcurrentQueue shim ------------------------------
        // We avoid System.Collections.Concurrent.ConcurrentQueue because
        // some older IL2CPP backends choke on it in stripped builds.
        // A tiny lock-based shim is plenty for our throughput (~50 ev/s).
        private sealed class ConcurrentQueueShim<T>
        {
            private readonly Queue<T> _q = new Queue<T>(64);
            private readonly object _lock = new object();
            public void Enqueue(T item) { lock (_lock) _q.Enqueue(item); }
            public bool TryDequeue(out T item)
            {
                lock (_lock)
                {
                    if (_q.Count == 0) { item = default; return false; }
                    item = _q.Dequeue(); return true;
                }
            }
        }
    }
}
