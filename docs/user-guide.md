# Parsek User Guide

Parsek lets you record missions, revert to launch, and merge them into the timeline so they play out automatically while you fly new missions.

## Controls

| Key | Action |
|-----|--------|
| **F9** | Start / Stop recording |
| **F10** | Preview playback (current recording) |
| **F11** | Stop preview |

The Parsek window is available from the toolbar button in Flight/Map view.

## How It Works

### Recording a Mission

1. Launch any vessel (career mode recommended for resource tracking)
2. Recording starts automatically when the vessel leaves the pad/runway
3. Fly your mission normally
4. Press **F9** to stop recording
5. Revert to Launch (Esc > Revert to Launch)

You can also press **F9** manually at any time to start or stop recording. Going EVA from a vessel on the pad will also auto-start recording on the EVA kerbal.

### EVA During Recording

If a kerbal goes EVA while recording a vessel, Parsek automatically:

1. Stops the parent vessel recording and commits it to the timeline
2. Starts a new linked child recording for the EVA kerbal
3. On revert, both ghosts play back - the vessel ghost and the EVA kerbal ghost
4. When the parent vessel spawns, the EVA'd kerbal is excluded from its crew

### Merge Dialog

After reverting, a dialog appears with context-aware options:

| Situation | Options |
|-----------|---------|
| Vessel destroyed or no snapshot | Merge to Timeline, Discard |
| Vessel intact with snapshot | Merge to Timeline, Discard |

- **Merge to Timeline** - Recording is merged; if the vessel is intact, it will appear in the game world when the ghost finishes playing
- **Discard** - Recording is thrown away

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

### Preview Playback

You can preview a recording without reverting:

1. Stop recording with **F9**
2. Press **F10** - a ghost replays your flight in real time
3. Press **F11** to stop the preview

### Recordings Manager

Click the "Recordings" button in the main Parsek window to open the Recordings Manager. This secondary window shows all committed recordings in a sortable table:

- **Name** - vessel name
- **Launch Time** - KSP calendar format
- **Duration** - compact format (e.g. "56s", "2m 30s", "1h 15m")
- **Status** - `future` (grey), `active` (green), or `past` (dim) based on current UT
- **Loop / Ghost** - per-recording loop toggle (ghost replays continuously with a pause between cycles)
- **Rewind / F.Forward** - "R" (rewind) / "FF" (fast-forward) buttons for recordings with rewind saves
- **Hide** - per-recording hide toggle (hidden recordings still play as ghosts normally)

Click any column header to sort by that column. Click again to reverse the sort order. The window is draggable and resizable.

The select-all checkbox in the Loop column header toggles looping for all recordings at once. The Hide checkbox in the column header controls whether hidden recordings are shown — when checked (default), hidden recordings are filtered out; uncheck it to see and manage hidden recordings.

The **Countdown** column shows how long until each recording's vessel spawns, formatted as `T-Xd Xh Xm Xs`. It updates live during playback and shows `-` for recordings already past their spawn time.

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

### Settings

Click the "Settings" button in the main Parsek window to open the Settings panel. Settings are saved per-save and can also be accessed from KSP's Difficulty Settings screen (Esc > Settings > Parsek).

| Setting | Default | Description |
|---------|---------|-------------|
| Auto-record on launch | On | Start recording when a vessel leaves the pad/runway |
| Auto-record on EVA | On | Start recording when a kerbal goes EVA from the pad |
| Auto-stop time warp | On | Stop time warp when a ghost playback is about to begin |
| Recording sampling density | Medium | Trajectory sampling precision: Low (smaller files), Medium (balanced), High (cinematic) |

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

### Rewind (Going Back in Time)

Rewind lets you go back to any earlier point in your timeline and launch new missions. Existing recordings continue to play as ghosts alongside your new flights.

**How to rewind:**
1. Open the Recordings Manager (click "Recordings" in the Parsek window)
2. Click the "R" (rewind) / "FF" (fast-forward) button next to any recording that has a rewind save
3. A confirmation dialog shows the vessel name, launch date, and how many future recordings exist
4. On confirm, the game loads back to that recording's launch point in the Space Center

**What happens on rewind:**
- Game time rewinds to the recording's launch UT
- Funds, science, and reputation are reset to their pre-launch values
- Committed game actions (tech research, part purchases, facility upgrades, crew hires) are re-applied automatically
- All committed recordings replay as ghosts from the rewound point, re-applying their resource deltas at the correct times
- The player can launch new missions with the remaining available resources

**Resource safety:**
- Resources are reset to the baseline snapshot captured at recording start. Ghost playback re-applies each recording's resource deltas at the correct UT, so the timeline replays naturally.
- The resource budget display shows committed costs from unreplayed recordings, so the player always sees what's actually available.

### Wipe Recordings

Click "Wipe Recordings" in the Parsek UI window to clear all committed recordings. This also frees any reserved crew and removes replacement kerbals. Milestones are preserved.

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

- **Time warp protection** - Time warp is stopped once when UT is about to enter a recording's time range, but only if the recording has an unspawned vessel. Time warp during active ghost playback is allowed. Can be disabled in Settings.
- **Orbital attitude** - Ghost vessels in orbital segments preserve their recorded orientation. A vessel holding retrograde, normal, or any other SAS mode will hold that attitude throughout the orbit, not snap to prograde. If the PersistentRotation mod is installed, spinning vessels are also reproduced — the ghost spins at the same rate the player saw during time warp.
- **SOI changes** - Recordings that cross SOI boundaries (e.g. Kerbin to Mun) play back correctly. Each trajectory point references its own celestial body.

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
- **Missed EndUT** - If a recording's EndUT passes while you're in the Space Center or Tracking Station, reserved crew are automatically freed so they don't stay stuck as Assigned forever.
