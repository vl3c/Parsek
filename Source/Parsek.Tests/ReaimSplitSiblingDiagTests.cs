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

        // --- The pin: today both shapes decline with the SAME reason ---------------------

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
        public void TodayTheSplitAndTheNoTransferVerdictTailsAreByteIdentical()
        {
            // THE DEFECT, stated as an assertion: a reader of KSP.log cannot tell "this mission
            // never recorded a transfer" from "this mission's transfer is spread across two
            // members", because every emitted verdict tail is the same bytes.
            SplitTransferChain(out Recording sa, out Recording sb);
            List<string> split = MemberVerdictLines(BuildTwoMemberChain(sa, sb))
                .Select(VerdictTail).ToList();

            NoTransferChain(out Recording na, out Recording nb);
            List<string> control = MemberVerdictLines(BuildTwoMemberChain(na, nb))
                .Select(VerdictTail).ToList();

            Assert.Equal(control, split);
        }
    }
}
