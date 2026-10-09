"""Run the trailers' running-gear checks headless (both variants: the cargo trailer and the hab):

    blender -b --factory-startup art/rover_cargo.blend --python-exit-code 1 --python tools/run_trailer_checks.py

Also checks their colours (blender_trailer_checks.palette). Prints the result as JSON and exits 0 only when all pass.
"""
import json
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import blender_trailer_checks as tc  # noqa: E402

res = {v: tc.running_gear(v) for v in ("cargo", "hab")}
pal = {v: tc.palette(v) for v in ("cargo", "hab")}
bad = {v: {k: r for k, r in res[v].items() if k != "ok" and r["bad"]} for v in res}
paint = tc.paint_cells()
ok = all(r["ok"] for r in res.values()) and all(p["ok"] for p in pal.values()) and paint["ok"]
print("TRAILER_CHECKS " + json.dumps({"ok": ok, "bad": bad, "palette": pal, "paint": paint,
                                      "lengths": {v: next((r["lengths"] for k, r in res[v].items() if k != "ok"), None) for v in res}}))
sys.exit(0 if ok else 1)
