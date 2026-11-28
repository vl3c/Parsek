# P9 Y1 "before2" flight (main's reentry fire, timing probe uncapped)

A re-fly of the y1-before timing with vsync and the frame limiter off during the probe. Lane
PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 2800), branch
`p9-y1-rig-before` at b9970a4c1, leased copy `stock-minimal`. Run
`2026-11-28_0028_PPS-20c-reentry-rig-stock`: PASS attempt 1, 579 s.

DLL sha256 `17b794227c0dd52c498bcae8aa36e74032697ef187070c03413c7778b0a49690` (equal to the
build; utf16 `before(main` x3, `uncapped: vSync` x1, `after(Y1` x0). Re-checked before launch.

`REAL DONE shots=86 files=64`, `GHOST DONE captured=30 late=0 missing=0 files=52`. Every timing
mark logged `uncapped: vSync ... targetFps 60->-1`. None was skipped, aborted or truncated.

| mark | ghosts | onMs | offMs | deltaMs | fire at "on" |
|---|---|---|---|---|---|
| above | 1 / 4 / 8 | 1.366 / 1.363 / 1.357 | 1.368 / 1.390 / 1.392 | -0.002 / -0.027 / -0.035 | none |
| m6 | 1 / 4 / 8 | 1.708 / 1.566 / 1.586 | 1.537 / 1.531 / 1.534 | 0.171 / 0.035 / 0.051 | intensity 0.85; 799 / 3100 / 6198 particles |
| m375 | 1 / 4 / 8 | 1.584 / 1.858 / 1.568 | 1.539 / 1.765 / 1.537 | 0.044 / 0.093 / 0.031 | 0.24; 296 / 1205 / 2419 |
| m3 | 1 / 4 / 8 | 1.596 / 1.578 / 1.564 | 1.528 / 1.640 / 1.496 | 0.068 / -0.062 / 0.068 | 0.14; 220 / 904 / 1812 |
| m25 | 1 / 4 / 8 | 1.456 / 1.426 / 1.468 | 1.410 / 1.425 / 1.398 | 0.046 / 0.001 / 0.070 | none |
| m2 | 1 / 4 / 8 | 1.416 / 1.397 / 1.407 | 1.419 / 1.411 / 1.421 | -0.002 / -0.013 / -0.014 | none |

Reading: with main's fire, even 8 burning ghosts (about 6200 particles) cost under 0.2 ms a
frame on this PC, inside the run-to-run noise (m375 with 4 ghosts has a p95 of 4.8 ms both on
and off).
