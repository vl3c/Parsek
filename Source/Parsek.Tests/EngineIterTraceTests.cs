using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pinned format contract for the engine-iteration trace line emitted by
    /// <see cref="GhostPlaybackEngine.UpdatePlayback"/> when
    /// ghostRenderTracing is on. The line bypasses
    /// <c>GhostRenderTrace.ShouldEmitPhase</c> / IsDetailedWindowOpen so a
    /// future ghost-vanish repro can answer: did the recording reach the
    /// per-trajectory loop, what producer-side skipReason did it carry, was
    /// anchorReFlyUnstable set (the engine consults that flag mid-loop and
    /// will skip even when skipReason is None), did its trajectory have
    /// renderable data, and was <c>ghostStates[i]</c> still populated.
    ///
    /// The format helpers are pure static so the contract can be pinned
    /// without any KSP-runtime dependency.
    ///
    /// Log-sink tests in this class mutate <see cref="ParsekLog.TestSinkForTesting"/>
    /// and the shared rate-limit map; they need <see cref="System.IDisposable"/>
    /// + <c>[Collection("Sequential")]</c> per <c>.claude/CLAUDE.md</c>.
    /// </summary>
    [Collection("Sequential")]
    public class EngineIterTraceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EngineIterTraceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // Per-test fresh clock so the rate-limit map's `lastEmitSeconds`
            // for the `Engine|engine-frame-iter` composite key starts in a
            // known state. The first VerboseRateLimited call always emits
            // (cold key), so we don't need to clear the map between tests
            // for the assertions below, but the override keeps the test
            // self-contained against neighbour fixtures.
            ParsekLog.ClockOverrideForTesting = () => 0.0;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void FormatRecordingIdShort_Truncates_To_First_Eight_Characters()
        {
            string longId = "rec_152453a952804ee7b54f129bdfe2fdc1";

            string shortId = GhostPlaybackEngine.FormatRecordingIdShort(longId);

            Assert.Equal("rec_1524", shortId);
        }

        [Fact]
        public void FormatRecordingIdShort_Returns_Full_Value_When_Eight_Or_Fewer_Characters()
        {
            Assert.Equal("rec_bc0c", GhostPlaybackEngine.FormatRecordingIdShort("rec_bc0c"));
            Assert.Equal("abc", GhostPlaybackEngine.FormatRecordingIdShort("abc"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatRecordingIdShort_Returns_None_Placeholder_For_Null_Or_Empty(string value)
        {
            Assert.Equal("<none>", GhostPlaybackEngine.FormatRecordingIdShort(value));
        }

        [Fact]
        public void FormatEngineIterEntry_Active_Trajectory_Reports_None_Skip()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 9,
                recordingId: "rec_152453a952804ee7b54f129bdfe2fdc1",
                skipReason: GhostPlaybackSkipReason.None,
                anchorReFlyUnstable: false,
                hasRenderableData: true,
                inGhostStates: true,
                endUT: 1740.436);

            // Compact format the spec calls out:
            // [i=N rec=ID skip=R aru=T/F hd=T/F hs=T/F endUT=X]
            Assert.Equal(
                "[i=9 rec=rec_1524 skip=None aru=F hd=T hs=T endUT=1740.4]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Skipped_Trajectory_Reports_Producer_Reason()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 0,
                recordingId: "691dd66b032b4919b752597f48692fd0",
                skipReason: GhostPlaybackSkipReason.SessionSuppressed,
                anchorReFlyUnstable: false,
                hasRenderableData: true,
                inGhostStates: false,
                endUT: 131.55);

            Assert.Equal(
                "[i=0 rec=691dd66b skip=session-suppressed aru=F hd=T hs=F endUT=131.6]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_NoRenderableData_Continuation_Tail_Marker()
        {
            // Re-Fly continuation rec_bc0c... has hasRenderableData=False and
            // is suppressed via the engine's NoRenderableData fast-skip. The
            // engine-iter line must show hd=F so a log reader can tell at a
            // glance that the slot is the post-supersede continuation marker.
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 10,
                recordingId: "rec_bc0cd07fde9840e4956ce30a524ec670",
                skipReason: GhostPlaybackSkipReason.NoRenderableData,
                anchorReFlyUnstable: false,
                hasRenderableData: false,
                inGhostStates: false,
                endUT: 128.27);

            Assert.Equal(
                "[i=10 rec=rec_bc0c skip=no-renderable-data aru=F hd=F hs=F endUT=128.3]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_AnchorReFlyUnstable_Flag_Reported_As_T()
        {
            // H2 hypothesis: the engine reads f.anchorReFlyUnstable later in
            // the UpdatePlayback per-trajectory loop and hides/skips the
            // ghost as anchor-refly-unstable AFTER the producer-side
            // skipGhost gate. So f.skipReason can be None while the engine
            // still skips the ghost mid-loop. The trace must surface the
            // producer-side anchorReFlyUnstable flag independently so a
            // future repro can tell "rendering normally" (aru=F) apart from
            // "engine will skip this frame" (aru=T) without scrolling for a
            // separate GuardSkip emit (which is rate-limited and may be
            // absent in the same 1.0s sample window as the iter line).
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 9,
                recordingId: "rec_152453a952804ee7b54f129bdfe2fdc1",
                skipReason: GhostPlaybackSkipReason.None,
                anchorReFlyUnstable: true,
                hasRenderableData: true,
                inGhostStates: true,
                endUT: 1740.436);

            Assert.Contains("aru=T", entry);
            Assert.Equal(
                "[i=9 rec=rec_1524 skip=None aru=T hd=T hs=T endUT=1740.4]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Handles_Null_Recording_Id()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 3,
                recordingId: null,
                skipReason: GhostPlaybackSkipReason.None,
                anchorReFlyUnstable: false,
                hasRenderableData: false,
                inGhostStates: false,
                endUT: 0.0);

            Assert.Equal(
                "[i=3 rec=<none> skip=None aru=F hd=F hs=F endUT=0.0]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Uses_InvariantCulture_For_EndUT()
        {
            // Comma-locale systems would otherwise emit "1740,4" which breaks
            // every downstream log parser (Python collect-logs.py, grep, the
            // KSP log validator). Pin the invariant: a comma-locale render
            // must still produce a period decimal separator.
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                    index: 9,
                    recordingId: "rec_1524",
                    skipReason: GhostPlaybackSkipReason.None,
                    anchorReFlyUnstable: false,
                    hasRenderableData: true,
                    inGhostStates: true,
                    endUT: 1740.4);
                Assert.Contains("endUT=1740.4", entry);
                Assert.DoesNotContain("1740,4", entry);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // --- Visibility-outcome token. Closes the gap that made the
        // loop-top-only engine-iter trace inconclusive for the
        // "active=1 but invisible" ghost-vanish symptom: skip=None at the loop
        // top did NOT prove the ghost rendered, because retire / zone-hide
        // happens AFTER positioning. The outcome token reports the per-frame
        // visibility verdict read from the post-loop GhostPlaybackState.

        [Fact]
        public void FormatEngineIterOutcome_No_Ghost_Reports_None()
        {
            Assert.Equal(
                "[out:none]",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    hasGhost: false,
                    meshVisible: false,
                    anchorRetiredThisFrame: false,
                    zone: RenderingZone.Physics,
                    renderDistance: double.NaN));
        }

        [Fact]
        public void FormatEngineIterOutcome_Visible_Ghost_Reports_Vis_T()
        {
            Assert.Equal(
                "[out:vis=T retired=F zone=Physics rdist=1200m]",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    hasGhost: true,
                    meshVisible: true,
                    anchorRetiredThisFrame: false,
                    zone: RenderingZone.Physics,
                    renderDistance: 1200.0));
        }

        [Fact]
        public void FormatEngineIterOutcome_Retire_Is_Distinguishable_From_Zone_Cull()
        {
            // The two competing hypotheses for a positioned-but-invisible ghost
            // are relative-anchor retire vs distance LOD. The outcome token
            // separates them: the retire path reports vis=F retired=T.
            Assert.Equal(
                "[out:vis=F retired=T zone=Visual rdist=4200m]",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    hasGhost: true,
                    meshVisible: false,
                    anchorRetiredThisFrame: true,
                    zone: RenderingZone.Visual,
                    renderDistance: 4200.0));
        }

        [Fact]
        public void FormatEngineIterOutcome_Zone_Beyond_Cull_Reports_Distance()
        {
            // Distance LOD cull (H4): vis=F retired=F zone=Beyond, with the
            // render distance so a reader can tell a real >120km gap from a
            // floating-origin seam phantom value.
            Assert.Equal(
                "[out:vis=F retired=F zone=Beyond rdist=350000m]",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    hasGhost: true,
                    meshVisible: false,
                    anchorRetiredThisFrame: false,
                    zone: RenderingZone.Beyond,
                    renderDistance: 350000.0));
        }

        [Fact]
        public void FormatEngineIterOutcome_Unresolved_Distance_Reported_As_Unresolved()
        {
            // NaN / MaxValue render distances (anchor unresolved) route through
            // RenderingZoneManager.FormatDistanceForLog -> "unresolved", never a
            // raw 1.7E308 that would break the log parsers.
            Assert.Equal(
                "[out:vis=F retired=T zone=Beyond rdist=unresolved]",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    hasGhost: true,
                    meshVisible: false,
                    anchorRetiredThisFrame: true,
                    zone: RenderingZone.Beyond,
                    renderDistance: double.MaxValue));
            Assert.Contains(
                "rdist=unresolved",
                GhostPlaybackEngine.FormatEngineIterOutcome(
                    true, false, true, RenderingZone.Beyond, double.NaN));
        }

        [Fact]
        public void EmitEngineIterTrace_Renders_Combined_Input_And_Outcome_Tokens()
        {
            // UpdatePlayback's post-loop pass concatenates the input entry and
            // the outcome token per index. Pin that the rendered line carries
            // BOTH so the next-repro grep `[Engine] engine-frame-iter` surfaces
            // vis / retired / zone for the affected ghost in one line.
            string sample =
                GhostPlaybackEngine.FormatEngineIterEntry(
                    9, "rec_152453a952804ee7b54f129bdfe2fdc1",
                    GhostPlaybackSkipReason.None, false, true, true, 1740.436)
                + GhostPlaybackEngine.FormatEngineIterOutcome(
                    true, false, true, RenderingZone.Visual, 4200.0);

            GhostPlaybackEngine.EmitEngineIterTrace(sample);

            Assert.Contains(logLines, l =>
                l.Contains("[Engine] engine-frame-iter")
                && l.Contains("[i=9 rec=rec_1524 skip=None aru=F hd=T hs=T endUT=1740.4]")
                && l.Contains("[out:vis=F retired=T zone=Visual rdist=4200m]"));
        }

        // --- Rendered-line contract (closes the PR #837 P2 bug where the
        // `engine-frame-iter` token only lived in the rate-limit key and was
        // invisible in KSP.log). The docs' next-repro grep
        // `[Engine] engine-frame-iter` must match the actual rendered line.

        [Fact]
        public void FormatEngineIterMessage_Prefixes_Token_To_Entries()
        {
            string sampleEntries =
                "[i=9 rec=rec_1524 skip=None aru=F hd=T hs=T endUT=1740.4]";

            string rendered = GhostPlaybackEngine.FormatEngineIterMessage(sampleEntries);

            Assert.Equal("engine-frame-iter " + sampleEntries, rendered);
            Assert.StartsWith("engine-frame-iter ", rendered);
        }

        [Fact]
        public void FormatEngineIterMessage_Empty_Entries_Returns_Bare_Token()
        {
            Assert.Equal("engine-frame-iter", GhostPlaybackEngine.FormatEngineIterMessage(""));
            Assert.Equal("engine-frame-iter", GhostPlaybackEngine.FormatEngineIterMessage(null));
        }

        [Fact]
        public void EmitEngineIterTrace_Rendered_Line_Contains_Token_And_Engine_Tag()
        {
            // The docs in CHANGELOG.md and docs/dev/todo-and-known-bugs.md
            // tell investigators to grep `[Engine] engine-frame-iter` in the
            // KSP.log. ParsekLog.VerboseRateLimited uses the `key` argument
            // only as a rate-limit composite-key suffix (ParsekLog.cs:176)
            // — only the `message` argument is rendered into the log line
            // via Write (ParsekLog.cs:194, ParsekLog.cs:406). Without the
            // PR #837 P2 fix the rendered line would be
            // `[Parsek][VERBOSE][Engine] [i=...]` (missing the token); the
            // grep would not match and the new instrumentation would be
            // effectively invisible. This test pins the end-to-end contract.
            string sampleEntries =
                "[i=9 rec=rec_1524 skip=None aru=F hd=T hs=T endUT=1740.4],"
                + "[i=10 rec=rec_bc0c skip=no-renderable-data aru=F hd=F hs=F endUT=128.3]";

            GhostPlaybackEngine.EmitEngineIterTrace(sampleEntries);

            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("engine-frame-iter")
                && l.Contains("rec_1524")
                && l.Contains("rec_bc0c"));
            // Pin the full grep pattern the docs reference so a future
            // refactor that drops the literal substring fails this test
            // rather than silently breaking the next-repro workflow.
            Assert.Contains(logLines, l => l.Contains("[Engine] engine-frame-iter"));
        }

        [Fact]
        public void EmitEngineIterTrace_Subsystem_Tag_Is_Engine_Not_Engine_FrameIter()
        {
            // Defensive: confirm we never split the subsystem column the way
            // the older `[Parsek][VERBOSE][Engine|engine-frame-iter] ...`
            // composite-key style would have. The subsystem column must
            // remain `[Engine]` so existing engine-tag greps keep working.
            GhostPlaybackEngine.EmitEngineIterTrace(
                "[i=0 rec=test123 skip=None aru=F hd=T hs=T endUT=10.0]");

            Assert.Contains(logLines, l => l.Contains("[Engine] "));
            Assert.DoesNotContain(logLines, l => l.Contains("[Engine|"));
            Assert.DoesNotContain(logLines, l => l.Contains("[engine-frame-iter]"));
        }
    }
}
