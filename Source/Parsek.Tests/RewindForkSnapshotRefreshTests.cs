using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the in-place Re-Fly fork ghost-snapshot refresh
    /// (<see cref="RewindInvoker.TryRefreshForkSnapshotsFromLiveVessel"/> +
    /// <see cref="RewindInvoker.CountSnapshotParts"/>).
    ///
    /// Background: <see cref="RewindInvoker.CopyInheritedIdentityForFork"/>
    /// copies the inheritance source's <c>VesselSnapshot</c> and
    /// <c>GhostVisualSnapshot</c> onto the fork. The source's
    /// <c>GhostVisualSnapshot</c> was captured exactly once at
    /// recording-start (e.g. before staging) and never refreshed by
    /// breakup events. So a fork created post-staging would inherit a
    /// pre-staging full-vessel ghost snapshot — observed in 2026-05-07
    /// playtest as a lower-stage Re-Fly rendering an 84-part Kerbal X
    /// ghost. The new helper refreshes both snapshots from the live
    /// post-Strip vessel before the fork is published.
    ///
    /// The refresh path itself takes a Unity <see cref="UnityEngine.Vessel"/>
    /// — fully exercised only by the in-game test harness. xUnit covers
    /// the null/failure fallbacks and the helpers that don't depend on
    /// Unity types.
    /// </summary>
    [Collection("Sequential")]
    public class RewindForkSnapshotRefreshTests : System.IDisposable
    {
        private readonly List<string> _logLines = new List<string>();

        public RewindForkSnapshotRefreshTests()
        {
            ParsekLog.TestSinkForTesting = line => _logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void TryRefreshForkSnapshotsFromLiveVessel_NullProvisional_ReturnsFalse()
        {
            bool refreshed = RewindInvoker.TryRefreshForkSnapshotsFromLiveVessel(
                provisional: null, liveVessel: null, sessionId: "sess_test");

            Assert.False(refreshed);
        }

        [Fact]
        public void TryRefreshForkSnapshotsFromLiveVessel_NullLiveVessel_KeepsInheritedSnapshots()
        {
            // Simulate the post-CopyInheritedIdentityForFork state: provisional
            // already has the inherited stale snapshots. The refresh helper
            // must not clear them when the live vessel handle is null.
            ConfigNode inheritedVessel = new ConfigNode("VESSEL");
            inheritedVessel.AddNode("PART");
            inheritedVessel.AddNode("PART");
            ConfigNode inheritedGhost = inheritedVessel.CreateCopy();
            var provisional = new Recording
            {
                RecordingId = "fork-rec",
                VesselSnapshot = inheritedVessel,
                GhostVisualSnapshot = inheritedGhost,
            };

            bool refreshed = RewindInvoker.TryRefreshForkSnapshotsFromLiveVessel(
                provisional, liveVessel: null, sessionId: "sess_test");

            Assert.False(refreshed);
            Assert.Same(inheritedVessel, provisional.VesselSnapshot);
            Assert.Same(inheritedGhost, provisional.GhostVisualSnapshot);
            Assert.Contains(_logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("TryRefreshForkSnapshotsFromLiveVessel: live vessel null")
                && l.Contains("rec=fork-rec")
                && l.Contains("sess=sess_test"));
        }

        [Fact]
        public void CountSnapshotParts_Null_ReturnsZero()
        {
            Assert.Equal(0, RewindInvoker.CountSnapshotParts(null));
        }

        [Fact]
        public void CountSnapshotParts_NoPartChildren_ReturnsZero()
        {
            // ConfigNode without PART children — defensive shape that earlier
            // versions of the snapshot have hit (e.g. malformed restore
            // payload). Helper must return 0 not throw.
            ConfigNode snap = new ConfigNode("VESSEL");
            snap.AddValue("name", "no-parts-here");
            snap.AddNode("ACTIONGROUPS"); // a non-PART child

            Assert.Equal(0, RewindInvoker.CountSnapshotParts(snap));
        }

        [Fact]
        public void CountSnapshotParts_MultiplePartChildren_ReturnsCount()
        {
            ConfigNode snap = new ConfigNode("VESSEL");
            snap.AddNode("PART");
            snap.AddNode("PART");
            snap.AddNode("PART");
            snap.AddNode("ACTIONGROUPS");
            snap.AddNode("PART");

            // 4 PART children, 1 ACTIONGROUPS child — count must reflect
            // PART nodes only, not the total children.
            Assert.Equal(4, RewindInvoker.CountSnapshotParts(snap));
        }

        [Fact]
        public void CountSnapshotParts_DiagnosticUsage_IndicatesStaleVsRefreshedDelta()
        {
            // Realistic shape: an 84-part inherited ghost (pre-staging full
            // rocket) vs a 17-part live vessel snapshot (post-staging upper
            // stage). The helper must report the inherited size before the
            // refresh swaps it out — that count is what the diagnostic log
            // line surfaces so reviewers can see the staleness magnitude
            // when reading KSP.log.
            ConfigNode preStagingSnap = new ConfigNode("VESSEL");
            for (int i = 0; i < 84; i++) preStagingSnap.AddNode("PART");
            ConfigNode postStagingSnap = new ConfigNode("VESSEL");
            for (int i = 0; i < 17; i++) postStagingSnap.AddNode("PART");

            int inherited = RewindInvoker.CountSnapshotParts(preStagingSnap);
            int live = RewindInvoker.CountSnapshotParts(postStagingSnap);

            Assert.Equal(84, inherited);
            Assert.Equal(17, live);
            // The replaced(...) field in the live-refresh log line is what
            // makes the staleness magnitude observable in production logs.
            Assert.NotEqual(inherited, live);
        }
    }
}
