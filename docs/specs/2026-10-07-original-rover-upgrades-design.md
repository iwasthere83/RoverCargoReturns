# Original rover: upgrade refit (sub-project 4)

Part of the original Cargo Rover programme (`2026-09-27-original-rover-design.md`, sub-project 4). The upgrades
themselves are unchanged (`2026-09-26-storm-upgrades-design.md`): this sub-project gives them parts on the new rover
and moves the trailer and hab parts into the rover's palette.

## Why
- On the original rover every upgrade is refused today ("This model has no parts for that upgrade yet",
  `CargoRover.cs`): the only rover parts are the 2020 hull's. A Workshop player could not fit armour, fairings or
  thrusters at all.
- The trailer's and hab's upgrade parts still use the old white and grey roles. The grey shows almost white in game
  (the user's build 9 report on the hab's hardware), so they clash with the new silver hulls.

## Decisions (the user, 2026-10-07)
| Topic | Decision |
|---|---|
| Trailer and hab parts | **Recolour only.** Shapes, positions and colliders stay as signed off |
| Armour look | **Storm shutters** |
| Fairings look | **Aero skirts** (the riding fenders stay part of the fairings) |
| Thruster pods | **Roof rack corners**, nozzles up |
| Colours | **Steel gunmetal; plastic silver with a thin orange edge; thruster pods charcoal with orange nozzle rings** |
| Approach | The rover's upgrade parts are built in Blender like its body and exported into `RoverAssets/rover.json`; the builder attaches them with the shared upgrade code the trailers use |

## 1. The parts on the original rover
Our own geometry (no game assets), in the rover's palette roles.

**Storm armour (gunmetal).** It must not read as the standard push bar, lower-hull plates or side steps.
- Bar grilles over the windscreen, the corner panes and both cab side and door windows: a frame round each pane and a
  few slim vertical bars standing a few cm off the glass.
- Steel caps along the cab roof's front edge and the module roof's side edges.
- A plate across the tail between the two tail pods.
- Kept clear: the headlights, the LED bars, the roof light bar, the work lamps, the slot panel and the bay doors.

**Wind fairings (silver, thin orange edge).**
- Smooth side skirts closing the gap under the body between the wheels, with a cut-out at each cab side step.
- Fender flares fixed to the hull over each wheel arch (the front wheel's; the middle and rear pair's), following
  the arch's upper outline and reaching out past the tyre's outer face to |x| 1.86, silver with an orange lip. **Revised
  at check-in 1 (the user: the riding fenders were tiny):** both the front and the rear axle steer, so a riding fender had
  to stay a 40-degree sliver to clear the arch at full lock; the flares are big and the wheels steer under them.
- A rounded nose fairing on the front bumper's lower face.

**Thrusters (charcoal pods, orange nozzle rings).**
- One pod at each corner of the roof rack, its nozzle pointing up; the puff comes out of each nozzle point.

**Trailer and hab (recolour only).** Steel parts (skirts, armour plates, stays) gunmetal; plastic parts (fender
arches, the nose fairing, the roof deflector) silver with the orange edge.

## 2. How it connects to the game
- **Data:** `rover.json` gains `upgrades` in `trailer.json`'s format: per entry the group (Armour, Fairings,
  Thrusters), the mesh, the position, the materials, the removal colliders, `follow` for a fender, and the thruster
  entries' nozzle points.
- **Builder:** `BuildRoverFromJson` calls the shared `AddUpgradeParts`. With parts present, the "no parts" refusal
  lifts by itself; fitting, removing, refunds, thruster firing and the propellant rules are unchanged.
- **Gate:** `RoverJson.Problems` checks every upgrade mesh exists. A missing mesh is logged; the rover still builds and
  only that upgrade is refused.
- **Removal colliders** (the wrench's targets for taking a part off, triggers) never cover the bay doors, the slot
  panel, the seat entries or the dash's tap targets.
- **Fenders:** none ride on the original rover (fixed flares); the trailers keep their riding fenders.
- **Work lights:** their cone narrows from 100 to 76 degrees (the user, check-in 1), so they no longer light the flares
  below them.
- **Old saves:** the upgrade states are already saved (Button9–12); nothing new is stored. No save has upgrades on the
  original rover (they were refused); a 2020-rover save with upgrades shows the new parts.
- **Trailer and hab:** palette roles only in `tools/blender_trailer_model.py`, re-exported.

## 3. Checks
**Blender** (headless, each shown failing before the fix where possible):
- The fender flares reach past the tyres' outer face (≥ 4 cm) over every arch; the tyres at full steer and bump keep
  clear of them (the steer check with the parts).
- Skirts clear of the tyres at every steer and lift, of the arms and the shocks; the side steps stay open.
- Shutters: from both seated eyes, the bars hide at most 25 % of the view through the windscreen (rays from each eye over the pane). The
  headlight and work-light spill checks include the upgrade parts.
- The bay doors still swing open with every upgrade fitted; the slot panel, the seat entries and the dash's tap
  targets stay exposed for a click from outside (the existing exposure checks, with the parts).
- Thruster pods clear of the rack lamps, the light bar and the mast, with a clear path above each nozzle.
- Towing: the trailer's and hab's turn checks include the rear plate and the parts at the tail.
- No coplanar faces; a triangle budget per upgrade mesh (`check_rover.py`).
- Trailer and hab: `blender_trailer_checks.palette` also covers their upgrade parts (no white, no grey).

**HabTests:** `rover.json` lists all three groups with existing meshes; the original-rover builder attaches them.

**Check-ins with the user:** (1) renders of the rover with each upgrade (front, side, back, top, 3/4); (2) the trailer
and hab recolour renders; (3) a test build, installed only with the game closed: fit and remove each upgrade on each
vehicle, look out through the shutters from the driver's seat, fire the thrusters, drive over bumps for the fenders.

## Out of scope
- New shapes for the trailer and hab upgrades.
- Changes to what the upgrades do, cost or how they are fitted.
- Multiplayer testing (still open programme-wide).
