"""Export the original Cargo Rover (tools/blender_rover_model.py, blender_rover_wheels.py) to RoverAssets/:
meshes/*.rcm and rover.json (Unity axes, rover-local, origin on the ground under the rover). Original work: ships with
the mod. Colours are palette UV spots on the game's ColorWhite texture (sampled from the 2020 color_white atlas; check
them in game at the switch-over - one table, ROLE_UV).
"""
import importlib
import json
import os
import sys

import bpy
from mathutils import Vector

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)
import blender_build_trailer as bt  # noqa: E402
import blender_rover_io as io  # noqa: E402

ROLE_UV = dict(bt.ROLE_UV, **{
    "silver": ("ColorWhite", (0.8906, 0.8594)),       # the paintable body (blender_build_trailer.ROLE_UV)
    "steel": ("ColorWhite", (0.03125, 0.46875)),      # (195, 199, 200)
    "charcoal": ("ColorWhite", (0.03125, 0.40625)),   # (61, 60, 61)
    "gunmetal": ("ColorWhite", (0.15625, 0.40625)),   # (70, 70, 70)
    "orange": ("ColorWhite", (0.15625, 0.34375)),     # (198, 65, 0)
})
WHEEL_COLLIDER = {
    "m_Center": {"x": 0.0, "y": 0.5, "z": 0.0}, "m_Radius": 0.72, "m_Mass": 1.0, "m_WheelDampingRate": 0.25,
    "m_SuspensionSpring": {"spring": 2000.0, "damper": 200.0, "targetPosition": 0.5}, "m_SuspensionDistance": 1.0,
    "m_ForceAppPointDistance": 0.0, "m_Enabled": True,
    "m_ForwardFriction": {"m_ExtremumSlip": 0.4, "m_ExtremumValue": 1.0, "m_AsymptoteSlip": 0.8, "m_AsymptoteValue": 0.5, "m_Stiffness": 3.0},
    "m_SidewaysFriction": {"m_ExtremumSlip": 0.2, "m_ExtremumValue": 1.0, "m_AsymptoteSlip": 0.5, "m_AsymptoteValue": 0.75, "m_Stiffness": 1.0},
}
FIELDS = {"SteeringPower": 50.0, "MaxTurnAngle": 40.0, "MotorSpeed": 100.0, "BrakeSpeed": 100.0, "SteeringSpeed": 1.0,
          "ThingHealth": 3578.0854, "SurfaceArea": 102.0692, "Volume": 50.0, "PressurePerTick": 101.325,
          "WasteMaxPressure": 4053.0, "MaxEnergy": 12000.0, "OutputSetting": 101.325, "OutputTemperature": 293.15,
          "WorkLightSpot": 76.0, "HeadlightSpot": 60.0, "LightBarSpot": 30.0}   # the spot lights' cones (blender_rover_checks.light_spill;
          # work lights 100 -> 76 deg: the user, check-in 1 of the upgrades, so they stop lighting the fender flares under them)
WHEEL_NAMES = {"Front": "WheelFrontTire{}", "Mid": "TireMid{}", "Rear": "WheelRearTire{}"}   # Mk I audio keys by name
MASS, COM_HEIGHT = 80.0, 0.53


def _parts(coll):
    col = bpy.data.collections.get(coll)
    return {o.name: o["role"] for o in (col.objects if col else []) if o.type == "MESH" and "role" in o}


def collider_boxes(m):
    """Solid box colliders (rover-local Unity centre/size) inside the one hull (spec 2026-09-28): the tucked skirt, the
    leaning sides, the cab's corner facets and the windscreen stepped (each step's outer face on the hull at its narrow
    end, so no box reaches out of the body); the bays, the slot recesses, the canister rack and the wheel arches
    (|x| >= ARCH_IN) stay open (the game's cursor is one raycast: a solid box in front of a trigger hides it). Both
    seated heads (the game's seated camera is the helmet, near the camera anchors), every dash tap target and the seats'
    click boxes' inner faces are inside ONE box, the lower cab (a ray starting in a collider ignores it but hits any
    other). The boxes do not overlap (autoCom weighs each once)."""
    boxes = []

    def add(lo, hi):
        if all(b > a + 1e-4 for a, b in zip(lo, hi)):
            boxes.append({"center": [round((a + b) / 2, 4) for a, b in zip(lo, hi)], "size": [round(b - a, 4) for a, b in zip(lo, hi)]})

    def span(x0, x1, h0, h1, z0, z1):
        """|x| x0 .. x1 on both sides, or one box straight across when x0 is 0."""
        if x0 <= 0:
            add((-x1, h0, z0), (x1, h1, z1))
            return
        for s in (1, -1):
            xa, xb = sorted((s * x0, s * x1))
            add((xa, h0, z0), (xb, h1, z1))

    X, B, T, A, R = m.UPPER_X, m.HULL_BOT, m.TUCK[0], m.ARCH_H, m.MODULE_ROOF
    MC, CC = m.MOD_CREASE, m.CAB_CREASE
    mid = (B + T) / 2
    mod_steps = [MC + (R - MC) * k / 3 for k in range(4)]                      # the storage module's 45 deg lean
    cab_steps = [2.62, 2.80, 2.98, R]                                          # the cab's 35 deg lean over the lower cab

    def skirt(x0, z0, z1, top=T, cap=X):
        """|x| x0 .. the tucked lower hull, HULL_BOT to top in two steps (their outer faces on the tuck at their feet)."""
        for lo, hi in ((B, mid), (mid, top)):
            span(x0, min(m.tuck_x(lo), cap), lo, hi, z0, z1)

    def module_top(x0, z0, z1, h0=T, h1=R, cap=X):
        """|x| x0 .. the side, h0 to h1, behind the cab step: upright to the crease, then the lean in three steps (their
        outer faces on the lean at their tops); no wider than cap."""
        if h0 < MC:
            span(x0, min(X, cap), h0, min(h1, MC), z0, z1)
        for a, b in zip(mod_steps, mod_steps[1:]):
            lo, hi = max(a, h0), min(b, h1)
            if hi > lo:
                span(x0, min(m.lean_x(hi, m.MOD_LEAN), cap), lo, hi, z0, z1)

    (zf, _), (zm, zr) = m.arches()
    t_f, t_r, f_r, f_f = zm + m.ARCH_REACH, zr - m.ARCH_REACH, zf - m.ARCH_REACH, zf + m.ARCH_REACH   # the arches' ends
    zp, zq, zc, rz = m.BAYS[0][1], m.BAYS[-1][2], m.CAB_STEP_AT, m.REAR_Z      # the bays' front and rear, the cab step
    # under the hull: the keel (its sides chamfered below 0.74), the rear bumper and the receiver box, the front bumper,
    # the battery packs and the air tanks
    add((-m.KEEL_X, 0.74, m.BUMPER["z"][0]), (m.KEEL_X, B, m.NOSE_Z))
    add((-0.41, m.BELLY, m.BUMPER["z"][0] + 0.14), (0.41, 0.74, m.NOSE_Z - 0.18))
    add((-(m.BUMPER["x"] - 0.06), m.BUMPER["h"][0] + 0.06, m.BUMPER["z"][1]), (m.BUMPER["x"] - 0.06, B, m.BUMPER["z"][0]))
    add((-m.HITCH_BOX[0], m.HITCH_BOX[1], m.BUMPER["z"][1] - m.HITCH_BOX[3]), (m.HITCH_BOX[0], m.HITCH_BOX[2], m.BUMPER["z"][1]))
    add((-0.94, 0.86, f_f), (0.94, B, 3.44))
    add((-0.86, 0.90, 3.44), (0.86, 1.22, 3.53))                               # the front bumper's nose (its face leans out)
    add((-0.28, 0.92, 3.53), (0.28, 1.14, 3.60))                               # the winch
    hz = m.HITCH[2]                                                            # the tow arm, back to the jaw, 17 cm short of the pin
    add((-0.095, 0.68, hz + 0.17), (0.095, 0.775, m.BUMPER["z"][1] - m.HITCH_BOX[3]))   # (a hitched tongue's collider starts behind its ring)
    span(m.FRAME_X, 0.95, 0.90, B, -0.50, 0.40)
    span(0.70, 0.90, 1.00, 1.20, 0.55, 1.25)
    # the tail: behind the tandem arch (its rear corners chamfered 18 cm), over the arch's rear end, the pods' overhang
    skirt(0, rz, t_r, cap=X - 0.18)
    module_top(0, rz, t_r, cap=X - 0.18)
    span(0, m.ARCH_IN, B, T, t_r, zq)
    module_top(0, t_r, zq)
    span(0.12, 1.28, 2.10, MC, rz - 0.12, rz)
    # the bays: the spine, the floors (inboard of the tandem arch, over it, the skirt forward of it), the roofs, the divider
    fl = m.BAY_FLOOR - 0.01                                                    # 1 cm under the crates
    span(0, m.BAY_IN_X, B, R, zq, zp)
    span(m.BAY_IN_X, m.ARCH_IN, B, fl, zq, t_f)
    span(m.ARCH_IN, m.tuck_x(A), A, fl, zq, t_f)
    skirt(m.BAY_IN_X, t_f, zp, top=fl)
    for lo, hi in ((m.BAY_TOP, (m.BAY_TOP + R) / 2), ((m.BAY_TOP + R) / 2, R)):
        span(m.BAY_IN_X, m.lean_x(hi, m.MOD_LEAN), lo, hi, zq, zp)
    module_top(m.BAY_IN_X, m.BAYS[1][1], m.BAYS[0][2], h0=fl, h1=m.BAY_TOP)
    # the slot panel section (bay 1 .. the cab step): round the canister rack and the recesses
    k, (rz0, rz1), (rh0, rh1), sx = m.RACK, m.RECESS_Z, m.SLOT_H, m.SLOT_BACK_X
    skirt(0, zp, zc)
    span(0, X, T, rh0, zp, zc)
    add((-k["x"], rh0, zp), (sx, rh1, zc))                                                 # between the rack and the right recess
    add((-sx, rh0, zp), (-k["x"], k["h"][0], zc))                                         # under the rack
    add((-sx, k["h"][1], zp), (-k["x"], rh1, zc))                                         # over it
    add((-sx, k["h"][0], zp), (-k["x"], k["h"][1], k["z"][0]))                            # behind it
    add((-sx, k["h"][0], k["z"][1]), (-k["x"], k["h"][1], zc))                            # in front of it
    for z0, z1 in ((zp, rz0), (rz1, zc)):                                                  # the walls round the recesses
        module_top(sx, z0, z1, h0=rh0, h1=rh1)
    module_top(0, zp, zc, h0=rh1)
    # the cab: the skirt behind the front arch, the belly between the wheel wells, the lower cab (both seated heads, the
    # dash and the seats' click boxes' inner faces: ONE box, up to the lean at the windscreen's z; the game's seated
    # camera is the helmet, near the camera anchors but not on them, review I1), beside it under the lean and the corner
    # facets, over it in steps under the lean and the windscreen, in front of it the dash and the nose, the snout
    (cx0, cz0), (cx1, cz1) = m.CORNER
    corner_x = lambda z: X if z <= cz0 else cx0 + (cx1 - cx0) * (z - cz0) / (cz1 - cz0)
    ws = lambda h: m.NOSE_Z - (h - 1.95) * (m.NOSE_Z - m.WINDSCREEN[0][0]) / (m.CAB_ROOF - 1.95)   # the windscreen's z at h
    lh = cab_steps[0]                                                          # the lower cab's top, 26 cm over the camera anchors
    lz = ws(lh)                                                                # its front (past the tap targets): the windscreen at lh
    lx = min(corner_x(lz), m.lean_x(lh, m.CAB_LEAN))                           # its width: the lean at lh, the corner facets at lz
    skirt(m.ARCH_IN, zc, f_r)
    span(0, m.ARCH_IN, B, T, zc, m.NOSE_Z)
    add((-lx, T, zc), (lx, lh, lz))
    zmid = (cz0 + lz) / 2
    for lo, hi in ((T, CC), (CC, 2.45), (2.45, lh)):                           # beside it: behind the corner facets, along them
        side = X if hi <= CC else m.lean_x(hi, m.CAB_LEAN)
        span(lx, side, lo, hi, zc, cz0)
        span(lx, min(corner_x(zmid), side), lo, hi, cz0, zmid)
    for lo, hi in zip(cab_steps, cab_steps[1:]):
        xl, zw = m.lean_x(hi, m.CAB_LEAN), ws(hi)
        if corner_x(zw) >= xl:
            span(0, xl, lo, hi, zc, zw)
        else:
            span(0, xl, lo, hi, zc, cz0)
            span(0, corner_x(zw), lo, hi, cz0, zw)
    for lo, hi in ((T, 1.95), (1.95, 2.20), (2.20, 2.45)):
        z_end = min(m.NOSE_Z, ws(hi))
        n = max(1, round((z_end - lz) / 0.16))
        for j in range(n):
            z0, z1 = lz + (z_end - lz) * j / n, lz + (z_end - lz) * (j + 1) / n
            span(0, min(corner_x(z1), m.lean_x(hi, m.CAB_LEAN)), lo, hi, z0, z1)
    add((-corner_x(3.20), B, m.NOSE_Z), (corner_x(3.20), mid, 3.20))
    return boxes


def auto_com3(colliders):
    vol = [c["size"][0] * c["size"][1] * c["size"][2] for c in colliders]
    return [round(sum(v * c["center"][k] for v, c in zip(vol, colliders)) / sum(vol), 4) for k in range(3)]


def export_upgrades(export):
    """rover.json "upgrades": {"rover": [...]} in trailer.json's format (CargoPrefabs.AddUpgradeParts): the armour and
    the fixed fairings (skirts, bumper corners, fender flares) one mesh each at the rover's origin; one thruster pod mesh
    (modelled at pod 0) placed at Nozzle0-3. No riding fenders on the rover (check-in 1: fixed flares). Removal
    colliders: each part's islands, less any that
    touch a keep-out (the bay doors, the slot recesses, the cab doors' entry zone)."""
    import blender_rover_upgrades as bu
    keep = bu.keep_out()
    objs = lambda cn: [o for o in bpy.data.collections[cn].objects if o.type == "MESH" and "role" in o]
    entries = []
    for group, cn, mesh in (("Armour", bu.ARMOUR, "RvArmour"), ("Fairings", bu.FAIRINGS, "RvFairings")):
        if objs(cn):
            mats = export(_parts(cn), mesh, Vector((0, 0, 0)))
            cols = bu.boxes_json([o for o in objs(cn) if not o.name.startswith("RVU_Grille")], (0.0, 0.0, 0.0), keep)
            if cn == bu.ARMOUR:                                      # one oriented box per window grille (AddBoxColliders' "rot")
                cols += [{"center": [round(v, 4) for v in c], "size": [round(v, 4) for v in s], "rot": rot}
                         for c, s, rot, axes in bu.grille_boxes() if not any(bu.obb_touches(c, s, axes, k) for k in keep)]
            entries.append({"group": group, "name": mesh, "mesh": mesh, "materials": mats, "colliders": cols})
    pods = bu.pod_positions()
    if objs(bu.THRUSTERS):
        x, h, z = pods[0]
        mats = export(_parts(bu.THRUSTERS), "RvThrusterPod", Vector((x, z, h)))
        boxes = bu.boxes_json(objs(bu.THRUSTERS), pods[0], [])
        for k, pos in enumerate(pods):
            entries.append({"group": "Thrusters", "name": f"Nozzle{k}", "mesh": "RvThrusterPod", "materials": mats,
                            "colliders": boxes, "pos": [round(v, 4) for v in pos]})
    return {"rover": entries}


def build(out_dir):
    import blender_rover_model as m
    importlib.reload(m)
    try:
        import blender_rover_wheels as w
        importlib.reload(w)
    except ImportError:
        w = None
    import blender_rover_upgrades as bu
    importlib.reload(bu)
    m.build()
    if w:
        w.build()
    bu.build()
    import blender_build_stages as bs
    importlib.reload(bs)
    bs.scaffold("rover")                                                    # the frame's build stages (sub-project 5)
    os.makedirs(os.path.join(out_dir, "meshes"), exist_ok=True)
    col = bpy.data.collections.get("EXP_Rover") or bpy.data.collections.new("EXP_Rover")
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    for ob in list(col.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    stats = {}

    def export(parts, name, pivot):
        ob, mats = bt._combine(parts, name, pivot, ROLE_UV)
        col.objects.link(ob); ob.hide_set(True); ob.hide_render = True
        stats[name] = io.export_mesh(ob, os.path.join(out_dir, "meshes", name + ".rcm"))
        return mats

    body_mats = export(_parts("GEO_RoverBody"), "RoverBody", Vector((0, 0, 0)))
    glass_parts = _parts("GEO_RoverGlass")
    if glass_parts:
        export(glass_parts, "RoverGlass", Vector((0, 0, 0)))
    glow_parts = _parts("GEO_RoverGlow")                                     # the front lamps' lit lenses (with the headlights)
    if glow_parts:
        export(glow_parts, "RoverGlow", Vector((0, 0, 0)))
    parts = []
    for o in sorted(bpy.data.collections["GEO_RoverDoors"].objects, key=lambda o: o.name):
        if o.type != "MESH" or "role" not in o or o.parent is not None:     # leaves only; stripes go with their leaf
            continue
        piv = o.matrix_world.translation.copy()
        mats = export({o.name: o["role"]} | {c.name: c["role"] for c in o.children if "role" in c}, o.name, piv)
        entry = {"name": o.name, "mesh": o.name, "materials": mats, "pos": [round(piv.x, 4), round(piv.z, 4), round(piv.y, 4)]}
        if "hinge" in o:                                                      # the bay door's hinge line (the builder turns it)
            entry["hinge"] = [round(float(v), 4) for v in o["hinge"]]
        parts.append(entry)                                                   # (the leaves' colliders are triggers: no mass)
    tyres = {}
    for n in ("TyreL", "TyreR", "ArmL", "ArmR", "ShockBody", "ShockRod", "ShockSpring"):
        o = bpy.data.objects.get(n)
        if o is not None and "role" in o:
            hidden = o.hide_get()                                             # the shock parts are hidden in the scene:
            o.hide_set(False)                                                 # shown while exported (evaluated with bevels)
            tyres[n] = export({n: o["role"]} | {c.name: c["role"] for c in o.children if "role" in c}, n, o.matrix_world.translation.copy())
            o.hide_set(hidden)
    wheels = []
    for name, z, mode in m.AXLES:
        for side, sx in (("L", -1), ("R", 1)):
            wheels.append({"name": WHEEL_NAMES[name].format(side), "pos": [round(sx * m.WHEEL_X, 4), m.WHEEL_H, z], "mode": mode,
                           "motor": True, "tyre": "Tyre" + side, "arm": "Arm" + side,
                           "armPivot": [round(sx * m.ARM_PIVOT_X, 4), m.ARM_PIVOT_H, round(z + m.ARM_REACH_Z, 4)]})
    shocks = [{"wheel": WHEEL_NAMES[name].format(side), "top": [round(v, 4) for v in m.shock_top(sx, z)],
               "bottom": [round(v, 4) for v in m.shock_bottom(sx, z)], "body": "ShockBody", "rod": "ShockRod",
               "spring": "ShockSpring", "springAt": list(w.SPRING_AT)}
              for name, z, mode in m.AXLES for side, sx in (("L", -1), ("R", 1))] if w else []
    anchors = {o.name[4:]: {"pos": list(o["unity_pos"]), "rot": list(o["unity_rot"])}
               for o in bpy.data.collections["ANC_Rover"].objects if o.name.startswith("ANC_")}
    colliders = collider_boxes(m)
    upgrades = export_upgrades(export)
    layout = {
        "version": 1,
        "body": {"mesh": "RoverBody", "materials": body_mats},
        "glass": {"mesh": "RoverGlass", "materials": ["WindowGlass"]} if glass_parts else {"mesh": None, "materials": []},
        "glow": {"mesh": "RoverGlow" if glow_parts else None},
        "parts": parts,
        "colliders": colliders,
        "autoCom": auto_com3(colliders),
        "comHeight": COM_HEIGHT,
        "mass": MASS,
        "wheelCollider": WHEEL_COLLIDER,
        "wheels": wheels,
        "shocks": shocks,
        "upgrades": upgrades,
        "frame": bs.export_frame("rover", out_dir, col, stats, "RoverBody", body_mats),   # the frame's build states
        "tyres": tyres,
        "anchors": anchors,
        "fields": FIELDS,
        "meshStats": stats,
    }
    with open(os.path.join(out_dir, "rover.json"), "w", encoding="utf-8") as fh:
        json.dump(layout, fh, indent=1)
    return {"meshes": stats, "autoCom": layout["autoCom"], "anchors": len(anchors), "parts": len(parts)}
