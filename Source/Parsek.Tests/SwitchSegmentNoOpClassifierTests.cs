using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-predicate tests for <see cref="SwitchSegmentNoOpClassifier.IsNoOpSegment"/>:
    /// which resumed (Fly / Switch-To) segments changed nothing meaningful and can
    /// be auto-discarded vs which must be kept. Covers the locked "did something"
    /// definition (trajectory / geometry / dock + crew transfer) and the edge-case
    /// table in docs/dev/plans/autodiscard-noop-switch-segment.md.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentNoOpClassifierTests : IDisposable
    {
        public SwitchSegmentNoOpClassifierTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrackSection Section(
            SegmentEnvironment env, ReferenceFrame frame = ReferenceFrame.Absolute,
            double startUT = 0, double endUT = 100,
            List<OrbitSegment> checkpoints = null)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = frame,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = checkpoints ?? new List<OrbitSegment>(),
            };
        }

        private static OrbitSegment Orbit(
            double sma, double ecc = 0, double inc = 0, double lan = 0, double aop = 0,
            string body = "Kerbin", double startUT = 0, double endUT = 100)
        {
            return new OrbitSegment
            {
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = inc,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = aop,
                bodyName = body,
                startUT = startUT,
                endUT = endUT,
            };
        }

        // ---- no-op (discard) cases ------------------------------------------

        [Fact]
        public void OrbitalCoast_WithInertEngineSeed_IsNoOp()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            // Zero-throttle resume seed — inert, must not block discard.
            rec.PartEvents.Add(new PartEvent
            {
                ut = 0, eventType = PartEventType.EngineShutdown, value = 0f
            });

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void OrbitalCoast_WithTimeJumpOnly_IsNoOp()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.SegmentEvents.Add(new SegmentEvent { ut = 50, type = SegmentEventType.TimeJump });

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Fact]
        public void LandedSitStill_IsNoOp()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.SurfaceStationary));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Fact]
        public void EmptySegment_WithNoPayload_IsKept_InsufficientData()
        {
            // Data-loss safeguard: a segment with no points / sections / orbit
            // segments cannot be confirmed empty (a failed sidecar looks the
            // same), so it is KEPT rather than discarded.
            var rec = new Recording();
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("insufficient-data", reason);
        }

        [Fact]
        public void OrbitalCoast_TwoPointsNoSections_StillEvaluatedAsNoOp()
        {
            // >= 2 points means there IS trajectory payload, so the data-loss
            // guard does not fire and an unremarkable coast is still a no-op.
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 0, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 100, altitude = 80000, bodyName = "Kerbin" });
            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Fact]
        public void CosmeticLightOnly_IsNoOp()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.PartEvents.Add(new PartEvent { ut = 10, eventType = PartEventType.LightOn });

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Fact]
        public void OrbitUnchanged_TwoIdenticalSegments_IsNoOp()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(sma: 700000, ecc: 0.01, inc: 5));
            rec.OrbitSegments.Add(Orbit(sma: 700000, ecc: 0.01, inc: 5, startUT: 100, endUT: 200));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        // ---- keep cases ------------------------------------------------------

        [Fact]
        public void NullSegment_IsKept()
        {
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(null, false, out string reason));
            Assert.Equal("segment-null", reason);
        }

        [Fact]
        public void VesselDestroyed_IsKept()
        {
            var rec = new Recording { VesselDestroyed = true };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("vessel-destroyed", reason);
        }

        [Fact]
        public void TerminalDestroyed_WithoutBool_IsKept()
        {
            // A destruction path may set TerminalStateValue=Destroyed without the
            // VesselDestroyed bool (ApplyDestroyedFallback); a Destroyed terminal
            // is still an unambiguous world-state change -> keep.
            var rec = new Recording
            {
                VesselDestroyed = false,
                TerminalStateValue = TerminalState.Destroyed,
            };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("terminal-destroyed", reason);
        }

        [Fact]
        public void HasDescendants_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, true, out string reason));
            Assert.Equal("has-descendants", reason);
        }

        [Theory]
        [InlineData(SegmentEnvironment.Atmospheric)]
        [InlineData(SegmentEnvironment.ExoPropulsive)]
        [InlineData(SegmentEnvironment.SurfaceMobile)]
        [InlineData(SegmentEnvironment.Approach)]
        public void NonBoringSection_IsKept(SegmentEnvironment env)
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.TrackSections.Add(Section(env, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.StartsWith("non-boring-section:", reason);
        }

        [Fact]
        public void PositiveThrottleEngine_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.PartEvents.Add(new PartEvent
            {
                ut = 10, eventType = PartEventType.EngineThrottle, value = 0.5f
            });

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.StartsWith("part-event:", reason);
        }

        [Fact]
        public void PositiveRcsThrottle_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.PartEvents.Add(new PartEvent
            {
                ut = 10, eventType = PartEventType.RCSThrottle, value = 0.3f
            });

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out _));
        }

        [Theory]
        [InlineData(PartEventType.Decoupled)]
        [InlineData(PartEventType.Docked)]
        [InlineData(PartEventType.Undocked)]
        [InlineData(PartEventType.ParachuteDeployed)]
        [InlineData(PartEventType.DeployableExtended)]
        [InlineData(PartEventType.GearDeployed)]
        public void StructuralPartEvent_IsKept(PartEventType type)
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.PartEvents.Add(new PartEvent { ut = 10, eventType = type });

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.StartsWith("part-event:", reason);
        }

        [Fact]
        public void DockTarget_IsKept()
        {
            var rec = new Recording { DockTargetVesselPid = 12345u };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("dock-target", reason);
        }

        [Theory]
        [InlineData(SegmentEventType.CrewTransfer)]
        [InlineData(SegmentEventType.CrewLost)]
        [InlineData(SegmentEventType.ControllerChange)]
        [InlineData(SegmentEventType.PartDestroyed)]
        [InlineData(SegmentEventType.PartAdded)]
        [InlineData(SegmentEventType.PartRemoved)]
        public void MeaningfulSegmentEvent_IsKept(SegmentEventType type)
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic));
            rec.SegmentEvents.Add(new SegmentEvent { ut = 10, type = type });

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.StartsWith("segment-event:", reason);
        }

        [Fact]
        public void FlagPlant_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.SurfaceStationary));
            rec.FlagEvents = new List<FlagEvent> { new FlagEvent() };

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("flag-event", reason);
        }

        [Fact]
        public void OrbitSmaChanged_IsKept()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(sma: 700000));
            rec.OrbitSegments.Add(Orbit(sma: 750000, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("orbit-sma-change", reason);
        }

        [Fact]
        public void OrbitBodyChanged_IsKept()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(sma: 700000, body: "Kerbin"));
            rec.OrbitSegments.Add(Orbit(sma: 700000, body: "Mun", startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("orbit-body-change", reason);
        }

        [Fact]
        public void OrbitInclinationChanged_IsKept()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(sma: 700000, inc: 5.0));
            rec.OrbitSegments.Add(Orbit(sma: 700000, inc: 25.0, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("orbit-inc-change", reason);
        }

        [Fact]
        public void OrbitalCheckpointSectionChange_IsKept()
        {
            // Sparse-section case: no per-frame sections, only OrbitalCheckpoint
            // sections carrying the (changed) Kepler elements.
            var rec = new Recording();
            rec.TrackSections.Add(Section(
                SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                0, 100, new List<OrbitSegment> { Orbit(sma: 700000) }));
            rec.TrackSections.Add(Section(
                SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                100, 200, new List<OrbitSegment> { Orbit(sma: 760000, startUT: 100, endUT: 200) }));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpSegment(rec, false, out string reason));
            Assert.Equal("orbit-sma-change", reason);
        }

        // ---- IsNoOpResumeTail (no-session committed-restore resume) ----------
        // The committed history before sinceUT must be ignored; only the resume
        // tail (>= sinceUT) decides.

        [Fact]
        public void ResumeTail_BoringCoast_IsNoOp()
        {
            var rec = new Recording();
            // Committed history section (ignored: endUT == sinceUT, not > it).
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 0, endUT: 100));
            // Resume tail: coasting.
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));
            // A meaningful event in the committed history (before the resume) is ignored.
            rec.PartEvents.Add(new PartEvent { ut = 50, eventType = PartEventType.Decoupled });

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out _));
        }

        [Fact]
        public void ResumeTail_MeaningfulPartEventAfterAnchor_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));
            rec.PartEvents.Add(new PartEvent { ut = 150, eventType = PartEventType.Decoupled });

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out string reason));
            Assert.StartsWith("part-event:", reason);
        }

        [Fact]
        public void ResumeTail_NonBoringSectionAfterAnchor_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoPropulsive, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out string reason));
            Assert.StartsWith("non-boring-section:", reason);
        }

        [Fact]
        public void ResumeTail_NonBoringSectionBeforeAnchor_Ignored_IsNoOp()
        {
            var rec = new Recording();
            // A burn that happened BEFORE the resume (committed history) is ignored.
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoPropulsive, startUT: 0, endUT: 90));
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out _));
        }

        [Fact]
        public void ResumeTail_TerminalDestroyedInWindow_IsKept()
        {
            // Destruction WITHIN the resume window (recording ends >= anchor) is a
            // real change — keep.
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 200,
            };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out string reason));
            Assert.Equal("terminal-destroyed", reason);
        }

        [Fact]
        public void ResumeTail_TerminalDestroyedBeforeAnchor_IsNoOp()
        {
            // REGRESSION (scene-exit auto-discard stopped firing): the no-session
            // tail check runs over EVERY recording in the restored clone, including
            // old committed debris destroyed in its ORIGINAL flight (a spent
            // booster). That destruction ends far before the resume anchor, so it
            // must NOT block a do-nothing resume's auto-discard.
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 0,
                ExplicitEndUT = 90, // committed history, before the anchor (100)
            };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 0, endUT: 90));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out _));
        }

        [Fact]
        public void ResumeTail_VesselDestroyedBoolBeforeAnchor_IsNoOp()
        {
            // Same regression via the VesselDestroyed bool (the other destruction
            // surface) on pre-window committed debris.
            var rec = new Recording
            {
                VesselDestroyed = true,
                ExplicitStartUT = 0,
                ExplicitEndUT = 90,
            };
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 0, endUT: 90));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out _));
        }

        [Fact]
        public void ResumeTail_NaNAnchor_IsKept()
        {
            var rec = new Recording();
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, double.NaN, out string reason));
            Assert.Equal("no-resume-anchor", reason);
        }

        [Fact]
        public void ResumeTail_OrbitChangedAfterAnchor_IsKept()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(sma: 700000, startUT: 90, endUT: 150));
            rec.OrbitSegments.Add(Orbit(sma: 800000, startUT: 150, endUT: 200));

            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out string reason));
            Assert.Equal("orbit-sma-change", reason);
        }

        [Fact]
        public void ResumeTail_OrbitUnchangedAfterAnchor_IsNoOp()
        {
            var rec = new Recording();
            // Pre-resume orbit differs, but it is ignored (endUT <= anchor).
            rec.OrbitSegments.Add(Orbit(sma: 500000, startUT: 0, endUT: 100));
            rec.OrbitSegments.Add(Orbit(sma: 700000, startUT: 100, endUT: 150));
            rec.OrbitSegments.Add(Orbit(sma: 700000, startUT: 150, endUT: 200));

            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, 100, out _));
        }

        [Fact]
        public void ResumeTail_FlagAfterAnchor_IsKept_BeforeIgnored()
        {
            var keep = new Recording();
            keep.TrackSections.Add(Section(SegmentEnvironment.SurfaceStationary, startUT: 100, endUT: 200));
            keep.FlagEvents = new List<FlagEvent> { new FlagEvent { ut = 150 } };
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(keep, 100, out string reason));
            Assert.Equal("flag-event", reason);

            var noop = new Recording();
            noop.TrackSections.Add(Section(SegmentEnvironment.SurfaceStationary, startUT: 100, endUT: 200));
            noop.FlagEvents = new List<FlagEvent> { new FlagEvent { ut = 50 } }; // pre-resume, ignored
            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(noop, 100, out _));
        }

        [Fact]
        public void ResumeTail_CrewTransferAfterAnchor_IsKept_TimeJumpIgnored()
        {
            var keep = new Recording();
            keep.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));
            keep.SegmentEvents.Add(new SegmentEvent { ut = 150, type = SegmentEventType.CrewTransfer });
            Assert.False(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(keep, 100, out string reason));
            Assert.StartsWith("segment-event:", reason);

            var noop = new Recording();
            noop.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, startUT: 100, endUT: 200));
            noop.SegmentEvents.Add(new SegmentEvent { ut = 150, type = SegmentEventType.TimeJump });
            Assert.True(SwitchSegmentNoOpClassifier.IsNoOpResumeTail(noop, 100, out _));
        }
    }

    /// <summary>
    /// Integration tests for the live-side resolver
    /// <see cref="RecordingStore.TryClassifyActiveSwitchSegmentNoOp"/>: tree /
    /// segment resolution, disposition classification, and the no-op decision
    /// against an armed <see cref="SwitchSegmentSession"/>. (The ParsekFlight
    /// flush + Re-Fly / merge-journal guards and the scene-exit / re-switch hooks
    /// are runtime-coupled and covered in-game.)
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentNoOpEvaluatorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentNoOpEvaluatorTests()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Segment(string id, string treeId, bool noOp)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = noOp ? SegmentEnvironment.ExoBallistic
                                   : SegmentEnvironment.ExoPropulsive,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100, endUT = 200,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }

        private static RecordingTree Tree(string treeId, string activeId, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var r in recs) tree.AddOrReplaceRecording(r);
            tree.RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null;
            tree.ActiveRecordingId = activeId;
            return tree;
        }

        private static SwitchSegmentSession ArmSession(
            string treeId, string segId, string parentId = null,
            string committedTreeId = null)
        {
            var scenario = ParsekScenario.Instance ?? new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.TrackingStationFly,
                TreeId = treeId,
                ParentRecordingId = parentId,
                ActiveSegmentRecordingId = segId,
                CommittedTreeId = committedTreeId,
                SwitchUT = 150.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            scenario.ArmSwitchSegmentSession(session);
            return session;
        }

        [Fact]
        public void NoSession_KeepsWithReason()
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out SwitchSegmentDisposition disp);
            Assert.False(noOp);
            Assert.Equal("no-session", reason);
            Assert.Equal(SwitchSegmentDisposition.None, disp);
        }

        [Fact]
        public void StandaloneNoOpSegment_IsNoOp_WithStandaloneDisposition()
        {
            var seg = Segment("rec_seg", "tree_s", noOp: true);
            var tree = Tree("tree_s", "rec_seg", seg);
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_s", "rec_seg");

            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out SwitchSegmentDisposition disp);

            Assert.True(noOp);
            Assert.Equal("no-op", reason);
            Assert.Equal(SwitchSegmentDisposition.Standalone, disp);
        }

        [Fact]
        public void StandaloneActiveSegment_ThatDidSomething_IsKept()
        {
            var seg = Segment("rec_seg", "tree_s", noOp: false); // ExoPropulsive
            var tree = Tree("tree_s", "rec_seg", seg);
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_s", "rec_seg");

            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out SwitchSegmentDisposition disp);

            Assert.False(noOp);
            Assert.StartsWith("non-boring-section:", reason);
            Assert.Equal(SwitchSegmentDisposition.Standalone, disp);
        }

        [Fact]
        public void ContinuationUnderParent_NoOp_IsBgMemberOrMixedDisposition()
        {
            var parent = new Recording { RecordingId = "rec_parent", TreeId = "tree_c" };
            var seg = Segment("rec_seg", "tree_c", noOp: true);
            var tree = Tree("tree_c", "rec_seg", parent, seg);
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_c", "rec_seg", parentId: "rec_parent");

            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out SwitchSegmentDisposition disp);

            // No-op decision still true; disposition flags it as deferred-at-scene-exit.
            Assert.True(noOp);
            Assert.Equal(SwitchSegmentDisposition.BgMemberOrMixed, disp);
        }

        [Fact]
        public void SegmentNotActiveRecording_IsKept()
        {
            var parent = new Recording { RecordingId = "rec_parent", TreeId = "tree_n" };
            var seg = Segment("rec_seg", "tree_n", noOp: true);
            // Active recording is the PARENT, not the segment.
            var tree = Tree("tree_n", "rec_parent", parent, seg);
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_n", "rec_seg", parentId: "rec_parent");

            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out _);

            Assert.False(noOp);
            Assert.Equal("segment-not-active-recording", reason);
        }

        [Fact]
        public void HasDescendantChild_IsKept()
        {
            // Segment with a child branch point + child recording → descendants.
            string bpId = "bp_child";
            var seg = Segment("rec_seg", "tree_d", noOp: true);
            seg.ChildBranchPointId = bpId;
            var child = new Recording
            {
                RecordingId = "rec_child", TreeId = "tree_d", ParentBranchPointId = bpId
            };
            var tree = Tree("tree_d", "rec_seg", seg, child);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { "rec_seg" },
                ChildRecordingIds = new List<string> { "rec_child" },
            });
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_d", "rec_seg");

            bool noOp = RecordingStore.TryClassifyActiveSwitchSegmentNoOp(
                out string reason, out _);

            Assert.False(noOp);
            Assert.Equal("has-descendants", reason);
        }

        [Fact]
        public void LogsDecisionLine()
        {
            var seg = Segment("rec_seg", "tree_s", noOp: true);
            var tree = Tree("tree_s", "rec_seg", seg);
            RecordingStore.StashPendingTree(tree);
            ArmSession("tree_s", "rec_seg");

            RecordingStore.TryClassifyActiveSwitchSegmentNoOp(out _, out _);

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("TryClassifyActiveSwitchSegmentNoOp")
                && l.Contains("noOp=True"));
        }
    }
}
