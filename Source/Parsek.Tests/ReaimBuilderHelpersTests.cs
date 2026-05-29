using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3c of re-aim: the pure MissionLoopUnitBuilder helper that gathers loop members' orbit
    // segments for re-aim classification. Guards the startUT ordering + null/range safety the
    // classifier depends on.
    public class ReaimBuilderHelpersTests
    {
        private static Recording RecWithSegs(string id, params OrbitSegment[] segs)
        {
            var rec = new Recording { RecordingId = id };
            for (int i = 0; i < segs.Length; i++)
                rec.OrbitSegments.Add(segs[i]);
            return rec;
        }

        private static OrbitSegment Seg(string body, double start, double end)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, semiMajorAxis = 1e7 };
        }

        [Fact]
        public void GatherMemberOrbitSegments_MergesMembers_SortedByStartUT()
        {
            var committed = new List<Recording>
            {
                RecWithSegs("a", Seg("Kerbin", 100, 600), Seg("Sun", 600, 5000)),
                RecWithSegs("b", Seg("Duna", 5000, 7000)),
            };
            var gathered = MissionLoopUnitBuilder.GatherMemberOrbitSegments(committed, new List<int> { 0, 1 });

            Assert.Equal(3, gathered.Count);
            // startUT ordered across members.
            Assert.Equal("Kerbin", gathered[0].bodyName);
            Assert.Equal("Sun", gathered[1].bodyName);
            Assert.Equal("Duna", gathered[2].bodyName);
        }

        [Fact]
        public void GatherMemberOrbitSegments_SkipsOutOfRangeAndNull_NeverThrows()
        {
            var committed = new List<Recording>
            {
                RecWithSegs("a", Seg("Kerbin", 0, 100)),
                null, // a null member must be skipped, not throw
            };
            var gathered = MissionLoopUnitBuilder.GatherMemberOrbitSegments(committed, new List<int> { 0, 1, 99 });
            Assert.Single(gathered);
            Assert.Equal("Kerbin", gathered[0].bodyName);
        }

        [Fact]
        public void GatherMemberOrbitSegments_NullInputs_ReturnsEmpty()
        {
            Assert.Empty(MissionLoopUnitBuilder.GatherMemberOrbitSegments(null, new List<int> { 0 }));
            Assert.Empty(MissionLoopUnitBuilder.GatherMemberOrbitSegments(new List<Recording>(), null));
        }
    }
}
