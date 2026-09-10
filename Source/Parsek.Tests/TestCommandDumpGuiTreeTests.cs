using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the GUI-census dump verb
    /// (<see cref="TestCommandDumpGuiTree"/>): the label parse and its agreement with the
    /// sibling capture verb AND with the recorder's own sanitiser, the poll decision and
    /// its two orderings, the path derivation and the payload shape.
    ///
    /// <para>The property that matters most here is the poll's FIRST rule: a faulted arm
    /// can still leave a dump on disk, because <c>GuiTreeRecorder.Fault</c> disarms but
    /// leaves an open capture for the pump to flush. Reporting OK over that file would put
    /// a partial control tree in a census folder, where a missing subtree reads as a Parsek
    /// UI defect rather than as a recorder fault.</para>
    /// </summary>
    public class TestCommandDumpGuiTreeTests
    {
        // ----- label parse -----

        [Fact]
        public void MissingLabel_IsItsOwnReject_NotTheInvalidOne()
        {
            // Distinct tokens for the same reason CaptureScreenshot keeps them distinct: a
            // forgotten arg and a bad arg send a spec author to different fixes.
            Assert.False(TestCommandDumpGuiTree.TryParseLabel(
                null, out string label, out string reason));
            Assert.Null(label);
            Assert.Equal(TestCommandDumpGuiTree.LabelArgMissingReason, reason);
            Assert.NotEqual(TestCommandDumpGuiTree.LabelArgMissingReason,
                TestCommandDumpGuiTree.LabelArgInvalidReason);
        }

        [Theory]
        [InlineData("ksc-main-advanced")]
        [InlineData("flight_settings_basic")]
        [InlineData("ksc-career.contracts")]
        [InlineData("a")]
        [InlineData("A1")]
        public void FilenameSafeLabels_Parse(string raw)
        {
            Assert.True(TestCommandDumpGuiTree.TryParseLabel(
                raw, out string label, out string reason));
            Assert.Equal(raw, label);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("../escape")]
        [InlineData("dir/label")]
        [InlineData("dir\\label")]
        [InlineData("C:label")]
        [InlineData("-leading-dash")]
        [InlineData("has space")]
        [InlineData("label!")]
        public void UnsafeLabels_AreRejectedFailClosed(string raw)
        {
            Assert.False(TestCommandDumpGuiTree.IsValidLabel(raw));
            Assert.False(TestCommandDumpGuiTree.TryParseLabel(
                raw, out string label, out string reason));
            Assert.Null(label);
            Assert.Equal(TestCommandDumpGuiTree.LabelArgInvalidReason, reason);
        }

        [Theory]
        [InlineData("ksc-main-advanced")]
        [InlineData("A1")]
        [InlineData("has space")]
        [InlineData("../escape")]
        [InlineData("")]
        [InlineData(null)]
        public void TheLabelRuleIsTheCaptureVerbsRule_NotACopyOfIt(string raw)
        {
            // A census drives the two verbs as a PAIR under ONE label, so a label one verb
            // accepts and the other refuses would leave a PNG with no dump beside it (or a
            // step that reds after a whole boot). Delegation rather than a second copy of
            // the predicate is what makes that impossible; this cell pins the delegation
            // over both answers, so a future divergence has to be deliberate.
            Assert.Equal(TestCommandCaptureScreenshot.IsValidLabel(raw),
                TestCommandDumpGuiTree.IsValidLabel(raw));
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgMissingReason,
                TestCommandDumpGuiTree.LabelArgMissingReason);
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgInvalidReason,
                TestCommandDumpGuiTree.LabelArgInvalidReason);
        }

        [Theory]
        [InlineData("ksc-main-advanced")]
        [InlineData("flight_settings_basic")]
        [InlineData("ksc-career.contracts")]
        [InlineData("A1")]
        public void AnAcceptedLabelSurvivesTheRecordersSanitiserUnchanged(string raw)
        {
            // THE MIRROR DIRECTION, and it is the reason the payload may state a path at
            // all. The verb reports `Screenshots/<label>.gui.json`, but the RECORDER owns
            // the real path and runs its own SanitizeLabel over the same string - replacing
            // anything outside [A-Za-z0-9._-] with an underscore and trimming leading and
            // trailing dots and underscores. If the verb's rule were the looser of the two,
            // the reported path would name a file that does not exist. It is the tighter
            // one (it additionally requires an alphanumeric first character), so the two
            // agree - asserted here rather than argued in a comment.
            Assert.True(TestCommandDumpGuiTree.IsValidLabel(raw));
            Assert.Equal(raw, GuiTreeRecorder.SanitizeLabel(raw));
        }

        // ----- poll decision -----

        [Fact]
        public void APollBeforeAnythingHappens_IsNotYet()
        {
            Assert.Equal(GuiTreeDumpPollOutcome.NotYet,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: false, recorderIdle: false, faultsSinceArm: 0,
                    budgetExpired: false));
        }

        [Fact]
        public void AWrittenDumpForThisArm_Settles()
        {
            Assert.Equal(GuiTreeDumpPollOutcome.Settled,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: true, recorderIdle: true, faultsSinceArm: 0,
                    budgetExpired: false));
        }

        [Fact]
        public void SettledIsDecidedBeforeTheBudget()
        {
            // The CaptureScreenshot.DecidePoll rule: a dump that landed on the very frame
            // the budget expired is a success, and reporting it as a timeout would red a
            // run over a file sitting in the artifact folder.
            Assert.Equal(GuiTreeDumpPollOutcome.Settled,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: true, recorderIdle: true, faultsSinceArm: 0,
                    budgetExpired: true));
        }

        [Fact]
        public void AFaultBeatsAWrittenDump()
        {
            // THE ORDERING THIS CLASS EXISTS FOR. GuiTreeRecorder.Fault clears ArmedFlag
            // but leaves an OPEN capture, so the pump still flushes what it had: a faulted
            // arm can leave a real file on disk that describes a frame the recorder stopped
            // following. The file is left in place (it is evidence about the fault); what
            // is refused is calling it a product.
            Assert.Equal(GuiTreeDumpPollOutcome.Faulted,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: true, recorderIdle: true, faultsSinceArm: 1,
                    budgetExpired: false));
            Assert.Equal(GuiTreeDumpPollOutcome.Faulted,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: false, recorderIdle: false, faultsSinceArm: 3,
                    budgetExpired: false));
        }

        [Fact]
        public void AnIdleRecorderWithNoDump_GivesUp_WithoutWaitingOutTheBudget()
        {
            // The recorder's own give-up (armed-no-repaint) fires after 900 frames, well
            // inside the verb's 60 s budget. Once it has, nothing can change the answer, so
            // holding the FIFO head for another 45 s would only delay the diagnosis.
            Assert.Equal(GuiTreeDumpPollOutcome.GaveUp,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: false, recorderIdle: true, faultsSinceArm: 0,
                    budgetExpired: false));
        }

        [Fact]
        public void AStillWorkingRecorderAtTheBudget_TimesOut()
        {
            Assert.Equal(GuiTreeDumpPollOutcome.TimedOut,
                TestCommandDumpGuiTree.DecidePoll(
                    writtenForThisArm: false, recorderIdle: false, faultsSinceArm: 0,
                    budgetExpired: true));
        }

        [Fact]
        public void TheTwoNoDumpOutcomesShareOneWireToken()
        {
            // Two causes, one actionable fact: the file a later step wanted is not there.
            // The applier's log line is what separates them (`gaveUp=` plus the recorder's
            // own disarm reason), because a spec cannot act on the difference and a reader
            // can.
            Assert.NotEqual(GuiTreeDumpPollOutcome.GaveUp, GuiTreeDumpPollOutcome.TimedOut);
            Assert.Equal("gui-tree-timeout", TestCommandDumpGuiTree.TimeoutReason);
            Assert.Equal("gui-tree-faulted", TestCommandDumpGuiTree.FaultedReason);
            Assert.Equal("gui-tree-arm-refused", TestCommandDumpGuiTree.ArmRefusedReason);
        }

        // ----- paths -----

        [Fact]
        public void RelativePath_IsTheHarvestedDirectory_WithForwardSlashes()
        {
            // run.py harvests <instance>/Screenshots/ and nowhere else, and
            // hlib.ARTIFACT_SHOTS_SUFFIXES carries `.gui.json` precisely so a dump rides
            // along with that run's images.
            Assert.Equal("Screenshots/ksc-main-advanced.gui.json",
                TestCommandDumpGuiTree.RelativePathFor("ksc-main-advanced"));
            Assert.DoesNotContain("\\", TestCommandDumpGuiTree.RelativePathFor("x"));
        }

        [Fact]
        public void TheDumpDirectoryAndSuffixAreTheRecordersOwn()
        {
            // Two spellings of one fact, so they are asserted against each other: the
            // recorder writes the file and this verb only NAMES it, so a suffix change on
            // one side has to red rather than produce a payload pointing at nothing.
            Assert.Equal(GuiTreeRecorder.OutputSuffix, TestCommandDumpGuiTree.DumpSuffix);
            Assert.Equal(GuiTreeRecorder.OutputDirectoryName,
                TestCommandCaptureScreenshot.ScreenshotsDirName);
            Assert.Equal("x.gui.json", TestCommandDumpGuiTree.FileNameFor("x"));
        }

        [Fact]
        public void TheDumpAndTheCaptureShareALabelAndDifferOnlyInSuffix()
        {
            // What makes `gui_tree_view.py` able to pair a dump with its screenshot: one
            // label, two files, same directory.
            Assert.Equal("Screenshots/ksc-settings-basic.png",
                TestCommandCaptureScreenshot.RelativePathFor("ksc-settings-basic"));
            Assert.Equal("Screenshots/ksc-settings-basic.gui.json",
                TestCommandDumpGuiTree.RelativePathFor("ksc-settings-basic"));
        }

        // ----- payload -----

        [Fact]
        public void Payload_CarriesEveryKey_InvariantlyFormatted()
        {
            List<KeyValuePair<string, string>> payload = TestCommandDumpGuiTree.BuildPayload(
                "ksc-settings-basic", 123456, 3, 217, 17, 17, 640);
            Assert.Equal(
                new[] { "label", "path", "bytes", "windows", "nodes", "patched", "hits" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("ksc-settings-basic", Value(payload, "label"));
            Assert.Equal("Screenshots/ksc-settings-basic.gui.json", Value(payload, "path"));
            Assert.Equal("123456", Value(payload, "bytes"));
            Assert.Equal("3", Value(payload, "windows"));
            Assert.Equal("217", Value(payload, "nodes"));
            Assert.Equal("17/17", Value(payload, "patched"));
            Assert.Equal("640", Value(payload, "hits"));
        }

        [Fact]
        public void PatchedIsAPairAndNamesTheShippedFunnelCount()
        {
            // A spec pins the WHOLE token (`patched=17/17`), because 17 alone says nothing
            // without the denominator the build shipped - and the denominator is the
            // recorder's own funnel count, not a number retyped here.
            Assert.Equal("17/17",
                TestCommandDumpGuiTree.FormatPatched(GuiTreeFunnels.Count, GuiTreeFunnels.Count));
            Assert.Equal(17, GuiTreeFunnels.Count);
            // A funnel that failed to patch is VISIBLE in the token rather than folded away.
            Assert.Equal("16/17", TestCommandDumpGuiTree.FormatPatched(16, 17));
        }

        [Fact]
        public void Payload_IsCultureInvariant()
        {
            // The harness parses this line; a ro-RO / de-DE host must not print a group
            // separator or a comma decimal into it.
            using (new CultureSwap("de-DE"))
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandDumpGuiTree.BuildPayload("a", 1234567, 1, 9876, 17, 17, 54321);
                Assert.Equal("1234567", Value(payload, "bytes"));
                Assert.Equal("9876", Value(payload, "nodes"));
                Assert.Equal("54321", Value(payload, "hits"));
                Assert.Equal("17/17", Value(payload, "patched"));
            }
        }

        [Fact]
        public void Payload_ReportsAnUnstattableFileAsMinusOneRatherThanZero()
        {
            // The applier answers -1 when the dump cannot be stat'd, and this cell pins
            // that the payload carries it through: the recorder has already said the write
            // RETURNED, so a file we cannot measure is a reporting gap, and `bytes=0` would
            // claim an empty dump that nothing observed.
            Assert.Equal("-1",
                Value(TestCommandDumpGuiTree.BuildPayload("a", -1, 1, 2, 17, 17, 3), "bytes"));
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.First(kv => kv.Key == key).Value;

        /// <summary>Scoped OS-culture swap, so the invariance cell above proves the
        /// production site is invariant rather than that the test host happens to be.</summary>
        private sealed class CultureSwap : System.IDisposable
        {
            private readonly CultureInfo previous;

            internal CultureSwap(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    CultureInfo.GetCultureInfo(name);
            }

            public void Dispose()
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
