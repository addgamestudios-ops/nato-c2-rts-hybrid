#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — verify-dpdu.py
#  ---------------------------------------------------------------------
#  Standalone integrity-checker for .dpdu capture files written by
#  Stanag5066Capture (S5CP magic, v1 or v2 layout).
#
#  Behaviour:
#      v1 file → prints "v1 (no chain to verify)"  → exit 0
#      v2 file, chain intact → prints "OK (N records)" → exit 0
#      v2 file, tampered     → prints "TAMPERED@record=K"  → exit 2
#      anything malformed    → prints diagnostic to stderr → exit 1
#
#  Mirrors Stanag5066Capture.Verify() in C#. The chain hash for record
#  K is SHA-256(prev_hash || record_header_8B || frame_bytes), where
#  prev_hash for K=0 is 32 zero bytes.
#
#  Usage:
#      python3 verify-dpdu.py path/to/L16-20260526-143012.dpdu
# =====================================================================

import hashlib
import sys

S5CP_MAGIC = b"S5CP"
HASH_SIZE  = 32


def verify(path: str) -> tuple[int, str]:
    """Return (exit_code, message)."""
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        return 1, f"cannot open {path}: {e}"

    if len(data) < 8 or data[:4] != S5CP_MAGIC:
        return 1, f"{path}: not an S5CP file (bad magic)"

    ver = data[4]
    if ver not in (1, 2):
        return 1, f"{path}: unsupported S5CP version {ver}"

    if ver == 1:
        # v1 has no chain; just walk records to confirm structural sanity.
        idx = 0
        pos = 8
        while pos < len(data):
            if pos + 8 > len(data):
                return 1, f"{path}: truncated record header at idx {idx}"
            flen = int.from_bytes(data[pos + 4:pos + 8], "big")
            if pos + 8 + flen > len(data):
                return 1, f"{path}: truncated frame body at idx {idx}"
            pos += 8 + flen
            idx += 1
        return 0, f"v1 (no chain to verify) — {idx} records"

    # ---- v2: walk + recompute the SHA-256 chain ----
    prev = bytes(HASH_SIZE)
    idx = 0
    pos = 8
    while pos < len(data):
        if pos + 8 > len(data):
            return 1, f"{path}: truncated record header at idx {idx}"
        hdr = data[pos:pos + 8]
        flen = int.from_bytes(hdr[4:8], "big")
        if pos + 8 + flen + HASH_SIZE > len(data):
            return 1, f"{path}: truncated record at idx {idx}"
        frame  = data[pos + 8:pos + 8 + flen]
        stored = data[pos + 8 + flen:pos + 8 + flen + HASH_SIZE]
        expected = hashlib.sha256(prev + hdr + frame).digest()
        if stored != expected:
            return 2, f"{path}: TAMPERED@record={idx}"
        prev = stored
        pos += 8 + flen + HASH_SIZE
        idx += 1

    return 0, f"OK ({idx} records)"


def main():
    if len(sys.argv) != 2:
        print("usage: verify-dpdu.py path/to/capture.dpdu", file=sys.stderr)
        sys.exit(2)
    code, msg = verify(sys.argv[1])
    stream = sys.stdout if code == 0 else sys.stderr
    print(msg, file=stream)
    sys.exit(code)


if __name__ == "__main__":
    main()
