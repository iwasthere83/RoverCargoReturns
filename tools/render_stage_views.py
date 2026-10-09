"""Renders for the user's check-ins on the build stages (spec 2026-10-08) and the kits' thumbnails. Headless, saves
nothing:

    blender -b --factory-startup art/rover_cargo.blend --python tools/render_stage_views.py -- <out dir> stages
    blender -b --factory-startup art/rover_cargo.blend --python tools/render_stage_views.py -- <repo mod dir> thumbs
    blender -b --factory-startup art/rover_cargo.blend --python tools/render_stage_views.py -- <repo mod dir> vehicles

stages: every state of each vehicle (the last = the finished body, wheels and glass), front 3/4 and side, Workbench.
thumbs: each finished vehicle front 3/4 on a transparent background, 512 x 512, into <assets>/textures/<kit>.png.
vehicles: the same picture for the vehicle itself (its Stationpedia and creative-menu thumbnail), <assets>/textures/<prefab>.png."""
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import blender_build_stages as bs  # noqa: E402
import render_upgrade_views as rv  # noqa: E402

ASSETS = {"rover": "RoverAssets", "cargo": "TrailerAssets", "hab": "HabAssets"}
VEHICLES = {"rover": "RoverCargo", "cargo": "TrailerCargo", "hab": "TrailerHab"}   # prefab names (CargoPrefabs)
FINISHED = {"rover": ("GEO_RoverBody", "GEO_RoverDoors", "GEO_RoverGlass"), "cargo": ("GEO_TrailerCargo",),
            "hab": ("GEO_TrailerHab", "GEO_HabSlideOut", "GEO_HabLadder", "GEO_HabLeg")}


def _show(objs):
    names = {o.name for o in objs}
    for c in bpy.context.scene.collection.children_recursive:     # the saved blend hides some collections from renders
        c.hide_render = False
    for o in bpy.context.scene.objects:
        o.hide_render = o.type == "MESH" and o.name not in names


def _finished(variant):
    """The finished vehicle: its body collections plus the posed running gear (not the trestles)."""
    objs = [o for cn in FINISHED[variant] if cn in bpy.data.collections for o in bpy.data.collections[cn].objects
            if o.type == "MESH" and "role" in o]                     # exported parts only (no helpers or previews)
    return objs + [o for o in bs.body_parts(variant) if o.name.startswith("STG_") and "until" not in o]


def _shoot(path, loc, at, lens, size=None, transparent=False):
    scn = bpy.context.scene
    scn.render.engine = "BLENDER_WORKBENCH"
    scn.display.shading.light, scn.display.shading.color_type, scn.display.shading.show_cavity = "STUDIO", "MATERIAL", True
    scn.render.film_transparent = transparent
    scn.render.resolution_x, scn.render.resolution_y = size or (1600, 1000)
    scn.render.image_settings.file_format, scn.render.image_settings.color_mode = "PNG", "RGBA"
    cam = bpy.data.objects.get("STGCam") or bpy.data.objects.new("STGCam", bpy.data.cameras.new("STGCam"))
    if cam.name not in scn.collection.objects:
        scn.collection.objects.link(cam)
    scn.camera, cam.location, cam.data.lens = cam, Vector(loc), lens
    cam.rotation_euler = (Vector(at) - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.hide_render = False
    scn.render.filepath = path
    bpy.ops.render.render(write_still=True)


def _thumb(path, objs, fill=0.92):
    """The finished vehicle from the front 3/4 (render_upgrade_views' angle), orthographic, centred and filling the
    square: a 512 x 512 icon on a transparent background."""
    loc, at, _ = rv.VIEWS["front34"]
    d = (Vector(at) - Vector(loc)).normalized()
    rot = d.to_track_quat("-Z", "Y")
    right, up = rot @ Vector((1, 0, 0)), rot @ Vector((0, 1, 0))
    pts = [p for o in objs for p in bs._verts(o)]
    us, vs = [p.dot(right) for p in pts], [p.dot(up) for p in pts]
    cu, cv = (min(us) + max(us)) / 2, (min(vs) + max(vs)) / 2
    centre = sum(pts, Vector()) / len(pts)
    centre += right * (cu - centre.dot(right)) + up * (cv - centre.dot(up))
    scn = bpy.context.scene
    cam = bpy.data.objects.get("STGCam") or bpy.data.objects.new("STGCam", bpy.data.cameras.new("STGCam"))
    if cam.name not in scn.collection.objects:
        scn.collection.objects.link(cam)
    cam.data.type, cam.data.ortho_scale = "ORTHO", max(max(us) - min(us), max(vs) - min(vs)) / fill
    cam.data.clip_end = 100.0
    _shoot(path, centre - d * 30.0, centre, 50, size=(512, 512), transparent=True)
    cam.data.type = "PERSP"


def main(out, what):
    os.makedirs(out, exist_ok=True)
    for v in bs.VARIANTS:
        bs.prepare(v)
        rv._paint(FINISHED[v] + ("GEO_RoverWheels", f"GEO_Stage_{v}"))
        o = bs._origin(v)
        if what == "stages":
            for k in range(bs.STATES[v]):
                _show(bs.state_parts(v, k) if k < bs.STATES[v] - 1 else _finished(v))
                for tag, (loc, at, lens) in (("front34", rv.VIEWS["front34"]), ("side", rv.VIEWS["side"])):
                    _shoot(os.path.join(out, f"{v}_state{k}_{tag}.png"), Vector(loc) + o, Vector(at) + o, lens)
        else:
            objs = _finished(v)
            _show(objs)
            path = os.path.join(out, ASSETS[v], "textures", (VEHICLES if what == "vehicles" else bs.KITS)[v] + ".png")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            _thumb(path, objs)
    print("RENDERED", out)


argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
main(os.path.abspath(argv[0]), argv[1] if len(argv) > 1 else "stages")   # Blender reads relative paths its own way
