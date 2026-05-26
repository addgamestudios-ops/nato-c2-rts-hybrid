#!/usr/bin/env bash
# Double-click wrapper. Runs start-freetak.sh in Terminal so output is
# visible while Docker is bringing up the stack.
cd "$(dirname "$0")"
exec bash ./start-freetak.sh
