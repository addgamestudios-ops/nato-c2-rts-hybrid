# Grafana dashboard for NATO C2 federation

`federation-l16.json` is an importable Grafana dashboard that visualises
the OTLP metrics emitted by `Link16OtlpExporter` (Unity-side runtime
component). It mirrors the in-game Federation Dashboard so Grafana is
"production view" and the in-game widget is "operator view".

## Quick start (recommended)

A single `docker compose up` brings up OTel Collector + Prometheus +
Grafana with the dashboard auto-provisioned:

```sh
cd Tools/grafana
docker compose up
```

Then:
- Grafana UI:    http://localhost:3000  (admin / admin)
- OTLP ingest:   http://localhost:4318/v1/metrics
- Prometheus:    http://localhost:9090

The dashboard appears under **Dashboards → NATO C2 → NATO C2 — Link 16
Federation**. No manual import / datasource selection needed.

To wipe state and start fresh: `docker compose down -v`.

## Manual install (without Docker)

If you already have an OTel Collector + Prometheus + Grafana running
locally, skip the compose stack and just import the dashboard:

1. Grafana → Dashboards → New → Import → upload
   `federation-l16.json`.
2. When prompted for "datasource", pick your Prometheus instance.
3. The dashboard auto-refreshes every 10 s and defaults to a 15-min
   window.

The OTel Collector needs a config that accepts OTLP/HTTP on `:4318`
and exports to Prometheus on `:8889` — see
`provisioning/otel-config.yaml` for the canonical example used by the
compose stack.

## Panels

| Panel                         | Source metric(s)                                              |
|-------------------------------|---------------------------------------------------------------|
| ARQ sent / acked / failed     | `nato_c2_l16_arq_sent` / `_acked` / `_failed`                 |
| Retried / Outstanding         | `nato_c2_l16_arq_retried` / `_outstanding`                    |
| PPLI envelope rate per mode   | `nato_c2_l16_envelopes_{stddp,p2dp,p4sp}_per_sec`             |
| ARQ retries (rate)            | `rate(nato_c2_l16_arq_retried[1m])`                           |
| Per-peer state                | `nato_c2_l16_peer_{rx_count,srej_rx,gaps}` joined by `peer`   |
| Advisor decisions             | `rate(nato_c2_l16_advisor_{demotions,promotions}[5m])`        |
| Federation health (ACK ratio) | `acked / clamp_min(sent, 1)` → red <50%, amber 50-90%, green ≥90% |

## Alerts (suggested)

Suggested alert rules to add in Grafana → Alerting:

- **ACK ratio < 80% for 5m** — federation degraded
- **`rate(nato_c2_l16_arq_failed[5m]) > 1`** — sustained delivery loss
- **`nato_c2_l16_arq_outstanding > 200`** — receive backlog growing

The dashboard description block links directly to
`docs/federation-playbook.md` so on-call can jump from a red panel to
the runbook entry that matches.
