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
    }
}
