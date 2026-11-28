using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Step 3 of the far watch (design-far-watch.md 8.1 - 8.5, 12.2, 17.1): the ship-safety
    /// rules, the radius-only conic math, the takeover / hand-back / W decisions, the stock warp
    /// transcription and Parsek's ceiling, and the hover strings' budgets.
    /// </summary>
    public class FarWatchShipSafetyTests
    {
        internal const double KerbinMu = 3.5316e12;
        internal const double KerbinR = 600000.0;

        internal static FarWatchSafetyBody Kerbin => new FarWatchSafetyBody
        {
            Name = "Kerbin", Radius = KerbinR, AtmosphereDepth = 70000.0, MaxTerrainHeight = 6767.0,
        };

        internal static FarWatchConic Circular(double radius, double startUT = 0.0, double endUT = double.PositiveInfinity)
        {
            return new FarWatchConic
            {
                BodyName = "Kerbin", SemiMajorAxis = radius, Eccentricity = 0.0, GravParameter = KerbinMu,
                MeanAnomalyAtEpoch = 0.0, Epoch = 0.0, StartUT = startUT, EndUT = endUT,
            };
        }

        /// <summary>An ellipse between <paramref name="pe"/> and <paramref name="ap"/> radii, at apoapsis at UT 0.</summary>
        internal static FarWatchConic Ellipse(double pe, double ap)
        {
            double a = (pe + ap) / 2.0;
            return new FarWatchConic
            {
                BodyName = "Kerbin", SemiMajorAxis = a, Eccentricity = (ap - pe) / (ap + pe), GravParameter = KerbinMu,
                MeanAnomalyAtEpoch = Math.PI, Epoch = 0.0, StartUT = 0.0, EndUT = double.PositiveInfinity,
            };
        }

        internal static FarWatchSafetyInputs Inputs(FarWatchSafetyPath path, bool landed = false)
        {
            var i = new FarWatchSafetyInputs
            {
                NowUT = 0.0,
                Ship = new FarWatchSafetyVessel { Pid = 1, Name = "Ship", LandedOrSplashed = landed, Path = path },
            };
            i.Bodies["Kerbin"] = Kerbin;
            return i;
        }

        internal static FarWatchSafetyPath Path(FarWatchPathEnding ending, double endUT, params FarWatchConic[] segs)
        {
            var p = new FarWatchSafetyPath { Ending = ending, EndUT = endUT };
            p.Segments.AddRange(segs);
            return p;
        }

        private static FarWatchSafetyPath SafeOrbit() => Path(FarWatchPathEnding.Final, double.NaN, Circular(700000.0));

        [Fact]
        public void SafeAltitude_IsTheHigherOfAirAndTerrainPlusOneKm()
        {
            Assert.Equal(71000.0, FarWatchShipSafety.SafeAltitude(Kerbin));
            var mun = new FarWatchSafetyBody { Name = "Mun", Radius = 200000.0, AtmosphereDepth = 0.0, MaxTerrainHeight = 7061.0 };
            Assert.Equal(8061.0, FarWatchShipSafety.SafeAltitude(mun));
        }

        [Fact]
        public void LandedStillShip_IsAllowedWithNoReturn()
        {
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(null, landed: true));
            Assert.Equal(FarWatchVerdictKind.Allowed, v.Kind);
            Assert.True(double.IsPositiveInfinity(v.DangerUT));
        }

        [Fact]
        public void LandedSliding_IsRefusedAboveStockSpeed()
        {
            FarWatchSafetyInputs i = Inputs(null, landed: true);
            i.Ship.SrfSpeed = 0.31;
            Assert.Equal(FarWatchRefusal.NotWhileMovingOverSurface, FarWatchShipSafety.Evaluate(i).Reason);
            i.Ship.SrfSpeed = 0.3;
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
        }

        [Fact]
        public void RuleOrder_ReFlyThenBusyThenPathThenStock()
        {
            FarWatchSafetyInputs i = Inputs(Path(FarWatchPathEnding.Failed, double.NaN));
            i.ReFlyActive = true;
            i.PendingWindow = "pending-split";
            i.ClearToSave = ClearToSaveStatus.NOT_IN_ATMOSPHERE;
            Assert.Equal(FarWatchRefusal.ReFly, FarWatchShipSafety.Evaluate(i).Reason);
            i.ReFlyActive = false;
            Assert.Equal(FarWatchRefusal.Busy, FarWatchShipSafety.Evaluate(i).Reason);
            i.PendingWindow = null;
            Assert.Equal(FarWatchRefusal.PathUnknown, FarWatchShipSafety.Evaluate(i).Reason);
            i.Ship.Path = SafeOrbit();
            Assert.Equal(FarWatchRefusal.NotInAtmosphere, FarWatchShipSafety.Evaluate(i).Reason);
            i.ClearToSave = ClearToSaveStatus.CLEAR;
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
        }

        [Theory]
        [InlineData(ClearToSaveStatus.NOT_UNDER_ACCELERATION, "NotUnderAcceleration")]
        [InlineData(ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH, "NotWhileAboutToCrash")]
        [InlineData(ClearToSaveStatus.NOT_WHILE_ON_A_LADDER, "NotWhileOnALadder")]
        [InlineData(ClearToSaveStatus.NOT_WHILE_THROTTLED_UP, "NotWhileThrottledUp")]
        [InlineData(ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE, "NotWhileMovingOverSurface")]
        public void StockClearToSave_RefusesWithStocksReason(ClearToSaveStatus s, string expectedName)
        {
            FarWatchRefusal expected = (FarWatchRefusal)Enum.Parse(typeof(FarWatchRefusal), expectedName);
            FarWatchSafetyInputs i = Inputs(SafeOrbit());
            i.ClearToSave = s;
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(expected, v.Reason);
            Assert.NotNull(FarWatchShipSafety.StockReasonKey(v.Reason));
            Assert.Equal("stock text", FarWatchShipSafety.HoverText(v.Reason, "stock text"));
        }

        [Fact]
        public void DockingCapture_OnShipOrExposed_Refuses()
        {
            FarWatchSafetyInputs i = Inputs(SafeOrbit());
            i.Exposed.Add(new FarWatchSafetyVessel { Pid = 7, DockingCaptureActive = true, Path = SafeOrbit() });
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchRefusal.DockingCapture, v.Reason);
            Assert.Equal(7u, v.RefusedByPid);
        }

        [Fact]
        public void ExposedCrewedInAir_Refuses_SpentStageIsAllowed()
        {
            var falling = Path(FarWatchPathEnding.Impact, 900.0, Ellipse(500000.0, 690000.0));
            FarWatchSafetyInputs i = Inputs(SafeOrbit());
            i.Exposed.Add(new FarWatchSafetyVessel { Pid = 8, Crewed = false, Controllable = false, Path = falling });
            FarWatchSafetyVerdict spent = FarWatchShipSafety.Evaluate(i);
            Assert.True(spent.IsAllowed);
            Assert.Equal(1, spent.SpentExposedAllowed);

            i.Exposed[0].Crewed = true;
            FarWatchSafetyVerdict crewed = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchRefusal.CrewedShipNearbyInAir, crewed.Reason);
            Assert.Equal(8u, crewed.RefusedByPid);

            // A crewed exposed vessel in a safe orbit is not in air.
            i.Exposed[0].Path = SafeOrbit();
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
        }

        [Fact]
        public void ClosedSafeOrbit_Final_HasNoDanger()
        {
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(SafeOrbit()));
            Assert.Equal(FarWatchVerdictKind.Allowed, v.Kind);
            Assert.True(double.IsPositiveInfinity(v.DangerUT));
        }

        [Fact]
        public void SafeRadiusBoundary_JustAboveIsSafe_JustBelowIsDanger()
        {
            double hSafeR = KerbinR + 71000.0;
            Assert.True(FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Final, double.NaN, Circular(hSafeR + 1.0)))).Kind
                == FarWatchVerdictKind.Allowed);
            FarWatchSafetyVerdict below = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Final, double.NaN, Circular(hSafeR - 1.0))));
            Assert.Equal(0.0, below.DangerUT);
            Assert.Equal(FarWatchRefusal.ReturnTooClose, below.Reason);
        }

        [Fact]
        public void Truncated_CountsItsEndAsDanger()
        {
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Truncated, 5000.0, Circular(700000.0, 0.0, 5000.0))));
            Assert.Equal(FarWatchVerdictKind.AllowedWithReturn, v.Kind);
            Assert.Equal(5000.0, v.DangerUT);
            Assert.Equal(4700.0, v.ReturnUT);
            Assert.Equal(FarWatchReturnCause.Danger, v.ReturnCause);
        }

        [Fact]
        public void Impact_EndsAtTheLatestAtItsEnd()
        {
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Impact, 3000.0)));
            Assert.Equal(3000.0, v.DangerUT);
            Assert.Equal(2700.0, v.ReturnUT);
        }

        [Fact]
        public void Failed_RefusesPathUnknown()
        {
            Assert.Equal(FarWatchRefusal.PathUnknown,
                FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Failed, double.NaN))).Reason);
        }

        [Fact]
        public void DangerWithin600Seconds_Refuses_Beyond_ReturnsThreeHundredBefore()
        {
            Assert.Equal(FarWatchRefusal.ReturnTooClose,
                FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Impact, 599.0))).Reason);
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Impact, 600.0)));
            Assert.Equal(FarWatchVerdictKind.AllowedWithReturn, v.Kind);
            Assert.Equal(300.0, v.ReturnUT);
        }

        [Fact]
        public void MunFreeReturn_WithLowKerbinPeriapsis_ReturnsBeforeTheDip()
        {
            // Apoapsis 12 000 km, periapsis 30 km: the early-career free return, no stock solver.
            FarWatchConic c = Ellipse(KerbinR + 30000.0, 12000000.0);
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Final, double.NaN, c)));
            Assert.Equal(FarWatchVerdictKind.AllowedWithReturn, v.Kind);
            double tPe = FarWatchConicMath.TimeToPeriapsis(c, 0.0);
            Assert.True(v.DangerUT > 600.0 && v.DangerUT < tPe, "danger before periapsis: " + v.DangerUT + " tPe=" + tPe);
            Assert.True(FarWatchConicMath.RadiusAt(c, v.DangerUT) <= KerbinR + 71000.0 + 1.0);
            Assert.Equal(v.DangerUT - 300.0, v.ReturnUT, 6);
        }

        [Fact]
        public void SoiThenDip_ReadsTheSecondSegment()
        {
            var mun = new FarWatchSafetyBody { Name = "Mun", Radius = 200000.0, AtmosphereDepth = 0.0, MaxTerrainHeight = 7061.0 };
            FarWatchConic munCoast = new FarWatchConic
            {
                BodyName = "Mun", SemiMajorAxis = 500000.0, Eccentricity = 0.0, GravParameter = 6.5138398e10,
                StartUT = 0.0, EndUT = 4000.0,
            };
            FarWatchConic kerbinDip = Ellipse(KerbinR + 30000.0, 12000000.0);
            kerbinDip.StartUT = 4000.0;
            kerbinDip.Epoch = 4000.0;
            FarWatchSafetyInputs i = Inputs(Path(FarWatchPathEnding.Final, double.NaN, munCoast, kerbinDip));
            i.Bodies["Mun"] = mun;
            FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(FarWatchVerdictKind.AllowedWithReturn, v.Kind);
            Assert.True(v.DangerUT > 4000.0);
        }

        [Fact]
        public void AirlessBody_TerrainSetsTheSafeHeight()
        {
            var mun = new FarWatchSafetyBody { Name = "Mun", Radius = 200000.0, AtmosphereDepth = 0.0, MaxTerrainHeight = 7061.0 };
            FarWatchConic low = new FarWatchConic { BodyName = "Mun", SemiMajorAxis = 207000.0, GravParameter = 6.5138398e10, EndUT = double.PositiveInfinity };
            FarWatchConic ok = low;
            ok.SemiMajorAxis = 210000.0;
            var i = new FarWatchSafetyInputs { Ship = new FarWatchSafetyVessel { Path = Path(FarWatchPathEnding.Final, double.NaN, low) } };
            i.Bodies["Mun"] = mun;
            Assert.False(FarWatchShipSafety.Evaluate(i).IsAllowed);
            i.Ship.Path = Path(FarWatchPathEnding.Final, double.NaN, ok);
            Assert.True(FarWatchShipSafety.Evaluate(i).IsAllowed);
        }

        [Fact]
        public void NodeAndAlarmLeads_PickTheEarliestReturn()
        {
            FarWatchSafetyInputs i = Inputs(SafeOrbit());
            i.ShipNodeUTs.Add(1000.0);
            FarWatchSafetyVerdict node = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(820.0, node.ReturnUT);
            Assert.Equal(FarWatchReturnCause.ManeuverNode, node.ReturnCause);
            i.ShipAlarmUTs.Add(500.0);
            FarWatchSafetyVerdict alarm = FarWatchShipSafety.Evaluate(i);
            Assert.Equal(440.0, alarm.ReturnUT);
            Assert.Equal(FarWatchReturnCause.Alarm, alarm.ReturnCause);
            // A node already passed is ignored; one too close refuses.
            i.ShipAlarmUTs.Clear();
            i.ShipNodeUTs.Clear();
            i.ShipNodeUTs.Add(-5.0);
            Assert.Equal(FarWatchVerdictKind.Allowed, FarWatchShipSafety.Evaluate(i).Kind);
            i.ShipNodeUTs.Add(299.0);
            Assert.Equal(FarWatchRefusal.ReturnTooClose, FarWatchShipSafety.Evaluate(i).Reason);
        }

        [Fact]
        public void VerdictLine_IsInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                FarWatchSafetyVerdict v = FarWatchShipSafety.Evaluate(Inputs(Path(FarWatchPathEnding.Impact, 3000.5)));
                string line = FarWatchShipSafety.FormatVerdictLine(v, null, 2, "drift");
                Assert.Contains("verdict=AllowedWithReturn", line);
                Assert.Contains("dangerUT=3000.5", line);
                Assert.Contains("returnUT=2700.5", line);
                Assert.Contains("trigger=drift", line);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void HoverText_BusyReFlyAndSafety()
        {
            Assert.Equal(FarWatchShipSafety.BusyHoverText, FarWatchShipSafety.HoverText(FarWatchRefusal.Busy, null));
            Assert.Equal(FarWatchShipSafety.ReFlyHoverText, FarWatchShipSafety.HoverText(FarWatchRefusal.ReFly, null));
            Assert.Equal(FarWatchShipSafety.SafetyHoverText, FarWatchShipSafety.HoverText(FarWatchRefusal.ReturnTooClose, null));
            Assert.Equal(FarWatchShipSafety.SafetyHoverText, FarWatchShipSafety.HoverText(FarWatchRefusal.NotInAtmosphere, null));
        }

        [Fact]
        public void PendingWindowRefusals_AreTheBusyOnes()
        {
            Assert.True(FarWatchTransitions.IsPendingWindowRefusal("pending-split"));
            Assert.True(FarWatchTransitions.IsPendingWindowRefusal("time-jump-placement"));
            Assert.True(FarWatchTransitions.IsPendingWindowRefusal("restoring-active-tree"));
            Assert.False(FarWatchTransitions.IsPendingWindowRefusal("refly-active"));
            Assert.False(FarWatchTransitions.IsPendingWindowRefusal(null));
        }
    }

    public class FarWatchConicMathTests
    {
        private const double Mu = FarWatchShipSafetyTests.KerbinMu;

        [Fact]
        public void StateVector_Circular_GivesConstantRadius()
        {
            double r = 700000.0;
            FarWatchConic c;
            Assert.True(FarWatchConicMath.TryFromStateVector(new Vector3d(r, 0, 0), new Vector3d(0, Math.Sqrt(Mu / r), 0), Mu, 100.0, "Kerbin", out c));
            Assert.Equal(r, c.SemiMajorAxis, 0);
            Assert.True(c.Eccentricity < 1e-9);
            Assert.Equal(r, FarWatchConicMath.RadiusAt(c, 5000.0), 0);
        }

        [Fact]
        public void StateVector_Hyperbola_Climbs()
        {
            double r = 700000.0;
            double vesc = Math.Sqrt(2.0 * Mu / r);
            FarWatchConic c;
            Assert.True(FarWatchConicMath.TryFromStateVector(new Vector3d(r, 0, 0), new Vector3d(0, 1.5 * vesc, 0), Mu, 0.0, "Kerbin", out c));
            Assert.True(c.Eccentricity > 1.0);
            Assert.True(c.SemiMajorAxis < 0.0);
            Assert.Equal(r, FarWatchConicMath.PeriapsisRadius(c), 0);
            Assert.True(FarWatchConicMath.RadiusAt(c, 1000.0) > r);
            Assert.True(FarWatchConicMath.RadiusAt(c, -1000.0) > r);
        }

        [Fact]
        public void StateVector_RoundTripsTheRadiusAtLaterTimes()
        {
            // An ellipse from apoapsis: after half a period the radius is the periapsis radius.
            FarWatchConic c = FarWatchShipSafetyTests.Ellipse(650000.0, 900000.0);
            double half = Math.PI / FarWatchConicMath.MeanMotion(c);
            Assert.Equal(900000.0, FarWatchConicMath.RadiusAt(c, 0.0), 0);
            Assert.Equal(650000.0, FarWatchConicMath.RadiusAt(c, half), 0);
            Assert.Equal(half, FarWatchConicMath.TimeToPeriapsis(c, 0.0), 3);
        }

        [Fact]
        public void FirstTimeBelowRadius_FindsTheDescendingCrossing()
        {
            FarWatchConic c = FarWatchShipSafetyTests.Ellipse(650000.0, 900000.0);
            double t;
            Assert.True(FarWatchConicMath.TryFirstTimeBelowRadius(c, 700000.0, 0.0, 1e7, out t));
            Assert.Equal(700000.0, FarWatchConicMath.RadiusAt(c, t), 0);
            Assert.True(FarWatchConicMath.RadiusAt(c, t - 1.0) > 700000.0);
            Assert.False(FarWatchConicMath.TryFirstTimeBelowRadius(c, 600000.0, 0.0, 1e7, out t));
            // Already below at the start: the start is the danger.
            double half = Math.PI / FarWatchConicMath.MeanMotion(c);
            Assert.True(FarWatchConicMath.TryFirstTimeBelowRadius(c, 700000.0, half, 1e7, out t));
            Assert.Equal(half, t, 6);
        }
    }

    public class FarWatchDecisionsTests
    {
        [Fact]
        public void Takeover_At290Km_OnTheShipsBodyWhileIdle()
        {
            Assert.True(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, true, 290000.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, true, 289999.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, false, 400000.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Following, true, 400000.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(false, FarWatchState.Idle, true, 400000.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, true, double.NaN));
        }

        [Fact]
        public void HandBack_Under250Km_NeverForAnotherBody()
        {
            Assert.True(FarWatchDecisions.ShouldHandBackForDistance(FarWatchState.Following, true, 249999.0));
            Assert.False(FarWatchDecisions.ShouldHandBackForDistance(FarWatchState.Following, true, 250000.0));
            Assert.False(FarWatchDecisions.ShouldHandBackForDistance(FarWatchState.Following, false, 1000.0));
            Assert.False(FarWatchDecisions.ShouldHandBackForDistance(FarWatchState.Spawning, true, 1000.0));
            // The 40 km slack between takeover and hand-back: no flip-flop inside it.
            Assert.False(FarWatchDecisions.ShouldHandBackForDistance(FarWatchState.Following, true, 270000.0));
            Assert.False(FarWatchDecisions.ShouldTakeOver(true, FarWatchState.Idle, true, 270000.0));
        }

        [Fact]
        public void NearWatchCutoff_IsSuppressedWhileAnyFarWatchStateRuns()
        {
            Assert.False(FarWatchDecisions.SuppressNearWatchCutoff(FarWatchState.Idle));
            Assert.True(FarWatchDecisions.SuppressNearWatchCutoff(FarWatchState.Spawning));
            Assert.True(FarWatchDecisions.SuppressNearWatchCutoff(FarWatchState.Following));
        }

        private static FarWatchWatchRequest W(FarWatchState state, double dist, bool sameBody = true,
            bool startAllowed = true, bool followingGhost = true, bool resolvable = true, bool watched = false)
        {
            return FarWatchDecisions.DecideWatchRequest(state, followingGhost, resolvable, watched, sameBody, dist, startAllowed);
        }

        [Fact]
        public void WatchRequest_Table()
        {
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Idle, 100000.0, startAllowed: false));
            Assert.Equal(FarWatchWatchRequest.FarWatchDirect, W(FarWatchState.Idle, 500000.0));
            Assert.Equal(FarWatchWatchRequest.RefusedUnsafe, W(FarWatchState.Idle, 500000.0, startAllowed: false));
            Assert.Equal(FarWatchWatchRequest.RefusedOtherBody, W(FarWatchState.Idle, 500000.0, sameBody: false));
            // A near ghost on another body, an unresolvable ghost and no distance are today's entry.
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Idle, 100.0, sameBody: false));
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Idle, 500000.0, resolvable: false));
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Idle, double.NaN));
            Assert.Equal(FarWatchWatchRequest.HandBackThenNearWatch, W(FarWatchState.Following, 100000.0, startAllowed: false));
            Assert.Equal(FarWatchWatchRequest.FollowInFarWatch, W(FarWatchState.Following, 900000.0, startAllowed: false));
            Assert.Equal(FarWatchWatchRequest.RefusedOtherBody, W(FarWatchState.Following, 900000.0, sameBody: false));
            // W on the watched ghost is today's toggle off, in every state.
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Spawning, 900000.0, watched: true));
            Assert.Equal(FarWatchWatchRequest.NearWatch, W(FarWatchState.Following, 900000.0, watched: true));
        }

        // catches: a W during the far watch's transitions (Spawning, HandoverOut, HandoverBack,
        // Teardown, Aborted) or in a hold watch reaching the follow switch, which enters watch
        // mode with the 300 km guard skipped and parks the camera on a far ghost while the drone
        // is not following it.
        [Theory]
        [InlineData((int)FarWatchState.Spawning)]
        [InlineData((int)FarWatchState.HandoverOut)]
        [InlineData((int)FarWatchState.HandoverBack)]
        [InlineData((int)FarWatchState.Teardown)]
        [InlineData((int)FarWatchState.Aborted)]
        public void WatchRequest_OutsideFollowing_IsRefused(int stateValue)
        {
            var state = (FarWatchState)stateValue;
            Assert.Equal(FarWatchWatchRequest.RefusedWatchSettling, W(state, 900000.0));
            Assert.Equal(FarWatchWatchRequest.RefusedWatchSettling, W(state, 100000.0));
            Assert.False(FarWatchDecisions.IsActionable(W(state, 900000.0)));
            Assert.False(FarWatchDecisions.RoutesToTodaysEntry(W(state, 900000.0), state));
            Assert.Equal(FarWatchDecisions.SettlingHoverText,
                FarWatchDecisions.WatchRequestHover(FarWatchWatchRequest.RefusedWatchSettling, null));
        }

        [Fact]
        public void WatchRequest_HoldWatch_RefusesTheFollowSwitch_KeepsTheHandBack()
        {
            Assert.Equal(FarWatchWatchRequest.RefusedHoldWatch, W(FarWatchState.Following, 900000.0, followingGhost: false));
            Assert.Equal(FarWatchWatchRequest.HandBackThenNearWatch, W(FarWatchState.Following, 100000.0, followingGhost: false));
            Assert.Equal(FarWatchShipSafety.StopWatchingFirstHoverText,
                FarWatchDecisions.WatchRequestHover(FarWatchWatchRequest.RefusedHoldWatch, null));
        }

        [Fact]
        public void WatchRequest_IdleRefusals_FallToTodaysEntry_RunningRefusalsDoNothing()
        {
            Assert.True(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.RefusedUnsafe, FarWatchState.Idle));
            Assert.True(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.RefusedOtherBody, FarWatchState.Idle));
            Assert.True(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.NearWatch, FarWatchState.Following));
            Assert.False(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.RefusedOtherBody, FarWatchState.Following));
            Assert.False(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.RefusedHoldWatch, FarWatchState.Following));
            Assert.False(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.FarWatchDirect, FarWatchState.Idle));
            Assert.False(FarWatchDecisions.RoutesToTodaysEntry(FarWatchWatchRequest.FollowInFarWatch, FarWatchState.Following));
        }

        [Fact]
        public void ReturnDue_ComparesGameTime()
        {
            Assert.True(FarWatchDecisions.IsReturnDue(100.0, 100.0));
            Assert.False(FarWatchDecisions.IsReturnDue(99.9, 100.0));
            Assert.False(FarWatchDecisions.IsReturnDue(1e9, double.NaN));
        }

        [Fact]
        public void RecheckTrigger_OncePerRealSecond_InPriorityOrder()
        {
            Assert.Null(FarWatchDecisions.RecheckTrigger(true, true, true, true, 1000.0, 0.5));
            Assert.Equal("soi", FarWatchDecisions.RecheckTrigger(true, true, true, true, 0.0, 1.0));
            Assert.Equal("orbit-delta", FarWatchDecisions.RecheckTrigger(false, true, true, true, 0.0, 1.0));
            Assert.Equal("node", FarWatchDecisions.RecheckTrigger(false, false, true, true, 0.0, 1.0));
            Assert.Equal("alarm", FarWatchDecisions.RecheckTrigger(false, false, false, true, 0.0, 1.0));
            Assert.Equal("drift", FarWatchDecisions.RecheckTrigger(false, false, false, false, 600.0, 1.0));
            Assert.Null(FarWatchDecisions.RecheckTrigger(false, false, false, false, 599.0, 5.0));
        }

        [Fact]
        public void Overlay_ReturnTimeAndWarpReason()
        {
            Assert.Equal("2:05", FarWatchDecisions.FormatMinutesSeconds(125.9));
            Assert.Equal("1:02:05", FarWatchDecisions.FormatMinutesSeconds(3725.0));
            Assert.Equal("0:00", FarWatchDecisions.FormatMinutesSeconds(-4.0));
            Assert.Equal("return in 2:05", FarWatchDecisions.BuildOverlayClause(0.0, 125.0, "none", "Kerbin"));
            Assert.Equal("warp limited: return in 2:05", FarWatchDecisions.BuildOverlayClause(0.0, 125.0, "return", "Kerbin"));
            Assert.Equal("return in 2:05, warp limited: near Kerbin", FarWatchDecisions.BuildOverlayClause(0.0, 125.0, "floor", "Kerbin"));
            Assert.Equal("warp limited: near Mun", FarWatchDecisions.BuildOverlayClause(0.0, double.NaN, "stock", "Mun"));
            Assert.Equal(string.Empty, FarWatchDecisions.BuildOverlayClause(0.0, double.NaN, "none", "Mun"));
        }

        [Fact]
        public void ScreenMessages_OnePerEvent()
        {
            Assert.Equal("Back to Ship: its path reaches air or ground soon",
                FarWatchDecisions.ForcedReturnMessage(FarWatchReturnCause.Danger, "Ship"));
            Assert.Equal("Back to Ship: its maneuver node is coming up",
                FarWatchDecisions.ForcedReturnMessage(FarWatchReturnCause.ManeuverNode, "Ship"));
            Assert.Equal("Back to your ship: an alarm for it is coming up",
                FarWatchDecisions.ForcedReturnMessage(FarWatchReturnCause.Alarm, null));
            Assert.Equal("Watch ended at 300 km: Not in atmosphere",
                FarWatchDecisions.SnapBackMessage("Not in atmosphere"));
            // No reason (a refusal with no player-facing sentence): the event alone, never the
            // ship-orbit sentence.
            Assert.Equal("Watch ended at 300 km", FarWatchDecisions.SnapBackMessage(null));
        }

        [Fact]
        public void PauseMenuLabels_MatchInsideAColorTag()
        {
            string[] labels = { "Revert Flight", "Save Game" };
            Assert.True(FarWatchPauseMenuPatch.IsBlockedLabel("<color=orange>Revert Flight</color>", labels));
            Assert.True(FarWatchPauseMenuPatch.IsBlockedLabel("Save Game", labels));
            Assert.False(FarWatchPauseMenuPatch.IsBlockedLabel("Resume Game", labels));
            Assert.False(FarWatchPauseMenuPatch.IsBlockedLabel(null, labels));
        }
    }

    public class FarWatchWarpLimitsTests
    {
        private static readonly float[] Rates = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f };
        private static readonly float[] Limits = { 0f, 70000f, 70000f, 70000f, 120000f, 240000f, 480000f, 600000f };

        private static FarWatchWarpInputs Ghost(double altitude)
        {
            return new FarWatchWarpInputs
            {
                TargetRateIndex = 7,
                LookAhead = true,
                Mode = TimeWarp.MaxRailsRateMode.PeAltitude,
                PeModeSurfaceMargin = 2000.0,
                WarpRates = Rates,
                AltitudeLimits = Limits,
                MaxPhysicsRateIndex = 0,
                FixedDeltaTime = 0.02,
                GThreshold = 0.1,
                Altitude = altitude,
                HasAtmosphere = true,
                AtmosphereDepth = 70000.0,
                HasSolidSurface = true,
                PqsMaxHeight = 6767.0,
                PqsMeshVertMax = 6500.0,
                BodyRadius = FarWatchShipSafetyTests.KerbinR,
                NowUT = 0.0,
            };
        }

        [Fact]
        public void LandedStillGhost_AllowsOnRailsWarp()
        {
            FarWatchWarpInputs i = Ghost(70.0);
            i.LandedOrSplashed = true;
            i.SrfSpeedSq = 0.01;
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.Equal(7, r.RateIndex);
            Assert.Equal(ClearToSaveStatus.CLEAR, r.Reason);
        }

        [Fact]
        public void LandedSlidingGhost_HoldsWarp()
        {
            FarWatchWarpInputs i = Ghost(70.0);
            i.LandedOrSplashed = true;
            i.SrfSpeedSq = 1.0;
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.Equal(0, r.RateIndex);
            Assert.Equal(ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE, r.Reason);
            Assert.True(FarWatchWarpLimits.WantsLowMode(true, false, 1.0));
            Assert.False(FarWatchWarpLimits.WantsLowMode(true, true, 0.01));
        }

        [Fact]
        public void GhostInAir_GetsNoRailsWarp_AndWantsLowMode()
        {
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(Ghost(30000.0));
            Assert.Equal(0, r.RateIndex);
            Assert.Equal(ClearToSaveStatus.NOT_IN_ATMOSPHERE, r.Reason);
            Assert.True(FarWatchWarpLimits.WantsLowMode(false, true, 0.0));
            Assert.False(FarWatchWarpLimits.WantsLowMode(false, false, 1e6));
        }

        [Fact]
        public void ThrustingGhost_IsUnderAcceleration()
        {
            FarWatchWarpInputs i = Ghost(200000.0);
            i.GeeForce = 0.5;
            Assert.Equal(ClearToSaveStatus.NOT_UNDER_ACCELERATION, FarWatchWarpLimits.EvaluateStock(i).Reason);
        }

        [Fact]
        public void VesselAltitudeMode_CapsByTheBodysAltitudeLimits()
        {
            FarWatchWarpInputs i = Ghost(100000.0);
            i.Mode = TimeWarp.MaxRailsRateMode.VesselAltitude;
            i.LookAhead = false;
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.Equal(3, r.RateIndex);
            Assert.Equal(ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH, r.Reason);
            Assert.Equal(Math.Max(6767.0, 70000.0), r.MinPeAllowed);
        }

        [Fact]
        public void CrashingConic_WalksDownWithLookAhead()
        {
            FarWatchWarpInputs i = Ghost(100000.0);
            FarWatchConic c = FarWatchShipSafetyTests.Ellipse(FarWatchShipSafetyTests.KerbinR - 100000.0, FarWatchShipSafetyTests.KerbinR + 100000.0);
            i.HasConic = true;
            i.Conic = c;
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.True(r.RateIndex < 7, "rate " + r.RateIndex);
            Assert.NotEqual(ClearToSaveStatus.CLEAR, r.Reason);
        }

        [Fact]
        public void SafeHighOrbit_KeepsTheRequestedRate()
        {
            FarWatchWarpInputs i = Ghost(700000.0);
            i.HasConic = true;
            i.Conic = FarWatchShipSafetyTests.Circular(FarWatchShipSafetyTests.KerbinR + 700000.0);
            FarWatchWarpResult r = FarWatchWarpLimits.EvaluateStock(i);
            Assert.Equal(7, r.RateIndex);
        }

        [Fact]
        public void Ceiling_LeavesTenRealSecondsBeforeTheReturn()
        {
            int idx = FarWatchWarpLimits.CeilingRateIndex(Rates, 0.02, 0.0, 1000.0, 0.0, 0.0, double.NaN);
            Assert.Equal(4, idx); // 100x * 10 s = 1000 s
            Assert.Equal("return", FarWatchWarpLimits.DescribeCeiling(Rates, 0.02, 0.0, 1000.0, 0.0, 0.0, double.NaN, idx));
            Assert.Equal(Rates.Length - 1, FarWatchWarpLimits.CeilingRateIndex(Rates, 0.02, 0.0, double.NaN, 0.0, 0.0, double.NaN));
            Assert.Equal("none", FarWatchWarpLimits.DescribeCeiling(Rates, 0.02, 0.0, double.NaN, 0.0, 0.0, double.NaN, Rates.Length - 1));
        }

        [Fact]
        public void Ceiling_KeepsTheDroneFloorUnderOneKm()
        {
            int idx = FarWatchWarpLimits.CeilingRateIndex(Rates, 0.02, 0.0, double.NaN, -100.0, 9.81, 10.0);
            Assert.True(idx < Rates.Length - 1);
            Assert.True(CameraDrone.TerrainFloorMeters(-100.0, Rates[idx] * 0.02, 9.81) <= 1000.0);
            Assert.True(CameraDrone.TerrainFloorMeters(-100.0, Rates[idx + 1] * 0.02, 9.81) > 1000.0);
            Assert.Equal("floor", FarWatchWarpLimits.DescribeCeiling(Rates, 0.02, 0.0, double.NaN, -100.0, 9.81, 10.0, idx));
        }

        [Fact]
        public void LimitLine_IsInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("warp limit source=ghost rule=ceiling:return requested=1000 granted=0.5",
                    FarWatchWarpLimits.FormatLimitLine("ghost", "ceiling:return", 1000f, 0.5f));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }

    public class FarWatchPathResultTests
    {
        private static Dictionary<string, ExtrapolationBody> Bodies()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                { "Kerbin", new ExtrapolationBody { Name = "Kerbin", GravitationalParameter = FarWatchShipSafetyTests.KerbinMu, Radius = 600000.0 } },
            };
        }

        [Fact]
        public void FailedExtrapolation_IsAFailedPath()
        {
            var path = new FarWatchSafetyPath();
            FarWatchPathGatherer.ApplyResult(new ExtrapolationResult { failureReason = ExtrapolationFailureReason.MissingBody }, Bodies(), path);
            Assert.Equal(FarWatchPathEnding.Failed, path.Ending);
            Assert.NotNull(path.FailureReason);
        }

        [Fact]
        public void Destroyed_IsImpact_FinalConic_IsFinal_Else_Truncated()
        {
            var seg = new OrbitSegment { bodyName = "Kerbin", semiMajorAxis = 700000.0, eccentricity = 0.0, startUT = 0.0, endUT = 100.0 };
            var path = new FarWatchSafetyPath();
            FarWatchPathGatherer.ApplyResult(new ExtrapolationResult
            {
                terminalState = TerminalState.Destroyed, terminalUT = 900.0, segments = new List<OrbitSegment> { seg },
            }, Bodies(), path);
            Assert.Equal(FarWatchPathEnding.Impact, path.Ending);
            Assert.Equal(900.0, path.EndUT);
            Assert.Single(path.Segments);
            Assert.Equal(FarWatchShipSafetyTests.KerbinMu, path.Segments[0].GravParameter);

            path = new FarWatchSafetyPath();
            FarWatchPathGatherer.ApplyResult(new ExtrapolationResult { terminalState = TerminalState.Orbiting, endedOnFinalConic = true }, Bodies(), path);
            Assert.Equal(FarWatchPathEnding.Final, path.Ending);

            path = new FarWatchSafetyPath();
            FarWatchPathGatherer.ApplyResult(new ExtrapolationResult { terminalState = TerminalState.Orbiting, terminalUT = 50.0 }, Bodies(), path);
            Assert.Equal(FarWatchPathEnding.Truncated, path.Ending);
            Assert.Equal(50.0, path.EndUT);
        }

        [Fact]
        public void UtSignature_IsOrderFree()
        {
            Assert.Equal(FarWatchPathGatherer.Signature(new List<double> { 2.0, 1.0 }),
                FarWatchPathGatherer.Signature(new List<double> { 1.0, 2.0 }));
            Assert.Equal(string.Empty, FarWatchPathGatherer.Signature(null));
        }
    }

    public class FarWatchHoverBudgetTests
    {
        // catches: a far-watch hover line (design-far-watch.md 12.2) outgrowing the single-line
        // help strip of the narrowest window that shows it: Timeline (W buttons) at 820 px; the
        // Recordings / Missions strip (1355 px) is wider. They are consts handed to the buttons
        // at runtime, so the literal scan cannot see them.
        [Fact]
        public void FarWatchHovers_FitTheTimelineStrip()
        {
            int budget = TooltipEchoBudgetTests.BudgetChars(TimelineWindowUI.DefaultWindowWidth, TooltipEchoBox.SingleLine);
            foreach (string tip in new[]
            {
                FarWatchShipSafety.SafetyHoverText, FarWatchShipSafety.BusyHoverText,
                FarWatchShipSafety.ReFlyHoverText, FarWatchShipSafety.StopWatchingFirstHoverText,
                FarWatchShipSafety.NotWhileWatchingFarHoverText,
            })
            {
                Assert.DoesNotContain("\n", tip);
                Assert.True(tip.Length <= budget, tip.Length + " > " + budget + ": " + tip);
            }
        }
    }
}
