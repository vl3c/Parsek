using System.Globalization;
using Parsek.Display;
using UnityEngine;

namespace Parsek.MapRender
{
    /// <summary>
    /// L4 OWNED treatment (design §6.5): draws a recorded non-conic stretch (ascent, burns, descent,
    /// surface) as our own polyline + marker/label. Because it fully owns those objects (nobody else
    /// touches them), the icon/marker and the drawn path are produced together from one
    /// <c>DriveUT</c> and cannot disagree - design §6.5 invariant 2 is STRUCTURALLY guaranteed here
    /// (unlike the managed StockConic surface KSP co-owns).
    ///
    /// <para>Phase 8b.1 scope: the treatment OWNS the non-orbital polyline leg draw for a
    /// director-owned ghost. It does NOT spin up a parallel host: the existing
    /// <c>GhostTrajectoryPolylineRenderer.Driver.LateUpdate</c> stays the single draw host (the locked
    /// design - drawing from the Update-time shadow would strobe / mis-layer against the stock orbit
    /// lines). When the Director decides <c>Visible &amp;&amp; TracedPath</c> for a pid this frame (the
    /// intent stamp surfaced via <see cref="ShadowRenderDriver.IsTracedPathOwnedThisFrame"/> - the single
    /// intent-sourced signal since the Phase-5b delete of the legacy side-channel), the Driver routes that
    /// ONE leg's draw through <see cref="TryDrawOwnedLeg"/> here (the same shared <c>TryDrawLeg</c>
    /// conic-anchor) and STANDS DOWN on its own direct <c>TryDrawLeg</c> call for that leg, so the leg is
    /// never drawn twice. The Driver-direct path survives ONLY for the fenced populations the spine does
    /// not enumerate (see the 5b fence note in the Driver walk).</para>
    ///
    /// <para>The ownership SIGNAL stays with the walk: the Driver publishes
    /// <c>drewNonOrbitalLegRecordings</c> / <c>IsRenderingNonOrbitalLeg</c> on any ACTUAL draw (owned or
    /// Driver-direct - the 8e S3b sole-ownership-source decision), so <c>IsPolylineOwningGhostPhase</c>
    /// stays correct and the proto orbit line is hidden + the marker rides the drawn line.</para>
    /// </summary>
    internal sealed class TracedPathTreatment : IGhostRenderTreatment
    {
        public Treatment Kind => Treatment.TracedPath;

        /// <summary>PURE applicability: a visible TracedPath intent (no conic payload needed - the
        /// treatment reads the source recording's points by the segment's UT window).</summary>
        internal static bool ShouldApply(GhostRenderIntent intent)
            => intent.Visible && intent.Treatment == Treatment.TracedPath;

        /// <summary>
        /// PURE no-double-draw decision (8b.1): should the OWNED treatment draw this ghost's
        /// non-orbital leg this frame (and the Driver stand down on its own
        /// <c>TryDrawLeg</c> for the same leg)? True exactly when the Director's active segment for the
        /// pid is a fresh TracedPath this frame (<paramref name="directorTracedPathActive"/> =
        /// <see cref="ShadowRenderDriver.IsTracedPathOwnedThisFrame"/>). The treatment's "draw" and the
        /// Driver's "stand down" are the SAME boolean, so they can never both draw the leg on any frame
        /// (the single shared predicate is also the one the icon-drive / orbit-line patches read to
        /// suppress the stock proto, so the proto and the treatment never co-draw either). Unit-testable
        /// without Unity (the predicate result is passed in).
        /// </summary>
        internal static bool ShouldOwnLeg(bool directorTracedPathActive)
            => directorTracedPathActive;

        /// <summary>
        /// OWNED single-leg draw (8b.1): draws one non-orbital leg through the SAME shared per-leg
        /// mechanics the autonomous Driver uses (<see cref="GhostTrajectoryPolylineRenderer.TryDrawLeg"/>
        /// - the conic-anchor seam math, the scaled-space build, the VectorLine inflate/fill/Draw3D, the
        /// frame stamp), so the bytes drawn are identical to the Driver-direct path: 8b.1 is a routing
        /// flip, not a pixel change (make-before-break). Mutates <paramref name="leg"/> in place exactly
        /// as the Driver's direct call does; the caller writes the struct back into its cached array.
        /// Returns the Driver's <c>anyDrawn</c> signal for the leg. The §13 traced-drive line is emitted
        /// alongside so the cutover is observable in the log.
        /// </summary>
        internal static bool TryDrawOwnedLeg(
            ref GhostTrajectoryPolylineRenderer.LegPolyline leg, Recording rec, CelestialBody body,
            int targetLayer, int drawFrame, string recordingId, int legIndex, uint pid)
        {
            bool drawn = GhostTrajectoryPolylineRenderer.TryDrawLeg(
                ref leg, rec, body, targetLayer, drawFrame, recordingId, legIndex);

            // §13 traced-drive line (design §13): the treatment owns this leg this frame. Rate-limited
            // per pid so a steady non-orbital phase does not spam. Reuses the recorded leg span as the
            // drive window (the treatment reads the recorded points by UT window).
            ParsekLog.VerboseRateLimited("MapRender", "tracedpath-own-" + pid.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture,
                    "TracedPath OWNS leg pid={0} rec={1} leg{2}=[{3:F1},{4:F1}] body={5} drawn={6} frame={7}",
                    pid, recordingId ?? "?", legIndex, leg.startUT, leg.endUT, body != null ? body.name : "?",
                    drawn, drawFrame),
                2.0);
            return drawn;
        }

        public void Apply(GhostRenderIntent intent, IGhostMapScene scene, uint pid)
        {
            if (!ShouldApply(intent))
                return;

            // The actual owned draw is routed through TryDrawOwnedLeg from the Driver's LateUpdate (the
            // single draw host - see the class summary). This Apply records the treatment's intent for
            // the per-pid §13 trace; it does not double-draw (the Driver, not Apply, calls TryDrawLeg).
            ParsekLog.VerboseRateLimited("MapRender", "tracedpath-drive-" + pid.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture,
                    "TracedPath drive pid={0} driveUT={1:F3} body={2}",
                    pid, intent.DriveUT, intent.FrameBodyName ?? "?"),
                2.0);
        }

        // The autonomous Driver owns hide/show via its per-frame deactivation sweep (a leg not drawn
        // this frame is deactivated). The treatment shares that mechanism (it draws via the same cached
        // leg + VectorLine), so a director-owned leg that stops being active is hidden by the same sweep;
        // there is nothing separate for the treatment to tear down here.
        public void StandDown(IGhostMapScene scene, uint pid)
        {
        }
    }
}
