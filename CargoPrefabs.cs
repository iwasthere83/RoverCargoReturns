using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Vehicles;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Stationeers.RoverCargo;

/// <summary>Builds the Rover (Cargo), its trailers, frames and kits from the Mk I prefabs plus the mod's own asset folders
/// (RoverAssets, TrailerAssets, HabAssets).</summary>
public sealed partial class CargoPrefabs
{
    public const string RoverName = "RoverCargo";
    public const string FrameName = "StructureRover";
    public const string KitName = "ItemKitRoverFrame";
    public const int ChipSlotIndex = 4;
    public const string TrailerName = "TrailerCargo";
    public const string HabName = "TrailerHab";
    public static Vector3 RoverHitchLocal = new(0f, 0.8f, -2.7f);

    private readonly Settings _settings;
    private readonly Dictionary<string, Material> _materials = new();
    private Dictionary<string, Material> _gameMaterials;
    private readonly List<string> _log = new();

    public sealed class Settings
    {
        public float MotorPower = 60f, BrakePower = 20f, MaxSpeed = 6.5f, GlassAlpha = 0.18f, CabinInsulation = 0.05f;
        public bool RearWheelSteer = true;
        public float TrailerComHeight = 0.58f, HabComHeight = 0.75f, TrailerSideGrip = 1.4f, StormDamage = 0.25f;
    }

    public CargoPrefabs(Settings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<string> Log => _log;

    // ------------------------------------------------------------------ entry
    public CargoRover BuildAll(CargoLayout trailer = null, CargoLayout hab = null, CargoLayout original = null)
    {
        var mk1 = Prefab.Find<Rover>("Rover_MkI") ?? throw new InvalidOperationException("Rover_MkI prefab not found");
        if (original == null) throw new InvalidOperationException("RoverAssets is missing or unreadable: the Rover (Cargo) cannot be built");
        var rover = BuildRoverFromJson(mk1, original)
                    ?? throw new InvalidOperationException("the original rover could not be built (see the lines above)");
        Register(rover);
        CargoTrailer trailerPrefab = null, habPrefab = null;              // kept for their frames and kits
        if (trailer != null)
        {
            try { trailerPrefab = BuildTrailer(mk1, trailer, original); Register(trailerPrefab); }
            catch (Exception ex) { _log.Add("trailer build failed (rover still available): " + ex); }
        }
        if (hab != null)
        {
            try { habPrefab = BuildTrailer(mk1, hab, original, HabName, _settings.HabComHeight, typeof(CargoHab)); Register(habPrefab); }
            catch (Exception ex) { _log.Add("hab trailer build failed (rover still available): " + ex); }
        }
        var mk1Kit = Prefab.Find<MultiConstructor>("ItemKitRoverMKI");
        var mk1Frame = mk1Kit != null && mk1Kit.Constructables.Count > 0 ? mk1Kit.Constructables[0] as RoverFrame : null;
        if (mk1Kit == null || mk1Frame == null) _log.Add("Mk I kit/frame not found: no vehicle is printable (all stay spawnable)");
        else
        {
            TryFrame(original, rover, FrameName, KitName, mk1Kit, mk1Frame, isRover: true);
            if (trailerPrefab != null) TryFrame(trailer, trailerPrefab, TrailerFrameName, TrailerKitName, mk1Kit, mk1Frame, isRover: false);
            if (habPrefab != null) TryFrame(hab, habPrefab, HabFrameName, HabKitName, mk1Kit, mk1Frame, isRover: false);
        }
        return rover;
    }

    private static void Register(Thing thing)
    {
        Prefab.RegisterExisting(thing);
        WorldManager.Instance.SourcePrefabs.Add(thing);
        // Construction cursors / creative menu are built lazily from SourcePrefabs; if that already happened, add ours now.
        if (InventoryManager.DynamicThingPrefabs.Count > 0 && thing is DynamicThing dyn && !InventoryManager.DynamicThingPrefabs.Contains(thing.PrefabName))
        {
            InventoryManager.DynamicThingPrefabs.Add(thing.PrefabName);
            Assets.Scripts.UI.ImGuiUi.ImguiCreativeSpawnMenu.AddDynamicItem(dyn);
        }
    }

    /// <summary>The saved upgrade flags and the thruster switch. No collider: they are set by the wrench (upgrades) and
    /// by the rover's panel (switch), never clicked. JoinInProgressSync: without it State is neither saved nor synced.</summary>
    private static void AddUpgradeInteractables(CargoRover cr)
    {
        foreach (var a in new[] { CargoRover.ArmourAction, CargoRover.FairingsAction, CargoRover.ThrustersAction, CargoRover.ThrustSwitchAction })
            if (!cr.Interactables.Exists(i => i.Action == a))
                cr.Interactables.Add(new Interactable { StringKey = a.ToString(), StringHash = Animator.StringToHash(a.ToString()),
                    Parent = cr, Action = a, JoinInProgressSync = true });
    }

    // 2020 wheel names -> Mk I names, so the Mk I rumble/skid audio events (keyed by collider name) match.
    private static readonly Dictionary<string, string> WheelAudioNames = new()
    {
        ["TireFrontL"] = "WheelFrontTireL", ["TireFrontR"] = "WheelFrontTireR",
        ["TireBackL"] = "WheelRearTireL", ["TireBackR"] = "WheelRearTireR",
    };

    // ------------------------------------------------------------------ upgrades
    /// <summary>The upgrade part groups (our meshes, JSON "upgrades.&lt;key&gt;"), hidden until fitted. Each piece's box
    /// colliders are triggers - hit by the wrench (removal), no collisions, no effect on the mass - and are what a wrench
    /// removes the upgrade by. Rover parts come from trailer.json "upgrades.rover".</summary>
    private void AddUpgradeParts(CargoRover target, CargoLayout src, string key, ICollection<string> skip = null)
    {
        if (target == null || src?.Json["upgrades"]?[key] is not JArray arr) return;
        foreach (var gr in skip ?? Array.Empty<string>()) _log.Add($"{target.name}: no {gr} upgrade parts (a mesh is missing): that upgrade is refused");
        int n = 0;
        foreach (var t in arr)
        {
            if (t is not JObject p || p["group"] == null) continue;
            string group = (string)p["group"];
            if (skip != null && skip.Contains(group)) continue;
            var root = group switch { "Armour" => target.ArmourParts, "Fairings" => target.FairingParts, _ => target.ThrusterParts };
            if (!root)
            {
                root = new GameObject("Upgrade" + group) { layer = target.gameObject.layer };
                root.transform.SetParent(target.transform, false);
                if (group == "Armour") target.ArmourParts = root; else if (group == "Fairings") target.FairingParts = root; else target.ThrusterParts = root;
            }
            var mesh = src.Mesh((string)p["mesh"]);
            if (!mesh) { _log.Add($"upgrade mesh {(string)p["mesh"]} missing"); continue; }
            var go = new GameObject((string)p["name"] ?? (string)p["mesh"]) { layer = target.gameObject.layer };
            go.transform.SetParent(root.transform, false);
            if (p["pos"] != null) go.transform.localPosition = V(p["pos"]);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = ((p["materials"] as JArray) ?? new JArray("ColorWhite"))
                .Select(m => GameMaterial((string)m) ?? GameMaterial("ColorWhite")).ToArray();
            AddBoxColliders(go, p["colliders"]);
            var cols = group switch { "Armour" => target.ArmourColliders, "Fairings" => target.FairingColliders, _ => target.ThrusterColliders };
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { c.isTrigger = true; cols.Add(c); }
            if ((bool?)p["follow"] == true) target.Fenders.Add(go.transform);   // rides its wheel (CargoRover.UpdateFenders)
            n++;
        }
        foreach (var g in new[] { target.ArmourParts, target.FairingParts, target.ThrusterParts }) if (g) g.SetActive(false);
        _log.Add($"{target.name}: {n} upgrade parts ({key})");
    }

    /// <summary>Box colliders from json ({center, size, rot?, name?}); named or rotated boxes get their own child.</summary>
    private void AddBoxColliders(GameObject host, JToken list)
    {
        if (list == null) return;
        int ci = 0;
        foreach (JObject c in list)
        {
            string cname = (string)c["name"];
            if (c["rot"] is JArray || cname != null)
            {
                var child = new GameObject(cname ?? "Collider" + ci++) { layer = host.layer };
                child.transform.SetParent(host.transform, false);
                child.transform.localPosition = V(c["center"]);
                if (c["rot"] is JArray r) child.transform.localEulerAngles = new Vector3((float)r[0], (float)r[1], (float)r[2]);
                child.AddComponent<BoxCollider>().size = V(c["size"]);
                continue;
            }
            var bc = host.AddComponent<BoxCollider>();
            bc.center = V(c["center"]);
            bc.size = V(c["size"]);
        }
    }

    public CargoTrailer BuildTrailer(Rover mk1, CargoLayout trailer, CargoLayout rover, string name = TrailerName, float comHeight = float.NaN, System.Type type = null)
    {
        if (rover == null) throw new InvalidOperationException("RoverAssets/rover.json is missing: the trailers run the original rover's wheels");
        var T = trailer.Json;
        var go = Object.Instantiate(mk1.gameObject, Prefab.PrefabsGameObject.transform);
        go.name = name;
        var old = go.GetComponent<Rover>();
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) Object.DestroyImmediate(r);
        foreach (var f in go.GetComponentsInChildren<MeshFilter>(true)) Object.DestroyImmediate(f);
        foreach (var l in go.GetComponentsInChildren<Light>(true)) l.gameObject.SetActive(false); // no headlights

        // body: our own mesh, game palette materials, on a child: the Mk I's Animator (kept) writes material slots of
        // the ROOT renderer every frame, which overwrote the body's paint (the user's build 16 paint test)
        var bodyNode = Child(go.transform, "Body");
        var bodyFilter = bodyNode.gameObject.AddComponent<MeshFilter>();
        bodyFilter.sharedMesh = trailer.Mesh((string)T["body"]["mesh"]);
        var body = bodyNode.gameObject.AddComponent<MeshRenderer>();
        body.sharedMaterials = T["body"]["materials"].Select(m => GameMaterial((string)m) ?? GameMaterial("ColorWhite")).ToArray();
        AddBoxColliders(go, T["colliders"]);
        float mass = (float)T["mass"];
        var rb = go.GetComponent<Rigidbody>();
        if (rb) rb.mass = mass;

        // wheels, trailing arms and shocks: the original rover's (RoverAssets), on rover.json's wheel numbers
        var groupColliders = Child(go.transform, "WheelColliders");
        var groupWheels = Child(go.transform, "WheelRenderers");
        var groupArms = Child(go.transform, "SuspensionRenderers");
        var colliderJson = (JObject)rover.Json["wheelCollider"];
        var armOf = new Dictionary<string, Transform>();
        float springScale = mass / (float?)T["roverMass"] ?? 1f;
        var wheels = new List<Wheel>();
        var arms = new List<Transform>();
        var armWheels = new List<Transform>();
        foreach (JObject w in T["wheels"])
        {
            string wheelName = (string)w["name"];
            var wcGo = new GameObject(wheelName);
            wcGo.transform.SetParent(groupColliders, false);
            wcGo.transform.localPosition = V(w["pos"]);
            BuildWheelCollider(wcGo, colliderJson);
            var wc = wcGo.GetComponent<WheelCollider>();
            var spring = wc.suspensionSpring;
            spring.spring *= springScale;
            spring.damper *= springScale;
            wc.suspensionSpring = spring;
            var side = wc.sidewaysFriction;   // sideways only: stops the trailer whipping out without making it drag in a straight line
            side.stiffness *= _settings.TrailerSideGrip;
            wc.sidewaysFriction = side;

            var (tyre, arm) = RunningGear(rover, w, groupWheels, groupArms, wheelName);
            wheels.Add(new Wheel { WheelCollider = wc, IsMotorized = false, Mode = 0, WheelTransform = tyre, WheelTransformParent = groupWheels, UvOffsetScale = 1f });
            arms.Add(arm); armWheels.Add(tyre);
            armOf[wheelName] = arm;
        }
        var shocks = BuildShocks(rover, T["shocks"], go.transform, groupArms, armOf, name);

        var slotRoot = Child(go.transform, "Slots");
        var hitch = Child(go.transform, "TrailerHitch");
        hitch.localPosition = V(T["hitch"]);

        // swap Rover -> CargoTrailer (same field copy as the rover)
        var tr = (CargoTrailer)go.AddComponent(type ?? typeof(CargoTrailer));
        CopyFields(old, tr, typeof(Rover));
        RetargetParents(go, old, tr);
        Object.DestroyImmediate(old);
        tr.name = name;
        tr.PrefabName = name;
        tr.PrefabHash = Animator.StringToHash(name);
        tr.Renderers.Clear();
        tr.RocketRenderers.Clear();
        tr.Interactables = new List<Interactable>();
        foreach (var w in wheels) w.Parent = tr;
        tr.Wheels = wheels;
        tr.SuspensionArms = arms;
        tr.ArmWheels = armWheels;
        SetShocks(tr, shocks);
        tr.TrailerHitch = hitch;
        tr.HitchPoint = null;

        // slots: 0 = hidden dummy seat (Vehicle needs driver/passenger indices), then per bay: 1 crate + 2 tank slots.
        // Each bay is one ContainerSlot pair, exactly like a rover lift: a crate blocks that bay's tanks and vice versa,
        // so bays mix freely (e.g. two tank bays + two crate bays).
        var slots = new List<Slot>
        {
            new() { StringKey = "Unused", StringHash = Animator.StringToHash("Unused"), Type = Slot.Class.Entity, Location = go.transform,
                    Parent = tr, IsInteractable = false, IsSwappable = false, HidesOccupant = true, Action = (InteractableType)(-1) },
        };
        tr.CrateSlotIndices = new List<int>();
        tr.TankSlotIndices = new List<int>();
        var bays = new List<ContainerSlot>();
        int n = 0;
        foreach (JObject bay in (T["bays"] as JArray) ?? new JArray())
        {
            int crate = slots.Count;
            tr.CrateSlotIndices.Add(crate);
            slots.Add(LiftSlot(tr, "ContainerSlot", Child(slotRoot, $"Bay{n}Crate", V(bay["crate"]))));
            var tanks = new List<int>();
            int t = 0;
            foreach (var p in bay["tanks"])
            {
                tanks.Add(slots.Count);
                tr.TankSlotIndices.Add(slots.Count);
                slots.Add(LiftSlot(tr, "GasTank", Child(slotRoot, $"Bay{n}Tank{t++}", V(p))));
            }
            bays.Add(new ContainerSlot { self = tr, ContainerSlots = new[] { crate }, TankSlots = tanks.ToArray() });
            n++;
        }
        tr.Slots = slots;
        tr.ConnectionSlots = bays.ToArray();
        tr.DriverSlotIndex = 0;
        tr.PassengerSlotIndex = 0;
        tr.DriverExit = tr.PassengerExit = go.transform;
        // never the root: Rover.PhysicsUpdate moves the seated player's camera point to the helmet every step, which
        // would drag the whole trailer if anyone ever sat in a slot that falls back to it
        tr.CameraPointDriver = tr.CameraPointPassenger = Child(go.transform, "CameraPoint", new Vector3(0f, 2.2f, 0f));
        tr.MotorPower = 0f;
        tr.BrakePower = 10f;
        tr.SteeringPower = 0f;
        tr.MaxTurnAngle = 0f;
        tr.MaxSpeed = 20f; // the towing rover sets the pace
        // trailer.json autoCom = Unity's collider-based centre of mass (volume-weighted boxes); hold the tuned height
        float autoCom = (float?)T["autoCom"] ?? 0.98f;
        float height = float.IsNaN(comHeight) ? _settings.TrailerComHeight : comHeight;
        tr.CenterOfMassOffset = new Vector3(0f, height - autoCom, 0f);
        var bounds = body.GetComponent<MeshFilter>().sharedMesh.bounds;
        tr.Bounds = bounds;
        tr.SurfaceArea = 40f;
        tr.ThingHealth = 1800f;
        tr.PaintableMaterial = GameMaterial("ColorWhite") ?? tr.PaintableMaterial;
        // The creative menu hides entries without a thumbnail. Our clones are not paint-mask objects, so the game uses
        // the single Thumbnail, which the Mk I leaves empty (it only fills per-colour Thumbnails). Placeholder until a
        // rendered trailer thumbnail exists.
        tr.Thumbnail = Thumbnail(name) ?? mk1.Thumbnail ?? mk1.Thumbnails?.FirstOrDefault(s => s != null) ?? Thumbnail(RoverName);
        tr.Blueprint = BuildTrailerBlueprint(mk1.Blueprint, bodyFilter.sharedMesh, name) ?? tr.Blueprint;

        tr.TailLights = new List<Light>();
        n = 0;
        foreach (var p in T["tailLights"])
        {
            var lt = Child(go.transform, "TailLight" + n++, V(p)).gameObject.AddComponent<Light>();
            lt.type = LightType.Point; lt.color = new Color(1f, 0.12f, 0.08f); lt.range = 3f; lt.intensity = 1.5f; lt.enabled = false;
            tr.TailLights.Add(lt);
        }
        tr.HeadLights = new List<Light>();
        tr.CabinLightList = new List<Light>();
        string habInfo = "";
        if (tr is CargoHab hab && T["deploy"] is JObject dep)
        {
            foreach (JObject p in (T["parts"] as JArray) ?? new JArray())
            {
                var pg = new GameObject((string)p["name"]) { layer = go.layer };
                pg.transform.SetParent(go.transform, false);
                pg.transform.localPosition = V(p["pos"]);
                pg.AddComponent<MeshFilter>().sharedMesh = trailer.Mesh((string)p["mesh"]);
                pg.AddComponent<MeshRenderer>().sharedMaterials = p["materials"].Select(m => GameMaterial((string)m) ?? GameMaterial("ColorWhite")).ToArray();
                AddBoxColliders(pg, p["colliders"]);
                if (pg.name == "SlideOut") hab.SlideOut = pg.transform;
                else if (pg.name == "Ladder") hab.Ladder = pg.transform;
                else if (pg.name.StartsWith("Leg")) hab.Legs.Add(pg.transform);
            }
            hab.SlideTravel = V(dep["slideTravel"]);
            hab.LadderStowEuler = V(dep["ladderStowEuler"]);
            hab.LegStowH = (float)dep["legStowH"];
            hab.LegMaxStroke = (float)dep["legMaxStroke"];
            hab.DeployPanel = go.transform.Find((string)dep["panel"])?.GetComponent<Collider>();
            if (hab.Ladder)
            {
                hab.Ladder.localEulerAngles = hab.LadderStowEuler;   // Travel pose: folded up
                foreach (var c in hab.Ladder.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            }
            // JoinInProgressSync: without it Interactable.State always reads 0 and is neither saved nor synced
            var panelBox = hab.DeployPanel as BoxCollider;
            hab.Interactables.Add(new Interactable
            {
                StringKey = "Activate", StringHash = Animator.StringToHash("Activate"), Parent = hab,   // a game-localised name
                Collider = hab.DeployPanel, Action = CargoHab.DeployAction, ActionName = "Deploy", JoinInProgressSync = true,
                Bounds = panelBox ? new Bounds(panelBox.center, panelBox.size) : default,
            });
            var byName = new Dictionary<string, int>();
            foreach (JObject s in (T["slots"] as JArray) ?? new JArray())
            {
                string sname = (string)s["name"], stype = (string)s["type"];
                var loc = Child(slotRoot, sname, V(s["pos"]));
                Slot slot = stype switch
                {
                    "Tank" => LiftSlot(hab, "GasTank", loc),
                    "Container" => new Slot { StringKey = sname, StringHash = Animator.StringToHash(sname), Type = Slot.Class.None,
                                              Location = loc, Parent = hab, AllowDragging = false, IsInteractable = false },   // never takes a crate
                    _ => new Slot { StringKey = sname, StringHash = Animator.StringToHash(sname),
                                    Type = stype == "Battery" ? Slot.Class.Battery : Slot.Class.GasFilter,
                                    Location = loc, Parent = hab, IsInteractable = true, IsSwappable = true },
                };
                if (stype == "Tank") { slot.StringKey = sname; slot.StringHash = Animator.StringToHash(sname); }
                int index = hab.Slots.Count;
                if (s["trigger"] is JObject trg)
                {
                    var tg = new GameObject("Trigger" + sname) { layer = go.layer };
                    tg.transform.SetParent(go.transform, false);
                    tg.transform.localPosition = V(trg["center"]);
                    var bc = tg.AddComponent<BoxCollider>(); bc.size = V(trg["size"]); bc.isTrigger = true;
                    // by name: the enum is not contiguous past Slot30 (vanilla ConfigureSlots parses "SlotN" too)
                    var slotAction = (InteractableType)System.Enum.Parse(typeof(InteractableType), "Slot" + (index + 1));
                    slot.Action = slotAction; slot.Collider = bc;
                    hab.Interactables.Add(new Interactable { StringKey = "Slot" + (index + 1), StringHash = Animator.StringToHash("Slot" + (index + 1)),
                        Parent = hab, Collider = bc, Action = slotAction, Slot = slot,
                        Bounds = new Bounds(bc.center, bc.size) });
                }
                hab.Slots.Add(slot);
                byName[sname] = index;
            }
            hab.ConnectionSlots = ((T["cradles"] as JArray) ?? new JArray()).Select(c => new ContainerSlot
            {
                self = hab, ContainerSlots = new[] { byName[(string)c["container"]] }, TankSlots = new[] { byName[(string)c["tank"]] },
            }).ToArray();
            hab.AirSupplySlot = byName.TryGetValue("AirSupply", out var a) ? a : -1;
            hab.WasteSlot = byName.TryGetValue("Waste", out var w) ? w : -1;
            hab.CabinVolumeLitres = (float?)T["cabinVolume"] ?? 26800f;
            hab.CabinInsulation = _settings.CabinInsulation;
            hab.SurfaceArea = (float?)T["surfaceArea"] ?? hab.SurfaceArea;
            if (T["door"] is JObject dj && Prefab.Find((string)dj["prefab"]) is Thing doorPrefab)
            {
                var door = new GameObject("Door") { layer = go.layer };
                door.transform.SetParent(go.transform, false);
                door.transform.localPosition = V(dj["pos"]);
                CopyVisual(doorPrefab.transform, door.transform, withColliders: true);          // frame + its box colliders
                hab.DoorLeafA = CopyChild(doorPrefab.transform, "InteriorDoor1", door.transform);
                hab.DoorLeafB = CopyChild(doorPrefab.transform, "InteriorDoor2", door.transform);
                hab.DoorLeafTravel = (float)dj["leafTravel"];
                var trg = (JObject)dj["trigger"];
                var tg = new GameObject("DoorTrigger") { layer = go.layer };
                tg.transform.SetParent(go.transform, false);
                tg.transform.localPosition = V(trg["center"]);
                var tbc = tg.AddComponent<BoxCollider>(); tbc.size = V(trg["size"]); tbc.isTrigger = true;
                hab.DoorTrigger = tbc;
                hab.Interactables.Add(new Interactable { StringKey = "Door", StringHash = Animator.StringToHash("Door"), Parent = hab,
                    Collider = tbc, Action = CargoHab.DoorAction, ActionName = "Door", JoinInProgressSync = true, Bounds = new Bounds(tbc.center, tbc.size) });
            }
            else _log.Add("hab door: StructureCompositeDoor prefab not found (doorway left open)");
            var interiorFile = System.IO.Path.Combine(trailer.Dir, "interior.json");
            string interiorInfo;
            try
            {
                interiorInfo = System.IO.File.Exists(interiorFile)
                    ? HabInterior.Build(hab, JObject.Parse(System.IO.File.ReadAllText(interiorFile)))
                    : "interior.json missing (empty room)";
            }
            catch (Exception e) { interiorInfo = "interior failed (empty room): " + e.Message; }   // never lose the hab itself
            habInfo = $", hab parts: slide-out {hab.SlideOut != null}, ladder {hab.Ladder != null}, legs {hab.Legs.Count}, panel {hab.DeployPanel != null}, door leaves {hab.DoorLeafA != null}/{hab.DoorLeafB != null}, slots {hab.Slots.Count}, cradles {hab.ConnectionSlots.Length}, {interiorInfo}";
        }
        _log.Add($"{name} built{habInfo}: {tr.ConnectionSlots.Length} bays ({tr.CrateSlotIndices.Count} crate / {tr.TankSlotIndices.Count} tank slots), {wheels.Count} wheels, mass {mass}, spring x{springScale:0.00}, thumbnail {(tr.Thumbnail ? tr.Thumbnail.name : "MISSING (hidden from creative menu)")}");
        AddUpgradeInteractables(tr);
        AddUpgradeParts(tr, trailer, tr is CargoHab ? "hab" : "trailer");
        return tr;
    }

    /// <summary>Copy one node's MeshFilter/MeshRenderer (shared mesh + materials) and, optionally, its box colliders.</summary>
    private static void CopyVisual(Transform src, Transform dst, bool withColliders)
    {
        var mf = src.GetComponent<MeshFilter>(); var mr = src.GetComponent<MeshRenderer>();
        if (mf && mr)
        {
            dst.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            dst.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
        }
        if (!withColliders) return;
        foreach (var bc in src.GetComponents<BoxCollider>())
        {
            if (bc.isTrigger) continue;
            var c = dst.gameObject.AddComponent<BoxCollider>(); c.center = bc.center; c.size = bc.size;
        }
    }

    /// <summary>Copy a named child (visual + box colliders, plus its visual children such as the glass).</summary>
    private static Transform CopyChild(Transform srcRoot, string name, Transform dstParent)
    {
        var src = srcRoot.Find(name);
        if (!src) return null;
        var dst = new GameObject(name) { layer = dstParent.gameObject.layer }.transform;
        dst.SetParent(dstParent, false);
        dst.localPosition = src.localPosition; dst.localRotation = src.localRotation; dst.localScale = src.localScale;
        CopyVisual(src, dst, withColliders: true);
        foreach (Transform ch in src)
        {
            if (!ch.GetComponent<MeshFilter>()) continue;
            var cd = new GameObject(ch.name) { layer = dst.gameObject.layer }.transform;
            cd.SetParent(dst, false);
            cd.localPosition = ch.localPosition; cd.localRotation = ch.localRotation; cd.localScale = ch.localScale;
            CopyVisual(ch, cd, withColliders: false);
        }
        return dst;
    }

    /// <summary>
    /// Same flags as the rover's own lift slots (2020 layout slots 12-15). Crates and portable tanks are
    /// DraggableThings: DraggableThing.CanEnter refuses slots without AllowDragging (the "Connect" bar filled but
    /// nothing attached), and RealWorldScale keeps them full size instead of shrinking to the slot.
    /// </summary>
    private static Slot LiftSlot(Thing parent, string key, Transform location) => new()
    {
        StringKey = key, StringHash = Animator.StringToHash(key), Type = Slot.Class.None, Location = location, Parent = parent,
        AllowDragging = true, RealWorldScale = true, IsInteractable = true, IsSwappable = true,
    };

    private static Transform Child(Transform parent, string name, Vector3? localPos = null)
    {
        var t = parent.Find(name) ?? new GameObject(name).transform;
        t.SetParent(parent, false);
        if (localPos.HasValue) t.localPosition = localPos.Value;
        return t;
    }

    private static Vector3 V(JToken a) => new((float)a[0], (float)a[1], (float)a[2]);

    private GameObject BuildTrailerBlueprint(GameObject mk1Blueprint, Mesh body, string name)
    {
        if (mk1Blueprint == null || body == null) return null;
        var bp = Object.Instantiate(mk1Blueprint, Prefab.PrefabsGameObject.transform);
        bp.name = name + "Blueprint";
        var filter = bp.GetComponentInChildren<MeshFilter>(true);
        if (filter) filter.sharedMesh = body;
        return bp;
    }

    // ------------------------------------------------------------------ frame + kit
    private Structure BuildLegacyFrame(RoverFrame mk1Frame, Rover rover)
    {
        var go = Object.Instantiate(mk1Frame.gameObject, Prefab.PrefabsGameObject.transform);
        go.name = FrameName;
        var frame = go.GetComponent<RoverFrame>();
        frame.PrefabName = FrameName;
        frame.PrefabHash = Animator.StringToHash(FrameName);
        frame.RoverPrefab = rover;
        frame.Thumbnail = Thumbnail(FrameName) ?? frame.Thumbnail;
        frame.Renderers.Clear();
        frame.RocketRenderers.Clear();
        _log.Add("StructureRover keeps the Mk I frame meshes (no build-stage data)");
        return frame;
    }

    private MultiConstructor BuildKit(MultiConstructor mk1Kit, Structure frame, string kitName = KitName, Sprite thumbnail = null)
    {
        var go = Object.Instantiate(mk1Kit.gameObject, Prefab.PrefabsGameObject.transform);
        go.name = kitName;
        var kit = go.GetComponent<MultiConstructor>();
        kit.PrefabName = kitName;
        kit.PrefabHash = Animator.StringToHash(kitName);
        kit.Constructables = new List<Structure> { frame };
        kit.Thumbnail = thumbnail ? thumbnail : Thumbnail(kitName) ?? kit.Thumbnail;
        if (kit.Thumbnails != null && kit.Thumbnails.Length > 0)              // per paint colour: ours, never the Mk I's
            kit.Thumbnails = Enumerable.Repeat(kit.Thumbnail, kit.Thumbnails.Length).ToArray();
        kit.Renderers.Clear();
        kit.RocketRenderers.Clear();
        return kit;
    }

    private static void BuildWheelCollider(GameObject go, JObject c)
    {
        var wc = go.AddComponent<WheelCollider>();
        wc.center = CargoLayout.V3(c["m_Center"]);
        wc.radius = (float)c["m_Radius"];
        wc.mass = (float)c["m_Mass"];
        wc.wheelDampingRate = (float)c["m_WheelDampingRate"];
        wc.suspensionDistance = (float)c["m_SuspensionDistance"];
        wc.forceAppPointDistance = (float)c["m_ForceAppPointDistance"];
        var s = c["m_SuspensionSpring"];
        wc.suspensionSpring = new JointSpring { spring = (float)s["spring"], damper = (float)s["damper"], targetPosition = (float)s["targetPosition"] };
        wc.forwardFriction = Friction(c["m_ForwardFriction"]);
        wc.sidewaysFriction = Friction(c["m_SidewaysFriction"]);
        wc.enabled = (bool)c["m_Enabled"];
    }

    private static WheelFrictionCurve Friction(JToken f) => new()
    {
        extremumSlip = (float)f["m_ExtremumSlip"], extremumValue = (float)f["m_ExtremumValue"],
        asymptoteSlip = (float)f["m_AsymptoteSlip"], asymptoteValue = (float)f["m_AsymptoteValue"],
        stiffness = (float)f["m_Stiffness"],
    };

    private Material _roverGlass;

    /// <summary>The 2020 glass mirrored cabin lights and was hard to see through: clear, low-gloss glass (GlassAlpha).</summary>
    private Material RoverGlass()
    {
        if (_roverGlass) return _roverGlass;
        var src = GameMaterial("WindowGlass") ?? GameMaterial("ColorWhite");
        var mat = new Material(src) { name = "RoverCargoGlass" };
        if (mat.HasProperty("_Color")) { var col = mat.color; col.a = _settings.GlassAlpha; mat.color = col; }
        foreach (var p in new[] { "_Glossiness", "_Smoothness", "_GlossMapScale", "_Metallic", "_SpecularHighlights", "_GlossyReflections" })
            if (mat.HasProperty(p)) mat.SetFloat(p, 0f);
        return _roverGlass = mat;
    }

    private Material GameMaterial(string name)
    {
        if (_gameMaterials == null)
        {
            _gameMaterials = new Dictionary<string, Material>();
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m && !_gameMaterials.ContainsKey(m.name)) _gameMaterials[m.name] = m;
        }
        return _gameMaterials.TryGetValue(name, out var mat) ? mat : null;
    }

    private static Sprite Thumbnail(string prefabName)
    {
        var sprite = Resources.Load<Sprite>("UI/Thumbnails/" + prefabName);
        if (sprite) return sprite;
        var tex = Resources.Load<Texture2D>("UI/Thumbnails/" + prefabName);
        return tex ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) : null;
    }

    private static Transform FindChild(Transform root, string path) => root.Find(path);

    /// <summary>Copies every instance field declared on <paramref name="upTo"/> and its bases (down to MonoBehaviour).</summary>
    private static void CopyFields(object from, object to, Type upTo)
    {
        for (var t = upTo; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                f.SetValue(to, f.GetValue(from));
    }

    /// <summary>Components and serializable helper objects that pointed at the old Rover now point at CargoRover.</summary>
    private static void RetargetParents(GameObject root, Thing old, Thing now)
    {
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb == old || mb == now) continue;
            foreach (var f in mb.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (typeof(Thing).IsAssignableFrom(f.FieldType) && ReferenceEquals(f.GetValue(mb), old)) f.SetValue(mb, now);
        }
        foreach (var listField in new[] { "AudioEvents", "AudioSources" })
        {
            var f = typeof(Thing).GetField(listField, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f?.GetValue(now) is not System.Collections.IEnumerable items) continue;
            foreach (var item in items)
            {
                if (item == null) continue;
                foreach (var pf in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (typeof(Thing).IsAssignableFrom(pf.FieldType) && ReferenceEquals(pf.GetValue(item), old)) pf.SetValue(item, now);
            }
        }
    }
}
