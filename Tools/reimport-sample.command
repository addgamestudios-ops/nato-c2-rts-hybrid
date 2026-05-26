#!/usr/bin/env bash
# Double-click wrapper for reimport-sample.sh — Finder will run this in
# Terminal so the user sees the output.
cd "$(dirname "$0")"
exec bash ./reimport-sample.sh
