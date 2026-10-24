using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The one message a Real Spawn Control Warp owes when the vessel it jumped to does not
    /// appear (todo GUI-P4-WARP-TO-SPAWN-NAMES-AN-OUTCOME-IT-CANNOT-PROMISE).
    /// </summary>
    [Collection("Sequential")]
    public class SpawnWarpOutcomeTests : IDisposable
    {
        public SpawnWarpOutcomeTests()
        {
            SpawnWarpOutcomeWatch.ResetForTesting();
        }

        public void Dispose()
        {
            SpawnWarpOutcomeWatch.ResetForTesting();
        }

        private static SpawnWarpOutcomeInput Fresh(double elapsed = 0.5)
        {
            return new SpawnWarpOutcomeInput
            {
                RecordingFound = true,
                TerminalSpawnNextAttemptUT = double.NaN,
                ElapsedSeconds = elapsed
            };
        }

        private static SpawnWarpOutcome Eval(SpawnWarpOutcomeInput input)
            => SpawnWarpOutcomeWatch.Evaluate(input, ut => "D" + ut.ToString("F0"));

        [Fact]
        public void NothingYet_IsPending()
        {
            Assert.Equal(SpawnWarpOutcomeKind.Pending, Eval(Fresh()).Kind);
            Assert.False(SpawnWarpOutcomeWatch.IsResolved(SpawnWarpOutcomeKind.Pending));
        }

        [Fact]
        public void SpawnedVessel_IsSpawned_AndSaysNothing()
        {
            var input = Fresh();
            input.SpawnedVesselPersistentId = 4242;
            input.VesselSpawned = true;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Spawned, outcome.Kind);
            Assert.True(SpawnWarpOutcomeWatch.IsResolved(outcome.Kind));
            Assert.Equal(string.Empty,
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Station", outcome.Reason));
        }

        [Fact]
        public void RecordingGone_IsGone_AndSaysNothing()
        {
            var outcome = Eval(new SpawnWarpOutcomeInput { RecordingFound = false });
            Assert.Equal(SpawnWarpOutcomeKind.Gone, outcome.Kind);
            Assert.Equal(string.Empty,
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Station", outcome.Reason));
        }

        [Fact]
        public void KscRetirement_WinsOverOtherAbandonCauses()
        {
            var input = Fresh();
            input.VesselSpawned = true;
            input.SpawnAbandoned = true;
            input.KscRetired = true;
            input.WalkbackExhausted = true;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonKscRetired, outcome.Reason);
            Assert.Equal(
                "Rover did not spawn: its flight ended parked at the launch site, which leaves no vessel.",
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Rover", outcome.Reason));
        }

        [Fact]
        public void WalkbackExhausted_NamesTheBlockedSpot()
        {
            var input = Fresh();
            input.VesselSpawned = true;
            input.SpawnAbandoned = true;
            input.WalkbackExhausted = true;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonWalkbackExhausted, outcome.Reason);
            Assert.Equal("walkback-exhausted", outcome.Cause);
        }

        [Fact]
        public void CollisionLimit_NamesTheBlockedSpot()
        {
            var input = Fresh();
            input.VesselSpawned = true;
            input.SpawnAbandoned = true;
            input.CollisionBlockCount = VesselSpawner.MaxCollisionBlocks;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonCollisionBlocked, outcome.Reason);
        }

        [Fact]
        public void NotedAbandon_UsesTheSpawnersNote()
        {
            var rec = new Recording { RecordingId = "rec-crew", VesselName = "Pod" };
            SpawnWarpOutcomeWatch.NoteAbandonReason(rec, SpawnWarpOutcomeWatch.ReasonNoLivingCrew);
            var input = Fresh();
            input.VesselSpawned = true;
            input.SpawnAbandoned = true;
            input.AbandonNote = SpawnWarpOutcomeWatch.GetAbandonNote("rec-crew");

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(
                "Pod did not spawn: none of its crew is alive to fly it.",
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Pod", outcome.Reason));
        }

        [Fact]
        public void AbandonNotes_ClearAndIgnoreBadInput()
        {
            var rec = new Recording { RecordingId = "rec-note" };
            SpawnWarpOutcomeWatch.NoteAbandonReason(rec, SpawnWarpOutcomeWatch.ReasonInvalidSnapshot);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonInvalidSnapshot,
                SpawnWarpOutcomeWatch.GetAbandonNote("rec-note"));

            SpawnWarpOutcomeWatch.ClearAbandonNote("rec-note");
            Assert.Null(SpawnWarpOutcomeWatch.GetAbandonNote("rec-note"));

            SpawnWarpOutcomeWatch.NoteAbandonReason(null, "x");
            SpawnWarpOutcomeWatch.NoteAbandonReason(new Recording { RecordingId = "" }, "x");
            SpawnWarpOutcomeWatch.NoteAbandonReason(rec, null);
            Assert.Null(SpawnWarpOutcomeWatch.GetAbandonNote("rec-note"));
            Assert.Null(SpawnWarpOutcomeWatch.GetAbandonNote(null));
        }

        [Fact]
        public void UnexplainedAbandon_PointsAtTheLog()
        {
            var input = Fresh();
            input.VesselSpawned = true;
            input.SpawnAbandoned = true;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonAbandonedUnknown, outcome.Reason);
        }

        [Theory]
        [InlineData(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, SpawnWarpOutcomeWatch.ReasonOrbitTooLow)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT, SpawnWarpOutcomeWatch.ReasonOrbitTooLow)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied, SpawnWarpOutcomeWatch.ReasonDiedAfterSpawn)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed, SpawnWarpOutcomeWatch.ReasonOrbitUnresolved)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNonFinitePeriapsis, SpawnWarpOutcomeWatch.ReasonOrbitUnresolved)]
        [InlineData(null, SpawnWarpOutcomeWatch.ReasonOrbitTooLow)]
        public void TerminalOrbitLatch_IsRefusedWithItsReason(string code, string expected)
        {
            var input = Fresh();
            input.TerminalSpawnCannotSpawnSafely = true;
            input.TerminalSpawnSafetyReasonCode = code;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(expected, outcome.Reason);
        }

        [Fact]
        public void TerminalOrbitDeferral_AfterTheWarp_IsHeldWithItsDate()
        {
            var input = Fresh();
            input.TerminalSpawnSafetyDeferred = true;
            input.TerminalDecisionAfterWarp = true;
            input.TerminalSpawnNextAttemptUT = 5000;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Held, outcome.Kind);
            Assert.Equal(
                "Station has not spawned yet: it is too low in the atmosphere right now; " +
                "it spawns on D5000, once its orbit climbs clear.",
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Station", outcome.Reason));
        }

        [Fact]
        public void TerminalOrbitDeferral_FromBeforeTheWarp_IsNotThisSpawnsAnswer()
        {
            var input = Fresh();
            input.TerminalSpawnSafetyDeferred = true;
            input.TerminalDecisionAfterWarp = false;
            input.TerminalSpawnNextAttemptUT = 5000;

            Assert.Equal(SpawnWarpOutcomeKind.Pending, Eval(input).Kind);
        }

        [Fact]
        public void TerminalOrbitHoldReason_UnknownDateOrFailedFormatter()
        {
            Assert.Equal(
                "it is too low in the atmosphere right now; it spawns once its orbit climbs clear",
                SpawnWarpOutcomeWatch.TerminalOrbitHoldReason(double.NaN, null));
            Assert.Equal(
                "it is too low in the atmosphere right now; it spawns on 5000, once its orbit climbs clear",
                SpawnWarpOutcomeWatch.TerminalOrbitHoldReason(5000, _ => null));
        }

        [Fact]
        public void LoadAttemptsExhausted_IsRefused()
        {
            var input = Fresh();
            input.SpawnAttempts = VesselSpawner.MaxSpawnAttempts;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonLoadFailed, outcome.Reason);
        }

        [Fact]
        public void RetriesStillRunning_StayPendingUntilTimeout()
        {
            var input = Fresh(SpawnWarpOutcomeWatch.TimeoutSeconds - 0.1);
            input.SpawnAttempts = 1;
            input.CollisionBlockCount = 40;

            Assert.Equal(SpawnWarpOutcomeKind.Pending, Eval(input).Kind);
        }

        [Fact]
        public void Timeout_WithCollisionRetry_IsHeldNamingTheBlocker()
        {
            var input = Fresh(SpawnWarpOutcomeWatch.TimeoutSeconds);
            input.CollisionBlockCount = 40;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Held, outcome.Kind);
            Assert.Equal(
                "Station has not spawned yet: something is in the way of its spawn spot.",
                SpawnWarpOutcomeWatch.FormatMessage(outcome.Kind, "Station", outcome.Reason));
        }

        [Fact]
        public void Timeout_WithBlockedChainTip_IsHeldNamingTheBlocker()
        {
            var input = Fresh(SpawnWarpOutcomeWatch.TimeoutSeconds + 1);
            input.ChainSpawnBlocked = true;

            var outcome = Eval(input);

            Assert.Equal(SpawnWarpOutcomeKind.Held, outcome.Kind);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonCollisionBlocked, outcome.Reason);
        }

        [Fact]
        public void Timeout_WithNoAnswer_IsHeldPointingAtTheLog()
        {
            var outcome = Eval(Fresh(SpawnWarpOutcomeWatch.TimeoutSeconds + 1));

            Assert.Equal(SpawnWarpOutcomeKind.Held, outcome.Kind);
            Assert.Equal("no-answer", outcome.Cause);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonNoAnswer, outcome.Reason);
        }

        [Fact]
        public void FormatMessage_NoName_SaysTheVessel()
        {
            Assert.Equal("The vessel did not spawn: x.",
                SpawnWarpOutcomeWatch.FormatMessage(SpawnWarpOutcomeKind.Refused, null, "x"));
        }

        [Fact]
        public void BuildInput_CopiesTheRecordingsSpawnState()
        {
            var rec = new Recording
            {
                RecordingId = "rec-build",
                SpawnAttempts = 2,
                CollisionBlockCount = 7,
                TerminalSpawnSafetyDeferred = true,
                TerminalSpawnSafetyDecisionUT = 1000,
                TerminalSpawnNextAttemptUT = 1500,
                TerminalSpawnSafetyReasonCode = TerminalOrbitSpawnSafety.ReasonCurrentAltitudeBelowSafeAltitude
            };
            SpawnWarpOutcomeWatch.NoteAbandonReason(rec, "noted");

            var atTarget = ParsekFlight.BuildSpawnWarpOutcomeInput(rec, 1000, 3.0, chainSpawnBlocked: true);
            var beforeTarget = ParsekFlight.BuildSpawnWarpOutcomeInput(rec, 1000.5, 3.0, chainSpawnBlocked: false);
            var missing = ParsekFlight.BuildSpawnWarpOutcomeInput(null, 1000, 3.0, chainSpawnBlocked: false);

            Assert.True(atTarget.RecordingFound);
            Assert.Equal(2, atTarget.SpawnAttempts);
            Assert.Equal(7, atTarget.CollisionBlockCount);
            Assert.True(atTarget.ChainSpawnBlocked);
            Assert.True(atTarget.TerminalDecisionAfterWarp);
            Assert.False(atTarget.KscRetired);
            Assert.Equal("noted", atTarget.AbandonNote);
            Assert.Equal(1500, atTarget.TerminalSpawnNextAttemptUT);
            Assert.False(beforeTarget.TerminalDecisionAfterWarp);
            Assert.False(missing.RecordingFound);
            Assert.Equal(3.0, missing.ElapsedSeconds);
        }
    }
}
