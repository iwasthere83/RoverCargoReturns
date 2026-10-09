using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Networking;
using Cysharp.Threading.Tasks;
using Assets.Scripts.Vehicles;
using HarmonyLib;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// The Cargo Rover (Mk II): today's working <see cref="Rover"/> driving code plus the pressurised cabin the 2020
/// rover had. The cabin uses the game's own suit conditioner (<see cref="InternalAtmosphereConditioner"/>):
/// Import = air pump from the O2 tanks, Export = filtration into the waste tank. Temperature: an insulated cabin plus
/// a heat pump that vents cabin heat outside (the suit conditioner dumps it into the waste tank, which a
/// vehicle-sized cabin on Venus overheats until the tank bursts).
/// Seats have <see cref="Slot.UseInternalAtmosphere"/>, so a seated player breathes the cabin.
/// </summary>
public class CargoRover : Rover, IInternalConditioner, ICircuitHolder, IPowered
{
    public const int WasteTankSlotIndex = 8;

    // Tuned values from the 2020 prefab. Public so they are serialized with the prefab clone.
    public float CabinVolumeLitres = 50f;
    public float CabinOutputSettingKPa = 101.325f;
    public float CabinTemperatureK = 293.15f;
    public float PressurePerTickKPa = 101.325f;
    public float WasteMaxPressureKPa = 4053f;
    public float ConditionerMaxEnergy = 12000f;
    // Fraction of the game's normal heat exchange with the outside for the cabin and everything in the rover's slots
    // (slot occupants inherit the parent's factors). The rover's outer surface is ~20x a suit's, so 0.05 is suit-like.
    public float CabinInsulation = 0.05f;
    public static bool LogClimate;
    private const float CoolPowerPerJoule = 0.01f, HeatPowerPerJoule = 0.5f; // vanilla suit conditioner costs
    private int _climateLogTick;

    // Visual parts wired by the prefab builder (serialized so every spawned rover keeps them).
    public List<Light> HeadLights = new();
    public List<Light> CabinLightList = new();
    public List<Light> SideLights = new(), RearLights = new();   // the original rover's roof-rack work lights (Button13/14)
    public List<GameObject> SideLampGlow = new(), RearLampGlow = new();   // their lamps' lenses lit, shown with the lights
    public List<GameObject> HeadLampGlow = new();                     // the nose's LED bars and the roof light bar lit (Button1)
    public List<GameObject> HeadlightsOn = new(), HeadlightsOff = new();
    public List<GameObject> CabinOn = new(), CabinOff = new();
    public List<GameObject> PumpOn = new(), PumpOff = new();
    public List<GameObject> FilterOn = new(), FilterOff = new();
    public List<GameObject> BatteryBars = new(); // 20,40,60,80,100
    public Transform SpeedNeedle, PitchNeedle, RollNeedle;
    public Transform HitchPoint; // rear tow ball (trailers couple here)
    public Transform ScreenAt;   // the original rover's dash screen face (RoverStatusScreen draws on it); null on the trailers
    protected virtual bool HasCabin => true;
    // Suspension arms (pivot at the body) and the wheel visual each one reaches; same order.
    public List<Transform> SuspensionArms = new(), ArmWheels = new();
    private Vector3[] _armRestReach;
    private Quaternion[] _armRestRotation;
    // Shocks (the original rover): each body hangs at its frame mount and each rod on its arm (the builder); the spring,
    // a child of the body, scales between its seats. ShockSpringFit: (start, end, rest) per shock. Same order.
    public List<Transform> ShockBodies = new(), ShockRods = new(), ShockSprings = new();
    public List<Vector3> ShockSpringFit = new();

    private IInternalConditionerHandler _conditioner;
    private readonly List<Slot> _filterSlots = new(3);
    private readonly List<Slot> _airTankSlots = new(3);
    private Quaternion _speedRest, _pitchRest, _rollRest;

    public override bool HasReadableAtmosphere => true;
    public override float ConvectionFactor => HasCabin ? base.ConvectionFactor * CabinInsulation : base.ConvectionFactor;
    public override float RadiationFactor => HasCabin ? base.RadiationFactor * CabinInsulation : base.RadiationFactor;
    public override float SolarHeatingFactor => HasCabin ? base.SolarHeatingFactor * CabinInsulation : base.SolarHeatingFactor;

    public override void Awake()
    {
        base.Awake();
        if (GameManager.GameState == GameState.None) return;
        _filterSlots.Clear();
        _airTankSlots.Clear();
        for (int i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            if (slot.Type == Slot.Class.GasFilter) _filterSlots.Add(slot);
            else if (slot.Type == Slot.Class.GasCanister && i != WasteTankSlotIndex) _airTankSlots.Add(slot);
        }
        InitInternalAtmosphere();
        if (!IsCursor && HasCabin)
        {
            AtmosphericsManager.Instance.Register(this);
            CircuitHolders.Register(this);
        }
        if (SpeedNeedle) _speedRest = SpeedNeedle.localRotation;
        if (PitchNeedle) _pitchRest = PitchNeedle.localRotation;
        if (RollNeedle) _rollRest = RollNeedle.localRotation;
        int n = Mathf.Min(SuspensionArms.Count, ArmWheels.Count);
        _armRestReach = new Vector3[n];
        _armRestRotation = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            if (!SuspensionArms[i] || !ArmWheels[i]) continue;
            _armRestReach[i] = LocalOf(ArmWheels[i].position) - LocalOf(SuspensionArms[i].position);
            _armRestRotation[i] = SuspensionArms[i].localRotation;
        }
        InitFenders();
        if (!IsCursor && ScreenAt && this is not CargoTrailer)
            gameObject.AddComponent<RoverStatusScreen>().Init(this, ScreenAt);   // built on each rover, not on the prefab
    }

    /// <summary>Each riding fender's wheel (nearest rendered tyre) and that tyre's spin axis in its own frame, taken
    /// here at the prefab's rest pose (no steer) so a steered wheel at fit time cannot bake in a yaw offset.</summary>
    private void InitFenders()
    {
        _fenderWheel = new int[Fenders.Count];
        _fenderAxle = new Vector3[Fenders.Count];
        for (int f = 0; f < Fenders.Count; f++)
        {
            int best = -1; float bd = float.MaxValue;
            var root = Fenders[f] ? Fenders[f].parent : null;
            for (int i = 0; root && i < ArmWheels.Count; i++)
            {
                if (!ArmWheels[i]) continue;
                float d = (root.InverseTransformPoint(ArmWheels[i].position) - Fenders[f].localPosition).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            _fenderWheel[f] = best;
            if (best >= 0) _fenderAxle[f] = ArmWheels[best].InverseTransformDirection(ThingTransform.right);
        }
    }

    private Vector3 LocalOf(Vector3 world) => ThingTransform.InverseTransformPoint(world);

    /// <summary>Swing each suspension arm about its body hinge so it keeps reaching its wheel hub.</summary>
    private void AnimateSuspensionArms()
    {
        if (_armRestReach == null) return;
        for (int i = 0; i < _armRestReach.Length; i++)
        {
            var arm = SuspensionArms[i];
            var wheel = ArmWheels[i];
            if (!arm || !wheel || _armRestReach[i] == Vector3.zero) continue;
            // Trailing arms: they hinge on the rover's lateral (local X) axis and swing in the fore-aft vertical
            // plane. Angle from the wheel's height at the arm's fixed fore-aft reach; steering/spin cannot twist it.
            // Rotation about +X turns +Y towards +Z, so the angle is measured as atan2(z, y).
            Vector3 rest = _armRestReach[i];
            float y = LocalOf(wheel.position).y - LocalOf(arm.position).y;
            float angle = (Mathf.Atan2(rest.z, y) - Mathf.Atan2(rest.z, rest.y)) * Mathf.Rad2Deg;
            angle = Mathf.Clamp(Mathf.DeltaAngle(0f, angle), -55f, 55f);
            arm.localRotation = Quaternion.AngleAxis(angle, Vector3.right) * _armRestRotation[i];
        }
    }

    /// <summary>Aim each shock's body and rod at each other after the arms swing (they telescope with the arm) and scale
    /// its spring between the seats. Local +z runs along the shock, local +y along the rover's axles.</summary>
    private void AnimateShocks()
    {
        var lateral = ThingTransform.right;
        int n = Mathf.Min(ShockBodies.Count, ShockRods.Count);
        for (int i = 0; i < n; i++)
        {
            var b = ShockBodies[i];
            var r = ShockRods[i];
            if (!b || !r) continue;
            b.LookAt(r.position, lateral);
            r.LookAt(b.position, lateral);
            if (i < ShockSprings.Count && ShockSprings[i] && i < ShockSpringFit.Count)
            {
                var f = ShockSpringFit[i];
                float d = (LocalOf(b.position) - LocalOf(r.position)).magnitude;
                ShockSprings[i].localScale = new Vector3(1f, 1f, RoverRules.SpringScale(d, f.x, f.y, f.z));
            }
        }
    }

    public override void InitInternalAtmosphere()
    {
        if (InternalAtmosphere == null)
            InternalAtmosphere = new Atmosphere(this, new VolumeLitres(CabinVolumeLitres), 0L);
    }

    public override void OnAtmosphericTick()
    {
        base.OnAtmosphericTick();
        if (GameManager.RunSimulation && !IsCursor && this is not CargoTrailer) BurnPropellant();
        if (GameManager.GameState != GameState.Running || IsCursor || !Powered || !UseConditioner || _conditioner == null || InternalAtmosphere == null)
            return;
        float pumped = HeatPump(InternalAtmosphere);
        _conditioner.SetGasToTank(MoleQuantity.Zero);   // filtration: excess pressure + filters into the waste tank
        _conditioner.GetGasFromTank();                  // air pump: top up from the O2 tanks
        if (LogClimate && ++_climateLogTick % 10 == 0) LogCabinClimate(pumped);
    }

    /// <summary>Hold the cabin at the set temperature; heat removed goes to the planet, not the waste tank.</summary>
    protected virtual bool ClimateOn => OnOff;
    /// <summary>The vanilla conditioner runs the cabin air (the hab runs its own).</summary>
    protected virtual bool UseConditioner => true;

    /// <summary>What the last HeatPump call took from the battery (joules; the hab's power ledger reads it).</summary>
    protected float LastHeatPumpCost;

    protected float HeatPump(Atmosphere cabin)
    {
        LastHeatPumpCost = 0f;
        var battery = Battery;
        if (!ClimateOn || battery == null || battery.IsEmpty || cabin.TotalMoles <= Chemistry.MINIMUM_QUANTITY_MOLES) return 0f;
        // GasMixture is a struct field: always go through cabin.GasMixture, never a local copy.
        MoleEnergy delta = IdealGas.Energy(cabin.GasMixture.HeatCapacity, new TemperatureKelvin(CabinTemperatureK)) - cabin.GasMixture.TotalEnergy;
        var used = new MoleEnergy(System.Math.Min(System.Math.Abs(delta.ToDouble()), ConditionerMaxEnergy * Efficiency));
        if (delta < MoleEnergy.Zero)
        {
            LastHeatPumpCost = (used * CoolPowerPerJoule).ToFloat();
            battery.PowerStored -= LastHeatPumpCost;
            PlanetaryAtmosphereSimulation.AddEnergy(cabin.GasMixture.RemoveEnergy(used));
            return -used.ToFloat();
        }
        LastHeatPumpCost = (used * HeatPowerPerJoule).ToFloat();
        battery.PowerStored -= LastHeatPumpCost;
        cabin.GasMixture.AddEnergy(used);
        return used.ToFloat();
    }

    private void LogCabinClimate(float pumped)
    {
        var cabin = InternalAtmosphere;
        var world = GridController.CanContainAtmos(WorldGrid) ? GridController.AtmosphericsController.SampleGlobalAtmosphere(WorldGrid) : null;
        float conv = world != null ? AtmosphereHelper.CalculateThingConvection(this, world, cabin).ToFloat() : 0f;
        var waste = WasteTank;
        Debug.Log($"[RoverCargo][climate] cabin {cabin.PressureGassesAndLiquids.ToFloat():0.0} kPa {cabin.Temperature.ToFloat() - 273.15f:0.0} C | " +
                  $"outside {world?.PressureGassesAndLiquids.ToFloat() ?? 0f:0} kPa {(world?.Temperature.ToFloat() ?? 273.15f) - 273.15f:0} C | " +
                  $"area {SurfaceArea:0.0} conv x{ConvectionFactor:0.0000} leak {conv:0} J/tick pump {pumped:0} J | " +
                  $"waste {(waste ? $"{waste.InternalAtmosphere.PressureGassesAndLiquids.ToFloat():0} kPa {waste.InternalAtmosphere.Temperature.ToFloat() - 273.15f:0} C" : "none")} | on {OnOff} batt {Battery?.PowerStored ?? 0f:0}");
    }

    public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
    {
        string label = interactable.Action switch
        {
            InteractableType.Import => "Air Pump",
            InteractableType.Export => "Filtration",
            ThrustSwitchAction when HasUpgrade(Upgrade.Thrusters) => "Thrusters",
            SideLightsAction when SideLights.Count > 0 => "Side Lights",
            RearLightsAction when RearLights.Count > 0 => "Rear Lights",
            _ => null,
        };
        if (label == null) return base.InteractWith(interactable, interaction, doAction);
        var result = new DelayedActionInstance
        {
            Duration = 0f,
            ActionMessage = label + (interactable.State == 1 ? " Off" : " On"),
        };
        if (!doAction) return result.Succeed();
        if (GameManager.RunSimulation) OnServer.Interact(interactable, interactable.State != 1 ? 1 : 0);
        return result.Succeed();
    }

    public override void UpdateEachFrame()
    {
        base.UpdateEachFrame();
        Show(ArmourParts, HasUpgrade(Upgrade.Armour));
        Show(FairingParts, HasUpgrade(Upgrade.Fairings));
        Show(ThrusterParts, HasUpgrade(Upgrade.Thrusters));
        UpdatePuff();
        UpdateFenders();
        if (!IsCursor) UpdateDoors();
        if (WorldManager.IsGamePaused || IsCursor) return;
        CloseCrampedLids();
        bool live = OnOff && Powered;
        SetLights(HeadLights, live && Button1 == 1);
        Show(HeadLampGlow, live && Button1 == 1);
        SetLights(SideLights, live && WorkLightOn(0));
        SetLights(RearLights, live && WorkLightOn(1));
        Show(SideLampGlow, live && WorkLightOn(0));
        Show(RearLampGlow, live && WorkLightOn(1));
        SetLights(CabinLightList, Powered && Button2 == 1);
        Toggle(HeadlightsOn, HeadlightsOff, Button1 == 1);
        Toggle(CabinOn, CabinOff, Button2 == 1);
        Toggle(PumpOn, PumpOff, Importing == 1);
        Toggle(FilterOn, FilterOff, Exporting == 1);
        int level = OnOff ? PoweredValue : 0; // 1..5 = charge band
        for (int i = 0; i < BatteryBars.Count; i++)
            if (BatteryBars[i]) BatteryBars[i].SetActive(i < level);
        if (SlotDiagnostics && Time.time >= _nextDiag && VelocityMagnitude > 0.5f)
        {
            _nextDiag = Time.time + 3f;
            LogLiftSlots();
        }
        if (IsOccluded) return;
        AnimateSuspensionArms();
        AnimateShocks();
        if (SpeedNeedle) SpeedNeedle.localRotation = _speedRest * Quaternion.Euler(0f, 0f, -Mathf.Clamp(VelocityMagnitude / MaxSpeed, 0f, 1f) * 240f);
        if (PitchNeedle) PitchNeedle.localRotation = _pitchRest * Quaternion.Euler(0f, 0f, Mathf.Clamp(GetPitch(), -45f, 45f));
        if (RollNeedle) RollNeedle.localRotation = _rollRest * Quaternion.Euler(0f, 0f, Mathf.Clamp(-GetRoll(), -45f, 45f));
    }

    /// <summary>Temporary: why does a crate/tank on one lift stay behind? Logs each layer of the attachment.</summary>
    public static bool SlotDiagnostics = true;
    private float _nextDiag;

    private void LogLiftSlots()
    {
        for (int i = 12; i < 16 && i < Slots.Count; i++)
        {
            var slot = Slots[i];
            var occ = slot.Get();
            if (occ == null) continue;
            var loc = slot.Location ? slot.Location : transform;
            var rb = occ.RigidBody;
            var mr = occ.GetComponentInChildren<MeshRenderer>();
            if ((occ.ThingTransform.position - loc.position).magnitude < 0.2f) continue; // only report drift
            string parent = occ.ThingTransform.parent ? occ.ThingTransform.parent.name : "(none)";
            Debug.Log($"[RoverCargo][diag] slot {i} {slot.StringKey}: {occ.PrefabName} parent={parent} " +
                      $"transformOff={(occ.ThingTransform.position - loc.position).magnitude:0.00} " +
                      $"rbOff={(rb ? (rb.position - loc.position).magnitude : -1):0.00} kinematic={(rb ? rb.isKinematic.ToString() : "-")} " +
                      $"rbInterp={(rb ? rb.interpolation.ToString() : "-")} detect={(rb ? rb.detectCollisions.ToString() : "-")} " +
                      $"meshOff={(mr ? (mr.bounds.center - loc.position).magnitude : -1):0.00} meshOn={(mr ? mr.enabled.ToString() : "-")} " +
                      $"joint={(occ.Joint != null)} parentSlotOk={(occ.ParentSlot == slot)} speed={VelocityMagnitude:0.0}");
        }
    }

    private static void SetLights(List<Light> lights, bool on)
    {
        foreach (var l in lights)
            if (l && l.enabled != on) l.enabled = on;
    }

    private static void Toggle(List<GameObject> on, List<GameObject> off, bool state)
    {
        foreach (var g in on) if (g && g.activeSelf != state) g.SetActive(state);
        foreach (var g in off) if (g && g.activeSelf == state) g.SetActive(!state);
    }

    public override StringBuilder GetExtendedText()
    {
        var sb = base.GetExtendedText() ?? new StringBuilder();   // vanilla lines, including damage
        if (CargoHab.IsAnchoring(this)) sb.AppendLine("Hab deployed - stow to tow");
        if (BayDoorHinges.Count > 0)
        {
            var open = new List<string>();
            for (int b = 0; b < RoverRules.Bays.Length; b++) if (IsBayDoorOpen(b)) open.Add(RoverRules.BayLabel(RoverRules.Bays[b]));
            sb.AppendLine(open.Count == 0 ? "Bay doors shut (a wrench opens a door; crates and tanks load through open doors)"
                                          : "Bay doors open: " + string.Join(", ", open));
        }
        AppendUpgrades(sb);
        if (!HasCabin) return sb;
        var a = InternalAtmosphere;
        if (a != null && a.PressureGassesAndLiquids.ToFloat() < 0.1f)
            sb.AppendLine("Cabin: vacuum");
        else if (a != null)
            sb.AppendLine($"Cabin: {a.PressureGassesAndLiquids.ToFloat():0.0} kPa, {a.Temperature.ToFloat() - 273.15f:0.0} C, O2 {a.PartialPressureO2.ToFloat():0.0} kPa");
        if (this is not CargoTrailer) sb.AppendLine($"Air pump {(Importing == 1 ? "on" : "off")}, filtration {(Exporting == 1 ? "on" : "off")}");
        return sb;
    }

    public override void OnDestroy()
    {
        if (GameManager.GameState != GameState.None && !IsCursor && HasCabin) CircuitHolders.Deregister(this);
        base.OnDestroy();
    }

    // ---- chip host (same model as AIMeE / RobotMining) ----
    public ulong LastEditedBy { get; set; }
    protected int _codeErrorState;
    private readonly List<ILogicable> _batch = new();

    public Slot ChipSlot
    {
        get
        {
            foreach (var s in Slots) if (s.Type == Slot.Class.ProgrammableChip) return s;
            return null;
        }
    }

    public ProgrammableChip Chip => ChipSlot?.Get() as ProgrammableChip;

    /// <summary>Called for every registered holder each logic tick; runs the chip while the rover is on and powered.</summary>
    public void Execute()
    {
        if (GameManager.GameState != GameState.Running || IsCursor || WorldManager.IsGamePaused) return;
        var chip = Chip;
        var battery = Battery;
        if (chip == null || chip.CompilationError || !OnOff || !Powered || battery == null || battery.IsEmpty) return;
        battery.PowerStored -= 2.5f;
        chip.Execute(128);
    }

    public void ClearError() => RaiseError(0);
    public void RaiseError(int state) => _codeErrorState = state;
    public ILogicable GetLogicableFromIndex(int deviceIndex, int networkIndex = int.MinValue) => deviceIndex == int.MaxValue ? this : null;
    public bool IsValidIndex(int index) => index == int.MaxValue || index == 0;
    public void SetDeviceLabel(int index, string label) { }
    public string GetSourceCode() => Chip ? Chip.GetSourceCode() : "";
    public virtual List<LogicBinding> GetLogicBindings() => new() { new LogicBinding("ROVER") };
    public void HasPut() { }

    public void SetSourceCode(string sourceCode)
    {
        var chip = Chip;
        if (!chip) return;
        chip.SetSourceCode(sourceCode, this);
        chip.SendUpdate();
    }

    /// <summary>The rover itself or anything sitting in its slots (batteries, tanks, crates).</summary>
    public ILogicable GetLogicableFromId(int deviceId, int networkIndex = int.MinValue)
    {
        if (deviceId == 0) return null;
        if (deviceId == ReferenceId) return this;
        var found = Referencable.Find<ILogicable>(deviceId);
        if (found == null) return null;
        foreach (var slot in Slots) if (ReferenceEquals(slot.Get(), found)) return found;
        return null;
    }

    public List<ILogicable> GetBatchOutput()
    {
        _batch.Clear();
        foreach (var slot in Slots) _batch.Add(slot.Get() as ILogicable);
        return _batch;
    }

    /// <summary>A vehicle should not explode for a bad program (AIMeE does): just stop and flag the error.</summary>
    public UniTask HaltAndCatchFire()
    {
        RaiseError(1);
        return UniTask.CompletedTask;
    }

    public override void OnChildEnterInventory(DynamicThing newChild)
    {
        base.OnChildEnterInventory(newChild);
        if (newChild is ProgrammableChip chip && GameManager.GameState == GameState.Running)
        {
            chip.Reset();
            ClearError();
        }
    }

    // ---- logic: what chips (onboard or via a logic transmitter) can read and write ----
    public float TargetX, TargetY, TargetZ;

    public override bool CanLogicRead(LogicType logicType)
    {
        switch (logicType)
        {
            case LogicType.PositionX: case LogicType.PositionY: case LogicType.PositionZ:
            case LogicType.VelocityMagnitude: case LogicType.VelocityX: case LogicType.VelocityY: case LogicType.VelocityZ:
            case LogicType.VelocityRelativeX: case LogicType.VelocityRelativeY: case LogicType.VelocityRelativeZ:
            case LogicType.ForwardX: case LogicType.ForwardY: case LogicType.ForwardZ: case LogicType.Orientation:
            case LogicType.PressureExternal: case LogicType.TemperatureExternal: case LogicType.PressureInternal:
            case LogicType.Filtration: case LogicType.Activate: case LogicType.Mode:
            case LogicType.PressureSetting: case LogicType.TemperatureSetting:
            case LogicType.TargetX: case LogicType.TargetY: case LogicType.TargetZ:
            case LogicType.Charge: case LogicType.Ratio: case LogicType.Error:
                return true;
            default:
                return base.CanLogicRead(logicType);
        }
    }

    public override double GetLogicValue(LogicType logicType)
    {
        switch (logicType)
        {
            case LogicType.PositionX: return Position.x;
            case LogicType.PositionY: return Position.y;
            case LogicType.PositionZ: return Position.z;
            case LogicType.VelocityMagnitude: return VelocityMagnitude;
            case LogicType.VelocityX: return Velocity.x;
            case LogicType.VelocityY: return Velocity.y;
            case LogicType.VelocityZ: return Velocity.z;
            case LogicType.VelocityRelativeX: return RelativeVelocity.x;
            case LogicType.VelocityRelativeY: return RelativeVelocity.y;
            case LogicType.VelocityRelativeZ: return RelativeVelocity.z;
            case LogicType.ForwardX: return ThingTransform.forward.x;
            case LogicType.ForwardY: return ThingTransform.forward.y;
            case LogicType.ForwardZ: return ThingTransform.forward.z;
            case LogicType.Orientation: return ThingTransform.eulerAngles.y; // compass heading, degrees
            case LogicType.PressureExternal: return WorldAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
            case LogicType.TemperatureExternal: return WorldAtmosphere?.Temperature.ToDouble() ?? 0.0;
            case LogicType.PressureInternal: return InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
            case LogicType.Filtration: return Exporting;
            case LogicType.Activate: return Importing;
            case LogicType.Mode: return (Button1 == 1 ? 1 : 0) | (Button2 == 1 ? 2 : 0);
            case LogicType.PressureSetting: return CabinOutputSettingKPa;
            case LogicType.TemperatureSetting: return CabinTemperatureK;
            case LogicType.TargetX: return TargetX;
            case LogicType.TargetY: return TargetY;
            case LogicType.TargetZ: return TargetZ;
            case LogicType.Charge: return BatteryTotals().stored;
            case LogicType.Ratio: { var (s, m) = BatteryTotals(); return m > 0 ? s / m : 0.0; }
            case LogicType.Error: return _codeErrorState;
            default: return base.GetLogicValue(logicType);
        }
    }

    public override bool CanLogicWrite(LogicType logicType)
    {
        switch (logicType)
        {
            case LogicType.Filtration: case LogicType.Activate: case LogicType.Mode:
            case LogicType.PressureSetting: case LogicType.TemperatureSetting:
            case LogicType.TargetX: case LogicType.TargetY: case LogicType.TargetZ:
                return true;
            default:
                return base.CanLogicWrite(logicType);
        }
    }

    public override void SetLogicValue(LogicType logicType, double value)
    {
        switch (logicType)
        {
            case LogicType.Filtration: SetInteractable(InteractableType.Export, value != 0 ? 1 : 0); return;
            case LogicType.Activate: SetInteractable(InteractableType.Import, value != 0 ? 1 : 0); return;
            case LogicType.Mode:
                int m = (int)value;
                SetInteractable(InteractableType.Button1, (m & 1) != 0 ? 1 : 0);
                SetInteractable(InteractableType.Button2, (m & 2) != 0 ? 1 : 0);
                return;
            case LogicType.PressureSetting: CabinOutputSettingKPa = Mathf.Clamp((float)value, 0f, 202.65f); return;
            case LogicType.TemperatureSetting: CabinTemperatureK = Mathf.Clamp((float)value, 273.15f, 313.15f); return;
            case LogicType.TargetX: TargetX = (float)value; return;
            case LogicType.TargetY: TargetY = (float)value; return;
            case LogicType.TargetZ: TargetZ = (float)value; return;
        }
        base.SetLogicValue(logicType, value);
    }

    private void SetInteractable(InteractableType action, int state)
    {
        var it = Interactables.Find(i => i.Action == action);
        if (it != null && it.State != state && GameManager.RunSimulation) OnServer.Interact(it, state);
    }

    protected (double stored, double max) BatteryTotals()
    {
        double s = 0, m = 0;
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Type == Slot.Class.Battery && IsPowerSlot(i) && Slots[i].Get() is BatteryCell b) { s += b.PowerStored; m += b.PowerMaximum; }
        return (s, m);
    }

    /// <summary>What the original rover's dash screen shows (RoverDashboard.Build), from state every machine has.</summary>
    public RoverInputs DashInputs()
    {
        bool sim = GameManager.RunSimulation;
        double j = 0, jMax = 0;
        int cells = 0;
        bool charged = false;
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Type == Slot.Class.Battery && IsPowerSlot(i) && Slots[i].Get() is BatteryCell b)
            {
                // PowerStored is server-side: a client knows a cell's charge as its synced percentage and mode
                double stored = sim ? b.PowerStored : b.CurrentPowerPercentage / 100.0 * b.PowerMaximum;
                j += stored; jMax += b.PowerMaximum; cells++;
                charged |= sim ? b.PowerStored > 0f : b.CurrentPowerPercentage > 0 || !b.IsEmpty;
            }
        var cabin = InternalAtmosphere;
        double moles = cabin?.TotalMoles.ToDouble() ?? 0;
        GasCanister air = AirTank, anyAir = null;
        foreach (var slot in _airTankSlots)
        {
            if (slot == PropellantSlot && HasUpgrade(Upgrade.Thrusters)) continue;
            if (slot.Get() is GasCanister c) { anyAir = c; break; }
        }
        if (!air) air = anyAir;                              // a fitted but empty canister reads empty, not none
        var waste = WasteTank;
        int filters = 0;
        foreach (var f in _filterSlots) if (f.Get() != null) filters++;
        string towing = null;
        foreach (var r in AllRovers)
            if (r is CargoTrailer t && (object)t.TowingRover == this) { towing = t is CargoHab ? "HAB" : "TRAILER"; break; }
        bool bays = BayDoorHinges.Count > 0;
        int filled = 0;                                                   // full bays (a crate, or both tanks)
        if (bays)
            foreach (var bay in RoverRules.Bays)
                if (RoverRules.BayFull(bay, i => i < Slots.Count && Slots[i].Get() != null)) filled++;
        return new RoverInputs
        {
            On = OnOff, Charged = charged, Remote = !sim, BatteryJ = j, BatteryMaxJ = jMax, Cells = cells,
            SpeedMs = VelocityMagnitude, MaxSpeedMs = MaxSpeed, PitchDeg = GetPitch(), RollDeg = -GetRoll(),   // GetRoll: + = right side up
            CabinKPa = cabin?.PressureGassesAndLiquids.ToDouble() ?? 0,
            CabinO2Pct = moles > 1e-6 ? cabin.GasMixture.Oxygen.Quantity.ToDouble() / moles * 100 : 0,
            CabinC = (cabin?.Temperature.ToDouble() ?? 273.15) - 273.15,
            AirKPa = air ? air.InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0 : null,
            AirMaxKPa = air ? air.MaxPressure.ToDouble() : null,
            WasteKPa = waste ? waste.InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0 : null,
            WasteMaxKPa = waste ? GetWasteMaxPressure().ToDouble() : null,
            HeadlightsOn = Button1 == 1, CabinLightOn = Button2 == 1, AirPumpOn = Importing == 1, FilterOn = Exporting == 1,
            SideLightsOn = WorkLightOn(0), RearLightsOn = WorkLightOn(1),
            ThrustersFitted = HasUpgrade(Upgrade.Thrusters), ThrustersOn = ThrustersOn, FiltersFitted = filters, Towing = towing,
            BaysFilled = bays ? filled : null, BaysTotal = bays ? RoverRules.Bays.Length : null, DoorsOpen = bays ? BayDoorsOpen() : 0,
        };
    }

    /// <summary>Does this battery-class slot power the vehicle? All of them on a rover; the hab keeps its charger's
    /// cells out.</summary>
    protected virtual bool IsPowerSlot(int index) => true;

    // ---- IInternalConditioner ----
    public GasCanister WasteTank => Slots.Count > WasteTankSlotIndex ? Slots[WasteTankSlotIndex].Get() as GasCanister : null;

    public GasCanister AirTank
    {
        get
        {
            foreach (var slot in _airTankSlots)
            {
                if (slot == PropellantSlot && HasUpgrade(Upgrade.Thrusters)) continue;   // the thrusters' propellant, not air
                if (slot.Get() is GasCanister c && !c.IsEmpty) return c;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ vehicle upgrades
    // (spec docs/superpowers/specs/2026-09-26-storm-upgrades-design.md) saved flags on spare Interactables; parts and
    // their removal colliders come from the prefab builder (CargoPrefabs.AddUpgradeParts)
    public const InteractableType ArmourAction = InteractableType.Button9, FairingsAction = InteractableType.Button10,
        ThrustersAction = InteractableType.Button11, ThrustSwitchAction = InteractableType.Button12;
    public GameObject ArmourParts, FairingParts, ThrusterParts;
    public List<Collider> ArmourColliders = new(), FairingColliders = new(), ThrusterColliders = new();
    public List<Transform> Fenders = new();            // fairing fenders pivoted on their hubs: they ride the suspension
    private int[] _fenderWheel;
    private Vector3[] _fenderAxle;                     // each wheel's spin axis in its own frame (spin leaves it fixed)
    public bool IsThrusting { get; protected set; }

    private static InteractableType ActionOf(Upgrade u) => u switch
    {
        Upgrade.Armour => ArmourAction, Upgrade.Fairings => FairingsAction, _ => ThrustersAction,
    };
    private Interactable[] _flags;                     // Button9-12, looked up once (the list is fixed after the prefab build)
    private Interactable Flag(InteractableType a)
    {
        int k = a - ArmourAction;
        if (k < 0 || k > 3) return null;
        if (_flags == null)
        {
            _flags = new Interactable[4];
            foreach (var i in Interactables) { int j = i.Action - ArmourAction; if (j >= 0 && j < 4) _flags[j] = i; }
        }
        return _flags[k];
    }
    public bool HasUpgrade(Upgrade u) => Flag(ActionOf(u)) is { State: 1 };
    public bool ThrustersOn => HasUpgrade(Upgrade.Thrusters) && Flag(ThrustSwitchAction) is { State: 1 };
    /// <summary>Air slot 3 (the last O2 tank): the thrusters' propellant tank while they are fitted.</summary>
    public Slot PropellantSlot => _airTankSlots.Count >= 3 ? _airTankSlots[2] : null;

    /// <summary>The StormDamage setting (Plugin; 0..1): applied at every storm tick, so armour fitted or removed later and the
    /// setting take effect at once on every rover and trailer (Workshop feedback 2026-10-09).</summary>
    public static float StormDamageSetting = 0.25f;

    public override bool CanBeWeathered() =>
        StormRules.Weathered(HasUpgrade(Upgrade.Armour), StormDamageSetting) && base.CanBeWeathered();

    public override void DoWeatherDamage(float damageMultiplier) =>
        base.DoWeatherDamage(damageMultiplier * StormRules.StormDamageMultiplier(HasUpgrade(Upgrade.Armour), StormDamageSetting));

    public override Vector3 GetStormWindVector() => base.GetStormWindVector() * StormRules.WindFactor(HasUpgrade(Upgrade.Fairings));

    /// <summary>Vanilla restores only some interactable states: restore the upgrade flags, the thruster switch, the bay
    /// doors and the work lights.</summary>
    public override void DeserializeSave(ThingSaveData saveData)
    {
        base.DeserializeSave(saveData);
        if (saveData?.States == null) return;
        foreach (var st in saveData.States)
        {
            foreach (var a in new[] { ArmourAction, FairingsAction, ThrustersAction, ThrustSwitchAction })
                if (st.StateName == a.ToString() && Flag(a) is { } f) f.State = st.State;
            for (int d = 0; BayDoorHinges.Count > 0 && d < RoverRules.Doors.Length; d++)          // not the hab: its buttons are its own
                if (st.StateName == RoverRules.Doors[d].Action && DoorFlag(d) is { } df) df.State = st.State;
            for (int k = 0; k < RoverRules.WorkLights.Length; k++)                                 // the original rover's alone
                if ((SideLights.Count > 0 || RearLights.Count > 0) && st.StateName == RoverRules.WorkLights[k] && WorkFlag(k) is { } wf)
                    wf.State = st.State;
        }
    }

    /// <summary>Each fitted fender follows its tyre's rendered hub (the suspension's full 0.5 m travel) and steer, so a
    /// tyre can never reach it. Rendered wheels move on every machine, so clients match.</summary>
    private void UpdateFenders()
    {
        if (Fenders.Count == 0 || !FairingParts || !FairingParts.activeInHierarchy || ArmWheels.Count == 0) return;
        var root = FairingParts.transform;
        if (_fenderWheel == null || _fenderWheel.Length != Fenders.Count) InitFenders();
        for (int f = 0; f < Fenders.Count; f++)
        {
            int i = _fenderWheel[f];
            if (i < 0 || !Fenders[f] || !ArmWheels[i]) continue;
            var w = ArmWheels[i];
            Fenders[f].localPosition = root.InverseTransformPoint(w.position);
            var axle = root.InverseTransformDirection(w.TransformDirection(_fenderAxle[f]));
            if (axle.x < 0f) axle = -axle;
            Fenders[f].localRotation = Quaternion.Euler(0f, Mathf.Atan2(-axle.z, axle.x) * Mathf.Rad2Deg, 0f);   // steer only
        }
    }

    private static void Show(GameObject g, bool on) { if (g && g.activeSelf != on) g.SetActive(on); }
    private static void Show(List<GameObject> gs, bool on) { foreach (var g in gs) Show(g, on); }

    /// <summary>The upgrade's parts on this vehicle (CargoPrefabs.AddUpgradeParts), or null when it has none.</summary>
    private GameObject UpgradePartsOf(Upgrade u) => u switch { Upgrade.Armour => ArmourParts, Upgrade.Fairings => FairingParts, _ => ThrusterParts };

    /// <summary>Wrench + material in the other hand fits that upgrade; a wrench on an upgrade's own parts removes it
    /// (and drops what it cost). Null = not an upgrade action: the caller does its usual wrench job.</summary>
    /// <summary>Is this one of the fitted upgrades' part colliders (UpgradeCursor lets the wrench use these triggers)?</summary>
    public bool IsUpgradeCollider(Collider c) => c && (ArmourColliders.Contains(c) || FairingColliders.Contains(c) || ThrusterColliders.Contains(c));

    /// <summary>The fitted upgrade whose part the wrench is on: its collider, or - on the server, where the completed
    /// attack arrives without a collider (as CargoHab.AtPanel) - a part collider within 5 cm of the hit point.</summary>
    private Upgrade? PartAt(Attack attack)
    {
        foreach (var (u, cols) in new[] { (Upgrade.Armour, ArmourColliders), (Upgrade.Fairings, FairingColliders), (Upgrade.Thrusters, ThrusterColliders) })
        {
            if (!HasUpgrade(u)) continue;
            if (attack.TargetCollider != null) { if (cols.Contains(attack.TargetCollider)) return u; continue; }
            foreach (var c in cols)
                if (c && c.enabled && c.gameObject.activeInHierarchy && (c.ClosestPoint(attack.Position) - attack.Position).sqrMagnitude < 0.05f * 0.05f)
                    return u;
        }
        return null;
    }

    protected DelayedActionInstance UpgradeAttack(Attack attack, bool doAction)
    {
        if (!(attack.SourceItem is Wrench)) return null;
        var part = PartAt(attack);
        var held = attack.OtherHandOccupant();
        var heldUp = StormRules.UpgradeFor(held ? held.PrefabName : null);
        var act = StormRules.PickAction(part, heldUp, heldUp is Upgrade hu && HasUpgrade(hu));
        if (act == WrenchAction.None) return null;
        if (act == WrenchAction.Remove && part is Upgrade u)
        {
            var rm = new DelayedActionInstance { Duration = StormRules.FitSeconds, ActionMessage = StormRules.RemoveVerb(u) };
            if (u == Upgrade.Thrusters && PropellantSlot?.Get() is GasCanister pc && !pc.IsEmpty)
                rm.ExtendedMessage = "The top air slot becomes an air tank again: empty the propellant first";
            if (!doAction) return rm.Succeed();
            if (GameManager.RunSimulation)
            {
                OnServer.Interact(Flag(ActionOf(u)), 0);
                if (u == Upgrade.Thrusters && Flag(ThrustSwitchAction) is { } sw) OnServer.Interact(sw, 0);
                var prefab = Prefab.Find<Item>(StormRules.Material(u));
                int cap = prefab is Stackable ps ? ps.MaxQuantity : 0;
                // refund what the fit charged, in full stacks, at the wrench (where the player stands), not on the part
                var at = (attack.SourceItem ? attack.SourceItem.ThingTransform.position : attack.Position) + Vector3.up * 0.3f;
                if (prefab)
                    foreach (int n in StormRules.DropStacks(StormRules.NeededFor(u, cap), cap))
                        OnServer.CreateOrStack(prefab, n, at, Quaternion.identity);
            }
            return rm.Succeed();
        }
        if (heldUp is not Upgrade up) return null;
        var fit = new DelayedActionInstance { Duration = StormRules.FitSeconds, ActionMessage = StormRules.FitVerb(up) };
        int count = held is Stackable s ? s.Quantity : 1;
        int maxStack = held is Stackable hs ? hs.MaxQuantity : 0;            // one hand holds one stack
        var refusal = StormRules.FitRefusal(up, HasUpgrade(up), count, this is not CargoTrailer, maxStack);
        if (refusal != null) return fit.Fail(refusal);
        if (Flag(ActionOf(up)) == null) return fit.Fail("This vehicle cannot take upgrades");
        if (UpgradePartsOf(up) == null) return fit.Fail("This model has no parts for that upgrade yet");   // the original rover until sub-project 4
        if (!doAction) return fit.Succeed();
        if (GameManager.RunSimulation)
        {
            int need = StormRules.NeededFor(up, maxStack);
            if (held is Stackable st && st.Quantity > need) st.Quantity -= need; else OnServer.Destroy(held);
            OnServer.Interact(Flag(ActionOf(up)), 1);
            if (up == Upgrade.Thrusters && Flag(ThrustSwitchAction) is { } sw) OnServer.Interact(sw, 1);
        }
        return fit.Succeed();
    }

    public override DelayedActionInstance AttackWith(Attack attack, bool doAction = true) =>
        DoorAttack(attack, doAction) ?? UpgradeAttack(attack, doAction) ?? TeardownAttack(attack, doAction) ?? base.AttackWith(attack, doAction);

    /// <summary>The frame this vehicle comes apart into (CargoPrefabs.TryFrame, staged frames only); null: it does not.</summary>
    public RoverFrame TeardownFrame;

    protected virtual bool TeardownHitched => false;        // the trailers: hitched to a rover
    protected virtual bool TeardownHabOut => false;         // the hab: deployed or moving

    private bool IsTowing()
    {
        foreach (var r in AllRovers) if (r is CargoTrailer t && t.TowedBy == this) return true;
        return false;
    }

    /// <summary>The build in reverse (the user, check-in 3): the drill takes a finished vehicle back to its frame at the
    /// stage before last, on the nearest grid cell and 90 deg heading; the last step's materials come back and the
    /// contents drop out (as the base game's deconstruct does); the frame's own stages then lead down to its kit.
    /// Null for any other tool, so the base game's tools keep working.</summary>
    protected DelayedActionInstance TeardownAttack(Attack attack, bool doAction)
    {
        var frame = TeardownFrame;
        var drill = frame ? frame.BuildStates[0].Tool.ToolExit : null;
        if (!drill || !(attack.SourceItem is Tool tool) || tool.PrefabHash != drill.PrefabHash) return null;
        var act = new DelayedActionInstance { Duration = FrameData.TeardownSeconds, ActionMessage = Assets.Scripts.Inventory.ActionStrings.Deconstruct };
        if (!tool.IsOperable) return act.Fail("The drill cannot run");
        string refusal = FrameData.TeardownRefusal(IsTowing(), TeardownHitched, TeardownHabOut, RigidBody ? RigidBody.velocity.magnitude : 0f);
        if (refusal != null) return act.Fail(refusal);
        if (!doAction || !GameManager.RunSimulation) return act.Succeed();
        var gc = GridController.World;
        var at = gc.ClampWorld(ThingTransform.position, frame.GridSize, frame.GridOffset);
        var rot = Quaternion.Euler(0f, Mathf.Round(ThingTransform.eulerAngles.y / 90f) * 90f, 0f);
        var built = Constructor.SpawnConstruct(new CreateStructureInstance(frame, gc.WorldToLocalGrid(at, frame.GridSize, frame.GridOffset), rot, OwnerClientId));
        if (!built) return act.Fail("The frame could not be placed here");
        built.UpdateBuildStateAndVisualizer(FrameData.TeardownState(frame.BuildStates.Count));
        var ev = new ConstructionEventInstance
        {
            Parent = built, Position = attack.Position, Rotation = built.ThingTransform.rotation,
            SteamId = OwnerClientId, OtherHandSlot = attack.OtherHand,
        };
        frame.BuildStates[frame.BuildStates.Count - 1].Tool.Deconstruct(ev);      // the last step's materials back
        foreach (var slot in Slots) if (slot.Get() != null) slot.PlayerMoveToWorld();
        tool.OnUseItem(frame.BuildStates[0].Tool.ExitQuantity, this);
        OnServer.Destroy(this);
        return act.Succeed();
    }

    /// <summary>Upgrade lines for the hover text (spec "Hover text").</summary>
    protected void AppendUpgrades(StringBuilder sb)
    {
        bool any = false;
        if (HasUpgrade(Upgrade.Armour)) { sb.AppendLine("Storm armour fitted (no storm damage)"); any = true; }
        if (HasUpgrade(Upgrade.Fairings)) { sb.AppendLine("Wind fairings fitted (storm push halved)"); any = true; }
        if (HasUpgrade(Upgrade.Thrusters))
        {
            float rig, rover;
            lock (_burnLock) { rig = _rigMass; rover = _roverMass; }
            if (rover <= 0f && RigidBody) rig = rover = RigidBody.mass;
            sb.AppendLine($"Thrusters: {ThrusterStatus}, 1 MPa per {StormRules.MinutesPerMPa(rig, rover):0} min (propellant: top air slot)");
            any = true;
        }
        if (!any) sb.AppendLine("Upgrades: wrench + 10 steel sheets (armour), 20 plastic sheets (fairings)" + (this is CargoTrailer ? "" : " or a rocket engine kit (thrusters)"));
    }

    // ------------------------------------------------------------------ covered bays (the original rover, sub-project 3)
    // door leaves hang on BayDoorHinges (RoverRules.Doors order), each with its trigger collider; their states are the
    // Button5-8 interactables (state 1 = open; synced, saved). Empty on the trailers.
    public List<Transform> BayDoorHinges = new();
    public List<BoxCollider> BayDoorColliders = new();
    private Interactable[] _doorFlags;
    private float[] _doorOpen;                         // each leaf's shown open fraction; null until the first frame

    private Interactable DoorFlag(int d)
    {
        if (_doorFlags == null)
        {
            _doorFlags = new Interactable[RoverRules.Doors.Length];
            foreach (var i in Interactables)
                for (int k = 0; k < _doorFlags.Length; k++)
                    if (i.Action.ToString() == RoverRules.Doors[k].Action) _doorFlags[k] = i;
        }
        return d >= 0 && d < _doorFlags.Length ? _doorFlags[d] : null;
    }

    // ---- the work lights (RoverRules.WorkLights): Button13 the side lights, Button14 the rear
    public const InteractableType SideLightsAction = InteractableType.Button13, RearLightsAction = InteractableType.Button14;
    private Interactable[] _workFlags;

    private Interactable WorkFlag(int k)
    {
        if (_workFlags == null)
        {
            _workFlags = new Interactable[RoverRules.WorkLights.Length];
            foreach (var i in Interactables)
                for (int w = 0; w < _workFlags.Length; w++)
                    if (i.Action.ToString() == RoverRules.WorkLights[w]) _workFlags[w] = i;
        }
        return k >= 0 && k < _workFlags.Length ? _workFlags[k] : null;
    }

    /// <summary>Is work light k (0 side, 1 rear) switched on? False on a rover without them.</summary>
    private bool WorkLightOn(int k) => WorkFlag(k) is { State: 1 };

    private float _nextLidCheck;

    /// <summary>Crate lids open where RoverRules.LidBlock refuses them (a bay door shut on an open crate, a save) are
    /// closed by the server, twice a second.</summary>
    private void CloseCrampedLids()
    {
        if (!GameManager.RunSimulation || this is CargoTrailer || BayDoorHinges.Count == 0 || Time.time < _nextLidCheck) return;
        _nextLidCheck = Time.time + 0.5f;
        foreach (int i in RoverRules.LidsToClose(k => k < Slots.Count && Slots[k].Get() != null,
                                                 k => k < Slots.Count && Slots[k].Get() is Container open && open.IsOpen, IsBayDoorOpen))
            if (Slots[i].Get() is Container crate && crate.InteractOpen != null) OnServer.Interact(crate.InteractOpen, 0);
    }

    private static readonly AccessTools.FieldRef<Rover, List<Slot>> BatterySlotsOf = AccessTools.FieldRefAccess<Rover, List<Slot>>("_batterySlots");

    /// <summary>The power tick (IPowered re-implemented: Rover's is not virtual): vanilla's (the motor, the headlights,
    /// the cabin light), then the work lights' draw (the user's request) out of the battery vanilla drains, while the
    /// rover is on.</summary>
    public new void OnPowerTick()
    {
        base.OnPowerTick();
        if (!GameManager.RunSimulation || IsCursor || GameManager.GameState != GameState.Running || !OnOff) return;
        float draw = RoverRules.WorkLightDraw(SideLights.Count > 0 && WorkLightOn(0), RearLights.Count > 0 && WorkLightOn(1));
        var slots = BatterySlotsOf(this);
        if (draw <= 0f || slots == null) return;
        int i = RoverRules.DrainSlot(slots.ConvertAll(s => s.Occupant is BatteryCell b && !b.IsEmpty));
        if (i >= 0) ((BatteryCell)slots[i].Occupant).PowerStored -= draw;
    }

    /// <summary>Is this one of the bay door leaves' trigger colliders (UpgradeCursor lets the wrench use it)?</summary>
    public bool IsBayDoorCollider(Collider c) => c is BoxCollider bc && BayDoorColliders.Contains(bc);

    /// <summary>Is this bay's door open? False on a rover without doors.</summary>
    public bool IsBayDoorOpen(int bay)
    {
        for (int d = 0; d < RoverRules.Doors.Length; d++)
            if (RoverRules.Doors[d].Bay == bay) return d < BayDoorHinges.Count && BayDoorHinges[d] && DoorFlag(d) is { State: 1 };
        return false;
    }

    public int BayDoorsOpen()
    {
        int n = 0;
        for (int b = 0; b < RoverRules.Bays.Length; b++) if (IsBayDoorOpen(b)) n++;
        return n;
    }

    /// <summary>Every machine turns each leaf toward its synced state; on the first frame (a loaded save) it shows the
    /// state at once, without a swing.</summary>
    private void UpdateDoors()
    {
        if (BayDoorHinges.Count == 0) return;
        bool first = _doorOpen == null;
        if (first) _doorOpen = new float[BayDoorHinges.Count];
        float step = Time.deltaTime / RoverRules.DoorSeconds;
        for (int d = 0; d < BayDoorHinges.Count && d < RoverRules.Doors.Length; d++)
        {
            if (!BayDoorHinges[d]) continue;
            float target = DoorFlag(d) is { State: 1 } ? 1f : 0f;
            float open = first ? target : Mathf.MoveTowards(_doorOpen[d], target, step);
            if (!first && open == _doorOpen[d]) continue;
            _doorOpen[d] = open;
            BayDoorHinges[d].localRotation = Quaternion.Euler(0f, 0f, RoverRules.DoorAngle(RoverRules.Doors[d].Side, open));
        }
    }

    /// <summary>The leaf the wrench is on: its collider, or on the server (the completed attack arrives without a
    /// collider) the leaf within 5 cm of the hit point; -1 = none.</summary>
    private int DoorAt(Attack attack)
    {
        for (int d = 0; d < BayDoorColliders.Count; d++)
        {
            var c = BayDoorColliders[d];
            if (!c) continue;
            if (attack.TargetCollider != null) { if (attack.TargetCollider == c) return d; continue; }
            if ((c.ClosestPoint(attack.Position) - attack.Position).sqrMagnitude < 0.05f * 0.05f) return d;
        }
        return -1;
    }

    /// <summary>A wrench on a bay door opens or closes it. Null = not a door job (an upgrade's material in the other
    /// hand means fitting the upgrade).</summary>
    protected DelayedActionInstance DoorAttack(Attack attack, bool doAction)
    {
        if (!(attack.SourceItem is Wrench) || BayDoorColliders.Count == 0) return null;
        var held = attack.OtherHandOccupant();
        if (StormRules.UpgradeFor(held ? held.PrefabName : null) != null) return null;   // fitting an upgrade
        int d = DoorAt(attack);
        var flag = d >= 0 ? DoorFlag(d) : null;
        if (flag == null) return null;
        bool open = flag.State == 1;
        var act = new DelayedActionInstance { Duration = RoverRules.DoorWrenchSeconds, ActionMessage = open ? "Close bay door" : "Open bay door" };
        if (!doAction) return act.Succeed();
        if (GameManager.RunSimulation) OnServer.Interact(flag, open ? 0 : 1);
        return act.Succeed();
    }

    private float _burnSeconds, _rigMass, _roverMass;     // written on the main thread, read by the atmos tick
    private readonly object _burnLock = new();
    private readonly List<ParticleSystem> _puffs = new();
    private bool _puffOn, _puffBuilt;
    private const float PodNozzleTop = 0.21f;          // tools/blender_trailer_model.py UPG_POD_TOP

    /// <summary>Main thread (GripAssist's physics postfix, where this machine has authority): do the thrusters fire this
    /// step? Also caches the rig mass (rover + hitched trailers) for the burn.</summary>
    public void UpdateThrust(float dt)
    {
        int grounded = 0;
        foreach (var w in Wheels) if (w?.WheelCollider != null && w.WheelCollider.isGrounded) grounded++;
        bool fuel = PropellantSlot?.Get() is GasCanister c && !c.IsEmpty;
        bool driven = Slots.Count > DriverSlotIndex && Slots[DriverSlotIndex].Get() != null;
        var rb = RigidBody;
        float speed = rb && !rb.isKinematic ? rb.velocity.magnitude : 0f;
        IsThrusting = Room == null && StormRules.ThrusterFiring(HasUpgrade(Upgrade.Thrusters), ThrustersOn, fuel, grounded > 0,
            WorldManager.WorldGravity, speed, driven, roverOn: OnOff && Powered, held: (rb && rb.isKinematic) || CargoHab.IsAnchoring(this));
        if (!IsThrusting) return;
        float rig = rb ? rb.mass : 0f;
        foreach (var r in AllRovers)
            if (r is CargoTrailer t && (object)t.TowingRover == this && t.RigidBody) rig += t.RigidBody.mass;
        lock (_burnLock) { _burnSeconds += dt; _roverMass = rb ? rb.mass : 0f; _rigMass = rig; }
    }

    /// <summary>Atmos tick (worker thread, server): vent the propellant burnt since the last tick to the world, at
    /// StormRules.BurnKPaPerSecond of the tank's pressure.</summary>
    private void BurnPropellant()
    {
        float secs, rig, rover;
        lock (_burnLock) { secs = _burnSeconds; rig = _rigMass; rover = _roverMass; _burnSeconds = 0f; }
        if (secs <= 0f || !(PropellantSlot?.Get() is GasCanister c) || c.InternalAtmosphere == null) return;
        float p = c.InternalAtmosphere.PressureGassesAndLiquids.ToFloat();
        if (p <= 0f) return;
        float fraction = Mathf.Clamp01(StormRules.BurnKPaPerSecond(rig, rover) * secs / p);
        var n = c.InternalAtmosphere.TotalMoles * fraction;
        if (n <= MoleQuantity.Zero) return;
        GridController.AtmosphericsController.CloneGlobalAtmosphere(WorldGrid, 0L)
            .Add(c.InternalAtmosphere.Remove(n, AtmosphereHelper.MatterState.All));
    }

    /// <summary>Cold-gas puff at each nozzle while firing: the game's jetpack exhaust, cloned at runtime (none if the
    /// jetpack has no particle system; nothing shipped).</summary>
    private void UpdatePuff()
    {
        if (_puffOn == IsThrusting) return;
        _puffOn = IsThrusting;
        if (!_puffBuilt && _puffOn && ThrusterParts)
        {
            _puffBuilt = true;
            ParticleSystem src = null;
            foreach (var name in new[] { "ItemJetpackBasic", "ItemHardJetpack" })
                if (!src && Prefab.Find<Thing>(name) is { } jp) src = jp.GetComponentInChildren<ParticleSystem>(true);
            if (src)
                foreach (var t in ThrusterParts.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Nozzle")) continue;
                    var ps = Instantiate(src, t, false);
                    ps.transform.localPosition = Vector3.up * PodNozzleTop;         // the bell's exit
                    ps.transform.localRotation = Quaternion.identity;               // the pod's local up is the bell's axis
                    var main = ps.main; main.loop = true; main.playOnAwake = false;
                    _puffs.Add(ps);
                }
        }
        foreach (var ps in _puffs) { if (!ps) continue; if (_puffOn) ps.Play(true); else ps.Stop(true, ParticleSystemStopBehavior.StopEmitting); }
    }

    public string ThrusterStatus => !HasUpgrade(Upgrade.Thrusters) ? "none" : !ThrustersOn ? "off" : !(OnOff && Powered) ? "rover off"
        : !(PropellantSlot?.Get() is GasCanister c && !c.IsEmpty) ? "no propellant" : IsThrusting ? "firing" : "on";

    public float Efficiency => DamageState.TotalRatioClampedUndamaged;
    public Atmosphere GetInternalAtmosphere() => InternalAtmosphere;
    public void SetInternalAtmosphere(Atmosphere atmosphere) => InternalAtmosphere = atmosphere;
    public float GetOutputSetting() => CabinOutputSettingKPa;
    public TemperatureKelvin GetOutputTemperature() => new(CabinTemperatureK);
    public PressurekPa GetPressurePerTick() => new(PressurePerTickKPa);
    /// <summary>The waste tank fills to 95 % of the fitted canister's own limit (a regular and a smart canister differ);
    /// the Mk I's fixed 4053 kPa only without a canister.</summary>
    public PressurekPa GetWasteMaxPressure() =>
        new(WasteTank ? StormRules.WasteLimitKPa(WasteTank.MaxPressure.ToFloat()) : WasteMaxPressureKPa);
    public MoleEnergy GetMaxEnergy() => new(ConditionerMaxEnergy);
    public List<Slot> GetFilterSlots() => _filterSlots;
    public void SetConditioningHandler(IInternalConditionerHandler handler) => _conditioner = handler;
}
