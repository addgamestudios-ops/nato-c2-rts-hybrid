# Wireshark integration for STANAG 5066 D_PDU captures

This folder ships a Lua dissector and a `.dpdu → .pcap` converter so a
real Wireshark install decodes the binary D_PDUs the NATO C2 federation
bridge writes to disk.

## What's here

| File                    | Purpose                                                  |
|-------------------------|----------------------------------------------------------|
| `stanag5066.lua`        | Wireshark Lua dissector for the 16-byte D_PDU header     |
| `dpdu-to-pcap.py`       | Converts a `.dpdu` capture to a classic PCAP file        |

## Install the dissector

1. Open Wireshark.
2. Click **About → Folders → Personal Lua Plugins**. Note the path.
   On macOS it's typically `~/.config/wireshark/plugins/`.
3. Copy `stanag5066.lua` into that folder.
4. Quit and relaunch Wireshark (or use **Analyze → Reload Lua Plugins**).

## Convert a capture

The Unity `Stanag5066Capture` MonoBehaviour writes files like:

```
~/Library/Application Support/com.<your-company>.<your-project>/Captures/L16-20260526-143012.dpdu
```

Convert with:

```sh
python3 dpdu-to-pcap.py \
  ~/Library/Application\ Support/.../Captures/L16-20260526-143012.dpdu \
  /tmp/L16.pcap
```

Then **File → Open** `/tmp/L16.pcap` in Wireshark.

## Tell Wireshark to decode USER0 as STANAG 5066

The first time you open one of these PCAPs, Wireshark treats the bytes
as raw USER0. Right-click any packet and pick:

**Decode As… → User 0 (DLT=147) → STANAG 5066 D_PDU**

After that, every packet shows the dissection tree with sync sequence,
type (DATA-ONLY / DATA-WITH-ACK / SREJ / NON-ARQ DATA), ESN, ACK/SREJ
target, packing mode, sender op, note, and CRC-32 trailer.

## Filter examples

Once decoded, these filters work in the Wireshark filter bar:

| Filter                              | What you see                    |
|-------------------------------------|---------------------------------|
| `s5066pdu.type == 3`                | All SREJ frames                 |
| `s5066pdu.ack_esn == 0x00aa`        | ACK / SREJ for ESN 0xAA         |
| `s5066pdu.op == "W1"`               | Frames from operator W1         |
| `s5066pdu.srej_end > 0`             | Range-SREJ frames only          |
| `s5066pdu.mode == 1`                | P2DP packing-mode envelopes     |

## CRC verification

The dissector exposes the trailer at `s5066pdu.crc` but does NOT
re-compute it against the body. To validate frames, run the
`Stanag5066DPduTests.Binary_TamperedPayload_FailsCrcAndRejects`
test in Unity Test Runner — it exercises the same byte-level codec.
