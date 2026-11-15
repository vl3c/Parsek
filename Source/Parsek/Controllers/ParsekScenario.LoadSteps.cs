using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Load steps OnLoad calls: the load classification facts, the save-folder change, the stock
    // pool reference, replay scope, held window, crew and group state, external files.
    public partial class ParsekScenario
    {
        private static void ReconcileReadableSidecarMirrorsOnLoadIfDisabled()
        {
            var settings = ParsekSettings.Current;
            if (settings != null && !settings.writeReadableSidecarMirrors)
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
        }

        internal static HashSet<string> BuildValidRecordingIdsForKspLoad()
        {
            return RecordingStore.BuildKnownRecordingIds();
        }

        /// <summary>The classification inputs OnLoad logs, captured once in its prologue.</summary>
        internal readonly struct LoadClassificationFacts
        {
            internal readonly bool InitialLoadDone;
            internal readonly bool Rewinding;
            internal readonly bool ReFlyInvoke;
            internal readonly string DiscardReFlyTarget;
            internal readonly string Handoff;
            internal readonly string ColdReason;
            internal readonly bool SaveFileLoad;
            internal readonly bool RevertPending;
            internal readonly int IntentAgeFrames;
            internal readonly double IntentNodeUT;

            internal LoadClassificationFacts(
                bool initialLoadDone, bool rewinding, bool reFlyInvoke, string discardReFlyTarget,
                string handoff, string coldReason, bool saveFileLoad, bool revertPending,
                int intentAgeFrames, double intentNodeUT)
            {
                InitialLoadDone = initialLoadDone;
                Rewinding = rewinding;
                ReFlyInvoke = reFlyInvoke;
                DiscardReFlyTarget = discardReFlyTarget;
                Handoff = handoff;
                ColdReason = coldReason;
                SaveFileLoad = saveFileLoad;
                RevertPending = revertPending;
                IntentAgeFrames = intentAgeFrames;
                IntentNodeUT = intentNodeUT;
            }
        }

        /// <summary>The once-per-load <see cref="LoadReconcilePolicy"/> classification line.</summary>
        private static void LogLoadClassification(
            EarlyLoadKind early, LoadKind refined, LoadClassificationFacts facts)
        {
            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                LoadReconcilePolicy.FormatClassificationLine(
                    early, refined, HighLogic.LoadedScene.ToString(),
                    facts.InitialLoadDone, facts.Rewinding, facts.ReFlyInvoke, facts.DiscardReFlyTarget,
                    facts.Handoff, facts.ColdReason, facts.SaveFileLoad, facts.RevertPending,
                    facts.IntentAgeFrames, facts.IntentNodeUT));
        }

        /// <summary>
        /// Detects loading a different save game (not a revert) and resets session state
        /// if the save folder changed.
        /// </summary>
        private void DetectSaveFolderChange()
        {
            string currentSave = HighLogic.SaveFolder;
            scenarioSaveFolder = currentSave;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                committedTreeStateLoaded = false;
                lastSaveFolder = currentSave;
                // Genuine new-session boundary: re-arm the drawdown-guard clamp toast so a
                // persistent leak in a DIFFERENT save toasts once again. A plain scene
                // change within the same save does NOT reset these latches (plan §9).
                KspStatePatcher.ResetDrawdownGuardSessionLatches();
                InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropSaveFolderChanged);
                ScenarioLog($"[Parsek Scenario] Save folder changed to '{currentSave}' — resetting session state");
            }
        }

        /// <summary>
        /// Loads crew replacement mappings, kerbal slots, and group hierarchy from the save node.
        /// Skipped during rewind since in-memory state is the source of truth.
        /// </summary>
        /// <summary>
        /// Installs the stock pool reference the loaded node carries when the policy cell
        /// says the save owns it (every load kind today); otherwise keeps memory's and logs
        /// why. See <see cref="StockPoolReference.LoadFrom"/>.
        /// </summary>
        private static void LoadStockPoolReference(ConfigNode node, EarlyLoadKind earlyLoadKind)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.DecideEarly(
                earlyLoadKind, LoadStateCategory.StockPoolReference);
            if (decision.Action == LoadReconcileAction.Save)
            {
                StockPoolReference.LoadFrom(node, earlyLoadKind.ToString());
                return;
            }
            ParsekLog.Info(StockPoolReference.Tag,
                $"Load ({earlyLoadKind}): stock pool reference kept from memory ({decision.Action}): " +
                $"funds={StockPoolReference.Describe(StockPoolReference.Funds)} " +
                $"science={StockPoolReference.Describe(StockPoolReference.Science)} " +
                $"reputation={StockPoolReference.Describe(StockPoolReference.Reputation)}");
        }

        /// <summary>
        /// Takes the position <see cref="LoadReconcilePolicy"/> holds on the replay-scope latch
        /// (<see cref="LoadStateCategory.PlaybackReplayScope"/>): a cold load installs the save's
        /// copy (the reset cleared memory's), a Re-Fly start adds the RP quicksave's copy, and
        /// every other load keeps memory's (a plain rewind adds its rewind save's copy in
        /// <c>RecordingStore.ExecuteRewindSaveLoad</c>, its OnLoad node being persistent.sfs).
        /// </summary>
        private static void LoadPlaybackReplayScope(ConfigNode node, EarlyLoadKind earlyLoadKind)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.DecideEarly(
                earlyLoadKind, LoadStateCategory.PlaybackReplayScope);
            if (decision.Action == LoadReconcileAction.Save)
            {
                PlaybackScopeTracker.InstallFromSave(node, "load " + earlyLoadKind);
                return;
            }
            if (earlyLoadKind == EarlyLoadKind.ReFlyStart)
            {
                PlaybackScopeTracker.UnionFromSave(node, "load " + earlyLoadKind);
                return;
            }
            ParsekLog.Verbose("PlaybackScope", string.Format(CultureInfo.InvariantCulture,
                "Load ({0}): replay scope kept from memory ({1}): inScope={2} seenBeforeActivation={3}",
                earlyLoadKind, decision.Action, PlaybackScopeTracker.InReplayScopeCount,
                PlaybackScopeTracker.SeenBeforeActivationCount));
        }

        /// <summary>
        /// Installs the ledger held-through UT the loaded node carries when the policy cell says
        /// the save owns it, resolved against the loaded save's own clock
        /// (<see cref="LedgerHeldWindow.InstallAfterLoad"/>); a plain rewind clears it here and
        /// <see cref="HandleRewindOnLoad"/> sets its own.
        /// </summary>
        private static void LoadLedgerHeldWindow(ConfigNode node, EarlyLoadKind earlyLoadKind)
        {
            LoadReconcileDecision decision = LoadReconcilePolicy.DecideEarly(
                earlyLoadKind, LoadStateCategory.LedgerHeldWindow);
            if (decision.Action == LoadReconcileAction.Save)
            {
                LedgerHeldWindow.InstallAfterLoad(
                    ReadLoadedFlightStateUTRaw(), LedgerHeldWindow.ReadSaved(node), "Load (" + earlyLoadKind + ")");
                return;
            }
            LedgerHeldWindow.Clear("load " + earlyLoadKind + " (" + decision.Action + ")");
        }

        private static void LoadCrewAndGroupState(ConfigNode node)
        {
            if (RewindContext.IsRewinding)
                return;

            CrewReservationManager.LoadCrewReplacements(node);
            LedgerOrchestrator.Initialize();
            var slotSummary = LedgerOrchestrator.Kerbals != null
                ? LedgerOrchestrator.Kerbals.LoadSlots(node)
                : default(KerbalSlotLoadSummary);
            KerbalLoadRepairDiagnostics.RecordSlotLoad(slotSummary);

            if (!ShouldLoadGroupHierarchyFromSave(initialLoadDone, RewindContext.IsRewinding))
            {
                ParsekLog.Verbose("Scenario",
                    "OnLoad: preserving in-memory group hierarchy for in-session load");
                return;
            }

            GroupHierarchyStore.LoadGroupHierarchy(node);
            GroupHierarchyStore.LoadHiddenGroups(node);
        }

        internal static bool ShouldLoadGroupHierarchyFromSave(bool initialLoadDone, bool isRewinding)
        {
            return !isRewinding && !initialLoadDone;
        }

        /// <summary>
        /// Loads the game state (events, baselines, milestones, ledger, pending science) from
        /// the save's GAME_STATE node, or from the external files for a save without one, and
        /// cleans up stale rewind temp files. Only called on initial load (not revert/scene
        /// change).
        /// </summary>
        private static void LoadExternalFiles(ConfigNode node)
        {
            ParsekLog.Verbose("Scenario", "OnLoad: initial load - loading game state");
            // The ledger is skipped during a rewind (the in-memory ledger is the source of truth).
            LoadGameStateFromNodeOrFiles(node);

            // Clean up stale parsek_rw_*.sfs temp files left by a crash during rewind
            try
            {
                string savesDir = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "", "saves", HighLogic.SaveFolder ?? "");
                if (System.IO.Directory.Exists(savesDir))
                {
                    string[] staleFiles = System.IO.Directory.GetFiles(savesDir, "parsek_rw_*.sfs");
                    for (int s = 0; s < staleFiles.Length; s++)
                    {
                        try
                        {
                            System.IO.File.Delete(staleFiles[s]);
                            ScenarioLog($"[Parsek Scenario] Deleted stale rewind temp file: {System.IO.Path.GetFileName(staleFiles[s])}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ScenarioLog($"[Parsek Scenario] Failed to scan for stale rewind temp files: {ex.Message}");
            }

            budgetDeductionApplied = false;
        }

        /// <summary>
        /// The stock difficulty dialog closed: the allow-negative switch's lock mark is taken
        /// off, and when the dialog applied its changes the live value is held to the career's
        /// lock (<see cref="CareerSettingsLock"/>; the greyed switch should leave it equal).
        /// </summary>
        private void OnDifficultySettingsDismissed(DifficultyOptionsMenu menu, bool applied)
        {
            ParsekLog.Verbose("CareerSettings", $"Difficulty dialog closed: applied={applied}");
            Patches.AllowNegativeCurrencyLockPatch.EndDialog("dismissed");
            if (!applied)
                return;
            CareerSettingsLock.EnsureAndEnforce(KspStatePatcher.ReadLiveAllowNegativeCurrency(), "difficulty-dialog");
        }

        /// <summary>
        /// The OnLoad <c>tree-mutable-state</c> reset (in-session branch: revert, scene
        /// change, quickload). Rolls back uncommitted continuation data and zeroes the
        /// spawn-tracking fields of every committed tree recording; the
        /// <c>tree-state-restore</c> loop that follows puts back what the saved
        /// <c>RECORDING_TREE</c> nodes say.
        ///
        /// <para>Terminal verdicts are deliberately left alone. A committed recording's
        /// <c>TerminalStateValue</c> is recorded content: real-vessel terminal events only
        /// stamp the pending tree (<c>UpdateRecordingsForTerminalEvent</c>), so nothing a
        /// spawned vessel does after commit can reach it, and a Destroyed / Recovered
        /// verdict is never spawnable anyway. Retracting it on reset erased genuine
        /// endings (overwhelmingly debris, whose <c>VesselSpawned</c> is only the
        /// crew-auto-unreserve marker) and made the trajectory's end read as a live,
        /// spawnable vessel. The rewind path
        /// (<c>RecordingStore.ResetRecordingPlaybackFields</c>) follows the same rule.</para>
        ///
        /// Returns the number of tree recordings reset.
        /// </summary>
        internal static int ResetTreeRecordingMutableStateForLoad(IReadOnlyList<Recording> recordings)
        {
            if (recordings == null) return 0;
            int reset = 0;
            int keptTerminal = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null || !rec.IsTreeRecording) continue;

                rec.VesselSpawned = false;
                rec.SpawnAttempts = 0;
                rec.SpawnDeathCount = 0;
                rec.SpawnedVesselPersistentId = 0;
                TerminalOrbitSpawnSafety.Clear(rec);

                rec.LastAppliedResourceIndex = -1;
                reset++;
                if (rec.TerminalStateValue.HasValue) keptTerminal++;
            }
            ParsekLog.Verbose("Scenario",
                "OnLoad tree-mutable-state: reset spawn tracking on " +
                reset.ToString(CultureInfo.InvariantCulture) + " tree recording(s); " +
                keptTerminal.ToString(CultureInfo.InvariantCulture) +
                " recorded terminal verdict(s) kept");
            return reset;
        }
    }
}
