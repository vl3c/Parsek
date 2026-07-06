using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M-MIS-11 item 1 signature-gated loop-unit cache in
    /// <c>RouteOrchestrator.ResolveLoopUnit</c>: unchanged inputs reuse the
    /// cached unit with NO builder run and NO log; a builder-input change or a
    /// count-neutral tree-topology change rebuilds; and the cached unit is
    /// field-identical to what the unchanged one-element
    /// <c>MissionLoopUnitBuilder.Build</c> pipeline produces (the
    /// zero-observable-behavior-change contract).
    /// </summary>
    /// <remarks>
    /// Drives the LIVE resolve path (LoopUnitResolverForTesting null) against
    /// RecordingStore fixtures. FlightGlobalsBodyInfo.Instance degrades to
    /// no-phase-lock on the empty xUnit FlightGlobals (the documented DEL-1
    /// contract), and the section-less fixtures keep every body digest empty,
    /// so the build is fully headless. The rebuild-event Verbose line is the
    /// observable cache probe (ParsekLog.TestSinkForTesting).
    /// </remarks>
    [Collection("Sequential")]
    public class RouteLoopUnitCacheTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteLoopUnitCacheTests()
        {
            RecordingStore.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            // Clear any leaked log state from earlier tests in the Sequential
            // collection, then force verbose ON: the rebuild-line assertions
            // depend on Verbose being emitted regardless of a leaked
            // ParsekSettings.CurrentOverrideForTesting with verboseLogging=false.
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RecordingStore.ResetForTesting();
        }

        // --- fixtures (same launch -> dock -> undock topology as
        //     RouteBackingMissionLoopUnitTests) ---

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, string vessel = "V")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                SplitCause = type == BranchPointType.Undock ? "UNDOCK" : null,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        // Registers the tree AND its recordings in RecordingStore (the live
        // resolve path reads RecordingStore.CommittedRecordings/CommittedTrees).
        private static RecordingTree RegisterLaunchDockUndockTree(string treeId)
        {
            var recs = new[]
            {
                Leg("launch", "C0", 0, 1000, 2000, vessel: "Transport"),
                Leg("docked", "C0", 1, 2000, 3000, vessel: "Transport"),
                Leg("survivor", "C0", 2, 3000, 4000, vessel: "Transport"),
                Leg("payload", "C1", 0, 3000, 3500, vessel: "Payload")
            };
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            foreach (var r in recs)
            {
                tree.Recordings[r.RecordingId] = r;
                RecordingStore.AddCommittedInternal(r);
            }
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "launch" }, new[] { "docked" }, ut: 2000));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { "docked" }, new[] { "survivor", "payload" }, ut: 3000));
            RecordingStore.CommittedTrees.Add(tree);
            return tree;
        }

        private static Route BuildRoute(RecordingTree tree, string id)
        {
            Route route = new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Cache Route")
                .WithBackingMissionTreeId(tree.Id)
                .WithSchedule(2000.0, 2000.0)   // span, dispatchInterval (== span)
                .WithLoopAnchorUT(1000.0)
                .Build();
            foreach (string key in RouteBackingMission.ComputeExcludedIntervalKeys(
                         tree, segmentEndUT: 3000.0, launchUT: 1000.0))
                route.ExcludedIntervalKeys.Add(key);
            return route;
        }

        private int RebuildLogCount()
        {
            return logLines.Count(l => l.Contains("loop-unit cache rebuilt"));
        }

        // catches: the cache not being primed on the first live resolve, or a
        // steady-state second resolve re-running the builder (the rebuild line
        // must appear exactly once and the cached unit must be reused).
        [Fact]
        public void SteadyState_SecondResolve_ReusesCachedUnit_NoSecondRebuild()
        {
            RecordingTree tree = RegisterLaunchDockUndockTree("cache-tree-steady");
            Route route = BuildRoute(tree, "route-cache-steady");

            GhostPlaybackLogic.LoopUnit? first =
                RouteOrchestrator.ResolveLoopUnit(route, 100000.0);
            Assert.True(first.HasValue, "live resolve must yield a unit");
            Assert.Equal(1, RebuildLogCount());
            Assert.Contains(logLines, l =>
                l.Contains("loop-unit cache rebuilt") && l.Contains("reason=first-build"));
            Assert.NotNull(route.LoopUnitBuilderSignature);
            Assert.NotNull(route.LoopUnitTopologySignature);

            GhostPlaybackLogic.LoopUnit? second =
                RouteOrchestrator.ResolveLoopUnit(route, 100001.0);
            Assert.True(second.HasValue);
            Assert.Equal(1, RebuildLogCount()); // hit: no second rebuild, no log
            Assert.Equal(first.Value.SpanStartUT, second.Value.SpanStartUT);
            Assert.Equal(first.Value.SpanEndUT, second.Value.SpanEndUT);
            Assert.Equal(first.Value.CadenceSeconds, second.Value.CadenceSeconds);
            Assert.Equal(first.Value.PhaseAnchorUT, second.Value.PhaseAnchorUT);
            Assert.Equal(first.Value.OwnerIndex, second.Value.OwnerIndex);
        }

        // catches: the cached unit diverging from the unchanged builder
        // pipeline's output (the zero-observable-behavior-change contract) -
        // resolve through the cache, then run the SAME synthesized mission
        // through the locked one-element Build and compare unit fields.
        [Fact]
        public void CachedUnit_FieldIdentical_ToUnchangedOneElementBuild()
        {
            RecordingTree tree = RegisterLaunchDockUndockTree("cache-tree-parity");
            Route route = BuildRoute(tree, "route-cache-parity");

            GhostPlaybackLogic.LoopUnit? cached =
                RouteOrchestrator.ResolveLoopUnit(route, 100000.0);
            Assert.True(cached.HasValue);

            Mission mission = RouteBackingMission.BuildMission(route, 100000.0);
            double autoLoop = ParsekSettings.Current?.autoLoopIntervalSeconds
                              ?? LoopTiming.DefaultLoopIntervalSeconds;
            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { mission },
                RecordingStore.CommittedTrees,
                RecordingStore.CommittedRecordings,
                autoLoop,
                FlightGlobalsBodyInfo.Instance,
                ParsekSettings.Current?.TransitedBodyRotationMode
                    ?? TransitedBodyRotationMode.Loose);
            Assert.Equal(1, set.Count);
            foreach (var kvp in set.UnitsByOwner)
            {
                GhostPlaybackLogic.LoopUnit expected = kvp.Value;
                Assert.Equal(expected.OwnerIndex, cached.Value.OwnerIndex);
                Assert.Equal(expected.MemberIndices, cached.Value.MemberIndices);
                Assert.Equal(expected.SpanStartUT, cached.Value.SpanStartUT);
                Assert.Equal(expected.SpanEndUT, cached.Value.SpanEndUT);
                Assert.Equal(expected.CadenceSeconds, cached.Value.CadenceSeconds);
                Assert.Equal(expected.PhaseAnchorUT, cached.Value.PhaseAnchorUT);
                Assert.Equal(expected.OverlapCadenceSeconds, cached.Value.OverlapCadenceSeconds);
            }
        }

        // catches: a builder-input change (the route's dispatch interval feeds
        // Mission.LoopIntervalSeconds, folded into BuildSignature) NOT
        // invalidating the cache - the rebuilt unit must carry the new cadence.
        [Fact]
        public void BuilderInputChange_DispatchInterval_RebuildsWithNewCadence()
        {
            RecordingTree tree = RegisterLaunchDockUndockTree("cache-tree-input");
            Route route = BuildRoute(tree, "route-cache-input");

            GhostPlaybackLogic.LoopUnit? first =
                RouteOrchestrator.ResolveLoopUnit(route, 100000.0);
            Assert.True(first.HasValue);
            Assert.Equal(2000.0, first.Value.CadenceSeconds);
            Assert.Equal(1, RebuildLogCount());

            route.DispatchInterval = 4000.0; // player raised the cadence multiplier

            GhostPlaybackLogic.LoopUnit? second =
                RouteOrchestrator.ResolveLoopUnit(route, 100001.0);
            Assert.True(second.HasValue);
            Assert.Equal(4000.0, second.Value.CadenceSeconds); // rebuilt, not stale
            Assert.Equal(2, RebuildLogCount());
            Assert.Contains(logLines, l =>
                l.Contains("loop-unit cache rebuilt") && l.Contains("reason=builder-inputs-changed"));
        }

        // catches: a COUNT-NEUTRAL tree mutation (branch-point id swap - counts
        // unchanged, so the BuildSignature count folds do not move) slipping past
        // the cache. The M-MIS-9 topology hash must force the rebuild.
        [Fact]
        public void CountNeutralTopologyChange_BranchPointIdSwap_Rebuilds()
        {
            RecordingTree tree = RegisterLaunchDockUndockTree("cache-tree-topo");
            Route route = BuildRoute(tree, "route-cache-topo");

            Assert.True(RouteOrchestrator.ResolveLoopUnit(route, 100000.0).HasValue);
            Assert.Equal(1, RebuildLogCount());

            // Count-neutral mutation: same BranchPoints.Count / Recordings.Count,
            // same committed list, different branch-point identity.
            tree.BranchPoints[1].Id = "undock-bp-replayed";

            Assert.True(RouteOrchestrator.ResolveLoopUnit(route, 100001.0).HasValue);
            Assert.Equal(2, RebuildLogCount());
            Assert.Contains(logLines, l =>
                l.Contains("loop-unit cache rebuilt") && l.Contains("reason=tree-topology-changed"));
        }

        // catches: the test seam accidentally routing through (and priming) the
        // cache - seam resolves must leave the route's cache fields untouched.
        [Fact]
        public void TestSeam_BypassesCache_NoCacheFieldsWritten()
        {
            RecordingTree tree = RegisterLaunchDockUndockTree("cache-tree-seam");
            Route route = BuildRoute(tree, "route-cache-seam");
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: 0, memberIndices: new[] { 0 },
                    spanStartUT: 1000.0, spanEndUT: 3000.0,
                    cadenceSeconds: 2000.0, phaseAnchorUT: 1000.0);

            Assert.True(RouteOrchestrator.ResolveLoopUnit(route, 100000.0).HasValue);
            Assert.Null(route.LoopUnitBuilderSignature);
            Assert.Null(route.LoopUnitTopologySignature);
            Assert.Null(route.CachedLoopUnit);
            Assert.Equal(0, RebuildLogCount());
        }
    }
}
