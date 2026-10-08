# Original rover programme: build stages and kits (sub-project 5)

Part of the original Cargo Rover programme (`2026-09-27-original-rover-design.md`, sub-project 5). Goal: the Cargo
Rover, the Cargo Trailer and the Habitat trailer can all be printed, placed and built in stages in survival, each
frame looking like its own vehicle part-built, so the Workshop build has no Mk I frame look and no missing kits.

## Decisions (the user, 2026-10-08)
| Topic | Decision |
|---|---|
| Deconstruction | **Frames only** (vanilla): a part-built frame is taken back stage by stage, refunding each stage's materials, down to the kit. A finished vehicle stays a vehicle |
| Costs | **The proposal** (table below) |
| Printer | **Electronics Printer, Tier Two** for all three kits |
| Frame look | **Assembly sequence**: each stage shows the real vehicle part-built, cut from its own Blender model |
| Approach | Stage meshes from each vehicle's own model parts (a stage tag per part), exported into its json; the builder clones the game's rover frame and kit per vehicle |
| Mk I removed by the game | **Out of scope** — a fallback base lookup comes after Workshop feedback (see Out of scope) |

## 1. What gets built
The game's `RoverFrame` turns into its vehicle the moment it reaches its last build state, so 3 states are 2 build
steps after the kit is placed, 4 states are 3.

| | Kit (Electronics Printer T2) | State 0: kit placed | Step 1 | Step 2 | Step 3 |
|---|---|---|---|---|---|
| Rover | as today: 120 steel, 25 copper, 15 electrum, 10 constantan, 10 silicon | chassis | 10 steel sheets → hull shells | 10 plastic sheets + 4 electronic parts → rover | – |
| Trailer | 90 steel, 20 copper (no electronics metals: it has no electronics) | chassis | 10 steel sheets → deck and pods | 10 plastic sheets → trailer | – |
| Hab | as the rover kit | chassis | 20 steel sheets → floor and wall frame | 20 plastic sheets → shell panels | 8 electronic parts → hab |

- **Chassis state:** the keel, the frame rails and the arms, the wheels standing on two trestles.
- **Hull state (rover):** the shells without glass, doors or details; the hab's middle states as in the table.
- Every state in the vehicle's own colours; the last state is the complete vehicle (spawned at once).
- **Tools:** each step's material with the tool vanilla uses for it (the welder for steel sheets; the Mk I frame's hand
  tool for plastic sheets and electronic parts); deconstruction with vanilla's undo tools, refunding the stage.
- **Names:** the rover keeps `StructureRover` and `ItemKitRoverFrame` (saves). New: `StructureTrailerCargo` /
  `ItemKitTrailerCargo`, `StructureTrailerHab` / `ItemKitTrailerHab`.
- **Kits:** all three are the game's Mk I kit box (copied at runtime, as allowed), each with its own thumbnail rendered
  from our model (the creative menu hides items without one).

## 2. How it connects
- **Data:** `rover.json`, `trailer.json` and `hab.json` each gain `frame`: per state the mesh, its materials, the item
  it takes and the quantity; and the kit's thumbnail PNG.
- **Frames:** the builder clones the game's rover frame per vehicle, sets its `RoverPrefab` to that vehicle, rebuilds
  its build states from the data (mesh, box collider, tool), and the game's own completion spawns it (the trailer and
  the hab are `Rover` subclasses).
- **Kits:** a clone of the Mk I kit per vehicle, constructing its frame; the thumbnail loaded from our PNG at startup;
  recipes in the mod's `GameData` xml beside the rover kit's.
- **Placing:** each frame's placement bounds and collider span its vehicle's footprint, so a frame cannot go where the
  finished vehicle would not fit.
- **Old saves:** a part-built `StructureRover` keeps its state index; one beyond our last state is capped (it completes,
  never breaks). Finished vehicles and the 2020 fallback are unaffected.
- **Missing data:** a missing `frame`, stage mesh or thumbnail is logged; that vehicle stays spawnable but not printable,
  and the rover always builds.
- **Multiplayer:** state progress syncs through the game's structure code; nothing new is synced.

## 3. Checks
**Blender** (headless, each shown failing first where possible):
- Every model part carries exactly one stage tag; each state's mesh holds all earlier states' parts; the last state is
  the complete vehicle.
- The chassis state's wheels stand on the trestles; the trestles' feet are on the ground.
- No floating parts and no coplanar faces in any state mesh; a triangle budget per state mesh.
- Each frame's placement box covers its finished vehicle's footprint.

**HabTests:**
- Each json's `frame`: state counts 3 / 3 / 4, the item and quantity per step as in the table, every mesh and the
  thumbnail present.
- The `GameData` recipes match the table, on the Electronics Printer at Tier Two.
- An old save's state index beyond our last is capped.
- A missing `frame`, mesh or thumbnail keeps that vehicle spawnable but not printable and never stops the rover.

**Check-ins with the user:** (1) renders of each vehicle at every state (front 3/4, side); (2) the three kit
thumbnails; (3) a test build, installed only with the game closed: print each kit, place it, build it stage by stage
into its vehicle, take a part-built frame apart (materials back), load a save with a half-built rover frame.

## Out of scope
- **Fallback base lookup** (the user: after Workshop feedback): if the game ever removes or renames the Mk I rover, find
  any rover-type prefab, kit and frame instead of `Rover_MkI` / `ItemKitRoverMKI`. Today a missing Mk I stops the
  rover, trailer and hab from registering (the log says why; the chase camera still works).
- Deconstructing finished vehicles.
- The switch-over clean-up (sub-project 6).
