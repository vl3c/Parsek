using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS, owner ruling 2026-11-20: a rescue the
    /// contract rebuild re-activates in Flight or an editor gets its stranded vessel at the next
    /// Space Center or Tracking Station visit. These pin the persisted pending list that carries
    /// it there across scene changes and save / load, and the load policy cells that install it.
    /// </summary>
    [Collection("Sequential")]
    public class RescueAcceptDeferralsTests : IDisposable
    {
        private const string Rescue = "e8ba5ea6-570c-4b7f-99b4-44c9dee51030";
        private const string Other = "0c1d2e3f-4a5b-4c6d-8e7f-901234567890";
        private readonly List<string> logLines = new List<string>();

        public RescueAcceptDeferralsTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RescueAcceptDeferrals.ResetForTesting();
        }

        public void Dispose()
        {
            RescueAcceptDeferrals.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void Add_ListsAContractOnce_InItsCanonicalForm()
        {
            Assert.True(RescueAcceptDeferrals.Add(Rescue.ToUpperInvariant(), "rebuild scene=FLIGHT"));
            Assert.False(RescueAcceptDeferrals.Add(" " + Rescue + " ", "rebuild scene=FLIGHT"));
            Assert.Equal(1, RescueAcceptDeferrals.Count);
            Assert.Equal(Rescue, RescueAcceptDeferrals.Pending[0]);
            Assert.Contains(Rescue, RescueAcceptDeferrals.Pending);
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("Rescue accept deferral listed: contractId='" + Rescue + "'")
                && l.Contains("rebuild scene=FLIGHT") && l.Contains("pending=1"));
            Assert.Contains(logLines, l => l.Contains("Rescue accept deferral already listed"));
        }

        [Fact]
        public void Add_RefusesAnIdThatIsNotAGuid()
        {
            Assert.False(RescueAcceptDeferrals.Add("not-a-guid", "test"));
            Assert.False(RescueAcceptDeferrals.Add(null, "test"));
            Assert.False(RescueAcceptDeferrals.Add(Guid.Empty.ToString(), "test"));
            Assert.Equal(0, RescueAcceptDeferrals.Count);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("is not a contract guid"));
        }

        [Fact]
        public void Remove_TakesAListedContractOff()
        {
            RescueAcceptDeferrals.Add(Rescue, "test");
            Assert.False(RescueAcceptDeferrals.Remove(Other, "test"));
            Assert.True(RescueAcceptDeferrals.Remove(Rescue, "drain regenerated"));
            Assert.Equal(0, RescueAcceptDeferrals.Count);
            Assert.Contains(logLines, l => l.Contains("Rescue accept deferral removed: contractId='" + Rescue + "'")
                && l.Contains("drain regenerated") && l.Contains("pending=0"));
        }

        [Fact]
        public void SaveAndLoad_RoundTripTheList()
        {
            RescueAcceptDeferrals.Add(Rescue, "test");
            RescueAcceptDeferrals.Add(Other, "test");
            var parsek = new ConfigNode("SCENARIO");
            Assert.Equal(2, RescueAcceptDeferrals.SaveInto(parsek));

            ConfigNode written = parsek.GetNode(RescueAcceptDeferrals.NodeName);
            Assert.NotNull(written);
            Assert.Equal(new[] { Rescue, Other }, written.GetValues(RescueAcceptDeferrals.ValueName));

            RescueAcceptDeferrals.ResetForTesting();
            RescueAcceptDeferrals.Replace(RescueAcceptDeferrals.LoadFrom(parsek), "load Cold");
            Assert.Equal(new[] { Rescue, Other }, RescueAcceptDeferrals.Pending);
            Assert.Contains(logLines, l => l.Contains("Rescue accept deferrals installed (load Cold): pending=2"));
        }

        [Fact]
        public void SaveInto_EmptyListWritesNothing_AndDropsAStaleCopy()
        {
            var parsek = new ConfigNode("SCENARIO");
            parsek.AddNode(RescueAcceptDeferrals.NodeName).AddValue(RescueAcceptDeferrals.ValueName, Rescue);
            Assert.Equal(0, RescueAcceptDeferrals.SaveInto(parsek));
            Assert.Null(parsek.GetNode(RescueAcceptDeferrals.NodeName));
        }

        [Fact]
        public void LoadFrom_SkipsUnreadableIds_AndAnAbsentNodeIsEmpty()
        {
            Assert.Empty(RescueAcceptDeferrals.LoadFrom(new ConfigNode("SCENARIO")));
            Assert.Empty(RescueAcceptDeferrals.LoadFrom(null));

            var parsek = new ConfigNode("SCENARIO");
            ConfigNode n = parsek.AddNode(RescueAcceptDeferrals.NodeName);
            n.AddValue(RescueAcceptDeferrals.ValueName, Rescue);
            n.AddValue(RescueAcceptDeferrals.ValueName, "garbage");
            n.AddValue(RescueAcceptDeferrals.ValueName, Rescue);
            Assert.Equal(new List<string> { Rescue }, RescueAcceptDeferrals.LoadFrom(parsek));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("skipped 1 unreadable contract id(s) of 3"));
        }

        [Fact]
        public void Replace_And_Clear_InstallOrEmptyTheList()
        {
            RescueAcceptDeferrals.Add(Rescue, "test");
            RescueAcceptDeferrals.Replace(new List<string> { Other, "bad", Other }, "load InSession");
            Assert.Equal(new[] { Other }, RescueAcceptDeferrals.Pending);
            RescueAcceptDeferrals.Clear("load PlainRewind (Clear)");
            Assert.Equal(0, RescueAcceptDeferrals.Count);
            Assert.Contains(logLines, l => l.Contains("Rescue accept deferrals cleared (load PlainRewind (Clear)): dropped=1"));
        }

        [Theory]
        // The list describes the world of the save that carries it.
        [InlineData("Cold", "Save")]
        [InlineData("ReFlyStart", "Save")]
        [InlineData("StockRevert", "Save")]
        [InlineData("QuickloadFlight", "Save")]
        [InlineData("InSessionOther", "Save")]
        // Todo RESCUE-DEFERRAL-LOST-ON-REWIND-LOAD: the persistent.sfs splice keeps the landed
        // save's own list (the rewind save's, the rewind point's), so the node's copy is the
        // landed world's on these two loads too.
        [InlineData("PlainRewind", "Save")]
        [InlineData("DiscardReFly", "Save")]
        public void LoadPolicy_TakesTheLoadedSavesCopy_OnEveryLoad(
            string kindName, string expected)
        {
            var kind = (LoadKind)Enum.Parse(typeof(LoadKind), kindName);
            LoadReconcileDecision d = LoadReconcilePolicy.Decide(kind, LoadStateCategory.RescueAcceptDeferrals);
            Assert.Equal(expected, d.Action.ToString());
            Assert.Null(d.KnownGapTodoId);
            Assert.True(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.RescueAcceptDeferrals));
            Assert.Equal(LoadReconcileAction.Save,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.RescueAcceptDeferrals).Action);
        }

        [Theory]
        [InlineData((int)EarlyLoadKind.PlainRewind)]
        [InlineData((int)EarlyLoadKind.DiscardReFly)]
        public void LoadPolicy_RewindAndDiscardCells_NameTheSplice(int early)
        {
            LoadReconcileDecision d = LoadReconcilePolicy.DecideEarly(
                (EarlyLoadKind)early, LoadStateCategory.RescueAcceptDeferrals);
            Assert.Equal(LoadReconcileAction.Save, d.Action);
            Assert.False(d.IsKnownGap);
            Assert.Contains("SpliceLiveParsekScenarioNode", d.Reason);
        }

        // ================================================================
        // Todo RESCUE-DEFERRAL-LOST-ON-REWIND-LOAD: the persistent.sfs splice a plain rewind
        // (RewindLauncher.WriteRewindGameAsPersistent) and a Discard Re-fly
        // (RevertInterceptor.WriteRewindPointGameAsPersistent) write keeps the landed save's own
        // list: it names the rescues Active in THAT world whose stranded vessel that world lacks.
        // ================================================================

        private static ConfigNode ParsekNode(params string[] deferredIds)
        {
            var n = new ConfigNode("SCENARIO");
            n.AddValue("name", "ParsekScenario");
            if (deferredIds.Length > 0)
            {
                ConfigNode list = n.AddNode(RescueAcceptDeferrals.NodeName);
                foreach (string id in deferredIds)
                    list.AddValue(RescueAcceptDeferrals.ValueName, id);
            }
            return n;
        }

        [Fact]
        public void Splice_KeepsTheLandedSavesOwnList_NotTheSessions()
        {
            ConfigNode landed = ParsekNode(Rescue);
            ConfigNode session = ParsekNode(Other);
            session.AddNode("RECORDING_TREE").AddValue("id", "tree-committed-later");

            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(landed, session));

            // The rest of the node is the session's; the deferral list is the landed world's.
            Assert.Single(landed.GetNodes("RECORDING_TREE"));
            Assert.Single(landed.GetNodes(RescueAcceptDeferrals.NodeName));
            Assert.Equal(new List<string> { Rescue }, RescueAcceptDeferrals.LoadFrom(landed));
            Assert.Contains(logLines, l => l.Contains("[RewindSave]")
                && l.Contains("kept the landed save's own " + RescueAcceptDeferrals.NodeName)
                && l.Contains("landed=1 session=1"));

            // The landed load then installs it and the next Space Center visit drains it.
            RescueAcceptDeferrals.Replace(RescueAcceptDeferrals.LoadFrom(landed), "load PlainRewind");
            Assert.Equal(new[] { Rescue }, RescueAcceptDeferrals.Pending);
        }

        [Fact]
        public void Splice_LandedSaveWithoutAList_DropsTheSessions()
        {
            ConfigNode landed = ParsekNode();
            ConfigNode session = ParsekNode(Other);

            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(landed, session));

            Assert.Null(landed.GetNode(RescueAcceptDeferrals.NodeName));
            Assert.Empty(RescueAcceptDeferrals.LoadFrom(landed));
            Assert.Contains(logLines, l => l.Contains("landed=0 session=1"));
        }

        [Fact]
        public void Splice_Refused_LeavesTheLandedListUntouched()
        {
            ConfigNode landed = ParsekNode(Rescue);
            Assert.NotNull(RevertInterceptor.SpliceLiveParsekScenarioNode(landed, null));
            Assert.Equal(new List<string> { Rescue }, RescueAcceptDeferrals.LoadFrom(landed));
        }
    }
}
