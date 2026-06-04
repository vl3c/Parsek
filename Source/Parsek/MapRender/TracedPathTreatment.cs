using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// L4 OWNED treatment (design §6.5): draws a recorded non-conic stretch (ascent, burns, descent,
    /// surface) as our own polyline + marker/label. Because it fully owns those objects (nobody else
    /// touches them), the icon/marker and the drawn path are produced together from one
    /// <c>DriveUT</c> and cannot disagree - design §6.5 invariant 2 is STRUCTURALLY guaranteed here
    /// (unlike the managed StockConic surface KSP co-owns).
    ///
    /// <para>Phase 5 scope: this is the follower shell. It follows the intent and emits the §13
    /// traced-drive line, and it is the structural OWNER the cutover fills in. The actual transfer of
    /// the polyline objects off the autonomous <c>GhostTrajectoryPolylineRenderer</c> +
    /// <c>activeLegRecordings</c> / <c>IsRenderingNonOrbitalLeg</c> publish is the Phase-8b sub-phase
    /// (retire those, the treatment owns the polyline) and the marker ownership is 8c; doing the draw
    /// here now would double-draw against the still-live autonomous renderer. So this stays a follower
    /// until those surfaces are flipped.</para>
    /// </summary>
    internal sealed class TracedPathTreatment : IGhostRenderTreatment
    {
        public Treatment Kind => Treatment.TracedPath;

        /// <summary>PURE applicability: a visible TracedPath intent (no conic payload needed - the
        /// treatment reads the source recording's points by the segment's UT window).</summary>
        internal static bool ShouldApply(GhostRenderIntent intent)
            => intent.Visible && intent.Treatment == Treatment.TracedPath;

        public void Apply(GhostRenderIntent intent, IGhostMapScene scene, uint pid)
        {
            if (!ShouldApply(intent))
                return;

            // Follower (pre-8b): the autonomous polyline renderer still draws the path and the marker
            // path still draws the icon for this recording's non-orbital leg; the treatment records its
            // intent to own them at the DriveUT so the 8b/8c flip is a swap, not a discovery.
            ParsekLog.VerboseRateLimited("MapRender", "tracedpath-drive-" + pid.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture,
                    "TracedPath drive pid={0} driveUT={1:F3} body={2}",
                    pid, intent.DriveUT, intent.FrameBodyName ?? "?"),
                2.0);
        }

        // Pre-8b the autonomous renderer owns hide/show; the treatment does not force the polyline off.
        public void StandDown(IGhostMapScene scene, uint pid)
        {
        }
    }
}
