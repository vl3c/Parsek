using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A flight whose kerbal boards, takes a seat on, docks with or moves cargo to a vessel
    /// outside the flight is player work, never idle-on-pad or a pad failure, even when
    /// nothing moved 30 m (owner ruling 2026-10-25: rocket V idle on the pad, kerbal K steps
    /// out and interacts with vessel A; the flight is kept, V's recording as K's origin). A
    /// board or dock back onto the flight's own vessel is not an interaction.
    /// </summary>
    [Collection("Sequential")]
    public class ForeignInteractionNotIdleTests : IDisposable
    {
        private const uint RocketPid = 100u;
        private const uint ForeignPid = 900u;
        private readonly List<string> logLines = new List<string>();

        public ForeignInteractionNotIdleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekFlight.HeldKerbalProbeForTesting = name => false;
        }

        public void Dispose()
        {
            ParsekFlight.HeldKerbalProbeForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Rec(string id, string name, uint pid, string evaCrew, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = name,
                VesselPersistentId = pid,
                StartSituation = "Prelaunch",
                EvaCrewName = evaCrew,
                MaxDistanceFromLaunch = 6.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" });
            return rec;
        }

        // Rocket V idle on the pad, Bill steps out (EVA split), then a merge of Bill's EVA onto
        // the vessel with pid targetPid (child: that vessel flying on with Bill aboard).
        private static RecordingTree PadRocketEvaThenMerge(BranchPointType mergeType, uint targetPid,
            bool shortFlight = false)
        {
            double end = shortFlight ? 104.0 : 160.0;
            var tree = new RecordingTree
            {
                Id = "tree-pad",
                TreeName = "Pad Rocket",
                Recordings = new Dictionary<string, Recording>(),
            };
            tree.Recordings["rocket"] = Rec("rocket", "Pad Rocket", RocketPid, null, 100.0, end);
            tree.Recordings["rocket-after"] = Rec("rocket-after", "Pad Rocket", RocketPid, null, 101.0, end);
            tree.Recordings["bill"] = Rec("bill", "Bill Kerman", 555u, "Bill Kerman", 101.0, 102.0);
            uint childPid = targetPid == 0 ? RocketPid : targetPid;
            tree.Recordings["merged"] = Rec("merged", "Other", childPid, null, 102.0, end);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-eva",
                UT = 101.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { "rocket" },
                ChildRecordingIds = new List<string> { "rocket-after", "bill" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-merge",
                UT = 102.0,
                Type = mergeType,
                TargetVesselPersistentId = targetPid,
                ParentRecordingIds = new List<string> { "bill" },
                ChildRecordingIds = new List<string> { "merged" },
            });
            return tree;
        }

        [Fact]
        public void IsTreeIdleOnPad_BoardOntoAForeignVessel_NotIdle()
        {
            Assert.False(ParsekFlight.IsTreeIdleOnPad(PadRocketEvaThenMerge(BranchPointType.Board, ForeignPid)));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - Board with a vessel outside the flight (bp=bp-merge targetPid=900)")
                && l.Contains("tree='Pad Rocket'"));
        }

        // The seat of a foreign rover is a Dock merge with the rover stamped as its partner.
        [Fact]
        public void IsTreeIdleOnPad_DockOntoAForeignVessel_NotIdle()
        {
            Assert.False(ParsekFlight.IsTreeIdleOnPad(PadRocketEvaThenMerge(BranchPointType.Dock, ForeignPid)));
        }

        // catches: the veto reading a board back into the kerbal's own rocket as an interaction.
        [Fact]
        public void IsTreeIdleOnPad_BoardBackIntoTheOwnRocket_StaysIdle()
        {
            Assert.True(ParsekFlight.IsTreeIdleOnPad(PadRocketEvaThenMerge(BranchPointType.Board, RocketPid)));
            Assert.DoesNotContain(logLines, l => l.Contains("vessel outside the flight"));
        }

        [Fact]
        public void IsTreeIdleOnPad_BoardWithNoStampedTarget_StaysIdle()
        {
            Assert.True(ParsekFlight.IsTreeIdleOnPad(PadRocketEvaThenMerge(BranchPointType.Board, 0u)));
        }

        [Fact]
        public void IsTreeIdleOnPad_CargoMovedToAForeignVessel_NotIdle()
        {
            RecordingTree tree = PadRocketEvaThenMerge(BranchPointType.Board, 0u);
            tree.Recordings["rover"] = Rec("rover", "Rover", ForeignPid, null, 103.0, 160.0);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-cargo",
                UT = 103.0,
                Type = BranchPointType.InventoryTransfer,
                ParentRecordingIds = new List<string> { "bill" },
                ChildRecordingIds = new List<string> { "rover" },
            });

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("not idle - InventoryTransfer with a vessel outside the flight (bp=bp-cargo"));
        }

        [Fact]
        public void IsTreePadFailure_ShortFlightBoardingAForeignVessel_NotPadFailure()
        {
            Assert.True(ParsekFlight.IsTreePadFailure(
                PadRocketEvaThenMerge(BranchPointType.Board, RocketPid, shortFlight: true)));
            Assert.False(ParsekFlight.IsTreePadFailure(
                PadRocketEvaThenMerge(BranchPointType.Board, ForeignPid, shortFlight: true)));
            Assert.Contains(logLines, l => l.Contains("IsTreePadFailure: not idle - Board with a vessel outside the flight"));
        }

        [Fact]
        public void FindForeignVesselInteraction_NullAndEmptyTrees()
        {
            Assert.Null(ParsekFlight.FindForeignVesselInteraction(null));
            Assert.Null(ParsekFlight.FindForeignVesselInteraction(new RecordingTree()));
        }
    }
}
