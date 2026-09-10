using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins <see cref="KerbalDeathRepPenalty.Decide"/>, the pure decision behind the
    /// kerbal-death reputation penalty row (design section 7.16).
    ///
    /// <para>
    /// The two properties worth stating up front, because both are the whole point of
    /// the row: ONE row per recording no matter how many kerbals died (stock applies one
    /// VesselLoss hit per vessel loss), and NO row at all when nothing captured the
    /// event - never a synthesized magnitude.
    /// </para>
    /// </summary>
    public class KerbalDeathRepPenaltyTests
    {
        private const string RecId = "rec_pod";

        private static GameStateEvent VesselLoss(
            double ut, double before, double after, string recordingId = RecId)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = KerbalDeathRepPenalty.VesselLossEventKey,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId,
            };
        }

        private static List<KerbalEndState> Crew(params KerbalEndState[] states)
            => new List<KerbalEndState>(states);

        // ---- emit ----------------------------------------------------------

        [Fact]
        public void OneDeadCrewAndOneVesselLossEvent_EmitsOneRow()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent> { VesselLoss(200.0, 0.0, -9.999828) },
                RecId,
                deathReferenceUT: 200.0);

            Assert.True(decision.Emit);
            Assert.Equal(1, decision.DeadCount);
            Assert.Equal(200.0, decision.UT);
            Assert.Null(decision.Reason);
            // Positive magnitude: NominalPenalty is stored as a magnitude, not a delta.
            Assert.Equal(9.999828f, decision.Magnitude, 1e-4f);
        }

        [Fact]
        public void TwoDeadCrew_StillEmitsExactlyOneRow()
        {
            // Stock charges per VESSEL LOSS, not per kerbal: two dead crew, one hit.
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead, KerbalEndState.Dead),
                new List<GameStateEvent> { VesselLoss(200.0, 12.0, 2.000172) },
                RecId,
                deathReferenceUT: 200.0);

            Assert.True(decision.Emit);
            Assert.Equal(2, decision.DeadCount);
            Assert.Equal(1, decision.CandidateCount);
            Assert.Equal(9.999828f, decision.Magnitude, 1e-4f);
        }

        [Fact]
        public void MixedCrew_CountsOnlyTheDead()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead, KerbalEndState.Recovered, KerbalEndState.Aboard),
                new List<GameStateEvent> { VesselLoss(200.0, 0.0, -10.0) },
                RecId,
                deathReferenceUT: 200.0);

            Assert.True(decision.Emit);
            Assert.Equal(1, decision.DeadCount);
        }

        // ---- refusals ------------------------------------------------------

        [Fact]
        public void DeadCrewWithNoEvent_EmitsNothingAndNamesTheReason()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent>(),
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(1, decision.DeadCount);
            Assert.Equal(0, decision.CandidateCount);
            Assert.Equal(0f, decision.Magnitude);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        [Fact]
        public void NoDeadCrewButAVesselLossEvent_EmitsNothing()
        {
            // A crewed vessel that survived cannot owe a crew-death penalty, even when
            // something else on the recording lost reputation under the same key.
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Recovered, KerbalEndState.Aboard),
                new List<GameStateEvent> { VesselLoss(200.0, 0.0, -10.0) },
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(0, decision.DeadCount);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoDeadCrew, decision.Reason);
        }

        [Fact]
        public void EmptyOrNullCrew_EmitsNothing()
        {
            var events = new List<GameStateEvent> { VesselLoss(200.0, 0.0, -10.0) };

            var empty = KerbalDeathRepPenalty.Decide(
                new List<KerbalEndState>(), events, RecId, 200.0);
            Assert.False(empty.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoDeadCrew, empty.Reason);

            var nullCrew = KerbalDeathRepPenalty.Decide(null, events, RecId, 200.0);
            Assert.False(nullCrew.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoDeadCrew, nullCrew.Reason);
        }

        [Fact]
        public void NullEventList_EmitsNothing()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead), null, RecId, 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        // ---- scoping -------------------------------------------------------

        [Fact]
        public void EventTaggedToAnotherRecording_IsNotAttributed()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent> { VesselLoss(200.0, 0.0, -10.0, "rec_other") },
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        [Fact]
        public void UntaggedEvent_IsNotAttributed()
        {
            // An untagged ReputationChanged is a career-level capture with no owning
            // flight; letting it pay for this recording's death would attribute an
            // unrelated loss.
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent> { VesselLoss(200.0, 0.0, -10.0, recordingId: "") },
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        [Fact]
        public void ReputationEventUnderAnotherReason_IsNotAVesselLoss()
        {
            var contractPenalty = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.ReputationChanged,
                key = "ContractPenalty",
                valueBefore = 0.0,
                valueAfter = -10.0,
                recordingId = RecId,
            };

            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent> { contractPenalty },
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        [Fact]
        public void NonLossDelta_IsNotAPenalty()
        {
            // A zero or positive delta under the VesselLoss key is not a penalty; it
            // must not become a row with a negative magnitude.
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent>
                {
                    VesselLoss(200.0, 0.0, 0.0),
                    VesselLoss(201.0, 0.0, 5.0),
                },
                RecId,
                deathReferenceUT: 200.0);

            Assert.False(decision.Emit);
            Assert.Equal(0, decision.CandidateCount);
            Assert.Equal(KerbalDeathRepPenalty.ReasonNoVesselLossEvent, decision.Reason);
        }

        // ---- ambiguity -----------------------------------------------------

        [Fact]
        public void TwoVesselLossEvents_TakesTheOneNearestTheDeathUT()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent>
                {
                    VesselLoss(120.0, 0.0, -4.0),   // far
                    VesselLoss(199.0, 0.0, -7.5),   // near
                },
                RecId,
                deathReferenceUT: 200.0);

            Assert.True(decision.Emit);
            // CandidateCount > 1 is what makes the caller log the ambiguity.
            Assert.Equal(2, decision.CandidateCount);
            Assert.Equal(199.0, decision.UT);
            Assert.Equal(7.5f, decision.Magnitude, 1e-4f);
        }

        [Fact]
        public void EquidistantVesselLossEvents_TakeTheFirstListed()
        {
            var decision = KerbalDeathRepPenalty.Decide(
                Crew(KerbalEndState.Dead),
                new List<GameStateEvent>
                {
                    VesselLoss(195.0, 0.0, -3.0),
                    VesselLoss(205.0, 0.0, -8.0),
                },
                RecId,
                deathReferenceUT: 200.0);

            Assert.True(decision.Emit);
            Assert.Equal(2, decision.CandidateCount);
            Assert.Equal(195.0, decision.UT);
            Assert.Equal(3.0f, decision.Magnitude, 1e-4f);
        }

        // ---- helpers -------------------------------------------------------

        [Fact]
        public void CountDeadCrew_CountsDeadOnly()
        {
            Assert.Equal(0, KerbalDeathRepPenalty.CountDeadCrew(null));
            Assert.Equal(0, KerbalDeathRepPenalty.CountDeadCrew(Crew()));
            Assert.Equal(0, KerbalDeathRepPenalty.CountDeadCrew(
                Crew(KerbalEndState.Aboard, KerbalEndState.Recovered, KerbalEndState.Unknown)));
            Assert.Equal(2, KerbalDeathRepPenalty.CountDeadCrew(
                Crew(KerbalEndState.Dead, KerbalEndState.Aboard, KerbalEndState.Dead)));
        }

        [Fact]
        public void IsRecordingScopedVesselLoss_RejectsEmptyRecordingId()
        {
            Assert.False(KerbalDeathRepPenalty.IsRecordingScopedVesselLoss(
                VesselLoss(200.0, 0.0, -10.0), ""));
            Assert.False(KerbalDeathRepPenalty.IsRecordingScopedVesselLoss(
                VesselLoss(200.0, 0.0, -10.0), null));
            Assert.True(KerbalDeathRepPenalty.IsRecordingScopedVesselLoss(
                VesselLoss(200.0, 0.0, -10.0), RecId));
        }

        // ---- the inside-seed rule ------------------------------------------

        // A seed already on the books when this commit started was fixed before this
        // flight was filed, so nothing this commit files can be inside it.
        [Fact]
        public void IsInsideReputationSeed_PreExistingSeed_IsOutside()
        {
            Assert.False(KerbalDeathRepPenalty.IsInsideReputationSeed(
                ReputationSeedOrigin.PreExisting));
        }

        // The live pool this commit read had already taken the hit.
        [Fact]
        public void IsInsideReputationSeed_LivePoolSeedThisCommit_IsInside()
        {
            Assert.True(KerbalDeathRepPenalty.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromLivePool));
        }

        // A career-start baseline predates the flight entirely.
        [Fact]
        public void IsInsideReputationSeed_CareerBaselineSeedThisCommit_IsOutside()
        {
            Assert.False(KerbalDeathRepPenalty.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline));
        }

        // The refusal fallback DECLINES the live pool and seeds career start (0), so it
        // is grouped with the baseline branch, not with the live-pool one. Grouping it
        // the other way would drop the penalty from a timeline seeded at career start -
        // the unmodeled-penalty failure the rule exists to prevent.
        [Fact]
        public void IsInsideReputationSeed_RefusalFallbackSeedThisCommit_IsOutside()
        {
            Assert.False(KerbalDeathRepPenalty.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback));
        }

        // The deferred case: no seed yet, so the seed will be captured LATER, off a live
        // pool this death has already lowered.
        [Fact]
        public void IsInsideReputationSeed_SeedNotYetCaptured_IsInside()
        {
            Assert.True(KerbalDeathRepPenalty.IsInsideReputationSeed(
                ReputationSeedOrigin.NotYetCaptured));
        }

        // The rule is a pure function of the ORIGIN and of nothing else. Stated as one
        // cell so a future input (a UT, a clock, a live pool read) has to break it.
        [Fact]
        public void IsInsideReputationSeed_CoversEveryOrigin()
        {
            var expected = new Dictionary<ReputationSeedOrigin, bool>
            {
                { ReputationSeedOrigin.NotYetCaptured, true },
                { ReputationSeedOrigin.PreExisting, false },
                { ReputationSeedOrigin.CreatedThisCommitFromLivePool, true },
                { ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline, false },
                { ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback, false },
            };

            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.True(expected.ContainsKey(origin),
                    "new ReputationSeedOrigin '" + origin + "' has no inside-seed answer");
                Assert.Equal(expected[origin],
                    KerbalDeathRepPenalty.IsInsideReputationSeed(origin));
            }
        }

        // ---- the re-stamp rule ---------------------------------------------

        // The refusal fallback seeds career start (0), which contains no death, so every
        // row stamped inside on the deferred expectation has to come back out. This is
        // the CL-2-pod-impact-ledger sequence: stamp inside at the commit, seed from the
        // refusal branch 4 ms later.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_RefusalFallback_Flips()
        {
            Assert.True(KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback));
        }

        // Same for the career-start baseline: its value predates the flight entirely.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_CareerBaseline_Flips()
        {
            Assert.True(KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline));
        }

        // THE MIRROR, and the case that must never flip: a live-pool seed already
        // contains every death filed before it. Flipping those rows subtracts the same
        // penalty twice.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_LivePool_DoesNotFlip()
        {
            Assert.False(KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromLivePool));
        }

        // Neither of these creates a seed in the call being answered for, so neither
        // invalidates anything: PreExisting found one already there, NotYetCaptured made
        // none. NotYetCaptured is in particular the origin that WROTE the stamps being
        // repaired - answering true here would flip them the moment they were written.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_NoSeedCreated_DoesNotFlip()
        {
            Assert.False(KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.PreExisting));
            Assert.False(KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.NotYetCaptured));
        }

        // Exhaustive over the enum, exactly like the inside-seed cell above, so a new
        // origin must state which side of the re-stamp it falls on.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_CoversEveryOrigin()
        {
            var expected = new Dictionary<ReputationSeedOrigin, bool>
            {
                { ReputationSeedOrigin.NotYetCaptured, false },
                { ReputationSeedOrigin.PreExisting, false },
                { ReputationSeedOrigin.CreatedThisCommitFromLivePool, false },
                { ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline, true },
                { ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback, true },
            };

            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.True(expected.ContainsKey(origin),
                    "new ReputationSeedOrigin '" + origin + "' has no re-stamp answer");
                Assert.Equal(expected[origin],
                    KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(origin));
            }
        }

        // The two rules answer the same enum and must not both claim a row: a row is
        // stamped inside on an origin, and re-stamped outside on an origin - never both
        // for the same origin, or the producer would undo its own stamp.
        [Fact]
        public void InsideSeedAndReStamp_NeverBothTrueForOneOrigin()
        {
            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.False(
                    KerbalDeathRepPenalty.IsInsideReputationSeed(origin)
                    && KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps(origin),
                    "origin '" + origin + "' both stamps rows inside and invalidates them");
            }
        }
    }
}
