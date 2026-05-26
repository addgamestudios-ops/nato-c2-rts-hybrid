// =====================================================================
//  NATO C2 RTS Hybrid — SimulatedAdapters.cs
//  ---------------------------------------------------------------------
//  Stand-in implementations of the four big NATO data ingest paths.
//  They emit the EXACT same envelope shape the certified production
//  adapter would emit — just sourced from the local simulation
//  instead of a classified network stack.
//
//  ┌──────────────────────────────────────────────────────────────────┐
//  │ Production-readiness gap (per adapter):                          │
//  │                                                                  │
//  │ SimulatedLink16Adapter                                           │
//  │   PRODUCTION-TODO: Replace with a certified MIDS-LVT or          │
//  │   MIDS-JTRS radio terminal + a J-series codec stack. Requires    │
//  │   NSA Type-1 crypto, a DAA-approved waveform certification,      │
//  │   and JTIDS time-slot allocation from a J3.4 NPG.                │
//  │                                                                  │
//  │ SimulatedJbcpAdapter                                             │
//  │   PRODUCTION-TODO: Replace with the JBC-P client SDK from PEO    │
//  │   C3T, running on a JBC-P V4 hardware terminal with VMF/USMTF    │
//  │   K-series message support. Requires US-only ACAT III IL-clear.  │
//  │                                                                  │
//  │ SimulatedCotAdapter                                              │
//  │   PRODUCTION-TODO: Connect to a TAK Server (TAK 5.x or higher)   │
//  │   over TLS-1.3 with mTLS certs issued by the TAK CA. CoT XML     │
//  │   message format is unclassified, federated and broadly          │
//  │   interoperable — this adapter is the EASIEST path to real-     │
//  │   world interop without classified crypto.                       │
//  │                                                                  │
//  │ SimulatedStanag4609Adapter                                       │
//  │   PRODUCTION-TODO: Demux a real STANAG 4609 / MISB 0601 RTP      │
//  │   stream — use ffmpeg or GStreamer to extract H.264 + KLV.       │
//  │   No classification needed for the codec, but most operational  │
//  │   feeds are SECRET-NOFORN or higher.                            │
//  └──────────────────────────────────────────────────────────────────┘
//
//  These adapters intentionally use the same FeedHub events as the
//  LocalSimFeed, so the rest of the package (UI, AI, persistence)
//  doesn't need to know which adapter produced a message. Swap is
//  literally one-line: remove SimulatedX, add CertifiedX.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NATO.C2.Net
{
    // =====================================================================
    //  Link 16 J-series — friendly + hostile track data over jam-resistant TDL
    // =====================================================================
    [AddComponentMenu("NATO C2/Simulated Link 16 Adapter")]
    public class SimulatedLink16Adapter : MonoBehaviour, IBftAdapter, IRadarAdapter
    {
        [Header("Tuning")]
        [Tooltip("Link 16 update rate per network participant — real systems run 1-12 Hz depending on NPG and Time Slot Allocation.")]
        [Range(0.5f, 12f)] public float updateHz = 3f;

        [Header("Production note")]
        [TextArea] public string productionTodo =
            "PRODUCTION-TODO: Swap for a certified MIDS-LVT/JTRS terminal. " +
            "Crypto: NSA Type-1. Waveform: DAA-approved. Time slots: NPG-assigned by the JICO.";

        private FeedHub _hub;
        private float _next;

        public void Open(FeedHub hub) { _hub = hub; }
        public void Close() { _hub = null; }

        private void Awake()  { _hub = FeedHub.Instance; }
        private void Update()
        {
            if (_hub == null || NATO_C2_Manager.Instance == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / Mathf.Max(0.1f, updateHz);

            var mgr = NATO_C2_Manager.Instance;
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null) continue;
                if (a.affiliation == Affiliation.Friendly)
                {
                    // J2.x Precise Participant Location and Identification (PPLI).
                    _hub.PublishBft(BuildPpliBft(a));
                }
                else
                {
                    // J3.x Surveillance Track.
                    _hub.PublishRadar(BuildJ3RadarTrack(a));
                }
            }
        }

        private BftPosition BuildPpliBft(Agent a)
        {
            return new BftPosition
            {
                unitId         = a.callsign,
                timestampUtc   = DateTime.UtcNow,
                latitude       = 0d, longitude = 0d, altitudeMeters = a.transform.position.y,
                headingDeg     = HeadingFromVec(a.desiredFacing),
                speedMs        = a.currentVelocity.magnitude,
                healthPct      = a.maxHealth > 0 ? a.health / a.maxHealth : 0f,
                ammoPct        = a.maxAmmo   > 0 ? a.ammo   / a.maxAmmo   : 0f,
                sourceNet      = "Link16/J2.2",
                confidence     = 0.95f
            };
        }

        private RadarTrack BuildJ3RadarTrack(Agent a)
        {
            return new RadarTrack
            {
                trackId         = $"J3-{a.GetEntityId().GetHashCode() & 0xFFFF:X4}",
                timestampUtc    = DateTime.UtcNow,
                latitude        = 0d, longitude = 0d,
                altitudeMeters  = a.transform.position.y,
                courseDeg       = HeadingFromVec(a.currentVelocity.sqrMagnitude > 0.01f ? a.currentVelocity.normalized : a.desiredFacing),
                speedMs         = a.currentVelocity.magnitude,
                affiliation     = a.affiliation,
                classifiedType  = a.unitType,
                confidence      = 0.78f,
                sourceSensor    = "Link16/J3.x"
            };
        }

        private static float HeadingFromVec(Vector3 v)
        {
            float h = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }
    }

    // =====================================================================
    //  JBC-P / VMF K-series — Blue Force Tracking over L-band / SATCOM
    // =====================================================================
    [AddComponentMenu("NATO C2/Simulated JBC-P Adapter")]
    public class SimulatedJbcpAdapter : MonoBehaviour, IBftAdapter
    {
        [Range(0.2f, 5f)] public float updateHz = 1f;

        [Header("Production note")]
        [TextArea] public string productionTodo =
            "PRODUCTION-TODO: Replace with the JBC-P V4 client SDK. " +
            "Hardware: JBC-P terminal. Message set: VMF K-series + USMTF. " +
            "Bearer: L-band SATCOM via INMARSAT-BGAN or Iridium Certus. US-only IL2+.";

        private FeedHub _hub;
        private float _next;
        private void Awake() { _hub = FeedHub.Instance; }
        public void Open(FeedHub hub) { _hub = hub; }
        public void Close() { _hub = null; }

        private void Update()
        {
            if (_hub == null || NATO_C2_Manager.Instance == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / Mathf.Max(0.1f, updateHz);

            var mgr = NATO_C2_Manager.Instance;
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null || a.affiliation != Affiliation.Friendly) continue;
                _hub.PublishBft(new BftPosition
                {
                    unitId         = a.callsign,
                    timestampUtc   = DateTime.UtcNow,
                    latitude       = 0d, longitude = 0d,
                    altitudeMeters = a.transform.position.y,
                    headingDeg     = 0f,
                    speedMs        = a.currentVelocity.magnitude,
                    healthPct      = a.maxHealth > 0 ? a.health / a.maxHealth : 0f,
                    ammoPct        = a.maxAmmo   > 0 ? a.ammo   / a.maxAmmo   : 0f,
                    sourceNet      = "JBC-P/VMF-K05.1",
                    confidence     = 0.90f
                });
            }
        }
    }

    // =====================================================================
    //  CoT XML — Cursor-on-Target, ATAK / TAK Server interop
    // =====================================================================
    [AddComponentMenu("NATO C2/Simulated CoT Adapter")]
    public class SimulatedCotAdapter : MonoBehaviour, ICotAdapter
    {
        [Range(0.5f, 5f)] public float updateHz = 1f;

        [Header("Production note")]
        [TextArea] public string productionTodo =
            "PRODUCTION-TODO: Connect to a real TAK Server (5.x) over " +
            "TLS-1.3 with mTLS. CoT XML is UNCLASSIFIED, federated, and " +
            "broadly interoperable. Easiest first step into real-world C2.";

        private FeedHub _hub;
        private float _next;
        private void Awake() { _hub = FeedHub.Instance; }
        public void Open(FeedHub hub) { _hub = hub; }
        public void Close() { _hub = null; }

        private void Update()
        {
            if (_hub == null || NATO_C2_Manager.Instance == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / Mathf.Max(0.1f, updateHz);

            var mgr = NATO_C2_Manager.Instance;
            for (int i = 0; i < mgr.Agents.Count; i++)
            {
                var a = mgr.Agents[i];
                if (a == null) continue;
                _hub.PublishCot(new CotEvent
                {
                    uid       = "uid-" + a.callsign,
                    type      = CotTypeFor(a),
                    start     = DateTime.UtcNow,
                    stale     = DateTime.UtcNow.AddSeconds(30),
                    latitude  = 0d, longitude = 0d,
                    hae       = a.transform.position.y,
                    xmlDetail = BuildDetail(a)
                });
            }
        }

        public void Send(CotEvent evt)
        {
            // PRODUCTION-TODO: serialise to CoT XML and write to the TAK Server socket.
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
            string dim = a.layer == AltitudeLayer.High ? "A"      // air
                       : a.unitType == UnitType.Drone ? "A"
                       : "G";                                       // ground
            string fn  = a.unitType switch
            {
                UnitType.Tank       => "U-C-A",  // unit / combat / armor
                UnitType.Infantry   => "U-C-I",
                UnitType.Drone      => "U-A-S-F",
                UnitType.Helicopter => "U-A-V-H",
                _                   => "U-C"
            };
            return $"a-{ident}-{dim}-{fn}";
        }

        private static string BuildDetail(Agent a)
        {
            var sb = new StringBuilder(192);
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"").Append(a.callsign).Append("\"/>");
            sb.Append("<__group name=\"").Append(a.affiliation == Affiliation.Friendly ? "Cyan" : "Red")
              .Append("\" role=\"").Append(a.commandingOfficer ?? "").Append("\"/>");
            sb.Append("<status battery=\"100\" readiness=\"true\"/>");
            sb.Append("<track speed=\"").Append(a.currentVelocity.magnitude.ToString("0.00"))
              .Append("\" course=\"").Append(0).Append("\"/>");
            sb.Append("</detail>");
            return sb.ToString();
        }
    }

    // =====================================================================
    //  STANAG 4609 / MISB 0601 — MISP-compliant video + KLV metadata
    // =====================================================================
    [AddComponentMenu("NATO C2/Simulated STANAG 4609 Adapter")]
    public class SimulatedStanag4609Adapter : MonoBehaviour, IVideoAdapter
    {
        [Header("Production note")]
        [TextArea] public string productionTodo =
            "PRODUCTION-TODO: Demux a real RTP/UDP MPEG-TS stream via " +
            "ffmpeg or GStreamer. Extract H.264 video + KLV metadata " +
            "tagged with MISB 0601 universal keys (sensor lat/lon/alt, " +
            "platform heading/pitch/roll, FOV).";

        public RenderTexture droneCameraOutput; // optional: a second Unity Camera renders to this RT
        public string sourceUnitId = "UAV-3";

        private FeedHub _hub;
        private float _next;
        private void Awake() { _hub = FeedHub.Instance; }
        public void Open(FeedHub hub) { _hub = hub; }
        public void Close() { _hub = null; }

        private void Update()
        {
            if (_hub == null) return;
            // Publish a metadata-only video heartbeat 4×/s; pip panel reads the latest one.
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            _hub.PublishVideo(new VideoFrame
            {
                streamId             = sourceUnitId,
                timestampUtc         = DateTime.UtcNow,
                frame                = null, // production: BindRenderTexture and pass the latest blit
                gimbalAzimuthDeg     = (Time.time * 12f) % 360f,
                gimbalElevationDeg   = -25f,
                horizontalFovDeg     = 18f,
                targetLatitude       = 38.74d,
                targetLongitude      = 22.25d,
                platformAltitudeMeters = 600f,
                sourceUnitId         = sourceUnitId
            });
        }
    }
}
