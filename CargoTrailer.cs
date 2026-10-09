using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Cargo Trailer (spec docs/superpowers/specs/2026-09-24-trailer-cargo-design.md). A CargoRover with no cabin, seats or
/// motor: it inherits lift mounting (wrench a crate/tank near it), LiftFix, traction bonus, crowbar flip, damage and the
/// suspension-arm animation. Hitch: wrench it with its tow bar near a Cargo Rover's hitch point -> ball joint.
/// Brakes hold while unhitched and release while towed. After a load, a trailer whose hitch sits on a rover hitch
/// re-couples by itself (both are saved in place), so no extra save data is needed.
/// </summary>
public class CargoTrailer : CargoRover
{
    public const float HitchReach = 1.8f;      // wrench range between hitch points
    public const float AutoHitchReach = 0.6f;  // re-couple after load
    public Transform TrailerHitch;
    public List<Light> TailLights = new();
    public List<int> CrateSlotIndices = new(), TankSlotIndices = new();

    public float ParkingBrakeTorque = 200f;
    public const float TowRollTorque = 0.0001f;
    public static bool TowDiagnostics;
    private float _nextDiag;

    /// <summary>Temporary: why does a hitched rig not move? Logs every layer of the tow.</summary>
    private void LogTow()
    {
        var trb = RigidBody;
        var rrb = _tow.RigidBody;
        int tg = 0, rg = 0;
        foreach (var w in Wheels) if (w?.WheelCollider != null && w.WheelCollider.isGrounded) tg++;
        foreach (var w in _tow.Wheels) if (w?.WheelCollider != null && w.WheelCollider.isGrounded) rg++;
        float trailerBrake = Wheels.Count > 0 && Wheels[0].WheelCollider ? Wheels[0].WheelCollider.brakeTorque : -1;
        float roverMotor = _tow.Wheels.Count > 0 && _tow.Wheels[0].WheelCollider ? _tow.Wheels[0].WheelCollider.motorTorque : -1;
        float roverBrake = _tow.Wheels.Count > 0 && _tow.Wheels[0].WheelCollider ? _tow.Wheels[0].WheelCollider.brakeTorque : -1;
        Debug.Log($"[RoverCargo][tow] trailer v={trb.velocity.magnitude:0.00} kin={trb.isKinematic} sleep={trb.IsSleeping()} mass={trb.mass} grounded={tg}/{Wheels.Count} brake={trailerBrake:0} | " +
                  $"rover v={rrb.velocity.magnitude:0.00} kin={rrb.isKinematic} grounded={rg}/{_tow.Wheels.Count} motorTorque={roverMotor:0.0} brake={roverBrake:0.0} targetMotor={_tow.TargetMotorPower:0.0} | " +
                  $"jointForce={_joint.currentForce.magnitude:0} hitchGap={Vector3.Distance(TrailerHitch.position, _tow.HitchPoint.position):0.00}");
        var ha = HitchAngles();
        Vector3 jf = _joint.currentForce;
        Debug.Log($"[RoverCargo][tow] hitch angles from hitched pose: pitch={ha.x:0.0} (limit +-{_joint.highAngularXLimit.limit:0}, {_joint.angularXMotion}) " +
                  $"yaw={ha.y:0.0} (+-{_joint.angularYLimit.limit:0}) roll={ha.z:0.0} (+-{_joint.angularZLimit.limit:0}, {_joint.angularZMotion}) | " +
                  $"joint force world: vertical={jf.y:0} horizontal={new Vector2(jf.x, jf.z).magnitude:0} | " +
                  $"wheel travel (0 = full droop, 1 = full bump): trailer {Travel(Wheels)} | rover {Travel(_tow.Wheels)}");
        Debug.Log("[RoverCargo][tow] overlaps rover<->trailer (not ignored): " + Overlaps());
        var wheels = new List<string>();
        foreach (var w in Wheels)
        {
            if (w?.WheelCollider == null) continue;
            w.WheelCollider.GetGroundHit(out var hit);
            wheels.Add($"{w.WheelCollider.name}: rpm={w.WheelCollider.rpm:0.0} fwdSlip={hit.forwardSlip:0.00} sideSlip={hit.sidewaysSlip:0.00} load={hit.force:0} sprung={w.WheelCollider.sprungMass:0.0} on={(hit.collider ? hit.collider.name : "-")} fwdDir={ThingTransform.InverseTransformDirection(hit.forwardDir):0.00}");
        }
        var rw = new List<string>();
        foreach (var w in _tow.Wheels)
        {
            if (w?.WheelCollider == null) continue;
            w.WheelCollider.GetGroundHit(out var h2);
            rw.Add($"{w.WheelCollider.name}: load={h2.force:0} fwdSlip={h2.forwardSlip:0.00} sideSlip={h2.sidewaysSlip:0.00} steer={w.WheelCollider.steerAngle:0.0} rpm={w.WheelCollider.rpm:0}");
        }
        Vector3 fLocal = _tow.ThingTransform.InverseTransformDirection(_joint.currentForce);
        var jointsOnMe = new List<string>();
        foreach (var j in FindObjectsOfType<Joint>())
            if (j.GetComponent<Rigidbody>() == trb || j.connectedBody == trb)
                jointsOnMe.Add($"{j.GetType().Name} on {j.name} -> {(j.connectedBody ? j.connectedBody.name : "WORLD")}");
        var bodies = GetComponentsInChildren<Rigidbody>(true);
        Debug.Log($"[RoverCargo][tow] joints touching trailer: {string.Join("; ", jointsOnMe)} | rigidbodies in trailer: {bodies.Length} | " +
                  $"trailer local vel={ThingTransform.InverseTransformDirection(trb.velocity):0.00} angVel={trb.angularVelocity:0.00} com={trb.centerOfMass:0.00} inertia={trb.inertiaTensor:0.0}");
        Debug.Log($"[RoverCargo][tow] joint force in rover frame: up={fLocal.y:0} forward={fLocal.z:0} side={fLocal.x:0} torque={_joint.currentTorque.magnitude:0} | " +
                  $"trailer pitch={Vector3.SignedAngle(_tow.ThingTransform.forward, ThingTransform.forward, _tow.ThingTransform.right):0.0} deg | " +
                  $"roll: rover={Vector3.SignedAngle(Vector3.up, _tow.ThingTransform.up, _tow.ThingTransform.forward):0.0} trailer={Vector3.SignedAngle(Vector3.up, ThingTransform.up, ThingTransform.forward):0.0} relative={Vector3.SignedAngle(_tow.ThingTransform.up, ThingTransform.up, ThingTransform.forward):0.0} deg | " +
                  $"hitch heights above ground: rover {HeightAboveGround(_tow.HitchPoint.position):0.00} trailer origin->hitch {TrailerHitch.localPosition.y:0.00} | rover wheels " + string.Join(" | ", rw));
        Debug.Log("[RoverCargo][tow] trailer wheels " + string.Join(" | ", wheels) + " || body contacts: " + (_probe ? _probe.Drain() : "no probe") +
                  $" || rb drag={trb.drag:0.00} angDrag={trb.angularDrag:0.00} constraints={trb.constraints}");
    }

    /// <summary>The hitch's yaw limit either way (degrees): no jackknife into the rover or its fitted upgrades.</summary>
    protected virtual float HitchYawLimit => 70f;

    private ConfigurableJoint _joint;
    private Quaternion _hitchRel = Quaternion.identity;   // trailer rotation in the rover's frame when hitched (the limits' zero)

    /// <summary>The hitch's pitch/yaw/roll from its hitched pose, in the joint frame (X right = pitch, Y up = yaw, Z = roll).</summary>
    private Vector3 HitchAngles()
    {
        var q = Quaternion.Inverse(_hitchRel) * (Quaternion.Inverse(_tow.RigidBody.rotation) * RigidBody.rotation);
        var e = q.eulerAngles;
        static float W(float a) => a > 180f ? a - 360f : a;
        return new Vector3(W(e.x), W(e.y), W(e.z));
    }
    private TowProbe _probe;

    /// <summary>Every solid collider pair between this trailer (incl. its slot items) and the towing rover (incl. its slot
    /// items) that physics does not ignore and that overlaps now: owner, collider and depth.</summary>
    private string Overlaps()
    {
        var mine = GetComponentsInChildren<Collider>(false);
        var theirs = _tow.GetComponentsInChildren<Collider>(false);
        var found = new List<string>();
        int checkedPairs = 0;
        foreach (var a in mine)
        {
            if (!a || a.isTrigger || !a.enabled || a is WheelCollider) continue;
            foreach (var b in theirs)
            {
                if (!b || b.isTrigger || !b.enabled || b is WheelCollider) continue;
                if (Physics.GetIgnoreCollision(a, b) || !a.bounds.Intersects(b.bounds)) continue;
                checkedPairs++;
                if (Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out var dir, out var depth))
                    found.Add($"{Owner(a)}:{a.name} x {Owner(b)}:{b.name} depth={depth:0.000} dir={dir:0.00}");
            }
        }
        return found.Count == 0 ? $"none ({checkedPairs} close pairs)" : string.Join(" | ", found);
    }

    private static string Owner(Collider c) => c.attachedRigidbody ? $"{c.attachedRigidbody.name}{(c.attachedRigidbody.isKinematic ? "(kin)" : "")}" : "static";

    private static string Travel(List<Wheel> ws)
    {
        var parts = new List<string>();
        foreach (var w in ws)
        {
            var wc = w?.WheelCollider;
            if (wc == null) continue;
            if (!wc.GetGroundHit(out var h)) { parts.Add("air"); continue; }
            float fromTop = -wc.transform.InverseTransformPoint(h.point).y + wc.center.y - wc.radius;   // hub below its top stop
            parts.Add((1f - Mathf.Clamp01(fromTop / Mathf.Max(0.01f, wc.suspensionDistance))).ToString("0.00"));
        }
        return string.Join(" ", parts);
    }

    private static float HeightAboveGround(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 10f, 1 << LayerMask.NameToLayer("Terrain")) ? hit.distance - 0.5f : -1f;
    private CargoRover _tow;
    private float _autoHitchAt = -1f;
    private readonly List<(Collider, Collider)> _ignored = new();

    protected override bool HasCabin => false;
    public override bool HasReadableAtmosphere => false;
    public bool IsHitched => _joint != null && _tow != null;
    public CargoRover TowedBy => IsHitched ? _tow : null;
    protected override bool TeardownHitched => IsHitched;
    public CargoRover TowingRover => IsHitched ? _tow : null;
    public float HitchForce => _joint ? _joint.currentForce.magnitude : 0f;

    public override void InitInternalAtmosphere() { }

    public override void Awake()
    {
        base.Awake();
        _autoHitchAt = Time.time + 1.5f;
    }

    public override void UpdateEachFrame()
    {
        base.UpdateEachFrame();
        if (IsCursor || GameManager.GameState != GameState.Running) return;
        if (_joint != null && _tow == null) Unhitch(); // towing rover destroyed
        if (!IsHitched && _autoHitchAt > 0f && Time.time >= _autoHitchAt)
        {
            _autoHitchAt = -1f;
            var near = FindRover(AutoHitchReach);
            if (near != null) Hitch(near);
        }
        // Free trailers hold still; towed trailers roll (the rover brakes the rig). Vanilla Wheel.Apply only sets
        // brake torque on motorised wheels, and trailer wheels are not motorised, so set it on the colliders directly.
        // PhysX "sticky tyre": a near-stationary wheel with exactly zero drive torque is held by a static-friction
        // constraint (outside the slip model: slip reads 0, yet the wheel will not roll), which anchored the towed
        // trailer against ~470 N of pull. A negligible motor torque while towed disables it.
        // Brakes follow the towing rover: while it drives, release and roll; while it brakes or stands, brake as hard as its
        // wheels do (never the 200 parking brake), and the tiny torque stays on even parked: the rover's sticky tyres anchor
        // the rig, and a pinned trailer locked to them jams (HabRules.TrailerWheelTorque). Unhitched: full parking brake.
        bool towDriving = IsHitched && Mathf.Abs(_tow.TargetMotorPower) > 0.5f;
        float towBrake = IsHitched ? _tow.CurrentBrakePower : 0f;
        var (drive, brake) = HabRules.TrailerWheelTorque(IsHitched, towDriving, towBrake, ParkingBrakeTorque, TowRollTorque);
        foreach (var w in Wheels)
        {
            if (w?.WheelCollider == null) continue;
            if (!Mathf.Approximately(w.WheelCollider.motorTorque, drive)) w.WheelCollider.motorTorque = drive;
            if (!Mathf.Approximately(w.WheelCollider.brakeTorque, brake)) w.WheelCollider.brakeTorque = brake;
        }
        if (IsHitched && TowDiagnostics && Time.time >= _nextDiag)
        {
            _nextDiag = Time.time + 2f;
            LogTow();
        }
        bool lights = IsHitched && _tow.OnOff && _tow.Powered && _tow.Button1 == 1;
        foreach (var l in TailLights) if (l && l.enabled != lights) l.enabled = lights;
        ApplyRigHold();
    }

    /// <summary>True while the trailer stands on its own legs (the deployed hab): frozen, and its rover with it.</summary>
    protected virtual bool OnLegs => false;

    private float _stillFor;
    private bool _parkHeld, _selfFrozen;
    private CargoRover _frozenRover;
    public bool IsParkHeld => _parkHeld;

    /// <summary>Freeze (kinematic) what HabRules.RigHold says: a parked hitched rig, trailer and rover together, and a
    /// deployed hab with its rover. Only what this code froze is ever released, so a body the game itself holds
    /// kinematic (a remote client's copy) is left alone.</summary>
    private void ApplyRigHold()
    {
        bool deployed = OnLegs;
        bool parkHold = !deployed && HasAuthority && UpdateParkHold();
        if (!parkHold) { _parkHeld = false; if (deployed) _stillFor = 0f; }
        var (trailer, rover) = HabRules.RigHold(IsHitched, deployed, parkHold);
        SetRoverFrozen(rover ? _tow : null);
        if (!RigidBody) return;
        if (trailer && !RigidBody.isKinematic) { RigidBody.isKinematic = true; _selfFrozen = true; }
        else if (!trailer && _selfFrozen)
        {
            _selfFrozen = false;
            RigidBody.isKinematic = false;
            RigidBody.WakeUp();
        }
    }

    /// <summary>The parked hold's clock (HabRules.ParkHold): on the authority, while the rig stands still with no gas.</summary>
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
        if (_parkHeld != was && TowDiagnostics) Debug.Log($"[RoverCargo][tow] {name} park hold {(_parkHeld ? "on" : $"off (driving={driving}, rover v={rs:0.00}, hitch {HitchForce:0} N)")}");
        return _parkHeld;
    }

    /// <summary>The towing rover is held with the trailer: held alone, the trailer became a fixed post the parked rover
    /// still pushed on (22 to 840 N in 20 s, a slight twist, and a nose kick when released). Only a rover this machine
    /// simulates and that was not already kinematic; released (and woken) with the trailer.</summary>
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

    public override DelayedActionInstance AttackWith(Attack attack, bool doAction = true)
    {
        if (UpgradeAttack(attack, doAction) is { } up) return up;   // wrench + material, or a wrench on an upgrade part
        if (!(attack.SourceItem is Wrench)) return base.AttackWith(attack, doAction);
        var result = new DelayedActionInstance { Duration = 1f, ActionMessage = IsHitched ? "Unhitch trailer" : "Hitch trailer" };
        if (!IsHitched)
        {
            var rover = FindRover(HitchReach);
            if (rover == null) return result.Fail("Back a Rover (Cargo) up to the tow bar first");
            if (!doAction) return result.Succeed();
            if (GameManager.RunSimulation) Hitch(rover);
            return result.Succeed();
        }
        if (!doAction) return result.Succeed();
        if (GameManager.RunSimulation) Unhitch();
        return result.Succeed();
    }

    private CargoRover FindRover(float reach)
    {
        if (!TrailerHitch) return null;
        CargoRover best = null;
        float bestD = reach;
        foreach (var r in AllRovers)
        {
            if (r is not CargoRover cr || cr is CargoTrailer || cr.IsCursor || !cr.HitchPoint) continue;
            float d = Vector3.Distance(cr.HitchPoint.position, TrailerHitch.position);
            if (d < bestD) { bestD = d; best = cr; }
        }
        return best;
    }

    public void Hitch(CargoRover rover)
    {
        if (rover == null || rover.RigidBody == null || RigidBody == null || IsHitched) return;
        // Bring the hitch points together first so the joint does not yank the rig.
        Vector3 delta = rover.HitchPoint.position - TrailerHitch.position;
        Debug.Log($"[RoverCargo][tow] hitching: snap {delta.magnitude:0.00} m, trailer kinematic={RigidBody.isKinematic}, rover kinematic={rover.RigidBody.isKinematic}");
        RigidBody.position += delta;
        ThingTransform.position += delta;
        _joint = gameObject.AddComponent<ConfigurableJoint>();
        _joint.connectedBody = rover.RigidBody;
        _joint.autoConfigureConnectedAnchor = false;
        _joint.anchor = ThingTransform.InverseTransformPoint(TrailerHitch.position);
        _joint.connectedAnchor = rover.ThingTransform.InverseTransformPoint(rover.HitchPoint.position);
        _joint.axis = Vector3.right;          // angular X = pitch
        _joint.secondaryAxis = Vector3.up;    // angular Y = yaw, Z = roll
        _joint.xMotion = _joint.yMotion = _joint.zMotion = ConfigurableJointMotion.Locked;
        _joint.angularXMotion = _joint.angularYMotion = _joint.angularZMotion = ConfigurableJointMotion.Limited;
        _hitchRel = Quaternion.Inverse(rover.RigidBody.rotation) * RigidBody.rotation;
        _joint.lowAngularXLimit = new SoftJointLimit { limit = -35f };
        _joint.highAngularXLimit = new SoftJointLimit { limit = 35f };
        _joint.angularYLimit = new SoftJointLimit { limit = HitchYawLimit };  // no jackknife into the rover
        _joint.angularZLimit = new SoftJointLimit { limit = 30f };
        _joint.projectionMode = JointProjectionMode.PositionAndRotation;
        _joint.projectionDistance = 0.05f;
        _joint.projectionAngle = 5f;
        _joint.enableCollision = false;
        _tow = rover;
        IgnoreCollisions(rover, true);
        if (TowDiagnostics) _probe = GetComponent<TowProbe>() ?? gameObject.AddComponent<TowProbe>();
        Debug.Log($"[RoverCargo] trailer {ReferenceId} hitched to rover {rover.ReferenceId}");
    }

    public void Unhitch()
    {
        if (_joint != null) Destroy(_joint);
        if (_tow != null) IgnoreCollisions(_tow, false);
        _joint = null;
        _tow = null;
    }

    /// <summary>Which colliders the hitch stops from colliding: the vehicles' own solid parts and their cargo, never a
    /// person. A player seated in the rover (or asleep in a hab bunk) is its child: swept in, the hab let them fall
    /// through every inch of it until unhitched (the user's 0.2.2 report, loading a save in the rover's seat).</summary>
    private static bool HitchIgnores(Collider c) => c && !c.isTrigger && !c.GetComponentInParent<Entity>();

    private void IgnoreCollisions(CargoRover rover, bool ignore)
    {
        if (ignore)
        {
            foreach (var a in GetComponentsInChildren<Collider>(true))
            {
                if (!HitchIgnores(a)) continue;
                foreach (var b in rover.GetComponentsInChildren<Collider>(true))
                {
                    if (!HitchIgnores(b)) continue;
                    Physics.IgnoreCollision(a, b, true);
                    _ignored.Add((a, b));
                }
            }
            return;
        }
        foreach (var (a, b) in _ignored) if (a && b) Physics.IgnoreCollision(a, b, false);
        _ignored.Clear();
    }

    public override void OnDestroy()
    {
        SetRoverFrozen(null);
        Unhitch();
        base.OnDestroy();
    }

    public override StringBuilder GetExtendedText()
    {
        var sb = base.GetExtendedText() ?? new StringBuilder();   // vanilla lines (damage); no cabin lines on a trailer
        sb.AppendLine(IsHitched ? "Hitched: wrench to unhitch" : "Wrench near a Rover (Cargo) hitch to couple");
        int crates = 0, tanks = 0;
        foreach (var i in CrateSlotIndices) if (Slots[i].Get() != null) crates++;
        foreach (var i in TankSlotIndices) if (Slots[i].Get() != null) tanks++;
        if (CrateSlotIndices.Count > 0)
            sb.AppendLine($"Bays: {CrateSlotIndices.Count} (each 1 crate or 2 tanks) - {crates} crates, {tanks} tanks aboard");
        return sb;
    }


}
