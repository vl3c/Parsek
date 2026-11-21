using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q1b in every scene: a ghost on its predicted tail is not drawn beside the
    /// vessel the tail predicts. FLIGHT's playback flags and the Space Center pass read one
    /// shared predicate (GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene); the
    /// map presence of FLIGHT and the Tracking Station drops a non-loop map ghost whenever the
    /// launch-matched vessel exists (the materialized gate), tail or not.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTailSceneHideTests : IDisposable
    {
        private const uint StagePid = 77u;
        private const string StageGuid = "0123456789abcdef0123456789abcdef";

        public PredictedTailSceneHideTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static Recording Stage()
        {
            var rec = new Recording
            {
                RecordingId = "rec_stage", VesselName = "Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = StageGuid,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 160.0, endUT = 400.0, bodyName = "Kerbin",
                semiMajorAxis = 700000, eccentricity = 0.05, isPredicted = true,
            });
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = 400.0;
            return rec;
        }

        [Fact]
        public void ScenePredicate_HidesTheTailBesideTheLiveVessel_NeverALoop()
        {
            Recording rec = Stage();
            Func<Recording, bool> alive = r => true;
            Func<Recording, bool> gone = r => false;

            Assert.True(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, false, alive));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 150.0, false, alive));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, false, gone));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, true, alive));
        }

        // The predicate's real-vessel read is the launch-gated one: a relaunch of the same
        // craft (same baked pid, another launch guid) never hides the tail.
        [Fact]
        public void ScenePredicate_ReadsTheLaunchGatedVessel()
        {
            Recording rec = Stage();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == StagePid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => StageGuid);
            Assert.True(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(
                rec, 200.0, false, GhostPlaybackLogic.RealVesselExistsForRecording));

            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => "fedcba9876543210fedcba9876543210");
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(
                rec, 200.0, false, GhostPlaybackLogic.RealVesselExistsForRecording));
        }

        // catches: the Space Center pass drawing the predicted tail beside the live vessel
        // (it had no such check), or FLIGHT drifting off the shared predicate.
        [Fact]
        public void FlightAndSpaceCenter_ReadTheSharedPredicate()
        {
            string flight = ReadSource("Controllers/ParsekFlightPlaybackHost.cs");
            string ksc = ReadSource("Controllers/ParsekKSC.cs");
            Assert.Contains("GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(", flight);
            Assert.Contains("GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(", ksc);
            // The KSC pass refreshes the live-vessel set the predicate reads once per frame.
            Assert.Contains("GhostPlaybackLogic.InvalidateVesselCache();", ksc);
        }

        // The map presence (FLIGHT map view and the Tracking Station) already covers the tail:
        // a non-loop recording whose launch-matched vessel exists is materialized, and the
        // gate refuses its map ghost. A loop member in its window still draws.
        [Fact]
        public void MapPresence_MaterializedGate_CoversThePredictedTail()
        {
            Recording rec = Stage();
            bool materialized = GhostMapPresence.IsTrackingStationRecordingMaterialized(rec, realVesselExists: true);
            Assert.True(materialized);

            Assert.True(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                rec, rec.RecordingId, materialized, loopMemberInWindow: false,
                liveLaunchMatchedAnchorOfActiveMember: false, out string skip, out _));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, skip);

            Assert.False(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                rec, rec.RecordingId, materialized, loopMemberInWindow: true,
                liveLaunchMatchedAnchorOfActiveMember: false, out _, out _));
        }

        private static string ReadSource(string file)
        {
            string srcRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek"));
            return SourceScanText.StripCSharpComments(
                File.ReadAllText(Path.Combine(srcRoot, file)).Replace("\r\n", "\n"));
        }
    }
}
