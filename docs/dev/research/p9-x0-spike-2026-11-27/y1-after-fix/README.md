# P9 Y1 after-fix confirm flight (on-screen submission fix)

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 3000,
`verboseLogging` on), branch `p9-y1-rig-after` at 0fd539e50, leased copy `stock-minimal`. Run
`2026-11-28_0203_PPS-20c-reentry-rig-stock`: PASS attempt 1, 578 s.

DLL sha256 `d3f5d0aedf33c506d0cf0b684aa1af575df2a9baccbb1ee8e3f589c396af6961` (equal to the
build; utf16 `submitted=`, `fxr-diag files written=`, `after(Y1: every ghost draws its own
sheath` present). Re-checked before launch.

`REAL DONE shots=76 files=59`, `GHOST DONE captured=27 late=0 missing=0 files=57`, both diag
marks `fxr-diag files written=5`. No `[ReentryFx]` WARN or ERROR.

`contact-real-before-after-fix.png`: rows stock real vessel (this flight), BEFORE ghost
(y1-before, main) and AFTER ghost (this flight); columns 72 km, Mach 6, 3.75, 3, 2.5, 2.

## Result: the sheath reaches the screen

- `fxr-diag-*-sanity`: the magenta probe silhouette now shows on screen (it did not in
  y1-after-diag).
- `fxr-diag-*-nodepth` and the normal ghost shots at m6, m375 and m3 show the flame.
- `submitted=` on drawing frames: 15, 6 and 41 (0 on frames with nothing to draw).

## Look against stock (side camera)

- 72 km: no flame on either side. Match.
- Mach 6: a stock-like orange shell trailing back along the direction of travel, same colour
  and streaking, about stock's length. A little dimmer than the real probe's flame, with a
  weaker bright rim at the base.
- Mach 3.75: visible, same colour and direction, but dimmer and narrower than stock's, which is
  still close to full there.
- Mach 3: faint on both, similar.
- Mach 2.5 and Mach 2: dark on both. Match.

## Timing (uncapped; now every copy draws)

| mark | ghosts | onMs | offMs | deltaMs | sheaths |
|---|---|---|---|---|---|
| above | 1 / 4 / 8 | 1.411 / 1.448 / 1.362 | 1.396 / 1.377 / 1.371 | 0.016 / 0.071 / -0.009 | noFx |
| m6 | 1 / 4 / 8 | 1.851 / 2.280 / 3.009 | 1.484 / 1.483 / 1.658 | 0.366 / 0.797 / 1.351 | Draw x1 / x4 / x8 |
| m375 | 1 / 4 / 8 | 1.890 / 2.281 / 2.932 | 1.494 / 1.473 / 1.463 | 0.396 / 0.808 / 1.469 | Draw x1 / x4 / x8 |
| m3 | 1 / 4 / 8 | 1.845 / 2.242 / 2.726 | 1.531 / 1.497 / 1.490 | 0.314 / 0.746 / 1.236 | Draw x1 / x4 / x8 |
| m25 | 1 / 4 / 8 | 1.681 / 1.433 / 1.398 | 1.665 / 1.428 / 1.429 | 0.016 / 0.005 / -0.031 | M:Draw at 0, copies none |
| m2 | 1 / 4 / 8 | 1.461 / 1.437 / 1.501 | 1.473 / 1.446 / 1.427 | -0.012 / -0.009 / 0.074 | M:Draw at 0, copies none |

The cost is roughly 0.3 ms for the first burning ghost plus about 0.15 ms for each extra one
(8 ghosts: about 1.2 to 1.5 ms a frame), against main's under 0.2 ms for 8 (y1-before2).
