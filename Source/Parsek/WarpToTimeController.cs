using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Orchestrates the Timeline "Warp to time" action: resolves the rewind target, shows the
    /// confirmation dialog (same simple text in flight and at KSC), and dispatches the warp.
    /// Backward warps rewind to the nearest reachable point at/before the target then
    /// fast-forward to the exact target via the deferred <see cref="WarpToTimeConsumer"/>.
    ///
    /// <para>In flight the warp is NOT executed in place and the recording is NOT auto-saved:
    /// the whole warp is deferred to the next Space Center arrival via a plain
    /// <c>LoadScene(SPACECENTER)</c>, so the existing <see cref="SceneExitInterceptor"/> shows
    /// its Merge / Discard dialog for the active recording first. On arrival the warp is
    /// re-resolved and executed KSC-side (<see cref="ExecuteAtKsc"/>) with no further prompt.</para>
    /// </summary>
    internal static class WarpToTimeController
    {
        private const string Tag = "WarpTime";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal enum RewindTargetKind { None, CareerStart, Launch }

        internal readonly struct RewindTarget
        {
            internal RewindTarget(RewindTargetKind kind, string ownerId, double startUT, string label)
            {
                Kind = kind;
                OwnerId = ownerId;
                StartUT = startUT;
                Label = label;
            }

            internal RewindTargetKind Kind { get; }
            internal string OwnerId { get; }   // Launch only
            internal double StartUT { get; }
            internal string Label { get; }     // vessel name (Launch) / "the start of the game" (CareerStart)

            internal static readonly RewindTarget None =
                new RewindTarget(RewindTargetKind.None, null, 0, null);
        }

        /// <summary>
        /// Resolves the warp plan for the given inputs at the current UT. Called each frame by
        /// the Timeline UI for the button's enabled state and tooltip.
        /// </summary>
        internal static WarpToTimeMath.WarpPlan ResolvePlan(
            int year, int day, int hour, int minute, bool inFlight, out double targetUT)
        {
            targetUT = WarpToTimeMath.ComputeTargetUT(year, day, hour, minute);
            double currentUT = SafeCurrentUT();
            RewindTarget target = ResolveRewindTarget(targetUT, out bool landsAtStart);
            return WarpToTimeMath.DecideWarpPlan(
                targetUT, currentUT, inFlight, target.Kind != RewindTargetKind.None, landsAtStart);
        }

        /// <summary>Entry point from the Timeline "Warp to time" button.</summary>
        internal static void RequestWarp(
            int year, int day, int hour, int minute, ParsekFlight flight, bool inFlight)
        {
            double targetUT = WarpToTimeMath.ComputeTargetUT(year, day, hour, minute);
            double currentUT = SafeCurrentUT();
            RewindTarget target = ResolveRewindTarget(targetUT, out bool landsAtStart);
            var plan = WarpToTimeMath.DecideWarpPlan(
                targetUT, currentUT, inFlight, target.Kind != RewindTargetKind.None, landsAtStart);

            ParsekLog.Info(Tag, string.Format(IC,
                "RequestWarp: input Y{0} D{1} {2}:{3:00} -> targetUT={4:F1} currentUT={5:F1} " +
                "inFlight={6} recording={7} plan={8} landsAtStart={9} rewindKind={10} " +
                "rewindStartUT={11:F1} rewindLabel={12}",
                year, day, hour, minute, targetUT, currentUT, inFlight,
                flight != null && flight.IsRecording, plan.Kind, plan.LandsAtTimelineStart,
                target.Kind, target.StartUT, target.Label ?? "<none>"));

            switch (plan.Kind)
            {
                case WarpToTimeMath.WarpPlanKind.AtTarget:
                    ParsekLog.ScreenMessage("Already at this time", 3f);
                    return;
                case WarpToTimeMath.WarpPlanKind.Unreachable:
                    ParsekLog.ScreenMessage(plan.Reason, 3f);
                    return;
                default:
                    // Confirm dialog uses the same simple text in flight and at KSC. On confirm:
                    //  - KSC: execute now.
                    //  - flight: defer the whole warp to the Space Center (the scene-exit
                    //    Merge / Discard dialog handles the active recording; no auto-save).
                    Action onConfirm = inFlight
                        ? (Action)(() => DeferWarpToSpaceCenter(year, day, hour, minute))
                        : () => Execute(plan, targetUT, target);
                    ShowConfirmation(plan, targetUT, currentUT, target, onConfirm);
                    return;
            }
        }

        /// <summary>
        /// Re-resolves and executes a warp at the Space Center with NO confirmation (the player
        /// already confirmed in flight). Invoked by <see cref="WarpToTimeConsumer"/> after a
        /// flight->KSC exit (and its Merge / Discard dialog) settles. State may have changed via
        /// that dialog (commit / discard), so the plan is recomputed from the entered date.
        /// </summary>
        internal static void ExecuteAtKsc(int year, int day, int hour, int minute)
        {
            double targetUT = WarpToTimeMath.ComputeTargetUT(year, day, hour, minute);
            double currentUT = SafeCurrentUT();
            RewindTarget target = ResolveRewindTarget(targetUT, out bool landsAtStart);
            var plan = WarpToTimeMath.DecideWarpPlan(
                targetUT, currentUT, inFlight: false,
                target.Kind != RewindTargetKind.None, landsAtStart);

            ParsekLog.Info(Tag, string.Format(IC,
                "ExecuteAtKsc: Y{0} D{1} {2}:{3:00} -> targetUT={4:F1} currentUT={5:F1} " +
                "plan={6} rewindKind={7}", year, day, hour, minute, targetUT, currentUT,
                plan.Kind, target.Kind));

            switch (plan.Kind)
            {
                case WarpToTimeMath.WarpPlanKind.AtTarget:
                    ParsekLog.ScreenMessage("Already at this time", 3f);
                    return;
                case WarpToTimeMath.WarpPlanKind.Unreachable:
                    ParsekLog.ScreenMessage(plan.Reason, 3f);
                    return;
                default:
                    Execute(plan, targetUT, target);
                    return;
            }
        }

        private static void DeferWarpToSpaceCenter(int year, int day, int hour, int minute)
        {
            WarpToTimeRequest.SetDeferredKscWarp(year, day, hour, minute);
            ParsekLog.Info(Tag,
                "Flight warp confirmed: returning to the Space Center; the scene-exit " +
                "Merge / Discard dialog will handle the active recording, then the warp runs.");
            // Defer one frame so we are not spawning the scene-exit dialog from inside the warp
            // dialog's button handler. The SceneExitInterceptor prefix on LoadScene shows the
            // Merge / Discard dialog when an active recording exists.
            WarpToTimeConsumer.RunNextFrame(() => HighLogic.LoadScene(GameScenes.SPACECENTER));
        }

        /// <summary>
        /// Resolves where a past target rewinds to. Candidates are the distinct launch rewind
        /// saves plus, when no re-fly supersedes exist and the snapshot exists, the career-start
        /// snapshot (a virtual launch at UT 0 = a true reset). Picks the greatest StartUT &lt;=
        /// target (nearest prior), or the earliest when the target precedes all candidates (sets
        /// <paramref name="landsAtTimelineStart"/>). ERS-routed.
        /// </summary>
        internal static RewindTarget ResolveRewindTarget(double targetUT, out bool landsAtTimelineStart)
        {
            landsAtTimelineStart = false;

            var kinds = new List<RewindTargetKind>();
            var ownerIds = new List<string>();
            var startUTs = new List<double>();
            var labels = new List<string>();

            int supersedeCount = ParsekScenario.Instance?.RecordingSupersedes?.Count ?? 0;
            if (supersedeCount == 0 && CareerStartSnapshot.Exists())
            {
                kinds.Add(RewindTargetKind.CareerStart);
                ownerIds.Add(null);
                startUTs.Add(0.0);
                labels.Add("the start of the game");
            }

            var ers = EffectiveState.ComputeERS();
            if (ers != null)
            {
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
                foreach (var ownerRec in owners.Values)
                {
                    kinds.Add(RewindTargetKind.Launch);
                    ownerIds.Add(ownerRec.RecordingId);
                    startUTs.Add(ownerRec.StartUT);
                    labels.Add(ownerRec.VesselName);
                }
            }

            if (startUTs.Count == 0)
                return RewindTarget.None;

            int idx = WarpToTimeMath.SelectRewindTargetIndex(startUTs, targetUT, out landsAtTimelineStart);
            if (idx < 0)
                return RewindTarget.None;

            return new RewindTarget(kinds[idx], ownerIds[idx], startUTs[idx], labels[idx]);
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
            RewindTarget target, Action onConfirm)
        {
            string message = BuildConfirmMessage(plan, targetUT, currentUT, target);

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
                            "User confirmed warp: plan={0} targetUT={1:F1} rewindKind={2}",
                            plan.Kind, targetUT, target.Kind));
                        onConfirm?.Invoke();
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info(Tag, "User cancelled warp confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private static string BuildConfirmMessage(
            WarpToTimeMath.WarpPlan plan, double targetUT, double currentUT, RewindTarget target)
        {
            string targetDate = SafePrintDate(targetUT);

            if (plan.Kind == WarpToTimeMath.WarpPlanKind.ForwardOnly)
            {
                double delta = targetUT - currentUT;
                return $"Fast-forward to {targetDate}?\n\nTime will advance by " +
                       $"{ParsekTimeFormat.FormatDurationFull(delta)}.";
            }

            // RewindThenForward
            if (target.Kind == RewindTargetKind.CareerStart)
            {
                string tail = targetUT > WarpToTimeMath.AtTargetEpsilonSeconds
                    ? $"Reset to the start of the game, then fast-forward to {targetDate}?"
                    : "Reset to the start of the game (Year 1, Day 1)?";
                return $"{tail}\n\nResources, facilities and the clock return to career start; " +
                       "your recordings are kept and replay as time advances.";
            }

            string ownerName = target.Label ?? "the earliest launch";
            string launchDate = SafePrintDate(target.StartUT);
            if (plan.LandsAtTimelineStart)
            {
                return $"Rewind to the earliest launch \"{ownerName}\" at {launchDate} " +
                       "(the start of your timeline)?\n\nAny uncommitted progress will be lost.";
            }

            return $"Rewind to \"{ownerName}\" launch at {launchDate}, then fast-forward to " +
                   $"{targetDate}?\n\nAny uncommitted progress will be lost.";
        }

        /// <summary>
        /// Executes a confirmed warp at the Space Center. Forward = a single time jump; backward
        /// = rewind to the resolved target (career-start snapshot or a launch save) then a
        /// deferred forward jump. Never called in flight (the flight path defers here via
        /// <see cref="ExecuteAtKsc"/>).
        /// </summary>
        private static void Execute(WarpToTimeMath.WarpPlan plan, double targetUT, RewindTarget target)
        {
            if (plan.Kind == WarpToTimeMath.WarpPlanKind.ForwardOnly)
            {
                ParsekLog.Info(Tag, string.Format(IC,
                    "Forward warp: jumping to targetUT={0:F1}", targetUT));
                TimeJumpManager.ExecuteForwardJump(targetUT);
                return;
            }

            // RewindThenForward
            StartRewind(target, targetUT);
        }

        /// <summary>
        /// Arms the pending forward warp and initiates the rewind (career-start snapshot or a
        /// launch save), re-validating preconditions first. Clears the pending warp if the
        /// rewind cannot run, so a later Space Center load cannot fire a stale jump.
        /// </summary>
        private static void StartRewind(RewindTarget target, double targetUT)
        {
            if (target.Kind == RewindTargetKind.CareerStart)
            {
                if (!CareerStartSnapshot.Exists())
                {
                    ParsekLog.Warn(Tag, "Career-start reset aborted: snapshot no longer present");
                    ParsekLog.ScreenMessage("Game-start snapshot not available", 3f);
                    return;
                }
                WarpToTimeRequest.Set(targetUT);
                ParsekLog.Info(Tag, string.Format(IC,
                    "Initiating career-start reset (pending forward warp targetUT={0:F1})", targetUT));
                if (!RecordingStore.InitiateRewindToCareerStart(CareerStartSnapshot.SaveFileName))
                {
                    ParsekLog.Warn(Tag, "Career-start reset refused — clearing pending warp");
                    WarpToTimeRequest.Clear();
                }
                return;
            }

            Recording ownerNow = ResolveOwnerById(target.OwnerId);
            if (ownerNow == null)
            {
                ParsekLog.Warn(Tag, $"Rewind aborted: owner id={target.OwnerId ?? "<none>"} no longer resolves");
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

            WarpToTimeRequest.Set(targetUT);
            ParsekLog.Info(Tag, string.Format(IC,
                "Initiating rewind to \"{0}\" id={1} StartUT={2:F1} (pending forward warp targetUT={3:F1})",
                ownerNow.VesselName, ownerNow.RecordingId, ownerNow.StartUT, targetUT));
            RecordingStore.InitiateRewind(ownerNow);
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
