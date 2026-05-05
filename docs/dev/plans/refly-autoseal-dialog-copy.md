# Re-Fly merge dialog: announce auto-seal and the reason

## Problem

When the player ends a Re-Fly attempt at KSC / TS / Main Menu and clicks
"Merge Re-Fly to Timeline" in the pre-transition merge dialog, a hidden
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
| `ClassifierClosed:stableTerminal` AND `IsHardSafetyTerminal` (Boarded) | Kerbal boarded another vessel | (not reachable in-flight - skipped, see Risks) |
| `ClassifierClosed:stableTerminal` AND `IsHardSafetyTerminal` (Recovered) | Vessel was recovered | (not reachable in-flight - skipped, see Risks) |
| `ClassifierClosed:stableTerminalFocusSlot` (player-chosen slot reached stable Orbiting/SubOrbital) | Reached stable orbit | "reached a stable orbit" |
| `ClassifierClosed:downstreamBp` | Created a downstream branch point | (subset case; covered by structural mutations in practice - skipped) |
| `classifierQualifies` path with any close reason | Generic concluded outcome | (covered by the above specific cases) |

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
        DockedWithAnother,     // live vessel.situation == DOCKED
        StableOrbit,           // live vessel.situation == ORBITING
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

4. **Live vessel terminal proxy**: if `liveActiveVessel != null`:
   - `vessel.situation == Vessel.Situations.DOCKED` ->
     `DockedWithAnother`.
   - `vessel.situation == Vessel.Situations.ORBITING` -> `StableOrbit`.
     (KSP marks situation ORBITING only when periapsis is above the
     body's surface, and above atmosphere if present, so this is a
     reliable proxy for the production "stable orbit" classification.)
   - Boarded / Recovered are unreachable in-flight (the vessel left the
     scene before either could occur) so they are intentionally not
     surfaced; their auto-seal still happens later via the production
     classifier, but the dialog wouldn't have been shown for those
     terminal states anyway because they exit flight first.

5. **`WillAutoSeal = Reasons.Count > 0`**.

### `FormatHumanReadable` spec

| Reason | Phrase |
| --- | --- |
| `EarnedScience` | "earned science" |
| `TransmittedScience` | "transmitted science" |
| `RecoveredScience` | "recovered science" |
| `Undocked` | "undocked" |
| `KerbalEva` | "sent a kerbal on EVA" |
| `PartBrokeOff` | "broke off a part" |
| `VesselBrokeUp` | "the vessel broke up" |
| `DockedWithAnother` | "docked with another vessel" |
| `StableOrbit` | "reached a stable orbit" |

Composition:
- 0 reasons: return `null`. Caller shows default copy.
- 1 reason: `"you {phrase}"`.
- 2 reasons: `"you {phrase1} and {phrase2}"`.
- 3+ reasons: `"you {phrase1}, {phrase2}, and {phraseN}"` (Oxford comma).

Reason ordering for output stability (player-relevance, descending):
1. `TransmittedScience` / `RecoveredScience` / `EarnedScience` (group)
2. Structural mutations (`Undocked`, `KerbalEva`, `PartBrokeOff`, `VesselBrokeUp`)
3. Live terminal (`DockedWithAnother`, `StableOrbit`)

Within each group, preserve insertion order. The Reasons list is
ordered at insertion to match this scheme - sort once at the end of
`Preview` for predictable test output.

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

In `MergeDialog.ShowTreeDialog` (4-arg overload), inside the
`labels == ReFlyAttempt` branch (currently line 282-309), replace the
fixed body string with:

```csharp
var preview = ReFlyAutoSealPreviewer.Preview(
    reFlyRec, marker, reFlyScenario, FlightGlobals.ActiveVessel);
string body;
if (preview.WillAutoSeal)
{
    string why = preview.FormatHumanReadable();
    body = $"<align=\"center\">{vesselLabel} - {FormatDuration(reFlyDuration)}</align>\n\n" +
           $"<align=\"left\"><b>This Re-Fly attempt will be merged AND auto-sealed</b> " +
           $"because {why}. The slot will become permanent and you will not be " +
           $"able to Re-Fly this line of flight again. This cannot be undone.</align>";
}
else
{
    body = $"<align=\"center\">{vesselLabel} - {FormatDuration(reFlyDuration)}</align>\n\n" +
           "<align=\"left\">Commit this Re-Fly attempt permanently to the timeline. " +
           "This cannot be undone.</align>";
}
message = body;
```

Notes:

- `reFlyScenario` is already in scope (lines 268-272 of `MergeDialog.cs`).
- `FlightGlobals.ActiveVessel` is the live active vessel - safe to read
  in flight (the prefix that spawned this dialog ran while in flight).
- The `<b>` emphasis flags the change as significant. Using
  `<align="left">` matches the existing layout style.
- `marker` is already resolved in scope.

Logging:

```csharp
ParsekLog.Info("MergeDialog",
    $"Re-Fly auto-seal preview: willSeal={preview.WillAutoSeal} " +
    $"reasons=[{string.Join(",", preview.Reasons)}] " +
    $"sess={marker?.SessionId ?? "<no-id>"}");
```

This logs at dialog-show time so we can correlate with the actual
post-merge classifier outcome in case of preview drift.

## Files to change

| Status | Path | Change |
| --- | --- | --- |
| New | `Source/Parsek/ReFlyAutoSealPreview.cs` | Preview helper + `ReFlyAutoSealReason` enum + `ReFlyAutoSealPreviewResult` struct + `FormatHumanReadable` |
| Modified | `Source/Parsek/SupersedeCommit.cs` | `CollectRecordingIdsForSafetyGate` -> `internal`; extract shared `ScanReFlySessionStructuralMutations` private helper; new `TryGetFirstReFlySessionStructuralMutationType` internal API |
| Modified | `Source/Parsek/MergeDialog.cs` | Wire preview into the ReFlyAttempt body copy in the 4-arg `ShowTreeDialog` overload; add canary log line |
| New | `Source/Parsek.Tests/ReFlyAutoSealPreviewTests.cs` | Unit tests per reason path + multi-reason composition + null guards + format spec |
| Modified | `CHANGELOG.md` | Entry under 0.9.2 Enhancements |

## Test matrix

### `ReFlyAutoSealPreviewer.Preview` core matrix

Pure unit tests, xUnit. `[Collection("Sequential")]` for shared
`Ledger`, `RecordingStore`, `ParsekScenario` static state. Constructor
calls `ParsekLog.ResetTestOverrides`, `Ledger.ResetForTesting`,
`RecordingStore.ResetForTesting`, `ParsekScenario.ResetInstanceForTesting`.

| Setup | Expected reasons | `FormatHumanReadable` |
| --- | --- | --- |
| `marker == null` | `WillAutoSeal=false`, empty | `null` |
| `liveProvisional == null` | `WillAutoSeal=false`, empty | `null` |
| `marker.TreeId == null` | `WillAutoSeal=false`, empty | `null` |
| `liveProvisional.TreeId != marker.TreeId` | `WillAutoSeal=false`, empty | `null` |
| Idle Re-Fly, no events, vessel landed | empty | `null` |
| Ledger has `ScienceEarning` (Method=Transmitted) tagged on provisional | `[TransmittedScience]` | `"you transmitted science"` |
| Ledger has `ScienceEarning` (Method=Recovered) tagged on provisional | `[RecoveredScience]` | `"you recovered science"` |
| Ledger has `ScienceEarning` tagged on a chain-segment of the provisional | `[TransmittedScience]` (or whichever Method) | proportional |
| Ledger has `ScienceEarning` tagged on a different recording (not in lineage) | empty | `null` |
| Ledger has tombstone-eligible `ScienceEarning` (paired with kerbal death penalty) | empty | `null` |
| Ledger has non-`ScienceEarning` action tagged on provisional (e.g. `FundsEarning`) | empty | `null` |
| Tree has post-RP `Undock` BP in lineage | `[Undocked]` | `"you undocked"` |
| Tree has post-RP `EVA` BP in lineage | `[KerbalEva]` | `"you sent a kerbal on EVA"` |
| Tree has post-RP `JointBreak` BP in lineage | `[PartBrokeOff]` | `"you broke off a part"` |
| Tree has post-RP `Breakup` BP in lineage | `[VesselBrokeUp]` | `"the vessel broke up"` (note: the canned phrase already includes "the vessel" so Format prefixes "you" only when grammatically appropriate; spec at end) |
| Pre-RP BP only (UT < cutoff) | empty | `null` |
| BP not in Re-Fly target lineage (background vessel) | empty | `null` |
| BP in `marker.PreSessionBranchPointIds` (re-spliced from load) | empty | `null` |
| `marker.PreSessionBranchPointIds == null` (legacy marker) | structural skipped, other paths still work | `null` if no other reason |
| Multiple structural BPs of different types | first only (e.g. `[Undocked]` even if Breakup followed) | first phrase only |
| Live vessel `situation == DOCKED` | `[DockedWithAnother]` | `"you docked with another vessel"` |
| Live vessel `situation == ORBITING` | `[StableOrbit]` | `"you reached a stable orbit"` |
| Live vessel `situation == LANDED` | empty | `null` |
| `liveActiveVessel == null` | live-vessel reasons skipped, other paths still work | depends |
| Multi-reason: science + undock | `[TransmittedScience, Undocked]` | `"you transmitted science and undocked"` |
| Multi-reason: science + undock + dock | 3 reasons, ordered (science, struct, terminal) | `"you transmitted science, undocked, and docked with another vessel"` |
| Multi-reason: undock + breakup | first structural only `[Undocked]` | `"you undocked"` |

### `FormatHumanReadable` standalone

| Reasons | Output |
| --- | --- |
| `[]` | `null` |
| `[EarnedScience]` | `"you earned science"` |
| `[TransmittedScience]` | `"you transmitted science"` |
| `[Undocked]` | `"you undocked"` |
| `[VesselBrokeUp]` | `"the vessel broke up"` (special case - phrase starts with "the vessel", no "you" prefix) |
| `[Undocked, KerbalEva]` | `"you undocked and sent a kerbal on EVA"` |
| `[VesselBrokeUp, DockedWithAnother]` | `"the vessel broke up and you docked with another vessel"` (mixed pronoun handled by sub-clause) |
| `[TransmittedScience, Undocked, DockedWithAnother]` | `"you transmitted science, undocked, and docked with another vessel"` |
| `[VesselBrokeUp, TransmittedScience, DockedWithAnother]` | `"the vessel broke up, you transmitted science, and you docked with another vessel"` |

The pronoun-mix is awkward but accurate. Simpler alternative: drop the
"you" prefix entirely and rely on phrasing -> `"transmitted science and
undocked"`, `"the vessel broke up"`. Less natural but unambiguous.
**Decision deferred to user**: pick one of the two styles in §Open
questions before implementation.

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

- **`vessel.situation == ORBITING` proxy correctness**: KSP sets
  `Vessel.Situations.ORBITING` only when both apsides are above the
  body's atmosphere (or surface for airless bodies). The
  `RecordingTree.DetermineTerminalState` override path may still flip
  between SUB_ORBITAL and ORBITING based on atmospheric drag
  predictions, but for a vessel currently in stable orbit (no thrust,
  no atmosphere intersection within current orbit), the live `situation`
  enum and the post-finalize `TerminalStateValue` agree. Edge case: a
  vessel coasting through a high-eccentricity orbit that grazes
  atmosphere may flicker between SUB_ORBITAL and ORBITING; the dialog
  is a one-shot snapshot at click time, so transient flicker is
  acceptable.

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

## Open questions

1. **Pronoun style**: "you transmitted science" vs "transmitted
   science" vs "the recording transmitted science." First-person ("you")
   is most direct but creates pronoun-mix awkwardness when combined
   with subject-led phrases like "the vessel broke up". Three options:
   - **A**: keep "you" prefix; accept mixed-subject sentences.
   - **B**: drop "you", rely on phrasing -> `"transmitted science and
     undocked"`. Less natural but uniform.
   - **C**: rewrite "the vessel broke up" -> "you broke up the vessel"
     so all phrases are "you"-led. Less accurate (vessel breakup is
     usually involuntary).

   Recommend **B** (drop "you"). Implementation defaults to B unless
   user picks otherwise.

2. **`Boarded` / `Recovered` detection**: the preview deliberately
   skips these because they are unreachable in flight. But is there an
   in-flight signal (e.g. a `Boarded` ledger row from an earlier crew
   transfer in the attempt) we should surface? Likely no: the seal for
   those triggers via `IsHardSafetyTerminal` which reads
   `TerminalStateValue`, and the recording wouldn't have a Boarded
   terminal state until finalize.

   Recommend **skip them** (do not surface in dialog). The production
   classifier still seals correctly post-finalize.

3. **Single structural reason vs all structural reasons**: production
   logs only the first BP. Should the dialog list all BP types
   encountered, or just the first?

   Recommend **first only**. The player typically remembers what they
   actively did (undock); cascading consequences (a follow-on breakup)
   would clutter the message.

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
