# P9 Y1 after-tune confirm flight

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 3000,
`verboseLogging` on), branch `p9-y1-rig-after` at 903d72696, leased copy `stock-minimal`. Run
`2026-11-28_0251_PPS-20c-reentry-rig-stock`: PASS attempt 1, 578 s.

DLL sha256 `dda3a1d10bffb29a243e2e5603568bfa3e4a2f7edc559fff8119bb3a768207a5` (equal to the
build; utf16 `speedSource=`, `tempOffset=`, `submitted=` present). Re-checked before launch.
No `[ReentryFx]` WARN or ERROR.

`contact-real-before-after-tune.png`: rows stock real vessel (this flight), BEFORE ghost
(y1-before, main) and AFTER ghost (this flight); columns 72 km, Mach 6, 3.75, 3, 2.5, 2.

## Ghost inputs against stock at the same marks

| mark | stock real (FxScalar, state, speed, rho) | ghost isolation frame (fx, state, speed / source / chord, rho, temp / offset, mach, rawFx) |
|---|---|---|
| m6 | 1.000, 1.000, 1818.0, 0.01128 | 1.000, 1.000, 1818.2 / stock / 1844.0, 0.01150, 231.1K / 25.6K, 5.966, 1.000 |
| m375 | 0.836, 0.998, 1110.2, 0.03291 | 0.806, 0.972, 1111.4 / stock / 1096.5, 0.03331, 222.7K / 25.6K, 3.715, 0.785 |
| m3 | 0.143, 0.400, 885.0, 0.04339 | 0.136, 0.379, 886.1 / stock / 890.9, 0.04382, 221.0K / 25.6K, 2.973, 0.128 |

Velocity camera at the diag captures: m375 ortho 32.4 / far 50 (stock's: 32.4 / 50); m6 ortho
2856.5 / far 2861.1 (that capture used depth map 1, sized to reach a ghost about 2.8 km away).

## Look against stock (side camera)

- 72 km, Mach 2.5, Mach 2: no flame on either side. Match.
- Mach 6: same orange colour, same streaky shell, same trail direction, about stock's length and
  base width. Now as bright as stock or a little brighter. One difference: a bright V of two
  converging streaks at the base of the ghost's flame that stock's flame does not show.
- Mach 3.75: now close to stock in colour, length, width and direction; a little brighter than
  stock's probe flame here, the same base V.
- Mach 3: faint on both; the ghost's is a little more visible and shows the same V.

## Timing (uncapped)

| mark | ghosts | onMs | offMs | deltaMs | sheaths drawn |
|---|---|---|---|---|---|
| above | 1 / 4 / 8 | 1.412 / 1.373 / 1.485 | 1.418 / 1.382 / 1.379 | -0.006 / -0.009 / 0.106 | 0 |
| m6 | 1 / 4 / 8 | 2.178 / 2.351 / 2.840 | 1.815 / 1.622 / 1.544 | 0.363 / 0.729 / 1.296 | 1 / 4 / 8 |
| m375 | 1 / 4 / 8 | 1.960 / 2.629 / 2.924 | 1.576 / 1.809 / 1.549 | 0.385 / 0.820 / 1.375 | 1 / 4 / 8 |
| m3 | 1 / 4 / 8 | 1.915 / 2.306 / 2.840 | 1.626 / 1.517 / 1.607 | 0.290 / 0.789 / 1.233 | 1 / 4 / 8 |
| m25 | 1 / 4 / 8 | 1.501 / 1.482 / 1.462 | 1.489 / 1.479 / 1.505 | 0.012 / 0.003 / -0.043 | 1 (at 0) |
| m2 | 1 / 4 / 8 | 1.527 / 1.469 / 1.420 | 1.472 / 1.474 / 1.461 | 0.055 / -0.005 / -0.040 | 1 (at 0) |
