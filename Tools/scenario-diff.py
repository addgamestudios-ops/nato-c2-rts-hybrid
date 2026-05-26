#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — scenario-diff.py
#  ---------------------------------------------------------------------
#  Renders a human-friendly markdown diff between two versions of a
#  chaos scenario JSON. Used by the PR scenario-diff GitHub Action so
#  reviewers see behaviour changes ("step at 5s changed from 30% to
#  50%") instead of raw JSON noise.
#
#  Usage:
#      python3 scenario-diff.py BASE.json HEAD.json [--name display-name]
#  Prints markdown to stdout. Exits 0 on success regardless of diff
#  size (no diff = empty table).
# =====================================================================

import json
import sys
from pathlib import Path


def load(path):
    if path is None or not Path(path).exists():
        return None
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


KIND_NAMES = {
    0: "SetDropRate",
    1: "PeerDrop",
    2: "PeerRestore",
    3: "End",
}


def fmt_step(s):
    """Return a 1-line label for a step dict."""
    kind = s.get("kind")
    kname = KIND_NAMES.get(kind, str(kind))
    if kname == "SetDropRate":
        return f"{kname} {s.get('valueFloat', 0):.0f}%"
    if kname in ("PeerDrop", "PeerRestore"):
        return f"{kname} {s.get('valueText', '')}".rstrip()
    return kname


def key_of(s):
    """Identity for matching across versions — (atSec, kind, value)."""
    return (round(float(s.get("atSec", 0)), 3),
            s.get("kind"),
            s.get("valueFloat", 0),
            (s.get("valueText") or "").strip())


def render(base, head, display_name):
    base_steps = (base or {}).get("steps", []) or []
    head_steps = (head or {}).get("steps", []) or []

    lines = []
    lines.append(f"### Chaos scenario diff — `{display_name}`")
    if base is None:
        lines.append("")
        lines.append(f"_New file — {len(head_steps)} step(s)._")
    elif head is None:
        lines.append("")
        lines.append("_File DELETED in this PR._")
    elif base_steps == head_steps:
        lines.append("")
        lines.append("_(no behavioural change — file edited but step list identical)_")
    lines.append("")

    # Build a stable timeline keyed on atSec; show side-by-side.
    base_by_t = {}
    for s in base_steps:
        base_by_t.setdefault(round(float(s["atSec"]), 3), []).append(s)
    head_by_t = {}
    for s in head_steps:
        head_by_t.setdefault(round(float(s["atSec"]), 3), []).append(s)

    timeline = sorted(set(list(base_by_t.keys()) + list(head_by_t.keys())))
    lines.append("| at (s) | BASE | HEAD | change |")
    lines.append("|--------|------|------|--------|")
    for t in timeline:
        b = base_by_t.get(t, [])
        h = head_by_t.get(t, [])
        # Sort by kind + value so the comparison is deterministic for
        # multi-step-at-same-time scenarios.
        b_sorted = sorted(b, key=key_of)
        h_sorted = sorted(h, key=key_of)
        max_rows = max(len(b_sorted), len(h_sorted))
        for i in range(max_rows):
            b_lbl = fmt_step(b_sorted[i]) if i < len(b_sorted) else "—"
            h_lbl = fmt_step(h_sorted[i]) if i < len(h_sorted) else "—"
            marker = ""
            if b_lbl == "—":             marker = "🟢 added"
            elif h_lbl == "—":           marker = "🔴 removed"
            elif b_lbl != h_lbl:         marker = "🟡 changed"
            lines.append(f"| {t:.1f} | {b_lbl} | {h_lbl} | {marker} |")

    if base and head:
        if base.get("description") != head.get("description"):
            lines.append("")
            lines.append("**description changed:**")
            lines.append(f"- BASE: `{base.get('description', '')}`")
            lines.append(f"- HEAD: `{head.get('description', '')}`")
    return "\n".join(lines)


def main():
    args = sys.argv[1:]
    name = None
    if "--name" in args:
        i = args.index("--name")
        name = args[i + 1]
        del args[i:i + 2]
    if len(args) != 2:
        print("usage: scenario-diff.py BASE.json HEAD.json [--name display]", file=sys.stderr)
        sys.exit(2)
    base_path, head_path = args
    base = load(base_path)
    head = load(head_path)
    display = name or Path(head_path if head else base_path).name
    print(render(base, head, display))


if __name__ == "__main__":
    main()
