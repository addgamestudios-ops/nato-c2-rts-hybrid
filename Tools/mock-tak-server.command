#!/usr/bin/env bash
# Double-click wrapper for mock-tak-server.py — launches in Terminal
# so you can see incoming/outgoing CoT traffic.
cd "$(dirname "$0")"
exec /usr/bin/python3 ./mock-tak-server.py
