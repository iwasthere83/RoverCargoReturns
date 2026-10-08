# Original Cargo Rover, sub-project 2: cab interior and dashboard — design

2026-09-27. Mod `mods/Stationeers.RoverCargo`. Part of the programme in `2026-09-27-original-rover-design.md` (read
its Global constraints). Status: designed with the user on 2026-09-27, section by section (screen, cab interior,
internals, testing); every section was approved, and the written spec was approved on 2026-09-27. Built 2026-09-27 (commits bd26505..aedcdb4 and the milestone-2 commit after them, plan
`docs/superpowers/plans/2026-09-27-original-rover-cab.md`); the in-game checks are at the switch-over.

Pictures beside this file:
- `2026-09-27-original-rover-dash-driving.png` (the driving page) and `2026-09-27-original-rover-dash-standby.png`
  (the standby page). Both are rendered from `2026-09-27-original-rover-dash-mockup.html`, which holds the layout
  numbers and colours (the hab status screen's palette).
- `2026-09-27-original-rover-cab-side.png` and `2026-09-27-original-rover-cab-front.png`: cab sections with the
  footwell dip, seat, dash, screen and the view line.

## Goal
Replace sub-project 1's temporary cab with the real cab: two proper seats, legroom, and a **touch status screen** in
the dash. The screen shows how the rover is doing and carries every control the temporary switches had.

## Decisions (user, 2026-09-27)
| Topic | Decision |
|---|---|
| Dashboard | "Kind of like the hab's status screen but touch screen, so turn on and off lights, turn on the air, interior light. But still showing speed/angles of the machine." |
| Touch | Tap targets: the buttons are drawn on the screen; an invisible target over each one works like any switch in the game (look and click; the tooltip says what it does). Not buttons round the frame, not a pop-up panel |
| Power | On the screen. Rover off: a standby page with only a big POWER button and the battery %. Battery flat: the screen is dark. Standby stays POWER only (asked again: the cabin light, which works while the rover is off, is switched from the driving page) |
| Screen | As the mockups (approved) |
| Cab | Footwell dip for legroom, real seats, a low dash console with the screen, a ceiling light; the temporary switches go (approved) |
| Delivery | As the programme: nothing changes the installed mod before the switch-over (sub-project 6); the first in-game test is there; phone reviews at 2 milestones |

## 1. The screen
- **Where:** one screen in the middle of the dash, face about **0.60 x 0.34 m** (a canvas of 1200 x 680 units at
  0.5 mm), set into the dash's sloped top and facing both seats. The whole dash and screen stay **below the
  windscreen's lower view line** from both eye points (`CameraDriver`, `CameraPassenger`), so the view over the nose
  is as today.
- **Pages:**
  - **Driving**: the rover is on and the batteries hold charge.
  - **Standby**: the rover is off. Only the POWER button (large, in the centre) and "Rover off · battery N%".
  - **Dark**: no charge (flat or missing batteries). Nothing is drawn.
- **Driving page** (the mockup):
  - **Header:** "ROVER"; a state badge, DRIVING, or PARKED below 0.2 m/s; a tag "TOWING HAB" / "TOWING TRAILER"
    while one is hitched; the clock (as on the hab screen).
  - **Gauges:**
    - SPEED: a ring to the rover's max speed setting, in km/h.
    - PITCH and ROLL: a side and a rear picture of the rover tilting with it, the angle in degrees, and words
      ("nose up", "right side down").
    - BATTERY: a ring in %, the number of cells and the power use.
  - **Cards:**
    - CABIN: pressure kPa, O2 %, °C.
    - AIR TANK and WASTE: pressure and % full.
    - BAYS: filled / total and the doors. This card appears once sub-project 3 makes the bays work; until then the
      cards share its width.
    - WARNINGS: up to three lines, the most serious first.
  - **Buttons:** POWER, HEADLIGHTS, CABIN LIGHT, AIR PUMP, FILTER, THRUSTERS. Green = on, grey = off. THRUSTERS is a
    dashed "not fitted" button until the thruster upgrade is fitted.
- **Colours and warnings** (starting values, tuned in game at the switch-over):

| What | Yellow | Red |
|---|---|---|
| Pitch or roll | 12 deg | 25 deg |
| Battery | below 15 % | below 5 % |
| Cabin pressure (rover on) | below 80 kPa | below 50 kPa |
| Cabin O2 | below 16 % | — |
| Air tank (air pump on) | below 20 % | empty or no canister |
| Waste tank | above 80 % | above 95 % |
| Filter on with no filter fitted | yes | — |

- **Refresh:** button states every frame; speed, pitch and roll 5 times a second; everything else once a second.
- **Multiplayer:** every player's game draws its own screen from values it already has (synced rover state), as the
  hab screen does. Anyone in the cab can tap.

## 2. Controls (the tap targets)
- The six controls keep their interactable keys and actions, so saves and sync work as today: POWER = `OnOff`,
  HEADLIGHTS = `Button1`, CABIN LIGHT = `Button2`, AIR PUMP = `Import`, FILTER = `Export`, THRUSTERS = `Button12`.
  Their tooltips stay the game's and the mod's ("Air Pump On", "Filtration Off", "Thrusters On", ...).
- Each control's collider becomes a thin box over its drawn button on the screen face. They replace the copied
  console switches.
- **Standby and dark:** only POWER's target is on, and it moves to the big centre button; the other five are off, so
  no hidden button can be hit. **Driving:** all six are on, except THRUSTERS when no thrusters are fitted.
- The cabin light keeps working while the rover is off, as today (it needs only charge). It is switched from the
  driving page.
- The targets come from the same layout as the drawing, so a target always sits on its button.
- Tapping from a seat works like the rover's switches today. The game turns the cursor off while you are seated,
  until you use its mouse-control (free look) mode.
- Getting in stays as sub-project 1's review fix made it: each seat's click box is on its cab door
  (`EntryDriver`, `EntryPassenger`), outside the solid cab collider.

## 3. Cab interior
- **Footwell dip** (legroom): between the front wheels, **1.2 m wide inside** (|x| < 0.60, walls to 0.66), floor at
  **1.40**. That is 0.19 m under the seat anchor (1.59), the same seat-to-floor offset as the 2020 rover, so the
  game's seated pose fits as it did there. It runs from the step (z 1.40) forward under the dash to z 2.45. The floor
  over the wheels stays at the sides (1.82). The keel drops under the dip.
- **Seats:** two seats at the existing anchors (`SeatDriver`, `SeatPassenger` at (±0.45, 1.59, 1.15); the camera
  points do not move). Each seat has a faceted shell, a cushion with its top at the anchor height, a backrest and a
  headrest. Charcoal, with an orange edge.
- **Dash console:** across the cab between the side walls. The legs go under it; its underside over the dip is at
  1.80 or higher. The screen sits in its sloped top, in a black bezel. Charcoal.
- **Ceiling light:** a panel at the `CabinLight` anchor: a white lens in a black frame. The game's cabin light moves
  there.
- **Removed:** the temporary cab parts (box seats, footwell slab, dash) and the six `Switch.*` anchors. The slot
  sockets stay (sub-project 3 decides the slot panel).
- The model rules of sub-project 1 hold: parts meet by burying joints, no coplanar faces, closed parts face outward;
  checked from all angles.

## 4. Code and data
- **`RoverRules` (pure, tested):**
  - a dashboard function: inputs → page, badge and tags, gauge values and colours, cards, warnings, button states;
  - the tap-target layout: page + thrusters fitted → a rectangle (screen units) per control;
  - the thresholds above as named constants.
- **`RoverStatusScreen`** (a MonoBehaviour like `HabStatusScreen`): built at runtime on each rover instance, not on
  the prefab, on a new `Screen` anchor. It draws the dashboard function's output and places and switches the tap
  colliders for the page.
- **Shared drawing helpers:** the font lookup, panel and ring sprites, labels and arcs move out of
  `HabStatusScreen` into one shared class; both screens use it. This is a pure move with no behaviour change.
- **`BuildRoverFromJson`:** no switch copies. The six interactables get their tap colliders under the `Screen`
  anchor. The builder stays unwired until sub-project 6.
- **`rover.json`:** a `Screen` anchor (the face centre, rotation and size) replaces the six `Switch.*` anchors, plus
  the new cab meshes. `RoverRules.NamedAnchors`, `check_rover.py` and the HabTests anchor checks follow.
- **Failure:** if the game's font is missing, or a refresh throws, the screen turns off and logs why, once. The tap
  targets stay on the driving layout and keep working, so the rover can still be driven and switched.
- **Data:** speed and angles from the rover's rigid body and transform; the batteries and canisters from their
  slots; the cabin from the rover's internal atmosphere; towing from the hitch; thrusters from the upgrade state.

## 5. Testing and acceptance
- **Code tests (`_tools/HabTests`):**
  - the page for every power and charge case;
  - each warning at and around its threshold, and their order;
  - which buttons show and which are active per page;
  - every tap target inside the screen, on its drawn button, and no two overlapping;
  - every tap target in reach of both seats (no solid collider between: the check sub-project 1 added for the
    switches), and the door entry boxes still clickable from outside;
  - the new anchors.
- **Blender checks:**
  - the dash and screen stay under the view line from both eye points;
  - the screen faces both eyes within 45 degrees and within reach (1.4 m from each eye);
  - the footwell dip clears the steered tyres (the steering sweep already tests every body part);
  - a simple leg shape from each seat into the dip touches nothing;
  - all sub-project 1 checks still pass.
- **Phone reviews:**
  1. The cab interior in Blender: seats, dip, dash and light.
  2. The cab with the mockup shown on the screen. The real screen is drawn only in the game, so the mockup stands in.
- **Done when:** the checks and tests pass, the user approves both milestones, and `rover.json` validates.

## 6. Checks at the switch-over (in game)
- The seated pose in the dip, from both seats.
- Getting in from both doors.
- Every button hit from both seats; the tooltips; the targets on the standby page.
- Readability while driving (text size at about 1.3 m).
- The screen on every player's game in multiplayer.
