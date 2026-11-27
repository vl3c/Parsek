using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The watch-lane <c>tree=</c> handle (first / latest / name:&lt;mission&gt;): pure
    /// resolution over the committed-tree list. The applier only builds the candidate
    /// list from RecordingStore.CommittedTrees + MissionStore.DisplayNameForTree.
    /// </summary>
    public class TestCommandTreeHandleTests
    {
        private static List<TreeHandleCandidate> Trees() => new List<TreeHandleCandidate>
        {
            new TreeHandleCandidate("aaaa", "Kerbal X"),
            new TreeHandleCandidate("bbbb", "Kerbal X #2"),
            new TreeHandleCandidate("cccc", "Duna Express"),
        };

        [Theory]
        [InlineData("first", true)]
        [InlineData(" LATEST ", true)]
        [InlineData("Name:Duna", true)]
        [InlineData("0da22482a8a648c6835b7bcd6b0f200d", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsHandle_recognises_the_keywords_only(string arg, bool want)
        {
            Assert.Equal(want, TestCommandTreeHandle.IsHandle(arg));
        }

        [Theory]
        [InlineData("first", "aaaa")]
        [InlineData("Latest", "cccc")]
        [InlineData("name:duna express", "cccc")]
        [InlineData("name: Kerbal X #2 ", "bbbb")]
        public void Resolve_picks_the_named_tree(string handle, string want)
        {
            Assert.Equal(want, TestCommandTreeHandle.Resolve(handle, Trees(), out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void A_plain_id_passes_through_untouched()
        {
            Assert.Equal("dddd", TestCommandTreeHandle.Resolve("dddd", Trees(), out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void No_trees_no_match_and_ambiguity_refuse()
        {
            Assert.Null(TestCommandTreeHandle.Resolve("latest", new List<TreeHandleCandidate>(),
                out string empty));
            Assert.Equal(TestCommandTreeHandle.NoTreesReason, empty);

            Assert.Null(TestCommandTreeHandle.Resolve("name:Mun Base", Trees(), out string none));
            Assert.Equal(TestCommandTreeHandle.NoMatchReason, none);

            var dup = Trees();
            dup.Add(new TreeHandleCandidate("eeee", "kerbal x"));
            Assert.Null(TestCommandTreeHandle.Resolve("name:Kerbal X", dup, out string two));
            Assert.Equal(TestCommandTreeHandle.AmbiguousReason, two);
        }
    }
}
