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

        // Seam-continuity tolerance (metres), RE-PINNED FROM MEASUREMENT
        // (S1.8 flight 3, 2026-08-06, PASS attempt 1): the flown corpus reads
        // gap=10,146.3 m at Kerbin->Sun and 7,284.0 m at Sun->Duna -- capture
        // jitter (the recorder samples the two segments a frame apart at
        // ~7 km/s) plus element round-trip error. 25 km = ~2.5x the larger
        // measured gap; the defect class this guards (the re-aim
        // center-to-center seam) is a ~50-90 THOUSAND km teleport, still
        // three-plus orders of magnitude above the pin.
        internal const double SeamContinuityToleranceMeters = 25_000.0;

        // Evaluation offset used for the paired samples around a seam. Small
        // enough that real orbital motion over the interval (< 8 km at the
        // ~7 km/s escape-leg speeds) stays well inside the tolerance above.
        internal const double SeamProbeOffsetSeconds = 0.5;

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
            Description = "GATING seam-continuity assertion (D6 soi-crossing-playback): at each recorded cross-body SOI handoff, the production segment resolution (EvaluateOrbitSegmentAtUT over each side's segment) yields world positions that agree within the seam tolerance -- the property whose ABSENCE is the re-aim dead-end defect (~1-SOI-radius teleports, four orders of magnitude above this tolerance).")]
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

                // Evaluate EACH SIDE'S OWN SEGMENT at the same pair of UTs
                // bracketing the seam, exactly as playback dispatch does when
                // it hands the ghost from one segment to the next.
                double utBefore = seam.seamUT - SeamProbeOffsetSeconds;
                double utAfter = seam.seamUT + SeamProbeOffsetSeconds;
                Vector3d? posBefore = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    new List<OrbitSegment> { before }, utBefore, ResolveBody);
                Vector3d? posAfter = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    new List<OrbitSegment> { after }, utAfter, ResolveBody);

                InGameAssert.IsTrue(posBefore.HasValue,
                    string.Format(ic, "{0}-side position resolves at seam UT {1:F3}",
                        before.bodyName, seam.seamUT));
                InGameAssert.IsTrue(posAfter.HasValue,
                    string.Format(ic, "{0}-side position resolves at seam UT {1:F3}",
                        after.bodyName, seam.seamUT));
                if (!posBefore.HasValue || !posAfter.HasValue)
                    continue;

                double gap = (posAfter.Value - posBefore.Value).magnitude;
                ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                    "seam {0}->{1} ut={2:F3} gap={3:F1}m (tolerance {4:F0}m)",
                    before.bodyName, after.bodyName, seam.seamUT, gap,
                    SeamContinuityToleranceMeters));
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
