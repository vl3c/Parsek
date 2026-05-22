using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Orchestrates the Timeline "Warp to time" action: resolves the rewind target,
    /// shows the confirmation dialog (same style as the Rewind / Fast-Forward dialogs),
    /// and dispatches one of four execution paths. Backward warps are achieved by rewinding
    /// to the nearest launch save at/before the target (or the earliest launch when the
    /// target precedes all launches) and then fast-forwarding to the exact target via the
    /// deferred <see cref="WarpToTimeConsumer"/>.
    /// </summary>
    internal static class WarpToTimeController
    {
        private const string Tag = "WarpTime";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Resolves the warp plan for the given inputs at the current UT. The Timeline UI
        /// calls this each frame to drive the button's enabled state and tooltip; the
        /// rewind-target resolution here is disk-free (it only checks for a non-empty rewind
        /// save filename), so the authoritative file-existence / precondition checks run on
        /// click in <see cref="Execute"/>.
        /// </summary>
        internal static WarpToTimeMath.WarpPlan ResolvePlan(
            int year, int day, int hour, int minute, bool inFlight, out double targetUT)
        {
            targetUT = WarpToTimeMath.ComputeTargetUT(year, day, hour, minute);
            double currentUT = SafeCurrentUT();
            Recording owner = ResolveRewindTargetLaunch(targetUT, out bool landsAtStart);
            return WarpToTimeMath.DecideWarpPlan(
                targetUT, currentUT, inFlight, owner != null, landsAtStart);
        }

        /// <summary>Entry point from the Timeline "Warp to time" button.</summary>
        internal static void RequestWarp(
            int year, int day, int hour, int minute, ParsekFlight flight, bool inFlight)
        {
            double targetUT = WarpToTimeMath.ComputeTargetUT(year, day, hour, minute);
            double currentUT = SafeCurrentUT();
            Recording owner = ResolveRewindTargetLaunch(targetUT, out bool landsAtStart);
            var plan = WarpToTimeMath.DecideWarpPlan(
                targetUT, currentUT, inFlight, owner != null, landsAtStart);

            ParsekLog.Info(Tag, string.Format(IC,
                "RequestWarp: input Y{0} D{1} {2}:{3:00} -> targetUT={4:F1} currentUT={5:F1} " +
                "inFlight={6} recording={7} plan={8} flightExit={9} landsAtStart={10} " +
                "rewindOwner={11} ownerStartUT={12}",
                year, day, hour, minute, targetUT, currentUT, inFlight,
                flight != null && flight.IsRecording, plan.Kind, plan.RequiresFlightExit,
                plan.LandsAtTimelineStart, owner?.RecordingId ?? "<none>",
                owner != null ? owner.StartUT.ToString("F1", IC) : "<none>"));

            switch (plan.Kind)
            {
                case WarpToTimeMath.WarpPlanKind.AtTarget:
                    ParsekLog.ScreenMessage("Already at this time", 3f);
                    return;
                case WarpToTimeMath.WarpPlanKind.Unreachable:
                    ParsekLog.ScreenMessage(plan.Reason, 3f);
                    return;
                default:
                    ShowConfirmation(plan, targetUT, currentUT, owner, flight, inFlight);
                    return;
            }
        }

        /// <summary>
        /// Resolves the recording to rewind to for a past target. Prefers the rewind-owner
        /// with the greatest StartUT &lt;= targetUT (nearest prior launch); if none qualifies
        /// (target precedes every launch, e.g. 1/1/0/0 game start), falls back to the owner
        /// with the smallest StartUT (earliest launch = start of the timeline) and sets
        /// <paramref name="landsAtTimelineStart"/>. Returns null when no recording owns a
        /// rewind save. ERS-routed (no raw CommittedRecordings / Ledger reads).
        /// </summary>
        internal static Recording ResolveRewindTargetLaunch(double targetUT, out bool landsAtTimelineStart)
        {
            landsAtTimelineStart = false;
            var ers = EffectiveState.ComputeERS();
            if (ers == null) return null;

            // Distinct rewind owners that have a save filename (disk-free check).
            var owners = new Dictionary<string, Recording>();
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec == null) continue;
                var ownerRec = RecordingStore.GetRewindRecording(rec);
                if (ownerRec == null || string.IsNullOrEmpty(ownerRec.RewindSaveFileName))
                    continue;
                if (string.IsNullOrEmpty(ownerRec.RecordingId)) continue;
                owners[ownerRec.RecordingId] = ownerRec;
            }
            if (owners.Count == 0) return null;

            var ownerList = new List<Recording>(owners.Values);
            var startUTs = new List<double>(ownerList.Count);
            for (int i = 0; i < ownerList.Count; i++)
                startUTs.Add(ownerList[i].StartUT);

            int idx = WarpToTimeMath.SelectRewindTargetIndex(startUTs, targetUT, out landsAtTimelineStart);
            return idx >= 0 ? ownerList[idx] : null;
        }

        private static Recording ResolveOwnerById(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return null;
            var ers = EffectiveState.ComputeERS();
            if (ers == null) return null;
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec != null && rec.RecordingId == ownerId)
                    return RecordingStore.GetRewindRecording(rec) ?? rec;
            }
            return null;
        }

        private static void ShowConfirmation(
            WarpToTimeMath.WarpPlan plan, double targetUT, double currentUT,
            Recording owner, ParsekFlight flight, bool inFlight)
        {
            string message = BuildConfirmMessage(plan, targetUT, currentUT, owner, inFlight);
            string ownerId = owner?.RecordingId;

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekWarpToTimeConfirm",
                    message,
                    "Confirm: Warp to Time",
                    HighLogic.UISkin,
                    new DialogGUIButton("Warp", () =>
                    {
                        ParsekLog.Info(Tag, string.Format(IC,
                            "User confirmed warp: plan={0} targetUT={1:F1}", plan.Kind, targetUT));
                        Execute(plan, targetUT, ownerId, flight, inFlight);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info(Tag, "User cancelled warp confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private static string BuildConfirmMessage(
            WarpToTimeMath.WarpPlan plan, double targetUT, double currentUT,
            Recording owner, bool inFlight)
        {
            string targetDate = SafePrintDate(targetUT);
            string flightPrefix = inFlight
                ? "Save your recording and return to the Space Center, "
                : "";

            if (plan.Kind == WarpToTimeMath.WarpPlanKind.ForwardOnly)
            {
                if (inFlight)
                    return $"{flightPrefix}then fast-forward to {targetDate}?";
                double delta = targetUT - currentUT;
                return string.Format(IC,
                    "Fast-forward to {0}?\n\nTime will advance by {1:F0} seconds.",
                    targetDate, delta);
            }

            // RewindThenForward
            string ownerName = owner?.VesselName ?? "the earliest launch";
            string launchDate = owner != null ? SafePrintDate(owner.StartUT) : "?";
            if (plan.LandsAtTimelineStart)
            {
                return $"{Capitalize(flightPrefix)}Rewind to the earliest launch \"{ownerName}\" at " +
                       $"{launchDate} (the start of your timeline)?\n\nAny uncommitted progress will be lost.";
            }

            return $"{Capitalize(flightPrefix)}{(inFlight ? "rewind" : "Rewind")} to \"{ownerName}\" " +
                   $"launch at {launchDate}, then fast-forward to {targetDate}?" +
                   "\n\nAny uncommitted progress will be lost.";
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        private static void Execute(
            WarpToTimeMath.WarpPlan plan, double targetUT, string ownerId,
            ParsekFlight flight, bool inFlight)
        {
            if (plan.Kind == WarpToTimeMath.WarpPlanKind.ForwardOnly)
            {
                if (!inFlight)
                {
                    ParsekLog.Info(Tag, string.Format(IC,
                        "Forward warp (KSC): jumping to targetUT={0:F1}", targetUT));
                    TimeJumpManager.ExecuteForwardJump(targetUT);
                    return;
                }

                CommitActiveRecordingIfAny(flight);
                WarpToTimeRequest.Set(targetUT);
                ParsekLog.Info(Tag, "Forward warp (flight): committed recording, exiting to Space Center");
                WarpToTimeConsumer.RunNextFrame(() => HighLogic.LoadScene(GameScenes.SPACECENTER));
                return;
            }

            // RewindThenForward
            if (inFlight)
            {
                // Commit in-frame, then defer the rewind one frame so the commit's spawned
                // leaves are not materialized into a scene the reload is about to discard.
                // StartRewind arms the pending warp only after it re-validates the rewind, so
                // we deliberately do NOT pre-arm here.
                CommitActiveRecordingIfAny(flight);
                ParsekLog.Info(Tag,
                    "Rewind-then-forward (flight): committed recording, deferring rewind one frame");
                WarpToTimeConsumer.RunNextFrame(() => StartRewind(ownerId, targetUT));
            }
            else
            {
                // KSC: nothing to commit / spawn, so no need to defer.
                StartRewind(ownerId, targetUT);
            }
        }

        /// <summary>
        /// Re-resolves the rewind owner by id (the in-flight commit may have mutated committed
        /// state), re-validates the rewind preconditions, then arms the pending forward warp
        /// and initiates the rewind. Clears any armed pending warp if the rewind cannot run.
        /// </summary>
        private static void StartRewind(string ownerId, double targetUT)
        {
            Recording ownerNow = ResolveOwnerById(ownerId);
            if (ownerNow == null)
            {
                ParsekLog.Warn(Tag, $"Rewind aborted: owner id={ownerId ?? "<none>"} no longer resolves");
                ParsekLog.ScreenMessage("Rewind target no longer available", 3f);
                WarpToTimeRequest.Clear();
                return;
            }

            if (!RecordingStore.CanRewind(ownerNow, out string reason, isRecording: false))
            {
                ParsekLog.Warn(Tag, string.Format(IC,
                    "Rewind aborted for \"{0}\" id={1}: {2}",
                    ownerNow.VesselName, ownerNow.RecordingId, reason));
                ParsekLog.ScreenMessage(reason, 3f);
                WarpToTimeRequest.Clear();
                return;
            }

            // Arm the pending forward warp (consumed at Space Center after the rewind settles)
            // only once we know the rewind will actually run.
            if (!WarpToTimeRequest.HasPending)
                WarpToTimeRequest.Set(targetUT);

            ParsekLog.Info(Tag, string.Format(IC,
                "Initiating rewind to \"{0}\" id={1} StartUT={2:F1} (pending forward warp targetUT={3:F1})",
                ownerNow.VesselName, ownerNow.RecordingId, ownerNow.StartUT, targetUT));
            RecordingStore.InitiateRewind(ownerNow);
        }

        private static void CommitActiveRecordingIfAny(ParsekFlight flight)
        {
            if (flight != null && flight.IsRecording)
            {
                ParsekLog.Info(Tag, "Committing active recording before warp");
                flight.CommitTreeFlight();
            }
        }

        private static double SafeCurrentUT()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0; }
        }

        private static string SafePrintDate(double ut)
        {
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch { return ut.ToString("F0", IC); }
        }
    }
}
