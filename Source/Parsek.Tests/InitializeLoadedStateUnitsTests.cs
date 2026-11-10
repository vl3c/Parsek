using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units extracted from <c>BackgroundRecorder.InitializeLoadedState</c>:
    /// the initial environment, the debris seed recording lookup, the seed-time debris tier, the
    /// Absolute fallback, the seed anchor pose chain, the contract-applied line and the Absolute
    /// first section.
    /// </summary>
    [Collection("Sequential")]
    public class InitializeLoadedStateUnitsTests : IDisposable
    {
        private const uint Pid = 5301u;
        private const uint ParentPid = 5302u;
        private const string RecId = "init-units-rec";
        private const string ParentRecId = "init-units-parent";

        private readonly List<string> logLines = new List<string>();
        private readonly Planetarium savedPlanetarium;

        public InitializeLoadedStateUnitsTests()
        {
            // ParsekSettings' static constructor logs once per AppDomain; run it before the
            // sink is installed so a single-line assertion does not depend on test order.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 100.0;
            savedPlanetarium = Planetarium.fetch;
        }

        public void Dispose()
        {
            Planetarium.fetch = savedPlanetarium;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class Fixture
        {
            public RecordingTree Tree;
            public BackgroundRecorder Bg;
            public Recording Rec;
            public Recording Parent;
        }

        private static Fixture Build()
        {
            var tree = new RecordingTree { Id = "init-units-tree" };
            var parent = new Recording
            {
                RecordingId = ParentRecId, TreeId = tree.Id, VesselPersistentId = ParentPid,
            };
            var rec = new Recording
            {
                RecordingId = RecId, TreeId = tree.Id, VesselPersistentId = Pid,
                IsDebris = true, ParentAnchorRecordingId = ParentRecId,
            };
            tree.Recordings[ParentRecId] = parent;
            tree.Recordings[RecId] = rec;
            tree.BackgroundMap[Pid] = RecId;
            return new Fixture { Tree = tree, Bg = new BackgroundRecorder(tree), Rec = rec, Parent = parent };
        }

        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint
        {
            ut = ut, latitude = -0.1, longitude = -74.5, altitude = 1200.0, bodyName = "Kerbin",
            rotation = Quaternion.identity, velocity = new Vector3(1f, 2f, 3f),
            recordedGroundClearance = double.NaN,
        };

        // ---------------- ResolveLoadedInitialEnvironment

        [Fact]
        public void InitialEnvironment_QueuedOverride_ConsumedAndLogged()
        {
            Fixture f = Build();
            InstallPlanetarium(50.0);
            f.Bg.OnVesselBackgrounded(Pid, initialEnvironmentOverride: SegmentEnvironment.SurfaceStationary);
            logLines.Clear();

            SegmentEnvironment env = f.Bg.ResolveLoadedInitialEnvironmentForTesting(null, Pid);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, env);
            Assert.Equal(0, f.Bg.PendingInitialEnvironmentOverrideCount);
            Assert.Equal(
                "[Parsek][VERBOSE][BgRecorder] InitializeLoadedState: consumed initial environment override " +
                "for pid=5301: SurfaceStationary",
                Assert.Single(logLines));
        }

        [UnityEntryFact]
        public void InitialEnvironment_NoOverride_ClassifiesTheVessel()
        {
            Fixture f = Build();
            Vessel high = Vessel(Pid, alive: false, body: Body("Kerbin", true, 70000.0), altitude: 90000.0);
            high.situation = global::Vessel.Situations.SUB_ORBITAL;
            Vessel low = Vessel(Pid, alive: false, body: Body("Kerbin", true, 70000.0), altitude: 1000.0);
            low.situation = global::Vessel.Situations.FLYING;

            Assert.Equal(SegmentEnvironment.ExoBallistic, f.Bg.ResolveLoadedInitialEnvironmentForTesting(high, Pid));
            Assert.Equal(SegmentEnvironment.Atmospheric, f.Bg.ResolveLoadedInitialEnvironmentForTesting(low, Pid));
            Assert.Empty(logLines);
        }

        // ---------------- ResolveDebrisSeedRecording

        [Fact]
        public void DebrisSeedRecording_SeededRecordingWins()
        {
            Fixture f = Build();
            var other = new Recording { RecordingId = "other" };
            Assert.Same(other, f.Bg.ResolveDebrisSeedRecordingForTesting(true, other, RecId));
        }

        [Fact]
        public void DebrisSeedRecording_NotSeeded_LooksUpTheTree()
        {
            Fixture f = Build();
            Assert.Same(f.Rec, f.Bg.ResolveDebrisSeedRecordingForTesting(false, new Recording(), RecId));
            Assert.Same(f.Rec, f.Bg.ResolveDebrisSeedRecordingForTesting(true, null, RecId));
        }

        [Fact]
        public void DebrisSeedRecording_UnknownOrEmptyId_Null()
        {
            Fixture f = Build();
            Assert.Null(f.Bg.ResolveDebrisSeedRecordingForTesting(false, null, "missing"));
            Assert.Null(f.Bg.ResolveDebrisSeedRecordingForTesting(false, null, ""));
            Assert.Null(f.Bg.ResolveDebrisSeedRecordingForTesting(false, null, null));
        }

        // ---------------- SeedDebrisProximityTier

        [Fact]
        public void DebrisTier_WithinFullRange_FullTierAndTransitionLine()
        {
            Fixture f = Build();
            f.Bg.InjectLoadedStateForTesting(Pid, RecId);

            f.Bg.SeedDebrisProximityTierForTesting(Pid, f.Rec, 100.0);

            f.Bg.GetDebrisProximityForTesting(Pid, out ProximitySamplingTier tier, out double distance, out string reason);
            ProximitySamplingTier expected = ProximitySamplingCadence.Resolve(100.0,
                BackgroundRecorder.DebrisFullFidelityProximityRangeMeters,
                BackgroundRecorder.DebrisHalfFidelityProximityRangeMeters, out string expectedReason);
            Assert.Equal(expected, tier);
            Assert.Equal(100.0, distance);
            Assert.Equal(expectedReason, reason);
            string line = Assert.Single(logLines);
            Assert.StartsWith("[Parsek][INFO][Anchor] debris-proximity-tier-transition: site=InitializeLoadedState " +
                "pid=5301 recId=init-units-rec parentRecId=init-units-parent old=None new=" + expected +
                " distance=100.0m reason=", line);
        }

        [Fact]
        public void DebrisTier_Unresolved_NoTierNoLine()
        {
            Fixture f = Build();
            f.Bg.InjectLoadedStateForTesting(Pid, RecId);

            f.Bg.SeedDebrisProximityTierForTesting(Pid, f.Rec, double.NaN);

            f.Bg.GetDebrisProximityForTesting(Pid, out ProximitySamplingTier tier, out double distance, out string reason);
            Assert.Equal(ProximitySamplingTier.None, tier);
            Assert.True(double.IsNaN(distance));
            Assert.Empty(logLines);
        }

        // ---------------- FallBackToAbsoluteDebrisSeed

        [Fact]
        public void AbsoluteFallback_DropsTheQueuedParentPose_AndWarns()
        {
            Fixture f = Build();
            f.Bg.QueueDebrisSeedParentAnchorPoint(Pid, ParentRecId, Pt(9.0));
            Assert.True(f.Bg.HasPendingDebrisSeedParentAnchorPointForTesting(Pid));
            logLines.Clear();

            f.Bg.FallBackToAbsoluteDebrisSeedForTesting(null, Pid, RecId, f.Rec, f.Parent, null, 812.0, 10.5, Pt(10.0));

            Assert.False(f.Bg.HasPendingDebrisSeedParentAnchorPointForTesting(Pid));
            string line = Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][WARN][BgRecorder] InitializeLoadedState: debris contract uses Absolute seed, " +
                "falling back to Absolute seed: pid=5301 recId=init-units-rec parentRecId=init-units-parent " +
                "parentRecResolved=True parentVesselResolved=False parentVesselLoaded=False " +
                "parentVesselPacked=False parentDistance=812.0m relativeEntryRange=500.0m initUT=10.500 " +
                "seed=ut=10.000 body=Kerbin lla=(-0.1000,-74.5000,1200.00) rot=" +
                DiagnosticFormatters.FormatQuaternion(Quaternion.identity) +
                " vel=" + DiagnosticFormatters.FormatVector3(new Vector3(1f, 2f, 3f)) + " child=null",
                line);
        }

        [Fact]
        public void AbsoluteFallback_NoParentRecording_ReportsUnresolved()
        {
            Fixture f = Build();
            f.Bg.FallBackToAbsoluteDebrisSeedForTesting(null, Pid, RecId, f.Rec, null, null, double.NaN, 10.5, Pt(10.0));

            string line = Assert.Single(logLines);
            Assert.Contains("parentRecResolved=False parentVesselResolved=False", line);
            Assert.Contains("parentDistance=NaNm", line);
        }

        // ---------------- ResolveDebrisSeedAnchorPose

        [Fact]
        public void SeedAnchorPose_NothingResolves_LivePoseWithWarn()
        {
            Fixture f = Build();
            var live = new AnchorPose(new Vector3d(1, 2, 3), Quaternion.identity, -1, ParentRecId);

            f.Bg.ResolveDebrisSeedAnchorPoseForTesting(Pid, RecId, f.Rec, Pt(10.0), 10.5, live,
                out AnchorPose pose, out string source, out string contract);

            Assert.Equal(live.WorldPos.x, pose.WorldPos.x);
            Assert.Equal(live.WorldPos.y, pose.WorldPos.y);
            Assert.Equal(live.WorldPos.z, pose.WorldPos.z);
            Assert.Equal(live.ResolvedRecordingId, pose.ResolvedRecordingId);
            Assert.Equal("live-warn-fallback", source);
            Assert.Equal("unresolved", contract);
            string line = Assert.Single(logLines, l => l.Contains("debris seed recorded parent pose unavailable"));
            Assert.StartsWith(
                "[Parsek][WARN][BgRecorder] InitializeLoadedState: debris seed recorded parent pose unavailable, " +
                "falling back to live parent pose: pid=5301 recId=init-units-rec parentRecId=init-units-parent reason=",
                line);
            Assert.Contains(" queuedReason=queued-parent-anchor-missing seedUT=10.000 initUT=10.500", line);
        }

        [Fact]
        public void SeedAnchorPose_QueuedForAnotherParent_ConsumedAndReasonReported()
        {
            Fixture f = Build();
            f.Bg.QueueDebrisSeedParentAnchorPoint(Pid, "someone-else", Pt(9.9));
            logLines.Clear();
            var live = new AnchorPose(new Vector3d(1, 2, 3), Quaternion.identity, -1, ParentRecId);

            f.Bg.ResolveDebrisSeedAnchorPoseForTesting(Pid, RecId, f.Rec, Pt(10.0), 10.5, live,
                out AnchorPose pose, out string source, out string contract);

            Assert.False(f.Bg.HasPendingDebrisSeedParentAnchorPointForTesting(Pid));
            Assert.Equal("live-warn-fallback", source);
            Assert.Contains(logLines, l => l.Contains("queuedReason=queued-parent-recording-id-mismatch"));
        }

        // ---------------- LogDebrisSeedContractApplied

        [Fact]
        public void ContractApplied_InfoLine()
        {
            Fixture f = Build();
            var parentVessel = (Vessel)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(Vessel));
            parentVessel.persistentId = ParentPid;
            TrajectoryPoint offset = Pt(10.0);
            offset.latitude = 1.5;
            offset.longitude = -2.25;
            offset.altitude = 3.0;

            BackgroundRecorder.LogDebrisSeedContractAppliedForTesting(Pid, RecId, f.Rec, parentVessel,
                "contract-x", "parentDrift=d ", "queued-parent-seed", RecId, "unresolved", offset);

            string expected = "[Parsek][INFO][BgRecorder] Debris parent-anchor contract applied at seed: pid=5301 " +
                "recId=init-units-rec parentRecId=init-units-parent parentPid=5302 frameContract=contract-x " +
                "parentDrift=d seedAnchorSource=queued-parent-seed seedFocusRecordingId=init-units-rec " +
                "seedRecordedFallbackFrameContract=unresolved seedUT=10.00 |offset|=(dx=" +
                1.5.ToString("F2") + ",dy=" + (-2.25).ToString("F2") + ",dz=" + 3.0.ToString("F2") + ")";
            Assert.Equal(expected, Assert.Single(logLines));
        }

        // ---------------- OpenAbsoluteInitialTrackSection

        [Fact]
        public void AbsoluteOpen_NoSeed_SectionAtNowNoWindow()
        {
            Fixture f = Build();
            f.Bg.OpenAbsoluteInitialTrackSectionForTesting(Pid, RecId, SegmentEnvironment.Atmospheric,
                false, default(TrajectoryPoint), 20.0, true, f.Rec);

            TrackSection? section = f.Bg.GetCurrentTrackSectionForTesting(Pid);
            Assert.True(f.Bg.GetTrackSectionActiveForTesting(Pid));
            Assert.Equal(ReferenceFrame.Absolute, section.Value.referenceFrame);
            Assert.Equal(20.0, section.Value.startUT);
            Assert.Equal(SegmentEnvironment.Atmospheric, section.Value.environment);
            Assert.True(double.IsNaN(f.Bg.GetHighFidelitySamplingUntilUTForTesting(Pid)));
            Assert.Empty(f.Rec.Points);
        }

        [Fact]
        public void AbsoluteOpen_SeedWithRecording_SectionAtSeedPointOnRecordingAndWindow()
        {
            Fixture f = Build();
            f.Bg.OpenAbsoluteInitialTrackSectionForTesting(Pid, RecId, SegmentEnvironment.Atmospheric,
                true, Pt(19.5), 20.0, true, f.Rec);

            TrackSection? section = f.Bg.GetCurrentTrackSectionForTesting(Pid);
            Assert.Equal(19.5, section.Value.startUT);
            Assert.Single(section.Value.frames);
            Assert.Single(f.Rec.Points);
            Assert.Equal(19.5, f.Rec.Points[0].ut);
            Assert.False(double.IsNaN(f.Bg.GetHighFidelitySamplingUntilUTForTesting(Pid)));
            Assert.Contains(logLines, l => l.Contains("reason=initial-trajectory-point "));
        }

        [Fact]
        public void AbsoluteOpen_SeedWithoutRecording_FrameOnSectionOnly()
        {
            Fixture f = Build();
            f.Bg.OpenAbsoluteInitialTrackSectionForTesting(Pid, RecId, SegmentEnvironment.Atmospheric,
                true, Pt(19.5), 20.0, false, null);

            TrackSection? section = f.Bg.GetCurrentTrackSectionForTesting(Pid);
            Assert.Equal(19.5, section.Value.startUT);
            Assert.Single(section.Value.frames);
            Assert.Empty(f.Rec.Points);
        }
    }
}
