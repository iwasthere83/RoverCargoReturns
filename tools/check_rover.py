"""Offline check of RoverAssets/rover.json (spec 2026-09-27): required keys, every mesh file, the 28 slot anchors
(names read from RoverRules.cs, the single source of the slot order), the named anchors, the carried-over wheel and
rover numbers, and the triangle budget.

    python check_rover.py <RoverAssets dir>      exit 1 with the problems listed
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RULES = open(os.path.join(HERE, "..", "RoverRules.cs"), encoding="utf-8").read()
SLOT_ANCHORS = re.findall(r'RoverSlotKind\.\w+, "(\w+)"\)', RULES)
NAMED = re.findall(r'"([\w.]+)"', RULES.split("NamedAnchors =")[1].split("};")[0])   # every name in the array
BUDGET = {"RoverBody": 30000, "RoverGlass": 600, "TyreL": 3000, "TyreR": 3000, "ArmL": 1000, "ArmR": 1000,
          "ShockBody": 600, "ShockRod": 600, "ShockSpring": 1200, "RoverGlow": 200,
          "RvArmour": 8000, "RvFairings": 4000, "RvThrusterPod": 800}   # the armour: its grilles' rounded rings
DOOR_BUDGET = 600
WHEEL_COLLIDER = {"m_Radius": 0.72, "m_SuspensionDistance": 1.0, "m_ForceAppPointDistance": 0.0, "m_Mass": 1.0, "m_WheelDampingRate": 0.25}


def main(assets):
    problems = []
    path = os.path.join(assets, "rover.json")
    if not os.path.exists(path):
        print("rover.json missing"); return 1
    j = json.load(open(path, encoding="utf-8"))
    for k in ("version", "body", "glass", "parts", "colliders", "autoCom", "mass", "wheelCollider", "wheels", "anchors", "fields"):
        if k not in j:
            problems.append(f"key {k} missing")
    if not j.get("glass", {}).get("mesh"):
        problems.append("glass mesh missing (cab glass not exported)")
    meshes = [j.get("body", {}).get("mesh"), j.get("glass", {}).get("mesh"), j.get("glow", {}).get("mesh")] + [p["mesh"] for p in j.get("parts", [])] \
        + [w[k] for w in j.get("wheels", []) for k in ("tyre", "arm")] + [s[k] for s in j.get("shocks", []) for k in ("body", "rod", "spring")] \
        + [e.get("mesh") for e in j.get("upgrades", {}).get("rover", [])]
    for mname in set(filter(None, meshes)):
        if not os.path.exists(os.path.join(assets, "meshes", mname + ".rcm")):
            problems.append(f"mesh {mname}.rcm missing")
        tris = j.get("meshStats", {}).get(mname, {}).get("triangles")
        cap = BUDGET.get(mname, DOOR_BUDGET if mname.startswith("BayDoor") else None)
        if cap and (tris is None or tris > cap):
            problems.append(f"mesh {mname}: {tris} triangles (budget {cap})")
    if len(SLOT_ANCHORS) != 28:
        problems.append(f"RoverRules.cs parse: {len(SLOT_ANCHORS)} slot anchors, expected 28")
    for a in SLOT_ANCHORS + NAMED:
        if a not in j.get("anchors", {}):
            problems.append(f"anchor {a} missing")
    wc = j.get("wheelCollider", {})
    for k, v in WHEEL_COLLIDER.items():
        if abs(wc.get(k, -99) - v) > 1e-4:
            problems.append(f"wheelCollider {k} = {wc.get(k)}, expected {v}")
    s = wc.get("m_SuspensionSpring", {})
    if (s.get("spring"), s.get("damper"), s.get("targetPosition")) != (2000.0, 200.0, 0.5):
        problems.append(f"suspension spring {s}")
    modes = sorted((w["pos"][2], w["mode"]) for w in j.get("wheels", []))
    if len(modes) != 6 or [md for _, md in modes] != [2, 2, 0, 0, 1, 1]:
        problems.append(f"wheels: {modes} (expected 6: rear 2, mid 0, front 1)")
    if j.get("mass") != 80.0 or abs(j.get("comHeight", 0) - 0.53) > 1e-4:
        problems.append(f"mass {j.get('mass')} / comHeight {j.get('comHeight')}")
    f = j.get("fields", {})
    for k, v in {"SteeringPower": 50.0, "MaxTurnAngle": 40.0, "MotorSpeed": 100.0, "BrakeSpeed": 100.0, "SteeringSpeed": 1.0}.items():
        if f.get(k) != v:
            problems.append(f"field {k} = {f.get(k)}, expected {v}")
    for p in problems:
        print(p)
    print(f"check_rover: {len(problems)} problems, {len(SLOT_ANCHORS)} slot anchors, {len(NAMED)} named anchors, {len(set(filter(None, meshes)))} meshes")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
