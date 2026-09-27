using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CARGOBAY-DEPLOY-LIMITED-BAY-RECORDS-NOTHING: a bay whose ModuleAnimateGeneric has
    /// allowDeployLimit = true and deployPercent &lt; 100 (the stock Mallard's Mk3 bays at
    /// 44 / 45 / 51) stops part-way. The recorder must call it open at that stop, and the
    /// ghost must pose it there. Stock's stop formula (KSP 1.12.5, decompiled
    /// ModuleAnimateGeneric): deployPercent * 0.01, snapped to 1 above 0.995, mirrored to
    /// 1 - stop under revClampPercent.
    /// </summary>
    public class CargoBayDeployLimitTests
    {
        private const float Tol = 1e-5f;

        #region ResolveCargoBayDeployLimitStop

        [Fact]
        public void Stop_StockMk3Medium45Percent_ClosedPosition1_Is055()
        {
            float? stop = FlightRecorder.ResolveCargoBayDeployLimitStop(
                allowDeployLimit: true, deployPercent: 45f, revClampPercent: true, closedPosition: 1f);
            Assert.True(stop.HasValue);
            Assert.InRange(stop.Value, 0.55f - Tol, 0.55f + Tol);
        }

        [Fact]
        public void Stop_ClosedPosition0_NoRevClamp_IsDeployFraction()
        {
            float? stop = FlightRecorder.ResolveCargoBayDeployLimitStop(
                allowDeployLimit: true, deployPercent: 45f, revClampPercent: false, closedPosition: 0f);
            Assert.True(stop.HasValue);
            Assert.InRange(stop.Value, 0.45f - Tol, 0.45f + Tol);
        }

        [Fact]
        public void Stop_AllowDeployLimitFalse_IsNull()
        {
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(
                allowDeployLimit: false, deployPercent: 45f, revClampPercent: true, closedPosition: 1f));
        }

        [Theory]
        [InlineData(100f)]
        [InlineData(99.6f)] // stock snaps a clamp above 0.995 to the full end
        public void Stop_FullDeploy_IsNull(float deployPercent)
        {
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(
                true, deployPercent, true, 1f));
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(
                true, deployPercent, false, 0f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1f)] // 0.99: inside the closed-end band, cannot be told from shut
        public void Stop_AtTheClosedEnd_IsNull(float deployPercent)
        {
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(
                true, deployPercent, true, 1f));
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(
                true, deployPercent, false, 0f));
        }

        [Fact]
        public void Stop_NonStandardClosedPosition_IsNull()
        {
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(true, 45f, true, 0.5f));
        }

        [Fact]
        public void Stop_NaNDeployPercent_IsNull()
        {
            Assert.Null(FlightRecorder.ResolveCargoBayDeployLimitStop(true, float.NaN, true, 1f));
        }

        #endregion

        #region ClassifyCargoBayState with a limit

        [Theory]
        [InlineData(0.55f)]
        [InlineData(0.545f)]
        [InlineData(0.555f)]
        public void Classify_ClosedPosition1_AtLimitStop_IsOpen(float animTime)
        {
            FlightRecorder.ClassifyCargoBayState(animTime, 1f, 0.55f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_ClosedPosition1_Limited_AtClosedEnd_IsClosed()
        {
            FlightRecorder.ClassifyCargoBayState(1f, 1f, 0.55f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.True(isClosed);
        }

        [Theory]
        [InlineData(0.8f)]
        [InlineData(0.6f)]
        [InlineData(0.3f)]
        public void Classify_ClosedPosition1_Limited_MidTravel_IsNeither(float animTime)
        {
            FlightRecorder.ClassifyCargoBayState(animTime, 1f, 0.55f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_ClosedPosition1_Limited_FullOpenEndStillOpen()
        {
            // A limit does not make the ordinary open end stop meaning open.
            FlightRecorder.ClassifyCargoBayState(0f, 1f, 0.55f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_ClosedPosition0_AtLimitStop_IsOpen()
        {
            FlightRecorder.ClassifyCargoBayState(0.45f, 0f, 0.45f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_ClosedPosition0_Limited_AtClosedEnd_IsClosed()
        {
            FlightRecorder.ClassifyCargoBayState(0f, 0f, 0.45f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.True(isClosed);
        }

        [Fact]
        public void Classify_ClosedPosition0_Limited_MidTravel_IsNeither()
        {
            FlightRecorder.ClassifyCargoBayState(0.2f, 0f, 0.45f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Theory]
        [InlineData(0.55f, 1f)]
        [InlineData(0.45f, 0f)]
        public void Classify_NoLimit_PartWayStopIsNeither_Unchanged(float animTime, float closedPosition)
        {
            // allowDeployLimit = false resolves to a null stop: the pre-fix behavior holds.
            float? stop = FlightRecorder.ResolveCargoBayDeployLimitStop(
                false, 45f, closedPosition > 0.5f, closedPosition);
            FlightRecorder.ClassifyCargoBayState(animTime, closedPosition, stop, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_NonStandardClosedPosition_IgnoresLimit()
        {
            FlightRecorder.ClassifyCargoBayState(0.55f, 0.5f, 0.55f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void Classify_LimitedOpenThenClosed_EmitsOneEdgeEach()
        {
            // The recorder's edge detector over the new classifier: closed -> travel ->
            // settled at the limit -> travel -> closed yields exactly Opened then Closed.
            var openSet = new System.Collections.Generic.HashSet<uint>();
            float[] polls = { 1f, 0.9f, 0.7f, 0.55f, 0.55f, 0.7f, 0.9f, 1f };
            var events = new System.Collections.Generic.List<PartEventType>();
            float? stop = FlightRecorder.ResolveCargoBayDeployLimitStop(true, 45f, true, 1f);
            for (int i = 0; i < polls.Length; i++)
            {
                FlightRecorder.ClassifyCargoBayState(polls[i], 1f, stop, out bool isOpen, out bool isClosed);
                if (!isOpen && !isClosed) continue;
                PartEvent? evt = FlightRecorder.CheckCargoBayTransition(
                    100000u, "mk3CargoBayM", isOpen, openSet, i);
                if (evt.HasValue) events.Add(evt.Value.eventType);
            }
            Assert.Equal(
                new[] { PartEventType.CargoBayOpened, PartEventType.CargoBayClosed },
                events.ToArray());
        }

        #endregion

        #region Snapshot side: node pick, stop, baseline, pose sample times

        private static ConfigNode PartWithModules(
            params (string moduleName, (string key, string value)[] kv)[] modules)
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("CargoLimitProbe");
            for (int i = 0; i < modules.Length; i++)
                builder.AddModuleToPart(0, modules[i].moduleName, modules[i].kv);
            return builder.Build().GetNodes("PART")[0];
        }

        private static (string, (string, string)[]) Module(
            string name, params (string, string)[] kv) => (name, kv);

        private static ConfigNode Mk3MediumAt(string animTime, string deployPercent = "45")
            => PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", animTime), ("deployPercent", deployPercent)),
                Module("ModuleCargoBay"));

        [Fact]
        public void SnapshotNodePick_PrefersDeployModuleIndex_ElseFirst()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("deployPercent", "10")),
                Module("ModuleAnimateGeneric", ("deployPercent", "45")),
                Module("ModuleCargoBay"));

            Assert.Equal("45", GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(partNode, 1)
                .GetValue("deployPercent"));
            // Index 2 is the ModuleCargoBay: falls back to the first animation.
            Assert.Equal("10", GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(partNode, 2)
                .GetValue("deployPercent"));
            Assert.Null(GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(null, 0));
        }

        [Fact]
        public void SnapshotStop_DeployPercent45_Mk3Config_Is055()
        {
            ConfigNode anim = GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(Mk3MediumAt("1"), 0);
            float? stop = GhostVisualBuilder.ResolveSnapshotCargoBayDeployLimitStop(
                anim, allowDeployLimit: true, revClampPercent: true, closedPosition: 1f);
            Assert.True(stop.HasValue);
            Assert.InRange(stop.Value, 0.55f - Tol, 0.55f + Tol);
        }

        [Fact]
        public void SnapshotStop_NoDeployPercentKey_OrNoLimitConfig_IsNull()
        {
            ConfigNode noKey = PartWithModules(Module("ModuleAnimateGeneric", ("animTime", "1")));
            Assert.Null(GhostVisualBuilder.ResolveSnapshotCargoBayDeployLimitStop(
                GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(noKey, 0), true, true, 1f));

            ConfigNode anim = GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(Mk3MediumAt("1"), 0);
            Assert.Null(GhostVisualBuilder.ResolveSnapshotCargoBayDeployLimitStop(anim, false, true, 1f));
        }

        [Fact]
        public void Baseline_DeployLimitedBaySavedAtItsStop_SpawnsOpen()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                Mk3MediumAt("0.55"), cargoBayClosedPosition: 1f, cargoDeployModuleIndex: 0,
                cargoAllowDeployLimit: true, cargoRevClampPercent: true);
            Assert.NotNull(baseline);
            Assert.True(baseline.cargoBayOpen);
        }

        [Fact]
        public void Baseline_DeployLimitedBaySavedClosed_SpawnsClosed()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                Mk3MediumAt("1"), cargoBayClosedPosition: 1f, cargoDeployModuleIndex: 0,
                cargoAllowDeployLimit: true, cargoRevClampPercent: true);
            Assert.NotNull(baseline);
            Assert.False(baseline.cargoBayOpen);
        }

        [Fact]
        public void Baseline_PartWayWithoutLimitConfig_NoOpinion_Unchanged()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                Mk3MediumAt("0.55"), cargoBayClosedPosition: 1f, cargoDeployModuleIndex: 0);
            Assert.Null(baseline);
        }

        [Fact]
        public void SampleTimes_DeployPercent45Snapshot_OpenPoseAtTheLimit()
        {
            // The ghost's deployed pose is sampled where the snapshot's doors stop, so the
            // applier's stowed->deployed interpolation ends at the limit, not fully open.
            ConfigNode anim = GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(Mk3MediumAt("1"), 0);
            float? stop = GhostVisualBuilder.ResolveSnapshotCargoBayDeployLimitStop(anim, true, true, 1f);

            Assert.True(GhostVisualBuilder.TryResolveCargoBaySampleTimes(
                1f, stop, out float closedTime, out float openTime));
            Assert.Equal(1f, closedTime);
            Assert.InRange(openTime, 0.55f - Tol, 0.55f + Tol);
        }

        [Theory]
        [InlineData(1f, 1f, 0f)]
        [InlineData(0f, 0f, 1f)]
        public void SampleTimes_NoLimit_FullEnds_Unchanged(float closedPosition, float expClosed, float expOpen)
        {
            Assert.True(GhostVisualBuilder.TryResolveCargoBaySampleTimes(
                closedPosition, null, out float closedTime, out float openTime));
            Assert.Equal(expClosed, closedTime);
            Assert.Equal(expOpen, openTime);
        }

        [Fact]
        public void SampleTimes_ClosedPosition0_Limited_OpenAtStop()
        {
            Assert.True(GhostVisualBuilder.TryResolveCargoBaySampleTimes(
                0f, 0.45f, out float closedTime, out float openTime));
            Assert.Equal(0f, closedTime);
            Assert.Equal(0.45f, openTime);
        }

        [Fact]
        public void SampleTimes_NonStandardClosedPosition_False()
        {
            Assert.False(GhostVisualBuilder.TryResolveCargoBaySampleTimes(0.5f, 0.45f, out _, out _));
        }

        [Fact]
        public void Baseline_And_Pose_ReadTheSameStop()
        {
            // The spawn classification and the pose sample both key off one resolver over
            // one node: a snapshot saved exactly at the pose's open time reads open.
            ConfigNode partNode = Mk3MediumAt("1", deployPercent: "51");
            float? stop = GhostVisualBuilder.ResolveSnapshotCargoBayDeployLimitStop(
                GhostVisualBuilder.FindSnapshotCargoAnimateGenericNode(partNode, 0), true, true, 1f);
            GhostVisualBuilder.TryResolveCargoBaySampleTimes(1f, stop, out _, out float openTime);

            SnapshotPartBaseline atOpen = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                Mk3MediumAt(openTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "51"),
                1f, 0, true, true);
            Assert.NotNull(atOpen);
            Assert.True(atOpen.cargoBayOpen);
        }

        #endregion
    }
}
