using Assets.Scripts.Objects;

namespace Stationeers.RoverCargo;

/// <summary>Inside a deployed hab's walls, the cabin is the world atmosphere and storms do not reach.</summary>
public static class HabAirZone
{
    private static CargoHab Around(DynamicThing t)
    {
        var habs = CargoHab.DeployedHabs;   // immutable snapshot: this runs on the occlusion worker thread
        if (habs.Length == 0 || (object)t == null || t.ParentSlot != null || t is CargoRover) return null;
        foreach (var hab in habs)
            if ((object)hab != null && hab.InternalAtmosphere != null && hab.ContainsPoint(t.Position)) return hab;
        return null;
    }

    public static void AfterSetWorldAtmosphere(DynamicThing __instance)
    {
        var hab = Around(__instance);
        if (hab != null) __instance.WorldAtmosphere = hab.InternalAtmosphere;
    }

    public static void NoStormInside(DynamicThing __instance, ref bool __result)
    {
        if (__result && Around(__instance) != null) __result = false;
    }
}
