using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// L4 MANAGED treatment (design §6.5): draws a conic segment with stock KSP objects (the
    /// proto-vessel icon + the OrbitRenderer line). It follows the <see cref="GhostRenderIntent"/> and
    /// drives ONE source: it seeds the proto-vessel's <see cref="Orbit"/> from the segment's
    /// <see cref="OrbitSegment"/> elements so the line (drawn by stock from those elements) and the icon
    /// ride the one orbit - design §6.5 invariant 2.
    ///
    /// <para>The icon-off-orbit fix lives in <see cref="SeedAndDriveLive"/>: KSP resolves a packed map
    /// ghost's icon world position by re-propagating its orbit at the LIVE Planetarium clock, NOT at any
    /// effUT a patch drives, so for a loop-shifted ghost the only way to land the icon on its recorded
    /// phase is to bake the loop shift into the orbit EPOCH (so live-clock evaluation = recorded phase).
    /// That moves PHASE only - LAN/inc/argPe are untouched - so the line shape is unchanged and the icon
    /// sits on it. <see cref="SeedAndDrive"/> (raw epoch, drive at effUT) is the pure-test / future
    /// cache-owning variant and does NOT by itself control the live-resolved icon.
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
        /// Raw-epoch seed + drive at <paramref name="driveUT"/> (the recorded effUT clock). Seeds the
        /// conic's inertial Kepler elements and propagates the SAME orbit to <paramref name="driveUT"/>.
        /// Mirrors the element order of the legacy <c>GhostMapPresence.ApplyOrbitToVessel</c> seed.
        /// NOTE: for a packed map ghost this does NOT control the rendered icon - KSP re-derives the
        /// icon world position by re-propagating the orbit at the LIVE clock (see
        /// <see cref="SeedAndDriveLive"/>); prefer that for the icon-off-orbit cutover. Retained for the
        /// pure-test harness and any caller that owns the orbit cache directly.
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

        /// <summary>
        /// The one-source icon-off-orbit fix core: seed the conic with the loop shift BAKED INTO THE
        /// EPOCH and propagate at the LIVE clock. KSP resolves a packed map ghost's world position
        /// (<c>CoMD = referenceBody.position + orbitDriver.pos</c>, <c>VesselPrecalculate</c> /stock
        /// <c>OrbitDriver</c>) by RE-PROPAGATING the orbit at the LIVE Planetarium clock every
        /// FixedUpdate, discarding any effUT propagation a patch did - so driving the orbit at effUT
        /// cannot move the icon (the live re-propagation overwrites it; that is exactly why the icon sat
        /// ~96.5 deg off its line on the looped re-aim mission). Instead we seed the epoch shifted by
        /// <paramref name="epochShift"/> (= <c>liveUT - effUT</c>) so evaluating the SAME orbit at the
        /// LIVE clock lands on the recorded phase: <c>M(live) = MAE + n*(live - epoch - shift) = MAE +
        /// n*(effUT - epoch)</c> = the recorded mean anomaly at effUT. The icon (live re-propagation) and
        /// the line (drawn from these elements, arc-clipped at the LIVE bounds) then ride the one orbit
        /// at the one clock - design Section 6.5 invariant 2, achieved through the epoch rather than a
        /// per-frame effUT drive KSP overwrites.
        ///
        /// <para>This is the SAME phase the legacy effUT-drive intended (<c>SetOrbit(rawEpoch)</c> +
        /// propagate at <c>live - shift</c> gives the identical mean anomaly), so it is not a new orbit;
        /// it only moves PHASE (the epoch). LAN/inc/argPe (the inertial OrbitalFrame) are untouched, so
        /// this does NOT repeat the reverted per-element LAN-rotation dead-end. The stored loop shift is a
        /// constant offset between reseeds, so propagating at the live rate advances phase at the eff rate
        /// (no sawtooth), and re-seeding every frame in the drive patch keeps it snapped with no
        /// rate-limited-reseed stall.</para>
        /// </summary>
        internal static void SeedAndDriveLive(
            Orbit orbit, OrbitSegment seg, CelestialBody body, double epochShift, double liveDriveUT)
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
                seg.epoch + epochShift,
                body);
            orbit.UpdateFromUT(liveDriveUT);
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
