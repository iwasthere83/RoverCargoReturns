"""Renders for the user's check-ins (spec 2026-10-07): the original rover with each upgrade group, and the trailer and
hab with theirs, Workbench, each part in its palette role's colour. Headless, nothing saved:

    blender -b --factory-startup art/rover_cargo.blend --python tools/render_upgrade_views.py -- <out dir> [rover|trailers]
"""
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import blender_rover_model as m  # noqa: E402
import blender_rover_upgrades as bu  # noqa: E402
import blender_rover_wheels as rw  # noqa: E402
import blender_trailer_model as g  # noqa: E402

ROVER = ("GEO_RoverBody", "GEO_RoverDoors", "GEO_RoverGlass", "GEO_RoverGlow", "GEO_RoverWheels")
VIEWS = {"front34": ((-7.5, 9.5, 3.4), (0, -0.2, 1.6), 35), "side": ((-13.0, -0.2, 1.9), (0, -0.2, 1.7), 40),
         "rear34": ((7.0, -11.0, 3.8), (0, -0.3, 1.6), 35), "top": ((0.0, -0.3, 21.0), (0, -0.25, 0.0), 35),
         "low": ((-6.5, 0.2, 0.9), (0, 0.2, 1.1), 30)}


def _paint(colls):
    for cn in colls:
        c = bpy.data.collections.get(cn)
        for o in (c.all_objects if c else []):
            if o.type == "MESH" and "role" in o and not o.data.materials:
                o.data.materials.append(g.role_material(o["role"]))


def _place_pods():
    """Preview copies (no role: never exported or checked) of pod 0 at the other three pods' origins, as the exporter
    places the one pod mesh (the left ones mirrored about the pod's own axis)."""
    from mathutils import Matrix
    col = bpy.data.collections[bu.THRUSTERS]
    p0 = m.U(*bu.pod_positions()[0])
    for k, pos in enumerate(bu.pod_positions()[1:], start=1):
        at = m.U(*pos)
        M = Matrix.Translation(at) @ (Matrix.Scale(-1, 4, Vector((1, 0, 0))) if pos[0] < 0 else Matrix()) @ Matrix.Translation(-p0)
        for o in [o for o in col.objects if "role" in o]:
            cp = o.copy()
            cp.data = o.data
            cp.name = f"PRV_{o.name}_{k}"
            del cp["role"]
            col.objects.link(cp)
            cp.matrix_world = M @ o.matrix_world


def _place_trailer_wheels(coll):
    """Preview copies (no role) of the rover's tyres and arms on a trailer's axles (blender_build_trailer.running_gear),
    at rest, into the trailer's collection: the game gives the trailers the rover's running gear at runtime."""
    import blender_build_trailer as bt
    from mathutils import Matrix
    col = bpy.data.collections[coll]
    zf = next(z for n, z, _ in m.AXLES if n == "Front")
    wheels, _ = bt.running_gear()
    for k, w in enumerate(wheels):
        side = "L" if w["pos"][0] < 0 else "R"
        shift = Matrix.Translation(g.T(0, 0, w["pos"][2]) - m.U(0, 0, zf))
        for src in ("Tyre" + side, "TyreRim" + side, "Arm" + side):
            o = bpy.data.objects.get(src)
            if o is None:
                continue
            cp = o.copy()
            cp.data = o.data
            cp.name = f"PRV_{src}_{k}"
            cp.parent = None
            for key in list(cp.keys()):
                del cp[key]
            col.objects.link(cp)
            cp.matrix_world = shift @ o.matrix_world
            if not cp.data.materials and "role" in o:
                cp.data.materials.append(g.role_material(o["role"]))


def _render(out, tag, show, views, offset=Vector((0, 0, 0))):
    scn = bpy.context.scene
    for c in scn.collection.children_recursive:
        c.hide_render = c.name not in show
    for o in scn.collection.objects:
        o.hide_render = o.name != "UPGCam"
    for n in ("ShockBody", "ShockRod", "ShockSpring"):              # the shock templates at the origin
        if n in bpy.data.objects:
            bpy.data.objects[n].hide_render = True
    scn.render.engine = "BLENDER_WORKBENCH"
    scn.display.shading.light, scn.display.shading.color_type, scn.display.shading.show_cavity = "STUDIO", "MATERIAL", True
    scn.render.resolution_x, scn.render.resolution_y = 1600, 1000
    cam = bpy.data.objects.get("UPGCam") or bpy.data.objects.new("UPGCam", bpy.data.cameras.new("UPGCam"))
    if cam.name not in scn.collection.objects:
        scn.collection.objects.link(cam)
    scn.camera = cam
    for name in views:
        loc, at, lens = VIEWS[name]
        cam.location, cam.data.lens = Vector(loc) + offset, lens
        cam.rotation_euler = (Vector(at) + offset - cam.location).to_track_quat("-Z", "Y").to_euler()
        scn.render.filepath = os.path.join(out, f"{tag}_{name}.png")
        bpy.ops.render.render(write_still=True)


def main(out, which):
    os.makedirs(out, exist_ok=True)
    if which in ("rover", "all"):
        m.build(); rw.build(); bu.build()
        _paint(ROVER + bu.FITTED)
        _place_pods()
        for tag, extra in (("rover_armour", (bu.ARMOUR,)), ("rover_fairings", (bu.FAIRINGS,)),
                           ("rover_thrusters", (bu.THRUSTERS,)), ("rover_all", bu.FITTED)):
            _render(out, tag, set(ROVER) | set(extra), ("front34", "side", "rear34", "top") + (("low",) if tag == "rover_fairings" else ()))
    if which in ("trailers", "all"):
        for variant, body, upg, oy in (("cargo", "GEO_TrailerCargo", ("GEO_UpgArmourTrailer", "GEO_UpgFairingTrailer"), g.TRAILER_Y),
                                       ("hab", "GEO_TrailerHab", ("GEO_UpgArmourHab", "GEO_UpgFairingHab"), g.HAB_ORIGIN_Y)):
            g.build(variant)
            if variant == "hab":
                g.build_upgrades_hab()
            rw.build()
            _paint((body,) + upg + ("GEO_RoverWheels",))
            _place_trailer_wheels(body)
            _render(out, f"{variant}_upgrades", {body} | set(upg), ("front34", "side", "rear34"), Vector((0, oy, -0.3)))
    print("RENDERED", sorted(os.listdir(out)))


if __name__ == "__main__":                                              # (render_stage_views imports the helpers)
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else os.path.join(os.environ.get("TEMP", "."), "upgrade_views"), argv[1] if len(argv) > 1 else "all")
