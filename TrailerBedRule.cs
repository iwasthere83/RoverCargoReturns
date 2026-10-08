using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Trailer bays: each bay is a vanilla ContainerSlot pair (one crate OR two tanks), so bays mix freely. Vanilla
/// Rover.Attach fills the first bay that fits; on a trailer we try bays nearest to where the crate/tank stands first,
/// so the player picks the bay by placing the item beside it. The original rover's covered bays load only through an
/// open door (the nearest open bay that takes the item).
/// </summary>
public static class TrailerBedRule
{
    public static bool NearestBayAttach(Rover __instance, Thing target, ref bool __result)
    {
        if (__instance is CargoRover original && __instance is not CargoTrailer && original.BayDoorHinges.Count > 0 && target != null)
        {
            __result = OriginalBayAttach(original, target);
            return false;
        }
        if (__instance is not CargoTrailer || target == null || __instance.ConnectionSlots == null) return true;
        bool tank = target is DynamicGasCanister;
        if (__instance is CargoHab && !tank) { __result = false; return false; }   // hab cradles take portable tanks only
        var bays = new System.Collections.Generic.List<ContainerSlot>(__instance.ConnectionSlots);
        Vector3 from = target.ThingTransform.position;
        bays.Sort((a, b) => Distance(__instance, a, from).CompareTo(Distance(__instance, b, from)));
        foreach (var bay in bays)
        {
            if (tank ? bay.TryAttachTank(target) : bay.TryAttachContainer(target))
            {
                __result = true;
                return false;
            }
        }
        __result = false;
        return false;
    }

    /// <summary>The original rover's covered bays: the nearest bay whose door is open and which takes the item
    /// (RoverRules.PickBaySlot); with every fitting door shut nothing loads.</summary>
    private static bool OriginalBayAttach(CargoRover rover, Thing target)
    {
        if (!(target is DynamicThing item)) return false;
        Vector3 from = target.ThingTransform.position;
        int slot = RoverRules.PickBaySlot(target is DynamicGasCanister, rover.IsBayDoorOpen,
            i => i >= rover.Slots.Count || rover.Slots[i].Get() != null,
            b => BayDistance(rover, b, from));
        if (slot < 0) return false;
        OnServer.MoveToSlot(item, rover.Slots[slot]);
        return true;
    }

    private static float BayDistance(Rover r, int bay, Vector3 from)
    {
        int i = RoverRules.Bays[bay].Crates[0];
        var loc = i < r.Slots.Count ? r.Slots[i].Location : null;
        return loc ? (loc.position - from).sqrMagnitude : float.MaxValue;
    }

    private static float Distance(Rover r, ContainerSlot bay, Vector3 from)
    {
        int i = bay.ContainerSlots != null && bay.ContainerSlots.Length > 0 ? bay.ContainerSlots[0] : -1;
        var loc = i >= 0 && i < r.Slots.Count ? r.Slots[i].Location : null;
        return loc ? (loc.position - from).sqrMagnitude : float.MaxValue;
    }
}
