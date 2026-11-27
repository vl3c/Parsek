using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The flight host's loop term (GhostPlaybackLogic.ResolveLoopingLike) and the BUG-B
    /// dormancy decision it feeds (todo MISSION-LOOP-DEBRIS-DORMANT-AS-HISTORICAL): booster
    /// debris that rides a looping parent's clock must not be parked as unreplayed history,
    /// while debris of a parent that does not loop stays dormant.
    /// </summary>
    public class LoopSyncedChildDormancyTests
    {
        private static Recording MakeRecording(string id, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = start, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = end, bodyName = "Kerbin" }
                }
            };
        }

        private static Recording MakeDebris(string id, int parentIdx)
        {
            Recording rec = MakeRecording(id, 20, 80);
            rec.IsDebris = true;
            rec.LoopSyncParentIdx = parentIdx;
            return rec;
        }

        private static GhostPlaybackLogic.LoopUnitSet UnitWithMembers(params int[] members)
        {
            var ownerByIndex = new Dictionary<int, int>();
            foreach (int m in members)
                ownerByIndex[m] = members[0];
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit>(), ownerByIndex);
        }

        [Fact]
        public void DebrisOfMissionUnitMember_IsLoopingLike_AndNotDormant()
        {
            var parent = MakeRecording("parent", 0, 110);
            var debris = MakeDebris("debris", 0);
            var committed = new List<Recording> { parent, debris };
            var units = UnitWithMembers(0);

            bool loopingLike = GhostPlaybackLogic.ResolveLoopingLike(
                debris, 1, false, committed, units, out bool child);

            Assert.True(loopingLike);
            Assert.True(child);
            // The render gate no longer parks it; its own end spawn still takes the gate.
            Assert.False(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike, inReFlySessionTree: false, scopeHistorical: true, forSpawn: false));
            Assert.True(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike, inReFlySessionTree: false, scopeHistorical: true, forSpawn: true));
        }

        [Fact]
        public void DebrisOfOwnLoopParent_IsLoopingLike()
        {
            var parent = MakeRecording("parent", 0, 110);
            parent.LoopPlayback = true;
            var debris = MakeDebris("debris", 0);
            var committed = new List<Recording> { parent, debris };

            Assert.True(GhostPlaybackLogic.ResolveLoopingLike(
                debris, 1, false, committed, GhostPlaybackLogic.LoopUnitSet.Empty, out bool child));
            Assert.True(child);
        }

        [Fact]
        public void DebrisOfParentThatDoesNotLoop_StaysDormant()
        {
            var parent = MakeRecording("parent", 0, 110);
            var debris = MakeDebris("debris", 0);
            var committed = new List<Recording> { parent, debris };
            // A unit exists, but for another recording.
            var units = UnitWithMembers(5);

            bool loopingLike = GhostPlaybackLogic.ResolveLoopingLike(
                debris, 1, false, committed, units, out bool child);

            Assert.False(loopingLike);
            Assert.False(child);
            Assert.True(GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                loopingLike, inReFlySessionTree: false, scopeHistorical: true, forSpawn: false));
        }

        [Fact]
        public void ParentWithLoopFlagButTooFewPoints_DoesNotExemptDebris()
        {
            // Mirrors the engine: a parent ShouldLoopPlayback refuses never drives the
            // debris on a loop clock, so the debris must not be exempted either.
            var parent = MakeRecording("parent", 0, 110);
            parent.LoopPlayback = true;
            parent.Points.RemoveAt(1);
            var debris = MakeDebris("debris", 0);
            var committed = new List<Recording> { parent, debris };

            Assert.False(GhostPlaybackLogic.ResolveLoopingLike(
                debris, 1, false, committed, GhostPlaybackLogic.LoopUnitSet.Empty, out bool child));
            Assert.False(child);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        [InlineData(1)]
        public void OutOfRangeOrSelfParentLink_IsIgnored(int parentIdx)
        {
            var parent = MakeRecording("parent", 0, 110);
            var debris = MakeDebris("debris", parentIdx);
            var committed = new List<Recording> { parent, debris };
            var units = UnitWithMembers(0);

            // Index 1 (the debris itself) is not a member; a link to itself or out of the
            // list never exempts it.
            Assert.False(GhostPlaybackLogic.ResolveLoopingLike(
                debris, 1, false, committed, units, out bool child));
            Assert.False(child);
        }

        [Fact]
        public void OwnTerms_StayLoopingLike_WithoutTheChildFlag()
        {
            var committed = new List<Recording> { MakeRecording("a", 0, 10), MakeRecording("b", 0, 10) };
            committed[0].LoopPlayback = true;

            Assert.True(GhostPlaybackLogic.ResolveLoopingLike(
                committed[0], 0, false, committed, GhostPlaybackLogic.LoopUnitSet.Empty, out bool c0));
            Assert.False(c0);
            Assert.True(GhostPlaybackLogic.ResolveLoopingLike(
                committed[1], 1, true, committed, GhostPlaybackLogic.LoopUnitSet.Empty, out bool c1));
            Assert.False(c1);
            Assert.True(GhostPlaybackLogic.ResolveLoopingLike(
                committed[1], 1, false, committed, UnitWithMembers(1), out bool c2));
            Assert.False(c2);
            Assert.False(GhostPlaybackLogic.ResolveLoopingLike(
                null, 0, true, committed, UnitWithMembers(0), out bool c3));
            Assert.False(c3);
        }

        [Fact]
        public void Summary_NamesTheCount()
        {
            Assert.Equal(
                "Loop-synced children on a looping parent: 6 recording(s) replay on the parent's loop clock (dormancy exempt)",
                ParsekFlightPlaybackHost.BuildLoopSyncedChildrenSummary(6));
        }
    }
}
