# Career-ledger automated coverage - plan (rev 4)

Rev 4 (2026-08-19) adds **section 4d, Route A**: the post-fix career forge, now
that the mission library can CREDIT science and recover a vessel. It supersedes
half of section 2 finding 2 and half of Phase C's "known ceiling" (both corrected
in place rather than deleted, so a reader who saw rev 3 can see what moved), and
it gives B.4's deferred strict arming a second candidate subject that does not
depend on promoting c2. Wave 1 of Route A - the capability itself - is done;
wave 2 is DONE (`career-science-pad` built, flown as run `2026-08-19_1912`, and
harvested as `C2CareerPostFix`); wave 3's replay is BUILT and does NOT close, so
strict arming stays deferred behind three named capture-side findings.


Status: **IN PROGRESS.** **Phase A is COMPLETE** and committed on
`career-ledger-lane` (A.0's fixture + replay test + the adjudication below, A.1's
synthetic ordering pin, the A.2-A.5 parser + report-only diff facets, and A.6's
un-deferred L1 roster assertion, LIVE-PROVEN on run
`2026-08-17_2049_L1-dismiss-kerbal-career`). **Phase B is COMPLETE** on
`career-ledger-phase-b`: B.1's reading run, B.2's arming + two negative controls +
armed run (`2026-08-17_2233`), B.3's per-claim cells, and B.4's per-scenario strict
seam - which ships settable and DELIBERATELY UNARMED, with the subject that would
make it non-vacuous named in 4c. D8 `ground-truth-harness` is closed. Phases C
onward are open (C is largely obsolete - see below).

Rev 3 adds the `c2` subject (a short career played 2026-08-17 on current code,
specifically to exercise the ledger) and the candidate strategy-conversion gap it
exposed. It answers rev 2's open question 4: **Phase C's forge is no longer
needed** - c2 is the manufactured subject, made by hand.

Rev 2 supersedes rev 1 after a five-lens independent review (fact audit, premise
critique, blocker hunt, completeness, conventions). The review overturned enough
of rev 1 that the phase order changed. Section 6 lists every correction, so a reader who
saw rev 1 can diff the reasoning rather than re-read the whole thing.

## 1. Premise

The brief assumed we would find a played career and make it a ledger oracle. The
search found `c1` (323 actions, full career span, sidecars intact, schema
generation 4) and it was then ruled out as an oracle:

> "we fixed a lot of bugs since c1, that saved career is not very reliable"

That splits every use of a played career in two, and the distinction must be held
throughout:

| Use | Trustworthy on c1? | Why |
| --- | --- | --- |
| **Value oracle** - "the ledger must reproduce these pools" | **NO** | A divergence may be an artifact of the code that recorded it |
| **Shape reference** - what a real ROSTER / Tech / STRATEGY node looks like | **YES** | Node shapes are KSP's, and do not rot when we fix our bugs |
| **Mechanism corpus** - does the recalc survive 323 ordered actions | **PARTLY** | Robustness holds; any assertion on the *values* does not |

## 2. What the review changed (read this before the phases)

Three findings reshaped the plan. Each is evidenced in section 6.

1. **The highest-value task needs no new fixture at all.** Closing D8
   `ground-truth-harness` (the brief's part 1) was sequenced behind the forge.
   It does not need it: every precondition `LedgerGroundTruthHarness` declares -
   CAREER mode, live Funding/R&D/Reputation instances, no pending or uncommitted
   tree, `Scene = FLIGHT` - is already satisfied by the **committed
   `career-pad-craft` fixture**. That reading run is also the cheapest available
   answer to the strict-mode question. It moves to the front.
2. **The forge cannot settle the one live-defect lead, by construction.** The
   parked science finding is an *ordering* problem: a 90-cost spend judged
   unaffordable at a reconstructed balance of 85.3, which requires
   `ScienceEarning` rows. `KscAction` has exactly four kinds and **none credits
   science**; `ScienceEarning` is produced only from flight science subjects via
   `GameStateEventConverter.ConvertScienceSubjects`, and no committed mission
   collects science. Worse, `KscAction` pre-refuses an unaffordable research at
   the door, so a forged ledger cannot contain the unaffordable-spend shape at
   all. But `ScienceModule.ProcessSpending` is pure - the finding is reproducible
   in a **two-action synthetic unit test today**. It moves to the front too.

   **PARTLY SUPERSEDED 2026-08-19 (see section 4d).** The half of this finding
   that said "no committed mission collects science" was true when written and is
   no longer: the mission library now has `run_science_experiments` /
   `transmit_science` / `recover_vessel` and the `science_bench_recover` mission,
   so a DRIVEN run can produce `ScienceEarning` rows and a vessel-recovery credit.
   What survives untouched is the *other* half, and it is the important one: **the
   unaffordable-spend ORDERING shape is still unforgeable**, because `KscAction`
   still pre-refuses an unaffordable research at the door. A.1 settled that
   synthetically and remains the right home for it. So the capability changes what
   a forge can COVER (earning rows, a recovery, a strict-mode subject), not what it
   can PROVE about the parked ordering finding.
3. **Closing `INV8-CAREER-DIFF` cannot gate, and is not this lane's job.**
   `Inv8Ledger.cs:109` is explicit: *"ANY divergence (hard or report-only) -> WARN
   offline, never FAIL"*, naming the in-game H5 path as the FAIL-severity home.
   The analyzer's RED token counts only non-baselined FAIL and STALE, so a WARN
   can never flip it. Wiring the recalc in produces a line nobody gates on.
   **Split out of this plan** (answering rev 1's own open question 5 with a no).

The net effect: the cheapest half of the plan now delivers the brief's part 1 and
the only concrete defect lead, with **no KSP launch, no new fixture, and no new
C# for the harness lane**.


## 2b. The c2 subject (new in rev 3)

A deliberate short career (`Kerbal Space Program/saves/c2`, 1.4 MB, 39 files)
played on current code: two flights auto-recorded and merged, one contract
accepted and completed, one strategy applied, tech nodes unlocked. Measured:

- **CAREER, Parsek-native** - `parsek_career_start.sfs` present (33000 funds /
  750 sci / 0 rep custom start; all difficulty multipliers 1.0), ledger and
  recordings at generation 4.
- **68 ledger actions** on current code: 36 FundsSpending, **11 ScienceEarning
  (real flight science - the shape section 2 finding 2 proved a forge can never
  produce)**, 7 Milestone, 4 ScienceSpending, 3 FundsEarning, 1 ContractAccept +
  1 ContractComplete, 1 StrategyActivate + 1 StrategyDeactivate
  (`researchIPsellout`, commitment 0.05), 3 seeds.
- **Passes the analyzer Forbid gate as-is**: `FAIL=0 WARN=1 INFO=2 RED=0`, no
  `baseline.cfg`. This was the constraint that disqualified c1 from the harness;
  c2 clears it today. The one WARN is `INV5-ORPHAN-SIDECAR` (a third `.prec`
  from a discarded recording - decide at fixture time: keep as a benign real-file
  or drop the orphan).
- **No VESSEL nodes** (both flights recovered), so c2 trips the harvester's
  focusability gate exactly as `fresh-career` did, and cannot host a
  FLIGHT-scene in-game batch. Consequence: **c2 is the headless subject;
  `career-pad-craft` stays the in-game subject for Phase B.** For committing c2
  as a fixture, either file-construct it directly (sanctioned; the harvester is
  not the only door) or record one more pad-vessel flight into it.
- **Strategy end-state is empty** - the strategy was activated and deactivated,
  so no on-disk `STRATEGY` node exists (which is why the machine-wide search
  found none). The *ledger* now carries current-code strategy rows; the save-side
  StrategySystem shape reference still comes from the single stale sample.
- Protective snapshot: `c2-snapshot-20260817/` at the umbrella root, verified
  byte-identical (39/39 files, SHA-256).

### The finding c2 exposed (ADJUDICATED 2026-08-17 by task A.0)

Task A.0 ran the **real `RecalculationEngine`** headlessly over c2's ledger (all
nine production modules registered in `LedgerOrchestrator.Initialize`'s tier
order) and diffed the reconstruction against c2's own `persistent.sfs` pools. The
engine run is the evidence; the earlier raw seeds+rows walk was not, and two of
its three numbers did not survive.

- **Funds - hypothesis REFUTED.** The engine reproduces KSP's pool to
  `d = 0.00034` (float32 print noise). The converted funds arrive as ordinary
  stock transactions the observers record organically, so nothing is missing on
  the funds side. The raw walk's `+1946.70` was a model artifact.
- **Science - CONFIRMED live defect.** The reconstruction runs
  `+108.84171851920314` HIGH against the save (`recon 750.632` vs `save 641.790`).
  Filed as **STRATEGY-SCIENCE-CONVERSION-LEAK** in
  `docs/dev/todo-and-known-bugs.md`. **FIXED 2026-08-18 on
  `strategy-science-leak-fix`, capture-side, LIVE PROOF PENDING** - the
  `TransactionReasons.StrategyInput` forward in `GameStateRecorder.OnScienceChanged`,
  `GameStateEventConverter.ConvertStrategyExchangeScience`, and the new
  `GameActionType.StrategyScienceDebit = 32`. c2's `ledger.pgld` is FROZEN PRE-FIX
  DATA that a capture-side fix cannot retro-fill, so both C2 cells (the structural
  one and the `108.84171851920314` magnitude pin) stay GREEN and UNCHANGED; they
  flip only on a post-fix re-harvest. That todo entry is the single authority for
  the fix shape, the pending live proof, and the named residuals (only one
  direction per currency is captured; identical-amount same-UT exchanges collapse
  under the dedup key; a mid-recovery exchange costs the recovered science its
  recording attribution).
- **Reputation - small real divergence, noted not chased.** `d = -0.0036`, above
  float32 print noise at that magnitude but far below display precision. Pinned as
  a `0.01` window rather than a value; recorded as a secondary observation on the
  same entry.

**Mechanism (science).** Patents Licensing converts a share of science earnings
into funds while active. `ScienceEarning` rows carry the FULL subject award, and
nothing on the recalc path models the diverted science: `StrategiesModule.cs`
documents itself as transforming **contract rewards only**, and c2's captured
StrategyActivate row carries `sourceResource = 0, targetResource = 0`, so
`GameStateEventConverter.cs:969` may not map the science-to-funds direction even
at capture time. The reconstruction therefore runs high by exactly the converted
amount.

`C2CareerLedgerReplayTests.RealEngine_ReplaysC2Ledger_PoolsVsSave` pins the
divergence to that exact delta, so any change in the behavior (the fix included)
surfaces there and must flip the pin deliberately.

## 3. Established facts (re-verified in review)

Facts that survived independent re-derivation, and the two that did not.

**Confirmed.** The recalc *dispatch path* is headless-drivable (20 existing xUnit
files already drive `RecalculationEngine`). `Inv8Ledger` emits the
`reconstruction-not-available (headless recalc deferred)` INFO. `run.py:2400`
passes `fresh_gate=True` as a literal with no spec escape, so a fixture carrying
`analysis/baseline.cfg` or a non-baselined FAIL reds `PARSEK-FAIL(analyzer)`.
`hlib.validate_spec` hard-bans `[expectations.ledger]` alongside `InvokeRewind` /
`AnswerMergeDialog`. `CareerSaveParser` parsed exactly seven domains at rev 3
(`CareerSaveParser.cs:74-80`), with ROSTER structurally unreachable (a `GAME`
child, not a `SCENARIO`) and tech/parts sitting inside the node `ParseScience`
already opens (`:153` calls only `GetNodes("Science")`) - **superseded by A.2-A.4,
which added those three domains for a current total of ten.** Facet policy: Funds /
SciencePool / Reputation HARD; SubjectScience, Facility, Contract, Milestone
report-only; Vessel HARD only when guid-corroborated.
`StrictPerIdentityForTesting` is `internal static bool = false`
(`LedgerGroundTruthDiff.cs:37`) with no scenario path. 24 implemented seam verbs.
549 `[InGameTest]` declarations across 99 categories, 62 undriven (215
declarations, 39%). Exactly one save machine-wide carries a `STRATEGY` node.
`harvest_bdock_station.py:106` hardcodes the `(SANDBOX)` title suffix.
S4.1 and CL-3 already own both poles of load->rewind->assert, driven unattended.

**Corrected.**

- **D8 is 12 of 18 covered, not 13.** Uncovered: `milestones`, `contracts`,
  `strategies`, `tombstones`, `ers-els-routing`, `ground-truth-harness`. Two
  reviewers independently re-derived this with the harness's own
  `hlib.compute_coverage`. Rev 1 dropped `milestones`, which is plausibly the
  cheapest new cell available to this lane - `CL-2` explicitly declines to claim
  it (`CL-2-pod-impact-ledger.toml:238`).
- **The "pure recalc core" claim was overstated.** The *import statements* are
  `System*`-only, but KSP's Assembly-CSharp types live in the **global namespace**
  and need no `using`. `KerbalsModule` (the ninth module) calls
  `HighLogic.CurrentGame?.CrewRoster` (`KerbalsModule.cs:975`) and takes
  `ConfigNode` parameters (`:481`, `:1912`); `LedgerOrchestrator` carries 14 such
  references. Correct statement: **the recalc dispatch path is KSP-free; the KSP
  touches are null-guarded and off the `ProcessAction` walk.**
- **`RestoreBatchFlightBaselineAfterExecution` is 72 declarations, not 51** -
  38 of them in `Logistics` alone, so the hazard is fenceable by skipping one
  category rather than auditing 72 sites.
- **The `head-tip-split` negative does not survive.** Rev 1 claimed no c1
  recording strictly spans the rewind UT. That is true only for the two
  `CHILD_SLOT` origins of c1's single rewind point, measured on
  `explicitStartUT`/`explicitEndUT`. Measured against **the predicate the splitter
  itself uses** (`Recording.TryGetActualTrajectoryBounds`, `Recording.cs:476-509`,
  applied at `RecordingTreeSplitter.cs:539-543`), **two other c1 recordings do
  strictly span it** - `14c10c43...` (475517.1 -> 2054279.4) and `6a15ca9e...`
  (287015.8 -> 2054358.8), both backed by real samples rather than predicted tails.
  D9 `head-tip-split` is therefore **not** ruled out on c1. Revisit in Phase E.
- **Task 2.0 (rev 1's biggest unknown) resolves affirmatively.** `validate_spec`
  imposes no step-count or one-`KscAction` rule; a reviewer validated a synthetic
  4x`KscAction` spec clean with a 4-entry manifest, and `oracle.compute_expected`
  accumulated all four. A multi-action forge can be one spec.

**Still true and still binding:** `strategies` cannot be claimed - one sample
machine-wide, no data to gate on.

## 4. Phases

Ordered de-risk-first: the cheapest tasks now settle the biggest questions.

### Phase A - Settle the defect lead and the parser, headlessly

No KSP, no fixture, no harness run.

| # | Task | Cost | Gates? |
| --- | --- | --- | --- |
| A.0 **DONE 2026-08-17** | **Run the real recalc engine over c2's ledger, headlessly; diff vs c2's save pools.** Adjudicates the strategy-conversion candidate finding (section 2b) and doubles as the suite's first many-action real-ledger walk (rev 2's 3.1, now with a trustworthy subject). Fixture: commit c2's `ledger.pgld` + `persistent.sfs` (~160 KB) under `Source/Parsek.Tests/Fixtures/` (precedent: `DefaultCareer`). **Shipped as `Fixtures/C2Career/` + `C2CareerLedgerReplayTests.cs`; verdict in 2b.** | M | **yes** |
| A.1 **DONE 2026-08-17** (`ScienceSpendingOrderingTests.cs`) | **Reproduce the parked science finding synthetically.** Two actions - `ScienceEarning` at UT_e, `ScienceSpending` cost 90 at UT_s - exercising `SortActions`' earning-before-spending tiebreak (`RecalculationEngine.cs:273-285`) and `ProcessSpending`'s affordability gate (`ScienceModule.cs:271-292`), plus the c1-shaped variant where the earning's commit-anchored UT falls *after* the spend. **This settles Phase E's question without any career subject.** | S | **yes** |
| A.2 **DONE 2026-08-17** | `CareerSaveParser` -> **ROSTER** (needs a `gameNode.GetNode("ROSTER")` path; `FindScenario` cannot reach it). **Shipped as `ParseRoster` + `SaveKerbal` (name/gender/type/trait/state); nameless KERBAL skipped, empty ROSTER = facet present with zero kerbals.** | M | **yes** |
| A.3 **DONE 2026-08-17** | `CareerSaveParser` -> **tech-node unlock set** + **part purchases** (both inside the already-opened SCENARIO). **Shipped as `ParseTechTree` off the R&D node ParseScience opens: `UnlockedTechIds` + `PurchasedPartNames` (the REPEATED `part` values) + `TechNodePartCounts`; `HasTechTree` is independent of `HasScience`.** | S | **yes** |
| A.4 **DONE 2026-08-17** | `CareerSaveParser` -> **StrategySystem**, shape-only. **Report-only, no D8 claim.** **Shipped as `ParseStrategies` + `SaveStrategy`. SHAPE CORRECTION: the real stock node is `STRATEGY { name, date, factor, EFFECT{} }` with NO `isActive` field - presence in STRATEGIES IS the active signal (an explicit `isActive = False` is honoured defensively). Every committed fixture's block is empty and parses to zero strategies.** | S | no |
| A.5 **DONE 2026-08-17** | Matching `LedgerGroundTruthDiff` facets for A.2-A.4, landed **report-only**. **Shipped as `CompareRoster` / `CompareTechNodes` / `ComparePartPurchases` / `CompareStrategies`. The first two are gated on a `recon.HasXSurface` flag: with no reconstruction surface the facet logs the save-side census and stays UNCOMPARED (`FacetsCompared` untouched) rather than diffing against an invented recon. Layer B wires exactly those two surfaces - roster (KerbalsModule created / permanently-gone) and researched tech (affordable `ScienceSpending` NodeIds off the ELS); part purchases and strategies have NO recalc surface, so they are CENSUS ONLY with no compare half at all (an unreachable compare half reads like coverage it cannot provide). The tech facet emits the PHANTOM direction only (the ledger's set is delta-only, the save's is absolute, so "unlocked but unclaimed" is counted in the log, never emitted per id).** | M | report-only |
| A.6 **DONE 2026-08-17** | Un-defer the roster assertion in `L1-dismiss-kerbal-career`. **Touches a protected spec - approve or cut.** **Approved (section 7.1) and landed as the full chain: `ReportWriter` exports `hasRoster` + a sorted `roster` array (analyzerVersion 3 -> 4, additive), `oracle.diff_world_roster` evaluates `[expectations.world.roster]` `present` / `absent` name claims (both HARD; a declared block against `hasRoster=false` is one hard `missing`, so an armed claim cannot green on a missing input), `run.py` calls it where the M-B2 `roster sub-facet deferred` Verbose line stood, and the spec declares `absent = ["Bill Kerman"]` + the three bystanders. No `ARMED_ALLOWLIST` change was needed (that allowlist is the saveparse `gating = true` set); a new `WorldRosterDeclarerTests` pins the declarer set and cross-checks the absent name against the driver's own dismiss step. LIVE-PROVEN 2026-08-17 on run `2026-08-17_2049_L1-dismiss-kerbal-career` (wall 54 s, PASS): the armed sub-facet read `declared=True present=3 absent=1 divergences=0` against a produced save exporting `hasRoster=true` and four kerbals, Bill Kerman gone and the three bystanders untouched.** | S | **yes** |

c1 is used in A.2-A.4 as a **shape reference only**; asserted values are authored.

### Phase B - Close D8 `ground-truth-harness` on an existing fixture

The brief's part 1. No new fixture, no new C#.

| # | Task | Cost | Gates? |
| --- | --- | --- | --- |
| B.1 **DONE 2026-08-17** | **Reading run:** drive the `LedgerGroundTruth` category against the committed `career-pad-craft`, report-only. Confirms the category executes rather than skips, and produces the measured tally. **Shipped as `harness/scenarios/L2-ledger-groundtruth-career.toml`; LIVE-PROVEN on run `2026-08-17_2202_L2-ledger-groundtruth-career` (PASS attempt 1, 75 s, every verifier PASS/REPORT). Findings in 4b below.** | M | no (reading) |
| B.2 **DONE 2026-08-18** | **Armed run + negative control**, tally pinned from B.1. Claims D8 `ground-truth-harness`. **Shipped: `[expectations.ledger]` (empty manifest, expected == seed), `[expectations.unityExceptions] maxTotal = 0` (+ the `test_hlib` armed-allowlist row), a second required token `result: hardFailures=0 reportOnly=0 facetsCompared=7 strict=False`, and the D8 claim worded "the non-circular recalc-vs-save loop closes unattended". TWO negative controls, both red as predicted: `2026-08-17_2228` PARSEK-FAIL(ledger) on a bogus 12345 funds manifest entry (`ledger-drift facet=funds expected=512345.0 parsed=500000.0`), `2026-08-17_2231` PARSEK-FAIL(expectation) on `facetsCompared=9`. ARMED RUN `2026-08-17_2233` PASS attempt 1, 59 s, every verifier PASS/REPORT with all gates live.** | M | **yes** |
| B.3 **DONE 2026-08-18** | **Per-claim unit cell** for the claimed cell, in the style of `Cl2CoverageClaimTests`. The six L1 specs and B10 lack these; do not repeat that. **Shipped as `harness/lib/test_l2_ledger_groundtruth.py` (21 cells, 5 classes): the claim exists in the registry and is backed by BOTH required tokens; the scope fence is written as "every OTHER D8 value stays unclaimed" so a registry that grows a value needs no edit; the armed blocks are checked through `hlib.validate_ledger_expectations` / `capture_cross_check_gates`; the B.4 strict fence is pinned per-spec.** | S | **yes** |
| B.4 **DONE 2026-08-18, ARMING DEFERRED** | **Strict mode.** Made `StrictPerIdentityForTesting` settable per-scenario through a new `strict` arg on the `RunTests` seam verb (`TestCommandRunTests.TryParseStrictArg`, `TryParseIsolatedArg`'s contract verbatim; the addon assigns the static UNCONDITIONALLY before `RunBatchSelector`, so absent = default and no batch inherits a previous one's strictness, and the three AUTORUN dispatches in `TestRunnerShortcut` reset it to `false` before their own batch so the same holds for an autorun batch that never touches the seam; the hand-driven buttons are out of scope by decision - review follow-up 2026-08-18). Deliberately NOT a `SettingWhitelist` entry - every name in that table is a real player-visible `ParsekSettings` field, and a diff-strictness knob that only one in-game category reads is a test seam, not a preference. Harness side: one row in `hlib.VERB_SCOPED_CLOSED_ARGS` gives it the same pre-launch spelling gate `isolated` / `scene` / `site` have. **NO COMMITTED SPEC ARMS IT** - see the deferral below. | M | **yes** |

Wiring note: `CommittedBatchTallySourceSyncTests` discovers owners from disk and
needs **no edit** (rev 1 named an edit that does not exist). If the new spec takes
an H-series id, `IngameBatchWiringGroupTests.GROUP` needs a row **and** its
anti-vacuity floor `assertEqual(24, len(GROUP))` must be bumped.

### 4b. What B.1 measured (2026-08-17), and what it does to B.2/B.4

Run `2026-08-17_2202_L2-ledger-groundtruth-career`, PASS attempt 1, 75 s wall,
every verifier PASS or REPORT. Four things came out of it, and two of them change
the shape of the tasks that follow.

1. **THE CATEGORY HAS TWO DECLARATIONS, NOT ONE.** Rev 3 (and the B.1 brief) said
   `LedgerGroundTruth` holds exactly one `[InGameTest]`. It holds two: the harness
   cell plus `KerbalExperienceReassertTest.SurvivingCareerLogEntriesAreOnTheLiveRoster`
   (P9a), whose own XML doc says it chose the category *"deliberately - no committed
   harness spec pins that category's tally, so this cell does not move a pinned
   `BATCH_COMPLETE` number"*. That premise is now retired: the category is pinned.
   `CommittedBatchTallySourceSyncTests` caught the error locally, before any flight.
   Measured tally: `total=2 passed=1 failed=0 skipped=1 category=LedgerGroundTruth
   scene=FLIGHT`. The P9a cell skips correctly (no `KerbalExperience` rows in the
   ELS on a fixture with no recorded crewed recovery).

2. **The category EXECUTES rather than skips, and the loop closes.** The harness
   cell cleared all six run-time guards in 35.9 ms: quicksave (101,259 bytes),
   independent re-parse off disk, `RecalculateAndPatch` over the fixture's 2-action
   ledger, then `Compare: result divergences=0 hardFailures=0 reportOnly=0
   facetsCompared=7 strict=False`. Seeded pools all `delta=0`
   (funds 500000, science 100, rep 0), vessel pid sets matched. **Zero divergences
   of any kind** - so there is no triage backlog blocking B.2.

3. **B.4 STRICT MODE ADDS NO VALUE-DRIFT COVERAGE ON THIS FIXTURE.**
   `StrictPerIdentityForTesting` promotes report-only divergences to hard failures.
   `reportOnly=0`, so on `career-pad-craft` there is nothing to promote and arming it
   here buys no coverage for a value-drift regression. It would still catch a
   *recon-invents-an-identity* regression (a phantom is fixture-independent), so the
   gate is not inert - the deferral is about the thin SUBJECT. B.4 is not
   *blocked* - it is **mis-targeted**. It needs a subject with populated
   per-identity facets, which on current evidence means c2, and c2 is committed
   headless-only (decision 2) with an unanswered focusability question for harness
   promotion. Recommend B.4 be re-scoped to "make the flag settable per-scenario
   AND name the subject that makes it non-vacuous", or deferred behind that subject.

4. **The subject is thin, and B.2's D8 claim must be worded to match.** Of the 7
   compared facets only the three seeded pools are genuinely two-sided. Per-subject
   science compares 0 against 0. Facilities, roster and researched-tech compare an
   EMPTY delta-only reconstruction against a populated save (`reconFacilities=0
   saveFacilities=10`; `saveUnlocked=1 reconResearched=0
   unlockedNotClaimedByRecon=1` - the documented delta-only direction, counted not
   emitted). Contracts, milestones, recovery credits and kerbal career logs SKIP
   outright for want of a facet. What B.2 can honestly claim for D8
   `ground-truth-harness` is that **the loop runs end to end unattended and closes
   on a clean career** - not that rich-career facet accuracy is gated. One further
   caveat found while reading: `PatchReputation: module has no ReputationInitial
   seed - skipping to preserve KSP values`, so the rep facet's 0-vs-0 agreement is
   not evidence that a rep drift would be caught here.

**Two things B.1 deliberately did NOT do**, both because doing them is arming and
B.1 is reading. (a) No `[expectations.ledger]` block: the M-B2 oracle has no
report-only mode (a hard pool drift classifies `PARSEK-FAIL(ledger)` outright), and
this cell patches then restores the live pools, which is exactly the interaction
the reading run existed to observe. Measured: the patch is a no-op here
(`PatchScience: balance unchanged (current=100.0, target=100.0)`) and the produced
save came out at the seed, so B.2 can arm it - with the inert-rep-half caveat above.
(b) No `[expectations.unityExceptions]` ceiling: the block is armed-only and its
armed set is a hardcoded allowlist in `harness/lib/test_hlib.py` (then 14, now 15), so
declaring one is an allowlist edit. Measured report-only: `total=0` across all four
counted classes. That is one reading, the first half of what an armed 0 needs.

### 4c. What B.2-B.4 shipped (2026-08-18), and the one thing left open

**The arming.** L2 now carries three gates and one claim, each naming the B.1
measurement it stands on: `[expectations.ledger]` with an EMPTY manifest (expected
== seed, on B.1's measured no-op patch), `[expectations.unityExceptions] maxTotal =
0` (on B.1's measured `total=0`, plus an allowlist row in `harness/lib/test_hlib.py`
recording that this is the table's THINNEST sample - ONE reading at arming time,
n=4 now that the three arming flights have also read 0 - and why the eleven-zero
same-boot-shape family is the borrowed other half), a second required token `result:
hardFailures=0 reportOnly=0 facetsCompared=7 strict=False`, and D8
`ground-truth-harness` worded exactly as 4b's finding 4 permits.

**The negative controls, both mandatory and both red.** They are recorded here as
well as in the spec because a control that is only described in the thing it
validates is not evidence.

| Run | What was broken | Measured |
| --- | --- | --- |
| `2026-08-17_2228` | one bogus manifest entry (`funds = 12345.0`) on the empty manifest | `PARSEK-FAIL(ledger)`, `ledger-drift facet=funds expected=512345.0 parsed=500000.0`, `ledgerOracle status=FAIL hardDivergences=1` |
| `2026-08-17_2231` | `facetsCompared=7` -> `9` in the required `result:` token | `PARSEK-FAIL(expectation)`, `logContracts.required not matched: result: hardFailures=0 reportOnly=0 facetsCompared=9 strict=False` |

The armed run is `2026-08-17_2233`, PASS attempt 1, 59 s, every verifier PASS or
REPORT with all three gates live.

**The four flights ran TWO DLLs**, corrected here because an earlier draft claimed
one. B.1 (`2026-08-17_2202`) flew the **pre-B.4 DLL**: its log reads `runtests start
category=LedgerGroundTruth isolated=false` with no `strict=` token, because the seam
did not exist yet. The three arming flights (`_2228`, `_2231`, `_2233`) flew the
**B.4 DLL** - built from this branch, deployed by `provision.py --profile
stock-minimal`, grep-verified in the automation instance (`TryParseStrictArg` utf8=1,
`strict-arg-invalid` utf16=3), and confirmed on each of the three by the new
`runtests start ... isolated=false strict=false` echo. Both measured surfaces came out
IDENTICAL across the two DLLs - the `BATCH_COMPLETE v1 total=2 passed=1 failed=0
skipped=1` tally and the `result: hardFailures=0 reportOnly=0 facetsCompared=7
strict=False` line - which is itself evidence that the B.4 change did not perturb the
surface these gates measure.

**STRICT ARMING IS DEFERRED, and the subject is named.** `StrictPerIdentityForTesting`
is now settable per scenario (`RunTests strict="true"`), unit-covered end to end
headlessly, and armed by NOTHING. On current evidence the subject that would make it
non-vacuous is **c2 promoted to a harness fixture** - which has an open focusability
question (section 2b) and is today committed headless-only - **or any future career
fixture carrying recorded crewed recoveries**. `career-pad-craft` is not that
subject and cannot become one by being re-flown: B.1 measured `reportOnly=0`, so
strict there adds no coverage for a value-drift regression - there is nothing to
promote. It would still catch a recon-invents-an-identity regression, so the gate is
not inert; the deferral stands because the subject is thin. Two mechanical
fences hold the deferral: `test_hlib.test_no_committed_spec_arms_the_runtests_strict_arg`
(lane-wide) and L2's own pinned `strict=False` token (per-spec).

**A SECOND CANDIDATE SUBJECT EXISTS AS OF 2026-08-19**, and it is exactly the
"any future career fixture carrying recorded crewed recoveries" this paragraph
already names. Route A (section 4d) produces one: a driven
`science_bench_recover` flight on `career-pad-craft` earns science subjects and
recovers the craft, so the HARVESTED save carries populated per-identity facets,
which is the whole of what strict needs. Everything above stays true as written -
`career-pad-craft` ITSELF is still not the subject. What changes is that the
subject is now reachable by FLYING rather than only by promoting c2. The deferral
stands unchanged until the subject has been flown and harvested (wave 2) and its
replay shown to close (wave 3); arming before that would be arming against a
prediction.

**Tier discipline used, and reusable.** The spec shipped `tier = "operator"` for
exactly as long as its `BATCH_COMPLETE` pin was a prediction, and was promoted to
`nightly` in the same commit that pinned the measurement. `pending-fixture` would
have been a lie (the fixture is committed). An `operator` tier that outlives the
measuring run becomes an unrecorded standing human call, so the promotion is part
of the pinning commit, never a follow-up.

### 4d. Route A: the post-fix career forge (rev 4, 2026-08-19)

Rev 3 closed Phase C as obsolete and left B.4's strict arming deferred behind a
subject that was "c2 promoted to a harness fixture, which has an open
focusability question". **Route A is the alternative that does not need that
promotion: fly a career forge on the committed `career-pad-craft` fixture, on
POST-FIX code, and harvest the produced save as the strict-mode subject.** The
thing that made Route A impossible until now was section 2 finding 2's first half
(no verb credits anything), and that is what this wave lifted.

**This is a three-wave sequence, and ALL THREE ARE DONE as of 2026-08-20.** It is written as a
sequence rather than a task table because each wave's shape is decided by the
previous wave's measurement - the discipline every armed lane in this system has
used (reading run, then arming).

| Wave | What it delivers | Status |
| --- | --- | --- |
| **1. Capability** | The three actions, the six channels, `science_bench_recover`, and the headless proofs. `design-autotest-mission-library.md` Amendment A is the binding contract. **No spec, no fixture, no flight.** | **DONE 2026-08-19** (`c2-postfix-forge`) |
| **2. Forge + harvest** | A committed spec + whatever fixture work it needs, ONE driven flight, and the produced save harvested as a fixture. | **FIXTURE WORK DONE 2026-08-19** (`career-science-craft`): the "whatever fixture work it needs" clause is closed by `career-science-pad`, built by construction by `harness/tools/build_career_science_pad.py` (three additive PART nodes - a DIRECT `SurfAntenna` plus 2x `batteryPack` for the 156 EC a transmit costs - spliced into `career-pad-craft`, whose own eight parts stay byte-identical), and `L3-career-science-recover` is re-pointed at it. Zero forge flights: see the struck bullet below for why the sizing that budgeted two was wrong. Earlier: flight 1 (`postfix-career-flight`) found and fixed a career-save telemetry blocker; flight 2 flew a textbook mission and hit the STOCK RULE that an INTERNAL antenna cannot transmit science. **FLOWN AND HARVESTED 2026-08-19.** Run `2026-08-19_1912`: MISSION-OK across all nine phases (`transmit_science sent=1` against flight 2's `sent=0`), craft recovered, 2 recordings, one committed tree, analyzer red=0. The run still classified `PARSEK-FAIL(expectation)` on a forbidden `[Parsek][ERROR]` token - a REAL finding, not a flake, and not retried. The produced save is harvested as `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`. |
| **3. Replay proof** | Replay the harvested ledger headlessly (the A.0 method) and show it closes; then arm B.4 strict on it. | **CLOSES TO ZERO 2026-08-20** (`career-closes-to-zero`). The chain is COMPLETE through the proof. WAVE 3 RAN IN TWO STEPS, and the first one is why the second is trustworthy. **Step 1, 2026-08-19 (`career-science-craft`): the replay was built and it DID NOT CLOSE - which was the result.** `C2CareerPostFixReplayTests` over the fixture harvested from run `2026-08-19_1912` reconstructed the EARNED science exactly, and funds 4558 low, science 100 low, reputation 0.00148 low, each with a separately named cause (CAREER-RECOVERY-FUNDS-NOT-LEDGERED, CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE, CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW). All three were PINNED AS MAGNITUDES rather than hidden in windows, precisely so the fixes would be provable - and the pins are what made them findable: each cause was READ (two off the flight log, one off the decompiled `Reputation.addReputation_granular`) rather than guessed. **Step 2, 2026-08-20: fixed, re-flown, re-harvested, closed.** The two capture-side defects landed as PR #1498; the recalc-side reputation defect landed here; run `2026-08-19_2130_L3-career-science-recover` flew PASS on attempt 1 with zero `[Parsek][ERROR]` lines; and its save REPLACES the wave-2 fixture. KSP's own pools came out IDENTICAL across the two flights, so the runs differ in the LEDGER and nowhere else - the reconstruction moved, the thing being reconstructed did not: **funds 536558 vs 536558 (delta 0, EXACT), science 111.60000014305115 vs 111.599998 (+2.14e-06), reputation 1.9999990463256836 vs 1.99999881 (+2.36e-07)** - float32 representation gaps against pools KSP rounded into its save, six orders of magnitude below this ledger's smallest real row. The suite is rewritten from divergence-characterization into a closes-to-zero proof with TIGHT tolerances, and the spec is PROMOTED `operator` -> `nightly` with its measurements pinned. **B.4 STRICT ARMING IS NOW READY AND IS DELIBERATELY NOT TAKEN HERE** - it is wave C's call. Every condition the 2026-08-17 deferral named is met: the subject career exists, it carries recorded crewed recoveries and populated per-identity facets, and all three pools reproduce. Arming a gating surface is its own decision with its own allowlist edits, and bundling it into the commit that proves the closure would hide the arming call inside the proof. |

**What wave 2 needs, stated concretely so it is not re-derived:**

- **Fixture base: `career-pad-craft`.** CORRECTED 2026-08-19: it is the fixture's
  BASE, but no longer the fixture the spec flies - `L3-career-science-recover`
  now points at `career-science-pad`, which is this craft plus a DIRECT antenna
  and the EC to transmit through it (see the struck bullet below). Everything the
  rest of this bullet says about the craft still holds, because the eight original
  parts are carried byte-identical. It is committed, it is CAREER, it carries
  exactly one PRELAUNCH VESSEL (so it is focusable and a FLIGHT-scene batch stays
  possible), it has an inert `ParsekScenario` node, and B1/CL-1 already fly this
  exact craft. The **one open question** was whether that craft carries a science
  part at all. **ANSWERED 2026-08-19, AND IT ANSWERED CHEAPER THAN PLANNED: IT
  DOES.** The plan budgeted a flight for the answer (`science_bench_recover` names
  `no-experiments-aboard` within two polls of landing, an ASSERT-FAIL that blames
  the fixture rather than the flight) and a possible
  `build_career_pad_craft.py`-style sibling derivation if it came back negative.
  Neither was needed: READING the committed fixture shows the craft is the stock
  Jumping Flea - `mk1pod.v2`, `parachuteSingle`, `GooExperiment` x2,
  `solidBooster.sm.v2`, `basicFin` x3, Jebediah aboard - so it carries THREE
  `ModuleScienceExperiment` modules (the two Mystery Goo canisters plus the pod's
  crew report), a `ModuleScienceContainer`, a `ModuleDataTransmitter` and 50 EC.
  **The sibling-fixture bullet is struck**: nothing is built, nothing is mutated,
  and the six specs already flying `career-pad-craft` (CL-1, CL-2, CL-3, H26, L2,
  R7a) are untouched. One caveat carried into the flight instead: two Goo
  canisters plus one crew report give a small subject set on ONE biome, and
  stock's repeat-subject diminishing returns
  apply, so `transmitMinScienceGain` is sized against the NET pool rise.
  Incidentally the craft's part list is BYTE-IDENTICAL to `b1-pad-craft`'s, which
  is why B1's apoapsis window transfers verbatim.
- **Spec shape:** `kind = "autopilot"`, `mission = "science_bench_recover"`,
  steps `LoadGame -> SetSetting autoRecordOnLaunch -> mission -> CommitTree ->
  FlushAndQuit`. `harness/lib/test_run_smoke.py`'s
  `ScienceBenchRecoverAdmissionTests._make_science_bench_spec` is that spec
  already, in memory, validated against the committed schema and driven to PASS
  over the fake KSP - promote it, do not re-invent it.
  **PROMOTED 2026-08-19 as `L3-career-science-recover`, with TWO deliberate
  deviations on the STEP LIST only** (every `missionParams` value is verbatim).
  Both were derived from committed source plus an already-measured flight, not
  predicted, and both are argued in full in the spec's own comments:
  1. **`SetSetting autoMerge=true` ADDED, and mandatory.** Stock recovery destroys
     the active vessel, so `ParsekFlight.OnVesselWillDestroy` classifies
     `DestructionMode.TreeAllLeavesCheck`, finalizes the tree and stashes it
     PENDING with `activeTree` nulled. At the following FLIGHT -> SPACECENTER
     change `SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeForPendingTree`
     returns `RegularMerge` whenever autoMerge is OFF - an approval DIALOG, in an
     unattended run, that no seam verb answers. With it ON the same call returns
     `None` and the exit takes the silent full-fidelity auto-commit. This is
     CL-2's measured path on this very fixture.
  2. **`CommitTree` KEPT VERBATIM though it is EXPECTED TO FAIL.** This bullet list
     names "whether `CommitTree` is still the right verb after the scene has
     already changed" as a wave-2 reading-run question, and keeping the step is
     what MEASURES the answer rather than asserting it.
     `ParsekTestCommandAddon` returns `ERROR / no-active-tree` when `HasActiveTree`
     is false, which the recovery teardown above guarantees - the shape CL-1
     flight 1 already measured on a DESTROYED craft. Keeping it is free because
     `run.py`'s autopilot carve-out gates driver validity on the steps up to and
     including the mission handoff, and `hlib.SEAM_VERB_POST_MISSION_ROLE` files
     `CommitTree` as `recording`, so a post-mission miss is RECORDED and
     NON-GATING on a MISSION-OK run. The commit therefore has to come from the
     auto-merge path, and `recordings.count` is what proves it did.
- **Expected pins: NONE on the first run, by rule.** Every mission param is a
  FLOOR on an OBSERVED movement, never a pool value; only KSP authors what a
  subject or a recovery is worth, and authoring one here is the
  authored-vs-measured mistake `harness/fixtures/saves/README.md` records. The
  first run ships `tier = "operator"` with no `[expectations.ledger]` manifest and
  no pinned tally, exactly as L2's B.1 reading run did; the arming and the tier
  promotion land in the SAME commit that pins the measurement.
- **A THIRD FIXTURE PROPERTY NOBODY BUDGETED FOR, and flight 1 is what found it:
  a CAREER save's un-upgraded Tracking Station makes kRPC refuse the
  maneuver-node read, and that alone kills the flight leg.** Runs
  `2026-08-19_1817_L3-career-science-recover` and its retry `_1818_..._a2` both
  died at 1.2 s in PRELAUNCH - `MISSION-ASSERT-FAIL`, `flight-leg vessel-lost
  (unreadable after repeated telemetry failures)` - because
  `Maneuver node editing is not available` raises on every telemetry read and
  `READ_FAIL_STREAK_LIMIT = 3` consecutive raises escalate to a `vessel_lost`
  snapshot the delegated B1 leg correctly condemns. The finding was ALREADY IN
  THE REPO, one file away: `cl3_refly_crew_tombstone.make_control` says the same
  sentence about the same fixture family. The fix is the per-mission
  `tolerate_unreadable_nodes=True` opt-in that exists for precisely this, argued
  safe here because B1's phase progression cannot be walked from the pad (the
  CL-1 hazard the flag's docstring records) and `flightCompletedObserved`
  additionally gates on the peak apoapsis. **The transferable lesson for the next
  career lane: a `career-pad-craft`-family fixture needs BOTH opt-ins, and the
  facility tier is a fixture property worth checking alongside the part list.**
- **A FOURTH FIXTURE PROPERTY, and this one is the wave's actual blocker: the
  craft carries no antenna that stock will transmit science over.** Flight 2
  (`2026-08-19_1823` + retry `_1831_..._a2`) flew a textbook mission - peak
  apoapsis 19,990 m inside the window, landed under canopy, COLLECT ran all three
  experiments - and then ran TRANSMIT's full 120 s budget over four bounded
  re-emit sweeps for ten identical `No transmitters available to transmit the
  data` raises out of kRPC, terminal `transmit-credited-no-science`. Decompiling
  the shipped `Assembly-CSharp`, `ModuleDataTransmitter.CanTransmit()` requires
  `antennaType != INTERNAL` BEFORE it consults CommNet, and `AntennaType.INTERNAL`
  is enum 0 - so the Jumping Flea's only transmitter, `mk1pod.v2`'s built-in one,
  can never transmit science. The mission behaved correctly: this is the fixture
  fault class Amendment A describes as "the fixture is wrong, and re-flying it
  changes nothing", and the terminal's own reason names "no antenna" first.
  **The remaining wave-2 work is therefore a SIBLING fixture whose craft carries a
  DIRECT antenna.** `SurfAntenna` (Communotron 16-S) is ALREADY in this fixture's
  purchased-parts set, so no tech-tree work is needed. ~~but no committed `.craft`
  for the Jumping Flea exists anywhere (it lives only as a FLIGHTSTATE VESSEL node
  inside `b1-pad-craft`), so `build_career_pad_craft.py`'s donor-splice has no
  donor and hand-authoring a surface-attached PART node into a FLIGHTSTATE is the
  failure mode the automation-first fixture rule exists to avoid. The route is the
  FORGE precedent: build the craft by construction, add a `FORGE-*` spec that
  launches it onto the pad over a CAREER base, harvest it, register it, re-point
  this spec.~~ **BUILT 2026-08-19 AS `career-science-pad`, AND THE STRUCK SIZING
  WAS WRONG BY TWO FLIGHTS.** The two premises above are both true and neither
  implies the conclusion: "hand-authored" and "authored by a committed script with
  post-conditions and a byte-identity gate" are different things, and each of the
  three hazards the struck text names has a mechanical answer rather than a
  careful one - `persistentId`/`uid` collisions are asserted unique across the
  vessel; `srfN`/`attN` reuse the `srfAttach, 0` + `attm = 1` shape the two Mystery
  Goos on this same pod already carry, with every index range-checked; and the
  `stg` renumber is not needed at all, because the spliced parts are `istg = -1`
  and are APPENDED after the last existing part, so no existing index moves. The
  pose is derived from a measured one (the -x Goo's position/rotation pair carried
  through one rigid yaw about the pod's +Y axis), not typed. `verify` additionally
  asserts the base's eight parts are byte-identical, so the six specs flying
  `career-pad-craft` are provably untouched. The FORGE route would have bought the
  same fixture for two flights and a `.craft` author. Full account in
  CAREER-FORGE-NEEDS-A-DIRECT-ANTENNA (`todo-and-known-bugs.md`).
  **A SECOND FIXTURE FAULT WAS FOUND WHILE FIXING THE FIRST, and it would have
  cost the next flight:** the antenna alone is not enough. Stock charges
  `packetResourceCost` per `packetSize` Mits and both values come off the ANTENNA,
  so through a `SurfAntenna` (2 Mits / 12 EC) the three experiments aboard cost
  156 EC to transmit - against the 50 EC flight 2 measured as UNSPENT at
  touchdown. The fixture therefore carries two Z-100s as well (250 EC total, 94 EC
  of margin), and the arithmetic is gated rather than commented.
- **One constant to WATCH on the first flight, named in advance so it is not
  diagnosed from scratch:** `mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES` (6 frames,
  ~3 s at the ~0.5 s poll cadence) does double duty. It bounds the read-ordering
  lag between the vessel-gone read and the funds-pool read, and it bounds how
  long the career pool may be dark on that path before `career-pool-channel-dark`
  is raised. Stock recovery leaves the FLIGHT scene, so a FLIGHT -> SPACECENTER
  reload that outlasts ~3 s turns a perfectly good recovery into that flake. That
  is the CORRECT side of the retry line and costs exactly one re-fly (widening it
  toward the break-up terminal is the direction that could certify one), so it is
  deliberately NOT pre-tuned. Expect it to be the first number wave 2 has to
  bump, and bump it off the measured reload rather than a guess. The same note is
  on `[params.recoverTimeoutSeconds]` in the mission schema, which is where a
  spec author sizing the phase will be looking. **NOT YET EXERCISED (2026-08-19):**
  no flight has reached RECOVER, so this constant is still unmeasured and the
  prediction that it would be the first number to need a bump is still open.
- **Two spec-authoring notes the promoted spec already encodes**, both worth
  keeping when it is copied out of `test_run_smoke.py`. `recoverMinFundsGain` is
  a small POSITIVE value even though the schema allows 0.0 - at 0.0 the terminal
  certifies "the pools were readable across the recovery" and nothing about the
  pool having moved. And `transmitMinScienceGain` is sized against the NET pool
  rise, which is what an active science converter (Patents Licensing) and stock's
  repeat-subject diminishing returns actually leave behind; the schema comments
  carry both arguments in full.
- **One flight-shape caveat to design around:** stock recovery leaves the FLIGHT
  scene. The post-mission seam steps therefore run at SPACECENTER, where the
  bootstrap re-reads `persistent.sfs` and runs the pending-tree auto-commit (the
  `ExitToSpaceCenter` note in `SEAM_COMMAND_POLL_SECONDS_BY_VERB` records the same
  settle). That is convenient rather than hostile - the recovery itself drives the
  commit path - but the step budgets must be sized for a scene reload, and whether
  `CommitTree` is still the right verb after the scene has already changed is a
  wave-2 reading-run question, not an assumption to bake in now.
- **The strategy leg is explicitly NOT part of this flight.** L3
  (`strategy-currency-conversion`) owns that proof and is already armed and
  pinned. Adding a strategy to the forge would put two independent claims on one
  flight and make a red ambiguous.

### 4e. Wave C: arming B.4 strict (2026-08-20)

**The brief's part C, and the last open item of the lane.** `StrictPerIdentityForTesting`
shipped SETTABLE per scenario in B.4 (PR #1481) and ARMED BY NOTHING; wave 3 closed
every condition the 2026-08-17 deferral named. This section records the arming.

**THE DESIGN QUESTION, SETTLED FIRST AND WITH EVIDENCE.** The in-game
`LedgerGroundTruth` cell is `Scene = GameScenes.FLIGHT` and carries a
live/pending-tree guard, and the facet-rich state exists only after the driven
mission. Three shapes were evaluated; two lose on a guard, not on taste.

| Shape | Verdict | Why |
| --- | --- | --- |
| (a) a `RunTests strict=true` step inside `L3-career-science-recover` | **LOSES, twice** | DURING the mission `autoRecordOnLaunch` is true, so `GameStateRecorder.HasLiveRecorder()` is true and the cell Skips - green, measuring nothing. AFTER it the facets exist but the scene does not: stock recovery destroys the vessel and leaves FLIGHT, so every post-mission seam step runs at SPACECENTER (that spec's own header records the settle) and a FLIGHT-scene declaration scene-skips. |
| (b) a spec booting the flown save directly | **LOSES** | The flown save carries ZERO `VESSEL` nodes - `C2CareerPostFixReplayTests.FixtureSave_CarriesNoVessel_BecauseTheCraftWasRecovered` asserts exactly that - so `LoadGame` routes NoVesselSpaceCenter and the batch scene-skips its only declaration: the vacuity defect B10 and `L1-passive-sandbox` were re-flown to fix. |
| (c) **the flown career PLUS a spliced PRELAUNCH craft** | **CHOSEN** | It is the only remaining shape, because `LoadGame` is the only seam verb that reaches FLIGHT and it reaches it only through a save that already holds a focusable vessel. It is also a precedent rather than an invention: `build_career_pad_craft.py` exists because `fresh-career` had the identical problem. |

The fixture is `career-earned-pad`, built by
`harness/tools/build_career_earned_pad.py` from the harvested career
(`Source/Parsek.Tests/Fixtures/C2CareerPostFix/`) plus `career-science-pad`'s
vessel, and the spec is `L4-ledger-groundtruth-strict`.

**ONE FIXTURE PROPERTY IS PURE STRICT-MODE PLUMBING, and it was read off the source
rather than discovered in a flight.** The donor craft IS the craft this career flew and
RECOVERED, so its committed identity (`pid = f77e4207...`, `persistentId = 2905720181`)
is byte-identical to both recordings' `recordedVesselGuid` / `vesselPersistentId`.
`LedgerGroundTruthDiff.CompareRecovery` treats a recovery credit whose vessel is STILL
PRESENT in the save as a divergence - ALWAYS-HARD when guid-corroborated, report-only
when pid-only, and **strict promotes the report-only one anyway**. A verbatim splice
would therefore have red the armed run on a fixture artifact indistinguishable from a
product defect. Both stamps are re-stamped and the non-collision is gated. The same
reasoning narrowed the roster edit to a single `state` flip: swapping Jebediah's whole
row for the donor's (what the base builder does) would delete his `CAREER_LOG`, the SAVE
side of the `KerbalXp` facet, manufacturing a `PhantomInRecon` that strict also promotes.

**WHAT STRICT ACTUALLY IS, stated once so the reading run is interpretable.**
`LedgerDivergenceReport.HardFailures(strict)` promotes EVERY report-only divergence -
per-subject science, facilities, contracts, milestones, roster, tech, kerbal career logs,
pid-only recovery matches and phantoms alike. It is not a per-facet dial, and `reportOnly`
on a strict run is 0 BY CONSTRUCTION.

**THE THREE-RUN LEDGER** is kept at the bottom of the spec file, in the L2 form.

### Phase C - Manufacture a career subject (**largely obsolete - c2 exists; see 4d**)

Rev 2 demoted this phase to "only if B shows it is needed"; c2 (section 2b) now covers
the need directly - a hand-played career on current code beats a forged one (it
contains flight science, a real contract cycle and strategy rows, none of which
the seam can produce). What survives of this phase: the harvester title fix
(C.3) as an independent small chore, and - only if c2 is ever promoted into a
*harness* fixture - the focusability question (section 2b). The forge tasks C.1/C.2
are retained below for reference but should not be scheduled.

| # | Task | Cost | Gates? |
| --- | --- | --- | --- |
| C.1 | **Forge from `career-pad-craft`, not `fresh-career`.** `fresh-career` has `activeVessel = -1` and zero VESSEL nodes; the harvester's focusability gate `SystemExit`s on that (`harvest_bdock_station.py:232-237`). A PRELAUNCH craft also keeps a FLIGHT-scene batch possible later, and `autoRecordOnLaunch=false` keeps it recording-free and analyzer-clean. | M | **yes** |
| C.2 | **Resolve the harvester's ledger/recordings contradiction *before* designing the forge.** The ledger is at `Parsek/GameState/ledger.pgld` - inside the dir default-mode prunes - and `--keep-parsek` hard-refuses when `Parsek/Recordings` is empty, which is exactly the recording-free fixture we want. Pick one: author one recording, relax the sanity gate for a ledger-only payload, or teach the harvester a ledger-only mode. **Unbudgeted in rev 1.** | M | **yes** |
| C.3 | Harvester title fix: derive the suffix from `Mode` (`:106` hardcodes `(SANDBOX)`). | S | **yes** |
| C.4 | Pin in the correct fixture table (`RECORDED_FIXTURES` vs `EXPECTED_SCENARIO_PRESENCE` - rev 1 named the wrong one for a recording-free payload). | S | **yes** |
| C.5 | **Expected-pools table.** Every constant must be derivable from stock data or a measured curve **without reading the produced save**; anything else is recorded as "measured off run `<id>`", never as an author constant. | S | doc |

**Rationale, corrected.** Rev 1 said a known action list makes the pools "an
oracle by construction". That is false and it is the exact mistake this repo has
already paid for - the hire cost was authored at -24000 and measured at -62113,
and `harness/fixtures/saves/README.md` still carries the warning that *a ledger
which agrees with a stale spec proves nothing*. The honest rationale: **only KSP
can author the pools; the ledger is Parsek's independent claim; the diff is the
test.** Forging preserves non-circularity - KSP writes the pools at
`FlushAndQuit`, Parsek's observers write the ledger, and
`TestCommandKscAction.cs:26-28` states the verb itself never writes a ledger row.
Two independent producers.

**Known ceiling, CORRECTED 2026-08-19.** As written this said a forged career
covers funds / facility / kerbal ordering only and can *never* cover science.
Half of that is now wrong: with `science_bench_recover` a forge CAN produce
`ScienceEarning` rows and a vessel-recovery credit (section 4d). What remains
true is the narrower statement the ceiling should always have made: a forge
cannot produce the **unaffordable-spend ordering shape**, because `KscAction`
pre-refuses an unaffordable research at the door. Scope a forge to earning and
recovery coverage; do not scope it to that ordering finding, which A.1 settles
synthetically.

### Phase D - c1 re-examination (needs a trusted baseline)

| # | Task | Cost |
| --- | --- | --- |
| D.1 | Replay the headless recalc over c1; classify each divergence as era artifact, fixture artifact, or live defect. | M |
| D.2 | Re-test **D9 `head-tip-split`** against c1 using the splitter's own predicate - rev 1 wrongly ruled this out (section 3). Two recordings qualify. | S |
| D.3 | Decide whether c1 earns a place as a committed **regression floor** (its analyzer baseline matched 30/30 across an analyzerVersion 2->3 bump). | S |

**The parked finding, restated with its status.** A headless walk of c1's ledger
reproduced **funds bit-exactly** (400199.2742919922 vs 400199.27429199219) and
**reputation to float32** (45.54322814941406 vs 45.5432281); science came out
**+90.0** (119.30025362968445 vs 29.3002529). Proximate cause:
`ScienceModule.ProcessSpending` deducts only when affordable and otherwise warns
and keeps the balance. One c1 action trips it - `heavyRocketry`, cost 90,
ut=475457.99765590986, reconstructed balance 85.30028700828552; bypassing the gate
yields 29.30025291442871, float32-identical to the save.
**Status: UNCONFIRMED against current code - c1 predates many fixes. Task A.1
settles the mechanism synthetically without waiting for any of this.**

### Phase E - Corpus sweeps (optional)

Six undriven read-only categories whose assertion volume scales with store size:
`TreeIntegrity` (4), `GhostChains` (4), `TerminalOrbit` (2),
`ContinuationIntegrity` (2), `RewindSaves` (1), `CrewReservationLive` (2) - all
`Scene=ANY`, batch-executable. Needs a rich save to be non-vacuous, so it depends
on D.3.

## 5. Out of scope

- **Closing `INV8-CAREER-DIFF`** - split to its own lane (section 2 finding 3).
- **A third rewind->assert scenario** - S4.1 and CL-3 own both poles.
- **Asserting the ledger as a ledger after a rewind** - blocked three ways
  (`saveparse` has no `ledger.pgld` reader, `[expectations.ledger]` is banned with
  `InvokeRewind`, ERS/ELS counters are never serialized). This is the L4
  rewound-career oracle: a design decision, not a task.
- **The live re-fly session lane (R7b)** - never committed, abandoned after
  finding `R7-SESSION-BATCH-ISOLATION`; four items still RECORDED-not-fixed.
- Editing the six L1 specs or CL-1/CL-2, except A.6 which asks first.

## 6. Corrections from the rev-1 review

| Rev 1 said | Actually | Severity |
| --- | --- | --- |
| D8 13 of 18 covered, 5 uncovered | **12 of 18**, 6 uncovered - `milestones` omitted | major |
| Recalc core is pure, "no KSP" | Import-only claim; `KerbalsModule`/`LedgerOrchestrator` reach global-namespace KSP types | major |
| Purity guard (grep `using` lines) | **Vacuous** - would certify a false green on day one; must grep type names | major |
| Mechanism tests over synthetic ledgers | **Already covered 3x**: `LedgerStateFuzzerTests`, `FullCareerTimelineTests`, `CrossTierIntegrationTests`. Cut to the uncovered large-N case or drop | major |
| `ResetDerivedFields` "exists precisely for this" | `LedgerStateFuzzerTests.cs:52-61` is a KNOWN BLIND SPOT block - deleting it reds nothing | minor |
| Close INV8, "Gates? yes", highest leverage | **Cannot gate** - WARN by documented design; B.2 is the highest-leverage item | major |
| Phase 3 depends on Phase 2 | **False** - `career-pad-craft` satisfies every `LedgerGroundTruth` precondition | blocker |
| Harvest with `--keep-parsek` + "few or no recordings" | **Mutually exclusive** - the mode hard-refuses a recording-free save | blocker |
| Forge from `fresh-career` | **Rejected by the harvester** (`activeVessel = -1`, zero VESSEL) | blocker |
| Pools "an oracle by construction" | Circular rationale; pools come from KSP's cost curves | major |
| `head-tip-split` verified negative on c1 | **Wrong candidate set and wrong predicate** - two recordings qualify | major |
| 51 `RestoreBatchFlightBaseline` declarations | **72** (38 in `Logistics`) | major |
| Add category to `CommittedBatchTallySourceSyncTests` | That edit does not exist - discovery is from disk | major |
| No docs-per-commit accounting | CHANGELOG + todo-and-known-bugs + autotest-status required in the **same** commit | major |

**Docs-per-commit (missing from rev 1 entirely).** Every task producing a
behaviour change stages `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` in
the same commit; every task touching the autotest system also stages
`docs/dev/autotest-status.md`, the single status authority. B.1/B.2 add a
committed spec, and the harness suite reds locally the moment the spec count and
the status doc disagree.

## 7. Decisions (2026-08-17) and open questions

Answered by Vlad:

1. **A.6 approved** - the one surgical edit to `L1-dismiss-kerbal-career` may land
   once the roster parser exists. **LANDED 2026-08-17.** Scope note for whoever
   reads this next: "one surgical edit" was the SPEC edit; the assertion needed the
   whole chain behind it (analyzer export -> oracle facet -> `run.py` call site),
   because the M-B2 deferral was in the pipeline, not in the spec. The `run.py`
   change is 12 lines at the exact site whose Verbose line said "roster sub-facet
   deferred", so no new verifier row, no new verdict, and no other spec is touched.
2. **c2 is committed headless-only** (`Source/Parsek.Tests/Fixtures/C2Career/`,
   ledger + persistent + career-start). No harness promotion; `career-pad-craft`
   stays the in-game subject.
3. **`milestones` is claimed only if A.0 gates it** - per-claim unit cell required.
4. **Build order: A.0 first**, then the rest of Phase A as one PR.

Superseded question list kept for history:

1. **A.6** touches a protected spec - approve or cut.
2. **C.2** must be resolved before Phase C is designed at all.
3. **`milestones`** is newly visible as an uncovered D8 cell in this lane's subject
   area. Claim it or defer it with a stated reason.
4. ~~Does Phase C survive Phase B at all?~~ **Answered by c2 (section 2b): no forge
   needed.** Remaining sub-question: is c2 committed as a headless xUnit fixture
   only (cheap, do it), or also promoted to a harness fixture (needs the
   focusability answer)?
5. The orphan sidecar in c2: keep (benign real-world file, exercises
   INV5-ORPHAN-SIDECAR as a WARN) or drop for a maximally clean fixture?

## 8. Artifacts

- `Parsek/c1-snapshot-20260809/` - verified byte-identical copy of c1 (834/834
  files, SHA-256 matched), outside git.
- `Parsek/c2-snapshot-20260817/` - verified byte-identical copy of c2 (39/39
  files, SHA-256 matched), outside git.
- Worktree `Parsek-career-ledger-lane/`, branch `career-ledger-lane`, clean at
  `origin/main` (b83a3a2b3).
