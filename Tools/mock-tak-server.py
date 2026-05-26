#!/usr/bin/env python3
# =====================================================================
#  NATO C2 RTS Hybrid — mock-tak-server.py
#  ---------------------------------------------------------------------
#  A tiny TCP CoT echo server for end-to-end testing of the Unity
#  TakServerCotAdapter without standing up FreeTAKServer.
#
#  What it does:
#    1. Listens on 0.0.0.0:8087 (CoT-over-TCP, no TLS — matches
#       FreeTAKServer / TAK Server's plain-TCP port).
#    2. Accepts any number of clients.
#    3. Parses inbound CoT XML events and logs them to stdout so you
#       can see Unity → server traffic.
#    4. Every 2 seconds, emits two SYNTHETIC FOREIGN TRACKS (a hostile
#       and a neutral) that the Unity client will receive via OnCot and
#       render via CotTrackPanel as map markers.
#    5. Echoes everything to every other connected client (just like
#       a real TAK Server does for federation).
#
#  Run with:
#      python3 /Users/alex/Documents/Claude/Projects/Nato/Tools/mock-tak-server.py
#  Or double-click  mock-tak-server.command  from Finder.
#
#  Stop with Ctrl-C.
#
#  Production note: this is for DEMO ONLY. A real TAK Server (TAK
#  Server 5.x, FreeTAKServer, NodeRedTAK) provides:
#    • TLS 1.2/1.3 mTLS with client X.509 certs.
#    • CoT federation across multiple servers (FEDX).
#    • Mission packages, geofences, video streaming, chat, file drop.
#    • Persistence (PostgreSQL).
#    • REST API for non-CoT clients.
#  This script implements none of that — it's just enough to validate
#  the wire format and bidirectional flow.
# =====================================================================

import math
import socket
import sys
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone

HOST = "0.0.0.0"
PORT = 8087

# Where the demo is centred (matches DemoSceneBootstrap.basemapLat/Lon).
ORIGIN_LAT = 38.7400
ORIGIN_LON = 22.2540

# Synthetic foreign tracks the server pushes out so Unity has something
# to render via CotTrackPanel. Tweak freely.
SYNTHETIC = [
    {
        "uid":  "TAK-RED-1",
        "type": "a-h-G-U-C-A",       # hostile, ground, armor
        "name": "RED-WOLF",
        "off":  (-300.0, +400.0),    # metres east/north of origin
        "speed_mps": 4.5,
        "course_deg": 90,
    },
    {
        "uid":  "TAK-NEUTRAL-7",
        "type": "a-n-G-U-C-F",       # neutral, ground, civilian
        "name": "AMBER-CIVIL",
        "off":  (+600.0, -200.0),
        "speed_mps": 1.2,
        "course_deg": 200,
    },
]

# -------------------- math --------------------
EARTH_R = 6_378_137.0

def metres_to_latlon(east_m, north_m):
    lat = ORIGIN_LAT + (north_m / EARTH_R) * (180.0 / math.pi)
    lon = ORIGIN_LON + (east_m / EARTH_R) * (180.0 / math.pi) / math.cos(math.radians(ORIGIN_LAT))
    return lat, lon

def utc_iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")

def build_cot_event(uid, cot_type, lat, lon, hae, callsign, group, course=0, speed=0):
    now   = datetime.now(timezone.utc)
    stale = now + timedelta(seconds=30)
    return (
        f'<?xml version="1.0"?>'
        f'<event version="2.0" uid="{uid}" type="{cot_type}"'
        f'   time="{utc_iso(now)}" start="{utc_iso(now)}" stale="{utc_iso(stale)}" how="m-g">'
        f'<point lat="{lat:.6f}" lon="{lon:.6f}" hae="{hae:.1f}" ce="9999999" le="9999999"/>'
        f'<detail>'
        f'<contact callsign="{callsign}"/>'
        f'<__group name="{group}" role=""/>'
        f'<status battery="100" readiness="true"/>'
        f'<track course="{course:d}" speed="{speed:.1f}"/>'
        f'<takv version="mock-0.1" platform="mock-tak-server"/>'
        f'</detail>'
        f'</event>'
    )

# -------------------- server --------------------
clients_lock = threading.Lock()
clients = set()

def broadcast(data, exclude=None):
    """Send to every connected client, except optional sender."""
    dead = []
    with clients_lock:
        for c in clients:
            if c is exclude:
                continue
            try:
                c.sendall(data)
            except OSError:
                dead.append(c)
        for c in dead:
            clients.discard(c)
            try: c.close()
            except OSError: pass

def handle_client(conn, addr):
    print(f"[mock-tak] + client {addr[0]}:{addr[1]}")
    with clients_lock:
        clients.add(conn)

    # CoT events arrive back-to-back. We accumulate in a buffer and parse
    # one <event>…</event> at a time. ElementTree.XMLPullParser handles
    # the streaming framing nicely.
    parser = ET.XMLPullParser(events=("end",))
    # Wrap incoming bytes in a synthetic root so XMLPullParser doesn't
    # complain about multiple top-level elements.
    parser.feed("<stream>")

    try:
        while True:
            chunk = conn.recv(8192)
            if not chunk:
                break
            text = chunk.decode("utf-8", errors="replace")
            # Strip XML declarations that Unity sends with every event.
            text = text.replace('<?xml version="1.0"?>', '')
            try:
                parser.feed(text)
            except ET.ParseError as e:
                print(f"[mock-tak]   parse error: {e}")
                continue

            for _evt, elem in parser.read_events():
                if elem.tag == "event":
                    uid  = elem.get("uid",  "?")
                    typ  = elem.get("type", "?")
                    point = elem.find("point")
                    contact = elem.find("detail/contact")
                    cs = contact.get("callsign", "") if contact is not None else ""
                    lat = point.get("lat", "?") if point is not None else "?"
                    lon = point.get("lon", "?") if point is not None else "?"
                    print(f"[mock-tak] ← {cs:<10} {uid:<24} {typ:<14} ({lat},{lon})")
                    # Echo to other clients so federation feels real.
                    broadcast(ET.tostring(elem, encoding="unicode").encode("utf-8"),
                              exclude=conn)
    except OSError as e:
        print(f"[mock-tak]   socket err on {addr}: {e}")
    finally:
        with clients_lock:
            clients.discard(conn)
        try: conn.close()
        except OSError: pass
        print(f"[mock-tak] - client {addr[0]}:{addr[1]}")

def build_request_event(uid, cot_type, lat, lon, callsign, group="Cyan"):
    """Build a CoT mission-request event (b-r-f-h-c, b-r-c-m) for use by an
    external client testing our ACCEPT/DENY HUD."""
    now   = datetime.now(timezone.utc)
    stale = now + timedelta(seconds=300)
    return (
        f'<?xml version="1.0"?>'
        f'<event version="2.0" uid="{uid}" type="{cot_type}"'
        f'   time="{utc_iso(now)}" start="{utc_iso(now)}" stale="{utc_iso(stale)}" how="h-g-i-g-o">'
        f'<point lat="{lat:.6f}" lon="{lon:.6f}" hae="0" ce="10" le="10"/>'
        f'<detail>'
        f'<contact callsign="{callsign}"/>'
        f'<__group name="{group}" role="FO"/>'
        f'<remarks>Test request from mock TAK Server</remarks>'
        f'<takv version="mock-0.1" platform="mock-tak-server"/>'
        f'</detail>'
        f'</event>'
    )

def synthetic_loop():
    """Periodically push fake foreign tracks so Unity has things to render."""
    t0 = time.time()
    request_idx = 0
    while True:
        time.sleep(2.0)
        if not clients:
            continue
        dt = time.time() - t0
        for t in SYNTHETIC:
            # Wiggle the track so it visibly moves on the map.
            east  = t["off"][0] + 30.0 * math.sin(dt * 0.1 + hash(t["uid"]) % 7)
            north = t["off"][1] + 30.0 * math.cos(dt * 0.1 + hash(t["uid"]) % 11)
            lat, lon = metres_to_latlon(east, north)
            xml = build_cot_event(
                uid=t["uid"], cot_type=t["type"],
                lat=lat, lon=lon, hae=0.0,
                callsign=t["name"], group="Red" if "-h-" in t["type"] else "Green",
                course=t["course_deg"], speed=t["speed_mps"]
            )
            broadcast(xml.encode("utf-8"))

        # Every ~20 seconds (10 ticks of 2s) emit a request event that
        # exercises the IncomingRequestPanel ACCEPT/DENY HUD.
        if int(dt) % 20 < 2:
            request_idx += 1
            if request_idx % 2 == 1:
                lat, lon = metres_to_latlon(+450.0, -250.0)
                xml = build_request_event(
                    uid=f"ATAK-BRAVO-CFF-{int(dt)}",
                    cot_type="b-r-f-h-c", lat=lat, lon=lon,
                    callsign="ATAK-BRAVO")
                print(f"[mock-tak] → injecting external CFF (ATAK-BRAVO)")
                broadcast(xml.encode("utf-8"))
            else:
                lat, lon = metres_to_latlon(-300.0, +350.0)
                xml = build_request_event(
                    uid=f"ATAK-CHARLIE-MED-{int(dt)}",
                    cot_type="b-r-c-m", lat=lat, lon=lon,
                    callsign="ATAK-CHARLIE")
                print(f"[mock-tak] → injecting external MEDEVAC (ATAK-CHARLIE)")
                broadcast(xml.encode("utf-8"))

def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind((HOST, PORT))
    except OSError as e:
        print(f"[mock-tak] FATAL: bind {HOST}:{PORT} failed: {e}", file=sys.stderr)
        sys.exit(1)
    sock.listen(16)
    print(f"[mock-tak] listening on {HOST}:{PORT}")
    print(f"[mock-tak] origin lat/lon = {ORIGIN_LAT}, {ORIGIN_LON}")
    print(f"[mock-tak] emitting {len(SYNTHETIC)} synthetic foreign track(s) every 2s")
    print(f"[mock-tak] press Ctrl-C to stop")

    threading.Thread(target=synthetic_loop, daemon=True, name="synthetic").start()

    try:
        while True:
            conn, addr = sock.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=handle_client, args=(conn, addr),
                             daemon=True, name=f"client-{addr[1]}").start()
    except KeyboardInterrupt:
        print("\n[mock-tak] shutting down")
    finally:
        try: sock.close()
        except OSError: pass

if __name__ == "__main__":
    main()
