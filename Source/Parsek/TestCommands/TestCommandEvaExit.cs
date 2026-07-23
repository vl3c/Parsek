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

        /// <summary>Terminal completion payload once the EVA vessel is live + active.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string kerbal, uint evaPid, bool released)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("evaPid",
                    evaPid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("released", released ? "true" : "false"),
            };
    }
}
