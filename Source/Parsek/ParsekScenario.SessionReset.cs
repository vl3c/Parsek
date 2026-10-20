using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// The save-scoped session reset and the save's own game state
    /// (design-load-restores-the-save.md sections 4.2 and 4.3). [ERS-exempt]: the GAME_STATE
    /// read logs the raw row count of the ledger it just loaded (I/O, not semantics).
    /// </summary>
    public partial class ParsekScenario
    {
        internal const string GameStateNodeName = "GAME_STATE";

        // Cold loads this process has run; 0 means the next one is the boot load. Process
        // lifetime by design (allowlisted in scripts/session-reset-allowlist.txt).
        private static int coldLoadsThisProcess;

        // The save's GAME_STATE/ATTEMPT_EVENTS node read by the cold branch, held until the
        // cold active-tree restore has run (ApplySaveAttemptEventsAfterActiveTreeRestore).
        private static ConfigNode pendingSaveAttemptEvents;

        /// <summary>
        /// Names why a load is Cold, for the reset line. Pure. The first cold load of the
        /// process is the boot; a load after the main-menu transition finds no previous
        /// folder; a different folder is a folder change; anything else re-armed the cold
        /// path in process (the in-game test batch's baseline restore or crash reconcile).
        /// </summary>
        internal static string ResolveColdLoadReason(
            int coldLoadsBefore, string saveFolderBeforeDetect, string currentSaveFolder)
        {
            if (coldLoadsBefore <= 0)
                return "boot";
            if (saveFolderBeforeDetect == null)
                return "main-menu";
            if (!string.Equals(saveFolderBeforeDetect, currentSaveFolder, StringComparison.Ordinal))
                return "folder-change";
            return "batch";
        }

        /// <summary>
        /// Runs at the top of every Cold <see cref="OnLoad"/>, right after the load is
        /// classified and before <c>LoadCrewAndGroupState</c>: forgets every piece of
        /// save-scoped session state so the cold branch rebuilds Parsek from the save (and,
        /// for a save without a <c>GAME_STATE</c> node, the files). The pending tree is
        /// dropped from memory only (no discard, no purge, files stay). Settings, test seams,
        /// log rate limiters and the committed-list notification events are untouched; the
        /// committed lists are cleared later by the cold branch with notifications.
        /// </summary>
        internal static void ResetSaveScopedSessionState(string reason)
        {
            int storesReset = 0;

            bool pendingTreeDropped = RecordingStore.ResetSaveScopedStateForLoad(out string pendingSummary);
            storesReset++;
            if (pendingTreeDropped)
            {
                ParsekLog.Info("Scenario",
                    "Save-scoped reset dropped the pending tree from memory only (no discard, no purge, "
                    + "sidecars kept): " + pendingSummary);
            }

            int droppedEvents = GameStateStore.ResetForSaveLoad();
            storesReset++;
            int droppedMilestones = MilestoneStore.ResetForSaveLoad();
            storesReset++;
            int droppedPendingScience = GameStateRecorder.ResetForSaveLoad();
            storesReset++;
            LedgerOrchestrator.ResetForSaveLoad();
            LedgerOrchestrator.Initialize();
            storesReset++;
            KspStatePatcher.ResetForSaveLoad();
            storesReset++;

            ResetScenarioSessionStatics();
            storesReset++;

            DiscardReFlyLoadIntent.Clear("save-scoped-reset");
            PlaybackScopeTracker.Reset();
            RouteStore.ClearAllEscrow("save-scoped-reset");
            storesReset += 3;

            coldLoadsThisProcess++;
            ParsekLog.Info("Scenario",
                "Save-scoped session state reset: reason=" + (reason ?? "unspecified")
                + " pendingTreeDropped=" + (pendingTreeDropped ? "true" : "false")
                + " storesReset=" + storesReset.ToString(CultureInfo.InvariantCulture)
                + " droppedEvents=" + droppedEvents.ToString(CultureInfo.InvariantCulture)
                + " droppedMilestones=" + droppedMilestones.ToString(CultureInfo.InvariantCulture)
                + " droppedPendingScience=" + droppedPendingScience.ToString(CultureInfo.InvariantCulture));
        }

        private static void ResetScenarioSessionStatics()
        {
            committedTreeStateLoaded = false;
            budgetDeductionApplied = false;
            mergeDialogPending = false;
            currentFlightLaunchUT = double.NaN;
            vesselSwitchPending = false;
            vesselSwitchPendingFrame = -1;
            flightSceneReadyForVesselSwitchFlag = false;
            lastSceneChangeRequestedUT = -1.0;
            lastSceneLoadRequestClockUT = double.NaN;
            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.None;
            pendingActiveTreeResumeRewindSave = null;
            pendingFreshRecordingAfterDiscard = null;
            pendingQuickloadResumeContext = null;
            lastRestoredQuicksaveTreeFacts = null;
            ClearQuickloadResumeDetach();
            pendingSaveAttemptEvents = null;
        }

        internal static int ColdLoadsThisProcessForTesting
        {
            get => coldLoadsThisProcess;
            set => coldLoadsThisProcess = value;
        }

        /// <summary>
        /// Builds the save's <c>GAME_STATE</c> node from the nodes OnSave built once for the
        /// external files (design 4.3). Children in the design's order; a null child is left
        /// out.
        /// </summary>
        internal static ConfigNode BuildGameStateNode(
            ConfigNode ledger, ConfigNode events, ConfigNode milestones, ConfigNode baselines,
            ConfigNode pendingScience, ConfigNode attemptEvents)
        {
            var root = new ConfigNode(GameStateNodeName);
            if (ledger != null) root.AddNode(ledger);
            if (events != null) root.AddNode(events);
            if (milestones != null) root.AddNode(milestones);
            if (baselines != null) root.AddNode(baselines);
            if (pendingScience != null) root.AddNode(pendingScience);
            if (attemptEvents != null) root.AddNode(attemptEvents);
            return root;
        }

        /// <summary>
        /// The cold branch's game-state read: each store from the save's <c>GAME_STATE</c>
        /// node when present, else from its external file (today's behavior, a save written
        /// by an older build). Order as the file path: events, baselines, milestones, then the
        /// ledger, whose legacy repairs read the events. The ledger is skipped while a rewind
        /// is in progress, as before. Holds the save's <c>ATTEMPT_EVENTS</c> for
        /// <see cref="ApplySaveAttemptEventsAfterActiveTreeRestore"/>.
        /// </summary>
        internal static void LoadGameStateFromNodeOrFiles(ConfigNode scenarioNode)
        {
            pendingSaveAttemptEvents = null;
            bool loadLedger = !RewindContext.IsRewinding;
            ConfigNode gameState = scenarioNode?.GetNode(GameStateNodeName);
            if (gameState == null)
            {
                ParsekLog.Info("Scenario", "OnLoad game state from files (no GAME_STATE node)");
                GameStateStore.LoadEventFile();
                GameStateStore.LoadBaselines();
                MilestoneStore.LoadMilestoneFile();
                if (loadLedger)
                    LedgerOrchestrator.OnLoad();
                return;
            }

            var fileFallbacks = new List<string>();

            ConfigNode eventsNode = gameState.GetNode(GameStateStore.EventsNodeName);
            if (eventsNode != null)
                GameStateStore.LoadEventsFromSaveNode(eventsNode);
            else
            {
                fileFallbacks.Add(GameStateStore.EventsNodeName);
                GameStateStore.LoadEventFile();
            }

            ConfigNode baselinesNode = gameState.GetNode(BaselinesNodeName);
            if (baselinesNode != null)
                GameStateStore.LoadBaselinesFromSaveNode(baselinesNode);
            else
            {
                fileFallbacks.Add(BaselinesNodeName);
                GameStateStore.LoadBaselines();
            }

            ConfigNode milestonesNode = gameState.GetNode(MilestoneStore.MilestonesNodeName);
            if (milestonesNode != null)
                MilestoneStore.LoadMilestonesFromSaveNode(milestonesNode);
            else
            {
                fileFallbacks.Add(MilestoneStore.MilestonesNodeName);
                MilestoneStore.LoadMilestoneFile();
            }

            if (loadLedger)
            {
                ConfigNode ledgerNode = gameState.GetNode(LedgerNodeName);
                if (ledgerNode == null)
                    fileFallbacks.Add(LedgerNodeName);
                LedgerOrchestrator.OnLoad(ledgerNode);
            }

            ConfigNode pendingScienceNode = gameState.GetNode(GameStateRecorder.PendingScienceNodeName);
            int pendingScienceMalformed = 0;
            if (pendingScienceNode != null)
            {
                var parsed = GameStateRecorder.ParsePendingScienceNode(pendingScienceNode, out pendingScienceMalformed);
                GameStateRecorder.PendingScienceSubjects.Clear();
                GameStateRecorder.PendingScienceSubjects.AddRange(parsed);
            }

            pendingSaveAttemptEvents = gameState.GetNode(GameStateStore.AttemptEventsNodeName);

            ParsekLog.Info("Scenario",
                "OnLoad game state from save node: ledgerRows="
                + (loadLedger ? Ledger.Actions.Count.ToString(CultureInfo.InvariantCulture) : "skipped-rewind")
                + " events=" + GameStateStore.EventCount.ToString(CultureInfo.InvariantCulture)
                + " milestones=" + MilestoneStore.MilestoneCount.ToString(CultureInfo.InvariantCulture)
                + " baselines=" + GameStateStore.BaselineCount.ToString(CultureInfo.InvariantCulture)
                + " pendingScience=" + GameStateRecorder.PendingScienceSubjects.Count.ToString(CultureInfo.InvariantCulture)
                + (pendingSaveAttemptEvents != null
                    ? " attemptEvents=" + pendingSaveAttemptEvents.GetNodes("GAME_STATE_EVENT").Length
                        .ToString(CultureInfo.InvariantCulture)
                    : ""));
            if (fileFallbacks.Count > 0)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad game state: the save's GAME_STATE node lacks " + string.Join(",", fileFallbacks.ToArray())
                    + "; read from the external file(s) instead");
            }
            if (pendingScienceMalformed > 0)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad game state: dropped " + pendingScienceMalformed.ToString(CultureInfo.InvariantCulture)
                    + " PENDING_SCIENCE subject(s) with no subject id");
            }
        }

        internal const string BaselinesNodeName = "BASELINES";
        internal const string LedgerNodeName = "LEDGER";

        /// <summary>Outcome of <see cref="DecideSaveAttemptEventsRestore"/>.</summary>
        internal enum SaveAttemptEventsDisposition
        {
            NoneInSave,
            Restore,
            DropAttemptNotArmed,
            DropActiveTreeDiffers,
        }

        /// <summary>
        /// Pure: the save's <c>ATTEMPT_EVENTS</c> go back into memory only when the save names
        /// an attempt, that attempt is armed again for the same tree id, and the restored
        /// active tree is that tree. Every other case drops them, which keeps the #866 rule:
        /// an unmerged attempt's events become durable only through its merge.
        /// </summary>
        internal static SaveAttemptEventsDisposition DecideSaveAttemptEventsRestore(
            string savedAttemptTreeId, string armedAttemptTreeId, string restoredActiveTreeId)
        {
            if (string.IsNullOrEmpty(savedAttemptTreeId))
                return SaveAttemptEventsDisposition.NoneInSave;
            if (!string.Equals(savedAttemptTreeId, armedAttemptTreeId, StringComparison.Ordinal))
                return SaveAttemptEventsDisposition.DropAttemptNotArmed;
            if (!string.Equals(savedAttemptTreeId, restoredActiveTreeId, StringComparison.Ordinal))
                return SaveAttemptEventsDisposition.DropActiveTreeDiffers;
            return SaveAttemptEventsDisposition.Restore;
        }

        /// <summary>
        /// Runs right after the cold active-tree restore: restores the held
        /// <c>ATTEMPT_EVENTS</c> into the in-memory event list (never the file, never a
        /// milestone) when <see cref="DecideSaveAttemptEventsRestore"/> says so, otherwise
        /// drops them with a Verbose line. Consumes the held node either way. Returns the
        /// number of events restored.
        /// </summary>
        internal static int ApplySaveAttemptEventsAfterActiveTreeRestore()
        {
            ConfigNode attempt = pendingSaveAttemptEvents;
            pendingSaveAttemptEvents = null;
            if (attempt == null)
                return 0;

            string savedId = attempt.GetValue(GameStateStore.AttemptTreeIdKey);
            string armedId = RecordingStore.CommittedTreeRestoreAttemptTreeId;
            string activeId = (RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                    || RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch)
                ? RecordingStore.PendingTree?.Id
                : null;
            int count = attempt.GetNodes("GAME_STATE_EVENT").Length;
            var disposition = DecideSaveAttemptEventsRestore(savedId, armedId, activeId);
            if (disposition != SaveAttemptEventsDisposition.Restore)
            {
                ParsekLog.Verbose("Scenario",
                    "OnLoad: dropped the save's ATTEMPT_EVENTS: attemptTreeId=" + (savedId ?? "<none>")
                    + " events=" + count.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + disposition
                    + " armedAttempt=" + (armedId ?? "<none>")
                    + " restoredActiveTree=" + (activeId ?? "<none>"));
                return 0;
            }

            int restored = GameStateStore.RestoreAttemptEventsToMemory(attempt);
            ParsekLog.Info("Scenario",
                "OnLoad: restored the save's ATTEMPT_EVENTS into memory only: attemptTreeId=" + savedId
                + " events=" + restored.ToString(CultureInfo.InvariantCulture));
            return restored;
        }

        internal static bool HasPendingSaveAttemptEventsForTesting => pendingSaveAttemptEvents != null;
    }
}
