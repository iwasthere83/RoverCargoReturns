using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Vehicles;
using Newtonsoft.Json.Linq;
using Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Stationeers.RoverCargo;

/// <summary>The vehicles' frames and kits (spec 2026-10-08): a clone of the Mk I RoverFrame per vehicle with build
/// states from the json "frame" (FrameData), a clone of the Mk I kit constructing it. RoverFrame's own completion spawns
/// the vehicle at the frame's pose (frame origin = vehicle origin, on the ground).</summary>
public sealed partial class CargoPrefabs
{
    public const string TrailerFrameName = "StructureTrailerCargo", TrailerKitName = "ItemKitTrailerCargo";
    public const string HabFrameName = "StructureTrailerHab", HabKitName = "ItemKitTrailerHab";

    private static readonly FieldInfo DrawDataField = typeof(BuildState).GetField("initialDrawData", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo Memberwise = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>One vehicle's frame and kit; never throws. The rover always ends with a frame and kit (saves hold them):
    /// its own stages, else the legacy frame. A trailer whose data or build fails stays spawnable, not printable.</summary>
    private void TryFrame(CargoLayout assets, Rover vehicle, string frameName, string kitName, MultiConstructor mk1Kit, RoverFrame mk1Frame, bool isRover)
    {
        try
        {
            var (spec, problems) = assets == null ? (null, new List<string> { "no frame data" })
                : FrameData.Read(assets.Json, assets.HasMesh, t => File.Exists(Path.Combine(assets.Dir, "textures", t + ".png")));
            if (spec != null)
                problems.AddRange(FrameData.ItemNames(spec).Where(n => Prefab.Find<Item>(n) == null).Select(n => $"item {n} not in this game"));
            switch (FrameData.Decide(isRover, problems.Count == 0))
            {
                case FrameOutcome.Staged:
                    var thumb = PngSprite(Path.Combine(assets.Dir, "textures", spec.Thumbnail + ".png"));
                    var frame = BuildStagedFrame(mk1Frame, vehicle, frameName, assets, spec, thumb);
                    var kit = BuildKit(mk1Kit, frame, kitName, thumb);
                    frame.BuildStates[0].Tool.ToolEntry = kit;
                    Register(frame);
                    Register(kit);
                    if (vehicle is CargoRover cr)                               // the drill takes it back to this frame
                    {
                        cr.TeardownFrame = frame;
                        cr.ExitTool = new ToolUse { ToolExit = frame.BuildStates[0].Tool.ToolExit, ExitTime = FrameData.TeardownSeconds };
                    }
                    break;
                case FrameOutcome.Legacy:
                    vehicle.ExitTool = new ToolUse();                          // no one-step deconstruct to the Mk I kit
                    _log.Add($"{frameName}: legacy frame ({string.Join("; ", problems)})");
                    RegisterLegacy(mk1Kit, mk1Frame, vehicle);
                    break;
                default:
                    vehicle.ExitTool = new ToolUse();                          // no one-step deconstruct to the Mk I kit
                    _log.Add($"{frameName}: not printable, {vehicle.PrefabName} still spawnable ({string.Join("; ", problems)})");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Add($"{frameName} build failed ({vehicle.PrefabName} still spawnable): {ex}");
            if (isRover && Prefab.Find(frameName) == null)
                try { RegisterLegacy(mk1Kit, mk1Frame, vehicle); } catch (Exception ex2) { _log.Add("legacy rover frame failed too: " + ex2); }
        }
    }

    private void RegisterLegacy(MultiConstructor mk1Kit, RoverFrame mk1Frame, Rover vehicle)
    {
        var frame = BuildLegacyFrame(mk1Frame, vehicle);
        Register(frame);
        Register(BuildKit(mk1Kit, frame));
    }

    private RoverFrame BuildStagedFrame(RoverFrame mk1Frame, Rover vehicle, string frameName, CargoLayout assets, FrameSpec spec, Sprite thumb)
    {
        var go = Object.Instantiate(mk1Frame.gameObject, Prefab.PrefabsGameObject.transform);
        go.name = frameName;
        var frame = go.GetComponent<RoverFrame>();
        frame.PrefabName = frameName;
        frame.PrefabHash = Animator.StringToHash(frameName);
        frame.RoverPrefab = vehicle;
        frame.Thumbnail = thumb ? thumb : frame.Thumbnail;
        if (frame.Thumbnails != null && frame.Thumbnails.Length > 0)          // per paint colour: ours, never the Mk I's
            frame.Thumbnails = Enumerable.Repeat(frame.Thumbnail, frame.Thumbnails.Length).ToArray();
        frame.Blueprint = null;                                              // not the Mk I's outline: the cursor ghosts the chassis state
        frame.Renderers.Clear();
        frame.RocketRenderers.Clear();
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);   // the Mk I's 3.6 m boxes
        foreach (var child in go.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
        var template = frame.BuildStates[frame.BuildStates.Count - 1];
        while (frame.BuildStates.Count < spec.States.Count) frame.BuildStates.Add((BuildState)Memberwise.Invoke(template, null));
        while (frame.BuildStates.Count > spec.States.Count) frame.BuildStates.RemoveAt(frame.BuildStates.Count - 1);
        var all = new Bounds();
        for (int i = 0; i < spec.States.Count; i++)
        {
            var s = spec.States[i];
            var bs = frame.BuildStates[i];
            var mesh = assets.Mesh(s.Mesh);
            var node = Child(go.transform, "Stage" + i);
            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var rend = node.gameObject.AddComponent<MeshRenderer>();
            rend.sharedMaterials = RoverMaterials(new JArray(s.Materials));
            rend.enabled = i == 0;                                            // as the Mk I prefab: the state 0 renderer on
            var box = node.gameObject.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;
            box.enabled = i == 0;
            bs.Visualizer = rend;
            bs.RendererInstance = null;
            bs.StateMeshes = new List<Mesh> { mesh };
            bs.Colliders = new List<Collider> { box };
            bs.LinkedGameObjects = new List<GameObject>();
            bs.Interactables = new List<Interactable>();
            bs.InteractableParent = null;
            bs.RenderMode = BuildStateRenderMode.OnMyState;
            DrawDataField.SetValue(bs, new DrawData { materials = new Material[0] });                // plan decision 6: no batched Mk I mesh
            var t = bs.Tool;
            bs.Tool = new ToolUse
            {
                ToolEntry = s.Entry.Prefab == FrameData.KitToken ? null : Prefab.Find<Item>(s.Entry.Prefab), EntryQuantity = s.Entry.Quantity,
                ToolEntry2 = s.Entry2 == null ? null : Prefab.Find<Item>(s.Entry2.Prefab), EntryQuantity2 = s.Entry2?.Quantity ?? 0,
                EntryTime = t?.EntryTime ?? 0.5f, ToolUseType = ToolUseType.Construction,
                ToolExit = Prefab.Find<Item>(s.Exit.Prefab), ExitTime = t?.ExitTime ?? 0.5f, ExitQuantity = s.Exit.Quantity,
            };
            if (i == 0) all = mesh.bounds; else all.Encapsulate(mesh.bounds);
        }
        // plan decision 7: an always-on, zero-area renderer spanning every state, so the grid footprint covers the
        // finished vehicle whenever the game computes the bounds
        var foot = Child(go.transform, "Footprint");
        var fm = new Mesh { name = frameName + "Footprint", vertices = new[] { all.min, all.max, all.min }, triangles = new[] { 0, 1, 2 } };
        fm.RecalculateBounds();
        foot.gameObject.AddComponent<MeshFilter>().sharedMesh = fm;
        foot.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(new JArray("ColorWhite"));
        frame.CachePrefabBounds();
        // the grid footprint, frozen: the game's own cell maths (GetLocalGridBounds) over every state's mesh widened by
        // half a cell on each side, at ratio 1 (the Mk I's 0.9 and its half-cell step inward would leave the ends and
        // wheels over walls); ForceGridBounds wins over any later bounds recompute (Prefab loading, inactive renderers)
        var cached = frame.Bounds;
        var wide = all;
        wide.Expand(new Vector3(frame.GridSize, 0f, frame.GridSize));
        frame.BoundsGridRatio = 1f;
        frame.Bounds = wide;
        frame.ForceGridBounds = new List<Grid3>((Grid3[])frame.GetLocalGridBounds());
        frame.Bounds = cached;
        frame.GridBounds = new GridBounds(frame);
        _log.Add($"{frameName} built: {spec.States.Count} states, footprint {all.size}, bounds {frame.Bounds.size}, {frame.ForceGridBounds.Count} grid cells");
        return frame;
    }

    private static Sprite PngSprite(string file)
    {
        if (!File.Exists(file)) return null;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = Path.GetFileNameWithoutExtension(file) };
        return tex.LoadImage(File.ReadAllBytes(file)) ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) : null;
    }
}
