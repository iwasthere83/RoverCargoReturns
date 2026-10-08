"""Export the original rover, the cargo trailer and the hab (RoverAssets/, TrailerAssets/, HabAssets/) from the model
scripts, headless; the scene may be empty:

    blender -b --factory-startup --python-exit-code 1 --python tools/export_all.py -- <mod dir>
"""
import os
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import blender_build_rover as br  # noqa: E402
import blender_build_trailer as bt  # noqa: E402

mod = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else os.path.dirname(TOOLS)
br.build(os.path.join(mod, "RoverAssets"))
bt.build(os.path.join(mod, "TrailerAssets"), "cargo")
bt.build(os.path.join(mod, "HabAssets"), "hab")
print("EXPORT_ALL ok")
