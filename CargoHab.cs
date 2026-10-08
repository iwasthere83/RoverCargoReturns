using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Vehicles;
using Assets.Scripts.Util;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Habitat trailer (spec docs/superpowers/specs/2026-09-25-habitat-trailer-design.md, section 2). One saved, synced
/// state - the Button4 interactable on the driver-side control panel, 0 Travel / 1 Deployed - drives a 5 s sequence:
/// lock (kinematic) -> legs to the ground -> slide-out out -> ladder down; stow runs it backwards. A wrench on the
/// panel toggles it (with refusal checks); a wrench anywhere else keeps the trailer's hitch behaviour (not while
/// deployed: hitching would move the locked hab).
/// </summary>
public class CargoHab : CargoTrailer, IExitable, ILifeSuspender
{
    public const InteractableType DeployAction = InteractableType.Button4;
    public const float CycleSeconds = 5f;
    public Transform SlideOut, Ladder;
    public List<Transform> Legs = new();
    public Vector3 SlideTravel, LadderStowEuler;
    public float LegStowH = 0.5f, LegMaxStroke = 0.6f;
    public Collider DeployPanel;
    public const InteractableType DoorAction = InteractableType.Button5;
    public const InteractableType LockerAction = InteractableType.Button6;
    public const InteractableType ShowerAction = InteractableType.Button7, ToiletAction = InteractableType.Button8;
    public Transform ShowerNode, ToiletNode;           // the copied shower (BuildState01) and toilet nodes
    public Transform ShowerValve;                       // its valve lever (turns a quarter-turn when on)
    public AudioSource ShowerWater;                     // the game shower's running-water loop, on the copy
    public Transform LockerLeafA, LockerLeafB, LockerLeafC, LockerLeafD;   // Door1, Door1/Door2Open, Door3, Door3/Door4Open
    public Transform DoorLeafA, DoorLeafB;
    public float DoorLeafTravel = 0.683f;
    public Collider DoorTrigger;
    public int AirSupplySlot = -1, WasteSlot = -1;
    /// <summary>A bed in the slide-out (HabInterior): its Entity slot index and the bed prefab's own camera and exit points.</summary>
    [System.Serializable]
    public class Bunk { public int SlotIndex; public Transform Camera, Exit; }
    public List<Bunk> Bunks = new();
    public List<GameObject> InteriorRoots = new();   // furniture roots (hab + slide-out): left out of the centre of mass
    public Transform Beacon, LightSwitch;             // beacon rotor; the switch lever
    // slot props (HabInterior.CopySlots): name, first slot, count - plain lists, which Unity copies onto spawned habs
    public List<Transform> ConsoleNodes = new();
    public Transform StatusScreenAt;                   // the HAB STATUS screen's face (HabStatusScreen draws on it)
    public List<Transform> RoofPanels = new();       // the two flat solar heads on the roof
    public float SolarPanelArea;                      // one panel's area, from the game's SolarPanel.PanelSize
    public float SolarWatts { get; private set; }     // last reading (server: charging; clients: display only)
    private float _solarReadAt;     // consoles and circuit housing: hovering shows the hab status
    public List<string> SlotPropNames = new();
    public List<int> SlotPropFirst = new(), SlotPropCount = new();
    public List<Light> BeaconLights = new();

    public static bool InteractDiagnostics;     // [Fixes] LogInteract
    private Collider _lastCursor;
    private static readonly HashSet<CargoRover> Anchored = new();
    private float _progress = -1f;              // -1 = not initialised: snap to the saved state on the first update
    private readonly float[] _legDrop = new float[4];
    private Interactable _deploy;
    private CargoRover _anchored;
    private Vector3 _slideHome;
    private float _remeasureAt = -1f;
    private int _lastState = -1;

    public const float PumpSeconds = 5f, DoorSeconds = 2f, CabinKPa = 101.325f;
    public enum DoorPhase { Closed, PumpDown, Opening, Open, Closing, Venting, Restoring }
    private static readonly HashSet<CargoHab> Deployed = new();
    // Read from the occlusion worker thread (SetWorldAtmosphere / storm checks): an immutable snapshot, replaced on
    // the main thread whenever the set changes, plus each hab's world-to-local matrix cached every frame.
    private static volatile CargoHab[] _deployedSnapshot = new CargoHab[0];
    public static CargoHab[] DeployedHabs => _deployedSnapshot;
    private Matrix4x4 _worldToLocal = Matrix4x4.identity;
    private volatile bool _slideOutExtended;
    private bool _spillLogged;
    private MeshRenderer _curtain;
    private float _lastPump;
    private int _climateTick;

    private static void SetDeployed(CargoHab hab, bool on)
    {
        if (on ? Deployed.Add(hab) : Deployed.Remove(hab)) _deployedSnapshot = new List<CargoHab>(Deployed).ToArray();
    }
    private Interactable _door;
    private DoorPhase _phase = DoorPhase.Closed;
    private float _phaseStart, _leaf;          // _leaf 0 closed .. 1 open (visual)
    private bool _doorInit;

    protected override bool HasCabin => true;
    protected override bool ClimateOn => true;
    private Interactable DoorInteractable => _door ??= Interactables.Find(i => i.Action == DoorAction);
    private Interactable _locker;
    private Interactable LockerInteractable => _locker ??= Interactables.Find(i => i.Action == LockerAction && i.JoinInProgressSync);
    public bool IsLockerOpen => (LockerInteractable?.State ?? 0) == 1;
    private float _lockerOpen = -1f;                    // eased 0 closed .. 1 open; -1 = not initialised
    private Quaternion _restA, _restB, _restC, _restD;
    public bool IsDoorOpen => (DoorInteractable?.State ?? 0) == 1;
    public DoorPhase Phase => _phase;
    /// <summary>Door leaves shut (not opening, open or closing): the room is sealed from the weather.</summary>
    public bool IsSealed => _phase is DoorPhase.Closed or DoorPhase.PumpDown or DoorPhase.Venting or DoorPhase.Restoring;
    private DynamicGasCanister Supply => AirSupplySlot >= 0 && AirSupplySlot < Slots.Count ? Slots[AirSupplySlot].Get() as DynamicGasCanister : null;
    private DynamicGasCanister Waste => WasteSlot >= 0 && WasteSlot < Slots.Count ? Slots[WasteSlot].Get() as DynamicGasCanister : null;

    public override void InitInternalAtmosphere()
    {
        if (InternalAtmosphere == null) InternalAtmosphere = new Atmosphere(this, new VolumeLitres(CabinVolumeLitres), 0L);
    }
    private Vector3 _leafHomeA, _leafHomeB, _leafOpenA, _leafOpenB;

    /// <summary>Slide a leaf away from the door's centre by its own width, read from the mesh the game uses (the
    /// game ships more than one InteriorDoor mesh; which side a leaf is on is not fixed by its name).</summary>
    private Vector3 LeafOpening(Transform leaf, float fallbackSign)
    {
        var mesh = leaf ? leaf.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (!mesh) return Vector3.right * fallbackSign * DoorLeafTravel;
        var b = mesh.bounds;
        float side = Mathf.Abs(b.center.x) > 0.01f ? Mathf.Sign(b.center.x) : fallbackSign;
        return leaf.localRotation * (Vector3.right * side * Mathf.Min(b.size.x, DoorLeafTravel));   // stay inside the wall
    }

    private void SetPhase(DoorPhase p) { _phase = p; _phaseStart = Time.time; }

    public bool IsDeployed => DeployState == 1;

    private float _stillFor;
    private bool _parkHeld;
    public bool IsParkHeld => _parkHeld;

    private CargoRover _frozenRover;

    /// <summary>The towing rover is held with the hab: held alone, the hab became a fixed post the parked rover still
    /// pushed on (22 to 840 N in 20 s, a slight twist, and a nose kick when released). Only a rover this machine
    /// simulates and that was not already kinematic; released (and woken) with the hab.</summary>
    private void SetRoverFrozen(CargoRover r)
    {
        if (r != null && (!r.HasAuthority || !r.RigidBody)) r = null;
        if ((object)_frozenRover == r) return;
        if ((object)_frozenRover != null && _frozenRover && _frozenRover.RigidBody)
        {
            _frozenRover.RigidBody.isKinematic = false;
            _frozenRover.RigidBody.WakeUp();
        }
        _frozenRover = null;
        if (r != null && !r.RigidBody.isKinematic) { r.RigidBody.isKinematic = true; _frozenRover = r; }
    }

    /// <summary>Hold the parked hitched hab still (see HabRules.ParkHold); every frame, on the authority.</summary>
    private bool UpdateParkHold()
    {
        var r = TowingRover;
        bool driving = r != null && Mathf.Abs(r.TargetMotorPower) > 0.5f;
        if (r != null && !driving)                                      // a remote driver's gas shows up as wheel torque
            foreach (var w in r.Wheels) if (w?.WheelCollider != null && Mathf.Abs(w.WheelCollider.motorTorque) > 0.5f) { driving = true; break; }
        float rs = r != null && r.RigidBody && !r.RigidBody.isKinematic ? r.RigidBody.velocity.magnitude : 0f;
        float hs = _parkHeld || !RigidBody || RigidBody.isKinematic ? 0f : RigidBody.velocity.magnitude;
        _stillFor = r != null && !driving && rs < HabRules.ParkStillSpeed && hs < HabRules.ParkStillSpeed ? _stillFor + Time.deltaTime : 0f;
        bool was = _parkHeld;
        _parkHeld = HabRules.ParkHold(r != null, driving, rs, _stillFor, _parkHeld);
        if (!_parkHeld && was) _stillFor = 0f;
        if (_parkHeld != was && TowDiagnostics) Debug.Log($"[RoverCargo][tow] hab park hold {(_parkHeld ? "on" : $"off (driving={driving}, rover v={rs:0.00}, hitch {HitchForce:0} N)")}");
        return _parkHeld;
    }
    public float Progress => Mathf.Max(0f, _progress);
    protected override bool TeardownHabOut => IsDeployed || Progress > 0f;
    private Interactable DeployInteractable => _deploy ??= Interactables.Find(i => i.Action == DeployAction);
    private int DeployState => DeployInteractable?.State ?? 0;

    /// <summary>True while this rover is hitched to a hab that is deployed (locked): it must not drive.</summary>
    public static bool IsAnchoring(CargoRover rover) => rover != null && Anchored.Contains(rover);

    public override void Awake()
    {
        base.Awake();
        if (!IsCursor && StatusScreenAt && GameManager.GameState != GameState.None)
            gameObject.AddComponent<HabStatusScreen>().Init(this, StatusScreenAt);   // built on each hab, not on the prefab
        FindBunks();
        FindPowerSlots();
        FixCenterOfMass();
        if (SlideOut) _slideHome = SlideOut.localPosition;
        if (DoorLeafA) _leafHomeA = DoorLeafA.localPosition;
        if (DoorLeafB) _leafHomeB = DoorLeafB.localPosition;
        _leafOpenA = LeafOpening(DoorLeafA, 1f);
        _leafOpenB = LeafOpening(DoorLeafB, -1f);
        if (LockerLeafA) _restA = LockerLeafA.localRotation;
        if (LockerLeafB) _restB = LockerLeafB.localRotation;
        if (LockerLeafC) _restC = LockerLeafC.localRotation;
        if (LockerLeafD) _restD = LockerLeafD.localRotation;
    }

    public override void OnDestroy()
    {
        _spray?.Cancel();
        SetRoverFrozen(null);
        if ((object)_anchored != null) Anchored.Remove(_anchored);   // also when the rover is already destroyed
        SetDeployed(this, false);
        base.OnDestroy();
    }

    /// <summary>Vanilla restores interactable states only for a fixed list (Button1-3, OnOff, ...): restore Button4.</summary>
    public override void DeserializeSave(ThingSaveData saveData)
    {
        base.DeserializeSave(saveData);
        if (saveData?.States == null || DeployInteractable == null) return;
        foreach (var st in saveData.States)
        {
            if (st.StateName == DeployAction.ToString()) DeployInteractable.State = st.State;
            if (st.StateName == DoorAction.ToString() && DoorInteractable != null) DoorInteractable.State = st.State;
            if (st.StateName == ShowerAction.ToString() && Interactables.Find(i => i.Action == ShowerAction) is { } sh) sh.State = st.State;
            if (st.StateName == LockerAction.ToString() && LockerInteractable != null) LockerInteractable.State = Mathf.Max(LockerInteractable.State, st.State);
        }
    }

    public override void UpdateEachFrame()
    {
        base.UpdateEachFrame();
        if (ShowerWater && ShowerWater.isPlaying && (GameManager.GameState != GameState.Running || WorldManager.IsGamePaused))
            ShowerWater.Pause();                                                          // paused game: silent
        if (IsCursor || GameManager.GameState != GameState.Running) return;
        int state = DeployState;
        if (_progress < 0f)
        {                                                    // loaded or spawned: snap, no animation
            MeasureLegs();
            _progress = state;
            if (state == 1) _remeasureAt = Time.time + 2f;   // terrain colliders may stream in after the first frame
        }
        else
        {
            if (state == 1 && _lastState == 0 && _progress <= 0f) MeasureLegs();   // every machine, at deploy start
            _progress = Mathf.MoveTowards(_progress, state, Time.deltaTime / CycleSeconds);
        }
        _lastState = state;
        if (_remeasureAt > 0f && Time.time >= _remeasureAt) { MeasureLegs(); _remeasureAt = -1f; }
        ApplyPose(_progress);
        bool deployedLock = _progress > 0f;
        bool parkHold = !deployedLock && HasAuthority && UpdateParkHold();   // parked hitched (HabRules.ParkHold)
        if (!parkHold) { _parkHeld = false; _stillFor = deployedLock ? 0f : _stillFor; }
        SetRoverFrozen(parkHold ? TowingRover : null);
        bool locked = deployedLock || parkHold;
        if (RigidBody && RigidBody.isKinematic != locked)
        {
            RigidBody.isKinematic = locked;
            if (!locked) RigidBody.WakeUp();
        }
        var rover = deployedLock ? TowingRover : null;       // the rover this hab holds, if any (deployed only: parked may drive)
        if (rover != _anchored)
        {
            if ((object)_anchored != null) Anchored.Remove(_anchored);
            if (rover != null) Anchored.Add(rover);
            _anchored = rover;                                // unhitching or stowing releases the old rover
        }
        bool ladderSolid = _progress >= 0.999f;
        if (Ladder)
            foreach (var c in Ladder.GetComponentsInChildren<Collider>(true))
                if (c.enabled != ladderSolid) c.enabled = ladderSolid;
        if (!_doorInit) { _phase = IsDoorOpen ? DoorPhase.Open : DoorPhase.Closed; _leaf = IsDoorOpen ? 1f : 0f; _doorInit = true; }
        bool wantOpen = IsDoorOpen;
        float t = Time.time - _phaseStart;
        switch (_phase)
        {
            case DoorPhase.Closed when wantOpen: SetPhase(DoorPhase.PumpDown); _spillLogged = false; break;
            case DoorPhase.PumpDown when t >= PumpSeconds: SetPhase(DoorPhase.Opening); break;
            case DoorPhase.Opening when t >= DoorSeconds: SetPhase(DoorPhase.Open); break;
            case DoorPhase.Open when !wantOpen: SetPhase(DoorPhase.Closing); break;
            case DoorPhase.Closing when t >= DoorSeconds: SetPhase(DoorPhase.Venting); break;
            case DoorPhase.Venting when t >= PumpSeconds: SetPhase(DoorPhase.Restoring); break;
            case DoorPhase.Restoring when t >= PumpSeconds: SetPhase(DoorPhase.Closed); break;
        }
        float leafTarget = _phase is DoorPhase.Opening or DoorPhase.Open ? 1f : 0f;
        _leaf = Mathf.MoveTowards(_leaf, leafTarget, Time.deltaTime / DoorSeconds);
        float open = Mathf.SmoothStep(0f, 1f, _leaf);
        if (DoorLeafA) DoorLeafA.localPosition = _leafHomeA + _leafOpenA * open;
        if (DoorLeafB) DoorLeafB.localPosition = _leafHomeB + _leafOpenB * open;
        bool beacon = _phase is not (DoorPhase.Closed or DoorPhase.Open) && Powered;
        foreach (var l in BeaconLights) if (l && l.enabled != beacon) l.enabled = beacon;
        if (Beacon && beacon) Beacon.Rotate(0f, 0f, 360f * Time.deltaTime, Space.Self);
        if (LightSwitch)
        {
            LightSwitch.localRotation = Quaternion.Euler(Button2 == 1 ? -14f : 14f, 0f, 0f);   // on: top pressed in
            if (_lastLights >= 0 && Button2 != _lastLights)
                PlayPooledAudioSound(Button2 == 1 ? Defines.Sounds.SwitchOn : Defines.Sounds.SwitchOff, transform.InverseTransformPoint(LightSwitch.position));
            _lastLights = Button2;
        }
        UpdateCurtain();
        UpdateLocker();
        UpdateShowerFx();
        // roof solar, on the main thread (the atmos tick runs on a worker thread, and physics rays must not): every
        // machine works it out once a second; the server's tick charges from it, clients only show it
        if (Time.time >= _solarReadAt) { SolarWatts = ComputeSolarWatts(); _solarReadAt = Time.time + 1f; }
        if (InteractDiagnostics) LogCursor();
        _worldToLocal = transform.worldToLocalMatrix;
        _slideOutExtended = Progress >= 0.999f;
        SetDeployed(this, IsDeployed && Progress >= 0.999f);
    }

    public override void OnAtmosphericTick()
    {
        base.OnAtmosphericTick();
        if (GameManager.GameState != GameState.Running || IsCursor || InternalAtmosphere == null || !GameManager.RunSimulation) return;
        float dt = AtmosphericsManager.Instance ? AtmosphericsManager.Instance.TickSpeedSeconds : 0.5f;
        float step = 1f - Mathf.Pow(0.01f, dt / PumpSeconds);     // removes 99 % over one pump phase
        var cabin = InternalAtmosphere;
        var supply = Supply?.InternalAtmosphere;
        switch (_phase)
        {
            case DoorPhase.PumpDown:
                if (supply != null && HasCharge) MoveInto(cabin, supply, step, new PressurekPa(Supply.MaxSetting));   // portable tank limit, 10,132.5 kPa
                else VentToWorld(cabin, step);
                DrainBattery(40f);
                break;
            case DoorPhase.Opening:
            case DoorPhase.Open:
                var world = GridController.AtmosphericsController.CloneGlobalAtmosphere(DoorGrid, 0L);
                AtmosphereHelper.Mix(cabin, world, AtmosphereHelper.MatterState.All);     // door open: outside air
                break;
            case DoorPhase.Venting:
                VentToWorld(cabin, step);
                if (Time.time - _phaseStart >= PumpSeconds - dt) VentToPressure(cabin, 1f);   // finish at about 1 kPa
                DrainBattery(40f);
                break;
            case DoorPhase.Restoring:
            case DoorPhase.Closed:
                if (supply != null) TopUp(cabin, supply, _phase == DoorPhase.Restoring ? 1f : 0.2f);
                if (cabin.PressureGassesAndLiquids > new PressurekPa(CabinKPa + 4f)) VentToPressure(cabin, CabinKPa);   // relief
                Filter(cabin);
                _lastPump = HeatPump(cabin);
                break;
        }
        SolarStep();
        ChargerStep();
        ShowerStep();
        FillerStep();
        SuitStationStep();
        if (LogClimate && ++_climateTick % 10 == 0)
        {
            var b = Battery;
            Debug.Log($"[RoverCargo][climate] hab {ReferenceId} room {cabin.PressureGassesAndLiquids.ToFloat():0.0} kPa {cabin.Temperature.ToFloat() - 273.15f:0.0} C | door {_phase} | pump {_lastPump:0} J/tick | supply {(Supply ? Supply.InternalAtmosphere.PressureGassesAndLiquids.ToFloat().ToString("0") : "-")} kPa waste {(Waste ? Waste.InternalAtmosphere.PressureGassesAndLiquids.ToFloat().ToString("0") : "-")} kPa | battery {(b ? b.PowerStored.ToString("0") : "none")}");
        }
    }

    /// <summary>What the flat roof panels make now: the game's panel formula (the planet's sunlight, softly capped near
    /// 500 W each) at the real sun angle, Dot(hab up, sun), nothing at night or in an eclipse, one sun ray per panel for
    /// shade, less for hab damage.</summary>
    private float ComputeSolarWatts()
    {
        if (RoofPanels.Count == 0 || IsCursor) return 0f;
        var sun = OrbitalSimulation.WorldSunVector;
        double dot = Vector3.Dot(transform.up, sun);
        float health = 1f - DamageState.TotalRatio;
        double total = 0;
        foreach (var panel in RoofPanels)
        {
            if (!panel) continue;
            bool shaded = false;
            if (dot > 0)
                foreach (var hit in Physics.RaycastAll(panel.position + transform.up * 0.05f, sun, 500f, ~(1 << LayerMask.NameToLayer("Ignore Raycast")), QueryTriggerInteraction.Ignore))
                    if (hit.rigidbody != RigidBody && !(TowingRover && hit.rigidbody == TowingRover.RigidBody)) { shaded = true; break; }
            float h = panel.position.y;
            total += HabRules.SolarPower(OrbitalSimulation.SolarIrradiance, SolarPanelArea, Weather.WeatherManager.GetSolarRatioAt(h),
                         Weather.WeatherManager.CurrentEventAffects(h))
                     * HabRules.SolarShare(dot, OrbitalSimulation.IsEclipse, shaded ? 0 : 1) * health;
        }
        return (float)total;
    }

    /// <summary>Roof solar, deployed or towing (server, atmos tick): the last main-thread reading's watts go into Battery1,
    /// then Battery2; past full they are lost.</summary>
    private void SolarStep()
    {
        float left = SolarWatts;
        foreach (int i in _powerSlots)
        {
            if (left <= 0f) break;
            if (i < Slots.Count && Slots[i].Get() is BatteryCell cell && !cell.IsCharged) left = cell.AddPowerSafe(left);
        }
    }

    public const float ChargerRate = 500f;           // J per atmos tick per cell (the game charger's per-tick figure)
    private int _lastLights = -1;

    /// <summary>The charger's two cells take up to 500 J a tick each from the hab batteries (1:1, as the game's charger)
    /// while the hab is deployed. The cells are not power slots, so the hab never draws on them.</summary>
    private void ChargerStep()
    {
        if (!IsDeployed || Progress < 0.999f) return;
        var (cf, cn) = SlotsOf("Charger");
        for (int i = cf; i >= 0 && i < cf + cn && i < Slots.Count; i++)
        {
            if (!(Slots[i].Get() is BatteryCell cell) || cell.IsCharged) continue;
            if (!ChargeToSpare) break;
            var src = Battery;
            if (src == null || src.IsEmpty) break;
            float j = HabRules.ChargeStep(cell.PowerDelta, src.PowerStored, ChargerRate);
            src.PowerStored -= j;
            cell.PowerStored += j;
        }
    }


    /// <summary>The game's suit storage, fed by the hab instead of pipes and cables (as vanilla, every atmos tick):
    /// the suit's air tank equalises with AIR SUPPLY, its waste tank and the helmet's air go to WASTE (not past 95 %),
    /// and suit, helmet and back batteries share up to 1000 J a tick from the hab batteries. Deployed and charged only.</summary>
    private void SuitStationStep()
    {
        var (sf, sn) = SlotsOf("SuitStorage");
        if (sn != 3 || sf + 2 >= Slots.Count) return;
        var suit = Slots[sf + 1].Get<ISuit>();
        var helmet = Slots[sf].Get<GasMask>();
        var hb = Battery;
        bool on = IsDeployed && Progress >= 0.999f && hb != null && !hb.IsEmpty;
        var waste = Waste;
        if (!on) return;
        bool dump = HabRules.StationGate(true, true,
            waste ? waste.InternalAtmosphere.PressureGassesAndLiquids.ToFloat() : float.MaxValue, waste ? waste.MaxSetting : 0f);
        if (suit?.AsThing && !suit.AsThing.IsEmergency)
        {
            if (suit.AirTank && Supply)
                AtmosphereHelper.Mix(Supply.InternalAtmosphere, suit.AirTank.InternalAtmosphere, AtmosphereHelper.MatterState.Gas);
            if (dump && suit.InternalAtmosphere != null)
            {
                waste.InternalAtmosphere.Add(suit.InternalAtmosphere.GasMixture);
                suit.InternalAtmosphere.GasMixture.Reset();
            }
            if (dump && suit.WasteTank)
            {
                waste.InternalAtmosphere.Add(suit.WasteTank.InternalAtmosphere.GasMixture);
                suit.WasteTank.InternalAtmosphere.GasMixture.Reset();
            }
        }
        if (dump && helmet && !helmet.IsEmergency)
        {
            waste.InternalAtmosphere.Add(helmet.InternalAtmosphere.GasMixture);
            helmet.InternalAtmosphere.GasMixture.Reset();
        }
        var cells = new[] { Slots[sf + 1].Get<IBatteryPowered>()?.Battery, Slots[sf].Get<IBatteryPowered>()?.Battery, Slots[sf + 2].Get<IBatteryPowered>()?.Battery };
        var want = new float[3];
        for (int k = 0; k < 3; k++) want[k] = cells[k] && !cells[k].IsCharged ? cells[k].PowerDelta : 0f;
        var got = ChargeToSpare ? HabRules.SplitBudget(Mathf.Min(1000f, hb.PowerStored), want) : new float[3];
        for (int k = 0; k < 3; k++)
            if (cells[k] && got[k] > 0f) { cells[k].PowerStored += got[k]; hb.PowerStored -= got[k]; }
    }

    // ---- water: the game's shower, toilet and bottle filler, fed by the rack's canisters instead of pipes ----
    private GasCanister RackCanister(int k)
    {
        var (first, count) = SlotsOf("WaterRack");
        return first >= 0 && k < count && first + k < Slots.Count ? Slots[first + k].Get<GasCanister>() : null;
    }
    private static string LitresIn(GasCanister c) =>
        c && c.InternalAtmosphere != null ? c.InternalAtmosphere.TotalVolumeLiquids.ToFloat().ToString("0.0") + " L" : "none";
    public GasCanister CleanWater => RackCanister(0);
    public GasCanister WasteWater => RackCanister(1);
    private Interactable _shower;
    private Interactable ShowerInteractable => _shower ??= Interactables.Find(i => i.Action == ShowerAction);
    public bool IsShowerOn => (ShowerInteractable?.State ?? 0) == 1;
    private bool _canisterLogged;

    /// <summary>Why the water fixtures cannot run now (the game's WaterDevice rules, HabRules.WaterRefusal), or null.</summary>
    private string WaterRefusalFor(double needMoles, bool usesWaste = true)
    {
        var clean = CleanWater?.InternalAtmosphere;
        var waste = WasteWater?.InternalAtmosphere;
        bool polluted = clean != null && HabRules.Polluted(clean.GasMixture.TotalToxins.ToDouble(),
            clean.GasMixture.GetTotalMolesLiquids.ToDouble(), clean.GasMixture.Water.Quantity.ToDouble());
        return HabRules.WaterRefusal(IsDeployed && Progress >= 0.999f && IsSealed,
            clean != null, clean?.TotalMolesLiquids.ToDouble() ?? 0, needMoles, clean?.Temperature.ToDouble() ?? 0,
            polluted,
            !usesWaste || waste != null, usesWaste ? waste?.TotalVolumeLiquids.ToDouble() ?? 0 : 0, usesWaste ? waste?.Volume.ToDouble() ?? 0 : 1,
            InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0);
    }

    /// <summary>Standing at the toilet (the game wants its grid cell): within about 0.6 m of it, on the floor.</summary>
    private bool AtToilet(Human h)
    {
        if (!h || !ToiletNode) return false;
        var p = ToiletNode.InverseTransformPoint(h.Position);
        return Mathf.Abs(p.x) <= 0.6f && Mathf.Abs(p.z) <= 0.8f && p.y >= -0.4f && p.y <= 1.2f;
    }

    /// <summary>Hab charge above the 20 % reserve that charging others (charger cells, the suit) must leave.</summary>
    private bool ChargeToSpare { get { var (st, mx) = BatteryTotals(); return HabRules.AboveReserve(st, mx); } }

    /// <summary>Standing in the shower (its tray, in the copied node's frame).</summary>
    private bool InShower(Human h)
    {
        if (!h || !ShowerNode) return false;
        var p = ShowerNode.InverseTransformPoint(h.Position);
        return Mathf.Abs(p.x) <= 0.25f && p.y >= -0.2f && p.y <= 1.0f && p.z >= -0.05f && p.z <= 0.75f;
    }

    private System.Threading.CancellationTokenSource _spray;
    private float _valve = -1f;                          // eased 0 off .. 1 on; -1 = not initialised
    private Quaternion _valveRest;

    /// <summary>What everyone sees and hears of the shower, from its synced on state (as the game's shower): the valve
    /// lever turns, the game's valve clicks, its spray particles every 0.1 s at the shower head, its running water.</summary>
    private void UpdateShowerFx()
    {
        if (!ShowerNode) return;
        bool on = IsShowerOn && IsDeployed;
        if (ShowerValve)
        {
            if (_valve < 0f) { _valveRest = ShowerValve.localRotation; _valve = on ? 1f : 0f; }
            else if ((_valve == 0f && on) || (_valve == 1f && !on))
                PlayPooledAudioSound(on ? Defines.Sounds.PipeValveOnHash : Defines.Sounds.PipeValveOffHash, transform.InverseTransformPoint(ShowerValve.position));
            _valve = Mathf.MoveTowards(_valve, on ? 1f : 0f, Time.deltaTime / 0.3f);
            ShowerValve.localRotation = _valveRest * Quaternion.Euler(0f, 90f * Mathf.SmoothStep(0f, 1f, _valve), 0f);
        }
        if (on && _spray == null && AtmosphericsManager.Instance && AtmosphericsManager.Instance.ShowerParticleSystem)
        {
            _spray = new System.Threading.CancellationTokenSource();
            AtmosphericsManager.Instance.ShowerParticleSystem.EmitShowerParticles(ShowerNode.TransformPoint(new Vector3(0f, 1.47f, 0.379f)), 1, _spray.Token).Forget();
        }
        else if (!on && _spray != null) { _spray.Cancel(); _spray = null; }
        if (ShowerWater && !ShowerWater.outputAudioMixerGroup)
        {   // through the game's own mixer, like the hab's other sounds: the volume sliders and muffling apply
            foreach (var a in GetComponentsInChildren<AudioSource>(true))
                if (a != ShowerWater && a.outputAudioMixerGroup) { ShowerWater.outputAudioMixerGroup = a.outputAudioMixerGroup; break; }
        }
        bool audible = on && !WorldManager.IsGamePaused;
        if (ShowerWater && ShowerWater.isPlaying != audible) { if (audible) ShowerWater.Play(); else ShowerWater.Stop(); }
    }

    private DelayedActionInstance ShowerInteract(Interactable it, Interaction interaction, bool doAction)
    {
        bool on = !IsShowerOn;
        var r = new DelayedActionInstance { Duration = 0f, ActionMessage = on ? "Shower on" : "Shower off" };
        if (interaction.SourceThing is Human who)                 // as the game's shower: say whether it will wash you
            r.ExtendedMessage = HabRules.ShowerCleanMessage(InShower(who), !who.SuitSlot.IsEmpty(), !who.UniformSlot.IsEmpty(),
                who.SpeciesClass == CharacterCustomisation.SpeciesClass.Robot) + System.Environment.NewLine;
        if (on && WaterRefusalFor(StructureShowerMoles) is { } no) return r.Fail(no);
        if (on && !HasCharge) return r.Fail("Hab battery flat");
        if (!doAction) return r.Succeed();
        if (GameManager.RunSimulation) OnServer.Interact(it, on ? 1 : 0);
        return r.Succeed();
    }

    private const double StructureShowerMoles = 5.0, ToiletMoles = 50.0;

    /// <summary>As StructureShower: 5 mol a tick from CLEAN WATER to WASTE WATER while on; anyone standing in it without
    /// suit and uniform gets cleaner, and then the water leaves as PollutedWater. Stops itself when it cannot run.</summary>
    private void ShowerStep()
    {
        if (!IsShowerOn || !ShowerNode) return;
        if (WaterRefusalFor(StructureShowerMoles) != null || !HasCharge) { OnServer.Interact(ShowerInteractable, 0); return; }
        var used = CleanWater.InternalAtmosphere.Remove(new MoleQuantity(StructureShowerMoles), AtmosphereHelper.MatterState.Liquid);
        bool cleaned = false;
        foreach (var h in Human.AllHumans)
        {
            if (!InShower(h)) continue;
            if (!h.SuitSlot.IsEmpty() || (!h.UniformSlot.IsEmpty() && h.SpeciesClass != CharacterCustomisation.SpeciesClass.Robot)) continue;
            h.Hygiene = HabRules.ShowerHygiene(h.Hygiene);
            cleaned = true;
        }
        if (cleaned)
        {
            var water = used.Remove(Chemistry.GasType.Water, MoleQuantity.MaxValue);
            used.Add(new Mole(Chemistry.GasType.PollutedWater, water.Quantity, water.Energy));
        }
        WasteWater.InternalAtmosphere.Add(used);
        DrainBattery(5f);
    }

    /// <summary>As StructureToilet: "Use" when the need is past 25 % and no suit is on; 50 mol of CLEAN WATER plus the
    /// stomach's PollutedWater go to WASTE WATER as PollutedWater.</summary>
    private DelayedActionInstance ToiletInteract(Interaction interaction, bool doAction)
    {
        var r = new DelayedActionInstance { Duration = 1f, ActionMessage = "Use toilet", ActionSoundHash = Item.WaterBottleFillHash };
        if (!(interaction.SourceThing is Human human)) return r.Fail();
        if (HabRules.ToiletRefusal(human.SanitationRatio, AtToilet(human), !human.SuitSlot.IsEmpty()) is { } why) return r.Fail(why);
        if (WaterRefusalFor(ToiletMoles) is { } no) return r.Fail(no);
        var waste = WasteWater.InternalAtmosphere;
        double stomachMoles = human.OrganStomach?.InternalAtmosphere?.GasMixture.PollutedWater.Quantity.ToDouble() ?? 0;
        if (!HabRules.WasteRoomFor(waste.Volume.ToDouble() - waste.TotalVolumeLiquids.ToDouble(), ToiletMoles + stomachMoles))
            return r.Fail("WASTE WATER full");                  // a flush must fit: an overfilled canister bursts
        if (!doAction) return r.Succeed();
        if (!GameManager.RunSimulation) return r.Succeed();
        var mix = CleanWater.InternalAtmosphere.Remove(new MoleQuantity(ToiletMoles), AtmosphereHelper.MatterState.Liquid);
        var water = mix.Remove(Chemistry.GasType.Water, MoleQuantity.MaxValue);
        var quantity = water.Quantity;
        var energy = water.Energy;
        var stomach = human.OrganStomach?.InternalAtmosphere;
        if (stomach != null)
        {
            var dirty = stomach.GasMixture.PollutedWater;
            if (dirty.Quantity > MoleQuantity.Zero)
            {
                quantity += dirty.Quantity;
                energy += dirty.Energy;
                AtmosphericEventInstance.CreateRemove(stomach, new GasMixture(new Mole(Chemistry.GasType.PollutedWater, dirty.Quantity, dirty.Energy)));
            }
        }
        mix.Add(new Mole(Chemistry.GasType.PollutedWater, quantity, energy));
        WasteWater.InternalAtmosphere.Add(mix);
        if (ToiletNode) AudioEvent.Create(ToiletNode.position, Defines.Sounds.ToiletFlush);    // every player hears it
        return r.Succeed();
    }

    /// <summary>As the powered WaterBottleFiller: bottles in its two slots top up from CLEAN WATER (5.56 mol a tick at most).</summary>
    private void FillerStep()
    {
        var (ff, fn) = SlotsOf("Filler");
        if (ff < 0 || !HasCharge) return;
        for (int i = ff; i < ff + fn && i < Slots.Count; i++)
        {
            if (!(Slots[i].Get() is WaterBottle bottle) || WaterRefusalFor(0.01, usesWaste: false) != null) continue;
            var clean = CleanWater.InternalAtmosphere;
            double have = clean.GasMixture.Water.Quantity.ToDouble();
            double moles = HabRules.FillerMoles(bottle.MaxQuantity - bottle.Quantity, have);
            if (moles <= 0) continue;
            var m = new MoleQuantity(moles);
            var energy = clean.GasMixture.Water.Energy * (m / clean.GasMixture.Water.Quantity).ToDouble();
            var got = clean.Remove(new GasMixture(new Mole(Chemistry.GasType.Water, m, energy)), AtmosphereHelper.MatterState.Liquid);
            bottle.AddLiquidToThing(got.GetTotalMolesLiquids.ToFloat() / 55.555557f);
            DrainBattery(5f);
        }
    }

    private void MoveInto(Atmosphere from, Atmosphere to, float fraction, PressurekPa limit)
    {
        var n = from.TotalMoles * fraction;
        if (n <= MoleQuantity.Zero) return;
        var mix = from.Remove(n, AtmosphereHelper.MatterState.All);
        to.Add(mix);
        if (to.PressureGassesAndLiquids > limit * 0.95)
        {   // supply nearly full: the surplus goes outside
            var over = to.TotalMoles * (1.0 - (limit * 0.95 / to.PressureGassesAndLiquids).ToDouble());
            var spill = to.Remove(over, AtmosphereHelper.MatterState.All);
            GridController.AtmosphericsController.CloneGlobalAtmosphere(DoorGrid, 0L).Add(spill);
            if (!_spillLogged) Debug.Log($"[RoverCargo] hab {ReferenceId}: AIR SUPPLY nearly full - surplus cabin air vented outside");
            _spillLogged = true;
        }
    }

    /// <summary>Locker doors fold open over 0.6 s (the game's own bifold leaves). Like the game's locker, its shelves
    /// (slot boxes and items) show from the moment it starts to open until it has shut.</summary>
    private void UpdateLocker()
    {
        if (!LockerLeafA || LockerInteractable == null) return;
        float target = IsLockerOpen ? 1f : 0f;
        if (_lockerOpen < 0f) { _lockerOpen = target; SetLockerContents(target > 0f); }      // loaded: snap, no sound
        else if (_lockerOpen != target)
        {
            if (_lockerOpen == 0f || _lockerOpen == 1f)                                     // starts moving
            {
                PlayPooledAudioSound(target > 0f ? Defines.Sounds.StorageLockerOpenHash : Defines.Sounds.StorageLockerCloseHash,
                    transform.InverseTransformPoint(LockerLeafA.parent.position));
                if (target > 0f) SetLockerContents(true);
            }
            _lockerOpen = Mathf.MoveTowards(_lockerOpen, target, Time.deltaTime / 0.6f);
            if (_lockerOpen == 0f) SetLockerContents(false);
        }
        var (a, b) = HabRules.LockerLeafAngles(Mathf.SmoothStep(0f, 1f, _lockerOpen));
        LockerLeafA.localRotation = _restA * Quaternion.Euler(0f, a, 0f);
        if (LockerLeafB) LockerLeafB.localRotation = _restB * Quaternion.Euler(0f, b, 0f);
        if (LockerLeafC) LockerLeafC.localRotation = _restC * Quaternion.Euler(0f, -a, 0f);
        if (LockerLeafD) LockerLeafD.localRotation = _restD * Quaternion.Euler(0f, -b, 0f);
    }

    private void SetLockerContents(bool visible)
    {
        var (first, count) = SlotsOf("Locker");
        for (int i = first; i >= 0 && i < first + count && i < Slots.Count; i++)
        {
            if (Slots[i].Collider) Slots[i].Collider.enabled = visible;
            if (Slots[i].Get() is DynamicThing item) item.SetVisibility(visible);
        }
    }

    private bool IsLockerSlot(Slot slot)
    {
        var (first, count) = SlotsOf("Locker");
        int i = slot != null ? Slots.IndexOf(slot) : -1;
        return first >= 0 && i >= first && i < first + count;
    }

    /// <summary>Items on the locker shelves sit as the game's locker holds them: shrunk to the shelf, tilted (45, 90, 90).</summary>
    public override void SetSlotOccupantTransformData(DynamicThing newChild)
    {
        if (!IsLockerSlot(newChild.ParentSlot)) { base.SetSlotOccupantTransformData(newChild); return; }
        newChild.ScaleToSlot();
        newChild.ThingTransformLocalRotation = Quaternion.Euler(new Vector3(45f, 90f, 90f) + newChild.ChildSlotOffset);
        newChild.ThingTransformLocalPosition = newChild.ChildSlotOffsetPosition;
        if (_lockerOpen == 0f) newChild.SetVisibility(false);
    }

    public override void OnChildEnterInventory(DynamicThing newChild)
    {
        base.OnChildEnterInventory(newChild);
        if (!_canisterLogged && newChild is GasCanister c && c.ParentSlot != null && c.ParentSlot.Type == Slot.Class.LiquidCanister && c.InternalAtmosphere != null)
        {
            _canisterLogged = true;
            Debug.Log($"[RoverCargo] hab water canister: {c.InternalAtmosphere.Volume.ToFloat():0.#} L ({c.PrefabName})");
        }
    }

    public override void OnChildExitInventory(DynamicThing previousChild)
    {
        base.OnChildExitInventory(previousChild);
        if (previousChild && !(previousChild is Entity)) previousChild.SetVisibility(true);   // off a closed shelf: visible again
    }

    /// <summary>Storm curtain over the door (seen through the door's window from inside), shown while vanilla shows its
    /// storm cards and the room is sealed. Vanilla drops a room's storm cards when a door breaks its seal, and so does
    /// the hab: from the moment the leaves start to open until they have shut again the doorway shows the real outside,
    /// and HabWeatherFx treats the room as outside (future windows follow the same rule).</summary>
    private void UpdateCurtain()
    {
        var mat = HabWeatherFx.CurtainMaterial;
        bool show = mat && IsDeployed && IsSealed && DoorLeafA;
        if (show && !_curtain)
        {
            var go = new GameObject("StormCurtain") { layer = gameObject.layer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = HabWeatherFx.CurtainQuad(new Vector3(0f, 2.3f, -3.32f));
            _curtain = go.AddComponent<MeshRenderer>();
            _curtain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _curtain.receiveShadows = false;
        }
        if (!_curtain) return;
        if (show && _curtain.sharedMaterial != mat) _curtain.sharedMaterial = mat;
        if (_curtain.enabled != show) _curtain.enabled = show;
    }

    /// <summary>Vent the cabin outside down to a target pressure.</summary>
    private void VentToPressure(Atmosphere cabin, float kPa)
    {
        var p = cabin.PressureGassesAndLiquids.ToFloat();
        if (p > kPa) VentToWorld(cabin, 1f - kPa / p);
    }

    private void VentToWorld(Atmosphere from, float fraction)
    {
        var n = from.TotalMoles * fraction;
        if (n <= MoleQuantity.Zero) return;
        GridController.AtmosphericsController.CloneGlobalAtmosphere(DoorGrid, 0L).Add(from.Remove(n, AtmosphereHelper.MatterState.All));
    }

    /// <summary>Bring the cabin to 101.3 kPa from the supply tank; rate 1 = all of the shortfall this tick.</summary>
    private void TopUp(Atmosphere cabin, Atmosphere supply, float rate)
    {
        var shortfall = new PressurekPa(CabinKPa) - cabin.PressureGassesAndLiquids;
        if (shortfall <= PressurekPa.Zero || supply.TotalMoles <= MoleQuantity.Zero) return;
        var need = IdealGas.Quantity(shortfall * rate, cabin.Volume, supply.Temperature);
        var take = need < supply.TotalMoles ? need : supply.TotalMoles;
        cabin.Add(supply.Remove(take, AtmosphereHelper.MatterState.Gas));
    }

    private void Filter(Atmosphere cabin)
    {
        var waste = Waste;
        if (waste == null || waste.InternalAtmosphere.PressureGassesAndLiquids.ToFloat() >= waste.MaxSetting * 0.95f) return;   // WASTE full
        foreach (var s in Slots)
            if (s.Type == Slot.Class.GasFilter && s.Get() is GasFilter f)
                f.FilterGas(ref cabin.GasMixture, ref waste.InternalAtmosphere.GasMixture);
    }

    private void DrainBattery(float units)
    {
        var b = Battery;
        if (b != null && !b.IsEmpty) b.PowerStored = Mathf.Max(0f, b.PowerStored - units);
    }

    /// <summary>Inside the walls: room (x -1..1, h 1.3..3.6, z -3.05..3.0) or the extended slide-out.</summary>
    public bool ContainsPoint(Vector3 world)
    {
        var p = _worldToLocal.MultiplyPoint3x4(world);        // cached on the main thread: safe off-thread
        if (p.y < 1.3f || p.y > 3.6f) return false;
        if (p.x >= -1.0f && p.x <= 1.0f && p.z >= -3.05f && p.z <= 3.0f) return true;
        return _slideOutExtended && p.x >= -1.85f && p.x < -1.0f && p.z >= -0.2f && p.z <= 2.2f && p.y >= 1.7f;
    }

    /// <summary>0..0.3 legs, 0.3..0.8 slide-out, 0.8..1 ladder (stow runs the same curve backwards).</summary>
    private void ApplyPose(float p)
    {
        float legs = Mathf.Clamp01(p / 0.3f), slide = Mathf.Clamp01((p - 0.3f) / 0.5f), ladder = Mathf.Clamp01((p - 0.8f) / 0.2f);
        for (int i = 0; i < Legs.Count && i < _legDrop.Length; i++)
        {
            var lp = Legs[i].localPosition;
            lp.y = LegStowH - _legDrop[i] * legs;
            Legs[i].localPosition = lp;
        }
        if (SlideOut) SlideOut.localPosition = _slideHome + SlideTravel * Mathf.SmoothStep(0f, 1f, slide);
        if (Ladder) Ladder.localEulerAngles = LadderStowEuler * (1f - Mathf.SmoothStep(0f, 1f, ladder));
    }

    /// <summary>How far each leg must drop from its stowed height to touch the ground (clamped to the stroke).
    /// Rays start inside the hull (local y 1.0) so a rock higher than the stowed foot is still found.</summary>
    private bool MeasureLegs()
    {
        const float rayTop = 1.0f;
        bool reach = true;
        int mask = ~(1 << LayerMask.NameToLayer("Ignore Raycast"));
        for (int i = 0; i < Legs.Count && i < _legDrop.Length; i++)
        {
            var home = Legs[i].localPosition;
            home.y = rayTop;
            Vector3 origin = transform.TransformPoint(home);
            float above = rayTop - LegStowH;                 // ray origin to the stowed foot
            float best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, above + LegMaxStroke + 0.1f, mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.rigidbody == RigidBody || (TowingRover && hit.rigidbody == TowingRover.RigidBody)) continue;
                if (hit.collider.GetComponentInParent<Human>()) continue;
                best = Mathf.Min(best, hit.distance);
            }
            bool found = best < float.MaxValue;
            float drop = found ? best - above : LegMaxStroke;
            if (!found || drop >= LegMaxStroke) reach = false;
            _legDrop[i] = Mathf.Clamp(drop, 0f, LegMaxStroke);
        }
        return reach;
    }

    private string DeployRefusal()
    {
        if (RigidBody && RigidBody.velocity.magnitude > 0.1f) return "Stop the trailer before deploying";
        if (Vector3.Angle(transform.up, Vector3.up) > 15f) return "Too steep to deploy (over 15 deg)";
        if (!MeasureLegs())
        {
            Debug.Log($"[RoverCargo] hab {ReferenceId} deploy refused: leg drops {string.Join(", ", System.Array.ConvertAll(_legDrop, d => d.ToString("0.00")))} (stroke {LegMaxStroke:0.00}, stowed foot {LegStowH:0.00})");
            return "Ground too far below the legs";
        }
        if (SlideOut && SlideOutBlocked()) return "Something is in the way of the slide-out";
        return null;
    }

    private bool SlideOutBlocked()
    {
        // the deployed slide-out volume as an oriented box in hab-local space (world AABBs grow at an angle)
        var b = new Bounds();
        bool first = true;
        foreach (var c in SlideOut.GetComponents<BoxCollider>())
        {
            var lb = new Bounds(_slideHome + c.transform.localPosition + c.center + SlideTravel, c.size);
            if (first) { b = lb; first = false; } else b.Encapsulate(lb);
        }
        if (first) return false;
        foreach (var hit in Physics.OverlapBox(transform.TransformPoint(b.center), b.extents * 0.95f, transform.rotation, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.attachedRigidbody == RigidBody || (TowingRover && hit.attachedRigidbody == TowingRover.RigidBody)) continue;
            if (hit.GetComponentInParent<Human>()) continue;          // a player just steps aside
            return true;
        }
        return false;
    }

    private string StowRefusal()
    {
        if (AnyBunkOccupied) return "Everyone out of the bunks before stowing";
        if (IsLockerOpen) return "Close the locker before stowing";
        if (IsDoorOpen || _phase != DoorPhase.Closed) return "Close the door before stowing";
        // the room plus the deployed slide-out: x -1.9..1.0, h 1.45..3.45, z -3.0..3.0
        Vector3 centre = transform.TransformPoint(new Vector3(-0.45f, 2.45f, 0f));
        var half = new Vector3(1.45f, 1.0f, 3.0f);
        foreach (var hit in Physics.OverlapBox(centre, half, transform.rotation, ~0, QueryTriggerInteraction.Ignore))
            if (hit.GetComponentInParent<Human>()) return "Everyone out before stowing";
        return null;
    }

    /// <summary>The server rebuilds a completed attack without its collider (TargetCollider null): fall back to the
    /// cursor hit point, which is carried over, as vanilla doors do.</summary>
    private bool AtPanel(Attack attack) => DeployPanel && (attack.TargetCollider == DeployPanel ||
        (attack.TargetCollider == null && (DeployPanel.ClosestPoint(attack.Position) - attack.Position).sqrMagnitude < 0.05f * 0.05f));

    public override DelayedActionInstance AttackWith(Attack attack, bool doAction = true)
    {
        if (UpgradeAttack(attack, doAction) is { } up) return up;   // wrench + material, or a wrench on an upgrade part
        if (!(attack.SourceItem is Wrench)) return base.AttackWith(attack, doAction);
        if (!AtPanel(attack))
        {
            if (IsDeployed || Progress > 0f)    // hitching would move the locked hab onto the rover's hitch
                return new DelayedActionInstance { Duration = 1f, ActionMessage = IsHitched ? "Unhitch trailer" : "Hitch trailer" }
                    .Fail("Stow the hab before hitching or unhitching");
            return base.AttackWith(attack, doAction);
        }
        bool deploying = !IsDeployed;
        var result = new DelayedActionInstance { Duration = 1f, ActionMessage = deploying ? "Deploy hab" : "Stow hab" };
        if (_progress > 0f && _progress < 1f) return result.Fail("Hab is moving");
        string refusal = deploying ? DeployRefusal() : StowRefusal();
        if (refusal != null) return result.Fail(refusal);
        if (!doAction) return result.Succeed();
        if (GameManager.RunSimulation && DeployInteractable != null) OnServer.Interact(DeployInteractable, deploying ? 1 : 0);
        return result.Succeed();
    }

    public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
    {
        if (InteractDiagnostics && doAction) Debug.Log($"[RoverCargo][interact] InteractWith {interactable?.Action} by {interaction.SourceThing?.name}");
        if (interactable.Action == ShowerAction) return ShowerInteract(interactable, interaction, doAction);
        if (interactable.Action == ToiletAction) return ToiletInteract(interaction, doAction);
        int si = Slots.FindIndex(s => s.Action == interactable.Action);        // by action: a completed action may lack its collider
        if (si >= 0 && Bunks.Exists(b => b.SlotIndex == si))
        {
            var r = new DelayedActionInstance { Duration = 1f, ActionMessage = "Get in" };
            if (!IsDeployed || Progress < 0.999f) return r.Fail("Deploy the hab first");
            if (Slots[si].Occupant) return r.Fail("Bunk taken");
            if (!(interaction.SourceThing is Human)) return r.Fail("Only people use the bunks");
            if (!doAction) return r.Succeed();
            if (GameManager.RunSimulation) OnServer.MoveToSlot(interaction.SourceThing.AsDynamicThing, Slots[si]);
            return r.Succeed();
        }
        if (interactable.Action == LockerAction)
        {
            bool opening = !IsLockerOpen;
            var r = new DelayedActionInstance { Duration = 0f, ActionMessage = opening ? "Open locker" : "Close locker" };
            if (!IsDeployed || Progress < 0.999f) return r.Fail("Deploy the hab first");
            if (!doAction) return r.Succeed();
            if (GameManager.RunSimulation && LockerInteractable != null) OnServer.Interact(LockerInteractable, opening ? 1 : 0);
            return r.Succeed();
        }
        if (interactable.Action == DoorAction)
        {
            bool opening = !IsDoorOpen;
            var r = new DelayedActionInstance { Duration = 0.3f, ActionMessage = opening ? "Open door" : "Close door" };
            if (DoorRefusal() is { } no) return r.Fail(no);
            if (opening && !HasCharge) r.ActionMessage = "Open door (battery flat: room vents outside)";
            if (opening && interaction.SourceThing && ContainsPoint(interaction.SourceThing.Position))
            {   // from inside: hold, and say what happens
                r.Duration = 1.5f;
                r.ActionMessage = "Open door (room goes to outside air)";
            }
            if (!doAction) return r.Succeed();
            if (GameManager.RunSimulation) OnServer.Interact(DoorInteractable, opening ? 1 : 0);
            return r.Succeed();
        }
        if (interactable.Action != DeployAction) return base.InteractWith(interactable, interaction, doAction);
        return new DelayedActionInstance { Duration = 0f, ActionMessage = IsDeployed ? "Stow hab" : "Deploy hab" }
            .Fail("Use a wrench on the panel");               // a hand click must not flip the saved state
    }

    /// <summary>Vanilla Bed: whoever lies in a bunk is life-suspended (no hunger/thirst, HUD hidden). Only bunk
    /// occupants have the hab as their root parent.</summary>
    public bool IsSuspendingLife => true;

    public bool AnyBunkOccupied => Bunks.Exists(b => b.SlotIndex < Slots.Count && Slots[b.SlotIndex].Occupant);

    /// <summary>The bunks, read back from this instance's own slots. Unity does not copy the mod's own serializable
    /// class (Bunk) when the game spawns a hab from the prefab, so the list HabInterior filled on the prefab arrives
    /// empty and a click on a bunk fell through to the plain slot handler. A bunk is a bed slot; its camera and exit
    /// marks are siblings of the slot's lying position.</summary>
    private void FindBunks()
    {
        int copied = Bunks.Count;
        Bunks.Clear();
        for (int i = 0; i < Slots.Count; i++)
        {
            var at = Slots[i].Location;
            if (Slots[i].StringKey != HabInterior.BunkSlotKey || !at || !at.parent) continue;
            Bunks.Add(new Bunk { SlotIndex = i, Camera = at.parent.Find("CameraPoint"), Exit = at.parent.Find("ExistPosition") });
        }
        if (!IsCursor) Debug.Log($"[RoverCargo] hab bunks: {Bunks.Count} (slots {string.Join(", ", Bunks.ConvertAll(b => b.SlotIndex.ToString()).ToArray())}), {copied} copied from the prefab");
    }

    /// <summary>A slot prop's slots (HabInterior.CopySlots): first index and count, (-1, 0) if the prop is absent.</summary>
    public (int first, int count) SlotsOf(string prop)
    {
        int i = SlotPropNames.IndexOf(prop);
        return i < 0 || i >= SlotPropFirst.Count || i >= SlotPropCount.Count ? (-1, 0) : (SlotPropFirst[i], SlotPropCount[i]);
    }

    private static readonly AccessTools.FieldRef<Rover, List<Slot>> BatterySlots = AccessTools.FieldRefAccess<Rover, List<Slot>>("_batterySlots");
    private readonly HashSet<int> _powerSlots = new();

    /// <summary>Only the hab's own Battery1/Battery2 power it. Vanilla Rover takes every battery-class slot as a power
    /// source (its power tick drains the last one first), which would drain the cells charging in the charger.</summary>
    private void FindPowerSlots()
    {
        _powerSlots.Clear();
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Type == Slot.Class.Battery && Slots[i].StringKey is "Battery1" or "Battery2") _powerSlots.Add(i);
        BatterySlots(this)?.RemoveAll(s => !_powerSlots.Contains(Slots.IndexOf(s)));
    }

    protected override bool IsPowerSlot(int index) => _powerSlots.Contains(index);

    private Bunk BunkOf(Entity e)
    {
        foreach (var b in Bunks)
            if (b.SlotIndex < Slots.Count && (object)Slots[b.SlotIndex].Occupant == e) return b;
        return null;
    }

    // IExitable is re-implemented: Rover's GetExitPosition/GetCameraPoint are not virtual
    public new Vector3 GetExitPosition(Entity entity) => BunkOf(entity) is { Exit: { } x } ? BunkExit(x) : base.GetExitPosition(entity);
    public new Transform GetCameraPoint(Entity entity) => BunkOf(entity) is { Camera: { } c } ? c : base.GetCameraPoint(entity);

    public override void Exit(Human human)
    {
        if (BunkOf(human) is not { Exit: { } x }) { base.Exit(human); return; }
        human.MoveToWorld(FreeBunkExit(human, x), Rotation, Vector3.zero, Vector3.zero);
    }

    /// <summary>As vanilla Seat.Exit: the exit point, or, if something is standing there, back along the player's
    /// facing, then to either side of the hab (each at standing height).</summary>
    private Vector3 FreeBunkExit(Human human, Transform exit)
    {
        const float r = 0.05f;
        float z = exit.localPosition.z;
        var f = human.EntityForward;
        var back = exit.position - f * z;
        var cands = new[] { BunkExit(exit), BunkExit(back - f * z), BunkExit(back + ThingTransform.right * z), BunkExit(back - ThingTransform.right * z) };
        var blocked = System.Array.ConvertAll(cands, c => Physics.CheckSphere(c, r));
        var inside = System.Array.ConvertAll(cands, ContainsPoint);        // never out through a wall
        int i = HabRules.FirstFreeExit(blocked, inside);
        return cands[i < 0 ? 0 : i];
    }

    /// <summary>The bed's exit point, but at standing height on the room floor (the top bunk's would drop you 0.8 m).</summary>
    private Vector3 BunkExit(Transform exit) => BunkExit(exit.position);

    private Vector3 BunkExit(Vector3 world)
    {
        var local = transform.InverseTransformPoint(world);
        local.y = Mathf.Min(local.y, 1.95f);
        return transform.TransformPoint(local);
    }

    /// <summary>The hab's power bus is live while deployed: the vanilla rover power tick then runs the cabin lights
    /// (Button2) off the hab batteries and reports the charge band on Powered. Off while towing.</summary>
    public override bool OnOff { get => IsDeployed && Progress >= 0.999f; set { } }
    protected override bool UseConditioner => false;     // the hab runs its own air (OnAtmosphericTick)

    /// <summary>Unity's automatic centre of mass counts every collider: the furniture would pull it toward the bunks and
    /// the locker. Measure it without the furniture and keep that plus the tuned height offset, as before Phase 4.</summary>
    private void FixCenterOfMass()
    {
        if (!RigidBody || InteriorRoots.Count == 0) return;
        var withFurniture = RigidBody.centerOfMass;
        foreach (var r in InteriorRoots) if (r) r.SetActive(false);
        RigidBody.ResetCenterOfMass();
        var com = RigidBody.centerOfMass + CenterOfMassOffset;
        RigidBody.centerOfMass = com;
        RigidBody.ResetInertiaTensor();                   // the rotational feel too (roll, yaw), not just the balance point
        var tensor = RigidBody.inertiaTensor;
        var tensorRotation = RigidBody.inertiaTensorRotation;
        foreach (var r in InteriorRoots) if (r) r.SetActive(true);
        RigidBody.centerOfMass = com;                     // explicit: Unity no longer recomputes them from the furniture
        RigidBody.inertiaTensor = tensor;
        RigidBody.inertiaTensorRotation = tensorRotation;
        if (!IsCursor) Debug.Log($"[RoverCargo] hab centre of mass {com:F3} (furniture would make it {withFurniture:F3}), inertia {tensor:F1}");
    }

    /// <summary>Diagnostics: what the game's cursor ray hits on this hab (collider, trigger, layer, interactable).</summary>
    private void LogCursor()
    {
        var hit = CursorManager.CursorHit;
        var col = hit.collider;
        bool mine = col && col.transform.IsChildOf(transform);
        if (col == _lastCursor || (!mine && (!_lastCursor || !_lastCursor.transform.IsChildOf(transform)))) { _lastCursor = col; return; }
        _lastCursor = col;
        var found = CursorManager.Instance ? CursorManager.Instance.FoundThing : null;
        string path = col ? col.transform.name : "none";
        for (var t = col ? col.transform.parent : null; t && t != transform; t = t.parent) path = t.name + "/" + path;
        Debug.Log($"[RoverCargo][interact] cursor {path} ({(col ? col.GetType().Name : "-")}, trigger {(col && col.isTrigger)}, layer {(col ? LayerMask.LayerToName(col.gameObject.layer) : "-")}, dist {hit.distance:0.00}) " +
                  $"found {(found ? found.name : "none")} interactable {(found && col ? found.GetInteractable(col)?.Action.ToString() ?? "none" : "-")}");
    }

    /// <summary>Why the door may not move now (hand or chip), or null (HabRules.DoorRefusal: never the battery).</summary>
    private string DoorRefusal() =>
        HabRules.DoorRefusal(IsDeployed && Progress >= 0.999f, _phase != DoorPhase.Closed && _phase != DoorPhase.Open);

    private bool HasCharge => Battery is { } b && !b.IsEmpty;

    /// <summary>The world cell just outside the door: vented and spilled gas goes there, and it is the air an open door
    /// lets in (the hab origin's cell is under the floor).</summary>
    private WorldGrid DoorGrid => new(transform.TransformPoint(new Vector3(0f, 2.2f, -3.6f)));

    // ---- logic (a chip in the circuit housing, or a logic reader): door, room, tanks; hab batteries via Ratio/Charge ----
    public override bool CanLogicRead(LogicType t) => t is LogicType.On or LogicType.Open or LogicType.Idle or LogicType.Temperature
        or LogicType.PressureInput or LogicType.PressureOutput or LogicType.TotalMolesInput or LogicType.TotalMolesOutput or LogicType.PowerGeneration || base.CanLogicRead(t);

    public override double GetLogicValue(LogicType t) => t switch
    {
        LogicType.On => IsDeployed && Progress >= 0.999f ? 1 : 0,
        LogicType.Open => IsDoorOpen ? 1 : 0,
        LogicType.Idle => _phase is DoorPhase.Closed or DoorPhase.Open ? 1 : 0,
        LogicType.Temperature => InternalAtmosphere?.Temperature.ToDouble() ?? 0,
        LogicType.PressureInput => Supply ? Supply.InternalAtmosphere.PressureGassesAndLiquids.ToDouble() : 0,
        LogicType.PressureOutput => Waste ? Waste.InternalAtmosphere.PressureGassesAndLiquids.ToDouble() : 0,
        LogicType.PowerGeneration => SolarWatts,
        LogicType.TotalMolesInput => CleanWater?.InternalAtmosphere?.TotalMolesLiquids.ToDouble() ?? 0,
        LogicType.TotalMolesOutput => WasteWater?.InternalAtmosphere?.TotalMolesLiquids.ToDouble() ?? 0,
        _ => base.GetLogicValue(t),
    };

    public override bool CanLogicWrite(LogicType t) => t == LogicType.Open || base.CanLogicWrite(t);

    /// <summary>Open writes the door, refused exactly as a hand click is (stowed or cycling: ignored).</summary>
    public override void SetLogicValue(LogicType t, double v)
    {
        if (t != LogicType.Open) { base.SetLogicValue(t, v); return; }
        bool open = v != 0;
        if (open == IsDoorOpen || DoorRefusal() != null || !GameManager.RunSimulation || DoorInteractable == null) return;
        OnServer.Interact(DoorInteractable, open ? 1 : 0);
    }

    public override List<LogicBinding> GetLogicBindings() => new() { new LogicBinding("HAB") };

    /// <summary>Hovering a console or the circuit housing shows the hab status (a real screen comes later).</summary>
    public override PassiveTooltip GetPassiveTooltip(Collider hit)
    {
        if (!hit || !ConsoleNodes.Exists(n => n && hit.transform.IsChildOf(n))) return base.GetPassiveTooltip(hit);
        var sb = new StringBuilder();
        sb.AppendLine($"Room {(InternalAtmosphere?.PressureGassesAndLiquids.ToFloat() ?? 0f):0.0} kPa, {(InternalAtmosphere?.Temperature.ToFloat() ?? 273.15f) - 273.15f:0.0} C");
        AppendStatus(sb);
        sb.AppendLine($"Chip: {ChipState()}");
        return new PassiveTooltip(toDefault: true) { Title = "Hab status", Extended = sb.ToString() };
    }

    public override StringBuilder GetExtendedText()
    {
        var sb = base.GetExtendedText() ?? new StringBuilder();
        sb.AppendLine(IsDeployed ? "Deployed: wrench the driver-side panel to stow" : "Wrench the driver-side panel to deploy");
        if (!Supply) sb.AppendLine("No AIR SUPPLY tank: opening the door vents the room outside");
        AppendStatus(sb);
        return sb;
    }

    private string ChipState() { var chip = Chip; return chip == null ? "none" : chip.CompilationError || _codeErrorState != 0 ? "error" : OnOff && Powered ? "running" : "off"; }

    /// <summary>How far the door's current cycle phase has run (0..1); 0 when closed, 1 when open.</summary>
    private double PumpProgress()
    {
        float t = Time.time - _phaseStart;
        return _phase switch
        {
            DoorPhase.PumpDown or DoorPhase.Venting or DoorPhase.Restoring => Mathf.Clamp01(t / PumpSeconds),
            DoorPhase.Opening or DoorPhase.Closing => Mathf.Clamp01(t / DoorSeconds),
            DoorPhase.Open => 1,
            _ => 0,
        };
    }

    /// <summary>Each power slot's charge ratio in slot order, -1 for an empty slot.</summary>
    private double[] CellRatios()
    {
        var cells = new List<double>();
        for (int i = 0; i < Slots.Count; i++)
            if (_powerSlots.Contains(i))
                cells.Add(Slots[i].Get() is BatteryCell b && b.PowerMaximum > 0 ? b.PowerStored / b.PowerMaximum : -1);
        return cells.ToArray();
    }

    /// <summary>What the HAB STATUS screen shows (HabRules.StatusPage): the same values as the hover status.</summary>
    public StatusInputs StatusInputs()
    {
        var (st, mx) = BatteryTotals();
        bool running = IsDeployed && Progress >= 0.999f && HasCharge;
        var (sf, _) = SlotsOf("SuitStorage");
        var (cf, cn) = SlotsOf("Charger");
        int needing = 0;
        for (int i = cf; cf >= 0 && i < cf + cn && i < Slots.Count; i++) if (Slots[i].Get() is BatteryCell c && !c.IsCharged) needing++;
        var (lf, ln) = SlotsOf("Locker");
        int items = 0;
        for (int i = lf; lf >= 0 && i < lf + ln && i < Slots.Count; i++) if (Slots[i].Get()) items++;
        var clean = CleanWater && CleanWater.InternalAtmosphere != null ? CleanWater.InternalAtmosphere : null;
        var wwater = WasteWater && WasteWater.InternalAtmosphere != null ? WasteWater.InternalAtmosphere : null;
        return new StatusInputs
        {
            Deployed = IsDeployed && Progress >= 0.999f, BatteryFlat = !HasCharge, Sealed = IsSealed, DoorPhase = _phase.ToString(),
            RoomKPa = InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0, RoomC = (InternalAtmosphere?.Temperature.ToDouble() ?? 273.15) - 273.15,
            BatteryPct = mx > 0 ? st / mx * 100 : 0, SolarW = SolarWatts,
            SupplyKPa = Supply && Supply.InternalAtmosphere != null ? Supply.InternalAtmosphere.PressureGassesAndLiquids.ToDouble() : (double?)null,
            WasteKPa = Waste && Waste.InternalAtmosphere != null ? Waste.InternalAtmosphere.PressureGassesAndLiquids.ToDouble() : (double?)null,
            WasteMaxKPa = Waste ? Waste.MaxSetting : (double?)null,
            CleanL = clean?.TotalVolumeLiquids.ToDouble(), WasteWaterL = wwater?.TotalVolumeLiquids.ToDouble(), WasteWaterMaxL = wwater?.Volume.ToDouble(),
            Charger = cf >= 0 ? HabRules.ChargerStatus(needing, running, ChargeToSpare) : "none",
            Suit = sf >= 0 && sf + 1 < Slots.Count ? HabRules.SuitStatus(Slots[sf + 1].Get<ISuit>()?.AsThing, running, ChargeToSpare) : "none",
            ShowerOn = IsShowerOn, LockerOpen = IsLockerOpen, LightsOn = Button2 == 1, LockerItems = items, LockerSlots = ln,
            Thrust = TowingRover?.ThrusterStatus ?? "none",
            BatteryJ = st, BatteryMaxJ = mx, PumpProgress = PumpProgress(), Cells = CellRatios(), ChipState = ChipState(),
            SupplyMaxKPa = Supply ? Supply.MaxSetting : (double?)null, CleanMaxL = clean?.Volume.ToDouble(),
        };
    }

    /// <summary>Door, tanks, battery, locker, suit station and charger lines (hover text and console).</summary>
    private void AppendStatus(StringBuilder sb)
    {
        sb.AppendLine($"Door {_phase} | AIR SUPPLY {(Supply ? Supply.InternalAtmosphere.PressureGassesAndLiquids.ToFloat().ToString("0") + " kPa" : "empty mount")}, WASTE {(Waste ? Waste.InternalAtmosphere.PressureGassesAndLiquids.ToFloat().ToString("0") + " kPa" : "empty mount")}, battery {(Battery ? (Battery.PowerStored / Battery.PowerMaximum * 100f).ToString("0") + " %" : "none")}");
        var (lf, ln) = SlotsOf("Locker");
        if (lf >= 0)
        {
            int items = 0;
            for (int i = lf; i < lf + ln && i < Slots.Count; i++) if (Slots[i].Get()) items++;
            sb.AppendLine($"Locker {(IsLockerOpen ? "open" : "closed")}, {items}/{ln} items");
        }
        sb.AppendLine($"Solar: {SolarWatts:0} W");
        if (SlotsOf("WaterRack").first >= 0)
            sb.AppendLine($"Water: CLEAN {LitresIn(CleanWater)}, WASTE {LitresIn(WasteWater)} | shower {(IsShowerOn ? "on" : "off")}");
        bool running = IsDeployed && Progress >= 0.999f && HasCharge;
        var (sf, sn) = SlotsOf("SuitStorage");
        if (sf >= 0 && sf + 1 < Slots.Count) sb.AppendLine($"Suit station: {HabRules.SuitStatus(Slots[sf + 1].Get<ISuit>()?.AsThing, running, ChargeToSpare)}");
        var (cf, cn) = SlotsOf("Charger");
        if (cf >= 0)
        {
            int needing = 0;
            for (int i = cf; i < cf + cn && i < Slots.Count; i++) if (Slots[i].Get() is BatteryCell c && !c.IsCharged) needing++;
            sb.AppendLine(HabRules.ChargerStatus(needing, running, ChargeToSpare));
        }
    }
}
