namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 2 (design §6.3): the single, pure <c>(chain, liveUT) → GhostSample</c> resolver that
    /// collapses the old duplicated "(recording, UT) → where/what" resolvers into one.
    ///
    /// It REUSES the span clock through the existing, already-tested entry point
    /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> (which internally runs
    /// <c>DecideUnitMemberRender</c> → <c>TryComputeSpanLoopUT</c>, including loiter-cut decompression
    /// and the relaunch schedule). That function is pure and scene-agnostic, so the same mapping
    /// serves flight-map and the Tracking Station. For a non-looped member (not in any unit) it
    /// returns liveUT unchanged — the identity mapping the design requires for exact recordings.
    ///
    /// The sampler stays Unity-world-free: it resolves the assembled-chain UT, classifies coverage,
    /// and hands the segment + DriveUT downstream; world projection is the scene/treatment's job.
    /// </summary>
    internal static class ChainSampler
    {
        internal static GhostSample Sample(GhostRenderChain chain, double liveUT, GhostPlaybackLogic.LoopUnitSet units)
        {
            if (chain == null)
                return GhostSample.Outside();

            // Map live UT → assembled-chain UT via the reused span clock. The member's assembled
            // window is the chain's [WindowStartUT, WindowEndUT]; renderHidden = the span clock parked
            // the member outside its window (pre-launch / inter-cycle tail) → render nothing.
            double sampleUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                chain.CommittedIndex,
                chain.WindowStartUT,
                chain.WindowEndUT,
                liveUT,
                units,
                out bool renderHidden);

            if (renderHidden)
                return GhostSample.Outside();

            Coverage coverage = chain.ClassifyCoverage(sampleUT, out RenderSegment seg, out int index);
            switch (coverage)
            {
                case Coverage.InSegment:
                    return GhostSample.InSegment(seg, index, sampleUT);
                case Coverage.InInteriorGap:
                    return GhostSample.Gap(sampleUT);
                default:
                    return GhostSample.Outside();
            }
        }

        /// <summary>
        /// Phase 3 (migration plan §5): the typed-spine variant of <see cref="Sample(GhostRenderChain,
        /// double, GhostPlaybackLogic.LoopUnitSet)"/> — maps live UT → assembled-chain UT with the SAME
        /// span clock, then classifies coverage against a <see cref="PhaseChain"/> instead of a
        /// <see cref="GhostRenderChain"/>, projecting the matched phase to the <see cref="RenderSegment"/>
        /// it emits (via <see cref="TrajectoryPhase.Emit"/>) so the downstream <see cref="GhostSample"/> is
        /// byte-identical to the legacy chain path for the same geometry.
        ///
        /// <para><b>ADDITIVE.</b> This does NOT replace the <see cref="GhostRenderChain"/> overload above
        /// (the flag-OFF spine still uses it). The two share the exact same span-clock call
        /// (<see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/>) and the same three-valued
        /// <see cref="Coverage"/> classification (<see cref="PhaseChain.ClassifyCoverage"/> mirrors
        /// <see cref="GhostRenderChain.ClassifyCoverage"/> exactly), so an InSegment frame projects the
        /// SAME <c>Treatment</c> / <c>FrameBodyName</c> / conic payload the assembler chain carried (proven
        /// by the Phase-2 byte-parity comparator), and the DriveUT / coverage / segment index match.</para>
        ///
        /// <para>The matched phase is projected through <see cref="TrajectoryPhase.Emit"/> with a
        /// <see cref="SampleContext"/> carrying the phase's anchor body (the same context
        /// <see cref="GeometryParityComparator.ProjectGeometry"/> uses), so a conic phase whose anchor /
        /// conic body is empty still resolves its frame name identically. A matched phase that emits NO
        /// geometry (only <see cref="HoldPhase"/>, which the factory never classifies as the in-segment
        /// owner because the assembler never produces a hold segment) is treated as an interior gap (hold
        /// the prior intent) rather than an in-segment draw — the same hold contract.</para>
        /// </summary>
        internal static GhostSample Sample(PhaseChain chain, double liveUT, GhostPlaybackLogic.LoopUnitSet units)
        {
            if (chain == null)
                return GhostSample.Outside();

            double sampleUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                chain.CommittedIndex,
                chain.WindowStartUt,
                chain.WindowEndUt,
                liveUT,
                units,
                out bool renderHidden);

            if (renderHidden)
                return GhostSample.Outside();

            // Phase 6 (the ONE cross-member geometric seam): a re-aim looped landing's descent member is
            // joined to its transfer member over a Rigid + G1 orbit↔landing seam by the
            // CrossMemberSeamStitcher. It composes AFTER the span-clock remap above (the sampleUT/liveUT are
            // already remapped). The stitcher OWNS the swept-deorbit-head / captureShift / per-leg head-gate
            // clock (so those identifiers stay out of this file and the Phase-3 source-gate stays GREEN);
            // this call names none of them. It returns false for every non-descent member / non-re-aim unit
            // (byte-identical), and false WITHOUT a held sample when the descent member's slice is out of
            // window so the sub-surface ghost RETIRES rather than clamping below the surface. The base
            // coverage path below is unchanged for everything else.
            if (CrossMemberSeamStitcher.TryStitchDescentSeam(chain, sampleUT, liveUT, units, out GhostSample stitched))
                return stitched;

            Coverage coverage = chain.ClassifyCoverage(sampleUT, out TrajectoryPhase phase, out int index);
            switch (coverage)
            {
                case Coverage.InSegment:
                    // Project the matched phase to the RenderSegment it emits — the SAME projection the
                    // Phase-2 comparator uses (anchor body as the SampleContext fallback) so the geometry
                    // fields are byte-identical to the assembler chain. A phase with no geometry this frame
                    // (HoldPhase) yields nothing → fall to the gap (hold) contract.
                    if (TryProjectInSegment(phase, sampleUT, index, out GhostSample inSegment))
                        return inSegment;
                    return GhostSample.Gap(sampleUT);
                case Coverage.InInteriorGap:
                    return GhostSample.Gap(sampleUT);
                default:
                    return GhostSample.Outside();
            }
        }

        // Project the located phase's first emitted RenderSegment into an InSegment GhostSample. Returns
        // false when the phase is null or emits no geometry (a HoldPhase), so the caller holds the prior
        // intent. The SampleContext carries the phase's anchor body as the frame-name fallback, mirroring
        // GeometryParityComparator.ProjectGeometry so the resolved FrameBodyName matches the assembler.
        private static bool TryProjectInSegment(
            TrajectoryPhase phase, double sampleUT, int index, out GhostSample sample)
        {
            sample = default(GhostSample);
            if (phase == null)
                return false;

            string frameBody = (phase.Anchor is AnchorFrame.BodyAnchor body) ? body.BodyName : null;
            // Pass the REAL per-frame sampleUT into the context. Emit is currently UT-INDEPENDENT (no Emit
            // implementation reads ctx.SampleUt, so this is byte-neutral today and keeps every parity test
            // green), but carrying the real sampling UT here is the correct contract: this is the live
            // per-frame path, unlike the static whole-chain projection in
            // GeometryParityComparator.ProjectGeometry, which has no per-frame UT and deliberately passes
            // phase.StartUt instead (see the comment there).
            var ctx = new SampleContext(sampleUT, frameBody);
            foreach (RenderSegment seg in phase.Emit(ctx))
            {
                sample = GhostSample.InSegment(seg, index, sampleUT);
                return true;
            }
            return false;
        }
    }
}
