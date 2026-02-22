# Parsek

![Parsek](img/ksp-parsek-stylized.jpg)

*Record, rewind and merge your parallel-sekuential adventures.*

**Time-rewind mission recording for KSP1.** Record missions sequentially, return to an earlier time, and watch them play out in parallel alongside you while you fly new ones.

## How It Works

1. **Launch a mission** and fly it normally
2. **Recording starts automatically** when your vessel leaves the pad
3. **Revert to launch** when you're done
4. **Choose what happens** — keep the vessel for later, recover it for funds, or discard
5. **Launch another mission** — your recorded flight replays alongside you
6. **Vessel spawns** at its final position with the original crew when playback finishes

Recorded vessels are full visual replicas — original part meshes, textures, engine flames, staging, and parachutes all play back at the correct times.

## Features

- **Automatic recording** on launch and EVA from pad
- **Visual replay** with original part meshes, textures, and engine FX
- **Vessel persistence** — recorded vessels spawn with crew, or get recovered for funds
- **Crew management** — reserved crew get temporary replacements so your roster stays full
- **Orbital recording** — time warp segments use analytical Keplerian orbits
- **Part events** — staging, decoupling, parachute deploy, engine ignition/shutdown all replay on the recording
- **Resource tracking** — funds, science, and reputation deltas applied at the correct time
- **Take control** — grab a recording mid-playback and fly it yourself
- **External recording files** — bulk trajectory data stored in sidecar files, keeping saves lightweight

## Controls

| Key     | Action                               |
| ------- | ------------------------------------ |
| **F9**  | Start / Stop recording               |
| **F10** | Preview playback (current recording) |
| **F11** | Stop preview                         |

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

Special thanks to **linuxgurugamer** for maintaining so many essential KSP mods, and to the KSP modding community for making this kind of project possible.
