# Wave 0910 open decisions (2026-09-11)

A read-only judgment memo written on 2026-09-11 by a Fable reviewer after the 2026-09-10
harness wave. That wave shipped four PRs, all merged: the claim-gap pass (#1670), cheap
flights and arming (#1671), the loop-render residue (#1672) and ghost-replay Tier B (#1673).
The memo weighs the calls that wave left open and gives each one a recommended answer and a
cost. It holds RECOMMENDATIONS, NOT DECISIONS: nothing in it has been ruled, and nothing in it
has been applied.

**Status: ALL DECISIONS OPEN - none applied.**

**Where the rest lives.**
- Ranked work and the decision list: `docs/dev/autotest-roadmap.md` -> "Priority register
  (2026-09-11)", items B1-B10 (decisions) and C1-C6 (work).
- Work items: each decision has ONE owning entry in `docs/dev/todo-and-known-bugs.md`, named
  in the table below.
- Status: verdicts, run ids and armed state live in `docs/dev/autotest-status.md`, never here.
- Rulings: the wave's binding rulings (G1-G11 plus three addenda) are in the operator's wave
  folder outside git (`logs/wave-0910/RULINGS.md` under the umbrella root). They are cited
  below by id.

**Provenance.** The author read the four PR worktrees, the wave records and the C# / harness
sources behind each claim, and re-derived every number from bytes or source. This copy was
re-checked against `main` at `b21fc2096` on 2026-09-11. The cheap claims were re-run:
file:line, spec lines, spec tiers, the coverage count and the fixture-Mode count. Every place
where the repo now disagrees is corrected in the text, marked [c1]..[c8], and explained under
"Corrections" at the end.

| # | Question | Recommendation | Cost | Owning todo entry |
|---|---|---|---|---|
| 1 | Register the optimizer's step-7 persistence graze as a D4 value? | Yes: D4 `persistence-graze-suppression`, claimed on LT-2 | 1 token, 2 short flights, no C# | REGISTRY-GROWTH-DECISIONS-2026-09-11 |
| 2 | Register repeat-rewind idempotence as a D9 value? | Yes: D9 `rewind-to-launch-repeat`, claimed on GS-9, control offline | 0 flights | REGISTRY-GROWTH-DECISIONS-2026-09-11 |
| 3a | What should D1 `stop-on-switch` mean? | Redefine it as `switch-backgrounds-recording` and claim it on CI-1 | 1 token, 2 short flights, no C# | D1-SUB-2-POINT-DROP-UNREACHABLE-IN-TREE-MODE |
| 3b | How does D1 `sub-2-point-drop` close? | Through a Gloops seam verb pair that also closes `manual-gloops` | ~150 lines C# + lanes | D1-SUB-2-POINT-DROP-UNREACHABLE-IN-TREE-MODE |
| 4 | What is D17 `making-history`? | Alt-site launch capture on stock-minimal, ranked last; or delete the value | 1 operator flight, or none | D17-MAKING-HISTORY-NEEDS-A-DEFINITION |
| 5 | Fix BDOCK-1's fallback merge dialog? | No fix now: a low-priority UX item that adds no UI; keep the lane's shape | 0 (optional ~36 min measurement) | BDOCK1-STATION-COMMIT-READOPT-LIMBO-FALLBACK-DIALOG |
| 6 | MC-3's tier (and GS-4 / GS-8 / GS-9, section 12)? | nightly, all four | ~1 min per night, +~20 min for the GS three | CADENCE-PROMOTIONS-2026-09-11 |
| 7a | GS-4's `unityExceptions` ceiling: 4 or 6? | 6 now, or pre-authorise the re-pin to 6 | 0 flights | GS4-UNITY-CEILING-NEGCTL-VACUOUS |
| 7b | The unity scanner cannot see stack frames | Count Parsek frames and after-quit exceptions; arm `maxParsekFrames = 0` | ~80 lines Python, 0 flights | UNITY-SCANNER-BLIND-TO-PARSEK-STACK-FRAMES |
| 8a | D3 `boundary-seam` | An in-game Optimizer cell on LT-2 | ~60-80 lines C#, 3 short flights | D3-BOUNDARY-SEAM-HAS-NO-DETERMINISTIC-WITNESS |
| 8b | D3 `relative-loop` | A synthetic injected-recordings lane | generator + preset + 1 lane, 3 flights, no product C# | D3-RELATIVE-LOOP-HAS-NO-PRODUCTION-PATH-CELL |
| 9 | Are V26M / V26T controls owed? | Yes, one in-place control each | ~60 s each | V26-CONTROLS-FLOWN-ON-B32X-COPIES |
| 10 | D14 game mode by fixture convention | Keep the convention, pin it with a test cell | ~30 lines Python | D14-GAME-MODE-CLAIMS-UNPINNED |
| 11 | D5 `staging-debris-ttl` / `-promotion` | A 2-flight stability reading first, then the promotion phase | 2 flights, then ~150 lines Python | "Autotest coverage: build-order TODOs from the basics roadmap", its R1 D5 paragraphs |

Baseline as written: main read 172 of 248 cells over 252 specs, and the four PRs were
projected to add +9 (#1670), +2 (#1671), +1 (#1673) and +0 (#1672), for 184 of 248 once
merged [c1].

## 1. D4: register the step-7 persistence graze (LT-2)?

RECOMMENDATION: add a D4 value `persistence-graze-suppression`, claimed on LT-2.

- Step 5 and step 7 of `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` are different
  predicates with different counters. `IsSurfaceGrazePattern` (`RecordingOptimizer.cs:517`)
  feeds `surfaceGrazeForward` / `surfaceGrazeBackward`. `IsGrazePattern` (`:461`, the
  `BriefSectionMaxSeconds = 120` collapse-walk) feeds `grazeForward` / `grazeBackward`
  (incremented at `:680` / `:683`). RF-1 claims `surface-graze-suppression` off
  `surfaceGrazeForward=1`. No lane gates a NONZERO step-7 count [c2].
- LT-2's `EccentricGrazing_StaysOneSegment_InGame`
  (`InGameTests/PersistenceSplitOptimizerTest.cs:166`) drives the production
  `RunOptimizationPass`. Its line `Split summary: rec=rec_persistence_smoke_grazing
  evaluated=4 grazeForward=2 grazeBackward=2 surfaceGrazeForward=0 ...` is byte-identical in
  9 of 9 logs that ran the cell.
- Cost: a registry + catalog line, one required token on LT-2, an armed re-flight and one
  negative control (`grazeForward=2` -> `=3`), about 50 s each. No C#. The registry comment
  must state the step-5 / step-7 distinction. Confidence: high.

The wave could not take this value (or section 2's) itself: ruling G5 forbade registry
growth in the wave and reported both candidates as operator decisions.

## 2. D9: register repeat-rewind idempotence (GS-9)?

RECOMMENDATION: add `rewind-to-launch-repeat`, claimed off the two backreference tokens GS-9
already requires. The first pins the same UT and the same save id across both rewinds; the
second pins the same-id `Rewind: loading save` pair (`GS-9-kerbalx-repeat-rewind.toml:233-234`).

- Precedent: `world-preservation`, `mesh-lifecycle-derender` and
  `watch-entry-distance-cutoff` were each added for a newly measured property.
- GS-9's negative control inverted `destroyLines`, not the backreference token. Discharge the
  token's control OFFLINE over `2026-09-11_0109`: mutate the second `\2` to a non-matching
  literal and expect exactly one mismatch. Fly the live control on the next GS-9 flight.
- Cost: 0 flights. Confidence: medium-high.

## 3. D1 `sub-2-point-drop` and R2 `stop-on-switch`

- `stop-on-switch`: REDEFINE it, renamed `switch-backgrounds-recording`: on a vessel switch
  the active recording transitions to background. The witness is `Transitioned to background
  (pid=` (`FlightRecorder.cs:11280`); `DecideOnVesselSwitch` (`FlightRecorder.cs:11676`) has
  no Stop decision. CI-1's logs print the token [c3], and no committed spec requires it. Claim
  it on CI-1 with the token, an armed re-flight and one negative control (the memo names
  `points=1` -> `points=99`). Confidence: high.
- `sub-2-point-drop`: the tree commit path KEEPS a 1-point recording (MC-3 measured it). The
  drop survives only in `RecordingStore.CreateRecordingFromFlightData`, reached on abort
  edges through `ParsekFlight.FallbackCommitSplitRecorder` (the `captured.Points.Count < 2`
  guard at `ParsekFlight.cs:4773`, the method at `:6843`), and in Gloops (`:17085`) [c4].
  Redefine the registry comment to name Gloops as the drop's only seam-reachable producer,
  and close it together with `manual-gloops` through ONE C# seam verb pair (`GloopsStart` /
  `GloopsStop`, about 150 lines). Fix the stale `S0.5-live-record-discard.toml:74` /
  `S0.6-live-record-commit.toml:62` comments. Confidence: medium on the verb shape.

## 4. D17 `making-history`

Making History is junctioned into BOTH automation instances, so the D17 "modded-compat only"
header does not describe it. Parsek's MH-touching paths:
- `ResolveLaunchSiteName` / `HumanizeLaunchSiteName` (`FlightRecorder.cs:6613-6652`);
- the career-only rollout distinct-site guard (`GameActions/LedgerRolloutAdoption.cs:195-209`);
- `IsKscOriginRecording` (`Logistics/RouteAnalysisEngine.cs:1194`): any named Kerbin site is
  a KSC origin.

RECOMMENDATION: define it as alt-site launch capture, on stock-minimal: `launchSiteName` is
captured and humanized from an MH site, persisted, and the ghost replays there. Add a registry
comment exempting MH from the modded-compat header. Lane: a GS-4 clone with
`launchSite = "Desert_Launch_Site"`, operator tier, ONE reading flight. A MechJeb ascent
failure from the Desert is driver-INVALID: record "mission-blocked" and stop. The marginal
value is low: rank it last, or delete the value with an honest "DLC present, no
Parsek-specific path worth a lane" comment. Confidence: medium.

## 5. BDOCK-1's fallback "Confirm: Merge to Timeline" dialog

Mechanism (run `2026-09-10_1815`):
1. The seam commit arms the DESIGNED copy-on-write committed-tree restore
   (`TryRestoreCommittedTreeForSpawnedActiveVessel`, `ParsekFlight.cs:4201`, whose match
   runs at `:15126-15209`).
2. kRPC `launch_vessel` is a FLIGHT -> FLIGHT reload that `FinalizeTreeOnSceneChangeCore`
   (`:3055`) can only stash (classified Quickload at OnLoad).
3. The guid-gated refusal is correct, and the dialog shows.
4. `SavePendingTreeIfAny` skips the Limbo tree 22 times (`ParsekScenario.cs:1907` / `:1931`),
   so the tree evaporates at quit. Committed history is untouched [c5].

What the two buttons would do:
- Discard: `DiscardPendingTree` with `pendingMatchesCommittedRestoreAttempt`
  (`RecordingStore.cs:2827`, used at `:2928` / `:2994`) purges only same-id attempt tails.
  Safe by construction.
- Merge: promotes the copy-on-write clone with cutoff-scoped events
  (`RecordingStore.cs:2479-2514`). That is the same operation the silent auto-commit performs
  on every copy-on-write host; it is unmeasured on this exact shape only.

Stock reachability: no stock path found. A player launches via the editor, and any non-FLIGHT
scene change auto-commits the clone. The shape is harness-specific.

RECOMMENDATION: not a defect worth a fix now. File a low-priority UX item with a no-new-UI
fix direction: on a refused resume of a Limbo committed-tree restore attempt, auto-clear a
no-op continuation and route a meaningful one to the silent auto-commit, so the dialog
disappears. Keep BDOCK-1's shape; do NOT add the `StopRecording` mitigation. Optional settling
measurement: a scratch BDOCK-1 copy with `AnswerMergeDialog choice=merge` (about 36 min).
Confidence: high on the mechanism and on Discard, medium on Merge.

## 6. MC-3 tier

RECOMMENDATION: nightly. MC-1 / MC-2 are already nightly on modded-compat; MC-3 costs about
1 min per night. Confidence: high. (MC-3 is still `tier = "operator"` at `b21fc2096`.)

## 7. GS-4 `maxTotal = 4`, the offline control, W1

- The offline discharge of GS-4's control: ACCEPT. The live row-6b red is proven by S1.6
  `2026-08-04_1348`: same evaluator, same bytes, a real committed spec.
- The ceiling: defensible, but not what the H23 precedent would pick. Over n=10 (GS-4 4, 1,
  2, 2, 1, 4, 0, 0 and GS-9 4, 2) the band top of 4 was hit 3 of 10 times. The legal maximum
  on the known class set is 6: STAGING 1 + MAP-FOCUS 2 + HATCH 1 + MECHJEB 1 + CAMERA 1.
  Expect a no-Parsek-frame 5 about once per 10-20 nightlies. Set 6 now per H23, or
  pre-authorise the re-pin to 6 on the first such red. The wave's supervisor ruled 4 (ruling
  R2-4), so either answer reverses a ruling and is the operator's to take.
- The real gap: `hlib.scan_unity_exceptions` (`harness/lib/hlib.py:5783`) is line-based and
  cannot see stack frames, so a Parsek-frame NRE under any ceiling is invisible. V15T's
  GHOST-MAP-ENSURE-ORBIT-RENDERERS-TEARDOWN-NRE was found by a human [c6].
  RECOMMENDATION:
  - parse the stack block after each exception line, and report `parsekFrames` and
    `afterQuit`;
  - add `maxParsekFrames` to the evaluator;
  - arm `maxParsekFrames = 0` on GS-4 and W1 (W1 then arms honestly while its `maxTotal` stays
    report-only);
  - offline-evaluate all armed lanes plus the V family first. V15T / V18T will red: fix the
    C# guard first, or carry expectedFail.
  About 80 lines of Python plus tests, 0 flights.

## 8. D3 residue

- `boundary-seam`: RF-12L's one sample is a pack-vs-hysteresis timing race, not a lane
  property (30 of 508 logs across 11 lanes), so an RF-12L stability pair would buy nothing.
  RECOMMENDATION: an in-game Optimizer cell (SPACECENTER, the LT-2 host). The cell builds a
  loaded state, calls `FlushLoadedStateForOnRailsTransitionForTesting`
  (`BackgroundRecorder.Testing.cs:406`, a wrapper over the production method), runs
  `RunOptimizationPass`, and asserts `isBoundarySeam=true` and `seamSkipped=1`. Claim the cell
  off the production lines `Persisted no-payload on-rails boundary section: ... (seam=1)` and
  `Split summary ... seamSkipped=1`. About 60-80 lines of C#, an LT-2 tally re-pin, 3 short
  flights. Confidence: high.
- `relative-loop`: production sets `LoopAnchorVesselId` only at load, from `loopAnchorPid`
  (`ParsekScenario.cs:7405`, `RecordingTreeRecordCodec.cs:669`); no recorder path produces
  it. RECOMMENDATION: a synthetic injected-recordings preset (`RecordingBuilder` plus a
  `WithLoopAnchorVesselId`) on a fixture whose active vessel is the anchor. It gates the
  engine's production lines (`GhostPlaybackLogic.WarpLoopPolicy.cs:791`;
  `GhostPlaybackEngine.cs:5238-5260`) and a placement facet. Generator + preset + one seam
  lane, 3 flights, no product C#. Confidence: medium [c7].

## 9. V26M / V26T

RECOMMENDATION: owe them; they are cheap. One in-place control per lane, inverting its own
armed renderComposition window `routeLineBuilds = { min = 2 }`
(`V26M-interbody-route-map-lines.toml:517`, `V26T-interbody-route-ts-arrival.toml:439`) to
`{ min = 3 }`, about 60 s each. Confidence: high.

## 10. D14 `sandbox` by fixture convention

RECOMMENDATION: keep the convention and pin it mechanically. 220 specs claim a game-mode cell
and 0 disagree with their fixture's `Mode =`. 2 are unresolvable: GUI-1 / GUI-2, whose
template is an operator-local fixture. Allowlist those. Add a `test_hlib` cell (fixture Mode
vs the D14 claim), about 30 lines of Python. Confidence: high. (Reproduced exactly at
`b21fc2096`.)

## 11. D5 `staging-debris-ttl` / `-promotion`

- ttl: `Debris TTL expired` appears in 6 of 553 archived logs [c8]. RECOMMENDATION: before
  authoring anything, fly a 2-flight stability reading of an uncommitted GS-7 variant with
  round 1's cut. 2 of 2 -> author GS-10 gating the TTL-closed terminals. Otherwise record "no
  deterministic flight producer, covered headlessly".
- promotion: distinct from CI-1's promotion (a TTL cancel, parent-anchored Relative exit). It
  needs a kx opt-in phase that sets kRPC `active_vessel` to a dropped booster inside 60 s
  (about 150 lines of Python). Take it after ttl. Confidence: medium.

## 12. Also from the records

- GS-4, GS-8 and GS-9 are operator tier and never run on cadence: promote all three to
  nightly (about +20 min). (Still operator tier at `b21fc2096`.)
- GHOST-MAP-ENSURE-ORBIT-RENDERERS-TEARDOWN-NRE is a genuine Parsek-frame NRE: schedule the
  small `EnsureGhostOrbitRenderers` quit / scene-cleanup guard together with the
  `maxParsekFrames` instrument.
- OPTIMIZER-INGAME-CELLS-LEAK-RECORDINGSTORE-SUPPRESSLOGGING is a two-line test-body fix;
  bundle it with the boundary-seam cell.
- Merge order: #1670 -> #1673 -> #1671 -> #1672 (docs conflicts only). DONE: all four merged
  in that order before `b21fc2096`.

## 13. Priority order (as written; item 1 is done)

1. ~~Merge the four PRs in the order above.~~ DONE.
2. Registry PR, no product C#, about 6 short flights:
   - D4 `persistence-graze-suppression` (LT-2, 2 flights);
   - D9 `rewind-to-launch-repeat` (GS-9, offline control);
   - `stop-on-switch` -> `switch-backgrounds-recording` (CI-1, 2 flights);
   - honest comments on `sub-2-point-drop` and `making-history`, and the S0.5 / S0.6 comments;
   - the fixture-Mode test cell;
   - promote MC-3 / GS-4 / GS-8 / GS-9 to nightly;
   - the two V26 renderComposition controls.
3. Harness instrument: `parsekFrames` / `afterQuit` in the unity scanner, `maxParsekFrames =
   0` on GS-4 and W1, an offline sweep of the armed lanes; GS-4's ceiling to 6, or a
   pre-authorised re-pin.
4. One C# PR: the boundary-seam Optimizer cell, the SuppressLogging restore and the
   teardown-NRE guard; an LT-2 re-pin and 3 short flights.
5. The D5 ttl stability pair, then the promotion kx phase.
6. The relative-loop synthetic lane.
7. The Gloops seam verb pair (closes `manual-gloops` and `sub-2-point-drop`).
8. Drop or defer:
   - the RF-12L stability pair;
   - the BDOCK-1 StopRecording mitigation (do not);
   - the BDOCK-1 Merge measurement;
   - the Making History flight (last, or delete the value).

The roadmap register lists the same work as C1-C6 with the decision-free items first; its
note says why the registry PR is third there.

## Corrections (re-checked against `main` at `b21fc2096`, 2026-09-11)

- [c1] Baseline. The memo was written against `98aa8b236`. All four PRs have since landed:
  `b21fc2096` reads 184 of 248 cells over 256 specs (the four PRs added MC-3, V20K, GS-9 and
  S0.12).
- [c2] Section 1 said "nothing gates the step-7 counters". That is too strong. RF-1's
  required `Split summary` token (`RF-1-continuation-stays-open.toml:406`) pins `grazeForward=0
  grazeBackward=0` literally around its surface-graze claim, so the step-7 counters ARE
  pinned, at zero. No lane gates a nonzero step-7 count, which is the line the persistence
  predicate prints when it fires. The recommendation is unchanged. The memo cited `:679-684`
  for the counters; the increments are at `:680` / `:683`, and the summary prints them at
  `:715-716`.
- [c3] Section 3 cited CI-1's armed log at line 11530. That run is not in the wave archive,
  so the line number is unverified. Checked instead: the two archived CI-1 logs
  (`logs/2026-09-08_1355_CI-1-eva-switch-bg-member` and `..._1400_...`) each carry the token
  twice, and a grep of `harness/scenarios/*.toml` finds no spec that requires it.
- [c4] Section 3 line numbers have moved. The `< 2`-point guard is `ParsekFlight.cs:4773`
  (was `:4759`). `FallbackCommitSplitRecorder` is declared at `:6843`. The Gloops
  `too short - discarded` message is at `:17085` (was `:17174`).
- [c5] Section 5 line numbers have moved:
  - `SavePendingTreeIfAny` skips at `ParsekScenario.cs:1907` / `:1931` (was `:1906` /
    `:1930`);
  - `pendingMatchesCommittedRestoreAttempt` is declared at `RecordingStore.cs:2827` and used
    at `:2928` / `:2994` (was `:2851` / `:2940`);
  - `TryRestoreCommittedTreeForSpawnedActiveVessel` is declared at `ParsekFlight.cs:4201` and
    `FinalizeTreeOnSceneChangeCore` at `:3055`. The memo's ranges `:4221-4243` and
    `:3076-3112` sit inside those methods.
- [c6] Section 7: `scan_unity_exceptions` is at `hlib.py:5783` (was `:5391`), and
  `evaluate_unity_exceptions` is at `:5813`.
- [c7] Section 8: the memo's `WarpLoopPolicy.cs:792` is the file
  `GhostPlaybackLogic.WarpLoopPolicy.cs`. Its `ShouldSpawnLoopedGhost: ... anchor pid=...
  valid` Verbose line is at `:791`.
- [c8] Section 11: the memo's 553 is a later archive count than the todo's 2026-09-10 scan
  (6 of 508, the same six hits); it is not re-derived here. The token is at
  `BackgroundRecorder.cs:1432`.

Checked and unchanged:
- source lines: `RecordingOptimizer.cs:461` / `:517`, `PersistenceSplitOptimizerTest.cs:166`,
  `FlightRecorder.cs:6613-6652` and `:11676`, `LedgerRolloutAdoption.cs:195-209`,
  `RouteAnalysisEngine.cs:1194` (under `Logistics/`), `BackgroundRecorder.Testing.cs:406`,
  `RecordingTreeRecordCodec.cs:669`, `GhostPlaybackEngine.cs:5238-5260`;
- spec lines: GS-9 `:233-234`, S0.5 `:74`, S0.6 `:62`, V26M `:517`, V26T `:439`;
- the fixture-Mode count (220 / 0 / 2);
- the operator tier of MC-3, GS-4, GS-8 and GS-9.

Not re-derived here:
- the log counts (9 of 9, 30 of 508, 6 of 553);
- the n=10 unity totals;
- MC-3's 1-point measurement;
- every flight-time and line-count estimate.
