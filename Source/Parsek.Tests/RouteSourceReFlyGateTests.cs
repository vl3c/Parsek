using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q17 ("Refuse Re-Fly"): a flight a supply route is built from is not
    /// re-flyable. The refusal lives in <see cref="RewindInvoker.CanInvoke(RewindPoint, ChildSlot, out string)"/>,
    /// the one gate the Unfinished Flights row and the timeline row grey the Fly button with
    /// (its reason is the button's tooltip) and that <see cref="RewindInvoker.StartInvoke"/>
    /// reads again on the confirm click, so the mark and the click-block share one predicate.
    /// </summary>
    [Collection("Sequential")]
    public class RouteSourceReFlyGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes previousScene;
        private readonly string tempDir;

        public RouteSourceReFlyGateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;

            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-route-refly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = previousScene;
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { }
        }

        private static Recording Rec(string id, string treeId, MergeState state = MergeState.Immutable)
            => new Recording
            {
                RecordingId = id, VesselName = id, TreeId = treeId, MergeState = state,
                TerminalStateValue = TerminalState.Destroyed,
            };

        private static RecordingTree InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Test_" + treeId };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            RecordingStore.CommittedTrees.Add(tree);
            return tree;
        }

        private static ChildSlot Slot(int index, string origin)
            => new ChildSlot { SlotIndex = index, OriginChildRecordingId = origin, Controllable = true };

        private static RewindPoint Rp(string id, params ChildSlot[] slots)
            => new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = "bp_" + id,
                UT = 100.0,
                QuicksaveFilename = "Parsek/RewindPoints/" + id + ".sfs",
                ChildSlots = new List<ChildSlot>(slots),
            };

        private void InstallInvokableRp(RewindPoint rp)
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
            });
            EffectiveState.ResetCachesForTesting();
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sfs");
            File.WriteAllText(path, "GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => path;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;
            RewindInvoker.NowUtProviderForTesting = () => 500.0;
        }

        private static void AddRoute(string id, Recording source)
        {
            RouteStore.AddRoute(new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(RouteStatus.Active)
                .WithRecordingId(source.RecordingId)
                .WithSourceRef(new RouteSourceRef { RecordingId = source.RecordingId, TreeId = source.TreeId })
                .Build());
        }

        // catches: Re-Fly offered on a flight a supply route is built from; the merge's cut and
        // supersede would leave the route without its source.
        [Fact]
        public void CanInvoke_RefusesASlotWhoseFlightIsARouteSource_WithTheTooltipReason()
        {
            var origin = Rec("rec_probe", "tree_route", MergeState.CommittedProvisional);
            var tanker = Rec("rec_tanker", "tree_route");
            InstallTree("tree_route", origin, tanker);
            var slot = Slot(0, "rec_probe");
            var rp = Rp("rp_route", slot);
            InstallInvokableRp(rp);

            Assert.True(RewindInvoker.CanInvoke(rp, slot, out string reason), reason);

            AddRoute("route_1", tanker);

            Assert.False(RewindInvoker.CanInvoke(rp, slot, out reason));
            Assert.Equal(RewindInvoker.RouteSourceReFlyReason, reason);
            Assert.False(RewindInvoker.CanInvoke(rp, out reason));
            Assert.Equal(RewindInvoker.RouteSourceReFlyReason, reason);
            // The Fly button of both rows reads this predicate through the slot gate.
            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 0, out reason));
            Assert.Equal(RewindInvoker.RouteSourceReFlyReason, reason);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("CanInvoke: refused rp=rp_route")
                && l.Contains("tree=tree_route routeSourceRec=rec_tanker reason=route-source"));
        }

        [Fact]
        public void CanInvoke_ARouteOnAnotherFlight_DoesNotRefuse()
        {
            var origin = Rec("rec_probe", "tree_a", MergeState.CommittedProvisional);
            InstallTree("tree_a", origin);
            var other = Rec("rec_other", "tree_b");
            InstallTree("tree_b", other);
            var slot = Slot(0, "rec_probe");
            var rp = Rp("rp_a", slot);
            InstallInvokableRp(rp);
            AddRoute("route_b", other);

            Assert.True(RewindInvoker.CanInvoke(rp, slot, out string reason), reason);
        }

        [Fact]
        public void SlotFlightIsRouteSource_Table()
        {
            var treeA = new RecordingTree { Id = "tree_a" };
            treeA.AddOrReplaceRecording(Rec("a1", "tree_a"));
            treeA.AddOrReplaceRecording(Rec("a2", "tree_a"));
            var treeB = new RecordingTree { Id = "tree_b" };
            treeB.AddOrReplaceRecording(Rec("b1", "tree_b"));
            var trees = new List<RecordingTree> { treeA, treeB };
            var rp = Rp("rp", Slot(0, "b1"), Slot(1, "a1"));

            Assert.True(RewindInvoker.SlotFlightIsRouteSource(rp, rp.ChildSlots[1], trees,
                new HashSet<string> { "a2" }, out string tree, out string src));
            Assert.Equal("tree_a", tree);
            Assert.Equal("a2", src);
            Assert.False(RewindInvoker.SlotFlightIsRouteSource(rp, rp.ChildSlots[0], trees,
                new HashSet<string> { "a2" }, out _, out _));
            // No slot: every slot of the rewind point.
            Assert.True(RewindInvoker.SlotFlightIsRouteSource(rp, null, trees,
                new HashSet<string> { "a2" }, out tree, out _));
            Assert.Equal("tree_a", tree);
            Assert.False(RewindInvoker.SlotFlightIsRouteSource(rp, null, trees,
                new HashSet<string>(), out _, out _));
            Assert.False(RewindInvoker.SlotFlightIsRouteSource(rp, Slot(2, "missing"), trees,
                new HashSet<string> { "a2" }, out _, out _));
            Assert.False(RewindInvoker.SlotFlightIsRouteSource(null, null, trees,
                new HashSet<string> { "a2" }, out _, out _));
        }
    }
}
