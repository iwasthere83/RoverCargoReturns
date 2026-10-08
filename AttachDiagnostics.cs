using Assets.Scripts.Objects;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>Temporary: trace crate/tank attach decisions on rovers and trailers (vehicle chosen, slot offered, result).</summary>
public static class AttachDiagnostics
{
    public static bool Enabled;

    public static void AfterAttach(Rover __instance, Thing target, bool __result)
    {
        if (!Enabled || target == null) return;
        Debug.Log($"[RoverCargo][attach] {target.PrefabName} ({target.GetType().Name}, tank={target is DynamicGasCanister}) -> {__instance.PrefabName} {__instance.ReferenceId}: {(__result ? "attached" : "FAILED")}");
    }

    public static void AfterCanPlaceTank(ContainerSlot __instance, Slot __result)
    {
        if (!Enabled || __instance.self is not CargoRover) return;
        Debug.Log($"[RoverCargo][attach] CanPlaceTank on {__instance.self.PrefabName}: {(__result != null ? "slot " + __result.SlotIndex + " " + __result.StringKey : "none")}");
    }

    public static void AfterMoveToSlot(DynamicThing __instance, Slot destinationSlot, bool __result)
    {
        if (!Enabled || __result || destinationSlot?.Parent is not CargoRover) return;
        string why;
        try { var r = __instance.CanEnter(destinationSlot); why = r.Result ? "ok" : "refused: " + r.Reason; } catch (System.Exception e) { why = "CanEnter threw " + e.Message; }
        Debug.Log($"[RoverCargo][attach] MoveToSlot refused: {__instance.PrefabName} -> {destinationSlot.Parent.PrefabName} slot {destinationSlot.SlotIndex} ({destinationSlot.StringKey}), occupied={destinationSlot.Get() != null}, CanEnter={why}");
    }
}
