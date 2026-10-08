"""Check a mod folder is fit to upload: only our shippable files, each asset folder's json present, the About files, a
thumbnail under 1 MB. Reads only.

    python tools/check_package.py <mod folder>
"""
import fnmatch
import os
import sys

TOP = {"Stationeers.RoverCargo.dll", "README.md", "About", "GameData", "RoverAssets", "TrailerAssets", "HabAssets"}
ABOUT = {"About.xml", "Preview.png", "thumb.png"}
REQUIRED = ["RoverAssets/rover.json", "TrailerAssets/trailer.json", "HabAssets/hab.json", "HabAssets/interior.json"]
ALLOWED = [                                    # every shipped file matches one of these (paths with "/")
    "Stationeers.RoverCargo.dll", "README.md", "About/About.xml", "About/Preview.png", "About/thumb.png",
    "GameData/*.xml", "GameData/Language/*.xml",
    "RoverAssets/rover.json", "TrailerAssets/trailer.json", "HabAssets/hab.json", "HabAssets/interior.json",
    "*Assets/meshes/*.rcm", "*Assets/textures/ItemKit*.png",
]


def problems(root):
    names = set(os.listdir(root))
    bad = [f"unexpected top-level {n}" for n in sorted(names - TOP)]
    bad += [f"missing {n}" for n in sorted(TOP - names)]
    about = os.path.join(root, "About")
    if os.path.isdir(about):
        bad += [f"About missing {n}" for n in sorted(ABOUT - set(os.listdir(about)))]
        t = os.path.join(about, "thumb.png")
        if os.path.exists(t) and os.path.getsize(t) >= 1_000_000:
            bad.append("thumb.png is 1 MB or more")
    bad += [f"missing {r}" for r in REQUIRED if not os.path.isfile(os.path.join(root, r))]
    for d, _, files in os.walk(root):
        for f in files:
            rel = os.path.relpath(os.path.join(d, f), root).replace(os.sep, "/")
            if not any(fnmatch.fnmatchcase(rel, pat) for pat in ALLOWED):
                bad.append("not shippable: " + rel)
    return bad


if __name__ == "__main__":
    bad = problems(sys.argv[1])
    print("PACKAGE_BAD " + "; ".join(bad) if bad else "PACKAGE_OK")
    sys.exit(1 if bad else 0)
