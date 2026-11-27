# P9 X0 spike re-flight (2026-11-27, spike commit 62f4cebd)

Second flight of the three spike lanes after the spike fix "guard burst sub-cases,
max aero FX in the rig, watch the ghost at 'above'". Same untracked specs as the
first flight (see `../README.md`).

Deployed Parsek.dll (stock-minimal and modded-restock): size 12534272, sha256
`6ca0e460ba47a1261379047a4268180c0a01d642a4baa65051fec3b5c16d70d9`
(utf8 `FxReentryRig` x6, `PartParityBurstSpikeInGameTests` x17, utf16
`AERO_FX_QUALITY` x2). Hash re-checked before each launch.

| Lane | Install | Verdict | Wall | PNGs |
|---|---|---|---|---|
| PPS-20a | stock-minimal | PASS | 169 s | 5 (`ppb-u1-fxmonger`, `ppb-u1-standins`, `ppb-u3-decouple`, `ppb-u4-dust`, `ppb-u5-drawmesh`) |
| PPS-20c | stock-minimal | PASS | 568 s | 106 (`fxr-real-*`, `fxr-ghost-*`, `fxr-diff-*`) |
| PPS-20b | modded-restock | PASS | 178 s | 5 (same set as PPS-20a) |

Folder layout as in the first flight. There is no `parsek-test-results-head.txt` for
PPS-20a: the next lane re-staged the instance before I collected the file. Its
`BATCH_COMPLETE` line is in `log-PartParityBurstSpike.txt`.

## Burst / dust cell (PPS-20a, PPS-20b)

- `summary: sections=U5,U1U2,U3,U4 files=5 failures=0 [] exceptions=0` on both
  installs. No sub-case threw, so there is no exception type or stack to report.
- The failure in the first flight came right after U1 burst index 0. This time index 1
  (`Explosion 0`, power 0.25) logs `names=[destroyed]`. The prefab's object is gone
  before the 4 s capture window ends, and the guarded code now survives that.
- U3 decouple: three cases (`u3-decoupler-root-2part`, `u3-stacked-3part`,
  `u3-plain-2part`). The two decoupler cases show the `fx_gasBurst_white` emitter at
  about 200 particles, with FX pixels only in the first ~1 s. The plain joint break
  shows no emitter and 0 FX pixels. `fxMongerNew=0` in every case: no FXMonger
  explosion on a decouple. The root decoupler case logged `jointBreaks=0 decouples=0`
  although `isDecoupled=True`.
- U4 dust: terrain at 0.2 of the 30 m reach gives side `dustPx` of about 8000.
  Terrain at 0.7 and at 1.2 (beyond reach) give 0. Ocean at 0.4 gives about 12000
  side / 9000 top. The pad slot did not run: `no LaunchpadFX surface found 18-40 m
  from the host`.

## Reentry rig (PPS-20c)

- `AERO_FX_QUALITY 0 -> 3`, `detailLevel 0 -> 3` before each leg, restored `3 -> 0`
  after each (real and ghost).
- Real side, `REAL DONE shots=76 files=59`. The FX camera state and the aero isolation
  diff (mainA vs mainB, full frame / probe rect) per mark:
  - above (72 km): fxCam off, diff 0.
  - Mach 6: fxCam on, FxScalar 1.0, diff 23290 / 347.
  - Mach 3.75: fxCam on, diff 13955 / 309.
  - Mach 3: fxCam on, FxScalar 0.14, diff 429 / 0.
  - Mach 2.5: fxCam on, diff 0.
  - Mach 2: fxCam off, diff 0.

  So the stock aero flames now render from Mach 6 down to about Mach 3.
- Ghost side, `GHOST DONE captured=27 late=0 missing=0 files=47`. The ghost is now
  watched at `above`. At Mach 6 the ghost logs `reentryFx=True intensity=0.940
  fireParticles=840 shellMeshes=6`.
- The framing question from the first flight is settled. With the new build the real
  probe rect and the ghost rect match (Mach 6: real `[524,354,36,15]`, ghost
  `[524,352,36,17]`), so the 230 px real rect in the first flight was a measurement
  artifact.
- WARN lines are the same kinds as in the first flight, all on `FXR Probe Debris` or
  budgets: unrecorded-gap boundary discontinuities, non-monotonic flush stitch (#419),
  solver unavailable, relative-retired.
