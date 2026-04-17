# Career State Window — Design Pass (#416)

**Branch:** `feat/416-career-window` (worktree at `C:\Users\vlad3\Documents\Code\Parsek\Parsek-416-career-window`)
**Source:** follow-up from PR #320 (#385 Kerbals window). Spec in `docs/dev/todo-and-known-bugs.md` §416.
**Status:** design pass, pre-implementation. No code written yet.

Four career modules (`ContractsModule`, `FacilitiesModule`, `StrategiesModule`, `MilestonesModule`) have no UI surface today. Their state lives in the ledger and leaks through the Timeline footer, but the per-module detail is invisible. This doc specifies a new top-level Parsek window that shows them, plus the tiny companion hookup from Kerbals-Fates rows to the Timeline.

---

## 1. Problem

A player using Parsek to plan multi-recording careers cannot answer basic questions without opening stock KSP UIs:

- Which contracts are accepted right now vs projected to be accepted after pending recordings play?
- How many admin slots are free?
- Which KSC facilities are destroyed / at what level?
- Which strategies are active?
- Which milestones have been credited, and when?

The Kerbals window (#385) is intentionally roster-scoped, so adding these would dilute it. Timeline's resource footer answers a different question (per-recording-in-flight reservations). A dedicated *career-scoped* window is the right home.

**User decisions settled** (2026-04-17):

1. **Snapshot timing:** show both **current** (walked up to live UT) and **projected** (terminal ledger state). Mirrors how `DrawResourceBudget` shows "X available / Y committed / Z total" for Funds/Sci/Rep.
2. **Scope:** active lists only for Contracts / Strategies; current state for Facilities; **full history with UT** for Milestones. No "recent history" rollup in v1.
3. **Layout:** tabbed — `[ Contracts | Strategies | Facilities | Milestones ]`. This is a **new UI pattern for Parsek**; no other window uses `GUILayout.Toolbar`. Noted as a style-debt risk; mitigation in §6.
4. **Mode handling:** button always visible; sections auto-hide or show an explanatory message when the relevant game mode doesn't expose the data.

---

## 2. Terminology

- **Live UT** — `Planetarium.GetUniversalTime()` at window build time. The "now" the player is living in.
- **Terminal UT** — the UT of the last action in `Ledger.Actions`. The "end of everything committed", including actions from recordings still pending playback.
- **Current state** — module state as it would be after walking `Ledger.Actions` up to and including actions with `UT <= liveUT`.
- **Projected state** — module state at terminal UT. This is what `ContractsModule.GetActiveContractCount()` already returns, because the recalculation walk visits every action.
- **Divergence** — `current != projected`. Only happens when the timeline contains committed-but-not-yet-played recordings whose actions are in the future relative to live UT.

---

## 3. Mental model

The ledger is a fully-ordered action list. Modules are deterministic functions of a *prefix* of that list:

```
    actions:   A0  A1  A2  A3  A4  A5  A6  A7  A8
                │       │           │           │
                ▼       ▼           ▼           ▼
             walk[0]  walk[2]    walk[4]    walk[8]   <- terminal, == projected
                                    │
                                 (liveUT falls here)
                                    │
                                    ▼
                              walk up to here = CURRENT
```

The four modules each expose a terminal-state snapshot. To show *current* state we need a second walk that stops at `liveUT`. Two implementation strategies, picked in §4.3.

The window surfaces **four tabs**, one per module. Each tab renders its own current/projected pair. There is no cross-tab summary line.

---

## 4. Data model

### 4.1 View model shape

```csharp
internal readonly struct CareerStateViewModel
{
    public ContractsTabVM Contracts;
    public StrategiesTabVM Strategies;
    public FacilitiesTabVM Facilities;
    public MilestonesTabVM Milestones;
    public Game.Modes Mode;                  // KSP's enum: CAREER / SCIENCE_SANDBOX / SANDBOX (+ MISSION_BUILDER/MISSION treated as Sandbox-equivalent)
    public double LiveUT;                    // for "as of UT X" labels
    public double TerminalUT;                // last action UT, or LiveUT if empty
    public bool HasDivergence;               // any tab where current != projected
}

internal struct ContractsTabVM
{
    public int CurrentActive;
    public int ProjectedActive;
    public int CurrentMaxSlots;
    public int ProjectedMaxSlots;
    public int MissionControlLevel;          // drives contract slots (LedgerOrchestrator.UpdateSlotLimitsFromFacilities:1440-1446)
    public int ProjectedMissionControlLevel;
    public List<ContractRow> CurrentRows;    // active-at-liveUT
    public List<ContractRow> ProjectedRows;  // active-at-terminal (superset, or different)
}
internal struct ContractRow
{
    public string ContractId;                // opaque
    public string DisplayTitle;               // humanized; see §4.4
    public double AcceptUT;
    public double DeadlineUT;                // NaN if none
    public bool IsPendingAccept;             // AcceptUT > liveUT
}

internal struct StrategiesTabVM
{
    public int CurrentActive;
    public int ProjectedActive;
    public int CurrentMaxSlots;
    public int ProjectedMaxSlots;
    public int AdminLevel;                   // drives strategy slots
    public int ProjectedAdminLevel;
    public List<StrategyRow> CurrentRows;
    public List<StrategyRow> ProjectedRows;
}
internal struct StrategyRow
{
    public string StrategyId;
    public string DisplayTitle;
    public double ActivateUT;
    public StrategyResource SourceResource;
    public StrategyResource TargetResource;
    public float Commitment;                 // 0.01..0.25
    public bool IsPendingActivate;
}

internal struct FacilitiesTabVM
{
    // Facility "current" and "projected" states keyed by facilityId.
    // In v1 we show the stock KSP set (see §4.5); unseen facilities get default (L1, not destroyed).
    public List<FacilityRow> Rows;
}
internal struct FacilityRow
{
    public string FacilityId;
    public int CurrentLevel;
    public bool CurrentDestroyed;
    public int ProjectedLevel;
    public bool ProjectedDestroyed;
    public bool HasUpcomingChange;           // projected != current
}

internal struct MilestonesTabVM
{
    public int CurrentCreditedCount;
    public int ProjectedCreditedCount;
    public List<MilestoneRow> Rows;          // sorted by UT ascending
}
internal struct MilestoneRow
{
    public string MilestoneId;               // raw id (e.g. "FirstLaunch")
    public string DisplayTitle;
    public double CreditedUT;
    public float FundsAwarded;
    public float RepAwarded;
    public float ScienceAwarded;
    public bool IsPendingCredit;
}
```

**VM is pure data**, mirrors the Kerbals pattern (`KerbalsViewModel` struct, `List<T>` fields, no mutation after `Build()`). Cached as `CareerStateViewModel?` on the UI object; `InvalidateCache()` nulls the nullable.

### 4.2 Persisted UI state (non-VM)

- `int selectedTab` (0..3). **Transient**, resets when window closes. Matches Kerbals' `foldedKerbals` / `expandedSlots` locality — scene transitions reset UI-only state.
- `Vector2 scrollPos` — per tab? In v1 **shared** across tabs (simpler; matches Kerbals single scroll). Revisit if one tab's length overflow makes cross-tab navigation awkward.
- `Rect windowRect`, resize drag state — same pattern as `KerbalsWindowUI.DrawIfOpen`.

### 4.3 Computing "current" state — how

The four modules expose only terminal state. We need `(liveUT, Ledger.Actions)` → current snapshot. Options considered:

| Option | Pros | Cons |
|---|---|---|
| **A. Walk `Ledger.Actions` in `Build()`, filter by UT** | Zero module changes. Read-only UI stays read-only. Obvious fan-in point. | Duplicates a slice of module semantics (accept/complete/fail/cancel pairing, activate/deactivate pairing, facility level sequencing). |
| B. Add `SimulateAtUT(double ut)` to each module | Reuses module logic canonically. | Invasive: each module gains mutable "snapshot" mode; recalc engine gets complicated; scope creep. |
| C. Extend `RecalculationEngine` to publish incremental snapshots | Single authoritative source. | Huge refactor; way beyond scope. |

**Chosen: A.** `Build()` walks `Ledger.Actions` once, projecting per-tab state as it goes, and emits the terminal snapshot on the way out. Pairing logic is simple for each action type:

- **Contracts**: `ContractAccept` adds to current-actives; `ContractComplete/Fail/Cancel` removes. Only care about `Effective` actions (matches `ContractsModule.ProcessAction`).
- **Strategies**: `StrategyActivate` adds; `StrategyDeactivate` removes.
- **Facilities**: `FacilityUpgrade` sets `Level`; `FacilityDestruction` sets `Destroyed=true`; `FacilityRepair` sets `Destroyed=false`. Last-write-wins per facility.
- **Milestones**: track a `HashSet<string>` of already-credited ids (first `MilestoneAchievement` wins; `Effective=true` actions only).
- **Facility level → slots:** reuse `LedgerOrchestrator.GetContractSlots(level)` (`LedgerOrchestrator.cs:1457`, returns 2/7/999) and `GetStrategySlots(level)` (line 1471, returns 1/3/5). Both are `internal static`; no new helpers needed. Per `LedgerOrchestrator.UpdateSlotLimitsFromFacilities:1440-1446` **contracts slots derive from MissionControl level; strategies slots derive from Administration level** — the walk tracks both independently and feeds the live-UT-capped level into each helper for `CurrentMaxSlots` plus the terminal level for `ProjectedMaxSlots`.

Log each filtering decision at `Verbose` — see §7.

**What "current" means when the walk is ambiguous:** an action with `UT == liveUT` counts as already-applied (≤, not <). Matches how `DrawResourceBudget` treats the live balance as post-current-frame.

### 4.4 Display titles

Contract and strategy IDs are opaque KSP strings (e.g. `"ExploreBody"`, `"MoreScienceStrategy"`). KSP exposes humanized titles via `Contract.Title` and `Strategies.Strategy.Config.Title`, but only while the active `ContractSystem` / `Strategies.StrategySystem` instances hold them.

**v1 rule — preference chain:**

1. **`action.ContractTitle`** — `GameAction.ContractAccept` already carries the humanized title and round-trips through save/load (`GameAction.cs:230`). This is the primary source; the walk reads it directly.
2. **Live `ContractSystem.Instance.Contracts` lookup by id** — secondary fallback, used when `action.ContractTitle` is null/empty (stale pre-title actions, or mod-generated contracts that skipped the field).
3. **Raw id** — final fallback. Log at Verbose with the id so debugging is traceable.

Strategies follow the same pattern if/when a `StrategyTitle` field is added to `GameAction.StrategyActivate`. Today only a live `StrategySystem` lookup is available; raw id is the v1 fallback. Log the fallback at `Verbose`.

Milestone titles: the `MilestoneId` strings are already human-readable enum names (e.g. `"FirstLaunch"`, `"FirstOrbit"`). Insert spaces before capitals; no KSP lookup needed in v1.

Facility titles: stock KSP facility ids (`"LaunchPad"`, `"MissionControl"`, etc.) are english. Space-insert for display.

### 4.5 Facility scope

The stock KSP upgradeable set, in display order:

```
VehicleAssemblyBuilding, SpaceplaneHangar, LaunchPad, Runway,
Administration, MissionControl, TrackingStation, ResearchAndDevelopment,
AstronautComplex
```

v1 shows all nine unconditionally. Unseen facilities (not in `FacilitiesModule.GetAllFacilities()`) display as `L1` / not-destroyed (the module's own default). This gives the player a complete KSC inventory at a glance even on a fresh career.

---

## 5. Behavior

### 5.1 Button and dispatch

- New button in `ParsekUI.DrawWindow()` between `Kerbals` and `Real Spawn Control`. Text label `"Career State"` (no count suffix; the window itself surfaces counts per tab).
- Visible in KSC **and** Flight scenes (matches Kerbals). Hidden in the Main Menu and Editor (matches every other Parsek window).
- Toggle via `careerStateUI.IsOpen = !careerStateUI.IsOpen` + `ParsekLog.Verbose("UI", "Career State window toggled: open/closed")`.

### 5.2 Window chrome

Follow `KerbalsWindowUI` exactly:

- `DefaultWindowWidth = 420` (wider than Kerbals' 320 — tabs plus two-column rows need more room), `DefaultWindowHeight = 400`.
- `GUILayoutWindow` via `ClickThruBlocker`, input lock on hover, resize handle via `ParsekUI.HandleResizeDrag` / `DrawResizeHandle`.
- Close button at bottom, `GUI.DragWindow()` at the end.

### 5.3 Tab bar

Implementation: `GUILayout.Toolbar(selectedTab, new[] { "Contracts", "Strategies", "Facilities", "Milestones" })` just below the window title / mode banner. Parsek does not use `GUILayout.Toolbar` anywhere else; pick a minimal styling that visually matches the `sectionHeaderStyle` already in `KerbalsWindowUI` (see §6 for the style-debt mitigation).

Tab change:

- `selectedTab` updates immediately; scroll position resets to zero (simpler than remembering per tab).
- Log at `Verbose`: `"Career State: tab switched {old}→{new}"`.

### 5.4 Mode banner

Top line under the title, before the tab bar:

- **Career:** `"Career mode — UT {liveUT:F0}"` (plus `"(projection ahead: {terminal-live:F0})"` if `HasDivergence`).
- **Science:** `"Science mode — contracts and strategies unavailable"`.
- **Sandbox:** `"Sandbox mode — career state is not tracked"`. Tabs still render but show empty-section messages.

### 5.5 Per-tab rendering

**Contracts tab (Career only; empty message otherwise):**

```
Contracts

Mission Control L1 — slots 1/2 now, 2/2 projected

Active now (1)
  Explore Mun              accepted UT 104230     deadline UT 240000
Pending in timeline (1)
  Rescue Kerbal            accepted UT 118900     (deadline --)
```

If `CurrentRows == ProjectedRows`, collapse to a single `Active` section; otherwise split into `Active now` + `Pending in timeline`. Rows sorted by `AcceptUT` ascending. Pending rows use `timelineRedStyle`-equivalent muted color to mark them as "not yet lived".

**Strategies tab (Career only; Science shows empty message):**

Same layout as Contracts. Strategy rows additionally show `SourceResource → TargetResource @ Commitment%`.

**Facilities tab (Career + Science; hidden in Sandbox):**

```
Facilities

  VAB                   L2
  SPH                   L1
  LaunchPad             L2  →  L3 (upcoming)
  Runway                destroyed (repair pending in timeline)
  Administration        L1
  MissionControl        L1
  TrackingStation       L1
  ResearchAndDevelopment L2
  AstronautComplex      L1
```

Rows with `HasUpcomingChange` show both current and projected, with an arrow. Destroyed + repair-upcoming is a common case called out explicitly.

**Milestones tab (Career + Science; hidden in Sandbox):**

```
Milestones  (12 credited / 14 projected)

  UT 8230       FirstLaunch          +  10000 funds  + 5 rep  + 2 sci
  UT 44120     FirstOrbit           +  15000 funds  + 8 rep  + 3 sci
  UT 118900    FirstMunFlyby        (pending)
  ...
```

Sorted by `CreditedUT` ascending. Pending rows (projected-but-not-current) appear inline with a pending tag. Rewards formatted per the spec mockup; zero rewards elided.

### 5.6 Cache invalidation

- Subscribe `careerStateUI.InvalidateCache()` to `LedgerOrchestrator.OnTimelineDataChanged` in the existing `ParsekUI` handler (next to `timelineUI.InvalidateCache()` + `kerbalsUI.InvalidateCache()`).
- **Live UT changes continuously**, but `HasDivergence` only flips when an action boundary is crossed. v1 rebuilds the VM once per cache invalidation; live-UT changes within a stable ledger do NOT trigger rebuild. Side effect: the "as of UT X" banner lags until the next ledger change or window reopen. Acceptable for v1; called out as edge case E5.

### 5.7 Companion: Kerbals Fates → Timeline scroll

Separate from the main window but scoped to this ticket (spec §416 "Small companion item"):

- In `KerbalsWindowUI.DrawEndStatesSection` (line 448), replace `GUILayout.Label("  " + FormatEndStateRow(e), StyleForEndState(e.EndState))` with `GUILayout.Button("  " + FormatEndStateRow(e), StyleForEndState(e.EndState))`. The existing per-state styles (`deadStyle`, `recoveredStyle`, `aboardStyle`, `grayStyle` at lines 191-202) are already `GUI.skin.label` clones with only `textColor` overridden, so passing them as a Button style preserves the current visual and only adds click semantics.
- On click: call a pure `internal static void OnFatesRowClicked(Action<string> scrollCallback, string recordingId)` helper from the window, which invokes the callback and logs. This mirrors the existing pure-helper pattern (e.g. `KerbalsWindowUI.ToggleFold` line 459) so tests can pass a lambda spy without any interface.
- Production wiring: `OnFatesRowClicked(parentUI.GetTimelineUI().ScrollToRecording, e.RecordingId)`.
- Log template: `ParsekLog.Verbose("UI", $"Kerbals Fates → Timeline scroll: recordingId={e.RecordingId}")`.
- Matches the existing `Timeline.GoTo` → `RecordingsTableUI.ScrollToRecording` pattern at `TimelineWindowUI.cs:661`.
- **Ship in the same PR** as the career window — it's a small change and belongs thematically with this roadmap item.

---

## 6. What doesn't change

- `Ledger`, `RecalculationEngine`, all four career modules — **read-only** consumers only.
- Timeline window, its budget footer, `DrawResourceLine` — untouched. `DrawResourceLine` is `private static` on `TimelineWindowUI`; we do NOT promote it to `internal` or share across windows. The career window has its own local helpers with an analogous signature.
- Kerbals window — only the Fates detail row changes from `Label` to `Button`; all VM / sorting / fold logic stays identical.
- Save format — no new persisted fields. Tab selection and scroll are transient.
- ParsekScenario, GameStateRecorder, sidecars — no changes.

**Tabs style debt (Parsek-first use of `GUILayout.Toolbar`):** mitigation is to keep the styling minimal in v1 (rely on stock `GUI.skin.button` with a `selected`-on-click visual). If future windows adopt tabs, extract a shared helper then. We do not pre-extract in v1.

---

## 7. Diagnostic logging

Tag `"UI"` unless stated.

| Decision point | Level | Line |
|---|---|---|
| Window toggled open/closed | Verbose | `"Career State window toggled: open/closed"` |
| Cache invalidated | Verbose | `"CareerStateWindow: cache invalidated"` |
| VM rebuilt | Verbose | `"CareerStateWindow: rebuilt VM liveUT={liveUT} terminalUT={termUT} divergence={bool} mode={mode} contracts={curC}/{projC} strategies={curS}/{projS} facilities={fRows} milestones={curM}/{projM}"` (Verbose: fires on every ledger invalidation; would spam Info on busy timelines) |
| Walk filter: action skipped | Verbose | `"CareerStateWindow: action skipped actionType={type} ut={ut} reason={Ineffective|UT>liveUT|Unknown}"` (rate-limited via `ParsekLog.VerboseRateLimited` with key `"CareerStateWindow.skip.{actionType}.{reason}"` so repeat walks don't spam) |
| Contract title fallback | Verbose | `"CareerStateWindow: contract title fallback id={contractId}"` (rate-limited per id via `VerboseRateLimited`) |
| Strategy title fallback | Verbose | `"CareerStateWindow: strategy title fallback id={strategyId}"` (rate-limited per id; live lookup uses `Strategies.StrategySystem.Instance.Strategies`) |
| Tab switched | Verbose | `"CareerStateWindow: tab switched {old}→{new}"` |
| Sandbox-mode render | Verbose | `"CareerStateWindow: rendered sandbox-empty state"` |
| Science-mode render | Verbose | `"CareerStateWindow: rendered science-mode (contracts/strategies hidden)"` |
| Fates → Timeline scroll | Verbose | `"Kerbals Fates → Timeline scroll: recordingId={id}"` (in `KerbalsWindowUI`) |

**Silent-branch guards:** every `if` in the walk-filter has a log line on both branches, matching project convention from the `Group W` button rotation logic.

---

## 8. Test plan

All tests unit-level, no KSP runtime required. Mirror `KerbalsWindowUITests` structure.

### 8.1 `CareerStateWindowUITests.cs` — Build() coverage

One test per behavior, each with a stated failure mode:

- **`Build_NoActions_EmptyVM`** — empty ledger yields empty tab rows. Fails if the walk throws on empty input.
- **`Build_Contracts_CurrentEqualsProjected_NoDivergence`** — all contract actions have `UT < liveUT`. Expect `HasDivergence=false`, single-section render. Fails if the walk double-counts terminal state.
- **`Build_Contracts_PendingAccept_AppearsInProjectedOnly`** — `ContractAccept` with `UT > liveUT`. Expect `CurrentRows` missing it, `ProjectedRows` containing it with `IsPendingAccept=true`. Fails if the UT filter is off-by-one or if `IsPendingAccept` is mis-set.
- **`Build_Contracts_CompletedAfterLiveUT_StaysActiveInCurrent`** — accept at UT 100 (past), complete at UT 300 (future), liveUT=200. Current should show 1 active; projected 0. Fails if the walk drops pending-complete from the current snapshot.
- **`Build_Contracts_IneffectiveAcceptSkipped`** — `ContractAccept.Effective=false`. Expect both current and projected to skip it. Fails if `Effective` isn't honored.
- **`Build_Strategies_ActivateDeactivateRoundTrip`** — activate at UT 100, deactivate at UT 200, liveUT=150. Current shows 1 active; projected 0. Fails if the pairing logic is inverted.
- **`Build_Facilities_UpgradeSequence`** — L1→L2 at UT 100, L2→L3 at UT 200, liveUT=150. Current L=2, projected L=3. Fails if last-write semantics break.
- **`Build_Facilities_DestructionThenRepair`** — destroy at UT 100, repair at UT 200, liveUT=150. Current destroyed, projected not. Fails if repair doesn't clear `Destroyed`.
- **`Build_Facilities_UnseenFacilityDefaults`** — no facility actions for `AstronautComplex`. Expect a row at `L1`/not-destroyed. Fails if missing ids are dropped.
- **`Build_Milestones_PendingCreditShowsInProjectedOnly`** — achievement at UT 300, liveUT=200. Expect `CurrentCreditedCount=0`, `ProjectedCreditedCount=1`, row present with `IsPendingCredit=true`.
- **`Build_Milestones_IneffectiveDuplicateSkipped`** — two `MilestoneAchievement` with same id, second `Effective=false`. Rows contain one entry.
- **`Build_FacilityLevelsEchoed_ContractsUseMissionControl_StrategiesUseAdministration`** — MissionControl upgrade to L2 at UT 50, Administration upgrade to L2 at UT 100, liveUT=150. Contract tab `MissionControlLevel=2`, `CurrentMaxSlots=7`; Strategy tab `AdminLevel=2`, `CurrentMaxSlots=3`. Fails if a tab is wired to the wrong facility (an earlier Phase-1 revision had this crossed).
- **`Build_LiveUTEqualsActionUT_CountsAsApplied`** — explicit `<=` boundary test.

### 8.2 Formatting tests

- `FormatContractRow`, `FormatStrategyRow`, `FormatFacilityRow`, `FormatMilestoneRow` — pure string helpers. One test per: with-deadline, without-deadline, pending-tag, muted-when-pending.
- `SpaceBeforeCapitals` helper for humanization — `"FirstMunFlyby"` → `"First Mun Flyby"`. Fails if runs of capitals collapse (e.g. `"VAB"` should NOT become `"V A B"`).

### 8.3 Log-assertion tests

Capture `ParsekLog` via the existing test sink (see `KerbalsWindowUITests` for the setup pattern):

- **`Build_Logs_VMRebuildOncePerCall`** — a single Info log `"rebuilt VM"` per `Build()`. Fails if the walk accidentally rebuilds twice.
- **`Build_Logs_DivergenceFlaggedWhenPendingExists`** — `divergence=true` appears in the rebuild log. Guards against the flag being computed but never logged.
- **`Build_Logs_ContractTitleFallback`** — a synthetic contract id with no live `ContractSystem` instance; expect one fallback log. Verifies the fallback branch isn't silent.

### 8.4 Cache test

- **`InvalidateCache_NullsCachedVM`** — after `InvalidateCache()`, next `DrawIfOpen` call triggers a fresh `Build()`. Mirrors `KerbalsWindowUITests.InvalidateCache_DoesNotClearFoldedKerbals`.

### 8.5 Edge-case tests (addressing review gap)

- **`Build_Mode_Sandbox_AllTabsEmptyWithBanner`** (E1) — pass `Game.Modes.SANDBOX`; all four tab VMs render empty rows. Fails if the walk still populates from actions in Sandbox.
- **`Build_Mode_Science_HidesContractsAndStrategies`** (E2) — pass `Game.Modes.SCIENCE_SANDBOX`; Contracts and Strategies tabs empty; Facilities and Milestones populated normally. Fails if science-mode gating is missed.
- **`Build_Facilities_EmptyLedger_AllNineAtLevel1`** (E3 strengthened) — empty `Ledger.Actions`, all nine stock facilities in `FACILITY_DISPLAY_ORDER` appear at L1 not-destroyed. Fails if unseen facilities are dropped.
- **`Build_MissionControlLevel_MultipleFutureUpgrades_CurrentEchoesLiveUTLevel`** (E6) — two `FacilityUpgrade` actions for MissionControl at UT 100 (→L2) and UT 200 (→L3), liveUT=150. Current `MissionControlLevel=2, CurrentMaxSlots=GetContractSlots(2)=7`; projected `ProjectedMissionControlLevel=3, ProjectedMaxSlots=999`. Fails if projections leak into current.
- **`Build_Facilities_DestroyAndRepairBothInFuture`** (E7 refinement) — destroy at UT 100, repair at UT 200, liveUT=50. Current not-destroyed, projected not-destroyed, `HasUpcomingChange=false`. Fails if ordering matters.
- **`Build_NullModules_ReturnsEmptyVMWithWarn`** (E12) — pass `null` for one or more modules; `Build` returns an empty VM and logs a Warn. Fails silently if the walk NREs on cold-start.
- **`Build_ContractTitleLookup_Throws_FallsBackToId`** (E13) — action with null `ContractTitle`; `ContractSystem.Instance` throws via the test harness. The walk catches and falls back to raw id with a Verbose log. Fails if the reflection path is unguarded.

### 8.6 Companion-item test (in `KerbalsWindowUITests`)

- **`OnFatesRowClicked_InvokesCallbackWithRecordingId`** — pure-helper test: call `KerbalsWindowUI.OnFatesRowClicked((id) => captured = id, "rec-42")`; assert `captured == "rec-42"`. Fails if the hookup is wired to the wrong id (e.g. group id vs recording id).
- **`OnFatesRowClicked_LogsRecordingId`** — log-assertion test; guards against silent-click regression.
- **`OnFatesRowClicked_MissingRecording_ScrollCallback_NoOpOk`** (E14) — callback receives an unknown id and returns; helper logs the click; no exception. Matches `TimelineWindowUI.ScrollToRecording`'s no-op behavior for stale ids.

No integration test runs the full KSP scene. Manual verification is in §10.

---

## 9. Edge cases

| # | Scenario | v1 behavior | Rationale |
|---|---|---|---|
| E1 | Sandbox mode | Tabs render with "not tracked" messages; button visible. | User chose "always visible, sections auto-hide". |
| E2 | Science mode | Contracts + Strategies tabs show "unavailable in Science mode"; Facilities + Milestones render normally. | Matches how KSP itself exposes these subsystems. |
| E3 | Career with empty ledger (fresh game) | All tabs render empty with "no history yet" hints; Facilities shows all nine at L1. | Full KSC inventory even on day 1 is useful. |
| E4 | `Ledger.Actions` lookup races with recalculation | `Build()` is called from the UI thread on the same frame as the `OnTimelineDataChanged` invalidation; no concurrent writer. | KSP's IMGUI model is single-threaded. |
| E5 | Live UT advances past a pending action between rebuilds | The banner UT and "pending" tags become stale until next rebuild. | **Acceptable v1 limitation.** Documented. Revisit if playtesting surfaces confusion. |
| E6 | More than one admin upgrade in future | Projected uses last-write; current tab's `MaxSlots` still reflects the level at liveUT. | Matches how the modules themselves compute slot limits. |
| E7 | Facility destroyed in future but repaired before terminal | Current not-destroyed (hasn't happened yet); projected not-destroyed (repair wins). Row shows neither tag. | Walk is deterministic. |
| E8 | Milestone with zero rewards | Row renders without the reward suffix. | Zero-additive suffix is visual noise. |
| E9 | Contract with NaN deadline | Show `"deadline --"`. | Stock-KSP convention for no-deadline contracts. |
| E10 | Window open during a scene transition (Flight→KSC) | Window toggle state persists on `ParsekUI`; `ClickThruBlocker` handles input correctly. | Matches Kerbals + Timeline behavior. |
| E11 | User has >50 credited milestones | Single scrollable tab, no virtualization. Acceptable unless playtesting shows >500. | IMGUI handles up to a few hundred rows fine. |
| E12 | `LedgerOrchestrator` modules aren't initialized (cold-start race) | `Build()` returns an empty VM with a banner `"Career state unavailable — ledger not ready"`. | Prevents NREs during save-load. Log at `Warn`. |
| E13 | Contract title lookup throws (KSP bug or mod interference) | Caught around the `ContractSystem` reflection; fall back to raw id. | Defensive — we can't trust third-party contract packs. |
| E14 | User clicks a Kerbals-Fates row whose recording no longer exists in Timeline | `TimelineWindowUI.ScrollToRecording` logs the request at Verbose; the draw-side no-match path at `TimelineWindowUI.cs:495-517` silently leaves `scrollTargetRow = -1` and does nothing. v1: add a Verbose "scroll target not found: id={id}" log in the no-match branch of the draw-side so the trail isn't silent. | Tightens an existing silent branch; one-line change next to the existing scroll logic. |
| E15 | Window closed while mode changes (Career → Sandbox via mod) | Cache invalidated on next `OnTimelineDataChanged`; mode banner updates on next open. | Good enough. |

---

## 10. Manual testing (gated on v1 ship)

1. Open a career save with accepted contracts; verify Contracts tab matches stock Mission Control.
2. Commit a recording that contains a future `ContractAccept`; verify the row shows only in `Pending in timeline`.
3. Play back the recording; verify on the next ledger invalidation the row moves to `Active now`.
4. Destroy a facility via stock KSP, verify Facilities tab; repair it, verify again.
5. Activate a strategy; confirm Strategies tab + admin-level slot arithmetic.
6. Earn a milestone; confirm it appears with the correct UT and rewards.
7. Switch to a Sandbox save, confirm the button is still visible and tabs show mode-empty messages.
8. Click a Kerbals-Fates row; confirm Timeline scrolls to the matching recording.

---

## 11. Out of scope for v1 (explicit)

- **Per-contract reward breakdown** (funds/sci/rep per contract row) — the spec left this open; defer to v2 unless playtesting asks for it. Rows only show the contract title + dates.
- **Milestone filter by category** — spec left open; v1 shows the flat chronological list. If milestone counts grow painful, revisit.
- **Cross-save diffing** — nice-to-have, way out of scope.
- **Tab state persisted across sessions** — transient in v1; upgrade only if playtesting shows repeated tab-hunt.
- **Refreshing the banner UT on a ticker** — acceptable staleness (E5) until next ledger change.
- **Per-tab scroll position** — shared scroll in v1.
- **Tooltips on contract/strategy rows** — no hover UI in v1; row text carries everything.

---

## 12. Files to touch (final inventory)

**New:**
- `Source/Parsek/UI/CareerStateWindowUI.cs` — window class + `Build()` + draw methods.
- `Source/Parsek.Tests/CareerStateWindowUITests.cs` — unit coverage per §8.

**Modified:**
- `Source/Parsek/ParsekUI.cs` — `careerStateUI` field, button render, `OnTimelineDataChanged` handler, `DrawCareerStateWindowIfOpen` call site, `GetCareerStateUI` (if needed by any cross-window caller).
- `Source/Parsek/ParsekFlight.cs` — call `ui.DrawCareerStateWindowIfOpen(windowRect)` next to the existing Kerbals call.
- `Source/Parsek/ParsekKSC.cs` — same dispatch addition.
- `Source/Parsek/UI/KerbalsWindowUI.cs` — convert Fates detail rows from `Label` to `Button` + extract `OnFatesRowClicked` static helper (companion item).
- `Source/Parsek/UI/TimelineWindowUI.cs` — one-line Verbose log in the `ScrollToRecording` no-match branch (see E14). **Smallest possible delta** — only the silent-branch fix, no behavior change.
- `Source/Parsek.Tests/KerbalsWindowUITests.cs` — add companion-item tests (§8.6).
- `docs/dev/todo-and-known-bugs.md` — mark §416 as DONE (or in-progress) after PR merge.
- `CHANGELOG.md` — `Unreleased` entry: "New Career State window surfaces contracts, strategies, facility levels, and credited milestones with current-vs-projected columns. Kerbals Fates rows now scroll the Timeline."

**Not touched** (to reiterate): `ContractsModule`, `FacilitiesModule`, `StrategiesModule`, `MilestonesModule`, `LedgerOrchestrator`, `RecalculationEngine`, save format, sidecars.

**Spec §416 files-to-touch deviation (noted for PR reviewer):** the original spec in `docs/dev/todo-and-known-bugs.md:1275-76` proposed adding `FacilitiesModule.GetFacilityStates()` and a milestone UT-of-credit helper. This design supersedes that — `FacilitiesModule.GetAllFacilities()` already exists (line 229), and milestone UTs are read directly from `Ledger.Actions` via the walk in `Build()`. Zero new module surface.

---

## 13. Open questions for the user

None blocking implementation. These can be addressed in the plan review or after v1 ships:

1. **Tab label wording.** `"Career State"` window title, tabs `Contracts | Strategies | Facilities | Milestones` — good as-is?
2. **Facilities tab order.** Currently grouped "launch infrastructure first, admin middle, science last" (see §4.5). Alternative: alphabetical. Happy to change.
3. **Milestone humanization threshold.** Heuristic: insert space before a capital unless the previous char is also uppercase. Handles `FirstMunFlyby` → `First Mun Flyby` and preserves `VAB`. Want something fancier?
4. **Tab-bar visual.** `GUILayout.Toolbar` is rendered by stock `GUI.skin.button` by default. If the look is jarring next to the rest of Parsek, we style it in v2.

---

## 14. Next step

Assuming this design passes review, the implementation cycle per `docs/dev/development-workflow.md` Step 4 is:

- **4a (Plan):** split into three phases — (1) VM + walk + tests; (2) window chrome + tab render; (3) Kerbals-Fates companion + changelog.
- **4c (Implement):** phases run sequentially on this branch (no isolation worktrees — single-feature, tightly coupled).
- **4d (Review):** clean-context agent reviews against this doc before merge.

Ready to plan on approval.
