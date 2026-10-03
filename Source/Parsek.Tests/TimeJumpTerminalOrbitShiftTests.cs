using System;
using System.Collections.Generic;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// REALSPAWN-ORBITAL-TIP-SPAWNS-AWAY-FROM-ITS-GHOST: an epoch-shift time jump keeps a
    /// loaded vessel at its pre-jump state vector; an orbital tip whose ghost stood in the
    /// same bubble must spawn with the same time translation, so the relative geometry
    /// survives the jump (design 14.5 steps 4 and 6).
    /// </summary>
    [Collection("Sequential")]
    public class TimeJumpTerminalOrbitShiftTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const double KerbinGM = 3.5316e12;
        private const double Bubble = DistanceThresholds.PhysicsBubbleMeters;

        public TimeJumpTerminalOrbitShiftTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            TimeJumpTerminalOrbitShift.ResetForTesting();
        }

        public void Dispose()
        {
            TimeJumpTerminalOrbitShift.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeOrbitingRecording(string id, double endUT,
            TerminalState terminal = TerminalState.Orbiting)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Tip " + id,
                ExplicitStartUT = endUT - 60.0,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 699813.8,
            };
        }

        // CI-9's geometry: an LKO (sma ~700 km, ecc ~0.0095), the tip led by 2.0e-4 rad.
        private static TwoBodyOrbit MakeLkoPlayerOrbit()
        {
            double r = 693000.0;
            double vCirc = Math.Sqrt(KerbinGM / r);
            var pos = new Vector3d(r, 0.0, 0.0);
            var vel = new Vector3d(0.0, vCirc * 1.0047, vCirc * 0.01);
            Assert.True(TwoBodyOrbit.TryCreate(pos, vel, KerbinGM, 0.0, out TwoBodyOrbit orbit));
            Assert.True(orbit.IsElliptic);
            return orbit;
        }

        private static TwoBodyOrbit WithPhase(TwoBodyOrbit orbit, double meanAnomalyAtEpoch, double epoch)
        {
            orbit.MeanAnomalyAtEpoch = meanAnomalyAtEpoch;
            orbit.Epoch = epoch;
            return orbit;
        }

        // ---------------------------------------------------------------- the geometry

        [Theory]
        [InlineData(10.0)]
        [InlineData(53.8)]
        [InlineData(600.0)]
        [InlineData(3000.0)]
        public void JumpShiftedTipSpawn_KeepsRelativePositionAndVelocity_OnLko(double jumpSeconds)
        {
            double t0 = 427.4;
            double target = t0 + jumpSeconds;
            // The playback loop spawns the tip a frame after the jump.
            double spawnUT = target + 0.02;

            TwoBodyOrbit player = MakeLkoPlayerOrbit();
            TwoBodyOrbit tip = WithPhase(player, player.MeanAnomalyAtEpoch + 2.0e-4, player.Epoch);

            // The jump re-epochs the player from its pre-jump state vector
            // (Orbit.UpdateFromStateVectors(pos, vel, body, targetUT) in ApplyEpochShifts).
            player.GetStateAtUT(t0, out Vector3d playerPos0, out Vector3d playerVel0);
            Assert.True(TwoBodyOrbit.TryCreate(playerPos0, playerVel0, KerbinGM, target,
                out TwoBodyOrbit shiftedPlayer));

            // The tip spawn, through the production phase math with the jump's lag.
            double m = VesselSpawner.ComputeSpawnMeanAnomaly(
                tip.MeanAnomalyAtEpoch, tip.Epoch, tip.SemiMajorAxis, KerbinGM,
                spawnUT, target - t0);
            TwoBodyOrbit spawnedTip = WithPhase(tip, m, spawnUT);

            // Reference: the un-jumped geometry spawnUT - target seconds after T0.
            double refUT = t0 + (spawnUT - target);
            player.GetStateAtUT(refUT, out Vector3d refPlayerPos, out Vector3d refPlayerVel);
            tip.GetStateAtUT(refUT, out Vector3d refTipPos, out Vector3d refTipVel);
            Vector3d expectedRelPos = refTipPos - refPlayerPos;
            Vector3d expectedRelVel = refTipVel - refPlayerVel;

            shiftedPlayer.GetStateAtUT(spawnUT, out Vector3d postPlayerPos, out Vector3d postPlayerVel);
            spawnedTip.GetStateAtUT(spawnUT, out Vector3d postTipPos, out Vector3d postTipVel);
            Vector3d relPos = postTipPos - postPlayerPos;
            Vector3d relVel = postTipVel - postPlayerVel;

            Assert.InRange(expectedRelPos.magnitude, 120.0, 160.0);
            Assert.True((relPos - expectedRelPos).magnitude < 0.05,
                $"relative position drifted {(relPos - expectedRelPos).magnitude:R} m over a {jumpSeconds:R} s jump");
            Assert.True((relVel - expectedRelVel).magnitude < 1e-4,
                $"relative velocity drifted {(relVel - expectedRelVel).magnitude:R} m/s over a {jumpSeconds:R} s jump");
        }

        [Fact]
        public void UnshiftedTipSpawn_IsTheDefect_TipRunsAheadByOrbitalSpeedTimesJump()
        {
            // Control: with no lag (the pre-fix spawn) a 53.8 s jump on LKO puts the tip
            // ~120 km from the frozen player, the CI-9 measurement.
            double t0 = 427.4, target = 481.2;
            TwoBodyOrbit player = MakeLkoPlayerOrbit();
            TwoBodyOrbit tip = WithPhase(player, player.MeanAnomalyAtEpoch + 2.0e-4, player.Epoch);

            player.GetStateAtUT(t0, out Vector3d p0, out Vector3d v0);
            Assert.True(TwoBodyOrbit.TryCreate(p0, v0, KerbinGM, target, out TwoBodyOrbit shiftedPlayer));
            double m = VesselSpawner.ComputeSpawnMeanAnomaly(
                tip.MeanAnomalyAtEpoch, tip.Epoch, tip.SemiMajorAxis, KerbinGM, target, 0.0);
            TwoBodyOrbit spawnedTip = WithPhase(tip, m, target);

            shiftedPlayer.GetStateAtUT(target, out Vector3d pp, out _);
            spawnedTip.GetStateAtUT(target, out Vector3d tp, out _);
            Assert.InRange((tp - pp).magnitude, 110000.0, 130000.0);
        }

        [Fact]
        public void ComputeSpawnMeanAnomaly_ZeroLag_MatchesTheUnshiftedPhase()
        {
            double mEp = 1.234, epoch = 421.19, sma = 699813.8;
            foreach (double spawnUT in new[] { 421.19, 481.2, 5000.0 })
            {
                double expected = Math.Abs(spawnUT - epoch) < 1e-9
                    ? mEp
                    : TimeJumpManager.ComputeEpochShiftedMeanAnomaly(mEp, epoch, sma, KerbinGM, spawnUT);
                Assert.Equal(expected,
                    VesselSpawner.ComputeSpawnMeanAnomaly(mEp, epoch, sma, KerbinGM, spawnUT, 0.0));
            }
        }

        [Fact]
        public void ComputeSpawnMeanAnomaly_LagEvaluatesThePhaseAtTheFrozenInstant()
        {
            double mEp = 1.234, epoch = 421.19, sma = 699813.8;
            double expected = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(mEp, epoch, sma, KerbinGM, 427.4);
            Assert.Equal(expected,
                VesselSpawner.ComputeSpawnMeanAnomaly(mEp, epoch, sma, KerbinGM, 481.2, 481.2 - 427.4), 12);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ResolvePhaseUT_NoUsableLag_IsTheSpawnUT(double lag)
        {
            Assert.Equal(481.2, TimeJumpTerminalOrbitShift.ResolvePhaseUT(481.2, lag));
        }

        [Fact]
        public void ResolvePhaseUT_Lag_SubtractsIt()
        {
            Assert.Equal(427.4, TimeJumpTerminalOrbitShift.ResolvePhaseUT(481.2, 53.8), 9);
        }

        // ---------------------------------------------------------------- the capture decision

        [Fact]
        public void ClassifyCapture_CrossedOrbitalTipInBubble_Arms()
        {
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonArm,
                TimeJumpTerminalOrbitShift.ClassifyCapture(481.2, 427.4, 481.2, true, false, 140.0, Bubble));
        }

        [Theory]
        [InlineData(427.4)]   // ended at T0 (already past)
        [InlineData(400.0)]   // ended before T0
        [InlineData(481.3)]   // ends after the jump target
        public void ClassifyCapture_EndNotCrossed_Skips(double endUT)
        {
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonEndNotCrossed,
                TimeJumpTerminalOrbitShift.ClassifyCapture(endUT, 427.4, 481.2, true, false, 140.0, Bubble));
        }

        [Fact]
        public void ClassifyCapture_LandedTip_IsUnchanged()
        {
            // A surface tip spawns surface-fixed; the body carries both vessels.
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonNotTerminalOrbit,
                TimeJumpTerminalOrbitShift.ClassifyCapture(481.2, 427.4, 481.2, false, false, 140.0, Bubble));
        }

        [Fact]
        public void ClassifyCapture_AlreadySpawned_Skips()
        {
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonAlreadySpawned,
                TimeJumpTerminalOrbitShift.ClassifyCapture(481.2, 427.4, 481.2, true, true, 140.0, Bubble));
        }

        [Theory]
        [InlineData(2300.5)]
        [InlineData(120000.0)]
        [InlineData(double.NaN)]
        public void ClassifyCapture_GhostOutsideBubble_Skips(double separation)
        {
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonGhostOutsideBubble,
                TimeJumpTerminalOrbitShift.ClassifyCapture(481.2, 427.4, 481.2, true, false, separation, Bubble));
        }

        [Fact]
        public void ClassifyCapture_ZeroDeltaJump_Skips()
        {
            Assert.Equal(TimeJumpTerminalOrbitShift.ReasonInvalidJump,
                TimeJumpTerminalOrbitShift.ClassifyCapture(481.2, 481.2, 481.2, true, false, 140.0, Bubble));
        }

        // ---------------------------------------------------------------- the store

        [Fact]
        public void CaptureForJump_ArmsOnlyTheCrossedOrbitalBubbleTip_AndLogs()
        {
            var tip = MakeOrbitingRecording("ctd-tip-rec", 481.2);
            var landed = MakeOrbitingRecording("landed-rec", 470.0, TerminalState.Landed);
            var later = MakeOrbitingRecording("later-rec", 900.0);
            var far = MakeOrbitingRecording("far-rec", 470.0);
            var ghosts = new List<KeyValuePair<Recording, double>>
            {
                new KeyValuePair<Recording, double>(tip, 140.2),
                new KeyValuePair<Recording, double>(landed, 30.0),
                new KeyValuePair<Recording, double>(later, 200.0),
                new KeyValuePair<Recording, double>(far, 5000.0),
            };

            int armed = TimeJumpTerminalOrbitShift.CaptureForJump(ghosts, 427.4, 481.2, Bubble);

            Assert.Equal(1, armed);
            Assert.True(TimeJumpTerminalOrbitShift.TryGetShift("ctd-tip-rec", 481.22, out var shift));
            Assert.Equal(481.2 - 427.4, shift.LagSeconds, 9);
            Assert.Equal(140.2, shift.GhostSeparationMeters);
            Assert.False(TimeJumpTerminalOrbitShift.TryGetShift("landed-rec", 481.22, out _));
            Assert.False(TimeJumpTerminalOrbitShift.TryGetShift("later-rec", 481.22, out _));
            Assert.False(TimeJumpTerminalOrbitShift.TryGetShift("far-rec", 481.22, out _));
            Assert.Contains(logLines, l => l.Contains("[TimeJump]")
                && l.Contains("Terminal-orbit jump shift armed: rec=ctd-tip-rec vessel=Tip ctd-tip-rec T0=427.4 target=481.2 lag=53.8s ghostSeparation=140.2m"));
            Assert.Contains(logLines, l => l.Contains("[TimeJump]")
                && l.Contains("bubbleGhosts=4 armed=1 endNotCrossed=1 notTerminalOrbit=1 alreadySpawned=0 outsideBubble=1"));
        }

        [Fact]
        public void CaptureForJump_NoBubbleGhosts_ArmsNothing()
        {
            Assert.Equal(0, TimeJumpTerminalOrbitShift.CaptureForJump(null, 427.4, 481.2, Bubble));
            Assert.Equal(0, TimeJumpTerminalOrbitShift.PendingCount);
        }

        [Fact]
        public void TryGetShift_ClockBehindTheJump_DropsTheShift()
        {
            var tip = MakeOrbitingRecording("ctd-tip-rec", 481.2);
            TimeJumpTerminalOrbitShift.CaptureForJump(
                new List<KeyValuePair<Recording, double>> { new KeyValuePair<Recording, double>(tip, 140.0) },
                427.4, 481.2, Bubble);

            Assert.False(TimeJumpTerminalOrbitShift.TryGetShift("ctd-tip-rec", 300.0, out _));
            Assert.Equal(0, TimeJumpTerminalOrbitShift.PendingCount);
            Assert.Contains(logLines, l => l.Contains("[TimeJump]")
                && l.Contains("Terminal-orbit jump shift dropped: rec=ctd-tip-rec reason=clock-behind-jump"));
        }

        [Fact]
        public void Release_And_Clear_EmptyTheStore()
        {
            var a = MakeOrbitingRecording("a", 470.0);
            var b = MakeOrbitingRecording("b", 471.0);
            TimeJumpTerminalOrbitShift.CaptureForJump(
                new List<KeyValuePair<Recording, double>>
                {
                    new KeyValuePair<Recording, double>(a, 10.0),
                    new KeyValuePair<Recording, double>(b, 20.0),
                },
                427.4, 481.2, Bubble);
            Assert.Equal(2, TimeJumpTerminalOrbitShift.PendingCount);

            TimeJumpTerminalOrbitShift.Release("a", "spawned");
            Assert.False(TimeJumpTerminalOrbitShift.TryGetShift("a", 481.2, out _));
            Assert.True(TimeJumpTerminalOrbitShift.TryGetShift("b", 481.2, out _));

            TimeJumpTerminalOrbitShift.Clear("flight-scene-destroyed");
            Assert.Equal(0, TimeJumpTerminalOrbitShift.PendingCount);
            Assert.Contains(logLines, l => l.Contains("Terminal-orbit jump shifts cleared: count=1 reason=flight-scene-destroyed"));
        }
    }
}
