"""Run the frames' build-stage checks headless, all three vehicles:

    blender -b --factory-startup art/rover_cargo.blend --python-exit-code 1 --python tools/run_stage_checks.py [-- rover|cargo|hab]

Prints the result as JSON and exits 0 only when all pass."""
import json
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import blender_stage_checks as sc  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
res = {v: sc.stage_checks(v) for v in (argv or ["rover", "cargo", "hab"])}
bad = {v: {k: r["bad"] for k, r in res[v].items() if k != "ok" and not r["ok"]} for v in res}
ok = all(r["ok"] for r in res.values())
print("STAGE_CHECKS " + json.dumps({"ok": ok, "bad": bad}))
sys.exit(0 if ok else 1)
