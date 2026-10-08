# Original Cargo Rover, sub-project 3: covered bays and slot panel — design

2026-09-27, overnight. Mod `mods/Stationeers.RoverCargo`. Part of the programme in `2026-09-27-original-rover-design.md`.
Status: designed by Claude on the user's delegation ("I'm going to go to bed, going to approve all things to get this
ready for testing tomorrow morning. I trust your judgment"). Every open question below was decided here; the user
reviews the decisions with the test build.

**Revised 2026-09-30 (the user, after test build 5): one crate a bay** (its bottom slot; the top crate slots stay for
saves, never filled), or two tanks: a closed crate with its lid is 0.73 m tall (measured as 0.55 at first), a bay 1.22 m
inside, so a stacked top crate came through its closed door. A crate's lid opens only with its bay door open, and the
rover closes a lid when the door shuts. The dashboard's BAYS card counts full bays of four.

## Decisions (made on the user's behalf)
| Question (programme spec) | Decision | Why |
|---|---|---|
| Door motion | Each leaf is hinged along its top edge, just outside the side face (2 cm out, 1 cm above the opening), and swings up and out to 100 deg in 1 s: an awning over the bay | the bays run end to end (a sliding leaf has nowhere to go); a bottom hinge would hit the tyres; an open leaf stands above head height (3.05 m) |
| Opening | A wrench on a door toggles it (already decided) | |
| Loading | A crate or tank attaches only into a bay whose door is open: the open bay nearest the item that accepts it (`RoverRules.BaySlotFor`: crates stack bottom then top, tanks front then rear, never mixed) | a closed door keeps the bay shut; the player picks the bay by where they stand the item, as on the trailer |
| Unloading | Through an open door (a closed leaf blocks the cursor). A crate or tank let go (the wrench's Disconnect) lands on the ground beside its door, 2.7 m out from the middle (clear of the tyres at full steer), the top crate above the bottom one | vanilla releases it where it sits: loose inside a bay or tipped out of it (final review I5) |
| Door state | Four interactables, Button5-8 (L1, R1, L2, R2), state 1 = open: synced by the game and restored from saves (as the upgrade flags) | Button1-2 and 9-12 are taken; vanilla Thing.HasState restores Button1-3 itself and puts a saved Button3 on Button1 (the headlights), so the doors skip 3-4 (final review I1) |
| Slot items (measured) | Sizes from the game's own meshes (the local install's resources.assets, read with UnityPy, 2026-09-27): a gas canister is 0.207 x 0.874 m (0.222 across for the smart canister), its origin 0.194 m below the valve end and 0.680 m above the bottom; a filter 0.188 x 0.287 (origin 4.75 cm above its middle); the chip 0.179 x 0.251 x 0.111 (its prefab is scaled 1.11); a battery cell 0.098 x 0.169; the closed crate 1.839 x 0.553 x 0.730 (its open lid rises to 0.732); the portable tank 0.813 x 1.177 | the sub-project 1 layout assumed the 2020 trigger boxes were the items: a canister is twice as long as its trigger |
| Slot panel | The recesses stay 25 cm deep (back wall at \|x\| 1.10) and grow to z 0.30-0.83. **Left:** the four canisters lie across the rover in a rack, as on the 2020 rover: valve end in, the bottom 4 cm inside the side face, in a deep pocket (z 0.31-0.59, h 1.92-2.91, back wall at \|x\| 0.40) behind the recess; Air1 at the bottom, then Air2, Air3 and Waste at the top (the 2020 order). The two filters stand in front of the rack (Filter1 above Filter2). **Right:** the chip above the three batteries in a row. Every item keeps 1 cm or more from the walls and 1.5 cm from its neighbours; black sockets behind the items and a valve socket at the back of each rack tube | a canister cannot stand in the 1.07 m recess two high, nor four abreast in its 0.53 m; lying across it needs only 0.24 m of the panel's length |
| Canister click box | The 2020 trigger: 0.225 x 0.429 x 0.225 over the canister's outer half (centre 0.474 m out from the origin), at the rack's mouth. Other click boxes cover their items (filter 0.20 x 0.30 x 0.20 on its body, chip 0.19 x 0.26 x 0.12, battery 0.10 x 0.17 x 0.10) | the cursor needs the trigger where the player looks: at the mouth |
| Vent lever | Not in this sub-project | not needed for the test; no design yet |
| Colliders | The bays are hollow in the collider set: the spine, a floor slab (1 cm under the crates), a roof slab, the divider and the tail block; the canister rack's pocket is open too. Each door leaf carries its own trigger box collider, which moves with it: a shut leaf still stops the cursor (one raycast, triggers included), an opening leaf shoves nothing and adds no mass, and UpgradeCursor lets the wrench onto it (final review I6: a solid leaf swinging out 1.2 m at 3 m up would push or tip the rover beside a wall) | crates in the bays must be clickable through an open door (the cursor is one raycast). (A slotted item's own colliders are switched off by the game, DynamicThing.SetPhysics(false), so only the click boxes and the line of sight matter) |
| Dash BAYS card | items in the bays / 8, and the number of open doors | |
| Tooltip | the rover's extended text lists the open doors and says a wrench opens them | there is no other feedback when an attach is refused |

## Design
- **Rules (`RoverRules`, pure, tested):** the door table (leaf name, bay, action, side), the open angle and time,
  `DoorAngle(side, open fraction)`, `PickBaySlot(tank, door open, occupied, distance)` (nearest open bay first, then
  `BaySlotFor`), the measured item bodies (`ItemBody`), the click-box centre and size per slot kind, the panel
  openings (both recesses and the rack), the door states in the interactable table, the bays' names for players.
- **Model (Blender):** the recess z range 0.30-0.83, the canister rack pocket, the anchors re-laid (`PANEL`: each
  item's origin and rotation), the sockets behind the new items, a `hinge` custom property on each door leaf
  (exported into rover.json `parts`), the bay-hollow colliders (and the leaves in the centre of mass). Checks: the
  door swing (0-100 deg in 5 deg steps: no leaf triangle crosses a body part, no leaf vertex within 2 mm of one),
  and every panel item's measured body inside its recess or the rack and clear of its neighbours.
- **Builder:** each door leaf hangs under a hinge node at its `hinge` point, with a box collider from the leaf mesh;
  the four door interactables; the hinge nodes and colliders stored on `CargoRover`.
- **Rover:** the doors turn toward their state every frame (every machine, from the synced state); a wrench on a leaf
  toggles it (the collider, or on the server the leaf within 5 cm of the hit point); the saved door states are
  restored; `DashInputs` reports the bays; the extended text lists the open doors.
- **Attach:** the `Rover.Attach` prefix (`TrailerBedRule`) handles the original rover with `PickBaySlot` and
  `OnServer.MoveToSlot`.

## Testing
- **HabTests:** the door table; `DoorAngle` (a point at the leaf bottom swings outward on either side); `PickBaySlot`
  (nearest open bay first, closed bays refused, full bays skipped, the crate/tank rules); the click boxes on their
  items; every panel item's measured body inside its recess or the rack and clear of the others (from rover.json,
  rotations included); every bay item clear of the solid colliders; the rover.json gate names a door without its
  hinge; source contracts for the builder, the rover and the attach prefix.
- **Blender:** `door_swing`, `panel_fit`, and every earlier check.
- **In game (the test build):** open and close each door with a wrench; load 2 crates or 2 tanks per bay; a closed
  bay refuses; unload through the door; the doors in multiplayer and after a save and reload; the canisters, filters,
  chip and batteries seated in the recesses and the rack; whether a crate can be opened in a top bay slot (its open
  lid would dip 7 cm into the bay roof: a known clash from the stacked layout).
- A wrench on a door with an upgrade's material in the other hand fits the upgrade (the door stays as it is).
- The upgrades (armour, fairings, thrusters) have no parts on this model yet (sub-project 4): fitting one is refused; flags
  from an older save carry over but show nothing.
