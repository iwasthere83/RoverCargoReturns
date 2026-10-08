using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Stationeers.RoverCargo;

// plain classes, not records: the mod targets a framework without IsExternalInit
public sealed class FrameItem
{
    public FrameItem(string prefab, int quantity) { Prefab = prefab; Quantity = quantity; }
    public string Prefab { get; }
    public int Quantity { get; }
}

public sealed class FrameState
{
    public FrameState(string mesh, string[] materials, FrameItem entry, FrameItem entry2, FrameItem exit)
    { Mesh = mesh; Materials = materials; Entry = entry; Entry2 = entry2; Exit = exit; }
    public string Mesh { get; }
    public string[] Materials { get; }
    public FrameItem Entry { get; }
    public FrameItem Entry2 { get; }
    public FrameItem Exit { get; }
}

public sealed class FrameSpec
{
    public FrameSpec(string thumbnail, IReadOnlyList<FrameState> states) { Thumbnail = thumbnail; States = states; }
    public string Thumbnail { get; }
    public IReadOnlyList<FrameState> States { get; }
}
public enum FrameOutcome { Staged, Legacy, NotPrintable }

/// <summary>A vehicle json's "frame" (tools/blender_build_stages.export_frame): per build state the mesh, its materials,
/// the item(s) it takes and the tool that undoes it; the kit's thumbnail. Pure (Newtonsoft only, tested in
/// _tools/HabTests). Never throws: every gap is one named problem line.</summary>
public static class FrameData
{
    public const string KitToken = "@kit";      // state 0's entry: the vehicle's own kit (the builder sets it)

    public static (FrameSpec Spec, List<string> Problems) Read(JObject j, Func<string, bool> meshExists, Func<string, bool> thumbnailExists)
    {
        var problems = new List<string>();
        try
        {
            if (j?["frame"] is not JObject f) { problems.Add("no \"frame\" in the json"); return (null, problems); }
            string thumb = Str(f["thumbnail"]);
            if (thumb == null) problems.Add("frame: no thumbnail name");
            else if (!thumbnailExists(thumb)) problems.Add($"frame: thumbnail textures/{thumb}.png missing");
            var states = new List<FrameState>();
            if (f["states"] is not JArray arr || arr.Count < 2) { problems.Add("frame: fewer than 2 states"); return (null, problems); }
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JObject s) { problems.Add($"frame state {i}: not an object"); continue; }
                string mesh = Str(s["mesh"]);
                if (mesh == null) problems.Add($"frame state {i}: no mesh");
                else if (!meshExists(mesh)) problems.Add($"frame state {i}: mesh {mesh} missing");
                var mats = (s["materials"] as JArray)?.Select(Str).ToArray();
                if (mats == null || mats.Length == 0 || mats.Any(m => m == null)) problems.Add($"frame state {i}: materials");
                var entry = Item(s["entry"], i, "entry", problems, required: true);
                var entry2 = s["entry2"] == null || s["entry2"].Type == JTokenType.Null ? null : Item(s["entry2"], i, "entry2", problems, required: true);
                var exit = Item(s["exit"], i, "exit", problems, required: true);
                if ((i == 0) != (entry?.Prefab == KitToken)) problems.Add($"frame state {i}: only state 0 is placed from the kit");
                states.Add(new FrameState(mesh, mats ?? new string[0], entry, entry2, exit));
            }
            return (new FrameSpec(thumb, states), problems);
        }
        catch (Exception ex)
        {
            problems.Add("frame unreadable: " + ex.Message);
            return (null, problems);
        }
    }

    /// <summary>The rover always gets a frame and kit (saves hold StructureRover / ItemKitRoverFrame): its own stages
    /// with good data, else the legacy frame. A trailer without good data is spawnable, not printable.</summary>
    public static FrameOutcome Decide(bool isRover, bool dataOk) =>
        dataOk ? FrameOutcome.Staged : isRover ? FrameOutcome.Legacy : FrameOutcome.NotPrintable;

    /// <summary>A finished vehicle torn down with the drill comes back as its frame at the stage before last (the user,
    /// check-in 3: the build in reverse).</summary>
    public static int TeardownState(int stateCount) => stateCount - 2;

    public const float TeardownMaxSpeed = 0.5f;     // m/s: a vehicle must be standing to come apart
    public const float TeardownSeconds = 3f;        // the drill on a finished vehicle (it drops everything aboard)

    /// <summary>Why a finished vehicle cannot come apart now, or null.</summary>
    public static string TeardownRefusal(bool towing, bool hitched, bool habOut, float speed) =>
        towing || hitched ? "Unhitch the trailer first"
        : habOut ? "Stow the hab first"
        : speed > TeardownMaxSpeed ? "Stop it first"
        : null;

    public static IEnumerable<string> ItemNames(FrameSpec spec) =>
        spec.States.SelectMany(s => new[] { s.Entry, s.Entry2, s.Exit }).Where(x => x != null && x.Prefab != KitToken)
            .Select(x => x.Prefab).Distinct();

    private static string Str(JToken t) => t is JValue v && v.Type == JTokenType.String ? (string)v : null;

    private static FrameItem Item(JToken t, int i, string what, List<string> problems, bool required)
    {
        if (t is JArray a && a.Count == 2 && Str(a[0]) is string p && a[1].Type == JTokenType.Integer && (int)a[1] >= 0)
            return new FrameItem(p, (int)a[1]);
        if (required) problems.Add($"frame state {i}: {what} is not [prefab, quantity]");
        return null;
    }
}
