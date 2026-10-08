"""Checks of the trailers' running gear (the original rover's tyres, arms and coil-over shocks on the cargo trailer and
the hab): each part posed over the wheel's travel keeps clear of the trailer's own parts, and each shock's length stays
inside its working range. Run headless with tools/run_trailer_checks.py.
"""
import math

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import blender_build_trailer as bt
import blender_rover_checks as rc
import blender_rover_model as rm
import blender_rover_wheels as rw
import blender_trailer_model as g

TRAILER_BUMP = 0.25   # the trailer hull's design rise: its edge lip clears the arm hubs up to 0.25 m (blender_trailer_model)
CLEAR = 0.01          # a posed arm or shock keeps 1 cm from the trailer's parts
TYRE_CLEAR = 0.002    # a tyre only never touches them: the hab's rub strip (bottom 1.625) is 4 mm over a tyre at the
                      # design rise, as with the 2020 tyres of the same size
# Where a part is fixed to the frame, touching is the mounting, not a clash: the arm's root in the keel wall (inboard
# of its face, within ARM_ROOT of the hinge, as on the rover) and the shock's upper eye in its mount plate (EYE_R).
ARM_ROOT, EYE_R = 0.20, 0.05


def _frame_bvh(coll):
    """One BVH of every exported part of the trailer (evaluated: bevels and the shock pockets' cuts)."""
    deps = bpy.context.evaluated_depsgraph_get()
    verts, polys = [], []
    for o in bpy.data.collections[coll].objects:
        if o.type != "MESH" or "role" not in o:
            continue
        e = o.evaluated_get(deps)
        me = e.to_mesh()
        base = len(verts)
        verts += [e.matrix_world @ v.co for v in me.vertices]
        polys += [[base + i for i in p.vertices] for p in me.polygons]
        e.to_mesh_clear()
    return BVHTree.FromPolygons(verts, polys)


def _posed(name, mat):
    """A rover wheel part's mesh (evaluated: bevels) at the world matrix `mat`: (vertices, polygons)."""
    deps = bpy.context.evaluated_depsgraph_get()
    o = bpy.data.objects[name]
    hidden = o.hide_get()
    o.hide_set(False)
    e = o.evaluated_get(deps)
    me = e.to_mesh()
    verts = [mat @ v.co for v in me.vertices]
    polys = [list(p.vertices) for p in me.polygons]
    e.to_mesh_clear()
    o.hide_set(hidden)
    return verts, polys


SAMPLE = 0.01         # surface sample spacing on a posed part: below CLEAR, so a part that enters the frame always
                      # leaves samples within CLEAR of its surface (no inside test, which misreads points by an edge)


def _samples(verts, polys):
    """Points over a part's surface, about SAMPLE apart (each polygon fanned into triangles, a barycentric grid on each)."""
    pts = []
    for poly in polys:
        a = verts[poly[0]]
        for b, c in ((verts[poly[k]], verts[poly[k + 1]]) for k in range(1, len(poly) - 1)):
            n = max(1, math.ceil(max((b - a).length, (c - a).length, (c - b).length) / SAMPLE))
            for i in range(n + 1):
                for j in range(n + 1 - i):
                    pts.append(a + (b - a) * (i / n) + (c - a) * (j / n))
    return pts


def _clash(frame, verts, polys, mounted=None, clear=CLEAR):
    """Surface points of a posed part within `clear` of the frame (so also where it enters it), but where `mounted`
    says it is fixed."""
    hits = []
    for p in _samples(verts, polys):
        if mounted is not None and mounted(p):
            continue
        loc, _, _, d = frame.find_nearest(p)
        if loc is not None and d < clear:
            hits.append(p)
    return hits


def running_gear(variant):
    """Build the trailer `variant` ("cargo" or "hab") and the rover's wheel parts; pose every wheel's tyre, arm and
    shock at each lift (the arm turned as CargoRover.AnimateSuspensionArms, the shock aimed and its spring scaled as
    AnimateShocks) and report clashes with the trailer's parts and shock lengths out of range."""
    g.build(variant)
    coll = "GEO_TrailerHab" if variant == "hab" else "GEO_TrailerCargo"
    rw.build()
    bpy.context.view_layer.update()
    frame = _frame_bvh(coll)
    zf = next(z for n, z, _ in rm.AXLES if n == "Front")
    lo_len = max(rw.SHOCK_ROD_L + 0.02, rw.SHOCK_BODY_L + rw.SPRING_AT[1] + 0.005)
    hi_len = rw.SHOCK_BODY_L + rw.SHOCK_ROD_L - 0.04
    lateral = Vector((1, 0, 0))
    wheels, shocks = bt.running_gear()
    shock_of = {s["wheel"]: s for s in shocks}
    res, ok = {}, True
    for w in wheels:
        hub, piv = w["pos"], w["armPivot"]
        s = -1 if hub[0] < 0 else 1
        side = "L" if s < 0 else "R"
        shift = g.T(0, 0, hub[2]) - rm.U(0, 0, zf)                      # the rover's front-axle parts to this wheel
        hinge = g.T(*piv)
        sh = shock_of[w["name"]]
        top, bot0 = g.T(*sh["top"]), sh["bottom"]
        rh, rz = hub[1] - piv[1], hub[2] - piv[2]
        y, zz = bot0[1] - piv[1], bot0[2] - piv[2]
        bad, lens = [], []
        for lift in (-rc.SUSP_TRAVEL, -TRAILER_BUMP, 0.0, TRAILER_BUMP / 2, TRAILER_BUMP, rc.SUSP_TRAVEL):
            a = math.radians(rc._arm_angle(lift, rh, rz))
            bot = g.T(bot0[0], piv[1] + y * math.cos(a) - zz * math.sin(a), piv[2] + y * math.sin(a) + zz * math.cos(a))
            L = (top - bot).length
            lens.append(round(L, 3))
            if not lo_len <= L <= hi_len:
                bad.append(f"shock length {L:.3f} at lift {lift}")
            if lift > TRAILER_BUMP:
                continue
            turn = Matrix.Translation(hinge) @ Matrix.Rotation(-a, 4, "X") @ Matrix.Translation(-hinge)
            arm = bpy.data.objects["Arm" + side]
            posed = {
                "tyre": (("Tyre" + side, Matrix.Translation(shift + Vector((0, 0, lift))) @ bpy.data.objects["Tyre" + side].matrix_world), None),
                "arm": (("Arm" + side, turn @ Matrix.Translation(shift) @ arm.matrix_world),
                        lambda p, h=hinge: abs(p.x) < rm.KEEL_X + CLEAR and (p - h).length < ARM_ROOT),
            }
            axis = (bot - top).normalized()
            for part, at, to, mount in (("ShockBody", top, bot, lambda p, t=top: (p - t).length < EYE_R), ("ShockRod", bot, top, None)):
                tmp = bpy.data.objects[part]
                rw._aim(tmp, at, to, lateral)
                posed[part] = ((part, tmp.matrix_world.copy()), mount)
            start, end = sh["springAt"]
            rest = (Vector(rm.shock_top(1, 0.0)) - Vector(rm.shock_bottom(1, 0.0))).length - start - end
            spring = bpy.data.objects["ShockSpring"]
            rw._aim(spring, top + axis * start, bot, lateral)
            k = max(L - start - end, 0.0) / rest
            posed["ShockSpring"] = (("ShockSpring", spring.matrix_world @ Matrix.Diagonal((1, k, 1, 1))), None)
            for label, ((name, mat), mounted) in posed.items():
                hits = _clash(frame, *_posed(name, mat), mounted, TYRE_CLEAR if label == "tyre" else CLEAR)
                if hits:
                    h = max(hits, key=lambda p: p.z)
                    bad.append(f"{label} at lift {lift} ({len(hits)} pts, e.g. x {h.x:.3f} h {h.z:.3f} z {h.y - g._origin_y:.3f})")
        res[w["name"]] = {"lengths": lens, "bad": bad[:12]}
        ok &= not bad
    return {"ok": ok, **res}


ROVER_OUTSIDE = {"silver", "gunmetal", "steel"}
INSIDE_GREY = {"HAB_LifeSupport", "HAB_WaterRack", "HAB_ShowerPlinth"}   # the room's fittings keep the old grey   # the rover's hull colours (the user's choice 2026-10-06, option A)


def _face_roles(ob):
    """(centre, normal, role) of every evaluated face of a part (its per-face roles, else its own)."""
    deps = bpy.context.evaluated_depsgraph_get()
    e = ob.evaluated_get(deps)
    me = e.to_mesh()
    mats = [m["role"] if m is not None and "role" in m else None for m in me.materials]
    out = []
    for p in me.polygons:
        r = mats[p.material_index] if p.material_index < len(mats) and mats[p.material_index] else ob["role"]
        out.append((e.matrix_world @ p.center, (e.matrix_world.to_3x3() @ p.normal).normalized(), r))
    e.to_mesh_clear()
    return out


def palette(variant):
    """The trailers wear the rover's colours outside and the hab stays white inside: the cargo trailer's hull is
    silver over gunmetal (no white); every face of the hab's shell facing its room (or the door's tunnel through the
    rear wall, under the game's door frame) is white and every other face silver or gunmetal."""
    g.build(variant)
    if variant == "hab":
        g.build_upgrades_hab()
    bpy.context.view_layer.update()
    upg = ("GEO_UpgArmourTrailer", "GEO_UpgFairingTrailer") if variant == "cargo" else ("GEO_UpgArmourHab", "GEO_UpgFairingHab")
    old = sorted({o.name for cn in upg for o in bpy.data.collections[cn].objects if o.type == "MESH" and "role" in o
                  for _, _, r in _face_roles(o) if r in ("white", "gray")})       # the upgrades in the rover's palette too
    if variant == "cargo":
        roles = {r for _, _, r in _face_roles(bpy.data.objects["TRL_Hull"])}
        return {"ok": roles == {"silver", "gunmetal"} and not old, "hull": sorted(roles), "upgrades in old colours": old}
    H = g.HAB
    inside, outside = set(), set()
    for c, n, r in _face_roles(bpy.data.objects["HAB_Shell"]):
        q = c + n * 0.02
        x, h, z = q.x, q.z, q.y - g._origin_y
        room = abs(x) < H["half_w"] - 0.005 and H["floor"] + 0.005 < h < H["top"] - 0.005 and abs(z) < H["half_l"] - 0.005
        (dx0, dx1), (dh0, dh1) = g.HAB_AIR["door_open"]
        door = dx0 - 0.005 < x < dx1 + 0.005 and dh0 - 0.005 < h < dh1 + 0.005 and -H["out_half_l"] - 0.01 < z < -H["half_l"]
        (inside if room or door else outside).add(r)
    # the game's ColorGray spot shows nearly white (the user's build 9 report: the tank straps, the stairs and their
    # hinge): only the room's own fittings keep it
    grey = sorted(o.name for cn in ("GEO_TrailerHab", "GEO_HabLadder", "GEO_HabLeg", "GEO_HabSlideOut")
                  for o in (bpy.data.collections[cn].objects if cn in bpy.data.collections else [])
                  if o.get("role") == "gray" and o.name not in INSIDE_GREY)
    return {"ok": inside == {"white"} and outside <= ROVER_OUTSIDE and "silver" in outside and not grey and not old,
            "inside": sorted(inside), "outside": sorted(outside), "grey outside": grey,
            "upgrades in old colours": old}


PAINT_UV = (0.8906, 0.8594)    # the one palette cell the game's paint swatches change (every other cell is the same in all)
KEEP_STEEL = ("ShockRod", "RVB_MastDome", "RVB_SlotHousings", "HAB_AC", "HAB_ACLouvreFrames", "HAB_RoofVent")


def paint_cells():
    """Only the main body takes paint (the user, 2026-10-08): "silver" is the one role on the paint cell (unpainted it
    shows the White swatch, 231); every other role, "white" included, sits on a cell no swatch changes; the fittings
    that were silver keep a fixed silver ("steel")."""
    import blender_build_rover as br
    import blender_build_trailer as bt
    import blender_rover_model as rm
    import blender_rover_wheels as rw
    bad = []
    for name, table in (("trailer", bt.ROLE_UV), ("rover", br.ROLE_UV)):
        for role, (mat, uv) in table.items():
            on = mat == "ColorWhite" and tuple(uv) == PAINT_UV
            if (role == "silver") != on:
                bad.append(f"{name} {role} {'on' if on else 'off'} the paint cell")
    rm.build()
    rw.build()
    g.build("hab")
    bad += [f"{n}: role {bpy.data.objects[n].get('role')}" for n in KEEP_STEEL
            if n not in bpy.data.objects or bpy.data.objects[n].get("role") != "steel"]
    return {"ok": not bad, "bad": bad}
