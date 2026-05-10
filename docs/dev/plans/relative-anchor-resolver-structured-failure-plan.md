# Plan: structured failure for `RelativeAnchorResolver`

## Verdict and recommendation

**Verdict.** Item (4) is real, localized, right-sized. The
`RelativeAnchorResolver` API throws away rich classification it
already computes: every failing call site produces one of 16 reason
strings inside `WarnUnresolved`
(`Source/Parsek/RelativeAnchorResolver.cs:1354-1375`), but the API
returns plain `bool false`. Downstream effects:

- `FlightRecorder.cs:7494-7529` (`TryResolveRecordedAnchorPose`)
  plumbs `out string reason` then overwrites it with the constant
  `"recorded-anchor-unresolved"` at line 7528.
- `BackgroundRecorder.cs:4627-4671`
  (`TryResolveBackgroundRecordedAnchorPose`) does the same overwrite
  at line 4669.
- `ProductionAnchorWorldFrameResolver.cs:65-89, 92-109` — the
  merge-side boundary stitching seam — cannot distinguish
  "anchor-out-of-recorded-range" from "anchor-recording-not-found"
  from "relative-pose-nonfinite". Item (5) needs the distinction.

**Recommendation.** Mirror the narrow-enum style of
`RelativeAnchorResolution.Outcome`
(`Source/Parsek/RelativeAnchorResolution.cs:18-110`): small enum,
small failure struct, populated at the same `WarnUnresolved` sites
that already classify the failure. Replace the `bool`-only API
outright with `bool + out failure`; the production resolver call
clusters migrate cleanly, and the wrapper/test consumers are
mechanical. Fix the two reason-overwrite bugs in the same PR by
propagating the resolver's classification through the recorder seams.

This plan is the API contract item (5) builds on. Item (5) decides
the merge-side reaction; this plan only commits to giving item (5)
the fields it needs.

---

## Problem restatement

Seventeen distinct reason keywords flow through 25 `WarnUnresolved`
emit sites (`RelativeAnchorResolver.cs:1354-1375`):
`anchor-out-of-recorded-range`, `anchor-recording-id-missing`,
`legacy-anchor-recording-id-missing`, `anchor-recording-not-found`,
`anchor-recording-null`, `anchor-cycle-detected`,
`anchor-cross-tree-out-of-scope`, `loop-anchor-out-of-scope`,
`active-provisional-out-of-scope`, `anchor-section-frame-unknown`,
`anchor-track-sections-missing`, `relative-pose-nonfinite`,
`absolute-position-unresolved`, `absolute-pose-nonfinite`,
`orbital-pose-resolver-missing`,
`orbital-pose-unresolved`, `orbital-pose-nonfinite`. All seventeen
collapse to `bool false` across the API boundary.

### Real-world reason-string distribution

Mining `C:/Users/vlad3/Documents/Code/Parsek/logs/` shows that only a
small subset of those 17 reasons actually fires in production:

| Reason | Sample bundles |
|---|---|
| `anchor-out-of-recorded-range` (dominant) | `2026-05-06_2351_refly-phase-d-rewind-button-debris` (49 emits), `2026-05-08_1740_rewind-and-refly-regressions` (22), `2026-05-08_2208_ghost-rendering-recording-followups` (22) |
| `active-provisional-out-of-scope` | `2026-05-01_2344_kerbal-x-not-sealed`, `2026-05-02_1018_ghost-anchor-playtest-investigation`, `2026-05-04_1817` |
| `anchor-cycle-detected` | same three Re-Fly bundles |
| `anchor-track-sections-missing` | `2026-05-09_1308_radial-debris-still-forward-after-pr780` and two siblings |

The remaining twelve resolver reasons are reachable in code but not
observed in any log bundle. The enum can be tightened to model the four
real resolver outcomes, one contract-required resolver outcome
(`anchor-recording-not-found`), one wrapper-precondition outcome, plus
a single bucket for the remaining resolver tail; see Design 1 below.

### Two structurally different `anchor-out-of-recorded-range` cases

The dominant reason fires at three sites that are not the same
failure: `:188-193` (no section at UT, `sectionIndex=(none)`),
`:927-933` (section found, relative-frame interpolation fails,
`sectionIndex=N`), and `:1026-1058` (section found, absolute frames
don't cover UT, `sectionIndex=N`). Real bundles confirm both shapes
co-occur: in `2026-05-06_2351_refly-phase-d-rewind-button-debris`,
lines 13547, 15369, 15538, and 19082 all carry the same reason with
mixed `sectionIndex`. The merge seam (item (5)) likely treats them
differently. The enum splits them.

### Sample of the constant-overwrite drop in production

`2026-05-06_2351_refly-phase-d-rewind-button-debris/KSP.log`:

```
:15538 [WARN][RelativeAnchorResolver] relative-anchor-unresolved:
       reason=anchor-out-of-recorded-range
       recordingId=e1ea034b... anchorRecordingId=e1ea034b...
       sectionIndex=(none) ut=16525.865...
:15539 [WARN][Anchor] Anchor recording id=e1ea034b... unresolved --
       forcing transition to ABSOLUTE at UT=16525.87
       reason=recorded-anchor-unresolved   <-- constant overwrite
:15543 [INFO][Anchor] RELATIVE mode force-exited:
       ... reason=recorded-anchor-unresolved   <-- propagated
```

Same UT, same recording, same anchor, milliseconds apart — line 15538
carries the resolver's classification; lines 15539 and 15543 overwrite
it with the constant, and a fourth log
(`relative-force-exit-recorded-anchor-unresolved` at `:15542` from the
high-fidelity sampling window) inherits the constant too. Four
downstream log lines lose the resolver's classification.

### Dedupe interaction

`WarnRateLimited` suppresses repeated WARNs aggressively. Real
`suppressed=` counts in the headline bundle: 18, 88, 396, 1723, 1735,
2096. A single failing per-frame condition produces thousands of
suppressed WARNs in one playtest. The structured-failure API must
coexist with the rate limiter — the failure struct is constructed on
every call (every frame), but the WARN line is emitted at most once
per `5.0` s window per dedupe key. The implementation constructs the
struct unconditionally and dispatches the log emission through the
existing `WarnRateLimited` path.

The affected production call clusters all collapse this into a `bool`:

| Caller | File:line | Today's signal | Behavior on `false` |
|---|---|---|---|
| Recorder force-exit | `FlightRecorder.cs:7494-7529` (via `TryResolveRecordedAnchorPose`) | constant `out string reason` overwrite | `ForceExitRelativeToAbsolute` |
| BG recorder | `BackgroundRecorder.cs:4627-4671` (via `TryResolveBackgroundRecordedAnchorPose`) | constant `out string reason` overwrite | Same shape |
| Anchor-candidate enumeration | `ParsekFlight.cs:15125-15134` | `bool` only | Increments `unresolved++` counter |
| Standalone (KSC/Map) world-pos | `ParsekFlight.cs:17584-17609` | `bool` only | Falls through to standalone absolute shadow |
| Recorded-relative anchor pose | `ParsekFlight.cs:22091-22121` (`TryResolveRecordedRelativeAnchorPose`) | `bool` only; local guard failures are logged by the wrapper, resolver failures collapse to generic `resolver-unresolved` / `anchor-pose-unresolved` traces | Used by late transform reapply (`:1491`), recorded relative world-position resolution (`:20467`), interpolation fallback (`:21710`), and single-point placement (`:21923`) |
| Standalone wrapper | `RecordedRelativeAnchorPoseResolver.cs:9-31`; callers are `ParsekKSC.cs:1704` and `GhostMapPresence.cs:5301` | `bool` only | Caller treats as hidden / unresolved state-vector anchor |
| Boundary stitching | `ProductionAnchorWorldFrameResolver.cs:131` (via `TryResolveKnownRelativeBoundaryPose`) | `bool` only | Falls to absolute shadow at `:65-89` and returns world pos REGARDLESS of how badly the section data disagrees — the seam item (5) needs to refuse |

Why `bool` is the bottleneck: every caller wants to do something
slightly different on failure (force-exit-to-absolute, count, retire,
fall through to a shadow, refuse-to-stitch), and three of them already
fake their own classification by hard-coding a string. The information
is being computed and immediately discarded across the API boundary.

---

## Proposed API shape — Design 1 (recommended)

A small `enum` plus a small `readonly struct`, following the
narrow-enum style of `RelativeAnchorResolution.Outcome`. Sized to the
four resolver reasons that actually fire in production logs, one
contract-required not-found outcome, one non-finite-pose corruption
outcome, one wrapper-precondition outcome, plus one bucket for the
dormant resolver tail.

```csharp
internal enum RelativeAnchorResolveOutcome
{
    None = 0,              // default/unset; valid only when the Try method returned true
    OutOfSectionRange,    // section found, data inside doesn't reach UT (:927, :1026, :1052)
    NoSectionAtUT,        // FindTrackSectionForUT returned -1 (:188)
    AnchorCycleDetected,  // structural/corrupt chain recursion guard
    AnchorOutOfScope,     // cross-tree / active-provisional / loop-anchor scope rejection
    TrackSectionsMissing, // non-loop recording has no TrackSections (:235-243)
    AnchorRecordingNotFound, // item (5) dispatch needs this distinct from range failures
    PoseNonFinite,        // relative/orbital pose resolved to non-finite data
    PreconditionFailed,   // invalid input / wrapper or resolver entry guard
    Other,                // dormant resolver tail — Reason string remains on struct
}

internal readonly struct RelativeAnchorResolveFailure
{
    public readonly RelativeAnchorResolveOutcome Outcome;
    public readonly string Reason;             // original keyword (resolver or wrapper)
    public readonly string FailureRecordingId; // value logged today as recordingId
    public readonly string AnchorRecordingId;
    public readonly double RequestedUT;
    public readonly int    SectionIndex;       // -1 when not section-scoped
    public readonly double RangeStartUT;       // NaN when no safe range source is available
    public readonly double RangeEndUT;         // NaN when no safe range source is available
}
```

`OutOfSectionRange` and `NoSectionAtUT` must be assigned by call site,
not inferred from `sectionIndex` alone. The `:188` site is
`NoSectionAtUT` because `FindTrackSectionForUT` returned `-1`.
The `:927`, `:1026`, and `:1052` sites are `OutOfSectionRange`
because a section/frame source was selected and its data does not cover
the requested UT. This distinction matters because legacy/no-section
absolute-frame resolution can also report `sectionIndex=-1` without
being a `NoSectionAtUT` failure. The factory therefore accepts an
explicit outcome (or uses tiny per-site helper overloads) while keeping
the existing reason keyword and dedupe key unchanged.

If a dormant tail reason later starts driving caller policy, split it
out into its own enum case with a follow-up plan: one line in the
mapping, one new enum value. The API contract is stable.

The `Reason` string is preserved on the struct so existing log-greppers
(`KSP.log`, ledger validation, dedupe key construction) keep working
unchanged. The enum is the strongly-typed dispatch surface for callers
that want to make decisions; `Reason` remains the human-readable WARN
keyword.

Canonical reason mapping:

| Reason keyword | Outcome | Notes |
|---|---|---|
| `anchor-out-of-recorded-range` at `:188` | `NoSectionAtUT` | `FindTrackSectionForUT` returned `-1`; range is whole recording when known. |
| `anchor-out-of-recorded-range` at `:927` | `OutOfSectionRange` | RELATIVE section exists, but relative-frame samples do not cover/interpolate the requested UT. |
| `anchor-out-of-recorded-range` at `:1026` | `OutOfSectionRange` | Absolute frame list does not cover UT; null/empty frame list leaves range fields `NaN`. |
| `anchor-out-of-recorded-range` at `:1052` | `OutOfSectionRange` | Absolute frame list passed coverage but interpolation still failed; keep same outcome, preserve `Reason` and range fields for diagnostics. |
| `anchor-recording-id-missing` at `:96` | `PreconditionFailed` | Resolver entry guard; same class as wrapper missing-id guards. |
| `anchor-recording-id-missing` at `:908` | `Other` | RELATIVE section is malformed/missing its anchor id after resolver entry. |
| `legacy-anchor-recording-id-missing` | `Other` | Legacy malformed section, distinct `Reason` preserved. |
| `anchor-recording-not-found` | `AnchorRecordingNotFound` | Directly emitted only by `TryResolveAnchorPose`; other entry points can return it only if they recurse into `TryResolveAnchorPose`. |
| `anchor-recording-null` | `PreconditionFailed` | Bad direct `TryResolveRecordingPose` input. |
| `anchor-cycle-detected` | `AnchorCycleDetected` | Structural chain corruption/recursion guard; intentionally not folded into scope misses. |
| `anchor-cross-tree-out-of-scope` | `AnchorOutOfScope` | Pending tree exists but is not in the active scope. |
| `loop-anchor-out-of-scope` | `AnchorOutOfScope` | Loop/live-anchor recording is outside recorded-chain resolver scope. |
| `active-provisional-out-of-scope` | `AnchorOutOfScope` | Active provisional Re-Fly recording cannot be used for this UT. |
| `anchor-section-frame-unknown` | `Other` | Unknown `ReferenceFrame` enum value. |
| `anchor-track-sections-missing` | `TrackSectionsMissing` | v6+ recording has no track sections. |
| `relative-pose-nonfinite` | `PoseNonFinite` | Resolver produced non-finite relative pose; data-corruption signal, not a routine scope miss. |
| `absolute-position-unresolved` | `Other` | Absolute/body world position could not be resolved. |
| `absolute-pose-nonfinite` | `PoseNonFinite` | Absolute helper resolved finite inputs but produced a non-finite final pose; data-corruption signal. |
| `orbital-pose-resolver-missing` | `Other` | Orbital checkpoint resolver delegate absent. |
| `orbital-pose-unresolved` | `Other` | Orbital checkpoint resolver returned false. |
| `orbital-pose-nonfinite` | `PoseNonFinite` | Orbital checkpoint resolver returned non-finite pose; data-corruption signal. |

Per-entry-point reason subsets:

- `TryResolveAnchorPose` can directly emit missing id,
  `anchor-cycle-detected` at `:107`,
  `anchor-recording-not-found`, `anchor-cross-tree-out-of-scope`, and
  active-provisional scope failures; it can also return any
  `TryResolveRecordingPose` failure after the lookup succeeds.
- `TryResolveRecordingPose` cannot directly emit
  `AnchorRecordingNotFound`; it can still return that outcome when a
  RELATIVE section recurses into `TryResolveAnchorPose` for its parent.
  The standalone `ParsekFlight.cs:17587` caller therefore must treat
  `AnchorRecordingNotFound` as propagated-only. It can directly emit
  `AnchorCycleDetected` via same-chain continuation at `:280`.
- `TryResolveRelativeSectionPose` directly emits missing anchor id,
  relative range, and non-finite relative pose failures; it can return
  parent-chain outcomes propagated from `TryResolveAnchorPose`.

### Why Design 1 over Design 2

Design 2 (`(bool, struct)` with `Failure.Reason` as a strongly-typed
enum-of-strings) was rejected: it would invent a second pattern next
to `RelativeAnchorResolution.Outcome` (which is a small C# `enum`),
force callers to dispatch on string comparisons, and balloon the
enum to 16 values that mostly differ only in log granularity. Design
1 keeps both: enum for dispatch, string for logging.

---

## Field-by-field rationale

Every field except `RangeStartUT/EndUT` is already in scope at every
`WarnUnresolved` call site — no new plumbing.

- `Outcome`, `Reason` — strongly-typed dispatch + preserved log keyword.
- `FailureRecordingId`, `AnchorRecordingId`, `RequestedUT`,
  `SectionIndex` — let callers tag follow-up logs without
  re-plumbing identity. `FailureRecordingId` is deliberately named
  after the resolver failure site, not the root focus recording; parent
  chain failures may describe a different recording from the caller's
  victim section.
- `RangeStartUT/EndUT` — populated only on
  `OutOfSectionRange`/`NoSectionAtUT`; use guarded reads from the
  in-scope `Recording`/`TrackSection`/frames. NaN otherwise. Item (5)
  uses these to compare magnitude (1e-12 past vs 200 s past) when the
  range is known.
- No `HasAbsoluteShadow` field lives on the resolver failure. Boundary
  shadow availability is caller-local state owned by
  `ProductionAnchorWorldFrameResolver`, because parent-chain failures
  can describe a different recording/section than the victim boundary.

Struct size is roughly 64 bytes; `readonly`; built by a static
factory at each WARN site. On the success path it is `default`
(`Outcome=None`). On every `false` return, including wrapper guard
returns that happen before the resolver is called, the callee must
return a non-default failure with a concrete `Reason`.

---

## Backwards-compatibility strategy

Change the API outright. Do not keep a `bool`-returning shim. The
resolver, `AnchorPose`, `RelativeAnchorResolverContext`, and all
production/test callers are internal to this repo. A shim would let new
callers re-introduce the bottleneck.

All three `internal` entry points at `:86`, `:146`, and `:888` gain an
`out RelativeAnchorResolveFailure failure`; their existing input
parameters differ and their direct reason subsets differ:

```csharp
internal static bool TryResolveAnchorPose(
    RelativeAnchorResolverContext context,
    string anchorRecordingId, double ut, HashSet<string> visited,
    out AnchorPose pose,
    out RelativeAnchorResolveFailure failure);
```

`failure` is `default` on the success path. Callers that don't want to
consume failure write `out _`. Returning `false` with `default`
failure is invalid; guard paths synthesize a failure with
`Outcome=PreconditionFailed` or `Outcome=Other` plus the exact reason
keyword they already log.

---

## Caller-by-caller migration

| # | Site | Change |
|---|---|---|
| 1 | `FlightRecorder.cs:7494-7529` (`TryResolveRecordedAnchorPose`) | Replace `out string reason` with `out RelativeAnchorResolveFailure failure`. Migrate the intermediate wrappers too: `TryResolveCurrentAnchorPose` (`:7421`) and `TryResolveAnchorPoseForCandidate` (`:7446`) either carry `out failure` or explicitly adapt `failure.Reason` back to the legacy string at their boundary. Callers at `:7268` and `:7491` use `failure.Reason`. The WARN at `:7279` and `ForceExitRelativeToAbsolute(...)` at `:7283` now carry the resolver's classification — fixes constant-overwrite #1. Also migrate `TryResolveRecordedAnchorPoseOverride` at `FlightRecorder.cs:99-104` and its `EnvironmentTrackingIntegrationTests.cs:612` assignment, or add a temporary test-only adapter that builds a structured failure from the override reason. |
| 2 | `BackgroundRecorder.cs:4627-4671` (`TryResolveBackgroundRecordedAnchorPose`) | Same shape as #1. Migrate `TryResolveBackgroundCurrentAnchorPose` (`:4432`) and `TryResolveBackgroundAnchorPoseForCandidate` (`:4553`) with the same `out failure` or explicit string-adapter contract. Caller at `:4624` consumes `failure.Reason`. Fixes constant-overwrite #2. |
| 3 | `ParsekFlight.cs:15125-15134` (anchor-candidate enumeration) | Pass `out _`. Counter behavior unchanged; per-candidate detail is already emitted by `WarnUnresolved`. |
| 4 | `ParsekFlight.cs:17584-17609` (standalone KSC/Map world-pos) | Capture `failure` from `RelativeAnchorResolver.TryResolveRecordingPose` at `:17587`. Existing `WarnRateLimited` at `:17601` adds `outcome={failure.Outcome}` and `reason={failure.Reason}` alongside its existing fields. Shadow fallback at `:17598` unchanged. |
| 5 | `ParsekFlight.cs:22091-22121` (`TryResolveRecordedRelativeAnchorPose`) | Add `out failure` to the two overloads at `:22002` and `:22015`. Local guard failures (`anchor-recording-id-missing`, `focus-tree-missing`) synthesize non-default failures. Update all callers: late ghost transform reapply at `:1491`, recorded relative world-position resolution at `:20467`, interpolation fallback at `:21710`, and single-point relative placement at `:21923`. The interpolation callers keep the `TryUseRelativeAbsoluteShadowFallback` plumbing — intentional shadow consumer, out of scope per prompt. `GhostRenderTrace.EmitRelativeResolver` logs `failure.Reason` instead of the generic `resolver-unresolved` / `anchor-pose-unresolved` when the resolver ran. |
| 6 | `RecordedRelativeAnchorPoseResolver.cs:9-31` | Add `out failure` to `TryResolveSectionAnchorPose`. Its actual callers are `ParsekKSC.cs:1704` and `GhostMapPresence.cs:5301`; both can pass `out _` unless they add local logs. Wrapper guard failures (`focusRecording == null`, non-relative section, missing anchor id, context build failed) synthesize non-default failures so no `false/default` state leaks. |
| 7 | `ProductionAnchorWorldFrameResolver.cs:131` (`TryResolveKnownRelativeBoundaryPose`) — item (5) seam | Add `out failure` and bubble up to `:60`. WARN at `:92-109` adds `outcome={failure.Outcome}` and `reason={failure.Reason}`. Shadow fallback at `:65-89` unchanged in policy for this plan, but the existing `relative-boundary-shadow-fallback` verbose line at `:73-88` also logs `chainOutcome={failure.Outcome}` and `chainReason={failure.Reason}` when the resolver failed before shadow fallback succeeded. Separately compute and log `boundaryHasAbsoluteShadow` from the adjacent `relSection.absoluteFrames`; do not infer it from resolver failure data. |
| 8 | `RelativeAnchorResolver.cs` internal recursion | `TryResolveAnchorPose` → `TryResolveRecordingPose` → `TryResolveRelativeSectionPose` → recurses. The deepest `WarnUnresolved` site populates the struct; outer layers write `default` on success and propagate unchanged on failure. |

---

## Where to populate the struct

The 22 `WarnUnresolved` emit sites already have everything except
`RangeStartUT/EndUT`. Range fields populate for the two range-bearing
outcomes from data already in scope:

- `:188` (`NoSectionAtUT`): bound the whole recording —
  `TrackSections[0].startUT` /
  `TrackSections[TrackSections.Count - 1].endUT`.
- `:927` (`OutOfSectionRange`, relative): use `section.startUT` /
  `section.endUT`.
- `:1026, :1052` (`OutOfSectionRange`, absolute frames): use
  `frames[0].ut` / `frames[frames.Count - 1].ut` when `frames`
  is non-null and non-empty; otherwise use finite
  `sectionStartUT` / `sectionEndUT` if available; otherwise leave
  both range fields as `NaN`. `FrameListCoversUT` returns false for
  null/empty frame lists, so the factory must never index `frames`
  before checking `Count`.

`anchor-recording-not-found` maps to `AnchorRecordingNotFound` in the
same factory branch that currently receives the `TryFindRecording`
reason string. The original reason string stays in `Reason`; the enum
exists so item (5) does not have to dispatch on string comparisons for
this required policy distinction.

Callers with a separate fallback/victim section, especially the
boundary stitcher, keep caller-local shadow availability as a separate
local boolean/log field. The resolver failure struct deliberately does
not carry a section shadow flag.

`WarnUnresolved` becomes a small factory that returns the failure
struct AND emits the log line. Callers replace
`{ WarnUnresolved(...); return false; }` with
`{ failure = WarnUnresolvedAndBuildFailure(...); return false; }`.
The factory takes an explicit `RelativeAnchorResolveOutcome` for the
three `anchor-out-of-recorded-range` families so it never guesses
`NoSectionAtUT` from `sectionIndex=-1`.

Private helper propagation contract:

- Any private helper that can call `WarnUnresolvedAndBuildFailure`
  also gains `out RelativeAnchorResolveFailure failure`.
- Helpers that simply probe and fail without logging return
  `false/default`, allowing the caller to continue to the next fallback
  or emit its broader failure.
- Helpers that log and build a non-default failure return
  `false/non-default`; callers propagate that failure immediately and
  do not overwrite it with a later broader failure. This is required
  for `TryResolveSmallSectionGapPose` → `TryResolveAbsoluteBracketPose`
  (`absolute-position-unresolved`) and
  `TryResolveSameChainContinuationPose` (`anchor-cycle-detected` /
  `active-provisional-out-of-scope`) before the outer `:188`
  `NoSectionAtUT` site.

---

## Test plan

### Unit tests

`Source/Parsek.Tests/RelativeAnchorResolverTests.cs` (1177 lines)
already covers most reason strings via log-capture. Add to each
existing test: assert `failure.Outcome` matches the canonical
mapping and `failure.Reason` matches the existing WARN keyword.

New tests:

- Two `anchor-out-of-recorded-range` tests pinning the
  `sectionIndex=(none)` → `NoSectionAtUT` (range = whole recording)
  vs `sectionIndex=N` → `OutOfSectionRange` (range = section bounds)
  discriminator. Real bundles confirm both fire — this assertion
  is load-bearing for item (5).
- Success path — `failure == default` when the resolver returns true.
- Guard path — every wrapper that gains `out failure` returns a
  non-default failure on `false` before the resolver is called
  (`ParsekFlight` missing anchor id/focus tree and
  `RecordedRelativeAnchorPoseResolver` null/non-relative/context
  guards). This prevents failed calls from being logged as
  `Outcome=None`.
- Missing-id mapping — resolver entry guard `anchor-recording-id-missing`
  (`:96`) and wrapper-synthesized missing-id failures are
  `PreconditionFailed`; section-level `anchor-recording-id-missing`
  (`:908`) remains `Other` with the original `Reason`.
- One per `Other`-bucket reason with an existing fixture
  (`orbital-pose-resolver-missing`, `absolute-position-unresolved`,
  section-level `anchor-recording-id-missing`) asserting they map to `Other` with
  their distinct `Reason` preserved.
- `relative-pose-nonfinite`, `absolute-pose-nonfinite`, and
  `orbital-pose-nonfinite` map to
  `PoseNonFinite`, not `Other`, because item (5) may need to treat
  non-finite pose data as corruption.
- `anchor-recording-not-found` maps to
  `AnchorRecordingNotFound`, not `Other`, because item (5) explicitly
  needs that policy distinction.
- Private helper propagation — drive a small-section-gap
  `absolute-position-unresolved` failure and a same-chain
  cycle/provisional failure and assert the outer `NoSectionAtUT`
  failure does not overwrite the deeper non-default failure. Also
  assert the helper-logged failure does **not** trigger a follow-on
  outer `NoSectionAtUT` WARN.
- Empty absolute frame coverage — drive the `:1024`/`:1026`
  null-or-empty `FrameListCoversUT` path and assert
  `Outcome=OutOfSectionRange` with `RangeStartUT/RangeEndUT=NaN`.

Update the resolver harness too: `Source/Parsek.Tests/Harness/ResolverPoseHasher.cs`
calls `TryResolveRecordingPose` in both hash and dump paths, so it must
pass `out _` or include failure details in unresolved dumps. This is a
test consumer of the API even though `RelativeAnchorResolverTests.cs`
is the main assertion target.

Update `FlightRecorder.RecordedAnchorPoseOverrideForTesting` and
`EnvironmentTrackingIntegrationTests.cs:612` with the new failure
shape. If the override stays string-based temporarily, the adapter must
convert `reason` into `RelativeAnchorResolveFailure` so tests exercise
the same non-default-failure contract as production.

### Recorder force-exit integration

Use the log-capture pattern from `RewindLoggingTests.cs`. Two new
tests in `FlightRecorderStructuralEventTests.cs` or a new focused
sibling:

- Drive `ApplyRelativeOffset` past `endUT`. Assert
  `ForceExitRelativeToAbsolute` receives
  `reason="anchor-out-of-recorded-range"`, NOT the constant. This
  pins the fix and would have caught the original bug.
- Drive the direct wrapper guard at `FlightRecorder.cs:7264`
  (`currentAnchorRecordingId` missing) and assert the synthesized
  failure/force-exit reason remains `anchor-recording-id-missing`
  with `Outcome=PreconditionFailed` rather than defaulting or being
  confused with resolver-emitted missing-id.
- Same shape for `BackgroundRecorder.TryResolveBackgroundRecordedAnchorPose`.
  Include the background direct guard at `BackgroundRecorder.cs:4428`.

### Seam log

Extend `Source/Parsek.Tests/Rendering/ProductionAnchorWorldFrameResolverTests.cs`
or `Source/Parsek.Tests/Rendering/AnchorWorldFrameResolverTests.cs`,
asserting the `outcome=`, `reason=`, and
`boundaryHasAbsoluteShadow=` fields appear in the seam WARN at
`ProductionAnchorWorldFrameResolver.cs:92-109`. Add a parent-chain
failure case proving the seam log/policy uses the boundary-local
shadow boolean, not any resolver-failure field. Also assert the
successful `relative-boundary-shadow-fallback` verbose line carries
`chainOutcome=` / `chainReason=` when it followed a structured
resolver failure.

All new test classes touching `ParsekLog.TestSinkForTesting` carry
`[Collection("Sequential")]`.

---

## Logging requirements

- Resolver keeps calling `WarnUnresolved` (via the renamed
  `WarnUnresolvedAndBuildFailure` factory). Existing dedupe keys at
  `RelativeAnchorResolver.cs:1365` are unchanged — playtest
  `suppressed=N` counts continue to suppress the per-frame storm.
- Recorder and BG recorder WARN lines that previously printed
  `reason={constant}` now print `reason={failure.Reason}`. Recorded
  relative traces that previously used generic resolver-miss reasons
  also print `failure.Reason`. No new dedupe keys.
- Recorded-relative world-position trace at `ParsekFlight.cs:20467`
  also uses `failure.Reason` instead of the generic
  `anchor-pose-unresolved`.
- Merge-seam WARN at `ProductionAnchorWorldFrameResolver.cs:92-109`
  and standalone-KSC WARN at `ParsekFlight.cs:17601` gain
  `outcome={failure.Outcome}` for grep/dashboard. Merge-seam WARN also
  gains `boundaryHasAbsoluteShadow={...}` from the boundary RELATIVE
  section. The existing `relative-boundary-shadow-fallback` verbose
  line gains `chainOutcome={failure.Outcome}` and
  `chainReason={failure.Reason}` when it runs after resolver failure.
- No new WARN emit sites. Existing dedupe keys stay stable. Private
  helper failures that already logged a more specific reason may skip
  the later broader outer WARN so the propagated failure is not
  overwritten.

---

## Documentation updates

- `docs/dev/todo-and-known-bugs.md` — mark item (4) superseded by
  this plan. New "Done" entry on PR landing.
- `CHANGELOG.md` — one line: "Internal: `RelativeAnchorResolver` API
  returns a structured failure object alongside the success boolean;
  recorder and background recorder force-exit paths now log the
  resolver's classification instead of a hard-coded constant."
- `.claude/CLAUDE.md` — no update. Resolver not mentioned in
  conventions.
- No format-version bump, nothing serialized.

---

## Coordination with item (5)

Item (5) (refuse-to-stitch at
`ProductionAnchorWorldFrameResolver.cs:65-89`) needs:

- `Outcome` — match against `OutOfSectionRange` and `NoSectionAtUT`.
  Real logs show both fire frequently and likely demand different
  decisions (1e-12 s past `endUT` is acceptable; no section at all
  is a structural gap). `AnchorCycleDetected`, `AnchorOutOfScope`, and
  `TrackSectionsMissing` likely have separate policies.
  `AnchorRecordingNotFound` is also a first-class outcome because item
  (5) explicitly needs to distinguish missing anchor data from range
  disagreements.
- `RangeStartUT`, `RangeEndUT`, `RequestedUT` — magnitude check
  (ε-past-endUT vs hundreds of seconds past). The headline bundle
  has real ε cases at `endUT + 1e-13..3e-12`
  (`docs/dev/todo-and-known-bugs.md:38`) plus full-second misses in
  the same playtest, so magnitude is the central decision.
- `SectionIndex` — supports diagnostics and, together with the
  explicit `Outcome`, identifies which resolver-site section failed.
  It must not be used by itself to infer `NoSectionAtUT`, because
  non-section absolute-frame paths can also report `-1`.
- Boundary-local shadow availability — whether a shadow fallback exists
  on the victim RELATIVE section before refusing.
  `relative-boundary-shadow-fallback` fires only in a
  handful of bundles
  (`2026-05-06_2156_refly-probe-booster-broken` has 3 hits), so this
  is "should we even try," not a hot loop. It is not stored on the
  resolver failure because parent-chain failures can describe a
  different recording/section. At the seam,
  `ProductionAnchorWorldFrameResolver` already has the victim
  `relSection` in scope, so it can read
  `relSection.absoluteFrames` directly.

Initial item (5) decision sketch:

| Outcome | Boundary policy item (5) should evaluate |
|---|---|
| `OutOfSectionRange` with `abs(RequestedUT - nearest known range edge) <= epsilon` | Candidate may remain stitchable if the resolved/shadow pose is continuous within the boundary tolerance. |
| `OutOfSectionRange` with a large miss or unknown range | Refuse or quarantine the stitch; use boundary shadow only if item (5) explicitly accepts the data-quality risk. |
| `NoSectionAtUT` | Treat as a structural gap by default; do not silently stitch through a shadow. |
| `AnchorRecordingNotFound` | Refuse/quarantine missing anchor data; do not confuse with a small range miss. |
| `AnchorCycleDetected` | Refuse immediately as corrupt recorded-chain structure. |
| `AnchorOutOfScope` | Refuse for merge stitching unless item (5) adds an explicit scope-specific exception. |
| `TrackSectionsMissing` | Refuse for v6+ recorded-chain stitching; legacy absolute paths are outside this resolver contract. |
| `PoseNonFinite` | Refuse immediately as corrupt pose data; do not fall through to a stitch without an explicit recovery rule. |
| `Other` / `PreconditionFailed` | Refuse by default and preserve `Reason` in logs for follow-up classification. |

Item (5)'s decision logic stays out of this plan. New fields it
discovers it needs become a follow-up plan, not churn here.

---

## Risk and rollback

- **Migration risk:** low. The direct production resolver call clusters
  and wrapper callers are named with file:line. Most get `out _`; the
  recorder, BG recorder, recorded-relative traces, standalone-KSC log,
  and boundary seam get failure-aware logging. Test harness callers in
  `ResolverPoseHasher.cs`, plus
  `FlightRecorder.RecordedAnchorPoseOverrideForTesting`, also need
  mechanical updates. No external consumers (resolver is `internal`).
- **API churn risk:** zero outside the mod.
- **Logging risk:** low. Same 16 resolver WARN sites and same keywords remain;
  no new dedupe keys. Some private fallback paths may stop emitting a
  later broader outer WARN after a more specific inner WARN, so the
  propagated failure is not overwritten. Two recorder WARN lines change
  `reason=` from a constant to the resolver's propagated string — this
  is the bug fix.
- **Test risk:** the existing 1177-line resolver test file is the
  main assertion target, and `ResolverPoseHasher.cs` plus
  `EnvironmentTrackingIntegrationTests.cs` are additional harness
  consumers that need mechanical signature updates. Each resolver test
  gains one assertion; no test removed.
- **Rollback:** pure refactor of a single `internal` API. Revert
  restores the `bool` return plus the recorder constant-overwrite
  bugs. No data migrated, no format change, no scenario-state shape
  change.

---

## Out of scope

- Items (1), (2), (5) — separate plans. Item (5) reads this file as
  its API contract.
- `ParsekFlight.TryUseRelativeAbsoluteShadowFallback` and
  `LegacyDebrisShadowGate.TryUseLegacyDebrisShadowFallback` stay
  intact — intentional post-resolver shadow consumers. This plan
  plumbs failure context to their call sites for richer logging and
  future policy, but it does not change their current decision logic.
  A later item may gate shadow fallback on outcomes such as
  `OutOfSectionRange` versus `AnchorOutOfScope`.
- `FindTrackSectionForUT` endUT-ε bug
  (`docs/dev/todo-and-known-bugs.md:36-41`) and sparse-relative
  debris discontinuity (`:45-50`) — separate open bugs.

---

## Implementation order

1. Define `RelativeAnchorResolveOutcome` and
   `RelativeAnchorResolveFailure` in a sibling file
   `RelativeAnchorResolveFailure.cs` (the resolver file is already
   1377 lines).
2. Replace `WarnUnresolved` with a factory that builds the struct
   AND emits the log via `WarnRateLimited`. The struct is built on
   every call; emission stays rate-limited by the existing key
   (`relative-anchor-resolver|rec|anchor|reason|section`). Verify
   `suppressed=N` counts in playtest logs are unchanged.
   Pass explicit outcomes at range sites instead of deriving
   `NoSectionAtUT` from `sectionIndex`.
3. Add `out RelativeAnchorResolveFailure failure` to the three
   `internal` resolver entry points (`TryResolveAnchorPose`,
   `TryResolveRecordingPose`, `TryResolveRelativeSectionPose`).
4. Add `out failure` to private helpers that can emit resolver WARNs,
   including `TryResolveAbsoluteSectionPose`,
   `TryResolveAbsoluteFramesPose`, `TryResolveOrbitalSectionPose`,
   `TryResolveSmallSectionGapPose`, `TryResolveAbsoluteBracketPose`,
   `TryBuildAbsolutePoseFromPoint`, and
   `TryResolveSameChainContinuationPose`. Preserve
   `false/default` only for non-logging probe misses.
5. Migrate the call clusters in order: recorder, BG recorder,
   anchor-candidate enumeration, standalone KSC/Map, recorded-relative,
   standalone wrapper plus its KSC/map-presence callers, boundary
   stitching, resolver test harness callers, and
   `RecordedAnchorPoseOverrideForTesting`. Steps 3-5 land atomically in
   one commit; the caller/test surface is broad enough that temporary
   overload shims add more risk than value. Do not leave a
   compile-broken commit between signature and caller migrations.
6. Add synthetic failures for wrapper guard paths and boundary-local
   shadow availability so `false/default` and parent-chain shadow
   misattribution cannot regress.
7. Add new tests per the test plan; update existing assertions.
8. Run `dotnet test`, build, verify deployed DLL per
   `.claude/CLAUDE.md`.
9. Update `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` in
   the final commit.
