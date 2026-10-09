"""Generate the Cargo Trailer and the Habitat trailer as Blender geometry. Run inside Blender.

Everything is built from numbers here (re-run to rebuild):
  GEO_TrailerCargo  TRL_* parts, trailer-local, placed at Blender Y = TRAILER_Y
  GEO_TrailerHab    HAB_* parts, placed at Blender Y = HAB_ORIGIN_Y
Coordinates below are written in Unity axes (x right, h up, z forward) and converted with T().

Design rules: the frame outline is fixed (spine walls at |x| = 0.53 back the suspension arm hinges, flat deck
top at 1.32 m, deck edge inside the tyres, hitch ball centre 0.8 m up). Parts meet by burying joints a centimetre
inside each other (a weld), never with coplanar visible faces; box parts are flat shaded with chamfered edges like
the rover, round parts smooth shaded.
"""
import math

import bmesh
import bpy
from mathutils import Matrix, Vector

TRAILER_Y = -6.6            # Blender Y of the trailer origin: hitched to the rover reference (ring on the pin)
DECK_TOP = 1.32
BAY_Z = [round((2.5 - i) * 0.85, 4) for i in range(6)]
ROLE_COLOUR = {"white": (0.92, 0.92, 0.9, 1), "red": (0.75, 0.12, 0.08, 1), "gray": (0.45, 0.47, 0.5, 1),
               "black": (0.12, 0.12, 0.13, 1), "glass": (0.15, 0.19, 0.24, 1), "amber": (1.0, 0.74, 0.11, 1),
               "lamp": (0.91, 0.02, 0.0, 1), "dark": (0.22, 0.23, 0.22, 1), "cell": (0.13, 0.37, 0.55, 1),
               # the original rover's (blender_rover_model.ROLE_COLOUR): the trailers wear its colours
               "silver": (0.906, 0.906, 0.906, 1), "steel": (0.76, 0.78, 0.78, 1), "charcoal": (0.24, 0.235, 0.24, 1), "gunmetal": (0.275, 0.275, 0.275, 1),
               "orange": (0.78, 0.25, 0.0, 1)}


_origin_y = TRAILER_Y          # Blender Y of the origin of the trailer being built (set per variant)


def set_origin(y):
    global _origin_y
    _origin_y = y


def T(x, h, z):
    return Vector((x, _origin_y + z, h))



# ---------------------------------------------------------------- mesh helpers
class Part:
    def __init__(self, name, role, smooth=False):
        self.name, self.role, self.smooth = name, role, smooth
        self.bm = bmesh.new()
        self.roles = None          # per-face roles (set_roles): material slots in this order


def set_roles(p, roles, role_of):
    """Give a part more than one palette role: role_of(face) names each face's role (one of roles). finish() turns them
    into material slots carrying the role; the exporter (blender_build_trailer._combine) reads a face's role from its
    material, and a boolean cutter with roles hands its role to the faces it cuts (material_mode TRANSFER)."""
    p.roles = list(roles)
    for f in p.bm.faces:
        f.material_index = p.roles.index(role_of(f))


def role_material(role):
    mat = bpy.data.materials.get("ROLE_" + role) or bpy.data.materials.new("ROLE_" + role)
    mat["role"] = role
    mat.diffuse_color = ROLE_COLOUR[role]
    return mat


def cut_with(ob, cutter, label):
    """A boolean difference whose new faces take the cutter's roles."""
    mod = ob.modifiers.new(label, "BOOLEAN")
    mod.operation, mod.object, mod.solver = "DIFFERENCE", cutter, "EXACT"
    mod.material_mode = "TRANSFER"
    return mod


def prism(p, profile, axis_from, axis_to, to_world):
    """Extrude a closed 2D profile [(u, v), ...] between two positions along one axis.
    to_world(u, v, w) -> Blender Vector. Returns the new faces."""
    bm = p.bm
    a = [bm.verts.new(to_world(u, v, axis_from)) for u, v in profile]
    b = [bm.verts.new(to_world(u, v, axis_to)) for u, v in profile]
    n = len(profile)
    faces = [bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i])) for i in range(n)]
    faces.append(bm.faces.new(list(reversed(a))))
    faces.append(bm.faces.new(b))
    return faces


def box(p, x0, x1, h0, h1, z0, z1, space=T):
    return prism(p, [(x0, h0), (x1, h0), (x1, h1), (x0, h1)], z0, z1, lambda u, v, w: space(u, v, w))


def tube(p, pts, r, seg=12, closed=False):
    """Round tube along a polyline of Blender-space points, mitred at the corners."""
    bm = p.bm
    n = len(pts)
    rings = []
    for i in range(n):
        if closed:
            t_in = (pts[i] - pts[i - 1]).normalized()
            t_out = (pts[(i + 1) % n] - pts[i]).normalized()
        else:
            t_in = (pts[i] - pts[i - 1]).normalized() if i > 0 else (pts[1] - pts[0]).normalized()
            t_out = (pts[i + 1] - pts[i]).normalized() if i < n - 1 else t_in
        tan = (t_in + t_out).normalized()
        if i == 0:
            ref = Vector((0, 0, 1)) if abs(tan.z) < 0.9 else Vector((1, 0, 0))
            u = tan.cross(ref).normalized()
        else:  # parallel transport: no twist between rings
            u = (u - tan * u.dot(tan)).normalized()
        v = tan.cross(u).normalized()
        ring = []
        for k in range(seg):
            a = 2 * math.pi * k / seg
            d = u * math.cos(a) + v * math.sin(a)
            # mitre: stretch in the bisector plane so the point stays r from the incoming tube axis
            ring.append(bm.verts.new(pts[i] + d * (r / max(0.3, math.sqrt(max(0.0, 1 - d.dot(t_in) ** 2))))))
        rings.append(ring)
    last = n if closed else n - 1
    for i in range(last):
        r0, r1 = rings[i], rings[(i + 1) % n]
        for k in range(seg):
            f = bm.faces.new((r0[k], r0[(k + 1) % seg], r1[(k + 1) % seg], r1[k]))
            f.smooth = True
    if not closed:
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[-1])


def rounded_rect_loop(x, z, rad, h, space, steps=4):
    """Closed loop (rectangle |x|, |z| with corner radius) at height h."""
    pts = []
    for cx, cz, a0 in ((x - rad, z - rad, 0), (-(x - rad), z - rad, 90), (-(x - rad), -(z - rad), 180), (x - rad, -(z - rad), 270)):
        for s in range(steps + 1):
            a = math.radians(a0 + 90 * s / steps)
            pts.append(space(cx + rad * math.cos(a), h, cz + rad * math.sin(a)))
    return pts


def cylinder(p, centre, axis, r, length, seg=16, smooth=True):
    bm = p.bm
    axis = axis.normalized()
    ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((1, 0, 0))
    u = axis.cross(ref).normalized()
    v = axis.cross(u).normalized()
    a = [bm.verts.new(centre + (u * math.cos(2 * math.pi * k / seg) + v * math.sin(2 * math.pi * k / seg)) * r) for k in range(seg)]
    b = [bm.verts.new(x.co + axis * length) for x in a]
    for k in range(seg):
        f = bm.faces.new((a[k], a[(k + 1) % seg], b[(k + 1) % seg], b[k]))
        f.smooth = smooth
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)


def sphere(p, centre, r, cut_below=None, seg=16, rings=10):
    """UV sphere; optionally cut flat (and capped) below Blender height cut_below."""
    bm = p.bm
    geom = bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=rings, radius=r, matrix=Matrix.Translation(centre))
    verts = geom["verts"]
    faces = list({f for v in verts for f in v.link_faces})
    for f in faces:
        f.smooth = True
    if cut_below is not None:
        res = bmesh.ops.bisect_plane(bm, geom=verts + faces + list({e for f in faces for e in f.edges}),
                                     plane_co=Vector((0, 0, cut_below)), plane_no=Vector((0, 0, 1)), clear_inner=True)
        cut = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
        if cut:
            bmesh.ops.edgeloop_fill(bm, edges=cut)


def torus(p, centre, axis, major, minor, seg=16, seg2=8):
    bm = p.bm
    axis = axis.normalized()
    ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((1, 0, 0))
    u = axis.cross(ref).normalized()
    v = axis.cross(u).normalized()
    rings = []
    for i in range(seg):
        a = 2 * math.pi * i / seg
        c = centre + (u * math.cos(a) + v * math.sin(a)) * major
        radial = (c - centre).normalized()
        rings.append([bm.verts.new(c + (radial * math.cos(2 * math.pi * k / seg2) + axis * math.sin(2 * math.pi * k / seg2)) * minor) for k in range(seg2)])
    for i in range(seg):
        r0, r1 = rings[i], rings[(i + 1) % seg]
        for k in range(seg2):
            f = bm.faces.new((r0[k], r1[k], r1[(k + 1) % seg2], r0[(k + 1) % seg2]))
            f.smooth = True


def finish(p, collection, bevel=0.0, bevel_segments=2):
    bm = p.bm
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    old = bpy.data.objects.get(p.name)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    me = bpy.data.meshes.new(p.name)
    bm.to_mesh(me)
    bm.free()
    for r in p.roles or ():
        me.materials.append(role_material(r))
    ob = bpy.data.objects.new(p.name, me)
    collection.objects.link(ob)
    ob["role"] = p.role
    ob.color = ROLE_COLOUR[p.role]
    if bevel > 0:
        m = ob.modifiers.new("Bevel", "BEVEL")
        m.width = bevel
        m.segments = bevel_segments
        m.limit_method = "ANGLE"
        m.angle_limit = math.radians(40)
        m.harden_normals = False
    return ob


def collection(name):
    col = bpy.data.collections.get(name)
    if not col:
        col = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(col)
    for ob in list(col.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    return col


# ---------------------------------------------------------------- trailer
# Hull cross-sections (x, h), right half from the deck top edge down to the keel; mirrored for the left.
# Index-matched so stations loft into one faceted hull. BAND = over an arm's swing zone (underside stays at the deck
# line 1.20, arms hinge on the keel walls |x| 0.53); POD = between the wheels (deep side pod, faceted chine);
# CHIN = the front, keel raised so the drawbar leaves a sloped chin.
HALF = {
    "BAND":     [(1.08, 1.32), (1.08, 1.21), (1.01, 1.18), (0.53, 1.20), (0.53, 0.62), (0.43, 0.56)],
    "POD":      [(1.08, 1.32), (1.08, 1.00), (0.90, 0.86), (0.53, 0.74), (0.53, 0.62), (0.43, 0.56)],
    "POD_TIP":  [(1.00, 1.32), (1.00, 1.00), (0.84, 0.87), (0.53, 0.75), (0.53, 0.62), (0.43, 0.56)],
    "CHIN":     [(1.08, 1.32), (1.08, 1.21), (1.01, 1.18), (0.53, 1.20), (0.53, 1.08), (0.43, 1.04)],
    "CHIN_TIP": [(1.00, 1.32), (1.00, 1.21), (0.93, 1.18), (0.53, 1.20), (0.53, 1.08), (0.43, 1.04)],
}
# the edge lip (1.18 at |x| >= 1.01) clears the arm hubs up to 0.25 m of wheel rise, like the deck underside
# Arm swing bands (z) keep BAND sections: A [1.38, 2.44], B [-0.32, 0.74], C [-2.02, -0.96].
STATIONS = [
    (2.65, "CHIN_TIP"), (2.57, "CHIN"), (2.45, "BAND"), (1.38, "BAND"), (1.22, "POD"), (0.90, "POD"), (0.76, "BAND"),
    (-0.34, "BAND"), (-0.46, "POD"), (-0.82, "POD"), (-0.96, "BAND"), (-2.02, "BAND"), (-2.20, "POD"), (-2.57, "POD"), (-2.65, "POD_TIP"),
]
POD_FLATS = [(0.90, 1.22), (-0.82, -0.46), (-2.57, -2.20)]


def section(key):
    half = HALF[key]
    return half + [(-x, h) for x, h in reversed(half)]


def loft(p, stations):
    bm = p.bm
    rings = [[bm.verts.new(T(x, h, z)) for x, h in section(key)] for z, key in stations]
    n = len(rings[0])
    for a, b in zip(rings, rings[1:]):
        for k in range(n):
            bm.faces.new((a[k], a[(k + 1) % n], b[(k + 1) % n], b[k]))
    bm.faces.new(rings[0])
    bm.faces.new(list(reversed(rings[-1])))


def _zc(f):
    return f.calc_center_median().y - _origin_y


TRAILER_TWO_TONE = 1.21      # the cargo trailer's hull: silver deck and side band above, gunmetal pods and keel below


def build_frame_body(col, out, hull_role="two-tone", prefix="TRL"):
    """Shared base frame: hull (deck, side pods, keel walls for the arm hinges, chin and tail in one
    welded-looking piece), tail lenses and rear step. hull_role "two-tone": silver above TRAILER_TWO_TONE, gunmetal
    below, like the rover's hull."""
    p = Part(prefix + "_Hull", "silver" if hull_role == "two-tone" else hull_role)
    loft(p, STATIONS)
    bm = p.bm
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    top = [f for f in bm.faces if f.normal.z > 0.99 and abs(f.calc_center_median().z - DECK_TOP) < 1e-4]
    bmesh.ops.dissolve_faces(bm, faces=top)
    pods = [f for f in bm.faces if abs(f.normal.x) > 0.99 and abs(abs(f.calc_center_median().x) - 1.08) < 1e-4
            and any(z0 - 1e-4 <= _zc(f) <= z1 + 1e-4 for z0, z1 in POD_FLATS) and f.calc_center_median().z < 1.25]
    bmesh.ops.inset_individual(bm, faces=pods, thickness=0.035, depth=-0.012)   # recessed pod side panels
    deck = [f for f in bm.faces if f.normal.z > 0.99 and abs(f.calc_center_median().z - DECK_TOP) < 1e-4]
    rim = [e for f in deck for e in f.edges if len(e.link_faces) == 2 and not all(g in deck for g in e.link_faces)]
    bmesh.ops.bevel(bm, geom=rim, offset=0.012, segments=1, affect="EDGES")
    for z in [2.55, 1.7, 0.85, 0.0, -0.85, -1.7, -2.55]:
        g = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=g, plane_co=T(0, 0, z), plane_no=Vector((0, 1, 0)))
    panels = [f for f in bm.faces if f.normal.z > 0.99 and abs(f.calc_center_median().z - DECK_TOP) < 1e-4]
    bmesh.ops.inset_individual(bm, faces=panels, thickness=0.014, depth=-0.005)  # bay panel seams
    if hull_role == "two-tone":
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=T(0, TRAILER_TWO_TONE, 0),
                               plane_no=Vector((0, 0, 1)))
        set_roles(p, ("silver", "gunmetal"), lambda f: "silver" if f.calc_center_median().z > TRAILER_TWO_TONE else "gunmetal")
    hull = finish(p, col)
    out.append(hull)
    _shock_pockets(col, out, hull, prefix)

    p = Part(prefix + "_TailLenses", "red")
    for s in (1, -1):
        box(p, min(s * 0.55, s * 0.85), max(s * 0.55, s * 0.85), 1.04, 1.16, -2.665, -2.645)
    out.append(finish(p, col, bevel=0.006, bevel_segments=1))
    p = Part(prefix + "_RearStep", "gunmetal")
    box(p, -0.62, 0.62, 0.86, 0.90, -2.74, -2.64)
    out.append(finish(p, col, bevel=0.01, bevel_segments=1))


SHOCK_POCKET = dict(x=(0.57, 0.71), h=(1.19, 1.265), dz=0.10)   # |x|, height, half-length about the shock's top


def _shock_pockets(col, out, hull, prefix):
    """The original rover's coil-over shocks (the trailers run its wheels): each one's upper eye sits at the rover's
    1.24 m, above the deck underside (1.20), so a pocket is cut up into the deck over every shock with a mount plate in
    its roof (blender_trailer_checks.running_gear)."""
    import blender_rover_model as rm
    px, ph, dz = SHOCK_POCKET["x"], SHOCK_POCKET["h"], SHOCK_POCKET["dz"]
    old = bpy.data.objects.get(prefix + "_ShockPocketCut")
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    cut, plate = Part(prefix + "_ShockPocketCut", "gunmetal"), Part(prefix + "_ShockMounts", "gunmetal")
    for z in UPG_AXLES_Z:
        for s in (1, -1):
            _, _, tz = rm.shock_top(s, z)
            xa, xb = sorted((s * px[0], s * px[1]))
            box(cut, xa, xb, ph[0], ph[1], tz - dz, tz + dz)
            xa, xb = sorted((s * (px[0] - EMB), s * (px[1] + EMB)))
            box(plate, xa, xb, ph[1] - 0.010, ph[1] + EMB, tz - dz - EMB, tz + dz + EMB)
    pocket_role = "gunmetal" if prefix == "TRL" else hull["role"]                # the pocket walls: the hull's lower colour
    set_roles(cut, (pocket_role,), lambda f: pocket_role)
    cut = finish(cut, col)
    del cut["role"]
    cut.hide_set(True)
    cut.hide_render = True
    cut.display_type = "WIRE"
    cut_with(hull, cut, "ShockPockets")
    out.append(finish(plate, col))


def build_trailer():
    """Cargo variant: the shared frame, per-bay handrails, the standard drawbar."""
    set_origin(TRAILER_Y)
    col = collection("GEO_TrailerCargo")
    out = []
    build_frame_body(col, out)

    # handrails: one U section per bay down each side, two across the front and rear; feet buried in the deck
    p = Part("TRL_Rails", "orange", smooth=True)
    rad, top_h, foot = 0.06, 1.58, 1.315
    for s in (1, -1):
        for z in BAY_Z:
            a, b = z + 0.33, z - 0.33
            tube(p, [T(s * 1.03, foot, a), T(s * 1.03, top_h - rad, a), T(s * 1.03, top_h, a - rad),
                     T(s * 1.03, top_h, b + rad), T(s * 1.03, top_h - rad, b), T(s * 1.03, foot, b)], 0.022, seg=10)
    for zz in (2.60, -2.60):
        for x0, x1 in ((0.12, 0.92), (-0.12, -0.92)):
            d = rad if x1 > x0 else -rad
            tube(p, [T(x0, foot, zz), T(x0, top_h - rad, zz), T(x0 + d, top_h, zz),
                     T(x1 - d, top_h, zz), T(x1, top_h - rad, zz), T(x1, foot, zz)], 0.022, seg=10)
    out.append(finish(p, col))
    build_drawbar(col, out)
    return out


def build_drawbar(col, out, draw_ext=0.0, prefix="TRL", braced=False):
    """Drawbar out of the chin, braced by a small truss, ending in a lunette ring (ring centre = hitch point),
    with the parking jack. draw_ext lengthens it: the lunette, truss end, jack and drawbar end move forward.
    braced (hab): the A-frame diagonals start on plates bolted under the chassis and end in lugs on a yoke clamped
    round the drawbar; a post from the cross tube to a rear collar replaces the V braces."""
    e = draw_ext
    p = Part(prefix + "_Drawbar", "charcoal")
    box(p, -0.06, 0.06, 0.74, 0.86, 2.40, 3.83 + e)
    out.append(finish(p, col, bevel=0.012))
    p = Part(prefix + "_Lunette", "charcoal", smooth=True)
    torus(p, T(0, 0.80, 3.90 + e), Vector((0, 0, 1)), 0.075, 0.028, seg=20, seg2=10)
    out.append(finish(p, col))

    d0, d1 = (0.62, 1.19, 2.58), (0.05, 0.86, 3.40 + e)
    if braced:
        d1 = (HAB_TOW["lug_x"], 0.86, HAB_TOW["yoke_z"])

    def diag(t, s):
        return T(s * (d0[0] + (d1[0] - d0[0]) * t), d0[1] + (d1[1] - d0[1]) * t, d0[2] + (d1[2] - d0[2]) * t)

    p = Part(prefix + "_Truss", "charcoal", smooth=True)
    if not braced:
        for s in (1, -1):
            tube(p, [T(s * d0[0], d0[1] + 0.01, d0[2] - 0.02), diag(1.0, s)], 0.03, seg=10)
            tube(p, [diag(0.30, s), T(s * 0.055, 0.80, 3.02 + 0.5 * e), diag(0.62, s)], 0.022, seg=8)
        tube(p, [diag(0.30, 1), diag(0.30, -1)], 0.022, seg=8)
        out.append(finish(p, col))
    else:
        zy, zc = HAB_TOW["yoke_z"], diag(0.30, 1).y - _origin_y          # yoke, and the collar under the cross tube
        hc = diag(0.30, 1).z
        for s in (1, -1):
            tube(p, [T(s * d0[0], d0[1] - 0.005, d0[2] - 0.03), diag(1.0, s)], 0.03, seg=10)   # diagonal: plate -> lug
        tube(p, [diag(0.30, 1), diag(0.30, -1)], 0.022, seg=8)                                   # cross tube
        tube(p, [T(0.0, hc, zc), T(0.0, 0.885 - EMB, zc)], 0.022, seg=8)                          # post to the collar
        out.append(finish(p, col))
        py, pb = Part(prefix + "_TrussYoke", "black"), Part(prefix + "_TrussPlates", "black")
        for z0, z1 in ((zy - 0.08, zy + 0.08), (zc - 0.055, zc + 0.055)):                         # clamp collars
            box(py, -0.085, 0.085, 0.715, 0.885, z0, z1)
        for s in (1, -1):
            box(py, min(s * 0.08, s * 0.155), max(s * 0.08, s * 0.155), 0.80, 0.905, zy - 0.05, zy + 0.05)   # lugs
            box(pb, min(s * 0.53, s * 0.71), max(s * 0.53, s * 0.71), 1.165, 1.205, d0[2] - 0.10, d0[2] + 0.07)  # chassis plate
        Mr = plane_map((0.0, 0.0, zy + 0.08), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
        for u in (-0.055, 0.055):
            for v in (0.745, 0.855):
                bolt(py, Mr, u, v, r=0.009, h=0.006)                                                # yoke clamp bolts
        Mu = plane_map((0.0, 1.165, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0))    # plate undersides
        for s in (1, -1):
            for du in (-0.06, 0.06):
                for dz in (-0.065, 0.035):
                    bolt(pb, Mu, s * 0.62 + du, d0[2] + dz, r=0.011, h=0.008)
        out.append(finish(py, col, bevel=0.008, bevel_segments=1))
        out.append(finish(pb, col, bevel=0.006, bevel_segments=1))

    # parking jack beside the drawbar (stowed: foot well clear of the ground)
    p = Part(prefix + "_Jack", "charcoal", smooth=True)
    jx, jz = 0.15, 3.56 + e   # clear of the truss end (3.40 + e) and the lunette block (3.76 + e)
    cylinder(p, T(jx, 0.50, jz), Vector((0, 0, 1)), 0.04, 0.52)
    cylinder(p, T(jx, 0.44, jz), Vector((0, 0, 1)), 0.09, 0.03)
    cylinder(p, T(jx, 0.47, jz), Vector((0, 0, 1)), 0.026, 0.04)
    out.append(finish(p, col))
    p = Part(prefix + "_JackBracket", "charcoal")
    box(p, 0.05, jx - 0.03, 0.77, 0.83, jz - 0.03, jz + 0.03)
    out.append(finish(p, col, bevel=0.006, bevel_segments=1))
    p = Part(prefix + "_JackCrank", "orange", smooth=True)
    cylinder(p, T(jx, 1.02, jz), Vector((0, 0, 1)), 0.018, 0.05)
    cylinder(p, T(jx, 1.058, jz), Vector((1, 0, 0)), 0.011, 0.12)
    out.append(finish(p, col))


# ---------------------------------------------------------------- rover receiver
def _ease(a, b, t):
    t = max(0.0, min(1.0, t))
    return a + (b - a) * (0.5 - 0.5 * math.cos(math.pi * t))


# ---------------------------------------------------------------- habitat trailer
HAB_ORIGIN_Y = -2.70 - 4.45   # Blender Y of the hab origin when its lunette sits on the rover pin
HAB_EXT = 0.55                # longer drawbar: turning clearance as the cargo trailer
HAB = dict(floor=1.45, half_w=1.0, top=3.45, half_l=3.0, out_half_w=1.15, out_half_l=3.2, roof=3.65,
           low_x=1.06, low_h=1.32, chamfer_h=1.70, shoulder_h=3.35, roof_x=0.95, door_half_w=0.6, door_top=3.35)
# Phase 2 moving parts: driver-side (left) slide-out on the flat wall band (raised floor 1.72), folding ladder hinged
# 0.10 m behind the rear face (folds 130 deg: its door-side edge stops 4 cm off the door frame), four stabiliser legs,
# the deploy control panel (front-left).
HAB_DEPLOY = dict(slide_z=(-0.2, 2.2), slide_h=(1.72, 3.30), slide_depth=0.75, slide_travel=0.65, slide_wall=0.04,
                  ladder_hinge=(0.0, 1.42, -3.40), ladder_stow_x=130.0,
                  legs=((0.9, 2.55), (-0.9, 2.55), (0.9, -2.55), (-0.9, -2.55)), leg_stow_h=0.35, leg_max=0.80,
                  panel=((-1.17, -1.15), (1.85, 2.15), (2.45, 2.75)))   # clear of the front corner facet (z 2.90)
# Phase 3: the game's composite door on the rear wall (frame 2.0 x 2.04 x 0.5, leaves 1.37 x 1.71 with the sill at the
# floor), the tongue box with the two portable-tank cradles, the life-support panel (right wall, rear).
HAB_AIR = dict(door_pos=(0.0, 2.205, -3.05), door_open=((-1.0, 1.0), (1.45, 3.205)),
               tongue=((-0.95, 0.95), (1.26, 1.32), (3.2, 4.05)), cradles=(("AIR SUPPLY", -0.46), ("WASTE", 0.46)),
               cradle_z=3.62, panel=((0.94, 1.0), (2.75, 3.25), (-2.64, -1.86)),
               sockets=(("Filter1", -2.52), ("Filter2", -2.34), ("Battery1", -2.16), ("Battery2", -1.98)))


def hab_wheel_refs():
    """Viewing only (not exported): the rover tyre/arm references shifted to the hab's hitched position."""
    col = collection("GEO_TrailerHab_Ref")
    for o in bpy.data.collections["BLK_TrailerCargo"].objects:
        if o.name.startswith("RWA_"):
            c = o.copy()
            c.name = "HABREF_" + o.name[4:]
            c.location.y += HAB_ORIGIN_Y - TRAILER_Y
            col.objects.link(c)


def _hab_section():
    """Outer section (x, h), clockwise from the right lower edge: tucked-in lower facet (inside the tyres), full-width
    walls above the tyre tops, chamfered roof shoulders."""
    H = HAB
    right = [(H["low_x"], H["low_h"]), (H["out_half_w"], H["chamfer_h"]), (H["out_half_w"], H["shoulder_h"]),
             (H["roof_x"], H["roof"])]
    return right + [(-x, h) for x, h in reversed(right)]


def _fill(bm, loops):
    edges = []
    for loop in loops:
        for i in range(len(loop)):
            a, b = loop[i], loop[(i + 1) % len(loop)]
            edges.append(bm.edges.get((a, b)) or bm.edges.new((a, b)))
    bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=True, edges=edges)


EMB = 0.006   # a mounted part reaches this far into the surface under it: a weld, never a coplanar face
# Hab tow bar: the A-frame diagonals and the tongue box's front struts meet in lugs on a yoke round the drawbar
HAB_TOW = dict(yoke_z=3.92, lug_x=0.125)
# End facets (Unity x, h, z): vertical corner chamfers and sloped top facets, all outside the room (x +-1.0, h 1.45-3.45,
# z +-3.0) by 2.5 cm or more; the rear ones stay clear of the game's door frame (x +-1.0, h 1.16-3.205, z -3.29..-2.79).
HAB_FACETS = dict(front_corner=((1.15, 2.90), (0.85, 3.20)), front_top=((2.85, 3.20), (3.65, 2.97)),
                  rear_corner=((1.15, -3.02), (1.06, -3.20)), rear_top=((3.65, -2.97), (3.33, -3.20)))
HAB_TWO_TONE = 1.65      # the hab outside: silver above, gunmetal below this line (under the side rub strip, 1.625-1.675)
HAB_SEAMS = dict(z=(-2.60, -1.90, -1.20, -0.50, 0.20, 0.90, 1.60, 2.30), h=(2.55,), x=(0.0,))
HAB_FRAME_BOX = ((-1.0, 1.164, -3.288), (1.0, 3.205, -2.788))    # StructureCompositeDoor frame (x, h, z), from its mesh
HAB_LANDING_Z = -3.36                                            # door landing ends 4 cm before the ladder hinge axis
HAB_LANDING_BARS = 8                                             # 13 mm bars on a 21.5 mm pitch, from there to the wall
# Cradle straps round the portable tanks (tank: r 0.42 at most, base ring r 0.356, top at +1.177, origin +0.573)
HAB_CRADLE = dict(r=0.437, t=0.012, w=0.05, heights=(1.62, 2.00))
SHARED_FRAME = ("HAB_Hull", "HAB_TailLenses", "HAB_RearStep", "HAB_Drawbar", "HAB_Lunette", "HAB_Truss", "HAB_Jack")


def _udir(dx, dh, dz):
    """Unity direction -> Blender direction."""
    return Vector((dx, dz, dh))


def plane_map(origin, eu, ev, ew):
    """Surface-local (u, v, w) -> Blender point: origin + u*eu + v*ev + w*ew, all given in Unity (x, h, z)."""
    o, a, b, c = (Vector(t) for t in (origin, eu, ev, ew))

    def f(u, v, w):
        q = o + a * u + b * v + c * w
        return T(q.x, q.y, q.z)
    return f


def side_map(s, x0=None):
    """Side wall s (+1 right, -1 left): u runs to the viewer's right (right wall +z, left wall -z), v = h, w outwards."""
    X = HAB["out_half_w"] if x0 is None else x0
    return plane_map((s * X, 0.0, 0.0), (0.0, 0.0, s * 1.0), (0.0, 1.0, 0.0), (s * 1.0, 0.0, 0.0))


def cap_map(front):
    """Front cap (z +3.2, u = -x) or rear cap (z -3.2, u = +x): v = h, w outwards."""
    Z = HAB["out_half_l"]
    if front:
        return plane_map((0.0, 0.0, Z), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
    return plane_map((0.0, 0.0, -Z), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, -1.0))


def facet_map(xz_a, xz_b, out_hint):
    """Vertical facet through two (x, z) points: u to the viewer's right from its first edge, v = h, w outwards.
    Returns (map, facet width)."""
    a, b = Vector(xz_a), Vector(xz_b)
    d = (b - a).normalized()
    n = Vector((d.y, -d.x))
    if n.dot(Vector(out_hint)) < 0:
        n = -n
    right = Vector((-n.y, n.x))           # (-n) x up, in (x, z)
    o = a if (b - a).dot(right) > 0 else b
    return plane_map((o.x, 0.0, o.y), (right.x, 0.0, right.y), (0.0, 1.0, 0.0), (n.x, 0.0, n.y)), (b - a).length


def slope_map(front):
    """Top end facet: u across (viewer's right), v up the slope from its lower edge, w outwards."""
    (h1, z1), (h2, z2) = HAB_FACETS["front_top" if front else "rear_top"]
    lo, hi = ((h1, z1), (h2, z2)) if h1 < h2 else ((h2, z2), (h1, z1))
    v = Vector((0.0, hi[0] - lo[0], hi[1] - lo[1])).normalized()
    n = Vector((0.0, v.z, -v.y)) if front else Vector((0.0, -v.z, v.y))
    if n.y < 0:
        n = -n
    u = (-1.0, 0.0, 0.0) if front else (1.0, 0.0, 0.0)
    return plane_map((0.0, lo[0], lo[1]), u, tuple(v), tuple(n)), (Vector(hi) - Vector(lo)).length


def wbox(p, M, u0, u1, v0, v1, w0, w1):
    return prism(p, [(u0, v0), (u1, v0), (u1, v1), (u0, v1)], w0, w1, lambda u, v, w: M(u, v, w))


def sweep_rect(p, pts, up, w, t):
    """Flat strap along a polyline of Blender points: w tall along up, t thick; flat top and bottom, round sides."""
    bm = p.bm
    n = len(pts)
    rings = []
    for i in range(n):
        t_in = (pts[i] - pts[i - 1]).normalized() if i > 0 else (pts[1] - pts[0]).normalized()
        t_out = (pts[i + 1] - pts[i]).normalized() if i < n - 1 else t_in
        tan = (t_in + t_out).normalized()
        side = tan.cross(up).normalized()
        k = 1.0 / max(0.3, side.dot(t_in.cross(up).normalized()))
        s_, u_ = side * (t / 2) * k, up * (w / 2)
        rings.append([bm.verts.new(pts[i] + a + b) for a, b in ((-s_, -u_), (s_, -u_), (s_, u_), (-s_, u_))])
    for i in range(n - 1):
        r0, r1 = rings[i], rings[i + 1]
        for k in range(4):
            f = bm.faces.new((r0[k], r0[(k + 1) % 4], r1[(k + 1) % 4], r1[k]))
            f.smooth = k in (1, 3)
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])


def text_into(p, text, M, u_c, v_c, size, w0, w1):
    """Raised letters (Blender's built-in font) centred at (u_c, v_c) on a surface map, from w0 to w1."""
    cu = bpy.data.curves.new("TMP_Text", type="FONT")
    cu.body, cu.size, cu.extrude = text, size, (w1 - w0) / 2
    cu.align_x, cu.align_y, cu.resolution_u = "CENTER", "CENTER", 2
    ob = bpy.data.objects.new("TMP_Text", cu)
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.update()
    deps = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(deps)
    me = ev.to_mesh()
    wm = (w0 + w1) / 2
    vs = [p.bm.verts.new(M(u_c + v.co.x, v_c + v.co.y, wm + v.co.z)) for v in me.vertices]
    for poly in me.polygons:
        try:
            p.bm.faces.new([vs[i] for i in poly.vertices])
        except ValueError:
            pass
    ev.to_mesh_clear()
    bpy.data.objects.remove(ob, do_unlink=True)
    bpy.data.curves.remove(cu)


def louvre(pf, ps, M, u0, u1, v0, v1, n=5, depth=0.035, frame=0.025):
    """Vent grille: frame bars (pf) round a dark back plate with slats sloping down outwards (ps)."""
    wbox(pf, M, u0, u1, v0, v0 + frame, -EMB, depth)
    wbox(pf, M, u0, u1, v1 - frame, v1, -EMB, depth)
    wbox(pf, M, u0, u0 + frame, v0 + frame - 0.002, v1 - frame + 0.002, -EMB, depth)
    wbox(pf, M, u1 - frame, u1, v0 + frame - 0.002, v1 - frame + 0.002, -EMB, depth)
    wbox(ps, M, u0 + frame - 0.003, u1 - frame + 0.003, v0 + frame - 0.003, v1 - frame + 0.003, -EMB, 0.006)
    pitch = (v1 - v0 - 2 * frame) / n
    for k in range(n):
        vb = v0 + frame + pitch * k
        prof = [(vb + pitch - 0.002, 0.004), (vb + pitch - 0.012, 0.004), (vb - 0.004, depth - 0.008), (vb + 0.006, depth - 0.008)]
        prism(ps, prof, u0 + frame - 0.003, u1 - frame + 0.003, lambda v, w, u: M(u, v, w))


def rrect(u0, u1, v0, v1, r, steps=4):
    """Rounded rectangle outline (u, v), counter-clockwise, 4 * (steps + 1) points."""
    r = max(0.002, min(r, (u1 - u0) / 2 - 0.001, (v1 - v0) / 2 - 0.001))
    pts = []
    for cu, cv, a0 in ((u1 - r, v0 + r, -90), (u1 - r, v1 - r, 0), (u0 + r, v1 - r, 90), (u0 + r, v0 + r, 180)):
        for k in range(steps + 1):
            a = math.radians(a0 + 90 * k / steps)
            pts.append((cu + r * math.cos(a), cv + r * math.sin(a)))
    return pts


def ring_prism(p, outer, inner, w0, w1, M):
    """Frame between two matching outlines (same point count), from w0 to w1 on a surface map."""
    bm = p.bm
    n = len(outer)
    lo = [[bm.verts.new(M(u, v, w)) for u, v in loop] for loop, w in ((outer, w0), (inner, w0))]
    hi = [[bm.verts.new(M(u, v, w)) for u, v in loop] for loop, w in ((outer, w1), (inner, w1))]
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((hi[0][i], hi[0][j], hi[1][j], hi[1][i]))          # front
        bm.faces.new((lo[0][j], lo[0][i], lo[1][i], lo[1][j]))          # back
        bm.faces.new((lo[0][i], lo[0][j], hi[0][j], hi[0][i]))          # outer side
        bm.faces.new((lo[1][j], lo[1][i], hi[1][i], hi[1][j]))          # inner side


def window(pf, pg, M, u0, u1, v0, v1, panes=1, frame=0.035, depth=0.026, r=0.06):
    """Window: dark pane recessed behind a rounded frame (pf) with mullions."""
    prism(pg, rrect(u0 + 0.01, u1 - 0.01, v0 + 0.01, v1 - 0.01, r - 0.01), -EMB, 0.010, lambda u, v, w: M(u, v, w))
    ring_prism(pf, rrect(u0, u1, v0, v1, r), rrect(u0 + frame, u1 - frame, v0 + frame, v1 - frame, r - frame * 0.7),
               -EMB, depth, M)
    for k in range(1, panes):
        uc = u0 + (u1 - u0) * k / panes
        wbox(pf, M, uc - frame * 0.4, uc + frame * 0.4, v0 + frame - 0.004, v1 - frame + 0.004, -EMB, depth - 0.006)


def bolt(p, M, u, v, w=0.0, r=0.009, h=0.008):
    """Hex bolt head standing on a surface map at (u, v), from w - EMB to w + h."""
    a, b = M(u, v, w - EMB), M(u, v, w + h)
    cylinder(p, a, b - a, r, (b - a).length, seg=6, smooth=False)


def lamp(ph, pl, M, u0, u1, v0, v1, proud=0.018):
    """Lamp: black housing with a lens standing 6 mm proud of it."""
    wbox(ph, M, u0, u1, v0, v1, -EMB, proud)
    wbox(pl, M, u0 + 0.008, u1 - 0.008, v0 + 0.008, v1 - 0.008, proud - 0.003, proud + 0.006)


def _plane_xz(p1, p2):
    (x1, z1), (x2, z2) = p1, p2
    n = Vector((z2 - z1, 0.0, -(x2 - x1)))
    if n.x * x1 + n.z * z1 < 0:
        n = -n
    return (x1, 0.0, z1), tuple(n.normalized())


def _plane_hz(p1, p2):
    (h1, z1), (h2, z2) = p1, p2
    n = Vector((0.0, z2 - z1, -(h2 - h1)))
    if n.y * (h1 - 2.5) + n.z * z1 < 0:
        n = -n
    return (0.0, h1, z1), tuple(n.normalized())


def _cut(bm, co, no):
    """Remove everything on the outer side of a plane (Unity point, outward normal) and cap the hole."""
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=T(*co),
                           plane_no=_udir(*no).normalized(), clear_outer=True)
    bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)


def _min_extent(f):
    e = max(f.edges, key=lambda x: x.calc_length())
    a = (e.verts[1].co - e.verts[0].co).normalized()
    b = f.normal.cross(a).normalized()
    pa, pb = [v.co.dot(a) for v in f.verts], [v.co.dot(b) for v in f.verts]
    return min(max(pa) - min(pa), max(pb) - min(pb))


def _hull_z(x):
    """Front surface z of the hull at |x| (flat cap, then the corner facet)."""
    (x1, z1), (x2, z2) = HAB_FACETS["front_corner"]
    ax = abs(x)
    if ax <= x2:
        return z2
    return z1 + (ax - x1) * (z2 - z1) / (x2 - x1)


def _hab_shell(col, out):
    """Hull: the section prism cut by the end facets (convex), edges chamfered, panel seams grooved; the room with
    the door tunnel and the slide-out opening are boolean cuts."""
    H, F = HAB, HAB_FACETS
    p = Part("HAB_Shell", "silver")
    bm = p.bm
    L = H["out_half_l"]
    prism(p, _hab_section(), -L, L, lambda x, h, z: T(x, h, z))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    for s in (1, -1):
        for key in ("front_corner", "rear_corner"):
            (x1, z1), (x2, z2) = F[key]
            _cut(bm, *_plane_xz((s * x1, z1), (s * x2, z2)))
    for key in ("front_top", "rear_top"):
        _cut(bm, *_plane_hz(*F[key]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.normal_update()
    sharp = [e for e in bm.edges if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > math.radians(10)]
    res = bmesh.ops.bevel(bm, geom=sharp, offset=0.022, segments=2, profile=0.5, affect="EDGES", clamp_overlap=True)
    for f in res["faces"]:
        f.smooth = True
    S = HAB_SEAMS
    for co, no in ([(T(0, 0, z), Vector((0, 1, 0))) for z in S["z"]] + [(T(0, h, 0), Vector((0, 0, 1))) for h in S["h"]]
                   + [(T(x, 0, 0), Vector((1, 0, 0))) for x in S["x"]]):
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=no)
    bm.normal_update()
    panels = [f for f in bm.faces if not f.smooth and f.normal.z > -0.2 and _min_extent(f) > 0.09]
    bmesh.ops.inset_individual(bm, faces=panels, thickness=0.011, depth=-0.004, use_even_offset=True)
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=T(0, HAB_TWO_TONE, 0),
                           plane_no=Vector((0, 0, 1)))
    set_roles(p, ("silver", "gunmetal"), lambda f: "silver" if f.calc_center_median().z > HAB_TWO_TONE else "gunmetal")
    shell = finish(p, col)
    out.append(shell)
    # cutters (not exported): the room plus the door tunnel through the rear cap, and the slide-out opening
    A, D = HAB_AIR, HAB_DEPLOY
    (ox0, ox1), (oh0, oh1) = A["door_open"]
    rl, r0, r1 = H["half_l"], H["floor"], H["top"]
    for name in ("HAB_RoomCut", "HAB_SlideCut"):
        old = bpy.data.objects.get(name)
        if old:
            bpy.data.objects.remove(old, do_unlink=True)
    cp = Part("HAB_RoomCut", "white")
    set_roles(cp, ("white",), lambda f: "white")
    prism(cp, [(rl, r0), (rl, r1), (-rl, r1), (-rl, oh1), (-L - 0.3, oh1), (-L - 0.3, r0)], ox0, ox1,
          lambda z, h, x: T(x, h, z))
    room = finish(cp, col)
    cp = Part("HAB_SlideCut", "white")
    (z0, z1), (h0, h1) = D["slide_z"], D["slide_h"]
    box(cp, -1.30, -0.95, h0, h1, z0, z1)
    set_roles(cp, ("silver",), lambda f: "silver")                     # the opening's jambs: the outside's colour
    slide = finish(cp, col)
    for cut, label in ((room, "Room"), (slide, "SlideOpening")):
        del cut["role"]
        cut.hide_set(True)
        cut.hide_render = True
        cut.display_type = "WIRE"
        cut_with(shell, cut, label)


def _hab_sides(col, out):
    """Rub strips with marker lamps, windows, vents, the service hatch and the deploy control panel."""
    pb, pg, pv = Part("HAB_Trim", "orange"), Part("HAB_Glass", "glass"), Part("HAB_Vents", "dark")
    pa, pr, pw = Part("HAB_MarkerAmber", "amber"), Part("HAB_MarkerRed", "lamp"), Part("HAB_Hatch", "silver")
    pm = Part("HAB_Hardware", "gunmetal", smooth=True)
    for s in (1, -1):
        M = side_map(s)

        def U(z0, z1):
            return (z0, z1) if s > 0 else (-z1, -z0)
        # rub strip at the tuck-in line (clear of the slide-out flange and of the tyres' reach, h >= 1.62)
        wbox(pb, M, *U(-2.95, 2.85), 1.625, 1.675, -0.025, 0.025)
        for z, part in ((2.55, pa), (0.95, pa), (-2.75, pr)):
            wbox(part, M, *U(z - 0.035, z + 0.035), 1.633, 1.667, 0.022, 0.031)
    M = side_map(1)
    window(pb, pg, M, 0.0, 1.2, 2.35, 2.95, panes=2)                 # right side, over the dining table
    window(pb, pg, M, -2.42, -1.98, 2.45, 2.92)                        # right side, rear
    louvre(pb, pv, M, 2.25, 2.70, 1.85, 2.20, n=4)                     # right side, front
    # service hatch (right, rear lower): raised panel hinged at the top, pull handle at the bottom
    wbox(pw, M, -1.55, -0.75, 1.80, 2.35, -EMB, 0.012)
    for u in (-1.52, -0.78):
        for v in (1.83, 2.32):
            bolt(pm, M, u, v, w=0.012, r=0.007, h=0.004)
    for zc in (-1.45, -0.85):
        cylinder(pm, M(zc - 0.05, 2.35, 0.018), M(zc + 0.05, 2.35, 0.018) - M(zc - 0.05, 2.35, 0.018), 0.012, 0.10, seg=10)
        wbox(pb, M, zc - 0.03, zc + 0.03, 2.33, 2.37, 0.008, 0.016)
    tube(pm, [M(-1.25, 1.86, 0.008), M(-1.25, 1.86, 0.045), M(-1.05, 1.86, 0.045), M(-1.05, 1.86, 0.008)], 0.009, seg=8)
    M = side_map(-1)
    window(pb, pg, M, -2.78, -2.32, 2.45, 2.92)                        # left side, front (u = -z)
    louvre(pb, pv, M, 2.30, 2.75, 1.95, 2.30, n=4)                     # left side, rear
    # deploy control panel (driver side, front): the collider box is D["panel"]
    D = HAB_DEPLOY
    (px0, px1), (ph0, ph1), (pz0, pz1) = D["panel"]
    depth = abs(px0) - HAB["out_half_w"]
    pp = Part("HAB_DeployPanel", "black")
    wbox(pp, M, -pz1, -pz0, ph0, ph1, -EMB, depth)
    out.append(finish(pp, col, bevel=0.006, bevel_segments=1))
    pf = Part("HAB_DeployFrame", "gunmetal")
    for a0, a1, b0, b1 in ((-pz1, -pz0, ph0, ph0 + 0.015), (-pz1, -pz0, ph1 - 0.015, ph1),
                           (-pz1, -pz1 + 0.015, ph0 + 0.013, ph1 - 0.013), (-pz0 - 0.015, -pz0, ph0 + 0.013, ph1 - 0.013)):
        wbox(pf, M, a0, a1, b0, b1, depth - 0.003, depth + 0.006)
    uc = -(pz0 + pz1) / 2
    for u in (-pz1 + 0.0075, -pz0 - 0.0075):
        for v in (ph0 + 0.0075, ph1 - 0.0075):
            bolt(pf, M, u, v, w=depth + 0.006, r=0.005, h=0.003)
    torus(pf, M(uc, 1.99, depth + 0.004), M(uc, 1.99, 1.0) - M(uc, 1.99, 0.0), 0.05, 0.008, seg=20, seg2=6)
    out.append(finish(pf, col, bevel=0.003, bevel_segments=1))
    pbt = Part("HAB_DeployButton", "red", smooth=True)
    cylinder(pbt, M(uc, 1.99, depth - 0.003), M(uc, 1.99, 1.0) - M(uc, 1.99, 0.0), 0.034, 0.022, seg=20)
    out.append(finish(pbt, col))
    wbox(pa, M, uc - 0.10, uc - 0.08, 2.085, 2.105, depth - 0.003, depth + 0.008)
    wbox(pr, M, uc + 0.08, uc + 0.10, 2.085, 2.105, depth - 0.003, depth + 0.008)
    pt = Part("HAB_DeployLabel", "white")
    text_into(pt, "DEPLOY", M, uc, 1.885, 0.034, depth - 0.002, depth + 0.003)
    out.append(finish(pt, col))
    for part in (pb, pv, pa, pr, pw):
        out.append(finish(part, col, bevel=0.004, bevel_segments=1))
    out.append(finish(pg, col))
    out.append(finish(pm, col))


def _hab_front(col, out):
    """Tongue box with the two portable-tank cradles: base chocks, two wall-anchored straps per tank with hinge,
    buckle and pads, label plates above; intake grille and marker lamps on the front facets."""
    A, C = HAB_AIR, HAB_CRADLE
    (tx0, tx1), (th0, th1), (tz0, tz1) = A["tongue"]
    p = Part("HAB_Tongue", "black")
    box(p, tx0, tx1, th0, th1, tz0 - 0.10, tz1)                                         # platform (back edge in the hull)
    for s_ in (1, -1):
        box(p, min(s_ * 0.90, s_ * 0.95), max(s_ * 0.90, s_ * 0.95), th1 - EMB, th1 + 0.18, _hull_z(0.95) - 0.02, tz1)
    box(p, tx0, tx1, th1 - EMB, th1 + 0.07, tz1 - 0.05, tz1)                             # low front lip (under the tanks' base)
    box(p, tx0 + 0.02, tx1 - 0.02, th0 - 0.05, th0 + EMB, tz0 - 0.10, tz0 - 0.04)         # rail under the rear edge (hull floor)
    for s_ in (1, -1):
        box(p, min(s_ * 0.74, s_ * 0.82), max(s_ * 0.74, s_ * 0.82), th0 - 0.03, th0 + EMB, 3.91, 3.99)   # strut clevis plates
    out.append(finish(p, col, bevel=0.01, bevel_segments=1))
    pst = Part("HAB_TongueStruts", "gunmetal", smooth=True)
    for s_ in (1, -1):                                                                    # front struts down to the yoke lugs
        tube(pst, [T(s_ * 0.78, th0 - 0.015, 3.95), T(s_ * (HAB_TOW["lug_x"] + 0.01), 0.87, HAB_TOW["yoke_z"])], 0.022, seg=8)
    out.append(finish(pst, col))
    ps, pk = Part("HAB_CradleStraps", "gunmetal"), Part("HAB_CradleBrackets", "gunmetal")
    pl, pd = Part("HAB_CradleLatches", "red"), Part("HAB_CradlePads", "black")
    pin = Part("HAB_CradleHinges", "gunmetal", smooth=True)
    R, t, w, cz = C["r"], C["t"], C["w"], A["cradle_z"]
    up = Vector((0, 0, 1))
    for _, cx in A["cradles"]:
        for a in (45, 135, 225, 315):                                                    # base chocks round the tank foot
            ca, sa = math.cos(math.radians(a)), math.sin(math.radians(a))
            M = plane_map((cx, 0.0, cz), (ca, 0.0, sa), (0.0, 1.0, 0.0), (-sa, 0.0, ca))
            prism(pd, [(0.368, th1 - EMB), (0.44, th1 - EMB), (0.44, th1 + 0.045), (0.40, th1 + 0.045)], -0.03, 0.03,
                  lambda u, v, ww: M(u, v, ww))
        for hs in C["heights"]:
            xo, xi = cx - R if cx < 0 else cx + R, cx + R if cx < 0 else cx - R          # outer / inner strap ends
            arc = [T(cx + R * math.cos(math.radians(a)), hs, cz + R * math.sin(math.radians(a))) for a in range(180, -1, -15)]
            pts = [T(cx - R, hs, _hull_z(cx - R) - 0.012)] + arc + [T(cx + R, hs, _hull_z(cx + R) - 0.012)]
            sweep_rect(ps, pts, up, w, t)
            hw = 0.028                                                                   # outer wall bracket
            zb = _hull_z(abs(xo) - hw) + 0.065
            box(pk, xo - hw, xo + hw, hs - 0.045, hs + 0.045, _hull_z(abs(xo) + hw) - 0.015, zb)
            Mf = plane_map((0.0, 0.0, zb), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
            for dv in (-0.034, 0.034):
                bolt(pk, Mf, -xo, hs + dv, r=0.007, h=0.006)
            cylinder(pin, T(xo, hs - 0.042, _hull_z(xo) + 0.05), up, 0.013, 0.084, seg=10)   # hinge pin on the outer bracket
            zf = cz + R + t / 2
            box(pl, cx - 0.035, cx + 0.035, hs - 0.03, hs + 0.03, zf - EMB, zf + 0.016)    # cam buckle at the front
            box(pl, cx - 0.075, cx + 0.03, hs - 0.013, hs + 0.013, zf + 0.012, zf + 0.024)  # lever folded along the strap
            cylinder(pin, T(cx + 0.03, hs - 0.02, zf + 0.018), up, 0.009, 0.04, seg=8)      # lever pivot
            for a in (45, 135):                                                          # rubber pads inside the strap
                ca, sa = math.cos(math.radians(a)), math.sin(math.radians(a))
                M = plane_map((cx, 0.0, cz), (ca, 0.0, sa), (0.0, 1.0, 0.0), (-sa, 0.0, ca))
                wbox(pd, M, 0.423, R - t / 2 + EMB, hs - 0.02, hs + 0.02, -0.02, 0.02)
    Mf = plane_map((0.0, 0.0, _hull_z(0.0) + 0.065), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
    for hs in C["heights"]:                                                              # shared centre bracket
        box(pk, -0.06, 0.06, hs - 0.045, hs + 0.045, _hull_z(0.0) - 0.015, _hull_z(0.0) + 0.065)
        for du in (-0.045, 0.045):
            for dv in (-0.034, 0.034):
                bolt(pk, Mf, du, hs + dv, r=0.007, h=0.006)
    out.append(finish(ps, col))
    out.append(finish(pk, col, bevel=0.004, bevel_segments=1))
    out.append(finish(pl, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pd, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pin, col))
    # label plates above the tanks (front cap: u = -x)
    M = cap_map(True)
    for label, cx in A["cradles"]:
        air = cx < 0
        pb_ = Part("HAB_CradleLabelFrame" + ("Air" if air else "Waste"), "black")
        wbox(pb_, M, -cx - 0.275, -cx + 0.275, 2.555, 2.725, -EMB, 0.008)
        out.append(finish(pb_, col, bevel=0.003, bevel_segments=1))
        pp = Part("HAB_CradleLabel" + ("Air" if air else "Waste"), "white" if air else "red")
        wbox(pp, M, -cx - 0.26, -cx + 0.26, 2.57, 2.71, -EMB, 0.012)
        for du in (-0.235, 0.235):
            bolt(pp, M, -cx + du, 2.64, w=0.012, r=0.008, h=0.004)
        out.append(finish(pp, col, bevel=0.003, bevel_segments=1))
        pt = Part("HAB_CradleText" + ("Air" if air else "Waste"), "black" if air else "white")
        text_into(pt, label, M, -cx, 2.64, 0.075, 0.010, 0.016)
        out.append(finish(pt, col))
    # intake grille and clearance lamps on the sloped top facet, marker lamps on the corner facets
    pb, pv, pa = Part("HAB_FrontTrim", "black"), Part("HAB_FrontVent", "dark"), Part("HAB_FrontLamps", "amber")
    M, length = slope_map(True)
    louvre(pb, pv, M, -0.55, 0.55, 0.16, 0.52, n=5)
    for u in (-0.78, 0.78):
        lamp(pb, pa, M, u - 0.05, u + 0.05, 0.58, 0.64)
    for s in (1, -1):
        (x1, z1), (x2, z2) = HAB_FACETS["front_corner"]
        M, length = facet_map((s * x1, z1), (s * x2, z2), (s, 1.0))
        lamp(pb, pa, M, length / 2 - 0.035, length / 2 + 0.035, 2.62, 2.95)
    out.append(finish(pb, col, bevel=0.004, bevel_segments=1))
    out.append(finish(pv, col, bevel=0.002, bevel_segments=1))
    out.append(finish(pa, col, bevel=0.003, bevel_segments=1))


def _hab_rear(col, out):
    """Door hood, grab bars and tail-lamp strips on the corner facets, bumper with lamps, the door landing with the
    static half of the ladder hinge."""
    D = HAB_DEPLOY
    pb, pl, pg = Part("HAB_RearTrim", "black"), Part("HAB_RearLamps", "lamp"), Part("HAB_RearRails", "gunmetal", smooth=True)
    M = cap_map(False)
    fx = HAB_FRAME_BOX
    hood = -fx[0][2] - HAB["out_half_l"] + 0.025
    wbox(pb, M, -1.0, 1.0, fx[1][1] + 0.01, fx[1][1] + 0.065, -EMB, hood)                          # door hood
    for u in (-0.9, -0.3, 0.3, 0.9):
        bolt(pb, M, u, fx[1][1] + 0.0375, w=hood, r=0.008, h=0.005)
    for s in (1, -1):
        (x1, z1), (x2, z2) = HAB_FACETS["rear_corner"]
        M, length = facet_map((s * x1, z1), (s * x2, z2), (s, -1.0))
        uc = length / 2
        lamp(pb, pl, M, uc - 0.035, uc + 0.035, 1.74, 2.12)
        tube(pg, [M(uc, 2.25, -EMB), M(uc, 2.25, 0.07), M(uc, 3.05, 0.07), M(uc, 3.05, -EMB)], 0.016, seg=10)
    # bumper under the door frame, on two brackets from the chassis tail; lamps at its ends
    zb0, zb1 = -3.20, -3.33
    box(pb, -1.0, 1.0, 0.98, 1.12, zb1, zb0)
    Mb = plane_map((0.0, 0.0, zb1), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, -1.0))
    for s in (1, -1):
        box(pb, min(s * 0.58, s * 0.66), max(s * 0.58, s * 0.66), 1.00, 1.10, zb0 - 0.01, -2.64)
        for v in (1.02, 1.08):
            bolt(pb, Mb, s * 0.62, v, r=0.010, h=0.007)
    M = cap_map(False)
    for s in (1, -1):
        lamp(pb, pl, plane_map((0.0, 0.0, zb1), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, -1.0)),
             min(s * 0.70, s * 0.95), max(s * 0.70, s * 0.95), 1.005, 1.095, proud=0.012)
    out.append(finish(pb, col, bevel=0.008, bevel_segments=1))
    out.append(finish(pl, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pg, col))
    # landing at the door sill (bars on two gussets welded to the hab's rear wall) and the static hinge knuckles + pin.
    # The game's door frame has nothing under the doorway, so the landing runs back to the shell (the user's 0.2.1
    # report: ending at the frame box it floated 8.5 cm off the wall)
    hx, hh, hz = D["ladder_hinge"]
    zf = -HAB["out_half_l"] + EMB                                                     # into the rear wall
    pk = Part("HAB_Landing", "gunmetal")
    for k in range(HAB_LANDING_BARS):
        z0 = HAB_LANDING_Z + k * 0.0215
        box(pk, -0.495, 0.495, 1.42, 1.45, z0, min(z0 + 0.013, zf))
    box(pk, -0.52, -0.49, 1.412, 1.452, HAB_LANDING_Z, zf)
    box(pk, 0.49, 0.52, 1.412, 1.452, HAB_LANDING_Z, zf)
    for x in (-0.30, 0.30):
        prism(pk, [(zf, 1.42 + EMB), (zf, HAB["low_h"] + 0.01), (HAB_LANDING_Z + 0.01, 1.42 + EMB)], x - 0.006, x + 0.006,
              lambda z, h, xx: T(xx, h, z))
    for s in (1, -1):
        box(pk, min(s * 0.37, s * 0.44), max(s * 0.37, s * 0.44), hh - 0.02, hh + 0.022, hz, HAB_LANDING_Z + 0.005)
    out.append(finish(pk, col, bevel=0.003, bevel_segments=1))
    ph = Part("HAB_LadderHinge", "gunmetal", smooth=True)
    for s in (1, -1):
        cylinder(ph, T(s * 0.37, hh, hz), _udir(s, 0, 0), 0.03, 0.07, seg=16)          # static knuckles
        cylinder(ph, T(s * 0.515, hh, hz), _udir(s, 0, 0), 0.02, 0.012, seg=12)         # pin nuts
    cylinder(ph, T(-0.515, hh, hz), _udir(1, 0, 0), 0.012, 1.03, seg=10)                # hinge pin
    out.append(finish(ph, col))


def _hab_roof(col, out):
    """AC unit (fan guard, side louvres, skids, pipe), a mushroom vent and an antenna; the solar panels are the game's
    own (interior.json, role roof); clearance lamps on the sloped rear facet."""
    rf = HAB["roof"]
    x0, x1, z0, z1 = -0.42, 0.42, -2.35, -1.65
    pw, pb, pd = Part("HAB_AC", "steel"), Part("HAB_ACSkids", "black"), Part("HAB_ACGrilles", "dark")
    pm = Part("HAB_ACFan", "gunmetal", smooth=True)
    box(pw, x0, x1, rf + 0.035, rf + 0.27, z0, z1)
    out.append(finish(pw, col, bevel=0.03, bevel_segments=2))
    for x in (-0.33, 0.33):
        box(pb, x - 0.035, x + 0.035, rf - EMB, rf + 0.04, z0 - 0.03, z1 + 0.03)
    out.append(finish(pb, col, bevel=0.006, bevel_segments=1))
    top = rf + 0.27
    zc = (z0 + z1) / 2
    cylinder(pd, T(0.0, top - EMB, zc), Vector((0, 0, 1)), 0.25, 0.012, seg=24, smooth=False)   # fan well
    cylinder(pm, T(0.0, top + 0.004, zc), Vector((0, 0, 1)), 0.055, 0.03, seg=16)              # hub
    torus(pm, T(0.0, top + 0.012, zc), Vector((0, 0, 1)), 0.245, 0.01, seg=24, seg2=6)          # guard rim
    torus(pm, T(0.0, top + 0.012, zc), Vector((0, 0, 1)), 0.15, 0.006, seg=20, seg2=6)          # guard ring
    for a in (0, 45, 90, 135):
        ca, sa = math.cos(math.radians(a)), math.sin(math.radians(a))
        tube(pm, [T(-0.245 * ca, top + 0.012, zc - 0.245 * sa), T(0.245 * ca, top + 0.012, zc + 0.245 * sa)], 0.005, seg=6)
    pf = Part("HAB_ACLouvreFrames", "steel")
    for s in (1, -1):
        M = side_map(s, x0=0.42)
        u0, u1 = (z0 + 0.08, z1 - 0.08) if s > 0 else (-z1 + 0.08, -z0 - 0.08)
        louvre(pf, pd, M, u0, u1, rf + 0.08, rf + 0.23, n=5, depth=0.02, frame=0.018)
    out.append(finish(pf, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pd, col))
    tube(pm, [T(0.28, rf + 0.12, z1 - 0.02), T(0.28, rf + 0.12, z1 + 0.10), T(0.28, rf - EMB, z1 + 0.10)], 0.018, seg=10)
    out.append(finish(pm, col))
    # mounting plates under the game's solar tracker arms (positions from the interior layout)
    import hab_interior
    ps = Part("HAB_SolarMounts", "gunmetal")
    for name, prefab, pos, euler, role in hab_interior.PROPS:
        if role != "roof":
            continue
        x, _, z = pos
        box(ps, x - 0.19, x + 0.19, rf - EMB, rf + 0.018, z - 0.19, z + 0.19)
        Mt = plane_map((0.0, rf + 0.018, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0))
        for du in (-0.155, 0.155):
            for dv in (-0.155, 0.155):
                bolt(ps, Mt, x + du, z + dv, r=0.011, h=0.008)
    out.append(finish(ps, col, bevel=0.004, bevel_segments=1))
    # mushroom vent and antenna
    pv = Part("HAB_RoofVent", "steel", smooth=True)
    cylinder(pv, T(-0.6, rf - EMB, -1.20), Vector((0, 0, 1)), 0.07, 0.11, seg=16)
    cylinder(pv, T(-0.6, rf + 0.10, -1.20), Vector((0, 0, 1)), 0.13, 0.03, seg=20)
    out.append(finish(pv, col))
    pa = Part("HAB_Antenna", "gunmetal", smooth=True)
    box(pa, 0.70, 0.80, rf - EMB, rf + 0.03, -2.80, -2.70)
    cylinder(pa, T(0.75, rf + 0.025, -2.75), Vector((0, 0, 1)), 0.012, 0.62, seg=8)
    sphere(pa, T(0.75, rf + 0.645, -2.75), 0.02, seg=10, rings=6)
    out.append(finish(pa, col))
    pb, pl = Part("HAB_RoofTrim", "black"), Part("HAB_RoofLamps", "lamp")
    M, length = slope_map(False)
    for u in (-0.72, 0.72):
        lamp(pb, pl, M, u - 0.05, u + 0.05, length * 0.45, length * 0.45 + 0.06)
    out.append(finish(pb, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pl, col, bevel=0.003, bevel_segments=1))


def _hab_legs(col, out):
    """Static leg sleeves: square tube, bottom collar, top plate into the chassis, a gusset, lock pin with pull ring."""
    D = HAB_DEPLOY
    p, pins = Part("HAB_LegSleeves", "black"), Part("HAB_LegPins", "red", smooth=True)
    for lx, lz in D["legs"]:
        top = 1.19 if lz > 0 else 0.90
        bot = D["leg_stow_h"] + 0.04
        box(p, lx - 0.05, lx + 0.05, bot, top, lz - 0.05, lz + 0.05)
        box(p, lx - 0.062, lx + 0.062, bot - 0.012, bot + 0.035, lz - 0.062, lz + 0.062)
        box(p, lx - 0.09, lx + 0.09, top - 0.022, top + EMB, lz - 0.09, lz + 0.09)
        si = -1 if lx > 0 else 1                                                          # inboard
        prism(p, [(lx + si * 0.049, top - 0.02), (lx + si * 0.049, top - 0.26), (lx + si * 0.17, top - 0.02)],
              lz - 0.008, lz + 0.008, lambda x, h, z: T(x, h, z))
        cylinder(pins, T(lx, bot + 0.13, lz - 0.075), Vector((0, 1, 0)), 0.011, 0.15, seg=10)
        torus(pins, T(lx, bot + 0.10, lz + 0.075), Vector((1, 0, 0)), 0.028, 0.006, seg=16, seg2=6)
    out.append(finish(p, col, bevel=0.006, bevel_segments=1))
    out.append(finish(pins, col))


def _hab_life_support(col, out):
    """Life-support panel inside, right wall by the door: 2 filter + 2 battery sockets."""
    (px0, px1), (ph0, ph1), (pz0, pz1) = HAB_AIR["panel"]
    p = Part("HAB_LifeSupport", "gray")
    box(p, px0, px1, ph0, ph1, pz0, pz1)
    for _, sz in HAB_AIR["sockets"]:
        box(p, px0 - 0.03, px0, 2.90, 3.10, sz - 0.08, sz + 0.08)
    out.append(finish(p, col, bevel=0.006, bevel_segments=1))
    # wall plate for the light switch (the game's switch lever is cloned onto it at runtime, see the interior layout)
    import hab_interior
    sw = next(pos for name, prefab, pos, euler, role in hab_interior.PROPS if role == "switch")
    M = plane_map((-HAB["half_w"], 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0), (1.0, 0.0, 0.0))   # inner left wall
    pp, pt = Part("HAB_SwitchPlate", "black"), Part("HAB_SwitchLabel", "white")
    wbox(pp, M, sw[2] - 0.075, sw[2] + 0.075, sw[1] - 0.10, sw[1] + 0.12, -EMB, 0.012)
    for dv in (-0.085, 0.105):
        bolt(pp, M, sw[2], sw[1] + dv, w=0.012, r=0.006, h=0.003)
    text_into(pt, "LIGHTS", M, sw[2], sw[1] + 0.075, 0.026, 0.010, 0.014)
    out.append(finish(pp, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pt, col))
    # water rack on the right wall (hab_interior WaterRack, R-facing): floor plate, back plate, two cradle bands per
    # Liquid Canister (0.21 wide, 0.85 tall, standing on the plate), raised CLEAN WATER / WASTE WATER labels
    _, _, rp, _, _ = next(e for e in hab_interior.PROPS if e[4] == "rack")
    slots = hab_interior.EXTRA["WaterRack"]["rackSlots"]
    Mr = plane_map((HAB["half_w"], 0.0, 0.0), (0.0, 0.0, -1.0), (0.0, 1.0, 0.0), (-1.0, 0.0, 0.0))   # inner right wall, u = -z
    pr, pl = Part("HAB_WaterRack", "gray"), Part("HAB_WaterRackLabel", "white")
    uc = -rp[2]
    wbox(pr, Mr, uc - 0.29, uc + 0.29, rp[1] - EMB, rp[1] + 0.012, -EMB, 0.23)          # floor plate
    wbox(pr, Mr, uc - 0.29, uc + 0.29, rp[1] + 0.012, rp[1] + 1.0, -EMB, 0.012)         # back plate
    for rs, label in zip(slots, ("CLEAN WATER", "WASTE WATER")):
        u = -(rp[2] + rs["pos"][0])                   # R-local x runs along +z
        for h in (rp[1] + 0.15, rp[1] + 0.60):          # two cradle bands round each canister
            wbox(pr, Mr, u - 0.13, u + 0.13, h, h + 0.03, 0.215, 0.227)                   # front bar
            for s_ in (-1, 1):
                wbox(pr, Mr, u + s_ * 0.13 - 0.006, u + s_ * 0.13 + 0.006, h, h + 0.03, 0.012 - EMB, 0.227)
        text_into(pl, label, Mr, u, rp[1] + 0.95, 0.024, 0.010, 0.014)
    out.append(finish(pr, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pl, col))
    # HAB STATUS screen above the consoles (hab_interior StatusScreen, face 3 cm off the left wall): a black bezel round a
    # dark face; the mod draws the live page 2 mm in front of the face at runtime
    _, _, qp, _, _ = next(e for e in hab_interior.PROPS if e[4] == "screen")
    pb, pf = Part("HAB_StatusScreen", "black"), Part("HAB_StatusScreenFace", "dark")
    wall, face = -HAB["half_w"], qp[0]
    sw, sh = hab_interior.MESH["StatusScreen"][1][:2]                                 # outer size, lip 2 cm
    z0, z1, h0, h1 = qp[2] - sw / 2, qp[2] + sw / 2, qp[1] - sh / 2, qp[1] + sh / 2
    box(pb, wall - EMB, face - 0.004, h0, h1, z0, z1)                                   # back box, to just behind the face
    for a0, a1, b0, b1 in ((h0, h0 + 0.02, z0, z1), (h1 - 0.02, h1, z0, z1), (h0, h1, z0, z0 + 0.02), (h0, h1, z1 - 0.02, z1)):
        box(pb, face - 0.004, face + 0.004, a0, a1, b0, b1)                             # bezel lip, 4 mm proud of the face
    box(pf, face - 0.006, face - 0.002, h0 + 0.02, h1 - 0.02, z0 + 0.02, z1 - 0.02)     # dark face
    out.append(finish(pb, col, bevel=0.003, bevel_segments=1))
    out.append(finish(pf, col))
    # shower plinth: a skirt round the (sunk) shower base, from the floor to just under the tray lip, hiding the game
    # model's pipe fitting and cable sockets (its mesh cannot be trimmed at runtime); the back is the wall
    _, _, sp, _, _ = next(e for e in hab_interior.PROPS if e[4] == "shower")
    ps = Part("HAB_ShowerPlinth", "gray")
    f0, h1 = HAB["floor"] - EMB, sp[1] + 0.128                  # tray lip starts at node y 0.13
    x_front = sp[0] - 0.75 - 0.002                              # tray front (node z 0.75, R: out along -x)
    z_a, z_b = sp[2] - 0.255 - 0.002, sp[2] + 0.255 + 0.002     # tray sides (node x +/-0.255, R: along +z)
    box(ps, x_front - 0.015, x_front, f0, h1, z_a - 0.015, z_b + 0.015)                   # front
    for z0, z1 in ((z_a - 0.015, z_a), (z_b, z_b + 0.015)):                              # sides, to the wall
        box(ps, x_front - 0.015, HAB["half_w"] + EMB, f0, h1, z0, z1)
    out.append(finish(ps, col, bevel=0.003, bevel_segments=1))


def _hab_slideout():
    """Slide-out box (retracted pose): 4 cm walls matching its colliders, a flange over the opening's edge, and on the
    outer face a three-pane window, a low vent and a drip rail."""
    D = HAB_DEPLOY
    scol = collection("GEO_HabSlideOut")
    (z0, z1), (h0, h1) = D["slide_z"], D["slide_h"]
    w, d = D["slide_wall"], D["slide_depth"]
    xo, xi = -1.15, -1.15 + d
    p = Part("HABS_Box", "silver")
    box(p, xo - w, xo, h0, h1, z0, z1)                        # outer wall
    box(p, xo - w, xi, h0 - w, h0, z0, z1)                    # floor
    box(p, xo - w, xi, h1, h1 + w, z0, z1)                    # roof
    box(p, xo - w, xi, h0, h1, z0, z0 + w)                    # rear end wall
    box(p, xo - w, xi, h0, h1, z1 - w, z1)                    # front end wall
    lo, hi = T(xo - w, h0 - w, z0), T(xi, h1 + w, z1)
    lo, hi = Vector(map(min, lo, hi)), Vector(map(max, lo, hi))

    def facing_out(f):                    # a face whose outside is beyond the box (not its inner edge, toward the room)
        c, n = f.calc_center_median(), f.normal
        q = c + n * 0.03
        return n.x < 0.5 and not all(lo[k] - 1e-4 <= q[k] <= hi[k] + 1e-4 for k in range(3))
    bmesh.ops.recalc_face_normals(p.bm, faces=p.bm.faces)
    p.bm.normal_update()
    set_roles(p, ("silver", "white"), lambda f: "silver" if facing_out(f) else "white")
    finish(p, scol, bevel=0.01, bevel_segments=2)
    # bunk end panels at the head and foot of the bunk pair (beds z 0.13..1.87), 2 cm boards with a black aisle-edge
    # trim, from the outer wall to the slide-out's inner edge (they ride in the room with it when stowed)
    pe, pt = Part("HABS_BunkEnds", "white"), Part("HABS_BunkEndTrim", "black")
    for za, zb in ((0.105, 0.125), (1.875, 1.895)):
        box(pe, xo - EMB, xi - 0.02, h0 - EMB, h1 + EMB, za, zb)
        box(pt, xi - 0.02, xi, h0 - EMB, h1 + EMB, za - 0.002, zb + 0.002)
    finish(pe, scol, bevel=0.004, bevel_segments=1)
    finish(pt, scol, bevel=0.002, bevel_segments=1)
    p = Part("HABS_Flange", "black")
    for a0, a1, b0, b1 in ((h0 - 0.05, h0, z0 - 0.05, z1 + 0.05), (h1, h1 + 0.05, z0 - 0.05, z1 + 0.05),
                           (h0, h1, z0 - 0.05, z0), (h0, h1, z1, z1 + 0.05)):
        box(p, xo - w - 0.02, xo - w, a0, a1, b0, b1)
    finish(p, scol, bevel=0.004, bevel_segments=1)
    M = side_map(-1, x0=-(xo - w))
    pf, pg, pv = Part("HABS_Frame", "orange"), Part("HABS_Glass", "glass"), Part("HABS_Vent", "dark")
    window(pf, pg, M, -1.85, -0.15, 2.46, 3.08, panes=3)
    louvre(pf, pv, M, -1.60, -0.40, 1.88, 2.22, n=4, depth=0.03)
    wbox(pf, M, -(z1 + 0.05), -(z0 - 0.05), h1 + 0.05, h1 + 0.075, -0.02, 0.055)       # drip rail over the flange
    finish(pf, scol, bevel=0.004, bevel_segments=1)
    finish(pg, scol)
    finish(pv, scol, bevel=0.002, bevel_segments=1)


def _hab_ladder():
    """Folding stairs in the deployed pose: 22 cm deep C-channel stringers that hold each level tread inside them and
    taper into knuckles on the hinge axis (the fold never sweeps into the landing), slatted treads with amber nosings
    on cross rods, bolted to the webs, short feet."""
    D = HAB_DEPLOY
    lcol = collection("GEO_HabLadder")
    hx, hh, hz = D["ladder_hinge"]
    zb, slope, half = hz - 1.40, 0.786, 0.11                     # stringer centre line runs through the tread centres

    def hc(z):
        return 0.225 + slope * (z - zb)
    zt = hz - 0.02
    band = [(zb, hc(zb) - half), (zb, hc(zb) + half), (zt, hc(zt) + half), (hz, hh + 0.04), (hz, hh - 0.04), (zt, hc(zt) - half)]
    flanges = ([(zb, hc(zb) + half), (zt, hc(zt) + half), (zt, hc(zt) + half - 0.012), (zb, hc(zb) + half - 0.012)],
               [(zb, hc(zb) - half), (zt, hc(zt) - half), (zt, hc(zt) - half + 0.012), (zb, hc(zb) - half + 0.012)])
    ps, pt, pn = Part("HABL_Stringers", "gunmetal"), Part("HABL_Treads", "gunmetal"), Part("HABL_Nosing", "amber")
    pk, pf = Part("HABL_Knuckles", "gunmetal", smooth=True), Part("HABL_Feet", "black")
    for s in (1, -1):
        prism(ps, band, min(s * 0.476, s * 0.49), max(s * 0.476, s * 0.49), lambda z, h, x: T(x, h, z))    # web
        for fl in flanges:
            prism(ps, fl, min(s * 0.45, s * 0.478), max(s * 0.45, s * 0.478), lambda z, h, x: T(x, h, z))
        cylinder(pk, T(s * 0.45, hh, hz), _udir(s, 0, 0), 0.04, 0.04, seg=16)
        box(ps, min(s * 0.445, s * 0.495), max(s * 0.445, s * 0.495), 0.03, hc(zb) - half + 0.02, zb, zb + 0.07)  # foot post
        box(pf, min(s * 0.435, s * 0.505), max(s * 0.435, s * 0.505), 0.0, 0.035, zb - 0.02, zb + 0.09)          # rubber pad
    for k in range(5):
        h = 0.35 + 0.22 * k
        zz = hz - 0.28 * (5 - k)
        for j in range(5):
            zj = zz + 0.03 + j * 0.045
            box(pn if j == 0 else pt, -0.478, 0.478, h - 0.03, h, zj, zj + 0.022)
        for x in (-0.22, 0.22):
            box(pt, x - 0.006, x + 0.006, h - 0.045, h - 0.028, zz + 0.025, zz + 0.255)
        for s in (1, -1):                                                  # tread end bolts on the web
            Mw = plane_map((s * 0.49, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0), (s * 1.0, 0.0, 0.0))
            for dz in (-0.05, 0.05):
                bolt(pk, Mw, zz + 0.14 + dz, h - 0.015, r=0.008, h=0.006)
    finish(ps, lcol, bevel=0.006, bevel_segments=1)
    finish(pt, lcol, bevel=0.002, bevel_segments=1)
    finish(pn, lcol, bevel=0.002, bevel_segments=1)
    finish(pk, lcol)
    finish(pf, lcol, bevel=0.006, bevel_segments=1)


def _hab_leg():
    """One inner leg with its swivel foot, local origin = foot bottom, built at the first leg position."""
    D = HAB_DEPLOY
    gcol = collection("GEO_HabLeg")
    lx, lz = D["legs"][0]
    p = Part("HABG_Leg", "gunmetal")
    box(p, lx - 0.035, lx + 0.035, 0.06, 0.93, lz - 0.035, lz + 0.035)     # stays 9 cm inside the sleeve at full stroke
    finish(p, gcol, bevel=0.005, bevel_segments=1)
    p = Part("HABG_Swivel", "gunmetal", smooth=True)
    sphere(p, T(lx, 0.06, lz), 0.034, seg=14, rings=8)
    cylinder(p, T(lx, 0.024, lz), Vector((0, 0, 1)), 0.10, 0.012, seg=20)
    finish(p, gcol)
    p = Part("HABG_Pad", "black", smooth=True)
    cylinder(p, T(lx, 0.0, lz), Vector((0, 0, 1)), 0.12, 0.026, seg=20)
    finish(p, gcol)


def build_hab():
    """Habitat variant: shared frame (black) with the longer drawbar, the faceted hollow shell, and its fittings."""
    set_origin(HAB_ORIGIN_Y)
    col = collection("GEO_TrailerHab")
    out = []
    build_frame_body(col, out, hull_role="charcoal", prefix="HAB")
    build_drawbar(col, out, draw_ext=HAB_EXT, prefix="HAB", braced=True)
    _hab_shell(col, out)
    _hab_sides(col, out)
    _hab_front(col, out)
    _hab_rear(col, out)
    _hab_roof(col, out)
    _hab_legs(col, out)
    _hab_life_support(col, out)
    _hab_slideout()
    _hab_ladder()
    _hab_leg()
    return out


def _points(objects, deps):
    pts = []
    for o in objects:
        e = o.evaluated_get(deps)
        me = e.to_mesh()
        pts += [(v.x, v.z, v.y - HAB_ORIGIN_Y) for v in (e.matrix_world @ w.co for w in me.vertices)]   # (x, h, z)
        e.to_mesh_clear()
    return pts


def hab_checks():
    """Tyre overlap, room clearance, the yaw at which the hab first reaches the rover's rear tyres, the door frame kept
    free, and the ladder's fold (stowed pose) clear of the frame and the landing."""
    deps = bpy.context.evaluated_depsgraph_get()
    parts = [o for o in bpy.data.collections["GEO_TrailerHab"].objects if o.type == "MESH" and "role" in o]
    body = [o for o in parts if not o.name.startswith(SHARED_FRAME)]
    fixtures = ("HAB_LifeSupport", "HAB_Switch", "HAB_WaterRack", "HAB_ShowerPlinth", "HAB_StatusScreen")                      # wall-mounted fixtures inside the room
    pts = _points([o for o in body if not o.name.startswith(fixtures)], deps)
    A = HAB_AIR
    for _, cx in A["cradles"]:        # portable tanks standing in the cradles (r 0.41, h 1.32..2.50)
        for k in range(4):
            for a16 in range(16):
                ang = 2 * math.pi * a16 / 16
                pts.append((cx + 0.41 * math.cos(ang), 1.32 + 0.39 * k, A["cradle_z"] + 0.41 * math.sin(ang)))
    tyre_hits = [p for p in pts if 1.114 < abs(p[0]) < 1.8 and p[1] < 1.62 and any(abs(p[2] - z) < 0.72 for z in (1.7, 0.0, -1.7))]
    room_blocked = [p for p in pts if abs(p[0]) < 0.95 and 1.47 < p[1] < 3.43 and abs(p[2]) < 2.95]
    rover_tyres = []
    for n in ("TireBackL.001", "TireBackR.001"):
        o = bpy.data.objects[n]
        q = [o.matrix_world @ v.co for v in o.data.vertices]
        rover_tyres.append(([min(v.x for v in q), min(v.z for v in q), min(v.y for v in q)],
                            [max(v.x for v in q), max(v.z for v in q), max(v.y for v in q)]))
    hitch_t, hitch_r = (0.0, 0.8, 3.9 + HAB_EXT), (0.0, 0.8, -2.7)
    min_yaw = None
    for deg in range(0, 91):
        a = math.radians(deg)
        for p in pts:
            dx, dz = p[0] - hitch_t[0], p[2] - hitch_t[2]
            x = hitch_r[0] + dx * math.cos(a) + dz * math.sin(a)
            z = hitch_r[2] - dx * math.sin(a) + dz * math.cos(a)
            if any(mn[0] < x < mx[0] and mn[1] < p[1] < mx[1] and mn[2] < z < mx[2] for mn, mx in rover_tyres):
                min_yaw = deg
                break
        if min_yaw is not None:
            break
    (fx0, fh0, fz0), (fx1, fh1, fz1) = HAB_FRAME_BOX
    # the landing runs under the doorway into the wall: the game's frame is open there (its box reaches h 1.164 only at
    # the posts; in game the old landing ended at the box face and hung 8.5 cm off the wall)
    rear = [o for o in body if not o.name.startswith(("HAB_Shell", "HAB_Landing") + fixtures)]
    in_frame = [p for p in _points(rear, deps) if fx0 + 0.005 < p[0] < fx1 - 0.005 and fh0 + 0.005 < p[1] < fh1 - 0.005
                and fz0 + 0.005 < p[2] < fz1 - 0.005]
    landing = _points([o for o in body if o.name.startswith("HAB_Landing")], deps)
    shell = [p for p in _points([o for o in body if o.name.startswith("HAB_Shell")], deps) if abs(p[0]) < 0.5 and HAB["low_h"] < p[1] < 1.45]
    wall_gap = round(min(p[2] for p in shell) - max(p[2] for p in landing), 4) if landing and shell else None   # > 0: floats
    result = {"points": len(pts), "tyre_hits": len(tyre_hits), "room_blocked": len(room_blocked), "min_yaw_deg": min_yaw,
              "door_frame_hits": len(in_frame), "landing_wall_gap": wall_gap}
    D = HAB_DEPLOY
    slide = _points([o for o in bpy.data.collections["GEO_HabSlideOut"].objects if o.type == "MESH"], deps)
    out_pts = [(x - D["slide_travel"], h, z) for x, h, z in slide]          # deployed pose
    slide_tyre_hits = [p for p in out_pts if -1.8 < p[0] < -1.114 and p[1] < 1.374 and any(abs(p[2] - z) < 0.72 for z in (1.7, 0.0, -1.7))]
    walkway = [p for p in slide if p[0] > -0.38 and 1.72 < p[1] < 3.30]
    # ladder folded (Unity +X rotation by ladder_stow_x about the hinge): clear of the door frame and the landing
    hx, hh, hz = D["ladder_hinge"]
    lad = _points([o for o in bpy.data.collections["GEO_HabLadder"].objects if o.type == "MESH"], deps)
    a = math.radians(D["ladder_stow_x"])
    stowed = []
    for x, h, z in lad:
        dy, dz = h - hh, z - hz
        stowed.append((x, hh + dy * math.cos(a) - dz * math.sin(a), hz + dy * math.sin(a) + dz * math.cos(a)))
    fold_hits = [p for p in stowed if (fx0 < p[0] < fx1 and fh0 < p[1] < fh1 and p[2] > fz0 + 0.002)
                 or (abs(p[0]) < 0.52 and 1.42 < p[1] < 1.45 and p[2] > HAB_LANDING_Z + 0.002)]
    deployed_hits = [p for p in lad if abs(p[0]) < 0.52 and 1.42 < p[1] < 1.45 and p[2] > HAB_LANDING_Z + 0.002]
    result.update(slide_tyre_hits=len(slide_tyre_hits), walkway_blocked_retracted=len(walkway), slide_points=len(slide),
                  ladder_fold_hits=len(fold_hits), ladder_landing_hits=len(deployed_hits))
    # bunk end panels: inside the slide-out (retracted x -1.15..-0.40, h 1.72..3.30), just past each bed end (bed z
    # 0.13..1.87 from hab_interior), never over the beds
    ends = [o for o in bpy.data.collections["GEO_HabSlideOut"].objects if o.type == "MESH" and o.name.startswith("HABS_BunkEnd")]
    if not ends:
        result["bunk_ends"] = "missing"
    else:
        bad = [p for p in _points(ends, deps) if not (-1.157 <= p[0] <= -0.399 and 1.713 <= p[1] <= 3.307
               and (0.098 <= p[2] <= 0.1295 or 1.8705 <= p[2] <= 1.902))]
        result["bunk_ends"] = "ok" if not bad else f"{len(bad)} points outside"
    return result



# ---------------------------------------------------------------- vehicle upgrades (spec 2026-09-26-storm-upgrades)


def frustum(p, centre, axis, r0, r1, length, seg=16, smooth=True):
    """Open-ended cone section from radius r0 at centre to r1 at centre + axis * length (a nozzle bell), capped."""
    bm = p.bm
    axis = axis.normalized()
    ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((1, 0, 0))
    u = axis.cross(ref).normalized()
    v = axis.cross(u).normalized()
    ring = lambda c, r: [bm.verts.new(c + (u * math.cos(2 * math.pi * k / seg) + v * math.sin(2 * math.pi * k / seg)) * r) for k in range(seg)]
    a, b = ring(centre, r0), ring(centre + axis * length, r1)
    for k in range(seg):
        f = bm.faces.new((a[k], a[(k + 1) % seg], b[(k + 1) % seg], b[k]))
        f.smooth = smooth
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)


UPG_AXLES_Z = (1.7, 0.0, -1.7)
UPG_HUB_X, UPG_HUB_H = 1.4574, 0.6446          # every tyre's hub (rover and trailers use the rover's wheels)
UPG_TYRE_OUT = 1.801                           # tyre outer face |x|
UPG_TRAVEL_UP = 0.5                            # WheelCollider: centre +0.5, distance 1.0, target 0.5 -> hub rises 0.5 m
UPG_HAB_SKIRTS = ((-2.85, -1.78), (-1.62, -0.08), (0.08, 1.62), (1.78, 2.85))   # split at the axles: gaps for the fender brackets


def _fender(col, name, hub, x_in, space, arc=(35, 145)):
    """One plastic fender flare over a tyre, pivoted on its hub so the mod can move it with the wheel (it rides the
    suspension and steers with a steered wheel, so the tyre can never reach it): an arch over `arc` (deg), radius
    0.87-0.905 about the hub (14 cm over the tread), |x| x_in-1.84, held by two stays down to a hub boss just outside
    the tyre's outer face. Silver with a 1.5 cm orange outer edge, gunmetal stays (the rover's palette). Returns
    [arch, stays] (object names start with name)."""
    hx, hh, hz = hub
    s_ = 1 if hx > 0 else -1
    ro, ri = 0.905, 0.87
    a0, a1 = arc
    pts = [(hz + ro * math.cos(math.radians(a)), hh + ro * math.sin(math.radians(a))) for a in range(a0, a1 + 1, 10)]
    pts += [(hz + ri * math.cos(math.radians(a)), hh + ri * math.sin(math.radians(a))) for a in range(a1, a0 - 1, -10)]
    pa, ps = Part(name + "_Arch", "silver", smooth=True), Part(name + "_Stays", "gunmetal")
    prism(pa, pts, *sorted((s_ * x_in, s_ * 1.84)), lambda u, v, w: space(w, v, u))
    bm = pa.bm
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=space(s_ * 1.825, hh, hz),
                           plane_no=Vector((1, 0, 0)))
    set_roles(pa, ("silver", "orange"), lambda f: "orange" if abs(f.calc_center_median().x) > 1.825 else "silver")
    xs = s_ * (UPG_TYRE_OUT + 0.014)                                      # stays and boss: 1.4 cm outside the tyre
    cylinder(ps, space(s_ * (UPG_TYRE_OUT + 0.006), hh, hz), Vector((s_, 0, 0)), 0.055, 0.018)
    for a in (a0 + 5, a1 - 5):
        top = space(xs, hh + ri * math.sin(math.radians(a)) + EMB, hz + ri * math.cos(math.radians(a)))
        tube(ps, [space(xs, hh, hz), top], 0.011, seg=8)
    return [finish(pa, col, bevel=0.004, bevel_segments=1), finish(ps, col)]


def build_upgrades_trailer():
    """Cargo Trailer (trailer-local T space, origin TRAILER_Y). Armour (steel): a rear plate on the rear face (z -2.65,
    h 0.82-1.25) and L corner caps on the four deck corners. Fairings (plastic): fender flares over the six tyres."""
    set_origin(TRAILER_Y)
    out = {}
    col = collection("GEO_UpgArmourTrailer")
    p = Part("UTA_RearPlate", "gunmetal")
    box(p, -0.92, 0.92, 0.84, 1.24, -2.672, -2.65 + EMB)
    for h in (0.94, 1.04, 1.14):
        box(p, -0.88, 0.88, h - 0.006, h + 0.006, -2.682, -2.670)
    out_a = [finish(p, col, bevel=0.004, bevel_segments=1)]
    p = Part("UTA_CornerCaps", "gunmetal")
    for sx in (-1, 1):
        for sz in (-1, 1):
            xa, xb = sorted((sx * (1.08 - EMB), sx * 1.10))
            za, zb = sorted((sz * 2.30, sz * 2.672))
            box(p, xa, xb, 1.12, 1.335, za, zb)                                  # side leg
            xa2, xb2 = sorted((sx * 0.80, sx * 1.10))
            za2, zb2 = sorted((sz * (2.65 - EMB), sz * 2.672))
            box(p, xa2, xb2, 1.12, 1.335, za2, zb2)                              # end leg
    out_a.append(finish(p, col, bevel=0.004, bevel_segments=1))
    out["Armour"] = out_a
    col = collection("GEO_UpgFairingTrailer")
    out["Fairings"] = [o for k, z in enumerate(UPG_AXLES_Z) for s_ in (-1, 1)
                       for o in _fender(col, f"UTF_Fender{'L' if s_ < 0 else 'R'}{k}", (s_ * UPG_HUB_X, UPG_HUB_H, z), 1.10, T)]
    return out


def build_upgrades_hab():
    """Habitat (hab-local T space, origin HAB_ORIGIN_Y). Armour (steel): skirt panels on the lower chamfer (x 1.06 at
    h 1.32 to 1.15 at h 1.70) from h 1.36 to 1.46, both sides, four panels split at the axles (fender brackets), clear of the deploy
    panel (driver side h 1.85+). Fairings (plastic): the same fender flares as the Cargo Trailer."""
    set_origin(HAB_ORIGIN_Y)
    H = HAB
    cx = lambda h: H["low_x"] + (h - H["low_h"]) * (H["out_half_w"] - H["low_x"]) / (H["chamfer_h"] - H["low_h"])
    out = {}
    col = collection("GEO_UpgArmourHab")
    p = Part("UHA_Skirts", "gunmetal")
    h0, h1, t = 1.36, 1.46, 0.014          # top at 1.46: the outer face stays inside the tyres' inner face (x 1.114)
    for s_ in (-1, 1):
        for za, zb in UPG_HAB_SKIRTS:
            prof = [(s_ * (cx(h0) - EMB), h0), (s_ * (cx(h0) + t), h0), (s_ * (cx(h1) + t), h1), (s_ * (cx(h1) - EMB), h1)]
            if s_ < 0: prof = list(reversed(prof))
            prism(p, prof, za, zb, lambda u, v, w: T(u, v, w))
    out_a = [finish(p, col, bevel=0.004, bevel_segments=1)]
    p = Part("UHA_SkirtBolts", "gunmetal")
    for s_ in (-1, 1):
        for za, zb in UPG_HAB_SKIRTS:
            for z in (za + 0.08, zb - 0.08):
                for h in (1.385, 1.435):
                    cylinder(p, T(s_ * (cx(h) + t - 0.002), h, z), Vector((s_, 0, 0)), 0.010, 0.008, seg=6, smooth=False)
    out_a.append(finish(p, col))
    out["Armour"] = out_a
    col = collection("GEO_UpgFairingHab")                       # inner edge outboard of the hull (1.15) so it can rise past it
    out["Fairings"] = [o for k, z in enumerate(UPG_AXLES_Z) for s_ in (-1, 1)
                       for o in _fender(col, f"UHF_Fender{'L' if s_ < 0 else 'R'}{k}", (s_ * UPG_HUB_X, UPG_HUB_H, z), 1.16, T)]
    return out




def build(variant="cargo"):
    if variant == "hab":
        return {"hab": [o.name for o in build_hab()]}
    trl = build_trailer()
    upt = build_upgrades_trailer()
    return {"trailer": [o.name for o in trl], "upgrades_trailer": {k: [o.name for o in v] for k, v in upt.items()}}
