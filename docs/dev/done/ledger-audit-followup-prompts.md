# Ledger Audit — Follow-Up Session Prompts

Five self-contained prompts, one per recommendation from
`docs/dev/ledger-state-reconstruction-audit.md`. Each is meant to be pasted into a
**fresh session** as the opening instruction to an orchestrator agent. They are ordered
by the priority in the audit (§8); they are mostly independent, but **Prompt 4
(LedgerTrace) should land after Prompt 2 (logging gaps)** since the trace lines reuse the
same per-identity logging, and **Prompt 1** is the highest-leverage standalone win.

Every prompt assumes the agent will:
- Read `docs/dev/ledger-state-reconstruction-audit.md` and
  `docs/dev/development-workflow.md` first.
- Follow the workflow's **plan → review → fix (loop) → implement → review → fix** cycle,
  dispatching **clean-context subagents** for each stage while the main agent stays the
  orchestrator (keeps its own context lean, reviews subagent output, decides
  proceed-vs-iterate).
- Obey `.claude/CLAUDE.md`: build/test commands, logging requirements (every decision +
  state transition logged), testing requirements (every new logic method gets unit
  tests + log-assertion tests), the ERS/ELS routing rule (route reads of
  `RecordingStore.CommittedRecordings` / `Ledger.Actions` through
  `EffectiveState.ComputeERS/ComputeELS` unless allowlisted), and the Post-Change
  Checklist (OnSave/OnLoad, generators, `dotnet test`, CHANGELOG, todo doc).
- Branch from `origin/main` into a dedicated worktree/branch, and open a PR at the end
  (no AI attribution / no Co-Authored-By).

---

## Prompt 1 — Rewind read-back divergence guard (production safety)

```
Implement a rewind-time read-back divergence guard for the Parsek ledger
reconstruction. Goal: when a rewind reconstructs career state, detect (and loudly log,
optionally abort) any case where the recalculated target diverges from the ground-truth
that the RewindPoint quicksave just restored — so a silent career-corruption bug becomes
a caught, logged event.

START by reading docs/dev/ledger-state-reconstruction-audit.md (esp. §6 "ground-truth
oracle" and §2 pipeline) and docs/dev/development-workflow.md. Follow the workflow:
plan -> review -> fix (loop until clean) -> implement -> review -> fix. Use clean-context
subagents for each stage; you are the orchestrator and keep your own context lean.

KEY FACTS (verify in code, don't trust blindly):
- On rewind, RewindInvoker loads the RP quicksave (.sfs) = a real KSP save at the rewind
  UT with actual funds/science/rep/facilities/vessels, THEN calls
  LedgerOrchestrator.RecalculateAndPatch(double.MaxValue) at RewindInvoker.cs:908. So the
  live KSP economy *immediately after load, before PatchAll writes* is ground truth.
- Apply funnel: GameActions/KspStatePatcher.cs:39 (PatchAll) -> PatchScience/PatchFunds/
  PatchReputation/PatchFacilities/... Each computes a target then writes via
  AddFunds/AddScience/SetReputation. Funds/Science/Rep already log old->new at Info
  (:733/:160/:803).
- The drawdown guard (KspStatePatcher.cs ~:2295) and authoritativeReduction flag
  (LedgerOrchestrator.cs:1786) already model "is this an authorized drawdown vs a buggy
  clobber" — integrate with that semantics, don't fight it.

SCOPE: capture the pre-patch live values (funds, science pool, reputation, and ideally
facility levels) at the rewind apply boundary; compare against the about-to-be-written
recalc targets; if |divergence| exceeds a tolerance AND the change is NOT an expected
authoritative rewind drawdown, emit a loud ParsekLog.Warn naming each diverging resource
with old/groundTruth/target/delta, and gate an optional abort behind a clearly-named
flag (default: warn-only, do NOT abort, to avoid bricking a legit rewind). Keep the hook
pure/testable: put the comparison decision in an internal static predicate
(e.g. ResolveRewindDivergence(...)) so it can be unit-tested without KSP singletons.

DESIGN CONSTRAINTS: this is a guard, not a behavior change — by default it must not alter
any successful rewind. Make the abort opt-in. Log every branch (no divergence / expected
authoritative drawdown / flagged divergence). Decide whether this is a small enough
change to skip the design-doc step (it likely is — it's an instrumentation/guard, single
subsystem) and note that decision explicitly to the reviewer.

TESTS: unit-test the divergence predicate (matching/diverging/authoritative-drawdown
cases) and add log-assertion tests via ParsekLog.TestSinkForTesting that the Warn fires
with the right fields. Consider an in-game Rewind-category test that forces a synthetic
divergence and asserts the Warn. Run `cd Source/Parsek && dotnet build` and
`cd Source/Parsek.Tests && dotnet test`.

Update CHANGELOG.md and docs/dev/todo-and-known-bugs.md in the same commit. Open a PR
targeting main when green; do a clean-context final review first.
```

---

## Prompt 2 — Close logging gaps 1–3 (per-subject science, tech nodes, contracts)

```
Close the three HIGH-risk per-identity logging gaps in the Parsek ledger apply boundary
so a per-subject-science / tech-node / contract corruption is debuggable from a
default-level KSP.log instead of only Verbose.

START by reading docs/dev/ledger-state-reconstruction-audit.md (§4.1 table + §4.2 gaps
1-3) and docs/dev/development-workflow.md. Follow plan -> review -> fix (loop) ->
implement -> review -> fix with clean-context subagents; you orchestrate and stay lean.
Also read .claude/CLAUDE.md "Logging Requirements" (batch-counting convention: per-item
decisions get local int counters + ONE summary after the loop; use VerboseRateLimited
for per-frame, Verbose for one-shot).

THE GAPS (verify each in GameActions/KspStatePatcher.cs):
- GAP 1 (per-subject science, :871 `kspSubject.science = target`): no per-subject
  old->new at any level; only aggregate `patched=/cleared=` at Info (:887). Also note
  this path is left UNCLAMPED by the drawdown guard (~:2295) — extra reason to log it.
- GAP 2 (tech nodes, :377 SetTechState / :388 protoNodes.Remove): per-node identity only
  at Verbose ("first 10 missing", :428); Info is counts only (:412).
- GAP 3 (contracts, :2177 RemoveAt / :1727 currentContracts.Add): contractId only at
  Verbose/Warn; Info aggregate at :1774.

APPROACH: do NOT spam Info with one line per subject/node/contract on every recalc (there
can be hundreds). Instead, follow the existing batch-counting convention and surface
*changed* identities compactly: keep the Info aggregate, but when the changed-set is
non-empty and bounded, append the changed identity list (or a bounded sample with a
"+N more" suffix) to the Info line — or emit identities through a change-gated path
(ParsekLog.VerboseOnChange keyed per subject/node/contract id) so a default log still
shows WHICH item changed when it changes, without per-frame spam. Match how kerbal roster
apply already logs per-item at Info (KerbalsModule.cs:1398 etc.) for the bounded cases.
Decide the Info-vs-change-gated split per gap and justify it to the reviewer (volume is
the deciding factor).

TESTS: add log-assertion tests (ParsekLog.TestSinkForTesting) proving each gap now emits
the changed identity + old->new (or changed-id) when a subject/node/contract flips, and
that steady-state (no change) does NOT spam. These pull double duty as diagnostic-coverage
guards. Many KspStatePatcher tests run with SuppressUnityCallsForTesting=true and
null singletons — structure the new logging so the identity/decision logging is reachable
in that headless mode (log from the target-computation/decision step, not only from the
post-singleton-write step), so the tests can assert it. Run build + dotnet test.

Update CHANGELOG.md + todo doc in the same commit. Clean-context final review, then PR to
main.
```

---

## Prompt 3 — In-game ground-truth verification harness

```
Build an in-game test harness that verifies Parsek's ledger reconstruction against an
independent ground-truth KSP save at the same UT, including vessels — the closed
rewind/recalc-vs-actual-save loop that does NOT exist today.

START by reading docs/dev/ledger-state-reconstruction-audit.md (§5.2 "the critical
question", §6 oracle, §7 in-game runner capability) and docs/dev/development-workflow.md.
This is a larger, multi-part feature touching test infrastructure — consider a short
design doc (workflow step 3) before planning, since it persists a new test category and a
save-parsing helper. Follow plan -> review -> fix (loop) -> implement -> review -> fix
with clean-context subagents; you orchestrate.

WHAT EXISTS TO REUSE:
- InGameTests/ runner: reflection discovery (InGameTestRunner.DiscoverTests:227),
  [InGameTest] attrs with Category/Scene/RunLast/AllowBatchExecution/
  RestoreBatchFlightBaselineAfterExecution; multi-frame IEnumerator coroutines;
  InGameAssert.ApproxEqual (float/double/Vector3 + tolerance) / Skip.
- Quicksave/quickload already wired: QuickloadResumeHelpers.TriggerQuicksave,
  ValidateQuicksaveStructure (parses a .sfs without mutating FlightGlobals).
- Live reads: Funding/ResearchAndDevelopment/Reputation.Instance, FlightGlobals.Vessels,
  vessel.id/persistentId. SnapshotFinancials/RestoreFinancials helpers at
  RuntimeTests.cs:14541.
- LedgerOrchestrator.RecalculateAndPatch is the reconstruction entry.

THE HARNESS (one new [InGameTest] family, Career-only, RestoreBatchFlightBaseline=true so
it self-cleans):
1. From a career save at the current UT, capture a quicksave snapshot S (ground truth:
   economy + the ConfigNode set of VESSEL nodes with their resource totals).
2. Parse S's .sfs INDEPENDENTLY of the ledger (reuse/extend the ValidateQuicksaveStructure
   path) to extract: funds, science (pool + per-subject), reputation, facility levels,
   active contract set, milestone set, and the vessel set (ids/persistentIds + resource
   totals).
3. Run LedgerOrchestrator.RecalculateAndPatch and read the reconstructed/live values.
4. Diff reconstructed vs S with tolerances; InGameAssert each facet; on mismatch produce a
   structured divergence report (which facet, expected-from-save vs reconstructed).
   IMPORTANT: this must be a NON-circular comparison — compare recalc output to the
   INDEPENDENTLY-PARSED save S, NOT module-getter-vs-live-after-patch (that tautology is
   exactly what TopBarReflectsLedgerAfterRecalc at RuntimeTests.cs:15573 already does and
   why it proves nothing). Document this distinction in the test's Description.
5. Skip cleanly (InGameAssert.Skip) when not Career, when singletons are null, or when a
   live/pending tree would defer patching (mirror the guards in
   TopBarReflectsLedgerAfterRecalc).

Vessels are NOT touched by the ledger (it reconstructs scalars only) — so the vessel facet
asserts cross-subsystem consistency: e.g. that a recovered-vessel funds credit in the
recalc matches whether that vessel is actually present/absent in S after restore. Scope
the vessel comparison realistically and mark deeper vessel-state checks as v1-deferred if
needed.

TESTS/VALIDATION: the harness IS the test, but also add pure unit tests for the new
save-parsing/diff helpers (Source/Parsek.Tests/) with synthetic .sfs fixtures (use the
Generators/ — ScenarioWriter etc.). Run `dotnet build` + `dotnet test`. For the in-game
part, document the manual run recipe (Ctrl+Shift+T, the new Category) in the PR.

Update CHANGELOG + todo + .claude/CLAUDE.md (new in-game test category) as needed. Clean
final review, PR to main.
```

---

## Prompt 4 — `LedgerTrace`: event-driven ledger state tracer

```
Build a gated, event-driven "LedgerTrace" observability system for the Parsek ledger
apply boundary, modeled on the existing render tracer pattern but adapted to discrete
recalc events (NOT a per-frame probe). It instruments every reconstructed value-change
and reconciles computed-vs-live (read-back) so career-corruption bugs are traceable.

PREREQUISITE: ideally land after the Prompt-2 logging work (per-identity change lines) so
LedgerTrace reuses, not duplicates, that logging.

START by reading docs/dev/ledger-state-reconstruction-audit.md (§7 tracer template, §8
rec 4) and docs/dev/development-workflow.md. Read the templates: MapRenderTrace.cs +
MapRenderProbe.cs (the pattern), GhostRenderTrace.cs (sibling), and ParsekLog.cs
(VerboseOnChange:271, RecState:511, test seams). Follow plan -> review -> fix (loop) ->
implement -> review -> fix with clean-context subagents; you orchestrate and stay lean.

CRITICAL DESIGN NOTE (from the audit): do NOT clone the per-frame MonoBehaviour probe.
Ledger state changes on discrete recalc/patch events, so a per-frame LateUpdate probe is
wasted cost. LedgerTrace is EVENT-DRIVEN, hooked at the KspStatePatcher Patch* sites.

REUSE THE PATTERN, THESE LAYERS:
- Gating: a `ledgerTracing` setting, near-zero cost when off (single bool early-return in
  every emit), mirroring MapRenderTrace.IsEnabled (:378). Add the flag via the 4-file
  template: ParsekSettings.cs:56 (field + [CustomParameterUI], default false),
  UI/SettingsWindowUI.cs:458 (Diagnostics toggle, suffix "(Warning: huge logs)"),
  ParsekSettingsPersistence.cs:46 (key + record/load/restore), defaults-reset at
  SettingsWindowUI.cs:195. Add a ForceEnabledForTesting seam.
- Tier-A structural: a RecState-style (ParsekLog.RecState, :511) grep-stable one-line
  snapshot per recalc — funds/science/rep/facilities=L.../techNodes=N/contracts=M plus
  cutoff + authoritativeReduction (the LedgerOrchestrator.cs:1786 decision). Always
  emitted at Info when enabled.
- Tier-B change-based truth: per-identity (subject/node/facility/contract id) change
  lines via VerboseOnChange keyed by id — emit only when that identity's value flips.
- Tier-C anomaly: a read-back reconcile. At each Patch*, capture the computed target as
  "intent", and after the live write read the value back; a pure, Unity-free predicate
  (e.g. IsResourceDrift(target, actual, tol)) decides mismatch -> emit a "ledger-vs-truth"
  anomaly Warn. Keep predicates pure + unit-tested (like IsIconJump). This is the ledger
  analogue of MapRenderProbe's ReconcileLineState.

Map the render concepts: "intent" = computed recalc target; "truth" = live
Funding/RnD/Reputation.Instance + facility level; per-entity key = action/subject/node/
facility/contract id (NOT a vessel pid). No detailed-window-registry needed unless it
earns its keep — justify if you add it.

Keep the formatters self-contained (the render tracers deliberately duplicate rather than
share — do NOT refactor MapRenderTrace/GhostRenderTrace). Respect the ERS/ELS routing
rule for any CommittedRecordings/Ledger.Actions reads.

TESTS: unit-test every pure predicate + the RecState formatter (matching/diverging/
boundary), log-assertion tests for each tier (structural snapshot present, change line
fires only on change, anomaly Warn on injected drift), and a gating test (nothing emitted
when ledgerTracing off / ForceEnabledForTesting false). Run build + dotnet test. Verify
OnSave/OnLoad persistence of the new setting (Post-Change Checklist).

Update CHANGELOG + todo + .claude/CLAUDE.md (new LedgerTrace file in the file index).
Clean final review (serialization + new-subsystem attention), PR to main.
```

---

## Prompt 5 — xUnit coverage for the live apply boundary

```
Add headless xUnit coverage for the Parsek KspStatePatcher apply boundary so the
compute-delta -> write -> read-back loop is actually tested, instead of null-skipped under
SuppressUnityCallsForTesting.

START by reading docs/dev/ledger-state-reconstruction-audit.md (§5.1 layer (e), §5.3
risk 1) and docs/dev/development-workflow.md. This is a test-infrastructure change (pure
refactor for testability + new tests, no gameplay change) — skip the design doc, go
plan -> implement -> review per the workflow's shortcut, with a clean-context reviewer.
You orchestrate; use a subagent for the exploration/plan and another for review.

THE PROBLEM (verify): KspStatePatcherTests run with
KspStatePatcher.SuppressUnityCallsForTesting = true, and the real mutation sites are
behind `if (Funding.Instance == null) return;` etc. In xUnit those singletons are null,
so every "apply" test hits the early-return. Only the pure target-computation helpers
(BuildTargetTechIdsForPatch, AdjustSciencePatchTargetForPending*, BuildSubjectIdsForPatch,
BuildFacilityPatchTargets) and null-guard logging are covered. The compute-delta ->
AddFunds/AddScience/SetReputation -> read-back loop is exercised live by only 2 in-game
tests with synthetic inputs.

APPROACH (pick the lowest-risk that works; justify in the plan):
Option A — extract the numeric core: refactor each Patch* so the
"given currentValue + target, produce the delta/clamp decision and the resulting value"
logic lives in an internal static pure function (taking current as a parameter instead of
reading Funding.Instance directly). Test those pure functions exhaustively in xUnit
(matching/clamped/authoritative/seed-missing/no-op/suspicious-drawdown cases), asserting
the computed delta AND the resulting value AND the log line (TestSinkForTesting). The thin
Unity-call wrapper stays untested headlessly but becomes trivial.
Option B — a minimal injectable economy seam: introduce a tiny interface/delegate for
"read current / write delta" that defaults to the KSP singletons but can be faked in
xUnit, letting tests drive the full Patch* path headlessly and assert the faked backend
received the right writes. Higher blast radius — only if Option A can't capture the
read-back semantics (e.g. the drawdown guard's running-vs-available distinction).

Prefer Option A unless review shows it leaves the dangerous read-back/clamp interaction
untested. Either way, do NOT change runtime behavior — this is a testability refactor;
existing tests must still pass unchanged.

COVER THE RISKY PATHS the audit names: per-subject science apply (unclamped!),
tech-node-set apply, facility-level apply — at least their delta/decision math against a
provided current value. Add the "what makes it fail" justification to every test (no
vacuous asserts).

Run `cd Source/Parsek && dotnet build` and `cd Source/Parsek.Tests && dotnet test` — all
green. Update CHANGELOG + todo doc. Clean-context final review (it touches the
career-critical apply boundary), PR to main.
```

---

### Orchestration notes for the human

- **Independence:** 1, 3, 5 are fully independent. 4 depends on 2 (shared per-identity
  logging). Run 1 first (highest leverage, smallest), then 2, then 3/5 in parallel, then
  4.
- Each session's orchestrator should keep the report doc + workflow doc paths in its
  opening turn and otherwise delegate reading to subagents to stay lean.
- All five end in a PR to `main`; none should `git merge` into the main checkout locally.
