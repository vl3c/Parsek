using System;
using System.Collections.Generic;
using System.Globalization;
using Contracts;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live check of todos CONTRACT-COMPLETED-BY-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT and
    /// STRATEGY-CONVERSION-OF-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT (owner ruling 2026-10-24,
    /// game-actions design 3.14): a contract a ground station's science completes is filed
    /// under no flight. Stock's <c>CollectDeployedScience</c> completes its contract inside
    /// the <c>OnScienceRecieved</c> fire of <c>ResearchAndDevelopment.SubmitScienceData</c>;
    /// this cell reproduces that shape with the career's own Active contract: it calls the
    /// real <c>SubmitScienceData</c> for a deployed-experiment subject and, from a handler on
    /// that same fire, completes the contract through stock's public <c>Complete()</c>. The
    /// recorder's live tag is pinned to a fixture id so the cell reads a flight's tag even on
    /// the pad; the completion must still be untagged and in the ledger at once. The rows the
    /// submission and the completion file are what stock really paid, so they stay; only the
    /// seams are restored, and the batch's campaign isolation reverts the save on top of that.
    /// A vessel's own science (the scope stays closed) is pinned by the unit cells.
    /// </summary>
    public class ClockCauseScopeInGameTest
    {
        private const string Tag = "TestRunner";
        private const string LiveTag = "clockscope-ingame-live-tag";
        private const string DeployedExperimentId = "deployedSeismicSensor";

        [InGameTest(Category = "ClockCauseScope", Scene = GameScenes.FLIGHT,
            Description = "A contract completed inside a ground station's science submission is filed untagged and written to the ledger at once")]
        public void GroundStationSubmission_CompletesAContractUntagged()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("needs a career game (mode=" + (HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode.ToString() : "none") + ")");
                return;
            }
            ContractSystem system = ContractSystem.Instance;
            if (system == null || ResearchAndDevelopment.Instance == null || Funding.Instance == null)
            {
                InGameAssert.Skip("needs ContractSystem, R&D and Funding");
                return;
            }
            ScienceExperiment experiment = ResearchAndDevelopment.GetExperiment(DeployedExperimentId);
            if (experiment == null)
            {
                InGameAssert.Skip("needs the Breaking Ground experiment '" + DeployedExperimentId + "'");
                return;
            }
            Contract contract = FindActiveContract(system);
            if (contract == null)
            {
                InGameAssert.Skip("needs an Active contract (career-earned-pad holds PartTest 07c8e34d Active)");
                return;
            }

            ScienceSubject subject = ResearchAndDevelopment.GetExperimentSubject(
                experiment, ExperimentSituations.SrfLanded, FlightGlobals.GetHomeBody(), "", "");
            string guid = contract.ContractGuid.ToString();
            int actionsBefore = Ledger.Actions.Count;
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "clock cause scope: fixture guid={0} title=\"{1}\" subject={2} actionsBefore={3}",
                guid, contract.Title, subject.id, actionsBefore));

            bool scopeSeen = false;
            bool claimingSeen = true;
            bool completed = false;
            EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent handler = (amount, s, pv, rev) =>
            {
                if (s == null || s.id != subject.id || completed) return;
                scopeSeen = GameStateRecorder.InClockCauseScope;
                claimingSeen = GameStateRecorder.HasClaimingLiveRecorder();
                // What CollectDeployedScience does on this fire: SetComplete, then the root
                // contract's SetState(Completed).
                completed = contract.Complete();
            };

            var savedTag = GameStateRecorder.TagResolverForTesting;
            var savedLive = GameStateRecorder.HasLiveRecorderProviderForTesting;
            GameStateRecorder.TagResolverForTesting = () => LiveTag;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameEvents.OnScienceRecieved.Add(handler);
            try
            {
                float value = ResearchAndDevelopment.Instance.SubmitScienceData(1f, subject);
                GameEvents.OnScienceRecieved.Remove(handler);

                GameStateEvent stored;
                bool eventFound = TryFindEvent(GameStateEventType.ContractCompleted, guid, out stored);
                GameAction row = FindRow(GameActionType.ContractComplete, guid, 0);
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "clock cause scope: ground station submit value={0:F2} scopeSeen={1} claimingRecorderSeen={2} completed={3} state={4} " +
                    "eventFound={5} eventTag='{6}' ledgerRow={7} rowTag='{8}' scopeAfter={9}",
                    value, scopeSeen, claimingSeen, completed, contract.ContractState,
                    eventFound, eventFound ? stored.recordingId ?? "" : "-",
                    row != null, row != null ? row.RecordingId ?? "" : "-",
                    GameStateRecorder.InClockCauseScope));

                InGameAssert.IsTrue(scopeSeen, "the clock-cause scope was not open inside the ground station's OnScienceRecieved fire");
                InGameAssert.IsFalse(claimingSeen, "a live recorder still claimed events inside the scope");
                InGameAssert.IsTrue(completed, "stock's Complete() did not complete the Active contract");
                InGameAssert.IsTrue(eventFound, "no ContractCompleted event was captured for " + guid);
                InGameAssert.AreEqual("", stored.recordingId ?? "", "the completion took the live flight's tag");
                InGameAssert.IsNotNull(row, "the completion was not written to the ledger at once");
                InGameAssert.IsTrue(string.IsNullOrEmpty(row.RecordingId), "the ledger row is tagged '" + row.RecordingId + "'");
                InGameAssert.IsFalse(GameStateRecorder.InClockCauseScope, "the scope stayed open after the submission");
            }
            finally
            {
                GameEvents.OnScienceRecieved.Remove(handler);
                GameStateRecorder.TagResolverForTesting = savedTag;
                GameStateRecorder.HasLiveRecorderProviderForTesting = savedLive;
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "clock cause scope: cleanup seamsRestored=True ledgerRowsAdded={0}",
                    Ledger.Actions.Count - actionsBefore));
            }
        }

        private static Contract FindActiveContract(ContractSystem system)
        {
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                Contract c = system.Contracts[i];
                if (c != null && c.ContractState == Contract.State.Active && !c.AutoAccept)
                    return c;
            }
            return null;
        }

        private static bool TryFindEvent(GameStateEventType type, string key, out GameStateEvent found)
        {
            foreach (var e in GameStateStore.Events)
            {
                if (e.eventType == type && e.key == key)
                {
                    found = e;
                    return true;
                }
            }
            found = default(GameStateEvent);
            return false;
        }

        private static GameAction FindRow(GameActionType type, string contractId, int fromIndex)
        {
            var actions = Ledger.Actions;
            for (int i = fromIndex; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a != null && a.Type == type && a.ContractId == contractId)
                    return a;
            }
            return null;
        }
    }
}
