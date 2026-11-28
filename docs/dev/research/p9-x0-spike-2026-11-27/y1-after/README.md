# P9 Y1 "after" flight (the per-ghost sheath rework, timing probe uncapped)

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 2800), branch
`p9-y1-rig-after` at 3fd255f44, leased copy `stock-minimal`. Run
`2026-11-28_0018_PPS-20c-reentry-rig-stock`: PASS attempt 1, 579 s.

DLL sha256 `aae8bf5eaa674f28d830b95a80496219e5a454a3f7ac5418520c364bb81c0a4f` (equal to the
build; utf16 `after(Y1: every ghost draws its own sheath` x3, `sheaths=[` x1, `uncapped: vSync`
x1, `Ghost aero FX renderer ready` x1, `before(main` x0). Re-checked before launch.

`REAL DONE shots=76 files=59`, `GHOST DONE captured=27 late=0 missing=0 files=47`.
`Ghost aero FX renderer ready: shaders=3 detailLevel=3 layer=6 ('') stockFxMask=0x20001
stockLight=Directional` once, 12 `reentry sheath: Draw` decisions, no Parsek WARN or ERROR from
`[ReentryFx]` (the 15 WARN lines in `log-warn-err.txt` are the usual debris / budget ones).

`contact-real-before-after.png`: rows stock real vessel (this flight), BEFORE ghost (y1-before,
main) and AFTER ghost (this flight); columns 72 km, Mach 6, 3.75, 3, 2.5, 2; side camera,
cropped around the probe.

## Timing (uncapped; variant after: every ghost draws its own sheath + depth map)

| mark | ghosts | onMs | offMs | deltaMs | sheaths at "on" | depthMaps |
|---|---|---|---|---|---|---|
| above | 1 / 4 / 8 | 1.414 / 1.358 / 1.363 | 1.410 / 1.390 / 1.426 | 0.004 / -0.031 / -0.063 | M:noFx, copies none | 0 |
| m6 | 1 / 4 / 8 | 1.797 / 1.885 / 1.796 | 1.464 / 1.474 / 1.459 | 0.333 / 0.411 / 0.337 | M:Draw (intensity 1.0), copies NoMeshes | 2 @ 2048 |
| m375 | 1 / 4 / 8 | 1.814 / 1.814 / 1.806 | 1.608 / 1.506 / 1.468 | 0.206 / 0.308 / 0.338 | M:Draw (0.50), copies NoMeshes | 1 @ 2048 |
| m3 | 1 / 4 / 8 | 1.782 / 1.778 / 1.774 | 1.488 / 1.455 / 1.459 | 0.294 / 0.323 / 0.315 | M:Draw (0.07), copies NoMeshes | 1 @ 2048 |
| m25 | 1 / 4 / 8 | 1.428 / 1.415 / 1.421 | 1.430 / 1.428 / 1.452 | -0.002 / -0.013 / -0.031 | M:Draw (0.0), copies none | 1 @ 2048 |
| m2 | 1 / 4 / 8 | 1.445 / 1.418 / 1.407 | 1.439 / 1.435 / 1.428 | 0.005 / -0.017 / -0.021 | M:Draw (0.0), copies none | 0 @ 2048 |

The timing copies (ghosts #9001..#9007) report `NoMeshes`, so only the main ghost drew a sheath at
every count: the 4- and 8-ghost rows measure one sheath, not four or eight.

## What the frames show

Every AFTER ghost frame is dark: no visible sheath at any mark, although the ghost shot lines
say `reentryFx=True intensity=1.000 sheath=Draw` at Mach 6 and the renderer logs
`Ghost #1 sheath drawn: fx=1.000 ... alpha=0.110 length=15.00 color=(1.00,0.29,0.11)`. Those
values are stock's own (`FXCamera.intensity` default 0.11 at shader LOD 0, length 15, the plasma
colour), so the parameters match and the sheath is not reaching the captured screen. The rig
captures with `ScreenCapture.CaptureScreenshotAsTexture` after `WaitForEndOfFrame`, the same path
that shows stock's flames on the real frames.
