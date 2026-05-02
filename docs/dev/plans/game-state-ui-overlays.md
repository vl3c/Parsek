# Game-State UI Overlays — Design Pass

**Branch:** `feat/game-state-ui-overlays` (worktree at `C:\Users\vlad3\Documents\Code\Parsek\Parsek-game-state-ui-overlays`)
**Status:** implemented through phases 1-6 on this branch; retained as the implementation contract and review reference.

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
| Facilities (KSC view) | Upgraded (correct) | Looks unupgraded until clicked — already click-blocked by `FacilityUpgradePatch`; visual overlay deferred to v2 (see §10) | Unupgraded (correct) |
| Mission Control | Completed in history (correct) | **Looks freshly offered**; nothing prevents double-accept | Offered (correct) |
| Astronaut Complex | Hired in history (correct) | **Looks like a normal applicant**; nothing prevents double-hire | Applicant (correct) |
| VAB/SPH crew dialog | Hidden (correct) | Hidden (correct) | Visible (correct) |
| Top resources bar | Patched (correct) | n/a | n/a |

The three cells marked in bold are the v1 overlay scope (R&D, Mission Control, Astronaut Complex). Facilities is **out of overlay scope for v1** — the existing `FacilityUpgradePatch` already prevents the player from wasting funds on a double-upgrade, so the safety property holds even without a visual marker. The KSC top-down view also has a less obvious per-facility hook than the three building screens; adding it cleanly is a v2 task. The two new click-blocks (contracts + kerbals) are scope-orthogonal: they ship in v1 alongside the three new overlays.

User-facing complaint: "I have to keep checking the Parsek Career window before I do anything in the buildings, otherwise I waste a click and read a popup."

---

## 2. Terminology

- **Committed-future action** — an action stored in `MilestoneStore` whose source milestone is committed but whose `LastReplayedEventIndex` has not advanced past it yet. Equivalently: an entry returned by `MilestoneStore.GetCommittedTechIds()` / `GetCommittedFacilityUpgrades()`, or — once the new helpers below ship — by `MilestoneStore.GetCommittedContractAcceptIds()` / `…KerbalHireNames()`.
- **Reserved kerbal** — a `ProtoCrewMember` that `KerbalsModule.ShouldFilterFromCrewDialog(name)` returns true for. This already covers reserved (slot owner stand-ins) and retired kerbals; for the Astronaut Complex overlay we want the same predicate plus a separate "will be hired at UT" predicate sourced from a new Kerbals helper.
- **Decorate vs hide** — for VAB/SPH crew dialogs we *hide* reserved kerbals (existing behavior); for Astronaut Complex we *decorate* them. The player wants to see the roster they actually have; an invisible kerbal in their own roster is confusing.
- **Click-block** — the existing pattern from `TechResearchPatch`: a Harmony prefix returns `false` and shows `CommittedActionDialog.ShowBlocked` with the committed UT. We extend the pattern to two more action types in this PR.
- **Unreplayed-event predicate** — the actual source of truth for "should the overlay decorate this row". Equals `MilestoneStore`'s existing `LastReplayedEventIndex + 1 .. Events.Count` slice intersected with `GameStateStore.IsEventVisibleToCurrentTimeline`. **NOT** a `event.ut > liveUT` filter — `LastReplayedEventIndex` already accounts for past-but-not-yet-replayed cases (e.g. timeline trims, recording purges). Using a UT cutoff here would silently desync overlays from `TechResearchPatch` / `FacilityUpgradePatch` / the new patches, which all use the unreplayed predicate.
- **Live UT (display only)** — `Planetarium.GetUniversalTime()` at render time. Used in the tooltip text (`"Will unlock at UT 12345"`) but never as a filter on the overlay set.

---

## 3. Mental model

Two parallel stores hold the truth, and the overlay must read both:

- `Ledger.Actions` — the converted action list. `KspStatePatcher` writes the **terminal** state of these actions into KSP singletons (R&D, Funds, Reputation, ProgressTracking, ContractSystem). After every recalc, the singletons reflect the end of the timeline.
- `MilestoneStore.Milestones[*].Events[*]` — the raw recorded `GameStateEvent`s. Each milestone tracks `LastReplayedEventIndex`; the slice from `LastReplayedEventIndex + 1` to the end is the **unreplayed-and-still-visible** slice. The existing click-blocks (`TechResearchPatch` / `FacilityUpgradePatch`) read this slice via `MilestoneStore.GetCommittedTechIds()` etc. — that is the source of truth this PR mirrors.

Note: `CrewHired` round-trips into `Ledger` (via `GameActions/GameStateEventConverter.cs:172`), but `CrewRemoved` does **not** — the converter explicitly returns `null` for it (`GameStateEventConverter.cs:219-224`). That means `FutureRetired` data is available only from `MilestoneStore`, not from `Ledger`. The plan reads everything overlay-related from `MilestoneStore` for consistency, never from `Ledger.Actions` directly.

```
   ┌─ MilestoneStore (raw events, unreplayed slice = source of truth for overlays + click-blocks) ─┐
   │                                                                                                │
   │  GetCommittedTechIds()           ◄── existing, used by TechResearchPatch                       │
   │  GetCommittedFacilityUpgrades()  ◄── existing, used by FacilityUpgradePatch                    │
   │  GetCommittedContractAcceptIds() ◄── NEW, used by ContractAcceptPatch + Mission Control overlay│
   │  GetCommittedKerbalHireNames()   ◄── NEW, used by KerbalHirePatch + Astronaut overlay          │
   │  GetCommittedKerbalRetireNames() ◄── NEW, used by Astronaut overlay only                       │
   │                                                                                                │
   └────────────────────────────────────────────────────────────────────────────────────────────────┘
                                          │                            │
                                ┌─────────┘                            └────────┐
                                ▼                                               ▼
                    ┌──────────────────────┐                       ┌──────────────────────┐
                    │   Click-block patches │                       │ StockUiOverlayController│
                    │   (refuse the click)  │                       │ (decorate stock rows) │
                    └──────────────────────┘                       └──────────────────────┘
                                ▲                                               ▲
                                │                                               │
                                └───────  same predicate, no UT filter   ───────┘
```

Critical invariant, **scoped to action types that have BOTH an overlay AND a click-block**: **overlay candidate set == click-block predicate set**. In v1 that means three action types: tech research, contract accept, kerbal hire. (Facility upgrade has a click-block — `FacilityUpgradePatch` — but no overlay in v1; see §1 and §10. The invariant becomes applicable to facility upgrade once a v2 facility overlay ships.) The implementation enforces this by reading the same `MilestoneStore` helper from both sides — never adding a UT filter to one side without the other, and never letting a UI-surface suppression (E9, E17) leak into the click-block predicate.

The invariant does **not** apply to overlay-only kinds (`FutureRetired`, `ReservedActive`, `ReservedRetired`) because there is no clickable "retire" or "un-reserve" affordance in the Astronaut Complex to block. Those are pure visual telemetry — the player cannot accidentally double-act on them. E18 in §9 lists the three pairwise unit tests that enforce the invariant in v1.

**Rule for adding a 5th action type later:** if the new type has a clickable affordance in any stock UI (e.g. someone adds a Strategy-activation overlay), both an overlay path AND a matching click-block patch must ship in the same PR — the invariant is non-negotiable for clickable kinds. Overlay-only kinds are reserved for purely informational state with no click surface (the three Astronaut Complex non-Hire kinds today). E18 must be extended with a fifth pairwise test in that PR.

The overlay is a thin presentation layer; it never mutates ledger state or KSP singletons. Click-blocks are the only mutators added by this PR, and they only refuse player actions — they do not invent new ones.

---

## 4. Data model

### 4.1 New `MilestoneStore` query helpers

Three new helpers, mirroring the existing `GetCommittedTechIds()` shape (one pass over `milestones`, filter on `LastReplayedEventIndex + 1`, gated on `IsEventVisibleToCurrentTimeline`):

```csharp
// MilestoneStore additions
internal static HashSet<string> GetCommittedContractAcceptIds()
{
    // GameStateEventType.ContractAccepted, key = contract.ContractGuid.ToString()
    // (default "D" form with hyphens, as written by GameStateRecorder.cs:413/522/553/583/608).
    // Returns the RAW key strings — do NOT parse to Guid here. Two reasons:
    //   1. MilestoneStore.FindCommittedEvent does an exact ordinal string compare on `key`,
    //      so the consumer must round-trip the same string shape the recorder wrote.
    //   2. Parsing-and-reformatting would silently drop modded contracts that happen to
    //      use a non-Guid-shaped key. Keep the raw string; let the patch boundary parse
    //      to Guid only if it actually needs the typed value.
    // See §5.3 for the consumer-side string-key contract and §8.1 for the
    // GetCommittedContractAcceptIds_KeyShapeMatchesRecorder test that pins this down.
}

internal static HashSet<string> GetCommittedKerbalHireNames()
{
    // GameStateEventType.CrewHired, key = crew.name (emitted by
    // GameStateRecorder.cs:779 with key = crew.name).
    // Converter side: GameActions/GameStateEventConverter.cs:172-173
    // dispatches CrewHired to ConvertCrewHired (we do not depend on the
    // converted ledger action — the helper reads MilestoneStore directly).
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

**Verified before plan publication:**
- `GameStateEventType.CrewHired` (value 8), `CrewRemoved` (9), and `CrewStatusChanged` (10) all exist in `Source/Parsek/GameStateEvent.cs:17-19`.
- `GameStateRecorder.cs:779` emits `CrewHired` with `key = crew.name`. `GameStateRecorder.cs:823` emits `CrewRemoved` with `key = crew.name`.
- Only `CrewHired` is converted to a ledger action (`Source/Parsek/GameActions/GameStateEventConverter.cs:172` → `ConvertCrewHired`). `CrewRemoved` falls into the "intentionally not converted" group at `GameStateEventConverter.cs:219-224` and `ConvertEvent` returns `null`. **This is fine for our use case** — the new `GetCommittedKerbalRetireNames()` helper reads `MilestoneStore` directly (the raw `GameStateEvent` slice), which is also where `GetCommittedTechIds()` reads from. We do not need ledger-side coverage for the FutureRetired overlay.
- No enum or converter changes are required by this PR.

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
    public ApplicantOverlayKind Kind;          // ReservedActive | ReservedRetired | FutureHired | FutureRetired
    public double CommittedUt;                 // NaN for the two Reserved* kinds (live stand-ins, no committed UT)
}
internal enum ApplicantOverlayKind
{
    ReservedActive,    // KerbalsModule slot owner currently has a live stand-in
    ReservedRetired,   // KerbalsModule retired/displaced stand-in (still managed)
    FutureHired,       // GameStateEventType.CrewHired in the unreplayed slice
    FutureRetired      // GameStateEventType.CrewRemoved in the unreplayed slice
}

internal readonly struct ContractOverlayMark
{
    public string ContractGuidKey;             // raw event key string, exact same shape the recorder writes
                                                // (contract.ContractGuid.ToString() — default "D" form, with hyphens)
    public ContractOverlayKind Kind;           // FutureAccepted | FutureCompleted | FutureFailed
    public double CommittedUt;
}
internal enum ContractOverlayKind { FutureAccepted, FutureCompleted, FutureFailed }
```

`CommittedRecordingId` is included so the overlay can show "from recording '<name>'" in the tooltip when known. Uses `RecordingStore.GetById(rid)?.DisplayName` with a raw-id fallback.

### 4.3 Persisted state and refresh trigger

**No persisted state.** All overlay state is transient.

**Caching policy:** marks are built **on `onGUI*Spawn`** and held until `…Despawn`. They are NOT rebuilt per OnGUI frame (the §13 review caught this — per-OnGUI walks would be three full passes over `MilestoneStore.Milestones[*].Events[k..]` per frame at 60Hz, too expensive at hundreds of milestones).

**Refresh trigger** (replaces the original `Ledger.StateVersion` polling proposal): the controller subscribes to `LedgerOrchestrator.OnTimelineDataChanged` (the established subscribe pattern — `Source/Parsek/ParsekUI.cs` already subscribes at lines 116/131/134/1290 for the Timeline, Recordings, Kerbals, and Career windows; see also `docs/dev/plans/career-state-window.md` §5.6). When that fires AND the relevant building is currently open, the controller calls `RebuildAllVisible()` on the next OnGUI frame.

**Honest about the routing** (review feedback): `OnTimelineDataChanged` is `internal static Action` declared at `Source/Parsek/GameActions/LedgerOrchestrator.cs:106`, with **a single publisher** at `LedgerOrchestrator.cs:1577` inside `RecalculateAndPatch`. The overlay's source-of-truth predicate depends on three things:
1. `Ledger.Actions` mutated.
2. `MilestoneStore.LastReplayedEventIndex` advanced (writes at `MilestoneStore.cs:132`/`:196`/`:382`/`:393`/`:511` and `RecordingStore.cs:2946`).
3. `GameStateStore.IsEventVisibleToCurrentTimeline` flipped (driven by `RecordingStore` timeline visibility changes).

In the current codebase, **every code path that mutates one of those three convenes on `RecalculateAndPatch`** — there are 9 call sites in `ParsekScenario.cs` plus the supersede / commit / rewind paths, and they all run a recalc after the mutation. So `OnTimelineDataChanged` is the right signal in practice, but it is not a primitive on the three signals — it is a coincidence of the codebase's discipline.

**Contract for future changes:** any code that mutates `MilestoneStore.LastReplayedEventIndex` or `GameStateStore` event visibility must end with `LedgerOrchestrator.RecalculateAndPatch()` (or directly invoke `OnTimelineDataChanged`). If a future contributor adds a mutation site that does not honor this, the overlay will silently go stale until the building is reopened. The Phase 6 doc updates should add this contract to `MilestoneStore.cs`'s file-level XML comment and to `LedgerOrchestrator.OnTimelineDataChanged`'s declaration comment.

### 4.4 What we explicitly do NOT add

- No new Parsek window. The Career window already exists for the cross-screen view.
- No Parsek toolbar entry for an "overlay on/off" toggle in v1. If players ask, add a setting to `ParsekSettings`; not before.
- No persisted user dismissal of individual overlays. They are recomputed every frame from ledger state.

---

## 5. Behavior

### 5.1 Tech tree overlay (R&D building)

**Hook:** subscribe to `RDController.OnRDTreeSpawn` (the `EventData<RDController>` event that fires after `RDController.Instance` is populated) and `RDController.OnRDTreeDespawn`. This is preferred over `GameEvents.onGUIRnDComplexSpawn` because it fires after the controller has its `nodes` list ready — no one-frame retry, no null check race.

If `OnRDTreeSpawn` does not exist in the running KSP version (defensive — verify during implementation by reading the decompiled `Assembly-CSharp.dll`), fall back to `GameEvents.onGUIRnDComplexSpawn` plus a one-frame delay before reading `RDController.Instance.nodes`.

**On spawn:**

1. Build a `Dictionary<string, TechNodeOverlayMark>` from `MilestoneStore.GetCommittedTechIds()`. For each id, call `MilestoneStore.FindCommittedEvent(GameStateEventType.TechResearched, id)` to get the UT and recording id.
2. Walk `RDController.Instance.nodes` (a `List<RDNode>` field on the controller, verified by decompile). For each `RDNode` whose `tech.techID` is in our marks dict, attach a child `GameObject` named `Parsek_TechOverlay` under the node's transform. Attach a `MonoBehaviour` `TechOverlayBadge` that draws a small clock-icon + tooltip ("Committed at UT 12345 — recording 'Mun Lander v3'"). The badge listens for pointer enter/exit to flip the tooltip, and null-checks its parent transform on `Update` so a stock-side re-layout (filter button click) does not leave dangling MonoBehaviours.

**On despawn:** destroy every child `GameObject` named `Parsek_TechOverlay` under the R&D root, no leaks across re-opens.

**Click-block:** unchanged. `TechResearchPatch` already blocks. Overlay just makes the block predictable.

**Refresh while open:** see §4.3 — `LedgerOrchestrator.OnTimelineDataChanged` triggers `RebuildAllVisible()`.

### 5.2 Astronaut Complex overlay

**Hook:** `GameEvents.onGUIAstronautComplexSpawn` / `…Despawn`.

**On spawn:**

1. Build an `applicantMarks` dict by walking `HighLogic.CurrentGame.CrewRoster.Crew` and `…Applicants` (and `…Tourist` if the player ever sees them in this list).
2. For each name, classify (priority order — first match wins so FutureHired beats Reserved):
   - **FutureHired** if `MilestoneStore.GetCommittedKerbalHireNames()` contains the name (sourced from `GameStateEventType.CrewHired`). Tooltip: "Will be hired at UT 12345 — recording '...'".
   - **FutureRetired** if `MilestoneStore.GetCommittedKerbalRetireNames()` contains the name (sourced from `GameStateEventType.CrewRemoved`). Tooltip with UT, badge color muted.
   - **ReservedActive / ReservedRetired** — call `LedgerOrchestrator.Kerbals?.GetReservationKind(name)` (the new helper added in Phase 1.5; see §11 modified-files list and §12 Phase 1.5). Switch on the returned enum: `ReservedActive` → "Reserved by Parsek for slot '<slotOwner>'"; `ReservedRetired` → "Retired stand-in (managed by Parsek)"; `NotManaged` → fall through to no-match.
   - **No match** — skip. (`KerbalsModule.ShouldFilterFromCrewDialog` is the union of ReservedActive + ReservedRetired and is what VAB/SPH uses to *hide* — for the Astronaut Complex we want the finer split so the tooltip can explain which case it is. The reason for the new `GetReservationKind` helper rather than reading `IsManaged` + `GetActiveOccupant != null` directly: `GetActiveOccupant` returns `null` in two distinct cases — "all occupants in the reserved prefix are still reserved" AND "no active stand-in" (verified at `Source/Parsek/KerbalsModule.cs:830-832`) — so a naive `GetActiveOccupant != null` split would misclassify the first case. The `KerbalsModule` accessors are instance methods reached through `LedgerOrchestrator.Kerbals?.…` — see `Source/Parsek/Patches/CrewDialogFilterPatch.cs:48-51`, `KerbalDismissalPatch.cs:35`, `Source/Parsek/GameStateRecorder.cs:873`.)
3. Find the panel's `UIList` that drives the applicants column. The Astronaut Complex UI is `KSP.UI.Screens.AstronautComplex`; its applicants are rendered into a `KSP.UI.CrewListItem` per row (verified by decompile — note the type name is `CrewListItem`, not `KerbalListItem`). The list is accessed via private fields on `AstronautComplex`; the implementation must reflect them, named like `availableCrewList` / `assignedCrewList` (verify exact names during Phase 4 by reading the decompiled assembly). Wrap the reflection in a one-shot warn-on-failure helper following the `KspStatePatcher.protoTechNodes` pattern (see `Source/Parsek/GameActions/KspStatePatcher.cs:439-474`).
4. For each `CrewListItem` row, derive the kerbal name. `CrewListItem` exposes `GetName() : string` publicly; **no public `ProtoCrewMember` accessor** (verified by decompile). v1 strategy: use `GetName()` for matching. The kerbal name in stock KSP `CrewRoster` is unique within a save (no two `ProtoCrewMember`s can share `name`), so name-based matching is safe. Locale risk: `GetName()` returns the display name which IS the same as `pcm.name` in stock KSP; if a localization mod changes that, the matching breaks (one Verbose log; overlay simply does not draw — fail-soft).
5. For each `CrewListItem` whose `GetName()` matches an entry in `applicantMarks`, attach a child `Parsek_KerbalOverlay` GameObject under the row transform with a small badge. Color/icon varies by `ApplicantOverlayKind`.

**On despawn:** strip every `Parsek_KerbalOverlay` child under the Astronaut Complex root.

**Click-block** (NEW patch `KerbalHirePatch`): Harmony prefix on `KerbalRoster.HireApplicant(ProtoCrewMember ap)` (verified entry point via decompile — the Astronaut Complex's private `HireRecruit(UIList fromlist, UIList tolist, UIListItem listItem)` calls into this, as does any modded auto-hire path). The patch:
1. Bypasses if `GameStateRecorder.IsReplayingActions == true` (mirrors `TechResearchPatch.cs:18-23`) — Parsek's own recalc/replay must be allowed to add kerbals back.
2. If `ap.name` is in `MilestoneStore.GetCommittedKerbalHireNames()`, looks up the committed UT via `MilestoneStore.FindCommittedEvent(GameStateEventType.CrewHired, ap.name)`, then returns `false` and shows `CommittedActionDialog.ShowBlocked("Cannot hire \"" + ap.name + "\"", "This kerbal is already committed on your timeline at UT …", "")`. Mirrors `TechResearchPatch`.

Why patching `KerbalRoster.HireApplicant` (and not the Astronaut Complex UI handler) is correct: it's scene-agnostic (resolves E14 — modded Flight-scene Astronaut Complex still gets the click-block), it's the single funnel the stock UI + every mod's auto-hire goes through, and it has a stable signature across KSP versions (private UI internals are not stable).

**Name uniqueness:** `ProtoCrewMember.name` is globally unique inside `HighLogic.CurrentGame.CrewRoster` — KSP refuses to add a second roster entry with the same name. So `ap.name` is a safe matching key.

**No filter on Astronaut Complex** — unlike VAB/SPH, the player keeps seeing the kerbal. The overlay tells the story; the click-block enforces the rule.

### 5.3 Mission Control overlay

**Hook:** `GameEvents.onGUIMissionControlSpawn` / `…Despawn`.

**On spawn:**

1. Build a `Dictionary<string, ContractOverlayMark>` keyed by the **raw event key string** from `MilestoneStore.GetCommittedContractAcceptIds()`. The recorder writes the key as `contract.ContractGuid.ToString()` (default "D" form with hyphens — verified at `Source/Parsek/GameStateRecorder.cs:413, 522, 553, 583, 608`). `MilestoneStore.FindCommittedEvent` does an ordinal string compare on `key`, so the overlay must use the SAME string shape — never `ToString("N")` (no-hyphen form). For each entry, call `FindCommittedEvent(GameStateEventType.ContractAccepted, keyString)` for UT + recording id.
2. Walk `ContractSystem.Instance.Contracts`; for each contract, compute `contract.ContractGuid.ToString()` and look it up in the marks dict. Match → attach a `Parsek_ContractOverlay` GameObject under the row that the Mission Control window renders for that contract.
3. The overlay text reads "Will be accepted at UT 12345 — recording '...'". If the contract is also already in the player's Active list (rare race: it was accepted live then re-emerged in a future commit), suppress the badge with one Verbose log.

**On despawn:** strip every `Parsek_ContractOverlay`.

**Click-block** (NEW patch `ContractAcceptPatch`): Harmony prefix on `Contracts.Contract.Accept()`. The patch:
1. Bypasses if `GameStateRecorder.IsReplayingActions == true`.
2. Computes `keyString = __instance.ContractGuid.ToString()` (default "D" form, matching the recorder).
3. If `keyString` is in `GetCommittedContractAcceptIds()`, looks up UT via `FindCommittedEvent(GameStateEventType.ContractAccepted, keyString)`, then returns `false` and `CommittedActionDialog.ShowBlocked`.

**Helper return type contract:** `GetCommittedContractAcceptIds()` returns `HashSet<string>` (raw event keys), NOT `HashSet<Guid>`. This avoids `Guid.TryParse` per-call work and mirrors the existing `GetCommittedTechIds` / `GetCommittedFacilityUpgrades` shape. Anywhere downstream needs a real `Guid`, parse at the boundary with one Verbose log on failure.

**Why both overlay AND click-block:** they answer different questions for the player. The overlay says "this is happening on your timeline already, don't waste your offered-slot quota"; the click-block enforces it if they click anyway.

### 5.4 Top resources bar — verification only

No production code changes. We add an in-game test that:

1. Captures `Funding.Instance.Funds`, `ResearchAndDevelopment.Instance.Science`, `Reputation.Instance.reputation` before and after a synthetic `LedgerOrchestrator.RecalculateAndPatch()`.
2. Asserts the values match `FundsModule.GetAvailableFunds()` / `ScienceModule.GetAvailableScience()` / `ReputationModule.GetRunningRep()`.
3. Asserts that exactly one `OnFundsChanged` / `OnScienceChanged` / `OnReputationChanged` `GameEvents` fires per delta during the patch (subscribe a counter inside the test, assert the count). This is the public contract that `CurrencyWidgetsApp` listens to; if those fire correctly the bar is correct.

The test deliberately does NOT reflect on `CurrencyWidgetsApp`'s private widget fields — that contract is brittle across KSP patches. If the events fire and the singletons hold the right values, the widget being out of sync would be a stock-KSP bug, not Parsek's bug.

Test lives in `Source/Parsek/InGameTests/RuntimeTests.cs` (Category = "ResourceTopBar", Scene = `GameScenes.SPACECENTER`).

### 5.5 Cross-screen: shared overlay infrastructure

A single `StockUiOverlayController` singleton (a `KSPAddon(GameScenes.SPACECENTER, false)`; v1 is SPACECENTER-only — see §10) owns:

- Subscriptions to: `RDController.OnRDTreeSpawn` / `OnRDTreeDespawn`, `GameEvents.onGUIAstronautComplexSpawn` / `…Despawn`, `GameEvents.onGUIMissionControlSpawn` / `…Despawn`, `LedgerOrchestrator.OnTimelineDataChanged`.
- A small `OverlayBadge` MonoBehaviour shared by all three screens, parameterized by icon + tooltip text. The badge `Update()` null-checks its parent transform; if the parent is destroyed (stock-side re-layout), it self-destroys with one Verbose log.
- A `RebuildAllVisible()` method called when `OnTimelineDataChanged` fires AND a tracked screen is currently open. If no tracked screen is open, the call is a no-op (one Verbose log).

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

**No serialized-enum changes.** Verified `GameStateEventType.CrewHired` and `CrewRemoved` already exist and are emitted by the recorder. (Only `CrewHired` is converted to a ledger action — see §4.1 verified note.) No save-format risk in this PR.

**Post-Change Checklist** (CLAUDE.md): N/A. No new persisted fields, no new enum values, no `ParsekScenario.OnSave/OnLoad` change, no test-generator change. The two new `ParsekSettings` boolean fields are the only new persisted state, and `ParsekSettingsPersistence` is already round-tripped per the existing test pattern.

---

## 7. Diagnostic logging

Subsystem tag `"StockUiOverlay"` for the controller, `"ContractAcceptPatch"` and `"KerbalHirePatch"` for the new patches.

| Decision point | Level | Line |
|---|---|---|
| Controller initialised | Info | `"StockUiOverlay: initialised, listening for R&D / Astronaut / MissionControl spawns + LedgerOrchestrator.OnTimelineDataChanged"` |
| `OnTimelineDataChanged` fired, no tracked screen open | Verbose | `"StockUiOverlay: timeline changed but no tracked screen open — RebuildAllVisible no-op"` |
| `OnTimelineDataChanged` fired, screen open → rebuild scheduled | Verbose | `"StockUiOverlay: timeline changed — scheduling RebuildAllVisible for {screen}"` |
| R&D spawn handler entered | Verbose | `"StockUiOverlay: R&D spawn — building tech marks committedTechCount={n}"` |
| R&D mark count | Info | `"StockUiOverlay: R&D decorated nodeCount={n} of total={t}"` |
| R&D `RDController.Instance.nodes` reflection failure (KSP API drift) | Warn (one-shot per session) | `"StockUiOverlay: RDController.nodes reflection failed — tech overlays disabled this session ({detail})"` |
| AstronautComplex private list-field reflection failure (KSP API drift) | Warn (one-shot per session) | `"StockUiOverlay: AstronautComplex list-field reflection failed — applicant overlays disabled this session ({detail})"` |
| `CrewListItem.GetName()` returns name not in roster (locale mod / mismatch) | Verbose (rate-limited per row) | `"StockUiOverlay: applicant row name '{n}' not found in CrewRoster — overlay skipped"` |
| Badge parent transform destroyed (stock-side re-layout) | Verbose | `"StockUiOverlay: {screen} badge self-destruct, parent gone — name={n}"` |
| Astronaut spawn handler entered | Verbose | `"StockUiOverlay: Astronaut spawn — building applicant marks reservedActive={ra} reservedRetired={rr} futureHired={fh} futureRetired={fr}"` |
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
- **`GetCommittedContractAcceptIds_ReturnsRawKeyStrings`** — three events with three contract Guid keys (default "D" form). Expect three-string result, no parsing performed inside the helper. Fails if the helper accidentally calls `Guid.TryParse` (which would silently drop modded contracts that use a non-standard key shape).
- **`GetCommittedContractAcceptIds_LogsCount`** — log-assertion test on the non-empty Verbose line.
- **`GetCommittedContractAcceptIds_KeyShapeMatchesRecorder`** — write a synthetic event with `key = Guid.NewGuid().ToString()` (default "D" form, with hyphens, matching `GameStateRecorder.cs:413`). Assert the helper returns it; assert that `MilestoneStore.FindCommittedEvent(GameStateEventType.ContractAccepted, key)` round-trips. Guards against the `ToString("N")` bug that the design review flagged.

### 8.2 `StockUiOverlayControllerTests.cs` — pure logic

The controller has Unity dependencies, so we test only the pure-helper layer (mark building) directly. Wrap the existing predicate calls in `internal static BuildTechMarks(MilestoneStore source) → Dictionary<string, TechNodeOverlayMark>` so it's directly testable. **No `liveUt` parameter** — the helper's filter is `LastReplayedEventIndex + 1 .. Events.Count` intersected with `IsEventVisibleToCurrentTimeline`, never a UT cutoff (see §2 terminology, §3 invariant). A past-but-still-unreplayed event must be marked, exactly so it stays in lockstep with the click-block.

- **`BuildTechMarks_EmptyStore_EmptyDict`** — zero entries.
- **`BuildTechMarks_UnreplayedEvent_PopulatedWithUtAndRecordingId`** — single unreplayed event (any UT, including past-relative-to-liveUT) with a `recordingId` carries through to the mark. Fails if the helper accidentally introduces a UT cutoff (the bug a reviewer caught).
- **`BuildTechMarks_AlreadyReplayedEvent_NotIncluded`** — event is at or before `LastReplayedEventIndex` for its milestone. Excluded. Fails if the cursor is off-by-one.
- **`BuildTechMarks_HiddenByTimelineFilter_NotIncluded`** — event in a milestone hidden by `IsEventVisibleToCurrentTimeline`. Excluded. Fails if the helper bypasses the visibility filter.
- **`BuildTechMarks_SignatureHasNoLiveUtParameter`** — reflection assertion: `typeof(StockUiOverlayController).GetMethod("BuildTechMarks", BindingFlags.Static | BindingFlags.NonPublic).GetParameters()` does not contain a parameter named `liveUt` or of type `double`. **This guards directly against the regression** of someone reintroducing a UT cutoff — a behavioral test like "past unreplayed event still included" cannot fail if the helper has no UT input to filter on, so the only meaningful guard is at the signature level.
- **`BuildApplicantMarks_PrefersFutureHiredOverReserved`** — when both predicates fire, FutureHired wins (it carries a UT; Reserved does not).
- **`BuildContractMarks_AlreadyActive_SuppressedWithLog`** — current `ContractSystem.Instance.Contracts` contains the same Guid in `Active` state; the controller suppresses the overlay and logs.
- **`BuildContractMarks_LogsSuppressionCountAtVerbose`** — log-assertion test.

### 8.3 `ContractAcceptPatchTests.cs` and `KerbalHirePatchTests.cs`

`TechResearchPatchTests.cs` does NOT currently exist (verified). The test pattern is therefore new — established by Phase 2 of this PR for both new patches. Pattern:

- **Test sink:** `CommittedActionDialog.ShowBlocked` directly calls Unity's `PopupDialog.SpawnPopupDialog`, so it cannot be unit-tested. Instead, assert on the `ParsekLog.Info("CommittedAction", "Blocked action: …")` log line that `ShowBlocked` emits at the top of its body (see `Source/Parsek/CommittedActionDialog.cs:17-19`). This is the canonical project pattern for verifying patch behavior — see `RewindLoggingTests.cs` for the ParsekLog test-sink setup.
- **Optional small refactor (Phase 2):** if log-only assertions feel weak, introduce an injectable `internal static Action<string, string, string> TestHook` on `CommittedActionDialog` that the dialog invokes before falling through to `PopupDialog.SpawnPopupDialog`. Only do this if the log assertions miss something during Phase 2 implementation; do not add it speculatively.

One test per:

- Allows accept/hire when not committed (no log line, return value `true`).
- Blocks when committed; asserts a `ParsekLog.Info("CommittedAction", "Blocked action: ...")` line and a `ParsekLog.Info("ContractAcceptPatch"|"KerbalHirePatch", "blocking ...")` line (which the patches emit before calling `ShowBlocked`).
- Bypasses block when `GameStateRecorder.IsReplayingActions == true`; asserts the bypass Verbose log.
- **Invariant test (§3, scoped to v1's three action types with both sides):** the click-block predicate must use **exactly** the helper that the matching overlay's candidate-mark builder reads — `KerbalHirePatch` against `GetCommittedKerbalHireNames()` (NOT `IsManaged`, which is the union of ReservedActive + ReservedRetired and is overlay-only — including reserved-only kerbals in the click-block would break the §3 scope and prevent the player from re-hiring a Parsek-reserved kerbal that has no future-hire commitment). `ContractAcceptPatch` against `GetCommittedContractAcceptIds()`. Test: build a synthetic milestone containing a `CrewHired` and a separate `KerbalsModule` reservation for an unrelated name, assert the patch blocks the first and ignores the second.

### 8.4 Settings round-trip test

`ParsekSettingsRoundTripTests.cs` already exists; add two cases for the new boolean toggles.

### 8.5 Edge-case tests (matching §9)

One test per edge case where the case is unit-testable, named after the E-id. Tests live in `MilestoneStoreOverlayHelperTests.cs` or `StockUiOverlayControllerTests.cs` depending on the surface.

Edge cases that require a live Unity / KSP harness move to the in-game test list in §8.6 (or to manual verification in §10):

- **E5** (KSP API drift, `RDController.nodes` reflection failure) — manual verification: stub the reflection lookup to return null in a Phase 4 dev build, confirm the one-shot Warn fires and tech overlays simply do not draw. Not a unit test (would require mocking reflection).
- **E6** (mid-display panel rebuild) — covered by the in-game `OverlaysClearedOnDespawn` test plus the `OnTimelineDataChanged`-driven rebuild path (§4.3); no dedicated unit test.
- **E13** (KSP renames `KerbalRoster.HireApplicant`) — manual verification: temporarily rename the Harmony target in a dev build, confirm the patch logs a Warn and the overlay continues to draw (decoupled).
- **E14** (Flight-scene Astronaut Complex from a mod) — covered by the design (`KerbalRoster.HireApplicant` is scene-agnostic); manual verification only if a Flight-scene mod is installed.

All other E-cases (E1–E4, E7–E12, E15–E18) get a dedicated unit test.

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
| E3 | Player opens R&D before Parsek's `LedgerOrchestrator` finishes early-load seeding | First `OnRDTreeSpawn` finds an empty marks dict; no decoration. The first `OnTimelineDataChanged` after seeding triggers `RebuildAllVisible()` if the screen is still open. | Cold-start race; documented; no silent crash. |
| E4 | A committed-future tech node is unlocked by replaying its source recording while the R&D screen is open | `OnTimelineDataChanged` fires, `RebuildAllVisible()` strips the overlay on the next frame because the event's milestone has now advanced past `LastReplayedEventIndex`. | Overlay should disappear automatically. |
| E5 | `RDController.Instance.nodes` field is renamed in a KSP update | Reflection returns null; one-shot per-session Warn fires; no decorations on R&D for the rest of the session; no crash. Covered by manual verification (see §8.5). | We use reflection with a single-shot warn (mirrors `KspStatePatcher.protoTechNodes` pattern at `GameActions/KspStatePatcher.cs:439-474`). |
| E6 | Stock KSP UI rebuilds the panel mid-display (e.g. tier filter button) | Our overlay GameObjects are children of the panel rows. When KSP rebuilds, the children go with the parents. There is no per-frame poll; the next legitimate `OnTimelineDataChanged` (or the next building reopen) repaints. **Acceptable v1 limitation:** if no ledger event fires between filter-button click and the player closing the screen, the overlay stays missing on the surviving rows until reopen. Documented; revisit only if playtesting flags it. | Per-frame polling for visual repaint was rejected for cost reasons (§4.3). |
| E7 | Two committed-future events for the same key (e.g. tech researched twice in two recordings) | Mark uses the **earliest** UT; tooltip notes `(+N more committed)`. | Correct: the player will hit the earliest first. |
| E8 | The committed event references a recording that has since been deleted | `RecordingStore.GetById(rid)` returns null; tooltip degrades to `"Committed at UT 12345"` without the recording name. One Verbose log. | Defensive. |
| E9 | A contract whose Guid is committed-future-accepted is no longer in `ContractSystem.Instance.Contracts` (KSP regenerated the offered list) | Suppress the overlay with one Verbose log. | The player will not see this offer; nothing to decorate. |
| E10 | Player has the overlay setting off | Controller still subscribes to events but skips decoration; click-blocks remain (separate setting). | Decoupled toggles. |
| E11 | Click-blocks setting off | Patches no-op (return true) with a Verbose `"feature disabled by ParsekSettings"` log. | Lets the player accept the consequence consciously. |
| E12 | `ContractAcceptPatch` fires during an internal stock-KSP `Contract.Accept` triggered by another mod's contract auto-accept | If `IsReplayingActions == false` and the Guid is committed, we still block. The mod loses the auto-accept. **Documented limitation**; no v1 escape hatch. | Better than silently desyncing the ledger. Worst case: player toggles off click-blocks. |
| E13 | KSP renames `KerbalRoster.HireApplicant` (or `Contract.Accept`) between versions | `KerbalHirePatch.TargetMethod()` (or `[HarmonyPatch]` attribute) fails; Harmony skips the patch with one Warn (matches `CrewDialogFilterPatch.cs:32-37` pattern). Overlay still draws (decoupled from the patch). Click-block silently disabled until manual verification — see §8.5. | Defensive against KSP API drift. |
| E14 | Astronaut Complex is opened from inside Flight via a mod | The overlay does not draw (controller is `KSPAddon(GameScenes.SPACECENTER, false)`). **The click-block still fires** because `KerbalHirePatch` patches `KerbalRoster.HireApplicant`, which is scene-agnostic — confirmed by the same Harmony-patch model that `TechResearchPatch` and `FacilityUpgradePatch` use today. **Acceptable v1 limitation** for the visual; the safety property holds. | The decoupling between overlay (scene-bound) and click-block (scene-agnostic) is intentional. |
| E15 | Player accepts a contract live, then commits a recording that contains a `ContractAccepted` for the same Guid (rare, defensive) | Overlay suppresses (E9 path) when the contract is already in `Active` state. Click-block does not fire because the contract is no longer offered. Ledger walk handles the duplicate via `ContractsModule`'s existing accept-idempotence. | Triple-defended. |
| E16 | Overlay GameObjects accumulate after many open/close cycles (leak) | The `OverlaysClearedOnDespawn` in-game test guards against this. The despawn handler logs the strip count so a leak surfaces as `stripped=0 stripped=0 stripped=0` despite open cycles. | Observability over silent correctness. |
| E17 | A future-hired kerbal name appears in `CrewRoster.Crew` or `CrewRoster.Tourist` (because the future hire already played back, OR the same kerbal entered as a tourist via a rescue contract that the recording then "regularized" to Crew via a second hire event) | KSP guarantees `ProtoCrewMember.name` uniqueness within `CrewRoster` (verified §5.2 click-block note), so the future event and the live entry refer to the same kerbal by identity. Decorating the row with "Will be hired at UT 12345" is therefore truthful but redundant — the player has already done it (Crew) or is about to via a non-applicant path (Tourist). **v1 mitigation:** when classifying applicant marks, exclude any name already in `CrewRoster.Crew` OR `CrewRoster.Tourist` from `FutureHired`. One Verbose log per excluded name. The click-block is moot because `KerbalRoster.HireApplicant` is only called from the Applicants list, never Crew/Tourist. | Stock KSP's roster-name uniqueness guarantees the future event refers to the same kerbal identity; suppressing the redundant badge keeps the visual honest without changing safety. |
| E18 | Overlay candidate set drifts from click-block predicate set (e.g. someone adds a new helper-side filter to one without the other) | Failure mode: a row that should be decorated AND blocked is silently allowed (or vice versa). **Mitigation:** three pairwise unit tests in `StockUiOverlayControllerTests.cs`, one per **clickable** action type with both sides in v1 scope (tech / contract accept / kerbal hire — facility overlay is out of v1 scope per §1, so no overlay/click-block pair to test there). Each test asserts that the **candidate** layer of the overlay (before any UI-surface suppression) is driven by the same `MilestoneStore` helper that the click-block patch consults. Specifically: (a) `BuildTechMarks_Candidates(...)` returns a key set equal to `GetCommittedTechIds()` exactly; (b) `BuildContractMarks_Candidates(...)` returns a key set equal to `GetCommittedContractAcceptIds()` exactly; (c) `BuildApplicantMarks_Candidates(...)` filtered to `Kind == FutureHired` (only) yields a name set equal to `GetCommittedKerbalHireNames()` exactly. The implementation must factor mark-building into a `_Candidates` step that returns the raw helper-driven set, plus a `_Suppress` step that applies UI-surface filters (E9 already-Active suppression for contracts; E17 already-in-Crew/Tourist suppression for FutureHired). The final overlay set may legally be smaller than the candidate set; the click-block predicate matches the **candidate** set, never the suppressed set, because the click-block patches the API entry point and does not see UI-surface state. The overlay-only kinds (`ReservedActive`, `ReservedRetired`, `FutureRetired`) are excluded from this test entirely — they have no click-block, by design (§3 invariant scope). Future filters added to either layer must update the matching pairwise test or extend the suppression layer, not both. | Tests the actual contract — same source predicate — without conflating it with E9/E17 UI-surface suppressions or with overlay-only telemetry kinds. |

---

## 10. Out of scope for v1 (explicit)

- **Flight-scene overlays.** The Astronaut Complex and Mission Control are SPACECENTER-only screens by KSP convention; mods that surface them in Flight are not supported in v1.
- **Per-user-dismissible overlays.** No "dismiss this badge until next session" affordance.
- **Non-stock screens.** ContractConfigurator's own Mission Control replacement, KCT's build queue, etc. are out of scope. Other mods can subscribe to the same `MilestoneStore` helpers if they want parity.
- **Per-contract / per-kerbal "claim" UI.** No way to tell Parsek "actually I want to override the timeline and accept this anyway" — the current escape hatch is the master setting toggle.
- **Future-completed / future-failed contract overlays.** v1 only flags FutureAccepted. The other two states are valuable but the data shape is identical and they can be added in a small follow-up once v1 is shipped and the overlay infrastructure is proven.
- **KSC facility upgrade overlay.** The existing `FacilityUpgradePatch` already prevents the player from wasting funds on a double-upgrade, so the safety property holds without a visual badge. The KSC top-down view's per-facility hook is also less established than the three building screens, so a clean overlay implementation is its own design pass — defer to v2. (Note: `FacilityUpgradePatch` is still in scope of this PR for the "click-blocks setting" gating in Phase 3.)
- **Strategy activate overlays in the Administration building.** Same shape; defer to a follow-up. Strategies are less commonly recorded than contracts/kerbals/tech in current playtest data.
- **Tooltip styling polish.** v1 uses stock `GUI.skin.box` + `GUIContent` tooltip; KSP has a fancier tooltip system (`KSP.UI.TooltipTypes.TooltipController_Text`). Use the fancier one in a follow-up if it is worth the dependency surface.

---

## 11. Files to touch (final inventory)

**New:**
- `Source/Parsek/StockUiOverlayController.cs` — controller MonoBehaviour + `KSPAddon(GameScenes.SPACECENTER, false)`.
- `Source/Parsek/OverlayBadge.cs` — small shared MonoBehaviour for the badge visual.
- `Source/Parsek/Patches/ContractAcceptPatch.cs` — Harmony prefix on `Contracts.Contract.Accept()`.
- `Source/Parsek/Patches/KerbalHirePatch.cs` — Harmony prefix on `KerbalRoster.HireApplicant(ProtoCrewMember)` (verified entry point — see §5.2).
- `Source/Parsek.Tests/MilestoneStoreOverlayHelperTests.cs` — coverage for the new `MilestoneStore` helpers.
- `Source/Parsek.Tests/StockUiOverlayControllerTests.cs` — pure-helper coverage.
- `Source/Parsek.Tests/ContractAcceptPatchTests.cs`.
- `Source/Parsek.Tests/KerbalHirePatchTests.cs`.

**Modified:**
- `Source/Parsek/MilestoneStore.cs` — three new query helpers (§4.1). File at top of `Source/Parsek/`, not under `GameActions/`.
- `Source/Parsek/KerbalsModule.cs` — new `GetReservationKind(string)` helper (§5.2 step 2; see Phase 1.5 in §12). File at top of `Source/Parsek/`.
- `Source/Parsek/Patches/TechResearchPatch.cs` — gate on the new "click-blocks" setting.
- `Source/Parsek/Patches/FacilityUpgradePatch.cs` — gate on the new "click-blocks" setting.
- `Source/Parsek/UI/SettingsWindowUI.cs` — two new checkboxes.
- `Source/Parsek/ParsekSettings.cs` + `Source/Parsek/ParsekSettingsPersistence.cs` — two new boolean fields with default `true`, round-tripped.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — five new `[InGameTest]` methods.
- `Source/Parsek.Tests/ParsekSettingsRoundTripTests.cs` — two new round-trip cases.
- `docs/dev/todo-and-known-bugs.md` — new entry for the feature; mark related items as covered.
- `CHANGELOG.md` — `Unreleased` entry, one line per project convention: "Stock R&D, Astronaut Complex, and Mission Control screens now flag and block actions that pending recordings already committed."

**File path notes** (response to review feedback that flagged path mistakes earlier in this doc):
- `GameStateRecorder.cs`, `MilestoneStore.cs`, `KerbalsModule.cs`, `GameStateEvent.cs` all live at `Source/Parsek/<file>.cs` (top of tree).
- `GameStateEventConverter.cs` and `KspStatePatcher.cs` live under `Source/Parsek/GameActions/`.
- All Harmony patches live under `Source/Parsek/Patches/`.

**Not touched** (to reiterate): `Ledger`, `RecalculationEngine`, all eight resource modules (the new `MilestoneStore` helpers do not change module surface), `KspStatePatcher`, `CrewDialogFilterPatch`, save format, sidecars, the Parsek Career window.

---

## 12. Implementation phases

Sequential on this branch (no isolation worktrees — the changes are tightly coupled and we want one PR for the feature). Each phase ends with `dotnet build && dotnet test` green before the next starts.

**Phase 1 — Data layer (small, self-contained).**
- Add the three `MilestoneStore` helpers (§4.1): `GetCommittedContractAcceptIds`, `GetCommittedKerbalHireNames`, `GetCommittedKerbalRetireNames`. The existing `GetCommittedTechIds` / `GetCommittedFacilityUpgrades` helpers are reused as-is — no changes needed.
- Add the unit tests in §8.1.
- One commit.

**Phase 1.5 — Kerbals helper for unambiguous reservation classification.**
- Add `KerbalsModule.GetReservationKind(string name) → enum {NotManaged, ReservedActive, ReservedRetired}`. Sized small enough to land before the patches; needed by Phase 4's overlay classification (§5.2 step 2). The need is described in §5.2 — `IsManaged` + `GetActiveOccupant != null` gives a wrong split because `GetActiveOccupant` returns null in two distinct cases.
- Add unit tests covering both null cases of `GetActiveOccupant` to lock the new helper's classification.
- One commit.

**Phase 2 — Click-blocks for contracts and kerbals.**
- Add `ContractAcceptPatch.cs` and `KerbalHirePatch.cs` (the `KerbalHirePatch.TargetMethod()` mirror of `CrewDialogFilterPatch.TargetMethod()` is the model).
- Wire them through `ParsekHarmony.Awake()` with the existing try/catch pattern.
- Add the patch tests in §8.3.
- **Do NOT** gate the new patches on the click-block setting yet — the setting field does not exist until Phase 3. Drop a `// TODO Phase 3: gate on ParsekSettings.BlockCommittedActions` next to each patch's entry guard so Phase 3 has an obvious anchor. The existing `TechResearchPatch` / `FacilityUpgradePatch` likewise stay un-gated until Phase 3.
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

None of these block implementation; defaults are listed for each.

**Resolved during review:**
- `GameStateEventType.CrewHired` (8) and `CrewRemoved` (9) already exist in `Source/Parsek/GameStateEvent.cs:17-18`. Only `CrewHired` round-trips through the converter; `CrewRemoved` reads directly from `MilestoneStore` (see §4.1 verified note).
- Mod auto-accept of contracts: handled by patching `Contracts.Contract.Accept()` directly, which is the funnel both stock UI and modded auto-accept go through. Behavior intentional (E12); no per-mod escape hatch in v1 — toggle off the master click-block setting if needed.
- Flight-scene Astronaut Complex (modded only): handled by E14 — `KerbalHirePatch` patches `KerbalRoster.HireApplicant` which is scene-agnostic. No Flight-scene `KSPAddon` needed in v1; the overlay decoration just does not draw in Flight (the click-block still fires).

---

## 14. Next step

Assuming this design passes user review, the implementation cycle per `docs/dev/development-workflow.md` Step 4 is:

- **4a (Plan):** the six phases in §12 are the plan; no separate Plan agent needed.
- **4c (Implement):** phases 1–5 run sequentially on this branch; no isolation worktrees.
- **4d (Review):** clean-context review agent at the end of phase 5 reviews the whole feature against this doc before phase 6 (PR creation).

Ready to start phase 1 on approval.
