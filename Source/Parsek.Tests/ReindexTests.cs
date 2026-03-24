using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="ParsekFlight.ReindexAfterDelete{T}"/>.
    /// Verifies that dictionary keys shift down correctly when an entry
    /// is removed from a contiguous integer-keyed dictionary (used for
    /// ghost state management after recording deletion).
    /// </summary>
    public class ReindexTests
    {
        [Fact]
        public void RemoveMiddleIndex_ShiftsKeysAboveDown()
        {
            // Bug caught: removing index 2 from {0,1,2,3,4} must shift 3→2 and 4→3,
            // leaving {0,1,2,3} with correct values — off-by-one in shift logic would
            // produce wrong key mappings or lose entries.
            var dict = new Dictionary<int, string>
            {
                { 0, "A" }, { 1, "B" }, { 2, "C" }, { 3, "D" }, { 4, "E" }
            };

            ParsekFlight.ReindexAfterDelete(dict, 2);

            Assert.Equal(4, dict.Count);
            Assert.Equal("A", dict[0]);
            Assert.Equal("B", dict[1]);
            Assert.Equal("D", dict[2]); // was key 3
            Assert.Equal("E", dict[3]); // was key 4
            Assert.False(dict.ContainsKey(4));
        }

        [Fact]
        public void RemoveFirstIndex_AllKeysShiftDown()
        {
            // Bug caught: removing index 0 must shift all remaining keys down by 1 —
            // a guard that skips key 0 would leave the dict unchanged.
            var dict = new Dictionary<int, string>
            {
                { 0, "A" }, { 1, "B" }, { 2, "C" }, { 3, "D" }, { 4, "E" }
            };

            ParsekFlight.ReindexAfterDelete(dict, 0);

            Assert.Equal(4, dict.Count);
            Assert.Equal("B", dict[0]); // was key 1
            Assert.Equal("C", dict[1]); // was key 2
            Assert.Equal("D", dict[2]); // was key 3
            Assert.Equal("E", dict[3]); // was key 4
            Assert.False(dict.ContainsKey(4));
        }

        [Fact]
        public void RemoveLastIndex_NoShiftNeeded()
        {
            // Bug caught: removing the highest key must simply drop it without
            // touching any other entry — shift logic applied to keys below the
            // removed index would corrupt the dictionary.
            var dict = new Dictionary<int, string>
            {
                { 0, "A" }, { 1, "B" }, { 2, "C" }, { 3, "D" }, { 4, "E" }
            };

            ParsekFlight.ReindexAfterDelete(dict, 4);

            Assert.Equal(4, dict.Count);
            Assert.Equal("A", dict[0]);
            Assert.Equal("B", dict[1]);
            Assert.Equal("C", dict[2]);
            Assert.Equal("D", dict[3]);
            Assert.False(dict.ContainsKey(4));
        }

        [Fact]
        public void EmptyDict_NoCrash()
        {
            // Bug caught: empty dictionary must not throw — guarding against
            // iteration over empty collection or key-not-found exceptions.
            var dict = new Dictionary<int, string>();

            ParsekFlight.ReindexAfterDelete(dict, 0);

            Assert.Empty(dict);
        }

        [Fact]
        public void SingleEntry_ResultsInEmptyDict()
        {
            // Bug caught: removing the only entry must leave the dict empty —
            // an off-by-one in the < vs <= comparison could retain the entry
            // or shift it to key -1.
            var dict = new Dictionary<int, string> { { 0, "only" } };

            ParsekFlight.ReindexAfterDelete(dict, 0);

            Assert.Empty(dict);
        }

        [Fact]
        public void WorksWithNonStringValueType()
        {
            // Bug caught: generic type parameter T must work with value types too —
            // the method is used with GhostPlaybackState (class) and double
            // (loopPhaseOffsets). Ensure no boxing/unboxing issues.
            var dict = new Dictionary<int, double>
            {
                { 0, 1.0 }, { 1, 2.0 }, { 2, 3.0 }
            };

            ParsekFlight.ReindexAfterDelete(dict, 1);

            Assert.Equal(2, dict.Count);
            Assert.Equal(1.0, dict[0]);
            Assert.Equal(3.0, dict[1]); // was key 2
        }

        [Fact]
        public void RemoveNonExistentIndex_PreservesLowerKeys()
        {
            // Bug caught: removing an index that doesn't exist in the dict (e.g.,
            // sparse dictionary) must not corrupt existing entries — keys below
            // removedIndex stay, keys above still shift down.
            var dict = new Dictionary<int, string>
            {
                { 0, "A" }, { 3, "D" }, { 5, "F" }
            };

            ParsekFlight.ReindexAfterDelete(dict, 2);

            // Key 0 < 2 → stays at 0
            // Key 3 > 2 → shifts to 2
            // Key 5 > 2 → shifts to 4
            Assert.Equal(3, dict.Count);
            Assert.Equal("A", dict[0]);
            Assert.Equal("D", dict[2]);
            Assert.Equal("F", dict[4]);
        }
    }
}
