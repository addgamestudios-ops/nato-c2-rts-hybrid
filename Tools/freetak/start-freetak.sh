#!/usr/bin/env bash
# =====================================================================
#  NATO C2 RTS Hybrid — start-freetak.sh
#  ---------------------------------------------------------------------
#  One-command FreeTAKServer bootstrap.  Detects Docker, brings the
#  stack up, prints the LAN IP + ATAK pairing details, and tails the
#  log so the user can see CoT events stream in.
# =====================================================================

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

echo "================================================================"
echo "  NATO C2 RTS Hybrid — FreeTAKServer launcher"
echo "================================================================"

# ---------- preflight ----------
if ! command -v docker >/dev/null 2>&1; then
  cat >&2 <<EOF
FATAL: docker is not installed.

Install Docker Desktop for Mac from:
  https://www.docker.com/products/docker-desktop/

After install:
  1. Launch Docker Desktop (whale icon top-right turns blue when ready).
  2. Re-run this script.
EOF
  exit 1
fi

if ! docker info >/dev/null 2>&1; then
  echo "FATAL: docker is installed but the daemon isn't running." >&2
  echo "       Launch Docker Desktop and wait for the whale icon to settle, then re-run." >&2
  exit 1
fi

# ---------- network info ----------
LAN_IP="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo 127.0.0.1)"
echo "[fts] LAN IP for ATAK pairing: $LAN_IP"

# ---------- bring up the stack ----------
echo "[fts] starting docker compose stack…"
docker compose up -d

echo
echo "[fts] waiting for the FTS API to answer (max 60 s)…"
for i in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:19023/Marti/api/clientEndPoints >/dev/null 2>&1; then
    echo "[fts] ✅ FreeTAKServer is alive."
    break
  fi
  sleep 2
done

# ---------- next-step summary ----------
cat <<EOF

================================================================
  ✅  FreeTAKServer is running.

  Web UI               http://localhost:5000
                       admin / change-me-on-first-login

  CoT endpoint (Unity) tcp://127.0.0.1:8087   ← already the default
                                                  in TakServerCotAdapter

  ATAK pairing target  tcp://$LAN_IP:8087     ← put this in your phone

  Live map (browser)   http://localhost:8443

  Logs (live)          docker compose logs -f freetakserver
  Stop the stack       docker compose down

  Pair ATAK on your phone — see ATAK_PAIRING.md in this directory.
================================================================
EOF
