# ghost-fx-9 (PR #2837) flight evidence, 2026-11-27

Build: branch `ghost-fx-9` head a0380cc88 plus the throwaway rig (hooks.diff, GhostFx9Rig.cs,
FX9-1 spec; never committed to ghost-fx-9). Provisioned into automation/stock-minimal from the
Parsek-ghost-fx-9 worktree. Deployed sha256: 12691ee1 for PP-7 and FX9-1 run 1, d41de014 for
FX9-1 run 2 (rig fix only). All rig markers were checked in the deployed DLL before each flight.

## Verdicts

| Lane | Run id | Verdict |
|---|---|---|
| PP-7-part-parity-fx-stock | 2026-11-27_1725 | PASS (report-only, no pp7 baseline) |
| FX9-1-transient-fx-stock (run 1) | 2026-11-27_1742 | INVALID (rig: driver-INVALID seam-timeout at StopRecording) |
| FX9-1-transient-fx-stock (run 2) | 2026-11-27_1751 | PASS (rig fixed) |

FX9-1 run 1 failures were rig faults, not Parsek faults:
- Spawned case vessels only load within 2.25 km of the active vessel (stock flying load
  range), and the burning dust craft carried the active vessel away: lamp, flea, decouple and
  reentry never loaded. Fix: the rig extends each spawned vessel's VesselRanges to 30 km.
- Rewind-to-Launch strips the pad craft; the fixture then has no vessel, so the reload lands
  in the Space Center and StopRecording waits for FLIGHT forever. Fix: the rig spawns an
  "Fx9 anchor" craft beside the pad before the lead recording, so it is in the rewind save.

FX9-1 run 2 still had one rig fault: the lamp craft (spawned at 900 m) crashed 14.5 s later,
KSP switched to the pad craft and Parsek resumed the committed lead tree on it, so the flea
case's StartRecording was refused and the flea case recorded the lead tree. The flea ghost
readings of FX9-1 are therefore NOT the flea. `rig/GhostFx9Rig.next.cs` (unflown) raises the
lamp to 2600 m and the decouple to 3600 m for any re-fly.

## Readings (FX9-1 run 2, `fx9-1-2026-11-27_1751/GhostFx9-lines.txt`)

Part puff (decoupler fire on the ghost, viewer flying beside it at 66-71 m/s):
- before: centroid 18.1 m from its spawn point at 0.28 s, 59.3 m at 0.88 s
- after:  0.1 m at 0.28 s, 0.2 m at 0.88 s

Explosion (ghost explosion FX the rig spawns at the decouple ghost, viewer at 80-97 m/s):
- before: 76.4 m from spawn at 1 s, 161.4 m at 2 s
- after:  0.1 m at 1 s, 0.4 m at 2 s

Lamp (spotLight1) intensity at +0.05 / 0.15 / 0.3 / 0.6 / 1.2 / 1.8 s:
- on,  real:   0.143 0.337 0.537 0.785 0.954 0.990
- on,  after:  0.095 0.295 0.528 0.777 0.950 0.988
- on,  before: 1.000 at every sample (snaps on)
- off, real:   0.852 0.659 0.461 0.213 0.046 0.010
- off, after:  0.899 0.700 0.469 0.222 0.049 0.012
- off, before: light disabled at +0.05 s (snaps off)

Launch dust: not exercised. No `ParsekLaunchDust` system existed on the dust ghost (8 m AGL,
Swivel at full throttle), so no stats line; the stock real shot shows no visible dust either.
Before and after pictures match (viewer frameVel 0, so the floating-origin fix has nothing to
move there).

Reentry fire: not exercised. Mach ~2.8 at 12 km built no `ReentryFire` system on the ghost and
the camera faces the sun; before / after / real pictures are the same dark frame.

## Solid booster sparks (PP-7, `pp7-fx-layers-*.txt`, strips in pp7-before / pp7-after)

Before (2026-11-24_2120) every running-spark booster listed `fxMissing=[fx_exhaustSparks_yellow]`:
Flea (solidBooster.sm.v2), Hammer (solidBooster.v2), Thumper (solidBooster1-1), Mite, Shrimp,
Pollux, Thoroughbred, Clydesdale. After (2026-11-27_1725) all eight read `fxMissing=[]`,
ghost flame layer 2 = real 2. The remaining fxMissing entries on other engines are only the
hidden flameout sparks (`fx_exhaustSparks_flameout*`), which stay out by design.

## Juno smoke

No `Engine FX thrust share:` line in either run: PP-7 has no jet-engine case and FX9-1 has no
Juno. The Juno full-throttle smoke stays unproven by flight.

## PP-7 judge counts (report-only)

after 2026-11-27_1725: pass 103 / warn 21 / fail 4; before 2026-11-24_2120: pass 103 / warn 23 /
fail 2. The before run is on an older main (FX PRs #2828 #2829 #2830 merged since), so the diff
is not #2837 alone. New fails: liquidEngineRK-7 burn-full (start colour hueL1 0.538, was a
0.211 warn), omsEngine burn-low (far flame length 1.47x), rcsBlock.01.small rcs-fire (was warn);
cleared: liquidEngine2-2.v2 doublebell burn-low. rcsblock.v2 fails in both.

## Rig v2 (ground re-shoot, 2026-11-27)

`rig/GhostFx9Rig.ground.cs` + `rig/FX9-2-ground-fx-stock.toml` (+ the same `rig/hooks.diff`). Copy the .cs to
`Source/Parsek/InGameTests/GhostFx9Rig.cs` and the toml to `harness/scenarios/` in a scratch worktree at
origin/main, apply hooks.diff, build, provision, fly `FX9-2-ground-fx-stock`, then revert. Never merged.
Frames: `fx9g-<case>-<real|before|after>-kNN.png`, one every 0.25 s from 0.25 s before the event.
