using Assets.Scripts;
using Assets.Scripts.Objects;

namespace Stationeers.RoverCargo;

/// <summary>
/// Crate lids in the original rover's bays: a crate there opens only while its bay door is open (its lid would come
/// through a shut door; RoverRules.LidBlock), and the rover closes lids open where that rule refuses them - a door shut on
/// an open crate, a save (CargoRover.CloseCrampedLids).
/// </summary>
public static class CrateLids
{
    /// <summary>Thing.InteractWith prefix: opening a crate's lid (InteractableType.Open) in one of our bays where it has no
    /// room is refused with the reason; closing is always allowed, and everything else runs vanilla.</summary>
    public static bool BeforeInteractWith(Thing __instance, Interactable interactable, ref Thing.DelayedActionInstance __result)
    {
        if (__instance is not Container crate || interactable == null || interactable.Action != InteractableType.Open || interactable.State == 1)
            return true;
        if (crate.ParentSlot is not { Parent: CargoRover rover } slot || rover is CargoTrailer || rover.BayDoorHinges.Count == 0)
            return true;
        var why = RoverRules.LidBlock(slot.SlotIndex, k => k < rover.Slots.Count && rover.Slots[k].Get() != null, rover.IsBayDoorOpen);
        if (why == null) return true;
        __result = new Thing.DelayedActionInstance { Duration = 0f, ActionMessage = interactable.ContextualName }.Fail(why);
        return false;
    }
}
