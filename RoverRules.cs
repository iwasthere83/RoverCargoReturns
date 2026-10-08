using System;
using System.Collections.Generic;

namespace Stationeers.RoverCargo;

/// <summary>What an original-rover slot holds (CargoPrefabs.Rover.cs maps it to the game's Slot.Class and flags).</summary>
public enum RoverSlotKind { Seat, Filter, Chip, Air, Waste, Battery, Crate, Tank }

/// <summary>One covered side bay: 1 crate (its bottom slot; the top one is kept for saves and never filled) or 2 tanks
/// standing (front, rear), never mixed.</summary>
public readonly struct RoverBay
{
    public readonly string Name;
    public readonly int[] Crates, Tanks;
    public RoverBay(string name, int[] crates, int[] tanks) { Name = name; Crates = crates; Tanks = tanks; }
}

/// <summary>An axis-aligned box in rover-local space (centre and full size), for the click-box checks.</summary>
public readonly struct RoverBox
{
    public readonly float Cx, Cy, Cz, Sx, Sy, Sz;
    public RoverBox(float cx, float cy, float cz, float sx, float sy, float sz) { Cx = cx; Cy = cy; Cz = cz; Sx = sx; Sy = sy; Sz = sz; }

    /// <summary>Strictly inside (by more than eps): a point on a face is outside.</summary>
    public bool Contains(float x, float y, float z, float eps = 1e-4f) =>
        Math.Abs(x - Cx) < Sx / 2 - eps && Math.Abs(y - Cy) < Sy / 2 - eps && Math.Abs(z - Cz) < Sz / 2 - eps;

    public static RoverBox FromCorners(float x0, float x1, float y0, float y1, float z0, float z1) =>
        new((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2, Math.Abs(x1 - x0), Math.Abs(y1 - y0), Math.Abs(z1 - z0));
}

/// <summary>A bay door leaf: its rover.json part, its bay (index into RoverRules.Bays), its interactable (state 1 =
/// open) and its side (-1 left, +1 right).</summary>
public readonly struct RoverDoor
{
    public readonly string Name, Action;
    public readonly int Bay, Side;
    public RoverDoor(string name, int bay, string action, int side) { Name = name; Bay = bay; Action = action; Side = side; }
}

/// <summary>
/// The original rover's slot order and bay rules (spec 2026-09-27-original-rover-design.md; pure, tested in
/// _tools/HabTests). Saves store a slot's index, so 0-15 keep the 2020 rover's meaning: the two lifts (12/14 left,
/// 13/15 right) became the front bays' bottom crate and front tank; the other bay slots follow from 16.
/// </summary>
public static class RoverRules
{
    public const int SlotCount = 28;


    /// <summary>index -> (the game's StringKey, kind, anchor name in rover.json "anchors").</summary>
    public static readonly (string Key, RoverSlotKind Kind, string Anchor)[] Slots =
    {
        ("Entity", RoverSlotKind.Seat, "SeatDriver"),                 // 0
        ("Entity", RoverSlotKind.Seat, "SeatPassenger"),              // 1
        ("GasFilter", RoverSlotKind.Filter, "Filter1"),               // 2
        ("GasFilter", RoverSlotKind.Filter, "Filter2"),               // 3
        ("ProgrammableChip", RoverSlotKind.Chip, "Chip"),             // 4 (the 2020 third filter slot, a chip since the mod)
        ("GasCanister", RoverSlotKind.Air, "Air1"),                   // 5
        ("GasCanister", RoverSlotKind.Air, "Air2"),                   // 6
        ("GasCanister", RoverSlotKind.Air, "Air3"),                   // 7 thruster propellant when fitted
        ("GasCanister", RoverSlotKind.Waste, "Waste"),                // 8
        ("Battery", RoverSlotKind.Battery, "Battery1"),               // 9
        ("Battery", RoverSlotKind.Battery, "Battery2"),               // 10
        ("Battery", RoverSlotKind.Battery, "Battery3"),               // 11
        ("ContainerSlot", RoverSlotKind.Crate, "BayL1CrateBottom"),   // 12 (was the left lift's crate)
        ("ContainerSlot", RoverSlotKind.Crate, "BayR1CrateBottom"),   // 13 (right lift)
        ("GasTank", RoverSlotKind.Tank, "BayL1TankFront"),            // 14 (left lift's tank)
        ("GasTank", RoverSlotKind.Tank, "BayR1TankFront"),            // 15 (right lift)
        ("ContainerSlot", RoverSlotKind.Crate, "BayL1CrateTop"),      // 16
        ("GasTank", RoverSlotKind.Tank, "BayL1TankRear"),             // 17
        ("ContainerSlot", RoverSlotKind.Crate, "BayR1CrateTop"),      // 18
        ("GasTank", RoverSlotKind.Tank, "BayR1TankRear"),             // 19
        ("ContainerSlot", RoverSlotKind.Crate, "BayL2CrateBottom"),   // 20
        ("ContainerSlot", RoverSlotKind.Crate, "BayL2CrateTop"),      // 21
        ("GasTank", RoverSlotKind.Tank, "BayL2TankFront"),            // 22
        ("GasTank", RoverSlotKind.Tank, "BayL2TankRear"),             // 23
        ("ContainerSlot", RoverSlotKind.Crate, "BayR2CrateBottom"),   // 24
        ("ContainerSlot", RoverSlotKind.Crate, "BayR2CrateTop"),      // 25
        ("GasTank", RoverSlotKind.Tank, "BayR2TankFront"),            // 26
        ("GasTank", RoverSlotKind.Tank, "BayR2TankRear"),             // 27
    };

    public static readonly RoverBay[] Bays =
    {
        new("L1", new[] { 12, 16 }, new[] { 14, 17 }),
        new("R1", new[] { 13, 18 }, new[] { 15, 19 }),
        new("L2", new[] { 20, 21 }, new[] { 22, 23 }),
        new("R2", new[] { 24, 25 }, new[] { 26, 27 }),
    };

    /// <summary>Why the crate in bay slot `slot` cannot open its lid, or null: only with its bay's door open (the lid
    /// would come through a shut door); never in a top slot an old save left filled, nor under one.</summary>
    public static string LidBlock(int slot, Func<int, bool> occupied, Func<int, bool> doorOpen)
    {
        for (int b = 0; b < Bays.Length; b++)
        {
            var bay = Bays[b];
            if (slot != bay.Crates[0] && slot != bay.Crates[1]) continue;
            if (slot == bay.Crates[1]) return "No room to open it here: take it out of the bay first";
            if (occupied(bay.Crates[1])) return "No room to open it: take the crate on top off first";
            return doorOpen(b) ? null : "Open the bay door first";
        }
        return null;
    }

    /// <summary>The bay slots holding a crate open where LidBlock would refuse it: a bay door shut on an open crate, a
    /// save. They are to be closed.</summary>
    public static List<int> LidsToClose(Func<int, bool> occupied, Func<int, bool> open, Func<int, bool> doorOpen)
    {
        var shut = new List<int>();
        foreach (var b in Bays)
            foreach (int i in b.Crates)
                if (occupied(i) && open(i) && LidBlock(i, occupied, doorOpen) != null) shut.Add(i);
        return shut;
    }

    /// <summary>A bay takes nothing more: it holds its crate, or both tanks.</summary>
    public static bool BayFull(RoverBay bay, Func<int, bool> occupied) =>
        occupied(bay.Crates[0]) || occupied(bay.Crates[1]) || (occupied(bay.Tanks[0]) && occupied(bay.Tanks[1]));

    /// <summary>The slot a crate (tank = false) or a tank takes in this bay, or -1. One crate per bay, on its floor (the
    /// user's choice after test build 5: a closed crate with its lid is 0.73 m tall, a bay 1.22 m inside), and only into
    /// a bay without tanks; tanks stand front first and only into a bay without crates. (Vanilla ContainerSlot lets a
    /// tank in beside a single crate: CanPlaceTank only needs one free container slot.)</summary>
    public static int BaySlotFor(RoverBay bay, bool tank, Func<int, bool> occupied)
    {
        foreach (int i in tank ? bay.Crates : bay.Tanks) if (occupied(i)) return -1;
        if (!tank) return occupied(bay.Crates[0]) || occupied(bay.Crates[1]) ? -1 : bay.Crates[0];
        foreach (int i in bay.Tanks) if (!occupied(i)) return i;
        return -1;
    }

    // ---- bay doors (sub-project 3): each leaf hangs from a hinge along its top edge and swings up and out. Their
    // states are Button5-8: vanilla Thing.HasState restores Button1-3 itself (a saved Button3 lands on Button1).
    // (Declared before Interactables: static initialisers run in text order.)
    public static readonly RoverDoor[] Doors =
    {
        new("BayDoorL1", 0, "Button5", -1), new("BayDoorR1", 1, "Button6", 1),
        new("BayDoorL2", 2, "Button7", -1), new("BayDoorR2", 3, "Button8", 1),
    };

    public const float DoorOpenDeg = 100f, DoorSeconds = 1f, DoorWrenchSeconds = 0.5f;

    /// <summary>The leaf's turn about its hinge (Unity Euler z, degrees) at an open fraction 0..1: the bottom edge
    /// swings up and outward on either side.</summary>
    public static float DoorAngle(int side, float open) => side * DoorOpenDeg * Math.Max(0f, Math.Min(1f, open));

    /// <summary>A shock's spring scale along its axis: the spring runs from `start` below the upper eye to `end` above the
    /// lower one, modelled at `rest`; the eyes are `distance` apart. Never below 5 %; 1 without a rest length.</summary>
    public static float SpringScale(float distance, float start, float end, float rest) =>
        rest > 0.01f ? Math.Max(0.05f, (distance - start - end) / rest) : 1f;

    // ---- work lights (the user's check-in 2 request): spot lights on the roof rack, the side pair and the rear pair,
    // switched from the dash. Button13/14: free in the game's list, and not Button1-3 (vanilla restores those itself).
    // (Declared before Interactables: static initialisers run in text order.)
    public static readonly string[] WorkLights = { "Button13", "Button14" };     // side, rear

    /// <summary>The work lights' draw a power tick (the user's request; vanilla Rover.OnPowerTick charges its headlights
    /// 20, its cabin light 5): the four side lamps, the rear pair.</summary>
    public const float SideLightsPower = 20f, RearLightsPower = 10f;

    public static float WorkLightDraw(bool side, bool rear) => (side ? SideLightsPower : 0f) + (rear ? RearLightsPower : 0f);

    /// <summary>The battery a power tick drains, as vanilla Rover.OnPowerTick picks it: the last charged one of its battery
    /// slots; -1 when all are flat or empty.</summary>
    public static int DrainSlot(IReadOnlyList<bool> charged)
    {
        for (int i = charged.Count - 1; i >= 0; i--)
            if (charged[i]) return i;
        return -1;
    }

    /// <summary>The work lamps' light anchors: one spot light at each lamp's lens (the user's check-in 3 report: one light
    /// between each side's two lamps did not look like two sources).</summary>
    public static readonly string[] SideLightAnchors = { "SideLightL1", "SideLightL2", "SideLightR1", "SideLightR2" },
        RearLightAnchors = { "RearLightL", "RearLightR" };

    /// <summary>The front lamps' spot lights (the user's check-in 3 report: the headlights and the roof light bar did not
    /// light up): one at each nose LED bar, a pair at the roof light bar; both on with the headlights (Button1).</summary>
    public static readonly string[] HeadlightAnchors = { "HeadlightL", "HeadlightR" }, LightBarAnchors = { "LightBarL", "LightBarR" };

    /// <summary>A seated head's turn toward where its owner looks is capped at this either way (the user's check-in 3
    /// report: in third person the head spun round and upside down; SeatedHead).</summary>
    public const float HeadTurnMaxDeg = 70f;

    /// <summary>An angle in degrees wrapped to (-180, 180].</summary>
    public static float WrapDeg(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        else if (deg <= -180f) deg += 360f;
        return deg;
    }

    /// <summary>A head turn (degrees, wrapped) capped at HeadTurnMaxDeg either way.</summary>
    public static float CapTurn(float deg) => Math.Max(-HeadTurnMaxDeg, Math.Min(HeadTurnMaxDeg, deg));

    /// <summary>The slot a crate (tank = false) or tank loads into: the nearest bay (by distance) whose door is open and
    /// which takes it (BaySlotFor), or -1.</summary>
    public static int PickBaySlot(bool tank, Func<int, bool> doorOpen, Func<int, bool> occupied, Func<int, float> distance)
    {
        var order = new List<int>();
        for (int b = 0; b < Bays.Length; b++) if (doorOpen(b)) order.Add(b);
        order.Sort((a, b) => distance(a).CompareTo(distance(b)));
        foreach (var b in order)
        {
            int slot = BaySlotFor(Bays[b], tank, occupied);
            if (slot >= 0) return slot;
        }
        return -1;
    }

    /// <summary>Is slot i one of the bays' crate or tank slots?</summary>
    public static bool IsBaySlot(int i)
    {
        foreach (var b in Bays) if (Array.IndexOf(b.Crates, i) >= 0 || Array.IndexOf(b.Tanks, i) >= 0) return true;
        return false;
    }

    /// <summary>The bay floor's height (tools/blender_rover_model.py BAY_FLOOR); a let-go item lands BayDropX out from
    /// the middle (clear of the tyres at full steer) and BayDropLift above the ground.</summary>
    public const float BayFloorH = 1.82f, BayDropX = 2.70f, BayDropLift = 0.30f;

    /// <summary>Where a crate or tank let go from a bay slot (at slotLocal, rover-local) lands: on the ground beside its
    /// door, as high above the ground as it stood above the bay floor plus BayDropLift (the top crate comes down on the
    /// bottom one), at the slot's own z.</summary>
    public static (float X, float Y, float Z) BayDropPoint((float X, float Y, float Z) slotLocal) =>
        (slotLocal.X < 0 ? -BayDropX : BayDropX, slotLocal.Y - BayFloorH + BayDropLift, slotLocal.Z);

    /// <summary>A bay as players read it: "left front" ... "right rear".</summary>
    public static string BayLabel(RoverBay bay) => (bay.Name[0] == 'L' ? "left " : "right ") + (bay.Name[1] == '1' ? "front" : "rear");

    /// <summary>The rover's interactables with the 2020 flags (StringKey = action name; Sync = JoinInProgressSync).
    /// Anchor = where the collider sits: a slot anchor (the slot's trigger box), a switch anchor, or null (none).</summary>
    public static readonly (string Key, string Action, bool Sync, bool KeyInteract, string Anchor)[] Interactables = BuildInteractables();

    private static (string, string, bool, bool, string)[] BuildInteractables()
    {
        var list = new List<(string, string, bool, bool, string)>
        {
            ("Export", "Export", true, true, "Screen"),        // filtration
            ("Button1", "Button1", true, true, "Screen"),     // headlights
            ("Button2", "Button2", true, true, "Screen"),     // cabin lights
            ("Import", "Import", true, true, "Screen"),        // air pump
            ("OnOff", "OnOff", true, true, "Screen"),
            ("Powered", "Powered", true, false, null),
        };
        foreach (var d in Doors) list.Add((d.Action, d.Action, true, false, null));    // door states (CargoRover.DeserializeSave restores them)
        foreach (var w in WorkLights) list.Add((w, w, true, true, "Screen"));           // work lights (restored there too)
        for (int n = 1; n <= 12; n++) list.Add(("Slot" + n, "Slot" + n, false, true, Slots[n - 1].Anchor));
        return list.ToArray();
    }

    /// <summary>Static load share per axle for a rigid body on equal springs (loads linear in z; they sum to 1 and
    /// balance about the centre of mass).</summary>
    public static float[] AxleShares(float comZ, float[] axleZ)
    {
        int n = axleZ.Length;
        double sz = 0, szz = 0, szc = 0;
        foreach (var z in axleZ) { sz += z; szz += z * z; szc += z - comZ; }
        double szzc = 0;
        foreach (var z in axleZ) szzc += z * (z - comZ);
        // n a + sz b = 1 ;  szc a + szzc b = 0
        double det = n * szzc - sz * szc;
        double a = szzc / det, b = -szc / det;
        var shares = new float[n];
        for (int i = 0; i < n; i++) shares[i] = (float)(a + b * axleZ[i]);
        return shares;
    }

    public static readonly string[] RequiredKeys = { "version", "body", "glass", "parts", "colliders", "autoCom", "mass", "wheelCollider", "wheels", "anchors", "fields" };

    /// <summary>Anchors the builder needs besides the slot anchors.</summary>
    public static readonly string[] NamedAnchors =
    {
        "DriverExit", "PassengerExit", "CameraDriver", "CameraPassenger", "Hitch", "HeadlightL", "HeadlightR",
        "TailLightL", "TailLightR", "CabinLight", "EntryDriver", "EntryPassenger",
        "Screen", "SideLightL1", "SideLightL2", "SideLightR1", "SideLightR2", "RearLightL", "RearLightR", "LightBarL", "LightBarR",
    };

    // ---- click boxes: the trigger a player aims at. The game's cursor is one Physics.Raycast against its hit mask
    // (CursorManager.SetCursorTarget), so a solid collider in front of a trigger hides it: a seat inside the solid
    // cab collider cannot be clicked from outside. Seats are entered from outside, so their click box is on the cab
    // door; the seat itself stays where the player sits.

    /// <summary>The trigger size per slot kind (x, y, z in the click anchor's frame): the 2020 sizes, and for a seat a
    /// box from 5 mm off its cab door's face (|x| 1.415) 31.5 cm into the cab (|x| 1.10, tools/blender_rover_model.py
    /// ENTRY_IN): a player outside clicks its outer face, a seated player its inner one (the cursor ray starts in the
    /// cab's collider and meets the box before leaving it).</summary>
    public static (float X, float Y, float Z) ClickBoxSize(RoverSlotKind kind) => kind switch
    {
        RoverSlotKind.Seat => (0.315f, 1.00f, 0.70f),
        RoverSlotKind.Filter => (0.20f, 0.30f, 0.20f),
        RoverSlotKind.Chip => (0.19f, 0.26f, 0.12f),
        RoverSlotKind.Air or RoverSlotKind.Waste => (0.225f, 0.429f, 0.225f),    // the 2020 trigger: the canister's outer half
        RoverSlotKind.Battery => (0.10f, 0.17f, 0.10f),
        _ => (0f, 0f, 0f),
    };

    /// <summary>A seat's click box: its outer SeatDoorSlab (5 mm off the cab door) is what a player outside clicks to get
    /// in; it must stay clear of the solid colliders.</summary>
    public const float SeatDoorSlab = 0.06f;

    /// <summary>The 2020 seat slot's Size (not its click box).</summary>
    public static readonly (float X, float Y, float Z) SeatSlotSize = (0.21f, 0.22f, 0.34f);

    /// <summary>The anchor a slot's click box sits on: a seat's is on its cab door, every other slot's is its own.</summary>
    public static string ClickAnchor(string slotAnchor) => slotAnchor switch
    {
        "SeatDriver" => "EntryDriver",
        "SeatPassenger" => "EntryPassenger",
        _ => slotAnchor,
    };

    /// <summary>The click box's centre in its anchor's frame: on the item's body (a canister's origin is at its valve
    /// end, a filter's above its middle).</summary>
    public static (float X, float Y, float Z) ClickBoxCenter(RoverSlotKind kind) => kind switch
    {
        RoverSlotKind.Air or RoverSlotKind.Waste => (0f, -0.4737f, 0f),          // the 2020 canister trigger's m_Center
        RoverSlotKind.Filter => (0f, -0.0475f, 0f),
        _ => (0f, 0f, 0f),
    };

    /// <summary>The item a slot kind holds, measured from the game's meshes (resources.assets, 2026-09-27): the body's
    /// centre from the item's origin and its full size, in the item's own frame. Canisters: the regular and the smart
    /// canister's envelope (valve end 0.194 above the origin, bottom 0.680 below); the chip with its prefab's 1.11
    /// scale; batteries: the largest cell; the crate closed (its open lid rises to 0.732).</summary>
    public static ((float X, float Y, float Z) Center, (float X, float Y, float Z) Size) ItemBody(RoverSlotKind kind) => kind switch
    {
        RoverSlotKind.Air or RoverSlotKind.Waste => ((0f, -0.243f, 0f), (0.208f, 0.874f, 0.222f)),
        RoverSlotKind.Filter => ((0f, -0.0475f, 0f), (0.188f, 0.287f, 0.188f)),
        RoverSlotKind.Chip => ((0f, 0f, 0f), (0.179f, 0.251f, 0.111f)),
        RoverSlotKind.Battery => ((0f, 0f, 0f), (0.098f, 0.169f, 0.096f)),
        RoverSlotKind.Crate => ((0f, 0.2766f, 0.0109f), (1.839f, 0.553f, 0.730f)),
        RoverSlotKind.Tank => ((0f, 0.0155f, 0f), (0.813f, 1.177f, 0.813f)),
        _ => ((0f, 0f, 0f), (0f, 0f, 0f)),
    };

    /// <summary>Where the slot-panel items sit, as tools/blender_rover_model.py cuts it (SLOT_H, RECESS_Z, SLOT_BACK_X,
    /// RACK): the left recess, the canister rack's pocket behind it, the right recess.</summary>
    public static readonly RoverBox[] PanelOpenings =
    {
        RoverBox.FromCorners(-1.35f, -1.10f, 1.88f, 2.95f, 0.30f, 0.83f),
        RoverBox.FromCorners(-1.35f, -0.40f, 1.92f, 2.91f, 0.31f, 0.59f),
        RoverBox.FromCorners(1.10f, 1.35f, 1.88f, 2.95f, 0.30f, 0.83f),
    };

    /// <summary>The share of a box's surface (an n x n grid on each face) outside every solid box: 0 = sealed in.</summary>
    public static float ExposedFraction(RoverBox click, IList<RoverBox> solids, int n = 8)
    {
        float[] c = { click.Cx, click.Cy, click.Cz }, s = { click.Sx, click.Sy, click.Sz };
        int total = 0, open = 0;
        var p = new float[3];
        for (int axis = 0; axis < 3; axis++)
            for (int side = -1; side <= 1; side += 2)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                        p[axis] = c[axis] + side * s[axis] / 2;
                        p[a1] = c[a1] + ((i + 0.5f) / n - 0.5f) * s[a1];
                        p[a2] = c[a2] + ((j + 0.5f) / n - 0.5f) * s[a2];
                        total++;
                        bool inside = false;
                        foreach (var b in solids) if (b.Contains(p[0], p[1], p[2])) { inside = true; break; }
                        if (!inside) open++;
                    }
        return (float)open / total;
    }

    /// <summary>Whether the straight line from `from` to `to` passes through a solid box. Boxes that contain `from`
    /// are skipped: a ray that starts inside a collider does not hit it (so a seated player reaches the dash).</summary>
    public static bool SegmentBlocked((float X, float Y, float Z) from, (float X, float Y, float Z) to, IList<RoverBox> solids)
    {
        float[] o = { from.X, from.Y, from.Z }, d = { to.X - from.X, to.Y - from.Y, to.Z - from.Z };
        foreach (var b in solids)
        {
            if (b.Contains(from.X, from.Y, from.Z, 0f)) continue;
            float[] c = { b.Cx, b.Cy, b.Cz }, h = { b.Sx / 2, b.Sy / 2, b.Sz / 2 };
            float t0 = 0f, t1 = 1f;
            bool hit = true;
            for (int k = 0; k < 3 && hit; k++)
            {
                if (Math.Abs(d[k]) < 1e-9f) { hit = Math.Abs(o[k] - c[k]) < h[k]; continue; }
                float ta = (c[k] - h[k] - o[k]) / d[k], tb = (c[k] + h[k] - o[k]) / d[k];
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta);
                t1 = Math.Min(t1, tb);
                hit = t0 <= t1;
            }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>Everything missing from a rover.json (keys, anchors, mesh files), for the builder's log. Empty = buildable.</summary>
    public static List<string> Problems(Func<string, bool> hasKey, Func<string, bool> hasAnchor, IEnumerable<string> meshes, Func<string, bool> meshExists)
    {
        var p = new List<string>();
        foreach (var k in RequiredKeys) if (!hasKey(k)) p.Add("key " + k);
        foreach (var s in Slots) if (!hasAnchor(s.Anchor)) p.Add("anchor " + s.Anchor);
        foreach (var a in NamedAnchors) if (!hasAnchor(a)) p.Add("anchor " + a);
        foreach (var m in meshes) if (!meshExists(m)) p.Add("mesh " + m);
        return p;
    }
}
