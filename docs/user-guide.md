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
3. On revert, both ghosts play back — the vessel ghost and the EVA kerbal ghost
4. When the parent vessel spawns, the EVA'd kerbal is excluded from its crew

### Merge Dialog

After reverting, a dialog appears with context-aware options:

| Situation | Default Option | Other Options |
|-----------|---------------|---------------|
| Vessel barely moved (short duration or low max distance) | Merge + Keep Vessel | Merge + Recover, Discard |
| Vessel returned to pad after a real mission | Merge + Keep Vessel | Merge + Recover, Discard |
| Vessel destroyed, near pad | Merge + Recover | Discard |
| Vessel destroyed, far from pad | Merge to Timeline | Discard |
| Vessel intact, moved far | Merge + Keep Vessel | Merge + Recover, Discard |

- **Merge + Recover** — Recording is merged; vessel is recovered for funds immediately
- **Merge + Keep Vessel** — Recording is merged; vessel will appear in the game world when the ghost finishes playing
- **Merge to Timeline** — Recording is merged; no vessel spawned (for destroyed vessels)
- **Discard** — Recording is thrown away

### Timeline Playback

After merging, wait on the pad (or time warp) until UT reaches the recording's timestamps:

- A ghost vessel appears (opaque replica with original part meshes and textures) and replays the recorded flight
- Engine flames and smoke appear on the ghost during burn phases (both modern EFFECTS engines and legacy stock parts like the Flea SRB)
- Parts that were decoupled or destroyed during the recording disappear from the ghost at the correct time
- Parachute canopies deploy on the ghost at the correct time (real canopy mesh, not a placeholder)
- Engine shrouds are jettisoned on the ghost when staging occurs
- Funds, science, and reputation changes from the recording are applied at the correct time
- When the ghost finishes, the vessel appears at its final position (if "Keep Vessel" was chosen)

### Crew Management

When you choose "Merge + Keep Vessel", the recorded crew (e.g. Jeb) are reserved for the deferred vessel spawn. A replacement kerbal with the same trait is hired automatically so your available crew pool stays the same size. When the vessel spawns at EndUT, the original crew board it and the replacement is removed.

### Take Control

While a ghost is actively playing back, you can take control of it from the Parsek UI window:

1. Open the Parsek window (toolbar button)
2. Click "Take Control" next to an active ghost
3. The ghost is replaced with a real vessel at its current position and velocity
4. You are switched to the new vessel and can fly it normally

The recording is marked as taken and the ghost will not reappear. Crew reservations are cleaned up as if the vessel had spawned normally at EndUT.

### Preview Playback

You can preview a recording without reverting:

1. Stop recording with **F9**
2. Press **F10** — a ghost replays your flight in real time
3. Press **F11** to stop the preview

### Wipe Recordings

Click "Wipe Recordings" in the Parsek UI window to clear all committed recordings. This also frees any reserved crew and removes replacement kerbals.

## Automatic Behaviors

Parsek handles several edge cases automatically. These are logged to `KSP.log` (search for `[Parsek]` or `[Parsek Scenario]`).

### Recording

- **Auto-start on launch** — Recording begins automatically when a vessel leaves the pad or runway (transitions out of PRELAUNCH). A screen message confirms "Recording STARTED (auto)".
- **Auto-start on EVA from pad** — Going EVA from a vessel sitting on the pad/runway also auto-starts recording on the EVA kerbal.
- **Mid-recording EVA** — Going EVA during an active recording auto-stops the parent recording, commits it, and starts a linked child recording on the EVA kerbal.
- **Part events** — Staging, decoupling, engine ignition/shutdown, and parachute events are recorded with timestamps. During ghost playback, decoupled parts (and their subtrees) disappear from the ghost at the correct time. Engines emit flames and smoke during burn phases. Parachute canopies deploy with the real mesh, and engine shrouds are jettisoned.
- **Paused game** — Recording cannot start while the game is paused.
- **Vessel change** — If the active vessel changes during recording (docking, switching with `[`/`]`), the recording stops automatically with a screen message.
- **Very short recordings** — Recordings with fewer than 2 sample points are silently dropped on revert (nothing to play back).

### Ghost Playback

- **Time warp protection** — Time warp is stopped once when UT is about to enter a recording's time range, but only if the recording has an unspawned vessel. Time warp during active ghost playback is allowed.
- **SOI changes** — Recordings that cross SOI boundaries (e.g. Kerbin to Mun) play back correctly. Each trajectory point references its own celestial body.

### Vessel Spawning

- **Proximity offset** — If a vessel would spawn within 200m of any other vessel, it is automatically moved to 250m away to prevent physics collisions. This can happen when multiple recordings end near the same location or near the launchpad.
- **Duplicate prevention** — Each spawned vessel is tracked by its persistent ID. If the vessel already exists (e.g. after a scene change), it won't be spawned again.
- **Dead crew removal** — If a crew member died during the recording but the vessel survived, they are removed from the snapshot before spawning.

### Resources

- **No negative balance** — Funds, science, and reputation deltas are clamped so they never go below zero.
- **Quicksave safety** — Resource application progress is saved, so quickloading doesn't double-apply deltas.

### Scene Transitions

- **Abort Mission** — If you leave Flight without reverting (e.g. Esc > Abort Mission to Space Center), any pending recording is automatically committed to the timeline. The vessel snapshot is discarded since the merge dialog isn't available outside Flight.
- **Missed EndUT** — If a recording's EndUT passes while you're in the Space Center or Tracking Station, reserved crew are automatically freed so they don't stay stuck as Assigned forever.
