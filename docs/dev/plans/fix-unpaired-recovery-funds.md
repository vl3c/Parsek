# Fix Plan: Unpaired Recovery-Funds Requests

## Problem Statement

The playtest log `logs/2026-05-10_1713/KSP.log` contains:

```text
[Parsek][WARN][LedgerOrchestrator] FlushStalePendingRecoveryFunds (rewind end): evicting 2 unclaimed recovery request(s) that never received a paired FundsChanged(VesselRecovery) event. Entries: [vessel='Gerdorf Kerman' ut=286.3, vessel='#autoLOC_501224' ut=288.7]
```

The two stale requests were queued after `onVesselRecovered` fired outside
FLIGHT. Neither request ever saw a recorded `FundsChanged(VesselRecovery)` event:
the saves around both recoveries wrote `events=0`, and the log contains no
`Game state: FundsChanged ... (VesselRecovery)` line.

The warning is currently useful as cleanup, but it is too late and too noisy for
recoveries that cannot produce a Parsek ledger event. The fix should distinguish
"stock computed no payout or a payout below Parsek's funds-event threshold" from
"stock computed a ledger-worthy payout but Parsek never saw the paired funds
event".

## Diagnosis Corrections

- A type-only `Debris`/`EVA` skip is not enough. It would not handle the
  localized `#autoLOC_501224` / `Jumping Flea` stale request if that callback
  is a normal craft type.
- Normalizing recovered names before pending-tree ownership checks is unsafe
  unless matching accepts both raw and normalized forms.
- Skipping deferred `VesselType.EVA` requests after immediate-pair failure can
  drop a real delayed EVA payout. The no-payout decision must come from stock's
  recovery payout context, not from vessel type alone.
- The localized `Jumping Flea` callback is plausibly a positive-but-small
  recovery. `GameStateRecorder.OnFundsChanged` ignores `Math.Abs(delta) < 100.0`
  before emitting any `GameStateEvent`, so a stock recovery payout below that
  threshold will never create a pair. The plan must classify this as
  below-ledger-threshold, not as a missing event.

## Current Flow

`ParsekScenario.OnVesselRecovered`:

- ignores ghost-map vessels and rewind stripping callbacks;
- reads `pv.vesselName` directly;
- updates matching pending recordings to `TerminalState.Recovered`;
- if outside FLIGHT and no pending tree owns the vessel, calls
  `LedgerOrchestrator.OnVesselRecoveryFunds(now, vesselName, fromTrackingStation, pv.vesselType)`.

`LedgerRecoveryFundsPairing.OnVesselRecoveryFunds`:

- first tries to add a recovery funds action immediately by finding a
  `FundsChanged` event with key `VesselRecovery` inside `0.1s`;
- if that succeeds, it creates `FundsEarning(Recovery)` from the positive
  `valueAfter - valueBefore` delta;
- if no event exists and `vesselType == VesselType.Debris`, it skips queueing;
- otherwise it queues a pending request for later
  `OnRecoveryFundsEventRecorded`.

`GameStateRecorder.OnFundsChanged` emits a `GameStateEvent` with
`eventType=FundsChanged`, `key=reason.ToString()`, and no vessel-name `detail`.
It also returns before emit when `Math.Abs(delta) < FundsThreshold`; the current
threshold is `100.0`. Therefore deferred matching is effectively UT-only in
production unless another producer starts populating `detail`, and any recovery
payout below the recorder threshold will never produce a pair.

## KSP API Surface

The local KSP 1.12.5 `Assembly-CSharp.xml` says:

- `GameEvents.onVesselRecoveryProcessing` fires in Space Center or Tracking
  Station when a vessel is recovered and before `onVesselRecovered`.
- `GameEvents.onVesselRecovered` fires after science data and part values have
  been accounted for.

Reflection against the local assembly shows:

```text
onVesselRecoveryProcessing =
  EventData<ProtoVessel, KSP.UI.Screens.MissionRecoveryDialog, float>

onVesselRecovered =
  EventData<ProtoVessel, bool>
```

`MissionRecoveryDialog` exposes public fields:

- `double beforeMissionFunds`
- `double fundsEarned`
- `double totalFunds`

This is the missing signal: Parsek can know, before `onVesselRecovered`, whether
stock recovery computed a positive funds payout.

## Log-Specific Diagnosis

`Gerdorf Kerman`:

- Appears to be the stand-in side of the Jebediah -> Gerdorf reservation.
- KSP logs `[VesselRecovery]: Gerdorf Kerman recovered At LaunchPad. Recovery Value: 100.0%`.
- Parsek records no `FundsChanged(VesselRecovery)` event near `ut=286.3`.
- The stand-in context explains why this callback is suspicious, but it is not
  the pair-match cause. No funds event was recorded under either name.

`#autoLOC_501224`:

- Resolves to `Jumping Flea` in KSP localization.
- The callback name was raw because `ParsekScenario.OnVesselRecovered` passes
  `pv.vesselName` directly.
- Localization was not the pair-match cause in this log because no
  `FundsChanged(VesselRecovery)` event existed. It still matters for terminal
  matching and ledger attribution.
- Strong suspect for the missing pair: `Jumping Flea` is a cheap tutorial/default
  craft, and `GameStateRecorder.OnFundsChanged` filters funds deltas below
  `100.0` before emit. If stock recovery computed a positive but sub-threshold
  payout, Parsek intentionally recorded no `FundsChanged(VesselRecovery)`.

Both cases should be classified through the recovery-processing payout context:

- `fundsEarned <= zero tolerance`: do not queue; log an explicit no-payout skip.
- `0 < fundsEarned < FundsThreshold`: do not queue; log an explicit
  below-ledger-threshold skip.
- `fundsEarned >= FundsThreshold`: require a real
  `FundsChanged(VesselRecovery)` pair; keep pending/deferred behavior and warn
  if the pair never arrives.

## Goals

1. Never fabricate recovery funds without a stock `FundsChanged(VesselRecovery)`
   delta.
2. Do not enqueue recoveries where stock already reported zero funds earned or a
   payout smaller than Parsek's `FundsChanged` emit threshold.
3. Preserve callback-before-funds-event recovery behavior for all vessel types,
   including EVA, when stock reports a positive payout.
4. Normalize names for display and attribution while matching pending recordings
   against both raw and normalized names.
5. Keep rewind-end flushing as the guardrail for positive-payout recoveries whose
   paired funds event never arrives.

## Proposed Implementation

### 0. Diagnostic-First Rollout

Ship the recovery-processing context capture and enriched logs before changing
queue suppression semantics if implementation risk feels non-trivial. This first
commit should:

- subscribe to `onVesselRecoveryProcessing`;
- log raw/normalized names, vessel type, recovery factor, and
  `MissionRecoveryDialog` funds fields;
- enrich pending and flush logs with vessel type and any captured expected funds;
- avoid changing queue/no-queue decisions.

Then capture one playtest log that recovers:

- an EVA/kerbal or stand-in;
- `Jumping Flea` / `#autoLOC_501224`;
- one normal funded vessel.

This confirms actual `VesselType`, `fundsEarned`, and threshold behavior before
the semantic suppression change lands. If implementation is small enough to land
in one commit, keep the diagnostic logging in the same commit and make the tests
pin the threshold-classification decisions.

### 1. Capture Recovery Processing Context

Subscribe in `ParsekScenario` alongside the existing recovery callback:

```csharp
GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessing);
```

Unsubscribe in the same lifecycle path as `onVesselRecovered`.

Add a small static/internal cache owned by `ParsekScenario` or a new helper
`RecoveryPayoutContextStore`:

```csharp
internal sealed class RecoveryPayoutContext
{
    public uint PersistentId;
    public string RawVesselName;
    public string NormalizedVesselName;
    public VesselType VesselType;
    public double Ut;
    public double BeforeFunds;
    public double FundsEarned;
    public double TotalFunds;
    public float RecoveryFactor;
}
```

`OnVesselRecoveryProcessing(ProtoVessel pv, MissionRecoveryDialog dialog,
float recoveryFactor)` should:

- ignore null `pv`;
- ignore ghost-map vessels;
- capture raw and normalized names;
- copy `dialog.beforeMissionFunds`, `dialog.fundsEarned`, and
  `dialog.totalFunds` when `dialog != null`;
- store by `pv.persistentId` when available, with a fallback bucket keyed by
  normalized/raw name plus a tight UT window;
- log a concise diagnostic:

```text
[Parsek][VERBOSE][Scenario] Recovery processing captured: vessel='Jumping Flea' raw='#autoLOC_501224' pid=... type=Ship fundsEarned=0 before=... total=... factor=...
```

Do not add a second generic "name normalized" `Scenario` log. `Recording.ResolveLocalizedName`
already logs successful localization under the `Recording` tag; the recovery
diagnostic should include both raw and normalized names because they are useful
for this flow, not because localization itself needs another log line.

Retention:

- `OnVesselRecovered` should read the matching context without consuming it, so a
  delayed `FundsChanged(VesselRecovery)` can still stamp vessel identity;
- mark a context as used once it successfully stamps a funds event, so it cannot
  stamp a later unrelated recovery event inside the same window;
- expire old contexts on scene switch, KSP load, and rewind end;
- keep the expiry window short, for example 30 real seconds or a small UT window,
  because `onVesselRecoveryProcessing` and `onVesselRecovered` are adjacent stock
  callbacks.

### 2. Route Recovery Funds With Payout Context

Change the orchestrator entry point to accept a context or an explicit
funds expectation:

```csharp
internal static void OnVesselRecoveryFunds(
    double ut,
    RecoveredVesselIdentity identity,
    bool fromTrackingStation,
    VesselType vesselType,
    RecoveryPayoutContext payoutContext = null)
```

If a positive `FundsChanged(VesselRecovery)` event is already present, keep the
current immediate-pair behavior. The event delta remains authoritative for the
ledger amount.

If immediate pairing fails:

- if `payoutContext != null && payoutContext.FundsEarned <= RecoveryFundsZeroTolerance`,
  skip deferred queueing and log the explicit no-payout decision;
- if `payoutContext != null && payoutContext.FundsEarned > RecoveryFundsZeroTolerance`
  but `payoutContext.FundsEarned < RecoveryFundsEventThreshold`, skip deferred
  queueing and log that stock reported a positive payout below Parsek's
  `FundsChanged` emit threshold;
- if `payoutContext != null && payoutContext.FundsEarned >= RecoveryFundsEventThreshold`,
  enqueue and include `expectedFunds` in the pending request;
- if `payoutContext == null`, keep existing behavior for non-debris vessels and
  existing debris skip behavior as a compatibility fallback.

Include current recorder suppression context in the deferred/skip diagnostic
when cheaply available (`GameStateRecorder.SuppressResourceEvents` and
`GameStateRecorder.IsReplayingActions`). Do not use those flags alone as the
queue/no-queue decision in the first implementation; they are diagnostic context
for explaining why a ledger-worthy positive payout might not emit a funds event.

Do not introduce a broad `VesselType.EVA` no-defer rule. EVA should only skip
when stock's processing context says `fundsEarned == 0`. This preserves delayed
positive EVA payouts if KSP ever emits them.

Suggested tolerance:

```csharp
internal const double RecoveryFundsZeroTolerance = 0.01;
internal const double RecoveryFundsEventThreshold = GameStateRecorder.FundsThreshold;
```

This may require making `GameStateRecorder.FundsThreshold` `internal const` or
adding an internal read-only property so the recovery router and tests use the
same threshold as `OnFundsChanged`.

New no-payout log:

```text
[Parsek][VERBOSE][LedgerOrchestrator] OnVesselRecoveryFunds: vessel='Jumping Flea' raw='#autoLOC_501224' type=Ship ut=288.7 stock recovery fundsEarned=0.0 — skipping deferred recovery-funds pairing
```

New below-threshold log:

```text
[Parsek][VERBOSE][LedgerOrchestrator] OnVesselRecoveryFunds: vessel='Jumping Flea' raw='#autoLOC_501224' type=Ship ut=288.7 stock recovery fundsEarned=42.0 below FundsChanged threshold=100.0 — skipping deferred recovery-funds pairing
```

Positive-payout pending log:

```text
[Parsek][VERBOSE][LedgerOrchestrator] OnVesselRecoveryFunds: deferred pairing for vessel='Foo' type=Ship ut=123.4 expectedFunds=250.0 until FundsChanged(VesselRecovery) is recorded
```

### 3. Make Raw/Normalized Name Matching Mandatory

Introduce a small value type:

```csharp
internal struct RecoveredVesselIdentity
{
    public string RawName;
    public string NormalizedName;
}
```

Add pure matching helpers in `ParsekScenario` or a shared helper:

```csharp
internal static bool VesselNameMatchesRecoveredIdentity(
    string recordingName,
    RecoveredVesselIdentity identity,
    Func<string, string> normalizer = null)
```

Matching rules:

1. exact `recordingName == identity.RawName`;
2. exact `recordingName == identity.NormalizedName`;
3. if a normalizer is available, `normalizer(recordingName) == identity.NormalizedName`.

Locale limitation: `Recording.ResolveLocalizedName` uses the current KSP
`Localizer.Format`, not the locale active when a historical recording was
captured. Therefore the matcher must never depend solely on re-localizing stored
recording names. Raw exact and stored-normalized exact matches are the primary
contract; re-localizing `recordingName` is only a fallback. If a user changes KSP
language between recording and recovery and the stored recording name is a
human-readable string in the old locale, recovery attribution can remain
ambiguous. Log this as an unmatched attribution context rather than forcing a
possibly wrong match.

Use this helper in:

- `UpdateRecordingsForTerminalEvent`;
- `HasPendingLedgerRecordingForVessel`;
- `ShouldPatchRecoveryFundsOutsideFlight`;
- recovery recording attribution (`PickRecoveryRecordingId` or a new overload).

This fixes the review's duplicate-routing concern: normalizing `#autoLOC_501224`
to `Jumping Flea` must not make Parsek miss a pending recording that still stores
the raw key.

Implementation shape options:

- Minimal: overload `ShouldPatchRecoveryFundsOutsideFlight` and
  `HasPendingLedgerRecordingForVessel` to accept `RecoveredVesselIdentity`, while
  leaving old string overloads for tests/legacy callers.
- Cleaner: route all recovery/termination name matching through
  `RecoveredVesselIdentity` and keep string overloads as thin wrappers.

### 4. Enrich Pending Request Logs

Extend `PendingRecoveryFundsRequest`:

```csharp
private struct PendingRecoveryFundsRequest
{
    public double Ut;
    public RecoveredVesselIdentity Identity;
    public VesselType VesselType;
    public bool FromTrackingStation;
    public double ExpectedFunds;
    public bool HasExpectedFunds;
}
```

Flush logs should include enough context to distinguish expected no-payout from
missing positive-payout:

```text
Entries: [vessel='Foo' raw='#autoLOC_...' type=Ship ut=123.4 expectedFunds=250.0]
```

The old flush warning should become rarer and more meaningful: it should mostly
mean "stock said there should be positive funds, but no `FundsChanged` pair was
captured before the lifecycle boundary."

### 5. Keep Existing Event-Dedup Semantics

Do not use `MissionRecoveryDialog.fundsEarned` as the ledger amount. Continue to
use the actual `FundsChanged(VesselRecovery)` delta for `FundsEarning(Recovery)`.

Do not change `BuildRecoveryEventDedupKey`: it correctly fingerprints the actual
funds event.

Do not change the pair key to depend on names yet. The funds event still has no
native vessel identity. The payout context can improve logs and skip decisions;
the true pair remains `VesselRecovery` reason + tight UT + per-event dedup.

### 6. Stamp FundsChanged Detail From Recovery Context

Once recovery-processing context exists, stamp the next
`FundsChanged(VesselRecovery)` with normalized vessel name/type in `detail`.
That makes the existing name-match tier useful in production and fixes recovery
attribution at the root instead of only suppressing stale warnings.

Implementation option:

- `GameStateRecorder.OnFundsChanged` asks the recovery context store for the
  best unused recovery-processing context within the same tight UT window when
  `reason == VesselRecovery`, preferring a matching `fundsEarned` amount when
  available before falling back to contexts without a known amount;
- when found, write detail fields such as
  `vessel=Jumping Flea;raw=#autoLOC_501224;type=Ship;expectedFunds=42`;
- leave `detail` empty when no context exists.

This should be included in the behavioral fix unless the context proves
unreliable in runtime validation. The existing UT+dedup pairing remains the
fallback.

## Tests

Add focused xUnit coverage in
`Source/Parsek.Tests/GameStateRecorderLedgerTests.cs`:

1. `OnVesselRecoveryFunds_ZeroPayoutContext_DoesNotEnqueueOrWarn`
   - pass a context with `FundsEarned = 0`;
   - use a normal vessel type such as `VesselType.Ship`;
   - assert pending count stays 0;
   - flush and assert no `FlushStalePendingRecoveryFunds` warning;
   - assert the no-payout skip log includes vessel type and expected/earned
     funds.

2. `OnVesselRecoveryFunds_LocalizedZeroPayoutContext_DoesNotEnqueue`
   - identity raw `#autoLOC_501224`, normalized `Jumping Flea`;
   - context `FundsEarned = 0`;
   - assert skip log uses both names and does not enqueue.

3. `OnVesselRecoveryFunds_LocalizedBelowThresholdPayoutContext_DoesNotEnqueue`
   - identity raw `#autoLOC_501224`, normalized `Jumping Flea`;
   - context `FundsEarned = GameStateRecorder.FundsThreshold - 1`;
   - no funds event exists;
   - assert no enqueue and log mentions below threshold.

4. `OnVesselRecoveryFunds_EvaZeroPayoutContext_DoesNotEnqueue`
   - same as above with `VesselType.EVA`;
   - this replaces a type-only EVA skip test.

5. `OnVesselRecoveryFunds_EvaPositivePayoutCallbackBeforeEvent_DefersAndPairs`
   - call recovery with `VesselType.EVA`, context `FundsEarned > 0`, and no
     funds event yet;
   - assert pending count becomes 1;
   - add a matching `FundsChanged(VesselRecovery)` event;
   - call `OnRecoveryFundsEventRecorded`;
   - assert the pending request drains and a `FundsEarning(Recovery)` action is
     added.

6. `OnVesselRecoveryFunds_PositivePayoutContext_MissingEventStillFlushesWarn`
   - context `FundsEarned >= GameStateRecorder.FundsThreshold`, no funds event;
   - flush;
   - assert warning includes `expectedFunds`.

7. `OnFundsChanged_VesselRecoveryBelowThreshold_DoesNotEmitAndRecoveryContextSkips`
   - exercise the threshold-filtered case directly at the helper seam if
     `OnFundsChanged` remains private;
   - assert no `GameStateEvent` is expected and the recovery router does not
     enqueue.

8. Keep and update existing debris tests:
   - debris with immediate funds event still adds recovery action;
   - debris without context/no event still skips as the legacy fallback.

Add/extend tests in `ParsekScenarioRecoveryRoutingTests.cs`:

1. `RecoveredIdentity_MatchesRawPendingRecording`
   - recording name `#autoLOC_501224`;
   - identity raw `#autoLOC_501224`, normalized `Jumping Flea`;
   - assert pending-owner guard sees the match.

2. `RecoveredIdentity_MatchesNormalizedPendingRecording`
   - recording name `Jumping Flea`;
   - same identity;
   - assert match.

3. `RecoveredIdentity_NormalizesRecordingNameForComparison`
   - inject normalizer mapping raw key to `Jumping Flea`;
   - assert a raw stored recording can match a normalized callback.

4. `ShouldPatchRecoveryFundsOutsideFlight_PendingTreeOwnsRawLocalizedVessel_ReturnsFalse`
   - this directly covers the review's duplicate-routing risk.

Add tests for the recovery-processing context cache if it is factored into a
helper:

- stores by persistent id;
- marks funds-event detail stamping as single-use while still allowing the
  subsequent `OnVesselRecovered` callback to read the same context;
- falls back to raw/normalized name + UT when pid is unavailable;
- expires stale contexts.

Add a small lifecycle assertion around `consumedRecoveryEventKeys` if the
implementation changes when recovery contexts are cleared. Current code clears
consumed recovery event keys on KSP load and uses per-event fingerprints; the
plan does not require changing it, but the test/comment should document why a
rewind or scene-switch pending flush does not also need to clear consumed keys.

Add tests for `FundsChanged` detail stamping:

- a matching recovery-processing context stamps `detail` on the next
  `FundsChanged(VesselRecovery)`;
- non-recovery `FundsChanged` events are not stamped;
- stale contexts are not stamped;
- deferred pairing prefers the stamped vessel-name match over nearest UT.

Runtime/in-game validation:

- Recover an EVA kerbal/stand-in outside FLIGHT and verify a zero-payout context
  produces the explicit no-payout skip, not a later rewind-end stale flush.
- Recover `Jumping Flea` / `#autoLOC_501224` and verify whether stock reports
  zero or sub-threshold `fundsEarned`; in both cases the recovery should log a
  skip reason and not later flush as stale.
- Recover a normal funded vessel outside FLIGHT and verify
  `VesselRecovery funds patched` still appears with a positive amount.
- If possible, capture a callback-before-funds ordering case and confirm positive
  payout contexts still defer and pair.

## Documentation Updates

During implementation, update `docs/dev/todo-and-known-bugs.md` with a new item
for the 2026-05-10 log:

- source lines: 9259, 9297, 34740;
- symptoms: pending recovery funds for `Gerdorf Kerman` and `#autoLOC_501224`;
- diagnosis: no stock `FundsChanged(VesselRecovery)` was captured; stand-in and
  localization names are secondary context, not the pair-match cause;
- fix: capture `onVesselRecoveryProcessing` payout context, skip zero-payout
  and below-threshold recoveries, preserve ledger-worthy positive-payout
  deferral, stamp recovery identity onto `FundsChanged(VesselRecovery)`, and use
  raw/normalized identity matching.

No `CHANGELOG.md` entry is needed for the plan-only commit. Add one when the
behavioral fix is implemented.

## Acceptance Criteria

- The reproduction log shape no longer produces a rewind-end
  `FlushStalePendingRecoveryFunds` warning when stock recovery processing reports
  zero `fundsEarned` or a positive payout below `GameStateRecorder.FundsThreshold`.
- A normal recovery with a positive `FundsChanged(VesselRecovery)` still creates
  exactly one `FundsEarning(Recovery)` ledger action.
- A positive-payout callback-before-funds-event recovery still pairs when
  `OnRecoveryFundsEventRecorded` runs, including `VesselType.EVA`.
- `#autoLOC_501224` is normalized for display/attribution without bypassing a
  pending raw-name recording owner.
- Flush warnings remain for positive-payout contexts whose funds event never
  arrives and whose expected funds are at or above the funds-event threshold.
- `FundsChanged(VesselRecovery)` events produced with a matching recovery context
  carry vessel identity in `detail`, and deferred pairing uses that identity when
  available.
- Existing recovery funds, debris skip, dedup, and staleness tests pass.

## Risks And Mitigations

- Risk: `MissionRecoveryDialog.fundsEarned` can be populated after
  `onVesselRecoveryProcessing` rather than before.
  - Mitigation: runtime validation must log `fundsEarned`, `beforeMissionFunds`,
    and `totalFunds` for zero and positive recoveries. If the value is not ready
    at processing time, an all-zero dialog funds snapshot is treated as unknown
    and keeps the deferred-pairing path instead of suppressing the recovery.
    The subsequent `FundsChanged(VesselRecovery)` remains the ledger amount.

- Risk: The context cannot be matched reliably to `onVesselRecovered`.
  - Mitigation: key first by `ProtoVessel.persistentId`; fallback to
    raw/normalized name plus tight UT; log context misses and retain old behavior
    on miss.

- Risk: Name normalization changes terminal-state matching for legacy raw-name
  recordings.
  - Mitigation: raw/normalized dual matching is mandatory, not optional.

- Risk: The warning disappears for a real data-loss case.
  - Mitigation: only skip when stock recovery processing explicitly reports zero
    funds or a payout below the same threshold that suppresses
    `GameStateRecorder.OnFundsChanged`. Ledger-worthy positive expected funds
    still queue and still flush-warn if unmatched.

- Risk: Locale changes make `Recording.ResolveLocalizedName` return a different
  display name than the one persisted in older recordings.
  - Mitigation: exact raw and exact stored-name matches come before any
    re-localization fallback. Do not force a localized match when both exact
    forms miss; log the attribution miss.

- Risk: `consumedRecoveryEventKeys` retains keys across scene switches after the
  pending queue is flushed.
  - Mitigation: verify current lifecycle behavior before implementation. It is
    already cleared on KSP load via `ClearConsumedRecoveryEventKeys`; add a
    small test or comment for whether scene-switch/rewind clearing is needed. Do
    not change it unless a replayed recovery event can be incorrectly skipped.

## Non-Goals

- Do not synthesize recovery funds from KSP recovery UI text or vessel part
  costs.
- Do not retroactively credit the two evicted requests from the 2026-05-10 log.
- Do not make vessel type the authoritative no-payout classifier.
- Do not change rewind resource rollback semantics.
