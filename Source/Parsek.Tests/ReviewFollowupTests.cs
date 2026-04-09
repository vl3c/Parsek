using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests addressing review feedback on PR #159:
    /// - Audio pause/unpause null-guard coverage
    /// - #259 orbit-segment fallback still runs when TerminalOrbitBody is empty
    /// - BackgroundRecorder seed-event skip predicate (recording already has events)
    /// - TimeRegressionThresholdSeconds constant applied correctly
    /// </summary>
    [Collection("Sequential")]
    public class ReviewFollowupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReviewFollowupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Audio pause/unpause methods are not xUnit-testable: both methods' IL
        // references UnityEngine.AudioSource (via the foreach over audioInfos.Values),
        // which forces the JIT to load UnityEngine.AudioModule.dll at first call,
        // and that assembly throws "ECall methods must be packaged into a system
        // module" under the xUnit host. Even the null-state early return trips it.
        // Coverage for these is deferred to in-game tests.

        #region #259 orbit-segment fallback — order-of-operations

        // The review pointed out that FinalizeIndividualRecording only fell back to
        // PopulateTerminalOrbitFromLastSegment when `v == null`. If `v != null` but
        // CaptureTerminalOrbit silently returned early (e.g. vessel.orbit == null, or
        // situation not in the accepted set), the backfill was skipped and
        // TerminalOrbitBody stayed empty. The fix checks TerminalOrbitBody AFTER the
        // capture attempt and falls back unconditionally.
        //
        // The full FinalizeIndividualRecording path needs a live ParsekFlight instance,
        // but the cascade predicate — "TerminalOrbitBody is empty → fall back to orbit
        // segments" — is exactly what PopulateTerminalOrbitFromLastSegment does already.
        // These tests document the specific scenarios and anchor the expected behavior
        // for regression detection.

        [Fact]
        public void PopulateTerminalOrbitFromLastSegment_EmptyBody_FallsBack()
        {
            var rec = new Recording
            {
                RecordingId = "test-empty-body",
                TerminalStateValue = TerminalState.Orbiting,
                // TerminalOrbitBody = null (simulates live capture failing silently)
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 100, endUT = 200,
                        bodyName = "Mun",
                        inclination = 10.0,
                        eccentricity = 0.5,
                        semiMajorAxis = 400000,
                        longitudeOfAscendingNode = 45,
                        argumentOfPeriapsis = 30,
                        meanAnomalyAtEpoch = 0.5,
                        epoch = 150,
                    },
                },
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(400000, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.5, rec.TerminalOrbitEccentricity);
        }

        [Fact]
        public void PopulateTerminalOrbitFromLastSegment_NullOrbitSegments_LeavesEmpty()
        {
            // No orbit segments → no fallback possible → TerminalOrbitBody stays null.
            // This is an acceptable degradation: the recording will still commit, just
            // without terminal orbit metadata. Ghost map presence for the recording's
            // tip vessel will be degraded, but no crash.
            var rec = new Recording
            {
                RecordingId = "test-no-segments",
                TerminalStateValue = TerminalState.Orbiting,
                OrbitSegments = null,
            };

            ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);

            Assert.Null(rec.TerminalOrbitBody);
        }

        #endregion

        // BackgroundRecorder.InitializeLoadedState's seed-event skip predicate
        // (treeRecForSeed.PartEvents.Count > 0) needs a live Vessel to exercise
        // end-to-end. The predicate is trivial enough that a pure-logic test would
        // just re-implement the condition and stay green through any code change —
        // zero regression value. Deferred to in-game tests, tracked as #265.

        #region TimeRegressionThresholdSeconds constant

        [Fact]
        public void TimeRegressionThreshold_IsOneSecond()
        {
            // Named constant extracted so CommitRecordedPoint / SamplePosition share the
            // same threshold. If we tune it (e.g. to accommodate warp jitter), this test
            // should update to match the intent.
            Assert.Equal(1.0, FlightRecorder.TimeRegressionThresholdSeconds);
        }

        [Fact]
        public void TrimRecordingToUT_UsesInvariantCultureForLogging()
        {
            // The warn log built by TrimRecordingToUT formats several doubles. On
            // comma-locale machines (de-DE, fr-FR, etc.), the original code emitted
            // "27 266,0" which breaks downstream log parsers. This test runs the
            // trim under a comma-locale current culture and asserts the exact
            // substring "from -1.0 to 105.5" appears — a freshly-constructed
            // FlightRecorder has lastRecordedUT = -1 (the sentinel initialized in
            // the field declaration), so this pins both the newUT formatting AND
            // the lastRecordedUT formatting in one assertion. If someone later
            // changes the initial value, the specific substring will change and
            // the test will force us to notice.
            var prevCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE"); // comma decimal

                var recorder = new FlightRecorder();
                recorder.Recording.Add(new TrajectoryPoint { ut = 100.5 });
                recorder.Recording.Add(new TrajectoryPoint { ut = 110.5 });
                recorder.Recording.Add(new TrajectoryPoint { ut = 120.5 });

                recorder.TrimRecordingToUT(105.5);

                string logLine = logLines.Find(l => l.Contains("Time regression detected"));
                Assert.NotNull(logLine);
                // Exact format assertion — locks in both lastRecordedUT and newUT formatting.
                // If comma-locale leaked in, this would be "from -1,0 to 105,5".
                Assert.Contains("from -1.0 to 105.5", logLine);
                Assert.DoesNotContain("-1,0 to 105,5", logLine);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prevCulture;
            }
        }

        #endregion
    }
}
