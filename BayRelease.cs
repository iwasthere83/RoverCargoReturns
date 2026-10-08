using Assets.Scripts;
using Assets.Scripts.Objects;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// A crate or tank let go from one of the original rover's covered bays (the wrench's Disconnect calls
/// OnServer.MoveToWorld; so does a destroyed rover) lands on the ground beside its door (RoverRules.BayDropPoint).
/// Vanilla releases it where it sits in the slot, pushed along its forward axis: loose inside a bay, or tipped out of it.
/// Container and PortableAtmospherics call this base method first, so one prefix covers crates and tanks.
/// </summary>
public static class BayRelease
{
    public static bool BeforeMoveToWorld(DynamicThing __instance, float force, ref bool __result)
    {
        var slot = __instance.ParentSlot;
        if (!GameManager.RunSimulation || !(slot?.Parent is CargoRover rover) || rover is CargoTrailer || rover.BayDoorHinges.Count == 0)
            return true;
        if (!RoverRules.IsBaySlot(rover.Slots.IndexOf(slot)) || !slot.Location) return true;
        var at = rover.ThingTransform.InverseTransformPoint(slot.Location.position);
        var (x, y, z) = RoverRules.BayDropPoint((at.x, at.y, at.z));
        __instance.HandleCollisionWith(rover, ignore: false);                  // as vanilla's release: it collides again
        __result = __instance.MoveToWorld(rover.ThingTransform.TransformPoint(new Vector3(x, y, z)), slot.Location.rotation, Vector3.zero, Vector3.zero, force);
        return false;
    }
}
