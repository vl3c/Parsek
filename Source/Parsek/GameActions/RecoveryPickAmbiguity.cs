using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// The UT tier that decided a recovery pick in
    /// <c>LedgerOrchestrator.PickRecoveryRecording</c>. The token spellings live in
    /// <see cref="RecoveryPickAmbiguity.TierToken"/> so the pick summary line and the
    /// stage-2 refusal line cannot drift apart.
    /// </summary>
    internal enum RecoveryPickTier
    {
        /// <summary>No candidate survived; the picker returned null.</summary>
        None = 0,

        /// <summary>A survivor whose [StartUT, EndUT] contains the recovery UT.</summary>
        Bracketing,

        /// <summary>No bracketing survivor; the survivor with the largest EndUT at or before the recovery UT.</summary>
        MostRecentEnded,

        /// <summary>Neither of the above; the survivor with the largest EndUT, whatever the recovery UT.</summary>
        GlobalLatest,
    }

    /// <summary>
    /// What <c>LedgerOrchestrator.PickRecoveryRecording</c> decided, with the two facts the
    /// XP leg needs beyond the id: which candidates SURVIVED the stage-1 launch-guid filter,
    /// and which tier chose among them.
    ///
    /// <para>
    /// <see cref="Survivors"/> is the POST-filter set (a subsequence of the name-match set,
    /// in store order). Reading a pre-filter set here would reinstate candidates stage 1
    /// removed and break the monotone property the whole stage-1 safety argument rests on -
    /// see <c>LedgerOrchestrator.FindSessionProvisionalAmong</c>, which carries the same
    /// constraint for the tier-1 tie-break.
    /// </para>
    /// </summary>
    internal struct RecoveryPickResult
    {
        public string RecordingId;
        public RecoveryPickTier Tier;

        /// <summary>Post-filter candidates, in input order. Never null.</summary>
        public List<Recording> Survivors;

        /// <summary>Candidates before the stage-1 launch-guid filter ran.</summary>
        public int NameMatchCount;

        /// <summary>How many candidates the stage-1 launch-guid filter dropped.</summary>
        public int GuidDropped;

        public int SurvivorCount => Survivors == null ? 0 : Survivors.Count;
    }

    /// <summary>
    /// What the SURVIVOR set says about how many distinct LAUNCHES it could be drawn from.
    /// The stage-2 predicate turns on this rather than on the survivor count alone, because
    /// a count cannot tell one launch's chained segments apart from two launches.
    /// </summary>
    internal enum SurvivorLaunchCorroboration
    {
        /// <summary>No survivors.</summary>
        None = 0,

        /// <summary>
        /// EVERY survivor carries a KNOWN launch guid and they are all the same one - the
        /// set is positively one launch, whatever its segment count.
        /// </summary>
        OneKnownLaunch,

        /// <summary>
        /// At least one survivor's launch guid is UNKNOWN, so nothing can corroborate the
        /// set as a single launch. Fail-closed: unknown is not "same".
        /// </summary>
        UnknownLaunchGuid,

        /// <summary>
        /// Two survivors carry known launch guids that differ - the set provably spans more
        /// than one launch.
        /// </summary>
        DistinctKnownLaunches,
    }

    /// <summary>
    /// The outcome of <see cref="RecoveryPickAmbiguity.Evaluate"/>: is this recovery pick
    /// too weakly determined for an IRREVERSIBLE row to be written against it?
    /// </summary>
    internal struct RecoveryPickAmbiguityResult
    {
        public bool IsAmbiguous;

        /// <summary>
        /// A grep-stable token naming the branch taken. <see cref="RecoveryPickAmbiguity.AmbiguousReason"/>
        /// when ambiguous; one of the named not-ambiguous tokens otherwise, so a live log can
        /// tell "the check ran and cleared it" apart from "the check never ran".
        /// </summary>
        public string Reason;

        public int SurvivorCount;
        public RecoveryPickTier Tier;
        public SurvivorLaunchCorroboration Corroboration;
    }

    /// <summary>
    /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY, STAGE 2: the ambiguity predicate that
    /// makes the KERBAL XP leg (and ONLY the XP leg) refuse to write against a recovery pick
    /// nothing determines.
    ///
    /// <para>
    /// <b>Why only the XP leg.</b> A recovery's funds and science rows are re-derived
    /// idempotently from the effective ledger on every recalc, so a mis-scoped one is wrong
    /// but REVISABLE - refusing those would drop a real payout to buy safety they do not
    /// need. A <c>KerbalExperience</c> row feeds <c>KerbalsModule.ReassertCareerLogEntries</c>,
    /// whose facade appends career entries with NO remove counterpart: a mis-scoped one is
    /// walked back only by a tombstone on the row, written by the WRONG merge. Where the
    /// consequence is irreversible, a missing row strictly dominates a wrong one.
    /// </para>
    ///
    /// <para>
    /// <b>THE PREDICATE.</b> A pick is ambiguous when ALL THREE hold:
    /// </para>
    /// <list type="number">
    /// <item>
    /// more than one candidate SURVIVED the stage-1 launch-guid filter
    /// (<c>LedgerOrchestrator.FilterRecoveryCandidatesByLaunchGuid</c>);
    /// </item>
    /// <item>
    /// the tier that chose among them is WEAK - <see cref="RecoveryPickTier.MostRecentEnded"/>
    /// or <see cref="RecoveryPickTier.GlobalLatest"/>, the two that rank by an EndUT ordering
    /// alone;
    /// </item>
    /// <item>
    /// the survivors are NOT positively corroborated as ONE launch - i.e.
    /// <see cref="ClassifySurvivorLaunches"/> returns something other than
    /// <see cref="SurvivorLaunchCorroboration.OneKnownLaunch"/>.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>CLAUSE 3 IS THE ONE THAT KEEPS THIS FROM GOING VACUOUS, and it was derived from
    /// measurement rather than from theory.</b> The recommendation's wording ("the filtered
    /// candidate set still holds more than one recording and the winner is decided by a weak
    /// tier") is clauses 1+2 alone, and clauses 1+2 alone REFUSE THE ORDINARY SINGLE-LAUNCH
    /// CAREER RECOVERY. Two independent measurements say so: the committed career fixture
    /// <c>Source/Parsek.Tests/Fixtures/C2CareerPostFix/</c> carries TWO chained
    /// <c>Jumping Flea</c> recordings under one launch guid <c>f77e4207...</c>, both ending
    /// before the recovery, so its pick is <c>survivors=2 tier=most-recent-ended</c>; and the
    /// flown stage-1 proof (<c>L6-career-same-name-recover</c> run <c>2026-09-02_1328</c>)
    /// measured <c>nameMatches=4 survivors=2 guidDropped=2 tier=most-recent-ended</c> with
    /// the XP row PRESENT. Both survivor sets are one launch's CHAINED SEGMENTS, not two
    /// launches. Refusing them is exactly the failure the entry's "What NOT to do" paragraph
    /// names - it would refuse the recoveries stage 1 was written to capture and leave
    /// <c>L4</c>'s <c>KerbalXp</c> facet vacuous again. Pinned by
    /// <c>CommittedCareerFixture_RecoveryPickIsNotAmbiguous</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Corroboration is POSITIVE, and <c>VesselLaunchIdentity.RecordingsShareLaunch</c> is
    /// deliberately NOT the helper used.</b> That predicate requires equal
    /// <c>persistentId</c> plus guids that do not CONCLUSIVELY differ - and
    /// <c>persistentId</c> is craft-baked and reused verbatim on every launch of a craft, so
    /// it reads TRUE for two guid-less launches of one craft: precisely the ambiguous shape
    /// this must catch (and the shape <c>career-same-name-pad</c> was built around, its
    /// colliding pid left in deliberately). Clause 3 instead demands that every survivor
    /// carry a KNOWN guid and that they all be the same one. Unknown is never "same".
    /// </para>
    ///
    /// <para>
    /// <b>A SINGLE survivor is never ambiguous, whatever the tier</b> - there is nothing to
    /// be ambiguous between. That is also why an unknown-guid recovery with one name match
    /// writes its row exactly as before: the filter is inert there and the set holds one
    /// recording.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="RecoveryPickTier.GlobalLatest"/> counts as weak</b>, matching the
    /// recommendation's own wording ("<c>most-recent-ended</c> or <c>global-latest</c> rather
    /// than bracketing"), and it is if anything weaker than tier 2: it fires only when NO
    /// survivor brackets the recovery UT and NO survivor ended at or before it, so every
    /// candidate ends strictly after the recovery and the winner is chosen by an EndUT
    /// ordering with no relation to the recovery moment at all.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="RecoveryPickTier.Bracketing"/> is NOT weak, even with several bracketing
    /// survivors from different launches</b>, and that is a deliberate refusal to widen the
    /// entry's rule. A bracketing survivor CONTAINS the recovery UT - a positive temporal
    /// fact about that candidate, not merely an ordering among candidates - and tier 1
    /// already carries a reasoned tie-break (TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT prefers
    /// the live session's provisional over largest-EndUT). Pinned by
    /// <c>Evaluate_BracketingIsNeverAmbiguous_EvenWithDistinctKnownLaunches</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Composed with the filter, the predicate is monotone in the safe direction.</b> It
    /// reads the POST-FILTER survivor set only and never re-derives a candidate set, so it
    /// cannot reinstate a candidate stage 1 dropped. More conclusive guid information can
    /// only turn an ambiguous pick into an unambiguous one (a foreign launch gets dropped by
    /// the filter, or an unknown recording guid becomes a known matching one), never the
    /// reverse.
    /// </para>
    ///
    /// <para>
    /// Pure and stateless: no Unity, no KSP, no store reads.
    /// </para>
    /// </summary>
    internal static class RecoveryPickAmbiguity
    {
        /// <summary>The refusal token. Mirrors the existing <c>reason=no-recovery-recording</c> fail-safe.</summary>
        internal const string AmbiguousReason = "ambiguous-recovery-recording";

        /// <summary>Exactly one candidate survived the guid filter - nothing to be ambiguous between.</summary>
        internal const string SingleSurvivorReason = "single-survivor";

        /// <summary>No candidate survived. The picker returns null there and the caller's existing no-recovery-recording refusal fires first.</summary>
        internal const string NoSurvivorsReason = "no-survivors";

        /// <summary>The winning tier brackets the recovery UT - a positive temporal fact, not an ordering.</summary>
        internal const string BracketingReason = "bracketing-pick";

        /// <summary>Several survivors, weak tier - but all of them are one positively identified launch's segments.</summary>
        internal const string OneCorroboratedLaunchReason = "one-corroborated-launch";

        /// <summary>No tier chose anything (defensive; the caller refuses earlier).</summary>
        internal const string NoPickReason = "no-pick";

        /// <summary>
        /// Cap on the survivor ids rendered into the refusal line. The survivor set is a
        /// name-match set and has no bound in principle (a long career can hold many launches
        /// of one craft name), so the log line is bounded rather than trusting the shape.
        /// </summary>
        internal const int MaxLoggedSurvivorIds = 8;

        /// <summary>
        /// The tier token as it appears in the log. ONE source for both the pick summary line
        /// and the refusal line, so a rename cannot desynchronize the two greps.
        /// </summary>
        internal static string TierToken(RecoveryPickTier tier)
        {
            switch (tier)
            {
                case RecoveryPickTier.Bracketing: return "bracketing";
                case RecoveryPickTier.MostRecentEnded: return "most-recent-ended";
                case RecoveryPickTier.GlobalLatest: return "global-latest";
                default: return "none";
            }
        }

        /// <summary>Log token for <see cref="SurvivorLaunchCorroboration"/>.</summary>
        internal static string CorroborationToken(SurvivorLaunchCorroboration corroboration)
        {
            switch (corroboration)
            {
                case SurvivorLaunchCorroboration.OneKnownLaunch: return "one-known-launch";
                case SurvivorLaunchCorroboration.UnknownLaunchGuid: return "unknown-launch-guid";
                case SurvivorLaunchCorroboration.DistinctKnownLaunches: return "distinct-known-launches";
                default: return "none";
            }
        }

        /// <summary>
        /// True for the tiers that rank candidates by an EndUT ordering alone:
        /// <see cref="RecoveryPickTier.MostRecentEnded"/> and
        /// <see cref="RecoveryPickTier.GlobalLatest"/>. See the type remarks for why
        /// <see cref="RecoveryPickTier.Bracketing"/> is not one of them.
        /// </summary>
        internal static bool IsWeakTier(RecoveryPickTier tier)
        {
            return tier == RecoveryPickTier.MostRecentEnded
                || tier == RecoveryPickTier.GlobalLatest;
        }

        /// <summary>
        /// How many distinct LAUNCHES the survivor set could be drawn from. A null entry is
        /// treated as an unknown guid (fail-closed), and
        /// <see cref="SurvivorLaunchCorroboration.DistinctKnownLaunches"/> takes precedence
        /// over <see cref="SurvivorLaunchCorroboration.UnknownLaunchGuid"/> because it is the
        /// stronger statement about the same set.
        /// </summary>
        internal static SurvivorLaunchCorroboration ClassifySurvivorLaunches(
            IReadOnlyList<Recording> survivors)
        {
            if (survivors == null || survivors.Count == 0)
                return SurvivorLaunchCorroboration.None;

            bool sawUnknown = false;
            bool sawDistinct = false;
            string first = null;

            for (int i = 0; i < survivors.Count; i++)
            {
                var rec = survivors[i];
                string guid = rec == null
                    ? null
                    : VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid);
                if (string.IsNullOrEmpty(guid))
                {
                    sawUnknown = true;
                    continue;
                }
                if (first == null)
                {
                    first = guid;
                    continue;
                }
                if (!string.Equals(first, guid, StringComparison.OrdinalIgnoreCase))
                    sawDistinct = true;
            }

            if (sawDistinct) return SurvivorLaunchCorroboration.DistinctKnownLaunches;
            if (sawUnknown) return SurvivorLaunchCorroboration.UnknownLaunchGuid;
            return SurvivorLaunchCorroboration.OneKnownLaunch;
        }

        /// <summary>
        /// The stage-2 decision. <paramref name="survivors"/> MUST be the post-filter set
        /// (see <see cref="RecoveryPickResult.Survivors"/>).
        /// </summary>
        internal static RecoveryPickAmbiguityResult Evaluate(
            IReadOnlyList<Recording> survivors, RecoveryPickTier tier)
        {
            int count = survivors == null ? 0 : survivors.Count;
            var corroboration = ClassifySurvivorLaunches(survivors);

            var result = new RecoveryPickAmbiguityResult
            {
                IsAmbiguous = false,
                SurvivorCount = count,
                Tier = tier,
                Corroboration = corroboration
            };

            if (count == 0)
            {
                result.Reason = NoSurvivorsReason;
                return result;
            }
            if (tier == RecoveryPickTier.None)
            {
                result.Reason = NoPickReason;
                return result;
            }
            if (count == 1)
            {
                // The shape a legacy no-guid recording produces, and the shape any career
                // with one recording of that name produces. Nothing to be ambiguous between.
                result.Reason = SingleSurvivorReason;
                return result;
            }
            if (!IsWeakTier(tier))
            {
                result.Reason = BracketingReason;
                return result;
            }
            if (corroboration == SurvivorLaunchCorroboration.OneKnownLaunch)
            {
                // One launch's chained segments. THE MEASURED ORDINARY CASE - see the type
                // remarks: this is what C2CareerPostFix and the flown L6 proof both produce.
                result.Reason = OneCorroboratedLaunchReason;
                return result;
            }

            result.IsAmbiguous = true;
            result.Reason = AmbiguousReason;
            return result;
        }

        /// <summary>
        /// A bounded, comma-separated rendering of the survivor ids for the refusal line:
        /// at most <see cref="MaxLoggedSurvivorIds"/> ids, then a <c>+N more</c> tail.
        /// Null / empty renders as <c>(none)</c>.
        /// </summary>
        internal static string FormatSurvivorIds(IReadOnlyList<Recording> survivors)
        {
            if (survivors == null || survivors.Count == 0)
                return "(none)";

            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < survivors.Count && shown < MaxLoggedSurvivorIds; i++)
            {
                var rec = survivors[i];
                if (rec == null) continue;
                if (shown > 0) sb.Append(',');
                sb.Append(string.IsNullOrEmpty(rec.RecordingId) ? "(null-id)" : rec.RecordingId);
                shown++;
            }

            if (shown == 0) return "(none)";

            int remaining = survivors.Count - shown;
            if (remaining > 0)
            {
                sb.Append("+");
                sb.Append(remaining.ToString(CultureInfo.InvariantCulture));
                sb.Append(" more");
            }
            return sb.ToString();
        }
    }
}
