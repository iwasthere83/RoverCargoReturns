"""Checks of the frames' build stages (spec 2026-10-08, plan 2026-10-08-original-rover-build-stages): every part in one
stage, states cumulative (scaffold parts only in their range), the chassis on its wheels and trestles, nothing floating,
no coplanar faces, a triangle budget per state mesh, the footprint covering the finished vehicle."""
import bpy

from mathutils import Vector
from mathutils.bvhtree import BVHTree

import blender_build_stages as bs


def tags(variant):
    """Every exported part of the vehicle carries one int "stage" in 0 .. last; a scaffold part also an int "until" in
    stage .. last - 1; every name in bs.STAGES[variant] exists."""
    bad = []
    n = bs.STATES[variant]
    names = set()
    for o in bs.body_parts(variant):
        names.add(o.name)
        st = o.get("stage")
        if not isinstance(st, int) or not 0 <= st < n:
            bad.append(f"{o.name}: stage {st!r}")
        un = o.get("until")
        if un is not None and (not isinstance(un, int) or not st <= un < n - 1):
            bad.append(f"{o.name}: until {un!r}")
    bad += [f"{k}: in STAGES, not in the model" for k in bs.STAGES[variant] if k not in names]
    return {"ok": not bad, "bad": bad}


def _evaluated(o):
    deps = bpy.context.evaluated_depsgraph_get()
    e = o.evaluated_get(deps)
    me = e.to_mesh()
    vs = [o.matrix_world @ v.co for v in me.vertices]
    ps = [tuple(p.vertices) for p in me.polygons]
    e.to_mesh_clear()
    return vs, ps


def grounded(variant):
    """The chassis state stands: each trestle's lowest point at h 0 (1 mm), the top of each trestle within 5 mm under a
    chassis part (a ray up from 2 mm above its top centre), each tyre copy's (tread's) lowest point at or under h 0.002 (the
    tyres sit as on the finished vehicle, pressed in a little)."""
    bad = []
    parts = [o for o in bs.body_parts(variant) if o["stage"] == 0]
    trest = [o for o in parts if o.name.startswith("STG_Trestle")]
    if len(trest) < 2:
        bad.append(f"{len(trest)} trestles")
    chassis = [o for o in parts if not o.name.startswith(("STG_Trestle", "STG_Tyre", "STG_Arm", "STG_Shock"))]
    vs, ps = [], []
    for o in chassis:
        v, p = _evaluated(o)
        ps += [tuple(i + len(vs) for i in f) for f in p]
        vs += v
    bvh = BVHTree.FromPolygons(vs, ps)
    for t in trest:
        v, _ = _evaluated(t)
        low, top = min(p.z for p in v), max(p.z for p in v)
        if abs(low) > 0.001:
            bad.append(f"{t.name}: feet at {low:.4f}")
        c = sum(v, Vector()) / len(v)
        hit, _, _, d = bvh.ray_cast(Vector((c.x, c.y, top - 0.002)), Vector((0, 0, 1)), 1.0)   # from just inside its top
        if hit is None or d - 0.002 > 0.005:
            bad.append(f"{t.name}: top {d - 0.002 if hit else 'no chassis above'}")
    for o in parts:
        if o.name.startswith(("STG_TyreL_", "STG_TyreR_")):                 # the tread (the rims sit inside it)
            low = min(p.z for p in _evaluated(o)[0])
            if low > 0.002:
                bad.append(f"{o.name}: lowest {low:.4f}")
    return {"ok": not bad, "bad": bad}


def cumulative(variant):
    """state k = every part with stage <= k, less scaffold parts whose until < k; nothing but scaffold leaves; the
    states before the last are not empty and each adds a part."""
    bad = []
    prev = set()
    for k in range(bs.STATES[variant] - 1):
        cur = {o.name for o in bs.state_parts(variant, k)}
        if not cur - prev:
            bad.append(f"state {k} adds nothing")
        gone = [n for n in prev - cur if "until" not in bpy.data.objects[n]]
        bad += [f"state {k}: {n} disappears" for n in gone]
        prev = cur
    return {"ok": not bad, "bad": bad}


def _samples(geo):
    """A part's vertices and face centres: two crossing faces touch with no corner on each other."""
    verts, polys = geo
    pts = verts + [sum((verts[i] for i in f), Vector()) / len(f) for f in polys]
    return pts[::max(1, len(pts) // 800)]


def _touch(pts, bvh, tol):
    return any(bvh.find_nearest(p, tol)[0] is not None for p in pts)


def connected(variant, tol=0.005):
    """No floating parts (tol 5 mm: the model's own mounts keep 4 mm, e.g. the rims in the tyres, the jack brackets on
    the drawbars, the tail pods on the hull): in every state before the last, each part is joined (a vertex within tol of the other's
    surface, or one of the other's) through other parts of that state to one that touches the ground (lowest point under h 0.01)."""
    bad = []
    for k in range(bs.STATES[variant] - 1):
        parts = bs.state_parts(variant, k)
        geo = {o.name: _evaluated(o) for o in parts}
        bvh = {n: BVHTree.FromPolygons(v, p) for n, (v, p) in geo.items()}
        pts = {n: _samples(g) for n, g in geo.items()}
        reach = [n for n, (v, _) in geo.items() if min(p.z for p in v) < 0.01]
        seen = set(reach)
        while reach:
            a = reach.pop()
            for b in geo:
                if b in seen:
                    continue
                # either way round: a narrow face can rest inside a wide one with no corner on it
                if _touch(pts[a], bvh[b], tol) or _touch(pts[b], bvh[a], tol):
                    seen.add(b)
                    reach.append(b)
        bad += [f"state {k}: {n} floats" for n in geo if n not in seen]
    return {"ok": not bad, "bad": bad}


def coplanar_states(variant):
    import blender_rover_checks as brc
    bad = []
    for k in range(bs.STATES[variant] - 1):
        bad += [f"state {k}: {a} / {b} at {pt}" for a, b, pt in brc.coplanar(objects=bs.state_parts(variant, k))]
    return {"ok": not bad, "bad": bad}


def budget(variant):
    """Each baked state mesh's triangles within bs.BUDGET (the exporter's meshStats)."""
    bad = [f"{name}: {tris} > {cap}" for name, tris, cap in bs.baked_triangles(variant) if tris > cap]
    return {"ok": not bad and bool(bs.baked_triangles(variant)), "bad": bad or ([] if bs.baked_triangles(variant) else ["nothing baked"])}


def footprint(variant):
    """The frame's footprint (the union of its state meshes' boxes, as the builder's Footprint renderer spans it) covers
    the finished vehicle in x and z: the body, the wheels, the doors (1 cm)."""
    lo, hi = bs.frame_box(variant)
    vlo, vhi = bs.vehicle_box(variant)
    bad = [f"{ax}: frame {lo[i]:.3f}..{hi[i]:.3f}, vehicle {vlo[i]:.3f}..{vhi[i]:.3f}"
           for i, ax in ((0, "x"), (2, "z")) if lo[i] > vlo[i] + 0.01 or hi[i] < vhi[i] - 0.01]
    return {"ok": not bad, "bad": bad}


def stage_checks(variant):
    bs.prepare(variant)
    bs.bake(variant)
    res = {"tags": tags(variant), "grounded": grounded(variant)}
    res.update(cumulative=cumulative(variant), connected=connected(variant), coplanar=coplanar_states(variant))
    res.update(budget=budget(variant), footprint=footprint(variant))
    res["ok"] = all(v["ok"] for v in res.values())
    return res
