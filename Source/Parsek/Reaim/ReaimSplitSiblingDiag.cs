using System;
using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Split-sibling decline diagnostics for the re-aim classifier - the CHEAP INTERIM of
    // REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS (docs/dev/todo-and-known-bugs.md; Design C of
    // docs/dev/plans/optimizer-split-transfer-cohesion.md open question 2 is still the structural
    // cure and is NOT implemented here).
    //
    // THE PROBLEM. `ReaimClassifier.Classify` needs parking orbit + heliocentric coast +
    // direct-child arrival among the segments of a SINGLE loop-unit member. A legitimate load-time
    // split (the deliberately preserved calibration row "SOI traversal while burning -> split")
    // spreads one transfer across two chain-group siblings, and then EVERY member declines with the
    // missing-heliocentric-leg reason - the same reason a mission that never recorded a transfer at
    // all emits. Nothing in the log separates the two, which is what made the original V9 defect
    // cost a full reading run to find.
    //
    // WHAT THIS DOES. Nothing but talk. It reads the per-member classify verdicts the builder
    // already has, notices when a declining member's CHAIN GROUP (the `CopySplitIdentityFields`
    // topology marker: same ChainId, launch guids that do not conclusively differ) does carry the
    // heliocentric leg the member lacks, and returns a clause the caller appends to its log lines.
    // No classification outcome changes, no plan field is written, no schema moves.
    //
    // WHY IT LIVES HERE AND NOT IN THE CLASSIFIER. The classifier is handed ONE member's segments
    // by design (the playtest interleaving bug that forced per-member classification is documented
    // at the ApplyReaim call site). Sibling awareness needs the whole member set, which only the
    // builder has - so the classifier stays pure and per-member, and the annotation is explicitly
    // the BUILDER's, appended OUTSIDE the quoted `reason='...'` the classifier produced.
    //
    // SCOPE, deliberately narrow: only the missing-heliocentric-leg decline class is annotated.
    // Other split shapes can produce other declines (e.g. a first half with parking + coast but no
    // arrival), but the entry names this class as the one that cost the reading run, and widening
    // the predicate without a measured instance would be guessing.
    internal static class ReaimSplitSiblingDiag
    {
        /// <summary>Grep-stable token every clause carries. Do not reword without re-pinning.</summary>
        internal const string GrepToken = "split-sibling-transfer";

        /// <summary>The todo entry this diagnostic serves, stamped into every clause.</summary>
        internal const string FindingId = "REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS";

        /// <summary>
        /// One classified loop-unit member, reduced to what the sibling walk needs. Built by the
        /// caller from the member's Recording + the segment list it handed the classifier, so this
        /// file stays free of Recording / KSP types and is directly unit-testable.
        /// </summary>
        internal sealed class MemberFacts
        {
            /// <summary>The member ordinal the ReaimDiag log lines use (`member#N`).</summary>
            public int Ordinal;
            public string ChainId;
            public string RecordedVesselGuid;
            public bool Supported;
            public string DeclineReason;
            /// <summary>Non-predicted, body-named segment bodies in startUT order (may repeat).</summary>
            public List<string> SegmentBodyNames = new List<string>();
            /// <summary>startUT of the member's earliest usable segment; NaN when it has none.</summary>
            public double EarliestSegmentStartUT = double.NaN;
            /// <summary>Body of that earliest segment - what the classifier calls the launch body.</summary>
            public string EarliestSegmentBodyName;
        }

        /// <summary>
        /// The per-member clauses (index-aligned with the input; null = nothing to say) plus one
        /// aggregate clause for the unit-level decline line (null when no member matched).
        /// </summary>
        internal sealed class Diagnosis
        {
            public string[] MemberClauses = Array.Empty<string>();
            public string AggregateClause;

            internal bool Any => AggregateClause != null;

            internal string ClauseFor(int index)
            {
                return (index >= 0 && index < MemberClauses.Length) ? MemberClauses[index] : null;
            }
        }

        /// <summary>
        /// Builds one <see cref="MemberFacts"/> from a member's classify inputs + verdict. Mirrors
        /// the classifier's own segment filter (non-predicted, body-named) so the diagnostic can
        /// never claim a heliocentric leg the classifier was never shown. <paramref name="segs"/> is
        /// expected in startUT order (the caller sorts it before classifying).
        /// </summary>
        internal static MemberFacts BuildFacts(
            int ordinal, string chainId, string recordedVesselGuid,
            IReadOnlyList<OrbitSegment> segs, ReaimMissionPlan verdict)
        {
            var facts = new MemberFacts
            {
                Ordinal = ordinal,
                ChainId = chainId,
                RecordedVesselGuid = recordedVesselGuid,
                Supported = verdict.Supported,
                DeclineReason = verdict.Reason
            };
            int count = segs?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                OrbitSegment s = segs[i];
                if (s.isPredicted || string.IsNullOrEmpty(s.bodyName))
                    continue;
                facts.SegmentBodyNames.Add(s.bodyName);
                if (double.IsNaN(facts.EarliestSegmentStartUT) || s.startUT < facts.EarliestSegmentStartUT)
                {
                    facts.EarliestSegmentStartUT = s.startUT;
                    facts.EarliestSegmentBodyName = s.bodyName;
                }
            }
            return facts;
        }

        /// <summary>
        /// True when a decline reason is the missing-heliocentric-leg class - the only class this
        /// interim annotates. Ordinal comparison against the classifier's own constant, so the two
        /// cannot drift.
        /// </summary>
        internal static bool IsMissingHeliocentricLegDecline(string reason)
        {
            return string.Equals(reason, ReaimClassifier.MissingHeliocentricLegReason,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Walks the members' chain groups and returns the split-sibling clauses. PURE and
        /// DIAGNOSTIC-ONLY: it reads verdicts, it never produces one.
        ///
        /// A member is annotated when ALL of these hold:
        ///   1. it declined with the missing-heliocentric-leg reason;
        ///   2. it carries a ChainId (no chain marker => it cannot be a load-time split sibling);
        ///   3. its chain group has at least two segment-bearing members;
        ///   4. NO member of that group classified Supported (if one did, the transfer IS whole
        ///      inside a single member and "spread across members" would be a false claim);
        ///   5. some member of the group records a STRICT ANCESTOR of the group's launch body -
        ///      i.e. the heliocentric leg exists in the group even though this member lacks it.
        /// </summary>
        internal static Diagnosis Diagnose(IReadOnlyList<MemberFacts> members, IBodyInfo bodyInfo)
        {
            int n = members?.Count ?? 0;
            var result = new Diagnosis { MemberClauses = new string[n] };
            // A single member cannot be a split sibling, and without a body graph there is no
            // ancestor relation to test.
            if (n < 2 || bodyInfo == null)
                return result;

            List<string> aggregates = null;
            foreach (List<MemberFacts> group in BuildChainGroups(members))
            {
                string clause = DiagnoseGroup(group, bodyInfo, result.MemberClauses, members);
                if (clause == null)
                    continue;
                if (aggregates == null)
                    aggregates = new List<string>();
                aggregates.Add(clause);
            }
            if (aggregates != null)
                result.AggregateClause = string.Join("; ", aggregates.ToArray());
            return result;
        }

        // Chain groups: same ChainId AND launch guids that do not CONCLUSIVELY differ (the
        // CLAUDE.md launch-identity rule - an unknown guid on either side is not a mismatch).
        // Members without a ChainId are not grouped at all: the marker load-time splits stamp is
        // exactly the ChainId, so its absence means "not a split half".
        private static List<List<MemberFacts>> BuildChainGroups(IReadOnlyList<MemberFacts> members)
        {
            var groups = new List<List<MemberFacts>>();
            for (int i = 0; i < members.Count; i++)
            {
                MemberFacts m = members[i];
                if (m == null || string.IsNullOrEmpty(m.ChainId) || m.SegmentBodyNames.Count == 0)
                    continue;
                List<MemberFacts> target = null;
                for (int g = 0; g < groups.Count && target == null; g++)
                {
                    MemberFacts rep = groups[g][0];
                    if (!string.Equals(rep.ChainId, m.ChainId, StringComparison.Ordinal))
                        continue;
                    if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                            rep.RecordedVesselGuid, m.RecordedVesselGuid))
                        continue;
                    target = groups[g];
                }
                if (target == null)
                {
                    target = new List<MemberFacts>();
                    groups.Add(target);
                }
                target.Add(m);
            }
            return groups;
        }

        // Evaluates one chain group, writing per-member clauses into <paramref name="clauses"/>
        // (indexed by the member's position in the ORIGINAL list) and returning the group's
        // aggregate clause, or null when the group is not a split-transfer shape.
        private static string DiagnoseGroup(
            List<MemberFacts> group, IBodyInfo bodyInfo, string[] clauses,
            IReadOnlyList<MemberFacts> members)
        {
            if (group.Count < 2)
                return null;

            // (4) A supported member means the transfer is whole inside one member; whatever the
            //     other members declined for, it is not this split.
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].Supported)
                    return null;
            }

            // The GROUP's launch body: the body of the earliest segment anywhere in the group.
            // (The declining member's own earliest body is not it - that is exactly the bug: the
            // second half of a split starts at the Sun and so has no strict ancestor of its own.)
            MemberFacts earliest = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                if (group[i].EarliestSegmentStartUT < earliest.EarliestSegmentStartUT)
                    earliest = group[i];
            }
            string groupLaunchBody = earliest.EarliestSegmentBodyName;
            if (string.IsNullOrEmpty(groupLaunchBody))
                return null;

            // (5) The nearest STRICT ancestor of the group launch body that the group actually
            //     recorded. Nearest-first so a Kerbin->Sun group names 'Sun', not a pack's root.
            List<string> ancestors = MissionPeriodicity.AncestorChain(groupLaunchBody, bodyInfo);
            string ancestorBody = null;
            for (int a = 1; a < ancestors.Count && ancestorBody == null; a++)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (Records(group[i], ancestors[a]))
                    {
                        ancestorBody = ancestors[a];
                        break;
                    }
                }
            }
            if (ancestorBody == null)
                return null;   // the group genuinely never recorded a heliocentric leg

            var annotated = new List<int>();
            for (int i = 0; i < group.Count; i++)
            {
                MemberFacts m = group[i];
                if (m.Supported || !IsMissingHeliocentricLegDecline(m.DeclineReason))
                    continue;

                bool carriesAncestor = Records(m, ancestorBody);
                MemberFacts sibling = carriesAncestor
                    ? FindSibling(group, m, groupLaunchBody)   // who holds the launch-body legs?
                    : FindSibling(group, m, ancestorBody);     // who holds the heliocentric leg?
                if (sibling == null)
                    continue;

                int slot = IndexOf(members, m);
                if (slot < 0)
                    continue;

                clauses[slot] = carriesAncestor
                    ? string.Format(
                        " | {0}: NOT a missing transfer - THIS member records the '{1}' "
                        + "(common-ancestor) leg but its own earliest body is '{2}', which has no "
                        + "strict ancestor, so Classify declines it; the launch-body legs are in "
                        + "sibling member#{3} of the same chain group (chainId={4} members={5}). "
                        + "The transfer is SPLIT across the group and Classify only ever sees one "
                        + "member ({6})",
                        GrepToken, ancestorBody, m.EarliestSegmentBodyName, sibling.Ordinal,
                        m.ChainId, group.Count, FindingId)
                    : string.Format(
                        " | {0}: NOT a missing transfer - sibling member#{1} of the same chain "
                        + "group (chainId={2} members={3}) records the '{4}' (common-ancestor) leg "
                        + "this member lacks. The transfer is SPLIT across the group and Classify "
                        + "only ever sees one member ({5})",
                        GrepToken, sibling.Ordinal, m.ChainId, group.Count, ancestorBody, FindingId);
                annotated.Add(m.Ordinal);
            }
            if (annotated.Count == 0)
                return null;

            var ordinals = new string[annotated.Count];
            for (int i = 0; i < annotated.Count; i++)
                ordinals[i] = "#" + annotated[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Format(
                " | {0}: chainId={1} members=[{2}] jointly record a '{3}'-legged transfer that no "
                + "SINGLE member carries whole - a load-time split, not a mission without a "
                + "transfer ({4})",
                GrepToken, group[0].ChainId, string.Join(",", ordinals), ancestorBody, FindingId);
        }

        private static bool Records(MemberFacts m, string bodyName)
        {
            for (int i = 0; i < m.SegmentBodyNames.Count; i++)
            {
                if (string.Equals(m.SegmentBodyNames[i], bodyName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // The earliest OTHER member of the group that records the wanted body.
        private static MemberFacts FindSibling(List<MemberFacts> group, MemberFacts self, string bodyName)
        {
            MemberFacts best = null;
            for (int i = 0; i < group.Count; i++)
            {
                MemberFacts c = group[i];
                if (ReferenceEquals(c, self) || !Records(c, bodyName))
                    continue;
                if (best == null || c.EarliestSegmentStartUT < best.EarliestSegmentStartUT)
                    best = c;
            }
            return best;
        }

        private static int IndexOf(IReadOnlyList<MemberFacts> members, MemberFacts m)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (ReferenceEquals(members[i], m))
                    return i;
            }
            return -1;
        }
    }
}
