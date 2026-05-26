#!/usr/bin/env bash
# =====================================================================
#  NATO C2 RTS Hybrid — git-init-and-push.sh
#  ---------------------------------------------------------------------
#  One-shot helper: initialise git in the project, make the first
#  commit, attach a remote, and push.  Prompts for the remote URL via
#  AppleScript so the user just needs to paste a URL once.
#
#  Idempotent — re-runs are safe:
#      • If .git already exists, skips init.
#      • If there's nothing new to commit, skips commit.
#      • If a remote called "origin" already exists, just verifies it.
# =====================================================================

set -uo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR"

echo "[git-setup] repository: $REPO_DIR"

# ---------- check we have git ----------
if ! command -v git >/dev/null 2>&1; then
  osascript -e 'display alert "git not found" message "Install Xcode Command Line Tools first: xcode-select --install" as critical' >/dev/null 2>&1 || true
  echo "[git-setup] FATAL: git is not on PATH. Install Xcode CLT first." >&2
  exit 1
fi

# ---------- git init (idempotent) ----------
if [ ! -d ".git" ]; then
  echo "[git-setup] running git init…"
  git init -b main
else
  echo "[git-setup] .git already present — skipping init"
fi

# Set a name+email if global isn't configured (git refuses to commit otherwise).
if ! git config user.email >/dev/null 2>&1; then
  echo "[git-setup] no global user.email — using addgamestudios@gmail.com for this repo"
  git config user.email "addgamestudios@gmail.com"
  git config user.name  "Alex"
fi

# ---------- clear stale .lock files if any are hanging around ----------
# Common cause: a prior `git add` / `commit` / `push` was interrupted
# (Cmd+C, Terminal closed, Editor quit) and left lock files behind.
# Git creates locks under .git/index.lock, .git/HEAD.lock,
# .git/refs/heads/*.lock, .git/packed-refs.lock, .git/config.lock, etc.
# We only sweep them when no other git process is currently using this
# repo — otherwise we'd race a live operation.
if find .git -maxdepth 4 -name '*.lock' -print -quit 2>/dev/null | grep -q .; then
  if ! pgrep -laf "git .*$REPO_DIR" >/dev/null 2>&1; then
    echo "[git-setup] sweeping stale .git/*.lock files (no live git process):"
    find .git -maxdepth 4 -name '*.lock' -print -delete
  else
    echo "[git-setup] WARNING: .git lock files present AND a git process is running."
    echo "             Wait for it to finish or kill it before re-running."
  fi
fi

# ---------- commit anything new ----------
# We use `set -e` semantics here defensively — if `git add` fails, don't
# bail-through to the push step (which would push stale state).
if [ -n "$(git status --porcelain)" ]; then
  echo "[git-setup] staging files…"
  if ! git add -A; then
    echo "[git-setup] FATAL: git add failed — see message above." >&2
    exit 1
  fi
  if ! git commit -m "$(date +'%Y-%m-%d') — NATO C2 RTS Hybrid checkpoint"; then
    echo "[git-setup] FATAL: git commit failed." >&2
    exit 1
  fi
else
  echo "[git-setup] working tree clean — nothing to commit"
fi

# ---------- remote ----------
if git remote get-url origin >/dev/null 2>&1; then
  current_remote=$(git remote get-url origin)
  echo "[git-setup] remote 'origin' is already set: $current_remote"
else
  REMOTE_URL=$(osascript <<'OSASCRIPT' 2>/dev/null
    set theResp to display dialog "Paste your GitHub repo URL (SSH or HTTPS).

Example:
  git@github.com:addgamestudios/nato-c2-rts-hybrid.git
  https://github.com/addgamestudios/nato-c2-rts-hybrid.git

Or click Cancel to skip the push step." default answer "" with title "NATO C2 — GitHub remote"
    text returned of theResp
OSASCRIPT
  )

  if [ -z "$REMOTE_URL" ]; then
    echo "[git-setup] no remote URL given. Run this again later or:"
    echo "    cd $REPO_DIR"
    echo "    git remote add origin <YOUR_REPO_URL>"
    echo "    git push -u origin main"
    exit 0
  fi
  git remote add origin "$REMOTE_URL"
  echo "[git-setup] added remote 'origin' → $REMOTE_URL"
fi

# ---------- push ----------
echo "[git-setup] pushing to origin main…"
if git push -u origin main 2>&1; then
  echo "[git-setup] ✅ pushed to $(git remote get-url origin)"
else
  echo "[git-setup] ❌ push failed — check the URL + your SSH key / token"
  echo "[git-setup] common fixes:"
  echo "    • For SSH: make sure your key is at ~/.ssh/id_ed25519 and added"
  echo "      to your GitHub account at https://github.com/settings/keys"
  echo "    • For HTTPS: GitHub prompts for a Personal Access Token, NOT"
  echo "      your password. Create one at https://github.com/settings/tokens"
fi
