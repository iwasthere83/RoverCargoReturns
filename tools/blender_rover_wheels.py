"""Tyres and trailing arms of the original Cargo Rover (spec 2026-09-27). Run inside Blender (MCP).
TyreL/TyreR: lugged off-road tyre (r TYRE_R, faces TYRE_IN..TYRE_OUT) with a dark gunmetal rim and bolted hub,
modelled at the front axle with the object origin at the hub (the game moves it from the WheelCollider pose).
ArmL/ArmR: a boxy trailing arm from the keel-wall hinge to the tyre's inner face, with the shock's lower eye boss, a
knuckle and a hub; origin = hinge. ShockBody/ShockRod/ShockSpring: one coil-over set for all six wheels (the rover aims
and scales it), modelled at the origin and hidden.
PRV_* copies at the other axles are previews only (never exported).
"""
import math

import bpy
from mathutils import Matrix, Vector

import blender_rover_model as m
import blender_trailer_model as g

LUGS, LUG_DEPTH = 22, 0.045


def _tyre(side, col):
    s = -1 if side == "L" else 1
    zf = next(z for n, z, _ in m.AXLES if n == "Front")
    hub = m.U(s * m.WHEEL_X, m.WHEEL_H, zf)
    axis = Vector((s, 0, 0))
    half = (m.TYRE_OUT - m.TYRE_IN) / 2
    p = g.Part("Tyre" + side, "black")
    inner_face = m.U(s * m.TYRE_IN, m.WHEEL_H, zf)
    g.cylinder(p, inner_face + axis * 0.03, axis, m.TYRE_R - LUG_DEPTH, 2 * half - 0.06, seg=36, smooth=True)   # carcass
    g.torus(p, inner_face + axis * 0.07, axis, m.TYRE_R - 0.10, 0.07, seg=36, seg2=8)                          # sidewall bulges,
    g.torus(p, inner_face + axis * (2 * half - 0.07), axis, m.TYRE_R - 0.10, 0.07, seg=36, seg2=8)             # flush with the faces
    top = math.sqrt(m.TYRE_R ** 2 - 0.07 ** 2)                      # lug corners on r TYRE_R: the mesh stays inside the checks' tyre
    for k in range(LUGS):                                                                                     # chevron lugs
        a = 2 * math.pi * k / LUGS
        for off, w0, w1 in ((0.0, 0.03, half + 0.05), (math.pi / LUGS, half - 0.05, 2 * half - 0.03)):
            aa = a + off
            rot = Matrix.Rotation(aa, 3, axis)
            c = inner_face + axis * ((w0 + w1) / 2)
            up = rot @ Vector((0, 0, 1))
            tang = rot @ Vector((0, 1, 0))
            corners = []
            for du in (-0.07, 0.07):
                for dw in (-(w1 - w0) / 2, (w1 - w0) / 2):
                    for dr in (m.TYRE_R - LUG_DEPTH - 0.01, top):
                        corners.append(c + tang * du + axis * dw + up * dr)
            bm = p.bm
            vs = [bm.verts.new(v) for v in corners]
            for f in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 2, 6, 4), (1, 5, 7, 3), (0, 4, 5, 1), (2, 3, 7, 6)):
                bm.faces.new([vs[i] for i in f])
    tyre = m.finish(p, col)
    tyre.location = (0, 0, 0)
    rim = g.Part("TyreRim" + side, "gunmetal")
    outer = m.U(s * (m.TYRE_OUT - 0.02), m.WHEEL_H, zf)
    g.cylinder(rim, outer - axis * 0.10, axis, 0.40, 0.10, seg=24, smooth=False)                              # dish
    g.cylinder(rim, outer - axis * 0.02, axis, 0.13, 0.04, seg=16, smooth=False)                              # hub cap,
    for k in range(8):                                                                                        # wheel nuts:
        a = 2 * math.pi * k / 8                                                                               # flush with TYRE_OUT
        c = outer + Matrix.Rotation(a, 3, axis) @ Vector((0, 0.24, 0))
        g.cylinder(rim, c - axis * 0.005, axis, 0.022, 0.025, seg=6, smooth=False)
    rim_ob = m.finish(rim, col, bevel=0.006, bevel_segments=1)       # 2 segments: 1784 rim tris, tyre over its 3000
    # object origins at the hub: move the geometry so the origin is the hub, then place the object there
    for ob in (tyre, rim_ob):
        ob.data.transform(Matrix.Translation(-hub))
        ob.location = hub
    rim_ob.parent = tyre
    rim_ob.matrix_parent_inverse = Matrix.Translation(-hub)     # tyre's world = translation(hub); its matrix_world is stale here
    return tyre


def _arm(side, col):
    s = -1 if side == "L" else 1
    zf = next(z for n, z, _ in m.AXLES if n == "Front")
    hinge = m.U(s * m.ARM_PIVOT_X, m.ARM_PIVOT_H, zf + m.ARM_REACH_Z)
    p = g.Part("Arm" + side, "charcoal")
    bm = p.bm
    rings = []
    for t in (0.0, 0.5, 1.0):                                       # tapered box section, hinge to stub axle
        (x, h, z), w = m.arm_line(s, zf, t)
        rings.append([bm.verts.new(m.U(x + dx, h + dh, z)) for dx, dh in ((-w, -w), (w, -w), (w, w), (-w, w))])
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(4):
            bm.faces.new((r0[k], r0[(k + 1) % 4], r1[(k + 1) % 4], r1[k]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    g.cylinder(p, m.U(s * m.ARM_PIVOT_X, m.ARM_PIVOT_H, zf + m.ARM_REACH_Z) - Vector((s * 0.01, 0, 0)), Vector((s, 0, 0)), 0.07, 0.10, seg=12, smooth=False)   # hinge bush
    (bx, bh, bz), bw = m.arm_line(s, zf, m.SHOCK_T)                 # the shock's lower eye sits on this boss
    xa, xb = sorted((bx - 0.035, bx + 0.035))
    g.box(p, xa, xb, bh + bw - 0.03, bh + bw + m.SHOCK_EYE, bz - 0.03, bz + 0.03, space=m.U)
    (kx, kh, kz), _ = m.arm_line(s, zf, 1.0)                        # the knuckle at the stub axle, the hub into the tyre's face
    xa, xb = sorted((s * (m.TYRE_IN - 0.11), s * (m.TYRE_IN - 0.03)))
    g.box(p, xa, xb, kh - 0.20, kh + 0.20, kz - 0.07, kz + 0.07, space=m.U)
    g.cylinder(p, m.U(s * (m.TYRE_IN - 0.04), kh, kz), Vector((s, 0, 0)), 0.12, 0.09, seg=16, smooth=False)
    arm = m.finish(p, col, bevel=0.01)
    arm.data.transform(Matrix.Translation(-hinge))
    arm.location = hinge
    return arm


SHOCK_BODY_L, SHOCK_ROD_L, SHOCK_R = 0.18, 0.20, 0.058   # damper body and rod lengths from their eyes; the widest radius
SPRING_AT, SPRING_TURNS = (0.035, 0.055), 5              # the spring's seats from the upper and lower eye; its coils


def _helix(p, R, r, L, turns, steps=12):
    """A coil along Blender +y (Unity +z) from 0 to L: radius R, wire r."""
    n = int(turns * steps)
    pts = [Vector((R * math.cos(2 * math.pi * k / steps), L * k / n, R * math.sin(2 * math.pi * k / steps))) for k in range(n + 1)]
    g.tube(p, pts, r, seg=6)


def _shock_parts(col):
    """ShockBody (upper eye, damper body, upper spring seat), ShockRod (lower eye, rod, lower seat) and ShockSpring (the
    coil at its rest length), modelled at the origin along Unity +z (Blender +y), the eyes along Unity +y (Blender +z):
    the rover aims body and rod at each other and scales the spring between the seats (CargoRover.AnimateShocks). One
    set for all six wheels; hidden here (the PRV_ copies show them in place)."""
    body, rod = g.Part("ShockBody", "gunmetal"), g.Part("ShockRod", "steel")
    spring = g.Part("ShockSpring", "orange", smooth=True)
    ax, eye = Vector((0, 1, 0)), Vector((0, 0, 1))
    for p in (body, rod):
        g.cylinder(p, Vector((0, 0, -0.03)), eye, 0.025, 0.06, seg=10, smooth=False)                 # eye
    g.cylinder(body, Vector((0, 0.015, 0)), ax, 0.030, SHOCK_BODY_L - 0.015, seg=14, smooth=False)   # damper body
    g.cylinder(body, Vector((0, SPRING_AT[0] - 0.01, 0)), ax, SHOCK_R, 0.01, seg=16, smooth=False)   # upper seat
    g.cylinder(rod, Vector((0, 0.015, 0)), ax, 0.011, SHOCK_ROD_L - 0.015, seg=10, smooth=False)     # rod
    g.cylinder(rod, Vector((0, SPRING_AT[1] - 0.01, 0)), ax, SHOCK_R, 0.01, seg=16, smooth=False)    # lower seat
    rest = (Vector(m.shock_top(1, 0.0)) - Vector(m.shock_bottom(1, 0.0))).length - SPRING_AT[0] - SPRING_AT[1]
    _helix(spring, SHOCK_R - 0.010, 0.010, rest, SPRING_TURNS)
    obs = [m.finish(body, col, bevel=0.004), m.finish(rod, col, bevel=0.003), m.finish(spring, col)]
    for ob in obs:
        ob.hide_set(True)
    return obs


def _aim(ob, at, to, lateral):
    """Place a shock part (modelled along Blender +y, eyes along +z) at `at`, pointing to `to`, its +z along `lateral`."""
    fy = (to - at).normalized()
    fz = (lateral - fy * lateral.dot(fy)).normalized()
    ob.matrix_world = Matrix.Translation(at) @ Matrix((fy.cross(fz), fy, fz)).transposed().to_4x4()


def build():
    col = g.collection("GEO_RoverWheels")
    out = [_tyre(sd, col) for sd in ("L", "R")] + [_arm(sd, col) for sd in ("L", "R")] + _shock_parts(col)
    zf = next(z for n, z, _ in m.AXLES if n == "Front")
    bpy.context.view_layer.update()                                 # world matrices of the new objects
    for name, z, _ in m.AXLES:                                      # previews at the other axles
        if name == "Front":
            continue
        for sd in ("L", "R"):
            for src in ("Tyre" + sd, "TyreRim" + sd, "Arm" + sd):
                o = bpy.data.objects[src]
                cp = o.copy()
                cp.data = o.data
                cp.name = f"PRV_{src}_{name}"
                del cp["role"]
                cp.parent = None
                cp.location = o.matrix_world.translation + Vector((0, z - zf, 0))
                col.objects.link(cp)
    lateral = Vector((1, 0, 0))                                     # the rover's right, as CargoRover's up hint
    for name, z, _ in m.AXLES:                                      # shock previews at every wheel, at rest
        for sd, s in (("L", -1), ("R", 1)):
            top, bot = m.U(*m.shock_top(s, z)), m.U(*m.shock_bottom(s, z))
            axis = (bot - top).normalized()
            for src, at, to in (("ShockBody", top, bot), ("ShockRod", bot, top), ("ShockSpring", top + axis * SPRING_AT[0], bot)):
                o = bpy.data.objects[src]
                cp = o.copy()
                cp.data = o.data
                cp.name = f"PRV_{src}{sd}_{name}"
                del cp["role"]
                col.objects.link(cp)
                _aim(cp, at, to, lateral)
    return [o.name for o in out]
