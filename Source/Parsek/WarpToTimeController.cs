using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Orchestrates the Timeline "Warp to time" action: resolves the rewind target, shows the
    /// confirmation dialog (same style as the Rewind / Fast-Forward dialogs), and dispatches
    /// the execution paths. Backward warps rewind to the nearest reachable point at/before the
    /// target then fast-forward to the exact target via the deferred <see cref="WarpToTimeConsumer"/>.
    /// The rewind point is the career-start snapshot (true UT-0 reset) when the target precedes
    /// the first launch and the snapshot exists, otherwise the nearest / earliest launch save.
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
        /// the Timeline UI for the button's enabled state and tooltip. Disk-free except for the
        /// career-start snapshot existence check; the authoritative rewind preconditions run on
        /// click in <see cref="Execute"/>.
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
                "inFlight={6} recording={7} plan={8} flightExit={9} landsAtStart={10} " +
                "rewindKind={11} rewindStartUT={12:F1} rewindLabel={13}",
                year, day, hour, minute, targetUT, currentUT, inFlight,
                flight != null && flight.IsRecording, plan.Kind, plan.RequiresFlightExit,
                plan.LandsAtTimelineStart, target.Kind, target.StartUT, target.Label ?? "<none>"));

            switch (plan.Kind)
            {
                case WarpToTimeMath.WarpPlanKind.AtTarget:
                    ParsekLog.ScreenMessage("Already at this time", 3f);
                    return;
                case WarpToTimeMath.WarpPlanKind.Unreachable:
                    ParsekLog.ScreenMessage(plan.Reason, 3f);
                    return;
                default:
                    ShowConfirmation(plan, targetUT, currentUT, target, flight, inFlight);
                    return;
            }
        }

        /// <summary>
        /// Resolves where a past target rewinds to. Candidates are the distinct launch rewind
        /// saves plus, when the target precedes the first launch can be made exact, the
        /// career-start snapshot (a virtual launch at UT 0). The career-start snapshot is only a
        /// candidate when it exists AND no re-fly supersede relations are present (a UT-0 reset
        /// would otherwise hide superseded originals; the launch path handles supersedes). Picks
        /// the greatest StartUT &lt;= target (nearest prior), or the earliest when the target
        /// precedes all candidates (sets <paramref name="landsAtTimelineStart"/> - only possible
        /// for the launch fallback when no career-start snapshot exists). ERS-routed.
        /// </summary>
        internal static RewindTarget ResolveRewindTarget(double targetUT, out bool landsAtTimelineStart)
        {
            landsAtTimelineStart = false;

            var kinds = new List<RewindTargetKind>();
            var ownerIds = new List<string>();
            var startUTs = new List<double>();
            var labels = new List<string>();

            // Career-start snapshot = a virtual launch at UT 0 (true reset), gated on no supersedes.
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
            RewindTarget target, ParsekFlight flight, bool inFlight)
        {
            string message = BuildConfirmMessage(plan, targetUT, currentUT, target, inFlight);

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
                        Execute(plan, targetUT, target, flight, inFlight);
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
            RewindTarget target, bool inFlight)
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
            if (target.Kind == RewindTargetKind.CareerStart)
            {
                // Lowercase "reset" mid-sentence after the flight prefix; capitalized at the
                // start of the sentence in the KSC case (no prefix).
                string verb = inFlight ? "reset" : "Reset";
                string tail = targetUT > WarpToTimeMath.AtTargetEpsilonSeconds
                    ? $"{verb} to the start of the game, then fast-forward to {targetDate}?"
                    : $"{verb} to the start of the game (Year 1, Day 1)?";
                return $"{Capitalize(flightPrefix)}{tail}\n\n" +
                       "Resources, facilities and the clock return to career start; your recordings " +
                       "are kept and replay as time advances.";
            }

            string ownerName = target.Label ?? "the earliest launch";
            string launchDate = SafePrintDate(target.StartUT);
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
            WarpToTimeMath.WarpPlan plan, double targetUT, RewindTarget target,
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

            // RewindThenForward: commit in flight, then defer the rewind/scene-load one frame so
            // the commit's spawned leaves are not materialized into a scene the reload discards.
            if (inFlight)
            {
                CommitActiveRecordingIfAny(flight);
                ParsekLog.Info(Tag,
                    $"Rewind-then-forward (flight, {target.Kind}): committed recording, deferring one frame");
                WarpToTimeConsumer.RunNextFrame(() => StartRewind(target, targetUT));
            }
            else
            {
                StartRewind(target, targetUT);
            }
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

            // Launch: re-resolve the owner by id (the in-flight commit may have mutated state).
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
