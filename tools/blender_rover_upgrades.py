"""The original rover's upgrade parts (spec docs/superpowers/specs/2026-10-07-original-rover-upgrades-design.md):
storm armour = storm shutters (gunmetal bar grilles over the cab windows, roof edge caps, a tail plate), wind fairings =
aero skirts between the front and middle wheels, rounded fairings on the front bumper's corners and fender flares
fixed over the wheel arches, thrusters = four pods on the roof rack's corners. Our own geometry in the rover's palette roles; exported by
blender_build_rover into rover.json "upgrades" and checked by blender_rover_upgrade_checks."""
import math

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

import blender_rover_model as m
import blender_trailer_model as g

ARMOUR, FAIRINGS, THRUSTERS = "GEO_RvUpgArmour", "GEO_RvUpgFairing", "GEO_RvUpgThruster"
FITTED = (ARMOUR, FAIRINGS, THRUSTERS)          # every part is fixed to the body (the fender flares too: the user, check-in 1)

RING = (0.04, 0.062)                            # a grille's ring: from 4 to 6.2 cm outside the window opening (its black
                                                # frame reaches 3 cm out)
GRILLE = dict(pitch=0.16, bar_w=0.02, ring_w=(0.025, 0.045), bar_off=(0.030, 0.040))   # bars every 16 cm, 6.5 cm off the glass
SKIRT = dict(z=(-0.66, 0.93), x=(1.045, 1.070), h=(0.95, m.HULL_BOT + g.EMB), edge=0.975, chamfer=0.10)
CORNER_FAIRING = dict(centre=(0.825, 3.37), r=0.20, h=(1.00, 1.16))
FLARE = dict(x_out=1.86, w=0.10, w_pair_top=0.04, end_h=1.50, edge=0.015, gap=0.003, bury=0.02)   # the fender flares
POD = dict(x=0.88, z=(0.50, -4.02), top=0.21)    # top = the nozzle exit over the pod's origin (CargoRover.PodNozzleTop)
TAIL_PLATE = dict(x=(-1.12, 0.45), h=(1.36, 2.04), t=0.02)
ROOF_CAP = dict(cab_x=0.70, cab_back=0.10, cab_lip=0.05, mod_x0=0.96, mod_z=(-3.62, 0.86), t=0.025)


def pod_positions():
    """The four pods' origins (Unity): on the roof rack's side rails (|x| 0.88, the rails' centre height), at its
    front and rear corners. Pod 0 is the one modelled."""
    rh = m.MODULE_ROOF + 0.26
    return [(sx * POD["x"], rh, z) for z in POD["z"] for sx in (1, -1)]


def keep_out():
    """Rover-space boxes (lo, hi), Unity (x, h, z), that no removal collider may touch: each bay door leaf (closed),
    each slot-panel recess (its opening, out to 10 cm past the side), and each cab door's entry zone (the seats'
    click boxes reach |x| 1.10-1.415 there)."""
    deps = bpy.context.evaluated_depsgraph_get()
    boxes = []
    doors = bpy.data.collections.get("GEO_RoverDoors")
    for o in (doors.objects if doors else []):
        if o.type == "MESH" and o.parent is None and "hinge" in o:
            vs = [o.matrix_world @ v.co for v in o.evaluated_get(deps).data.vertices]
            boxes.append(((min(v.x for v in vs), min(v.z for v in vs), min(v.y for v in vs)),
                          (max(v.x for v in vs), max(v.z for v in vs), max(v.y for v in vs))))
    for s in (1, -1):
        xa, xb = sorted((s * m.SLOT_BACK_X, s * (m.UPPER_X + 0.10)))
        boxes.append(((xa, m.SLOT_H[0], m.SLOT_Z[0]), (xb, m.SLOT_H[1], m.SLOT_Z[1])))
        xa, xb = sorted((s * 1.00, s * 1.60))
        boxes.append(((xa, 1.40, 0.90), (xb, 2.95, 1.75)))
        xa, xb = sorted((s * 1.00, s * 2.20))                     # the bays' approach (the final review I2): a player beside
        boxes.append(((xa, 1.40, m.BAYS[1][2] - 0.05), (xb, 2.00, m.BAYS[0][1] + 0.05)))   # the rover aims under the doors' foot
    boxes.append(((-0.70, m.CAB_LOW + 0.06, m.CAB_Z0 + 0.07), (0.70, 2.20, 2.70)))   # the dash and its tap targets (the
    # cab room's own bounding box would reach out past the sloped windscreen: at h 2.20 the slope is at z 2.81)
    return boxes


def unity_euler(eu, ev, ew):
    """Unity localEulerAngles (deg) of the rotation whose local x, y, z axes are eu, ev, ew (Unity vectors, right-handed:
    eu x ev = ew). Unity composes Ry(y) Rx(x) Rz(z) of standard matrices in its (x, y, z)."""
    R = [[eu[0], ev[0], ew[0]], [eu[1], ev[1], ew[1]], [eu[2], ev[2], ew[2]]]
    a = math.asin(max(-1.0, min(1.0, -R[1][2])))
    b = math.atan2(R[0][2], R[2][2])
    c = math.atan2(R[1][0], R[1][1])
    return [round(math.degrees(v), 3) for v in (a, b, c)]


def euler_matrix(rot):
    """The rotation matrix (rows) of Unity Euler angles (deg): Ry(y) Rx(x) Rz(z), to check unity_euler round trips."""
    a, b, c = (math.radians(v) for v in rot)
    rx = [[1, 0, 0], [0, math.cos(a), -math.sin(a)], [0, math.sin(a), math.cos(a)]]
    ry = [[math.cos(b), 0, math.sin(b)], [0, 1, 0], [-math.sin(b), 0, math.cos(b)]]
    rz = [[math.cos(c), -math.sin(c), 0], [math.sin(c), math.cos(c), 0], [0, 0, 1]]
    mul = lambda p, q: [[sum(p[i][k] * q[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    return mul(ry, mul(rx, rz))


def grille_boxes():
    """One oriented removal collider per window grille, flat over its pane (an axis-aligned box round a slanted grille
    would reach into the cab): [(centre (Unity), size, rot (Unity Euler deg), axes (eu, ev, ew))]."""
    out = []
    for Mp, poly, rect in m.cab_panes():
        ccw = poly if m._area(poly) > 0 else list(reversed(poly))
        outer = m._offset_poly(ccw, RING[1])
        us, vs = [q[0] for q in outer], [q[1] for q in outer]
        uc, vc, wc = (min(us) + max(us)) / 2, (min(vs) + max(vs)) / 2, sum(GRILLE["ring_w"]) / 2
        o = Mp(0, 0, 0)
        axes = [Mp(*e) - o for e in ((1, 0, 0), (0, 1, 0), (0, 0, 1))]
        eu, ev, ew = ((a.x, a.z, a.y) for a in axes)                  # Blender -> Unity (x, h, z)
        if Vector(eu).cross(Vector(ev)).dot(Vector(ew)) < 0:          # a mirrored (left side) frame: flip its u axis
            eu = tuple(-v for v in eu)
        c = Mp(uc, vc, wc)
        size = (max(us) - min(us), max(vs) - min(vs), GRILLE["ring_w"][1] - GRILLE["ring_w"][0] + 0.02)
        out.append(((c.x, c.z, c.y), size, unity_euler(eu, ev, ew), (eu, ev, ew)))
    return out


def obb_touches(centre, size, axes, box, n=6):
    """Whether an oriented box (centre, size, axes) reaches into an axis-aligned box (lo, hi): sampled on an n x n x 3
    grid through its volume."""
    (lo, hi) = box
    for i in range(n + 1):
        for j in range(n + 1):
            for k in range(3):
                f = (i / n - 0.5, j / n - 0.5, k / 2 - 0.5)
                p = [centre[d] + sum(axes[a][d] * f[a] * size[a] for a in range(3)) for d in range(3)]
                if all(lo[d] < p[d] < hi[d] for d in range(3)):
                    return True
    return False


def part_boxes(ob, keep):
    """A part's removal collider boxes, Unity rover space (lo, hi): its mesh islands of 5 cm or more
    (blender_build_trailer._island_boxes), less any box that touches a keep-out box."""
    import blender_build_trailer as bt
    out = []
    for b in bt._island_boxes(ob):
        c, s = b["center"], b["size"]
        lo = tuple(ob.matrix_world.translation[[0, 2, 1][k]] + c[k] - s[k] / 2 for k in range(3))
        hi = tuple(ob.matrix_world.translation[[0, 2, 1][k]] + c[k] + s[k] / 2 for k in range(3))
        if not any(all(lo[k] < khi[k] and klo[k] < hi[k] for k in range(3)) for klo, khi in keep):
            out.append((lo, hi))
    return out


def boxes_json(objs, pivot, keep):
    """Removal colliders for one exported mesh: the parts' boxes as rover.json "colliders" (centre and size relative to
    the mesh's pivot, Unity)."""
    out = []
    for o in objs:
        for lo, hi in part_boxes(o, keep):
            out.append({"center": [round((lo[k] + hi[k]) / 2 - pivot[k], 4) for k in range(3)],
                        "size": [round(max(hi[k] - lo[k], 0.02), 4) for k in range(3)]})
    return out


def _vrange(poly, u):
    """(lowest, highest) v where the vertical line at u crosses the closed outline poly [(u, v)], or None."""
    vs = []
    for i in range(len(poly)):
        (u0, v0), (u1, v1) = poly[i], poly[(i + 1) % len(poly)]
        if u0 != u1 and (u0 - u) * (u1 - u) <= 0:
            vs.append(v0 + (v1 - v0) * (u - u0) / (u1 - u0))
    return (min(vs), max(vs)) if len(vs) >= 2 else None


def _grille(p, ps, M, poly):
    """A bar grille over one window: a ring RING outside the opening's outline, standing GRILLE ring_w off the
    surface on four standoffs (buried in the hull or the door skin), and vertical bars every GRILLE pitch reaching the
    ring's centre line."""
    ccw = poly if m._area(poly) > 0 else list(reversed(poly))
    inner, outer, mid = (m._offset_poly(ccw, d) for d in (RING[0], RING[1], (RING[0] + RING[1]) / 2))
    w0, w1 = GRILLE["ring_w"]
    g.ring_prism(p, outer, inner, w0, w1, M)
    us = [q[0] for q in ccw]
    umin, umax = min(us), max(us)
    n = max(1, round((umax - umin) / GRILLE["pitch"]))
    bw, (b0, b1) = GRILLE["bar_w"] / 2, GRILLE["bar_off"]
    for k in range(n):
        u = umin + (umax - umin) * (k + 0.5) / n
        vr = _vrange(mid, u)
        if vr:
            g.wbox(p, M, u - bw, u + bw, vr[0], vr[1], b0, b1)
    mus = [q[0] for q in mid]
    for u in (min(mus) + 0.03, max(mus) - 0.03):
        for v in (_vrange(mid, u) or ()):
            g.wbox(ps, M, u - 0.015, u + 0.015, v - 0.015, v + 0.015, -g.EMB, w0 + g.EMB)


def _cap_profile(corner, along_a, along_b, back, lip, t):
    """An L cap's section over a convex edge: `corner` (2D), the surfaces leaving it along unit vectors along_a (back
    over the first face, `back` long) and along_b (down the second, `lip` long), t thick, its inner side buried EMB."""
    c, a, b = Vector(corner), Vector(along_a), Vector(along_b)
    na, nb = Vector((-a.y, a.x)), Vector((b.y, -b.x))              # outward normals of the two faces
    if na.dot(b) > 0:
        na = -na
    if nb.dot(a) > 0:
        nb = -nb
    # the outer corner: where the two faces offset by t meet
    p, q = c + na * t, c + nb * t
    den = a.x * b.y - a.y * b.x
    s = ((q - p).x * b.y - (q - p).y * b.x) / den if abs(den) > 1e-9 else 0.0
    oc = p + a * s
    return [tuple(c + a * back - na * g.EMB), tuple(c + a * back + na * t), tuple(oc), tuple(c + b * lip + nb * t),
            tuple(c + b * lip - nb * g.EMB), tuple(c - (na + nb).normalized() * 0.01)]


def build_shutters(col, out):
    """Storm armour: a bar grille over every cab window, steel caps on the cab roof's front edge (with a lip down the
    windscreen slope) and along the storage module's roof edges, a plate on the tail left of the ladder."""
    pg, ps = g.Part("RVU_Grilles", "gunmetal"), g.Part("RVU_GrilleStandoffs", "gunmetal")
    for Mp, poly, rect in m.cab_panes():
        _grille(pg, ps, Mp, poly)
    out.append(m.finish(pg, col, bevel=0.003))
    out.append(m.finish(ps, col))
    pc = g.Part("RVU_RoofCaps", "gunmetal")
    (zt, ht), (zb, hb) = m.WINDSCREEN
    d = Vector((zb - zt, hb - ht)).normalized()                      # down the windscreen slope (z, h)
    prof = _cap_profile((zt, ht), (-1.0, 0.0), tuple(d), ROOF_CAP["cab_back"], ROOF_CAP["cab_lip"], ROOF_CAP["t"])
    g.prism(pc, prof, -ROOF_CAP["cab_x"], ROOF_CAP["cab_x"], lambda z, h, x: m.U(x, h, z))
    edge = m.lean_x(m.MODULE_ROOF, m.MOD_LEAN) - 0.008                # flat on the roof, 8 mm inboard of its edge
    z0, z1 = ROOF_CAP["mod_z"]
    for s in (1, -1):
        xa, xb = sorted((s * ROOF_CAP["mod_x0"], s * edge))
        g.box(pc, xa, xb, m.MODULE_ROOF - g.EMB, m.MODULE_ROOF + ROOF_CAP["t"], z0, z1, space=m.U)
    out.append(m.finish(pc, col, bevel=0.006))
    pt, pb = g.Part("RVU_TailPlate", "gunmetal"), g.Part("RVU_TailPlateBolts", "charcoal")
    R = m.rear_face(m.REAR_Z)
    (x0, x1), (h0, h1), t = TAIL_PLATE["x"], TAIL_PLATE["h"], TAIL_PLATE["t"]
    g.wbox(pt, R, x0, x1, h0, h1, -g.EMB, t)
    for u in (x0 + 0.05, (x0 + x1) / 2, x1 - 0.05):
        for v in (h0 + 0.05, h1 - 0.05):
            g.bolt(pb, R, u, v, w=t, r=0.012, h=0.008)
    out.append(m.finish(pt, col, bevel=0.006))
    out.append(m.finish(pb, col))


def build_fairings(col, out):
    """Wind fairings: smooth skirts closing the gap under the body between the front and middle wheels (ending 4 cm
    before each side step), and rounded fairings on the front bumper's two chamfered corners (beside the red markers);
    silver with an orange lower or top edge."""
    ps = g.Part("RVU_Skirts", "silver")
    (z0, z1), (x0, x1), (h0, h1), c = SKIRT["z"], SKIRT["x"], SKIRT["h"], SKIRT["chamfer"]
    for s in (1, -1):
        xa, xb = sorted((s * x0, s * x1))
        g.prism(ps, [(z0, h1), (z1, h1), (z1 - c, h0), (z0 + c, h0)], xa, xb, lambda z, h, x: m.U(x, h, z))
    bm = ps.bm
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=m.U(0, SKIRT["edge"], 0),
                           plane_no=Vector((0, 0, 1)))
    g.set_roles(ps, ("silver", "orange"), lambda f: "orange" if f.calc_center_median().z < SKIRT["edge"] else "silver")
    out.append(m.finish(ps, col, bevel=0.01))
    pf = g.Part("RVU_NoseFairings", "silver", smooth=True)
    (cx, cz), r, (fh0, fh1) = CORNER_FAIRING["centre"], CORNER_FAIRING["r"], CORNER_FAIRING["h"]
    prof = [(cx + 0.155, cz - 0.01), (cx + r, cz - 0.01)]
    prof += [(cx + r * math.cos(math.radians(a)), cz + r * math.sin(math.radians(a))) for a in range(0, 91, 10)]
    prof += [(cx - 0.025, cz + r), (cx - 0.025, cz + 0.13)]
    for s in (1, -1):
        g.prism(pf, [(s * x, z) for x, z in prof], fh0, fh1, lambda x, z, h: m.U(x, h, z))
    bm = pf.bm
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    g.set_roles(pf, ("silver", "orange"), lambda f: "orange" if f.calc_center_median().z > fh1 - 0.005 else "silver")
    out.append(m.finish(pf, col, bevel=0.01))


def _hull_bvh():
    deps = bpy.context.evaluated_depsgraph_get()
    vs, tris = [], []
    for o in m.hull_parts():
        e = o.evaluated_get(deps)
        me = e.to_mesh()
        me.calc_loop_triangles()
        base = len(vs)
        vs += [o.matrix_world @ v.co for v in me.vertices]
        tris += [tuple(base + i for i in t.vertices) for t in me.loop_triangles]
        e.to_mesh_clear()
    return BVHTree.FromPolygons(vs, tris)


def _flare_outline(zf, zr):
    """An arch's upper outline (z, h), front to rear: from FLARE end_h up its front shoulder, over the top, down its rear
    shoulder (blender_rover_model.arch_profile's middle points, the shoulders extended down toward the skirt line)."""
    prof = [Vector(p) for p in m.arch_profile(zf, zr)]
    t = (prof[2].y - FLARE["end_h"]) / (prof[2].y - prof[1].y)
    return [prof[2] + (prof[1] - prof[2]) * t, prof[2], prof[3], prof[4], prof[5], prof[5] + (prof[6] - prof[5]) * t]


def build_flares(col, out):
    """Fender flares fixed to the hull (the user, check-in 1: the riding fenders were tiny): over each wheel arch (the
    front wheel's, the middle and rear pair's) a band following the arch's upper outline, FLARE w wide, standing out from
    the hull (its inner edge FLARE bury inside the surface, found by a ray) to |x| FLARE x_out, silver with an orange
    outer lip. Over the pair the band's top is only w_pair_top over the arch: the bay doors' bottom edge (h 1.82) is
    just above it. The wheels move and steer inside the arch under them (steer_sweep checks it with the parts)."""
    hull = _hull_bvh()
    F = FLARE

    def hull_x(h, z):
        hit = hull.ray_cast(m.U(2.5, h, z), Vector((-1, 0, 0)), 2.5)
        return (hit[0].x if hit[0] is not None else 1.0) - F["bury"]

    p = g.Part("RVU_Flares", "silver")
    bm = p.bm
    for k, (zf, zr) in enumerate(m.arches()):
        pts = _flare_outline(zf, zr)
        segs = [((pts[i + 1] - pts[i]).normalized()) for i in range(len(pts) - 1)]
        seg_n = [Vector((d.y, -d.x)) for d in segs]                     # away from the opening (front to rear: up / out)
        widths = [F["w"]] * len(pts)
        if k == 1:
            widths[2] = widths[3] = F["w_pair_top"]
        rings = []
        for i, P in enumerate(pts):
            if i == 0:
                n = seg_n[0]
            elif i == len(pts) - 1:
                n = seg_n[-1]
            else:
                a, b = seg_n[i - 1], seg_n[i]
                n = (a + b) / (1.0 + a.dot(b))                           # mitred: the band keeps its width
            lo, hi = P + n * F["gap"], P + n * widths[i]
            rings.append([(hull_x(lo.y, lo.x), lo.y, lo.x), (F["x_out"], lo.y, lo.x), (F["x_out"], hi.y, hi.x), (hull_x(hi.y, hi.x), hi.y, hi.x)])
        for s in (1, -1):
            vr = [[bm.verts.new(m.U(s * x, h, z)) for x, h, z in r] for r in rings]
            for r0, r1 in zip(vr, vr[1:]):
                for j in range(4):
                    bm.faces.new((r0[j], r0[(j + 1) % 4], r1[(j + 1) % 4], r1[j]))
            bm.faces.new(vr[0])
            bm.faces.new(list(reversed(vr[-1])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    for s in (1, -1):
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=m.U(s * (F["x_out"] - F["edge"]), 1.5, 0),
                               plane_no=Vector((1, 0, 0)))
    g.set_roles(p, ("silver", "orange"), lambda f: "orange" if abs(f.calc_center_median().x) > F["x_out"] - F["edge"] else "silver")
    out.append(m.finish(p, col, bevel=0.008))


def build_pods(col, out):
    """Thrusters: a pod clamped round the roof rack's side rail (modelled at pod 0; the exporter places the same mesh
    at each corner): a charcoal body, a dark bell, an orange nozzle ring, its exit POD top over the origin."""
    x, h, z = pod_positions()[0]
    up = Vector((0, 0, 1))
    pb, pn = g.Part("RVU_PodBody", "charcoal"), g.Part("RVU_PodBell", "dark")
    po, pe = g.Part("RVU_PodRing", "orange"), g.Part("RVU_PodExit", "dark")
    g.box(pb, x - 0.045, x + 0.045, h - 0.045, h + 0.045, z - 0.07, z + 0.07, space=m.U)     # the clamp round the rail
    g.cylinder(pb, m.U(x, h + 0.045 - g.EMB, z), up, 0.065, 0.10 + g.EMB, seg=16, smooth=False)
    g.cylinder(pn, m.U(x, h + 0.145 - g.EMB, z), up, 0.045, 0.04 + 2 * g.EMB, seg=16, smooth=False)
    g.cylinder(po, m.U(x, h + 0.185, z), up, 0.055, POD["top"] - 0.185, seg=16, smooth=False)
    g.cylinder(pe, m.U(x, h + POD["top"] - 0.003, z), up, 0.040, 0.004, seg=16, smooth=False)
    for p, bev in ((pb, 0.006), (pn, 0.0), (po, 0.003), (pe, 0.0)):
        out.append(m.finish(p, col, bevel=bev))


def build():
    """Every upgrade part, in its collection (each rebuilt from scratch)."""
    out = {}
    for name in (ARMOUR, FAIRINGS, THRUSTERS):
        out[name] = []
        g.collection(name)
    build_shutters(bpy.data.collections[ARMOUR], out[ARMOUR])
    build_fairings(bpy.data.collections[FAIRINGS], out[FAIRINGS])
    build_flares(bpy.data.collections[FAIRINGS], out[FAIRINGS])
    build_pods(bpy.data.collections[THRUSTERS], out[THRUSTERS])
    return {k: [o.name for o in v] for k, v in out.items()}
