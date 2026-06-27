using System.Collections.Generic;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for <see cref="PhaseId"/> (design §6): the RUNTIME render-layer identity that is
    /// value-equatable so it can key a dictionary / be asserted. The load-bearing property is that two
    /// phases of the SAME recording but DIFFERENT instance-key (overlapping self-loop instances) or
    /// different ordinal are DISTINCT ids.
    ///
    /// Each assertion states the bug it catches: a collision between two overlapping-instance phases
    /// would make a per-phase dictionary (seam wiring / trace registry) overwrite one with the other.
    /// </summary>
    public class PhaseIdTests
    {
        [Fact]
        public void Equality_SameComponents_AreEqual()
        {
            var a = new PhaseId("rec-A", instanceKey: 2, ordinal: 5);
            var b = new PhaseId("rec-A", instanceKey: 2, ordinal: 5);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void Equality_DifferentInstanceKey_AreDistinct()
        {
            var a = new PhaseId("rec-A", instanceKey: 0, ordinal: 5);
            var b = new PhaseId("rec-A", instanceKey: 1, ordinal: 5);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Equality_DifferentOrdinal_AreDistinct()
        {
            var a = new PhaseId("rec-A", instanceKey: 0, ordinal: 1);
            var b = new PhaseId("rec-A", instanceKey: 0, ordinal: 2);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Equality_DifferentRecording_AreDistinct()
        {
            var a = new PhaseId("rec-A", 0, 0);
            var b = new PhaseId("rec-B", 0, 0);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void CanKeyADictionary_OverlappingInstancesDoNotCollide()
        {
            var dict = new Dictionary<PhaseId, string>
            {
                [new PhaseId("rec-A", 0, 3)] = "instance0",
                [new PhaseId("rec-A", 1, 3)] = "instance1",
            };
            Assert.Equal(2, dict.Count);
            Assert.Equal("instance0", dict[new PhaseId("rec-A", 0, 3)]);
            Assert.Equal("instance1", dict[new PhaseId("rec-A", 1, 3)]);
        }

        [Fact]
        public void IsEmpty_DefaultStruct()
        {
            Assert.True(default(PhaseId).IsEmpty);
            Assert.False(new PhaseId("rec-A", 0, 0).IsEmpty);
        }

        [Fact]
        public void ToString_IsGrepStable()
        {
            Assert.Equal("rec-A#i2#p5", new PhaseId("rec-A", 2, 5).ToString());
        }
    }
}
