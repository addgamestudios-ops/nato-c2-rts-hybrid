# Federation playbook

Operator-facing runbook for the **Link 16 Federation Dashboard** in the
top-right of the Lattice UI. Each row in this document is keyed to a
symptom you can see on screen, with the likely cause, an immediate
action, and an escalation path.

> Where it says **"console"**, open Unity's Console window or, on a
> deployed build, tail `Logs/Player.log` in the build output folder.
> Where it says **"capture"**, look at the latest file in
> `<persistentDataPath>/Captures/`.

---

## 1. Peer dot stays red (>30 s silent)

**What you see**

- A row in the federation dashboard with a 🔴 dot and `LastRX` past 30 s.
- A critical radio message in the chat panel:
  `[FED] S5066-BRIDGE: peer W1 dropped (30s silent)`.

**Likely causes (most common first)**

1. The peer's Unity instance crashed / was force-quit.
2. The peer's TAK Server connection dropped (TLS handshake, cert expiry).
3. Network path between you and the TAK Server is degraded (VPN flap).
4. Their `Stanag5066FederationBridge` was disabled or removed from the
   scene.

**Immediate action**

1. **Look at the peer's UI in chat** — if their `OperatorPresence`
   marker is also stale (>10 s), the issue is on their side, not yours.
2. **Ping the peer** via radio chat (`/say hello W1`). If they reply,
   the federation bridge is broken but the TAK channel is fine →
   collect a capture, restart their `Stanag5066FederationBridge`
   GameObject.
3. **Check TAK Server logs** — FreeTAKServer logs live under
   `Tools/freetak/data/logs/`. Look for `disconnected` events around
   the drop timestamp.

**Escalation**

- If the peer can't be raised in chat either, treat as full network
  loss. Page the comms officer; switch to the radio backup net until
  TAK Server is reachable again.

---

## 2. Sustained high SREJ-RX from one peer

**What you see**

- A peer's `SREJ-RX` column climbs steadily (>10 in the last 30 s).
- ARQ HUD's retry counter ticking up.

**Likely causes**

1. That peer is consistently missing your envelopes — their network
   path has packet loss (Wi-Fi, congested link, RF jamming).
2. Your simulator is publishing faster than the link can drain.

**Immediate action**

1. Open **NATO C2 → Link 16 → Packing Mode Debug**. The advisor
   should already be demoting the saturated terminals — verify
   `STD-DP → P4SP` transitions are happening for the affected agents.
2. If they aren't, force a manual demotion: pick the noisiest 2–3
   terminals and click **P4SP** for each. Sparser packing = longer
   range, fewer drops.
3. If retries keep climbing after the demotion, reduce
   `ppliPerEpoch` on the simulator from 256 to 128.

**Escalation**

- If `arq.TotalFailed` crosses 10% of `arq.TotalTransmitted`, the link
  is unrecoverable for the current mode mix. Switch the affected peer
  to manual MSI (mission-specific intel) until the link clears.

---

## 3. Gap-storm — `Gaps→` jumping for multiple peers

**What you see**

- Several peers' `Gaps→` columns increment together.
- Each new envelope triggers `range-gap-from-…` log entries.

**Likely causes**

1. Your own ESN counter wrapped (uint16 — 65 536 envelopes is ~9 hours
   at typical PPLI cadence). Peers receive ESN 0 and treat it as a
   gap from 65 535 → false-positive.
2. Local simulator was restarted mid-run; the new ARQ instance starts
   at ESN 0 while peers still remember your old high ESN.

**Immediate action**

1. **Confirm** by looking at the dashboard's "L16 PPLI" panel — if
   your envelope rate just dropped and then resumed, you almost
   certainly restarted.
2. The `DetectGapAndMaybeSrej` code treats `dist > 16` as a re-sync
   and ignores it, so the storm should auto-clear within a few
   envelopes. If it doesn't, restart the federation bridge on every
   peer simultaneously (chat: `/restart-bridge`).

**Escalation**

- If the gaps persist >60 s, the bridge state is corrupt. Stop the
  simulator on all operators, agree on a clock-sync moment, and bring
  the federation back up together.

---

## 4. Mode-flap storm in PackingModeDebugWindow

**What you see**

- Agents oscillating between STD-DP / P2DP every 2 s or so.
- The Editor console logs many `[LearnedMode] … → …` lines per second.

**Likely causes**

- `demoteThreshold` and `promoteThreshold` are too close. An agent
  bumped to P2DP immediately sees enough misses to demote back,
  bumped back to STD-DP, and the cycle never settles.

**Immediate action**

1. Open the simulator GameObject's `LearnedModeAdvisor` component in
   the Inspector.
2. Widen the hysteresis: raise `demoteThreshold` from `0.20` to
   `0.30`, lower `promoteThreshold` from `0.02` to `0.005`.
3. Optionally raise `windowSec` from 8 s to 16 s so the rolling
   average is less twitchy.

**Escalation**

- If the storm continues after widening thresholds, disable
  `LearnedModeAdvisor` entirely (uncheck the component) and pick
  modes manually for the run. Open a ticket — the advisor has a real
  bug at this threshold range.

---

## 5. Health alarm fires for ALL peers at once

**What you see**

- Multiple `[FED] S5066-BRIDGE: peer X dropped` messages within ~5 s.
- Every peer row goes 🔴.

**Likely causes**

1. *Your* TAK Server connection dropped — every peer goes silent
   from your perspective simultaneously even though they're fine.
2. The TAK Server itself crashed.
3. Your local clock jumped backward (NTP correction) — all
   `Time.unscaledTime` deltas suddenly exceed `dropOutThresholdSec`.

**Immediate action**

1. Open the chat panel — if you can still send messages and they
   reach you back, the TAK channel is up; the federation bridge or
   your local time is the culprit.
2. Run `date +%s` in a terminal twice 5 s apart. If the delta isn't
   ~5, your clock is unstable.
3. Restart Unity Play mode — that resets all `lastRxTime` baselines.

**Escalation**

- If TAK Server is down, fall back to direct radio comms with
  authority pre-coordinated frequencies. Bring up the backup
  FreeTAKServer container (`Tools/freetak/docker-compose-backup.yml`)
  while the primary recovers.

---

## 6. Chaos-mode artifact for any of the above

Whenever you're investigating something on this list, drop a
**Federation Chaos Mode** run into the ticket — the bundle under
`Captures/chaos-{timestamp}/` contains:

- `capture.dpdu` — every D_PDU you emitted/received, chain-validated.
- `final-counters.csv` — ARQ + per-peer state at run end.
- `dashboard.png` — screenshot of the federation dashboard.
- `scenario.txt` — exact step list you ran.

Convert the capture to PCAP via `Tools/wireshark/dpdu-to-pcap.py`,
open in Wireshark with the `stanag5066.lua` plugin loaded, attach all
files to the ticket. Three minutes of work; saves hours of "what
happened" debugging later.
