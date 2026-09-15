namespace Parsek
{
    /// <summary>
    /// Where the career's single <see cref="GameActionType.ReputationInitial"/> row came
    /// from, AS SEEN BY ONE COMMIT at the moment that commit ensured the seed exists.
    /// Produced by <c>LedgerOrchestrator.EnsureReputationSeedForCommit</c> and consumed
    /// only by <see cref="ReputationSeedMembership.IsInsideReputationSeed"/>.
    ///
    /// <para>
    /// This is PRODUCTION ORDER, not game time. A rewind moves the game clock backwards,
    /// so comparing a row's UT against the UT the seed was captured at orders nothing:
    /// a post-rewind capture can carry a SMALLER UT than a row produced before it
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
        /// already taken every reputation row this commit files - an assumption, not a
        /// guarantee. When
        /// the later capture turns out to be a career-start branch instead, the rows
        /// stamped on this expectation are flipped back outside by
        /// <see cref="ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps"/>.
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
    /// THE INSIDE-SEED RULE, for EVERY reputation-changing ledger row.
    ///
    /// <para>
    /// The career carries a single <see cref="GameActionType.ReputationInitial"/> row: one
    /// ABSOLUTE reputation figure. On a save Parsek first meets mid-career there is no
    /// career-start baseline to read it from, so it is captured off the LIVE pool
    /// (<c>Reputation.Instance.reputation</c>) at the first commit that can. That value
    /// already contains every reputation change the flights being committed produced, and
    /// the recording's captured events are then converted into rows and applied ON TOP of
    /// it - the same award counted twice. Measured on
    /// <c>2026-09-09_1815_CL-4-refly-crew-standin</c>: a <c>+1 'Progression'</c> milestone
    /// raised the pool to 0.999999464, the seed was taken at that value, the walk applied
    /// the milestone row again, and <c>PatchReputation: 1.00 -&gt; 2.00</c> wrote the
    /// doubled figure into the career.
    /// </para>
    ///
    /// <para>
    /// The rule is PRODUCTION ORDER, never game time. A rewind moves the game clock
    /// backwards, so "the row's UT precedes the seed's capture UT" orders nothing across a
    /// re-fly: a post-rewind capture can carry a SMALLER UT than a row produced before it
    /// in real time, and the mirror case (a re-fly from a RewindPoint earlier than the
    /// capture) reads the other way round. What a producer does know is whether the live
    /// pool the seed was, or will be, read from had already taken this row's hit - and that
    /// is exactly what <see cref="ReputationSeedOrigin"/> reports.
    /// </para>
    ///
    /// <para>
    /// SCOPE: reputation only. <see cref="GameActionType.FundsInitial"/> and
    /// <see cref="GameActionType.ScienceInitial"/> carry the same shape of hazard and are
    /// NOT addressed here - see the todo entry
    /// <c>REPUTATION-SEED-CAPTURED-MID-FLIGHT-REAPPLIES-PRE-SEED-AWARDS</c>. A row that
    /// also moves funds or science (a milestone, a contract completion) has only its
    /// REPUTATION leg suppressed; the other modules never read this flag.
    /// </para>
    ///
    /// Pure - no KSP state access, no logging. Callers log.
    /// </summary>
    internal static class ReputationSeedMembership
    {
        /// <summary>
        /// THE ROW SET. True for exactly the action types
        /// <c>ReputationModule.ProcessAction</c> turns into a reputation movement, MINUS
        /// <see cref="GameActionType.ReputationInitial"/> itself - the seed row is the
        /// value being reasoned about and can never be inside itself.
        ///
        /// <para>
        /// Kept as an explicit per-member switch rather than a set literal so that a new
        /// <see cref="GameActionType"/> forces an answer here;
        /// <c>ReputationSeedMembershipTests.EveryGameActionType_HasAnAnswer</c> reds when
        /// one is added without one. The default arm answers false, which is the
        /// apply-as-before behaviour every non-reputation row already has.
        /// </para>
        /// </summary>
        internal static bool IsReputationAffectingRow(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                case GameActionType.StrategyActivate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The module-side question: must this row's reputation leg be skipped because the
        /// seed already contains it? An <see cref="GameActionType.ReputationInitial"/> row
        /// carrying the flag (which nothing writes) answers false through
        /// <see cref="IsReputationAffectingRow"/> rather than suppressing the seed itself.
        /// </summary>
        internal static bool ShouldSkipAsInsideSeed(GameActionType type, bool insideReputationSeed)
        {
            return insideReputationSeed && IsReputationAffectingRow(type);
        }

        /// <summary>
        /// THE ORIGIN ARM OF THE INSIDE-SEED RULE. True iff a reputation row this producer
        /// is about to file is ALREADY contained in the career's ReputationInitial seed
        /// value, so <c>ReputationModule</c> must not apply it a second time.
        ///
        /// <para>
        /// The seed is a single absolute reputation figure. It is inside-the-seed exactly
        /// when the live pool that figure was (or will be) read from had already taken
        /// this row's hit - and a commit knows that from WHERE the seed came, never from
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
        /// from the LIVE POOL contains every reputation row filed before it, so rows stamped inside
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

    }
}
