using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The far watch's step 3 review fixes (design-far-watch.md 8.2 - 8.5, 12.2): one far-start
    /// predicate for the Watch buttons and their clicks, the paced takeover and its snap-back
    /// reason, a switch during the watch that ends it before any switch gate runs, the
    /// gravity-free ghost gee, and stock's on-rails limit pinned cell by cell.
    /// </summary>
    public class FarWatchStartPredicateTests
    {
        // catches: the Watch button reading only the ship's safety verdict, so it looks
        // clickable while the start itself refuses (a kerbal on EVA, a drone already up) and
        // the click does nothing (CLAUDE.md D1: a greyed control and its block share one predicate).
        [Theory]
        [InlineData("active-is-eva")]
        [InlineData("already-active")]
        [InlineData("active-is-ghost-or-drone")]
        [InlineData("no-active-vessel")]
        [InlineData("ghost-not-resolvable")]
        [InlineData("pending-split")]
        [InlineData("refly-active")]
        public void EntryRefusal_RefusesTheStart_EvenWithASafeShip(string entry)
        {
            Assert.Equal(entry, FarWatchDecisions.CombineStartRefusal(entry, true, FarWatchRefusal.None));
            Assert.Equal(FarWatchWatchRequest.RefusedUnsafe, FarWatchDecisions.DecideWatchRequest(
                FarWatchState.Idle, false, true, false, true, 500000.0,
                FarWatchDecisions.CombineStartRefusal(entry, true, FarWatchRefusal.None) == null));
        }

        [Fact]
        public void NoEntryRefusal_ReadsTheVerdict()
        {
            Assert.Null(FarWatchDecisions.CombineStartRefusal(null, true, FarWatchRefusal.None));
            Assert.Equal("unsafe:NOT_IN_ATMOSPHERE",
                FarWatchDecisions.CombineStartRefusal(null, false, FarWatchRefusal.NotInAtmosphere));
            Assert.Equal("unsafe:ReturnTooClose",
                FarWatchDecisions.CombineStartRefusal(null, false, FarWatchRefusal.ReturnTooClose));
        }

        [Theory]
        [InlineData("active-is-eva", FarWatchDecisions.EvaHoverText)]
        [InlineData("already-active", FarWatchShipSafety.NotWhileWatchingFarHoverText)]
        [InlineData("active-is-ghost-or-drone", FarWatchDecisions.NoShipHoverText)]
        [InlineData("no-active-vessel", FarWatchDecisions.NoShipHoverText)]
        [InlineData("not-flight", FarWatchDecisions.NoShipHoverText)]
        [InlineData("refly-active", FarWatchShipSafety.ReFlyHoverText)]
        [InlineData("pending-dock-merge", FarWatchShipSafety.BusyHoverText)]
        [InlineData("time-jump-placement-armed", FarWatchShipSafety.BusyHoverText)]
        [InlineData("restoring-active-tree", FarWatchShipSafety.BusyHoverText)]
        [InlineData("ghost-not-resolvable", null)]
        public void EntryRefusal_HoverNamesItsOwnReason(string entry, string expected)
        {
            Assert.Equal(expected, FarWatchDecisions.StartRefusalHover(entry, FarWatchRefusal.None, null));
        }

        [Fact]
        public void VerdictRefusal_HoverIsStocksOrTheSafetySentence()
        {
            Assert.Equal("stock text", FarWatchDecisions.StartRefusalHover(null, FarWatchRefusal.NotInAtmosphere, "stock text"));
            Assert.Equal(FarWatchShipSafety.SafetyHoverText, FarWatchDecisions.StartRefusalHover(null, FarWatchRefusal.ReturnTooClose, null));
            Assert.Equal("why", FarWatchDecisions.WatchRequestHover(FarWatchWatchRequest.RefusedUnsafe, "why"));
            Assert.Null(FarWatchDecisions.WatchRequestHover(FarWatchWatchRequest.RefusedNoReading, "why"));
            Assert.Null(FarWatchDecisions.WatchRequestHover(FarWatchWatchRequest.FarWatchDirect, "why"));
        }

        // catches: the button and the click drifting apart again: both must ask the one W
        // decision, and the start must read the one far-start predicate.
        [Fact]
        public void ButtonAndClick_ReadTheOneDecision()
        {
            string flight = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "ParsekFlight.cs"));
            string canFar = FarWatchSource.Body(flight, "internal bool CanFarWatch(");
            string enter = FarWatchSource.Body(flight, "internal void EnterWatchMode(int index)");
            Assert.Contains("DecideFarWatchRequest(index, forButton: true", canFar);
            Assert.Contains("DecideFarWatchRequest(index, forButton: false", enter);
            Assert.DoesNotContain("EvaluateAdmission(", canFar);
            Assert.DoesNotContain("EnterWatchModeForFarWatch(", enter.Substring(0, enter.IndexOf("FollowInFarWatch", StringComparison.Ordinal)));

            string fw = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.cs"));
            Assert.Contains("EvaluateFarStart(recordingIndex, follow, \"start\"", FarWatchSource.Body(fw, "internal bool TryStart("));
            Assert.Contains("EvaluateFarStart(recordingIndex, true, trigger", FarWatchSource.Body(fw, "internal FarWatchWatchRequest DecideWatchRequest("));
            Assert.Contains("EvaluateFarStart(recordingIndex, true, \"takeover\"", FarWatchSource.Body(fw, "internal void CheckTakeover("));
        }

        // catches: the safety check calling FlightGlobals.ClearToSave(), whose logMsg overload
        // posts stock's refusal as a screen message, from the Watch buttons' poll.
        [Fact]
        public void Admission_ReadsClearToSaveWithoutItsScreenMessage()
        {
            string fw = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.cs"));
            Assert.DoesNotContain("ClearToSave()", fw);
            Assert.Contains("FlightGlobals.ClearToSave(false)", FarWatchSource.Body(fw, "internal FarWatchSafetyVerdict EvaluateAdmission("));
        }

        [Fact]
        public void NewHovers_FitTheTimelineStrip()
        {
            int budget = TooltipEchoBudgetTests.BudgetChars(TimelineWindowUI.DefaultWindowWidth, TooltipEchoBox.SingleLine);
            foreach (string tip in new[]
            {
                FarWatchDecisions.SettlingHoverText, FarWatchDecisions.EvaHoverText, FarWatchDecisions.NoShipHoverText,
            })
            {
                Assert.DoesNotContain("\n", tip);
                Assert.True(tip.Length <= budget, tip.Length + " > " + budget + ": " + tip);
            }
        }
    }

    public class FarWatchTakeoverGateTests
    {
        // catches: the takeover retried every frame at 290 km and beyond when an entry refusal
        // (which never reaches the safety check) refused it, so "takeover requested" and
        // "start refused" logged every frame.
        [Fact]
        public void EveryRefusal_PacesTheNextAttemptToOncePerSecond()
        {
            var g = new FarWatchTakeoverGate();
            Assert.True(g.ShouldAttempt("rec", 10.0));
            Assert.True(g.IsFirstAttempt("rec"));
            Assert.True(g.RecordRefusal("rec", 10.0, "active-is-eva", FarWatchDecisions.EvaHoverText));
            Assert.False(g.ShouldAttempt("rec", 10.02));
            Assert.False(g.ShouldAttempt("rec", 10.99));
            Assert.True(g.ShouldAttempt("rec", 11.0));
            Assert.False(g.IsFirstAttempt("rec"));
            // Another watch is never paced by this one's refusal.
            Assert.True(g.ShouldAttempt("other", 10.02));
        }

        [Fact]
        public void RefusalLog_IsAtOnceOnlyWhenTheReasonChanges()
        {
            var g = new FarWatchTakeoverGate();
            Assert.True(g.RecordRefusal("rec", 1.0, "pending-split", FarWatchShipSafety.BusyHoverText));
            Assert.False(g.RecordRefusal("rec", 2.0, "pending-split", FarWatchShipSafety.BusyHoverText));
            Assert.True(g.RecordRefusal("rec", 3.0, "unsafe:ReturnTooClose", FarWatchShipSafety.SafetyHoverText));
            Assert.True(g.RecordRefusal("rec2", 4.0, "unsafe:ReturnTooClose", FarWatchShipSafety.SafetyHoverText));
        }

        // catches: the snap-back naming the ship-orbit sentence for a refusal that is not about
        // the ship's orbit (an EVA kerbal), and a second message for the same event.
        [Fact]
        public void SnapBack_NamesTheRefusalsOwnReason_Once()
        {
            var g = new FarWatchTakeoverGate();
            g.RecordRefusal("rec", 1.0, "active-is-eva",
                FarWatchDecisions.StartRefusalHover("active-is-eva", FarWatchRefusal.None, null));
            Assert.Null(g.TakeSnapBackMessage("other"));
            string msg = g.TakeSnapBackMessage("rec");
            Assert.Equal("Watch ended at 300 km: " + FarWatchDecisions.EvaHoverText, msg);
            Assert.DoesNotContain(FarWatchShipSafety.SafetyHoverText, msg);
            Assert.Null(g.TakeSnapBackMessage("rec"));
            Assert.True(g.ShouldAttempt("rec", 1.01));
        }

        [Fact]
        public void Clear_ForgetsTheRefusal()
        {
            var g = new FarWatchTakeoverGate();
            g.RecordRefusal("rec", 1.0, "x", "y");
            g.Clear();
            Assert.True(g.ShouldAttempt("rec", 1.01));
            Assert.Null(g.TakeSnapBackMessage("rec"));
        }
    }

    public class FarWatchSwitchRouteTests
    {
        [Fact]
        public void SwitchDuringAFarWatch_EndsTheWatchFirst()
        {
            Assert.Equal(FarWatchSwitchRoute.EndWatchThenReplay, FarWatchDecisions.DecideOwnedVesselSwitch(true));
            Assert.Equal(FarWatchSwitchRoute.RunGates, FarWatchDecisions.DecideOwnedVesselSwitch(false));
        }

        // catches: the switch after the hand-back sent as a bare SetActiveVessel, which skips
        // the Re-Fly refusal, the pre-switch Merge / Discard dialog and the intent arm.
        [Theory]
        [InlineData(false, false, false, false, (int)FarWatchPostSwitch.None)]
        [InlineData(true, false, false, true, (int)FarWatchPostSwitch.DropTargetGoneOrActive)]
        [InlineData(true, true, true, true, (int)FarWatchPostSwitch.DropTargetGoneOrActive)]
        [InlineData(true, true, false, false, (int)FarWatchPostSwitch.DropNoGatedPath)]
        [InlineData(true, true, false, true, (int)FarWatchPostSwitch.ReplayThroughGates)]
        public void PostTeardownSwitch_ReplaysThroughTheGates(bool hasTarget, bool alive, bool active, bool gated, int expected)
        {
            Assert.Equal((FarWatchPostSwitch)expected, FarWatchDecisions.DecidePostTeardownSwitch(hasTarget, alive, active, gated));
        }

        // catches: the Re-Fly refusal or the pre-switch dialog (Case B on an unloaded target)
        // running while the drone has focus, before the token patch refuses the switch.
        [Fact]
        public void SwitchToGates_DeferBeforeAnyGateRuns()
        {
            string map = FarWatchSource.Strip(FarWatchSource.Read("Patches", "MapFocusObjectOnSelectPatch.cs"));
            string prep = FarWatchSource.Body(map, "internal static bool PrepareOwnedVesselSwitch(");
            int defer = prep.IndexOf("TryDeferSwitchUntilFarWatchEnds(", StringComparison.Ordinal);
            int refly = prep.IndexOf("ReFlySwitchGate.IsRefused(", StringComparison.Ordinal);
            int dialog = prep.IndexOf("TryTakePreSwitch(", StringComparison.Ordinal);
            int arm = prep.IndexOf("ArmStockActionIntent(", StringComparison.Ordinal);
            Assert.True(defer >= 0 && refly > defer && dialog > defer && arm > defer,
                "defer=" + defer + " refly=" + refly + " dialog=" + dialog + " arm=" + arm);
            string replay = FarWatchSource.Body(map, "internal static void ReplayOwnedVesselSwitch(");
            Assert.True(replay.IndexOf("PrepareOwnedVesselSwitch(", StringComparison.Ordinal)
                < replay.IndexOf("FlightGlobals.SetActiveVessel(", StringComparison.Ordinal));

            string warp = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.Warp.cs"));
            Assert.DoesNotContain("SetActiveVessel(", warp);
            string drone = FarWatchSource.Strip(FarWatchSource.Read("Patches", "CameraDronePatches.cs"));
            Assert.Contains("MapFocusObjectOnSelectPatch.ReplayOwnedVesselSwitch(", drone);
        }
    }

    public class FarWatchGhostGeeTests
    {
        private const double Mu = FarWatchShipSafetyTests.KerbinMu;

        private static void Circular(double radius, double angle, out Vector3d r, out Vector3d v)
        {
            double speed = Math.Sqrt(Mu / radius);
            r = new Vector3d(radius * Math.Cos(angle), radius * Math.Sin(angle), 0.0);
            v = new Vector3d(-speed * Math.Sin(angle), speed * Math.Cos(angle), 0.0);
        }

        // catches: the one-window velocity difference minus the END point's gravity, which reads
        // the gravity vector's turn over a long window (high warp) as felt g and trips stock's
        // 0.1 g limit on a ghost that only coasts.
        [Theory]
        [InlineData(1.0)]
        [InlineData(200.0)]
        [InlineData(500.0)]
        public void Coast_ReadsNoGee_HoweverLongTheWindow(double dt)
        {
            double radius = FarWatchShipSafetyTests.KerbinR + 100000.0;
            double n = Math.Sqrt(Mu / (radius * radius * radius));
            Vector3d r0, v0, r1, v1;
            Circular(radius, 0.0, out r0, out v0);
            Circular(radius, n * dt, out r1, out v1);
            Assert.True(FarWatchWarpLimits.NonGravitationalGee(r0, v0, r1, v1, Mu, dt) < 1e-6);
            // The finite difference this replaced reads the gravity's turn as g at 200 s.
            Vector3d accel = (v1 - v0) / dt;
            double g = Mu / r1.sqrMagnitude;
            double old = (accel + r1.normalized * g).magnitude / 9.80665;
            if (dt >= 200.0)
                Assert.True(old > 0.1, "old reading " + old);
        }

        [Fact]
        public void RotatedFrame_ChangesNothing()
        {
            double radius = FarWatchShipSafetyTests.KerbinR + 100000.0;
            Vector3d r0, v0, r1, v1;
            Circular(radius, 0.0, out r0, out v0);
            Circular(radius, 0.3, out r1, out v1);
            // The second sample in a frame turned 10 degrees about z (KSP's inverse rotation).
            double c = Math.Cos(0.17), s = Math.Sin(0.17);
            Vector3d rr = new Vector3d(c * r1.x - s * r1.y, s * r1.x + c * r1.y, r1.z);
            Vector3d vr = new Vector3d(c * v1.x - s * v1.y, s * v1.x + c * v1.y, v1.z);
            Assert.True(FarWatchWarpLimits.NonGravitationalGee(r0, v0, rr, vr, Mu, 100.0) < 1e-6);
        }

        [Fact]
        public void ProgradeBurn_ReadsItsAcceleration()
        {
            double radius = FarWatchShipSafetyTests.KerbinR + 100000.0;
            Vector3d r0, v0;
            Circular(radius, 0.0, out r0, out v0);
            // 1 g along the velocity for 1 s (the position change of one second is negligible).
            Vector3d v1 = v0 + v0.normalized * 9.80665;
            double gee = FarWatchWarpLimits.NonGravitationalGee(r0, v0, r0, v1, Mu, 1.0);
            Assert.InRange(gee, 0.95, 1.05);
            Assert.Equal(0.0, FarWatchWarpLimits.NonGravitationalGee(r0, v0, r0, v1, Mu, 0.0));
            Assert.Equal(0.0, FarWatchWarpLimits.NonGravitationalGee(Vector3d.zero, v0, r0, v1, Mu, 1.0));
        }
    }

    /// <summary>
    /// Stock's <c>TimeWarp.getMaxOnRailsRateIdx</c> (decompiled 1.12.5) cell by cell over
    /// <see cref="FarWatchWarpLimits.EvaluateStock"/>: the ladder, landed, atmosphere and
    /// acceleration returns in stock's order, <c>GetMaxRateForAltitude</c> per body, and the
    /// look-ahead walk-downs, each at its boundary so a moved comparison reds.
    /// </summary>
    public class FarWatchStockWarpTableTests
    {
        private static readonly float[] Rates = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f };
        private static readonly float[] KerbinLimits = { 0f, 70000f, 70000f, 70000f, 120000f, 240000f, 480000f, 600000f };
        private static readonly float[] MunLimits = { 0f, 5000f, 5000f, 5000f, 5000f, 7000f, 10000f, 100000f };

        private static FarWatchWarpInputs Kerbin(double altitude, TimeWarp.MaxRailsRateMode mode = TimeWarp.MaxRailsRateMode.VesselAltitude)
        {
            return new FarWatchWarpInputs
            {
                TargetRateIndex = 7, LookAhead = false, Mode = mode, PeModeSurfaceMargin = 2000.0,
                WarpRates = Rates, AltitudeLimits = KerbinLimits, MaxPhysicsRateIndex = 0,
                FixedDeltaTime = 0.02, GThreshold = 0.1, Altitude = altitude,
                HasAtmosphere = true, AtmosphereDepth = 70000.0, HasSolidSurface = true,
                PqsMaxHeight = 6767.0, PqsMeshVertMax = 6500.0, BodyRadius = FarWatchShipSafetyTests.KerbinR,
            };
        }

        private static FarWatchWarpInputs Mun(double altitude)
        {
            FarWatchWarpInputs i = Kerbin(altitude);
            i.AltitudeLimits = MunLimits;
            i.HasAtmosphere = false;
            i.AtmosphereDepth = 0.0;
            i.PqsMaxHeight = 7061.0;
            i.PqsMeshVertMax = 7000.0;
            i.BodyRadius = 200000.0;
            return i;
        }

        private static void Expect(FarWatchWarpInputs i, int rate, ClearToSaveStatus reason)
        {
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.Equal(rate, r.RateIndex);
            Assert.Equal(reason, r.Reason);
        }

        // GetMaxRateForAltitude: the first index whose limit is at or above the altitude, less one.
        [Theory]
        [InlineData(70000.0, 0)]
        [InlineData(70001.0, 3)]
        [InlineData(120000.0, 3)]
        [InlineData(120001.0, 4)]
        [InlineData(240001.0, 5)]
        [InlineData(480000.0, 5)]
        [InlineData(599999.0, 6)]
        public void Kerbin_VesselAltitude_CapsAtTheBodysLimits(double altitude, int rate)
        {
            Expect(Kerbin(altitude), rate, ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH);
        }

        [Fact]
        public void Kerbin_AtTheTopLimit_KeepsTheRequest()
        {
            Expect(Kerbin(600000.0), 7, ClearToSaveStatus.CLEAR);
            FarWatchWarpInputs i = Kerbin(240001.0);
            i.TargetRateIndex = 5;
            Expect(i, 5, ClearToSaveStatus.CLEAR);
        }

        [Theory]
        [InlineData(5001.0, 4)]
        [InlineData(7000.0, 4)]
        [InlineData(7001.0, 5)]
        [InlineData(10001.0, 6)]
        public void Mun_VesselAltitude_CapsAtItsOwnLimits(double altitude, int rate)
        {
            Expect(Mun(altitude), rate, ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH);
        }

        [Fact]
        public void Airless_LowAltitude_IsNeverInAtmosphere()
        {
            Expect(Mun(3000.0), 0, ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH);
            Expect(Mun(100000.0), 7, ClearToSaveStatus.CLEAR);
        }

        [Fact]
        public void Atmosphere_IsBelowItsTop_AndComesBeforeAcceleration()
        {
            Expect(Kerbin(69999.0), 0, ClearToSaveStatus.NOT_IN_ATMOSPHERE);
            FarWatchWarpInputs i = Kerbin(69999.0);
            i.GeeForce = 5.0;
            Expect(i, 0, ClearToSaveStatus.NOT_IN_ATMOSPHERE);
            // At the top itself stock's pressure is 0: the altitude cap applies instead.
            Expect(Kerbin(70000.0, TimeWarp.MaxRailsRateMode.PeAltitude), 7, ClearToSaveStatus.CLEAR);
        }

        [Theory]
        [InlineData(0.1, 3, ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH)]
        [InlineData(0.1001, 0, ClearToSaveStatus.NOT_UNDER_ACCELERATION)]
        public void Acceleration_IsStrictlyAboveTheThreshold(double gee, int rate, ClearToSaveStatus reason)
        {
            FarWatchWarpInputs i = Kerbin(100000.0);
            i.GeeForce = gee;
            Expect(i, rate, reason);
        }

        [Theory]
        [InlineData(0.08, 7, ClearToSaveStatus.CLEAR)]
        [InlineData(0.09, 7, ClearToSaveStatus.CLEAR)]
        [InlineData(0.1, 0, ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE)]
        public void Landed_SlidesAboveStocksSpeed_ElseKeepsTheRequest(double srfSpeedSq, int rate, ClearToSaveStatus reason)
        {
            FarWatchWarpInputs i = Kerbin(70.0);
            i.LandedOrSplashed = true;
            i.SrfSpeedSq = srfSpeedSq;
            i.GeeForce = 5.0; // stock never reads gee for a landed vessel
            Expect(i, rate, reason);
        }

        [Fact]
        public void Ladder_ComesFirst_AndNoRequestIsNeverLimited()
        {
            FarWatchWarpInputs i = Kerbin(70.0);
            i.LandedOrSplashed = true;
            i.AnyLoadedEvaOnLadder = true;
            Expect(i, 0, ClearToSaveStatus.NOT_WHILE_ON_A_LADDER);
            FarWatchWarpInputs none = Kerbin(30000.0);
            none.TargetRateIndex = 0;
            none.AnyLoadedEvaOnLadder = true;
            Expect(none, 0, ClearToSaveStatus.CLEAR);
        }

        [Fact]
        public void MinPeAllowed_IsTheHighestOfTerrainAirAndMesh()
        {
            Assert.Equal(70000.0, FarWatchWarpLimits.EvaluateStock(Kerbin(700000.0)).MinPeAllowed);
            Assert.Equal(7061.0, FarWatchWarpLimits.EvaluateStock(Mun(100000.0)).MinPeAllowed);
            FarWatchWarpInputs mesh = Mun(100000.0);
            mesh.PqsMeshVertMax = 8000.0;
            Assert.Equal(8000.0, FarWatchWarpLimits.EvaluateStock(mesh).MinPeAllowed);
        }

        // An ellipse 100 km below / above Kerbin's surface, at apoapsis: index 7's step (2000 s)
        // passes the periapsis (777 s) and reads its altitude; index 6's (200 s) is in air; the
        // PeAltitude walk then steps down while the look-ahead altitude is under the limit.
        [Fact]
        public void LookAhead_WalksDownThroughAirThenTheLimits()
        {
            FarWatchWarpInputs i = Kerbin(100000.0, TimeWarp.MaxRailsRateMode.PeAltitude);
            i.LookAhead = true;
            i.HasConic = true;
            i.Conic = FarWatchShipSafetyTests.Ellipse(FarWatchShipSafetyTests.KerbinR - 100000.0, FarWatchShipSafetyTests.KerbinR + 100000.0);
            Expect(i, 3, ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH);
            // Without the look-ahead stock reads only the current altitude (clear in PeAltitude mode).
            i.LookAhead = false;
            Expect(i, 7, ClearToSaveStatus.CLEAR);
        }

        [Fact]
        public void LookAhead_PeriapsisAboveTheMargin_SkipsTheLimitWalk()
        {
            FarWatchWarpInputs i = Kerbin(100000.0, TimeWarp.MaxRailsRateMode.PeAltitude);
            i.LookAhead = true;
            i.HasConic = true;
            // Periapsis 72 001 m: above 70 km + the 2 km margin; the look-ahead stays in vacuum.
            i.Conic = FarWatchShipSafetyTests.Ellipse(FarWatchShipSafetyTests.KerbinR + 72001.0, FarWatchShipSafetyTests.KerbinR + 100000.0);
            Expect(i, 7, ClearToSaveStatus.CLEAR);
        }
    }

    public class FarWatchSafetyRuleCellTests
    {
        private static FarWatchSafetyInputs Safe() =>
            FarWatchShipSafetyTests.Inputs(FarWatchShipSafetyTests.Path(FarWatchPathEnding.Final, double.NaN,
                FarWatchShipSafetyTests.Circular(700000.0)));

        [Fact]
        public void LandedShip_WalksNoPath()
        {
            FarWatchSafetyInputs i = FarWatchShipSafetyTests.Inputs(
                FarWatchShipSafetyTests.Path(FarWatchPathEnding.Failed, double.NaN), landed: true);
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
            i.Ship.Path = null;
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
            i.Ship.LandedOrSplashed = false;
            Assert.Equal(FarWatchRefusal.PathUnknown, FarWatchShipSafety.Evaluate(i).Reason);
        }

        [Fact]
        public void StockRefusal_ComesBeforeDockingCapture_WhichNamesTheShip()
        {
            FarWatchSafetyInputs i = Safe();
            i.Ship.DockingCaptureActive = true;
            i.ClearToSave = ClearToSaveStatus.NOT_UNDER_ACCELERATION;
            Assert.Equal(FarWatchRefusal.NotUnderAcceleration, FarWatchShipSafety.Evaluate(i).Reason);
            i.ClearToSave = ClearToSaveStatus.CLEAR;
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchRefusal.DockingCapture, v.Reason);
            Assert.Equal(1u, v.RefusedByPid);
        }

        [Fact]
        public void Exposed_LandedIsSkipped_UncrewedProbeInAirRefuses()
        {
            var falling = FarWatchShipSafetyTests.Path(FarWatchPathEnding.Impact, 900.0,
                FarWatchShipSafetyTests.Ellipse(500000.0, 690000.0));
            FarWatchSafetyInputs i = Safe();
            i.Exposed.Add(new FarWatchSafetyVessel { Pid = 9, Crewed = true, LandedOrSplashed = true, Path = falling });
            FarWatchSafetyVerdict landed = FarWatchShipSafety.Evaluate(i);
            Assert.True(landed.IsAllowed);
            Assert.Equal(0, landed.SpentExposedAllowed);
            i.Exposed[0] = new FarWatchSafetyVessel { Pid = 10, Crewed = false, Controllable = true, Path = falling };
            FarWatchSafetyVerdict probe = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchRefusal.CrewedShipNearbyInAir, probe.Reason);
            Assert.Equal(10u, probe.RefusedByPid);
        }

        [Fact]
        public void SlideSpeed_IsStrictlyAboveTheLimit_AndAfterDockingCapture()
        {
            FarWatchSafetyInputs i = FarWatchShipSafetyTests.Inputs(null, landed: true);
            i.Ship.SrfSpeed = FarWatchShipSafety.LandedSlideSpeed;
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
            i.Ship.SrfSpeed = 1.0;
            i.Ship.DockingCaptureActive = true;
            Assert.Equal(FarWatchRefusal.DockingCapture, FarWatchShipSafety.Evaluate(i).Reason);
        }

        [Fact]
        public void ReturnLeads_AndTheMinimumReturnTime_AtTheirBoundaries()
        {
            FarWatchSafetyInputs i = Safe();
            // Node lead 180 s: a node at 300 s returns at 120 s, exactly the minimum.
            i.ShipNodeUTs.Add(300.0);
            FarWatchSafetyVerdict ok = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchVerdictKind.AllowedWithReturn, ok.Kind);
            Assert.Equal(120.0, ok.ReturnUT);
            i.ShipNodeUTs[0] = 299.9;
            Assert.Equal(FarWatchRefusal.ReturnTooClose, FarWatchShipSafety.Evaluate(i).Reason);
            // A node at now counts; an alarm later than the node's return does not replace it.
            i.ShipNodeUTs[0] = 1000.0;
            i.ShipAlarmUTs.Add(900.0);
            FarWatchSafetyVerdict node = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(820.0, node.ReturnUT);
            Assert.Equal(FarWatchReturnCause.ManeuverNode, node.ReturnCause);
            i.ShipAlarmUTs.Clear();
            i.ShipNodeUTs[0] = 0.0;
            Assert.Equal(FarWatchRefusal.ReturnTooClose, FarWatchShipSafety.Evaluate(i).Reason);
        }

        [Fact]
        public void DangerBeforeANode_IsTheReturnCause()
        {
            FarWatchSafetyInputs i = FarWatchShipSafetyTests.Inputs(FarWatchShipSafetyTests.Path(FarWatchPathEnding.Impact, 1000.0));
            i.ShipNodeUTs.Add(5000.0);
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(700.0, v.ReturnUT);
            Assert.Equal(FarWatchReturnCause.Danger, v.ReturnCause);
        }
    }

    [Collection("Sequential")]
    public class FarWatchAltimeterAndMissionsTests : IDisposable
    {
        private readonly System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();

        public FarWatchAltimeterAndMissionsTests()
        {
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // catches: the altimeter's Space Center button left unlocked while the drone holds
        // focus (stock unlocks it for any vessel clear to save), and a click that leaves the
        // flight from the drone.
        [Fact]
        public void Altimeter_IsHeldLockedAndItsClickRefused_WhileAFarWatchRuns()
        {
            Assert.Equal(0, FarWatchAltimeterBlock.GreyedUnlockState(1, true));
            Assert.Equal(0, FarWatchAltimeterBlock.GreyedUnlockState(2, true));
            Assert.Equal(1, FarWatchAltimeterBlock.GreyedUnlockState(1, false));
            Assert.Equal(2, FarWatchAltimeterBlock.GreyedUnlockState(2, false));
            Assert.True(FarWatchAltimeterBlock.RunStockClick("returnToSpaceCenter", false));
            Assert.False(FarWatchAltimeterBlock.RunStockClick("returnToSpaceCenter", true));
            Assert.Contains(lines, l => l.Contains("[FarWatch]") && l.Contains("altimeter returnToSpaceCenter refused during the far watch"));
        }

        // catches: the greyed state and the click-block reading different predicates (D1).
        [Fact]
        public void Altimeter_GreyAndBlock_ReadTheOnePredicate()
        {
            string code = FarWatchSource.Strip(FarWatchSource.Read("Patches", "FarWatchAltimeterPatch.cs"));
            Assert.Contains("[HarmonyPatch(typeof(AltimeterSliderButtons), \"setUnlock\")]", code);
            Assert.Contains("[HarmonyPatch(typeof(AltimeterSliderButtons), \"returnToSpaceCenter\")]", code);
            Assert.Contains("[HarmonyPatch(typeof(AltimeterSliderButtons), \"recoverVessel\")]", code);
            Assert.Equal(3, Regex.Matches(code, @"FarWatchAltimeterBlock\.HoldsTheFlightExits\(\)").Count);
        }

        // catches: the Missions Watch button greyed by a far-watch refusal while its hover says
        // nothing of this mission is flying.
        [Fact]
        public void MissionsWatch_GreyedByTheFarWatch_SaysWhy()
        {
            Assert.Equal(FarWatchDecisions.EvaHoverText,
                MissionsWindowUI.MissionWatchDisabledReason(true, false, FarWatchDecisions.EvaHoverText));
            Assert.Equal("Nothing from this mission is flying right now", MissionsWindowUI.MissionWatchDisabledReason(true, false, null));
            Assert.Equal(string.Empty, MissionsWindowUI.MissionWatchDisabledReason(true, true, FarWatchDecisions.EvaHoverText));
            Assert.Equal("Watching only works while you are flying",
                MissionsWindowUI.MissionWatchDisabledReason(false, false, FarWatchDecisions.EvaHoverText));
        }
    }

    internal static class FarWatchSource
    {
        internal static string Read(params string[] parts)
        {
            string dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "scripts")) && Directory.Exists(Path.Combine(dir, "Source")))
                    return File.ReadAllText(Path.Combine(new[] { dir, "Source", "Parsek" }.Concat(parts).ToArray()));
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }

        internal static string Strip(string src)
        {
            src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", "");
        }

        /// <summary>The brace-matched body of the member whose declaration starts with <paramref name="signature"/>.</summary>
        internal static string Body(string code, string signature)
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + signature);
            int open = code.IndexOf('{', at);
            int depth = 0;
            for (int k = open; k < code.Length; k++)
            {
                if (code[k] == '{') depth++;
                else if (code[k] == '}' && --depth == 0)
                    return code.Substring(open, k - open + 1);
            }
            throw new InvalidOperationException("unbalanced: " + signature);
        }
    }
}
