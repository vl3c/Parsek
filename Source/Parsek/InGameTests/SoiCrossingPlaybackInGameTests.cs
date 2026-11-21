using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Parsek.InGameTests
{
    /// <summary>
    /// SoiCrossingPlayback (Tier 1 of the looped-interplanetary
    /// arrival-validation lane): the segment-dispatch half of the "Looped
    /// re-aim interplanetary transfer" todo entry's validation criterion,
    /// asserted over the injected looped-interplanetary corpus recording (the
    /// REAL flown duna-direct geometry, byte-pinned by
    /// LoopedInterplanetaryFixture in Parsek.Tests).
    ///
    /// <para>
    /// WHAT THIS GATES AND WHAT IT DOES NOT. These cells drive the SAME
    /// production position-resolution seam the map presence rides
    /// (TrajectoryMath.FindOrbitSegment* + the EvaluateOrbitSegment* pair: covering
    /// segment -> stock Orbit -> position; the TRUE-position variant, because a
    /// recorded-UT cross-frame comparison needs epoch-consistent body anchors)
    /// across both recorded SOI
    /// handoffs and at arrival, on live stock bodies. They do NOT spawn a
    /// ghost, advance the clock, or render anything -- the RENDERED truth
    /// (icon/line across the handoffs, the re-aim mode's known dead-end) is
    /// the Tier-2 map-dwell lane's job. M1/M2 gate the re-aim SOLVER; these
    /// cells gate faithful segment PLAYBACK RESOLUTION; the dwell gates the
    /// render. Three layers, no overlap.
    /// </para>
    ///
    /// <para>
    /// Cells are FLIGHT-scene because the S1.4-family injected-corpus template
    /// (gloops-airshow) boots into FLIGHT (measured: the first reading run
    /// scene-skipped all three as SPACECENTER, and the pinned skipped=0 tally
    /// red the run -- the anti-vacuity gate working). Every cell self-skips
    /// (never asserts against an assumed context) when
    /// the corpus recording is absent: the cells are meaningful only on a
    /// save injected with the looped-interplanetary preset.
    /// </para>
    /// </summary>
    public static class SoiCrossingPlaybackInGameTests
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        // The injected corpus recording's identity (must match
        // Parsek.Tests LoopedInterplanetaryFixture.RecordingId).
        internal const string CorpusRecordingId = "loopedinterp0000000000000000dd17";

        // Both sides of a seam are evaluated at the SAME instant, the seam UT (the before
        // segment's endUT and the after segment's startUT; a one-segment list is closed at
        // both ends), because that is the instant playback hands the ghost from one segment to
        // the other. Evaluating the two sides at different instants measures the vessel's own
        // travel in the world frame, which getTruePositionAtUT anchors on the Sun: the
        // earlier seam - 0.5 s / seam + 0.5 s pair read 10,146.3 m (Kerbin->Sun) and 7,284.0 m
        // (Sun->Duna), exactly the probe's heliocentric speeds there (10,146.3 m/s and
        // 7,284.0 m/s), with both conics correct.
        // At one instant the residual is the patched-conic divergence since the true crossing:
        // the after segment is the next patch (exact at the true crossing, its epoch) and the
        // two conics then drift apart by about 0.5 * da * L^2, with da below 1e-3 m/s^2 at
        // either SOI edge (Kerbin's own pull at its SOI radius, 5.0e-4, plus the Sun's tidal
        // difference across it, 7.8e-5; Duna's 1.3e-4 plus 1.4e-5) and L the handoff's
        // lateness behind the crossing (0.015 s and 0.048 s on the corpus: nanometres; the
        // recorded elements agree to 0.1 mm headless, SoiSeamWorldGapTests). 1 km allows L
        // up to about 1,400 s of warp lag. What it still catches: a side evaluated against
        // the wrong body (the re-aim center-to-center class is 50-90 THOUSAND km), a rotation
        // of one side's body-relative vector above ~0.0007 deg (the probe sits one SOI
        // radius, 84,159 km or 47,922 km, from its local body), and a side evaluated more
        // than ~0.1 s off the seam (heliocentric travel).
        internal const double SeamContinuityToleranceMeters = 1_000.0;

        private static Recording FindCorpusRecording()
        {
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec != null && string.Equals(rec.RecordingId,
                        CorpusRecordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        private static CelestialBody ResolveBody(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null)
                return null;
            return FlightGlobals.Bodies.Find(b => b != null && b.bodyName == name);
        }

        [InGameTest(Category = "SoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "The looped-interplanetary corpus recording is present, loopable, and its OrbitSegment chain carries EXACTLY the two flown cross-body SOI seams (Kerbin->Sun, Sun->Duna) via the pure seam enumerator the other cells gate on.")]
        public static void CorpusRecordingCarriesBothSoiSeams()
        {
            Recording rec = FindCorpusRecording();
            if (rec == null)
            {
                InGameAssert.Skip("looped-interplanetary corpus recording not present "
                    + "(this cell needs the looped-interplanetary injection preset)");
                return;
            }

            InGameAssert.IsTrue(rec.OrbitSegments != null && rec.OrbitSegments.Count >= 4,
                string.Format(ic, "corpus recording carries OrbitSegments (found {0})",
                    rec.OrbitSegments == null ? 0 : rec.OrbitSegments.Count));

            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            InGameAssert.AreEqual(2, seams.Count,
                "cross-body SOI seam count over the corpus segment chain");
            if (seams.Count == 2)
            {
                string fromA = rec.OrbitSegments[seams[0].beforeIndex].bodyName;
                string toA = rec.OrbitSegments[seams[0].afterIndex].bodyName;
                string fromB = rec.OrbitSegments[seams[1].beforeIndex].bodyName;
                string toB = rec.OrbitSegments[seams[1].afterIndex].bodyName;
                InGameAssert.IsTrue(fromA == "Kerbin" && toA == "Sun",
                    string.Format(ic, "first seam is Kerbin->Sun (got {0}->{1})", fromA, toA));
                InGameAssert.IsTrue(fromB == "Sun" && toB == "Duna",
                    string.Format(ic, "second seam is Sun->Duna (got {0}->{1})", fromB, toB));
            }

            InGameAssert.IsTrue(rec.LoopPlayback,
                "corpus recording is flagged for loop playback");
            ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                "corpus recording ok: segments={0} seams={1} loop={2} span=[{3:F1},{4:F1}]",
                rec.OrbitSegments.Count, seams.Count, rec.LoopPlayback,
                rec.StartUT, rec.EndUT));
        }

        [InGameTest(Category = "SoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "GATING seam-continuity assertion (D6 soi-crossing-playback): at each recorded cross-body SOI handoff, the production segment resolution (EvaluateOrbitSegmentTruePositionAtUT over each side's segment, both at the seam UT) yields world positions that agree within the seam tolerance -- the property whose ABSENCE is the re-aim dead-end defect (~1-SOI-radius teleports, four orders of magnitude above this tolerance).")]
        public static void SoiSeamWorldPositionsAreContinuous()
        {
            Recording rec = FindCorpusRecording();
            if (rec == null)
            {
                InGameAssert.Skip("looped-interplanetary corpus recording not present "
                    + "(this cell needs the looped-interplanetary injection preset)");
                return;
            }

            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            InGameAssert.IsTrue(seams.Count > 0,
                "at least one cross-body seam to probe");

            foreach (var seam in seams)
            {
                OrbitSegment before = rec.OrbitSegments[seam.beforeIndex];
                OrbitSegment after = rec.OrbitSegments[seam.afterIndex];

                // Evaluate EACH SIDE'S OWN SEGMENT at the seam UT itself, in one
                // synchronous pass: both stock Orbits read the same Planetarium.Zup and
                // the bodies are anchored at the same instant.
                Vector3d? posBefore = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    new List<OrbitSegment> { before }, seam.seamUT, ResolveBody);
                Vector3d? posAfter = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    new List<OrbitSegment> { after }, seam.seamUT, ResolveBody);

                InGameAssert.IsTrue(posBefore.HasValue,
                    string.Format(ic, "{0}-side position resolves at seam UT {1:F3}",
                        before.bodyName, seam.seamUT));
                InGameAssert.IsTrue(posAfter.HasValue,
                    string.Format(ic, "{0}-side position resolves at seam UT {1:F3}",
                        after.bodyName, seam.seamUT));
                if (!posBefore.HasValue || !posAfter.HasValue)
                    continue;

                double gap = (posAfter.Value - posBefore.Value).magnitude;
                // The side that is not the Sun: the probe there sits on that body's SOI
                // radius at the after segment's epoch (the true crossing).
                bool localIsBefore = !string.Equals(before.bodyName, "Sun", StringComparison.Ordinal);
                string localName = localIsBefore ? before.bodyName : after.bodyName;
                Vector3d localPos = localIsBefore ? posBefore.Value : posAfter.Value;
                CelestialBody localBody = ResolveBody(localName);
                double localSideFromBody = object.ReferenceEquals(localBody, null)
                    ? double.NaN
                    : (localPos - localBody.getTruePositionAtUT(seam.seamUT)).magnitude;
                double localSoi = object.ReferenceEquals(localBody, null)
                    ? double.NaN
                    : localBody.sphereOfInfluence;
                ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                    "seam {0}->{1} ut={2:F3} gap={3:F1}m (tolerance {4:F0}m, both sides at the seam UT) "
                    + "afterEpoch={5:F3} seamMinusAfterEpoch={6:F3}s localBody={7} "
                    + "localSideFromBody={8:F0}m localSoi={9:F0}m",
                    before.bodyName, after.bodyName, seam.seamUT, gap,
                    SeamContinuityToleranceMeters, after.epoch, seam.seamUT - after.epoch,
                    localName, localSideFromBody, localSoi));
                InGameAssert.IsTrue(gap <= SeamContinuityToleranceMeters,
                    string.Format(ic,
                        "seam {0}->{1} world-position gap {2:F1}m within {3:F0}m",
                        before.bodyName, after.bodyName, gap,
                        SeamContinuityToleranceMeters));
            }
        }

        [InGameTest(Category = "SoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "GATING arrival assertion (D6 soi-crossing-playback): at arrival UT the resolved position sits INSIDE the destination (Duna) SOI, and the covering segment IS Duna-bodied -- the todo entry's validation criterion is the ENCOUNTER into the destination SOI, not a seam number.")]
        public static void ArrivalResolvesInsideDestinationSoi()
        {
            Recording rec = FindCorpusRecording();
            if (rec == null)
            {
                InGameAssert.Skip("looped-interplanetary corpus recording not present "
                    + "(this cell needs the looped-interplanetary injection preset)");
                return;
            }

            CelestialBody duna = ResolveBody("Duna");
            if (duna == null)
            {
                InGameAssert.Skip("stock body Duna not present in this game database");
                return;
            }

            if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
            {
                InGameAssert.Fail("looped-interplanetary corpus recording is present "
                    + "but carries no OrbitSegments - corrupted injection");
                return;
            }

            // Arrival = one second past the last SOI seam into the
            // destination (the hyperbolic entry), plus the terminal end of
            // the recording (the committed park): both must resolve inside
            // the SOI, covering entry AND the parked tail.
            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            double lastSeamUT = seams.Count > 0
                ? seams[seams.Count - 1].seamUT
                : rec.OrbitSegments[rec.OrbitSegments.Count - 1].startUT;
            double[] arrivalUTs =
            {
                lastSeamUT + 1.0,
                rec.OrbitSegments[rec.OrbitSegments.Count - 1].endUT - 1.0,
            };

            foreach (double ut in arrivalUTs)
            {
                OrbitSegment? covering = TrajectoryMath.FindOrbitSegment(
                    rec.OrbitSegments, ut);
                InGameAssert.IsTrue(covering.HasValue
                        && covering.Value.bodyName == "Duna",
                    string.Format(ic, "covering segment at ut={0:F1} is Duna-bodied "
                        + "(got {1})", ut,
                        covering.HasValue ? covering.Value.bodyName : "<none>"));

                Vector3d? pos = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    rec.OrbitSegments, ut, ResolveBody);
                InGameAssert.IsTrue(pos.HasValue,
                    string.Format(ic, "arrival position resolves at ut={0:F1}", ut));
                if (!pos.HasValue)
                    continue;

                Vector3d dunaAtUt = duna.getTruePositionAtUT(ut);
                double distFromDuna = (pos.Value - dunaAtUt).magnitude;
                ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                    "arrival probe ut={0:F1} distFromDuna={1:F0}m soi={2:F0}m",
                    ut, distFromDuna, duna.sphereOfInfluence));
                InGameAssert.IsTrue(distFromDuna < duna.sphereOfInfluence,
                    string.Format(ic,
                        "resolved position inside Duna SOI at ut={0:F1} "
                        + "(dist {1:F0}m vs SOI {2:F0}m)",
                        ut, distFromDuna, duna.sphereOfInfluence));
            }
        }
    }
}
