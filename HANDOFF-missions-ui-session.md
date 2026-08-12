# HANDOFF — Missions/Recordings UI improvement session (remote → local)

**TEMP FILE — delete before this branch's PR is merged.** Written 2026-08-12 by the remote
session that produced PR #1462 and commit `4b37132`, so a local session can resume with full
context from this one file.

## Local setup (do this first)

Per `.claude/CLAUDE.md` hard rules: do NOT work in the main `Parsek/` checkout. Create a
dedicated worktree for this branch:

```bash
cd Parsek
git fetch origin claude/missions-recordings-ui-analysis-hwznj7
git worktree add ../Parsek-missions-ui claude/missions-recordings-ui-analysis-hwznj7
cd ../Parsek-missions-ui
```

## State of the work

Two workstreams, both grounded in five research docs merged to `main` via **PR #1462**
(`docs/dev/research/*-2026-08-12.md`):

- `mission-presentation-ux-analysis-2026-08-12.md` — issue 1: why the Missions tab is hard to
  parse; failure ranking F1–F11; 3-tier staged fix plan (T1.x / T2.x / T3).
- `crosstree-dock-loop-coherence-analysis-2026-08-12.md` — issue 2: dock/undock topology
  invariants I1–I7, the AB/CD→BC/AD scenario walk, tree-accretion diagnosis, 9 prioritized
  recommendations.
- Three structure extracts (missions UI / recordings UI / dock-loop model) with ASCII mockups
  and file:line references — the evidence base for both analyses.

### Issue 1 — DONE: Stage 1 (Tier 1), commit `4b37132` on this branch. NOT YET VERIFIED.

All seven items implemented (new pure module `Source/Parsek/MissionPresentation.cs` + 25 tests
in `Source/Parsek.Tests/MissionPresentationTests.cs` + `MissionsWindowUI.cs` rendering changes
+ 9-line tooltip-renderer wiring in `RecordingsTableUI.cs`):

- T1.1/T1.2 mission-header summary line + `TTL` → `Next launch` (col 90→105px)
- T1.3 delta-phrased interval labels (`after undock: <peel> left - (composition)`)
- T1.4 same-tree two-parent dock partner naming (`Docked with <vessel>`, clipped cell + tooltip)
- T1.5 tooltips (include checkbox = the F2 fix, loop, 4 period states, next-launch state words,
  Clone/Archive/Warp-to, partner rows)
- T1.6 ScreenMessage naming the mission whose loop was cleared
- T1.7 `#` ordinal hidden in Basic; `Partner journey -` → `Docked partner:`

**The remote container had NO dotnet SDK and NO KSP assemblies — nothing was compiled.**

### FIRST TASK for the local session — verify Stage 1

1. `cd Source/Parsek.Tests && dotnet test` — all tests must pass (25 new
   MissionPresentationTests + the full existing suite; watch `GrepAuditTests`).
2. Watch for these compile surfaces flagged at review: `TextClipping.Clip` (first use in repo),
   `GUILayout.Toggle(bool, GUIContent, ...)` overloads.
3. Build + deploy (`cd Source/Parsek && dotnet build`, verify deployed DLL per CLAUDE.md
   recipe; good marker literal: `Docked partner:` or type name `MissionPresentation`) and
   eyeball in-game:
   - the mission header bubble (restructured horizontal→vertical wrapper; same x positions by
     construction, but one layout change worth seeing),
   - Timeline `GoTo` reveal-scroll still lands on the right mission
     (`CaptureRevealAnchor` measures the new vertical group's rect),
   - tooltips actually render on the Missions tab (new wiring in
     `RecordingsTableUI.cs:1393-1408`),
   - the summary line's `·`/`→` glyphs render in the KSP font (swap the two consts at
     `MissionPresentation.cs:26-27` if not).
4. Fix whatever falls out; amend/commit on this branch.

### NEXT: Issue 1 Stage 2 (after Stage 1 is green)

Per the analysis §3 Tier 2 (read it first):

- **T2.1** mission header becomes a two-line summary block with a narrative line
  (`Kerbin → Mun → Kerbin · 2d 3h · Jeb, Bob, Val · Loops ~6.4d · Next: T-2d 4h`); body path
  derivable like the Recordings tab's `GetSegmentPhaseLabel`.
- **T2.2** flatten the staircase: one row per physical vessel (through-line), depth encodes
  ONLY separation lineage, inline event phrase (`Launch → drop booster → Landed`); interval
  checkboxes move into an expandable per-vessel detail (Basic may hide interval sub-rows and
  offer per-vessel include that expands to explicit `ExcludedIntervalKeys` — the non-cascading
  contract is a hard constraint, expand to key sets, never change the semantics).
- T2.3 (chronological event digest) is deliberately deferred — it merges with issue-2
  recommendation #4 (see the coherence analysis §6 table) so it is built once, fed by the
  dock-event graph.

Estimated 1–2 weeks. `MissionThroughLineBuilder` / `MissionThroughLineView` already provide the
per-vessel abstraction; `MissionPresentation` is the place for the new pure derivations.

### Issue 2 — PLANNING NOT STARTED. Prompt for a Fable planning agent below.

Run this in its own session/worktree; it produces a design doc, no implementation. It shares a
seam with issue-1 T2.3 (event digest) and T1.4 (partner naming) — the design must state who
owns what.

---

## Fable planning prompt for issue 2 (copy verbatim into a fresh Fable session)

> You are a senior architect planning improvements for **Parsek** (KSP mod recording flights
> and replaying them as looping "ghost" missions). Your task is to produce the **design doc +
> implementation plan** for fixing the cross-mission dock/undock narrative and loop-coherence
> problems ("issue 2"). You are PLANNING only — no implementation.
>
> **Required reading, in order (all in the repo, merged via PR #1462):**
> 1. `docs/dev/research/crosstree-dock-loop-coherence-analysis-2026-08-12.md` — the
>    authoritative analysis. Its §6 lists 9 prioritized recommendations; your plan covers
>    **#1–#6** as committed scope, **#7** (double-clock playtest + advisory) as a verification
>    task, and **#8** (D-provenance feasibility investigation) as a spike with an explicit
>    go/no-go gate. #9 is out of scope.
> 2. `docs/dev/research/dock-loop-model-extract-2026-08-12.md` — the verified data-model
>    extraction (branch topology, the AB/CD scenario walk, loop-unit semantics, what tests pin).
> 3. `docs/dev/research/mission-presentation-ux-analysis-2026-08-12.md` — the sibling issue-1
>    analysis; its T1.4 (dock-partner naming) and T2.3 (event digest) overlap your #3/#4 — your
>    design must define the shared seam so the two workstreams don't collide. NOTE: T1.4 is
>    already implemented (same-tree two-parent case only) in `Source/Parsek/MissionPresentation.cs`
>    (`ResolveSameTreeDockPartnerVesselName`) — your #3 generalizes it and should subsume that
>    call site rather than duplicate it.
> 4. `docs/dev/dock-undock-recording-structure.md`, `docs/dev/design-mission-crosstree-dock.md`,
>    `docs/dev/design-mission-abstractions.md` — the binding design docs you are extending.
> 5. `docs/dev/development-workflow.md` and `docs/dev/design-doc-template.md` — your deliverable
>    follows this process and template.
> 6. Source ground truth (verify every load-bearing claim yourself before designing on it):
>    `ParsekFlight.cs:10770-10787` and `:12118-12135` (dock seam + partner-PID stamping),
>    `MissionCrossTreeDock.cs` (FindLinks tree-inequality skip at ~:58-63; journey walk
>    ~:599-619), `RecordingTree.cs:433-442` (BackgroundMap eligibility),
>    `MissionLoopUnitBuilder.cs`, `GhostPlaybackLogic.SpanClock.cs`, `EffectiveState.cs`
>    (ERS/ELS routing rules — any new derivation reading `RecordingStore.CommittedRecordings`
>    must route through `EffectiveState` or be allowlisted, see the grep gate in
>    `.claude/CLAUDE.md` → "ERS / ELS routing").
>
> **Scope, restated:** (1) stamp `BranchPoint.TargetVesselPersistentId` unconditionally at
> dock, decoupled from route eligibility — audit every consumer that may assume
> `target≠0 ⇒ route-eligible` first; (2) same-tree dock-link derivation (guid-gated via
> `VesselLaunchIdentity`, recovering docks like A→D from the absorbed side); (3) a load-time
> **global dock-event graph** module + bidirectional partner naming surfaced in both the
> Missions and Recordings tabs; (4) a mission **event digest** (chronological dock/undock story
> rows with named partners and GoTo cross-navigation — design it as the upgraded form of
> issue-1's T2.3, one feature not two); (5) **chapter grouping** in mission selection for
> accreted sub-stories (dock-with-foreign / `VesselSwitchContinuation` roots), with one-click
> include/exclude that expands to explicit `ExcludedIntervalKeys` sets (the non-cascading
> contract must be respected, not changed); (6) loop seam markers R2/R3 and gap statements R5
> from the analysis §5.
>
> **Hard constraints:** recording schema stays at format 1 / generation 4 — **no schema bump,
> no migration paths**; old recordings with `TargetVesselPersistentId = 0` must degrade to
> exactly today's behavior. Never trust a bare `persistentId` match — all identity resolution
> guid-gates through `VesselLaunchIdentity`. Pinned behaviors you must not regress: everything
> asserted by `CrossTreeDockLoopUnitInGameTest` and `MissionDockCompositionRuntimeTest` (read
> both), the merged-ghost render contract, window partitioning at branch UTs, and "two
> disjoint-tree missions may loop concurrently". The dock graph is a **pure derivation**
> (headless-testable static core per the Testing Requirements in `.claude/CLAUDE.md`); nothing
> in the playback path consumes it except the opt-in features above. Per-frame cost discipline:
> the graph builds at load/refresh, never per-frame; seam markers must not add
> per-ghost-per-frame work beyond a UT-window check.
>
> **Deliverable:** a design doc at `docs/dev/design-dock-event-graph.md` following the
> template, containing: the graph's data structures and build algorithm (inputs, identity
> resolution rules, degradation table for missing PIDs/guids); the consumer API for UI naming,
> event digest, chapter grouping, and seam markers; the T1.4/T2.3 seam with the issue-1
> workstream (who owns which file regions, what lands first); the #8 spike design
> (route-window part-PID ∩ split-child snapshot intersection — including the specific
> verification of whether split-child snapshots preserve live part PIDs, and the go/no-go
> criterion); a test plan (unit tests for the pure graph core with `RecordingBuilder`/
> `ScenarioWriter` generators, which in-game test categories gain cells, whether a harness
> scenario TOML is warranted — check `harness/scenarios/` and note the
> `CommittedBatchTallySourceSyncTests` trap in `.claude/CLAUDE.md` before adding any
> `[InGameTest]`); and a PR-by-PR implementation sequence with rollback-safe ordering (#1 and
> #2 are independent and land first). Flag every open design decision needing the owner's
> input as a numbered question at the top.

---

## Session log (what already happened, so nothing is redone)

1. Three extraction agents mapped the Missions UI, Recordings UI, and dock/loop data model.
2. Two Fable analysts produced the issue-1 and issue-2 analyses (all five docs → PR #1462,
   squash-merged to `main`).
3. This branch was restarted from post-merge `main`; an Opus agent implemented issue-1 Stage 1;
   the supervisor review verified every referenced symbol against source (no compile possible
   remotely), then committed `4b37132` and pushed.
4. Known review notes riding with `4b37132`: `GetSummaryFacts` builds its Verbose log string
   eagerly (once per tree per frame on cache miss — acceptable, but the `Func<string>` overload
   of `VerboseRateLimited` would avoid it); `.claude/CLAUDE.md`'s key-source list does not yet
   mention `MissionPresentation.cs` (add a line when convenient); T1.3 peel naming picks the
   first matching sibling when several pieces peel at the same UT (1e-3 epsilon).
