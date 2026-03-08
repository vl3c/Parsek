# Parsek

![Parsek](img/ksp-parsek-stylized.jpg)

*Record, rewind and merge your parallel-sekuential adventures to a single player main timeline.*

**Time-rewind mission recording for KSP1.** Record missions sequentially, return to an earlier time, and watch them play out in parallel alongside you while you fly new ones.

## How It Works

1. **Launch a mission** and fly it normally
2. **Recording starts automatically** when your vessel leaves the pad
3. **Merge your recorded mission** to the single player main timeline
4. **Rewind to any launch**
5. **Launch another mission** — your recorded flight replays alongside you
6. **Vessel spawns** at its final recorded position with the original crew when playback finishes

Recorded vessels are full visual replicas — original part meshes, textures, engine flames, staging, and parachutes all play back at the correct times.

## Features

- **Automatic recording** on launch and EVA from pad
- **Visual replay** with original part meshes, textures, and engine FX
- **Vessel persistence** — recorded vessels spawn with crew, or get recovered for funds
- **Crew management** — reserved crew get temporary replacements so your roster stays full
- **Orbital recording** — time warp segments use analytical Keplerian orbits
- **Part events** — staging, decoupling, parachutes, engines, solar panels, antennas, lights, landing gear, cargo bays, fairings, RCS, and inventory deployables replay on the ghost; docking/undocking are recorded as chain boundaries
- **Resource tracking** — game actions related to funds, science, and reputation deltas are recorded and applied at the correct time
- **Rewind** — go back to any earlier point in your timeline; resources reset to baseline, ghost playback re-applies everything at the correct time
- **Multi-vessel recording** — undocking, EVA, and docking are tracked automatically; all vessels in a mission record as a single tree
- **Career mode integration** — milestones track tech research, part purchases, facility upgrades, and contracts; resource budgeting prevents paradoxes when rewinding
- **Recordings manager** — browse, sort, loop, and delete individual recordings
- **External recording files** — bulk trajectory data stored in sidecar files, keeping saves lightweight

## Controls

The Parsek window is available from the toolbar button in Flight and Map view.

## Installation

Requires KSP 1.12.x.

**Dependencies** (install these first):

- [Module Manager](https://github.com/sarbian/ModuleManager)
- [Harmony (HarmonyKSP)](https://github.com/KSPModdingLibs/HarmonyKSP)
- [ClickThroughBlocker](https://github.com/linuxgurugamer/ClickThroughBlocker)
- [ToolbarControl](https://github.com/linuxgurugamer/ToolbarControl)

Copy the `Parsek` folder into `GameData/`.

## Building from Source

```
cd Source/Parsek
dotnet build
```

Requires .NET SDK and KSP assemblies in `Kerbal Space Program/KSP_x64_Data/Managed/`.

## Beyond Recording

Parsek's infrastructure — looped playback, vessel snapshots, game state tracking, resource budgeting — forms a natural foundation for building on top of. Some possibilities:

- **Logistics network** — fly a cargo route once, Parsek records it, then that recording becomes a reusable supply route that replays automatically between bases
- **Multiplayer-like experience** — share recording files with other players and watch their missions play out as ghosts in your game, turning single-player KSP into a shared timeline

See the [roadmap](docs/roadmap.md) for what's planned and what's possible.

## License

MIT

## Acknowledgements

Parsek was inspired by and learned from the KSP modding community. The following mods and their authors shaped our approach to vessel recording, playback, spawning, and UI integration:

- **[FMRS](https://github.com/linuxgurugamer/FMRS)** (linuxgurugamer / dtobi) — time-revert patterns for stage recovery
- **[Persistent Trails](https://github.com/JPLRepo/KSPPersistentTrails)** (JPLRepo) — trajectory recording and adaptive sampling
- **[KSP Community Fixes](https://github.com/KSPModdingLibs/KSPCommunityFixes)** (gotmachine / KSPModdingLibs) — Harmony patching patterns, performance techniques
- **[VesselMover](https://github.com/jrodrigv/VesselMover)** (jrodrigv / BDArmory team) — vessel positioning and geographic coordinate handling
- **[StageRecovery](https://github.com/linuxgurugamer/StageRecovery)** (linuxgurugamer / magico13) — GameEvents + polling hybrid for event detection
- **[Kerbal Alarm Clock](https://github.com/TriggerAu/KerbalAlarmClock)** (TriggerAu) — time-based event scheduling
- **[ClickThroughBlocker](https://github.com/linuxgurugamer/ClickThroughBlocker)** (linuxgurugamer) — UI click-through prevention
- **[ToolbarControl](https://github.com/linuxgurugamer/ToolbarControl)** (linuxgurugamer) — toolbar integration
- **[Module Manager](https://github.com/sarbian/ModuleManager)** (sarbian) — essential config patching
- **[Harmony (HarmonyKSP)](https://github.com/KSPModdingLibs/HarmonyKSP)** (KSPModdingLibs / pardeike) — runtime method patching

Special thanks to **[linuxgurugamer](https://github.com/linuxgurugamer/)** for maintaining so many essential KSP mods, and to the KSP modding community for making this kind of project possible.
