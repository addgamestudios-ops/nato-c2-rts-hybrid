#!/usr/bin/env bash
# =====================================================================
#  NATO C2 RTS Hybrid — sign-and-notarize.sh
#  ---------------------------------------------------------------------
#  codesign + notarize the standalone macOS .app so it launches without
#  a Gatekeeper warning ("Apple cannot check it for malicious software").
#
#  Inputs — set as env vars (NEVER hardcode credentials):
#      APP_PATH       Path to the .app bundle (default: ~/Desktop/NATO_C2.app)
#      APPLE_DEV_ID   The "Developer ID Application: …" identity name.
#                     Check with: security find-identity -p codesigning -v
#      APPLE_ID       Your Apple ID email (notarytool only).
#      APPLE_TEAM_ID  Your 10-char team ID from developer.apple.com/account.
#      APPLE_APP_PWD  An app-specific password from appleid.apple.com.
#                     (Apple won't accept your account password.)
#
#  Steps the script performs:
#      1. codesign --deep --force --options runtime --timestamp
#         (hardened runtime is REQUIRED for notarization)
#      2. zip the .app for upload (notarytool needs an archive)
#      3. xcrun notarytool submit ... --wait
#         (takes 30 s – 5 min, returns once Apple stamps the ticket)
#      4. xcrun stapler staple   (embeds the ticket so the app
#         can launch offline without phoning home)
#
#  After this finishes:
#      • Right-click → Open works, no warning.
#      • Drag the app to Applications and double-click — Just Works.
#      • The user's first launch performs an offline Gatekeeper check
#        and finds the stapled ticket.
#
#  Production tip — wrap the .app in a notarized DMG and ship the DMG
#  if you want a nicer install experience. Skipped here for simplicity.
# =====================================================================

set -euo pipefail

APP_PATH="${APP_PATH:-$HOME/Desktop/NATO_C2.app}"

# ---------- sanity checks ----------
if [ ! -d "$APP_PATH" ]; then
  echo "FATAL: $APP_PATH not found. Build the app first via Unity → NATO C2 → Build → macOS standalone (.app)" >&2
  exit 1
fi

missing=()
for v in APPLE_DEV_ID APPLE_ID APPLE_TEAM_ID APPLE_APP_PWD; do
  if [ -z "${!v:-}" ]; then missing+=("$v"); fi
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "FATAL: missing required env var(s): ${missing[*]}" >&2
  echo
  echo "Example:"
  echo "  export APPLE_DEV_ID='Developer ID Application: Alex Example (ABCD123456)'"
  echo "  export APPLE_ID='you@icloud.com'"
  echo "  export APPLE_TEAM_ID='ABCD123456'"
  echo "  export APPLE_APP_PWD='abcd-efgh-ijkl-mnop'   # app-specific password"
  echo "  bash $0"
  exit 1
fi

if ! command -v codesign >/dev/null 2>&1; then
  echo "FATAL: codesign missing. Install Xcode Command Line Tools: xcode-select --install" >&2
  exit 1
fi
if ! xcrun --find notarytool >/dev/null 2>&1; then
  echo "FATAL: xcrun notarytool missing. Update Xcode (14+)." >&2
  exit 1
fi

# ---------- 1. codesign with hardened runtime ----------
ENTITLEMENTS="$(dirname "$0")/macos-entitlements.plist"
if [ ! -f "$ENTITLEMENTS" ]; then
  echo "[sign] writing default entitlements to $ENTITLEMENTS"
  cat > "$ENTITLEMENTS" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <!-- Unity needs JIT for some Mono compile paths even in IL2CPP builds. -->
  <key>com.apple.security.cs.allow-jit</key><true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
  <key>com.apple.security.cs.disable-library-validation</key><true/>
  <!-- Network access for TAK Server / OSM tiles / Unity Cloud. -->
  <key>com.apple.security.network.client</key><true/>
  <key>com.apple.security.network.server</key><true/>
</dict>
</plist>
PLIST
fi

echo "[sign] codesign-ing $APP_PATH with $APPLE_DEV_ID"
codesign --deep --force --options runtime --timestamp \
  --entitlements "$ENTITLEMENTS" \
  --sign "$APPLE_DEV_ID" \
  "$APP_PATH"

echo "[sign] verifying…"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"

# ---------- 2. zip for notarization ----------
ZIP_PATH="${APP_PATH%.app}.zip"
echo "[zip] producing $ZIP_PATH"
rm -f "$ZIP_PATH"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"

# ---------- 3. submit to Apple ----------
echo "[notarize] submitting (this can take 30 s – 5 min)…"
xcrun notarytool submit "$ZIP_PATH" \
  --apple-id "$APPLE_ID" \
  --team-id  "$APPLE_TEAM_ID" \
  --password "$APPLE_APP_PWD" \
  --wait

# ---------- 4. staple the ticket ----------
echo "[staple] stapling notarization ticket to the .app"
xcrun stapler staple "$APP_PATH"

# ---------- 5. final verification ----------
echo "[verify] Gatekeeper assessment:"
spctl --assess --type execute --verbose=4 "$APP_PATH" || true
echo
echo "✅ Done. $APP_PATH is signed, notarized, and stapled."
echo "   Double-click it from anywhere — no Gatekeeper warning."
echo "   Cleanup: rm $ZIP_PATH"
