# Design: Harness Core (Module M-A5)

Status: DRAFT (2026-07-12). Module M-A5 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, sections 3, 4, 9, 10, 11b row M-A5). This
is the Step 3 design doc the plan's section 11b mandates before any code; the
vision + gameplay scenarios are the plan itself plus the scenario catalog
(`docs/dev/automated-testing-scenario-catalog.md`). The scenario-spec format and
the coverage-ledger tool are "new data that persists", so this is the full
workflow, no shortcuts.

Consumed contracts (already merged, read as authorities, never re-specified
here): the command seam `docs/dev/design-autotest-command-seam.md` (M-A2), the
autorun hooks `docs/dev/design-autotest-autorun-hooks.md` (M-A3), the offline
analyzer + per-save baseline `docs/dev/design-autotest-offline-analyzer.md` /
`docs/dev/design-autotest-findings-baseline.md` (M-A1 + follow-on), and the
stack setup / provisioner `docs/dev/design-autotest-stack-setup.md` (M-A6). This
doc pins against their PUBLIC contract surfaces (channel-file grammar, env-var
contract, `RED=` gate token, manifest admission projection), never their
internals, so a later Gloops file relocation does not break it.

Module boundary / submodule readiness (harness ownership, the Parsek-repo
contract surface it consumes, and the split recipe) is enumerated once, for the
whole `harness/` tree including this M-A5 half, in
`docs/dev/design-autotest-stack-setup.md` under "Module boundary and submodule
readiness". `run.py` / `hlib.py` reach the Parsek repo ONLY through that
contract surface (`scripts/*.ps1`, `collect-logs.py`, the dotnet-test analyzer,
the seam/hooks env vars, the deployed Parsek DLL); everything they generate
(`harness/results`, `harness/coverage`) stays under `harness/`.

Plain ASCII, no em dashes, no emoji.

---

## Problem

Every automated-testing building block now exists in isolation: the command seam
can drive a running KSP unattended (M-A2), the autorun hooks can self-fire a test
batch and quit (M-A3), the offline analyzer can red a bad save (M-A1), and the
provisioner can build byte-reproducible automation instances with an admission
manifest (M-A6). Nothing yet TIES THEM TOGETHER into an unattended pipeline. A
human still has to: pick which scenario to run, confirm the instance is the right
one, stage the fixture save, launch KSP with the right env vars, feed the seam its
commands, notice when the run wedges and kill it, run the analyzer and the log
validator and the results parser over what came out, decide whether the outcome
means "Parsek is broken" or "the autopilot flaked" or "this is the known bug we
are tracking", and write down what got covered. That orchestration is the whole
value of the initiative (plan section 0: "replace most manual playtesting with an
unattended pipeline"), and it is exactly what module M-A5 is.

M-A5 is the external Python orchestrator (`harness/run.py` plus a pure decision
library) that runs SCENARIOS start to finish with no human in the loop: select,
admit the instance, stage the fixture, launch KSP with the scenario's env vars,
drive the seam, enforce a wall-clock budget (timeout -> kill -> a KILLED verdict,
never a hang), run the verifier chain over the produced save + logs, classify the
outcome into the plan's verdict taxonomy, snapshot diagnostics on failure, and
record a per-run result plus a coverage ledger so "exhaustive" is measured rather
than asserted.

v1 is deliberately SEAM-DRIVEN ONLY. Autopilot flight (kRPC + MechJeb, module
M-B1) is out of scope. A v1 scenario is a boot channel plus an ordered list of
seam commands plus optional injection-seeded fixtures. This is not a toy subset:
the daily-tier loop the plan asks for (inject, boot, batch, exit, analyze) is
entirely seam + injection + autorun, with zero flying, so M-A5's v1 already
delivers the plan's daily cadence.

## Terminology

- **Harness / orchestrator**: the external Python process this module builds
  (`harness/run.py`). One invocation runs one SELECTION of scenarios sequentially
  against one automation instance.
- **Scenario spec**: a declarative TOML file under `harness/scenarios/` describing
  one runnable scenario (id, tier, fixture, driver, dimensions covered,
  expectations, budget, retry, expected-fail key). The plan's section 4 item 2
  artifact.
- **Driver (v1 = seam driver)**: the ordered program that makes the scenario
  happen. v1 supports exactly one driver kind, `seam`: an ordered list of M-A2
  seam commands each with the seam verdict the run REQUIRES to proceed, optionally
  plus an M-A3 autorun-batch env configuration. The `autopilot` driver kind is
  RESERVED for M-B1 and rejected by v1 spec validation.
- **Automation instance**: a provisioned KSP directory (M-A6), one of two
  profiles (`stock-minimal`, `modded-compat`), carrying a
  `provision-manifest.json` the harness admits against.
- **Admission**: the pre-launch gate that compares the instance's provision
  manifest against the pins the harness expects (reusing the M-A6 pure
  `provlib.compare_manifest` / `project_admission`). A nonempty diff refuses the
  run before KSP is launched.
- **Verifier chain**: the ordered set of post-run checks over the produced save +
  KSP.log + results file (plan section 9). Each returns a pass/fail plus detail.
- **Verdict**: the terminal classification of one run attempt, from the plan's
  section 10 taxonomy: `PASS`, `PARSEK-FAIL`, `INVALID`, `KILLED`,
  `EXPECTED-FAIL`, plus one addition this module introduces, `XPASS` (an
  expected-fail scenario that unexpectedly passed; see Behavior). FLAKE is NOT a
  verdict in this module: a nondeterministic attempt-1-INVALID-then-attempt-2-PASS
  pair terminates as PASS carrying a `flakedThenPassed` note (fed to the flake
  ledger), not as a distinct FLAKE verdict, so the enum stays
  `{PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS}`.
- **Coverage ledger**: the generated report mapping every dimension-registry value
  to the scenarios covering it and each scenario's last green run, plus the
  expected-fail table and the flake ledger.
- **Dimension registry**: the D1-D18 value set from the catalog section 1,
  materialized as a committed data file (`harness/coverage/registry.toml`) so a
  spec citing an unknown value fails validation and the coverage tool has a
  denominator.
- **Result record**: one per-run JSON file (`harness/results/<runId>.json`)
  carrying the verdict, per-verifier detail, and pointers to the heavy diagnostic
  snapshot.
- **hlib**: the pure, separately-importable, pytest-covered decision library
  (`harness/lib/hlib.py`), the M-A5 analogue of the provisioner's `provlib.py`.
  All non-trivial decision logic (spec validation, response-stream evaluation,
  verdict classification, coverage computation, flake computation, retry decision)
  lives here so it is testable with no KSP and no filesystem.

## Mental Model

```
   harness/scenarios/*.toml         harness/coverage/registry.toml
            |                                   |
            v                                   v
   hlib.validate_spec(spec, registry)  --------/     (pure; no KSP)
            |  valid
            v
   +--------------------------------------------------------------+
   |  run.py: per-scenario loop (one attempt)                     |
   |                                                              |
   |  1. ADMIT   read <instance>/GameData/Parsek/                 |
   |             provision-manifest.json; provlib.compare_manifest|
   |             nonempty diff -> INVALID (no launch)             |
   |  2. STAGE   copy fixture saveTemplate -> instance saves/;    |
   |             inject recordings (inject-recordings.ps1 /       |
   |             InjectAllRecordings) with recording OFF          |
   |  3. LAUNCH  spawn KSP_x64.exe with env:                      |
   |             PARSEK_TEST_COMMANDS=1 (+ AUTORUN/EXIT per spec, |
   |             + ANALYZER baseline mode never set here)         |
   |  4. DRIVE   append seam command lines to the channel file;   |
   |             tail the response file; match verdicts to spec   |
   |  5. BUDGET  wall-clock watchdog: exceed budget -> kill the   |
   |             KSP process TREE -> KILLED                        |
   |  6. VERIFY  ordered verifier chain over the produced save +  |
   |             KSP.log + parsek-test-results.txt                |
   |  7. CLASSIFY hlib.classify_verdict(...) -> Verdict           |
   |  8. On non-PASS: collect-logs.py <scenarioId> snapshot       |
   |  9. WRITE   harness/results/<runId>.json + summary line;     |
   |             retry once if hlib.should_retry says so          |
   +--------------------------------------------------------------+
            |
            v
   harness/coverage.py  (reads all specs + all results + registry)
            |
            v
   coverage.json / coverage.txt  +  flake.json
```

Two invariants shape the whole design:

- **The seam and the hooks own everything inside KSP; run.py owns everything
  outside.** run.py never links kRPC, never reaches into KSP memory, never parses
  a `.sfs` itself for verdicts (the analyzer and CareerSaveParser do that behind
  the seam / behind the analyzer entry point). run.py's only channels into the
  running game are: the process env vars at launch, the M-A2 command/response
  files, and the files KSP leaves on disk (KSP.log, the save, the results file,
  the analyzer report). This keeps run.py free of Unity, testable as pure Python
  over files, and immune to the GPL/kRPC entanglement the seam design already
  avoided.

- **A run never hangs and never lies.** Every wait has a budget; expiry kills the
  process tree and yields KILLED, never an indefinite block (plan section 10). And
  every verdict is derived from an explicit signal the verifier chain read, logged
  with the evidence, so a green run positively demonstrates each check ran (no
  silent pass-by-absence).

## Data Model

Three persisted formats plus one committed registry. All are "new data that
persists" per the plan section 11b, so all get round-trip / determinism tests.

### Scenario spec: `harness/scenarios/<id>.toml`

TOML, read with Python `tomllib`. Format decision and justification: the
provisioner (M-A6) already reads its `pins.toml` / `profiles/*.toml` via
`import tomllib` (`harness/provision/provision.py:44,800`), with no hand-rolled
fallback. The task brief anticipated a hand-rolled reader; the ACTUAL provisioner
uses stdlib `tomllib` (Python 3.11+), so M-A5 stays consistent and uses `tomllib`
too. One TOML dialect, one reader family, across the whole `harness/` tree. (If
the runtime is ever pinned below 3.11, the single `tomllib` import is the one
place to swap for a vendored reader; that is a one-line change confined to a
`harness/lib/toml_load.py` shim, not a format decision.)

Plan-amendment note: the plan's section 4 named YAML for the scenario spec; this
design deliberately OVERRIDES that to TOML for consistency with the provisioner
(M-A6 `pins.toml` / `profiles/*.toml`) and with the stdlib `tomllib` reader, so
the whole `harness/` tree reads one dialect with zero third-party dependencies.
Treat this paragraph as the amendment of record for plan section 4.

```toml
schema = 1
id = "B10-career-passive-safety"
tier = "daily"                       # perpr | daily | nightly | weekly
description = "Fresh career, stock actions only, warp + scene change + cold load; no economy drift."
instanceProfile = "stock-minimal"    # must equal a provisioned profile (manifest.profile)
tags = ["ledger", "B10", "R1", "R2"] # free-text selectors + regression ids

[fixture]
# saveTemplate: a directory under harness/fixtures/saves/ copied verbatim into
# the instance's saves/<runSaveName>/ before launch. "fresh-career" is a
# from-zero career; "none" means the driver's LoadGame targets a save the
# instance already ships (a stock Scenario save). runSaveName IS the template's
# leaf directory name (basename of saveTemplate): "fixtures/saves/fresh-career"
# stages into saves/fresh-career/, so runSaveName = "fresh-career". The driver's
# first LoadGame step MUST target that same save (see the ${runSave} note in
# [driver] below); spec validation rejects a mismatch.
saveTemplate    = "fixtures/saves/fresh-career"
# injectedRecordings: v1 value set is exactly "none" | "all-synthetic".
#   "none"          = no injection.
#   "all-synthetic" = inject the 8 synthetic recordings via inject-recordings.ps1
#                     with recording OFF by construction (plan 3.3), invoked as
#                     KSPDIR=<instanceDir> pwsh scripts/inject-recordings.ps1
#                     -SaveName <runSaveName>, so the target save matches the
#                     LoadGame name= arg. Preset/corpus-scoped injection (a named
#                     subset) is DEFERRED to M-A4 (corpus harvest) / M-B5 (preset
#                     library); v1 spec validation rejects any other value.
injectedRecordings = "none"
# craft: .craft files staged into the instance's Ships/ (rarely used by v1
# seam-only scenarios; reserved for M-B1 flying).
craft = []

[driver]
kind = "seam"                        # v1 ONLY. "autopilot" is RESERVED (M-B1) -> spec-validation reject.
# Ordered seam commands. Each: cmd + args + the seam verdict the run REQUIRES,
# plus an optional per-command budget override (else the seam's own default /
# the scenario runtime budget applies). The literal ${runSave} is substituted by
# the harness with runSaveName (the saveTemplate leaf, "fresh-career" here)
# before the line is written to the channel, so the LoadGame save= arg cannot
# drift from the staged save; spec validation requires the first LoadGame's save
# arg to be exactly "${runSave}" (or a literal equal to runSaveName). A RunTests
# step budget is capped at 540s (see spec-validation rules; the seam's own
# fallback ceiling is 600s).
steps = [
  { cmd = "LoadGame",    args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting",  args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "RunTests",    args = { category = "RecordingInvariants" },          expect = "OK", budget = 540 },
  { cmd = "FlushAndQuit",                                                       expect = "OK" },
]
# OPTIONAL autorun path (M-A3): instead of (or in addition to) a RunTests step,
# the batch may self-fire via the env hooks. When present the harness sets
# PARSEK_AUTORUN_TESTS / PARSEK_AUTORUN_EXIT at launch. Exactly one of {a RunTests
# step, an autorun block} should own the batch (validated).
# autorun = { tests = "RecordingInvariants", exit = true }

# Dimension values this scenario covers. Keys/values validated against
# harness/coverage/registry.toml (catalog section 1). An unknown key or value
# fails spec validation.
[dimensionsCovered]
D8  = ["funds", "science", "reputation", "recalc-from-ut0"]
D14 = ["career", "cold-load-ut0"]
D16 = ["schema-gate"]

# Expectations manifest. The block vocabulary is the plan section 4 item 2 schema.
# v1 EVALUATES: recordings, perRecording, logContracts, plus the analyzer red gate
# and the anomaly sweep (below). The world / route / rewind / loop blocks are
# RESERVED here (parsed + spec-validated) but their heavier verifiers land with
# M-B2 (world-diff / ledger oracle) and M-C2 (rewind/loop) -- the spec carries
# them now so scenarios do not need a format break when those verifiers arrive.
[expectations.recordings]
count = { min = 0, max = 0 }
# treeShape, sectionFrameKinds, eventKinds, resourceDeltas -> reserved
[expectations.logContracts]
# required/forbidden are LITERAL KSP.log line patterns, NOT anomaly tokens (the
# anomaly sweep is harness-owned; see allowedAnomalies below). Anchor the
# BATCH_COMPLETE match so "failed=0" cannot match "failed=05": the pattern ends in
# a word boundary (\b) -- equivalently a trailing space, since the M-A3 line always
# has "failed=<n> skipped=".
required  = ["BATCH_COMPLETE v1 .* failed=0\\b"]
# The level token is UPPERCASE in ParsekLog.Write ("[Parsek][ERROR][...]") and the
# forbidden patterns are case-sensitive re.search, so match ERROR (not Error).
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]
# allowedAnomalies: the scenario's bounded, documented exceptions to the Tier-C
# anomaly sweep. This is a DEDICATED field (NOT logContracts.forbidden): the sweep's
# forbidden token set is harness-owned and fixed (see the anomaly-sweep verifier);
# a scenario only ADDS known-benign exceptions here, it never redefines the sweep.
# Empty = the sweep must find zero anomaly lines.
allowedAnomalies = []                # e.g. ["polyline-orbit-overlap"] for a known-benign leg
# [expectations.perRecording] terminalState / endUT / predictedTail / mergeState -> reserved
# [expectations.world]  vesselPid -> resource totals / roster (M-B2)          -> reserved
# [expectations.rewind] supersedeCount / tombstoneCount / rpState (M-C2)      -> reserved
# [expectations.loop]   LoopStartUT / LoopEndUT / first-play floor (M-C2)     -> reserved
# [expectations.route]  hold-reason strings (M-D1)                            -> reserved

[runtime]
budgetSeconds = 600                  # wall-clock; watchdog kills on exceed -> KILLED

[retry]
policy = "once"                      # once | none  (retry-once-then-INVALID for driver stages)

[expectedFail]
# Non-empty bugId -> this scenario targets an in-flux subsystem tracked by a
# todo-doc bug id (plan section 10). A PARSEK-FAIL whose signature matches is
# demoted to EXPECTED-FAIL and does not poison triage; an unexpected PASS becomes
# XPASS. The OPTIONAL subkind narrows the signature match to ONE PARSEK-FAIL class
# (S2): when set, only a PARSEK-FAIL carrying that exact subkind is demoted, so an
# expected-fail scenario that fails a DIFFERENT way (a different subkind) still
# surfaces as PARSEK-FAIL instead of being silently swallowed. When subkind is
# EMPTY the match is bugId-only (ANY PARSEK-FAIL on the scenario demotes -- the v1
# adaptation the harness Warn-logs at demotion time). subkind must be one of the
# classifier's PARSEK-FAIL subkinds:
# {batch-crashed, analyzer, log-contract, results, anomaly, expectation, ledger};
# an unknown subkind fails spec validation.
bugId   = ""                         # e.g. "R10-reaim-heliocentric-in-plane"
subkind = ""                         # e.g. "analyzer" to match only an analyzer PARSEK-FAIL
```

Spec-validation rules (pure, `hlib.validate_spec`): `id` unique + filename-safe;
`tier` in the enum; `instanceProfile` in `{stock-minimal, modded-compat}`;
`fixture.injectedRecordings` in `{none, all-synthetic}` (v1 value set; any other
value rejected, per S4); `driver.kind == "seam"` (autopilot rejected); the FIRST
step is a `LoadGame` (the boot handshake owner) whose `save` arg is exactly
`${runSave}` or a literal equal to runSaveName (the saveTemplate leaf), so the
loaded save cannot drift from the staged save; every `steps[].cmd` is a v1
IMPLEMENTED seam verb (rejecting a reserved verb like `InvokeRewind`, since v1
cannot drive it); every `steps[].expect` in the seam verdict set
`{OK, ERROR, REJECTED, TIMEOUT}` (INTERRUPTED is NOT a v1-reachable expect, since
the harness never restarts KSP mid-run to observe an at-most-once replay; see the
edge-case note); exactly one BATCH owner (a `RunTests` step XOR an
`[driver.autorun]` block, never both, never neither when `logContracts.required`
names BATCH_COMPLETE); exactly one QUIT owner (a `FlushAndQuit` step XOR
`autorun.exit = true`, never both, never neither); any `RunTests` step `budget`
(and any per-step budget bounding a deferred seam command) is `<= 540` seconds,
because the seam's own fallback deferral ceiling is 600s and the harness step-wait
must clear the seam's deferral budget with margin (see Budget enforcement, S8);
every `dimensionsCovered` key and value present in the registry;
`runtime.budgetSeconds > 0`; `retry.policy` in the enum; if `expectedFail.bugId`
set, it must resolve in the todo doc (warn, not hard-fail, so a scenario can land
slightly ahead of the doc entry).

### Dimension registry: `harness/coverage/registry.toml`

A committed materialization of catalog section 1, the single source of truth for
what exists to cover. One table per dimension, each a list of value tokens.

```toml
schema = 1
[D1]  # recording lifecycle
values = ["auto-record-launch", "auto-record-eva", "manual-gloops", "commit-scene-exit",
          "discard-rollback", "auto-merge", "switch-segment", "ballistic-extrapolation", "..."]
[D8]  # ledger / economy
values = ["funds", "science", "reputation", "milestones", "kerbals", "facilities",
          "contracts", "strategies", "route", "recalc-from-ut0", "epoch-isolation", "..."]
# ... D2..D18 per catalog section 1. D15 (UI) carries only "timeline-projection"
#     (the one honest automated cell); everything else in D15 is out of scope.
```

The plan section 4 item 5 growth rule: a new Parsek feature adds its dimension
values here in the same PR, so the coverage report immediately shows the gap.

Authority note (N9): once committed, `registry.toml` is the AUTHORITATIVE machine
source of the dimension value set (spec validation and the coverage denominator
both read it, nothing else). The catalog (`automated-testing-scenario-catalog.md`
section 1) stays a NARRATIVE companion, not a second source of truth; there is no
automated drift check between the two, and that is an accepted call -- the registry
is what code reads, the catalog is what humans read, and a human keeps them in
sync in the same PR that adds a value.

### Result record: `harness/results/<runId>.json`

`runId = "<YYYY-MM-DD_HHMM>_<scenarioId>[_a<attempt>]"`. Machine-readable, stable
key order, deterministic (no absolute paths beyond the pointer fields, floats via
`repr`), so results diff cleanly and the coverage tool parses them without
guessing.

```json
{
  "schema": 1,
  "runId": "2026-07-12_1830_B10-career-passive-safety",
  "scenarioId": "B10-career-passive-safety",
  "tier": "daily",
  "instanceProfile": "stock-minimal",
  "startedUtc": "2026-07-12T18:30:04Z",
  "endedUtc":   "2026-07-12T18:36:56Z",
  "wallSeconds": 412,
  "attempt": 1,
  "verdict": "PASS",
  "admission": { "admitted": true, "diff": [] },
  "driver": {
    "steps": [
      { "cmd": "LoadGame", "id": "0001", "expect": "OK", "verdict": "OK", "met": true },
      { "cmd": "RunTests", "id": "0003", "expect": "OK", "verdict": "OK", "met": true },
      { "cmd": "FlushAndQuit", "id": "0004", "expect": "OK", "verdict": "OK", "met": true }
    ],
    "allExpectedMet": true
  },
  "verifiers": {
    "driverValidity": { "status": "PASS" },
    "batchComplete":  { "status": "PASS", "found": true, "failed": 0, "line": "BATCH_COMPLETE v1 total=12 passed=12 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT" },
    "analyzer":       { "status": "PASS", "red": 0, "failNonBaselined": 0, "staleNonBaselined": 0, "topFailRule": null, "reportTxt": "saves/.../analysis/persistent.analysis.txt", "reportJson": "saves/.../analysis/persistent.analysis.json" },
    "logValidate":    { "status": "PASS", "recRulesSuppressed": true, "killedRunMode": false },
    "testResults":    { "status": "PASS", "failures": 0, "path": "parsek-test-results.txt" },
    "anomalySweep":   { "status": "PASS", "hits": 0 },
    "expectations":   { "status": "PASS", "mismatches": [] },
    "ledgerOracle":   { "status": "SKIPPED", "reason": "no-actions-or-mb2-not-landed" }
  },
  "expectedFail": { "bugId": "", "matched": false },
  "kspExit": { "code": 0, "killed": false },
  "collectLogs": { "ran": false, "path": null }
}
```

### Coverage / flake ledger: `harness/coverage/coverage.{json,txt}`, `harness/coverage/flake.json`

Generated by `harness/coverage.py` from all specs + all `harness/results/*.json` +
the registry. `coverage.json`: for every registry `(dimension, value)`, the list
of covering scenario ids and the `lastGreenRunUtc` (newest result whose verdict is
PASS or EXPECTED-FAIL for a scenario that covers it), plus the uncovered list
(values with zero covering scenarios), the expected-fail table (`bugId ->
[scenarioId]` with each scenario's latest verdict), and a rollup. `coverage.txt`:
grep-friendly, one line per value:
`<D> <value> coveredBy=<n> lastGreen=<utc|never> [UNCOVERED|EXPECTED-FAIL:<bugId>]`.
`flake.json`: per `(scenarioId, driverStage)`, a rolling 7-day window of attempt
outcomes and the computed flake rate, with a `quarantined` bool set when the rate
exceeds 20% over the week (plan section 10).

### Results layout decision (harness/results vs ../logs)

Two DISTINCT stores, deliberately not merged:

- **Durable machine record** -> `harness/results/<runId>.json` + a rolling
  `harness/results/summary.txt` (one line per run: the "one-line summary the next
  interactive or chat session reads", plan section 10 Runner). Small, textual,
  lives WITH the harness, safe to keep.
- **Heavy diagnostic snapshot** -> the EXISTING `collect-logs.py` convention.
  `collect-logs.py <scenarioId>` already gathers KSP.log, Player.log,
  `parsek-test-results.txt`, the save, and the log-validation output into a
  timestamped `../logs/<ts>_<label>/` folder (sibling of the repo, gitignored per
  `.claude/CLAUDE.md`). M-A5 REUSES it verbatim on any non-PASS run rather than
  re-implementing a gigabyte snapshot; the result JSON's `collectLogs.path` points
  at it. Rationale: `collect-logs.py` is the established, git-ignored home for bulk
  state; duplicating that into `harness/results/` would bloat the durable store
  and fight the existing convention.

## Behavior

### Scenario selection

`run.py --select <expr>` chooses scenarios by id, tier, tag, or cadence. `--tier
daily` runs all daily specs; `--tag R14` runs every spec tagged R14; `--id X`
runs one; `--cadence` maps a cadence name to a tier set per plan section 10
(`per-pr` -> analyzer-on-fixtures only, no KSP; `daily` -> daily tier;
`nightly` -> daily + nightly + a regression-rotation slice; `weekly` -> all).
Selection is pure (`hlib.select_scenarios(specs, expr)`), so the exact set a
cadence resolves to is unit-tested. Selected specs are validated
(`hlib.validate_spec`); any invalid spec is reported and SKIPPED with an
`INVALID-SPEC` result (it never launches KSP), so one broken spec cannot abort the
batch.

### Instance admission (before any launch)

For the scenario's `instanceProfile`, read
`<instanceDir>/GameData/Parsek/provision-manifest.json` and project + diff it
against the EXPECTED manifest, REUSING the M-A6 pure functions
(`provlib.project_admission`, `provlib.compare_manifest`). A nonempty diff, a
missing manifest, or a manifest with a `.provision-incomplete` marker beside it
means the instance is not the one the scenario assumes: the run is refused with a
verdict of `INVALID` (subkind `admission`), the field-level diff recorded, and NO
KSP process is launched. This is the plan section 9 admission gate; classifying
it INVALID (not PARSEK-FAIL) keeps an environment drift out of the Parsek-defect
triage bucket.

Expected-manifest construction (S11): the harness does NOT hand-author the
expected pins. It derives them exactly the way the provisioner authors the on-disk
manifest -- `provlib.project_admission` over (a) the committed `pins.toml`, (b) the
scenario's `instanceProfile` (which selects the profile's mod/setting set), and
(c) the CURRENT build's `Parsek.dll` hash computed with the SAME recipe the
provisioner uses to stamp `provision-manifest.json`. The consequence is a hard
POLICY: a Parsek rebuild changes the DLL hash, so after any rebuild the instance
must be re-provisioned (`provision.py DEPLOY`, which refreshes the manifest) BEFORE
the harness runs; otherwise admission correctly reds the run as drifted (the
deployed DLL no longer matches the current build). The harness does not silently
tolerate a stale DLL -- admission is the gate that catches "you rebuilt Parsek but
forgot to redeploy the automation instance".

### Run lock (two-harness-race decision)

Before staging, the harness acquires a per-instance RUN lock at
`<instanceDir>/GameData/Parsek/.harness-run.lock` (pid + timestamp), reusing the
M-A6 pure `provlib.acquire_lock` predicate (no lock -> acquire; live foreign pid
-> refuse; dead pid -> reclaim). Decision on WHICH lock: the harness uses its OWN
run lock, distinct from BOTH the provisioner's `.provision.lock` AND the seam's
in-KSP `parsek-test-commands.lock`. Reasons:

- The provisioner lock only exists during provisioning and is removed on
  completion (M-A6 R12), so it cannot guard a later run.
- The seam lock is written by the KSP addon AFTER KSP boots (M-A2 lock grammar),
  which is far too late to stop a second harness from staging a fixture over the
  first run's save while the first is mid-flight. The staging step (a destructive
  `saves/` overwrite) must be fenced BEFORE launch.

So the harness run lock is the PRIMARY guard (acquired pre-stage, released at run
end or reclaimed if the holder pid is dead), and the seam lock remains the SECOND
line of defense inside KSP (the addon stands down on a live foreign pid). Both use
the same tested `acquire_lock` logic. KSP itself can only run one instance safely,
so a live run lock plus KSP holding the instance files is the real mutual
exclusion; the lock file makes the refusal fast and explicit instead of a
mysterious file-in-use error.

Zombie-KSP preflight (S10): a dead harness (crashed, or killed at the OS level
without releasing its lock) can leave a live `KSP_x64.exe` still holding the
instance's files even though `.harness-run.lock` reads reclaimable. So AFTER
acquiring / reclaiming the run lock and BEFORE staging, the harness checks that no
process is running out of the instance directory, reusing the provisioner's EC-1
approach (`provlib`'s "is a KSP process bound to `<instanceDir>`" probe used by
provisioning to refuse deploy over a live game). If one is found, the run is
refused with `INVALID` (subkind `instance-busy`), NO staging (the destructive
`saves/` overwrite would corrupt the live game's files), and the diagnostic names
the offending pid. This is distinct from the lock refusal: the lock guards against
a live SIBLING HARNESS, the zombie preflight guards against an orphaned KSP the
lock bookkeeping already forgot.

### Fixture staging

With the lock held: (1) remove any prior `saves/<runSaveName>/` in the instance;
(2) copy the fixture `saveTemplate` directory into `saves/<runSaveName>/`;
(3) if `injectedRecordings == "all-synthetic"`, inject via `inject-recordings.ps1`
invoked against THIS instance -- `KSPDIR=<instanceDir> pwsh
scripts/inject-recordings.ps1 -SaveName <runSaveName>` -- so the target save is the
one just staged and matches the driver's `LoadGame` name= arg; injection happens
with recording OFF by construction (it writes committed recordings directly, never
a live recorder, plan 3.3); (4) stage any `craft` files into `Ships/`;
(5) DELETE / rotate any stale `<instanceDir>/parsek-test-results.txt` left by a
prior run, so this attempt's results parse cannot read a previous run's rows
(the in-game runner appends/overwrites this file at batch end; a leftover from an
earlier attempt would otherwise contaminate the verifier). Staging never touches
the dev install (only the automation instance) and never runs KSP.

### KSP launch and env

Spawn `<instanceDir>/KSP_x64.exe` (the exe path read from the provision manifest's
`instanceDir`, resolved against the umbrella root) as a child process, with a
per-run env:

- `PARSEK_TEST_COMMANDS=1` always (arms the M-A2 addon; the channel files sit at
  the instance's KSP root).
- If the spec has an `[driver.autorun]` block: `PARSEK_AUTORUN_TESTS=<tests>` and,
  when `exit=true`, `PARSEK_AUTORUN_EXIT=1` (the M-A3 hooks).
- `PARSEK_ANALYZER_BASELINE_MODE` is NEVER set at KSP launch (it is an analyzer
  env var, consumed by a later `dotnet test`, not by KSP; setting it here would be
  meaningless and confusing).
- Tracing flags (`mapRenderTracing`, `ledgerTracing`) are pinned via seam
  `SetSetting` steps in the driver, not env, so a scenario that needs the anomaly
  sweep to have data turns them on deliberately.

The harness records the child PID and the whole process TREE (KSP spawns a
launcher on Windows) so the budget watchdog can kill the tree, not just the parent
(edge case: a killed launcher orphaning KSP).

### Instance targeting for reused scripts (S5)

Every DEV script the harness reuses defaults to the DEVELOPER install, never the
automation instance -- so the harness must target each one at `<instanceDir>`
EXPLICITLY on every invocation, and a bare call is a bug that would read/write the
wrong game:

- `inject-recordings.ps1` (staging step 3): `KSPDIR=<instanceDir>` env +
  `-SaveName <runSaveName>` (as above), so it injects into the staged instance
  save, not the dev save.
- `validate-ksp-log.ps1` (verifier 4): `-LogPath <instanceDir>/KSP.log` (the
  script defaults to the dev `Kerbal Space Program/KSP.log`), plus `-KilledRun`
  and/or the recording-rules suppression when the harness selects those modes
  (below).
- `analyze-recordings.ps1` (verifier 3): `-SaveDir <producedSave>` pointed at the
  instance's `saves/<runSaveName>/`, plus the harness-passed `-FreshSaveGate`.
- `collect-logs.py` (on non-PASS): invoked with `--save <runSaveName>` and its
  KSP-root argument resolved to `<instanceDir>`, so the snapshot gathers the
  instance's KSP.log / save / results, not the dev install's.

The harness NEVER lets one of these run against the dev `Kerbal Space Program/`;
the instance path comes from the admitted provision manifest's `instanceDir`.

### Driving the seam

The harness writes command lines to `<kspRoot>/parsek-test-commands.txt` and tails
`<kspRoot>/parsek-test-responses.txt`, exactly the M-A2 channel grammar. It
assigns monotonic-across-run ids (so the seam's at-most-once journal is stable) and
TRUNCATES the four channel files at run start (M-A2 Backward Compatibility
cross-run reuse: monotonic ids + truncate). It appends the driver's `steps` in
order (the seam is strict FIFO; the harness lets the seam serialize), and after
each append polls the response file for a terminal line for that id.

Boot handshake: the FIRST step is expected to be `LoadGame` (the boot channel; the
addon arms at process start and executes `LoadGame` at MAINMENU). The harness waits
for the `LoadGame` response up to that command's budget (default 300s). Until any
response line at all appears, the harness is in the BOOT-WAIT state, watched by a
boot budget: if the KSP process EXITS during boot-wait with no response line, that
is a boot crash (edge cases below); if the boot budget elapses with KSP still alive
and no response, the watchdog kills the tree -> KILLED.

Self-exit precedence (S6): "KSP exited on its own" (a process exit the harness did
NOT cause by a watchdog kill) classifies by WHERE the run was, and the two cases do
not overlap:

- **Boot-phase self-exit** -- the process exits while still in BOOT-WAIT (no seam
  handshake yet, zero response lines). The seam never armed, so the game died
  before it could do anything Parsek-specific: -> `INVALID` (subkind boot-crash),
  retry once. A boot crash is an environment/tooling event, not a Parsek defect.
- **Post-boot self-exit with a pending step** -- the process exits AFTER the boot
  handshake succeeded but while a driver step is still awaiting its terminal
  response (i.e. KSP got past `LoadGame` then died mid-batch, not via the driver's
  `FlushAndQuit` / `autorun.exit`). The batch aborted without finishing: ->
  `PARSEK-FAIL` (subkind batch-crashed), NOT retried (an in-batch crash is a real
  finding, and re-running it just burns another boot). A CLEAN post-boot exit --
  all required steps met, then the QUIT owner fired -- is the normal PASS path, not
  a self-exit.

Because the harness never restarts KSP mid-run, it never observes the seam's
at-most-once `INTERRUPTED` verdict (that verdict only surfaces on a seam RESTART
reading its own journal). `INTERRUPTED` is therefore unreachable in v1; the
self-exit precedence above is the complete account of an unexpected KSP death.

Response evaluation is pure (`hlib.evaluate_response_stream(response_lines,
expected_steps)`): parse each `id=.. cmd=.. verdict=..` line, DEDUPE by id keeping
the FIRST terminal line (M-A2 crash-recovery rewrites re-emit a byte-equivalent
line; first-wins matches the seam's own orchestrator contract), and for each
expected step compare the observed verdict against `expect`. A step whose observed
verdict does not equal its `expect` marks the driver stage failed at that step.

### Budget enforcement and the kill

A single wall-clock watchdog runs for `runtime.budgetSeconds` from launch. Per-step
budgets (the seam's own `RunTests` / `LoadGame` deferral budgets, or a spec
`budget` override) bound how long the harness waits for one response; the run
budget bounds the whole attempt. On EITHER a per-step budget expiry with no
response OR the run budget expiring, the harness KILLS the KSP process tree
(Windows: taskkill /T on the recorded root pid) and classifies the attempt KILLED.
The kill is unconditional and hard; the harness does not try to "ask nicely" first,
because a wedged KSP is by definition not responding to the seam. The plan's
killed-run log-validation mode (below) covers the truncated log a kill produces.

Seam-budget wiring (S8): the M-A2 seam applies its OWN fallback deferral ceiling of
600s to a deferred command (a `RunTests` / `LoadGame` that parks at the seam head
waiting for a scene) before it self-emits a `TIMEOUT`. Two v1 constraints keep the
harness watchdog and the seam ceiling from fighting:

- Spec validation REJECTS any `RunTests` (or other deferred-step) `budget > 540`
  seconds -- 60s below the seam's 600s fallback ceiling -- so the spec can never
  ask the harness to out-wait the seam.
- The rule the harness enforces on every deferred step is
  `harness-step-wait >= seam-deferral-budget + 60s` margin: the harness always
  gives the seam a full deferral window plus slack to emit its own terminal
  verdict, so a genuine seam `TIMEOUT` is OBSERVED (a driver-INVALID, distinct from
  a hang) rather than pre-empted by a harness kill. A harness kill is reserved for a
  truly unresponsive process, not a slow-but-alive deferral.

Passing an explicit `budget=` ARGUMENT down to the seam command itself (so the seam
adopts the spec's per-step budget instead of its 600s fallback) is DEFERRED to
M-A5.1; v1 lives within the fixed 600s seam ceiling via the 540s cap above.

### The verifier chain (order and short-circuit)

Run after the process exits (cleanly, via FlushAndQuit / autorun-exit, or by kill).
Order chosen so the cheapest and most decisive checks run first and a driver-level
failure short-circuits before the expensive analyzer.

Subprocess timeouts (S14): every verifier that shells out (the `analyze-recordings.ps1`
and `validate-ksp-log.ps1` invocations, the `dotnet test` analyzer run, and any
future verifier subprocess) runs under its OWN per-subprocess wall-clock timeout,
distinct from the KSP run budget (which is already spent by the time the chain
starts). A verifier subprocess that exceeds its timeout is KILLED and its verifier
result is `INVALID` (subkind tooling) -- a stuck analyzer or a wedged pwsh is a
tooling failure, never a silent PASS and never a Parsek-defect PARSEK-FAIL. The
tooling INVALID follows the same retry-once policy as the analyzer-error path (the
retry re-runs only that verifier subprocess, not a fresh KSP boot).

1. **Driver validity** (`hlib`, from the response stream). If any required step
   verdict was not met, or the driver never got past boot, the run is
   driver-INVALID: retry-once-then-INVALID (a seam mishap / boot flake is not a
   Parsek defect). A post-boot self-exit with a pending step is the exception (->
   PARSEK-FAIL batch-crashed, per the self-exit precedence above), not a
   driver-INVALID. `INTERRUPTED` is unreachable in v1 (the harness never restarts
   KSP mid-run, so the seam's at-most-once replay verdict is never observed), so
   there is no INTERRUPTED branch here. This gate short-circuits the CLASSIFYING
   verifiers (a run that never reached its batch has nothing for the analyzer to
   judge). EXCEPTION (N6): even on a terminal driver-INVALID the harness still runs
   the analyzer ONCE in triage-only mode (its RED/FAIL/STALE counts are recorded in
   the result for debugging) but that pass is NON-VERDICT -- it cannot turn a
   driver-INVALID into PARSEK-FAIL or PASS; the verdict stays INVALID. This gives a
   maintainer the analyzer read of a broken-driver save without letting a
   possibly-torn save drive the verdict.
2. **BATCH_COMPLETE presence** (grep KSP.log for the M-A3 `BATCH_COMPLETE v1 `
   contract line). Absent when the spec expected a batch -> the batch hung or
   crashed mid-run: KILLED if the process was killed, else PARSEK-FAIL
   (batch-crashed). When present, parse `failed=<n>` for the results cross-check.
   For a multi-category autorun, the per-scene / aggregate lines are all parsed and
   the relevant one (matching the driven category + scene) selected.
3. **Offline analyzer** over the produced save, via `scripts/analyze-recordings.ps1
   -SaveDir <producedSave> -FailOnRed` in FRESH-SAVE (Forbid) mode. Gate decision
   and subclassification come from TWO distinct sources (S1):
   - The GATE is the terminal `RED=<0|1>` token from `<save>.analysis.txt` (the SOLE
     gate source; never recompute the gate from `FAIL=`/`STALE=`, per the baseline
     doc). `RED=0` -> analyzer PASS.
   - The FAIL-vs-STALE SUBCLASSIFICATION of a `RED=1` is read from
     `<save>.analysis.json`, NOT from the txt header: `failNonBaselined` and
     `staleNonBaselined` are JSON-only fields (they are not header tokens). The
     harness parses the JSON via `hlib.parse_analysis_json` (a NAMED hlib parser,
     alongside `parse_analysis_red_token`). `RED=1` with `failNonBaselined > 0` ->
     inspect the findings list (below); `RED=1` with `staleNonBaselined > 0` and
     `failNonBaselined == 0` -> INVALID (fixture stale, points at the M-A4
     re-harvest queue, NOT a Parsek defect).
   Findings-list precedence for a `RED=1` FAIL (S2): `BASELINE-FORBIDDEN` (a stray
   `baseline.cfg` in a fresh mission save) is ITSELF a FAIL finding, so the harness
   inspects the JSON findings list by rule id and applies precedence -- a REAL
   (non-`BASELINE-*`) FAIL wins and classifies the run PARSEK-FAIL (analyzer); if
   the ONLY FAILs are `BASELINE-*`, the run is INVALID (subkind fixture-authoring,
   still surfaced/flagged so the offending template gets fixed). A real defect never
   hides behind a fixture-authoring FAIL, and a fixture-authoring FAIL never reds the
   Parsek-defect bucket.
   NOTE: `analyze-recordings.ps1` today exposes only `-UseBaseline` (Apply) and
   `-WriteBaseline`; Forbid is "passed programmatically by the mission harness
   verifier" (baseline doc). M-A5 adds a thin `-FreshSaveGate` switch to the script
   that sets `PARSEK_ANALYZER_BASELINE_MODE=forbid` for the Manual analyzer run.
   `-FreshSaveGate` is MUTUALLY EXCLUSIVE with `-UseBaseline` / `-WriteBaseline`
   (N1): the script errors out if more than one baseline-mode switch is passed,
   since Forbid, Apply, and Write are three incompatible modes. This is the one
   small production-adjacent seam M-A5 introduces; it is inert until the harness
   passes it.
4. **Log validation + LogContract** via `scripts/validate-ksp-log.ps1 -LogPath
   <instanceDir>/KSP.log`. A failure here -> PARSEK-FAIL (log-contract). The
   validator's rules carry stable codes: `SES-000`/`SES-001` (session start/end
   markers), `REC-001`/`REC-003` (recording started/stopped presence),
   `FMT-001`/`FMT-002` (log line format/level), `WRN-001` (forbidden warning
   contract). Two SUPPRESSION PROFILES the harness selects by run shape:
   - **Recording-rules suppression (B1).** The `REC-001`/`REC-003` rules require a
     "Recording started" and "Recording stopped" line to be present and paired; a
     legitimately RECORDING-FREE scenario (the flagship B10-style daily loop -- boot,
     RunTests, quit, zero recordings) has no such lines, so those rules would RED
     every clean no-recording run and B10 could never PASS. Fix: the harness passes a
     recording-rules suppression to the validator IFF the spec's
     `expectations.recordings.count.max == 0` (and ONLY then), suppressing exactly
     `REC-001` and `REC-003` while `SES-000`/`SES-001`/`FMT-001`/`FMT-002`/`WRN-001`
     stay MANDATORY. For any scenario that expects recordings (`count.max > 0`) the
     REC rules stay mandatory, so a dropped recording in a recording scenario still
     reds. This suppression is orthogonal to the killed-run profile below (a run can
     be in one, both, or neither).
   - **Killed-run mode (S13).** On a KILLED attempt the harness ADDS `-KilledRun`
     (below), which suppresses the marker-pairing rules `SES-000`/`SES-001`/
     `REC-001`/`REC-003` (a kill legitimately truncates the tail mid-session) while
     keeping `FMT-001`/`FMT-002`/`WRN-001`. In killed-run mode only those
     format/level/warning rules can fail.
5. **parsek-test-results.txt parse** (any `FAIL` rows in the autorun categories)
   -> PARSEK-FAIL. Cross-checked against the BATCH_COMPLETE `failed=` count; a
   disagreement between the two is itself a PARSEK-FAIL (the runner's own
   accounting is inconsistent). On a KILLED attempt this parse is TRIAGE-ONLY (S9):
   a kill can tear the results file mid-write, so whatever rows landed are recorded
   for debugging but NEVER drive the verdict -- the verdict stays KILLED. Combined
   with the stage-time rotation of the file (staging step 5), this guarantees the
   results parse reads only this attempt's rows and never lets a torn or stale file
   flip a KILLED into a PARSEK-FAIL.
6. **Anomaly sweep**: grep KSP.log for the Tier-C anomaly lines the tracers emit
   during playback sessions. The forbidden token set is HARNESS-OWNED and fixed:
   `icon-jump`, `line-blink`, `parity-drift`, `decision-vs-truth`,
   `polyline-orbit-overlap`, `rigid-seam-tangent-discontinuity`, `ledger-vs-truth`.
   The scenario's bounded exceptions come from its dedicated `allowedAnomalies` spec
   field (N2), NOT from `logContracts.forbidden` -- a scenario only ADDS
   known-benign tokens to allow, it never redefines the sweep set. Any hit not in
   `allowedAnomalies` -> PARSEK-FAIL (anomaly). Only meaningful when the relevant
   tracing was pinned on (a `SetSetting` step in the driver); a scenario that did
   not enable tracing sweeps trivially clean and records that. Scope note (N10):
   this grep sweep is M-A5's CHEAP v1 anomaly check; M-B5's richer render/ledger
   anomaly analysis EXTENDS this same `allowedAnomalies` contract (adding structured
   per-anomaly detail), it does not duplicate or replace the grep.
7. **Expectations manifest** (`hlib.evaluate_expectations`): the v1-evaluated
   blocks (recordings count, logContracts required/forbidden patterns) are matched
   against the produced save + KSP.log with tolerances, never golden trajectories.
   A mismatch -> PARSEK-FAIL (expectation). Reserved blocks (world/route/rewind/
   loop) are recorded as SKIPPED until their verifiers land (M-B2/M-C2).
8. **Ledger oracle** (M-B2 hook): on a run whose expectations declare a world /
   ledger block AND M-B2 has landed, run the world-diff verifier; drift ->
   PARSEK-FAIL (ledger). In v1 this is SKIPPED with a recorded reason.

REVISION (post-first-live-run, 2026-07-12): the recording-rules suppression key
is NOT `expectations.recordings.count.max == 0` as originally written. The
first live S1.4 run proved that key wrong: injection-seeded scenarios carry
recordings in the SAVE (count.max > 0) yet never record live, so REC-001 and
REC-003 red-flagged a correct run. The implemented key is
`hlib.spec_expects_live_recording(spec)`: the run expects live recording IFF
the driver carries a StartRecording step or pins autoRecordOnLaunch=true via
SetSetting. Every count.max-based suppression mention in this doc reads
through that revision.


On the FIRST verifier that produces a hard failure the harness records that
verifier's detail and stops running later verifiers (they add cost, not signal),
EXCEPT it always still parses BATCH_COMPLETE and the results file if reachable
(cheap, and useful triage context). On any non-PASS it invokes
`collect-logs.py <scenarioId>` for the heavy snapshot.

### Verdict classification

`hlib.classify_verdict(driver_result, verifier_results, expected_fail, attempt,
retry_policy)` maps to the taxonomy:

- **PASS**: driver valid, every run verifier PASS/SKIPPED, and NO `expectedFail.bugId`
  key at all. An expected-fail scenario that passes is NOT a PASS -- it is XPASS
  (N11): the key means "this is expected to fail", so a clean run is an unexpected
  event that must be surfaced, never folded silently into PASS. (An expected-fail
  scenario whose PARSEK-FAIL matches its signature is EXPECTED-FAIL, below.) The
  only PASS variant is the nondeterministic `flakedThenPassed` note: attempt-1
  INVALID then attempt-2 clean terminates PASS with the note recorded (there is no
  FLAKE verdict; see below).
- **INVALID**: admission diff, driver stage failed, boot crash (repeated boot crash
  -> subkind boot-crash-repeated, still INVALID, no new top-level verdict),
  instance-locked (lock race) / instance-busy (zombie KSP), verifier tooling
  timeout, analyzer-error, spec invalid, or fixture STALE / fixture-authoring
  (BASELINE-FORBIDDEN-only). Retryable-once for driver / tooling
  stages (`retry.policy = once`): a first INVALID triggers one retry; a second
  INVALID is terminal and adds a flake-ledger entry (plan section 10 "two invalids
  => quarantine entry"). `INTERRUPTED` is NOT a source here (unreachable in v1).
- **KILLED**: the watchdog killed the process (budget). Not retried by default (a
  hang usually recurs); recorded so the flake ledger sees repeated KILLEDs.
- **PARSEK-FAIL**: a verifier found a real Parsek defect (analyzer non-`BASELINE-*`
  nonbaselined FAIL, log-contract, results FAIL, anomaly, expectation, ledger
  drift), OR a post-boot self-exit aborted the batch (batch-crashed). NOT retried (a
  defect is a defect). There is no FLAKE verdict: nondeterminism is captured as the
  PASS-side `flakedThenPassed` note (attempt-1 INVALID -> attempt-2 PASS), and a
  PARSEK-FAIL is never retried, so no green/red retry pair exists to reclassify.
- **EXPECTED-FAIL**: the scenario carries an `expectedFail.bugId` AND this run's
  PARSEK-FAIL signature matches the expected failure (the failing verifier + a
  bug-id-associated marker). Recorded green-for-triage (does not red the nightly).
  Promotion to a live guard is NOT automatic (N8): the bug id CLOSING is the trigger
  and an XPASS run is the evidence, but the actual promotion is always a HUMAN spec
  edit removing the `expectedFail.bugId` key -- the harness only ambers until that
  edit lands. The match is signature-based, not "any failure counts", so an
  expected-fail scenario that fails a DIFFERENT way still surfaces as PARSEK-FAIL.
- **XPASS** (new): an `expectedFail.bugId` scenario that PASSED. The harness does
  NOT silently promote it to a normal PASS (that would drop the guard the moment
  the bug flickers), and does NOT red the run (it passed). It records XPASS and
  raises an actionable amber in the coverage ledger:
  `expected-fail <bugId> now passing on <scenarioId>: confirm the bug is closed
  and remove the expectedFail key`. This is the two-part promotion contract (N8):
  the bug id CLOSING is the trigger, the XPASS run is the EVIDENCE that the guard
  can be retired, and both feed a HUMAN spec edit that removes the key. Until that
  edit lands the scenario stays expected-fail and the XPASS just keeps ambering;
  the harness never flips it automatically.

### Coverage + flake generation

`harness/coverage.py` (pure core in `hlib.compute_coverage` /
`hlib.compute_flake`) reads every spec's `dimensionsCovered`, every result JSON,
and the registry. For each `(dimension, value)`: covering scenarios = specs
declaring it; `lastGreen` = newest result with verdict in {PASS, EXPECTED-FAIL}
for a covering scenario; UNCOVERED if zero covering scenarios. The expected-fail
table groups scenarios by `bugId` with their latest verdict (surfacing XPASS
ambers and still-failing keys). The flake ledger accumulates per (scenario, stage)
the last 7 days of attempts and computes the flake rate = (INVALID + KILLED) /
attempts -- KILLED counts toward the quarantine rate (N4), since a scenario that
keeps timing out is as unusable in nightly as one that keeps going INVALID. A
`flakedThenPassed` PASS still contributes its attempt-1 INVALID to the numerator
(the nondeterminism is the point of tracking it), but terminates green. `> 0.20`
sets `quarantined = true` and the coverage report flags it for redesign (plan
section 10). Quarantine is STICKY and human-gated: a quarantined scenario is
skipped in nightly (0 further attempts, so its rolling window stops moving) and
STAYS quarantined until a human EDITS the spec to fix or redesign it -- there is no
automatic unquarantine on a quiet window, because a benched scenario cannot earn
green attempts to dilute its own rate. Un-quarantine is a human-only action tied to
that spec edit. All of this is deterministic given the inputs, so the coverage tool
is fully unit-tested.

### Exit-code contract (N8)

`run.py` exits with a code a scheduler / CI step reads without parsing the result
JSON:

- **0** -- every selected scenario terminated `PASS` or `EXPECTED-FAIL` (green).
- **1** -- at least one scenario terminated `PARSEK-FAIL`, `INVALID`, `KILLED`, or
  `XPASS`. `XPASS` deliberately exits 1 as SCHEDULER-AMBER: the run itself passed,
  but an expected-fail guard now passes and a human must confirm the bug is closed
  and remove the `expectedFail` key (N8/N11), so it must NOT read green to a
  scheduler. An invalid spec (INVALID-SPEC, KSP never launched) also contributes a
  1.
- **2** -- no scenario selection was given (`--id`/`--tier`/`--tag`/`--cadence`
  absent); this is also argparse's own bad-argument exit code.

The exit code is the OR of the per-scenario outcomes; a single non-green scenario
in a batch makes the whole invocation exit 1, so a nightly step fails loudly rather
than needing the summary parsed.

### The pure decision library (hlib)

`harness/lib/hlib.py`, the M-A5 analogue of `provlib.py`, holds every non-trivial
decision as a pure function with injected clock/pid where needed:
`validate_spec`, `select_scenarios`, `evaluate_response_stream`,
`evaluate_expectations`, `classify_verdict`, `classify_expected_fail`,
`should_retry`, `compute_coverage`, `compute_flake`, plus the small parsers
(`parse_batch_complete_line`, `parse_analysis_red_token`, `parse_analysis_json`
-- the JSON `failNonBaselined`/`staleNonBaselined`/findings-list reader from S1 --
and `parse_results_failures`). run.py is the THIN imperative shell that does I/O
(launch, tail, kill, copy, subprocess the ps1 scripts) and calls hlib for every
decision. This is the same discipline the provisioner used (provlib.py pure,
provision.py the shell) and the same the plan section 3.1 asks of the seam.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **KSP crashes at boot before the seam arms.** The child process exits during
   boot-wait with no response line. Boot-crash is classified on EXIT-WITH-NO-RESPONSE
   REGARDLESS OF EXIT CODE (N5): KSP can exit 0 on a clean-looking abort that still
   never armed the seam, so the "did the seam ever respond" signal, not the exit
   code, is authoritative. -> Verdict INVALID (subkind boot-crash), retry once. If
   the SAME boot-crash signature (exit code + last KSP.log lines) recurs on the
   retry, the harness records INVALID with subkind boot-crash-repeated (S7) -- a
   deterministic boot crash flagged for human triage, but STILL the INVALID verdict,
   NOT a new top-level verdict. collect-logs snapshot taken. v1.
2. **Seam response file never appears though KSP is alive** (env not passed, or
   `PARSEK_TEST_COMMANDS != "1"`, so the M-A2 addon stayed inert). -> Boot budget
   elapses with the process alive and zero response lines -> watchdog kills the
   tree -> KILLED. The KSP.log will lack the seam's "armed" line, which the harness
   records as the likely cause (env not propagated). v1.
3. **LoadGame returns ERROR msg=load-failed** (template save missing / incompatible
   / wrong KSP version). -> The `LoadGame` step's observed verdict `ERROR` != its
   `expect=OK` -> driver stage failed at step 1 -> INVALID (subkind load-failed),
   retry once. The instance stayed at the menu; the harness sends no further steps
   (they would just TIMEOUT). v1.
4. **Batch hangs vs budget** (a `RunTests` category infinite-loops; no
   BATCH_COMPLETE, no `RunTests` response). -> The `RunTests` step budget (from the
   spec / the seam's scenario budget) expires -> watchdog kills the tree -> KILLED.
   Log validation runs in killed-run mode. v1.
5. **KILLED mid-quicksave / mid-FlushAndQuit save write.** The kill can land while
   KSP is flushing a save, leaving a torn `.sfs`. -> The harness NEVER treats a
   KILLED run's produced save as ground truth: on KILLED it SKIPS the analyzer and
   the expectation/world verifiers (they would analyze a possibly-torn save) and
   relies only on the killed-run log validation + whatever BATCH_COMPLETE lines
   landed before the kill. The verdict stays KILLED regardless of the torn save.
   v1.
6. **Instance manifest drift** (admission diff nonempty, or manifest missing, or
   `.provision-incomplete` present). -> INVALID (subkind admission), NO launch, the
   field-level diff recorded (via `provlib.compare_manifest`). Points at re-running
   the provisioner. v1.
7. **Two harness runs racing one instance.** -> The harness run lock
   (`<instanceDir>/GameData/Parsek/.harness-run.lock`, `provlib.acquire_lock`)
   refuses the second run against a live holder pid (INVALID subkind
   instance-locked, no launch); a dead holder pid is reclaimed. The seam's own
   in-KSP lock is the secondary guard. v1.
8. **Template save schema-generation mismatch -> STALE.** An injected/staged
   fixture carries recordings at a generation != current (an M-A4 generation bump
   landed but fixtures were not regenerated). -> The analyzer reports STALE-FIXTURE;
   the GATE `RED=1` comes from the `.analysis.txt` token, and the harness reads the
   `<save>.analysis.json` `staleNonBaselined > 0` / `failNonBaselined == 0` split
   (JSON-only fields, per S1) to classify -> INVALID (subkind fixture-stale),
   pointing at the M-A4 re-harvest queue, NOT PARSEK-FAIL (so a fixture-maintenance
   lapse does not poison Parsek triage). v1.
9. **Expected-fail scenario that unexpectedly PASSES.** -> XPASS: not silently
   promoted (would drop the guard), not red (it passed). Recorded XPASS + an
   actionable coverage-ledger amber telling the maintainer to confirm the bug id
   closed and remove the `expectedFail` key. Promotion is a manual spec edit. v1.
10. **Results dir full / locked** (cannot write `harness/results/<runId>.json`).
    -> The harness retries the result write with bounded backoff to a fallback
    (`harness/results/.pending/`), and if still failing emits the one-line summary
    to stdout + the run log so the verdict is never lost even when the durable
    record cannot land. The RUN verdict itself is already computed; only its
    persistence is degraded, and that degradation is logged Error. v1.
11. **BATCH_COMPLETE token absent** when the spec expected a batch. -> The batch
    hung or crashed. If the process was killed -> KILLED; if it exited on its own
    with no BATCH_COMPLETE -> PARSEK-FAIL (subkind batch-crashed, the batch
    aborted without finishing, e.g. an NRE storm that did not even reach its
    end-region emit). v1.
12. **RED= token absent** from the analyzer report (the analyzer itself failed to
    run, threw, or produced no `.analysis.txt`). -> Treated as an analyzer TOOLING
    failure -> INVALID (subkind analyzer-error, retry once), NOT a green pass (an
    absent gate token must never read as RED=0). The analyzer-error retry re-runs
    ONLY the analyzer subprocess (N6), not a fresh KSP boot -- the produced save is
    already on disk, so re-running the whole scenario would just burn a boot; the
    retry re-invokes `analyze-recordings.ps1` over the same save. The analyzer's own
    stderr is captured. v1.
13. **A driver step's observed verdict mismatches `expect`** (e.g. `CommitTree`
    expected OK returned ERROR no-active-tree because a prior step did not arrange
    a tree). -> Driver stage failed at that step -> INVALID (subkind
    driver-verdict-mismatch), retry once. The mismatch (expected vs got) recorded
    per step. v1.
14. **Seam INTERRUPTED verdict is UNREACHABLE in v1.** The seam only emits
    `INTERRUPTED` when it RESTARTS and reconciles its at-most-once journal, but the
    harness never restarts KSP mid-run (each attempt truncates the channel files and
    boots a fresh process), so the harness never observes an `INTERRUPTED` line. The
    real event -- KSP dying mid-command -- surfaces instead as a process self-exit,
    handled by the self-exit precedence (boot-phase -> INVALID boot-crash + retry;
    post-boot with a pending step -> PARSEK-FAIL batch-crashed, no retry; see
    Driving the seam / edges 1 and 11). `INTERRUPTED` is therefore NOT in the v1
    `expect` set and has no classification branch. Deferred: if M-C1 multi-session
    ever drives a seam RESTART within one scenario, an `INTERRUPTED` branch lands
    with it. v1 (scoped: unreachable).
15. **Spec invalid** (missing required field, unknown dimension value, autopilot
    driver kind, reserved seam verb in a step, both/neither BATCH owner,
    both/neither QUIT owner (FlushAndQuit XOR autorun.exit, N3), first step not
    LoadGame, LoadGame save arg not `${runSave}`/runSaveName, `injectedRecordings`
    outside `{none, all-synthetic}`, a `RunTests` budget > 540s). ->
    `hlib.validate_spec` fails it; recorded INVALID-SPEC, KSP never launched, the
    rest of the selection continues. v1.
16. **Expected-fail bugId not resolvable in the todo doc** (stale key). -> Spec
    validation WARNS (not hard-fail, so a scenario can land just ahead of its doc
    row); the coverage ledger flags the dangling bug id so it is cleaned up. v1.
17. **Anomaly sweep finds a Tier-C line** (`icon-jump` / `line-blink` /
    `parity-drift` / `polyline-orbit-overlap` / `rigid-seam-tangent-discontinuity`
    etc.) during a playback session, not listed in the scenario's `allowedAnomalies`.
    -> PARSEK-FAIL (subkind anomaly), the matching line(s) recorded. A scenario
    declares its bounded exceptions in the dedicated `allowedAnomalies` spec field
    (N2), NOT `logContracts.forbidden`, so a known-benign line does not false-red
    while the harness-owned sweep set stays fixed. v1.
18. **collect-logs.py itself fails on a red run** (KSP still holding files, or a
    partial process-tree kill left a lock). -> Best-effort: the harness waits a
    bounded grace for the process tree to fully die before invoking collect-logs,
    then records `collectLogs.ran=false` + the failure reason if it still cannot
    snapshot. The verdict is unaffected (it was already classified); only the
    diagnostic snapshot is degraded, logged Error. v1.
19. **Multi-category autorun emits several BATCH_COMPLETE lines** (per token +
    aggregate, M-A3). -> The harness parses all of them and selects the line(s)
    matching the driven categories + the scene the scenario ran in; the aggregate
    `category=multi:<n>` line drives the overall failed-count check. A missing
    per-token line for a category the scenario declared -> PARSEK-FAIL
    (batch-incomplete). v1.
20. **Duplicate response line for one id** (M-A2 crash-recovery rewrite re-emits a
    byte-equivalent terminal line). -> `evaluate_response_stream` dedupes by id
    keeping the FIRST terminal line, matching the seam's own orchestrator contract;
    the duplicate is not a second outcome. v1.
21. **KILLED-run log validation.** A timeout-kill produces a log that legitimately
    ends mid-session, so the marker-pairing rules would false-fail. -> On a KILLED
    attempt the harness runs `validate-ksp-log.ps1` with `-KilledRun`, which
    suppresses exactly the marker-pairing rule codes `SES-000`/`SES-001`/`REC-001`/
    `REC-003` and keeps `FMT-001`/`FMT-002`/`WRN-001` (S13). This mode does not
    exist in the script today; M-A5 adds the `-KilledRun` switch, which sets an env
    var the log-validation xUnit reads (sibling to the existing `PARSEK_LIVE_*`
    seam) to disable the four pairing rules; the ps1 CLEARS that env var in a
    `finally` block (the same pattern the `analyze-recordings.ps1` baseline-mode
    switch uses), so a crash mid-validation never leaks the suppression into a later
    invocation. This is the second small seam addition M-A5 introduces, required by
    plan section 9 item 1. v1.
22. **A dimension registry value with zero covering scenarios.** -> Not a run
    error; the coverage tool lists it UNCOVERED (the visible backlog, plan section
    4 item 3). v1.
23. **Flake quarantine.** A (scenario, stage) whose 7-day (INVALID + KILLED) rate
    exceeds 20% -> `compute_flake` sets `quarantined=true` (KILLED counts toward the
    rate, N4); the coverage report flags it for redesign and the harness skips it in
    nightly. Quarantine is STICKY: a skipped scenario runs 0 further attempts so its
    window cannot self-heal, and it stays quarantined until a HUMAN edits the spec to
    fix/redesign it -- there is no automatic unquarantine on a quiet window. v1.
24. **A stray `baseline.cfg` rode into the staged fixture template.** -> The
    analyzer runs in Forbid over the fresh mission save; a present baseline is a
    `BASELINE-FORBIDDEN` FAIL (`RED=1`). Findings-list precedence applies (S2): if
    the ONLY FAILs are `BASELINE-*` -> INVALID (subkind fixture-authoring), pointing
    at the offending template; but if a REAL (non-`BASELINE-*`) FAIL is ALSO present,
    the real FAIL wins and the run is PARSEK-FAIL (the baseline mistake never hides a
    genuine defect). A fresh mission save must never carry a baseline (baseline doc),
    and the harness's Forbid mode enforces it structurally. v1.
25. **Retry-once-then-INVALID for driver stages.** A driver-INVALID first attempt
    triggers one clean-slate retry (fresh stage, fresh launch); a second
    driver-INVALID is terminal INVALID + a flake-ledger entry. A retry that PASSES
    after an INVALID is recorded PASS with a `flakedThenPassed` note feeding the
    flake ledger. v1.
26. **Clock skew for lastGreen timestamps.** All result timestamps are UTC ISO-8601
    from the harness host; `lastGreen` ordering is a string compare on UTC, immune
    to local-tz / DST. An NTP jump backward could momentarily misorder two runs
    seconds apart; accepted (the coverage report tolerates second-level ordering
    noise). v1.
27. **A seam TIMEOUT verdict on a driver step** (the command sat at the seam head
    past its deferral budget, e.g. `StartRecording` in a scene that never became
    FLIGHT). -> Observed `TIMEOUT` != `expect` -> driver stage failed -> INVALID,
    retry once. Distinct from the harness watchdog KILL: a seam TIMEOUT means KSP
    is alive and advanced past the command; a harness KILL means the whole process
    was unresponsive. v1.
28. **modded-compat-only scenario selected against a stock-minimal instance.** The
    admission diff on `profile` is nonempty -> INVALID (admission). D17 / R25 / R26
    scenarios declare `instanceProfile = "modded-compat"` and the harness refuses to
    run them against the wrong instance rather than certifying the wrong stack. v1.
29. **A zombie `KSP_x64.exe` still bound to the instance** (a prior harness died
    without releasing its lock, leaving a live game holding the instance files while
    `.harness-run.lock` reads reclaimable). -> The pre-stage zombie-KSP preflight
    (S10, reusing the provisioner's EC-1 process probe) finds it and refuses with
    INVALID (subkind instance-busy), NO staging (the destructive `saves/` overwrite
    would corrupt the live game). Distinct from edge 7 (a live sibling HARNESS held
    by the run lock); here the harness lock is stale but a KSP process is not. v1.
30. **A verifier subprocess hangs** (a wedged `analyze-recordings.ps1` /
    `validate-ksp-log.ps1` / `dotnet test`, e.g. pwsh stuck on a locked file). ->
    Each verifier subprocess runs under its own wall-clock timeout (S14); on expiry
    the harness kills that subprocess and records the verifier result as INVALID
    (subkind tooling), retry-once (the retry re-runs only that subprocess). Never a
    silent PASS, never a PARSEK-FAIL. v1.
31. **Recording-free scenario (B10-style daily loop) with zero recordings.** The
    driver boots, runs `RecordingInvariants`, quits; no "Recording started/stopped"
    lines ever emit. Without B1 the `REC-001`/`REC-003` marker-pairing rules would
    RED every clean run and B10 could never PASS. -> The harness detects
    `expectations.recordings.count.max == 0` and passes the recording-rules
    suppression to `validate-ksp-log.ps1`, suppressing exactly `REC-001`/`REC-003`
    while `SES-000`/`SES-001`/`FMT-001`/`FMT-002`/`WRN-001` stay mandatory, so a
    genuinely no-recording run validates clean and can PASS. A scenario that expects
    recordings (`count.max > 0`) keeps the REC rules mandatory, so a dropped
    recording there still reds. v1.

## What Doesn't Change

- **No Parsek gameplay or in-game code changes.** M-A5 is an external Python
  orchestrator plus a coverage tool. The two small tooling seams it needs
  (`analyze-recordings.ps1 -FreshSaveGate` for programmatic Forbid, and
  `validate-ksp-log.ps1 -KilledRun` for the killed-run suppression) are additive
  switches on existing DEV scripts, inert unless the harness passes them; they add
  no new save data and no in-game behavior. The command seam, the autorun hooks,
  the analyzer core, the baseline filter, and the provisioner are consumed as-is.
- **The dev install is never touched.** The harness operates only on provisioned
  automation instances (M-A6), never the developer's `Kerbal Space Program/`.
- **No new recording / save / sidecar / ledger format.** Scenario specs, result
  records, the registry, and the coverage/flake ledgers are harness-side artifacts
  under `harness/`, not KSP save data; KSP's `GameDatabase` never loads them (they
  are `.toml` / `.json` / `.txt`, ignored like the provision manifest).
- **collect-logs.py, validate-ksp-log.ps1, analyze-recordings.ps1,
  inject-recordings.ps1, InjectAllRecordings** keep their existing behavior; M-A5
  invokes them, and only ADDS the two inert switches above.
- **The seam's at-most-once, FIFO, and lock semantics** are unchanged; the harness
  is a conforming client (monotonic ids, truncate-between-runs, first-wins dedupe,
  its own outer run lock layered above the seam lock).
- **The plan's verdict taxonomy** is used almost as-is; M-A5 ADDS the `XPASS`
  outcome for the "expected-fail unexpectedly passes" case the taxonomy did not
  name, and DROPS `FLAKE` from the verdict enum -- nondeterminism is captured as a
  `flakedThenPassed` note on a PASS (attempt-1 INVALID -> attempt-2 PASS) rather
  than as a distinct terminal verdict, so the enum is
  `{PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS}`.
- **The baseline Forbid contract** (fresh mission saves are absolute-gated) is
  preserved and made operational: the harness is exactly the "M-A5 harness
  verifier" the baseline doc says passes `Forbid`.

## Backward Compatibility

Greenfield module; nothing to migrate. Forward policy mirrors the provisioner: the
scenario spec, registry, result record, and coverage/flake files all carry
`schema = 1`; a schema bump makes the harness refuse an old artifact (with a clear
message) rather than silently mis-parse, consistent with the project's
no-migration stance for versioned data. New seam verbs, new dimension values, new
expectation blocks, and new tags are additive: a spec that uses only known fields
runs on a newer harness unchanged, and the reserved expectation blocks (world /
route / rewind / loop) already parse today so an M-B2/M-C2 scenario written now
needs no format change when those verifiers land. Result records from an older
harness version are readable by `coverage.py` as long as the top-level `schema`
matches; an unknown newer result schema is skipped with a warning (a stale
coverage run is better than a crash). The two dev-script switches default OFF, so
every existing manual invocation of those scripts is byte-identical.

## Diagnostic Logging

The harness logs to stdout AND an append-only per-INVOCATION log
`harness/results/<ts>_harness.log` (S6; one run.py invocation runs a whole
selection, so the log is keyed by the launch timestamp, not a per-scenario runId),
one decision per line, format `[Harness][LEVEL][Step] message` (mirroring ParsekLog
/ the provisioner's `[Provision]`), so a scheduled unattended run is fully
reconstructable from the log alone (plan section 10). Every branch logs; the batch-counting convention applies
to the per-response-line poll (one summary line, not one per poll).

- **SELECT**: `Info` "select expr='<expr>' -> <n> scenarios: [<ids>]"; per invalid
  spec `Warn` "spec invalid id=<id> reasons=[<...>]".
- **ADMIT**: `Info` "admit instance=<profile> manifest=<path> result=<OK|DRIFT>";
  on drift `Warn` one line per diff field
  "admit drift field=<f> expected=<e> actual=<a>".
- **LOCK**: `Info` "run-lock acquired instance=<profile> pid=<n>", or `Warn`
  "run-lock refused: live holder pid=<n>", or `Warn` "run-lock reclaimed stale
  pid=<n>".
- **PREFLIGHT**: `Info` "zombie-check instance=<profile> result=<CLEAR|BUSY>"; on
  BUSY `Warn` "instance-busy: live KSP pid=<n> bound to <instanceDir>; refusing
  (INVALID instance-busy)" (S10).
- **STAGE**: `Info` "stage save=<runSaveName> template=<t> inject=<none|all-synthetic>
  craft=<n> results-rotated=<bool>" (results-rotated notes the stale
  parsek-test-results.txt deletion, S9).
- **LAUNCH**: `Info` "launch exe=<path> pid=<n> env=[TEST_COMMANDS=1
  AUTORUN=<sel|unset> EXIT=<0|1>] budget=<s>s".
- **DRIVE**: per step `Info` "drive step=<i> id=<id> cmd=<verb> expect=<v>";
  on response `Info` "drive resp id=<id> verdict=<v> met=<bool>"; a `Verbose`
  rate-limited poll summary "poll: newLines=<n> pendingId=<id|none>".
- **BOOT-WAIT**: `VerboseRateLimited` (1 Hz) "boot-wait: elapsed=<s>/<budget>
  kspAlive=<bool> anyResponse=<bool>" so a stuck boot shows exactly what it waited
  on; `Warn` "boot-crash: process exited (exit=<c>) with no response -> INVALID"
  (classified on no-response regardless of exit code, N5; edge 1) or `Warn`
  "boot-timeout: killing tree" (edge 2). Post-boot self-exit with a pending step:
  `Warn` "batch-crashed: KSP exited (exit=<c>) with step id=<id> pending -> PARSEK-FAIL"
  (S6, edge 11).
- **BUDGET / KILL**: `Warn` "budget exceeded scope=<run|step:<cmd>> elapsed=<s>;
  killing process tree root pid=<n>" then `Info` "kill complete pids=[<...>]".
- **VERIFY**: one line per verifier: `Info`
  "verify <name> status=<PASS|FAIL|SKIPPED|INVALID> <detail>" (e.g.
  "verify analyzer status=FAIL red=1 failNonBaselined=2 staleNonBaselined=0
  topRule=<ruleId>" -- the fail/stale split + top finding rule id read from the
  `.analysis.json`, S1/S2; "verify batchComplete status=PASS failed=0";
  "verify logValidate status=PASS recRulesSuppressed=<bool> killedRunMode=<bool>"
  showing both suppression profiles, B1/S13). A verifier subprocess timeout logs
  `Warn` "verify <name> status=INVALID subkind=tooling: subprocess timed out at
  <s>s" (S14). A short-circuit is logged "verify short-circuit at <name>; skipping
  later verifiers (analyzer still run triage-only)" (N6).
- **CLASSIFY**: `Info` "verdict=<V> scenario=<id> attempt=<n> reason=<why>"; for
  expected-fail `Info` "expected-fail bugId=<id> matched=<bool>"; for XPASS `Warn`
  "XPASS bugId=<id> scenario=<id>: confirm bug closed, remove expectedFail key".
- **COLLECT**: `Info` "collect-logs label=<scenarioId> -> <path>" or `Error`
  "collect-logs failed: <reason>; snapshot degraded".
- **RESULT**: `Info` "result written <path>"; on write failure `Error`
  "result write failed: <reason>; emitted to stdout+log".
- **RETRY**: `Info` "retry scenario=<id> attempt=2 reason=<first-verdict>" or
  `Info` "no retry (policy=<p> verdict=<V>)".
- **COVERAGE** (`coverage.py`): `Info`
  "coverage: values=<n> covered=<c> uncovered=<u> expectedFail=<e> xpass=<x>";
  per uncovered value `Verbose`; per quarantine `Warn`
  "flake quarantine scenario=<id> stage=<s> rate=<r> over 7d".

Goal: reading only `harness/results/<ts>_harness.log` and `<runId>.json`, a developer
can reconstruct which scenario ran on which instance, which driver steps got which
verdicts, why the run was classified as it was, and where the heavy snapshot
landed, without rerunning anything.

## Test Plan

Every test states the regression it catches. Pure decision logic lives in
`harness/lib/hlib.py` and is pytest-covered in `harness/lib/test_hlib.py` (the
M-A5 analogue of `harness/provision/test_provlib.py`), so it runs in the per-PR
cadence with no KSP. The thin run.py shell (subprocess launch, file tail, process
kill, script invocation) is exercised by an operator-driven smoke run plus a
FAKE-KSP harness test (a stub process that reads the command file and writes a
scripted response file), because an agent cannot pilot KSP (MEMORY:
in-game-sweep-needs-operator).

### Pure unit tests (pytest, no KSP)

- **Spec validation accept + each reject.** A well-formed spec validates clean;
  fixtures that (a) omit a required field, (b) cite an unknown dimension value,
  (c) use `driver.kind = "autopilot"`, (d) put a reserved seam verb
  (`InvokeRewind`) in a step, (e) declare both a `RunTests` step and an
  `[driver.autorun]` block, (f) declare neither while requiring BATCH_COMPLETE,
  (g) declare both/neither QUIT owner (FlushAndQuit XOR autorun.exit, N3), (h) have
  a first step that is not LoadGame or a LoadGame `save` arg that is not
  `${runSave}`/runSaveName (S3), (i) set `injectedRecordings` outside
  `{none, all-synthetic}` (S4), (j) give a `RunTests` step a `budget > 540` (S8),
  each reject with the right reason. Fails if a malformed spec launches KSP (wastes
  a boot and produces a meaningless verdict) or a valid spec is wrongly rejected.
- **Selection resolves cadence/tier/tag/id.** `--tier daily` -> exactly the daily
  specs; `--tag R14` -> every R14-tagged spec; `--cadence nightly` -> the mapped
  tier set. Fails if a cadence silently drops or adds scenarios (the nightly
  coverage would then be wrong without anyone noticing).
- **Response-stream evaluation: verdict match + first-wins dedupe.** Given a
  response file with the expected verdicts -> `allExpectedMet=true`; a verdict
  mismatch -> the failing step flagged; a duplicate terminal line for one id -> the
  FIRST kept, the duplicate ignored. Fails if a crash-recovery rewrite (M-A2) is
  miscounted as a second outcome, or a verdict mismatch is missed (a driver failure
  reads as a pass).
- **Verdict classification matrix.** Table-drive (driver_ok, batchComplete,
  analyzerRed{none|fail|stale|forbidden|forbidden+real-fail}, logValidate,
  resultsFail, anomaly, expectation, killed, expectedFail{none|matched|xpass},
  attempt) -> {PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS} (NO FLAKE
  verdict; a `flakedThenPassed` case terminates PASS with the note). Key rows:
  analyzer STALE-only -> INVALID (not PARSEK-FAIL); analyzer BASELINE-FORBIDDEN-only
  -> INVALID; analyzer BASELINE-FORBIDDEN + a real non-BASELINE FAIL -> PARSEK-FAIL
  (findings-list precedence, S2); expected-fail signature match -> EXPECTED-FAIL;
  expected-fail scenario clean -> XPASS (never a plain PASS); PARSEK-FAIL is never
  retried; driver-INVALID retries once; attempt-1 INVALID + attempt-2 PASS -> PASS
  with `flakedThenPassed`. Fails if a fixture-stale run poisons the Parsek-defect
  bucket, an expected-fail bug reds the nightly, an XPASS silently promotes and
  drops the guard, or a real defect hides behind a fixture-authoring FAIL.
- **STALE vs FAIL split from the analysis JSON.** The GATE is `RED=` from the txt
  header; the FAIL-vs-STALE split is read from the `.analysis.json` counts (S1, not
  header tokens). Given `RED=1` + JSON `staleNonBaselined=3 failNonBaselined=0` ->
  INVALID(fixture-stale); `RED=1` + JSON `failNonBaselined=2` -> PARSEK-FAIL
  (analyzer); `RED=0` -> analyzer PASS; RED token ABSENT -> INVALID (analyzer-error),
  never PASS. Fails if an absent gate token reads as green (the most dangerous
  silent pass), a stale corpus is triaged as a code defect, or the split is wrongly
  read from the txt header instead of the JSON.
- **Expected-fail signature match, not any-failure.** An expected-fail scenario
  whose bug id targets the analyzer, failing on the analyzer -> EXPECTED-FAIL;
  the SAME scenario failing on a log-contract instead -> PARSEK-FAIL (a different,
  unexpected break). Fails if "expected-fail" swallows an unrelated regression.
- **Coverage computation.** Given specs + results + registry, a value covered by a
  PASS run shows `lastGreen`; a value covered only by a PARSEK-FAIL run shows
  `lastGreen=never`; a value with no covering spec is UNCOVERED; an XPASS surfaces
  its amber. Fails if a red run is counted as coverage (false "exhaustive" signal)
  or a genuinely covered value shows uncovered.
- **Flake computation + quarantine.** A (scenario, stage) with 3/10 INVALIDs over
  the window -> rate 0.30, `quarantined=true`; 1/10 -> not quarantined; 2 INVALID +
  1 KILLED out of 10 -> rate 0.30, `quarantined=true` (KILLED counts, N4); and a
  quarantined scenario with a subsequent quiet window stays `quarantined=true`
  (sticky, human-only unquarantine). Fails if the 20% threshold (plan section 10)
  is mis-evaluated, a KILLED-heavy scenario escapes quarantine, or a benched
  scenario auto-unquarantines on a window it never ran in.
- **Retry decision.** Driver-INVALID + policy `once` + attempt 1 -> retry; attempt
  2 -> no retry; PARSEK-FAIL -> never retry; KILLED -> no retry; analyzer-error /
  verifier-tooling INVALID -> retry the subprocess only, not a fresh boot (N6/S14).
  Fails if a real defect is retried into a false pass, or a transient boot flake is
  not retried (nightly noise).
- **Log-validation profile selection.** `expectations.recordings.count.max == 0`
  -> the harness passes the recording-rules suppression (REC-001/REC-003 off,
  SES/FMT/WRN on, B1); `count.max > 0` -> no suppression (REC rules mandatory); a
  KILLED attempt -> `-KilledRun` (SES-000/SES-001/REC-001/REC-003 off, FMT/WRN on,
  S13); both conditions together -> both profiles applied. Fails if a clean
  no-recording B10 run reds on REC-001/REC-003 (the flagship daily loop could never
  PASS), or a killed run reds on marker-pairing.
- **BATCH_COMPLETE / results / RED / analysis-JSON parsers.**
  `parse_batch_complete_line` extracts total/passed/failed/skipped/category/scene
  from the M-A3 `v1` line and REJECTS a future `v2` line (contract guard);
  `parse_results_failures` counts FAIL rows; `parse_analysis_red_token` reads the
  terminal `RED=` (anchored end-of-line, never an earlier literal);
  `parse_analysis_json` reads `failNonBaselined`/`staleNonBaselined` + the findings
  list (rule ids) from `.analysis.json` (S1) and drives the BASELINE-* precedence
  (S2). Fails if a `v2` line is silently misparsed by a v1 harness, a save leaf
  containing `RED=0` earlier in the header spoofs the gate, or the FAIL/STALE split
  is read from the txt header instead of the JSON.
- **Admission reuse.** `provlib.compare_manifest` over a drifted manifest returns
  the field diff the harness refuses on; identical -> admit. Fails if a drifted
  instance is admitted (certifies the wrong stack) -- guards the M-A6 seam reuse.
- **Run-lock reuse.** `provlib.acquire_lock` refuses a live foreign holder,
  reclaims a dead one. Fails if two harness runs both stage over one instance.

### Result / determinism tests

- **Result JSON round-trip + determinism.** Writing then reading a result yields an
  equal object; the same inputs produce byte-identical JSON (stable key order, no
  volatile absolute paths in the compared fields). Fails if result diffs churn or a
  field is dropped, breaking the coverage parser.
- **Coverage report stability.** The same specs+results produce byte-identical
  `coverage.json`. Fails if nondeterministic dict iteration reorders values and
  makes coverage diffs unreadable.
- **Schema gate.** A result / spec / manifest with a future `schema` is refused
  with a clear message, not mis-parsed. Fails if a schema bump silently mis-admits
  old data.

### Log-assertion tests (pytest over the harness log buffer)

- Every classify branch emits a `[Harness][...][Classify] verdict=<V> reason=<...>`
  line; the boot-crash / boot-timeout / kill / short-circuit branches each emit
  their line. Fails if a decision branch is silent (a scheduled unattended run
  would be undebuggable, the whole point of the harness log).
- XPASS emits the actionable amber line. Fails if an expected-fail-now-passing
  scenario leaves no trail telling the maintainer to remove the key.

### Operator-driven / fake-KSP integration (run.py shell)

Per the MEMORY note that an agent cannot pilot KSP, the LIVE end-to-end path is a
PENDING-OPERATOR runbook; the shell is additionally covered by a fake-KSP stub so
CI exercises the tail/kill/verify plumbing without a real game:

- **Fake-KSP happy path.** A stub process reads `parsek-test-commands.txt` and
  writes a scripted `parsek-test-responses.txt` (LoadGame OK, RunTests OK,
  FlushAndQuit OK) plus a synthetic KSP.log with a BATCH_COMPLETE line and a clean
  analyzer report; run.py drives it end to end -> PASS. Fails if the tail/dedupe/
  verify wiring mishandles a well-formed run.
- **Fake-KSP hang -> KILLED.** The stub never writes a RunTests response; the
  watchdog must kill the stub tree within budget and classify KILLED, and the
  killed-run log-validation mode must be selected. Fails if the harness hangs (no
  budget enforcement) or reds a killed run on marker-pairing.
- **Fake-KSP boot crash -> INVALID + retry.** The stub exits during boot-wait with
  no response; run.py must classify INVALID(boot-crash) and retry once. Fails if a
  boot crash wedges the run or is not retried.
- **PENDING-OPERATOR live smoke (once, before nightly is trusted).** On a
  provisioned stock-minimal instance, run the daily selection: confirm the harness
  admits the instance, stages the fresh-career fixture, boots via the seam
  `LoadGame`, self-fires (or drives) the `RecordingInvariants` batch, quits via
  `FlushAndQuit` / autorun-exit, runs the analyzer in Forbid (`RED=0`), the log
  validation with the recording-rules suppression on (the daily loop is a
  `count.max == 0` no-recording scenario, so REC-001/REC-003 are suppressed and the
  clean run PASSES rather than falsely redding, B1) and killed-run mode off, and the
  results parse, and writes a PASS result + a coverage line. Grep evidence: the
  `[Harness]` launch/verdict lines, the
  `BATCH_COMPLETE v1 ... failed=0` line, the `RED=0` analyzer header, and the
  written `harness/results/<runId>.json`. Then force a KILLED (a deliberately
  over-budget scenario) and confirm the KILLED verdict + killed-run log mode + the
  collect-logs snapshot. This is the first fully-unattended KSP-open cycle the plan
  section 11 Phase 2 exit criterion names.

## Deferred Items and Open Questions

Recorded so they are not lost; none blocks the v1 seam-driven daily loop.

- **Autopilot driver kind (M-B1).** The `autopilot` driver is reserved and
  rejected by v1 validation; kRPC + MechJeb flight, the mission library, and craft
  staging land with M-B1. v1 delivers the daily tier (inject, boot, batch, exit,
  analyze) with zero flying.
- **World-diff / ledger oracle verifier (M-B2).** The `[expectations.world]` /
  `[expectations.route]` blocks parse and validate today but are SKIPPED by the v1
  verifier chain; the CareerSaveParser world-diff and the action-manifest ledger
  oracle land with M-B2, at which point the reserved blocks activate with no spec
  format change.
- **Rewind / loop expectations (M-C2).** `[expectations.rewind]` /
  `[expectations.loop]` are reserved; the seam verbs that drive them
  (`InvokeRewind`, `StartLoopPlayback`, `TimeJump`) are M-A2 RESERVED verbs, so v1
  spec validation rejects a step using them. They activate when M-A2 implements the
  verbs and M-C2 adds the verifiers.
- **Multi-session orchestration (M-C1).** v1 runs one KSP process per scenario
  attempt. Fly-commit-restart-observe cycles (re-fly merges, routes, synodic
  cadence, loop self-overlap) need the multi-session primitive; the result record
  and coverage schema already carry `attempt` and per-step detail that a
  multi-session extension will build on.
- **The two dev-script switches** (`-FreshSaveGate` on `analyze-recordings.ps1`,
  `-KilledRun` on `validate-ksp-log.ps1`) are the only non-Python additions M-A5
  needs; both are additive and inert by default. They are called out here rather
  than assumed so their tiny production-adjacent surface is reviewed with this
  module.
- **Q1: budget timing uses wall-clock.** The watchdog uses host wall-clock; an NTP
  jump mid-run could distort a budget. Low impact on a scheduled dev PC; a
  monotonic source is the harden.
- **Q2: process-tree kill portability.** v1 targets Windows (KSP + the dev PC);
  the process-tree kill is `taskkill /T`. A future Linux/Proton path needs a
  process-group kill; scoped out.
- **M-A5.1: explicit seam per-step `budget=` argument.** v1 lives within the seam's
  fixed 600s fallback deferral ceiling (spec caps RunTests budgets at 540s, S8);
  threading the spec's per-step budget down to the seam command so the seam adopts
  it is deferred to M-A5.1.
- **M-A5.1: subprocess-scoped tooling retry (v1 adaptation 4).** The verifier-chain
  prose above (S14, edges 12/30) describes a retryable tooling / analyzer-error
  INVALID as "re-running only that verifier subprocess, not a fresh KSP boot". v1
  does NOT yet do that: a retryable INVALID re-runs the WHOLE attempt through
  `_run_scenario_with_retry` (fresh stage + fresh launch + fresh verifier chain), so
  an analyzer-error retry currently burns another boot. Threading a subprocess-scoped
  retry (re-invoke just the wedged `analyze-recordings.ps1` / `validate-ksp-log.ps1`
  over the already-produced save) is deferred to M-A5.1; the whole-attempt retry is
  correct, just more expensive.
- **M-A4 / M-B5: preset-scoped injection.** `injectedRecordings` in v1 is
  `none | all-synthetic` (S4); a named corpus/preset subset lands with the M-A4
  harvest queue and the M-B5 preset library.
