# NATO C2 RTS Hybrid — API Reference

> All public types live in the `NATO.C2` namespace (or `NATO.C2.UI` for HUD).
> Internal helpers (priority queue, SIDC factory) are intentionally `internal`.

## `NATO_C2_Manager`

The orchestrator. One instance per scene.

| Member | Description |
| --- | --- |
| `static Instance` | Active manager. Set in `Awake()`. |
| `Agents` | Read-only list of every registered Agent. |
| `Selected` | Read-only list of the currently-selected friendlies. |
| `OnSelectionChanged` | Fires after `SetSelection` mutates the set. |
| `OnCommandIssued(order, target)` | Fires after the Manager dispatches an order. |
| `OnAutonomousModeChanged(bool)` | Mythos handover toggle. |
| `SetSelection(IEnumerable<Agent>)` | Replaces the current selection. |
| `ClearSelection()` | Convenience for `SetSelection(empty)`. |
| `IssueCommand(CommandOrder, Vector3)` | Routes a Radial-Menu choice into HPA* + formations. |
| `AutonomousMode { get; set; }` | Hand control to Mythos AI. |
| `EnumerateByAffiliation(Affiliation)` | Yields agents matching the filter. |
| `EnumerateByLayer(AltitudeLayer)` | Yields agents in the given altitude bucket. |

### Tick order (`Update`)

```
1.  DrainPendingRegistration()       — additions/removals safe inside Tick
2.  For each fixed step:
     a. Per-Agent: refresh preferredVelocity from path/formation
     b. Mythos AdviseAgents()  (writes evasion vectors if autonomous)
     c. ORCA Solve()  (Burst job; writes currentVelocity)
     d. Integrate: position += currentVelocity * dt
```

---

## `Agent`

Unit wrapper. Identity + motion + visual state.

Key Inspector fields:

- `callsign`, `higherFormation`, `unitType`, `affiliation`, `layer`
- `echelon`, `reinforcedDetached`, `quantity`, `sidc`
- `radius`, `maxSpeed`, `acceleration`
- `neighbourDistance`, `maxNeighbours`, `timeHorizon`, `timeHorizonObstacle`
- `maxHealth`, `health`, `maxAmmo`, `ammo`
- `hullRenderer`, `selectionRing`

Runtime state (read by Manager/ORCA/HPA*):

- `preferredVelocity` — what we want this tick
- `currentVelocity` — what ORCA approved
- `path` / `pathCursor` — waypoint queue
- `formationSlot` — local offset within active formation
- `currentOrder` — last `CommandOrder` issued

Events: `OnDestroyed`, `OnOrderChanged`, `OnDamaged`.

`ResolveSIDC()` lazily fills `sidc` via `SIDCFactory.Build`.

---

## `ORCA`

Nebukam ORCA bridge.

- `Solve(IReadOnlyList<Agent> agents, float dt, bool layerAware)` — called once
  per Manager tick. Maintains internal `Dictionary<Agent, IAgent>` so adding
  or removing units between ticks is cheap.
- `RegisterDynamic(DynamicObstacle)` / `UnregisterDynamic(...)` — used by
  `DynamicObstacle.OnEnable/Disable`.
- `RegisterStatic(IEnumerable<Vector3>)` — register an immutable obstacle
  polygon once at scene load.
- `LayerBitFor(AltitudeLayer)` — utility for callers that need to compose
  custom layer masks.

Tuning fields:

- `dynamicObstacleWeight` — multiplier applied to dynamic obstacle repulsion.
- `minAgentRadius` — hard floor on radius reported to ORCA.
- `drawDebug` — gizmo overlay for neighbour radii and preferred velocity.

---

## `HPAStar`

Hierarchical Pathfinding A*.

- `RequestPath(Vector3 start, Vector3 goal, AltitudeLayer layer, List<Vector3> outPath)`
  — synchronous; appends smoothed waypoints to `outPath`.
- `RebuildAll()` — re-probes the obstacle grid on every layer. Call after
  large terrain mutations (procedural map streaming, demolitions, etc.).
- High-altitude layer is treated as fully traversable.

Tuning:

- `worldSize`, `worldOrigin`, `cellSize`, `clusterSize`
- `obstacleMask`, `probeRadius`
- `smoothingSamples`, `losShortcut`

---

## `FormationController`

- `active` — `FormationType` enum (Wedge/Line/Circle/Column/Free).
- `AssignSlots(agents, worldTarget)` — fills each agent's `formationSlot`.
- `PreviewSlots(count, worldTarget, facing)` — returns slot world positions for the HUD's ghost preview.

Spacing/wedge geometry: `spacing`, `wedgeAngle`.

---

## `AIAutonomousMode`  (Mythos)

- `AdviseAgents(agents, dt)` — invoked every Manager tick. Always rebuilds
  `ThreatField` + `Opportunities`; only mutates preferred velocities when
  `NATO_C2_Manager.Instance.AutonomousMode == true`.
- Tuning: `lookaheadSeconds`, `threatRadius`, `evasionWeight`,
  `opportunityClusterMin`, `opportunityClusterRadius`.

Read-outs (HUD subscribes):

- `ThreatField : List<ThreatBubble>`
- `Opportunities : List<OpportunityCluster>`

---

## `CommandRadialMenu`

- `Open(Vector3 worldTarget)` — show wheel at mouse position; `worldTarget`
  is the resolved target (cursor raycast or sky-plane intersection).
- `Close()` — explicit hide.
- `OnOrderCommitted` — fires on RMB release over a wedge.
- Six orders, always in this clockwise order from top: `Move`, `Attack`,
  `Loiter`, `Swarm`, `RTB`, `Hold`.

---

## `TacticalHUD`

- LMB drag → world-space rectangle pick → `Manager.SetSelection`.
- Shift+drag is additive.
- Ctrl+1..9 stores a control group; 1..9 recalls it.
- RMB anywhere → `CommandRadialMenu.Open(...)`.
- `OnRenderObject()` draws the threat heatmap rings (uses
  `threatMaterial`, an unlit transparent material the host scene provides).

Top-bar labels are auto-populated from `Manager` + `Mythos`:

- Mission
- Selection count
- Threat count (green / yellow / red)
- Mythos status (Advisory / Autonomous)

---

## `MilsymbolBridge`

- `ResolveSymbol(Agent)` — returns a cached `Texture2D` for the agent's
  SIDC + amplifiers. Cache key includes echelon, quantity, callsign, and
  higher formation so editing those refreshes the texture.
- On WebGL the bridge calls `__Internal MilsymbolRender(sidc, optsJson)`.
- Elsewhere it draws a NATO-styled placeholder (frame shape selected by
  affiliation, echelon dots/bars across the top).
