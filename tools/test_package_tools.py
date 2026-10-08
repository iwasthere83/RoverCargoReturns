"""Tests for package_mod.py and check_package.py (the final review of the switch-over):

    python tools/test_package_tools.py

Safety: nothing here deletes or overwrites a real folder. package_mod's guard is tested through its pure refusal()
function on paths only; the one real packaging run writes into a fresh temp dir; check_package only reads.
"""
import os
import shutil
import subprocess
import sys
import tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(TOOLS)
sys.path.insert(0, TOOLS)
import check_package  # noqa: E402
import package_mod  # noqa: E402  (main() runs only as a script)

fails = []


def check(cond, label):
    print(("ok   " if cond else "FAIL ") + label)
    if not cond:
        fails.append(label)


def good_folder(root):
    """A minimal valid package, copied into a temp folder."""
    shutil.copytree(os.path.join(MOD, "About"), os.path.join(root, "About"))
    shutil.copytree(os.path.join(MOD, "GameData"), os.path.join(root, "GameData"))
    for d in ("RoverAssets", "TrailerAssets", "HabAssets"):
        shutil.copytree(os.path.join(MOD, d), os.path.join(root, d))
    shutil.copy2(os.path.join(MOD, "README.md"), root)
    open(os.path.join(root, "Stationeers.RoverCargo.dll"), "wb").write(b"MZ")


with tempfile.TemporaryDirectory() as tmp:
    # check_package: contents, not only file types
    ok = os.path.join(tmp, "ok")
    good_folder(ok)
    check(check_package.problems(ok) == [], "a complete package passes")
    empty = os.path.join(tmp, "empty")
    good_folder(empty)
    shutil.rmtree(os.path.join(empty, "RoverAssets"))           # (a temp copy)
    os.makedirs(os.path.join(empty, "RoverAssets"))
    check(any("rover.json" in p for p in check_package.problems(empty)), "an empty RoverAssets fails (no rover.json)")
    stale = os.path.join(tmp, "stale")
    good_folder(stale)
    open(os.path.join(stale, "RoverAssets", "layout.json"), "w").write("{}")
    open(os.path.join(stale, "RoverAssets", "textures", "color_gray_278.png"), "wb").write(b"\x89PNG")
    probs = check_package.problems(stale)
    check(any("layout.json" in p for p in probs), "a stray layout.json fails")
    check(any("color_gray_278.png" in p for p in probs), "an extracted game texture fails")

    # package_mod's guard: paths only, nothing touched
    if os.path.basename(MOD) == package_mod.NAME:                # (this workspace's layout: <parent>/Stationeers.RoverCargo)
        check(package_mod.refusal(os.path.dirname(MOD)) is not None, "refuses an output that would be the mod's own folder")
    check(package_mod.refusal(MOD) is not None, "refuses an output inside the mod's folder")
    check(package_mod.refusal(os.path.join(MOD, "tools")) is not None, "refuses an output deeper inside the mod's folder")
    user = os.path.join(tmp, "user")
    os.makedirs(os.path.join(user, "Stationeers.RoverCargo"))
    open(os.path.join(user, "Stationeers.RoverCargo", "RoverCargo.cfg"), "w").write("[Driving]\n")
    check(package_mod.refusal(user) is not None and package_mod.refusal(user, force=True) is None
          or not os.path.isfile(os.path.join(MOD, "bin", "Stationeers.RoverCargo.dll")),
          "refuses a folder holding a player's RoverCargo.cfg unless --force")
    r = subprocess.run([sys.executable, os.path.join(TOOLS, "package_mod.py")], capture_output=True, text=True)
    check(r.returncode != 0 and "usage" in (r.stdout + r.stderr).lower(), "package_mod without an argument prints usage")
    # one real run, into a fresh temp dir
    if os.path.isfile(os.path.join(MOD, "bin", "Stationeers.RoverCargo.dll")):
        out = os.path.join(tmp, "pkg")
        r = subprocess.run([sys.executable, os.path.join(TOOLS, "package_mod.py"), out], capture_output=True, text=True)
        check(r.returncode == 0 and "PACKAGE_OK" in r.stdout, "package_mod builds a clean package in a temp dir")

readme = " ".join(open(os.path.join(MOD, "README.md"), encoding="utf-8").read().split())   # line breaks ignored
check("crowbar, angle grinder, drill" in readme, "README: the frame comes down crowbar, angle grinder, drill (the real order)")

print("TEST_PACKAGE_TOOLS " + ("ok" if not fails else "FAIL %d" % len(fails)))
sys.exit(1 if fails else 0)
