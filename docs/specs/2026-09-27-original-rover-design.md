# Original Cargo Rover — design

2026-09-27. Mod `mods/Stationeers.RoverCargo`. Status: design approved in conversation, section by section, on
2026-09-27. This document covers the whole programme and gives the full design of **sub-project 1**. Sub-projects 2-5
get their own short design when they come up. Sub-project 1 built (commits 2136ee1..a626dfb); sub-project 2 next.

## Goal
Replace every 2020 RocketWerkz asset the mod uses with our own models and data, so the mod ships **complete on the
Steam Workshop** with no extraction step. At the same time, the rover becomes the user's own design: the "crew rover"
from the user's two reference pictures, adapted to the game. Today every player has to download a 1 GB 2020 depot and
run a Python extractor. Without that, the mod loads only the chase camera: no rover, no trailer, no hab.

## Decisions (user, 2026-09-27)
| Topic | Decision |
|---|---|
| Rover model | Original, from the user's reference pictures; 6 wheels, not 8 |
| Cargo | **Covered side bays: 2 per side, end to end.** Each holds **2 crates stacked or 2 tanks standing**, for **8 crates or 8 tanks** in total. Bay doors are operated with a wrench. **Revised 2026-09-30 (the user, after test build 5): 1 crate or 2 tanks a bay, 4 crates or 8 tanks in all** - a closed crate is 0.73 m with its lid (see Measured cargo), a bay 1.22 m inside; the top crate slots stay for saves, never filled |
| Seats | 2. The game's rover code is hard-wired to one driver and one passenger (get in, exit points, camera points) |
| Size | Width, track, tyres and heights as today. Longer only by what the contents need: **7.85 m nose to hitch** (today 5.7 m). Built: 8.15 m, the hitch 0.30 m further back so the hab clears to 70 deg (the body itself ends 0.18 m shorter) |
| Colours | Charcoal grey, light silver and orange on the rover, **and** on the trailer and hab (at switch-over) |
| 2020 model | **Removed from the mod.** The user keeps the current 2020-based build locally, through a git tag. The Workshop build never reads or mentions the extraction |
| Delivery | Build everything, then switch over once at the end (approach 3). **No in-game test before the switch-over.** The user reviews Blender renders on the phone at each milestone |
| Build stages | Trailer: 3 stages (kit at 3/4 of the rover kit cost, then steel sheets, then plastic sheets). Hab: 4 stages (kit at the rover kit cost, then steel, plastic, and 8 electronic parts). Quantities are open (sub-project 5) |

## Programme
Each sub-project gets its own spec (1 is below), plan and build. All work stays off the live mod until sub-project 6.
1. **Exterior and driving:** hull, cab shell, doors, glass, wheels, arms, colliders, lights, mast, temporary cab,
   `rover.json`, and the new rover builder.
2. **Cab interior and dashboard.**
3. **Covered side bays and slot panel:** bay doors (wrench), bay slots and loading rules, the gas, filter and battery
   slots, the vent lever.
4. **Upgrade refit:** armour, fairings, thruster pods and riding fenders on the new hull.
5. **Build stages and kits:** rover frame (3 stages), trailer (3), hab (4); kits, recipes, deconstruction.
6. **Switch-over:**
   - The new model takes the `RoverCargo` prefab name.
   - The trailer and hab get the new colours and the new tyres and arms.
   - The 2020 path is removed: code, `tools/extract_rover_cargo.py`, and the README setup steps. The 2020-based build
     is tagged `rover-2020` first.
   - Old-save check, then the user's first in-game drive and final acceptance.

## Global constraints
- **No RocketWerkz assets in the Workshop build:** no extracted mesh, texture or data file shipped or required. Parts
  copied at runtime from the loaded game are allowed, as today (the Mk I rover, the on/off switch, jetpack particles,
  game materials).
- **Old saves keep working:** the prefab name `RoverCargo` stays; slot indices 0-15 keep their meaning (see Slots);
  interactable actions keep their names (Button1-12, OnOff, Import, Export, Slot1-12).
- DLL swaps only with the game closed. Never push unless asked. Commits carry the session attribution lines.
- **Models:** check from all angles (side, top, back orthographic) before presenting. No visible overlapping meshes.
  Test-fit crates and tanks at the slot nodes.
- **Style:** the hab's faceted, chunky, flat-shaded look. Game palette materials. No logos or lettering.

## Sub-project 1: exterior and driving

### 1. Layout (v3)
Drawings: `2026-09-27-original-rover-layout-v3.png` (side and top) and `2026-09-27-original-rover-bay-section.png`
(bay cross-section). Axes as in the game: z forward, y up, x right. The origin is on the ground under the rover, as
today.

| Part | Position / size |
|---|---|
| Wheels | front z **+2.265**, mid **-1.50**, rear **-3.18** (tandem spacing 1.68 as today); y 0.645; x +-1.457; tyre r 0.72 |
| Wheelbase | 5.45 m (today 4.0) |
| Bull bar | tip z +3.25 |
| Cab (2 seats) | z +3.05 .. +0.85; roof 3.1; 3-pane faceted windscreen; one 0.8 m door each side |
| Slot panel section | z +0.85 .. +0.25 (gas, filter and battery slots; sub-project 3 places them) |
| Bay 1 / bay 2 (each side) | z +0.25 .. -1.65 and -1.75 .. -3.65; inside 1.90 long x 0.86 deep (x 0.45 .. 1.31) x 1.22 tall; floor 1.82, top 3.04 |
| Spine | 0.90 m (x -0.45 .. +0.45) |
| Tail pods | z -3.65 .. -4.22 (built; the tail ends at -4.10, right behind the rear tyres); top 3.25 |
| Rear bumper block | z -3.98 .. -4.10 (built; planned -4.10 .. -4.40), y 0.7 .. 1.3, tail lights |
| Hitch point | z **-4.90** (built, on a 0.80 m receiver arm; planned -4.60, where the hab's tongue touched the tail from 35 deg of yaw), y 0.8 (hitch height as today); 1.72 m behind the rear axle (today 0.97) |
| Heights | belly 0.6; wheel arches **1.76** (visual tyre r 0.73: tyre top 1.375 + 0.35 m bump room + the checks' 3 cm margin); bay floor 1.82 (a 6 cm floor plate over the arch line), bay top 3.04; module roof 3.16 (built: an 8 cm roof chamfer keeps the side flat to 3.08 for the bay frames), tail pods 3.25; cab roof 3.08 (under the module roof: no coplanar roof faces); mast about 4.0 |
| Width | **3.60 m** over the tyres (tyre faces x 1.114 .. 1.801, measured; the drawings said 3.46). Upper body 2.7 m (x +-1.35) above the arches. Below the arches the steered tyres sweep in to x 0.72 at 40 deg, so the lower hull is a **keel** (x +-0.53, which carries the arm hinges, as on the trailer) plus **side pods** out to x +-1.05 between the front and mid wheel zones (z -0.67 .. 1.43) |

Measured cargo (from the current game): crate `DynamicCrate` 1.84 x 0.73 x 0.55 m (collider 1.71 x 0.71 x 0.54; crate mesh 1.839 x 0.553 x 0.730: its
open lid reaches 0.732 tall, so a lid opened in a stacked bay clips the crate above, visual only, sub-project 3);
**corrected 2026-09-30:** the prefab's child `BoxColliderOpenRenderer` is the crate's lid in its *closed* pose (its own
solid collider, 0.526-0.732 above the base), so a closed crate is 1.84 x 0.73 x **0.73** m; two stacked (1.46 m) do not
fit a bay, whose top crate came through its door in the test (the cargo decision above is revised);
portable tank dia 0.81 (0.84 at the rim) x 1.18 m, origin 0.573 above its base; crate origin at its base. Clearance
in a bay: 2 crates stacked = 1.10 m tall, 3 cm spare at each end, 14 cm spare in depth; a tank has 4 cm spare above it
and 3 cm across; 2 tanks in a row take 1.68 of the 1.90 m.

Bump room: the tow logs (3708 wheel samples, every 2 s) show a rest travel of 0.50 of the 1.0 m, 95 % under 0.59 and
a maximum of 0.67. So the largest bump seen was 0.17 m above rest; the arches allow 0.35 m. A harder bump can briefly
show a tyre through an arch. That is visual only: wheels are raycasts and never touch the hull colliders.

### 2. Look
Reference: the user's two pictures (front 3/4 and rear 3/4), in the game's chunky, flat-shaded style.
- **Shape:** faceted armour: big flat panels with chamfered edges, a wedge nose. The cab widens over the wheels. A
  3-pane windscreen (centre plus two angled sides); windows in the doors.
- **Colours:**
  - charcoal grey: lower hull, cab roof, frames, tail pods;
  - light silver: upper body panels, cab sides, bay surrounds;
  - orange: a stripe from the nose along the beltline to the tail, and a frame round each bay door;
  - black: bull bar, underbody, suspension; dark gunmetal rims.
  - Material choice: the game palette (`ColorGray`, `ColorWhite`, `ColorOrange`, `ColorBlack`, or a game metal
    material for silver), decided by a test render. `GameMaterial()` finds any loaded game material by name.
- **Front:** a heavy bull bar, fitted as standard; two LED light bars as the headlights (the Mk I's volumetric
  headlights move there); small red marker lamps.
- **Rear:** two raised tail pods with vent grilles; a bumper block with square red tail lights; the hitch in its
  centre.
- **Roof:** segmented plates and a few vent boxes; the sensor mast mid-roof (camera pods and a dome, decoration only)
  and a whip antenna at the back. The roof stays clear for the thruster-pod upgrade (sub-project 4).
- **Sides:** vent grilles; the slot panel is a recessed dark panel.
- **Wheels:** chunky lugged off-road tyres, dark rims with a bolted hub, boxy trailing arms with a visible damper.
- **Not copied:** logos and lettering ("ARES", "MARS-6"), 8 wheels, film-level greebles, mud.

### 3. Parts and data sources
- **Our meshes, shipped** (a new `RoverAssets/` folder: `rover.json` plus `meshes/*.rcm`, the same RCM1 format and
  pipeline as `TrailerAssets`/`HabAssets`):
  - hull, cab, doors, glass, bull bar, light housings, mast, tail pods, bay doors;
  - tyres, rims, suspension arms;
  - seats (unless the live Mk I's seat is a separate part that can be copied);
  - the temporary switch panel.
- **Blender scripts:** `tools/blender_rover_model.py` (builders and checks) and `tools/blender_build_rover.py`
  (export), following `blender_trailer_model.py` / `blender_build_trailer.py`.
- **Runtime copies from the loaded game (not shipped), as today:**
  - the Mk I rover as the base (`Instantiate(mk1)`): Rover code, rigid body, audio sources and events, headlight beams;
  - the game's on/off switch;
  - game materials (palette, `WindowGlass`).
- **Our numbers in `rover.json`**, replacing everything `CargoPrefabs.BuildRover` reads from the 2020 layout:
  - **Wheels:** 6, all motorised. Front `Mode` Normal (steer), mid None, rear Inverted (counter-steer). The
    `RearWheelSteer` config stays.
  - **WheelCollider (same as today):** centre y +0.5, radius 0.72, mass 1, damping rate 0.25, suspension distance 1.0,
    spring 2000 / damper 200 / target 0.5, force-app point 0. Forward friction 0.4/1.0/0.8/0.5, stiffness 3.0.
    Sideways friction 0.2/1.0/0.5/0.75, stiffness 1.0.
  - **Rover fields:** SteeringPower 50, MaxTurnAngle 40, MotorSpeed 100, BrakeSpeed 100, SteeringSpeed 1.0,
    ThingHealth 3578, mass 80. Motor, brake and max speed stay config-driven (60 / 20 / 6.5).
  - **Cabin:** Volume 50 L, PressurePerTick 101.325, MaxEnergy 12000, OutputSetting 101.325, OutputTemperature
    293.15, waste fallback 4053 kPa (the upgrade rule of 95 % of the canister's MaxPressure stays).
  - **Positions:** seat, exit and camera points; Bounds from the new mesh; SurfaceArea kept at 102 (gameplay parity).
  - **Centre of mass:** keep today's resulting height, **0.53 m** (below the wheel centres). The default of the
    `RoverCenterOfMassOffset` config is rebased so it still gives 0.53 m with the new colliders. The fore-aft position
    comes from the new colliders and is checked against the axle loads.

### 4. Slots and save compatibility
Saves store an item's slot **index**, so indices 0-15 keep today's meaning. The bay slots are appended.

| Index | Slot | Notes |
|---|---|---|
| 0, 1 | Entity (driver, passenger) | seats |
| 2, 3, 4 | GasFilter | 4 is the chip slot |
| 5, 6, 7 | GasCanister (air) | 7 is the thruster propellant slot when thrusters are fitted |
| 8 | GasCanister (waste) | |
| 9, 10, 11 | Battery | |
| 12 | ContainerSlot | **left front bay, bottom crate** (was the left lift) |
| 13 | ContainerSlot | **right front bay, bottom crate** (was the right lift) |
| 14 | GasTank | **left front bay, front tank** (was the left lift) |
| 15 | GasTank | **right front bay, front tank** (was the right lift) |
| 16, 17 | left front bay: top crate, rear tank | new |
| 18, 19 | right front bay: top crate, rear tank | new |
| 20-23 | left rear bay: bottom crate, top crate, front tank, rear tank | new |
| 24-27 | right rear bay: bottom crate, top crate, front tank, rear tank | new |

Each bay is one `ContainerSlot` (bay) entry with 2 container slots and 2 tank slots. Vanilla `ContainerSlot` would let
a tank in beside a single crate: `CanPlaceTank` only needs one free container slot. So bays use the mod's own attach
rule, like `TrailerBedRule`: a crate only into a bay with no tanks, a tank only into a bay with no crates, nearest bay
first. Sub-project 1 writes the slot nodes and the table into `rover.json`; sub-project 3 makes the bays work (doors,
attach rule).

### 5. Temporary cab (until sub-project 2)
2 seats and a plain panel carrying today's interactables: Button1 headlights, Button2 cabin lights, OnOff, Import (air
pump), Export (filtration), and Button12 thrusters (the copied on/off switch). No gauges. Slot1 and Slot2 are the
seats; Slot3-12 (filters, canisters, batteries) sit on temporary sockets in the slot panel section.
`JoinInProgressSync` and the saved states work as today.

### 6. Code
- A new builder, `CargoPrefabs.BuildRoverFromJson` (the name may change in the plan), reads `RoverAssets/rover.json`
  the way `BuildTrailer` reads `trailer.json`. It keeps the proven parts of `BuildRover`: instantiate the Mk I, keep
  the audio and headlights, copy fields into `CargoRover`, retarget parents.
- A validator in the style of `tools/check_layout.py` for `rover.json`: required nodes; 28 slots with 0-15 in the order
  above; colliders present; wheels.
- The 2020 builder stays unchanged and in use until sub-project 6. **Nothing in sub-projects 1-5 changes what the
  installed mod loads.** The new builder is wired in at the switch-over.

### 7. Testing and acceptance (sub-project 1)
- **Blender checks** (in the style of `upgrade_checks()` / `hab_checks()`):
  - front and rear wheel steering sweep (MaxTurnAngle 40, both directions) against the hull, at rest and at +0.35 m
    bump;
  - each bay fits 2 crates stacked and 2 tanks, all at least 2 cm clear;
  - trailer and hab turning at their hitch yaw limits (70 deg) against the new rear, with no body contact;
  - no overlapping meshes; colliders cover the hull and none sit below the belly.
- **Code tests** (`_tools/HabTests`): the slot table (0-15 unchanged, 16-27 mapped), the bay attach rule (pure part)
  and the `rover.json` validator.
- **Phone reviews:** orthographic side, top and back renders plus a 3/4 view at each milestone: blockout, detailed
  hull, wheels, complete. The next milestone starts after the user's feedback, or right away when the user says so.
- **Done when:** the checks pass, the user approves the complete renders, and `rover.json` validates. The first
  in-game drive is at the switch-over (sub-project 6), by the user's choice.

## Later sub-projects: decided so far / open
- **2. Dashboard:** designed and approved: `2026-09-27-original-rover-cab-design.md` (a touch status screen in the
  hab screen's style, tap-target controls, power on the screen, the footwell dip for legroom, real seats).
- **3. Bays:** decided: a wrench opens and closes the bay doors; the bay door swing-clearance check lives here (moved
  from sub-project 1: the opening motion is decided here; the cab doors are static outlines). Open: whether loading needs an open door, or wrenching
  a crate opens the nearest door; where the gas, filter and battery slots sit on the panel; the vent lever. Carried
  from the sub-project 1 review: the bays are still inside the solid module collider, so crates and tanks there cannot
  be clicked (the game's cursor is one raycast); hollow the bays in the collider set, give the door leaves colliders,
  and extend the click-box exposure check to the crate and tank slots. Before placing the panel, measure the canister
  origin: the 2020 canister trigger sits 0.47 m along the canister from its slot node, so upright canisters at h 2.12
  may hang below the recess floor (1.88).
- **4. Upgrades:** open: the look of armour, fairings, pods and fenders on the new hull. The bull bar is standard, so
  the armour must look different from it.
- **5. Build stages:** open: sheet counts (proposal: trailer 10 steel / 10 plastic, hab 20 / 20 / 8 electronic parts);
  whether the trailer kit drops the electronics metals; deconstruction and refunds; the frame meshes per stage for all
  three.
- **6. Switch-over:** decided: tag `rover-2020`, remove the 2020 path, recolour the trailer and hab, new tyres on the
  trailer and hab. Check in game: palette colours (silver/charcoal/gunmetal/orange UV spots); slot item placement
  (filter and battery origins near their 2020 trigger boxes; canisters: see sub-project 3); the seated pose against the
  temporary seat cushion; the dash switches in reach from the seats (about 1.25 m from the eye); crate lid in a stacked
  bay; headlight aim at the LED bars; handling at the 5.45 m wheelbase with the hitch 1.72 m behind the rear axle (the
  pin position and arm length are the numbers to retune); a new rover thumbnail; no per-frame error in Player.log and
  the lights and arms work; getting in from both doors and using every panel slot from outside; spray paint on the new
  rover (every palette role is on the paintable ColorWhite). Wire BuildRoverFromJson and replace the
  RoverCenterOfMassOffset config with a height (default 0.53 m) for the new rover. When wiring (from the sub-project 1
  review): a null rover skips Register, the frame, the kit and the hitch receiver; the builder log is printed in a
  finally; rover.json is parsed inside a try (a truncated file throws); the new rover gets no hitch receiver from the
  old trailer.json. The bay-attach rule (RoverRules.BaySlotFor) is hooked into Rover.Attach in sub-project 3, with the
  bay door swing check.

## Risks
- **Handling:** the 5.45 m wheelbase turns about a third wider, and the hitch sits 0.75 m further back. They are
  first driven at the end. Mitigation: all wheel and suspension numbers stay identical, and the centre of mass stays
  at 0.53 m. Steering (SteeringPower, MaxTurnAngle) is retuned at the switch-over if needed.
- **Hab turning clearance** changes with the new rear (58-62 deg today). Covered by the Blender turning check.
- **Old saves:** covered by the slot table and by keeping the prefab name and action names. It is verified on a copy
  of a save at the switch-over.
