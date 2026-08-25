using System;
using System.Text.RegularExpressions;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 RECORDER-level decisions that are pure enough to drive headlessly: the auto-flush
    /// clobber guard and the enum token tables that keep the armed per-frame path off
    /// <c>Enum.ToString()</c>.
    /// </summary>
    public class RenderCompositionRecorderTests
    {
        // =====================================================================================
        //  Auto-flush clobber guard
        // =====================================================================================
        //
        // The manifest is written to ONE fixed path, so every auto flush overwrites the previous
        // one. The failure this guards is not "an empty manifest exists" but "a populated manifest
        // was REPLACED by an empty one" - a scene bounce or a process teardown arriving after the
        // accumulation was Reset.

        [Fact]
        public void AutoFlush_FirstFlushOfAProcessAlwaysWrites_EvenWithNoDwells()
        {
            // A KSC / tracking-station-only session legitimately produces plan + clock records and no
            // dwells at all. That manifest IS the evidence, so the first flush must never be skipped.
            Assert.False(RenderCompositionRecorder.ShouldSkipAutoFlush(
                closedDwells: 0, openDwells: 0, transitions: 0, alreadyWroteThisProcess: false));
        }

        [Fact]
        public void AutoFlush_SkipsOnlyWhenAManifestExistsAndNothingWasObservedSince()
        {
            Assert.True(RenderCompositionRecorder.ShouldSkipAutoFlush(
                closedDwells: 0, openDwells: 0, transitions: 0, alreadyWroteThisProcess: true));
        }

        [Theory]
        [InlineData(1, 0, 0)]
        [InlineData(0, 1, 0)]
        [InlineData(0, 0, 1)]
        [InlineData(3, 2, 5)]
        public void AutoFlush_WritesWheneverAnythingWasObservedSinceTheLastManifest(
            int closed, int open, int transitions)
        {
            Assert.False(RenderCompositionRecorder.ShouldSkipAutoFlush(
                closed, open, transitions, alreadyWroteThisProcess: true));
        }

        [Fact]
        public void AutoFlush_SkipReasonIsAGrepStableToken()
        {
            // The skip is logged, not silent: a run whose manifest looks stale must be diagnosable
            // from KSP.log without attaching a debugger.
            Assert.Equal("no-new-observation", RenderCompositionRecorder.AutoFlushSkipReason);
        }

        // =====================================================================================
        //  Enum token tables
        // =====================================================================================

        /// <summary>
        /// The tables must produce EXACTLY <c>Enum.ToString()</c> for every declared value - they are
        /// an allocation optimisation, never a re-spelling. Driven by enumerating the enums, which is
        /// also how the production tables are built: the
        /// <c>LineBlinkWindowExitExemptionTests</c> source gate reserves the two named
        /// render-window-coverage member spellings for the four measuring decision sites in
        /// <c>GhostOrbitLinePatch</c>, so neither the table nor this cell may write them out.
        /// </summary>
        [Theory]
        [InlineData(typeof(MapRenderTrace.RenderWindowCoverage))]
        [InlineData(typeof(Treatment))]
        [InlineData(typeof(Coverage))]
        public void EnumTokenTables_MatchEnumToStringForEveryValue(Type enumType)
        {
            foreach (object value in Enum.GetValues(enumType))
            {
                string expected = value.ToString();
                string actual = RenderCompositionRecorder.EnumTokenForTesting(enumType, value);
                Assert.Equal(expected, actual);
            }
        }

        /// <summary>
        /// The source gate itself, applied to the recorder: the table is built by ENUMERATING the
        /// enum precisely so no member name is written out here. If a later edit reached for the
        /// literal spelling instead, <c>LineBlinkWindowExitExemptionTests</c> would red - this cell
        /// names the reason in the file that would have to be changed.
        /// </summary>
        [Fact]
        public void Recorder_NeverSpellsAMeasuringRenderWindowCoverageMember()
        {
            string source = ReadRecorderSource();
            var gate = new Regex(@"RenderWindowCoverage\s*\.\s*(Inside|Outside)");
            Assert.False(gate.IsMatch(source),
                "RenderCompositionRecorder.cs must not spell a measuring RenderWindowCoverage member: "
                + "only the four stamp sites in GhostOrbitLinePatch.cs may, and the recorder's token "
                + "table is built by enumerating the enum so it never has to.");
        }

        private static string ReadRecorderSource()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(
                    dir, "Source", "Parsek", "MapRender", "RenderCompositionRecorder.cs");
                if (System.IO.File.Exists(candidate))
                    return System.IO.File.ReadAllText(candidate);
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            Assert.True(false, "Could not locate RenderCompositionRecorder.cs from " + AppContext.BaseDirectory);
            return "";
        }
    }
}
