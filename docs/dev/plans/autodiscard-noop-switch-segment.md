# Plan: Auto-discard no-op resumed (switch/Fly) recording segments

## Problem / Motivation

When you **Fly** a vessel from the Tracking Station / KSC marker, or **Switch-To** a
vessel via the map, Parsek starts a *resumed* recording segment (a
`SwitchSegmentSession` armed by `StockActionIntentMarker` consumed in
`ParsekFlight.TryConsumeStockActionIntent`). That segment is appended to the
vessel's tree (committed-clone, BG-member continuation, or a fresh standalone
tree) and, at the commit boundary (scene exit, or the next switch), it is
merged into the timeline.

Often the player switches to a vessel only to *look* at it — checks an orbit,
confirms a lander is fine — and switches/leaves without doing anything. Today
that produces a boring recorded segment that becomes a permanent part of the
ghost timeline. There is **no point keeping it**: it prolongs the ghost state
(the chain now replays "vessel coasts/sits doing nothing"), and the act of
switching only to spawn a no-op segment is wasted.

This is the same spirit as two existing mechanisms:

- `RecordingOptimizer.TrimBoringTail` — *"Trim to a minimal window from the
  start so the ghost finishes quickly and the real vessel spawns promptly."*
- `ParsekFlight.IsActiveTreeIdleOnPad` / `SceneExitInterceptor.TryAutoDiscardIdleActiveTree`
  — a whole-tree auto-discard when the vessel never moved (idle on pad).

**Goal:** when a resumed switch/Fly segment **changed nothing meaningful**,
auto-discard *just that segment* (preserving any committed history it continued
from) instead of committing it.

## Decisions locked with the user (2026-06-13)

1. **Hook scope:** Scene exit **and** in-flight re-switch (Map Switch-To while a
   prior session is armed). Not just scene exit.
2. **"Did something" (KEEP) definition:** trajectory / geometry / dock, **plus
   crew transfer**. Specifically:
   - Science is recorded by the action/ledger recorder, **not** flight
     recordings — **confirmed** (no `PartEventType`/`SegmentEventType` for
     science; science lives in `GameActions`). So science is irrelevant to this
     flight-recording decision.
   - EC drain is **irrelevant** (idle power use is not "doing something").
   - Crew transfer **counts as doing something** (`SegmentEventType.CrewTransfer`).
   - Fuel transfer between tanks **should** count as doing something (changes
     vessel internal state) — **but see the Known Limitation below; it is not
     detectable from currently-recorded data.**

## Plan-review fixes (applied 2026-06-13)

A clean-context code-grounded review caught two blockers and several hardening
items; all are folded into the design below.

- **B1 (BLOCKER) — live data is not in the `Recording` object until flushed.**
  The `FlightRecorder` accumulates into private buffers; the tree `Recording`'s
  `Points`/`TrackSections`/`PartEvents` are only populated at finalize
  (`BuildCaptureRecording`) — or by the same flush `IsActiveTreeIdleOnPad` uses:
  `ParsekFlight.FlushRecorderIntoActiveTreeForSerialization()`. The live-side
  evaluator **must flush first** (and guard `restoringActiveTree`), exactly like
  `IsActiveTreeIdleOnPad`. It must also confirm the segment recording **is** the
  active tree's `ActiveRecordingId` (the flush only populates that one); if the
  segment is in a non-active slot (torn-down tree) it cannot be evaluated →
  conservative **keep**.
- **B2 (BLOCKER) — Hook 1 control flow.** `HasActiveTree => activeTree != null`,
  so a scoped-discard that leaves an empty `activeTree` would still read
  `HasActiveTree == true` and mis-route the prefix. Hook 1 is therefore
  disposition-aware and does a full teardown (mirroring idle-on-pad) for the
  standalone / committed-restore-clone cases, then `return true`; it **defers**
  (returns false → normal commit) the BG-member-continuation case. See Hook 1.
- **S1** — the consume-site `superseded-by-new-switch` clear (when the
  pre-switch dialog fails to spawn or the re-entry guard fires) clears the
  session without commit/discard, so a no-op segment can leak as plain tree data
  on those rare paths. Documented as a known minor gap; addressed only if the
  injection is clean.
- **S2** — the evaluator also returns **keep** when `scenario.ActiveMergeJournal
  != null` (matches the Hook-2 handler guards; a re-fly merge must not race).
- **S4** — descendants detection: a null `PreSessionBranchPointIds` baseline →
  "unknown" → keep; `CollectSwitchSegmentSubtreeRecordingIds` count 0 (segment
  absent) → keep; count 1 → no children; count > 1 → has children.
- **S5** — `MaxDistanceFromLaunch` is per-recording but skips
  RELATIVE/OrbitalCheckpoint sections and measures body-fixed surface distance,
  so it is meaningless for an orbital coast. Use orbit-element comparison for
  orbital and a segment-local surface displacement for landed/surface.
- **S6** — do NOT reuse `GhostingTriggerClassifier.IsGhostingSegmentEvent` for
  the segment-event gate: it returns false for `CrewTransfer`, which the user
  wants KEPT. The gate is the hand-rolled "meaningful unless `type == TimeJump`".
- **S7** — `DockTargetVesselPid == 0` is a redundant defense; docking is
  primarily caught by the `Docked` part event + the dock child branch point.

## Background: what is actually recorded

`SegmentEnvironment` (TrackSection.cs):
| value | meaning | boring? |
|------|---------|---------|
| Atmospheric | flying in atmosphere | NO (keep) |
| ExoPropulsive | thrust in space | NO (keep) |
| ExoBallistic | coasting in space (Keplerian) | **YES** |
| SurfaceMobile | landed/splashed, moving (rover driving) | NO (keep) |
| SurfaceStationary | landed/splashed, stationary | **YES** |
| Approach | descending on airless body | NO (keep) |

`GhostPlaybackLogic.IsBoringEnvironment(env)` = `ExoBallistic || SurfaceStationary`.
So **"every track section is boring"** already means: never flew through
atmosphere, never thrust, never drove on the surface, never descended — i.e.
the vessel only coasted in space or sat still. (Rover driving is `SurfaceMobile`
= non-boring; a burn is `ExoPropulsive` = non-boring.)

`PartEventType`: structural/geometry/engine events. `GhostingTriggerClassifier
.IsGhostingTrigger(type)` returns false only for cosmetic events (lights,
thermal animations) and true for everything else (decouple, destroy, dock,
undock, parachute, deployables, gear, cargo, fairing, inventory, robotics,
**engine/RCS** ignite/shutdown/throttle).

`RecordingOptimizer.IsInertPartEventForTailTrim(evt)` = true for
EngineIgnited/EngineShutdown/RCSActivated/RCSThrottle **with value <= 0** (the
zero-throttle seed events the recorder emits on a resume — see
`FlightRecorder.EmitEngineOnlySeedEventsForPromotion`).

`SegmentEventType`: ControllerChange/Disabled/Enabled, CrewLost, **CrewTransfer**,
PartDestroyed/Removed/Added, **TimeJump** (a recording-discontinuity marker,
*not* a physical state change — `IsGhostingSegmentEvent(TimeJump) == false`).

Resources (`ResourceManifest`): `ResourceAmount` is **summed across all parts of
a vessel** (per-vessel totals), and `EndResources` is only captured at
finalize (`FlightRecorder.BuildCaptureRecording`). A tank-to-tank transfer does
not change vessel totals, emits no event, and the end manifest is not even
captured at the scene-exit decision point. **Pure intra-vessel fuel transfer is
therefore not detectable from recorded data.**

## The no-op predicate

New pure class `SwitchSegmentNoOpClassifier` (file
`Source/Parsek/SwitchSegmentNoOpClassifier.cs`), all `internal static`.

```
internal static bool IsNoOpSegment(
    Recording segment,
    bool segmentHasDescendantsOrClaimingChildren,
    out string keepReason)   // null when no-op (discard), else the reason it is kept
```

A segment is a **no-op** (→ discard) iff **all** of:

1. **`segment != null`** and the segment is structurally evaluable (has
   `Points`/`TrackSections` or is genuinely empty — see edge "empty segment").
2. **No descendants / claiming children** (`segmentHasDescendantsOrClaimingChildren
   == false`): no debris / EVA / dock / decouple / undock children were spawned
   during the segment. (Computed live-side by the wrapper from the tree
   topology + session-authored branch points.)
3. **No meaningful part events:** for every `PartEvent`, it is **not meaningful**,
   where *meaningful* = `IsGhostingTrigger(type) && !IsInertPartEventForTailTrim(evt)`.
   This excludes cosmetic events (lights/thermal — not triggers) and the
   zero-throttle engine/RCS resume seeds (inert), and keeps real
   decouple/dock/parachute/deploy/gear/cargo/robotics/positive-throttle events.
4. **No meaningful segment events:** for every `SegmentEvent`, `type == TimeJump`.
   Any other segment event (CrewTransfer, CrewLost, Controller*,
   PartDestroyed/Added/Removed) → keep. (TimeJump is an ignored
   recording-discontinuity marker, so warp-coasting stays discardable. This
   honors the user's "crew transfer counts.")
5. **No flag events** (`FlagEvents` null or empty).
6. **No dock target** (`DockTargetVesselPid == 0`).
7. **All track sections boring** (every `TrackSection.environment` satisfies
   `IsBoringEnvironment`). If `TrackSections` is null/empty, this gate is vacuously
   true and the displacement/orbit defenses below carry the decision.
8. **Orbit elements unchanged** (defense; only when the segment is orbital, i.e.
   has >= 2 distinct orbit checkpoints / orbit segments): first vs last Kepler
   elements within tolerance (semi-major-axis fractional, eccentricity absolute,
   inclination/LAN/AoP degrees). Redundant with (7) under normal recording, but
   guards against a missing-`ExoPropulsive`-classification edge.
9. **Surface displacement within tolerance** (defense; only when surface/landed):
   segment-LOCAL max horizontal surface range from `Points[0]` and max absolute
   altitude delta from `Points[0]` both under threshold. **Do not use
   `MaxDistanceFromLaunch`** (S5): it is computed per-recording but skips
   RELATIVE/OrbitalCheckpoint sections and measures body-fixed surface distance,
   so it is meaningless for an orbital coast. Compute the displacement directly
   from the segment's own `Points`. Catches drift / wheel creep not captured as a
   `SurfaceMobile` section.

Tolerances (constants, tuned conservatively so we only discard on a clear no-op):
- `OrbitSemiMajorAxisToleranceFraction = 1e-3` (0.1%)
- `OrbitEccentricityToleranceAbs = 1e-3`
- `OrbitAngleToleranceDeg = 0.1`
- `SurfaceDisplacementToleranceMeters = 30.0` (reuse the pad-localized constant
  intent; small drift OK)
- `AltitudeDeltaToleranceMeters = 30.0`

Conservative bias: **when in doubt, KEEP.** The predicate returns a non-null
`keepReason` for the first gate that fails, for logging.

### As-built deltas from the gate list above

- **Gate 9 (explicit surface-displacement) was folded into gate 7.** Surface
  motion is detected by the recorder's own `SurfaceMobile` (> 0.1 m/s)
  classification, which gate 7 already treats as non-boring → keep. A separate
  `Points[0]`-relative displacement metric was dropped: it would have had to
  re-derive lat/lon distance, which is meaningless / hazardous under
  `ReferenceFrame.Relative` sections (where lat/lon hold metre offsets, not
  degrees) and for orbital ground tracks. `SurfaceStationary` already means the
  game itself classified the vessel as not moving.
- **Descendants (gate 2) are detected via the subtree recording count**
  (`CollectSwitchSegmentSubtreeRecordingIds(...).Count > 1`), not a separate
  claiming-branch-point scan: dock / undock / EVA / decouple / breakup all
  create child recordings that the downward branch-point walk collects, and
  docking is independently caught by the `Docked` part event + dock target.
- **Data-loss safeguard added** (Bug #290d precedent): a segment with no payload
  at all (< 2 points AND no sections AND no orbit segments) is KEPT
  (`insufficient-data`), since a failed sidecar load is indistinguishable from a
  nothing-recorded switch. The only cost is not cleaning up a near-empty
  sub-second segment (negligible ghost cost); a genuine no-op coast / sit always
  has payload and is still discarded.

### Live-side wrapper

The evaluator lives on `ParsekFlight` (it owns the recorder + activeTree + the
flush) and delegates topology to `RecordingStore` and the decision to the pure
classifier:

`ParsekFlight.TryEvaluateActiveSwitchSegmentNoOp(out string reason, out SwitchSegmentDisposition disposition)`:
1. **Re-Fly / journal / restore guards (keep):** return false if
   `restoringActiveTree`, or `scenario.ActiveReFlySessionMarker != null`, or
   `scenario.ActiveMergeJournal != null` (S2), or no
   `scenario.ActiveSwitchSegmentSession`.
2. **Flush (B1):** call `FlushRecorderIntoActiveTreeForSerialization()` so the
   live recorder's buffers + open TrackSection land on the tree recordings
   (non-destructive; identical to an OnSave tick — same as `IsActiveTreeIdleOnPad`).
3. **Resolve** the segment tree (`RecordingStore.FindSegmentTreeForSession`) and
   the segment recording (`session.ActiveSegmentRecordingId`). If the tree is
   null, the recording is missing, or the recording is **not** the active tree's
   `ActiveRecordingId` (so the flush did not populate it) → keep (B1).
4. **Descendants (S4):** via `RecordingStore`:
   - `CollectSwitchSegmentSubtreeRecordingIds(tree, session)` count: 0 → keep
     (segment absent, can't evaluate); 1 → no children; > 1 → has children → keep.
   - `CollectSessionAuthoredBranchPointIds(tree, session)`: a null
     `PreSessionBranchPointIds` baseline → "unknown" → keep; any session-authored
     **claiming** branch point (`IsClaimingBranchPoint`: Dock/Board/Undock/EVA/
     JointBreak) → keep.
5. **Disposition (B2):** classify how a discard would resolve, for Hook 1's
   teardown choice:
   - `Standalone` — `activeTree.Recordings.Keys` minus the segment subtree is
     empty (the segment subtree IS the whole live tree).
   - `CommittedRestoreClone` — `committedTreeRestoreAttemptTreeId` is set and
     matches the session's tree/committed-tree id.
   - `BgMemberOrMixed` — the live tree has recordings outside the segment
     subtree that are not the restore-clone origin (defer at scene exit).
6. **Pure predicate:** `SwitchSegmentNoOpClassifier.IsNoOpSegment(segment,
   segmentHasDescendantsOrClaimingChildren: false, out keepReason)`.
7. **Log** the decision (Info on discard with disposition; Verbose with the
   keep-reason otherwise).

The segment recording is **not yet finalized** at the decision points, which is
exactly why step 2's flush is mandatory (B1). After the flush it carries live
`Points` / `TrackSections` / `PartEvents`, mirroring how `IsActiveTreeIdleOnPad`
flushes before reading `MaxDistanceFromLaunch`.

## Hook points

### Hook 1 — scene exit (primary commit boundary)

In `HighLogic_LoadScene_Patch.Prefix` (SceneExitInterceptor.cs), insert a step
**after** the cheap flight-exit filter + Discard-Re-Fly suppression peek and the
`var flight = ParsekFlight.Instance;` fetch, and **before** the
`flight == null || !flight.HasActiveTree` routing check (so the routing decision
is taken on post-discard state — B2):

```
if (flight != null
    && SceneExitInterceptor.TryAutoDiscardNoOpSwitchSegment(scene, flight))
{
    if (!SceneExitInterceptor.SafeWritePersistent(scene))
        return false;   // MAINMENU save failed: hard-block (mirrors idle-on-pad)
    return true;        // torn down; transition proceeds, OnSceneChangeRequested no-ops
}
```

`SceneExitInterceptor.TryAutoDiscardNoOpSwitchSegment(scene, flight)` (test seam
`TryAutoDiscardNoOpSwitchSegmentForTesting` mirrors `AutoDiscardIdleForTesting`):
- `flight.TryEvaluateActiveSwitchSegmentNoOp(out reason, out disposition)`; if
  not no-op, return false (fall through to the existing branches unchanged).
- Disposition-aware teardown (B2), via a new reason-aware entry on `ParsekFlight`:
  - **Standalone** (the segment subtree IS the whole live active tree) — reuse the
    **proven** whole-tree teardown body `AutoDiscardActiveTreeCore`
    (StopAllContinuations, CleanupGloops, ForceStop recorder + clear
    `PhysicsFramePatch.ActiveRecorder`, discard backgroundRecorder, null
    `activeTree`, `ClearSwitchSegmentSession`, ledger recalc rollback) — *exactly*
    what `AutoDiscardIdleActiveTree` already calls for an idle standalone tree, so
    zero new teardown risk. Returns true.
  - **CommittedRestoreClone** and **BgMemberOrMixed** — **defer** at scene exit
    (return false → normal dialog / auto-commit path, today's behavior). The
    committed-clone bookkeeping (`committedTreeRestoreAttemptTreeId`, clone-slot
    resolution) is intricate, and the BG-member case has live tree content beyond
    the segment that must still commit. Both are **covered by Hook 2** when the
    player re-switches in-flight (the proven `DiscardPriorAndSwitchTo` handles all
    dispositions). Deferring them at scene exit is a documented minor gap, not a
    regression — see "Out of scope".
- Returns true only for the Standalone teardown; the caller then re-saves
  persistent.sfs and returns true (identical to the idle-on-pad fast path).

Ordering vs the existing idle-on-pad fast path: the no-op-segment check runs
**before** `TryAutoDiscardIdleActiveTree`. A standalone no-op segment that also
happens to be idle-on-pad is caught here first (and torn down identically); a
non-idle orbital standalone no-op segment is caught here where idle-on-pad would
have missed it. The two never double-fire (the no-op path returns true and the
prefix exits).

### Hook 2 — in-flight re-switch (Map Switch-To, session armed = Case A)

In `MapFocusObjectOnSelectPatch`, the `PreSwitchDialogDecision.OpenDialog` case,
the real control structure is a ternary (`MapFocusObjectOnSelectPatch.cs:333-335`)
that picks the session vs no-session handler. Inject a guard **before** that
ternary, gated on `existingSession != null` (Case A only — S3): if the prior
session segment is a no-op, **silently scoped-discard it and switch** (no prompt)
via the existing `DiscardPriorAndSwitchTo` handler, skipping the
`pre-switch-dialog opening` Info line:

```
if (existingSession != null
    && (ParsekFlight.Instance?.TryEvaluateActiveSwitchSegmentNoOp(
            out reason, out _) ?? false))
{
    // prior resumed segment did nothing — drop it, switch with no prompt
    ParsekLog.Info("SwitchIntentPatch",
        $"pre-switch no-op auto-discard priorSessionId=... targetPid=...");
    DiscardPriorAndSwitchTo(vessel, existingSession);   // existing handler
    return false;
}
// else: existing ternary (open dialog Case A / Case B / Case C) unchanged
```

This **reuses `DiscardPriorAndSwitchTo` verbatim** — the same code the dialog's
Discard button already runs — so Hook 2 is low risk: it only auto-selects the
existing Discard path instead of prompting. The evaluator's flush (B1) and its
Re-Fly / merge-journal guards (S2) make this safe; `DiscardPriorAndSwitchTo`
itself already handles the post-discard recorder/scene-reload reconciliation
(it is exercised by the existing dialog path).

Scope guard: **only Case A** (session armed = a resumed segment). The no-session
Case B / Case C operate on the live `activeTree` (the original flight, not a
resumed segment) and must keep prompting as today — those are not in scope.

(TS Fly / KSC marker Fly while a prior session is armed are not an in-flight
case: by the time you reach the Tracking Station / Space Center you have already
left FLIGHT, so Hook 1 has already handled the prior session.)

## Setting / gating

Auto-discard fires **unconditionally** (no new setting), mirroring the existing
idle-on-pad auto-discard which also fires regardless of the autoMerge setting.
Rationale: a segment that changed nothing has nothing to confirm. A brief screen
message ("Switched-to recording discarded - nothing changed") + Info log gives
feedback. **Open question for review:** should it instead be gated behind a
default-on setting, or behind `IsAutoMerge`? (Leaning unconditional + logged.)

## Edge cases (explicit)

| scenario | section/events | decision |
|---|---|---|
| Coast in orbit, no input (incl. high warp) | all ExoBallistic, inert seeds, maybe TimeJump | **discard** |
| Sit landed, warp | all SurfaceStationary | **discard** |
| Rover driving | SurfaceMobile (non-boring) | keep |
| Plane / atmospheric flight | Atmospheric | keep |
| Any burn (even tiny) | ExoPropulsive + throttle>0 event + orbit changes | keep |
| Descent on airless body | Approach | keep |
| Docking / undocking | Docked/Undocked event + DockTargetVesselPid + child BP | keep |
| Decouple / staging | Decoupled event + child BP | keep |
| EVA | EVA claiming child BP | keep |
| Crew transfer (no movement) | CrewTransfer segment event | keep (user req) |
| Vessel destroyed mid-segment | Destroyed event / terminal Destroyed | keep |
| Flag plant | FlagEvents non-empty | keep |
| Parachute/gear/deploy/cargo/robotic/inventory | meaningful part event | keep |
| Light / thermal toggle only | cosmetic events only | **discard** |
| **Fuel transfer between tanks only** | no event, no total change, no section change | **discard (KNOWN LIMITATION)** |
| Re-Fly session active | — | keep (never touch) |
| Segment has descendants but vessel idle | child BP present | keep |
| Switched & immediately left (sub-second) | tiny / empty | discard |
| Empty segment (0 points) but events/dock/children all clean | — | discard (truly nothing) |
| Data-loss segment (sidecar epoch mismatch, 0 points but tree expects data) | — | keep (cannot determine) — guard like `IsTreeIdleOnPad`'s anyHasPoints |

### Known limitation: fuel transfer between tanks

A coasting segment whose *only* activity was a tank-to-tank fuel transfer will be
discarded, because intra-vessel transfer is **not currently recorded** (no event
type, per-vessel resource totals unchanged, end manifest not captured at the
decision point). The visual ghost does not represent fuel distribution, so no
*visual* content is lost (consistent with the project's "Visual & Recording
Design Principle"). Honoring "keep on fuel transfer" would require a new
resource-redistribution recording surface (a `ResourceTransfer` part/segment
event or per-part resource snapshots) — **out of scope for this PR; tracked in
todo-and-known-bugs.md.** Surfaced explicitly for the user / reviewer to confirm.

## Files touched

- **NEW** `Source/Parsek/SwitchSegmentNoOpClassifier.cs` — pure predicate +
  orbit/displacement helpers + tolerance constants + the `SwitchSegmentDisposition`
  enum. Fully unit-testable, no Unity dependency.
- `Source/Parsek/ParsekFlight.cs` — `TryEvaluateActiveSwitchSegmentNoOp` (flush +
  resolve + descendants + disposition + pure predicate) and
  `AutoDiscardNoOpSwitchSegment` (disposition-aware teardown reusing
  `AutoDiscardActiveTreeCore`'s recorder block).
- `Source/Parsek/RecordingStore.cs` — small internal helper(s) the evaluator
  calls for topology (reuse `FindSegmentTreeForSession`,
  `CollectSwitchSegmentSubtreeRecordingIds`, `CollectSessionAuthoredBranchPointIds`);
  no new storage state.
- `Source/Parsek/SceneExitInterceptor.cs` — `TryAutoDiscardNoOpSwitchSegment` +
  call in the LoadScene prefix (before the `!HasActiveTree` routing check) +
  test seam.
- `Source/Parsek/Patches/MapFocusObjectOnSelectPatch.cs` — Hook 2 guard in the
  Case A OpenDialog branch (reuses `DiscardPriorAndSwitchTo`).
- **Docs:** `CHANGELOG.md` (1 line), `docs/dev/todo-and-known-bugs.md` (feature +
  fuel-transfer limitation + S1 minor gap), `.claude/CLAUDE.md` (new file in the
  list).

**No schema change** — the feature reads existing recorded data and reuses the
existing scoped-discard path. Post-Change Checklist: no enum/serialized-field
change, so OnSave/OnLoad and generators are unaffected.

## Tests

- **xUnit `SwitchSegmentNoOpClassifierTests`** (pure, no Unity): one case per
  edge-case row above, building synthetic `Recording`s via the test generators.
  Assert discard vs keep + the `keepReason` string.
- **xUnit log-assertion** for `TryEvaluateActiveSwitchSegmentNoOp` gates
  (Re-Fly active → keep; no session → keep) via the source-gate / `TestSink`
  pattern where scenario state is needed (mirror existing SwitchSegment tests).
- **xUnit `SceneExitInterceptor` seam test:** `TryAutoDiscardNoOpSwitchSegment`
  invokes the scoped discard when the seam reports no-op; does not when it
  reports keep.
- **In-game (optional, RuntimeTests):** Fly to an orbiting vessel, do nothing,
  trigger scene exit → assert the segment recording is gone and committed
  history survives. Note in plan; add if cheap.

## Build / verify

`cd Source/Parsek.Tests && dotnet test` (does not deploy). Then a focused
in-game smoke if feasible. Verify deployed DLL hash before any KSP launch
(multiple worktrees active).

## Out of scope

- Recording resource redistribution (fuel transfer) — future feature.
- Auto-discarding the no-session live `activeTree` on re-switch (Case B/C) — that
  is the original flight, not a resumed segment.
- Changing the idle-on-pad whole-tree path.
