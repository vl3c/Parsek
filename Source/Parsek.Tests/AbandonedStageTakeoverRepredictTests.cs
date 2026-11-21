using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Flight asr-final3 (lane ASR-6): the probe core was split by the optimizer into a head
    /// (acb64ead, atmosphere) and a tail (8cb643ad, space), the Tracking Station Fly took the
    /// tail over (predicted ending dropped) and opened a standalone continuation segment
    /// (2707d948) on the same pid, the vessel the player then flew. The next in-flight commit
    /// ran the lifetime finalizer on BOTH rows and re-predicted a Destroyed ending: the head
    /// because its section-based end (the cut, 173.74) read later than the tail's first sample
    /// (168.4, before the tail's own section start), so it looked recorded past its successor;
    /// the tail because nothing asked whether its vessel lives on in the tree.
    /// </summary>
    [Collection("Sequential")]
    public class AbandonedStageTakeoverRepredictTests : IDisposable
    {
        private const uint ProbePid = 2167733714;
        private const string ProbeGuid = "3a814f769b98415090719caff2822836";
        private const string OtherLaunchGuid = "270d1da5000040008000000000000002";
        private const string ChainId = "e4267a65b6354678ae946113bb0ec3a5";
        private const double CutUT = 173.7424560547023;
        private const double SharedSampleUT = 168.44000000001236;
        private const double SwitchUT = 280.63224487305575;
        private const double CommitUT = 300.0;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> finalizerRuns = new List<string>();
        private int situationStamps;

        public AbandonedStageTakeoverRepredictTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT,
                    out IncompleteBallisticFinalizationResult result) =>
                {
                    finalizerRuns.Add(recording.RecordingId);
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 428.507,
                    };
                    return true;
                };
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Pt(double ut, double alt) =>
            new TrajectoryPoint { ut = ut, altitude = alt, bodyName = "Kerbin" };

        private sealed class Fixture
        {
            public RecordingTree Tree;
            public Recording Head;
            public Recording Tail;
            public Recording Segment;
            public Vessel Probe;
            public Vessel FindVessel(uint pid) => pid == ProbePid ? Probe : null;
        }

        private static Fixture Build()
        {
            var tree = new RecordingTree
            {
                Id = "b5d8f38823084ac781a38a0377f6188e",
                TreeName = "Kerbal X",
                ActiveRecordingId = "2707d948-a6c9-40ef-bd9d-284769d43aa9",
            };

            // Head: the optimizer's first half. Its one section was trimmed at the cut; its
            // last sample is the shared 168.44 one.
            var headFrames = new List<TrajectoryPoint> { Pt(132.5, 39760.0), Pt(SharedSampleUT, 64000.0) };
            var head = new Recording
            {
                RecordingId = "acb64eadf6304fbfb77ceedc1aec656d",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = ProbePid,
                RecordedVesselGuid = ProbeGuid,
                ChainId = ChainId,
                ChainIndex = 0,
                ExplicitStartUT = 132.5,
                ExplicitEndUT = SharedSampleUT,
            };
            head.Points.AddRange(headFrames);
            head.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = 132.5,
                endUT = CutUT,
                frames = headFrames,
            });

            // Tail: the second half, taken over (terminal dropped). Its space section starts at
            // the cut but opens on the shared 168.44 sample.
            var tailFrames = new List<TrajectoryPoint>
            {
                Pt(SharedSampleUT, 64000.0), Pt(CutUT, 67000.0), Pt(176.5, 68000.0),
            };
            var tail = new Recording
            {
                RecordingId = "8cb643ad464a45a1b0cf2d0cf281292f",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = ProbePid,
                RecordedVesselGuid = ProbeGuid,
                ChainId = ChainId,
                ChainIndex = 1,
            };
            tail.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Background,
                startUT = CutUT,
                endUT = 176.5,
                frames = tailFrames,
            });
            tail.Points.AddRange(tailFrames);
            tail.Points.Add(Pt(280.052, 94210.0));
            tail.Points.Add(Pt(SwitchUT, 94135.0));

            // The standalone continuation segment the TS Fly opened on the same vessel.
            var segment = new Recording
            {
                RecordingId = "2707d948-a6c9-40ef-bd9d-284769d43aa9",
                TreeId = tree.Id,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = ProbePid,
                RecordedVesselGuid = ProbeGuid,
            };
            segment.Points.Add(Pt(SwitchUT, 94135.0));
            segment.Points.Add(Pt(CommitUT, 95000.0));

            tree.Recordings[head.RecordingId] = head;
            tree.Recordings[tail.RecordingId] = tail;
            tree.Recordings[segment.RecordingId] = segment;

            var probe = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            probe.persistentId = ProbePid;
            probe.situation = Vessel.Situations.SUB_ORBITAL;
            return new Fixture { Tree = tree, Head = head, Tail = tail, Segment = segment, Probe = probe };
        }

        private ParsekFlight.FinalizationLiveVesselAccess Access() =>
            new ParsekFlight.FinalizationLiveVesselAccess(
                isFound: v => !ReferenceEquals(v, null),
                determineTerminalState: v => { situationStamps++; return TerminalState.SubOrbital; },
                captureTerminalOrbit: (r, v) => { },
                captureTerminalPosition: (r, v) => { },
                tryBackupSnapshot: v => null,
                tryRefreshStableTerminalSnapshot: (r, v, s, p) => false,
                findLaterSpawnOwner: (r, v) => null,
                isRecordedIdentityLost: (r, v) => false);

        // ------------------------------------------------------------------ the head row

        [Fact]
        public void Head_SuccessorOpensOnTheSharedSample_IsANonFinalChainSegment()
        {
            Fixture fx = Build();
            Assert.True(fx.Head.EndUT > fx.Tail.StartUT); // the flight shape
            Assert.True(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out string successor));
            Assert.Equal(fx.Tail.RecordingId, successor);
        }

        // Mirror: a head this session recorded past the cut is still flown, not replayed.
        [Fact]
        public void Head_RecordedPastTheCut_IsNotANonFinalChainSegment()
        {
            Fixture fx = Build();
            fx.Head.Points.Add(Pt(CutUT + 30.0, 80000.0));
            Assert.False(ParsekFlight.IsNonFinalChainSegmentInTree(fx.Head, fx.Tree, out _));
        }

        [Fact]
        public void ChainSuccessorBoundaryUT_ReadsTheFirstSectionStartWhenLater()
        {
            Fixture fx = Build();
            Assert.Equal(CutUT, ParsekFlight.ChainSuccessorBoundaryUT(fx.Tail), 9);
            Assert.Equal(SwitchUT, ParsekFlight.ChainSuccessorBoundaryUT(fx.Segment), 9);
        }

        [Fact]
        public void InFlightCommit_Head_IsNotFinalizedAndGetsNoPrediction()
        {
            Fixture fx = Build();

            ParsekFlight.FinalizeIndividualRecording(
                fx.Head, CommitUT, isSceneExit: false, treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel, liveVesselAccess: Access());

            Assert.DoesNotContain(fx.Head.RecordingId, finalizerRuns);
            Assert.False(fx.Head.TerminalStateValue.HasValue);
            Assert.False(fx.Head.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("[Flight]") && l.Contains("non-final chain segment")
                && l.Contains("successor=" + fx.Tail.RecordingId));
        }

        // ------------------------------------------------------------------ the taken-over row

        [Fact]
        public void Tail_WithAStandaloneSegmentOnTheSameVessel_HasAContinuation()
        {
            Fixture fx = Build();
            Assert.True(ParsekFlight.HasSameVesselContinuationInTree(fx.Tail, fx.Tree, out string id));
            Assert.Equal(fx.Segment.RecordingId, id);
        }

        [Fact]
        public void HasSameVesselContinuationInTree_Mirrors()
        {
            Fixture fx = Build();
            // The segment itself has nothing after it (the head and tail are earlier).
            Assert.False(ParsekFlight.HasSameVesselContinuationInTree(fx.Segment, fx.Tree, out _));

            // A relaunch of the same craft (baked pid, new guid) is not a continuation.
            fx.Segment.RecordedVesselGuid = OtherLaunchGuid;
            Assert.False(ParsekFlight.HasSameVesselContinuationInTree(fx.Tail, fx.Tree, out _));

            // A later same-launch row that starts inside the tail is not a continuation unless
            // it is the one being flown.
            Fixture fx2 = Build();
            fx2.Segment.Points.Insert(0, Pt(250.0, 93000.0));
            fx2.Tree.ActiveRecordingId = null;
            Assert.False(ParsekFlight.HasSameVesselContinuationInTree(fx2.Tail, fx2.Tree, out _));
            fx2.Tree.ActiveRecordingId = fx2.Segment.RecordingId;
            Assert.True(ParsekFlight.HasSameVesselContinuationInTree(fx2.Tail, fx2.Tree, out _));

            // No tree, no continuation.
            Assert.False(ParsekFlight.HasSameVesselContinuationInTree(fx.Tail, null, out _));
        }

        [Fact]
        public void InFlightCommit_TakenOverTail_KeepsTheSituationStampNotAPredictedCrash()
        {
            Fixture fx = Build();

            ParsekFlight.FinalizeIndividualRecording(
                fx.Tail, CommitUT, isSceneExit: false, treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel, liveVesselAccess: Access());

            Assert.DoesNotContain(fx.Tail.RecordingId, finalizerRuns);
            Assert.Equal(TerminalState.SubOrbital, fx.Tail.TerminalStateValue);
            Assert.False(fx.Tail.TerminalPredicted);
            Assert.Equal(1, situationStamps);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("of '" + fx.Tail.RecordingId + "' continues in '" + fx.Segment.RecordingId + "'"));
            Assert.DoesNotContain(logLines, l => l.Contains("unflown - running the lifetime finalizer")
                && l.Contains(fx.Tail.RecordingId));
        }

        // Mirror (B4 kept): the same row with no continuation in the tree is an abandoned
        // vessel and still gets the predicted ending.
        [Fact]
        public void InFlightCommit_TailWithoutContinuation_StillGetsThePredictedCrash()
        {
            Fixture fx = Build();
            fx.Tree.Recordings.Remove(fx.Segment.RecordingId);
            fx.Tree.ActiveRecordingId = "some-other-vessel";

            ParsekFlight.FinalizeIndividualRecording(
                fx.Tail, CommitUT, isSceneExit: false, treeContext: fx.Tree,
                findVesselByPid: fx.FindVessel, liveVesselAccess: Access());

            Assert.Contains(fx.Tail.RecordingId, finalizerRuns);
            Assert.Equal(TerminalState.Destroyed, fx.Tail.TerminalStateValue);
            Assert.True(fx.Tail.TerminalPredicted);
        }

        // The whole commit pass over the tree: neither probe row is re-predicted.
        [Fact]
        public void InFlightCommit_WholeTree_RepredictsNeitherProbeRow()
        {
            Fixture fx = Build();

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                fx.Tree, CommitUT, isSceneExit: false,
                findVesselByPid: fx.FindVessel,
                liveVesselAccess: Access());

            Assert.DoesNotContain(fx.Head.RecordingId, finalizerRuns);
            Assert.DoesNotContain(fx.Tail.RecordingId, finalizerRuns);
            Assert.False(fx.Head.TerminalPredicted);
            Assert.False(fx.Tail.TerminalPredicted);
            Assert.DoesNotContain(logLines, l => l.Contains("terminal predicted flag false->true"));
        }
    }
}
