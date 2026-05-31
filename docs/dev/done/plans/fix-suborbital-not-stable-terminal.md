# Plan: SubOrbital is not a stable terminal for the Re-Fly auto-seal contract

## Problem

The Re-Fly merge auto-seal contract currently treats `TerminalState.SubOrbital` as a "stable terminal" alongside Orbiting / Landed / Splashed. When a Re-Fly recording ends with the active vessel in a suborbital arc on the player-chosen slot, the merge code closes (seals) the slot — preventing further Re-Flies — even though the vessel is, by definition, still in flight and on track to either crash, land, splash, or (with a burn) achieve orbit.

The user observed two symptoms from a single Re-Fly attempt:

1. The merge dialog's auto-seal preview surfaced "reached a sub-orbital arc" and warned the user the slot would be sealed.
2. After clicking Merge, the slot stayed open in the timeline — i.e. the recording was NOT actually sealed by the production classifier.

The user wants both ends of this fixed in a single change: the preview should not claim "will seal" on a sub-orbital arc, AND the production seal path should not seal on a sub-orbital arc. The behavioral contract should be: a sub-orbital arc is "still in flight", auto-seal waits until the arc resolves into a real terminal (Destroyed / Landed / Splashed / Orbiting).

## Why SubOrbital is not a stable terminal in this codebase

[RecordingTree.cs:1031-1055](../../Source/Parsek/RecordingTree.cs:1031) — `DetermineTerminalState(int situation)` buckets THREE KSP situations into `TerminalState.SubOrbital`:

```csharp
case 16: // SUB_ORBITAL
case 8:  // FLYING
case 64: // ESCAPING
    return TerminalState.SubOrbital;
```

[RecordingTree.cs:1140-1182](../../Source/Parsek/RecordingTree.cs:1140) — `DetermineTerminalStateFromOrbitEvidence` actively *downgrades* an ORBITING situation to `SubOrbital` when the orbit is bound but its periapsis lies inside the atmosphere or the surface. The decompile comment is unambiguous: "Atmospheric grazers… are excluded because they decay to destruction within a few orbits via drag."

[ParsekFlight.cs:14917-14947](../../Source/Parsek/ParsekFlight.cs:14917) — `InferTerminalStateFromTrajectory` *defaults* to `TerminalState.SubOrbital` when there is no Landed / Orbiting signal in the trajectory. The XML doc on the method explicitly says: "Returns SubOrbital as a safe default if no trajectory data is available." It is a "still in flight" fallback, not a conclusion.

[TerminalKindClassifier.cs:34-54](../../Source/Parsek/TerminalKindClassifier.cs:34) — `Classify(Recording)` already routes `TerminalState.SubOrbital` to `TerminalKind.InFlight`, NOT to `TerminalKind.Landed`. That classifier's job is to pick MergeState.Immutable vs. MergeState.CommittedProvisional after a Re-Fly merge; it correctly says SubOrbital is "still re-flyable" (CommittedProvisional). The slot-close contract disagrees, which is the inconsistency this plan fixes.

In every reading of the codebase, `TerminalState.SubOrbital` means one of:
- ESCAPING / FLYING / SUB_ORBITAL — still in flight.
- ORBITING with periapsis inside atmosphere — will decay.
- Trajectory had no clear Landed / Orbiting signal — couldn't be classified.

It is never "the player finished this run." That makes the seal contract the outlier, not the other way around.

## Current code paths that auto-seal on SubOrbital

### 1. Re-Fly merge focus override (production)

[UnfinishedFlightClassifier.cs:351-363](../../Source/Parsek/UnfinishedFlightClassifier.cs:351) — `IsReFlyOverrideStableTerminal` lists `SubOrbital`:

```csharp
private static bool IsReFlyOverrideStableTerminal(TerminalState terminal)
{
    switch (terminal)
    {
        case TerminalState.Orbiting:
        case TerminalState.SubOrbital:   // <-- problem
        case TerminalState.Landed:
        case TerminalState.Splashed:
            return true;
        ...
    }
}
```

[UnfinishedFlightClassifier.cs:240-257](../../Source/Parsek/UnfinishedFlightClassifier.cs:240) — the Re-Fly merge focus override path: when `focusSlotOverride.HasValue` (always set by the merge call site to `slotListIndex`, see [SupersedeCommit.cs:1004-1007](../../Source/Parsek/SupersedeCommit.cs:1004)) AND the terminal is in the "stable" list AND the override slot matches the player's chosen slot, the classifier returns `false` with `reason = "stableTerminalFocusSlot"`. False means "not an Unfinished Flight" — i.e. the slot should be sealed.

[SupersedeCommit.cs:1501-1534](../../Source/Parsek/SupersedeCommit.cs:1501) — `ShouldAutoSealReFlySlotAfterMerge` then maps `stableTerminalFocusSlot` to `true`:

```csharp
if (string.Equals(closeReason.Detail, "stableTerminalFocusSlot",
        StringComparison.Ordinal))
    return true;
```

### 2. Non-Re-Fly static focus path (production)

[UnfinishedFlightClassifier.cs:275-282](../../Source/Parsek/UnfinishedFlightClassifier.cs:275) — `Orbiting`/`SubOrbital` are the only terminals that fall through to the focus-slot check:

```csharp
if (terminal.Value != TerminalState.Orbiting
    && terminal.Value != TerminalState.SubOrbital)
{
    reason = "stableTerminal";
    ...
    return false;
}
```

[UnfinishedFlightClassifier.cs:309-317](../../Source/Parsek/UnfinishedFlightClassifier.cs:309) — when the resolved slot equals `rp.FocusSlotIndex`, returns `false` with the same `stableTerminalFocusSlot` reason — so the static-focus path also seals on SubOrbital.

### 3. Auto-seal preview dialog copy

[ReFlyAutoSealPreview.cs:27-43](../../Source/Parsek/ReFlyAutoSealPreview.cs:27) — declares the `SubOrbitalArc` reason; the comment in the enum lists `stableTerminalFocusSlot` as the production path the preview is mirroring.

[ReFlyAutoSealPreview.cs:318-320](../../Source/Parsek/ReFlyAutoSealPreview.cs:318) — adds `SubOrbitalArc` whenever the live active vessel reports `Vessel.Situations.SUB_ORBITAL` at dialog spawn time.

[ReFlyAutoSealPreview.cs:336-339](../../Source/Parsek/ReFlyAutoSealPreview.cs:336) — when the live vessel reports `Vessel.Situations.ORBITING` but the orbit is not stable above atmosphere (via `RecordingTree.IsBoundOrbitAboveAtmosphere`), falls back to adding `SubOrbitalArc` — and still claims auto-seal. This is the same wrong contract: an orbit-with-periapsis-in-atmosphere is not a conclusion, it is a decay.

[ReFlyAutoSealPreview.cs:362-364](../../Source/Parsek/ReFlyAutoSealPreview.cs:362) — adds `SubOrbitalArc` whenever the recording's recorded terminal is `TerminalState.SubOrbital`, with no slot-or-focus check.

### 4. Slot-aware classification gate

[SupersedeCommit.cs:2057-2068](../../Source/Parsek/SupersedeCommit.cs:2057) — `RequiresSlotAwareMergeClassification` lists `SubOrbital` alongside `Orbiting`:

```csharp
if (terminal.Value == TerminalState.Orbiting
    || terminal.Value == TerminalState.SubOrbital)
    return true;
```

When this returns true and `TryResolveSlotForMergeClassification` fails, [SupersedeCommit.cs:1039-1054](../../Source/Parsek/SupersedeCommit.cs:1039) THROWS instead of falling back to the v0.9 TerminalKindClassifier. This is a hard precondition for the auto-seal contract — "if the terminal says stable, we must know which slot to seal." Once SubOrbital is removed from the seal contract this gate must follow, otherwise SubOrbital terminals would crash the merge whenever slot lookup happens to fail (a long tail of edge cases that today's contract papers over by always sealing).

## Why the user saw "preview said seal, slot stayed open"

The preview and the production classifier do not read the same inputs:

- **Preview** ([ReFlyAutoSealPreview.cs:344-387](../../Source/Parsek/ReFlyAutoSealPreview.cs:344) — `CollectRecordedTerminalReasons`) walks ONLY `liveProvisional.TerminalStateValue`. It does not check the rewind-point slot, the slot index match, or whether `TryResolveSlotForMergeClassification` will succeed. If the recording's terminal is `SubOrbital` at dialog spawn, the preview unconditionally adds `SubOrbitalArc` and reports `WillAutoSeal = true`.

- **Production** ([SupersedeCommit.ClassifyMergeStateOrThrow:963-1073](../../Source/Parsek/SupersedeCommit.cs:963)) is much narrower. It requires:
  1. `TryResolveSlotForMergeClassification` to succeed (returns rp + slotListIndex).
  2. `UnfinishedFlightClassifier.TryQualify` to return `false` with the specific reason `stableTerminalFocusSlot`.
  3. `ShouldAutoSealReFlySlotAfterMerge` to then map that reason to true.

There are at least three production-only paths the preview cannot see, any of which silently produces "no seal" while the preview still claims seal:

- **`TryResolveSlotForMergeClassification` fails.** The marker's branch-link lookup may not match any RP/slot (debris carve-outs, optimized-survivor edge cases, stale supersede tables). The throw at [SupersedeCommit.cs:1039-1054](../../Source/Parsek/SupersedeCommit.cs:1039) is gated on `RequiresSlotAwareMergeClassification && !IsInPlaceContinuation`. If `IsInPlaceContinuation(marker, provisional)` returns true, the code logs a fallback message and uses the v0.9 `TerminalKindClassifier.Classify` — which routes SubOrbital to `InFlight`, leaving `AutoSealSlot=false`, MergeState=Immutable.

- **Terminal flips between dialog and merge finalize.** [ParsekFlight.cs:14635-14641](../../Source/Parsek/ParsekFlight.cs:14635) re-sets `rec.TerminalStateValue = DetermineTerminalState(v.situation, v)` during `FinalizeIndividualRecording` if the recording's terminal was not already set. If the recording's terminal happened to be unset at finalize but set to `SubOrbital` by the live-vessel `CollectLiveVesselReasons` path at preview spawn (the live vessel was SUB_ORBITAL but the recording terminal had not yet been stamped), and the situation changes by the time finalize runs (vessel touched down briefly into FLYING, hit ground → Destroyed), the recording's terminal ends up different from what the preview saw. In particular, `IsTerminalFailureReFlyOutcome(rec)` returns true for `Destroyed` at [SupersedeCommit.cs:1075-1084](../../Source/Parsek/SupersedeCommit.cs:1075), routing through `ShouldKeepReFlySlotOpenAfterMerge → return true` and skipping the seal entirely.

- **Merge is interrupted.** [MergeDialog.TryCommitReFlySupersede:2627-2640](../../Source/Parsek/MergeDialog.cs:2627) catches orchestrator exceptions and returns `Interrupted`; the user sees "Merge interrupted — will finish on next load" but production never reaches the seal call. The preview's "will seal" copy is misleading in that window.

The point of this plan is NOT to make the preview and production agree by tightening the preview — it is to remove the wrong-contract premise from BOTH so they agree on "SubOrbital does not seal." Once SubOrbital is no longer a sealing terminal, the preview reports no-seal and production reports no-seal, regardless of which of the three production gates the player happened to hit.

## Fix scope

Four production touch points + four test files. No schema or serialization change.

### Production

1. **[UnfinishedFlightClassifier.cs:351-363](../../Source/Parsek/UnfinishedFlightClassifier.cs:351)** — drop `TerminalState.SubOrbital` from `IsReFlyOverrideStableTerminal`. Update the XML doc above it to say SubOrbital falls through to the non-focus Orbiting/SubOrbital `stableLeafUnconcluded` branch.

2. **[UnfinishedFlightClassifier.cs:275-340](../../Source/Parsek/UnfinishedFlightClassifier.cs:275)** — the non-Re-Fly static-focus branch. The change here is subtle: the `if (terminal.Value != Orbiting && terminal.Value != SubOrbital) return false` gate at 275-282 stays as-is (so SubOrbital still falls through to the focus-slot logic), but the `slotListIndex == rp.FocusSlotIndex` branch at 309-317 must NOT return `stableTerminalFocusSlot` for `SubOrbital`. The path becomes:
   - Orbiting + slot == FocusSlot → still seals as `stableTerminalFocusSlot`.
   - SubOrbital + slot == FocusSlot → returns `true` with `stableLeafUnconcluded` (same as the non-focus path at 319-332).
   - SubOrbital + slot != FocusSlot → returns `true` with `stableLeafUnconcluded` (unchanged).

3. **[ReFlyAutoSealPreview.cs:27-43, 318-320, 336-339, 362-364](../../Source/Parsek/ReFlyAutoSealPreview.cs:27)** — drop the `SubOrbitalArc` enum member and every site that adds or maps it:
    - Enum declaration at line 42.
    - `CollectLiveVesselReasons` SUB_ORBITAL case at lines 318-320 (delete).
    - `CollectLiveVesselReasons` ORBITING branch at lines 336-339 — change to "add `StableOrbit` only when `stable==true`; otherwise add nothing." `IsBoundOrbitAboveAtmosphere` is still called for the diagnostic, but the `false` branch no longer maps to `SubOrbitalArc`.
    - `CollectRecordedTerminalReasons` SubOrbital case at lines 362-364 (delete).
    - **`PhraseFor` switch at line 121** (`case ReFlyAutoSealReason.SubOrbitalArc: return "reached a sub-orbital arc";`) — delete.
    - **`GroupOrdinal` switch at line 442** (`case ReFlyAutoSealReason.SubOrbitalArc: return 340;`) — delete.

4. **[SupersedeCommit.cs:2057-2068](../../Source/Parsek/SupersedeCommit.cs:2057)** — drop `TerminalState.SubOrbital` from `RequiresSlotAwareMergeClassification`. SubOrbital is no longer a seal-trigger, so it no longer requires the strict slot-lookup precondition that's gated by this method. EVA-stranded (terminalRec.EvaCrewName non-empty + not Boarded) is still seal-relevant via `strandedEva` → kept; Orbiting still seal-relevant → kept.

### What to keep (explicit callouts so a future reader does not over-prune)

- **[UnfinishedFlightClassifier.cs:802-811](../../Source/Parsek/UnfinishedFlightClassifier.cs:802) `StashedTerminalQualifies` keeps `TerminalState.SubOrbital`.** Manual Stash + Seal is the ONLY remaining path to seal a SubOrbital recording under the new contract, and that is intentional: the player explicitly chose to stash and end the engagement. Without this, the Unfinished Flights table would have no way to surface a SubOrbital row to the player at all.
- **[UnfinishedFlightClassifier.cs:319-332](../../Source/Parsek/UnfinishedFlightClassifier.cs:319) `stableLeafUnconcluded` branch keeps `SubOrbital`.** SubOrbital + non-focus slot is already an Unfinished Flight today; the fix just extends that same verdict to SubOrbital + focus slot (point 2 above).
- **[RecordingEndpointResolver.cs](../../Source/Parsek/RecordingEndpointResolver.cs) / [GhostMapPresence.cs](../../Source/Parsek/GhostMapPresence.cs) endpoint-eligibility lists keep `SubOrbital`.** Those are ghost-map and orbital-endpoint decisions, not seal decisions. Untouched.
- **[UI/RecordingsTableFormatters.cs:101-102](../../Source/Parsek/UI/RecordingsTableFormatters.cs:101) / [Timeline/TimelineEntryDisplay.cs:234](../../Source/Parsek/Timeline/TimelineEntryDisplay.cs:234) display strings keep `"Sub-orbital"`.** Pure UI labels, contract-neutral.

### Tests — add new ones (existing `UnfinishedFlightClassifierTests.cs` has no SubOrbital coverage)

5. **`Source/Parsek.Tests/UnfinishedFlightClassifierTests.cs`** — ADD (not flip; grep confirms zero SubOrbital references today):
   - `SubOrbitalNonFocusSlot_WithFocusOverride_FallsThroughToStableLeafUnconcluded` — under the new contract the override gate no longer fires for SubOrbital; the test pins that the call returns `qualifies=true` with reason `stableLeafUnconcluded`, NOT `qualifies=false` with `stableTerminalFocusSlot`. Mirror the shape of the existing `OrbitingNonFocusSlot_WithFocusOverride_ReturnsStableTerminalFocusSlot` (line 141).
   - `SubOrbitalFocusSlot_StaticFocusPath_ReturnsStableLeafUnconcluded` — non-Re-Fly static focus path: SubOrbital + `slotListIndex == rp.FocusSlotIndex` now returns true with `stableLeafUnconcluded`. Mirror `OrbitingFocusSlot_StaticFocusPathUnchangedByOverride` (line 197).
   - The `OrbitingFocusSlot_*` tests stay unchanged: Orbiting still seals as `stableTerminalFocusSlot`.

6. **`Source/Parsek.Tests/UnfinishedFlightsMembershipTests.cs`** — ADD:
   - `SubOrbitalFocusSlotUnderPostFeatureRP_IsMember` — the actual sibling pair is `OrbitingNonFocusUnderPostFeatureRP_IsMember` (line 604, IS member) and `OrbitingFocusSlotUnderPostFeatureRP_NotMember` (line 621, NOT member). The new SubOrbital case mirrors the focus-slot test but flips the outcome: under the new contract, SubOrbital + focus slot is a UF member, contrasting with Orbiting + focus slot which is not.
   - Existing `SubOrbitalNonFocusUnderPostFeatureRP_IsMember` (line 730) keeps passing unchanged: the non-focus path verdict does not change.

7. **`Source/Parsek.Tests/SupersedeCommitTests.cs`** — ADD:
   - `SubOrbitalStableLeaf_SlotLookupFailure_DoesNotThrow_FallsBackToInFlight` — sibling of the existing Orbiting throw tests. Construct a SubOrbital provisional with an unresolvable RP/slot, confirm `ClassifyMergeStateOrThrow` no longer throws (because `RequiresSlotAwareMergeClassification(SubOrbital)==false` after the fix), and the v0.9 `TerminalKindClassifier` fallback path yields `NewState == MergeState.Immutable + AutoSealSlot == false` (the fallback's `Immutable` is fine: the recording is real, the slot stays open).
   - Stale comment edit at lines 459-461: replace `"Landed/Splashed/Orbiting/SubOrbital on the player-chosen slot all seal"` with `"Landed/Splashed/Orbiting on the player-chosen slot seal; SubOrbital does not (still in flight)"`.
   - The existing Orbiting throw tests stay unchanged: Orbiting still requires the slot-aware precondition.

8. **`Source/Parsek.Tests/ReFlyAutoSealPreviewTests.cs`** — UPDATE by name:
   - `Preview_RecordedTerminalSubOrbital_NullVessel_FlagsSubOrbitalArc` (line 390-401): flip to assert `!WillAutoSeal` and `Reasons` is empty, or rename to `_FlagsNoSeal` and assert no reasons.
   - `Phrase_AllReasons_MatchSpec` (line 524-546): drop the `SubOrbitalArc` row.
   - ADD `Preview_LiveVesselSubOrbital_NoReasonAdded`: pin the `CollectLiveVesselReasons` SUB_ORBITAL branch removal.
   - ADD `Preview_LiveVesselOrbitingWithPeRInAtmo_NoReasonAdded`: pin the ORBITING-fallback branch at 336-339 (`IsBoundOrbitAboveAtmosphere` still runs for diagnostics, but `stable==false` no longer maps to `SubOrbitalArc`).

9. **[InGameTests/MergeNonFocusReFlyToOrbitImmutableTest.cs:76-77](../../Source/Parsek/InGameTests/MergeNonFocusReFlyToOrbitImmutableTest.cs:76)** — tighten the "orbit-class outcome" check to require `TerminalState.Orbiting` only. Today the test treats SubOrbital and Orbiting equivalently as a passing outcome and asserts `MergeState.Immutable`. After the fix, SubOrbital → `CommittedProvisional` (slot kept open), which would fail the Immutable assertion. The test's stated intent is "stable orbit reached"; Orbiting only.

10. **[InGameTests/StableLeafUnfinishedFlightsRuntimeTest.cs](../../Source/Parsek/InGameTests/StableLeafUnfinishedFlightsRuntimeTest.cs:51)** — the "SubOrbital Probe" stable-leaf row (lines 50-51, 132, 200-201) pins SubOrbital **as a non-focus Unfinished Flight member** and as seal-able through `UnfinishedFlightSealHandler.TrySeal`. Already works under the new contract; no edit needed. Listed here so the implementor double-confirms before assuming.

10b. **[InGameTests/MergeReFlyStructuralMutationAutoSealsTest.cs:7-21](../../Source/Parsek/InGameTests/MergeReFlyStructuralMutationAutoSealsTest.cs:7)** — XML doc says "the focus override path closes the slot via `stableTerminalFocusSlot` before the structural gate runs, so the structural gate is now a defensive backstop rather than the primary seal trigger." Under the new contract this is still true for Orbiting/Landed/Splashed terminals (the test's stated coverage), but no longer true for SubOrbital: a SubOrbital re-fly with a structural BP now seals via `structuralMutation:*`, NOT via `stableTerminalFocusSlot`. Add a one-line clarification: "For SubOrbital chain tips the structural gate is the primary (and only) seal trigger; the override path no longer fires for SubOrbital." Test assertions stay unchanged (the `or` between override and structural already covers both paths).

10c. **NEW in-game test `MergeReFlyToSubOrbitalKeepsSlotOpenTest`** under `Source/Parsek/InGameTests/`: end-to-end coverage that pins the user's observed scenario:
   1. Arm a Re-Fly session.
   2. Drive the active vessel to a `Vessel.Situations.SUB_ORBITAL` situation.
   3. Confirm `ReFlyAutoSealPreviewer.Preview` returns `WillAutoSeal == false`.
   4. Run `MergeJournalOrchestrator.RunMerge` (or the production code path that wraps it).
   5. Assert: slot stays open (`slot.Sealed == false`), recording committed as `MergeState.CommittedProvisional`, `ActiveReFlySessionMarker == null` after orchestrator success.
   This is the only test in the plan that pins the FULL chain (preview + production + post-merge state) end-to-end.

### Doc updates (per CLAUDE.md per-commit rule)

11. **`CHANGELOG.md`** under `## 0.9.3` → `### Bug Fixes`. Draft (compliant: no em dashes, two sentences, user-facing):

    > Re-Fly: a suborbital arc no longer seals the rewind slot or shows "reached a sub-orbital arc" in the merge dialog. The slot now stays open until the vessel actually lands, splashes, crashes, or reaches a stable orbit.

12. **`docs/dev/todo-and-known-bugs.md`** — new `## Open - v0.9.3 ...` entry inserted between the existing Done items (line 15 `Scene-exit finalizer leaves sub-orbital recordings stale...`) and the next Open entry. Flips to `## Done` on PR merge. Per CLAUDE.md's follow-up commit trap warning, re-read the existing wording before editing on any follow-up commit so the doc stays in lockstep with the actual change.

13. **`docs/parsek-rewind-to-separation-design.md`** — this is the AUTHORITATIVE seal-contract design doc and is the load-bearing update. SubOrbital is referenced as a stable terminal / sealable / stashable terminal at lines 44, 47, 168, 174, 410, 1138, 1155-1156, 2132, 2138, 2166 (plus the prose at lines 1545-1551, 1657, 1666, 1751, 1864, 1872, 1874 that describe the post-feature predicate). The Stash references (lines 47, 174, 2138) and Stash matrix (line 2138) STAY: manual Stash + Seal is the only remaining seal path for SubOrbital and SubOrbital must still be in the spawnable-stable-terminal stash set. The auto-seal references (44, 168, 410, 1155-1156, 1545-1551, 1657, 1666, 1751, etc.) need editing: remove SubOrbital from "stable terminal that seals on the player-chosen slot" lists, add a short paragraph stating that SubOrbital is not a conclusive outcome (the vessel will crash, land, splash, or with a burn reach orbit) and so does not auto-seal even on the focus slot. The "Vacuum-arc SubOrbital" pseudocode at line 1155 changes from "is focus → seal" to "is in-flight → stays open."

14. **`docs/dev/done/plans/refly-autoseal-dialog-copy.md`** — stale sibling plan doc references `SubOrbitalArc` and a "decaying orbit" example as canonical at lines 56 (the long row with `Landed / Splashed / Orbiting / SubOrbital` as the unified `stableTerminalFocusSlot` set), 119, 208 (the prose mentioning "Landed, Splashed, Orbiting, SubOrbital"), 222, 225, 259, 277, 451, 454. Either prepend a "superseded by `fix-suborbital-not-stable-terminal.md`" banner at the top, or strike the `SubOrbitalArc` rows / decaying-orbit examples and update the unified-seal-set rows so they list `Landed / Splashed / Orbiting` only.

14. **XML doc edits — be precise:**
    - [UnfinishedFlightClassifier.cs:342-350](../../Source/Parsek/UnfinishedFlightClassifier.cs:342) `<summary>` for `IsReFlyOverrideStableTerminal` — remove the SubOrbital mention.
    - [ReFlyAutoSealPreview.cs:198-211](../../Source/Parsek/ReFlyAutoSealPreview.cs:198) `<summary>` for `CollectRecordedTerminalReasons` — drop the SubOrbital case from the prose.
    - [ReFlyAutoSealPreview.cs:42](../../Source/Parsek/ReFlyAutoSealPreview.cs:42) enum-member comment goes away with the enum value.

## Out of scope

- Re-tuning the Orbiting seal contract. Orbiting-with-PeR-above-atmosphere remains a stable terminal that auto-seals; that part of the contract is correct.
- Changing `TerminalKindClassifier.Classify` — it already says SubOrbital is `InFlight`, which the merge code uses to keep `MergeState.CommittedProvisional`. The new contract aligns the slot-close decision with the existing MergeState decision; no MergeState math changes.
- The "merge interrupted" path. If the orchestrator throws, the journal will finish the merge on next load — that path is unrelated to the seal contract.
- The pre-existing `noTerminal` reason. A recording that reaches the merge with no terminal at all is a different bug (probably finalize ordering); not in this scope.

## Risk

- **The four production touch points all live on the seal hot path.** Re-Fly merge is the central rewind/seal seam. Any miss here regresses the slot-close behavior for ALL stable terminals (Orbiting / Landed / Splashed), not just SubOrbital. The fix is small but the surrounding code is tightly interlocked; need tests covering all five stable terminals through both Re-Fly override and non-Re-Fly static focus paths to catch any accidental over-narrowing.
- **`RequiresSlotAwareMergeClassification` removal could expose a latent slot-lookup-failure path.** Today SubOrbital + lookup-failure throws; after this change it would silently use the v0.9 `TerminalKindClassifier` fallback (SubOrbital → InFlight → MergeState.Immutable, no seal). That's the desired behavior under the new contract, but worth verifying with a synthetic SubOrbital+lookup-failure test.
- **The preview's `SubOrbitalArc` removal is purely cosmetic** (dialog copy). No serialized field, no save/load impact.

### Important nuance the verification must surface

A SubOrbital Re-Fly that hits the slot-lookup-failure path (lookup returns `false` OR returns an invalid slot index AND `IsInPlaceContinuation` is true) now falls through to the v0.9 `TerminalKindClassifier` instead of throwing. The result is `MergeState = Immutable + AutoSealSlot = false + slot stays open`. This is the desired outcome: the recording is immutably committed (player did a real flight that happened to end mid-suborbital), but the slot stays Re-Flyable. Make sure the new `SubOrbitalStableLeaf_SlotLookupFailure_DoesNotThrow_FallsBackToInFlight` test (§7) pins all three of: no throw, `Immutable`, `AutoSealSlot == false`.

## Verification

- `dotnet test` — all green.
- In-game Re-Fly playtest: launch, rewind to staging, fly to suborbital arc on the rewind slot, click Merge from the dialog. Expect:
  - Dialog should NOT mention "reached a sub-orbital arc."
  - After Merge, the timeline slot should remain open (R / Re-Fly button still enabled on the slot).
  - Recording should commit as `MergeState.CommittedProvisional` (post-fix: classifier returns `stableLeafUnconcluded` with `qualifies=true`, `ShouldKeepReFlySlotOpenAfterMerge` returns true via `IsSafeStableRetryClassifierReason("stableLeafUnconcluded")==true`, so `MergeState.CommittedProvisional` + slot open). The slot-lookup-failure fallback path is the only one that produces `MergeState.Immutable` for SubOrbital, and even there the slot stays open.
  - Subsequent Re-Fly of the same slot should still be allowed.
- KSP.log grep:
  - `[Parsek][INFO][SupersedeCommit] ...auto-seal=false...` for a SubOrbital outcome.
  - `[Parsek][VERBOSE][UnfinishedFlights] IsUnfinishedFlight=true rec=... reason=stableLeafUnconcluded terminal=SubOrbital` on the merge classifier pass.
- Deployed DLL verification per CLAUDE.md: grep for a distinctive UTF-16 string from the change (e.g. the new XML doc wording) to confirm the build copied to the KSP GameData folder.

## Rough order of changes inside one PR

1. Update `UnfinishedFlightClassifier` (drop SubOrbital from `IsReFlyOverrideStableTerminal` at line 351-363; split SubOrbital out of the static-focus seal at lines 309-317 so it falls through to `stableLeafUnconcluded`; update the `<summary>` XML doc at lines 342-350) + the two new xUnit tests in `UnfinishedFlightClassifierTests.cs` (§5).
2. Update `SupersedeCommit.RequiresSlotAwareMergeClassification` at lines 2057-2068 (drop SubOrbital) + the new xUnit test `SubOrbitalStableLeaf_SlotLookupFailure_DoesNotThrow_FallsBackToInFlight` (§7) + the stale comment edit at lines 459-461.
3. Update `ReFlyAutoSealPreview`: drop the enum value, the two switch cases (`PhraseFor` line 121, `GroupOrdinal` line 442), the three add sites (lines 318-320, 336-339, 362-364), and the `<summary>` XML doc at lines 198-211 (§3). Update `ReFlyAutoSealPreviewTests` (§8): flip the existing SubOrbital test, drop the `Phrase_AllReasons_MatchSpec` row, add two new tests.
4. Update `UnfinishedFlightsMembershipTests.cs` (§6) with the new `SubOrbitalFocusSlotUnderPostFeatureRP_IsMember` test.
5. Update the two in-game tests that pin the old contract: tighten `MergeNonFocusReFlyToOrbitImmutableTest` (§9), update the `MergeReFlyStructuralMutationAutoSealsTest` XML doc (§10b). Add the new end-to-end `MergeReFlyToSubOrbitalKeepsSlotOpenTest` (§10c).
6. Update authoritative design doc `docs/parsek-rewind-to-separation-design.md` (§13) and stale sibling plan doc `docs/dev/done/plans/refly-autoseal-dialog-copy.md` (§14).
7. CHANGELOG (§11) + todo doc (§12).
8. Build + dotnet test + deployed-DLL UTF-16 grep (per CLAUDE.md verification recipe).
9. PR + clean-context Opus review (NOT /ultrareview).
