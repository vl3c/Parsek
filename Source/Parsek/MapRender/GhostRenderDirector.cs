namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 3 (design §6.4): the single owner of the Parsek-side render decision. Pure function
    /// <c>(sample, priorIntent) → intent</c>; the scene driver holds the per-instance prior-intent
    /// map (keyed by committed index + <see cref="GhostRenderChain.InstanceKey"/>), which is also
    /// where overlap-instance distinctness lives — so two self-overlap instances of one recording
    /// decide independently (design §10.8).
    ///
    /// What this owns: which treatment is active, visibility, the drive-UT, and the gap-hold-vs-retire
    /// choice. Exactly one <see cref="Treatment"/> is emitted per call, which structurally prevents
    /// the polyline/orbit double-draw class.
    ///
    /// What this does NOT own (kept out deliberately):
    ///  - KSP's internal line.active toggling: the StockConic surface stays a MANAGED contract; the
    ///    treatment re-asserts intent each frame and the reconciler flags residual blinks (§6.5).
    ///  - The make-before-break SWAP EXECUTION: when the treatment changes between frames, the SCENE
    ///    must place the incoming surface before tearing down the outgoing one. Whether that needs a
    ///    bounded one-frame settle depends on KSP proto-vessel orbit re-seed latency (open question
    ///    §15.1, to resolve in-game) — so it is a parameterized scene concern, NOT a value guessed here.
    /// </summary>
    internal static class GhostRenderDirector
    {
        internal static GhostRenderIntent Decide(GhostSample sample, GhostRenderIntent priorIntent, string label)
        {
            switch (sample.Coverage)
            {
                case Coverage.InSegment:
                    return new GhostRenderIntent(
                        visible: true,
                        treatment: sample.Treatment,
                        driveUT: sample.DriveUT,
                        frameBodyName: sample.FrameBodyName,
                        payload: sample.Segment.Payload,
                        label: label);

                case Coverage.InInteriorGap:
                    // Hold the last intent: no blink, no retire, no icon jump. The held surface stays
                    // put until coverage resumes; an interior gap is a brief, off-camera flexible-SOI
                    // seam by construction (design §3.3 / §6.4). With nothing drawn yet, stay hidden.
                    return priorIntent.Visible ? priorIntent : GhostRenderIntent.Hidden(label);

                default: // OutsideWindow → pre-launch / past end / inter-cycle tail → render nothing
                    return GhostRenderIntent.Hidden(label);
            }
        }

        /// <summary>
        /// Phase 3 (migration plan §5): the typed-spine entry point. Samples a <see cref="PhaseChain"/>
        /// (via <see cref="ChainSampler.Sample(PhaseChain, double, GhostPlaybackLogic.LoopUnitSet)"/>) and
        /// runs the SAME three-case <see cref="Decide(GhostSample, GhostRenderIntent, string)"/> the legacy
        /// chain spine runs. ADDITIVE — it does not change the existing
        /// <see cref="GhostRenderChain"/>-fed sampler/director path (the flag-OFF spine). Because the
        /// sampler projects the matched phase to the same <see cref="GhostSample"/> the assembler chain
        /// produced (Phase-2 byte-parity), the emitted <see cref="GhostRenderIntent"/> is identical to the
        /// legacy spine's for the same geometry.
        ///
        /// <para><b>Test/parity convenience overload — NOT the production call path.</b>
        /// <see cref="ShadowRenderDriver.RunFrame"/> inlines the equivalent
        /// <see cref="ChainSampler.Sample(PhaseChain, double, GhostPlaybackLogic.LoopUnitSet)"/> +
        /// <see cref="Decide(GhostSample, GhostRenderIntent, string)"/> pair itself, because it needs the
        /// intermediate <see cref="GhostSample"/> afterward for the per-active-segment re-aim skip
        /// (<c>sample.Coverage == Coverage.InSegment</c>), which this overload hides. This convenience form
        /// keeps the typed-spine sampler+director composition addressable in one call for the headless
        /// parity sweep (<c>PhaseSpineParityTests</c>); it is behaviorally identical to the inlined pair.</para>
        /// </summary>
        internal static GhostRenderIntent Decide(
            PhaseChain chain, double liveUT, GhostPlaybackLogic.LoopUnitSet units,
            GhostRenderIntent priorIntent, string label)
        {
            GhostSample sample = ChainSampler.Sample(chain, liveUT, units);
            return Decide(sample, priorIntent, label);
        }
    }
}
