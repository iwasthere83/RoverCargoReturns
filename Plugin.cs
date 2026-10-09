using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Stationeers.RoverCargo;

/// <summary>StationeersLaunchPad entry point (same pattern as Stationeers.WirelessUpgrade).</summary>
public class Plugin : MonoBehaviour
{
    public const string Version = "0.2.1";
    private static Harmony _harmony;
    private static CargoLayout _trailer, _hab, _original;
    private static CargoPrefabs.Settings _settings;
    private static bool _built;

    public void OnLoaded(List<Assembly> assemblies)
    {
        if (_harmony != null) return;
        try
        {
            string dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            var cfg = new ConfigFile(Path.Combine(dir, "RoverCargo.cfg"), true);
            _settings = new CargoPrefabs.Settings
            {
                MotorPower = Mathf.Clamp(cfg.Bind("Driving", "MotorPower", 60f, "Wheel motor torque. The Mk I uses 18 at a quarter of the mass.").Value, 5f, 400f),
                BrakePower = Mathf.Clamp(cfg.Bind("Driving", "BrakePower", 20f, "Wheel brake torque.").Value, 1f, 200f),
                MaxSpeed = Mathf.Clamp(cfg.Bind("Driving", "MaxSpeed", 6.5f, "Top speed in m/s (Mk I 7.8, 2020 Cargo Rover 3).").Value, 1f, 20f),
                RearWheelSteer = cfg.Bind("Driving", "RearWheelSteer", true, "Rear wheels counter-steer like the 2020 rover (tight turns); false = front-wheel steering only (calmer at speed). Restart required.").Value,
                TrailerComHeight = Mathf.Clamp(cfg.Bind("Stability", "TrailerCenterOfMassHeight", 0.58f, "Trailer centre of mass height above the ground in metres (the wheel centres are at 0.645). Lower = harder to roll over. Cargo in slots does not change it. Restart required.").Value, 0f, 1.5f),
                HabComHeight = Mathf.Clamp(cfg.Bind("Stability", "HabCenterOfMassHeight", 0.75f, "Hab trailer centre of mass height above the ground in metres (wheel centres 0.645). Lower = harder to tip. Restart required.").Value, 0f, 2f),
                TrailerSideGrip = Mathf.Clamp(cfg.Bind("Driving", "TrailerSideGrip", 1.4f, "Trailer tyre sideways grip multiplier (1 = same as the rover). Higher stops the trailer whipping around behind the rover. Restart required.").Value, 0.5f, 3f),
                CabinInsulation = Mathf.Clamp(cfg.Bind("Cabin", "Insulation", 0.05f, "Cabin heat exchange with the outside, as a fraction of normal (also shields the tanks in the rover's slots). 1 = uninsulated, 0.05 = about a suit. Restart required.").Value, 0f, 1f),
                StormDamage = Mathf.Clamp(cfg.Bind("Storm", "StormDamage", 0.25f, "Storm damage to the Cargo Rover and its trailers without storm armour, as a fraction of vanilla: 1 = vanilla (a Venus storm can destroy the rover), 0.25 = four times tougher, 0 = immune. Storm armour stops storm damage. Restart required.").Value, 0f, 1f),
                GlassAlpha = Mathf.Clamp01(cfg.Bind("Cabin", "GlassAlpha", 0.18f, "Cabin glass opacity; lower is clearer.").Value),
            };
            CargoRover.StormDamageSetting = _settings.StormDamage;           // applied at each storm tick
            CargoRover.LogClimate = cfg.Bind("Cabin", "LogClimate", false, "Log cabin temperature, heat leak and heat pump every 10 atmospheric ticks (diagnostics).").Value;
            ChaseCamera.Enabled = cfg.Bind("Camera", "ChaseCamera", true, "Third person (third-person key + wheel) works in rovers.").Value;
            ChaseCamera.BaseDistance = Mathf.Clamp(cfg.Bind("Camera", "Distance", 8f, "Chase camera distance at the closest zoom.").Value, 3f, 30f);
            ChaseCamera.Height = Mathf.Clamp(cfg.Bind("Camera", "Height", 2.2f, "Look-at height above the vehicle origin.").Value, 0f, 10f);

            _harmony = new Harmony("stationeers.rovercargo");
            _harmony.Patch(AccessTools.Method(typeof(CameraController), "CacheCameraPosition"),
                postfix: new HarmonyMethod(typeof(ChaseCamera), nameof(ChaseCamera.AfterCacheCameraPosition)));

            LiftFix.Enabled = cfg.Bind("Fixes", "LiftFix", true, "Keep crates/tanks on rover lifts from floating away (also fixes the vanilla Mk I).").Value;
            CargoRover.SlotDiagnostics = cfg.Bind("Fixes", "LogLiftDrift", false, "Log when a lift item had to be snapped back (diagnostics).").Value;
            var liftFix = new HarmonyMethod(typeof(LiftFix), nameof(LiftFix.AfterRoverUpdate));
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "UpdateEachFrame"), postfix: liftFix);
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "PhysicsUpdate"), postfix: liftFix);

            GripAssist.Bonus = Mathf.Clamp(cfg.Bind("Driving", "TractionBonus", 0.25f, "Extra tyre grip relative to the world's own gravity while wheels touch the ground: 0 = vanilla, 0.25 = +25%.").Value, 0f, 3f);
            GripAssist.ApplyToMkI = cfg.Bind("Driving", "GripAssistMkI", true, "Also apply the traction bonus to the vanilla Rover Mk I.").Value;
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "PhysicsUpdate"),
                postfix: new HarmonyMethod(typeof(GripAssist), nameof(GripAssist.AfterRoverPhysics)));
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "PhysicsUpdate"),
                postfix: new HarmonyMethod(typeof(HabLock), nameof(HabLock.AfterRoverPhysics)));
            _harmony.Patch(AccessTools.Method(typeof(DynamicThing), "SetWorldAtmosphere"),
                postfix: new HarmonyMethod(typeof(HabAirZone), nameof(HabAirZone.AfterSetWorldAtmosphere)));
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Inventory.InventoryManager), "CanAttackWith"),
                postfix: new HarmonyMethod(typeof(UpgradeCursor), nameof(UpgradeCursor.AfterCanAttackWith)));
            _harmony.Patch(AccessTools.Method(typeof(DynamicThing), "CanBeExposedToStorm"),
                postfix: new HarmonyMethod(typeof(HabAirZone), nameof(HabAirZone.NoStormInside)));
            _harmony.Patch(AccessTools.Method(typeof(DynamicThing), "CanBeWeathered"),
                postfix: new HarmonyMethod(typeof(HabAirZone), nameof(HabAirZone.NoStormInside)));
            _harmony.Patch(AccessTools.Method(typeof(Weather.WeatherManager), "ManagerUpdate"),
                postfix: new HarmonyMethod(typeof(HabWeatherFx), nameof(HabWeatherFx.AfterManagerUpdate)));
            _harmony.Patch(AccessTools.Method(typeof(Thing), "InteractWith", new[] { typeof(Interactable), typeof(Interaction), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(CrateLids), nameof(CrateLids.BeforeInteractWith)));   // no room for a lid in a bay
            SeatedHead.Enabled = cfg.Bind("Camera", "LimitSeatedHead", true, "Seated in the Cargo Rover (or its trailers), the head turns at most 70 deg toward where you look (vanilla turns it all the way round).").Value;
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Objects.Entities.Human), "IkSolveHead"),
                prefix: new HarmonyMethod(typeof(SeatedHead), nameof(SeatedHead.BeforeIkSolveHead)),
                postfix: new HarmonyMethod(typeof(SeatedHead), nameof(SeatedHead.AfterIkSolveHead)));

            _original = CargoLayout.ForFolder(Path.Combine(dir, "RoverAssets"), "rover.json", out var originalError);
            if (_original == null)
            {
                Debug.LogError("[RoverCargo] RoverAssets is missing or unreadable (" + originalError + "): the Rover (Cargo) is not available; the chase camera still works.");
                return;
            }
            _trailer = CargoLayout.ForFolder(Path.Combine(dir, "TrailerAssets"), "trailer.json", out var trailerError);
            if (_trailer == null) Debug.LogWarning("[RoverCargo] trailer disabled: " + trailerError);
            _hab = CargoLayout.ForFolder(Path.Combine(dir, "HabAssets"), "hab.json", out var habError);
            if (_hab == null) Debug.LogWarning("[RoverCargo] hab trailer disabled: " + habError);
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "Attach"),
                prefix: new HarmonyMethod(typeof(TrailerBedRule), nameof(TrailerBedRule.NearestBayAttach)));
            _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Objects.DynamicThing), "MoveToWorld", new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(BayRelease), nameof(BayRelease.BeforeMoveToWorld)));   // let go from a bay: beside its door
            CargoTrailer.TowDiagnostics = cfg.Bind("Fixes", "LogTow", false, "Log trailer towing physics every 2 s while hitched (diagnostics).").Value;
            CargoHab.InteractDiagnostics = cfg.Bind("Fixes", "LogInteract", false, "Log what the cursor hits inside the hab and each hab interaction (diagnostics).").Value;
            AttachDiagnostics.Enabled = cfg.Bind("Fixes", "LogAttach", false, "Log crate/tank attach decisions on rovers and trailers (diagnostics).").Value;
            if (AttachDiagnostics.Enabled)
            {
                _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.Rover), "Attach"), postfix: new HarmonyMethod(typeof(AttachDiagnostics), nameof(AttachDiagnostics.AfterAttach)));
                _harmony.Patch(AccessTools.Method(typeof(Assets.Scripts.Vehicles.ContainerSlot), "CanPlaceTank"), postfix: new HarmonyMethod(typeof(AttachDiagnostics), nameof(AttachDiagnostics.AfterCanPlaceTank)));
                _harmony.Patch(AccessTools.Method(typeof(DynamicThing), "MoveToSlot", new[] { typeof(Slot), typeof(Thing), typeof(bool) }), postfix: new HarmonyMethod(typeof(AttachDiagnostics), nameof(AttachDiagnostics.AfterMoveToSlot)));
            }
            Prefab.OnPrefabsLoaded += BuildPrefabs;
            if (Prefab.Find("Rover_MkI") != null) BuildPrefabs(); // prefabs were already registered before this mod loaded
            Debug.Log($"[RoverCargo] v{Version} loaded; waiting for prefabs.");
        }
        catch (Exception ex)
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            Debug.LogError("[RoverCargo] Startup failed: " + ex);
        }
    }

    private static void BuildPrefabs()
    {
        // Prefabs are re-registered when the game reloads data; rebuild ours whenever the Mk I is freshly registered.
        if (_built && Prefab.Find(CargoPrefabs.RoverName) != null) return;
        CargoPrefabs builder = null;
        try
        {
            builder = new CargoPrefabs(_settings);
            builder.BuildAll(_trailer, _hab, _original);
            _built = true;
            Debug.Log($"[RoverCargo] v{Version} ready: {CargoPrefabs.RoverName}, {CargoPrefabs.FrameName}, {CargoPrefabs.KitName}{(Prefab.Find(CargoPrefabs.TrailerName) != null ? ", " + CargoPrefabs.TrailerName : "")}{(Prefab.Find(CargoPrefabs.HabName) != null ? ", " + CargoPrefabs.HabName : "")} registered.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[RoverCargo] Building the Cargo Rover failed; vanilla rovers are unaffected: " + ex);
        }
        finally { foreach (var line in builder?.Log ?? new List<string>()) Debug.Log("[RoverCargo] " + line); }
    }
}
