"""The frames' build stages (spec 2026-10-08): which model parts show at each frame state, the chassis state's running
gear and trestles, the hab's wall frame, and the baked state meshes with rover.json / trailer.json / hab.json "frame".
A part's "stage" is the first state it shows in; a scaffold part (trestles, the hab's wall frame) also has "until", the
last state it shows in. The last state is never baked: it reuses the vehicle's body mesh (plan decision 3)."""
import os
import sys

import bpy
from mathutils import Vector

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

VARIANTS = ("rover", "cargo", "hab")
STATES = {"rover": 3, "cargo": 3, "hab": 4}
# state 0 = chassis, the rover's 1 = hull shells (no glass, doors or details), the hab's 1 = floor and wall frame and 2 =
# shell panels; anything not listed shows only in the last state (the finished vehicle's body mesh)
STAGES = {
    "rover": {**dict.fromkeys(("RVB_Keel", "RVB_Frame", "RVB_Equipment", "RVB_Skid"), 0),
              **dict.fromkeys(("RVB_Hull", "RVB_HullLow", "RVB_ArchLiners", "RVB_TailPodL", "RVB_TailPodR", "RVB_Bumper",
                               "RVB_FrontBumper", "RVB_Roof", "RVB_Plates", "RVB_PlatesLow",
                               "RVB_HitchMount"), 1)},   # the hitch mount hangs from the rear bumper
    "cargo": {**dict.fromkeys(("TRL_Drawbar", "TRL_Truss", "TRL_Lunette", "TRL_Jack", "TRL_JackBracket", "TRL_JackCrank",
                               "TRL_ShockMounts"), 0),
              **dict.fromkeys(("TRL_Hull", "TRL_RearStep"), 1)},
    "hab": {**dict.fromkeys(("HAB_Truss", "HAB_TrussPlates", "HAB_TrussYoke", "HAB_Drawbar", "HAB_Lunette", "HAB_Tongue",
                             "HAB_TongueStruts", "HAB_Jack", "HAB_JackBracket", "HAB_JackCrank", "HAB_ShockMounts"), 0),
            **dict.fromkeys(("HAB_Hull", "HAB_RearStep", "HAB_LegSleeves"), 1),
            **dict.fromkeys(("HAB_Shell", "HAB_Trim", "HAB_FrontTrim", "HAB_RearTrim", "HAB_RoofTrim", "HAB_Hatch"), 2)},
}


BODY = {"rover": ("GEO_RoverBody",), "cargo": ("GEO_TrailerCargo",), "hab": ("GEO_TrailerHab",)}


def body_parts(variant):
    out = []
    for cn in BODY[variant] + (f"GEO_Stage_{variant}",):
        col = bpy.data.collections.get(cn)
        out += [o for o in (col.objects if col else []) if o.type == "MESH" and "role" in o]
    return out


def tag(variant):
    """Give every body part its "stage" (STAGES, else the last state); scaffold parts are tagged where they are made."""
    last = STATES[variant] - 1
    for cn in BODY[variant]:
        for o in bpy.data.collections[cn].objects:
            if o.type == "MESH" and "role" in o:
                o["stage"] = STAGES[variant].get(o.name, last)


def prepare(variant):
    """Build the vehicle's model (and the rover's running gear, which every variant borrows) and tag it."""
    import blender_rover_wheels as rw
    if variant == "rover":
        import blender_rover_model as m
        m.build()
    else:
        import blender_trailer_model as g
        g.build(variant)
    rw.build()
    scaffold(variant)
    return list(BODY[variant])


def scaffold(variant):
    """After the vehicle's model and the rover's running gear are built: tag the parts, and make the stage-only parts
    (the posed running gear, the trailers' chassis, the trestles, the hab's wall frame). The exporters call this."""
    tag(variant)
    stage_collection(variant)
    posed_gear(variant)
    if variant != "rover":
        trailer_chassis(variant)
    trestles(variant)
    if variant == "hab":
        hab_wall_frame()


TRESTLE_Z = {"rover": (0.40, -2.34), "cargo": (0.85, -0.85), "hab": (0.85, -0.85)}   # Unity z: between the axles
TRESTLE = dict(half_w=0.50, depth=0.25, beam=0.08, leg=0.07, foot=(0.16, 0.03))     # top beam |x| <= half_w


def _space(variant):
    if variant == "rover":
        import blender_rover_model as m
        return m.U
    import blender_trailer_model as g
    return g.T


def stage_collection(variant):
    """GEO_Stage_<variant>, emptied, and every other variant's stage copies removed (object names are global: stale
    copies would push this variant's to .001 names)."""
    import blender_trailer_model as g
    for o in [o for o in bpy.data.objects if o.name.startswith("STG_")]:
        bpy.data.objects.remove(o, do_unlink=True)
    return g.collection(f"GEO_Stage_{variant}")


def _copy(src, name, col, stage, until=None):
    o = bpy.data.objects[src]
    cp = o.copy()
    cp.data = o.data
    cp.name = name
    cp.parent = None
    for k in list(cp.keys()):
        del cp[k]
    cp["role"], cp["stage"] = o["role"], stage
    if until is not None:
        cp["until"] = until
    col.objects.link(cp)
    return cp


def posed_gear(variant):
    """Copies of the rover's tyre, rim, arm and shock on every wheel of the vehicle, at rest (as blender_rover_wheels'
    previews and render_upgrade_views._place_trailer_wheels place them), stage 0: the chassis stands on its wheels."""
    import blender_rover_model as m
    import blender_rover_wheels as rw
    from mathutils import Matrix
    col = bpy.data.collections[f"GEO_Stage_{variant}"]
    S = _space(variant)
    zf = next(z for n, z, _ in m.AXLES if n == "Front")
    if variant == "rover":
        axles = [z for _, z, _ in m.AXLES]
    else:
        import blender_build_trailer as bt
        axles = [z for _, z in bt.AXLES]
    bpy.context.view_layer.update()
    for k, z in enumerate(axles):
        for side, sx in (("L", -1), ("R", 1)):
            shift = Matrix.Translation(S(0, 0, z) - m.U(0, 0, zf))
            for src in ("Tyre" + side, "TyreRim" + side, "Arm" + side):
                cp = _copy(src, f"STG_{src}_{k}", col, 0)
                cp.matrix_world = shift @ bpy.data.objects[src].matrix_world
            top, bot = S(*m.shock_top(sx, z)), S(*m.shock_bottom(sx, z))
            axis = (bot - top).normalized()
            for src, at, to in (("ShockBody", top, bot), ("ShockRod", bot, top),
                                ("ShockSpring", top + axis * rw.SPRING_AT[0], bot)):
                rw._aim(_copy(src, f"STG_{src}{side}_{k}", col, 0), at, to, Vector((1, 0, 0)))


TRLF = dict(rail_x=0.45, rail=0.12, rail_h=(0.80, 0.98), z_rear=-2.60, z_front=2.54, mount_h=1.25, mount_x=0.72,
            beam=0.10, head_h=(1.13, 1.23), head_x=0.66)   # z_front: the truss's rear ends (h 1.16 .. 1.23, |x| 0.59 .. 0.65)


def trailer_chassis(variant):
    """The trailers' chassis (state 0 only: their keel and frame are inside the one hull part, TRL_Hull / HAB_Hull,
    which comes at state 1): a ladder frame of two rails (the arms' pivots at |x| 0.53 on their outer faces) from the
    tail to a headstock the truss bolts to, crossmembers at the ends, under each axle and under the trestles, and a beam under the shock
    mounts at each axle on two posts. Gunmetal, inside the hull's volume (keel 0.56 .. deck 1.32)."""
    import blender_trailer_model as g
    import blender_build_trailer as bt
    F = TRLF
    col = bpy.data.collections[f"GEO_Stage_{variant}"]
    x, w, (h0, h1), b = F["rail_x"], F["rail"] / 2, F["rail_h"], F["beam"] / 2
    z_front = F["z_front"]
    p = g.Part("STG_Chassis", "gunmetal")
    for s in (-1, 1):
        g.box(p, s * x - w, s * x + w, h0, h1, F["z_rear"], z_front)
    for z in [F["z_rear"] + b, z_front - b] + [z for _, z in bt.AXLES] + list(TRESTLE_Z[variant]):
        g.box(p, -x + w, x - w, h0, h1 - 0.03, z - b, z + b)
    for _, z in bt.AXLES:
        g.box(p, -F["mount_x"], F["mount_x"], F["mount_h"] - 0.08, F["mount_h"], z - b, z + b)
        for s in (-1, 1):
            g.box(p, s * x - w, s * x + w, h1, F["mount_h"] - 0.08, z - b, z + b)
    (k0, k1), kx = F["head_h"], F["head_x"]                           # the headstock the truss bolts to, on two posts
    g.box(p, -kx, kx, k0, k1, z_front - 2 * b, z_front)
    for s in (-1, 1):
        g.box(p, s * x - w, s * x + w, h1, k0, z_front - 2 * b, z_front)
    ob = g.finish(p, col)                                             # no bevel: the stage meshes' triangle budget
    ob["stage"], ob["until"] = 0, 0


def _underside(variant, z):
    """Unity h of the lowest stage-0 chassis surface over (0, z): a ray up from the ground."""
    from mathutils.bvhtree import BVHTree
    deps = bpy.context.evaluated_depsgraph_get()
    vs, ps = [], []
    for o in body_parts(variant):
        if o["stage"] != 0 or o.name.startswith(("STG_Tyre", "STG_Arm", "STG_Shock", "STG_Trestle")):
            continue
        e = o.evaluated_get(deps)
        me = e.to_mesh()
        base = len(vs)
        vs += [o.matrix_world @ v.co for v in me.vertices]
        ps += [tuple(base + i for i in p.vertices) for p in me.polygons]
        e.to_mesh_clear()
    o = _space(variant)(0, 0, z)
    hit, _, _, _ = BVHTree.FromPolygons(vs, ps).ray_cast(Vector((o.x, o.y, 0.001)), Vector((0, 0, 1)), 5.0)
    if hit is None:
        raise ValueError(f"{variant}: no chassis over z {z} for a trestle (move it in TRESTLE_Z)")
    return hit.z


def trestles(variant):
    """Two shop trestles (orange, scaffold: every state but the last) under the chassis at TRESTLE_Z: a top beam
    across under the keel or truss, two splayed legs, foot pads on the ground."""
    import blender_trailer_model as g
    from mathutils import Vector as V
    col = bpy.data.collections[f"GEO_Stage_{variant}"]
    S, t = _space(variant), TRESTLE
    last = STATES[variant] - 1
    for k, z in enumerate(TRESTLE_Z[variant]):
        h = _underside(variant, z)
        p = g.Part(f"STG_Trestle{k}", "orange")
        d = t["depth"] / 2
        g.box(p, -t["half_w"], t["half_w"], h - t["beam"], h, z - d, z + d, space=S)
        for s in (-1, 1):
            x_top, x_foot = s * (t["half_w"] - 0.06), s * (t["half_w"] + 0.08)
            for dz in (-d + t["leg"] / 2, d - t["leg"] / 2):          # legs as thin prisms from the foot to the beam
                g.prism(p, [(x_foot - t["leg"] / 2, t["foot"][1]), (x_foot + t["leg"] / 2, t["foot"][1]),
                            (x_top + t["leg"] / 2, h - t["beam"]), (x_top - t["leg"] / 2, h - t["beam"])],
                        z + dz - t["leg"] / 2, z + dz + t["leg"] / 2, lambda u, v, w: S(u, v, w))
            g.box(p, x_foot - t["foot"][0] / 2, x_foot + t["foot"][0] / 2, 0.0, t["foot"][1], z - d, z + d, space=S)
        ob = g.finish(p, col)                                         # no bevel: the stage meshes' triangle budget
        ob["stage"], ob["until"] = 0, last - 1


def state_parts(variant, k):
    """The parts shown at state k (< last): stage <= k and (no until, or until >= k)."""
    return [o for o in body_parts(variant) if o["stage"] <= k and o.get("until", k) >= k]


HABF = dict(beam=0.08, inset=0.03, post_pitch=1.2)    # 8 cm square tube, 3 cm inside the shell's inner face


def hab_wall_frame():
    """The hab's floor and wall frame (state 1 only: HAB_Shell is one boolean'd part, so the frame stands in for it):
    a floor ring on the hull's top (under the floor slab, which comes with the shell), corner and side posts every ~1.2 m, an eaves ring at HAB top, roof bows
    across at each post pair, all inside the shell's inner face by HABF inset. Gunmetal, like the rover's steel."""
    import blender_trailer_model as g
    H, F = g.HAB, HABF
    col = bpy.data.collections["GEO_Stage_hab"]
    b, x = F["beam"], H["half_w"] - F["inset"] - F["beam"] / 2         # beam centre lines
    zl = H["half_l"] - F["inset"] - b / 2
    f0, top = max((bpy.data.objects["HAB_Hull"].matrix_world @ v.co).z for v in bpy.data.objects["HAB_Hull"].data.vertices), H["top"]   # on the hull top
    n = max(2, round(2 * zl / F["post_pitch"]))
    zs = [-zl + 2 * zl * i / n for i in range(n + 1)]
    p = g.Part("HABF_Frame", "gunmetal")
    for h0 in (f0, top - b):                                          # floor ring and eaves ring
        for s in (-1, 1):
            g.box(p, s * x - b / 2, s * x + b / 2, h0, h0 + b, -zl - b / 2, zl + b / 2)
            g.box(p, -x + b / 2, x - b / 2, h0, h0 + b, s * zl - b / 2, s * zl + b / 2)
    for z in zs:                                                      # posts both sides, roof bow across
        for s in (-1, 1):
            g.box(p, s * x - b / 2, s * x + b / 2, f0 + b, top - b, z - b / 2, z + b / 2)
        g.box(p, -x + b / 2, x - b / 2, top - b, top, z - b / 2, z + b / 2)
    ob = g.finish(p, col, bevel=0.004)
    ob["stage"], ob["until"] = 1, 1


COSTS = {   # per state: (entry, entry2, exit) as [prefab, quantity]; "@kit" = the vehicle's own kit (the builder sets it)
    "rover": [(["@kit", 1], None, ["ItemDrill", 1]),
              (["ItemWeldingTorch", 0], ["ItemSteelSheets", 10], ["ItemAngleGrinder", 5]),
              (["ItemPlasticSheets", 10], ["ItemElectronicParts", 4], ["ItemCrowbar", 5])],
    "cargo": [(["@kit", 1], None, ["ItemDrill", 1]),
              (["ItemWeldingTorch", 0], ["ItemSteelSheets", 10], ["ItemAngleGrinder", 5]),
              (["ItemWrench", 0], ["ItemPlasticSheets", 10], ["ItemCrowbar", 5])],
    "hab": [(["@kit", 1], None, ["ItemDrill", 1]),
            (["ItemWeldingTorch", 0], ["ItemSteelSheets", 20], ["ItemAngleGrinder", 5]),
            (["ItemWrench", 0], ["ItemPlasticSheets", 20], ["ItemCrowbar", 5]),
            (["ItemWrench", 0], ["ItemElectronicParts", 8], ["ItemCrowbar", 5])],
}
KITS = {"rover": "ItemKitRoverFrame", "cargo": "ItemKitTrailerCargo", "hab": "ItemKitTrailerHab"}
MESH = {"rover": "RoverStage{}", "cargo": "TrailerStage{}", "hab": "HabStage{}"}
BUDGET = {"rover": (45000, 70000), "cargo": (35000, 45000), "hab": (35000, 50000, 70000)}   # triangles per baked state
_baked = {}


def bake(variant, role_uv=None):
    """Combine each state but the last into one object (bt._combine: per-face roles, palette UVs), pivot = the vehicle
    origin on the ground. Returns [(name, object, materials)]; also kept for the checks."""
    import blender_build_trailer as bt
    import blender_trailer_model as g
    from mathutils import Vector as V
    py = 0.0 if variant == "rover" else (g.TRAILER_Y if variant == "cargo" else g.HAB_ORIGIN_Y)
    role_uv = role_uv or (__import__("blender_build_rover").ROLE_UV)
    out = []
    for k in range(STATES[variant] - 1):
        name = MESH[variant].format(k)
        old = bpy.data.objects.get(name)
        if old:
            bpy.data.objects.remove(old, do_unlink=True)
        ob, mats = bt._combine({o.name: o["role"] for o in state_parts(variant, k)}, name, V((0, py, 0)), role_uv)
        out.append((name, ob, mats))
    _baked[variant] = out
    return out


def baked_triangles(variant):
    return [(name, sum(len(p.vertices) - 2 for p in ob.data.polygons), BUDGET[variant][i])
            for i, (name, ob, _) in enumerate(_baked.get(variant, []))]


def frame_box(variant):
    """Unity-axis (x, h, z) box of the baked states plus the last state (the vehicle's body parts)."""
    pts = [v.co for _, ob, _ in _baked[variant] for v in ob.data.vertices] + _body_points(variant)
    return [min(p[i] for p in pts) for i in (0, 2, 1)], [max(p[i] for p in pts) for i in (0, 2, 1)]


def vehicle_box(variant):
    """The finished vehicle's box: its body, every posed wheel part, and (rover) the bay doors, about its origin."""
    pts = _body_points(variant) + [p - _origin(variant) for o in body_parts(variant) if o.name.startswith("STG_")
                                   and "until" not in o for p in _verts(o)]
    if variant == "rover":
        pts += [p for o in bpy.data.collections["GEO_RoverDoors"].objects if o.type == "MESH" for p in _verts(o)]
    return [min(p[i] for p in pts) for i in (0, 2, 1)], [max(p[i] for p in pts) for i in (0, 2, 1)]


def _origin(variant):
    import blender_trailer_model as g
    from mathutils import Vector as V
    return V((0, 0.0 if variant == "rover" else (g.TRAILER_Y if variant == "cargo" else g.HAB_ORIGIN_Y), 0))


def _verts(o):
    deps = bpy.context.evaluated_depsgraph_get()
    e = o.evaluated_get(deps)
    me = e.to_mesh()
    vs = [o.matrix_world @ v.co for v in me.vertices]
    e.to_mesh_clear()
    return vs


def _body_points(variant):
    return [p - _origin(variant) for cn in BODY[variant] for o in bpy.data.collections[cn].objects
            if o.type == "MESH" and "role" in o for p in _verts(o)]


def export_frame(variant, out_dir, col, stats, body_mesh, body_materials):
    """Bake (into col, hidden), write meshes/<Stage>.rcm, return the json "frame" block (the last state = the body)."""
    import blender_rover_io as io
    states = []
    for k, (name, ob, mats) in enumerate(bake(variant)):
        col.objects.link(ob)
        ob.hide_set(True)
        ob.hide_render = True
        stats[name] = io.export_mesh(ob, os.path.join(out_dir, "meshes", name + ".rcm"))
        states.append({"mesh": name, "materials": mats})
    states.append({"mesh": body_mesh, "materials": body_materials})
    for st, (entry, entry2, exit_) in zip(states, COSTS[variant]):
        st.update(entry=entry, entry2=entry2, exit=exit_)
    return {"thumbnail": KITS[variant], "states": states}
