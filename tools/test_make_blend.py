"""Tests for make_blend.py's guard (plain python, no Blender):   python tools/test_make_blend.py

Safety: paths only, in a fresh temp dir; nothing is built, saved or removed outside it.
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_blend  # noqa: E402  (main() runs only in Blender, as a script)

fails = []


def check(cond, label):
    print(("ok   " if cond else "FAIL ") + label)
    if not cond:
        fails.append(label)


with tempfile.TemporaryDirectory() as tmp:
    src = os.path.join(tmp, "old.blend")
    open(src, "wb").write(b"BLENDER")
    check(make_blend.refusal(src, os.path.join(tmp, "new.blend")) is None, "a new name next to the source is fine")
    check(make_blend.refusal(src, src) is not None, "refuses to write over the source")
    check(make_blend.refusal(src, os.path.join(tmp, ".", "old.blend")) is not None, "refuses the source by another spelling")
    taken = os.path.join(tmp, "taken.blend")
    open(taken, "wb").write(b"x")
    check(make_blend.refusal(src, taken) is not None, "refuses any existing file")
    check(make_blend.refusal(os.path.join(tmp, "none.blend"), os.path.join(tmp, "n.blend")) is not None, "refuses a missing source")
    check(make_blend.refusal(src, os.path.join(tmp, "new.txt")) is not None, "the new file must be a .blend")
    check(open(src, "rb").read() == b"BLENDER" and open(taken, "rb").read() == b"x", "the guard touched nothing")

print("TEST_MAKE_BLEND " + ("ok" if not fails else "FAIL %d" % len(fails)))
sys.exit(1 if fails else 0)
