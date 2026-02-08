# Parsek User Guide

Parsek lets you record missions, revert to launch, and merge them into the timeline so they play out automatically while you fly new missions.

## Controls

| Key | Action |
|-----|--------|
| **F9** | Start / Stop recording |
| **F10** | Preview playback (current recording) |
| **F11** | Stop preview |
| **Alt+P** | Toggle UI window |

## How It Works

### Recording a Mission

1. Launch any vessel (career mode recommended for resource tracking)
2. Recording starts automatically when the vessel leaves the pad/runway
3. Fly your mission normally
4. Press **F9** to stop recording
5. Revert to Launch (Esc > Revert to Launch)

You can also press **F9** manually at any time to start or stop recording. Going EVA from a vessel on the pad will also auto-start recording on the EVA kerbal.

### Merge Dialog

After reverting, a dialog appears with context-aware options:

| Situation | Default Option | Other Options |
|-----------|---------------|---------------|
| Vessel barely moved (<100m) | Merge + Recover | Merge + Keep Vessel, Discard |
| Vessel destroyed | Merge to Timeline | Discard |
| Vessel intact, moved far | Merge + Keep Vessel | Merge + Recover, Discard |

- **Merge + Recover** — Recording is merged; vessel is recovered for funds immediately
- **Merge + Keep Vessel** — Recording is merged; vessel will appear in the game world when the ghost finishes playing
- **Merge to Timeline** — Recording is merged; no vessel spawned (for destroyed vessels)
- **Discard** — Recording is thrown away

### Timeline Playback

After merging, wait on the pad (or time warp) until UT reaches the recording's timestamps:

- A green-cyan ghost sphere appears and replays the recorded flight
- Funds, science, and reputation changes from the recording are applied at the correct time
- When the ghost finishes, the vessel appears at its final position (if "Keep Vessel" was chosen)

### Crew Management

When you choose "Merge + Keep Vessel", the recorded crew (e.g. Jeb) are reserved for the deferred vessel spawn. A replacement kerbal with the same trait is hired automatically so your available crew pool stays the same size. When the vessel spawns at EndUT, the original crew board it and the replacement is removed.

### Preview Playback

You can preview a recording without reverting:

1. Stop recording with **F9**
2. Press **F10** — a ghost replays your flight in real time
3. Press **F11** to stop the preview

### Wipe Recordings

Click "Wipe Recordings" in the Parsek UI window (Alt+P) to clear all committed recordings. This also frees any reserved crew and removes replacement kerbals.

## Automatic Behaviors

Parsek handles several edge cases automatically. These are logged to `KSP.log` (search for `[Parsek Spike]` or `[Parsek Scenario]`).

### Recording

- **Auto-start on launch** — Recording begins automatically when a vessel leaves the pad or runway (transitions out of PRELAUNCH). A screen message confirms "Recording STARTED (auto)".
- **Auto-start on EVA from pad** — Going EVA from a vessel sitting on the pad/runway also auto-starts recording on the EVA kerbal.
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
