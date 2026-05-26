// =====================================================================
//  NATO C2 RTS Hybrid — OperatorPresenceBroadcaster.cs
//  ---------------------------------------------------------------------
//  Periodically publishes a CoT b-d-c-l event ("Detection / chat / link")
//  carrying THIS operator's current camera-center lat/lon, callsign,
//  role, and station prefix. Other Unity instances on the same TAK
//  Server see these events and render a small "watching here" pin via
//  CotTrackPanel (special-cased for the b-d-c-l type).
//
//  This is how co-op operators see WHERE each peer is looking without
//  any custom netcode — just CoT through the federation.
//
//  Cadence is intentionally low (0.2 Hz = one event every 5 s) — peer
//  presence is ambient information, not real-time, so we don't flood
//  the TAK Server with chatter.
//
//  Stale-out: each event has a 30-second stale time, so if an operator
//  disconnects their pin auto-vanishes from peers' maps within 30 s.
// =====================================================================

using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(140)]
    [AddComponentMenu("NATO C2/Operator Presence Broadcaster")]
    public class OperatorPresenceBroadcaster : MonoBehaviour
    {
        [Header("Cadence")]
        [Tooltip("Hz — how often we publish our camera-center to peers. 0.2 Hz = every 5 s.")]
        [Range(0.05f, 1f)] public float updateHz = 0.2f;
        [Tooltip("Seconds before a presence pin staling on peers' maps.")]
        public float staleSeconds = 30f;

        [Header("Behaviour")]
        [Tooltip("If true, only publish when the TAK adapter is connected. Saves background CPU when offline.")]
        public bool onlyWhenConnected = true;

        // ---------- runtime ------------------------------------------
        private TakServerCotAdapter _tak;
        private OperatorIdentity _ident;
        private float _next;

        private void Awake()
        {
            _tak   = FindAnyObjectByType<TakServerCotAdapter>();
            _ident = OperatorIdentity.Instance ?? FindAnyObjectByType<OperatorIdentity>();
        }

        private void Update()
        {
            if (_tak == null || _ident == null) return;
            if (onlyWhenConnected && !_tak.IsConnected) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / Mathf.Max(0.01f, updateHz);

            // Where is the operator looking? Use camera-center → ground.
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float t)) return;
            Vector3 lookAt = ray.GetPoint(t);

            PublishPresence(lookAt);
        }

        private void PublishPresence(Vector3 world)
        {
            // Build the event by hand here rather than through PublishMarker
            // because b-d-c-l carries a different detail payload (chat-link
            // metadata + operator role) that markers don't include.

            var (lat, lon) = _tak.WorldToLatLonPublic(world);
            float hae = world.y * _tak.metresPerUnit;
            string uid = $"{_ident.CotPrefix()}PRESENCE";  // single stable uid → updates in place
            DateTime now = DateTime.UtcNow;
            string nowS   = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string staleS = now.AddSeconds(staleSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(512);
            sb.Append("<event version=\"2.0\"")
              .Append(" uid=\"").Append(uid).Append("\"")
              .Append(" type=\"b-d-c-l\"")      // bit / detection / chat / link
              .Append(" time=\"").Append(nowS).Append("\"")
              .Append(" start=\"").Append(nowS).Append("\"")
              .Append(" stale=\"").Append(staleS).Append("\"")
              .Append(" how=\"h-g-i-g-o\">");
            sb.Append("<point")
              .Append(" lat=\"").Append(lat.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" lon=\"").Append(lon.ToString("F6", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" hae=\"").Append(hae.ToString("F1", CultureInfo.InvariantCulture)).Append("\"")
              .Append(" ce=\"0\" le=\"0\"/>");
            sb.Append("<detail>");
            sb.Append("<contact callsign=\"").Append(SafeXml(_ident.callsign)).Append("\"/>");
            sb.Append("<__group name=\"Cyan\" role=\"").Append(SafeXml(_ident.role ?? "")).Append("\"/>");
            sb.Append("<operatorPresence")
              .Append(" station=\"").Append(SafeXml(_ident.stationPrefix)).Append("\"")
              .Append(" role=\"").Append(SafeXml(_ident.role)).Append("\"")
              .Append(" watching=\"true\"")
              .Append("/>");
            sb.Append("<remarks>operator watching this location</remarks>");
            sb.Append("</detail>");
            sb.Append("</event>");
            _tak.EnqueueRawEvent(sb.ToString());
        }

        private static string SafeXml(string s)
            => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;") ?? "";
    }
}
