using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the GUI-census capture verb
    /// (<see cref="TestCommandCaptureScreenshot"/>): the label / superSize parses, the
    /// two-agreeing-samples poll decision, the path derivation and the payload shape.
    ///
    /// <para>The property that matters most here is that a capture is never reported OK
    /// before the file is really there. The whole reason the verb is two-phase is that
    /// <c>ScreenCapture.CaptureScreenshot</c> returns before Unity has written anything, so
    /// a poll that settled on a first sighting could hand a census a truncated PNG - which
    /// in a contact sheet reads as a Parsek render defect rather than as a harvest
    /// artifact.</para>
    /// </summary>
    public class TestCommandCaptureScreenshotTests
    {
        // ----- label parse -----

        [Fact]
        public void MissingLabel_IsItsOwnReject_NotTheInvalidOne()
        {
            // Distinct tokens on purpose: a forgotten arg and a bad arg send a spec author
            // to different fixes, and a shared token would hide which happened.
            Assert.False(TestCommandCaptureScreenshot.TryParseLabel(
                null, out string label, out string reason));
            Assert.Null(label);
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgMissingReason, reason);
        }

        [Theory]
        [InlineData("ksc-main-advanced")]
        [InlineData("flight_settings_basic")]
        [InlineData("ksc-career.contracts")]
        [InlineData("a")]
        [InlineData("A1")]
        public void FilenameSafeLabels_Parse(string raw)
        {
            Assert.True(TestCommandCaptureScreenshot.TryParseLabel(
                raw, out string label, out string reason));
            Assert.Equal(raw, label);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("")]                    // empty
        [InlineData(".")]                   // a relative path component
        [InlineData("..")]                  // traversal
        [InlineData("../escape")]           // traversal with a separator
        [InlineData("dir/label")]           // POSIX separator
        [InlineData("dir\\label")]          // Windows separator
        [InlineData("C:label")]             // drive-letter colon
        [InlineData("-leading-dash")]       // must start alphanumeric
        [InlineData("_leading-underscore")]
        [InlineData(".hidden")]
        [InlineData("has space")]           // whitespace the codec would encode
        [InlineData("has\ttab")]
        [InlineData("star*")]
        [InlineData("quote\"")]
        [InlineData("percent%20")]
        // Written as an escape rather than as a literal: the house style is plain-ASCII
        // source, and the test subject is the BYTE being non-ASCII rather than how it is
        // spelled in this file.
        [InlineData("na\u00efve")]      // non-ASCII
        public void UnsafeLabels_AreRejected(string raw)
        {
            // The label becomes a filename in the harvested artifact directory, so the rule
            // is fail-closed: anything that is not the harness's own filename-safe id shape
            // must never reach the filesystem.
            Assert.False(TestCommandCaptureScreenshot.IsValidLabel(raw));
            Assert.False(TestCommandCaptureScreenshot.TryParseLabel(
                raw, out _, out string reason));
            Assert.Equal(TestCommandCaptureScreenshot.LabelArgInvalidReason, reason);
        }

        [Fact]
        public void LabelLength_IsBounded()
        {
            string atCap = new string('a', TestCommandCaptureScreenshot.MaxLabelLength);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(atCap));
            Assert.False(TestCommandCaptureScreenshot.IsValidLabel(atCap + "a"));
        }

        // ----- superSize parse -----

        [Fact]
        public void AbsentSuperSize_DefaultsToOneAndSucceeds()
        {
            // Optional, and the default is 1 deliberately: Unity's supersize path re-renders
            // through the cameras, and screen-space IMGUI is not guaranteed to survive that -
            // which would drop the very windows the census exists to photograph.
            Assert.True(TestCommandCaptureScreenshot.TryParseSuperSize(
                null, out int size, out string reason));
            Assert.Equal(TestCommandCaptureScreenshot.DefaultSuperSize, size);
            Assert.Equal(1, size);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("1", 1)]
        [InlineData("2", 2)]
        [InlineData("4", 4)]
        public void InRangeSuperSize_Parses(string raw, int expected)
        {
            Assert.True(TestCommandCaptureScreenshot.TryParseSuperSize(
                raw, out int size, out string reason));
            Assert.Equal(expected, size);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("5")]
        [InlineData("1.5")]
        [InlineData("two")]
        [InlineData("")]
        [InlineData(" 2")]
        public void OutOfRangeOrUnparseableSuperSize_IsRejectedNotClamped(string raw)
        {
            // Rejected rather than clamped: a typo that silently captured at 1x would put an
            // unreadable image in a review folder and read as a resolution problem.
            Assert.False(TestCommandCaptureScreenshot.TryParseSuperSize(
                raw, out _, out string reason));
            Assert.Equal(TestCommandCaptureScreenshot.SuperSizeArgInvalidReason, reason);
        }

        // ----- the poll decision -----

        [Fact]
        public void FirstSighting_IsNeverSettled()
        {
            // THE headline property. previousBytes is the negative first-poll sentinel, so
            // even a file that already exists at a plausible size reads NotYet: one sample
            // cannot distinguish a finished PNG from one Unity is still encoding.
            Assert.Equal(ScreenshotPollOutcome.NotYet,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: true, bytes: 120000, previousBytes: -1, budgetExpired: false));
        }

        [Fact]
        public void TwoAgreeingNonZeroSamples_Settle()
        {
            Assert.Equal(ScreenshotPollOutcome.Settled,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: true, bytes: 120000, previousBytes: 120000, budgetExpired: false));
        }

        [Fact]
        public void AGrowingFile_IsNotYet()
        {
            Assert.Equal(ScreenshotPollOutcome.NotYet,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: true, bytes: 200000, previousBytes: 120000, budgetExpired: false));
        }

        [Fact]
        public void TwoAgreeingZeroSamples_DoNotSettle()
        {
            // A 0-byte file is a created-but-unwritten stub. Settling on it would report OK
            // for an image with nothing in it.
            Assert.Equal(ScreenshotPollOutcome.NotYet,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: true, bytes: 0, previousBytes: 0, budgetExpired: false));
        }

        [Fact]
        public void AbsentFile_IsNotYetUntilTheBudgetExpires()
        {
            Assert.Equal(ScreenshotPollOutcome.NotYet,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: false, bytes: -1, previousBytes: -1, budgetExpired: false));
            Assert.Equal(ScreenshotPollOutcome.TimedOut,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: false, bytes: -1, previousBytes: -1, budgetExpired: true));
        }

        [Fact]
        public void SettledWinsOverAnExpiredBudget()
        {
            // ORDER MATTERS: a capture that landed on the very frame the budget expired is a
            // success. Reporting it as screenshot-not-written would red a run over a file
            // that is sitting in the artifact folder.
            Assert.Equal(ScreenshotPollOutcome.Settled,
                TestCommandCaptureScreenshot.DecidePoll(
                    exists: true, bytes: 4096, previousBytes: 4096, budgetExpired: true));
        }

        // ----- paths -----

        [Fact]
        public void RelativePath_IsTheHarvestedDirectory_WithForwardSlashes()
        {
            // run.py harvests <instance>/Screenshots/ and nowhere else, so this is not a
            // cosmetic choice: a capture written anywhere else never reaches a result folder.
            Assert.Equal("Screenshots/ksc-main-advanced.png",
                TestCommandCaptureScreenshot.RelativePathFor("ksc-main-advanced"));
            Assert.Equal("Screenshots", TestCommandCaptureScreenshot.ScreenshotsDirName);
            Assert.DoesNotContain("\\",
                TestCommandCaptureScreenshot.RelativePathFor("x"));
        }

        [Fact]
        public void FileName_IsTheLabelPlusPng()
        {
            Assert.Equal("x.png", TestCommandCaptureScreenshot.FileNameFor("x"));
            Assert.Equal(".png", TestCommandCaptureScreenshot.ScreenshotExtension);
        }

        // ----- payload -----

        [Fact]
        public void Payload_CarriesEveryKey_InvariantlyFormatted()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandCaptureScreenshot.BuildPayload("ksc-settings-basic", 123456, 2, true);
            Assert.Equal(new[] { "label", "path", "bytes", "superSize", "overwrote" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("ksc-settings-basic", Value(payload, "label"));
            Assert.Equal("Screenshots/ksc-settings-basic.png", Value(payload, "path"));
            Assert.Equal("123456", Value(payload, "bytes"));
            Assert.Equal("2", Value(payload, "superSize"));
            Assert.Equal("true", Value(payload, "overwrote"));
        }

        [Fact]
        public void Payload_AlwaysCarriesOverwrote_BothWays()
        {
            // The EnterMapView `alreadyOpen` rule: a census reading its own results needs to
            // know an earlier image with that label was replaced, and an ABSENT key would be
            // indistinguishable from an older seam build.
            Assert.Equal("false",
                Value(TestCommandCaptureScreenshot.BuildPayload("a", 1, 1, false), "overwrote"));
            Assert.Equal("true",
                Value(TestCommandCaptureScreenshot.BuildPayload("a", 1, 1, true), "overwrote"));
        }

        [Fact]
        public void Payload_BytesIsCultureInvariant()
        {
            // The harness parses this line; a ro-RO / de-DE host must not print a group
            // separator or a comma decimal into it.
            using (new CultureSwap("de-DE"))
            {
                Assert.Equal("1234567",
                    Value(TestCommandCaptureScreenshot.BuildPayload("a", 1234567, 1, false),
                          "bytes"));
            }
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
