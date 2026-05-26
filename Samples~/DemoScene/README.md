# DemoScene — NATO C2 RTS Hybrid

The sample ships as a runtime bootstrapper instead of a binary `.unity` asset
so it stays diff-friendly and version-control-portable.

## Run the demo

1. Create a new empty scene (`File → New Scene → Basic Built-in`).
2. Create an empty GameObject named `Bootstrap`.
3. Add the `DemoSceneBootstrap` component.
4. Press Play.

You should see:

- 12 friendly drones (low-altitude layer)
- 7 friendly tanks (ground layer)
- 5 hostile tanks (ground layer)
- A spawner cycling 8 dynamic obstacles
- Screen-space HUD canvas with a selection box that follows your drag

## Controls

| Action | Input |
| --- | --- |
| Drag-select friendlies | LMB drag |
| Add to selection | Shift + LMB drag |
| Open Radial Command Wheel | RMB (release on wedge) |
| Assign control group | Ctrl + 1..9 |
| Recall control group | 1..9 |
| Toggle autonomous (Mythos) | bottom-bar toggle |
| Change formation | bottom-bar dropdown |

## Saving the scene

Once the bootstrapper has populated the scene at runtime, you can stop Play
mode and **save the populated hierarchy as `DemoScene.unity`** under
`Samples~/DemoScene/` if you prefer a static scene asset for shipping builds.
