using System;
using System.Collections.Generic;
using System.Linq;

namespace Stationeers.RoverCargo;

/// <summary>Which page the rover's dash screen shows.</summary>
public enum RoverPage { Dark, Standby, Driving }

/// <summary>How a dash button looks: green on, grey off, dim "not fitted".</summary>
public enum ScreenButtonState { Off, On, NotFitted }

/// <summary>What the rover's dash screen is fed (CargoRover.DashInputs); null = no canister / not known.</summary>
public struct RoverInputs
{
    public bool On;                                   // the rover's power switch (OnOff)
    public bool Charged;                              // a fitted cell holds charge (on a client: its synced percentage or mode)
    public bool Remote;                               // a client: BatteryCell.PowerStored is server-side, so the charge is known
                                                      // to 1 % and power use not at all
    public double BatteryJ, BatteryMaxJ;              // every power cell together
    public int Cells;                                 // power cells fitted
    public double? DrawW;                             // power use; null while not known
    public double SpeedMs, MaxSpeedMs;
    public double PitchDeg, RollDeg;                  // + = nose up; + = right side down
    public double CabinKPa, CabinO2Pct, CabinC;
    public double? AirKPa, AirMaxKPa, WasteKPa, WasteMaxKPa;
    public bool HeadlightsOn, CabinLightOn, AirPumpOn, FilterOn, ThrustersFitted, ThrustersOn;
    public bool SideLightsOn, RearLightsOn;           // the roof rack's work lights (the original rover)
    public int FiltersFitted;
    public string Towing;                             // null, "HAB" or "TRAILER"
    public int? BaysFilled, BaysTotal;                // null on a rover without bay doors
    public int DoorsOpen;                             // bay doors open (the original rover)
}

public struct RoverButton { public string Label, Action; public ScreenButtonState State; public bool Active; }

/// <summary>What the rover's dash screen shows (RoverDashboard.Build).</summary>
public class RoverDash
{
    public RoverPage Page;
    public string Badge, TowTag, StandbyText, BatteryCells;
    public StatusLevel BadgeLevel;
    public Gauge Speed, Battery;
    public double PitchDeg, RollDeg;
    public StatusLevel PitchLevel, RollLevel;
    public string PitchText, RollText, PitchWords, RollWords;
    public Card[] Cards;
    public StatusLine[] Warnings;
    public RoverButton[] Buttons;
}

/// <summary>
/// The rover's touch dash screen (spec docs/superpowers/specs/2026-09-27-original-rover-cab-design.md; pure, tested
/// in _tools/HabTests): what each page shows, and where each control's tap target sits. The canvas is 1200 x 680
/// units at 0.5 mm (a 0.60 x 0.34 m face); the drawn buttons and the tap targets come from the same rectangles.
/// </summary>
public static class RoverDashboard
{
    public const float Width = 1200f, Height = 680f, Unit = 0.0005f;
    // starting values, tuned in game at the switch-over
    public const double TiltWarnDeg = 12, TiltFaultDeg = 25, BatteryWarnPct = 15, BatteryFaultPct = 5,
        CabinWarnKPa = 80, CabinFaultKPa = 50, CabinO2WarnPct = 16, AirWarnPct = 20, WasteWarnPct = 80, WasteFaultPct = 95,
        ParkedMs = 0.2;

    /// <summary>The eight controls in button order: label, interactable action (the keys saves and sync already use).</summary>
    public static readonly (string Label, string Action)[] Controls =
    {
        ("POWER", "OnOff"), ("HEADLIGHTS", "Button1"), ("SIDE LIGHTS", "Button13"), ("REAR LIGHTS", "Button14"),
        ("CABIN LIGHT", "Button2"), ("AIR PUMP", "Import"), ("FILTER", "Export"), ("THRUSTERS", "Button12"),
    };

    /// <summary>The big POWER button on the standby (and dark) page.</summary>
    public static readonly (float X, float Y, float W, float H) StandbyPower = (450f, 170f, 300f, 300f);

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    static double Pct(double? v, double? max) => v is double a && max is double m && m > 0 ? Clamp(a / m * 100, 0, 100) : 0;

    /// <summary>No charge: dark (on or off); off: standby; on: driving.</summary>
    public static RoverPage PageFor(bool on, bool charged) => !charged ? RoverPage.Dark : on ? RoverPage.Driving : RoverPage.Standby;

    public static StatusLevel TiltLevel(double deg) =>
        Math.Abs(deg) >= TiltFaultDeg ? StatusLevel.Fault : Math.Abs(deg) >= TiltWarnDeg ? StatusLevel.Warning : StatusLevel.Normal;

    /// <summary>The degree sign, or " deg" when the screen's font has no degree glyph.</summary>
    public static string Degrees(string text, bool fontHasDegree) => fontHasDegree ? text : text.Replace("°", " deg");

    /// <summary>The bottom row of the driving page: seven 132-wide buttons and a 146-wide eighth, 14 apart.</summary>
    public static (float X, float Y, float W, float H) DriveButton(int i) => (16f + i * 146f, 540f, i == 7 ? 146f : 132f, 124f);

    /// <summary>A control's tap rectangle on the canvas (x, y from the top-left, w, h), or null when it has no target on
    /// that page: standby and dark have only POWER (the centre button); THRUSTERS only when fitted.</summary>
    public static (float X, float Y, float W, float H)? TapRect(string action, RoverPage page, bool thrustersFitted)
    {
        if (page != RoverPage.Driving) return action == "OnOff" ? StandbyPower : null;
        int i = Array.FindIndex(Controls, c => c.Action == action);
        if (i < 0 || (action == "Button12" && !thrustersFitted)) return null;
        return DriveButton(i);
    }

    /// <summary>A canvas rectangle as a trigger box in the Screen anchor's frame (+z out of the face toward the seats;
    /// the canvas is turned 180 deg about y as on the hab screen, so canvas +x = face -x): 6 mm proud, 1 cm deep.</summary>
    public static ((float X, float Y, float Z) Center, (float X, float Y, float Z) Size) TapBox((float X, float Y, float W, float H) r) =>
        ((-(r.X + r.W / 2 - Width / 2) * Unit, (Height / 2 - (r.Y + r.H / 2)) * Unit, 0.006f), (r.W * Unit, r.H * Unit, 0.01f));

    /// <summary>A point in an anchor's frame (Unity Euler degrees: Z, then X, then Y) in rover-local space.</summary>
    public static (float X, float Y, float Z) AnchorPoint((float X, float Y, float Z) pos, (float X, float Y, float Z) rotDeg, (float X, float Y, float Z) local)
    {
        double rx = rotDeg.X * Math.PI / 180, ry = rotDeg.Y * Math.PI / 180, rz = rotDeg.Z * Math.PI / 180;
        double x = local.X, y = local.Y, z = local.Z;
        (x, y) = (x * Math.Cos(rz) - y * Math.Sin(rz), x * Math.Sin(rz) + y * Math.Cos(rz));
        (y, z) = (y * Math.Cos(rx) - z * Math.Sin(rx), y * Math.Sin(rx) + z * Math.Cos(rx));
        (x, z) = (x * Math.Cos(ry) + z * Math.Sin(ry), -x * Math.Sin(ry) + z * Math.Cos(ry));
        return ((float)(pos.X + x), (float)(pos.Y + y), (float)(pos.Z + z));
    }

    public static RoverDash Build(RoverInputs s)
    {
        var d = new RoverDash { Page = PageFor(s.On, s.Charged) };
        double batt = s.BatteryMaxJ > 0 ? Clamp(s.BatteryJ / s.BatteryMaxJ * 100, 0, 100) : 0;
        d.StandbyText = $"Rover off - battery {batt:0}%";
        (d.Badge, d.BadgeLevel) = s.SpeedMs < ParkedMs ? ("PARKED", StatusLevel.Off) : ("DRIVING", StatusLevel.Normal);
        d.TowTag = s.Towing == null ? null : "TOWING " + s.Towing;
        double kmh = s.SpeedMs * 3.6, maxKmh = Math.Max(1, s.MaxSpeedMs * 3.6);
        d.Speed = new Gauge { Label = "SPEED", ValueText = $"{kmh:0}", Sub = $"max {maxKmh:0} km/h", Value = Clamp(kmh, 0, maxKmh), Min = 0, Max = maxKmh, Level = StatusLevel.Normal };
        var battLevel = batt < BatteryFaultPct ? StatusLevel.Fault : batt < BatteryWarnPct ? StatusLevel.Warning : StatusLevel.Normal;
        d.Battery = new Gauge { Label = "BATTERY", ValueText = $"{batt:0}%", Sub = s.DrawW is double w ? $"using {w / 1000:0.0} kW" : s.Remote ? "" : "measuring",
                                Value = batt, Min = 0, Max = 100, Level = battLevel };
        d.BatteryCells = s.Cells == 1 ? "1 cell" : $"{s.Cells} cells";
        d.PitchDeg = s.PitchDeg;
        d.RollDeg = s.RollDeg;
        d.PitchLevel = TiltLevel(s.PitchDeg);
        d.RollLevel = TiltLevel(s.RollDeg);
        d.PitchText = Math.Abs(s.PitchDeg) < 0.5 ? "0°" : $"{(s.PitchDeg > 0 ? "+" : "-")}{Math.Abs(s.PitchDeg):0}°";
        d.RollText = $"{Math.Abs(s.RollDeg):0}°";
        d.PitchWords = s.PitchDeg >= 1 ? "nose up" : s.PitchDeg <= -1 ? "nose down" : "level";
        d.RollWords = s.RollDeg >= 1 ? "right side down" : s.RollDeg <= -1 ? "left side down" : "level";

        double airPct = Pct(s.AirKPa, s.AirMaxKPa), wastePct = Pct(s.WasteKPa, s.WasteMaxKPa);
        var cabinLevel = s.CabinKPa < CabinFaultKPa ? StatusLevel.Fault : s.CabinKPa < CabinWarnKPa ? StatusLevel.Warning : StatusLevel.Normal;
        var wasteLevel = wastePct > WasteFaultPct ? StatusLevel.Fault : wastePct > WasteWarnPct ? StatusLevel.Warning : StatusLevel.Normal;
        var cards = new List<Card>
        {
            new Card { Title = "CABIN", Lines = new[] { $"{s.CabinKPa:0} kPa", $"O2 {s.CabinO2Pct:0}% - {s.CabinC:0} C" },
                       LineLevels = new[] { cabinLevel, StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = Clamp(s.CabinKPa / 101.325, 0, 1), Level = cabinLevel } } },
            new Card { Title = "AIR TANK", Lines = new[] { s.AirKPa is double a ? $"{a / 1000:0.0} MPa" : "none", s.AirKPa == null ? "" : $"{airPct:0}% full" },
                       LineLevels = new[] { s.AirKPa == null ? StatusLevel.Fault : StatusLevel.Normal, StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = airPct / 100, Level = airPct < AirWarnPct ? StatusLevel.Warning : StatusLevel.Normal } } },
            new Card { Title = "WASTE", Lines = new[] { s.WasteKPa is double x ? $"{x / 1000:0.0} MPa" : "none", s.WasteKPa == null ? "" : $"{wastePct:0}% full" },
                       LineLevels = new[] { s.WasteKPa == null ? StatusLevel.Fault : StatusLevel.Normal, StatusLevel.Normal },
                       Bars = new[] { new Bar { Ratio = wastePct / 100, Level = wasteLevel } } },
        };
        if (s.BaysTotal is int total && total > 0)
            cards.Add(new Card { Title = "BAYS", Lines = new[] { $"{s.BaysFilled ?? 0} / {total} full", s.DoorsOpen == 0 ? "doors shut" : s.DoorsOpen == 1 ? "1 door open" : $"{s.DoorsOpen} doors open" },
                                 LineLevels = new[] { StatusLevel.Normal, StatusLevel.Normal },
                                 Bars = new[] { new Bar { Ratio = Clamp((double)(s.BaysFilled ?? 0) / total, 0, 1), Level = StatusLevel.Normal } } });
        d.Cards = cards.ToArray();

        var warn = new List<StatusLine>();
        void W(bool when, string text, StatusLevel l) { if (when) warn.Add(new StatusLine { Text = text, Level = l }); }
        bool rollMore = Math.Abs(s.RollDeg) >= Math.Abs(s.PitchDeg);
        double tilt = rollMore ? Math.Abs(s.RollDeg) : Math.Abs(s.PitchDeg);
        W(tilt >= TiltWarnDeg, $"{(rollMore ? "Roll" : "Pitch")} {tilt:0}°", TiltLevel(tilt));
        W(batt < BatteryWarnPct, "Battery low", battLevel);
        W(s.On && s.CabinKPa < CabinWarnKPa, "Cabin pressure low", cabinLevel);
        W(s.CabinKPa >= CabinFaultKPa && s.CabinO2Pct < CabinO2WarnPct, "Cabin O2 low", StatusLevel.Warning);
        W(s.AirPumpOn && s.AirKPa == null, "No air canister", StatusLevel.Fault);
        W(s.AirPumpOn && s.AirKPa is double empty && empty <= 0, "Air tank empty", StatusLevel.Fault);
        W(s.AirPumpOn && s.AirKPa is double some && some > 0 && airPct < AirWarnPct, "Air tank low", StatusLevel.Warning);
        W(s.WasteKPa != null && wastePct > WasteWarnPct, wastePct > WasteFaultPct ? "Waste full" : $"Waste {wastePct:0}% full", wasteLevel);
        W(s.FilterOn && s.FiltersFitted == 0, "No filter fitted", StatusLevel.Warning);
        d.Warnings = warn.Where(l => l.Level == StatusLevel.Fault).Concat(warn.Where(l => l.Level != StatusLevel.Fault)).ToArray();

        d.Buttons = new RoverButton[Controls.Length];
        for (int i = 0; i < Controls.Length; i++)
        {
            var (label, action) = Controls[i];
            bool on = action switch
            {
                "OnOff" => s.On, "Button1" => s.HeadlightsOn, "Button13" => s.SideLightsOn, "Button14" => s.RearLightsOn,
                "Button2" => s.CabinLightOn, "Import" => s.AirPumpOn, "Export" => s.FilterOn, _ => s.ThrustersOn,
            };
            bool fitted = action != "Button12" || s.ThrustersFitted;
            d.Buttons[i] = new RoverButton
            {
                Label = label, Action = action,
                State = !fitted ? ScreenButtonState.NotFitted : on ? ScreenButtonState.On : ScreenButtonState.Off,
                Active = TapRect(action, d.Page, s.ThrustersFitted) != null,
            };
        }
        return d;
    }
}
