using Assets.Scripts.Vehicles;

namespace Stationeers.RoverCargo;

/// <summary>A deployed hab is locked (kinematic); the towing rover must not drive against it.</summary>
public static class HabLock
{
    public static void AfterRoverPhysics(Rover __instance)
    {
        if (__instance is not CargoRover cr || !CargoHab.IsAnchoring(cr)) return;
        foreach (var w in cr.Wheels)
        {
            if (w?.WheelCollider == null) continue;
            w.WheelCollider.motorTorque = 0f;
            w.WheelCollider.brakeTorque = cr.BrakePower * 10f;
        }
    }
}
