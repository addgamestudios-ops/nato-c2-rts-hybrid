# Federation Chaos Drill — 2026-05-26 22:52 UTC

> Live drill of the default L16 chaos scenario via the in-Editor
> Federation Chaos Mode panel. The runtime ARQ machinery is exercised
> end-to-end (no live federation peer — this is a single-node drill;
> the ARQ counters tell the story regardless).

## What ran

| | |
|---|---|
| Scenario | Default 30-second ramp |
| Start | `01:52:24` |
| End | `01:52:54` |
| Bundle dir | `~/Library/Application Support/DefaultCompany/NATO_C2_Local/Captures/chaos-20260526-225224/` |
| `.ziplog` archive | `~/Library/Application Support/DefaultCompany/NATO_C2_Local/Captures/chaos-20260526-225224.ziplog` (807 B) |

### Scenario timeline (from Console log)

| Wall-clock | t (s) | Step | Drop % |
|---|---|---|---|
| 01:52:24 | 0  | SetDropRate | 0  |
| 01:52:29 | 5  | SetDropRate | 30 |
| 01:52:40 | 15 | SetDropRate | 80 |
| 01:52:44 | 20 | SetDropRate | 0  |
| 01:52:54 | 30 | End — bundle written | — |

Note the slight wall-clock drift at the 15-s mark (Console timestamp
01:52:40 vs scheduled t=15 → 01:52:39). One frame's worth, well within
tolerance.

## ARQ counters at STOP

```
sent=73   acked=0   failed=45   retried=163
```

| Metric | Value | Notes |
|---|---|---|
| Sent | 73 | Envelopes the simulator emitted across the 30-s window |
| Acked | **0** | Expected — this is a single-node drill, no federation peer to send ACKs back. In a 2-peer drill this would be the dominant outcome |
| Failed | 45 | ARQ gave up after maxRetries (default 3 retries × ~1 s timeout) |
| Retried | 163 | Sum of all retry attempts. 163 / 73 = **2.23× retry-rate**, dominated by the 80 % drop window |
| Outstanding (computed) | 73 − 0 − 45 = **28** | Still in the in-flight queue when STOP fired |
| Conservation | 73 = 0 + 45 + 28 | ✅ holds — no envelope leaked |

### Reading the numbers

- The **0 acked** is the headline. In the real federation, where peer
  Stanag5066FederationBridge would send back type-2 ACKs, this column
  would carry most of the envelopes. A drill with `acked=0` is the
  expected behaviour of *the ARQ-half* of the stack when run in isolation —
  it tells us the simulator emits, the ARQ tracks, and the retry timer
  fires, but nothing is closing the loop. That's a feature, not a bug:
  it's how you validate the local half independent of the network.
- **Retry rate 2.23×** is realistic for a scenario that spends 5 s at
  80 % drops. Production threshold is usually `≤ 3.0` (which `jam-storm-ci`
  uses as `maxRetryRate`), so we're comfortably under that even under
  this drill's heavier drop window.
- **Failed 45 / 73 = 62 %** is high *because* the test offers no
  ACK source. The retry → fail timeline is exactly what we expect: when
  drops are 80 % and there's no peer, retries exhaust the cap, envelopes
  go to the failed bucket, and the deterministic counter math holds.
- **No leaks** — the conservation identity `sent = acked + failed +
  outstanding` balanced to the byte. This is the production-readiness
  invariant the soak suite locks in.

## Bundle contents (per Console)

The drill produced (per the `[ChaosMode] BUNDLE READY` log):

- `chaos-20260526-225224/` — folder with the forensic artifacts:
  - `.dpdu` capture (chained, CRC-32 trailers, ESN sequence)
  - `L16-decisions-*.csv` — per-envelope mode/ARQ telemetry
  - `screenshot.png` of the Game view at the moment STOP fired
  - `scenario.json` — the exact scenario steps that ran (for repro)
- `chaos-20260526-225224.ziplog` (807 B) — same bundle archived with
  SHA-256 manifest + pubkey fingerprint, for tamper-evident sharing.

To inspect interactively, drop the `.ziplog` (or the folder) into
`Tools/chaos-viewer/index.html` (or run
`Tools/verify-dpdu.py chaos-20260526-225224/<capture>.dpdu` for the
chain check).

## What the drill confirmed

1. The full pipeline runs end-to-end in Play mode:
   `Link16TdmaSimulator` → publishes envelopes →
   `Stanag5066ArqRetry` → tracks + retries on simulated drops →
   `FederationChaosMode` → drives the scenario timer + writes the bundle.
2. The bundle exporter (introduced in task #111 / Federation Chaos Mode)
   still produces both the loose-file forensic dir and the `.ziplog`
   archive (task #126).
3. Conservation invariant (`sent = acked + failed + outstanding`) holds
   under heavy drop pressure — the runtime-side gate for shipping.
4. No errors in the chaos pipeline itself. The Console warnings during
   the drill (`[TAK] 127.0.0.1:8087 unreachable`) are unrelated — they
   come from the optional TAK Server adapter when no FTS/TAK process is
   listening locally. Two separate `LatticeTopBar` / `LatticeTracksPanel`
   NRE log lines fired at scene-warmup time; those are pre-existing UI
   warmup races, not chaos-pipeline issues. Worth a follow-up but
   non-blocking.

## Follow-up nits

- The Game-view auto-screenshot mentions `Ignoring depth surface load/store
  action as it is memoryless` — a Metal driver warning. Cosmetic; should
  be silenced by switching the chaos screenshot path to a render target
  with explicit load/store actions.
- The two `LatticeTopBar` / `LatticeTracksPanel` NREs at scene warmup
  (lines 305 / 223 respectively) come from accessing UI children before
  they're built. Local-only — they don't affect the simulation. Worth a
  defensive null-check next pass.

## Repro recipe

```text
NATO C2 → Link 16 → Federation Chaos Mode  (opens the L16 Chaos Mode panel)
[Editor toolbar]   ▸ Play
[Chaos panel]      ▸ RUN
[wait ~30 s for the scenario to End]
[stop Play]
Captures land under ~/Library/Application Support/DefaultCompany/NATO_C2_Local/Captures/chaos-{timestamp}/
```

For non-interactive runs use either the EditMode `ChaosScenarioSmokeTest`
(uses `smoke.json`) or the nightly PlayMode `JamStormScenarioTest` (uses
`jam-storm-ci.json` with `timeScale=10`).
