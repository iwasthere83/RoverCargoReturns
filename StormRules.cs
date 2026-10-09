namespace Stationeers.RoverCargo;

public enum Upgrade { Armour, Fairings, Thrusters }
public enum WrenchAction { None, Fit, Remove }

/// <summary>Vehicle upgrades (spec docs/superpowers/specs/2026-09-26-storm-upgrades-design.md): what the other hand
/// fits, what it costs, the storm and grip factors, when the thrusters fire and how fast they burn, the waste limit.</summary>
public static class StormRules
{
    public const float VenusGravity = 8.87f;                 // Worlds/Venus/Venus.xml
    public const float MinFireSpeed = 0.5f, FitSeconds = 3f;
    const float AloneMinutesPerMPa = 20f, FloorMinutesPerMPa = 10f, DefaultWasteKPa = 4053f;

    public static Upgrade? UpgradeFor(string prefab) => prefab switch
    {
        "ItemSteelSheets" => Upgrade.Armour,
        "ItemPlasticSheets" => Upgrade.Fairings,
        "ItemKitGovernedGasRocketEngine" => Upgrade.Thrusters,
        _ => null,
    };

    public static string Material(Upgrade u) => u switch
    {
        Upgrade.Armour => "ItemSteelSheets", Upgrade.Fairings => "ItemPlasticSheets", _ => "ItemKitGovernedGasRocketEngine",
    };

    public static int Needed(Upgrade u) => u switch { Upgrade.Armour => 10, Upgrade.Fairings => 20, _ => 1 };

    static string Name(Upgrade u) => u switch { Upgrade.Armour => "Storm armour", Upgrade.Fairings => "Wind fairings", _ => "Thrusters" };

    static string Cost(Upgrade u, int n = 0)
    {
        if (n <= 0) n = Needed(u);
        return u switch
        {
            Upgrade.Armour => $"{n} steel sheets", Upgrade.Fairings => $"{n} plastic sheets", _ => "a Kit (Governed Gas Rocket Engine)",
        };
    }

    /// <summary>The cost within the material's stack cap (one hand holds one stack): a cap below the cost charges one
    /// full stack. maxStack 0 = unknown, the full cost.</summary>
    public static int NeededFor(Upgrade u, int maxStack) => maxStack > 0 ? System.Math.Min(Needed(u), maxStack) : Needed(u);

    /// <summary>A refund of count items as stacks of at most maxStack (0 = unknown: one stack).</summary>
    public static int[] DropStacks(int count, int maxStack)
    {
        if (maxStack <= 0 || count <= maxStack) return new[] { count };
        var list = new System.Collections.Generic.List<int>();
        for (int left = count; left > 0; left -= maxStack) list.Add(System.Math.Min(left, maxStack));
        return list.ToArray();
    }

    public static string FitVerb(Upgrade u) => $"Hold to fit {Name(u).ToLowerInvariant()} ({Cost(u)})";

    public static string RemoveVerb(Upgrade u) => $"Hold to remove {Name(u).ToLowerInvariant()}";

    public static string FitRefusal(Upgrade u, bool fitted, int held, bool isRover, int maxStack = 0)
    {
        if (u == Upgrade.Thrusters && !isRover) return "Thrusters fit the Cargo Rover only";
        if (fitted) return $"{Name(u)} already fitted";
        int need = NeededFor(u, maxStack);
        if (held < need) return $"Needs {Cost(u, need)} in the other hand";
        return null;
    }

    /// <summary>Whether a storm tick may damage the vehicle: storm armour stops storm damage altogether (the user,
    /// 2026-10-09), and the StormDamage setting 0 means immune.</summary>
    public static bool Weathered(bool armour, float setting) => !armour && setting > 0f;

    /// <summary>The factor on the game's storm damage: the StormDamage setting, nothing with armour.</summary>
    public static float StormDamageMultiplier(bool armour, float setting) => armour ? 0f : setting;
    public static float WindFactor(bool fairings) => fairings ? 0.5f : 1f;

    /// <summary>Extra downforce (m/s^2) along the chassis down axis: GripAssist's |g| x bonus, or while the thrusters
    /// fire the tyre load of Venus (8.87 x (1 + bonus)), whichever is more.</summary>
    public static float GripDownforce(float g, float bonus, bool thrusting)
    {
        float a = System.Math.Abs(g), plain = a * bonus;
        return thrusting ? System.Math.Max(plain, VenusGravity * (1f + bonus) - a) : plain;
    }

    /// <summary>roverOn: the rover's main switch on and powered (the valves need power); held: locked kinematic, e.g.
    /// anchored by a deployed hab, where downforce would do nothing.</summary>
    public static bool ThrusterFiring(bool fitted, bool on, bool propellant, bool grounded, float g, float speed, bool driven,
                                      bool roverOn = true, bool held = false) =>
        fitted && on && roverOn && !held && propellant && grounded && System.Math.Abs(g) < VenusGravity && (speed > MinFireSpeed || driven);

    /// <summary>Propellant tank pressure drop (kPa/s) while firing: 1 MPa per 20 min for the rover alone, scaled by the
    /// mass pressed down, never faster than 1 MPa per 10 min.</summary>
    public static float BurnKPaPerSecond(float totalMass, float roverMass)
    {
        float ratio = roverMass > 0f && totalMass > roverMass ? totalMass / roverMass : 1f;
        float minutes = System.Math.Max(FloorMinutesPerMPa, AloneMinutesPerMPa / ratio);
        return 1000f / (minutes * 60f);
    }

    /// <summary>The burn rate for the hover text: minutes per MPa of tank pressure.</summary>
    public static float MinutesPerMPa(float totalMass, float roverMass) => 1000f / BurnKPaPerSecond(totalMass, roverMass) / 60f;

    /// <summary>What a wrench does: a material in the other hand for an upgrade not yet fitted fits it (even aimed at
    /// another upgrade's part, which can cover the body); otherwise a part under the wrench comes off; off the parts,
    /// a held material fits (or gives its refusal), else the wrench's usual job (None).</summary>
    public static WrenchAction PickAction(Upgrade? partUnder, Upgrade? held, bool heldFitted)
    {
        if (held != null && !heldFitted) return WrenchAction.Fit;
        if (partUnder != null) return WrenchAction.Remove;
        return held != null ? WrenchAction.Fit : WrenchAction.None;
    }

    public static float WasteLimitKPa(float? canisterMax) => canisterMax is float m && m > 0f ? m * 0.95f : DefaultWasteKPa;
}
