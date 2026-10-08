using Assets.Scripts.Inventory;
using HarmonyLib;
using UnityEngine;
using StormVolumes;
using Weather;

namespace Stationeers.RoverCargo;

/// <summary>
/// Storm visuals for the hab, as vanilla does for rooms: the full-screen storm dust is hidden and the fog pushed out
/// 15 m while the local player is inside a deployed hab whose door is sealed, and the storm is seen from inside through
/// a "storm curtain" - a quad over the door (and its window) drawn with the game's own storm-card material, shown
/// while vanilla's storm cards are shown and the room is sealed. An open door breaks the seal, as it breaks a vanilla
/// room: the room counts as outside, so the doorway shows the real storm instead of a curtain.
/// </summary>
public static class HabWeatherFx
{
    private static readonly AccessTools.FieldRef<WeatherManager, StormPostEffect> PostEffect =
        AccessTools.FieldRefAccess<WeatherManager, StormPostEffect>("stormPostEffect");
    private static readonly AccessTools.FieldRef<WeatherManager, StormCardMeshController> Cards =
        AccessTools.FieldRefAccess<WeatherManager, StormCardMeshController>("stormCardMeshController");
    private static readonly AccessTools.FieldRef<StormCardMeshController, Material> CardMaterial =
        AccessTools.FieldRefAccess<StormCardMeshController, Material>("_material");
    private static readonly AccessTools.FieldRef<StormCardMeshController, MeshRenderer> CardRenderer =
        AccessTools.FieldRefAccess<StormCardMeshController, MeshRenderer>("stormCardRenderer");
    private static bool _wasInside;

    /// <summary>Storm-card material while vanilla shows its storm cards, else null (main thread).</summary>
    public static Material CurtainMaterial { get; private set; }

    public static void AfterManagerUpdate(WeatherManager __instance)
    {
        var cards = Cards(__instance);
        var cardRenderer = cards ? CardRenderer(cards) : null;
        // only while a storm runs: vanilla switches the card renderer off only when a storm ends, so in clear weather it
        // can be on (drawing nothing, it has no curtain mesh then) and would put our curtain in a clear sky
        bool storm = WeatherManager.IsWeatherEventRunning && WeatherManager.CurrentWeatherEvent?.StormEffect != null;
        CurtainMaterial = storm && cardRenderer && cardRenderer.enabled ? CardMaterial(cards) : null;

        var player = InventoryManager.Parent;
        bool inside = false;
        if (player && WeatherManager.IsWeatherEventRunning)
            foreach (var hab in CargoHab.DeployedHabs)
                if ((object)hab != null && hab.IsSealed && hab.ContainsPoint(player.Position)) { inside = true; break; }
        if (inside)
        {
            var fx = PostEffect(__instance);
            if (fx && fx.enabled) fx.enabled = false;
            var fog = WeatherManager.CurrentWeatherEvent?.Fog;
            if (fog != null)
            {
                RenderSettings.fogStartDistance = 15f + fog.StartDistance;
                RenderSettings.fogEndDistance = 15f + fog.EndDistance;
            }
        }
        bool leaving = _wasInside && !inside;
        _wasInside = inside;                                      // before any call that could throw
        // vanilla resets the fog itself when a storm ends; UpdateFogDistance needs a running event (else NRE)
        if (leaving && WeatherManager.IsWeatherEventRunning && WeatherManager.CurrentWeatherEvent?.Fog != null)
            WeatherManager.UpdateFogDistance();
    }

    /// <summary>A 2 x 2 m curtain quad facing outward (-z) just behind the hab's door face, like vanilla's room faces.</summary>
    public static Mesh CurtainQuad(Vector3 centre)
    {
        var m = new Mesh { name = "HabStormCurtain" };
        m.SetVertices(new[] { centre + new Vector3(1f, -1f, 0f), centre + new Vector3(-1f, -1f, 0f),
                              centre + new Vector3(-1f, 1f, 0f), centre + new Vector3(1f, 1f, 0f) });
        m.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
        // front face toward the room, like vanilla's WeatherManager.AddQuad (the storm card shader culls back faces);
        // the old winding faced outward, so from inside the hab the curtain was culled
        m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
