using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Per-row Seal action for Unfinished Flights. Seal closes the RP child
    /// slot only; it does not mutate the recording or its MergeState.
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

            DateTime now = UtcNowForTesting != null ? UtcNowForTesting() : DateTime.UtcNow;
            if (!slot.Sealed)
            {
                slot.Sealed = true;
                slot.SealedRealTime = now.ToString("o", CultureInfo.InvariantCulture);
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

            ParsekLog.Info("UnfinishedFlights",
                $"Sealed slot={slotListIndex} rec={rec.RecordingId ?? "<no-id>"} " +
                $"bp={rp.BranchPointId ?? "<no-bp>"} rp={rp.RewindPointId ?? "<no-rp>"} " +
                $"terminal={terminal} reaperImpact={impact} reaped={reaped}");
            return true;
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
                    "Confirm Seal Unfinished Flight",
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
