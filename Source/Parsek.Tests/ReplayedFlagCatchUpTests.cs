using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo FLAG-LOST-WHEN-REPLAY-HAD-NO-GHOST-2026-11-24: a recording's flags are planted once its
    /// window has passed even when no ghost was ever on screen for it, under the end spawn's
    /// replay-scope gate.
    /// </summary>
    [Collection("Sequential")]
    public class ReplayedFlagCatchUpTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> spawned = new List<string>();
        private readonly HashSet<string> standing = new HashSet<string>();
        private readonly HashSet<string> failing = new HashSet<string>();

        public ReplayedFlagCatchUpTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            GhostPlaybackLogic.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.SetFlagExistsOverrideForTesting(evt => standing.Contains(evt.flagSiteName));
            GhostPlaybackLogic.SetSpawnFlagOverrideForTesting(evt =>
            {
                if (failing.Contains(evt.flagSiteName))
                    return false;
                spawned.Add(evt.flagSiteName);
                standing.Add(evt.flagSiteName);
                return true;
            });
            GhostPlaybackLogic.SetBodyRadiusOverrideForTesting(_ => KerbinRadius);
            GhostPlaybackLogic.SetRemoveStandingFlagOverrideForTesting(t =>
                standing.Remove(t.FlagName) ? 1 : 0);
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static FlagEvent Flag(string name, double ut)
        {
            return new FlagEvent
            {
                ut = ut, flagSiteName = name, placedBy = "Jebediah Kerman", bodyName = "Kerbin",
                latitude = -0.0972, longitude = -74.5577, altitude = 70.0,
            };
        }

        // The WF-1 shape: an EVA kerbal's recording, terminal Boarded, window [64, 80], plant at 70.
        private static Recording KerbalRecording(params FlagEvent[] flags)
        {
            return new Recording
            {
                RecordingId = "rec-kerbal",
                VesselName = "Jebediah Kerman",
                TerminalStateValue = TerminalState.Boarded,
                ExplicitStartUT = 64.0,
                ExplicitEndUT = 80.0,
                FlagEvents = new List<FlagEvent>(flags),
            };
        }

        [Fact]
        public void WindowPassedWithNoGhost_PlantsTheFlagOnce()
        {
            Recording rec = KerbalRecording(Flag("Pad Flag", 70.0));
            var catchUp = new ReplayedFlagCatchUp("ksc");

            Assert.False(catchUp.Tick(rec, 63.0, replays: true));
            Assert.False(catchUp.Tick(rec, 79.0, replays: true));
            Assert.Empty(spawned);

            Assert.True(catchUp.Tick(rec, 900.0, replays: true));
            Assert.Equal(new[] { "Pad Flag" }, spawned);
            Assert.Contains(logLines, l => l.Contains("[INFO][FlagReplay]")
                && l.Contains("Flag catch-up: rec=rec-kerbal host=ksc currentUT=900 endUT=80 eligible=1 planted=1 "
                    + "alreadyPresent=0 removedByPlayer=0 failed=0"));

            // Once per scene, and a later scene finds the flag standing.
            Assert.False(catchUp.Tick(rec, 901.0, replays: true));
            Assert.True(new ReplayedFlagCatchUp("flight").Tick(rec, 950.0, replays: true));
            Assert.Single(spawned);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][FlagReplay]")
                && l.Contains("host=flight") && l.Contains("planted=0 alreadyPresent=1"));
        }

        [Fact]
        public void HistoricalRecording_IsNeverWalked()
        {
            // Plain forward play: the playhead was never before the recording's start, and the
            // player has removed the live flag in a way no tombstone recorded.
            Recording rec = KerbalRecording(Flag("Pad Flag", 70.0));
            PlaybackScopeTracker.NotePlayhead(rec.RecordingId, 900.0, 64.0);
            bool historical = PlaybackScopeTracker.IsHistoricalNeverReplayed(rec.RecordingId, 900.0, 64.0);
            Assert.True(historical);

            Assert.False(new ReplayedFlagCatchUp("ksc").Tick(rec, 900.0, replays: !historical));
            Assert.Empty(spawned);

            // The same recording after a rewind to before its start is in scope and is walked.
            PlaybackScopeTracker.NotePlayhead(rec.RecordingId, 46.0, 64.0);
            historical = PlaybackScopeTracker.IsHistoricalNeverReplayed(rec.RecordingId, 900.0, 64.0);
            Assert.False(historical);
            Assert.True(new ReplayedFlagCatchUp("ksc").Tick(rec, 900.0, replays: !historical));
            Assert.Single(spawned);
        }

        [Fact]
        public void FlagThePlayerRemoved_IsNotPlantedAgain()
        {
            FlagEvent f = Flag("Pad Flag", 70.0);
            RemovedReplayFlags.RecordRemoval(new RemovedReplayFlags.Tombstone
            {
                BodyName = f.bodyName, BodyRadius = KerbinRadius, Latitude = f.latitude,
                Longitude = f.longitude, Altitude = f.altitude, RemovedUT = 300.0,
                FlagName = f.flagSiteName, Source = "test",
            });

            Assert.True(new ReplayedFlagCatchUp("ksc").Tick(KerbalRecording(f), 900.0, replays: true));
            Assert.Empty(spawned);
            Assert.Contains(logLines, l => l.Contains("Flag catch-up:") && l.Contains("removedByPlayer=1"));
        }

        [Fact]
        public void PickUpPastTheEnd_WaitsForItsUT_ThenNeverPlantsTheFlag()
        {
            // A pick-up recorded after the last sample: the walk must not run between the two.
            FlagEvent f = Flag("Pad Flag", 70.0);
            Recording rec = KerbalRecording(f);
            rec.SegmentEvents = new List<SegmentEvent>
            {
                new SegmentEvent
                {
                    type = SegmentEventType.FlagPickedUp, ut = 80.4,
                    details = FlagPickUp.FormatDetails(f.bodyName, f.latitude, f.longitude, f.altitude, f.flagSiteName),
                },
            };
            var catchUp = new ReplayedFlagCatchUp("flight");

            Assert.Equal(80.4, ReplayedFlagCatchUp.ResolveDueUT(rec));
            Assert.False(catchUp.Tick(rec, 80.2, replays: true));
            Assert.Empty(spawned);
            Assert.True(catchUp.Tick(rec, 81.0, replays: true));
            Assert.Empty(spawned);
            Assert.Contains(logLines, l => l.Contains("Flag pick-up replayed: 'Pad Flag' on Kerbin"));
        }

        [Fact]
        public void PickUpOnlyRecording_TakesDownTheStandingFlag()
        {
            FlagEvent f = Flag("Pad Flag", 70.0);
            standing.Add("Pad Flag");
            Recording rec = KerbalRecording();
            rec.SegmentEvents = new List<SegmentEvent>
            {
                new SegmentEvent
                {
                    type = SegmentEventType.FlagPickedUp, ut = 75.0,
                    details = FlagPickUp.FormatDetails(f.bodyName, f.latitude, f.longitude, f.altitude, f.flagSiteName),
                },
            };

            Assert.True(ReplayedFlagCatchUp.HasFlagHistory(rec));
            Assert.True(new ReplayedFlagCatchUp("flight").Tick(rec, 900.0, replays: true));
            Assert.Empty(standing);
        }

        [Fact]
        public void RecordingWithNoFlagHistory_RunsNoWalkAndLogsNothing()
        {
            Recording rec = KerbalRecording();
            Assert.False(ReplayedFlagCatchUp.HasFlagHistory(rec));
            Assert.False(new ReplayedFlagCatchUp("ksc").Tick(rec, 900.0, replays: true));
            Assert.DoesNotContain(logLines, l => l.Contains("Flag catch-up"));
        }

        [Fact]
        public void FailedPlant_RetriesThenGivesUpForTheScene()
        {
            Recording rec = KerbalRecording(Flag("Pad Flag", 70.0));
            failing.Add("Pad Flag");
            var catchUp = new ReplayedFlagCatchUp("ksc");
            for (int i = 0; i < ReplayedFlagCatchUp.MaxAttemptsPerScene; i++)
                Assert.True(catchUp.Tick(rec, 900.0 + i, replays: true));
            Assert.False(catchUp.Tick(rec, 910.0, replays: true));
            Assert.Contains(logLines, l => l.Contains("[WARN][FlagReplay]")
                && l.Contains("Flag catch-up gave up in this scene: rec=rec-kerbal host=ksc failed=1 attempts=3"));

            // A transient failure recovers on the next frame.
            var next = new ReplayedFlagCatchUp("ksc");
            Assert.True(next.Tick(rec, 920.0, replays: true));
            failing.Clear();
            Assert.True(next.Tick(rec, 920.1, replays: true));
            Assert.Equal(new[] { "Pad Flag" }, spawned);
        }

        [Fact]
        public void ClockWoundBackInsideTheScene_OwesTheWalkAgain()
        {
            Recording rec = KerbalRecording(Flag("Pad Flag", 70.0));
            var catchUp = new ReplayedFlagCatchUp("ksc");
            Assert.True(catchUp.Tick(rec, 900.0, replays: true));
            standing.Clear();
            Assert.False(catchUp.Tick(rec, 10.0, replays: true));
            Assert.True(catchUp.Tick(rec, 900.0, replays: true));
            Assert.Equal(2, spawned.Count);
        }

        [Theory]
        [InlineData(0.5, "Other", false, true)]   // within 1 m: any flag, as every walk
        [InlineData(4.0, "Pad Flag", false, false)]
        [InlineData(4.0, "Pad Flag", true, true)]  // toppled 2 m away since it was planted
        [InlineData(4.0, "Other", true, false)]    // a different flag 2 m away
        [InlineData(9.0, "Pad Flag", true, true)]
        [InlineData(9.1, "Pad Flag", true, false)]
        public void StandingFlagMatch(double distanceSquared, string name, bool settled, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.IsStandingFlagOfEvent(
                distanceSquared, name, Flag("Pad Flag", 70.0), settled));
        }

        [Fact]
        public void UnnamedFlag_MatchesTheNameItIsPlantedUnder()
        {
            Assert.Equal("Flag", GhostVisualBuilder.FlagVesselNameOf(Flag("", 70.0)));
            Assert.True(GhostPlaybackLogic.IsStandingFlagOfEvent(4.0, "Flag", Flag(null, 70.0), true));
        }

        [Fact]
        public void FlightGate_IsTheEndSpawnsGateWithoutTheVessel()
        {
            Assert.True(ParsekFlightPlaybackHost.ReplaysItsFlags(new GhostCommNetEligibilityInput(), false));
            Assert.False(ParsekFlightPlaybackHost.ReplaysItsFlags(new GhostCommNetEligibilityInput(), true));
            Assert.False(ParsekFlightPlaybackHost.ReplaysItsFlags(
                new GhostCommNetEligibilityInput { HistoricalNeverReplayed = true }, false));
            Assert.False(ParsekFlightPlaybackHost.ReplaysItsFlags(
                new GhostCommNetEligibilityInput { SupersededByRelation = true }, false));
            Assert.False(ParsekFlightPlaybackHost.ReplaysItsFlags(
                new GhostCommNetEligibilityInput { RewindRetired = true }, false));
            Assert.False(ParsekFlightPlaybackHost.ReplaysItsFlags(
                new GhostCommNetEligibilityInput { SessionSuppressed = true }, false));
        }

        [Fact]
        public void BothHosts_AskForEveryRecording()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string ksc = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Controllers", "ParsekKSC.cs"));
            int inactive = ksc.IndexOf("inactive timeline reason=", StringComparison.Ordinal);
            int call = ksc.IndexOf("flagCatchUp.Tick(rec, currentUT,", StringComparison.Ordinal);
            int dispatch = ksc.IndexOf("KscStructuralDispatch structuralDispatch = DecideKscStructuralDispatch(", StringComparison.Ordinal);
            Assert.True(inactive > 0 && call > inactive && dispatch > call,
                "the Space Center must ask after the timeline-inactive exit and before any draw decision");
            Assert.Contains("replays: !historicalNeverReplayed", ksc.Substring(call, dispatch - call));

            string flight = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Controllers", "ParsekFlightPlaybackHost.cs"));
            int engine = flight.IndexOf("host.engine.UpdatePlayback(cachedTrajectories, flags, ctx);", StringComparison.Ordinal);
            int drive = flight.IndexOf("DriveFlagCatchUp(committed, flags, currentUT, activeReFlyMarker);", StringComparison.Ordinal);
            Assert.True(engine > 0 && drive > engine, "flight must ask after the engine's own walks");
        }
    }
}
