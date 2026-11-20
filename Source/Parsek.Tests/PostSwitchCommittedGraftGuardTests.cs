using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo POST-SWITCH-GRAFTS-COMMITTED-VESSEL (owner ruling Q16): the post-switch watcher's
    /// fresh start under a live tree attached any vessel as a Launch child of the recording the
    /// player left, a vessel of another committed mission included. The guard refuses that graft;
    /// the vessel continues its own mission once the live tree is committed.
    /// </summary>
    public class PostSwitchCommittedGraftGuardTests
    {
        private const ParsekFlight.PostSwitchAutoRecordStartDecision Fresh =
            ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording;

        // catches: the graft itself. A loaded vessel of another committed tree, switched to
        // while a live tree records, reached StartFreshRecording and was attached to the live tree.
        [Fact]
        public void FreshStart_UnderLiveTree_OfSeparateCommittedVessel_IsRefused()
        {
            Assert.True(ParsekFlightPostSwitchAutoRecord.IsPostSwitchGraftOfSeparateCommittedVessel(
                Fresh, hasActiveTree: true, matchedCommittedTree: true,
                matchedTreeId: "stationTree", activeTreeId: "shipTree"));
        }

        // catches: the guard swallowing ordinary outsiders (a vessel no committed tree claims
        // still starts its recording under the live tree, as before).
        [Fact]
        public void FreshStart_OfUncommittedOutsider_IsNotRefused()
        {
            Assert.False(ParsekFlightPostSwitchAutoRecord.IsPostSwitchGraftOfSeparateCommittedVessel(
                Fresh, true, false, null, "shipTree"));
        }

        // catches: the guard refusing a vessel of the live tree's own committed original (the
        // live tree is that tree's clone), which is not another mission.
        [Fact]
        public void FreshStart_OfVesselInTheLiveTreesOwnCommittedTree_IsNotRefused()
        {
            Assert.False(ParsekFlightPostSwitchAutoRecord.IsPostSwitchGraftOfSeparateCommittedVessel(
                Fresh, true, true, "sameTree", "sameTree"));
        }

        // catches: the guard firing with no live tree (StartRecording then makes no graft; the
        // committed-tree restore needs no live tree) or on a promotion of a tracked member.
        [Theory]
        [InlineData(false, (int)ParsekFlight.PostSwitchAutoRecordStartDecision.StartFreshRecording)]
        [InlineData(true, (int)ParsekFlight.PostSwitchAutoRecordStartDecision.PromoteTrackedRecording)]
        [InlineData(true, (int)ParsekFlight.PostSwitchAutoRecordStartDecision.None)]
        public void OnlyAFreshStartUnderALiveTreeIsAGraft(bool hasActiveTree, int decision)
        {
            Assert.False(ParsekFlightPostSwitchAutoRecord.IsPostSwitchGraftOfSeparateCommittedVessel(
                (ParsekFlight.PostSwitchAutoRecordStartDecision)decision, hasActiveTree, true,
                "stationTree", "shipTree"));
        }

        [Fact]
        public void RefusalLine_NamesTheVesselTreesAndReason_Invariantly()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = ParsekFlightPostSwitchAutoRecord.FormatPostSwitchGraftRefusal(
                    "Station", 1234u, ParsekFlight.PostSwitchAutoRecordTrigger.EngineActivity,
                    "stationTree", "shipTree");
                Assert.Contains("Post-switch fresh start refused: vessel='Station' pid=1234 trigger=EngineActivity", line);
                Assert.Contains("reason=separate-committed-vessel matchedTreeId=stationTree activeTreeId=shipTree", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // catches: the guard not wired before the graft (PrepareActiveTreeForFreshPostSwitchRecording
        // is the one place that writes the Launch branch point).
        [Fact]
        public void TryStartPostSwitchAutoRecord_RunsTheGuardBeforeTheGraft()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string src = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Controllers",
                "ParsekFlightPostSwitchAutoRecord.cs"));
            int start = src.IndexOf("private bool TryStartPostSwitchAutoRecord(", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = src.IndexOf("internal void OnPostSwitchAutoRecordPhysicsFrame(", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string body = src.Substring(start, end - start);
            int guard = body.IndexOf("IsPostSwitchGraftOfSeparateCommittedVessel(", StringComparison.Ordinal);
            int disarm = body.IndexOf("DisarmPostSwitchAutoRecord(\"separate-committed-vessel\")", StringComparison.Ordinal);
            int graft = body.IndexOf("PrepareActiveTreeForFreshPostSwitchRecording(", StringComparison.Ordinal);
            Assert.True(guard > 0 && disarm > guard && graft > disarm,
                "TryStartPostSwitchAutoRecord must refuse a separate committed vessel before it grafts");
        }
    }
}
