using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Per-row Seal action for Unfinished Flights. Seal permanently closes the
    /// slot by flipping its effective chain+supersede tip recording's
    /// MergeState from CommittedProvisional to Immutable (the single
    /// open/closed source of truth). This cannot be undone.
    /// </summary>
    internal static class UnfinishedFlightSealHandler
    {
        internal const string DialogName = "ParsekUFSealDialog";
        internal const string DialogLockId = "ParsekUFSealDialog";

        internal static Func<DateTime> UtcNowForTesting;
        internal static Func<bool> SavePersistentForTesting;
        internal static bool DialogLockActiveForTesting { get; private set; }

        internal static void ResetForTesting()
        {
            UtcNowForTesting = null;
            SavePersistentForTesting = null;
            DialogLockActiveForTesting = false;
        }

        internal static bool TrySeal(Recording rec, out string reason)
        {
            reason = null;
            if (rec == null)
            {
                reason = "recording-null";
                ParsekLog.Error("UnfinishedFlights",
                    "Seal could not resolve slot for rec=<null> reason=recording-null");
                return false;
            }

            RewindPoint rp;
            int slotListIndex;
            string rejectReason;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    rec, out rp, out slotListIndex, out rejectReason)
                || rp?.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
            {
                reason = rejectReason ?? "slot-index-invalid";
                ParsekLog.Error("UnfinishedFlights",
                    $"Seal could not resolve slot for rec={rec.RecordingId ?? "<no-id>"} reason={reason}");
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                reason = "slot-null";
                ParsekLog.Error("UnfinishedFlights",
                    $"Seal could not resolve slot for rec={rec.RecordingId ?? "<no-id>"} reason=slot-null");
                return false;
            }

            // Seal is a permanent CommittedProvisional -> Immutable transition
            // on the slot's effective chain+supersede tip. Open/closed is read
            // from the tip MergeState (the single source of truth), so closing
            // the slot just means flipping the tip to Immutable. Idempotent: a
            // tip already Immutable is a no-op. Tips are disjoint across slots
            // (distinct origins -> distinct tips) so this cannot cross-close a
            // sibling slot.
            var scenarioForSupersedes = ParsekScenario.Instance;
            IReadOnlyList<RecordingSupersedeRelation> sealSupersedes =
                !object.ReferenceEquals(null, scenarioForSupersedes)
                    ? scenarioForSupersedes.RecordingSupersedes
                    : null;
            string tipId = slot.EffectiveRecordingId(sealSupersedes);
            Recording tipRec = FindCommittedRecordingById(tipId);
            if (tipRec == null)
            {
                // The slot resolved to an RP child slot, but its effective
                // chain+supersede tip recording is not in the committed store
                // (dangling supersede edge, or the tip was reaped out from
                // under the slot). Open/closed is read from the tip MergeState,
                // so with no tip there is nothing to flip to Immutable. Report a
                // hard failure instead of silently returning success: otherwise
                // the slot keeps reading as open while the UI claims "Sealed".
                reason = "tip-unresolvable";
                ParsekLog.Error("UnfinishedFlights",
                    $"Seal could not resolve effective tip for rec={rec.RecordingId ?? "<no-id>"} " +
                    $"tip={tipId ?? "<no-tip>"} reason=tip-unresolvable");
                return false;
            }
            MergeState oldState = tipRec.MergeState;
            if (tipRec.MergeState != MergeState.Immutable)
            {
                tipRec.MergeState = MergeState.Immutable;
                tipRec.FilesDirty = true;
            }

            var scenario = ParsekScenario.Instance;
            bool persistedBeforeReap = true;
            if (!object.ReferenceEquals(null, scenario))
            {
                scenario.BumpSupersedeStateVersion();
                persistedBeforeReap = PersistSealBeforeReap();
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.RecordingSupersedes
                    : null;
            bool reapEligible = RewindPointReaper.IsReapEligible(rp, supersedes);
            bool willReap = persistedBeforeReap && reapEligible;
            int reaped = 0;
            if (willReap)
                reaped = RewindPointReaper.ReapOrphanedRPs();
            else if (reapEligible && !persistedBeforeReap)
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Seal deferred RP reap until persistent save succeeds " +
                    $"rec={rec.RecordingId ?? "<no-id>"} rp={rp.RewindPointId ?? "<no-rp>"}");
            }

            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec);
            string terminal = tip?.TerminalStateValue.HasValue == true
                ? tip.TerminalStateValue.Value.ToString()
                : "<none>";
            string impact = willReap
                ? "willReap"
                : (reapEligible ? "deferredPersistence" : "stillBlocked");
            MergeState newState = tipRec.MergeState;

            ParsekLog.Info("UnfinishedFlights",
                $"Sealed slot={slotListIndex} rec={rec.RecordingId ?? "<no-id>"} " +
                $"bp={rp.BranchPointId ?? "<no-bp>"} rp={rp.RewindPointId ?? "<no-rp>"} " +
                $"tip={tipId ?? "<no-tip>"} mergeState={oldState}->{newState} " +
                $"terminal={terminal} reaperImpact={impact} reaped={reaped}");
            return true;
        }

        private static Recording FindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Open/closed is read from the slot's effective tip MergeState;
            // EffectiveState owns the raw committed-list read (allowlisted for
            // the ERS/ELS grep gate). The tip may be NotCommitted, which ERS
            // would filter out, so route through the raw-by-id helper.
            return EffectiveState.FindCommittedRecordingByIdRaw(recordingId);
        }

        private static bool PersistSealBeforeReap()
        {
            var saveHook = SavePersistentForTesting;
            if (saveHook != null)
            {
                bool saved = saveHook();
                ParsekLog.Info("UnfinishedFlights",
                    $"Seal persisted before RP reap via test hook saved={saved}");
                return saved;
            }

            if (HighLogic.CurrentGame != null
                && !string.IsNullOrEmpty(HighLogic.SaveFolder))
            {
                string result = GamePersistence.SaveGame(
                    "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("UnfinishedFlights",
                        "Seal persistent save before RP reap returned empty result");
                    return false;
                }
                else
                {
                    ParsekLog.Info("UnfinishedFlights",
                        "Seal persisted to persistent.sfs before RP reap");
                }
                return true;
            }

            ParsekLog.Verbose("UnfinishedFlights",
                "Seal skipped persistent save before RP reap " +
                "(no HighLogic.CurrentGame / SaveFolder — test harness or pre-scene path)");
            return true;
        }

        internal static void ShowConfirmation(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("UnfinishedFlights", "Seal confirmation requested for null recording");
                return;
            }

            string vesselName = string.IsNullOrEmpty(rec.VesselName) ? rec.RecordingId ?? "<unnamed>" : rec.VesselName;
            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec);
            string terminal = tip?.TerminalStateValue.HasValue == true
                ? tip.TerminalStateValue.Value.ToString()
                : "Unknown";
            string ut = rec.EndUT.ToString("F1", CultureInfo.InvariantCulture);

            string body =
                $"Seal \"{vesselName}\" ({terminal} at UT {ut})?\n\n" +
                "This cannot be undone. After sealing, this entry is permanently merged to the timeline in its current state.\n\n" +
                "If you might want to re-fly this later, click Cancel.";

            var captured = rec;

            PopupDialog.DismissPopup(DialogName);
            LockInput();
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    body,
                    "Confirm: Seal Unfinished Flight",
                    HighLogic.UISkin,
                    new DialogGUIButton("Seal Permanently", () =>
                    {
                        try
                        {
                            TrySeal(captured, out string sealReason);
                        }
                        finally
                        {
                            ClearLock();
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("UnfinishedFlights",
                            $"Seal cancelled rec={captured.RecordingId ?? "<no-id>"}");
                        ClearLock();
                    })
                ),
                false,
                HighLogic.UISkin);
        }

        internal static void LockInput()
        {
            InputLockManager.SetControlLock(ControlTypes.All, DialogLockId);
            DialogLockActiveForTesting = true;
            ParsekLog.Verbose("UnfinishedFlights", $"Seal dialog input lock set ({DialogLockId})");
        }

        internal static void ClearLock()
        {
            InputLockManager.RemoveControlLock(DialogLockId);
            DialogLockActiveForTesting = false;
            ParsekLog.Verbose("UnfinishedFlights", $"Seal dialog input lock cleared ({DialogLockId})");
        }
    }
}
