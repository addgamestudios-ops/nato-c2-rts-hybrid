// =====================================================================
//  NATO C2 RTS Hybrid — FeedTypes.cs
//  ---------------------------------------------------------------------
//  Data-transfer types that flow through the FeedHub. Each maps to a
//  real-world tactical data link:
//
//      BftPosition  →  JBC-P / JREAP-C / VMF K-series (blue force position)
//      RadarTrack   →  Link 16 J-series (J3.x track), ASTERIX CAT-048
//      RadioMessage →  Link 16 J28.x free-text + IRC-on-RF + voice metadata
//      VideoFrame   →  STANAG 4609 MISB-compliant H.264/H.265 + KLV metadata
//
//  These are framework-neutral structs — any adapter (MQTT, WebSocket,
//  CoT XML, raw TCP) can produce them. Consumers (UI, AI, persistence)
//  read them off the FeedHub without knowing where they came from.
// =====================================================================

using System;
using UnityEngine;

namespace NATO.C2.Net
{
    /// <summary>Blue Force Tracking position update. One per friendly unit per BFT tick (typically 1-5 Hz).</summary>
    [Serializable]
    public struct BftPosition
    {
        public string unitId;            // stable identifier — usually maps to Agent.callsign
        public DateTime timestampUtc;    // when the GPS fix was taken
        public double latitude;          // WGS-84
        public double longitude;
        public float altitudeMeters;
        public float headingDeg;         // 0..360, true north
        public float speedMs;            // metres / second
        public float healthPct;          // 0..1 from on-board telemetry
        public float ammoPct;
        public string sourceNet;         // e.g. "JBC-P", "MQTT/bft/alpha-1"
        public float confidence;         // 0..1, 1.0 = direct GPS lock
    }

    /// <summary>Radar / ESM track on a non-cooperative contact (hostile, neutral, unknown).</summary>
    [Serializable]
    public struct RadarTrack
    {
        public string trackId;           // sensor-assigned track number, monotonic
        public DateTime timestampUtc;
        public double latitude;
        public double longitude;
        public float altitudeMeters;
        public float courseDeg;          // direction of motion
        public float speedMs;
        public Affiliation affiliation;  // Hostile / Neutral / Unknown
        public UnitType  classifiedType; // best-guess classification
        public float confidence;         // 0..1, lower for ESM/SIGINT, higher for radar+IFF
        public string sourceSensor;      // "AESA-FWD-3", "EW-001", etc.
    }

    /// <summary>One radio chat message on a tactical net (Link 16 free-text or IRC-on-RF).</summary>
    [Serializable]
    public struct RadioMessage
    {
        public string net;               // "TANGO-6", "HQ", "MEDEVAC"
        public DateTime timestampUtc;
        public string fromCallsign;
        public string text;
        public RadioSeverity severity;   // Info / Warning / Critical / System
    }

    public enum RadioSeverity { Info, Warning, Critical, System }

    /// <summary>One video frame from a UAV EO/IR sensor plus its KLV metadata.</summary>
    [Serializable]
    public struct VideoFrame
    {
        public string streamId;          // "UAV-3", "GROUND-CAM-12"
        public DateTime timestampUtc;
        public Texture2D frame;          // null if metadata-only update
        public float gimbalAzimuthDeg;   // sensor pointing direction
        public float gimbalElevationDeg;
        public float horizontalFovDeg;
        public double targetLatitude;    // lat/lon of the centre of the frame
        public double targetLongitude;
        public float platformAltitudeMeters;
        public string sourceUnitId;      // unit that owns the sensor (maps to a BftPosition)
    }

    /// <summary>Cursor-on-Target (CoT) typed event — the lingua franca of ATAK/TAK Server.</summary>
    [Serializable]
    public struct CotEvent
    {
        public string uid;
        public string type;              // e.g. "a-f-G-U-C" (Friend, Ground, Unit, Combat)
        public DateTime start;
        public DateTime stale;
        public double latitude;
        public double longitude;
        public float hae;                // height above ellipsoid (m)
        public string xmlDetail;         // raw <detail> XML for full fidelity
    }
}
