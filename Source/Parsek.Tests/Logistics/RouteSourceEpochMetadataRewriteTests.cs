using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-SOURCE-EPOCH-ON-METADATA-REWRITE. A route compares each source's sidecar epoch
    /// (<see cref="RouteStore.RevalidateSources"/>), and the epoch advances whenever a recording
    /// is marked <see cref="Recording.FilesDirty"/> and re-saved, whatever changed. These cells
    /// drive the sites that dirtied an already-committed route source without changing a byte
    /// of its sidecars: the re-commit of the source's tree
    /// (<see cref="TreeCommitService.CommitTree"/> marked every member dirty), the terminal-spawn
    /// supersede marks and the auto-group repair (both .sfs metadata only). Each cell commits,
    /// lets the commit's flush run against a temp save root (the flush is what sets the
    /// deferred epoch advance a route reads), revalidates as OnLoad does and reads the route's
    /// status. One cell pins the other direction: a real content change still reads as
    /// SourceChanged through the epoch.
    /// </summary>
    [Collection("Sequential")]
    public class RouteSourceEpochMetadataRewriteTests : IDisposable
    {
        private const string Mun = "Mun";

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RouteSourceEpochMetadataRewriteTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_rsemr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            ParsekLog.ResetTestOverrides();
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MissionStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
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

        /// <summary>A committed leg as it sits after its own commit's save: clean, epoch 3.</summary>
        private static Recording Leg(string id, double startUT, double endUT, uint pid = 4242)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Tanker",
                VesselPersistentId = pid,
                MergeState = MergeState.Immutable,
                SidecarEpoch = 3,
                PrecHash = "prec-" + id,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = Mun });
            rec.Points.Add(new TrajectoryPoint { ut = (startUT + endUT) / 2, altitude = 50000, bodyName = Mun });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = Mun });
            return rec;
        }

        private static RecordingTree CommitRunTree(string treeId, params Recording[] members)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = members[0].RecordingId,
                ActiveRecordingId = members[members.Length - 1].RecordingId,
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

        private static void AddRoute(string id, params Recording[] sources)
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
            RouteStore.AddRoute(builder.Build());
        }

        private static RouteStatus StatusOf(string routeId)
        {
            Assert.True(RouteStore.TryGetRoute(routeId, out Route route));
            return route.Status;
        }

        private static Recording Committed(string id)
            => RecordingStore.CommittedRecordings.FirstOrDefault(r => r.RecordingId == id);

        /// <summary>The next load's revalidation.</summary>
        private static void Revalidate()
        {
            EffectiveState.ResetCachesForTesting();
            RouteStore.RevalidateSources("OnLoad");
        }

        /// <summary>
        /// The resumed-vessel shape: a copy-on-write clone of the committed run tree with one
        /// new recording past its end (the transport flown again after the run), committed back.
        /// </summary>
        private static RecordingTree CloneWithContinuation(RecordingTree committed, string continuationId,
            double startUT, double endUT)
        {
            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording cont = Leg(continuationId, startUT, endUT);
            cont.TreeId = committed.Id;
            cont.TreeOrder = clone.Recordings.Count;
            cont.PrecHash = null;
            cont.SidecarEpoch = 0;
            cont.MarkFilesDirty();
            clone.AddOrReplaceRecording(cont);
            clone.ActiveRecordingId = continuationId;
            return clone;
        }

        // -----------------------------------------------------------------
        // The re-commit of a route's tree
        // -----------------------------------------------------------------

        // catches: CommitTree marking every member of a re-committed tree dirty. The flush then
        // rewrote the unchanged route sources with a deferred epoch advance, the route's
        // effective epoch read one higher and the next revalidation paused it as SourceChanged.
        [Fact]
        public void ReCommitOfRouteTree_WithAContinuation_LeavesSourcesClean_RouteStaysActive()
        {
            InstallScenario();
            Recording launch = Leg("src-launch", 17000, 17100);
            Recording dock = Leg("src-dock", 17100, 17400);
            RecordingTree committed = CommitRunTree("tree-run", launch, dock);
            AddRoute("route-run", launch, dock);
            logLines.Clear();

            TreeCommitService.CommitTree(CloneWithContinuation(committed, "after-run", 17400, 17900));

            Recording launchNow = Committed("src-launch");
            Recording dockNow = Committed("src-dock");
            Assert.NotNull(Committed("after-run"));
            Assert.False(launchNow.FilesDirty);
            Assert.False(dockNow.FilesDirty);
            Assert.False(launchNow.SidecarEpochAdvancePending);
            Assert.False(dockNow.SidecarEpochAdvancePending);
            Assert.Equal(3, launchNow.SidecarEpoch);
            Assert.Equal(3, dockNow.SidecarEpoch);
            // The new recording is written as before.
            Assert.True(Committed("after-run").SidecarEpoch > 0
                || Committed("after-run").SidecarEpochAdvancePending);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("route sources in tree 'tree-run'")
                && l.Contains("keptUnchanged=2"));

            Revalidate();

            Assert.Equal(RouteStatus.Active, StatusOf("route-run"));
        }

        // catches: the kept sources must also skip the session merge, so memory keeps exactly
        // the bytes their sidecars hold (they are never rewritten).
        [Fact]
        public void ReCommitOfRouteTree_KeptSource_KeepsItsCommittedTrajectoryLists()
        {
            InstallScenario();
            Recording dock = Leg("src-dock", 17100, 17400);
            RecordingTree committed = CommitRunTree("tree-run", dock);
            AddRoute("route-run", dock);
            RecordingTree clone = CloneWithContinuation(committed, "after-run", 17400, 17900);
            List<TrajectoryPoint> clonePoints = clone.Recordings["src-dock"].Points;
            List<TrackSection> cloneSections = clone.Recordings["src-dock"].TrackSections;

            TreeCommitService.CommitTree(clone);

            Assert.Same(clonePoints, Committed("src-dock").Points);
            Assert.Same(cloneSections, Committed("src-dock").TrackSections);
        }

        // catches: the fix swallowing a real change. A re-committed source whose trajectory was
        // edited in flight (dirty) is still rewritten, its epoch moves, and the route reads
        // SourceChanged through the epoch field even though its span and proof hash held.
        [Fact]
        public void ReCommitOfRouteTree_WithAChangedSource_StillFlipsSourceChanged()
        {
            InstallScenario();
            Recording launch = Leg("src-launch", 17000, 17100);
            Recording dock = Leg("src-dock", 17100, 17400);
            RecordingTree committed = CommitRunTree("tree-run", launch, dock);
            AddRoute("route-run", launch, dock);
            RecordingTree clone = CloneWithContinuation(committed, "after-run", 17400, 17900);
            Recording edited = clone.Recordings["src-dock"];
            edited.Points[1] = new TrajectoryPoint { ut = 17250, altitude = 61000, bodyName = Mun };
            edited.MarkFilesDirty();
            logLines.Clear();

            TreeCommitService.CommitTree(clone);
            Revalidate();

            Assert.Equal(RouteStatus.SourceChanged, StatusOf("route-run"));
            Assert.Equal(17400.0, Committed("src-dock").EndUT);
            Assert.True(Committed("src-dock").SidecarEpochAdvancePending);
            Assert.False(Committed("src-launch").SidecarEpochAdvancePending);
            Assert.Contains(logLines, l => l.Contains("keptUnchanged=1") && l.Contains("changed=1"));
            Assert.Contains(logLines, l => l.Contains("sidecar-epoch"));
        }

        // catches: the exemption leaking past route sources. A member no route names is still
        // forced dirty by the re-commit, exactly as before.
        [Fact]
        public void ReCommitOfRouteTree_NonSourceMember_IsStillRewritten()
        {
            InstallScenario();
            Recording dock = Leg("src-dock", 17100, 17400);
            Recording debris = Leg("debris", 17100, 17200, pid: 7);
            RecordingTree committed = CommitRunTree("tree-run", dock, debris);
            AddRoute("route-run", dock);

            TreeCommitService.CommitTree(CloneWithContinuation(committed, "after-run", 17400, 17900));

            Assert.False(Committed("src-dock").SidecarEpochAdvancePending);
            Assert.True(Committed("debris").SidecarEpochAdvancePending);
        }

        // -----------------------------------------------------------------
        // The pure predicates
        // -----------------------------------------------------------------

        [Fact]
        public void IsUnchangedCommittedRecording_Table()
        {
            Recording committed = Leg("r", 0, 10);
            Func<Recording> clone = () => Recording.DeepClone(committed);

            Assert.True(TreeCommitService.IsUnchangedCommittedRecording(committed, committed));
            Assert.True(TreeCommitService.IsUnchangedCommittedRecording(clone(), committed));

            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(null, committed));
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(clone(), null));

            Recording dirty = clone();
            dirty.MarkFilesDirty();
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(dirty, committed));

            Recording pending = clone();
            pending.SidecarEpochAdvancePending = true;
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(pending, committed));

            Recording resaved = clone();
            resaved.SidecarEpoch = 4;
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(resaved, committed));

            Recording otherBytes = clone();
            otherBytes.PrecHash = "prec-other";
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(otherBytes, committed));

            Recording otherSnapshot = clone();
            otherSnapshot.VesselSnapshotHash = "vessel-other";
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(otherSnapshot, committed));

            Recording extended = clone();
            extended.Points.Add(new TrajectoryPoint { ut = 20, bodyName = Mun });
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(extended, committed));

            // The committed copy itself owing a write: the clone may not hold what disk holds.
            Recording dirtyCommitted = Leg("r2", 0, 10);
            Recording cloneOfDirty = Recording.DeepClone(dirtyCommitted);
            dirtyCommitted.FilesDirty = true;
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(cloneOfDirty, dirtyCommitted));
            dirtyCommitted.FilesDirty = false;
            dirtyCommitted.SidecarEpochAdvancePending = true;
            Assert.False(TreeCommitService.IsUnchangedCommittedRecording(cloneOfDirty, dirtyCommitted));
        }

        [Fact]
        public void CollectUnchangedRouteSourceIds_OnlyCommittedUnchangedRouteSources()
        {
            Recording kept = Leg("kept", 0, 10);
            Recording notNamed = Leg("not-named", 0, 10);
            Recording changed = Leg("changed", 0, 10);
            RecordingTree committed = CommitRunTree("tree", kept, notNamed, changed);
            RecordingTree clone = RecordingTree.DeepClone(committed);
            clone.Recordings["changed"].MarkFilesDirty();
            Recording fresh = Leg("fresh", 10, 20);
            fresh.TreeId = "tree";
            clone.AddOrReplaceRecording(fresh);

            HashSet<string> ids = TreeCommitService.CollectUnchangedRouteSourceIds(
                clone, new HashSet<string> { "kept", "changed", "fresh" });

            Assert.Equal(new[] { "kept" }, ids.ToArray());
            Assert.Contains(logLines, l => l.Contains("examined=3") && l.Contains("keptUnchanged=1")
                && l.Contains("changed=1") && l.Contains("notCommitted=1"));
            Assert.Empty(TreeCommitService.CollectUnchangedRouteSourceIds(clone, new HashSet<string>()));
            Assert.Empty(TreeCommitService.CollectUnchangedRouteSourceIds(null, new HashSet<string> { "kept" }));
        }

        // -----------------------------------------------------------------
        // .sfs-only marks on a committed route source
        // -----------------------------------------------------------------

        // catches: the continuation supersede mark (an .sfs field) setting FilesDirty, which
        // the next save turned into a sidecar rewrite and an epoch advance on the route source.
        [Fact]
        public void ContinuationSupersedeMark_OnRouteSource_DoesNotDirtyIt_RouteStaysActive()
        {
            InstallScenario();
            Recording dock = Leg("src-dock", 17100, 17400, pid: 555);
            dock.VesselSpawned = true;
            dock.SpawnedVesselPersistentId = 555;
            CommitRunTree("tree-run", dock);
            AddRoute("route-run", dock);

            Recording continued = Leg("continued", 17400, 17900, pid: 555);
            continued.TreeId = "tree-next";
            continued.TreeOrder = 0;
            var next = new RecordingTree { Id = "tree-next", TreeName = "next", RootRecordingId = "continued" };
            next.AddOrReplaceRecording(continued);

            int marked = TreeCommitService.MarkSupersededTerminalSpawnsForContinuedSources(next);
            RecordingStore.FlushDirtyCommittedFiles();
            Revalidate();

            Assert.Equal(1, marked);
            Assert.Equal("continued", dock.TerminalSpawnSupersededByRecordingId);
            Assert.False(dock.FilesDirty);
            Assert.False(dock.SidecarEpochAdvancePending);
            Assert.Equal(RouteStatus.Active, StatusOf("route-run"));
        }

        // catches: the dock-merge supersede mark (.sfs fields only) setting FilesDirty.
        [Fact]
        public void DockMergeSupersedeMark_OnRouteSource_DoesNotDirtyIt_RouteStaysActive()
        {
            InstallScenario();
            Recording dock = Leg("src-dock", 17100, 17400, pid: 555);
            dock.VesselSpawned = true;
            dock.SpawnedVesselPersistentId = 90001;
            CommitRunTree("tree-run", dock);
            AddRoute("route-run", dock);

            int marked = TreeCommitService.MarkTerminalSpawnSupersededByDockMerge(
                90001, null, 777, "merged-continuation");
            RecordingStore.FlushDirtyCommittedFiles();
            Revalidate();

            Assert.Equal(1, marked);
            Assert.Equal("merged-continuation", dock.TerminalSpawnSupersededByRecordingId);
            Assert.Equal(0u, dock.SpawnedVesselPersistentId);
            Assert.False(dock.FilesDirty);
            Assert.False(dock.SidecarEpochAdvancePending);
            Assert.Equal(RouteStatus.Active, StatusOf("route-run"));
        }

        // catches: the auto-group membership repair (group names live in the .sfs) dirtying
        // the recording it repaired.
        [Fact]
        public void AutoGroupRepair_RepairsMembershipWithoutDirtying()
        {
            Recording rec = Leg("src-dock", 17100, 17400);
            rec.TreeId = "tree-run";
            rec.RecordingGroups = new List<string> { "Stale Group" };
            var tree = new RecordingTree
            {
                Id = "tree-run",
                TreeName = "Tanker",
                RootRecordingId = "src-dock",
                AutoGeneratedRootGroupName = "Tanker"
            };
            tree.AddOrReplaceRecording(rec);

            int changes = RecordingGroupStore.RepairAutoGeneratedTreeGroups(tree);

            Assert.True(changes > 0);
            Assert.Contains("Tanker", rec.RecordingGroups);
            Assert.False(rec.FilesDirty);
        }
    }
}
