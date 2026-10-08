"""Blender <-> Rover (Cargo) Returns bridge. Run inside Blender (MCP or Scripting tab).

    export_mesh(obj, path)  -> writes an .rcm the mod can load (RoverAssets/, TrailerAssets/, HabAssets/ meshes)

Axes: Unity is left-handed Y-up, Blender right-handed Z-up. Conversion is the reflection
(x, y, z)_unity -> (x, z, y)_blender for points, normals and translations; triangle winding is
reversed; quaternions (x, y, z, w)_unity -> (w, -x, -z, -y)_blender. Export applies the inverse.
Units are metres in both.
"""
import struct

import bpy


def export_mesh(obj, path):
    """Write obj's evaluated mesh (modifiers applied, triangulated) in the mod's .rcm format, Unity axes."""
    import bmesh
    deps = bpy.context.evaluated_depsgraph_get()
    me = obj.evaluated_get(deps).to_mesh()
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()
    me.calc_normals_split() if hasattr(me, "calc_normals_split") else None
    uvl = me.uv_layers.active
    # one Unity vertex per loop keeps split normals / uv seams exact
    verts, norms, uvs, subs = [], [], [], {}
    for poly in me.polygons:
        idx = []
        for li in poly.loop_indices:
            loop = me.loops[li]
            v = me.vertices[loop.vertex_index].co
            nrm = loop.normal
            verts += [v.x, v.z, v.y]
            norms += [nrm.x, nrm.z, nrm.y]
            uvs += list(uvl.data[li].uv) if uvl else [0.0, 0.0]
            idx.append(len(verts) // 3 - 1)
        subs.setdefault(poly.material_index, []).extend([idx[0], idx[2], idx[1]])
    n = len(verts) // 3
    order = sorted(subs)
    with open(path, "wb") as fh:
        fh.write(b"RCM1")
        fh.write(struct.pack("<iii", n, len(order), 1 | 4))
        fh.write(struct.pack(f"<{len(verts)}f", *verts))
        fh.write(struct.pack(f"<{len(norms)}f", *norms))
        fh.write(struct.pack(f"<{len(uvs)}f", *uvs))
        for k in order:
            fh.write(struct.pack("<i", len(subs[k])))
            fh.write(struct.pack(f"<{len(subs[k])}i", *subs[k]))
    obj.evaluated_get(deps).to_mesh_clear()
    return {"vertices": n, "triangles": sum(len(v) for v in subs.values()) // 3, "submeshes": len(order)}
