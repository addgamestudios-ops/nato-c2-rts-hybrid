# Key rotation runbook (chaos scenarios)

This procedure is the leak-response path for the RSA-2048 keypair that
signs chaos scenarios. Run it any time you suspect
`scenarios-priv.xml` has been exposed (laptop loss, CI secret leak,
accidental commit, contractor offboarding).

## What rotation does

1. Archives the current public key into
   `Tools/chaos-scenarios/key-archive/scenarios-pub.{oldKeyId}.{ts}.xml`
   so old `.sig` files can still be traced to their origin in audits.
2. Generates a fresh RSA-2048 keypair with a new `keyId` (the UTC
   timestamp at rotation time).
3. Re-signs every `.json` in `Tools/chaos-scenarios/` with the new
   private key, producing fresh sidecars.
4. Old `.sig` files (e.g. on a feature branch made before the
   rotation) **will fail verification** the next time someone tries
   to load them — that's the point: the compromised key is dead.

## Procedure

### 1. Restore the current private key locally

The private key is gitignored, so it lives outside the repo (password
manager, vaulted secret store). Copy it back:

```sh
cp ~/secrets/scenarios-priv.xml Tools/chaos-scenarios/
```

You'll need this for the rotation step to re-sign existing scenarios.
If you don't have it, you can still rotate — but every old `.sig`
gets discarded; scenarios will be unsigned until you sign them again.

### 2. Run the rotation

In Unity, open **NATO C2 → Link 16 → Scenario Signer**. The header
shows the active `keyId`. Click **Rotate keypair (key leak response)**
and confirm the dialog.

The console logs:

```
[ScenarioSigner] archived old pub key (20260526-150022) → key-archive/scenarios-pub.20260526-150022.20260527-091233.xml
[ScenarioSigner] generated RSA-2048 keypair (keyId=20260527-091233) in ...
[ScenarioSigner] signed 4 scenario(s) in ...
[ScenarioSigner] rotation complete — active keyId=20260527-091233, every .sig refreshed.
```

### 3. Move the NEW private key out

Crucial — same hygiene as the initial setup:

```sh
mv Tools/chaos-scenarios/scenarios-priv.xml ~/secrets/
```

Update your secret store / 1Password vault with the new key.

### 4. Commit and push

```sh
git add Tools/chaos-scenarios/scenarios-pub.xml
git add Tools/chaos-scenarios/*.json.sig
git add Tools/chaos-scenarios/key-archive/scenarios-pub.*.xml
git commit -m "rotate chaos-scenario signing key (incident response)"
git push
```

CI then immediately enforces the new key — any open PR with a stale
`.sig` fails the EditMode tests with a
`scenario signature check: BadSignature` error in the JUnit summary.

### 5. Notify cosigners (if applicable)

If the project uses multi-party cosigners (see `README.md`), every
cosigner needs to re-sign each scenario with their own private key.
Announce the rotation in the team's secure channel and link this
runbook.

## What NOT to do

- **Do not** commit `scenarios-priv.xml`. The `.gitignore` blocks it
  by name, but only if the file lives at the canonical path. Don't
  rename or move it into another location.
- **Do not** rotate "preventively" on a schedule unless you're also
  willing to bump every open PR. Rotation is cheap; surprise rotation
  during release week is not.
- **Do not** delete the `key-archive/` folder — those archived public
  keys let auditors verify historical scenario versions even after
  the active key has moved on.
