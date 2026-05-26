# Pairing ATAK (phone) with FreeTAKServer + the NATO C2 RTS demo

## What you need

- An Android phone with **ATAK-CIV** installed
  ([Play Store](https://play.google.com/store/apps/details?id=com.atakmap.app.civ) — free)
- The phone on the same Wi-Fi network as the Mac running FreeTAKServer
- Docker Desktop running on the Mac
- The NATO C2 RTS demo open in Unity (or the standalone `NATO_C2.app`)

## 5-minute setup

### 1. Start FreeTAKServer

Double-click `Tools/freetak/start-freetak.command` (or `bash start-freetak.sh`).

The script prints your Mac's LAN IP — note it. Looks like `192.168.1.42`.

### 2. Generate a Data Package for the phone

1. Open <http://localhost:5000> in your browser.
2. Sign in: `admin` / `change-me-on-first-login` (change immediately under Settings).
3. **Data Package Generator** → tick **Streaming** (port 8087, TCP, no SSL).
4. Set **Server Address** to your Mac's LAN IP (e.g. `192.168.1.42`).
5. Click **Generate** → downloads a `.zip` Data Package.

### 3. Send the Data Package to the phone

Three ways, pick one:

| Method | How |
|---|---|
| **AirDrop** | The .zip → phone (if phone supports). |
| **USB** | Plug phone in, copy to `Internal storage / atak / tools / datapackage/`. |
| **HTTP** | In FTS-UI, copy the download link, open it in the phone's Chrome. |

### 4. Import in ATAK

1. ATAK menu (☰) → **Import Manager** → **Local SD** → pick the `.zip`.
2. ATAK restarts, hits the server, and you should see a green satellite icon
   top-right (the "TAK Server Connected" indicator).

### 5. Connect Unity → FTS

In Unity, with the demo open:

1. Select **Bootstrap** in the Hierarchy.
2. In the Inspector → **TAK Server (CoT interop)**:
   - **Connect To Tak Server** → ✓
   - **Tak Host** → `127.0.0.1`
   - **Tak Port** → `8087`
3. Press Play.

### 6. Verify both directions

- **Unity → ATAK:** your phone's ATAK map should show ALPHA-7, BRAVO-9,
  SWIFT-1, etc. as live blue (friendly) icons.
- **ATAK → Unity:** drop a CoT pin on your phone (long-press the map →
  pick "Hostile" or "Fire Mission"). Within a second you should see:
  - The Lattice Tracks panel (left side) add a row with the callsign.
  - If you picked "Fire Mission" or "Medevac", the **ACCEPT/DENY HUD
    card** stacks at top-center.

## Troubleshooting

| Symptom | Fix |
|---|---|
| ATAK "Server Unreachable" red dot | Confirm phone + Mac on same Wi-Fi. Try the LAN IP from `ipconfig getifaddr en0` rather than localhost. |
| Unity Console: `unreachable (Connection refused)` | `docker compose ps` — confirm `nato_c2_fts` is `Up`. Otherwise `docker compose up -d`. |
| Phone connects but no units appear | Bootstrap.Connect To Tak Server is off, or LocalSimFeed origin lat/lon is far from ATAK's map view. Change `basemapLat / basemapLon` to your current location. |
| Map view in ATAK is blank | ATAK needs an offline tile cache (Mil-Std tiles or Mapbox). Free pack: <https://www.atakmaps.com/>. |

## Production hardening

The stack above is `tcp://...:8087` — clear, unauthenticated. For real
deployments switch to port **8089** (TLS + mTLS) and:

1. In FTS-UI → **Settings → Certificate Authority** → generate a root CA.
2. **Data Package Generator** → tick **SSL Connection** → port 8089 →
   FTS bakes a per-client cert into the .zip.
3. In Unity Inspector → **TakServerCotAdapter**:
   - `useTls` → ✓
   - `port` → 8089
   - `clientCertPath` → path to the .p12 from FTS
   - `clientCertPassword` → as set in FTS-UI
   - `pinServerThumbprint` → SHA-256 of the FTS server cert
4. STANAG 4774/4778 confidentiality labels are still a manual step —
   add them to the `<detail>` block in `TakServerCotAdapter.BuildEventXml`.
