# POE2 AutoMate Update Log

## 2026-06-18 - Map Load Settling, Walkable Routing, BossRush Atlas Progress

### Context
- User reported that the bot handed control to map clear after about 2.7s instead of waiting at least 5s.
- User reported boss routing still walking into walls instead of following walkable cells.
- User showed an atlas route where Boss Rush moved left instead of progressing down/right toward the boss-interest route.
- User reported overall movement felt slow.

### Done
- `WaitForMap` now requires at least 5000ms after the map is actually detected as ready. The timer resets if
  ready signals disappear, instead of counting only from state entry.
- `MapSettleMs` default is now 5000ms.
- `RoutePlanner` no longer returns the raw target point when terrain pathfinding fails. It expands search,
  blocks diagonal corner-cutting, and can return a partial route to the closest reachable cell instead of a
  direct wall path.
- `CombatBot` refuses direct movement to a target when terrain exists but no walkable route was built.
- Boss landmark movement no longer falls back to direct landmark coordinates when the route is exhausted.
- Boss Rush atlas selection now prefers primary boss-interest nodes (`Deadly Map Boss` / UberBoss), uses generic
  Boss/Unique only as fallback, and requires selected runnable maps to make progress toward the boss-interest target.
- Combat movement cooldown default reduced from 450ms to 160ms; runtime clamps saved old values to 60-220ms so
  stale settings no longer make movement feel sticky.
- Release build passed with 0 warnings / 0 errors.

### Still Needed
- Live validate atlas graph behavior on the user's route. The current selector scores by grid progress because
  explicit atlas edge/path decoding is not implemented yet; a future patch should decode atlas edges and draw
  the planned route before selecting maps.

## 2026-06-18 - BossRush Target Discipline and Boss Combo Recast

### Context
- User reported that Boss Rush was still behaving like full clear: HUD showed `ClearMap`, the bot shot
  barrels/scenery, probed dormant mobs near a boss entrance, and retargeted boss attack entities.
- User also asked for boss combo/precast keys to have a separate recast delay instead of repeating too often.

### Done
- Boss Rush target selection no longer falls back to arbitrary nearest monsters. It now targets:
  - nearby path-clear threats;
  - the visible boss;
  - otherwise no target, so movement follows decoded boss landmarks/explore route.
- Boss Rush path-clear threat radius is now tightened around the player/cast radius instead of using the
  wider full-clear radius, reducing wasted shots into dormant packs near boss gates.
- Dormant non-boss entities without readable life are skipped in Boss Rush, preventing long probing loops
  against inactive ambush packs before a trigger/gate.
- Expanded non-combat filters for barrels, barricades, breakables, destructibles, palisades/fences, urns,
  vases and similar scenery that can decode through monster/entity paths.
- Expanded combat metadata ignores for common effect/helper entities such as ground effects, beams, novas,
  explosions, impacts, daemon/decoy helpers, hazards, telegraphs, triggered effects and volatiles.
- HUD/main AutoMapper display now shows `BossRush` while the internal state machine is technically in the
  map-clearing stage, so the overlay matches the selected strategy.
- Added `Boss/Unique combo recast ms` setting. Unique/boss opener combo can now repeat on its own slower
  cooldown while normal ability casting remains fast.
- Reworked the Combat menu from one long scroll into compact workflow tabs:
  `Core`, `Skills`, `Movement`, `Loot`, `Survival`, `Advanced`.
- Moved Combat live state/action into a fixed bottom status panel so it is visible without scrolling.
- Added dark/minimal TabControl/TabItem styling to match the existing WPF panel theme.
- Switched module status logging from blocking `Dispatcher.Invoke` to non-blocking `Dispatcher.InvokeAsync`
  so combat/automapper updates do not wait on UI log rendering.
- Release build passed with 0 warnings / 0 errors.

### Still Needed
- Live validate metadata for the specific dormant packs/boss attacks on the user's maps. If any still pass
  the filter, dump their metadata from logs and add exact signatures rather than broad ignores.
- Continue the larger UI cleanup/optimization pass so settings are grouped by workflow instead of long scrolls.

## 2026-06-18 - Boss Landmarks, Skill Bind Scan, Combat UI Sections

### Context
- User asked to start completing the remaining plan items 1-6 in order: boss landmarks from POE2Radar,
  Boss Rush routing, load-state stability, combat priority, skill/bind parsing, and large Combat UI cleanup.

### Done
- Ported POE2Radar-style terrain landmark foundation:
  - copied `CustomLandmarks.json` from the reference repo;
  - embedded it in `POE2_AutoMate.Core`;
  - added `CustomLandmarkData` area/tile matcher with keyword fallback;
  - added terrain tile-path landmark scanning from `TerrainMetadata.TileDetailsPtr`;
  - `GameSnapshot` now carries `Landmarks`.
- Boss Rush / boss hunt can now route toward decoded boss/arena/final landmarks when a live boss entity is
  not visible yet. This uses A* through existing `RoutePlanner` and keeps normal entity targeting intact.
- Combat target priority keeps path clearing: nearby monsters inside path-clear radius are selected before
  a far boss target, so the bot should not simply run through packs.
- Added `SkillLoadoutReader` foundation:
  - reads `Documents/My Games/Path of Exile 2/poe2_production_Config.ini`;
  - parses `[WASD_ACTION_KEYS]` / `[ACTION_KEYS]`;
  - extracts `use_bound_skill*`, temporary skills, and flask slot binds;
  - converts common virtual key codes to readable tokens (`Q`, `E`, `R`, `LMB`, `RMB`, etc.).
- Added Combat UI actions:
  - `Scan skill binds`;
  - `Use scanned skill keys`.
- Split the Combat screen visually into operational sections: `Core`, `Skills`, `Loot`, `Movement`,
  `Survival`, `Advanced`. This is a first-pass cleanup that preserves existing bindings.
- Release build passed with 0 warnings / 0 errors.

### Still Needed
- Live validate landmark quality per endgame map. The POE2Radar method depends on map-specific tile labels;
  if a map has no matching curated boss tile, Boss Rush falls back to frontier exploration.
- Add a proper skill-bar memory/UI reader for actual skill names. The current reader knows bound slot keys,
  not the skill names/icons occupying those slots.
- Finish deeper UI redesign with tabs/accordions after combat stability is confirmed.

## 2026-06-18 - Combat Delay and Portal Bounce Fix

### Context
- User reported a severe new delay, map portal bouncing after short load waits, and Boss Rush running into
  monster packs instead of clearing the path.

### Done
- Disabled heavy full snapshots during active combat/ClearMap. Combat now stays on lightweight snapshots
  instead of reading inventory + full UI tree every second.
- Added `WaitForMap` after clicking an entry portal. The bot now waits for a different area plus active map
  signals to settle before handing control to ClearMap, instead of immediately reacting to stale hideout
  snapshots.
- Added route-clearing target priority: while Boss Rush target is far away, nearby monsters inside the
  path-clear radius are killed first.
- Removed extra per-key sleep from combat ability/combo sequences; `PressKey` already has its own small
  input pacing.
- Release build passed with 0 warnings / 0 errors.

### Next
- Port POE2Radar-style landmarks: terrain tile paths -> clustered landmarks -> boss/arena route target.
  Current bot has terrain/entities/atlas nodes, but not the full boss-landmark table yet.

## 2026-06-18 - Emergency Input Safety Fix

### Context
- User reported that the app moved the mouse toward the top of the screen even when the bot was not enabled,
  including on the desktop.

### Done
- Fixed the death/revive guard. It now only runs when Combat or AutoStart is actually running and that
  module has active input enabled.
- Removed the broad standalone `Checkpoint` revive text match, which could false-positive on non-death UI.
- `SPACE` revive fallback now only fires when memory vitals explicitly report HP <= 0.
- Added startup safety: persisted `Enabled` and `ActiveInput` flags are reset to false on app launch, so
  stale settings from a previous run cannot arm input by themselves.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-18 - Boss Gate, Frontier Explore, Revive Hardening

### Context
- User reported that the bot still attacks Expedition objects/Azmiri spirits, leaves maps before the boss
  is killed, loops locally during exploration, fails to revive reliably, repeats boss combo keys until mana
  is empty, and can start the next run before the hideout has fully loaded.
- User also asked whether character skills and their bound keys can be parsed automatically.

### Done
- Added broader non-combat filtering for Expedition and Azmeri/Azmiri metadata so those entities are not
  selected as combat targets or encounter goals.
- Added a boss-kill completion gate:
  - AutoStart has `Require boss kill before leaving map`;
  - `BossRush` forces that gate on;
  - combat reports `Boss Hunt` instead of `Map Done` when the monster threshold is reached but boss kill
    is not confirmed.
- Boss targets are prioritized in `BossRush`.
- Unique/boss combo keys now behave as a one-time opener by default. A new UI checkbox,
  `Repeat boss/unique combo instead of one opener`, restores the old repeating behavior when needed.
- Added boss invulnerability handling: when a boss has no HP progress in range, the bot temporarily
  retargets nearby adds; if no adds are nearby it performs chaotic movement plus dodge instead of standing
  still.
- Exploration now uses decoded terrain frontier routing instead of only local circular movement:
  the bot remembers visited walkable cells and plans an A* route toward distant unvisited walkable cells.
- Added route-planner support for arbitrary world points, not only entity targets.
- Added `WaitForHideout` between return portal and dump/next-run. It waits for hideout anchors to be stable
  for several ticks plus a settle delay before dumping inventory or starting the next map.
- Revive detection no longer depends only on valid vitals. If the death UI button is decoded, the bot can
  click it even when the player vitals snapshot is incomplete.
- Main window was widened, and the newest high-impact toggles were surfaced in the UI instead of hiding in
  internal defaults.
- Release build passed with 0 warnings / 0 errors using:
  `dotnet build POE2_AutoMate.sln --configuration Release --no-restore -p:UseSharedCompilation=false`.

### Next
- Add `SkillLoadoutReader`:
  - first read keybinds from the game config/UI action bar where possible;
  - then map visible action slots to skill names from memory/UI;
  - show detected skills in Combat as selectable abilities/openers instead of forcing raw key text boxes.
- Port more of POE2Radar's landmark model for full-map boss navigation. We already use terrain/entities,
  but true boss-route quality needs map-specific boss landmark labels or reliable boss-interest entity tags.
- Rework Combat UI into dense tabs/sections (`Core`, `Skills`, `Survival`, `Movement`, `Advanced`) instead
  of one long scroll. The current patch only surfaced the urgent toggles.
- Add a live debug reason panel for ignored entities (`Expedition`, `Azmiri`, `dead`, `no line of sight`,
  `temporary boss phase ignore`) to make tuning faster.

## 2026-06-18 - Loot Filters, Full Map Overlay, Boss Rush Preset

### Context
- User reported weak loot clicking, wanted currency-only pickup/filtering, full currency loot statistics
  with divine value to 4 decimals, original-style full minimap view in our overlay, and mapping presets:
  full clear vs boss rush toward `Deadly Map Boss` atlas interest nodes.

### Done
- Added visible loot-label targeting: combat now first asks the memory UI reader for the nearest visible
  text label around a projected loot entity and clicks that true label center; old offset-click sweep remains
  as fallback.
- Added loot pickup filters in the Loot screen:
  - `Currency`
  - `Gear/items`
  - `Other drops`
  Ground loot entities are filtered by metadata before the bot clicks them.
- Added shared loot classification so dumped loot stats show concrete currency buckets (`Divine Orb`,
  `Exalted Orb`, `Orb of Alchemy`, etc.) while non-currency items stay aggregated by rarity.
- Loot statistics now show currency lines with `count = 0.0000 div`; the compact HUD also prints divine
  value to 4 decimals.
- Added `Full map` overlay mode. It renders the whole decoded terrain buffer scaled into the overlay window,
  instead of only the local player radius. This is our external radar/full-map view; it does not patch the
  game client minimap.
- Increased combat snapshot entity cap to improve breach monster visibility when effects/loot consume the
  old entity budget.
- Expedition encounters are now skipped more aggressively by metadata, not only by the decoded encounter
  kind.
- Proximal/no-damage targets are handled differently: if no HP progress happens while the target is still
  farther than the proximity range, the bot moves closer instead of treating it as permanently invulnerable.
- Added AutoStart preset selector:
  - `Full Map Clear`: current full-clear behavior.
  - `Boss Rush Clear`: disables optional encounter targeting and atlas selection routes toward boss-interest
    nodes while refusing to select the final boss-interest node itself.
- Release build passed with 0 warnings / 0 errors using:
  `dotnet build POE2_AutoMate.sln --configuration Release --no-restore -p:UseSharedCompilation=false`.

### Next
- Validate atlas tags for real `Deadly Map Boss` nodes from live dumps; if tags are stable, add a dedicated
  full-screen click-through atlas route overlay that draws the selected path over the atlas before enabling
  automatic path traversal.
- Improve stash/inventory item name decoding beyond metadata path heuristics so unknown currency names become
  exact display names.
- Add a small loot diagnostic panel showing rejected ground loot reason (`currency off`, `gear off`,
  `monster too close`, `label not found`) to tune pickup behavior faster.

## 2026-06-18 - Portal Retry, Kiting, Revive, Flask Split

### Context
- User reported that map portal activation sometimes creates portals but the bot does not actually enter,
  inactive/invulnerable monsters can trap combat, flasks are unreliable, and map deaths need auto-revive.

### Done
- `EnterPortal` now retries portal clicks if the area does not change quickly: it orders portal candidates
  near the map device/player, tries alternate portal/click offsets, and no longer waits the whole map-load
  timeout after one bad click.
- Combat now ignores a target temporarily when it has been in cast range for a few seconds with no HP
  progress, which covers inactive/invulnerable statue-style monsters until they are awakened.
- Added effective pool kiting: Life + Energy Shield + Ward fields; ES is read from memory now, Ward is wired
  as a field for a later offset. If effective pool is below the configured percent, the bot holds a random
  WASD direction, presses the dodge key (default `SPACE`), then releases movement.
- HP and mana flasks now have separate cooldown timers and can both fire when both pools are low; one flask
  no longer blocks the other through a shared cooldown.
- Added death handling in the live loop: when HP reaches 0 during AutoStart/Combat, combat stops and the UI
  revive button is clicked from memory when decoded, with `SPACE` as a fallback.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-18 - Live Combat Loop

### Context
- Combat felt delayed because it was driven by the main WPF live loop: full `ReadSnapshot()` (UI tree +
  inventory + monster counter) and `Task.Delay(250)`, then `CombatBot.UpdateSnapshot()` on Dispatcher.

### Done
- Added `GameReader.ReadCombatSnapshot(...)`: lightweight memory read for combat only
  (resolve/player/entities/terrain/camera, with optional monster counter; no inventory and no full UI tree).
- Changed `MainWindow` live loop to auto-switch during active combat/ClearMap:
  - normal mode: full snapshot every 250ms for hideout/atlas/inventory workflow;
  - combat mode: lightweight snapshot every ~60ms, full snapshot only about once per second;
  - combat decisions run on the background loop instead of WPF Dispatcher;
  - UI/overlay combat refresh is throttled separately (~160ms), so the UI no longer caps cast timing.
- Cached the map monster counter per area and refresh it during combat, so lightweight ticks can still use
  the last reliable `monsters remain` value instead of falling back to visible monsters only.
- Release build passed with 0 warnings / 0 errors using:
  `dotnet build POE2_AutoMate.sln --configuration Release --no-restore -p:UseSharedCompilation=false`.

## 2026-06-18 - ES Invuln, Exile≠Boss, Portal Wait, New UI Theme

### Done
- **ES "invulnerable" false-skip fixed.** Target progress was measured by LIFE only; an ES target loses
  shield (not life) and was flagged invulnerable/skipped after a few hits. Now `EntityData` carries
  EnergyShield (read in `ReadEntityLife`) and combat progress = EFFECTIVE pool (life+ES) dropping. Fixes
  both ES mobs being skipped and ES bosses triggering the false "invuln phase".
- **Exile ≠ map boss.** `IsMapBoss` (requires "Boss"/"MapBoss" metadata) drives map-completion; killing a
  Rogue Exile (Rarity=Unique but not the boss) no longer ends the map. `IsBossMonster` still drives combat
  priority.
- **Portal wait.** `PortalInitialWaitMs=5000` before the first portal click (portal needs ~3s to become
  interactable) and `PortalClickRetryMs` 2200→7000 (was retrying 8× during the load — the "can't load" cycle).
- **New UI theme** in `App.xaml`: refined palette, rounded buttons/inputs, custom checkboxes, slim scrollbars,
  accent tabs — applies across all screens; control names/handlers untouched.
- Release build 0/0.

### TODO (from tonight's debug — see NEXT_STEPS.md)
- Atlas can't pan → picks only on-screen nodes (main map-selection blocker).
- Entity filters: do NOT aggro Azmiri spirit; DO aggro Delirium monsters; do NOT aggro projectile/wind
  ground effects.
- Pathfinding: rams a wall and won't replan — need stuck-detection → reroute.
- Verify portal wait + full boss-rush loop on the new build.

## 2026-06-18 - Fix False Boss-Kill + Dormant Mobs + Boss-Rush Map Exit

### Done
- **False boss-kill fixed (root of "boss not aggroed").** Both `CombatBot.UpdateBossState` and
  `AutoMapper.UpdateBossState` confirmed a kill on ANY `Health<=0` read; bosses read 0 transiently
  (intro/ES phase/misread), so a live boss was logged "Boss kill confirmed" repeatedly and — with
  "boss killed = map done" — the bot abandoned the fight (user had to kill it manually) and returned to
  hideout early. Now a kill is confirmed ONLY after the boss was first seen ALIVE (`Health>0`, valid Max),
  then reads dead; logged once. Fixes the false aggro-drop and the premature return.
- **Dormant/event-locked mobs ignored.** `IsCombatMonster` now requires a readable ALIVE life
  (`HasLife && IsAlive`) — arena adds that stay frozen until the boss dies expose no HP and were being
  targeted/counted and blocking the loot gate; they're skipped until they actually activate.
- **Boss rush leaves on boss death.** `ClearMap` returns to portal as soon as the boss is killed in boss
  rush, ignoring the remaining (often just-activated) trash.
- Release build 0/0.

### Still open (need next run's log)
- #1 random atlas pick — graph-adjacency is wired; auto-dump now logs `{N} graph nodes` to verify the
  graph + current node resolve.
- #4 shrines/buffs skipped in boss rush (encounters ignored by design) — could add "grab a shrine on the
  way if close".

## 2026-06-18 - Atlas Graph-Adjacent Selection + Boss-Rush-Done + Kite Flask

### Done
- **Graph-adjacent map selection.** Selection used Euclidean grid distance and could jump to a far,
  UNCONNECTED node. The atlas connection graph (`_connections` / `ConnectionsSnapshot`) is now threaded
  through `AtlasSelection.Connections`; `BaseRunnableCandidates` restricts candidates to the DIRECT graph
  neighbours of the current node (you can only traverse to a connected node), with a fall-back to all
  candidates when the graph/neighbours are unavailable.
- **Reliable current node.** Dropped the (wrong) screen-centre proxy — the atlas opens at the last pan
  position, not centred. reference = the in-memory "you are here" marker, else the node the bot last
  traversed into. The marker is re-detected when stale (bounded by `_markerFails`).
- **Boss rush: boss killed = map complete** — `Map Done` fires as soon as the boss dies, ignoring remaining
  trash monsters (no more chasing the monster-count objective).
- **Kite drinks the life flask** — the kite threshold (effective Life+ES+Ward) can trip while raw life% is
  still above the flask threshold, so the kite branch now presses the HP flask directly (cooldown-respecting).
- Release build 0/0.

### Next (user's open combat items)
- Dormant/intangible arena mobs (proximal tangibility) — don't probe them forever; push into the arena.
- Loot not collected (check config + "no monster nearby" gate vs dormant mobs).
- Ground hazard avoidance (burning ground / curses) + don't aggro indestructible projectiles/entities.

## 2026-06-18 - Fix Atlas Hard-Stop + Directed Boss-Seek

### Done
- **Atlas hard-stop fixed.** Boss-rush atlas graph routing failed and stopped the whole bot when the "you are
  here" marker didn't resolve (`atlas.Current = null` → reference (0,0) → empty progress logic →
  "No boss-rush route step near node (0,0)"). Now: reference falls back to the on-screen node nearest the
  SCREEN CENTRE (the atlas is centred on the current node, so a robust proxy), and boss-rush selection falls
  back to `SelectFullClearMap` (nearest clean map) instead of failing. The bot no longer stops on the atlas.
- **Directed boss-seek** (my own, name-independent): record the map ENTRY position on area change; in boss
  rush, `PickGlobalExploreWaypoint` scores frontiers by depth from entry (not distance from player), so the
  bot drives toward the boss end of the map instead of circling spawn. Complements the tile-arena landmark.

## 2026-06-18 - Generic Boss-Arena Detection (boss rush)

### Context
- Boss rush both "couldn't find the arena/boss" AND "wandered / turned into a full clear". Root cause was
  ONE thing: `ScanLandmarks` discarded every terrain tile except those hand-listed in `CustomLandmarks.json`.
  On any map without a curated arena entry there was no boss landmark, so `PickBossLandmarkWaypoint` returned
  null and the bot fell back to explore — i.e. wandered/cleared instead of rushing the boss.

### Done
- Added `GameReader.IsBossArenaTilePath`: PoE2 boss-arena tiles consistently carry "Arena" or "Boss" in their
  `.tdt` terrain path (verified against the reference landmark data: HagWitchArena, BossArena,
  MausoleumBoss_ArenaFloor, Bosstile, Chapel_boss, ArenaTop, …; "AreaTransitions" does NOT match). `ScanLandmarks`
  now keeps curated landmarks AND any generic boss-arena tile, so the arena is a navigable landmark on every
  map. `IsBossLandmark` already recognises Arena/Boss, and boss-rush routing (A* via the reachable-route fix)
  then heads straight to it instead of exploring.
- Verification: log shows `Boss landmark route planned to <arena tile> ...` when the arena is detected.

### Next
- If some map's arena still isn't found, add a one-shot dump of ALL tile path names to extend the heuristic.
- Still open: dedicated BossRush HUD label (vs ClearMap), atlas graph routing for next-node selection.

## 2026-06-18 - Global Input Rate Gate (anti-disconnect)

### Done
- Added a PROCESS-WIDE action-rate gate in `GameInputController` (`GateAction`, static, shared by the
  AutoMapper + CombatBot instances). PoE disconnects clients that send too many action packets/s — a SERVER
  rate limit, not synthetic-input detection — so the fix is staying under a human rate. `MinActionIntervalMs`
  (default 90 ≈ 11 actions/s) + `ActionJitterMs` (default 30) gate every action SendInput (skills, attack
  click, move click, generic clicks, kite hold, WASD). Cursor-only moves and Ctrl+C are not gated.
- Note: WASD hold-move is far gentler than click-to-move (one keydown vs per-tick LMB spam) and should be the
  preferred MovementMode to minimise action volume.

## 2026-06-18 - Target Priority + Reachable-Only Explore (movement)

### Done
- **Target priority** (`CombatBot.SelectTarget`, non-BossRush): survival path-threat first, then the NEAREST
  valuable target across rarities (Rare/Unique/Boss + Encounter) — so a unique closer than a rare is hit
  first — then nearest trash. (Tracks a new `nearestValuable`.)
- **Reachable-only exploration** (fixes "rammed the mountain"): `PickGlobalExploreWaypoint` used to return a
  frontier even when no route could be built, and explore movement then straight-lined into the wall. Now it
  scores all frontier candidates, commits to the highest-scoring one that is ACTUALLY reachable (route built,
  best-first, up to `ExploreReachableAttempts=6` tries), marks unreachable cells failed, and falls back to a
  near routable ring point. `PickNextExploreWaypoint` returns null (STOP) instead of the raw frontier when
  there is no route, so the bot never straight-lines into terrain. (`TryMoveAlongRoute` already refused wall
  paths for combat targets; explore now matches.)
- Release build 0/0.

### Next (from the user's roadmap, not yet done)
- Atlas graph routing (graph is BUILT via `EnsureConnections`/`ConnectionsSnapshot`, not yet wired to
  selection/A*), BossRush-on-map (HUD + arena/boss find), in-map boss detection, load stability (5-6s after
  area change), entity filters tuning (barrels/rocks/spirits/invuln packs), revive, UI cleanup, StashService.

## 2026-06-18 - Portal Wait, Skip Stash Stub, Atlas Auto-Dump

### Context
- After Traverse worked the map activated, but `EnterPortal` failed in ~0.6s ("portal was not found") — the
  portal spawns a few seconds after activation. User also wants the half-baked stash step to stop "bugging
  out" (it opened the stash, did a no-op transfer, left it open) and the atlas to auto-dump on the first lap.

### Done
- Rewrote `EnterPortal` as a dedicated state: waits up to `PortalWaitTimeoutMs` (20s) for the portal entity
  instead of failing fast, clicks it, then waits for the area instance to actually change before handing off
  to `ClearMap` (so ClearMap does not bounce back to hideout prep during the load); retries after
  `MapLoadTimeoutMs` (30s).
- Added `TakeSuppliesFromStash` (default false). When off, AutoStart skips FindStash/TakeSupplies and goes
  straight to `PrepareWaystone` using backpack supplies — the stash is never opened. `PrepareWaystone`
  self-heals by pressing the inventory key once if Ctrl+C reads empty (backpack not visible), then re-hovers.
- Atlas now auto-dumps once per AutoStart run (first lap) via the node provider — no manual button needed.
- Release build passed with 0 warnings / 0 errors.

### Fix follow-up
- Portal still timed out at 20s even though it opened at ~5s: the map-device portal was not categorized as
  "Portal" (only TownPortal/ReturnToLastTownPortal were). Broadened `Categorize` so any metadata containing
  "Portal" is a Portal entity, and added a one-shot EnterPortal diagnostic that logs the 10 nearest
  non-monster entities + metadata after 6s if the portal still isn't found. The 250ms loop already polls
  ~4x/s, so detection — not poll rate — was the issue.

### Next
- `StashService` (the real answer to "which stash tab"): read the open stash tab's items + tab names from
  the UI tree, find the configured supplies, Ctrl+LMB transfer, then flip `TakeSuppliesFromStash` on. Until
  then keep waystones/tablets in the backpack.

## 2026-06-18 - Traverse Button Found From Memory (Map Activation)

### Context
- Live run proved memory map selection works (`Clicked 'Sinkhole' [grid 1,0] ... (clean)`), the device
  opened and a waystone was inserted — but `ActivateMap` failed "Map activation button was not decoded"
  even though TRAVERSE was plainly on screen. Two causes: the per-tick UI snapshot is capped at 2500
  nodes (Traverse is past it with inventory+device+atlas open), and it clicked the element's
  RelativePosition, not its absolute screen position. The failure restarted the whole AutoStart loop,
  which re-processed waystones each cycle — the "inventory scan looks buggy" symptom.

### Done
- Added `Poe2UiGeometry.FindVisibleTextCenter`: BFS the full UI tree (up to 40k nodes) for the first
  visible text element matching given strings and project only the match to a window-local pixel center.
- `GameReader.FindUiButtonCenter(winW, winH, texts...)` + `AutoMapper.UiButtonLocator` delegate (wired in
  MainWindow). `ActivateMap` now locates "Traverse"/"Activate" from memory and clicks its true screen
  center instead of the capped snapshot + relative position.
- Confirmed inventory waystone reading is fine (reads up to 8 items live; skips 0-revive waystones, uses a
  valid one) — the churn was the Traverse-failure loop restart, now fixed.
- Release build passed with 0 warnings / 0 errors.

### Next
- Live-test: expect `Inserted supplies ... clicked Traverse at (x,y)` then EnterPortal -> ClearMap.
- Then panel hygiene (close/verify) if needed, and `StashService` for real stash supply pulls.

## 2026-06-18 - Memory-Driven Atlas Map Selection

### Context
- Old `SelectMap` matched arbitrary visible UI text and picked garbage (e.g. "Ziggurat Refuge", a hideout
  node, and not the nearest map). User asked to select the map from memory instead of text crutches.

### Done
- Ported POE2Radar's atlas node reader into Core as `Poe2AtlasNodes`: detects the node element class +
  canvas (BFS, biome-spread + ~40px size heuristic), reads each node's id/state/biome/flags/content/grid
  coord + resolved map name & code (`MapXxx`) + content tags, and projects each to a window-local screen
  pixel using the atlas-specific projection (`relPos x winH/1600 x medianZoom`, no offset) — NOT the
  generic parent-chain math. Also reads the player's current node via the marker element.
- Verified live (1920x1080): 1244 nodes, 691 visible, 115 on-screen, current=(11,6); names/codes/flags
  all correct (e.g. boss nodes carry tags=[Powerful Map Boss], hideout nodes read MapHideout...).
- `GameReader.ReadAtlasNodes(winW, winH)` + an Atlas-view "Dump atlas nodes" diagnostic button.
- Rewrote `SelectMap` to pick from memory: a runnable `Map…` code (not hideout/tower/citadel/boss/unique/
  merchant), `Unlocked && !Visited && Visible`, on-screen, not the current node, no content tags when
  AvoidDecorated is on, not blacklisted, nearest by grid distance to the current node — then click the
  node's exact screen center. Removed the old text-matching atlas helpers.
- Release build passed with 0 warnings / 0 errors.

### Next
- Live-test the full AutoStart: expect `Clicked '<map>' [grid x,y] ... (clean)` instead of text guesses.
- Still open from the prior run: the bot does not close the stash/inventory before opening the atlas, and
  `ActivateMap` fails "Map activation button was not decoded" (Traverse). Add panel close+verify and fix
  the device-insert/Traverse step.

## 2026-06-17 - Waystone Inspection Works; Prep Reordered Before Map Device

### Context
- Memory grid calibration verified live at 1920x1080: detector picked the exact backpack rect
  (ratio 2.40, square 53x53 cells). The "Test waystone hover" diagnostic confirmed Ctrl+C now reads
  the waystone (`Clipboard OK (waystone): "Item Class: Waystones" (716 chars)`).
- Root cause of the AutoStart failure was sequencing, not calibration: `PrepareWaystone` ran AFTER
  `OpenAtlas`/`SelectMap`, when the backpack is hidden, so the hover landed on the atlas UI.

### Done
- Added a "Test waystone hover" diagnostic button (`AutoMapper.TestWaystoneHover`) that moves the cursor
  to the first backpack waystone and Ctrl+C's it, reporting the clipboard result. Isolates calibration +
  copy from the state machine.
- Fixed inventory slot geometry: `SlotEnd` is EXCLUSIVE (start + size). Treat it as inclusive (`-1`),
  so 1x1 items stop over-counting a cell and the hover lands dead-center instead of on the cell edge.
  Also corrects FreeCellCount and multi-cell item width.
- Reordered the AutoStart state machine so waystone prep happens while the backpack is visible:
  `TakeSupplies -> PrepareWaystone -> FindMapDevice -> OpenAtlas -> SelectMap -> ActivateMap`.
  (Was `... -> OpenAtlas -> SelectMap -> PrepareWaystone -> ActivateMap`.)
- Release build passed with 0 warnings / 0 errors.

### Next
- Live-test full AutoStart with a waystone in the BACKPACK (stash take is still a stub): expect
  PrepareWaystone to read tier/revives and apply Alchemy/Exalt, then device insert + Traverse.
- Then `StashService` so supplies are actually pulled from the stash.

## 2026-06-17 - Memory-Driven Inventory Grid Calibration

### Context
- Inventory memory read now works (`inventory=4/55free` in live logs); priority #1 from the plan is closed.
- Two remaining blockers share one root cause: mapping a grid cell (col,row) to a screen pixel.
  - Waystone hover+Ctrl+C missed the item (clipboard empty) because the fraction-based cell->pixel
    calibration (`Left=0.03, Width=0.945`) did not match the real 1920x1080 inventory layout.
  - Stash supply transfer is still a no-op (no stash decoder exists in the POE2Radar reference either).

### Done
- Ported POE2Radar's UiElement screen projection (`Poe2Runeforge` geometry) into the Core as
  `Poe2UiGeometry`: parent-chain unscaled position x resolution scale, returning absolute window-local
  pixel rects for any UiElement.
- Added the missing UiElement offsets: `PositionModifier 0xF0`, `LocalScaleMul 0x130`,
  `ScaleIndex 0x18A`, `FlagModifyPosBit 0x0A`, `BaseResW/H 2560/1600`.
- Added `GameReader.DetectInventoryGrid(winW, winH, cols, rows)` -> best grid rectangle + ranked
  container candidates (heuristic: visible, lower-right, aspect ratio ~ cols/rows).
- Added an Inventory-view button "Calibrate grid from memory": detects the backpack grid from memory,
  converts it to the legacy 0..1 fractions, updates + saves the calibration, and logs candidates.
- Made the "no waystone" failure honest: it now dumps the actual inventory item metadata so the log
  shows exactly what is (or is not) in the backpack.
- Release build passed with 0 warnings / 0 errors.

### Next
- Verify the memory grid detection against a live 1920x1080 inventory; tune the candidate heuristic from
  the logged candidates if the auto-pick is wrong.
- Then build `StashService` (no reference exists): use the same UI projection to locate stash-tab cells
  and the hovered/searched item, then Ctrl+LMB transfer. Stash items are NOT in the player
  PlayerInventories vector, so this is UI-driven, not a memory inventory clone.

## 2026-06-17 - Real AutoStart And Inventory Chain Fix

### Done
- Removed manual dry-run from the normal AutoStart UI flow.
- AutoStart now runs as a real loop by default:
  - already in map -> skip hideout stages and go to clear logic,
  - in hideout -> run hideout preparation stages.
- Forced `AutoMapperDryRun=false` from app settings/config mapping so stale old settings cannot re-enable it.
- Fixed inventory memory chain using the POE2Radar reference:
  - `AreaInstance +0x580 -> ServerData`,
  - `ServerData +0x48 -> PlayerServerData vector`,
  - first player entry -> `ServerDataStructure`,
  - `ServerDataStructure +0x320 -> PlayerInventories`.
- Added fallback scanning for `PlayerServerData` and `PlayerInventories` vectors using inventory entry fingerprints.
- Added `LastInventoryError` logging to snapshot logs, so future inventory failures say exactly which hop failed.
- Deduplicated inventory items by item entity pointer to avoid multi-cell items appearing multiple times.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-17 - TakeSupplies Inventory Retry

### Done
- `TakeSupplies` no longer fails immediately when `snapshot.Inventory` is temporarily unavailable.
- Dry-run supply planning no longer requires a live inventory snapshot.
- Added inventory scan wait/retry behavior:
  - waits up to 8.5s for the memory inventory snapshot,
  - if active input is enabled and inventory/stash UI is not visible, presses the configured inventory key,
  - if stash/inventory UI is already visible, waits without toggling it closed.
- Failure message now explains that inventory scan timed out and points to Debug inventory diagnostics.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-17 - F2 AutoStart Loop And Hideout Detection

### Done
- Changed the global bot hotkey to start/stop the AutoStart cleanup loop instead of starting CombatBot directly.
- F2 now enables AutoMapper with active input and disables dry-run for a real cleanup loop start.
- CombatBot is now treated as an AutoStart executor:
  - stays off in hideout/stash/atlas preparation,
  - starts only when AutoMapper reaches `ClearMap`,
  - pauses again when AutoMapper leaves `ClearMap`.
- Hideout detection now has priority over `MapMonsterCount`.
- `Stash`, `MapDevice`, or `Waypoint` anchors force AutoMapper to treat the area as hideout, even if a stale/non-map monster counter is non-zero.
- If AutoMapper is already stuck in `ClearMap` while hideout anchors are visible, it switches back to preparation instead of continuing clear logic.
- HUD mob count now shows `0` in hideout-like snapshots.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-17 - ComboBox Contrast Fix

### Done
- Added a global dark `ComboBox` template so closed fields and opened dropdown lists use the same readable theme.
- Added dark `ComboBoxItem` styling with readable hover/highlight/selected states.
- Limited dropdown height and kept vertical scrolling for long tablet lists.
- Release build passed with 0 warnings / 0 errors.

## 2026-06-17 - Clipboard Waystone Inspection

### Done
- Added game-window hover text capture:
  - focus game window,
  - clear clipboard to avoid stale item text,
  - send `Ctrl+C`,
  - read fresh Unicode clipboard text.
- Waystone preparation now uses copied item text as the primary source for:
  - `Item Class: Waystones`,
  - `Rarity`,
  - `Waystone (Tier N)`,
  - `Revives Available`.
- Waystone tier parsing now avoids modifier tiers such as `{ Prefix Modifier ... (Tier: 1) }`.
- If copied hover text is not a waystone and UI fallback has no usable signal, AutoMapper stops with a calibration error instead of applying currency blindly.
- UI tooltip/memory text remains as fallback when clipboard capture is blocked.

### TODO
- Add a small Debug panel field with the last copied waystone summary: source, rarity, tier, revives, copy error.
- Consider preserving/restoring the user's previous clipboard after inspection if clipboard churn becomes annoying during manual play.
- Add a manual `Inspect hovered item` debug button for testing without starting AutoStart.

## 2026-06-17 - AutoStart Supplies, Waystone Rules, Loot Stats

### Done
- Added AutoStart supply settings for waystones, currency tab, alchemy use, and selected tablet slots.
- Added tablet slot selection for up to 3 tablets:
  - Abyss Tablet
  - Breach Tablet
  - Delirium Tablet
  - Expedition Tablet
  - Irradiated Tablet
  - Temple Tablet
  - Overseer Tablet
  - Ritual Tablet
  - Clear Skies Delirium Tablet
  - Cruel Hegemony Overseer Tablet
  - Forgotten By Time Expedition Tablet
  - Freedom of Faith Ritual Tablet
  - Mastered Domain Irradiated Tablet
  - Season of the Hunt Overseer Tablet
  - The Grand Project Irradiated Tablet
  - Unforeseen Consequences Abyss Tablet
  - Visions of Paradise Irradiated Tablet
  - Wraeclast Besieged Breach Tablet
- Added waystone preparation state before map activation:
  - Hover waystone.
  - Read tooltip text for `Revives Available`.
  - Require exactly 2 revives.
  - Skip waystones below minimum revives.
  - Skip waystones above configured max tier.
  - Use Orb of Alchemy on normal waystones when enabled.
  - If alchemy cannot be used and revives are above 2, apply ordinary `Exalted Orb` only, never Greater/Perfect variants, then re-check revives.
- Added max waystone tier setting, default T16.
- Added `Allow unknown tier waystones` setting, default off.
- Added inventory safe grid mass actions:
  - Select all.
  - Deselect all.
- Added `-` key capture and input support for portal/hotkey binds.
- Added atlas setting to avoid decorated atlas nodes with extra icons or implicit mechanics.
- Added loot statistics:
  - Compact HUD line.
  - Full Loot panel summary.
  - Counts by bucket such as Common, Magic, Rare, Unique, Divine Orb, Exalted Orb, Orb of Alchemy.
  - Local poe.ninja price cache for divine-equivalent value.
- Release build passed with 0 warnings / 0 errors.

### Notes
- Current stash supply taking is still guarded by dry-run/calibration logic. Main inventory is memory-decoded; stash inventory needs a dedicated decoder before fully reliable automatic item extraction from stash tabs.
- Atlas node selection now has a heuristic decorated-node skip. The reference project has a deeper `Poe2Atlas.ReadNodes` reader with map names/tags; porting that is the next stronger version.

### TODO
- Port or reimplement the full atlas node reader from POE2Radar reference:
  - map name,
  - node state/color,
  - content tags like Powerful Map Boss/Breach/Delirium,
  - clean nearest-map selection without UI heuristics.
- Build a proper stash item memory decoder:
  - active stash tab,
  - item names/stacks,
  - item grid rects,
  - tab switching by configured names.
- Finish true supply extraction:
  - take selected tablets if available,
  - take ordinary Exalted Orbs,
  - take Orb of Alchemy,
  - skip missing optional tablets.
- Add a waystone tooltip diagnostic panel showing tier/revives/rarity before activation.
- Improve loot valuation buckets with exact item display names, not only metadata heuristics.
- Add manual refresh button for poe.ninja prices and show cache age/error in the big UI.
- Persist per-run loot history separately from current session counters.
- Add dry-run action log export for AutoStart.
