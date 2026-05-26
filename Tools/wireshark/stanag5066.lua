-- =====================================================================
--  NATO C2 RTS Hybrid — stanag5066.lua
--  ---------------------------------------------------------------------
--  Wireshark Lua dissector for the STANAG 5066 Annex C D_PDU as
--  emitted by Stanag5066FederationBridge.
--
--  Recognised types:
--      0  DATA-ONLY            — ARQ-tracked, may carry payload
--      2  DATA-WITH-ACK        — piggybacked ACK + optional new data
--      3  SELECTIVE-REJECT     — NAK (single ESN or [start..end] range)
--      4  NON-ARQ DATA         — best-effort, no ACK requested
--
--  Wire layout (matches Stanag5066DPdu.cs ToBytes):
--      [0..1]   sync 0x90 0xEB
--      [2]      high nibble = D_PDU type, low nibble = EOW type
--      [3]      EOW data
--      [4]      header size (always 16)
--      [5..6]   payload size (BE u16)
--      [7]      hop count
--      [8..9]   source address (BE u16, FNV hash of senderOp)
--      [10..11] destination address (0xFFFF broadcast)
--      [12..13] C_PDU ID / ESN (BE u16)
--      [14..15] type-specific:
--                 type 2 / 3 → ACK ESN / rejected ESN (BE u16)
--                 type 0 / 4 → mode (1B) + msg count (1B)
--      [16..]   payload:
--                 type 3 only: srejRangeEnd (BE u16, 0 = single SREJ)
--                 u16 op_len, op_utf8
--                 u16 note_len, note_utf8
--      [end-4..end-1]  CRC-32 (BE u32) over header+payload
--
--  Install:
--      cp stanag5066.lua ~/.config/wireshark/plugins/
--      (or wherever your platform's plugin folder lives; the
--       Wireshark "About → Folders" dialog tells you.)
--      Restart Wireshark.
--
--  Use:
--      1. Convert a .dpdu capture to PCAP via dpdu-to-pcap.py.
--      2. Open the .pcap in Wireshark.
--      3. Right-click any packet → Decode As → User 0 (DLT=147) →
--         STANAG 5066 D_PDU. Fields appear in the dissection tree.
-- =====================================================================

local s5066 = Proto.new("s5066pdu", "STANAG 5066 D_PDU")

local D_PDU_TYPES = {
    [0] = "DATA-ONLY",
    [2] = "DATA-WITH-ACK",
    [3] = "SELECTIVE-REJECT",
    [4] = "NON-ARQ DATA",
}

local PACKING_MODES = {
    [0] = "STD-DP",
    [1] = "P2DP",
    [2] = "P4SP",
}

local f = s5066.fields
f.sync         = ProtoField.bytes ("s5066pdu.sync",         "Sync")
f.type         = ProtoField.uint8 ("s5066pdu.type",         "D_PDU Type",  base.DEC, D_PDU_TYPES, 0xF0)
f.eow          = ProtoField.uint8 ("s5066pdu.eow",          "EOW Type",    base.DEC, nil,          0x0F)
f.eow_data     = ProtoField.uint8 ("s5066pdu.eow_data",     "EOW Data")
f.hdr_size     = ProtoField.uint8 ("s5066pdu.hdr_size",     "Header Size")
f.payload_size = ProtoField.uint16("s5066pdu.payload_size", "Payload Size")
f.hop          = ProtoField.uint8 ("s5066pdu.hop",          "Hop Count")
f.src          = ProtoField.uint16("s5066pdu.src",          "Source Address",      base.HEX)
f.dst          = ProtoField.uint16("s5066pdu.dst",          "Destination Address", base.HEX)
f.esn          = ProtoField.uint16("s5066pdu.esn",          "C_PDU ID (ESN)",      base.HEX)
f.ack_esn      = ProtoField.uint16("s5066pdu.ack_esn",      "ACK / Rejected ESN",  base.HEX)
f.mode         = ProtoField.uint8 ("s5066pdu.mode",         "Packing Mode",        base.DEC, PACKING_MODES)
f.msg_count    = ProtoField.uint8 ("s5066pdu.msg_count",    "Message Count")
f.srej_end     = ProtoField.uint16("s5066pdu.srej_end",     "SREJ Range End",      base.HEX)
f.op_len       = ProtoField.uint16("s5066pdu.op_len",       "Sender Op Length")
f.op           = ProtoField.string("s5066pdu.op",           "Sender Op")
f.note_len     = ProtoField.uint16("s5066pdu.note_len",     "Note Length")
f.note         = ProtoField.string("s5066pdu.note",         "Note")
f.crc          = ProtoField.uint32("s5066pdu.crc",          "CRC-32",              base.HEX)

function s5066.dissector(buf, pkt, tree)
    if buf:len() < 20 then return 0 end
    if buf(0,1):uint() ~= 0x90 or buf(1,1):uint() ~= 0xEB then return 0 end

    local subtree = tree:add(s5066, buf(0, buf:len()))
    subtree:add(f.sync, buf(0, 2))
    local typeEow = buf(2,1):uint()
    local dtype   = bit.rshift(typeEow, 4)
    subtree:add(f.type,     buf(2,1))
    subtree:add(f.eow,      buf(2,1))
    subtree:add(f.eow_data, buf(3,1))
    subtree:add(f.hdr_size, buf(4,1))
    local payloadSize = buf(5,2):uint()
    subtree:add(f.payload_size, buf(5,2))
    subtree:add(f.hop, buf(7,1))
    subtree:add(f.src, buf(8,2))
    subtree:add(f.dst, buf(10,2))
    subtree:add(f.esn, buf(12,2))

    if dtype == 2 or dtype == 3 then
        subtree:add(f.ack_esn, buf(14,2))
    else
        subtree:add(f.mode,      buf(14,1))
        subtree:add(f.msg_count, buf(15,1))
    end

    -- Payload region.
    local off = 16
    local payload_end = 16 + payloadSize
    if dtype == 3 and off + 2 <= payload_end then
        subtree:add(f.srej_end, buf(off, 2))
        off = off + 2
    end
    if off + 2 <= payload_end then
        local opLen = buf(off, 2):uint()
        subtree:add(f.op_len, buf(off, 2))
        off = off + 2
        if opLen > 0 and off + opLen <= payload_end then
            subtree:add(f.op, buf(off, opLen))
            off = off + opLen
        end
    end
    if off + 2 <= payload_end then
        local noteLen = buf(off, 2):uint()
        subtree:add(f.note_len, buf(off, 2))
        off = off + 2
        if noteLen > 0 and off + noteLen <= payload_end then
            subtree:add(f.note, buf(off, noteLen))
        end
    end

    -- CRC-32 trailer.
    if buf:len() >= payload_end + 4 then
        subtree:add(f.crc, buf(payload_end, 4))
    end

    pkt.cols.protocol = "S5066"
    local label = D_PDU_TYPES[dtype] or string.format("Type %d", dtype)
    pkt.cols.info = string.format("%s  ESN=0x%04X", label, buf(12,2):uint())
    if dtype == 2 or dtype == 3 then
        pkt.cols.info = string.format("%s  ACK/REJ=0x%04X", tostring(pkt.cols.info), buf(14,2):uint())
    end
    return buf:len()
end

-- Register on LINKTYPE_USER0 (DLT=147). The dpdu-to-pcap.py tool emits
-- frames in this encapsulation so Wireshark routes them here.
local user_dlt = DissectorTable.get("wtap_encap")
user_dlt:add(wtap.USER0, s5066)
