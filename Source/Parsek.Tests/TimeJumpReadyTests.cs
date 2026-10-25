using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// Step 2 ("Ready") of the Real Spawn Control time jump that keeps the player beside the
    /// ghost (docs/dev/design-realspawn-time-jump-move.md; plan Step 2 items 1-3): the
    /// rotation-safe reference resolve, the recorded-pair T0 lookup, the spawn-room slide and
    /// the greyed Ready row.
    /// </summary>
    [Collection("Sequential")]
    public class TimeJumpReadyTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRotationPeriod = 21549.425;
        private readonly List<string> logLines = new List<string>();

        public TimeJumpReadyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            TimeJumpManager.ResetPlacementForTesting();
        }

        public void Dispose()
        {
            TimeJumpManager.ResetPlacementForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- helpers ----------------

        private static void InclinedCircularState(double radius, double phaseRad, double incRad,
            out Vector3d r, out Vector3d v)
        {
            double speed = Math.Sqrt(KerbinMu / radius);
            var rp = new Vector3d(radius * Math.Cos(phaseRad), radius * Math.Sin(phaseRad), 0.0);
            var vp = new Vector3d(-speed * Math.Sin(phaseRad), speed * Math.Cos(phaseRad), 0.0);
            r = RotateX(rp, incRad);
            v = RotateX(vp, incRad);
        }

        private static Vector3d RotateX(Vector3d p, double a)
        {
            return new Vector3d(p.x, p.y * Math.Cos(a) - p.z * Math.Sin(a), p.y * Math.Sin(a) + p.z * Math.Cos(a));
        }

        /// <summary>A prograde turn about the Zup spin axis (+Z), the sense the body spins.</summary>
        private static Vector3d RotateAboutSpinAxis(Vector3d p, double a)
        {
            return new Vector3d(p.x * Math.Cos(a) - p.y * Math.Sin(a), p.x * Math.Sin(a) + p.y * Math.Cos(a), p.z);
        }

        private static TrajectoryPoint Pt(double ut, string body = "Kerbin")
        {
            return new TrajectoryPoint { ut = ut, latitude = 0.1, longitude = 20.0, altitude = 90000.0, bodyName = body };
        }

        private static TrackSection Coast(double start, double end, params TrajectoryPoint[] frames)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>(frames),
            };
        }

        // ---------------- rotation-safe T1 resolve (plan Step 2 item 1) ----------------

        [Fact]
        public void TailSeedLanCorrection_AdvancesTheNodeByTheBodysTurnOverTheGap()
        {
            // Built 20 minutes before the spawn: Kerbin turns 360 * 1200 / P degrees.
            double expected = 360.0 * 1200.0 / KerbinRotationPeriod;
            Assert.Equal(expected, TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                true, true, KerbinRotationPeriod, 1000.0, 2200.0), 9);
            // Built at the spawn UT (frame F+1): no correction, the spawn's own build.
            Assert.Equal(0.0, TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                true, true, KerbinRotationPeriod, 2200.0, 2200.0));
            // A pair fitted at T0 from samples recorded just before it: turned back.
            Assert.True(TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                true, true, KerbinRotationPeriod, 500.0, 498.0) < 0.0);
        }

        [Theory]
        [InlineData(false, true, 21549.425)] // a stored segment carries no rotation
        [InlineData(true, false, 21549.425)] // a body that does not turn
        [InlineData(true, true, 0.0)]
        [InlineData(true, true, double.NaN)]
        public void TailSeedLanCorrection_IsZeroWhenTheSeedDoesNotTurn(bool tail, bool rotates, double period)
        {
            Assert.Equal(0.0, TimeJumpPlacement.TailSeedLanCorrectionDegrees(tail, rotates, period, 0.0, 600.0));
        }

        // catches: a correction applied in the wrong sense, or about the wrong axis: turning the
        // state about the Zup spin axis by theta must read as the node line moved by +theta
        // with the plane, size and shape unchanged.
        [Fact]
        public void ATurnAboutTheSpinAxis_IsANodeAdvance_WithAEAndIncUnchanged()
        {
            InclinedCircularState(700000.0, 0.9, 0.4, out Vector3d r, out Vector3d v);
            r += new Vector3d(1500.0, 0.0, 0.0); // a little eccentricity
            double theta = 2.0 * Math.PI * 1200.0 / KerbinRotationPeriod;
            Assert.True(TwoBodyOrbit.TryCreate(r, v, KerbinMu, 0.0, out TwoBodyOrbit a));
            Assert.True(TwoBodyOrbit.TryCreate(
                RotateAboutSpinAxis(r, theta), RotateAboutSpinAxis(v, theta), KerbinMu, 0.0, out TwoBodyOrbit b));

            double dLan = b.LongitudeOfAscendingNode - a.LongitudeOfAscendingNode;
            dLan = Math.Atan2(Math.Sin(dLan), Math.Cos(dLan));
            Assert.Equal(theta, dLan, 9);
            Assert.Equal(a.SemiMajorAxis, b.SemiMajorAxis, 3);
            Assert.Equal(a.Eccentricity, b.Eccentricity, 9);
            Assert.Equal(a.Inclination, b.Inclination, 9);
            Assert.Equal(a.ArgumentOfPeriapsis, b.ArgumentOfPeriapsis, 9);
            Assert.Equal(theta * 180.0 / Math.PI,
                TimeJumpPlacement.TailSeedLanCorrectionDegrees(true, true, KerbinRotationPeriod, 0.0, 1200.0), 9);
        }

        // catches: a scan-time burn gate that compares the T1 orbit built 20 minutes early
        // without turning its node line: an inclined coasting ghost would read as a plane
        // change of tens of m/s and grey its Warp.
        [Fact]
        public void BurnGate_OnAnInclinedCoast_RefusesUncorrected_PassesCorrected()
        {
            InclinedCircularState(700000.0, 0.3, 0.5, out Vector3d r0, out Vector3d v0);
            double theta = 2.0 * Math.PI * 1200.0 / KerbinRotationPeriod;
            // The T1 orbit as a tail seed reads 20 minutes before the spawn: turned back by theta.
            Vector3d r1Early = RotateAboutSpinAxis(r0, -theta);
            Vector3d v1Early = RotateAboutSpinAxis(v0, -theta);
            double uncorrected = TimeJumpPlacement.EstimateBurnDeltaV(r0, v0, r1Early, v1Early, KerbinMu);
            Assert.True(uncorrected > TimeJumpPlacement.MaxReferenceBurnDeltaV, "uncorrected dv " + uncorrected);

            double corrRad = TimeJumpPlacement.TailSeedLanCorrectionDegrees(
                true, true, KerbinRotationPeriod, 1000.0, 2200.0) * Math.PI / 180.0;
            double corrected = TimeJumpPlacement.EstimateBurnDeltaV(r0, v0,
                RotateAboutSpinAxis(r1Early, corrRad), RotateAboutSpinAxis(v1Early, corrRad), KerbinMu);
            Assert.True(corrected < 1e-3, "corrected dv " + corrected);
        }

        [Theory]
        [InlineData(370.0, 10.0)]
        [InlineData(-30.0, 330.0)]
        [InlineData(720.0, 0.0)]
        [InlineData(45.5, 45.5)]
        public void NormalizeDegrees_FoldsIntoOneTurn(double input, double expected)
        {
            Assert.Equal(expected, TimeJumpPlacement.NormalizeDegrees(input), 9);
        }

        // ---------------- T0 from a recorded pair ----------------

        [Fact]
        public void BracketingPair_TheTwoCoastSamplesAroundT0()
        {
            var sections = new List<TrackSection>
            {
                Coast(100.0, 140.0, Pt(100.0), Pt(110.0), Pt(120.0), Pt(130.0), Pt(140.0)),
            };
            Assert.True(TimeJumpPlacement.TryFindBracketingCoastPair(sections, 123.0,
                out TrajectoryPoint a, out TrajectoryPoint b, out string reason));
            Assert.Null(reason);
            Assert.Equal(120.0, a.ut);
            Assert.Equal(130.0, b.ut);

            // On a sample: the first pair that brackets it (the one ending there).
            Assert.True(TimeJumpPlacement.TryFindBracketingCoastPair(sections, 110.0, out a, out b, out _));
            Assert.Equal(100.0, a.ut);
            Assert.Equal(110.0, b.ut);
        }

        // catches: an anchor-local Relative offset read as lat/lon/alt (a spot deep inside the
        // planet), a burn read as a coast, or a pair straddling a body change.
        [Fact]
        public void BracketingPair_RefusesWhatIsNotABodyFixedCoast()
        {
            Assert.False(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection>(), 10.0, out _, out _, out string reason));
            Assert.Equal("no-section-at-t0", reason);

            TrackSection burn = Coast(0.0, 20.0, Pt(0.0), Pt(20.0));
            burn.environment = SegmentEnvironment.ExoPropulsive;
            Assert.False(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection> { burn }, 10.0, out _, out _, out reason));
            Assert.Equal("not-coasting-at-t0", reason);

            TrackSection relative = Coast(0.0, 20.0, Pt(0.0), Pt(20.0));
            relative.referenceFrame = ReferenceFrame.Relative;
            Assert.False(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection> { relative }, 10.0, out _, out _, out reason));
            Assert.Equal("no-body-fixed-pair-at-t0", reason);

            // A Relative section's body-fixed shadow is the surface to read.
            relative.bodyFixedFrames = new List<TrajectoryPoint> { Pt(0.0), Pt(20.0) };
            Assert.True(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection> { relative }, 10.0, out TrajectoryPoint a, out _, out _));
            Assert.Equal(0.0, a.ut);

            TrackSection soi = Coast(0.0, 20.0, Pt(0.0, "Kerbin"), Pt(20.0, "Mun"));
            Assert.False(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection> { soi }, 10.0, out _, out _, out reason));
            Assert.Equal("pair-body-mismatch", reason);

            TrackSection single = Coast(0.0, 20.0, Pt(0.0), Pt(5.0));
            Assert.False(TimeJumpPlacement.TryFindBracketingCoastPair(
                new List<TrackSection> { single }, 10.0, out _, out _, out reason));
            Assert.Equal("no-bracketing-pair-at-t0", reason);
        }

        [Fact]
        public void ReadyResolvers_RefuseBeforeTouchingTheGame()
        {
            Assert.False(TimeJumpReference.TryResolveReadyT0(null, 10.0, 10.0, true,
                out _, out _, out _, out string reason));
            Assert.Equal("no-recording", reason);

            var rec = new Recording { RecordingId = "r" };
            Assert.False(TimeJumpReference.TryResolveReadyT0(rec, 10.0, 10.0, true,
                out _, out _, out _, out reason));
            Assert.Equal("no-section-at-t0", reason);

            // A Landed end spawns at its endpoint, not on a recorded terminal orbit.
            rec.TerminalStateValue = TerminalState.Landed;
            Assert.False(TimeJumpReference.TryResolveReadyEnd(rec, 100.0, 10.0,
                out _, out _, out _, out reason));
            Assert.Equal("no-terminal-orbit-spawn", reason);
            Assert.False(TimeJumpReference.TryResolveReadyEnd(null, 100.0, 10.0,
                out _, out _, out _, out reason));
            Assert.Equal("no-recording", reason);
        }

        [Fact]
        public void ReadyEndEstimate_IsCurrentOnlyForItsStoreVersion()
        {
            var entry = new ReadyEndEstimate { StateVersion = 7 };
            Assert.True(TimeJumpManager.IsReadyEndEstimateCurrent(entry, 7));
            Assert.False(TimeJumpManager.IsReadyEndEstimateCurrent(entry, 8));
            Assert.False(TimeJumpManager.IsReadyEndEstimateCurrent(null, 7));
        }

        // ---------------- spawn room (design 4.4, ruling Q7b) ----------------

        [Fact]
        public void EnclosingHalfExtent_CoversTheBoxUnderAnyRotation()
        {
            var b = new Bounds(new Vector3(1f, 0f, 0f), new Vector3(2f, 2f, 2f));
            Assert.Equal(1.0 + Math.Sqrt(3.0), TimeJumpPlacement.EnclosingHalfExtent(b), 5);
            Assert.Equal(0.0, TimeJumpPlacement.EnclosingHalfExtent(new Bounds(Vector3.zero, Vector3.zero)));
        }

        private static List<SpawnRoomMember> Group(params double[] alongTrack)
        {
            var list = new List<SpawnRoomMember>();
            for (int i = 0; i < alongTrack.Length; i++)
                list.Add(new SpawnRoomMember
                {
                    Offset = new Vector3d(0.0, alongTrack[i], 0.0),
                    HalfExtent = 8.0,
                    Name = "M" + i,
                });
            return list;
        }

        [Fact]
        public void SpawnRoom_AlreadyClear_DoesNotSlide()
        {
            // CI-9's geometry: the player ~140 m behind the spawn spot.
            List<SpawnRoomMember> g = Group(-140.0, -124.0);
            Assert.False(TimeJumpPlacement.OverlapsSpawnAtShift(1.5, g, 0.0, 5f, out string first));
            Assert.Null(first);
            SlideResult s = TimeJumpPlacement.ComputeReadySpawnRoomSlide(1.5, g, -140.0, 5f);
            Assert.True(s.Cleared);
            Assert.Equal(0.0, s.ShiftMeters);
            Assert.Equal(1, s.StepsTried);
        }

        // catches: the player left inside the spawning vessel's box (scenario d: 6 m behind),
        // a slide toward the spawn instead of away from it, or a slide that clears the first
        // member and drops the rest of the group in the box.
        [Fact]
        public void SpawnRoom_SixMetresBehind_SlidesTheGroupBackUntilClearPlusTheMargin()
        {
            // Clearance on the along-track axis: 8 (member) + 1.5 (spawn) + 2 x 5 (padding on
            // both boxes, the guard's BoundsOverlap) = 19.5 m.
            List<SpawnRoomMember> alone = Group(-6.0);
            Assert.True(TimeJumpPlacement.OverlapsSpawnAtShift(1.5, alone, 0.0, 5f, out string first));
            Assert.Equal("M0", first);
            SlideResult s = TimeJumpPlacement.ComputeReadySpawnRoomSlide(1.5, alone, -6.0, 5f);
            Assert.True(s.Cleared);
            Assert.Equal(-(14.0 + TimeJumpPlacement.SlideMarginMeters), s.ShiftMeters);
            Assert.False(TimeJumpPlacement.OverlapsSpawnAtShift(1.5, alone, s.ShiftMeters, 5f, out _));

            // A second member 16 m ahead of the player (inside the box on the far side) holds
            // the slide until it is clear too; the group moves together.
            List<SpawnRoomMember> pair = Group(-6.0, 10.0);
            SlideResult both = TimeJumpPlacement.ComputeReadySpawnRoomSlide(1.5, pair, -6.0, 5f);
            Assert.True(both.Cleared);
            Assert.Equal(-(30.0 + TimeJumpPlacement.SlideMarginMeters), both.ShiftMeters);
            Assert.False(TimeJumpPlacement.OverlapsSpawnAtShift(1.5, pair, both.ShiftMeters, 5f, out _));
        }

        [Fact]
        public void SpawnRoom_AheadOfTheSpawn_SlidesForward_AndARadialOffsetSlidesBehind()
        {
            SlideResult ahead = TimeJumpPlacement.ComputeReadySpawnRoomSlide(1.5, Group(6.0), 6.0, 5f);
            Assert.True(ahead.Cleared);
            Assert.True(ahead.ShiftMeters > 0.0);

            var radial = new List<SpawnRoomMember>
            {
                new SpawnRoomMember { Offset = new Vector3d(4.0, 0.0, 0.0), HalfExtent = 2.0, Name = "R" },
            };
            SlideResult behind = TimeJumpPlacement.ComputeReadySpawnRoomSlide(1.5, radial, 0.0, 5f);
            Assert.True(behind.Cleared);
            Assert.True(behind.ShiftMeters < 0.0);
        }

        // catches: a spawn box that no slide inside the cap can clear being "cleared" anyway
        // (the group would be placed inside the spawning vessel).
        [Fact]
        public void SpawnRoom_CannotClearWithinTheCap_IsNotCleared()
        {
            SlideResult s = TimeJumpPlacement.ComputeReadySpawnRoomSlide(400.0, Group(-6.0), -6.0, 5f);
            Assert.False(s.Cleared);
            Assert.Equal(0.0, s.ShiftMeters);
        }

        [Fact]
        public void ShiftAlongTrack_MovesOnlyTheAlongTrackOffset()
        {
            var s = new RelativeState { Offset = new Vector3d(1.0, -6.0, 2.0), Velocity = new Vector3d(0.1, 0.2, 0.3) };
            RelativeState slid = TimeJumpPlacement.ShiftAlongTrack(s, -19.0);
            Assert.Equal(1.0, slid.Offset.x, 9);
            Assert.Equal(-25.0, slid.Offset.y, 9);
            Assert.Equal(2.0, slid.Offset.z, 9);
            Assert.Equal(0.1, slid.Velocity.x, 9);
            Assert.Equal(0.2, slid.Velocity.y, 9);
            Assert.Equal(0.3, slid.Velocity.z, 9);
        }

        // ---------------- log lines ----------------

        [Fact]
        public void ReadyLines_AreInvariantAndCarryThePinnedTokens()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(
                    "Placement spawn room: ref=rsr-target-rec slide=-19.0m cleared=true steps=15 members=2"
                    + " spawnHalfExtent=1.5m padding=5.0m firstOverlap=Kerbal X",
                    TimeJumpPlacement.FormatSpawnRoomLine("rsr-target-rec",
                        new SlideResult { Cleared = true, ShiftMeters = -19.0, StepsTried = 15 },
                        2, 1.5, 5f, "Kerbal X"));
                Assert.Equal(
                    "Placement reference: ref=ctd-tip-rec kind=Ready source=stored-segment body=Kerbin"
                    + " placeUT=481.21 sma=699813.8 ecc=0.00953 gapToClickEstimate=0.0m",
                    TimeJumpPlacement.FormatReadyReferenceLine("ctd-tip-rec", "stored-segment", "Kerbin",
                        481.2131, 699813.7695, 0.0095322, 0.0));
                Assert.Equal(
                    "Placement spawn check: ref=ctd-tip-rec pid=3156176881 ut=481.25 toActive=139.6m"
                    + " expected=139.5m drift=0.1m orbitGap=0.0m spawnedBeforePlacement=false",
                    TimeJumpPlacement.FormatReadySpawnCheckLine("ctd-tip-rec", 3156176881u, 481.25,
                        139.6, 139.5, 0.0, false));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
            Assert.Contains("firstOverlap=none", TimeJumpPlacement.FormatSpawnRoomLine(
                null, default(SlideResult), 0, double.NaN, 5f, null));
            Assert.Contains("drift=n/am orbitGap=n/am", TimeJumpPlacement.FormatReadySpawnCheckLine(
                "r", 0u, 0.0, double.NaN, 1.0, double.NaN, true));
        }

        // A Ready plan dropped between its frames (a scene change) reads as a plain forward
        // jump, and the scene's cached Ready estimates go with it.
        [Fact]
        public void DropPendingPlacement_ReadyPlan_LogsThePlainJumpAndClearsTheCache()
        {
            TimeJumpManager.ArmPendingPlacementForTesting(new PlacementPlan
            {
                RecordingId = "rsr-target-rec",
                Kind = TimeJumpManager.PlacementKindReady,
                T0 = 428.0,
                T1 = 481.2,
            });
            Assert.True(TimeJumpManager.HasPendingPlacement);
            logLines.Clear();

            TimeJumpManager.DropPendingPlacement("scene-change");

            Assert.False(TimeJumpManager.HasPendingPlacement);
            Assert.Equal(0, TimeJumpManager.ReadyEndCacheCount);
            Assert.Contains(logLines, l => l.Contains("[TimeJump]")
                && l.Contains("Placement jump dropped: reason=scene-change ref=rsr-target-rec T0=428.0 T1=481.2 (plain forward jump)"));
        }

        // catches: a reference spawned in frame F, before frame F+1 placed the group, whose
        // collision check then tests the group's unplaced position; or a deferral that
        // holds some other recording's spawn, or follows a Leaves plan.
        [Theory]
        [InlineData("rsr-target-rec", true, "rsr-target-rec", true)]
        [InlineData("rsr-target-rec", true, "other-rec", false)]
        [InlineData("rsr-target-rec", false, "rsr-target-rec", false)]
        [InlineData(null, true, "rsr-target-rec", false)]
        [InlineData("rsr-target-rec", true, null, false)]
        [InlineData("", true, "", false)]
        public void ShouldDeferSpawnForPendingPlacement_OnlyTheReadyReference(
            string pendingId, bool pendingIsReady, string recId, bool expected)
        {
            Assert.Equal(expected,
                TimeJumpManager.ShouldDeferSpawnForPendingPlacement(pendingId, pendingIsReady, recId));
        }

        [Fact]
        public void IsSpawnDeferredForPendingPlacement_FollowsTheArmedPlan()
        {
            Assert.False(TimeJumpManager.IsSpawnDeferredForPendingPlacement("rsr-target-rec"));
            TimeJumpManager.ArmPendingPlacementForTesting(new PlacementPlan
            {
                RecordingId = "rsr-target-rec",
                Kind = TimeJumpManager.PlacementKindReady,
            });
            Assert.True(TimeJumpManager.IsSpawnDeferredForPendingPlacement("rsr-target-rec"));
            Assert.False(TimeJumpManager.IsSpawnDeferredForPendingPlacement("other-rec"));

            TimeJumpManager.ArmPendingPlacementForTesting(new PlacementPlan
            {
                RecordingId = "rsr-target-rec",
                Kind = TimeJumpManager.PlacementKindLeaves,
            });
            Assert.False(TimeJumpManager.IsSpawnDeferredForPendingPlacement("rsr-target-rec"));
        }

        [Fact]
        public void EvaluateReadyMoveGate_NoActiveVessel_Passes()
        {
            Assert.True(TimeJumpManager.EvaluateReadyMoveGate(new Recording(), 10.0, null).Passed);
        }

        // ---------------- the Ready row ----------------

        // catches: a Ready row whose move gate refused still offering a live Warp, or greying
        // it with the status sentence instead of the gate's reason.
        [Theory]
        [InlineData((int)MoveRefusal.Atmosphere, "Can't warp here: inside the atmosphere")]
        [InlineData((int)MoveRefusal.BodyChange, "Can't warp: CTD Target changes orbit body first")]
        [InlineData((int)MoveRefusal.Burns, "Can't warp: CTD Target burns before it spawns here")]
        [InlineData((int)MoveRefusal.Degenerate, "Can't warp: CTD Target is not on an orbit you can follow")]
        [InlineData((int)MoveRefusal.DestinationTaken, "Can't warp: the spot beside CTD Target is taken then")]
        public void Ready_RefusedMove_GreysTheWarpWithTheGateReason(int refusal, string reason)
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    vesselName = "CTD Target", endUT = 481.2, distance = 140, relativeSpeed = 0.1,
                    moveRefusal = (MoveRefusal)refusal
                },
                428.0, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed, null);

            Assert.Equal(SpawnCandidateStatus.Ready, row.Status);
            Assert.False(row.WarpButtonEnabled);
            Assert.Equal(reason, row.WarpButtonDisabledReason);
            Assert.False(row.UsesDepartureWarp);
            Assert.True(row.ConditionsMet);
        }

        [Fact]
        public void Ready_MoveAllowed_KeepsTheWarpLive()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate { vesselName = "CTD Target", endUT = 481.2, distance = 140, relativeSpeed = 0.1 },
                428.0, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed, null);
            Assert.Equal(SpawnCandidateStatus.Ready, row.Status);
            Assert.True(row.WarpButtonEnabled);
            Assert.Equal(string.Empty, row.WarpButtonDisabledReason);
        }

        // catches: Warp to Next Spawn jumping to a Ready row whose own Warp is greyed.
        [Fact]
        public void FindNextSpawnCandidate_SkipsAReadyRowWhoseMoveIsRefused()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "Refused", endUT = 150, moveRefusal = MoveRefusal.Burns },
                new NearbySpawnCandidate { vesselName = "Moves", endUT = 250 },
            };
            var result = SelectiveSpawnUI.FindNextSpawnCandidate(
                candidates, 100, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxRelativeSpeed);
            Assert.NotNull(result);
            Assert.Equal("Moves", result.Value.vesselName);
        }
    }
}
