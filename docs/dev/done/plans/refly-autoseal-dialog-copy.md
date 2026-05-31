# Re-Fly merge dialog: announce auto-seal and the reason

> **Partially superseded by [fix-suborbital-not-stable-terminal.md](./fix-suborbital-not-stable-terminal.md).**
> The `SubOrbitalArc` reason, the live-vessel `SUB_ORBITAL` situation row,
> the `ORBITING with PeR-inside-atmosphere` fallback to `SubOrbitalArc`,
> and any "Landed / Splashed / Orbiting / SubOrbital" auto-seal set
> mentioned below are stale: `SubOrbital` was dropped from the seal
> contract because a suborbital arc is still in flight. The dialog copy
> framework and the other reasons (science, structural mutations, hard
> safety terminals, Landed / Splashed / Orbiting) described here remain
> current. Treat any SubOrbital reference in this doc as historical
> context, not as the active behavior.

## Problem

When the player ends a Re-Fly attempt at KSC / TS / Main Menu and clicks
"Merge" in the pre-transition merge dialog, a hidden
policy decides whether the slot stays re-flyable or is **auto-sealed**
(slot becomes immutable; the line of flight cannot be Re-Flown again).

The current dialog body is generic:

> Commit this Re-Fly attempt permanently to the timeline. This cannot be
> undone.

This is technically true but does not convey:

1. that auto-seal will fire (vs the slot staying re-flyable for further
   retry);
2. **why** auto-seal will fire (the player's specific actions that
   triggered the policy).

Result: players hit Merge expecting they can still retry the line, then
discover the slot is sealed.

## Goal

Customize the Re-Fly variant of `MergeDialog.ShowTreeDialog` (the
4-arg `(tree, labels, preCommitFinalize, postChoice)` overload, line
269-345 of `Source/Parsek/MergeDialog.cs`) so that when the merge will
auto-seal the slot, the body copy:

- Calls out auto-seal explicitly ("will be merged AND auto-sealed");
- Lists the player-attributable reason(s) ("you transmitted science",
  "you undocked", "you docked with another vessel", etc.);
- States the consequence ("you will not be able to Re-Fly this line of
  flight again").

Default copy stays for non-auto-seal Re-Fly merges (slot remains
re-flyable; current language is correct).

## Auto-seal trigger map

`SupersedeCommit.ShouldAutoSealReFlySlotAfterMerge`
(`Source/Parsek/SupersedeCommit.cs:908-941`) seals when any of the
following fire (close-reason builder at `:567-633`):

| Internal reason | What the player did | Player-facing phrase |
| --- | --- | --- |
| `RecordingAction` (any retry-blocking `ScienceEarning` row tagged on the provisional or its chain segments) | Transmitted / earned science during the attempt | "transmitted science" or "recovered science" or "earned science" |
| `StructuralMutation` with `BranchPointType == Undock` | Undocked | "undocked" |
| `StructuralMutation` with `BranchPointType == EVA` | Sent a kerbal on EVA | "sent a kerbal on EVA" |
| `StructuralMutation` with `BranchPointType == JointBreak` | A part broke off | "broke off a part" |
| `StructuralMutation` with `BranchPointType == Breakup` | Vessel broke up | "the vessel broke up" |
| `ClassifierClosed:stableTerminal` AND `IsHardSafetyTerminal` (Docked) | Docked with another vessel | "docked with another vessel" |
| `ClassifierClosed:stableTerminal` AND `IsHardSafetyTerminal` (Boarded / Recovered) | Kerbal boarded / vessel recovered | (not reachable in-flight - skipped, see Risks) |
| `ClassifierClosed:stableTerminalFocusSlot` covers ALL of Landed / Splashed / Orbiting / SubOrbital at the merge-time focus slot (`UnfinishedFlightClassifier.cs:240-256, IsReFlyOverrideStableTerminal:351-363`) | Concluded the attempt at any stable terminal | "landed", "splashed down", "reached a stable orbit", "reached a sub-orbital arc" |
| `ClassifierClosed:downstreamBp` | Created a downstream branch point | (subset case; covered by structural mutations in practice - skipped) |
| `classifierQualifies` path with any close reason | Generic concluded outcome | (covered by the above specific cases) |

Note: `stashedStableLeaf` (`UnfinishedFlightClassifier.cs:259-273`) and
`stableLeafUnconcluded` (`:329`) both **keep the slot open** rather
than seal it (`SupersedeCommit.cs:623-631`,
`IsSafeStableRetryClassifierReason:975-981`). The preview does NOT
need to detect these - the slot stays re-flyable and the default
"permanently to the timeline" copy applies (which is still correct;
the merge IS permanent, the slot just remains re-flyable for retry).

## Constraint: preview is computed pre-finalize

The dialog appears **before** `preCommitFinalize` runs (PR #750's
deferred-finalize design). At dialog-show time:

- `Recording.TerminalStateValue` is null on the live provisional.
- `Ledger.Actions` IS populated for in-flight events (science
  earnings emit on transmission/recovery as they happen).
- Tree topology IS populated for branch points (decouple / stage /
  EVA / joint-break write `BranchPoint` rows when they occur).
- Live `vessel.situation` IS authoritative for the current
  Docked / Orbiting / Landed status.

This rules out calling `SupersedeCommit.ClassifyMergeStateOrThrow`
directly: it reads `TerminalStateValue` via `UnfinishedFlightClassifier`
and `IsHardSafetyTerminal`. We need a **parallel preview helper**
that reads only data that is reliable pre-finalize.

The preview is intentionally a **subset** of the production classifier:

- Conservative (false negatives over false positives): if the preview
  cannot determine seal, default copy applies. The player still gets
  the truthful "permanently" wording, just not the auto-seal call-out.
- Mirrors the production gates (`IsRetryBlockingRecordingAction`,
  `HasReFlySessionStructuralMutation`) so when the preview says
  "you transmitted science," the production classifier will agree.

## Architecture

### New file: `Source/Parsek/ReFlyAutoSealPreview.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek
{
    internal enum ReFlyAutoSealReason
    {
        EarnedScience,         // any retry-blocking ScienceEarning row in the lineage
        TransmittedScience,    // ScienceMethod.Transmitted (more specific override)
        RecoveredScience,      // ScienceMethod.Recovered (more specific override)
        Undocked,              // BranchPointType.Undock
        KerbalEva,             // BranchPointType.EVA
        PartBrokeOff,          // BranchPointType.JointBreak
        VesselBrokeUp,         // BranchPointType.Breakup
        DockedWithAnother,     // live vessel.situation == DOCKED -> IsHardSafetyTerminal
        Landed,                // live vessel.situation == LANDED  -> stableTerminalFocusSlot
        SplashedDown,          // live vessel.situation == SPLASHED -> stableTerminalFocusSlot
        StableOrbit,           // live vessel.situation == ORBITING && PeR > atmosphere
        SubOrbitalArc,         // live vessel.situation == SUB_ORBITAL OR ORBITING && PeR <= atmosphere
    }

    internal struct ReFlyAutoSealPreviewResult
    {
        public bool WillAutoSeal;
        public List<ReFlyAutoSealReason> Reasons;

        public static readonly ReFlyAutoSealPreviewResult NoSeal =
            new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = false,
                Reasons = new List<ReFlyAutoSealReason>(0),
            };

        /// <summary>
        /// Composes a player-facing phrase from <see cref="Reasons"/>:
        ///   0 reasons -> null (caller shows default copy)
        ///   1 reason  -> "you transmitted science"
        ///   2 reasons -> "you transmitted science and undocked"
        ///   3+ reasons -> "you transmitted science, undocked, and docked with another vessel"
        /// </summary>
        public string FormatHumanReadable() { /* see Format spec below */ }
    }

    internal static class ReFlyAutoSealPreviewer
    {
        internal static ReFlyAutoSealPreviewResult Preview(
            Recording liveProvisional,
            ReFlySessionMarker marker,
            ParsekScenario scenario,
            Vessel liveActiveVessel);
    }
}
```

### Preview body (read-only, conservative, no mutation)

Sequence:

1. **Null guards**:
   - `marker == null` -> return `NoSeal`.
   - `liveProvisional == null` -> return `NoSeal`.
   - `marker.TreeId` empty OR `liveProvisional.TreeId` empty OR
     `liveProvisional.TreeId != marker.TreeId` -> return `NoSeal`.

2. **Earned-science check** (mirrors
   `SupersedeCommit.IsRetryBlockingRecordingAction` at line 1257):

   - Build the lineage recording-id set via
     `SupersedeCommit.CollectRecordingIdsForSafetyGate(liveProvisional)`
     (currently `private`; see Refactor below).
   - Walk `Ledger.Actions`. For each action:
     - Skip if `action == null` or `action.RecordingId` empty.
     - Skip if `action.Type != GameActionType.ScienceEarning`.
     - Skip if `!recordingIds.Contains(action.RecordingId)`.
     - Skip if `TombstoneEligibility.IsEligible(action)` (mirror
       `IsWorldStateChangingRecordingAction`).
     - Skip if `TombstoneEligibility.TryPairBundledRepPenalty(action,
       Ledger.Actions, out _)` (same).
     - Otherwise add a reason: prefer `TransmittedScience` if
       `action.Method == ScienceMethod.Transmitted`; otherwise
       `RecoveredScience` if `Recovered`; else `EarnedScience` (defensive
       fallback for unexpected enum values).

   Multiple science rows of the same kind dedupe to one reason. Mixed
   methods produce one of each (e.g. one Transmitted + one Recovered ->
   both reasons listed).

3. **Structural-mutation check**: call the new typed helper
   `SupersedeCommit.TryGetFirstReFlySessionStructuralMutationType(
   liveProvisional, marker, out BranchPointType firstType)` (see
   Refactor below). If true, map `firstType`:

   - `Undock` -> `Undocked`
   - `EVA` -> `KerbalEva`
   - `JointBreak` -> `PartBrokeOff`
   - `Breakup` -> `VesselBrokeUp`

   Only the first structural mutation is surfaced (production logs the
   first too at `:773`). Multiple BPs of different kinds collapse to
   one reason; this avoids long copy and matches what the player
   typically remembers ("I undocked" rather than "I undocked and then
   the booster broke up as a consequence").

4. **Live vessel terminal proxy**: if `liveActiveVessel != null`,
   read `liveActiveVessel.situation` and surface a reason for any
   stable terminal that production seals via `stableTerminalFocusSlot`
   (`UnfinishedFlightClassifier.IsReFlyOverrideStableTerminal:351-363`
   covers Landed, Splashed, Orbiting, SubOrbital) or `IsHardSafetyTerminal`
   (`SupersedeCommit.cs:963-973` covers Docked):
   - `Vessel.Situations.DOCKED` -> `DockedWithAnother`.
   - `Vessel.Situations.LANDED` -> `Landed`.
   - `Vessel.Situations.SPLASHED` -> `SplashedDown`.
   - `Vessel.Situations.ORBITING`: defensively null-check
     `liveActiveVessel.orbit` AND `liveActiveVessel.orbit.referenceBody`.
     If either is null (transient pre-launch / scene-switch frames -
     mirror the production pattern at `RecordingTree.cs:863`), fall
     back to no live-terminal reason (treat like FLYING). Otherwise
     use `RecordingTree.IsBoundOrbitAboveAtmosphere(orbit.eccentricity,
     orbit.PeR, body.Radius, body.atmosphere, body.atmosphereDepth)`
     (already `internal static` at `RecordingTree.cs:827-852`,
     reachable from preview). True -> `StableOrbit`. False ->
     `SubOrbitalArc` (decaying orbit; production downgrades to
     SubOrbital terminal at finalize but still seals via
     `stableTerminalFocusSlot`).
   - `Vessel.Situations.SUB_ORBITAL` -> `SubOrbitalArc`.
   - Anything else (PRELAUNCH, FLYING, ESCAPING) -> no live-terminal
     reason (other paths still apply).
   - `Boarded` / `Recovered` are unreachable in-flight (the vessel
     leaves the scene before either occurs) so they are intentionally
     not surfaced. Their seal still fires via the production classifier
     post-finalize, but the dialog never spawns for those terminal
     states from this entry point.

   The vessel `situation` enum is a one-shot snapshot at dialog spawn.
   While the dialog is up with `LockInput()` applied, KSP physics still
   runs in the background; the active vessel's situation could in
   theory flip (e.g. an unstable orbit decays into atmosphere over a
   minute while the dialog sits open). Acceptable - the player must
   click eventually, and re-classification happens at finalize. Add a
   "// situation sampled at dialog spawn only" comment at the call site.

5. **`WillAutoSeal = Reasons.Count > 0`**.

### `FormatHumanReadable` spec

| Reason | Phrase (subject-free; phrases are listed after a colon, no leading "you") |
| --- | --- |
| `EarnedScience` | "earned science" |
| `TransmittedScience` | "transmitted science" |
| `RecoveredScience` | "recovered science" |
| `Undocked` | "undocked" |
| `KerbalEva` | "sent a kerbal on EVA" |
| `PartBrokeOff` | "broke off a part" |
| `VesselBrokeUp` | "the vessel broke up" |
| `DockedWithAnother` | "docked with another vessel" |
| `Landed` | "landed" |
| `SplashedDown` | "splashed down" |
| `StableOrbit` | "reached a stable orbit" |
| `SubOrbitalArc` | "reached a sub-orbital arc" |

Composition (Opus pass-1 finding F6: drop "because" + "you" prefix to
avoid pronoun-mix grammar problems with subject-led phrases like
"the vessel broke up"):

- 0 reasons: return `null`. Caller shows default copy.
- 1 reason: return the bare phrase, e.g. `"transmitted science"`.
- 2 reasons: `"{phrase1} and {phrase2}"`.
- 3+ reasons: `"{phrase1}, {phrase2}, and {phraseN}"` (Oxford comma).

The dialog body wraps the phrase in a "for the following reason(s):"
prefix so the rendered sentence reads naturally regardless of the
number of reasons. See "Dialog integration" below.

Reason ordering for output stability (player-relevance, descending):
1. `TransmittedScience` / `RecoveredScience` / `EarnedScience` (group)
2. Structural mutations (`Undocked`, `KerbalEva`, `PartBrokeOff`, `VesselBrokeUp`)
3. Live terminal (`DockedWithAnother`, `Landed`, `SplashedDown`, `StableOrbit`, `SubOrbitalArc`)

Within each group, preserve insertion order. The Reasons list is
ordered at insertion to match this scheme - sort once at the end of
`Preview` for predictable test output.

This ordering does **not** match production's first-hit close-reason
log (production walks gates in order and stops on first hit at
`SupersedeCommit.cs:574-633`). The preview is intentionally a
**superset** of the production log line: production logs only the
single reason that gated open vs closed; the dialog shows all the
player-attributable reasons in a stable order so the player gets the
full picture. Tests assert this stable readability ordering, not
parity with production's first-hit selection.

### Refactor: minimal SupersedeCommit visibility changes

Two helpers in `SupersedeCommit.cs` need `internal` visibility (currently
`private`) so the preview can reuse them:

1. `CollectRecordingIdsForSafetyGate(Recording rec)` at
   `Source/Parsek/SupersedeCommit.cs:1137` -> change to `internal static`.

2. New public surface for the structural mutation walk:

   ```csharp
   internal static bool TryGetFirstReFlySessionStructuralMutationType(
       Recording rec,
       ReFlySessionMarker marker,
       out BranchPointType firstType);
   ```

   Implementation: refactor `HasReFlySessionStructuralMutation`
   (line 677-780) to share its walk with the new method. Cleanest:
   extract a private helper `ScanReFlySessionStructuralMutations` that
   returns a tuple `(int matchedCount, BranchPointType? firstType,
   string detail)`. Both `HasReFlySessionStructuralMutation` and
   `TryGetFirstReFlySessionStructuralMutationType` call it; existing
   behaviour is unchanged.

   The new helper does NOT need `out string detail` for the preview path
   - it gets `firstType` only. The existing `out detail` behaviour in
   `HasReFlySessionStructuralMutation` stays for the production classifier
   logs.

3. (Optional / not required) `TryResolveReFlyStructuralCutoffUT` at
   line 849 - the new typed helper will use `HasReFlySessionStructuralMutation`'s
   internals via the shared private helper, so this stays private.

### Dialog integration

Extract a pure helper `BuildReFlyDialogBody` so the body composition
is unit-testable without spinning up a Unity dialog (Opus pass-1
finding F8). Place it on `MergeDialog` (or co-locate with the
preview helper - `MergeDialog` is more discoverable since it is
where the dialog body lives today).

```csharp
internal static string BuildReFlyDialogBody(
    string vesselLabel,
    double reFlyDuration,
    ReFlyAutoSealPreviewResult preview)
{
    string headline = $"<align=\"center\">{vesselLabel} - " +
                      $"{FormatDuration(reFlyDuration)}</align>\n\n";
    if (!preview.WillAutoSeal)
    {
        return headline +
            "<align=\"left\">Commit this Re-Fly attempt permanently to " +
            "the timeline. This cannot be undone.</align>";
    }

    string reasons = preview.FormatHumanReadable();
    return headline +
        "<align=\"left\"><b>This Re-Fly attempt will be merged AND " +
        $"auto-sealed</b> for the following reason(s): {reasons}. " +
        "The slot will become permanent and you will not be able to " +
        "Re-Fly this line of flight again. This cannot be undone.</align>";
}
```

Rendered examples (auto-seal branch):

| `Reasons` | Final body (after headline) |
| --- | --- |
| `[TransmittedScience]` | "...for the following reason(s): transmitted science. The slot..." |
| `[Undocked, DockedWithAnother]` | "...for the following reason(s): undocked and docked with another vessel. The slot..." |
| `[TransmittedScience, Undocked, StableOrbit]` | "...for the following reason(s): transmitted science, undocked, and reached a stable orbit. The slot..." |
| `[VesselBrokeUp, DockedWithAnother]` | "...for the following reason(s): the vessel broke up and docked with another vessel. The slot..." (mixed-subject phrase still reads correctly under the colon-list form because there is no implicit "you" subject) |

Then in `MergeDialog.ShowTreeDialog` (4-arg overload), replace the
fixed body string at line 298-300 with:

```csharp
// situation sampled at dialog spawn only - while the dialog sits
// open, KSP physics still runs in background and the active vessel
// situation could in theory flip, but the player has to click
// eventually and the production classifier re-classifies at finalize.
var preview = ReFlyAutoSealPreviewer.Preview(
    reFlyRec, marker, reFlyScenario, FlightGlobals.ActiveVessel);
message = BuildReFlyDialogBody(vesselLabel, reFlyDuration, preview);

ParsekLog.Info("MergeDialog",
    $"Re-Fly auto-seal preview: willSeal={preview.WillAutoSeal} " +
    $"reasons=[{string.Join(",", preview.Reasons)}] " +
    $"sess={marker?.SessionId ?? "<no-id>"}");
```

This Info log at dialog-show time lets us correlate with the actual
post-merge classifier outcome in case of preview drift.

Notes:

- `reFlyScenario` is already in scope (lines 268-272 of `MergeDialog.cs`).
- `FlightGlobals.ActiveVessel` is the live active vessel - safe to read
  in flight (the prefix that spawned this dialog ran while in flight).
- The `<b>` emphasis flags the change as significant. Using
  `<align="left">` matches the existing layout style.
- `marker` is already resolved in scope.

## Files to change

| Status | Path | Change |
| --- | --- | --- |
| New | `Source/Parsek/ReFlyAutoSealPreview.cs` | Preview helper + `ReFlyAutoSealReason` enum + `ReFlyAutoSealPreviewResult` struct + `FormatHumanReadable` |
| Modified | `Source/Parsek/SupersedeCommit.cs` | `CollectRecordingIdsForSafetyGate` -> `internal`; extract shared `ScanReFlySessionStructuralMutations` private helper; new `TryGetFirstReFlySessionStructuralMutationType` internal API |
| Modified | `Source/Parsek/MergeDialog.cs` | Add `BuildReFlyDialogBody(vesselLabel, duration, preview)` static helper; wire preview into the ReFlyAttempt body copy in the 4-arg `ShowTreeDialog` overload; add canary log line |
| New | `Source/Parsek.Tests/ReFlyAutoSealPreviewTests.cs` | Unit tests per reason path + multi-reason composition + null guards + format spec |
| Modified | `CHANGELOG.md` | Entry under 0.9.2 Enhancements |

## Test matrix

### `ReFlyAutoSealPreviewer.Preview` core matrix

Pure unit tests, xUnit. `[Collection("Sequential")]` for shared
`Ledger`, `RecordingStore`, `ParsekScenario` static state. Constructor
calls `ParsekLog.ResetTestOverrides`, `Ledger.ResetForTesting`,
`RecordingStore.ResetForTesting`, `ParsekScenario.ResetInstanceForTesting`.

Pronoun convention: phrases are subject-free (Opus pass-1 F6); the
dialog body wraps them in "for the following reason(s):" so the
`FormatHumanReadable` column shows the bare phrase.

| Setup | Expected reasons | `FormatHumanReadable` |
| --- | --- | --- |
| `marker == null` | `WillAutoSeal=false`, empty | `null` |
| `liveProvisional == null` | `WillAutoSeal=false`, empty | `null` |
| `marker.TreeId == null` | `WillAutoSeal=false`, empty | `null` |
| `liveProvisional.TreeId != marker.TreeId` | `WillAutoSeal=false`, empty | `null` |
| `liveProvisional.RecordingId != marker.ActiveReFlyRecordingId` (in-place continuation) | preview still runs - TreeId match is the gate, not RecordingId match (matches production: in-place continuation has same TreeId, different RecordingId, same lineage) | depends on other paths |
| Idle Re-Fly, no events, vessel landed | empty | `null` |
| Ledger has `ScienceEarning` (Method=Transmitted) tagged on provisional | `[TransmittedScience]` | `"transmitted science"` |
| Ledger has `ScienceEarning` (Method=Recovered) tagged on provisional | `[RecoveredScience]` | `"recovered science"` |
| Ledger has 2 `ScienceEarning` rows, both Transmitted, both tagged on provisional | `[TransmittedScience]` (deduped) | `"transmitted science"` |
| Ledger has 1 Transmitted + 1 Recovered tagged on provisional | `[TransmittedScience, RecoveredScience]` | `"transmitted science and recovered science"` |
| Ledger has `ScienceEarning` tagged on a chain-segment of the provisional | `[TransmittedScience]` (or whichever Method) | proportional |
| Ledger has `ScienceEarning` tagged on a different recording (not in lineage) | empty | `null` |
| Ledger has tombstone-eligible `ScienceEarning` (paired with kerbal death penalty) | empty | `null` |
| Ledger has tombstone-eligible `ScienceEarning` (`TombstoneEligibility.IsEligible == true` non-paired) | empty | `null` |
| Ledger has non-`ScienceEarning` action tagged on provisional (e.g. `FundsEarning`) | empty | `null` |
| Tree has post-RP `Undock` BP in lineage | `[Undocked]` | `"undocked"` |
| Tree has post-RP `EVA` BP in lineage | `[KerbalEva]` | `"sent a kerbal on EVA"` |
| Tree has post-RP `JointBreak` BP in lineage | `[PartBrokeOff]` | `"broke off a part"` |
| Tree has post-RP `Breakup` BP in lineage | `[VesselBrokeUp]` | `"the vessel broke up"` |
| Pre-RP BP only (UT < cutoff) | empty | `null` |
| BP not in Re-Fly target lineage (background vessel) | empty | `null` |
| BP in `marker.PreSessionBranchPointIds` (re-spliced from load) | empty | `null` |
| `marker.PreSessionBranchPointIds == null` (legacy marker) | structural skipped, other paths still work | `null` if no other reason |
| Multiple structural BPs of different types | first only (e.g. `[Undocked]` even if Breakup followed) | first phrase only |
| Live vessel `situation == DOCKED` | `[DockedWithAnother]` | `"docked with another vessel"` |
| Live vessel `situation == LANDED` | `[Landed]` | `"landed"` |
| Live vessel `situation == SPLASHED` | `[SplashedDown]` | `"splashed down"` |
| Live vessel `situation == ORBITING` && PeR > atmosphereDepth | `[StableOrbit]` | `"reached a stable orbit"` |
| Live vessel `situation == ORBITING` && airless body (e.g. Mun) && PeR > body.Radius | `[StableOrbit]` | `"reached a stable orbit"` |
| Live vessel `situation == ORBITING` && PeR <= atmosphereDepth (decaying) | `[SubOrbitalArc]` | `"reached a sub-orbital arc"` |
| Live vessel `situation == ORBITING` && `vessel.orbit == null` (transient) | empty | `null` (treat like FLYING) |
| Live vessel `situation == ORBITING` && `vessel.orbit.referenceBody == null` | empty | `null` (treat like FLYING) |
| Live vessel `situation == SUB_ORBITAL` | `[SubOrbitalArc]` | `"reached a sub-orbital arc"` |
| Live vessel `situation == FLYING` (no terminal yet) | empty | `null` (other paths may still apply) |
| Live vessel `situation == PRELAUNCH` | empty | `null` |
| `liveActiveVessel == null` | live-vessel reasons skipped, other paths still work | depends |
| Multi-reason: science + undock | `[TransmittedScience, Undocked]` | `"transmitted science and undocked"` |
| Multi-reason: science + undock + dock | 3 reasons, ordered (science, struct, terminal) | `"transmitted science, undocked, and docked with another vessel"` |
| Multi-reason: undock + breakup | first structural only `[Undocked]` | `"undocked"` |
| Multi-reason: science + landed | `[TransmittedScience, Landed]` | `"transmitted science and landed"` |

### `FormatHumanReadable` standalone

Subject-free phrasing wrapped by the dialog body in "for the following
reason(s): ...". This sidesteps the pronoun-mix grammar problem with
phrases like "the vessel broke up" (Opus pass-1 F6).

| Reasons | Output |
| --- | --- |
| `[]` | `null` |
| `[EarnedScience]` | `"earned science"` |
| `[TransmittedScience]` | `"transmitted science"` |
| `[Undocked]` | `"undocked"` |
| `[VesselBrokeUp]` | `"the vessel broke up"` |
| `[Landed]` | `"landed"` |
| `[Undocked, KerbalEva]` | `"undocked and sent a kerbal on EVA"` |
| `[VesselBrokeUp, DockedWithAnother]` | `"the vessel broke up and docked with another vessel"` |
| `[TransmittedScience, Undocked, DockedWithAnother]` | `"transmitted science, undocked, and docked with another vessel"` |
| `[VesselBrokeUp, TransmittedScience, DockedWithAnother]` | `"the vessel broke up, transmitted science, and docked with another vessel"` |

Composition rules:
- 0 reasons: `null`.
- 1 reason: bare phrase.
- 2 reasons: `"{a} and {b}"`.
- 3+ reasons: `"{a}, {b}, ..., and {n}"` (Oxford comma).

### `BuildReFlyDialogBody` standalone

| Setup | Body fragment after the headline |
| --- | --- |
| `WillAutoSeal=false` | `"Commit this Re-Fly attempt permanently to the timeline. This cannot be undone."` |
| `WillAutoSeal=true, Reasons=[TransmittedScience]` | `"<b>This Re-Fly attempt will be merged AND auto-sealed</b> for the following reason(s): transmitted science. The slot will become permanent and you will not be able to Re-Fly this line of flight again. This cannot be undone."` |
| `WillAutoSeal=true, Reasons=[VesselBrokeUp, DockedWithAnother]` | `"...the following reason(s): the vessel broke up and docked with another vessel. The slot..."` |

### Reason ordering test

Insert reasons in random order; assert the `Reasons` list comes back
sorted by group (science -> structural -> terminal) and within group by
insertion order. Stability matters for deterministic test output and
matches what production `SupersedeCommit.ShouldAutoSealReFlySlotAfterMerge`
would prioritise (it picks the first close-reason hit).

### Defensive tests

- `Recording.Points` empty -> safe (preview reads metadata only).
- Provisional has no chain segments -> `CollectRecordingIdsForSafetyGate`
  returns just the provisional id; lineage check works.
- `Ledger.Actions` empty -> no science reasons added.
- `RecordingStore.CommittedTrees` empty -> structural check returns false
  (matches production behaviour at `SupersedeCommit.cs:706-712`).
- Marker with empty `RewindPointId` -> structural cutoff falls back to
  `marker.InvokedUT`; preview still works.

### State-version invariance (Opus pass-1 F17)

Add a single test that pins the read-only contract: capture
`Ledger.StateVersion`, `RecordingStore.SupersedeStateVersion`, and any
other mutation-tracking counters before and after `Preview()` for a
non-trivial scenario (provisional, marker, ledger entries, tree, live
vessel). Assert all counters unchanged. Catches accidental future
routes through cached / mutating helpers.

## Risks

- **False positives** (preview says seal, finalize doesn't): would
  mislead the player into thinking they cannot retry when they actually
  could. Mitigation: mirror production gates exactly. Specifically
  reuse `SupersedeCommit.IsRetryBlockingRecordingAction` (already
  internal at line 1257) and the typed structural-mutation helper
  refactored to share with `HasReFlySessionStructuralMutation`. A unit
  test asserts science-action filtering matches `TombstoneEligibility`
  exclusion rules.

- **False negatives** (preview says no seal, finalize seals): default
  copy shown, player learns of seal post-load via slot UI. Acceptable
  degradation. The cases the preview deliberately skips
  (`Boarded`/`Recovered` terminals, `downstreamBp`, generic
  `classifierQualifies`) are either unreachable from the in-flight
  dialog (`Boarded`/`Recovered`) or rare/redundant (`downstreamBp` is
  almost always covered by a structural mutation that fires first;
  generic `classifierQualifies` requires a specific terminal state which
  is null pre-finalize).

- **Stable terminal proxy correctness**: the preview maps live
  `Vessel.Situations.LANDED/SPLASHED/ORBITING/SUB_ORBITAL/DOCKED` to
  reasons that match the production seal verdict via
  `stableTerminalFocusSlot` (Landed/Splashed/Orbiting/SubOrbital) and
  `IsHardSafetyTerminal` (Docked). A vessel coasting through a
  high-eccentricity orbit that grazes atmosphere may flicker between
  SUB_ORBITAL and ORBITING; the preview uses
  `RecordingTree.IsBoundOrbitAboveAtmosphere` to pick "stable orbit"
  vs "sub-orbital arc" wording, but BOTH terminals seal under
  `stableTerminalFocusSlot`, so the seal verdict is correct even if
  the wording mispredicts. The dialog is a one-shot snapshot at
  click time, so transient flicker is acceptable.

- **In-place continuation case**: `IsInPlaceContinuation(marker,
  provisional)` returns true when
  `provisional.RecordingId == marker.OriginChildRecordingId`. The
  production classifier's slot-lookup branch (line 481-521) handles
  in-place differently from new-attempt continuations, but the
  auto-seal logic itself runs against the same `provisional` and same
  close-reason classification. The preview is unaffected by in-place
  vs new-attempt distinction.

- **`ScienceMethod` enum drift**: today only `Transmitted` and
  `Recovered`. If KSP adds new methods, `EarnedScience` is the
  catch-all default. Added defensively.

- **Wording length**: the longest possible composed phrase is roughly
  `"you transmitted science, recovered science, undocked, sent a kerbal
  on EVA, broke off a part, docked with another vessel, and reached a
  stable orbit"` - ~150 chars. KSP `MultiOptionDialog` width handles
  this with line wrapping. In practice 1-2 reasons covers the common
  cases. No truncation needed.

- **Logging volume**: one Info line per dialog spawn is bounded (dialog
  shows at most once per scene exit). Not a spam concern.

## Out of scope

- The post-load deferred merge dialog (zero-arg `ShowTreeDialog`). The
  user's ask is for the in-flight pre-transition dialog. Adding the
  same preview to the post-load path is a logical extension but
  requires different live-state handling (no `ActiveVessel` post-load;
  classifier could be called directly post-finalize). Defer to a
  follow-up.

- An "abort merge / retain re-flyable slot" option. The auto-seal
  policy is a deliberate gameplay design choice; the dialog only
  *announces* the consequence, it does not let the player override it.

- Localization. Parsek is English-only.

- Per-action breakdown (which specific science subject the player
  transmitted, which part broke off). Adds noise without helping the
  player understand the consequence.

## Open questions (resolved)

1. **Pronoun style**: subject-free phrasing wrapped in "for the
   following reason(s):" copy form. Resolved per Opus pass-1 F6.

2. **`Boarded` / `Recovered` detection**: skip - unreachable in-flight.
   Production classifier seals correctly post-finalize.

3. **Single structural reason vs all structural reasons**: first only,
   matching production's first-bp log at `SupersedeCommit.cs:760-764`.

## Implementation order

1. Refactor `SupersedeCommit.cs`: extract `ScanReFlySessionStructuralMutations`
   shared helper; change `CollectRecordingIdsForSafetyGate` to internal;
   add new `TryGetFirstReFlySessionStructuralMutationType` API. Verify
   existing tests still pass. (1 commit.)

2. Add `ReFlyAutoSealPreview.cs` with the enum, struct, and
   `Preview` / `FormatHumanReadable` implementations. Add
   `ReFlyAutoSealPreviewTests.cs` covering the matrix. (1 commit, or
   split into "preview helper" + "tests" if the diff is large.)

3. Wire the preview into `MergeDialog.ShowTreeDialog`'s ReFlyAttempt
   body copy. Smoke-test in-game: trigger Re-Fly with a science
   transmission, exit to KSC, confirm dialog says "transmitted science."
   (1 commit.)

4. CHANGELOG entry. Open PR.

## CHANGELOG entry

Per `.claude/CLAUDE.md` "Documentation Updates - Per Commit, Not Per PR":

`CHANGELOG.md` -> `0.9.2 -> Enhancements`:

> The Re-Fly merge confirmation dialog now announces auto-seal
> explicitly when a merge will permanently seal the slot, and lists
> the player-attributable reason (transmitted science, undocked,
> docked with another vessel, etc.). Previously the dialog said the
> commit was permanent but did not distinguish auto-seal from a
> regular commit that left the slot re-flyable.
