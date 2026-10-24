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
    }
}
