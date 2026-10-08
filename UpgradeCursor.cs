using Assets.Scripts;
using Assets.Scripts.Inventory;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// The wrench on an upgrade part or a bay door leaf: the game's cursor refuses to attack a trigger collider that is not
/// the thing's own transform (InventoryManager.CanAttackWith), and those colliders are triggers (no collisions, no
/// mass). Allow them for our vehicles so "Hold to remove" and "Open bay door" work.
/// </summary>
public static class UpgradeCursor
{
    public static void AfterCanAttackWith(Collider selectedCollider, ref bool __result)
    {
        if (!__result && selectedCollider && CursorManager.CursorThing is CargoRover cr && (cr.IsUpgradeCollider(selectedCollider) || cr.IsBayDoorCollider(selectedCollider)))
            __result = true;
    }
}
