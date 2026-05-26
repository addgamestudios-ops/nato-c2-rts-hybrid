# Milsymbol Integration Guide

[Milsymbol](https://github.com/spatialillusions/milsymbol) is a JavaScript
library that renders true APP-6E / MIL-STD-2525E military symbols as SVG
or canvas. The NATO C2 RTS Hybrid package bridges to it for WebGL builds
and falls back to a built-in placeholder renderer everywhere else.

This guide covers:

1. WebGL plugin authoring
2. Standalone build options
3. SIDC composition rules used by the package
4. Performance considerations

---

## 1. WebGL plugin authoring

### 1.1 Bundle Milsymbol with your WebGL build

Add `milsymbol.js` to your WebGL `index.html`:

```html
<script src="StreamingAssets/milsymbol.min.js"></script>
```

Or, simpler: embed it directly inside the `.jslib` (file size ~250 KB).

### 1.2 Create the `.jslib` plugin

Drop this file in `Assets/Plugins/WebGL/MilsymbolBridge.jslib` (host project,
not the package — `.jslib` files are project-local):

```javascript
mergeInto(LibraryManager.library, {
  MilsymbolRender: function(sidcPtr, optsPtr) {
    var sidc = UTF8ToString(sidcPtr);
    var opts = JSON.parse(UTF8ToString(optsPtr));
    // ms is the global namespace from milsymbol.min.js
    var symbol = new ms.Symbol(sidc, {
      size:              opts.size,
      quantity:          opts.quantity || undefined,
      reinforcedReduced: opts.reinforcedReduced || undefined,
      uniqueDesignation: opts.uniqueDesignation || undefined,
      higherFormation:   opts.higherFormation || undefined,
      echelon:           opts.echelon || undefined,
      colorMode:         "Light",
      monoColor:         false
    });
    // Render to PNG-base64 (strip the data URI prefix the C# side expects).
    var dataUrl = symbol.asCanvas().toDataURL("image/png");
    var base64 = dataUrl.replace(/^data:image\/png;base64,/, "");
    var len = lengthBytesUTF8(base64) + 1;
    var buf = _malloc(len);
    stringToUTF8(base64, buf, len);
    return buf;
  }
});
```

Unity will surface this function to C# automatically; `MilsymbolBridge.cs`
imports it via `[DllImport("__Internal")]`.

### 1.3 Memory: free the returned buffer

Unity's WebGL marshalling copies the returned UTF-8 string into managed
memory before C# sees it, so you do not need to free `buf` manually — but if
you call the bridge thousands of times per second, prefer the pattern shown
in `MilsymbolBridge.cs` of computing a cache key once and reusing the
resulting `Texture2D`.

---

## 2. Standalone build options

`MilsymbolBridge` ships a placeholder renderer that draws the affiliation
frame (rectangle / diamond / square / circle) with echelon amplifiers. For
production standalone builds you have three options, ordered by cost:

| Option | Effort | Fidelity |
| --- | --- | --- |
| Ship placeholder only | None | NATO-styled but generic |
| Pre-render every SIDC you use as PNG + load via Resources | One-off script | Pixel-perfect; static |
| Embed a JS engine (Jint, ClearScript) and run Milsymbol natively | Significant | Full Milsymbol fidelity |

For most pilot deployments option 2 (pre-render) is the right answer:
ground truth SIDCs are known per scenario.

---

## 3. SIDC composition rules

`SIDCFactory.Build(unitType, affiliation, layer, echelon)` produces a 20-char
APP-6E SIDC:

```
Position(s)   Field        Source
1-2           Version      "10"
3             Standard ID  F/H/N/U (from Affiliation)
4-5           Symbol Set   "10" land unit / "01" air / "30" sea
6             Status       "0" present
7             HQ/TF/Dummy  "0"
8-9           Amplifier    echelon (11..25)
10-15         Entity       "000000" placeholder
16-19         Modifier     "0000" placeholder
20            Reserved     "0"
```

Override `Agent.sidc` manually to opt into the full Entity/Modifier matrix.

### Echelon amplifier table

| Code | NATO Echelon | Placeholder Mark |
| ---- | --- | --- |
| 11 | Team / Crew | 1 dot |
| 12 | Squad | 2 dots |
| 13 | Section | 3 dots |
| 14 | Platoon | 1 bar |
| 15 | Company | 2 bars |
| 16 | Battalion | 3 bars |
| 17 | Brigade | 1 X |
| 18 | Division | 2 X |

---

## 4. Performance considerations

- **Cache aggressively.** A symbol with one callsign change is a different
  cache key. The bridge stores `Dictionary<string, Texture2D>` keyed on
  `sidc | affiliation | echelon | quantity | reinforcedDetached | callsign | higherFormation`.
- **Resolution.** `symbolPixelSize = 96` covers most HUD sizes. For a
  battle-map zoom-out view you can dropdown to 48 px without losing
  legibility on the placeholder shapes.
- **Eviction.** The bridge does not currently evict — for a battalion-scale
  scene (~1k unique SIDC×amplifier combinations) this costs ~36 MB of
  textures at 96 px. Add an LRU evictor if you push past that.
