using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Case C of the pre-switch dialog (a loaded target that is a SEPARATE committed vessel)
    /// must not fire for a vessel the live tree already records in the background: the craft
    /// a kerbal just left on EVA, when the live tree was started on a vessel an earlier
    /// committed flight also recorded (todo PRESWITCH-CASE-C-LIVE-TREE-MEMBER, found by lane
    /// FAR-4's first flight).
    /// </summary>
    public class PreSwitchCaseCLiveTreeMemberTests
    {
        // catches: the FAR-4 shape, a live-tree background member read as a separate
        // committed vessel because an older committed tree also matched it.
        [Fact]
        public void MatchedOtherTree_TargetIsLiveTreeBackgroundMember_NotSeparate()
        {
            Assert.False(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: true, matchedTreeId: "committed", activeTreeId: "live",
                targetIsLiveTreeBackgroundMember: true));
        }

        // catches: the fix widening into "never Case C" - a separate committed vessel the
        // live tree does not record still opens the dialog.
        [Fact]
        public void MatchedOtherTree_TargetNotInLiveTree_Separate()
        {
            Assert.True(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: true, matchedTreeId: "committed", activeTreeId: "live",
                targetIsLiveTreeBackgroundMember: false));
        }

        [Fact]
        public void MatchedOwnTree_NotSeparate()
        {
            Assert.False(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: true, matchedTreeId: "live", activeTreeId: "live",
                targetIsLiveTreeBackgroundMember: false));
        }

        [Fact]
        public void NoMatch_NotSeparate()
        {
            Assert.False(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: false, matchedTreeId: null, activeTreeId: "live",
                targetIsLiveTreeBackgroundMember: false));
            Assert.False(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: true, matchedTreeId: null, activeTreeId: "live",
                targetIsLiveTreeBackgroundMember: false));
        }

        [Fact]
        public void MatchedOtherTree_NoLiveTree_Separate()
        {
            Assert.True(MapFocusObjectOnSelectPatch.IsSeparateCommittedTarget(
                matched: true, matchedTreeId: "committed", activeTreeId: null,
                targetIsLiveTreeBackgroundMember: false));
        }

        [Fact]
        public void LiveTreeBackgroundMember_ReadsTheBackgroundMap()
        {
            var tree = new RecordingTree { Id = "live" };
            tree.BackgroundMap = new Dictionary<uint, string> { { 2708531065u, "rec-bg" } };

            Assert.True(MapFocusObjectOnSelectPatch.IsLiveTreeBackgroundMember(tree, 2708531065u));
            Assert.False(MapFocusObjectOnSelectPatch.IsLiveTreeBackgroundMember(tree, 42u));
            Assert.False(MapFocusObjectOnSelectPatch.IsLiveTreeBackgroundMember(tree, 0u));
            Assert.False(MapFocusObjectOnSelectPatch.IsLiveTreeBackgroundMember(null, 2708531065u));
        }
    }
}
