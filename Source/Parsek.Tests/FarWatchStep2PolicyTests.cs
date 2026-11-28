using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The drone VESSEL node contract (design-far-watch.md 6.2).</summary>
    public class CameraDroneBuildNodeTests
    {
        private static ConfigNode StockLikeNode(string partName = CameraDrone.PartName)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "Untitled Space Craft");
            node.AddValue("type", "Ship");
            node.AddValue("sit", "PRELAUNCH");
            node.AddValue("landed", "True");
            var part = new ConfigNode("PART");
            part.AddValue("name", partName);
            part.AddValue("crew", "Jebediah Kerman");
            node.AddNode(part);
            return node;
        }

        [Fact]
        public void ApplyFlags_MakesTheNodeMeetTheContract()
        {
            ConfigNode node = StockLikeNode();
            Assert.NotEmpty(CameraDrone.DescribeNodeProblems(node));
            CameraDrone.ApplyDroneNodeFlags(node, "ORBITING", 1000.0);
            Assert.Empty(CameraDrone.DescribeNodeProblems(node));
            Assert.Equal(CameraDroneIdentity.DroneVesselName, node.GetValue("name"));
            Assert.Equal("Debris", node.GetValue("type"));
            Assert.False(node.GetNodes("PART")[0].HasValue("crew"));
            ConfigNode disc = node.GetNode("DISCOVERY");
            Assert.Equal("0", disc.GetValue("state"));
        }

        [Fact]
        public void ApplyFlags_NullSituation_IsOrbiting()
        {
            ConfigNode node = StockLikeNode();
            CameraDrone.ApplyDroneNodeFlags(node, null, 0.0);
            Assert.Equal("ORBITING", node.GetValue("sit"));
        }

        [Fact]
        public void Problems_NameEachBreach()
        {
            ConfigNode node = StockLikeNode();
            CameraDrone.ApplyDroneNodeFlags(node, "FLYING", 0.0);
            node.SetValue("sit", "LANDED");
            node.SetValue("cln", "True");
            node.AddNode(CameraDrone.BuildDiscoveryNode(0.0));
            List<string> problems = CameraDrone.DescribeNodeProblems(node);
            Assert.Contains("sit=LANDED", problems);
            Assert.Contains("cln=True", problems);
            Assert.Contains("discovery-count=2", problems);
            Assert.Contains("node-null", CameraDrone.DescribeNodeProblems(null));
        }

        [Fact]
        public void Problems_WrongPart()
        {
            ConfigNode node = StockLikeNode("mk1pod.v2");
            CameraDrone.ApplyDroneNodeFlags(node, "ORBITING", 0.0);
            Assert.Contains("part-name=mk1pod.v2", CameraDrone.DescribeNodeProblems(node));
        }

        [Fact]
        public void ModuleProblems_StockListAndModAllowance()
        {
            Assert.Empty(CameraDrone.DescribeModuleProblems(
                new[] { "ModuleCargoPart", "KOSNameTag" }, CameraDrone.ExpectedVesselModules));
            List<string> p = CameraDrone.DescribeModuleProblems(
                new[] { "ModuleCommand" }, new[] { "FlightIntegrator", "CommNetVessel" });
            Assert.Contains("unexpected-part-module=ModuleCommand", p);
            Assert.Contains("missing-part-module=ModuleCargoPart", p);
            Assert.Contains("unexpected-vessel-module=CommNetVessel", p);
            Assert.Contains("missing-vessel-module=AxisGroupsModule", p);
        }

        [Fact]
        public void BesideShip_InFlight_OffsetAlongOrbitNormal()
        {
            Assert.True(CameraDrone.TryComputeBesideShip(new Vector3d(1000, 0, 0), Vector3d.zero, new Vector3d(0, 10, 0),
                false, 50.0, out Vector3d pos, out bool held));
            Assert.False(held);
            Assert.Equal(1000.0, pos.x, 6);
            Assert.Equal(0.0, pos.y, 6);
            Assert.Equal(50.0, pos.z, 6);
        }

        [Fact]
        public void BesideShip_Landed_StraightUpAndHeld()
        {
            Assert.True(CameraDrone.TryComputeBesideShip(new Vector3d(0, 600, 0), Vector3d.zero, Vector3d.zero,
                true, 50.0, out Vector3d pos, out bool held));
            Assert.True(held);
            Assert.Equal(650.0, pos.y, 6);
        }

        [Fact]
        public void BesideShip_Degenerate_Refused()
        {
            Assert.False(CameraDrone.TryComputeBesideShip(Vector3d.zero, Vector3d.zero, new Vector3d(0, 1, 0),
                false, 50.0, out _, out _));
            // Radial velocity in flight: no orbit normal.
            Assert.False(CameraDrone.TryComputeBesideShip(new Vector3d(1000, 0, 0), Vector3d.zero, new Vector3d(5, 0, 0),
                false, 50.0, out _, out _));
        }
    }

    /// <summary>The terrain floor and the state validation (design-far-watch.md 6.3).</summary>
    public class CameraDroneFloorTests
    {
        [Fact]
        public void Floor_BasePlusFallPlusGravity()
        {
            Assert.Equal(20.0, CameraDrone.TerrainFloorMeters(0.0, 0.0, 9.81), 9);
            Assert.Equal(20.0 + 100.0 + 0.5 * 10.0 * 4.0, CameraDrone.TerrainFloorMeters(-50.0, 2.0, 10.0), 9);
            // Climbing adds no margin; a non-finite input reads as 0.
            Assert.Equal(20.0, CameraDrone.TerrainFloorMeters(50.0, 2.0, 0.0), 9);
            Assert.Equal(20.0, CameraDrone.TerrainFloorMeters(double.NaN, double.PositiveInfinity, -1.0), 9);
        }

        [Fact]
        public void Clamp_RaisesToTheFloor_SeaLevelOverOcean()
        {
            Assert.Equal(120.0, CameraDrone.ClampAltitudeToFloor(50.0, 100.0, false, 20.0, out bool c1));
            Assert.True(c1);
            Assert.Equal(500.0, CameraDrone.ClampAltitudeToFloor(500.0, 100.0, false, 20.0, out bool c2));
            Assert.False(c2);
            Assert.Equal(20.0, CameraDrone.ClampAltitudeToFloor(5.0, -300.0, true, 20.0, out bool c3));
            Assert.True(c3);
            Assert.Equal(20.0, CameraDrone.ClampAltitudeToFloor(double.NaN, 0.0, false, 20.0, out bool c4));
            Assert.True(c4);
        }

        [Fact]
        public void ValidateState_Cases()
        {
            double mu = 3.5316e12, R = 600000.0;
            var r = new Vector3d(700000.0, 0, 0);
            double vCirc = Math.Sqrt(mu / 700000.0);
            Assert.Equal(CameraDroneStateCheck.Valid,
                CameraDrone.ValidateState(r, new Vector3d(0, vCirc, 0), R, mu));
            Assert.Equal(CameraDroneStateCheck.BelowBody,
                CameraDrone.ValidateState(new Vector3d(598000.0, 0, 0), new Vector3d(0, vCirc, 0), R, mu));
            Assert.Equal(CameraDroneStateCheck.NonFinite,
                CameraDrone.ValidateState(new Vector3d(double.NaN, 0, 0), Vector3d.zero, R, mu));
            double vEsc = Math.Sqrt(2.0 * mu / 700000.0);
            Assert.Equal(CameraDroneStateCheck.Parabolic,
                CameraDrone.ValidateState(r, new Vector3d(0, vEsc, 0), R, mu));
        }
    }

    /// <summary>The career fences and belts (design-far-watch.md 10).</summary>
    public class FarWatchFencePredicateTests
    {
        [Fact]
        public void PolledList_IsTheEighteenClasses()
        {
            Assert.Equal(18, FarWatchFencePolicy.PolledParameterTypeNames.Length);
            Assert.Equal(18, new HashSet<string>(FarWatchFencePolicy.PolledParameterTypeNames).Count);
            Assert.True(FarWatchFencePolicy.IsPolledParameterTypeName("Contracts.Parameters.ReachBiome"));
            Assert.False(FarWatchFencePolicy.IsPolledParameterTypeName("Contracts.Parameters.ReachDestination"));
            Assert.False(FarWatchFencePolicy.IsPolledParameterTypeName(null));
        }

        [Fact]
        public void EachConditionFencedBothWays()
        {
            Assert.True(FarWatchFencePolicy.ShouldFencePoll(true));
            Assert.False(FarWatchFencePolicy.ShouldFencePoll(false));
            Assert.True(FarWatchFencePolicy.ShouldFenceEvent(true));
            Assert.False(FarWatchFencePolicy.ShouldFenceEvent(false));
            Assert.True(FarWatchFencePolicy.ShouldFenceReset(true));
            Assert.False(FarWatchFencePolicy.ShouldFenceReset(false));
            Assert.True(FarWatchFencePolicy.ShouldFenceRendezvous(true, false));
            Assert.True(FarWatchFencePolicy.ShouldFenceRendezvous(false, true));
            Assert.False(FarWatchFencePolicy.ShouldFenceRendezvous(false, false));
        }

        [Fact]
        public void ContractBelt_OnlyPolledClassesWhileTheDroneIsActive()
        {
            Assert.True(ContractParameterRewardCapturePatch.IsDroneCausedPolledCompletion(
                "FinePrint.Contracts.Parameters.SpecificOrbitParameter", true));
            Assert.False(ContractParameterRewardCapturePatch.IsDroneCausedPolledCompletion(
                "FinePrint.Contracts.Parameters.SpecificOrbitParameter", false));
            Assert.False(ContractParameterRewardCapturePatch.IsDroneCausedPolledCompletion(
                "Contracts.Parameters.CollectScience", true));
        }

        [Fact]
        public void Lines_AreInvariant()
        {
            Assert.Equal("fenced ReachBiome.OnUpdate vessel=4242 droneActive=True",
                FarWatchFencePolicy.FormatFenced("ReachBiome.OnUpdate", 4242u, true));
            Assert.Equal("fenced ? vessel=0 droneActive=False", FarWatchFencePolicy.FormatFenced(null, 0u, false));
        }
    }

    [Collection("Sequential")]
    public class FarWatchAwardScopeTests : IDisposable
    {
        public FarWatchAwardScopeTests()
        {
            CameraDroneIdentity.ResetForTesting();
            FarWatchAwardScope.ResetForTesting();
        }

        public void Dispose()
        {
            CameraDroneIdentity.ResetForTesting();
            FarWatchAwardScope.ResetForTesting();
        }

        [Theory]
        // thisNode, thisFrame, namesDrone, records, droneActive, attributed
        [InlineData(true, true, true, false, false, true)]
        [InlineData(true, false, true, false, false, false)]
        [InlineData(false, true, true, false, false, false)]
        [InlineData(true, true, false, false, true, false)]
        [InlineData(false, false, false, true, true, true)]
        [InlineData(false, false, false, true, false, false)]
        [InlineData(false, false, false, false, true, false)]
        public void IsDroneAttributed_Table(bool node, bool frame, bool names, bool records, bool active, bool expected)
        {
            Assert.Equal(expected, FarWatchAwardScope.IsDroneAttributed(node, frame, names, records, active));
        }

        [Fact]
        public void ShouldRefuseAward_NoDroneNoStamp_IsFalseWithoutUnity()
        {
            Assert.False(FarWatchAwardScope.ShouldRefuseAward(null, "AwardProgress", "x"));
        }
    }

    /// <summary>H5: stop, capture and resume use the recorded vessel, never the drone.</summary>
    public class FarWatchRecordedVesselFallbackTests
    {
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        public void ActiveFallback_NeverTheDrone(bool recordedFound, bool activeIsDrone, bool expected)
        {
            Assert.Equal(expected, FlightRecorder.ShouldUseActiveVesselAsRecordedFallback(recordedFound, activeIsDrone));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        public void Resume_StaysOnTheRecordedVesselWhileTheDroneIsActive(bool keep, bool activeIsDrone, bool expected)
        {
            Assert.Equal(expected, FlightRecorder.ShouldResumeOnRecordedVessel(keep, activeIsDrone));
        }
    }

    /// <summary>The FarWatch seam verb's pure half (docs/dev/design-far-watch.md 17.4, H7).</summary>
    public class TestCommandFarWatchTests
    {
        [Fact]
        public void Parse_StartByTree_DefaultFollow()
        {
            Assert.Null(TestCommandFarWatch.TryParseArgs("start", null, "abc", null, out TestCommandFarWatch.Args a));
            Assert.Equal(FarWatchOp.Start, a.Op);
            Assert.Equal("abc", a.Tree);
            Assert.Equal(-1, a.Index);
            Assert.True(a.Follow);
        }

        [Fact]
        public void Parse_StartByIndex_Hold()
        {
            Assert.Null(TestCommandFarWatch.TryParseArgs("start", "3", null, "hold", out TestCommandFarWatch.Args a));
            Assert.Equal(3, a.Index);
            Assert.False(a.Follow);
        }

        [Theory]
        [InlineData(null, null, null, null, "op-missing")]
        [InlineData("Start", null, "t", null, "op-invalid")]
        [InlineData("start", null, null, null, "target-missing")]
        [InlineData("start", "1", "t", null, "target-ambiguous")]
        [InlineData("start", "-1", null, null, "index-invalid")]
        [InlineData("start", "x", null, null, "index-invalid")]
        [InlineData("start", null, "t", "chase", "mode-invalid")]
        [InlineData("stop", null, null, "Hold", "mode-invalid")]
        public void Parse_Refusals(string op, string index, string tree, string mode, string reason)
        {
            Assert.Equal(reason, TestCommandFarWatch.TryParseArgs(op, index, tree, mode, out _));
        }

        [Fact]
        public void Parse_StopAndStatus_NeedNoTarget()
        {
            Assert.Null(TestCommandFarWatch.TryParseArgs("stop", null, null, null, out TestCommandFarWatch.Args s));
            Assert.Equal(FarWatchOp.Stop, s.Op);
            Assert.Null(TestCommandFarWatch.TryParseArgs("status", null, null, null, out TestCommandFarWatch.Args t));
            Assert.Equal(FarWatchOp.Status, t.Op);
        }

        [Fact]
        public void ResolveTarget_LowestInScopeLiveGhost_OnShipBodyWhenFollowing()
        {
            var c = new List<TestCommandFarWatch.GhostCandidate>
            {
                new TestCommandFarWatch.GhostCandidate { Index = 0, InScope = false, HasActiveGhost = true, OnShipBody = true },
                new TestCommandFarWatch.GhostCandidate { Index = 1, InScope = true, HasActiveGhost = false, OnShipBody = true },
                new TestCommandFarWatch.GhostCandidate { Index = 2, InScope = true, HasActiveGhost = true, OnShipBody = false },
                new TestCommandFarWatch.GhostCandidate { Index = 3, InScope = true, HasActiveGhost = true, OnShipBody = true },
            };
            Assert.Equal(3, TestCommandFarWatch.ResolveTargetIndex(c, true));
            Assert.Equal(2, TestCommandFarWatch.ResolveTargetIndex(c, false));
            Assert.Equal(-1, TestCommandFarWatch.ResolveTargetIndex(null, false));
            Assert.Equal(-1, TestCommandFarWatch.ResolveTargetIndex(c.GetRange(0, 2), false));
        }

        [Theory]
        [InlineData("FarWatch", null, true)]
        [InlineData("RecordingState", null, true)]
        [InlineData("FlushAndQuit", null, true)]
        [InlineData("SaveGame", "fw1-probe", true)]
        [InlineData("SaveGame", "persistent", false)]
        [InlineData("SaveGame", "Quicksave", false)]
        [InlineData("SaveGame", null, false)]
        [InlineData("LoadGame", "fw1-probe", false)]
        [InlineData("TimeJump", null, false)]
        [InlineData("WarpToUT", null, true)]
        [InlineData("EnterWatchMode", null, false)]
        [InlineData(null, null, false)]
        public void AllowedDuringFarWatch(string verb, string save, bool allowed)
        {
            Assert.Equal(allowed, TestCommandFarWatch.IsAllowedDuringFarWatch(verb, save));
        }

        [Theory]
        [InlineData("Start", "Spawning", "KeepWaiting")]
        [InlineData("Start", "HandoverOut", "KeepWaiting")]
        [InlineData("Start", "Aborted", "KeepWaiting")]
        [InlineData("Start", "Following", "CompleteOk")]
        [InlineData("Start", "Idle", "CompleteEnded")]
        [InlineData("Stop", "HandoverBack", "KeepWaiting")]
        [InlineData("Stop", "Teardown", "KeepWaiting")]
        [InlineData("Stop", "Idle", "CompleteOk")]
        [InlineData("Status", "Following", "CompleteOk")]
        public void Completion(string opName, string stateName, string expectedName)
        {
            var op = (FarWatchOp)Enum.Parse(typeof(FarWatchOp), opName);
            var state = (FarWatchState)Enum.Parse(typeof(FarWatchState), stateName);
            var expected = (FarWatchCompletionDecision)Enum.Parse(typeof(FarWatchCompletionDecision), expectedName);
            Assert.Equal(expected, TestCommandFarWatch.DecideCompletion(op, state));
        }

        [Fact]
        public void Payload_IsInvariantKeyValuePairs()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandFarWatch.BuildPayload(FarWatchState.Following, 4242u, 77u, 5, false,
                    FarWatchEndReason.None, 1234.56, FarWatchReturnCause.Danger, "return");
                Assert.Equal("state=Following dronePid=4242 shipPid=77 ghostIndex=5 mode=hold lastEnd=None"
                    + " returnUT=1234.6 returnCause=Danger warpLimit=return",
                    TestCommandFarWatch.FormatPayload(p));
                Assert.Equal("far-watch-ended:ShipLost", TestCommandFarWatch.EndedReason(FarWatchEndReason.ShipLost));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void RecordingStateFields_OnlyWhileADroneExists()
        {
            var p = new List<KeyValuePair<string, string>>();
            TestCommandFarWatch.AppendDroneFields(p, false, 4242u, 77u);
            Assert.Empty(p);
            TestCommandFarWatch.AppendDroneFields(p, true, 4242u, 77u);
            Assert.Equal("dronePid=4242 playerShipPid=77", TestCommandFarWatch.FormatPayload(p));
        }

        [Fact]
        public void Verb_IsImplementedAndStateMutating()
        {
            Assert.Contains(TestCommandFarWatch.Verb, TestCommandVerbs.ImplementedVerbNames);
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(TestCommandFarWatch.Verb));
            Assert.Equal(TestCommandFarWatch.BudgetSeconds, DeferralBudget.BudgetSeconds(TestCommandFarWatch.Verb));
        }

        [Fact]
        public void Dispatcher_RefusesMutatingVerbsWhileFarWatching()
        {
            var state = new DispatchState
            {
                Scene = TestCommandScene.Flight,
                GameLoaded = true,
                SettingsPresent = true,
                FarWatchActive = true,
            };
            var timeJump = TestCommandParser.ParseLine("id=a cmd=TimeJump ut=100", 1);
            DispatchResult r = TestCommandDispatcher.DecideDispatch(timeJump, state);
            Assert.Equal("far-watch-active", r.Reason);
            var status = TestCommandParser.ParseLine("id=b cmd=FarWatch op=status", 2);
            Assert.NotEqual("far-watch-active", TestCommandDispatcher.DecideDispatch(status, state).Reason);
        }
    }
}
