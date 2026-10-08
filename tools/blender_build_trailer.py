"""Build the Cargo Trailer assets from the generated model (tools/blender_trailer_model.py). Run inside Blender.

Writes <out>/meshes/TrailerBody.rcm, RoverHitch.rcm (rover-local) and <out>/trailer.json (all trailer-local, Unity axes:
x right, y up, z forward toward the rover, origin at ground under the bed centre). The trailer model is original
work, so these assets ship with the mod.

Colours reuse the Cargo Rover's own palette UV spots per role (read from the rover mesh), so the trailer renders
with the same game materials exactly like the rover.
"""
import json
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
import blender_rover_io as io  # noqa: E402

BLOCKOUT_CENTRE_Y = -6.6          # Blender Y of the trailer origin (hitched to the rover reference at the origin)
ROLE_UV = {                        # role -> (material, uv) taken from the rover's faces
    "white": ("ColorWhite", (0.05469, 0.11719)),   # (231, 231, 231) on every paint swatch: never painted
    "red": ("ColorRed", (0.2969, 0.0781)),
    "gray": ("ColorGray", (0.1406, 0.8594)),
    "tyre": ("ColorWhite", (0.2344, 0.3906)),
    "black": ("ColorWhite", (0.2344, 0.3906)),   # the rover tyre spot: dark frame
    # palette spots the rover itself uses (same atlas): tinted glass, amber and red lamp lenses, dark grey trim
    "glass": ("ColorWhite", (0.4688, 0.3438)),
    "amber": ("ColorWhite", (0.18, 0.086)),
    "lamp": ("ColorWhite", (0.305, 0.086)),
    "dark": ("ColorWhite", (0.102, 0.101)),
    # the original rover's (its own hull's spots): the trailers wear its colours
    # the main body: the one cell the game's paint swatches change (unpainted: the White swatch, 231); the user,
    # 2026-10-08: only the body takes paint
    "silver": ("ColorWhite", (0.8906, 0.8594)),
    "steel": ("ColorWhite", (0.03125, 0.46875)),      # (195, 199, 200): fittings that keep a fixed silver
    "charcoal": ("ColorWhite", (0.03125, 0.40625)),   # (61, 60, 61)
    "gunmetal": ("ColorWhite", (0.15625, 0.40625)),   # (70, 70, 70)
    "orange": ("ColorWhite", (0.15625, 0.34375)),     # (198, 65, 0)
}
# Body parts come from tools/blender_trailer_model.py (collections GEO_TrailerCargo / GEO_TrailerHab);
# each object carries its palette role in the custom property "role".
TANK_ORIGIN_UP = 0.573         # a tank's origin sits this far above its base (crate origins sit at the base)
# Wheels, trailing arms and shocks are the original rover's own (RoverAssets meshes, referenced by name, not copied):
# same ride height, look and suspension animation as the rover (running_gear).
# Base frame shared by every trailer variant: 5.1 m bed on three axles. Variants change the deck, not these.
AXLES = (("Front", 1.7), ("Mid", 0.0), ("Rear", -1.7))  # wheel z (trailer-local); 1.7 m spacing clears the 1.44 m tyres
BAY_PITCH = 0.85               # one crate lying crosswise, or two tanks side by side
BAYS = tuple(round((2.5 - i) * BAY_PITCH, 4) for i in range(6))  # 6 bay centres (trailer-local z), front to rear
MASS = 60.0                    # 10 kg per wheel, like the 2-axle prototype (spring scale = MASS / rover mass)


def _combine(parts, name, pivot, role_uv=None):
    """Evaluated copies of parts, translated so pivot is the origin, joined with per-role material slots + UVs."""
    role_uv = role_uv or ROLE_UV
    deps = bpy.context.evaluated_depsgraph_get()

    def face_roles(src):
        """A part's per-face roles from its material slots (blender_trailer_model.set_roles), else None."""
        mats = src.evaluated_get(deps).data.materials
        return [m["role"] if m is not None and "role" in m else None for m in mats] or None
    used = set(parts.values())
    for obj_name in parts:
        used |= {r for r in face_roles(bpy.data.objects[obj_name]) or () if r}
    roles = sorted(used, key=list(role_uv).index)
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    deps = bpy.context.evaluated_depsgraph_get()
    for obj_name, role in parts.items():
        src = bpy.data.objects[obj_name]
        tmp = src.evaluated_get(deps).to_mesh()
        m = Matrix.Translation(-pivot) @ src.matrix_world
        start = len(bm.faces)
        bm.from_mesh(tmp)
        bm.faces.ensure_lookup_table()
        new_faces = bm.faces[start:]
        by_slot = [r or role for r in face_roles(src) or ()]
        for f in new_faces:
            r = by_slot[f.material_index] if f.material_index < len(by_slot) else role
            f.material_index = roles.index(r)
            for loop in f.loops:
                loop[uv].uv = role_uv[r][1]
        bmesh.ops.transform(bm, matrix=m, verts=list({v for f in new_faces for v in f.verts}))
        src.evaluated_get(deps).to_mesh_clear()
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    for r in roles:
        mat = bpy.data.materials.get("TRL_" + r) or bpy.data.materials.new("TRL_" + r)
        me.materials.append(mat)
    return ob, [role_uv[r][0] for r in roles]


def _parts(collection):
    return {o.name: o["role"] for o in bpy.data.collections[collection].objects
            if o.type == "MESH" and "role" in o and not o.name.startswith("PRV_")}   # PRV_: preview copies, never exported


def _island_boxes(ob, min_size=0.05):
    """One box per connected piece of ob's mesh (Unity axes, relative to the object's origin), dropping pieces
    smaller than min_size (bolts, ribs) and pieces inside another piece's box: the removal colliders."""
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    seen, boxes = set(), []
    for f in bm.faces:
        if f.index in seen:
            continue
        stack, verts = [f], set()
        seen.add(f.index)
        while stack:
            g = stack.pop()
            for v in g.verts:
                verts.add(v)
                for h in v.link_faces:
                    if h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
        us = [(v.co.x, v.co.z, v.co.y) for v in verts]
        lo = [min(u[k] for u in us) for k in range(3)]
        hi = [max(u[k] for u in us) for k in range(3)]
        if max(hi[k] - lo[k] for k in range(3)) >= min_size:
            boxes.append((lo, hi))
    bm.free()
    keep = [b for b in boxes if not any(o is not b and all(o[0][k] <= b[0][k] + 1e-4 and b[1][k] <= o[1][k] + 1e-4 for k in range(3)) for o in boxes)]
    return [{"center": [round((lo[k] + hi[k]) / 2, 4) for k in range(3)], "size": [round(max(hi[k] - lo[k], 0.02), 4) for k in range(3)]}
            for lo, hi in keep]


def _fender_entries(col, out_dir, stats, coll, prefix, hubs):
    """Riding fenders: one mesh per side (pivot = its hub), placed on each hub; "follow" = the mod moves it with the
    wheel's rendered pose (height and steer)."""
    import blender_trailer_model as m
    objs = {o.name: o["role"] for o in bpy.data.collections[coll].objects if "_Fender" in o.name and "role" in o}
    entries = []
    for side in ("L", "R"):
        ref = sorted({n.rsplit("_", 1)[0] for n in objs if "_Fender" + side in n})
        if not ref:
            continue
        first = ref[0]
        parts = {n: r for n, r in objs.items() if n.startswith(first + "_")}
        sx = -1 if side == "L" else 1
        hub0 = next(h for h in hubs if (h[0] > 0) == (sx > 0))
        ob, mats = _combine(parts, prefix + side, Vector((hub0[0], hub0[2], hub0[1])) + Vector((0, _origin_y_of(coll), 0)))
        col.objects.link(ob); ob.hide_set(True); ob.hide_render = True
        stats[prefix + side] = io.export_mesh(ob, os.path.join(out_dir, "meshes", prefix + side + ".rcm"))
        boxes = _island_boxes(ob)
        for k, (x, h, z) in enumerate(h for h in hubs if (h[0] > 0) == (sx > 0)):
            entries.append({"group": "Fairings", "name": f"Fender{side}{k}", "mesh": prefix + side, "materials": mats,
                            "colliders": boxes, "pos": [round(x, 4), round(h, 4), round(z, 4)], "follow": True})
    return entries


def _origin_y_of(coll):
    import blender_trailer_model as m
    return 0.0 if coll.endswith("Rover") else (m.HAB_ORIGIN_Y if coll.endswith("Hab") else m.TRAILER_Y)


def export_upgrades(col, out_dir, stats, specs):
    """specs: (group, collection, mesh_name, blender_pivot, placements) -> "upgrades" entries. placements None = one
    part at the vehicle origin; else a list of (name, unity_pos) sharing one mesh modelled at the first placement."""
    entries = []
    for group, coll, mesh_name, pivot, placements in specs:
        parts = {n: r for n, r in _parts(coll).items() if "_Fender" not in n}   # riding fenders export separately
        if coll not in bpy.data.collections or not parts:
            continue
        ob, mats = _combine(parts, mesh_name, pivot)
        col.objects.link(ob); ob.location = pivot; ob.hide_set(True); ob.hide_render = True
        stats[mesh_name] = io.export_mesh(ob, os.path.join(out_dir, "meshes", mesh_name + ".rcm"))
        boxes = _island_boxes(ob)
        for name, pos in (placements or [(mesh_name, None)]):
            e = {"group": group, "name": name, "mesh": mesh_name, "materials": mats, "colliders": boxes}
            if pos is not None:
                e["pos"] = [round(v, 4) for v in pos]
            entries.append(e)
    return entries


def collider_boxes():
    """Box colliders (trailer-local Unity centre/size) approximating the body: deck, keel, pods, rails, drawbar."""
    import blender_trailer_model as m
    top = m.DECK_TOP
    boxes = [
        ((0, top - 0.06, 0), (2.16, 0.12, 5.3)),          # deck
        ((0, 0.88, 0), (1.06, 0.64, 5.2)),                # keel (arm hinge walls)
    ]
    for z0, z1 in m.POD_FLATS:                             # side pods between the wheels
        boxes.append(((0, 1.03, (z0 + z1) / 2), (2.16, 0.34, z1 - z0)))
    for s in (1, -1):                                      # handrails
        boxes.append(((s * 1.03, top + 0.14, 0), (0.06, 0.28, 5.3)))
    for zz in (2.60, -2.60):
        boxes.append(((0, top + 0.14, zz), (2.0, 0.28, 0.06)))
    boxes.append(((0, 0.80, 3.14), (0.14, 0.14, 1.5)))     # drawbar
    return [{"center": [round(v, 4) for v in c], "size": [round(v, 4) for v in sz]} for c, sz in boxes]


def auto_com(colliders):
    """Unity's automatic centre of mass for uniform-density box colliders: volume-weighted mean of the centres."""
    vol = [c["size"][0] * c["size"][1] * c["size"][2] for c in colliders]
    return round(sum(v * c["center"][1] for v, c in zip(vol, colliders)) / sum(vol), 4)


def running_gear():
    """(wheels, shocks) of every trailer: the original rover's tyres, trailing arms and coil-over shocks (RoverAssets
    meshes; the builder takes rover.json's wheel numbers) on the trailers' three axles, hinged and mounted as on the
    rover (blender_trailer_checks.running_gear checks them against each trailer's hull)."""
    import blender_rover_model as rm
    import blender_rover_wheels as rw
    wheels, shocks = [], []
    for name, z in AXLES:
        for side, sx in (("L", -1), ("R", 1)):
            wn = f"Wheel{name}Tire{side}"
            wheels.append({"name": wn, "pos": [round(sx * rm.WHEEL_X, 4), rm.WHEEL_H, z], "tyre": "Tyre" + side, "arm": "Arm" + side,
                           "armPivot": [round(sx * rm.ARM_PIVOT_X, 4), rm.ARM_PIVOT_H, round(z + rm.ARM_REACH_Z, 4)]})
            shocks.append({"wheel": wn, "top": [round(v, 4) for v in rm.shock_top(sx, z)],
                           "bottom": [round(v, 4) for v in rm.shock_bottom(sx, z)], "body": "ShockBody", "rod": "ShockRod",
                           "spring": "ShockSpring", "springAt": list(rw.SPRING_AT)})
    return wheels, shocks


def build(out_dir, variant="cargo"):
    import importlib
    import blender_trailer_model as m
    importlib.reload(m)
    m.build(variant)
    import blender_build_stages as bs
    import blender_rover_wheels as rw
    importlib.reload(bs)
    rw.build()                                                         # the rover's running gear, posed in the frame's stages
    bs.scaffold(variant)
    if variant == "hab":
        return build_hab_assets(out_dir, m)
    os.makedirs(os.path.join(out_dir, "meshes"), exist_ok=True)
    col = bpy.data.collections.get("EXP_TrailerCargo") or bpy.data.collections.new("EXP_TrailerCargo")
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    for ob in list(col.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    body, body_mats = _combine(_parts("GEO_TrailerCargo"), "TrailerBody", Vector((0.0, BLOCKOUT_CENTRE_Y, 0.0)))
    col.objects.link(body)
    body.location = (0, BLOCKOUT_CENTRE_Y, 0)
    body.hide_render = True
    body.hide_set(True)
    stats = {"TrailerBody": io.export_mesh(body, os.path.join(out_dir, "meshes", "TrailerBody.rcm"))}
    upgrades = {
        "trailer": export_upgrades(col, out_dir, stats, [
            ("Armour", "GEO_UpgArmourTrailer", "UpgArmourTrailer", Vector((0.0, BLOCKOUT_CENTRE_Y, 0.0)), None)])
        + _fender_entries(col, out_dir, stats, "GEO_UpgFairingTrailer", "UpgFenderTrailer",
                          [(sx * m.UPG_HUB_X, m.UPG_HUB_H, z) for z in m.UPG_AXLES_Z for sx in (-1, 1)]),
    }

    wheels, shocks = running_gear()
    top = m.DECK_TOP
    colliders = collider_boxes()
    layout = {
        "version": 2,
        "body": {"mesh": "TrailerBody", "materials": body_mats},
        "upgrades": upgrades,                                                # storm armour / fairings
        "frame": bs.export_frame("cargo", out_dir, col, stats, "TrailerBody", body_mats),   # the frame's build states
        "wheels": wheels,                    # the rover's tyres and arms (RoverAssets); its wheel numbers, spring scaled by mass
        "shocks": shocks,
        "colliders": colliders,
        "autoCom": auto_com(colliders),
        "roverMass": 80.0,
        # 6 bays along the bed (0.85 m pitch); each takes one crate (lying crosswise, origin at its base) OR two tanks
        # side by side (origin 0.573 m above the base), like the rover's two lifts (vanilla ContainerSlot pair rules).
        "bays": [{"crate": [0.0, top, z], "tanks": [[-0.46, round(top + TANK_ORIGIN_UP, 3), z], [0.46, round(top + TANK_ORIGIN_UP, 3), z]]}
                 for z in BAYS],
        "hitch": [0.0, 0.8, 3.9],            # lunette ring centre = rover pintle pin axis
        "roverHitch": [0.0, 0.8, -2.7],
        "tailLights": [[-0.7, 1.10, -2.72], [0.7, 1.10, -2.72]],
        "mass": MASS,
    }
    with open(os.path.join(out_dir, "trailer.json"), "w", encoding="utf-8") as fh:
        json.dump(layout, fh, indent=1)
    return {"meshes": stats, "autoCom": layout["autoCom"], "bays": layout["bays"][:1]}


def hab_colliders(m):
    """Static hab colliders (trailer-local). The left upper wall is split around the slide-out opening; the stairs
    now belong to the Ladder part; the deploy control panel is a named box the wrench can target."""
    H, D = m.HAB, m.HAB_DEPLOY
    f, t, l = H["floor"], H["top"], H["out_half_l"]
    (z0, z1), (h0, h1) = D["slide_z"], D["slide_h"]
    low_top = h0                                                        # lower band up to the slide-out floor (1.72)
    boxes = [
        ((0, 1.26, 0), (2.16, 0.12, 5.3)),                              # chassis deck
        ((0, 0.88, 0), (1.06, 0.64, 5.2)),                              # keel
        ((0, (1.32 + f) / 2, 0), (2.12, f - 1.32, 2 * l)),              # hab floor slab (top 1.45)
        ((0, (t + H["roof"]) / 2, 0), (2.3, H["roof"] - t, 2 * l)),     # roof
        ((0, (f + t) / 2, 3.1), (2.0, t - f, 0.2)),                     # front wall
        ((0, (m.HAB_AIR["door_open"][1][1] + t) / 2, -3.1), (2.0, t - m.HAB_AIR["door_open"][1][1], 0.2)),   # lintel above the door frame
        ((0, 1.29, 3.625), (1.9, 0.06, 0.85)),                                                                  # tongue platform
        ((0, 0.80, 3.14 + m.HAB_EXT / 2), (0.14, 0.14, 1.5 + m.HAB_EXT)),      # drawbar
    ]
    for pz0, pz1 in m.POD_FLATS:
        boxes.append(((0, 1.03, (pz0 + pz1) / 2), (2.16, 0.34, pz1 - pz0)))
    for s in (1, -1):   # lower band tucked in (clears the tyres)
        boxes.append(((s * 1.05, (f + low_top) / 2, 0), (0.10, low_top - f, 2 * l)))
    up = low_top
    boxes.append(((1.075, (up + t) / 2, 0), (0.15, t - up, 2 * l)))                        # right upper band
    boxes.append(((-1.075, (up + t) / 2, (-l + z0) / 2), (0.15, t - up, z0 + l)))           # left, rear of the opening
    boxes.append(((-1.075, (up + t) / 2, (z1 + l) / 2), (0.15, t - up, l - z1)))            # left, front of the opening
    boxes.append(((-1.075, (h1 + t) / 2, (z0 + z1) / 2), (0.15, t - h1, z1 - z0)))          # left, above the opening
    out = [{"center": [round(v, 4) for v in c], "size": [round(v, 4) for v in sz]} for c, sz in boxes]
    (px0, px1), (ph0, ph1), (pz0, pz1) = D["panel"]
    out.append({"name": "DeployPanel", "center": [round((px0 + px1) / 2, 4), round((ph0 + ph1) / 2, 4), round((pz0 + pz1) / 2, 4)],
                "size": [round(px1 - px0, 4), round(ph1 - ph0, 4), round(pz1 - pz0, 4)]})
    return out


def build_hab_assets(out_dir, m):
    import blender_build_stages as bs
    os.makedirs(os.path.join(out_dir, "meshes"), exist_ok=True)
    col = bpy.data.collections.get("EXP_TrailerHab") or bpy.data.collections.new("EXP_TrailerHab")
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    for ob in list(col.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    body, mats = _combine(_parts("GEO_TrailerHab"), "HabBody", Vector((0.0, m.HAB_ORIGIN_Y, 0.0)))
    col.objects.link(body); body.location = (0, m.HAB_ORIGIN_Y, 0); body.hide_set(True); body.hide_render = True
    stats = {"HabBody": io.export_mesh(body, os.path.join(out_dir, "meshes", "HabBody.rcm"))}
    m.build_upgrades_hab()
    upgrades = {"hab": export_upgrades(col, out_dir, stats, [
        ("Armour", "GEO_UpgArmourHab", "UpgArmourHab", Vector((0.0, m.HAB_ORIGIN_Y, 0.0)), None)])
        + _fender_entries(col, out_dir, stats, "GEO_UpgFairingHab", "UpgFenderHab",
                          [(sx * m.UPG_HUB_X, m.UPG_HUB_H, z) for z in m.UPG_AXLES_Z for sx in (-1, 1)])}
    D = m.HAB_DEPLOY
    parts = []

    def export_part(coll, mesh_name, name, pivot_local, colliders):
        pv = Vector((pivot_local[0], m.HAB_ORIGIN_Y + pivot_local[2], pivot_local[1]))
        ob, mats_p = _combine(_parts(coll), mesh_name, pv)
        col.objects.link(ob); ob.location = pv; ob.hide_set(True); ob.hide_render = True
        stats[mesh_name] = io.export_mesh(ob, os.path.join(out_dir, "meshes", mesh_name + ".rcm"))
        parts.append({"name": name, "mesh": mesh_name, "materials": mats_p, "pos": [round(v, 4) for v in pivot_local],
                      "colliders": colliders})

    # slide-out: local origin = outer wall face, floor, rear end (retracted)
    (z0, z1), (h0, h1) = D["slide_z"], D["slide_h"]
    w, d = D["slide_wall"], D["slide_depth"]
    so_pos = (-1.15, h0, z0)
    sc = []
    for c, sz in (((-1.15 - w / 2, (h0 + h1) / 2, (z0 + z1) / 2), (w, h1 - h0, z1 - z0)),          # outer wall
                  ((-1.15 - w + (d + w) / 2, h0 - w / 2, (z0 + z1) / 2), (d + w, w, z1 - z0)),   # floor
                  ((-1.15 - w + (d + w) / 2, h1 + w / 2, (z0 + z1) / 2), (d + w, w, z1 - z0)),   # roof
                  ((-1.15 - w + (d + w) / 2, (h0 + h1) / 2, z0 + w / 2), (d + w, h1 - h0, w)),   # rear end wall
                  ((-1.15 - w + (d + w) / 2, (h0 + h1) / 2, z1 - w / 2), (d + w, h1 - h0, w))):  # front end wall
        sc.append({"center": [round(c[k] - so_pos[k], 4) for k in range(3)], "size": [round(v, 4) for v in sz]})
    export_part("GEO_HabSlideOut", "HabSlideOut", "SlideOut", so_pos, sc)
    # ladder: pivot at the hinge; stairs ramp from (hz, 1.45) down to (hz - 1.40, 0.35), relative to the hinge
    hx, hh, hz = D["ladder_hinge"]
    run, rise, th = 1.40, 1.45 - 0.35, 0.04
    ang = math.degrees(math.atan2(rise, run)); length = math.hypot(run, rise)
    ny, nz = math.cos(math.radians(ang)), -math.sin(math.radians(ang))
    cy, cz = 0.35 + rise / 2 - ny * th / 2, hz - run / 2 - nz * th / 2
    export_part("GEO_HabLadder", "HabLadder", "Ladder", (hx, hh, hz),
                [{"name": "Stairs", "center": [0.0, round(cy - hh, 4), round(cz - hz, 4)], "size": [0.9, th, round(length, 4)],
                  "rot": [round(-ang, 3), 0.0, 0.0]}])
    # legs: one mesh (origin = foot bottom) at the four corners, Travel pose raised to leg_stow_h
    lx, lz = D["legs"][0]
    export_part("GEO_HabLeg", "HabLeg", "Leg0", (lx, 0.0, lz), [])
    leg0 = parts[-1]
    leg0["pos"] = [lx, D["leg_stow_h"], lz]
    for k, (x, z) in enumerate(D["legs"][1:], start=1):
        parts.append(dict(leg0, name=f"Leg{k}", pos=[x, D["leg_stow_h"], z]))
    wheels, shocks = running_gear()
    colliders = hab_colliders(m)
    # Unity's automatic centre of mass counts every enabled collider: static + the retracted slide-out
    # (ladder colliders are disabled in Travel)
    so_world = [dict(c, center=[c["center"][k] + so_pos[k] for k in range(3)]) for c in sc]
    layout = {
        "version": 2,
        "body": {"mesh": "HabBody", "materials": mats},
        "wheels": wheels,
        "shocks": shocks,
        "colliders": colliders,
        "parts": parts,
        "upgrades": upgrades,                                                # storm armour / fairings
        "frame": bs.export_frame("hab", out_dir, col, stats, "HabBody", mats),   # the frame's build states
        # travel < depth: extended, the box's inner edge stays 10 cm inside the wall opening (no seam)
        "deploy": {"slideTravel": [-D["slide_travel"], 0.0, 0.0], "ladderStowEuler": [D["ladder_stow_x"], 0.0, 0.0],
                   "legs": ["Leg0", "Leg1", "Leg2", "Leg3"], "legStowH": D["leg_stow_h"], "legMaxStroke": D["leg_max"],
                   "panel": "DeployPanel"},
        "autoCom": auto_com(colliders + so_world),
        "roverMass": 80.0,
        "bays": [],
        "hitch": [0.0, 0.8, round(3.9 + m.HAB_EXT, 4)],
        "roverHitch": [0.0, 0.8, -2.7],
        "tailLights": [[-0.83, 1.05, -3.36], [0.83, 1.05, -3.36]],      # the bumper lamps
        "cabinVolume": 26800.0,
        "surfaceArea": 100.0,
        "door": {"prefab": "StructureCompositeDoor", "pos": list(m.HAB_AIR["door_pos"]), "leafTravel": 0.44,   # leaves stop at the frame jambs (a full 0.683 m slide would poke out of the hull)
                 "trigger": {"center": [0.0, 2.305, -3.05], "size": [1.37, 1.71, 0.7]}},
        "slots": [
            {"name": "AirSupplyMount", "type": "Container", "pos": [-0.46, 1.32, 3.62]},
            {"name": "AirSupply", "type": "Tank", "pos": [-0.46, round(1.32 + TANK_ORIGIN_UP, 3), 3.62]},
            {"name": "WasteMount", "type": "Container", "pos": [0.46, 1.32, 3.62]},
            {"name": "Waste", "type": "Tank", "pos": [0.46, round(1.32 + TANK_ORIGIN_UP, 3), 3.62]},
        ] + [{"name": n, "type": "GasFilter" if n.startswith("Filter") else "Battery", "pos": [0.93, 3.0, z],
              "trigger": {"center": [0.93, 3.0, z], "size": [0.12, 0.25, 0.18]}} for n, z in m.HAB_AIR["sockets"]],
        "cradles": [{"label": "AIR SUPPLY", "container": "AirSupplyMount", "tank": "AirSupply"},
                    {"label": "WASTE", "container": "WasteMount", "tank": "Waste"}],
        "mass": 90.0,
    }
    with open(os.path.join(out_dir, "hab.json"), "w", encoding="utf-8") as fh:
        json.dump(layout, fh, indent=1)
    return {"meshes": stats, "autoCom": layout["autoCom"], "colliders": len(colliders), "parts": [p["name"] for p in parts]}
