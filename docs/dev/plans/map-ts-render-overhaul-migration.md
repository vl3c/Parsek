# Migration Plan: Unified Trajectory & Map/TS Render Architecture

> **STATUS: CURRENT plan.** The detailed, phased, parity-gated implementation sequence for
> [`../design-map-ts-render-architecture.md`](../design-map-ts-render-architecture.md) (the unified
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
