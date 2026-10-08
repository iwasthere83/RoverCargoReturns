"""Writes HabAssets/interior.json: the hab's furniture as game prefabs placed in trailer space (cloned from the loaded
prefabs at runtime - nothing shipped). Start: the user's reference room in save Mars2_2 (room centre 345,129,-265;
trailer (x, y, z) = (room z, 1.45 + room y + 1, -room x)), then fitted to the hab's walls and slide-out.
    python hab_interior.py <HabAssets dir>"""
import json, math, os, sys

# Root-mesh bounds of each prefab (centre, size) in its own space, read from the game's resources.assets (UnityPy).
MESH = {
    "StructureConsole": ((0.00, -0.02, 0.08), (0.42, 0.46, 0.25)),
    "StructureBatteryChargerSmall": ((0.00, -0.05, 0.12), (0.34, 0.39, 0.31)),
    "StructureGasMaskStorage": ((0.00, 0.00, 0.11), (0.50, 0.50, 0.22)),
    "StructureActiveVent": ((0.00, 0.00, 0.03), (0.50, 0.51, 0.29)),
    "StructureCircuitHousing": ((0.00, 0.00, 0.03), (0.50, 0.51, 0.14)),
    "StructureWaterBottleFillerPowered": ((0.00, 0.00, 0.08), (0.34, 0.50, 0.39)),
    "StructureFireExtinguisherStorage": ((0.00, 0.00, 0.11), (0.50, 0.50, 0.22)),
    "StructureBench1": ((0.00, 0.35, 0.01), (1.15, 0.81, 0.61)),
    "ApplianceMicrowave": ((0.00, 0.14, -0.01), (0.46, 0.28, 0.31)),
    "StructureSmallTableDinnerSingle": ((-0.25, 0.32, 0.25), (0.76, 0.63, 0.72)),
    "StructureChair": ((-0.25, 0.35, 0.26), (0.71, 0.70, 0.69)),
    "StructureStorageLocker": ((0.00, 0.94, 0.00), (1.50, 1.87, 0.54)),
    "StructurePassiveVentInsulated": ((0.00, 0.00, 0.02), (0.50, 0.50, 0.29)),
    "StructureSingleBed": ((0.75, 0.37, 0.25), (1.74, 0.74, 0.85)),
    "StructureLightRound": ((0.00, -0.03, 0.07), (0.39, 0.44, 0.20)),
    "StructureFlashingLight": ((0.00, -0.04, 0.07), (0.33, 0.42, 0.25)),
    # the console's switch node as it sits on the console (plate facing the console's -x), lever and all
    "SwitchOnOff": ((0.00, 0.00, 0.00), (0.05, 0.11, 0.07)),
    "StructureSuitStorage": ((0.00, 0.73, 0.00), (0.50, 1.70, 0.56)),
    "StructureShowerPowered/StructureShowerPowered/BuildState01": ((0.0, 0.838, 0.25), (0.511, 1.909, 1.0)),
    "StructureToiletModern/StructureToilet02": ((0.0, 0.195, 0.0), (0.5, 0.609, 0.5)),
    "WaterRack": ((0.0, 0.43, 0.0), (0.58, 0.86, 0.22)),
    "StatusScreen": ((0.0, 0.0, -0.015), (1.04, 0.72, 0.03)),  # L-local: x along the wall, z out of it (face at z 0)     # our own floor rack (R-local: x along the wall, z out of it)
    # a node's whole subtree, as posed by its nodeRot (the preview bake checks it against the game's meshes):
    # the tracker arm with its panel flat (user: fixed and flat on the roof)
    "StructureSolarPanel/SolarPanelArmBasic": ((0.00, 0.00, 0.26), (1.71, 1.71, 0.52)),
}
L, R, DOWN = (0, 90, 0), (0, -90, 0), (90, 0, 0)       # left wall faces +x, right wall faces -x, ceiling faces down
# name, prefab, trailer position (deployed), Unity euler, role
PROPS = [
    # rear, left wall (bunk side): the reference's two consoles and the charger, then the gas mask storage
    ("Console1", "StructureConsole", (-1.0, 2.45, -2.35), L, "console"),
    ("Console2", "StructureConsole", (-1.0, 2.45, -1.85), L, "console"),
    # the HAB STATUS dashboard: our own 1.04 x 0.72 display (1.00 x 0.68 glass) above the two consoles, at eye height
    # (the console glass is too small to read from the aisle); its face is 3 cm off the wall
    ("StatusScreen", "", (-0.97, 3.08, -2.15), L, "screen"),
    ("Charger", "StructureBatteryChargerSmall", (-1.0, 2.45, -1.35), L, "slots"),
    ("GasMask", "StructureGasMaskStorage", (-1.0, 2.45, -0.75), L, "slots"),
    # light switch by the door at hand height (1.15 m above the floor), just inside the door frame (z -2.79), its base
    # on the 12 mm wall plate (front face x -0.988). (0, 180, 0), not L: the switch sits on its console's left side, so
    # this turns its plate to face the room
    ("LightSwitch", "StructureConsole", (-0.979, 2.60, -2.70), (0, 180, 0), "switch"),
    # rear, right wall: vent and circuit housing under the life-support panel, then the bench with the microwave
    ("ActiveVent", "StructureActiveVent", (1.0, 2.45, -2.5), R, "prop"),
    ("Housing", "StructureCircuitHousing", (1.0, 2.52, -1.55), R, "console"),
    ("Bench", "StructureBench1", (0.685, 1.45, -1.32), R, "prop"),
    ("Microwave", "ApplianceMicrowave", (0.595, 2.08, -1.07), R, "prop"),   # on the bench's Slot1
    # middle, right side: dining set along the wall, bottle filler and fire extinguisher above it
    # right wall, where the dining set was: shower (behind the slide-out's stowed zone, which starts at z -0.2),
    # toilet, then the floor rack with the two liquid canisters; nodes are the game's finished-build meshes
    ("Shower", "StructureShowerPowered", (0.75, 1.446, -0.47), R, "shower"),   # sunk 12 cm: base just above the hull underside (1.32)
    ("Toilet", "StructureToiletModern", (0.75, 1.44, 0.40), R, "toilet"),     # tank to the wall; sunk 12 cm
    ("WaterRack", "", (0.89, 1.45, 1.50), R, "rack"),
    ("Filler", "StructureWaterBottleFillerPowered", (1.0, 2.45, 1.0), R, "slots"),
    ("FireExt", "StructureFireExtinguisherStorage", (1.0, 2.45, 0.3), R, "slots"),
    # front: locker on the front wall, passive vent high on the right
    ("Locker", "StructureStorageLocker", (0.25, 1.45, 2.73), (0, 180, 0), "locker"),
    # front-left corner between the slide-out and the locker, facing the rear (user 2026-09-26); its mesh reaches
    # 0.117 m below its origin (a grid-floor device), so the origin is that far above the hab floor
    ("SuitStorage", "StructureSuitStorage", (-0.745, 1.567, 2.715), (0, 180, 0), "suit"),
    ("PassiveVent", "StructurePassiveVentInsulated", (1.0, 2.95, 1.8), (0, -90, 180), "prop"),
    # slide-out bunks (deployed position; head to the rear, get out into the aisle)
    ("BunkLow", "StructureSingleBed", (-1.625, 1.72, 1.75), (0, 90, 0), "bunk"),
    ("BunkTop", "StructureSingleBed", (-1.625, 2.50, 1.75), (0, 90, 0), "bunk"),
    # ceiling
    ("Beacon", "StructureFlashingLight", (0.0, 3.45, -2.0), DOWN, "beacon"),
    ("LightA", "StructureLightRound", (0.0, 3.45, -1.0), DOWN, "light"),
    ("LightB", "StructureLightRound", (0.0, 3.45, 1.0), DOWN, "light"),
    # roof: the game's solar tracker heads (arm + panel, no pole or base), local z up, lying flat
    ("SolarFront", "StructureSolarPanel", (0.0, 3.645, 1.85), (-90, 0, 0), "roof"),
    ("SolarRear", "StructureSolarPanel", (0.0, 3.645, 0.05), (-90, 0, 0), "roof"),
]
# Slot-bearing props, in saved-slot order (append-only: saved games store slot contents by index).
SLOTS = [("BunkLow", 1), ("BunkTop", 1), ("Housing", 1), ("Charger", 2), ("GasMask", 1), ("FireExt", 1),
         ("SuitStorage", 3), ("Locker", 30), ("Filler", 2), ("WaterRack", 2)]
FIRST_INTERIOR_SLOT = 9          # after hab.json's 9 slots (Unused, 2 mounts, 2 tanks, 2 filters, 2 batteries)
# Copy only part of a prefab (node path) and override node rotations (Unity euler) inside the copy.
EXTRA = {
    "LightSwitch": {"node": "SwitchOnOff"},                      # the console's own switch lever
    # the waste-pipe fitting at the front bottom (prefab-local box around its ring): cut off, the hab has no pipes
    # (the shower's mesh is not readable in the game build, so it cannot be trimmed at runtime like the suit storage)
    "Shower": {"node": "StructureShowerPowered/BuildState01"},
    "Toilet": {"node": "StructureToilet02"},
    "WaterRack": {"rackSlots": [{"key": "CleanWater", "pos": [-0.14, 0.68, 0.0]}, {"key": "WasteWater", "pos": [0.14, 0.68, 0.0]}]},
    "SuitStorage": {"trim": [{"min": [-0.145, -0.125, 0.195], "max": [0.145, 0.125, 0.29]}]},
    "SolarFront": {"node": "SolarPanelArmBasic", "nodeRot": {"YawPivot/PitchPivot": (0, 0, 0)}},
    "SolarRear": {"node": "SolarPanelArmBasic", "nodeRot": {"YawPivot/PitchPivot": (0, 0, 0)}},
}


def quat(e):
    """Unity Euler (degrees) -> quaternion (x, y, z, w); Unity applies Z, then X, then Y."""
    def axis(a, deg):
        s, c = math.sin(math.radians(deg) / 2), math.cos(math.radians(deg) / 2)
        return tuple(s if i == a else 0.0 for i in range(3)) + (c,)
    return mul(mul(axis(1, e[1]), axis(0, e[0])), axis(2, e[2]))


def mul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def rotate(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def bounds(prefab, pos, q):
    (c, s) = MESH[prefab]
    pts = [rotate(q, (c[0] + dx * s[0], c[1] + dy * s[1], c[2] + dz * s[2]))
           for dx in (-.5, .5) for dy in (-.5, .5) for dz in (-.5, .5)]
    return ([pos[i] + min(p[i] for p in pts) for i in range(3)], [pos[i] + max(p[i] for p in pts) for i in range(3)])


# Pairs whose meshes may touch: an appliance standing on the bench, chairs tucked under the table.
ALLOWED = {("Bench", "Microwave")}


def slide_deployed(hab):
    part = next(p for p in hab["parts"] if p["name"] == "SlideOut")
    return [part["pos"][i] + hab["deploy"]["slideTravel"][i] for i in range(3)]


def build(hab):
    base = slide_deployed(hab)
    props = []
    for name, prefab, pos, euler, role in PROPS:
        parent = "SlideOut" if role == "bunk" else "hab"
        local = [round(pos[i] - (base[i] if parent == "SlideOut" else 0.0), 4) for i in range(3)]
        entry = {"name": name, "prefab": prefab, "parent": parent, "pos": local,
                 "rot": [round(v, 5) for v in quat(euler)], "role": role}
        base_slot = FIRST_INTERIOR_SLOT
        for sname, count in SLOTS:
            if sname == name:
                entry["slotBase"], entry["slotCount"] = base_slot, count
            base_slot += count
        extra = EXTRA.get(name, {})
        node = extra.get("node")
        key = "SwitchOnOff" if role == "switch" else name if role in ("rack", "screen") else (prefab + "/" + node if node else prefab)
        c, sz = MESH[key]
        entry["mesh"] = {"center": list(c), "size": list(sz)}   # for the offline checks
        if node:
            entry["node"] = node
        if "rackSlots" in extra:
            entry["rackSlots"] = extra["rackSlots"]
        if "trim" in extra:
            entry["trim"] = extra["trim"]
        if "nodeRot" in extra:
            entry["nodeRot"] = {k: [round(v, 5) for v in quat(e)] for k, e in extra["nodeRot"].items()}
        props.append(entry)
    return {"version": 1, "props": props, "allowedTouch": sorted(list(p) for p in ALLOWED)}


if __name__ == "__main__":
    d = sys.argv[1]
    hab = json.load(open(os.path.join(d, "hab.json"), encoding="utf-8"))
    out = build(hab)
    with open(os.path.join(d, "interior.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)
    print(f"interior.json: {len(out['props'])} props")
