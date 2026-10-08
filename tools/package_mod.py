"""Build the clean upload folder from the repo: <out>/Stationeers.RoverCargo with the DLL (bin/), About, GameData, the
three asset folders and the README; then run check_package on it.

    python tools/package_mod.py <out dir> [--force]

Refuses (before deleting anything) when the output would be the mod's own folder, inside it or around it; when the
output folder holds a player's RoverCargo.cfg (the installed mod) unless --force; and when the DLL is not built.
"""
import os
import shutil
import subprocess
import sys

MOD = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NAME = "Stationeers.RoverCargo"


def _inside(a, b):
    """a is b or inside b (resolved, case-insensitive on Windows)."""
    a, b = os.path.normcase(os.path.realpath(a)), os.path.normcase(os.path.realpath(b))
    return a == b or a.startswith(b.rstrip(os.sep) + os.sep)


def refusal(out_root, mod_dir=MOD, force=False):
    """Why packaging into out_root must not run, or None. Pure path checks: nothing is touched."""
    out = os.path.join(out_root, NAME)
    if _inside(out, mod_dir) or _inside(mod_dir, out):
        return f"refusing: {out} is or contains the mod's own folder {mod_dir}"
    if os.path.exists(os.path.join(out, "RoverCargo.cfg")) and not force:
        return f"refusing: {out} holds a player's RoverCargo.cfg (an installed mod); use --force to replace it"
    if not os.path.isfile(os.path.join(mod_dir, "bin", NAME + ".dll")):
        return f"refusing: {os.path.join(mod_dir, 'bin', NAME + '.dll')} is not built (dotnet build -c Release)"
    return None


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    if len(args) != 1:
        print(__doc__)
        print("usage: python tools/package_mod.py <out dir> [--force]")
        return 2
    why = refusal(args[0], force="--force" in argv)
    if why:
        print(why)
        return 1
    out = os.path.join(args[0], NAME)
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(out)
    shutil.copy2(os.path.join(MOD, "bin", NAME + ".dll"), out)
    shutil.copy2(os.path.join(MOD, "README.md"), out)
    for d in ("About", "GameData", "RoverAssets", "TrailerAssets", "HabAssets"):
        shutil.copytree(os.path.join(MOD, d), os.path.join(out, d))
    return subprocess.call([sys.executable, os.path.join(MOD, "tools", "check_package.py"), out])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
