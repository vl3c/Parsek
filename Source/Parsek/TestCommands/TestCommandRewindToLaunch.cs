using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase <c>InvokeRewindToLaunch</c> completion outcome. Rewind-to-Launch
    /// (<c>RecordingStore.InitiateRewind</c>, the Recordings-table "R" button) straddles a
    /// KSP scene reload into SPACECENTER: the synchronous half copies the
    /// <c>parsek_rw_*</c> quicksave, parses it, stamps
    /// <c>RewindContext.SetAdjustedUT</c> and calls <c>HighLogic.LoadScene</c>, while the
    /// terminal answer is only known once <c>ParsekScenario.OnLoad</c>'s
    /// <c>HandleRewindOnLoad</c> has run in the loaded Space Center and cleared the rewind
    /// flags. Modeled on <see cref="RewindCompletionDecision"/>.
    ///
    /// <para>This is NOT the Re-Fly system. <c>InvokeRewind</c> drives
    /// <c>RewindInvoker.StartInvoke</c> over a RewindPoint / ChildSlot and completes on a
    /// fresh <c>ReFlySessionMarker</c>; this verb drives the plain launch rewind, which
    /// creates no RewindPoint, no marker and no MergeJournal (see
    /// <c>RecordingStore.BeginRewindForOwner</c>'s own log line saying exactly that). Two
    /// mechanisms, two verbs, two completion deciders.</para>
    /// </summary>
    internal enum RewindToLaunchCompletionDecision
    {
        /// <summary>Mid-straddle: the rewind flags are still set, so
        /// <c>HandleRewindOnLoad</c> has not run yet. Keep holding the FIFO head.</summary>
        StillWaiting,

        /// <summary>The flags cleared AND we are settled in SPACECENTER:
        /// <c>HandleRewindOnLoad</c> ran to its <c>RewindContext.EndRewind()</c> tail.
        /// Terminal OK.</summary>
        CompleteOk,

        /// <summary>The flags cleared but we never reached SPACECENTER: the load failed and
        /// <c>RecordingStore.ResetRewindFlags</c> ran (LoadGame returned null, or the copy /
        /// pre-process threw), leaving the scene unchanged. Terminal ERROR
        /// (rewind-failed).</summary>
        RewindFailed,

        /// <summary>The reload never settled within the budget: terminal ERROR
        /// (rewind-timeout).</summary>
        RewindTimeout,
    }

    /// <summary>The outcome of resolving WHICH committed tree this rewind targets.</summary>
    internal enum RewindToLaunchTargetOutcome
    {
        /// <summary>A single tree was selected; <c>TreeId</c> carries it.</summary>
        Selected,

        /// <summary>An explicit <c>tree=</c> arg named an id no committed tree carries.</summary>
        UnknownTree,

        /// <summary>No <c>tree=</c> arg and no committed tree exists to rewind to.</summary>
        NoCommittedTree,

        /// <summary>No <c>tree=</c> arg and more than one committed tree exists: the verb
        /// refuses rather than guessing which flight the spec meant to unwind.</summary>
        AmbiguousTree,
    }

    /// <summary>Pure result of <see cref="TestCommandRewindToLaunch.ResolveTarget"/>.</summary>
    internal struct RewindToLaunchTarget
    {
        public RewindToLaunchTargetOutcome Outcome;

        /// <summary>The selected committed-tree id (null unless
        /// <see cref="RewindToLaunchTargetOutcome.Selected"/>).</summary>
        public string TreeId;

        /// <summary>The REJECTED <c>msg</c> for a non-Selected outcome (null when
        /// Selected).</summary>
        public string RefusalReason;
    }

    /// <summary>
    /// Pure decision + payload helpers for the two-phase <c>InvokeRewindToLaunch</c> seam
    /// verb. The Unity applier on the addon resolves the target tree's root recording,
    /// routes through the real <c>RecordingStore.CanRewind</c> gate, calls
    /// <c>RecordingStore.InitiateRewind</c>, and samples the live rewind flags / scene;
    /// every decision it makes is factored here so it is xUnit-covered without a live KSP
    /// scene reload. Sibling of <see cref="TestCommandInvokeRewind"/>, which does the same
    /// job for the DIFFERENT (Re-Fly) mechanism.
    /// </summary>
    internal static class TestCommandRewindToLaunch
    {
        /// <summary>
        /// Decide the two-phase InvokeRewindToLaunch completion. The addon polls this only
        /// at SETTLED scenes (the pump gates off during LOADING / transition / settle), so
        /// a SPACECENTER reading here means the rewind's destination scene has arrived and
        /// <c>ParsekScenario.OnLoad</c> has already run for it.
        ///
        /// <para>Order (mirroring
        /// <see cref="TestCommandInvokeRewind.DecideRewindCompletion"/>): the success
        /// conjunction wins first and even past the budget; the budget expiry is then
        /// checked UNCONDITIONALLY (before the still-pending straddle) so a reload that
        /// never completes - leaving <c>IsRewinding</c> set forever - still terminates as
        /// RewindTimeout instead of holding the FIFO head indefinitely; a still-set flag
        /// within budget keeps waiting; cleared flags with no SPACECENTER is the fast
        /// failure.</para>
        ///
        /// <para>CompleteOk needs BOTH halves and neither is redundant.
        /// <c>RewindContext.EndRewind()</c> has TWO callers: the success tail of
        /// <c>HandleRewindOnLoad</c> (ParsekScenario, after the ledger recalc), and
        /// <c>RecordingStore.ResetRewindFlags</c> on the load-failure paths inside
        /// <c>ExecuteRewindSaveLoad</c> - which leave the scene exactly where it was
        /// (FLIGHT). <c>!isRewinding</c> alone would therefore read a failed load as a
        /// success. Conversely a SPACECENTER reading alone proves nothing while the flags
        /// are still set (a foreign scene change could land there mid-rewind). The pair is
        /// the statement.</para>
        /// </summary>
        internal static RewindToLaunchCompletionDecision DecideRewindToLaunchCompletion(
            double elapsedSeconds, bool isRewinding, bool sceneIsSpaceCenter, double budgetSeconds)
        {
            if (!isRewinding && sceneIsSpaceCenter) return RewindToLaunchCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds) return RewindToLaunchCompletionDecision.RewindTimeout;
            if (isRewinding) return RewindToLaunchCompletionDecision.StillWaiting;
            return RewindToLaunchCompletionDecision.RewindFailed;
        }

        /// <summary>The gate-decline <c>msg</c>: the real <c>RecordingStore.CanRewind</c>
        /// reason surfaced VERBATIM behind a stable <c>rewind-gate</c> prefix so the
        /// orchestrator sees WHY, not a bare failure. Same shape as
        /// <see cref="TestCommandInvokeRewind.GateRefusalMsg"/>'s <c>refly-gate</c>, and
        /// deliberately a DIFFERENT prefix: the two gates have disjoint reason vocabularies
        /// and a spec must be able to tell which one refused.</summary>
        internal static string GateRefusalMsg(string reason)
            => "rewind-gate " + (reason ?? string.Empty);

        /// <summary>
        /// Resolve WHICH committed tree the rewind targets, from the committed-tree ids and
        /// the optional <c>tree=</c> arg. Kept Unity-free by taking the ids rather than the
        /// <c>RecordingTree</c> objects - the applier does the root-recording lookup after
        /// this returns Selected.
        ///
        /// <para>An absent arg auto-selects ONLY when exactly one committed tree exists.
        /// Guessing among several would silently unwind the wrong flight, which is
        /// irreversible in the same breath - hence <c>ambiguous-tree</c> rather than
        /// "first wins".</para>
        /// </summary>
        internal static RewindToLaunchTarget ResolveTarget(
            IReadOnlyList<string> committedTreeIds, string treeArg)
        {
            if (!string.IsNullOrEmpty(treeArg))
            {
                if (committedTreeIds != null)
                {
                    for (int i = 0; i < committedTreeIds.Count; i++)
                    {
                        if (committedTreeIds[i] == treeArg)
                            return new RewindToLaunchTarget
                            {
                                Outcome = RewindToLaunchTargetOutcome.Selected,
                                TreeId = treeArg,
                            };
                    }
                }
                return new RewindToLaunchTarget
                {
                    Outcome = RewindToLaunchTargetOutcome.UnknownTree,
                    RefusalReason = "unknown-tree",
                };
            }

            int count = committedTreeIds != null ? committedTreeIds.Count : 0;
            if (count == 0)
                return new RewindToLaunchTarget
                {
                    Outcome = RewindToLaunchTargetOutcome.NoCommittedTree,
                    RefusalReason = "no-committed-tree",
                };
            if (count > 1)
                return new RewindToLaunchTarget
                {
                    Outcome = RewindToLaunchTargetOutcome.AmbiguousTree,
                    RefusalReason = "ambiguous-tree",
                };
            return new RewindToLaunchTarget
            {
                Outcome = RewindToLaunchTargetOutcome.Selected,
                TreeId = committedTreeIds[0],
            };
        }

        /// <summary>Terminal OK payload once the loaded Space Center settles.
        /// <paramref name="adjustedUT"/> is the UT the world actually reverted to (the
        /// pre-processed save's <c>flightState.universalTime</c>, i.e. the launch UT minus
        /// the rewind lead time) - LATCHED by the applier at Execute, because
        /// <c>RewindContext.EndRewind()</c> zeroes <c>RewindAdjustedUT</c> in the same tail
        /// that makes the completion CompleteOk.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string rec, string tree, double adjustedUT)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("rewound", "true"),
                new KeyValuePair<string, string>("rec", rec ?? string.Empty),
                new KeyValuePair<string, string>("tree", tree ?? string.Empty),
                new KeyValuePair<string, string>("adjustedUT", adjustedUT.ToString("F1", CultureInfo.InvariantCulture)),
            };
    }
}
