using System;
using System.IO;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The switch-time rule (flight recorder design section 5.4, "Switching vessels in
    /// flight"): the recording the player leaves is decided without the Merge / Discard
    /// dialog - a recording that changed nothing is discarded, anything else is merged.
    /// </summary>
    public class PreSwitchAutoResolutionTests
    {
        private static MapFocusObjectOnSelectPatch.PreSwitchAutoResolution Decide(
            bool isAutoMerge, bool hasSession, bool evaluable, bool priorIsNoOp,
            SwitchSegmentDisposition disposition = SwitchSegmentDisposition.None,
            bool idleOnPad = false)
            => MapFocusObjectOnSelectPatch.DecidePreSwitchAutoResolution(
                isAutoMerge, hasSession, evaluable, priorIsNoOp, disposition, idleOnPad);

        private const MapFocusObjectOnSelectPatch.PreSwitchAutoResolution Dialog =
            MapFocusObjectOnSelectPatch.PreSwitchAutoResolution.Dialog;
        private const MapFocusObjectOnSelectPatch.PreSwitchAutoResolution Discard =
            MapFocusObjectOnSelectPatch.PreSwitchAutoResolution.Discard;
        private const MapFocusObjectOnSelectPatch.PreSwitchAutoResolution Merge =
            MapFocusObjectOnSelectPatch.PreSwitchAutoResolution.Merge;

        // catches: a no-op segment of a background member still asking (the reported case).
        [Fact]
        public void Session_NoOpMixedSegment_AutoMerge_Discards()
        {
            Assert.Equal(Discard, Decide(true, true, true, true, SwitchSegmentDisposition.BgMemberOrMixed));
        }

        [Theory]
        [InlineData((int)SwitchSegmentDisposition.Standalone)]
        [InlineData((int)SwitchSegmentDisposition.CommittedRestoreClone)]
        public void Session_NoOpStandaloneOrClone_DiscardsInEitherMode(int dispositionValue)
        {
            var disposition = (SwitchSegmentDisposition)dispositionValue;
            Assert.Equal(Discard, Decide(true, true, true, true, disposition));
            Assert.Equal(Discard, Decide(false, true, true, true, disposition));
        }

        // catches: a segment that did something prompting instead of merging.
        [Theory]
        [InlineData((int)SwitchSegmentDisposition.Standalone)]
        [InlineData((int)SwitchSegmentDisposition.CommittedRestoreClone)]
        [InlineData((int)SwitchSegmentDisposition.BgMemberOrMixed)]
        [InlineData((int)SwitchSegmentDisposition.None)]
        public void Session_MeaningfulSegment_AutoMerge_Merges(int dispositionValue)
        {
            var disposition = (SwitchSegmentDisposition)dispositionValue;
            Assert.Equal(Merge, Decide(true, true, true, false, disposition));
        }

        // catches: the automation-only auto-merge-off path losing the dialog it pins.
        [Fact]
        public void AutoMergeOff_KeepsTodaysDialogCases()
        {
            Assert.Equal(Dialog, Decide(false, true, true, false, SwitchSegmentDisposition.Standalone));
            Assert.Equal(Dialog, Decide(false, true, true, true, SwitchSegmentDisposition.BgMemberOrMixed));
            Assert.Equal(Dialog, Decide(false, false, true, false, idleOnPad: true));
            Assert.Equal(Dialog, Decide(false, false, true, false));
        }

        [Fact]
        public void NoSession_ResumeNoOp_DiscardsInEitherMode()
        {
            Assert.Equal(Discard, Decide(true, false, true, true));
            Assert.Equal(Discard, Decide(false, false, true, true));
        }

        // catches: a fresh flight left sitting on the pad being merged at the switch while
        // the scene exit would have discarded it.
        [Fact]
        public void NoSession_IdleOnPad_AutoMerge_Discards()
        {
            Assert.Equal(Discard, Decide(true, false, true, false, idleOnPad: true));
        }

        [Fact]
        public void NoSession_FlownTree_AutoMerge_Merges()
        {
            Assert.Equal(Merge, Decide(true, false, true, false));
        }

        // catches: pressing a button for the player while a restore owns the tree or the
        // no-op check threw.
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Unevaluated_AlwaysShowsTheDialog(bool hasSession, bool priorIsNoOp)
        {
            Assert.Equal(Dialog, Decide(true, hasSession, false, priorIsNoOp,
                SwitchSegmentDisposition.Standalone, idleOnPad: true));
        }

        [Theory]
        [InlineData("restoring-active-tree", true)]
        [InlineData("exception:NullReferenceException", true)]
        [InlineData("part-event:Decoupled", false)]
        [InlineData("non-boring-section:ExoPropulsive", false)]
        [InlineData("merge-journal-active", false)]
        [InlineData("loaded-background-member", false)]
        [InlineData("not-committed-restore-clone", false)]
        [InlineData(null, false)]
        public void IsUnevaluatedNoOpReason_OnlyRestoreAndThrow(string reason, bool expected)
        {
            Assert.Equal(expected, MapFocusObjectOnSelectPatch.IsUnevaluatedNoOpReason(reason));
        }

        [Fact]
        public void VerbRoutes_NameTheButtonPressed()
        {
            Assert.Equal("pre-switch-auto-discard", TestCommandSimulateSwitchClick.AutoResolveRoute(Discard));
            Assert.Equal("pre-switch-auto-merge", TestCommandSimulateSwitchClick.AutoResolveRoute(Merge));
            Assert.Equal("auto-resolve-no-switch", TestCommandSimulateSwitchClick.AutoResolveNoSwitchReason);
        }

        private static string ReadSource(params string[] parts)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string[] all = new string[parts.Length + 2];
            all[0] = root;
            all[1] = "Source";
            Array.Copy(parts, 0, all, 2, parts.Length);
            return File.ReadAllText(Path.Combine(all)).Replace("\r\n", "\n");
        }

        // catches: the Prefix spawning the dialog before (or without) the auto-resolution.
        [Fact]
        public void Prefix_AutoResolvesBeforeOpeningTheDialog()
        {
            string src = ReadSource("Parsek", "Patches", "MapFocusObjectOnSelectPatch.cs");
            int openCase = src.IndexOf("case PreSwitchDialogDecision.OpenDialog:", StringComparison.Ordinal);
            int resolve = src.IndexOf("if (TryAutoResolvePreSwitch(vessel, existingSession,", openCase, StringComparison.Ordinal);
            int spawn = src.IndexOf("TryOpenPreSwitchDecisionDialog(vessel, existingSession)", openCase, StringComparison.Ordinal);
            Assert.True(openCase >= 0 && resolve > openCase && spawn > resolve,
                "the OpenDialog branch must call TryAutoResolvePreSwitch before it spawns the dialog");
        }

        // catches: the seam verb certifying a path the click no longer takes.
        [Fact]
        public void Verb_DrivesTheSameAutoResolution()
        {
            string src = ReadSource("Parsek", "TestCommands", "ParsekTestCommandAddon.SimulateSwitchClick.cs");
            int call = src.IndexOf("MapFocusObjectOnSelectPatch.TryAutoResolvePreSwitch(", StringComparison.Ordinal);
            int refusal = src.IndexOf("if (gate != SwitchClickGateDecision.Proceed)", StringComparison.Ordinal);
            Assert.True(call >= 0 && refusal > call,
                "the verb must try the auto-resolution before its dialog-required refusal");
        }
    }
}
