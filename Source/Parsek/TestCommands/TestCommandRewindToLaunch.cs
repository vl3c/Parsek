using System;
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
    /// <c>HandleRewindOnLoad</c> has run in the loaded Space Center, cleared the rewind
    /// flags, AND its deferred adjustment coroutine has drained. Modeled on
    /// <see cref="RewindCompletionDecision"/>.
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
        /// <summary>Mid-straddle: either the rewind flags are still set (so
        /// <c>HandleRewindOnLoad</c> has not run yet), or they cleared in SPACECENTER but
        /// the deferred post-load adjustment is still draining. Keep holding the FIFO
        /// head.</summary>
        StillWaiting,

        /// <summary>The flags cleared, we are settled in SPACECENTER, AND the deferred
        /// post-load adjustment has drained: <c>HandleRewindOnLoad</c> ran to its
        /// <c>RewindContext.EndRewind()</c> tail and
        /// <c>ApplyRewindResourceAdjustment</c> finished applying the adjusted UT and the
        /// ledger recalc. Terminal OK.</summary>
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

    /// <summary>HOW a <see cref="RewindToLaunchTargetOutcome.Selected"/> target was
    /// picked. Reported in the applier's log line so a collected KSP.log says which
    /// rule chose the tree, not just which tree was chosen: an `explicit-id` rewind and
    /// a `latest` rewind that happen to land on the same tree are the same OUTCOME and
    /// very different EVIDENCE.</summary>
    internal enum RewindToLaunchTargetResolution
    {
        /// <summary>Nothing was selected (a refusal outcome).</summary>
        None,

        /// <summary>An explicit <c>tree=&lt;id&gt;</c> matched a committed tree id.</summary>
        ExplicitId,

        /// <summary><c>tree=latest</c>: the most recently committed tree.</summary>
        LatestKeyword,

        /// <summary>No <c>tree=</c> arg, and exactly one committed tree existed.</summary>
        AutoSingle,
    }

    /// <summary>Pure result of <see cref="TestCommandRewindToLaunch.ResolveTarget"/>.</summary>
    internal struct RewindToLaunchTarget
    {
        public RewindToLaunchTargetOutcome Outcome;

        /// <summary>The selected committed-tree id (null unless
        /// <see cref="RewindToLaunchTargetOutcome.Selected"/>).</summary>
        public string TreeId;

        /// <summary>Which rule picked <see cref="TreeId"/>;
        /// <see cref="RewindToLaunchTargetResolution.None"/> on every refusal.</summary>
        public RewindToLaunchTargetResolution Resolution;

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
        /// <summary>The one <c>tree=</c> value that is not an id: "the most recently
        /// committed tree". A const rather than a literal so the spec-facing spelling,
        /// the resolver and the seam-design doc's verb grammar cannot drift apart, and so
        /// a `nameof`-free grep finds every site.</summary>
        internal const string LatestTreeKeyword = "latest";

        /// <summary>
        /// Decide the two-phase InvokeRewindToLaunch completion. The addon polls this only
        /// at SETTLED scenes (the pump gates off during LOADING / transition / settle), so
        /// a SPACECENTER reading here means the rewind's destination scene has arrived and
        /// <c>ParsekScenario.OnLoad</c> has already run for it.
        ///
        /// <para>Order (mirroring
        /// <see cref="TestCommandInvokeRewind.DecideRewindCompletion"/>): the success
        /// conjunction wins first and even past the budget; the budget expiry is then
        /// checked UNCONDITIONALLY (before either still-pending branch) so a reload that
        /// never completes - leaving <c>IsRewinding</c> set forever, or a deferred
        /// adjustment whose host died mid-wait - still terminates as RewindTimeout instead
        /// of holding the FIFO head indefinitely; a still-set flag within budget keeps
        /// waiting; cleared flags with no SPACECENTER is the fast failure.</para>
        ///
        /// <para>CompleteOk needs ALL THREE halves and none is redundant.
        /// <c>RewindContext.EndRewind()</c> has TWO callers: the success tail of
        /// <c>HandleRewindOnLoad</c> (ParsekScenario), and
        /// <c>RecordingStore.ResetRewindFlags</c> on the load-failure paths inside
        /// <c>ExecuteRewindSaveLoad</c> - which leave the scene exactly where it was
        /// (FLIGHT). <c>!isRewinding</c> alone would therefore read a failed load as a
        /// success. A SPACECENTER reading alone proves nothing while the flags are still
        /// set (a foreign scene change could land there mid-rewind). And the flags clear
        /// EARLY relative to the world the rewind promises: <c>HandleRewindOnLoad</c> arms
        /// <c>RecordingStore.RewindUTAdjustmentPending</c> +
        /// <c>RewindContext.BeginRewindResourceAdjustment()</c> immediately BEFORE the
        /// <c>EndRewind()</c> that clears <c>IsRewinding</c>, and only the deferred
        /// <c>ApplyRewindResourceAdjustment</c> coroutine (~2s later) sets the adjusted UT
        /// on Planetarium and runs the ledger recalc. Reporting OK on the two-half
        /// conjunction hands the next verb a scene whose UT is still the pre-rewind future
        /// and whose career resources have not been patched yet.</para>
        /// </summary>
        internal static RewindToLaunchCompletionDecision DecideRewindToLaunchCompletion(
            double elapsedSeconds, bool isRewinding, bool sceneIsSpaceCenter,
            bool deferredAdjustmentPending, double budgetSeconds)
        {
            if (!isRewinding && sceneIsSpaceCenter && !deferredAdjustmentPending)
                return RewindToLaunchCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds) return RewindToLaunchCompletionDecision.RewindTimeout;
            if (isRewinding) return RewindToLaunchCompletionDecision.StillWaiting;
            // Flags cleared and we ARE in the destination scene: the deferred adjustment is
            // still draining, so keep holding rather than reading it as the fast failure.
            if (sceneIsSpaceCenter) return RewindToLaunchCompletionDecision.StillWaiting;
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
        ///
        /// <para><b><c>tree=latest</c> - the one spelling a STATIC spec can write.</b> A
        /// scenario that produces its own rewind subject in-run (<c>StartRecording</c> ->
        /// <c>CommitTree</c>, which is what makes a rewindable tree at all, since
        /// <c>FlightRecorder.CaptureRewindSave</c> writes the <c>parsek_rw_*</c> quicksave
        /// at every non-promotion recording start) cannot NAME it: a freshly committed
        /// tree's id is a runtime <c>Guid</c>, and the harness has exactly one spec-side
        /// substitution (<c>${runSave}</c>) with no way to feed a prior step's payload
        /// into a later step's args. On any host that already carries committed trees the
        /// auto-select then refuses <c>ambiguous-tree</c>, which left the whole
        /// Rewind-to-Launch mechanism undriveable by a step-sequence lane
        /// (ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES). This keyword is
        /// that gap and nothing else.
        /// <list type="bullet">
        /// <item>It is an EXPLICIT choice, not a relaxation of the ambiguity rule: the
        /// bare no-arg call still refuses over several trees, because "the operator did
        /// not say" and "the operator said: the newest one" are different intents.</item>
        /// <item>The ID PATH IS UNTOUCHED and still wins. The keyword is tested only
        /// AFTER the exact-id scan fails, so a committed tree literally named
        /// <c>latest</c> would still be selected by id. Real ids are 32-hex <c>Guid</c>
        /// "N" strings, so the collision is unreachable in practice and the ordering
        /// makes it harmless anyway.</item>
        /// <item>"Latest" is LAST-IN-LIST, and that is the definition, not an
        /// approximation: <c>RecordingStore.CommittedTrees</c> is append-ordered on
        /// commit, so the last entry is the most recently committed tree. The applier
        /// hands the ids in that list order.</item>
        /// <item>Matched ORDINAL-IGNORE-CASE so a spec may write <c>latest</c> or
        /// <c>LATEST</c>; every id comparison stays ordinal-exact.</item>
        /// <item>With NO committed tree it answers <c>no-committed-tree</c> - the same
        /// verdict the bare call gives for the same world state, rather than
        /// <c>unknown-tree</c>, which would blame the argument for an empty save.</item>
        /// </list></para>
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
                                Resolution = RewindToLaunchTargetResolution.ExplicitId,
                            };
                    }
                }

                // The keyword, tested only after the id scan (see the contract above).
                if (string.Equals(treeArg, LatestTreeKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    int latestCount = committedTreeIds != null ? committedTreeIds.Count : 0;
                    if (latestCount == 0)
                        return new RewindToLaunchTarget
                        {
                            Outcome = RewindToLaunchTargetOutcome.NoCommittedTree,
                            RefusalReason = "no-committed-tree",
                        };
                    return new RewindToLaunchTarget
                    {
                        Outcome = RewindToLaunchTargetOutcome.Selected,
                        TreeId = committedTreeIds[latestCount - 1],
                        Resolution = RewindToLaunchTargetResolution.LatestKeyword,
                    };
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
                Resolution = RewindToLaunchTargetResolution.AutoSingle,
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
