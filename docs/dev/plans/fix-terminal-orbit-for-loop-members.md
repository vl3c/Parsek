# Fix terminal-orbit line for loop members with no recorded OrbitSegments (v5)

> **v5 changes (since v4 review):**
> - MAJOR-1: section 1.4 `ResolveMapPresenceGhostSource` signature box rewritten to match the actual 11+2 positional/defaulted signature (`isSuppressed`, `alreadyMaterialized`, `allowTerminalOrbitFallback`, `logOperationName`, `ref stateVectorCachedIndex`, `recordingIndex`, `allowSoiGapStateVectorFallback`, `expectedSoiGapBody`). New `acceptTerminalOrbitForLoopSynthesis` parameter is appended as the trailing optional. Example call-site snippet rewritten to match.
> - MAJOR-2: 5th production call site at `GhostMapPresence.cs:7227` (inside `ResolveTrackingStationGhostSourceCore`, the TS-startup wrapper) added to section 2 table. section 1.4 prose updated from "4 caller updates" to "5 production caller updates plus 2 test callers".
> - MINOR-1: section 1.5 / section 2 comment-refresh row updated to call out that the SECOND endpoint-tail branch (5725) is now CONDITIONALLY enabled for same-body loop members via `IsTerminalOrbitSynthesisSafeForLoopMember`, while the FIRST branch (5710) stays suppressed.
> - MINOR-2: section 6 OQ #2 (4-caller `effUT` walk) deleted -- already enumerated in section 1.4.
> - MINOR-3: section 1.4 caller-4 paragraph trimmed to a one-line cross-reference to section 1.6 (the helper widening); section 1.6 carries the full code box.
> - MINOR-4: section 3.3 gains `TryResolveTerminalFallbackMapOrbitUpdate_NoSegmentLoopMemberAtZeroSegSplit_PassesAcceptFlagThrough` (covers the new plumbing path end-to-end).
> - OQ #1 marked RESOLVED (idx 18 = `Orbiting`, persisted phase `(TrajectoryPoint, Kerbin)`).
> - OQ #4 collapsed to a one-line note in section 1.1.
> - OQ #6 marked RESOLVED (`InternalsVisibleTo` present at `Source/Parsek/Properties/AssemblyInfo.cs:5`).
> - section 0.5 CRITICAL-1 verification step closed out: idx 18 = `terminal=Orbiting`, no SubOrbital widening required.
>
> ## v4 PENDING CHANGES (historical -- already folded; kept for diff readability)
>
> Source: clean-context Opus review of v3 (`tasks/ab774e1d69c2d66db.output`,
> may be wiped on context compaction; full content captured below).
>
> ### CRITICAL-1 - IsTerminalMapPresenceRegion rejects SubOrbital; may apply to #18
>
> `IsTerminalStateEligibleForTerminalOrbitMapPresence`
> (`GhostMapPresence.cs:3455-3460`) admits only `Orbiting | Docked | (null)`.
> Rejects `SubOrbital`. Reviewer's evidence cited recording `87b341` as
> `terminal=SubOrbital`. The playtest log line for the user's actual idx 18
> explicitly says `terminal=Orbiting` (atmospheric marker line). So this is
> likely a reviewer recording-misidentification. **v4 action:** before code,
> confirm idx 18's TerminalState by inspecting the save (or the BackfillEndpointDecision
> log line for the exact idx). If `Orbiting`, v3 design works as-is. If
> `SubOrbital`, either widen the eligibility check OR document the scope
> limit.
>
> ### MAJOR-1 - section 1.5 prose mischaracterizes when historical body rotation enters
>
> v3 says "this is the path that does NOT use historical body rotation."
> Actually `OrbitSeedResolver.TryDeriveTailOrbitSeed`
> (`GhostMapPresence.cs:3842`, `OrbitSeedResolver.cs:151`) DOES use
> historical body rotation for `TailSeedUse.MapPresence` via
> `OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocity`. Conclusion
> still correct (shift-safe for same-body) but the reason is different:
> historical rotation enters at SEED CONSTRUCTION time to convert recorded
> body-fixed lat/lon/alt -> inertial elements, but once those inertial
> elements are populated (LAN/AoP/inc), they are body-rotation-frame
> independent and `SetOrbit(epoch + shift)` propagates them inertially.
> Mean anomaly advances correctly under the shift.
>
> **v4 action:** rewrite section 1.5 to acknowledge historical-rotation use, then
> state the actual reason it's shift-safe. Cross-reference and refresh the
> in-code comment at `GhostMapPresence.cs:5687-5705` so future readers
> don't conclude the relaxation contradicts the existing #571/#584
> invariant.
>
> ### MAJOR-2 - Caller 1 at ParsekPlaybackPolicy.cs:1045 has no effUT in scope
>
> v3 section 1.4 says "all 4 callers compute effUT". Wrong. Caller 1 is the
> initial-create-on-first-loop-entry path; it passes
> `startUT = evt.Trajectory.StartUT` as currentUT, with no `loopUnits`, no
> `effUT`, no `loopEpochShiftSeconds`.
>
> **v4 action:** explicitly enumerate caller 1 as a "pass false" site in
> section 1.4 + section 2 table. It is conceptually similar to the TS-startup-create
> path that already passes false.
>
> ### MAJOR-3 - TryResolveTerminalFallbackMapOrbitUpdate needs signature widening
>
> Caller 4 (`ParsekPlaybackPolicy.cs:1867`) lives INSIDE
> `TryResolveTerminalFallbackMapOrbitUpdate` which takes only `double
> currentUT`. The call site at line 1495 has both `effUT` and
> `loopEpochShiftSeconds`, but the helper signature doesn't carry them
> through to the resolver call.
>
> **v4 action:** add to section 2 table:
> ```
> ParsekPlaybackPolicy.cs:1852 TryResolveTerminalFallbackMapOrbitUpdate:
>   add `double loopEpochShiftSeconds` parameter (or pre-compute
>   `bool acceptTerminalOrbitForLoopSynthesis` at the call site and pass
>   it down)
> ```
>
> ### MINOR-1 - InternalsVisibleTo verification
>
> v3 section 1.1 says "verify". v4 action: grep `InternalsVisibleTo` in
> `Source/Parsek/Properties/AssemblyInfo.cs` and confirm `Parsek.Tests` is
> listed. Almost certainly is, given dozens of existing `internal static`
> tested helpers.
>
> ### MINOR-2 - IsTrackingTerminalOrbitBody redundancy is muddy
>
> v3 section 1.1 introduces it inside the synthesizer; the outer `IsTerminalOrbitSynthesisSafeForLoopMember(rec)`
> already covers same-body at the call site. The two DO compare different
> things (inner: `endpointBodyName` from persisted-decision vs
> `traj.TerminalOrbitBody`; outer: `rec.Points[last].bodyName` vs
> `rec.TerminalOrbitBody`). For #18 both agree, but they could in theory
> diverge across `RefreshEndpointDecision` calls.
>
> **v4 action:** add one sentence to section 1.1 noting "the inner check guards
> against persisted body diverging from last-point body across
> `RefreshEndpointDecision` calls."
>
> ### MINOR-3 - Predicate should defensively check TerminalOrbitSemiMajorAxis > 0
>
> A recording could have `TerminalOrbitBody="Kerbin"` but
> `TerminalOrbitSemiMajorAxis=0` (uninitialized). The `endpoint-terminal-orbit`
> source path requires `HasRecordedTerminalOrbit(traj)` downstream, but the
> safety predicate could emit the "loop-accept" log line for a recording
> that has no actual orbit elements.
>
> **v4 action:** add `traj.TerminalOrbitSemiMajorAxis > 0` to
> `IsTerminalOrbitSynthesisSafeForLoopMember`.
>
> ### Additional flag - in-game test callers of ResolveMapPresenceGhostSource
>
> `RuntimeTests.cs:1203` and `:4190` also call `ResolveMapPresenceGhostSource`.
> v3 omits these from the section 2 table. Default-false parameter means they
> remain byte-identical, but the call sites still need re-inspection.
>
> **v4 action:** note in section 2 table; if either RuntimeTest constructs and
> passes a non-zero loop shift, plumb the new param accordingly.
>
> ### v4 additional regression test
>
> Add to section 3.3: "non-loop caller with default false param on a no-segment
> Orbiting recording still returns None" -- pins that the relaxation
> default stays safe.
>
> ---

Status: DRAFT v5. v4 body fully revised per the v5 changes at the top of this file (v4 PENDING CHANGES are historical, kept for diff readability). v2 (and v1) failed review for the same fundamental reason: both proposed relaxing the SECOND endpoint-tail BRANCH but the synthesizer + create-path gates rejected the target case earlier. v3/v4/v5 enlarges scope: relax BOTH halves of the synthesizer's inner gate AND patch the create-path resolver, with a fixed same-body predicate that no longer collapses to a tautology. v5 corrects the create-path resolver signature box to the actual 11+2-param shape, adds the missed TS-startup call site at `GhostMapPresence.cs:7227`, sharpens the comment-refresh wording, and closes resolved open questions.

Branch: `fix-terminal-orbit-for-loop-members` (off `origin/main` at `9b6d69c2`).

**Honest scope warning.** v1 was framed as "small fix"; the reviewer pass on v2
showed the real fix is substantially bigger:
- 6 call sites of `TryResolveEndpointTailForMapPresence` (not 3), three of them in the
  proto-vessel CREATE path.
- The synthesizer's `endpointPhase == OrbitSegment` gate must also be relaxed (the
  reviewer pinned Open Question #1 with playtest-log evidence: a no-OrbitSegment
  recording always gets `EndpointPhase = TrajectoryPoint`).
- `ResolveMapPresenceGhostSource` (the shared create-path resolver, called from 5
  production callers: 4 in `ParsekPlaybackPolicy.cs` plus the TS-startup wrapper
  at `GhostMapPresence.cs:7227`) must accept a new "loop-aware terminal-orbit
  acceptance" hint.
- The same-body predicate must compare against `traj.TerminalOrbitBody` directly
  (not via `TryGetPreferredEndpointBodyName`, which returns the last-point body for
  no-OrbitSegment recordings and would make the predicate tautological).

This is no longer a small fix. The user previously chose "leave as-is" for this
regression twice. Re-read section 5 before committing to ship.

Revision history:
- 2026-05-28 v1 draft -- flipped outer SECOND-branch gate; was a no-op for #18.
- 2026-05-28 v2 -- relaxed inner gate's source half; still a no-op because
  the persisted-phase half AND the create-path resolver both reject.
- 2026-05-28 v5 (this) -- folded fourth clean-context review:
  - MAJOR-1: section 1.4 `ResolveMapPresenceGhostSource` signature corrected to the
    actual 11+2-param shape (`isSuppressed`, `alreadyMaterialized`,
    `allowTerminalOrbitFallback`, `logOperationName`,
    `ref stateVectorCachedIndex`, `out segment`, `out stateVectorPoint`,
    `out skipReason`, `recordingIndex`, `allowSoiGapStateVectorFallback`,
    `expectedSoiGapBody`). New `acceptTerminalOrbitForLoopSynthesis` flag
    appended as the trailing optional. Example call-site snippet rewritten.
  - MAJOR-2: 5th production call site at `GhostMapPresence.cs:7227` (inside
    `ResolveTrackingStationGhostSourceCore`, the TS-startup wrapper) added
    to section 2 table. section 1.4 prose updated from "4 caller updates" to "5 production
    caller updates plus 2 test callers".
  - MINOR-1: section 1.5 / section 2 comment-refresh row updated to call out that the
    SECOND endpoint-tail branch (5725) is now CONDITIONALLY enabled for
    same-body loop members via `IsTerminalOrbitSynthesisSafeForLoopMember`,
    while the FIRST branch (5710) stays suppressed.
  - MINOR-2: section 6 OQ #2 deleted (already enumerated in section 1.4).
  - MINOR-3: section 1.4 caller-4 paragraph trimmed to a one-line cross-reference
    to section 1.6.
  - MINOR-4: section 3.3 gains
    `TryResolveTerminalFallbackMapOrbitUpdate_NoSegmentLoopMemberAtZeroSegSplit_PassesAcceptFlagThrough`
    (covers the new plumbing path end-to-end).
  - OQ #1 RESOLVED (idx 18 = `Orbiting`, persisted phase
    `(TrajectoryPoint, Kerbin)`); OQ #4 collapsed to a one-line note in section 1.1;
    OQ #6 RESOLVED (`InternalsVisibleTo` present at
    `Source/Parsek/Properties/AssemblyInfo.cs:5`).
  - section 0.5 CRITICAL-1 closed: idx 18 = `Orbiting`, no SubOrbital widening
    required.
- 2026-05-28 v4 -- folded second clean-context review:
  - CRITICAL-1: pre-implementation verification step added (read playtest log
    line for idx 18 / `BackfillEndpointDecision`; widen
    `IsTerminalStateEligibleForTerminalOrbitMapPresence` defensively only
    if `SubOrbital`). See section 0.4 / section 3.4.
  - MAJOR-1: section 1.5 prose rewritten -- historical body rotation DOES enter at
    seed CONSTRUCTION time via `OrbitSeedResolver.TryDeriveTailOrbitSeed`,
    but inertial elements + Planetarium `OrbitalFrame` make propagation
    body-rotation-frame independent. `Orbit.SetOrbit` line 488 / `Orbit.Init`
    cited from disassembly. Comment-refresh task added at
    `GhostMapPresence.cs:5687-5705`.
  - MAJOR-2: Caller 1 at `ParsekPlaybackPolicy.cs:1045` reclassified as a
    "pass false" site (no `effUT` at startup).
  - MAJOR-3: `TryResolveTerminalFallbackMapOrbitUpdate` (line 1852) signature
    widened with `double loopEpochShiftSeconds`; line 1495 call site passes
    the shift in.
  - MINOR-1: `InternalsVisibleTo("Parsek.Tests")` grep step added to section 6.
  - MINOR-2: `IsTrackingTerminalOrbitBody` rationale documented in section 1.1
    (defense in depth across `RefreshEndpointDecision` calls).
  - MINOR-3: `traj.TerminalOrbitSemiMajorAxis > 0` guard added to
    `IsTerminalOrbitSynthesisSafeForLoopMember`.
  - NEW: `RuntimeTests.cs:1203, 4190` resolver call sites added to section 2 table.
  - NEW: `ResolveMapPresenceGhostSource_NonLoopCallerOnNoSegmentOrbitingRecording_StillReturnsNone`
    regression test added to section 3.3.
  - NEW: KSP disassembly verification block appended to section 7.
- 2026-05-28 v3 -- folded clean-context review:
  - CRITICAL #1: Open Question #1 answer pinned. The persisted-phase gate is
    ALSO blocking. Plan now relaxes both halves behind
    `acceptTerminalOrbitSource`.
  - CRITICAL #2: 3 missed call sites added: `ResolveMapPresenceGhostSource`
    at lines 4215 and 4435 (the create path), plus the diagnostic at line
    4028 (read-only; no change needed). Create-path relaxation required.
  - MAJOR-1: signature corrected (`private static`, `IPlaybackTrajectory`,
    `TailDerivedOrbitSeed`). Promoted to `internal static` for testability.
  - MAJOR-2: `IsTerminalMapPresenceRegion` precondition explicitly validated.
  - MAJOR-3: same-body predicate compares against `traj.TerminalOrbitBody`
    DIRECTLY, not against `TryGetPreferredEndpointBodyName` (which collapses
    to a tautology for no-segment recordings).
  - MAJOR-4: `RefreshEndpointDecision` clobbering noted; safe by inspection
    today.
  - MAJOR-5: plumbing of loop-shift hint through `ResolveMapPresenceGhostSource`
    + its 4 callers spelled out.

References:
- `Source/Parsek/GhostMapPresence.cs`:
  - `EndpointTailAllowedInTrackingStationUpdate` line 108 -- outer gate
  - `TryResolveEndpointTailForMapPresence` around lines 3800-3920 (`private static`,
    takes `IPlaybackTrajectory traj`, returns `TailDerivedOrbitSeed`) -- the
    synthesizer + its dual inner gate at 3868-3891
  - 6 callers: lines 4028 (diagnostic), 4215 (create -- override), 4435 (create --
    no-segment fallback), 5596 (refresh -- rescue), 5710 (refresh -- FIRST),
    5725 (refresh -- SECOND)
  - `ResolveMapPresenceGhostSource` -- the create-path resolver called by 5
    production callers: `ParsekPlaybackPolicy.cs:1045, 1343, 1656, 1867` (4
    flight callers) plus `GhostMapPresence.cs:7227` (TS-startup wrapper
    `ResolveTrackingStationGhostSourceCore`). 2 additional test callers in
    `RuntimeTests.cs:1203, 4190`.
  - `RefreshTrackingStationGhosts` -- the update path; only iterates
    `vesselsByRecordingIndex` (already-created ghosts), so patching just here is
    insufficient for #18 which is never created in the first place
  - `ApplyOrbitToVessel` -- already shift-aware; no change needed
- `Source/Parsek/RecordingEndpointResolver.cs`:
  - `TryGetEndpointAlignedOrbitSeed` lines 261-400 -- two source paths
  - `TryGetPersistedEndpointDecision` -- returns `(TrajectoryPoint, lastBody)` for
    a no-OrbitSegment recording (NOT `(OrbitSegment, body)`); confirmed in the
    reviewer's playtest log evidence (`logs/2026-05-28_2002_ts-rendering-validate`,
    recording `87b341` shows `after=(TrajectoryPoint, Kerbin)`)
  - `TryComputeEndpointDecisionFromData` lines 539-593 -- the source of the
    persisted phase decision; both OrbitSegment-yielding branches
    (`TryGetTerminalOrbitAlignedOrbitDecision` line 737,
    `ShouldUseOrbitEndpointByHeuristic` line 670) gate on `OrbitSegments.Count > 0`
  - `RefreshEndpointDecision` lines 90-141 -- overwrites persisted phase; safe
    by inspection today, see section 6 OQ #4
- `Source/Parsek/Recording.cs`:
  - line 161 `SegmentBodyName`
  - line 164 `StartBodyName`
  - line 195-196 `TerminalOrbitEccentricity` / `TerminalOrbitSemiMajorAxis`
  - line 201 `TerminalOrbitBody`
  - line 211 `EndpointBodyName`
- Playtest log `logs/2026-05-28_2002_ts-rendering-validate/KSP.log`:
  - Line 11994 summary: idx 18 falls in the `noOrbit=4` bucket
  - No `recIndex=18` line in `tracking-station-startup` source-resolve logs
    (filtered before source-resolve)
  - The reviewer's added evidence: every no-OrbitSegment recording in the save
    shows `BackfillEndpointDecision: ... after=(TrajectoryPoint, <body>)`

---

## 0. The problem (corrected diagnosis, v3)

A looped mission's loop member that is `terminal=Orbiting` with ZERO recorded
`OrbitSegment`s renders as a point marker only. Three coupled blockers, all of
which must be addressed:

### 0.1 Blocker 1: synthesizer's source-half gate

`TryResolveEndpointTailForMapPresence` (`GhostMapPresence.cs:3875`) requires
`endpointSeedSource == "endpoint-segment"`. For a no-segment recording the seed
resolver successfully produces a seed from `traj.TerminalOrbit*` but tags it
`"endpoint-terminal-orbit"`. The synthesizer rejects.

### 0.2 Blocker 2: synthesizer's persisted-phase-half gate

The same synthesizer also requires `endpointPhase == OrbitSegment`. For a
no-segment recording the persisted phase is **`TrajectoryPoint`**, not
`OrbitSegment`. Confirmed:

- `TryComputeEndpointDecisionFromData` (`RecordingEndpointResolver.cs:539-593`)
  walks a cascade. Both OrbitSegment-yielding branches gate on
  `OrbitSegments.Count > 0`; for a no-segment recording the cascade falls
  through to `(TrajectoryPoint, lastPointBody)`.
- Playtest log evidence (reviewer-pinned): `BackfillEndpointDecision: ...
  after=(TrajectoryPoint, Kerbin)` for every no-OrbitSegment recording in
  the save.

So even with the source-half gate relaxed (v2's change), the synthesizer
still rejects.

### 0.3 Blocker 3: create-path resolver rejects upstream

`ResolveMapPresenceGhostSource` (called from 4 `ParsekPlaybackPolicy` flight
callers) calls `TryResolveEndpointTailForMapPresence` at lines 4215 and 4435.
For a no-OrbitSegment recording at TS-startup it returns `source = None`, so
the proto-vessel ghost is never CREATED. `RefreshTrackingStationGhosts` (the
update path v1 and v2 proposed to patch) only iterates already-created ghosts
via `vesselsByRecordingIndex`; there is nothing to update. Patching the update
path alone is dead code.

### 0.4 What "right" looks like (unchanged)

Recording #18 (Kerbin-return-after-Mun-takeoff) shows a stable Kerbin terminal-
orbit line in TS, with the proto-vessel icon clamped to it, shifted into the
live frame by `tsLoopEpochShift`. Same architectural rule applies as today:
non-orbital phases drawn by the atmospheric marker; orbital phase drawn by
the proto-vessel orbit line; cross-body terminal recordings still suppressed
(the 181 Mm bug class stays fixed).

### 0.5 Pre-implementation verification (CRITICAL-1, RESOLVED in v5)

`IsTerminalStateEligibleForTerminalOrbitMapPresence`
(`GhostMapPresence.cs:3455-3460`) admits only `Orbiting | Docked | (null)`,
rejecting `SubOrbital`. The v3 review's evidence cited recording `87b341`
which is `terminal=SubOrbital`; however the user's actual #18 playtest log
line (`logs/2026-05-28_2002_ts-rendering-validate/KSP.log:12534`) reads
`terminal=Orbiting`, with persisted phase `(TrajectoryPoint, Kerbin)`. The
reviewer misidentified the recording.

Resolution: idx 18 is `terminal=Orbiting`. The v5 design works as-is. No
widening of `IsTerminalStateEligibleForTerminalOrbitMapPresence` is needed
for this fix; the `SubOrbital` widening is a defensive future option only
and is out of scope here.

---

## 1. The relaxation, v3

### 1.1 Synthesizer changes (`TryResolveEndpointTailForMapPresence`)

**Promote to `internal static`** (currently `private`) so xUnit can test it.
The existing `InternalsVisibleTo("Parsek.Tests")` already covers it; verify.

**Signature (actual, corrected from v2):**
```csharp
internal static bool TryResolveEndpointTailForMapPresence(
    IPlaybackTrajectory traj,
    double currentUT,
    OrbitSegment? selectedSegment,
    bool terminalMapPresenceRegion,
    out OrbitSegment endpointTailSegment,
    out TailDerivedOrbitSeed tailSeed,
    out string detail,
    bool acceptTerminalOrbitSource = false)  // NEW (default false = today's behavior)
```

**Inner gate (lines 3868-3891), relaxed:**

```csharp
bool sourceAccepted =
    string.Equals(endpointSeedSource, "endpoint-segment", StringComparison.Ordinal)
    || (acceptTerminalOrbitSource
        && string.Equals(endpointSeedSource, "endpoint-terminal-orbit",
            StringComparison.Ordinal));

bool persistedPhaseAccepted =
    (endpointPhase == RecordingEndpointPhase.OrbitSegment
     && string.Equals(endpointBodyName, preferredEndpointBody, StringComparison.Ordinal))
    || (acceptTerminalOrbitSource
        && endpointPhase == RecordingEndpointPhase.TrajectoryPoint
        && IsTrackingTerminalOrbitBody(traj, endpointBodyName));

if (!sourceAccepted || !persistedPhaseAccepted)
{
    // ... existing decline path
    return false;
}
```

The new `IsTrackingTerminalOrbitBody(traj, lastPointBody)` helper (pure)
returns true when `lastPointBody` matches `traj.TerminalOrbitBody` (i.e., the
recording's last sampled point is in the same body as the terminal orbit's
reference body). This is the SAME-BODY predicate per section 1.3; we apply it here
inside the synthesizer so the persisted-phase relaxation is gated by it, not
just the outer caller's gate. Defense in depth.

Defense in depth: the inner check compares `endpointBodyName` (from
`TryGetPersistedEndpointDecision`) against `traj.TerminalOrbitBody`, while
the outer `IsTerminalOrbitSynthesisSafeForLoopMember` compares
`rec.Points[last].bodyName` against `rec.TerminalOrbitBody`. For #18 both
bodies agree; the inner check guards against persisted body diverging from
last-point body across `RefreshEndpointDecision` calls.

`RefreshEndpointDecision` clobber risk (former OQ #4): verified by grep
that today it's only called in load/save and supersede paths, never on
loop-tracking trajectories. The persisted phase relaxation is therefore
safe against in-flight overwrites; the inner same-body check above
provides the belt-and-braces guarantee should a future caller appear.

The recording's `IsTerminalMapPresenceRegion` precondition stays (line 3804-
3805). It includes the
`IsTerminalStateEligibleForTerminalOrbitMapPresence` eligibility check
(`GhostMapPresence.cs:3455-3460`); the user's specific #18 should pass via
`terminal=Orbiting` per the playtest log. Widening that eligibility to
include `SubOrbital` is cited in section 0.5 as a defensive future option only.
For a loop member, `effUT` near the recording's end satisfies the region
check as long as `ResolveGhostActivationStartUT(traj)` is `<= effUT`
(verified in testing).

### 1.2 Same-body predicate (the actual fix, MAJOR-3)

```csharp
/// <summary>
/// Pure: should the terminal-orbit synthesis be allowed for this recording
/// under a non-zero loop epoch shift?
///
/// Compares the recording's last sampled point's body against the
/// terminal-orbital reference body (Recording.TerminalOrbitBody DIRECTLY,
/// NOT TryGetPreferredEndpointBodyName which for a no-OrbitSegment
/// recording would return the last-point body and make this predicate
/// tautological).
///
/// Same-body terminals (recording ended in body B's orbit having last
/// sampled in body B's SOI) are safe: the synthesized orbit is around the
/// same body the recording is anchored to at its end. Cross-body terminals
/// (last sampled in body A but TerminalOrbitBody = B) are the 181 Mm bug
/// class and remain suppressed.
/// </summary>
internal static bool IsTerminalOrbitSynthesisSafeForLoopMember(Recording rec)
{
    if (rec == null) return false;
    if (rec.Points == null || rec.Points.Count == 0) return false;
    string terminalBody = rec.TerminalOrbitBody;
    if (string.IsNullOrEmpty(terminalBody)) return false;
    // Defensive: a recording could have TerminalOrbitBody non-empty but
    // TerminalOrbitSemiMajorAxis = 0 (uninitialised). The downstream
    // endpoint-terminal-orbit source path requires HasRecordedTerminalOrbit;
    // suppress the "loop-accept" log line for such recordings here so the
    // predicate's truth value matches downstream behaviour.
    if (rec.TerminalOrbitSemiMajorAxis <= 0) return false;
    string lastPointBody = rec.Points[rec.Points.Count - 1].bodyName;
    if (string.IsNullOrEmpty(lastPointBody)) return false;
    return string.Equals(lastPointBody, terminalBody, StringComparison.Ordinal);
}
```

For #18: last point body = "Kerbin" (Kerbin-return arc finishes in Kerbin
orbit), `TerminalOrbitBody = "Kerbin"`. Match. Synthesize.

For a hypothetical cross-body case (recording last sampled in "Mun"
mid-approach but TerminalOrbitBody = "Kerbin" because of broken recording
tail): mismatch. Suppress. The 181 Mm bug class stays fixed.

### 1.3 Outer gates (`RefreshTrackingStationGhosts`)

Update-path call sites at lines 5596 (rescue), 5710 (FIRST), 5725 (SECOND).
Replace single `endpointTailAllowed` flag with:

```csharp
bool endpointTailOverrideAllowed =
    EndpointTailAllowedInTrackingStationUpdate(tsLoopEpochShift);    // shift == 0
bool acceptTerminalOrbitForLoopSynthesis =
    tsLoopEpochShift != 0.0
    && IsTerminalOrbitSynthesisSafeForLoopMember(rec);
```

- **Line 5596 (rescue):** pass `acceptTerminalOrbitSource:
  acceptTerminalOrbitForLoopSynthesis`. Non-loop members byte-identical (the
  predicate's `shift != 0` check returns false). Same-body loop members get
  the rescue.
- **Line 5710 (FIRST -- override):** pass `acceptTerminalOrbitSource: false`.
  The override path is the actual 181 Mm bug class; KEEP suppressed for loop
  members. Byte-identical.
- **Line 5725 (SECOND -- fallback):** pass `acceptTerminalOrbitSource:
  acceptTerminalOrbitForLoopSynthesis`. Outer branch condition becomes
  `(endpointTailOverrideAllowed || acceptTerminalOrbitForLoopSynthesis)`.

### 1.4 Create-path plumbing (CRITICAL #2 from earlier review; signature corrected in v5)

The create path is the load-bearing site for the user's #18 case. Without
this change, the proto-vessel is never created and section 1.3 is dead code.

**`ResolveMapPresenceGhostSource` signature change:** append a new trailing
optional parameter `bool acceptTerminalOrbitForLoopSynthesis = false` AFTER
all existing parameters (default false preserves byte-identical non-loop
behavior at every existing call site).

Actual current signature at `GhostMapPresence.cs:4046-4059`:

```csharp
internal static TrackingStationGhostSource ResolveMapPresenceGhostSource(
    IPlaybackTrajectory traj,
    bool isSuppressed,
    bool alreadyMaterialized,
    double currentUT,
    bool allowTerminalOrbitFallback,
    string logOperationName,
    ref int stateVectorCachedIndex,
    out OrbitSegment segment,
    out TrajectoryPoint stateVectorPoint,
    out string skipReason,
    int recordingIndex = -1,
    bool allowSoiGapStateVectorFallback = false,
    string expectedSoiGapBody = null,
    bool acceptTerminalOrbitForLoopSynthesis = false)   // NEW (trailing optional)
```

Returns `TrackingStationGhostSource` (NOT `bool`). 11 positional parameters
followed by 2 defaulted optionals; the new flag becomes the 3rd defaulted
optional. Appending at the tail means every existing call site stays
byte-identical without touching its argument list.

Pass the new arg to both `TryResolveEndpointTailForMapPresence` calls inside
the body (lines 4215 and 4435).

**5 production caller updates plus 2 test callers** (the count corrects the
v3/v4 "4 callers" wording):

- Production callers in `ParsekPlaybackPolicy.cs`: lines 1045, 1343, 1656,
  1867.
- Production caller in `GhostMapPresence.cs:7227` inside
  `ResolveTrackingStationGhostSourceCore` (the TS-startup wrapper invoked
  by `CreateGhostVesselsFromCommittedRecordings`).
- Test callers in `RuntimeTests.cs`: lines 1203 and 4190 (default-false
  param keeps them byte-identical; still inspect to confirm neither passes
  a non-zero loop shift).

Caller 1 (`ParsekPlaybackPolicy.cs:1045`) is the initial-create-on-first-loop-
entry path: it passes `evt.Trajectory.StartUT` as `currentUT` with NO loop
bookkeeping yet -- no `loopUnits`, no `effUT`, no `loopEpochShiftSeconds`.
Treat it the same as the TS-startup-create path: pass
`acceptTerminalOrbitForLoopSynthesis: false`. The recording's proto-vessel
will be created on a subsequent loop-aware refresh tick once `effUT` is in
scope, not on this initial pass.

Callers 2 and 3 (`ParsekPlaybackPolicy.cs:1343, 1656`) are the loop-aware
flows that already compute `effUT` via `ResolveMapPresenceSampleUT`. Each
computes `loopEpochShiftSeconds = currentUT - effUT`. Pass to the resolver
using the actual call shape (positional 11 + the new trailing named
optional):

```csharp
double pendingLoopShift = currentUT - effUT;
bool acceptTerminalOrbit =
    pendingLoopShift != 0.0
    && IsTerminalOrbitSynthesisSafeForLoopMember(rec);

TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
    traj,
    isSuppressed,
    alreadyMaterialized,
    currentUT,
    allowTerminalOrbitFallback,
    logOperationName,
    ref stateVectorCachedIndex,
    out var segment,
    out var stateVectorPoint,
    out var skipReason,
    recordingIndex: recIndex,
    allowSoiGapStateVectorFallback: soiGapAllowed,
    expectedSoiGapBody: gapBody,
    acceptTerminalOrbitForLoopSynthesis: acceptTerminalOrbit);
```

Caller 4 (`ParsekPlaybackPolicy.cs:1867`) lives INSIDE
`TryResolveTerminalFallbackMapOrbitUpdate`; its signature is widened to
carry the loop shift through (see section 1.6).

Caller 5 -- TS-startup wrapper at `GhostMapPresence.cs:7227`
(`ResolveTrackingStationGhostSourceCore`, invoked by
`CreateGhostVesselsFromCommittedRecordings`): pass
`acceptTerminalOrbitForLoopSynthesis: false`. This wrapper is NOT
loop-aware -- accepting the relaxed source at startup without a shift would
seed the orbit at raw recorded UTs (the wrong-position class PR #967
already documented). Recording #18's proto-vessel will be created on the
FIRST loop-aware refresh tick after TS entry (within
`LifecycleCheckIntervalSec = 2.0s`), not at startup.

### 1.6 `TryResolveTerminalFallbackMapOrbitUpdate` signature widening (MAJOR-3)

The helper around line 1852 (`TryResolveTerminalFallbackMapOrbitUpdate`)
currently takes only `double currentUT`. Its body calls
`ResolveMapPresenceGhostSource` at line 1867. The call site at line 1495 has
both `effUT` and `loopEpochShiftSeconds` in scope.

**v4 action:** widen the helper signature with a `double loopEpochShiftSeconds`
parameter, compute the flag inside, and pass it through:

```csharp
private bool TryResolveTerminalFallbackMapOrbitUpdate(
    Recording rec,
    IPlaybackTrajectory traj,
    double currentUT,
    double loopEpochShiftSeconds,
    out ...)
{
    bool acceptTerminalOrbitForLoopSynthesis =
        loopEpochShiftSeconds != 0.0
        && IsTerminalOrbitSynthesisSafeForLoopMember(rec);

    if (!ResolveMapPresenceGhostSource(traj, currentUT,
            out var source, out var ept, out var skipReason,
            acceptTerminalOrbitForLoopSynthesis:
                acceptTerminalOrbitForLoopSynthesis))
    { ... }
}
```

Update the line 1495 call site to pass `loopEpochShiftSeconds` (already in
scope) through to the helper.

### 1.5 Orientation under loop shift (CRITICAL #2 from v1 review, corrected per MAJOR-1)

The v3 wording "this is the path that does NOT use historical body rotation"
was wrong. `OrbitSeedResolver.TryDeriveTailOrbitSeed` (called from
`GhostMapPresence.cs:3842`, see `OrbitSeedResolver.cs:151`) DOES use
historical body rotation for `TailSeedUse.MapPresence` via
`OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocity`. The conclusion
(shift-safe for same-body) is still correct, but the REASON is different.

The actual reason the `endpoint-terminal-orbit` source is shift-safe under
the same-body invariant:

1. **Historical body rotation enters at SEED CONSTRUCTION time.** It
   converts recorded body-fixed lat/lon/alt + recorded velocity to inertial
   orbital elements (`Inclination`, `LAN`, `ArgumentOfPeriapsis`,
   `MeanAnomalyAtEpoch`, `Epoch`). This is correct only when the historical
   rotation phase of the body at the recording's terminal UT is used.
2. **Once those inertial elements are populated, they are body-rotation-
   frame independent.** LAN / inclination / argument of periapsis live in
   Planetarium's inertial frame.
3. **`Orbit.SetOrbit(inc, e, sma, lan, argPe, mEp, epoch + shift, body)`
   calls `Init()`**, which builds `OrbitFrame` via
   `Planetarium.CelestialFrame.OrbitalFrame(LAN, inclination,
   argumentOfPeriapsis, ...)`. That's the Planetarium INERTIAL frame --
   not body-rotation-aware. See `Orbit.SetOrbit` body in
   `Assembly-CSharp.dll`, lines 488-499; `Orbit.Init` builds the frame via
   `Planetarium.CelestialFrame.OrbitalFrame`.
4. **The shift only advances the epoch.** Mean anomaly advances correctly
   under `meanMotion * (UT - epoch)` because the shift is added to BOTH
   the epoch and the propagation UT.

Concrete tie-in:
- Seed source: `traj.TerminalOrbit{Inclination,Eccentricity,SemiMajorAxis,LAN,
  ArgumentOfPeriapsis,MeanAnomalyAtEpoch,Epoch}` (inertial-frame elements
  derived using historical body rotation at the recording's terminal UT,
  then frozen).
- Application: `ApplyOrbitToVessel` -> `orb.SetOrbit(..., epoch +
  loopEpochShiftSeconds, body)` (verified `GhostMapPresence.cs:6829-6837`).
  Shift added to epoch; inertial elements unchanged.

Asymmetry: the `endpoint-segment` path also uses historical body rotation via
`OrbitReseed` BUT its OrbitSegment payload tends to encode a different
relationship to the recorded points (covering segment over a UT span) and is
the path that exhibits the 181 Mm cross-body bug; it stays suppressed for
loop members via the FIRST branch's unchanged gate.

The existing in-code comment at `GhostMapPresence.cs:5687-5705` currently
asserts forcefully that "both endpoint-tail branches are suppressed for
loop members". v5 partially contradicts that for the same-body subset.

The section 2 comment-refresh task MUST explicitly call out that:
- The FIRST endpoint-tail branch (`GhostMapPresence.cs:5710` -- covering-
  segment OVERRIDE) stays unconditionally suppressed for loop members.
  This is the 181 Mm bug class and the suppression remains the protective
  invariant.
- The SECOND endpoint-tail branch (`GhostMapPresence.cs:5725` -- no-covering-
  segment FALLBACK) is now CONDITIONALLY enabled for loop members when
  `IsTerminalOrbitSynthesisSafeForLoopMember(rec)` returns true (same-body
  terminal-orbit elements), and stays suppressed for cross-body loop
  terminals. Refresh the comment to state this and cross-link to section 1.5 of
  this plan for the inertial-frame propagation rationale.

---

## 2. Code changes (precise summary)

| File | Line | Change |
|---|---|---|
| `GhostMapPresence.cs` | 108 | `EndpointTailAllowedInTrackingStationUpdate` unchanged. |
| `GhostMapPresence.cs` | 3808 | `TryResolveEndpointTailForMapPresence`: promote to `internal static`, add `acceptTerminalOrbitSource = false` param. |
| `GhostMapPresence.cs` | 3868-3891 | Relax both halves of the inner gate per section 1.1. |
| `GhostMapPresence.cs` | new helper | `IsTerminalOrbitSynthesisSafeForLoopMember(Recording)` per section 1.2. |
| `GhostMapPresence.cs` | new helper | `IsTrackingTerminalOrbitBody(traj, body)` per section 1.1. |
| `GhostMapPresence.cs` | 4028 | Diagnostic-only; no change. |
| `GhostMapPresence.cs` | 4215, 4435 | Pass new `acceptTerminalOrbitForLoopSynthesis` through. |
| `GhostMapPresence.cs` | `ResolveMapPresenceGhostSource` | Add `acceptTerminalOrbitForLoopSynthesis = false` param. |
| `GhostMapPresence.cs` | 5596 (rescue), 5710 (FIRST), 5725 (SECOND) | Apply split gates per section 1.3. |
| `GhostMapPresence.cs` | 5687-5705 | Comment refresh: state explicitly that the SECOND endpoint-tail branch (5725) is now CONDITIONALLY enabled for same-body loop members via `IsTerminalOrbitSynthesisSafeForLoopMember`, while the FIRST branch (5710) stays unconditionally suppressed (181 Mm bug class). Cross-link to section 1.5 for the inertial-frame propagation rationale. |
| `GhostMapPresence.cs:7227` | `ResolveTrackingStationGhostSourceCore` (TS-startup wrapper used by `CreateGhostVesselsFromCommittedRecordings`) | Pass `acceptTerminalOrbitForLoopSynthesis: false` -- TS-startup wrapper is not loop-aware. Recording #18's orbit line comes up on the first loop-aware refresh tick within `LifecycleCheckIntervalSec = 2.0s`. |
| `ParsekPlaybackPolicy.cs:1045` | Caller 1 | Initial spawn (no `effUT`). Pass `acceptTerminalOrbitForLoopSynthesis: false`. Same treatment as TS-startup. |
| `ParsekPlaybackPolicy.cs:1343` | Caller 2 | Loop-aware (`effUT` in scope). Pass `(shift != 0) && IsTerminalOrbitSynthesisSafeForLoopMember(rec)`. |
| `ParsekPlaybackPolicy.cs:1656` | Caller 3 | Loop-aware (`effUT` in scope). Same as :1343. |
| `ParsekPlaybackPolicy.cs:1867` | Caller 4 (inside `TryResolveTerminalFallbackMapOrbitUpdate`) | See MAJOR-3 row below -- the helper's signature is widened, and the flag is computed from the new parameter inside the helper. |
| `ParsekPlaybackPolicy.cs:1852` `TryResolveTerminalFallbackMapOrbitUpdate` | helper signature | Add `double loopEpochShiftSeconds` parameter; compute `acceptTerminalOrbitForLoopSynthesis = (loopEpochShiftSeconds != 0) && IsTerminalOrbitSynthesisSafeForLoopMember(rec)` inside; pass to the resolver call. Update the line 1495 call site to pass the shift in. |
| `Source/Parsek/InGameTests/RuntimeTests.cs:1203, 4190` | call sites | Inspection only. Default-false param keeps these byte-identical. If either RuntimeTest constructs and passes a non-zero loop shift, plumb the new param accordingly. |

### 2.1 Diagnostic logging

`Verbose` log site inside the synthesizer when the relaxed path accepts the seed:

```
[GhostMap] endpoint-tail-synthesis-loop-accept rec=<id> idx=<i>
  terminalBody=<X> lastPointBody=<X> tsLoopEpochShift=<s>
```

Sibling line when the predicate suppresses (cross-body): `accept-terminal-
orbit-suppressed reason=cross-body-terminal`. Use the structured emitter
(`BuildGhostMapDecisionLine` / `EmitGhostMapDecision`) per the GhostMap log
conventions.

### 2.2 No schema changes, no settings flag, no `RefreshEndpointDecision` touch

The relaxation is a pure function of existing `Recording` fields + the new
parameter. No new ConfigNode keys, no save/load changes, no new setting. The
plan does NOT touch `RefreshEndpointDecision`; per the reviewer's MAJOR-4, that
function would clobber any pre-persisted phase, but it's not called on the
relevant trajectories during loop tracking (verified by grep).

---

## 3. Test plan

### 3.1 Unit tests on the predicate (`GhostMapSoiGapStateVectorTests`)

`[Collection("Sequential")]` test class. Tests use direct field assignment on
`Recording`; `RecordingBuilder` is not extended in this PR.

- `IsTerminalOrbitSynthesisSafeForLoopMember_SameBody_ReturnsTrue`.
- `IsTerminalOrbitSynthesisSafeForLoopMember_CrossBody_ReturnsFalse`.
- `IsTerminalOrbitSynthesisSafeForLoopMember_NullRecording_ReturnsFalse`.
- `IsTerminalOrbitSynthesisSafeForLoopMember_EmptyPoints_ReturnsFalse`.
- `IsTerminalOrbitSynthesisSafeForLoopMember_NullTerminalOrbitBody_ReturnsFalse`.
- `IsTerminalOrbitSynthesisSafeForLoopMember_NullLastPointBody_ReturnsFalse`.

### 3.2 Unit tests on the relaxed synthesizer gate

- `TryResolveEndpointTailForMapPresence_TerminalOrbitSource_AcceptedWhenLoopFlag`.
- `TryResolveEndpointTailForMapPresence_TerminalOrbitSource_RejectedWithoutFlag`
  (regression: byte-identical for non-loop callers).
- `TryResolveEndpointTailForMapPresence_TerminalOrbitSourceWithMismatchedPersistedBody_Rejected`
  (defense in depth: persisted-phase relaxation requires same-body match too).
- `TryResolveEndpointTailForMapPresence_EndpointSegmentSource_AcceptedRegardlessOfFlag`
  (the relaxation widens, doesn't narrow).

### 3.3 Unit tests on `ResolveMapPresenceGhostSource`

- `ResolveMapPresenceGhostSource_NoSegmentLoopMemberWithTerminalOrbit_AcceptsEndpointTail`
  (the create-path end-to-end test the reviewer's MAJOR-2 asks for).
- `ResolveMapPresenceGhostSource_NoSegmentNonLoopMember_StillRejects`
  (regression: non-loop unchanged).
- `ResolveMapPresenceGhostSource_NoSegmentCrossBodyLoopMember_StillRejects`
  (predicate suppresses 181 Mm class).
- `ResolveMapPresenceGhostSource_NonLoopCallerOnNoSegmentOrbitingRecording_StillReturnsNone`
  (regression: relaxation default false stays safe for non-loop callers).
- `TryResolveTerminalFallbackMapOrbitUpdate_NoSegmentLoopMemberAtZeroSegSplit_PassesAcceptFlagThrough`
  (covers the section 1.6 helper plumbing path end-to-end: the new
  `loopEpochShiftSeconds` parameter is correctly converted into the
  `acceptTerminalOrbitForLoopSynthesis` flag inside the helper and the
  flag reaches the resolver call).

### 3.4 Activation-region precondition test

- `IsTerminalMapPresenceRegion_NoSegmentLoopMemberAtEffUTNearEnd_ReturnsTrue`
  (confirm `ResolveGhostActivationStartUT(traj)` admits the effective UT
  for a typical loop window).

(Former CRITICAL-1 pre-implementation verification step is resolved in section 0.5;
idx 18 = `terminal=Orbiting`, no `SubOrbital` widening required.)

### 3.5 No in-game canary (reviewer-flagged not feasible)

Drop. Manual playtest is the integration coverage.

### 3.6 Manual playtest

Load the user's "Kerbal X" save. Enter TS. Scrub the loop to a UT where the
Kerbin-return leg (recording #18) is the active loop member. Confirm a Kerbin
orbit line draws for it. Negative case: temporarily hand-edit the recording's
`TerminalOrbitBody` to "Mun" (cross-body contrivance), reload, confirm the
line does NOT draw.

---

## 4. Phase breakdown

One PR, one commit (the synthesizer + resolver + caller changes are tightly
coupled; splitting would land dead code).

- All section 2 code changes.
- All section 3 unit tests.
- CHANGELOG.md entry.
- `docs/dev/todo-and-known-bugs.md` deferred-item-B entry flipped to "fixed"
  with cross-link to v3 of this plan.
- `dotnet test` green.

A clean-context Opus code review runs on the commit diff before the PR is
opened. The synthesizer + create-path resolver changes are not docs-only;
warrant review per the "Code Review Follow-Ups" section of `CLAUDE.md`.

---

## 5. Backward-compatibility / what does NOT change + scope warning

### 5.1 What does NOT change

- Non-loop members (`tsLoopEpochShift == 0`): byte-identical. All call sites
  pass `acceptTerminalOrbitSource: false` (the new param defaults false; the
  outer predicate's `shift != 0` check rules out non-loop).
- The FIRST endpoint-tail branch (covering-segment override) for ALL members:
  byte-identical. The 181 Mm protection stays.
- Same-body loop members with at least one matching OrbitSegment:
  byte-identical. The `endpoint-segment` source path is unaffected by the
  relaxation; the `acceptTerminalOrbitSource` parameter only widens.
- Cross-body loop terminals (181 Mm bug class): predicate returns false ->
  suppressed -> ghost not created -> atmospheric marker draws.
- Recording schema: no changes.
- Settings: no new flag.
- `BuildSignature`: no changes.
- `RefreshEndpointDecision` behavior: unchanged.

### 5.2 What DOES change (honest scope)

Five files modified (`GhostMapPresence.cs` + `ParsekPlaybackPolicy.cs` + tests
+ CHANGELOG + todo doc); 6 synthesizer call sites updated; one resolver
signature widened across 5 production callers (4 in `ParsekPlaybackPolicy`
+ 1 TS-startup wrapper at `GhostMapPresence.cs:7227`) plus 2 inert test
callers in `RuntimeTests.cs`. This is larger than the user's "small fix"
expectation when the workflow started. The user has previously chosen
"leave as-is for now" for this regression twice. Re-confirm the cost is worth
the visual fix BEFORE committing.

If the user decides the cost is too high: defer indefinitely; the regression
stays as documented in the deferred-item-B entry. The v3 plan is then a
parked artifact in case the user revisits.

---

## 6. Open questions to pin before implementing

1. **RESOLVED (v5).** Persisted-phase answer for idx 18. The playtest log
   for #18 shows `terminal=Orbiting` with persisted phase
   `(TrajectoryPoint, Kerbin)`. The persisted-phase relaxation IS required;
   section 1.1's inner-gate change is on-target. No further verification needed
   before code.

2. **TS-startup-create path (former OQ #3).** Pass `false` via the new
   trailing optional. Confirms the first ghost is created by the lifecycle
   pass (within 2s of TS entry) via the relaxed resolver, not at startup.
   The user will see a ~2s blank for #18 at TS entry before the orbit line
   appears -- acceptable, matches the existing "startup-then-recreate"
   pattern documented in `todo` line 92. Carried as a user-acceptance
   check; no design blocker.

3. **Cost vs benefit (former OQ #5).** The user's deferred-item-B entry
   says "leave as-is for now". This v5 plan represents the full cost of
   UNdeferring it. Re-confirm with the user before code lands.

4. **RESOLVED (v5).** `InternalsVisibleTo("Parsek.Tests")` verified present
   at `Source/Parsek/Properties/AssemblyInfo.cs:5`. The promotion of
   `TryResolveEndpointTailForMapPresence` + the new
   `IsTerminalOrbitSynthesisSafeForLoopMember` /
   `IsTrackingTerminalOrbitBody` helpers can be tested from `Parsek.Tests`
   without further AssemblyInfo work.

(Former OQ #2 -- "walk each caller and confirm `effUT` is computed and
available" -- deleted in v5; section 1.4 already enumerates all five production
callers with their `effUT` availability. Former OQ #4 -- "RefreshEndpointDecision
clobber risk" -- collapsed to a one-line note in section 1.1.)

---

## 7. References

(See also References block at top.)

- v1 of this plan (overwritten) -- wrong outer gate.
- v2 of this plan (overwritten) -- partial inner gate; missed create path.
- v3 of this plan -- reviewed for prose / signature / caller-enumeration
  issues; v4 folds those fixes in.
- Clean-context review of v1 (2026-05-28).
- Clean-context review of v2 (2026-05-28).
- Clean-context review of v3 (2026-05-28).

### Verified from KSP disassembly (Assembly-CSharp.dll, 2026-05-28)

Backs the MAJOR-1 prose correction in section 1.5 -- inertial-frame propagation
under epoch shift is the right model.

- `Orbit.SetOrbit(double inc, double e, double sma, double lan, double argPe,
  double mEp, double t, CelestialBody body)` at line 488 -- sets the orbital
  element fields on the `Orbit`, then calls `Init()`.
- `Orbit.Init` builds `OrbitFrame` via
  `Planetarium.CelestialFrame.OrbitalFrame(LAN, inclination,
  argumentOfPeriapsis, ...)` -- Planetarium INERTIAL frame, NOT
  body-rotation-aware. This is why `SetOrbit(epoch + shift, ...)` is safe:
  the frame depends only on the inertial elements, not on the body's
  rotation phase at the seeded UT.
- `Orbit.UpdateFromUT(double UT)` line 922 -- propagates the orbit from the
  seeded epoch using `meanMotion * (UT - epoch)`; the shift to epoch
  cancels against any compensating shift in the propagation UT.
- `Orbit` constructor takes the same `(inc, e, sma, lan, argPe, mEp, t,
  body)` tuple at line 464 -- structurally equivalent to `SetOrbit`.
- Confirms v3 section 1.5 / section 1.4 conclusion: `SetOrbit(epoch + shift, ...)`
  propagates inertially under same-body loop synthesis. The corrected v4
  reasoning is: historical body rotation enters at seed construction (when
  the recorder converted body-fixed lat/lon/alt + recorded velocity into
  inertial orbital elements); once those elements are persisted, propagation
  + shift is body-rotation independent via the Planetarium inertial frame.
