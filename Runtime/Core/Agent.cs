// =====================================================================
//  NATO C2 RTS Hybrid — Agent.cs
//  ---------------------------------------------------------------------
//  Universal unit wrapper for any controllable entity: drone, tank,
//  UGV, helicopter, dismounted infantry. Holds the data ORCA/HPA*
//  consume each tick and exposes the visual feedback hooks the HUD
//  binds to (color, symbol, health bar).
// =====================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2
{
    /// <summary>Classification of an Agent's primary platform.</summary>
    public enum UnitType
    {
        Drone,
        Tank,
        UGV,
        Helicopter,
        Infantry,
        Vehicle,
        Ship,
        Artillery,   // Indirect-fire piece — picked first by CFF auto-routing.
        Medic,       // Combat medic / corpsman — picked first by MEDEVAC auto-routing.
        Unknown
    }

    /// <summary>
    /// Layer/Altitude bucket. The simulation keeps a separate neighbour
    /// search tree per layer so a helicopter doesn't burn avoidance
    /// budget steering around a tank, etc.
    /// </summary>
    public enum AltitudeLayer
    {
        Ground = 0,
        Low = 1,
        High = 2
    }

    /// <summary>NATO standard affiliation (APP-6E Identity).</summary>
    public enum Affiliation
    {
        Friendly,
        Hostile,
        Neutral,
        Unknown
    }

    /// <summary>
    /// A movable, selectable, command-receivable entity. The Manager
    /// registers every Agent and reads <see cref="preferredVelocity"/>
    /// each tick, then writes back a velocity that ORCA approved.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("NATO C2/Agent")]
    public class Agent : MonoBehaviour
    {
        // ---------- Identity ---------------------------------------------
        [Header("Identity")]
        [Tooltip("Callsign rendered as APP-6E Field T (e.g. \"ALPHA-1\").")]
        public string callsign = "UNIT";

        [Tooltip("Free-text higher formation tag (APP-6E Field M).")]
        public string higherFormation = "";

        public UnitType unitType = UnitType.Drone;
        public Affiliation affiliation = Affiliation.Friendly;
        public AltitudeLayer layer = AltitudeLayer.Ground;

        [Tooltip("APP-6E echelon code: 11=Team, 12=Squad, 13=Section, 14=Platoon, 15=Company, 16=Battalion, 17=Regiment, 18=Brigade, 19=Division...")]
        [Range(0, 25)] public int echelon = 12;

        [Tooltip("APP-6E Field F: '+' reinforced, '-' detached, '±' reinforced & detached.")]
        public string reinforcedDetached = "";

        [Tooltip("Number of like units represented (APP-6E Field C, quantity amplifier).")]
        public int quantity = 1;

        [Tooltip("MIL-STD-2525E/APP-6E SIDC code (20 chars). If left blank, computed from unitType + affiliation + layer.")]
        public string sidc = "";

        // ---------- Movement / Avoidance --------------------------------
        [Header("Movement")]
        [Min(0.05f)] public float radius = 0.45f;
        [Min(0.1f)]  public float maxSpeed = 8f;
        [Min(0.1f)]  public float acceleration = 16f;
        [Tooltip("How aggressively ORCA neighbours are weighted (higher = more responsive avoidance, costlier).")]
        [Min(0.1f)]  public float neighbourDistance = 6f;
        [Min(1)]     public int   maxNeighbours = 12;
        [Tooltip("Look-ahead window for ORCA agent-agent avoidance.")]
        [Min(0.1f)]  public float timeHorizon = 2.0f;
        [Tooltip("Look-ahead window for ORCA agent-obstacle avoidance.")]
        [Min(0.1f)]  public float timeHorizonObstacle = 1.0f;

        // ---------- Combat / Status -------------------------------------
        [Header("Combat / Status")]
        [Min(0f)] public float maxHealth = 100f;
        [Min(0f)] public float health    = 100f;
        [Min(0f)] public float maxAmmo   = 100f;
        [Min(0f)] public float ammo      = 100f;

        // ---------- Personnel (APP-6E Field W/V/Y/X amplifiers) ---------
        [Header("Personnel")]
        [Tooltip("Rank code of the commanding officer (e.g. \"1LT\", \"CPT\", \"SFC\").")]
        public string rank = "";
        [Tooltip("Full name or short title of the unit commander (e.g. \"1LT MAYER\").")]
        public string commandingOfficer = "";
        [Tooltip("Sensor health flags — bitmask of GPS|Radio|Radar|BFT|EW.")]
        public SensorHealth sensors = SensorHealth.GPS | SensorHealth.Radio | SensorHealth.BFT;

        // ---------- Visual Hooks ----------------------------------------
        [Header("Visual Feedback")]
        [Tooltip("Optional renderer whose material colour is tinted by affiliation.")]
        public Renderer hullRenderer;
        [Tooltip("World-space marker drawn under the agent when selected.")]
        public Transform selectionRing;
        [Tooltip("If true, the unit is currently in a player-controlled selection group.")]
        public bool isSelected;

        [Tooltip("Active control-group number 1-9, or 0 if the unit isn't in a group. Drives the small badge under the symbol.")]
        public int controlGroup = 0;

        // ---------- Runtime State (read by Manager / ORCA / HPA*) -------
        /// <summary>The velocity we WANT this tick (set by Manager from path/formation).</summary>
        [NonSerialized] public Vector3 preferredVelocity;
        /// <summary>The velocity ORCA approved (written back by Manager).</summary>
        [NonSerialized] public Vector3 currentVelocity;
        /// <summary>The waypoint queue HPA* produced for the active order.</summary>
        [NonSerialized] public readonly List<Vector3> path = new List<Vector3>(64);
        /// <summary>Index into <see cref="path"/> of the next corner we're driving toward.</summary>
        [NonSerialized] public int pathCursor;
        /// <summary>Slot offset within the active formation (local to formation origin).</summary>
        [NonSerialized] public Vector3 formationSlot;
        /// <summary>Heading toward the formation slot, used while resolving a move order.</summary>
        [NonSerialized] public Vector3 desiredFacing = Vector3.forward;
        /// <summary>Active high-level order issued by the player or Mythos AI.</summary>
        [NonSerialized] public CommandOrder currentOrder = CommandOrder.Hold;
        /// <summary>Per-Agent integer slot in the Manager's compact arrays.</summary>
        [NonSerialized] public int simIndex = -1;

        // ---------- Events ----------------------------------------------
        public event Action<Agent> OnDestroyed;
        public event Action<Agent, CommandOrder> OnOrderChanged;
        public event Action<Agent, float> OnDamaged;

        // ---------- Unity lifecycle -------------------------------------
        private void OnEnable()
        {
            NATO_C2_Manager.Register(this);
            ApplyAffiliationTint();
            if (selectionRing != null) selectionRing.gameObject.SetActive(false);
        }

        private void OnDisable()
        {
            NATO_C2_Manager.Unregister(this);
            OnDestroyed?.Invoke(this);
        }

        // ---------- Public API ------------------------------------------
        public void IssueOrder(CommandOrder order)
        {
            if (order == currentOrder) return;
            currentOrder = order;
            OnOrderChanged?.Invoke(this, order);
        }

        public void TakeDamage(float dmg)
        {
            if (dmg <= 0f) return;
            health = Mathf.Max(0f, health - dmg);
            OnDamaged?.Invoke(this, dmg);
            if (health <= 0f) Destroy(gameObject);
        }

        public void SetSelected(bool sel)
        {
            isSelected = sel;
            if (selectionRing != null) selectionRing.gameObject.SetActive(sel);
        }

        /// <summary>True if this Agent has more waypoints to consume.</summary>
        public bool HasPath => path.Count > 0 && pathCursor < path.Count;

        /// <summary>Current target waypoint, or the agent's position if the path is exhausted.</summary>
        public Vector3 CurrentWaypoint => HasPath ? path[pathCursor] : transform.position;

        /// <summary>Build (and cache) a default SIDC if the user left the field blank.</summary>
        public string ResolveSIDC()
        {
            if (!string.IsNullOrEmpty(sidc) && sidc.Length >= 10) return sidc;
            sidc = SIDCFactory.Build(unitType, affiliation, layer, echelon);
            return sidc;
        }

        // ---------- Internal helpers ------------------------------------
        private void ApplyAffiliationTint()
        {
            if (hullRenderer == null) return;
            var mat = hullRenderer.material; // instance, intentional
            mat.color = NATOPalette.For(affiliation);
        }
    }

    /// <summary>High-level orders that flow from the Command Radial Menu.</summary>
    public enum CommandOrder
    {
        Hold,
        Move,
        Attack,
        Loiter,
        Swarm,
        RTB
    }

    /// <summary>Sensor health flags for an Agent — populated from BFT/Link 16/SAPIENT feeds in production.</summary>
    [System.Flags]
    public enum SensorHealth
    {
        None  = 0,
        GPS   = 1 << 0,
        Radio = 1 << 1,
        Radar = 1 << 2,
        BFT   = 1 << 3,
        EW    = 1 << 4,
    }

    /// <summary>Canonical NATO colour palette used across HUD + agent tinting.</summary>
    public static class NATOPalette
    {
        public static readonly Color BackgroundBlue = new Color32(0x0A, 0x16, 0x28, 0xFF);
        public static readonly Color AccentCyan     = new Color32(0x00, 0xD4, 0xFF, 0xFF);
        public static readonly Color FriendlyGreen  = new Color32(0x00, 0xFF, 0x88, 0xFF);
        public static readonly Color HostileRed     = new Color32(0xFF, 0x3B, 0x3B, 0xFF);
        public static readonly Color NeutralYellow  = new Color32(0xFF, 0xD7, 0x00, 0xFF);
        public static readonly Color UnknownWhite   = new Color32(0xEE, 0xEE, 0xEE, 0xFF);

        public static Color For(Affiliation a) => a switch
        {
            Affiliation.Friendly => FriendlyGreen,
            Affiliation.Hostile  => HostileRed,
            Affiliation.Neutral  => NeutralYellow,
            _                    => UnknownWhite
        };
    }

    /// <summary>Cheap helper that produces an APP-6E SIDC from semantic inputs.</summary>
    internal static class SIDCFactory
    {
        public static string Build(UnitType u, Affiliation a, AltitudeLayer l, int echelon)
        {
            // SIDC layout (APP-6E, 20 chars):
            // pos 1-2: version    (10)
            // pos 3:   standard ID (F=Friend, H=Hostile, N=Neutral, U=Unknown)
            // pos 4:   symbol set (10=Land Unit, 15=Land Equipment, 01=Air, 30=Sea Surface, 35=Sea Subsurface)
            // pos 5:   status (0 present)
            // pos 6:   HQTF dummy (0)
            // pos 7-8: amplifier  (11..25 echelon)
            // pos 9-10: descriptor / entity (00)
            // pos 11-16: entity   (000000 placeholder)
            // pos 17-20: modifier (0000 placeholder)
            char ident = a switch
            {
                Affiliation.Friendly => 'F',
                Affiliation.Hostile  => 'H',
                Affiliation.Neutral  => 'N',
                _                    => 'U'
            };
            string symbolSet = (u, l) switch
            {
                (UnitType.Drone,      _)                    => "01",
                (UnitType.Helicopter, _)                    => "01",
                (UnitType.Ship,       _)                    => "30",
                (_,                   AltitudeLayer.High)   => "01",
                (_,                   AltitudeLayer.Low)    => "01",
                _                                           => "10"
            };
            string ech = echelon.ToString("00");
            return $"10{ident}{symbolSet}0000{ech}000000000000".Substring(0, 20);
        }
    }
}
