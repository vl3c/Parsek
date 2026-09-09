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
    }
}
