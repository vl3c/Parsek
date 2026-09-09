using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Where the career's single <see cref="GameActionType.ReputationInitial"/> row came
    /// from, AS SEEN BY ONE COMMIT at the moment that commit ensured the seed exists.
    /// Produced by <c>LedgerOrchestrator.EnsureReputationSeedForCommit</c> and consumed
    /// only by <see cref="KerbalDeathRepPenalty.IsInsideReputationSeed"/>.
    ///
    /// <para>
    /// This is PRODUCTION ORDER, not game time. A rewind moves the game clock backwards,
    /// so comparing a death's UT against the UT the seed was captured at orders nothing:
    /// a post-rewind capture can carry a SMALLER UT than a death that happened before it
    /// in real time, and the mirror case (a re-fly from a RewindPoint earlier than the
    /// capture) reads the other way round. What a commit does know for certain is
    /// whether the live pool the seed was, or will be, read from had already taken the
    /// hit.
    /// </para>
    /// </summary>
    internal enum ReputationSeedOrigin
    {
        /// <summary>
        /// No seed exists yet and this commit did not create one - the live-pool read is
        /// still deferred (<c>Reputation.Instance</c> missing, or |reputation| &lt;= 0.01,
        /// which <c>EnsureInitialReputationSeed</c> refuses to treat as a real balance).
        /// The seed is therefore EXPECTED to be captured LATER, off a live pool that has
        /// already taken any death from this commit - an assumption, not a guarantee. When
        /// the later capture turns out to be a career-start branch instead, the rows
        /// stamped on this expectation are flipped back outside by
        /// <see cref="KerbalDeathRepPenalty.CareerStartSeedInvalidatesInsideStamps"/>.
        /// </summary>
        NotYetCaptured = 0,

        /// <summary>
        /// A ReputationInitial row already existed before this commit ran, so its value
        /// was fixed before this commit's flights were filed.
        /// </summary>
        PreExisting = 1,

        /// <summary>
        /// This commit created the seed by reading the LIVE reputation pool
        /// (<c>Reputation.Instance.reputation</c>), which by then had already taken every
        /// hit this commit is filing.
        /// </summary>
        CreatedThisCommitFromLivePool = 2,

        /// <summary>
        /// This commit created the seed from the career-start
        /// <c>GameStateBaseline</c>. That value predates the flight entirely, so nothing
        /// this commit files can be inside it.
        /// </summary>
        CreatedThisCommitFromCareerBaseline = 3,

        /// <summary>
        /// This commit created the seed through the refusal fallback
        /// (<c>LedgerHasReputationTimelineActions</c> with no baseline available), which
        /// deliberately declines the live pool and seeds career start - the value 0 a
        /// stock career begins at.
        /// </summary>
        CreatedThisCommitFromRefusalFallback = 4,
    }

    /// <summary>
    /// Pure decision for the kerbal-death reputation penalty row (design
    /// docs/parsek-rewind-to-separation-design.md section 7.16).
    ///
    /// <para>
    /// When a crewed vessel is destroyed in flight, stock applies ONE reputation hit
    /// keyed <c>TransactionReasons.VesselLoss</c> per vessel loss - not one per kerbal
    /// aboard - and <c>GameStateRecorder.OnReputationChanged</c> captures it as a
    /// recording-scoped <see cref="GameStateEventType.ReputationChanged"/> event. This
    /// class turns "the recording's crew end states + the recording's captured
    /// reputation events" into "emit one
    /// <see cref="GameActionType.ReputationPenalty"/> row, of this magnitude, at this
    /// UT - or emit nothing, for this named reason".
    /// </para>
    ///
    /// <para>
    /// THE MAGNITUDE IS NEVER FABRICATED. It is only ever the magnitude stock already
    /// took off the pool (<c>valueBefore - valueAfter</c>), which is why
    /// <c>ReputationModule.ProcessRepPenalty</c> treats this source as PRE-CURVED. A
    /// recording with dead crew but no captured event (an injected fixture, a
    /// suppressed event, a sub-threshold penalty) yields NO row: a synthesized -10
    /// would be a guess at a number the curve makes install-state-dependent.
    /// </para>
    ///
    /// Pure - no KSP state access, no logging. The caller logs.
    /// </summary>
    internal static class KerbalDeathRepPenalty
    {
        /// <summary>
        /// The <see cref="GameStateEvent.key"/> a crew-death reputation hit carries.
        /// It is <c>TransactionReasons.VesselLoss.ToString()</c>: stock has no
        /// "CrewKilled" transaction reason at all (the only such symbol in
        /// Assembly-CSharp is <c>GameEvents.onCrewKilled</c>), and both CL-1-pod-impact
        /// flights measured <c>Added -9.999828 (-10) reputation: 'VesselLoss'</c>.
        /// <c>PostWalkActionReconciler</c> maps the row back to this same key.
        /// </summary>
        internal const string VesselLossEventKey = "VesselLoss";

        /// <summary>Reason text when the recording carries no <see cref="KerbalEndState.Dead"/> crew.</summary>
        internal const string ReasonNoDeadCrew = "no dead crew";

        /// <summary>Reason text when dead crew exist but no captured VesselLoss event does.</summary>
        internal const string ReasonNoVesselLossEvent = "no captured VesselLoss event";

        /// <summary>
        /// Outcome of <see cref="Decide"/>. <see cref="Emit"/> false means no row;
        /// <see cref="Reason"/> then names why, for the caller's one Info line.
        /// </summary>
        internal struct Decision
        {
            /// <summary>True when the caller must emit exactly one ReputationPenalty row.</summary>
            public bool Emit;

            /// <summary>
            /// Positive applied magnitude, i.e. <c>valueBefore - valueAfter</c> of the
            /// chosen event. Zero when <see cref="Emit"/> is false.
            /// </summary>
            public float Magnitude;

            /// <summary>UT of the chosen event. Zero when <see cref="Emit"/> is false.</summary>
            public double UT;

            /// <summary>How many crew members ended <see cref="KerbalEndState.Dead"/>.</summary>
            public int DeadCount;

            /// <summary>
            /// How many recording-scoped VesselLoss candidates were found. Greater than
            /// one means the caller should log the ambiguity: the nearest to the death
            /// reference UT was taken.
            /// </summary>
            public int CandidateCount;

            /// <summary>Named refusal reason, or null when <see cref="Emit"/> is true.</summary>
            public string Reason;
        }

        /// <summary>
        /// THE INSIDE-SEED RULE. True iff the penalty this producer is about to file is
        /// ALREADY contained in the career's ReputationInitial seed value, so
        /// <c>ReputationModule</c> must not subtract it a second time.
        ///
        /// <para>
        /// The seed is a single absolute reputation figure. It is inside-the-seed exactly
        /// when the live pool that figure was (or will be) read from had already taken
        /// this hit - and a commit knows that from WHERE the seed came, never from
        /// comparing UTs, because a rewind moves the game clock backwards and a re-fly
        /// can file a death whose UT is smaller than the seed's capture UT yet later in
        /// real time (and the mirror case reads the other way round).
        /// </para>
        ///
        /// <para>
        /// THE REFUSAL FALLBACK IS GROUPED WITH THE CAREER BASELINE, NOT WITH THE LIVE
        /// POOL, and that is a choice worth naming. That branch exists precisely to
        /// DECLINE the live pool; it seeds 0, which is the reputation a stock career
        /// starts at, so its value predates the flight in exactly the way a career-start
        /// baseline does and cannot contain this death. Grouping it with the live-pool
        /// branch would drop the penalty from a timeline seeded at career start, which is
        /// the "penalty goes unmodeled" failure this rule exists to prevent. The
        /// alternative reading - "the branch ran during this commit, so treat it like the
        /// other this-commit branch" - is what the enum member keeps to one line if it
        /// ever needs to change.
        /// </para>
        ///
        /// <para>
        /// The <see cref="ReputationSeedOrigin.NotYetCaptured"/> answer is the one
        /// PREDICTION this rule makes, and it is repaired rather than trusted:
        /// <see cref="CareerStartSeedInvalidatesInsideStamps"/> flips those rows back
        /// outside when the seed is finally created from a career-start value instead.
        /// </para>
        /// </summary>
        internal static bool IsInsideReputationSeed(ReputationSeedOrigin origin)
        {
            switch (origin)
            {
                case ReputationSeedOrigin.NotYetCaptured:
                case ReputationSeedOrigin.CreatedThisCommitFromLivePool:
                    return true;
                case ReputationSeedOrigin.PreExisting:
                case ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline:
                case ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback:
                    return false;
                default:
                    // Unreachable while the enum is exhaustive above. Fail toward
                    // APPLYING the penalty: an unmodeled death is a silent refund on the
                    // next supersede merge, while a double subtraction is visible in the
                    // pool and in the post-walk reconcile.
                    return false;
            }
        }

        /// <summary>
        /// THE RE-STAMP RULE, the mirror of <see cref="IsInsideReputationSeed"/>. True iff
        /// a seed CREATED BY THIS CALL carries a value that predates the flights, so every
        /// row already stamped inside a seed is now stamped against a value that does not
        /// contain it and must be flipped back outside.
        ///
        /// <para>
        /// <see cref="ReputationSeedOrigin.NotYetCaptured"/> is an ASSUMPTION, not a fact:
        /// the producer stamps a death inside the seed because the seed is still deferred
        /// and will therefore be read off a LATER live pool that has already taken the
        /// hit. Nothing guarantees the later capture is a live-pool read. Measured on
        /// CL-2-pod-impact-ledger (run 2026-09-09_2253): the commit's ensure deferred, the
        /// death row was stamped inside, and four milliseconds later the SAME commit's
        /// recalc created the seed through the refusal fallback - career start, value 0,
        /// containing no death. The walk then skipped a -10 that nothing else carried, and
        /// the rebuilt pool read +2 against a live -7.99.
        /// </para>
        ///
        /// <para>
        /// Only the two career-start branches invalidate an earlier stamp. A seed created
        /// from the LIVE POOL contains every death filed before it, so rows stamped inside
        /// it stay inside; <see cref="ReputationSeedOrigin.PreExisting"/> and
        /// <see cref="ReputationSeedOrigin.NotYetCaptured"/> create no seed at all in the
        /// call being answered for and so invalidate nothing.
        /// </para>
        /// </summary>
        internal static bool CareerStartSeedInvalidatesInsideStamps(ReputationSeedOrigin origin)
        {
            switch (origin)
            {
                case ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline:
                case ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback:
                    return true;
                case ReputationSeedOrigin.NotYetCaptured:
                case ReputationSeedOrigin.PreExisting:
                case ReputationSeedOrigin.CreatedThisCommitFromLivePool:
                    return false;
                default:
                    // Unreachable while the enum is exhaustive above. Fail toward NOT
                    // re-stamping: leaving a stamp alone is the behaviour every origin
                    // that creates no seed already gets, while flipping one against a
                    // live-pool seed subtracts the same penalty twice.
                    return false;
            }
        }

        /// <summary>
        /// Counts crew members whose end state is <see cref="KerbalEndState.Dead"/>.
        /// A null list counts zero.
        /// </summary>
        internal static int CountDeadCrew(IReadOnlyList<KerbalEndState> crewEndStates)
        {
            if (crewEndStates == null) return 0;
            int dead = 0;
            for (int i = 0; i < crewEndStates.Count; i++)
            {
                if (crewEndStates[i] == KerbalEndState.Dead)
                    dead++;
            }
            return dead;
        }

        /// <summary>
        /// True iff <paramref name="evt"/> is a reputation LOSS keyed
        /// <see cref="VesselLossEventKey"/> and tagged to <paramref name="recordingId"/>.
        ///
        /// <para>
        /// The tag must match EXACTLY and both sides must be non-empty. An untagged
        /// VesselLoss event is a career-level capture with no owning flight, and
        /// attributing one to a recording would let an unrelated loss pay for this
        /// recording's death. A non-loss (zero or positive) delta under the same key is
        /// not a penalty and is skipped rather than emitted as a negative magnitude.
        /// </para>
        /// </summary>
        internal static bool IsRecordingScopedVesselLoss(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (evt.eventType != GameStateEventType.ReputationChanged) return false;
            if (!string.Equals(evt.key, VesselLossEventKey, StringComparison.Ordinal)) return false;
            if (!string.Equals(evt.recordingId ?? "", recordingId, StringComparison.Ordinal)) return false;
            return (evt.valueBefore - evt.valueAfter) > 0.0;
        }

        /// <summary>
        /// Decides whether <paramref name="recordingId"/> gets a kerbal-death
        /// reputation penalty row.
        ///
        /// <para>
        /// AT MOST ONE ROW PER RECORDING regardless of how many crew died: stock
        /// applies one VesselLoss hit per vessel loss. When more than one scoped
        /// VesselLoss candidate exists (a destroyed vessel that lost a second piece
        /// under the same recording tag), the one NEAREST
        /// <paramref name="deathReferenceUT"/> - the recording's end - wins, with ties
        /// going to the earlier entry in <paramref name="events"/>.
        /// </para>
        /// </summary>
        /// <param name="crewEndStates">End states of the recording's crew.</param>
        /// <param name="events">Any event list; only recording-scoped VesselLoss rows are read.</param>
        /// <param name="recordingId">The recording the row would be scoped to.</param>
        /// <param name="deathReferenceUT">The recording's end UT - where a death lands.</param>
        internal static Decision Decide(
            IReadOnlyList<KerbalEndState> crewEndStates,
            IReadOnlyList<GameStateEvent> events,
            string recordingId,
            double deathReferenceUT)
        {
            var decision = new Decision();
            decision.DeadCount = CountDeadCrew(crewEndStates);

            if (decision.DeadCount == 0)
            {
                decision.Reason = ReasonNoDeadCrew;
                return decision;
            }

            int chosen = -1;
            double bestDistance = 0.0;
            int candidates = 0;
            int eventCount = events == null ? 0 : events.Count;
            for (int i = 0; i < eventCount; i++)
            {
                var evt = events[i];
                if (!IsRecordingScopedVesselLoss(evt, recordingId))
                    continue;

                candidates++;
                double distance = Math.Abs(evt.ut - deathReferenceUT);
                if (chosen < 0 || distance < bestDistance)
                {
                    chosen = i;
                    bestDistance = distance;
                }
            }

            decision.CandidateCount = candidates;
            if (chosen < 0)
            {
                decision.Reason = ReasonNoVesselLossEvent;
                return decision;
            }

            var winner = events[chosen];
            decision.Emit = true;
            decision.Magnitude = (float)(winner.valueBefore - winner.valueAfter);
            decision.UT = winner.ut;
            return decision;
        }
    }
}
