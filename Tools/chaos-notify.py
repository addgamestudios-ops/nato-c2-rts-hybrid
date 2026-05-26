#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — chaos-notify.py
#  ---------------------------------------------------------------------
#  Posts a chaos-bundle summary to a Slack channel via an incoming
#  webhook. Designed to be called at the end of a chaos run so the
#  team sees the outcome without anyone having to open the bundle.
#
#  Webhook URL is read from $SLACK_CHAOS_WEBHOOK_URL.
#
#  Usage:
#      SLACK_CHAOS_WEBHOOK_URL=https://hooks.slack.com/services/... \
#        python3 chaos-notify.py path/to/chaos-{timestamp}/
#
#  The dashboard PNG is NOT uploaded — Slack's incoming webhooks
#  don't support file attachment. The script links to the bundle dir
#  instead; if the team's bundles live somewhere routable (an S3
#  bucket, a shared Drive folder), set CHAOS_BUNDLE_BASE_URL so the
#  posted message includes a clickable link.
# =====================================================================

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path


def read_counters(path: Path) -> dict:
    if not path.exists():
        return {}
    out = {}
    with open(path, "r", encoding="utf-8") as f:
        next(f, None)   # skip header "metric,value"
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split(",", 1)
            if len(parts) == 2:
                out[parts[0]] = parts[1]
    return out


def read_scenario_name(path: Path) -> str:
    if not path.exists():
        return "(no scenario.txt)"
    head = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            head.append(line.rstrip())
            if len(head) >= 6:
                break
    return "\n".join(head)


def health_label(counters: dict) -> str:
    """Quick green/amber/red classification based on retry + fail rate."""
    sent    = int(counters.get("arq_sent", 0))
    failed  = int(counters.get("arq_failed", 0))
    retried = int(counters.get("arq_retried", 0))
    if sent == 0:
        return "⚪ no traffic"
    fail_rate  = failed / sent
    retry_rate = retried / sent
    if fail_rate > 0.10 or retry_rate > 0.50:
        return "🔴 stressed"
    if fail_rate > 0.02 or retry_rate > 0.20:
        return "🟡 degraded"
    return "🟢 nominal"


def build_payload(bundle_dir: Path) -> dict:
    name = bundle_dir.name
    counters_path = bundle_dir / "final-counters.csv"
    scenario_path = bundle_dir / "scenario.txt"
    counters = read_counters(counters_path)
    scenario_head = read_scenario_name(scenario_path)
    status = health_label(counters)

    rows = [
        ("sent",        counters.get("arq_sent",        "—")),
        ("acked",       counters.get("arq_acked",       "—")),
        ("failed",      counters.get("arq_failed",      "—")),
        ("retried",     counters.get("arq_retried",     "—")),
        ("outstanding", counters.get("arq_outstanding", "—")),
    ]
    metrics_text = "\n".join(f"• {k:<11} *{v}*" for k, v in rows)

    # Optional clickable bundle link.
    bundle_url_base = os.environ.get("CHAOS_BUNDLE_BASE_URL", "").rstrip("/")
    link_line = f"<{bundle_url_base}/{name}|open bundle>" if bundle_url_base else f"`{name}`"

    return {
        "blocks": [
            {
                "type": "header",
                "text": {"type": "plain_text", "text": f"Chaos run: {name}"},
            },
            {
                "type": "section",
                "text": {"type": "mrkdwn", "text": f"*Status:* {status}\n{link_line}"},
            },
            {
                "type": "section",
                "fields": [
                    {"type": "mrkdwn", "text": f"*Scenario*\n```{scenario_head}```"},
                    {"type": "mrkdwn", "text": f"*ARQ counters*\n{metrics_text}"},
                ],
            },
        ],
        # Fallback for clients that don't render blocks (mobile/older apps).
        "text": f"Chaos run {name}: {status} — sent={rows[0][1]} retried={rows[3][1]} failed={rows[2][1]}",
    }


def post_to_slack(webhook_url: str, payload: dict):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        webhook_url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=10) as resp:
        if resp.status >= 400:
            raise RuntimeError(f"Slack returned HTTP {resp.status}")


def main():
    if len(sys.argv) != 2:
        print("usage: chaos-notify.py path/to/chaos-bundle-dir", file=sys.stderr)
        sys.exit(2)
    bundle_dir = Path(sys.argv[1])
    if not bundle_dir.is_dir():
        print(f"{bundle_dir}: not a directory", file=sys.stderr)
        sys.exit(1)
    webhook = os.environ.get("SLACK_CHAOS_WEBHOOK_URL")
    if not webhook:
        print("SLACK_CHAOS_WEBHOOK_URL not set — printing payload to stdout instead", file=sys.stderr)
        print(json.dumps(build_payload(bundle_dir), indent=2))
        sys.exit(0)
    try:
        post_to_slack(webhook, build_payload(bundle_dir))
        print(f"posted chaos summary for {bundle_dir.name}")
    except urllib.error.HTTPError as e:
        print(f"slack HTTPError {e.code}: {e.read().decode('utf-8', 'replace')}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"failed to post: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
