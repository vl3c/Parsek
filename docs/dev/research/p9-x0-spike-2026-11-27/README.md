# P9 X0 spike flights (2026-11-27)

Evidence for `docs/dev/design-part-ghost-parity.md` Appendix G (G.6 U1-U10). The
spike code is the THROWAWAY branch `fx-x0-spike` (commit 2191dc953, never merged);
the lane specs PPS-20a/b/c lived outside the repo and were flown as untracked
files from that worktree. All three lanes are report-only rigs.

Deployed Parsek.dll (both installs): size 12526592,
sha256 `41cb1cfedfed5b474575979ca9a38baea5006fbe9c2f6dc220c4d7411d37b2d2`
(utf8 `FxReentryRig` x6, `PartParityBurstSpikeInGameTests` x13). Hash re-checked
before each launch.

| Lane | Install | Verdict | Wall | Classify reason | PNGs |
|---|---|---|---|---|---|
| PPS-20a | stock-minimal | PARSEK-FAIL | 70 s | results FAIL rows or count mismatch | 1 (`ppb-u5-drawmesh.png`) |
| PPS-20c | stock-minimal | PASS | 570 s | driver valid, every verifier PASS/SKIPPED | 99 (`fxr-real-*` 59, `fxr-ghost-*` 23, `fxr-diff-*` 17) |
| PPS-20b | modded-restock | PARSEK-FAIL | 90 s | results FAIL rows or count mismatch | 1 (`ppb-u5-drawmesh.png`) |

Per lane folder: `log-<tag>.txt` (every `[PartParityBurstSpike]` / `[FxReentryRig]`
line plus the runner's batch lines), `log-warn-err.txt` (every `[Parsek]` WARN/ERROR
line inside that window), `result-summary.json` (verdict, verifier statuses, seam
steps), `parsek-test-results-head.txt` (burst lanes) and the pngs. Full snapshots
(result JSON, KSP.log, Player.log, produced save, contact sheet) are on the PC under
`logs/flights/p9-x0-<lane>-2026-11-27/`.

## PPS-20a / PPS-20b: the burst cell stops in U1

Same result on both installs. U5 (DrawMesh in a manual render) and the U1 / U2
merge cases ran and logged. The per-prefab burst loop logged `U1 burst index=0`
(Thud 0) and then the test failed with an EMPTY failure message about 4.5 s
later (one 0.5 s gap plus the 4 s capture window of index 1), before the
`U1 burst index=1` line. So `ppb-u1-fxmonger.png`, `ppb-u1-standins.png` and every
U3 (decouple burst vs joint break) and U4 (launch dust) output were never produced,
and the `summary:` line never printed. The runner records only `inner.Message`, so
no stack trace exists in KSP.log or Player.log. The cause is in the spike code (an
exception with an empty message thrown during or right after the index-1 capture);
a rerun needs the spike to log the exception type and stack.

## PPS-20c: reentry rig

- Real side: `REAL DONE shots=76 storedFrames=20 files=59 ut=81.8`. Marks above
  (72 km), m6, m375, m3, m25, m2 all reached; the booster sweep ran only at m6
  (booster 2.6 km away there, `booster=none` at the other marks).
- Ghost side: `GHOST DONE captured=23 late=0 missing=4 files=40 ut=79.1`. The four
  missing shots are all of mark `above` (ut 29.30 to 29.50): no ghost was there to
  shoot, and watch mode entered only later (`ghost watch enter index=1`). So
  there is no ghost `above` frame and no `fxr-diff-above-*`.
- At the same marks the ghost's main rect is much smaller than the real probe's
  (for example m6 mainA: real probe `[524,282,230,87]`, ghost main `[524,352,36,17]`),
  so the ghost frames are framed differently from the real ones. Check this before
  reading the diff images as an FX difference.
- WARN lines in the window (`log-warn-err.txt`): frame-budget overruns, two
  `PatchedSnapshot solver unavailable` lines for the debris, the
  `AppendPointsFromTrackSections skipped 106 non-monotonic frame(s)` (#419) line,
  three MergeTree boundary discontinuities on `FXR Probe Debris` (unrecorded gaps),
  and two `recorded-relative-retired` lines for the debris outside its Relative coverage.
