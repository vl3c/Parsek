using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using Contracts;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live check of the in-flight contract fail block for a committed outcome the flight
    /// clock already passed (todo CONTRACT-FAIL-AFTER-REACHED-COMMITTED-OUTCOME, game-actions
    /// design 3.13 rule 1). The flight scene applies a committed outcome only at the next
    /// recalculation, so until then stock still holds the contract Active; stock must not
    /// end it in between. The test files a committed (Space Center origin) completion of a
    /// live Active contract one second in the past, moves the contract's deadline behind the
    /// clock so stock's own <c>ContractSystem</c> update daemon expires it through
    /// <c>Contract.Update()</c> -> <c>SetState(DeadlineExpired)</c>, then calls stock's public
    /// <c>Fail()</c>. Both must be refused: the contract stays Active and neither funds nor
    /// reputation move. The deadline, the row and the index are restored afterwards; the
    /// batch's campaign isolation reverts the save on top of that.
    /// </summary>
    public class ContractReachedOutcomeInGameTest
    {
        private const string Tag = "TestRunner";
        private const float DaemonWaitSeconds = 15f;

        private static readonly FieldInfo DateDeadlineField =
            typeof(Contract).GetField("dateDeadline", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DeadlineTypeField =
            typeof(Contract).GetField("deadlineType", BindingFlags.Instance | BindingFlags.NonPublic);

        [InGameTest(Category = "ContractReachedOutcome", Scene = GameScenes.FLIGHT,
            Description = "A contract whose committed outcome the flight already passed cannot expire or fail before the career update applies it")]
        public IEnumerator ReachedCommittedOutcomeBlocksDeadlineExpiryAndFail()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("needs a career game (mode=" + (HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode.ToString() : "none") + ")");
                yield break;
            }
            ContractSystem system = ContractSystem.Instance;
            if (system == null || Funding.Instance == null || Reputation.Instance == null)
            {
                InGameAssert.Skip("needs ContractSystem, Funding and Reputation");
                yield break;
            }
            InGameAssert.IsNotNull(DateDeadlineField, "Contract.dateDeadline field not found");
            InGameAssert.IsNotNull(DeadlineTypeField, "Contract.deadlineType field not found");

            Contract contract = null;
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                Contract c = system.Contracts[i];
                if (c != null && c.ContractState == Contract.State.Active && !c.AutoAccept)
                {
                    contract = c;
                    break;
                }
            }
            if (contract == null)
            {
                InGameAssert.Skip("needs an Active contract (career-earned-pad holds PartTest 07c8e34d Active)");
                yield break;
            }

            string guid = contract.ContractGuid.ToString();
            double now = Planetarium.GetUniversalTime();
            double fundsBefore = Funding.Instance.Funds;
            float repBefore = Reputation.CurrentRep;
            int refusedBefore = Patches.ContractEarlyFailPatch.RefusedFailCount;
            double deadlineBefore = (double)DateDeadlineField.GetValue(contract);
            object deadlineTypeBefore = DeadlineTypeField.GetValue(contract);

            var row = new GameAction
            {
                UT = now - 1.0,
                Type = GameActionType.ContractComplete,
                ContractId = guid,
                ContractTitle = contract.Title,
            };
            Ledger.AddAction(row, LedgerRowWriter.StockApplied);
            CommittedFutureIndexCache.Invalidate("ContractReachedOutcomeInGameTest");
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "contract reached outcome: fixture guid={0} title=\"{1}\" committedCompleteUT={2:F1} now={3:F1} deadline={4:F1} deadlineType={5} funds={6:F1} rep={7:F3}",
                guid, contract.Title, row.UT, now, deadlineBefore, deadlineTypeBefore, fundsBefore, repBefore));

            bool restored = false;
            try
            {
                var resolution = StockUiReservationPredicates.CommittedContractResolution(
                    CommittedFutureIndexCache.Current, guid);
                InGameAssert.IsNotNull(resolution, "the committed completion is not in the committed-future index");
                InGameAssert.IsTrue(resolution.UT <= now,
                    "fixture row is not behind the clock (ut=" + resolution.UT.ToString("F1", CultureInfo.InvariantCulture) + ")");

                // (a) stock's own deadline expiry through the update daemon.
                if (Convert.ToInt32(deadlineTypeBefore) == (int)Contract.DeadlineType.None)
                    DeadlineTypeField.SetValue(contract, Contract.DeadlineType.Fixed);
                DateDeadlineField.SetValue(contract, Planetarium.GetUniversalTime() - 1.0);
                float waited = 0f;
                while (waited < DaemonWaitSeconds
                    && Patches.ContractEarlyFailPatch.RefusedFailCount == refusedBefore
                    && contract.ContractState == Contract.State.Active)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    waited += 0.25f;
                }
                int refusedAfterExpiry = Patches.ContractEarlyFailPatch.RefusedFailCount;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "contract reached outcome: expiry state={0} refused={1} waited={2:F2}s funds={3:F1} rep={4:F3}",
                    contract.ContractState, refusedAfterExpiry - refusedBefore, waited,
                    Funding.Instance.Funds, Reputation.CurrentRep));
                InGameAssert.AreEqual(Contract.State.Active, contract.ContractState,
                    "stock's deadline expiry ended the contract whose committed completion the flight passed");
                InGameAssert.IsTrue(refusedAfterExpiry > refusedBefore,
                    "stock's update daemon reached no SetState(DeadlineExpired) within " +
                    DaemonWaitSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");

                // Put the deadline back before the explicit fail, so the daemon stays quiet.
                DateDeadlineField.SetValue(contract, deadlineBefore);
                DeadlineTypeField.SetValue(contract, deadlineTypeBefore);

                // (b) stock's public Fail().
                contract.Fail();
                int refusedAfterFail = Patches.ContractEarlyFailPatch.RefusedFailCount;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "contract reached outcome: fail state={0} refused={1} funds={2:F1} rep={3:F3}",
                    contract.ContractState, refusedAfterFail - refusedAfterExpiry,
                    Funding.Instance.Funds, Reputation.CurrentRep));
                InGameAssert.AreEqual(Contract.State.Active, contract.ContractState,
                    "stock's Fail() ended the contract whose committed completion the flight passed");
                InGameAssert.IsTrue(refusedAfterFail > refusedAfterExpiry, "Fail() was not refused by the patch");

                InGameAssert.IsTrue(Math.Abs(Funding.Instance.Funds - fundsBefore) < 0.01,
                    "funds moved: before=" + fundsBefore.ToString("F1", CultureInfo.InvariantCulture) +
                    " after=" + Funding.Instance.Funds.ToString("F1", CultureInfo.InvariantCulture));
                InGameAssert.IsTrue(Math.Abs(Reputation.CurrentRep - repBefore) < 0.001f,
                    "reputation moved: before=" + repBefore.ToString("F3", CultureInfo.InvariantCulture) +
                    " after=" + Reputation.CurrentRep.ToString("F3", CultureInfo.InvariantCulture));
            }
            finally
            {
                DateDeadlineField.SetValue(contract, deadlineBefore);
                DeadlineTypeField.SetValue(contract, deadlineTypeBefore);
                var actions = Ledger.Actions;
                for (int i = actions.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(actions[i], row))
                    {
                        Ledger.RemoveActionAt(i);
                        restored = true;
                        break;
                    }
                }
                CommittedFutureIndexCache.Invalidate("ContractReachedOutcomeInGameTest cleanup");
                ParsekLog.Info(Tag, "contract reached outcome: cleanup rowRemoved=" + restored +
                    " state=" + contract.ContractState);
            }
        }

        /// <summary>
        /// Live check of todo STALE-OFFER-RE-ACCEPTABLE-AFTER-FLIGHT-PASSES-COMMITTED-RESOLUTION:
        /// an Offered contract whose committed accept and committed completion both lie behind
        /// the clock (a rewind to before the accept, then a flight past both with no
        /// recalculation in between) is removed from stock's live list by the next career
        /// update, so it cannot be accepted for a second advance. The test files a committed
        /// (Space Center origin, zero-amount) accept 20 s and completion 10 s behind the clock for
        /// a live Offered contract and runs the cutoff recalculation the scene entry runs.
        /// </summary>
        [InGameTest(Category = "ContractReachedOutcome", Scene = GameScenes.FLIGHT,
            Description = "An Offered contract whose committed accept and outcome the clock passed is taken off the board at the next career update")]
        public void EndedCommittedLifecycleRemovesTheStaleOffer()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("needs a career game");
                return;
            }
            ContractSystem system = ContractSystem.Instance;
            if (system == null || Funding.Instance == null)
            {
                InGameAssert.Skip("needs ContractSystem and Funding");
                return;
            }
            string deferral = GameStateRecorder.HasActiveUncommittedTree() || RecordingStore.HasPendingTree
                ? "a flight tree is live or pending, so the career update would defer its patch"
                : null;
            InGameAssert.IsTrue(deferral == null, deferral ?? "");

            Contract offer = null;
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                Contract c = system.Contracts[i];
                if (c != null && c.ContractState == Contract.State.Offered && !c.AutoAccept)
                {
                    offer = c;
                    break;
                }
            }
            if (offer == null)
            {
                InGameAssert.Skip("needs an Offered contract (career-earned-pad holds seven)");
                return;
            }

            Guid guid = offer.ContractGuid;
            string key = guid.ToString();
            double now = Planetarium.GetUniversalTime();
            int offersBefore = CountOfState(system, guid, Contract.State.Offered);
            var accept = new GameAction
            {
                UT = now - 20.0,
                Type = GameActionType.ContractAccept,
                ContractId = key,
                ContractTitle = offer.Title,
                DeadlineUT = double.NaN,
            };
            var complete = new GameAction
            {
                UT = now - 10.0,
                Type = GameActionType.ContractComplete,
                ContractId = key,
                ContractTitle = offer.Title,
            };
            Ledger.AddAction(accept, LedgerRowWriter.StockApplied);
            Ledger.AddAction(complete, LedgerRowWriter.StockApplied);
            CommittedFutureIndexCache.Invalidate("ContractReachedOutcomeInGameTest stale offer");
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "contract stale offer: fixture guid={0} title=\"{1}\" committedAcceptUT={2:F1} committedCompleteUT={3:F1} now={4:F1} offeredBefore={5}",
                key, offer.Title, accept.UT, complete.UT, now, offersBefore));

            int offersAfter;
            int activeAfter;
            try
            {
                InGameAssert.AreEqual(1, offersBefore, "the fixture contract is not Offered exactly once");
                LedgerOrchestrator.RecalculateAndPatch(utCutoff: now);
                offersAfter = CountOfState(system, guid, Contract.State.Offered);
                activeAfter = CountOfState(system, guid, Contract.State.Active);
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "contract stale offer: after career update offered={0} active={1} listed={2}",
                    offersAfter, activeAfter, CountOfState(system, guid, null)));
            }
            finally
            {
                var actions = Ledger.Actions;
                int removed = 0;
                for (int i = actions.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(actions[i], accept) || ReferenceEquals(actions[i], complete))
                    {
                        Ledger.RemoveActionAt(i);
                        removed++;
                    }
                }
                CommittedFutureIndexCache.Invalidate("ContractReachedOutcomeInGameTest stale offer cleanup");
                LedgerOrchestrator.RecalculateAndPatch(utCutoff: Planetarium.GetUniversalTime());
                ParsekLog.Info(Tag, "contract stale offer: cleanup rowsRemoved=" +
                    removed.ToString(CultureInfo.InvariantCulture));
            }

            InGameAssert.AreEqual(0, offersAfter,
                "the Offered copy of a contract whose committed lifecycle ended is still on the board");
            InGameAssert.AreEqual(0, activeAfter, "the ended contract came back Active");
        }

        private static int CountOfState(ContractSystem system, Guid guid, Contract.State? state)
        {
            int n = 0;
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                Contract c = system.Contracts[i];
                if (c != null && c.ContractGuid == guid && (!state.HasValue || c.ContractState == state.Value))
                    n++;
            }
            return n;
        }
    }
}
