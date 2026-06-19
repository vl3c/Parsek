# Re-aim looped-landing: DESCENT TRIGGER (implementation spec)

Reviewed design (workflows wc742j7r0 + wu93cpq43 + wn0kxi9k0). Builds on origin/main `5c087440b`.

## Problem (settled)
Looped re-aimed interplanetary LANDING. The re-aimed transfer is shorter, so the conic/icon arrives ~`captureShift` early (s15 ~ -2.86 Ms ~ -43.65 destination sidereal rotations) but the deorbit→reentry→landing **body-fixed polyline** stays pinned to the recorded loop-clock slot. The icon finishes the (early) parking orbit and then **circles the periodic parking ellipse ~700 revs** until the recorded deorbit UT is reached. Proven dead ends: arrival HOLD (insert-only, can only delay; froze mid-transfer when fed `captureShift`), loiter CUT (can only excise the ~12 recorded revs), render-window/gate shift (the intervening arrival-hold throws the live rotation off). `TryDrawLeg` draws body-fixed points via `GetWorldSurfacePosition(lat,lon,alt)` at the LIVE planet rotation — no recorded-epoch input (`GhostTrajectoryPolylineRenderer.cs:2603-2609`).

## Behavior (user, authoritative)
Once on the loiter orbit, discard recorded absolute time. Circle the parking orbit **as many synthetic revs as needed** (NOT bounded by recorded loiter) until the **first descent alignment**, then play the **exact recorded** deorbit→reentry→landing clip, landing on the **exact recorded site**. Timing irrelevant; trajectory geometry exact. Land at recorded site (descent unchanged), icon in the right spot at the handoff.

## Mechanism — DESCENT TRIGGER (detach + re-anchor) — AS BUILT
A SINGLE per-member head remap (one head drives BOTH the icon — rides the OrbitSegment containing it — AND
the body-fixed descent polyline — per-leg `ShouldDrawLegAtHeadUT` gate; verified by Explore). The whole
landing is ONE committed recording = ONE LoopUnit member; parking + descent are OrbitSegments/TrackSections
within it, so `DescentMemberIndex` points at that single member and the remap targets the descent *portion* of
its head. Pure math in `Reaim/DescentTrigger.cs` (`ComputeDescentMemberHead`, 21 xUnit tests); live wiring in
`ResolveTrackingStationSampleUT`.

Per cycle N (STATELESS — everything derives from the loop clock + cycle index; no latch):
- `conicEnd = recordedDeorbitUT + captureShift` — the deorbit point on the SHIFTED parking conic. `ShiftInTime`
  preserved orbital phase, so `conicEnd ≡` the recorded deorbit point (same position).
- `entryUT = phaseAnchor + N*cadence + (CompressSpanUT(conicEnd, loiterCuts) - spanStart)` — the live UT the
  icon reaches `conicEnd`. `conicEnd` lands in the recorded TRANSFER region (before arrival), so only
  LAUNCH-side cuts precede it; `CompressSpanUT` subtracts exactly those (the destination cut, after arrival, is
  after `conicEnd` and contributes 0).
- `triggerUT = entryUT + ((recordedDeorbitUT - entryUT) mod T_rot)` — first live UT ≥ entry whose body rotation
  equals the recorded deorbit rotation (`ComputeRotationAlignedTriggerUT`; same modular form as the existing
  `ComputeArrivalAlignHoldSeconds`). Lands on the EXACT recorded site.
- `currentUT < entryUT` → **Inert** (normal loop clock: launch / transfer / parking-conic ride).
- `entryUT ≤ currentUT < triggerUT` → **Loiter**: circle the conic's last rev, ANCHORED TO triggerUT:
  `head = conicEnd - ((triggerUT - currentUT) mod T_park)`. The icon reaches `conicEnd` (deorbit point) EXACTLY
  at the handoff — only the final partial rev retimes (imperceptibly; timing is irrelevant). **This is the
  user's "icon in the right spot": smooth handoff AND exact site with NO T_park/T_rot beat-search, no
  tolerance, no fallback hop** (supersedes the rotation-only-first / dual-search staging this doc originally
  proposed).
- `currentUT ≥ triggerUT` → `head = recordedDeorbitUT + (currentUT - triggerUT)`: **Descent** while
  `≤ descentEndUT` (clip plays forward verbatim), else **Done** (landed → hide member until next loop).

### Build-time `captureShift`
`captureShift = HohmannTransferTimeSeconds(aOrigin, aTarget, muAncestor) - plan.RecordedTransferTofSeconds`
(`MissionLoopUnitBuilder`, in the `destTrim.Applied` branch). The build-time equivalent of the resolver's
per-window value (`ReaimPlaybackResolver.cs:509`, `newArrival - recordedArrival`), constant across loops
(the per-window usedTof ≈ geomTof). SMAs from `ReaimClassifier.HeliocentricSemiMajorAxis`. Gated on `cs < 0`
(early arrival = the conic-shift gap exists). A small cs error shifts the circling start / conic-end (cosmetic
handoff) but NOT the site (the trigger is rotation-congruent regardless); refine by threading the resolver cs
if a hop shows in playtest.

### Seam (single chokepoint) — WIRED
`ResolveTrackingStationSampleUT` (:8239) — the TS/map primary head, feeding the polyline head (:3341), marker,
GhostMapPresence, 3D mesh. The override fires only for `i == DescentMemberIndex` when the normal decision is
`Render` (the descent member's window is the full span, so Render holds through Loiter/Descent). `…SampleFrame`
(:8355) inherits it via the primary; the boundary-overlap SECONDARY is untouched (R6). **Follow-up: the FLIGHT
engine path is NOT yet wired (the user tests in the Tracking Station). Multi-recording landings (descent in a
separate member) are also a follow-up — v1 assumes the single through-line member = `ownerIndex`.**

### LoopUnit fields (sentinel = identity; GhostPlaybackLogic.cs)
**(field SOURCING below is SUPERSEDED by the Multi-member rework section — see it for the as-shipped values.)**
`DescentMemberIndex` (-1), `RecordedDeorbitUT`, `DescentEndUT`, `DestinationBodyRotationPeriodSeconds` (T_rot),
`LoiterPeriodSeconds` (T_park), `CaptureShiftSeconds`. `HasDescentTrigger` gates the resolver. No latch (the
stateless cycle-N derivation replaced it). The single `DescentMemberIndex` and the `destTrim`-sourced
deorbit/period this section originally proposed were both replaced (the builder re-derives deorbit from the
transfer seam and T_park from the parking `LoiterRun`; the `DestinationLoiterTrimResult` no longer carries
deorbit/period).

### Invariants — HELD
- Byte-identical-off: no-op unless `HasDescentTrigger && i == DescentMemberIndex && decision == Render`.
  15891 xUnit tests green (incl. all loop-clock / resolver / re-aim / arrival-hold tests).
- Freeze-free: forward-only head substitution; NEVER routes through `ApplyArrivalHoldToPhase` (the existing
  arrival hold is left intact but is moot for the descent member — its head is overridden, and the hold sits at
  arrival, after `entryUT`, so it never alters the Inert-phase head either).
- Recorded clip byte-unchanged (lat/lon/alt read as-is). HARD RULE honored.

### Tests — DONE (21 pure xUnit in `DescentTriggerTests.cs`)
Trigger-time formula (∈[entry,entry+T_rot), congruent mod T_rot, body-general, wrap, NaN guards); head
monotone / freeze-free / never < recordedDeorbit; piecewise phases (Inert/Loiter/Descent/Done) incl. smooth
handoff (head→conicEnd at trigger), cycle re-arm by index, launch-side cut shifts entry, byte-identical-off
degenerate guards. In-game (PENDING user playtest, TS + map): icon circles ≤ ~1 body-day, descent draws
connected, lands at recorded lat/lon, no freeze; diagnostics under `[Parsek][...][ReaimDescent]`.

---

## Multi-member rework (AS BUILT — supersedes §1 member identification + the single-`DescentMemberIndex` field)

A focused investigation (workflow `wf_aff2c917-13f`) on the failing subject ("Route: KSC → Duna", save
`orbital supply route`) overturned the single-member assumption with direct `.prec` evidence:

- **The subject is a CHAIN-LOOP** (owner=committed-#41) with the post-arrival clip in **separate late members
  #49/#50/#51** (recording IDs `3700f40e` / `caa6190c` / `fca32e43`), NOT in the transfer member (#53,
  `36c7688b`) and NOT in the owner (#41 = the Kerbin launch). The deployed `DescentMemberIndex = ownerIndex`
  pointed at the launch → no-op.
- **It is an orbital rendezvous + station DOCK at ~237 km Duna, not a surface landing** (`STOP endpoint
  isSurface=False connectionKind=DockingPort alt=237316.9`; the `minAlt=-0.73` was the v6 Relative-frame
  metre-offset trap CLAUDE.md warns about). The trigger MATH is unchanged — the body-fixed/relative approach
  members still render via live destination rotation, so rotation alignment is required identically to a
  landing. "Descent" in the code = "post-parking body-fixed approach clip (landing OR dock)".

**Changes:**
- `LoopUnit.DescentMemberIndex` (int) → **`DescentMemberIndices` (int[] SET)** + `IsDescentMember(i)`; the
  approach spans several contiguous chain members sharing ONE clip + ONE trigger. `HasDescentTrigger` requires
  a non-empty set (empty ⇒ off ⇒ byte-identical).
- **`RecordedDeorbitUT` = the SEAM** = the transfer member's max non-predicted target-body OrbitSegment endUT
  (= the first approach member's start, 72353179). NOT `descentRun.EndUT` (the parking-loiter end ≈72348068,
  ~5111 s too early and mid-conic — the deorbit-transition orbits seg#13-17 continue past it). The seam fix
  also corrects `conicEnd = RecordedDeorbitUT + captureShift` to point at the shifted conic's true end.
- **`DescentEndUT` = the last approach member's recorded EndUT** (NOT `spanEndUT`, which can include a
  route-excluded interval like `1331a21b`).
- **Member identification** (`DescentTrigger.SelectDescentMemberIndices`, pure + tested): members whose window
  starts at/after the seam (≤1 s eps) AND whose start body == `plan.TargetBody`. Excludes launch/probe/transfer
  (all start pre-seam) and any post-seam member on the wrong body.
- **Multi-member dispatch** (`DescentTrigger.TryResolveDescentMemberHead`, pure + tested): the single monotone
  descent head is dispatched by window — each member renders ONLY the slice of the clip inside its own
  `[MemberStartUT, MemberEndUT]`, hidden in every other phase. The transfer member carries the icon over the
  shifted conic during the wait (descent members never ride the raw loop clock). Sweep test proves ≤1 member
  renders at any instant (no tear, no double icon).
- **R6 secondary guard:** descent-set members suppress the boundary-overlap SECONDARY (it does not descent-remap)
  in `ResolveTrackingStationSampleFrame`.

**Net change surface:** `DescentTrigger.cs` (+`MemberArrivalInfo`, `SelectDescentMemberIndices`,
`TryResolveDescentMemberHead`; `ComputeDescentMemberHead` math UNCHANGED), `GhostPlaybackLogic.cs` (set fields +
multi-member resolver + secondary guard), `MissionLoopUnitBuilder.cs` (seam + set identification + set-based
deorbit/end), `DestinationLoiterTrim.cs` (surfaced RecordedDeorbitUT/DestinationPeriodSeconds, now unused by the
re-gated builder but harmless). 29 pure xUnit tests; full suite 15899 green.

**PENDING in-game validation (user AFK):** R-trim (are #49/#50/#51 classified Render at the post-trigger UTs?),
R-double-icon (transfer-member conic icon hides as the approach icon appears), R-seam (runtime seam == first
member start), R-rotation (the relative-anchored approach actually reaches the live-rotated station). Grep
`[Parsek][INFO][ReaimDescent] ... DESCENT TRIGGER engaged members=[49,50,51]` and the per-member
`[VERBOSE][ReaimDescent]` phase lines to confirm engagement + render dispatch.
