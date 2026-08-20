using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // Same-tree dock-claim recovery (design-dock-event-graph.md 6.2): the A->D shape from
    // the 2026-08-12 model extract section 1.5 / 2 step 6. A committed tree's later-session
    // dock records single-parent with a same-tree target pid; FindLinks deliberately skips
    // own-tree claims, so FindSameTreeDockClaims is the derivation that recovers the partner
    // from the absorbed side. Fixtures reuse CrossTreeDockFixture's builders.
    [Collection("Sequential")]
    public class MissionSameTreeDockClaimTests : System.IDisposable
    {
        private const uint PidD = 300;
        private const string GuidD = "dddddddddddddddddddddddddddddddd";
        private const double Dock2UT = 500.0;

        private readonly List<string> logLines = new List<string>();

        public MissionSameTreeDockClaimTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // One tree carrying the accreted A->D shape: A' [400..500] -> Dock(target=pid(D))
        // -> AD [500..600]; D's own pre-dock line D0 [300..380] is a committed terminal leaf
        // in the SAME tree with no edge to the dock branch point.
        private static RecordingTree AccretedTree(
            uint mergedPid = CrossTreeDockFixture.PidA,
            string mergedGuid = CrossTreeDockFixture.GuidA,
            string dGuid = GuidD,
            bool dIsDebris = false)
        {
            var aPrime = CrossTreeDockFixture.Rec(
                "A1", CrossTreeDockFixture.PidA, CrossTreeDockFixture.GuidA, "CA2", 0,
                400, Dock2UT, pods: 1, probes: 0, childBp: "dockbp2");
            var d0 = CrossTreeDockFixture.Rec(
                "D0", PidD, dGuid, "CD", 0, 300, 380, debris: dIsDebris, vessel: "Depot D");
            var ad = CrossTreeDockFixture.Rec(
                "AD", mergedPid, mergedGuid, "CAD", 0, Dock2UT, 600,
                pods: 1, probes: 1, parentBp: "dockbp2", vessel: "Stack AD");
            var bp = CrossTreeDockFixture.BP(
                "dockbp2", BranchPointType.Dock, Dock2UT,
                new[] { "A1" }, new[] { "AD" }, targetPid: PidD, mergeCause: "DOCK");
            return CrossTreeDockFixture.Tree("t1", new[] { aPrime, d0, ad }, new[] { bp });
        }

        [Fact]
        public void RecoveredClaim_Derives_FromTheAbsorbedSide()
        {
            // The gap this feature closes (analysis I2-ii): the dock is in the SAME tree as
            // the partner's line, FindLinks skips it, and before this derivation nothing
            // could name D from the dock. Fails if the recovery is dropped or the claim
            // resolves the wrong recording.
            var tree = AccretedTree();

            var claims = MissionCrossTreeDock.FindSameTreeDockClaims(tree);

            var claim = Assert.Single(claims);
            Assert.Equal("dockbp2", claim.BranchPointId);
            Assert.Equal(Dock2UT, claim.DockUT);
            Assert.Equal(BranchPointType.Dock, claim.ClaimType);
            Assert.Equal(PidD, claim.PartnerPid);
            Assert.Equal("D0", claim.ClaimedRecordingId);
            Assert.Equal("AD", claim.MergedChildRecordingId);
        }

        [Fact]
        public void CrossTreeFindLinks_StillSkipsOwnTree()
        {
            // Guard against the recovery accidentally relaxing FindLinks' tree-inequality
            // skip (the controller side must never be offered its own dock as a partner
            // journey; pinned behavior #2 of the in-game test).
            var tree = AccretedTree();

            var links = MissionCrossTreeDock.FindLinks(tree, new List<RecordingTree> { tree });

            Assert.Empty(links);
        }

        [Fact]
        public void TwoParentMerge_Skipped()
        {
            // A genuine two-parent same-tree merge already closes the partner's line; the
            // co-parent IS the partner. Fails if the scanner double-resolves it into a
            // recovered claim (the section 6.3 resolution-order contract).
            var tree = AccretedTree();
            tree.BranchPoints[0].ParentRecordingIds = new List<string> { "A1", "D0" };

            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
        }

        [Fact]
        public void ZeroTargetPid_Skipped()
        {
            // Degradation contract: an unstamped (pre-decoupling) dock derives nothing,
            // exactly like today. Fails if a zero pid ever mints a claim.
            var tree = AccretedTree();
            tree.BranchPoints[0].TargetVesselPersistentId = 0;

            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
        }

        [Fact]
        public void ParentRecording_NeverClaimed()
        {
            // The single parent is the recorder's own incoming line. If the target pid
            // matches it (self-referential stamp corruption), the claim must not resolve
            // to the parent. Fails if the parent-exclusion guard is dropped.
            var tree = AccretedTree();
            tree.BranchPoints[0].TargetVesselPersistentId = CrossTreeDockFixture.PidA;

            // Candidates with PidA: A1 (the parent, excluded) and AD (merged child pid,
            // excluded + starts at the dock). Nothing else -> no claim.
            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
        }

        [Fact]
        public void PartnerSurvivedAsMergedVessel_ClaimsPreDockLine_NotTheStack()
        {
            // When D was the dock TARGET its Vessel object survives and the merged child
            // carries pid(D): the merged child and every post-dock continuation are pid
            // matches that must NOT be claimed (they would name the stack as its own
            // partner). Fails if the merged-child or pre-dock-start guard is dropped.
            var tree = AccretedTree(mergedPid: PidD, mergedGuid: GuidD);
            // A post-undock continuation of the stack, also carrying pid(D), starting later.
            var cont = CrossTreeDockFixture.Rec("AD2", PidD, GuidD, "CAD", 1, 600, 700);
            tree.Recordings["AD2"] = cont;

            var claims = MissionCrossTreeDock.FindSameTreeDockClaims(tree);

            var claim = Assert.Single(claims);
            Assert.Equal("D0", claim.ClaimedRecordingId);
        }

        [Fact]
        public void PartnerSurvived_GuidGate_RejectsForeignLaunchWithSamePid()
        {
            // Craft-baked pid collision inside one accreted tree: an older launch of the
            // same craft carries pid(D) with a DIFFERENT launch guid. With the partner
            // surviving as the merged vessel the expected guid is known (merged child), so
            // the collision must be rejected, not claimed. Fails if the guid gate is
            // dropped and the wrong launch gets named as the dock partner.
            var tree = AccretedTree(
                mergedPid: PidD, mergedGuid: GuidD,
                dGuid: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            var claims = MissionCrossTreeDock.FindSameTreeDockClaims(tree);

            Assert.Empty(claims);
            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("SameTreeDock:")
                && l.Contains("guidRejected=1"));
        }

        [Fact]
        public void PartnerAbsorbed_UnknownGuid_PidOnlyFallback()
        {
            // When A survived as the merged vessel, D's object was destroyed and no
            // expected guid is derivable: walker parity says pid-only fallback, not a
            // silent decline. Fails if an unknown expected guid starts rejecting claims.
            var tree = AccretedTree(dGuid: null);

            var claim = Assert.Single(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
            Assert.Equal("D0", claim.ClaimedRecordingId);
        }

        [Fact]
        public void DebrisCandidate_NeverClaimed()
        {
            // Same rationale as FindLinks' debris exclusion: guid-less debris with a
            // colliding craft-baked pid would mint a false partner name. Fails if the
            // debris guard is dropped.
            var tree = AccretedTree(dIsDebris: true);

            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
        }

        [Fact]
        public void BoardClaim_Recovered()
        {
            // Board branch points are modeled (FindLinks parity); production does not stamp
            // them in v1 (design Q1) but hand-built or future data must recover. Fails if
            // the type filter regresses to Dock-only.
            var tree = AccretedTree();
            tree.BranchPoints[0].Type = BranchPointType.Board;

            var claim = Assert.Single(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
            Assert.Equal(BranchPointType.Board, claim.ClaimType);
        }

        [Fact]
        public void MultipleClaims_OrderedByDockUTThenBranchPointId()
        {
            // Deterministic output is the contract consumers (graph build, digest rows)
            // sort-stability depends on. Fails if the ordering is dropped.
            var tree = AccretedTree();
            var e0 = CrossTreeDockFixture.Rec("E0", 400, "ffffffffffffffffffffffffffffffff",
                "CE", 0, 100, 200, vessel: "Depot E");
            var ae = CrossTreeDockFixture.Rec("AE", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAE", 0, 250, 300, parentBp: "dockbp1");
            tree.Recordings["E0"] = e0;
            tree.Recordings["AE"] = ae;
            tree.BranchPoints.Add(CrossTreeDockFixture.BP(
                "dockbp1", BranchPointType.Dock, 250.0,
                new[] { "A1" }, new[] { "AE" }, targetPid: 400, mergeCause: "DOCK"));

            var claims = MissionCrossTreeDock.FindSameTreeDockClaims(tree);

            Assert.Equal(2, claims.Count);
            Assert.Equal("dockbp1", claims[0].BranchPointId);
            Assert.Equal("dockbp2", claims[1].BranchPointId);
        }

        [Fact]
        public void DerivedClaim_EmitsSummaryLine_AndSuppressionSilences()
        {
            // The FindLinks evidence rule applied to the sibling: a recovered claim (rows
            // appear) must leave a grep-stable summary. Fails if the diagnostic is removed
            // (silent-derivation blind spot) or SuppressLogging stops being honored.
            var tree = AccretedTree();

            MissionCrossTreeDock.FindSameTreeDockClaims(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("SameTreeDock:")
                && l.Contains("tree=t1") && l.Contains("claims=1"));

            logLines.Clear();
            bool prev = MissionCrossTreeDock.SuppressLogging;
            MissionCrossTreeDock.SuppressLogging = true;
            try
            {
                MissionCrossTreeDock.FindSameTreeDockClaims(tree);
            }
            finally
            {
                MissionCrossTreeDock.SuppressLogging = prev;
            }
            Assert.DoesNotContain(logLines, l => l.Contains("SameTreeDock:"));
        }
    }
}
