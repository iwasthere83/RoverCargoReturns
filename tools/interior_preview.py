"""Bakes the hab furniture (interior.json) out of the game's own prefabs into OBJ files in Blender coordinates, for a
layout preview beside the hab model (tools/blender_trailer_model.py). Reads the local game install with UnityPy
(pip install UnityPy==1.25.3). The OBJs hold game geometry: they go to art/ (git-ignored), never into the repo or mod.
Also cross-checks each prop's root-mesh bounds against interior.json (the numbers the offline checks rely on).
    python interior_preview.py "<Stationeers>/rocketstation_Data" <HabAssets dir> <out dir>"""
import json, os, re, sys

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hab_interior import mul, rotate

# same nodes the runtime copier skips (HabInterior.Skip): damage states, shadow casters, outlines, interaction boxes
SKIP = re.compile(r"Destroyed|Shadows|outline|Trigger|BuildState|TerrainLight|lodFlare|LOD[1-9]|^BoxCollider(Slot|OnOff)", re.I)
HAB_ORIGIN_Y = -2.70 - 4.45        # blender_trailer_model.HAB_ORIGIN_Y
SWITCH_PLATE_X = -1.0 + 0.012      # front face of the light switch's wall plate (blender_trailer_model, left wall + 12 mm)


def components(go):
    out = {}
    for c in go.m_Component:
        ptr = c.component if hasattr(c, "component") else c
        try:
            o = ptr.deref()
        except Exception:
            continue
        out.setdefault(o.type.name, []).append(o)
    return out


def prefab_roots(env, names):
    """name -> root GameObject; the fullest root wins (the placement cursor copies share the name)."""
    best = {}
    for obj in env.objects:
        if obj.type.name != "GameObject":
            continue
        try:
            go = obj.read()
        except Exception:
            continue
        if go.m_Name not in names:
            continue
        t = components(go)["Transform"][0].read()
        if getattr(t.m_Father, "path_id", 0) != 0:
            continue
        if go.m_Name not in best or len(t.m_Children) > best[go.m_Name][1]:
            best[go.m_Name] = (go, len(t.m_Children))
    return {k: v[0] for k, v in best.items()}


def bake(go, pos, q, scale, verts, faces, root_bounds=None, over=None, path="", children=True, trim=None, trimmed=None):
    """Append go's meshes (and its active children's, unless children=False) in trailer space; over = {child path:
    quaternion} replaces those nodes' local rotations (like interior.json nodeRot)."""
    cs = components(go)
    if cs.get("MeshFilter") and cs.get("MeshRenderer"):
        r = cs["MeshRenderer"][0].read()
        if getattr(r, "m_Enabled", 1) and getattr(r, "m_CastShadows", 1) != 3:
            m = cs["MeshFilter"][0].read().m_Mesh.read()
            h = MeshHandler(m)
            h.process()
            base = len(verts)
            for v in h.m_Vertices:
                w = rotate(q, (v[0] * scale[0], v[1] * scale[1], v[2] * scale[2]))
                verts.append((pos[0] + w[0], pos[1] + w[1], pos[2] + w[2]))
            lv = h.m_Vertices
            if trim and path == "" and trimmed is not None and not getattr(m, "m_IsReadable", True):
                trimmed.append(None)                 # the game cannot trim this mesh at runtime (not readable)
            def inside(i):      # prefab-local, the same rule as HabRules.TrimTriangles (all three vertices in one box)
                return any(all(b["min"][k] <= lv[i][k] <= b["max"][k] for k in range(3)) for b in trim)

            def cut(a, b, c):
                return any(all(all(bx["min"][k] <= lv[i][k] <= bx["max"][k] for k in range(3)) for i in (a, b, c)) for bx in trim)
            for sub in h.get_triangles():
                for a, b, c in sub:
                    if trim and path == "" and cut(a, b, c):
                        if trimmed is not None:
                            trimmed.append((lv[a], lv[b], lv[c]))
                        continue
                    faces.append((base + a, base + b, base + c))
            if root_bounds is not None:
                a = m.m_LocalAABB
                root_bounds.append(((a.m_Center.x, a.m_Center.y, a.m_Center.z),
                                    (2 * a.m_Extent.x, 2 * a.m_Extent.y, 2 * a.m_Extent.z)))
    tr = (cs.get("Transform") or cs.get("RectTransform") or [None])[0]
    if tr is None or not children:
        return
    for ch in tr.read().m_Children:
        t = ch.read()
        cgo = t.m_GameObject.read()
        if not cgo.m_IsActive or SKIP.search(cgo.m_Name):
            continue
        cpath = (path + "/" if path else "") + cgo.m_Name
        lp, ls = t.m_LocalPosition, t.m_LocalScale
        lr = tuple(over[cpath]) if over and cpath in over else (t.m_LocalRotation.x, t.m_LocalRotation.y, t.m_LocalRotation.z, t.m_LocalRotation.w)
        cp = rotate(q, (lp.x * scale[0], lp.y * scale[1], lp.z * scale[2]))
        bake(cgo, (pos[0] + cp[0], pos[1] + cp[1], pos[2] + cp[2]), mul(q, lr),
             (scale[0] * ls.x, scale[1] * ls.y, scale[2] * ls.z), verts, faces, over=over, path=cpath)


SLOT_NODE = re.compile(r"Slot(\d+)")
SLOT_COLLIDERS = {"BoxCollider", "SphereCollider"}      # the click boxes HabInterior.CopySlots can copy for a slot


def slot_nodes(go):
    """Distinct slot numbers among the prefab's direct children (BoxColliderTriggerSlot7, Slot2BoxCollider..., ...)."""
    nums = set()
    for ch in components(go)["Transform"][0].read().m_Children:
        m = SLOT_NODE.search(ch.read().m_GameObject.read().m_Name)
        if m:
            nums.add(int(m.group(1)))
    return nums


def find_child(go, path):
    """Child by path (a/b/c): its GameObject and Transform."""
    t = None
    for name in path.split("/"):
        nxt = None
        for ch in components(go)["Transform"][0].read().m_Children:
            cgo = ch.read().m_GameObject.read()
            if cgo.m_Name == name:
                nxt = (cgo, ch.read())
                break
        if nxt is None:
            return None, None
        go, t = nxt
    return go, t


def json_aabb(p, pos):
    """The offline checks' box for a prop (interior.json mesh bounds, rotated), in trailer space."""
    c, s = p["mesh"]["center"], p["mesh"]["size"]
    pts = [rotate(tuple(p["rot"]), (c[0] + dx * s[0], c[1] + dy * s[1], c[2] + dz * s[2]))
           for dx in (-.5, .5) for dy in (-.5, .5) for dz in (-.5, .5)]
    return [pos[i] + min(q[i] for q in pts) for i in range(3)], [pos[i] + max(q[i] for q in pts) for i in range(3)]


def main():
    data, habdir, out = sys.argv[1:4]
    hab = json.load(open(os.path.join(habdir, "hab.json"), encoding="utf-8"))
    j = json.load(open(os.path.join(habdir, "interior.json"), encoding="utf-8"))
    part = next(p for p in hab["parts"] if p["name"] == "SlideOut")
    home = part["pos"]
    deployed = [home[i] + hab["deploy"]["slideTravel"][i] for i in range(3)]
    env = UnityPy.load(os.path.join(data, "resources.assets"))
    roots = prefab_roots(env, {p["prefab"] for p in j["props"] if p["prefab"]})
    problems = [f"prefab {p['prefab']} not in the game files" for p in j["props"] if p["prefab"] and p["prefab"] not in roots]
    os.makedirs(out, exist_ok=True)
    for label, slide in (("deployed", deployed), ("stowed", home)):
        lines = []
        nv = 0
        for p in j["props"]:
            go = roots.get(p["prefab"])
            if go is None:
                continue
            if label == "deployed" and "slotCount" in p and p["role"] not in ("bunk", "rack"):
                n = len(slot_nodes(go))
                if n != p["slotCount"]:
                    problems.append(f"{p['name']}: game prefab has {n} slots, interior.json says {p['slotCount']}")
                for ch in components(go)["Transform"][0].read().m_Children:
                    cgo = ch.read().m_GameObject.read()
                    kinds = {k for k in components(cgo) if k.endswith("Collider")}
                    if SLOT_NODE.search(cgo.m_Name) and kinds and not kinds & SLOT_COLLIDERS:
                        problems.append(f"{p['name']}: slot node {cgo.m_Name} has {sorted(kinds)}, HabInterior.CopySlots copies {sorted(SLOT_COLLIDERS)}")
            base = slide if p["parent"] == "SlideOut" else (0.0, 0.0, 0.0)
            pos = tuple(p["pos"][i] + base[i] for i in range(3))
            q = tuple(p["rot"])
            verts, faces, rb = [], [], []
            if p.get("node"):
                go, t = find_child(go, p["node"])
                if go is None:
                    problems.append(f"{p['name']}: node {p.get('node')} missing")
                    continue
                r = t.m_LocalRotation
                cut = []
                bake(go, pos, mul(q, (r.x, r.y, r.z, r.w)), (1, 1, 1), verts, faces, over=p.get("nodeRot"), trim=p.get("trim"), trimmed=cut)
                if label == "deployed" and p.get("trim"):
                    if None in cut:
                        problems.append(f"{p['name']}: its game mesh is not readable, so it cannot be trimmed at runtime (it would vanish)")
                    cut = [t for t in cut if t is not None]
                    high = max((v[1] for t in cut for v in t), default=0.0)
                    if not 20 <= len(cut) <= 400 or high > 0.30:
                        problems.append(f"{p['name']}: trim removes {len(cut)} triangles (want 20-400), highest removed y {high:.3f} (want <= 0.30)")
                if label == "deployed" and p["role"] == "switch":
                    # its base plate must lie flat on the black wall plate, flush with its front face: the game's switch
                    # sits on its console's side, so a console's wall pose leaves it sticking out edgeways
                    base = []
                    bake(go, pos, mul(q, (r.x, r.y, r.z, r.w)), (1, 1, 1), base, [], children=False)
                    xs = [v[0] for v in base]
                    if not base or max(xs) - min(xs) > 0.015 or abs(min(xs) - SWITCH_PLATE_X) > 0.003:
                        problems.append(f"{p['name']}: its base plate spans x {min(xs, default=0):.3f}..{max(xs, default=0):.3f}, "
                                        f"not flat on the wall plate's front face (x {SWITCH_PLATE_X:.3f})")
                if label == "deployed" and verts:
                    lo, hi = json_aabb(p, pos)
                    blo = [min(v[i] for v in verts) for i in range(3)]
                    bhi = [max(v[i] for v in verts) for i in range(3)]
                    if any(blo[i] < lo[i] - 0.02 or bhi[i] > hi[i] + 0.02 for i in range(3)):
                        problems.append(f"{p['name']}: baked bounds {[round(v, 3) for v in blo]}..{[round(v, 3) for v in bhi]} "
                                        f"exceed interior.json's {[round(v, 3) for v in lo]}..{[round(v, 3) for v in hi]}")
            else:
                cut = []
                bake(go, pos, q, (1, 1, 1), verts, faces, rb, over=p.get("nodeRot"), trim=p.get("trim"), trimmed=cut)
                if label == "deployed" and p.get("trim"):
                    # the trim must take a small part off the base only (the suit storage's waste-pipe stub)
                    if None in cut:
                        problems.append(f"{p['name']}: its game mesh is not readable, so it cannot be trimmed at runtime (it would vanish)")
                    cut = [t for t in cut if t is not None]
                    high = max((v[1] for t in cut for v in t), default=0.0)
                    if not 20 <= len(cut) <= 400 or high > 0.30:
                        problems.append(f"{p['name']}: trim removes {len(cut)} triangles (want 20-400), highest removed y {high:.3f} (want <= 0.30)")
                if label == "deployed" and rb:
                    (c, s), (jc, js) = rb[0], (p["mesh"]["center"], p["mesh"]["size"])
                    if any(abs(c[i] - jc[i]) > 0.011 or abs(s[i] - js[i]) > 0.011 for i in range(3)):
                        problems.append(f"{p['name']}: game mesh bounds c={c} s={s} differ from interior.json c={jc} s={js}")
            lines.append(f"o {p['name']}")
            lines += [f"v {x:.5f} {HAB_ORIGIN_Y + z:.5f} {h:.5f}" for x, h, z in verts]      # Unity (x,h,z) -> Blender
            lines += [f"f {a + 1 + nv} {b + 1 + nv} {c + 1 + nv}" for a, b, c in faces]
            nv += len(verts)
        path = os.path.join(out, f"interior_{label}.obj")
        with open(path, "w") as f:
            f.write("\n".join(lines) + "\n")
        print(f"{path}: {nv} vertices")
    if problems:
        print("FAIL:\n  " + "\n  ".join(problems))
        sys.exit(1)
    print(f"OK: {len(j['props'])} props baked")


if __name__ == "__main__":
    main()
