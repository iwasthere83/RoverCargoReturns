"""Generate the original Cargo Rover (spec docs/superpowers/specs/2026-09-27-original-rover-design.md) as Blender
geometry. Run inside Blender (MCP): everything is built from the numbers below; re-run to rebuild.

Rover-local Unity axes (x right, h up, z forward), origin on the ground under the rover, at the Blender origin (where
REF_RoverCargo, the 2020 reference, also sits: render_views hides it). Collections:
  GEO_RoverBody    body parts (one exported mesh, a material slot per palette role)
  GEO_RoverGlass   cab glass (exported alone, the game's WindowGlass)
  GEO_RoverDoors   bay door leaves, one object per bay (exported per bay for sub-project 3)
  GEO_RoverWheels  TyreL/R, ArmL/R (tools/blender_rover_wheels.py)
  ANC_Rover        empties ANC_<name>: slot anchors, seats, exits, cameras, lights, hitch, switches (custom props
                   unity_pos / unity_rot = exactly what rover.json gets)
Design rules as the hab: faceted planes with chamfered edges, joints buried EMB inside each other, never coplanar.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)
import blender_trailer_model as g  # noqa: E402  mesh helpers (Part, prism, box, finish, ...)

# ---------------------------------------------------------------- measured (art .blend)
TYRE_R, TYRE_IN, TYRE_OUT = 0.73, 1.114, 1.801      # visual tyre radius and faces |x| (REF_RoverCargo)
WHEEL_X, WHEEL_H = 1.4574, 0.6446                   # hub centre at rest (WheelCollider position, as the trailer)
CRATE_L, CRATE_H, CRATE_W = 1.839, 0.553, 0.730     # DynamicCrate mesh; long axis = its own x
TANK_D, TANK_H, TANK_UP = 0.813, 1.177, 0.573       # DynamicGasCanisterEmpty; origin 0.573 above its base
# ---------------------------------------------------------------- driving (carried over)
STEER_DEG, BUMP = 40.0, 0.35
AXLES = (("Front", 2.265, 1), ("Mid", -1.50, 0), ("Rear", -3.18, 2))   # (name, z, Wheel.Mode: 1 Normal, 0 None, 2 Inverted)
ARM_PIVOT_X, ARM_PIVOT_H, ARM_REACH_Z = 0.53, 0.904, 0.45           # trailing-arm hinge on the keel wall, ahead of the hub
# ---------------------------------------------------------------- layout v3 (spec, corrected in Task 1)
NOSE_Z = 3.05
CAB_Z0, CAB_ROOF, CAB_X = 0.85, 3.16, 1.35       # one hull (2026-09-28): the cab has the storage module's roof and side plane
CAB_LOW, CAB_STEP_Z = 1.40, 1.73          # the cab floor under the seats (1.46 = CAB_LOW + 0.06); the step to the footwell
WINDSCREEN = ((NOSE_Z - (CAB_ROOF - 1.95) * 1.10 / 1.13, CAB_ROOF), (NOSE_Z, 1.95))   # the 2026-09-27 slope and lower edge, up to the one roof
SLOT_Z = (0.25, 0.85)                               # the slot panel's span between bay 1 and the cab
SLOT_H = (1.88, 2.95)                               # slot-panel recess floor and ceiling
RECESS_Z = (0.30, 0.83)                             # the recesses along z
SLOT_BACK_X = 1.10                                  # slot-panel recess back wall |x| (25 cm deep)
RACK = dict(x=0.40, h=(1.92, 2.91), z=(0.31, 0.59))   # left: the canister rack's pocket behind the recess (back wall |x|)
RACK_FACE, PORT_R = 0.10, 0.112     # the rack's face: 10 cm out of the recess back wall, a round port 6 mm round each canister
LABEL_X = (SLOT_BACK_X + RACK_FACE - g.EMB, 1.265)   # the tank labels' blocks (|x|): from the rack face out under the recess ceiling, which
                                    # the side's 45 deg lean cuts off at |x| 1.275 (the user's report: at the mouth the WASTE
                                    # sign hung in the air)
FRONT_LAMPS = dict(head_tilt=8.0, bar_x=0.32, bar_tilt=-1.0)   # the headlights aim 8 deg down, the roof light bar's pair 1 deg up
                                    # (their cones clear the push bar and the cab roof: blender_rover_checks.light_spill)
DOCK = dict(top=2.030, riser=0.060, cup=0.045)   # right: the battery dock's top (5.5 mm under the batteries' terminals),
                                    # the riser behind them, the cups round their feet
# slot items measured from the game's meshes (RoverRules.ItemBody): (centre offset from the origin, size), item frame
ITEM = {"canister": ((0.0, -0.243, 0.0), (0.208, 0.874, 0.222)), "filter": ((0.0, -0.0475, 0.0), (0.188, 0.287, 0.188)),
        "chip": ((0.0, 0.0, 0.0), (0.179, 0.251, 0.111)), "battery": ((0.0, 0.0, 0.0), (0.098, 0.169, 0.096))}
# the round items' measured profiles along their own y, (y0, y1, radius): the canister's body to its valve end; the
# filter's spigot (its connector, pointing down), then its body
ITEM_ROUND = {"canister": ((-0.68, 0.194, 0.107),), "filter": ((-0.191, -0.12, 0.027), (-0.12, 0.096, 0.095))}
# slot anchor -> (item, Unity position of its origin, Unity rotation). Left: the canisters lie across in the rack (valve
# end in, the bottom 4 cm inside the side face; Air1 at the bottom, as the 2020 rover), the filters stand in front of
# it; right: the chip above the three batteries. Items stand 3 cm or more inside the recess walls.
_CAN = (0.0, 0.0, -90.0)                            # local +y (the valve end) points inboard (+x) on the left
PANEL = {
    "Air1": ("canister", (-0.630, 2.055, 0.45), _CAN), "Air2": ("canister", (-0.630, 2.295, 0.45), _CAN),
    "Air3": ("canister", (-0.630, 2.535, 0.45), _CAN), "Waste": ("canister", (-0.630, 2.775, 0.45), _CAN),
    "Filter1": ("filter", (-1.225, 2.6675, 0.71), (0.0, 0.0, 0.0)), "Filter2": ("filter", (-1.225, 2.2575, 0.71), (0.0, 0.0, 0.0)),
    "Chip": ("chip", (1.225, 2.62, 0.565), (0.0, 90.0, 0.0)),        # face out: its thin side (local z) across
    "Battery1": ("battery", (1.225, 2.12, 0.415), (0.0, 0.0, 0.0)), "Battery2": ("battery", (1.225, 2.12, 0.565), (0.0, 0.0, 0.0)),
    "Battery3": ("battery", (1.225, 2.12, 0.715), (0.0, 0.0, 0.0)),
}
DOOR_HINGE = (0.02, 0.01)                           # the bay doors' hinge: 2 cm outside the side face, 1 cm above the opening
DOOR_OPEN_DEG = 100.0                               # RoverRules.DoorOpenDeg
BAYS = (("1", 0.25, -1.65), ("2", -1.75, -3.65))   # (name, front z, rear z)
BAY_IN_X, BAY_OUT_X = 0.45, 1.31                   # bay inner wall |x|; inside face of the closed door
UPPER_X = 1.35
ARCH_H, BAY_FLOOR, BAY_TOP, MODULE_ROOF = 1.76, 1.82, 3.04, 3.16   # arch = tyre top 1.375 + BUMP 0.35 + 3 cm; roof: side face flat to 3.08 (bay frames)
TAIL_Z0, REAR_Z, TAIL_TOP = -3.67, -4.10, 3.25     # the tail pods start 2 cm behind bay 2's rear wall (no coplanar faces)
BUMPER = dict(x=1.05, h=(0.70, 1.30), z=(-3.98, -4.12))   # behind the rear tyres (steered: -3.96), 2 cm proud of the tail
HITCH = (0.0, 0.80, -4.90)       # 0.80 m behind the tail: the hab tongue (deck, A-frame, cradle tanks) clears to 70 deg
BELLY, KEEL_X = 0.60, 0.53
DIP = dict(x=0.60, wall=0.06, floor=1.40, z=(1.695, 2.45))     # footwell dip: inside |x| < 0.60, floor 0.19 under the seat anchor
DASH = ((2.06, 1.80), (2.62, 1.80), (2.62, 2.06), (2.36, 2.10))  # (z, h): the screen slope runs DASH[0] -> DASH[3] at 45 deg
SCREEN = (0.60, 0.34)                                            # screen face width, height (RoverDashboard: 1200 x 680 at 0.5 mm)
SEAT_Z = 1.45                                                    # seat anchors and camera points along z (0.4 m clear of the walls)
SEAT_X = 0.43                                                    # seats and cameras |x|
SEAT_BACK = ((-0.10, 0.0), (-0.15, 0.69))   # the backrest's front edge (z, h from the seat anchor's rest on the cushion): 4 deg back
POSE = dict(recline=20.0, back=0.145)       # the game's Seated pose (the 2020 seat was built round it: its backrest 18-21 deg
                                            # back, the hips 14.5 cm in front of it): its back 20 deg back, 14.5 cm behind the hips
ENTRY_IN = 1.10                             # the seats' click boxes reach into the cab to here (RoverRules.ClickBoxSize)
SEAT_PITCH, SEAT_FWD = 16.0, 0.04           # the seat anchors pitched and moved forward: the pose's back on the backrest
# ---------------------------------------------------------------- exterior rework (spec 2026-09-28-original-rover-exterior-rework-design.md)
HULL_BOT = 1.30                     # the one hull's lower edge (side skirt, nose, tail)
MOD_LEAN, CAB_LEAN = (2.875, 45.0), (2.30, 35.0)   # (crease h, deg): the storage module upright to 2.875 (the top crates'
                                    # corners 2 cm inside the leaves) then chamfered to the roof; the cab leaning from its sill
MOD_CREASE, CAB_CREASE, CAB_STEP_AT = MOD_LEAN[0], CAB_LEAN[0], 0.90   # the cab's lean runs forward of the step at z 0.90
TUCK = (1.78, 30.0)                 # (belt h, deg): the lower hull tucks in below the belt line to the lower edge
LOWER_ROLE = "gunmetal"             # the lower hull (below the belt line); the upper is silver
CORNER = ((1.35, 2.05), (0.72, 3.05))   # the cab's front corner facets (|x|, z): the wedge nose, |x| 0.72 at the windscreen's foot
WINDSCREEN_HALF = 0.56              # the windscreen pane's half-width (the pillars inside the corner facets)
BAND = (3.10, 1.78)                 # the nose's light band foot (z, h); its top is the windscreen's lower edge
SNOUT = (3.30, HULL_BOT)            # the snout's foot, leaning forward to the front bumper
ARCH_IN, ARCH_REACH = 0.70, 0.80    # wheel arches cut from |x| 0.70 out (a steered tyre reaches in that far), ARCH_REACH past the hubs
FRAME_X, FRAME_H = 0.565, (1.05, HULL_BOT)   # frame rails on the keel walls, 1.7 cm inboard of the shocks' springs
SHOCK_T, SHOCK_EYE, SHOCK_TOP_H = 0.18, 0.015, 1.24   # the shock's lower eye SHOCK_T along the arm, SHOCK_EYE over its top
                                    # face (a tyre steered 40 deg sweeps in to |x| 0.73 further out); the upper eye under
                                    # a cross member, straight above at rest
HITCH_BOX = (0.22, 0.62, 0.92, 0.16)   # the receiver box: half width, bottom, top, depth behind the rear bumper
REAR_VENTS = ((-1.00, -0.16, 6), (0.16, 0.46, 3))   # the tail pods' vents (x from, to, slats) on their rear faces, 4 cm
REAR_VENT_H = (2.30, 3.05)                           # off the pods' inner edges: the left one's top outer corner 10 cm
                                                     # inside the pod's lean (|x| 1.105 at 3.05), the right one beside the ladder
WORK_LAMPS = dict(side_z=(-0.70, -2.70), side_x=1.40, rear_x=1.00, rear_z=-4.30, down=35.0)   # roof-rack work lights,
                                    # clear of the body (no ray of their cones meets it): side lamps over the bays on brackets
                                    # past the sides, rear lamps at the rack's rear corners behind the tail pods; one light at
                                    # each lamp's lens, aimed down
RACK_REAR = -4.24                   # the roof rack's side rails run back over the tail pods to here
# ---------------------------------------------------------------- palette roles (preview colours; export: ROLE_UV)
ROLE_COLOUR = {"silver": (0.906, 0.906, 0.906, 1), "steel": (0.76, 0.78, 0.78, 1), "charcoal": (0.24, 0.235, 0.24, 1), "gunmetal": (0.275, 0.275, 0.275, 1),
               "orange": (0.78, 0.25, 0.0, 1)}
g.ROLE_COLOUR.update(ROLE_COLOUR)


def finish(p, col, bevel=0.0, bevel_segments=2):
    """g.finish, then make a closed part's faces point outward: recalc_face_normals guessed one mirrored tail pod
    inside-out (negative signed volume), which the game would cull as invisible from outside."""
    ob = g.finish(p, col, bevel=bevel, bevel_segments=bevel_segments)
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    if bm.faces and all(len(e.link_faces) == 2 for e in bm.edges) and bm.calc_volume(signed=True) < 0:
        bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
        bm.to_mesh(ob.data)
    bm.free()
    return ob


def U(x, h, z):
    """Unity rover-local point -> Blender (the rover sits at the Blender origin)."""
    return Vector((x, z, h))


def unity_rot(rot):
    """Unity Euler degrees (applied Z, then X, then Y) as a matrix on Unity (x, h, z) vectors."""
    rx, ry, rz = (math.radians(a) for a in rot)
    mx = Matrix(((1, 0, 0), (0, math.cos(rx), -math.sin(rx)), (0, math.sin(rx), math.cos(rx))))
    my = Matrix(((math.cos(ry), 0, math.sin(ry)), (0, 1, 0), (-math.sin(ry), 0, math.cos(ry))))
    mz = Matrix(((math.cos(rz), -math.sin(rz), 0), (math.sin(rz), math.cos(rz), 0), (0, 0, 1)))
    return my @ mx @ mz


def item_box(name):
    """A PANEL item's measured body as a Unity box (lo, hi) at its anchor."""
    item, pos, rot = PANEL[name]
    (cx, cy, cz), (sx, sy, sz) = ITEM[item]
    R = unity_rot(rot)
    pts = [Vector(pos) + R @ Vector((cx + i * sx / 2, cy + j * sy / 2, cz + k * sz / 2)) for i in (-1, 1) for j in (-1, 1) for k in (-1, 1)]
    return tuple(min(p[a] for p in pts) for a in range(3)), tuple(max(p[a] for p in pts) for a in range(3))


def item_contains(name, q, pad=0.0):
    """Whether a Unity point is inside a PANEL item's measured body (its round profile, else its box), grown by pad."""
    item, pos, rot = PANEL[name]
    p = unity_rot(rot).transposed() @ (Vector(q) - Vector(pos))
    if item in ITEM_ROUND:
        return any(y0 - pad < p.y < y1 + pad and math.hypot(p.x, p.z) < r + pad for y0, y1, r in ITEM_ROUND[item])
    c, size = ITEM[item]
    return all(abs(p[k] - c[k]) < size[k] / 2 + pad for k in range(3))


def _ud(x, h, z):
    return Vector((x, z, h))


def cut(bm, co, no):
    """Remove everything on the outer side of a plane (Unity point, outward normal) and cap the hole."""
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=U(*co), plane_no=_ud(*no).normalized(), clear_outer=True)
    bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)


def plane_xz(a, b, out_hint):
    """Vertical plane through two (x, z) points, normal towards out_hint (x, z)."""
    (x1, z1), (x2, z2) = a, b
    n = Vector((z2 - z1, -(x2 - x1)))
    if n.dot(Vector(out_hint)) < 0:
        n = -n
    return (x1, 0.0, z1), (n.x, 0.0, n.y)


def plane_xh(a, b, out_hint):
    """Plane along z through two (x, h) points, normal towards out_hint (x, h)."""
    (x1, h1), (x2, h2) = a, b
    n = Vector((h2 - h1, -(x2 - x1)))
    if n.dot(Vector(out_hint)) < 0:
        n = -n
    return (x1, h1, 0.0), (n.x, n.y, 0.0)


def plane_zh(a, b, out_hint):
    """Plane across x through two (z, h) points, normal towards out_hint (z, h)."""
    (z1, h1), (z2, h2) = a, b
    n = Vector((h2 - h1, -(z2 - z1)))
    if n.dot(Vector(out_hint)) < 0:
        n = -n
    return (0.0, h1, z1), (0.0, n.y, n.x)


def faceted(name, role, profile_zh, x0, x1, planes, bevel=0.02):
    """Solid: a side profile [(z, h), ...] extruded over x0..x1, cut by planes [(point, normal)], sharp edges
    chamfered (the hab's recipe). Returns the Part (not finished)."""
    p = g.Part(name, role)
    g.prism(p, profile_zh, x0, x1, lambda z, h, x: U(x, h, z))
    bm = p.bm
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    for co, no in planes:
        cut(bm, co, no)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.normal_update()
    if bevel:
        # clamp_overlap zeroes whole bevels next to the small chamfer facets (measured: a tail pod lost all of it);
        # unclamped, the few corners where two bevels meet are dissolved instead of left as zero-area faces
        sharp = [e for e in bm.edges if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > math.radians(10)]
        bmesh.ops.bevel(bm, geom=sharp, offset=bevel, segments=1, profile=0.5, affect="EDGES", clamp_overlap=False)
        bmesh.ops.dissolve_degenerate(bm, dist=1e-4, edges=bm.edges[:])
    return p


def mirrored(planes_right):
    """Right-side planes plus their mirrors in x."""
    out = list(planes_right)
    for (x, h, z), (nx, nh, nz) in planes_right:
        if abs(nx) > 1e-6:
            out.append(((-x, h, z), (-nx, nh, nz)))
    return out


def cutter(col, name, lo, hi):
    """Hidden box object (Unity lo/hi corners) used as a boolean DIFFERENCE cutter; not exported (no role)."""
    old = bpy.data.objects.get(name)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    p = g.Part(name, "charcoal")
    g.box(p, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], space=U)
    ob = finish(p, col)
    del ob["role"]
    ob.hide_set(True)
    ob.hide_render = True
    ob.display_type = "WIRE"
    return ob


def boolean(target, cutters, label):
    """DIFFERENCE modifiers. Blender's MANIFOLD solver (4.5+): EXACT returned an empty keel and holed pods for the
    cab-room cut (measured), MANIFOLD removed exactly the expected volume with closed results."""
    solvers = {i.identifier for i in bpy.types.BooleanModifier.bl_rna.properties["solver"].enum_items}
    for k, c in enumerate(cutters):
        mod = target.modifiers.new(f"{label}{k}", "BOOLEAN")
        mod.operation, mod.object = "DIFFERENCE", c
        mod.solver = "MANIFOLD" if "MANIFOLD" in solvers else "EXACT"


# ---------------------------------------------------------------- main volumes
def build_keel(col, out):
    """Central keel under the hull (the arms hinge on its walls at |x| KEEL_X), belly 0.60, its top buried in the hull."""
    z_back, z_front = BUMPER["z"][0], NOSE_Z
    planes = mirrored([plane_xh((KEEL_X, 0.74), (KEEL_X - 0.12, BELLY), (1, -1))]) + [
        plane_zh((z_front - 0.18, BELLY), (z_front, 0.80), (1, -1)),      # nose chin
        plane_zh((z_back + 0.14, BELLY), (z_back, 0.75), (-1, -1))]       # tail chin
    top = HULL_BOT + g.EMB
    out.append(finish(faceted("RVB_Keel", "charcoal", [(z_back, BELLY), (z_front, BELLY), (z_front, top), (z_back, top)], -KEEL_X, KEEL_X, planes), col))


def build_hull(col, out):
    """The one silver hull (spec 2026-09-28): cab and storage on one side plane and one roof, the windscreen, the nose's
    light band and snout, the lower edge at HULL_BOT with its chine, the sides leaning in. The bays, the slot recesses,
    the canister rack, the wheel arches and the cab's lower lean are cut by booleans."""
    (zt, ht), (zb, hb) = WINDSCREEN
    prof = [(REAR_Z, HULL_BOT), (SNOUT[0], HULL_BOT), BAND, (zb, hb), (zt, ht), (REAR_Z, MODULE_ROOF)]
    planes = mirrored([lean_plane(MOD_LEAN), tuck_plane(),
                       plane_xz((CORNER[0][0], CORNER[0][1]), (CORNER[1][0], CORNER[1][1]), (1, 1)),
                       plane_xz((UPPER_X, REAR_Z + 0.18), (UPPER_X - 0.18, REAR_Z), (1, -1))])
    ob = finish(faceted("RVB_Hull", "silver", prof, -UPPER_X, UPPER_X, planes + [((0.0, TUCK[0] - g.EMB, 0.0), (0.0, -1.0, 0.0))]), col)
    low = finish(faceted("RVB_HullLow", LOWER_ROLE, prof, -UPPER_X, UPPER_X, planes + [((0.0, TUCK[0], 0.0), (0.0, 1.0, 0.0))]), col)
    cuts = []
    for s in (1, -1):
        lab = "R" if s > 0 else "L"
        for name, zf, zr in BAYS:
            cuts.append(cutter(col, f"RVX_Bay{lab}{name}", (min(s * BAY_IN_X, s * (UPPER_X + 0.1)), BAY_FLOOR, zr), (max(s * BAY_IN_X, s * (UPPER_X + 0.1)), BAY_TOP, zf)))
        cuts.append(cutter(col, f"RVX_SlotPanel{lab}", (min(s * SLOT_BACK_X, s * (UPPER_X + 0.1)), SLOT_H[0], RECESS_Z[0]), (max(s * SLOT_BACK_X, s * (UPPER_X + 0.1)), SLOT_H[1], RECESS_Z[1])))
        xa, xb = sorted((s * ARCH_IN, s * (UPPER_X + 0.3)))
        for k, (zf, zr) in enumerate(arches()):
            p = g.Part(f"RVX_Arch{lab}{k}", "charcoal")
            g.prism(p, arch_profile(zf, zr), xa, xb, lambda z, h, x: U(x, h, z))
            cuts.append(_hide_cutter(finish(p, col)))
        wedge = [(UPPER_X, CAB_CREASE), (UPPER_X + 0.2, CAB_CREASE), (UPPER_X + 0.2, CAB_ROOF + 0.2), (lean_x(CAB_ROOF + 0.2, CAB_LEAN), CAB_ROOF + 0.2)]
        p = g.Part(f"RVX_CabLean{lab}", "charcoal")                   # the cab's lower lean, forward of the step
        g.prism(p, [(s * x, h) for x, h in wedge], CAB_STEP_AT, NOSE_Z + 0.6, lambda x, h, z: U(x, h, z))
        cuts.append(_hide_cutter(finish(p, col)))
    cuts.append(cutter(col, "RVX_CanisterRack", (-(UPPER_X + 0.1), RACK["h"][0], RACK["z"][0]), (-RACK["x"], RACK["h"][1], RACK["z"][1])))
    boolean(ob, cuts, "Cut")
    boolean(low, cuts, "Cut")
    out += [ob, low]


def tuck_plane():
    """The right side's lower lean-in below the belt line (Unity point, outward normal)."""
    return plane_xh((UPPER_X, TUCK[0]), (UPPER_X - (TUCK[0] - HULL_BOT) * math.tan(math.radians(TUCK[1])), HULL_BOT), (1, -1))


def hull_parts():
    """The hull's objects (the upper and the lower part): cuts through the body go into both."""
    return [bpy.data.objects[n] for n in ("RVB_Hull", "RVB_HullLow")]


def build_arch_liners(col, out):
    """Dark wheel-well liners: a band over each arch's roof (3 mm into the opening, 1 cm into the hull) and a plate on
    the well's inner wall, trimmed 1.2 cm inside the chine, so an arch reads as a well, not as silver behind a tyre."""
    p = g.Part("RVB_ArchLiners", "charcoal")
    for s in (1, -1):
        for zf, zr in arches():
            top = arch_profile(zf, zr)[1:-1]                          # the arch's roof line, front to rear
            band = _offset(top, -0.010) + list(reversed(_offset(top, 0.003)))
            xa, xb = sorted((s * (ARCH_IN - 0.01), s * (UPPER_X - 0.012)))
            g.prism(p, band, xa, xb, lambda z, h, x: U(x, h, z))
            wall = [(top[0][0], HULL_BOT - 0.004)] + top[1:-1] + [(top[-1][0], HULL_BOT - 0.004)]
            xa, xb = sorted((s * (ARCH_IN - 0.01), s * (ARCH_IN + 0.006)))
            g.prism(p, wall, xa, xb, lambda z, h, x: U(x, h, z))
    for co, no in mirrored([inset(tuck_plane(), 0.012)]):
        cut(p.bm, co, no)
    out.append(finish(p, col))


def build_tail(col, out):
    """The rear bumper block under the tail and the two raised tail pods (they lean with the storage module's sides)."""
    rz = REAR_Z
    bz0, bz1 = BUMPER["z"]
    bp = faceted("RVB_Bumper", "charcoal", [(bz1, BUMPER["h"][0]), (bz0, BUMPER["h"][0]), (bz0, HULL_BOT + g.EMB), (bz1, HULL_BOT + g.EMB)],
                 -BUMPER["x"], BUMPER["x"], mirrored([plane_xz((BUMPER["x"], bz1 + 0.08), (BUMPER["x"] - 0.08, bz1), (1, -1))])
                 + [plane_zh((bz1 + 0.10, BUMPER["h"][0]), (bz1, BUMPER["h"][0] + 0.12), (-1, -1)),
                    plane_zh((bz1, HULL_BOT - 0.05), (bz1 + 0.05, HULL_BOT + g.EMB), (-1, 1))])   # top rear: the hab's corner at 70 deg
    out.append(finish(bp, col))
    for s in (1, -1):
        xa, xb = sorted((s * 0.12, s * 1.28))
        pod = faceted(f"RVB_TailPod{'R' if s > 0 else 'L'}", "silver",
                      [(rz - 0.12, 2.10), (TAIL_Z0, 2.10), (TAIL_Z0, TAIL_TOP), (rz - 0.12, TAIL_TOP)], xa, xb,
                      [plane_zh((TAIL_Z0 - 0.10, TAIL_TOP), (TAIL_Z0, TAIL_TOP - 0.10), (1, 1)),
                       plane_zh((rz - 0.12, TAIL_TOP - 0.10), (rz, TAIL_TOP), (-1, 1)),
                       plane_xh((s * 1.28, TAIL_TOP - 0.10), (s * 1.18, TAIL_TOP), (s, 1)),
                       plane_xh((s * (UPPER_X - 0.07), MOD_CREASE), (s * (lean_x(CAB_ROOF, MOD_LEAN) - 0.07), CAB_ROOF), (s, 1))])
        out.append(finish(pod, col))


def slope_h(z):
    """Height of the windscreen slope's outer surface at z."""
    (zt, ht), (zb, hb) = WINDSCREEN
    return hb + (ht - hb) * (zb - z) / (zb - zt)


def cab_planes():
    """The cab's cut planes (both sides): the vertical front corner facets and the lean above the sill. The slope is
    slope_plane(); the room (build_cab_glass) and the dash take them inset."""
    return mirrored([plane_xz((CORNER[0][0], CORNER[0][1]), (CORNER[1][0], CORNER[1][1]), (1, 1)), lean_plane(CAB_LEAN)])


def slope_plane():
    (zt, ht), (zb, hb) = WINDSCREEN
    return plane_zh((zb, hb), (zt, ht), (1, 1))


def inset(plane, d):
    """The same plane moved d inward (against its outward normal)."""
    (x, h, z), (nx, nh, nz) = plane
    n = Vector((nx, nh, nz)).normalized()
    return ((x - n.x * d, h - n.y * d, z - n.z * d), (nx, nh, nz))


def lean_x(h, lean):
    """The side's |x| at height h under a lean (crease h, deg): upright below the crease."""
    return UPPER_X - max(0.0, h - lean[0]) * math.tan(math.radians(lean[1]))


def lean_h(x, lean):
    """The height at which a lean reaches |x| x."""
    return lean[0] + (UPPER_X - x) / math.tan(math.radians(lean[1]))


def lean_v(h, lean):
    """The distance up a lean face from its crease to height h."""
    return (h - lean[0]) / math.cos(math.radians(lean[1]))


def lean_plane(lean):
    """The right side's lean as a cut plane (Unity point, outward normal)."""
    return plane_xh((UPPER_X, lean[0]), (lean_x(CAB_ROOF, lean), CAB_ROOF), (1, 1))


def arches():
    """(front hub z, rear hub z) per wheel arch: the front wheel, the tandem."""
    zf, zm, zr = (a[1] for a in AXLES)
    return [(zf, zf), (zm, zr)]


FRONT_WELL_Z = AXLES[0][1] - ARCH_REACH - 0.03   # the cabin floor steps up over the front wheel wells here (1.435)


def arch_profile(zf, zr):
    """A faceted arch (z, h) over tyres centred zf .. zr: ARCH_REACH past them at the skirt line, ARCH_H at the top."""
    return [(zf + ARCH_REACH, HULL_BOT - 0.3), (zf + ARCH_REACH, HULL_BOT), (zf + 0.62, 1.60), (zf + 0.35, ARCH_H),
            (zr - 0.35, ARCH_H), (zr - 0.62, 1.60), (zr - ARCH_REACH, HULL_BOT), (zr - ARCH_REACH, HULL_BOT - 0.3)]


def arm_line(s, z, t):
    """The trailing arm of side s at axle z: its centre line at t (0 hinge, 1 stub axle) and its half-width there
    (blender_rover_wheels._arm's box section)."""
    a0 = (s * (ARM_PIVOT_X + 0.01), ARM_PIVOT_H, z + ARM_REACH_Z)
    a1 = (s * (TYRE_IN - 0.02), WHEEL_H, z)
    return tuple(a0[k] + (a1[k] - a0[k]) * t for k in range(3)), 0.085 - 0.02 * t


def shock_bottom(s, z):
    """The shock's lower eye at rest, on the arm's top face."""
    (x, h, zz), w = arm_line(s, z, SHOCK_T)
    return (x, h + w + SHOCK_EYE, zz)


def shock_top(s, z):
    """The shock's upper eye, straight above the lower at rest."""
    x, h, zz = shock_bottom(s, z)
    return (x, SHOCK_TOP_H, zz)


def tuck_x(h):
    """The lower hull's |x| at height h (on its tuck below the belt line)."""
    return UPPER_X - max(0.0, TUCK[0] - h) * math.tan(math.radians(TUCK[1]))


def _offset(poly, d):
    """A polyline's points moved d along their vertex normals (+ = left of travel: into an arch travelled front to rear)."""
    out = []
    for i, p in enumerate(poly):
        a, b = Vector(poly[max(i - 1, 0)]), Vector(poly[min(i + 1, len(poly) - 1)])
        t = (b - a).normalized()
        out.append(tuple(Vector(p) + Vector((-t.y, t.x)) * d))
    return out


# ---------------------------------------------------------------- surface maps and details (Task 6)
def pmap(origin, eu, ev, ew):
    """Surface-local (u, v, w) -> Blender point: origin + u*eu + v*ev + w*ew, all Unity (x, h, z)."""
    o, a, b, c = (Vector(t) for t in (origin, eu, ev, ew))
    def f(u, v, w):
        q = o + a * u + b * v + c * w
        return U(q.x, q.y, q.z)
    return f


def side_face(s, x):
    """Side face at |x| = x of side s: u along z (forward on the right side, backward on the left), v = h, w out."""
    return pmap((s * x, 0.0, 0.0), (0.0, 0.0, s * 1.0), (0.0, 1.0, 0.0), (s * 1.0, 0.0, 0.0))


def rear_face(z):
    return pmap((0.0, 0.0, z), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, -1.0))


def windscreen_face():
    """The windscreen slope: u = x, v up the slope from its lower edge, w out."""
    (zt, ht), (zb, hb) = WINDSCREEN
    d = Vector((0.0, ht - hb, zt - zb)).normalized()
    n = Vector((0.0, -d.z, d.y))
    if n.z < 0:
        n = -n
    return pmap((0.0, hb, zb), (1.0, 0.0, 0.0), tuple(d), tuple(n)), (Vector((zt, ht)) - Vector((zb, hb))).length


def lean_face(s, lean):
    """A leaning side as a surface map: u = s * z, v up the lean from its crease, w out."""
    c, sn = math.cos(math.radians(lean[1])), math.sin(math.radians(lean[1]))
    return pmap((s * UPPER_X, lean[0], 0.0), (0.0, 0.0, s * 1.0), (-s * sn, c, 0.0), (s * c, sn, 0.0))


def band_face():
    """The nose's light band: u = -x (the viewer's right), v up the band from its foot, w out; and its length."""
    (zb, hb) = WINDSCREEN[1]
    d = Vector((0.0, hb - BAND[1], zb - BAND[0])).normalized()
    n = Vector((0.0, -d.z, d.y))
    return pmap((0.0, BAND[1], BAND[0]), (-1.0, 0.0, 0.0), tuple(d), tuple(n)), math.hypot(hb - BAND[1], zb - BAND[0])


def tuck_face(s):
    """The lower hull's tuck as a surface map: u = s * z, v down it from the belt line, w out."""
    c, sn = math.cos(math.radians(TUCK[1])), math.sin(math.radians(TUCK[1]))
    return pmap((s * UPPER_X, TUCK[0], 0.0), (0.0, 0.0, s * 1.0), (-s * sn, -c, 0.0), (s * c, -sn, 0.0))


def snout_face():
    """The snout (the nose under the light band) as a surface map: u = -x, v up it from its foot, w out; its length."""
    d = Vector((0.0, BAND[1] - SNOUT[1], BAND[0] - SNOUT[0])).normalized()
    n = Vector((0.0, -d.z, d.y))
    return pmap((0.0, SNOUT[1], SNOUT[0]), (-1.0, 0.0, 0.0), tuple(d), tuple(n)), math.hypot(BAND[1] - SNOUT[1], BAND[0] - SNOUT[0])


def corner_face(s):
    """The front corner facet of side s (the CORNER line): u along it forward, v = h, w out."""
    a, b = Vector((s * CORNER[0][0], CORNER[0][1])), Vector((s * CORNER[1][0], CORNER[1][1]))
    d = (b - a).normalized()
    n = Vector((d.y, -d.x)) * (1 if s > 0 else -1)
    if n.x * s < 0:
        n = -n
    return pmap((a.x, 0.0, a.y), (d.x, 0.0, d.y), (0.0, 1.0, 0.0), (n.x, 0.0, n.y)), (b - a).length


def _hide_cutter(ob):
    del ob["role"]
    ob.hide_set(True)
    ob.hide_render = True
    ob.display_type = "WIRE"
    return ob


def build_cab_glass(body, out):
    """The cab made hollow: a room following the shell 7-8 cm inside (the slope, the corner facets and the lean
    inset), cut from the hull. Window openings through the shell:
    the windscreen, two corner panes and two quarter windows (both following the slope line, so the glass wraps
    round the front), and the door windows. Glass panes sit 2.5-3.5 cm inside each opening (GEO_RoverGlass);
    black frames round the rectangular ones."""
    step = CAB_STEP_Z - 0.03                               # a 3 cm step wall stays between the seats and the footwell
    room_prof = [(CAB_Z0 + 0.07, CAB_LOW + 0.06), (FRONT_WELL_Z, CAB_LOW + 0.06), (FRONT_WELL_Z, ARCH_H + 0.06),
                 (NOSE_Z - 0.08, ARCH_H + 0.06), (NOSE_Z - 0.08, CAB_ROOF - 0.07), (CAB_Z0 + 0.07, CAB_ROOF - 0.07)]
    rp = faceted("RVX_CabRoom", "charcoal", room_prof, -(CAB_X - 0.10), CAB_X - 0.10,
                 [inset(pl, 0.08) for pl in cab_planes() + [slope_plane()]], bevel=0.0)
    room = _hide_cutter(finish(rp, body))
    plat = cutter(body, "RVX_SeatPlatform", (-0.675, CAB_LOW + 0.065, FRONT_WELL_Z - 0.01), (0.675, ARCH_H + 0.07, step))
    for hp in hull_parts():
        boolean(hp, [room], "Room")
        boolean(hp, [plat], "Plat")                              # the seats' floor between the front wheel wells, 5 mm up
    glass_col = bpy.data.collections["GEO_RoverGlass"]
    pg, pf = g.Part("RVG_Cab", "glass"), g.Part("RVB_WindowFrames", "black")
    panes = cab_panes()
    cuts = []
    for k, (Mp, poly, rect) in enumerate(panes):
        cp = g.Part(f"RVX_Win{k}", "charcoal")
        g.prism(cp, poly, -0.25, 0.25, lambda u, v, w, Mp=Mp: Mp(u, v, w))
        cuts.append(_hide_cutter(finish(cp, body)))
        g.prism(pg, poly, -0.035, -0.025, lambda u, v, w, Mp=Mp: Mp(u, v, w))
        if rect:
            u0, u1, v0, v1 = rect
            g.ring_prism(pf, g.rrect(u0 - 0.03, u1 + 0.03, v0 - 0.03, v1 + 0.03, 0.06), g.rrect(u0 + 0.001, u1 - 0.001, v0 + 0.001, v1 - 0.001, 0.049),
                         -g.EMB, 0.012, Mp)                    # inner edge 1 mm into the opening: not on its cut wall
        else:                                                  # the corner panes and the quarter windows: their own outline
            ccw = poly if _area(poly) > 0 else list(reversed(poly))
            g.ring_prism(pf, _offset_poly(ccw, 0.03), _offset_poly(ccw, -0.001), -g.EMB, 0.012, Mp)
    for hp in hull_parts():
        boolean(hp, cuts, "Win")
    out.append(finish(pg, glass_col))
    out.append(finish(pf, body))


def _area(poly):
    """The signed area of an outline (u, v): positive when counter-clockwise."""
    return sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly))) / 2


def _offset_poly(poly, d):
    """A convex counter-clockwise outline (u, v) moved d outward along its edges' normals, the corners mitred."""
    out = []
    for i in range(len(poly)):
        p0, p1, p2 = Vector(poly[i - 1]), Vector(poly[i]), Vector(poly[(i + 1) % len(poly)])
        e1, e2 = (p1 - p0).normalized(), (p2 - p1).normalized()
        n1, n2 = Vector((e1.y, -e1.x)), Vector((e2.y, -e2.x))      # outward: right of travel
        out.append(tuple(p1 + (n1 + n2) * (d / (1.0 + n1.dot(n2)))))
    return out


def cab_panes():
    """The cab's window panes: (surface map, outline [(u, v)], rect for a rounded frame or None) — the windscreen, two
    corner panes and two quarter windows (following the slope line, so the glass wraps round the front), the door
    windows."""
    panes = []
    M, L = windscreen_face()
    rect = (-WINDSCREEN_HALF, WINDSCREEN_HALF, 0.14, L - 0.12)
    panes.append((M, g.rrect(*rect, 0.05), rect))
    for s in (1, -1):
        C, W = corner_face(s)                             # corner facet: u along the CORNER line, forward
        (xa_, za_), (xb_, zb_) = CORNER
        at = lambda u: (xa_ + (xb_ - xa_) * u / W, za_ + (zb_ - za_) * u / W)          # (|x|, z) at u
        top = lambda u: min(slope_h(at(u)[1]) - 0.07, lean_h(at(u)[0], CAB_LEAN) - 0.05)   # under the slope and the lean
        u0, u1 = 0.10, W - 0.14
        while top(u1) < 2.14 and u1 > u0 + 0.10:
            u1 -= 0.02
        us = [u0 + (u1 - u0) * k / 8 for k in range(9)]
        panes.append((C, [(u0, 2.02), (u1, 2.02)] + [(u, top(u)) for u in reversed(us)], None))
        Lf = lean_face(s, CAB_LEAN)                       # the side windows are on the cab's lean: v up it from the sill
        v = lambda hh: lean_v(hh, CAB_LEAN)
        qtop = lambda z: min(CAB_ROOF - 0.18, slope_h(z) - 0.08)
        za, zb = 1.76, 2.00                               # quarter window (ends before the corner facet), top following the slope
        panes.append((Lf, [(s * za, v(2.35)), (s * zb, v(2.35)), (s * zb, v(qtop(zb))), (s * za, v(qtop(za)))], None))
        a, b = sorted((s * 0.98, s * 1.64))               # door window
        rect = (a, b, v(2.35), v(2.82))
        panes.append((Lf, g.rrect(*rect, 0.04), rect))
    return panes


def build_trim(body, out):
    """Orange cab stripe and bay-door frames (the stripe does not cross the bays: the orange frames carry it back),
    door outlines, the recessed slot panels' charcoal backs, vents, segmented roof plates and vent boxes."""
    po, pc, pv, pb = g.Part("RVB_Orange", "orange"), g.Part("RVB_Charcoal", "charcoal"), g.Part("RVB_Vents", "black"), g.Part("RVB_Roof", "silver")
    for s in (1, -1):
        Fm, Fc = side_face(s, UPPER_X), side_face(s, CAB_X)
        u = lambda z: s * z                                          # side_face u runs with s * z
        g.wbox(po, Fc, min(u(CAB_Z0), u(CORNER[0][1] - 0.02)), max(u(CAB_Z0), u(CORNER[0][1] - 0.02)), 2.05, 2.11, -g.EMB, 0.006)   # cab stripe, to the corner facet
        g.wbox(po, Fm, min(u(REAR_Z + 0.20), u(-3.72)), max(u(REAR_Z + 0.20), u(-3.72)), 2.05, 2.11, -g.EMB, 0.006)    # tail stripe
        # (the stripe continues at the same height on each bay door leaf, build_bay_doors; gaps at the slot panel)
        Lm, Lc = lean_face(s, MOD_LEAN), lean_face(s, CAB_LEAN)
        vt = lean_v(BAY_TOP, MOD_LEAN)                              # the bay top, up the chamfer
        for name, zf, zr in BAYS:                                   # orange frame round each bay opening, bent at the crease
            a, b = sorted((u(zr), u(zf)))
            g.wbox(po, Fm, a - 0.04, b + 0.04, BAY_FLOOR - 0.04, BAY_FLOOR + 0.001, -g.EMB, 0.010)
            for u0, u1 in ((a - 0.04, a + 0.001), (b - 0.001, b + 0.04)):
                g.wbox(po, Fm, u0, u1, BAY_FLOOR - 0.04, MOD_CREASE + 0.004, -g.EMB, 0.010)
                g.wbox(po, Lm, u0, u1, -0.004, vt + 0.04, -g.EMB, 0.010)
            g.wbox(po, Lm, a - 0.04, b + 0.04, vt - 0.001, vt + 0.04, -g.EMB, 0.010)
        a, b = sorted((u(RECESS_Z[0]), u(RECESS_Z[1])))              # the slot recess's back plate (charcoal), edges in its walls
        B = side_face(s, SLOT_BACK_X)
        if s > 0:
            g.wbox(pc, B, a - g.EMB, b + g.EMB, SLOT_H[0] - g.EMB, SLOT_H[1] + g.EMB, -0.01, g.EMB)
        else:                                                        # left: round the canister rack's mouth, 1 mm lip into it
            ra, rb = sorted((u(RACK["z"][0]), u(RACK["z"][1])))
            g.ring_prism(pc, g.rrect(a - g.EMB, b + g.EMB, SLOT_H[0] - g.EMB, SLOT_H[1] + g.EMB, 0.002),
                         g.rrect(ra + 0.001, rb - 0.001, RACK["h"][0] + 0.001, RACK["h"][1] - 0.001, 0.002), -0.01, g.EMB, B)
        a, b = sorted((u(0.92), u(1.70)))                            # cab door outline (a 1.2 cm frame), bent at the sill
        vd = lean_v(2.90, CAB_LEAN)
        g.wbox(pc, Fc, a, b, ARCH_H + 0.06, ARCH_H + 0.072, -g.EMB, 0.004)
        for u0, u1 in ((a, a + 0.012), (b - 0.012, b)):
            g.wbox(pc, Fc, u0, u1, ARCH_H + 0.06, CAB_CREASE + 0.003, -g.EMB, 0.004)
            g.wbox(pc, Lc, u0, u1, -0.003, vd, -g.EMB, 0.004)
        g.wbox(pc, Lc, a, b, vd - 0.012, vd, -g.EMB, 0.004)
    Rk = side_face(-1, RACK["x"])                                    # the canister rack's back wall (u = -z)
    ra, rb = sorted((-RACK["z"][0], -RACK["z"][1]))
    g.wbox(pc, Rk, ra - g.EMB, rb + g.EMB, RACK["h"][0] - g.EMB, RACK["h"][1] + g.EMB, -0.01, g.EMB)
    R = rear_face(REAR_Z - 0.12)                                     # tail pod vents (rear faces)
    for a, b, n in REAR_VENTS:
        g.louvre(pc, pv, R, a, b, *REAR_VENT_H, n=n)
    for z0, z1, xw in ((1.80, 0.95, 0.60), (0.75, -0.40, 0.95), (-0.50, -1.60, 0.95), (-1.80, -2.95, 0.95), (-3.05, -3.60, 0.95)):
        g.box(pb, -xw, xw, MODULE_ROOF - g.EMB, MODULE_ROOF + 0.03, z1, z0, space=U)   # roof plates with gaps (the cab's roof is |x| <= 1.04)
    for x, z in ((-0.70, 0.20), (0.70, -2.40)):                     # roof vent boxes
        g.box(pc, x - 0.20, x + 0.20, MODULE_ROOF + 0.02, MODULE_ROOF + 0.14, z - 0.25, z + 0.25, space=U)
    for p, bev in ((po, 0.0), (pc, 0.004), (pv, 0.0), (pb, 0.01)):
        out.append(finish(p, body, bevel=bev))


def build_lights_mast_hitch(body, out):
    ph, pw, pr = g.Part("RVB_LampHousings", "black"), g.Part("RVB_LedLens", "white"), g.Part("RVB_RedLens", "lamp")
    Bf, Lb = band_face()
    pg = g.Part("RVL_HeadGlow", "white")                              # the LED bars' lenses lit (GEO_RoverGlow: shown in
    g.wbox(ph, Bf, -0.64, 0.64, 0.012, Lb - 0.012, -0.03, 0.004)       # game with the headlights), 1.5 mm proud of them
    for s in (1, -1):                                                  # a dark plate across the light band
        a, b = sorted((s * 0.20, s * 0.60))                           # the band is |x| <= 0.69 (corner facets)
        g.lamp(ph, pw, Bf, a, b, 0.05, 0.12)                           # LED light bars (the headlights)
        g.wbox(pg, Bf, a + 0.010, b - 0.010, 0.060, 0.110, 0.0235, 0.0255)
        R = rear_face(BUMPER["z"][1])
        a, b = sorted((s * 0.70, s * 0.90))
        g.lamp(ph, pr, R, a, b, 0.95, 1.15)                            # square tail lights
    finish(pg, bpy.data.collections["GEO_RoverGlow"])
    pm, pd = g.Part("RVB_Mast", "gunmetal"), g.Part("RVB_MastDome", "steel", smooth=True)
    mz = -0.85
    g.box(pm, -0.16, 0.16, MODULE_ROOF - g.EMB, MODULE_ROOF + 0.12, mz - 0.16, mz + 0.16, space=U)
    g.cylinder(pm, U(0, MODULE_ROOF + 0.10, mz), Vector((0, 0, 1)), 0.06, 0.34, seg=12, smooth=False)
    g.box(pm, -0.30, 0.30, 3.52, 3.62, mz - 0.10, mz + 0.10, space=U)                        # camera bar
    for s in (1, -1):
        g.box(pm, s * 0.22 - 0.09, s * 0.22 + 0.09, 3.47, 3.65, mz - 0.12, mz + 0.14, space=U)  # camera pods
        g.cylinder(pm, U(s * 0.22, 3.56, mz + 0.14), Vector((0, 1, 0)), 0.05, 0.03, seg=12, smooth=False)
    g.cylinder(pm, U(0, 3.62, mz), Vector((0, 0, 1)), 0.03, 0.12 + g.EMB, seg=10, smooth=False)   # stalk into the dome (no coplanar caps)
    g.sphere(pd, U(0, 3.86, mz), 0.15, cut_below=3.74)
    ah = MODULE_ROOF + 0.26 + 0.02                                     # the antenna on the roof rack's rear-right rail
    g.cylinder(pm, U(0.88, ah - g.EMB, -3.40), Vector((0, 0, 1)), 0.03, 0.06, seg=10, smooth=False)   # antenna base
    pa = g.Part("RVB_Antenna", "black", smooth=True)
    g.cylinder(pa, U(0.88, ah + 0.045, -3.40), Vector((0, 0, 1)), 0.012, 1.25, seg=8)
    # hitch: a forged receiver arm from the bumper back to a pintle jaw on the pin (the 2020 receiver's section sizes;
    # jaw plates and pin as there, so the trailers' lunette rings fit). The pin sits 0.80 m behind the bumper so the
    # hab's tongue clears the tail to 70 deg of yaw (Task 3 ruling).
    hx, hh, hz = HITCH
    pj, pp, pl = g.Part("RVB_HitchJaw", "gunmetal"), g.Part("RVB_HitchPin", "gunmetal", smooth=True), g.Part("RVB_HitchLatch", "orange")
    z0, zf, zj = BUMPER["z"][1] + 0.012, BUMPER["z"][1] - 0.20, hz + 0.17          # arm start (in the bumper), end of fillet, jaw front

    def width(z):
        return g._ease(0.17, 0.095, (z0 - z) / (z0 - zf)) if z >= zf else 0.095 + (0.075 - 0.095) * (zf - z) / (zf - zj)

    def top(z):
        return g._ease(0.86, 0.775, (z0 - z) / (z0 - zf)) if z >= zf else 0.775 + (0.765 - 0.775) * (zf - z) / (zf - zj)

    def bottom(z):
        return g._ease(0.66, 0.68, (z0 - z) / (z0 - zf)) if z >= zf else 0.68
    stations = [z0 + (zf - z0) * i / 6 for i in range(7)] + [(zf + zj) / 2, zj - 0.01]
    bm = pj.bm
    rings = []
    for z in stations:
        w, t, b = width(z), top(z), bottom(z)
        c = min(0.025, w * 0.3)
        sec = [(w, b + c), (w, t - c), (w - c, t), (-w + c, t), (-w, t - c), (-w, b + c), (-w + c, b), (w - c, b)]
        rings.append([bm.verts.new(U(x, h, z)) for x, h in sec])
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(8):
            bm.faces.new((r0[k], r0[(k + 1) % 8], r1[(k + 1) % 8], r1[k]))
    bm.faces.new(rings[0])
    bm.faces.new(list(reversed(rings[-1])))
    g.box(pj, -0.075, 0.075, 0.655, 0.885, hz + 0.11, zj, space=U)                        # jaw upright
    rounded = [(0.075 * math.cos(math.radians(a)), hz + 0.075 * math.sin(math.radians(a))) for a in range(0, -181, -15)]
    plate = [(0.075, hz + 0.125)] + rounded + [(-0.075, hz + 0.125)]
    for h0, h1 in ((0.70, 0.75), (0.845, 0.885)):
        g.prism(pj, plate, h0, h1, lambda x, z, h: U(x, h, z))
    g.cylinder(pp, U(hx, 0.745, hz), Vector((0, 0, 1)), 0.034, 0.105)
    g.box(pl, -0.018, 0.018, 0.884, 0.905, hz - 0.02, hz + 0.09, space=U)
    for p, bev in ((ph, 0.004), (pw, 0.0), (pr, 0.0), (pm, 0.006), (pd, 0.0), (pa, 0.0), (pj, 0.006), (pp, 0.0), (pl, 0.004)):
        out.append(finish(p, body, bevel=bev))


# ---------------------------------------------------------------- cab interior (sub-project 2) and bay doors
def dash_slope():
    """The dash's screen slope (DASH[0] -> DASH[3]) as a surface map: u = x, v up the slope, w out toward the seats.
    Returns (map, length, (origin, v, w) as Unity (x, h, z) tuples)."""
    (z0, h0), (z1, h1) = DASH[0], DASH[3]
    L = math.hypot(z1 - z0, h1 - h0)
    v = (0.0, (h1 - h0) / L, (z1 - z0) / L)
    w = (0.0, (z1 - z0) / L, -(h1 - h0) / L)
    o = (0.0, h0, z0)
    return pmap(o, (1.0, 0.0, 0.0), v, w), L, (o, v, w)


def build_cab_interior(body, out):
    """The cab interior (spec 2026-09-27-original-rover-cab-design.md): the footwell dip between the front wheels (a
    tub under a hole cut in the cab floor, 5 mm lips so no faces meet flush), two seats at the seat anchors, the dash
    console whose 45 deg slope carries the touch screen in a black bezel, and the ceiling light panel."""
    X, wall, fl, (z0, z1) = DIP["x"], DIP["wall"], DIP["floor"], DIP["z"]
    top = ARCH_H + g.EMB
    step = CAB_STEP_Z - 0.03                                         # the room's step wall (build_cab_glass)
    hole = cutter(body, "RVX_Dip", (-(X - 0.005), fl - 0.01, z0), (X - 0.005, ARCH_H + 0.07, z1 - 0.005))
    for hp in hull_parts():
        boolean(hp, [hole], "Dip")
    pt = g.Part("RVB_DipTub", "charcoal")
    g.box(pt, -(X + wall), X + wall, fl - wall, fl, step + 0.036, z1 + wall, space=U)             # floor, in front of the seat slab
    g.box(pt, -(X - 0.005), X - 0.005, fl - wall, fl, z0, step + 0.036 + g.EMB, space=U)          # floor under the hole's rear
    for s in (1, -1):
        xa, xb = sorted((s * X, s * (X + wall)))
        g.box(pt, xa, xb, fl - g.EMB, top, step + 2 * g.EMB, z1 + wall, space=U)                    # side walls, from inside the step (past the cushions)
    g.box(pt, -(X + g.EMB), X + g.EMB, fl - g.EMB, top, z1, z1 + wall, space=U)                     # front wall
    out.append(finish(pt, body))

    ps, po = g.Part("RVB_Seats", "charcoal"), g.Part("RVB_SeatTrim", "orange")
    seat_top, floor = CAB_LOW + 0.19, CAB_LOW + 0.06                  # the seat anchor height; the room floor under the seats
    dz = SEAT_Z - 1.15                                               # the seat parts were drawn for SEAT_Z 1.15
    for s in (-1, 1):
        x = s * SEAT_X
        g.box(ps, x - 0.16, x + 0.16, floor - g.EMB, floor + 0.04, 1.00 + dz, 1.34 + dz, space=U)                  # pedestal
        g.box(ps, x - 0.23, x + 0.23, floor + 0.04 - g.EMB, seat_top, 0.95 + dz, step + g.EMB, space=U)          # cushion, front in the step wall
        (bz0, _), (bz1, bh1) = SEAT_BACK
        g.prism(ps, [(SEAT_Z + bz0 - 0.12, seat_top - g.EMB), (SEAT_Z + bz0, seat_top - g.EMB), (SEAT_Z + bz1, seat_top + bh1),
                     (SEAT_Z + bz1 - 0.10, seat_top + bh1)], x - 0.23, x + 0.23, lambda z, h, xx: U(xx, h, z))   # backrest, leaning back
        g.box(ps, x - 0.14, x + 0.14, 2.28 - g.EMB, 2.44, 0.93 + dz, 1.02 + dz, space=U)                           # headrest
        for e in (-1, 1):                                                                                          # piping on the backrest's front edges
            xa, xb = sorted((x + e * 0.212, x + e * 0.230))
            g.prism(po, [(1.05 + dz - g.EMB, 1.62), (1.058 + dz, 1.62), (1.008 + dz, 2.24), (1.0 + dz - g.EMB, 2.24)], xa, xb, lambda z, h, xx: U(xx, h, z))
        g.box(po, x - 0.21, x + 0.21, seat_top - 0.06, seat_top - 0.03, step + 0.003, step + g.EMB + 0.008, space=U)   # piping on the cushion's front
    out.append(finish(ps, body, bevel=0.02))
    out.append(finish(po, body))

    planes = [inset(pl, 0.08 - g.EMB) for pl in cab_planes() + [slope_plane()]]                     # the room's walls, EMB wider
    pd = faceted("RVB_Dash", "charcoal", list(DASH), -(CAB_X - 0.10 + g.EMB), CAB_X - 0.10 + g.EMB, planes, bevel=0.01)
    out.append(finish(pd, body))
    S, L, _ = dash_slope()
    vc, (fw, fh) = L / 2, SCREEN
    pb, pf = g.Part("RVB_ScreenBezel", "black"), g.Part("RVB_ScreenFace", "black")
    g.ring_prism(pb, g.rrect(-fw / 2 - 0.03, fw / 2 + 0.03, vc - fh / 2 - 0.03, vc + fh / 2 + 0.03, 0.03),
                 g.rrect(-fw / 2, fw / 2, vc - fh / 2, vc + fh / 2, 0.02), -g.EMB, 0.008, S)
    g.wbox(pf, S, -fw / 2 - 0.002, fw / 2 + 0.002, vc - fh / 2 - 0.002, vc + fh / 2 + 0.002, -g.EMB - 0.002, 0.002)   # edges in the bezel; the canvas 2 mm in front
    out.append(finish(pb, body, bevel=0.002))
    out.append(finish(pf, body))

    ceil = CAB_ROOF - 0.07                                           # the room's ceiling
    pl, pw = g.Part("RVB_CabinLightFrame", "black"), g.Part("RVB_CabinLightLens", "white")
    g.box(pl, -0.25, 0.25, ceil - 0.035, ceil + g.EMB, 1.40, 1.80, space=U)
    g.box(pw, -0.22, 0.22, ceil - 0.04, ceil - 0.03, 1.43, 1.77, space=U)
    out.append(finish(pl, body, bevel=0.004))
    out.append(finish(pw, body))


def tank_labels():
    """The tank labels' blocks (the user's requests), (text, Unity lo, hi) on the left: WASTE hung from the recess
    ceiling over the waste port, AIR standing on its floor under the air ports, each out from the rack face to
    LABEL_X[1] (blender_rover_checks.labels_mounted)."""
    hs = sorted(pos[1] for item, pos, rot in PANEL.values() if item == "canister")
    zc, r, (x0, x1) = PANEL["Air1"][1][2], ITEM_ROUND["canister"][0][2], LABEL_X
    return [("WASTE", (-x1, hs[-1] + r + 0.010, zc - 0.085), (-x0, SLOT_H[1] + g.EMB, zc + 0.085)),
            ("AIR", (-x1, SLOT_H[0] - g.EMB, zc - 0.085), (-x0, hs[0] - r - 0.010, zc + 0.085))]


def _circle(cu, cv, r, n=16):
    """A circle's outline (u, v), counter-clockwise."""
    return [(cu + r * math.cos(2 * math.pi * k / n), cv + r * math.sin(2 * math.pi * k / n)) for k in range(n)]


def holed_plate(p, outer, holes, w0, w1, M):
    """A plate on a surface map: the outline `outer` (u, v) less the `holes` (outlines), from w0 to w1."""
    bm = p.bm
    loops = []
    for pts in [outer] + holes:
        a = [bm.verts.new(M(u, v, w0)) for u, v in pts]
        b = [bm.verts.new(M(u, v, w1)) for u, v in pts]
        for i in range(len(pts)):
            j = (i + 1) % len(pts)
            bm.faces.new((a[i], a[j], b[j], b[i]))
        loops.append((a, b))
    for k in (0, 1):
        g._fill(bm, [lp[k] for lp in loops])


def level_map(h):
    """A horizontal map at height h: u = z, v = x, w up."""
    return pmap((0.0, h, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))


def build_slot_sockets(body, out):
    """The slot panels' sockets (the user's check-in 3 report: flat plates behind floating items did not look
    polished). Left: the canister rack's face with a round port per canister (a collar round each, the pocket lined
    black behind them, a valve port at its back), the filters' spigots in bosses on brackets off the back wall. Right:
    the batteries in a charging dock (a cup and a contact pad each), the chip's legs in a socket on a logic housing, a
    duct between them. Every item rests on, or plugs into, its socket (blender_rover_checks.slot_sockets)."""
    ph, pk, po = g.Part("RVB_SlotHousings", "steel"), g.Part("RVB_SlotSockets", "black"), g.Part("RVB_SlotTrim", "orange")
    E = g.EMB
    pw, pr = g.Part("RVB_SlotLabels", "white"), g.Part("RVB_SlotWaste", "lamp")
    L = side_face(-1, SLOT_BACK_X)                                  # left: u = -z
    z0, z1 = RECESS_Z[0] - E, RACK["z"][1] + 0.005                  # the rack's face, from inside the recess's rear wall,
    h0, h1 = SLOT_H[0] - E, SLOT_H[1] + E                           # floor and ceiling
    cans = sorted((pos[1], -pos[2]) for item, pos, rot in PANEL.values() if item == "canister")   # (h, u)
    holed_plate(ph, [(-z1, h0), (-z0, h0), (-z0, h1), (-z1, h1)], [list(reversed(_circle(u, h, PORT_R))) for h, u in cans],
                -E, RACK_FACE, L)
    waste_h = PANEL["Waste"][1][1]
    for h, u in cans:                                               # the ports' collars, the waste port's red
        g.ring_prism(pr if h == waste_h else pk, _circle(u, h, PORT_R + 0.006), _circle(u, h, PORT_R),
                     RACK_FACE - E, RACK_FACE + 0.006, L)
    # the tanks' labels (the user's request; the hab's tank cradles' colours): WASTE hung from the recess ceiling over
    # the waste port, AIR standing on its floor under the air ports, both blocks out from the rack face (a flat label on
    # it hides behind the canister under it from standing height)
    Lm = side_face(-1, LABEL_X[1])
    for text, lo, hi in tank_labels():
        plate, letters = (pr, pw) if text == "WASTE" else (pw, pk)
        g.box(plate, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], space=U)
        uc, vt = -(lo[2] + hi[2]) / 2, (max(lo[1], SLOT_H[0]) + min(hi[1], SLOT_H[1])) / 2     # its face's middle (u = -z)
        g.text_into(letters, text, Lm, uc, vt, 0.034, -0.001, 0.004)
        for du in (-0.075, 0.075):
            g.bolt(pk, Lm, uc + du, vt, 0.0, r=0.006, h=0.004)
    Lf = side_face(-1, SLOT_BACK_X + RACK_FACE)
    for u in (-z1 + 0.016, -z0 - 0.016):                            # the face's corner bolts
        for v in (SLOT_H[0] + 0.014, SLOT_H[1] - 0.014):
            g.bolt(pk, Lf, u, v, 0.0, r=0.007, h=0.005)
    xa, xb = -(SLOT_BACK_X + E), -(RACK["x"] - E)                   # the pocket lined (dark through an empty port)
    (ra, rb), (rh0, rh1) = RACK["z"], RACK["h"]
    for lo, hi in (((xa, rh0 - E, ra), (xb, rh0 + 0.003, rb)), ((xa, rh1 - 0.003, ra), (xb, rh1 + E, rb)),
                   ((xa, rh0, ra - E), (xb, rh1, ra + 0.003)), ((xa, rh0, rb - 0.003), (xb, rh1, rb + E))):
        g.box(pk, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], space=U)
    for h, u in cans:                                               # a valve port at the rack's back, 8 mm off each valve
        g.cylinder(pk, U(-(RACK["x"] - E), h, -u), Vector((-1, 0, 0)), 0.06, E + 0.008, seg=8, smooth=False)
        g.cylinder(pk, U(-(RACK["x"] - E), h, -u), Vector((-1, 0, 0)), 0.035, E + 0.028, seg=8, smooth=False)
    for item, (x, hf, zf), rot in PANEL.values():                   # the filters: boss, flange, bracket, feed pipe
        if item != "filter":
            continue
        foot, tip = hf - 0.12, hf - 0.191                           # the body's foot, the spigot's tip (ITEM_ROUND)
        g.cylinder(pk, U(x, tip - 0.012, zf), Vector((0, 0, 1)), 0.040, foot - tip + 0.006, seg=12, smooth=False)
        g.cylinder(pk, U(x, foot - 0.016, zf), Vector((0, 0, 1)), 0.054, 0.010, seg=12, smooth=False)
        hb = tip - 0.012
        g.box(ph, -(SLOT_BACK_X - E), x - 0.045, hb - 0.025, hb + E, zf - 0.045, zf + 0.045, space=U)
        g.prism(ph, [(-(SLOT_BACK_X - E), hb - 0.025 + E), (x + 0.01, hb - 0.025 + E), (-(SLOT_BACK_X - E), hb - 0.085)],
                zf - 0.012, zf + 0.012, lambda u, v, w: U(u, v, w))
        g.tube(pk, [U(x + 0.03, (foot + tip) / 2 - 0.009, zf), U(-(SLOT_BACK_X - E), (foot + tip) / 2 - 0.009, zf)], 0.012, seg=8)
    R = side_face(1, SLOT_BACK_X)                                   # right: u = z
    bats = sorted(pos for item, pos, rot in PANEL.values() if item == "battery")
    bz0, bz1, top = bats[0][2] - 0.08, bats[-1][2] + 0.08, DOCK["top"]
    g.wbox(ph, R, bz0, bz1, SLOT_H[0] - E, top, -E, 0.200)                            # the dock's base
    g.wbox(ph, R, bz0, bz1, top - E, top + 0.20, -E, DOCK["riser"])                    # its riser
    for x, _, zb in bats:                                           # a cup (5 mm round the foot) and a contact pad each
        g.ring_prism(pk, g.rrect(zb - 0.061, zb + 0.061, SLOT_BACK_X + DOCK["riser"] - E, x + 0.062, 0.008),
                     g.rrect(zb - 0.053, zb + 0.053, x - 0.054, x + 0.054, 0.004), -E, DOCK["cup"], level_map(top))
        g.box(pk, x - 0.030, x + 0.030, top - E, top + 0.004, zb - 0.030, zb + 0.030, space=U)
    F = side_face(1, SLOT_BACK_X + 0.200)
    g.wbox(po, F, bz0 + 0.012, bz1 - 0.012, 1.950, 1.972, -E, 0.003)                 # its orange stripe and bolts
    for u in (bz0 + 0.02, bz1 - 0.02):
        for v in (1.905, 2.010):
            g.bolt(pk, F, u, v, 0.0, r=0.007, h=0.005)
    _, ch, cz = PANEL["Chip"][1]                                    # the chip's logic housing, its socket (the legs' tips
    hz0, hz1, hv0, hv1 = cz - 0.115, cz + 0.115, ch - 0.155, ch + 0.155   # 1.5 mm off it), bolts, the duct to the dock
    g.wbox(ph, R, hz0, hz1, hv0, hv1, -E, 0.058)
    g.ring_prism(pk, g.rrect(cz - 0.097, cz + 0.097, ch - 0.134, ch + 0.134, 0.004),
                 g.rrect(cz - 0.058, cz + 0.058, ch - 0.122, ch + 0.122, 0.003), 0.058 - E, 0.068, R)
    for u in (hz0 + 0.02, hz1 - 0.02):
        for v in (hv0 + 0.0105, hv1 - 0.0105):
            g.bolt(pk, side_face(1, SLOT_BACK_X + 0.058), u, v, 0.0, r=0.007, h=0.005)
    g.wbox(pk, R, cz - 0.020, cz + 0.020, top + 0.20 - E, hv0 + E, -E, 0.025)
    for p, bev in ((ph, 0.004), (pk, 0.0), (po, 0.0), (pw, 0.0), (pr, 0.0)):
        out.append(finish(p, body, bevel=bev))


def build_bay_doors():
    """One silver leaf per bay (GEO_RoverDoors), 5 mm inside its opening: a 3.5 cm skin, upright to MOD_CREASE and then
    following the lean, a stiffening rib and the beltline stripe (an orange child part, exported with its leaf). Origin
    at the leaf centre; `hinge` = a point on its top-edge hinge line, 2 cm outside the lean and 1 cm above the opening."""
    col = bpy.data.collections["GEO_RoverDoors"]
    for ob in list(col.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    top, t = BAY_TOP - 0.008, UPPER_X - 0.005 - BAY_OUT_X            # 8 mm under the chamfered top: the frame strip overhangs it
    for s, lab in ((1, "R"), (-1, "L")):
        for name, zf, zr in BAYS:
            p = g.Part(f"BayDoor{lab}{name}", "silver")
            prof = [(BAY_OUT_X, BAY_FLOOR + 0.005), (UPPER_X - 0.005, BAY_FLOOR + 0.005), (UPPER_X - 0.005, MOD_CREASE),
                    (lean_x(top, MOD_LEAN) - 0.005, top), (lean_x(top, MOD_LEAN) - 0.005 - t, top), (BAY_OUT_X, MOD_CREASE)]
            g.prism(p, [(s * x, h) for x, h in prof], zr + 0.005, zf - 0.005, lambda x, h, z: U(x, h, z))
            xr = sorted((s * (UPPER_X - 0.005 - g.EMB), s * (UPPER_X + 0.012)))
            g.box(p, xr[0], xr[1], 2.42, 2.48, zr + 0.05, zf - 0.05, space=U)               # rib, above the stripe, into the rim
            xm = sorted((s * (UPPER_X - 0.005 - g.EMB), s * (UPPER_X + 0.008)))              # a raised rim round the upright panel
            for h0, h1 in ((BAY_FLOOR + 0.025, BAY_FLOOR + 0.065), (MOD_CREASE - 0.045, MOD_CREASE - 0.005)):
                g.box(p, xm[0], xm[1], h0, h1, zr + 0.025, zf - 0.025, space=U)
            for z0, z1 in ((zr + 0.025, zr + 0.065), (zf - 0.065, zf - 0.025)):
                g.box(p, xm[0], xm[1], BAY_FLOOR + 0.025, MOD_CREASE - 0.005, z0, z1, space=U)
            leaf = finish(p, col, bevel=0.006, bevel_segments=1)                           # one segment: the rim keeps it under DOOR_BUDGET
            ps = g.Part(f"BayDoor{lab}{name}Stripe", "orange")                                # the beltline stripe across the leaf
            xs = sorted((s * (UPPER_X - 0.005 - g.EMB), s * (UPPER_X + 0.001)))
            g.box(ps, xs[0], xs[1], 2.05, 2.11, zr + 0.01, zf - 0.01, space=U)
            xl = sorted((s * (UPPER_X - 0.005 - g.EMB), s * (UPPER_X + 0.020)))              # the latch handle, bottom centre
            g.box(ps, xl[0], xl[1], BAY_FLOOR + 0.09, BAY_FLOOR + 0.15, (zf + zr) / 2 - 0.07, (zf + zr) / 2 + 0.07, space=U)
            stripe = finish(ps, col)
            c = U(s * (BAY_OUT_X + UPPER_X) / 2, (BAY_FLOOR + BAY_TOP) / 2, (zf + zr) / 2)
            leaf["hinge"] = [round(s * (lean_x(BAY_TOP + DOOR_HINGE[1], MOD_LEAN) + DOOR_HINGE[0]), 4), round(BAY_TOP + DOOR_HINGE[1], 4), round((zf + zr) / 2, 4)]
            for ob in (leaf, stripe):                                # both: origin at the leaf centre
                ob.data.transform(Matrix.Translation(-c))
                ob.location = c
            stripe.parent = leaf
            stripe.matrix_parent_inverse = Matrix.Translation(-c)   # leaf's world = translation(c); its matrix_world is stale here


# ---------------------------------------------------------------- running gear, front end, hitch, details (Task 3)
def build_chassis(body, out):
    """The dark running gear (spec 2026-09-28 section 2): frame rails on the keel walls, a cross member at each wheel
    from the rail to the well's inner wall (the shock's upper mount), battery packs and air tanks under the lower hull."""
    pr = g.Part("RVB_Frame", "gunmetal")
    for s in (1, -1):
        xa, xb = sorted((s * (KEEL_X - g.EMB), s * FRAME_X))
        g.box(pr, xa, xb, FRAME_H[0], HULL_BOT + g.EMB, BUMPER["z"][0] + 0.10, NOSE_Z - 0.10, space=U)
        for name, z, mode in AXLES:
            tx, th, tz = shock_top(s, z)
            xa, xb = sorted((s * (FRAME_X - g.EMB), s * (ARCH_IN - 0.005)))              # into the well's liner plate
            g.box(pr, xa, xb, th + 0.015, HULL_BOT + g.EMB, tz - 0.06, tz + 0.06, space=U)
    out.append(finish(pr, body, bevel=0.008))
    pe = g.Part("RVB_Equipment", "charcoal")
    for s in (1, -1):
        xa, xb = sorted((s * (FRAME_X - g.EMB), s * 0.95))
        g.box(pe, xa, xb, 0.90, HULL_BOT + g.EMB, -0.50, 0.40, space=U)                     # battery pack
        for k in range(5):                                                                   # its cooling ribs
            xr = sorted((s * (0.95 - g.EMB), s * 0.975))
            g.box(pe, xr[0], xr[1], 0.94, 1.24, -0.42 + 0.18 * k, -0.38 + 0.18 * k, space=U)
        g.cylinder(pe, U(s * 0.80, 1.10, 0.55), Vector((0, 1, 0)), 0.14, 0.70, seg=14, smooth=False)   # an air tank along z
        for zz in (0.70, 1.10):                                                              # its straps, up into the hull
            xa, xb = sorted((s * 0.64, s * 0.96))
            g.box(pe, xa, xb, 1.20, HULL_BOT + g.EMB, zz - 0.02, zz + 0.02, space=U)
    out.append(finish(pe, body, bevel=0.01))


def build_front_end(body, out):
    """The heavy angular front bumper under the snout (a winch, red markers, orange tow shackles, a skid plate back to
    the keel) and the push bar: flat angled uprights either side of each headlight, a bar under it, stays into the band."""
    pb = faceted("RVB_FrontBumper", "gunmetal", [(3.00, 0.80), (3.50, 0.86), (3.58, 1.16), (3.44, HULL_BOT + g.EMB), (3.00, HULL_BOT + g.EMB)],
                 -1.00, 1.00, mirrored([plane_xz((1.00, 3.38), (0.82, 3.58), (1, 1)), plane_xz((0.82, 3.00), (1.00, 3.16), (1, -1))]), bevel=0.015)
    out.append(finish(pb, body))
    pw = g.Part("RVB_Winch", "black")
    g.box(pw, -0.28, 0.28, 0.92, 1.14, 3.46, 3.60, space=U)
    g.cylinder(pw, U(-0.22, 1.03, 3.59), Vector((1, 0, 0)), 0.07, 0.44, seg=12, smooth=False)     # the drum
    out.append(finish(pw, body, bevel=0.008))
    pl = g.Part("RVB_FrontMarkers", "lamp")
    for s in (1, -1):
        g.box(pl, s * 0.72 - 0.07, s * 0.72 + 0.07, 1.00, 1.08, 3.49, 3.585, space=U)            # backs buried in the sloped face
    out.append(finish(pl, body))
    po = g.Part("RVB_TowShackles", "orange", smooth=True)
    for s in (1, -1):
        cx = s * 0.50
        g.tube(po, [U(cx - 0.06, 0.87, 3.46), U(cx - 0.06, 0.76, 3.48), U(cx + 0.06, 0.76, 3.48), U(cx + 0.06, 0.87, 3.46)], 0.018)
    out.append(finish(po, body))
    sk = g.Part("RVB_Skid", "black")
    g.prism(sk, [(3.48, 0.855), (3.48, 0.885), (3.00, 0.765), (3.00, 0.735)], -0.50, 0.50, lambda z, h, x: U(x, h, z))
    out.append(finish(sk, body, bevel=0.006))
    pp = g.Part("RVB_PushBar", "gunmetal")
    for s in (1, -1):
        for x0 in (0.14, 0.66):                                  # uprights either side of the light, leaning back with the snout
            xa, xb = sorted((s * (x0 - 0.03), s * (x0 + 0.03)))
            g.prism(pp, [(3.36, HULL_BOT - 0.02), (3.44, HULL_BOT - 0.02), (3.22, 1.93), (3.14, 1.93)], xa, xb, lambda z, h, x: U(x, h, z))
        xa, xb = sorted((s * 0.115, s * 0.685))                  # the bar under the light, its ends in the uprights
        g.box(pp, xa, xb, 1.66, 1.74, 3.235, 3.28, space=U)
        for x0, x1 in ((0.12, 0.16), (0.64, 0.68)):              # stays from the uprights' tops back into the band
            xa, xb = sorted((s * x0, s * x1))
            g.box(pp, xa, xb, 1.88, 1.92, 3.05, 3.17, space=U)
    out.append(finish(pp, body, bevel=0.008))


def build_hitch_mount(body, out):
    """A receiver box bolted into the rear bumper with gussets: the tow arm's root sits inside it."""
    ph = g.Part("RVB_HitchMount", "gunmetal")
    z0 = BUMPER["z"][1]
    g.box(ph, -HITCH_BOX[0], HITCH_BOX[0], HITCH_BOX[1], HITCH_BOX[2], z0 - HITCH_BOX[3], z0 + 0.02, space=U)
    for s in (1, -1):
        xa, xb = sorted((s * (HITCH_BOX[0] - g.EMB), s * (HITCH_BOX[0] + 0.012)))
        g.prism(ph, [(z0 + 0.01, HITCH_BOX[1] + 0.02), (z0 + 0.01, HULL_BOT - 0.05), (z0 - HITCH_BOX[3] + 0.03, HITCH_BOX[2] - 0.01)],
                xa, xb, lambda z, h, x: U(x, h, z))                                     # gussets from the box's sides into the bumper
    out.append(finish(ph, body, bevel=0.006))


def build_details(body, out):
    """Surface detail (spec revision: panels in two or three depths, details that explain the machine): seams at the
    cab step and the tail, the cab doors' raised skins (round their windows), armour plates on the lower hull and the
    snout, a louvred vent in an orange frame on the lower hull under the storage module, grab handles ahead of the doors."""
    pk, po, pv = g.Part("RVB_Seams", "black"), g.Part("RVB_VentFrames", "orange"), g.Part("RVB_Grilles", "black")
    pg = g.Part("RVB_GrabHandles", "gunmetal", smooth=True)
    pu, pl = g.Part("RVB_Plates", "silver"), g.Part("RVB_PlatesLow", LOWER_ROLE)
    for s in (1, -1):
        S, T, Lc = side_face(s, UPPER_X), tuck_face(s), lean_face(s, CAB_LEAN)
        u = lambda z: s * z
        for z, top in ((CAB_STEP_AT, CAB_CREASE - 0.02), (-3.72, MOD_CREASE - 0.02)):      # seams: the cab step, the tail
            a, b = sorted((u(z) - 0.004, u(z) + 0.004))
            g.wbox(pk, S, a, b, TUCK[0] + 0.02, top, -g.EMB, 0.002)
        a, b = sorted((u(0.937), u(1.683)))                                                  # the door skin inside its outline
        g.wbox(pu, S, a, b, ARCH_H + 0.077, CAB_CREASE + 0.004, -g.EMB, 0.012)
        wa, wb = sorted((u(0.98), u(1.64)))
        g.ring_prism(pu, g.rrect(a, b, -0.004, lean_v(2.883, CAB_LEAN), 0.02),
                     g.rrect(wa - 0.035, wb + 0.035, lean_v(2.35, CAB_LEAN) - 0.035, lean_v(2.82, CAB_LEAN) + 0.035, 0.02), -g.EMB, 0.012, Lc)
        a, b = sorted((u(0.42), u(1.40)))                                                    # armour plate on the lower hull
        g.wbox(pl, T, a, b, 0.04, 0.50, -g.EMB, 0.015)
        a, b = sorted((u(-0.45), u(0.35)))
        g.louvre(po, pv, T, a, b, 0.08, 0.40, n=3)                                           # the lower-hull vent
        x0, x1 = s * (UPPER_X - g.EMB), s * (UPPER_X + 0.045)
        g.tube(pg, [U(x0, 1.88, 1.78), U(x1, 1.90, 1.78), U(x1, 2.22, 1.78), U(x0, 2.24, 1.78)], 0.014)   # grab handle
    N, Ln = snout_face()
    g.wbox(pl, N, -0.34, 0.34, 0.06, Ln - 0.10, -g.EMB, 0.015)                               # the snout's armour plate
    for p, bev in ((pk, 0.0), (po, 0.004), (pv, 0.004), (pg, 0.0), (pu, 0.005), (pl, 0.006)):
        out.append(finish(p, body, bevel=bev))


def build_roof_rack(body, out):
    """The roof rack on the storage module (spec revision, a character piece): side rails on posts into the roof, cross
    bars clear of the mast, and a light bar on its front bar facing forward over the cab."""
    pr = g.Part("RVB_RoofRack", "gunmetal")
    rh = MODULE_ROOF + 0.26                                   # the rails' centre height
    z0, z1 = 0.70, RACK_REAR                                  # the rack's front and rear (behind the cab step, over the tail pods)
    for s in (1, -1):
        xa, xb = sorted((s * 0.86, s * 0.90))
        g.box(pr, xa, xb, rh - 0.02, rh + 0.02, z1, z0, space=U)                              # side rail
        for zp in (0.62, -0.45, -1.70, -2.90, -3.48):                                        # posts into the roof
            g.box(pr, xa, xb, MODULE_ROOF - g.EMB, rh - 0.02 + g.EMB, zp - 0.02, zp + 0.02, space=U)
        xa, xb = sorted((s * 0.84, s * 0.88))                                                 # and into the tail pod's top
        g.box(pr, xa, xb, TAIL_TOP - g.EMB, rh - 0.02 + g.EMB, -3.82, -3.78, space=U)
        for zc in WORK_LAMPS["side_z"]:                                                      # the side lamps' brackets
            xa, xb = sorted((s * (0.90 - g.EMB), s * (WORK_LAMPS["side_x"] - 0.10 + g.EMB)))
            g.box(pr, xa, xb, rh - 0.015, rh + 0.015, zc - 0.02, zc + 0.02, space=U)
    for zc in (0.68, 0.10, -1.40, -2.10, -2.80, -3.52):                                      # cross bars (none over the mast at -0.85)
        g.box(pr, -0.86 - g.EMB, 0.86 + g.EMB, rh - 0.015, rh + 0.015, zc - 0.015, zc + 0.015, space=U)
    out.append(finish(pr, body, bevel=0.004))
    ph, pw = g.Part("RVB_LightBar", "black"), g.Part("RVB_LightBarLens", "white")
    g.box(ph, -0.70, 0.70, rh - 0.02, rh + 0.13, 0.66, 0.78, space=U)                          # the housing over the front bar
    F = pmap((0.0, 0.0, 0.78), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))            # its front face (u = x, v = h)
    pg = g.Part("RVL_BarGlow", "white")                                                      # its lenses lit (with the headlights)
    for k in range(4):
        x0 = -0.62 + 0.32 * k
        g.wbox(pw, F, x0, x0 + 0.28, rh + 0.01, rh + 0.10, -g.EMB, 0.006)
        g.wbox(pg, F, x0 + 0.002, x0 + 0.278, rh + 0.012, rh + 0.098, 0.0055, 0.0075)
    finish(pg, bpy.data.collections["GEO_RoverGlow"])
    sx, (rx, rz) = WORK_LAMPS["side_x"], (WORK_LAMPS["rear_x"], WORK_LAMPS["rear_z"])
    for s in (1, -1):                                         # work lights: side lamps over the bays, rear lamps at the rear corners
        for zc in WORK_LAMPS["side_z"]:
            xa, xb = sorted((s * (sx - 0.10), s * sx))
            g.box(ph, xa, xb, rh - 0.06, rh + 0.06, zc - 0.10, zc + 0.10, space=U)           # the lamp, its lens facing out
            xa, xb = sorted((s * (sx - g.EMB), s * (sx + 0.006)))
            g.box(pw, xa, xb, rh - 0.045, rh + 0.045, zc - 0.085, zc + 0.085, space=U)
        xa, xb = sorted((s * (0.90 - g.EMB), s * (rx + 0.10)))
        g.box(ph, xa, xb, rh - 0.06, rh + 0.06, rz, z1 + 0.06, space=U)                       # the rear lamp, off the rail end
        xa, xb = sorted((s * (rx - 0.085), s * (rx + 0.085)))
        g.box(pw, xa, xb, rh - 0.045, rh + 0.045, rz - 0.006, rz + g.EMB, space=U)
    out.append(finish(ph, body, bevel=0.004))
    out.append(finish(pw, body))


def build_ladder(body, out):
    """A ladder up the tail (spec revision, a character piece), beside the right tail pod's vent: tube rails with
    mitred bends that hug the surfaces (a foot in the rear bumper, 3 cm off the rear face, stepping out 3 cm off the
    pod, a hoop over the pod's top edge into its roof), rungs every 0.30 m and standoff brackets into the rear face and
    the pod."""
    pl = g.Part("RVB_Ladder", "gunmetal", smooth=True)
    zf, zp = REAR_Z - 0.03, REAR_Z - 0.12 - 0.03              # the rails' line off the rear face, and off the pod's rear face
    zq = REAR_Z - 0.12                                        # the pod's rear face
    path = [(zf + 0.05, 1.26), (zf, 1.36), (zf, 2.00), (zp, 2.14), (zp, 3.16), (zq + 0.05, TAIL_TOP + 0.035),
            (REAR_Z + 0.08, TAIL_TOP + 0.035), (REAR_Z + 0.11, TAIL_TOP - 0.02)]
    xs = (0.52, 0.86)                                          # on the pod's flat top (it leans in past |x| 0.90)
    for x in xs:                                              # the rails (both ends buried: the bumper, the pod's roof)
        g.tube(pl, [U(x, h, z) for z, h in path], 0.018, seg=10)
    for hh in (1.52, 1.82, 2.12, 2.42, 2.72, 3.02):           # rungs between the rails' inner surfaces
        zz = zf if hh <= 2.00 else zp if hh >= 2.14 else zf + (zp - zf) * (hh - 2.00) / 0.14
        g.cylinder(pl, U(xs[0] + 0.01, hh, zz), Vector((1, 0, 0)), 0.014, xs[1] - xs[0] - 0.02, seg=8, smooth=False)
    for hh, z0, z1 in ((1.55, zf, REAR_Z + g.EMB), (1.95, zf, REAR_Z + g.EMB), (2.50, zp, zq + g.EMB), (3.00, zp, zq + g.EMB)):
        for x in xs:                                          # standoffs from the rails into the surfaces
            g.box(pl, x - 0.02, x + 0.02, hh - 0.02, hh + 0.02, z0 - 0.005, z1, space=U)
    out.append(finish(pl, body))


def build_steps_flaps(body, out):
    """Side steps under the cab doors with orange front edges, and mud flaps behind the front and rear wheels (spec
    revision, character pieces), each on gussets or a bracket into the lower hull or the rear bumper."""
    ps, pe, pf = g.Part("RVB_Steps", "gunmetal"), g.Part("RVB_StepEdges", "orange"), g.Part("RVB_MudFlaps", "black")
    for s in (1, -1):
        xa, xb = sorted((s * (tuck_x(1.45) - 0.03), s * 1.42))
        g.box(ps, xa, xb, 1.42, 1.48, 0.97, 1.40, space=U)                                    # the tread, behind the front arch
        for zg in (1.02, 1.35):                                                               # gussets under it, into the tuck
            g.prism(ps, [(s * (tuck_x(1.33) - 0.02), 1.33), (s * (tuck_x(1.42) + 0.10), 1.42 + g.EMB), (s * (tuck_x(1.42) - 0.02), 1.42 + g.EMB)],
                    zg - 0.015, zg + 0.015, lambda x, h, z: U(x, h, z))
        xa, xb = sorted((s * 1.36, s * 1.43))
        g.box(pe, xa, xb, 1.43, 1.485, 0.98, 1.39, space=U)                                   # its orange front edge
        for zf, zb, hb in ((1.405, 1.425, HULL_BOT), (-4.037, -4.017, 1.24)):                 # flaps: behind the front and rear tyres
            xa, xb = sorted((s * 1.12, s * 1.78))
            g.box(pf, xa, xb, BELLY + 0.02, hb - 0.02, zf, zb, space=U)                          # down to the belly line (terrain)
            xa, xb = sorted((s * (1.04 if hb == HULL_BOT else 1.00), s * 1.80))               # the bracket bar into the hull / the bumper
            g.box(pf, xa, xb, hb - 0.04, hb + 0.02, zf - 0.005, zb + 0.005, space=U)
    out.append(finish(ps, body, bevel=0.006))
    out.append(finish(pe, body, bevel=0.003))
    out.append(finish(pf, body, bevel=0.004))


# ---------------------------------------------------------------- anchors (rover.json "anchors")
def anchor(col, name, pos, rot=(0.0, 0.0, 0.0)):
    old = bpy.data.objects.get("ANC_" + name)
    if old:
        bpy.data.objects.remove(old, do_unlink=True)
    ob = bpy.data.objects.new("ANC_" + name, None)
    col.objects.link(ob)
    ob.empty_display_type, ob.empty_display_size = "ARROWS", 0.12
    ob.location = U(*pos)
    ob.rotation_euler = Euler((math.radians(rot[0]), math.radians(rot[2]), -math.radians(rot[1])))   # display only
    ob["unity_pos"] = [round(v, 4) for v in pos]
    ob["unity_rot"] = [round(v, 3) for v in rot]
    return ob


def build_anchors():
    col = g.collection("ANC_Rover")
    bay_x = (BAY_IN_X + BAY_OUT_X) / 2
    for s, lab in ((-1, "L"), (1, "R")):
        for name, zf, zr in BAYS:
            zc = (zf + zr) / 2
            yaw = -90 * s                                           # the crate's fuller side (its local +z, 2.2 cm more) inboard:
            anchor(col, f"Bay{lab}{name}CrateBottom", (s * bay_x, BAY_FLOOR, zc), (0, yaw, 0))   # the top crate clears the lean
            anchor(col, f"Bay{lab}{name}CrateTop", (s * bay_x, BAY_FLOOR + CRATE_H + 0.005, zc), (0, yaw, 0))
            anchor(col, f"Bay{lab}{name}TankFront", (s * bay_x, BAY_FLOOR + TANK_UP, zc + 0.43))
            anchor(col, f"Bay{lab}{name}TankRear", (s * bay_x, BAY_FLOOR + TANK_UP, zc - 0.43))
    seat_z = SEAT_Z
    for s, who in ((-1, "Driver"), (1, "Passenger")):
        anchor(col, "Seat" + who, (s * SEAT_X, CAB_LOW + 0.19, seat_z + SEAT_FWD), (SEAT_PITCH, 0.0, 0.0))
        anchor(col, "Camera" + who, (s * SEAT_X, CAB_LOW + 0.96, seat_z))
        anchor(col, who + "Exit", (s * 2.35, 0.76, seat_z))
        anchor(col, "Entry" + who, (s * (CAB_X + 0.065 + ENTRY_IN) / 2, 2.36, 1.31))   # the seat's click box: from 5 mm off the
                                                                                        # cab door's face into the cab to |x| ENTRY_IN
    for name, (item, pos, rot) in PANEL.items():                  # the slot panel (the items' origins)
        anchor(col, name, pos, rot)
    anchor(col, "Hitch", HITCH)
    rh = MODULE_ROOF + 0.26
    for s, lab in ((-1, "L"), (1, "R")):                          # centre of each LED bar, 2 cm off its lens; the light bar's pair
        anchor(col, "Headlight" + lab, (s * 0.40, 1.87, 3.12), (FRONT_LAMPS["head_tilt"], 0.0, 0.0))   # 2 mm off its lenses
        anchor(col, "LightBar" + lab, (s * FRONT_LAMPS["bar_x"], rh + 0.055, 0.788), (FRONT_LAMPS["bar_tilt"], 0.0, 0.0))
        anchor(col, "TailLight" + lab, (s * 0.80, 1.05, BUMPER["z"][1] - 0.05))
    anchor(col, "CabinLight", (0.0, CAB_ROOF - 0.13, 1.60))
    rh, dn = MODULE_ROOF + 0.26, WORK_LAMPS["down"]           # the work lights (spot lights shine along their +z), 2 mm
    for s, lab in ((-1, "L"), (1, "R")):                      # off each lamp's lens
        for k, zc in enumerate(WORK_LAMPS["side_z"]):
            anchor(col, f"SideLight{lab}{k + 1}", (s * (WORK_LAMPS["side_x"] + 0.008), rh, zc), (dn, s * 90.0, 0.0))
        anchor(col, "RearLight" + lab, (s * WORK_LAMPS["rear_x"], rh, WORK_LAMPS["rear_z"] - 0.008), (dn, 180.0, 0.0))
    _, L, (o, v, w) = dash_slope()
    tilt = math.degrees(math.atan2(DASH[3][1] - DASH[0][1], DASH[3][0] - DASH[0][0]))
    face = tuple(o[k] + v[k] * L / 2 + w[k] * 0.002 for k in range(3))            # the screen face's front, centred
    anchor(col, "Screen", face, (-tilt, 180.0, 0.0))                               # +z out to the seats, +y up the slope
    return col


def build():
    body = g.collection("GEO_RoverBody")
    out = []
    build_keel(body, out)
    build_hull(body, out)
    build_arch_liners(body, out)
    build_tail(body, out)
    build_front_end(body, out)
    build_chassis(body, out)
    build_hitch_mount(body, out)
    g.collection("GEO_RoverGlass")
    g.collection("GEO_RoverGlow")
    build_cab_glass(body, out)
    build_trim(body, out)
    build_lights_mast_hitch(body, out)
    build_details(body, out)
    build_roof_rack(body, out)
    build_ladder(body, out)
    build_steps_flaps(body, out)
    build_cab_interior(body, out)
    build_slot_sockets(body, out)
    g.collection("GEO_RoverDoors")
    build_bay_doors()
    anc = build_anchors()
    return {"body": [o.name for o in out], "anchors": len(anc.objects)}
