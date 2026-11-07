using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure builder of the one-shot message a Re-Fly merge shows when it retired the
    /// replaced flight's losses (<see cref="SupersedeCommit.BuildReFlyUndoneMessage"/>,
    /// owner ruling 2026-11-06, todo GUI-EXPOSURE-4).
    /// </summary>
    public class ReFlyUndoneMessageTests
    {
        private static GameAction Death(string name) => new GameAction
        {
            Type = GameActionType.KerbalAssignment, KerbalName = name, KerbalEndStateField = KerbalEndState.Dead,
        };

        private static GameAction Assignment(string name, KerbalEndState state) => new GameAction
        {
            Type = GameActionType.KerbalAssignment, KerbalName = name, KerbalEndStateField = state,
        };

        private static GameAction Rep(ReputationPenaltySource source, float amount = 10f) => new GameAction
        {
            Type = GameActionType.ReputationPenalty, RepPenaltySource = source, NominalPenalty = amount,
        };

        private static string Msg(params GameAction[] rows) => SupersedeCommit.BuildReFlyUndoneMessage(rows);

        [Fact]
        public void NoRows_Null()
        {
            Assert.Null(SupersedeCommit.BuildReFlyUndoneMessage(null));
            Assert.Null(Msg());
        }

        [Fact]
        public void OnlyBookkeeping_Null_NoMessageForANoOp()
        {
            // A surviving crew member's assignment row and a zero-value duplicate are not losses.
            Assert.Null(Msg(
                Assignment("Bill", KerbalEndState.Recovered),
                Assignment("Bob", KerbalEndState.Aboard),
                new GameAction { Type = GameActionType.ContractComplete },
                Rep(ReputationPenaltySource.ContractFail, 0f)));
        }

        [Fact]
        public void OneDeath_WithItsBundledPenalty_NamesTheDeathOnly()
        {
            Assert.Equal(SupersedeCommit.ReFlyUndoneMessagePrefix + "Jebediah Kerman's death",
                Msg(Death("Jebediah Kerman"), Rep(ReputationPenaltySource.KerbalDeath)));
        }

        [Fact]
        public void SeveralDeaths_ListedOnce_InOrder()
        {
            Assert.Equal(SupersedeCommit.ReFlyUndoneMessagePrefix + "the deaths of Jeb, Bill and Bob",
                Msg(Death("Jeb"), Death("Bill"), Death("Jeb"), Death("Bob")));
        }

        [Fact]
        public void ContractFailuresAndPenalties_Counted_WithPlurals()
        {
            Assert.Equal(SupersedeCommit.ReFlyUndoneMessagePrefix
                    + "Val's death, 2 contract failures, 1 reputation penalty",
                Msg(Death("Val"),
                    new GameAction { Type = GameActionType.ContractFail },
                    new GameAction { Type = GameActionType.ContractFail },
                    Rep(ReputationPenaltySource.Other)));
            Assert.Equal(SupersedeCommit.ReFlyUndoneMessagePrefix + "1 contract failure, 2 reputation penalties",
                Msg(new GameAction { Type = GameActionType.ContractFail },
                    Rep(ReputationPenaltySource.ContractFail), Rep(ReputationPenaltySource.Other)));
        }

        [Fact]
        public void DeadKerbalWithoutAName_IsNotNamed()
        {
            Assert.Null(Msg(Death(null), Death("")));
        }
    }
}
