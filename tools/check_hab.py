"""Offline check of HabAssets/hab.json: shell encloses a walkable room, doorway and stairs are clear, nothing hits
the tyres, and the hitch sits on the longer drawbar.   python check_hab.py <HabAssets dir>"""
import json, math, os, sys

d = sys.argv[1]
j = json.load(open(os.path.join(d, "hab.json"), encoding="utf-8"))
problems = []


def corners(c):
    cx, cy, cz = c["center"]; sx, sy, sz = (v / 2 for v in c["size"])
    rx, ry, rz = (math.radians(v) for v in c.get("rot", (0, 0, 0)))
    pts = []
    for dx in (-sx, sx):
        for dy in (-sy, sy):
            for dz in (-sz, sz):
                x, y, z = dx, dy, dz
                y, z = y * math.cos(rx) - z * math.sin(rx), y * math.sin(rx) + z * math.cos(rx)   # Unity X rotation
                x, z = x * math.cos(ry) + z * math.sin(ry), -x * math.sin(ry) + z * math.cos(ry)
                x, y = x * math.cos(rz) - y * math.sin(rz), x * math.sin(rz) + y * math.cos(rz)
                pts.append((cx + x, cy + y, cz + z))
    return pts


def aabb(c):
    p = corners(c)
    return [min(q[i] for q in p) for i in range(3)], [max(q[i] for q in p) for i in range(3)]


def hits(box, zone, eps=0.002):
    (mn, mx), (zmn, zmx) = box, zone
    return all(mn[i] < zmx[i] - eps and mx[i] > zmn[i] + eps for i in range(3))


boxes = [aabb(c) for c in j["colliders"]]
if j.get("hitch") != [0.0, 0.8, 4.45]:
    problems.append(f"hitch {j.get('hitch')} != [0.0, 0.8, 4.45]")
if j.get("bays") not in ([], None):
    problems.append("hab must not have cargo bays")
if not os.path.exists(os.path.join(d, "meshes", j["body"]["mesh"] + ".rcm")):
    problems.append("body mesh missing")
if "autoCom" not in j:
    problems.append("autoCom missing")
room = ([-0.95, 1.47, -2.95], [0.95, 3.43, 2.95])
door = ([-0.66, 1.47, -3.35], [0.66, 3.14, -2.85])
for name, zone in (("room", room), ("doorway", door)):
    for i, b in enumerate(boxes):
        if hits(b, zone):
            problems.append(f"collider {i} blocks the {name}: {b}")
floor = [b for b in boxes if abs(b[1][1] - 1.45) < 0.006 and b[0][0] <= -0.95 and b[1][0] >= 0.95 and b[0][2] <= -2.95 and b[1][2] >= 2.95]
if not floor:
    problems.append("no floor collider with its top at 1.45 under the whole room")
for name, test in (
        ("left wall (rear of the slide-out)", lambda b: b[1][0] <= -0.99 and b[0][1] <= 1.75 and b[1][1] >= 3.40 and b[0][2] <= -2.9 and b[1][2] >= -0.25),
        ("left wall (front of the slide-out)", lambda b: b[1][0] <= -0.99 and b[0][1] <= 1.75 and b[1][1] >= 3.40 and b[0][2] <= 2.25 and b[1][2] >= 2.9),
        ("right wall", lambda b: b[0][0] >= 0.99 and b[0][1] <= 1.75 and b[1][1] >= 3.40 and b[0][2] <= -2.9 and b[1][2] >= 2.9),
        ("roof", lambda b: b[0][1] >= 3.44 and b[0][0] <= -0.99 and b[1][0] >= 0.99 and b[0][2] <= -2.9 and b[1][2] >= 2.9),
        ("front wall", lambda b: b[0][2] >= 2.99 and b[0][0] <= -0.99 and b[1][0] >= 0.99 and b[0][1] <= 1.5 and b[1][1] >= 3.4)):
    if not any(test(b) for b in boxes):
        problems.append(f"no collider for the {name}")
def part(name):
    return next((p for p in j.get("parts", []) if p["name"] == name), None)


def shifted(c, off):
    return dict(c, center=[c["center"][i] + off[i] for i in range(3)])


if any("rot" in c for c in j["colliders"]):
    problems.append("static colliders must not include the stairs (the ladder part carries them)")
dep = j.get("deploy", {})
for key in ("slideTravel", "ladderStowEuler", "legs", "legStowH", "legMaxStroke", "panel"):
    if key not in dep:
        problems.append(f"deploy.{key} missing")
lad = part("Ladder")
if not lad:
    problems.append("no Ladder part")
else:
    ramps = [c for c in lad["colliders"] if c.get("name") == "Stairs"]
    if len(ramps) != 1:
        problems.append("Ladder needs exactly one collider named Stairs")
    else:
        top = max(corners(shifted(ramps[0], lad["pos"])), key=lambda p: p[1])
        if abs(top[1] - 1.45) > 0.02 or not -3.45 <= top[2] <= -3.19:
            problems.append(f"deployed ladder top {top} does not meet the floor edge (h 1.45, z -3.2..-3.45)")
        if min(p[1] for p in corners(shifted(ramps[0], lad["pos"]))) < 0.25:
            problems.append("deployed ladder foot below 0.25 m")
so = part("SlideOut")
if not so:
    problems.append("no SlideOut part")
else:
    travel = dep.get("slideTravel", [0, 0, 0])
    for i, c in enumerate(so["colliders"]):
        mn, mx = aabb(shifted(c, so["pos"]))
        if mx[0] > -0.38 and mn[1] < 3.30 and mx[1] > 1.72:
            problems.append(f"retracted slide-out collider {i} reaches into the walkway (x > -0.38)")
        dmn, dmx = aabb(shifted(c, [so["pos"][k] + travel[k] for k in range(3)]))
        for s_z in (1.7, 0.0, -1.7):
            if hits((dmn, dmx), ([-1.8, 0.0, s_z - 0.72], [-1.114, 1.62, s_z + 0.72])):
                problems.append(f"deployed slide-out collider {i} overlaps the left tyre at z {s_z}")
    opening = ([-1.16, 1.74, -0.15], [-0.99, 3.28, 2.15])
    for i, b in enumerate(boxes):
        if hits(b, opening):
            problems.append(f"static collider {i} blocks the slide-out opening: {b}")
legs = [part(n) for n in dep.get("legs", [])]
if len(legs) != 4 or None in legs:
    problems.append("expected 4 leg parts")
if not dep.get("panel") or not any(c.get("name") == dep["panel"] for c in j["colliders"]):
    problems.append("no static collider named after deploy.panel")
for s in (1, -1):
    for z in (1.7, 0.0, -1.7):
        tyre = ([min(s * 1.114, s * 1.8), 0.0, z - 0.72], [max(s * 1.114, s * 1.8), 1.62, z + 0.72])
        for i, b in enumerate(boxes):
            if hits(b, tyre):
                problems.append(f"collider {i} overlaps the tyre zone at x {s * 1.46}, z {z}")
d = j.get("door")
if not d or d.get("prefab") != "StructureCompositeDoor" or len(d.get("pos", [])) != 3:
    problems.append("door block missing (prefab StructureCompositeDoor, pos, leafTravel, trigger)")
elif abs(d["pos"][1] + 0.1 - 0.855 - 1.45) > 0.01:
    problems.append(f"door sill {d['pos'][1] + 0.1 - 0.855:.3f} is not at the floor (1.45)")
names = {s["name"]: s for s in j.get("slots", [])}
for want, typ in (("AirSupply", "Tank"), ("Waste", "Tank"), ("Filter1", "GasFilter"), ("Filter2", "GasFilter"),
                  ("Battery1", "Battery"), ("Battery2", "Battery")):
    if names.get(want, {}).get("type") != typ:
        problems.append(f"slot {want} ({typ}) missing")
for s in ("Filter1", "Filter2", "Battery1", "Battery2"):
    if s in names and "trigger" not in names[s]:
        problems.append(f"slot {s} needs a trigger box (hand interaction)")
cr = {c["label"]: c for c in j.get("cradles", [])}
if set(cr) != {"AIR SUPPLY", "WASTE"}:
    problems.append("cradles AIR SUPPLY and WASTE missing")
if not 20000 <= j.get("cabinVolume", 0) <= 30000:
    problems.append("cabinVolume should be about 26800 L (room + slide-out)")
if problems:
    print("\n".join(problems)); sys.exit(1)
print("hab ok")
