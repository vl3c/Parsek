using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug 1 (fix-funds-economy-divergence §3.2): the pure contract-completion debounce
    /// decision <see cref="GameStateRecorder.IsDuplicateContractCompletion"/>. Stock KSP can
    /// fire Contract.onCompleted N times for one contract within a few ms; the recorder
    /// debounces a re-fire of the same guid within the resource-coalesce window so it records
    /// each completion exactly once. These cover the pure helper against a caller-owned dict
    /// (the production call passes the static lastContractCompletionUtByGuid map).
    /// </summary>
    public class ContractCompletionDedupTests
    {
        private const double Window = 0.1; // GameStateStore.ResourceCoalesceEpsilon
        private const string GuidA = "1eb2baf9-afce-48c7-82d6-364dae85f57a";
        private const string GuidB = "2cd3cbf0-bggf-59d8-93f4-475ebf96g68b";

        [Fact]
        public void IsDuplicateContractCompletion_FirstSeen_NotDuplicate_RecordsLastSeen()
        {
            var seen = new Dictionary<string, double>();

            bool dup = GameStateRecorder.IsDuplicateContractCompletion(
                GuidA, 475409.6, seen, Window);

            Assert.False(dup);
            Assert.True(seen.ContainsKey(GuidA));
            Assert.Equal(475409.6, seen[GuidA]);
        }

        [Fact]
        public void IsDuplicateContractCompletion_SameGuidWithinWindow_IsDuplicate()
        {
            // The observed burst: four onCompleted fires for one contract at the same UT.
            var seen = new Dictionary<string, double>();
            const double Ut = 475409.6;

            bool first = GameStateRecorder.IsDuplicateContractCompletion(GuidA, Ut, seen, Window);
            bool second = GameStateRecorder.IsDuplicateContractCompletion(GuidA, Ut, seen, Window);
            bool third = GameStateRecorder.IsDuplicateContractCompletion(GuidA, Ut, seen, Window);
            bool fourth = GameStateRecorder.IsDuplicateContractCompletion(GuidA, Ut, seen, Window);

            Assert.False(first);   // first accepted
            Assert.True(second);   // re-fires flagged as duplicate
            Assert.True(third);
            Assert.True(fourth);
        }

        [Fact]
        public void IsDuplicateContractCompletion_WithinWindowSmallDrift_IsDuplicate()
        {
            // A re-fire a hair apart (well inside the window, the real burst's tiny
            // sub-frame UT drift) is still a duplicate.
            var seen = new Dictionary<string, double>();
            GameStateRecorder.IsDuplicateContractCompletion(GuidA, 1000.0, seen, Window);

            bool dup = GameStateRecorder.IsDuplicateContractCompletion(
                GuidA, 1000.0 + (Window / 2.0), seen, Window);

            Assert.True(dup);
        }

        [Fact]
        public void IsDuplicateContractCompletion_SameGuidOutsideWindow_NotDuplicate()
        {
            // A genuine later re-completion of the same contract (UT far apart) is allowed
            // through, and the last-seen UT advances to the new completion.
            var seen = new Dictionary<string, double>();
            GameStateRecorder.IsDuplicateContractCompletion(GuidA, 475409.6, seen, Window);

            bool dup = GameStateRecorder.IsDuplicateContractCompletion(
                GuidA, 475409.6 + 100.0, seen, Window);

            Assert.False(dup);
            Assert.Equal(475409.6 + 100.0, seen[GuidA]);
        }

        [Fact]
        public void IsDuplicateContractCompletion_DifferentGuid_NotDuplicate()
        {
            // Two different contracts completing at the same instant are independent.
            var seen = new Dictionary<string, double>();
            const double Ut = 475409.6;

            bool a = GameStateRecorder.IsDuplicateContractCompletion(GuidA, Ut, seen, Window);
            bool b = GameStateRecorder.IsDuplicateContractCompletion(GuidB, Ut, seen, Window);

            Assert.False(a);
            Assert.False(b);
            Assert.True(seen.ContainsKey(GuidA));
            Assert.True(seen.ContainsKey(GuidB));
        }

        [Fact]
        public void IsDuplicateContractCompletion_NullOrEmptyGuid_NotDuplicate()
        {
            // A missing guid cannot be keyed; never treat it as a duplicate.
            var seen = new Dictionary<string, double>();

            Assert.False(GameStateRecorder.IsDuplicateContractCompletion(null, 1.0, seen, Window));
            Assert.False(GameStateRecorder.IsDuplicateContractCompletion("", 1.0, seen, Window));
            Assert.Empty(seen);
        }
    }
}
