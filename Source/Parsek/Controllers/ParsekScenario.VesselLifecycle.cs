using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Vessel lifecycle GameEvents: recovery, termination, the terminal-state stamp on the
    // recordings and the crew reservations a recovery closes.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Subscribes to vessel lifecycle events (recovery, termination, switching).
        /// Removes existing subscriptions first to avoid duplicates on revert/scene change.
        /// </summary>
        private void SubscribeVesselLifecycleEvents()
        {
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessing);
            GameEvents.onVesselRecoveryProcessingComplete.Remove(OnVesselRecoveryProcessingComplete);
            GameEvents.onVesselRecoveryProcessingComplete.Add(OnVesselRecoveryProcessingComplete);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.OnVesselRecoveryRequested.Remove(OnVesselRecoveryRequested);
            GameEvents.OnVesselRecoveryRequested.Add(OnVesselRecoveryRequested);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            // Career settings audit 3.4: note an allow-negative-currency change when the stock
            // difficulty dialog closes with its changes applied.
            GameEvents.OnDifficultySettingsDismiss.Remove(OnDifficultySettingsDismissed);
            GameEvents.OnDifficultySettingsDismiss.Add(OnDifficultySettingsDismissed);
            // M4b Phase B2 (plan D11 / OQ7): clear the RAM-only cargo escrow on any
            // within-game scene change. The in-cycle reservation is a within-tick /
            // dispatch-to-window-phase guard recomputed from pending route state on
            // the next RouteOrchestrator.Tick, so it need not survive a scene change;
            // dropping it here avoids a stale craft-baked-pid reservation mis-gating
            // a competing route after the scene reloads. Idempotent (Remove then Add).
            GameEvents.onGameSceneSwitchRequested.Remove(OnGameSceneSwitchClearEscrow);
            GameEvents.onGameSceneSwitchRequested.Add(OnGameSceneSwitchClearEscrow);
            // #434: subscribe idempotently so revert detection is armed from first OnLoad.
            RevertDetector.Subscribe();
            // Re-fly Esc-menu button gate: Apply() on every onFlightReady so a
            // save loaded with an active re-fly marker re-enables the Revert
            // button before the player can click it.
            ReFlyRevertButtonGate.Subscribe();
        }

        private void OnVesselRecoveryProcessing(
            ProtoVessel pv,
            KSP.UI.Screens.MissionRecoveryDialog recoveryDialog,
            float recoveryFactor)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostOrDroneVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            if (string.IsNullOrEmpty(pv.vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            bool hasFundsEarned = recoveryDialog != null;
            double fundsEarned = hasFundsEarned ? recoveryDialog.fundsEarned : double.NaN;
            double beforeMissionFunds = hasFundsEarned ? recoveryDialog.beforeMissionFunds : double.NaN;
            double totalFunds = hasFundsEarned ? recoveryDialog.totalFunds : double.NaN;

            RecoveryPayoutContext context = RecoveryPayoutContextStore.Remember(
                pv.persistentId,
                pv.vesselName,
                pv.vesselType,
                now,
                recoveryFactor,
                hasFundsEarned,
                fundsEarned,
                beforeMissionFunds,
                totalFunds);

            if (context == null)
                return;

            ParsekLog.Verbose("Scenario",
                $"Recovery processing captured: {context.Identity.FormatForLog()} " +
                $"pid={pv.persistentId} vesselType={pv.vesselType} " +
                $"ut={now.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"fundsEarned={(context.HasFundsEarned ? fundsEarned.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"before={(hasFundsEarned ? beforeMissionFunds.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"total={(hasFundsEarned ? totalFunds.ToString("F1", CultureInfo.InvariantCulture) : "(unknown)")} " +
                $"recoveryFactor={recoveryFactor.ToString("R", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Second half of the recovery payout capture: fills in the expectation stock could
        /// not yet supply at <see cref="OnVesselRecoveryProcessing"/>.
        ///
        /// <para>
        /// Decompile-verified (KSP 1.12.5 <c>VesselRecovery.OnVesselRecovered</c>): this event
        /// is fired as the method's LAST statement, immediately after <c>totalFunds</c> is
        /// stamped and the currency-modifier delta is folded into <c>fundsEarned</c>. That is
        /// the first moment the dialog's funds triple is internally coherent - at the
        /// processing seam <c>fundsEarned</c> has never been assigned and <c>totalFunds</c> is
        /// still zero, so the context Remember() built there is deliberately payout-unknown.
        /// </para>
        ///
        /// <para>
        /// KSP also fires this event with a NULL dialog on its early-out and <c>quick</c>
        /// recovery paths; those carry no payout at all and correctly leave the context
        /// unknown, which routes to deferred pairing against the real
        /// <c>FundsChanged(VesselRecovery)</c> event.
        /// </para>
        /// </summary>
        private void OnVesselRecoveryProcessingComplete(
            ProtoVessel pv,
            KSP.UI.Screens.MissionRecoveryDialog recoveryDialog,
            float recoveryFactor)
        {
            if (pv == null) return;
            if (recoveryDialog == null) return;
            if (GhostMapPresence.IsGhostOrDroneVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            if (string.IsNullOrEmpty(pv.vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            RecoveredVesselIdentity identity = RecoveredVesselIdentity.FromRawName(pv.vesselName);

            if (!RecoveryPayoutContextStore.TryRefreshPayoutFromCompletion(
                    pv.persistentId,
                    identity,
                    now,
                    recoveryDialog.fundsEarned,
                    recoveryDialog.beforeMissionFunds,
                    recoveryDialog.totalFunds,
                    out RecoveryPayoutContext refreshed))
            {
                return;
            }

            ParsekLog.Verbose("Scenario",
                $"Recovery processing completed: {refreshed.Identity.FormatForLog()} " +
                $"pid={pv.persistentId} vesselType={pv.vesselType} " +
                $"ut={now.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"fundsEarned={recoveryDialog.fundsEarned.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"before={recoveryDialog.beforeMissionFunds.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"total={recoveryDialog.totalFunds.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"recoveryFactor={recoveryFactor.ToString("R", CultureInfo.InvariantCulture)} - " +
                $"payout expectation now known");
        }

        /// <summary>
        /// The flight-scene Recover button (and kRPC's <c>Vessel.Recover</c>): stock fires
        /// this in FLIGHT before it saves and loads the Space Center, where the vessel is
        /// actually recovered. Arms <see cref="InFlightRecoveryRequest"/> so the scene-exit
        /// finalize commits the recording Recovered (operator ruling 2026-10-01). When
        /// stock's own listener ran first, the tree is already finalized and pending, so
        /// the request is applied to it here.
        /// </summary>
        private void OnVesselRecoveryRequested(Vessel v)
        {
            if (v == null) return;
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Info("Scenario",
                    $"Ignoring recovery request for '{v.vesselName}' during rewind");
                return;
            }

            string guid = v.id != Guid.Empty
                ? v.id.ToString("N", CultureInfo.InvariantCulture)
                : null;
            var flight = ParsekFlight.Instance;
            string boundRecordingId = flight != null
                ? flight.LiveRecordingIdForVessel(v.persistentId)
                : null;
            float labStoredScience = 0f;
            try
            {
                labStoredScience = Patches.ScienceLabCreditCapture.SumLiveStoredScience(v);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    $"Recovery request: reading lab science for '{v.vesselName}' threw: {ex.Message}");
            }
            List<string> crewNames = null;
            try
            {
                var crew = v.GetVesselCrew();
                crewNames = new List<string>();
                if (crew != null)
                {
                    for (int i = 0; i < crew.Count; i++)
                    {
                        if (crew[i] != null && !string.IsNullOrEmpty(crew[i].name))
                            crewNames.Add(crew[i].name);
                    }
                }
            }
            catch (Exception ex)
            {
                crewNames = null;
                ParsekLog.Warn("Scenario",
                    $"Recovery request: reading the crew of '{v.vesselName}' threw: {ex.Message} - " +
                    "the recording's crew fall back to their start seats");
            }
            InFlightRecoveryRequest.Arm(
                v.persistentId, guid, v.vesselName, Planetarium.GetUniversalTime(),
                boundRecordingId, labStoredScience, crewNames);

            if (PendingTreeSlot.HasPendingTree
                && PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized)
            {
                InFlightRecoveryRequest.TryApplyToFinalizedPendingTree(
                    PendingTreeSlot.PendingTree, "recovery-request pending tree");
            }
        }

        /// <param name="quick">Stock's quick flag (<c>VesselRecovery.OnVesselRecovered</c>): false on
        /// every player route (Tracking Station, VesselRetrieval, KSC marker), true only from
        /// <c>ProtoVessel.Clean</c>. It does not name the recovery route.</param>
        private void OnVesselRecovered(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostOrDroneVessel(pv.persistentId)) return;

            // During rewind, vessels are stripped from the save which fires onVesselRecovered.
            // Ignore these — the recordings must keep their snapshots for ghost playback and spawning.
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Info("Scenario",
                    $"Ignoring recovery of '{pv.vesselName}' during rewind");
                return;
            }

            // A recovered flag is never planted again by a replay (todo
            // LOOP-REPLAY-REPLANTS-REMOVED-FLAG).
            NoteFlagProtoRemoved(pv, "recovered");

            // KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: carry the live launch guid
            // on the identity. It rides all the way into
            // LedgerOrchestrator.PickRecoveryRecordingId, including through the DEFERRED
            // recovery-funds queue (which stores this struct verbatim), so a recovery paired
            // against a later FundsChanged(VesselRecovery) event still filters by launch. The
            // payout-context lookup below still pairs by name (both sides are this one live
            // vessel); the pending-tree terminal update and the pending-owner check match by
            // pid + this guid (MatchesVessel), falling back to the name only when either
            // guid is unknown.
            // Stage 3: the pid rides along too, read by the picker ONLY as a genuine Parsek
            // spawn identity (a spawned vessel's fresh guid matches no recording).
            RecoveredVesselIdentity identity = RecoveredVesselIdentity.FromRawName(
                pv.vesselName, VesselLaunchIdentity.ReadLaunchGuid(pv), pv.persistentId);
            if (!identity.HasName) return;

            double now = Planetarium.GetUniversalTime();
            RecoveryPayoutContextStore.TryFind(
                pv.persistentId,
                identity,
                now,
                out RecoveryPayoutContext payoutContext);

            // The crew stock recovers with the vessel, read before anything drops the end
            // snapshot: a kerbal named here reads Recovered whatever seat he started in.
            List<string> recoveredCrew = ReadRecoveredCrewNames(pv, identity);
            bool updated = UpdateRecordingsForTerminalEvent(
                identity, TerminalState.Recovered, now, pv.persistentId, recoveredCrew);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{identity.DisplayName}' recovered — recording(s) updated with Recovered terminal state");

            // Owner rulings Q13 / Q13b: a COMMITTED recording of this launch that ends in a
            // crash Parsek only predicted (at any UT), or a predicted chute touchdown recovered
            // before the touchdown, follows the recovery (stock still had the vessel). Before
            // the ledger rows below, so the funds pick, the crew close and the VesselRecovered
            // row read the Recovered ending. Parsek's own programmatic recoveries are not the
            // player's outcome.
            if (!GameStateRecorder.SuppressCrewEvents)
                ReconcileRecoveredCommittedPredictions(VesselLaunchIdentity.ReadLaunchGuid(pv), now, recoveredCrew);

            // #444: when recovery happens outside the Flight scene (tracking station or
            // post-flight summary at KSC), KSP's FundsChanged(VesselRecovery) event lies
            // between the committed recording's points window — CreateVesselCostActions
            // never sees it and the funds get silently dropped from the ledger. Route the
            // payout into the ledger as a real-time FundsEarning(Recovery) action tagged
            // with the matching committed recording (or null for non-Parsek vessels).
            // In-flight recovery is already covered by the terminal-state path above:
            // UpdateRecordingsForTerminalEvent flips the live recording to Recovered, and
            // the later commit now pairs the FundsChanged(VesselRecovery) event near the
            // recording end UT. Only patch immediately when no pending-tree recording
            // still owns this vessel; otherwise the commit-time path should emit the
            // recovery action exactly once.
            // Inside stock's recovery dispatch for this very vessel (KSC-DEBRIS-CLEANUP-REFUND-
            // UNLEDGERED), stock's VesselRecovery listener runs after this one and pays in the
            // same call, so a debris recovery with no payout context yet is deferred, not skipped.
            uint dispatchPid = RecoveryDispatchScope.IsOpen
                && RecoveryDispatchScope.CurrentPersistentId == pv.persistentId
                ? pv.persistentId : 0u;
            if (ShouldPatchRecoveryFundsOutsideFlight(HighLogic.LoadedScene, identity, pv.persistentId))
                LedgerOrchestrator.OnVesselRecoveryFunds(
                    now,
                    identity,
                    quick,
                    pv.vesselType,
                    payoutContext,
                    dispatchPid);

            // KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL: the committed flight this
            // vessel continues may hold its crew open-ended (a Tracking Station recovery of an
            // older flight, or a pre-2026-10-01 in-flight Recover committed Landed). Parsek's own
            // housekeeping recoveries run crew-suppressed and are not a kerbal coming home.
            //
            // SPAWNED-VESSEL-RECOVERED-OUTSIDE-FLIGHT-RESPAWNS-ON-SANDBOX: the same filter
            // covers the VesselRecovered row, written independent of crew and game mode so a
            // committed recording whose vessel the player recovered (an uncrewed sandbox
            // Tracking Station recovery leaves neither a funds nor a crew-close row) never
            // spawns it again.
            if (GameStateRecorder.SuppressCrewEvents)
            {
                ParsekLog.Verbose("Scenario",
                    $"Recovery crew reservation close and vessel recovery row skipped for " +
                    $"'{identity.DisplayName}' pid={pv.persistentId}: crew events suppressed " +
                    "(programmatic recovery)");
            }
            else
            {
                CloseCrewReservationsForRecoveredVessel(pv, identity, now);
                LedgerOrchestrator.OnRealVesselRecovered(
                    now,
                    pv.persistentId,
                    VesselLaunchIdentity.ReadLaunchGuid(pv),
                    identity.DisplayName);
            }
        }

        /// <summary>
        /// The names of the kerbals aboard the recovered vessel (the crew stock recovers), or
        /// null when the vessel's crew cannot be read (the recording then falls back to the
        /// per-part rule over the start seats).
        /// </summary>
        private static List<string> ReadRecoveredCrewNames(ProtoVessel pv, RecoveredVesselIdentity identity)
        {
            try
            {
                var crew = pv.GetVesselCrew();
                var names = new List<string>();
                if (crew != null)
                {
                    for (int i = 0; i < crew.Count; i++)
                    {
                        if (crew[i] != null && !string.IsNullOrEmpty(crew[i].name))
                            names.Add(crew[i].name);
                    }
                }
                return names;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    $"Recovered crew read: GetVesselCrew threw for '{identity.DisplayName}': {ex.Message} - " +
                    "the recording's crew fall back to their start seats");
                return null;
            }
        }

        private static void CloseCrewReservationsForRecoveredVessel(
            ProtoVessel pv,
            RecoveredVesselIdentity identity,
            double now)
        {
            var names = new List<string>();
            try
            {
                var crew = pv.GetVesselCrew();
                if (crew != null)
                {
                    for (int i = 0; i < crew.Count; i++)
                    {
                        if (crew[i] != null && !string.IsNullOrEmpty(crew[i].name))
                            names.Add(crew[i].name);
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    $"Recovery crew reservation close: GetVesselCrew threw for " +
                    $"'{identity.DisplayName}': {ex.Message} - skipping");
                return;
            }

            LedgerOrchestrator.OnRealVesselCrewRecovered(
                now,
                pv.persistentId,
                VesselLaunchIdentity.ReadLaunchGuid(pv),
                identity.DisplayName,
                names);
        }

        /// <summary>
        /// Owner ruling Q13, the live shell of <see cref="ApplyRecoveryOfCommittedPredictions"/>:
        /// the trees a live session owns (the flight's active tree, the pending tree) are left
        /// to that session's commit, and nothing is touched while a Re-Fly session runs (its
        /// Merge / Discard decide). Matches by launch guid only.
        /// </summary>
        private void ReconcileRecoveredCommittedPredictions(
            string launchGuid, double now, IEnumerable<string> recoveredCrew)
        {
            if (string.IsNullOrEmpty(launchGuid))
                return;
            if (ActiveReFlySessionMarker != null)
            {
                ParsekLog.Verbose("Scenario",
                    $"Committed predicted-ending recovery skipped for guid={launchGuid}: a Re-Fly session is active");
                return;
            }
            var flight = ParsekFlight.Instance;
            string activeTreeId = flight != null ? flight.ActiveTreeForSerialization?.Id : null;
            string pendingTreeId = PendingTreeSlot.PendingTree?.Id;
            Func<Recording, bool> ownedElsewhere = rec =>
                (activeTreeId != null && string.Equals(rec.TreeId, activeTreeId, StringComparison.Ordinal))
                || (pendingTreeId != null && string.Equals(rec.TreeId, pendingTreeId, StringComparison.Ordinal));
            int converted;
            try
            {
                converted = ApplyRecoveryOfCommittedPredictions(
                    EffectiveState.ComputeERS(), launchGuid, now, ownedElsewhere,
                    "OnVesselRecovered:" + HighLogic.LoadedScene, recoveredCrew);
            }
            catch (Exception ex)
            {
                // The recovery's own ledger rows below must still be written.
                ParsekLog.Warn("Scenario",
                    $"Committed predicted-ending recovery threw for guid={launchGuid}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (converted > 0)
                ParsekLog.Info("Scenario",
                    $"Recovery of guid={launchGuid} at ut={now.ToString("F1", CultureInfo.InvariantCulture)}: " +
                    $"{converted.ToString(CultureInfo.InvariantCulture)} committed predicted ending(s) now end Recovered");
        }

        /// <summary>
        /// The committed-history half of owner ruling Q13: for every recording
        /// <see cref="PredictedEndingRecovery.SelectRecoveryTargets"/> picks, deep-clones its
        /// committed tree, converts the recording on the clone
        /// (<see cref="PredictedEndingRecovery.ConvertToRecovered"/>), commits the clone over the
        /// committed tree (same-id replace, which retires the forecast's crew rows and death
        /// penalty, <see cref="LedgerOrchestrator.ShouldRefreshCrewRowsOnReplace"/>) and files
        /// the tree's ledger rows (the crew Recovered). <paramref name="recoveredCrew"/> is the
        /// recovered vessel's crew (null: not known). A committed tree is untouched when
        /// anything before its commit refuses. Returns the recordings converted.
        /// </summary>
        internal static int ApplyRecoveryOfCommittedPredictions(
            IReadOnlyList<Recording> effectiveRecordings, string launchGuid, double recoveryUT,
            Func<Recording, bool> ownedElsewhere, string context, IEnumerable<string> recoveredCrew = null)
        {
            List<Recording> targets = PredictedEndingRecovery.SelectRecoveryTargets(
                effectiveRecordings, launchGuid, recoveryUT, ownedElsewhere, context);
            int applied = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Recording target = targets[i];
                RecordingTree committedTree = RecordingStore.FindCommittedTreeById(target.TreeId);
                if (committedTree?.Recordings == null
                    || !committedTree.Recordings.TryGetValue(target.RecordingId, out Recording committedRec)
                    || committedRec == null)
                {
                    PredictedEndingRecovery.LogRefused(context, target.TreeId, target.RecordingId, "tree-not-found");
                    continue;
                }
                TerminalState? previous = committedRec.TerminalStateValue;
                bool touchdown = PredictedTouchdownStockFate.IsPredictedTouchdown(committedRec);
                double predictedEndUT = committedRec.EndUT;

                RecordingTree clone = RecordingTree.DeepClone(committedTree);
                if (clone?.Recordings == null
                    || !clone.Recordings.TryGetValue(target.RecordingId, out Recording rec) || rec == null)
                {
                    PredictedEndingRecovery.LogRefused(context, committedTree.Id, target.RecordingId, "clone-failed");
                    continue;
                }
                if (!PredictedEndingRecovery.ConvertToRecovered(
                        rec, recoveryUT, context, out PredictedEndingDropResult drop, out string reason,
                        recoveredCrew))
                {
                    PredictedEndingRecovery.LogRefused(context, committedTree.Id, target.RecordingId, reason);
                    continue;
                }
                if (!TreeCommitService.CommitTree(clone))
                {
                    PredictedEndingRecovery.LogRefused(context, committedTree.Id, target.RecordingId, "commit-refused");
                    continue;
                }
                RecordingTree committed = TreeCommitService.ResolveCommittedTreeAfterCommit(clone, context);
                LedgerOrchestrator.NotifyLedgerTreeCommitted(committed);
                applied++;
                PredictedEndingRecovery.LogApplied(
                    context, committedTree.Id, rec, previous, touchdown, predictedEndUT, recoveryUT, drop.CutUT);
            }
            return applied;
        }

        private void OnVesselTerminated(ProtoVessel pv)
        {
            if (pv == null) return;
            if (GhostMapPresence.IsGhostOrDroneVessel(pv.persistentId)) return;
            if (RewindContext.IsRewinding) return;
            NoteFlagProtoRemoved(pv, "terminated");
            string launchGuid = VesselLaunchIdentity.ReadLaunchGuid(pv);
            RecoveredVesselIdentity identity = RecoveredVesselIdentity.FromRawName(
                pv.vesselName, launchGuid);
            double now = Planetarium.GetUniversalTime();

            // The player removed the vessel: a committed recording it continues (a vessel
            // Parsek spawned at the recording's end, or the recorded launch itself) gets a
            // VesselTerminated row, so the next flight scene does not read the vanished
            // vessel as a spawn death and spawn it again. The pending-tree stamp below cannot
            // reach a committed recording.
            LedgerOrchestrator.OnRealVesselTerminated(
                now, pv.persistentId, launchGuid, identity.DisplayName);

            if (!identity.HasName) return;

            // Stock fires onVesselTerminated only from SpaceTracking.OnVesselDeleteConfirm
            // (Tracking Station Terminate / Delete), never from a recovery. The guard in
            // UpdateRecordingsForTerminalEvent still keeps a Recovered stamp from being
            // overwritten with Destroyed.
            bool updated = UpdateRecordingsForTerminalEvent(
                identity, TerminalState.Destroyed, now, pv.persistentId);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{identity.DisplayName}' terminated — recording(s) updated with Destroyed terminal state");
        }

        /// <summary>
        /// Finds pending-tree recordings of the given vessel and updates their terminal state.
        /// A recording matches by launch identity first (see
        /// <see cref="ClassifyVesselMatch(Recording, RecoveredVesselIdentity, uint)"/>): equal
        /// known launch Guids match under any name (a vessel renamed after its recording
        /// captured the name still gets its stamp), a different known Guid or pid never
        /// matches, and only an unknown Guid falls back to the name. Without the identity gate
        /// a stock KSC-declutter autoclean of an unrelated "&lt;Craft&gt; Debris" stamped every
        /// same-named pending debris recording Recovered and dropped its snapshot.
        /// Recovered/Destroyed can overwrite situation-based terminal states (Orbiting, Landed, etc.)
        /// that were set by OnSceneChangeRequested. Only prevents Destroyed from overwriting Recovered.
        /// </summary>
        internal static bool UpdateRecordingsForTerminalEvent(string vesselName, TerminalState state, double ut)
        {
            return UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(vesselName),
                state,
                ut);
        }

        internal static bool UpdateRecordingsForTerminalEvent(
            RecoveredVesselIdentity identity,
            TerminalState state,
            double ut,
            uint vesselPid = 0,
            IEnumerable<string> recoveredCrew = null)
        {
            bool anyUpdated = false;

            // Check pending tree recordings
            if (PendingTreeSlot.HasPendingTree)
            {
                var pendingTree = PendingTreeSlot.PendingTree;
                int stampedCount = 0;
                int skippedEarlierSegmentCount = 0;
                int skippedReplacedCount = 0;
                int stampedRenamedCount = 0;
                var scenario = Instance;
                IReadOnlyList<RecordingSupersedeRelation> supersedes =
                    object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes;
                foreach (var rec in pendingTree.Recordings.Values)
                {
                    VesselMatchKind matchKind = ClassifyVesselMatch(rec, identity, vesselPid);
                    if (matchKind == VesselMatchKind.None
                        || !CanOverwriteTerminalState(rec.TerminalStateValue, state, rec.TerminalPredicted))
                        continue;

                    // An earlier segment of the same vessel (split parent, dock dominant
                    // parent, background or switch continuation) shares name + pid + guid
                    // with the recovered vessel but ended when its continuation began.
                    // Stamping it would stretch its EndUT to the event UT, drop its
                    // snapshot, and pair the same recovery funds event a second time at
                    // commit (AddVesselRecoveryCostActions keys by recording id).
                    if (IsReplacedByReFly(rec, pendingTree, supersedes))
                    {
                        skippedReplacedCount++;
                        continue;
                    }
                    if (!IsTerminalEventTarget(rec, pendingTree, supersedes))
                    {
                        skippedEarlierSegmentCount++;
                        continue;
                    }

                    if (matchKind == VesselMatchKind.LaunchIdentity
                        && !identity.MatchesName(rec.VesselName))
                        stampedRenamedCount++;
                    ApplyTerminalEventStamp(rec, state, ut, "UpdateRecordingsForTerminalEvent", recoveredCrew);
                    anyUpdated = true;
                    stampedCount++;
                    ParsekLog.Verbose("Scenario", $"Updated pending tree recording '{rec.VesselName}' with {state}");
                }

                if (stampedRenamedCount > 0)
                {
                    ParsekLog.Info("Scenario", string.Format(CultureInfo.InvariantCulture,
                        "UpdateRecordingsForTerminalEvent: vessel='{0}' pid={1} state={2} " +
                        "stampedByLaunchIdentity={3} under a different recorded name " +
                        "(matched by launch guid; a vessel name is not its identity)",
                        identity.DisplayName, vesselPid, state, stampedRenamedCount));
                }

                if (skippedReplacedCount > 0)
                {
                    ParsekLog.Info("Scenario", string.Format(CultureInfo.InvariantCulture,
                        "UpdateRecordingsForTerminalEvent: vessel='{0}' pid={1} state={2} stamped={3} " +
                        "skippedReplacedByReFly={4} (a recording a Re-Fly replaced keeps its own end)",
                        identity.DisplayName, vesselPid, state, stampedCount, skippedReplacedCount));
                }

                if (skippedEarlierSegmentCount > 0)
                {
                    ParsekLog.Info("Scenario", string.Format(CultureInfo.InvariantCulture,
                        "UpdateRecordingsForTerminalEvent: vessel='{0}' pid={1} state={2} stamped={3} " +
                        "skippedEarlierSegments={4} (same-vessel segments with a continuation keep their own end)",
                        identity.DisplayName, vesselPid, state, stampedCount, skippedEarlierSegmentCount));
                }
            }

            // Committed recordings are never modified by terminal events. Recovery or
            // destruction of a real vessel (whether spawned by Parsek or pre-existing) must
            // not alter frozen recording data - snapshot, terminal state, crew, or EndUT.
            // Any mutation would persist through reverts, permanently preventing re-spawn.

            return anyUpdated;
        }

        /// <summary>
        /// The stamp a recovery / termination event applies to a NOT-YET-COMMITTED
        /// recording: end at the event UT, release the snapshot's crew, drop the vessel
        /// snapshot, stamp the terminal. Shared by the pending-tree path above and the
        /// in-flight Recover path (<see cref="InFlightRecoveryRequest"/>), so a recording
        /// committed after either reads exactly the same. <paramref name="recoveredCrew"/> (a
        /// recovery only) is the recovered vessel's crew, carried on the stamp because the
        /// snapshot that names the kerbals' last seats is dropped here.
        /// </summary>
        internal static void ApplyTerminalEventStamp(
            Recording rec, TerminalState state, double ut, string source,
            IEnumerable<string> recoveredCrew = null)
        {
            if (rec == null) return;
            if (state == TerminalState.Recovered
                && rec.TerminalStateValue == TerminalState.Destroyed && rec.TerminalPredicted)
            {
                // The vessel a predicted ending gave up on was recovered: it was never
                // destroyed, and the Recovered stamp below re-infers its crew as recovered.
                rec.VesselDestroyed = false;
                ParsekLog.Info("Scenario",
                    $"{source ?? "(none)"}: predicted Destroyed of '{rec.RecordingId ?? "(null)"}' " +
                    $"overwritten by Recovered at ut={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(remnantSurvives={rec.TerminalRemnantSurvives}; KSP still had the vessel and the player recovered it)");
            }
            rec.ExplicitEndUT = ut;
            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            // Snapshot first, stamp second: CanOverwriteTerminalState
            // deliberately allows Landed/Orbiting/Splashed/SubOrbital ->
            // Recovered|Destroyed, and that is exactly the transition the
            // crew-end-state seam must re-infer against - it has to judge
            // re-derivability against the surface that survives this block
            // (the ghost snapshot), not the snapshot being dropped here.
            rec.VesselSnapshot = null;
            rec.StampTerminalState(state, source,
                recoveredCrew: state == TerminalState.Recovered ? recoveredCrew : null);
        }

        /// <summary>
        /// True when a matching pending-tree recording is the vessel's current tip and so
        /// the target of a recovery / termination stamp: a recording with no child branch
        /// point, or a breakup-continuous recording whose branch point has no same-pid
        /// child (<see cref="GhostPlaybackLogic.IsEffectiveLeafForVessel(Recording, RecordingTree)"/>,
        /// #224). A recording whose branch point continued the same vessel as another
        /// recording is an earlier segment and is left alone.
        /// </summary>
        internal static bool IsTerminalEventTarget(Recording rec, RecordingTree tree)
        {
            var scenario = Instance;
            return IsTerminalEventTarget(rec, tree,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes);
        }

        /// <summary>
        /// <see cref="IsTerminalEventTarget(Recording, RecordingTree)"/> with the supersede
        /// relations passed in. A recording a Re-Fly replaced is never the target either: the
        /// in-place fork is a SIBLING of the slot's prior tip with the same pid + launch guid,
        /// and both are leaves of the session tree, so without this a recovery stamped the
        /// replaced prior tip too (stretching its end to the recovery UT, dropping its
        /// snapshot, and pairing the recovery funds event a second time at commit). During the
        /// session the fork names the replaced recording in <c>SupersedeTargetId</c>; after a
        /// merge the replaced recording is superseded by relation.
        /// </summary>
        internal static bool IsTerminalEventTarget(
            Recording rec, RecordingTree tree, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null)
                return false;
            if (IsReplacedByReFly(rec, tree, supersedes))
                return false;
            if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                return true;
            return GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, tree);
        }

        /// <summary>
        /// True when another recording of <paramref name="tree"/> names <paramref name="rec"/>
        /// as its <c>SupersedeTargetId</c> (a live Re-Fly fork), or a supersede relation
        /// replaces it.
        /// </summary>
        internal static bool IsReplacedByReFly(
            Recording rec, RecordingTree tree, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (tree?.Recordings != null)
            {
                foreach (var other in tree.Recordings.Values)
                {
                    if (other == null || object.ReferenceEquals(other, rec))
                        continue;
                    if (string.Equals(other.SupersedeTargetId, rec.RecordingId, StringComparison.Ordinal)
                        && !string.Equals(other.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                        return true;
                }
            }
            return EffectiveState.IsSupersededByRelation(rec, supersedes);
        }

        /// <summary>
        /// Returns true when an outside-FLIGHT recovery should be patched into the
        /// ledger immediately. Pending-tree recordings that still own the vessel are
        /// handled at commit time by CreateVesselCostActions, which now pairs the
        /// FundsChanged(VesselRecovery) event near the recording end UT.
        /// </summary>
        internal static bool ShouldPatchRecoveryFundsOutsideFlight(GameScenes scene, string vesselName)
        {
            return ShouldPatchRecoveryFundsOutsideFlight(
                scene,
                RecoveredVesselIdentity.FromRawName(vesselName));
        }

        internal static bool ShouldPatchRecoveryFundsOutsideFlight(
            GameScenes scene,
            RecoveredVesselIdentity identity,
            uint vesselPid = 0)
        {
            return scene != GameScenes.FLIGHT &&
                   !HasPendingLedgerRecordingForVessel(identity, vesselPid);
        }

        /// <summary>
        /// Returns true when a pending-tree recording still owns the named vessel and
        /// will later contribute ledger actions on commit.
        /// </summary>
        internal static bool HasPendingLedgerRecordingForVessel(
            RecoveredVesselIdentity identity,
            uint vesselPid = 0)
        {
            if ((!identity.HasName && !identity.HasLaunchGuid) || !PendingTreeSlot.HasPendingTree)
                return false;

            foreach (var rec in PendingTreeSlot.PendingTree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (MatchesVessel(rec, identity, vesselPid))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a new terminal state can overwrite the existing one.
        /// Recovered/Destroyed can overwrite null or situation-based states (Orbiting, Landed, etc.).
        /// Destroyed cannot overwrite Recovered (a guard: stock fires onVesselTerminated only
        /// from the Tracking Station delete confirm, never after a recovery). Disassembled,
        /// Docked and Boarded are final too.
        /// </summary>
        internal static bool CanOverwriteTerminalState(
            TerminalState? existing, TerminalState incoming, bool existingPredicted = false)
        {
            if (!existing.HasValue) return true;

            // Recovered and Destroyed are "final" — only Recovered blocks further overwrite
            if (existing.Value == TerminalState.Recovered) return false;
            // A Destroyed Parsek only predicted (Recording.TerminalPredicted: a crash forecast
            // while KSP still had the vessel, or an identity loss over a remnant KSP keeps) is
            // not final against a recovery: KSP still had the vessel and the player recovered
            // it, so the recovery is what happened and the crew come home.
            if (existing.Value == TerminalState.Destroyed)
                return existingPredicted && incoming == TerminalState.Recovered;

            // Disassembled is final for the same reason, and has to be named separately
            // because the caller's match (MatchesVessel) still falls back to the VESSEL NAME
            // when a launch guid is unknown. A pocketed debris recording keeps the name of the
            // craft the part came off, so a later recovery of a live vessel sharing that name
            // could otherwise reach it, replace the verdict AND null its VesselSnapshot. Not cosmetic: Recovered is
            // exactly what LedgerOrchestrator.AddVesselRecoveryCostActions and
            // ResurrectionRetirementEligibility key on, so the overwrite would start
            // paying recovery funds for a part that was merely put in a pocket.
            if (existing.Value == TerminalState.Disassembled) return false;

            // Docked and Boarded end the vessel by merging it into another one: what is
            // recovered or destroyed later is that other vessel, reached here only through the
            // name fallback or the shared pid of the absorbed craft. Overwriting would stamp a
            // dock or a boarding as a recovery (funds paired at commit) and drop the snapshot.
            // An ending this build does not know (held verbatim from a newer build) is final:
            // only the build that wrote it knows what it means.
            if (!Enum.IsDefined(typeof(TerminalState), existing.Value))
            {
                ParsekLog.Verbose("Scenario",
                    $"CanOverwriteTerminalState: refused existing={(int)existing.Value} incoming={incoming} " +
                    "(an ending unknown to this build, held from a newer build, is final)");
                return false;
            }

            if (existing.Value == TerminalState.Docked || existing.Value == TerminalState.Boarded)
            {
                ParsekLog.Verbose("Scenario",
                    $"CanOverwriteTerminalState: refused existing={existing.Value} incoming={incoming} " +
                    "(the vessel merged into another one; its ending is final)");
                return false;
            }

            // Situation-based states (Orbiting, Landed, Splashed, SubOrbital) can be overwritten
            // by Recovered or Destroyed (lifecycle events take precedence over scene-exit situation)
            return true;
        }

        /// <summary>
        /// Checks if a recording is the recovered / terminated vessel
        /// (<see cref="ClassifyVesselMatch"/>): a positive launch-Guid match (pids not
        /// disagreeing) is sufficient whatever the names; the name is required only when a
        /// launch Guid is unknown on either side.
        /// </summary>
        internal static bool MatchesVessel(
            Recording rec,
            RecoveredVesselIdentity identity,
            uint vesselPid = 0)
        {
            return ClassifyVesselMatch(rec, identity, vesselPid) != VesselMatchKind.None;
        }

        /// <summary>How <see cref="ClassifyVesselMatch"/> tied a recording to a vessel.</summary>
        internal enum VesselMatchKind
        {
            /// <summary>Not the same vessel.</summary>
            None = 0,

            /// <summary>Both launch Guids known and equal (and the pids do not disagree): the
            /// same physical vessel whatever either side is called now.</summary>
            LaunchIdentity = 1,

            /// <summary>A launch Guid is unknown on at least one side (legacy data, or a seam
            /// that could not read it): matched by name, narrowed by a known pid.</summary>
            LegacyName = 2,
        }

        /// <summary>
        /// Identity-first decision behind <see cref="MatchesVessel"/>. A vessel name is an
        /// attribute, never a key (a player can rename a vessel after its recording captured
        /// the name), so:
        /// <list type="number">
        /// <item>a known, non-zero pid on both sides that differs is never a match (the pid is
        /// craft-baked, so equality proves nothing alone, but inequality is conclusive);</item>
        /// <item>when both launch Guids are known they decide alone: equal is a match under
        /// any name, different is never one (a relaunch of the same craft);</item>
        /// <item>only when a Guid is unknown on either side does the name decide, as it did
        /// before launch identity existed.</item>
        /// </list>
        /// Pure; the callers log.
        /// </summary>
        internal static VesselMatchKind ClassifyVesselMatch(
            Recording rec,
            RecoveredVesselIdentity identity,
            uint vesselPid = 0)
        {
            if (rec == null)
                return VesselMatchKind.None;

            if (vesselPid != 0 && rec.VesselPersistentId != 0
                && rec.VesselPersistentId != vesselPid)
                return VesselMatchKind.None;

            string recGuid = VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid);
            string liveGuid = VesselLaunchIdentity.NormalizeGuid(identity.LaunchGuid);
            if (recGuid != null && liveGuid != null)
            {
                return string.Equals(recGuid, liveGuid, StringComparison.OrdinalIgnoreCase)
                    ? VesselMatchKind.LaunchIdentity
                    : VesselMatchKind.None;
            }

            if (!identity.HasName ||
                string.IsNullOrEmpty(rec.VesselName) ||
                !identity.MatchesName(rec.VesselName))
                return VesselMatchKind.None;

            return VesselMatchKind.LegacyName;
        }
    }
}
