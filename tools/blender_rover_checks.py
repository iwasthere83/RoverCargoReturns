"""Checks and phone renders for the original Cargo Rover (tools/blender_rover_model.py). Run inside Blender (MCP).

rover_checks() -> {"bay_fit", "steer", "arms", "tow", "belly", "view", "screen", "legs", "doors", "panel"}: each
    {"ok": bool, ...details}
render_views(out_dir) -> {view: png}: side / top / back / front orthographic + 3/4 front and rear, only the new rover
render_doors_open(out_dir), render_panel_views(out_dir), render_cab_views(out_dir), render_mockup_views(out_dir, png)
"""
import math
import os

import bpy
from mathutils import Euler, Matrix, Vector
from mathutils.bvhtree import BVHTree

import blender_rover_model as m
import blender_trailer_model as g

MARGIN = 0.03          # tyres, arms and tow bodies keep 3 cm from the body
FIT_MARGIN = 0.02      # crates and tanks keep 2 cm from their bay
DOOR_CLEAR = 0.002     # a swinging bay door keeps 2 mm from the body (closed, it sits 4-5 mm inside its opening)
PANEL_CLEAR = 0.01     # slot-panel items keep 1 cm from their recess walls
PANEL_GAP = 0.015      # and 1.5 cm from each other
SEAT_GAP = 0.012       # and each rests on, or plugs into, its socket: within 1.2 cm of it


BODY_COLLS = ("GEO_RoverBody", "GEO_RoverDoors", "GEO_RoverGlass")


def _body_bvhs(extra=()):
    """(name, BVH in world space, bbox lo, bbox hi) per exported body part, and per part of the `extra` collections
    (the fitted upgrades, blender_rover_upgrades.FITTED; missing collections are skipped)."""
    deps = bpy.context.evaluated_depsgraph_get()
    out = []
    for cn in BODY_COLLS + tuple(extra):
        col = bpy.data.collections.get(cn)
        for o in (col.objects if col else []):
            if o.type == "MESH" and "role" in o:
                ev = o.evaluated_get(deps)
                me = ev.to_mesh()
                me.calc_loop_triangles()
                vs = [o.matrix_world @ v.co for v in me.vertices]
                tris = [tuple(t.vertices) for t in me.loop_triangles]
                ev.to_mesh_clear()
                if not vs:
                    continue
                lo = Vector([min(v[k] for v in vs) for k in range(3)])
                hi = Vector([max(v[k] for v in vs) for k in range(3)])
                out.append((o.name, BVHTree.FromPolygons(vs, tris), lo, hi))
    return out


_RAY_DIRS = [Vector(v).normalized() for v in ((0.80, 0.36, 0.48), (-0.37, 0.83, 0.42), (0.27, -0.44, 0.86))]   # parallel to no model face


def _parity(bvh, co, d):
    o, n = co.copy(), 0
    for _ in range(64):
        hit = bvh.ray_cast(o, d)
        if hit[0] is None:
            break
        n += 1
        o = hit[0] + d * 1e-5
    return n % 2 == 1


def _inside(bvh, co):
    """Ray parity (odd = inside that closed part), the majority of three generic directions: a ray that grazes a face
    counts it wrong (a point 0.3 mm under the cab floor read as inside with one +x ray), and cannot flip the answer."""
    return sum(_parity(bvh, co, d) for d in _RAY_DIRS) >= 2


def _hits(points, bvhs, margin):
    """Names of the parts each point is inside of or within margin of (bounding boxes skip far parts)."""
    bad = []
    for p in points:
        for name, b, lo, hi in bvhs:
            if any(p[k] < lo[k] - margin or p[k] > hi[k] + margin for k in range(3)):
                continue
            if b.find_nearest(p, margin)[0] is not None or _inside(b, p):
                bad.append(name)
                break
    return bad


def _anchor(name):
    ob = bpy.data.objects.get("ANC_" + name)
    if ob is None:
        return None
    x, h, z = ob["unity_pos"]
    rx, ry, rz = ob["unity_rot"]
    return (x, h, z), ry


def _ref_local(obj_name):
    """A REF item's mesh in its own Unity frame (x, h, z) relative to its origin."""
    o = bpy.data.objects[obj_name]
    return [(v.co.x, v.co.z, v.co.y) for v in o.data.vertices]


def _ref_crate_local():
    """The game's crate as it stands closed, in its own Unity frame: its body and its lid (the prefab's child
    BoxColliderOpenRenderer, in its rest pose: the closed lid, 0.526-0.732 above the base - measured wrongly at first
    as an open lid, so two crates were stacked in a bay 1.22 m tall; the user's test build 5 report)."""
    crate, lid = bpy.data.objects["DynamicCrate"], bpy.data.objects["BoxColliderOpenRenderer"]
    inv = crate.matrix_world.inverted()
    lid_pts = [inv @ (lid.matrix_world @ v.co) for v in lid.data.vertices]
    return _ref_local("DynamicCrate") + [(p.x, p.z, p.y) for p in lid_pts]


def _place(local, pos, yaw_deg):
    a = math.radians(yaw_deg)
    ca, sa = math.cos(a), math.sin(a)
    out = []
    for x, h, z in local:                          # Unity yaw: x' = x cos + z sin, z' = -x sin + z cos
        out.append((pos[0] + x * ca + z * sa, pos[1] + h, pos[2] - x * sa + z * ca))
    return out


def bay_fit():
    """Every crate/tank slot a bay fills: its REF item (the game's real mesh; the crate with its lid) at the exported
    anchor stays FIT_MARGIN inside its bay's walls, floor to ceiling, and no body part (the bent door leaves included)
    comes within FIT_MARGIN of the item's surface; the item's foot, standing on the floor, does not count."""
    body = [b for b in _body_bvhs() if not b[0].startswith("RVG_")]
    report, ok = {}, True
    for side, sx in (("L", -1), ("R", 1)):
        for name, z0, z1 in m.BAYS:
            lo = (min(sx * m.BAY_IN_X, sx * m.BAY_OUT_X), m.BAY_FLOOR, z1)
            hi = (max(sx * m.BAY_IN_X, sx * m.BAY_OUT_X), m.BAY_TOP, z0)
            for slot in ("CrateBottom", "TankFront", "TankRear"):     # one crate a bay (the user's choice: the top crate
                                                                     # slot is kept for saves, never filled: RoverRules.BaySlotFor)
                a = _anchor(f"Bay{side}{name}{slot}")
                if a is None:
                    report[f"{side}{name}{slot}"] = "no anchor"; ok = False; continue
                pts = _place(_ref_crate_local() if slot.startswith("Crate") else _ref_local("DynamicGasCanisterEmpty"), a[0], a[1])
                out = [p for p in pts if p[1] < lo[1] - 0.001 or any(p[k] < lo[k] + FIT_MARGIN - 1e-4 for k in (0, 2))
                       or any(p[k] > hi[k] - FIT_MARGIN + 1e-4 for k in range(3))]
                foot = min(p[1] for p in pts) + 0.01
                near = set()
                for p in pts:
                    if p[1] <= foot:
                        continue
                    q = m.U(*p)
                    for bn, b, blo, bhi in body:
                        if bn in near or any(q[k] < blo[k] - FIT_MARGIN or q[k] > bhi[k] + FIT_MARGIN for k in range(3)):
                            continue
                        if b.find_nearest(q, FIT_MARGIN)[0] is not None:
                            near.add(bn)
                report[f"{side}{name}{slot}"] = {"outside": len(out), "body_near": sorted(near)}
                ok &= not out and not near
    return {"ok": ok, **report}


def _tyre_points(hub, side, steer_deg, lift, n_around=32, n_across=6):
    """Tread and inner-sidewall points of a visual tyre (r TYRE_R, faces TYRE_IN..TYRE_OUT), steered about the
    vertical axis through the hub and raised by lift. Blender points."""
    cx, ch, cz = hub
    a = math.radians(steer_deg)
    ca, sa = math.cos(a), math.sin(a)
    pts = []
    for i in range(n_across):
        ax = side * (m.TYRE_IN + (m.TYRE_OUT - m.TYRE_IN) * i / (n_across - 1)) - cx      # along the axle from the hub
        for k in range(n_around):
            t = 2 * math.pi * k / n_around
            for r in ((m.TYRE_R,) if 0 < i < n_across - 1 else (m.TYRE_R, 0.62, 0.50)):
                lh, lz = r * math.sin(t), r * math.cos(t)
                x, z = cx + ax * ca + lz * sa, cz - ax * sa + lz * ca
                pts.append(m.U(x, ch + lift + lh, z))
    return pts


def _body_points(extra=()):
    """Every exported body vertex in Unity (x, h, z), and the `extra` collections' (the fitted upgrades)."""
    deps = bpy.context.evaluated_depsgraph_get()
    pts = []
    for cn in BODY_COLLS + tuple(extra):
        col = bpy.data.collections.get(cn)
        for o in (col.objects if col else []):
            if o.type == "MESH" and "role" in o:
                me = o.evaluated_get(deps).to_mesh()
                pts += [(w.x, w.z, w.y) for w in (o.matrix_world @ v.co for v in me.vertices)]
                o.evaluated_get(deps).to_mesh_clear()
    return pts


def _in_tyre(pts, hub, side, steer_deg, lift, margin):
    """Body points inside the (steered, raised) tyre solid grown by margin: parts buried in a tyre, which the
    surface samples cannot see."""
    cx, ch, cz = hub
    a = math.radians(steer_deg)
    ca, sa = math.cos(a), math.sin(a)
    half = (m.TYRE_OUT - m.TYRE_IN) / 2
    mid = side * (m.TYRE_IN + m.TYRE_OUT) / 2 - cx          # the tread's centre along the axle, from the hub
    n = 0
    for x, h, z in pts:
        dx, dh, dz = x - cx, h - (ch + lift), z - cz
        ax = dx * ca - dz * sa                                # inverse of _tyre_points' steer rotation
        lz = dx * sa + dz * ca
        if abs(ax - mid) <= half + margin and dh * dh + lz * lz <= (m.TYRE_R + margin) ** 2:
            n += 1
    return n


def steer_sweep():
    """Steered axles (front Normal, rear Inverted), both sides, -40..40 deg, at rest and +BUMP: tyre surface points
    vs the body, and body points inside the tyre. The fixed axle straight, at rest and +BUMP."""
    import blender_rover_upgrades as bu
    bvhs = _body_bvhs(bu.FITTED)
    body = _body_points(bu.FITTED)
    worst = {}
    for name, z, mode in m.AXLES:
        for side in (-1, 1):
            hub = (side * m.WHEEL_X, m.WHEEL_H, z)
            hits = 0
            for deg in (range(-40, 41, 10) if mode else (0,)):
                for lift in (0.0, m.BUMP):
                    hits += len(_hits(_tyre_points(hub, side, deg, lift), bvhs, MARGIN)) + _in_tyre(body, hub, side, deg, lift, MARGIN)
            worst[f"{name}{'L' if side < 0 else 'R'}"] = hits
    return {"ok": all(v == 0 for v in worst.values()), **worst}


def _arm_points(side, z_hub):
    """The trailing arm's swing: its mesh (GEO_RoverWheels Arm*) if built, else a 0.12 m bar from the hinge to the
    hub face; rotated about the hinge's x axis through -55..+55 deg (CargoRover's clamp)."""
    px, ph, pz = side * m.ARM_PIVOT_X, m.ARM_PIVOT_H, z_hub + m.ARM_REACH_Z
    arm = bpy.data.objects.get("Arm" + ("L" if side < 0 else "R"))
    if arm is not None:
        base = [(v.co.x, v.co.z, v.co.y) for v in arm.data.vertices]          # arm-local = relative to the hinge
    else:
        base = []
        for k in range(11):
            t = k / 10
            x = (side * m.TYRE_IN - px) * t
            h, zz = (m.WHEEL_H - ph) * t, (z_hub - pz) * t
            for dx, dh in ((0, 0.06), (0, -0.06), (0.06 * side, 0), (-0.06 * side, 0)):
                base.append((x + dx, h + dh, zz))
    base = [(x, h, zz) for x, h, zz in base if (x * x + h * h + zz * zz) ** 0.5 > 0.12]   # the hinge bush is buried by design
    pts = []
    for deg in range(-55, 56, 5):
        a = math.radians(deg)
        ca, sa = math.cos(a), math.sin(a)
        for x, h, zz in base:
            pts.append(m.U(px + x, ph + h * ca - zz * sa, pz + h * sa + zz * ca))
    return pts


def arm_envelope():
    import blender_rover_upgrades as bu
    bvhs = _body_bvhs(bu.FITTED)
    res = {}
    for name, z, mode in m.AXLES:
        for side in (-1, 1):
            res[f"{name}{'L' if side < 0 else 'R'}"] = len(_hits(_arm_points(side, z), bvhs, 0.015))
    return {"ok": all(v == 0 for v in res.values()), **res}


def _tow_points(coll, origin_y, hitch_z):
    """A towed body's points relative to its hitch, in (x, h, z) with z measured forward from its hitch."""
    deps = bpy.context.evaluated_depsgraph_get()
    pts = []
    for o in bpy.data.collections[coll].objects:
        if o.type != "MESH" or "role" not in o or o.name.startswith("PRV_"):
            continue
        me = o.evaluated_get(deps).to_mesh()
        pts += [(w.x, w.z, (w.y - origin_y) - hitch_z) for w in (o.matrix_world @ v.co for v in me.vertices)]
        o.evaluated_get(deps).to_mesh_clear()
    if coll == "GEO_TrailerHab":                    # the portable tanks standing in the hab's tongue cradles (not meshes)
        A = g.HAB_AIR
        for _, cx in A["cradles"]:
            for k in range(4):
                for a16 in range(16):
                    ang = 2 * math.pi * a16 / 16
                    pts.append((cx + 0.41 * math.cos(ang), 1.32 + 0.39 * k, A["cradle_z"] + 0.41 * math.sin(ang) - hitch_z))
    # every third vertex, minus the coupling itself (the lunette ring sits on the pin by design)
    return [p for p in pts[::3] if p[0] * p[0] + p[2] * p[2] > 0.35 * 0.35]


def tow_clearance():
    """Trailer and hab hitched to the new pin, yawed 0..70 deg (the joint's limit): first yaw with contact on the
    rover body (and its fitted upgrades), and on the rover's rear tyres (straight and at full counter-steer)."""
    import blender_rover_upgrades as bu
    bvhs = _body_bvhs(bu.FITTED)
    hx, hh, hz = m.HITCH
    rear_z = next(z for n, z, _ in m.AXLES if n == "Rear")
    out = {}
    for label, coll, oy, hitch in (("trailer", "GEO_TrailerCargo", g.TRAILER_Y, 3.9), ("hab", "GEO_TrailerHab", g.HAB_ORIGIN_Y, 3.9 + g.HAB_EXT)):
        pts = _tow_points(coll, oy, hitch)
        first_body = first_tyre = None
        for deg in range(0, 71, 5):
            a = math.radians(deg)
            ca, sa = math.cos(a), math.sin(a)
            world = [m.U(hx + x * ca + z * sa, h, hz - x * sa + z * ca) for x, h, z in pts]
            if first_body is None and _hits(world, bvhs, MARGIN):
                first_body = deg
            if first_tyre is None:
                for steer in (0, 40, -40):
                    for side in (-1, 1):
                        c = Vector((side * m.WHEEL_X, rear_z, m.WHEEL_H))
                        axle = Vector((math.cos(math.radians(steer)), -math.sin(math.radians(steer)), 0))
                        for p in world:
                            d = p - c
                            along = d.dot(axle)
                            radial = (d - axle * along).length
                            if abs(along) < (m.TYRE_OUT - m.TYRE_IN) / 2 + MARGIN and radial < m.TYRE_R + MARGIN:
                                first_tyre = deg
                                break
                        if first_tyre is not None:
                            break
                    if first_tyre is not None:
                        break
            if first_body is not None and first_tyre is not None:
                break
        out[label] = {"first_body_contact_deg": first_body, "first_tyre_contact_deg": first_tyre}
    ok = all(v["first_body_contact_deg"] is None and (v["first_tyre_contact_deg"] is None or v["first_tyre_contact_deg"] >= 55) for v in out.values())
    return {"ok": ok, **out}


def belly():
    deps = bpy.context.evaluated_depsgraph_get()
    low = 9.0
    for o in bpy.data.collections["GEO_RoverBody"].objects:
        if o.type == "MESH" and "role" in o:
            me = o.evaluated_get(deps).to_mesh()
            low = min([low] + [(o.matrix_world @ v.co).z for v in me.vertices])
            o.evaluated_get(deps).to_mesh_clear()
    return {"ok": low >= m.BELLY - 0.02, "lowest_h": round(low, 3)}


def _unity_rot(rot):
    """Unity Euler degrees (applied Z, then X, then Y) as a matrix on Unity (x, h, z) vectors."""
    rx, ry, rz = (math.radians(a) for a in rot)
    mx = Matrix(((1, 0, 0), (0, math.cos(rx), -math.sin(rx)), (0, math.sin(rx), math.cos(rx))))
    my = Matrix(((math.cos(ry), 0, math.sin(ry)), (0, 1, 0), (-math.sin(ry), 0, math.cos(ry))))
    mz = Matrix(((math.cos(rz), -math.sin(rz), 0), (math.sin(rz), math.cos(rz), 0), (0, 0, 1)))
    return my @ mx @ mz


def _apos(name):
    return Vector(bpy.data.objects["ANC_" + name]["unity_pos"])


def cab_view():
    """From both eye points the view over the nose stays clear: the lines to points across the windscreen opening's
    lower edge (3 cm up the glass) cross no body part (the dash, the screen, its bezel). The glass is not an obstacle."""
    M, L = m.windscreen_face()
    targets = [M(f * m.WINDSCREEN_HALF, 0.17, -0.03) for f in (-0.8, -0.4, 0.0, 0.4, 0.8)]
    bvhs = [b for b in _body_bvhs() if not b[0].startswith("RVG_")]
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        eye = m.U(*_apos("Camera" + who))
        blocked = []
        for t in targets:
            d = t - eye
            for name, bvh, lo, hi in bvhs:
                if bvh.ray_cast(eye, d.normalized(), d.length - 0.01)[0] is not None:
                    blocked.append(name)
                    break
        res[who] = blocked
        ok &= not blocked
    return {"ok": ok, **res}


def screen_facing():
    """The Screen anchor faces both eyes within 45 deg and within reach (1.4 m)."""
    a = bpy.data.objects.get("ANC_Screen")
    if a is None:
        return {"ok": False, "error": "no ANC_Screen"}
    pos, n = Vector(a["unity_pos"]), _unity_rot(a["unity_rot"]) @ Vector((0, 0, 1))
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        d = _apos("Camera" + who) - pos
        ang, dist = math.degrees(n.angle(d)), d.length
        res[who] = {"angle_deg": round(ang, 1), "distance_m": round(dist, 3)}
        ok &= ang <= 45.0 and dist <= 1.4
    return {"ok": ok, **res}


LEG_R = 0.065


def _leg_points(seat):
    """A seated leg (low seat, legs forward into the footwell dip, as the 2020 seat): thigh hip -> knee and shin
    knee -> heel, each sampled along its axis and on a ring of radius LEG_R (Unity points)."""
    x, h, z = seat
    hip, knee = Vector((x, h + 0.07, z + 0.05)), Vector((x, h + 0.11, z + 0.47))
    heel = Vector((x, m.DIP["floor"] + LEG_R + 0.012, z + 0.90))
    pts = []
    for a, b in ((hip, knee), (knee, heel)):
        ax = (b - a).normalized()
        side = ax.cross(Vector((1, 0, 0))).normalized()
        up = ax.cross(side).normalized()
        for i in range(9):
            c = a.lerp(b, i / 8)
            pts.append(c)
            for k in range(8):
                t = 2 * math.pi * k / 8
                pts.append(c + (side * math.cos(t) + up * math.sin(t)) * LEG_R)
    return pts


def leg_room():
    """Each seated leg shape (from the seat anchors) is clear of every body part but the seats it rests on."""
    bvhs = [b for b in _body_bvhs() if not b[0].startswith(("RVB_Seat", "RVG_"))]
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        hits = _hits([m.U(*p) for p in _leg_points(_apos("Seat" + who))], bvhs, 0.0)
        res[who] = sorted(set(hits))
        ok &= not hits
    return {"ok": ok, **res}


def _leaf_mesh(leaf):
    """World-space vertices and triangles of a door leaf with its stripe, as exported (modifiers applied: the bevel)."""
    deps = bpy.context.evaluated_depsgraph_get()
    verts, tris = [], []
    for o in [leaf] + [c for c in leaf.children if c.type == "MESH"]:
        ev = o.evaluated_get(deps)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        base = len(verts)
        verts += [o.matrix_world @ v.co for v in me.vertices]
        tris += [tuple(base + i for i in t.vertices) for t in me.loop_triangles]
        ev.to_mesh_clear()
    return verts, tris


def door_swing(step=5):
    """Each leaf turned about its hinge (the Unity z axis through `hinge`) from 0 to DOOR_OPEN_DEG in step degrees:
    no leaf triangle crosses a body part (BVH overlap) and no leaf vertex comes within DOOR_CLEAR of one."""
    bpy.context.view_layer.update()
    import blender_rover_upgrades as bu
    body = [b for b in _body_bvhs(bu.FITTED) if not b[0].startswith("BayDoor")]
    res, ok = {}, True
    for leaf in sorted((o for o in bpy.data.collections["GEO_RoverDoors"].objects if o.parent is None and "hinge" in o), key=lambda o: o.name):
        verts, tris = _leaf_mesh(leaf)
        hx, hh, hz = leaf["hinge"]
        side = 1 if hx > 0 else -1
        pivot = m.U(hx, hh, hz)
        hits, near = set(), set()
        for a in range(0, int(m.DOOR_OPEN_DEG) + 1, step):
            R = Matrix.Rotation(-math.radians(side * a), 3, "Y")        # Unity +z turn (x toward h) = Blender -y turn
            vs = [pivot + R @ (v - pivot) for v in verts]
            lo = Vector([min(v[k] for v in vs) for k in range(3)])
            hi = Vector([max(v[k] for v in vs) for k in range(3)])
            bvh = BVHTree.FromPolygons(vs, tris)
            for name, b, blo, bhi in body:
                if any(hi[k] < blo[k] - 0.01 or lo[k] > bhi[k] + 0.01 for k in range(3)):
                    continue
                if bvh.overlap(b):
                    hits.add(f"{name}@{a}")
                elif any(b.find_nearest(v, DOOR_CLEAR)[0] is not None for v in vs):
                    near.add(f"{name}@{a}")
        res[leaf.name] = {"hits": sorted(hits), "near": sorted(near)}
        ok &= not hits and not near
    return {"ok": ok, **res}


def panel_fit():
    """Every slot-panel item (its measured body at its anchor, m.item_box) inside its recess or the canister rack by
    PANEL_CLEAR, PANEL_GAP clear of every other item, and no body vertex inside it (its measured shape, m.item_contains:
    a socket round a canister, a filter's spigot or a battery's foot stays outside it)."""
    rooms = {"left": ((-m.UPPER_X, m.SLOT_H[0], m.RECESS_Z[0]), (-m.SLOT_BACK_X, m.SLOT_H[1], m.RECESS_Z[1])),
             "rack": ((-m.UPPER_X, m.RACK["h"][0], m.RACK["z"][0]), (-m.RACK["x"], m.RACK["h"][1], m.RACK["z"][1])),
             "right": ((m.SLOT_BACK_X, m.SLOT_H[0], m.RECESS_Z[0]), (m.UPPER_X, m.SLOT_H[1], m.RECESS_Z[1]))}
    boxes = {n: m.item_box(n) for n in m.PANEL}
    deps = bpy.context.evaluated_depsgraph_get()
    body = []
    for o in bpy.data.collections["GEO_RoverBody"].objects:
        if o.type == "MESH" and "role" in o:
            me = o.evaluated_get(deps).to_mesh()
            body += [(w.x, w.z, w.y) for w in (o.matrix_world @ v.co for v in me.vertices)]
            o.evaluated_get(deps).to_mesh_clear()
    res, ok = {}, True
    for n, (lo, hi) in boxes.items():
        room = next((r for r, (rlo, rhi) in rooms.items()
                     if all(lo[k] >= rlo[k] + PANEL_CLEAR - 1e-6 and hi[k] <= rhi[k] - PANEL_CLEAR + 1e-6 for k in range(3))), None)
        close = [o for o, (olo, ohi) in boxes.items() if o != n and all(lo[k] < ohi[k] + PANEL_GAP and olo[k] < hi[k] + PANEL_GAP for k in range(3))]
        intr = sum(1 for q in body if all(lo[k] < q[k] < hi[k] for k in range(3)) and m.item_contains(n, q))
        res[n] = {"room": room, "too_close": close, "body_in_item": intr}
        ok &= room is not None and not close and intr == 0
    return {"ok": ok, **res}


# where each slot item meets its socket: (item-local point on its measured shape, outward direction). The battery's
# terminal on its contact pad and its foot's four sides in a cup; the chip's legs in their socket; the filter's foot on
# its spigot's boss; the canister (4 sides) in the rack face 5 cm out of the recess back wall (the local y is set per
# canister in slot_sockets) and its valve end at its port
SEAT_PROBES = {
    "battery": [((0.0, -0.0845, 0.0), (0, -1, 0)), ((0.049, -0.065, 0.0), (1, 0, 0)), ((-0.049, -0.065, 0.0), (-1, 0, 0)),
                ((0.0, -0.065, 0.048), (0, 0, 1)), ((0.0, -0.065, -0.048), (0, 0, -1))],
    "chip": [((sx * 0.076, sy * 0.06, -0.055), (0, 0, -1)) for sx in (1, -1) for sy in (1, -1)],
    "filter": [((0.035, -0.1205, 0.0), (0, -1, 0)), ((-0.035, -0.1205, 0.0), (0, -1, 0))],
    "canister": [((0.106, None, 0.0), (1, 0, 0)), ((-0.106, None, 0.0), (-1, 0, 0)), ((0.0, None, 0.106), (0, 0, 1)),
                 ((0.0, None, -0.106), (0, 0, -1)), ((0.0, 0.194, 0.0), (0, 1, 0))],
}


def slot_sockets():
    """Every slot-panel item sits in its socket (the user's check-in 3 report: flat plates behind floating items did
    not look polished): from each SEAT_PROBES point on the item's measured shape, the body is within SEAT_GAP along the
    probe. {item: worst distance (None: nothing hit)}"""
    bvhs = _body_bvhs()
    res, ok = {}, True
    for name, (item, pos, rot) in m.PANEL.items():
        R, P = m.unity_rot(rot), Vector(pos)
        worst = 0.0
        for (px, py, pz), d in SEAT_PROBES[item]:
            if py is None:                                          # 5 cm out of the recess back wall, along the canister
                py = (R.transposed() @ (Vector((math.copysign(m.SLOT_BACK_X + 0.05, P.x), P.y, P.z)) - P)).y
            q, dq = P + R @ Vector((px, py, pz)), R @ Vector(d)
            hit = _first_hit(bvhs, m.U(q.x, q.y, q.z), m.U(dq.x, dq.y, dq.z).normalized())
            dist = hit[2] if hit else None
            worst = None if dist is None or worst is None else max(worst, dist)
        res[name] = None if worst is None else round(worst, 4)
        ok &= worst is not None and worst <= SEAT_GAP
    return {"ok": ok, **res}


def _first_hit(bvhs, origin, d):
    """The nearest (part, hit point, distance) along a ray, or None."""
    best = None
    for name, b, lo, hi in bvhs:
        hit = b.ray_cast(origin, d)
        if hit[0] is not None and (best is None or hit[3] < best[2]):
            best = (name, hit[0], hit[3])
    return best


def silhouette():
    """Probe rays for the one-hull look (spec 2026-09-28 and its check-in 1 revision): the side plane runs on from the
    bays to the cab at the window line, one roof from the tail to the windscreen, the lower hull comes down to its
    tucked-in edge between the front and middle wheels, and the snout (the hull, not the keel) down to the bumper."""
    bvhs = [b for b in _body_bvhs() if not b[0].startswith("RVG_")]
    fails = []
    for z in (-3.0, -0.8, 1.2, 1.9):                                   # the side plane at h 1.95 (bay doors at -3.0, -0.8)
        h = _first_hit(bvhs, m.U(3.0, 1.95, z), Vector((-1, 0, 0)))
        if not h or h[1].x < m.UPPER_X - 0.01:
            fails.append(f"side z {z}: {h and (h[0], round(h[1].x, 3))}")
    for z in (-3.5, -2.2, 0.5, 1.5):                                   # one roof (clear of the mast at z -0.85)
        h = _first_hit(bvhs, m.U(0.0, 5.0, z), Vector((0, 0, -1)))
        if not h or h[1].z < m.MODULE_ROOF - 0.01:
            fails.append(f"roof z {z}: {h and (h[0], round(h[1].z, 3))}")
    tuck = m.UPPER_X - (m.TUCK[0] - 1.40) * math.tan(math.radians(m.TUCK[1]))
    for z in (0.0, 0.8, 1.3):                                          # the lower hull (or its armour plate) at h 1.40, on its tuck
        h = _first_hit(bvhs, m.U(3.0, 1.40, z), Vector((-1, 0, 0)))
        if not h or h[0] not in ("RVB_HullLow", "RVB_PlatesLow") or h[1].x < tuck - 0.01:
            fails.append(f"skirt z {z}: {h and (h[0], round(h[1].x, 3))}")
    for x in (-0.40, 0.40):                                            # the snout, between the push bar's uprights (Task 3)
        for hh in (1.40, 1.60):
            h = _first_hit(bvhs, m.U(x, hh, 6.0), Vector((0, -1, 0)))
            if not h or h[0] not in ("RVB_Hull", "RVB_HullLow"):
                fails.append(f"nose x {x} h {hh}: {h and h[0]}")
    return {"ok": not fails, "fails": fails}


def coplanar(dist=2e-4, objects=None):
    """Visible z-fighting: a face whose centre lies on another part's face (within dist) with the same facing, unless
    1 mm in front of it is inside a third part (a buried back face). Face centres only: thin strips along buried edges
    are not seen. objects: check these instead of the rover's body, doors and fitted upgrades (the build stages).
    [(part, other part, Unity point)]"""
    deps = bpy.context.evaluated_depsgraph_get()
    parts = {}
    import blender_rover_upgrades as bu
    if objects is None:
        objects = [o for cn in ("GEO_RoverBody", "GEO_RoverDoors") + bu.FITTED
                   for o in (bpy.data.collections[cn].objects if cn in bpy.data.collections else [])]
    for o in objects:
        if o.type == "MESH" and "role" in o:
            ev = o.evaluated_get(deps)
            me = ev.to_mesh()
            vs = [o.matrix_world @ v.co for v in me.vertices]
            R = o.matrix_world.to_3x3()
            faces = [((R @ p.normal).normalized(), o.matrix_world @ p.center) for p in me.polygons]
            bvh = BVHTree.FromPolygons(vs, [tuple(p.vertices) for p in me.polygons])
            ev.to_mesh_clear()
            parts[o.name] = (faces, bvh)
    found = []
    for a, (faces, _) in parts.items():
        for n, cpt in faces:
            for b, (bfaces, bvh) in parts.items():
                if b == a:
                    continue
                loc, _, idx, _ = bvh.find_nearest(cpt, dist)
                if loc is None or n.dot(bfaces[idx][0]) < 0.999:
                    continue
                probe = cpt + n * 0.001
                if any(_inside(ob, probe) for k, (_, ob) in parts.items() if k not in (a, b)):
                    continue
                found.append((a, b, [round(cpt.x, 3), round(cpt.z, 3), round(cpt.y, 3)]))
                break
    return found


def hitch_mounted():
    """The tow arm's root sits in its mount: every arm vertex ahead of the receiver box's rear face is inside the box
    or the rear bumper."""
    deps = bpy.context.evaluated_depsgraph_get()
    o = bpy.data.objects["RVB_HitchJaw"]
    ev = o.evaluated_get(deps)
    me = ev.to_mesh()
    pts = [(w.x, w.z, w.y) for w in (o.matrix_world @ v.co for v in me.vertices)]
    ev.to_mesh_clear()
    z_rear = m.BUMPER["z"][1] - getattr(m, "HITCH_BOX", (0.0, 0.0, 0.0, 0.16))[3]
    mounts = [b for b in _body_bvhs() if b[0] in ("RVB_HitchMount", "RVB_Bumper")]
    loose = [p for p in pts if p[2] > z_rear + 0.005 and not any(_inside(b, m.U(*p)) for _, b, _, _ in mounts)]
    return {"ok": not loose, "loose": len(loose)}


CAM_CLEAR = 0.40


def cam_clear():
    """Each seated camera point keeps CAM_CLEAR from every body part but the seats (the head rests on its own)."""
    bvhs = [b for b in _body_bvhs() if not b[0].startswith(("RVB_Seat", "RVG_"))]
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        eye = m.U(*_apos("Camera" + who))
        near = min((hit[3] for hit in (b.find_nearest(eye) for _, b, _, _ in bvhs) if hit[0] is not None), default=9.0)
        res[who] = round(near, 3)
        ok &= near >= CAM_CLEAR
    return {"ok": ok, **res}


SUSP_TRAVEL = 0.5      # the WheelCollider's travel either way (suspension distance 1.0, target position 0.5)


def _arm_angle(lift, rest_h, rest_z):
    """CargoRover.AnimateSuspensionArms' angle (deg) for the wheel raised by lift, clamped to +-55."""
    a = math.degrees(math.atan2(rest_z, rest_h + lift) - math.atan2(rest_z, rest_h))
    return max(-55.0, min(55.0, (a + 180.0) % 360.0 - 180.0))


def shock_travel():
    """Each shock over the wheel's full travel (the arm clamped as CargoRover): its eyes' distance keeps the rod 2 cm
    short of the upper eye, the body clear of the lower spring seat and the rod 4 cm inside the body; and no tyre point
    (steered as its axle steers, raised up to BUMP, sampled densely) comes within MARGIN of the shock's widest part."""
    import blender_rover_wheels as w
    if "ShockBody" not in bpy.data.objects:
        return {"ok": False, "error": "no shocks built"}
    lo_len = max(w.SHOCK_ROD_L + 0.02, w.SHOCK_BODY_L + w.SPRING_AT[1] + 0.005)
    hi_len = w.SHOCK_BODY_L + w.SHOCK_ROD_L - 0.04
    res, ok = {}, True
    for name, z, mode in m.AXLES:
        for s in (-1, 1):
            hub = (s * m.WHEEL_X, m.WHEEL_H, z)
            piv = (s * m.ARM_PIVOT_X, m.ARM_PIVOT_H, z + m.ARM_REACH_Z)
            top, bot = Vector(m.shock_top(s, z)), Vector(m.shock_bottom(s, z))
            rh, rz = hub[1] - piv[1], hub[2] - piv[2]
            y, zz = bot.y - piv[1], bot.z - piv[2]
            bad, lens = [], []
            for lift in (-SUSP_TRAVEL, -m.BUMP, 0.0, m.BUMP / 2, m.BUMP, SUSP_TRAVEL):
                a = math.radians(_arm_angle(lift, rh, rz))
                b = Vector((bot.x, piv[1] + y * math.cos(a) - zz * math.sin(a), piv[2] + y * math.sin(a) + zz * math.cos(a)))
                L = (top - b).length
                lens.append(round(L, 3))
                if not lo_len <= L <= hi_len:
                    bad.append(f"length {L:.3f} at lift {lift}")
                if lift > m.BUMP:
                    continue
                p0 = m.U(*top)
                d = m.U(*b) - p0
                for deg in (range(-40, 41, 5) if mode else (0,)):
                    for p in _tyre_points(hub, s, deg, lift, n_around=96):
                        t = max(0.0, min(1.0, (p - p0).dot(d) / d.length_squared))
                        if (p0 + d * t - p).length < w.SHOCK_R + MARGIN:
                            bad.append(f"tyre at {deg} deg, lift {lift}")
                            break
            res[f"{name}{'L' if s < 0 else 'R'}"] = {"lengths": lens, "bad": sorted(set(bad))[:4]}
            ok &= not bad
    return {"ok": ok, **res}


POSE_FIT = (2.0, 0.0, 0.03)   # the seated pose's back within 2 deg of the backrest, 0-3 cm in front of it
END_TOL = 0.08                # the collision shape's front and rear within 8 cm of the body's


def seat_pose():
    """The game's Seated pose sits on our seats (the user's check-in 3 report: he leant back through the chair, his feet
    in the view of the screen): turned by the seat anchor's pitch, the pose's back (POSE) lies within POSE_FIT of the
    backrest's front edge (SEAT_BACK) in angle and in front of it at the hips."""
    (z0, h0), (z1, h1) = m.SEAT_BACK
    back_deg = math.degrees(math.atan2(z0 - z1, h1 - h0))                      # the backrest's lean back from upright
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        x, h, z = _apos("Seat" + who)
        pitch = bpy.data.objects["ANC_Seat" + who]["unity_rot"][0]
        p = math.radians(pitch)
        bh, bz = h + m.POSE["back"] * math.sin(p), z - m.POSE["back"] * math.cos(p)   # the pose's back at the hips
        line_z = m.SEAT_Z + z0 + (bh - (m.CAB_LOW + 0.19) - h0) * (z1 - z0) / (h1 - h0)
        lean, gap = m.POSE["recline"] - pitch, bz - line_z
        res[who] = {"lean": round(lean, 2), "backrest": round(back_deg, 2), "gap": round(gap, 4)}
        ok &= abs(lean - back_deg) <= POSE_FIT[0] and POSE_FIT[1] <= gap <= POSE_FIT[2]
    return {"ok": ok, **res}


def ends_covered():
    """The collision shape reaches the body's front and rear (review M3: the front bumper sank 14 cm into what it met
    head-on): the colliders' front-most and rear-most faces within END_TOL of the body's front-most and rear-most
    points (its solid parts, not the glass, the door leaves or the tow arm: a trailer's tongue must reach its pin)."""
    import blender_build_rover as br
    boxes = br.collider_boxes(m)
    front = max(b["center"][2] + b["size"][2] / 2 for b in boxes)
    rear = min(b["center"][2] - b["size"][2] / 2 for b in boxes)
    zs = [v for name, _, lo, hi in _body_bvhs() if not name.startswith(("RVG_", "BayDoor", "RVB_HitchJaw", "RVB_HitchPin", "RVB_HitchLatch")) for v in (lo.y, hi.y)]
    bf, bb = max(zs), min(zs)
    return {"ok": bf - front <= END_TOL and rear - bb <= END_TOL, "front": [round(bf, 3), round(front, 3)], "rear": [round(bb, 3), round(rear, 3)]}


FRAME_PROBE = (0.015, 0.004)   # a pane's frame is found 1.5 cm outside its outline, 4 mm off the surface
SPILL_CLEAR = 2.0              # no spot light's cone meets the rover's own body nearer than this


def window_frames():
    """Every cab window pane is framed (the user's check-in 3 report: the corner panes and the quarter windows had no
    gaskets, the windscreen and the door windows did): points 1.5 cm outside the middle of each outline edge, 4 mm off
    the surface, are inside the window frames."""
    fr = [b for n, b, _, _ in _body_bvhs() if n == "RVB_WindowFrames"]
    bad = []
    for k, (M, poly, _) in enumerate(m.cab_panes()):
        ccw = sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly))) > 0
        for i in range(len(poly)):
            (u0, v0), (u1, v1) = poly[i], poly[(i + 1) % len(poly)]
            L = math.hypot(u1 - u0, v1 - v0)
            if L < 0.05:
                continue
            nu, nv = ((v1 - v0) / L, -(u1 - u0) / L) if ccw else (-(v1 - v0) / L, (u1 - u0) / L)   # outward in (u, v)
            q = M((u0 + u1) / 2 + nu * FRAME_PROBE[0], (v0 + v1) / 2 + nv * FRAME_PROBE[0], FRAME_PROBE[1])
            if not any(_inside(b, q) for b in fr):
                bad.append((k, i))
    return {"ok": not bad, "unframed": bad}


SPOT_FIELDS = {"SideLight": "WorkLightSpot", "RearLight": "WorkLightSpot", "Headlight": "HeadlightSpot", "LightBar": "LightBarSpot"}


def light_spill():
    """Our spot lights light the ground, not the rover (the user's check-in 3 reports: one light between each side's two
    work lamps lit the bay doors under it; the headlights and the roof light bar did not light up): every ray of each
    light's cone (the axis and three rings to its spot angle's edge, SPOT_FIELDS), from its anchor, meets no body part
    nearer than SPILL_CLEAR; every family of lights is there."""
    import blender_build_rover as br
    import blender_rover_upgrades as bu
    bvhs = _body_bvhs(bu.FITTED)
    res, ok, seen = {}, True, set()
    for o in bpy.data.collections["ANC_Rover"].objects:
        name = o.name[4:]
        fam = next((f for f in SPOT_FIELDS if name.startswith(f)), None)
        if fam is None:
            continue
        seen.add(fam)
        half = math.radians(br.FIELDS.get(SPOT_FIELDS[fam], 110.0) / 2)
        R = m.unity_rot(o["unity_rot"])
        ax = R @ Vector((0.0, 0.0, 1.0))
        up = (R @ Vector((0.0, 1.0, 0.0))).normalized()
        side = ax.cross(up).normalized()
        dirs = [ax] + [(ax * math.cos(half * f) + (up * math.cos(t) + side * math.sin(t)) * math.sin(half * f)).normalized()
                       for f in (1 / 3, 2 / 3, 1.0) for t in (2 * math.pi * k / 12 for k in range(12))]
        at = m.U(*o["unity_pos"])
        near = min((hit for d in dirs for _, b, _, _ in bvhs
                    if (hit := (b.ray_cast(at, m.U(*d))[3] or 99.0)) is not None), default=99.0)
        res[name] = round(near, 2)
        ok &= near >= SPILL_CLEAR
    missing = sorted(set(SPOT_FIELDS) - seen)
    return {"ok": ok and not missing, "missing": missing, **res}


def labels_mounted():
    """The tank labels are mounted (the user's report: at the recess mouth the WASTE sign hung in the air, past the
    side's 45 deg lean): each block's back is in the rack face, its outer top edge inside the hull's outline, WASTE's
    top in the recess ceiling, AIR's foot in its floor."""
    face, res, ok = m.SLOT_BACK_X + m.RACK_FACE, {}, True
    for text, lo, hi in m.tank_labels():
        x_out, x_in, h_bot, h_top = -lo[0], -hi[0], lo[1], hi[1]            # the left side: |x| = -x
        fails = []
        if x_in > face:
            fails.append(f"back {round(x_in, 3)} off the rack face {face}")
        if x_out > m.lean_x(h_top, m.MOD_LEAN) + 1e-6:
            fails.append(f"outer top edge {round(x_out, 3)} outside the hull ({round(m.lean_x(h_top, m.MOD_LEAN), 3)})")
        if text == "WASTE" and h_top < m.SLOT_H[1]:
            fails.append("not up in the ceiling")
        if text == "AIR" and h_bot > m.SLOT_H[0]:
            fails.append("not down on the floor")
        res[text] = fails
        ok &= not fails
    return {"ok": ok and len(res) == 2, **res}


def front_glow():
    """The front lamps' lenses light up with the headlights (the user's check-in 3 report): the glow mesh
    (GEO_RoverGlow, shown in game while they are on) covers every outer face of the nose's LED bars and the roof light
    bar's lenses, 0.5-4 mm in front of it."""
    deps = bpy.context.evaluated_depsgraph_get()
    col = bpy.data.collections.get("GEO_RoverGlow")
    glow = []
    for o in (col.objects if col else []):
        if o.type == "MESH" and "role" in o:
            me = o.evaluated_get(deps).to_mesh()
            glow.append(BVHTree.FromPolygons([o.matrix_world @ v.co for v in me.vertices], [tuple(p.vertices) for p in me.polygons]))
            o.evaluated_get(deps).to_mesh_clear()
    if not glow:
        return {"ok": False, "error": "no glow mesh (GEO_RoverGlow)"}
    band = (m.band_face()[0](0.0, 0.0, 1.0) - m.band_face()[0](0.0, 0.0, 0.0)).normalized()     # the light band's outward normal
    bare, faces = [], 0
    for pname, out in (("RVB_LedLens", band), ("RVB_LightBarLens", m.U(0.0, 0.0, 1.0))):
        o = bpy.data.objects.get(pname)
        if o is None:
            bare.append(pname); continue
        me = o.evaluated_get(deps).to_mesh()
        R = o.matrix_world.to_3x3()
        for poly in me.polygons:
            n = (R @ poly.normal).normalized()
            c = o.matrix_world @ poly.center
            if n.dot(out) < 0.999 or (pname == "RVB_LightBarLens" and (abs(c.x) > 0.70 or c.y < 0.70)):   # the lenses' outer
                continue                                                        # faces (the light bar's, not the work lamps' edges)
            faces += 1
            if not any((h := b.ray_cast(c + n * 1e-4, n, 0.004))[0] is not None and h[3] >= 0.0004 for b in glow):
                bare.append(f"{pname}@{round(c.x, 2)},{round(c.z, 2)}")
        o.evaluated_get(deps).to_mesh_clear()
    return {"ok": not bare and faces == 6, "faces": faces, "bare": bare}


def hitch_covered():
    """The tow arm is solid (the user's check-in 3 report: the hitch had no collision): its centre line from behind the
    receiver box to the jaw is inside the solid colliders; the pin keeps 17 cm of open space round it (a hitched
    trailer's tongue collider starts 1 cm behind its ring, and unhitching must not find it inside a rover collider)."""
    import blender_build_rover as br
    boxes = br.collider_boxes(m)
    inside = lambda p, b: all(abs(p[k] - b["center"][k]) <= b["size"][k] / 2 + 1e-4 for k in range(3))
    hx, hh, hz = m.HITCH
    z0, zj = m.BUMPER["z"][1] - m.HITCH_BOX[3] - 0.02, hz + 0.17
    line = [(0.0, 0.7275, z0 + (zj - z0) * k / 7) for k in range(8)]
    bare = [round(p[2], 3) for p in line if not any(inside(p, b) for b in boxes)]
    gap = min(math.dist((hx, hh, hz), [min(max(v, c - sz / 2), c + sz / 2) for v, c, sz in zip((hx, hh, hz), b["center"], b["size"])]) for b in boxes)
    return {"ok": not bare and gap >= 0.17 - 1e-4, "bare": bare, "pin_gap": round(gap, 3)}


VENT_EDGE = 0.015      # a vent frame's outline stays 1.5 cm inside its pod's rear face (past the bevel's start)


def vents_on_pods():
    """Each tail-pod vent sits on its pod (the user's report at check-in 3: the left vent's frame hung past the pod's
    lean): its frame outline's corners, moved VENT_EDGE further out and 5 mm into the pod, are inside the pod."""
    bvhs = {n: b for n, b, _, _ in _body_bvhs() if n.startswith("RVB_TailPod")}
    z = m.REAR_Z - 0.12 + 0.005
    (v0, v1), e = m.REAR_VENT_H, VENT_EDGE
    bad = []
    for a, b, _ in m.REAR_VENTS:
        pod = bvhs["RVB_TailPod" + ("R" if a > 0 else "L")]
        for x, h in ((a - e, v0 - e), (b + e, v0 - e), (a - e, v1 + e), (b + e, v1 + e)):
            if not _inside(pod, m.U(x, h, z)):
                bad.append((round(x, 3), round(h, 3)))
    return {"ok": not bad, "outside": bad}


ENVELOPE = ("RVB_Hull", "RVB_HullLow", "RVB_Keel", "RVB_Bumper", "RVB_TailPodL", "RVB_TailPodR", "RVB_FrontBumper",
            "RVB_Equipment", "RVB_HitchMount", "RVB_Winch", "RVB_HitchJaw")
COLLIDER_TOL = 0.05    # a solid collider may reach 5 cm past the body (a stepped box's corner on a chamfer)


def _envelope_bvhs():
    """The body's solids as the colliders see them: the hull without its cab-room, seat-platform, footwell and window
    cuts (the cab is solid to the game: the seated cameras sit inside one collider), the keel, the bumpers and the
    winch, the tail pods and the gear under the hull."""
    muted = [md for hp in m.hull_parts() for md in hp.modifiers
             if md.type == "BOOLEAN" and md.name.startswith(("Room", "Plat", "Dip", "Win")) and md.show_viewport]
    for md in muted:
        md.show_viewport = False
    try:
        return [t for t in _body_bvhs() if t[0] in ENVELOPE]
    finally:
        for md in muted:
            md.show_viewport = True


def _face_points(c, s, step=0.30):
    """A grid of points (Unity) on a box's six faces (centre c, size s), at most `step` apart."""
    n = [max(2, math.ceil(v / step) + 1) for v in s]
    pts = []
    for axis in range(3):
        a1, a2 = (axis + 1) % 3, (axis + 2) % 3
        for side in (-0.5, 0.5):
            for i in range(n[a1]):
                for j in range(n[a2]):
                    p = [0.0, 0.0, 0.0]
                    p[axis] = c[axis] + side * s[axis]
                    p[a1] = c[a1] + (i / (n[a1] - 1) - 0.5) * s[a1]
                    p[a2] = c[a2] + (j / (n[a2] - 1) - 0.5) * s[a2]
                    pts.append(p)
    return pts


def colliders_inside():
    """The exported solid colliders (blender_build_rover.collider_boxes) follow the body (spec 2026-09-28): every point
    of a grid on each box's faces is inside one of the body's solids or within COLLIDER_TOL of its surface. A box
    reaching into a bay, a recess, the rack or an arch, or past a chamfer, reaches out of the body there."""
    import blender_build_rover as br
    bvhs = _envelope_bvhs()
    boxes = br.collider_boxes(m)
    bad = {}
    for i, b in enumerate(boxes):
        worst = 0.0
        for p in _face_points(b["center"], b["size"]):
            q = m.U(*p)
            if any(all(lo[k] - COLLIDER_TOL <= q[k] <= hi[k] + COLLIDER_TOL for k in range(3))
                   and (t.find_nearest(q, COLLIDER_TOL)[0] is not None or _inside(t, q)) for _, t, lo, hi in bvhs):
                continue
            worst = max(worst, min(t.find_nearest(q)[3] for _, t, _, _ in bvhs))
        if worst > COLLIDER_TOL:
            bad[i] = [round(worst, 3), b["center"], b["size"]]
    return {"ok": not bad, "boxes": len(boxes), "out": bad}


def _upgrade_checks():
    import blender_rover_upgrade_checks as uc
    return uc.upgrade_checks()


def rover_checks():
    if not bpy.data.collections.get("GEO_RoverBody") or not bpy.data.collections["GEO_RoverBody"].objects:
        return {"ok": False, "error": "GEO_RoverBody missing: run blender_rover_model.build() first"}
    res = {"look": silhouette(), "bay_fit": bay_fit(), "steer": steer_sweep(), "arms": arm_envelope(), "tow": tow_clearance(),
           "belly": belly(), "view": cab_view(), "screen": screen_facing(), "legs": leg_room(), "doors": door_swing(), "panel": panel_fit(),
           "head": cam_clear(), "hitch": hitch_mounted(), "shocks": shock_travel(), "colliders": colliders_inside(),
           "vents": vents_on_pods(), "pose": seat_pose(), "ends": ends_covered(),
           "frames": window_frames(), "spill": light_spill(), "towarm": hitch_covered(), "sockets": slot_sockets(),
           "glow": front_glow(), "labels": labels_mounted(), "upgrades": _upgrade_checks()}
    res["ok"] = all(v["ok"] for v in res.values())
    return res


SHOW = ("GEO_RoverBody", "GEO_RoverGlass", "GEO_RoverDoors", "GEO_RoverWheels")
VIEWS = {   # (look-at Blender point, view rotation, distance, projection)
    "side": (Vector((0, -0.8, 1.9)), Euler((1.5708, 0, 1.5708)), 9.6, "ORTHO"),
    "top": (Vector((0, -0.8, 0)), Euler((0, 0, 0)), 9.8, "ORTHO"),
    "back": (Vector((0, -0.8, 1.9)), Euler((1.5708, 0, 0)), 5.2, "ORTHO"),
    "front": (Vector((0, -0.8, 1.9)), Euler((1.5708, 0, 3.14159)), 5.2, "ORTHO"),
    "threeq_front": (Vector((0, -0.4, 1.5)), Euler((1.22, 0, 2.35)), 11.0, "PERSP"),
    "threeq_rear": (Vector((0, -1.2, 1.5)), Euler((1.22, 0, 0.75)), 11.0, "PERSP"),
}


def _grab_view(area, path, loc, rot, dist, persp):
    """The 3D view (its shading and overlays) drawn offscreen into a PNG from a view (look-at point, rotation,
    distance, PERSP/ORTHO). A screenshot of the area is black when Blender's window is not presented (minimized or
    behind a full-screen game), and the region's own matrices only follow a real redraw, so both matrices are built
    here: the viewport's lens on its 72 mm effective sensor (a 36 mm sensor at zoom 2), ortho scale dist * 72 / lens."""
    import gpu
    import numpy as np
    region = next(r for r in area.regions if r.type == "WINDOW")
    space = area.spaces.active
    W, H = region.width, region.height
    view = (Matrix.Translation(loc) @ rot.to_matrix().to_4x4() @ Matrix.Translation((0.0, 0.0, dist))).inverted()
    cd = bpy.data.cameras.new("_rover_grab_cam")
    cam = bpy.data.objects.new("_rover_grab_cam", cd)
    try:
        cd.lens, cd.sensor_width, cd.sensor_fit = space.lens, 72.0, "AUTO"
        cd.clip_start, cd.clip_end = space.clip_start, space.clip_end
        if persp == "ORTHO":
            cd.type, cd.ortho_scale = "ORTHO", dist * 72.0 / space.lens
        proj = cam.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(), x=W, y=H)
    finally:
        bpy.data.objects.remove(cam)
        bpy.data.cameras.remove(cd)
    off = gpu.types.GPUOffScreen(W, H)
    try:
        off.draw_view3d(bpy.context.scene, bpy.context.view_layer, space, region, view, proj, do_color_management=True)
        with off.bind():
            buf = gpu.state.active_framebuffer_get().read_color(0, 0, W, H, 4, 0, "UBYTE")
        px = np.asarray(buf).ravel(order="K").astype(np.float32) / 255.0   # memory order: its reported strides are wrong
    finally:
        off.free()
    img = bpy.data.images.get("_rover_grab") or bpy.data.images.new("_rover_grab", W, H, alpha=True)
    if tuple(img.size) != (W, H):
        img.scale(W, H)
    img.pixels.foreach_set(px)
    img.filepath_raw, img.file_format = path, "PNG"
    img.save()


def render_views(out_dir):
    """Viewport images of the new rover alone (solid shading, object colours = palette roles), drawn offscreen."""
    os.makedirs(out_dir, exist_ok=True)
    lc = bpy.context.view_layer.layer_collection
    was = {c.name: c.hide_viewport for c in lc.children}
    for c in lc.children:
        c.hide_viewport = c.name not in SHOW
    area = next(a for a in bpy.context.screen.areas if a.type == "VIEW_3D")
    space = area.spaces.active
    shading = (space.shading.type, space.shading.color_type)
    ui = (space.overlay.show_overlays, space.show_region_header, space.show_region_tool_header, space.show_gizmo)
    space.shading.type = "SOLID"
    if space.shading.color_type != "TEXTURE":                    # render_mockup_views asks for images
        space.shading.color_type = "OBJECT"
    space.overlay.show_overlays = space.show_region_header = space.show_region_tool_header = space.show_gizmo = False
    r3 = space.region_3d
    saved = {}
    try:
        for name, (loc, rot, dist, persp) in VIEWS.items():
            r3.view_location, r3.view_rotation, r3.view_distance, r3.view_perspective = loc, rot.to_quaternion(), dist, persp
            bpy.ops.wm.redraw_timer(type="DRAW_WIN_SWAP", iterations=1)
            path = os.path.join(out_dir, f"rover_{name}.png")
            _grab_view(area, path, loc, rot, dist, persp)
            saved[name] = path
    finally:
        for c in lc.children:
            c.hide_viewport = was.get(c.name, c.hide_viewport)
        space.shading.type, space.shading.color_type = shading
        space.overlay.show_overlays, space.show_region_header, space.show_region_tool_header, space.show_gizmo = ui
    return saved


CAB_VIEWS = {   # the cab without its glass (solid shading hides what is behind a pane)
    "cab_interior": (Vector((0.0, 1.90, 1.95)), Euler((1.05, 0, 2.75)), 4.4, "PERSP"),
    "cab_interior_side": (Vector((0.0, 1.60, 1.95)), Euler((1.32, 0, 1.40)), 4.0, "PERSP"),
    "cab_from_seat": (Vector((0.0, 2.21, 1.95)), Euler((1.131, 0, -0.515)), 0.95, "PERSP"),   # at the screen, from the driver's eye (SEAT_Z 1.45)
}


def render_cab_views(out_dir):
    """The cab interior without its glass: from the front, the side, and the driver's seat."""
    global SHOW, VIEWS
    saved = SHOW, VIEWS
    try:
        SHOW, VIEWS = tuple(n for n in SHOW if n != "GEO_RoverGlass"), CAB_VIEWS
        return render_views(out_dir)
    finally:
        SHOW, VIEWS = saved


PANEL_VIEWS = {   # each slot panel from beside the rover
    "panel_left": (Vector((-1.35, 0.56, 2.40)), Euler((1.5708, 0, -1.5708)), 2.4, "PERSP"),
    "panel_left_rack": (Vector((-1.35, 0.45, 2.40)), Euler((1.45, 0, -2.05)), 2.0, "PERSP"),
    "panel_right": (Vector((1.35, 0.56, 2.40)), Euler((1.5708, 0, 1.5708)), 2.4, "PERSP"),
}


def render_panel_views(out_dir):
    """Both slot panels (and into the canister rack) from beside the rover."""
    global VIEWS
    saved = VIEWS
    try:
        VIEWS = PANEL_VIEWS
        return render_views(out_dir)
    finally:
        VIEWS = saved


def render_doors_open(out_dir):
    """The standard views with every bay door open (turned about its hinge for the render, restored after)."""
    bpy.context.view_layer.update()
    leaves = [o for o in bpy.data.collections["GEO_RoverDoors"].objects if o.parent is None and "hinge" in o]
    keep = {o.name: o.matrix_world.copy() for o in leaves}
    try:
        for o in leaves:
            hx, hh, hz = o["hinge"]
            p = m.U(hx, hh, hz)
            R = Matrix.Rotation(-math.radians((1 if hx > 0 else -1) * m.DOOR_OPEN_DEG), 4, "Y")
            o.matrix_world = Matrix.Translation(p) @ R @ Matrix.Translation(-p) @ o.matrix_world
        bpy.context.view_layer.update()
        return render_views(out_dir)
    finally:
        for o in leaves:
            o.matrix_world = keep[o.name]
        bpy.context.view_layer.update()


GEAR_VIEWS = {   # low views of the running gear (tune the look-at if a tyre hides the shock)
    "gear_front_low": (Vector((0.9, 2.3, 0.9)), Euler((1.50, 0, 2.30)), 4.0, "PERSP"),
    "gear_side_low": (Vector((0.9, -0.6, 0.8)), Euler((1.52, 0, 1.40)), 7.0, "PERSP"),
}


def render_gear_views(out_dir):
    """Low views of the running gear: the front shock, arm and knuckle from ahead and outside; the chassis along the side."""
    global VIEWS
    saved = VIEWS
    try:
        VIEWS = GEAR_VIEWS
        return render_views(out_dir)
    finally:
        VIEWS = saved


def render_mockup_views(out_dir, png):
    """The cab with the dash mockup on its screen (a preview plane PRV_ScreenMockup, never exported: no role). Solid
    TEXTURE shading shows images but not object colours, so every shown part gets a temporary material in its role
    colour for the render; all of it is removed afterwards."""
    S, L, _ = m.dash_slope()
    vc, (fw, fh) = L / 2, m.SCREEN
    me = bpy.data.meshes.new("PRV_ScreenMockup")
    pts = [S(u, v, 0.0035) for u, v in ((-fw / 2, vc - fh / 2), (fw / 2, vc - fh / 2), (fw / 2, vc + fh / 2), (-fw / 2, vc + fh / 2))]
    me.from_pydata(pts, [], [(0, 1, 2, 3)])
    uv = me.uv_layers.new()
    for li, (a, b) in zip(range(4), ((0, 0), (1, 0), (1, 1), (0, 1))):     # u = rover x: the image reads left to right from the seats
        uv.data[li].uv = (a, b)
    ob = bpy.data.objects.new("PRV_ScreenMockup", me)
    bpy.data.collections["GEO_RoverBody"].objects.link(ob)
    img = bpy.data.images.load(png, check_existing=True)
    mat = bpy.data.materials.new("PRV_Mockup")
    mat.use_nodes = True
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = img
    mat.node_tree.links.new(tex.outputs["Color"], mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"])
    mat.node_tree.nodes.active = tex
    me.materials.append(mat)
    temp = []
    for cn in SHOW:
        col = bpy.data.collections.get(cn)
        for o in (col.objects if col else []):
            if o.type == "MESH" and "role" in o and not o.data.materials:
                tm = bpy.data.materials.new("PRV_" + o["role"])
                tm.diffuse_color = g.ROLE_COLOUR[o["role"]]
                o.data.materials.append(tm)
                temp.append((o, tm))
    area = next(a for a in bpy.context.screen.areas if a.type == "VIEW_3D")
    space = area.spaces.active
    saved = space.shading.color_type
    try:
        space.shading.color_type = "TEXTURE"
        return render_cab_views(out_dir)
    finally:
        space.shading.color_type = saved
        for o, tm in temp:
            o.data.materials.clear()
            bpy.data.materials.remove(tm)
        bpy.data.objects.remove(ob, do_unlink=True)
        bpy.data.meshes.remove(me)
        bpy.data.materials.remove(mat)
