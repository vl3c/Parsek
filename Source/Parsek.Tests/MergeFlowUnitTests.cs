using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization and unit tests for ParsekFlightMergeFlow: the headless-reachable
    /// instance paths (idle-on-pad, the auto-discard teardown), the helpers extracted from
    /// them and from the scene-exit commit (idle walk, re-home ids, snapshot release,
    /// dirty sidecar force-write) and the final segment phase / endpoint / finalization-cache statics.
    /// </summary>
    [Collection("Sequential")]
    public class MergeFlowUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeFlowUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static ParsekFlight Flight(RecordingTree tree)
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            FieldInfo field = typeof(ParsekFlight).GetField(
                "activeTree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(field);
            field.SetValue(flight, tree);
            // Field initializers do not run on an uninitialized object; the scene-change
            // transient reset clears these two sets.
            flight.dockingInProgress = new HashSet<uint>();
            flight.lastBranchVesselPids = new HashSet<uint>();
            return flight;
        }

        private static RecordingTree ActiveTree(RecordingTree tree, ParsekFlight flight)
        {
            FieldInfo field = typeof(ParsekFlight).GetField(
                "activeTree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return (RecordingTree)field.GetValue(flight);
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Tree One" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            if (recs.Length > 0)
            {
                tree.RootRecordingId = recs[0].RecordingId;
                tree.ActiveRecordingId = recs[0].RecordingId;
            }
            return tree;
        }

        private static TrajectoryPoint Pt(double ut, string body = "Kerbin", double alt = 0)
        {
            return new TrajectoryPoint { ut = ut, bodyName = body, altitude = alt };
        }

        private static TrackSection Section(ReferenceFrame frame, double start, double end,
            SegmentEnvironment env = SegmentEnvironment.Atmospheric,
            List<TrajectoryPoint> frames = null, List<TrajectoryPoint> bodyFixed = null)
        {
            return new TrackSection
            {
                referenceFrame = frame,
                startUT = start,
                endUT = end,
                environment = env,
                frames = frames,
                bodyFixedFrames = bodyFixed,
            };
        }

        // ================= characterization: instance paths =================

        [Fact]
        public void IdleOnPad_NoTreeOrNoRecordings_False()
        {
            Assert.False(Flight(null).MergeFlow.IsActiveTreeIdleOnPad());
            Assert.False(Flight(new RecordingTree { Id = "t" }).MergeFlow.IsActiveTreeIdleOnPad());
        }

        [Fact]
        public void IdleOnPad_NoPointsAnywhere_IsDataLossNotIdle()
        {
            var rec = new Recording { RecordingId = "r1", VesselName = "Pad", LaunchSiteName = "LaunchPad" };
            var flight = Flight(Tree(rec));
            Assert.False(flight.MergeFlow.IsActiveTreeIdleOnPad());
            Assert.Contains(logLines, l => l.Contains("IsActiveTreeIdleOnPad: all 1 recordings have 0 points"));
        }

        [Fact]
        public void IdleOnPad_RecordingFarFromLaunch_NotIdle()
        {
            var rec = new Recording
            {
                RecordingId = "r1", VesselName = "Flyer", LaunchSiteName = "LaunchPad",
                MaxDistanceFromLaunch = 5000,
            };
            rec.Points.Add(Pt(1));
            var flight = Flight(Tree(rec));
            Assert.False(flight.MergeFlow.IsActiveTreeIdleOnPad());
            Assert.Contains(logLines, l => l.Contains("IsActiveTreeIdleOnPad: 'Flyer' maxDist=") && l.Contains("not idle"));
        }

        [Fact]
        public void IdleOnPad_AllNearPadWithPadEvidence_Idle()
        {
            var rec = new Recording
            {
                RecordingId = "r1", VesselName = "Sitter", LaunchSiteName = "LaunchPad",
                MaxDistanceFromLaunch = 1,
            };
            rec.Points.Add(Pt(1));
            var flight = Flight(Tree(rec));
            Assert.True(flight.MergeFlow.IsActiveTreeIdleOnPad());
            Assert.Contains(logLines, l => l.Contains("IsActiveTreeIdleOnPad: all 1 recordings within 30m - idle on pad"));
        }

        [Fact]
        public void IdleOnPad_OneNullRecordingIsSkipped()
        {
            var rec = new Recording
            {
                RecordingId = "r1", VesselName = "Sitter", LaunchSiteName = "LaunchPad",
                MaxDistanceFromLaunch = 1,
            };
            rec.Points.Add(Pt(1));
            var tree = Tree(rec);
            tree.Recordings["null-slot"] = null;
            Assert.True(Flight(tree).MergeFlow.IsActiveTreeIdleOnPad());
        }

        [Fact]
        public void AutoDiscard_DropsTheActiveTreeAndLogsTheReason()
        {
            var rec = new Recording { RecordingId = "r1", VesselName = "Gone" };
            var flight = Flight(Tree(rec));
            flight.MergeFlow.AutoDiscardActiveTreeWithMessage(
                "unit-test", null, "unit-recalc", reapSidecars: false);
            Assert.Null(ActiveTree(null, flight));
            Assert.Contains(logLines, l => l.Contains("AutoDiscardActiveTreeCore: discarding live tree reason='unit-test'")
                && l.Contains("ledgerRecalcReason='unit-recalc'"));
        }

        // ================= extracted helpers =================

        [Fact]
        public void IdleWalk_StopsAtTheFirstNonIdleRecording()
        {
            var near = new Recording { RecordingId = "a", VesselName = "Near", MaxDistanceFromLaunch = 1 };
            near.Points.Add(Pt(1));
            var far = new Recording { RecordingId = "b", VesselName = "Far", MaxDistanceFromLaunch = 900 };
            var after = new Recording { RecordingId = "c", VesselName = "After", MaxDistanceFromLaunch = 900 };
            var tree = Tree(near, far, after);

            Assert.False(ParsekFlightMergeFlow.AllRecordingsIdleOnPadAfterBackfill(tree, out bool anyHasPoints));
            Assert.True(anyHasPoints);
            Assert.Contains(logLines, l => l.Contains("IsActiveTreeIdleOnPad: 'Far' maxDist=") && l.Contains("m - not idle"));
            Assert.DoesNotContain(logLines, l => l.Contains("'After'"));
        }

        [Fact]
        public void IdleWalk_AllIdle_ReportsWhetherAnyHasPoints()
        {
            var a = new Recording { RecordingId = "a", VesselName = "A", MaxDistanceFromLaunch = 2 };
            var b = new Recording { RecordingId = "b", VesselName = "B", MaxDistanceFromLaunch = 3 };
            var tree = Tree(a, b);
            tree.Recordings["null-slot"] = null;

            Assert.True(ParsekFlightMergeFlow.AllRecordingsIdleOnPadAfterBackfill(tree, out bool none));
            Assert.False(none);

            b.Points.Add(Pt(1));
            Assert.True(ParsekFlightMergeFlow.AllRecordingsIdleOnPadAfterBackfill(tree, out bool some));
            Assert.True(some);
        }

        [Fact]
        public void IdleWalk_NonIdleFirst_LeavesAnyHasPointsFromTheWalkedPrefixOnly()
        {
            var far = new Recording { RecordingId = "a", VesselName = "Far", MaxDistanceFromLaunch = 900 };
            var later = new Recording { RecordingId = "b", VesselName = "Later", MaxDistanceFromLaunch = 1 };
            later.Points.Add(Pt(1));
            Assert.False(ParsekFlightMergeFlow.AllRecordingsIdleOnPadAfterBackfill(Tree(far, later), out bool any));
            Assert.False(any);
        }

        [Fact]
        public void RehomeIds_EveryNonEmptyKey()
        {
            var tree = Tree(new Recording { RecordingId = "r1" }, new Recording { RecordingId = "r2" });
            tree.Recordings[""] = new Recording();
            var ids = ParsekFlightMergeFlow.CollectRehomeRecordingIds(tree);
            Assert.Equal(2, ids.Count);
            Assert.Contains("r1", ids);
            Assert.Contains("r2", ids);
            Assert.DoesNotContain("R1", ids);
            Assert.Empty(ParsekFlightMergeFlow.CollectRehomeRecordingIds(new RecordingTree { Id = "t" }));
        }

        [Theory]
        [InlineData(TerminalState.Landed, true)]
        [InlineData(TerminalState.Splashed, true)]
        [InlineData(TerminalState.Orbiting, true)]
        [InlineData(TerminalState.Destroyed, false)]
        [InlineData(TerminalState.SubOrbital, false)]
        [InlineData(TerminalState.Recovered, false)]
        [InlineData(TerminalState.Docked, false)]
        [InlineData(TerminalState.Boarded, false)]
        public void KeepSnapshotForSpawn_OnlyStableTerminals(TerminalState state, bool keep)
        {
            Assert.Equal(keep, ParsekFlightMergeFlow.ShouldKeepVesselSnapshotForSpawnAtEnd(
                new Recording { TerminalStateValue = state }));
        }

        [Fact]
        public void KeepSnapshotForSpawn_OpenRecording_False()
        {
            Assert.False(ParsekFlightMergeFlow.ShouldKeepVesselSnapshotForSpawnAtEnd(new Recording()));
        }

        [Fact]
        public void SceneExitSnapshotRelease_NullsNonSpawnableAndKeepsStableTerminals()
        {
            var landed = new Recording { RecordingId = "landed", VesselName = "L", TerminalStateValue = TerminalState.Landed };
            var orbiting = new Recording { RecordingId = "orbiting", VesselName = "O", TerminalStateValue = TerminalState.Orbiting };
            var splashed = new Recording { RecordingId = "splashed", VesselName = "S", TerminalStateValue = TerminalState.Splashed };
            var destroyed = new Recording { RecordingId = "destroyed", VesselName = "D", TerminalStateValue = TerminalState.Destroyed };
            var open = new Recording { RecordingId = "open", VesselName = "N" };
            var keepsGhost = new Recording { RecordingId = "ghost", VesselName = "G", TerminalStateValue = TerminalState.Recovered };
            var noSnapshot = new Recording { RecordingId = "none", VesselName = "X", TerminalStateValue = TerminalState.Destroyed };
            foreach (var r in new[] { landed, orbiting, splashed, destroyed, open, keepsGhost })
            {
                r.VesselSnapshot = new ConfigNode("VESSEL");
                r.VesselSnapshot.AddValue("name", r.VesselName);
            }
            var existingGhost = new ConfigNode("VESSEL");
            existingGhost.AddValue("name", "older ghost");
            keepsGhost.GhostVisualSnapshot = existingGhost;
            var landedSnapshot = landed.VesselSnapshot;

            ParsekFlightMergeFlow.ReleaseNonSpawnableSceneExitSnapshots(
                Tree(landed, orbiting, splashed, destroyed, open, keepsGhost, noSnapshot));

            Assert.Same(landedSnapshot, landed.VesselSnapshot);
            Assert.Null(landed.GhostVisualSnapshot);
            Assert.NotNull(orbiting.VesselSnapshot);
            Assert.NotNull(splashed.VesselSnapshot);
            Assert.Null(destroyed.VesselSnapshot);
            Assert.Equal("D", destroyed.GhostVisualSnapshot.GetValue("name"));
            Assert.Null(open.VesselSnapshot);
            Assert.Equal("N", open.GhostVisualSnapshot.GetValue("name"));
            Assert.Null(keepsGhost.VesselSnapshot);
            Assert.Same(existingGhost, keepsGhost.GhostVisualSnapshot);
            Assert.Null(noSnapshot.VesselSnapshot);
            Assert.Null(noSnapshot.GhostVisualSnapshot);
            Assert.Contains(logLines, l => l.Contains("UnreserveCrewInSnapshot: null snapshot"));
            Assert.Contains(logLines, l => l.Contains("UnreserveCrewInSnapshot: no crew roster"));
        }

        [Fact]
        public void ForceWriteDirtySidecars_SkipsCleanAndCountsOnlySuccessfulWrites()
        {
            var clean = new Recording { RecordingId = "clean", FilesDirty = false };
            var badId = new Recording { RecordingId = "bad/id", FilesDirty = true };
            Assert.Equal(0, ParsekFlightMergeFlow.ForceWriteDirtySidecarFiles(Tree(clean, badId)));
            Assert.True(badId.FilesDirty);
            Assert.Contains(logLines, l => l.Contains("[Paths]") && l.Contains("Recording id validation failed for 'bad/id'"));
            Assert.DoesNotContain(logLines, l => l.Contains("'clean'"));
        }

        // ================= statics =================

        [Fact]
        public void ShouldApplyFinalSegmentPhaseFromCapture_NeedsRecordingAndCapturedPhase()
        {
            var rec = new Recording { RecordingId = "r" };
            Assert.False(ParsekFlightMergeFlow.ShouldApplyFinalSegmentPhaseFromCapture(null, "r", new Recording { SegmentPhase = "exo" }));
            Assert.False(ParsekFlightMergeFlow.ShouldApplyFinalSegmentPhaseFromCapture(rec, "r", null));
            Assert.False(ParsekFlightMergeFlow.ShouldApplyFinalSegmentPhaseFromCapture(rec, "r", new Recording()));
        }

        [Theory]
        [InlineData(null, 0, false)]
        [InlineData("", 0, false)]
        [InlineData("c", -1, false)]
        [InlineData("c", 0, true)]
        [InlineData("c", 3, true)]
        public void CommittedChainSegmentOwnership(string chainId, int index, bool expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.HasCommittedChainSegmentOwnership(
                new Recording { ChainId = chainId, ChainIndex = index }));
        }

        [Fact]
        public void CommittedChainSegmentOwnership_Null_False()
        {
            Assert.False(ParsekFlightMergeFlow.HasCommittedChainSegmentOwnership(null));
        }

        [Theory]
        [InlineData(null, null, false)]
        [InlineData("rp", null, true)]
        [InlineData(null, "sess", true)]
        [InlineData("", "", false)]
        public void ExplicitReFlyProvisionalIdentity(string rpId, string sessionId, bool expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.HasExplicitReFlyProvisionalIdentity(
                new Recording { ProvisionalForRpId = rpId, CreatingSessionId = sessionId }));
            Assert.False(ParsekFlightMergeFlow.HasExplicitReFlyProvisionalIdentity(null));
        }

        [Fact]
        public void SetFinalSegmentPhase_AppliesChangeAndLogs()
        {
            var rec = new Recording { RecordingId = "r1", SegmentPhase = "atmo", SegmentBodyName = "Kerbin" };
            Assert.True(ParsekFlightMergeFlow.SetFinalSegmentPhase(rec, "exo", "Mun", "unit", "src-1"));
            Assert.Equal("exo", rec.SegmentPhase);
            Assert.Equal("Mun", rec.SegmentBodyName);
            Assert.Contains(logLines, l => l.Contains(
                "Final SegmentPhase applied from unit: rec=r1 old=Kerbin atmo new=Mun exo sourceRec=src-1"));
        }

        [Fact]
        public void SetFinalSegmentPhase_NoChangeOrNoPhase_False()
        {
            var rec = new Recording { RecordingId = "r1", SegmentPhase = "exo", SegmentBodyName = "Mun" };
            Assert.False(ParsekFlightMergeFlow.SetFinalSegmentPhase(rec, "exo", "Mun", "unit"));
            Assert.False(ParsekFlightMergeFlow.SetFinalSegmentPhase(rec, null, "Mun", "unit"));
            Assert.False(ParsekFlightMergeFlow.SetFinalSegmentPhase(null, "exo", "Mun", "unit"));
            Assert.DoesNotContain(logLines, l => l.Contains("Final SegmentPhase applied"));
        }

        [Fact]
        public void SetFinalSegmentPhase_PlaceholdersForMissingValues()
        {
            var rec = new Recording { RecordingId = null };
            Assert.True(ParsekFlightMergeFlow.SetFinalSegmentPhase(rec, "surface", null, "unit"));
            Assert.Contains(logLines, l => l.Contains("rec=<none> old=<none> new=surface sourceRec=<none>"));
        }

        [Theory]
        [InlineData(null, null, "<none>")]
        [InlineData("Mun", "", "<none>")]
        [InlineData(null, "exo", "exo")]
        [InlineData("Mun", "exo", "Mun exo")]
        public void FormatSegmentPhaseForLog(string body, string phase, string expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.FormatSegmentPhaseForLog(body, phase));
        }

        [Fact]
        public void ActiveRecorderCacheCandidate_ActiveIdOrRecordedPid()
        {
            var recorder = new FlightRecorder();
            var tree = new RecordingTree { ActiveRecordingId = "active" };
            Assert.False(ParsekFlightMergeFlow.IsActiveRecorderCacheCandidate(tree, null, recorder));
            Assert.False(ParsekFlightMergeFlow.IsActiveRecorderCacheCandidate(tree, new Recording { RecordingId = "active" }, null));
            Assert.True(ParsekFlightMergeFlow.IsActiveRecorderCacheCandidate(tree, new Recording { RecordingId = "active" }, recorder));
            Assert.False(ParsekFlightMergeFlow.IsActiveRecorderCacheCandidate(tree, new Recording { RecordingId = "other" }, recorder));
            Assert.False(ParsekFlightMergeFlow.IsActiveRecorderCacheCandidate(null, new Recording { RecordingId = "active" }, recorder));
        }

        [Theory]
        [InlineData("a", "a", 1u, 1u, true)]
        [InlineData("a", "b", 1u, 1u, false)]
        [InlineData(null, "b", 1u, 1u, true)]
        [InlineData("a", null, 1u, 1u, true)]
        [InlineData("a", "a", 1u, 2u, false)]
        [InlineData("a", "a", 0u, 2u, true)]
        [InlineData("a", "a", 1u, 0u, true)]
        public void CacheIdentityMatchesRecording(string recId, string cacheId, uint recPid, uint cachePid, bool expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.CacheIdentityMatchesRecording(
                new Recording { RecordingId = recId, VesselPersistentId = recPid },
                new RecordingFinalizationCache { RecordingId = cacheId, VesselPersistentId = cachePid }));
        }

        [Fact]
        public void CacheIdentityMatchesRecording_Nulls_False()
        {
            Assert.False(ParsekFlightMergeFlow.CacheIdentityMatchesRecording(null, new RecordingFinalizationCache()));
            Assert.False(ParsekFlightMergeFlow.CacheIdentityMatchesRecording(new Recording(), null));
        }

        [Theory]
        [InlineData(100.0, 100.0, true)]
        [InlineData(100.0000005, 100.0, true)]
        [InlineData(100.01, 100.0, false)]
        [InlineData(double.NaN, 100.0, false)]
        [InlineData(100.0, double.NaN, false)]
        [InlineData(double.PositiveInfinity, 100.0, false)]
        [InlineData(100.0, double.PositiveInfinity, false)]
        public void CacheTerminalAtOrBeforeCommit(double terminalUT, double commitUT, bool expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.IsCacheTerminalAtOrBeforeCommit(
                new RecordingFinalizationCache { TerminalUT = terminalUT }, commitUT));
        }

        [Fact]
        public void CacheTerminalAtOrBeforeCommit_NullCache_False()
        {
            Assert.False(ParsekFlightMergeFlow.IsCacheTerminalAtOrBeforeCommit(null, 1));
        }

        [Fact]
        public void LogDestroyedCacheRepairRejected_Format()
        {
            ParsekFlightMergeFlow.LogDestroyedCacheRepairRejected(
                new Recording { RecordingId = "r1", VesselName = "V" },
                new RecordingFinalizationCache { TerminalState = TerminalState.Destroyed, TerminalUT = 12.5 },
                20, TerminalState.Landed, "unit-reason");
            Assert.Contains(logLines, l => l.Contains("Finalization cache repair skipped:")
                && l.Contains("reason=unit-reason existingTerminal=Landed cacheTerminal=Destroyed terminalUT=12.500 commitUT=20.000"));
        }

        // --- final endpoint segment phase ---

        [Fact]
        public void FinalPhase_NullRecording()
        {
            Assert.False(ParsekFlightMergeFlow.TryResolveFinalEndpointSegmentPhase(
                null, false, null, out _, out _, out _, out string skip));
            Assert.Equal("recording-null", skip);
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.Docked)]
        public void FinalPhase_TerminalOrbitIsExo(TerminalState terminal)
        {
            var rec = new Recording { RecordingId = "r", TerminalStateValue = terminal, TerminalOrbitBody = "Mun" };
            Assert.True(ParsekFlightMergeFlow.TryResolveFinalEndpointSegmentPhase(
                rec, false, null, out string phase, out string body, out string source, out string skip));
            Assert.Equal("exo", phase);
            Assert.Equal("Mun", body);
            Assert.Equal("terminal-orbit", source);
            Assert.Null(skip);
        }

        [Fact]
        public void FinalPhase_TerminalSurfaceUsesTerminalPositionBody()
        {
            var rec = new Recording
            {
                RecordingId = "r", TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Minmus" },
            };
            Assert.True(ParsekFlightMergeFlow.TryResolveFinalEndpointSegmentPhase(
                rec, false, null, out string phase, out string body, out string source, out _));
            Assert.Equal("surface", phase);
            Assert.Equal("Minmus", body);
            Assert.Equal("terminal-surface", source);
        }

        [Fact]
        public void FinalPhase_TerminalSurfaceFallsBackToBestEndpointBodyUnlessRelative()
        {
            var rec = new Recording
            {
                RecordingId = "r", TerminalStateValue = TerminalState.Splashed, EndpointBodyName = "Eve",
            };
            Assert.True(ParsekFlightMergeFlow.TryResolveTerminalStateSegmentPhase(
                rec, out string phase, out string body, out string source));
            Assert.Equal("surface", phase);
            Assert.Equal("Eve", body);

            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 0, 10));
            Assert.False(ParsekFlightMergeFlow.TryResolveTerminalStateSegmentPhase(
                rec, out phase, out body, out source));
        }

        [Fact]
        public void FinalPhase_TrackSectionEnvironmentForAbsoluteFinalSection()
        {
            var rec = new Recording { RecordingId = "r", StartBodyName = "Kerbin" };
            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 0, 10, SegmentEnvironment.ExoBallistic));
            Assert.True(ParsekFlightMergeFlow.TryResolveFinalEndpointSegmentPhase(
                rec, false, null, out string phase, out string body, out string source, out _));
            Assert.Equal(SegmentPhaseClassifier.EnvironmentToPhase(SegmentEnvironment.ExoBallistic), phase);
            Assert.Equal("Kerbin", body);
            Assert.Equal("track-section-environment", source);
        }

        [Fact]
        public void FinalPhase_RelativeFinalSectionUsesShadowBodyOrSkips()
        {
            var rec = new Recording { RecordingId = "r" };
            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 0, 10, SegmentEnvironment.Atmospheric,
                bodyFixed: new List<TrajectoryPoint> { Pt(9, "Duna") }));
            Assert.True(ParsekFlightMergeFlow.TryResolveTrackSectionEnvironmentSegmentPhase(
                rec, out string phase, out string body, out string source, out string skip));
            Assert.Equal("Duna", body);
            Assert.Equal("relative-absolute-shadow-environment", source);
            Assert.Null(skip);

            var bare = new Recording { RecordingId = "b" };
            bare.TrackSections.Add(Section(ReferenceFrame.Relative, 0, 10));
            Assert.False(ParsekFlightMergeFlow.TryResolveTrackSectionEnvironmentSegmentPhase(
                bare, out _, out _, out _, out skip));
            Assert.Equal("relative-endpoint-unresolved-no-terminal-body-or-absolute-shadow", skip);
        }

        [Fact]
        public void ReliableRelativeEndpointBody_Order()
        {
            var section = Section(ReferenceFrame.Relative, 0, 10,
                bodyFixed: new List<TrajectoryPoint> { Pt(9, "Duna") });
            var rec = new Recording { TerminalOrbitBody = "Ike", TerminalPosition = new SurfacePosition { body = "Gilly" } };
            Assert.True(ParsekFlightMergeFlow.TryGetReliableRelativeEndpointBodyName(rec, section, out string body, out string source));
            Assert.Equal("Ike", body);
            Assert.Equal("terminal-orbit-body-environment", source);

            rec.TerminalOrbitBody = null;
            Assert.True(ParsekFlightMergeFlow.TryGetReliableRelativeEndpointBodyName(rec, section, out body, out source));
            Assert.Equal("Gilly", body);
            Assert.Equal("terminal-position-body-environment", source);

            Assert.True(ParsekFlightMergeFlow.TryGetReliableRelativeEndpointBodyName(null, section, out body, out source));
            Assert.Equal("Duna", body);

            Assert.False(ParsekFlightMergeFlow.TryGetReliableRelativeEndpointBodyName(
                null, Section(ReferenceFrame.Relative, 0, 10, bodyFixed: new List<TrajectoryPoint> { Pt(9, "") }),
                out _, out _));
        }

        [Fact]
        public void TerminalSurfaceBodyName_TerminalPositionThenSurfacePos()
        {
            Assert.False(ParsekFlightMergeFlow.TryGetTerminalSurfaceBodyName(null, out _));
            Assert.False(ParsekFlightMergeFlow.TryGetTerminalSurfaceBodyName(new Recording(), out _));
            var rec = new Recording { SurfacePos = new SurfacePosition { body = "Mun" } };
            Assert.True(ParsekFlightMergeFlow.TryGetTerminalSurfaceBodyName(rec, out string body));
            Assert.Equal("Mun", body);
            rec.TerminalPosition = new SurfacePosition { body = "Minmus" };
            Assert.True(ParsekFlightMergeFlow.TryGetTerminalSurfaceBodyName(rec, out body));
            Assert.Equal("Minmus", body);
            rec.TerminalPosition = new SurfacePosition { body = "" };
            Assert.True(ParsekFlightMergeFlow.TryGetTerminalSurfaceBodyName(rec, out body));
            Assert.Equal("Mun", body);
        }

        [Fact]
        public void FinalTrackSection_PrefersExplicitEndThenLastPointThenLast()
        {
            var rec = new Recording();
            Assert.False(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out _));
            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 0, 10, SegmentEnvironment.Atmospheric));
            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 10, 20, SegmentEnvironment.ExoBallistic));
            rec.TrackSections.Add(Section(ReferenceFrame.OrbitalCheckpoint, 20, 30, SegmentEnvironment.ExoPropulsive));

            rec.ExplicitEndUT = 5;
            Assert.True(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out TrackSection s));
            Assert.Equal(ReferenceFrame.Absolute, s.referenceFrame);

            rec.ExplicitEndUT = 99;
            rec.Points.Add(Pt(15));
            Assert.True(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out s));
            Assert.Equal(ReferenceFrame.Relative, s.referenceFrame);

            rec.Points.Clear();
            Assert.True(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out s));
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, s.referenceFrame);
            Assert.False(ParsekFlightMergeFlow.TryGetFinalTrackSection(null, out _));
        }

        [Fact]
        public void LastBodyFixedPrimaryPoint()
        {
            Assert.False(ParsekFlightMergeFlow.TryGetLastBodyFixedPrimaryPoint(Section(ReferenceFrame.Relative, 0, 1), out _));
            Assert.True(ParsekFlightMergeFlow.TryGetLastBodyFixedPrimaryPoint(
                Section(ReferenceFrame.Relative, 0, 1, bodyFixed: new List<TrajectoryPoint> { Pt(1), Pt(2) }), out var p));
            Assert.Equal(2, p.ut);
        }

        [Fact]
        public void FinalAbsoluteEndpointPoint_Sources()
        {
            Assert.False(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(null, out _, out _, out string skip));
            Assert.Equal("recording-null", skip);

            var rec = new Recording();
            Assert.False(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out _, out _, out skip));
            Assert.Equal("no-track-sections-or-points", skip);

            rec.Points.Add(Pt(7));
            Assert.True(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out var p, out string source, out _));
            Assert.Equal("legacy-flat-point", source);
            Assert.Equal(7, p.ut);

            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 0, 10, frames: new List<TrajectoryPoint> { Pt(3), Pt(4) }));
            Assert.True(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out p, out source, out _));
            Assert.Equal("absolute-section-point", source);
            Assert.Equal(4, p.ut);

            rec.TrackSections[0] = Section(ReferenceFrame.Absolute, 0, 10);
            Assert.True(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out p, out source, out _));
            Assert.Equal("absolute-flat-point", source);

            rec.Points.Clear();
            Assert.False(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out _, out _, out skip));
            Assert.Equal("absolute-section-has-no-points", skip);
        }

        [Fact]
        public void FinalAbsoluteEndpointPoint_RelativeAndCheckpointFrames()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 0, 10,
                bodyFixed: new List<TrajectoryPoint> { Pt(8) }));
            Assert.True(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out var p, out string source, out _));
            Assert.Equal("relative-absolute-shadow-point", source);
            Assert.Equal(8, p.ut);

            rec.TrackSections[0] = Section(ReferenceFrame.Relative, 0, 10);
            Assert.False(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out _, out _, out string skip));
            Assert.Equal("relative-endpoint-unresolved-no-absolute-shadow", skip);

            rec.TrackSections[0] = Section(ReferenceFrame.OrbitalCheckpoint, 0, 10);
            Assert.False(ParsekFlightMergeFlow.TryGetFinalAbsoluteEndpointPoint(rec, out _, out _, out skip));
            Assert.Equal("unsupported-reference-frame-OrbitalCheckpoint", skip);
        }

        [Fact]
        public void BestEndpointBodyName_PrecedenceChain()
        {
            Assert.Null(ParsekFlightMergeFlow.GetBestEndpointBodyName(null, true));
            var rec = new Recording();
            rec.Points.Add(Pt(1, "Flat"));
            Assert.Null(ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, false));
            Assert.Equal("Flat", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.StartBodyName = "Start";
            Assert.Equal("Start", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.SegmentBodyName = "Segment";
            Assert.Equal("Segment", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.SurfacePos = new SurfacePosition { body = "Surface" };
            Assert.Equal("Surface", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.TerminalPosition = new SurfacePosition { body = "TerminalPos" };
            Assert.Equal("TerminalPos", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.TerminalOrbitBody = "Orbit";
            Assert.Equal("Orbit", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
            rec.EndpointBodyName = "Endpoint";
            Assert.Equal("Endpoint", ParsekFlightMergeFlow.GetBestEndpointBodyName(rec, true));
        }

        // --- terminal inference body, restored-recording gates, orbit metadata ---

        [Fact]
        public void TerminalInferenceBody_UsesTestResolvers()
        {
            var savedRadius = ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting;
            var savedFloor = ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting;
            try
            {
                ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = name => name == "Kerbin" ? 600000.0 : (double?)null;
                ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = name => 70000.0;
                Assert.True(ParsekFlightMergeFlow.TryResolveTerminalInferenceBody("Kerbin", out double radius, out double floor));
                Assert.Equal(600000.0, radius);
                Assert.Equal(70000.0, floor);
                Assert.False(ParsekFlightMergeFlow.TryResolveTerminalInferenceBody("Nowhere", out radius, out floor));
                Assert.Equal(0.0, radius);
                Assert.False(ParsekFlightMergeFlow.TryResolveTerminalInferenceBody(null, out _, out _));

                ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = null;
                Assert.True(ParsekFlightMergeFlow.TryResolveTerminalInferenceBody("Kerbin", out radius, out floor));
                Assert.Equal(0.0, floor);
            }
            finally
            {
                ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = savedRadius;
                ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = savedFloor;
            }
        }

        [Fact]
        public void RestoredRecordingGates()
        {
            Assert.False(ParsekFlightMergeFlow.ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(null, out string reason));
            Assert.Null(reason);
            var rec = new Recording { RestoredFromCommittedTreeThisFrame = true };
            Assert.True(ParsekFlightMergeFlow.ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(rec, out reason));
            Assert.Contains("repaired from committed tree", reason);

            Assert.True(ParsekFlightMergeFlow.ShouldPreserveRestoredSceneExitTerminalState(rec, true, true, out reason));
            Assert.False(ParsekFlightMergeFlow.ShouldPreserveRestoredSceneExitTerminalState(rec, false, true, out reason));
            Assert.Null(reason);
            Assert.False(ParsekFlightMergeFlow.ShouldPreserveRestoredSceneExitTerminalState(rec, true, false, out reason));
            Assert.False(ParsekFlightMergeFlow.ShouldPreserveRestoredSceneExitTerminalState(new Recording(), true, true, out reason));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting, true)]
        [InlineData(TerminalState.SubOrbital, true)]
        [InlineData(TerminalState.Docked, true)]
        [InlineData(TerminalState.Landed, false)]
        [InlineData(TerminalState.Destroyed, false)]
        [InlineData(TerminalState.Recovered, false)]
        public void UsesTerminalOrbitMetadata(TerminalState state, bool expected)
        {
            Assert.Equal(expected, ParsekFlightMergeFlow.UsesTerminalOrbitMetadata(state));
        }

        [Fact]
        public void TerminalOrbitMetadataUpdate_DetectsAnyChangedElement()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin", TerminalOrbitInclination = 1, TerminalOrbitEccentricity = 0.1,
                TerminalOrbitSemiMajorAxis = 700000, TerminalOrbitLAN = 2, TerminalOrbitArgumentOfPeriapsis = 3,
                TerminalOrbitMeanAnomalyAtEpoch = 4, TerminalOrbitEpoch = 5,
            };
            var same = new ParsekFlight.TerminalOrbitMetadataSnapshot
            {
                body = "Kerbin", inclination = 1, eccentricity = 0.1, semiMajorAxis = 700000,
                lan = 2, argumentOfPeriapsis = 3, meanAnomalyAtEpoch = 4, epoch = 5,
            };
            Assert.False(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(same, rec));

            var changed = same;
            changed.epoch = 6;
            Assert.True(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(changed, rec));
            changed = same;
            changed.body = "Mun";
            Assert.True(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(changed, rec));
            Assert.True(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(
                default(ParsekFlight.TerminalOrbitMetadataSnapshot), rec));

            Assert.False(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(same, null));
            Assert.False(ParsekFlightMergeFlow.DidSceneExitUpdateTerminalOrbitMetadata(same, new Recording()));
        }
    }
}
