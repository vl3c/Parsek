using System;
using System.Globalization;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 6 (migration plan §8 / design §9.1): the ONE cross-member geometric seam built in v1 — the
    /// minimal <c>CrossMemberSeamStitcher</c> that joins a re-aim looped landing's transfer-member deorbit
    /// run to its descent member over a numerically-enforced G1 orbit↔landing seam.
    ///
    /// <para><b>It absorbs the existing clock-domain join</b> (today scattered in
    /// <see cref="Parsek.Reaim.DescentTrigger"/> + <see cref="GhostPlaybackLogic"/>'s span clock): the swept
    /// deorbit head <c>recordedDeorbitUT + (currentUT - triggerUT)</c>, the <c>captureShift</c> phase
    /// alignment that lands the body-fixed descent on the parking orbit at the same rotation phase, and the
    /// per-leg head-gate (the transfer member's deorbit-tail legs sweep down to the seam during the Loiter
    /// phase). This file is the HOME of that clock logic for the typed spine — it reuses the pure
    /// <see cref="Parsek.Reaim.DescentTrigger"/> helpers verbatim (so the flag-OFF
    /// <see cref="Parsek.Reaim.DescentTrigger"/> behavior is UNCHANGED — this never modifies them) and keeps
    /// the <c>deorbit-head</c> / <c>captureShift</c> / <c>ResolveTransferLegHeadUT</c> identifiers OUT of the
    /// three gated spine files (<see cref="ShadowRenderDriver"/> / <see cref="ChainSampler"/> /
    /// <see cref="GhostRenderDirector"/>). The spine invokes this stitcher through a clean API
    /// (<see cref="TryStitchDescentSeam"/>) that names none of those identifiers, so the Phase-3
    /// deorbit-decoupling source-gate stays GREEN.</para>
    ///
    /// <para><b>It adds the G1 continuity assertion ON TOP</b>: a numerical TANGENT MATCH — the capture
    /// orbit's velocity direction at SOI/atmosphere entry matched to the recorded descent's first-sample
    /// tangent — over one <see cref="PhaseSeam.Rigid"/> (Rigid + G1 + OnCamera). A tangent mismatch beyond
    /// tolerance is the Tier-C <c>rigid-seam-tangent-discontinuity</c> anomaly
    /// (<see cref="PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity"/> / <see cref="IsTangentSeamContinuous"/>
    /// / <see cref="EmitTangentDiscontinuity"/>). Those tangents are a RENDER-TIME quantity (a
    /// <see cref="RenderSegment"/> carries no points — the treatment reads the recorded points at draw time),
    /// so the pure predicate + emit helper are built and test-exercised here while the production anomaly
    /// auto-raise is wired at the descent DRAW site in Phase 5b (when that path is reworked).</para>
    ///
    /// <para><b>Tracer integration (Tier-A).</b> Every successful promote emits the Tier-A
    /// <c>DescentStitched</c> structural event from the decision site (<see cref="EmitDescentStitched"/> via
    /// <see cref="TryStitchDescentSeam"/>), once-per-stitch-onset (not per frame) and free in normal play
    /// (gated on <see cref="MapRenderTrace.IsEnabled"/>) — the new producer participates in the map render
    /// tracer like every other decision site.</para>
    ///
    /// <para><b>It promotes the deorbit arc into a visible first-class <see cref="DescentPhase"/></b> (no
    /// longer hidden in the transfer member), carrying the Rigid G1 leading seam.</para>
    ///
    /// <para><b>Ordering (design §9.1):</b> the swept-deorbit-head UT join composes AFTER the arrival-hold +
    /// destination-loiter-trim span-clock remap. The spine hands this stitcher the ALREADY-REMAPPED sample
    /// UT (the span clock <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> has already run by
    /// the time <see cref="ChainSampler.Sample(PhaseChain, double, GhostPlaybackLogic.LoopUnitSet)"/> calls
    /// in), so the stitcher composes after the remap by construction (it never re-applies it).</para>
    ///
    /// <para><b>Flag-gated, additive.</b> Only invoked when <see cref="ShadowRenderDriver.PhaseSpineDriveActive"/>
    /// (default OFF). Flag OFF: the spine never calls in, so the descent renders through the legacy path,
    /// byte-identical to today. Flag ON: the stitcher promotes the descent + enforces the G1 seam. Nothing
    /// here is deleted; the legacy descent path stays intact as the reversible fallback. Phase 6 INTENTIONALLY
    /// changes the flag-ON descent geometry (it is the fix for the sub-surface ghost), so the parity oracle
    /// runs in SYNTHESIZED mode here (rendered == the stitcher's intended G1 arc), NOT recorded-vs-rendered.</para>
    ///
    /// <para><b>Minimal — NOT the full <see cref="MissionComposite"/></b> (design §17). It is the focused
    /// slice that the eventual composite absorbs unchanged: ONE cross-member seam (orbit↔landing), not the
    /// launch-rotation cross-member seam or mission-wide composite ownership.</para>
    /// </summary>
    internal static class CrossMemberSeamStitcher
    {
        /// <summary>
        /// The G1 tangent tolerance the descent re-stitch enforces (radians). Shared with
        /// <see cref="PhaseSeamClassifier.DefaultTangentToleranceRadians"/> so the predicate and the stitcher
        /// agree; named here so the call sites + tests reference the stitcher's contract directly.
        /// </summary>
        internal const double TangentToleranceRadians =
            PhaseSeamClassifier.DefaultTangentToleranceRadians;

        /// <summary>
        /// PURE clock absorb: resolve the descent member's re-anchored playback head this frame (the swept
        /// deorbit head + captureShift phase alignment) by REUSING the pure
        /// <see cref="Parsek.Reaim.DescentTrigger"/> helpers. This is the cross-member clock the spine cannot
        /// reference directly (the source-gate forbids the deorbit-clock identifiers in the three spine
        /// files), so it lives here.
        ///
        /// <para>Returns true (with <paramref name="head"/> set + <paramref name="phase"/> = Descent) ONLY
        /// when the member is a descent-set member of a descent-trigger unit AND the shared monotone descent
        /// head falls inside this member's window THIS frame; false (head NaN) to hide the member otherwise
        /// (Inert / Loiter / Done / outside-this-member's-slice). For every non-descent member / non-re-aim
        /// unit it returns false — byte-identical to the no-trigger path. <paramref name="unitCycle"/> is the
        /// span clock's resolved cycle for this frame (the spine resolves it through the same
        /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> call, so there is no second
        /// UT→cycle mapping that could disagree).</para>
        ///
        /// <para>The arithmetic is <c>recordedDeorbitUT + (currentUT - triggerUT)</c> (the swept deorbit
        /// head), with <c>triggerUT</c> derived from <c>captureShift</c> + the destination rotation period —
        /// exactly the clock the legacy polyline Driver + the span clock host today, re-homed here.</para>
        /// </summary>
        internal static bool TryResolveDescentSeamHead(
            GhostPlaybackLogic.LoopUnit unit,
            int committedIndex,
            long unitCycle,
            double currentUT,
            double memberStartUT,
            double memberEndUT,
            out double head,
            out Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
        {
            head = double.NaN;
            phase = Parsek.Reaim.DescentTrigger.DescentHeadPhase.Inert;

            if (!unit.HasDescentTrigger || !unit.IsDescentMember(committedIndex))
                return false;

            memberStartUT = unit.MemberStartUT(committedIndex, memberStartUT);
            memberEndUT = unit.MemberEndUT(committedIndex, memberEndUT);

            return Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(
                currentUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                memberStartUT, memberEndUT, out head, out phase);
        }

        /// <summary>
        /// PURE per-leg head-gate absorb: the head ONE transfer-member deorbit-tail leg should gate on this
        /// frame, REUSING <see cref="Parsek.Reaim.DescentTrigger.ResolveTransferLegHeadUT"/>. A deorbit-tail
        /// leg (the contiguous approach legs ending at/below the seam) gates on the swept deorbit head so it
        /// sweeps down to the seam during the Loiter phase; every other leg keeps the normal loop head. This
        /// is the same per-leg gate the legacy polyline Driver applies, re-homed into the stitcher so the
        /// spine never names <c>ResolveTransferLegHeadUT</c>. Pure; no Unity.
        /// </summary>
        internal static double ResolveDeorbitTailLegHead(
            double legEndUT, double seamUT, double epsSeconds, double loopHeadUT,
            double deorbitTailHead, bool deorbitTailLegEligible)
        {
            return Parsek.Reaim.DescentTrigger.ResolveTransferLegHeadUT(
                legEndUT, seamUT, epsSeconds, loopHeadUT, deorbitTailHead, deorbitTailLegEligible);
        }

        /// <summary>
        /// PURE G1 tangent-match evaluation: given the leaving phase's terminal velocity DIRECTION (the
        /// capture orbit's velocity at SOI/atmosphere entry) and the entering phase's first-sample velocity
        /// direction (the recorded descent's first tangent), classify the orbit↔landing seam as continuous
        /// (true) or a <c>rigid-seam-tangent-discontinuity</c> (false). NaN/Inf/zero-length tangents on
        /// either side yield "continuous" (no false anomaly) — an unmeasurable tangent is not a
        /// discontinuity. Delegates the angle math to
        /// <see cref="PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity"/> (the Phase-1 predicate). Pure;
        /// Unity-vector math only (no ECalls), so it is directly unit-testable.
        /// </summary>
        internal static bool IsTangentSeamContinuous(
            Vector3 leavingTangent, Vector3 enteringTangent,
            double toleranceRadians = TangentToleranceRadians)
        {
            return !PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                leavingTangent, enteringTangent, toleranceRadians);
        }

        /// <summary>
        /// PURE first-difference tangent from two consecutive recorded points' world/body-relative
        /// positions: the velocity DIRECTION of the segment from <paramref name="aXyz"/> to
        /// <paramref name="bXyz"/>. Used to extract the capture-orbit-exit tangent (the last two recorded
        /// approach points) and the descent's first-sample tangent (the first two recorded descent points)
        /// without a live KSP orbit sampler, so the seam math is headless. A degenerate (equal / non-finite)
        /// pair yields <see cref="Vector3.zero"/> (an unmeasurable tangent → no false anomaly downstream).
        /// </summary>
        internal static Vector3 TangentFromPositions(Vector3 aXyz, Vector3 bXyz)
        {
            Vector3 d = bXyz - aXyz;
            if (!IsFinite(d.x) || !IsFinite(d.y) || !IsFinite(d.z))
                return Vector3.zero;
            if (d.sqrMagnitude <= 1e-12f)
                return Vector3.zero;
            return d;
        }

        /// <summary>
        /// THE SPINE API (clean, gate-safe). When the flag is ON, the typed-spine sampler calls this AFTER it
        /// has resolved the base coverage of a per-member <see cref="PhaseChain"/> at the already-remapped
        /// <paramref name="sampleUT"/>. If <paramref name="committedIndex"/> is a descent-set member of a
        /// descent-trigger unit AND its re-anchored descent head is live this frame, the stitcher PROMOTES the
        /// matched <see cref="DescentPhase"/> over a Rigid + G1 leading seam and returns the stitched
        /// <see cref="GhostSample"/> (visible TracedPath descent at the re-anchored head). Otherwise it
        /// returns false and the caller keeps the base sample.
        ///
        /// <para><b>The method name + every parameter type names NONE of the deorbit-clock identifiers the
        /// source-gate forbids</b> (<c>ResolveTransferLegHeadUT</c> / <c>deorbitHead</c> /
        /// <c>captureShift</c>), so the spine can invoke it without tripping the Phase-3 gate. The clock
        /// arithmetic itself stays inside this stitcher (and the pure helpers it reuses).</para>
        ///
        /// <para><b>Sub-surface retire (design section 9.1 / migration plan section 8) - defense-in-depth
        /// here, NOT the production gate:</b> the PRODUCTION sub-surface retire gate is the span-clock
        /// resolver's <c>renderHidden</c> (<c>GhostPlaybackLogic.SpanClock</c>'s
        /// <c>ResolveTrackingStationSampleUT</c> resolves the descent head itself and hides a Done / Inert /
        /// Loiter / out-of-slice descent member BEFORE the sampler ever reaches this stitcher). When the
        /// re-anchored head is NOT live (the descent member's slice is out of window - past
        /// <c>descentEndUT</c> / before entry / loitering), this returns false WITHOUT a held sample so the
        /// caller's base path renders nothing - a second, defense-in-depth layer behind the span clock,
        /// never the load-bearing retire.</para>
        /// </summary>
        internal static bool TryStitchDescentSeam(
            PhaseChain chain,
            double sampleUT,
            double liveUT,
            GhostPlaybackLogic.LoopUnitSet units,
            out GhostSample stitched)
        {
            stitched = default(GhostSample);

            if (chain == null || units == null)
                return false;

            int committedIndex = chain.CommittedIndex;
            if (!units.TryGetUnitForMember(committedIndex, out GhostPlaybackLogic.LoopUnit unit))
                return false;
            if (!unit.HasDescentTrigger || !unit.IsDescentMember(committedIndex))
                return false;

            // Resolve the span clock cycle the SAME way the spine's ChainSampler did (one source of truth):
            // the descent re-anchor needs the unit cycle, which DecideUnitMemberRender resolved when the
            // sampler called ResolveTrackingStationSampleUT. Re-resolve it here from the live UT via the same
            // pure decision (no second mapping that could disagree with the cached geometry).
            double memberStartUT = unit.MemberStartUT(committedIndex, chain.WindowStartUt);
            double memberEndUT = unit.MemberEndUT(committedIndex, chain.WindowEndUt);
            if (!GhostPlaybackLogic.TryResolveDescentUnitCycle(
                    unit, committedIndex, liveUT, memberStartUT, memberEndUT, out long unitCycle))
                return false;

            if (!TryResolveDescentSeamHead(
                    unit, committedIndex, unitCycle, liveUT, memberStartUT, memberEndUT,
                    out double reAnchoredHead,
                    out Parsek.Reaim.DescentTrigger.DescentHeadPhase phase))
            {
                // Not rendering its descent slice this frame: Inert / Loiter / Done / out-of-window. Return
                // false WITHOUT a held sample so the descent member retires (sub-surface-ghost-retires).
                return false;
            }

            // Locate the descent phase covering the re-anchored head in the per-member chain. The factory
            // already classifies the post-conic body-fixed run as a DescentPhase, so this is the promoted
            // first-class phase. Use the RE-ANCHORED head as the assembled UT (the swept deorbit head), not
            // the raw sampleUT, because the descent clip plays forward from recordedDeorbitUT.
            if (!chain.TryGetPhase(reAnchoredHead, out TrajectoryPhase phaseAtHead, out int phaseIndex))
                return false;

            // TracedPath-family guard: PhaseFactory legitimately classifies a real landing-shaped member's
            // post-conic traced run as a DescentPhase, a SurfacePhase (a surface-tailed run: the run's LAST
            // overlapping TrackSection is Surface, per ResolveEnvPhaseForWindow's terminal-class rule), or an
            // AscentPhase (no OrbitSegments at all, the v1 pure-atmospheric default). All three are TracedPhase
            // subclasses emitting the same TracedPath geometry, so promoting ANY of them over the Rigid
            // orbit<->landing seam is CORRECT - warning on them would flood normal post-flip play every
            // stitched frame. Only a ConicPhase / HoldPhase at the re-anchored head is a genuine
            // factory-classification anomaly (a conic or hold can never be the promoted descent run); warn
            // on that, rate-limited per (recordingId, phaseType) so a persistent regression logs LOUDLY
            // ("if it didn't get logged, it didn't happen") without per-frame spam. Behaviour is unchanged
            // on the correct path; this is observability.
            if (!(phaseAtHead is TracedPhase))
                ParsekLog.WarnRateLimited("MapRender",
                    "stitch-non-traced-" + (chain.RecordingId ?? "?") + "-"
                        + (phaseAtHead?.GetType().Name ?? "null"),
                    string.Format(CultureInfo.InvariantCulture,
                        "CrossMemberSeamStitcher: phase at re-anchored head {0:R} is {1}, not a TracedPath-family "
                        + "(TracedPhase) phase (rec={2} member={3}) - stamping orbit<->landing seam anyway; "
                        + "factory classification regression?",
                        reAnchoredHead, phaseAtHead?.GetType().Name ?? "null", chain.RecordingId, committedIndex));

            // PROMOTE: emit the descent phase as a visible first-class phase carrying the Rigid + G1 leading
            // seam (the orbit↔landing cross-member seam). The seam CONTRACT is stamped here, and the Tier-A
            // DescentStitched structural event is emitted from this decision site (EmitDescentStitchedTraceOnChange,
            // below). The G1 TANGENT MATCH's production anomaly raise lives at the descent DRAW site (the only
            // place the live world tangents exist — a RenderSegment carries no points), wired in Phase 5b when
            // that path is reworked; the pure predicate + EmitTangentDiscontinuity helper are built + test-exercised.
            //
            // The phase the factory built carries a null leading seam (PhaseFactory deliberately leaves seams
            // null — they are a spine-side re-derivation concern), so its emitted RenderSegment.LeadingSeam is
            // SeamKind.None. The cross-member orbit↔landing seam is the STITCHER's to own, so re-stamp the
            // promoted segment with the Rigid leading seam (BuildOrbitLandingSeam = Rigid + G1 + OnCamera,
            // mapped to the legacy SeamKind the live draw path reads). This is the one cross-member seam the
            // design promotes onto the descent member.
            string frameBody =
                (phaseAtHead.Anchor is AnchorFrame.BodyAnchor body) ? body.BodyName : null;
            var ctx = new SampleContext(reAnchoredHead, frameBody);
            foreach (RenderSegment seg in phaseAtHead.Emit(ctx))
            {
                RenderSegment seamed = StampOrbitLandingSeam(seg);
                stitched = GhostSample.InSegment(seamed, phaseIndex, reAnchoredHead);
                EmitDescentStitchedTraceOnChange(chain.RecordingId, committedIndex, liveUT, reAnchoredHead, phase);
                return true;
            }

            // A phase that emits no geometry this frame (only HoldPhase, which the descent member never is):
            // keep the base sample.
            return false;
        }

        /// <summary>
        /// Re-stamp a promoted descent <see cref="RenderSegment"/>'s LEADING seam with the cross-member
        /// orbit↔landing seam (<see cref="BuildOrbitLandingSeam"/> = Rigid + G1 + OnCamera) the stitcher owns,
        /// mapped to the legacy <see cref="SeamKind"/> the live draw path reads. Every other field is carried
        /// through unchanged (RenderSegment is a readonly struct, so this rebuilds it). Pure; no Unity.
        /// </summary>
        internal static RenderSegment StampOrbitLandingSeam(RenderSegment seg)
        {
            SeamKind leading = PhaseGeometry.ToLegacySeam(BuildOrbitLandingSeam());
            return new RenderSegment(
                seg.Kind, seg.Treatment, seg.StartUT, seg.EndUT, seg.FrameBodyName, seg.Payload,
                isGenerated: seg.IsGenerated, leadingSeam: leading, trailingSeam: seg.TrailingSeam);
        }

        /// <summary>
        /// Build the Rigid + G1 orbit↔landing seam this stitch enforces (design §6.1 / §9.1). The descent
        /// member's leading seam is OnCamera (the landing is in view) so a tangent discontinuity is a real,
        /// visible kink the oracle / reconciler must surface.
        /// </summary>
        internal static PhaseSeam BuildOrbitLandingSeam() => PhaseSeam.Rigid(onCamera: true);

        /// <summary>
        /// PURE detail-line builder for the Tier-A <c>DescentStitched</c> structural event (kept pure - no
        /// Unity reads, no global sink - so the line schema is directly unit-testable, mirroring
        /// <see cref="MapRenderTrace.BuildLifecycleDetails"/>).
        /// </summary>
        internal static string BuildDescentStitchedDetails(
            int committedIndex, double reAnchoredHead,
            Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
            => string.Format(CultureInfo.InvariantCulture,
                "member={0} reAnchoredHead={1:R} phase={2} seam=rigid+G1 onCamera=true",
                committedIndex, reAnchoredHead, phase);

        /// <summary>
        /// PURE detail-line builder for the Tier-C <c>rigid-seam-tangent-discontinuity</c> anomaly (pure, so
        /// the line schema is unit-testable without the global trace sink).
        /// </summary>
        internal static string BuildTangentDiscontinuityDetails(
            Vector3 leavingTangent, Vector3 enteringTangent, double measuredAngleRadians)
            => string.Format(CultureInfo.InvariantCulture,
                "angle={0:F4}rad tol={1:F4}rad leaving=({2:F2},{3:F2},{4:F2}) entering=({5:F2},{6:F2},{7:F2})",
                measuredAngleRadians, TangentToleranceRadians,
                leavingTangent.x, leavingTangent.y, leavingTangent.z,
                enteringTangent.x, enteringTangent.y, enteringTangent.z);

        /// <summary>
        /// Emit the Tier-C <c>rigid-seam-tangent-discontinuity</c> anomaly when the orbit↔landing G1 seam's
        /// two tangents diverge beyond tolerance. Gated by <see cref="MapRenderTrace.IsEnabled"/> (the
        /// off-by-default <c>mapRenderTracing</c> setting), so normal play pays nothing. The caller supplies
        /// the live world/body-relative tangents (the Unity path) and the measured angle for the detail line.
        /// </summary>
        internal static void EmitTangentDiscontinuity(
            uint pid, string recordingId, double currentUT,
            Vector3 leavingTangent, Vector3 enteringTangent, double measuredAngleRadians)
        {
            if (!MapRenderTrace.IsEnabled)
                return;

            string details = BuildTangentDiscontinuityDetails(
                leavingTangent, enteringTangent, measuredAngleRadians);

            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.Polyline,
                pid.ToString(CultureInfo.InvariantCulture),
                currentUT, currentUT,
                MapRenderTrace.AnomalyRigidSeamTangentDiscontinuity,
                details, recordingId);
        }

        /// <summary>
        /// Emit the Tier-A descent-stitch seam structural event on a (re)stitch: the promoted descent member,
        /// the re-anchored head, and the seam continuity. Gated by <see cref="MapRenderTrace.IsEnabled"/>
        /// (free in normal play). Surface = Polyline (the descent draws as our owned polyline).
        /// </summary>
        internal static void EmitDescentStitched(
            uint pid, string recordingId, double currentUT, double reAnchoredHead,
            int committedIndex, Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
        {
            if (!MapRenderTrace.IsEnabled)
                return;

            string details = BuildDescentStitchedDetails(committedIndex, reAnchoredHead, phase);

            MapRenderTrace.EmitStructural(
                "DescentStitched", MapRenderTrace.RenderSurface.Polyline,
                pid.ToString(CultureInfo.InvariantCulture), currentUT, reAnchoredHead,
                MapRenderTrace.SegmentChangeWindowSeconds, details, recordingId);
        }

        /// <summary>
        /// Wire the Tier-A <see cref="EmitDescentStitched"/> structural event from the live decision site
        /// (<see cref="TryStitchDescentSeam"/>), gated ONCE-PER-STITCH-ONSET (committed index + descent head
        /// phase) via <see cref="MapRenderTrace.ShouldEmitDescentStitchOnChange"/> so a steady re-anchored
        /// descent emits ONE structural line, not one per frame (the VerboseRateLimited convention;
        /// <see cref="MapRenderTrace.EmitStructural"/> routes to Info unconditionally). Resolves the ghost pid
        /// from the committed index ONLY behind the <see cref="MapRenderTrace.IsEnabled"/> gate, so the
        /// flag-OFF / tracing-OFF path never touches <see cref="GhostMapPresence"/> and the headless unit
        /// tests stay pure; pid resolves to 0 when no ghost vessel exists yet (the recId still names the line).
        /// </summary>
        private static void EmitDescentStitchedTraceOnChange(
            string recordingId, int committedIndex, double liveUT, double reAnchoredHead,
            Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
        {
            if (!MapRenderTrace.IsEnabled)
                return;

            uint pid = GhostMapPresence.GetGhostVesselPidForRecording(committedIndex);
            string pidKey = pid.ToString(CultureInfo.InvariantCulture);
            string signature = committedIndex.ToString(CultureInfo.InvariantCulture)
                + ":" + phase.ToString();
            if (!MapRenderTrace.ShouldEmitDescentStitchOnChange(pidKey, signature))
                return;

            EmitDescentStitched(pid, recordingId, liveUT, reAnchoredHead, committedIndex, phase);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
