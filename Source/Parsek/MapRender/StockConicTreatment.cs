using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// L4 MANAGED treatment (design §6.5): draws a conic segment with stock KSP objects (the
    /// proto-vessel icon + the OrbitRenderer line). It follows the <see cref="GhostRenderIntent"/> and
    /// drives ONE source: it seeds the proto-vessel's <see cref="Orbit"/> from the segment's
    /// <see cref="OrbitSegment"/> elements and positions the icon by propagating THAT SAME orbit at the
    /// intent's <c>DriveUT</c>. Because the line (drawn by stock from the orbit elements) and the icon
    /// (the orbit's position at DriveUT) come from the one orbit, the icon rides the line by
    /// construction - design §6.5 invariant 2. There is no separate body-fixed drive of the icon.
    ///
    /// <para>"MANAGED, not fully owned": KSP co-owns <c>line.active</c> (it toggles it during SOI
    /// transitions / floating-origin shifts), so the treatment re-asserts the intent every frame and
    /// the reconciler flags residual blinks. This surface is exercised at the Phase-8a cutover (the
    /// Director's decision replaces the old patch's, behind a runtime gate), NOT via a shadow draw - the
    /// stock object is a single shared per-pid object the old patch still co-owns while shadow runs, so
    /// a second writer would fight it.</para>
    /// </summary>
    internal sealed class StockConicTreatment : IGhostRenderTreatment
    {
        public Treatment Kind => Treatment.StockConic;

        /// <summary>
        /// PURE applicability predicate: this treatment draws only a visible StockConic intent that
        /// actually carries a conic payload. Anything else is a no-op (some other treatment owns it).
        /// </summary>
        internal static bool ShouldApply(GhostRenderIntent intent)
            => intent.Visible && intent.Treatment == Treatment.StockConic && intent.Payload.HasConic;

        /// <summary>
        /// The one-source seed + drive (the fix core). Seeds the conic's inertial Kepler elements (what
        /// stock draws the line from) and propagates the SAME orbit to <paramref name="driveUT"/> so the
        /// icon sits on the line. Kept as an internal static so the cutover and any test harness call
        /// the exact same drive. Mirrors the element order of the legacy
        /// <c>GhostMapPresence.ApplyOrbitToVessel</c> seed.
        /// </summary>
        internal static void SeedAndDrive(Orbit orbit, OrbitSegment seg, CelestialBody body, double driveUT)
        {
            if (orbit == null || body == null)
                return;
            orbit.SetOrbit(
                seg.inclination,
                seg.eccentricity,
                seg.semiMajorAxis,
                seg.longitudeOfAscendingNode,
                seg.argumentOfPeriapsis,
                seg.meanAnomalyAtEpoch,
                seg.epoch,
                body);
            // Position the icon from the SAME orbit at the same drive clock the line is evaluated at.
            orbit.UpdateFromUT(driveUT);
        }

        public void Apply(GhostRenderIntent intent, IGhostMapScene scene, uint pid)
        {
            if (scene == null || !ShouldApply(intent))
                return;
            if (!scene.TryGetGhostOrbit(pid, out Orbit orbit) || orbit == null)
                return;
            CelestialBody body = scene.ResolveBody(intent.FrameBodyName);
            if (body == null)
                return;

            SeedAndDrive(orbit, intent.Payload.Conic, body, intent.DriveUT);

            ParsekLog.VerboseRateLimited("MapRender", "stockconic-drive-" + pid.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture,
                    "StockConic drive pid={0} driveUT={1:F3} body={2} sma={3:F0} ecc={4:F4}",
                    pid, intent.DriveUT, intent.FrameBodyName ?? "?",
                    intent.Payload.Conic.semiMajorAxis, intent.Payload.Conic.eccentricity),
                2.0);
        }

        // The managed surface's visibility (line.active / icon) is owned by the stock patch contract
        // (the Director's hidden intent + the cutover gate decide show/hide). Standing down here is a
        // no-op: this treatment never force-hides a co-owned stock object out from under that contract.
        public void StandDown(IGhostMapScene scene, uint pid)
        {
        }
    }
}
