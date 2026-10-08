"""Scan a folder before it goes public: no game files, no developer-machine paths, no session scratch paths, and none of
the private words listed in a file kept OUTSIDE the repo (one regex per line: names, emails, domains).

    python tools/scan_public.py <dir> [--private <file>]
"""
import os
import re
import sys

BAD_EXT = {".blend", ".blend1", ".obj", ".fbx", ".unity3d", ".assets", ".cfg", ".bak"}
BAD_NAMES = {"layout.json", "extract_rover_cargo.py"}
BAD_TEXT = [r"[A-Z]:\\Dev\\", r"\\Users\\[^\\\s]+\\", r"AppData\\Local\\Temp\\claude", r"Assets\\textures",
            r"steamapps\\content"]
TEXT_EXT = {".cs", ".py", ".md", ".xml", ".json", ".csproj", ".txt", ".gitignore"}
SELF = os.path.abspath(__file__)


def problems(root, private=()):
    patterns = BAD_TEXT + list(private)
    bad = []
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if x not in (".git", "bin", "obj")]
        for f in files:
            p = os.path.join(d, f)
            rel = os.path.relpath(p, root)
            ext = os.path.splitext(f)[1].lower()
            if ext in BAD_EXT or f in BAD_NAMES:
                bad.append("file: " + rel)
            elif (ext in TEXT_EXT or f == "LICENSE") and os.path.abspath(p) != SELF:
                text = open(p, encoding="utf-8", errors="replace").read()
                bad += [f"text {pat!r}: {rel}" for pat in patterns if re.search(pat, text, re.I)]
    return bad


if __name__ == "__main__":
    private = []
    if "--private" in sys.argv:
        with open(sys.argv[sys.argv.index("--private") + 1], encoding="utf-8") as fh:
            private = [ln.strip() for ln in fh if ln.strip() and not ln.startswith("#")]
    bad = problems(sys.argv[1], private)
    print("SCAN_BAD " + "; ".join(bad) if bad else "SCAN_OK")
    sys.exit(1 if bad else 0)
