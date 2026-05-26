# Chaos Bundle Viewer

A no-backend, single-page HTML viewer for the bundles emitted by
`FederationChaosMode` (Unity Editor menu **NATO C2 → Link 16 →
Federation Chaos Mode**).

## Usage

1. Double-click `index.html` (or any HTTP server like
   `python3 -m http.server 8000` if your browser blocks local file
   drag-drop).
2. **One bundle**: drag a `chaos-{timestamp}/` folder into the
   **SLOT A** drop zone. The page renders the panels described below.
3. **Compare two bundles**: also drag a second bundle into the
   **SLOT B** drop zone. The view switches to side-by-side mode with
   Δ (B − A) deltas for every counter and a scenario diff table.
   Useful for answering "did this PR make jam-storm better or worse?"
   - Δ values colour-code: red = went up (more retries/fails),
     green = went down, grey = unchanged.
4. The page renders these panels (when present in either bundle):
   - **Scenario** — visual timeline of the chaos script's step events,
     plus the raw `scenario.txt`.
   - **Final Counters** — table of `final-counters.csv` showing ARQ
     totals and per-peer RX / gap / SREJ figures.
   - **Capture (capture.dpdu)** — parses the binary record stream
     in-browser (mirrors the C# codec), summarises:
     - S5CP version, total record count, TX vs RX split
     - Frame counts per D_PDU type (DATA-ONLY / DATA-WITH-ACK / SREJ / NON-ARQ)
   - **Dashboard Screenshot** — embedded PNG.
   - **Telemetry** — summary of `telemetry.csv` with the last 5
     DECISION rows.

## What this is not

- Not a Wireshark replacement — for byte-level inspection use the
  `stanag5066.lua` dissector in `Tools/wireshark/`.
- Not a CRC / chain validator — the embedded parser counts frames
  but doesn't recompute the SHA-256 chain. Use the C# replay test
  (`Stanag5066CaptureReplayTests.Verify_DetectsTamperedFrame`) for
  that.

## Browser support

Tested in Chrome 120+, Safari 17+, Firefox 121+. Uses
`webkitGetAsEntry` for folder drop and standard `Uint8Array` /
`DataView` for binary parsing — no build step, no dependencies.
