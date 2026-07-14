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
math. The one code touch it may need is additive `STOCK_AWARD_PATTERNS` entries in
`hlib.py`, each VERIFY-PENDING-OPERATOR, and only to strengthen the capture
cross-check (Behavior "Stock-award capture sequencing").

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
   cross-check (`hlib.py:1508`) reds a captured award the spec never declared. The v1
   `STOCK_AWARD_PATTERNS` set (`hlib.py:1359`) is three EN-pinned patterns, all still
   VERIFY-PENDING-OPERATOR, none of which matches a research SPEND, a facility-upgrade
   funds debit, or a hire debit. A research-node script can FALSE-RED if the existing
   `science-transmit` pattern captures the research spend as the wrong kind. The
   capture set must be sequenced before a nonzero script can trust the cross-check.

M-B3 supplies: the L1 script family (the four M-C1 sub-actions x three career modes),
the one L2 script the M-C1 verb set can drive today (facility spend + scene-change
refund window, R6), the three career fixtures with named contents, the manifest
author-constant discipline applied per action, and the capture-pattern sequencing.
It names the rest (contract triple-credit, strategy conversion, milestone-fed
rewards, EVA science, and L3-L5) as Deferred with the seam sub-actions or flown
scenarios they need.

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
| **Nonzero-manifest cross-check** | The M-B3 assertion pair: (a) `compute_expected(seed, seam_entries)` == parsed save on the touched HARD pool, and (b) `unmatched_captured_awards` empty (no undeclared award fired). Both must hold. |

Design-concept-to-implementation mapping (names diverge across the seam and the
nine modules):

| Design concept (this doc) | Implementation |
|---------------------------|----------------|
| research-node script | `KscAction action=research-node` (M-C1) -> `RDTech.ResearchTech` (`TechResearchSpendPatch.cs:17`); ledger via `ScienceModule` |
| upgrade-facility script | `KscAction action=upgrade-facility` -> `SpaceCenterBuilding.UpgradeFacility(bool)` (`FacilityUpgradeSpendPatch.cs:15`); ledger via `FacilitiesModule` + `FundsModule` |
| hire-kerbal script | `KscAction action=hire-kerbal` -> `Funding.AddFunds(-hireCost, CrewRecruited)` + `KerbalRoster.HireApplicant` (`KerbalHirePatch.cs:19-22`); ledger via `FundsModule` + `KerbalsModule` |
| dismiss-kerbal script | `KscAction action=dismiss-kerbal` -> `KerbalRoster.Remove` (`KerbalDismissalPatch`); ledger via `KerbalsModule` |
| the ledger cross-check | `oracle.compute_expected` + `oracle.diff_expected_vs_parsed` + `hlib.unmatched_captured_awards` |
| the nine modules | `Source/Parsek/GameActions/{Funds,Science,Reputation,Milestones,Facilities,Contracts,Strategies,Route}Module.cs` + `Source/Parsek/KerbalsModule.cs` |

---

## Mental Model

An L1 script is one turn of the M-B2 loop with a NONZERO manifest. The seed comes
from the fixture; the driver causes exactly one real career effect through a
`KscAction` verb; the DRIVER declares that effect as a seam-declared manifest entry;
the oracle proves the produced save moved by exactly the declared amount and that no
other award fired.

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
        |          + stock-log capture (cross-check ONLY, not summed)       |
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
              expected == parsed on the touched HARD pool  AND
              unmatched_captured_awards empty                  -> PASS
              hard-pool drift  -> PARSEK-FAIL(ledger), UT window named
              undeclared award captured  -> PARSEK-FAIL(ledger)
```

Three invariants carry over from M-B2 and shape every M-B3 decision:

- **The oracle never reads a Parsek-computed number.** The declared delta is an
  author constant from stock DATA (the node's science cost, the facility's per-level
  funds cost) or a fixture-pinned known cost (hire at a pinned roster size), never
  `Funding.Instance.Funds` after the action or any recalc output. `compute_expected`
  consumes the seam-declared entry only (`oracle.py:590`); the captured award is a
  belt-and-suspenders cross-check, never summed (self-cancellation defense, M-B2
  Accumulation).

- **The two legs cannot cancel.** If the save moved by the declared amount and the
  capture saw exactly that award, both legs agree -> PASS. If the save moved by a
  WRONG amount (Parsek over/under-credited), leg B drifts from the author constant ->
  hard drift. If an EXTRA award fired that the script never declared,
  `unmatched_captured_awards` reds it (M-B2 edge 4). A missed capture pattern cannot
  hide a real drift (the save-diff still catches it); but a MIS-firing capture (the
  wrong-kind hazard below) can FALSE-red, which is why the capture set is sequenced.

- **Module activation is the fixture's mode.** In Career all nine modules can move;
  in Science only `ScienceModule` (research spend) is assertable and there are no
  funds/rep pools; in Sandbox no economy pool exists, so every KscAction either
  refuses (all tech already unlocked) or produces no pool delta. The mode-specific
  script set falls straight out of this.

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
| 2 | `ScienceModule` | science transmit/recover, tech-unlock spend | tech-unlock spend (M-C1 research-node) | yes | yes | all-unlocked |
| 3 | `ReputationModule` | contract complete rep, milestone rep, tombstone penalty | no (contract/milestone deferred) | yes | no pool | no pool |
| 4 | `MilestonesModule` | `ProgressTracking` (first orbit, first landing, records) | no (flown-only byproduct) | flown | flown | flown |
| 5 | `FacilitiesModule` | `SpaceCenterBuilding.UpgradeFacility` | yes (M-C1 upgrade-facility) | yes | free | free |
| 6 | `ContractsModule` | `ContractSystem` accept/complete/fail | no (no headless verb) | deferred | deferred | n/a |
| 7 | `StrategiesModule` | `StrategySystem` activate; conversion as byproduct | no (no verb) | deferred | deferred | n/a |
| 8 | `RouteModule` | logistics route delivery (Rec-3 residual) | no (RouteCommand reserved) | L3+ | L3+ | n/a |
| 9 | `KerbalsModule` (`Source/Parsek/KerbalsModule.cs`) | hire/dismiss, tombstones | yes (M-C1 hire/dismiss) | yes | free hire | free hire |

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
  science    = -45.0          # AUTHOR CONSTANT: the basicRocketry node cost, stock data
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
change; the science pool expected becomes `seed.science + (-45.0)`, diffed HARD.

### The three career fixtures (`fixtures/saves/`)

Specified so the operator runbook is mechanical (exact contents, one per mode). Each
is a fresh stock save created in the named mode, then trimmed of anything nondeterministic:

| Fixture | Mode (GAME node) | Seed funds | Seed science | Seed rep | Roster (pinned) | Purpose / afford |
|---------|------------------|-----------|--------------|----------|-----------------|------------------|
| `fixtures/saves/fresh-career` | CAREER | a KNOWN value well above the scripted spends (e.g. 500000 to afford a facility upgrade + a hire) | a KNOWN value above the node cost (e.g. 100.0, affords a 45-science node) | 0.0 | exactly the 4 stock starting kerbals (Jeb/Bill/Bob/Val), 0 assigned, plus 1 KNOWN applicant so hire cost is deterministic | research-node, upgrade-facility, hire-kerbal, dismiss-kerbal; the L2 facility refund window |
| `fixtures/saves/fresh-science` | SCIENCE_SANDBOX | absent (no funds pool) | a KNOWN value above the node cost (e.g. 100.0) | absent | stock starting kerbals | research-node only (the science-facet single-module script) |
| `fixtures/saves/fresh-sandbox` | SANDBOX | absent | absent | absent | stock starting kerbals | passive-only (a mode variant of B10; every KscAction refuses or is null-delta) |

Fixture contents rules (so the seed is reproducible and the analyzer seed leg is
stable):

- No craft in flight, no active vessels, no in-progress contracts, no unlocked tech
  beyond the mode's default start node, no facility above level 0 (so the facility
  upgrade has a level to climb). A clean cold KSC.
- The GAME node's `Mode` is the mode selector; the seed pools are read from
  `Funding`/`ResearchAndDevelopment`/`Reputation` SCENARIO nodes by the pre-launch
  analyzer (`CareerSaveParser`), so their values ARE the seed. The fixture author sets
  them to the KNOWN values above.
- The roster is pinned to a known applicant count so the `GameVariables` recruit-cost
  curve yields a DETERMINISTIC hire cost (the hire funds author constant). Recording
  the pinned applicant count in a fixture README is load-bearing: if the roster drifts,
  the hire cost drifts and the author constant goes stale.
- Each fixture carries the recording schema generation stamp (M-A4, plan section 7)
  and is registered synthetic-provenance in the coverage ledger; a generation bump
  re-produces it via the operator runbook (no in-game harvest needed for a fresh
  career save).

`fixtures/saves/fresh-career` is the save the ALREADY-COMMITTED B10 spec references;
creating it here closes B10's dangling fixture reference as a side effect (Edge
Cases). It must be the SAME `fresh-career` B10 loads, so B10 and the L1-career scripts
share one fixture.

### STOCK_AWARD_PATTERNS additions (additive, `hlib.py`, each VERIFY-PENDING-OPERATOR)

The only C# / Python code M-B3 may touch. The v1 set (`hlib.py:1359`) has three
patterns (science-transmit, contract-complete funds, contract-complete rep). The
capture cross-check is SECONDARY (the primary assertion is seam-declared vs save), so
these are strengthening, NOT blocking, EXCEPT where an existing pattern MIS-fires on
an L1 action (the research-spend hazard, Behavior). Candidate additions, each cited to
its stock emitter and each VERIFY-PENDING-OPERATOR against a live EN KSP.log before a
nonzero script trusts it:

| Candidate pattern | kind | facet | emitter (to cite + verify) | why |
|-------------------|------|-------|----------------------------|-----|
| research-spend science DELTA | `tech-unlock` | science | `ResearchAndDevelopment` on `RDTech.ResearchTech` `AddScience(-cost)` | so the research SPEND is captured as `tech-unlock` and MATCHES the seam entry, not mis-captured as `science-transmit` |
| facility-upgrade funds DELTA | `facility-upgrade` | funds | `Funding` on `SpaceCenterBuilding.UpgradeFacility` `AddFunds(-cost, StructureConstruction)` | strengthens the funds cross-check for upgrade scripts |
| hire funds DELTA | `kerbal-hire` | funds | `Funding` `AddFunds(-hireCost, CrewRecruited)` (`KerbalHirePatch.cs:19-22`) | strengthens the funds cross-check for hire scripts |

If, on operator verification, stock emits NO stable EN line for one of these, it is
NOT enumerated (the M-B2 conservative rule) and the primary save-diff carries the
assertion alone; where stock is silent the capture falls to the RESERVED
`gameevents-captured` provenance (M-B3-adjacent, an in-game event-time subscriber),
NEVER a Parsek recalc read.

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
  author constant (basicRocketry = -45.0). Per M-B2, the science facet is STATE-DEPENDENT
  for its stock MAGNITUDE only when the amount is a diminishing-returns science CREDIT;
  a tech-unlock SPEND is a fixed cost, so an author constant is correct and required
  (a null fill-from-capture on the science facet is rejected outright by
  `parse_manifest_entries`, `oracle.py`, the state-dependent forbid).
- Expected: `seed.science + (-nodeCost)`; diffed HARD on `sciencePool`.
- **Capture hazard (must sequence, Behavior below):** the research SPEND may emit a
  `ResearchAndDevelopment ... science ... delta=` line that the EXISTING
  `science-transmit` pattern (`hlib.py:1362`) would capture as `kind=science-transmit`.
  Because `unmatched_captured_awards` matches by `(seq_key, kind, identity)`
  (`hlib.py:1519`), a `science-transmit` capture would NOT match the `tech-unlock`
  seam entry and would FALSE-RED. This is the one place a capture-pattern interaction
  BLOCKS an L1 script.

#### upgrade-facility (modules: FacilitiesModule + FundsModule; facet: funds pool, HARD)

- Driver: `KscAction action=upgrade-facility facility=<facilityId>` (M-C1). Requires
  SPACECENTER (the funds debit lives in the scene `SpaceCenterBuilding` instance,
  `FacilityUpgradeSpendPatch.cs:15`); the verb resolves the live building and invokes
  the real `UpgradeFacility(bool)` so both the `AddFunds(-cost, StructureConstruction)`
  and `SetLevel(level+1)` are the genuine stock ones.
- Manifest: `kind = "facility-upgrade"`, `funds = -<upgradeCost>`, provenance
  seam-declared. The facility LEVEL change lands on the report-only facility facet;
  the funds spend is the HARD assertion.
- **Author-constant rule: DETERMINISTIC PER LEVEL.** A facility's upgrade cost is fixed
  data per (facility, level) in the building config (the value stock's
  `SpaceCenterBuilding.GetUpgradeCost()` returns for level 0 -> 1). It is state-INDEPENDENT,
  so an author constant is correct; funds is the fill-eligible pool-only facet (M-B2),
  so the author MAY instead leave it null to fill-from-capture, but an author constant
  is preferred here because the cost is fixed and fill-from-capture depends on the
  (VERIFY-PENDING-OPERATOR) facility-upgrade capture pattern existing. VERIFY during
  implementation that the per-level cost is fixed data (the building config) and NOT
  GameVariables-scaled; if any facility's cost turns out GameVariables-scaled, treat it
  as fixture-pinned like hire (below).
- Expected: `seed.funds + (-upgradeCost)`; diffed HARD on `funds`.

#### hire-kerbal (modules: FundsModule + KerbalsModule; facet: funds pool, HARD)

- Driver: `KscAction action=hire-kerbal kerbal=<applicantName>` (M-C1). The verb
  mirrors the stock debit `Funding.AddFunds(-hireCost, TransactionReasons.CrewRecruited)`
  (`KerbalHirePatch.cs:19-22`; the `CrewRecruited` reason key is load-bearing:
  `KscActionExpectationClassifier.cs:140-148` keys the ledger's hire funds leg on it)
  then `KerbalRoster.HireApplicant`, confirming roster membership.
- Manifest: `kind = "kerbal-hire"`, `funds = -<hireCost>`, provenance seam-declared.
  The roster ADD lands on the (deferred, M-B2) world roster sub-facet; the funds spend
  is the HARD assertion.
- **Author-constant rule: STATE-DEPENDENT-BUT-FIXTURE-PINNED.** The hire cost rises with
  roster size via the `GameVariables` recruit-cost curve (M-C1 confirms it is NOT a
  hardcoded constant). Funds is the fill-eligible pool-only facet, so the ROBUST M-B2
  choice would be null-to-fill-from-capture. BUT no hire-funds capture pattern is
  enumerated yet (Data Model), so a null fill would fail ambiguous (zero matches ->
  `parse_manifest_entries` flags it, `oracle.py`). The M-B3 resolution: PIN the fixture
  roster so the hire cost is DETERMINISTIC at that roster size, and declare it as an
  author constant. This keeps the assertion independent of any capture pattern and
  independent of Parsek recalc. When the hire-funds capture pattern is later
  operator-verified, the script MAY switch to null-to-fill; until then the
  fixture-pinned constant is the contract. The fixture README records the pinned
  roster + the resulting known cost so the constant does not go stale silently.
- Expected: `seed.funds + (-hireCost)`; diffed HARD on `funds`.

#### dismiss-kerbal (module: KerbalsModule; facet: NONE hard)

- Driver: `KscAction action=dismiss-kerbal kerbal=<name>` (M-C1). Drives
  `KerbalRoster.Remove`; a Parsek-managed kerbal pre-declines `kerbal-parsek-managed`
  (`KerbalDismissalPatch`).
- Manifest: `kind = "kerbal-dismiss"`, ALL pool deltas 0 (a dismissal moves no career
  pool; stock does not refund a hire). The entry exists to record the roster change and
  to assert VIA the zero-delta cross-check that NO pool moved (a dismiss must not
  spuriously credit or debit funds).
- **Assertion:** `expected == seed` on all pools (the dismiss is pool-neutral), plus the
  roster removal on the deferred world roster sub-facet, plus
  `unmatched_captured_awards` empty (no stray award on a dismiss). So the dismiss L1
  script is effectively a passive-safety cross-check scoped to a roster mutation: it
  proves a KSC roster action does not perturb the economy. This is the R6/R7-adjacent
  "no phantom economy movement on a non-economic action" guard.
- Because the roster world sub-facet is DEFERRED in M-B2 (no `Roster` parse on
  `CareerSaveSnapshot`), the dismiss script's ROSTER assertion is a Deferred item; its
  POOL-neutrality assertion is drivable today. Named honestly in Deferred Items.

### The four L1 items with no seam verb (per-item decision)

The plan's L1 list also names complete-contract, strategy, milestone, and EVA science.
None has an M-C1 verb. Decided per-item, honestly:

- **complete-contract -> DEFER to a KscAction batch-2 sub-action, gated + reasoned.**
  Contract completion normally requires FLYING a mission that satisfies the contract's
  parameters; there is NO headless stock path to LEGITIMATELY complete a contract
  without a vessel meeting its conditions. Two options: (a) a synthetic batch-2
  sub-action `KscAction action=complete-contract contract=<guid>` driving
  `ContractSystem.Instance` -> the accepted contract's `Contract.Complete()`, which
  force-fires the funds+rep+science triple-credit through the stock award path but
  BYPASSES the parameter checks (lower fidelity, but it DOES exercise `ContractsModule`
  + `FundsModule` + `ReputationModule` reconcile and the R7 no-N-fold-bake guard); or
  (b) route full-fidelity contract completion to a FLOWN B-track scenario (B6/B7-style)
  where a real mission satisfies the contract. Decision: the L-track's ledger cross-check
  wants the ECONOMIC EFFECT, not the flight, so a batch-2 `complete-contract` sub-action
  is the pragmatic instrument. It needs a companion `accept-contract` (arg
  `contract=<guid|type>`) to set up an acceptable contract first, and it is gated on
  operator-verifying the `ContractSystem completed` funds + rep award lines (partly
  enumerated already, `hlib.py:1368`/`:1374`). This is a KscAction batch-2 seam
  follow-up (Deferred Items), NOT M-B3 script work.
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

### The one L2 script drivable with the M-C1 verb set (facility spend + refund window, R6)

L2 is cross-module / windowed interactions. Of the four L2 examples the plan names
(contract triple-credit, strategy conversion, milestone-fed rewards, facility spend +
refund windows), only the FACILITY SPEND + REFUND WINDOW is drivable with the M-C1
verb set today, because the other three need the deferred contract / strategy /
milestone instruments above. The facility-refund-window L2 script (R6):

- `L2-facility-refund-window.toml` (Career mode): drive `KscAction upgrade-facility`
  (the real spend + level bump), then a `LoadGame` (a SCENE CHANGE), then assert the
  ledger shows the facility level held AND NO phantom `facility-refund` (the R6 guard:
  BUG-G, no silent facility-upgrade refund on scene change, `FacilityUpgradeSpendPatch.cs`).
- Manifest: the one `facility-upgrade` funds entry (as in the L1 upgrade script). The L2
  ASSERTION is that after the scene change, `expected == seed + (-upgradeCost)` STILL
  holds (the spend is NOT refunded) and no undeclared `facility-refund` award appears in
  the capture. If Parsek phantom-refunded on the scene change, the produced save's funds
  would be HIGHER than expected -> hard funds drift -> PARSEK-FAIL(ledger). This is R6
  automated.
- The scene change is a `LoadGame` back into the same run save (the run save was mutated
  by the upgrade; re-loading it is the scene-exit-and-reenter the R6 guard protects). No
  warp verb is needed (R6 is a scene-change guard, not a warp guard; R1's warp trigger
  stays deferred with the `TimeJump`/scene verbs per M-B2).

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
  hire-kerbal, dismiss-kerbal) plus the L2 facility-refund-window. All hard pools present.
  This is the richest mode and the one the plan's L1 milestone centers on.
- **Science (`fresh-science`)**: research-node ONLY (the science-facet single-module
  script). Funds and rep pools are ABSENT (`CareerSaveParser` sets `hasFunds`/`hasRep`
  false; the oracle facet-skips them, M-B2 edge 5), so upgrade-facility / hire have no
  funds facet to assert, and the M-C1 verbs that require a funds debit are meaningless.
  **DEPENDENCY / OPEN QUESTION (Edge Cases + Open Questions):** M-C1's `CareerPresent`
  readiness bit gates `research-node` on `Mode == Game.Modes.CAREER`
  (design-autotest-seam-verbs-c1.md, DispatchState). Science mode is
  `SCIENCE_SANDBOX`, so as specified M-C1 would DEFER `research-node`
  (`career-not-ready`) -> TIMEOUT -> INVALID in Science mode, even though R&D and node
  research are live there. The Science-mode research-node script therefore needs a small
  M-C1 follow-up: widen the science-facet sub-action's readiness gate to admit
  `SCIENCE_SANDBOX` (a `RnDPresent` bit gating `research-node` on
  `Mode == CAREER || Mode == SCIENCE_SANDBOX` with `ResearchAndDevelopment.Instance`
  live). The FALLBACK, if that follow-up is not taken, is that Science-mode L1 degrades
  to passive-only (like Sandbox), which under-covers the "Science mode has science" axis.
  This is the one M-B3 finding that reaches back into a merged module.
- **Sandbox (`fresh-sandbox`)**: PASSIVE-ONLY (a mode variant of B10). No economy pool
  exists; all tech is unlocked (research-node -> `node-already-unlocked` REJECT,
  driver-INVALID by design), hire is free (no funds delta). The Sandbox "script" is the
  B10 passive-safety cross-check re-run in Sandbox mode: `expected == seed` (all pools
  absent), and no KscAction produces a hard-pool delta. It proves the ledger stays inert
  in Sandbox and that the facet-skip-when-absent path is correct for a no-pools save.

### Stock-award capture sequencing (the honest ordering)

The capture cross-check (`unmatched_captured_awards`) is SECONDARY to the seam-declared
vs save diff, but it can FALSE-RED when an existing pattern MIS-fires on an L1 action.
The sequence, honestly:

1. **The primary assertion never depends on the capture.** For every L1 script the
   author declares the effect as a seam-declared author constant, and
   `compute_expected` diffs it HARD against the save. A missing capture pattern cannot
   hide a real drift (the save-diff catches it) and cannot cause a false PASS (M-B2
   Mental Model).
2. **The one BLOCKING interaction: research-node.** Before the research-node script can
   go green, an operator must verify on a live EN KSP.log whether `RDTech.ResearchTech`'s
   science SPEND emits a `ResearchAndDevelopment ... science ... delta=` line. If it
   does, the existing `science-transmit` pattern (`hlib.py:1362`) captures it as the
   WRONG kind and `unmatched_captured_awards` FALSE-reds (kind mismatch against the
   `tech-unlock` seam entry). Resolution, EITHER: (a) add a `tech-unlock` science-spend
   pattern (Data Model) so the spend is captured with the MATCHING kind, OR (b) tighten
   the `science-transmit` regex so a negative-delta spend line is not matched as a
   transmit credit. Option (a) is preferred (it strengthens the cross-check rather than
   narrowing it). This is a REQUIRED operator step in the research-node runbook.
3. **The non-blocking additions.** The facility-upgrade-funds and hire-funds patterns
   strengthen those scripts' cross-checks but are not required for the primary assertion
   (both use author / fixture-pinned constants). Each is VERIFY-PENDING-OPERATOR and
   added only when confirmed stable on EN; where stock is silent the primary save-diff
   stands alone.
4. **Every capture pattern the script RELIES ON must be operator-verified first** (M-B2
   discipline: a nonzero L1 scenario trusts a pattern only after PENDING-OPERATOR EN
   verification). The runbook per script names exactly which patterns must be confirmed
   before that script's cross-check is trusted.

---

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **research-node spend mis-captured as science-transmit.** The existing pattern
   matches a `ResearchAndDevelopment science delta=` line; a research SPEND with a
   negative delta would be captured as `kind=science-transmit`, mismatching the
   `tech-unlock` seam entry and FALSE-reding via `unmatched_captured_awards`. -> The
   research-node runbook REQUIRES an operator EN verification and either a `tech-unlock`
   capture pattern or a tightened `science-transmit` regex BEFORE the script is trusted
   (Behavior "Stock-award capture sequencing" step 2). v1 (BLOCKING, sequenced).
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
   Science-mode script needs a small M-C1 follow-up (a `RnDPresent` bit admitting
   `SCIENCE_SANDBOX` for the science-facet sub-action); the fallback is Science-mode =
   passive-only. Named as an Open Question + Deferred dependency. v1 (dependency named).
5. **Sandbox research-node rejected (all tech unlocked).** In Sandbox every node is
   already researched, so `KscAction research-node` -> `node-already-unlocked` REJECT
   (M-C1), classified driver-INVALID. -> The Sandbox script does NOT drive research-node;
   Sandbox L1 is passive-only (`expected == seed`, all pools absent). v1.
6. **dismiss-kerbal moves no pool but the roster sub-facet is unparsed.** -> The
   pool-neutrality assertion (`expected == seed` on all pools + empty capture) is drivable
   today and is the R6/R7-adjacent guard; the ROSTER-changed assertion is Deferred with the
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
12. **Capture pattern operator-unverified when a script runs.** A nonzero script whose
    cross-check relies on an unverified pattern. -> Per M-B2, the script's cross-check is
    NOT trusted until the pattern is PENDING-OPERATOR EN-verified; until then the script
    runs with only the primary save-diff (still a valid ledger assertion) and the runbook
    flags the pending verification. v1 (primary assertion stands alone).
13. **R6 refund window: the scene change is a LoadGame, not a warp.** -> The L2
    facility-refund-window script uses `LoadGame` (scene exit + reenter), the exact
    transition BUG-G's phantom refund rode; no warp verb is needed (R1's warp trigger is a
    separate deferred surface, M-B2). v1.
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
- **No manifest schema change.** The `[expectations.ledger]` block, the entry kinds
  (`tech-unlock` / `facility-upgrade` / `kerbal-hire` / `kerbal-dismiss` all already in
  `KINDS`, `oracle.py:69`), and `schema = 1` are unchanged; M-B3 populates entries the
  schema already defines.
- **No seam verb change.** `KscAction`, `TimeJump`, and their dispatch gates are M-C1's;
  M-B3 authors driver STEPS that invoke them and adds no verb. The ONE reach-back is the
  proposed Science-mode readiness widening (Open Questions), which is an M-C1 FOLLOW-UP, not
  an M-B3 change, and is optional (fallback: Science = passive-only).
- **The only code M-B3 may touch is additive `STOCK_AWARD_PATTERNS` entries in `hlib.py`,
  each VERIFY-PENDING-OPERATOR and additive-only**, plus their pure pytest coverage. No
  existing pattern is removed; the one existing-pattern EDIT considered (tightening
  `science-transmit`) is an alternative to adding a `tech-unlock` pattern and is chosen only
  if operator verification shows the spend line collides (Behavior sequencing).
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
- **The `STOCK_AWARD_PATTERNS` additions are additive.** An older harness that lacks a
  pattern simply captures fewer awards (the primary save-diff is unaffected); a newer harness
  reading an older run's log captures what its patterns match. No versioned wire artifact
  changes.
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
- A `[Harness][WARN][CAPTURE] pattern <kind> VERIFY-PENDING-OPERATOR: cross-check not trusted`
  when a script's relied-on capture pattern is not yet operator-verified, so a run does not
  silently trust an unverified pattern.

Goal (M-B2's, extended): reading only the harness log, a developer reconstructs the fixture
seed, the one declared effect, the expected pool, the diff, and whether any capture pattern
was untrusted, without rerunning the scenario.

---

## Test Plan

Every test states the regression it catches. The pure decision logic M-B3 EXERCISES already
has M-B2 / M-C1 pytest coverage; M-B3 adds the NONZERO-manifest coverage those modules never
exercised, the fixture-seed cross-lane tests, and the live PENDING-OPERATOR runbook (an agent
cannot pilot KSP, MEMORY: in-game-sweep-needs-operator).

### Pure Python (pytest, no KSP; `harness/lib/test_oracle.py` + `test_hlib.py` additions)

- **Nonzero single-entry expected (science spend).** `compute_expected(seed,
  [tech-unlock science=-45])` returns `seed.science - 45` on `sciencePool`, HARD; the diff
  against a save carrying `seed.science - 45` PASSES, against `seed.science` (an unspent /
  refunded node) reds hard. Fails if the first nonzero manifest cannot be computed or a
  science over/under-credit reads green (the R7-class economy-drift silent pass).
- **Nonzero single-entry expected (funds spend).** `compute_expected(seed, [facility-upgrade
  funds=-<cost>])` returns `seed.funds - cost`; a phantom refund (save funds HIGHER than
  expected) reds hard on `funds` with the UT window named. Fails if a BUG-G phantom refund
  reads green (the exact R6 silent pass this L2 script exists to prevent).
- **State-dependent facet forbid on tech-unlock null.** A `tech-unlock` entry with a null
  `science` amount is REJECTED by `parse_manifest_entries`; the reject becomes a HARD
  divergence (M-B2 edge 18), never a dropped effect. Fails if a null science delta silently
  passes.
- **Capture wrong-kind cross-check.** A captured `science-transmit` award with no matching
  `tech-unlock` seam entry is UNMATCHED (`unmatched_captured_awards`) and reds; a captured
  `tech-unlock` award matching the seam entry's `(seq_key, kind, identity)` is EXPLAINED and
  does not red. Fails if the research-spend mis-capture hazard is not caught (a false red) or a
  genuine undeclared award is not caught (a false pass).
- **hire fixture-pinned constant.** `compute_expected(seed, [kerbal-hire funds=-<pinnedCost>])`
  == `seed.funds - pinnedCost`; a save at a DIFFERENT hire cost (roster drift) reds. Fails if
  a stale hire constant reads green.
- **Sandbox / Science pool-absence.** A `fresh-sandbox` seed (all `hasX` false) yields
  `expected == seed` skipping all pools; a `fresh-science` seed asserts science only. Fails if
  a no-pools mode reds on an absent pool.

### `STOCK_AWARD_PATTERNS` additions (pytest, if a pattern is added)

- **tech-unlock science-spend pattern.** A synthetic EN research-spend line parses to a
  `tech-unlock` `CapturedAward` with the negative science delta; a running-BALANCE line is
  rejected (M-B2 balance rule). Fails if the spend is mis-kinded (the false-red hazard) or a
  balance line is admitted.
- **facility / hire funds patterns (when verified).** A synthetic EN funds-debit line parses
  to the right kind/facet DELTA. Fails if a funds balance line is captured as a delta.

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
   node only, pinned roster + one known applicant for Career), set the known seed pools, save,
   and stage the save as `fixtures/saves/fresh-<mode>` with the schema generation stamp. Record
   the pinned roster + the resulting hire cost in the fixture README.
2. **research-node (Career).** Run `L1-research-node-career`: confirm the harness stages
   `fresh-career`, boots, runs `KscAction research-node node=basicRocketry` (grep
   `kscaction action=research-node ... applied=true` + `OnTechnologyResearched`), computes
   `expected = seed.science - 45`, diffs clean HARD, writes `ledgerOracle status=PASS`. BEFORE
   trusting the cross-check, VERIFY on the live EN KSP.log whether the research spend emits a
   `ResearchAndDevelopment ... science ... delta=` line, and add the `tech-unlock` pattern (or
   tighten `science-transmit`) accordingly (Behavior sequencing step 2). Negative control:
   hand-edit the produced save's R&D science to an unspent value; re-run just the verifier;
   confirm PARSEK-FAIL(ledger) with `sciencePool` named.
3. **upgrade-facility (Career) + the L2 refund window.** Run `L1-upgrade-facility-career` in
   SPACECENTER: confirm the funds pool dropped by the per-level cost (grep the facility POLLING
   observer line + `kscaction action=upgrade-facility applied=true`), `expected = seed.funds -
   cost` diffs clean. Then run `L2-facility-refund-window` (upgrade -> LoadGame -> assert):
   confirm NO phantom `facility-refund` (the funds stay at `seed - cost` after the scene change;
   the R6 guard). Negative control: inject a phantom refund into the produced save; confirm the
   funds drift reds.
4. **hire-kerbal / dismiss-kerbal (Career).** hire: confirm funds dropped by the pinned hire
   cost (grep `OnCrewmemberHired`), `expected = seed.funds - pinnedCost` diffs clean. dismiss:
   confirm NO pool moved (`expected == seed`, empty capture; grep `onKerbalRemoved`), the
   pool-neutrality guard. The roster-changed assertion is Deferred (M-B2 roster sub-facet).
5. **research-node (Science).** IF the M-C1 Science-mode readiness widening lands, run
   `L1-research-node-science` (confirm the verb fires in `SCIENCE_SANDBOX`, science drops by the
   node cost, no funds/rep facet asserted). IF not, record Science-mode L1 as passive-only
   (fallback).
6. **Sandbox passive-only.** Run `L1-passive-sandbox` (a B10 variant): confirm `research-node`
   REJECTs `node-already-unlocked`, no pool exists, `expected == seed`, PASS.

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

Recorded so they are not lost; none blocks the four drivable L1 scripts + the L2 facility-refund
window.

- **The four no-seam-verb L1 items.** complete-contract and strategy defer to a KscAction
  BATCH-2 seam follow-up (`KscAction action=accept-contract contract=<guid|type>` +
  `complete-contract contract=<guid>`, force-firing the triple-credit through
  `Contract.Complete()`; `activate-strategy strategy=<id> commitment=<0..1>`), gated on the M-B2
  oracle's contract/strategy facets being operator-verified end-to-end. milestone and EVA science
  are NOT KSC actions: milestone-fed rewards defer to FLOWN B-track missions (B2/B7) + a
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
- **The full stock-award capture enumeration.** The facility-upgrade-funds and hire-funds
  patterns (and any research-spend `tech-unlock` pattern) are each VERIFY-PENDING-OPERATOR;
  they strengthen the cross-check but the primary save-diff carries the assertion without them.
  Where stock is silent, the capture waits for the RESERVED `gameevents-captured` in-game
  subscriber, never a Parsek recalc read.
- **L3-L5 (M-C3 / M-D1).** Actions x recording lifecycle (commit / discard / auto-merge; the
  Rec-3 residual carve-out), actions x rewind/re-fly (tombstones, supersede flips, timeline
  layering), and the L5 grand oracle career run all build on the M-B3 nonzero-manifest scripts +
  the fixtures here. `rec3CarveOut` and the UT-window machinery (M-B2) are their seams. Deferred
  with M-C3 / M-D1.

### Open Questions (for the review panel)

1. **Science-mode research-node readiness gate.** M-C1's `CareerPresent` gates `research-node`
   on `Mode == Game.Modes.CAREER`, which DEFERS the verb in `SCIENCE_SANDBOX` even though R&D
   and node research are live there. The Science-mode L1 script needs a small M-C1 FOLLOW-UP (a
   `RnDPresent` dispatch bit admitting `SCIENCE_SANDBOX` for the science-facet sub-action). Is
   that follow-up in scope for M-B3's PR, or a separate M-C1 patch M-B3 depends on? The FALLBACK
   (Science-mode = passive-only) under-covers the "Science mode has science" axis; the panel
   should confirm the widening is taken and where.
2. **Author constant vs fill-from-capture for the funds facet.** M-B3 prefers author /
   fixture-pinned constants (independent of any capture pattern) for facility and hire funds,
   because no funds capture pattern is enumerated yet. Is the panel comfortable with the
   fixture-pinned HIRE constant as the v1 contract (with null-to-fill deferred until the
   hire-funds pattern is operator-verified), given the state-dependent recruit-cost curve?
3. **complete-contract fidelity.** The deferred `complete-contract` batch-2 sub-action
   force-fires `Contract.Complete()`, bypassing the contract's parameter checks. Is that
   acceptable fidelity for the L2 triple-credit LEDGER cross-check (the economic effect is real
   even if the mission was not flown), or must contract completion be a flown B-track scenario
   only? This determines whether R7 (no N-fold contract bake) is automatable at L2 or only via a
   flown mission.
4. **Which facility to upgrade in the L1/L2 scripts, and whether its per-level cost is fixed.**
   The design assumes a facility whose level-0 -> 1 upgrade cost is fixed stock DATA (not
   GameVariables-scaled). The panel should confirm the chosen facility (e.g. the Tracking Station
   or R&D) has a deterministic per-level cost, or the script switches to a fixture-pinned constant
   (Edge Case 3).
