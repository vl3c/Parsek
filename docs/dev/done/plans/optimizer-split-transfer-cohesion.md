# OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER: restore transfer cohesion at the on-rails SOI handoff so the re-aim classifier sees the whole transfer

Fix branch to stack on `dres-lane` (the finding, the `dres-orbit-recorded` fixture, and the `V9-dres-player-loop` lane all live in the unmerged `C:/Users/vlad3/Documents/Code/Parsek/Parsek-dres-lane` worktree; PR should target `dres-lane`, not `main`). Plan authored 2026-08-12 by a clean-context planning pass. Every load-bearing claim below was reproduced mechanically from the on-disk fixtures before any design was chosen: a predicate-mirror script was run over both fixtures' `.prec.txt` section metadata (Dres main `bc4a3a6d361549d2a7cdd9d4eb5574c1`, 57 sections; Eve main `75a6ab25a0f445219a82b7b841e44ba8`, 65 sections), and its output reproduces the measured V9 facts exactly (splits, member counts, and the 8/11 = 19 segment accounting). Phase 0 pins the same measurement as in-repo xUnit cells through the real code, not the mirror.

## 1. Problem statement and code-truth analysis

### What V9 measured

`V9-dres-player-loop` (two deterministic runs, 2026-08-12 `_0150`/`_0153`): the B19 Kerbin→Sun→Dres unit classifies FAITHFUL. `[ReaimDiag] member#1 segs=8 startBody=Kerbin supported=False; member#2 segs=11 startBody=Sun supported=False reason='no heliocentric (common-ancestor) leg recorded...'; gatheredSegs=19 transferMemberSegs=0`. Downstream: cadence = span (20,393,382 s, not the ~11.39M s synodic), no loiter cut of the 8.4M s LKO wait (V8 cut 11,819,849 s on the same shape), census `evaluated=0 skip.no-cross-body-successor=1`, and the Dres 5-degree tilt question — the thing V9 exists to answer — never reached.

### The discriminating fact, measured (task 1)

**Where the Dres main actually splits, and which §3 rule fires.** `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` (`Source/Parsek/RecordingOptimizer.cs:357`) walked over the 57 on-disk sections yields exactly three splittable boundaries, of which `CanAutoSplitIgnoringGhostTriggers` accepts two — matching the measured "+2" (5 recordings load as 7):

| Boundary | Sections (env/body, UT) | Rule fired | Outcome |
|---|---|---|---|
| 1→2 @ ut 31.0 | SurfaceMobile/Kerbin → Atmospheric/Kerbin | rule 5 SurfaceInvolved | splittable but **rejected**: first half 31.0 − 26.24 = 4.76 s < the 5 s floor (`RecordingOptimizer.cs:200`) |
| 3→4 @ ut 224.5 | Atmospheric/Kerbin → ExoBallistic/Kerbin | rule 7 PersistedPhaseChange | **SPLIT** (the ascent split — same class as Eve/V6M's "+1") |
| 28→29 @ ut 8,490,936.2 | **ExoPropulsive**(ref=OrbitalCheckpoint, Kerbin) → ExoBallistic(ref=OrbitalCheckpoint, Sun) | rule 3 check FAILS on the raw-env test → falls to rule 4 **BodyChange** | **SPLIT** (the defect) |
| 45→46 @ ut 20,376,838.0 | ExoBallistic/Sun → ExoBallistic/Dres | rule 3 SuppressedExoCoastBodyChange | suppressed — **the Sun→Dres boundary does NOT split** |

Member accounting confirms this is the whole story: after the two splits, member#0 = sections 0–3 (surface + atmo ascent, 0 orbit segments, hence absent from ReaimDiag but present in `[MissionPeriodicity] members=3`), member#1 = sections 4–28 (checkpoint-bearing sections 5,7,10,15,22,24,27,28 = **8 segs, startBody=Kerbin**), member#2 = sections 29–56 (29,32,35,36,39,42,45,46,48,50,52 = **11 segs, startBody=Sun**), 8+11 = **gatheredSegs=19**. Exact match to the measured log.

**Correction to the record.** The todo entry (`docs/dev/todo-and-known-bugs.md:17` on dres-lane) and the V9 spec's `[expectations.recordings]` comment both say the optimizer split "at its two body boundaries (rule 4)" producing "one member per SOI leg". Measured: only ONE body split happened (Kerbin→Sun); Sun→Dres was rule-3 suppressed; the second split is the ordinary ascent env split; member#2 holds the Sun AND Dres legs together. Phase 0 corrects both docs.

**Why rule 3 fails at 28→29 and nowhere else.** `ShouldKeepCohesiveCrossBodyExoCoast` (`RecordingOptimizer.cs:1930`) requires raw `prev.environment == ExoBallistic && next.environment == ExoBallistic`. Section 28 is env=ExoPropulsive, ref=OrbitalCheckpoint, spanning 25,921 s [8,465,014.9 → 8,490,936.2] — and its single ORBIT_SEGMENT payload is **byte-identical to its ExoBallistic predecessor section 27's**: the same Kerbin escape hyperbola (ecc 2.3601122370442775, sma −1,007,185.2465716415, startUT 8,447,964.407 = the recorded escape event), just with a longer endUT. No burn altered the conic across sections 27→28; the ExoPropulsive label on a packed/on-rails checkpoint section is recorder bookkeeping (a stock vessel cannot thrust while packed), not "engine firing at the crossing". The body flips to Sun at 8,490,936.2, the on-rails SOI transition.

**Why Eve does not split.** The Eve main's trans-Eve burn checkpoint (section 31, ExoPropulsive ref=2, 8,282 s) ends at 11,838,908 and is followed by two ExoBallistic stubs (33: 2.3 s Absolute; 34: 30 s checkpoint) **before** the body flip at 11,838,940 (→Mun) — so all four of Eve's crossings (Kerbin→Mun 34→35, Mun→Kerbin 37→38, Kerbin→Sun 40→41, Sun→Eve 54→55) present ExoBallistic|ExoBallistic and rule 3 suppresses. Eve's only accepted split is the ascent (5→6 @ ut 187.0; its roll boundary is also under the 5 s floor). The surviving member holds parking + escape + Sun coast + Eve arrival — the V8 ENGAGED shape. **The discriminator between the two fixtures is whether a ~30 s ExoBallistic checkpoint stub happened to be re-emitted between the warp-burn section and the on-rails SOI transition — recorder timing noise, not gameplay semantics.**

**Fixture-inventory sweep (blast-radius ground truth).** The same predicate mirror over every recorded fixture in `harness/fixtures/saves/` finds the Dres 28→29 boundary is the **only rule-4 body split in the entire inventory**: duna-direct-recorded (Kerbin→Sun @ 4,742,953, Sun→Duna @ 9,128,108), mun-orbit-recorded, and minmus-orbit-recorded all present ExoBallistic|ExoBallistic and suppress; the bdock/eva/gloops fixtures have no cross-body boundaries at all.

### The classifier premise, verified with real numbers

Restoring cohesion is only a fix if the cohesive chain then classifies Supported. Walking `ReaimClassifier.Classify` (`Source/Parsek/Reaim/ReaimClassifier.cs:74`) over the unsplit 19-segment chain:

- launchBody=Kerbin; helioIdx = section 29's Sun segment (startUT 8,490,936.2); parking = section 28's Kerbin segment (the escape hyperbola — the same shape class as Eve's proven ENGAGED plan, whose parking seed is also the post-burn escape stub); arrival = section 46's Dres segment (startUT 20,376,838.036), Dres a direct Sun child; no second helio leg.
- Transfer-run walk (`ReaimClassifier.cs:197-266`): lastCoast = section 45's segment (endUT 20,376,838.036 = arrival.startUT, within the 1.0 s eps). Backward walk: sections 42/45 are one conic (sma delta 2e-4 m); MCC#2 (39→42) shifts sma 0.0103% and MCC#1, the plane change onto Dres's plane (32→35, inc 0.0073°→4.658°), shifts sma 0.579% — both far under `DefaultAStepRelThreshold = 0.05`, and every chain gap (24 s, 0.12 s, 474 s, 32.6 s) is under the 3,600 s burn tolerance. The run therefore extends back to section 29 and ends at the Kerbin predecessor: `sunPredecessor=false`, so the MCC-departure decline **cannot fire**. Run duration 11,885,902 s ≈ 0.53 revolutions of the a≈24.6 Gm transfer conic (T ≈ 22.4M s) — passes the 1.5-rev gate.
- Expected plan: Supported=true, target=Dres, RecordedDepartureUT = RecordedSoiExitUT = 8,490,936.2, tof = 11,885,902 s.

So cohesion is sufficient on this fixture's actual data; Phase 0 pins this as a headless cell with a stop rule in case the in-repo arithmetic disagrees.

## 2. Candidate designs

### Design A (RECOMMENDED — direction (b), scoped): extend rule-3 cohesion to on-rails Exo-class body changes

In `ShouldKeepCohesiveCrossBodyExoCoast`, keep a body-change boundary cohesive when both sides are Exo class AND (both raw ExoBallistic — today's rule — OR **both sections are `referenceFrame == OrbitalCheckpoint`**). Rationale: a packed/on-rails SOI traversal is a coast by construction regardless of the env label (stock vessels cannot thrust while packed; the measured section-28 payload is literally the predecessor's conic re-emitted), echoing the existing `BackgroundOnRailsState` doctrine that on-rails data carries no gameplay-grade env classification. `referenceFrame` is the persisted discriminator (`TrackSection.source` is not serialized in the sidecar codecs, so it cannot carry this decision).

- **Fixes the root**, not just the classifier's view: the transfer stays one loopable member (the explicit §3 rule-3 intent — "transfer coasts render as one loopable recording"), so every split consumer is fixed at once — Reaim classify, MissionPeriodicity member count, seam census, UI member labels, per-member loop toggles, the mid-transfer playback seam at 8,490,936, rewind chain-walks.
- **Preserves the pinned contract for genuine burns.** `Persistence_BodyChange_ExoPropulsiveCrossing_Splits` (`Source/Parsek.Tests/RecordingOptimizerTests.cs:5626`) and the §3 calibration row "SOI traversal while burning → split" build their sections with `ReferenceFrame.Absolute` — a physics-frame burn across an SOI boundary still splits, byte-identical. No existing predicate cell changes.
- **Blast radius measured to zero elsewhere:** the inventory sweep shows no other fixture has any rule-4 body split, so V2/V4/V5/V8 pins, H34/H35 {21,21} (which include deterministic load-time splits), and V6M's "+1" split-mechanism measurement are untouched; Phase 1 asserts this mechanically, not by supposition. `EccentricOrbitOptimizerInvariantTests` is orthogonal (no body change in that shape). No schema field, no migration path (CLAUDE.md recording-schema rule respected). §3 ordering intact: seam short-circuit and graze suppression unchanged; only the rule-3 membership widens; reason stays `SuppressedExoCoastBodyChange`.
- Dres fixture then loads 5→6 (ascent split only) — still inside V9's `count = {min 5, max 7}` plumbing window.

### Design B (direction (b), broad): class-level rule 3 (both sides Exo class, full stop)

Simpler, but flips the committed calibration row and test #11, silently un-splitting genuine physics-frame powered SOI crossings (powered Mun captures). Changes a pinned product contract for no measured need — the narrow rule already covers every measured instance. Rejected.

### Design C (direction (a)): chain-aware classifier gather across load-time-split siblings

The topology marker exists and is sound: `CopySplitIdentityFields` (`Source/Parsek/RecordingStore.Optimization.cs:280`) gives split halves a shared `ChainId`, same `TreeId`/`RecordedVesselGuid`, chain re-indexed by StartUT, no branch point (the parent BP keeps pointing at the first half; chain linkage is the ChainId). `ApplyReaim` (`Source/Parsek/MissionLoopUnitBuilder.cs:554-615`) could classify per chain-group (same ChainId + guid, UT-ordered concatenation) instead of per member; the playtest interleaving bug that forced per-member classify would not recur because a chain group is one vessel's non-overlapping time-slices. Rejected **for this branch** because: (i) it leaves the product-visible wart in place (a member seam mid-escape, an extra member in UI/periodicity/census); (ii) downstream carries single-transfer-member assumptions (`transferMemberIndex`/`transferMemberRecordingId`, descent gating "EXACTLY on this member", per-member heliocentric substitution in `ReaimPlaybackResolver`) that a plan spanning two members would strain; (iii) two mechanisms changed at once makes the V9 re-measure unattributable. **Recorded as the defense-in-depth follow-up** (see open question 2) — it is the only direction that makes the classifier robust to split topologies in general.

### Design D (direction (c)): loop unit reassembles members before classification

A physical re-merge at unit build is the heaviest variant of C (snapshot/part-event/points re-stitch, or a shadow "stitched recording" object) with all of C's downstream questions plus lifetime ones. Rejected.

### Design E: recorder-side — stop labeling on-rails checkpoint re-emissions ExoPropulsive

Attacks the label at the source, but does nothing for already-recorded saves and fixtures (the regression floor is a recorded fixture), and the env label may be load-bearing elsewhere. Out of scope; noted as a possible hygiene follow-up.

## 3. Experiments first (Phase 0, with decision rules)

| # | Experiment | Where | Decision rule |
|---|---|---|---|
| E1 | Load BOTH fixture mains from their `.prec.txt` (precedent: the minimal fixture reader at `ReaimTransferSynthesizerTests.cs:639/959`, or the full `TrajectoryTextSidecarCodec` deserializer) and run `FindSplitCandidatesForOptimizer`; assert Dres yields exactly the two accepted candidates (env split @ 224.5, body split @ 8,490,936.2 with the boundary pair ExoPropulsive-ckpt/Kerbin → ExoBallistic-ckpt/Sun) and Eve yields exactly the ascent split; assert Sun→Dres and all four Eve crossings tally `suppressedExoCoastBodyChange` | new `Source/Parsek.Tests/OptimizerTransferCohesionTests.cs` (or a region in `RecordingOptimizerTests.cs`) | If the in-repo run disagrees with the mirror (different boundary or reason), the diagnosis is wrong — STOP and re-derive before any behavior change |
| E2 | Feed the unsplit Dres main's 19 non-predicted OrbitSegments to `ReaimClassifier.Classify` behind a stub IBodyInfo; assert Supported=true, target=Dres, DepartureUT=8,490,936.2, tof=11,885,902 ± eps | same file | If it declines (e.g. at the MCC/park gates), cohesion is NOT sufficient and the fix direction pivots to classifier work (Design C) — the optimizer change alone would be pointless |
| E3 | Synthetic predicate cell: `ExoPropulsive(ckpt, Kerbin) \| ExoBallistic(ckpt, Sun)` → today asserts SPLIT (pins pre-fix behavior; Phase 1 flips it to suppress). Sibling cell: same envs with `Absolute` frames → SPLIT, and stays SPLIT after Phase 1 | same file | Guards that Phase 1 changes exactly one boundary class |
| E4 | Fixture-inventory sweep cell: for every `harness/fixtures/saves/*/Parsek/Recordings/*.prec.txt`, record the candidate list; Phase 1 re-asserts lists unchanged everywhere except the Dres main (2→1) | same file | Any other fixture moving = unmodeled blast radius — STOP |
| E5 | Doc corrections: rewrite the todo entry's mechanism paragraph and the V9 spec's recordings-window comment to the measured account (one rule-4 split via ExoPropulsive adjacency + one ascent split; Sun→Dres suppressed) | docs only | — |

## 4. Phased implementation

- **Phase 0 — pin the measurement (tests + docs only, no behavior change).** E1–E5. Full `dotnet test` green. Test-working-dir note: xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/`, five `..` segments to repo root for the fixture paths (CLAUDE.md).
- **Phase 1 — the predicate change.** Extend `ShouldKeepCohesiveCrossBodyExoCoast` (both call sites — `FindSplitCandidates` and the §3 predicate — stay consistent by construction since they share the helper); flip E3's checkpoint cell; E4 sweep re-assert; update `docs/dev/done/plans/optimizer-persistence-split.md` §3 (rule-3 wording + a new calibration row "on-rails SOI traversal with ExoPropulsive-labeled checkpoint → keep cohesive") — the doc's step-3 line "if body changed and both sides are Exo: keep cohesive" actually moves CLOSER to its literal wording; CHANGELOG + todo in the same commit (follow-up-commit trap noted in CLAUDE.md). No reason-enum change, no log-vocabulary change.
- **Phase 2 — in-game confirmation (lean).** Deploy to the dev instance (`cd Source/Parsek && dotnet build`, then verify the deployed DLL per the CLAUDE.md recipe — long distinctive marker, both encodings), load the dres fixture save, confirm the load log reads one `SplitAtSection` (ascent only) and `suppressedExoCoastBodyChange` includes the Kerbin→Sun boundary. HARNESS TRAP if a permanent in-game cell is added instead: any `[InGameTest]` in `RecordingInvariants` (or the other five counted categories) reds `CommittedBatchTallySourceSyncTests` and `IngameCategoryInventoryDocTests` until the `BATCH_COMPLETE v1 total=N` tallies are re-pinned in the same commit. Recommendation: no new in-game category cell — the V9 lane is the in-game truth for this change.
- **Phase 3 — V9 re-measure, then arm.** Provision the automation instance (`cd harness && python provision/provision.py --profile stock-minimal`; machine-lock semantics per CLAUDE.md; verify the automation DLL, not the dev one). Fly V9 twice (the V8 determinism discipline). Expected readings: recordings 6 (window {5,7} already green); `ReaimDiag member#1 segs=19 startBody=Kerbin supported=True target=Dres`; cadence a synodic multiple, not span; a non-zero loiter cut on the 8.4M s wait; the census evaluating; and — the lane's original question — the tilt disposition at Dres's 5 degrees (`state=retained` vs decline; either reading is a result, and a decline is a NEW finding, not this defect). Then arm per the todo's standing intent: pin the recordings count at 6, the supported/target tokens, and whatever schedule/census tokens read deterministic across both runs; FORBID the two measured decline reasons. PhaseLock `UnsupportedCrossParent` stays expected (V8's ENGAGED unit reads the same — not part of the defect).
- **Phase 4 — regression sweep.** Full `dotnet test`; fly V8/V8T/V8F (must be byte-identical — Eve's fixture has no ExoPropulsive-adjacent flip, proven in E1/E4), V2/V4/V5 (Duna), V6M (its "+1" split mechanism must still read "+1"), H34/H35 (counts stay {21,21}). Any movement outside the Dres lane is stop-and-revert. ERS/ELS grep gate and the polyline/marker grep gates run in CI as usual; this change adds no raw `CommittedRecordings` reads.

Lanes summary: **re-read/re-pinned:** V9 only (plus its spec comment fix in Phase 0). **Must stay byte-identical:** V2, V4, V5, V6M, V8, V8T, V8F, H34, H35, and the full headless suite except the cells this plan adds/flips.

## 5. Risk register

- **Top risk — the premise cell (E2) declines.** Then the MCC/park gates, not the split, are the binding constraint and Design C becomes the mainline. The walk in §1 says it passes with wide margins (a-step 0.58% vs 5% threshold), so this is a guard, not an expectation.
- **A genuine physics-frame burn straddling an SOI crossing still splits** (contract preserved, deliberately) and would re-create FAITHFUL-by-blindness for such a flight. Accepted for this branch; Design C is the recorded escape hatch.
- **Already-split saves do not self-heal:** a save persisted after loading under the old predicate keeps its 7 recordings (`CanAutoMerge` will not re-merge across the boundary, and the supersede-row guard should not be weakened to force it). Fixtures on disk are unsplit, so all lanes are deterministic; player saves keep working, merely un-optimally. Note in CHANGELOG.
- **Sidecar `.prec` vs `.prec.txt` divergence in E1:** the binary codec carries the same section fields (`TrajectorySidecarBinary.cs:823` writes `isBoundarySeam`; env/ref/UTs likewise); E1 reads the text mirror for test ergonomics. If the loaders ever diverge, `FormatRoundtripTests` owns that, not this plan.
- **DLL-deploy and machine-lock traps:** per CLAUDE.md; the 2026-07-25 B12 precedent is the reason Phase 2/3 verify the deployed DLL before reading any flight.

## 6. Open questions (with recommendations)

1. **Product ruling: is "burning while packed" a thing Parsek should ever split on?** The narrow rule says no only when both sections are checkpoint-framed. Should the broad class-level rule (Design B) be adopted instead for simplicity? Recommendation: no — keep the physics-frame burn split contract; it is pinned by test #11 and the calibration table, and no measured instance needs it changed.
2. **Should Design C (chain-aware classify) land later as defense in depth?** Recommendation: yes, as a separate follow-up entry filed in the todo when this lands — it is the only structural cure for the classifier's fragility to member splits, and the topology marker (shared `ChainId` + `RecordedVesselGuid`, no branch point) is already sufficient. Not now, to keep V9's re-measure attributable to one change.
3. **Should the recorder stop emitting ExoPropulsive-labeled checkpoint re-emissions (Design E)?** Recommendation: file as hygiene follow-up only if it shows up again; it cannot help recorded saves.
4. **Arming scope for V9:** should the tilt outcome be part of the armed contract, or a separate reading? Recommendation: arm only classification/schedule/count tokens from this fix; the 5-degree tilt disposition is the lane's next measurement and gets its own arming decision (V8 Phase-3 precedent).

## 7. Recommendation and the strongest argument against it

**Recommendation: Design A** — extend rule-3 cohesion to body-change boundaries whose two sections are both Exo-class and both `OrbitalCheckpoint`-framed. It fixes the defect at its root for every consumer, is provably inert on every other committed fixture (measured, and re-asserted mechanically in Phase 1), changes no schema, no reason vocabulary, no pinned unit cell, and moves the §3 implementation closer to its own design doc's stated intent.

**Strongest argument against:** it cures this boundary class, not the classifier's structural fragility. The re-aim classifier still requires parking + heliocentric coast + arrival inside ONE member, so any other legitimate split shape — most concretely a genuine physics-frame burn straddling an SOI exit, which the preserved contract deliberately still splits — will silently reproduce FAITHFUL-with-a-misleading-reason, and nothing in this plan makes that failure loud. If the product's answer to open question 2 is "never", Design C, not A, is the honest mainline and A is only an optimization.

### Critical Files for Implementation

- `Source/Parsek/RecordingOptimizer.cs` (`IsSplittableEnvOrBodyBoundary` :357, `ShouldKeepCohesiveCrossBodyExoCoast` :1930, `FindSplitCandidatesForOptimizer` :586)
- `Source/Parsek.Tests/RecordingOptimizerTests.cs` (the §3 persistence cells, `MakePersistenceRecording` :5411 — needs a checkpoint-frame overload)
- `Source/Parsek/Reaim/ReaimClassifier.cs` (premise cell target; unchanged by Design A)
- `docs/dev/done/plans/optimizer-persistence-split.md` (§3 contract + calibration table update)
- `harness/scenarios/V9-dres-player-loop.toml` (comment correction, Phase-3 arming) plus `docs/dev/todo-and-known-bugs.md` (mechanism correction, entry closure)
