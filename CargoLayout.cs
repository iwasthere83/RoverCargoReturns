using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stationeers.RoverCargo;

/// <summary>One of the mod's asset folders (RoverAssets, TrailerAssets, HabAssets): its json and its meshes/*.rcm, written
/// by the Blender export tools (tools/export_all.py).</summary>
public sealed class CargoLayout
{
    public readonly string Dir;
    public readonly JObject Json;
    private readonly Dictionary<string, Mesh> _meshes = new();

    private CargoLayout(string dir, JObject json)
    {
        Dir = dir;
        Json = json;
    }

    /// <summary>The folder's json (error: why it is missing or unreadable; never throws).</summary>
    public static CargoLayout ForFolder(string dir, string jsonFile, out string error)
    {
        error = null;
        var file = Path.Combine(dir, jsonFile);
        if (!File.Exists(file)) { error = "missing " + file; return null; }
        try { return new CargoLayout(dir, JObject.Parse(File.ReadAllText(file))); }
        catch (Exception ex) { error = "unreadable " + file + ": " + ex.Message; return null; }
    }

    public static Vector3 V3(JToken t) => t == null ? Vector3.zero : new Vector3((float)t["x"], (float)t["y"], (float)t["z"]);

    public Mesh Mesh(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_meshes.TryGetValue(name, out var m)) return m;
        var file = Path.Combine(Dir, "meshes", name + ".rcm");
        using var br = new BinaryReader(File.OpenRead(file));
        if (new string(br.ReadChars(4)) != "RCM1") throw new InvalidDataException($"{file}: not an RCM1 mesh");
        int n = br.ReadInt32(), subs = br.ReadInt32(), flags = br.ReadInt32();
        Vector3[] V(int count) { var a = new Vector3[count]; for (int i = 0; i < count; i++) a[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); return a; }
        Vector2[] U(int count) { var a = new Vector2[count]; for (int i = 0; i < count; i++) a[i] = new Vector2(br.ReadSingle(), br.ReadSingle()); return a; }
        m = new Mesh { name = name };
        if (n > 65535) m.indexFormat = IndexFormat.UInt32;
        m.vertices = V(n);
        if ((flags & 1) != 0) m.normals = V(n);
        if ((flags & 2) != 0)
        {
            var t = new Vector4[n];
            for (int i = 0; i < n; i++) t[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            m.tangents = t;
        }
        if ((flags & 4) != 0) m.uv = U(n);
        if ((flags & 8) != 0) m.uv2 = U(n);
        m.subMeshCount = subs;
        for (int s = 0; s < subs; s++)
        {
            int count = br.ReadInt32();
            var idx = new int[count];
            for (int i = 0; i < count; i++) idx[i] = br.ReadInt32();
            m.SetTriangles(idx, s);
        }
        if ((flags & 1) == 0) m.RecalculateNormals();
        m.RecalculateBounds();
        _meshes[name] = m;
        return m;
    }

    public bool HasMesh(string name) => !string.IsNullOrEmpty(name) && (_meshes.ContainsKey(name) || File.Exists(Path.Combine(Dir, "meshes", name + ".rcm")));

}
