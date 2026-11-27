# P9 Y1 "before" flight (main's reentry fire, with the frame-time probe)

Lane PPS-20c (untracked spec, from the X0 spike; both deferred `WarpToUT` budgets kept at
540 s, the validator's cap, `budgetSeconds` 2800), flown 2026-11-27 23:29 UTC on the leased copy
`stock-minimal-2` from branch `p9-y1-rig-before` at 4da56875f. Run
`2026-11-27_2329_PPS-20c-reentry-rig-stock`: PASS attempt 1, 703 s.

Deployed DLL sha256 `3f149a1e03f74971e0df37a4c3bec4d031fd65692221e92a554c30197fb9555d` (equal
to the worktree build; utf16 `before(main` x3, `timing copy snapshot after staging` x1,
`after(Y1` x0). Re-checked before launch.

`REAL DONE shots=76 storedFrames=20 files=59`, `GHOST DONE captured=27 late=0 missing=0
files=47`. Frames for every mark: above, m6, m375, m3, m25, m2 (106 pngs). No timing mark was
skipped, aborted or truncated.

## Timing (variant `before`: particles + shell; off = renderer off + shell list emptied)

| mark | ghosts | onMs | offMs | deltaMs | fire at "on" |
|---|---|---|---|---|---|
| above | 1 / 4 / 8 | 16.799 / 16.814 / 16.794 | 16.791 / 16.852 / 16.801 | 0.008 / -0.039 / -0.006 | intensity 0 |
| m6 | 1 / 4 / 8 | 16.717 / 16.715 / 16.715 | 16.722 / 16.724 / 16.711 | -0.005 / -0.009 / 0.004 | intensity 0.86, 785 / 3129 / 6300 particles |
| m375 | 1 / 4 / 8 | 16.722 / 16.728 / 16.705 | 16.772 / 16.793 / 16.746 | -0.051 / -0.065 / -0.041 | intensity 0.24, 314 / 1231 / 2482 particles |
| m3 | 1 / 4 / 8 | 16.703 / 16.711 / 16.725 | 16.775 / 16.793 / 16.770 | -0.071 / -0.082 / -0.046 | intensity 0.12, 218 / 877 / 1686 particles |
| m25 | 1 / 4 / 8 | 16.783 / 16.804 / 16.810 | 16.754 / 16.800 / 16.780 | 0.029 / 0.004 / 0.031 | intensity 0 |
| m2 | 1 / 4 / 8 | 16.775 / 16.789 / 16.774 | 16.891 / 16.786 / 16.811 | -0.115 / 0.003 / -0.037 | intensity 0 |

Caveat: every mean sits at about 16.7 ms, the 60 FPS vsync interval. The game is frame-capped,
so these numbers show that 8 burning ghosts (6300 particles) still fit inside one 60 FPS frame on
this PC. They cannot measure how much of the frame the fire costs. A real cost figure needs vsync
off (or the frame limiter raised) during the probe, or a CPU/GPU timer around the FX work itself.

Files: `fxr-*.png` (real, ghost, diff frames), `log-FxReentryRig.txt` (every `[FxReentryRig]`
line, timing included), `result.json`, `KSP.log.gz`.
