# Re-aim Cross-SOI Transfer Seam Investigation

## Problem statement

When the looped "Duna One" mission is re-aimed to a shifted launch window, the re-aimed ghost's
interplanetary transfer arc visibly does not connect to the recorded Kerbin-escape and Duna-capture
legs: the ghost teleports about 62 degrees at the Kerbin->Sun departure handoff and about 62 degrees
again at the Sun->Duna arrival handoff. Re-aim ENGAGED cleanly (it did not decline to faithful), and
the heliocentric Lambert solved a sane prograde transfer, so the defect is not a solver failure. It is
a stitching defect at the two SOI body-change handoffs: the re-aim system substitutes ONLY the
heliocentric coast with a fresh center-to-center Lambert, then splices that re-oriented, reshaped arc
onto the recorded escape/capture legs (which replay verbatim at their original inertial directions and
end at the SOI boundary). The handoff that the original design assumed would be "sub-pixel at the body
centre" is instead a full-SOI-radius position discontinuity plus an orientation mismatch.

## Evidence

All log line numbers are in
`C:/Users/vlad3/Documents/Code/Parsek/logs/2026-06-15_1906_duna-mission-investigation/KSP.log`.
All file:line references are under
`C:/Users/vlad3/Documents/Code/Parsek/Parsek/Source/Parsek/`.

Seam jumps (the visible symptom):

- `KSP.log:21896` "SEAM member=30 seg#2->3 body=Sun loopUT 64042932->64044932 jump=87759936m". The
  Kerbin SOI radius is 84,159,286 m, so the Kerbin->Sun departure jump is about 1.043x SOI. Treated as
  a chord across the SOI sphere this is a central angle of about 62.85 deg
  (2*84159286*sin(62.85/2) = 87,758,276 m, under 0.005% from the logged jump).
- `KSP.log:32175` "SEAM member=30 seg#3->4 body=Duna ... jump=49193460m". Duna SOI radius is
  47,921,949 m, so the Sun->Duna arrival jump is about 1.027x SOI, a central angle of about 61.76 deg.
- The two seam angles agree to within about 1.09 deg, which would by itself suggest a near-rigid
  rotation. The magnitude evidence below shows the jump is dominated by a radial endpoint gap, not the
  rotation.

Endpoint geometry (the dominant source, proven by the seam trace):

- `KSP.log:21349` synth geometry (proximity): "sma=17158186084.79 ecc=0.2416 | xfer-vs-Kerbin@depart=0m
  | xfer-vs-Duna@arrival=0m". The fresh Lambert passes exactly through Kerbin CENTER at departure and
  Duna CENTER at arrival by construction.
- `KSP.log:21894` (the frame just BEFORE the departure seam): "FLIGHT covering-segment CHANGED:
  member=30 effUT=64004932 body=Kerbin ... seg=[63966985.6,64044032.7] sma=-3818300 ecc=1.192". The
  recorded Kerbin-escape hyperbola covers up to UT 64044032.7 and ends at the Kerbin SOI boundary
  (about 84 Mm from Kerbin center).
- `KSP.log:21897` (the frame just AFTER the seam): "FLIGHT covering-segment CHANGED: member=30
  effUT=64044932 body=Sun ... seg=[64044032.7,70898646.1] sma=17158186085 ecc=0.242". The synth Sun
  transfer takes over starting at 64044032.7 (the recorded departure UT), where it sits at Kerbin
  center.
- So at the seam UT the recorded escape leg is at the SOI shell (~84 Mm out) while the full-span synth
  transfer is at Kerbin center. About 96% of the 87.76 Mm jump is this radial center-vs-SOI gap; the
  remaining ~4% is the orientation/asymptote mismatch. The 62 deg "chord across the SOI" framing is an
  artifact of treating both endpoints as if they sat on the SOI sphere, which the shipped full-span
  render does not do (one endpoint is at the planet center).
- `KSP.log:21946` confirms the full-span placement: a bit later on the transfer (seg#3, loopUT
  64180932) "dist[Kerbin]=211007760m"; the synth arc only reaches the SOI distance scale well after the
  seam UT, consistent with starting from Kerbin center.

Shape mismatch (a secondary, smaller contributor):

- Recorded heliocentric transfer: `KSP.log:13186` "seg#9 Sun [64044033,65004887] sma=17604964389.77
  period=13555238". Recomputing 2*pi*sqrt(sma^3/mu_sun) reproduces 13,555,238 s exactly, so the
  recorded sma is trustworthy ground truth.
- Fresh Lambert sma = 17,158,186,085 (proximity, `KSP.log:21349`) and 17,523,930,023 (patched-conic,
  `KSP.log:15753`) vs recorded 17,604,964,390: about 2.5% / ~447 Mm smaller for the SAME reused
  tof = 6,854,613 s. The fresh ecc (0.2416) also differs from the recorded Sun-leg ecc (0.228, per the
  Anchor-leg line `KSP.log:21934` before=seg9 ecc=0.228). Same-tof / different-endpoints yields a
  different ellipse.

Engaged parameters and window phase:

- `KSP.log:13201` "ENGAGED re-aim Kerbin->Duna via Sun; D0=142619013.38 synodic=19653075.77
  tof=6854613.33 anchor=131152023.18 ... loiterCuts=1". (D0-anchor)/synodic = 0.5835, a FRACTIONAL
  synodic, so the Kerbin-Duna relative geometry does not repeat at the rendered window; the recorded
  escape/capture asymptotes were aimed at the planets' OLD longitudes.
- `KSP.log:13200` PAD-ALIGN launchShift = -7808.3 s sweeps Kerbin only a fraction of a degree
  heliocentric; it is negligible vs the seam and is not the cause.

Recorded SOI legs are real recorded segments that replay verbatim:

- `KSP.log:13185` "seg#8 Kerbin [63966986,64044033] sma=-3818299.68" (Kerbin-escape hyperbola, ecc>1).
- `KSP.log:13189` "seg#12 Duna [70898646,70912684] sma=-563351.21" (Duna-capture hyperbola).
- Note: the design doc claims S1 ejection is "NOT a recorded segment"
  (`Parsek/docs/dev/done/plans/reaim-interplanetary-transfers.md:13,118`), but this mission's on-rails
  recording DOES carry a real Kerbin-escape hyperbola and a Duna-capture hyperbola. The shipped re-aim
  replays those verbatim and never synthesizes S1; that is the actual stitching gap. (Doc/code mismatch
  worth recording.)

Source confirms the mechanism and the deliberate full-span render:

- `Reaim/ReaimTransferSynthesizer.cs:137-138`: r1 = launchBody center, r2 = targetBody center (the
  Lambert is center-to-center). `:163-166`: the prograde branch is chosen via the launch-plane normal;
  the solver has no v-infinity input from the recorded escape/capture legs. `:166` discards v2
  ("out _").
- `Reaim/ReaimPlaybackResolver.cs:232-247`: the transfer is rendered over the FULL recorded span with
  `double.NaN, double.NaN` render bounds. The comment documents that an earlier pass DID trim the
  launch side to the synthesized SOI-exit UT and it "opened a gap right after the launch SOI exit where
  the orbit ghost was destroyed (gap-between-orbit-segments) and the transfer line restarted displaced
  by the launch body's own motion" - that trim was REVERTED.
- `Reaim/ReaimSegmentAssembler.cs:91-104`: ReplaceHeliocentricLeg renders the center-to-center transfer
  over the full span on the stated assumption that "the brief in-SOI stub sits sub-pixel at the body
  centre" and "the ghost is hidden in the brief handoff gap." False for interplanetary SOI radii of
  tens of Mm.

Existing seam-bridge is doubly out of scope:

- `Display/GhostTrajectoryPolylineRenderer.cs:1118` BridgeMaxAngleRadians = 0.785398 (45 deg),
  `:1125` BridgeMaxSeamGapSeconds = 120.0, `:1272` IsBridgeAdjacentConic hard-returns false unless
  `segBodyName == legBodyName` (Ordinal). These are same-body and <= 45 deg; the observed seams are
  body changes (Kerbin->Sun, Sun->Duna) at ~62 deg, doubly out of scope.

The ghost vessel transform (not just the polyline) teleports:

- `ParsekFlight.cs:22379-22390`: PositionGhostFromOrbit places the on-rails ghost from the OrbitSegment
  Kepler elements; the ReaimSeam discontinuity line reads `state.ghost.transform.position`. The stock
  map icon and the proto orbit line both ride that transform, so any render-only polyline bridge cannot
  move the icon.

## Pinned root cause

Confidence: HIGH. Classification: SYSTEMATIC and DESIGN-DEFERRED, not inherent.

The ~62 deg cross-SOI seam is a designed-in stitching defect at the two SOI body-change handoffs, not a
Lambert/re-aim reliability limit. Re-aim substitutes ONLY the heliocentric coast with a FRESH
center-to-center Lambert (`ReaimTransferSynthesizer.TrySynthesizeTransfer`: r1 = launch-body center,
r2 = target-body center), reusing the recorded time-of-flight, and replays the recorded Kerbin-escape
and Duna-capture hyperbolae verbatim at their original inertial asymptote directions and endpoints. The
defect has two superimposed systematic sources:

1. ENDPOINT (dominant, about 96% of the jump): the Lambert endpoints are at planet CENTERS, but the
   transfer is rendered FULL-SPAN (`ReaimPlaybackResolver.cs:244-247` passes NaN render bounds). At the
   seam UT the synth transfer is at the body center while the recorded escape/capture legs end at the
   SOI boundary. The jump magnitude is therefore approximately one SOI radius at each seam
   (87.76 Mm = 1.043x Kerbin SOI; 49.19 Mm = 1.027x Duna SOI), proven by the covering-segment trace
   (`KSP.log:21894` -> `21897`).

2. ORIENTATION (secondary, about 4% plus the shape gap): the fresh Lambert departs/arrives in different
   inertial directions than the recorded asymptotes (no v-infinity awareness), and it is a different
   shape (2.5% smaller sma, different ecc) because it connects different endpoints over the same tof.
   At the fractional 0.5835 synodic window the relative geometry does not repeat, so the recorded
   asymptotes point at the planets' old longitudes.

The full-span render at planet centers is the reason the gap is large rather than sub-pixel. The
original design (`reaim-interplanetary-transfers.md:252-279`) explicitly accepted the orientation
residual as "the accepted small seam," shipped only PadAlignLaunch, and tracked both the
"minimally rotate the ejection arc toward v1" polish (`:256-261`) and the SOI-handoff teleports
(`:359-361`, "option 3: re-plan the whole patched-conic chain") as SEPARATE DEFERRED work. None of
those have been built. The same-body 45 deg / 120 s seam-bridge cannot cover a body-change seam by
construction.

Inherent vs systematic verdict: there is a tiny genuinely-inherent residual (the recorded
escape/capture v-infinity was sized for the old window, and matching it perfectly while reaching the
moved target over the recorded tof is over-constrained), but it is sub-degree and dwarfed by the two
systematic sources above. The visible 62 deg seam is fixable.

## Candidate directions

Note: the candidate descriptions below carried an initial "rigid rotation dominates" framing. The seam
trace evidence (`KSP.log:21894` -> `21897`) overturns that: the jump is dominated by the radial
center-vs-SOI ENDPOINT gap, not rotation. Each adversarial verdict below reflects that correction.

### A. Re-phase / rotate the RECORDED heliocentric transfer to the new epoch

Mechanism: instead of a fresh center-to-center Lambert, transform the recorded Sun-leg Kepler elements
(seg#9 sma=17.605 Gm, ecc=0.228, inc/LAN/argPe) by a rigid rotation about the Sun so the recorded
SOI-exit/entry states line up with the new launch/target positions, and apply the same rotation to the
recorded escape/capture legs so all three pieces rotate as one rigid body, preserving the recorded
v-infinity at both handoffs.

- Scope: geometry-fix. Addresses root cause: partially (orientation, not the dominant endpoint gap).
- Pros: preserves the recorded transfer shape (eliminates the 2.5% sma / ecc gap); no new solver
  (MIT-clean, pure rotation of existing Kepler elements); in-memory loop-only transform of copied
  structs (immutability-safe); this is the design's own deferred "minimally rotate toward v1" idea.
- Cons: a single Sun-frame rigid rotation cannot rotate a Kerbin-relative escape hyperbola and a
  Duna-relative capture hyperbola at once (they live in different body frames); at a fractional synodic
  the required transfer angle differs, so one rotation cannot satisfy both endpoints; over-constrained
  (two endpoint constraints, three rotation DOF + epoch); reintroduces the rejected on-camera low-orbit
  plane-change kink if the bookkeeping is wrong.
- Risk: medium-high. Effort: large.
- Files: `Reaim/ReaimTransferSynthesizer.cs`, `Reaim/ReaimPlaybackResolver.cs`, `Reaim/ReaimClassifier.cs`,
  `Reaim/ReaimSegmentAssembler.cs`, `Parsek.Tests`.
- Adversarial verdict: NOT VIABLE. The rigid-rotation premise is geometrically false: the recorded
  escape leg is body=Kerbin and the capture leg is body=Duna; only the middle leg is heliocentric, so a
  single Sun-frame rotation cannot rotate all three at once. At the fractional 0.58 synodic window one
  rotation cannot close both seams. It also does not touch the dominant endpoint (center-vs-SOI) gap,
  so it cannot close either seam in magnitude. Blockers: body-relative legs cannot share one Sun-frame
  rotation; over-constrained at fractional synodic; re-introduces the rejected low-orbit kink; heaviest
  collision with in-flight reaim branches.

### B. Anchored / shooting solve: SOI-boundary endpoints AND recorded asymptote directions

Mechanism: change the Lambert endpoints from planet centers to the recorded legs' SOI-crossing
positions (re-phased to the new body positions), then iterate (shooting) on tof and/or a small
departure-state adjustment so the solved departure/arrival velocity directions match the recorded
escape/capture v-infinity directions.

- Scope: geometry-fix. Addresses root cause: aims at both sources but is over-determined.
- Pros: most physically correct in principle (C0 position + C1 tangent at both seams); removes the
  center-vs-SOI endpoint error directly; reuses the existing UvLambert seam (MIT-clean); in-memory,
  immutability-safe.
- Cons: a single-rev Lambert is fully determined by (mu, r1, r2, tof); fixing r1 and r2 at the SOI
  boundaries leaves exactly ONE free scalar (tof), which cannot satisfy two 3D asymptote-direction
  constraints; the shooting loop has no exact solution and must relax a constraint; iterative per-window
  solve is heavier and multiplies across many ghosts (against the visual-efficiency principle); highest
  regression risk; the SOI-boundary-endpoint sub-fix combined with the existing full-span render
  re-triggers the already-reverted trim/gap regression.
- Risk: high. Effort: large.
- Files: `Reaim/ReaimTransferSynthesizer.cs`, `Reaim/UvLambert.cs` (or an outer iteration),
  `Reaim/ReaimPlaybackResolver.cs`, `Reaim/ReaimClassifier.cs`, `Parsek.Tests`.
- Adversarial verdict: NOT VIABLE. Over-determined by construction: r1, r2 fixed at SOI-boundary points
  leaves only tof free in a single-rev Lambert (UvLambert is single-rev, no multi-rev DOF); one scalar
  cannot match two 3D asymptote directions, so it closes at most one seam. Its endpoint sub-fix
  (SOI-boundary endpoints) combined with the deliberate full-span render reopens the reverted
  "transfer in the wrong place after Kerbin SOI exit" gap regression (`ReaimPlaybackResolver.cs:232-243`).
  It inverts the design's sanctioned direction (which matched only the departure asymptote by rotating
  the recorded ejection RENDER toward v1, accepting the arrival seam). Collides with the in-flight
  near-180 handedness fix in TrySynthesizeTransfer.

### C. Render-only rotation of the recorded escape/capture legs onto the fresh Lambert asymptotes

Mechanism: keep the fresh center-to-center Lambert, but rotate the recorded escape leg about the launch
body so its outbound asymptote aligns with the fresh transfer's departure velocity, rotate the recorded
capture leg about the target body to align its inbound asymptote with the arrival velocity, and trim the
transfer render to the SOI-boundary UTs.

- Scope: hybrid (render rotation of recorded legs). Addresses root cause: claims to, but misdiagnoses
  the dominant source.
- Pros: leaves the heliocentric solver untouched (low risk to validated Lambert work); rotations are
  pure in-memory transforms of copied structs (immutability-safe, MIT-clean); the required rotation is
  readable from the already-computed v1/v2.
- Cons: cosmetically aligns tangents but the escape/capture hyperbolae no longer point where the vessel
  actually departed/arrived (physically dishonest); two independent rotations can drift the body-end
  ascent/descent off the recorded surface track (new surface seams); still uses the 2.5%-smaller-sma
  fresh shape; per-window re-derivation cost.
- Risk: medium. Effort: medium.
- Files: `Reaim/ReaimSegmentAssembler.cs`, `Reaim/ReaimPlaybackResolver.cs`,
  `Reaim/ReaimTransferSynthesizer.cs` (expose v1/v2), `Parsek.Tests`.
- Adversarial verdict: NOT VIABLE. It closes NEITHER seam because it misdiagnoses the dominant source.
  The jump (87.76 Mm = 1.043x Kerbin SOI; 49.19 Mm = 1.027x Duna SOI) is an ENDPOINT (radial
  center-vs-SOI) gap, proven by the covering-segment trace (recorded leg at the SOI boundary, full-span
  fresh transfer at the planet center at the seam UT). Rotating the recorded legs about their bodies
  only re-aims the asymptote (~4% of the jump) and changes WHERE on the SOI sphere the leg endpoint
  sits; it cannot move the fresh transfer's seam-UT position from the planet center out to the SOI
  boundary. The only way rotation could help is paired with trimming the render to the SOI-boundary UTs,
  which is the documented, already-reverted gap regression (`ReaimPlaybackResolver.cs:232-243`), and
  even then the center-to-center Lambert's SOI-piercing POSITION still does not match the recorded leg's
  SOI-exit position. Rotating the escape leg about Kerbin also drags its low-orbit / ascent handoff off
  the recorded ascent, re-introducing the rejected low-orbit plane-change kink.

### D. Render-only cross-SOI seam bridge (generalize the same-body bridge to body changes)

Mechanism: drop the same-body Ordinal gate and the 45 deg cap for the re-aim escape->transfer and
transfer->capture handoffs, and draw a smooth interpolating connector across the 62 deg gap in the
appropriate body frame.

- Scope: render-only. Addresses root cause: no (masks the line symptom only).
- Pros: localized to the renderer; does not touch the solver or recorded data; immutability-safe,
  MIT-clean; reuses a tested mechanism; cheap to gate.
- Cons: masks the symptom; the ghost ICON and proto-orbit still teleport (they ride the ghost
  transform, not the polyline); a 62 deg fabricated arc across a body change is exactly the "wild
  spiral" the 45 deg cap was added to avoid; risks the planet-intersection regression already on record;
  the connector endpoints come from the same teleporting OrbitSegments.
- Risk: medium. Effort: medium (under-estimated; likely a second iteration when the icon still jumps).
- Files: `Display/GhostTrajectoryPolylineRenderer.cs`, `Parsek.Tests`.
- Adversarial verdict: NOT VIABLE. The reported defect is a ghost-vessel transform teleport that the
  icon and proto-orbit line both ride (`ParsekFlight.cs:22379-22390` PositionGhostFromOrbit; the
  ReaimSeam line reads `state.ghost.transform.position`), which a render-only polyline bridge cannot
  touch. Even restricted to the polyline, the existing bridge is structurally inapplicable
  (`IsBridgeAdjacentConic:1272` requires same body; `TryBuildSeamBridgeLocalPoints` unwinds a single
  rotation axis for a spin gap), and a cross-SOI body-change connector with combined rotation + 2.5%
  shape difference + full-SOI-radius endpoint mismatch is the wild-spiral case the bridge was gated
  against, with a planet-intersection regression on record (MEMORY project_reaim_no_extend_transfer_into_soi).
  Would change the verdict: if the icon and proto-orbit were proven decoupled from the ghost transform
  AND both body-change seams drew without planet intersection, this could become a viable-with-caveats
  cosmetic on top of a real geometry fix.

### E. Accept the gap: cleanly clip the line and suppress the ghost across the handoff

Mechanism: trim the rendered transfer to the SOI-boundary UTs (the render-start/render-end params,
currently NaN) and explicitly clip/hide the ghost line and icon during the in-SOI handoff window at each
seam, replacing the teleport with an intentional clean gap.

- Scope: render-only. Addresses root cause: no.
- Pros: lowest effort and risk; makes the design's stated fallback intent explicit; no solver or
  recorded-data change; cannot regress the validated solve path; trivially revertable; matches the
  bridge author's "an honest gap reads better than a wild spiral."
- Cons: does not make the trajectory accurate (the user's actual complaint); the residual clean gap is
  still ~62 deg / tens of Mm at map scale; the icon must be suppressed across the gap or it still
  teleports, and suppressing it can read as the ghost vanishing; trimming is the exact already-reverted
  gap-regression path; the map proto vessel is destroyed/recreated across the gap (warp-reseed-lag /
  line-blink).
- Risk: low (process-blocker only). Effort: small.
- Files (corrected): the re-aimed heliocentric leg renders through the PROTO orbit-line / map-render
  pipeline (`ReaimedTrajectory` exposes the transfer as an OrbitSegment; Points/TrackSections empty by
  design), NOT `GhostTrajectoryPolylineRenderer` (atmospheric/non-orbital only). Icon/line behavior
  lives in `ParsekFlight.PositionGhostFromOrbit` (SetActive(false) when no segment covers the UT),
  `GhostMapPresence` (gap-between-orbit-segments removal), `Patches/GhostOrbitLinePatch.cs`, plus the
  SOI-boundary UTs from `Reaim/ReaimPlaybackResolver.cs` (pass real render bounds instead of NaN) and
  `Reaim/ReaimSegmentAssembler.cs`.
- Adversarial verdict: VIABLE WITH CAVEATS, as a stopgap only. It does not close either seam; it only
  changes the teleport into a clean gap (the residual gap is ~62 deg / tens of Mm, same scale as the
  teleport). It re-litigates a settled decision (the reverted trim, `ReaimPlaybackResolver.cs:232-243`),
  so it carries a process blocker and re-introduces the destroyed-orbit-ghost gap regression unless the
  proto destroy/recreate is explicitly accepted. The filesAffected as originally specified (pointing at
  GhostTrajectoryPolylineRenderer) is wrong; edits there are dead for this leg. Defensible only as an
  interim "honest discontinuity" ship while the real geometry fix is built, and must be coordinated with
  the in-flight `fix-soi-trajectory-seam-coverage` progressive-fill branch and PR #1155
  (reaim-dest-loiter-retimer) which both touch this region.

## Ranked recommendation

The adversarial pass rejected all four geometry/render candidates as specified (A, B, C, D NOT VIABLE)
and rated only E viable-with-caveats. The honest conclusion is that NONE of the five as-specified
candidates is a correct, low-risk geometry fix, and two of them (B's and C's trim sub-fix) reopen an
already-reverted regression. The ranking therefore is:

1. The design's deferred "option 3: re-plan the whole patched-conic chain"
   (`reaim-interplanetary-transfers.md:359-361`) is the only direction that actually closes both seams
   at their dominant (endpoint) source. Mechanism: synthesize the WHOLE patched-conic chain from one
   fresh solve so the escape hyperbola's SOI-exit STATE matches the heliocentric departure v1 and the
   capture hyperbola's SOI-entry STATE matches the arrival v2 (the symmetric arrival blend), instead of
   splicing a fresh heliocentric arc onto verbatim recorded SOI legs. All three legs then share one
   solve and meet at the same SOI-sphere position with continuous velocity. This is large and touches
   the synthesizer + assembler, but it is the only approach that is geometrically sound at a fractional
   synodic. It is immutability-safe (in-memory loop-only) and MIT-clean (no new solver; reuses the
   already-solved Lambert v1/v2 to size the synthesized hyperbolae).

2. E (accept-the-gap clip) as an explicitly-labeled interim ship while option 3 is built, ONLY if the
   user confirms a clean gap is acceptable and the team coordinates with the in-flight
   `fix-soi-trajectory-seam-coverage` branch. Not a fix for "the transfer did not look accurate."

Rationale: the seam is systematic and design-deferred, not inherent, so a correct fix exists. But every
shortcut (rotate-recorded, anchored-shooting, render-bridge, trim) either misses the dominant endpoint
gap, is over-constrained, masks a symptom the icon still exhibits, or reopens a reverted regression. The
correct fix re-derives the SOI legs from the same fresh solve so position AND velocity match at both
SOI spheres.

Open questions that must be resolved BEFORE any code is written:

- What exactly is the requirement? Pin it against this one concrete case with the user: "C1-continuous
  line through the SOI" (which for a recorded mission re-aimed to a moved target may be impossible to
  satisfy exactly while keeping recorded surface tracks) vs "the seam must not look broken at playback
  scale" (achievable). Do NOT guess-rewrite before this is pinned.
- For option 3: synthesizing the escape/capture hyperbolae to match v1/v2 changes where the body-end
  (ascent/descent / parking orbit) connects. Does re-deriving the ejection re-introduce a low-orbit
  plane-change kink (the exact artifact the original design avoided by accepting the SOI seam)? The
  design's "rotate the parking-orbit LAN to contain v_inf and re-time the ascent" note (`:260-261`) is
  the relevant deferred polish.
- How wide is the residual gap (if any) for option 3 at a fractional synodic, and is there a remaining
  inherent residual when the recorded v-infinity magnitude cannot simultaneously match the new transfer
  energy and reach the moved target over the recorded tof?
- Per-frame cost: option 3 runs once per (member, window) and is cached, but verify it does not dent
  frame time across many simultaneous re-aimed ghosts.
- In-flight coordination: `reaim-lambert-reliability` (near-180 handedness + eccentric tof),
  `reaim-eccentric-tof`, `reaim-resolver-reliability`, `reaim-dest-loiter-retimer` (PR #1155), and
  `fix-soi-trajectory-seam-coverage` all touch the synthesizer/resolver/orbit-line region. Sequence
  option 3 after they land to avoid stacked re-aim rewrites (the history of which went net-negative).

## Do NOT do

- Do NOT rewrite, finalize, or load-time-modify any recorded data (.prec, OrbitSegments, recorded
  points) on any path. The re-aim substitution must stay in-memory and loop-only on copied structs, as
  `ReaimSegmentAssembler` already does.
- Do NOT vendor a GPL/LGPL/AGPL solver (MechJeb-repo GPLv3, pykep, lamberthub, Vallado/CelesTrak).
  Parsek is MIT. The only sanctioned vendor is MechJebLib's per-file permissive-SPDX Izzo/Gooding behind
  ITransferSolver, gated on verifying the SPDX header on the exact copied commit, and that is a deferred
  contingency, not a planned stage. The recommended option 3 needs NO new solver.
- Do NOT auto-extend the heliocentric transfer's draw/icon window over the SOI escape/capture window.
  This was tried and reverted ("broke everything", MEMORY project_reaim_no_extend_transfer_into_soi): it
  puts the ghost behind the planet inside the SOI.
- Do NOT stack a fix on a state that is worse than the current baseline. The current single honest kink
  is a known baseline; revert-on-regression discipline applies (prior re-aim rewrites went net-negative,
  ~20M tokens). A non-converging or wrong-branch solve renders worse than the current kink.
- Do NOT begin coding before the requirement is pinned with the user against this one concrete case
  (per MEMORY feedback_dont_over_iterate_underspecified). No guess-rewrite churn.

## How to validate the chosen fix

Re-run the same looped Duna One re-aim playtest and collect a fresh log
(`python scripts/collect-logs.py reaim-seam-fix`). The fix is working when, for the re-aim member
(member=30):

- The `[ReaimSeam] SEAM member=... seg#X->Y body=... jump=...m` lines at the Kerbin->Sun and Sun->Duna
  handoffs drop from ~87.76 Mm and ~49.19 Mm (about one SOI radius) to a small value (sub-SOI, ideally
  a few hundred km or less, the scale of the in-SOI handoffs already tolerated, e.g. the Ike/Duna seams
  that already log ~0.3-2.6 Mm).
- The `synth geometry` line still shows a sane elliptic transfer (0 <= ecc < 1, positive finite sma),
  and the seam reduction is NOT achieved by declining re-aim (the ENGAGED line must still appear).
- No new "gap-between-orbit-segments" / orbit-ghost-destroyed warnings appear at the launch SOI exit
  (the reverted-trim regression must not return).
- The ghost ICON visibly follows the line through both handoffs in-game (the transform teleport is
  gone), not just the polyline.
- Filter the seam counts by the failing UT and body before trusting them (MEMORY
  feedback_anchor_on_duty_cycle_not_single_line): confirm the reduced jumps are at the Kerbin->Sun and
  Sun->Duna seam UTs, not an unrelated frame.
