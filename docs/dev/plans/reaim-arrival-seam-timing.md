# Re-aim Arrival Seam via SOI-entry Timing - Design + Plan

Status: PLAN (pre-implementation, revised after a clean-Opus design review that corrected the search method and the residual budget). Branch `reaim-arrival-timing` off `main` (after #982).
Supersedes the closed PR #983 (ROTATE-ALL), which moved the destination and was rejected.

## 0. The hard requirement (from the owner)

The system renders the EXACT recorded maneuvers to the EXACT destination. A looped supply-route ghost must visibly arrive at the exact base / station / landing site where the resource transfer happens, because the recorded arrival + descent (or rendezvous + dock) is recorded RELATIVE to that exact destination. The recorded destination leg must NOT be deformed, rotated, or relocated. Alignment from re-aiming may only be spent on (a) SOI-entry timing and (b) extending recorded loiter orbits before descent (where they exist). Direct-approach-then-land recordings have no loiter buffer (edge case).

## 1. What is broken (and what is NOT) - confirmed by code review

NOT broken: the re-aim already LANDS at the exact recorded spot. `ReaimSegmentAssembler.ReplaceHeliocentricLeg` replaces ONLY the Sun-bodied (heliocentric) segments; every body-relative segment (the recorded arrival hyperbola, capture, loiter, and the body-fixed descent) passes through UNTOUCHED and is reconstructed relative to the LIVE destination body, so it follows the body and lands exactly where recorded. The destination requirement is already met.

Broken (cosmetic, but real): the APPROACH seam. The synthesized transfer arrives at the destination SOI with an arrival velocity that DIFFERS from the recorded approach, so the recorded arrival hyperbola (which expects the recorded v_inf) is spliced on at a different bearing/speed and the ghost visibly jumps at the SOI boundary (~1.37 Gm in the Duna case) and re-approaches. We minimize this WITHOUT touching the recorded destination leg.

Root cause (confirmed): `ReaimTransferSynthesizer.TrySynthesizeTransfer` aims the Lambert at the body CENTER (`r2 = targetBody.orbit.getRelativePositionAtUT(arrivalUT).xzy`), ecliptic-projects it (`r2 = ProjectOntoPlane(r2, launchPlaneNormal)`), and DISCARDS the arrival velocity (`UvLambert.Solve(..., out v1, out _)`). Nothing constrains the arrival velocity to match the recording.

## 2. The approach: best-match the recorded arrival v_inf by timing (shrinks the seam to a floor, does not zero it)

HONEST framing (review CRITICAL-1/2): timing can only REDUCE the seam, not close it, for an eccentric target. The re-aim windows are `RecordedDepartureUT + k*synodic`, and the synodic period is MEAN-anomaly-only. After k periods the bodies return to the same MEAN configuration, but an eccentric target (Duna e~0.05) sits at a different point in its eccentric orbit, so its heliocentric velocity differs and the achievable arrival v_inf at the quantized window differs from the recorded one by a residual (potentially a few degrees and up to a couple hundred m/s vs a recorded |v_inf| ~731 m/s for the Duna case) that timing alone cannot remove. Departure is also pinned (pad-aligned to a whole sidereal day so the recorded ascent connects), so effectively only the tof is free: a 1-D search that converges to a NONZERO floor.

So v1 changes the transfer's OBJECTIVE to best-match the recorded arrival v_inf, shrinking the seam toward that floor, and MEASURES the floor so a playtest tells us if it is visually acceptable. It never deforms the destination, and it declines to faithful when it cannot get close.

The recorded arrival v_inf relative to the destination body is fixed and known:
- direction = the analytic inbound hyperbolic asymptote from the recorded arrival conic's (e, h) (REUSE the #983 `InboundAsymptoteDir`).
- magnitude = `sqrt(mu_body / (-sma_rec))` from the recorded hyperbola's sma (NEW code, not in the #983 salvage).
- entry point `r_soi_rec` = where the recorded hyperbola crosses the SOI sphere inbound (NEW code).

Search method (review-chosen framing B; framing A rejected):
- Keep the per-window Lambert. CAPTURE the arrival velocity `v2` the solve already returns and currently discards (`out _` -> `out Vector3d v2`).
- Change the per-window search OBJECTIVE from "minimize arrival POSITION miss to the body center" to "minimize the arrival v_inf mismatch `|(v2 - V_body) - v_inf_rec|`" (a direction-weighted norm), searching the tof (1-D) around the window (reuse the existing +-6% / 12-step sweep, add a parabolic/golden-section refine). Optionally aim at the recorded entry point `bodyPos + r_soi_rec` rather than the body center to also shrink the position component (minor; the v_inf objective is the main lever).
- KEEP the ecliptic projection (review MAJOR-1): it stabilizes the Lambert plane at the ~180-degree single-rev window (the singularity `UvLambert` hard-bails on). Aiming at the SOI-offset entry point does NOT dodge that singularity (the offset is ~0.1 degree), so do not drop the projection; it only flattens the target endpoint to stabilize the plane and does not have to corrupt the v_inf objective (which is evaluated against the recorded v_inf, not the projected geometry). For the low-inclination v1 (Duna) the out-of-plane distortion is small (design-doc bound: Duna 0.06 deg -> ~2.4e7 m, inside the SOI). A plane-aware solve for inclined targets stays deferred.
- Framing A (construct the transfer from the full recorded arrival STATE and back-propagate to the launch body) is REJECTED: fixing 6 arrival DOF leaves nothing to satisfy "departs from the launch body", so it over-constrains and would decline almost every window.

REFUTED shortcut (review MINOR-4): rigidly rotating the recorded heliocentric leg into the live synodic frame does NOT work and is #983 in disguise. A rigid rotation assumes both bodies advanced by the SAME inertial angle over k*synodic; they advance by `k*synodic/P_launch*360` and `k*synodic/P_target*360`, which DIFFER (that differential is the definition of the synodic period). No single rotation maps the recorded departure->arrival vector onto the live one; applying one moves the arrival off the live body (the #983 landing-site move). So a fresh Lambert is required.

## 3. The residual budget (two DIFFERENT residuals; do not conflate)

- v_inf residual (direction + magnitude): ONLY the SOI-entry timing search shrinks it, to the eccentric-orbit floor. LOITER CANNOT touch it (loiter shifts time/phase, not approach velocity/bearing). If the floor is too large for a window, DECLINE TO FAITHFUL (keep the current cosmetic seam for that window). Never deform the destination.
- arrival-UT / phase residual: where the recording has a capture/parking/loiter orbit before descent, the existing `ReaimLoiterCompressor` (whole-period extension) can absorb a small arrival-UT residual so the descent still lands on the exact site. This is a SEPARATE, follow-up step, GATED on the measured v_inf residual being acceptable (review MINOR-3). It is NOT in the v1 PR.
- direct-approach-land (no recorded loiter): no loiter buffer, so the v_inf floor is the whole budget. These will COMMONLY decline-to-faithful for eccentric targets (set expectation; it is the common outcome, not a rare edge). Fail-closed, never wrong.

Decline tolerance (numeric, review MINOR-2): accept the timed transfer for a window only when the post-fix SOI-edge seam magnitude is BOTH (i) smaller than the faithful (position-targeted) seam AND (ii) below a defined SOI fraction (start at < 0.25 * SOI radius, tune from the logged playtest number). Log both the faithful seam and the post-fix seam so the playtest tunes the threshold. Until tuned, prefer accepting when strictly better than faithful (so v1 can never regress and we collect the numbers).

## 4. v1 scope + implementation

v1 = the objective change + the residual measurement + decline-to-faithful. NO loiter integration (gated follow-up). NO touching the recorded destination leg.

- `Source/Parsek/Reaim/UvLambert.cs`: already returns `v2`; just stop discarding it upstream.
- `Source/Parsek/Reaim/ReaimTransferSynthesizer.cs`: capture `v2` (line ~144 `out _` -> `out Vector3d v2`); keep `r2` center-aim + the ecliptic projection as the plane stabilizer; expose the arrival velocity (and optionally accept the recorded entry-point target) so the resolver can score the v_inf mismatch.
- `Source/Parsek/Reaim/ReaimPlaybackResolver.cs` (`BuildWindowSegments`, the tof loop ~168-204): change the per-window selection objective from position-miss to the arrival-v_inf mismatch `|(v2 - V_body) - v_inf_rec|`; keep departure pinned at the pad-aligned D_k; add a refine step; compute and log the achieved v_inf residual (direction-deg + magnitude) AND the resulting SOI-edge seam magnitude vs the faithful seam; apply the decline-to-faithful gate (fall back to the current position-targeted transfer for that window if not strictly better / over tolerance).
- Recorded arrival v_inf extraction: direction via the salvaged `InboundAsymptoteDir`; magnitude via `sqrt(mu/(-sma_rec))`; entry point `r_soi_rec` (NEW). Build the recorded arrival Orbit from `plan.ArrivalLeg`.
- The recorded arrival + capture + loiter + descent segments: UNTOUCHED. No rotation, no shift of those segments (contrast #983). A regression test pins them byte-identical between the timed path and the faithful path.
- Diagnostics: `ParsekFlight.LogReaimGhostTrace` logs the v_inf residual + the pre/post seam magnitude at the SOI seam.

## 5. Reuse from #983 (cherry-pick the MEASUREMENT math only)

Branch `reaim-arrival-seam` is kept. Port (they measure geometry, they do not move anything):
- `ReaimRotation.InboundAsymptoteDir` + the vector ops (Cross/Dot/Normalize): inbound v_inf DIRECTION from a conic's (e, h).
- The recorded-arrival-frame extraction logic (to get the recorded (e, h)).
NEW code (NOT in the salvage, which is direction-only): the recorded |v_inf| = `sqrt(mu/(-sma_rec))` and the recorded SOI-crossing point `r_soi_rec`.
DROP: `RotationFrameToFrame`, `RotateVector`, `RotateSegmentOrientation`, `RotateBodyRelativeSegments`, the handedness guard, the entire rotation-and-apply path, and the #983 "rotated v_inf matches" canary assertion.

## 6. Tests

Pure (xUnit):
- Recorded v_inf direction (asymptote) + magnitude (`sqrt(mu/(-a))`) extraction from a known recorded hyperbola.
- The arrival-v_inf-mismatch objective: scored correctly; a better-aligned tof scores lower; a worse one higher.
- The decline gate: accepts only when strictly-better-than-faithful AND under tolerance; declines otherwise (returns the faithful transfer).
- Destination-leg byte-identical regression: the recorded arrival/capture/loiter/descent segments are byte-identical between the timed path and the faithful path (the load-bearing guard for the hard requirement; note it passes trivially today since the assembler does not touch them, so it is the NON-regression guard, not the correctness guard).
In-game canary: the timed transfer's arrival v_inf matches the recorded v_inf better than the faithful transfer, and the post-fix SOI-edge seam magnitude + the achieved v_inf residual are LOGGED across windows (the go/no-go number for the loiter follow-up).

## 7. Risks / open questions

- The achievable v_inf residual floor is the make-or-break; it CANNOT be asserted small up front (review CRITICAL-1). v1 measures it; the loiter follow-up and any tolerance tuning are gated on the measured number from a playtest.
- Departure and arrival seams are coupled through the single departure DOF (pad-aligned). v1 closes the departure seam (pad-align, unchanged) and only MINIMIZES the arrival seam to the floor; they do not both close at the recurrence (review MAJOR-2).
- |v_inf| magnitude residual: a speed mismatch makes the transfer hand off into the recorded hyperbola at a slightly wrong speed (a velocity kink at the SOI edge); loiter cannot fix it; it is part of the floor.
- Keep the ecliptic projection (review MAJOR-1); dropping it reintroduces the 180-degree Lambert singularity at exactly these windows.
- Direct-approach-land eccentric-target missions will COMMONLY decline-to-faithful (review MAJOR-3). Fail-closed, expected.
- Not playtestable during this build run; the pure math + the byte-identical-destination regression de-risk correctness; the seam-shrink magnitude is playtest-measured.

## 8. Phases (v1 PR)

1. Cherry-pick the #983 measurement math (asymptote + vector ops) into this branch; strip all rotation.
2. Recorded v_inf extraction (direction + magnitude + entry point) + the arrival-v_inf-mismatch objective, pure + xUnit-tested.
3. Capture `v2`; change the resolver's per-window objective to the v_inf mismatch (departure stays pad-aligned); decline-to-faithful gate; residual + seam-magnitude diagnostics. Destination leg untouched + the byte-identical regression test.
4. Canary that LOGS the achieved v_inf residual + seam magnitude across windows (the go/no-go for loiter) + docs (CHANGELOG / todo / re-aim design doc).
5. Clean Opus review -> fix -> build/deploy-verify/test green -> open PR (ready-for-playtest; the residual floor + the gated loiter follow-up + the decline-to-faithful fail-safe flagged).

Loiter integration for the arrival-UT residual is a SEPARATE follow-up, gated on the v1 playtest residual being acceptable.

## 9. The contract, restated

The destination leg (arrival hyperbola + capture + recorded loiter + descent / rendezvous + dock) is SACRED and replays verbatim relative to the exact destination, byte-identical to faithful. Re-aim alignment is spent ONLY on transfer timing (the arrival-v_inf objective) and, in a gated follow-up, minimal recorded loiter. If a window cannot align within tolerance, it declines to faithful (the current cosmetic seam) rather than move the destination. No code path in this design relocates the landing/destination.
