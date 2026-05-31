# Switch/Fly in-bubble committed-clone: pre-switch dialog (Bounded fix)

Status: IMPLEMENTED 2026-05-31. Worktree `Parsek-fix-switch-fly-inbubble-dialog`, branch `fix-switch-fly-inbubble-dialog` (off `origin/main`).

## Implementation notes / deviations

Implemented exactly as planned. Minor deviations:
- The new `DecidePreSwitchDialogAction` parameter is named `targetIsSeparateCommittedVessel` (Case C). The Prefix computes it from `hasActiveRecording && !targetIsUnloaded && TryFindCommittedTreeMatchingVessel(pid, guid, out matched)` returning a non-null tree whose `Id` differs (Ordinal) from `ParsekFlight.Instance?.ActiveTreeForDisplay?.Id`, wrapped in try/catch with a Verbose note on failure (falls back to today's behavior). The live guid is read from `vessel.id` ("N" form) so the launch-identity guard inside the helper still applies.
- `ParsekFlight.TryFindCommittedTreeMatchingVessel` gained the `out RecordingTree matchedTree` overload; BOTH bool overloads now delegate to it (single implementation), matching semantics unchanged.
- The Case B Discard screen message was changed to the neutral "Recording discarded - switching vessels" (covers both unloaded and loaded targets); ledger reason unchanged.
- Added a distinguishing Info log on dialog open (`pre-switch-dialog opening case=A-session | B-unloaded | C-loaded-separate-committed`) so KSP.log separates Case C from Case B, and added `targetIsSeparateCommittedVessel` to the `SkipDialogReEntry` Verbose log.
- The out-param overload / bool-delegation smoke test was placed in `VesselSwitchTreeTests` (alongside the existing `TryFindCommittedTreeMatchingVessel_*` tests, which already carry the `[Collection("Sequential")]` guard and the committed-tree builders) rather than `SwitchIntentPatchSmokeTests` (which is parallel and does not manage `RecordingStore` static state). All other tests landed in `SwitchIntentPatchSmokeTests` as planned.

Closes the OPEN todo entry "v0.9.3 Switch/Fly auto-record: in-FLIGHT committed-clone restore + multi-segment scene-exit" with a contained, low-risk fix (NOT the multi-tree rework, which was abandoned as disproportionate/high-risk).

## The gap (verified)

When you are recording vessel A (live `activeTree`) and Map-"Switch To" a SEPARATE previously-committed vessel B that is **loaded** (in physics bubble) with no switch-segment session armed, today's `DecidePreSwitchDialogAction` returns `NoPriorSession` (no dialog). The consume then runs with `activeTree` still = A's tree, so the committed-clone restore (which requires `activeTree == null`) is skipped and B routes to standalone/BG-member instead of continuing B's committed history. (Scene-reload paths — TS Fly / KSC Fly / far/unloaded Map Switch-To via Case B — already work because `activeTree == null` at restore time.) The "multi-segment scene-exit" sub-issue only arises when two switch trees coexist; this fix prevents coexistence, so it is closed too.

No data loss today (A stays live, B becomes a separate standalone). This is a continuity gap. The dangerous data-loss class was already fixed in PR #876.

## The fix (Bounded): one new pre-switch-dialog case

Mirror the existing Case B (no-session, unloaded target) for the loaded-separate-committed-target case, reusing the SAME no-session handlers. The dialog commits/discards A FIRST, so `activeTree` becomes null, and B's committed-clone restores through the existing, already-tested Path B (`TryRouteCommittedSpawnedClone` -> `TryRestoreCommittedTreeForSpawnedActiveVessel`).

Single-session model is UNCHANGED. No parked trees, no scene-exit chain, no serialization changes, no schema bump.

### Changes (all in `Source/Parsek/Patches/MapFocusObjectOnSelectPatch.cs` + a tiny `ParsekFlight` helper)

1. `ParsekFlight.cs`: add `internal static bool TryFindCommittedTreeMatchingVessel(uint pid, string liveGuid, out RecordingTree matchedTree)` (the existing bool overloads delegate to it) so the Prefix can compare the matched committed tree id against the live active tree id.
2. `MapFocusObjectOnSelectPatch.Prefix`: compute
   `bool targetIsSeparateCommittedVessel` = `hasActiveRecording && !targetIsUnloaded` AND `TryFindCommittedTreeMatchingVessel(vessel.persistentId, guid, out matched)` returns a non-null tree whose `Id` differs from `ParsekFlight.Instance.ActiveTreeForDisplay?.Id` (so re-selecting the vessel you are already flying, i.e. the active committed clone, does NOT trigger a dialog).
3. `DecidePreSwitchDialogAction`: add a `bool targetIsSeparateCommittedVessel` parameter. In the no-session branch, change
   `if (hasActiveRecording && targetIsUnloaded)` to
   `if (hasActiveRecording && (targetIsUnloaded || targetIsSeparateCommittedVessel))`
   (keep the `anotherDialogOpen -> SkipDialogReEntry` guard). Case A (session armed) is unchanged and keeps priority.
4. The no-session handlers `MergeActiveTreeAndSwitchTo` / `DiscardActiveRecordingAndSwitchTo` are reused as-is. The Discard screen message currently says "switching to far vessel" (`MapFocusObjectOnSelectPatch.cs:711`); make it neutral ("Recording discarded - switching vessels") since it now also covers a loaded/near target. Keep the ledger reason.

### Why this also closes "multi-segment scene-exit"

A is committed/discarded BEFORE the switch, so A's tree and B's tree never coexist as pending switch-segment trees. At most one live tree at scene exit -> the single-session/single-tree scene-exit path is sufficient. No multi-session enumeration needed.

### Edge cases
- Target B is a NON-committed loaded vessel (random, or a BG-member of A's tree): `targetIsSeparateCommittedVessel` is false -> `NoPriorSession` -> existing in-bubble behavior (standalone / BG-member). Unchanged.
- Target B IS the active vessel's own committed clone (same tree): matched tree id == active tree id -> `targetIsSeparateCommittedVessel` false -> no dialog. Correct.
- Target B is a separate committed vessel but LANDED/SPLASHED/PRELAUNCH: dialog still resolves A correctly; B's consume defers per the existing on-surface rule (recording starts on the normal trigger). Acceptable and consistent.

## Tests
- `SwitchIntentPatchSmokeTests`: update all `DecidePreSwitchDialogAction(...)` call sites for the new param. Add predicate-matrix cases: `NoSession_ActiveRecording_LoadedTarget_SeparateCommitted_OpensDialog`; `_LoadedTarget_NonCommitted_ReturnsNoPriorSession`; `_LoadedTarget_SameTreeCommitted_NotSeparate_ReturnsNoPriorSession`; `_LoadedSeparateCommitted_AnotherDialogOpen_SkipsReEntry`; `SessionActive_LoadedSeparateCommitted_StillSessionPath` (Case A priority retained).
- Add a source-text / smoke test that the new `TryFindCommittedTreeMatchingVessel(out matchedTree)` overload returns the matched tree and the bool overloads delegate.
- Optional in-game `RuntimeTests` note documenting the in-bubble committed-clone-via-dialog path (honest about coverage).
- Full `dotnet test` green + ERS/ELS grep gate clean.

## Docs
- Close the todo entry (strikethrough + `Status: CLOSED` per neighboring entries) with a Fix: describing the Bounded pre-switch-dialog Case C and that the multi-tree rework was explicitly NOT taken.
- CHANGELOG: one user-facing line (<=2 sentences, plain ASCII, no em dashes): in-flight Switch-To from a recording vessel to a separate previously-flown vessel now prompts to keep or discard the current recording first, then continues the other vessel's recorded history correctly.
