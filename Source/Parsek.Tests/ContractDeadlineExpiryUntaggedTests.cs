using System;
using System.Collections.Generic;
using Contracts;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-24: a contract's deadline expiry is the clock's, not a flight's.
    /// It is filed untagged and written to the ledger at once, in flight too, so a discard,
    /// a Re-Fly merge check or a supersede never reads it as the live flight's loss (todo
    /// REFLY-DEADLINE-EXPIRY-REFUSES-THE-MERGE).
    /// </summary>
    [Collection("Sequential")]
    public class ContractDeadlineExpiryUntaggedTests : IDisposable
    {
        public ContractDeadlineExpiryUntaggedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Theory]
        [InlineData(Contract.State.DeadlineExpired, true)]
        [InlineData(Contract.State.Failed, false)]
        [InlineData(Contract.State.Cancelled, false)]
        [InlineData(Contract.State.Active, false)]
        public void IsDeadlineExpiryFire_OnlyForADeadlineExpiredContract(Contract.State state, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsDeadlineExpiryFire(state));
        }

        // catches: an in-flight expiry waiting for a commit that never converts it (the
        // commit converts only its own tag), so the penalty never reaches the ledger.
        [Theory]
        [InlineData(true, "", true, true)]
        [InlineData(true, "", false, true)]
        [InlineData(false, "", false, true)]
        [InlineData(false, "", true, false)]
        [InlineData(false, "rec-live", false, false)]
        public void ShouldForwardFailToLedger_AnExpiryAlways_OtherFailsByTheOldRule(
            bool deadlineExpiry, string tag, bool liveRecorder, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.ShouldForwardFailToLedger(deadlineExpiry, tag, liveRecorder));
        }

        // catches: the expiry picking up the live flight's tag through the ordinary Emit.
        [Fact]
        public void EmitUntagged_InFlightWithALiveTag_StoresTheEventUntagged()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            var evt = new GameStateEvent
            {
                ut = 400.5,
                eventType = GameStateEventType.ContractFailed,
                key = "c2",
                detail = "",
            };

            GameStateRecorder.EmitUntagged(ref evt, "test");

            Assert.Equal("", evt.recordingId);
            var stored = new List<GameStateEvent>(GameStateStore.Events);
            Assert.Single(stored);
            Assert.Equal("", stored[0].recordingId ?? "");
        }

        // catches: the expiry's penalty currency changes staying tagged to the flight, so
        // the commit reconcile warns about a funds / reputation change with no ledger row.
        [Fact]
        public void UntagContractPenaltyChanges_UntagsOnlyThatFramesPenaltyChanges()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            var funds = new GameStateEvent { ut = 400.5, eventType = GameStateEventType.FundsChanged, key = "ContractPenalty", detail = "" };
            var rep = new GameStateEvent { ut = 400.5, eventType = GameStateEventType.ReputationChanged, key = "ContractPenalty", detail = "" };
            var otherReason = new GameStateEvent { ut = 400.5, eventType = GameStateEventType.FundsChanged, key = "ContractReward", detail = "" };
            var otherFrame = new GameStateEvent { ut = 350.0, eventType = GameStateEventType.FundsChanged, key = "ContractPenalty", detail = "" };
            GameStateRecorder.Emit(ref funds, "test");
            GameStateRecorder.Emit(ref rep, "test");
            GameStateRecorder.Emit(ref otherReason, "test");
            GameStateRecorder.Emit(ref otherFrame, "test");

            Assert.Equal(2, GameStateRecorder.UntagContractPenaltyChanges(400.5));

            var stored = new List<GameStateEvent>(GameStateStore.Events);
            Assert.Equal("", stored.Find(e => e.ut == 400.5 && e.key == "ContractPenalty" && e.eventType == GameStateEventType.FundsChanged).recordingId ?? "");
            Assert.Equal("", stored.Find(e => e.eventType == GameStateEventType.ReputationChanged).recordingId ?? "");
            Assert.Equal("rec-live", stored.Find(e => e.key == "ContractReward").recordingId);
            Assert.Equal("rec-live", stored.Find(e => e.ut == 350.0).recordingId);
        }

        [Fact]
        public void Emit_InFlightWithALiveTag_TagsTheEvent_DocumentsWhyTheExpiryBypassesIt()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            var evt = new GameStateEvent
            {
                ut = 350.0,
                eventType = GameStateEventType.ContractFailed,
                key = "c2",
                detail = "",
            };

            GameStateRecorder.Emit(ref evt, "test");

            Assert.Equal("rec-live", evt.recordingId);
        }
    }
}
