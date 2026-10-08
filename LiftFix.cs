using Assets.Scripts.Objects;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Fix for crates/tanks "floating" behind a rover (vanilla Mk I bug too). Diagnosed 2026-09-24: the occupant stays
/// parented to its slot and kinematic, but its Rigidbody gets switched to RigidbodyInterpolation.Interpolate
/// (observed after the driver gets out/in). An interpolated kinematic body under a moving parent has its transform
/// overwritten each frame with the physics pose, which is never moved with the rover, so the crate stays where it
/// was attached (measured 2.3 m off the slot at 1 m/s). Postfix on Rover.UpdateEachFrame/PhysicsUpdate: non-entity
/// slot occupants get interpolation None and are snapped back onto their slot.
/// </summary>
public static class LiftFix
{
    public static bool Enabled = true;

    public static void AfterRoverUpdate(Rover __instance)
    {
        if (!Enabled || __instance == null || __instance.IsCursor) return;
        var slots = __instance.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var occ = slot.Get();
            if (occ == null || occ is Entity) continue;
            var rb = occ.RigidBody;
            if (rb != null && rb.interpolation != RigidbodyInterpolation.None) rb.interpolation = RigidbodyInterpolation.None;
            var t = occ.ThingTransform;
            var loc = slot.Location ? slot.Location : __instance.ThingTransform;
            if (t.parent != loc) continue; // not in the attached state we manage
            if (t.localPosition.sqrMagnitude > 1e-4f || Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.5f)
            {
                float drift = t.localPosition.magnitude;
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                if (rb != null) { rb.position = loc.position; rb.rotation = loc.rotation; }
                if (drift > 0.2f && CargoRover.SlotDiagnostics)
                    Debug.Log($"[RoverCargo][liftfix] {__instance.PrefabName} slot {i} {occ.PrefabName} snapped back ({drift:0.00} m)");
            }
        }
    }
}
