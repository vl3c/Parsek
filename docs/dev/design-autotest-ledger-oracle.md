# Design: Action Manifest + Ledger Oracle (Module M-B2)

Status: DRAFT (2026-07-12). Module M-B2 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, section 6 the L-track, section 4 the
expectations manifest, section 9 verifier chain slot 8, module table row M-B2).
This is the Step 3 design doc that plan section 11b mandates before any code; the
vision + gameplay scenarios are the plan itself (section 6, the Ledger Accuracy
Campaign) plus the scenario catalog block B10 and regressions R1-R7. The manifest
schema is "new data that persists", so this is the full workflow, no shortcuts.

Consumed contracts (already merged, read as authorities, never re-specified here):
the harness core `docs/dev/design-autotest-harness-core.md` (M-A5, the verifier
chain, `expectations.world` parse-and-reserve, `hlib.evaluate_expectations`, the
`ledgerOracle` result slot and the `ledger_drift` classify branch), the command
seam `docs/dev/design-autotest-command-seam.md` (M-A2), the autorun hooks
`docs/dev/design-autotest-autorun-hooks.md` (M-A3), the offline analyzer +
per-save baseline `docs/dev/design-autotest-offline-analyzer.md` /
`design-autotest-findings-baseline.md` (M-A1 + follow-on, the `.analysis.json`
producer), and the ledger ground-truth harness
`docs/dev/design-ledger-groundtruth-harness.md` (`CareerSaveParser`,
`LedgerGroundTruthDiff`, `LedgerGroundTruthHarness`). It also pins against the
game-actions authority `docs/parsek-game-actions-and-resources-recorder-design.md`
(sections 2-3 the ledger architecture; 15.1 the reputation curve; 15.6 the
non-circularity / tautology warning).

This doc pins against those PUBLIC contract surfaces (the `.analysis.json` field
set, the KSP.log line grammar, the M-A5 verifier-dict keys, the FacetTolerances
policy), never their internals, so a later Gloops file relocation does not break
it.

Plain ASCII, no em dashes, no emoji.

---

## Problem

The L-track (plan section 6) is the one part of the initiative with a COMPUTABLE
independent oracle: every stock career total (funds, science, reputation, contract
states, per-subject science) can be derived from the history of career actions and
diffed against what Parsek's ledger actually wrote into the save. Nothing in the
harness computes that oracle yet.

Two things exist but neither is the oracle. (1) The in-game `LedgerGroundTruthHarness`
(Layer B) checks that Parsek's RECALCULATION output matches KSP's on-disk save;
that is a valuable check but it is Parsek-vs-Parsek in spirit (recalc output vs the
save Parsek's patch pipeline produced), and it runs INSIDE KSP, not in the
unattended harness. (2) The M-A5 harness core parses `expectations.world` and
reserves the `ledgerOracle` verifier slot but SKIPS it ("M-B2 hook SKIPPED in v1",
harness-core verifier 8), so a scenario that declares a career expectation gets no
career verdict.

M-B2 supplies the missing piece: an ACTION MANIFEST (what career effect each
scenario/driver step causes, captured from stock emissions at event time), a pure
Python ORACLE that turns an accumulated manifest into expected career totals, and
the harness VERIFIER (chain slot 8) that diffs those expected totals against the
produced save and reds `PARSEK-FAIL(ledger)` on drift, naming the UT window. It
also ACTIVATES the reserved `expectations.world` block (vessel resource totals)
that M-A5 left parse-only.

The whole point is INDEPENDENCE. The oracle must never reach for a Parsek-computed
number, or the L-track certifies the ledger against itself and goes confidently
green while wrong. Section 6 of the plan pins two correctness rules that this
design treats as load-bearing invariants (see Terminology and Behavior), and
game-actions design 15.6 warns of exactly the circularity to avoid.

v1 scope is deliberately narrow: activate the oracle for the B10 passive-safety
scenario (a fresh career, stock actions only, ZERO expected career deltas) so the
cold-boot-load + in-scene passivity regressions become automated. The COMMITTED
driver spec is `LoadGame` / `SetSetting` / `RunTests` / `FlushAndQuit` - a cold-boot
`LoadGame` (the genuine R2/BUG-F cold-load-wipe guard) plus an in-scene test batch
that must leave the economy untouched (no-op passivity). There is NO warp or
scene-change verb in v1 (`TimeJump` is RESERVED; no scene-change verb exists), so
R1's warp/scene-change economy-drift trigger surface is NOT yet automated here; it
arrives with the `TimeJump`/scene verbs (Deferred Items). The B10 manifest is EMPTY;
the oracle proves the produced save's career totals still equal the fixture's seed
baseline within tolerance, and that no unexpected stock award fired. L1
single-module action scripts (nonzero deltas) are the follow-on (M-B3), not v1.

## Terminology

- **Action manifest**: the machine-readable list of career effects a run causes.
  Each entry is `{ut, kind, deltas{funds,science,reputation}, subjectIds,
  contractGuid, provenance, sourceLine}`. The plan's "machine-readable ACTION
  MANIFEST" (section 6 Spine).
- **Manifest entry provenance**: how the entry's amounts were obtained. v1 has two:
  `seam-declared` (a driver step annotates the career effect it is about to cause,
  L1 scripts) and `stock-log-captured` (the harness greps an enumerated set of
  stable stock KSP.log lines for awards that fired). A third, `gameevents-captured`
  (an in-game GameEvents subscriber writes an event-time manifest line), is
  RESERVED for M-B3 where in-game action driving lands.
- **Seed baseline**: the fixture's starting career totals. Acquired by running the
  offline analyzer subprocess over the STAGED template save copy BEFORE launch and
  reading the seed from ITS `careerSave` block, so ONE parser (the analyzer's
  `CareerSaveParser` path) produces BOTH legs - the seed and the produced-save
  totals - and a parser drift can never desync them. The pre-launch analysis output
  is redirected OUT of the save tree (`PARSEK_ANALYZER_RESULTS` pointed at the
  harness results dir, NEVER inside the save KSP will boot) and deleted before boot,
  so no analyzer artifact rides into the launched save. The captured seed is a
  pre-recalc snapshot of the stock currency values (an independence-approved source).
  The oracle's expected total is `seed + accumulated manifest deltas`.
- **Oracle**: the pure Python function (`harness/lib/oracle.py`) that turns an
  accumulated manifest plus a seed baseline into expected career totals, applying
  the tolerance policy, the reputation-curve exception, and the Rec-3 carve-out.
- **Ledger-oracle verifier**: the harness-core verifier-chain slot 8. Post-run it
  reads the produced save's parsed career totals from the analyzer's
  `.analysis.json` `careerSave` block, computes expected totals via the oracle,
  diffs them, and returns a verifier result feeding `hlib.classify_verdict`'s
  existing `ledger_drift -> PARSEK-FAIL(ledger)` branch.
- **INDEPENDENCE OF AMOUNTS** (plan section 6, BINDING): manifest amounts come from
  stock emissions AT EVENT TIME (stock log lines, GameEvents callbacks, pre-recalc
  snapshots of the stock currency singletons), NEVER by calling Parsek's
  recalculation code. Reading Parsek's DERIVED running totals (ERS/ELS/recalc
  output) into the oracle would make the L-track circular (game-actions design
  15.6). Documented exception: the reputation curve (below).
- **Reputation-curve exception** (plan section 6, game-actions 15.1): reputation is
  nonlinear. KSP's `AddReputation` applies a gain/loss curve, so rep deltas do NOT
  sum linearly. The oracle asserts reputation via `SetReputation` semantics: the
  expected rep is the curve-aware accumulation of event-time captured deltas, not a
  linear sum. The curve is RESOLVED, not open: `ReputationModule.ApplyReputationCurve`
  mirrors the decompiled stock keyframes (`docs/dev/done/game-actions/game-actions-spike-findings.md (note: the ReputationModule.cs:22 comment cites a stale plans/ path)`,
  Spike A), replicating Unity's Hermite tangent evaluation between keyframes. The
  entry schema carries a per-provenance `rep_mode` discriminator: seam-declared
  values are `nominal` (pre-curve, the curve is applied by the oracle); future
  `gameevents-captured` old/new deltas are `applied` (post-curve, added with no
  further curve). v1 (B10) has zero rep deltas, so this degenerates to `expected =
  seed`.
- **RATIFIED-RESIDUAL CARVE-OUT** (plan section 6, logistics 10.6, [Rec-3
  residual], BINDING): on a plain (non-rewind) discard, route funds and physical
  route cargo persist BY DESIGN. A rollback oracle must consult this carve-out
  before declaring drift: whitelisted free-standing route rows are NOT expected to
  roll back on a non-rewind discard, and the oracle asserts the `[Rec-3 residual]`
  diagnostic line instead of a rollback-to-zero. v1 (B10, no routes) never exercises
  it; it is specified here so L3 does not need a format break.
- **Facet policy**: the hard-vs-report-only split the oracle diff MIRRORS from
  `LedgerGroundTruthDiff` (funds / science pool / reputation = HARD; per-subject
  science, facilities, contracts, milestones = REPORT-ONLY). "Mirrors", not
  "calls": the C# diff compares recalc-vs-save; the oracle compares
  manifest-vs-save, so it reuses the POLICY, not the code (independence). Only the
  SPLIT transfers: the `LedgerGroundTruthDiff` `UpliftClampedExpected` carve-out (a
  Layer-B drawdown-guard concept that pardons a seeded pool whose recalc value sits
  ABOVE the live save because Parsek's guard clamps the patch DOWN to live) does NOT
  transfer to manifest-vs-save semantics: the manifest oracle asserts the
  stock-granted amount against the save directly and has NO uplift-clamp exemption.

## Mental Model

Two independent legs cross-check the ledger, and their agreement closes the loop:

```
  STAGED fixture save  --analyzer subprocess (pre-launch, one parser)-->  seed baseline
        |                                                      |
        v                                                      |
   run the scenario (M-A5 driver: boot, actions, warp, quit)   |
        |                                                      |
        |  leg A: manifest capture                             |
        |    seam-declared entries (spec)                      |
        |    + stock-log-captured entries (grep KSP.log)       |
        |          => accumulated manifest                     |
        |                                                      v
        |                                        oracle.compute_expected(
        |                                            seed, manifest, tol, carveouts)
        |                                                => EXPECTED totals
        |  leg B: produced-save parse                          |
        v                                                      |
   analyze-recordings.ps1 (verifier 3, ALREADY RUNS)           |
     -> <save>.analysis.json  +  additive "careerSave" block   |
        (CareerSaveParser output, ALREADY parsed offline)      |
                       |                                        |
                       v                                        v
                   PARSED totals   ===  diff (facet policy)  === EXPECTED totals
                                        |
                       hard-facet drift -> PARSEK-FAIL(ledger), UT window named
                       report-only drift -> logged, not red
                       no drift + empty-manifest cross-check -> PASS
```

Three invariants shape the design:

- **The oracle never reads a Parsek-computed number.** Leg A (manifest) is
  event-time stock emissions + author-declared constants + the fixture seed. Leg B
  (produced save) is KSP's own serialization, parsed by `CareerSaveParser` with
  zero ledger involvement (the same independence `LedgerGroundTruthHarness` relies
  on, game-actions 15.6). The oracle diffs A against B. Neither leg is Parsek's
  recalc output, so the amount Parsek's ledger granted is CONSERVED across the two
  legs: an over- or under-credit that lands in the save (leg B) has no matching entry
  in the independent manifest (leg A) and reds. RESIDUAL BLIND SPOT: a stock award
  MAGNITUDE is itself a function of Parsek-PATCHED state (per-subject science
  diminishing returns, current reputation level feeding the rep curve), so if
  upstream state is corrupted the SAME distortion appears in a `stock-log-captured`
  leg-A amount AND in the leg-B save - a pure magnitude corruption can hide from a
  capture-vs-save diff. The catch for that class is the SEAM-DECLARED author-constant
  leg (an amount fixed by mission design, independent of live state) plus Layer B
  (`LedgerGroundTruthHarness`, recalc-vs-save). Consequently author constants are
  REQUIRED (fill-from-capture FORBIDDEN) for any facet whose stock magnitude is
  state-dependent - per-subject science and reputation - and every captured amount
  must be a DELTA: a stock line reporting a post-grant BALANCE is inadmissible as a
  manifest amount.

- **Leg A and leg B cross-check.** For B10 the manifest is empty, so `expected =
  seed`. If a stock award fired that the capture enumeration MISSED, leg B (the
  produced save) still shows the changed total, so the diff against the empty-manifest
  expected still reds. A capture-enumeration gap cannot produce a false PASS; at
  worst it produces a caught drift whose UT window points at the un-enumerated
  award. This is why a conservative capture set is safe for the zero-delta flagship.

- **The verifier is additive and export-only.** The produced-save parse REUSES the
  analyzer path that verifier 3 already runs; M-B2 only ADDS a `careerSave` block to
  the analyzer's `.analysis.json` (the analyzer already parses the save into
  `model.CareerSave`; it just does not export it). No new KSP subprocess, no new
  in-game Parsek behavior, no new save data. The one non-export C# tweak is a small,
  tested loader / INV8 gating change so the block is populated on non-funds career
  saves (named in What Doesn't Change).

## Data Model

Four artifacts. The manifest, the accumulated-manifest result, and the `careerSave`
export block are "new data that persists" per plan section 11b, so all get
round-trip / determinism tests.

### Manifest entry (spec-declared + runtime-captured)

Spec-side, a scenario declares its expected career effects in an ordered array
under a NEW `[expectations.ledger]` block (a sibling of the reserved
`expectations.world`; both activate with M-B2). Empty for B10.

```toml
[expectations.ledger]
# The oracle runs iff this block is present. Absent => ledgerOracle SKIPPED (v1
# default for non-career scenarios), exactly as harness-core verifier 8 reserves.
seedFrom = "template"          # "template" = parse the staged fixture save for the
                               #   seed baseline (the only v1 value). A literal
                               #   {funds,science,reputation} seed is RESERVED.
tolerances = "default"         # "default" = FacetTolerances.Default mirror
                               #   (funds 1.0, science 0.1, rep 0.1, subject 0.1).
rec3CarveOut = false           # true only for a non-rewind-discard route scenario
                               #   (L3); v1 B10 leaves it false.

# Declared career effects (seam-declared provenance). EMPTY for B10 passive safety.
# Each entry is the career effect a driver step is about to cause; the amount is an
# author constant from the mission design (independent of Parsek recalc) OR, for a
# state-INDEPENDENT facet only, left null to be filled from the stock-log capture
# (see Behavior). State-dependent facets (per-subject science, reputation) MUST use
# an author constant; a null there is a scenario-authoring defect.
# [[expectations.ledger.manifest]]
#   ut        = 0.0            # UT the effect fires (sort key; NEVER a prune bound)
#   kind      = "contract-complete"   # see the kind enum below
#   funds     = 50000.0        # delta on the funds pool (0/absent if none)
#   science   = 0.0            # delta on the science pool
#   reputation= 0.0            # rep delta (curve-aware; see the exception)
#   subjectIds= []             # per-subject science ids this entry credits
#   contractGuid = ""          # contract identity for the contract facet
#   provenance = "seam-declared"
```

`kind` enum (open, additive): `contract-accept`, `contract-complete`,
`contract-fail`, `science-transmit`, `science-recover`, `milestone`,
`facility-upgrade`, `facility-refund`, `tech-unlock`, `kerbal-hire`,
`kerbal-dismiss`, `strategy-activate`, `strategy-convert`, `vessel-recovery`,
`vessel-build-cost`, `route-delivery`. v1 validates the enum but B10 declares no
entries.

### Accumulated manifest: `harness/results/<runId>.manifest.json`

The harness writes the ACCUMULATED manifest (spec-declared entries + stock-log-captured
entries) as a per-run artifact for the oracle and for audit. Deterministic key
order, floats via `repr`, sorted by `(ut, sequence)`.

```json
{
  "schema": 1,
  "runId": "2026-07-12_1830_B10-career-passive-safety",
  "seed": { "funds": 25000.0, "science": 0.0, "reputation": 0.0,
            "hasFunds": true, "hasScience": true, "hasRep": true },
  "entries": [],
  "capturedRaw": []
}
```

`entries` is the accumulated manifest the oracle consumes; `capturedRaw` records
every stock-log line the capture matched (before dedupe) so a maintainer can audit
what fired. For B10 both are empty.

### `careerSave` export block (additive `.analysis.json` field)

The analyzer ALREADY parses the produced save into `model.CareerSave` (a
`CareerSaveSnapshot`, via `CareerSaveParser.Parse` inside `SaveDirectoryLoader`).
M-B2 adds a `careerSave` block to `ReportWriter.BuildJson` (test-side, in
`Source/Parsek.Tests/Analyzer/`) that serializes THAT already-parsed snapshot. This
is the smallest export seam: no new parse, no new KSP behavior, no new subprocess,
and the ledger-oracle verifier reads the SAME `.analysis.json` verifier 3 already
produces.

WRITER CONTRACT: the `careerSave` block is ALWAYS emitted whenever the analyzer ran,
populated with `parsed` + the per-facet `hasFunds`/`hasScience`/`hasRep` flags, so
its ABSENCE from an `.analysis.json` is unambiguous - it means an OLD or BROKEN
analyzer, which the ledger-oracle verifier treats as INVALID(tooling) (edge 13),
never facet-absence. Facet-absence (Sandbox / Science) is signalled INSIDE the block
by the `hasX` flags, not by omitting the block. For a career-but-non-funds save
(Science / Sandbox) to reach the writer populated, the analyzer loader stops nulling
the snapshot on the funds facet (see What Doesn't Change). Because this ADDS a field
to the frozen analyzer output contract, `AnalyzerVersion` bumps `2` -> `3` and the
analyzer golden-output test updates in the same change (the findings-baseline `1` ->
`2` precedent); existing per-save baselines stay applicable because
`createdAtAnalyzerVersion` is provenance-only and never gates baseline matching.

```json
  "careerSave": {
    "parsed": true,
    "hasFunds": true,  "funds": 25000.0,
    "hasScience": true, "sciencePool": 0.0,
    "hasRep": true,     "reputation": 0.0,
    "subjectScience": { "crewReport@KerbinSrfLandedLaunchPad": 0.0 },
    "activeContractGuids": ["...guid..."],
    "vessels": [
      { "pid": "guid-string", "persistentId": 100000, "name": "X",
        "type": "Ship", "resourceTotals": { "LiquidFuel": 90.0, "Oxidizer": 110.0 } }
    ]
  }
```

Field set = the `CareerSaveSnapshot` surface that already exists (funds, science
pool + per-subject, rep, active contract guids, per-vessel resource totals).
Milestone and facility fractions are exported too (they exist on the snapshot) but
are report-only facets. Determinism: keys sorted, floats InvariantCulture "R",
"\n" line endings, mirroring `ReportWriter` (the existing writer is already
byte-deterministic). CREW ROSTER is NOT on `CareerSaveSnapshot` today and is NOT
added in v1 (that would be new parse behavior); see the `world` roster note in
Behavior.

### Oracle expected-state + diff types (Python, `harness/lib/oracle.py`)

```python
@dataclass(frozen=True)
class SeedBaseline:            # from CareerSaveParser of the staged template
    funds: Optional[float]; science: Optional[float]; reputation: Optional[float]

@dataclass(frozen=True)
class ManifestEntry:
    ut: Optional[float]; seq: int; kind: str   # ut null for a captured line with no
                                               # UT-stamped [Parsek] neighbor; seq =
                                               # the ordinal seqKey used for sort/dedupe
    funds: float; science: float; reputation: float
    rep_mode: str                              # "nominal" (pre-curve, seam-declared)
                                               # | "applied" (post-curve, gameevents)
    subject_ids: Tuple[str, ...]; contract_guid: str; provenance: str

@dataclass(frozen=True)
class ExpectedCareer:
    funds: Optional[float]; science: Optional[float]; reputation: Optional[float]
    subject_science: Dict[str, float]           # report-only facet
    active_contract_guids: Tuple[str, ...]      # report-only facet

@dataclass(frozen=True)
class OracleDivergence:
    facet: str            # "funds"|"sciencePool"|"reputation"|"subjectScience"|"contract"|"vessel"
    kind: str             # "value-mismatch"|"phantom"|"missing"|"residual-expected"
    identity: str         # subjectId/contractGuid/vesselPid; "" for scalar pools
    expected: float; parsed: float
    ut_window: Tuple[Optional[float], Optional[float]]   # bounding manifest UTs
    hard: bool            # facet policy: pools hard; per-identity report-only
    detail: str
```

## Behavior

### Manifest capture (leg A)

- **Seam-declared entries** are read from `[expectations.ledger.manifest]` at
  scenario load. Each entry's amount is an author constant taken from the mission
  design (independent of Parsek). This is the L1 primary source; for B10 there are
  none.

- **Stock-log-captured entries** are grepped from the produced `KSP.log` by the
  harness (pure `hlib.parse_stock_award_lines`) against an ENUMERATED, EN-pinned set
  of stable stock lines. Every enumerated line CITES the stock emitter it comes
  from, and an unverified candidate is EXCLUDED until confirmed on the EN instance
  (mirroring the analyzer's "every invariant cites its enforcing code" discipline).
  The v1 enumeration is deliberately conservative because the zero-delta cross-check
  (below) makes an incomplete set safe. The candidate set (each VERIFY-PENDING-OPERATOR
  against the EN instance before it is trusted for a NONZERO-delta L1 scenario):
  - contract completion / acceptance / failure lines emitted by `ContractSystem`;
  - science credit lines emitted by `ResearchAndDevelopment` on transmit / recover;
  - milestone lines emitted by `ProgressTracking`;
  - facility upgrade lines emitted by `ScenarioUpgradeableFacilities`.
  A candidate that is NOT stable in EN KSP.log (message-system chatter, localized
  text) is not enumerated; where stock is silent, the capture falls to the RESERVED
  `gameevents-captured` provenance (M-B3, an in-game event-time subscriber), NOT to
  a Parsek recalc read.

- **UT source for captured entries.** A stock KSP.log award line is not reliably
  self-stamped with a UT, so the capture assigns `ut` by correlating the award line
  to the NEAREST UT-stamped `[Parsek]` log line at or before it when one is in range;
  when no UT-stamped line is in range, `ut = null` and a monotonic wall-clock ORDINAL
  (line order in the log) is the entry's `seq` sequence key. The `(ut, seq)` sort and
  the dedupe key both use `seq` as the seqKey when `ut` is null, so a null-UT captured
  entry still orders and dedupes deterministically.

- **Captured amounts are DELTAS only.** A captured line is admissible only when it
  reports a per-event CHANGE; a line reporting a post-grant running BALANCE is
  REJECTED (it would double-count against the seed). `roundedAmount` in the dedupe
  key is the entry amount rounded to 3 decimal places, InvariantCulture, so
  float-format jitter across re-emitted lines does not defeat dedupe.

- **Accumulation**: `entries = dedupe(sort(seam_declared + stock_captured))`, sorted
  by `(ut, seq)`, deduped by `(seqKey, kind, contractGuid|subjectId, roundedAmount)`
  where `seqKey` is `ut` when known and the wall-clock ordinal `seq` when `ut` is
  null, keeping the FIRST (a stock line re-emitted on a scene reload at the same
  seqKey is one effect, not two; a genuine second identical award at a DISTINCT seqKey
  survives because the seq key is part of the dedupe key). Written to
  `<runId>.manifest.json`.

### Oracle computation (`oracle.compute_expected`)

Pure. `ExpectedCareer = compute_expected(seed, entries, tol, rec3_whitelist)`:

- **Funds pool (HARD)**: `expected.funds = seed.funds + sum(e.funds for e in
  entries)`, MINUS nothing on a non-rewind discard EXCEPT the Rec-3 carve-out: a
  discard entry that would roll a route row back is SUPPRESSED from the rollback
  when its row is on `rec3_whitelist` (the residual persists by design), and the
  oracle instead expects the `[Rec-3 residual]` diagnostic line in the log. For B10
  there are no discard entries, so this is `seed.funds + 0`.
- **Science pool (HARD)**: `seed.science + sum(e.science)`. Subject caps are a
  Parsek/KSP concern already reflected in the captured event-time magnitudes, so the
  oracle sums the captured (post-cap) deltas, never recomputes caps.
- **Reputation (HARD, curve exception)**: NOT a linear sum. The oracle accumulates
  rep entries sequentially from `seed.reputation`, dispatching on each entry's
  `rep_mode`: a `nominal` entry (a seam-declared contract value, pre-curve) is run
  through `apply_rep_curve` at its running-rep-at-that-point; an `applied` entry (a
  future `gameevents-captured` old/new delta, already post-curve) is added directly
  with NO second curve pass (re-curving an applied delta is the double-curve
  distortion game-actions 15.1 warns of). `apply_rep_curve` mirrors
  `ReputationModule.ApplyReputationCurve`, which mirrors the decompiled STOCK
  keyframes (`docs/dev/done/game-actions/game-actions-spike-findings.md (note: the ReputationModule.cs:22 comment cites a stale plans/ path)`, Spike A) and must
  replicate Unity's Hermite tangent evaluation between keyframes, the
  `SetReputation`-semantics exception (plan 6 / game-actions 15.1). For B10 (no rep
  entries) this is `seed.reputation` unchanged.
- **Per-subject science (REPORT-ONLY)**: accumulate `e.science` onto `e.subjectIds`.
- **Active contracts (REPORT-ONLY)**: apply `contract-accept` / `contract-complete`
  / `contract-fail` transitions to the guid set.
- **Facet absence**: if `seed.hasFunds` is false (Sandbox), the funds facet is
  absent from `ExpectedCareer` and the diff skips it (the facet-skip-when-absent
  policy, mirroring `LedgerGroundTruthDiff`).

### The ledger-oracle verifier (harness-core verifier chain, slot 8)

Runs post-run, AFTER the analyzer (verifier 3) has produced `<save>.analysis.json`.
Activation gate (matching harness-core): run IFF M-B2 is present AND the scenario
declares an `[expectations.ledger]` OR an `[expectations.world]` block; else the slot
stays SKIPPED with the recorded reason `mb2-not-landed` (harness-core reserved
contract). A world-ONLY scenario (an `[expectations.world]` block with no
`[expectations.ledger]`) activates the verifier for the vessel-resource facet alone
and needs NO seed baseline (the world path never reads career pools). SKIPPED
unconditionally on a KILLED attempt (the produced save may be torn; harness-core
edge 5). Steps:

1. Read the parsed produced-save totals from the `.analysis.json` `careerSave` block
   (`hlib.parse_career_save_block`). Absent block when the ledger verifier is active
   -> verifier result INVALID(subkind tooling) (an active ledger verifier requires
   the export; never a silent pass), following the harness-core subprocess-tooling
   policy.
2. When an `[expectations.ledger]` block is present, load `<runId>.manifest.json`
   (the accumulated manifest) and the pre-launch seed baseline; a world-only
   activation SKIPS this step (no manifest, no seed) and jumps to step 4 with only
   the `world` block.
3. `expected = oracle.compute_expected(seed, entries, tol, rec3_whitelist)` (ledger
   activation only).
4. `diffs = oracle.diff_expected_vs_parsed(expected, careerSave, tol, rec3_whitelist)`
   (pure), mirroring the `LedgerGroundTruthDiff` FACET POLICY: hard pools
   (funds/science/rep), report-only per-identity (subject science, contracts,
   milestones, facilities), vessel-resource consistency for the `world` block
   (below). Each divergence carries the `ut_window` (the bounding manifest UTs, or
   `(None, None)` when the drift is a pool-level residual with no bracketing entry).
5. Verdict mapping: ANY hard-facet divergence -> the verifier returns
   `ledger_drift=True`, which the EXISTING `hlib.classify_verdict` branch maps to
   `PARSEK-FAIL(ledger)` with the detail naming the facet and UT window. Report-only
   divergences are logged and recorded in the result, never red. No divergence ->
   verifier PASS. The `ledgerOracle` result slot (currently
   `{"status":"SKIPPED","reason":"mb2-not-landed"}`, the `run.py` reserved value) is
   filled with `{"status":"PASS|FAIL", "hardDivergences":n, "reportOnly":m,
   "utWindow":[..]}`.

Zero-delta cross-check (B10): with an empty manifest, `expected == seed`; the diff
against the parsed produced save must be within tolerance on every hard pool AND the
`capturedRaw` stock-award set must be empty. A nonempty `capturedRaw` on an
empty-manifest scenario is itself the economy-drift signal (an unexpected stock award
fired during passive play) and reds `PARSEK-FAIL(ledger)`, cross-checked by the save
diff. The detection is trigger-agnostic - it reds on any unexpected award - but v1
only EXERCISES the cold-load (R2/BUG-F) + in-scene passivity triggers; R1's
warp/scene-change trigger surface arrives with the `TimeJump`/scene verbs.

### `expectations.world` activation (vessel resource totals)

M-A5 left `expectations.world` parse-and-reserve (it is one of the harness-core
`RESERVED_EXPECTATION_BLOCKS`, so slot 7's expectations verifier records it as
`reserved`). On M-B2 activation `world` LEAVES `RESERVED_EXPECTATION_BLOCKS`: verifier
8 becomes its SOLE owner, so slot 7 STOPS recording it as reserved and there is
exactly ONE owner (no double-count). `[expectations.ledger]` is NOT in
`RESERVED_EXPECTATION_BLOCKS` today (it is a tolerated-unknown block slot 7 ignores),
so activating it moves nothing out of that tuple. M-B2 activates the VESSEL RESOURCE
TOTALS sub-facet using the `careerSave.vessels` export (already parsed). Semantics:

```toml
[expectations.world]
[expectations.world.vessels]
# Correlate a declared vessel to a parsed vessel by launch guid (preferred) then
# persistentId (craft-baked-pid caveat: pid-only is a weak match, report-only).
[[expectations.world.vessels.entry]]
guid = "..."                 # or persistentId = 100000
resources = { LiquidFuel = { expected = 90.0, tol = 0.1 }, Oxidizer = { expected = 110.0 } }
```

- A resource whose parsed total is outside `expected +/- tol` -> hard `world`
  mismatch -> `PARSEK-FAIL(ledger)` (routed through the same verifier-8 result).
- A declared vessel absent from the parsed save (guid/pid correlated) -> missing;
  present-but-undeclared -> phantom. Guid-correlated mismatches are hard; pid-only
  correlations are report-only (craft-baked-pid caveat, mirroring
  `LedgerGroundTruthDiff.CompareRecovery`).
- Default resource tolerance: `0.1` unit absolute (physics settling noise), tunable
  per resource, never a golden trajectory.

ROSTER states: `CareerSaveSnapshot` carries no crew roster today, so a roster
sub-facet would require a small additive `Roster` parse in `CareerSaveParser`
(new parse behavior, out of the v1 export-only scope). v1 activates ONLY the vessel
resource totals; roster-state comparison SEMANTICS are defined here (correlate crew
by name; assert active/reserved/retired/stand-in status against the declared set)
but the facet is DEFERRED until the first scenario needs it (L1 kerbal scripts /
B3 EVA), at which point the single additive `Roster` parse lands with it. This
keeps v1 strictly export-only.

### v1 scenario: B10 with a real (empty) manifest

`harness/scenarios/B10-career-passive-safety.toml` gains an
`[expectations.ledger]` block with an EMPTY manifest, `seedFrom = "template"`,
`tolerances = "default"`, `rec3CarveOut = false`. The driver (unchanged from the
M-A5 flagship, committed verbs only) `LoadGame`s a fresh career cold, `SetSetting`s
auto-record off, `RunTests` the `RecordingInvariants` batch in-scene, and
`FlushAndQuit`s. The ledger-oracle verifier then proves the produced save's
funds/science/rep still equal the seed baseline within tolerance and that no stock
award fired. This automates the R2/BUG-F cold-load-wipe-at-UT=0 guard (the cold
`LoadGame`) and the in-scene no-op passivity guard (the batch must not move the
economy). It does NOT yet cover R1's warp/scene-change economy-drift trigger: v1 has
no warp/scene verb, so that trigger surface is Deferred with the `TimeJump`/scene
verbs. L1 single-module scripts (nonzero declared deltas across the 9 modules x 3
career modes) are the M-B3 follow-on.

Relationship to the in-game harness: `LedgerGroundTruthHarness` (Layer B) proves
recalc-output == save; M-B2 proves manifest == save. Both green means recalc == save
== manifest, closing the loop end to end. They are complementary, not redundant:
Layer B runs in KSP against the live recalc; M-B2 runs unattended against an
independent event-time manifest.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **Stock log line format drift across locales.** The instance is EN; a non-EN
   KSP.log would have localized award text the enumerated patterns miss. -> The
   capture enumeration is EN-PINNED and the automation instance profile pins EN; a
   scenario run against a non-EN instance is out of contract. For the zero-delta B10
   the miss is caught by the save-diff cross-check anyway. For a NONZERO L1 scenario
   the capture set is only trusted after PENDING-OPERATOR EN verification. v1
   (EN-pinned).
2. **Duplicate stock lines on scene reload.** A stock award line re-emitted when a
   scene reloads. -> `dedupe` by `(seqKey, kind, guid|subject, roundedAmount)` where
   `seqKey` is the entry UT when known and the wall-clock ordinal otherwise, keeping
   the first; a re-emit at the same seqKey is one effect. A genuine second award at a
   DISTINCT seqKey survives (the seq key is in the dedupe key). If a same-UT genuine
   double-award ever collided with a re-emit under this key, edge 6's SAVE-is-
   authoritative defense still catches a real double CREDIT (the save holds the true
   count), so the dedupe risk here cannot cause a false PASS. v1.
3. **Manifest entry with no matching stock line** (declared but the effect did not
   fire). -> Cross-check against the save: if the save ALSO lacks the delta, the
   ACTION never happened -> mission/driver failure -> INVALID (the scenario did not
   set up its precondition), not PARSEK-FAIL. If the save SHOWS the delta but no
   capture line matched, that is a capture-enumeration gap -> report-only, flagged
   for enumeration repair (the save diff still reconciles because the declared
   entry's amount matches the save). v1.
4. **Stock line with no matching manifest entry** (captured but undeclared). -> An
   unexpected stock award. On an empty-manifest B10 this is the economy-drift signal
   (trigger-agnostic; v1 exercises the cold-load / passivity triggers, R1's
   warp/scene-change trigger is deferred) -> `PARSEK-FAIL(ledger)`, UT window = the
   captured line's UT (or the ordinal seqKey when null), cross-checked by the save
   diff. On an L1 scenario with a partial manifest it is likewise a hard drift. v1.
5. **Sandbox mode = no career pools.** `CareerSaveParser` sets
   `hasFunds/hasScience/hasRep` false; the seed baseline carries the same. -> The
   `careerSave` block is STILL emitted (the loader returns any `Parsed` snapshot; see
   What Doesn't Change) with the `hasX` flags false, and the oracle skips the pool
   facets entirely (facet-skip-when-absent); the L-track career axis's Sandbox run
   asserts "no career pools present" and passes trivially on pools. Science mode:
   science present, funds/rep absent - the same block carries `hasScience=true`,
   `hasFunds=hasRep=false`. v1.
6. **Quicksave-reload double-award.** An award credited once but a reload re-fires
   the stock line / GameEvents. -> Capture dedupe drops the duplicate LINE; the
   authoritative check is the SAVE, which holds the true single credit. A double
   CAPTURE that is not in the save is a capture artifact (report-only); a double
   CREDIT actually in the save is the real N-fold-bake bug (R7) and reds on the pool
   facet. v1.
7. **Tolerance vs float accumulation.** Summing many small deltas accumulates float
   error. -> Per-facet tolerance mirrors `FacetTolerances.Default` (funds 1.0,
   science 0.1, rep 0.1, subject 0.1); accumulation uses a DETERMINISTIC order
   `(ut, sequence)` so the sum is reproducible, and the tolerance absorbs the
   residual. A drift that exceeds tolerance is real. v1.
8. **The Rec-3 residual** (non-rewind discard leaves route funds + cargo by design).
   -> The oracle consults the `rec3_whitelist`; whitelisted route rows are NOT rolled
   back and the oracle expects the `[Rec-3 residual]` diagnostic line instead of a
   rollback-to-zero, so the ratified behavior does not read as drift (plan 6 /
   logistics 10.6). v1 (specified; B10 leaves `rec3CarveOut=false`, exercised at L3).
9. **UT ordering across scene changes; cold-load UT=0.** Planetarium UT resets to 0
   on a cold OnLoad (BUG-F gotcha) and jumps across scene changes. -> Manifest
   entries are sorted by `(ut, sequence)` for accumulation ONLY; the oracle NEVER
   uses UT=0 (or any UT) as a prune/cutoff bound (the coldload-ut-zero trap). The
   accumulation is order-based, not window-based, so a UT=0 cold load does not drop
   any entry. v1.
10. **Reputation curve nonlinearity.** Rep deltas do not sum linearly. -> The rep
    facet uses `apply_rep_curve` (SetReputation semantics, the 15.1 exception), not
    a linear sum. B10 has zero rep deltas so `expected = seed`; the curve path is
    exercised by L1 rep scripts. v1 (curve path specified; degenerate in B10).
11. **KILLED run, torn save.** A watchdog kill can tear the produced `.sfs`
    mid-flush. -> The ledger-oracle verifier is SKIPPED on any KILLED attempt (the
    save is not ground truth), matching harness-core edge 5; the verdict stays
    KILLED. v1.
12. **M-B2 not landed / no `[expectations.ledger]` or `[expectations.world]` block.**
    -> The slot stays SKIPPED with the recorded reason `mb2-not-landed` (the `run.py`
    reserved value), exactly the harness-core reserved contract; a scenario declaring
    neither block is unaffected. v1.
13. **`careerSave` block absent from `.analysis.json`** (older analyzer, or the
    analyzer threw). -> When the ledger verifier is ACTIVE the missing export is a
    tooling failure -> verifier INVALID(tooling), NOT a silent pass (an active ledger
    check must never green on a missing input). Follows the harness-core
    subprocess-tooling retry-once policy. v1.
14. **Contract state transition mid-run** (accept then complete in one run). -> The
    active-guid SET is a report-only facet (transitions logged, not red); the
    funds/rep DELTAS from completion are HARD (pool facets). So a wrong contract
    STATE is report-only but a wrong contract PAYOUT reds. v1.
15. **Fixture seed baseline missing or unparsable** (the staged template has no
    career pools, or the pre-launch analyzer failed to parse it, but the scenario
    declares `[expectations.ledger]`). -> `seedFrom = "template"` runs the analyzer
    over the staged template pre-launch and reads the seed from its `careerSave`
    block; the two failure modes are distinguished by that block's `parsed`/`hasX`
    flags. `parsed=true` but every `hasX=false` (a template with no
    Funding/R&D/Reputation SCENARIO for a career-ledger scenario) is a spec/fixture
    error -> INVALID(fixture-authoring); a MISSING or `parsed=false` block (the
    analyzer threw / could not parse) is a tooling failure -> INVALID(tooling).
    Never a false PASS either way. v1.
16. **Strategy currency conversion** (a strategy transforms a reward before it is
    credited). -> The captured amount is the POST-transform stock-emitted magnitude
    at event time, so the oracle needs no strategy model and stays independent; a
    `strategy-convert` entry records the actual per-facet emission. v1 (captured as
    declared; strategy L2).
17. **Cross-facet flow** (science auto-converted to funds via a strategy, or a
    milestone that pays funds). -> Each stock emission is captured as a SEPARATE
    manifest entry on the facet it actually credits; the oracle accumulates per
    facet, so a milestone-fed fund reward lands on the funds pool (hard) and the
    milestone id on the report-only milestone facet. v1.
18. **Manifest amount left null (fill-from-capture).** A seam-declared entry with a
    null amount to be filled from the stock capture. -> Fill-from-capture is
    RESTRICTED: it is FORBIDDEN for a facet whose stock magnitude is state-dependent
    (per-subject science, reputation), because there leg A would just re-copy the
    possibly-corrupted leg-B magnitude and lose its independence (see the Mental
    Model residual blind spot); such a facet MUST carry an author constant, and a null
    amount on it reds as a scenario-authoring defect. For an admissible
    (state-independent) facet, if exactly one captured line matches the entry's
    `(seqKey, kind, identity)`, its amount fills the entry; zero or multiple matches
    -> the entry is flagged ambiguous and the run reds `PARSEK-FAIL(ledger)` with the
    ambiguity detail (an un-fillable declaration is surfaced, not swallowed). v1 (B10
    declares none).

## What Doesn't Change

- **No Parsek gameplay or recalc code changes.** The ledger modules,
  `LedgerOrchestrator`, `KspStatePatcher`, ERS/ELS routing, `CareerSaveParser`,
  `LedgerGroundTruthDiff`, and `LedgerGroundTruthHarness` are all consumed as-is.
  M-B2 adds NO in-game behavior and reads NO Parsek-derived number into the oracle.
- **One small analyzer-gating behavior change (named honestly, not zero-change).**
  For the writer to ALWAYS emit `careerSave` on a career-but-non-funds save (Science /
  Sandbox), the analyzer loader stops nulling the snapshot on the funds facet:
  `ParseCareer` (`Source/Parsek.Tests/Analyzer/SaveDirectoryLoader.cs`) now returns
  ANY `Parsed` snapshot (dropping the `HasFunds` gate), and
  `Inv8Ledger.EvaluateCareerDiff` (`Source/Parsek/Analyzer/Rules/Inv8Ledger.cs`)
  RE-GATES the career diff on `HasFunds` instead of on snapshot null-ness. INV8's
  funds-only diff stays unchanged for existing career saves, while Science / Sandbox
  now flow a populated (hasX-flagged) snapshot to the export. This is a small, TESTED
  behavior change in the loader / INV8 gating - INV8's existing career-diff tests plus
  a new Science / Sandbox export test pin it.
- **The primary C# addition is the `careerSave` export block**, test-side in
  `Source/Parsek.Tests/Analyzer/ReportWriter` (+ threading `model.CareerSave` into
  the `AnalysisReport` the writer serializes). It is EXPORT-ONLY: it serializes a
  snapshot the analyzer already parsed. It adds no save data, no runtime behavior,
  no new subprocess. It mirrors the existing `ReportWriter` determinism (sorted
  keys, InvariantCulture, "\n"). If a future scenario needs roster assertions, a
  single additive `Roster` facet on `CareerSaveParser`/`CareerSaveSnapshot` lands
  then; it is NOT in v1.
- **The harness verdict taxonomy and verifier chain** are unchanged. The
  `ledger_drift -> PARSEK-FAIL(ledger)` branch and the `ledgerOracle` result slot
  ALREADY EXIST in `hlib.classify_verdict` and the result JSON (harness core); M-B2
  fills them. No new verdict.
- **The analyzer gate** (`RED=` token, baseline Forbid) is untouched; the
  `careerSave` block is purely additive JSON alongside the existing
  counts/findings, invisible to the `RED=` gate reader.
- **The independence contract** (game-actions 15.6): the oracle never calls Parsek
  recalc; leg A is event-time stock emissions + author constants + the fixture seed,
  leg B is KSP's own serialization. Preserved and made operational.

## Backward Compatibility

Greenfield verifier on a greenfield artifact. The accumulated manifest, the
`careerSave` block, and the oracle result all carry `schema = 1`; a schema bump
makes the harness refuse an old artifact with a clear message rather than mis-parse
(the project no-migration stance). `[expectations.world]` ALREADY parses today as one
of the harness-core `RESERVED_EXPECTATION_BLOCKS`; `[expectations.ledger]` is NOT
reserved - it is a tolerated-unknown block that slot 7 ignores today - so both parse
without error now and a scenario written now needs no format change when M-B2
activates them (activation moves `world` OUT of the reserved tuple and gives verifier
8 sole ownership). A scenario WITHOUT an `[expectations.ledger]` or
`[expectations.world]` block is unaffected (SKIPPED, exactly as today). The
`careerSave` export is additive JSON: an older harness that does not read it ignores
it, and a newer harness reading an older `.analysis.json` that lacks the block treats
the ledger verifier as tooling-unavailable (edge 13), never a false pass.

## Diagnostic Logging

The harness logs to stdout + the per-invocation `harness/results/<ts>_harness.log`,
format `[Harness][LEVEL][Step] message` (mirroring M-A5). Every decision logs; the
batch-counting convention applies to the per-line capture grep (one summary line).

- **SEED**: `Info` "ledger-seed template=<save> via=analyzer parsed=<b> funds=<f>
  science=<s> rep=<r> hasFunds=<b> hasScience=<b> hasRep=<b> resultsRedirect=<dir>";
  `Warn` "ledger-seed: template parsed but no career pools + [expectations.ledger]
  declared -> INVALID(fixture-authoring)"; `Warn` "ledger-seed: template careerSave
  missing/parsed=false -> INVALID(tooling)".
- **CAPTURE**: `Info` "manifest-capture stockLines=<n> deduped=<m>
  seamDeclared=<k> accumulated=<total>"; per unexpected award on an empty manifest
  `Warn` "manifest-capture: unexpected stock award ut=<ut> kind=<k> line='<...>'".
- **ORACLE**: `Info` "oracle-expected funds=<f> science=<s> rep=<r>
  subjects=<n> activeContracts=<n> rec3CarveOut=<b>"; per Rec-3 residual
  `Info` "oracle: rec3 residual retained row=<id> expecting [Rec-3 residual]".
- **DIFF**: one line per facet `Info` "ledger-diff facet=<facet> expected=<e>
  parsed=<p> delta=<d> tol=<t> within=<b> hard=<b>"; per hard divergence `Warn`
  "ledger-drift facet=<facet> id=<id> expected=<e> parsed=<p> utWindow=[<lo>,<hi>]".
- **WORLD**: per vessel `Info` "world-vessel corr=<guid|pid> resource=<r>
  expected=<e> parsed=<p> tol=<t> within=<b>"; roster deferral once
  `Verbose` "world: roster sub-facet deferred (no CareerSaveSnapshot roster)".
- **VERIFY**: `Info` "verify ledgerOracle status=<PASS|FAIL|SKIPPED|INVALID>
  hardDivergences=<n> reportOnly=<m> reason=<...>"; on a missing export block
  `Warn` "verify ledgerOracle status=INVALID subkind=tooling: careerSave block
  absent from analysis.json".

Goal: reading only the harness log and `<runId>.manifest.json`, a developer can
reconstruct the seed, every captured award, the expected totals, which facet
drifted, and the UT window, without rerunning the scenario.

## Test Plan

Every test states the regression it catches. Pure Python decision logic lives in
`harness/lib/oracle.py` and the new `hlib` parsers, pytest-covered in
`harness/lib/test_oracle.py` (+ `test_hlib.py` additions), so it runs in the per-PR
cadence with no KSP. The `careerSave` export is xUnit-covered in
`Source/Parsek.Tests/Analyzer/`. The live path is a PENDING-OPERATOR runbook (an
agent cannot pilot KSP, MEMORY: in-game-sweep-needs-operator).

### Oracle math (pure Python)

- **Empty manifest -> expected == seed.** `compute_expected(seed, [], tol, {})`
  returns the seed unchanged on all three pools. Fails if an empty passive-safety
  run computes a nonzero expected drift (B10 could never PASS).
- **Linear pool accumulation + tolerance.** N funds entries summing to K within
  tolerance -> `expected.funds = seed + K`; a sum outside tolerance -> hard
  divergence. Fails if float accumulation order is nondeterministic or the tolerance
  is mis-applied.
- **Reputation curve, not linear sum.** Two rep deltas through `apply_rep_curve`
  produce the curve-composed value, NOT their arithmetic sum. Fails if rep is summed
  linearly (the double-curve distortion 15.1 warns about, read backward).
- **Rec-3 carve-out.** A non-rewind-discard entry on the `rec3_whitelist` does NOT
  reduce expected funds and the oracle flags an expected `[Rec-3 residual]`; the
  SAME entry NOT whitelisted DOES roll back. Fails if a ratified route residual reds
  as drift, or a non-ratified rollback is skipped.
- **Facet absence (Sandbox).** `seed.hasFunds=false` -> the funds facet is absent
  from `ExpectedCareer` and skipped in the diff. Fails if a Sandbox run reds on a
  missing funds pool.

### Manifest parse + capture (pure Python)

- **Spec-declared parse.** A `[[expectations.ledger.manifest]]` array parses into
  `ManifestEntry` list with the right kinds/amounts; an unknown `kind` rejects.
  Fails if a malformed declaration silently drops an expected effect.
- **Stock-log capture + dedupe.** A KSP.log with a stock award line preceded by a
  UT-stamped `[Parsek]` line -> one captured entry carrying that UT; the SAME line
  re-emitted at the same seqKey -> still one (dedupe on `(seqKey, kind, identity,
  roundedAmount)`); a repeat at a DISTINCT seqKey -> two. A captured line with NO
  UT-stamped neighbor -> `ut=null`, seqKey = wall-clock ordinal, still ordered and
  deduped. A balance-reporting (non-delta) line -> rejected. Fails if a scene-reload
  re-emit double-counts, a genuine second award is dropped, a null-UT entry misorders,
  or a running-balance line is admitted as an amount.
- **Fill-from-capture ambiguity + state-dependent forbid.** A null-amount declared
  entry on a state-INDEPENDENT facet with exactly one matching capture fills;
  zero/multiple matches -> flagged ambiguous. A null amount on a state-DEPENDENT facet
  (per-subject science, reputation) is REJECTED outright (author constant required).
  Fails if an un-fillable declaration silently passes, or if a state-dependent facet
  is allowed to fill from capture.

### Diff facet policy (pure Python)

- **Hard pools vs report-only per-identity.** A funds-pool drift -> `hard=True` ->
  `ledger_drift`; a subject-science-only drift -> `hard=False`, logged not red.
  Fails if a per-identity mixed-history difference reds the run (the exact
  false-positive `LedgerGroundTruthDiff` avoids) or a real pool drift reads as
  report-only.
- **UT window naming.** A drift bracketed by manifest entries at ut=10 and ut=50
  reports `utWindow=[10,50]`; a pool residual with no bracketing entry reports
  `[None,None]`. Fails if a red run cannot point at where the drift entered.
- **World vessel resource correlation.** Guid-correlated resource drift -> hard;
  pid-only -> report-only (craft-baked-pid caveat); missing/phantom vessel
  classified. Fails if a pid collision certifies the wrong vessel as matched.

### `careerSave` export (xUnit, Parsek.Tests/Analyzer)

- **Export round-trip + determinism.** `ReportWriter.BuildJson` over a model with a
  populated `CareerSave` emits a `careerSave` block whose fields equal the snapshot;
  the same input is byte-identical (sorted keys, InvariantCulture). Fails if the
  export churns (breaking the Python parser) or drops a facet.
- **Always-emitted block + facet flags.** A Science / Sandbox model (`Parsed=true`,
  `HasFunds` false) emits a `careerSave` block with `parsed:true` and the correct
  `hasX` flags (the loader no longer nulls it on the funds facet); a truly non-career
  / unparsable model (`CareerSave == null`) emits `careerSave: {parsed:false}` -
  ALWAYS a block, never omitted. The Python verifier treats `parsed:false` /
  `hasX:false` as facet-absent (pools skipped) and only a MISSING block as
  tooling-missing. Fails if a Sandbox save reds the ledger verifier, or if the writer
  omits the block (which would alias facet-absence with tooling-absence).

### End-to-end (fake save JSON, no KSP)

- **B10 empty-manifest zero-drift PASS.** A synthetic `.analysis.json` with a
  `careerSave` block equal to the seed baseline + an empty
  `<runId>.manifest.json` -> the ledger-oracle verifier returns PASS and the
  `ledgerOracle` result slot records `hardDivergences=0`. Fails if the zero-delta
  flagship cannot go green (the R2 cold-load + no-op passivity guard could never be
  automated).
- **Injected unexpected award -> PARSEK-FAIL(ledger).** The same fake save but with
  the funds pool moved beyond tolerance (an economy drift) and/or a stock award line
  in the fake KSP.log -> the verifier returns `ledger_drift=True`,
  `classify_verdict` maps to `PARSEK-FAIL(ledger)`, and the UT window is named.
  Fails if a real economy drift (BUG-A) or a cold-load wipe (BUG-F) reads as PASS
  (the most dangerous silent pass this module exists to prevent).

### PENDING-OPERATOR live row

On a provisioned stock-minimal EN instance, run the B10 scenario for real: cold
`LoadGame` into a fresh career, the in-scene `RecordingInvariants` batch,
`FlushAndQuit` (committed verbs only; no warp / scene-change verb in v1). Confirm the
harness stages the fresh career, boots via the seam, produces a `.analysis.json`
carrying the `careerSave` block, computes `expected == seed`, diffs clean, and writes
a PASS `ledgerOracle`
result with `hardDivergences=0`. Grep evidence: the `[Harness][...][SEED]`,
`[...][CAPTURE] accumulated=0`, `[...][DIFF] within=True`, and
`verify ledgerOracle status=PASS` lines, plus the `careerSave` block in
`<save>.analysis.json` and the empty `<runId>.manifest.json`. Then, as a negative
control, hand-edit the produced save's `Funding` funds beyond tolerance, re-run just
the verifier over that save, and confirm `PARSEK-FAIL(ledger)` with the funds facet
named. This is the first automated cold-load-wipe (R2/BUG-F) + in-scene no-op passivity
guard the plan section 6 L1-passive-safety milestone names (R1's warp/scene-change
economy-drift trigger is Deferred with the `TimeJump`/scene verbs), and it verifies
the EN stock-award capture enumeration against a real log before any nonzero-delta L1
scenario trusts it.

## Deferred Items and Open Questions

Recorded so they are not lost; none blocks the v1 B10 passive-safety oracle.

- **Warp / scene-change trigger surface (R1).** The committed v1 driver is
  `LoadGame` / `SetSetting` / `RunTests` / `FlushAndQuit`, with no warp or
  scene-change verb (`TimeJump` is RESERVED; no scene-change verb exists in the seam
  table). v1 automates the cold-boot-load (R2/BUG-F) and in-scene no-op passivity
  guards only; R1's warp/scene-change economy-drift trigger arrives when the
  `TimeJump`/scene verbs land (M-B3 or later). The oracle + verifier here are
  trigger-agnostic, so no oracle change is needed - only the new driver verbs.
- **L1 single-module scripts (M-B3).** Nonzero declared deltas across the 9 modules
  x 3 career modes need the KscAction seam commands and the `gameevents-captured`
  provenance; v1 delivers only the zero-delta B10 cross-check. The manifest format
  already carries the entry schema those scripts populate.
- **Roster world sub-facet.** `CareerSaveSnapshot` has no crew roster; the world
  block v1 activates vessel resource totals only. The roster sub-facet lands with a
  single additive `Roster` parse in `CareerSaveParser` when the first kerbal
  scenario (L1 / B3 EVA) needs it; its comparison semantics are defined above.
- **Stock-award capture enumeration completeness.** The v1 EN enumeration is
  conservative and safe for the zero-delta cross-check; a NONZERO L1 scenario must
  confirm each enumerated line against a live EN KSP.log (PENDING-OPERATOR) and,
  where stock is silent, wait for the `gameevents-captured` in-game subscriber
  (M-B3) rather than reading a Parsek-derived number.
- **Rewind / timeline-layering oracle (L4, M-C3).** Reservations across rewinds,
  tombstones, supersede flips, and recalc-from-UT=0 layering exercise the oracle
  under save manipulation; the `rec3CarveOut` and UT-window machinery here are the
  seams those build on. Deferred with M-C2/M-C3.
- **Reputation curve fidelity.** RESOLVED, not open: `apply_rep_curve` mirrors
  `ReputationModule.ApplyReputationCurve`, whose keyframes are the decompiled STOCK
  values extracted in the game-actions spike (`docs/dev/done/game-actions/game-actions-spike-findings.md (note: the ReputationModule.cs:22 comment cites a stale plans/ path)`,
  Spike A - the authority) and which replicates Unity's Hermite tangent evaluation
  between keyframes. The Python port must reproduce that Hermite evaluation and be
  unit-tested against known in-game rep transitions before an L1 rep script trusts a
  nonzero rep expectation; the per-provenance `nominal|applied` discriminator (curve
  applied ONLY to `nominal` seam-declared values, never to an `applied`
  gameevents-captured delta) keeps a future applied delta from being double-curved.
  v1 (B10) has zero rep deltas, so the curve is inert until then.
