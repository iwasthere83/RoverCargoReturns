"""Build a fresh working .blend from the model code, with the game reference meshes copied from an existing blend:

    blender -b --factory-startup --python-exit-code 1 --python tools/make_blend.py -- <source .blend> <new .blend>

The rover, the cargo trailer and the hab (with their build stages and upgrades) come from the same scripts as
export_all.py (exported to a temp folder, never into the mod); the REFERENCE collections below, which no script can
rebuild, are appended from <source>. Never overwrites: refuses a <new> that exists, or that is <source>.
"""
import os
import sys
import tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
REFERENCE = ("REF_RoverCargo", "REF_DynamicCrate", "REF_DynamicGasCanisterEmpty", "BLK_TrailerCargo")


def refusal(src, out):
    """Why make_blend must not run with these paths (None = fine). Pure: looks at paths only."""
    if not src or not os.path.isfile(src):
        return f"no source blend at {src}"
    if not out or not out.lower().endswith(".blend"):
        return f"the new file must be a .blend: {out}"
    if os.path.normcase(os.path.abspath(src)) == os.path.normcase(os.path.abspath(out)):
        return "the new file is the source blend"
    if os.path.exists(out):
        return f"{out} exists: make_blend never overwrites (pick a new name)"
    return None


def main(src, out):
    import bpy
    if TOOLS not in sys.path:
        sys.path.insert(0, TOOLS)
    import blender_build_rover as br
    import blender_build_trailer as bt

    cube = bpy.data.objects.get("Cube")                         # the factory scene's default cube
    if cube:
        bpy.data.objects.remove(cube)
    with bpy.data.libraries.load(src, link=False) as (have, want):
        missing = [n for n in REFERENCE if n not in have.collections]
        want.collections = [n for n in REFERENCE if n in have.collections]
    for c in want.collections:
        bpy.context.scene.collection.children.link(c)
    tmp = tempfile.mkdtemp(prefix="make_blend_")                # the export side effect lands here, not in the mod
    br.build(os.path.join(tmp, "RoverAssets"))
    bt.build(os.path.join(tmp, "TrailerAssets"), "cargo")
    bt.build(os.path.join(tmp, "HabAssets"), "hab")
    for c in bpy.context.scene.collection.children:
        if c.name.startswith("REF_"):                            # references stay out of renders, as before
            c.hide_render = True
    bpy.ops.wm.save_as_mainfile(filepath=out, check_existing=True)
    print("MAKE_BLEND ok", out, "missing references: " + (", ".join(missing) or "none"))


if __name__ == "__main__":
    args = [os.path.abspath(a) for a in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else []   # Blender reads relative paths its own way
    if len(args) != 2:
        print("usage: blender -b --factory-startup --python tools/make_blend.py -- <source .blend> <new .blend>")
        sys.exit(2)
    why = refusal(*args)
    if why:
        print("MAKE_BLEND refused: " + why)
        sys.exit(1)
    main(*args)
