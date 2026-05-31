# Re-aim Arrival Seam via SOI-entry Timing - Design + Plan

Status: PLAN (pre-implementation). Branch `reaim-arrival-timing` off `main` (after #982).
Supersedes the closed PR #983 (the ROTATE-ALL approach), which was wrong: it closed the seam by rotating the recorded arrival, which MOVES the destination. The hard requirement below forbids that.

## 0. The hard requirement (from the owner)

The system renders the EXACT recorded maneuvers to the EXACT destination. A looped supply-route ghost must visibly arrive at the exact base / station / landing site where the resource transfer happens, because the recorded arrival + descent (or rendezvous + dock) is recorded RELATIVE to that exact destination. The recorded destination leg must NOT be deformed, rotated, or relocated.

Alignment from re-aiming may only be spent where there is genuine slack:
- PRIMARY: the SOI-entry seam. Wait for an accurate entry timing (the Mun/Minmus phase-lock philosophy) so the transfer hands off into the recorded approach exactly.
- SECONDARY: recorded loiter orbits before descent (extend them where the recording has them). Direct-approach-then-land recordings have no loiter buffer (edge case), so SOI-entry timing must carry those alone.

## 1. What is actually broken (and what is NOT)

NOT broken: the re-aim already on `main` LANDS at the exact recorded spot. The descent is body-fixed (lat/lon/alt on the destination body), so it already touches down exactly where the player landed; the re-aim never touches it. So the exact-destination requirement is already met for the touchdown itself.

Broken (cosmetic, but real): the APPROACH seam. The synthesized heliocentric transfer arrives at the destination SOI with a v_inf that DIFFERS from the recorded approach, so the recorded arrival hyperbola (which expects the recorded v_inf) is spliced on at a different bearing. The ghost reaches the SOI, jumps (~1.37 Gm in the Duna case), and re-approaches along the recorded direction. We must remove this jump WITHOUT touching the recorded arrival + descent.

Why the transfer arrives wrong (root cause, code): `ReaimTransferSynthesizer.TrySynthesizeTransfer` aims the Lambert solve at the target body's CENTER with an ecliptic projection (`r2 = targetBody.orbit.getRelativePositionAtUT(arrivalUT).xzy` then `ProjectOntoPlane(r2, launchPlaneNormal)`), and the per-window search minimizes the arrival POSITION miss. Nothing constrains the arrival VELOCITY (the v_inf) to match the recording, and the ecliptic projection distorts the out-of-plane component. So the arrival v_inf is whatever the position-targeted Lambert yields, generally not the recorded one.

## 2. The approach: target the recorded arrival STATE, aligned by timing

Keep the recorded arrival + capture + descent EXACTLY as recorded. Change only WHERE/HOW the transfer arrives, so it hands off into the recorded approach with no jump.

Core idea: the synthesized transfer must arrive at the destination SOI at the RECORDED body-relative SOI-entry STATE (the recorded incoming v_inf direction + magnitude, and the recorded entry point), placed at the LIVE destination body. Then the recorded arrival hyperbola continues seamlessly, and the descent lands on the exact site, untouched.

The recorded arrival state relative to the destination body is fixed and known from the recording:
- `v_inf_rec` = the recorded incoming hyperbolic v_inf (direction + magnitude) relative to the destination body, from the recorded arrival hyperbola (`ReaimMissionPlan.ArrivalLeg`). The direction is the analytic inbound asymptote from the recorded conic's (e, h); the magnitude is `sqrt(mu/(-a))` for the recorded hyperbola.
- `r_soi_rec` = the recorded SOI-entry point relative to the destination body (where the recorded hyperbola crosses the SOI sphere on the inbound side).

Two equivalent framings of the construction (the implementation picks the robust one):
- (A) State-targeted: place `v_inf_rec` / `r_soi_rec` at the live destination body to get the required HELIOCENTRIC arrival state (`V_arr = V_body(t_arr) + v_inf_rec`, position = `bodyPos(t_arr) + r_soi_rec`). The transfer orbit through that state is determined; propagate it back to the launch body's orbit to find the departure, and check the launch body's phase alignment there.
- (B) Search-objective: keep the per-window Lambert but change the search objective from "minimize arrival POSITION miss to the body center" to "minimize the arrival V_INF mismatch `|(V_arr - V_body) - v_inf_rec|` (direction-weighted)", searching departure + tof around the synodic window, and aim at the recorded entry point `bodyPos + r_soi_rec` rather than the body center (drop / revisit the ecliptic projection, which only existed to dodge the 180-degree single-rev Lambert singularity; the entry-point target + the timing search avoid that singularity differently).

Why timing makes this solvable: the re-aim windows are already synodic recurrences (`RecordedDepartureUT + k*synodic`), where the launch-body / destination-body RELATIVE configuration repeats. At that recurrence the recorded transfer's relative geometry recurs, so a transfer arriving with `v_inf_rec` EXISTS and also departs the launch body with the recorded escape geometry (both the departure and arrival seams close together at the true recurrence). The residual (the recurrence is not perfectly exact for eccentric orbits) is small and is what the SOI-entry timing search + the minimal recorded loiter absorb. This is exactly "wait for accurate SOI-entry timing", just like Mun/Minmus.

What replays verbatim (never touched): the recorded arrival hyperbola, the recorded capture orbit, any recorded loiter orbits, and the body-fixed descent. The descent already lands exactly; this change only makes the transfer feed into it cleanly.

## 3. Residual handling (the slack budget)

After the timing search the arrival will match the recorded v_inf to a small residual (direction + magnitude + entry-point + arrival-UT). Spend it ONLY in the allowed places:
- SOI-entry timing: the search picks the departure/tof that minimizes the residual. A small leftover v_inf direction residual is an acceptable momentary SOI-edge seam, far smaller than the 1.37 Gm gross seam and the same class the design already accepts for the departure seam.
- Minimal recorded loiter: where the recording has a capture/parking orbit before descent, the existing loiter compression/extension can absorb an arrival-UT / phase residual so the descent still lands on the exact site (the system already does minimal optimized loitering). NO new loiter is invented; we only extend recorded loiter within the existing mechanism.
- Direct-approach-land edge case (no recorded loiter): there is no loiter buffer, so the SOI-entry timing residual is the whole budget. The search must hit a tight v_inf tolerance for these; if it cannot for a given window, that window declines to faithful (keeps the current cosmetic seam) rather than deform the descent. Fail-safe, never wrong.

NEVER spend the residual by rotating/moving the recorded arrival or descent (the #983 mistake).

## 4. Implementation sketch (to be sharpened by the plan review)

- `ReaimTransferSynthesizer`: add an arrival-STATE-targeted path. Inputs: the destination body, the arrival UT, and the recorded `v_inf_rec` (direction + magnitude) + `r_soi_rec` relative to the body. Output: a transfer whose arrival v_inf matches `v_inf_rec` (framing A or B above). Keep the existing position-targeted path as the fallback when the state-targeted solve fails (faithful, current behavior).
- The per-window search (`ReaimPlaybackResolver.BuildWindowSegments`): change the objective to the arrival-v_inf mismatch and search departure + tof around the synodic window (the existing tof search becomes a v_inf-match search; the departure may also need a small search rather than being pinned to D_k, TBD by the review). Log the achieved v_inf residual (direction-deg + magnitude + entry-point miss) so a playtest can read the slack spent.
- Recorded arrival-state extraction: build the recorded arrival Orbit from `ArrivalLeg`, read its (e, h) and SOI-crossing to get `v_inf_rec` + `r_soi_rec` (relative to the body). REUSE the #983 salvage (the pure inbound-asymptote + vector math + the recorded v_inf extraction).
- The recorded arrival + capture + descent legs: UNCHANGED. No rotation, no shift of those segments. (Contrast #983, which rotated them.)
- Loiter: reuse the existing `ReaimLoiterCompressor` / minimal-loiter mechanism for the arrival-UT/phase residual; do NOT add a new loiter system.
- Diagnostics: `ParsekFlight.LogReaimGhostTrace` logs the arrival v_inf residual + the post-fix seam magnitude at the SOI seam.

## 5. Reuse from #983 (cherry-pick)

Branch `reaim-arrival-seam` is kept. Port these pure / measurement pieces (they MEASURE the geometry; they do not rotate anything):
- `ReaimRotation.InboundAsymptoteDir` + the vector ops (Cross/Dot/Normalize): compute the inbound v_inf direction from a conic's (e, h).
- The recorded-arrival-frame extraction (`TryRecordedArrivalFrame` logic) and the candidate-transfer arrival-frame extraction (`TryReaimedArrivalFrame` logic): used to compute `v_inf_rec` and to score a candidate window's arrival v_inf against it.
- The seam diagnostic logging.
DROP: `RotationFrameToFrame`, `RotateVector`, `RotateSegmentOrientation`, `RotateBodyRelativeSegments`, the handedness guard, the whole rotation-and-apply path, and the canary's "rotated v_inf matches" assertion (replace with "the timing-searched transfer's arrival v_inf matches the recorded v_inf, and the recorded arrival/descent segments are byte-identical to faithful").

## 6. Tests

Pure (xUnit):
- Recorded `v_inf_rec` (direction + magnitude) extraction from a known recorded hyperbola.
- The arrival-v_inf-mismatch objective: scoring is correct and minimized at the recurrence; a worse window scores worse.
- The state-targeted transfer construction (framing A or B): given a target arrival state, the produced transfer arrives at that v_inf within tolerance; declines (faithful) when no solution.
- The recorded arrival + descent segments are byte-identical between the timing-fixed path and the faithful path (the destination leg is never touched) - the key regression guard for the hard requirement.
In-game canary: the timing-searched transfer's arrival v_inf matches the recorded v_inf within tolerance, and the post-fix SOI seam magnitude is below an SOI fraction (was ~1.37 Gm).

## 7. Risks / open questions (for the plan review + owner)

- The exact search method (framing A vs B; whether the departure must be searched off D_k or stays pinned; how wide the search). The review should pick the robust, convergent method.
- The ecliptic projection: it was a Lambert-singularity workaround. Dropping it for the entry-point target must not reintroduce the 180-degree single-rev degeneracy; confirm the entry-point + timing search avoids it (or keep a guarded projection only as a fallback).
- The departure seam: targeting the arrival exactly may grow the departure (launch-escape) seam in non-recurrence windows. At the true synodic recurrence both close. Quantify and confirm the residual stays in the accepted small class; decline to faithful if a window can satisfy neither.
- Magnitude residual: matching v_inf DIRECTION still leaves a |v_inf| residual if the window is off-recurrence; the recorded hyperbola shape is fixed, so a |v_inf| mismatch slightly changes the SOI-crossing. Bound it; absorb via loiter/timing or decline.
- Direct-approach-land recordings (no loiter): tight tolerance or decline-to-faithful; never deform.
- Cannot be playtested during this build run; the pure math + the byte-identical-destination regression test de-risk it, the seam-closing is playtest-gated.

## 8. Phases

1. Cherry-pick the #983 measurement math into this branch (asymptote + v_inf extraction); strip the rotation.
2. Recorded arrival-state extraction (`v_inf_rec`, `r_soi_rec`) + the arrival-v_inf-mismatch objective, pure + tested.
3. State-targeted / objective-changed transfer synthesis + window search; the recorded arrival/descent untouched; the byte-identical-destination regression test.
4. Diagnostics + canary + docs (CHANGELOG / todo / the re-aim design doc).
5. Clean Opus plan review BEFORE coding step 3 in earnest (the search method is the risk); then implement, review, build/deploy-verify/test, open PR (ready-for-playtest).

## 9. The contract, restated

The destination leg (arrival hyperbola + capture + recorded loiter + descent / rendezvous + dock) is SACRED and replays verbatim relative to the exact destination. Re-aim alignment is spent ONLY on transfer timing (SOI-entry) and minimal recorded loiter. If a window cannot align within tolerance, it declines to faithful (the current cosmetic seam) rather than move the destination. There is no path in this design that relocates the landing/destination.
