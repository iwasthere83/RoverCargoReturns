"""Run the original rover's model checks headless (the saved art .blend holds the REF items and the trailers):

    blender -b --factory-startup art/trailer_cargo_blockout.blend --python-exit-code 1 --python tools/run_rover_checks.py

Builds the model and wheels from code, runs blender_rover_checks.rover_checks() and coplanar(), prints the result as
JSON and exits 0 only when every check passes and no face is coplanar.

The checks that measure against game items (bay_fit: a crate and a portable tank in each bay) need the developer's
local reference scene (art/*.blend with the game's own meshes, never shipped); the exports need none
(tools/export_all.py runs in an empty scene).
"""
import json
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import bpy  # noqa: E402

import blender_rover_checks as c  # noqa: E402
import blender_rover_model as m  # noqa: E402
import blender_rover_upgrades as u  # noqa: E402
import blender_rover_wheels as w  # noqa: E402

m.build()
w.build()
u.build()
bpy.context.view_layer.update()
res = c.rover_checks()
cp = c.coplanar()
bad = {k: v for k, v in res.items() if k != "ok" and not v.get("ok")}
print("ROVER_CHECKS " + json.dumps({"ok": res["ok"], "checks": sorted(k for k in res if k != "ok"), "bad": bad, "coplanar": cp[:20]}, default=str))
sys.exit(0 if res["ok"] and not cp else 1)
