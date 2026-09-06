using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-SOURCECHANGED-AT-LOAD-AFTER-SIDECAR-EPOCH-DRIFT.
    ///
    /// <para>PR #1630 taught the codec predicates to SKIP a payload-free TrackSection
    /// instead of reading it as "this recording's payload is incomplete". That also
    /// unblocked the load-time malformed-flat-fallback heal for every recording already
    /// on disk carrying such an empty shell - and that heal called
    /// <c>MarkFilesDirty()</c>. The next <c>FlushDirtyFiles</c> rewrote the sidecar with
    /// <c>incrementEpoch: true</c>, so <see cref="Recording.SidecarEpoch"/> advanced on a
    /// plain LOAD. <see cref="RouteSourceRef.SidecarEpoch"/> is a route's
    /// proof-of-source field and is compared BEFORE the route-proof hash, so every
    /// committed route whose member recording healed parked in
    /// <see cref="RouteStatus.SourceChanged"/> - permanently, since design 7.4 forbids
    /// auto-recovery from SourceChanged - with no witnessed proof datum changed.</para>
    ///
    /// <para>The fix: the two READ paths heal in memory and pass
    /// <c>markDirty: false</c>. The repair PR #1630 wanted (no anchor-local metres in the
    /// flat POINT list that playback and the maxDist walk read) is untouched; only the
    /// file rewrite is gone, which is what the read path's own normalize-on-rewrite
    /// contract already promised ("files no flow dirties stay byte-identical").</para>
    ///
    /// <para>Fixture paths follow the CLAUDE.md rule: xUnit runs from
    /// <c>Source/Parsek.Tests/bin/Debug/net472/</c>, so five '..' segments reach the
    /// repo root.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteLoadTimeSidecarEpochTests : IDisposable
    {
        // The three committed route member recordings that flipped their routes on the
        // 2026-09-07 01:0x runs, with the sidecarEpoch each route captured as proof.
        // Both numbers are read from the committed bytes below, never assumed.
        public static IEnumerable<object[]> DriftingRouteMembers()
        {
            // fixture, member recording id, route id, route status in the fixture
            yield return new object[]
            {
                "depot-route-recorded", "0c8ec58d618246e38eafedc116a262c8",
                "5420f805fcbb453b8d5928b71393f14b", "Active"
            };
            yield return new object[]
            {
                "interbody-route-recorded", "3700f40e66c84ff79ce5197b362cf937",
                "71a983a16dc04d78bc2a2b90f1d184b0", "Active"
            };
            yield return new object[]
            {
                "interbody-route-recorded", "5737c255fba64ad6aa062b3fd7b0683d",
                "8f644e71b1164df3bb735330127d2ee7", "Paused"
            };
        }

        private readonly bool priorStoreSuppress;

        public RouteLoadTimeSidecarEpochTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // -----------------------------------------------------------------
        // Fixture access
        // -----------------------------------------------------------------

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        private static string FixtureSaveDir(string fixture)
        {
            return Path.Combine(RepoRoot(), "harness", "fixtures", "saves", fixture);
        }

        private static string PrecPath(string fixture, string recordingId)
        {
            return Path.Combine(FixtureSaveDir(fixture), "Parsek", "Recordings",
                recordingId + ".prec.txt");
        }

        /// <summary>
        /// Loads a committed sidecar through the PRODUCTION text read path - the same
        /// entry point <c>TrajectorySidecarBinary.Read</c> mirrors - so the heal under
        /// test is the shipped one, not a re-implementation.
        /// </summary>
        private static Recording ReadFixtureSidecar(string fixture, string recordingId)
        {
            string path = PrecPath(fixture, recordingId);
            Assert.True(File.Exists(path), "committed fixture sidecar must exist at " + path);

            ConfigNode node = ConfigNode.Load(path);
            Assert.NotNull(node);

            var rec = new Recording { RecordingId = recordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
            return rec;
        }

        /// <summary>Reads the `sidecarEpoch` the .prec file itself carries.</summary>
        private static int SidecarEpochOnDisk(string fixture, string recordingId)
        {
            ConfigNode node = ConfigNode.Load(PrecPath(fixture, recordingId));
            Assert.NotNull(node);
            Assert.True(int.TryParse(node.GetValue("sidecarEpoch"), out int epoch),
                "the committed .prec.txt must carry a sidecarEpoch");
            return epoch;
        }

        /// <summary>
        /// Reads the `sidecarEpoch` the ROUTE's SOURCE node captured for one member,
        /// straight out of the fixture's persistent.sfs. Deliberately a raw line scan:
        /// the point is to read the committed proof bytes, not to re-run the codec that
        /// produced them.
        /// </summary>
        private static int RouteSourceEpochInSave(string fixture, string recordingId)
        {
            string[] lines = File.ReadAllLines(Path.Combine(FixtureSaveDir(fixture), "persistent.sfs"));
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "recordingId = " + recordingId) continue;
                // A ROUTE SOURCE node is the one that carries sidecarEpoch + routeProofHash;
                // the RECORDING node with the same recordingId carries neither.
                for (int j = i + 1; j < Math.Min(i + 12, lines.Length); j++)
                {
                    string t = lines[j].Trim();
                    if (t.StartsWith("sidecarEpoch = ", StringComparison.Ordinal))
                        return int.Parse(t.Substring("sidecarEpoch = ".Length),
                            System.Globalization.CultureInfo.InvariantCulture);
                    if (t == "}") break;
                }
            }
            Assert.True(false, "no ROUTE SOURCE node for " + recordingId + " in " + fixture);
            return -1;
        }

        private static bool HasPayloadFreeSection(Recording rec)
        {
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                if (TrajectoryTextSidecarCodec.IsPayloadFreeTrackSection(rec.TrackSections[i]))
                    return true;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // The regression
        // -----------------------------------------------------------------

        // catches: a read path that dirties the file it just read. Before the fix all
        // three of these recordings came back FilesDirty, so OnLoad's FlushDirtyFiles
        // rewrote them with incrementEpoch: true and their routes parked SourceChanged.
        [Theory]
        [MemberData(nameof(DriftingRouteMembers))]
        public void FixtureRouteMember_ReadDoesNotDirtyTheSidecar(
            string fixture, string recordingId, string routeId, string routeStatus)
        {
            _ = routeId;
            _ = routeStatus;

            Recording rec = ReadFixtureSidecar(fixture, recordingId);

            // The shape PR #1630 widened the heal onto: one payload-free shell section.
            Assert.True(HasPayloadFreeSection(rec),
                "this fixture member is the regression subject because it carries a " +
                "payload-free TrackSection; if that stopped being true, re-pick the subject");

            Assert.False(rec.FilesDirty,
                "reading a committed sidecar must leave it byte-identical - a dirty flag " +
                "here becomes a SidecarEpoch bump on the next flush, which is drift the " +
                "route's proof-of-source comparison reads as SourceChanged");
        }

        // catches: a "fix" that keeps the epoch by disabling the heal. PR #1630's whole
        // point is that the flat POINT list must not carry the Relative section's
        // anchor-local METRES; those show up as out-of-range lat/lon.
        [Theory]
        [MemberData(nameof(DriftingRouteMembers))]
        public void FixtureRouteMember_StillHealsTheFlatListInMemory(
            string fixture, string recordingId, string routeId, string routeStatus)
        {
            _ = routeId;
            _ = routeStatus;

            Recording rec = ReadFixtureSidecar(fixture, recordingId);

            Assert.NotEmpty(rec.Points);
            Assert.All(rec.Points, p =>
            {
                Assert.InRange(p.latitude, -90.0, 90.0);
                Assert.InRange(p.longitude, -180.0, 180.0);
            });
        }

        // catches: a fixture re-harvest (or a stray rewrite) that leaves the route's
        // captured epoch and the sidecar's own epoch disagreeing AT REST. If these two
        // ever differ, the route is SourceChanged before any code runs and the cells
        // above would be pinning the wrong thing.
        [Theory]
        [MemberData(nameof(DriftingRouteMembers))]
        public void FixtureRouteMember_CapturedEpochAgreesWithTheSidecarAtRest(
            string fixture, string recordingId, string routeId, string routeStatus)
        {
            _ = routeId;
            _ = routeStatus;

            Assert.Equal(
                RouteSourceEpochInSave(fixture, recordingId),
                SidecarEpochOnDisk(fixture, recordingId));
        }

        // -----------------------------------------------------------------
        // Route-level consequence, end to end through RouteStore
        // -----------------------------------------------------------------

        /// <summary>
        /// Mirrors <c>RecordingSidecarStore.SaveRecordingFilesInternal</c>'s single
        /// epoch rule (<c>rec.SidecarEpoch + (incrementEpoch ? 1 : 0)</c>) for a dirty
        /// recording, so a cell can show what a load-time flush would do to the epoch
        /// without touching the disk.
        /// </summary>
        private static void FlushLikeOnLoad(Recording rec)
        {
            if (!rec.FilesDirty) return;
            rec.SidecarEpoch += 1;
            rec.FilesDirty = false;
        }

        private static Recording RegisterRouteSource(Recording rec, int sidecarEpoch)
        {
            rec.VesselName = rec.RecordingId;
            rec.MergeState = MergeState.Immutable;
            rec.TreeId = "tree-" + rec.RecordingId;
            rec.TreeOrder = 0;
            rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            rec.RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration;
            rec.SidecarEpoch = sidecarEpoch;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static RouteSourceRef MatchingSourceRef(Recording rec)
        {
            return new RouteSourceRef
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                TreeOrder = rec.TreeOrder,
                RecordingFormatVersion = rec.RecordingFormatVersion,
                RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                SidecarEpoch = rec.SidecarEpoch,
                StartUT = rec.StartUT,
                EndUT = rec.EndUT,
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
            };
        }

        private static Route BuildRoute(string id, RouteStatus status, RouteSourceRef sref)
        {
            return new Parsek.Tests.Generators.RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(status)
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
                        BodyName = "Mun",
                        Latitude = 3.2,
                        Longitude = -45.1,
                        Altitude = 612.5,
                        VesselPersistentId = 67890,
                        IsSurface = true
                    },
                    ConnectionKind = RouteConnectionKind.DockingPort,
                    SegmentIndexBefore = 0,
                    DeliveryOffsetSeconds = 0.0,
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
                })
                .WithRecordingId(sref.RecordingId)
                .WithSourceRef(sref)
                .Build();
        }

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

        // catches: the whole regression, end to end. Read a real committed route member,
        // run the load-time flush rule over it, then revalidate the route that names it.
        // Before the fix the read dirtied the recording, the flush bumped the epoch, and
        // RevalidateSources flipped Active -> SourceChanged on `sidecar-epoch-drift`.
        [Theory]
        [MemberData(nameof(DriftingRouteMembers))]
        public void FixtureRouteMember_SurvivesALoadCycleWithoutFlippingSourceChanged(
            string fixture, string recordingId, string routeId, string routeStatus)
        {
            RouteStatus captured = (RouteStatus)Enum.Parse(typeof(RouteStatus), routeStatus);

            // The route captured its proof when the sidecar carried THIS epoch.
            int capturedEpoch = RouteSourceEpochInSave(fixture, recordingId);
            Recording rec = RegisterRouteSource(
                ReadFixtureSidecar(fixture, recordingId), capturedEpoch);
            RouteSourceRef sref = MatchingSourceRef(rec);

            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();
            InstallScenario();
            RouteStore.AddRoute(BuildRoute(routeId, captured, sref));

            // Now do what OnLoad does after every recording has been read.
            FlushLikeOnLoad(rec);
            Assert.Equal(capturedEpoch, rec.SidecarEpoch);

            int transitioned = RouteStore.RevalidateSources("test-load-cycle");

            Assert.Equal(0, transitioned);
            Assert.True(RouteStore.TryGetRoute(routeId, out Route route));
            Assert.Equal(captured, route.Status);
        }

        // MIRROR DIRECTION: the guard must not have been softened into "epoch drift is
        // never a change". A member whose sidecar really was rewritten (any sanctioned
        // flow: optimizer merge, re-fly supersede, tail finalizer) still flips the route.
        [Fact]
        public void MemberSidecarGenuinelyRewritten_StillFlipsSourceChanged()
        {
            Recording rec = RegisterRouteSource(
                ReadFixtureSidecar("depot-route-recorded", "0c8ec58d618246e38eafedc116a262c8"),
                capturedEpochForTest);
            RouteSourceRef sref = MatchingSourceRef(rec);

            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-genuine-rewrite", RouteStatus.Active, sref));

            // A real mutation: something dirtied the recording for its OWN reasons, and
            // the flush advanced the epoch.
            rec.MarkFilesDirty();
            FlushLikeOnLoad(rec);
            Assert.Equal(capturedEpochForTest + 1, rec.SidecarEpoch);

            int transitioned = RouteStore.RevalidateSources("test-genuine-rewrite");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-genuine-rewrite", out Route route));
            Assert.Equal(RouteStatus.SourceChanged, route.Status);
        }

        private const int capturedEpochForTest = 4;

        // MIRROR DIRECTION on the seam itself: the heal still dirties when a WRITE-side
        // or repair flow calls it. Only the two read paths opt out.
        [Fact]
        public void HealCalledWithoutTheReadPathOptOut_StillMarksFilesDirty()
        {
            Recording rec = ReadFixtureSidecar(
                "depot-route-recorded", "0c8ec58d618246e38eafedc116a262c8");
            Assert.False(rec.FilesDirty);

            // Re-damage the flat list so the heal has something to do, then call it the
            // way a non-read flow does (default markDirty).
            rec.Points = new List<TrajectoryPoint>();
            Assert.True(
                TrajectoryTextSidecarCodec.TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                    rec, allowRelativeSections: true),
                "the heal must still rebuild a flat list it is asked to rebuild");
            Assert.True(rec.FilesDirty,
                "the default (write-side) call must still dirty the sidecar");
        }

        // -----------------------------------------------------------------
        // The hash pin
        // -----------------------------------------------------------------

        // catches: someone "fixing" this by folding the section list or the flat point
        // list into the route proof hash. Neither is witnessed transfer data, and the
        // dropped-section shape at the centre of PR #1630 must move nothing.
        [Fact]
        public void PayloadFreeSectionAndFlatHeal_DoNotMoveTheRouteProofHash()
        {
            var rec = new Recording
            {
                RecordingId = "hash-pin",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-hash-pin",
                        DockUT = 150.0,
                        UndockUT = 450.0,
                        TransferTargetVesselPid = 9999u,
                        TransferKind = RouteConnectionKind.DockingPort,
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            { "LiquidFuel", new ResourceAmount { amount = 1000.0, maxAmount = 1000.0 } }
                        }
                    }
                }
            };

            string baseline = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);
            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, baseline);

            // 1. Adding the exact shape PR #1630 stopped persisting.
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 200.0,
                endUT = 200.02,
                frames = new List<TrajectoryPoint>(),
            });
            Assert.True(TrajectoryTextSidecarCodec.IsPayloadFreeTrackSection(rec.TrackSections[0]));
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));

            // 2. Dropping it again.
            rec.TrackSections.Clear();
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));

            // 3. Rewriting the flat POINT list wholesale, which is what the heal does.
            rec.Points.Add(new TrajectoryPoint { ut = 210.0, latitude = 1.0, longitude = 2.0 });
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }

        // catches: the fixture members losing the property the whole diagnosis rests on -
        // their route proof is the sentinel, so `sidecar-epoch` was the ONLY field that
        // could have drifted (it is compared before the hash and after the UTs).
        [Theory]
        [MemberData(nameof(DriftingRouteMembers))]
        public void FixtureRouteMember_ProofHashIsStableAcrossTheRead(
            string fixture, string recordingId, string routeId, string routeStatus)
        {
            _ = routeId;
            _ = routeStatus;

            Recording rec = ReadFixtureSidecar(fixture, recordingId);
            // The .prec sidecar carries no route-proof data at all (that lives on the
            // RECORDING node in persistent.sfs), so the read can only ever produce the
            // sentinel - which is exactly what the fixture's ROUTE SOURCE node stored.
            Assert.Equal(
                RouteProofHasher.NoRouteProofSentinel,
                RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
        }
    }
}
