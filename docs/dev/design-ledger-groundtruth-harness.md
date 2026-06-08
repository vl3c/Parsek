# Design: In-Game Ledger Ground-Truth Verification Harness

**Status:** design (2026-06-08). Follow-up to `docs/dev/ledger-state-reconstruction-audit.md`
recommendation #3 ("In-game ground-truth harness ... the closed loop that doesn't exist today").
**Workflow:** this is a dev-facing test-infrastructure feature (no player-facing gameplay),
so steps 1-2 (vision/scenarios) are compressed; the value is in the Data Model, Behavior,
Edge Cases, Logging, and Test Plan sections below.

---

## Problem

Parsek's ledger reconstructs real KSP career scalar state (funds, science pool + per-subject,
reputation, facility levels, contracts, milestones) on every rewind / warp-exit / time-jump /
load, by walking the effective ledger (ELS) and patching the live KSP singletons. A silent
bug here corrupts an entire career save. Per the audit (§5.2):

- Pure recalc math is well covered by xUnit.
- But there is **no end-to-end test that reconstructs state and compares it to a ground-truth
  save**, and none that touches vessels.
- The one existing in-game check, `TopBarReflectsLedgerAfterRecalc`
  (`RuntimeTests.cs:15573`), is **circular**: it compares `Funding.Instance.Funds ==
  FundsModule.GetAvailableFunds()` *after* patching. The patcher just wrote the module value
  into the singleton, so it always passes. It proves the write happened, not that the
  reconstruction is correct.

The natural oracle already exists (audit §6): a real KSP save serialized at the current UT
holds the actual funds/science/rep/facilities/vessels. Nothing reads it back to verify the
recalc agrees. This harness closes that loop.

## Terminology

- **Ground-truth save (S):** a quicksave written from the live career at the current UT,
  parsed straight off disk as a `ConfigNode`, with zero involvement from the Parsek ledger.
  This is KSP's own independent serialization of career state.
- **Reconstruction:** the values produced by `LedgerOrchestrator.RecalculateAndPatch()` and
  read from the recalc modules (`LedgerOrchestrator.Funds/Science/Reputation/...`).
- **Non-circular comparison:** reconstruction-vs-S, where S was parsed independently off disk.
  Contrast with the circular module-getter-vs-live-singleton-after-patch tautology that
  `TopBarReflectsLedgerAfterRecalc` performs.
- **Seeded facet vs delta-only facet:** a facet is *seeded* if `SeedInitialResourceBalances`
  (or module init) initializes its baseline from live KSP before the ledger walk, so the
  reconstruction reflects the full career state. A facet is *delta-only* if the ledger only
  tracks changes Parsek captured, so the reconstruction reflects only the Parsek-tracked
  window (and will legitimately differ from S on a mixed-history career). This distinction
  drives the per-facet assertion strictness (see Behavior).

## Mental Model

```
   live career @ UT
        |
        |  (1) GamePersistence.SaveGame(dedicated slot)   <- writes S to disk
        v
     S.sfs  --- (2) CareerSaveParser.Parse(ConfigNode) ---> CareerSaveSnapshot   [INDEPENDENT]
        |                                                          |
        |                                                          |
        |  (3) LedgerOrchestrator.RecalculateAndPatch()            |
        v                                                          v
   recalc modules --- read accessors ---> LedgerReconstructionSnapshot
                                                          |
                          (4) LedgerGroundTruthDiff.Compare(save, recon, tol)
                                                          v
                                              LedgerDivergenceReport
                                                          |
                          (5) InGameAssert per facet  +  Warn-log each divergence
                                                          |
                          (6) restore live pools (RestoreFinancials) in finally
```

The comparison in (4) is the crux: `CareerSaveSnapshot` came from disk (KSP), the
`LedgerReconstructionSnapshot` came from the ledger modules. They are two independent
representations of the same career at the same UT. A divergence means KSP and the ledger
disagree, which is exactly the corruption class to catch.

## Architecture (two layers)

**Layer A: pure, headless-testable helpers (in `Source/Parsek/`).** No Unity scene, no live
singletons. Operate on `ConfigNode` and plain data structs. Unit-tested in
`Source/Parsek.Tests/` with synthetic `.sfs` `ConfigNode` fixtures. This is the
save-parsing/diff code the task asks to unit-test.

**Layer B: thin in-game wiring test (in `Source/Parsek/InGameTests/`).** Guards, quicksave
capture, module reads, calls Layer A, asserts, restores. Mirrors the precedent standalone
in-game test files (`ContractTombstonesAcrossSupersedeTest.cs`,
`KerbalRecoveryOnSupersedeTest.cs`).

This split keeps all parse/diff logic pure and unit-testable; the in-game layer is a small
orchestration shell that needs live KSP only for the quicksave and the module reads.

## Data Model

New file `Source/Parsek/LedgerGroundTruth.cs` (one cohesive module; the implementer may split
into 2-3 files if preferred), containing these `internal` types:

```
// ---- Parsed ground-truth save (Layer A output of parsing) ----
internal sealed class CareerSaveSnapshot
{
    bool   Parsed;                 // false if the GAME/scenario shape was unrecognizable
    string Reason;                 // why Parsed is false (for Skip messages)

    bool   HasFunds;   double Funds;
    bool   HasScience; double SciencePool;
    bool   HasRep;     double Reputation;

    Dictionary<string,double> SubjectScience;     // subjectId -> sci (cumulative earned)
    Dictionary<string,double> FacilityLevelFrac;  // "SpaceCenter/LaunchPad" -> lvl (0..1 normalized fraction)
    HashSet<string>           ActiveContractGuids; // CONTRACT state==Active
    HashSet<string>           ContractGuidsAllStates; // every CONTRACT guid (for phantom test)
    HashSet<string>           CompletedMilestoneIds;  // ProgressTracking nodes carrying `completed`
    HashSet<string>           AllMilestoneIds;        // every ProgressTracking node (phantom test)
    // CORRECTION (plan agent): the recalc milestone ids are QUALIFIED and NESTED, not flat.
    //   KspStatePatcher.PatchProgressNodeTree walks the ProgressTree building
    //   qualifiedId = pathPrefix + "/" + nodeId (top-level nodes use the bare nodeId; body
    //   subtree children use "<Body>/<Child>", e.g. "Mun/Landing"), with a bare-id fallback for
    //   body children. The parser MUST recursively walk SCENARIO[ProgressTracking] > Progress,
    //   emitting BOTH the qualified "<Body>/<Child>" id AND (for safety) the bare child id, and
    //   classify a node "completed" by presence of the `completed` field (`reached`-only nodes,
    //   e.g. RecordsDepth, are NOT completed). crew{}/vessel{} data sub-nodes are NOT milestones.
    //   The phantom test flags a recon-credited id only when it matches NEITHER form.

    List<SaveVessel> Vessels;      // FLIGHTSTATE > VESSEL
}

internal struct SaveVessel
{
    string Pid;            // VESSEL.pid (Guid string)
    uint   PersistentId;   // VESSEL.persistentId
    string Name;           // VESSEL.name
    string Type;           // VESSEL.type
    Dictionary<string,double> ResourceTotals; // resource name -> summed amount across parts
}

// ---- Reconstruction snapshot (Layer B reads modules; Layer A diff consumes it) ----
internal sealed class LedgerReconstructionSnapshot
{
    bool   HasFunds;   double Funds;          // LedgerOrchestrator.Funds.GetRunningBalance()
    bool   HasScience; double SciencePool;    // Science.GetRunningScience()
    bool   HasRep;     double Reputation;     // Reputation.GetRunningRep()
    // READER CHOICE (plan-review correction): use the RAW RUNNING values
    // (GetRunningBalance / GetRunningScience / GetRunningRep), NOT GetAvailableFunds /
    // GetAvailableScience. The save holds KSP's actual current funds/science (a running
    // balance, no future-spending reservation). For a no-cutoff RecalculateAndPatch() at the
    // live tip there are no future actions so running == available anyway, but the running
    // reader avoids the reservation/clamp-to-0/stale-projection branches in the Available
    // readers, and it is exactly the "realized running value" the production rewind read-back
    // guard compares (KspStatePatcher.cs:2568,2589). Reading the RAW module value (not the
    // post-drawdown-guard patched live value) also means a divergence the drawdown guard would
    // CLAMP/MASK in production is still DETECTED here. (Science pending-adjustment is moot: the
    // harness Skips when a pending tree exists, so running == pending-adjusted running.)

    Dictionary<string,double> SubjectScience;   // Science.GetAllSubjects() -> CreditedTotal
    Dictionary<string,int>    FacilityLevel;    // Facilities.GetAllFacilities() -> Level (int)
    HashSet<string>           ActiveContractGuids; // Contracts.GetActiveContractIds()
    HashSet<string>           CreditedMilestoneIds; // Milestones.GetCreditedMilestoneIds()

    List<RecoveryCredit> RecoveryCredits; // ledger FundsEarning + Recovery (vessel facet)
}

internal struct RecoveryCredit
{
    string RecordingId;
    string VesselName;      // from Recording.VesselName (GameAction has NO VesselName field)
    string VesselGuid;      // from Recording.RecordedVesselGuid (launch-unique; preferred correlator)
    uint   VesselPid;       // from Recording.VesselPersistentId (craft-baked, NOT launch-unique)
    double Amount;          // action.FundsAwarded
}
// CORRECTION (plan agent): a recovery action is Type=FundsEarning, FundsSource=Recovery; it carries
// RecordingId + FundsAwarded (+ DedupKey only on the paired-event path, null on the fallback path).
// GameAction has NO VesselName. Resolve vessel identity by RecordingId -> Recording.VesselName /
// .RecordedVesselGuid / .VesselPersistentId. Correlate to a SaveVessel by guid (SaveVessel.Pid is the
// VESSEL.pid Guid) preferentially; a persistentId-only match is report-only (craft-baked-pid caveat).

// ---- Diff result ----
internal enum DivergenceFacet { Funds, SciencePool, Reputation, SubjectScience,
                                Facility, Contract, Milestone, Vessel }

internal enum DivergenceKind  { ValueMismatch, PhantomInRecon, MissingInRecon, Consistency }

internal struct LedgerDivergence
{
    DivergenceFacet Facet;
    DivergenceKind  Kind;
    string Identity;          // subjectId / facilityId / contractGuid / vessel id; "" for scalars
    double ExpectedFromSave;  // NaN when N/A
    double Reconstructed;     // NaN when N/A
    string Detail;            // human-readable, grep-stable
}

internal sealed class LedgerDivergenceReport
{
    List<LedgerDivergence> All;
    // helpers: HardFailures(strict) -> the subset that must fail the test;
    //          Format() -> multi-line string for the assert message + log.
}

// ---- Tolerances ----
internal struct FacetTolerances
{
    double Funds;        // default 1.0
    double SciencePool;  // default 0.1
    double Reputation;   // default 0.1
    double Subject;      // default 0.1
    static FacetTolerances Default;
}
```

Pure entry points (Layer A, all `internal static`, no Unity scene access):

```
CareerSaveParser.Parse(ConfigNode root) -> CareerSaveSnapshot
    // Handles root-vs-GAME wrapper (mirror ValidateQuicksaveStructure:
    // if root.GetNode("FLIGHTSTATE")==null, descend into root.GetNode("GAME")).
    // Each facet is independently optional: a missing SCENARIO sets HasX=false,
    // never throws. InvariantCulture for all double parses.

LedgerGroundTruthDiff.Compare(
    CareerSaveSnapshot save,
    LedgerReconstructionSnapshot recon,
    FacetTolerances tol,
    IReadOnlyDictionary<string,int> facilityMaxLevels  // maxLevel per facility, from live KSP
) -> LedgerDivergenceReport
    // Pure. facilityMaxLevels is injected so the facility fraction<->int conversion
    // stays Unity-free and testable (maxLevel is a constant, not state).
    //
    // FACILITY LEVEL ENCODING (plan agent correction): three representations are in play.
    //   save  = normalized fraction lvl in {0, 0.5, 1.0}  (UpgradeableFacility.GetNormLevel)
    //   recon = FacilitiesModule.FacilityState.Level, 1-BASED int (1/2/3)
    //   KSP   = 0-BASED int (0/1/2)
    //   facilityMaxLevels[id] = the 0-based MAX index (=2 for a 3-tier facility).
    //   Compare in 0-based space:
    //     saveLevel0  = (int)Math.Round(lvlFraction * maxLevel0)
    //     reconLevel0 = FacilityStatePatcher.ToKspFacilityLevel(reconLevel1)   // 1-based -> 0-based
    //   A mismatch is saveLevel0 != reconLevel0. Facility node name "SpaceCenter/<X>" already
    //   equals the ledger FacilityId, so NO id remapping is needed.
```

## Behavior

### The in-game harness (Layer B)

One `[InGameTest]` method, `LedgerGroundTruthHarness.VerifyReconstructionAgainstGroundTruthSave`:

```
[InGameTest(Category = "LedgerGroundTruth", Scene = GameScenes.FLIGHT,
    Description = "NON-CIRCULAR closed loop: quicksaves the live career at the current UT, "
      + "parses that .sfs INDEPENDENTLY of the ledger, runs RecalculateAndPatch, and diffs "
      + "the reconstruction against the parsed save (funds/science/rep/per-subject/facilities/"
      + "contracts/milestones + vessel-recovery consistency). Unlike TopBarReflectsLedgerAfterRecalc "
      + "(which compares module-getter vs live-singleton AFTER patch and is therefore a tautology), "
      + "this compares recalc output to KSP's own on-disk serialization. Career-only; skips if a "
      + "live/pending tree would defer patching. Stop recording before running.",
    AllowBatchExecution = true,
    RestoreBatchFlightBaselineAfterExecution = true)]
```

Flow:

1. **Guards (Skip, mirror `TopBarReflectsLedgerAfterRecalc` + the full deferral set):**
   - `HighLogic.CurrentGame == null` -> Skip.
   - `HighLogic.CurrentGame.Mode != Game.Modes.CAREER` -> Skip.
   - `Funding.Instance == null || ResearchAndDevelopment.Instance == null ||
     Reputation.Instance == null` -> Skip.
   - `RecordingStore.HasPendingTree || GameStateRecorder.HasActiveUncommittedTree() ||
     GameStateRecorder.HasLiveRecorder()` -> Skip. (Note: `GetKspPatchDeferralReason`
     checks all THREE; the existing in-game test omits `HasLiveRecorder`. We include it,
     because a live recorder both defers the patch AND means S includes uncommitted
     in-flight state that the committed-ledger recalc legitimately will not contain. The
     comparison is only valid in a quiescent committed state.)

2. **Snapshot live pools** via `SnapshotFinancials()` (for restore in finally).

3. **Capture S:** `QuickloadResumeHelpers.TriggerQuicksave("parsek_ledger_groundtruth")`
   to a dedicated slot (NOT the player's "quicksave"). `TriggerQuicksave` runs
   `EnsureQuicksaveFileReady` internally so the file is on disk when it returns; if the
   implementer confirms it is NOT synchronous, convert the method to an `IEnumerator` and
   `yield return` a readiness wait. Default assumption: synchronous -> `void` test.

4. **Parse S independently:** `ConfigNode.Load(path)` then `CareerSaveParser.Parse(root)`.
   If `!snapshot.Parsed` -> Skip with the reason (do not fail on an unreadable save shape).

5. **Run reconstruction:** `LedgerOrchestrator.Initialize(); LedgerOrchestrator.RecalculateAndPatch();`
   Then verify the modules initialized (`LedgerOrchestrator.Funds/Science/Reputation != null`);
   if not -> Skip.

6. **Build `LedgerReconstructionSnapshot`** from the module accessors (verified to exist):
   `Funds.GetRunningBalance()`, `Science.GetRunningScience()`, `Reputation.GetRunningRep()`
   (the RAW running values, per the reader-choice note in the data model),
   `Science.GetAllSubjects()` (CreditedTotal per subject), `Facilities.GetAllFacilities()`
   (Level per facility), `Contracts.GetActiveContractIds()`, `Milestones.GetCreditedMilestoneIds()`,
   and the recovery-credit scan over `EffectiveState.ComputeELS()` for `FundsEarning` +
   `FundsEarningSource.Recovery`.

7. **Build the facility maxLevel map** from live KSP (`ScenarioUpgradeableFacilities` /
   `Upgradeables[id].MaxLevel`) so the diff can convert save fractions to int levels.

8. **Diff:** `LedgerGroundTruthDiff.Compare(save, recon, FacetTolerances.Default, maxLevels)`.

9. **Report + assert:** Warn-log the full `report.Format()` (one line per divergence with
   facet/identity/expected/recon). Then `InGameAssert` per the facet policy below. On hard
   failure, the assert message is the structured report so the developer sees every facet at
   once.

10. **Restore (finally):** `RestoreFinancials(...)` for the three pools.
    `RestoreBatchFlightBaselineAfterExecution = true` gives a full reload-restore in batch
    mode as a backstop (covers facility/contract/milestone live mutation in the rare buggy
    case). On a healthy save every patch is a no-op, so nothing beyond the pools needs
    restoring.

### Per-facet assertion policy

The pivotal question is whether each facet is **seeded** or **delta-only** (see Terminology).
The Plan agent MUST resolve this by reading `SeedInitialResourceBalances` and each module's
init: does the ledger seed per-subject science / facility levels / contract set / milestone
set from live KSP, or only the funds/science-pool/reputation scalars?

- **If a facet is seeded:** the reconstruction reflects full career state; hard-assert it
  (tolerance for scalars, exact set/value for identities).
- **If a facet is delta-only:** the reconstruction only reflects the Parsek-tracked window;
  on a mixed-history career it will legitimately differ from S, so a hard value/set assert
  would false-fail. Such facets are REPORT-ONLY in v1 (Warn-logged divergences) EXCEPT for
  the phantom rule below, which is always safe.

Default policy (assuming the common case: pool scalars seeded, per-identity delta-only;
adjust per the Plan agent's finding):

| Facet | Assertion (v1) | Rationale |
|---|---|---|
| Funds | HARD (tol 1.0) | seeded pool; the dangerous apply boundary; must match S |
| Science pool | HARD (tol 0.1) | seeded pool |
| Reputation | HARD (tol 0.1) | seeded pool (curve-applied via Set) |
| Per-subject science | REPORT-ONLY (incl. phantom) | delta-only; on a mixed-history career (Parsek installed mid-game) a tracked subject may legitimately diverge from S, and even a "phantom" (recon subjectId absent from S) can be a contract/subject KSP cleared via a path Parsek did not capture. Surface in the report; do not fail. |
| Facilities | REPORT-ONLY (incl. phantom) | delta-only; a pre-Parsek upgrade legitimately diverges |
| Contracts | REPORT-ONLY (incl. phantom) | delta-only; an untracked accept/complete/expire legitimately diverges |
| Milestones | REPORT-ONLY (incl. phantom) | delta-only; an untracked milestone legitimately diverges |
| Vessel | HARD (recovery consistency, guid-corroborated) + report-only sanity | see below |

**Plan-review correction (strictness).** The per-identity facets are REPORT-ONLY by default,
INCLUDING phantoms. Rationale: the user's real career is a mixed-history career (Parsek was
installed partway), so the delta-only modules are inherently incomplete relative to KSP's
full history. A phantom-hard default would false-fail on the first real run (e.g. a contract
Parsek tracked as accepted that KSP later expired via an uncaptured path => recon-active,
save-absent => "phantom" that is NOT corruption). Hard-failing per-identity facets is only
sound on a clean test career flown entirely under Parsek tracking.

The HARD pass/fail set is therefore: **funds, science pool, reputation** (seeded pools, the
dangerous apply boundary) **+ vessel recovery consistency when guid-corroborated**. Every
other facet (per-identity values AND phantoms, plus uncorroborated recovery and vessel parse
sanity) is REPORTED at Warn in the structured divergence report but does not fail the test.

A `StrictPerIdentityForTesting` static bool (default false) promotes ALL report-only
per-identity and phantom divergences to hard failures, for use on a clean test career.
Document this in the method and the design.

This still satisfies "assert each facet": every facet is COMPARED and emitted to the report;
the hard-fail subset is the one that cannot legitimately diverge on a real career. If a
per-identity facet is ever made seeded in the future, promote it to HARD unconditionally.

### Vessel facet (cross-subsystem consistency)

Vessels are NOT patched by the ledger; the ledger reconstructs scalars only. So the vessel
facet asserts cross-subsystem consistency, not a vessel reconstruction:

1. **Parse correctness (REPORT-ONLY, plan-review correction):** the parsed `save.Vessels` set
   is cross-checked against live `FlightGlobals.Vessels` at this UT, but FIRST excluding
   `GhostMapPresence.ghostMapVesselPids` (Parsek's transient ghost/map-presence ProtoVessels
   are present in live `FlightGlobals.Vessels` during playback but are NOT written to the save,
   so an unfiltered count/set compare would false-fail). After excluding ghosts, a residual
   mismatch is REPORTED (Warn, with the offending PIDs) rather than hard-failed, because vessel
   set membership at a single instant can differ for benign timing/transient reasons. This
   validates the parse path without risking a false career-corruption verdict.
2. **Recovery-credit consistency (HARD when guid-corroborated):** for each `RecoveryCredit` in the
   reconstruction, the recovered vessel must be ABSENT from `save.Vessels` (a recovered
   vessel is gone from the world). Correlate via the best available identity (guid preferred,
   falling back to persistentId/name; honor the craft-baked-pid caveat: a bare persistentId
   match is not proof of identity, so a pid-only match is reported, not hard-failed, unless a
   guid corroborates). If identity correlation is inconclusive, downgrade that single credit
   to a `Consistency` report entry rather than a hard failure.
3. **Deferred to a future version (logged as "v1-deferred"):** per-vessel resource
   reconstruction vs the ledger (the ledger does not track vessel resources), orbit/situation
   reconstruction, and crew-aboard reconciliation. The resource-totals parse IS implemented
   (it is the reusable, unit-tested helper) but is only diffed against live FlightGlobals for
   parse sanity in v1, not reconstructed.

## Edge Cases

| # | Scenario | Expected behavior | v1? |
|---|---|---|---|
| 1 | Not a career (sandbox/science) | Skip with mode in message | v1 |
| 2 | A pool singleton is null | Skip | v1 |
| 3 | Live recorder / active uncommitted tree / pending tree | Skip (deferral; S would include uncommitted state) | v1 |
| 4 | Quicksave file not ready when parsed | If TriggerQuicksave is synchronous, cannot happen; else IEnumerator readiness wait, then Skip if still absent | v1 |
| 5 | Save shape unrecognizable (no GAME/FLIGHTSTATE) | `Parsed=false` -> Skip with reason; never throw | v1 |
| 6 | A facet SCENARIO missing in S (e.g. no Funding node) | `HasFunds=false`; diff skips that facet (no divergence emitted); logged at Verbose | v1 |
| 7 | Modules null after RecalculateAndPatch | Skip | v1 |
| 8 | Healthy career | report empty (or only report-only per-identity entries); test PASSES | v1 |
| 9 | Corrupted pool (funds wrong) | HARD divergence; structured report in the assert message; test FAILS | v1 |
| 10 | Phantom subject/facility/contract/milestone in recon | report-only Warn (PhantomInRecon); test PASSES unless StrictPerIdentityForTesting (mixed-history careers can phantom benignly) | v1 |
| 11 | Per-identity value mismatch on a shared identity (mixed-history) | report-only Warn; test PASSES unless StrictPerIdentityForTesting | v1 |
| 12 | Recovery credit for a vessel still present in S (guid-corroborated) | HARD Consistency divergence; test FAILS | v1 |
| 13 | Recovery credit identity inconclusive (pid-only) | report-only Consistency entry, not a hard fail (craft-baked-pid caveat) | v1 |
| 14 | Comma-locale machine | InvariantCulture on every parse and format; no breakage | v1 |
| 15 | Dedicated quicksave slot collides with a player slot | Use a Parsek-prefixed slot name unlikely to collide; do not touch "quicksave" | v1 |
| 16 | Patch corrupts live facilities/contracts/milestones on a buggy save (single run, not batch) | Documented residual: only pools are restored in finally; the report tells the developer; batch mode reloads the baseline. Matches TopBar precedent. | v1 |
| 17 | Ghost/map-presence ProtoVessels in live FlightGlobals but not in save | Vessel parse-sanity excludes `GhostMapPresence.ghostMapVesselPids` before comparing; residual mismatch is report-only | v1 |
| 18 | KSP mutates live economy between the quicksave and the patch | Out of scope: the harness validates ledger-reconstruction-vs-last-serialized-save, not live-vs-save. The quicksave->patch window is sub-frame, so this is near-impossible in practice; documented for honesty. | v1 |

## What Doesn't Change

- No production code path changes. `LedgerOrchestrator`, `KspStatePatcher`, the modules, and
  the rewind pipeline are untouched. This is purely additive test infrastructure plus, at
  most, NEW `internal` read accessors on modules if a needed one is missing (the audited ones
  all exist).
- No new serialized fields, no save schema change, no `ParsekScenario` OnSave/OnLoad change.
- No new settings flag (this is a runner-invoked test, not a gated tracer; the `LedgerTrace`
  flag from audit rec #4 is a separate future feature).

## Backward Compatibility

None required. The harness reads existing module accessors and parses a standard KSP `.sfs`.
The dedicated quicksave slot is overwritten each run and is disposable.

## Diagnostic Logging

Subsystem tag: `LedgerGroundTruth`.

- Skip: `Verbose` (or Info) with the precise reason (mode / null singleton / deferral / unparseable).
- Capture: `Info` "captured ground-truth quicksave slot=... bytes=...".
- Per facet, one `Info` summary line: `funds save=X recon=Y delta=Z within tol=T`.
- Each divergence: `Warn` one line, grep-stable:
  `divergence facet=Subject kind=ValueMismatch id=... expected=... recon=... detail=...`.
- Final: `Info` "result: hardFailures=N reportOnly=M facetsCompared=K" (batch-counter
  convention: accumulate, log one summary, no per-item Info inside loops beyond the bounded
  divergence list).
- Pure helpers (`CareerSaveParser`, `LedgerGroundTruthDiff`) log decision points at Verbose
  via `ParsekLog` so the xUnit log-assertion tests can verify the parse/diff paths executed.

## Test Plan

### Unit tests (xUnit, `Source/Parsek.Tests/LedgerGroundTruthTests.cs`)

`[Collection("Sequential")]`, `ParsekLog.TestSinkForTesting` capture pattern. Fixtures are
hand-built `ConfigNode`s (`new ConfigNode("GAME")` + AddNode/AddValue) shaped like a real
`.sfs` (verified node paths: `SCENARIO[name=Funding].funds`, `SCENARIO[ResearchAndDevelopment].sci`
+ child `Science{ id, sci, cap }`, `SCENARIO[Reputation].rep`,
`SCENARIO[ScenarioUpgradeableFacilities] > SpaceCenter/X { lvl }`,
`SCENARIO[ContractSystem] > CONTRACTS > CONTRACT { guid, state }`,
`SCENARIO[ProgressTracking] > Progress > <Milestone> { completed }`,
`FLIGHTSTATE > VESSEL { pid, persistentId, name } > PART > RESOURCE { name, amount }`).

Parser tests (each states the regression it guards):
- `Parse_FundsScienceRep_ReadsScalars` - fails if a scalar key/path is misread.
- `Parse_GameWrapperAndBareRoot_BothWork` - fails if root-vs-GAME descent regresses.
- `Parse_PerSubjectScience_BuildsDict` - fails if Science{} child enumeration breaks.
- `Parse_FacilityFractions_ReadAllTen` - fails if facility node naming (`SpaceCenter/X`) breaks.
- `Parse_ActiveVsNonActiveContracts_SeparatesStates` - fails if state filtering breaks.
- `Parse_Milestones_CompletedVsReached` - fails if completed/reached distinction is lost.
- `Parse_BodySubtreeMilestones_BuildsQualifiedIds` - fails if nested body subtrees (e.g.
  `Mun/Landing`) are not walked recursively into qualified ids matching the recalc scheme.
- `Parse_VesselResourceTotals_SumsAcrossParts` - fails if per-part RESOURCE summation breaks.
- `Parse_MissingScenario_SetsHasFalseNoThrow` - fails if an absent SCENARIO throws.
- `Parse_CommaLocale_InvariantCulture` - fails if culture leaks into double parsing.

Diff tests:
- `Diff_HealthyMatch_EmptyReport` - fails if a clean save+recon emits a divergence.
- `Diff_FundsBeyondTolerance_HardFail` - fails if a real pool gap is not flagged.
- `Diff_WithinTolerance_NoDivergence` - fails if tolerance is not honored.
- `Diff_PhantomSubject_PhantomInRecon` - fails if a recon-only subject is not flagged phantom.
- `Diff_SharedSubjectMismatch_ReportOnly` - fails if a shared-identity mismatch hard-fails by default.
- `Diff_StrictMode_PromotesReportOnly` - fails if the strict flag does not promote.
- `Diff_FacilityFractionToInt_UsesMaxLevel` - fails if the maxLevel conversion is wrong.
- `Diff_RecoveryCreditWithPresentVessel_Consistency` - fails if a present-vessel recovery is not flagged.
- `Diff_RecoveryCreditPidOnly_ReportNotHard` - fails if a pid-only identity hard-fails.
- `Format_StableAndComplete` - fails if a divergence is dropped from the formatted report.

### In-game test (the harness itself)

`LedgerGroundTruthHarness.VerifyReconstructionAgainstGroundTruthSave`. It IS the end-to-end
test. Manual run recipe (document in the PR): launch KSP, load a career, enter FLIGHT, stop
recording (so no live recorder), open the runner (Ctrl+Shift+T or Settings > Diagnostics),
run the `LedgerGroundTruth` category. A healthy career passes; a corrupted pool fails with
the structured report; `parsek-test-results.txt` and `KSP.log` carry the divergence lines.

### Post-change checklist

- No serialized fields / enums / schema change -> `ParsekScenario` OnSave/OnLoad untouched
  (confirm). Generators: no new generator needed (fixtures are hand-built ConfigNodes; note
  why ScenarioWriter does not apply: it builds Parsek SCENARIO nodes, not KSP career nodes).
- `dotnet build` + `dotnet test` green.
- Update `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, and `.claude/CLAUDE.md`
  (new in-game test category `LedgerGroundTruth` + the new pure helper file).

## Open Questions (RESOLVED by the Plan agent, 2026-06-08)

Resolution summary (evidence in the plan):
1. **Seed vs delta-only:** `SeedInitialResourceBalances` (`LedgerOrchestrator.cs:1458`) seeds ONLY
   funds / science-pool / reputation. Per-subject science, facilities, contracts, milestones are
   all DELTA-ONLY (each module's dict is cleared in `Reset()` and rebuilt only from its action
   type). => facet policy: seeded pools HARD; per-identity facets REPORT-ONLY (incl. phantom,
   per the plan-review correction above; strict-promotable via `StrictPerIdentityForTesting`).
   Also confirmed: the recalc WALK populates modules before the patch-deferral gate, so module
   reads are valid regardless of deferral. Pool readers are the RAW running values
   (`GetRunningBalance` / `GetRunningScience` / `GetRunningRep`), not the Available readers.
2. **`TriggerQuicksave` is synchronous** (`EnsureQuicksaveFileReady` asserts the file is on disk
   before returning) => `void` test.
3. **Facility id == save node name** "SpaceCenter/<X>" (no remapping); level encoding corrected above.
4. **Recovery identity** comes from the Recording (no `VesselName` on `GameAction`); corrected above.
5. **maxLevel** via `ScenarioUpgradeableFacilities.protoUpgradeables[id].facilityRefs[0].MaxLevel`.
6. **Milestone ids are qualified+nested** (not flat); corrected above.

Original questions (kept for record):

1. **Seed vs delta-only per facet** (pivotal, drives strictness): does
   `SeedInitialResourceBalances` / module init seed per-subject science, facility levels,
   contract set, milestone set from live KSP, or only the pool scalars? Set each facet's v1
   strictness accordingly.
2. **`TriggerQuicksave` synchronicity:** does `EnsureQuicksaveFileReady` block until the file
   is on disk (=> `void` test), or is a frame wait needed (=> `IEnumerator`)?
3. **Facility id mapping:** what string does the ledger use for `FacilityId`, and does it
   match the save's `SpaceCenter/<Name>` node name? If not, where is the mapping?
4. **Recovery-credit identity:** what identity does a `FundsEarning + Recovery` action carry
   (DedupKey/RecordingId -> vessel guid/pid/name), and how to correlate it to a `SaveVessel`
   honoring the craft-baked-pid caveat.
5. **Live facility maxLevel accessor:** confirm the KSP API to read per-facility maxLevel
   (`ScenarioUpgradeableFacilities` / `UpgradeableFacility.MaxLevel`).
6. **Milestone id mapping:** how `Milestones.GetCreditedMilestoneIds()` ids relate to the
   `ProgressTracking > Progress` node names (for the phantom test).
```
