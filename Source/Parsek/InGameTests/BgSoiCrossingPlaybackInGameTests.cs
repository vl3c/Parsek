using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// BgSoiCrossingPlayback (lane SOI-3): a BACKGROUND vessel that crossed the Mun SOI exit
    /// while packed / on rails. The SOI-3 flight decouples the probe core off the Kerbal X
    /// stack (a controlled child the background recorder keeps recording) on a Mun escape
    /// hyperbola, rails-warps both across the boundary and commits; these cells then read the
    /// committed probe recording through the SAME production seams playback and the map
    /// presence use (TrajectoryMath.FindOrbitSegment / FindOrbitSegmentForMapDisplay and the
    /// epoch-consistent EvaluateOrbitSegmentTruePositionAtUT) on live stock bodies.
    ///
    /// <para>
    /// What they gate: the recording carries exactly one Mun -&gt; Kerbin seam with the old
    /// body's checkpoint section ending and the new body's starting at the same UT; the two
    /// sides resolve to the same world position across the seam; and after the seam both the
    /// playback covering segment and the map display segment are Kerbin-bodied with the
    /// resolved position outside the Mun's SOI (before it: Mun-bodied, inside). What they do
    /// not: they spawn no ghost and draw nothing (the vessels are live after the commit, so
    /// stock would not draw a ghost for them anyway).
    /// </para>
    ///
    /// <para>
    /// Every cell self-skips when no committed recording of a decoupled background vessel
    /// carries a Mun -&gt; Kerbin seam, so the lane's pinned <c>skipped=0</c> tally is what
    /// proves the flight produced the subject. The headless half (the handler's sections and
    /// the pure seam / map lookups) is <c>BackgroundOnRailsSoiCrossingTests</c>.
    /// </para>
    /// </summary>
    public static class BgSoiCrossingPlaybackInGameTests
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal const string FromBody = "Mun";
        internal const string ToBody = "Kerbin";

        // Both sides of an on-rails seam come from one frame's state (the old conic closed and
        // the re-based conic opened at the SOI event UT), so the only spread is the vessel's
        // own travel between the two probe instants (about 0.7 km at most for a slow Mun
        // escape). The defect class, a side evaluated against the wrong body, is the Mun's
        // ~12,000 km orbit radius.
        internal const double SeamContinuityToleranceMeters = 5_000.0;
        internal const double SeamProbeOffsetSeconds = 0.5;
        // How far from the seam the placement cells read (clamped to the segment spans).
        internal const double PlacementOffsetSeconds = 30.0;

        internal static bool IsBackgroundCrossingCandidate(Recording rec)
        {
            return rec != null
                && !rec.IsDebris
                && !string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                && FindMunToKerbinSeamCount(rec.OrbitSegments) > 0;
        }

        internal static int FindMunToKerbinSeamCount(List<OrbitSegment> segments)
        {
            int count = 0;
            var seams = TrajectoryMath.FindCrossBodySoiSeams(segments);
            for (int i = 0; i < seams.Count; i++)
            {
                if (segments[seams[i].beforeIndex].bodyName == FromBody
                    && segments[seams[i].afterIndex].bodyName == ToBody)
                    count++;
            }
            return count;
        }

        private static Recording FindProbeRecording()
        {
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            if (ers == null)
                return null;
            for (int i = 0; i < ers.Count; i++)
            {
                if (IsBackgroundCrossingCandidate(ers[i]))
                    return ers[i];
            }
            return null;
        }

        private static CelestialBody ResolveBody(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null)
                return null;
            return FlightGlobals.Bodies.Find(b => b != null && b.bodyName == name);
        }

        private static bool TryFindSeam(Recording rec, out OrbitSegment before, out OrbitSegment after,
            out double seamUT)
        {
            before = default(OrbitSegment);
            after = default(OrbitSegment);
            seamUT = double.NaN;
            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            for (int i = 0; i < seams.Count; i++)
            {
                OrbitSegment b = rec.OrbitSegments[seams[i].beforeIndex];
                OrbitSegment a = rec.OrbitSegments[seams[i].afterIndex];
                if (b.bodyName == FromBody && a.bodyName == ToBody)
                {
                    before = b;
                    after = a;
                    seamUT = seams[i].seamUT;
                    return true;
                }
            }
            return false;
        }

        private static void SkipNoSubject()
        {
            InGameAssert.Skip("no committed recording of a decoupled background vessel carries a "
                + "Mun -> Kerbin seam (this cell needs lane SOI-3's flight)");
        }

        [InGameTest(Category = "BgSoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "The committed recording of the decoupled background probe carries exactly one Mun -> Kerbin seam, every segment before it on the Mun and after it on Kerbin, the Mun checkpoint section ending and the Kerbin one starting at the seam UT, and a Kerbin terminal orbit body.")]
        public static void BackgroundProbeRecordingCarriesOneMunToKerbinSeam()
        {
            Recording rec = FindProbeRecording();
            if (rec == null)
            {
                SkipNoSubject();
                return;
            }

            InGameAssert.AreEqual(1, FindMunToKerbinSeamCount(rec.OrbitSegments),
                "Mun -> Kerbin seams on the probe recording");
            InGameAssert.AreEqual(1, TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments).Count,
                "cross-body seams of any kind on the probe recording");
            OrbitSegment before, after;
            double seamUT;
            if (!TryFindSeam(rec, out before, out after, out seamUT))
                return;

            int munSegments = 0, kerbinSegments = 0, misplaced = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment s = rec.OrbitSegments[i];
                if (s.bodyName == FromBody) munSegments++;
                else if (s.bodyName == ToBody) kerbinSegments++;
                bool onWrongSide = s.endUT <= seamUT ? s.bodyName != FromBody
                    : s.startUT >= seamUT ? s.bodyName != ToBody
                    : true;
                if (onWrongSide) misplaced++;
            }
            InGameAssert.AreEqual(0, misplaced,
                "segments on the wrong side of the seam (or spanning it)");

            bool munSectionEndsAtSeam = false, kerbinSectionStartsAtSeam = false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection sec = rec.TrackSections[i];
                if (sec.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                    || sec.checkpoints == null || sec.checkpoints.Count == 0)
                    continue;
                string body = sec.checkpoints[0].bodyName;
                if (body == FromBody && sec.endUT == seamUT) munSectionEndsAtSeam = true;
                if (body == ToBody && sec.startUT == seamUT) kerbinSectionStartsAtSeam = true;
            }
            InGameAssert.IsTrue(munSectionEndsAtSeam,
                string.Format(ic, "a Mun OrbitalCheckpoint section ends at the seam UT {0:F3}", seamUT));
            InGameAssert.IsTrue(kerbinSectionStartsAtSeam,
                string.Format(ic, "a Kerbin OrbitalCheckpoint section starts at the seam UT {0:F3}", seamUT));
            InGameAssert.AreEqual(ToBody, rec.TerminalOrbitBody,
                "probe recording terminal orbit body");

            ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                "bg probe recording ok: rec={0} seamUT={1:F3} munSegments={2} kerbinSegments={3} "
                + "terminalOrbitBody={4} span=[{5:F1},{6:F1}]",
                rec.RecordingId, seamUT, munSegments, kerbinSegments,
                rec.TerminalOrbitBody ?? "(null)", rec.StartUT, rec.EndUT));
        }

        [InGameTest(Category = "BgSoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "At the probe recording's Mun -> Kerbin seam the two sides' own segments resolve (epoch-consistent, live bodies) to world positions within the seam tolerance: neither side is evaluated against the wrong body.")]
        public static void BackgroundProbeSeamWorldPositionsAreContinuous()
        {
            Recording rec = FindProbeRecording();
            if (rec == null)
            {
                SkipNoSubject();
                return;
            }
            OrbitSegment before, after;
            double seamUT;
            if (!TryFindSeam(rec, out before, out after, out seamUT))
            {
                InGameAssert.Fail("candidate recording lost its Mun -> Kerbin seam");
                return;
            }

            Vector3d? posBefore = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                new List<OrbitSegment> { before }, seamUT - SeamProbeOffsetSeconds, ResolveBody);
            Vector3d? posAfter = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                new List<OrbitSegment> { after }, seamUT + SeamProbeOffsetSeconds, ResolveBody);
            InGameAssert.IsTrue(posBefore.HasValue, "Mun-side position resolves at the seam");
            InGameAssert.IsTrue(posAfter.HasValue, "Kerbin-side position resolves at the seam");
            if (!posBefore.HasValue || !posAfter.HasValue)
                return;

            double gap = (posAfter.Value - posBefore.Value).magnitude;
            ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                "bg probe seam Mun->Kerbin ut={0:F3} gap={1:F1}m (tolerance {2:F0}m)",
                seamUT, gap, SeamContinuityToleranceMeters));
            InGameAssert.IsTrue(gap <= SeamContinuityToleranceMeters,
                string.Format(ic, "seam world-position gap {0:F1}m within {1:F0}m",
                    gap, SeamContinuityToleranceMeters));
        }

        [InGameTest(Category = "BgSoiCrossingPlayback", Scene = GameScenes.FLIGHT,
            Description = "Playback (FindOrbitSegment) and the map line (FindOrbitSegmentForMapDisplay) place the background probe on the Mun inside its SOI before the seam, and on Kerbin outside the Mun's SOI after the seam and at the recording's end.")]
        public static void BackgroundProbePlaybackAndMapUseTheNewBodyAfterTheSeam()
        {
            Recording rec = FindProbeRecording();
            if (rec == null)
            {
                SkipNoSubject();
                return;
            }
            CelestialBody mun = ResolveBody(FromBody);
            CelestialBody kerbin = ResolveBody(ToBody);
            if (mun == null || kerbin == null)
            {
                InGameAssert.Skip("stock bodies Mun / Kerbin not present in this game database");
                return;
            }
            OrbitSegment before, after;
            double seamUT;
            if (!TryFindSeam(rec, out before, out after, out seamUT))
            {
                InGameAssert.Fail("candidate recording lost its Mun -> Kerbin seam");
                return;
            }

            double lastEnd = rec.OrbitSegments[rec.OrbitSegments.Count - 1].endUT;
            double beforeUT = Math.Max(before.startUT, seamUT - PlacementOffsetSeconds);
            double afterUT = Math.Min(after.endUT, seamUT + PlacementOffsetSeconds);
            var probes = new List<KeyValuePair<double, string>>
            {
                new KeyValuePair<double, string>(beforeUT, FromBody),
                new KeyValuePair<double, string>(afterUT, ToBody),
                new KeyValuePair<double, string>(lastEnd, ToBody),
            };

            foreach (var probe in probes)
            {
                double ut = probe.Key;
                string want = probe.Value;
                OrbitSegment? covering = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, ut);
                OrbitSegment? map = TrajectoryMath.FindOrbitSegmentForMapDisplay(rec.OrbitSegments, ut);
                InGameAssert.IsTrue(covering.HasValue && covering.Value.bodyName == want,
                    string.Format(ic, "playback covering segment at ut={0:F1} is {1} (got {2})",
                        ut, want, covering.HasValue ? covering.Value.bodyName : "<none>"));
                InGameAssert.IsTrue(map.HasValue && map.Value.bodyName == want,
                    string.Format(ic, "map display segment at ut={0:F1} is {1} (got {2})",
                        ut, want, map.HasValue ? map.Value.bodyName : "<none>"));

                Vector3d? pos = TrajectoryMath.EvaluateOrbitSegmentTruePositionAtUT(
                    rec.OrbitSegments, ut, ResolveBody);
                InGameAssert.IsTrue(pos.HasValue,
                    string.Format(ic, "position resolves at ut={0:F1}", ut));
                if (!pos.HasValue)
                    continue;
                double fromMun = (pos.Value - mun.getTruePositionAtUT(ut)).magnitude;
                double fromKerbin = (pos.Value - kerbin.getTruePositionAtUT(ut)).magnitude;
                ParsekLog.Info("SoiCrossPlay", string.Format(ic,
                    "bg probe placement ut={0:F1} want={1} covering={2} map={3} distFromMun={4:F0}m "
                    + "munSoi={5:F0}m distFromKerbin={6:F0}m",
                    ut, want, covering.HasValue ? covering.Value.bodyName : "<none>",
                    map.HasValue ? map.Value.bodyName : "<none>", fromMun, mun.sphereOfInfluence,
                    fromKerbin));
                if (want == FromBody)
                {
                    InGameAssert.IsTrue(fromMun < mun.sphereOfInfluence,
                        string.Format(ic, "inside the Mun SOI at ut={0:F1} ({1:F0}m vs {2:F0}m)",
                            ut, fromMun, mun.sphereOfInfluence));
                }
                else
                {
                    InGameAssert.IsTrue(fromMun > mun.sphereOfInfluence,
                        string.Format(ic, "outside the Mun SOI at ut={0:F1} ({1:F0}m vs {2:F0}m)",
                            ut, fromMun, mun.sphereOfInfluence));
                    InGameAssert.IsTrue(fromKerbin < kerbin.sphereOfInfluence,
                        string.Format(ic, "inside Kerbin's SOI at ut={0:F1}", ut));
                }
            }
        }
    }
}
