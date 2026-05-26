# NATO C2 RTS Hybrid

A Unity 6 demo that combines StarCraft-style RTS control with NATO C2
standards (APP-6E symbology, MIL-STD-2525E, TAK Server CoT interop) —
the visual / interaction grammar of Anduril Lattice + Project Maven on
top of a real-time tactical simulation.

> **What "C2" actually means** — Command and Control. See
> [`docs/nato-c2-doctrine.md`](docs/nato-c2-doctrine.md) for the
> authoritative definitions, the C3 (Consultation, Command, Control)
> framework, the C4ISR / MDC2 / CJADC2 variations, and how each maps
> onto this codebase. New UX copy and class names should align to that
> reference rather than coining new terms.

## What's in here

| Layer | Tech |
|---|---|
| Realtime sim | Nebukam ORCA collision avoidance + HPA* hierarchical pathfinding + Catmull-Rom path smoothing |
| Map | Real satellite imagery (ESRI World Imagery, OSM, dark Carto, terrain) over real WGS-84 lat/lon |
| Symbology | APP-6E pictograms (tank / drone / infantry / armor / artillery / medic) |
| Interop | TAK Server CoT 2.0 over TCP (8087) + TLS/mTLS (8089), bidirectional |
| HUD | Lattice-style top bar, left-side track feed with EO/IR thumbnails, ACCEPT/DENY HUD for inbound CFF + MEDEVAC, drone PIP video panel with click-to-paint targeting |
| AI | Mythos advisory mode, hostile combat AI (engage / break contact), drone autopilot |

## Quick start

```bash
# 1. Open the Unity project (Unity 6.4 / 6000.4.8f1)
open ~/Desktop/NATO_C2_Local

# 2. In the Editor: Window → Package Manager → "NATO C2 RTS Hybrid"
#    → Samples → "DemoScene (12 Drones + 7 Tanks)" → Import

# 3. Open the SampleScene and press Play
```

The demo bootstrap auto-builds the whole scene at runtime — there's
nothing to wire in the Inspector.

## TAK interop

```bash
# Easiest end-to-end test — run the mock TAK server included in /Tools:
bash ~/Documents/Claude/Projects/Nato/Tools/mock-tak-server.command

# Then enable the toggle in the Inspector on the Bootstrap GameObject:
#   ✓ Connect To Tak Server
#     Tak Host: 127.0.0.1
#     Tak Port: 8087
```

The mock server emits two synthetic foreign tracks every 2 s + a fire /
MEDEVAC request every 20 s so you can see both directions working.

## Production paths to NATO certification

This is a **training / exercise** simulator. It uses canonical CoT 2.0
on the wire, so a TAK-federated ATAK client sees our tracks as live CoT.
Hardening for classified deployment is scoped in:

- `Runtime/Net/TakServerCotAdapter.cs` — TLS 1.2 + mTLS with X.509
  client cert support today. Production swaps the file-based cert load
  for a hardware token (PIV / CAC / YubiKey via PKCS#11).
- STANAG 4774/4778 confidentiality labels in CoT `<detail>` blocks —
  marked `PRODUCTION-TODO` throughout.
- Federation with classified TAK Servers / Maxar tile feeds — the
  basemap loader takes any XYZ URL template.

## Repo layout

```
Runtime/             # Runtime C# (all the simulation + UI + nets)
  Core/              # Agent, NATO_C2_Manager, palette, types
  Simulation/        # ORCA, HPA*, formation, AI, drone autopilot, engagement
  Net/               # FeedHub + adapters (LocalSim, TAK Server, BFT…)
  UI/                # TacticalHUD + Lattice-style panels + radial menu
  AI/                # IntentParser (natural-language → CommandOrder)
Editor/              # Editor menu items (CoT test, validation)
Samples~/            # The auto-bootstrapping demo scene
Tools/               # mock-tak-server, MCP server, reimport script
Documentation~/      # Spec + diagrams
package.json         # Unity Package Manager manifest
```

## Mock TAK server

Pure-stdlib Python — no pip deps. Accepts CoT XML on `:8087`, echoes
events to other clients, emits synthetic foreign tracks + fire / MEDEVAC
requests for HUD testing. Double-click `Tools/mock-tak-server.command`.
