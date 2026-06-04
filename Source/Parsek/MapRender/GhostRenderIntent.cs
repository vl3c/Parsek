namespace Parsek.MapRender
{
    /// <summary>
    /// The single per-frame, per-ghost-instance decision the <c>GhostRenderDirector</c> emits
    /// (design §6.4 / §6.10): the one authority over the Parsek-owned surfaces. Treatments are pure
    /// followers of this.
    ///
    /// World-space icon position and orbit-line geometry are NOT carried here — the scene/treatment
    /// resolves them from (FrameBodyName, Payload, DriveUT) at draw time (KSP world projection),
    /// keeping the intent Unity-world-free per the renderer/scene responsibility split (design §5).
    /// </summary>
    internal readonly struct GhostRenderIntent
    {
        internal bool Visible { get; }
        /// <summary>The active treatment this frame (exactly one; <see cref="Treatment.None"/> when hidden).</summary>
        internal Treatment Treatment { get; }
        /// <summary>Assembled-chain UT to drive the icon/conic at.</summary>
        internal double DriveUT { get; }
        /// <summary>Body frame of the active segment.</summary>
        internal string FrameBodyName { get; }
        /// <summary>What to draw (conic for StockConic; Traced otherwise — the treatment reads recorded points by UT window).</summary>
        internal SegmentPayload Payload { get; }
        internal string Label { get; }

        internal GhostRenderIntent(
            bool visible, Treatment treatment, double driveUT,
            string frameBodyName, SegmentPayload payload, string label)
        {
            Visible = visible;
            Treatment = treatment;
            DriveUT = driveUT;
            FrameBodyName = frameBodyName;
            Payload = payload;
            Label = label;
        }

        /// <summary>Nothing drawn this frame (outside window / not yet launched / retired).</summary>
        internal static GhostRenderIntent Hidden(string label = null)
            => new GhostRenderIntent(false, Treatment.None, 0.0, null, SegmentPayload.Traced, label);
    }
}
