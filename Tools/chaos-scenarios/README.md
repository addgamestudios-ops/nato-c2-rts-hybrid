# Chaos scenarios

Canonical chaos-mode scripts driven by **NATO C2 → Link 16 → Federation
Chaos Mode** in the Unity Editor.

| File                  | Purpose                                                    |
|-----------------------|------------------------------------------------------------|
| `smoke.json`          | 5 s baseline + light drops. Also run as an EditMode test.  |
| `jam-storm.json`      | 40 s ramp to 95% drops and back.                           |
| `peer-failover.json`  | Named peer drops; recovery exercises the health alarm.     |
| `restart-resync.json` | Simultaneous peer-drop + high drops, then sudden restore.  |

## Signing (production / release builds)

When `NATO_REQUIRE_SIGNED_SCENARIOS=1` is set in the environment (CI
does this), `FederationChaosMode.LoadScenarioFromFile` **refuses to
run any scenario whose sidecar `.sig` is missing or whose signature
doesn't verify against the bundled public key**. Otherwise it warns.

### One-time setup (per project)

1. Open **NATO C2 → Link 16 → Scenario Signer** in the Unity Editor.
2. Click **Generate keypair**. This writes:
   - `Tools/chaos-scenarios/scenarios-pub.xml`  — commit this.
   - `Tools/chaos-scenarios/scenarios-priv.xml` — **DO NOT COMMIT**.
     The `.gitignore` already excludes it; back the private key up
     out-of-repo (e.g. 1Password, vaulted secret store).
3. Click **Sign all scenarios**. Each `.json` gets a sidecar
   `.json.sig` (base64 RSA-2048 SHA-256). Commit the `.sig` files.

### After editing a scenario

1. Move `scenarios-priv.xml` back into `Tools/chaos-scenarios/`
   temporarily.
2. Click **Sign all scenarios** to refresh every `.sig`.
3. Move `scenarios-priv.xml` back out.
4. Commit the updated `.json` + `.sig`.

### Verify all (read-only)

Click **Verify all scenarios** in the signer window. Lists each
scenario with `✓`, `✗ SIGNATURE INVALID`, or `NO SIGNATURE`.

## Adding a new scenario

1. In **Federation Chaos Mode**, build the script via the step
   table.
2. Click **Save as...** and drop the file in
   `Tools/chaos-scenarios/`.
3. Click into the **Scenario Signer** window and sign it as above.
