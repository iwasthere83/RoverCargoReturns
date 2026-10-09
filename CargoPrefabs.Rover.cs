using System;
using System.Collections.Generic;
using System.Linq;
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

// usings as CargoPrefabs.cs (InternalAtmosphereConditioner, Slot, Thing ... resolve the same way)
public sealed partial class CargoPrefabs
{
    public const float RoverComHeight = 0.53f;          // the 2020 rover's resulting height, below the wheel centres (0.645)

    private static Vector3 V3((float X, float Y, float Z) t) => new(t.X, t.Y, t.Z);

    private Material[] RoverMaterials(JToken list) =>
        ((list as JArray) ?? new JArray("ColorWhite")).Select(m => (string)m == "WindowGlass" ? RoverGlass() : GameMaterial((string)m) ?? GameMaterial("ColorWhite")).ToArray();

    /// <summary>One wheel's tyre (at its hub) and trailing arm (at its hinge): the original rover's meshes from RoverAssets,
    /// with rover.json's materials. The rover and the trailers (the user, 2026-10-04: no 2020 parts on the trailers).</summary>
    private (Transform tyre, Transform arm) RunningGear(CargoLayout rover, JObject w, Transform groupWheels, Transform groupArms, string wn)
    {
        var mats = rover.Json["tyres"];
        var tyre = Child(groupWheels, wn + "Visual", V(w["pos"]));
        tyre.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)w["tyre"]);
        tyre.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(mats?[(string)w["tyre"]]);
        var arm = Child(groupArms, wn + "Arm", V(w["armPivot"]));
        arm.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)w["arm"]);
        arm.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(mats?[(string)w["arm"]]);
        return (tyre, arm);
    }

    private sealed class Shocks
    {
        public readonly List<Transform> Bodies = new(), Rods = new(), Springs = new();
        public readonly List<Vector3> Fit = new();
    }

    /// <summary>The coil-over shocks in `list` (rover.json's format; the trailers' jsons name the rover's meshes): the body
    /// under the vehicle at its upper mount, the rod on its wheel's arm at its lower mount, the spring on the body at its
    /// upper seat; CargoRover.AnimateShocks aims and scales them every frame.</summary>
    private Shocks BuildShocks(CargoLayout rover, JToken list, Transform root, Transform groupArms, Dictionary<string, Transform> armOf, string who)
    {
        var mats = rover.Json["tyres"];
        var s = new Shocks();
        foreach (JObject sh in (list as JArray) ?? new JArray())
        {
            string wn = (string)sh["wheel"];
            if (!armOf.TryGetValue(wn, out var armT)) { _log.Add(who + ": a shock for an unknown wheel " + wn); continue; }
            Vector3 top = V(sh["top"]), bottom = V(sh["bottom"]);
            var body = Child(groupArms, wn + "ShockBody", top);
            body.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)sh["body"]);
            body.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(mats?[(string)sh["body"]]);
            var rod = Child(armT, wn + "ShockRod", bottom - armT.localPosition);
            rod.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)sh["rod"]);
            rod.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(mats?[(string)sh["rod"]]);
            body.LookAt(rod.position, root.right);
            rod.LookAt(body.position, root.right);
            float start = (float)sh["springAt"][0], end = (float)sh["springAt"][1];
            var spring = Child(body, wn + "ShockSpring", new Vector3(0f, 0f, start));
            spring.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)sh["spring"]);
            spring.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(mats?[(string)sh["spring"]]);
            s.Bodies.Add(body); s.Rods.Add(rod); s.Springs.Add(spring);
            s.Fit.Add(new Vector3(start, end, Vector3.Distance(top, bottom) - start - end));
        }
        return s;
    }

    private static void SetShocks(CargoRover target, Shocks s)
    {
        target.ShockBodies = s.Bodies; target.ShockRods = s.Rods; target.ShockSprings = s.Springs; target.ShockSpringFit = s.Fit;
    }

    private static Transform RoverAnchor(Transform root, JObject a, string name)
    {
        var t = Child(root, name, V(a["pos"]));
        t.localRotation = Quaternion.Euler((float)a["rot"][0], (float)a["rot"][1], (float)a["rot"][2]);
        return t;
    }

    /// <summary>
    /// The original Cargo Rover from RoverAssets/rover.json (spec 2026-09-27-original-rover-design.md): our meshes on
    /// the live Mk I (its Rover code, rigid body, audio and headlight beams), like BuildRover. Not registered until the
    /// switch-over (sub-project 6). An incomplete RoverAssets logs every problem and returns null, never throws.
    /// </summary>
    public CargoRover BuildRoverFromJson(Rover mk1, CargoLayout rover)
    {
        _partialRover = _partialBlueprint = null;
        try
        {
            var J = rover?.Json;
            var problems = J == null || mk1 == null ? new List<string> { J == null ? "rover.json not loaded" : "no Mk I rover" }
                : RoverJson.Problems(J, rover.HasMesh);
            if (problems.Count > 0) { _log.Add("original rover not built: " + string.Join("; ", problems)); return null; }
            return BuildRoverFromJsonCore(mk1, rover, J);
        }
        catch (Exception ex)
        {
            _log.Add("original rover build failed: " + ex);
            if (_partialBlueprint) Object.DestroyImmediate(_partialBlueprint);
            if (_partialRover) Object.DestroyImmediate(_partialRover);
            return null;
        }
        finally { _partialRover = _partialBlueprint = null; }
    }

    private GameObject _partialRover, _partialBlueprint;    // what a failed build leaves under PrefabsGameObject

    private CargoRover BuildRoverFromJsonCore(Rover mk1, CargoLayout rover, JObject J)
    {
        var go = _partialRover = Object.Instantiate(mk1.gameObject, Prefab.PrefabsGameObject.transform);
        go.name = RoverName;
        var old = go.GetComponent<Rover>();
        var mk1Seat = old.Slots.Count > 0 ? old.Slots[0] : null;
        var mk1HeadLeft = FindChild(go.transform, "HeadLights/Left");
        var mk1HeadRight = FindChild(go.transform, "HeadLights/Right");
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) Object.DestroyImmediate(r);
        foreach (var f in go.GetComponentsInChildren<MeshFilter>(true)) Object.DestroyImmediate(f);
        // the Mk I Animator stays (as in BuildRover/BuildTrailer): Rover.UpdateEachFrame sets its floats unguarded

        // body, glass, door leaves: our meshes, game palette materials
        // the body renders on a child (the user's build 11/12 report: black parts turned white with the headlights on):
        // the Mk I's Animator plays RoverHeadlightsOn, which swaps a material slot of the ROOT object's MeshRenderer
        var bodyMesh = rover.Mesh((string)J["body"]["mesh"]);
        var bodyNode = Child(go.transform, "Body");
        bodyNode.gameObject.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
        bodyNode.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(J["body"]["materials"]);
        if (J["glass"]?["mesh"] is JValue gm && gm.Type == JTokenType.String)
        {
            var glass = Child(go.transform, "CabGlass");
            glass.gameObject.AddComponent<MeshFilter>().sharedMesh = rover.Mesh((string)gm);
            glass.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(J["glass"]["materials"]);
        }
        var doorHinges = new Transform[RoverRules.Doors.Length];
        var doorCols = new BoxCollider[RoverRules.Doors.Length];
        foreach (JObject p in (J["parts"] as JArray) ?? new JArray())
        {
            string pn = (string)p["name"];
            int di = Array.FindIndex(RoverRules.Doors, d => d.Name == pn);
            var parent = go.transform;
            var pos = V(p["pos"]);
            if (di >= 0)                                             // a bay door leaf turns about its hinge's z axis
            {
                var hinge = Child(go.transform, pn + "Hinge", V(p["hinge"]));
                doorHinges[di] = hinge;
                parent = hinge;
                pos -= hinge.localPosition;
            }
            var t = Child(parent, pn, pos);
            var mesh = rover.Mesh((string)p["mesh"]);
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            t.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(p["materials"]);
            if (di >= 0 && mesh)
            {
                // a trigger: a shut leaf still stops the cursor (one raycast, triggers included), but an opening leaf
                // shoves nothing and adds no mass; UpgradeCursor lets the wrench onto it
                t.gameObject.layer = go.layer;
                var leafBox = t.gameObject.AddComponent<BoxCollider>();
                leafBox.isTrigger = true;
                leafBox.center = mesh.bounds.center;
                leafBox.size = mesh.bounds.size;
                doorCols[di] = leafBox;
            }
        }
        AddBoxColliders(go, J["colliders"]);
        var rb = go.GetComponent<Rigidbody>();
        if (rb) rb.mass = (float)J["mass"];

        // anchors
        var anchorRoot = Child(go.transform, "Anchors");
        var A = new Dictionary<string, Transform>();
        foreach (var kv in (JObject)J["anchors"]) A[kv.Key] = RoverAnchor(anchorRoot, (JObject)kv.Value, kv.Key);

        // wheels: our WheelCollider numbers (rover.json = the 2020 values), our tyres and arms
        var groupColliders = Child(go.transform, "WheelColliders");
        var groupWheels = Child(go.transform, "WheelRenderers");
        var groupArms = Child(go.transform, "SuspensionRenderers");
        var wcJson = (JObject)J["wheelCollider"];
        var wheels = new List<Wheel>();
        var arms = new List<Transform>();
        var armWheels = new List<Transform>();
        var armOf = new Dictionary<string, Transform>();
        foreach (JObject w in J["wheels"])
        {
            string wn = (string)w["name"];
            var wcGo = new GameObject(wn);
            wcGo.transform.SetParent(groupColliders, false);
            wcGo.transform.localPosition = V(w["pos"]);
            BuildWheelCollider(wcGo, wcJson);
            var (tyre, arm) = RunningGear(rover, w, groupWheels, groupArms, wn);
            int mode = (int)w["mode"];
            wheels.Add(new Wheel
            {
                WheelCollider = wcGo.GetComponent<WheelCollider>(),
                IsMotorized = (bool)w["motor"],
                Mode = !_settings.RearWheelSteer && mode == (int)WheelSteeringMode.Inverted ? (WheelSteeringMode)0 : (WheelSteeringMode)mode,
                WheelTransform = tyre, WheelTransformParent = groupWheels, UvOffsetScale = 1f,
            });
            arms.Add(arm);
            armOf[wn] = arm;
            armWheels.Add(tyre);
        }

        var shocks = BuildShocks(rover, J["shocks"], go.transform, groupArms, armOf, "original rover");

        // swap Rover -> CargoRover (every Mk I field, as BuildRover)
        var cr = go.AddComponent<CargoRover>();
        CopyFields(old, cr, typeof(Rover));
        RetargetParents(go, old, cr);
        Object.DestroyImmediate(old);
        cr.name = cr.PrefabName = RoverName;
        cr.PrefabHash = Animator.StringToHash(RoverName);
        cr.Renderers.Clear();
        cr.RocketRenderers.Clear();
        foreach (var w in wheels) w.Parent = cr;
        cr.Wheels = wheels;
        cr.SuspensionArms = arms;
        cr.ArmWheels = armWheels;
        SetShocks(cr, shocks);

        // slots in RoverRules order (saves store the index)
        var slotColliders = new Dictionary<string, Collider>();
        cr.Slots = new List<Slot>();
        for (int i = 0; i < RoverRules.SlotCount; i++)
        {
            var (key, kind, anchorName) = RoverRules.Slots[i];
            var at = A[anchorName];
            bool cargo = kind is RoverSlotKind.Crate or RoverSlotKind.Tank;
            var slot = cargo ? LiftSlot(cr, key, at) : new Slot
            {
                StringKey = key, StringHash = Animator.StringToHash(key), Location = at, Parent = cr,
                Type = kind switch
                {
                    RoverSlotKind.Seat => Slot.Class.Entity, RoverSlotKind.Filter => Slot.Class.GasFilter,
                    RoverSlotKind.Chip => Slot.Class.ProgrammableChip, RoverSlotKind.Battery => Slot.Class.Battery,
                    _ => Slot.Class.GasCanister,
                },
                UseInternalAtmosphere = kind == RoverSlotKind.Seat, RealWorldScale = true, IsInteractable = true, IsSwappable = true,
                Size = V3(kind == RoverSlotKind.Seat ? RoverRules.SeatSlotSize : RoverRules.ClickBoxSize(kind)),
            };
            if (Enum.TryParse<InteractableType>("Slot" + (i + 1), out var act)) slot.Action = act;
            if (!cargo)
            {
                // the trigger the player clicks: a seat's is on its cab door (the seat is inside the solid cab
                // collider, and the cursor is one raycast), every other slot's is at the slot
                var box = A[RoverRules.ClickAnchor(anchorName)].gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = V3(RoverRules.ClickBoxCenter(kind));          // on the item's body (a canister's origin is its valve end)
                box.size = V3(RoverRules.ClickBoxSize(kind));
                slot.Collider = box;
                slotColliders[anchorName] = box;
            }
            if (kind == RoverSlotKind.Seat && mk1Seat != null) slot.EntityControlMode = mk1Seat.EntityControlMode;
            cr.Slots.Add(slot);
        }
        cr.ConnectionSlots = RoverRules.Bays.Select(b => new ContainerSlot { self = cr, ContainerSlots = b.Crates, TankSlots = b.Tanks }).ToArray();
        cr.DriverSlotIndex = 0;
        cr.PassengerSlotIndex = 1;
        cr.DriverExit = A["DriverExit"];
        cr.PassengerExit = A["PassengerExit"];
        cr.CameraPointDriver = A["CameraDriver"];
        cr.CameraPointPassenger = A["CameraPassenger"];

        // interactables: 2020 flags; the dash controls are dash-screen tap targets
        cr.Interactables = new List<Interactable>();
        foreach (var (key, action, sync, keyInteract, anchorName) in RoverRules.Interactables)
        {
            Collider col = null;
            if (anchorName == "Screen") col = TapTarget(A["Screen"], action);
            else if (anchorName != null) slotColliders.TryGetValue(anchorName, out col);
            var it = new Interactable
            {
                StringKey = key, StringHash = Animator.StringToHash(key), Parent = cr, Collider = col,
                Action = (InteractableType)Enum.Parse(typeof(InteractableType), action), JoinInProgressSync = sync, CanKeyInteract = keyInteract, KeyMap = "",
                ActionName = "",
            };
            if (col is BoxCollider bc) it.Bounds = new Bounds(bc.center, bc.size);
            cr.Interactables.Add(it);
        }
        AddUpgradeInteractables(cr);
        var thrust = cr.Interactables.Find(i => i.Action == CargoRover.ThrustSwitchAction);
        if (thrust != null)
        {
            var tb = TapTarget(A["Screen"], "Button12");
            thrust.Collider = tb; thrust.Bounds = new Bounds(tb.center, tb.size); thrust.ActionName = "Thrusters";
        }
        cr.ScreenAt = A["Screen"];                                  // RoverStatusScreen draws on it (each instance)
        cr.BayDoorHinges = doorHinges.ToList();                     // the covered bays (CargoRover turns and toggles them)
        cr.BayDoorColliders = doorCols.ToList();

        // numbers
        var F = (JObject)J["fields"];
        var com = J["autoCom"];
        cr.CenterOfMassOffset = new Vector3(0f, RoverComHeight - (float)com[1], 0f);
        cr.CabinInsulation = _settings.CabinInsulation;
        cr.SurfaceArea = (float)F["SurfaceArea"];
        cr.ThingHealth = (float)F["ThingHealth"];
        cr.Bounds = bodyMesh.bounds;
        cr.MotorPower = _settings.MotorPower;
        cr.BrakePower = _settings.BrakePower;
        cr.MaxSpeed = _settings.MaxSpeed;
        cr.SteeringPower = (float)F["SteeringPower"];
        cr.MaxTurnAngle = (float)F["MaxTurnAngle"];
        cr.MotorSpeed = (float)F["MotorSpeed"];
        cr.BrakeSpeed = (float)F["BrakeSpeed"];
        cr.SteeringSpeed = (float)F["SteeringSpeed"];
        cr.CabinVolumeLitres = (float)F["Volume"];
        cr.PressurePerTickKPa = (float)F["PressurePerTick"];
        cr.WasteMaxPressureKPa = (float)F["WasteMaxPressure"];
        cr.ConditionerMaxEnergy = (float)F["MaxEnergy"];
        cr.CabinOutputSettingKPa = (float)F["OutputSetting"];
        cr.CabinTemperatureK = (float)F["OutputTemperature"];
        cr.PaintableMaterial = GameMaterial("ColorWhite") ?? cr.PaintableMaterial;
        VehicleThumbnail(cr, rover, RoverName);
        _partialBlueprint = BuildTrailerBlueprint(mk1.Blueprint, bodyMesh, RoverName);
        cr.Blueprint = _partialBlueprint ?? cr.Blueprint;

        // lights: our own spot lights at the nose's LED bars and the roof light bar (the Mk I's volumetric headlights,
        // moved onto our nose, stayed dark: the user's check-in 3 report; they are switched off), their lenses lit with
        // them; red tail lights; one cabin light
        cr.HeadLights = HeadLamps(A, RoverRules.HeadlightAnchors, (float?)F["HeadlightSpot"] ?? 60f, 30f, 2.0f);
        cr.HeadLights.AddRange(HeadLamps(A, RoverRules.LightBarAnchors, (float?)F["LightBarSpot"] ?? 30f, 35f, 2.5f));
        foreach (var t in new[] { mk1HeadLeft, mk1HeadRight })
            if (t) t.gameObject.SetActive(false);
        cr.HeadLampGlow = new List<GameObject>();
        if (J["glow"]?["mesh"] is JValue glm && glm.Type == JTokenType.String && rover.Mesh((string)glm) is { } glowMesh)
        {
            var glow = Child(go.transform, "HeadLampGlow");
            glow.gameObject.AddComponent<MeshFilter>().sharedMesh = glowMesh;
            glow.gameObject.AddComponent<MeshRenderer>().sharedMaterial = LampGlow();
            glow.gameObject.SetActive(false);
            cr.HeadLampGlow.Add(glow.gameObject);
        }
        foreach (var n in new[] { "TailLightL", "TailLightR" })
        {
            var lt = A[n].gameObject.AddComponent<Light>();
            lt.type = LightType.Point; lt.color = new Color(1f, 0.12f, 0.08f); lt.range = 3f; lt.intensity = 1.5f;
            cr.HeadLights.Add(lt);                                  // like the 2020 brake lights: on with the headlights
        }
        var cabin = A["CabinLight"].gameObject.AddComponent<Light>();
        cabin.type = LightType.Point; cabin.range = 2.6f; cabin.intensity = 1.2f; cabin.shadows = LightShadows.None;
        cr.CabinLightList = new List<Light> { cabin };
        float spot = (float?)F["WorkLightSpot"] ?? 100f;            // the roof rack's work lights (the dash's SIDE / REAR LIGHTS)
        cr.SideLights = WorkLights(A, RoverRules.SideLightAnchors, spot, cr.SideLampGlow = new List<GameObject>());
        cr.RearLights = WorkLights(A, RoverRules.RearLightAnchors, spot, cr.RearLampGlow = new List<GameObject>());
        foreach (var l in cr.HeadLights.Concat(cr.CabinLightList).Concat(cr.SideLights).Concat(cr.RearLights)) l.enabled = false;
        cr.HeadlightsOn = new(); cr.HeadlightsOff = new(); cr.CabinOn = new(); cr.CabinOff = new();
        cr.PumpOn = new(); cr.PumpOff = new(); cr.FilterOn = new(); cr.FilterOff = new();
        cr.BatteryBars = new(); cr.SpeedNeedle = cr.PitchNeedle = cr.RollNeedle = null;     // the dashboard: sub-project 2

        cr.HitchPoint = A["Hitch"];
        go.AddComponent<InternalAtmosphereConditioner>().Thing = cr;
        var upgSkip = RoverJson.IncompleteUpgradeGroups(J, rover.HasMesh);     // sub-project 4: a bad upgrades entry costs
        if (upgSkip.Contains("?"))                                              // only the upgrades, never the rover (review I1)
            _log.Add("original rover: rover.json \"upgrades\" unreadable: no upgrade parts (every upgrade is refused)");
        else
            try { AddUpgradeParts(cr, rover, "rover", upgSkip); }
            catch (Exception ex) { _log.Add("original rover upgrade parts failed (the rover is built; upgrades without parts are refused): " + ex.Message); }
        _log.Add($"original rover built: {cr.Slots.Count} slots, {cr.Interactables.Count} interactables, {cr.Wheels.Count} wheels, mass {rb?.mass}, com offset {cr.CenterOfMassOffset}");
        return cr;
    }

    /// <summary>The front lamps (the user's check-in 3 report): a spot light at each anchor, off its lamp's lens, shining along
    /// the anchor's +z (its cone clears the push bar and the cab roof: tools/blender_rover_checks light_spill); cool white,
    /// no shadows (as the work lights).</summary>
    private static List<Light> HeadLamps(Dictionary<string, Transform> A, string[] anchors, float spot, float range, float intensity)
    {
        var lights = new List<Light>();
        foreach (var name in anchors)
        {
            var lt = A[name].gameObject.AddComponent<Light>();
            lt.type = LightType.Spot; lt.spotAngle = spot; lt.range = range; lt.intensity = intensity;
            lt.color = new Color(0.86f, 0.95f, 1f); lt.shadows = LightShadows.None;
            lights.Add(lt);
        }
        return lights;
    }

    /// <summary>Work lamps (the user's check-ins 2 and 3): at each anchor, 2 mm off a roof-rack lamp's lens, a wide spot
    /// light shining along the anchor's +z (out or back, and down; its cone clears the rover, tools/blender_rover_checks
    /// light_spill) and the lens lit (a glowing quad facing the lamp's way, hidden until the light is on).</summary>
    private List<Light> WorkLights(Dictionary<string, Transform> A, string[] anchors, float spot, List<GameObject> glow)
    {
        var lights = new List<Light>();
        foreach (var name in anchors)
        {
            var at = A[name];
            var lt = at.gameObject.AddComponent<Light>();
            lt.type = LightType.Spot; lt.spotAngle = spot; lt.range = 14f; lt.intensity = 2.2f;
            lt.color = new Color(1f, 0.95f, 0.86f); lt.shadows = LightShadows.None;
            lights.Add(lt);
            if (LampGlow() is not { } glowMat) continue;                                  // no ColorWhite: no lit lens
            var q = HabInterior.NewNode(at.parent, name + "Glow");
            q.localPosition = at.localPosition;
            q.localRotation = Quaternion.Euler(0f, at.localEulerAngles.y, 0f);          // the lens faces the lamp's way, upright
            q.gameObject.AddComponent<MeshFilter>().sharedMesh = LensQuad();
            q.gameObject.AddComponent<MeshRenderer>().sharedMaterial = glowMat;
            q.gameObject.SetActive(false);
            glow.Add(q.gameObject);
        }
        return lights;
    }

    private Mesh _lensQuad;

    /// <summary>A work lamp's lens (17 x 9 cm), both faces.</summary>
    private Mesh LensQuad()
    {
        if (_lensQuad) return _lensQuad;
        var m = new Mesh { name = "RoverCargoLensQuad" };
        m.vertices = new[] { new Vector3(-0.085f, -0.045f, 0f), new Vector3(0.085f, -0.045f, 0f), new Vector3(0.085f, 0.045f, 0f), new Vector3(-0.085f, 0.045f, 0f) };
        m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return _lensQuad = m;
    }

    private Material _lampGlow;

    /// <summary>A lit lens: the game's ColorWhite, plain white and emissive warm white.</summary>
    private Material LampGlow()
    {
        if (_lampGlow) return _lampGlow;
        if (GameMaterial("ColorWhite") is not { } src) return null;
        var mat = new Material(src) { name = "RoverCargoLampGlow", mainTexture = Texture2D.whiteTexture };
        mat.color = new Color(1f, 0.97f, 0.9f);
        mat.EnableKeyword("_EMISSION");
        if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", Texture2D.whiteTexture);
        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", new Color(1f, 0.95f, 0.86f) * 2.5f);
        return _lampGlow = mat;
    }

    /// <summary>The trigger over one dash-screen button, on the driving layout (RoverStatusScreen moves and switches it
    /// per page; RoverDashboard holds the rectangles).</summary>
    private static BoxCollider TapTarget(Transform screen, string action)
    {
        var node = HabInterior.NewNode(screen, "Tap." + action);
        var bc = node.gameObject.AddComponent<BoxCollider>();
        bc.isTrigger = true;
        var (c, s) = RoverDashboard.TapBox(RoverDashboard.DriveButton(Array.FindIndex(RoverDashboard.Controls, k => k.Action == action)));
        bc.center = new Vector3(c.X, c.Y, c.Z);
        bc.size = new Vector3(s.X, s.Y, s.Z);
        return bc;
    }
}
