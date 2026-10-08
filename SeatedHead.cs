using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// The seated head (the user's check-in 3 report: in third person it spun round and upside down). Vanilla
/// Human.IkSolveHead turns the head bone toward where its owner looks, at 90 % weight when seated, with no limit. For
/// anyone seated in our vehicles the turn it adds is capped at RoverRules.HeadTurnMaxDeg: the prefix keeps the
/// animated head's rotation, the postfix limits the turn from it.
/// </summary>
public static class SeatedHead
{
    public static bool Enabled = true;

    public static void BeforeIkSolveHead(Human __instance, out Quaternion __state) =>
        __state = __instance.HeadBone ? __instance.HeadBone.rotation : Quaternion.identity;

    public static void AfterIkSolveHead(Human __instance, Quaternion __state)
    {
        var head = __instance.HeadBone;
        if (!Enabled || !head || __instance.ParentSlot?.Parent is not CargoRover) return;
        (head.rotation * Quaternion.Inverse(__state)).ToAngleAxis(out float angle, out Vector3 axis);
        float turn = RoverRules.WrapDeg(angle), capped = RoverRules.CapTurn(turn);
        if (capped != turn) head.rotation = Quaternion.AngleAxis(capped, axis) * __state;
    }
}
