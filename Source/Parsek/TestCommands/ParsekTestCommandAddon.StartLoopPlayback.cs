using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Player-workflow-lane partial: the thin Unity applier for the DEFERRED
    /// two-phase <c>StartLoopPlayback</c> verb (the THIRD strict reserved-name
    /// promotion since M-C1, after R12's SimulateStockSwitchClick and the
    /// arrival-validation lane's MissionConfig; the wire token is byte-identical
    /// before and after, only the response changes).
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. <c>MissionConfig</c> ARMS a mission's loop; nothing
    /// unattended could then DRIVE the player's next move, which is the Missions
    /// window's "Warp to..." button - fast-forward the clock to the looped
    /// mission's next faithful departure so the replay is actually watchable.
    /// This verb is that button, through the SAME production path
    /// (<c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c> ->
    /// <c>MissionsWindowUI.ComputeNextRelaunchUT</c> ->
    /// <c>ParsekFlight.FastForwardToEventUT</c>), not a hand-rolled clock poke.
    /// </para>
    ///
    /// <para>
    /// ARGS. <c>tree=&lt;treeId&gt;</c> (required; the committed RECORDING_TREE id,
    /// resolved through <c>MissionStore.FindOriginalMission</c> exactly like
    /// MissionConfig). The verb starts playback of an ALREADY-ARMED loop, so a
    /// mission whose <c>LoopPlayback</c> is off is REJECTED <c>loop-not-armed</c>
    /// rather than silently armed here - arming is MissionConfig's job and keeping
    /// the two separate keeps each verb's OK meaning one thing.
    /// </para>
    ///
    /// <para>
    /// THE 15 s LEAD (the one deliberate deviation from a naive "wait for
    /// relaunchUT" completion). <c>FastForwardToEventUT</c> does NOT land the clock
    /// on the relaunch UT: it applies
    /// <c>TimeJumpManager.ApplyJumpLead</c> internally and lands
    /// <c>RecordingStore.RewindToLaunchLeadTimeSeconds</c> (15 s) BEFORE it, so the
    /// player arrives on the pad just before lift-off. The caller must therefore
    /// NOT pre-apply the lead (that is the documented double-lead trap at
    /// MissionsWindowUI.cs "The 15 s warp lead is NOT applied here"), and the
    /// completion must watch the LANDED target, not the relaunch UT: waiting for
    /// the relaunch UT itself would burn 15 s of real time at 1x on every jump and
    /// hang outright with the game paused. The applier computes the landed target
    /// with the SAME production <c>ApplyJumpLead</c> the flight path uses (no
    /// duplicated arithmetic) and echoes BOTH UTs in the payload.
    /// </para>
    ///
    /// <para>
    /// DEFERRED, TimeJump-shaped. <c>FastForwardToEventUT</c> ->
    /// <c>TimeJumpManager.ExecuteForwardJump</c> is an instantaneous
    /// <c>Planetarium.SetUniversalTime</c> set (no rails warp, no magnitude cap),
    /// so the deferral exists for exactly what TimeJump's exists for: the settle
    /// frames that let the engine playback loop drain its spawn queue and the
    /// ledger recalc land. The completion decision REUSES
    /// <c>TestCommandTimeJump.DecideJumpCompletion</c> plus its
    /// <c>UtToleranceSeconds</c> / <c>SettleFrames</c> constants verbatim rather
    /// than cloning the one-sided-latch reasoning (that latch is load-bearing and
    /// was arrived at from a live failure; a second copy would be a second place to
    /// get it wrong).
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // StartLoopPlayback two-phase state: the LANDED target UT the completion
        // waits on (relaunch UT minus the 15 s launch lead), the relaunch UT it was
        // derived from, the echoed mission / tree identity, and the settle-frame
        // countdown. Re-armed wholesale at the start of every StartLoopPlaybackImpl,
        // so (like the TimeJump fields) they are deliberately NOT cleared in
        // ClearTwoPhase and no stale value can be read across commands.
        private double loopPlaybackTargetUt;
        private double loopPlaybackRelaunchUt;
        private string loopPlaybackMissionName;
        private string loopPlaybackTreeId;
        private int loopPlaybackSettleFramesRemaining;

        private void StartLoopPlaybackImpl(ParsedCommand cmd)
        {
            string treeArg = ArgOrNull(cmd, "tree");
            Mission mission = string.IsNullOrEmpty(treeArg)
                ? null
                : MissionStore.FindOriginalMission(treeArg);

            string reject = TestCommandStartLoopPlayback.ClassifyRejection(
                treeArg, mission != null, mission != null && mission.LoopPlayback);
            if (reject != null)
            {
                ParsekLog.Warn(Tag,
                    $"startloopplayback rejected reason={reject} tree={treeArg ?? string.Empty}");
                SetExecResult("REJECTED", null, reject);
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ParsekLog.Warn(Tag, "startloopplayback no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            // Build the loop unit through the PRODUCTION single-selection door, with
            // parameter sourcing mirroring MissionConfigImpl (which mirrors
            // ParsekFlight.DriveMissionLoopUnits): settings-derived interval +
            // rotation mode + the faithful-playback override, and the live body seam.
            double autoLoopIntervalSeconds =
                ParsekSettings.Current?.autoLoopIntervalSeconds
                ?? LoopTiming.DefaultLoopIntervalSeconds;
            TransitedBodyRotationMode tbrMode =
                ParsekSettings.LandingBodyAlignmentMode;
            bool forceFaithful =
                ParsekSettings.Current?.forceFaithfulLoopPlayback ?? false;
            // [ERS-exempt] Same rationale as the MissionConfig verb's read: the raw
            // CommittedRecordings list feeds MissionLoopUnitBuilder, whose member
            // indices are committed-LIST indices (the RouteOrchestrator
            // list-alignment rationale); an ERS-filtered list would re-index members
            // under the builder. File allowlisted in
            // scripts/ers-els-audit-allowlist.txt.
            if (!MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                    mission, RecordingStore.CommittedTrees,
                    RecordingStore.CommittedRecordings, autoLoopIntervalSeconds,
                    FlightGlobalsBodyInfo.Instance, tbrMode, forceFaithful,
                    out GhostPlaybackLogic.LoopUnit unit))
            {
                ParsekLog.Warn(Tag,
                    $"startloopplayback error reason=unit-not-built tree={treeArg}");
                SetExecResult("ERROR", null, "unit-not-built");
                return;
            }

            // nowUt is sampled ONCE and reused for the relaunch solve, the lead, and
            // the forward-jump check. Same benign sub-frame double-read race the
            // TimeJump verb documents: the clock may tick between this read and
            // ExecuteForwardJump landing, which can at most shift the lead clamp
            // decision by one frame and never flips a forward jump backward.
            double nowUt = SafeUniversalTime();
            double relaunchUt = MissionsWindowUI.ComputeNextRelaunchUT(unit, nowUt);
            if (!TestCommandStartLoopPlayback.IsUsableRelaunchUt(relaunchUt))
            {
                ParsekLog.Warn(Tag,
                    $"startloopplayback error reason=no-next-window tree={treeArg}");
                SetExecResult("ERROR", null, "no-next-window");
                return;
            }

            // The LANDED target: what FastForwardToEventUT will actually jump to.
            // Computed with the production ApplyJumpLead so the completion watches
            // the same UT the flight path lands on (see the class doc's lead note).
            double landedTargetUt = TimeJumpManager.ApplyJumpLead(relaunchUt, nowUt);
            if (!TimeJumpManager.IsValidJump(nowUt, landedTargetUt))
            {
                // FastForwardToEventUT silently NO-OPS on a non-forward jump, so
                // without this pre-check the verb would hold the FIFO head for its
                // whole 120 s budget and then report a misleading jump-timeout.
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "startloopplayback rejected reason=window-not-forward tree={0} " +
                    "relaunchUt={1} nowUt={2}",
                    treeArg, relaunchUt.ToString("R", CultureInfo.InvariantCulture),
                    nowUt.ToString("R", CultureInfo.InvariantCulture)));
                SetExecResult("REJECTED", null, "window-not-forward");
                return;
            }

            // Mirrors ShowMissionWarpToWindowConfirmation's in-flight branch exactly:
            // the same label string, the same single FastForwardToEventUT call, and
            // NO lead applied here (the double-lead trap).
            string label = string.Format(CultureInfo.InvariantCulture,
                "next launch of \"{0}\"", mission.Name);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "startloopplayback initiated: mission='{0}' tree={1} relaunchUt={2} nowUt={3}",
                mission.Name, treeArg,
                relaunchUt.ToString("R", CultureInfo.InvariantCulture),
                nowUt.ToString("R", CultureInfo.InvariantCulture)));

            flight.FastForwardToEventUT(relaunchUt, label);

            loopPlaybackRelaunchUt = relaunchUt;
            loopPlaybackTargetUt = landedTargetUt;
            loopPlaybackMissionName = mission.Name ?? string.Empty;
            loopPlaybackTreeId = treeArg;
            loopPlaybackSettleFramesRemaining = TestCommandTimeJump.SettleFrames;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteStartLoopPlayback(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("StartLoopPlayback");
            if (loopPlaybackSettleFramesRemaining > 0)
                loopPlaybackSettleFramesRemaining--;

            double currentUt = SafeUniversalTime();
            // REUSED from the TimeJump verb: same one-sided reached-latch + settle
            // window + budget catch-all (see the class doc).
            JumpCompletionDecision decision = TestCommandTimeJump.DecideJumpCompletion(
                elapsed, currentUt, loopPlaybackTargetUt,
                TestCommandTimeJump.UtToleranceSeconds,
                loopPlaybackSettleFramesRemaining, budget);
            if (decision == JumpCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            double relaunch = loopPlaybackRelaunchUt;
            string missionName = loopPlaybackMissionName;
            string treeId = loopPlaybackTreeId;
            ClearTwoPhase();

            if (decision == JumpCompletionDecision.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandStartLoopPlayback.BuildCompletePayload(
                        relaunch, currentUt, missionName, treeId);
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "startloopplayback complete: mission='{0}' tree={1} relaunchUt={2} reachedUt={3}",
                    missionName ?? string.Empty, treeId ?? string.Empty,
                    relaunch.ToString("R", CultureInfo.InvariantCulture),
                    currentUt.ToString("R", CultureInfo.InvariantCulture)));
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
            }
            else // JumpTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "jump-timeout");
                ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                    "startloopplayback timeout elapsed={0}s tree={1}",
                    elapsed.ToString("F1", CultureInfo.InvariantCulture),
                    treeId ?? string.Empty));
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "jump-timeout", dequeueHead: true);
            }
        }
    }

    /// <summary>
    /// Pure decision half of the StartLoopPlayback verb (headlessly testable; the
    /// applier above owns the Unity / production-path calls only). The completion
    /// decision itself deliberately lives in <see cref="TestCommandTimeJump"/> and
    /// is REUSED rather than re-derived here.
    /// </summary>
    internal static class TestCommandStartLoopPlayback
    {
        /// <summary>
        /// The ordered pre-flight refusal classification: a missing <c>tree</c> arg,
        /// a tree no mission resolves for, or a mission whose loop is not armed.
        /// Returns the REJECTED reason token, or null when the verb may proceed.
        /// Ordering is load-bearing - an absent arg must not read as "unknown tree",
        /// which would send an author hunting a fixture problem for a typo.
        /// </summary>
        internal static string ClassifyRejection(
            string treeArg, bool missionFound, bool loopArmed)
        {
            if (string.IsNullOrEmpty(treeArg))
                return "tree-arg-missing";
            if (!missionFound)
                return "unknown-tree";
            if (!loopArmed)
                return "loop-not-armed";
            return null;
        }

        /// <summary>
        /// A relaunch UT is usable only when finite:
        /// <c>MissionsWindowUI.ComputeNextRelaunchUT</c> returns NaN for a
        /// degenerate anchor and for a zero-drift schedule that hit its safety cap
        /// (the "not aligned" cell), and a non-finite UT must never reach the jump.
        /// net472 has no <c>double.IsFinite</c>, so NaN / Infinity are tested
        /// explicitly (same shape as TestCommandTimeJump's range guard).
        /// </summary>
        internal static bool IsUsableRelaunchUt(double relaunchUt)
            => !double.IsNaN(relaunchUt) && !double.IsInfinity(relaunchUt);

        /// <summary>
        /// Terminal completion payload. <c>relaunchUt</c> is the mission's next
        /// faithful departure (the UT the Missions window's countdown targets),
        /// <c>reachedUt</c> the clock actually observed at completion - which sits
        /// ~15 s BEFORE relaunchUt by design (the launch lead), so a consumer
        /// comparing the two must expect the gap rather than treat it as drift. All
        /// doubles InvariantCulture round-trip ("R") so no locale comma can leak
        /// into a UT.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            double relaunchUt, double reachedUt, string mission, string tree)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "relaunchUt", relaunchUt.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "reachedUt", reachedUt.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("mission", mission ?? string.Empty),
                new KeyValuePair<string, string>("tree", tree ?? string.Empty),
            };
    }
}
