using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase EvaExit completion outcome (M-C2). Mirrors
    /// <see cref="LoadCompletionDecision"/>'s shape: the spawn settled to a live, active
    /// EVA vessel (CompleteOk), the completion budget expired without that (ExitTimeout),
    /// or the auto-switch / release / dwell has not landed yet (StillWaiting).
    /// </summary>
    internal enum EvaExitCompletionDecision
    {
        /// <summary>The spawn has not settled to a live+active EVA vessel yet (or the
        /// requested ladder release / settleSeconds dwell has not completed): keep polling.</summary>
        StillWaiting,

        /// <summary>The EVA vessel exists, is the active vessel, the scene settled, the
        /// release (if requested) applied, and the settleSeconds dwell (if any) elapsed:
        /// terminal OK.</summary>
        CompleteOk,

        /// <summary>The completion budget expired without a settled active EVA vessel
        /// (the auto-switch never completed): terminal ERROR (msg=eva-exit-timeout).</summary>
        ExitTimeout,
    }

    /// <summary>
    /// Pure decision + resolution helpers for the two-phase <c>EvaExit</c> seam verb
    /// (M-C2, design-autotest-eva-missions.md). The Unity applier calls
    /// <c>FlightEVA.fetch.spawnEVA</c>, optionally runs the ladder let-go FSM event, and
    /// hands the primitive shape of the settling scene here. Kept pure so the kerbal
    /// resolution + the completion-conjunct decision are xUnit-covered without a live KSP
    /// EVA vessel.
    /// </summary>
    internal static class TestCommandEvaExit
    {
        /// <summary>
        /// Resolve the <c>kerbal=</c> arg against the active vessel's crew names. A
        /// null/empty arg defaults to the FIRST crew member (the stock hatch-click shape).
        /// A named arg must match a crew name EXACTLY (ordinal); anything else refuses
        /// <c>kerbal-not-aboard</c>. An empty crew list cannot resolve a first member and
        /// refuses <c>no-crew</c>. Returns the resolved name (null on error), with the
        /// typed refusal reason in <paramref name="error"/>.
        /// </summary>
        internal static string ResolveKerbalArg(string arg, IList<string> crewNames, out string error)
        {
            error = null;
            if (crewNames == null || crewNames.Count == 0)
            {
                error = "no-crew";
                return null;
            }

            if (string.IsNullOrEmpty(arg))
                return crewNames[0];

            for (int i = 0; i < crewNames.Count; i++)
            {
                // Exact ordinal match: a typo must never silently EVA the wrong kerbal.
                if (string.Equals(crewNames[i], arg, System.StringComparison.Ordinal))
                    return crewNames[i];
            }

            error = "kerbal-not-aboard";
            return null;
        }

        /// <summary>
        /// Decide the two-phase EvaExit completion (design Behavior / F7). Positive
        /// completion first (mirroring <see cref="TestCommandLoadGame.DecideLoadCompletion"/>),
        /// then the budget, then StillWaiting as the default. CompleteOk requires the spawned
        /// EVA vessel to exist, to BE the active vessel (the stock auto-switch completed), the
        /// scene to have settled, the ladder release to have applied when it was requested,
        /// and the optional settleSeconds dwell to have elapsed. The release is deliberately
        /// NOT gated on ground contact (the kerbal may complete mid-fall); the ground-contact
        /// wait belongs to PlantFlag's bounded gate.
        /// </summary>
        internal static EvaExitCompletionDecision DecideEvaExitCompletion(
            double elapsed, bool evaVesselExists, bool evaVesselIsActive,
            bool sceneSettled, bool releaseRequested, bool releaseApplied,
            bool settleElapsed, double budget)
        {
            bool releaseSatisfied = !releaseRequested || releaseApplied;
            if (evaVesselExists && evaVesselIsActive && sceneSettled && releaseSatisfied && settleElapsed)
                return EvaExitCompletionDecision.CompleteOk;
            if (elapsed >= budget)
                return EvaExitCompletionDecision.ExitTimeout;
            return EvaExitCompletionDecision.StillWaiting;
        }

        /// <summary>
        /// The ladder-release action for one <c>ApplyLadderRelease</c> poll (EVA-1 flight-2
        /// release-false-positive defect, 2026-07-24). Decompiled <c>KerbalEVA</c> /
        /// <c>KerbalFSM</c> (KSP 1.12.5) ground truth: <c>On_ladderLetGo</c> (goto
        /// <c>st_idle_fl</c>) is registered on ONLY four states -
        /// <c>st_ladder_idle</c> / <c>st_ladder_climb</c> / <c>st_ladder_descend</c> /
        /// <c>st_ladder_end_reached</c> (KerbalEVA.cs:8737). A fresh EVA that auto-grabs the
        /// hatch ladder STARTS in <c>st_ladder_acquire</c> (KerbalEVA.cs:3062), a transitional
        /// state that does NOT register <c>On_ladderLetGo</c> and only advances to
        /// <c>st_ladder_idle</c> after a 1.0s <c>KFSMTimedEvent</c> (On_ladderGrabComplete,
        /// KerbalEVA.cs:8504-8506). <c>correctLadderPosition()</c> sets <c>onLadder=true</c>
        /// during that acquire window (KerbalEVA.cs:14762), so <c>OnALadder</c> is already
        /// true. <c>KerbalFSM.RunEvent</c> for an event NOT registered on the current state
        /// logs "Event ... not assigned to state ..." and RETURNS a silent no-op
        /// (KerbalFSM.cs:298-311); a registered event transitions SYNCHRONOUSLY in-call. The
        /// old applier fired <c>On_ladderLetGo</c> the instant <c>OnALadder</c> was true
        /// (~0.2s, still in <c>st_ladder_acquire</c>), hit the silent-return path, yet marked
        /// <c>released=true</c> - the kerbal hung on the hatch ladder in <c>st_ladder_idle</c>
        /// forever (no ground contact -> the 180s PlantFlag timeout). The fix fires ONLY from a
        /// receptive state and treats <c>released=true</c> as VERIFIED-left-the-ladder.
        /// </summary>
        internal enum LadderReleaseAction
        {
            /// <summary>The kerbal is not on a ladder: either it never was (the noop path) or a
            /// prior fire already took. The release phase concludes; <c>released</c> is VERIFIED
            /// only when a fire actually preceded this (fireCount &gt; 0).</summary>
            NotOnLadder,

            /// <summary>On a ladder but the current FSM state does NOT register
            /// <c>On_ladderLetGo</c> (the <c>st_ladder_acquire</c> / lean / pushoff transitional
            /// window): WAIT - firing now would be a silent no-op. Do NOT touch releaseApplied.</summary>
            WaitForReceptiveState,

            /// <summary>On a ladder in a receptive let-go state: fire <c>On_ladderLetGo</c> this
            /// poll (RunEvent transitions synchronously to <c>st_idle_fl</c>), then re-verify
            /// next poll via <see cref="NotOnLadder"/>.</summary>
            Fire,

            /// <summary>Attempted the let-go <c>maxFires</c> times and STILL on a ladder (a mod
            /// re-grab or a stuck FSM): give up bounded so the EvaExit budget is not burned -
            /// conclude the phase with <c>released=false</c> (NOT verified) and a warn (liveness).</summary>
            ExhaustedStillOnLadder,
        }

        /// <summary>The bounded re-fire cap for the ladder let-go (liveness: a stuck / re-grabbing
        /// FSM must not spin the release forever). Counted in ATTEMPTS, so a <c>RunEvent</c> that
        /// throws still costs one and the give-up stays bounded.</summary>
        internal const int LadderReleaseMaxFires = 3;

        /// <summary>
        /// Decide one <c>ApplyLadderRelease</c> poll (EVA-1 flight-2 fix). The applier passes
        /// the LIVE <c>KerbalEVA.OnALadder</c> and whether <c>fsm.CurrentState</c> is one of the
        /// four states that register <c>On_ladderLetGo</c>
        /// (<paramref name="inReceptiveLetGoState"/>), plus how many times it has already
        /// ATTEMPTED the fire (<paramref name="attemptCount"/>). Off the ladder ->
        /// <see cref="LadderReleaseAction.NotOnLadder"/> (verified-left or never-on). Still on
        /// it: exhausted first (bounded), else fire when receptive, else wait out the
        /// transitional acquire window. The wait branch is the whole fix - it stops the
        /// premature fire during <c>st_ladder_acquire</c> that the old applier logged as a false
        /// <c>released=true</c>.
        /// ATTEMPTS, not fires: the caller increments attempts BEFORE calling
        /// <c>fsm.RunEvent</c> (which throws when the fsm was never started) and its separate
        /// fire counter only AFTER RunEvent returns. The cap must bound the loop even when every
        /// call throws, while only a real fire may license <c>released=true</c>; one shared
        /// counter would let a throw be reported as our verified release.
        /// </summary>
        internal static LadderReleaseAction DecideLadderRelease(
            bool onLadder, bool inReceptiveLetGoState, int attemptCount, int maxFires)
        {
            if (!onLadder)
                return LadderReleaseAction.NotOnLadder;
            if (attemptCount >= maxFires)
                return LadderReleaseAction.ExhaustedStillOnLadder;
            if (inReceptiveLetGoState)
                return LadderReleaseAction.Fire;
            return LadderReleaseAction.WaitForReceptiveState;
        }

        /// <summary>
        /// The state of the optional post-release STANDOFF wait (EVA-4 flight-3 canopy-cut
        /// defect, 2026-07-25).
        ///
        /// STOCK GROUND TRUTH (decompiled KSP 1.12.5): a kerbal whose personal chute has
        /// reached SEMIDEPLOYED is in <c>KerbalEVA.st_semi_deployed_parachute</c>, and
        /// <c>On_stumble</c> is registered on that state
        /// (<c>fsm.AddEventExcluding(On_stumble, st_ragdoll, st_grappled,
        /// st_clamber_acquireP2, st_clamber_acquireP3)</c>, KerbalEVA.cs:8153) with
        /// <c>GoToStateOnEvent = st_ragdoll</c>. It is fired from the COLLISION callback
        /// whenever <c>c.relativeVelocity.sqrMagnitude &gt; stumbleThreshold * stumbleThreshold</c>
        /// (KerbalEVA.cs:12700, <c>stumbleThreshold = 3.5</c> m/s). Leaving the semi-deployed
        /// state to anything but <c>st_fully_deployed_parachute</c> runs
        /// <c>OnSemiDeployedParachuteModeLeft</c> -&gt; <c>evaChute.CutParachute()</c>
        /// (KerbalEVA.cs:11152-11169), and <c>ragdoll_OnEnter</c> then calls
        /// <c>AllowRepack(false)</c>, so the cut is terminal. The kerbal free-falls.
        ///
        /// On EVA-4 flight 3 that fired 200 ms after the canopy opened, 254 ms after the
        /// kerbal let go of the pod's hatch ladder, with the pod still essentially in
        /// contact (the background recorder measured only 90 m of separation 3.8 s later).
        /// So the mitigation is SEPARATION: hold the exit until the kerbal is observably
        /// clear of every other loaded vessel, and only then let the chute step run.
        /// </summary>
        internal enum EvaExitStandoffState
        {
            /// <summary>The kerbal is not yet observably clear: keep polling.</summary>
            Waiting,

            /// <summary>Clear (or no standoff was requested): the exit may complete.</summary>
            Cleared,

            /// <summary>The bounded wait expired without the standoff clearing. The exit
            /// completes ANYWAY, carrying <c>standoff=timeout</c>. Deliberately NON-FATAL:
            /// the kerbal's chute is its only survival mechanism, and refusing to complete
            /// would strand it unchuted, which is strictly worse than the pre-fix behaviour
            /// this mitigates. The give-up is named in the log and on the wire so a run that
            /// took it is never mistaken for a clean separation.</summary>
            GaveUp,
        }

        /// <summary>Consecutive polls the observed standoff must hold before it counts.
        /// Two, for the same reason every other observed gate in this lane debounces: one
        /// glitched distance read (a floating-origin shift, a frame where a vessel's
        /// position has not been updated yet) must not certify separation.</summary>
        internal const int StandoffDebouncePolls = 2;

        /// <summary>The bounded wait for the standoff, in seconds. Sized from the measured
        /// separation rate on the EVA-4 profile: a kerbal that lets go at ~-11 m/s and free
        /// falls away from a pod under full canopy reached 90 m in under 4 s, so a 30 m
        /// standoff clears in ~2-3 s. 15 s is ~5x that and still small against the altitude
        /// budget (~165 m of fall), so a run that burns it has a real problem rather than a
        /// slow frame.</summary>
        internal const double StandoffMaxWaitSeconds = 15.0;

        /// <summary>
        /// True iff THIS poll observes the kerbal clear of every other loaded vessel.
        /// <paramref name="nearestOtherLoadedVesselMeters"/> is
        /// <c>double.PositiveInfinity</c> when there is no other loaded vessel at all
        /// (nothing to collide with -&gt; clear) and <c>double.NaN</c> when the distance
        /// could not be read (fail-closed: an unreadable frame is never separation, and the
        /// bounded wait above stops that from hanging).
        /// </summary>
        internal static bool StandoffClearThisPoll(double nearestOtherLoadedVesselMeters,
                                                   double minStandoffMeters)
        {
            if (minStandoffMeters <= 0.0) return true;   // not requested
            // Explicit rather than leaning on IEEE (a NaN comparison is already false, so
            // this line is belt-and-braces): the fail-closed intent is the point, and it
            // must survive someone rewriting the comparison below into a form where NaN
            // does NOT fall out false.
            if (double.IsNaN(nearestOtherLoadedVesselMeters)) return false;
            return nearestOtherLoadedVesselMeters >= minStandoffMeters;
        }

        /// <summary>
        /// Classify the standoff wait. <paramref name="consecutiveClearPolls"/> is the
        /// caller-owned run of <see cref="StandoffClearThisPoll"/> trues (reset to 0 on any
        /// non-clear poll, so the debounce is a RUN and not a tally).
        /// </summary>
        internal static EvaExitStandoffState ClassifyStandoff(
            double minStandoffMeters, int consecutiveClearPolls, double elapsed,
            double maxWaitSeconds)
        {
            if (minStandoffMeters <= 0.0) return EvaExitStandoffState.Cleared;
            if (consecutiveClearPolls >= StandoffDebouncePolls)
                return EvaExitStandoffState.Cleared;
            if (elapsed >= maxWaitSeconds) return EvaExitStandoffState.GaveUp;
            return EvaExitStandoffState.Waiting;
        }

        /// <summary>The <c>standoff=</c> payload token for a concluded wait: "off" when none
        /// was requested, "cleared" when it was observed, "timeout" when the bounded wait
        /// gave up. Never a bare bool - a reader must be able to tell "we never asked" from
        /// "we asked and gave up".</summary>
        internal static string StandoffToken(double minStandoffMeters,
                                             EvaExitStandoffState state)
        {
            if (minStandoffMeters <= 0.0) return "off";
            return state == EvaExitStandoffState.GaveUp ? "timeout" : "cleared";
        }

        /// <summary>Terminal completion payload once the EVA vessel is live + active.
        /// <paramref name="standoff"/> is the <see cref="StandoffToken"/> value, so a run
        /// whose separation never cleared says so on the wire rather than reading identical
        /// to one that separated cleanly.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string kerbal, uint evaPid, bool released, string standoff = "off")
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("evaPid",
                    evaPid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("released", released ? "true" : "false"),
                new KeyValuePair<string, string>("standoff", standoff ?? "off"),
            };
    }
}
