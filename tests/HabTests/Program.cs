using Newtonsoft.Json.Linq;
using Stationeers.RoverCargo;

var checks = 0;
void Check(bool c, string label) { if (!c) throw new Exception("FAIL: " + label); checks++; }
bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;

// locker: closed flat, open = outer leaf 90 deg out, inner leaf folded back flat against it
var (a0, b0) = HabRules.LockerLeafAngles(0f); Check(Near(a0, 0) && Near(b0, 0), "locker closed");
var (a1, b1) = HabRules.LockerLeafAngles(1f); Check(Near(a1, 90) && Near(b1, -180), "locker open folds");
var (ah, bh) = HabRules.LockerLeafAngles(0.5f); Check(Near(ah, 45) && Near(bh, -90), "locker half");
Check(HabRules.LockerLeafAngles(2f).leafA <= 90f && HabRules.LockerLeafAngles(-1f).leafA >= 0f, "locker clamps");

// charger: never negative, never more than the cell needs, the source has or the rate allows
Check(Near(HabRules.ChargeStep(1000f, 50000f, 500f), 500f), "charger rate cap");
Check(Near(HabRules.ChargeStep(120f, 50000f, 500f), 120f), "charger tops off");
Check(Near(HabRules.ChargeStep(1000f, 30f, 500f), 30f), "charger source cap");
Check(HabRules.ChargeStep(-5f, 50000f, 500f) == 0f && HabRules.ChargeStep(100f, -1f, 500f) == 0f, "charger never negative");

// suit station battery budget: suit first, then helmet, then back; total <= budget
var s = HabRules.SplitBudget(1000f, 800f, 400f, 300f);
Check(Near(s[0], 800) && Near(s[1], 200) && Near(s[2], 0), "budget priority");
Check(HabRules.SplitBudget(1000f, 0f, -3f, 50f)[1] == 0f, "budget ignores negative delta");

// suit station waste gate
Check(HabRules.StationGate(true, true, 100f, 4053f), "gate open");
Check(!HabRules.StationGate(false, true, 100f, 4053f), "gate stowed");
Check(!HabRules.StationGate(true, false, 100f, 4053f), "gate no battery");
Check(!HabRules.StationGate(true, true, 3851f, 4053f), "gate waste 95 %");

// mesh trim: a triangle wholly inside the box goes, one straddling it stays
float[] xyz = { 0,0,0, 1,0,0, 0,1,0,   5,5,5, 6,5,5, 5,6,5 };
int[] tris = { 0,1,2, 3,4,5, 0,1,3 };
var kept = HabRules.TrimTriangles(xyz, tris, new float[] { -1,-1,-1 }, new float[] { 2,2,2 });
Check(kept.Length == 6 && kept[0] == 3 && kept[3] == 0, "trim keeps outside and straddling");

// door: a flat battery never keeps the door shut (the pump-down vents instead) - review finding 1
Check(HabRules.DoorRefusal(true, false) == null, "door opens when deployed and idle, whatever the battery");
Check(HabRules.DoorRefusal(false, false) == "Deploy the hab first", "door refused when stowed");
Check(HabRules.DoorRefusal(true, true) == "Door is cycling", "door refused while cycling");

// bunk exit fallback: first candidate that is free AND inside the room, else none - review finding 3
Check(HabRules.FirstFreeExit(new[] { true, false, false }, new[] { true, false, true }) == 2, "exit skips outside-hull candidate");
Check(HabRules.FirstFreeExit(new[] { true, true }, new[] { true, true }) == -1, "exit none free");

// status lines from state every client has - review finding 4
Check(HabRules.ChargerStatus(2, true, true) == "Charger: charging 2 cells" && HabRules.ChargerStatus(1, true, true) == "Charger: charging 1 cell", "charger status");
Check(HabRules.ChargerStatus(1, false, true) == "Charger: idle" && HabRules.ChargerStatus(0, true, true) == "Charger: idle", "charger idle");
Check(HabRules.ChargerStatus(1, true, false) == "Charger: waiting (hab battery at its 20 % reserve)", "charger held by the reserve");
Check(HabRules.SuitStatus(true, true, true) == "working" && HabRules.SuitStatus(true, false, true) == "idle" && HabRules.SuitStatus(false, true, true) == "empty", "suit status");
Check(HabRules.SuitStatus(true, true, false) == "working, not charging (hab battery at its 20 % reserve)", "suit held by the reserve");

// water (vanilla WaterDevice / StructureShower / StructureToilet / WaterBottleFiller rules)
string W(bool run = true, bool clean = true, double moles = 500, double need = 5, double tK = 293.15, bool pol = false,
         bool waste = true, double wL = 1, double wV = 10, double kPa = 101) =>
    HabRules.WaterRefusal(run, clean, moles, need, tK, pol, waste, wL, wV, kPa);
Check(W() == null, "water ok");
Check(W(run: false) == "Deploy the hab first", "water stowed");
Check(W(clean: false) == "No CLEAN WATER canister", "no clean canister");
Check(W(moles: 4) == "Not enough water", "not enough water");
Check(W(tK: 272.0) == "Water too cold" && W(tK: 374.0) == "Water too hot", "water temperature");
Check(W(pol: true) == "Water polluted", "water polluted");
Check(W(waste: false) == "No WASTE WATER canister" && W(wL: 9.5) == "WASTE WATER full", "waste missing/full");
Check(W(kPa: 25) == "Room pressure too low", "room pressure");
Check(HabRules.Polluted(0, 10, 10) == false && HabRules.Polluted(0.1, 10, 10) && HabRules.Polluted(0, 10, 9), "polluted rule");
Check(Near(HabRules.ShowerHygiene(0.25f), 0.30f) && Near(HabRules.ShowerHygiene(1.48f), 1.5f), "shower hygiene");
// toilet, in the game's order: no need, then not in position, then suit on
Check(HabRules.ToiletRefusal(0.5f, true, false) == null, "toilet ok");
Check(HabRules.ToiletRefusal(0.2f, false, true) == "No need yet", "toilet need first");
Check(HabRules.ToiletRefusal(0.5f, false, true) == "Stand at the toilet", "toilet position before suit");
Check(HabRules.ToiletRefusal(0.5f, true, true) == "Take the suit off first", "toilet suit");
Check(Math.Abs(HabRules.FillerMoles(0.5f, 100) - 5.56) < 1e-6 && Math.Abs(HabRules.FillerMoles(0.01f, 100) - 0.5555556) < 1e-4
      && HabRules.FillerMoles(0.5f, 2) == 2 && HabRules.FillerMoles(0f, 100) == 0, "filler rate");

// review: a flush must fit in WASTE WATER (50 mol + the stomach's polluted water, 55.56 mol per litre, 0.25 L spare)
Check(HabRules.WasteRoomFor(2.0, 50) && !HabRules.WasteRoomFor(1.0, 50 + 20) && !HabRules.WasteRoomFor(0.9, 50), "flush fits waste");
// review: the shower says why it will not wash you (vanilla CanClean messages)
Check(HabRules.ShowerCleanMessage(true, false, false, false) == "Will wash you", "shower will clean");
Check(HabRules.ShowerCleanMessage(false, false, false, false) == "Stand in the shower to wash", "shower not in position");
Check(HabRules.ShowerCleanMessage(true, true, false, false) == "Take off your suit to wash", "shower suit");
Check(HabRules.ShowerCleanMessage(true, false, true, false) == "Take off your jumpsuit to wash" && HabRules.ShowerCleanMessage(true, false, true, true) == "Will wash you", "shower uniform / robot");

// the charger and suit station leave a 20 % reserve in the hab batteries (lights, pumps, the door)
Check(HabRules.AboveReserve(30000, 100000) && !HabRules.AboveReserve(20000, 100000) && !HabRules.AboveReserve(5, 0), "battery reserve");

// roof solar: the game's SolarPanel formula; flat panels take the sun angle as Dot(up, sun)
double P(double I, double a = 2.9, double r = 1, bool storm = false) => HabRules.SolarPower(I, a, r, storm);
Check(Math.Abs(P(100) - 290 / Math.Log(Math.Max(1.4, 290 / 500.0), 1.4)) < 1e-6 && P(100) > 280, "solar below the cap ~ linear");
Check(P(1000) < 2900 && P(1000) > 500, "solar soft cap above 500 W");
// the game's storm factor (log base 1.6) loosens the cap; storms cut power through the solar ratio instead
Check(Math.Abs(P(1000, storm: true) - 2900 / (Math.Log(5.8) / Math.Log(1.6))) < 1e-6 && P(1000, r: 0.3) < P(1000), "solar storm factor and ratio as the game");
Check(P(0) == 0 && P(100, r: 0) == 0, "no sun, no power");
Check(HabRules.SolarShare(-0.1, false, 1) == 0 && HabRules.SolarShare(0, false, 1) == 0, "sun below the roof plane");
Check(HabRules.SolarShare(0.8, true, 1) == 0 && HabRules.SolarShare(0.8, false, 0) == 0, "eclipse or shade");
Check(Math.Abs(HabRules.SolarShare(0.5, false, 1) - 0.5) < 1e-9 && Math.Abs(HabRules.SolarShare(1, false, 1) - 1) < 1e-9, "sun angle");

StatusInputs Ok() => new StatusInputs { Deployed = true, Sealed = true, DoorPhase = "Closed", RoomKPa = 101.3, RoomC = 20.1, BatteryPct = 98,
    SolarW = 412, SupplyKPa = 6060, WasteKPa = 75, WasteMaxKPa = 10132.5, CleanL = 8.2, WasteWaterL = 1.1, WasteWaterMaxL = 12.4,
    Charger = "Charger: idle", Suit = "empty", LockerItems = 4, LockerSlots = 30, LightsOn = true };
// the dashboard canvas matches the screen glass (1.00 x 0.68 m)
Check(Math.Abs(HabRules.DashWidth * HabRules.DashUnit - 1.00) < 0.005 && Math.Abs(HabRules.DashHeight * HabRules.DashUnit - 0.68) < 0.005, "dash canvas fits the glass");

// dashboard (reactor-display style): gauges with bands, badge, cards, warnings, cells; trend ring buffer
StatusInputs D() { var x = Ok(); x.BatteryJ = 70000; x.BatteryMaxJ = 72000; x.Cells = new[] { 1.0, 0.94 }; x.ChipState = "none"; x.DrawW = 45; return x; }
var dm = HabRules.Dashboard(D());
Check(dm.Badge == "SEALED" && dm.BadgeLevel == StatusLevel.Normal && !dm.Dim, "dash badge sealed");
Check(dm.Gauges.Length == 4 && dm.Gauges[0].Label == "ROOM" && dm.Gauges[0].Level == StatusLevel.Normal && dm.Gauges[3].Label == "SOLAR", "dash gauges");
Check(dm.Cards.Length == 5 && dm.Cards[0].Title == "AIR SUPPLY" && Math.Abs(dm.Cards[0].Bars[0].Ratio - 6060 / 10132.5) < 1e-6, "dash air card fill");
Check(dm.Warnings.Length == 0 && dm.Cells.Length == 2, "dash normal: no warnings, 2 cells");
var cold = D(); cold.RoomC = 5; cold.RoomKPa = 70;
var dc = HabRules.Dashboard(cold);
Check(dc.Gauges[1].Level == StatusLevel.Warning && dc.Gauges[0].Level == StatusLevel.Warning, "dash bands amber");
var cyc = D(); cyc.DoorPhase = "PumpDown"; cyc.Sealed = true;
Check(HabRules.Dashboard(cyc).Badge == "CYCLING", "dash badge cycling");
var opn = D(); opn.DoorPhase = "Open"; opn.Sealed = false;
Check(HabRules.Dashboard(opn).Badge == "OPEN" && HabRules.Dashboard(opn).BadgeLevel == StatusLevel.Fault, "dash badge open");
var sto = D(); sto.Deployed = false;
Check(HabRules.Dashboard(sto).Badge == "STOWED" && HabRules.Dashboard(sto).Dim, "dash stowed dims");
var bad = D(); bad.SupplyKPa = null; bad.CleanL = null; bad.BatteryPct = 10;
var db = HabRules.Dashboard(bad);
Check(db.Warnings.Length >= 3 && db.Cards[0].Lines[0] == "none" && db.Gauges[2].Level == StatusLevel.Fault, "dash warnings and faults");
var ext = D(); ext.RoomKPa = 900; ext.RoomC = -80; ext.SolarW = 5000;
var de = HabRules.Dashboard(ext);
Check(de.Gauges[0].Value <= de.Gauges[0].Max && de.Gauges[1].Value >= de.Gauges[1].Min && de.Gauges[3].Value <= de.Gauges[3].Max, "dash gauges clamp");
var tr = new Trend(3, 2);
tr.Add(1, 10); tr.Add(2, 20); tr.Add(3, 30); tr.Add(4, 40);
Check(tr.Count == 3 && tr.Get(0, 0) == 2 && tr.Get(2, 1) == 40 && tr.Min(0) == 2 && tr.Max(1) == 40, "trend ring buffer");
Check(new Trend(120, 2).Count == 0 && new Trend(120, 2).Max(0) == 0, "trend empty");


// a tank or canister reporting no maximum (0) must not warn or draw NaN bars
var z = D(); z.WasteMaxKPa = 0; z.WasteWaterMaxL = 0; z.CleanMaxL = 0; z.SupplyMaxKPa = 0;
var zm = HabRules.Dashboard(z);
Check(zm.Warnings.Length == 0 && Array.TrueForAll(zm.Cards, c => Array.TrueForAll(c.Bars, b => b.Ratio >= 0 && b.Ratio <= 1)), "dash zero maxima");
// a flat battery turns the screen off; stowed only dims it
var fl = D(); fl.BatteryFlat = true;
Check(HabRules.Dashboard(fl).Dark && !HabRules.Dashboard(D()).Dark, "dash dark when flat");

// draw estimate: game "W" are joules per atmos tick; unknown while full, when cells changed, or with no interval
double? dw = HabRules.DrawEstimate(412, (412 - 45) * 10, 5, 0.5, false, false);
Check(dw is double dv && Math.Abs(dv - 45) < 1e-9, "draw per tick from battery change");
Check(HabRules.DrawEstimate(412, 0, 5, 0.5, true, false) == null && HabRules.DrawEstimate(412, -72000, 5, 0.5, false, true) == null
      && HabRules.DrawEstimate(412, 0, 0, 0.5, false, false) == null, "draw unknown when full, cells changed or no interval");
Check(HabRules.DrawEstimate(0, 500, 5, 0.5, false, false) == 0, "draw never negative");
var full = D(); full.DrawW = null; full.BatteryPct = 100;
Check(HabRules.Dashboard(full).Gauges[2].Sub == "batteries full", "battery sub when full");
// missing tanks and canisters colour their card lines; a missing waste-water canister warns
var gone = D(); gone.SupplyKPa = null; gone.WasteKPa = null; gone.CleanL = null; gone.WasteWaterL = null;
var dg = HabRules.Dashboard(gone);
Check(dg.Cards[0].LineLevels[0] == StatusLevel.Fault && dg.Cards[1].LineLevels[0] == StatusLevel.Fault
      && dg.Cards[2].LineLevels[0] == StatusLevel.Warning && dg.Cards[2].LineLevels[1] == StatusLevel.Warning, "missing tanks colour their cards");
Check(Array.Exists(dg.Warnings, x => x.Text == "No WASTE WATER canister"), "missing waste water warns");
Check(Array.TrueForAll(dm.Cards, c => c.LineLevels.Length == c.Lines.Length && Array.TrueForAll(c.LineLevels, l => l == StatusLevel.Normal)), "normal card lines");

// polish: the solar gauge does not claim to charge full batteries; big pressures drop the decimal to fit the ring
var sf = D(); sf.BatteryPct = 100; sf.SolarW = 400;
Check(HabRules.Dashboard(sf).Gauges[3].Sub == "batteries full" && HabRules.Dashboard(D()).Gauges[3].Sub == "charging", "solar sub when full");
var hp = D(); hp.RoomKPa = 10132.5;
Check(HabRules.Dashboard(hp).Gauges[0].ValueText == "10133 kPa" && HabRules.Dashboard(D()).Gauges[0].ValueText == "101.3 kPa", "room kPa text fits");

// vehicle upgrades (spec 2026-09-26-storm-upgrades-design.md)
Check(StormRules.UpgradeFor("ItemSteelSheets") == Upgrade.Armour && StormRules.UpgradeFor("ItemPlasticSheets") == Upgrade.Fairings
      && StormRules.UpgradeFor("ItemKitGovernedGasRocketEngine") == Upgrade.Thrusters && StormRules.UpgradeFor("ItemIronSheets") == null
      && StormRules.UpgradeFor(null) == null, "upgrade picked by the other hand");
Check(StormRules.Needed(Upgrade.Armour) == 10 && StormRules.Needed(Upgrade.Fairings) == 20 && StormRules.Needed(Upgrade.Thrusters) == 1, "materials needed");
Check(StormRules.FitRefusal(Upgrade.Armour, false, 10, false) == null && StormRules.FitRefusal(Upgrade.Fairings, false, 25, true) == null, "fit ok");
Check(StormRules.FitRefusal(Upgrade.Armour, true, 10, true) == "Storm armour already fitted", "already fitted");
Check(StormRules.FitRefusal(Upgrade.Fairings, false, 19, true) == "Needs 20 plastic sheets in the other hand", "too few sheets");
Check(StormRules.FitRefusal(Upgrade.Thrusters, false, 1, false) == "Thrusters fit the Cargo Rover only", "thrusters rover only");
Check(StormRules.FitVerb(Upgrade.Armour) == "Hold to fit storm armour (10 steel sheets)", "fit verb");
Check(StormRules.WindFactor(true) == 0.5f && StormRules.WindFactor(false) == 1f, "fairings halve the storm's push");
// storm armour = no storm damage on all three vehicles (the user, 2026-10-09, after Workshop feedback); the StormDamage
// setting scales what is left, applied at each storm tick (0 = immune), not baked into the prefab
Check(!StormRules.Weathered(armour: true, setting: 1f) && !StormRules.Weathered(armour: false, setting: 0f) && StormRules.Weathered(armour: false, setting: 0.25f),
      "storm: armour or StormDamage 0 means no storm damage tick at all");
Check(StormRules.StormDamageMultiplier(armour: true, setting: 1f) == 0f && StormRules.StormDamageMultiplier(armour: false, setting: 0.25f) == 0.25f,
      "storm: the damage is the game's times the setting, nothing with armour");
// grip: Bonus 0.25; Venus tyre load 8.87 x 1.25 = 11.0875
Check(Near(StormRules.GripDownforce(3.7f, 0.25f, false), 0.925f) && Near(StormRules.GripDownforce(3.7f, 0.25f, true), 11.0875f - 3.7f), "grip Mars");
Check(Near(StormRules.GripDownforce(1.62f, 0.25f, true), 11.0875f - 1.62f) && Near(StormRules.GripDownforce(0.97f, 0.25f, true), 11.0875f - 0.97f), "grip Moon, Mimas");
Check(Near(StormRules.GripDownforce(8.87f, 0.25f, true), 8.87f * 0.25f) && Near(StormRules.GripDownforce(-3.7f, 0.25f, true), 11.0875f - 3.7f), "grip Venus unchanged, sign-free");
Check(Near(StormRules.GripDownforce(3.7f, 0f, true), 8.87f - 3.7f) && StormRules.GripDownforce(3.7f, 0f, false) == 0f, "grip with TractionBonus 0");
Check(StormRules.ThrusterFiring(true, true, true, true, 3.7f, 2f, false) && StormRules.ThrusterFiring(true, true, true, true, 3.7f, 0f, true), "fires moving or driven");
Check(!StormRules.ThrusterFiring(true, true, true, true, 3.7f, 0.2f, false), "parked and empty seat: no fire");
Check(!StormRules.ThrusterFiring(true, true, true, true, 8.87f, 5f, true) && !StormRules.ThrusterFiring(true, true, false, true, 3.7f, 5f, true)
      && !StormRules.ThrusterFiring(true, false, true, true, 3.7f, 5f, true) && !StormRules.ThrusterFiring(false, true, true, true, 3.7f, 5f, true)
      && !StormRules.ThrusterFiring(true, true, true, false, 3.7f, 5f, true), "no fire: Venus, empty, off, not fitted, airborne");
Check(Near(StormRules.BurnKPaPerSecond(80, 80), 1000f / 1200f) && Near(StormRules.BurnKPaPerSecond(120, 80), 1000f / 800f), "burn alone, towing");
Check(Near(StormRules.BurnKPaPerSecond(400, 80), 1000f / 600f) && Near(StormRules.BurnKPaPerSecond(0, 0), 1000f / 1200f), "burn floor 10 min, bad masses");
Check(Near(StormRules.WasteLimitKPa(10132.5f), 9625.875f) && Near(StormRules.WasteLimitKPa(null), 4053f) && Near(StormRules.WasteLimitKPa(0f), 4053f), "waste limit");

var th = D(); th.Thrust = "firing";
var dt = HabRules.Dashboard(th);
Check(dt.Tags.Length == 5 && dt.Tags[4].Name == "THRUST" && dt.Tags[4].On && !HabRules.Dashboard(D()).Tags[4].On, "dash thrust tag");

// stack caps: a material that stacks below the cost is charged one full stack; refunds split into full stacks
Check(StormRules.NeededFor(Upgrade.Fairings, 50) == 20 && StormRules.NeededFor(Upgrade.Fairings, 15) == 15 && StormRules.NeededFor(Upgrade.Armour, 0) == 10, "needed within the stack cap");
Check(StormRules.FitRefusal(Upgrade.Fairings, false, 15, true, 15) == null && StormRules.FitRefusal(Upgrade.Fairings, false, 14, true, 15) == "Needs 15 plastic sheets in the other hand", "refusal follows the stack cap");
Check(string.Join(",", StormRules.DropStacks(20, 15)) == "15,5" && string.Join(",", StormRules.DropStacks(10, 50)) == "10" && string.Join(",", StormRules.DropStacks(3, 0)) == "3", "refund stacks");

// the rover must be switched on (and powered) and not held kinematic for the thrusters to fire, even with a driver
Check(!StormRules.ThrusterFiring(true, true, true, true, 3.7f, 0f, true, roverOn: false)
      && !StormRules.ThrusterFiring(true, true, true, true, 3.7f, 5f, false, roverOn: false)
      && !StormRules.ThrusterFiring(true, true, true, true, 3.7f, 0f, true, held: true)
      && StormRules.ThrusterFiring(true, true, true, true, 3.7f, 0f, true, roverOn: true, held: false), "no fire while the rover is off or held");

// hover burn rate, and which wrench action wins on an upgrade part
Check(Math.Abs(StormRules.MinutesPerMPa(80, 80) - 20) < 1e-3 && Math.Abs(StormRules.MinutesPerMPa(400, 80) - 10) < 1e-3, "minutes per MPa");
Check(StormRules.PickAction(Upgrade.Armour, Upgrade.Fairings, false) == WrenchAction.Fit, "material for an unfitted upgrade fits, even on another part");
Check(StormRules.PickAction(Upgrade.Armour, null, false) == WrenchAction.Remove && StormRules.PickAction(Upgrade.Armour, Upgrade.Armour, true) == WrenchAction.Remove
      && StormRules.PickAction(Upgrade.Armour, Upgrade.Fairings, true) == WrenchAction.Remove, "otherwise the part under the wrench comes off");
Check(StormRules.PickAction(null, Upgrade.Fairings, false) == WrenchAction.Fit && StormRules.PickAction(null, Upgrade.Fairings, true) == WrenchAction.Fit
      && StormRules.PickAction(null, null, false) == WrenchAction.None, "off the parts: fit (or its refusal) or the usual wrench job");

// trailer wheels. Unhitched: 200 parking brake, sticky allowed. Hitched: never sticky and never braked harder than the
// rover's own wheels (its brake, e.g. 20 parked): a parked hab braked at 200 propped the rover's rear up at the hitch
// (locked pose: both would have to roll apart to settle)
var (um, ub) = HabRules.TrailerWheelTorque(false, false, 0f, 200f, 0.0001f); Check(um == 0f && Near(ub, 200f), "unhitched: full parking brake, sticky allowed");
var (pm, pb) = HabRules.TrailerWheelTorque(true, false, 20f, 200f, 0.0001f); Check(pm > 0f && Near(pb, 20f), "hitched parked: the rover's brake, never sticky");
var (tdm, tdb) = HabRules.TrailerWheelTorque(true, true, 0f, 200f, 0.0001f); Check(tdm > 0f && tdb == 0f, "hitched driving: rolls free");
Check(Near(HabRules.TrailerWheelTorque(true, false, 10f, 200f, 0.0001f).brake, 10f), "hitched braking follows the rover");
Check(Near(HabRules.TrailerWheelTorque(true, false, 500f, 200f, 0.0001f).brake, 200f) && HabRules.TrailerWheelTorque(true, false, -3f, 200f, 0.0001f).brake == 0f, "hitched brake clamps to 0..parking");

// parked hitched hab: held still (kinematic) once the rig has stood 1 s, released by the gas or a shove
Check(!HabRules.ParkHold(false, false, 0f, 5f, false) && !HabRules.ParkHold(false, false, 0f, 5f, true), "unhitched: never held");
Check(!HabRules.ParkHold(true, false, 0f, 0.5f, false) && HabRules.ParkHold(true, false, 0f, 1f, false), "held after 1 s still");
Check(!HabRules.ParkHold(true, true, 0f, 5f, true), "gas releases at once");
Check(HabRules.ParkHold(true, false, 0.1f, 0f, true) && !HabRules.ParkHold(true, false, 0.3f, 0f, true), "stays held until the rover is shoved past 0.2 m/s");
Check(HabRules.ParkHold(true, false, 0f, 5f, true), "held on (the hitch force at hold time can read 700+ N: it must not release it)");

// power ledger (MadelynPlays, 0.2.3: "the hab uses 1.6 kW nearly constantly"): every system's joules per atmos tick
var ledger = new PowerLedger();
ledger.Add(PowerUse.Heater, 300f); ledger.Add(PowerUse.Charger, 500f); ledger.EndTick();
ledger.Add(PowerUse.Heater, 100f); ledger.EndTick();
var avg = ledger.PerTick();
Check(Near(avg[PowerUse.Heater], 200f) && Near(avg[PowerUse.Charger], 250f) && avg[PowerUse.Shower] == 0f && ledger.Ticks == 2,
      "power ledger: average W per system over the window (the game's W = J per atmos tick)");
Check(Near(ledger.TotalPerTick(), 450f), "power ledger: total draw per tick");
Check(ledger.Report().StartsWith("draw 450 W:") && ledger.Report().Contains("heater 200") && !ledger.Report().Contains("shower"),
      "power ledger: report lists the systems that drew, largest first");
ledger.Reset();
Check(ledger.Ticks == 0 && ledger.TotalPerTick() == 0f, "power ledger: reset starts a new window");

// the hab is insulated like a base room, not a suit (Europa, measured: 1637 W of heater at the cab's 0.05; the user's
// choice A: always insulated, 0.005 by default, its own cfg setting)
{
    var plug = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "Plugin.cs")));
    var prefabs = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "CargoPrefabs.cs")));
    var habSrc = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "CargoHab.cs")));
    var screenSrc = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "HabStatusScreen.cs")));
    Check(plug.Contains("cfg.Bind(\"Cabin\", \"HabInsulation\", 0.005f") && prefabs.Contains("HabInsulation = 0.005f")
          && prefabs.Contains("hab.CabinInsulation = _settings.HabInsulation;"),
          "hab insulation: its own setting, 0.005 by default (a base room)");
    // MadelynPlays (0.2.4): the rover's cab heater drained on Europa too (the cab's 0.05). A NEW key, because existing
    // cfgs keep their saved Insulation = 0.05 and a changed default would never reach them
    var roverBuild = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "CargoPrefabs.Rover.cs")));
    Check(plug.Contains("cfg.Bind(\"Cabin\", \"RoverInsulation\", 0.005f") && !plug.Contains("\"Insulation\",")
          && prefabs.Contains("RoverInsulation = 0.005f") && roverBuild.Contains("cr.CabinInsulation = _settings.RoverInsulation;"),
          "rover insulation: a new RoverInsulation key, 0.005 by default; the old Insulation key is no longer read");
    Check(habSrc.Contains("public double? MeasuredDrawW") && habSrc.IndexOf("MeasuredDrawW = Power.TotalPerTick()") < habSrc.IndexOf("if (LogClimate && ++_climateTick")
          && screenSrc.Contains("_hab.MeasuredDrawW ??"),
          "hab screen: the draw is the power ledger's measurement (any log setting), the estimate only where none exists (a client)");
}

// what freezes (0.2.2): a hitched rig is two bodies locked through the hitch on gripping tyres; left free while parked
// they push on each other (the rover tilts, players slide), so parked = trailer and rover frozen together, the gas frees both
Check(HabRules.RigHold(hitched: true, deployed: false, parkHeld: true) == (true, true), "parked hitched trailer: trailer and rover frozen");
Check(HabRules.RigHold(hitched: true, deployed: false, parkHeld: false) == (false, false), "hitched, driving or not yet still: both free");
Check(HabRules.RigHold(hitched: true, deployed: true, parkHeld: false) == (true, true), "deployed hab: the rover is frozen with it (no leaning on a fixed hab)");
Check(HabRules.RigHold(hitched: false, deployed: true, parkHeld: false) == (true, false), "deployed unhitched hab: frozen on its legs, no rover");
Check(HabRules.RigHold(hitched: false, deployed: false, parkHeld: false) == (false, false), "unhitched trailer: free (parking brake)");

// ---------------------------------------------------------------- original rover (RoverRules, spec 2026-09-27)
Check(RoverRules.Slots.Length == RoverRules.SlotCount && RoverRules.SlotCount == 28, "rover: 28 slots");
string[] oldKeys = { "Entity", "Entity", "GasFilter", "GasFilter", "ProgrammableChip", "GasCanister", "GasCanister", "GasCanister",
                     "GasCanister", "Battery", "Battery", "Battery", "ContainerSlot", "ContainerSlot", "GasTank", "GasTank" };
for (int i = 0; i < 16; i++) Check(RoverRules.Slots[i].Key == oldKeys[i], $"rover: slot {i} keeps its 2020 key");
Check(RoverRules.Slots[12].Anchor == "BayL1CrateBottom" && RoverRules.Slots[13].Anchor == "BayR1CrateBottom"
      && RoverRules.Slots[14].Anchor == "BayL1TankFront" && RoverRules.Slots[15].Anchor == "BayR1TankFront",
      "rover: the 2020 lift slots (12/14 left, 13/15 right) land in the front bays");
Check(RoverRules.Slots[7].Kind == RoverSlotKind.Air && RoverRules.Slots[8].Kind == RoverSlotKind.Waste && RoverRules.Slots[4].Kind == RoverSlotKind.Chip,
      "rover: propellant (7), waste (8) and chip (4) where the mod expects them");
var bayIdx = RoverRules.Bays.SelectMany(b => b.Crates.Concat(b.Tanks)).ToList();
Check(bayIdx.Count == 16 && bayIdx.Distinct().Count() == 16 && bayIdx.All(i => i >= 12 && i < 28), "rover: 16 bay slots, each in one bay");
Check(RoverRules.Bays.All(b => b.Crates.Length == 2 && b.Tanks.Length == 2
      && b.Crates.All(i => RoverRules.Slots[i].Kind == RoverSlotKind.Crate) && b.Tanks.All(i => RoverRules.Slots[i].Kind == RoverSlotKind.Tank)),
      "rover: each bay = 2 crate slots (the top one kept for saves, never filled) + 2 tank slots of the right kind");
Check(RoverRules.Slots.Select(s => s.Anchor).Distinct().Count() == 28, "rover: every slot has its own anchor");
var bayL1 = RoverRules.Bays[0]; var bayOcc = new HashSet<int>();
Check(RoverRules.BaySlotFor(bayL1, false, bayOcc.Contains) == 12, "bay: first crate goes to the bottom"); bayOcc.Add(12);
Check(RoverRules.BaySlotFor(bayL1, false, bayOcc.Contains) == -1,
      "bay: one crate per bay - a second is refused (the user's choice: a closed crate with its lid is 0.73 m, a bay 1.22 m inside)");
Check(RoverRules.BaySlotFor(bayL1, true, bayOcc.Contains) == -1, "bay: no tank beside a crate (vanilla ContainerSlot would allow it)");
Check(RoverRules.BayFull(bayL1, bayOcc.Contains) && !RoverRules.BayFull(bayL1, new HashSet<int>().Contains)
      && !RoverRules.BayFull(bayL1, new HashSet<int> { 14 }.Contains) && RoverRules.BayFull(bayL1, new HashSet<int> { 14, 17 }.Contains),
      "bay: full with its crate, or with both tanks (one tank leaves room)");
bayOcc.Clear();
Check(RoverRules.BaySlotFor(bayL1, true, bayOcc.Contains) == 14, "bay: first tank stands at the front"); bayOcc.Add(14);
Check(RoverRules.BaySlotFor(bayL1, true, bayOcc.Contains) == 17, "bay: second tank at the rear"); bayOcc.Add(17);
Check(RoverRules.BaySlotFor(bayL1, true, bayOcc.Contains) == -1 && RoverRules.BaySlotFor(bayL1, false, bayOcc.Contains) == -1,
      "bay: bayOcc of tanks takes nothing, not even a crate");
var ia = RoverRules.Interactables.ToDictionary(i => i.Key);
Check(new[] { "Export", "Button1", "Button2", "Import", "OnOff", "Powered" }.All(k => ia[k].Sync) && Enumerable.Range(1, 12).All(n => !ia["Slot" + n].Sync),
      "rover interactables: buttons synced, slot clicks not (2020 flags)");
Check(!ia["Powered"].KeyInteract && ia["Button1"].KeyInteract && ia["Powered"].Anchor == null && ia["Button1"].Anchor == "Screen",
      "rover interactables: Powered has no collider or key; the controls are dash-screen tap targets");
var even = RoverRules.AxleShares(0f, new[] { 1f, 0f, -1f });
Check(Near(even[0], 1f / 3) && Near(even[1], 1f / 3) && Near(even[2], 1f / 3), "axle shares: centred COM loads the axles evenly");
var fwd = RoverRules.AxleShares(0.5f, new[] { 1f, 0f, -1f });
Check(fwd[0] > fwd[1] && fwd[1] > fwd[2] && Near(fwd.Sum(), 1f), "axle shares: a forward COM loads the front most");
Check(RoverRules.Problems(k => true, a => true, new[] { "RoverBody" }, m => true).Count == 0, "rover.json problems: complete = none");
Check(RoverRules.Problems(k => k != "wheels", a => true, new string[0], m => true).Any(p => p.Contains("wheels")), "rover.json problems: missing key named");
Check(Near(RoverRules.SpringScale(0.286f, 0.035f, 0.055f, 0.196f), 1f) && Near(RoverRules.SpringScale(0.25f, 0.035f, 0.055f, 0.196f), 0.16f / 0.196f)
      && RoverRules.SpringScale(0.05f, 0.035f, 0.055f, 0.196f) == 0.05f && RoverRules.SpringScale(0.3f, 0.035f, 0.055f, 0f) == 1f,
      "shocks: the spring scales with its eyes' distance between the seats, never below 5 %, and one without a rest length stays as modelled");
Check(RoverRules.WorkLights.SequenceEqual(new[] { "Button13", "Button14" })
      && RoverRules.WorkLights.All(w => RoverRules.Interactables.Any(i => i.Action == w && i.Sync && i.KeyInteract && i.Anchor == "Screen"))
      && RoverRules.Interactables.Select(i => i.Action).Distinct().Count() == RoverRules.Interactables.Length
      && RoverRules.SideLightAnchors.Length == 4 && RoverRules.RearLightAnchors.Length == 2
      && RoverRules.SideLightAnchors.Concat(RoverRules.RearLightAnchors).All(a => RoverRules.NamedAnchors.Contains(a)),
      "work lights: side (Button13) and rear (Button14) are synced screen buttons, no action used twice (not Button1-3: vanilla restores those), their light anchors required");
Check(RoverRules.WorkLightDraw(false, false) == 0f && RoverRules.WorkLightDraw(true, false) == RoverRules.SideLightsPower
      && RoverRules.WorkLightDraw(false, true) == RoverRules.RearLightsPower
      && RoverRules.WorkLightDraw(true, true) == RoverRules.SideLightsPower + RoverRules.RearLightsPower
      && RoverRules.SideLightsPower == 20f && RoverRules.RearLightsPower == 10f,
      "work lights: they draw power (the user's request) - side 20, rear 10 a power tick (vanilla: headlights 20, cabin light 5)");
Check(RoverRules.DrainSlot(new[] { true, false, true }) == 2 && RoverRules.DrainSlot(new[] { true, false, false }) == 0
      && RoverRules.DrainSlot(new[] { false, false }) == -1 && RoverRules.DrainSlot(new bool[0]) == -1,
      "work lights: their draw comes out of the battery vanilla drains (the last charged one), nothing when all are flat");
// crate lids in the bays (the user's test build 4 report: the top crate of a stacked pair poked through its closed door;
// an open lid rises 18 cm and two crates leave 11 cm under the bay's ceiling)
Func<int, bool> doorsOpen = b => true, doorsShut = b => false;
Check(RoverRules.LidBlock(12, k => k == 12, doorsOpen) == null && RoverRules.LidBlock(12, k => k == 12, doorsShut) != null
      && RoverRules.LidBlock(16, k => k == 16, doorsOpen) != null && RoverRules.LidBlock(12, k => k == 12 || k == 16, doorsOpen) != null
      && RoverRules.LidBlock(14, k => true, doorsShut) == null && RoverRules.LidBlock(5, k => true, doorsShut) == null,
      "bays: a crate's lid opens only with its bay door open (none in a top slot left by an old save, nor under one)");
Check(RoverRules.LidsToClose(k => k == 12, k => k == 12, doorsShut).SequenceEqual(new[] { 12 })
      && RoverRules.LidsToClose(k => k == 12, k => k == 12, doorsOpen).Count == 0
      && RoverRules.LidsToClose(k => k == 20 || k == 21, k => k == 20 || k == 21, doorsOpen).OrderBy(x => x).SequenceEqual(new[] { 20, 21 })
      && RoverRules.LidsToClose(k => true, k => false, doorsShut).Count == 0,
      "bays: a lid open in a bay whose door is shut is to be closed (so it never comes through the door)");
Check(RoverRules.HeadlightAnchors.SequenceEqual(new[] { "HeadlightL", "HeadlightR" }) && RoverRules.LightBarAnchors.SequenceEqual(new[] { "LightBarL", "LightBarR" })
      && RoverRules.HeadlightAnchors.Concat(RoverRules.LightBarAnchors).All(a => RoverRules.NamedAnchors.Contains(a)),
      "front lights: the headlights' and the roof light bar's anchors are required");
Check(RoverRules.Problems(k => true, a => a != "BayR2TankRear", new string[0], m => true).Any(p => p.Contains("BayR2TankRear")), "rover.json problems: missing slot anchor named");
Check(RoverRules.Problems(k => true, a => a != "Hitch", new string[0], m => true).Any(p => p.Contains("Hitch")), "rover.json problems: missing named anchor named");
Check(RoverRules.Problems(k => true, a => true, new[] { "RoverBody", "TyreL" }, m => m != "TyreL").Any(p => p.Contains("TyreL")), "rover.json problems: missing mesh file named");
// click boxes: exposure and line of sight (the game's cursor is one raycast: a solid collider in front hides a trigger)
var bigBox = new RoverBox(0, 0, 0, 2, 2, 2);
Check(RoverRules.ExposedFraction(new RoverBox(0, 0, 0, 0.5f, 0.5f, 0.5f), new[] { bigBox }) == 0f, "exposure: a box inside a solid is sealed");
Check(RoverRules.ExposedFraction(new RoverBox(5, 0, 0, 0.5f, 0.5f, 0.5f), new[] { bigBox }) == 1f, "exposure: a box clear of every solid is open");
var halfOpen = RoverRules.ExposedFraction(new RoverBox(1, 0, 0, 0.5f, 0.5f, 0.5f), new[] { bigBox });
Check(halfOpen > 0.3f && halfOpen < 0.7f, $"exposure: a box half in a solid is half open ({halfOpen:0.00})");
Check(RoverRules.SegmentBlocked((-3f, 0f, 0f), (3f, 0f, 0f), new[] { bigBox }), "line of sight: a solid in the way blocks");
Check(!RoverRules.SegmentBlocked((0f, 0f, 0f), (3f, 0f, 0f), new[] { bigBox }), "line of sight: a ray from inside a solid is not blocked by it");
Check(!RoverRules.SegmentBlocked((-3f, 3f, 0f), (3f, 3f, 0f), new[] { bigBox }), "line of sight: a line past the solid is clear");
Check(RoverRules.ClickAnchor("SeatDriver") == "EntryDriver" && RoverRules.ClickAnchor("SeatPassenger") == "EntryPassenger"
      && RoverRules.ClickAnchor("Filter1") == "Filter1", "click anchors: seats are entered at their cab doors, other slots at the slot");

// ---------------------------------------------------------------- rover dash screen (RoverDashboard)
RoverInputs DashIn() => new RoverInputs
{
    On = true, Charged = true, BatteryJ = 76, BatteryMaxJ = 100, Cells = 3, DrawW = 400, SpeedMs = 5, MaxSpeedMs = 6.5,
    CabinKPa = 101, CabinO2Pct = 21, CabinC = 22, AirKPa = 4200, AirMaxKPa = 6000, WasteKPa = 1000, WasteMaxKPa = 3850, FiltersFitted = 2,
};
RoverDash Dash(Func<RoverInputs, RoverInputs> change) => RoverDashboard.Build(change(DashIn()));
Check(RoverDashboard.PageFor(true, false) == RoverPage.Dark && RoverDashboard.PageFor(false, false) == RoverPage.Dark, "dash: no charge = dark, on or off");
Check(RoverDashboard.PageFor(false, true) == RoverPage.Standby && RoverDashboard.PageFor(true, true) == RoverPage.Driving, "dash: off = standby, on = driving");
// a remote client knows a cell's charge only as its synced percentage (BatteryCell.PowerStored is server-side): the page
// follows Charged, and power use is not shown (1 % steps cannot measure it)
Check(Dash(x => x with { Remote = true, BatteryJ = 0, Charged = true }).Page == RoverPage.Driving && Dash(x => x with { Charged = false }).Page == RoverPage.Dark,
      "dash: the page follows the cells' synced charge, not PowerStored (review C1: remote clients read 0 J)");
Check(Dash(x => x with { Remote = true, DrawW = null }).Battery.Sub == "" && Dash(x => x with { DrawW = null }).Battery.Sub == "measuring",
      "dash: a remote client shows no power use; the host measures it");
Check(Dash(x => x with { AirPumpOn = true, AirKPa = 0 }).Warnings.Any(wl => wl.Text == "Air tank empty" && wl.Level == StatusLevel.Fault)
      && Dash(x => x with { AirKPa = 0 }).Cards[1].Lines[0] == "0.0 MPa", "dash: an empty air canister reads empty, not none");
var dash0 = RoverDashboard.Build(DashIn());
Check(dash0.Page == RoverPage.Driving && dash0.Badge == "DRIVING" && dash0.Speed.ValueText == "18" && dash0.Speed.Sub == "max 23 km/h",
      "dash: 5 m/s reads 18 km/h of max 23");
Check(Dash(x => x with { SpeedMs = 0.1 }).Badge == "PARKED", "dash: below 0.2 m/s the badge says PARKED");
Check(dash0.TowTag == null && Dash(x => x with { Towing = "HAB" }).TowTag == "TOWING HAB", "dash: the towing tag names what is hitched");
Check(dash0.Battery.ValueText == "76%" && dash0.Battery.Sub == "using 0.4 kW" && dash0.BatteryCells == "3 cells"
      && Dash(x => x with { DrawW = null }).Battery.Sub == "measuring", "dash: battery %, power use, cells");
Check(Dash(x => x with { BatteryJ = 14.9 }).Battery.Level == StatusLevel.Warning && Dash(x => x with { BatteryJ = 4.9 }).Battery.Level == StatusLevel.Fault
      && dash0.Battery.Level == StatusLevel.Normal, "dash: battery yellow under 15 %, red under 5 %");
Check(RoverDashboard.TiltLevel(11.9) == StatusLevel.Normal && RoverDashboard.TiltLevel(12) == StatusLevel.Warning
      && RoverDashboard.TiltLevel(-25) == StatusLevel.Fault, "dash: tilt yellow from 12 deg, red from 25, either way");
var tilted = Dash(x => x with { PitchDeg = 8, RollDeg = 14 });
Check(tilted.PitchText == "+8°" && tilted.PitchWords == "nose up" && tilted.RollText == "14°" && tilted.RollWords == "right side down"
      && tilted.RollLevel == StatusLevel.Warning && tilted.PitchLevel == StatusLevel.Normal, "dash: pitch and roll texts, words and colours");
var tiltedBack = Dash(x => x with { PitchDeg = -5, RollDeg = -3 });
Check(tiltedBack.PitchText == "-5°" && tiltedBack.PitchWords == "nose down" && tiltedBack.RollWords == "left side down"
      && Dash(x => x with { PitchDeg = 0.2 }).PitchWords == "level", "dash: nose down, left side down, level");
Check(tilted.Warnings.Length == 1 && tilted.Warnings[0].Text == "Roll 14°" && tilted.Warnings[0].Level == StatusLevel.Warning,
      "dash: a tilt warning names the steeper axis");
Check(Dash(x => x with { CabinKPa = 70 }).Warnings.Any(wl => wl.Text == "Cabin pressure low" && wl.Level == StatusLevel.Warning)
      && Dash(x => x with { CabinKPa = 40 }).Warnings.Any(wl => wl.Text == "Cabin pressure low" && wl.Level == StatusLevel.Fault)
      && !Dash(x => x with { On = false, CabinKPa = 40 }).Warnings.Any(wl => wl.Text == "Cabin pressure low"), "dash: cabin pressure warns while the rover is on");
Check(Dash(x => x with { CabinO2Pct = 15 }).Warnings.Any(wl => wl.Text == "Cabin O2 low")
      && !Dash(x => x with { CabinO2Pct = 15, CabinKPa = 30 }).Warnings.Any(wl => wl.Text == "Cabin O2 low"), "dash: O2 warns only with air to measure");
Check(Dash(x => x with { AirPumpOn = true, AirKPa = null }).Warnings.Any(wl => wl.Text == "No air canister" && wl.Level == StatusLevel.Fault)
      && Dash(x => x with { AirPumpOn = true, AirKPa = 1000 }).Warnings.Any(wl => wl.Text == "Air tank low")
      && !Dash(x => x with { AirKPa = null }).Warnings.Any(wl => wl.Text == "No air canister"), "dash: air warnings only with the air pump on");
Check(Dash(x => x with { WasteKPa = 3100 }).Warnings.Any(wl => wl.Text == "Waste 81% full" && wl.Level == StatusLevel.Warning)
      && Dash(x => x with { WasteKPa = 3700 }).Warnings.Any(wl => wl.Text == "Waste full" && wl.Level == StatusLevel.Fault), "dash: waste yellow over 80 %, red over 95 %");
Check(Dash(x => x with { FilterOn = true, FiltersFitted = 0 }).Warnings.Any(wl => wl.Text == "No filter fitted"), "dash: filter on without a filter warns");
var worst = Dash(x => x with { WasteKPa = 3700, RollDeg = 14 }).Warnings;
Check(worst.Length == 2 && worst[0].Text == "Waste full" && worst[1].Text == "Roll 14°", "dash: faults come before warnings");
var bayCards = Dash(x => x with { BaysFilled = 3, BaysTotal = 4 }).Cards;
Check(string.Join(",", dash0.Cards.Select(cd => cd.Title)) == "CABIN,AIR TANK,WASTE" && bayCards.Last().Title == "BAYS" && bayCards.Last().Lines[0] == "3 / 4 full",
      "dash: the BAYS card appears once the bays report");
Check(string.Join(",", dash0.Buttons.Select(bt => bt.Label)) == "POWER,HEADLIGHTS,SIDE LIGHTS,REAR LIGHTS,CABIN LIGHT,AIR PUMP,FILTER,THRUSTERS"
      && dash0.Buttons[0].State == ScreenButtonState.On && dash0.Buttons[1].State == ScreenButtonState.Off
      && dash0.Buttons[7].State == ScreenButtonState.NotFitted && !dash0.Buttons[7].Active, "dash: eight buttons; thrusters not fitted and not tappable");
var thr = Dash(x => x with { ThrustersFitted = true, ThrustersOn = true });
Check(thr.Buttons[7].State == ScreenButtonState.On && thr.Buttons[7].Active, "dash: fitted thrusters are a live button");
var work = Dash(x => x with { SideLightsOn = true });
Check(work.Buttons[2].Action == "Button13" && work.Buttons[2].State == ScreenButtonState.On && work.Buttons[3].Action == "Button14"
      && work.Buttons[3].State == ScreenButtonState.Off && Dash(x => x with { RearLightsOn = true }).Buttons[3].State == ScreenButtonState.On
      && work.Buttons[2].Active && work.Buttons[3].Active, "dash: the side and rear work lights have live buttons showing their states");
var stby = Dash(x => x with { On = false });
Check(stby.Page == RoverPage.Standby && stby.Buttons.Count(bt => bt.Active) == 1 && stby.Buttons[0].Active && stby.StandbyText == "Rover off - battery 76%",
      "dash: standby has only POWER");
bool OnCanvas((float X, float Y, float W, float H) rc) => rc.X >= 0 && rc.Y >= 0 && rc.X + rc.W <= RoverDashboard.Width && rc.Y + rc.H <= RoverDashboard.Height;
bool Overlaps((float X, float Y, float W, float H) qa, (float X, float Y, float W, float H) qb) => qa.X < qb.X + qb.W && qb.X < qa.X + qa.W && qa.Y < qb.Y + qb.H && qb.Y < qa.Y + qa.H;
var driveRects = RoverDashboard.Controls.Select(ct => RoverDashboard.TapRect(ct.Action, RoverPage.Driving, true)).ToList();
Check(driveRects.All(rr => rr != null && OnCanvas(rr.Value)), "tap targets: all eight on the canvas when driving");
Check(driveRects.SelectMany((ra, ri) => driveRects.Skip(ri + 1).Select(rb => Overlaps(ra!.Value, rb!.Value))).All(ov => !ov), "tap targets: no two overlap");
Check(RoverDashboard.TapRect("Button12", RoverPage.Driving, false) == null, "tap targets: no THRUSTERS target when not fitted");
Check(RoverDashboard.TapRect("OnOff", RoverPage.Standby, false) == RoverDashboard.StandbyPower && RoverDashboard.TapRect("OnOff", RoverPage.Dark, false) == RoverDashboard.StandbyPower
      && RoverDashboard.Controls.Skip(1).All(ct => RoverDashboard.TapRect(ct.Action, RoverPage.Standby, true) == null), "tap targets: standby and dark keep only POWER, in the centre");
var (tapC, tapS) = RoverDashboard.TapBox((550f, 290f, 100f, 100f));
Check(Math.Abs(tapC.X) < 1e-6 && Math.Abs(tapC.Y) < 1e-6 && Near(tapC.Z, 0.006f) && Near(tapS.X, 0.05f) && Near(tapS.Z, 0.01f), "tap box: the canvas centre is the face centre");
Check(RoverDashboard.TapBox(RoverDashboard.DriveButton(0)).Center.X > 0 && RoverDashboard.TapBox(RoverDashboard.DriveButton(0)).Center.Y < 0,
      "tap box: POWER (canvas lower left) sits at the face's +x and below its centre (the canvas faces the cab)");
var fq = RoverDashboard.AnchorPoint((0f, 0f, 0f), (0f, 90f, 0f), (0f, 0f, 1f));
Check(Near(fq.X, 1f) && Near(fq.Z, 0f), "anchor frame: yaw 90 turns forward to +x (Unity)");
fq = RoverDashboard.AnchorPoint((0f, 0f, 0f), (90f, 0f, 0f), (0f, 0f, 1f));
Check(Near(fq.Y, -1f) && Near(fq.Z, 0f), "anchor frame: pitch 90 turns forward down (Unity)");
fq = RoverDashboard.AnchorPoint((0f, 0f, 0f), (-45f, 180f, 0f), (0f, 0f, 1f));
Check(Near(fq.X, 0f) && Near(fq.Y, 0.7071f) && Near(fq.Z, -0.7071f), "anchor frame: the Screen anchor's +z points up and back to the seats");
Check(RoverDashboard.Degrees("Roll 14°", false) == "Roll 14 deg" && RoverDashboard.Degrees("14°", true) == "14°", "dash: no degree glyph -> ' deg'");

// ---------------------------------------------------------------- bay doors, the bay attach rule, the slot panel (sub-project 3)
Check(RoverRules.Doors.Length == 4 && RoverRules.Doors.Select(d => d.Bay).Distinct().Count() == 4
      && RoverRules.Doors.All(d => d.Name == "BayDoor" + RoverRules.Bays[d.Bay].Name && d.Side == (RoverRules.Bays[d.Bay].Name[0] == 'L' ? -1 : 1))
      && RoverRules.Doors.Select(d => d.Action).SequenceEqual(new[] { "Button5", "Button6", "Button7", "Button8" }),
      "doors: one per bay, named for it, left -1 / right +1, states on Button5-8");
Check(RoverRules.Doors.All(d => d.Action is not ("Button1" or "Button2" or "Button3")),
      "doors: no door state on Button1-3 (vanilla Thing.HasState restores those itself: a saved Button3 lands on Button1, the headlights; review I1)");
Check(RoverRules.Doors.All(d => ia.TryGetValue(d.Action, out var di) && di.Sync && !di.KeyInteract && di.Anchor == null),
      "doors: each door's state is a synced interactable without a collider or key (the leaf is the wrench target)");
Check(Near(RoverRules.DoorAngle(1, 1f), 100f) && Near(RoverRules.DoorAngle(-1, 1f), -100f) && RoverRules.DoorAngle(1, 0f) == 0f && Near(RoverRules.DoorAngle(1, 2f), 100f),
      "doors: 100 deg open, clamped, mirrored left and right");
var swungR = RoverDashboard.AnchorPoint((0f, 0f, 0f), (0f, 0f, RoverRules.DoorAngle(1, 0.9f)), (0f, -1.2f, 0f));
var swungL = RoverDashboard.AnchorPoint((0f, 0f, 0f), (0f, 0f, RoverRules.DoorAngle(-1, 0.9f)), (0f, -1.2f, 0f));
Check(swungR.X > 1f && swungL.X < -1f, "doors: the leaf's bottom edge swings up and out on the right and on the left");
Check(RoverRules.BayLabel(RoverRules.Bays[0]) == "left front" && RoverRules.BayLabel(RoverRules.Bays[3]) == "right rear", "doors: bays named for players");
Func<int, bool> allOpen = bay => true, noneFilled = new HashSet<int>().Contains;
float BayDist(int bay) => bay == 3 ? 0f : bay + 1;                                  // R2 nearest, then L1, R1, L2
Check(RoverRules.PickBaySlot(false, allOpen, noneFilled, BayDist) == 24, "bay attach: a crate goes to the nearest open bay's bottom (R2)");
Check(RoverRules.PickBaySlot(false, bay => bay != 3, noneFilled, BayDist) == 12, "bay attach: a shut bay is passed over (L1 next)");
Check(RoverRules.PickBaySlot(true, bay => false, noneFilled, BayDist) == -1, "bay attach: nothing loads with every door shut");
Check(RoverRules.PickBaySlot(false, allOpen, new HashSet<int> { 24 }.Contains, BayDist) == 12, "bay attach: a bay with its crate passes the next crate on");
Check(RoverRules.PickBaySlot(true, allOpen, new HashSet<int> { 24 }.Contains, BayDist) == 14, "bay attach: no tank beside a crate: the next bay's front tank");
foreach (var kind in new[] { RoverSlotKind.Filter, RoverSlotKind.Chip, RoverSlotKind.Air, RoverSlotKind.Waste, RoverSlotKind.Battery })
{
    var (bodyC, bodyS) = RoverRules.ItemBody(kind);
    var clickC = RoverRules.ClickBoxCenter(kind);
    Check(Math.Abs(clickC.X - bodyC.X) < bodyS.X / 2 && Math.Abs(clickC.Y - bodyC.Y) < bodyS.Y / 2 && Math.Abs(clickC.Z - bodyC.Z) < bodyS.Z / 2,
          $"click boxes: the {kind} box is centred on the item's body (an item's origin is not its middle)");
}
Check(RoverRules.IsBaySlot(12) && RoverRules.IsBaySlot(27) && RoverRules.IsBaySlot(16) && !RoverRules.IsBaySlot(11) && !RoverRules.IsBaySlot(0) && !RoverRules.IsBaySlot(28),
      "bay release: the bay slots are 12-27");
var dropL = RoverRules.BayDropPoint((-0.88f, 1.82f, -0.70f));
var dropTop = RoverRules.BayDropPoint((0.88f, 1.82f + 0.558f, -2.70f));
Check(dropL.X < -2.5f && Near(dropL.Y, 0.30f) && Near(dropL.Z, -0.70f) && dropTop.X > 2.5f && Near(dropTop.Y, 0.858f) && Near(dropTop.Z, -2.70f),
      "bay release: an item let go from a bay lands on the ground beside its door; the top crate above the bottom one (review I5)");
var (canBodyC, canBodyS) = RoverRules.ItemBody(RoverSlotKind.Air);
Check(Near(canBodyC.Y + canBodyS.Y / 2, 0.194f) && Near(canBodyC.Y - canBodyS.Y / 2, -0.680f) && RoverRules.ClickBoxCenter(RoverSlotKind.Air).Y < canBodyC.Y,
      "click boxes: a canister (valve end 0.194 above its origin, 0.874 long) is clicked on its outer half, as the 2020 trigger");
Check(Dash(x => x with { BaysFilled = 3, BaysTotal = 4, DoorsOpen = 2 }).Cards.Last().Lines[1] == "2 doors open"
      && Dash(x => x with { BaysFilled = 0, BaysTotal = 4, DoorsOpen = 1 }).Cards.Last().Lines[1] == "1 door open"
      && Dash(x => x with { BaysFilled = 0, BaysTotal = 4 }).Cards.Last().Lines[1] == "doors shut", "dash: the BAYS card counts the open doors");

// ---------------------------------------------------------------- RoverAssets/rover.json against RoverRules
static string RepoFile(string rel)
{
    // this workspace (mods/Stationeers.RoverCargo/...) or the mod's own repo (the mod at the root)
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !Directory.Exists(Path.Combine(d.FullName, "mods")) && !File.Exists(Path.Combine(d.FullName, "Stationeers.RoverCargo.csproj"))) d = d.Parent;
    if (d == null) return rel;
    var prefix = Path.Combine("mods", "Stationeers.RoverCargo") + Path.DirectorySeparatorChar;
    return File.Exists(Path.Combine(d.FullName, "Stationeers.RoverCargo.csproj")) && rel.StartsWith(prefix)
        ? Path.Combine(d.FullName, rel.Substring(prefix.Length)) : Path.Combine(d.FullName, rel);
}
var roverPath = RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "RoverAssets", "rover.json"));
Check(File.Exists(roverPath), "rover.json exported");
using (var rj = System.Text.Json.JsonDocument.Parse(File.ReadAllText(roverPath)))
{
    var root = rj.RootElement;
    var anchors = root.GetProperty("anchors");
    Check(RoverRules.Slots.All(s => anchors.TryGetProperty(s.Anchor, out _)) && RoverRules.NamedAnchors.All(a => anchors.TryGetProperty(a, out _)),
          "rover.json: every RoverRules anchor exported");
    float Ax(string name, int k) => anchors.GetProperty(name).GetProperty("pos")[k].GetSingle();
    Check(Ax("BayL1CrateBottom", 0) < 0 && Ax("BayR1CrateBottom", 0) > 0 && Ax("BayL1TankFront", 2) > Ax("BayL1TankRear", 2),
          "rover.json: the 2020 lift slots' new homes are the front bays (left/right, front tank ahead)");
    var wheelZ = root.GetProperty("wheels").EnumerateArray().Select(w => w.GetProperty("pos")[2].GetSingle()).Distinct().OrderByDescending(z => z).ToArray();
    var comZ = root.GetProperty("autoCom")[2].GetSingle();
    var shares = RoverRules.AxleShares(comZ, wheelZ);
    Check(wheelZ.Length == 3 && shares.All(s => s >= 0.20f), $"rover.json: every axle carries >= 20 % (shares {string.Join("/", shares.Select(s => s.ToString("0.00")))})");

    var solids = new List<RoverBox>();
    foreach (var c in root.GetProperty("colliders").EnumerateArray())
    {
        Check(!c.TryGetProperty("rot", out _), "rover.json: colliders are axis-aligned (the click-box check assumes it)");
        float C(string p, int k) => c.GetProperty(p)[k].GetSingle();
        solids.Add(new RoverBox(C("center", 0), C("center", 1), C("center", 2), C("size", 0), C("size", 1), C("size", 2)));
    }
    // a box (centre c, size s in an anchor's frame) as the rover-local box round it, the anchor's rotation applied
    RoverBox Aabb(string anchor, (float X, float Y, float Z) c, (float X, float Y, float Z) s)
    {
        var an = anchors.GetProperty(anchor);
        return AabbAt(Vec(an.GetProperty("pos")), Vec(an.GetProperty("rot")), c, s);
    }
    RoverBox AabbAt((float X, float Y, float Z) pos, (float X, float Y, float Z) rot, (float X, float Y, float Z) c, (float X, float Y, float Z) s)
    {
        var pts = new List<(float X, float Y, float Z)>();
        foreach (var i in new[] { -1, 1 }) foreach (var j in new[] { -1, 1 }) foreach (var k in new[] { -1, 1 })
            pts.Add(RoverDashboard.AnchorPoint(pos, rot, (c.X + i * s.X / 2, c.Y + j * s.Y / 2, c.Z + k * s.Z / 2)));
        return RoverBox.FromCorners(pts.Min(q => q.X), pts.Max(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.Y), pts.Min(q => q.Z), pts.Max(q => q.Z));
    }
    // the original rover's upgrades (sub-project 4): their removal colliders (triggers the wrench aims at) in rover space
    var upgBoxes = new List<(string Name, RoverBox Box)>();
    if (root.TryGetProperty("upgrades", out var upAll) && upAll.TryGetProperty("rover", out var upRover))
        foreach (var e in upRover.EnumerateArray())
        {
            var at = e.TryGetProperty("pos", out var pe) ? Vec(pe) : (0f, 0f, 0f);
            foreach (var c in e.GetProperty("colliders").EnumerateArray())
            {
                var cc = Vec(c.GetProperty("center"));
                var centre = (at.Item1 + cc.Item1, at.Item2 + cc.Item2, at.Item3 + cc.Item3);
                var rot = c.TryGetProperty("rot", out var cr) ? Vec(cr) : (0f, 0f, 0f);   // a rotated box: its corners' bounds
                upgBoxes.Add((e.GetProperty("name").GetString(), AabbAt(centre, rot, (0f, 0f, 0f), Vec(c.GetProperty("size")))));
            }
        }
    Check(upgBoxes.Count > 0, "rover.json: the original rover's upgrade parts have removal colliders");
    for (int i = 0; i < RoverRules.SlotCount; i++)
    {
        var (_, kind, anchor) = RoverRules.Slots[i];
        if (kind is RoverSlotKind.Crate or RoverSlotKind.Tank) continue;     // the bay items: below (through their open doors)
        var click = Aabb(RoverRules.ClickAnchor(anchor), RoverRules.ClickBoxCenter(kind), RoverRules.ClickBoxSize(kind));
        if (kind == RoverSlotKind.Seat)                     // a seat's: its outer slab off the door (the rest reaches into the cab)
            click = click.Cx > 0 ? RoverBox.FromCorners(click.Cx + click.Sx / 2 - RoverRules.SeatDoorSlab, click.Cx + click.Sx / 2, click.Cy - click.Sy / 2, click.Cy + click.Sy / 2, click.Cz - click.Sz / 2, click.Cz + click.Sz / 2)
                                 : RoverBox.FromCorners(click.Cx - click.Sx / 2, click.Cx - click.Sx / 2 + RoverRules.SeatDoorSlab, click.Cy - click.Sy / 2, click.Cy + click.Sy / 2, click.Cz - click.Sz / 2, click.Cz + click.Sz / 2);
        var open = RoverRules.ExposedFraction(click, solids);
        float need = kind == RoverSlotKind.Seat ? 0.9f : 0.25f;
        Check(open >= need, $"rover.json: slot {i} ({anchor}) can be clicked from outside: {open:P0} of its click box is outside the solid colliders (need {need:P0})");
        var over = upgBoxes.Where(b => !Apart(b.Box, click, 0f)).Select(b => b.Name).Distinct().ToList();
        Check(over.Count == 0, $"rover.json: slot {i} ({anchor})'s click box is clear of every upgrade's removal collider ({string.Join(", ", over)})");
    }
    (float, float, float) Vec(System.Text.Json.JsonElement je) => (je[0].GetSingle(), je[1].GetSingle(), je[2].GetSingle());
    var screen = anchors.GetProperty("Screen");
    var (screenPos, screenRot) = (Vec(screen.GetProperty("pos")), Vec(screen.GetProperty("rot")));
    // the seated camera is the helmet (vanilla Rover.PhysicsUpdate moves the camera point there every step), near the
    // camera anchor but not on it (review I1): from every eye point 30 cm behind to 10 cm ahead of the anchor and 15 cm
    // below to 20 cm above it, every tap target and the other seat's click box (swapping seats, the user's check-in 3
    // report: its face toward the cab, at the door's lower half) are in reach, no solid collider between
    var eyes = new[] { -0.30f, -0.15f, 0f, 0.10f }.SelectMany(dz => new[] { -0.15f, 0f, 0.20f }.Select(dh => (dz, dh))).ToList();
    foreach (var (cam, other) in new[] { ("CameraDriver", "EntryPassenger"), ("CameraPassenger", "EntryDriver") })
    {
        var eb = Aabb(other, RoverRules.ClickBoxCenter(RoverSlotKind.Seat), RoverRules.ClickBoxSize(RoverSlotKind.Seat));
        var door = (eb.Cx > 0 ? eb.Cx - eb.Sx / 2 : eb.Cx + eb.Sx / 2, eb.Cy - eb.Sy / 4, eb.Cz);
        foreach (var (dz, dh) in eyes)
        {
            var eye = (Ax(cam, 0), Ax(cam, 1) + dh, Ax(cam, 2) + dz);
            foreach (var page in new[] { RoverPage.Driving, RoverPage.Standby })
                foreach (var (_, action) in RoverDashboard.Controls)
                    if (RoverDashboard.TapRect(action, page, true) is { } rect)
                        Check(!RoverRules.SegmentBlocked(eye, RoverDashboard.AnchorPoint(screenPos, screenRot, RoverDashboard.TapBox(rect).Center), solids),
                              $"rover.json: the {action} tap target ({page}) is in reach from {cam} {dz:+0.00;-0.00} z {dh:+0.00;-0.00} h (no solid collider between)");
            Check(!RoverRules.SegmentBlocked(eye, door, solids),
                  $"rover.json: from {cam} {dz:+0.00;-0.00} z {dh:+0.00;-0.00} h the other seat's click box ({other}) is in reach (swapping seats)");
        }
    }

    // the covered bays and the slot panel (sub-project 3): the measured items at their anchors
    var partsArr = root.GetProperty("parts").EnumerateArray().ToList();
    Check(RoverRules.Doors.All(d => partsArr.Any(p => p.GetProperty("name").GetString() == d.Name && p.TryGetProperty("hinge", out var hg) && hg.GetArrayLength() == 3)),
          "rover.json: every bay door leaf has its hinge");
    bool Within(RoverBox a, RoverBox room, float m) =>
        a.Cx - a.Sx / 2 >= room.Cx - room.Sx / 2 + m - 1e-4f && a.Cx + a.Sx / 2 <= room.Cx + room.Sx / 2 - m + 1e-4f
        && a.Cy - a.Sy / 2 >= room.Cy - room.Sy / 2 + m - 1e-4f && a.Cy + a.Sy / 2 <= room.Cy + room.Sy / 2 - m + 1e-4f
        && a.Cz - a.Sz / 2 >= room.Cz - room.Sz / 2 + m - 1e-4f && a.Cz + a.Sz / 2 <= room.Cz + room.Sz / 2 - m + 1e-4f;
    bool Apart(RoverBox a, RoverBox b, float gap) =>
        Math.Abs(a.Cx - b.Cx) >= (a.Sx + b.Sx) / 2 + gap || Math.Abs(a.Cy - b.Cy) >= (a.Sy + b.Sy) / 2 + gap || Math.Abs(a.Cz - b.Cz) >= (a.Sz + b.Sz) / 2 + gap;
    var items = new List<(string Anchor, RoverBox Body)>();
    for (int i = 0; i < RoverRules.SlotCount; i++)
    {
        var (_, kind, anchor) = RoverRules.Slots[i];
        if (kind is RoverSlotKind.Seat or RoverSlotKind.Crate or RoverSlotKind.Tank) continue;
        var (bc, bs) = RoverRules.ItemBody(kind);
        var body = Aabb(anchor, bc, bs);
        var click = Aabb(anchor, RoverRules.ClickBoxCenter(kind), RoverRules.ClickBoxSize(kind));
        Check(RoverRules.PanelOpenings.Any(o => Within(body, o, 0.01f)), $"rover.json: {anchor}'s item (measured) sits inside its recess or the rack, 1 cm clear");
        Check(body.Contains(click.Cx, click.Cy, click.Cz), $"rover.json: {anchor}'s click box is on its item");
        items.Add((anchor, body));
    }
    for (int a = 0; a < items.Count; a++)
        for (int b = a + 1; b < items.Count; b++)
            Check(Apart(items[a].Body, items[b].Body, 0.015f), $"rover.json: {items[a].Anchor} and {items[b].Anchor} are 1.5 cm apart");
    foreach (var bay in RoverRules.Bays)
        foreach (var i in bay.Crates.Concat(bay.Tanks))
        {
            var (_, kind, anchor) = RoverRules.Slots[i];
            var (bc, bs) = RoverRules.ItemBody(kind);
            Check(RoverRules.ExposedFraction(Aabb(anchor, bc, bs), solids) == 1f,
                  $"rover.json: {anchor}'s item is clear of the solid colliders (clickable through its open door)");
            // let go (the wrench's Disconnect), it lands beside its door: clear of the body and of the tyres at full steer
            var an = anchors.GetProperty(anchor);
            var dropped = AabbAt(RoverRules.BayDropPoint(Vec(an.GetProperty("pos"))), Vec(an.GetProperty("rot")), bc, bs);
            var tyres = root.GetProperty("wheels").EnumerateArray().Select(w =>
            {
                float wx = w.GetProperty("pos")[0].GetSingle(), wz = w.GetProperty("pos")[2].GetSingle();
                return RoverBox.FromCorners(Math.Sign(wx) * 1.0f, Math.Sign(wx) * 2.25f, 0f, 1.40f, wz - 0.80f, wz + 0.80f);   // the steered sweep (40 deg)
            }).ToList();
            Check(RoverRules.ExposedFraction(dropped, solids.Concat(tyres).ToList()) == 1f && dropped.Cy - dropped.Sy / 2 >= 0.25f,
                  $"rover.json: {anchor}'s item let go lands clear of the body and the steered tyres, just above the ground");
        }
    // the final review (I2): a player standing beside the rover (eye 1.45-1.75 m, 1.2 m out) aims a wrench at a bay item or
    // the closed door leaf over it: no upgrade's removal trigger may sit in that line of sight (it would take the wrench)
    var upgSolids = upgBoxes.Select(b => b.Box).ToList();
    foreach (var bay in RoverRules.Bays)
        foreach (var i in bay.Crates.Concat(bay.Tanks))
        {
            var (_, kind, anchor) = RoverRules.Slots[i];
            var (bc, bs) = RoverRules.ItemBody(kind);
            var body = Aabb(anchor, bc, bs);
            float sx = Math.Sign(body.Cx);
            var targets = new[] { (body.Cx, body.Cy, body.Cz), (body.Cx, body.Cy - body.Sy / 2 + 0.05f, body.Cz),
                                  (sx * 1.36f, 1.86f, body.Cz), (sx * 1.36f, 2.40f, body.Cz) };     // the item; the leaf's foot and middle
            foreach (var eh in new[] { 1.45f, 1.60f, 1.75f })
            {
                var eye = (sx * 2.55f, eh, body.Cz);
                var hit = targets.Where(t => RoverRules.SegmentBlocked(eye, t, upgSolids)).ToList();
                Check(hit.Count == 0, $"rover.json: from beside the rover (eye {eh:0.00} m) {anchor}'s item and door are in a wrench's reach past every upgrade trigger ({hit.Count} blocked)");
            }
        }
    // the door leaves are triggers (no mass): the centre of mass is the solid colliders' alone (review I6)
    double vol = 0, comX = 0, comY = 0, comZ2 = 0;
    foreach (var sb in solids) { double v = sb.Sx * sb.Sy * sb.Sz; vol += v; comX += v * sb.Cx; comY += v * sb.Cy; comZ2 += v * sb.Cz; }
    var ac = root.GetProperty("autoCom");
    Check(Math.Abs(comX / vol - ac[0].GetSingle()) < 1e-3 && Math.Abs(comY / vol - ac[1].GetSingle()) < 1e-3 && Math.Abs(comZ2 / vol - ac[2].GetSingle()) < 1e-3,
          "rover.json: autoCom is the solid colliders' centre (the trigger door leaves add no mass)");
    // the shocks (exterior rework): one per wheel, upright, three meshes, a spring between its seats
    var wheelNames = root.GetProperty("wheels").EnumerateArray().Select(w => w.GetProperty("name").GetString()).OrderBy(n => n).ToList();
    var shocks = root.TryGetProperty("shocks", out var sj) ? sj.EnumerateArray().ToList() : new List<System.Text.Json.JsonElement>();
    Check(shocks.Select(x => x.GetProperty("wheel").GetString()).OrderBy(n => n).SequenceEqual(wheelNames), "rover.json: a shock on every wheel");
    foreach (var sh in shocks)                                        // (not `s`: a top-level `s` exists)
    {
        var (t, b) = (Vec(sh.GetProperty("top")), Vec(sh.GetProperty("bottom")));
        float len = MathF.Sqrt((t.Item1 - b.Item1) * (t.Item1 - b.Item1) + (t.Item2 - b.Item2) * (t.Item2 - b.Item2) + (t.Item3 - b.Item3) * (t.Item3 - b.Item3));
        var at = sh.GetProperty("springAt");
        Check(t.Item2 > b.Item2 && at.GetArrayLength() == 2 && len - at[0].GetSingle() - at[1].GetSingle() > 0.05f
              && new[] { "body", "rod", "spring" }.All(k => !string.IsNullOrEmpty(sh.GetProperty(k).GetString())),
              $"rover.json: {sh.GetProperty("wheel").GetString()}'s shock stands upright with its body, rod and spring, the spring between its seats");
    }
    // the work lights (the user's check-in 2 request): the side lights shine out and down, the rear lights back and down
    foreach (var (name, sx) in new[] { ("SideLightL1", -1), ("SideLightL2", -1), ("SideLightR1", 1), ("SideLightR2", 1), ("RearLightL", 0), ("RearLightR", 0) })
    {
        bool lit = anchors.TryGetProperty(name, out var la);
        (float X, float Y, float Z) fw = lit ? RoverDashboard.AnchorPoint((0f, 0f, 0f), Vec(la.GetProperty("rot")), (0f, 0f, 1f)) : (0f, 0f, 0f);
        Check(lit && fw.Y < -0.3f && (sx != 0 ? Math.Sign(fw.X) == sx && Math.Abs(fw.X) > 0.5f : fw.Z < -0.5f),
              $"rover.json: {name} shines {(sx != 0 ? "out" : "back")} and down");
    }
    // the front lights (the user's check-in 3 report: the headlights and the roof light bar did not light up): our own
    // spot lights shine ahead (the headlights a little down), and the lenses' glow mesh is exported
    foreach (var name in RoverRules.HeadlightAnchors.Concat(RoverRules.LightBarAnchors))
    {
        bool lit = anchors.TryGetProperty(name, out var la);
        (float X, float Y, float Z) fw = lit ? RoverDashboard.AnchorPoint((0f, 0f, 0f), Vec(la.GetProperty("rot")), (0f, 0f, 1f)) : (0f, 0f, 0f);
        Check(lit && fw.Z > 0.95f && fw.Y <= 0.02f, $"rover.json: {name} shines ahead");
    }
    Check(root.TryGetProperty("glow", out var glow) && glow.TryGetProperty("mesh", out var glowMesh)
          && File.Exists(Path.Combine(Path.GetDirectoryName(roverPath)!, "meshes", glowMesh.GetString() + ".rcm")),
          "rover.json: the front lamps' glow mesh is exported");
}

// ---------------------------------------------------------------- rover.json gate (RoverJson): problems, never exceptions
var roverJsonText = File.ReadAllText(roverPath);
var roverMeshDir = Path.Combine(Path.GetDirectoryName(roverPath)!, "meshes");
List<string> Gate(Action<JObject> spoil)
{
    var j = JObject.Parse(roverJsonText);
    spoil(j);
    try { return RoverJson.Problems(j, m => File.Exists(Path.Combine(roverMeshDir, m + ".rcm"))); }
    catch (Exception e) { return new List<string> { "THREW " + e.GetType().Name }; }
}
Check(Gate(j => { }).Count == 0, "rover.json gate: the exported file builds");
var upgJ = JObject.Parse(roverJsonText);
var upgList = (upgJ["upgrades"]?["rover"] as JArray) ?? new JArray();
Func<string, bool> upgMesh = m => File.Exists(Path.Combine(roverMeshDir, m + ".rcm"));
Check(new[] { "Armour", "Fairings", "Thrusters" }.All(gr => upgList.Any(e => (string)e["group"] == gr))
      && RoverJson.IncompleteUpgradeGroups(upgJ, upgMesh).Count == 0,
      "rover.json: the original rover has parts for all three upgrades, every mesh exported");
Check(upgList.Where(e => (string)e["group"] == "Thrusters").Select(e => (string)e["name"]).OrderBy(n => n).SequenceEqual(new[] { "Nozzle0", "Nozzle1", "Nozzle2", "Nozzle3" })
      && upgList.Where(e => (string)e["group"] == "Thrusters").Select(e => (string)e["mesh"]).Distinct().Count() == 1,
      "rover.json: four thruster pods named Nozzle0-3 (the puff's names), one pod mesh");
Check(upgList.Count(e => (bool?)e["follow"] == true) == 0,
      "rover.json: no riding fenders on the original rover (its fender flares are fixed to the hull: the user, check-in 1)");
var spoilt = JObject.Parse(roverJsonText);
((JObject)((JArray)spoilt["upgrades"]["rover"]).First(e => (string)e["group"] == "Armour"))["mesh"] = "NoSuchMesh";
Check(RoverJson.IncompleteUpgradeGroups(spoilt, upgMesh).SequenceEqual(new[] { "Armour" })
      && Gate(j => ((JObject)((JArray)j["upgrades"]["rover"]).First(e => (string)e["group"] == "Armour"))["mesh"] = "NoSuchMesh").Count == 0,
      "rover.json gate: a missing upgrade mesh refuses only that upgrade; the rover still builds");
Check(RoverJson.IncompleteUpgradeGroups(JObject.Parse("{\"upgrades\": 5}"), upgMesh).Count == 1,
      "rover.json gate: an unreadable upgrades key refuses the upgrades, never throws");
Check(ModFile("CargoPrefabs.Rover.cs").Contains("var upgSkip = RoverJson.IncompleteUpgradeGroups(J, rover.HasMesh);")
      && ModFile("CargoPrefabs.Rover.cs").Contains("if (upgSkip.Contains(\"?\"))")
      && ModFile("CargoPrefabs.Rover.cs").Contains("try { AddUpgradeParts(cr, rover, \"rover\", upgSkip); }")
      && ModFile("CargoPrefabs.cs").Contains("if (t is not JObject p || p[\"group\"] == null) continue;")
      && ModFile("CargoPrefabs.cs").Contains("ICollection<string> skip = null")
      && ModFile("CargoRover.cs").Contains("private const float PodNozzleTop = 0.21f;"),
      "original rover builder: attaches its upgrade parts guarded (a group with a missing mesh left out; an unreadable upgrades key or a throw costs only the upgrades, never the rover: review I1); the puff sits 0.21 m up each pod");
foreach (var (label, spoil, named) in new (string, Action<JObject>, string)[]
{
    ("anchors null", j => j["anchors"] = JValue.CreateNull(), "anchors"),
    ("anchors []", j => j["anchors"] = new JArray(), "anchors"),
    ("glass null", j => j["glass"] = JValue.CreateNull(), "glass"),
    ("body a string", j => j["body"] = "RoverBody", "body"),
    ("parts [\"x\"]", j => j["parts"] = new JArray("x"), "parts"),
    ("body without a mesh", j => ((JObject)j["body"]!).Remove("mesh"), "body"),
    ("a wheel without a tyre", j => ((JObject)j["wheels"]![0]!).Remove("tyre"), "wheel"),
    ("a door without its hinge", j => ((JObject)j["parts"]!.First(t => (string)t["name"]! == "BayDoorL1")).Remove("hinge"), "BayDoorL1"),
    ("a shock without its rod mesh", j => ((JObject)j["shocks"]![0]!).Remove("rod"), "shocks"),
    ("a shock without its spring seats", j => ((JObject)j["shocks"]![0]!).Remove("springAt"), "shock"),
    ("a glow mesh name not a string", j => j["glow"] = new JObject { ["mesh"] = 5 }, "glow"),
})
{
    var p = Gate(spoil);
    Check(p.Count > 0 && !p.Any(x => x.StartsWith("THREW")) && p.Any(x => x.Contains(named)),
          $"rover.json gate: {label} is a named problem, not an exception (got: {string.Join("; ", p)})");
}

// ---------------------------------------------------------------- builder source contracts (Unity code: not runnable here)
var roverBuilderSrc = File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "CargoPrefabs.Rover.cs")));
Check(!roverBuilderSrc.Contains("GetComponentsInChildren<Animator>"),
      "original rover builder keeps the Mk I Animator (Rover.UpdateEachFrame calls BaseAnimator.SetFloat unguarded)");
var entry = roverBuilderSrc.IndexOf("public CargoRover BuildRoverFromJson(", StringComparison.Ordinal);
var entryBody = entry < 0 ? "" : roverBuilderSrc.Substring(entry, roverBuilderSrc.IndexOf("private CargoRover BuildRoverFromJsonCore(", entry, StringComparison.Ordinal) - entry);
int tryAt = entryBody.IndexOf("try", StringComparison.Ordinal), gateAt = entryBody.IndexOf("RoverJson.Problems", StringComparison.Ordinal);
Check(tryAt >= 0 && gateAt > tryAt, "original rover builder: the rover.json gate runs inside the try (never throws)");
Check(entryBody.Contains("DestroyImmediate(_partialRover)") && entryBody.Contains("DestroyImmediate(_partialBlueprint)"),
      "original rover builder: a failed build removes its half-built rover and blueprint");
Check(roverBuilderSrc.Contains("box.center = V3(RoverRules.ClickBoxCenter(kind));"),
      "original rover builder: each click box sits where the tests put it (ClickBoxCenter: a canister's at the rack's mouth; review C1)");
Check(roverBuilderSrc.Contains("RoverRules.ClickAnchor(") && roverBuilderSrc.Contains("RoverRules.ClickBoxSize(") && !roverBuilderSrc.Contains("RoverSlotSize"),
      "original rover builder: click boxes from RoverRules (seats on their cab doors), the sizes the click-box checks test");

string ModFile(string name) { var f = RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", name)); return File.Exists(f) ? File.ReadAllText(f) : ""; }
var habScreenSrc = ModFile("HabStatusScreen.cs");
Check(habScreenSrc.Contains("using static Stationeers.RoverCargo.ScreenKit;") && !habScreenSrc.Contains("static Image PanelAt(")
      && ModFile("ScreenKit.cs").Contains("internal static Image PanelAt("), "screens: the hab screen draws with the shared ScreenKit");

var roverSrc = ModFile("CargoRover.cs");
var dashScreenSrc = ModFile("RoverStatusScreen.cs");
Check(roverSrc.Contains("double stored = sim ? b.PowerStored : b.CurrentPowerPercentage / 100.0 * b.PowerMaximum;")
      && roverSrc.Contains("charged |= sim ? b.PowerStored > 0f : b.CurrentPowerPercentage > 0 || !b.IsEmpty;") && roverSrc.Contains("Remote = !sim"),
      "rover dash inputs: on a remote client the charge comes from the cells' synced percentage and mode (review C1)");
Check(roverSrc.Contains("if (!air) air = anyAir;"), "rover dash inputs: a fitted but empty air canister is reported (empty), not missing (review I1)");
Check(ModFile("RoverStatusScreen.cs").Contains("if (entering) { slow = true; _nextFast = 0f; }") && ModFile("RoverStatusScreen.cs").Contains("if (s.Remote) { _drawW = null; return; }"),
      "rover dash screen: a new driving page is filled in its first frame (review M1); no power-use sampling on clients");
Check(roverSrc.Contains("AddComponent<RoverStatusScreen>().Init(this, ScreenAt)") && roverSrc.Contains("public RoverInputs DashInputs()"),
      "rover: builds its dash screen on each instance that has a Screen face, fed by DashInputs");
Check(dashScreenSrc.Contains("RoverDashboard.Build(") && dashScreenSrc.Contains("RoverDashboard.TapRect(") && dashScreenSrc.Contains("PlaceTargets(RoverPage.Driving, true)"),
      "rover dash screen: draws RoverDashboard, moves the tap targets per page, and falls back to the driving layout on error");

Check(roverBuilderSrc.Contains("TapTarget(") && roverBuilderSrc.Contains("cr.ScreenAt = A[\"Screen\"]") && !roverBuilderSrc.Contains("RoverSwitch("),
      "original rover builder: the controls are tap targets under the Screen anchor, no switch copies");

// the switch-over (sub-project 6): the 2020 path is gone; the original rover is the only rover
var buildAllSrc = ModFile("CargoPrefabs.cs");
var pluginSrc = ModFile("Plugin.cs");
var modSources = Directory.GetFiles(Path.GetDirectoryName(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "Plugin.cs"))), "*.cs");
var stale = new[] { "layout.json", "extract_rover_cargo", "TryLoad(", "PickRover", "Rover2020", "OriginalRover", "RoverComOffsetY",
                    "RoverCenterOfMassOffset", "BallHitch", "AddHitchReceiver", "BuildRover(" };
var hits = modSources.SelectMany(f => stale.Where(s => File.ReadAllText(f).Contains(s)).Select(s => Path.GetFileName(f) + ": " + s)).ToList();
Check(hits.Count == 0, "switch-over: no 2020 path left in the mod's source (" + string.Join(", ", hits) + ")");
Check(buildAllSrc.Contains("BuildRoverFromJson(mk1, original)") && buildAllSrc.Contains("RoverAssets is missing or unreadable"),
      "switch-over: BuildAll always builds the original rover and says why when it cannot");
Check(pluginSrc.Contains("\"RoverAssets\"") && pluginSrc.Contains("the chase camera still works") && pluginSrc.Contains("finally { foreach (var line in builder?.Log"),
      "startup: without RoverAssets one clear error, the chase camera kept, the builder log always printed");
Check(ModFile("CargoLayout.cs").Contains("catch (Exception ex) { error = \"unreadable \" + file"), "switch-over: a truncated json file is an error message, not an exception");
foreach (var j in new[] { Path.Combine("TrailerAssets", "trailer.json") })
{
    var tj = JObject.Parse(File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", j))));
    Check(tj["roverHitchMesh"] == null && tj["upgrades"]?["rover"] == null, "trailer.json: no 2020 receiver and no 2020 rover upgrade parts");
}

// covered bays (sub-project 3)
var bayRuleSrc = ModFile("TrailerBedRule.cs");
roverSrc = ModFile("CargoRover.cs");
roverBuilderSrc = ModFile("CargoPrefabs.Rover.cs");
Check(roverBuilderSrc.Contains("doorHinges[di] = hinge") && roverBuilderSrc.Contains("cr.BayDoorColliders = doorCols.ToList()") && roverBuilderSrc.Contains("t.gameObject.layer = go.layer"),
      "bays: the builder hangs each door leaf on its hinge with a solid collider on the rover's layer");
Check(roverSrc.Contains("DoorAttack(attack, doAction) ?? UpgradeAttack(attack, doAction)") && roverSrc.Contains("if (!IsCursor) UpdateDoors();")
      && roverSrc.Contains("st.StateName == RoverRules.Doors[d].Action && DoorFlag(d)") && roverSrc.Contains("float open = first ? target :"),
      "bays: a wrench toggles a door, doors turn every frame, saved states restored and shown without an opening swing");
Check(roverSrc.Contains("StormRules.UpgradeFor(held ? held.PrefabName : null) != null) return null;   // fitting an upgrade"),
      "bays: a wrench with an upgrade's material in the other hand fits the upgrade, not the door");
Check(bayRuleSrc.Contains("OriginalBayAttach(") && bayRuleSrc.Contains("RoverRules.PickBaySlot(") && bayRuleSrc.Contains("OnServer.MoveToSlot("),
      "bays: Rover.Attach loads the original rover through its open doors (PickBaySlot)");
Check(roverBuilderSrc.Contains("leafBox.isTrigger = true;") && ModFile("UpgradeCursor.cs").Contains("cr.IsBayDoorCollider(selectedCollider)")
      && ModFile(Path.Combine("tools", "blender_build_rover.py")).Contains("\"autoCom\": auto_com3(colliders),"),
      "bays: the door leaves are triggers (no shove, no mass; the wrench is let onto them; review I6)");
Check(roverSrc.Contains("BayDoorHinges.Count > 0 && d < RoverRules.Doors.Length"), "bays: only a rover with bay doors restores door states (the hab's buttons are its own; review M4)");
Check(roverSrc.Contains("if (UpgradePartsOf(up) == null) return fit.Fail("), "upgrades: a vehicle without the parts for an upgrade refuses to fit it (the original rover until sub-project 4)");
Check(ModFile("BayRelease.cs").Contains("RoverRules.BayDropPoint(") && pluginSrc.Contains("nameof(BayRelease.BeforeMoveToWorld)"),
      "bays: an item let go from a bay is set down beside its door (DynamicThing.MoveToWorld prefix; review I5)");
Check(roverBuilderSrc.Contains("foreach (JObject sh in (list as JArray)") && roverBuilderSrc.Contains("var rod = Child(armT,")
      && roverBuilderSrc.Contains("var spring = Child(body,") && roverBuilderSrc.Contains("target.ShockSpringFit = s.Fit;")
      && roverBuilderSrc.Contains("BuildShocks(rover, J[\"shocks\"],") && roverBuilderSrc.Contains("SetShocks(cr, shocks);"),
      "original rover builder: each shock's body hangs on the rover at its upper mount, its rod on its wheel's arm, its spring on the body");
Check(roverSrc.Contains("AnimateSuspensionArms();\n        AnimateShocks();") || roverSrc.Contains("AnimateSuspensionArms();\r\n        AnimateShocks();"),
      "rover: the shocks follow the arms every frame (skipped when occluded, as the arms)");
Check(roverSrc.Contains("SetLights(SideLights, live && WorkLightOn(0));") && roverSrc.Contains("SetLights(RearLights, live && WorkLightOn(1));")
      && roverSrc.Contains("(SideLights.Count > 0 || RearLights.Count > 0) && st.StateName == RoverRules.WorkLights[k]"),
      "rover: the work lights follow Button13/14 while the rover is on, and their states come back from a save (not on the hab)");
Check(roverBuilderSrc.Contains("cr.SideLights = WorkLights(A, RoverRules.SideLightAnchors") && roverBuilderSrc.Contains("cr.RearLights = WorkLights(A, RoverRules.RearLightAnchors")
      && roverBuilderSrc.Contains("(float?)F[\"WorkLightSpot\"]"),
      "original rover builder: a spot light at every work lamp (its spot angle from rover.json), its lens lit with it");
Check(roverSrc.Contains("Show(SideLampGlow, live && WorkLightOn(0));") && roverSrc.Contains("Show(RearLampGlow, live && WorkLightOn(1));"),
      "rover: the work lamps' lenses glow while their lights are on");
Check(roverBuilderSrc.Contains("cr.HeadLights = HeadLamps(A, RoverRules.HeadlightAnchors") && roverBuilderSrc.Contains("HeadLamps(A, RoverRules.LightBarAnchors")
      && roverBuilderSrc.Contains("(float?)F[\"HeadlightSpot\"]") && roverBuilderSrc.Contains("(float?)F[\"LightBarSpot\"]")
      && roverBuilderSrc.Contains("t.gameObject.SetActive(false)"),
      "original rover builder: its own spot lights at the headlights and the roof light bar (spot angles from rover.json), the Mk I's lights off");
Check(ModFile("CrateLids.cs").Contains("RoverRules.LidBlock(") && ModFile("CrateLids.cs").Contains("InteractableType.Open")
      && pluginSrc.Contains("nameof(CrateLids.BeforeInteractWith)"),
      "bays: opening a crate's lid in a bay is refused where it has no room (a Thing.InteractWith prefix; closing is always allowed)");
Check(roverSrc.Contains("RoverRules.LidsToClose(") && roverSrc.Contains("OnServer.Interact(crate.InteractOpen, 0)") && roverSrc.Contains(", IsBayDoorOpen))")
      && ModFile("CrateLids.cs").Contains("rover.IsBayDoorOpen"),
      "bays: the rover (the server) closes crate lids open where they have no room");
Check(roverBuilderSrc.Contains("cr.HeadLampGlow = ") && roverSrc.Contains("Show(HeadLampGlow, live && Button1 == 1);"),
      "rover: the front lamps' lenses (nose bars, roof light bar) glow while the headlights are on");
Check(roverSrc.Contains("public class CargoRover : Rover, IInternalConditioner, ICircuitHolder, IPowered")
      && roverSrc.Contains("public new void OnPowerTick()") && roverSrc.Contains("base.OnPowerTick();")
      && roverSrc.Contains("RoverRules.WorkLightDraw(SideLights.Count > 0 && WorkLightOn(0), RearLights.Count > 0 && WorkLightOn(1))")
      && roverSrc.Contains("RoverRules.DrainSlot("),
      "rover: its power tick (IPowered re-implemented: Rover's is not virtual) runs vanilla's, then takes the work lights' draw while it is on");
// the seated head (the user's check-in 3 report: in third person it spun round and upside down)
Check(Near(RoverRules.WrapDeg(300f), -60f) && Near(RoverRules.WrapDeg(-190f), 170f) && Near(RoverRules.WrapDeg(540f), 180f)
      && RoverRules.CapTurn(160f) == RoverRules.HeadTurnMaxDeg && RoverRules.CapTurn(-120f) == -RoverRules.HeadTurnMaxDeg
      && RoverRules.CapTurn(45f) == 45f && RoverRules.HeadTurnMaxDeg <= 80f,
      "seated head: a turn wraps to +-180 and is capped either way (at most 80 deg)");
Check(ModFile("SeatedHead.cs").Contains("ParentSlot?.Parent is not CargoRover") && ModFile("SeatedHead.cs").Contains("RoverRules.CapTurn(")
      && pluginSrc.Contains("nameof(SeatedHead.BeforeIkSolveHead)") && pluginSrc.Contains("nameof(SeatedHead.AfterIkSolveHead)"),
      "seated head: Human.IkSolveHead's turn is capped for anyone seated in our vehicles");
// the trailers' running gear (the user, 2026-10-04: no 2020 tyres or arms on the trailers; the rover's shocks on them too)
foreach (var tj in new[] { Path.Combine("TrailerAssets", "trailer.json"), Path.Combine("HabAssets", "hab.json") })
{
    var text = ModFile(tj);
    var t = JObject.Parse(text);
    var roverMesh = (Func<JToken, bool>)(n => n != null && File.Exists(Path.Combine(roverMeshDir, (string)n + ".rcm")));
    var wheelNames = t["wheels"].Select(w => (string)w["name"]).ToList();
    var shocks = (t["shocks"] as JArray) ?? new JArray();
    Check(!text.Contains("RoverCargo/") && t["wheelCollider"] == null
          && t["wheels"].All(w => roverMesh(w["tyre"]) && roverMesh(w["arm"])),
          $"{tj}: the wheels are the original rover's tyres and arms (RoverAssets meshes, rover.json's wheel numbers), nothing from the 2020 rover");
    Check(shocks.Count == wheelNames.Count && wheelNames.All(n => shocks.Count(s => (string)s["wheel"] == n) == 1)
          && shocks.All(s => roverMesh(s["body"]) && roverMesh(s["rod"]) && roverMesh(s["spring"]) && s["springAt"] is JArray { Count: 2 }),
          $"{tj}: a coil-over shock (the rover's) at every wheel");
}
var trailerClassSrc = ModFile("CargoTrailer.cs");
var habClassSrc = ModFile("CargoHab.cs");
Check(trailerClassSrc.Contains("HabRules.RigHold(") && trailerClassSrc.Contains("HabRules.ParkHold(") && trailerClassSrc.Contains("SetRoverFrozen(")
      && !habClassSrc.Contains("HabRules.ParkHold(") && !habClassSrc.Contains("void SetRoverFrozen("),
      "rig hold: the parked hold and the rover freeze live in CargoTrailer (both trailers), not only in the hab");
Check(habClassSrc.Contains("override bool OnLegs"), "rig hold: the hab tells the shared hold when it is deployed");
// the user's 0.2.2 report: fell through every inch of the hitched hab. Hitching ignores collisions between everything in
// the trailer and everything in the rover, and a player seated in the rover (or asleep in a hab bunk) is a child of it
var ignoreSrc = trailerClassSrc[trailerClassSrc.IndexOf("private void IgnoreCollisions(")..];
ignoreSrc = ignoreSrc[..ignoreSrc.IndexOf("\n    }")];
Check(trailerClassSrc.Contains("static bool HitchIgnores(Collider c) => c && !c.isTrigger && !c.GetComponentInParent<Entity>()")
      && ignoreSrc.Contains("if (!HitchIgnores(a)) continue;") && ignoreSrc.Contains("if (!HitchIgnores(b)) continue;"),
      "hitch: ignores collisions only between the vehicles' own parts and cargo, never a person (seated or asleep)");
// the user's choice (b): the hab's front corner brushed the rover's upgrade tail plate at 70 deg (clear at 65)
Check(trailerClassSrc.Contains("angularYLimit = new SoftJointLimit { limit = HitchYawLimit }") && trailerClassSrc.Contains("virtual float HitchYawLimit => 70f")
      && habClassSrc.Contains("override float HitchYawLimit => 65f"),
      "hitch swing: cargo trailer 70 deg, hab 65 deg (clear of the fitted upgrades)");
Check(File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "tools", "blender_rover_checks.py"))).Contains("(\"hab\", \"GEO_TrailerHab\", g.HAB_ORIGIN_Y, 3.9 + g.HAB_EXT, 65)"),
      "hitch swing: the model's tow check sweeps the hab to its own 65 deg limit");
// the user's report (0.2.1): players slid down the hab stairs. The player's body is "Zero Friction" (combine Minimum),
// so a collider with no material gives 0 friction; the game's stairs carry "Stairs" (static 1, dynamic 0.3, Maximum)
Check(buildAllSrc.Contains("StairsMaterial()") && buildAllSrc.Contains("c.sharedMaterial = stairs")
      && buildAllSrc.Contains("staticFriction = 1f") && buildAllSrc.Contains("dynamicFriction = 0.3f") && buildAllSrc.Contains("PhysicMaterialCombine.Maximum"),
      "hab stairs: the game's Stairs physics material (or the same values), so players stand on them");
// ...and with grip the player could not climb them at all: the game's slope walking and step-up both skip colliders on
// a rigidbody (any vehicle), so the deployed (frozen) hab's stairs are a static copy in the world, like the game's own
Check(habClassSrc.Contains("void SyncStairs(") && habClassSrc.Contains("new GameObject(\"RoverCargoHabStairs\")")
      && !habClassSrc[habClassSrc.IndexOf("void SyncStairs(")..].Split("\n    }")[0].Contains("SetParent(transform")
      && habClassSrc.Contains("Destroy(_stairs)") && habClassSrc.Contains("SyncStairs(false)"),
      "hab stairs: deployed, a static world copy of the stair collider (no rigidbody), removed on stow and on destroy");
// Stationpedia (the user's 0.2.1 report): the rover and both trailers showed the game's 2020 rover picture and the rover
// the game's 2020 text. Every vehicle has its own rendered picture, and every thing we register its own English page
var modDir = Path.GetDirectoryName(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "Plugin.cs")));   // both layouts
foreach (var (assets, prefab) in new[] { ("RoverAssets", "RoverCargo"), ("TrailerAssets", "TrailerCargo"), ("HabAssets", "TrailerHab") })
    Check(File.Exists(Path.Combine(modDir, assets, "textures", prefab + ".png")), $"stationpedia: {assets}/textures/{prefab}.png (our rendered picture)");
Check(!buildAllSrc.Contains("Thumbnail(RoverName)") && !roverBuilderSrc.Contains("Thumbnail(RoverName)")
      && !buildAllSrc.Contains("mk1.Thumbnail") && buildAllSrc.Contains("VehicleThumbnail("),
      "stationpedia: vehicle pictures are ours only (never the game's 2020 RoverCargo picture or the Mk I's)");
var pediaXml = System.Xml.Linq.XDocument.Load(Path.Combine(modDir, "GameData", "Language", "english.xml"));
var pages = pediaXml.Descendants("RecordThing").ToDictionary(r => (string)r.Element("Key"), r => (Name: (string)r.Element("Value"), Text: (string)r.Element("Description")));
foreach (var key in new[] { "RoverCargo", "TrailerCargo", "TrailerHab", "ItemKitRoverFrame", "StructureRover", "ItemKitTrailerCargo", "StructureTrailerCargo", "ItemKitTrailerHab", "StructureTrailerHab" })
    Check(pages.TryGetValue(key, out var pg) && !string.IsNullOrWhiteSpace(pg.Name) && (pg.Text?.Length ?? 0) > 80, $"stationpedia: {key} has its own name and page");
Check(pages.TryGetValue("RoverCargo", out var rp) && rp.Text.Contains("Duct tape") && rp.Text.Contains("no storm damage") && rp.Text.Contains("drill"),
      "stationpedia: the rover's page says how to repair, armour and take it apart");
Check(pages.TryGetValue("TrailerHab", out var habPage) && habPage.Text.Contains("deploy panel") && habPage.Text.Contains("AIR SUPPLY") && habPage.Text.Contains("WASTE"),
      "stationpedia: the hab's page says how to deploy it and where its air comes from");
var trailerBuilderSrc = buildAllSrc[buildAllSrc.IndexOf("public CargoTrailer BuildTrailer(")..];
trailerBuilderSrc = trailerBuilderSrc[..trailerBuilderSrc.IndexOf("\n    private ")];
Check(!trailerBuilderSrc.Contains("_layout") && trailerBuilderSrc.Contains("RunningGear(") && trailerBuilderSrc.Contains("BuildShocks(")
      && roverBuilderSrc.Contains("RunningGear(") && roverBuilderSrc.Contains("BuildShocks(")
      && buildAllSrc.Contains("BuildTrailer(mk1, trailer, original)") && buildAllSrc.Contains("BuildTrailer(mk1, hab, original,"),
      "trailer builder: the trailers' tyres, arms and shocks come from RoverAssets, built by the rover's own helpers");

// the user's build 11/12 report: black parts turned white whenever the headlights were on, day or night. The Mk I's
// Animator (kept) plays RoverHeadlightsOn, which swaps a material slot of the ROOT object's MeshRenderer (its lamp glass):
// the body must render on a child, never the root
var bodyBuilder = ModFile("CargoPrefabs.Rover.cs");
Check(bodyBuilder.Contains("var bodyNode = Child(go.transform, \"Body\");") && bodyBuilder.Contains("bodyNode.gameObject.AddComponent<MeshRenderer>().sharedMaterials = RoverMaterials(J[\"body\"][\"materials\"]);")
      && !bodyBuilder.Contains("go.AddComponent<MeshRenderer>()"),
      "original rover builder: the body renders on a child node (the Mk I's headlights-on clip swaps the root renderer's material slot)");

// ---------------------------------------------------------------- build stages: the json "frame" (plan 2026-10-08)
var stageCosts = new Dictionary<string, (string Json, int States, (string, int, string, int, string)[] Steps)>
{   // (entry, qty, entry2, qty2, exit) per state; the spec's table
    ["rover"] = (Path.Combine("RoverAssets", "rover.json"), 3, new[] {
        ("@kit", 1, null, 0, "ItemDrill"), ("ItemWeldingTorch", 0, "ItemSteelSheets", 10, "ItemAngleGrinder"),
        ("ItemPlasticSheets", 10, "ItemElectronicParts", 4, "ItemCrowbar") }),
    ["cargo"] = (Path.Combine("TrailerAssets", "trailer.json"), 3, new[] {
        ("@kit", 1, null, 0, "ItemDrill"), ("ItemWeldingTorch", 0, "ItemSteelSheets", 10, "ItemAngleGrinder"),
        ("ItemWrench", 0, "ItemPlasticSheets", 10, "ItemCrowbar") }),
    ["hab"] = (Path.Combine("HabAssets", "hab.json"), 4, new[] {
        ("@kit", 1, null, 0, "ItemDrill"), ("ItemWeldingTorch", 0, "ItemSteelSheets", 20, "ItemAngleGrinder"),
        ("ItemWrench", 0, "ItemPlasticSheets", 20, "ItemCrowbar"), ("ItemWrench", 0, "ItemElectronicParts", 8, "ItemCrowbar") }),
};
var frameTools = new HashSet<string> { "ItemWeldingTorch", "ItemWrench", "ItemDrill", "ItemCrowbar", "ItemAngleGrinder" };
foreach (var (fv, (rel, nStates, steps)) in stageCosts)
{
    var fdDir = Path.GetDirectoryName(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", rel)));
    var fdJson = JObject.Parse(File.ReadAllText(Path.Combine(fdDir, Path.GetFileName(rel))));
    bool FdMesh(string m) => File.Exists(Path.Combine(fdDir, "meshes", m + ".rcm"));
    bool FdPng(string t) => File.Exists(Path.Combine(fdDir, "textures", t + ".png"));
    var (fdSpec, fdProblems) = FrameData.Read(fdJson, FdMesh, FdPng);
    Check(fdProblems.Count == 0, $"{fv} frame: no problems ({string.Join("; ", fdProblems)})");
    Check(fdSpec.States.Count == nStates, $"{fv} frame: {nStates} states");
    for (int i = 0; i < nStates; i++)
    {
        var fs = fdSpec.States[i]; var (fe, feq, fe2, feq2, fx) = steps[i];
        Check(fs.Entry.Prefab == fe && fs.Entry.Quantity == feq && (fe2 == null ? fs.Entry2 == null : fs.Entry2?.Prefab == fe2 && fs.Entry2.Quantity == feq2)
              && fs.Exit.Prefab == fx, $"{fv} frame state {i}: {fe} x{feq} + {fe2} x{feq2}, undone with {fx}");
        // review focus 2: deconstruction refunds the materials, never a tool
        Check((!frameTools.Contains(fs.Entry.Prefab) || fs.Entry.Quantity == 0) && (fs.Entry2 == null || !frameTools.Contains(fs.Entry2.Prefab) || fs.Entry2.Quantity == 0),
              $"{fv} frame state {i}: tools are held, not consumed or refunded");
    }
    Check(fdSpec.States[^1].Mesh == (string)fdJson["body"]["mesh"], $"{fv} frame: the last state is the vehicle's body mesh");
    // broken copies: each costs a named problem, none throws
    var noFrame = (JObject)fdJson.DeepClone(); noFrame.Remove("frame");
    Check(FrameData.Read(noFrame, FdMesh, FdPng).Problems.Any(p => p.Contains("frame")), $"{fv} frame missing: named");
    Check(FrameData.Read(fdJson, m => m != fdSpec.States[0].Mesh, FdPng).Problems.Any(p => p.Contains(fdSpec.States[0].Mesh)), $"{fv} frame: missing stage mesh named");
    Check(FrameData.Read(fdJson, FdMesh, t => false).Problems.Any(p => p.Contains("thumbnail")), $"{fv} frame: missing thumbnail named");
    var badType = (JObject)fdJson.DeepClone(); badType["frame"]["states"][1]["entry2"] = "ItemSteelSheets";
    Check(FrameData.Read(badType, FdMesh, FdPng).Problems.Any(p => p.Contains("state 1")), $"{fv} frame: a malformed entry is named, not thrown");
    var noStates = (JObject)fdJson.DeepClone(); noStates["frame"]["states"] = new JArray();
    Check(FrameData.Read(noStates, FdMesh, FdPng).Problems.Count > 0, $"{fv} frame: no states is a problem");
}
// review focus 1: an old save's part-built StructureRover keeps its index (the Mk I and the 2020 frame have 3 states)
var roverFrame = FrameData.Read(JObject.Parse(File.ReadAllText(roverPath)), m => true, t => true).Spec;
Check(roverFrame.States.Count == 3 && roverFrame.States[0].Entry.Prefab == FrameData.KitToken,
      "rover frame: exactly 3 states (old saves' indices stay valid), state 0 placed from the kit");
// review focus 4: the outcome per vehicle
Check(FrameData.Decide(true, true) == FrameOutcome.Staged && FrameData.Decide(true, false) == FrameOutcome.Legacy
      && FrameData.Decide(false, true) == FrameOutcome.Staged && FrameData.Decide(false, false) == FrameOutcome.NotPrintable,
      "frame outcome: the rover always printable (legacy frame without data), a trailer without data spawnable only");
Check(FrameData.ItemNames(roverFrame).OrderBy(n => n).SequenceEqual(new[] { "ItemAngleGrinder", "ItemCrowbar", "ItemDrill",
      "ItemElectronicParts", "ItemPlasticSheets", "ItemSteelSheets", "ItemWeldingTorch" }), "frame item names: every prefab but the kit token, once");

// ---------------------------------------------------------------- build stages: recipes and names (spec table)
var recipes = System.Xml.Linq.XDocument.Load(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "GameData", "electronics.xml")));
Dictionary<string, string> RecipeOf(string prefab)
{
    var rd = recipes.Descendants("RecipeData").FirstOrDefault(r => r.Parent?.Name.LocalName == "ElectronicsPrinterRecipes" && (string)r.Element("PrefabName") == prefab);
    if (rd == null || (string)rd.Element("RecipeTier") != "TierTwo") return null;
    return rd.Element("Recipe").Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
}
string Metals(Dictionary<string, string> r) => r == null ? "missing" : string.Join(",", r.Where(kv => kv.Key is not ("Time" or "Energy")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
var roverMetals = "Constantan=10,Copper=25,Electrum=15,Silicon=10,Steel=120";
Check(Metals(RecipeOf("ItemKitRoverFrame")) == roverMetals, "recipe: rover kit 120 steel, 25 copper, 15 electrum, 10 constantan, 10 silicon, Electronics Printer T2");
Check(Metals(RecipeOf("ItemKitTrailerCargo")) == "Copper=20,Steel=90", "recipe: trailer kit 90 steel, 20 copper (no electronics metals), T2");
Check(Metals(RecipeOf("ItemKitTrailerHab")) == roverMetals, "recipe: hab kit as the rover kit, T2");
var lang = System.Xml.Linq.XDocument.Load(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "GameData", "Language", "English.xml")));
foreach (var key in new[] { "ItemKitTrailerCargo", "ItemKitTrailerHab", "StructureTrailerCargo", "StructureTrailerHab" })
    Check(lang.Descendants("RecordThing").Any(r => (string)r.Element("Key") == key && !string.IsNullOrWhiteSpace((string)r.Element("Value"))),
          $"language: {key} has a name");
foreach (var key in new[] { "ItemKitRoverFrame", "StructureRover" })
    Check(lang.Descendants("RecordThing").Any(r => (string)r.Element("Key") == key && !string.IsNullOrWhiteSpace((string)r.Element("Value"))),
          $"language: {key} has a name");
Check(ModFile("CargoPrefabs.cs").Contains("kit.Thumbnails = Enumerable.Repeat(kit.Thumbnail")
      && ModFile("CargoPrefabs.Frames.cs").Contains("frame.Thumbnails = Enumerable.Repeat(frame.Thumbnail"),
      "icons: every paint colour shows our kit and frame icon, never the Mk I's");

// ---------------------------------------------------------------- build stages: the builder (plan decisions 6, 7, 10)
var framesSrc = ModFile("CargoPrefabs.Frames.cs");
var buildAllFrames = ModFile("CargoPrefabs.cs");
Check(framesSrc.Contains("FrameData.Read(") && framesSrc.Contains("FrameData.Decide(") && framesSrc.Contains("FrameOutcome.Legacy"),
      "frame builder: reads the json frame and decides per vehicle (the rover falls back to the legacy frame)");
Check(framesSrc.Contains("\"initialDrawData\"") && framesSrc.Contains("BuildStateRenderMode.OnMyState"),
      "frame builder: states render through their own renderers (draw data cleared), one state at a time");
Check(framesSrc.Contains("\"Footprint\"") && framesSrc.IndexOf("\"Footprint\"") < framesSrc.IndexOf("CachePrefabBounds()"),
      "frame builder: the footprint renderer spans every state before the bounds and grid footprint are computed");
// review focus 5: a cloned state shares nothing with its template
Check(framesSrc.Contains("bs.StateMeshes = new List<Mesh>") && framesSrc.Contains("bs.Colliders = new List<Collider>")
      && framesSrc.Contains("bs.LinkedGameObjects = new List<GameObject>()") && framesSrc.Contains("bs.Interactables = new List<Interactable>()")
      && framesSrc.Contains("bs.Tool = new ToolUse"),
      "frame builder: every state gets its own lists and tool (the hab's 4th state is a clone)");
Check(buildAllFrames.Contains("TryFrame(") && buildAllFrames.Contains("TrailerFrameName") && buildAllFrames.Contains("HabFrameName"),
      "BuildAll: a frame and kit per vehicle");
Check(framesSrc.Contains("catch (Exception ex)") && framesSrc.Contains("isRover") && framesSrc.Contains("BuildLegacyFrame("),
      "frame builder: a failed staged rover frame falls back to the legacy frame; a failed trailer frame costs only its kit");

// final review (Important 1, 2): the placement ghost is the frame's own chassis, not the Mk I's outline; the grid
// footprint is frozen from every state's mesh widened by half a cell (the game's own cell maths, ratio 1), so neither
// the 0.9 BoundsGridRatio nor a later bounds recompute shrinks it below the vehicle
var framesSrc2 = ModFile("CargoPrefabs.Frames.cs");
Check(framesSrc2.Contains("frame.Blueprint = null;"), "frame builder: no Mk I blueprint outline (the cursor ghosts the chassis state)");
Check(framesSrc2.Contains("frame.BoundsGridRatio = 1f;") && framesSrc2.Contains("frame.ForceGridBounds = new List<Grid3>(")
      && framesSrc2.IndexOf("frame.ForceGridBounds = new List<Grid3>(") > framesSrc2.IndexOf("CachePrefabBounds()"),
      "frame builder: the grid footprint is frozen from the widened state meshes after the bounds are cached");
Check(ModFile("CargoPrefabs.Frames.cs").Contains("new DrawData { materials = new Material[0] }"),
      "frame builder: cleared draw data keeps an empty materials array (Structure.Awake reads its Length)");
// ---------------------------------------------------------------- vehicle teardown (the user, check-in 3): a finished
// vehicle comes apart in reverse: the drill takes it back to its frame at the stage before last (the last step's
// materials back, contents dropped), then the frame's own stages down to its kit (never the Mk I kit)
Check(FrameData.TeardownState(3) == 1 && FrameData.TeardownState(4) == 2, "teardown: the frame comes back at the stage before last");
Check(FrameData.TeardownRefusal(towing: false, hitched: false, habOut: false, speed: 0f) == null, "teardown: a parked, unhitched vehicle comes apart");
Check(FrameData.TeardownRefusal(towing: true, hitched: false, habOut: false, speed: 0f)?.Contains("Unhitch") == true
      && FrameData.TeardownRefusal(towing: false, hitched: true, habOut: false, speed: 0f)?.Contains("Unhitch") == true,
      "teardown: refused while towing or hitched (unhitch first)");
Check(FrameData.TeardownRefusal(towing: false, hitched: false, habOut: true, speed: 0f)?.Contains("Stow") == true, "teardown: refused while the hab is deployed");
Check(FrameData.TeardownRefusal(towing: false, hitched: false, habOut: false, speed: FrameData.TeardownMaxSpeed + 0.1f)?.Contains("Stop") == true
      && FrameData.TeardownRefusal(towing: false, hitched: false, habOut: false, speed: FrameData.TeardownMaxSpeed - 0.1f) == null,
      "teardown: refused while moving");
var roverSrcT = ModFile("CargoRover.cs");
var teardown = roverSrcT.Contains("TeardownAttack(") ? roverSrcT[roverSrcT.IndexOf("protected DelayedActionInstance TeardownAttack(")..] : "";
Check(roverSrcT.Contains("?? TeardownAttack(attack, doAction) ?? base.AttackWith(attack, doAction)"),
      "teardown: every Cargo vehicle's AttackWith tries the teardown before the base game's one-step deconstruct");
Check(teardown.Contains("Constructor.SpawnConstruct(") && teardown.Contains("UpdateBuildStateAndVisualizer(")
      && teardown.IndexOf("Constructor.SpawnConstruct(") < teardown.IndexOf(".Deconstruct(")
      && teardown.IndexOf(".Deconstruct(") < teardown.IndexOf("OnServer.Destroy(this)")
      && teardown.Contains("PlayerMoveToWorld()"),
      "teardown: the frame is spawned first, then the last step refunded and the contents dropped, then the vehicle removed");
var framesSrcT = ModFile("CargoPrefabs.Frames.cs");
Check(framesSrcT.Contains("cr.TeardownFrame = frame") && framesSrcT.Contains("ExitTool = new ToolUse"),
      "teardown: the staged frame is the vehicle's teardown target; no vehicle keeps the Mk I's kit refund");
// the user's paint test (build 16): the cargo trailer's body would not take paint. The Mk I's Animator (kept) writes
// material slots 1 and 3 of the ROOT renderer every frame, so the trailers' body must render on a child too
var trailerBody = ModFile("CargoPrefabs.cs");
trailerBody = trailerBody[trailerBody.IndexOf("public CargoTrailer BuildTrailer(")..];
trailerBody = trailerBody[..trailerBody.IndexOf("\n    private ")];
Check(trailerBody.Contains("var bodyNode = Child(go.transform, \"Body\");") && trailerBody.Contains("bodyNode.gameObject.AddComponent<MeshRenderer>()")
      && !trailerBody.Contains("var body = go.AddComponent<MeshRenderer>();"),
      "trailer builder: the body renders on a child node (the Mk I Animator writes the root renderer's material slots)");
// ---------------------------------------------------------------- Workshop prep: About and README (sub-project 6)
var about = System.Xml.Linq.XDocument.Load(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "About", "About.xml"))).Root;
var pluginVersion = System.Text.RegularExpressions.Regex.Match(ModFile("Plugin.cs"), "Version = \"([0-9.]+)\"").Groups[1].Value;
Check((string)about.Element("Version") == "0.2.5" && pluginVersion == "0.2.5", "about: version 0.2.5 in About.xml and the plugin");
Check((string)about.Element("Author") == "BillBrasky" && (string)about.Element("Name") == "Rover (Cargo) Returns"
      && (string)about.Element("ModID") == "stationeers.rovercargo", "about: name, author and mod id");
Check(!((string)about.Element("Description")).Contains("extract", StringComparison.OrdinalIgnoreCase)
      && !File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "README.md"))).Contains("extract_rover_cargo"),
      "about and README: no extraction steps");
// the Workshop item (published 2026-10-08): the game reads About.xml with XmlArray("Tags")/XmlArrayItem("Tag") and
// updates the item named by WorkshopHandle; without it the next Publish would create a second item
Check(about.Element("Tags")?.Elements("Tag").Count() >= 1 && !about.Element("Tags").Elements("string").Any(), "about: tags as <Tag> (the game's format)");
Check((string)about.Element("WorkshopHandle") == "3816061059", "about: the Workshop item's handle, so Publish updates it");
// storm wiring (Workshop feedback 2026-10-09: StormDamage 0 still took damage; armour should make the vehicles immune)
var stormRover = ModFile("CargoRover.cs");
var stormPrefabs = ModFile("CargoPrefabs.cs") + ModFile("CargoPrefabs.Rover.cs");
Check(stormRover.Contains("public override bool CanBeWeathered() =>") && stormRover.Contains("StormRules.Weathered(HasUpgrade(Upgrade.Armour), StormDamageSetting)")
      && stormRover.Contains("StormRules.StormDamageMultiplier(HasUpgrade(Upgrade.Armour), StormDamageSetting)")
      && !stormPrefabs.Contains("WeatherDamageScale *= _settings.StormDamage") && ModFile("Plugin.cs").Contains("CargoRover.StormDamageSetting = "),
      "storm: armour and StormDamage are checked at every storm tick on the rover and both trailers, not baked into the prefab");
Check(stormRover.Contains("Storm armour fitted (no storm damage)"), "storm: the hover text says armour stops storm damage");

var aboutDesc = (string)about.Element("Description");
var readmeText = string.Join(" ", File.ReadAllText(RepoFile(Path.Combine("mods", "Stationeers.RoverCargo", "README.md"))).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
Check(aboutDesc.Contains("Requires:") && aboutDesc.Contains("BepInEx") && aboutDesc.Contains("StationeersLaunchPad")
      && readmeText.Contains("BepInEx") && readmeText.Contains("StationeersLaunchPad"), "docs: BepInEx and StationeersLaunchPad named as requirements");
Check(readmeText.Contains("duct tape", StringComparison.OrdinalIgnoreCase) && aboutDesc.Contains("duct tape", StringComparison.OrdinalIgnoreCase) && readmeText.Contains("no storm damage"), "docs: duct tape repairs, armour stops storm damage");
Console.WriteLine($"HabTests: {checks} checks passed");
