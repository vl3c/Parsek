# Design: L1-L2 Ledger Action Scripts (Module M-B3)

Status: DRAFT (2026-07-14). Module M-B3 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, section 6 the L-track L0-L5, the module
table row M-B3 ~line 501). This is the Step 3 design doc the plan mandates
(section 11b) before any code: it turns the plan's "L1-L2 ledger scripts:
KscAction commands + per-module action scripts x 3 career modes" into concrete
scenario specs with the FIRST-EVER nonzero seam-declared manifests, the career
fixtures those scripts need, and the honest sequencing of the stock-award capture
enumeration they depend on.

M-B3 is primarily a SCENARIO-AUTHORING + FIXTURE + OPERATOR-RUNBOOK module, not a
new-code module. Its two hard dependencies both merged: M-C1 (the four `KscAction`
sub-actions plus `TimeJump`) and M-B2 (the ledger oracle whose `compute_expected`
already sums nonzero funds / science deltas). The nonzero-manifest machinery, the
author-constant vs fill-from-capture rules, the facet policy, and the reputation
curve are all M-B2's; M-B3 exercises them for the first time and adds no oracle
math. The SOLE trusted leg for every L1/L2 script is the seam-declared-vs-save diff.
The stock-award capture cross-check contributes NO signal for a nonzero manifest today
and is UNTRUSTED by design. Two facts combine. First, the shipped `STOCK_AWARD_PATTERNS`
set is DEAD against a real EN log: every Parsek-emitted `ResearchAndDevelopment ... delta=`
line is `[Parsek]`-tagged and excluded (`hlib.py:1466`), and NO stock line matches any
shipped pattern (the pinned Assembly-CSharp emits zero `delta=` strings; stock R&D logs as
`[Research & Development]: +N data on ...`), so a nonzero L1 run captures ZERO awards,
`unmatched_captured_awards` is empty, and the seam entry cross-checks against the save-diff
alone -- the scripts are GREEN today. Second, the hazard is LATENT: as WIRED
(`run.py:1333`-`:1346`) an unmatched captured award becomes a HARD divergence, and a
nonzero action's real award fires at a runtime `seq_key` that can NEVER match its `ut=0.0`
seam entry, so the MOMENT the patterns are rewritten to match real stock text a nonzero
capture becomes a STRUCTURAL FALSE-RED. So M-B3 does NOT enumerate the tech-unlock /
facility-upgrade-funds / hire-funds patterns it names; the REAL future work is REWRITING
the patterns against real stock log text AND solving the `seq_key`/UT correlation
TOGETHER (Behavior "Stock-award capture sequencing"), DEFERRED behind a UT-agnostic
single-action match. B10's empty-manifest cross-check stays trusted (any-capture-reds is
sound with no seam entry, and today captures nothing anyway).

Consumed contracts (already merged, read as authorities, never re-specified here):

- The ledger oracle `docs/dev/design-autotest-ledger-oracle.md` (M-B2), which OWNS
  the manifest-entry schema, the `[expectations.ledger]` block, the seam-declared
  provenance, the author-constant-vs-fill-from-capture rules, the state-dependent
  facet forbid, `compute_expected` (seam-declared entries ONLY), the facet policy
  (funds / science pool / reputation HARD; per-subject / contracts / facilities /
  milestones REPORT-ONLY), the reputation curve, and the `unmatched_captured_awards`
  cross-check.
- The seam verbs `docs/dev/design-autotest-seam-verbs-c1.md` (M-C1), which OWNS the
  `KscAction` verb (`research-node` / `upgrade-facility` / `hire-kerbal` /
  `dismiss-kerbal`), its per-sub-action dispatch gates, its effect-confirmation
  requirement, its refusal taxonomy, `TimeJump`, and the seam-declared manifest
  annotation contract (the DRIVER declares, the verb performs, amounts NEVER from
  Parsek recalc).
- The harness core `docs/dev/design-autotest-harness-core.md` (M-A5), which OWNS the
  scenario spec schema, the verifier chain, the driver-validity overlay, and the
  fixture staging.
- The command seam `docs/dev/design-autotest-command-seam.md` (M-A2), the mission
  library `docs/dev/design-autotest-mission-library.md` (M-B1, the `steps` shape),
  and the fixture regeneration / versioning `docs/dev/design-autotest-stack-setup.md`
  + the plan section 7 (the fixture generation stamp + re-harvest queue).

This doc pins against those PUBLIC surfaces (the `[expectations.ledger]` block, the
`KscAction` verb args, `compute_expected` / `parse_manifest_entries`, the
`STOCK_AWARD_PATTERNS` set, the `CareerSaveSnapshot` field set), never their
internals, so a later Gloops file relocation does not break it.

Plain ASCII, no em dashes, no emoji, LF endings.

---

## Problem

The L-track (plan section 6) is the initiative's one subsystem with a COMPUTABLE
independent oracle. M-B2 built and activated that oracle for the ZERO-delta flagship
B10 (a fresh career, empty manifest, `expected == seed`), which automated the
R2/BUG-F cold-load-wipe guard and the in-scene no-op passivity guard. M-C1 built the
four `KscAction` sub-actions that let the harness cause real KSC career actions
headlessly. Neither has yet been used to drive a NONZERO career effect and prove the
ledger accounted it exactly.

L1 (plan section 6) is "single-module action scripts, stock actions only (complete
contract, research node, upgrade facility, hire/dismiss, strategy, milestone, EVA
science) -> snapshot -> zero drift". L2 is "cross-module interactions (contract
completion touches funds + rep + science; strategy currency conversion;
milestone-fed rewards; facility spend + refund windows)". Both run in Career,
Science, and Sandbox because module activation differs per mode.

What is missing to author those scripts:

1. **The first nonzero seam-declared manifests.** B10's `[expectations.ledger]`
   manifest is EMPTY. Every L1 script needs one or more `[[expectations.ledger.manifest]]`
   entries with a real per-facet delta, an author constant vs fill-from-capture
   decision, and the matching `KscAction` driver step. This is the first exercise of
   `compute_expected` on a non-empty entry list.

2. **Career fixtures.** The B10 spec already references
   `fixtures/saves/fresh-career` (`harness/scenarios/B10-career-passive-safety.toml`),
   but that save DOES NOT EXIST on disk yet (only `harness/fixtures/saves/gloops-airshow`
   is committed). No committed career fixture exists at all. L1 needs one fresh-mode
   save PER career mode, each with a known seed and enough of the right currency for
   its scripted actions, created by an operator (an agent cannot pilot KSP).

3. **Per-item decisions for the four L1 items with no seam verb.** M-C1 shipped
   `research-node` / `upgrade-facility` / `hire-kerbal` / `dismiss-kerbal`. It did NOT
   ship complete-contract, strategy, milestone, or EVA-science. Each must be decided:
   defer to a named `KscAction` batch-2 sub-action, drive via existing capabilities,
   or route to a flown B-track scenario. Contract-complete in particular has no
   headless stock path without a vessel; that must be reasoned honestly, not waved
   away.

4. **Honest capture-enumeration sequencing.** The oracle's `unmatched_captured_awards`
   cross-check (`hlib.py:1508`) turns a captured award the spec never declared into a
   HARD divergence (`run.py:1333`-`:1346`). The shipped `STOCK_AWARD_PATTERNS` set
   (`hlib.py:1359`) is three EN-pinned patterns, and against a REAL EN log they are DEAD:
   every Parsek-emitted `ResearchAndDevelopment ... delta=` line is `[Parsek]`-tagged and
   skipped (`hlib.py:1466`), and no stock line matches any shipped pattern (the pinned
   Assembly-CSharp has zero `delta=` strings; stock R&D logs as `[Research & Development]:
   +N data on ...`). So a nonzero L1 run captures ZERO awards today, the unmatched set is
   empty, and the seam entry cross-checks against the save-diff alone -- the scripts are
   GREEN. The false-red hazard is LATENT, not current: the cross-check MATCHES on
   `(seq_key, kind, identity)` (`hlib.py:1508`-`:1526`) where a captured award's `seq_key`
   is its runtime UT while a seam entry carries an author `ut` (typically `0.0`;
   `oracle.py:199`), so a real award can NEVER match a `ut=0.0` seam entry -- and the
   MOMENT the patterns are rewritten to match real stock text, a nonzero capture would be
   a STRUCTURAL FALSE-RED (kind-split AND seq_key mismatch). So for nonzero scripts the
   capture leg is UNTRUSTED; the seam-declared-vs-save diff is the sole trusted leg. The
   REAL work item is REWRITING the patterns against real stock log text AND solving the
   UT correlation together (a UT-agnostic single-action match), DEFERRED. B10's
   empty-manifest cross-check stays trusted (any-capture-reds is sound regardless of
   `seq_key`).

M-B3 supplies: the L1 script family (the four M-C1 sub-actions x three career modes),
the three career fixtures with named contents, the manifest author-constant discipline
applied per action, and the honest capture-pattern sequencing. NO L2 script is
drivable with the M-C1 verb set today: the one L2 candidate (facility spend +
scene-change refund window, R6) needs a persist-before-reload seam the verb set lacks
(Behavior "The L2 refund-window script"), so it is Deferred behind a named `SaveGame`
batch-2 seam verb. It names the rest (contract triple-credit, strategy conversion,
milestone-fed rewards, EVA science, and L3-L5) as Deferred with the seam sub-actions
or flown scenarios they need.

---

## Terminology

Terms from M-B2 (Action manifest, Seed baseline, Oracle, Ledger-oracle verifier,
INDEPENDENCE OF AMOUNTS, author constant, fill-from-capture, state-dependent facet,
facet policy) and M-C1 (KscAction sub-action, seam-declared manifest annotation,
verb refusal, effect confirmation) are used unchanged. New terms this doc introduces:

| Term | Definition |
|------|------------|
| **L1 script** | A scenario spec that drives EXACTLY ONE career-effecting `KscAction` sub-action against a career fixture, declares the effect as a nonzero seam-declared manifest, and asserts `expected == save` on the touched pool. One module, one action, one facet. |
| **L2 script** | A scenario spec that drives an action plus a following state transition (a scene change, a second action) so a CROSS-module or windowed interaction is asserted (e.g. facility spend then scene change asserting no phantom refund). |
| **Career-mode axis** | The three stock game modes the L-track runs each level in: Career (`Game.Modes.CAREER`, funds + science + rep), Science (`SCIENCE_SANDBOX`, science only), Sandbox (`SANDBOX`, no economy pools). The fixture's `persistent.sfs` GAME node Mode field selects it; the fixture IS the mode selector. |
| **Career fixture** | A fresh-mode staged save (`fixtures/saves/<mode>`) with a KNOWN seed (funds/science/rep pool values), enough of the right currency to afford the scripted actions, and a pinned roster so a state-dependent cost (hire) is a known constant. Created by the operator runbook, stamped with the recording schema generation (M-A4), harvested-provenance = synthetic. |
| **Module activation matrix** | The per-mode table of which of the nine ledger modules produce an assertable delta, which drives each mode's script set. |
| **Author-constant action** | An L1 action whose stock magnitude is FIXED DATA independent of live state (tech-node science cost, facility per-level funds cost), so the manifest declares a literal author constant. |
| **State-dependent-but-fixture-pinned action** | An action whose stock magnitude depends on live state (hire cost rises with roster size via the `GameVariables` recruit-cost curve) but whose state the FIXTURE pins, making the magnitude a known constant AT the fixture's roster size. This is the honest resolution of a state-dependent funds facet when no capture line is enumerated. |
| **Nonzero-manifest primary assertion** | The M-B3 TRUSTED leg: `compute_expected(seed, seam_entries)` == parsed save on the touched HARD pool. This is the SOLE pass/fail authority for every nonzero L1/L2 script. |
| **Nonzero-manifest capture leg (untrusted)** | `unmatched_captured_awards` over the produced KSP.log, wired to hard-red an unmatched award (`run.py:1333`). For a nonzero manifest this leg is UNTRUSTED. TODAY the shipped patterns are DEAD against a real EN log (no captures), so the leg is an empty no-op and the scripts pass on the save-diff alone. The hazard is LATENT: a seam entry's author `ut` (typically `0.0`) and a real award's runtime `seq_key` cannot match, so once the patterns are rewritten to real stock text a nonzero capture would be a structural false-red. Making it trustworthy needs the deferred UT-agnostic single-action match. B10's EMPTY-manifest capture leg is a different, trusted case (any-capture-reds). |

Design-concept-to-implementation mapping (names diverge across the seam and the
nine modules):

| Design concept (this doc) | Implementation |
|---------------------------|----------------|
| research-node script | `KscAction action=research-node` (M-C1) -> `RDTech.ResearchTech` (`TechResearchSpendPatch.cs:17`); ledger via `ScienceModule` |
| upgrade-facility script | `KscAction action=upgrade-facility` -> `SpaceCenterBuilding.UpgradeFacility(bool)` (`FacilityUpgradeSpendPatch.cs:15`); ledger via `FacilitiesModule` + `FundsModule` |
| hire-kerbal script | `KscAction action=hire-kerbal` -> `Funding.AddFunds(-hireCost, CrewRecruited)` + `KerbalRoster.HireApplicant` (the debit mirror is `TestCommandKscAction.cs:517-519`; `KerbalHirePatch.cs:19-22` is the committed-hire BLOCK prefix, not the debit); ledger via `FundsModule` + `KerbalsModule` |
| dismiss-kerbal script | `KscAction action=dismiss-kerbal` -> `KerbalRoster.Remove` (`KerbalDismissalPatch`); ledger via `KerbalsModule` |
| the ledger cross-check | `oracle.compute_expected` + `oracle.diff_expected_vs_parsed` + `hlib.unmatched_captured_awards` |
| the nine modules | `Source/Parsek/GameActions/{Funds,Science,Reputation,Milestones,Facilities,Contracts,Strategies,Route}Module.cs` + `Source/Parsek/KerbalsModule.cs` |

---

## Mental Model

An L1 script is one turn of the M-B2 loop with a NONZERO manifest. The seed comes
from the fixture; the driver causes exactly one real career effect through a
`KscAction` verb; the DRIVER declares that effect as a seam-declared manifest entry;
the oracle proves the produced save moved by exactly the declared amount. The stock-log
capture is UNTRUSTED for nonzero scripts (the shipped patterns are DEAD against a real EN
log, so it is an empty no-op today; a latent structural false-red once the patterns are
rewritten to match real stock text, because a seam entry's author `ut` and a real award's
runtime `seq_key` never match); the seam-declared-vs-save diff is the sole PASS/FAIL
authority.

```
  career fixture (fresh-<mode>)  --analyzer (pre-launch, one parser)-->  seed pool
        |  (Mode + known funds/science/rep + pinned roster)                 |
        v                                                                   |
   M-A5 driver (seam steps):                                                |
     LoadGame fresh-<mode>  -> SetSetting autoRecordOnLaunch=false          |
     -> KscAction <sub-action> <target>   (the ONE real career effect)      |
     -> RecordingState -> FlushAndQuit                                      |
        |                                                                   |
        |  leg A: manifest = the scenario's ONE seam-declared entry         |
        |    kind=<tech-unlock|facility-upgrade|kerbal-hire|kerbal-dismiss> |
        |    facet delta = AUTHOR CONSTANT (or fixture-pinned constant)     |
        |    provenance = seam-declared                                     |
        |          + stock-log capture (UNTRUSTED for nonzero, not summed;   |
        |            shipped patterns DEAD today -> 0 captures -> no-op)      |
        |                                                                   v
        |                                    oracle.compute_expected(seed, [entry])
        |                                       => EXPECTED = seed +/- the one delta
        |  leg B: produced-save parse (careerSave block, CareerSaveParser)  |
        v                                                                   |
   analyze-recordings.ps1 (verifier 3) -> <save>.analysis.json careerSave   |
                       |                                                     |
                       v                                                     v
                 PARSED pool  ===  diff (HARD on the touched pool)  === EXPECTED pool
                                        |
              expected == parsed on the touched HARD pool   -> PASS (SOLE gate)
              hard-pool drift  -> PARSEK-FAIL(ledger), UT window named
              (nonzero capture leg: UNTRUSTED; shipped patterns dead -> 0 captures ->
               empty no-op today; latent false-red once patterns match real stock text.
               B10 empty-manifest ONLY: any captured award reds -- still trusted)
```

Three invariants carry over from M-B2 and shape every M-B3 decision:

- **The oracle never reads a Parsek-computed number.** The declared delta is an
  author constant from stock DATA (the node's science cost, the facility's per-level
  funds cost) or a fixture-pinned known cost (hire at a pinned roster size), never
  `Funding.Instance.Funds` after the action or any recalc output. `compute_expected`
  consumes the seam-declared entry only (`oracle.py:590`); the captured award is never
  summed (self-cancellation defense, M-B2 Accumulation) and, for a nonzero manifest, is an
  untrusted cross-check that today captures nothing.

- **The primary leg carries the whole assertion.** If the save moved by a WRONG amount
  (Parsek over/under-credited), the produced-save diff drifts from the author constant ->
  hard drift -> PARSEK-FAIL(ledger). The nonzero capture leg is UNTRUSTED and does not
  help: `unmatched_captured_awards` is wired to hard-red an unmatched award
  (`run.py:1333`), yet a real award's runtime `seq_key` never equals a seam entry's author
  `ut` (typically `0.0`), so it can never be "explained" by the seam entry. TODAY the
  shipped patterns are DEAD against a real EN log (Parsek lines `[Parsek]`-excluded at
  `hlib.py:1466`; no stock line matches), so a nonzero run captures nothing, the leg is an
  empty no-op, and the scripts pass on the save-diff. The hazard is LATENT: rewriting the
  patterns to match real stock text WITHOUT first solving the UT correlation would turn
  every nonzero capture into a structural FALSE-RED. B10's EMPTY-manifest capture leg is a
  distinct trusted case: with no seam entries the "any captured award reds" signal is sound
  for any `seq_key`, so B10 stays gated on it. Making the nonzero capture leg trustworthy
  is a named deferred item (rewrite patterns to real stock text + a UT-agnostic
  single-action match, together).

- **Module activation is the fixture's mode.** In Career all nine modules can move;
  in Science only `ScienceModule` (research spend) is assertable and there are no
  funds/rep pools; in Sandbox no economy pool exists and the CAREER-gated verbs DEFER
  (`career-not-ready`) rather than execute, so the Sandbox script drives NO KscAction
  at all and is a pure passive B10 variant. The mode-specific script set falls straight
  out of this.

---

## Data Model

M-B3 adds NO persisted file format, NO new manifest schema, NO oracle types. It
authors declarative artifacts against the frozen M-B2 / M-C1 / M-A5 surfaces and
specifies three fixtures. Four artifact groups.

### The module activation matrix (drives the per-mode script sets)

The nine ledger modules and which stock action feeds each, cross-referenced with
whether the M-C1 verb set can drive it TODAY and in which mode a delta is assertable:

| # | Module (`GameActions/` unless noted) | Stock action that feeds it | Driven by M-C1 today? | Career | Science | Sandbox |
|---|--------------------------------------|----------------------------|-----------------------|--------|---------|---------|
| 1 | `FundsModule` | contract payout, recovery, facility spend, hire spend, strategy conversion | facility + hire spend (M-C1) | yes | no pool | no pool |
| 2 | `ScienceModule` | science transmit/recover, tech-unlock spend | tech-unlock spend (M-C1 research-node) | yes | yes (needs M-C1 gate widen, OQ1) | verb defers (career-not-ready) |
| 3 | `ReputationModule` | contract complete rep, milestone rep, tombstone penalty | no (contract/milestone deferred) | yes | no pool | no pool |
| 4 | `MilestonesModule` | `ProgressTracking` (first orbit, first landing, records) | no (flown-only byproduct) | flown | flown | flown |
| 5 | `FacilitiesModule` | `SpaceCenterBuilding.UpgradeFacility` | yes (M-C1 upgrade-facility) | yes | verb defers (CAREER-only, Funding null) | verb defers (CAREER-only) |
| 6 | `ContractsModule` | `ContractSystem` accept/complete/fail | no (no headless verb) | deferred | deferred | n/a |
| 7 | `StrategiesModule` | `StrategySystem` activate; conversion as byproduct | no (no verb) | deferred | deferred | n/a |
| 8 | `RouteModule` | logistics route delivery (Rec-3 residual) | no (RouteCommand reserved) | L3+ | L3+ | n/a |
| 9 | `KerbalsModule` (`Source/Parsek/KerbalsModule.cs`) | hire/dismiss, tombstones | yes (M-C1 hire/dismiss) | yes | verb defers (CAREER-only, Funding null) | verb defers (CAREER-only) |

The drivable-today L1 surface (the "yes" cells) is exactly the four M-C1 sub-actions.
Everything else is Deferred with its blocking verb or its flown-scenario route named.

### The L1 scenario specs (declarative, `harness/scenarios/`)

Each L1 script is a `schema = 1` scenario spec in the M-A5 / M-B2 shape, adding a
nonzero `[[expectations.ledger.manifest]]` entry. The naming convention:
`L1-<action>-<mode>.toml` (e.g. `L1-research-node-career.toml`). Skeleton (shared by
all L1 scripts; per-action deltas in Behavior):

```toml
schema = 1
id = "L1-research-node-career"
tier = "daily"
description = "Career: research one tech node, science pool drops by the node cost, no other drift."
instanceProfile = "stock-minimal"
tags = ["ledger", "L1", "R7"]

[fixture]
saveTemplate = "fixtures/saves/fresh-career"   # the mode selector; leaf == runSaveName
injectedRecordings = "none"
craft = []

[driver]
kind = "seam"
steps = [
  { cmd = "LoadGame",    args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting",  args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "KscAction",   args = { action = "research-node", node = "basicRocketry" }, expect = "OK", budget = 60 },
  { cmd = "RecordingState", expect = "OK" },
  { cmd = "FlushAndQuit", expect = "OK" },
]

[dimensionsCovered]
D8  = ["science", "recalc-engine", "orchestrator", "ksp-state-patcher", "action-blocking"]
D14 = ["career"]

[expectations.recordings]
count = { min = 0, max = 0 }   # no flight; recording-free run (selects REC-001/003 suppression)

[expectations.logContracts]
required  = ["kscaction action=research-node .* applied=true", "OnTechnologyResearched"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]
allowedAnomalies = []

[expectations.ledger]
seedFrom     = "template"
tolerances   = "default"
rec3CarveOut = false

[[expectations.ledger.manifest]]
  ut         = 0.0
  kind       = "tech-unlock"
  science    = -5.0           # AUTHOR CONSTANT: the basicRocketry node cost (stock data, KSP 1.12.5). VERIFY-PENDING-OPERATOR.
  provenance = "seam-declared"

[runtime]
budgetSeconds = 600
[retry]
policy = "once"
[expectedFail]
bugId = ""
```

The nonzero entry is the ONLY structural difference from B10. `compute_expected`
(`oracle.py:590`) already sums `e.science` and `e.funds`, so the oracle math needs no
change; the science pool expected becomes `seed.science + (-5.0)`, diffed HARD. Every
node-cost author constant is VERIFY-PENDING-OPERATOR against the pinned KSP 1.12.5
stock tech tree, the same discipline the capture patterns carry (the operator reads the
node cost during fixture creation and records it in the fixture README).

### The three career fixtures (`fixtures/saves/`)

Specified so the operator runbook is mechanical (exact contents, one per mode). Each
is a fresh stock save created in the named mode, then trimmed of anything nondeterministic:

| Fixture | Mode (GAME node) | Seed funds | Seed science | Seed rep | Roster (pinned) | Purpose / afford |
|---------|------------------|-----------|--------------|----------|-----------------|------------------|
| `fixtures/saves/fresh-career` | CAREER | a KNOWN value well above the scripted spends (e.g. 500000 to afford a facility upgrade + a hire) | a KNOWN value above the node cost (e.g. 100.0, affords a 5-science node) | 0.0 | EXACTLY the 4 stock hired starting kerbals (Jeb/Bill/Bob/Val), 0 assigned, ZERO Parsek reservations / stand-ins, plus 1 KNOWN applicant to hire | research-node, upgrade-facility, hire-kerbal, dismiss-kerbal |
| `fixtures/saves/fresh-science` | SCIENCE_SANDBOX | absent (no funds pool) | a KNOWN value above the node cost (e.g. 100.0) | absent | stock starting kerbals | research-node only (the science-facet single-module script) |
| `fixtures/saves/fresh-sandbox` | SANDBOX | absent | absent | absent | stock starting kerbals | passive-only (a pure B10 variant; NO KscAction step -- the CAREER-gated verbs would DEFER career-not-ready) |

Fixture contents rules (so the seed is reproducible and the analyzer seed leg is
stable):

- No craft in flight, no active vessels, no in-progress contracts, no unlocked tech
  beyond the mode's default start node, no facility above level 0 (so the facility
  upgrade has a level to climb). A clean cold KSC.
- The GAME node's `Mode` is the mode selector; the seed pools are read from
  `Funding`/`ResearchAndDevelopment`/`Reputation` SCENARIO nodes by the pre-launch
  analyzer (`CareerSaveParser`), so their values ARE the seed. The fixture author sets
  them to the KNOWN values above.
- The roster is pinned to EXACTLY the 4 stock hired starting kerbals (Jeb/Bill/Bob/Val)
  with ZERO Parsek reservations or stand-ins, so the `GameVariables` recruit-cost curve
  yields a DETERMINISTIC hire cost. The curve input is the HIRED-CREW COUNT (the size of
  the already-hired roster), NOT the applicant's identity: adding a 5th kerbal costs
  what the curve returns at a hired count of 4, whichever named applicant is chosen. The
  single known applicant is the hire TARGET, not the cost determinant. Recording the
  pinned hired-crew count + the resulting known cost in a fixture README is load-bearing:
  if the hired roster drifts (a Parsek reservation, an extra hire, a stand-in), the curve
  input changes, the hire cost drifts, and the author constant goes stale.
- Each fixture carries the recording schema generation stamp (M-A4, plan section 7)
  and is registered synthetic-provenance in the coverage ledger; a generation bump
  re-produces it via the operator runbook (no in-game harvest needed for a fresh
  career save).

`fixtures/saves/fresh-career` is the save the ALREADY-COMMITTED B10 spec references;
creating it here closes B10's dangling fixture reference as a side effect (Edge
Cases). It must be the SAME `fresh-career` B10 loads, so B10 and the L1-career scripts
share one fixture.

### STOCK_AWARD_PATTERNS: shipped set is DEAD; the nonzero patterns are DEFERRED, not added

M-B3 does NOT add any `STOCK_AWARD_PATTERNS` entry (a reversal of the naive plan). The
shipped set (`hlib.py:1359`) has three patterns (science-transmit at `hlib.py:1363`,
contract-complete funds at `:1369`, contract-complete rep at `:1375`), and against a REAL
EN log ALL THREE ARE DEAD: every Parsek-emitted `ResearchAndDevelopment ... delta=` line
is `[Parsek]`-tagged and skipped by the capture walk (`hlib.py:1466`), and NO stock line
matches any shipped regex -- the pinned Assembly-CSharp emits zero `delta=` strings, and
stock R&D logs as `[Research & Development]: +N data on ...`, contract completion via the
message system, etc. So a nonzero L1 run captures ZERO awards, the cross-check is a
structural NO-OP, and every nonzero script is GREEN on the save-diff alone. This is SAFE
under save-diff-primary.

The naive fix -- "add a research-spend `tech-unlock` pattern, a facility-upgrade-funds
pattern, a hire-funds pattern" -- would ACTIVELY BREAK the scripts and is DEFERRED. Two
compounding reasons: (1) as written those candidate patterns still match nothing real
(they target `delta=` text stock never emits), so they must first be REWRITTEN against
the actual stock log lines; and (2) the MOMENT a pattern DOES capture a nonzero action's
own award, `unmatched_captured_awards` hard-reds it (`run.py:1333`), and that award's
runtime `seq_key` can never match the `ut=0.0` seam entry -- a STRUCTURAL FALSE-RED.
"Add a `tech-unlock` pattern" is therefore NECESSARY-BUT-NOT-SUFFICIENT: it must be done
TOGETHER with (a) rewriting the pattern to real stock text, (b) SPLITTING a science SPEND
(tech-unlock) from a science CREDIT (transmit) in that text, and (c) the UT-agnostic
single-action match (Deferred Items) that fixes the `seq_key` correlation. Until all
three land, the nonzero capture leg stays a dead no-op and the save-diff carries the
assertion. The candidate emitters, recorded for that FUTURE rewrite (each will be
VERIFY-PENDING-OPERATOR against a live EN KSP.log, matched against REAL stock text, NOT
the `delta=` placeholder):

| Future candidate | kind | facet | stock emitter (to reverse from real EN log) | note |
|------------------|------|-------|---------------------------------------------|------|
| research-spend science DELTA | `tech-unlock` | science | `RDTech.ResearchTech` `AddScience(-cost)` (real line `[Research & Development]: ...`) | must be split from a science CREDIT (transmit) in the real text |
| facility-upgrade funds DELTA | `facility-upgrade` | funds | `SpaceCenterBuilding.UpgradeFacility` `AddFunds(-cost, StructureConstruction)` | reverse the real stock funds-debit line |
| hire funds DELTA | `kerbal-hire` | funds | `Funding.AddFunds(-hireCost, CrewRecruited)` (the debit mirror is `TestCommandKscAction.cs:517-519`) | reverse the real stock funds-debit line |

Where stock is silent (no stable EN line for an action's award), the capture stays
unenumerated and the primary save-diff carries the assertion alone (which it does
regardless); a future in-game capture falls to the RESERVED `gameevents-captured`
provenance (M-B3-adjacent, an event-time subscriber), NEVER a Parsek recalc read.

---

## Behavior

### The L1 script family (one per drivable action per mode)

Each L1 script drives exactly one `KscAction` sub-action and asserts the touched HARD
pool. The manifest entry per action, with the author-constant discipline applied:

#### research-node (module: ScienceModule; facet: science pool, HARD)

- Driver: `KscAction action=research-node node=<techId>` (M-C1). The verb drives
  `RDTech.ResearchTech()` (spends science), confirms `RDTech.OperationResult` /
  node-researched (M-C1 effect confirmation), and reports OK. A committed-node block
  (`TechResearchSpendPatch.cs:17`) surfaces as `REJECTED msg=blocked-committed`
  (driver-INVALID), never a false OK.
- Manifest: `kind = "tech-unlock"`, `science = -<nodeCost>`, provenance seam-declared.
- **Author-constant rule: DETERMINISTIC.** A tech node's science cost is fixed stock
  DATA (the node's `RDNode` cost), independent of live state, so the delta is a literal
  author constant (basicRocketry = -5.0, VERIFY-PENDING-OPERATOR against KSP 1.12.5).
  Per M-B2, the science facet is STATE-DEPENDENT for its stock MAGNITUDE only when the
  amount is a diminishing-returns science CREDIT; a tech-unlock SPEND is a fixed cost,
  so an author constant is correct and required (a null fill-from-capture on the science
  facet is rejected outright by `parse_manifest_entries`, `oracle.py`, the
  state-dependent forbid).
- Expected: `seed.science + (-nodeCost)`; diffed HARD on `sciencePool`. This SAVE-DIFF
  is the sole trusted assertion.
- **Capture note (dead today, latent hazard):** the research SPEND does NOT get captured
  today. The `science-transmit` pattern (`hlib.py:1363`) needs a `ResearchAndDevelopment
  ... delta=` line; the only such lines are Parsek's own, which are `[Parsek]`-tagged and
  skipped by the capture walk (`hlib.py:1466`), and stock logs the spend as
  `[Research & Development]: ...` with no `delta=`. So the capture set is EMPTY, the
  cross-check is a no-op, and the script is GREEN on the save-diff. The hazard is LATENT:
  IF a future rewrite makes a pattern capture the spend, `unmatched_captured_awards`
  hard-reds it (`run.py:1333`) and the spend's runtime `seq_key` never matches the
  `ut=0.0` seam entry (`hlib.py:1508`-`:1526`, `oracle.py:199`) -- a structural
  false-red. So adding a `tech-unlock` pattern is NECESSARY-BUT-NOT-SUFFICIENT: it must
  land TOGETHER with the science-spend-vs-credit text split AND the UT-agnostic match
  (Deferred Items). Until then no pattern is added and the save-diff carries the
  assertion.

#### upgrade-facility (modules: FacilitiesModule + FundsModule; facet: funds pool, HARD)

- Driver: `KscAction action=upgrade-facility facility=<facilityId>` (M-C1). Requires
  SPACECENTER (the funds debit lives in the scene `SpaceCenterBuilding` instance,
  `FacilityUpgradeSpendPatch.cs:15`); the verb resolves the live building and invokes
  the real `UpgradeFacility(bool)` so both the `AddFunds(-cost, StructureConstruction)`
  and `SetLevel(level+1)` are the genuine stock ones.
- Manifest: `kind = "facility-upgrade"`, `funds = -<upgradeCost>`, provenance
  seam-declared. The facility LEVEL change lands on the report-only facility facet;
  the funds spend is the HARD assertion.
- **Facility choice (NIT A-N1): NOT R&D.** Upgrade a NON-R&D facility -- recommend the
  Tracking Station or the VAB. The `science-transmit` capture regex keys on
  `ResearchAndDevelopment`; an R&D-building upgrade log line could collide with it and
  add capture noise. A Tracking-Station / VAB upgrade keeps the funds-debit line clear of
  the R&D emitter. The chosen facility is pinned in the fixture README.
- **Author-constant rule: DETERMINISTIC PER LEVEL.** A facility's upgrade cost is fixed
  data per (facility, level) in the building config (the value stock's
  `SpaceCenterBuilding.GetUpgradeCost()` returns for level 0 -> 1). It is state-INDEPENDENT,
  so an author constant is correct (OQ4 RESOLVED: the per-level cost is fixed data on the
  pinned stock instance, so the author constant is valid). funds is the fill-eligible
  pool-only facet (M-B2), so the author MAY instead leave it null to fill-from-capture,
  but an author constant is preferred here because the cost is fixed and fill-from-capture
  depends on the (VERIFY-PENDING-OPERATOR) facility-upgrade capture pattern existing.
  **Operator confirmation step (fixture creation):** the operator reads the
  `GetUpgradeCost` value off the `observedAfter=` field in the `kscaction
  action=upgrade-facility applied=true` log line during fixture creation and records it as
  the author constant; if any chosen facility's cost turns out GameVariables-scaled rather
  than fixed, treat it as fixture-pinned like hire (below).
- Expected: `seed.funds + (-upgradeCost)`; diffed HARD on `funds`. This SAVE-DIFF is the
  sole trusted assertion.

#### hire-kerbal (modules: FundsModule + KerbalsModule; facet: funds pool, HARD)

- Driver: `KscAction action=hire-kerbal kerbal=<applicantName>` (M-C1). The verb
  mirrors the stock debit `Funding.AddFunds(-hireCost, TransactionReasons.CrewRecruited)`
  (the debit mirror is `TestCommandKscAction.cs:517-519`, NOT `KerbalHirePatch.cs`,
  which is the committed-hire block prefix; the `CrewRecruited` reason key is
  load-bearing: `KscActionExpectationClassifier.cs:140-148` keys the ledger's hire funds
  leg on it) then `KerbalRoster.HireApplicant`, confirming roster membership.
- Manifest: `kind = "kerbal-hire"`, `funds = -<hireCost>`, provenance seam-declared.
  The roster ADD lands on the (deferred, M-B2) world roster sub-facet; the funds spend
  is the HARD assertion.
- **Author-constant rule: STATE-DEPENDENT-BUT-FIXTURE-PINNED.** The hire cost rises with
  the HIRED-CREW COUNT via the `GameVariables` recruit-cost curve -- the curve input is
  the number of already-hired kerbals, NOT the applicant's identity (M-C1 confirms it is
  NOT a hardcoded constant). Funds is the fill-eligible pool-only facet, so the ROBUST
  M-B2 choice would be null-to-fill-from-capture. BUT no hire-funds capture pattern is
  enumerated yet (Data Model), so a null fill would fail ambiguous (zero matches ->
  `parse_manifest_entries` flags it, `oracle.py`). The M-B3 resolution (OQ2 RESOLVED):
  PIN the fixture roster to EXACTLY the 4 stock hired kerbals (zero Parsek reservations /
  stand-ins) so the hire cost is DETERMINISTIC at hired-count 4, and declare it as an
  author constant. This fixture-pinned HIRE constant is the v1 contract. It keeps the
  assertion independent of any capture pattern and independent of Parsek recalc. funds
  stays fill-eligible for LATER: when the hire-funds capture pattern is operator-verified
  the script MAY switch to null-to-fill; until then the fixture-pinned constant is the
  contract. The fixture README records the pinned hired-crew count + the resulting known
  cost so the constant does not go stale silently.
- Expected: `seed.funds + (-hireCost)`; diffed HARD on `funds`. This SAVE-DIFF is the
  sole trusted assertion.

#### dismiss-kerbal (module: KerbalsModule; facet: NONE hard)

- Driver: `KscAction action=dismiss-kerbal kerbal=<name>` (M-C1). Drives
  `KerbalRoster.Remove`; a Parsek-managed kerbal pre-declines `kerbal-parsek-managed`
  (`KerbalDismissalPatch`).
- Manifest: `kind = "kerbal-dismiss"`, ALL pool deltas 0 (a dismissal moves no career
  pool; stock does not refund a hire). The entry exists to record the roster change and
  to assert VIA the zero-delta cross-check that NO pool moved (a dismiss must not
  spuriously credit or debit funds).
- **Assertion:** `expected == seed` on all pools (the dismiss is pool-neutral, the SOLE
  trusted leg), plus the roster removal on the deferred world roster sub-facet. The
  `unmatched_captured_awards`-empty check IS trusted here (unlike the nonzero economic
  scripts): a dismiss fires NO economic award, so the captured set is empty and the
  any-captured-award-reds signal is sound for any `seq_key` -- exactly the B10
  empty-manifest case scoped to a roster mutation (the kerbal-dismiss seam entry declares
  no pool delta, so a stray funds/science/rep capture would be unmatched and red). So the
  dismiss L1 script is effectively a passive-safety cross-check scoped to a roster
  mutation: it proves a KSC roster action does not perturb the economy. This is the
  R6/R7-adjacent "no phantom economy movement on a non-economic action" guard.
- Because the roster world sub-facet is DEFERRED in M-B2 (no `Roster` parse on
  `CareerSaveSnapshot`), the dismiss script's ROSTER assertion is a Deferred item; its
  POOL-neutrality assertion is drivable today. Named honestly in Deferred Items.

### The four L1 items with no seam verb (per-item decision)

The plan's L1 list also names complete-contract, strategy, milestone, and EVA science.
None has an M-C1 verb. Decided per-item, honestly:

- **complete-contract -> DEFER to a KscAction batch-2 sub-action, gated + reasoned
  (OQ3 RESOLVED).** Contract completion normally requires FLYING a mission that
  satisfies the contract's parameters; there is NO headless stock path to LEGITIMATELY
  complete a contract without a vessel meeting its conditions. Resolution: a batch-2
  sub-action `KscAction action=complete-contract contract=<guid>` drives the accepted
  contract's `Contract.Complete()` (PUBLIC; it REQUIRES the contract be in the Active
  state, so a companion `accept-contract` genuinely IS required first -- the verb cannot
  short-circuit an unaccepted contract). `Contract.Complete()` force-fires the REAL
  contract-LEVEL funds+rep+science triple-credit through the stock award path headlessly,
  but WITHOUT the per-parameter rewards (those only accrue in real flight as each
  parameter is met). DOCUMENTED FIDELITY CAVEAT: this exercises `ContractsModule` +
  `FundsModule` + `ReputationModule` reconcile and the R7 no-N-fold-bake guard at the
  contract level; FULL fidelity (per-parameter rewards) stays a FLOWN B-track scenario
  (B6/B7-style). The L-track's ledger cross-check wants the ECONOMIC EFFECT of the
  contract-level credit, not the flight, so accept the `complete-contract` sub-action for
  the L2 triple-credit ledger cross-check + R7 as a batch-2 sub-action, with the caveat
  logged. It is gated on operator-verifying the `ContractSystem completed` funds + rep
  award lines (partly enumerated already, `hlib.py:1369`/`:1375`). This is a KscAction
  batch-2 seam follow-up (Deferred Items), NOT M-B3 script work.
- **strategy -> DEFER to a KscAction batch-2 sub-action.** Strategy ACTIVATION is
  `StrategySystem.Instance.SetStrategyActive`; a batch-2 sub-action
  `KscAction action=activate-strategy strategy=<id> commitment=<0..1>` would drive it.
  Strategy CONVERSION is not a discrete action: it happens as a SIDE EFFECT of other
  awards routed through an active strategy (`StrategiesModule` is the `Transformed`
  class in `KscActionExpectationClassifier.cs`). Per M-B2 edge 16 the captured amount is
  the POST-transform stock magnitude, so conversion is asserted as a byproduct of a
  contract/science award UNDER an active strategy, not by a dedicated verb. Deferred to
  KscAction batch-2 (activate) + L2 (conversion byproduct).
- **milestone -> NOT a KSC action; route to FLOWN B-track.** Milestones (`ProgressTracking`,
  `MilestonesModule`) fire as BYPRODUCTS of flying (first orbit, first Mun landing,
  altitude/speed records). There is no legitimate "cause a milestone" KSC verb, and
  synthesizing one would be a fake. Milestone-fed rewards are exercised by flown B-track
  missions (B2 first-orbit, B7 first-Mun-landing) with the `ProgressTracking` capture
  pattern (VERIFY-PENDING-OPERATOR), asserted at L2. Deferred to B-track + a milestone
  capture pattern.
- **EVA science -> NOT a KSC action; route to FLOWN B-track (B3 EVA).** EVA science needs
  a kerbal on EVA taking a science report: a flown vessel plus EVA control, which the
  plan defers as HARD autopilot (EVA-jetpack). It is a flown B-track concern (B3 EVA) and
  is precisely where the M-B2 roster world sub-facet lands (M-B2 defers the `Roster`
  parse until the first kerbal/EVA scenario needs it). Deferred to B3 + the roster
  sub-facet.

So the M-B3 L1 SCRIPT deliverable is the FOUR drivable sub-actions; the other four L1
items are Deferred with their named seam sub-actions or flown routes.

### The L2 refund-window script (R6) is NOT drivable today -- deferred behind a SaveGame verb

L2 is cross-module / windowed interactions. Of the four L2 examples the plan names
(contract triple-credit, strategy conversion, milestone-fed rewards, facility spend +
refund windows), NONE is drivable with the M-C1 verb set today. The contract / strategy
/ milestone three need the deferred contract / strategy / milestone instruments above.
The fourth -- the facility spend + refund window (R6) -- LOOKS drivable but is NOT,
because the M-C1 verb set cannot PERSIST the upgrade before re-loading, and the R6 guard
only bites on a reload of a save that ALREADY carries the upgrade.

The naive flow `upgrade-facility -> LoadGame -> assert` MANUFACTURES A FALSE SIGNAL:
`upgrade-facility` mutates LIVE career state only (the in-memory `Funding` debit +
`SetLevel`), and there is NO in-process `SaveGame` verb -- the only `SaveGame` lives
inside `FlushAndQuit` (`ParsekTestCommandAddon.cs:1215`), which QUITS. So a `LoadGame`
after the upgrade reloads the PRE-upgrade disk fixture (`LoadGameImpl` reloads disk,
`:1251`): funds snap back to seed, the facility level resets. That is INDISTINGUISHABLE
from the exact phantom-refund the R6 guard exists to catch -- the script would FALSE-RED
on its own missing-persist, or (worse) be tuned to expect the snap-back and thereby
NEVER catch a real refund. Either way it is not a valid R6 test.

Two honest routes, both requiring work M-B3 does not ship:

- **(a) Two-session flow -- a multi-session orchestration dependency the harness does
  NOT support today.** `LoadGame fresh-career -> upgrade-facility -> FlushAndQuit`
  (persists the post-upgrade save, quits), then a SECOND KSP boot `LoadGame <that
  post-upgrade save>` (the reload where BUG-G would refund) `-> FlushAndQuit -> assert`.
  The M-A5 harness runs EXACTLY ONE `runtime.launch` per scenario attempt (`run.py:1941`)
  and stages a PRISTINE fixture per attempt; it does not boot KSP twice within one
  scenario, nor carry one attempt's produced save into the next. So this is a genuine
  MULTI-SESSION orchestration dependency, named honestly as such -- not a today path.
- **(b) A `SaveGame` batch-2 seam verb -- the cleaner named deferral (RECOMMENDED).** A
  standalone in-process `SaveGame` verb makes the whole thing ONE launch:
  `LoadGame fresh-career -> upgrade-facility -> SaveGame (persist post-upgrade) ->
  LoadGame (reload = the R6 window, OnLoad recalc) -> FlushAndQuit -> assert`. This fits
  the existing one-launch-per-attempt model exactly and is a single small verb, versus
  standing up multi-session orchestration.

**Decision: R6 / the one L2 script is DEFERRED behind a named `SaveGame` batch-2 seam
verb (route b), moving L2 to ZERO-drivable-today.** Route (b) is the minimal honest
dependency (one verb inside the existing launch model), and the persist-before-reload
gap is the SAME root cause across both routes. When `SaveGame` lands,
`L2-facility-refund-window.toml` (Career mode) declares the one `facility-upgrade` funds
entry (as in the L1 upgrade script) and asserts that after the persist+reload
`expected == seed + (-upgradeCost)` STILL holds (the spend is NOT refunded); a phantom
refund makes the produced save's funds HIGHER than expected -> hard funds drift ->
PARSEK-FAIL(ledger) (BUG-G, `FacilityUpgradeSpendPatch.cs`). No warp verb is needed (R6
is a scene-change guard, not a warp guard; R1's warp trigger stays deferred with the
`TimeJump`/scene verbs per M-B2).

The three other L2 examples are Deferred:

- **Contract triple-credit (funds + rep + science).** Needs the deferred
  `complete-contract` batch-2 sub-action. When it lands, the L2 script declares THREE
  seam entries at one `ut` (a `contract-complete` funds entry, a `contract-complete` rep
  entry with `rep_mode=nominal` through the curve, and any science leg) and asserts all
  three HARD pools moved by exactly the declared amounts. The oracle already supports
  this (multi-entry `compute_expected`, the reputation curve); only the verb is missing.
- **Strategy currency conversion.** Needs the deferred `activate-strategy` sub-action;
  the conversion is asserted as a post-transform captured byproduct (M-B2 edge 16).
- **Milestone-fed rewards.** Needs a flown B-track milestone + the milestone capture
  pattern.

### The three career-mode script sets (module activation differs)

Straight from the module activation matrix:

- **Career (`fresh-career`)**: the full L1 set (research-node, upgrade-facility,
  hire-kerbal, dismiss-kerbal). All hard pools present. This is the richest mode and the
  one the plan's L1 milestone centers on. (The L2 facility-refund-window is NOT part of
  the Career set today -- it is deferred behind the `SaveGame` verb, above.)
- **Science (`fresh-science`)**: research-node ONLY (the science-facet single-module
  script). Funds and rep pools are ABSENT (`CareerSaveParser` sets `hasFunds`/`hasRep`
  false; the oracle facet-skips them, M-B2 edge 5), so upgrade-facility / hire have no
  funds facet to assert, and (per B-SF3, below) STAY CAREER-only because their `Funding`
  debit is null in Science. **DEPENDENCY (Open Question 1, RESOLVED):** M-C1's
  `CareerPresent` readiness bit gates `research-node` on `Mode == Game.Modes.CAREER`
  (design-autotest-seam-verbs-c1.md, DispatchState). Science mode is `SCIENCE_SANDBOX`,
  so as specified M-C1 DEFERS `research-node` (`career-not-ready`) -> TIMEOUT -> INVALID
  in Science mode, even though R&D and node research are live there. The Science-mode
  research-node script depends on a SEPARATE, SUB-ACTION-SCOPED M-C1 follow-up patch (the
  plan of record, NOT passive-only): widen ONLY the `research-node` sub-action's
  readiness to admit `(Mode == CAREER || Mode == SCIENCE_SANDBOX) &&
  ResearchAndDevelopment.Instance != null` (a `RnDPresent` bit). hire / dismiss /
  upgrade-facility STAY CAREER-only, because `Funding.Instance` is null in Science and
  their funds legs would NRE / be meaningless. **WARNING: do NOT relax the shared
  top-level `Mode` check** that all four sub-actions share -- widen the per-sub-action
  gate for `research-node` ALONE. Passive-only Science is the TEMPORARY FALLBACK if the
  follow-up slips, not the plan; it under-covers the "Science mode has science" axis. This
  is the one M-B3 finding that reaches back into a merged module.
- **Sandbox (`fresh-sandbox`)**: PASSIVE-ONLY, a PURE B10 variant with NO KscAction step.
  No economy pool exists. Sandbox is `Game.Modes.SANDBOX`, so `research-node` (like the
  other CAREER-gated verbs) DEFERS `career-not-ready` -> TIMEOUT -> INVALID -- it NEVER
  reaches a `node-already-unlocked` check, so the Sandbox script does not attempt it. The
  Sandbox "script" is the B10 passive-safety cross-check re-run in Sandbox mode:
  `expected == seed` (all pools absent), no KscAction driven, the empty-manifest
  any-capture-reds cross-check trusted. It proves the ledger stays inert in Sandbox and
  that the facet-skip-when-absent path is correct for a no-pools save.

### Stock-award capture sequencing (the honest ordering)

The capture cross-check (`unmatched_captured_awards`) is, for a NONZERO manifest,
UNTRUSTED. TODAY it captures nothing (the shipped patterns are dead against a real EN
log), so it is an empty no-op and the seam-declared-vs-save diff -- the sole trusted leg
-- carries the assertion. The sequence, honestly:

1. **The primary assertion is the ONLY trusted leg.** For every L1 script the author
   declares the effect as a seam-declared author constant, and `compute_expected` diffs
   it HARD against the produced save. A missing capture pattern cannot hide a real drift
   (the save-diff catches it) and cannot cause a false PASS. This leg carries the entire
   assertion.
2. **The shipped patterns are DEAD, so today the leg is an empty no-op.** The three
   shipped patterns need a `... delta=` line: the only such lines are Parsek's own, which
   are `[Parsek]`-tagged and skipped (`hlib.py:1466`); no stock line matches (the pinned
   Assembly-CSharp has zero `delta=` strings; stock R&D logs `[Research & Development]:
   +N data on ...`, contracts via the message system). So a nonzero L1 run captures ZERO
   awards, `unmatched_captured_awards` is empty, nothing reds -- SAFE under
   save-diff-primary. (B10's EMPTY manifest stays TRUSTED: with no seam entries the
   "any captured award reds" signal is sound; today it also captures nothing.)
3. **The LATENT structural false-red (why a naive pattern rewrite would break the
   scripts).** `unmatched_captured_awards` (`hlib.py:1508`) is WIRED to turn any unmatched
   captured award into a HARD divergence (`run.py:1333`). It matches on
   `(seq_key, kind, identity)`, and a captured award's `seq_key` is its runtime UT
   (nearest preceding `[Parsek] ut=` line, else line ordinal; `hlib.py:1407`) while a
   seam entry carries the author `ut` (typically `0.0`; `oracle.py:199`) -- they never
   match. So the MOMENT a pattern is rewritten to actually capture a nonzero action's own
   award, that award is unmatched and hard-reds: a STRUCTURAL FALSE-RED. Therefore M-B3
   ADDS NO PATTERN. The REAL future work is not "operator-verify the shipped patterns"
   (they are dead) but REWRITING them against real stock log text, and that rewrite is
   NECESSARY-BUT-NOT-SUFFICIENT: it must land TOGETHER with (a) SPLITTING a science SPEND
   (tech-unlock) from a science CREDIT (transmit) in the real text, and (b) the
   UT-agnostic single-action match below that fixes the `seq_key` correlation. Any one of
   the three alone re-introduces the false-red.
4. **DEFERRED: a UT-agnostic single-action match (the mechanism that would make the
   nonzero cross-check trusted).** Two candidate designs, evaluated:
   - **Match on `(kind, identity, roundedAmount)` when EXACTLY ONE seam entry and EXACTLY
     ONE capture of that kind exist.** Drop `seq_key` from the match key in the
     single-action case: if the manifest declares one `tech-unlock` entry and the log
     yields one `tech-unlock` (or coerced) capture of the same rounded amount, pair them
     regardless of UT. SIMPLE and needs no C# change, but only sound in the strict
     one-entry / one-capture case and is fragile if any second same-kind award slips in.
   - **Stamp the `KscAction ... applied=true` log line with `ut=` so captures correlate
     to the ACTION's own UT.** The verb already logs `applied=true observedAfter=`; add
     `ut=<Planetarium.GetUniversalTime()>` to that line, and have the capture correlator
     read the action's stamped UT as the award's `seq_key`, and the seam entry adopt the
     SAME action UT (instead of `0.0`) at accumulation time. This makes `seq_key` match
     legitimately for ANY single action and generalizes to multi-action L2, but needs a
     one-line C# log change plus a correlator tweak in `hlib.parse_stock_award_lines` /
     the manifest accumulator.
   - **Recommendation: the UT-stamped `applied=true` line (candidate 2).** It is robust
     beyond the strict single-action case, generalizes to L2, and keeps amounts out of
     the match key (so a legitimate two-equal-awards case does not collapse). Deferred to
     a batch-2 seam + oracle follow-up, and to be landed IN THE SAME change as the stock
     pattern rewrite (step 3); until then M-B3 adds no pattern, EVERY nonzero script's
     capture leg is a dead no-op, and the save-diff carries the assertion alone.
5. **No pattern is enumerated until the whole rewrite lands.** M-B3 ships zero pattern
   changes. When the future step arrives, each rewritten pattern is VERIFY-PENDING-OPERATOR
   against a live EN log (M-B2 discipline), matched to REAL stock text, and enumerated only
   alongside the spend/credit split and the UT-agnostic match. Until then a script never
   trusts the nonzero capture leg (it cannot -- see steps 2-3).

---

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **research-node spend and the capture patterns.** The shipped `science-transmit`
   pattern needs a `ResearchAndDevelopment ... delta=` line. -> TODAY it captures NOTHING
   from a real EN log: Parsek's own such lines are `[Parsek]`-tagged and skipped
   (`hlib.py:1466`), and stock never emits `delta=` (it logs `[Research & Development]:
   +N data on ...`). So the cross-check is a structural no-op and the research-node script
   is GREEN on the save-diff. The false-red hazard (kind-split AND the A-B1 `seq_key`/UT
   mismatch) is LATENT, not current: it becomes real only when the patterns are REWRITTEN
   to match actual stock text. The REAL M-B3 work item is therefore that pattern REWRITE
   (not "operator-verify the shipped patterns"), and it must solve BOTH hazards together
   -- splitting a science SPEND (tech-unlock) from a science CREDIT (transmit) in the real
   text, AND the UT correlation (the UT-agnostic single-action match) -- or a nonzero
   capture hard-reds (`run.py:1333`). Deferred; until then no pattern is added and the
   save-diff carries the assertion. v1 (capture dead no-op today; rewrite + UT-agnostic
   match deferred).
2. **hire cost drifts because the fixture roster changed.** The hire funds author
   constant is only valid at the fixture's pinned roster size (the `GameVariables`
   recruit-cost curve). -> The fixture README pins the roster + records the resulting
   known cost; a roster drift is caught by the save-diff (the produced funds no longer
   matches the stale constant) and localized by the fixture-authoring INVALID subkind if
   the seed itself changed. Prefer null-to-fill-from-capture once the hire-funds capture
   pattern is operator-verified. v1 (fixture-pinned constant).
3. **facility upgrade cost turns out GameVariables-scaled, not fixed per level.** -> If
   implementation verification finds any facility's cost scaled, treat it as fixture-pinned
   (like hire): the fixture's KSC state fixes the value, declared as a known constant.
   v1 (verify during implementation).
4. **Science mode research-node deferred by M-C1's CAREER-only gate.** M-C1's
   `CareerPresent` gates `research-node` on `Mode == CAREER`; Science mode is
   `SCIENCE_SANDBOX`, so the verb DEFERS `career-not-ready` -> TIMEOUT -> INVALID. -> The
   plan of record (OQ1 RESOLVED) is a SUB-ACTION-SCOPED M-C1 follow-up patch: widen ONLY
   the `research-node` sub-action's gate to `(CAREER || SCIENCE_SANDBOX) &&
   ResearchAndDevelopment.Instance != null` (a `RnDPresent` bit); hire / dismiss /
   upgrade stay CAREER-only (Funding null in Science). Do NOT relax the shared top-level
   Mode check. Passive-only Science is the TEMPORARY fallback, not the plan. v1
   (sub-action-scoped M-C1 dependency, plan of record).
5. **Sandbox research-node: the verb DEFERS, it is not a clean node-already-unlocked
   reject.** Sandbox is `Game.Modes.SANDBOX`, which is NOT CAREER, so `research-node`
   DEFERS `career-not-ready` -> TIMEOUT -> INVALID -- it never reaches the
   `node-already-unlocked` check. -> The Sandbox script therefore drives NO KscAction at
   all; it is a PURE B10 passive variant (`expected == seed`, all pools absent, no verb
   step). v1.
6. **dismiss-kerbal moves no pool but the roster sub-facet is unparsed.** -> The
   pool-neutrality assertion (`expected == seed` on all pools + empty capture) is drivable
   today and is the R6/R7-adjacent guard (its empty-capture leg is TRUSTED like B10, since
   a dismiss fires no economic award); the ROSTER-changed assertion is Deferred with the
   M-B2 world roster sub-facet (`CareerSaveSnapshot` has no roster). v1 (pool-neutral part);
   deferred (roster part).
7. **Sandbox / Science pool absence reds nothing.** `CareerSaveParser` sets the absent
   `hasX` flags false; the seed and the oracle facet-skip the absent pools (M-B2 edge 5).
   -> A Science-mode funds assertion is impossible (no pool), so Science scripts only
   assert science; a Sandbox script asserts no pool. Never a false red on a missing pool.
   v1.
8. **Fixture seed unparsable or wrong mode for the script.** A `fresh-science` fixture
   used by a funds-asserting script, or a template the pre-launch analyzer cannot parse.
   -> M-B2's seed path distinguishes: `parsed=true` but the required `hasX=false` ->
   INVALID(fixture-authoring); a missing/`parsed=false` block -> INVALID(tooling). Never a
   false PASS. The M-B3 fixture-per-mode discipline (a funds script only ever loads
   `fresh-career`) prevents the mismatch by construction. v1.
9. **`fresh-career` fixture missing on disk.** The committed B10 spec already references
   `fixtures/saves/fresh-career`, which does not exist yet; every career L1 script and B10
   fail-stage without it. -> M-B3's operator runbook CREATES `fresh-career` (Data Model),
   closing B10's dangling reference. The three fixtures are the first committed career
   fixtures. v1 (this module supplies them).
10. **Research node cost changes across a KSP update.** The author constant is stock DATA
    that a KSP version could change. -> The instance profile pins KSP 1.12.5; the fixture
    carries the schema generation stamp; a KSP version change is out of contract until the
    fixture is re-produced. The author constant cites the node id + the pinned version.
    v1 (KSP-version-pinned).
11. **Two L1 actions in one run (accidental multi-effect).** A script that drives two
    KscActions would need two manifest entries. -> Each L1 script drives EXACTLY ONE
    sub-action by design (single-module discipline); a multi-action script is an L2 concern
    with an entry per effect. The oracle sums them deterministically by `(ut, seq)`. v1
    (L1 = single action).
12. **Nonzero capture leg contributes no trusted signal.** A nonzero script's capture
    cross-check is UNTRUSTED. -> Today it captures nothing (shipped patterns dead), so it
    is an empty no-op and the script runs on the primary save-diff alone (the valid ledger
    assertion). It is never a pass gate, and M-B3 adds no pattern that would make it one
    (that needs the deferred rewrite + UT-agnostic match). v1 (primary assertion stands
    alone).
13. **R6 refund window needs a persist-before-reload the M-C1 verb set lacks.** ->
    `upgrade-facility` mutates LIVE state only; the sole `SaveGame` is inside `FlushAndQuit`
    (which quits), so `upgrade -> LoadGame` reloads the PRE-upgrade fixture and manufactures
    a false phantom-refund signal. R6 is DEFERRED behind a named `SaveGame` batch-2 seam
    verb (in-process `upgrade -> SaveGame -> LoadGame -> assert`), the minimal honest
    dependency vs multi-session orchestration (Behavior "The L2 refund-window script").
    No warp verb is needed once `SaveGame` lands (R6 is a scene-change guard; R1's warp
    trigger is a separate deferred surface, M-B2). deferred (SaveGame verb).
14. **R7 N-fold contract bake needs contract completion (deferred).** The R7 guard (no
    N-fold stock contract reward bake) needs a real contract completion, which has no M-C1
    verb. -> R7 is Deferred to the `complete-contract` batch-2 sub-action; until then the
    dismiss-kerbal pool-neutrality script and B10 are the closest passive economy guards.
    Deferred.
15. **KILLED run mid-action.** A watchdog kill mid-`KscAction`. -> The ledger-oracle
    verifier is SKIPPED on any KILLED attempt (M-B2 edge 11); the scenario retries from a
    PRISTINE re-staged fixture (M-C1 at-most-once), never re-driving a half-applied action.
    v1.
16. **A KscAction refusal (insufficient science/funds, unknown id).** The fixture did not
    afford the action or the id was wrong. -> M-C1 REJECTED -> driver-INVALID (retry-once),
    NEVER PARSEK-FAIL. The fixture seed is sized above the scripted spends precisely to
    avoid this; a genuine affordability refusal is a fixture-authoring defect, not a ledger
    fault. v1.
17. **Guard-blocked action (committed target).** A `KscAction` whose stock call a
    committed-action guard patch silently blocks. -> M-C1's effect confirmation reports
    `REJECTED msg=blocked-committed` (driver-INVALID), never a false OK; a fresh fixture has
    no committed actions, so this does not arise in M-B3 scripts (named for completeness).
    v1.
18. **Manifest entry with a null amount on the science facet.** An author leaves the
    tech-unlock science delta null intending fill-from-capture. -> `parse_manifest_entries`
    REJECTS it (science is state-dependent; fill forbidden, `oracle.py`), which M-B2 edge 18
    routes to a HARD divergence (a dropped expected effect can false-PASS). The research-node
    script MUST carry the author constant. v1.

---

## What Doesn't Change

- **No Parsek gameplay, recalc, or ledger code changes.** The nine modules,
  `LedgerOrchestrator`, `KspStatePatcher`, `KscActionReconciler`,
  `KscActionExpectationClassifier`, ERS/ELS routing, `CareerSaveParser`, and the
  ground-truth harness are consumed as-is. M-B3 causes real career actions through the
  UNCHANGED M-C1 verbs, which call the exact stock entry points a player click uses.
- **No oracle math change.** `compute_expected` already sums nonzero funds / science
  deltas and applies the reputation curve (`oracle.py:590`); M-B3 is the FIRST caller with
  a non-empty manifest but adds no new computation. The author-constant vs fill rules, the
  facet policy, the state-dependent forbid, and `unmatched_captured_awards` are all M-B2's.
  M-B3 does NOT change `unmatched_captured_awards`; it recognizes that its
  `(seq_key, kind, identity)` match leaves the nonzero capture leg untrusted, and that the
  shipped patterns are dead against a real EN log (so today the leg is an empty no-op). The
  pattern rewrite + UT-agnostic single-action match that would make it trusted are a named
  DEFERRED follow-up, not an M-B3 change.
- **No manifest schema change.** The `[expectations.ledger]` block, the entry kinds
  (`tech-unlock` / `facility-upgrade` / `kerbal-hire` / `kerbal-dismiss` all already in
  `KINDS`, `oracle.py:69`), and `schema = 1` are unchanged; M-B3 populates entries the
  schema already defines.
- **No seam verb change (M-B3 itself adds no verb; it NAMES deferred ones).** `KscAction`,
  `TimeJump`, and their dispatch gates are M-C1's; M-B3 authors driver STEPS that invoke
  them and adds no verb. The reach-back it depends on is the SUB-ACTION-SCOPED Science-mode
  readiness widening for `research-node` ALONE (OQ1, plan of record NOT optional -- passive
  Science is only a temporary fallback), an M-C1 FOLLOW-UP. M-B3 also NAMES (but does not
  build) the deferred `SaveGame` batch-2 verb the L2 refund window needs, and the batch-2
  `accept-contract` / `complete-contract` / `activate-strategy` sub-actions the other L2
  interactions need. Those are batch-2 seam follow-ups, not M-B3 changes.
- **M-B3 touches NO `STOCK_AWARD_PATTERNS`.** The shipped set is dead against a real EN
  log (captures nothing), so the nonzero scripts are green on the save-diff without any
  pattern. Adding a pattern that captured a nonzero action's own award would false-red it
  (`run.py:1333`; the award's runtime `seq_key` never matches the `ut=0.0` seam entry), so
  the tech-unlock / facility-upgrade-funds / hire-funds patterns are DEFERRED, to be
  REWRITTEN against real stock text alongside the spend/credit split and the UT-agnostic
  match (Behavior sequencing). M-B3 ships zero C#/Python code changes to the capture set.
- **The harness verdict taxonomy, verifier chain, and B10 flagship are unchanged.** The
  `ledger_drift -> PARSEK-FAIL(ledger)` branch and the `ledgerOracle` slot are M-B2's; B10
  stays the zero-delta flagship and M-B3's fixtures give B10 the `fresh-career` save it
  already references.

---

## Backward Compatibility

- **Greenfield scenarios and fixtures.** The L1/L2 specs are new declarative files; a
  harness that predates them ignores them. They carry `schema = 1`; a schema bump makes the
  harness refuse an old spec with a clear message (the no-migration stance), never mis-parse.
- **The manifest entries use only kinds the frozen M-B2 schema already defines.** A scenario
  authored now needs no format change when a later capture pattern lands (the capture is a
  run-time cross-check, not a spec-format concern).
- **The career fixtures carry the recording schema generation stamp (M-A4).** A generation
  bump flags them stale and the operator runbook re-produces them (a fresh career save is
  synthetic-provenance, not harvested, so it regenerates by script/operator, not by a green
  harness run). The analyzer refuses a mismatched fixture loudly (plan section 7).
- **`STOCK_AWARD_PATTERNS` is unchanged by M-B3** (and any FUTURE addition is additive). An
  older harness that lacks a pattern simply captures fewer awards (the primary save-diff is
  unaffected); a newer harness reading an older run's log captures what its patterns match.
  No versioned wire artifact changes.
- **No recording, save, tree, ledger, or `.sfs` field changes.** M-B3 mutates only the live
  career state the four real actions already mutate through their existing paths.

---

## Diagnostic Logging

M-B3 introduces no new subsystem tag. It relies on THREE existing logging surfaces, and its
Test Plan asserts the lines that prove an L1 script executed and was accounted:

- **The harness ledger-oracle log (`[Harness][...][SEED|CAPTURE|ORACLE|DIFF|VERIFY]`,
  M-B2 Diagnostic Logging).** For a nonzero L1 script the load-bearing lines are:
  `[SEED] ... funds=<f> science=<s> rep=<r>` (the fixture seed leg), `[CAPTURE]
  manifest-capture ... seamDeclared=1 accumulated=<total>` (the one declared entry),
  `[ORACLE] oracle-expected science=<seed-nodeCost>` (the nonzero expected), `[DIFF]
  ledger-diff facet=sciencePool expected=<e> parsed=<p> delta=<d> within=true hard=true`
  (the HARD assertion), and `[VERIFY] verify ledgerOracle status=PASS hardDivergences=0`.
  A drift emits `[DIFF] ledger-drift facet=<facet> expected=<e> parsed=<p> utWindow=[..]`.
- **The M-C1 seam action log (`[Parsek][INFO][TestCommands] kscaction action=<action>
  target=<target> applied=true manifestKind=<kind> observedAfter=<value>`, M-C1 Diagnostic
  Logging).** Proves the verb ran and confirmed its effect. A refusal emits
  `kscaction refused action=<action> reason=<...>`.
- **The Parsek recorder-observer lines the real stock handlers emit** (M-C1 Test Plan,
  `verify-harness-seeder-mutation` discipline): `OnTechnologyResearched` /
  `OnCrewmemberHired` / `onKerbalRemoved` (`GameStateRecorder.cs:315`/`:323`/`:324`) and the
  facility POLLING line (`GameStateFacilityRecorder`). Asserting the OBSERVER line (not the
  guard-patch line) proves the stock action reached Parsek's recorder; a guard-patch line
  would mean the call was BLOCKED (a REJECTED, not an OK).

New harness-side logging M-B3 adds (in the L1 runbook / result, not new Parsek tags):
- A one-line `[Harness][INFO][FIXTURE] fixture=<mode> seedFunds=<f> seedScience=<s>
  pinnedRoster=<n> hireCostConstant=<c>` when a fixture is staged, so a stale author constant
  is diagnosable from the harness log alone.
- A `[Harness][INFO][CAPTURE] nonzero capture leg UNTRUSTED (shipped patterns dead ->
  captured=0): primary=save-diff` line on every nonzero L1 run, so the log is explicit that
  the capture cross-check is not the pass authority for that run (the `[VERIFY] status=PASS`
  is decided by `hardDivergences=0` from the save-diff alone) and that nothing was captured.
- A `[Harness][WARN][CAPTURE] captured=<n>>0 on a nonzero run -- unexpected, patterns should
  be dead` line if a nonzero run EVER captures an award (a regression signalling a pattern has
  started matching real stock text without the deferred UT-agnostic match, the latent
  false-red).

Goal (M-B2's, extended): reading only the harness log, a developer reconstructs the fixture
seed, the one declared effect, the expected pool, the diff (the sole PASS/FAIL authority),
and that the nonzero capture leg captured nothing, without rerunning the scenario.

---

## Test Plan

Every test states the regression it catches. The pure decision logic M-B3 EXERCISES already
has M-B2 / M-C1 pytest coverage; M-B3 adds the NONZERO-manifest coverage those modules never
exercised, the fixture-seed cross-lane tests, and the live PENDING-OPERATOR runbook (an agent
cannot pilot KSP, MEMORY: in-game-sweep-needs-operator).

### Pure Python (pytest, no KSP; `harness/lib/test_oracle.py` + `test_hlib.py` additions)

- **Nonzero single-entry expected (science spend).** `compute_expected(seed,
  [tech-unlock science=-5])` returns `seed.science - 5` on `sciencePool`, HARD; the diff
  against a save carrying `seed.science - 5` PASSES, against `seed.science` (an unspent /
  refunded node) reds hard. Fails if the first nonzero manifest cannot be computed or a
  science over/under-credit reads green (the R7-class economy-drift silent pass).
- **Nonzero single-entry expected (funds spend).** `compute_expected(seed, [facility-upgrade
  funds=-<cost>])` returns `seed.funds - cost`; a phantom refund (save funds HIGHER than
  expected) reds hard on `funds` with the UT window named. Fails if a BUG-G phantom refund
  reads green. NOTE: the ORACLE MATH here is sound and tested, but DRIVING the R6 refund
  window is DEFERRED (needs the `SaveGame` verb; Behavior "The L2 refund-window script"), so
  this pytest exercises the oracle, not a live L2 run.
- **State-dependent facet forbid on tech-unlock null.** A `tech-unlock` entry with a null
  `science` amount is REJECTED by `parse_manifest_entries`; the reject becomes a HARD
  divergence (M-B2 edge 18), never a dropped effect. Fails if a null science delta silently
  passes.
- **Shipped patterns are dead against real stock text (the safety property).** A synthetic
  KSP.log body carrying REAL stock-style lines (`[Research & Development]: +5 data on ...`,
  a `[Parsek]`-tagged `ResearchAndDevelopment ... delta=` line) yields ZERO captures from
  `parse_stock_award_lines`: the stock line matches no shipped pattern, and the Parsek line
  is skipped (`hlib.py:1466`). So a nonzero run's `unmatched_captured_awards` is EMPTY and
  the verdict is the save-diff alone. Fails if a shipped pattern starts matching real stock
  text (a regression that would re-introduce the false-red).
- **The LATENT false-red is characterized (guard against a naive pattern add).** GIVEN a
  hypothetical captured award at a NONZERO `seq_key` and a seam entry at `ut=0.0`,
  `unmatched_captured_awards` returns it (non-empty) and the verifier turns it into a HARD
  divergence -> FAIL. This test documents WHY M-B3 adds no pattern: a pattern that captured
  a nonzero action's own award WOULD false-red. Fails if the wiring silently stops
  hard-reding an unmatched award (which would hide the hazard). Separately, the B10
  EMPTY-manifest case: any captured award reds (trusted). Fails if B10's any-capture-reds
  signal is lost.
- **hire fixture-pinned constant.** `compute_expected(seed, [kerbal-hire funds=-<pinnedCost>])`
  == `seed.funds - pinnedCost`; a save at a DIFFERENT hire cost (roster drift) reds. Fails if
  a stale hire constant reads green.
- **Sandbox / Science pool-absence.** A `fresh-sandbox` seed (all `hasX` false) yields
  `expected == seed` skipping all pools; a `fresh-science` seed asserts science only. Fails if
  a no-pools mode reds on an absent pool.

### `STOCK_AWARD_PATTERNS` (M-B3 adds NONE; these are the FUTURE-rewrite tests)

M-B3 ships no pattern change, so it adds no pattern-parse test. The tests below belong to
the DEFERRED rewrite step (real stock text + spend/credit split + UT-agnostic match) and are
recorded here so that step is not authored blind:

- **tech-unlock science-spend pattern (future).** A synthetic line matching the REAL stock
  research-spend text (NOT the `delta=` placeholder) parses to a `tech-unlock`
  `CapturedAward` with the negative science delta AND is distinguished from a science CREDIT
  (transmit) line; a running-BALANCE line is rejected (M-B2 balance rule). Fails if the
  spend is mis-kinded as a credit or a balance line is admitted.
- **facility / hire funds patterns (future).** A synthetic line matching the REAL stock
  funds-debit text parses to the right kind/facet DELTA. Fails if a funds balance line is
  captured as a delta.
- **UT-agnostic single-action match (future).** With the recommended ut-stamped
  `applied=true` correlation, a single captured award and a single seam entry pair on the
  ACTION UT so the cross-check goes green on a correct run and reds on a wrong-amount run.
  Fails if the correlation still keys on the `ut=0.0`-vs-runtime mismatch.

### Fixture-seed cross-lane (pytest + xUnit boundary)

- **Fixture seed round-trips through both parsers.** The `fresh-career` careerSave block
  drives BOTH `oracle.parse_seed_baseline` and `hlib.parse_career_save_block` to the same
  pool values (the M-B2 single-seed contract). Fails if a fixture's seed desyncs the two legs.
- **Fixture mode selects the right facets.** `fresh-science` yields `hasFunds=false
  hasScience=true`; `fresh-sandbox` yields all false; `fresh-career` all true. Fails if a
  fixture's GAME-node mode does not map to the expected facet presence.

### PENDING-OPERATOR live runbook (an agent cannot pilot KSP)

On a provisioned stock-minimal EN instance, per L1 script:

1. **Create the three fixtures (mechanical from Data Model).** Start a fresh Career / Science /
   Sandbox game, set the KSC to the specified clean state (no craft, level-0 facilities, start
   node only, pinned roster of exactly the 4 stock hired kerbals + one known applicant for
   Career), set the known seed pools, save, and stage the save as `fixtures/saves/fresh-<mode>`
   with the schema generation stamp. Record the pinned hired-crew count + the resulting hire
   cost + the chosen non-R&D facility's `GetUpgradeCost` (read off `observedAfter=`) in the
   fixture README.
2. **research-node (Career).** Run `L1-research-node-career`: confirm the harness stages
   `fresh-career`, boots, runs `KscAction research-node node=basicRocketry` (grep
   `kscaction action=research-node ... applied=true` + `OnTechnologyResearched`), computes
   `expected = seed.science - 5` (VERIFY the basicRocketry cost on the pinned KSP 1.12.5 tech
   tree during fixture creation), diffs clean HARD, writes `ledgerOracle status=PASS`. Confirm
   the capture set is EMPTY (grep the KSP.log: stock logs `[Research & Development]: +... data`
   with no `delta=`, so nothing is captured; the cross-check is a no-op). Do NOT add a capture
   pattern -- the pattern rewrite is deferred (Behavior sequencing steps 3-5). Negative control:
   hand-edit the produced save's R&D science to an unspent value; re-run just the verifier;
   confirm PARSEK-FAIL(ledger) with `sciencePool` named.
3. **upgrade-facility (Career).** Run `L1-upgrade-facility-career` in SPACECENTER on a NON-R&D
   facility (Tracking Station / VAB): confirm the funds pool dropped by the per-level cost (grep
   the facility POLLING observer line + `kscaction action=upgrade-facility applied=true
   observedAfter=<cost>`), `expected = seed.funds - cost` diffs clean. The L2 refund-window
   (R6) is NOT run here -- it is DEFERRED behind the `SaveGame` verb (Behavior "The L2
   refund-window script"); do NOT run an `upgrade -> LoadGame -> assert` script, which would
   reload the pre-upgrade fixture and manufacture a false phantom-refund signal.
4. **hire-kerbal / dismiss-kerbal (Career).** hire: confirm funds dropped by the pinned hire
   cost (grep `OnCrewmemberHired`), `expected = seed.funds - pinnedCost` diffs clean. dismiss:
   confirm NO pool moved (`expected == seed`, empty capture; grep `onKerbalRemoved`), the
   pool-neutrality guard. The roster-changed assertion is Deferred (M-B2 roster sub-facet).
5. **research-node (Science).** Requires the SUB-ACTION-SCOPED M-C1 readiness widening for
   `research-node` (OQ1, plan of record). With it, run `L1-research-node-science` (confirm the
   verb fires in `SCIENCE_SANDBOX`, science drops by the node cost, no funds/rep facet
   asserted). Until it lands, record Science-mode L1 as passive-only (temporary fallback).
6. **Sandbox passive-only.** Run `L1-passive-sandbox` (a PURE B10 variant, NO KscAction step):
   confirm no pool exists, `expected == seed`, the empty-manifest any-capture-reds cross-check
   trusted, PASS. (`research-node` is NOT driven -- in `SANDBOX` it would DEFER career-not-ready
   -> INVALID, never a clean node-already-unlocked reject.)

Grep evidence per run: the `[Harness][...][SEED|CAPTURE|ORACLE|DIFF|VERIFY]` lines, the
`kscaction ... applied=true manifestKind=<kind>` line, the matching `GameStateRecorder`
observer line (NOT a guard-patch line), and the `ledgerOracle status=PASS` result. This is the
FIRST automated nonzero L1 ledger cross-check the plan section 6 L1 milestone names.

### Synthetic recordings

None. L1/L2 scripts are recording-free (`count = {min = 0, max = 0}`); the assertion is the
career ledger, not a trajectory. The B10 recording-free log-validation suppression (REC-001/003)
applies unchanged.

---

## Deferred Items and Open Questions

Recorded so they are not lost; none blocks the four drivable L1 scripts. (No L2 script is
drivable today -- the one L2 candidate, R6, is deferred behind the `SaveGame` verb below.)

- **The R6 L2 facility-refund-window script (behind a `SaveGame` batch-2 seam verb).** The
  M-C1 verb set cannot persist an upgrade before re-loading (the only `SaveGame` is inside
  `FlushAndQuit`, which quits), so R6 is not drivable today; the naive `upgrade -> LoadGame ->
  assert` manufactures a false phantom-refund signal. Deferred behind a standalone in-process
  `SaveGame` verb (the minimal honest dependency vs multi-session orchestration; Behavior "The
  L2 refund-window script"). When it lands, `L2-facility-refund-window` runs `upgrade ->
  SaveGame -> LoadGame -> FlushAndQuit -> assert no phantom refund`.
- **The nonzero capture cross-check (pattern rewrite + UT-agnostic single-action match).** The
  shipped `STOCK_AWARD_PATTERNS` are dead against a real EN log, so today the nonzero capture
  leg is an empty no-op and the save-diff carries every assertion. Making the leg TRUSTED needs
  a single combined follow-up: REWRITE the patterns against real stock log text (`[Research &
  Development]: ...`, the real funds-debit lines), SPLIT a science SPEND (tech-unlock) from a
  science CREDIT (transmit) in that text, AND land a UT-agnostic single-action match
  (recommended: stamp the `KscAction ... applied=true` line with `ut=` so a capture correlates
  to the action's own UT and the seam entry adopts the same UT). All three must ship together
  or a nonzero capture hard-reds (`run.py:1333`). Deferred to a batch-2 seam + oracle follow-up.
- **The four no-seam-verb L1 items.** complete-contract and strategy defer to a KscAction
  BATCH-2 seam follow-up (`KscAction action=accept-contract contract=<guid|type>` +
  `complete-contract contract=<guid>`, force-firing the CONTRACT-LEVEL triple-credit through
  the public `Contract.Complete()` -- which REQUIRES the Active state, so accept-contract is
  genuinely required first -- WITHOUT the per-parameter rewards, the documented fidelity caveat;
  `activate-strategy strategy=<id> commitment=<0..1>`), gated on the M-B2 oracle's
  contract/strategy facets being operator-verified end-to-end. milestone and EVA science are NOT
  KSC actions: milestone-fed rewards defer to FLOWN B-track missions (B2/B7) + a
  `ProgressTracking` capture pattern; EVA science defers to B3 EVA + the M-B2 roster world
  sub-facet.
- **The three L2 cross-module interactions besides facility-refund.** Contract triple-credit
  (funds + rep + science at one UT, three seam entries, the reputation curve on the rep leg),
  strategy currency conversion (post-transform captured byproduct, M-B2 edge 16), and
  milestone-fed rewards all defer with the contract / strategy / milestone instruments above.
  The oracle already supports multi-entry expected and the curve; only the driving verbs /
  flown scenarios are missing.
- **The roster world sub-facet.** The dismiss-kerbal and hire-kerbal ROSTER assertions defer
  with M-B2's single additive `Roster` parse on `CareerSaveSnapshot`; their POOL assertions are
  drivable today. B3 EVA is the first scenario that forces the roster parse to land.
- **The full stock-award capture enumeration.** Subsumed by the pattern-rewrite item above:
  the facility-upgrade-funds, hire-funds, and research-spend `tech-unlock` patterns are NOT
  added by M-B3 (adding one would false-red its own script), and land only as part of the
  combined rewrite + UT-agnostic match. Where stock is silent, the capture waits for the
  RESERVED `gameevents-captured` in-game subscriber, never a Parsek recalc read.
- **L3-L5 (M-C3 / M-D1).** Actions x recording lifecycle (commit / discard / auto-merge; the
  Rec-3 residual carve-out), actions x rewind/re-fly (tombstones, supersede flips, timeline
  layering), and the L5 grand oracle career run all build on the M-B3 nonzero-manifest scripts +
  the fixtures here. `rec3CarveOut` and the UT-window machinery (M-B2) are their seams. Deferred
  with M-C3 / M-D1.

### Open Questions (RESOLVED by the review panel)

1. **Science-mode research-node readiness gate. RESOLVED.** The plan of record is a
   SUB-ACTION-SCOPED M-C1 follow-up patch that M-B3's Science script depends on: widen ONLY the
   `research-node` sub-action to admit `(Mode == CAREER || Mode == SCIENCE_SANDBOX) &&
   ResearchAndDevelopment.Instance != null`. hire / dismiss / upgrade STAY CAREER-only because
   `Funding` is null in Science. WARNING: do NOT relax the shared top-level Mode check; widen the
   per-sub-action gate for `research-node` alone. Passive-only Science is the TEMPORARY fallback,
   not the plan of record.
2. **Author constant vs fill-from-capture for the funds facet. RESOLVED.** The fixture-pinned
   HIRE constant is the v1 contract (the roster is pinned to exactly the 4 stock hired kerbals,
   so the recruit-cost curve yields a deterministic cost at hired-count 4). funds stays
   fill-eligible for later: the script MAY switch to null-to-fill once a hire-funds capture
   pattern is operator-verified (part of the deferred pattern rewrite). Until then the
   fixture-pinned constant holds.
3. **complete-contract fidelity. RESOLVED.** Accept the batch-2 `complete-contract` sub-action
   for the L2 ledger cross-check + R7. The public `Contract.Complete()` requires the Active state
   (so accept-contract is genuinely required first) and force-fires the REAL contract-LEVEL
   funds+rep+science triple-credit headlessly, WITHOUT the per-parameter rewards. Documented
   fidelity caveat: per-parameter rewards accrue only in real flight, so FULL fidelity stays a
   flown B-track scenario; the contract-level economic effect is real and sufficient for the
   ledger cross-check + R7 at L2.
4. **Which facility to upgrade, and whether its per-level cost is fixed. RESOLVED.** Upgrade a
   NON-R&D facility (Tracking Station or VAB; NIT A-N1 -- avoid an R&D upgrade line colliding
   with the `science-transmit` regex). Its per-level cost is fixed stock DATA on the pinned
   instance, so the author constant is valid; the operator CONFIRMS it during fixture creation by
   reading `GetUpgradeCost` off the `observedAfter=` field of the `kscaction upgrade-facility
   applied=true` log line and recording it in the fixture README. If any chosen facility turns
   out GameVariables-scaled, fall back to a fixture-pinned constant (Edge Case 3).
