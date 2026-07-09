# Migration Plan: Unified Trajectory & Map/TS Render Architecture

> **STATUS: CURRENT plan.** The detailed, phased, parity-gated implementation sequence for
> [`../design-map-ts-render-architecture.md`](../../design-map-ts-render-architecture.md) (the unified
> design, landed in PR #1202). This expands §16 of that doc into an executable sequence. Each phase is a
> separate worktree + PR, gated on the three non-negotiable acceptance criteria (no regression / full
> `MapRenderTrace` logging / full unit + in-game tests). Reviewed clean-context before landing (the
> Phase 5/6 dependency split came out of that review).

---

## 0. The three hard gates (apply to EVERY phase, no exceptions)

Every phase below is a separate worktree + PR and is **not done** until all three are green:

1. **No regression of working behavior.** The migration is **additive-first and parity-gated**: a new
   path is built and proven equivalent *alongside* the old one before the old one is touched, and **no
   working draw surface is deleted until the §14 parity oracle is green on it** across the regression
   scenario set (§2). Every PR is independently revertible.
2. **Full `MapRenderTrace` logging integration.** Every new phase / treatment / seam / provenance /
   anchor-resolve / lifecycle / retire / fail-closed event emits through the existing tracer's
   Tier-A/B/C model (warp-stable keys). A phase's logging is part of its definition of done; "if it
   isn't logged, it isn't done."
3. **Full test coverage.** Every new pure method gets an xUnit suite; everything runtime-only gets an
   in-game test (`RuntimeTests` / `ExtendedRuntimeTests`), plus `LogContractTests` for every new trace
   tag/format. A phase is not complete until both are green and the in-game suite passes on the
   regression set.

**Definition of done (per phase):** parity oracle green on the regression set · all new unit tests green
· all new in-game tests green · the expected `MapRenderTrace` Tier-A/B/C lines present in a tracing-on
run · `dotnet test` fully green · CHANGELOG/todo updated if behavior changed · a grep-audit gate added
for any deleted symbol (so it can't be reintroduced).

---

## 1. Guiding principles

- **Shadow before swap.** New producers run in shadow and assert equivalence (parity / byte-parity)
  before they drive anything. New owners draw behind the parity oracle before the legacy owner is
  deleted.
- **Delete last, gated, gripped.** Legacy code is removed only after its replacement is parity-green,
  and each deletion adds a `scripts/grep-audit-*.ps1` gate (xUnit-enforced, the existing pattern:
  `grep-audit-map-render-director-drive`, `grep-audit-active-leg-recordings`) so it cannot creep back.
- **One concern per PR.** Surfaces are re-homed one at a time (TracedPath, then markers), each its own
  parity-gated PR — never a big-bang cutover.
- **Preserve the kept fallbacks.** `StockConic` stays MANAGED (`seedByPid` + the `GhostOrbitLinePatch`
  prefix read are kept), and `ghostsWithSuppressedIcon`/`IsIconSuppressed` becomes the `SuppressedMarker`
  tier — neither is deleted (§12 of the design).
- **Substrate untouched.** `Recording` / `TrackSection` / `OrbitSegment` / `TrajectoryPoint` structs are
  not retyped (the Re-Fly value-copy rollback contract). The new types are an in-memory render view.

---

## 2. Phase 0 — Safety net first (parity oracle + regression set + tracer skeleton)

**Nothing else starts until Phase 0 ships.** This is the instrument every later phase gates on.

**Goal:** build the §14 recorded-vs-rendered geometry-diff parity oracle and a fixed regression
scenario set, so every subsequent phase has an objective "did I break rendering?" gate.

**What changes (additive only — zero behavior change):**
- A new **parity oracle** that samples the *currently rendered* ghost geometry (proto orbit line / icon
  world positions, polyline points) and diffs it against the recorded source (faithful) or the
  producer's intended arc (synthesized), within tolerance. It is a **DISTINCT recorded-vs-rendered axis
  added BESIDE** the existing `GhostRenderReconciler.CheckIntentAgainstOldTruth` (intent-vs-old-truth);
  the two comparators **coexist** Phases 0–8 (Phase 8 unwires the old one) — this is not a rename. In
  Phase 0 the oracle only *observes* and emits the `parity-drift` Tier-C anomaly. Pure diff math is
  `internal static`. **Coordinate frame:** icon/orbit positions are diffed **body-relative**
  (`GetWorldPos3D - referenceBody.position`, matching `MapRenderProbe`'s `bodyRelPos` to avoid high-warp
  false positives); polyline points are converted **scaled→world** before diffing. Tolerance is
  per-scenario, derived from **geometry scale** (metres at the relevant map zoom), not a blanket fit.
- A **regression scenario set** with a **scenario → design-§11.5-matrix-row traceability table** as a
  DoD artifact (the design enumerates ~45 situations; coverage must be *demonstrated*, not asserted).
  Representative recordings/missions: faithful ascent→orbit→descent, a re-aimed interplanetary loop, a
  dock/undock mission, a parent-anchored-child mission, a Jool tour, a looped overlap, a below-atmosphere
  descent. Most are synthetic recordings in `Tests/Generators/` + an injected save (the
  `InjectAllRecordings` pattern). **Runtime-only rows cannot be exercised by synthetic recordings alone
  and need dedicated in-game scenarios:** cold-load UT=0, quickload mid-gap, scene-transition no-blank,
  warp-through-`HoldPhase`, dock-merge retire-at-`dockUT`, overlap gap-hold. The traceability table must
  show every matrix row mapped to either a recording scenario or a dedicated in-game test (or an explicit
  "out-of-scope-ok" with reason), so holes can't pass silently.
- The **`MapRenderTrace` skeleton** for the new surfaces/anomalies (`rigid-seam-tangent-discontinuity`,
  `parity-drift`, `retire-not-held`, `anchor-resolve-fail`, `clock-not-ready`; new `RenderSurface`
  values), wired but inert until the producing code exists.

**Logging:** the oracle emits `parity-drift` (Tier-C) with recordingId + pid + the measured deviation;
a per-frame Tier-B "parity-ok" change line is NOT emitted (noise) — only drift.
**Tests (two distinct DoD sub-tasks — do not conflate):** (1) unit — the pure geometry-diff math
(faithful vs synthesized, tolerance, NaN handling); (2) **capture-harness validation** — the net-new
Unity geometry sampler (reading `OrbitDriver` world pos, `ScaledSpace`, Vectrosity `VectorLine.points3`)
validated **against a known-position synthetic fixture** (a recording at a fixed, hand-computed orbit) to
within tolerance. The sampler is by-construction-untestable-by-itself; without (2) the oracle is
green-but-blind and Phases 3/5 gate on theatre. In-game — a `parity-baseline` test runs **only the
KNOWN-GOOD scenarios** and asserts zero drift.
**Tolerance calibration caveat:** the baseline is captured on **known-good scenarios only**. Today's
output carries open bugs (icon-off-orbit, warp-reseed-lag, depot-double, sub-surface-ghost-not-retiring)
— those scenarios are tracked as **expected-to-change**, NOT baselined, so a later incidental fix does
not trip `parity-drift` and read as a regression, and so a loose blanket tolerance does not mask real
drift.
**Parity gate:** N/A (this IS the gate). **Rollback:** trivially revertible (additive observer).
**Risk:** low-but-load-bearing — the capture harness (not the diff math) is the real failure surface;
sub-task (2) is what de-risks it.

---

## 3. Phase 1 — Pure L0/L2 types (no wiring)

**Goal:** introduce the new type system, fully unit-tested, NOT wired into the live pipeline. Zero
runtime change.

**What changes:** new `internal` types in `Source/Parsek/MapRender/` (new namespace area):
`SegmentProvenance`, `AnchorFrame` (+ the `ParentAnchoredChild` / `LiveVesselAnchor` / etc. variants),
`ITrajectorySample` (`AbsoluteSample` / `RelativeSample` / `OrbitalState`), the abstract
`TrajectoryPhase` + the concrete subclasses (`AscentPhase`, `DepartureLoiterPhase`, `SoiDeparturePhase`,
`HeliocentricTransferPhase`, `SoiArrivalPhase`, `ArrivalLoiterPhase`, `DescentPhase`, `SurfacePhase`,
`HoldPhase`), `PhaseSeam` (+ `SwitchContinuation`), `PhaseId`, `PhaseChain` (the `GhostRenderChain`
successor). Implementations delegate to the existing kernels (`OrbitSegment`, `OrbitArcSampler`, the span
clock) but nothing calls these types yet. **The `FrameBodyName` → `AnchorFrame` widening must carry
forward the `RenderSegment.cs:94-98` loud-assertion** (a parent-anchored child is never handed a re-aimed
segment list — it stays loud, not silently body-framed), so the §11.3 transfer-leg-debris fail-closed
(Phase 7) stays loud rather than silently degrading.

**Logging:** none yet (no runtime path). **Tests:** unit — every phase subclass's `ResolveTreatment` /
`CoversUt` / `Emit`; `PhaseSeam` classification; `SegmentProvenance`; `AnchorFrame` resolution incl.
`ParentAnchoredChild` dual-surface (≥2-sample, out-of-range→retire) and `BodyAnchor` fail-closed.
**Parity gate:** N/A (nothing wired). **Rollback:** trivial (dead code). **Risk:** low.

---

## 4. Phase 2 — `PhaseFactory` in shadow (byte-parity of geometry)

**Goal:** prove the new factory produces the same *geometry* the current assembler does, before it drives
anything.

**What changes:** a `PhaseFactory` that builds `PhaseChain` (= `TrajectoryPhase[]`) from the SAME inputs
`ChainAssembler.Build` consumes — faithful identity from `SegmentPhaseClassifier.EnvironmentToPhase` +
`RecordingOptimizer.SplitEnvironmentClass`; re-aimed from the `ReaimMissionPlan`; the heliocentric-park
variant via `DecideDepartureAnchor`/`RotateLanForParkRephase`; BG-on-rails → all-orbital chain
(tolerates absent `SegmentPhase`). It runs in **shadow** alongside `ChainAssembler` (behind
`mapRenderTracing`), and a shadow-comparator asserts **byte-parity of the geometry fields only** — per-segment (`Treatment`,
`StartUt`, `EndUt`, `FrameBodyName`, conic-element payload) **plus the chain-level `WindowStartUt` /
`WindowEndUt` + `IsFaithfulFallback`** (`GhostRenderChain.cs:24-27`, which drive coverage/clip and matter
for the `RouteBackingMission` trimmed-window case) — against the assembler's `GhostRenderChain`.
`PhaseKind`/`Provenance` are validated by the Phase-1 unit tests, NOT the parity gate.

**Logging:** a `factory-parity` Tier-C anomaly on any geometry mismatch (recordingId + the diverging
field). **Tests:** unit — the factory per phase kind + the park/BG/single-recording cases + the geometry
byte-parity comparator. In-game — a shadow-parity test over the regression set asserting zero
`factory-parity` anomalies. **Parity gate:** zero `factory-parity` anomalies across the regression set is
the gate to Phase 3. **Rollback:** trivial (shadow only). **Risk:** medium — the faithful-identity
classification is the subtle part; the byte-parity comparator is what catches a wrong classification.

---

## 5. Phase 3 — Swap the decision spine to consume phases (same draw path) ⚠️ RISKIEST

**Goal:** make `PlaybackResolver` (renamed from `ShadowRenderDriver`) + `ChainSampler` +
`GhostRenderDirector` consume the `PhaseChain` from `PhaseFactory` instead of `RenderSegment[]` from
`ChainAssembler` — but keep the **existing draw path** (still via the side-channel dicts). This is the
"swap the producer under a stable consumer" step.

**What changes:** the resolver builds/caches a `PhaseChain` (via `BuildChainSignature`, unchanged keying
+ the `|w{window}` token); the sampler maps liveUT→assembled-UT off the phase chain (same span-clock
call); the director's 3-case `Decide` consumes a `PhaseChain` sample. The intent it produces is
identical in geometry to today's. The legacy `GhostOrbitLinePatch` / polyline `Driver` / markers still
draw, reading the same side-channel signals the director still stamps.

**Logging:** Tier-A `phase-chain-assembled` (count + kinds + seams + provenance) on (re)build; Tier-B
per-frame phase identity. **Tests:** in-game — the parity oracle reports zero drift over the regression
set with the spine swapped (the whole point). Unit — the resolver's chain build/cache + the sampler
mapping. **Parity gate:** zero parity-oracle drift across the regression set. **Rollback:** the PR is a
clean revert to the assembler-driven spine. **Risk:** HIGH — this is where a subtle sampling/UT or
gap-hold difference would surface. Mitigations: the Phase-2 byte-parity already proved geometry
equivalence; keep the assembler available behind a feature flag (instant flip-back on a regression),
**removed in Phase 5b alongside the legacy-draw delete and gated by a grep-audit on the flag symbol** —
not an indefinite dual spine; require an explicit in-game playtest sign-off (not just the automated
suite) before merge. Note: the descent deorbit-head / `captureShift` clock is consumed only by the legacy
polyline Driver + the span clock, **not** by the swapped spine (no `ResolveTransferLegHeadUT`/`deorbitHead`
refs in `ShadowRenderDriver.cs`), so Phase 3 parity does not hide a Phase-3↔Phase-6 coupling — that
coupling surfaces only at the Phase-5b deletion.

---

## 6. Phase 4 — Re-home the OWNED surfaces into `scene.Apply(intent)` (one PR per surface)

Each sub-phase is its own parity-gated PR. `StockConic` is deliberately NOT here (it stays MANAGED).

### 6a — TracedPath polyline → treatment-owned
**What changes:** `TracedPathTreatment` draws the polyline directly from the intent via
`scene.Apply(intent)` (the `onPreCull` host stays the shared draw site). Once the oracle is green, delete
the **two TracedPath side-channels — they live in DIFFERENT files:** `tracedPathByPid` (a
`ShadowRenderDriver.cs:95` field — write :327, read :158, evicted in the `PruneStaleState` loop at
:486-487, **where its KEPT sibling `seedByPid` must survive the same loop**) and
`drewNonOrbitalLegRecordings` (the polyline renderer's ownership set,
`GhostTrajectoryPolylineRenderer.cs:109`). The polyline `Driver`'s **ownership walk is NOT deleted here**
— it still hosts the descent deorbit-head clock until Phase 6 re-homes it (deleted in Phase 5b).
**Logging:** Tier-B polyline draw + the existing `PolylineLegChange appear|disappear` EVENTs re-pointed
to the treatment. **Tests:** in-game — polyline renders identically (oracle green) on the
atmospheric/non-orbital regression scenarios. **Grep gate:** `grep-audit-traced-path-bypid` covering
**both files** (zero `tracedPathByPid` AND zero `drewNonOrbitalLegRecordings` refs after delete).

**IMPLEMENTED (Phase 4a, behind `MapRenderPhaseSpineDrive`, ADDITIVE / flag-reversible — side-channel
deletions stay Phase 5):** the TracedPath owned-draw decision is re-homed to the spine's
`GhostRenderIntent` *under the flag* without deleting anything.
- **Important entanglement discovered.** A prior, fully-merged rewrite (the 8b.1 / 8e-S4 path) already
  routes the TracedPath leg through the OWNED `TracedPathTreatment.TryDrawOwnedLeg` (the `onPreCull` host)
  whenever the always-on shadow decides Visible+TracedPath for the ghost, keyed on `tracedPathByPid` via
  `IsDirectorTracedPathActive`. So the BEHAVIORAL re-home (owned draw + autonomous direct stand-down +
  proto/marker suppression) is ALREADY live in flag-OFF play; `tracedPathByPid` is stamped unconditionally
  by `RunFrame`, independent of the spine flag. A clean "stand down the autonomous draw only when flag-ON"
  branch is therefore either a no-op (both states already take the owned path) or a flag-OFF parity break
  (re-routing flag-OFF to the autonomous-direct path, which would also desync the orbit-line/marker
  consumers that read the same `IsDirectorTracedPathActive`). This is the entanglement the phase brief
  flagged; rather than force it, 4a delivers the meaningful, non-no-op part:
- **What 4a actually changed (additive).** `ShadowRenderDriver` gained an intent-sourced sibling stamp
  `tracedPathIntentByPid` (written ONLY when `PhaseSpineDriveActive`, from the SAME intent in the SAME
  `RunFrame` pass as the legacy `tracedPathByPid`), the predicate
  `IsDirectorTracedPathActiveFromIntent`, and the FLAG-AWARE selector `IsTracedPathOwnedThisFrame` (flag ON
  → intent source; flag OFF → legacy `IsDirectorTracedPathActive`, byte-identical to today). The polyline
  `Driver`'s per-recording routing now reads `IsTracedPathOwnedThisFrame` instead of the raw side-channel.
  Because the two stamps are byte-identical on the flag, the flag-ON owned routing never disagrees with the
  proto/marker consumers (which still read `IsDirectorTracedPathActive`): exactly one painter, no
  double-draw, no gap. NOTHING was deleted — `tracedPathByPid`, `drewNonOrbitalLegRecordings`, the
  autonomous Driver, `TryDrawOwnedLeg`, the legacy marker anchoring all stay intact for the flag-OFF path
  and Phase 5. Tests: `ShadowRenderDriverTests` (flag-aware selection, flag-gated stamp source-gates,
  the equal-stamp no-double/no-gap invariant) + a `GhostTrajectoryPolylineBuildTests` Driver routing
  source-gate + the `TracedPathOwnedDrawInGameTest` (flag ON/OFF, exactly-one-painter over a live
  non-orbital ghost). Phase 5's `tracedPathIntentByPid` must be deleted alongside `tracedPathByPid`.

### 6b — IMGUI marker → `SuppressedMarker` treatment-owned
**What changes:** the marker draw moves into a `SuppressedMarker` treatment; the
`ghostsWithSuppressedIcon`/`IsIconSuppressed` floor becomes that treatment's input (KEPT, re-homed —
deleting it would regress below-atmosphere/no-bounds ghosts to a blank icon). **Logging:** Tier-B
`SuppressedMarker` on/off change line; the existing `icon-suppressed` EVENT re-pointed. **Tests:**
in-game — below-atmosphere / no-bounds / off-arc ghosts still show a marker, never a blank icon (oracle +
an explicit "no blank icon" assertion). **Parity gate:** oracle green on the no-conic regression
scenarios.

**StockConic (MANAGED) — formalize, don't move:** in the same window, document and lightly refactor the
`seedByPid` re-assert into an explicit MANAGED-treatment contract, but the `GhostOrbitLinePatch:396`
prefix read with `±SeedFreshnessFrames=2` STAYS (KSP owns the icon-drive site). No deletion here.

**IMPLEMENTED (Phase 4b, behind `MapRenderPhaseSpineDrive`, ADDITIVE / flag-reversible — side-channel
deletions stay Phase 5):** the marker-draw decision is re-sourced to the spine's `GhostRenderIntent`
*under the flag* without deleting anything.
- **Entanglement (anticipated by the phase brief).** The marker decision
  (`GhostMapPresence.ShouldDrawNonProtoMarkerForGhost` → pure `ResolveMarkerDrawDecision`, read by the
  flight-map `ParsekUI.DrawMapMarkers` and the TS `ParsekTrackingStation.ClassifyAtmosphericMarkerSkip`)
  composes THREE disjuncts — `directorTracedPathActive || polylineOwning || iconSuppressedLegacy`. Only
  the FIRST is something the spine's intent decides. `polylineOwning` (`IsPolylineOwningGhostPhase`) is the
  downstream ACTUAL-draw set, not an intent field; `iconSuppressedLegacy` (`ghostsWithSuppressedIcon` /
  `IsIconSuppressed`) is the KEPT no-conic floor, set by `GhostOrbitLinePatch` — and the director emits
  only `None`/`StockConic`/`TracedPath` (there is NO `SuppressedMarker` `Treatment` value yet), so the
  spine does not decide the no-conic floor case. Re-sourcing the floor would require the director to learn
  a new `SuppressedMarker` treatment + producer (out of Phase-4 scope). So 4b re-homes the one
  intent-decided disjunct and KEEPS the other two on their legacy sources — exactly the 4a outcome
  (re-source the intent-decided signal, keep the rest).
- **What 4b actually changed (additive).** `ShouldDrawNonProtoMarkerForGhost` now resolves its
  `directorTracedPathActive` disjunct through the FLAG-AWARE selector
  `ShadowRenderDriver.IsTracedPathOwnedThisFrame` (flag ON → the intent source
  `IsDirectorTracedPathActiveFromIntent`; flag OFF → the legacy `IsDirectorTracedPathActive`,
  byte-identical to today), instead of the raw legacy `IsDirectorTracedPathActive`. This is the SAME
  selector 4a routes the polyline Driver on, so the marker call sites, the polyline owned-draw, and the
  proto/marker consumers in `GhostOrbitLinePatch` (which keep reading the legacy `IsDirectorTracedPathActive`
  to set `drawIcons=NONE` + add to `ghostsWithSuppressedIcon`) all read a byte-identical TracedPath signal
  on the flag — exactly one of {proto icon, our marker} per ghost, no double-marker, no gap, and flag-OFF
  byte-identical. NOTHING was deleted — `ghostsWithSuppressedIcon`, `IsIconSuppressed`, `seedByPid`, the
  legacy marker anchoring all stay intact for the flag-OFF path and Phase 5. **No NEW side-channel was
  added:** 4b reuses 4a's `tracedPathIntentByPid` / `IsTracedPathOwnedThisFrame` (so there is nothing new
  for the Phase-5 deletion list beyond what 4a already recorded; reusing the one selector is what keeps the
  marker site in lockstep with the polyline Driver). The `icon-suppressed` EVENT is NOT re-pointed in 4b —
  it tracks `IsIconSuppressed` (the kept floor, unchanged); re-pointing it onto a `SuppressedMarker`
  treatment is the Phase-5+ cutover when the director learns that treatment. Tests:
  `ShadowRenderDriverTests` (the marker-decision flag-aware selection, the equal-stamp no-desync/no-double/
  no-gap invariant, a marker-site source-gate that the disjunct reads the flag-aware selector not the raw
  legacy) + `MarkerDrawDecisionTests` (the pure decision, unchanged) + the
  `SuppressedMarkerOwnedDrawInGameTest` (flag ON/OFF over a live below-atmosphere ghost: never a blank
  icon, marker disjunct agrees with the proto-line consumer).

---

## 7. Phase 5 — Delete the legacy draw, in TWO parts with different dependencies (cutover-complete) ⚠️

The legacy draw has two deletable parts that must **not** be deleted together: the polyline `Driver`
ownership walk still hosts the descent deorbit-head clock that Phase 6 re-homes
(`GhostTrajectoryPolylineRenderer.cs:3801-3834`: `deorbitHead` :3801-3811, `deorbitTailLeg` :3830,
`ResolveTransferLegHeadUT` :3833-3834). Deleting that walk before Phase 6 re-homes the clock would lose
deorbit-head alignment on **every looped landing mission** — a regression window. So Phase 5 splits:

### 5a — Delete the 430-line line-visibility cascade (after Phase 4; independent of Phase 6)
**What changes:** delete the ~430-line line-visibility Postfix cascade in `GhostOrbitLinePatch.cs`; the
patch shrinks toward just the StockConic icon-drive seed apply. This portion does **not** touch the
deorbit clock. **Logging:** verify no trace regressions (EVENT coverage now flows from the treatments).
**Tests:** in-game — the regression set is oracle-green with the cascade gone. **Parity gate:** the
universal gate across the regression set. **Grep gate:** assert the cascade symbols are gone.
**Rollback:** revert restores the cascade. **Risk:** medium (an isolated line-visibility delete).

**IMPLEMENTED (Phase 5a, re-scoped per the 4c/8f findings - the spine decides, floors and non-driven
populations retained):** the line Postfix is restructured so the SPINE signals are the primary decision
source, and the pure-legacy chatter machinery is deleted; the plan's original "shrinks toward just the
seed apply" wording predates the 4c re-scope (the icon floor is a KEPT permanent fallback) and two
populations the spine deliberately does not drive.
- **Deleted:** the FIX-#26 orbit-line grace machinery in full: `ShouldDeferOrbitLineHide`,
  `OrbitLineGraceFrames`, the `OffReasonStaleSegment`/`OffReasonPolylineOwns` consts, both grace-defer
  branches (polyline-owns + stale-segment), the per-branch grace re-stamps, and the per-pid grace map in
  `GhostMapPresence` (`StampOrbitLineGrace`/`GetOrbitLineGraceUntilFrame`/`ghostOrbitLineGraceUntilFrame`
  + its teardown clears). It debounced chatter between the legacy cascade's own transient off reasons;
  the spine's decisions are freshness-bridged (`SeedFreshnessFrames`) and the Director TracedPath
  suppress was never graced, so no spine-driven ghost loses a debounce. `GhostOrbitLineGraceTests`
  (20 tests, all pinning deleted symbols) removed with it.
- **Added:** the `director-stockconic-visible` branch: a Director-driven ghost's line SHOW is now
  applied from `IsDirectorDriveActive` (the same fresh seed the icon-drive bakes and the arc-clip
  switches to live bounds on), gated on the applied bounds covering the live clock (the legacy
  stale-segment contract folded into the gate). One decision source for icon + line + show.
- **Retained (fallback-only; a Director-driven ghost never reaches them):** the Director TracedPath
  suppress first branch; the polyline actual-draw ownership hide + `StampPolylineOwning` (the
  Driver-direct legs still draw - 5b RETAINED them as the fenced populations, see the 5b IMPLEMENTED
  note - and the `polyline-orbit-overlap` oracle invariant plus the
  ParsekUI marker's release-stamp read require both); the below-atmosphere icon floor (4c/8f: the ONLY
  marker signal for that population); the body-frame window clamp + stale-segment guard for the
  populations with NO spine signal (per-segment re-aim SKIP on declined synodic windows still renders
  the recorded conic via the legacy seg-drive; terminal-orbit / endpoint-tail protos have no spine
  model - the Director emits Hidden past the window while the parked terminal ellipse must keep
  rendering); the parking-conic loiter hold (belt-and-suspenders: the spine's span-clock loiter wrap
  keeps the seed fresh through the gap, so the hold only fires if the spine goes stale); and the
  post-polyline-release grace + director-terminal-suppress no-bounds guards. The tracer wiring
  (`RecordLineIntent`/`EmitLineVisibilityOnChange` via `LogOrbitLineDecision`) is unchanged and every
  surviving branch still routes through it. **Grep gate:** `GhostOrbitLineCascadeDeleteGateTests`
  (deleted symbols stay deleted in `GhostOrbitLinePatch.cs` + `GhostMapPresence.cs`; retained
  mechanisms stay wired). The retained fallback branches were re-examined at 5b (which RETAINED the
  Driver-direct draws as fenced populations, so the polyline-owns feeder stays; see the 5b IMPLEMENTED
  note) and are re-examined again whenever the spine learns the terminal/endpoint-tail
  and re-aim-declined populations.

### 5b — Delete the autonomous polyline Driver ownership walk (HARD-depends on Phase 6) ⚠️
**What changes:** delete the autonomous `GhostTrajectoryPolylineRenderer.Driver` ownership walk —
**including the `deorbitHead`/`captureShift`/`ResolveTransferLegHeadUT` consumer (:3801-3834), only after
Phase 6 has re-homed that clock into the `CrossMemberSeamStitcher`.** Confirm the Phase-4 deletions
(`tracedPathByPid` in `ShadowRenderDriver`, `drewNonOrbitalLegRecordings` in the polyline renderer) are
gone. **Logging:** verify no trace regressions (EVENT coverage now flows from the treatments + the
stitcher). **Tests:** in-game — the FULL regression set is oracle-green; a `map+TS render parity` test
(one icon/line or one polyline+marker per ghost); the descent scenarios still align (Phase 6's stitcher
owns the clock). **Parity gate:** the universal gate across the entire regression set, AND an explicit
in-game playtest sign-off. **Grep gate:** assert the Driver-ownership-walk symbols are gone.
**Rollback:** revert restores the legacy walk (kept intact until this PR). **Risk:** HIGH (a delete with
a cross-phase dependency) — mitigated by the Phase-6-predecessor gate + the parity gate + playtest
sign-off.

**IMPLEMENTED (Phase 5b, re-scoped per the walk end-to-end population map - the same 4c/8f "the plan's
deletion list is the intent, the populations decide" discipline as 5a):** the walk itself is RETAINED as
a documented fence; what died is the cutover flag, the legacy TracedPath side-channel, the walk's direct
deorbit-clock reads, and the driver's dead reconciler write. Mapping the Driver-direct vs owned draws
under the (then) const-true flag showed the walk is the SINGLE draw host and the only renderer for four
populations the spine does not enumerate, so "delete the walk" would have deleted the owned draw's
dispatch too.
- **Deleted - the flag:** `MapRenderFlags.MapRenderPhaseSpineDrive` (+ the now-empty `MapRenderFlags`
  class), `ShadowRenderDriver.ForceSpineDriveForTesting`, and `PhaseSpineDriveActive` - the typed
  PhaseChain spine is UNCONDITIONAL. `GetOrBuildChain` builds the PhaseChain always; RunFrame's
  assembler-chain else-branch is KEPT as the FENCED exception fallback for a PhaseFactory throw only
  (the SAFER keep-decision), warned loudly once per pid (`WarnSpineAssemblerFallback`; the C4 warn
  reworded). The cold-load clock guard is unconditional. Rollback is a revert of the 5b commit, never a
  runtime toggle.
- **Deleted - the legacy TracedPath side-channel:** `tracedPathByPid` + `IsDirectorTracedPathActive`;
  `IsTracedPathOwnedThisFrame` COLLAPSED onto the single intent source
  (`IsDirectorTracedPathActiveFromIntent`), and every consumer re-routed to it: the
  `GhostOrbitLinePatch` Director TracedPath suppress (both the icon-drive Prefix and the line Postfix),
  `IsDirectorTracking`'s disjunct, the marker decision's disjunct (already on the selector), and the
  Driver's owned-draw routing (already on the selector). RunFrame stamps ONE intent-sourced map; the
  test seam collapsed to `SetTracedPathIntentStampForTesting`.
- **Deleted - the walk's direct deorbit-clock consumption:** the I1 block now routes through the
  Phase-6 stitcher's absorb APIs (`CrossMemberSeamStitcher.TryResolveTransferDeorbitTailHead` +
  `ResolveDeorbitTailLegHead` - the absorb finally has its production caller), so
  `GhostTrajectoryPolylineRenderer.cs` names NO deorbit-clock helper directly (file-scoped source gate).
  The I1 SWEEP itself is retained: the stitcher promotes the DescentPhase only from the trigger onward,
  and the transfer member's LOITER-phase deorbit-tail sweep has no spine equivalent.
- **Deleted - the dead reconciler write:** `GhostRenderReconciler.NoteIntent` no longer called from
  `RunFrame` (the store lost its last production reader at the Phase-8 unwiring); the type + pure
  predicates stay for their unit tests.
- **Wired - the deferred Tier-C tangent raise:** `rigid-seam-tangent-discontinuity` is now LIVE at the
  descent DRAW site (`Driver.EvaluateDescentSeamTangents`, called for a drawn owned seam-entry leg of a
  stitched descent member - the pure gate `ShouldEvaluateTangentSeamAtDraw`): leaving tangent from the
  bracketing capture conic sampled at the seam, entering tangent from the drawn leg's first two
  body-relative world points; tracing-gated + once-per-onset (`ShouldEmitTangentSeamOnChange`, healed
  seams re-arm). A continuous seam emits nothing.
- **Retained (fenced, documented in the walk):** the Driver walk as the single draw host - the only
  renderer for (1) proto-less pid-0 recordings (never in `scene.GhostPids`), (2) StockConic
  Driver-direct "bridge" legs (8b.2), (3) the boundary-overlap secondary head legs, (4) the forward
  legs/arcs/seam bridges; the dispatch of the OWNED `TracedPathTreatment.TryDrawOwnedLeg` draw; and the
  8e S3b SOLE ownership source `drewNonOrbitalLegRecordings` (KEPT, as is the icon floor). Re-examine
  the fence whenever the spine learns one of those populations.
- **Grep gates:** `scripts/grep-audit-map-render-phase-spine-drive.ps1` (+
  `GrepAuditMapRenderPhaseSpineDriveTests`) forbids the four deleted symbols repo-wide under
  `Source/Parsek/`; `PolylineDriverWalkDeleteGateTests` file-scopes the deorbit-clock delete + the
  NoteIntent delete and positively pins the retained fence. The old flag pins
  (`PhaseSpineDriveFlag_DefaultsOn_TheCutoverFlip`, `PhaseSpineDriveActive_ConstCarriesTheCutover`,
  `IsTracedPathOwnedThisFrame_LegacyElseBranch_RetainedForRollback_SourceGate`) became flag-GONE pins;
  the in-game spine tests dropped the seam writes (PhaseSpineSwap lost its A/B arm - there is no second
  spine to compare against).

---

## 8. Phase 6 — Descent re-stitch (the one new producer built in v1)

**Goal:** the orbit↔landing G1 seam, owned by the minimal `CrossMemberSeamStitcher`.

**What changes:** a `CrossMemberSeamStitcher` that **absorbs the existing clock-domain join** (the swept
deorbit head `recordedDeorbitUT + (currentUt - triggerUT)` + the `captureShift` phase alignment + the
per-leg head-gate, today in `DescentTrigger.cs:265-302` / `ResolveTransferLegHeadUT`) AND adds the G1
**tangent match** (capture-orbit velocity direction at SOI/atmosphere entry ↔ the recorded descent's
first-sample tangent). It composes AFTER the arrival-hold + destination-loiter-trim span-clock remap. The
deorbit arc is promoted to a visible first-class `DescentPhase` (no longer hidden in the transfer
member). **Logging:** Tier-A descent-stitch seam event; Tier-C `rigid-seam-tangent-discontinuity` when
the tangents diverge beyond tolerance. **Tests:** unit — the swept-head/`captureShift` UT join + the G1
tangent-match math + the ordering. In-game — a landing mission renders the deorbit arc and lands with a
continuous seam (zero `rigid-seam-tangent-discontinuity`); the sub-surface ghost retires (the documented
bug closed). **Parity gate:** the oracle runs in **SYNTHESIZED mode** here (rendered == the stitcher's
intended G1 arc — NOT recorded-vs-rendered, because Phase 6 intentionally *changes* the deorbit geometry
to fix the sub-surface bug; cf. design §14 faithful-vs-synthesized scope), plus the explicit
`sub-surface-ghost-retires` + zero-`rigid-seam-tangent-discontinuity` assertions. **Risk:** medium — the
cross-member clock re-home is the substantive work; the existing `DescentTrigger` logic de-risks it.

**Status (implemented, flag-gated):** the clock re-home is DONE — the swept deorbit head + `captureShift`
phase alignment + per-leg head-gate now live in `Source/Parsek/MapRender/CrossMemberSeamStitcher.cs`
(delegating to the pure `DescentTrigger.*` helpers verbatim), invoked only from the `PhaseChain` spine
overload of `ChainSampler.Sample` under `MapRenderFlags.MapRenderPhaseSpineDrive`. The G1 leading seam is
stamped onto the promoted `DescentPhase` segment by `CrossMemberSeamStitcher.StampOrbitLandingSeam` (the
`PhaseFactory` builds `DescentPhase` with a null leading seam by design — seams are a spine concern).
**This satisfies Phase 5b's HARD predecessor:** the `deorbitHead`/`captureShift`/`ResolveTransferLegHeadUT`
consumer is now owned by the stitcher, so 5b may delete the autonomous polyline `Driver` ownership walk
without losing deorbit-head alignment. The deorbit-clock identifiers are kept OUT of the three gated spine
files (`ShadowRenderDriver` / `ChainSampler` / `GhostRenderDirector`); the `SwappedSpine_DoesNotConsumeDeorbitClock_SourceGate`
source-gate remains green. Flag OFF (default): byte-identical (`DescentTrigger.cs` untouched). The
`sub-surface-ghost-retires` fix is live only under the flag, so `todo-and-known-bugs.md` / `CHANGELOG.md`
mark the bug closed when the flag flips default-on (post in-game sign-off), not at this PR.

**Tracer integration (logging priority):** the new producer participates in `MapRenderTrace`. The Tier-A
`DescentStitched` structural event is wired at the stitcher's decision site
(`CrossMemberSeamStitcher.TryStitchDescentSeam` -> `EmitDescentStitchedTraceOnChange`), emitted
once-per-stitch-onset (not per frame) via `MapRenderTrace.ShouldEmitDescentStitchOnChange` and free in
normal play. The Tier-C `rigid-seam-tangent-discontinuity` anomaly's pure predicate + emit helper +
detail builders are built and test-exercised (unit + the in-game live-body-tangent path), but its
PRODUCTION auto-raise is deferred to **Phase 5b**: the leaving/entering world tangents exist only in the
descent DRAW path (`GhostTrajectoryPolylineRenderer.TryDrawLeg` - a `RenderSegment` carries no points),
which is the shared deorbit-clock-hosting file 5b reworks/deletes anyway, so wiring the raise there avoids
a throwaway, flight-touching edit to a soon-deleted file this cycle. In the interim the parity oracle
(SYNTHESIZED mode, this section) is the render-time seam-quality gate.

---

## 9. Phase 7 — Define the fail-closed producers

**Goal:** give cross-SOI / Jool / station a typed home that renders recorded data faithfully and never a
broken synthetic guess.

**What changes:** `SoiCrossing`, `NestedSoiSubtree` (Jool), and the moving-target station phase
(`LiveVesselAnchor` arrival + `HoldPhase` station-period hold) are defined as types; the classifiers
return unsupported → `FaithfulFallback` for them (no synthetic producer). The cross-SOI kink renders the
current `FlexibleSoi` G0 behavior unchanged. **Logging:** Tier-A `fail-closed-to-faithful` event naming
the unsupported producer. **Tests:** unit — the fail-closed decision per case. In-game — a Jool tour /
station approach / cross-SOI transfer renders the recorded trajectory faithfully (oracle green), with the
`fail-closed-to-faithful` line present. **Parity gate:** the oracle runs in **FAITHFUL mode** (rendered
== recorded), since fail-closed renders the recorded trajectory verbatim. **Risk:** low
(no new geometry; fail-closed is what the classifiers already do).

**Status (implemented, define-only / geometry-neutral):** the three types have a typed home.
`Source/Parsek/MapRender/NestedSoiSubtree.cs` (Jool moon-tour body tree + per-leg `SoiCrossing` list +
the pure `TryBuildFromBodySequence` nesting decision) and `Source/Parsek/MapRender/MovingTargetStationApproach.cs`
(the `HeliocentricTransferPhase` arrival-anchored to a `LiveVesselAnchor` + a station-period `HoldPhase`,
joined by a `FlexibleSoi` G0 `PhaseSeam`) join the already-defined `SoiCrossing` (Phase 1). The pure,
headless `Source/Parsek/MapRender/FailClosedClassifier.cs` is the fail-closed DECISION: its `Classify`
returns `MovingTargetStation` when the arrival anchor is a live moving vessel, `NestedSoi` when the
recorded body sequence is a moon-rich tour (two visited bodies are siblings under a shared non-root
ancestor), and SUPPORTED otherwise: a single-level cross-SOI transfer (Kerbin->Mun->Sun->Duna) is NOT
auto-failed (it renders correctly through the existing per-crossing `FlexibleSoi` G0 path, so auto-raising
`CrossSoiChain` on every ordinary interplanetary mission would be noise that changes nothing). The
`CrossSoiChain` reason + its detection are defined for the deferred whole-patched-conic-chain synthesis
effort's home, surfaced only by the explicit `ClassifyCrossSoiChainForTesting` seam, never by the live
path. A fail-closed decision changes only PROVENANCE (`FaithfulFallback`) and emits the tracer event; it
NEVER mutates geometry (the three synthetic producers do not exist in v1, so "fail-closed to faithful" is
exactly what the pipeline already does, and the cross-SOI kink renders the current `FlexibleSoi` G0
behavior unchanged). **Decision site:** `PhaseFactory.EmitFailClosedDecisionTraceIfEnabled` (called from
`BuildPhaseChain` AFTER the chain is built, returning the same `PhaseChain`), gated wholly on
`MapRenderTrace.IsEnabled`, so flag-OFF (`MapRenderPhaseSpineDrive` default-OFF) is byte-identical and
tracing-OFF normal play pays a single bool check and never touches the live `FlightGlobalsBodyInfo`
resolver (keeping the headless factory tests pure). The deorbit-clock identifiers stay out of the spine
files, so `SwappedSpine_DoesNotConsumeDeorbitClock_SourceGate` remains green.

**Tracer integration (logging priority):** the Tier-A `fail-closed-to-faithful` structural event
(`MapRenderTrace.EventFailClosedToFaithful`) is emitted once-per-event from the fail-closed decision site
(`FailClosedClassifier.EmitFailClosedToFaithful` -> `MapRenderTrace.EmitStructural`), gated on
`MapRenderTrace.IsEnabled` and deduped per-pid/per-reason via `MapRenderTrace.ShouldEmitFailClosedOnChange`
(its `lastFailClosedSignatureByPid` signature dict mirrors `ShouldEmitDescentStitchOnChange` /
`lastDescentStitchSignatureByPid`, same `MaxTrackedMarkerDecisionKeys` warp cap, cleared in
`MapRenderTrace.Reset()` on scene switch), so a steady fail-closed member emits ONE line, not one per
frame. The line names the unsupported PRODUCER token (`FailClosedClassifier.ReasonToken`) plus the
`faithful-fallback` provenance and the case payload (the nested-SOI subtree summary), built by the PURE
`FailClosedClassifier.BuildFailClosedDetails` (+ the per-type `ToSummaryToken` builders) so the detail
schema is directly unit-testable without the global log sink. The v1 LIVE path detects the nested-SOI
(Jool) case from the recorded body sequence; the moving-target-station case is reachable only through the
explicit `FailClosedClassifier.Classify` seam the unit / in-game tests drive (no faithful trajectory
resolves a `LiveVesselAnchor` arrival in v1).

---

## 10. Phase 8 — Retire the circular reconciler; finalize tracer EVENT coverage

**Goal:** make the parity oracle the sole acceptance oracle and close out observability.

**What changes:** **unwire** the now-circular intent-vs-old-truth comparator — remove the live
`GhostRenderReconciler.CheckIntentAgainstOldTruth` call site (`MapRenderProbe.cs:528`) and add a
grep-audit gate on `CheckIntentAgainstOldTruth`. The Phase-0 recorded-vs-rendered oracle is a **distinct
axis that has coexisted since Phase 0** and now stands alone — this is an **unwiring, NOT a
rename/promote** of the old comparator. Finalize the
`MapRenderTrace` EVENT coverage so every surface and every new phase/seam/lifecycle event has
appear/disappear + Tier-A/B/C lines. **Logging:** the full tracer matrix is now live. **Tests:** in-game
— a `mapRenderTracing`-on run over the regression set emits the expected Tier-A/B/C lines for every new
surface (a tracer-coverage assertion). **Parity gate:** oracle green. **Risk:** low (cleanup).

**Status (implemented, observability-only / flag-OFF byte-identical):**
- **Unwiring DONE.** The live `GhostRenderReconciler.CheckIntentAgainstOldTruth` call site was removed from
  `Source/Parsek/MapRenderProbe.cs` (the end-of-frame "Tier-C new-pipeline reconcile" block in `Sample`,
  replaced with a comment recording the unwiring rationale). Once the spine drives the render (Phases 3-7)
  the OLD truth this probe reads (`lineActive` / `drawIcons` / `polylineOwns`) IS the spine's own
  consequence, so the intent-vs-old-truth comparison became CIRCULAR / self-confirming; the Phase-0
  recorded-vs-rendered `RenderParityOracle` (`TrySampleAndEmitFaithfulOrbitParity` -> `parity-drift`) is the
  DISTINCT axis that has coexisted since Phase 0 and is now the SOLE acceptance oracle. The whole probe is
  `MapRenderTrace.IsEnabled`-gated (tracing OFF by default), so the call only ever ran when tracing was on;
  removing it is OBSERVABILITY-ONLY and flag-OFF / tracing-OFF normal play is byte-identical (the probe never
  ran). The scene-switch `GhostRenderReconciler.ClearRateLimitState()` call is KEPT (now a harmless no-op in
  production, still populated/cleared by the unit tests) with an updated comment.
- **KEEP-not-remove decision (justified).** This is an UNWIRING, not a deletion of the type. The
  `GhostRenderReconciler` type, its PURE predicates (`ReconcileVisibility` / `ReconcileTreatment` /
  `IsPolylineOriginShiftJump`), and the `CheckIntentAgainstOldTruth` method itself are KEPT, exercised by
  `Source/Parsek.Tests/MapRender/GhostRenderReconcilerTests.cs` (~14 references). Removing the method would
  force deleting/rewriting those tests for no functional gain - not the minimal change the phase brief asks
  for ("prefer removing only the LIVE call site unless removing the method is clearly clean"; it is not).
  The shadow PRODUCER side (`ShadowRenderDriver` -> `GhostRenderReconciler.NoteIntent`) is UNAFFECTED: it
  feeds the spine, not the retired comparator, so it stays live. Class + method doc-comments updated to
  record the Phase-8 unwiring.
- **Grep-audit gate.** `scripts/grep-audit-render-reconciler-unwired.ps1` asserts ZERO LIVE call sites of the
  comparator under `Source/Parsek/`. The forbidden token is the CALL form (a leading dot + a trailing
  open-paren) so the KEPT method DEFINITION (no leading dot) and the doc-comment crefs (no trailing paren)
  are never flagged; the xUnit tests under `Source/Parsek.Tests/` are out of the audit's `Source/Parsek/`
  scope. Its xUnit gate (mirroring `GrepAuditMapRenderDirectorDriveTests`, with a managed fallback for
  non-Windows CI) lands in the Phase-8 test step.
- **Tracer EVENT coverage: AUDITED COMPLETE, no production gap closed.** Every `RenderSurface` already has
  appear/disappear + Tier-A/B/C coverage: ProtoOrbitLine / ProtoIcon via `GhostCreated` / `GhostDestroyed`
  (A) + `LineVisibilityChange` / `body-orbit` / `icon-suppressed` / `drawIcons` / `line.active` (B) +
  `icon-teleport` / `icon-off-orbit` / `line-blink` / `decision-vs-truth` / `polyline-orbit-overlap` /
  `parity-drift` (C); Polyline + PolylineForwardArc via `PolylineLegChange appear|disappear` (A) +
  `unaccounted-drawn-recording` (C); ImguiLabeledMarker + AtmosphericMarker via `MarkerDecision`
  on-change (B, both scenes); the flight-scene mesh via `GhostRenderTrace` `MeshSpawned` / `MeshDestroyed`
  (A). The NEW phase/seam/lifecycle events are covered: `PhaseChainAssembled` (Phase 3, A),
  `DescentStitched` (Phase 6, A), `fail-closed-to-faithful` (Phase 7, A), `factory-parity` (Phase 2, C). The
  only production-inert tokens (`rigid-seam-tangent-discontinuity` and the reserved
  `retire-not-held` / `anchor-resolve-fail` / `clock-not-ready`) are deferred BY DESIGN: the tangent token's
  production auto-raise lives at the descent DRAW site that Phase 5b reworks (Phase 5b is a separate parallel
  branch, not part of this Phase-8 tree), and the other three are future-phase reserved constants documented
  as wired-but-inert. No real Phase-8 gap exists; the build-core step adds no new EVENT emits. The remaining
  Phase-8 work is the tracer-coverage in-game assertion + the grep-audit xUnit gate (test step).

---

## 10b. Cutover-hardening instruments (stacked on Phase 8)

**Goal:** build the observability + guard instruments that make the eventual flag-ON flip (the spine drives
the render) and the 5a/5b legacy-draw deletes provably regression-free, BEFORE any of those irreversible
steps. A 5-lens cutover-readiness audit found gaps in the live oracle and four previously-unraised
guard/anomaly sites; this stacked PR closes them. Every item is ADDITIVE and reversible: with the spine flag
OFF (`MapRenderFlags.MapRenderPhaseSpineDrive` default false) AND tracing OFF (`mapRenderTracing` default
off) normal play is BYTE-IDENTICAL to today.

**Status (implemented, observability-only / flag-OFF + tracing-OFF byte-identical):**

- **A1 - the live parity oracle gains the SYNTHESIZED lens (rendered-vs-intended) it was missing.** Before
  this PR the only LIVE oracle caller (`MapRenderProbe.ComputeFaithfulOrbitParity`) ran the oracle in
  `ParityMode.Faithful` ONLY and SKIPPED every re-aimed / synthesized member (no-covering-segment /
  body-mismatch), so a re-aim / descent / TracedPath DRAW regression produced no anomaly at all. The pure
  Synthesized diff (`RenderParityOracle.ComputeDrift` / `ComputeDriftScaleDerived` in `ParityMode.Synthesized`,
  count-independent nearest-point projection) and `NearestDistanceToPolyline` already existed and were
  unit-tested but had NO live caller. A1 wires two live Synthesized lenses, both inside the
  `MapRenderTrace.IsEnabled`-gated probe (tracing OFF = the probe never runs = zero new work):
  - SYNTHESIZED conic parity: rendered StockConic vs the Director's intended re-aimed seed
    (`ShadowRenderDriver.TryGetFreshStockConicSeed` = `intent.Payload.Conic`, stamped in BOTH flag states).
    The intended reference is built PHASE-MATCHED to the live rendered orbit (its epoch baked with the SAME
    loop shift `StockConicTreatment.SeedAndDriveLive` drove the rendered conic with,
    `GetGhostOrbitEpochShift(pid)`, via the shared `BuildPhaseMatchedReferenceOrbit` helper - the SAME helper
    the faithful lens uses) and BOTH orbits are sampled at the SAME live-clock UTs. So a faithful member reads
    ~0 whether or not it is looped (a non-loop member's shift is ~0; a LOOPED member's reference now lands on
    the same arc as its rendered orbit instead of a different mean-anomaly half-arc). Without this the raw-
    epoch reference false-fired `parity-drift` on every CORRECT looped synthesized draw. The lens lights up
    only on a re-aim DRAW divergence - the exact complement of the faithful lens's skip. Emits
    `parity-drift mode=synthesized`.
  - SYNTHESIZED polyline-leg parity: the live `VectorLine.points3` (scaled->world body-relative) vs the leg's
    own recorded surface track, diffed per drawn leg. CONIC-ANCHORED legs are SKIPPED (a known, stated
    limitation): the draw intentionally rotates an anchored leg ~96 deg onto the bracketing conic seam, so a
    rendered-vs-raw-recorded diff would false-fire on a CORRECT anchor (and read ~0 on a leg that FAILED to
    anchor - exactly backwards), and the anchor's per-point Slerp is not cheaply recoverable to rebuild the
    reference in. The lens therefore validates only NON-anchored body-fixed legs (descent / atmospheric /
    surface - the descent re-stitch the audit cares about), where the rendered points ARE the raw body-fixed
    points so rendered == recorded by construction and a genuine mis-draw drifts. Emits
    `parity-drift mode=polyline`.
  - The pure diff math (`ComputeDrift*` / `EstimateScaleFromPoints` / `ToleranceForScale`) is xUnit-green
    (the RenderParity regression set, including a Synthesized rotated-arc and a dense-vs-sparse count-mismatch
    case). The Unity capture orchestration (`ComputeSynthesizedConicParity`,
    `CaptureRenderedVsRecordedLegGeometry` - Orbit construction / `points3` / `ScaledSpace` /
    `GetWorldSurfacePosition`) and the per-pid / per-recording rate-limit guards are Unity-bound and validated
    by the FOLLOW-UP in-game harness (see below).

- **B4/D2 - cold-load clock-readiness guard (design 11.2).** `ShadowRenderDriver.RunFrame` reached the MAP
  spine at the cold-load Planetarium UT=0 (or a non-finite UT) before the clock was established, with no
  `liveUT <= 0` check (`ChainSampler.Sample` passed `liveUT` straight through). Sampling the span clock at
  UT<=0 would place a degenerate TS/map ghost on the first cold-load frames. B4 adds the pure predicate
  `ShadowRenderDriver.IsLiveClockReady(double)` (strictly positive AND finite, mirroring
  `LedgerOrchestrator.IsCurrentUtReadyForCutoff(ut > 0)`, extended with a finite guard) and, gated on
  `PhaseSpineDriveActive` (flag-ON ONLY), DEFERS the whole spine frame (renders nothing / holds) when the
  clock is not ready, raising the once-per-event `clock-not-ready` anomaly. The defer is a HOLD: it does NOT
  call `PruneStaleState`, so the cached chains / prior intents the next ready frame resumes from survive. This
  guard lives in the MAP spine, NOT flight gameplay, and only affects the flag-ON path; flag-OFF is unchanged.

- **C1 - three previously-unraised Tier-C cutover anomalies now have production raises.**
  `clock-not-ready` / `retire-not-held` / `anchor-resolve-fail` were defined-but-inert constants (Phase 8
  status above). C1 wires their raises, each tracing-gated and deduped once-per-event:
  - `clock-not-ready`: the B4 defer above.
  - `retire-not-held` (pure `MapRenderTrace.IsRetireNotHeld`): a member whose sample resolved OUTSIDE its
    window (should retire) yet was kept visible (held) - the inverse-hold defect. The Director's
    OutsideWindow case returns Hidden, so this never fires in the normal pipeline; it is the guard that proves
    it stays that way.
  - `anchor-resolve-fail` (pure `AnchorFrameResolver.ResolveBodyAndRaise` over the existing `ResolveBody`
    decision): a visible BodyAnchor whose body fails closed (missing / unknown body) - the fail-closed (hide)
    outcome the draw already takes, now made observable rather than a silent drop.
  - Shared once-per-event dedup `MapRenderTrace.ShouldEmitCutoverAnomalyOnChange` (warp-capped + scene-switch
    cleared, mirroring the sibling `ShouldEmit*OnChange` gates).

- **C4 - the silent flag-ON -> assembler-chain fallback is now observable.** `BuildChainSignature` omits the
  spine flag from the cache key, so after an A/B toggle / hot-reload (without a `Reset`) a chain cached while
  the flag was off carries a null PhaseChain and the flag-ON branch silently falls through to the LEGACY
  assembler chain. C4 warns ONCE per pid (`WarnSpineFlagOnAssemblerFallback`, one-shot HashSet cleared in
  `Reset()` + `PruneStaleState`) inside the flag-ON `else` branch so the toggle is visible without flooding.
  The render result is unchanged (the assembler chain still renders); this only surfaces that the flag-ON run
  is driving the legacy spine for that ghost.

- **Flag-OFF / tracing-OFF byte-identical.** A1 / C1's `retire-not-held` + `anchor-resolve-fail` are behind
  `MapRenderTrace.IsEnabled` (tracing OFF short-circuits before any work). B4's defer + C4's warn are behind
  `PhaseSpineDriveActive` (the false const folds out; flag-OFF never enters either block). No
  currently-supported producer's geometry changed; the renderer accessor only READS `points3`. No flight-scene
  gameplay touched. No new user-visible default-play behavior, so NO CHANGELOG / todo entry (the cold-load
  defer is a flag-ON-only guard; everything else is observability gated on `mapRenderTracing`).

- **Tests.** Headless: `Source/Parsek.Tests/MapRender/CutoverHardeningTests.cs` (the pure predicates
  `IsLiveClockReady` / `IsRetireNotHeld` / `AnchorFrameResolver.OutcomeToken` + `ResolveBodyAndRaise`, the
  once-per-event dedup + warp-cap, the three emit helpers' reason tokens + IsEnabled short-circuits, and
  source gates proving the RunFrame wiring is flag-/tracing-gated). The Synthesized diff MATH is covered by the
  RenderParity regression set. **FOLLOW-UP (next PR) = the LIVE in-game validation harness:** the A1
  Unity-capture orchestration (synthesized conic + polyline-leg capture), the B4 cold-load defer firing at
  UT=0 (`Source/Parsek/InGameTests/ColdLoadClockGuardInGameTest.cs`), the C4 one-shot warn on a live flag
  toggle, and the descent / re-aim regression scenarios are all Unity-bound (RunFrame reads
  `Time.frameCount` + a live scene; Orbit / Vectrosity / ScaledSpace are Unity-bound) and must run via
  Ctrl+Shift+T in FLIGHT to actually exercise the live capture. The headless gates lock the decision logic +
  the schema those instruments feed.

---

## 10c. Cutover regression harness (stacked on Phase 9)

**Goal:** turn "flag-ON is byte-identical BY CONSTRUCTION" into "demonstrably regression-free ACROSS
gameplay" by adding the tests the 5-lens cutover-readiness audit found missing, now that the Phase-9 parity
oracle is trustworthy (faithful + synthesized + polyline lenses all live). TEST-FOCUSED + additive: NO
producer geometry or draw path changed, so with the spine flag OFF
(`MapRenderFlags.MapRenderPhaseSpineDrive` default false) AND tracing OFF (`mapRenderTracing` default off),
normal play stays BYTE-IDENTICAL to today (all flag-OFF / tracing-OFF source gates green - see "Status" below).

**Architectural truth these tests respect (do not write a test that assumes otherwise):** through this
stack, flag-ON only swaps the DECISION SOURCE (the typed `PhaseChain` spine); the LEGACY code still DRAWS
the pixels (`GhostOrbitLinePatch` for StockConic, the autonomous `GhostTrajectoryPolylineRenderer.Driver`
for TracedPath, IMGUI markers). The geometry REPLACEMENT (e.g. the Phase-6 descent sub-surface-retire fix)
only LANDS at the draw layer when Phase 5b deletes the legacy draw. So a flag-ON test asserts the CURRENT
contract: (i) the spine's DECISION/intent is correct and matches what flag-OFF would decide for faithful
members (byte-identical), (ii) the live oracle stays GREEN (zero parity-drift) on correct draws, and (iii)
for the descent re-stitch it DOCUMENTS that the retire is not yet live at the draw layer (lands at 5b)
rather than asserting a pixel change.

**Status (implemented, test-only / flag-OFF + tracing-OFF byte-identical):**

### Headless comparator + intent-parity widening (A2 / A4 / A5)

- **A4 - the byte-parity comparator's seam / generated omission is now a CHECKED invariant, not a silent
  gap.** `GeometryParityComparator.CompareSegment` deliberately does NOT byte-compare
  `RenderSegment.LeadingSeam` / `TrailingSeam` / `IsGenerated`: `PhaseFactory.ClassifySegment` builds every
  phase with NULL seams by design (seam re-derivation is a spine-side / Phase-5b concern), while
  `ChainAssembler.AssignSeams` stamps `Rigid` / `FlexibleSoi`, so the two diverge BY CONSTRUCTION; comparing
  them would force a forbidden producer change and false-fail every real factory-vs-assembler build. Instead
  of GATE-by-comparison, A4 adds a SOURCE gate
  (`Source/Parsek.Tests/MapRender/SeamFieldsDrawIrrelevantSourceGateTests.cs`) asserting the two LIVE
  PIXEL-DRAW files (`Patches/GhostOrbitLinePatch.cs`, `Display/GhostTrajectoryPolylineRenderer.cs`) read
  ZERO `RenderSegment` seam / `IsGenerated` fields (comments stripped, whitespace collapsed; mirrors
  `FailClosedWiringSourceGateTests`), plus a sanity test that the comparator header documents the omission +
  names the gate. The fields are provably draw-irrelevant today (`GhostRenderIntent` carries treatment /
  payload / frame / drive-UT but NO seam / `IsGenerated`), so omitting them cannot let a draw-affecting
  divergence pass. The gate is the pre-5b TRIPWIRE: the instant a draw path reads a seam / `IsGenerated` off
  a spine segment (e.g. Phase 5b makes the descent G1 seam load-bearing), it FAILS and forces the comparator
  to widen + the factory to stamp seams at exactly the right moment. Doc updates: comparator class header +
  inline NOTE in `CompareSegment`. CLOSES the audit's "comparator surface omits seam / generated fields"
  finding.

- **A5 - intent parity widened from a SUBSET to the FULL conic Kepler set, plus an inclined / nonzero-LAN
  fixture.** `PhaseSpineParityTests.AssertIntentParity` compared only `sma` / `ecc` / start / end / body;
  A5 widens it to inclination / LAN / argPe / MnA / epoch / isPredicted, and adds an `InclinedChain()`
  fixture (inc 28.5 deg, LAN 75 deg, argPe 40 deg, MnA 1.2, epoch 12.5) with a spot-frame theory, a
  non-vacuity guard, and a full prior-threaded sweep. CLOSES the audit's "parity asserts only a subset of
  conic elements, equatorial zero-LAN fixtures only" finding.

- **A2 - the realistic MULTI-segment flag-ON-vs-OFF differential the audit found missing.**
  `Source/Parsek.Tests/MapRender/MultiMemberSpineParityTests.cs` runs ONE faithful recording whose
  assembled chain spans Kerbin ascent (TracedPath) -> Kerbin parking (StockConic) -> Kerbin->Mun SOI
  crossing (FlexibleSoi seam) -> Mun arrival (StockConic) -> Mun descent (TracedPath), with inclined /
  nonzero-LAN elements, through BOTH spines: full geometry byte-parity (`GeometryParityComparator`) + full
  intent parity over a continuous `[-5, 85]` 0.5s sweep + per-segment spot frames + a fixture-shape guard.
  CLOSES the audit's "single-member equatorial circular fixtures only; no realistic multi-segment / SOI
  chain through both spines" finding.

### Flag-ON live in-game scenario tests (A3 / B-rows)

All five are `[InGameTest(Category="MapRender", Scene=GameScenes.FLIGHT)]`, auto-discovered by reflection,
run via Ctrl+Shift+T. Each drives the REAL wired path (`ShadowRenderDriver.ForceSpineDriveForTesting` +
`MapRenderTrace.ForceEnabledForTesting`) and asserts via the Phase-9 oracle, respecting the architectural
truth above (assert the decision-source-swap contract; never a 5b-only geometry change).

- **A3 - `FlagOnParityBaselineInGameTest.cs`** (known-good + loop-shifted): the FLAG-ON parity BASELINE the
  audit found missing (the existing `RenderParityBaselineTest` is flag-OFF; `PhaseSpineSwapInGameTest`
  exercises only the faithful lens). Drives the REAL `ShadowRenderDriver.RunFrame` SPINE-ON over a live
  faithful ghost and asserts ZERO parity-drift across the FAITHFUL + SYNTHESIZED Phase-9 oracle modes,
  plus a POLYLINE ORACLE ZERO-CONTRACT SANITY check (rendered == recorded input yields zero drift; NOT
  live polyline capture coverage - the live `CaptureRenderedVsRecordedLegGeometry` walk needs a real map
  render and is validated by tracing-on play sessions), the spine's stamped loop-shift matches
  `GetGhostOrbitEpochShift`, and NO `parity-drift` anomaly fired on the trace sink. Non-vacuous: each lens
  must `Sampled` + `HasMeasurement` or it fails as blind; the loop-shifted arm bakes a 1100s shift
  end-to-end through the real seam. CLOSES the section 11.5 "loop a single recording" /
  baseline-only-flag-OFF gap for the faithful + synthesized lenses, flag-ON; live polyline flag-ON capture
  remains a tracing-on play-session check.

- **B-row2 - `ReaimedLoopSynthesizedOracleInGameTest.cs`** (the Phase-9 SYNTHESIZED payoff): drives a live
  ghost from a RE-AIMED segment (recorded shape rotated 70 deg in LAN) while the recording stores the
  un-re-aimed segment. SYNTHESIZED oracle (rendered conic vs the re-aimed INTENDED seed) reads ~0 (the
  proof); FAITHFUL oracle (rendered vs RECORDED) FLAGS drift (the negative control proving the faithful-only
  oracle could not have validated a re-aimed draw, and that the LAN rotation actually moves the conic - a
  tautology guard). CLOSES section 11.5 "re-aim with a large synodic target move" / "mixed faithful + re-aimed
  members" at the LIVE synthesized-lens layer (headless only before).

- **B-row1 - `DescentEndToEndSpineInGameTest.cs`** (descent end-to-end through the spine): drives the EXACT
  RunFrame-inlined pair (`ChainSampler.Sample(PhaseChain, liveUT, units)` -> `GhostRenderDirector.Decide`)
  over a descent-trigger unit + descent `PhaseChain` at a TRIGGERED live UT and a PAST-END UT. Asserts the
  spine resolves a VISIBLE re-anchored first-class `DescentPhase` at the swept-deorbit head with the Rigid
  orbit-landing seam, and RETIRES (OutsideWindow -> Hidden, no held sample) past the clip even though the
  prior frame was visible. **GATE FINDING surfaced (not faked):** asserts `IsRenderingNonOrbitalLeg(recId)
  == false` after a correct spine descent DECISION - documenting that the spine's descent decision does NOT
  own the leg DRAW (the legacy autonomous Driver owns the pixels), so the Phase-6 sub-surface-retire fix
  lands at the DRAW layer ONLY at Phase 5b. Asserts the current decision-source-swap contract, not a
  5b-only pixel change. CLOSES section 11.5 "atmospheric descent to landing" / "reentry / destruction
  mid-recording retire" at the spine-DECISION layer (the draw-layer retire stays expected-to-change until
  5b).

- **B-row4 - `ParentAnchoredChildSpineInGameTest.cs`** (parent-anchored controlled child): (a) drives the
  DEFINE-ONLY `AnchorFrameResolver.ResolveParentAnchoredChild` (ZERO production callers until Phase 5b
  wires the spine's parent-anchored routing through it) on LIVE-body UT magnitudes through all three
  outcomes - >=2-sample in-range -> `BodyFixedPrimary`; out-of-range / no loop-frames -> `Retire` (never
  clamp to a stale child offset, the documented "stale ghost" bug it prevents); too-few-samples + covering
  loop frames -> `AnchorLocalSecondary`; (b) drives a live controlled-decoupled child ghost
  (`IsDebris=false`, `ParentAnchorRecordingId` set) through RunFrame spine-ON as a CRASH-SMOKE + PLUMBING
  check with the faithful oracle green (the oracle compares the ghost against the segment it was created
  from - green-by-construction for the routing question). Non-vacuous as a contract pin: all three
  resolver branches asserted distinctly; the oracle arm must `Sampled` + `HasMeasurement`. This is a
  CONTRACT PIN for the 5b wiring, NOT closed spine-decision coverage of parent-anchored routing; the
  section 11.5 "controlled-decoupled child (lander off a stage)" dual-surface routing row CLOSES when 5b
  wires the resolver.

- **B-row9 / B-row20 - `WarpThroughInteriorGapSpineInGameTest.cs`** (the HoldPhase decision +
  interior-gap warp-step hold). **HoldPhase decision: VACUOUS-UNDER-FLAG-ON in v1, with evidence.** The
  factory constructs ZERO live HoldPhases - the only `new HoldPhase(...)` in the pipeline is in
  `MovingTargetStationApproach.cs`, which `FailClosedClassifier` routes to FaithfulFallback (never reaches a
  live spine-driven chain); interior chain gaps are coverage-classified
  (`PhaseChain.ClassifyCoverage -> InInteriorGap`), NOT modelled as HoldPhases. Building a HoldPhase
  producer would be new geometry (out of this test-only scope). The test documents this and asserts the
  HoldPhase warp-safety contract on a CONSTRUCTED HoldPhase (`CoversUt` whole span, `Treatment.None`)
  without shipping a producer. **Plus the live, valuable coverage-state warp-step assertion:** drives the
  RunFrame-inlined `ChainSampler.Sample` + `GhostRenderDirector.Decide` pair across a 2-phase chain with a
  real interior gap at 3 UTs (phase 1 / a single warp step landing IN the gap / a step past into phase 2);
  asserts the gap HOLDS the prior visible intent (same treatment + body, no blink / retire) and the ghost is
  NEVER Hidden once visible. Non-vacuous: a pre-assert confirms the fixture chain actually classifies
  `InInteriorGap`. CLOSES section 11.5 "warp through HoldPhase / loiter / descent re-anchor" via the OBSERVABLE
  EQUIVALENT the spine actually uses in v1 (the interior-gap hold), and DISPOSITIONS the HoldPhase producer
  row as vacuous-for-v1 with factory evidence.

### Gate findings surfaced (not faked)

- **Descent retire is not yet live at the draw layer (lands at Phase 5b).** B-row1's
  `IsRenderingNonOrbitalLeg == false` assertion (after a correct spine descent decision) turns the
  architectural note into a passing TRIPWIRE: it documents that the spine's descent decision does not own
  the leg DRAW, so the Phase-6 sub-surface-retire fix only reaches the pixels when 5b deletes the legacy
  autonomous Driver. The G1 tangent-discontinuity PRODUCTION anomaly raise at the descent draw site is
  likewise a Phase-5b item; the headless `CrossMemberSeamStitcher` predicate + this in-game test exercise it
  only at the predicate / sample layer.
- **HoldPhase has no live v1 producer.** B-row9/20 dispositions the row as vacuous-under-flag-ON with the
  factory evidence above, rather than fabricating a producer (which would be out-of-scope new geometry).

### Flag-OFF / tracing-OFF byte-identical

No producer geometry or draw code was touched - only test files plus comparator doc-comments. Flag-OFF
(`MapRenderPhaseSpineDrive` default false) and tracing-OFF (`mapRenderTracing` default off) normal play is
byte-identical to today; every flag-OFF / tracing-OFF source gate stays green: the A4 seam-omission gate
(`SeamFieldsDrawIrrelevantSourceGateTests`), `FailClosedWiringSourceGateTests` (incl. the SwappedSpine
deorbit-clock + fail-closed-classifier discipline), and the repo-wide grep-audit gates
(`grep-audit-render-reconciler-unwired`, `grep-audit-map-render-director-drive`,
`grep-audit-active-leg-recordings`, `grep-audit-non-loop-live-pid`, `grep-audit-ers-els`). Because this is
test-only / additive with no user-visible default-play change, there is NO CHANGELOG / todo entry.

### Left for the runtime-only rows headless cannot reach

The section 11.5 runtime-only rows that need a real KSP lifecycle / scene frame remain producer-phase /
follow-up in-game work: quickload mid-gap, dock / undock member swaps, overlap gap-hold, in-bubble switch,
Fly / Switch-To teardown, warp across a synodic boundary, and the scene-transition no-one-frame-blank settle.
(Cold-load UT<=0 is already covered flag-ON by Phase 9's `ColdLoadClockGuardInGameTest` - the clock-readiness
defer guard - so it is NOT in this list.) The headless gates + the five flag-ON scenario tests above lock the
decision / geometry contract those runtime rows build on.

---

## 10d. Phase 11 - test automation + coverage (stacked on Phase 10)

**Goal:** make every MapRender in-game test RUN and ASSERT (not skip) from a fresh launch-pad FLIGHT
scene with zero manual setup and FAST, and add self-contained gameplay-case coverage the prior phases
left to the manual runbook. TEST-ONLY + additive: NO production geometry / draw / spine code changed,
so with the spine flag OFF (`MapRenderFlags.MapRenderPhaseSpineDrive` default false) AND tracing OFF
(`mapRenderTracing` default off) normal play stays BYTE-IDENTICAL to today (all flag-OFF / tracing-OFF
source gates stay green).

**Status (implemented, test-only / flag-OFF + tracing-OFF byte-identical):**

### P1 portability hardening (the two prior in-game tests no longer pass-as-skip on a cold pad)

`Source/Parsek/InGameTests/TracedPathOwnedDrawInGameTest.cs` and
`Source/Parsek/InGameTests/SuppressedMarkerOwnedDrawInGameTest.cs` previously resolved a PRE-EXISTING
ghost (via `GetGhostVesselPidForRecording` + `FindVessel`) and, when none was present on a cold pad,
Skipped - so they passed as skips with no assertion. Both now SELF-CREATE their ghost via
`GhostMapPresence.CreateGhostVesselFromSource(... TrackingStationGhostSource.StateVector ...)` (the
`RenderParityBaselineTest.cs` create pattern) from the recording's first flat low-altitude
`TrajectoryPoint` (a non-orbital leg has no conic to seed), which registers the pid in
`ghostMapVesselPids` + the pid->recording maps the spine resolves through. The two pass-as-skip Skips
were removed: the spine-decided-TracedPath early-return is now a firm `InGameAssert.IsTrue(legacyActive,
...)`, and the FLAG-ON intent-stamp assertion is unconditional. The ONLY Skips kept are legitimate
environmental guards - `Kerbin not found` (non-stock pack), `MapViewScene not active` (not in FLIGHT),
and a new `ghost ProtoVessel did not create` (a true no-proto-at-all context, NOT a pass-as-skip on the
TracedPath / marker render decision).

### Five new SELF-CONTAINED pad-runnable in-game tests

All are `[InGameTest(Category="MapRender", Scene=GameScenes.FLIGHT)]`, VOID (not IEnumerator - fast),
self-contained (build their own synthetic Recording / ghost / PhaseChain - no live mission / active
vessel / loaded save), and CLEAN UP in `finally` (remove synthetic ghosts via `GhostMapPresence`,
restore `ForceSpineDriveForTesting` / `ForceEnabledForTesting` / `CurrentUTNow` / `RecordingStore`,
`ShadowRenderDriver.Reset()`). Each carries the honest caveat that it asserts the spine
DECISION / parity, not the live ProtoVessel lifecycle / 5b pixel. Files under
`Source/Parsek/InGameTests/`:

- **N1 `OverlapNInstanceSpineInGameTest.cs`** (P2-pure) - copies `OverlapUnit` verbatim from
  `ChainSamplerTests.cs:72` (two-arg overlap LoopUnit ctor + `UnitMemberOverlaps`); samples 8 liveUTs
  across 4 span-cadence cycles through `ChainSampler.Sample` + `GhostRenderDirector.Decide`. Asserts
  >=2 DISTINCT selected-cycle head-UTs, every sample InSegment + Visible, `DriveUT ==
  ResolveTrackingStationSampleUT` (legacy single-head parity = ONE ghost not N), never Hidden-after-
  visible. A frozen-head / N-at-once regression collapses the distinct-head count to 1 and fails.
- **N2 `DockUndockMemberWindowSpineInGameTest.cs`** (P2-pure) - a `LoopUnit` with `memberWindows`
  (`MemberWindow` ctor at `GhostPlaybackLogic.SpanClock.cs:61`): absorbed member trimmed to dockUT,
  split child beginning at splitUT, cadence==span + anchor==spanStart so `spanLoopUT` tracks liveUT
  linearly. Samples at boundary -/+ 1.0s. Asserts the absorbed member flips Visible->Hidden EXACTLY at
  dockUT (no clamp past its window = the stale-ghost-after-dock guard) and the split child
  Hidden->Visible exactly at splitUT.
- **N3 `MultiBodyConcurrentGhostsParityInGameTest.cs`** (P1-ghost) - TWO live faithful ghosts in one
  `RecordingStore` swap (Kerbin orbit idx 0, Mun orbit idx 1) via `CreateGhostVesselFromSource(...
  Segment ...)`; runs `ComputeFaithfulOrbitParity` for each vs its OWN body. Asserts both Sampled (not
  body-mismatch-skipped) + zero drift, DISTINCT pids, own-body framing (a cross-frame leak surfaces as
  body-mismatch skip or large drift). Skips cleanly if Mun absent (mirrors the fail-closed Jool guard);
  cleans up BOTH ghosts + restores RecordingStore / CurrentUTNow in `finally`.
- **N4 `TerminalRetireDecisionInGameTest.cs`** (P2-pure) - a one-phase `DescentPhase` PhaseChain whose
  window ENDS at a terminal crashUT (recording `EndpointPhase=SurfacePosition`). Samples inside
  (Visible) and past the terminal end WITH a visible prior intent fed in; asserts
  `Coverage.OutsideWindow` past end -> `Decide` returns Hidden even with the visible prior (the
  crash-no-linger retire). A regression that held the prior (treating post-terminal as an interior gap)
  keeps it Visible and fails.
- **N5 `BgOnRailsAllOrbitalSpineInGameTest.cs`** (P1+P2) - an all-`OrbitSegment` recording (NO
  atmospheric Points, NO TrackSections = the on-rails BG shape). Runs the REAL
  `PhaseFactory.BuildPhaseChain` and asserts zero Descent / Surface / Ascent phases + >=1 conic phase,
  then creates the live ghost and asserts `ComputeFaithfulOrbitParity` zero drift.

The P2-pure tests (N1 / N2 / N4 + N5's factory half) do pure builder / count-seam work, so they run
instantly from a cold pad. The P1-ghost tests (N3, N5's parity half) author their own Recording(s), swap
`RecordingStore`, create the ghost via `CreateGhostVesselFromSource`, and restore in `finally`.

### New headless unit tests

- `PhaseFactoryTests.cs`: ascent/descent positional split around the first conic; surface env wins over
  the after-conic Descent default; `ResolveEnvPhaseForWindow` last-overlapping-section + null-list
  tolerance; `IsArrivalConic` approach-env short-circuit (Theory, 2 cases) / destination-park /
  departure-body-conic rejected.
- `RenderParityOracleTests.cs`: `ComputeDrift` with the reference point at a segment INTERIOR (not a
  vertex) measures ~0 (point-to-POLYLINE), catching a point-to-VERTEX rewrite that would false-fire
  ("green but blind").
- `FailClosedClassifierTests.cs`: Kerbin->Mun->Minmus siblings -> `NestedSoi`, `RootBody=="Kerbin"`,
  `FaithfulFallback` (stock-system nested-SOI, proves not Jool-hardcoded).
- `CrossMemberSeamStitcherTests.cs`: descent seam head EXACTLY at descentEnd still renders, one tick
  past retires (Theory, 2 cases) - the off-by-one boundary guard.
- `AnchorFrameTests.cs`: 1-sample primary + degenerate (start>end) secondary -> `Retire`, not a
  spurious `AnchorLocalSecondary` match.

### Gameplay case dispositioned NOT self-containable (with evidence)

The HoldPhase warp-through PRODUCER stays vacuous-under-flag-ON: the factory constructs no live
HoldPhase (only `MovingTargetStationApproach`, which is fail-closed / define-only); fabricating a
producer would be new geometry, out of test-only scope. The existing
`WarpThroughInteriorGapSpineInGameTest` already documents this with factory evidence and asserts the
observable interior-gap-hold equivalent, so no new HoldPhase test was fabricated. Anything needing a
real re-fly merge / live re-aim mission plan / actual ProtoVessel lifecycle transition belongs in the
manual Ctrl+Shift+T runbook.

### P2 runbook note - the one intentional cold-pad skip

A full launch-pad Ctrl+Shift+T MapRender sweep now runs and asserts every method EXCEPT exactly ONE:
`RenderParityBaselineTest.ParityBaseline_KnownGoodFaithfulGhost_ZeroDrift_TrackingStation`
(Category `GhostMap`, `Scene=GameScenes.TRACKSTATION`), which is intentional per design 11.5 - it
exercises the TRACKSTATION variant of the parity baseline and therefore cannot run from a FLIGHT-scene
sweep. It MUST be signed off separately from the Tracking Station view (Ctrl+Shift+T in TRACKSTATION).
No in-repo cutover sign-off runbook file exists; this one-line TS-view caveat is recorded here in the
migration plan (the in-game playtest sign-off home, see sections 0 and 12) and should be carried into
the umbrella cutover sign-off doc when one is written.

### Flag-OFF / tracing-OFF byte-identical

No producer geometry or draw code was touched - only test files. Flag-OFF
(`MapRenderPhaseSpineDrive` default false) and tracing-OFF (`mapRenderTracing` default off) normal play
is byte-identical to today; every source gate stays green. Because this is test-only / additive with no
user-visible default-play change, there is NO CHANGELOG / todo entry.

---

## 11. Phase ordering & dependencies

```
Phase 0 (oracle + regression set + tracer skeleton)   ← BLOCKS EVERYTHING
  └─ Phase 1 (pure types)            ← additive, can overlap Phase 0 tail
       └─ Phase 2 (factory shadow + byte-parity)
            └─ Phase 3 (spine swap)  ⚠️ HIGH RISK — playtest sign-off
                 └─ Phase 4 (re-home surfaces: 4a TracedPath, then 4b markers)
                      └─ Phase 5a (delete 430-line line-visibility cascade)
                           └─ Phase 6 (descent re-stitch — re-homes the deorbit clock)
                                ├─ Phase 5b (delete polyline Driver ownership walk)  ⚠️ HARD-depends on Phase 6
                                ├─ Phase 7 (fail-closed producers)
                                └─ Phase 8 (unwire the old-truth reconciler; finalize tracer)
```

**Execution order: 0 → 1 → 2 → 3 → 4 → 5a → 6 → 5b → (7, 8).** Phases 0–5a are strictly serial (each
gates the next on the parity oracle). **Phase 5b HARD-depends on Phase 6** — the polyline Driver
ownership walk hosts the descent deorbit-head clock that Phase 6 re-homes, so 5b must wait for 6 (the
single non-obvious cross-phase coupling; deleting earlier opens a looped-landing regression window).
Phases 7 and 8 are independent of each other and of 5b (separate parallel PRs after Phase 6). Phase 1 may
begin while Phase 0's regression set is being finalized (it's pure, additive).

## 12. Cross-cutting requirements (every PR)

- **Worktree discipline:** each phase is its own dedicated sibling worktree off the latest `origin/main`
  (or stacked on the prior phase's branch if not yet merged); land via PR, never a local merge into the
  main checkout.
- **Docs per commit:** CHANGELOG (user-facing, only when behavior changes — most phases are internal/no
  visible change until Phase 4+), `docs/dev/todo-and-known-bugs.md` (mark the descent/sub-surface bug
  fixed in Phase 6), and the architecture doc if a contract shifts.
- **Grep gates for every deletion** (the existing `scripts/grep-audit-*.ps1` + xUnit pattern).
- **The parity oracle is the universal merge gate** for every phase from 3 onward, run over the full
  regression set, plus an explicit in-game playtest sign-off for the two HIGH-RISK phases (3 and 5).

## 13. What this plan explicitly does NOT do (deferred — design §17)

The full `MissionComposite`; the cross-SOI whole-patched-conic-chain synthesis; the recursive Jool /
moving-target station *producers*; the flight-mesh fold-in (a third `ISceneAdapter`); and the substrate
retype. Each is a separate future effort; this plan only ensures their *types* exist and fail closed.
