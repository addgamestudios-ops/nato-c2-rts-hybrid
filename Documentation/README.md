# NATO C2 RTS Hybrid

**Next-generation Command & Control interface.** Built to replace legacy systems
like SitaWare HQ and NCOP with the responsiveness of a tier-one RTS and the
precision of NATO doctrine.

| Capability | Implementation |
| --- | --- |
| Reciprocal local avoidance | Nebukam ORCA (Job System + Burst), `Runtime/Core/ORCA.cs` |
| Hierarchical global pathfinding | Custom HPA* with Catmull-Rom + LOS string-pull, `Runtime/Core/HPAStar.cs` |
| Formations | Wedge / Line / Circle / Column with live preview, `Runtime/Core/FormationController.cs` |
| Multi-domain operations | Three altitude layers (Ground / Low / High) with layer-aware neighbour queries |
| MIL-STD-2525E / APP-6E symbology | Milsymbol bridge with placeholder fallback, `Runtime/UI/MilsymbolBridge.cs` |
| Autonomous AI | Mythos — 8–12 s threat lookahead + opportunity detection, `Runtime/Simulation/AIAutonomousMode.cs` |
| Drag-box selection + control groups | `Runtime/UI/TacticalHUD.cs` |
| Radial command wheel | `Runtime/UI/CommandRadialMenu.cs` |
| Per-unit APP-6E symbol billboards | `Runtime/UI/UnitSymbolRenderer.cs` |

---

## Installation

This package is delivered for Unity Package Manager (UPM).

1. Open `Window → Package Manager` in Unity 2022.3.20f1 or later.
2. Click **+ → Add package from disk…**, point at this folder's `package.json`.
3. The package declares hard dependencies on:
   - `com.unity.burst`
   - `com.unity.collections`
   - `com.unity.jobs`
   - `com.unity.mathematics`
   - `com.unity.ugui`
   - `com.unity.textmeshpro`
   - `com.nebukam.orca`
4. Install the Nebukam ORCA package from its [registry / git URL](https://github.com/Nebukam/com.nebukam.orca) — UPM will refuse to load `NATO.C2.Runtime.asmdef` until it can resolve `Nebukam.ORCA`.

> **Milsymbol** is not a UPM package. For WebGL builds, ship a `.jslib` plugin
> exposing the global `MilsymbolRender(sidc, optsJson)` function — see
> `Documentation/Milsymbol_Integration_Guide.md`. In Editor and on
> non-WebGL platforms the bridge falls back to a built-in NATO-styled
> placeholder renderer so authoring continues uninterrupted.

---

## Running the demo

Import the sample **DemoScene** from the Package Manager UI, open the included
empty scene, attach `DemoSceneBootstrap` to a GameObject, and press Play.
See `Samples~/DemoScene/README.md` for full controls.

---

## Project layout

```
NATO_C2_RTS_Hybrid/
├── Runtime/
│   ├── Core/
│   │   ├── NATO_C2_Manager.cs         Central orchestrator
│   │   ├── Agent.cs                   Unit wrapper (drone/tank/UGV/heli/infantry)
│   │   ├── ORCA.cs                    Nebukam ORCA bridge
│   │   ├── HPAStar.cs                 HPA* + Catmull-Rom + LOS shortcutting
│   │   └── FormationController.cs     Wedge / Line / Circle / Column / Free
│   ├── Simulation/
│   │   ├── DynamicObstacle.cs         Polygonal moving/static obstacle
│   │   ├── DynamicObstacleSpawner.cs  Demo-only spawner
│   │   └── AIAutonomousMode.cs        Mythos AI (threat + opportunity + evasion)
│   └── UI/
│       ├── TacticalHUD.cs             Drag-box, control groups, HUD bars
│       ├── CommandRadialMenu.cs       6-wedge radial command wheel
│       └── MilsymbolBridge.cs         C#↔JS bridge + placeholder fallback
├── Editor/
│   └── NATO_C2_EditorTools.cs         Menu items + scene validation
├── Samples~/
│   └── DemoScene/                     Runtime bootstrap + README
├── Documentation/
│   ├── README.md
│   ├── API_Reference.md
│   └── Milsymbol_Integration_Guide.md
└── package.json
```

---

## Design principles

1. **Single API surface.** `NATO_C2_Manager.Instance` is the only object the HUD ever talks to. Subsystems (ORCA, HPA*, Mythos) talk to each other through compact arrays the Manager owns. This is what keeps audit-readability high — one read of `NATO_C2_Manager.Update()` tells you the whole tick.
2. **Fixed-step simulation.** The Manager advances ORCA in `simulationStep` slices (default 1/60 s) so Burst jobs are reproducible across machines.
3. **Layer-aware avoidance.** Each Agent has an `AltitudeLayer` (Ground / Low / High). ORCA neighbour queries filter by layer so a helicopter never steers around a tank.
4. **Compose-then-compute.** Formation slots are assigned **before** path requests are issued, so each agent walks its own path to its own slot — no inverse-kinematics on a moving formation centroid.
5. **Renderer-agnostic HUD.** TacticalHUD binds to plain Unity UI by default but exposes the data hooks (`ThreatField`, `Opportunities`, `Selected`) for UI Toolkit or third-party rendering.
6. **Zero cognitive overload.** Every visual element is single-purpose: selection ring = "this is yours", red pulsing circle = "danger here, in N seconds", cyan wedge = "this is the order you're about to give". No nested menus.

---

## Standards compliance

- **MIL-STD-2525E** and **APP-6E** SIDC codes are accepted in `Agent.sidc`. If
  the field is blank, `SIDCFactory.Build` synthesises one from `unitType`,
  `affiliation`, `layer`, and `echelon`.
- Supported amplifiers: **Field B** (Echelon), **Field C** (Quantity),
  **Field D** (Task Force flag — composed into Field M), **Field F**
  (Reinforced/Detached), **Field M** (Higher formation), **Field T** (Callsign).
- Affiliation colours follow NATO C2 visual style:
  Friendly = `#00FF88`, Hostile = `#FF3B3B`, Neutral = `#FFD700`, Unknown = `#EEEEEE`.
- **Cyber symbols** (APP-6E Appendix L) are reachable by setting the SIDC
  manually; the placeholder renderer falls back to a square frame for any
  symbol set it doesn't recognise so unknown SIDCs still render legibly.

---

## License

Proprietary — for NATO pilot deployment evaluation.
