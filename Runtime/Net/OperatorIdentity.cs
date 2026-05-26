// =====================================================================
//  NATO C2 RTS Hybrid — OperatorIdentity.cs
//  ---------------------------------------------------------------------
//  Per-instance operator identity. Used by:
//      • TakServerCotAdapter — namespaces outbound UID prefix so two
//        operators on the same TAK Server can't collide.
//      • IncomingRequestPanel — recognises peer ACK/DENY echoes and
//        dismisses cards globally.
//      • RadioChatPanel — colours messages from the local operator vs
//        peers, like a chat client.
//      • CoT track panel — labels foreign Unity operators' tracks
//        differently from external ATAK clients.
//
//  This is how we get multi-operator co-op WITHOUT adding a netcode
//  dependency (Mirror, Netcode for GameObjects, etc.):  the existing
//  TAK Server federation IS the multiplayer transport. Every operator
//  publishes their state as CoT events; everyone else subscribes via
//  TakServerCotAdapter. The Lattice top bar + radio chat + ACCEPT/DENY
//  HUD already render foreign actors.
//
//  Persisted to PlayerPrefs so the same operator identity survives
//  Play→Stop and editor restarts.
// =====================================================================

using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-300)]
    [AddComponentMenu("NATO C2/Operator Identity")]
    public class OperatorIdentity : MonoBehaviour
    {
        public static OperatorIdentity Instance { get; private set; }

        [Header("Identity")]
        [Tooltip("Short callsign — what other operators see for this station. e.g. WATCH-1, FIRES-3.")]
        public string callsign = "WATCH-1";
        [Tooltip("Operator role — what this station is responsible for. Free text.")]
        public string role = "JTAC";
        [Tooltip("Two- or three-letter station prefix used in CoT UIDs. Keep unique per station on the federation.")]
        public string stationPrefix = "W1";

        [Header("Behaviour")]
        [Tooltip("If true, identity persists to PlayerPrefs across Play sessions.")]
        public bool persist = true;

        private const string KeyCallsign = "NATO_C2_OPERATOR_CALLSIGN";
        private const string KeyRole     = "NATO_C2_OPERATOR_ROLE";
        private const string KeyStation  = "NATO_C2_OPERATOR_STATION";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            if (persist) Load();
        }

        public void Save()
        {
            if (!persist) return;
            PlayerPrefs.SetString(KeyCallsign, callsign ?? "");
            PlayerPrefs.SetString(KeyRole,     role ?? "");
            PlayerPrefs.SetString(KeyStation,  stationPrefix ?? "");
            PlayerPrefs.Save();
        }
        public void Load()
        {
            if (PlayerPrefs.HasKey(KeyCallsign)) callsign      = PlayerPrefs.GetString(KeyCallsign);
            if (PlayerPrefs.HasKey(KeyRole))     role          = PlayerPrefs.GetString(KeyRole);
            if (PlayerPrefs.HasKey(KeyStation))  stationPrefix = PlayerPrefs.GetString(KeyStation);
        }

        /// <summary>Compose a per-operator unique CoT UID prefix. Used by
        /// TakServerCotAdapter so two stations don't collide on the wire.</summary>
        public string CotPrefix() => $"NATO-C2-{stationPrefix}-";
    }
}
