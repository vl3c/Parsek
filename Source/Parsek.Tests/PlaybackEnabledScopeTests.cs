using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #433: PlaybackEnabled toggle must be purely visual. Every career-state
    /// effect — ledger actions, resource deltas, crew reservations, vessel spawn
    /// at ghost-end — stays active regardless of the flag. This suite covers
    /// the per-gate invariant and the narrow scope of the engine + KSC detours.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackEnabledScopeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PlaybackEnabledScopeTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        static Recording MakeSpawnableRecording(string id = "rec-1", string vesselName = "TestVessel")
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", vesselName);

            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                VesselPersistentId = 12345,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                VesselSpawned = false,
                VesselDestroyed = false,
                PlaybackEnabled = true,
                LoopPlayback = false,
                ChainBranch = 0,
                ChildBranchPointId = null,
                IsDebris = false,
                TerminalStateValue = TerminalState.Landed,
                SpawnedVesselPersistentId = 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 },
                    new TrajectoryPoint { ut = 2000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.55, altitude = 70 }
                }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Engine past-end completion helper (pure predicate)
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_PlaybackDisabledPastEnd_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PlaybackEnabled = false;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 250,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.True(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_PlaybackEnabledPastEnd_ReturnsFalse()
        {
            // traj.PlaybackEnabled=true means skipGhost came from a non-#433 cause
            // (no data or external-vessel-suppressed). Do not fire completion.
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PlaybackEnabled = true;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 250,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.False(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_NoPoints_ReturnsFalse()
        {
            // Hidden but no trajectory data — nothing to complete.
            var traj = new MockTrajectory(); // empty Points
            traj.PlaybackEnabled = false;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 250,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.False(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_BeforeEnd_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PlaybackEnabled = false;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 150,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.False(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_PastChainEffectiveEnd_ReturnsTrue()
        {
            // Mid-chain trajectory ends at 200 but chain effective end is 300.
            // currentUT=350 is past effective end → fire.
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PlaybackEnabled = false;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 300, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 350,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.True(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_AlreadyFired_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PlaybackEnabled = false;

            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                traj, flags, currentUT: 250,
                completionAlreadyFired: true, earlyDebrisCompletion: false);

            Assert.False(fire);
        }

        [Fact]
        public void ShouldFireHiddenPastEndCompletion_NullTrajectory_ReturnsFalse()
        {
            var flags = new TrajectoryPlaybackFlags { chainEndUT = 200, skipGhost = true };
            bool fire = GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                null, flags, currentUT: 250,
                completionAlreadyFired: false, earlyDebrisCompletion: false);

            Assert.False(fire);
        }

        // ─────────────────────────────────────────────────────────────
        // ShouldSpawnAtRecordingEnd — chain-disabled must now spawn
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldSpawnAtRecordingEnd_ChainFullyDisabled_StillSpawns()
        {
            // Bug #433: previously this was gated by IsChainFullyDisabled; now the
            // caller never passes isChainLooping=true for a fully-disabled chain.
            var rec = MakeSpawnableRecording();
            rec.ChainId = "chain-off";
            rec.PlaybackEnabled = false;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_ChainLooping_ReasonIsChainLooping()
        {
            var rec = MakeSpawnableRecording();
            rec.ChainId = "chain-loop";

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: true);

            Assert.False(needsSpawn);
            Assert.Equal("chain looping", reason);
        }

        // ─────────────────────────────────────────────────────────────
        // IsChainLooping — invariant
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void IsChainLooping_DisabledLoopSegment_StillReturnsTrue()
        {
            // The chain's "is looping?" state is a career property — it determines
            // if the vessel spawns at tip. It must not change when the visual toggle
            // flips. Bug #433.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };
            var rec = RecordingStore.CreateRecordingFromFlightData(points, "LoopRec");
            Assert.NotNull(rec);
            rec.ChainId = "chain-disabled-loop";
            rec.ChainIndex = 0;
            rec.ChainBranch = 0;
            rec.PlaybackEnabled = false;
            rec.LoopPlayback = true;
            RecordingStore.CommitRecordingDirect(rec);

            Assert.True(RecordingStore.IsChainLooping("chain-disabled-loop"));

            // Paired with ShouldSpawnAtRecordingEnd_ChainLooping_ReasonIsChainLooping
            // above: when IsChainLooping→true, the tip's spawn is suppressed with
            // reason "chain looping". Together these prove hiding the loop visual
            // does not flip the chain from "loop, no spawn" to "spawn at tip".
        }

        // ─────────────────────────────────────────────────────────────
        // ParsekKSC factoring: IsKscStructurallyEligible + ShouldShowInKSC
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void IsKscStructurallyEligible_KerbinWithPoints_True()
        {
            var rec = MakeSpawnableRecording();
            Assert.True(ParsekKSC.IsKscStructurallyEligible(rec));
        }

        [Fact]
        public void IsKscStructurallyEligible_NonKerbin_False()
        {
            var rec = MakeSpawnableRecording();
            var p0 = rec.Points[0]; p0.bodyName = "Mun"; rec.Points[0] = p0;
            var p1 = rec.Points[1]; p1.bodyName = "Mun"; rec.Points[1] = p1;
            Assert.False(ParsekKSC.IsKscStructurallyEligible(rec));
        }

        [Fact]
        public void IsKscStructurallyEligible_TooFewPoints_False()
        {
            var rec = MakeSpawnableRecording();
            rec.Points.RemoveAt(1);
            Assert.False(ParsekKSC.IsKscStructurallyEligible(rec));
        }

        [Fact]
        public void IsKscStructurallyEligible_IgnoresPlaybackEnabled()
        {
            // Structural predicate must be independent of the visibility toggle.
            var rec = MakeSpawnableRecording();
            rec.PlaybackEnabled = false;
            Assert.True(ParsekKSC.IsKscStructurallyEligible(rec));
        }

        [Theory]
        [InlineData(true, true, true, true)]    // Kerbin, 2+ pts, enabled → show
        [InlineData(true, true, false, false)]  // Kerbin, 2+ pts, disabled → hide
        [InlineData(true, false, true, false)]  // Kerbin, 1 pt, enabled → hide (structural)
        [InlineData(false, true, true, false)]  // Mun, 2+ pts, enabled → hide (structural)
        [InlineData(false, true, false, false)] // Mun, 2+ pts, disabled → hide
        [InlineData(false, false, false, false)] // all-off
        public void ShouldShowInKSC_EqualsStructuralAndEnabled(
            bool isKerbin, bool hasTwoPoints, bool playbackEnabled, bool expected)
        {
            var rec = MakeSpawnableRecording();
            if (!isKerbin)
            {
                var p0 = rec.Points[0]; p0.bodyName = "Mun"; rec.Points[0] = p0;
                var p1 = rec.Points[1]; p1.bodyName = "Mun"; rec.Points[1] = p1;
            }
            if (!hasTwoPoints)
            {
                rec.Points.RemoveAt(1);
            }
            rec.PlaybackEnabled = playbackEnabled;

            Assert.Equal(expected, ParsekKSC.ShouldShowInKSC(rec));
            Assert.Equal(expected,
                ParsekKSC.IsKscStructurallyEligible(rec) && rec.PlaybackEnabled);
        }

        // ─────────────────────────────────────────────────────────────
        // ShouldSpawnAtKscEnd — visibility-hidden recording still spawns
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldSpawnAtKscEnd_VisibilityHidden_StillSpawns()
        {
            // A disabled standalone recording past its end must still reach the
            // KSC spawn path when the Update loop calls ShouldSpawnAtKscEnd.
            var rec = MakeSpawnableRecording();
            rec.PlaybackEnabled = false;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, rec.EndUT + 1);
            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }
    }
}
