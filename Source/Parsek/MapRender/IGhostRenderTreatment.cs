namespace Parsek.MapRender
{
    /// <summary>
    /// L4 treatment (design §6.5): a pure FOLLOWER of the <see cref="GhostRenderIntent"/>. A treatment
    /// never decides visibility; each frame it reads the intent and either draws (when it is the active
    /// treatment for the instance, at the intent's <c>DriveUT</c> / payload) or stands down. The
    /// Director (L3) is the single owner of the show/hide/which-treatment decision; the treatments only
    /// execute it. Exactly one treatment is active per ghost instance per frame, which structurally
    /// kills the polyline/orbit double-draw.
    ///
    /// <para>Two implementations:
    ///  - <see cref="StockConicTreatment"/> (MANAGED): drives the stock proto-vessel + KSP orbit line
    ///    from ONE <c>OrbitSegment</c> at <c>DriveUT</c>. It is exercised at the Phase-8a cutover (it
    ///    replaces the old patch's decision behind a runtime gate), NOT via a shadow draw, because the
    ///    stock surface is a single shared object the old patch still co-owns in shadow.
    ///  - <see cref="TracedPathTreatment"/> (OWNED): fully owns its polyline + marker objects, so it can
    ///    draw in shadow (its objects are diffable, nobody else touches them).</para>
    /// </summary>
    internal interface IGhostRenderTreatment
    {
        /// <summary>The treatment kind this implementation handles.</summary>
        Treatment Kind { get; }

        /// <summary>
        /// Apply the intent for the ghost <paramref name="pid"/> against the scene. Called only when
        /// this treatment is the intent's active treatment. Implementations re-assert the intent every
        /// frame (the managed stock surface is co-owned by KSP, design §6.5).
        /// </summary>
        void Apply(GhostRenderIntent intent, IGhostMapScene scene, uint pid);

        /// <summary>
        /// Stand down for the ghost <paramref name="pid"/>: this treatment is not active this frame
        /// (another treatment is, or the ghost is hidden). Hide/clear only THIS treatment's surface;
        /// never touch another treatment's objects.
        /// </summary>
        void StandDown(IGhostMapScene scene, uint pid);
    }
}
