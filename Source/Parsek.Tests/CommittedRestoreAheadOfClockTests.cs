using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// VN-5 (2026-11-01 nightly, INV2-NO-DOUBLE-COVER): after a stock Revert to Launch the pad
    /// craft (same pid, same launch guid) matched its own committed pad recording, whose data
    /// ran past the reverted clock, and resumed it in place - recording behind the
    /// recording's own end. The committed-spawned restore now refuses a recording that
    /// already holds data later than the live clock.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedRestoreAheadOfClockTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedRestoreAheadOfClockTests()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        private static Recording PadRecording(double sectionStart, double sectionEnd)
        {
            return new Recording
            {
                RecordingId = "9f993cda",
                VesselName = "GS1 Auto-Chute Booster",
                VesselPersistentId = 2200110033,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = sectionStart, bodyName = "Kerbin" },
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection { referenceFrame = ReferenceFrame.Absolute, startUT = sectionStart, endUT = sectionEnd },
                },
            };
        }

        private static RecordingTree TreeWith(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = "5d83cae0",
                TreeName = rec.VesselName,
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = null,
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        [Fact]
        public void AheadOfClock_RecordedDataPastLiveUT_True()
        {
            // The VN-5 shape: committed pad recording to 26.88, reverted clock 25.64.
            Assert.True(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(PadRecording(26.80, 26.88), 25.64));
        }

        [Fact]
        public void AheadOfClock_ClockAtOrPastEnd_False()
        {
            Recording rec = PadRecording(26.80, 26.88);
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(rec, 26.88));
            // Inside the float-noise slack (a save written in the frame that ended it).
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(rec, 26.875));
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(rec, 400.0));
        }

        [Fact]
        public void AheadOfClock_NaNClockOrNoData_False()
        {
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(PadRecording(26.80, 26.88), double.NaN));
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(new Recording { RecordingId = "empty" }, 10.0));
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(null, 10.0));
        }

        [Fact]
        public void AheadOfClock_PredictedOrbitTailIgnored_RecordedOrbitCounted()
        {
            // A predicted tail is extrapolation past the scene exit, not something the vessel
            // did: a vessel flown again inside it is not behind its recording.
            Recording rec = PadRecording(100.0, 200.0);
            rec.OrbitSegments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 200.0, endUT = 5000.0, bodyName = "Kerbin", isPredicted = true },
            };
            Assert.False(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(rec, 300.0));

            rec.OrbitSegments.Add(new OrbitSegment { startUT = 200.0, endUT = 900.0, bodyName = "Kerbin", isPredicted = false });
            Assert.True(ParsekFlight.IsCommittedRecordingAheadOfLiveClock(rec, 300.0));
        }

        [Fact]
        public void TryFind_RevertedPadCraftBehindItsRecording_NotMatched()
        {
            var tree = TreeWith(PadRecording(26.80, 26.88));

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 2200110033,
                out RecordingTree matched,
                out string matchedId,
                activeVesselGuid: null,
                liveUT: 25.64);

            Assert.False(found);
            Assert.Null(matched);
            Assert.Null(matchedId);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("TryFindCommittedTreeForSpawnedVessel: refused 1 committed recording(s)")
                && l.Contains("pid=2200110033")
                && l.Contains("ut=25.64"));
        }

        [Fact]
        public void TryFind_ClockPastRecordingEnd_StillMatches()
        {
            // Mirror: the guard only refuses a vessel behind its recording; one that comes
            // back to it later (the normal fly-again resume) still resumes.
            var tree = TreeWith(PadRecording(26.80, 26.88));

            Assert.True(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 2200110033,
                out RecordingTree matched,
                out string matchedId,
                activeVesselGuid: null,
                liveUT: 30.0));
            Assert.Same(tree, matched);
            Assert.Equal("9f993cda", matchedId);

            // And a caller with no clock keeps today's behavior.
            Assert.True(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 2200110033,
                out _,
                out _));
        }
    }
}
