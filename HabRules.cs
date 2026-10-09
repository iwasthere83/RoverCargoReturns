namespace Stationeers.RoverCargo;

/// <summary>The hab's power users (MadelynPlays, 0.2.3: "1.6 kW nearly constantly").</summary>
public enum PowerUse { Heater, Cooler, Airlock, Charger, SuitStation, Shower, Filler, Lights, Chip }

/// <summary>Joules each system took from the hab batteries, per atmos tick (the game's "W"), over a window of ticks.</summary>
public sealed class PowerLedger
{
    private readonly float[] _sum = new float[System.Enum.GetValues(typeof(PowerUse)).Length];
    public int Ticks { get; private set; }

    public void Add(PowerUse use, float joules) { if (joules > 0f) _sum[(int)use] += joules; }
    public void EndTick() => Ticks++;
    public void Reset() { System.Array.Clear(_sum, 0, _sum.Length); Ticks = 0; }

    public System.Collections.Generic.Dictionary<PowerUse, float> PerTick()
    {
        var d = new System.Collections.Generic.Dictionary<PowerUse, float>();
        foreach (PowerUse u in System.Enum.GetValues(typeof(PowerUse))) d[u] = Ticks > 0 ? _sum[(int)u] / Ticks : 0f;
        return d;
    }

    public float TotalPerTick()
    {
        float t = 0f;
        foreach (var s in _sum) t += s;
        return Ticks > 0 ? t / Ticks : 0f;
    }

    /// <summary>"draw 450 W: charger 250, heater 200": the systems that drew, largest first.</summary>
    public string Report()
    {
        var parts = new System.Collections.Generic.List<(float w, string name)>();
        foreach (var kv in PerTick()) if (kv.Value > 0.05f) parts.Add((kv.Value, kv.Key.ToString().ToLowerInvariant()));
        parts.Sort((a, b) => b.w.CompareTo(a.w));
        return $"draw {TotalPerTick():0} W: " + (parts.Count == 0 ? "nothing" : string.Join(", ", parts.ConvertAll(p => $"{p.name} {p.w:0}")));
    }
}

/// <summary>What the console status page shows (filled from the hab once a second; null = no tank/canister).</summary>
public struct StatusInputs
{
    public bool Deployed, BatteryFlat, Sealed;
    public string DoorPhase;
    public double RoomKPa, RoomC, BatteryPct, SolarW;
    public double? SupplyKPa, WasteKPa, WasteMaxKPa, CleanL, WasteWaterL, WasteWaterMaxL;
    public string Charger, Suit;
    public bool ShowerOn, LockerOpen, LightsOn;
    public int LockerItems, LockerSlots;
    // dashboard extras
    public double BatteryJ, BatteryMaxJ, PumpProgress;
    public double? DrawW;                                         // null: not known (full batteries, cells swapped, starting)   // PumpProgress 0..1 while the door cycles
    public double? SupplyMaxKPa, CleanMaxL;
    public double[] Cells;                                        // each power cell's charge ratio
    public string ChipState;
    public string Thrust;                                         // towing rover's thrusters: none/off/on/firing/no propellant
}

public struct Gauge { public string Label, ValueText, Sub; public double Value, Min, Max; public StatusLevel Level; }
public struct Bar { public double Ratio; public StatusLevel Level; }
public struct Card { public string Title; public string[] Lines; public StatusLevel[] LineLevels; public Bar[] Bars; }
public struct Tag { public string Name; public bool On; }

/// <summary>What the dashboard shows (HabRules.Dashboard), in the reactor display's layout.</summary>
public class DashModel
{
    public string Badge; public StatusLevel BadgeLevel; public bool Dim, Dark;   // Dim: stowed; Dark: battery flat, screen off
    public Gauge[] Gauges; public Card[] Cards; public Tag[] Tags; public StatusLine[] Warnings;
    public double[] Cells; public string BatteryText, Footer;
}

/// <summary>A fixed-size history (oldest dropped) of a few series, for the dashboard's 10-minute trends.</summary>
public class Trend
{
    private readonly double[,] _v; private int _start;
    public int Capacity { get; } public int Series { get; } public int Count { get; private set; }
    public Trend(int capacity, int series) { Capacity = capacity; Series = series; _v = new double[capacity, series]; }
    public void Add(params double[] values)
    {
        int i = (_start + Count) % Capacity;
        if (Count == Capacity) { i = _start; _start = (_start + 1) % Capacity; } else Count++;
        for (int k = 0; k < Series; k++) _v[i, k] = k < values.Length ? values[k] : 0;
    }
    /// <summary>Sample i (0 = oldest) of series k.</summary>
    public double Get(int i, int k) => _v[(_start + i) % Capacity, k];
    public double Min(int k) { double m = double.MaxValue; for (int i = 0; i < Count; i++) m = System.Math.Min(m, Get(i, k)); return Count == 0 ? 0 : m; }
    public double Max(int k) { double m = double.MinValue; for (int i = 0; i < Count; i++) m = System.Math.Max(m, Get(i, k)); return Count == 0 ? 0 : m; }
}

public enum StatusLevel { Normal, Warning, Fault, Off }

public struct StatusLine { public string Text; public StatusLevel Level; }

/// <summary>Pure rules for the hab's working furniture (no Unity types; tested by _tools/HabTests).</summary>
public static class HabRules
{
    public const double TankMaxKPa = 10132.5, CanisterL = 12.4, SolarGaugeW = 1000;

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>The dashboard (reactor-display style): state badge, 4 gauges with bands, 5 cards, system tags, warnings,
    /// battery cells. Bands: room 80-120 kPa (fault under 30 or over 150), temperature 18-26 C (warning 0-40, else
    /// fault), battery warning under 40 %, fault under 20 %.</summary>
    /// <summary>The hab's draw in the game's "W" (joules per atmos tick, as SolarWatts) from the battery change over a
    /// sample: draw = solar - stored per tick, at least 0. Null when it cannot be told: batteries full (solar is then
    /// not stored, so the change says nothing), the power cells changed, or no interval.</summary>
    public static double? DrawEstimate(double solarPerTick, double dJ, double seconds, double tickSeconds, bool full, bool cellsChanged)
    {
        if (full || cellsChanged || seconds <= 0 || tickSeconds <= 0) return null;
        return System.Math.Max(0, solarPerTick - dJ / (seconds / tickSeconds));
    }

    public static DashModel Dashboard(StatusInputs s)
    {
        var d = new DashModel();
        bool cycling = s.DoorPhase is "PumpDown" or "Opening" or "Closing" or "Venting" or "Restoring";
        (d.Badge, d.BadgeLevel) = !s.Deployed ? ("STOWED", StatusLevel.Off)            // a flat battery turns the screen off (Dark)
            : cycling ? ("CYCLING", StatusLevel.Warning) : s.DoorPhase == "Open" ? ("OPEN", StatusLevel.Fault) : ("SEALED", StatusLevel.Normal);
        d.Dim = !s.Deployed;
        d.Dark = s.BatteryFlat;
        StatusLevel Band(double v, double g0, double g1, double w0, double w1) =>
            v >= g0 && v <= g1 ? StatusLevel.Normal : v >= w0 && v <= w1 ? StatusLevel.Warning : StatusLevel.Fault;
        string net = s.DrawW is double dw ? $"{(s.SolarW - dw >= 0 ? "+" : "")}{s.SolarW - dw:0} W net"
            : s.BatteryPct >= 99.5 ? "batteries full" : "measuring";
        d.Gauges = new[]
        {
            new Gauge { Label = "ROOM", ValueText = s.RoomKPa >= 1000 ? $"{s.RoomKPa:0} kPa" : $"{s.RoomKPa:0.0} kPa", Sub = s.Sealed ? "sealed" : "open to outside",
                        Value = Clamp(s.RoomKPa, 0, 150), Min = 0, Max = 150, Level = Band(s.RoomKPa, 80, 120, 30, 150) },
            new Gauge { Label = "TEMP", ValueText = $"{s.RoomC:0.0} C", Sub = "room air",
                        Value = Clamp(s.RoomC, -20, 50), Min = -20, Max = 50, Level = Band(s.RoomC, 18, 26, 0, 40) },
            new Gauge { Label = "BATTERY", ValueText = $"{s.BatteryPct:0} %", Sub = net,
                        Value = Clamp(s.BatteryPct, 0, 100), Min = 0, Max = 100,
                        Level = s.BatteryPct < 20 ? StatusLevel.Fault : s.BatteryPct < 40 ? StatusLevel.Warning : StatusLevel.Normal },
            new Gauge { Label = "SOLAR", ValueText = $"{s.SolarW:0} W", Sub = s.SolarW <= 0 ? "no sun" : s.BatteryPct >= 99.5 ? "batteries full" : "charging",
                        Value = Clamp(s.SolarW, 0, SolarGaugeW), Min = 0, Max = SolarGaugeW, Level = StatusLevel.Normal },
        };
        double Max(double? v, double fallback) => v is double m && m > 0 ? m : fallback;   // unknown or 0: the stock size
        double supMax = Max(s.SupplyMaxKPa, TankMaxKPa), wstMax = Max(s.WasteMaxKPa, TankMaxKPa);
        double cleanMax = Max(s.CleanMaxL, CanisterL), wwMax = Max(s.WasteWaterMaxL, CanisterL);
        StatusLevel SupLevel() => s.SupplyKPa is double a2 ? (a2 < 1000 ? StatusLevel.Warning : StatusLevel.Normal) : StatusLevel.Fault;
        StatusLevel WstLevel() => s.WasteKPa is double w2 ? (w2 >= wstMax * 0.9 ? StatusLevel.Warning : StatusLevel.Normal) : StatusLevel.Fault;
        d.Cards = new[]
        {
            new Card { Title = "AIR SUPPLY", Lines = new[] { s.SupplyKPa is double a ? $"{a:0} kPa" : "none" },
                       LineLevels = new[] { s.SupplyKPa == null ? StatusLevel.Fault : StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = Clamp((s.SupplyKPa ?? 0) / supMax, 0, 1), Level = SupLevel() } } },
            new Card { Title = "WASTE", Lines = new[] { s.WasteKPa is double w ? $"{w:0} kPa" : "none" },
                       LineLevels = new[] { s.WasteKPa == null ? StatusLevel.Fault : StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = Clamp((s.WasteKPa ?? 0) / wstMax, 0, 1), Level = WstLevel() } } },
            new Card { Title = "WATER", Lines = new[] { s.CleanL is double c ? $"CLEAN {c:0.0} L" : "CLEAN none", s.WasteWaterL is double x ? $"WASTE {x:0.0} L" : "WASTE none" },
                       LineLevels = new[] { s.CleanL == null ? StatusLevel.Warning : StatusLevel.Normal, s.WasteWaterL == null ? StatusLevel.Warning : StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = Clamp((s.CleanL ?? 0) / cleanMax, 0, 1), Level = s.CleanL is double c2 && c2 >= 2 ? StatusLevel.Normal : StatusLevel.Warning },
                                      new Bar { Ratio = Clamp((s.WasteWaterL ?? 0) / wwMax, 0, 1), Level = (s.WasteWaterL ?? 0) >= wwMax * 0.9 ? StatusLevel.Warning : StatusLevel.Normal } } },
            new Card { Title = "DOOR", Lines = new[] { s.DoorPhase ?? "-" }, LineLevels = new[] { StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = Clamp(s.PumpProgress, 0, 1), Level = cycling ? StatusLevel.Warning : StatusLevel.Normal } } },
            new Card { Title = "SYSTEMS", Lines = new string[0], LineLevels = new StatusLevel[0], Bars = new Bar[0] },
        };
        d.Tags = new[]
        {
            new Tag { Name = "CHARGER", On = (s.Charger ?? "").Contains("charging") },
            new Tag { Name = "SUIT", On = (s.Suit ?? "").StartsWith("working") },
            new Tag { Name = "SHOWER", On = s.ShowerOn },
            new Tag { Name = "LIGHTS", On = s.LightsOn },
            new Tag { Name = "THRUST", On = s.Thrust is "firing" or "on" },
        };
        var warn = new System.Collections.Generic.List<StatusLine>();
        void W(bool when, string text, StatusLevel l) { if (when) warn.Add(new StatusLine { Text = text, Level = l }); }
        W(s.BatteryPct < 20, "Battery low", StatusLevel.Warning);
        W(s.SupplyKPa == null, "No AIR SUPPLY tank", StatusLevel.Fault);
        W(s.SupplyKPa is double sa && sa < 1000, "AIR SUPPLY low", StatusLevel.Warning);
        W(s.WasteKPa == null, "No WASTE tank", StatusLevel.Fault);
        W(s.WasteKPa is double sw && sw >= wstMax * 0.9, "WASTE nearly full", StatusLevel.Warning);
        W(s.CleanL == null, "No CLEAN WATER canister", StatusLevel.Warning);
        W(s.CleanL is double sc && sc < 2, "CLEAN WATER low", StatusLevel.Warning);
        W(s.WasteWaterL == null, "No WASTE WATER canister", StatusLevel.Warning);
        W(s.WasteWaterL is double sww && sww >= wwMax * 0.9, "WASTE WATER nearly full", StatusLevel.Warning);
        W(s.Sealed && s.Deployed && s.RoomKPa < 30, "Room pressure low", StatusLevel.Fault);
        d.Warnings = warn.ToArray();
        d.Cells = s.Cells ?? new double[0];
        d.BatteryText = $"{s.BatteryJ / 1000:0.0} / {s.BatteryMaxJ / 1000:0.0} kJ";
        d.Footer = $"Locker {(s.LockerOpen ? "open" : "closed")} {s.LockerItems}/{s.LockerSlots}   Chip {s.ChipState ?? "none"}";
        return d;
    }
    // the dashboard canvas (HabStatusScreen): 1470 x 1000 units at 0.68 mm, the 1.00 x 0.68 m screen glass
    public const float DashWidth = 1470f, DashHeight = 1000f, DashUnit = 0.00068f;

    /// <summary>Bifold locker leaves: the hinged leaf swings out to 90 deg, the inner leaf folds back flat (-180 deg
    /// relative to it), so an open door stands 0.35 m out of the locker face.</summary>
    public static (float leafA, float leafB) LockerLeafAngles(float open01)
    {
        float t = open01 < 0f ? 0f : open01 > 1f ? 1f : open01;
        return (90f * t, -180f * t);
    }

    /// <summary>Joules into one cell this tick: what it needs, what the source has, the charger's rate.</summary>
    public static float ChargeStep(float cellDelta, float source, float rate)
    {
        float j = System.Math.Min(cellDelta, System.Math.Min(source, rate));
        return j > 0f ? j : 0f;
    }

    /// <summary>Share one budget in order (vanilla suit storage: suit, helmet, back).</summary>
    public static float[] SplitBudget(float budget, params float[] deltas)
    {
        var got = new float[deltas.Length];
        for (int i = 0; i < deltas.Length && budget > 0f; i++)
        {
            got[i] = System.Math.Max(0f, System.Math.Min(deltas[i], budget));
            budget -= got[i];
        }
        return got;
    }

    /// <summary>The suit station works only on a deployed hab with charge, and stops emptying at 95 % WASTE.</summary>
    public static bool StationGate(bool deployed, bool batteryHasCharge, float wastePressure, float wasteMax) =>
        deployed && batteryHasCharge && wastePressure < wasteMax * 0.95f;

    /// <summary>Why the door may not move now (hand or chip), or null. The battery never keeps it shut: with no charge
    /// the pump-down vents the room outside instead of pumping it into AIR SUPPLY (a flat hab must never lock anyone
    /// out of the batteries inside it, or in).</summary>
    public static string DoorRefusal(bool deployed, bool cycling) =>
        !deployed ? "Deploy the hab first" : cycling ? "Door is cycling" : null;

    /// <summary>Index of the first exit candidate that is free and inside the room, or -1.</summary>
    public static int FirstFreeExit(bool[] blocked, bool[] inside)
    {
        for (int i = 0; i < blocked.Length && i < inside.Length; i++)
            if (!blocked[i] && inside[i]) return i;
        return -1;
    }

    /// <summary>Charger line; aboveReserve = the hab has charge to spare past its 20 % reserve (else charging waits).</summary>
    public static string ChargerStatus(int cellsNeedingCharge, bool running, bool aboveReserve) =>
        !running || cellsNeedingCharge <= 0 ? "Charger: idle"
        : !aboveReserve ? "Charger: waiting (hab battery at its 20 % reserve)"
        : $"Charger: charging {cellsNeedingCharge} cell{(cellsNeedingCharge > 1 ? "s" : "")}";

    public static string SuitStatus(bool suitPresent, bool running, bool aboveReserve) =>
        !suitPresent ? "empty" : !running ? "idle" : aboveReserve ? "working" : "working, not charging (hab battery at its 20 % reserve)";

    /// <summary>The game's WaterDevice refusals, with the hab's canisters for pipes: deployed and sealed, a CLEAN WATER
    /// canister with enough liquid at 0-100 C and unpolluted, a WASTE WATER canister with 1 L free, room >= 30 kPa.</summary>
    public static string WaterRefusal(bool running, bool hasClean, double cleanLiquidMoles, double needMoles, double cleanTempK,
        bool polluted, bool hasWaste, double wasteLiquidLitres, double wasteVolumeLitres, double roomKPa)
    {
        if (!running) return "Deploy the hab first";
        if (roomKPa < 30.0) return "Room pressure too low";
        if (!hasClean) return "No CLEAN WATER canister";
        if (cleanLiquidMoles < needMoles) return "Not enough water";
        if (cleanTempK < 273.15) return "Water too cold";
        if (cleanTempK > 373.15) return "Water too hot";
        if (polluted) return "Water polluted";
        if (!hasWaste) return "No WASTE WATER canister";
        if (wasteLiquidLitres > wasteVolumeLitres - 1.0) return "WASTE WATER full";
        return null;
    }

    /// <summary>Vanilla WaterPolluted: any toxin, or any liquid that is not water.</summary>
    public static bool Polluted(double toxinMoles, double liquidMoles, double waterMoles) => toxinMoles > 0 || liquidMoles > waterMoles;

    /// <summary>Does this much liquid fit in WASTE WATER, keeping 0.25 L spare? A canister is small: the game's "1 L free"
    /// test was made for pipe networks, and an overfilled canister bursts.</summary>
    public static bool WasteRoomFor(double freeLitres, double addMoles) => freeLitres >= addMoles / 55.555557 + 0.25;

    /// <summary>What the shower says to you (the game's CanClean): stand in it, without suit and jumpsuit (robots may keep
    /// theirs on).</summary>
    public static string ShowerCleanMessage(bool inStall, bool wearingSuit, bool wearingUniform, bool robot) =>
        !inStall ? "Stand in the shower to wash" : wearingSuit ? "Take off your suit to wash"
        : wearingUniform && !robot ? "Take off your jumpsuit to wash" : "Will wash you";

    public static float ShowerHygiene(float hygiene) => System.Math.Min(hygiene + 0.05f, 1.5f);

    /// <summary>The game's toilet check, in its order: a need past 25 %, standing at it, no suit.</summary>
    public static string ToiletRefusal(float sanitationRatio, bool inPlace, bool wearingSuit) =>
        sanitationRatio <= 0.25f ? "No need yet" : !inPlace ? "Stand at the toilet" : wearingSuit ? "Take the suit off first" : null;

    /// <summary>Charging others (charger cells, the suit) stops at 20 % hab charge: the lights, pumps and door keep it.</summary>
    public static bool AboveReserve(double stored, double max) => max > 0 && stored > max * 0.2;

    /// <summary>Vanilla bottle filler: 55.56 mol per missing litre, at most 5.56 mol a tick, never more than there is.</summary>
    public static double FillerMoles(float bottleMissingLitres, double waterMoles) =>
        System.Math.Max(0.0, System.Math.Min(System.Math.Min(bottleMissingLitres * 55.555557, waterMoles), 5.56));

    /// <summary>The game's SolarPanel.PowerGenerated: I = irradiance x area x solar ratio, softly capped near 500 W
    /// (log base 1.4, or 1.6 in a storm at that height).</summary>
    public static double SolarPower(double irradiance, double area, double solarRatio, bool stormHere)
    {
        double I = irradiance * area * solarRatio;
        if (I <= 0) return 0;
        double k = stormHere ? 1.6 : 1.4;
        return I / System.Math.Log(System.Math.Max(k, I / 500.0), k);
    }

    /// <summary>Flat roof panel: the sun angle (Dot(up, sun)), nothing below the plane or in an eclipse, times the
    /// unobscured share.</summary>
    public static double SolarShare(double dotUpSun, bool eclipse, double unobscured) =>
        dotUpSun <= 0 || eclipse ? 0 : dotUpSun * System.Math.Max(0, System.Math.Min(1, unobscured));

    /// <summary>Triangle list minus the triangles whose three vertices all lie inside [min, max].</summary>
    public static int[] TrimTriangles(float[] xyz, int[] tris, float[] min, float[] max)
    {
        bool In(int v)
        {
            for (int k = 0; k < 3; k++)
            {
                float c = xyz[v * 3 + k];
                if (c < min[k] || c > max[k]) return false;
            }
            return true;
        }
        var kept = new System.Collections.Generic.List<int>(tris.Length);
        for (int i = 0; i + 2 < tris.Length; i += 3)
            if (!(In(tris[i]) && In(tris[i + 1]) && In(tris[i + 2]))) { kept.Add(tris[i]); kept.Add(tris[i + 1]); kept.Add(tris[i + 2]); }
        return kept.ToArray();
    }

    /// <summary>A trailer's wheel torques. Unhitched: full parking brake at zero drive, so PhysX's standstill "sticky tyre"
    /// pins it. Hitched: never zero drive (a negligible roll torque) and braked no harder than the towing rover's own
    /// wheels (towBrake, its brake torque now), capped at the parking brake. A sticky or hard-braked trailer locked to the
    /// rover through the hitch jams in whatever pose it stopped: the parked hab leaned 12 deg (sticky), then, braked at 200,
    /// propped the rover's rear 0.4 m up at the hitch - both had to roll apart to settle, and the gas released it.</summary>
    public static (float motor, float brake) TrailerWheelTorque(bool hitched, bool towDriving, float towBrake, float parkingBrake, float rollTorque)
    {
        if (!hitched) return (0f, parkingBrake);
        return (rollTorque, towDriving ? 0f : System.Math.Max(0f, System.Math.Min(parkingBrake, towBrake)));
    }

    /// <summary>A parked, hitched hab is held still (kinematic) once the rig has stood still ParkHoldSeconds: two free
    /// bodies locked through the hitch on 12 gripping tyres grew a sideways hitch force from ~30 N to 1.5 kN by themselves
    /// and tipped the rover onto one side. Released at once by driving (the gas, or motor torque on the rover's wheels),
    /// or by the rover being shoved past ParkReleaseSpeed. Not by the hitch force: it read 700-1500 N the moment the hold
    /// took, and releasing on it made the hold flicker off every time.</summary>
    public const float ParkHoldSeconds = 1f, ParkReleaseSpeed = 0.2f, ParkStillSpeed = 0.05f;
    public static bool ParkHold(bool hitched, bool towDriving, float roverSpeed, float stillSeconds, bool holding)
    {
        if (!hitched || towDriving) return false;
        if (holding) return roverSpeed < ParkReleaseSpeed;
        return stillSeconds >= ParkHoldSeconds;
    }

    /// <summary>What a trailer freezes (kinematic). A hitched rig left free while parked is two bodies locked through the
    /// hitch on gripping tyres that push on each other (the rover tilts, players slide), so: parked = trailer and rover
    /// frozen together, the gas frees both (ParkHold). A deployed hab stands on its legs and freezes its rover too: left
    /// free, the rover leaned on the fixed hab. An unhitched trailer is free (its parking brake holds it).</summary>
    public static (bool trailer, bool rover) RigHold(bool hitched, bool deployed, bool parkHeld)
    {
        bool trailer = deployed || (hitched && parkHeld);
        return (trailer, hitched && trailer);
    }
}
