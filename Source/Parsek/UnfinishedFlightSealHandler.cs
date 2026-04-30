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
        internal static bool DialogLockActiveForTesting { get; private set; }

        internal static void ResetForTesting()
        {
            UtcNowForTesting = null;
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
            if (!object.ReferenceEquals(null, scenario))
                scenario.BumpSupersedeStateVersion();

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.RecordingSupersedes
                    : null;
            bool willReap = RewindPointReaper.IsReapEligible(rp, supersedes);
            int reaped = 0;
            if (willReap)
                reaped = RewindPointReaper.ReapOrphanedRPs();

            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec);
            string terminal = tip?.TerminalStateValue.HasValue == true
                ? tip.TerminalStateValue.Value.ToString()
                : "<none>";
            string impact = willReap ? "willReap" : "stillBlocked";

            ParsekLog.Info("UnfinishedFlights",
                $"Sealed slot={slotListIndex} rec={rec.RecordingId ?? "<no-id>"} " +
                $"bp={rp.BranchPointId ?? "<no-bp>"} rp={rp.RewindPointId ?? "<no-rp>"} " +
                $"terminal={terminal} reaperImpact={impact} reaped={reaped}");
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
                "This action CANNOT BE UNDONE.\n\n" +
                "After sealing:\n" +
                "- This slot is closed permanently; the recording can never be re-flown from this Rewind Point.\n" +
                "- The Play button on this row disappears.\n" +
                "- The rewind point quicksave may be deleted once every sibling slot is closed.\n" +
                "- The recording itself is unchanged and remains in the timeline.\n\n" +
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
                    "Seal Unfinished Flight?",
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
