#!/usr/bin/env bash
# Double-click wrapper for git-init-and-push.sh — runs in Terminal so
# you see the output. The first run shows a dialog asking for your
# GitHub repo URL; subsequent runs just push new commits.
cd "$(dirname "$0")"
exec bash ./git-init-and-push.sh
