# Original Cargo Rover: exterior rework — design

2026-09-28. Mod `mods/Stationeers.RoverCargo`. Part of the programme in `2026-09-27-original-rover-design.md`; it comes
after sub-projects 1-3 (exterior, cab, bays) and before sub-project 4 (upgrade refit), whose parts attach to the outside.
Status: designed with the user on 2026-09-28, section by section, after the first in-game test.

Pictures beside this file (the blockout: shapes only, a design sketch that is never exported; its script is
`tools/sketch_rover_hull_2026-09-28.py`):
- `2026-09-28-original-rover-exterior-rework-body-lines.png`: body line A and B (the user chose B).
- `...-blockout-side.png`, `...-blockout-front.png`, `...-blockout-rear.png`: variant B.

## Why
First in-game test (2026-09-28): everything works (driving, every cab button, the bay doors, crates and tanks in and
out, both seats), but the look fell short of the user's reference picture (the "ARES crew rover" concept):
1. the tow arm's root hangs below the rear bumper;
2. the cab and the storage module do not line up (the cab 10 cm narrower, 8 cm lower, other roof bevels);
3. the suspension looks unfinished (a bare arm and pin);
4. the seats sit so far back that the seated camera goes through the cab's rear wall;
5. the black lower body (keel, pods, tail) reads as a block the rover sits on;
6. the nose drops straight instead of sweeping down;
7. the bull bar is chunky tubes, "a place holder", with none of the picture's angles.

## Decisions (user, 2026-09-28)
| Question | Decision |
|---|---|
| How far | Keep the layout (wheelbase, wheels, bays, slot panel and rack, dash and screen, every slot and control), rebuild the body |
| How | One continuous hull from nose to tail, not patched parts |
| Side shape | The sides lean in, the bay sides too (the bay doors get angled top panels) |
| Body line | B: the cab leans from its window sill, a clean step up to the taller storage module |
| Front end | A heavy angular bumper plus a push bar of flat angled plates round the lights |
| Details | Chassis, shocks, hitch, lights, surface detail as section 2 (no sensor turret) |
| Cab | Seats 30 cm forward; the dash and screen stay |
| Check-ins | The bare body, then the detailed body, then the test build |

## 1. Silhouette (the blockout)
- **One hull.** One side plane (|x| 1.35) and one roof (3.16) for the cab and the storage module; the cab narrows only
  at its front corner facets. The windscreen keeps its slope and lower edge (3.05, 1.95), so the view over the nose is
  as now; its top rises to the one roof (z 1.87).
- **Lower edge 1.30.** Between the front and middle wheels the silver body comes down to 1.30 as a side skirt (over
  the old black pods), with a 14 cm chine along the lower edge. At the nose and tail too.
- **Wheel arches.** Faceted arches over the front wheel and over the rear pair (one tandem arch), cut from |x| 0.70
  outward (a steered tyre reaches that far in), clear of every tyre steered 40 deg and raised 0.35 m by 3 cm. Dark
  liners line each well (3 mm into the opening, 1 cm into the hull), so an arch reads as a well.
- **Nose.** The windscreen, then a light band (the LED headlights) at 1.78-1.95, then a snout sloping down and forward
  to the bumper at 1.30.
- **Sides lean in 20 deg.** The storage module from 2.82 (the tanks stand 2.4 cm inside the doors; a tank's full radius
  ends 25 cm below its top, measured from the game's mesh, so above 2.82 only its valve stands up); the cab from its
  sill at 2.30, forward of a step at z 0.90 where the storage module starts. The tail pods lean with the module.
- **Bay doors.** Each leaf bends at 2.82 and follows the lean; its hinge moves in with its top edge (to about |x| 1.29).
  The orange frames bend at the crease too.
- **Glazing.** One dark band: the windscreen to thin pillars, the corner panes, the side windows on the cab's lean.

## 2. Details
- **Chassis:** the flat black slabs go. Frame rails along the keel (the spine the arms hinge on), cross members in the
  wheel wells, bevelled battery and equipment boxes under the skirt. Dark, but machinery, not a block.
- **Suspension:** a coil-over shock on every arm, from the frame to the arm, stretching and compressing as the arm
  swings (animated with the arms); a hub and knuckle where the arm meets the wheel.
- **Front end:** a heavy angular steel bumper (a winch in the middle, red marker lights, orange tow shackles, a skid
  plate back to the keel) and a push bar of flat angled plates framing the lights.
- **Hitch:** a receiver box bolted into the rear bumper with gussets; the arm's root sits inside it. The pin stays at
  (0, 0.80, -4.90).
- **Lights:** the headlights in the nose's light band (the Headlight anchors move there), marker lights in the bumper,
  the tail lights in the rear bumper.
- **Surface:** panel seams, a vent in an orange frame on the storage module, grab handles, the cab door outline.
- Model rules as before: joints buried EMB, never coplanar, closed parts outward; checked from all angles and against
  the reference picture at the same angle.

## 3. Cab interior
- The seats move 30 cm forward (seat and camera anchors z 1.15 -> 1.45); the seated head is then about 0.53 m from the
  rear wall (it was about 0.23 m).
- The raised floor under the seats reaches forward only between the front wheel wells (|x| < 0.70); the footwell dip
  starts where the seats end (step z 1.43 -> 1.73). The dash and screen stay: the eye is 0.97 m from the screen (was
  1.22), the view over the nose improves, the knees clear the dash by about 7 cm, the heels land 10 cm short of the
  dip's front wall.
- The cabin follows the hull: upper walls leaning in from 2.30 (0.8 m beside each head), the ceiling 8 cm higher, the
  ceiling light moved up with it.
- The cab doors' upper parts are on the lean; the get-in click boxes (EntryDriver, EntryPassenger) move onto the new
  door surface.

## 4. Underneath, and the checks
- **Unchanged:** wheelbase, wheels, bays (positions, sizes, slots), the slot panel and rack, the dash and Screen anchor,
  every slot index and interactable name (the door states on Button5-8), the hitch pin.
- **Moved anchors:** SeatDriver/Passenger, CameraDriver/Passenger, EntryDriver/Passenger, HeadlightL/R, CabinLight;
  the door leaves' hinge points.
- **New in rover.json:** the shocks (upper and lower mounts, meshes); the rover animates them with the arms.
- **Colliders** follow the new body (the lean, the skirt, the arches) and keep the bays, the recesses and the rack open.
- **Checks (all pass before any renders go to the user):** the tyre sweep (steered, bumped) against every part; arm
  and shock travel; towing; the bent doors' swing; the bay fit against the real tank and crate shapes (not their
  boxes); the slot-panel fit; the view over the nose; the screen's facing and reach; leg room; new: the seated camera
  at least 0.40 m from the cab's walls; no coplanar faces. HabTests for rover.json (anchors, click boxes, tap targets
  in reach of both cameras, bay and panel items).
- **Check-ins:** (1) the bare body (the real model's new hull, arches, lean and nose, before the details); (2) the
  detailed body (renders from every angle next to the reference); (3) the test build (installed only with the game
  closed).

## Out of scope
The sensor turret (the mast stays); the upgrade parts (sub-project 4); build stages (sub-project 5); the trailer and
hab bodies (they may be matched later).

## Revision after check-in 1 (user, 2026-09-28)
The user imported a 3D model generated from the reference (Bambu MakerLab, scaled 4.1 to match ours: its wheelbase is
within 3 %) and found the bare body "clean but like a 2015 SketchUp model": the balance wanted is Stationeers-simple
but designed. A same-scale comparison showed the gap is the body's shape and surface, not its size.

| Question | Decision |
|---|---|
| Wheels | Keep them (track, size, positions) |
| Body shape | Further than the first reshape study: steeper facets, a sharper nose, a bigger roof chamfer |
| Lower hull | Dark (gunmetal) below the belt line, silver above: the one hull split at the belt |
| Character pieces | Roof rack + light bar, a rear ladder to the roof, side steps + mud flaps (no spare tyre) |
| Detail density | Layered armour plates (1-3 cm proud, bevelled, with seams), hatches, vents, plus section 2 |

**Style rules (the balance).** (1) The silhouette carries the design: a few big, confident facets. (2) Panels in two or
three depths. (3) Colour blocking, not texture: silver panels, a gunmetal lower hull, charcoal trim, orange on frames,
handles and tow points. (4) Details that explain the machine, chunky enough to read in game (nothing under ~3 cm); no
greebles for their own sake, no curved sci-fi surfaces.

**Section (replaces "Sides lean in 20 deg" in section 1).** The storage module stands upright to 2.875 and then
chamfers 45 deg to the roof (the top crates' corners stay 2 cm inside the bent leaves; the tanks' shoulders are lower).
The cab leans 35 deg from its sill (2.30) forward of the step at z 0.90. Below the belt line (1.78) the lower hull
tucks in 30 deg to the lower edge (1.30); it is gunmetal, the upper hull silver. The nose narrows to |x| 0.72 at the
windscreen's foot (corner facets from z 2.05) and its snout leans forward to its foot at z 3.30; the windscreen keeps
its slope. The glazing, lights and roof plates follow the narrower cab.
