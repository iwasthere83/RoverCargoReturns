# Rover (Cargo) Returns

A pressurised 6-wheel Cargo Rover for today's Stationeers — an original model, inspired by the Cargo Rover the game once
had — with two trailers to tow behind it: a cargo trailer with six bays for crates or portable tanks, and a walk-in
habitat trailer with its own air, water and power. Also adds a third-person chase camera to every rover.

**Requires:** BepInEx and StationeersLaunchPad.

This mod ships no game assets. Everyone in a multiplayer game needs it installed.

## Getting the vehicles
Print the kits on an **Electronics Printer (Tier Two)**, place the kit, and build the frame up in stages:

| Kit | Printer cost | Building the frame |
|---|---|---|
| Kit (Rover Cargo) | 120 steel, 25 copper, 15 electrum, 10 constantan, 10 silicon | welder + 10 steel sheets, then 10 plastic sheets in one hand and 4 electronic parts in the other |
| Kit (Trailer Cargo) | 90 steel, 20 copper | welder + 10 steel sheets, then wrench + 10 plastic sheets |
| Kit (Trailer Habitat) | as the rover kit | welder + 20 steel sheets, wrench + 20 plastic sheets, wrench + 8 electronic parts |

The last step turns the frame into the vehicle. **Taking it apart** goes the other way: a **drill** turns a finished,
parked vehicle back into its frame (the last step's materials come back, anything aboard drops out), then the frame
comes down stage by stage to its kit, each stage refunding its materials, with the tool its tooltip names (crowbar,
angle grinder, drill).

## The Rover (Cargo)
- A sealed cab for two: the air pump fills it from the O2 canisters, filtration moves the used air into the waste
  canister, climate control holds the temperature. Once it is pressurised the helmet can come off.
- A touch dashboard: drive, lights, air and power at a glance and a tap.
- Covered side bays for crates and portable tanks: a **wrench** opens and closes the bay doors; items load through an
  open door.
- Gas, filter and battery slots on the side panel; one slot takes a programmable chip that can read the rover's
  position, speed, heading and cabin, and run its air and lights.
- Headlights, work lights and a light bar.
- **Upgrades** (wrench with the material in the other hand): storm armour (10 steel sheets; no storm damage), wind
  fairings (20 plastic sheets; halves the storm's push), thrusters (a Kit (Governed Gas Rocket Engine)). The trailers
  take armour and fairings too.
- **Repairs**: duct tape repairs the rover and both trailers, as on any rover.
- **Paint**: a spray can paints the body; the trim stays orange.

## The trailers
- **Hitching**: back the rover up to the trailer and use a **wrench** on the trailer's drawbar; wrench again to release.
  An unhitched trailer holds its brakes.
- **Cargo trailer**: six bays, each for one crate or two portable tanks. Place an item beside the bay you want.
- **Habitat trailer**: a walk-in room with an airlock door, its own air supply and waste cradles, water, bunks, a shower
  and a toilet, roof solar and a battery, and a status screen. Use a **wrench** on the deploy panel (front left) to
  deploy it — the slide-out, the stairs and the stabiliser legs come out — and again to stow it before towing.

## Chase camera
In any rover: hold the third-person key and scroll while seated; the mouse orbits.

## Settings (`RoverCargo.cfg`, in the mod's folder, created on first start)
Driving: `MotorPower`, `BrakePower`, `MaxSpeed`, `RearWheelSteer`, `TrailerSideGrip`, `TractionBonus`,
`GripAssistMkI`. Stability: `TrailerCenterOfMassHeight`, `HabCenterOfMassHeight`. Cabin: `RoverInsulation`, `HabInsulation`, `GlassAlpha`.
Storm: `StormDamage`. Camera: `ChaseCamera`, `Distance`, `Height`, `LimitSeatedHead`. Fixes: `LiftFix`. Each has a
description in the file. The `Log*` switches write diagnostics to the game's `Player.log` (lines start with
`[RoverCargo]`) — useful for bug reports.

## Credits
Rover (Cargo) Returns by BillBrasky. An original model and code, inspired by the Cargo Rover that was once part of
Stationeers; it borrows the game's own Rover Mk I, its kit and materials at runtime and ships none of the game's files.

## Building from source
- **The mod:** `dotnet build -c Release` (set `GameDir` in `Stationeers.RoverCargo.csproj` to your Stationeers folder;
  it references the game's and BepInEx's assemblies). `python tools/package_mod.py <out dir>` builds the upload folder
  and checks it.
- **The models:** generated from code — `blender -b --factory-startup --python-exit-code 1 --python tools/export_all.py -- .`
  (Blender 5.1) rewrites `RoverAssets/`, `TrailerAssets/` and `HabAssets/`. The model checks that measure against game
  items (e.g. a crate in a bay) need a local reference scene with the game's own meshes, which is not part of this repo;
  `tools/make_blend.py -- <reference .blend> <new .blend>` builds a working scene from the code plus those references.
- **The tests:** `cd tests/HabTests && dotnet run -c Release`; `python tools/test_package_tools.py`;
  `python tools/test_make_blend.py`.
