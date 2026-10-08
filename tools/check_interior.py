"""Offline check of HabAssets/interior.json against hab.json: every prop inside the hull, nothing overlapping, the
stowed slide-out's space kept free, a walkable aisle from the door to the locker (deployed and stowed), door and
life-support panel clear, bunks inside the slide-out.   python check_interior.py <HabAssets dir>"""
import json, math, os, sys

d = sys.argv[1]
hab = json.load(open(os.path.join(d, "hab.json"), encoding="utf-8"))
path = os.path.join(d, "interior.json")
if not os.path.exists(path):
    sys.exit("FAIL: interior.json missing (run tools/hab_interior.py)")
j = json.load(open(path, encoding="utf-8"))
problems = []
KNOWN = {"StructureConsole", "StructureBatteryChargerSmall", "StructureGasMaskStorage", "StructureActiveVent",
         "StructureCircuitHousing", "StructureWaterBottleFillerPowered", "StructureFireExtinguisherStorage",
         "StructureBench1", "ApplianceMicrowave", "StructureSmallTableDinnerSingle", "StructureChair",
         "StructureStorageLocker", "StructurePassiveVentInsulated", "StructureSingleBed", "StructureLightRound",
         "StructureFlashingLight", "StructureSolarPanel", "StructureSuitStorage",
         "StructureShowerPowered", "StructureToiletModern"}


def rotate(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


part = next((p for p in hab["parts"] if p["name"] == "SlideOut"), None)
travel = hab["deploy"]["slideTravel"]
home = part["pos"]
deployed_base = [home[i] + travel[i] for i in range(3)]


def aabb(p, stowed=False):
    c, s = p["mesh"]["center"], p["mesh"]["size"]
    pts = [rotate(p["rot"], (c[0] + dx * s[0], c[1] + dy * s[1], c[2] + dz * s[2]))
           for dx in (-.5, .5) for dy in (-.5, .5) for dz in (-.5, .5)]
    base = [0.0, 0.0, 0.0]
    if p["parent"] == "SlideOut":
        base = home if stowed else deployed_base
    pos = [p["pos"][i] + base[i] for i in range(3)]
    return [pos[i] + min(q[i] for q in pts) for i in range(3)], [pos[i] + max(q[i] for q in pts) for i in range(3)]


def hits(a, b, eps=0.005):
    return all(a[0][i] < b[1][i] - eps and a[1][i] > b[0][i] + eps for i in range(3))


def inside(a, lo, hi, eps=0.005):
    return all(a[0][i] >= lo[i] - eps and a[1][i] <= hi[i] + eps for i in range(3))


props = j.get("props", [])
names = [p["name"] for p in props]
if len(set(names)) != len(names):
    problems.append("duplicate prop names")
for p in props:
    if p["prefab"] not in KNOWN and not (p["role"] in ("rack", "screen") and p["prefab"] == ""):
        problems.append(f"{p['name']}: unknown prefab {p['prefab']}")
    if p["parent"] not in ("hab", "SlideOut"):
        problems.append(f"{p['name']}: parent must be hab or SlideOut")
roles = [p["role"] for p in props]
for role, want in (("bunk", 2), ("beacon", 1), ("switch", 1), ("locker", 1), ("shower", 1), ("toilet", 1), ("rack", 1)):
    if roles.count(role) != want:
        problems.append(f"need {want} {role} prop(s), found {roles.count(role)}")
if roles.count("light") < 2:
    problems.append("need at least 2 ceiling lights")
# Saved games store slot contents by index: this table only ever grows. Bunks 9-10 are from 4a.
SAVED_SLOTS = {"BunkLow": (9, 1), "BunkTop": (10, 1), "Housing": (11, 1), "Charger": (12, 2), "GasMask": (14, 1),
               "FireExt": (15, 1), "SuitStorage": (16, 3), "Locker": (19, 30),
               "Filler": (49, 2), "WaterRack": (51, 2)}
got = {p["name"]: (p["slotBase"], p["slotCount"]) for p in props if "slotBase" in p}
for name, want in SAVED_SLOTS.items():
    if got.get(name) != want:
        problems.append(f"{name}: slots {got.get(name)} but saved games expect {want}")
for p in props:
    if p["role"] == "rack" and len(p.get("rackSlots", [])) != p.get("slotCount"):
        problems.append(f"{p['name']}: {len(p.get('rackSlots', []))} rack slots listed but slotCount {p.get('slotCount')}")
spans = sorted(got.values())
for (a, n), (b, _) in zip(spans, spans[1:]):
    if a + n != b:
        problems.append(f"slot indices not contiguous at {a + n} (next starts at {b})")
if spans and spans[0][0] != 9:
    problems.append(f"first interior slot is {spans[0][0]}, must be 9 (after the 9 hab.json slots)")

box = {p["name"]: aabb(p) for p in props}
# hull: the room (x -1..1, h 1.45..3.45, z -2.8..3.0 in front of the door frame) with wall/floor/ceiling recesses up to
# the outer skin; bunks inside the deployed slide-out (x -1.80..-0.95, h 1.72..3.30, z -0.16..2.16)
for p in props:
    a = box[p["name"]]
    if p["role"] == "bunk":
        if p["parent"] != "SlideOut" or not inside(a, (-1.80, 1.72, -0.16), (-0.95, 3.30, 2.16)):
            problems.append(f"{p['name']} is not inside the deployed slide-out: {a}")
    elif p["role"] == "roof":   # on the flat roof: inside the shoulders, behind the front facet, ahead of the AC unit
        if not inside(a, (-0.95, 3.60, -1.62), (0.95, 4.40, 2.95)):
            problems.append(f"{p['name']} is off the roof: {a}")
    # the shower and toilet sink into the floor to hide their plugs, down to just above the hull's underside (1.32)
    elif not inside(a, (-1.13, 1.33 if p["role"] in ("shower", "toilet") else 1.39, -2.80), (1.13, 3.55, 3.00)):
        problems.append(f"{p['name']} pokes out of the hull: {a}")
allowed = {tuple(x) for x in j.get("allowedTouch", [])}
for i, p in enumerate(props):
    for q in props[i + 1:]:
        if (p["name"], q["name"]) in allowed or (q["name"], p["name"]) in allowed:
            continue
        if hits(box[p["name"]], box[q["name"]]):
            problems.append(f"{p['name']} overlaps {q['name']}")
# the stowed slide-out box (its colliders at home) must find the room empty
env = None
for c in part.get("colliders", []):
    lo = [home[i] + c["center"][i] - c["size"][i] / 2 for i in range(3)]
    hi = [home[i] + c["center"][i] + c["size"][i] / 2 for i in range(3)]
    env = (lo, hi) if env is None else ([min(env[0][i], lo[i]) for i in range(3)], [max(env[1][i], hi[i]) for i in range(3)])
for p in props:
    if p["parent"] == "hab" and env and hits(box[p["name"]], env):
        problems.append(f"{p['name']} is in the stowed slide-out's space {env}")
    if p["parent"] == "SlideOut":
        for q in props:
            if q["parent"] == "hab" and hits(aabb(p, stowed=True), box[q["name"]]):
                problems.append(f"stowed {p['name']} overlaps {q['name']}")
# door approach and life-support panel stay clear
for zone_name, zone in (("door approach", ((-0.66, 1.46, -2.80), (0.66, 3.14, -2.30))),
                        ("life-support panel", ((0.80, 2.73, -2.66), (1.00, 3.27, -1.84)))):
    for p in props:
        if hits(box[p["name"]], zone):
            problems.append(f"{p['name']} blocks the {zone_name}")


# the locker's doors swing 0.36 m out of its face (bifold, hinges at locker x +/-0.708): keep that clear
for p in props:
    if p["role"] == "locker":
        (x, y, z) = p["pos"]
        for hx in (x - 0.708, x + 0.708):
            zone = ((hx - 0.03, y + 0.18, z - 0.259 - 0.37), (hx + 0.03, y + 1.82, z - 0.259))
            for q in props:
                if q is not p and hits(box[q["name"]], zone):
                    problems.append(f"{q['name']} is in the locker door's swing")


def aisle(stowed):
    """Narrowest free walking width (body height h 1.5..2.2) from the door (z -2.8) to the locker, x -1..1."""
    blockers = [aabb(p, stowed) for p in props]
    if stowed and env:
        blockers.append(env)
    worst = (9.0, None)
    z = -2.8
    front = min((b[0][2] for b in blockers if b[0][2] > 2.0 and b[0][0] < 0.0 < b[1][0]), default=3.0)
    while z < front - 0.05:
        spans = sorted((max(-1.0, b[0][0]), min(1.0, b[1][0])) for b in blockers
                       if b[0][2] < z < b[1][2] and b[0][1] < 2.2 and b[1][1] > 1.5)
        best, x = 0.0, -1.0
        for lo, hi in spans:
            best = max(best, lo - x); x = max(x, hi)
        best = max(best, 1.0 - x)
        if best < worst[0]:
            worst = (best, z)
        z += 0.05
    return worst


for stowed, need in ((False, 0.9), (True, 0.55)):     # stowed = towing mode: the bunks ride in the room
    w, z = aisle(stowed)
    if w < need:
        problems.append(f"aisle only {w:.2f} m wide at z {z:.2f} ({'stowed' if stowed else 'deployed'})")
    else:
        print(f"aisle {'stowed' if stowed else 'deployed'}: narrowest {w:.2f} m at z {z:.2f}")

if problems:
    print("FAIL:\n  " + "\n  ".join(problems))
    sys.exit(1)
print(f"OK: {len(props)} props")
