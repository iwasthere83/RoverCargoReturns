"""Checks of the original rover's upgrade parts (spec 2026-10-07 section 3), run inside blender_rover_checks.rover_checks
after blender_rover_upgrades.build(): the shutters' view, the
thruster pods, the side steps, the shocks, the fender flares' size, and the removal colliders' keep-outs. The fixed parts also join the existing
checks (steer, arms, tow, doors, spill, coplanar)."""
import math

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

import blender_rover_checks as rc
import blender_rover_model as m
import blender_rover_upgrades as bu
import blender_rover_wheels as rw

SHUTTER_LIMIT = 0.25    # the bars hide at most 25 % of the view through the windscreen (spec)
POD_CLEAR = 0.02
SAMPLE = 0.02


def _mesh(ob):
    """An object's evaluated mesh in world space: (verts [Vector], tris [(i, j, k)])."""
    deps = bpy.context.evaluated_depsgraph_get()
    e = ob.evaluated_get(deps)
    me = e.to_mesh()
    me.calc_loop_triangles()
    vs = [ob.matrix_world @ v.co for v in me.vertices]
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    e.to_mesh_clear()
    return vs, tris


def _objs(coll):
    c = bpy.data.collections.get(coll)
    return [o for o in (c.objects if c else []) if o.type == "MESH" and "role" in o]


def _samples(vs, tris):
    """Points over a mesh's surface, about SAMPLE apart."""
    pts = []
    for a, b, c in tris:
        A, B, C = vs[a], vs[b], vs[c]
        n = max(1, math.ceil(max((B - A).length, (C - A).length, (C - B).length) / SAMPLE))
        for i in range(n + 1):
            for j in range(n + 1 - i):
                pts.append(A + (B - A) * (i / n) + (C - A) * (j / n))
    return pts


def _bvh(objs):
    vs, tris = [], []
    for o in objs:
        v, t = _mesh(o)
        tris += [(a + len(vs), b + len(vs), c + len(vs)) for a, b, c in t]
        vs += v
    return BVHTree.FromPolygons(vs, tris) if vs else None


def shutter_view():
    """From both seated eyes, the fraction of sight lines through the windscreen (9 x 5 points across the pane, 30 cm
    beyond the glass) that a shutter part blocks: above 0 (the windscreen has its grille) and at most SHUTTER_LIMIT."""
    armour = _bvh(_objs(bu.ARMOUR))
    if armour is None:
        return {"ok": False, "error": "no armour built"}
    M, L = m.windscreen_face()
    targets = [M(f * m.WINDSCREEN_HALF, L * k, 0.30) for f in (-0.8, -0.6, -0.4, -0.2, 0.0, 0.2, 0.4, 0.6, 0.8)
               for k in (0.2, 0.35, 0.5, 0.65, 0.8)]
    res, ok = {}, True
    for who in ("Driver", "Passenger"):
        eye = m.U(*rc._apos("Camera" + who))
        hit = sum(1 for t in targets if armour.ray_cast(eye, (t - eye).normalized(), (t - eye).length)[0] is not None)
        frac = hit / len(targets)
        res[who] = round(frac, 3)
        ok &= 0 < frac <= SHUTTER_LIMIT
    return {"ok": ok, **res}


def flares():
    """The fender flares are big (the user, check-in 1: the riding fenders were tiny): over every wheel arch, on both
    sides, the fairings reach out to the tyre's outer face + 4 cm or more, their top at or above the arch's top."""
    objs = [o for o in _objs(bu.FAIRINGS) if o.name.startswith("RVU_Flares")]
    if not objs:
        return {"ok": False, "error": "no flares built"}
    vs = [v for o in objs for v in _mesh(o)[0]]
    res, ok = {}, True
    for k, (zf, zr) in enumerate(m.arches()):
        for s in (-1, 1):
            pts = [v for v in vs if v.x * s > 0 and zr - 0.70 <= v.y <= zf + 0.70]      # the whole arch (its corners)
            reach = max((abs(v.x) for v in pts), default=0.0)
            top = max((v.z for v in pts), default=0.0)
            res[f"arch{k}{'L' if s < 0 else 'R'}"] = [round(reach, 3), round(top, 3)]
            ok &= reach >= m.TYRE_OUT + 0.04 and top >= m.ARCH_H
    return {"ok": ok, **res}


def pods():
    """Four pods: pod 0 modelled, the others the same mesh moved to their origins. Each keeps POD_CLEAR from the body
    away from its clamp on the rail, its nozzle exit is POD top over its origin, and nothing is above the exit."""
    objs = _objs(bu.THRUSTERS)
    if not objs:
        return {"ok": False, "error": "no pods built"}
    vs, tris = [], []
    for o in objs:
        v, t = _mesh(o)
        tris += [(a + len(vs), b + len(vs), c + len(vs)) for a, b, c in t]
        vs += v
    pts = _samples(vs, tris)
    body = rc._body_bvhs()
    above = rc._body_bvhs(bu.FITTED)
    p0 = bu.pod_positions()[0]
    top = max(v.z for v in vs) - p0[1]
    res, ok = {"exit": round(top, 4)}, abs(top - bu.POD["top"]) <= 0.005
    for k, (x, h, z) in enumerate(bu.pod_positions()):
        d = m.U(x, h, z) - m.U(*p0)
        if x < 0:                                            # the mesh is symmetric about its own x: mirror its offsets
            moved = [Vector((2 * m.U(*p0).x - q.x, q.y, q.z)) + d for q in pts]
        else:
            moved = [q + d for q in pts]
        free = [q for q in moved if not (abs(abs(q.x) - bu.POD["x"]) < 0.05 and abs(q.z - h) < 0.05)]   # off the clamp
        hits = rc._hits(free, body, POD_CLEAR)
        exit_pt = m.U(x, h + top + 0.005, z)
        blocked = any(b.ray_cast(exit_pt, Vector((0, 0, 1)), 3.0)[0] is not None for _, b, _, _ in above)
        res[f"pod{k}"] = {"hits": sorted(set(hits))[:4], "blocked_above": blocked}
        ok &= not hits and not blocked
    return {"ok": ok, **res}


def steps_open():
    """No upgrade part in the space above either side step's tread."""
    n = 0
    for s in (1, -1):
        xa, xb = sorted((s * (m.tuck_x(1.45) - 0.03), s * 1.45))
        for cn in bu.FITTED:
            for o in _objs(cn):
                vs, _ = _mesh(o)
                n += sum(1 for v in vs if xa < v.x < xb and 1.49 < v.z < 1.95 and 0.97 < v.y < 1.40)
    return {"ok": n == 0, "inside": n}


def shocks_clear():
    """Every shock (its axis from the upper eye to the lower, the arm turned over the full travel) keeps its widest
    radius + 2 cm from the fixed upgrades."""
    parts = rc._body_bvhs(bu.FITTED)
    parts = [b for b in parts if b[0].startswith("RVU_")]
    bad = []
    for name, z, mode in m.AXLES:
        for s in (-1, 1):
            hub = (s * m.WHEEL_X, m.WHEEL_H, z)
            piv = (s * m.ARM_PIVOT_X, m.ARM_PIVOT_H, z + m.ARM_REACH_Z)
            top, bot = Vector(m.shock_top(s, z)), Vector(m.shock_bottom(s, z))
            rh, rz = hub[1] - piv[1], hub[2] - piv[2]
            y, zz = bot.y - piv[1], bot.z - piv[2]
            for lift in (-rc.SUSP_TRAVEL, 0.0, m.BUMP, rc.SUSP_TRAVEL):
                a = math.radians(rc._arm_angle(lift, rh, rz))
                b = Vector((bot.x, piv[1] + y * math.cos(a) - zz * math.sin(a), piv[2] + y * math.sin(a) + zz * math.cos(a)))
                axis = [m.U(*(top + (b - top) * (k / 10))) for k in range(11)]
                if rc._hits(axis, parts, rw.SHOCK_R + 0.02):
                    bad.append(f"{name}{'L' if s < 0 else 'R'} at lift {lift}")
    return {"ok": not bad, "bad": bad[:6]}


def triggers():
    """The removal colliders touch no keep-out box (the bay door leaves, the slot panel's recesses, the cab doors'
    entry zone, the cab's room) and every group keeps at least one: each part's islands (blender_rover_upgrades
    .part_boxes; the grilles excepted) and one oriented box per window grille (grille_boxes, kept only clear of every
    keep-out; their Unity Euler angles round-trip to their axes)."""
    keep = bu.keep_out()
    res, ok = {}, True
    for cn in bu.FITTED:
        objs = [o for o in _objs(cn) if not o.name.startswith("RVU_Grille")]
        boxes = [b for o in objs for b in bu.part_boxes(o, keep)]
        n = len(boxes)
        if cn == bu.ARMOUR:
            grilles = [gb for gb in bu.grille_boxes() if not any(bu.obb_touches(gb[0], gb[1], gb[3], k) for k in keep)]
            n += len(grilles)
            for c, s, rot, axes in bu.grille_boxes():
                R = bu.euler_matrix(rot)
                ok &= all(abs(R[d][a] - axes[a][d]) < 1e-3 for a in range(3) for d in range(3))
            res["grilles kept"] = len(grilles)
        res[cn] = n
        ok &= n > 0 and not any(_touch(b, k) for b in boxes for k in keep)
    return {"ok": ok, **res}


def _touch(a, b):
    (alo, ahi), (blo, bhi) = a, b
    return all(alo[k] < bhi[k] and blo[k] < ahi[k] for k in range(3))


def upgrade_checks():
    res = {"shutters": shutter_view(), "flares": flares(), "pods": pods(), "steps": steps_open(),
           "shocks": shocks_clear(), "triggers": triggers()}
    return {"ok": all(v["ok"] for v in res.values()), **res}
