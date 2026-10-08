using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Stationeers.RoverCargo;

/// <summary>
/// The hab's furniture from HabAssets/interior.json (written by tools/hab_interior.py, checked by
/// tools/check_interior.py). Each prop is the game's own prefab model: its finished-state meshes, materials and solid
/// box colliders are copied from the loaded prefab at runtime, nothing is shipped. Roles add behaviour: "light"
/// (ceiling light on the hab battery via the Button2 switch), "beacon" (spins while the airlock cycles), "switch" (the
/// console's own switch lever), "bunk" (a bed you lie in, parented to the slide-out), "roof" (outside, e.g. the solar
/// panels). "node" copies only that part of a prefab, "nodeRot" re-poses parts inside the copy.
/// </summary>
internal static class HabInterior
{
    // damage states, shadow casters, blueprint outlines, interaction boxes, lower LODs, extra lights and empty marker
    // nodes (the bunk makes its own markers) are not furniture
    private static readonly Regex Skip = new("Destroyed|Shadows|outline|Trigger|BuildState|TerrainLight|lodFlare|LOD[1-9]|^BoxCollider(Slot|OnOff)|^(CameraPoint|ExistPosition|PlayerAnimationPosition|OperatingAudio|SoundPosition)$",
        RegexOptions.IgnoreCase);

    public static string Build(CargoHab hab, JObject j)
    {
        int props = 0, colliders = 0;
        var missing = new List<string>();
        var roots = new Dictionary<string, Transform> { ["hab"] = NewNode(hab.transform, "Interior") };
        if (hab.SlideOut) roots["SlideOut"] = NewNode(hab.SlideOut, "Interior");
        hab.InteriorRoots = new List<GameObject>();
        foreach (var r in roots.Values) hab.InteriorRoots.Add(r.gameObject);
        var slotWork = new List<(Transform node, Thing src, JObject p)>();   // slot props, added after the visuals
        foreach (JObject p in (j["props"] as JArray) ?? new JArray())
        {
            string name = (string)p["name"], role = (string)p["role"] ?? "prop";
            var src = Prefab.Find((string)p["prefab"]) as Thing;
            if (!roots.TryGetValue((string)p["parent"] ?? "hab", out var parent)) { missing.Add(name); continue; }
            if (role == "screen")                                   // our own status screen (mesh in the hab body): the page's face
            {
                hab.StatusScreenAt = Place(NewNode(parent, name), p);
                continue;
            }
            if (role == "rack")                                     // our own rack: its mesh is part of the hab body
            {
                slotWork.Add((Place(NewNode(parent, name), p), null, p));
                continue;
            }
            if (!src)
            {
                missing.Add(name);
                // slots are saved by index: keep them (bare) so later slots never shift
                if (p["slotBase"] != null) slotWork.Add((Place(NewNode(parent, name), p), null, p));
                continue;
            }
            var node = Place(NewNode(parent, name), p);
            var from = src.transform;
            if (p["node"] != null)                                  // only part of the prefab (e.g. a panel without its pole)
            {
                from = src.transform.Find((string)p["node"]);
                if (!from) { missing.Add(name); Object.DestroyImmediate(node.gameObject); continue; }
                node.localRotation *= from.localRotation;          // as it sits on its own prefab
            }
            colliders += Copy(from, node);
            if (p["trim"] is JArray trim) TrimRootMesh(node, trim);
            if (p["nodeRot"] is JObject poses)                      // re-posed parts inside the copy (e.g. a tilted panel)
                foreach (var kv in poses)
                    if (node.Find(kv.Key) is { } t) t.localRotation = Q(kv.Value);
            if (role == "switch") AddSwitch(hab, node);
            if (p["slotBase"] != null) slotWork.Add((node, src, p));
            props++;
            switch (role)
            {
                case "light": CopyLights(src.transform, node, hab.CabinLightList, "Light"); break;
                case "beacon":
                    hab.Beacon = FindDeep(node, "Rotater");
                    CopyLights(src.transform, node, hab.BeaconLights, "Light1", "Light2", "Light3");
                    break;
                case "console": hab.ConsoleNodes.Add(node); break;
                case "roof":                                        // the solar heads: they charge the hab (CargoHab.SolarStep)
                    hab.RoofPanels.Add(node);
                    if (hab.SolarPanelArea <= 0f)
                        hab.SolarPanelArea = src is Assets.Scripts.Objects.Electrical.SolarPanel sp ? sp.PanelSize.x * sp.PanelSize.y : 1.71f * 1.70f;
                    break;
                case "shower":
                    hab.ShowerNode = node;
                    hab.ShowerValve = node.Find("ValveHandle");
                    hab.ShowerWater = AddLoopSound(src, node, new Vector3(0f, 1.47f, 0.379f));
                    AddTrigger(hab, node.Find("ValveHandle") ?? node, new Vector3(0f, 0.2f, -0.089f), new Vector3(0.13f, 0.136f, 0.28f),
                        CargoHab.ShowerAction, "Open", true);
                    break;
                case "toilet":
                    hab.ToiletNode = node;
                    AddTrigger(hab, node, new Vector3(0f, 0.37f, 0f), new Vector3(0.28f, 0.21f, 0.32f), CargoHab.ToiletAction, "Activate", false);
                    break;
                case "locker":
                    hab.LockerLeafA = node.Find("Door1"); hab.LockerLeafB = node.Find("Door1/Door2Open");
                    hab.LockerLeafC = node.Find("Door3"); hab.LockerLeafD = node.Find("Door3/Door4Open");
                    AddLockerDoors(hab);
                    break;
            }
        }
        // the slots in saved order (interior.json slotBase, checked append-only by check_interior.py)
        slotWork.Sort((a, b) => ((int)a.p["slotBase"]).CompareTo((int)b.p["slotBase"]));
        foreach (var (node, src, p) in slotWork)
        {
            int want = (int)p["slotBase"];
            if (hab.Slots.Count != want)            // never misnumber saved slots: stop adding instead
            {
                missing.Add($"{p["name"]} (slot {want}, have {hab.Slots.Count})");
                break;
            }
            if ((string)p["role"] == "bunk") AddBunk(hab, src, node);
            else if ((string)p["role"] == "rack") AddRackSlots(hab, node, p);
            else CopySlots(hab, src, node, p);
        }
        // the vanilla rover power tick reports the charge band on Powered
        if (!hab.Interactables.Exists(i => i.Action == InteractableType.Powered))
            hab.Interactables.Add(new Interactable { StringKey = "Powered", StringHash = Animator.StringToHash("Powered"), Parent = hab,
                Action = InteractableType.Powered, JoinInProgressSync = true });
        return $"interior {props} props ({(missing.Count == 0 ? "none" : string.Join("/", missing))} missing), {colliders} colliders, " +
               $"{hab.CabinLightList.Count} lights, beacon {hab.Beacon != null}, switch {hab.LightSwitch != null}, bunks {hab.Bunks.Count}";
    }

    /// <summary>Copy src's mesh, materials and solid box colliders onto dst, then its active children (minus Skip).
    /// Returns the number of colliders copied.</summary>
    internal static int Copy(Transform src, Transform dst)
    {
        int n = 0;
        var mf = src.GetComponent<MeshFilter>(); var mr = src.GetComponent<MeshRenderer>();
        if (mf && mr && mr.enabled && mr.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
        {
            dst.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = dst.gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterials = mr.sharedMaterials;
            r.shadowCastingMode = mr.shadowCastingMode;
        }
        foreach (var bc in src.GetComponents<BoxCollider>())
        {
            if (bc.isTrigger || !bc.enabled) continue;
            var c = dst.gameObject.AddComponent<BoxCollider>(); c.center = bc.center; c.size = bc.size; n++;
        }
        foreach (Transform ch in src)
        {
            if (!ch.gameObject.activeSelf || Skip.IsMatch(ch.name)) continue;
            var cd = NewNode(dst, ch.name);
            cd.localPosition = ch.localPosition; cd.localRotation = ch.localRotation; cd.localScale = ch.localScale;
            n += Copy(ch, cd);
        }
        return n;
    }

    /// <summary>Cut part of a prop's own mesh (e.g. the suit storage's waste-pipe fitting: the hab has no pipes): the
    /// triangles wholly inside the prefab-local box go. The copy is the prop's own; the game's mesh is untouched.</summary>
    private static void TrimRootMesh(Transform node, JArray boxes)
    {
        var mf = node.GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) return;
        if (!mf.sharedMesh.isReadable)                  // the game won't hand out its data: leave the prop whole, never blank it
        {
            Debug.Log($"[RoverCargo] interior: mesh {mf.sharedMesh.name} is not readable, trim skipped");
            return;
        }
        var mesh = Object.Instantiate(mf.sharedMesh);
        mesh.name = mf.sharedMesh.name + "_hab";
        var v = mesh.vertices;
        var xyz = new float[v.Length * 3];
        for (int i = 0; i < v.Length; i++) { xyz[i * 3] = v[i].x; xyz[i * 3 + 1] = v[i].y; xyz[i * 3 + 2] = v[i].z; }
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            var tris = mesh.GetTriangles(sub);
            foreach (JObject box in boxes)                   // each box on its own (a triangle goes when wholly in one)
            {
                float[] min = { (float)box["min"][0], (float)box["min"][1], (float)box["min"][2] };
                float[] max = { (float)box["max"][0], (float)box["max"][1], (float)box["max"][2] };
                tris = HabRules.TrimTriangles(xyz, tris, min, max);
            }
            mesh.SetTriangles(tris, sub);
        }
        mf.sharedMesh = mesh;
    }

    /// <summary>Real lights at the prefab's light nodes (copied settings, no shadows), off until the hab turns them on.</summary>
    private static void CopyLights(Transform src, Transform dst, List<Light> into, params string[] names)
    {
        foreach (var n in names)
        {
            var s = FindDeep(src, n);
            var sl = s ? s.GetComponent<Light>() : null;
            if (!sl) continue;
            var host = s.parent == src ? dst : (FindDeep(dst, s.parent.name) ?? dst);
            var t = NewNode(host, n);
            t.localPosition = s.localPosition; t.localRotation = s.localRotation;
            var l = t.gameObject.AddComponent<Light>();
            l.type = sl.type; l.color = sl.color; l.intensity = sl.intensity; l.range = sl.range; l.spotAngle = sl.spotAngle;
            l.shadows = LightShadows.None;
            l.enabled = false;
            into.Add(l);
        }
    }

    /// <summary>Button2 = cabin lights, exactly as on the rover (Rover.InteractWith gives "Cabin Lights On/Off").</summary>
    private static void AddSwitch(CargoHab hab, Transform node)
    {
        hab.LightSwitch = node.Find("Switch");
        var bc = node.gameObject.AddComponent<BoxCollider>();
        bc.center = new Vector3(0f, 0.02f, 0f);                   // the switch's own axes: x across, y out of its plate,
        bc.size = new Vector3(0.10f, 0.06f, 0.14f);               // z along the rocker
        bc.isTrigger = true;
        hab.Interactables.Add(new Interactable { StringKey = "Button2", StringHash = Animator.StringToHash("Button2"), Parent = hab,
            Collider = bc, Action = InteractableType.Button2, JoinInProgressSync = true, Bounds = new Bounds(bc.center, bc.size) });
    }

    // bed-local fallbacks (StructureSingleBed, build 0.2.x) if the prefab is missing: bed == null
    private static readonly Dictionary<string, Vector3> BedPoints = new()
    {
        ["BoxColliderSlot1TypeEntity001"] = new(0.76f, 0.58f, 0.26f), ["PlayerAnimationPosition"] = new(0.69f, 0.05f, 0.25f),
        ["CameraPoint"] = new(0.30f, 0.60f, 0.25f), ["ExistPosition"] = new(0.77f, 0.24f, 1.10f),
    };

    /// <summary>The prefab's own slots, appended to the hab: class, name, location, trigger box and size, all posed
    /// under node exactly as they sit on the prefab (a locker shelf, a charger cell, a helmet hook...). A missing prefab
    /// still reserves its slots (bare, not interactable) so later saved indices never shift.</summary>
    private static void CopySlots(CargoHab hab, Thing prefab, Transform node, JObject p)
    {
        int count = (int)p["slotCount"];
        var root = prefab ? prefab.transform : null;
        for (int i = 0; i < count; i++)
        {
            var ps = prefab && i < prefab.Slots.Count ? prefab.Slots[i] : null;
            int index = hab.Slots.Count;
            var action = (InteractableType)Enum.Parse(typeof(InteractableType), "Slot" + (index + 1));
            var loc = NewNode(node, "Slot" + (i + 1));
            if (root && ps != null)
            {
                var at = ps.Location ? ps.Location : root;
                loc.localPosition = root.InverseTransformPoint(at.position);
                loc.localRotation = Quaternion.Inverse(root.rotation) * at.rotation;
            }
            // the slot's click box: its own BoxCollider, else the prefab's slot node (the suit storage's helmet hook is a
            // sphere, reached in vanilla through the prefab interactable, since Slot.Collider can only hold a box)
            Collider src = root ? (ps?.Collider as Collider ?? SlotNodeCollider(root, i + 1)) : null;
            Collider hit = null;
            BoxCollider bc = null;
            if (src)
            {
                var tg = NewNode(node, "SlotTrigger" + (i + 1));
                tg.localPosition = root.InverseTransformPoint(src.transform.position);
                tg.localRotation = Quaternion.Inverse(root.rotation) * src.transform.rotation;
                if (src is SphereCollider sph)
                {
                    var sc = tg.gameObject.AddComponent<SphereCollider>();
                    sc.center = sph.center; sc.radius = sph.radius; sc.isTrigger = true;
                    hit = sc;
                }
                else if (src is BoxCollider box)
                {
                    bc = tg.gameObject.AddComponent<BoxCollider>();
                    bc.center = box.center; bc.size = box.size; bc.isTrigger = true;
                    hit = bc;
                }
            }
            var slot = new Slot
            {
                StringKey = ps?.StringKey ?? "Slot", StringHash = ps?.StringHash ?? Animator.StringToHash("Slot"),
                Type = ps?.Type ?? Slot.Class.None, Parent = hab, Location = loc, Collider = bc, Size = ps?.Size ?? Vector3.zero,
                Action = action, IsInteractable = hit != null, IsSwappable = ps?.IsSwappable ?? true,
                RealWorldScale = ps?.RealWorldScale ?? false, ScaleMultiplier = ps?.ScaleMultiplier ?? 1f,
                HidesOccupant = ps?.HidesOccupant ?? false,
                // the rest of the game slot as it is: what it takes (a gas mask holder takes gas masks), how it shows
                SpecificTypePrefabHashes = ps?.SpecificTypePrefabHashes ?? System.Array.Empty<int>(),
                OccupantCastsShadows = ps?.OccupantCastsShadows ?? true, IsHiddenInSeat = ps?.IsHiddenInSeat ?? false,
                AllowDragging = ps?.AllowDragging ?? false, IsLocked = ps?.IsLocked ?? false,
                UseInternalAtmosphere = ps?.UseInternalAtmosphere ?? false,
                EntityControlMode = ps?.EntityControlMode ?? MovementController.Mode.Seated,
            };
            hab.Slots.Add(slot);
            if (hit) hab.Interactables.Add(new Interactable { StringKey = "Slot" + (index + 1), StringHash = Animator.StringToHash("Slot" + (index + 1)),
                Parent = hab, Collider = hit, Action = action, Slot = slot,
                Bounds = hit is SphereCollider s1 ? new Bounds(s1.center, Vector3.one * s1.radius * 2f) : new Bounds(bc.center, bc.size) });
        }
        hab.SlotPropNames.Add((string)p["name"]); hab.SlotPropFirst.Add((int)p["slotBase"]); hab.SlotPropCount.Add(count);
    }

    /// <summary>A click box on each locker door, riding on its hinged leaf: over the closed doors, and standing out
    /// with the open leaves, clear of the shelves. Both carry the locker action; the first holds the saved state.</summary>
    private static void AddLockerDoors(CargoHab hab)
    {
        foreach (var (leaf, side, first) in new[] { (hab.LockerLeafA, -1f, true), (hab.LockerLeafC, 1f, false) })
        {
            if (!leaf) continue;
            var tg = NewNode(leaf, "LockerDoorTrigger");
            var bc = tg.gameObject.AddComponent<BoxCollider>();
            bc.center = new Vector3(side * 0.36f, 0f, 0.014f);            // leaf-local: both folding panels, just proud
            bc.size = new Vector3(0.72f, 1.64f, 0.06f);
            bc.isTrigger = true;
            hab.Interactables.Add(new Interactable { StringKey = "Open", StringHash = Animator.StringToHash("Open"), Parent = hab,
                Collider = bc, Action = CargoHab.LockerAction, ActionName = "Locker", JoinInProgressSync = first,
                Bounds = new Bounds(bc.center, bc.size) });
        }
    }

    private static readonly Regex SlotNumber = new(@"Slot(\d+)");

    /// <summary>The prefab's collider on the child named for slot n (Slot1SphereCollider..., BoxColliderTriggerSlot1...).</summary>
    private static Collider SlotNodeCollider(Transform root, int n)
    {
        foreach (Transform ch in root)
        {
            var m = SlotNumber.Match(ch.name);
            if (m.Success && int.Parse(m.Groups[1].Value) == n && ch.GetComponent<Collider>() is { } c) return c;
        }
        return null;
    }

    /// <summary>The prefab's own looping sound (the shower's running water), played by an AudioSource at `at` on the copy.
    /// Taken from its interactables' sound events, then its thing-level ones; null if the prefab has none.</summary>
    private static AudioSource AddLoopSound(Thing prefab, Transform node, Vector3 at)
    {
        AudioClip clip = null;
        foreach (var it in prefab.Interactables)
            foreach (var ev in it.AssociatedAudioEvents)
                if (!clip && ev?.ClipsData != null && ev.ClipsData.Looping && ev.ClipsData.Clips.Count > 0) clip = ev.ClipsData.Clips[0];
        foreach (var ev in prefab.AudioEvents)
            if (!clip && ev?.ClipsData != null && ev.ClipsData.Looping && ev.ClipsData.Clips.Count > 0) clip = ev.ClipsData.Clips[0];
        Debug.Log($"[RoverCargo] interior: {prefab.name} running-water sound {(clip ? clip.name : "not found")}");
        if (!clip) return null;
        var src = NewNode(node, "WaterSound").gameObject.AddComponent<AudioSource>();
        src.transform.localPosition = at;
        src.clip = clip; src.loop = true; src.playOnAwake = false;
        src.spatialBlend = 1f; src.minDistance = 1f; src.maxDistance = 12f; src.volume = 0.6f;
        return src;
    }

    /// <summary>A click box for one of the hab's own actions (the shower valve, the toilet), as the game's prefab has it.</summary>
    private static void AddTrigger(CargoHab hab, Transform at, Vector3 center, Vector3 size, InteractableType action, string key, bool saved)
    {
        var tg = NewNode(at, "Trigger" + action);
        var bc = tg.gameObject.AddComponent<BoxCollider>();
        bc.center = center; bc.size = size; bc.isTrigger = true;
        hab.Interactables.Add(new Interactable { StringKey = key, StringHash = Animator.StringToHash(key), Parent = hab, Collider = bc,
            Action = action, JoinInProgressSync = saved, Bounds = new Bounds(bc.center, bc.size) });
    }

    /// <summary>The water rack's liquid-canister slots (our own rack, no prefab): upright, one trigger box each.</summary>
    private static void AddRackSlots(CargoHab hab, Transform node, JObject p)
    {
        foreach (JObject rs in (p["rackSlots"] as JArray) ?? new JArray())
        {
            int index = hab.Slots.Count;
            var action = (InteractableType)Enum.Parse(typeof(InteractableType), "Slot" + (index + 1));
            var loc = NewNode(node, (string)rs["key"]);
            loc.localPosition = V(rs["pos"]);
            var tg = NewNode(node, "Trigger" + (string)rs["key"]);
            tg.localPosition = loc.localPosition + new Vector3(0f, -0.26f, 0f);          // over the canister body
            var bc = tg.gameObject.AddComponent<BoxCollider>(); bc.size = new Vector3(0.24f, 0.86f, 0.24f); bc.isTrigger = true;
            var slot = new Slot { StringKey = (string)rs["key"], StringHash = Animator.StringToHash((string)rs["key"]), Type = Slot.Class.LiquidCanister,
                Parent = hab, Location = loc, Collider = bc, Action = action, IsInteractable = true, IsSwappable = true, RealWorldScale = true };
            hab.Slots.Add(slot);
            hab.Interactables.Add(new Interactable { StringKey = "Slot" + (index + 1), StringHash = Animator.StringToHash("Slot" + (index + 1)),
                Parent = hab, Collider = bc, Action = action, Slot = slot, Bounds = new Bounds(bc.center, bc.size) });
        }
        hab.SlotPropNames.Add((string)p["name"]); hab.SlotPropFirst.Add((int)p["slotBase"]); hab.SlotPropCount.Add((int)p["slotCount"]);
    }

    /// <summary>The bunks' slot name: the game's own "Bed" string (CargoHab finds its bunks by it).</summary>
    public const string BunkSlotKey = "Bed";

    /// <summary>An Entity slot on the hab, placed with the bed prefab's own pose, camera and exit points.</summary>
    private static void AddBunk(CargoHab hab, Thing bed, Transform node)
    {
        Transform Mark(string child)
        {
            var s = bed ? bed.transform.Find(child) : null;
            var t = NewNode(node, child);
            if (s) { t.localPosition = s.localPosition; t.localRotation = s.localRotation; }
            else if (BedPoints.TryGetValue(child, out var fallback)) t.localPosition = fallback;
            return t;
        }
        int index = hab.Slots.Count;                                   // appended: saved slot indices never move
        var action = (InteractableType)Enum.Parse(typeof(InteractableType), "Slot" + (index + 1));
        var srcBox = bed ? bed.transform.Find("BoxColliderSlot1TypeEntity001")?.GetComponent<BoxCollider>() : null;
        var tg = Mark("BoxColliderSlot1TypeEntity001");
        var bc = tg.gameObject.AddComponent<BoxCollider>();
        var size = srcBox ? srcBox.size : new Vector3(1.6f, 0.3f, 0.8f);
        var lo = (srcBox ? srcBox.center : Vector3.zero) - size / 2f;
        var hi = lo + size;
        // the prefab's box covers only the mattress, behind the frame's aisle side: grown down over the bed base and
        // 2.5 cm past the frame (bed-local z 0.675), so from the aisle the cursor meets the slot before the solid frame
        // (the top bunk is at eye height, where it only ever found the frame)
        lo.y = Mathf.Min(lo.y, 0.01f - tg.localPosition.y);
        hi.z = Mathf.Max(hi.z, 0.70f - tg.localPosition.z);
        bc.center = (lo + hi) / 2f;
        bc.size = hi - lo;
        bc.isTrigger = true;
        var mode = bed && bed.Slots != null && bed.Slots.Count > 0 ? bed.Slots[0].EntityControlMode : MovementController.Mode.LyingDown;
        var slot = new Slot
        {
            StringKey = BunkSlotKey, StringHash = Animator.StringToHash(BunkSlotKey), Type = Slot.Class.Entity, Parent = hab,
            Location = Mark("PlayerAnimationPosition"), EntityControlMode = mode, UseInternalAtmosphere = true,
            IsInteractable = true, IsSwappable = false, Action = action, Collider = bc,
        };
        hab.Slots.Add(slot);
        hab.Interactables.Add(new Interactable { StringKey = "Slot" + (index + 1), StringHash = Animator.StringToHash("Slot" + (index + 1)),
            Parent = hab, Collider = bc, Action = action, Slot = slot, Bounds = new Bounds(bc.center, bc.size) });   // the highlight box
        hab.Bunks.Add(new CargoHab.Bunk { SlotIndex = index, Camera = Mark("CameraPoint"), Exit = Mark("ExistPosition") });   // sic, the prefab's name
    }

    private static Transform Place(Transform node, JObject p)
    {
        node.localPosition = V(p["pos"]);
        node.localRotation = Q(p["rot"]);
        return node;
    }

    internal static Transform NewNode(Transform parent, string name)
    {
        var t = new GameObject(name) { layer = parent.gameObject.layer }.transform;
        t.SetParent(parent, false);
        return t;
    }

    internal static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform ch in root)
        {
            var hit = FindDeep(ch, name);
            if (hit) return hit;
        }
        return null;
    }

    private static Vector3 V(JToken a) => new((float)a[0], (float)a[1], (float)a[2]);
    private static Quaternion Q(JToken a) => new((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
}
