#!/usr/bin/env bash
# Double-click me from Finder to install the Unity MCP into Claude desktop.
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$DIR/install.sh"
echo
echo "Press any key to close this window."
read -n 1 -s
