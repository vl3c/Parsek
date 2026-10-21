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
        // cold active-tree restore has run: the restore re-arms the attempt it names
        // (TryRearmSaveAttemptForRestoredTree), then ApplySaveAttemptEventsAfterActiveTreeRestore
        // consumes it.
        private static ConfigNode pendingSaveAttemptEvents;

        // The save's ATTEMPT_EVENTS when the save holds the attempt's committed tree but no
        // active node for it (an attempt that resumed its recording in place: the overlap skip
        // leaves the active node out of every save while a member is dirty). Held past OnLoad
        // until the flight's committed-tree restore arms the same tree again
        // (ResolveHeldSaveAttemptEventsOnArm); the next OnLoad drops it.
        private static ConfigNode heldSaveAttemptEvents;

        /// <summary>
        /// Names why a load that found <c>initialLoadDone</c> false is Cold, for the reset line
        /// (<see cref="LoadReconcilePolicy.ResolveColdReason"/>; a save-file load is Cold with
        /// <c>initialLoadDone</c> true and names itself there).
        /// </summary>
        internal static string ResolveColdLoadReason(
            int coldLoadsBefore, string saveFolderBeforeDetect, string currentSaveFolder)
        {
            return LoadReconcilePolicy.ResolveColdReason(
                EarlyLoadKind.Cold, initialLoadDoneAtClassify: false,
                coldLoadsBefore, saveFolderBeforeDetect, currentSaveFolder);
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

            // GC1-SAVE-SCOPED-STATE-LEFTOVERS. A Cold load is never a rewind's or a Re-Fly
            // invoke's own load (ClassifyEarly ranks both ahead of a save-file load, and neither
            // initiator loads with initialLoadDone false), so either still set here is stale and
            // would make this load skip the crew, group and ledger loads or run the Re-Fly
            // post-load dispatch against another career.
            RewindContext.ResetForSaveLoad(reason);
            if (RewindInvokeContext.Pending)
                ParsekLog.Warn("Scenario",
                    "Cold load cleared a stale Re-Fly invoke: sess=" + (RewindInvokeContext.SessionId ?? "<null>")
                    + " rp=" + (RewindInvokeContext.RewindPointId ?? "<null>")
                    + " reason=" + (reason ?? "unspecified"));
            RewindInvokeContext.Clear();
            RouteRunPrompt.ResetForSaveLoad("save-scoped-reset");
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
            heldSaveAttemptEvents = null;
            announcedInSessionSaveLoadSource = null;
        }

        // The stamp the last OnSave of this process wrote (the canary's reference). Process
        // lifetime by design (allowlisted in scripts/session-reset-allowlist.txt): a load never
        // changes what was last written.
        private static string lastWrittenSaveStamp;

        /// <summary>
        /// Writes a fresh <c>saveStamp</c> into the Parsek node and remembers it (design 4.1).
        /// Returns the stamp.
        /// </summary>
        internal static string WriteSaveStamp(ConfigNode node)
        {
            string stamp = Guid.NewGuid().ToString("N");
            if (node != null)
                node.SetValue(LoadReconcilePolicy.SaveStampKey, stamp, true);
            lastWrittenSaveStamp = stamp;
            return stamp;
        }

        /// <summary>
        /// The canary, after the refined classification: an InSessionOther / QuickloadFlight load
        /// whose node carries a stamp other than the last one this process wrote read a save no
        /// stock path announced. Logs one Warn and returns true; never reclassifies.
        /// </summary>
        internal static bool CheckLoadedSaveStamp(ConfigNode node, LoadKind refined)
        {
            string announcedSource = ConsumeAnnouncedInSessionSaveLoad();
            if (!LoadReconcilePolicy.ShouldCheckSaveStamp(refined))
                return false;
            string loaded = node?.GetValue(LoadReconcilePolicy.SaveStampKey);
            string last = lastWrittenSaveStamp;
            if (announcedSource != null && LoadReconcilePolicy.IsForeignSaveStamp(loaded, last))
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    "saveStamp check: refined=" + refined + " read an older save on purpose (source="
                    + announcedSource + "); not reported");
                return false;
            }
            if (!LoadReconcilePolicy.IsForeignSaveStamp(loaded, last))
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    "saveStamp check: refined=" + refined
                    + (string.IsNullOrEmpty(loaded) || string.IsNullOrEmpty(last)
                        ? " skipped (loaded=" + (loaded ?? "<none>") + " lastWritten=" + (last ?? "<none>") + ")"
                        : " matches the last save written"));
                return false;
            }
            string scene;
            try { scene = HighLogic.LoadedScene.ToString(); }
            catch (Exception) { scene = null; }
            ParsekLog.Warn(LoadReconcilePolicy.LogTag,
                LoadReconcilePolicy.FormatForeignSaveStampWarning(refined, scene, loaded, last));
            return true;
        }

        // A Parsek-side loader that deliberately reads a save in-session (the harness LoadGame
        // verb without playerLoad=true) announces itself here so the canary does not report it.
        // One-shot: consumed by the next canary check, whatever that load classifies as.
        private static string announcedInSessionSaveLoadSource;

        /// <summary>
        /// Notes that the next OnLoad deliberately reads a save in-session through a known
        /// non-stock loader, so the saveStamp canary stays quiet for it.
        /// </summary>
        internal static void AnnounceInSessionSaveLoad(string source)
        {
            announcedInSessionSaveLoadSource = string.IsNullOrEmpty(source) ? "<unnamed>" : source;
            ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                "In-session save load announced: source=" + announcedInSessionSaveLoadSource);
        }

        private static string ConsumeAnnouncedInSessionSaveLoad()
        {
            string source = announcedInSessionSaveLoadSource;
            announcedInSessionSaveLoadSource = null;
            return source;
        }

        internal static string LastWrittenSaveStampForTesting
        {
            get => lastWrittenSaveStamp;
            set => lastWrittenSaveStamp = value;
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

        /// <summary>Outcome of <see cref="DecideSaveAttemptRearm"/>.</summary>
        internal enum SaveAttemptRearmDecision
        {
            NoAttemptInSave,
            NotColdLoad,
            OtherTree,
            NoCommittedCopy,
            Rearm,
        }

        /// <summary>
        /// Pure: whether the cold active-tree restore re-arms the committed-tree restore attempt
        /// the save's <c>ATTEMPT_EVENTS</c> names. Only a Cold load (the reset dropped memory's
        /// attempt; every other load keeps memory's), only for the restored active tree the
        /// attempt was armed for, and only when the save also holds that tree's committed copy:
        /// the attempt's cutoffs come from it, exactly as at the original arm. Without the
        /// committed copy the attempt cannot be reconstructed and its events are dropped.
        /// </summary>
        internal static SaveAttemptRearmDecision DecideSaveAttemptRearm(
            EarlyLoadKind earlyLoadKind, string savedAttemptTreeId, string restoredTreeId,
            bool committedCopyInSave)
        {
            if (string.IsNullOrEmpty(savedAttemptTreeId))
                return SaveAttemptRearmDecision.NoAttemptInSave;
            if (earlyLoadKind != EarlyLoadKind.Cold)
                return SaveAttemptRearmDecision.NotColdLoad;
            if (!string.Equals(savedAttemptTreeId, restoredTreeId, StringComparison.Ordinal))
                return SaveAttemptRearmDecision.OtherTree;
            if (!committedCopyInSave)
                return SaveAttemptRearmDecision.NoCommittedCopy;
            return SaveAttemptRearmDecision.Rearm;
        }

        internal const string SaveAttemptRearmReasonPrefix = "cold load re-armed it from the save; armed by: ";

        /// <summary>
        /// Pure: the arm reason of a re-armed attempt. Names the cold-load restore and keeps the
        /// original arm reason the save recorded; a reason already re-armed by an earlier cold
        /// load is not wrapped again, so F5 / F9 cycles do not grow it.
        /// </summary>
        internal static string BuildSaveAttemptRearmReason(string savedArmReason)
        {
            string original = string.IsNullOrEmpty(savedArmReason) ? "<unrecorded>" : savedArmReason;
            while (original.StartsWith(SaveAttemptRearmReasonPrefix, StringComparison.Ordinal))
                original = original.Substring(SaveAttemptRearmReasonPrefix.Length);
            if (original.Length == 0)
                original = "<unrecorded>";
            return SaveAttemptRearmReasonPrefix + original;
        }

        /// <summary>
        /// Runs inside <see cref="TryRestoreActiveTreeNode"/> once the saved active tree is
        /// known to be restored, before the splice and the detach of its committed copy. When
        /// <see cref="DecideSaveAttemptRearm"/> says so, arms the committed-tree restore attempt
        /// from the save's committed copy of the tree (the same input the original arm in
        /// <c>ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore</c> had), so the restored
        /// tree is the live attempt again: <see cref="ApplySaveAttemptEventsAfterActiveTreeRestore"/>
        /// then puts its held-back events back, and the caller keeps the committed copy committed
        /// as a live attempt does. Returns true when the attempt was armed.
        /// </summary>
        internal static bool TryRearmSaveAttemptForRestoredTree(
            RecordingTree tree, EarlyLoadKind earlyLoadKind)
        {
            ConfigNode attempt = pendingSaveAttemptEvents;
            string savedId = attempt?.GetValue(GameStateStore.AttemptTreeIdKey);
            if (string.IsNullOrEmpty(savedId))
                return false;

            string savedReason = attempt.GetValue(GameStateStore.AttemptArmReasonKey);
            int heldEvents = attempt.GetNodes("GAME_STATE_EVENT").Length;
            RecordingTree committedCopy = tree != null ? FindCommittedTreeById(tree.Id, exclude: tree) : null;
            SaveAttemptRearmDecision decision = DecideSaveAttemptRearm(
                earlyLoadKind, savedId, tree?.Id, committedCopy != null);
            if (decision != SaveAttemptRearmDecision.Rearm)
            {
                ParsekLog.Verbose("Scenario",
                    "OnLoad: did not re-arm the save's committed-tree restore attempt: attemptTreeId=" + savedId
                    + " restoredTree=" + (tree?.Id ?? "<none>")
                    + " decision=" + decision
                    + " loadKind=" + earlyLoadKind
                    + " heldEvents=" + heldEvents.ToString(CultureInfo.InvariantCulture)
                    + (decision == SaveAttemptRearmDecision.NoCommittedCopy
                        ? " - the save holds no committed copy of the tree, so the attempt cannot be "
                          + "reconstructed and its held-back events are dropped"
                        : ""));
                return false;
            }

            string reason = BuildSaveAttemptRearmReason(savedReason);
            RecordingStore.ArmCommittedTreeRestoreAttempt(committedCopy, reason);
            ParsekLog.Info("Scenario",
                "OnLoad: re-armed the save's committed-tree restore attempt for tree '"
                + (tree.TreeName ?? "<unnamed>") + "' id=" + tree.Id
                + " from the save's committed copy (committedRecordings="
                + (committedCopy.Recordings?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                + " heldEvents=" + heldEvents.ToString(CultureInfo.InvariantCulture)
                + " savedArmReason=" + (savedReason ?? "<none>") + ")");
            return true;
        }

        /// <summary>Outcome of <see cref="DecideSaveAttemptEventsRestore"/>.</summary>
        internal enum SaveAttemptEventsDisposition
        {
            NoneInSave,
            Restore,
            DropAttemptNotArmed,
            DropActiveTreeDiffers,
            HoldForFlightRestore,
        }

        /// <summary>
        /// Pure: the save's <c>ATTEMPT_EVENTS</c> go back into memory only when the save names
        /// an attempt, that attempt is armed again for the same tree id, and the restored
        /// active tree is that tree. A save with no active tree, no armed attempt and the
        /// attempt's committed tree loaded is the in-place resume shape: the events are held
        /// for the flight's own re-arm of that tree (<see cref="ResolveHeldSaveAttemptEventsOnArm"/>).
        /// Every other case drops them, which keeps the #866 rule: an unmerged attempt's events
        /// become durable only through its merge.
        /// </summary>
        internal static SaveAttemptEventsDisposition DecideSaveAttemptEventsRestore(
            string savedAttemptTreeId, string armedAttemptTreeId, string restoredActiveTreeId,
            bool attemptCommittedTreeLoaded = false)
        {
            if (string.IsNullOrEmpty(savedAttemptTreeId))
                return SaveAttemptEventsDisposition.NoneInSave;
            if (string.IsNullOrEmpty(armedAttemptTreeId)
                && string.IsNullOrEmpty(restoredActiveTreeId)
                && attemptCommittedTreeLoaded)
                return SaveAttemptEventsDisposition.HoldForFlightRestore;
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
            bool committedTreeLoaded = !string.IsNullOrEmpty(savedId)
                && FindCommittedTreeById(savedId, exclude: null) != null;
            var disposition = DecideSaveAttemptEventsRestore(savedId, armedId, activeId, committedTreeLoaded);
            if (disposition == SaveAttemptEventsDisposition.HoldForFlightRestore)
            {
                heldSaveAttemptEvents = attempt;
                ParsekLog.Info("Scenario",
                    "OnLoad: held the save's ATTEMPT_EVENTS for the flight's committed-tree restore: attemptTreeId="
                    + savedId + " events=" + count.ToString(CultureInfo.InvariantCulture)
                    + " - the save holds the attempt's committed tree and no active tree (an attempt "
                    + "resumed in place); they go back into memory if the flight re-arms that tree");
                return 0;
            }
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

        /// <summary>Outcome of <see cref="DecideHeldSaveAttemptEventsOnArm"/>.</summary>
        internal enum HeldSaveAttemptEventsDisposition
        {
            NoneHeld,
            Restore,
            DropOtherTree,
        }

        /// <summary>
        /// Pure: held <c>ATTEMPT_EVENTS</c> go back into memory when the flight arms a
        /// committed-tree restore attempt for the tree they were held back for; any other arm
        /// drops them.
        /// </summary>
        internal static HeldSaveAttemptEventsDisposition DecideHeldSaveAttemptEventsOnArm(
            string heldAttemptTreeId, string armedTreeId)
        {
            if (string.IsNullOrEmpty(heldAttemptTreeId))
                return HeldSaveAttemptEventsDisposition.NoneHeld;
            return string.Equals(heldAttemptTreeId, armedTreeId, StringComparison.Ordinal)
                ? HeldSaveAttemptEventsDisposition.Restore
                : HeldSaveAttemptEventsDisposition.DropOtherTree;
        }

        /// <summary>
        /// Called right after the flight arms a committed-tree restore attempt
        /// (<c>ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore</c>): restores the held
        /// <c>ATTEMPT_EVENTS</c> into memory only when they belong to the armed tree, drops them
        /// otherwise. Consumes the held node either way. Returns the number of events restored.
        /// </summary>
        internal static int ResolveHeldSaveAttemptEventsOnArm(string armedTreeId)
        {
            ConfigNode held = heldSaveAttemptEvents;
            heldSaveAttemptEvents = null;
            string heldId = held?.GetValue(GameStateStore.AttemptTreeIdKey);
            var disposition = DecideHeldSaveAttemptEventsOnArm(heldId, armedTreeId);
            if (disposition == HeldSaveAttemptEventsDisposition.NoneHeld)
                return 0;
            int count = held.GetNodes("GAME_STATE_EVENT").Length;
            if (disposition == HeldSaveAttemptEventsDisposition.DropOtherTree)
            {
                ParsekLog.Verbose("Scenario",
                    "Dropped the held ATTEMPT_EVENTS: attemptTreeId=" + heldId
                    + " events=" + count.ToString(CultureInfo.InvariantCulture)
                    + " reason=DropOtherTree armedTree=" + (armedTreeId ?? "<none>"));
                return 0;
            }
            // The re-arm builds its cutoffs from the committed copy alone, so an event of a
            // recording the attempt made after the resume (gone with the save's missing active
            // tree) is not one it would keep off the file: drop it rather than let the next
            // save write it outside a merge (#866).
            int restored = GameStateStore.RestoreAttemptEventsToMemory(
                held, RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence,
                out int dropped);
            ParsekLog.Info("Scenario",
                "Restored the held ATTEMPT_EVENTS into memory only on the flight's re-arm: attemptTreeId="
                + heldId + " events=" + restored.ToString(CultureInfo.InvariantCulture)
                + " droppedNotHeldByTheArm=" + dropped.ToString(CultureInfo.InvariantCulture));
            return restored;
        }

        /// <summary>
        /// Drops held <c>ATTEMPT_EVENTS</c> no re-arm took: a later load starts another
        /// timeline, so they belong to nothing it restores.
        /// </summary>
        internal static void DropHeldSaveAttemptEvents(string reason)
        {
            ConfigNode held = heldSaveAttemptEvents;
            heldSaveAttemptEvents = null;
            if (held == null)
                return;
            ParsekLog.Verbose("Scenario",
                "Dropped the held ATTEMPT_EVENTS: attemptTreeId="
                + (held.GetValue(GameStateStore.AttemptTreeIdKey) ?? "<none>")
                + " events=" + held.GetNodes("GAME_STATE_EVENT").Length.ToString(CultureInfo.InvariantCulture)
                + " reason=" + (reason ?? "unspecified"));
        }

        internal static bool HasPendingSaveAttemptEventsForTesting => pendingSaveAttemptEvents != null;
        internal static bool HasHeldSaveAttemptEventsForTesting => heldSaveAttemptEvents != null;
    }
}
