using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE PARTNER RULE (ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY), pinned headlessly.
    ///
    /// <para><see cref="RouteProofCapture.SelectStartDockedOriginHalf"/> answers "which half of
    /// this settled dock seam is the origin depot?" from the two halves' own
    /// <c>DockedVesselInfo</c> records, and from NOTHING ELSE. What it must never read is which
    /// half stock made dominant on the merge: the H57 rig couples the depot INTO the transport
    /// with a raw <c>Part.Couple</c>, so the depot is the child that LEAVES, while in the
    /// canonical supply shape (a Base depot, a Ship transport) the depot is dominant and STAYS.
    /// A dominance-derived rule is wrong on exactly one of those two, which is why the rule is
    /// depot-TYPED. Derivation: docs/dev/research/origin-proof-partner-identity-memo.md.</para>
    /// </summary>
    [Collection("Sequential")]
    public class OriginHalfSelectionTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OriginHalfSelectionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static DockSeamHalfIdentity Half(VesselType type, uint root = 10u, string name = "half")
        {
            return new DockSeamHalfIdentity(true, name, (int)type, root);
        }

        // ---------- the depot / cargo-owner predicates ----------

        [Theory]
        [InlineData(VesselType.Base, true)]
        [InlineData(VesselType.Station, true)]
        [InlineData(VesselType.Ship, false)]
        [InlineData(VesselType.Rover, false)]
        [InlineData(VesselType.Lander, false)]
        [InlineData(VesselType.Probe, false)]
        [InlineData(VesselType.Plane, false)]
        [InlineData(VesselType.Relay, false)]
        [InlineData(VesselType.Debris, false)]
        [InlineData(VesselType.EVA, false)]
        [InlineData(VesselType.Flag, false)]
        [InlineData(VesselType.SpaceObject, false)]
        [InlineData(VesselType.Unknown, false)]
        public void IsDepotVesselType_AdmitsOnlyBaseAndStation(VesselType type, bool expected)
        {
            // FAILS IF: the depot set widens. Every other type is something a player FLIES,
            // and the rule's whole content is that the flown half is the transport.
            Assert.Equal(expected, RouteProofCapture.IsDepotVesselType((int)type));
        }

        [Theory]
        [InlineData(VesselType.Base, true)]
        [InlineData(VesselType.Station, true)]
        [InlineData(VesselType.Ship, true)]
        [InlineData(VesselType.Plane, true)]
        [InlineData(VesselType.Rover, true)]
        [InlineData(VesselType.Lander, true)]
        [InlineData(VesselType.Probe, true)]
        [InlineData(VesselType.Relay, true)]
        [InlineData(VesselType.Debris, false)]
        [InlineData(VesselType.SpaceObject, false)]
        [InlineData(VesselType.Unknown, false)]
        [InlineData(VesselType.EVA, false)]
        [InlineData(VesselType.Flag, false)]
        [InlineData(VesselType.DeployedScienceController, false)]
        [InlineData(VesselType.DeployedSciencePart, false)]
        public void IsValidCargoOwnerVesselType_MatchesTheDesignDocRejectSet(
            VesselType type, bool expected)
        {
            // FAILS IF: the reject set drifts from the route design doc's own sentence
            // ("the start-docked vessel is a ghost/EVA/debris/invalid cargo owner -> route
            // analysis rejects the candidate", section 7). Admitting Debris here would let a
            // discarded booster still docked to the transport read as a supply depot.
            Assert.Equal(expected, RouteProofCapture.IsValidCargoOwnerVesselType((int)type));
        }

        [Fact]
        public void IsValidCargoOwnerVesselType_UnknownTypeSentinel_Rejects()
        {
            Assert.False(RouteProofCapture.IsValidCargoOwnerVesselType(-1));
        }

        // ---------- the selection over every shape ----------

        [Fact]
        public void CanonicalSupplyShape_BaseDepotAndShipTransport_PicksTheDepot()
        {
            // The shipping shape: a transport flies to a base and docks. Stock makes the Base
            // dominant (VesselType priority in Vessel.GetDominantVessel), so here the rule and
            // dominance AGREE - which is what makes the next cell the load-bearing one.
            Assert.Equal(
                OriginHalfSelection.OriginIsNear,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Base, 100u), Half(VesselType.Ship, 200u)));
            Assert.Equal(
                OriginHalfSelection.OriginIsFar,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Ship, 200u), Half(VesselType.Base, 100u)));
        }

        [Fact]
        public void InvertedRigTopology_DepotIsTheChildThatLeaves_StillPicksTheDepot()
        {
            // THE CELL THAT MAKES THE RULE MEAN SOMETHING. H57's rig runs
            // depot.Port.Couple(transportPort), which absorbs the DEPOT into the transport,
            // so the merged vessel is the transport, v.rootPart is the transport's root, and
            // the depot is the half that leaves at undock. The old producer stamped
            // v.persistentId and therefore named the TRANSPORT as its own origin. Nothing in
            // the selection inputs can express dominance, so the answer is unchanged.
            OriginHalfSelection selection = RouteProofCapture.SelectStartDockedOriginHalf(
                Half(VesselType.Base, root: 100u, name: "depot"),
                Half(VesselType.Probe, root: 200u, name: "transport"));
            Assert.Equal(OriginHalfSelection.OriginIsNear, selection);
        }

        [Fact]
        public void StationAndShip_PicksTheStation()
        {
            Assert.Equal(
                OriginHalfSelection.OriginIsFar,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Ship, 200u), Half(VesselType.Station, 100u)));
        }

        [Fact]
        public void NeitherHalfIsADepot_ReadsNoDepotHalf()
        {
            // Two ships docked in orbit: a crew transfer, a refuel, a rendezvous. There is no
            // depot, so there is no origin, and the safe answer is no proof at all rather than
            // a coin flip between two identical halves.
            Assert.Equal(
                OriginHalfSelection.NoDepotHalf,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Ship, 100u), Half(VesselType.Probe, 200u)));
        }

        [Fact]
        public void BothHalvesAreDepots_ReadsBothHalvesDepot()
        {
            // A base docked to a station: both are supply points and neither is the transport.
            Assert.Equal(
                OriginHalfSelection.BothHalvesDepot,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Base, 100u), Half(VesselType.Station, 200u)));
        }

        [Theory]
        [InlineData(VesselType.Debris)]
        [InlineData(VesselType.EVA)]
        [InlineData(VesselType.Flag)]
        [InlineData(VesselType.SpaceObject)]
        public void InvalidCargoOwnerOnEitherSide_Rejects(VesselType bad)
        {
            // Checked in BOTH directions on purpose: an asymmetric guard would admit exactly
            // the shape it was written to reject, just from the other node.
            Assert.Equal(
                OriginHalfSelection.InvalidCargoOwner,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Base, 100u), Half(bad, 200u)));
            Assert.Equal(
                OriginHalfSelection.InvalidCargoOwner,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(bad, 200u), Half(VesselType.Base, 100u)));
        }

        [Fact]
        public void MissingHalfInfo_ReadsHalfIdentityMissing()
        {
            var absent = new DockSeamHalfIdentity(false, null, -1, 0u);
            Assert.Equal(
                OriginHalfSelection.HalfIdentityMissing,
                RouteProofCapture.SelectStartDockedOriginHalf(Half(VesselType.Base, 100u), absent));
            Assert.Equal(
                OriginHalfSelection.HalfIdentityMissing,
                RouteProofCapture.SelectStartDockedOriginHalf(absent, Half(VesselType.Base, 100u)));
        }

        [Fact]
        public void ZeroRootPartId_ReadsHalfIdentityMissing()
        {
            // A half whose root id is 0 has no usable identity, and 0 is also the resolver's
            // "no origin" sentinel - admitting it would produce a proof naming nothing.
            var zeroRoot = new DockSeamHalfIdentity(true, "depot", (int)VesselType.Base, 0u);
            Assert.Equal(
                OriginHalfSelection.HalfIdentityMissing,
                RouteProofCapture.SelectStartDockedOriginHalf(zeroRoot, Half(VesselType.Ship, 200u)));
            Assert.Equal(
                OriginHalfSelection.HalfIdentityMissing,
                RouteProofCapture.SelectStartDockedOriginHalf(Half(VesselType.Ship, 200u), zeroRoot));
        }

        [Fact]
        public void InvalidCargoOwnerBeatsBothDepot_OrderIsPartOfTheContract()
        {
            // A Base docked to a Flag is rejected as an invalid cargo owner, not classified as
            // a one-depot pair. The order matters because the reject set is the design doc's
            // guard and must not be reachable-around.
            Assert.Equal(
                OriginHalfSelection.InvalidCargoOwner,
                RouteProofCapture.SelectStartDockedOriginHalf(
                    Half(VesselType.Base, 100u), Half(VesselType.Flag, 200u)));
        }

        // ---------- the resolver keyed on the origin root ----------

        private static OriginPartnerCandidate Candidate(uint seamPart, uint originRoot, int situation)
        {
            return new OriginPartnerCandidate(
                seamPart, originRoot, "depot", (int)VesselType.Base,
                originRoot + 1u, (int)VesselType.Ship,
                situation, "Kerbin", 0.1, -74.7, 68.9);
        }

        [Fact]
        public void Resolver_TwoSeamsOnOneMergedPair_CollapseToOneOrigin()
        {
            // A two-port dock produces two seams naming the SAME origin half, so the resolver
            // must read Captured, not PartnerAmbiguous.
            var candidates = new List<OriginPartnerCandidate>
            {
                Candidate(11u, 555u, (int)Vessel.Situations.LANDED),
                Candidate(12u, 555u, (int)Vessel.Situations.LANDED),
            };
            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedOriginPartner(
                (int)Vessel.Situations.LANDED, false, candidates, out uint originRoot);
            Assert.Equal(OriginProofDetection.Captured, outcome);
            Assert.Equal(555u, originRoot);
        }

        [Fact]
        public void Resolver_TwoDifferentDepots_ReadsAmbiguous()
        {
            var candidates = new List<OriginPartnerCandidate>
            {
                Candidate(11u, 555u, (int)Vessel.Situations.LANDED),
                Candidate(12u, 777u, (int)Vessel.Situations.LANDED),
            };
            Assert.Equal(
                OriginProofDetection.PartnerAmbiguous,
                RouteProofCapture.TryResolveStartDockedOriginPartner(
                    (int)Vessel.Situations.LANDED, false, candidates, out _));
        }

        // ---------- the no-depot announcement (the case the rule fail-closes on) ----------

        private static ConfigNode Snapshot()
        {
            var vessel = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("persistentId", "100");
            part.AddValue("name", "fuelTank");
            vessel.AddNode(part);
            return vessel;
        }

        [Fact]
        public void NoDepotHalf_AnnouncesAtInfo_KeyedOnSCANNEDSeamsNotAcceptedCandidates()
        {
            // THE BUG THIS CELL EXISTS FOR. A seam whose halves are not depot-typed adds NO
            // candidate - the producer loop skips it - so the accepted list is EMPTY on
            // exactly the case the message is written for. Keying the announcement on
            // candidates.Count therefore made it unreachable: it could only ever have fired
            // when a proof was capturable, which is when it is not needed. The scanned seam
            // count is the input that distinguishes "started docked and found no depot" from
            // "started undocked".
            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)Vessel.Situations.LANDED,
                activeVesselIsEva: false,
                candidates: new List<OriginPartnerCandidate>(),
                settledDockSeamsScanned: 2,
                snapshot: Snapshot(),
                isGloopsMode: false,
                vesselContext: "<test>",
                recordingVesselId: 7u,
                out RouteOriginProof proof,
                out List<uint> _);

            Assert.Null(proof);
            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof skipped: no depot half")
                && l.Contains("seams=2")
                && l.Contains("candidates=0")
                && l.Contains("set the depot's type in the tracking station"));
        }

        [Fact]
        public void NoSeamsAtAll_StaysQuiet_TheOrdinaryUndockedStart()
        {
            // FAILS IF: the announcement becomes a standing complaint. Every ordinary
            // recording start is an undocked one; an Info line on each would be noise, and
            // the house rule is that a one-shot announces an EVENT, not a condition.
            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)Vessel.Situations.LANDED,
                activeVesselIsEva: false,
                candidates: new List<OriginPartnerCandidate>(),
                settledDockSeamsScanned: 0,
                snapshot: Snapshot(),
                isGloopsMode: false,
                vesselContext: "<test>",
                recordingVesselId: 7u,
                out RouteOriginProof proof,
                out List<uint> _);

            Assert.Null(proof);
            Assert.DoesNotContain(logLines, l => l.Contains("no depot half"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof skipped: no external coupling"));
        }

        [Fact]
        public void PrelaunchHost_TakesItsOwnSkip_EvenWithSeamsScanned()
        {
            // The PRELAUNCH short-circuit runs BEFORE the candidate walk, so a clamped pad
            // vessel must not gain the depot-typing advice: it is not a delivery origin at
            // all, and telling the player to retype something would be wrong guidance.
            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)Vessel.Situations.PRELAUNCH,
                activeVesselIsEva: false,
                candidates: new List<OriginPartnerCandidate>(),
                settledDockSeamsScanned: 2,
                snapshot: Snapshot(),
                isGloopsMode: false,
                vesselContext: "<test>",
                recordingVesselId: 7u,
                out RouteOriginProof proof,
                out List<uint> _);

            Assert.Null(proof);
            Assert.DoesNotContain(logLines, l => l.Contains("no depot half"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof skipped: active vessel PRELAUNCH"));
        }

        // ---------- the far-half lookup on a multi-port partner (fail closed) ----------

        private static RouteProofCapture.SeamNodeRecord Node(bool hasInfo, uint dockedPartUId)
        {
            return new RouteProofCapture.SeamNodeRecord(hasInfo, dockedPartUId);
        }

        [Fact]
        public void FacingSeamNode_PicksTheNodeThatNamesOurPart_NotTheFirstOne()
        {
            // THE MULTI-PORT CASE. An adapter carrying two docked ports has a node for
            // ANOTHER seam sitting earlier in the module list. Taking the first node with a
            // vesselInfo would hand the origin rule a THIRD vessel's identity, which it
            // would then happily classify as the depot.
            var nodes = new List<RouteProofCapture.SeamNodeRecord>
            {
                Node(true, 777u),   // another seam's far half
                Node(true, 4242u),  // ours
            };
            Assert.Equal(1, RouteProofCapture.SelectFacingSeamNodeIndex(nodes, 4242u));
        }

        [Fact]
        public void FacingSeamNode_NoNodeNamesUs_FailsClosed()
        {
            // FAILS IF: the fall-open comes back. -1 becomes HalfIdentityMissing and the
            // seam contributes no candidate - "no proof" is the right failure here, "a proof
            // about the wrong craft" never is.
            var nodes = new List<RouteProofCapture.SeamNodeRecord>
            {
                Node(true, 777u),
                Node(true, 888u),
            };
            Assert.Equal(-1, RouteProofCapture.SelectFacingSeamNodeIndex(nodes, 4242u));
        }

        [Fact]
        public void FacingSeamNode_NodeWithoutVesselInfoIsNotAFarHalf()
        {
            // A node with no vesselInfo has no identity to contribute even when its
            // dockedPartUId happens to name us (a stale id survives an undock).
            var nodes = new List<RouteProofCapture.SeamNodeRecord> { Node(false, 4242u) };
            Assert.Equal(-1, RouteProofCapture.SelectFacingSeamNodeIndex(nodes, 4242u));
        }

        [Fact]
        public void FacingSeamNode_ZeroFacingIdAndEmptyListNeverMatch()
        {
            var nodes = new List<RouteProofCapture.SeamNodeRecord> { Node(true, 0u) };
            Assert.Equal(-1, RouteProofCapture.SelectFacingSeamNodeIndex(nodes, 0u));
            Assert.Equal(-1, RouteProofCapture.SelectFacingSeamNodeIndex(
                new List<RouteProofCapture.SeamNodeRecord>(), 4242u));
            Assert.Equal(-1, RouteProofCapture.SelectFacingSeamNodeIndex(null, 4242u));
        }

        [Fact]
        public void Resolver_AllZeroRoots_ReadsPartnerPidZero()
        {
            var candidates = new List<OriginPartnerCandidate>
            {
                Candidate(11u, 0u, (int)Vessel.Situations.LANDED),
            };
            Assert.Equal(
                OriginProofDetection.PartnerPidZero,
                RouteProofCapture.TryResolveStartDockedOriginPartner(
                    (int)Vessel.Situations.LANDED, false, candidates, out uint originRoot));
            Assert.Equal(0u, originRoot);
        }
    }
}
