using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ParsekFlight.FindBackgroundRecordingForVessel — the pure static method
    /// that locates a committed recording covering a given UT for a vessel PID.
    /// Used by chain ghost trajectory playback (Task 6b-4).
    /// </summary>
    [Collection("Sequential")]
    public class ChainGhostTrajectoryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainGhostTrajectoryTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        /// <summary>
        /// Creates a recording with trajectory points spanning the given UT range.
        /// Points are spaced 10s apart from startUT to endUT.
        /// </summary>
        static Recording MakeRecordingWithPoints(string id, uint vesselPid,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                Points = new List<TrajectoryPoint>()
            };

            // Generate points every 10 seconds from startUT to endUT
            for (double ut = startUT; ut <= endUT; ut += 10.0)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 75.0,
                    bodyName = "Kerbin"
                });
            }

            // Ensure endUT is included as the last point
            if (rec.Points.Count > 0 && rec.Points[rec.Points.Count - 1].ut < endUT)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 75.0,
                    bodyName = "Kerbin"
                });
            }

            return rec;
        }

        /// <summary>
        /// Creates a recording with no trajectory points (empty Points list).
        /// </summary>
        static Recording MakeRecordingNoPoints(string id, uint vesselPid,
            double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                Points = new List<TrajectoryPoint>()
            };
        }

        #endregion

        #region FindBackgroundRecordingForVessel — core behavior

        /// <summary>
        /// UT falls within recording range. Returns the recording.
        /// Guards: basic in-range lookup.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_InRange_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// UT is outside recording range. Returns null.
        /// Guards: out-of-range rejection.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_OutOfRange_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1200);

            Assert.Null(result);
        }

        /// <summary>
        /// Recording matches UT but wrong vessel PID. Returns null.
        /// Guards: PID filtering.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_WrongVesselPid_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 200, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Recording matches PID and UT but has empty Points list. Returns null.
        /// Guards: trajectory data required.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_EmptyPointsList_ReturnsNull()
        {
            var rec = MakeRecordingNoPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Null recordings list. Returns null without NullReferenceException.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_NullRecordings_ReturnsNull()
        {
            var result = ParsekFlight.FindBackgroundRecordingForVessel(null, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Multiple recordings cover the same UT for the same vessel.
        /// Returns the first one found (list order).
        /// Guards: deterministic selection.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_MultipleCandidates_ReturnsFirst()
        {
            var rec1 = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var rec2 = MakeRecordingWithPoints("bg-2", 100, 1020, 1120);
            var recordings = new List<Recording> { rec1, rec2 };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// Chain-scoped lookup must prefer the tree that actually owns the chain link,
        /// not an overlapping alternate-history recording with the same PID.
        /// Guards: same-PID historical reuse does not steal chain trajectory playback.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PrefersClaimingTreeOverAlternateHistory()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            altRec.TreeId = "tree-alt";

            var chainRec = MakeRecordingWithPoints("bg-chain", 100, 1060, 1120);
            chainRec.TreeId = "tree-1";

            var recordings = new List<Recording> { altRec, chainRec };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, chain, 1070);

            Assert.NotNull(result);
            Assert.Equal("bg-chain", result.RecordingId);
        }

        /// <summary>
        /// When a later tree extends the same chain, lookup should advance to the
        /// most recent claim tree once its claim UT has been reached.
        /// Guards: cross-tree continuation picks the newest link's trajectory.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PrefersMostRecentClaimTreeAtCurrentUT()
        {
            var tree1Rec = MakeRecordingWithPoints("bg-tree-1", 100, 1060, 1300);
            tree1Rec.TreeId = "tree-1";

            var tree2Rec = MakeRecordingWithPoints("bg-tree-2", 100, 1260, 1320);
            tree2Rec.TreeId = "tree-2";

            var recordings = new List<Recording> { tree1Rec, tree2Rec };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-2"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });
            chain.Links.Add(new ChainLink
            {
                recordingId = "R2",
                treeId = "tree-2",
                ut = 1260,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, chain, 1270);

            Assert.NotNull(result);
            Assert.Equal("bg-tree-2", result.RecordingId);
        }

        /// <summary>
        /// Chain ghosts are active from rewind, not from first claim UT. If the
        /// claiming tree has no pre-claim trajectory for the claimed vessel, lookup
        /// must fall back to any committed recording that actually covers the UT.
        /// Guards: foreign-merge chains still have a trajectory source before claim.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_FallsBackToGlobalHistoryBeforeFirstClaim()
        {
            var preClaimRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            preClaimRec.TreeId = "tree-alt";

            var postClaimRec = MakeRecordingWithPoints("bg-chain", 100, 1060, 1120);
            postClaimRec.TreeId = "tree-1";

            var recordings = new List<Recording> { preClaimRec, postClaimRec };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, chain, 1030);

            Assert.NotNull(result);
            Assert.Equal("bg-alt", result.RecordingId);
        }

        /// <summary>
        /// UT equals exactly the recording's StartUT. Returns the recording.
        /// Guards: inclusive start boundary.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ExactStartUT_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1000);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// UT equals exactly the recording's EndUT. Returns the recording.
        /// Guards: inclusive end boundary.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ExactEndUT_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1100);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        #endregion

        #region FindBackgroundRecordingForVessel — edge cases

        /// <summary>
        /// UT is just before StartUT (one epsilon below). Returns null.
        /// Guards: strict lower bound.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_JustBeforeStart_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 999.999);

            Assert.Null(result);
        }

        /// <summary>
        /// UT is just after EndUT (one epsilon above). Returns null.
        /// Guards: strict upper bound.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_JustAfterEnd_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1100.001);

            Assert.Null(result);
        }

        /// <summary>
        /// Empty recordings list. Returns null.
        /// Guards: empty list safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_EmptyList_ReturnsNull()
        {
            var result = ParsekFlight.FindBackgroundRecordingForVessel(
                new List<Recording>(), 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Multiple recordings for different vessels — only the matching PID is returned.
        /// Guards: vessel isolation.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_MixedVessels_ReturnsCorrectPid()
        {
            var rec1 = MakeRecordingWithPoints("bg-ship", 100, 1000, 1100);
            var rec2 = MakeRecordingWithPoints("bg-station", 200, 1000, 1100);
            var recordings = new List<Recording> { rec1, rec2 };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 200, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-station", result.RecordingId);
        }

        /// <summary>
        /// Recording has null Points list. Returns null.
        /// Guards: null Points safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_NullPointsList_ReturnsNull()
        {
            var rec = new Recording
            {
                RecordingId = "bg-null-pts",
                VesselPersistentId = 100,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 1100,
                Points = null
            };
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Vessel PID = 0 matches recording with PID = 0.
        /// Guards: zero PID is valid for FindBackgroundRecordingForVessel
        /// (filtering out zero is the caller's responsibility).
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ZeroPid_MatchesZeroPidRecording()
        {
            var rec = MakeRecordingWithPoints("bg-zero", 0, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 0, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-zero", result.RecordingId);
        }

        #endregion
    }
}
