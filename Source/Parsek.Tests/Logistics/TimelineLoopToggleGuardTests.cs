using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// LST-1 mutual-exclusion guard for the Timeline window per-recording "L"
    /// loop toggle. The timeline "L" writes <see cref="Recording.LoopPlayback"/>
    /// directly (bypassing MissionStore), so the commit gate
    /// <see cref="TimelineWindowUI.TryCommitTimelineLoopToggle"/> is the only
    /// thing preventing a route-vs-manual owner collision (design §0.6) when a
    /// recording's tree is bound to an active supply route. These tests cover the
    /// pure guard predicate (<see cref="TimelineWindowUI.ShouldBlockTimelineLoopToggle"/>)
    /// and the commit gate's block + log path; the OnGUI grey itself
    /// (GUI.enabled = false) needs the Unity runtime and is not unit-tested.
    /// </summary>
    /// <remarks>
    /// Touches shared static state (<see cref="RouteStore"/>,
    /// <see cref="ParsekLog"/>), so the class is <c>[Collection("Sequential")]</c>
    /// and resets all of it in the ctor + Dispose.
    /// </remarks>
    [Collection("Sequential")]
    public class TimelineLoopToggleGuardTests : System.IDisposable
    {
        private const string GuardTag = "[RouteGuard]";
        private readonly List<string> logLines = new List<string>();

        public TimelineLoopToggleGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static RouteSourceRef SourceRef(string recId, string treeId)
        {
            return new RouteSourceRef { RecordingId = recId, TreeId = treeId };
        }

        // A route bound to one tree via a single source ref.
        private static Route RouteOnTree(string id, string treeId)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(RouteStatus.Active)
                .WithBackingMissionTreeId(treeId)
                .WithRecordingId("rec-" + treeId)
                .WithSourceRef(SourceRef("rec-" + treeId, treeId))
                .Build();
        }

        private static Recording Rec(string id, string treeId, bool loop)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "V-" + id,
                LoopPlayback = loop
            };
        }

        // -----------------------------------------------------------------
        // ShouldBlockTimelineLoopToggle (pure predicate)
        // -----------------------------------------------------------------

        [Fact]
        public void ShouldBlock_TrueWhenRecordingTreeBoundToActiveRoute()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", "tree-X", loop: false);

            Assert.True(TimelineWindowUI.ShouldBlockTimelineLoopToggle(rec));
        }

        [Fact]
        public void ShouldBlock_FalseForUnboundRecording()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", "tree-OTHER", loop: false);

            Assert.False(TimelineWindowUI.ShouldBlockTimelineLoopToggle(rec));
        }

        [Fact]
        public void ShouldBlock_FalseWhenNoRoutes()
        {
            Recording rec = Rec("rec-1", "tree-X", loop: false);

            Assert.False(TimelineWindowUI.ShouldBlockTimelineLoopToggle(rec));
        }

        [Fact]
        public void ShouldBlock_FalseForNullRecording()
        {
            Assert.False(TimelineWindowUI.ShouldBlockTimelineLoopToggle(null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ShouldBlock_FalseForNullOrEmptyTreeId(string treeId)
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", treeId, loop: false);

            Assert.False(TimelineWindowUI.ShouldBlockTimelineLoopToggle(rec));
        }

        // -----------------------------------------------------------------
        // TryCommitTimelineLoopToggle (commit gate + log)
        // -----------------------------------------------------------------

        [Fact]
        public void TryCommit_BlocksAndLogs_WhenRecordingTreeBound()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", "tree-X", loop: false);
            logLines.Clear();

            bool mayWrite = TimelineWindowUI.TryCommitTimelineLoopToggle(rec, requestedLoop: true);

            Assert.False(mayWrite);
            // The write is blocked: the gate returning false means the caller never
            // mutates LoopPlayback.
            Assert.False(rec.LoopPlayback);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains(GuardTag)
                && l.Contains("Timeline per-recording Loop blocked")
                && l.Contains("tree=tree-X")
                && l.Contains("request=True")
                && l.Contains("id=rec-1"));
        }

        [Fact]
        public void TryCommit_AllowsWrite_ForUnboundRecording()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", "tree-OTHER", loop: false);
            logLines.Clear();

            bool mayWrite = TimelineWindowUI.TryCommitTimelineLoopToggle(rec, requestedLoop: true);

            Assert.True(mayWrite);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Timeline per-recording Loop blocked"));
        }

        [Fact]
        public void TryCommit_AllowsWrite_WhenNoRoutes()
        {
            Recording rec = Rec("rec-1", "tree-X", loop: false);

            bool mayWrite = TimelineWindowUI.TryCommitTimelineLoopToggle(rec, requestedLoop: true);

            Assert.True(mayWrite);
        }

        [Fact]
        public void TryCommit_FalseForNullRecording()
        {
            Assert.False(TimelineWindowUI.TryCommitTimelineLoopToggle(null, requestedLoop: true));
        }

        // Turning a loop OFF on a bound recording is still blocked: a bound tree's
        // per-recording loop surface is route-owned in both directions. (The
        // separate route-activation clear path is what actually drops a stale
        // pre-existing manual loop.)
        [Fact]
        public void TryCommit_BlocksOffRequest_WhenBound()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));
            Recording rec = Rec("rec-1", "tree-X", loop: true);
            logLines.Clear();

            bool mayWrite = TimelineWindowUI.TryCommitTimelineLoopToggle(rec, requestedLoop: false);

            Assert.False(mayWrite);
            Assert.Contains(logLines, l =>
                l.Contains(GuardTag)
                && l.Contains("Timeline per-recording Loop blocked")
                && l.Contains("request=False"));
        }
    }
}
