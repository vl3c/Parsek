using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS (docs/dev/todo-and-known-bugs.md).
    //
    // `ReaimClassifier.Classify` needs parking orbit + heliocentric coast + direct-child arrival
    // inside a SINGLE loop-unit member. A legitimate load-time split (the preserved calibration
    // row "SOI traversal while burning -> split") spreads one transfer across two chain-group
    // siblings, and then EVERY member declines with the missing-heliocentric-leg reason - the
    // same reason a mission that genuinely never flew a transfer emits. That indistinguishability
    // is what cost V9 a full reading run to diagnose.
    //
    // These cells own the DIAGNOSTIC surface only. Nothing here may change a classification
    // outcome: the classifier's own `reason=` text is asserted byte-identical across the split and
    // the no-transfer control in every cell below.
    [Collection("Sequential")]
    public class ReaimSplitSiblingDiagTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReaimSplitSiblingDiagTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // Set EXPLICITLY, not just reset in Dispose: the builder-driven cells below assert on
            // emitted lines, so riding whatever the Sequential collection left behind would make
            // them pass or fail on a neighbour's state.
            MissionLoopUnitBuilder.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // --- Fixtures -----------------------------------------------------------------

        // Stock-like Sun/Kerbin/Duna seam. Only the parent graph and the grav parameters matter
        // to the classifier + the split-sibling diagnostic (both decline at the heliocentric gate).
        private sealed class SolBodies : IBodyInfo
        {
            public double RotationPeriod(string b) => b == "Kerbin" ? 21549.425 : double.NaN;
            public double OrbitPeriod(string b)
            {
                if (b == "Kerbin") return 9203545.0;
                if (b == "Duna") return 17315400.0;
                return double.NaN;
            }
            public string ReferenceBodyName(string b)
                => (b == "Kerbin" || b == "Duna") ? "Sun" : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b)
            {
                if (b == "Kerbin") return 3.5316e12;
                if (b == "Duna") return 3.0136321e11;
                if (b == "Sun") return 1.1723328e18;
                return double.NaN;
            }
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private static OrbitSegment Seg(string body, double start, double end, double sma = 1.0e7)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = start,
                endUT = end,
                semiMajorAxis = sma,
                isPredicted = false
            };
        }

        // One chain-group member: a committed Recording carrying the shared split-identity fields
        // `CopySplitIdentityFields` stamps on both halves (same ChainId, same RecordedVesselGuid,
        // chain re-indexed by StartUT) plus its own orbit segments.
        private static Recording ChainLeg(string id, int chainIndex, double start, double end,
            OrbitSegment[] segs, string chainId = "chain-A", string guid = "guid-A")
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Splitter",
                VesselPersistentId = 4242u,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                RecordedVesselGuid = guid
            };
            rec.OrbitSegments.AddRange(segs);
            return rec;
        }

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (Recording r in recs)
                tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        // Drives the real builder over a two-member chain group and returns the emitted log lines.
        private List<string> BuildTwoMemberChain(Recording first, Recording second)
        {
            logLines.Clear();
            RecordingTree tree = Tree("t-split", first, second);
            var committed = new List<Recording> { first, second };
            var mission = new Mission("m-split", tree.Id, "Split Transfer")
            {
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Auto,
                LoopIntervalSeconds = 0.0
            };
            bool built = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                mission,
                new List<RecordingTree> { tree },
                committed,
                30.0,
                new SolBodies(),
                TransitedBodyRotationMode.Loose,
                forceFaithful: false,
                out GhostPlaybackLogic.LoopUnit unit);
            Assert.True(built, "the two-leg chain must resolve a loop unit (no unit => the cell is vacuous)");
            Assert.Equal(2, unit.MemberIndices.Length);
            Assert.False(unit.IsReaim, "both members must DECLINE - these cells are about the decline reason");
            return new List<string>(logLines);
        }

        // THE SPLIT: one Kerbin->Sun->Duna transfer cut across two chain-group siblings. Leg 0
        // holds the launch-body parking, leg 1 holds the heliocentric coast + the arrival. This is
        // the V9 Dres shape (member#1 startBody=Kerbin, member#2 startBody=Sun), reduced.
        private static void SplitTransferChain(out Recording first, out Recording second)
        {
            first = ChainLeg("leg-park", 0, 100.0, 200.0, new[]
            {
                Seg("Kerbin", 100.0, 200.0)
            });
            second = ChainLeg("leg-transfer", 1, 200.0, 400.0, new[]
            {
                Seg("Sun", 200.0, 300.0, 2.4e10),
                Seg("Duna", 300.0, 400.0)
            });
        }

        // THE CONTROL: the same two-member chain group with NO heliocentric leg ANYWHERE - the
        // mission reached Duna but the coast was never warped through / ran in the background, so
        // no common-ancestor segment was ever recorded. This is the shape the classifier's decline
        // reason literally describes, and it is the shape the split must become distinguishable
        // FROM. Structurally identical to the split (same chain group, same member count, same
        // decline reason on every member); only the recorded geometry differs.
        private static void NoTransferChain(out Recording first, out Recording second)
        {
            first = ChainLeg("leg-park", 0, 100.0, 200.0, new[]
            {
                Seg("Kerbin", 100.0, 200.0)
            });
            second = ChainLeg("leg-arrival", 1, 200.0, 400.0, new[]
            {
                Seg("Duna", 200.0, 300.0),
                Seg("Duna", 300.0, 400.0)
            });
        }

        private static List<string> MemberVerdictLines(IEnumerable<string> lines)
        {
            return lines.Where(l => l.Contains("[ReaimDiag]") && l.Contains(" member#")).ToList();
        }

        private static string DeclineLine(IEnumerable<string> lines)
        {
            return lines.Single(l => l.Contains("[Reaim]") && l.Contains("not re-aim ("));
        }

        // The verdict TAIL: everything from `supported=` onwards. The head of the line
        // (`member#N segs=K startBody=B`) is incidental structural bookkeeping that differs
        // between any two fixtures; the tail is the part a reader consults to learn WHY the
        // mission stayed faithful, and it is the part this interim changes.
        private static string VerdictTail(string line)
        {
            int i = line.IndexOf("supported=", StringComparison.Ordinal);
            Assert.True(i >= 0, "expected a member verdict line, got: " + line);
            return line.Substring(i);
        }

        // --- The pin: the CLASSIFICATION is unchanged; only the diagnostics moved ---------

        [Fact]
        public void TheClassifierReasonIsIdenticalForASplitTransferAndForNoTransferAtAll()
        {
            SplitTransferChain(out Recording sa, out Recording sb);
            List<string> splitLines = MemberVerdictLines(BuildTwoMemberChain(sa, sb));

            NoTransferChain(out Recording na, out Recording nb);
            List<string> controlLines = MemberVerdictLines(BuildTwoMemberChain(na, nb));

            Assert.Equal(2, splitLines.Count);
            Assert.Equal(2, controlLines.Count);

            // The classifier's OWN reason - the thing this interim must not change - is the
            // missing-heliocentric-leg decline on all four member verdicts, in both shapes.
            const string reason = "reason='no heliocentric (common-ancestor) leg recorded"
                                  + " - never warped through the coast, or background; staying faithful'";
            Assert.All(splitLines, l => Assert.Contains(reason, l));
            Assert.All(controlLines, l => Assert.Contains(reason, l));

            // ...and the aggregate decline is the same sentinel in both.
            SplitTransferChain(out Recording sa2, out Recording sb2);
            string splitDecline = DeclineLine(BuildTwoMemberChain(sa2, sb2));
            NoTransferChain(out Recording na2, out Recording nb2);
            string controlDecline = DeclineLine(BuildTwoMemberChain(na2, nb2));
            Assert.Contains("not re-aim (no member yields a re-aim transfer); faithful", splitDecline);
            Assert.Contains("not re-aim (no member yields a re-aim transfer); faithful", controlDecline);
        }

        [Fact]
        public void TheSplitAndTheNoTransferVerdictTailsAreNoLongerByteIdentical()
        {
            // THE DEFECT WAS: a reader of KSP.log could not tell "this mission never recorded a
            // transfer" from "this mission's transfer is spread across two members", because every
            // emitted verdict tail was the same bytes. Before this interim landed, the two lists
            // below were Assert.Equal. Now the split names its sibling and the control does not.
            SplitTransferChain(out Recording sa, out Recording sb);
            List<string> split = MemberVerdictLines(BuildTwoMemberChain(sa, sb))
                .Select(VerdictTail).ToList();

            NoTransferChain(out Recording na, out Recording nb);
            List<string> control = MemberVerdictLines(BuildTwoMemberChain(na, nb))
                .Select(VerdictTail).ToList();

            Assert.NotEqual(control, split);

            // The control - a mission that genuinely never recorded a heliocentric leg - stays
            // exactly as it was: no annotation, nothing to chase.
            Assert.All(control, t => Assert.DoesNotContain(ReaimSplitSiblingDiag.GrepToken, t));

            // The split says so on BOTH members, each from its own side of the cut.
            Assert.Equal(2, split.Count);
            Assert.All(split, t => Assert.Contains(ReaimSplitSiblingDiag.GrepToken, t));
            Assert.All(split, t => Assert.Contains(ReaimSplitSiblingDiag.FindingId, t));
            // Both clauses lead with the union verdict - the measured proof that the transfer is
            // there at all, and the reason the "no SINGLE member carries it" wording is honest.
            Assert.All(split, t =>
                Assert.Contains("this chain group's segments classify Supported as Kerbin->Duna via 'Sun'", t));
            // member#0 holds the parking and LACKS the Sun leg -> it points at member#1.
            Assert.Contains("sibling member#1 (chainId=chain-A members=2)", split[0]);
            Assert.Contains("records the 'Sun' (common-ancestor) leg this member lacks", split[0]);
            // member#1 IS the Sun-leg carrier; it declines because it recorded nothing at a strict
            // ancestor of its OWN earliest body -> it points back at member#0.
            Assert.Contains("THIS member records the 'Sun' (common-ancestor) leg but recorded no "
                            + "segment at a strict ancestor of its own earliest body 'Sun'", split[1]);
            Assert.Contains("the 'Kerbin' legs are in sibling member#0 (chainId=chain-A members=2)",
                split[1]);
        }

        [Fact]
        public void TheAggregateDeclineLineAnnotatesTheSplitAndKeepsTheCommittedLaneSubstrings()
        {
            SplitTransferChain(out Recording sa, out Recording sb);
            string splitDecline = DeclineLine(BuildTwoMemberChain(sa, sb));
            NoTransferChain(out Recording na, out Recording nb);
            string controlDecline = DeclineLine(BuildTwoMemberChain(na, nb));

            // THE TRAP GUARD. Three committed harness lanes (V9-dres-player-loop,
            // V11-moho-player-loop, V12-eeloo-player-loop) forbid the exact substring
            // `not re-aim \(no member yields a re-aim transfer\); faithful` as their ENGAGED
            // regression floor, and V10 / V11A forbid the bare `no member yields a re-aim
            // transfer`. Those patterns are applied with re.search over the log body, so the
            // annotation MUST be a suffix - splicing it into the parentheses would leave three
            // lanes green while their guard silently stopped matching.
            const string laneFloor = "not re-aim (no member yields a re-aim transfer); faithful";
            Assert.Contains(laneFloor, splitDecline);
            Assert.Contains(laneFloor, controlDecline);
            Assert.Contains("no member yields a re-aim transfer", splitDecline);
            Assert.Contains("no member yields a re-aim transfer", controlDecline);
            // The suffix starts immediately after the pinned text, so the pinned text is intact.
            Assert.EndsWith(laneFloor, controlDecline);
            int floorEnd = splitDecline.IndexOf(laneFloor, StringComparison.Ordinal) + laneFloor.Length;
            Assert.StartsWith(" | " + ReaimSplitSiblingDiag.GrepToken, splitDecline.Substring(floorEnd));

            // And the aggregate is loud about WHICH members jointly hold the transfer.
            Assert.Contains("chainId=chain-A members=[#0,#1] jointly classify Supported as "
                            + "Kerbin->Duna via 'Sun', which no SINGLE member does", splitDecline);
            Assert.DoesNotContain(ReaimSplitSiblingDiag.GrepToken, controlDecline);
        }

        [Fact]
        public void TheWholeDiagnosticIsSkippedWhenTheBuilderIsSuppressed()
        {
            // The commit claims the diagnostic is skipped entirely - not merely unprinted - when
            // logging is off. Pin the observable half: with the builder suppressed, the split shape
            // emits no ReaimDiag verdicts and no clause anywhere.
            MissionLoopUnitBuilder.SuppressLogging = true;
            try
            {
                SplitTransferChain(out Recording sa, out Recording sb);
                List<string> lines = BuildTwoMemberChain(sa, sb);
                Assert.Empty(MemberVerdictLines(lines));
                Assert.All(lines, l => Assert.DoesNotContain(ReaimSplitSiblingDiag.GrepToken, l));
            }
            finally
            {
                MissionLoopUnitBuilder.SuppressLogging = false;
            }
        }

        // --- The pure core -----------------------------------------------------------------

        private static ReaimSplitSiblingDiag.MemberFacts Facts(
            int ordinal, string chainId, string guid, ReaimMissionPlan plan, params OrbitSegment[] segs)
        {
            return ReaimSplitSiblingDiag.BuildFacts(ordinal, chainId, guid, segs, plan);
        }

        private static ReaimMissionPlan Declined()
        {
            return ReaimMissionPlan.Unsupported(
                "Kerbin", ReaimClassifier.MissingHeliocentricLegReason);
        }

        private static ReaimMissionPlan Supported()
        {
            return new ReaimMissionPlan { Supported = true, TargetBody = "Duna" };
        }

        [Fact]
        public void TheDiagnosticRecognizesTheClassifiersOwnMissingHeliocentricDecline()
        {
            // Drift guard: the predicate matches the string the classifier ACTUALLY emits, not a
            // copy of it. Kerbin parking -> Duna arrival, no Sun segment anywhere.
            ReaimMissionPlan plan = ReaimClassifier.Classify(
                new List<OrbitSegment> { Seg("Kerbin", 0, 100), Seg("Duna", 100, 200) },
                new SolBodies());
            Assert.False(plan.Supported);
            Assert.True(ReaimSplitSiblingDiag.IsMissingHeliocentricLegDecline(plan.Reason),
                "the predicate must recognize the classifier's live output: " + plan.Reason);
            Assert.False(ReaimSplitSiblingDiag.IsMissingHeliocentricLegDecline(null));
            Assert.False(ReaimSplitSiblingDiag.IsMissingHeliocentricLegDecline(
                "no target arrival leg after the heliocentric coast"));
        }

        [Fact]
        public void ASingleMemberIsNeverASplitSibling()
        {
            var one = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Kerbin", 0, 100))
            };
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(one, new SolBodies()).AggregateClause);
        }

        // A two-slice group whose UNION classifies Supported (Kerbin parking | Sun coast + Duna
        // arrival) - the positive fixture every negative cell below is measured against.
        private static List<ReaimSplitSiblingDiag.MemberFacts> SupportedUnionGroup(
            string chainId = "chain-A", string guid0 = "g", string guid1 = "g")
        {
            return new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, chainId, guid0, Declined(), Seg("Kerbin", 100, 200)),
                Facts(1, chainId, guid1, Declined(),
                    Seg("Sun", 200, 300, 2.4e10), Seg("Duna", 300, 400))
            };
        }

        [Fact]
        public void ThePositiveFixtureReallyDoesClassifySupportedAsAUnion()
        {
            // Anti-vacuity anchor: every negative cell below differs from THIS group by exactly one
            // property, so if this stopped being annotated they would all pass for the wrong reason.
            ReaimSplitSiblingDiag.Diagnosis d =
                ReaimSplitSiblingDiag.Diagnose(SupportedUnionGroup(), new SolBodies());
            Assert.NotNull(d.AggregateClause);
            Assert.NotNull(d.MemberClauses[0]);
            Assert.NotNull(d.MemberClauses[1]);
        }

        [Fact]
        public void NoBodyInfoOrNoMembersDiagnosesNothing()
        {
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(null, new SolBodies()).AggregateClause);
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(SupportedUnionGroup(), null).AggregateClause);
        }

        [Fact]
        public void AGroupThatNeverRecordedAHeliocentricLegDiagnosesNothing()
        {
            var noHelio = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Kerbin", 0, 100)),
                Facts(1, "chain-A", "g", Declined(), Seg("Duna", 100, 200))
            };
            ReaimSplitSiblingDiag.Diagnosis d = ReaimSplitSiblingDiag.Diagnose(noHelio, new SolBodies());
            Assert.Null(d.AggregateClause);
            Assert.All(d.MemberClauses, Assert.Null);
        }

        [Fact]
        public void AGroupWithASupportedMemberDiagnosesNothing()
        {
            // The transfer is whole inside member#0; the other member's decline is not a split
            // artifact and claiming "spread across members" would be a false statement.
            var withSupported = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Supported(),
                    Seg("Kerbin", 0, 100), Seg("Sun", 100, 200), Seg("Duna", 200, 300)),
                Facts(1, "chain-A", "g", Declined(), Seg("Kerbin", 0, 300))
            };
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(withSupported, new SolBodies()).AggregateClause);
        }

        [Fact]
        public void MembersWithoutAChainIdAreNotSplitSiblings()
        {
            // No ChainId = no CopySplitIdentityFields marker = these two are unrelated recordings
            // that merely ride the same mission, not two halves of one split. Same segments as the
            // positive fixture, so the ChainId is the only difference.
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(
                SupportedUnionGroup(chainId: null), new SolBodies()).AggregateClause);
        }

        [Fact]
        public void DifferentChainIdsAreDifferentGroups()
        {
            List<ReaimSplitSiblingDiag.MemberFacts> twoChains = SupportedUnionGroup();
            twoChains[1].ChainId = "chain-B";
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(twoChains, new SolBodies()).AggregateClause);
        }

        [Fact]
        public void ConclusivelyDifferentLaunchGuidsSplitTheChainGroup()
        {
            // CLAUDE.md launch identity: a ChainId match is not proof of the same launch when the
            // guids conclusively differ. An UNKNOWN guid is never conclusive, so the second pair
            // (null/empty guids) still groups and is still annotated.
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(
                SupportedUnionGroup(guid0: "11111111111111111111111111111111",
                                    guid1: "22222222222222222222222222222222"),
                new SolBodies()).AggregateClause);

            Assert.NotNull(ReaimSplitSiblingDiag.Diagnose(
                SupportedUnionGroup(guid0: null, guid1: ""), new SolBodies()).AggregateClause);
        }

        [Fact]
        public void APredictedHeliocentricTailIsNotARecordedTransfer()
        {
            // The classifier ignores predicted segments, so the diagnostic must too - otherwise it
            // would claim a sibling records a leg the classifier was never shown. Identical to the
            // positive fixture except the Sun coast is a predicted tail.
            var predicted = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Kerbin", 100, 200)),
                Facts(1, "chain-A", "g", Declined(),
                    new OrbitSegment { bodyName = "Sun", startUT = 200, endUT = 300,
                                       semiMajorAxis = 2.4e10, isPredicted = true },
                    Seg("Duna", 300, 400))
            };
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(predicted, new SolBodies()).AggregateClause);
        }

        [Fact]
        public void OnlyTheMissingHeliocentricDeclineClassIsAnnotated()
        {
            // Scope, deliberately narrow (see the entry): another decline class in a group whose
            // union IS Supported stays unannotated rather than inheriting a claim nobody measured.
            List<ReaimSplitSiblingDiag.MemberFacts> otherReason = SupportedUnionGroup();
            otherReason[0].DeclineReason = "no target arrival leg after the heliocentric coast";
            otherReason[1].DeclineReason =
                "more than one heliocentric leg (multi-hop / gravity assist) - deferred";
            Assert.Null(ReaimSplitSiblingDiag.Diagnose(otherReason, new SolBodies()).AggregateClause);
        }

        // --- The union-classify gate: shapes that must NOT be annotated ---------------------

        [Fact]
        public void AGroupWhoseUnionRecordsNoArrivalIsNotAnnotated()
        {
            // FALSE-CLAIM SHAPE (a): a probe ejected to solar orbit, or a recording that ends
            // mid-coast. Both members decline missing-heliocentric and the group DOES record a
            // strict ancestor of its launch body - but joined it still declines `no target arrival
            // leg after the heliocentric coast`, so there is no transfer to announce.
            var noArrival = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Kerbin", 100, 200)),
                Facts(1, "chain-A", "g", Declined(), Seg("Sun", 200, 300, 2.4e10))
            };
            // The pre-gate predicate would have annotated this - the group DOES record a 'Sun' leg.
            // What the gate adds is the rest of Classify's contract, measured here:
            ReaimMissionPlan union = ReaimClassifier.Classify(
                new List<OrbitSegment> { Seg("Kerbin", 100, 200), Seg("Sun", 200, 300, 2.4e10) },
                new SolBodies());
            Assert.False(union.Supported);
            Assert.Equal("no target arrival leg after the heliocentric coast", union.Reason);

            ReaimSplitSiblingDiag.Diagnosis d = ReaimSplitSiblingDiag.Diagnose(noArrival, new SolBodies());
            Assert.Null(d.AggregateClause);
            Assert.All(d.MemberClauses, Assert.Null);
        }

        [Fact]
        public void AMunReturnSplitAtTheSoiExitIsNotAnnotated()
        {
            // FALSE-CLAIM SHAPE (b): a Mun return cut at the SOI exit - i.e. the DELIBERATELY
            // preserved "SOI traversal while burning -> split" calibration row. Kerbin is a strict
            // ancestor of Mun, so the pre-gate predicate would have announced a "'Kerbin'-legged
            // transfer", which is not an interplanetary transfer at all.
            var munReturn = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Mun", 100, 200)),
                Facts(1, "chain-A", "g", Declined(), Seg("Kerbin", 200, 300))
            };
            ReaimSplitSiblingDiag.Diagnosis d =
                ReaimSplitSiblingDiag.Diagnose(munReturn, new KerbinSystemBodies());
            Assert.Null(d.AggregateClause);
            Assert.All(d.MemberClauses, Assert.Null);
        }

        // --- Non-Sun common ancestor, and the 3-slice sibling choice ------------------------

        [Fact]
        public void ANonSunCommonAncestorIsNamedWithoutClaimingItHasNoParent()
        {
            // Mun -> Kerbin -> Minmus: a same-system transfer whose common ancestor is KERBIN, a
            // body that very much does have a parent. The carrier clause must not assert otherwise
            // - it states the measured fact (this member recorded nothing at a strict ancestor of
            // its OWN earliest body) instead of a graph property that only holds for the Sun.
            var group = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Mun", 0, 100)),
                Facts(1, "chain-A", "g", Declined(),
                    Seg("Kerbin", 100, 200, 1.0e7), Seg("Minmus", 200, 300))
            };
            ReaimSplitSiblingDiag.Diagnosis d =
                ReaimSplitSiblingDiag.Diagnose(group, new KerbinSystemBodies());

            Assert.NotNull(d.AggregateClause);
            Assert.Contains("jointly classify Supported as Mun->Minmus via 'Kerbin'", d.AggregateClause);

            // The non-carrier half.
            Assert.Contains("records the 'Kerbin' (common-ancestor) leg this member lacks",
                d.MemberClauses[0]);
            // THE CARRIER HALF - the clause that used to read "'Kerbin' ... has no strict ancestor".
            Assert.Contains("THIS member records the 'Kerbin' (common-ancestor) leg but recorded no "
                            + "segment at a strict ancestor of its own earliest body 'Kerbin'",
                d.MemberClauses[1]);
            Assert.DoesNotContain("has no strict ancestor", d.MemberClauses[1]);
            Assert.Contains("the 'Mun' legs are in sibling member#0", d.MemberClauses[1]);
        }

        [Fact]
        public void ThreeSliceGroupNamesTheEarliestMatchingSibling()
        {
            // Three time slices of one launch: two Kerbin parking slices then the transfer slice.
            // Both parking halves point at the single Sun carrier; the carrier points back at the
            // EARLIEST launch-body slice (member#0), which is the documented choice - not merely
            // whichever the loop happened to see first.
            var group = new List<ReaimSplitSiblingDiag.MemberFacts>
            {
                Facts(0, "chain-A", "g", Declined(), Seg("Kerbin", 100, 200)),
                Facts(1, "chain-A", "g", Declined(), Seg("Kerbin", 200, 300)),
                Facts(2, "chain-A", "g", Declined(),
                    Seg("Sun", 300, 400, 2.4e10), Seg("Duna", 400, 500))
            };
            ReaimSplitSiblingDiag.Diagnosis d = ReaimSplitSiblingDiag.Diagnose(group, new SolBodies());

            Assert.Contains("members=[#0,#1,#2] jointly classify Supported as Kerbin->Duna via 'Sun'",
                d.AggregateClause);
            Assert.Contains("sibling member#2 (chainId=chain-A members=3)", d.MemberClauses[0]);
            Assert.Contains("sibling member#2 (chainId=chain-A members=3)", d.MemberClauses[1]);
            Assert.Contains("the 'Kerbin' legs are in sibling member#0 (chainId=chain-A members=3)",
                d.MemberClauses[2]);
        }

        // Sun -> Kerbin -> {Mun, Minmus}: the non-Sun-common-ancestor and Mun-return fixtures.
        private sealed class KerbinSystemBodies : IBodyInfo
        {
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => b == "Kerbin" ? 9203545.0 : double.NaN;
            public string ReferenceBodyName(string b)
            {
                if (b == "Mun" || b == "Minmus") return "Kerbin";
                if (b == "Kerbin") return "Sun";
                return null;
            }
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b)
            {
                if (b == "Kerbin") return 3.5316e12;
                if (b == "Mun") return 6.5138398e10;
                if (b == "Minmus") return 1.7658e9;
                if (b == "Sun") return 1.1723328e18;
                return double.NaN;
            }
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }
    }
}
