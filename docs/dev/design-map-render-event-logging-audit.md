# Audit + Design: Map / Tracking-Station Render-EVENT Logging Gaps

*Status: AUDIT + design note, pending a clean-context review before implementation.
Goal: close the gaps in the map / tracking-station RENDER-TRACER (`MapRenderTrace`)
so that EVERY visible render EVENT is captured in KSP.log as a clean, greppable,
EVENT-based (on-change / threshold-crossing) line — for every ghost, every surface, in
BOTH the flight map and the tracking station. This is OBSERVABILITY ONLY: it must not
change any render behaviour (the tracer is read-only instrumentation). Off by default
behind the existing `mapRenderTracing` setting; per-frame detail stays gated on Verbose.*

**Baseline branch:** `reaim-descent-render` (NOT yet merged to `origin/main` as of
2026-06-23; 13 commits ahead). This work branches from `origin/reaim-descent-render`, so
the PR targets that branch (or `main` if `reaim-descent-render` merges first). The
descent branch already added a per-frame `SceneSnapshot` dump (`MapRenderProbe`),
`NoteDescentRenderWindow` (gate latch in `MapRenderTrace`), and
`EmitRenderedPolylineSnapshot` — those are window-gated *snapshots*, not the on-change
*events* this work adds; they are complementary.

Related: `docs/dev/design-map-ts-render-tracer.md` (the tracer's own design doc; this
audit closes its deferred Tier-A events), `Source/Parsek/MapRenderTrace.cs`,
`Source/Parsek/MapRenderProbe.cs`, `Source/Parsek/GhostRenderTrace.cs` (flight sibling).

---

## Motivation

Debugging in-game render issues repeatedly fails because the log does not cleanly record
on-screen events. The user says "there's an extra trajectory line being rendered" or
"the icon stayed on the loiter" and an AI reading the log cannot tell **which surface**
that is or **when it changed**. The tracer already has the right tiers
(`EmitStructural` / `EmitOnChange` / `EmitWindowSnapshot` / `EmitAnomaly` / `EmitMarker` /
`EmitMarkerDecisionOnChange`). The job is to make each visible render surface emit a
clean on-change EVENT when it APPEARS, DISAPPEARS, or CHANGES IDENTITY/OWNER. Each event
line must carry **the ghost `persistentId` + `recordingId` + the surface + the event +
the before→after identity**, so one grep reconstructs "what appeared/disappeared/changed
on screen and when."

## Hard constraints (carried from the tracer's design)

1. **EVENT-based only.** Threshold crossings / state transitions / on-change. No new
   per-frame logging; no per-frame spam. Per-frame detail stays inside the existing
   detailed-window / Verbose tiers.
2. **Observability only — zero render-behaviour change.** Every add is a read of state
   the render code already computed, plus an emit. No new mutation of renderer / orbit /
   line / icon / marker / mesh state; no new `continue`/`return` that changes which
   surface draws.
3. **Off by default.** Gated behind `mapRenderTracing` (`MapRenderTrace.IsEnabled`) /
   `ghostRenderTracing` (`GhostRenderTrace.IsEnabled`). When disabled, every entry point
   is a bool check that returns before any formatting.
4. **Both scenes.** Flight map AND tracking station, for every ghost population
   (timeline, chain, loop, overlap).
5. **Pure helpers are unit-tested.** New formatters / predicates are `internal static`
   and Unity-ECall-free, per the repo's testing requirements.

---

## The audit matrix

Surfaces (rows) × lifecycle events (columns). Each cell is one of:

- **EVENT** — already a clean on-change EVENT line through the unified tracer
  (`MapRenderTrace` / `GhostRenderTrace`), carrying surface + identity.
- **truth-only** — observable, but only as per-frame *truth* on-change (probe Tier-B) or
  on a *different* legacy tag (`GhostOrbitLine` / `GhostOrbitIcon` / `GhostMap` /
  `DriverTag`), so it is not a unified, recordingId-bearing render EVENT.
- **MISSING** — no on-change emit at all; the transition is silent.

Columns: **Appears · Disappears · Moves/Rebinds · Identity/Owner change · Suppressed↔Restored**

| Surface | Appears | Disappears | Moves/Rebinds | Identity/Owner | Suppressed↔Restored |
| --- | --- | --- | --- | --- | --- |
| **Proto orbit line** (`ProtoOrbitLine`) | truth-only¹ | truth-only¹ | MISSING² | EVENT³ | truth-only¹ |
| **Proto icon** (`ProtoIcon`) | truth-only⁴ | truth-only⁴ | EVENT⁵ / MISSING² | EVENT³ | **MISSING**⁶ |
| **Non-orbital polyline leg** (`Polyline`) | **MISSING**⁷ | **MISSING**⁷ | n/a (continuous) | **MISSING**⁸ | (= disappears) |
| **Forward predicted arc** (`Polyline`, conflated) | **MISSING**⁹ | **MISSING**⁹ | n/a | **MISSING**⁹ | (= disappears) |
| **IMGUI flight-map marker** (`ImguiLabeledMarker`) | EVENT¹⁰ / MISSING¹¹ | EVENT¹⁰ / MISSING¹¹ | EVENT¹² | EVENT¹² / MISSING¹¹ | n/a |
| **IMGUI TS marker** (`AtmosphericMarker`) | EVENT¹³ | EVENT¹³ | EVENT¹³ | EVENT¹³ | n/a |
| **Flight-scene mesh** (`GhostRenderTrace`) | **MISSING**¹⁴ | **MISSING**¹⁴ | MISSING¹⁵ | MISSING¹⁵ | truth-only¹⁶ |

### Cell notes (exact current state)

1. **Orbit-line visibility.** `MapRenderProbe.Sample` emits `EmitTruthOnChange` for
   `line.active` (`MapRenderProbe.cs:379`) and `renderer.enabled` — a real on-change
   line, but keyed by **pid only**, no `recordingId`, no **reason**. The *decision* side
   (`GhostOrbitLinePatch.LogOrbitLineDecision`, `:697`) logs the reason
   (`visible-body-frame` / `polyline-owns-phase` / `below-atmosphere` / `stale-segment` /
   `parking-conic-loiter-hold` / `terminal-visible` / …) but under the legacy
   `GhostOrbitLine` tag via `VerboseOnChange`, NOT through `MapRenderTrace`. So a grep on
   `surface=ProtoOrbitLine` does not show *why* the line appeared/disappeared. The design
   doc's `LineVisibilityChange` Tier-A event is **not implemented**.
2. **Orbit rebind (`SegmentApplied`).** When a new `OrbitSegment` / source
   (`Segment`/`TerminalOrbit`/`StateVector`) / SOI body / loop epoch shift is applied to
   a ghost (`GhostMapPresence.UpdateGhostOrbitForRecording` `:7719`, `UpdateGhostOrbit`
   `:2704`, `ApplyOrbitToVessel` `:8939`), there is NO `MapRenderTrace` event — only
   `VerboseRateLimited` / `VerboseOnChange` under the `GhostMap` tag. The probe's
   `body-orbit` on-change (`MapRenderProbe.cs:408`) catches a *body/sma/ecc* change
   (truth-only, pid-only), but not a same-shape segment/source/loop-shift rebind. The
   design doc's `SegmentApplied` Tier-A event is **not implemented**.
3. **Identity.** `GhostCreated` (`GhostMapPresence.cs:11182`) and `GhostDestroyed`
   (`:2813` chain, `:4343` recording-index) ARE clean Tier-A structural events carrying
   `pid` + `rec=<recordingId>` + body + worldPos + reason. The proto line/icon are 1:1
   with a pid, so their identity is fixed at create. The `vesselPidToRecordingId`
   write/remove itself is not separately evented, but `GhostCreated`/`GhostDestroyed`
   bracket it.
4. **Icon visibility.** Probe emits `drawIcons` `EmitTruthOnChange`
   (`MapRenderProbe.cs:394`) — on-change, pid-only, no recordingId/reason. Same gap as 1.
5. **Icon teleport / off-orbit.** `icon-teleport` (`MapRenderProbe.cs:567`) and
   `icon-off-orbit` (`:643`) ARE Tier-C anomaly EVENTS (pid-only). They cover the visible
   "icon jumped / icon off its line" rebind symptom well. The *reason* for a legitimate
   rebind is still the missing `SegmentApplied` (note 2).
6. **Icon suppression.** `ghostsWithSuppressedIcon` flips drive whether the proto icon is
   the visible indicator vs the non-proto marker. The flip is observable only via the
   `GhostOrbitIcon` `icon-flip` `VerboseOnChange` (`GhostOrbitLinePatch.cs:296`,
   different tag, pid-only) and the per-pid suppress-enter/exit `VerboseRateLimited`
   (`:508`). There is NO `MapRenderTrace` event for the suppressed↔restored flip. The
   probe READS `IsIconSuppressed(pid)` as an anomaly guard (`MapRenderProbe.cs:549`,
   `:605`) but does NOT emit an on-change EVENT for it. This is half of the "the icon
   stayed on the loiter" debugging case.
7. **Polyline leg appear/disappear — the headline gap.** The Driver
   (`GhostTrajectoryPolylineRenderer.cs`) publishes ownership by a *silent* set mutation
   `drewNonOrbitalLegRecordings.Add(rec.RecordingId)` (`:3867`); the set is `Clear()`-ed
   at the top of every `LateUpdate` (`:3331`) and the deactivation sweep
   (`OnMapCameraPreCull`) hides meshes with no per-recording log. The only signals are a
   5 s-rate-limited `EmitMarker` per leg (`:4064`), a 5 s frame summary (`:3931`), and a
   `VerboseOnChange` keyed on the **drawn-COUNT** (`:3954`) — so a swap (A leaves, B
   enters, count unchanged) is silent, and there is no per-recording "this leg started /
   stopped drawing" EVENT. This is exactly the "extra trajectory line that won't go away"
   case: a reader cannot tell which recording's polyline appeared or disappeared, or when.
8. **Polyline ownership flip.** `IsRenderingNonOrbitalLeg(recId)` /
   `ResolveNonOrbitalLegOwnership` (`:399`/`:410`) read `drewNonOrbitalLegRecordings`.
   This is THE signal that hides/shows the proto orbit line (note 1). Its flip is silent.
9. **Forward predicted arc.** Drawn additively (`DecideForwardWindowForRecording`,
   `DrawForwardArc`) and logged via the SAME `EmitMarker(surface=Polyline,…)` with a
   `"FWD-ARC"` *string prefix* in the details (`:4126`), 5 s-rate-limited. No distinct
   surface, no appear/disappear event. A grep on `surface=Polyline` cannot separate the
   forward arc from the main leg.
10. **Flight-map marker (main loop).** `DrawMapMarkers` routes every *ghost-state* outcome
    (drawn-non-proto / drawn-proto-icon / skipped-*) through `EmitMarkerDecisionOnChange`
    (`ParsekUI.cs:1324` + the per-branch calls) — a clean on-change EVENT carrying
    `rec=<index>` + vessel + the decision disjuncts + outcome + ride + posSource.
11. **Flight-map marker (ghostless fallback + edge skips).** The recording-keyed
    "ghostless polyline marker" fallback (`ParsekUI.cs:1737-1776`) draws a marker but
    emits NO `EmitMarkerDecisionOnChange` — only `VerboseRateLimited` under `GhostMap`.
    Its five `continue` points (covered / not-rendering / already-chain-drawn /
    ghostless-hidden / ride-failed) are silent, so a chain ghost whose marker the engine
    retired mid-phase appears and disappears with no decision event. The `state == null`
    `continue` (`:1302`) and the boundary-overlap-secondary skip (`:1421` returns false)
    are likewise silent. **The TS path (note 13) has none of these gaps — it is the gold
    standard the flight-map path should match.**
12. **Flight-map identity / position-source.** Decision line is keyed by `recordingId`
    and carries `rec=<index>` + vessel + `posSource` (mesh/traj/polyline). A position-
    source change is on-change when drawn. Good — except the ghostless fallback (note 11)
    carries no identity.
13. **TS marker — clean.** `ParsekTrackingStation.DrawAtmosphericMarkers` routes EVERY
    path (overlap / loop-hidden / classify-skip / position-fail / draw) through
    `EmitMarkerDecisionOnChange` before any `continue` (`:439`, `:976`), carrying the
    finer `tsSkip=` reason. No silent appears/disappears. This is the reference contract.
14. **Flight-scene mesh appear/disappear.** `GhostPlaybackEngine.SpawnGhost` /
    `DestroyGhost` fire policy events (`OnGhostCreated`/`OnGhostDestroyed`) but emit NO
    `GhostRenderTrace` structural event. The first `GhostRenderTrace` line for a ghost is
    a next-frame `BeginFrame`/`AfterUpdate` with `reason=first-seen`; destroy is inferable
    only from a trailing `retired=true` on `AfterUpdate`. The flight scene has no
    equivalent of the map's `GhostCreated`/`GhostDestroyed`.
15. **Flight-scene rebind.** Loop-cycle reassignment (`state.loopCycleIndex = …`) and
    anchor changes are silent in `GhostRenderTrace`.
16. **Flight-scene visibility.** `EmitActivationDecision` (`GhostRenderTrace`) logs the
    `first-visible` (hidden→shown) transition; a re-hide (loop wrap / watch pause / leave
    range) is NOT evented (steady-hidden is silent). `active` (GameObject.activeSelf) is
    carried per-frame on `AfterUpdate` (truth-only).

### Audit summary

- **Map/TS proto surfaces:** lifecycle (`GhostCreated`/`GhostDestroyed`) and the visible
  anomalies (`icon-teleport`/`icon-off-orbit`/`line-blink`/`decision-vs-truth`/
  `polyline-orbit-overlap`) are solid. The gaps are: (a) **visibility transitions are
  truth-only, pid-only, reason-less** (no unified `LineVisibilityChange` with recordingId
  + reason); (b) **icon suppression flip has no MapRenderTrace event**; (c) **orbit
  rebind / `SegmentApplied` is decision-tag-only**.
- **Polyline:** **leg appear / disappear / ownership flip and forward-arc appear /
  disappear are entirely MISSING** as events. This is the single biggest gap and the
  literal "extra line being rendered" case.
- **IMGUI markers:** TS is clean; **flight-map has silent appears/disappears in the
  ghostless-fallback + edge-skip paths**. (Loop/overlap per-instance markers are ALREADY
  EVENT-covered on both scenes via `EmitMarkerDecisionOnChange` keyed `recId#cycle` —
  `ParsekUI.cs:1383`, `ParsekTrackingStation.cs:976` — so they are NOT a gap. The one
  remaining silent IMGUI hole besides the ghostless fallback is the flight
  boundary-overlap-secondary marker `DrawOneOverlapInstanceMarker` false-return at
  `ParsekUI.cs:1421`.)
- **Flight-scene mesh:** **spawn / despawn / rebind / re-hide have no clean events**;
  only first-seen / first-visible / per-frame truth.
- **Schema:** every new event must carry `recId=` (the design doc's intended schema; the
  current `BuildPrefix` carries only `pid=`). The probe and anomalies are pid-only today.

---

## Concrete missing emits to add

All adds are read-only (observability), gated, on-change. Prioritised.

### Schema prerequisite — recordingId on every line (`MapRenderTrace` only)

**Scope (review fix):** this applies to `MapRenderTrace` ONLY. `GhostRenderTrace.BuildPrefix`
already emits both `rec=<ShortId>` and `recId=<Token>` (`GhostRenderTrace.cs:984-985`), so
P3.F (flight mesh) needs no recId threading.

Add `recId` to the `MapRenderTrace` line prefix so every event carries `pid=` AND `recId=`:

- Extend `MapRenderTrace.BuildPrefix` to emit `recId=<token>` immediately after `pid=`,
  fed by a new trailing `string recId = null` parameter (default → `recId=<none>`), and
  thread `string recId = null` (last param, defaulted) through `EmitRaw` / `EmitStructural`
  / `EmitOnChange` / `EmitWindowSnapshot` / `EmitAnomaly` / `EmitMarker`. Additive
  defaulted params keep every existing call site compiling unchanged; only the new events
  and the probe pass a real `recId`.
- `MapRenderProbe` resolves `recId` per pid via the EXISTING
  `GhostMapPresence.FindRecordingIdByVesselPid(uint)` (`GhostMapPresence.cs:10005`; the
  reverse map already covers chain ghosts) and passes it on its truth / anomaly /
  suppression lines.
- The existing prefix test (`MapRenderTraceTests.FormatTracePrefix_*`) uses substring
  asserts (`Assert.Contains("pid=100037", …)`), so inserting `recId=` does NOT break it;
  only a NEW `recId=` assertion is added.
- **ERS/ELS gate is a non-issue as scoped:** the grep-audit matches only
  `.CommittedRecordings` / `Ledger.Actions`. recId resolves through the already-allowlisted
  `GhostMapPresence`, and the Driver is already allowlisted, so `MapRenderTrace.cs` /
  `MapRenderProbe.cs` need NO allowlist entry as long as no add introduces a literal
  `.CommittedRecordings` token in them. Do not add a spurious allowlist entry.

### P1 — Map/TS proto + polyline (the stated GOAL)

**A. Polyline leg + forward-arc appear / disappear / ownership EVENT.** In the Driver,
keep a `previousDrewNonOrbitalLegRecordings` field; after the decide-walk completes, diff
the new `drewNonOrbitalLegRecordings` against it and emit one `MapRenderTrace` on-change
line per recording that **appeared** (in new, not previous) and per recording that
**disappeared** (in previous, not new):

- New phase `PolylineLegChange`, `surface=Polyline`, `event=appear|disappear`, carrying
  `recId`, the resolved ghost `pid`, leg index / body / span, and `owned=<bool>`
  (ownership of the proto-line-hide). The diff is the canonical set-appear/disappear; it
  catches the count-unchanged swap the existing `polyline.drawset` count-key misses.
- Mirror a `previousDrewForwardArcRecordings` set for forward arcs/legs (built in
  `OnMapCameraPreCull` where the forward draw actually fires), and emit
  `PolylineLegChange` with a new `surface=PolylineForwardArc` enum value so a grep
  separates the forward arc from the main leg (fixes note 9's identity-conflation).
- Pure helper `DiffDrawnSets(prev, cur)` → `(appeared[], disappeared[])`, unit-tested.
- Read-only: the diff consumes the set the Driver already computed; no draw change.
- **Lifecycle (review fix):** the `previous*` sets are updated once per `LateUpdate`
  AFTER the diff and CLEARED on cross-save flush (`Clear()` / `OnGameStateLoad`). The
  `previous:=current` copy only suppresses RE-DETECTION of a STABLE set (an unchanged set
  diffs empty) — it does NOT suppress a genuinely oscillating draw-set (the documented
  warp-reseed-lag line-blink, which toggles drawn↔not-drawn every frame and would re-emit
  an appear+disappear pair every frame). That churn is throttled separately: `EmitPolylineLegChange`
  applies a per-`(surface, recordingId, event)` ~1 s wall-clock floor (`legChangeLastEmitRealtime`,
  4096-capped, cleared on cross-save), so an oscillation collapses to onset + a ~1/s
  heartbeat per event while a stable appear/disappear still emits immediately.

**B. `LineVisibilityChange` EVENT (proto orbit line + icon) folded into MapRenderTrace.**
At `GhostOrbitLinePatch.LogOrbitLineDecision` (the single point every line/icon decision
already routes through, and where `RecordLineIntent` is already called under
`MapRenderTrace.IsEnabled`), add a `MapRenderTrace` on-change emit:

- New `MapRenderTrace.EmitLineVisibilityOnChange(pidKey, recId, currentUT, signature,
  details)` that owns a per-pid last-signature dict (mirroring
  `lastMarkerDecisionSignatureByPid`, same `MaxTrackedMarkerDecisionKeys = 4096` warp cap,
  and cleared in `MapRenderTrace.Reset()` — already called from the probe's scene-switch
  hook at `MapRenderProbe.cs:134`) and emits one line only when the signature changes.
  Phase `LineVisibilityChange`, `surface=ProtoOrbitLine`, carrying `recId` + `pid` +
  `lineActive` + `drawIcons` + `iconSuppressed` + **`reason`** + bounds. The signature is
  the existing `BuildGhostOrbitLineDecisionStateKey`, so the change-detection is identical
  to today's `VerboseOnChange` — one extra emit, no new decision. Resolve `recId` via
  `FindRecordingIdByVesselPid`. NOTE: this also gives EVENT coverage to the descent
  `parking-conic-loiter-hold` / `director-traced-path-suppress` / `past-body-frame-end`
  reasons (active debugging targets) as a side effect, since every `LogOrbitLineDecision`
  reason flows through it.
- This pairs the *decision/reason* side (recordingId + why) with the probe's *truth* side
  (line.active/drawIcons) under one `surface=ProtoOrbitLine` grep, closing notes 1 & 4.

**C. Icon suppressed↔restored EVENT in the probe.** Add `IsIconSuppressed(pid)` to the
probe's Tier-B on-change set:

- New `lastIconSuppressed` per-pid dict, cleared in `ClearAllPerPidState`; emit
  `EmitTruthOnChange(... phase="icon-suppressed", surface=ProtoIcon ...)` carrying
  `recId` + before→after. Single observer (the probe already iterates every ghost each
  frame), read-only. Closes note 6.

**D. Orbit rebind fields on the existing `body-orbit` on-change (review fix: no rename).**
Keep the probe's existing `body-orbit` phase (do NOT churn the working facet); just ADD
`recId` and the loop epoch shift (`GhostMapPresence.GetGhostOrbitEpochShift(pid)`) to its
line so the proto "moves/rebinds" column carries recording identity + loop-shift for the
common visible case (body/shape change). The decision-side `SegmentApplied`
(source/segment-index, in `GhostMapPresence` apply paths) stays a documented second cut —
it requires hooking the apply path and is higher-risk; not in this PR.

### P2 — Flight-map marker parity with TS

**E. Flight-map ghostless-fallback marker appear/disappear EVENT.** Route the ghostless
fallback (`ParsekUI.cs:1737-1776`) through `EmitMarkerDecisionOnChange` so a ghostless
chain marker's appear (drew via polyline ride) and disappear (any of the five `continue`
skips) become on-change events. **Review fix:** key the emit by `rec.RecordingId` (the
pid-less case: no proto ghost), and build a FULL `BuildMarkerDecisionSignature` —
`directorTracedPathActive=false`, `polylineOwning=true`, `posSource="polyline"`, the
real `MarkerOutcome` (`DrawnNonProto` for the ride; a `Skipped*` outcome for each skip) —
NOT defaulted args (defaults thrash the signature). Match the TS contract: emit BEFORE
each `continue`. Pure read-only emit; must not change which markers draw (in-game-validate
the draw set is identical with tracing on vs off). The boundary-overlap-secondary marker
(`ParsekUI.cs:1421`, `DrawOneOverlapInstanceMarker` false-return) is **DEFERRED** (not
shipped): it has "NO continue" — the recording's MAIN decision still emits for the same
`recordingId` later in the loop, and `EmitMarkerDecisionOnChange` is keyed by
`recordingId`, so a secondary emit would thrash the same on-change key as the main
decision; and the secondary's underlying line is already covered by a
`PolylineLegChange` / `PolylineForwardArc` EVENT. The `state == null` skip (`:1302`) stays
lower-value (a transient race) — deferred too.

### P3 — Flight-scene mesh parity with the map

**F. Flight-scene mesh APPEAR / DISAPPEAR EVENT.** Add `GhostRenderTrace.EmitStructural`
at `GhostPlaybackEngine.SpawnGhost` (phase `MeshSpawned`, AFTER the snapshot-less debris
early-return at `:6572-6577` so it reflects an actual spawn) and `DestroyGhost` (phase
`MeshDestroyed`), carrying `recordingId` + `ghostIndex` + reason + (spawn) initial world
pos / (destroy) last pose. This mirrors the map's `GhostCreated`/`GhostDestroyed` so the
flight scene is no longer silent on spawn/despawn. **No recId threading:**
`GhostRenderTrace`'s prefix already carries `rec=`/`recId=` (`GhostRenderTrace.cs:984-985`).
The engine already references `GhostRenderTrace`, so this is a localised add. Re-hide
visibility events (note 16) and loop-cycle rebind (note 15) are a documented follow-up —
`EmitActivationDecision` already covers the most important (first-visible) transition;
full re-hide coverage is a larger, separately-validated change.

### Out of scope for this PR (documented second cuts)

- Decision-side `SegmentApplied` with source/segment-index in `GhostMapPresence` apply
  paths (P1.D's probe-side rebind covers the visible case).
- Flight-scene full visibility re-hide events and loop-cycle / anchor rebind events.
- The shared `RenderTraceWindow` registry extraction (already deferred by the tracer's
  own design doc).

---

## Log schema (new / changed lines)

Representative lines as ACTUALLY emitted (the `MapRenderTrace` prefix is
`phase= surface= pid= recId= frame= currentUT= effUT=`; for the polyline / marker
surfaces the `pid=` slot carries the recordingId). The `…` fields elide numeric values:

```
# MapRenderTrace surfaces (tag [MapRenderTrace]; Info for the IMPORTANT lines, else Verbose)
phase=PolylineLegChange   surface=Polyline           pid=ab12cd34 recId=ab12cd34 frame=… currentUT=… effUT=… event=appear scene=FLIGHT warp=1x        (IMPORTANT; per-(surface,recId,event) ~1s floor)
phase=PolylineLegChange   surface=PolylineForwardArc pid=ab12cd34 recId=ab12cd34 frame=… currentUT=… effUT=… event=disappear scene=FLIGHT warp=1x      (IMPORTANT; throttled)
phase=LineVisibilityChange surface=ProtoOrbitLine    pid=100037 recId=ab12cd34 frame=… currentUT=… effUT=… Orbit line decision: pid=100037 reason=polyline-owns-phase lineActive=False drawIcons=NONE iconSuppressed=True belowAtmosphere=False hasBounds=False currentUT=… bounds=[…] scene=…   (Verbose, on-change)
phase=icon-suppressed     surface=ProtoIcon          pid=100037 recId=ab12cd34 frame=… currentUT=… effUT=… iconSuppressed=true drawIcons=NONE lineActive=False body=Mun   (Verbose, on-change)
phase=body-orbit          surface=ProtoOrbitLine     pid=100037 recId=ab12cd34 frame=… currentUT=… effUT=… body=Mun sma=… ecc=… loopShift=… from=[Kerbin] lineActive=False renderer.enabled=True   (Verbose, on-change — the proto rebind)
phase=MarkerDecision      surface=ImguiLabeledMarker pid=ab12cd34 recId=ab12cd34 frame=… currentUT=… effUT=… rec=3 vessel=… directorTracedPathActive=false polylineOwning=true iconSuppressed=false shouldDrawNonProto=true outcome=drawn-non-proto ride=rode-leg-1 posSource=polyline   (Verbose, on-change; ghostless fallback)

# Flight-scene mesh (tag [GhostRenderTrace]; prefix is phase= rec= recId= ghostIndex= frame= currentUT= playbackUT= — NO surface= field)
phase=MeshSpawned   rec=ab12cd34 recId=ab12cd34 ghostIndex=0 frame=… currentUT=… playbackUT=… vessel=Munar_Probe reason=ghost-created          (Info)
phase=MeshDestroyed rec=ab12cd34 recId=ab12cd34 ghostIndex=0 frame=… currentUT=… playbackUT=… vessel=Munar_Probe reason=retire-out-of-window   (Info)
```

IMPORTANT lines (`PolylineLegChange`, `GhostCreated`/`GhostDestroyed`, `MeshSpawned`/`MeshDestroyed`,
anomalies) → `ParsekLog.Info`; on-change truth / decision (`LineVisibilityChange`,
`icon-suppressed`, `body-orbit`, `MarkerDecision`) → `Verbose`. All new lines obey the
existing gates and the warp-stable rate-limit-key rule (never key a rate-limit on a
per-frame-advancing value).

---

## Testing plan

- **Pure xUnit** (new): `DiffDrawnSets` (appear/disappear set diff), the
  `LineVisibilityChange` signature/format, the `recId`-bearing prefix
  (`FormatTracePrefixForTesting`), the polyline appear/disappear line formatters, the
  icon-suppression on-change formatter. Reuse the `ForceEnabledForTesting` +
  `FrameCounterOverrideForTesting` seams and the `ParsekLog.TestSinkForTesting`
  log-capture pattern (`RewindLoggingTests.cs` model).
- **Log-assertion xUnit**: feed the probe/driver decision sequences and assert each
  `phase=` + `event=` + `recId=` token emits exactly once per transition (and is
  suppressed when unchanged).
- **In-game** (`InGameTests/RuntimeTests.cs`, where live KSP is needed): two TS tests
  shipped — `MapRenderTrace_ResolvesRecordingIdForLiveGhost` (the live reverse-map that
  every `recId=` depends on) and `MapRenderTrace_EmitsRenderEventsForLiveGhost` (the
  emit-WIRING the pure tests cannot reach: with `mapRenderTracing` forced on it tees
  `ParsekLog` and asserts real `[MapRenderTrace]` EVENT lines carry the ghost's `recId`,
  and — when the renderer built its OrbitLine — that `phase=LineVisibilityChange` fires,
  proving the probe + `GhostOrbitLinePatch` call-sites are wired). The polyline
  `PolylineLegChange` appear/disappear and flight `MeshSpawned`/`MeshDestroyed` call-sites
  are not driven in-TS (they need a non-orbital leg draw / a flight mesh spawn); their
  wiring is confirmed at code level by the merge-verification review and left to a future
  flight-scene in-game test.
- `dotnet test` green (incl. `GhostRenderTrace` tests after the flight-scene add).

## Risks

- **Behaviour drift in the marker / driver paths.** P1.A and P2.E touch hot draw loops.
  Mitigation: emits only; explicitly assert (in-game) the drawn marker/leg set is
  identical with the setting on vs off, and keep every new emit `IsEnabled`-gated so
  disabled play is byte-identical.
- **recId threading churn.** Defaulted trailing params keep call sites compiling; the one
  behaviour-visible change is the prefix string (covered by the updated prefix test).
- **Double-logging vs the descent `SceneSnapshot`.** The new events are on-change; the
  descent dump is window-gated per-frame snapshots. They are complementary, but the doc
  notes the overlap so a reader knows which to grep.

## Open questions — RESOLVED by the clean-context review

1. Forward arc: **new `PolylineForwardArc` enum value** (cleaner grep). ✓
2. Rebind: **do NOT rename `body-orbit`; just add `recId` + `loopShift` fields** (P1.D);
   decision-side `SegmentApplied` stays deferred. ✓
3. P3 (F): **spawn/despawn only this PR**; re-hide / loop-cycle rebind deferred. ✓
4. `recId` in the prefix for all `MapRenderTrace` lines (`MapRenderTrace`-only; the
   existing substring prefix test survives, add a new `recId=` assertion). ✓
5. Flight boundary-overlap-secondary marker (`ParsekUI.cs:1421`): **DEFERRED, not
   shipped** — it shares the `recordingId` on-change key with the recording's main marker
   decision (which has "NO continue" and still emits), so a secondary emit would thrash
   that key; its underlying line is already a `PolylineLegChange`/`PolylineForwardArc`
   EVENT. (The initial plan to pull it into P2.E was reverted after the implementation
   review surfaced the key-collision.)
