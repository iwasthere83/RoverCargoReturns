# Original rover programme: switch-over and Workshop prep (sub-project 6)

Part of the original Cargo Rover programme (`2026-09-27-original-rover-design.md`, sub-project 6). Goal: a mod folder that
can go on the Steam Workshop as it stands — only our own files, no trace of the 2020 extraction, old saves still loading —
plus its own public source repository.

## Decisions (the user, 2026-10-08)
| Topic | Decision |
|---|---|
| 2020 path | **Deleted outright**; the last 2020-capable commit is tagged `rover-2020` in this workspace first |
| Scope | Switch-over **and** Workshop prep (uploading stays the user's) |
| Author / version | **BillBrasky**, **0.2.0** |
| Preview image | The user's screenshot `Screenshot 2026-10-08 090714.png` (rover towing the hab, stairs down, loaded cargo trailer on Mars) |
| Settings | Drop `OriginalRover`, `RoverCenterOfMassOffset`, `BallHitch`; keep every gameplay setting and the `Log*` switches (off by default) |
| Repo | A **new public GitHub repo** on the user's personal account `iwasthere83`, **fresh start** (one commit), **MIT** license; contains the mod, the Blender tools, the tests (HabTests) and the rover programme's design docs |

## 1. Switch-over
1. **Tag** `rover-2020` on the last commit that can still build the 2020 rover (today's HEAD, before any removal).
2. **Removed:**
   - code: the 2020 rover builder (`BuildRover` and the node, component and material reconstruction from
     `layout.json`), the bolt-on hitch receiver, the 2020 rover's upgrade parts, the 2020 branches of the legacy frame and
     kit, `RoverRules.PickRover`, the extraction load in `Plugin.cs`;
   - `CargoLayout` keeps only our asset-folder loader (json, `.rcm` meshes, textures);
   - config: `OriginalRover`, `RoverCenterOfMassOffset`, `BallHitch` (the setting and its test code);
   - tools: `tools/extract_rover_cargo.py`, `tools/check_layout.py`;
   - data: `trailer.json`'s 2020-only entries (the receiver mesh `RoverHitch`, the 2020 rover's upgrade parts) and their
     export code and meshes;
   - the HabTests that covered the removed code.
3. **Follows from it:** the rover's frame always uses its own build stages (the Mk I look remains only for missing data);
   the deferred "2020 kit mesh" item disappears; the installed `Assets.disabled` folder is deleted at install (asked
   first).
4. **Old saves:** saves made with the 2020 model load as the new rover (same `RoverCargo` prefab name, slots 0-15 keep
   their meaning); a leftover `OriginalRover` line in a cfg is ignored.

## 2. Workshop prep
1. **`About/About.xml`:** Name "Rover (Cargo) Returns", Author BillBrasky, Version 0.2.0 (`Plugin.Version` matches),
   ModID unchanged (`stationeers.rovercargo`), tags, and this description (approved with the spec):

   > Brings back a pressurised 6-wheel Cargo Rover for today's Stationeers — an original model, inspired by the Cargo
   > Rover the game once had — with two trailers to tow behind it: a cargo trailer with six bays for crates or portable
   > tanks, and a walk-in habitat trailer with its own air, water, power and a deployable living space.
   >
   > The rover: a sealed cab with working air (O2 supply, filtration into the waste tank, climate control), a touch
   > dashboard, covered side bays, gas, filter and battery slots, headlights and work lights, and optional storm armour,
   > fairings and thrusters. Print the kits on the Electronics Printer (Tier Two), place the frame and build it up in
   > stages; take it apart again with a drill. A spray can paints the body. Also adds a chase camera for all rovers.
   >
   > This mod ships no game assets. Everyone in a multiplayer game needs it. Settings in RoverCargo.cfg.

2. **Images** from the user's screenshot: `About/Preview.png` (16:9 crop, the in-game mod list) and `About/thumb.png`
   (square crop under 1 MB, the Workshop upload). Both sent to the user before they ship.
3. **README** rewritten for players: the three vehicles, their kits and costs, the wrench (hitching, bay doors), the hab's
   deploy panel, drill teardown, spray paint, the settings, multiplayer, credits. No extraction steps.
4. **Deferred items folded in:** English names and descriptions for `ItemKitRoverFrame` and `StructureRover`; the kits'
   and frames' per-colour icons (`Thumbnails[]`) use our icon, so no Mk I icon shows in any paint colour.
5. **Clean upload folder:** a packaging step builds the mod folder from the repo; a check confirms it holds only the
   DLL, `About/`, `GameData/`, `RoverAssets/`, `TrailerAssets/`, `HabAssets/` and the README. For the upload the user's
   installed folder is replaced by that clean folder, the user's own `RoverCargo.cfg` set aside first and put back after.

## 3. The public repo
- **Account:** `iwasthere83` (the gh CLI's active account). **Commit identity:** BillBrasky,
  `45979447+iwasthere83@users.noreply.github.com` — no other email in the repo.
- **One commit**, the accepted switch-over build; this workspace keeps the full history.
- **Contents:** the mod (source, `GameData/`, the three asset folders, `About/`, README), `tools/*.py` (the Blender
  model, export, check and render scripts), the tests (HabTests, re-pointed at the new layout), the rover programme's
  specs and plans, `LICENSE` (MIT) and a `.gitignore`.
- **Not in it:** any `.blend` (the art scene holds RocketWerkz reference meshes and links extracted textures; already
  git-ignored here for that reason), `.obj` files, extracted assets, scratch files, the user's cfg.
- **Models from code:** the exports must run from an empty Blender scene. Checks that need the game's own meshes as
  references (e.g. a crate in a bay) are documented as needing the developer's local reference scene, not shipped.
- **Before the push:** a scan of every file for RocketWerkz assets, extraction paths and personal paths/emails; the user
  approves the file list. Creating the repo and pushing are confirmed with the user at that moment.

## 4. Checks and acceptance
1. **HabTests:** the 2020 tests removed; new: no 2020 reference left in the mod's source or data (`layout.json`,
   `extract_rover_cargo`, `Assets/`, `OriginalRover`); `About.xml`'s version equals `Plugin.Version`, Author and Name
   set; the rover kit and frame have English names.
2. **Package check** (`tools/check_package.py`): only the allowed files; `thumb.png` under 1 MB; `Preview.png` and
   `About.xml` present; no cfg, `.bak`, `Assets*` or `.py`.
3. **Blender and asset checks** re-run (rover, trailer, stage; `check_rover`, `check_hab`, `check_interior`): the models
   do not change.
4. **Old-save check** (installed with the game closed): an older save with the 2020 rover (a Mars2 save) loads as the new
   rover with its contents; the current Europa save loads with frames, vehicles, paint and hitches as left;
   `Player.log` read after each.
5. **Final acceptance (the user):** both doors, the dash, the lights; bays and panel slots; hitching and towing both
   trailers; deploying the hab; one kit printed, built and taken apart; paint. The log checked for errors.
6. **Then:** tag the accepted build, decide with the user whether `erimos-abandoned` merges into `main`, create the public
   repo. The Workshop upload is the user's.

## Out of scope
- The fallback base lookup if the game removes the Mk I rover (after Workshop feedback).
- The deferred robustness minors from sub-project 5 (a half-built frame left over after a builder exception; the
  kit-registration gap).
- Uploading to the Workshop.
