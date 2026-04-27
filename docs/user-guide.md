# Parsek User Guide

Parsek lets you record missions, revert to launch, and merge them into the timeline so they play out automatically while you fly new missions.

## Controls

| Key | Action |
|-----|--------|
| **[** or **]** | Exit watch mode (return to active vessel) |
| **V** | Toggle watch camera between Free Orbit and Horizon-Locked (watch mode only) |
| **Ctrl+Shift+T** | Open the in-game Test Runner (any scene) |

The Parsek window is available from the toolbar button in Flight, Map, KSC, and Tracking Station views. Recording is triggered automatically on launch/EVA; there is no start/stop hotkey. Manual ghost-only recordings are made from the Gloops Flight Recorder window (see below).

## How It Works

### Recording a Mission

1. Launch any vessel (career mode recommended for resource tracking)
2. Recording starts automatically when the vessel leaves the pad/runway
3. Fly your mission normally
4. Recording stops when the active vessel changes (docking, switching with `[`/`]`, scene exit) or on revert
5. Revert to Launch (Esc > Revert to Launch)

Going EVA from a vessel on the pad auto-starts recording on the EVA kerbal. Going EVA mid-flight stops the parent recording and starts a linked child recording on the EVA kerbal. Both behaviors can be disabled in Settings.

### EVA During Recording

If a kerbal goes EVA while recording a vessel, Parsek automatically:

1. Stops the parent vessel recording and commits it to the timeline
2. Starts a new linked child recording for the EVA kerbal
3. On revert, both ghosts play back - the vessel ghost and the EVA kerbal ghost
4. When the parent vessel spawns, the EVA'd kerbal is excluded from its crew

### Gloops Flight Recorder

The Gloops window (opened from the "Gloops Flight Recorder" button in the main Parsek window, flight only) records a manual ghost-only recording alongside the normal auto-recorder. Typical uses are airshow replays, scenery decor, and captured maneuvers that don't need to spawn a real vessel at the end.

- **Start Recording** - begins capture on the active vessel.
- **Stop Recording** - commits the recording immediately with looping off by default. The recording is placed in the **Gloops - Ghosts Only** group in the Recordings Manager, is flagged ghost-only (no rewind save, no vessel spawn at loop end), and keeps its loop period on the global **auto** setting if you later enable looping.
- **Preview** / **Stop Preview** - plays the last saved Gloops recording back as a ghost from current UT without affecting the timeline.
- **Discard** / **Discard Recording** - drops the in-progress or last saved recording.
- **Start New Recording** - begins a fresh capture after a previous one is saved.

Gloops recording auto-stops if the active vessel changes. Ghost-only recordings get an **X** button in the Recordings Manager's Group column for quick deletion.

Gloops recordings are purely visual. They never charge funds for the vessel you captured, never reserve the kerbals aboard for the loop duration, never complete contracts, and never credit science or milestones — those stay with your normal mission recording (if any) or your between-mission career state.

### Merge Dialog

After reverting (or aborting a mission to the Space Center with a recording pending), a dialog appears with context-aware options:

| Situation | Options |
|-----------|---------|
| Vessel destroyed or no snapshot | Merge to Timeline, Discard |
| Vessel intact with snapshot | Merge to Timeline, Discard |

- **Merge to Timeline** - Recording is merged; if the vessel is intact, it will appear in the game world when the ghost finishes playing
- **Discard** - Recording is thrown away. Every career effect captured during the discarded flight — contracts accepted or completed, tech researched, crew changes, milestones achieved, funds/science/reputation deltas — is rolled back as if the flight never happened. (Career actions unrelated to a recording, e.g. unlocking a tech node at KSC, stay.)

If **Auto-merge recordings** is enabled in Settings, the merge happens silently without a dialog.

### Rewind to Separation (v0.9+)

When a vessel stages, undocks, or EVAs into two or more controllable pieces, Parsek automatically captures a **Rewind Point** (a quicksave plus a per-slot vessel map) so you can replay the split. This is the booster-recovery feature in spirit: launch an AB stack, stage, take B to orbit and commit — and later come back to fly A down as a self-landing booster.

- **Unfinished Flights group** — appears in the Recordings Manager when a sibling from a past split ends badly (crash, destroyed, BG-crash). The group is read-only: you cannot hide it and you cannot drag its members into manual groups.
- **Rewind button** — click the row to re-fly the unfinished sibling from the moment of the split. Parsek loads the Rewind Point quicksave, strips the other split siblings to ghosts, and hands you the active vessel. The five preconditions (Corrupted flag, quicksave file present, no active session already, scene is not transitioning, parts still load) are checked before the button enables.
- **Merging the re-fly** — when your re-fly ends, the normal Merge dialog appears. Merging writes supersede relations for the retired siblings; if the re-fly landed/recovered/orbited it seals as `Immutable`, if it crashed it commits as `CommittedProvisional` and remains re-rewindable from the same slot.
- **What survives supersede** — contracts, milestones, facility upgrades, strategies, tech unlocks, science, funds, and vessel-destruction rep penalties from the retired sibling stay in your career totals. Only kerbal deaths in the retired sibling are reversed (plus rep penalties bundled with those deaths).
- **Revert during re-fly** — pressing stock Revert-to-Launch or Revert-to-VAB/SPH while a session is active shows the same three-option dialog: Retry from Rewind Point (re-loads the split moment in FLIGHT either way), Discard Re-fly (throws away the current attempt and returns you to the scene you clicked at the split UT; the tree's other re-fly state is preserved and the Unfinished Flights entry remains), or Continue Flying.
- **Disk usage** — Settings > Diagnostics shows live Rewind Point disk usage (total size + file count). Rewind Points self-reap when the split has been fully resolved.

See `docs/parsek-rewind-to-separation-design.md` for the full feature design.

### Timeline Playback

After merging, wait on the pad (or time warp) until UT reaches the recording's timestamps:

- A ghost vessel appears (opaque replica with original part meshes and textures) and replays the recorded flight
- Engine flames and smoke appear on the ghost during burn phases (both modern EFFECTS engines and legacy stock parts like the Flea SRB)
- Parts that were decoupled or destroyed during the recording disappear from the ghost at the correct time
- Parachute canopies deploy on the ghost at the correct time (real canopy mesh, not a placeholder)
- Engine shrouds are jettisoned on the ghost when staging occurs
- Solar panels, antennas, and radiators extend/retract on the ghost
- Landing gear deploys and retracts on the ghost
- Cargo bay and service bay doors open and close on the ghost
- Lights turn on/off and blink on the ghost
- Procedural fairings are displayed on the ghost and disappear when jettisoned
- RCS thrusters emit particle FX when firing on the ghost
- Funds, science, and reputation changes from the recording are applied at the correct time
- When the ghost finishes, the vessel appears at its final position (if "Keep Vessel" was chosen)

### Crew Management

When you choose "Merge to Timeline", the recorded crew (e.g. Jeb) are reserved for the deferred vessel spawn. A replacement kerbal with the same trait is hired automatically so your available crew pool stays the same size. When the vessel spawns at EndUT, the original crew board it and the replacement is removed.

### Timeline Window

Click the "Timeline" button in the main Parsek window to open a read-only chronological view of every committed recording, player action, and game event, anchored by a `— UT (now) —` divider between past and future.

Top of the window:

- **Resources** block - same as the Resource Budget section below, shown when any resource has a committed cost.
- **Tier toggles** - **Overview** shows the headline entries; **Details** adds lower-significance events (resource changes, individual career-event rows, etc.).
- **Source toggles** - **Recordings**, **Actions**, **Events** each toggle that kind of row in and out.
- **Time-Range Filter** - preset buttons (Last Day / Last 7d / Last 30d / This Year / All) and a **Custom range** disclosure with From/To sliders. The filter is shared with the Recordings Manager.

Each entry row shows UT, a description, and (for `RecordingStart` entries) the following buttons:

- **W** - watch button in flight. Enabled only when the recording currently has an active same-body ghost within the watch cutoff; otherwise shown grayed out. A watched row shows **W\***.
- **R** / **FF** - same rewind / fast-forward buttons as the Recordings Manager.
- **L** - loop toggle. Only shown for past or active recordings that Parsek considers logically loopable (launches, atmospheric descents, surface departures, docking segments), or for any recording that is already looping. Active loops display in green text.
- **GoTo** - jumps to the same recording in the Recordings Manager (opening it and un-hiding the recording if necessary).

The footer shows `N Recordings, M Actions, K Events` for whatever is currently visible after filtering.

### Recordings Manager

Click the "Recordings" button in the main Parsek window to open the Recordings Manager. This secondary window shows all committed recordings in a sortable table. Recordings that belong to the same mission tree or user group collapse into expandable parent rows.

Columns:

- **Playback enable** - per-row checkbox; when unchecked, the ghost is hidden during playback. Visual-only: the recording's career effects (resources, contracts, crew, and the final vessel spawn) still apply regardless. To fully exclude a recording from the career, Delete (post-commit) or Discard (pre-commit) it. The header checkbox toggles all rows at once.
- **#** - row index.
- **Name** - vessel name; double-click to rename.
- **Phase** - colored label (`atmo`, `exo`, `space`, `approach`, `surface`).
- **Site** - launch site name.
- **Launch** - KSP calendar format.
- **Duration** - compact format (e.g. "56s", "2m 30s", "1h 15m").
- **Info columns** (shown when the **Info** toggle at the bottom of the window is expanded) - Max altitude, max speed, distance travelled, point count, start/end positions.
- **Status** - `future` / `active` / countdown `T-Xd Xh Xm Xs` for unspawned recordings, `past` or a terminal state (`Orbiting`, `Landed`, `Splashed`, `Docked`, `Recovered`, `Destroyed`) for finished ones. Color-coded. Hovering shows chain status when flying alongside an active ghost.
- **Group** - "G" button opens a group picker; custom (user-created) groups add an "X" button to disband the group; ghost-only recordings add an "X" button to delete the recording.
- **Loop Ghost** - per-row loop toggle. Header checkbox toggles all rows. See Loop Playback below.
- **Period** - launch-to-launch loop period with a unit button that cycles `sec -> min -> hr -> auto`. "auto" inherits the default from Settings -> Looping.
- **Watch** (flight only) - "W" / "W*" button. See Watch Mode below.
- **Rewind / F.Forward** - "R" (rewind) for past/active recordings with a rewind save; "FF" (fast-forward) for future recordings. The button is disabled if the operation is currently not safe; hover for the reason.
- **Archive** - per-row archive toggle. The header checkbox controls whether archived recordings are filtered out of the table (checked, default) or shown (unchecked). Archived recordings still play as ghosts, still apply their career effects, still spawn vessels — the toggle only hides the row from this table.

Click any sortable column header (#, Name, Phase, Site, Launch, Duration, Status) to sort by that column. Click again to reverse. The window is draggable and resizable.

Bottom bar: **Info** toggles the expanded-stats columns; **New Group** creates a user-defined group you can drag recordings into via the "G" picker.

### Time-Range Filter

A shared filter at the top of the Timeline window narrows both the Timeline and the Recordings table to a slice of the career:

- Quick presets: **Last Day**, **Last 7d**, **Last 30d**, **This Year**, **All** (clears the filter).
- **Custom range**: a collapsible disclosure with **From** / **To** sliders over the full data range.

When a filter is active, the Recordings table shows a `Filtered: ...` line with a **Clear** button so the filter stays visible even with the Timeline closed.

### Loop Playback

A recording with **Loop** checked replays on a fixed launch-to-launch period: the ghost relaunches every N seconds/minutes/hours regardless of how long the recording itself is. When the period is shorter than the recording duration, successive cycles overlap and multiple ghosts coexist. Edit the period inline in the Period column, or leave it on `auto` to inherit the default from Settings.

### Watch Mode

Click **W** on a recording in either the Timeline or the Recordings Manager (or on a group header in the Recordings Manager) to enter watch mode — the KSP camera follows the ghost vessel instead of the active vessel. A watched row shows **W\*** and any group containing the watched recording also shows **W\***. Press `[`, `]`, or click W again to exit. Press **V** while watching to toggle the camera between Free Orbit (stock behavior) and Horizon-Locked (ground stays at the bottom of the screen; picked automatically near planetary surfaces, free in orbit).

Clicking **W** on a group header cycles through the group's watchable vessels: each press advances to the next member with an active same-body in-range ghost.

Watch mode auto-exits when the ghost passes the fixed 300 km watch range, when the watched ghost despawns at the end of its playback, or when it leaves the current SOI.

### Rewind / Fast-Forward

- **R** reloads the quicksave captured at the recording's launch, undoing everything that happened since. All committed recordings (including the one you rewound to) replay as ghosts from that point; you can launch new missions alongside them. Resources, research progress, and facility upgrades are reset to their pre-launch state and will be re-applied as the ghost timeline replays.
- **FF** instantly advances the game clock to a future recording's launch UT. The current scene stays (KSC stays KSC, flight stays on the current vessel). If a ghost is being watched, watch transfers to the fast-forwarded recording.

Both buttons show a confirmation dialog naming the recording before acting. Disabled buttons hover-tip their blocker ("recording in progress", "no rewind save", etc.).

### Real Spawn Control

Click the "Real Spawn Control (N)" button in the main Parsek window to open the spawn control window. This button shows the number of nearby spawn candidates and is grayed out when none are detected.

The window shows ghost craft within 500m whose recording ends in the future — these are vessels that will become real when their ghost playback finishes. Each row shows:

- **Craft** — vessel name
- **Dist** — distance in meters from your active vessel
- **Spawns at** — the KSP calendar time when the ghost becomes real
- **In T-** — countdown to spawn time
- **Warp** — button to time-warp directly to that vessel's spawn time

Click column headers (Dist, In T-) to sort. Default sort is by distance. Click again to reverse order.

The **Warp to Next Spawn** button at the bottom warps to whichever candidate spawns soonest. A screen notification appears when a new ghost craft enters the 500m range.

### Kerbals Window

Click the "Kerbals" button in the main Parsek window to open the Kerbals window. The main button stays count-free; detailed roster state and mission-outcome totals live inside the window itself. The window has two tabs:

- **Roster State** — per-owner collapsible tree of slots and their replacement chains.
  - Each top-level row is an original kerbal (Jeb, Bill, Val, ...); clicking the arrow expands the replacement chain underneath, labelled `(active)`, `(retired)`, or `(displaced)`. Reserved slots (waiting for a committed recording to spawn the real crew back in) show their reservation status inline.
  - An **Unlinked Retired** sub-section lists retired stand-ins that aren't attached to any current kerbal's chain. Usually empty; populated after certain rollback edge cases.
- **Mission Outcomes** — chronological record of every kerbal's mission outcomes. Click a kerbal's name header to fold their rows under a compact `N missions - X Dead, Y Recovered, Z Aboard` summary; each detail row shows recording name + end state (`Dead`, `Recovered`, `Aboard`, `Unknown`) color-coded and UT timestamp. **Click any detail row to scroll the Timeline window to the matching recording.**

The window is draggable and resizable. Fold/expand state is transient (resets when the window is closed or the scene changes).

### Career State Window

Click the "Career" button in the main Parsek window to open the Career State window. The window surfaces four career-scoped modules that otherwise have no UI, across four tabs:

- **Contracts** — active contracts with accept UT and deadline, plus Mission Control slot usage (`1/2 now, 2/2 at timeline end`). When the timeline holds a committed recording that hasn't been played yet, its future `ContractAccept` actions appear under a collapsible **Pending in timeline** sub-section separate from **Active now**.
- **Strategies** — active Administration strategies with source/target resource, commitment percentage, activation UT, and Administration slot usage. Same "current vs. at-timeline-end" split as Contracts when future activations are committed.
- **Facilities** — level (1-3) and destroyed/repair state for all nine KSC buildings (VAB, SPH, LaunchPad, Runway, Administration, Mission Control, Tracking Station, R&D, Astronaut Complex). Upcoming level changes show as `L2 -> L3 (upcoming)`; destroyed buildings with a pending repair show `(destroyed, repair pending)`.
- **Milestones** — full chronological list of credited milestones with UT and any funds/rep/science reward. Pending milestones (from committed-but-unplayed recordings) are interleaved and flagged.

The mode banner at the top shows `Career mode - UT {liveUT}` and, when the timeline extends past the live moment, appends `(timeline ends at UT {terminalUT})` so you can see at a glance whether the career has committed-but-unplayed actions reaching into the future. The window is hidden-but-clickable in Science and Sandbox modes: Contracts and Strategies tabs show "unavailable in Science mode" / "not tracked in Sandbox mode" messages, Facilities and Milestones still render in Science.

The window is draggable, resizable, and the tab bar uses the same styling as the rest of Parsek. Tab selection resets on close.

### Settings

Click the "Settings" button in the main Parsek window to open the Settings panel. Settings are saved per-save and can also be accessed from KSP's Difficulty Settings screen (Esc > Settings > Parsek).

Recording:

| Setting | Default | Description |
|---------|---------|-------------|
| Auto-record on launch | On | Start recording when a vessel leaves the pad or runway |
| Auto-record on EVA | On | Start recording when a kerbal goes EVA from the pad |
| Auto-merge recordings | Off | When on, recordings commit to the timeline silently on revert; when off, the merge dialog appears |

Looping:

| Setting | Default | Description |
|---------|---------|-------------|
| Auto-launch every | 10s | Default launch-to-launch period used by recordings whose Period column is set to `auto`. Cycle the unit button to switch between sec / min / hr |

Ghosts:

| Setting | Default | Description |
|---------|---------|-------------|
| Ghost audio | 70% | Volume multiplier for ghost engines, RCS, decouplers, and explosions. Set to 0% to mute |
| Show ghosts in Tracking Station | On | When off, Parsek ghosts and atmospheric ghost markers are hidden from the tracking station vessel list and map |

Diagnostics:

| Setting | Default | Description |
|---------|---------|-------------|
| Verbose logging | On | Write detailed diagnostics to `KSP.log` |
| Write readable sidecar mirrors | On | Also write human-readable `.txt` mirrors alongside binary recording sidecars |
| In-Game Test Runner | - | Opens the runtime-test window (same as Ctrl+Shift+T) |
| Run Diagnostics Report | - | Dumps a full diagnostics snapshot to `KSP.log` |

Recorder Sample Density: three preset buttons plus a live summary line showing the resulting sampling thresholds.

| Preset | Description |
|--------|-------------|
| Low | Fewer samples, smaller files; trajectories may look angular during sharp maneuvers |
| Medium (default) | Balanced sampling for most flights |
| High | Dense sampling for cinematic recordings; larger files |

Data Management:

- **Wipe All Recordings (N)** - clears all committed recordings. Also frees reserved crew and removes replacement kerbals. Milestones are preserved.
- **Wipe All Game Actions (N)** - clears all recorded milestones and career actions.

The "Defaults" button resets all settings to their original values.

### Resource Budget

When recordings or milestones have unreplayed resource costs, the Parsek UI shows a Resources section:

- **Funds: X available (Y committed)** - current funds minus reserved amounts
- **Science: X available (Y committed)** - current science minus reserved amounts
- **Reputation: X available (Y committed)** - current reputation minus reserved amounts

If any resource goes negative (over-committed), the value turns red and a yellow "Over-committed! Some timeline actions may fail." warning appears. This means you've committed more resources to future timeline events than you currently have available.

The resource budget is computed on-the-fly from two sources:
1. **Recording costs** - net flight impact (launch cost minus in-flight earnings), proportional to replay progress
2. **Milestone costs** - game state event costs (tech research, part purchases) not yet replayed

### Action Blocking

If you try to re-research a technology or re-upgrade a facility that is already committed on your timeline (but not yet replayed), Parsek blocks the action and shows a popup dialog explaining why. This prevents paradoxes - you can't spend resources that are already committed to future timeline events.

### Milestones

Parsek captures career actions (tech research, part purchases, facility upgrades, contracts, crew changes) into milestones. These are independent of flight recordings - even if you never record a flight, your R&D and facility work is captured.

Milestones are created:
- When you commit a recording (bundles events since the last milestone)
- On game save (captures any events not yet bundled)

Hiding a recording does not affect its milestone or ghost playback - hidden recordings still play as ghosts normally.

## Automatic Behaviors

Parsek handles several edge cases automatically. These are logged to `KSP.log` (search for `[Parsek]` or `[Parsek Scenario]`).

### Recording

- **Auto-start on launch** - Recording begins automatically when a vessel leaves the pad or runway (transitions out of PRELAUNCH). A screen message confirms "Recording STARTED (auto)". Can be disabled in Settings.
- **Auto-start on EVA from pad** - Going EVA from a vessel sitting on the pad/runway also auto-starts recording on the EVA kerbal. Can be disabled in Settings.
- **Mid-recording EVA** - Going EVA during an active recording auto-stops the parent recording, commits it, and starts a linked child recording on the EVA kerbal.
- **Part events** - 28 event types are recorded with timestamps, including staging, decoupling, engine ignition/shutdown/throttle, parachute deploy/cut, solar panel/antenna/radiator extend/retract, light on/off/blink, landing gear deploy/retract, cargo bay open/close, fairing jettison, RCS fire, docking/undocking, and inventory part placement/removal. During ghost playback, decoupled parts (and their subtrees) disappear from the ghost at the correct time. Engines and RCS thrusters emit particle FX during burn phases. Parachute canopies deploy with the real mesh, engine shrouds are jettisoned, and deployable parts animate between stowed/deployed states. Docking/undocking events are used as chain segment boundaries, not direct ghost mesh transforms.
- **Paused game** - Recording cannot start while the game is paused.
- **Vessel change** - If the active vessel changes during recording (docking, switching with `[`/`]`), the recording stops automatically with a screen message.
- **Very short recordings** - Recordings with fewer than 2 sample points are silently dropped on revert (nothing to play back).

### Ghost Playback

- **Time warp protection** - Time warp is stopped once when UT is about to enter a recording's time range, but only if the recording has an unspawned vessel. Time warp during active ghost playback is allowed.
- **Rewind / Fast-Forward jumps** - Both R and FF stop any active time warp before jumping so the clock lands cleanly on the target UT.
- **Orbital attitude** - Ghost vessels in orbital segments preserve their recorded orientation. A vessel holding retrograde, normal, or any other SAS mode will hold that attitude throughout the orbit, not snap to prograde. If the PersistentRotation mod is installed, spinning vessels are also reproduced — the ghost spins at the same rate the player saw during time warp.
- **SOI changes** - Recordings that cross SOI boundaries (e.g. Kerbin to Mun) play back correctly. Each trajectory point references its own celestial body.
- **Ghost distance tiers** - As a ghost moves away from the camera it drops to a reduced visual tier around 2.3 km, and its mesh unloads entirely beyond 50 km (still logically playing, just not drawn). Watching a ghost overrides the visual cutoff until the fixed 300 km watch range is reached.
- **Map and Tracking Station icons** - Ghost vessels use the stock icon matching their vessel type (Ship, Plane, Probe, Station, etc.). Icon labels are hidden by default, appear on hover, and pin on click. The **Show ghosts in Tracking Station** setting hides ghost icons entirely from the tracking station when off.

### Vessel Spawning

- **Proximity offset** - If a vessel would spawn within 200m of any other vessel, it is automatically moved to 250m away to prevent physics collisions. This can happen when multiple recordings end near the same location or near the launchpad.
- **Duplicate prevention** - Each spawned vessel is tracked by its persistent ID. If the vessel already exists (e.g. after a scene change), it won't be spawned again.
- **Dead crew removal** - If a crew member died during the recording but the vessel survived, they are removed from the snapshot before spawning.

### Resources

- **No negative balance** - Funds, science, and reputation deltas are clamped so they never go below zero.
- **Quicksave safety** - Resource application progress is saved, so quickloading doesn't double-apply deltas.
- **Resource deduction on revert** - When you revert, committed resource costs are deducted from game state so KSP's funds/science/reputation displays and purchase checks reflect what's actually available.
- **Action blocking** - Researching tech or upgrading facilities that are already committed on the timeline is blocked with an explanatory dialog.

### Game State Recording

- **Career events captured** - Tech research, part purchases, facility upgrades/downgrades, building destruction/repair, contract lifecycle, crew changes, and resource changes are recorded automatically in career mode.
- **Milestone creation** - Events are bundled into milestones at recording commit time and on game save.
- **Epoch isolation** - After a revert, events from the abandoned timeline branch are excluded from new milestones. This prevents old-branch actions from contaminating the current branch.

### Scene Transitions

- **Abort Mission** - If you leave Flight without reverting (e.g. Esc > Abort Mission to Space Center), any pending recording is automatically committed to the timeline. The vessel snapshot is discarded since the merge dialog isn't available outside Flight.
- **Revert to Launch / VAB / SPH** - KSP's stock flight-results dialog (with its Revert buttons) shows first on a crash; Parsek no longer pre-empts it. Picking Revert unstashes the in-progress recording without deleting it — sidecar files and career events captured during the reverted flight stay on disk, so a quicksave you took during the flight can still be F9'd back into. The reverted events are hidden from the current career's ledger by an epoch bump. If you want the merge dialog instead, pick **Space Center** (or any non-revert exit).
- **Missed EndUT** - If a recording's EndUT passes while you're in the Space Center or Tracking Station, reserved crew are automatically freed so they don't stay stuck as Assigned forever.
