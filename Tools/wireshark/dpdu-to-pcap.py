#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — dpdu-to-pcap.py
#  ---------------------------------------------------------------------
#  Converts a .dpdu capture file (S5CP magic, written by
#  Stanag5066Capture) into a classic-format PCAP file that Wireshark
#  can open. Each D_PDU frame becomes one packet, encapsulated as
#  LINKTYPE_USER0 (DLT=147) so the stanag5066.lua dissector picks it up.
#
#  Usage:
#      python3 dpdu-to-pcap.py input.dpdu output.pcap
#
#  Then in Wireshark:
#      File → Open → output.pcap
#      (If the Lua plugin is installed and Wireshark hasn't been told
#       to decode USER0 yet: right-click any packet → Decode As → User 0
#       → STANAG 5066 D_PDU.)
# =====================================================================

import struct
import sys
import time

S5CP_MAGIC      = b"S5CP"
S5CP_VERSION    = 1
LINKTYPE_USER0  = 147
PCAP_MAGIC_LE   = 0xA1B2C3D4
SNAPLEN         = 65535


def read_dpdu(path):
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 8 or data[:4] != S5CP_MAGIC:
        raise SystemExit(f"{path}: not an S5CP capture (bad magic)")
    if data[4] != S5CP_VERSION:
        raise SystemExit(f"{path}: unsupported S5CP version {data[4]}")
    records = []
    pos = 8                       # past magic + version + reserved
    while pos < len(data):
        if pos + 8 > len(data):
            raise SystemExit(f"{path}: truncated record header at {pos}")
        direction = data[pos]
        pos += 4                  # direction (1B) + reserved (3B)
        frame_len = int.from_bytes(data[pos:pos + 4], "big")
        pos += 4
        if pos + frame_len > len(data):
            raise SystemExit(f"{path}: truncated frame body at {pos}")
        records.append((direction, data[pos:pos + frame_len]))
        pos += frame_len
    return records


def write_pcap(path, records):
    with open(path, "wb") as f:
        # Classic PCAP global header: 24 bytes.
        f.write(struct.pack(
            "<IHHiIII",
            PCAP_MAGIC_LE,         # magic
            2, 4,                  # version major / minor
            0, 0,                  # tz offset, sigfigs
            SNAPLEN,
            LINKTYPE_USER0,
        ))
        t = time.time()
        for direction, frame in records:
            ts_sec  = int(t)
            ts_usec = int((t - ts_sec) * 1_000_000)
            f.write(struct.pack("<IIII", ts_sec, ts_usec, len(frame), len(frame)))
            f.write(frame)
            # Space packets 1ms apart so Wireshark's timing display is readable.
            t += 0.001


def main():
    if len(sys.argv) != 3:
        print("usage: dpdu-to-pcap.py input.dpdu output.pcap", file=sys.stderr)
        sys.exit(2)
    records = read_dpdu(sys.argv[1])
    write_pcap(sys.argv[2], records)
    print(f"wrote {len(records)} packets to {sys.argv[2]}")


if __name__ == "__main__":
    main()
