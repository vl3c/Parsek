using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The committed-list optimization pass (<see cref="CommittedListOptimizer.RunOptimizationPass"/>)
    /// runs after every tree commit and on every load, and <see cref="RouteStore.RevalidateSources"/>
    /// compares each route's <see cref="RouteSourceRef"/>s (id, start / end UT, sidecar epoch,
    /// proof hash) against the committed recordings. A merge drops a source id
    /// (MissingSourceRecording), a split or a tail trim moves a source's EndUT (SourceChanged),
    /// and neither status recovers by itself, so a pass that rewrites a route's source would pause
    /// the route for good after the next load. Every cell builds the source refs the way
    /// <c>RouteBuilder.BuildRouteSourceRefs</c> does, runs the pass, revalidates as OnLoad does and
    /// reads the route's status.
    /// </summary>
    [Collection("Sequential")]
    public class RouteSourceOptimizerTests : IDisposable
    {
        private const string Mun = "Mun";

        private readonly List<string> logLines = new List<string>();

        public RouteSourceOptimizerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MissionStore.ResetForTesting();
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static void InstallScenario()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = null
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        private static RecordingTree CommitTree(string treeId, params Recording[] members)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = members[0].RecordingId,
            };
            for (int i = 0; i < members.Length; i++)
            {
                members[i].TreeId = treeId;
                members[i].TreeOrder = i;
                tree.AddOrReplaceRecording(members[i]);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            for (int i = 0; i < members.Length; i++)
                RecordingStore.AddRecordingWithTreeForTesting(members[i]);
            return tree;
        }

        /// <summary>
        /// One chain segment of an exo coast (same phase and body as its neighbours), so two
        /// consecutive ones are an optimizer merge candidate.
        /// </summary>
        private static Recording ChainSegment(string id, string chainId, int chainIndex,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Tanker",
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = Mun,
                MergeState = MergeState.Immutable,
                SidecarEpoch = 3,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = Mun });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = Mun });
            return rec;
        }

        /// <summary>
        /// A launch recording that climbs out of the atmosphere: an Atmospheric section then an
        /// ExoBallistic one, which the optimizer splits at the boundary.
        /// </summary>
        private static Recording AtmoToExoRecording(string id, double startUT, double midUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Tanker",
                MergeState = MergeState.Immutable,
                SidecarEpoch = 3,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 20000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = midUT - 1, altitude = 69000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = midUT, altitude = 70000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 90000, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = startUT,
                endUT = midUT,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = midUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }

        /// <summary>
        /// A rover that drives (SurfaceMobile) and then sits parked (SurfaceStationary) for ten
        /// minutes: one split class, so no split, and a boring tail the optimizer trims.
        /// </summary>
        private static Recording ParkedRoverRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Rover",
                MergeState = MergeState.Immutable,
                SidecarEpoch = 3,
            };
            const double start = 17000, mid = 17030, end = 17630;
            for (int i = 0; i < 5; i++)
                rec.Points.Add(new TrajectoryPoint { ut = start + (mid - start) * i / 4.0 });
            for (int i = 0; i < 60; i++)
                rec.Points.Add(new TrajectoryPoint { ut = mid + (end - mid) * (i + 1) / 60.0 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceMobile, startUT = start, endUT = mid });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = mid, endUT = end });
            return rec;
        }

        /// <summary>The ref <c>RouteBuilder.BuildRouteSourceRefs</c> captures for one member.</summary>
        private static RouteSourceRef CaptureSourceRef(Recording rec)
        {
            return new RouteSourceRef
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                TreeOrder = rec.TreeOrder,
                RecordingFormatVersion = rec.RecordingFormatVersion,
                RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                SidecarEpoch = RouteSourceRef.EffectiveSidecarEpoch(
                    rec.SidecarEpoch, rec.SidecarEpochAdvancePending),
                StartUT = rec.StartUT,
                EndUT = rec.EndUT,
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
            };
        }

        private static Route BuildRoute(string id, params Recording[] sources)
        {
            var builder = new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(RouteStatus.Active)
                .WithOrigin(new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    Latitude = -0.0972,
                    Longitude = -74.5577,
                    Altitude = 75.2,
                    IsSurface = true
                })
                .WithStop(new RouteStop
                {
                    Endpoint = new RouteEndpoint
                    {
                        BodyName = Mun,
                        Latitude = 3.2,
                        Longitude = -45.1,
                        Altitude = 612.5,
                        VesselPersistentId = 67890,
                        IsSurface = true
                    },
                    ConnectionKind = RouteConnectionKind.DockingPort,
                    SegmentIndexBefore = 0,
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
                });
            if (sources.Length > 0)
                builder.WithBackingMissionTreeId(sources[0].TreeId);
            for (int i = 0; i < sources.Length; i++)
            {
                builder.WithRecordingId(sources[i].RecordingId);
                builder.WithSourceRef(CaptureSourceRef(sources[i]));
            }
            return builder.Build();
        }

        private static Route AddRoute(string id, params Recording[] sources)
        {
            Route route = BuildRoute(id, sources);
            RouteStore.AddRoute(route);
            return route;
        }

        private static RouteStatus StatusOf(string routeId)
        {
            Assert.True(RouteStore.TryGetRoute(routeId, out Route route));
            return route.Status;
        }

        private static Recording Committed(string id)
            => RecordingStore.CommittedRecordings.FirstOrDefault(r => r.RecordingId == id);

        /// <summary>The load order: the pass, then the next load's revalidation.</summary>
        private static void RunPassThenRevalidate()
        {
            CommittedListOptimizer.RunOptimizationPass();
            EffectiveState.ResetCachesForTesting();
            RouteStore.RevalidateSources("OnLoad");
        }

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        // -----------------------------------------------------------------
        // A pass over a route's own source recordings
        // -----------------------------------------------------------------

        // catches: a merge of two route-source chain segments drops the absorbed id from the
        // committed list, so the next revalidation flips the route to MissingSourceRecording.
        [Fact]
        public void MergeCandidatePair_NamedByARoute_IsLeftWhole_RouteStaysActive()
        {
            InstallScenario();
            Recording head = ChainSegment("seg-head", "chain-tanker", 0, 17000, 17030);
            Recording tail = ChainSegment("seg-tail", "chain-tanker", 1, 17030, 17060);
            CommitTree("tree-tanker", head, tail);
            AddRoute("route-merge", head, tail);
            logLines.Clear();

            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-merge"));
            Assert.NotNull(Committed("seg-head"));
            Assert.NotNull(Committed("seg-tail"));
            Assert.Equal(17030.0, head.EndUT);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("route source")
                && l.Contains("merge")
                && l.Contains("seg-tail"));
        }

        // catches: the optimizer splitting a route source at an environment boundary moves the
        // source's EndUT (the head keeps the first half), so the route reads SourceChanged.
        [Fact]
        public void SplitCandidate_NamedByARoute_IsNotSplit_RouteStaysActive()
        {
            InstallScenario();
            Recording launch = AtmoToExoRecording("rec-launch", 17000, 17030, 17090);
            CommitTree("tree-launch", launch);
            AddRoute("route-split", launch);
            logLines.Clear();

            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-split"));
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal(17090.0, launch.EndUT);
            Assert.Equal(2, launch.TrackSections.Count);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("route source")
                && l.Contains("split")
                && l.Contains("rec-launch"));
        }

        // catches: a boring-tail trim of a route source moves its EndUT, so the route reads
        // SourceChanged after the next load.
        [Fact]
        public void TrimCandidate_NamedByARoute_KeepsItsTail_RouteStaysActive()
        {
            InstallScenario();
            Recording rover = ParkedRoverRecording("rec-rover");
            CommitTree("tree-rover", rover);
            AddRoute("route-trim", rover);
            logLines.Clear();

            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-trim"));
            Assert.Equal(17630.0, rover.EndUT);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("TrimBoringTail skipped")
                && l.Contains("route-source=1"));
        }

        // catches: a dormant route (created after a rewind cutoff, waiting to materialize) is
        // revalidated when it materializes, so its sources need the same protection.
        [Fact]
        public void DormantRouteSources_AreAlsoLeftWhole()
        {
            InstallScenario();
            Recording head = ChainSegment("seg-d-head", "chain-dormant", 0, 17000, 17030);
            Recording tail = ChainSegment("seg-d-tail", "chain-dormant", 1, 17030, 17060);
            CommitTree("tree-dormant", head, tail);
            Route dormant = BuildRoute("route-dormant", head, tail);
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { dormant });

            CommittedListOptimizer.RunOptimizationPass();

            Assert.NotNull(Committed("seg-d-head"));
            Assert.NotNull(Committed("seg-d-tail"));
            Assert.Equal(17030.0, head.EndUT);
        }

        // -----------------------------------------------------------------
        // The production sequence that makes a route source a merge candidate
        // -----------------------------------------------------------------

        // catches: a per-recording loop (a group Loop toggle sets LoopPlayback with no loop
        // range) blocks the commit-time merge; creating a route over the tree clears that loop
        // (RouteTreeGuard, "route looping wins"), so the NEXT pass - the next commit or load -
        // merges two of the route's sources and the route goes MissingSourceRecording for good.
        [Fact]
        public void RouteCreationClearsALoopThatBlockedAMerge_NextPassLeavesTheSourcesWhole()
        {
            InstallScenario();
            Recording head = ChainSegment("seg-loop-head", "chain-loop", 0, 17000, 17030);
            Recording tail = ChainSegment("seg-loop-tail", "chain-loop", 1, 17030, 17060);
            tail.LoopPlayback = true;
            CommitTree("tree-loop", head, tail);

            // The commit's own pass: the loop keeps the pair apart.
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // Route creation (RouteCreationService): add, then clear the tree's manual loops.
            Route route = AddRoute("route-loop", head, tail);
            int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(route, 17100.0);
            Assert.Equal(1, cleared);
            Assert.False(tail.LoopPlayback);

            // The next load: revalidate (sources still whole), reconcile, pass, and the load
            // after that revalidates what the pass left.
            RouteStore.RevalidateSources("OnLoad");
            Assert.Equal(RouteStatus.Active, StatusOf("route-loop"));
            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-loop"));
            Assert.NotNull(Committed("seg-loop-head"));
            Assert.NotNull(Committed("seg-loop-tail"));
        }

        // -----------------------------------------------------------------
        // Scope: the guard holds only what a route names
        // -----------------------------------------------------------------

        // catches: an over-wide guard (a whole tree, or every recording once any route exists)
        // that stops the optimizer for recordings no route sources from.
        [Fact]
        public void RecordingsNoRouteNames_AreStillMergedSplitAndTrimmed()
        {
            InstallScenario();
            Recording sourced = ChainSegment("seg-src-head", "chain-src", 0, 17000, 17030);
            Recording sourcedTail = ChainSegment("seg-src-tail", "chain-src", 1, 17030, 17060);
            CommitTree("tree-src", sourced, sourcedTail);
            AddRoute("route-scope", sourced, sourcedTail);

            Recording freeHead = ChainSegment("seg-free-head", "chain-free", 0, 18000, 18030);
            Recording freeTail = ChainSegment("seg-free-tail", "chain-free", 1, 18030, 18060);
            CommitTree("tree-free", freeHead, freeTail);
            Recording freeLaunch = AtmoToExoRecording("rec-free-launch", 19000, 19030, 19090);
            CommitTree("tree-free-launch", freeLaunch);
            Recording freeRover = ParkedRoverRecording("rec-free-rover");
            CommitTree("tree-free-rover", freeRover);

            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-scope"));
            Assert.NotNull(Committed("seg-src-tail"));
            Assert.Null(Committed("seg-free-tail"));
            Assert.Equal(18060.0, freeHead.EndUT);
            Assert.True(freeLaunch.EndUT < 19090.0,
                "the free launch was not split: endUT=" + F(freeLaunch.EndUT));
            Assert.True(freeRover.EndUT < 17630.0,
                "the free rover tail was not trimmed: endUT=" + F(freeRover.EndUT));
        }

        // -----------------------------------------------------------------
        // The ordinary order: a route built from an already optimized tree
        // -----------------------------------------------------------------

        // catches: a pass that is not a fixpoint over its own output (a re-split, a second
        // trim, a re-merge) would rewrite a route built after the commit-time pass on the very
        // next load.
        [Fact]
        public void RouteBuiltAfterTheCommitPass_SurvivesTheLoadPass()
        {
            InstallScenario();
            Recording launch = AtmoToExoRecording("rec-opt-launch", 17000, 17030, 17090);
            CommitTree("tree-opt-launch", launch);
            Recording rover = ParkedRoverRecording("rec-opt-rover");
            CommitTree("tree-opt-rover", rover);
            Recording head = ChainSegment("seg-opt-head", "chain-opt", 0, 20000, 20030);
            Recording tail = ChainSegment("seg-opt-tail", "chain-opt", 1, 20030, 20060);
            CommitTree("tree-opt-chain", head, tail);

            // The commit's pass splits the launch, trims the rover and merges the chain.
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(4, RecordingStore.CommittedRecordings.Count);
            Recording launchTail = RecordingStore.CommittedRecordings
                .Single(r => r.TreeId == "tree-opt-launch" && r.RecordingId != "rec-opt-launch");

            AddRoute("route-opt-launch", launch, launchTail);
            AddRoute("route-opt-rover", rover);
            AddRoute("route-opt-chain", head);

            RunPassThenRevalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-opt-launch"));
            Assert.Equal(RouteStatus.Active, StatusOf("route-opt-rover"));
            Assert.Equal(RouteStatus.Active, StatusOf("route-opt-chain"));
        }

        // -----------------------------------------------------------------
        // The pure pieces
        // -----------------------------------------------------------------

        [Fact]
        public void CollectSourceRecordingIds_UnionsCommittedAndDormant_SkipsBlankRefs()
        {
            Recording a = ChainSegment("rec-a", "c", 0, 0, 10);
            Recording b = ChainSegment("rec-b", "c", 1, 10, 20);
            Route committed = BuildRoute("route-c", a);
            committed.SourceRefs.Add(null);
            committed.SourceRefs.Add(new RouteSourceRef { RecordingId = "" });
            Route dormant = BuildRoute("route-d", b);
            Route noRefs = BuildRoute("route-none");
            noRefs.SourceRefs = null;
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { committed, noRefs }, new List<Route> { dormant });

            HashSet<string> ids = RouteStore.CollectSourceRecordingIds();

            Assert.Equal(new[] { "rec-a", "rec-b" }, ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void ChooseMergeCandidateIndex_SkipsPairsTouchingARouteSource_AndNamesThem()
        {
            var recs = new List<Recording>
            {
                ChainSegment("r0", "c1", 0, 0, 10),
                ChainSegment("r1", "c1", 1, 10, 20),
                ChainSegment("r2", "c2", 0, 30, 40),
                ChainSegment("r3", "c2", 1, 40, 50),
            };
            var candidates = new List<(int, int)> { (0, 1), (2, 3) };
            var sources = new HashSet<string>(StringComparer.Ordinal) { "r1" };
            HashSet<string> held = null;

            Assert.Equal(1, CommittedListOptimizer.ChooseMergeCandidateIndex(candidates, recs, sources, ref held));
            Assert.Equal(new[] { "r1" }, held.ToArray());

            sources.Add("r2");
            held = null;
            Assert.Equal(-1, CommittedListOptimizer.ChooseMergeCandidateIndex(candidates, recs, sources, ref held));
            Assert.Equal(new[] { "r1", "r2" }, held.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            held = null;
            Assert.Equal(0, CommittedListOptimizer.ChooseMergeCandidateIndex(
                candidates, recs, new HashSet<string>(), ref held));
            Assert.Null(held);
        }

        [Fact]
        public void ChooseSplitCandidateIndex_SkipsRouteSources_BesideTheReFlyDefer()
        {
            var recs = new List<Recording>
            {
                ChainSegment("s0", "c", 0, 0, 10),
                ChainSegment("s1", "c", 1, 10, 20),
                ChainSegment("s2", "c", 2, 20, 30),
            };
            var candidates = new List<(int, int)> { (0, 1), (1, 1), (2, 1) };
            var sources = new HashSet<string>(StringComparer.Ordinal) { "s0" };
            HashSet<string> held = null;

            int chosen = CommittedListOptimizer.ChooseSplitCandidateIndex(
                candidates, recs, "s1", sources, ref held, out int deferred);

            Assert.Equal(2, chosen);
            Assert.Equal(1, deferred);
            Assert.Equal(new[] { "s0" }, held.ToArray());

            held = null;
            Assert.Equal(1, CommittedListOptimizer.ChooseSplitCandidateIndex(
                candidates, recs, null, sources, ref held, out deferred));
            Assert.Equal(0, deferred);
        }
    }
}
