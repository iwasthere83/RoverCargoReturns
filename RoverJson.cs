using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Stationeers.RoverCargo;

/// <summary>
/// The rover.json checks the original rover builder runs before it builds anything (pure: Newtonsoft only, tested in
/// _tools/HabTests against the exported file and broken copies of it). Never throws: a key of the wrong JSON type
/// counts as missing, a missing mesh name is named, and anything else unreadable is one problem line.
/// </summary>
public static class RoverJson
{
    private static readonly Dictionary<string, JTokenType[]> KeyTypes = new()
    {
        ["version"] = new[] { JTokenType.Integer, JTokenType.Float, JTokenType.String },
        ["body"] = new[] { JTokenType.Object }, ["glass"] = new[] { JTokenType.Object }, ["parts"] = new[] { JTokenType.Array },
        ["colliders"] = new[] { JTokenType.Array }, ["autoCom"] = new[] { JTokenType.Array },
        ["mass"] = new[] { JTokenType.Integer, JTokenType.Float }, ["wheelCollider"] = new[] { JTokenType.Object },
        ["wheels"] = new[] { JTokenType.Array }, ["anchors"] = new[] { JTokenType.Object }, ["fields"] = new[] { JTokenType.Object },
    };

    private static bool HasKey(JObject j, string k) =>
        j[k] is JToken t && (KeyTypes.TryGetValue(k, out var ok) ? ok.Contains(t.Type) : t.Type != JTokenType.Null);

    private static bool Vec3(JToken t) => t is JArray a && a.Count == 3 && a.All(v => v.Type is JTokenType.Integer or JTokenType.Float);

    private static bool HasAnchor(JObject j, string a) => j["anchors"] is JObject all && all[a] is JObject o && Vec3(o["pos"]) && Vec3(o["rot"]);

    private static string Str(JToken t) => t is JValue v && v.Type == JTokenType.String ? (string)v : null;

    /// <summary>Every mesh reference: body, glass and the front lamps' glow (both optional), parts, each wheel's tyre and
    /// arm, and each shock's body, rod and spring; mesh = null where the name is missing or not a string.</summary>
    public static IEnumerable<(string What, string Mesh)> MeshRefs(JObject j)
    {
        yield return ("body", Str((j["body"] as JObject)?["mesh"]));
        if ((j["glass"] as JObject)?["mesh"] is JToken g && g.Type != JTokenType.Null) yield return ("glass", Str(g));
        if ((j["glow"] as JObject)?["mesh"] is JToken gl && gl.Type != JTokenType.Null) yield return ("glow", Str(gl));
        int i = 0;
        foreach (var p in (j["parts"] as JArray) ?? new JArray()) yield return ($"parts[{i++}]", Str((p as JObject)?["mesh"]));
        i = 0;
        foreach (var w in (j["wheels"] as JArray) ?? new JArray())
        {
            yield return ($"wheels[{i}].tyre", Str((w as JObject)?["tyre"]));
            yield return ($"wheels[{i++}].arm", Str((w as JObject)?["arm"]));
        }
        i = 0;
        foreach (var s in (j["shocks"] as JArray) ?? new JArray())
        {
            foreach (var k in new[] { "body", "rod", "spring" })
                yield return ($"shocks[{i}].{k}", Str((s as JObject)?[k]));
            i++;
        }
    }

    /// <summary>The mesh files the rover needs (names only).</summary>
    public static IEnumerable<string> Meshes(JObject j) => MeshRefs(j).Select(r => r.Mesh).Where(m => m != null);

    /// <summary>The upgrade groups (rover.json "upgrades"."rover") with a part whose mesh is unnamed or missing: the
    /// builder leaves those upgrades out (refused in game, "no parts") and builds the rest. Never part of Problems, so
    /// a missing upgrade mesh never stops the rover. An unreadable "upgrades" key counts as one group "?".</summary>
    public static List<string> IncompleteUpgradeGroups(JObject j, Func<string, bool> meshExists)
    {
        var bad = new List<string>();
        try
        {
            foreach (var t in (j?["upgrades"]?["rover"] as JArray) ?? new JArray())
            {
                var o = t as JObject;
                string group = Str(o?["group"]) ?? "?", mesh = Str(o?["mesh"]);
                if ((mesh == null || !meshExists(mesh)) && !bad.Contains(group)) bad.Add(group);
            }
        }
        catch (Exception) { bad.Add("?"); }
        return bad;
    }

    /// <summary>Everything that stops the rover from building, for the log. Empty = buildable.</summary>
    public static List<string> Problems(JObject J, Func<string, bool> meshExists)
    {
        try
        {
            if (J == null) return new List<string> { "rover.json not loaded" };
            var refs = MeshRefs(J).ToList();
            var p = RoverRules.Problems(k => HasKey(J, k), a => HasAnchor(J, a), refs.Where(r => r.Mesh != null).Select(r => r.Mesh), meshExists);
            foreach (var r in refs) if (r.Mesh == null) p.Add("mesh name (" + r.What + ")");
            var parts = (J["parts"] as JArray) ?? new JArray();
            foreach (var d in RoverRules.Doors)                      // each bay door leaf turns about its hinge
                if (!parts.Any(t => t is JObject o && Str(o["name"]) == d.Name && Str(o["mesh"]) != null && Vec3(o["hinge"])))
                    p.Add("door part " + d.Name + " (its mesh and hinge)");
            if (J["shocks"] is JToken sh && sh.Type is not (JTokenType.Array or JTokenType.Null)) p.Add("shocks (not a list)");
            int si = 0;
            foreach (var s in (J["shocks"] as JArray) ?? new JArray())        // each shock: its wheel, both eyes, its spring's seats
            {
                if (!(s is JObject o && Str(o["wheel"]) != null && Vec3(o["top"]) && Vec3(o["bottom"])
                      && o["springAt"] is JArray at && at.Count == 2 && at.All(v => v.Type is JTokenType.Integer or JTokenType.Float)))
                    p.Add($"shock {si} (its wheel, mounts and spring seats)");
                si++;
            }
            return p;
        }
        catch (Exception e) { return new List<string> { "rover.json unreadable: " + e.Message }; }
    }
}
