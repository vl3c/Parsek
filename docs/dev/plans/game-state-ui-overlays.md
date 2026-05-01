# Game-State UI Overlays — Design Pass

**Branch:** `feat/game-state-ui-overlays` (worktree at `C:\Users\vlad3\Documents\Code\Parsek\Parsek-game-state-ui-overlays`)
**Status:** design pass, pre-implementation. No code written yet.

The Parsek ledger and `KspStatePatcher` already make stock KSP singletons (Funding, R&D, Reputation, ProgressTracking, ContractSystem, AssetBase.RnDTechTree) reflect the committed timeline. What is still missing: the stock building UIs (R&D, Astronaut Complex, Mission Control) do not visually distinguish state that is "already lived" from state that the player has committed in the future. The player can still click a tech node that a pending recording will unlock, gets a blocking dialog, and has to mentally cross-reference the Parsek Career window. The same gap exists for kerbal hires and contract acceptance.

This doc covers a focused set of overlays + click-blocks that make the native UIs first-class citizens of the Parsek timeline. It explicitly does not introduce new Parsek windows; the goal is to make the buildings the player already opens carry the same truth that Parsek's internal state carries.

---

## 1. Problem

The investigation in this branch's preceding session (see commit history and the agent reports cached in PR drafts) confirmed three things:

1. **Resource top bar is correct already.** `Funding.Instance.AddFunds`, `ResearchAndDevelopment.Instance.AddScience`, and `Reputation.Instance.SetReputation` fire `GameEvents.OnFundsChanged / OnScienceChanged / OnReputationChanged` internally; `CurrencyWidgetsApp` listens. After every `LedgerOrchestrator.RecalculateAndPatch()` the bar is in sync. This is verified by reading; an in-game test would seal it.
2. **Tech + facility click-blocks exist** (`TechResearchPatch`, `FacilityUpgradePatch`) and pop `CommittedActionDialog.ShowBlocked` with the committed UT. They depend on `MilestoneStore.GetCommittedTechIds()` / `GetCommittedFacilityUpgrades()` / `FindCommittedEvent(type, key)`.
3. **Crew filter exists** but only for the VAB/SPH crew assignment dialog (`CrewDialogFilterPatch`). The Astronaut Complex itself is untouched. There are zero Harmony patches on `Contracts.Contract.Accept`, on `KerbalRoster.AddCrewMember` from the hire flow, on `RDController`, on `MissionController`, or on any `onGUI*ComplexSpawn` event.

What the player sees today, by stock screen:

| Stock screen | Past commit | Future commit | Locked / available |
|---|---|---|---|
| Tech tree (R&D) | Available (correct) | **Looks identical to a fresh node** until clicked | Locked (correct) |
| Facilities (KSC view) | Upgraded (correct) | **Looks unupgraded** until clicked | Unupgraded (correct) |
| Mission Control | Completed in history (correct) | **Looks freshly offered**; nothing prevents double-accept | Offered (correct) |
| Astronaut Complex | Hired in history (correct) | **Looks like a normal applicant**; nothing prevents double-hire | Applicant (correct) |
| VAB/SPH crew dialog | Hidden (correct) | Hidden (correct) | Visible (correct) |
| Top resources bar | Patched (correct) | n/a | n/a |

The four cells marked in bold are the v1 scope.

User-facing complaint: "I have to keep checking the Parsek Career window before I do anything in the buildings, otherwise I waste a click and read a popup."

---

## 2. Terminology

- **Committed-future action** — an action stored in `MilestoneStore` whose source milestone is committed but whose `LastReplayedEventIndex` has not advanced past it yet. Equivalently: an entry returned by `MilestoneStore.GetCommittedTechIds()` / `GetCommittedFacilityUpgrades()`, or — once the new helpers below ship — by `MilestoneStore.GetCommittedContractAcceptIds()` / `…KerbalHireNames()`.
- **Reserved kerbal** — a `ProtoCrewMember` that `KerbalsModule.ShouldFilterFromCrewDialog(name)` returns true for. This already covers reserved (slot owner stand-ins) and retired kerbals; for the Astronaut Complex overlay we want the same predicate plus a separate "will be hired at UT" predicate sourced from a new Kerbals helper.
- **Decorate vs hide** — for VAB/SPH crew dialogs we *hide* reserved kerbals (existing behavior); for Astronaut Complex we *decorate* them. The player wants to see the roster they actually have; an invisible kerbal in their own roster is confusing.
- **Click-block** — the existing pattern from `TechResearchPatch`: a Harmony prefix returns `false` and shows `CommittedActionDialog.ShowBlocked` with the committed UT. We extend the pattern to two more action types in this PR.
- **Live UT cutoff** — `Planetarium.GetUniversalTime()` at the moment the screen renders. Committed-future overlays are decorated when `event.ut > liveUT`.

---

## 3. Mental model

The data is already present in two stores. The patcher writes the *terminal* state to KSP singletons. The patches block the *future* slice. The overlays paint that same future slice on top of stock UI.

```
                         past (UT < liveUT)        future (UT > liveUT)
  Ledger / MilestoneStore  ─────────────┬─────────────────────┐
                                        │                     │
                                        ▼                     ▼
     KspStatePatcher ───────────►  KSP singletons        (no patcher writes;
       (writes terminal             (R&D, Funds,          terminal includes them
        state, fires                 Reputation,          but the live UT cursor
        GameEvents)                  ProgressTracking,    has not advanced past)
                                     ContractSystem)
                                                              │
                                                              │
   ┌─────────────────────── this PR adds: ─────────────────────┤
   │                                                          │
   │  TechResearchPatch ◄─ existing click-block ──┐           │
   │  FacilityUpgradePatch ◄─ existing click-block┤           │
   │  ContractAcceptPatch ◄─ NEW click-block ─────┤           │
   │  KerbalHirePatch ◄─ NEW click-block ─────────┤           │
   │                                              │           │
   │  StockUiOverlayController ◄── reads ─────────┘   ───────►│
   │    - decorates R&D nodes                                 │
   │    - decorates Astronaut Complex rows                    │
   │    - decorates Mission Control rows                      │
   └──────────────────────────────────────────────────────────┘
```

The overlay is a thin presentation layer; it never mutates ledger state or KSP singletons. Click-blocks are the only mutators added by this PR, and they only refuse player actions — they do not invent new ones.

---

## 4. Data model

### 4.1 New `MilestoneStore` query helpers

Three new helpers, mirroring the existing `GetCommittedTechIds()` shape (one pass over `milestones`, filter on `LastReplayedEventIndex + 1`, gated on `IsEventVisibleToCurrentTimeline`):

```csharp
// MilestoneStore additions
internal static HashSet<Guid> GetCommittedContractAcceptIds()
{
    // GameStateEventType.ContractAccepted, key = Contract.ContractGuid.ToString()
    // returns Guids parsed from the key (Guid.TryParse, skip on parse failure with one Verbose log)
}

internal static HashSet<string> GetCommittedKerbalHireNames()
{
    // GameStateEventType.CrewHired, key = crew.name (already emitted by
    // GameStateRecorder.cs:779 and converted by GameStateEventConverter.cs:172-173)
}

internal static HashSet<string> GetCommittedKerbalRetireNames()
{
    // GameStateEventType.CrewRemoved, key = crew.name. Source for the FutureRetired
    // overlay variant in §5.2.
}

// Optional v2 (NOT in v1): future-completion / future-decline / future-cancel queries
// for contracts, used by the overlay to show a finer state. v1 only flags "future-accepted".
```

Each helper logs at `Verbose` when it returns a non-empty set, matching the existing pattern in `GetCommittedTechIds()`.

**Verified before plan publication:** the event types already exist — `GameStateEventType.CrewHired` (value 8), `CrewRemoved` (9), and `CrewStatusChanged` (10) — and are emitted with `key = crew.name` from `GameStateRecorder.cs:779` and converted by `GameActions/GameStateEventConverter.cs:172`. The new helpers are pure read-only queries over `MilestoneStore`; no enum or converter changes are needed.

### 4.2 Overlay view model

One small struct per stock screen. Built on demand inside `StockUiOverlayController`; no caching across `onGUI*Despawn`.

```csharp
internal readonly struct TechNodeOverlayMark
{
    public string TechId;
    public double CommittedUt;
    public string CommittedRecordingId;       // empty when source is a flush milestone
}

internal readonly struct ApplicantOverlayMark
{
    public string KerbalName;
    public ApplicantOverlayKind Kind;          // Reserved | FutureHired | FutureRetired
    public double CommittedUt;                 // NaN for "Reserved" (active stand-in)
}
internal enum ApplicantOverlayKind { Reserved, FutureHired, FutureRetired }

internal readonly struct ContractOverlayMark
{
    public Guid ContractGuid;
    public ContractOverlayKind Kind;           // FutureAccepted | FutureCompleted | FutureFailed
    public double CommittedUt;
}
internal enum ContractOverlayKind { FutureAccepted, FutureCompleted, FutureFailed }
```

`CommittedRecordingId` is included so the overlay can show "from recording '<name>'" in the tooltip when known. Uses `RecordingStore.GetById(rid)?.DisplayName` with a raw-id fallback.

### 4.3 Persisted state

**None.** All overlay state is transient. The buildings are short-lived screens; rebuilding the marks on `onGUI*Spawn` is cheap and avoids cache-invalidation bugs.

### 4.4 What we explicitly do NOT add

- No new Parsek window. The Career window already exists for the cross-screen view.
- No Parsek toolbar entry for an "overlay on/off" toggle in v1. If players ask, add a setting to `ParsekSettings`; not before.
- No persisted user dismissal of individual overlays. They are recomputed every frame from ledger state.

---

## 5. Behavior

### 5.1 Tech tree overlay (R&D building)

**Hook:** subscribe to `GameEvents.onGUIRnDComplexSpawn` and `…Despawn` from a new `KSPAddon(GameScenes.SPACECENTER, false)` MonoBehaviour `StockUiOverlayController`.

**On spawn:**

1. Build a `Dictionary<string, TechNodeOverlayMark>` from `MilestoneStore.GetCommittedTechIds()`. For each id, call `MilestoneStore.FindCommittedEvent(GameStateEventType.TechResearched, id)` to get the UT and recording id.
2. Wait one frame for `RDController.Instance` to populate (it instantiates nodes on its own `Start`). If `RDController.Instance == null` after one frame, abort with one `Warn` and try again on the next `onGUIRnDComplexSpawn`.
3. Walk `RDController.Instance.nodes` (a `List<RDNode>` field on the controller). For each `RDNode` whose `tech.techID` is in our marks dict, attach a child `GameObject` named `Parsek_TechOverlay` under the node's transform. Attach a `MonoBehaviour` `TechOverlayBadge` that draws a small clock-icon + tooltip ("Committed at UT 12345 — recording 'Mun Lander v3'"). The badge listens for pointer enter/exit to flip the tooltip.

**On despawn:** destroy every child `GameObject` named `Parsek_TechOverlay` under the R&D root, no leaks across re-opens.

**Click-block:** unchanged. `TechResearchPatch` already blocks. Overlay just makes the block predictable.

**Refresh trigger:** if `Ledger.StateVersion` changes while the screen is open (rare but possible if a recording is committed via the Parsek main window in another scene before R&D closes), rebuild the marks dict on the next OnGUI frame. We poll `Ledger.StateVersion` once per frame inside the controller's `Update()`.

### 5.2 Astronaut Complex overlay

**Hook:** `GameEvents.onGUIAstronautComplexSpawn` / `…Despawn`.

**On spawn:**

1. Build an `applicantMarks` dict by walking `HighLogic.CurrentGame.CrewRoster.Crew` and `…Applicants` (and `…Tourist` if the player ever sees them in this list).
2. For each name, classify:
   - **Reserved** if `KerbalsModule.ShouldFilterFromCrewDialog(name)` is true AND no future hire event exists. Tooltip: "Reserved by Parsek for slot 'Bob Kerman'".
   - **FutureHired** if `MilestoneStore.GetCommittedKerbalHireNames()` contains the name (sourced from `GameStateEventType.CrewHired`). Tooltip: "Will be hired at UT 12345 — recording '...'".
   - **FutureRetired** if `MilestoneStore.GetCommittedKerbalRetireNames()` contains the name (sourced from `GameStateEventType.CrewRemoved`). Same shape as FutureHired but the badge color is muted.
3. Find the panel's `UIList` that drives the applicants column. The Astronaut Complex UI is `KSP.UI.Screens.AstronautComplex`; its applicants are rendered into a `KerbalListItem` per row inside a `UIList` accessible via `crewListController` (or whatever the field is named — to be confirmed during implementation; look in `BaseCrewAssignmentDialog.AddAvailItem` for the parallel call).
4. For each `KerbalListItem` whose `pcm.name` matches an entry in `applicantMarks`, attach a child `Parsek_KerbalOverlay` GameObject with a small badge. Color/icon varies by `ApplicantOverlayKind`.

**On despawn:** strip every `Parsek_KerbalOverlay` child under the Astronaut Complex root.

**Click-block** (NEW patch `KerbalHirePatch`): Harmony prefix on the hire-button callback. The KSP API surface is `KSP.UI.Screens.AstronautComplex.HireApplicant(ProtoCrewMember pcm)` (or the equivalent — confirm during implementation by reading the decompiled `Assembly-CSharp.dll`). If `pcm.name` is in `MilestoneStore.GetCommittedKerbalHireNames()`, return `false` and show `CommittedActionDialog.ShowBlocked("Cannot hire \"" + pcm.name + "\"", "This kerbal is already committed on your timeline at UT …", "")`. Mirrors `TechResearchPatch`.

**No filter on Astronaut Complex** — unlike VAB/SPH, the player keeps seeing the kerbal. The overlay tells the story; the click-block enforces the rule.

### 5.3 Mission Control overlay

**Hook:** `GameEvents.onGUIMissionControlSpawn` / `…Despawn`.

**On spawn:**

1. Build a `Dictionary<Guid, ContractOverlayMark>` from `MilestoneStore.GetCommittedContractAcceptIds()`. For each Guid, call `FindCommittedEvent(GameStateEventType.ContractAccepted, guid.ToString("N"))` (or whichever key shape `GameStateEventConverter` uses; verify during implementation) for UT + recording id.
2. Walk `ContractSystem.Instance.Contracts`; for each contract whose `ContractGuid` is in our marks dict, attach a `Parsek_ContractOverlay` GameObject under the row that the Mission Control window renders for that contract.
3. The overlay text reads "Will be accepted at UT 12345 — recording '...'". If the contract is also already in the player's Active list (rare race: it was accepted live then re-emerged in a future commit), suppress the badge with one Verbose log.

**On despawn:** strip every `Parsek_ContractOverlay`.

**Click-block** (NEW patch `ContractAcceptPatch`): Harmony prefix on `Contracts.Contract.Accept()`. If `__instance.ContractGuid` is in `GetCommittedContractAcceptIds()`, return `false` and `CommittedActionDialog.ShowBlocked`.

**Why both overlay AND click-block:** they answer different questions for the player. The overlay says "this is happening on your timeline already, don't waste your offered-slot quota"; the click-block enforces it if they click anyway.

### 5.4 Top resources bar — verification only

No production code changes. We add an in-game test that:

1. Captures `Funding.Instance.Funds`, `ResearchAndDevelopment.Instance.Science`, `Reputation.Instance.reputation` before and after a synthetic `LedgerOrchestrator.RecalculateAndPatch()`.
2. Asserts the values match `FundsModule.GetAvailableFunds()` / `ScienceModule.GetAvailableScience()` / `ReputationModule.GetRunningRep()`.
3. Asserts `CurrencyWidgetsApp.Instance` exists and that its widgets reflect the new values via reflection (the widgets are private fields on the app; we read them rather than the public API to verify the screen, not the singleton).

Test lives in `InGameTests/RuntimeTests.cs` (Category = "ResourceTopBar", Scene = `GameScenes.SPACECENTER`).

### 5.5 Cross-screen: shared overlay infrastructure

A single `StockUiOverlayController` singleton (a `KSPAddon(GameScenes.SPACECENTER, false)` plus a sibling `KSPAddon(GameScenes.FLIGHT, false)` if the player can open R&D from in-flight via mods — for v1 SPACECENTER only) owns:

- Subscriptions to all six `onGUI*Spawn` / `…Despawn` events.
- A small `OverlayBadge` MonoBehaviour shared by all three screens, parameterized by icon + tooltip text.
- A `RebuildAllVisible()` method called on `Ledger.StateVersion` change.

The badge prefab is built in code (no asset bundle). It uses `Texture2D` from `GameDatabase.Instance.GetTexture("Parsek/Textures/clock_overlay", false)` with a code-only fallback (a `Texture2D.whiteTexture` tinted via `GUI.color`) so the feature still works if the texture is missing during early dev.

### 5.6 Settings

Two new toggles in `SettingsWindowUI.cs`, default ON:

- `Show committed-future overlays in stock UI` (master toggle for the three overlays).
- `Block player actions that conflict with committed timeline` (master toggle for the four click-blocks; existing `TechResearchPatch` and `FacilityUpgradePatch` honor it too — small refactor to gate them on the same flag).

Persisted via `ParsekSettings` exactly like the existing toggles. Both toggles take effect on the next `onGUI*Spawn`.

---

## 6. What doesn't change

- `Ledger`, `RecalculationEngine`, all eight resource modules — read-only consumers only. Even the new `MilestoneStore` helpers are pure.
- `KspStatePatcher` — unchanged.
- VAB/SPH `CrewDialogFilterPatch` — unchanged. Hiding stays the right call there.
- The Parsek Career window — unchanged. The overlays complement it; they do not replace it.
- Save format — no new persisted fields. Everything is recomputed per-OnGUI from existing state.
- ParsekScenario, GameStateRecorder, sidecars — no changes.
- The `MilestoneStore` storage shape — only new query helpers; no new `Milestone` fields.

**No serialized-enum changes.** Verified `GameStateEventType.CrewHired` and `CrewRemoved` already exist and are wired through the recorder and converter. No save-format risk in this PR.

---

## 7. Diagnostic logging

Subsystem tag `"StockUiOverlay"` for the controller, `"ContractAcceptPatch"` and `"KerbalHirePatch"` for the new patches.

| Decision point | Level | Line |
|---|---|---|
| Controller initialised | Info | `"StockUiOverlay: initialised, listening for R&D / Astronaut / MissionControl spawns"` |
| R&D spawn handler entered | Verbose | `"StockUiOverlay: R&D spawn — building tech marks ledger version={Ledger.StateVersion}"` |
| R&D mark count | Info | `"StockUiOverlay: R&D decorated nodeCount={n} of total={t}"` |
| R&D `RDController.Instance` null after one frame | Warn | `"StockUiOverlay: R&D spawn — RDController.Instance still null after one frame; will retry on next spawn"` |
| Astronaut spawn handler entered | Verbose | `"StockUiOverlay: Astronaut spawn — building applicant marks reserved={r} futureHired={fh} futureRetired={fr}"` |
| Astronaut applicant decoration applied | Verbose (rate-limited per name) | `"StockUiOverlay: applicant decorated name={n} kind={k} ut={ut}"` |
| Mission Control spawn handler entered | Verbose | `"StockUiOverlay: MissionControl spawn — building contract marks futureAcceptedCount={n}"` |
| Mission Control row not found for committed contract | Verbose (rate-limited per Guid) | `"StockUiOverlay: MissionControl committed contract not present in current Contracts list guid={g} — likely already-active suppression"` |
| Despawn cleanup | Verbose | `"StockUiOverlay: {screen} despawn — stripped overlayCount={n}"` |
| Master overlay toggle off | Info | `"StockUiOverlay: feature disabled by ParsekSettings — no decorations applied"` |
| Click-block: contract already committed | Info | `"ContractAcceptPatch: blocking accept for guid={g} — committed at UT {ut}"` |
| Click-block: kerbal already committed for hire | Info | `"KerbalHirePatch: blocking hire for name={n} — committed at UT {ut}"` |
| Click-block bypass during replay | Verbose | `"ContractAcceptPatch: bypass — replay in progress"` (and the parallel line in KerbalHirePatch) |
| New `MilestoneStore` helper non-empty result | Verbose | matches the existing `GetCommittedTechIds` style |

**Silent-branch guards:** every `if/else` in the controller's `BuildMarksFor…` paths logs both branches at Verbose. The OnGUI cleanup loop logs the count of stripped GameObjects so a leak shows up as a steadily-growing number.

---

## 8. Test plan

### 8.1 `MilestoneStoreOverlayHelperTests.cs` — new query helpers

- **`GetCommittedKerbalHireNames_NoMilestones_EmptySet`** — empty store yields empty set. Fails if the helper NREs on cold start.
- **`GetCommittedKerbalHireNames_PastEvent_NotIncluded`** — `LastReplayedEventIndex >= eventIdx`. Fails if the cursor is off-by-one.
- **`GetCommittedKerbalHireNames_FutureEvent_Included`** — single future event. Fails if `IsEventVisibleToCurrentTimeline` filtering breaks future visibility.
- **`GetCommittedKerbalHireNames_HiddenByTimelineFilter_NotIncluded`** — event in a milestone that is hidden by the abandoned-branch filter. Fails if the helper bypasses `IsEventVisibleToCurrentTimeline`.
- **`GetCommittedContractAcceptIds_ParsesGuids`** — three events, two valid Guid keys + one malformed. Expect two-Guid result + one Verbose `Guid.TryParse failed` log.
- **`GetCommittedContractAcceptIds_LogsCount`** — log-assertion test on the non-empty Verbose line.

### 8.2 `StockUiOverlayControllerTests.cs` — pure logic

The controller has Unity dependencies, so we test only the pure-helper layer (mark building) directly. Wrap the existing predicate calls in `internal static BuildTechMarks(MilestoneStore source, double liveUt) → Dictionary<string, TechNodeOverlayMark>` so it's directly testable.

- **`BuildTechMarks_EmptyStore_EmptyDict`** — zero entries.
- **`BuildTechMarks_FutureEvent_PopulatedWithUtAndRecordingId`** — single future event with a `recordingId` carries through to the mark.
- **`BuildTechMarks_PastEvent_NotIncluded`** — past event filtered out.
- **`BuildApplicantMarks_PrefersFutureHiredOverReserved`** — when both predicates fire, FutureHired wins (it carries a UT; Reserved does not).
- **`BuildContractMarks_AlreadyActive_SuppressedWithLog`** — current `ContractSystem.Instance.Contracts` contains the same Guid in `Active` state; the controller suppresses the overlay and logs.
- **`BuildContractMarks_LogsSuppressionCountAtVerbose`** — log-assertion test.

### 8.3 `ContractAcceptPatchTests.cs` and `KerbalHirePatchTests.cs`

Mirror `TechResearchPatchTests.cs` (existing). One test per:

- Allows accept/hire when not committed.
- Blocks when committed; pops the dialog (assert via `CommittedActionDialog.ShowBlocked` test sink).
- Bypasses block when `GameStateRecorder.IsReplayingActions == true`.
- Logs the block at Info with the UT.
- Logs the bypass at Verbose during replay.

### 8.4 Settings round-trip test

`ParsekSettingsRoundTripTests.cs` already exists; add two cases for the new boolean toggles.

### 8.5 Edge-case tests (matching §9)

One test per edge case in the table below; named after the E-id.

### 8.6 In-game tests (`RuntimeTests.cs`)

- **`TopBarReflectsLedgerAfterRecalc`** (Category `"ResourceTopBar"`, Scene `SPACECENTER`) — see §5.4.
- **`RnDOverlayDecoratesCommittedFutureNode`** (Category `"StockUiOverlay"`, Scene `SPACECENTER`) — programmatically open R&D, inject a synthetic milestone with a future TechResearched event, assert that exactly one `Parsek_TechOverlay` child appears under the matching `RDNode` transform.
- **`AstronautOverlayDecoratesReservedAndFutureHired`** — analogous, opens Astronaut Complex, injects two kerbals (one reserved, one future-hired), asserts both badges appear.
- **`MissionControlOverlayDecoratesCommittedContract`** — analogous.
- **`OverlaysClearedOnDespawn`** — open + close R&D twice, assert no `Parsek_TechOverlay` GameObjects survive.

These are the riskiest gaps in unit-test coverage; they exercise the Unity-side wiring that the pure helpers cannot.

---

## 9. Edge cases

| # | Scenario | v1 behavior | Rationale |
|---|---|---|---|
| E1 | Sandbox mode | Controller still runs but every `MilestoneStore` query returns empty; no overlays drawn; no patch firing. | Sandbox has no committed timeline. |
| E2 | Science mode | Same as Career for tech / kerbal hire / facilities. Contracts overlay no-ops because `ContractSystem.Instance` is null in Science. | Mirrors stock UI gating. |
| E3 | Player opens R&D before Parsek's `LedgerOrchestrator` finishes early-load seeding | First `onGUIRnDComplexSpawn` finds an empty marks dict; no decoration. The first ledger-version bump after seeding triggers `RebuildAllVisible()`. | Cold-start race; documented; no silent crash. |
| E4 | A committed-future tech node is unlocked by replaying its source recording while the R&D screen is open | `Ledger.StateVersion` bumps, `RebuildAllVisible()` strips the overlay on the next frame because the event is now past-replayed. | Overlay should disappear automatically. |
| E5 | `RDController.Instance.nodes` field is renamed in a KSP update | One Warn at controller init; no decorations; no crash. | We use reflection with a single-shot warn (mirrors `KspStatePatcher.protoTechNodes` pattern). |
| E6 | Stock KSP UI rebuilds the panel mid-display (e.g. tier filter button) | Our overlay GameObjects are children of the panel rows. When KSP rebuilds, the children go with the parents; the next frame's `RebuildAllVisible()` (triggered via a fast 30Hz poll on the panel root's child count) repaints. | Cheap poll; well below the cost of a new RDNode click. |
| E7 | Two committed-future events for the same key (e.g. tech researched twice in two recordings) | Mark uses the **earliest** UT; tooltip notes `(+N more committed)`. | Correct: the player will hit the earliest first. |
| E8 | The committed event references a recording that has since been deleted | `RecordingStore.GetById(rid)` returns null; tooltip degrades to `"Committed at UT 12345"` without the recording name. One Verbose log. | Defensive. |
| E9 | A contract whose Guid is committed-future-accepted is no longer in `ContractSystem.Instance.Contracts` (KSP regenerated the offered list) | Suppress the overlay with one Verbose log. | The player will not see this offer; nothing to decorate. |
| E10 | Player has the overlay setting off | Controller still subscribes to events but skips decoration; click-blocks remain (separate setting). | Decoupled toggles. |
| E11 | Click-blocks setting off | Patches no-op (return true) with a Verbose `"feature disabled by ParsekSettings"` log. | Lets the player accept the consequence consciously. |
| E12 | `ContractAcceptPatch` fires during an internal stock-KSP `Contract.Accept` triggered by another mod's contract auto-accept | If `IsReplayingActions == false` and the Guid is committed, we still block. The mod loses the auto-accept. **Documented limitation**; no v1 escape hatch. | Better than silently desyncing the ledger. Worst case: player toggles off click-blocks. |
| E13 | KSP renames the kerbal-hire entry-point method between versions | `KerbalHirePatch.TargetMethod()` returns null; Harmony skips the patch with one Warn (matches `CrewDialogFilterPatch` pattern). Overlay still draws. | Defensive against KSP API drift. |
| E14 | Astronaut Complex is opened from inside Flight via a mod | Controller is registered for `SPACECENTER` only in v1; the Flight-scene overlay does not draw. **Acceptable v1 limitation.** Click-blocks still fire (Harmony patches are scene-agnostic). | Most players open the Astronaut Complex from KSC; covering Flight too means a second `KSPAddon(GameScenes.FLIGHT, false)` later. |
| E15 | Player accepts a contract live, then commits a recording that contains a `ContractAccepted` for the same Guid (impossible but defensive) | Overlay suppresses (E9 path) when the contract is already in `Active` state. Click-block does not fire because the contract is no longer offered. Ledger walk handles the duplicate via `ContractsModule`'s existing accept-idempotence. | Triple-defended. |
| E16 | Overlay GameObjects accumulate after many open/close cycles (leak) | The `OverlaysClearedOnDespawn` in-game test guards against this. The despawn handler logs the strip count so a leak surfaces as `stripped=0 stripped=0 stripped=0` despite open cycles. | Observability over silent correctness. |

---

## 10. Out of scope for v1 (explicit)

- **Flight-scene overlays.** The Astronaut Complex and Mission Control are SPACECENTER-only screens by KSP convention; mods that surface them in Flight are not supported in v1.
- **Per-user-dismissible overlays.** No "dismiss this badge until next session" affordance.
- **Non-stock screens.** ContractConfigurator's own Mission Control replacement, KCT's build queue, etc. are out of scope. Other mods can subscribe to the same `MilestoneStore` helpers if they want parity.
- **Per-contract / per-kerbal "claim" UI.** No way to tell Parsek "actually I want to override the timeline and accept this anyway" — the current escape hatch is the master setting toggle.
- **Future-completed / future-failed contract overlays.** v1 only flags FutureAccepted. The other two states are valuable but the data shape is identical and they can be added in a small follow-up once v1 is shipped and the overlay infrastructure is proven.
- **Strategy activate overlays in the Administration building.** Same shape; defer to a follow-up. Strategies are less commonly recorded than contracts/kerbals/tech in current playtest data.
- **Tooltip styling polish.** v1 uses stock `GUI.skin.box` + `GUIContent` tooltip; KSP has a fancier tooltip system (`KSP.UI.TooltipTypes.TooltipController_Text`). Use the fancier one in a follow-up if it is worth the dependency surface.

---

## 11. Files to touch (final inventory)

**New:**
- `Source/Parsek/StockUiOverlayController.cs` — controller MonoBehaviour + `KSPAddon(GameScenes.SPACECENTER, false)`.
- `Source/Parsek/OverlayBadge.cs` — small shared MonoBehaviour for the badge visual.
- `Source/Parsek/Patches/ContractAcceptPatch.cs` — Harmony prefix on `Contracts.Contract.Accept()`.
- `Source/Parsek/Patches/KerbalHirePatch.cs` — Harmony prefix on the Astronaut Complex hire entry point (exact target TBD during implementation).
- `Source/Parsek.Tests/MilestoneStoreOverlayHelperTests.cs` — coverage for the new `MilestoneStore` helpers.
- `Source/Parsek.Tests/StockUiOverlayControllerTests.cs` — pure-helper coverage.
- `Source/Parsek.Tests/ContractAcceptPatchTests.cs`.
- `Source/Parsek.Tests/KerbalHirePatchTests.cs`.

**Modified:**
- `Source/Parsek/MilestoneStore.cs` — three new query helpers (§4.1).
- `Source/Parsek/Patches/TechResearchPatch.cs` — gate on the new "click-blocks" setting.
- `Source/Parsek/Patches/FacilityUpgradePatch.cs` — gate on the new "click-blocks" setting.
- `Source/Parsek/UI/SettingsWindowUI.cs` — two new checkboxes.
- `Source/Parsek/ParsekSettings.cs` + `ParsekSettingsPersistence.cs` — two new boolean fields with default `true`, round-tripped.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — five new `[InGameTest]` methods.
- `Source/Parsek.Tests/ParsekSettingsRoundTripTests.cs` — two new round-trip cases.
- `docs/dev/todo-and-known-bugs.md` — new entry for the feature; mark related items as covered.
- `CHANGELOG.md` — `Unreleased` entry: "Stock R&D, Astronaut Complex, and Mission Control screens now flag actions that pending recordings have already committed; double-clicking is blocked with a clear message. Top resources bar reflects the ledger after every recalc (verified by a new in-game test)."

**Not touched** (to reiterate): `Ledger`, `RecalculationEngine`, all eight resource modules (the new `MilestoneStore` helpers do not change module surface), `KspStatePatcher`, `CrewDialogFilterPatch`, save format, sidecars, the Parsek Career window.

---

## 12. Implementation phases

Sequential on this branch (no isolation worktrees — the changes are tightly coupled and we want one PR for the feature). Each phase ends with `dotnet build && dotnet test` green before the next starts.

**Phase 1 — Data layer (small, self-contained).**
- Add the four `MilestoneStore` helpers (§4.1): `GetCommittedContractAcceptIds`, `GetCommittedKerbalHireNames`, `GetCommittedKerbalRetireNames` (and reuse the existing tech / facility helpers for symmetry).
- Add the unit tests in §8.1.
- One commit.

**Phase 2 — Click-blocks for contracts and kerbals.**
- Add `ContractAcceptPatch.cs` and `KerbalHirePatch.cs` (the `KerbalHirePatch.TargetMethod()` mirror of `CrewDialogFilterPatch.TargetMethod()` is the model).
- Wire them through `ParsekHarmony.Awake()` with the existing try/catch pattern.
- Add the patch tests in §8.3.
- One commit.

**Phase 3 — Settings toggles.**
- Add the two boolean fields + persistence + UI checkboxes.
- Gate the four click-blocks (existing two + new two) on the click-block setting.
- Add the round-trip tests.
- One commit.

**Phase 4 — Overlay controller (the big one).**
- `StockUiOverlayController` + `OverlayBadge` + the three `BuildXMarks` helpers as `internal static` for direct testability.
- Subscribe to all six `onGUI*Spawn / Despawn` events.
- Decorate / strip per the §5.1–5.3 rules.
- Add `StockUiOverlayControllerTests.cs` for the pure helpers.
- One commit.

**Phase 5 — In-game tests (the verification layer).**
- Add the five new `[InGameTest]` methods in §8.6.
- Run `dotnet test --filter InjectAllRecordings`, then launch KSP, run Ctrl+Shift+T, capture results.
- One commit (tests only; no production change).

**Phase 6 — Final review pass + docs + CHANGELOG.**
- Self-review against this doc's checklist.
- Update `docs/dev/todo-and-known-bugs.md` and `CHANGELOG.md` per CLAUDE.md per-commit guidance.
- Open PR.

Total expected: 6 commits, ~10–12 files touched, fits in one PR.

---

## 13. Open questions for the user

1. **Master toggle wording.** Proposed: "Show committed-future overlays in stock UI" and "Block player actions that conflict with committed timeline". Want shorter? More explicit?
2. **Badge visual.** Proposed: a small clock icon top-right of the row, hover for tooltip. Alternatives: a colored side stripe, a faded-out row with a banner. I'd default to the clock icon and tighten in a follow-up if playtest finds it unclear.
3. **Recording-name in tooltip.** Worth showing the source recording's display name, or is "Committed at UT 12345" enough? My default: include the name when known, fall back to UT-only.
4. **Should the click-block on contracts also fire when a mod auto-accepts a contract?** v1 says yes (E12). If you want a per-mod escape hatch, I would design that as a separate setting.
5. **Astronaut Complex / Flight-scene support (E14).** v1 is SPACECENTER-only. Want me to include a Flight-scene `KSPAddon` in Phase 4? Adds maybe 20 lines.

None of these block implementation; defaults are listed for each.

**Resolved before publication:** `GameStateEventType.CrewHired` and `CrewRemoved` are already in the enum (`GameStateEvent.cs:17-18`) and emitted by `GameStateRecorder.cs:779`. Phase 1 has no enum-extension contingency.

---

## 14. Next step

Assuming this design passes user review, the implementation cycle per `docs/dev/development-workflow.md` Step 4 is:

- **4a (Plan):** the six phases in §12 are the plan; no separate Plan agent needed.
- **4c (Implement):** phases 1–5 run sequentially on this branch; no isolation worktrees.
- **4d (Review):** clean-context review agent at the end of phase 5 reviews the whole feature against this doc before phase 6 (PR creation).

Ready to start phase 1 on approval.
