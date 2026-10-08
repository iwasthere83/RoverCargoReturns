using Assets.Scripts;
using Assets.Scripts.Vehicles;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>
/// Traction bonus relative to each world's own gravity. Unity wheel friction scales with the wheel's normal load
/// (mass x gravity), so low-gravity worlds are slippery while cornering forces do not shrink. Outdoors DynamicThing
/// uses the planet's Physics.gravity (indoor rooms already add an Earth offset). While wheels are grounded this adds
/// a downforce of Bonus x world gravity along the chassis down axis (default +25%: each world keeps its character,
/// just grippier), scaled by the fraction of grounded wheels so jumps are unaffected. Acting along -up rather than
/// world down adds grip without adding weight along a slope.
/// </summary>
public static class GripAssist
{
    public static float Bonus = 0.25f;     // 0 = vanilla physics, 0.25 = 25% more tyre load than the world gives
    public static bool ApplyToMkI = true;

    public static void AfterRoverPhysics(Rover __instance)
    {
        if (__instance == null || __instance.IsCursor) return;
        // every machine decides firing (the server burns the propellant even while a client drives)
        if (__instance is CargoRover own and not CargoTrailer) own.UpdateThrust(Time.fixedDeltaTime);
        if (!__instance.HasAuthority) return;
        if (!ApplyToMkI && __instance is not CargoRover) return;
        var rb = __instance.RigidBody;
        if (rb == null || rb.isKinematic || __instance.Room != null) return;
        if (!WorldManager.HasGravityAtHeight(__instance.Position.y)) return;
        var source = __instance is CargoTrailer tr ? tr.TowingRover : __instance as CargoRover;   // towed trailers share the rover's thrust
        float extra = StormRules.GripDownforce(WorldManager.WorldGravity, Bonus, source != null && source.IsThrusting);
        if (extra <= 0f) return;
        int grounded = 0, total = 0;
        foreach (var wheel in __instance.Wheels)
        {
            if (wheel?.WheelCollider == null) continue;
            total++;
            if (wheel.WheelCollider.isGrounded) grounded++;
        }
        if (grounded == 0) return;
        rb.AddForce(-__instance.ThingTransform.up * (extra * grounded / total), ForceMode.Acceleration);
    }
}
