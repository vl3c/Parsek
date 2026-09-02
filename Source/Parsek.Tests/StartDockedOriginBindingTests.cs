using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE START-DOCKED ORIGIN RULE (P12), pinned headlessly end to end: capture the seam
    /// PAIR, bind the origin at the UNDOCK to the half the player did not keep flying, and
    /// validate the pickup on the transport half's own manifests.
    ///
    /// <para>WHAT REPLACED WHAT. The previous rule selected the origin at CAPTURE by vessel
    /// type (<c>Base</c> / <c>Station</c>), which required the player to have retyped their
    /// base (ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT) and could not tell a
    /// depot-side start from a transport-side one
    /// (ROUTE-ORIGIN-PROOF-SELF-ORIGIN-ON-A-DEPOT-SIDE-START). Both are gone: no code decides
    /// anything on a <c>VesselType</c> any more. A route candidate is defined by TRANSFERS AND
    /// DOCKS - the transport takes cargo at one docked partner and delivers it at another - so
    /// the capture defers and the undock decides. Derivation:
    /// docs/dev/research/origin-proof-partner-identity-memo.md.</para>
    /// </summary>
    [Collection("Sequential")]
    public class StartDockedOriginBindingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StartDockedOriginBindingTests()
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

        // ==============================================================
        // 1. PAIR ADMISSION - what capture accepts, over every shape
        // ==============================================================

        [Fact]
        public void NoVesselTypeAuthorityRemains_TheDepotPredicateIsGone()
        {
            // THE DELETION, pinned as a fact rather than as an absence. IsDepotVesselType was
            // the ONLY place a VesselType decided anything; if it comes back, this cell is
            // what has to be edited to let it.
            System.Reflection.MethodInfo revived = typeof(RouteProofCapture).GetMethod(
                "IsDepotVesselType",
                System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
            Assert.Null(revived);
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
            // analysis rejects the candidate"). This is the ONE surviving type check, and it
            // is a can-this-thing-own-cargo-at-all guard, not a role assignment.
            Assert.Equal(expected, RouteProofCapture.IsValidCargoOwnerVesselType((int)type));
        }

        [Fact]
        public void IsValidCargoOwnerVesselType_UnknownTypeSentinel_Rejects()
        {
            Assert.False(RouteProofCapture.IsValidCargoOwnerVesselType(-1));
        }

        [Theory]
        [InlineData(VesselType.Base, VesselType.Ship)]        // the canonical supply shape
        [InlineData(VesselType.Ship, VesselType.Base)]        // ... mirrored across the seam
        [InlineData(VesselType.Ship, VesselType.Probe)]       // two ordinary craft: WAS NoDepotHalf
        [InlineData(VesselType.Base, VesselType.Station)]     // two depots:        WAS BothHalvesDepot
        [InlineData(VesselType.Rover, VesselType.Rover)]      // the rover-supplies-rover shape
        [InlineData(VesselType.Lander, VesselType.Probe)]
        public void PairAdmission_AdmitsEveryPairOfCargoOwners_WhateverTheTypes(
            VesselType nearType, VesselType farType)
        {
            // THE CELL THAT ENCODES THE RULING. Every one of these pairs is admitted now.
            // The middle two are the ones that used to capture NOTHING: an untyped landed base
            // (which is every stock-built base, since no stock part declares Base or Station)
            // read NoDepotHalf, and a station docked to a base read BothHalvesDepot. Bases are
            // ordinary vessels; the undock decides which half the run left behind.
            Assert.Equal(
                DockSeamPairAdmission.Admitted,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(nearType, 100u), Half(farType, 200u)));
        }

        [Theory]
        [InlineData(VesselType.Debris)]
        [InlineData(VesselType.EVA)]
        [InlineData(VesselType.Flag)]
        [InlineData(VesselType.SpaceObject)]
        public void PairAdmission_InvalidCargoOwnerOnEitherSide_Rejects(VesselType bad)
        {
            // Checked in BOTH directions on purpose: an asymmetric guard would admit exactly
            // the shape it was written to reject, just from the other node.
            Assert.Equal(
                DockSeamPairAdmission.InvalidCargoOwner,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(VesselType.Ship, 100u), Half(bad, 200u)));
            Assert.Equal(
                DockSeamPairAdmission.InvalidCargoOwner,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(bad, 200u), Half(VesselType.Ship, 100u)));
        }

        [Fact]
        public void PairAdmission_MissingHalfInfoOrZeroRoot_ReadsHalfIdentityMissing()
        {
            var absent = new DockSeamHalfIdentity(false, null, -1, 0u);
            var zeroRoot = new DockSeamHalfIdentity(true, "half", (int)VesselType.Ship, 0u);
            Assert.Equal(
                DockSeamPairAdmission.HalfIdentityMissing,
                RouteProofCapture.ClassifyStartDockedSeamPair(Half(VesselType.Ship, 100u), absent));
            Assert.Equal(
                DockSeamPairAdmission.HalfIdentityMissing,
                RouteProofCapture.ClassifyStartDockedSeamPair(absent, Half(VesselType.Ship, 100u)));
            Assert.Equal(
                DockSeamPairAdmission.HalfIdentityMissing,
                RouteProofCapture.ClassifyStartDockedSeamPair(zeroRoot, Half(VesselType.Ship, 200u)));
            Assert.Equal(
                DockSeamPairAdmission.HalfIdentityMissing,
                RouteProofCapture.ClassifyStartDockedSeamPair(Half(VesselType.Ship, 200u), zeroRoot));
        }

        [Fact]
        public void PairAdmission_BothHalvesNameTheSameCraft_ReadsHalfIdentityMissing()
        {
            // A seam that names ONE identity twice can never be split into an origin and a
            // transport at any later moment, so it is not a pair at all.
            Assert.Equal(
                DockSeamPairAdmission.HalfIdentityMissing,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(VesselType.Ship, 100u), Half(VesselType.Base, 100u)));
        }

        [Fact]
        public void PairAdmission_InvalidCargoOwnerBeatsEverythingElse()
        {
            // A Ship docked to a Flag is rejected as an invalid cargo owner, and the order
            // matters because the reject set is the design doc's guard.
            Assert.Equal(
                DockSeamPairAdmission.InvalidCargoOwner,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(VesselType.Ship, 100u), Half(VesselType.Flag, 200u)));
        }

        // ==============================================================
        // 2. THE SEAM SPLIT - how a half's part set is known while both halves are one vessel
        // ==============================================================

        private static RouteProofCapture.SeamPartRecord Part(uint flightId, uint pid, int parentIndex)
        {
            return new RouteProofCapture.SeamPartRecord(flightId, pid, parentIndex);
        }

        /// <summary>
        /// A merged five-part stack: transport root(0) - tank(1) - port(2) ]|[ port(3) -
        /// depot root(4). The seam is the 2/3 edge; index 3's parent is 2.
        /// </summary>
        private static List<RouteProofCapture.SeamPartRecord> MergedStack()
        {
            return new List<RouteProofCapture.SeamPartRecord>
            {
                Part(10u, 110u, -1), // transport root
                Part(11u, 111u, 0),  // transport tank
                Part(12u, 112u, 1),  // transport port  (near / seam side)
                Part(13u, 113u, 2),  // depot port      (far / partner side)
                Part(14u, 114u, 3),  // depot root
            };
        }

        [Fact]
        public void SeamSplit_CutsTheSeamEdgeIntoExactlyTwoHalves()
        {
            Assert.True(RouteProofCapture.TrySplitPartsAcrossSeam(
                MergedStack(), 12u, 13u, out List<uint> near, out List<uint> far));
            Assert.Equal(new List<uint> { 110u, 111u, 112u }, near);
            Assert.Equal(new List<uint> { 113u, 114u }, far);
        }

        [Fact]
        public void SeamSplit_IsSymmetricAcrossTheSeam()
        {
            // Naming the halves in the other order swaps the outputs and nothing else - the
            // mirror direction of the same cut.
            Assert.True(RouteProofCapture.TrySplitPartsAcrossSeam(
                MergedStack(), 13u, 12u, out List<uint> near, out List<uint> far));
            Assert.Equal(new List<uint> { 113u, 114u }, near);
            Assert.Equal(new List<uint> { 110u, 111u, 112u }, far);
        }

        [Fact]
        public void SeamSplit_NonAdjacentParts_FailClosed()
        {
            // The transport ROOT and the depot ROOT are not parent and child, so the "seam"
            // is not an edge and the two components are not the halves. A wrong part set
            // would silently mis-scope every manifest downstream.
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(
                MergedStack(), 10u, 14u, out List<uint> near, out List<uint> far));
            Assert.Null(near);
            Assert.Null(far);
        }

        [Fact]
        public void SeamSplit_MissingOrDegenerateInputs_FailClosed()
        {
            List<RouteProofCapture.SeamPartRecord> parts = MergedStack();
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(parts, 12u, 999u, out _, out _));
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(parts, 0u, 13u, out _, out _));
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(parts, 12u, 12u, out _, out _));
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(null, 12u, 13u, out _, out _));
        }

        [Fact]
        public void SeamSplit_DisconnectedPart_FailsClosedRatherThanLosingIt()
        {
            // A part list that is not ONE tree (a stray part with no parent path to either
            // side) cannot be two components, and a silent drop would under-scope a manifest.
            List<RouteProofCapture.SeamPartRecord> parts = MergedStack();
            parts.Add(Part(15u, 115u, -1)); // second root
            Assert.False(RouteProofCapture.TrySplitPartsAcrossSeam(parts, 12u, 13u, out _, out _));
        }

        // ==============================================================
        // 3. THE PAIR RESOLVER
        // ==============================================================

        private static DockSeamPairCandidate Candidate(
            uint seamPart, uint nearRoot, uint farRoot, int situation)
        {
            return new DockSeamPairCandidate(
                seamPart,
                Half(VesselType.Ship, nearRoot, "transport"),
                Half(VesselType.Base, farRoot, "depot"),
                new List<uint> { 110u, 111u, 112u },
                new List<uint> { 113u, 114u },
                situation, "Kerbin", 0.1, -74.7, 68.9);
        }

        [Fact]
        public void Resolver_TwoSeamsBetweenTheSameTwoCraft_CollapseToOnePair()
        {
            // A two-port dock produces two seams naming the SAME pair, so the resolver must
            // read Captured, not PartnerAmbiguous. The key is UNORDERED, so the second seam
            // naming the halves the other way round still collapses.
            var candidates = new List<DockSeamPairCandidate>
            {
                Candidate(11u, 200u, 555u, (int)Vessel.Situations.LANDED),
                Candidate(12u, 555u, 200u, (int)Vessel.Situations.LANDED),
            };
            OriginProofDetection outcome = RouteProofCapture.TryResolveStartDockedSeamPair(
                (int)Vessel.Situations.LANDED, false, candidates, out int chosen);
            Assert.Equal(OriginProofDetection.Captured, outcome);
            Assert.Equal(0, chosen);
        }

        [Fact]
        public void Resolver_TwoDifferentPartners_ReadsAmbiguous()
        {
            var candidates = new List<DockSeamPairCandidate>
            {
                Candidate(11u, 200u, 555u, (int)Vessel.Situations.LANDED),
                Candidate(12u, 200u, 777u, (int)Vessel.Situations.LANDED),
            };
            Assert.Equal(
                OriginProofDetection.PartnerAmbiguous,
                RouteProofCapture.TryResolveStartDockedSeamPair(
                    (int)Vessel.Situations.LANDED, false, candidates, out _));
        }

        [Fact]
        public void Resolver_AllZeroRoots_ReadsPartnerPidZero()
        {
            var candidates = new List<DockSeamPairCandidate>
            {
                Candidate(11u, 0u, 0u, (int)Vessel.Situations.LANDED),
            };
            Assert.Equal(
                OriginProofDetection.PartnerPidZero,
                RouteProofCapture.TryResolveStartDockedSeamPair(
                    (int)Vessel.Situations.LANDED, false, candidates, out int chosen));
            Assert.Equal(-1, chosen);
        }

        [Fact]
        public void Resolver_EvaAndPrelaunchShortCircuitBeforeTheCandidateWalk()
        {
            var candidates = new List<DockSeamPairCandidate>
            {
                Candidate(11u, 200u, 555u, (int)Vessel.Situations.LANDED),
            };
            Assert.Equal(
                OriginProofDetection.NoExternalCoupling,
                RouteProofCapture.TryResolveStartDockedSeamPair(
                    (int)Vessel.Situations.LANDED, true, candidates, out _));
            Assert.Equal(
                OriginProofDetection.ActiveVesselPrelaunch,
                RouteProofCapture.TryResolveStartDockedSeamPair(
                    (int)Vessel.Situations.PRELAUNCH, false, candidates, out _));
        }

        // ==============================================================
        // 4. THE PRODUCER: a captured pair names NO origin
        // ==============================================================

        private static ConfigNode Snapshot(params uint[] partPids)
        {
            var vessel = new ConfigNode("VESSEL");
            uint[] pids = partPids.Length > 0 ? partPids : new uint[] { 100u };
            foreach (uint pid in pids)
            {
                var part = new ConfigNode("PART");
                part.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                part.AddValue("name", "fuelTank");
                vessel.AddNode(part);
            }
            return vessel;
        }

        [Fact]
        public void Producer_CapturedPair_LeavesTheOriginSlotsEmptyAndAnnouncesThePair()
        {
            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)Vessel.Situations.LANDED,
                activeVesselIsEva: false,
                candidates: new List<DockSeamPairCandidate>
                {
                    Candidate(11u, 200u, 555u, (int)Vessel.Situations.LANDED),
                },
                settledDockSeamsScanned: 1,
                snapshot: Snapshot(110u, 111u, 112u, 113u, 114u),
                isGloopsMode: false,
                vesselContext: "<test>",
                recordingVesselId: 7u,
                out RouteOriginProof proof,
                out List<uint> mergedPids);

            Assert.NotNull(proof);
            // NO ORIGIN AT CAPTURE. This is the whole shape change: the identity slots stay
            // empty until an undock separates the pair.
            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(0u, proof.StartDockedOriginVesselPid);
            Assert.Equal(StartDockedOriginBindState.PairPendingBinding, proof.StartDockedOriginBindState);
            Assert.False(proof.StartDockedOriginPickupValidated);
            // BOTH halves are recorded, with their own part sets and manifests.
            Assert.NotNull(proof.StartDockedPair);
            Assert.Equal(200u, proof.StartDockedPair.HalfA.RootPartUId);
            Assert.Equal(555u, proof.StartDockedPair.HalfB.RootPartUId);
            Assert.Equal(3, proof.StartDockedPair.HalfA.PartPersistentIds.Count);
            Assert.Equal(2, proof.StartDockedPair.HalfB.PartPersistentIds.Count);
            // The M1 descriptor is the merged pair's, which is both halves' while docked.
            Assert.Equal("Kerbin", proof.StartDockedOriginBodyName);
            Assert.True(proof.StartDockedOriginIsSurface);
            Assert.Equal(5, mergedPids.Count);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof pair captured:")
                && l.Contains("halfARoot=200")
                && l.Contains("halfBRoot=555")
                && l.Contains("bindState=PairPendingBinding"));
        }

        [Fact]
        public void Producer_CapturedPairIsNotAnOriginForRouteAnalysis()
        {
            RouteProofCapture.BuildStartRouteOriginProof(
                (int)Vessel.Situations.LANDED, false,
                new List<DockSeamPairCandidate>
                {
                    Candidate(11u, 200u, 555u, (int)Vessel.Situations.LANDED),
                },
                1, Snapshot(110u, 111u, 112u, 113u, 114u), false, "<test>", 7u,
                out RouteOriginProof proof, out _);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void Producer_UnusableSeamPair_AnnouncesAtInfo_KeyedOnSCANNEDSeamsNotAcceptedCandidates()
        {
            // A seam rejected by the admission rule adds NO candidate - the producer loop
            // skips it - so the accepted list is EMPTY on exactly the case the message is
            // written for. Keying the announcement on candidates.Count would make it
            // unreachable: it could only ever fire when a pair was capturable.
            RouteProofCapture.BuildStartRouteOriginProof(
                activeVesselSituation: (int)Vessel.Situations.LANDED,
                activeVesselIsEva: false,
                candidates: new List<DockSeamPairCandidate>(),
                settledDockSeamsScanned: 2,
                snapshot: Snapshot(),
                isGloopsMode: false,
                vesselContext: "<test>",
                recordingVesselId: 7u,
                out RouteOriginProof proof,
                out List<uint> _);

            Assert.Null(proof);
            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof skipped: unusable seam pair")
                && l.Contains("seams=2")
                && l.Contains("candidates=0"));
        }

        [Fact]
        public void Producer_NoSeamsAtAll_StaysQuiet_TheOrdinaryUndockedStart()
        {
            // FAILS IF: the announcement becomes a standing complaint. Every ordinary
            // recording start is an undocked one; an Info line on each would be noise, and
            // the house rule is that a one-shot announces an EVENT, not a condition.
            RouteProofCapture.BuildStartRouteOriginProof(
                (int)Vessel.Situations.LANDED, false,
                new List<DockSeamPairCandidate>(), 0, Snapshot(), false, "<test>", 7u,
                out RouteOriginProof proof, out _);

            Assert.Null(proof);
            Assert.DoesNotContain(logLines, l => l.Contains("unusable seam pair"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof skipped: no external coupling"));
        }

        [Fact]
        public void Producer_PrelaunchHost_TakesItsOwnSkip_EvenWithSeamsScanned()
        {
            RouteProofCapture.BuildStartRouteOriginProof(
                (int)Vessel.Situations.PRELAUNCH, false,
                new List<DockSeamPairCandidate>(), 2, Snapshot(), false, "<test>", 7u,
                out RouteOriginProof proof, out _);

            Assert.Null(proof);
            Assert.DoesNotContain(logLines, l => l.Contains("unusable seam pair"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof skipped: active vessel PRELAUNCH"));
        }

        // ==============================================================
        // 5. THE BINDING RULE
        // ==============================================================

        private static readonly List<uint> HalfAParts = new List<uint> { 110u, 111u, 112u };
        private static readonly List<uint> HalfBParts = new List<uint> { 113u, 114u };

        [Fact]
        public void Binding_ActiveIsHalfA_OriginIsHalfB()
        {
            Assert.Equal(
                OriginUndockBinding.BoundToHalfB,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, HalfAParts, HalfBParts));
        }

        [Fact]
        public void Binding_ActiveIsHalfB_OriginIsHalfA()
        {
            // THE MIRROR DIRECTION. The rig that couples the depot INTO the transport and the
            // one that docks the transport into the depot land on opposite sides of this, and
            // the rule must be symmetric because nothing about a half is privileged.
            Assert.Equal(
                OriginUndockBinding.BoundToHalfA,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, HalfBParts, HalfAParts));
        }

        [Fact]
        public void Binding_ActiveSideOwnsPartsOfBothHalves_RefusesToBind()
        {
            // This split did not separate the PAIR (something else came off the merged
            // stack), so neither half is "the one that was left behind".
            var mixed = new List<uint> { 110u, 113u };
            Assert.Equal(
                OriginUndockBinding.BothHalvesActive,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, mixed, new List<uint> { 999u }));
        }

        [Fact]
        public void Binding_UnrelatedSeamSplit_RefusesToBind()
        {
            // A decoupler inside ONE half fired: both sides of this split are pieces of half
            // A, so the pair is still docked and no origin was left behind.
            Assert.Equal(
                OriginUndockBinding.UnrelatedSeam,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts,
                    new List<uint> { 110u, 111u },
                    new List<uint> { 112u }));
        }

        [Fact]
        public void Binding_ActiveSideMatchesNeitherHalf_RefusesToBind()
        {
            Assert.Equal(
                OriginUndockBinding.ActiveHalfUnresolved,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, new List<uint> { 900u }, HalfBParts));
            Assert.Equal(
                OriginUndockBinding.ActiveHalfUnresolved,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, null, HalfBParts));
        }

        [Fact]
        public void Binding_NoCapturedPartSets_ReadsNoPairPending()
        {
            // A seam whose split failed closed carries no part sets, so there is nothing to
            // match the split against.
            Assert.Equal(
                OriginUndockBinding.NoPairPending,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    null, HalfBParts, HalfAParts, HalfBParts));
            Assert.Equal(
                OriginUndockBinding.NoPairPending,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, new List<uint>(), HalfAParts, HalfBParts));
        }

        [Fact]
        public void Binding_PartSetDrift_StillBinds_OverlapNotEquality()
        {
            // EVA construction added a part to the transport and took one off during the
            // docked span. An equality test would refuse to bind an otherwise clean run.
            var driftedActive = new List<uint> { 110u, 111u, 9001u };
            Assert.Equal(
                OriginUndockBinding.BoundToHalfB,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    HalfAParts, HalfBParts, driftedActive, HalfBParts));
        }

        // ==============================================================
        // 6. THE PICKUP RULE
        // ==============================================================

        private static Dictionary<string, ResourceAmount> Manifest(params object[] pairs)
        {
            var dict = new Dictionary<string, ResourceAmount>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                dict[(string)pairs[i]] = new ResourceAmount
                {
                    amount = System.Convert.ToDouble(pairs[i + 1]),
                    maxAmount = 1000.0
                };
            }
            return dict;
        }

        [Fact]
        public void Pickup_CargoWentUp_ReadsGain()
        {
            Assert.Equal(
                OriginPickupKind.Gain,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 10.0),
                    Manifest("LiquidFuel", 90.0)));
        }

        [Fact]
        public void Pickup_NewResourceAppeared_ReadsGain()
        {
            Assert.Equal(
                OriginPickupKind.Gain,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 10.0),
                    Manifest("LiquidFuel", 10.0, "Ore", 40.0)));
        }

        [Fact]
        public void Pickup_Unchanged_ReadsCarried()
        {
            // The workflow sentence under design 19.2.2 says starting docked at the origin IS
            // a Loaded provenance: while docked the pair is ONE Vessel with stock crossfeed,
            // so cargo aboard the transport at the split was part of the merged stack the
            // origin is half of. Recorded as the WEAKER witness so a reader can tell the two
            // apart in the log and in the persisted proof.
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 40.0),
                    Manifest("LiquidFuel", 40.0)));
        }

        [Fact]
        public void Pickup_CargoWentDown_ReadsCarried_NotGain()
        {
            // The transport burned fuel while docked. Still carried, never a gain.
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 90.0),
                    Manifest("LiquidFuel", 40.0)));
        }

        [Fact]
        public void Pickup_TransportLeavesEmpty_ReadsNone()
        {
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 40.0),
                    Manifest("LiquidFuel", 0.0)));
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 40.0),
                    Manifest()));
        }

        [Fact]
        public void Pickup_ElectricChargeAndIntakeAirAreNotCargo()
        {
            // Environmental noise: a docked transport recharges its batteries from the depot
            // and its IntakeAir reading drifts between snapshots, so either would forge a
            // gain on an otherwise empty run.
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("ElectricCharge", 10.0),
                    Manifest("ElectricCharge", 200.0, "IntakeAir", 5.0)));
        }

        [Fact]
        public void Pickup_MissingStartManifest_CannotReadAGain_FallsToCarried()
        {
            // No baseline means no delta. The run is still admitted on the carried branch,
            // which is the honest reading, and the log records the start count as 0.
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(null, Manifest("LiquidFuel", 40.0)));
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(null, Manifest()));
        }

        [Fact]
        public void Pickup_MissingUndockManifest_IsUnevaluable()
        {
            Assert.Equal(
                OriginPickupKind.NoUndockManifest,
                RouteProofCapture.ClassifyOriginPickup(Manifest("LiquidFuel", 40.0), null));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.NoUndockManifest));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.None));
            Assert.True(RouteProofCapture.IsPickupValidated(OriginPickupKind.Gain));
            Assert.True(RouteProofCapture.IsPickupValidated(OriginPickupKind.Carried));
        }

        // ==============================================================
        // 7. THE GUID GATE on the origin pid stamp
        // ==============================================================

        private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public void PidStamp_DifferentPid_Stamps()
        {
            Assert.Equal(
                OriginPidStampDecision.Stamped,
                RouteProofCapture.DecideOriginPidStamp(500u, GuidB, 400u, GuidA));
        }

        [Fact]
        public void PidStamp_SamePidAndGuidsDoNotDiffer_RefusesTheSelfOrigin()
        {
            // The candidate origin IS the recorded launch. Stamping it would make the run its
            // own supply origin - the exact shape
            // ROUTE-ORIGIN-PROOF-SELF-ORIGIN-ON-A-DEPOT-SIDE-START names.
            Assert.Equal(
                OriginPidStampDecision.RefusedSameLaunch,
                RouteProofCapture.DecideOriginPidStamp(400u, GuidA, 400u, GuidA));
            // ... and an UNKNOWN guid on either side is not conclusive, so it refuses too:
            // the pid alone is craft-baked and proves nothing, and the safe direction here is
            // to keep the pid slot empty (the root part id is still the identity).
            Assert.Equal(
                OriginPidStampDecision.RefusedSameLaunch,
                RouteProofCapture.DecideOriginPidStamp(400u, null, 400u, GuidA));
        }

        [Fact]
        public void PidStamp_SamePidButGuidsConclusivelyDiffer_Stamps()
        {
            // A craft-baked persistentId is reused verbatim on every launch of a craft file,
            // so an equal pid alone proves nothing; a known guid difference clears it.
            Assert.Equal(
                OriginPidStampDecision.Stamped,
                RouteProofCapture.DecideOriginPidStamp(400u, GuidB, 400u, GuidA));
        }

        [Fact]
        public void PidStamp_UnknownGuidOnADifferentPid_DegradesToPidOnly()
        {
            Assert.Equal(
                OriginPidStampDecision.StampedGuidUnknown,
                RouteProofCapture.DecideOriginPidStamp(500u, null, 400u, GuidA));
            Assert.Equal(
                OriginPidStampDecision.StampedGuidUnknown,
                RouteProofCapture.DecideOriginPidStamp(500u, GuidB, 400u, null));
        }

        [Fact]
        public void PidStamp_NoLiveVessel_KeepsThePidSlotEmpty()
        {
            Assert.Equal(
                OriginPidStampDecision.NoLiveVessel,
                RouteProofCapture.DecideOriginPidStamp(0u, GuidB, 400u, GuidA));
        }

        // ==============================================================
        // 8. THE WHOLE BIND, end to end on a DTO
        // ==============================================================

        private static RouteOriginProof PendingProof()
        {
            return new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = 200u,
                        VesselName = "transport",
                        VesselType = (int)VesselType.Ship,
                        PartPersistentIds = new List<uint>(HalfAParts),
                        StartResources = Manifest("LiquidFuel", 10.0),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = 555u,
                        VesselName = "depot",
                        VesselType = (int)VesselType.Base,
                        PartPersistentIds = new List<uint>(HalfBParts),
                        StartResources = Manifest("LiquidFuel", 800.0),
                    },
                },
                // The merged-pair baseline the capture wrote; the bind must REPLACE it.
                StartTransportResources = Manifest("LiquidFuel", 810.0),
            };
        }

        private static ConfigNode SnapshotWithResource(uint partPid, string resource, double amount)
        {
            var vessel = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("persistentId", partPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            part.AddValue("name", "fuelTank");
            var res = part.AddNode("RESOURCE");
            res.AddValue("name", resource);
            res.AddValue("amount", amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            res.AddValue("maxAmount", "1000");
            vessel.AddNode(part);
            return vessel;
        }

        [Fact]
        public void Bind_TransportKeptFlying_BindsTheDepotAndRescopesTheManifests()
        {
            RouteOriginProof proof = PendingProof();
            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts,
                activeSnapshot, activeSnapshot,
                originLiveVesselPid: 500u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 400u, recordedVesselGuid: GuidA,
                undockUT: 1234.5, recordingContext: "rec-1");

            Assert.True(bound);
            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);
            Assert.Equal(555u, proof.StartDockedOriginRootPartUId);
            Assert.Equal("depot", proof.StartDockedOriginVesselName);
            Assert.Equal(200u, proof.StartDockedTransportRootPartUId);
            Assert.Equal(500u, proof.StartDockedOriginVesselPid);
            // TRANSPORT-SCOPED: the merged 810.0 baseline is replaced by the transport half's
            // own 10.0, which closes ROUTE-ORIGIN-PROOF-TRANSPORT-MANIFESTS-INCLUDE-THE-DEPOT.
            Assert.Equal(10.0, proof.StartTransportResources["LiquidFuel"].amount, 6);
            // 10 -> 90 across the docked span is the strong witness.
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("originRoot=555")
                && l.Contains("originHalf=B")
                && l.Contains("guidDecision=Stamped")
                && l.Contains("pickup=Gain")
                && l.Contains("pickupValidated=1"));

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void Bind_DepotKeptFlying_BindsTheOtherHalf()
        {
            // The mirror direction driven all the way through the orchestrator, not just
            // through the classifier.
            RouteOriginProof proof = PendingProof();
            ConfigNode activeSnapshot = SnapshotWithResource(113u, "LiquidFuel", 800.0);

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfBParts, HalfAParts,
                activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1");

            Assert.True(bound);
            Assert.Equal(200u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(555u, proof.StartDockedTransportRootPartUId);
            Assert.Equal(800.0, proof.StartTransportResources["LiquidFuel"].amount, 6);
        }

        [Fact]
        public void Bind_NoPickup_BindsButIsNotAnOrigin()
        {
            RouteOriginProof proof = PendingProof();
            ConfigNode emptyTransport = SnapshotWithResource(110u, "LiquidFuel", 0.0);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, emptyTransport, emptyTransport,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1"));

            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);
            Assert.Equal(555u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.None, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof unbound:")
                && l.Contains("reason=pickup-None"));
        }

        [Fact]
        public void Bind_SplitDoesNotSeparateThePair_LeavesTheProofPending()
        {
            RouteOriginProof proof = PendingProof();
            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof,
                new List<uint> { 110u, 113u }, new List<uint> { 999u },
                SnapshotWithResource(110u, "LiquidFuel", 90.0), null,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1"));

            Assert.Equal(StartDockedOriginBindState.PairPendingBinding, proof.StartDockedOriginBindState);
            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof unbound:")
                && l.Contains("reason=BothHalvesActive"));
        }

        [Fact]
        public void Bind_RunsOnce_ASecondUndockCannotRebindTheOrigin()
        {
            RouteOriginProof proof = PendingProof();
            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1"));
            // The transport docks somewhere else later and undocks again: the ORIGIN is a
            // birth fact of the run and must not move to the delivery endpoint.
            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfBParts, HalfAParts, activeSnapshot, activeSnapshot,
                900u, GuidB, 400u, GuidA, 5678.0, "rec-1"));
            Assert.Equal(555u, proof.StartDockedOriginRootPartUId);
        }

        [Fact]
        public void Bind_SelfOriginPid_KeepsThePidSlotEmptyButStillBindsTheRoot()
        {
            RouteOriginProof proof = PendingProof();
            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, activeSnapshot, activeSnapshot,
                originLiveVesselPid: 400u, originLiveVesselGuid: GuidA,
                recordedVesselPid: 400u, recordedVesselGuid: GuidA,
                undockUT: 1234.5, recordingContext: "rec-1"));

            Assert.Equal(0u, proof.StartDockedOriginVesselPid);
            Assert.Equal(555u, proof.StartDockedOriginRootPartUId);
            Assert.Contains(logLines, l => l.Contains("guidDecision=RefusedSameLaunch"));
        }

        // ==============================================================
        // 8b. THE ROVER-RELAY SAVE, replayed headlessly
        //
        // MEASURED, not invented: a sibling session's rover-relay flight
        // (KSP.log 2026-09-02 20:29, DLL from main 96ac15dfb, three same-craft-file rovers
        // A / B / C landed near KSC) is the live corroboration for this whole package.
        //
        //  (a) BOTH docks logged `RouteOriginProof skipped: no depot half recId=1461186781
        //      ... seams=2 candidates=0` with nearType=5 farType=5 - two Rovers. The
        //      depot-typed discriminator fails CLOSED on the rover-to-rover relay, which is
        //      the shape the roadmap is aimed at. That reading is what P12 deletes.
        //  (b) At UT 276.00 the half NOT kept is rover B and the transport-half delta is
        //      LiquidFuel +200 (a pickup) -> B binds as the origin. At UT 402.50 the half
        //      not kept is rover A and the transport delta is -126.8 (a delivery) -> A must
        //      NOT become an origin.
        //
        // These cells replay exactly those two undocks against the pure rules. Live pids
        // and window ids from that session are quoted in the comments, not asserted: the
        // decisions here take part sets and manifests, which is the point.
        // ==============================================================

        // Transport C (live vessel pid 1461186781, tree 87fba47a981e4c86a598fe855a6e8113).
        private static readonly List<uint> RoverCParts = new List<uint> { 5100u, 5101u };
        private static readonly List<uint> RoverBParts = new List<uint> { 5200u, 5201u };
        private static readonly List<uint> RoverAParts = new List<uint> { 5300u, 5301u };

        private static RouteOriginProof RoverRelayPendingProof(
            List<uint> partnerParts, uint partnerRoot, string partnerName, double transportStartLf)
        {
            return new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    // BOTH halves are Rover-typed (vesselType 5), which is exactly the pair
                    // the deleted rule read as NoDepotHalf.
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = 5100u,
                        VesselName = "Rover C",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RoverCParts),
                        StartResources = Manifest("LiquidFuel", transportStartLf),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = partnerRoot,
                        VesselName = partnerName,
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(partnerParts),
                        StartResources = Manifest("LiquidFuel", 400.0),
                    },
                },
            };
        }

        [Fact]
        public void RoverRelay_BothHalvesRoverTyped_ArePairAdmitted()
        {
            // (a) The measured skip line's own inputs: nearType=5 farType=5. The old rule
            // read NoDepotHalf and captured nothing on BOTH docks of that flight.
            Assert.Equal(
                DockSeamPairAdmission.Admitted,
                RouteProofCapture.ClassifyStartDockedSeamPair(
                    Half(VesselType.Rover, 5100u, "Rover C"),
                    Half(VesselType.Rover, 5200u, "Rover B")));
        }

        [Fact]
        public void RoverRelay_FirstUndockAtUt276_PickupOf200_BindsRoverBAsOrigin()
        {
            // Window dock-218.22000000003783-target-2123618197. Transport C keeps flying,
            // rover B is left behind, and C's own half gained LiquidFuel +200 across the
            // docked span: a Gain, the strong witness.
            RouteOriginProof proof = RoverRelayPendingProof(RoverBParts, 5200u, "Rover B", 0.0);
            ConfigNode transportAtUndock = SnapshotWithResource(5100u, "LiquidFuel", 200.0);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RoverCParts, RoverBParts, transportAtUndock, transportAtUndock,
                originLiveVesselPid: 2123618197u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 1461186781u, recordedVesselGuid: GuidA,
                undockUT: 276.00, recordingContext: "rover-relay"));

            Assert.Equal(5200u, proof.StartDockedOriginRootPartUId);
            Assert.Equal("Rover B", proof.StartDockedOriginVesselName);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void RoverRelay_SecondUndockAtUt402_DeliveryOf126_8_DoesNotMakeRoverAAnOrigin()
        {
            // Window dock-340.11999999998062-target-831319732. Same transport, the OTHER
            // partner, and the delta is -126.8: this is the DELIVERY leg. Driven here as a
            // standalone pending pair - i.e. the worst case, where nothing earlier bound -
            // so the refusal comes from the transfer rule alone and not from write-once.
            RouteOriginProof proof = RoverRelayPendingProof(RoverAParts, 5300u, "Rover A", 200.0);
            ConfigNode transportAtUndock = SnapshotWithResource(5100u, "LiquidFuel", 73.2);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RoverCParts, RoverAParts, transportAtUndock, transportAtUndock,
                831319732u, GuidB, 1461186781u, GuidA, 402.50, "rover-relay"));

            // The half IS bound (the split really did leave rover A behind), but a leg that
            // only LOST cargo at that seam did not pick anything up there, so it is not a
            // supply origin. Carried, not Gain - and route analysis still refuses it below
            // only because the identity would be the delivery endpoint, which is what the
            // write-once cell that follows actually prevents.
            Assert.Equal(5300u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
        }

        [Fact]
        public void RoverRelay_TwoUndocksInSequence_TheFirstBindsAndTheSecondCannotOverwriteIt()
        {
            // THE WRITE-ONCE PROPERTY, on the real two-undock sequence. The origin is a BIRTH
            // fact of the run: rover B supplied the cargo at UT 276.00, and the delivery
            // undock at UT 402.50 must not move the origin onto rover A - which is what
            // would turn the delivery endpoint into the thing the route debits.
            RouteOriginProof proof = RoverRelayPendingProof(RoverBParts, 5200u, "Rover B", 0.0);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RoverCParts, RoverBParts,
                SnapshotWithResource(5100u, "LiquidFuel", 200.0), null,
                2123618197u, GuidB, 1461186781u, GuidA, 276.00, "rover-relay"));
            Assert.Equal(5200u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);

            // The second undock, with rover A's parts as the background side.
            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RoverCParts, RoverAParts,
                SnapshotWithResource(5100u, "LiquidFuel", 73.2), null,
                831319732u, GuidB, 1461186781u, GuidA, 402.50, "rover-relay"));

            Assert.Equal(5200u, proof.StartDockedOriginRootPartUId);
            Assert.Equal("Rover B", proof.StartDockedOriginVesselName);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
        }

        // ==============================================================
        // 9. STOPPED WHILE STILL DOCKED
        // ==============================================================

        [Fact]
        public void StopWhileDocked_MarksUnboundAndIsNotAnOrigin()
        {
            RouteOriginProof proof = PendingProof();
            Assert.True(RouteProofCapture.MarkStartDockedOriginUnboundAtStop(proof, "rec-1"));
            Assert.Equal(StartDockedOriginBindState.UnboundAtStop, proof.StartDockedOriginBindState);
            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof unbound:")
                && l.Contains("reason=stopped-while-docked"));
        }

        [Fact]
        public void StopWhileDocked_NeverUndoesABind()
        {
            RouteOriginProof proof = PendingProof();
            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1");

            Assert.False(RouteProofCapture.MarkStartDockedOriginUnboundAtStop(proof, "rec-1"));
            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);
        }

        // ==============================================================
        // 10. THE FAR-HALF LOOKUP on a multi-port partner (fail closed) - unchanged by P12
        // ==============================================================

        private static RouteProofCapture.SeamNodeRecord Node(bool hasInfo, uint dockedPartUId)
        {
            return new RouteProofCapture.SeamNodeRecord(hasInfo, dockedPartUId);
        }

        [Fact]
        public void FacingSeamNode_PicksTheNodeThatNamesOurPart_NotTheFirstOne()
        {
            // THE MULTI-PORT CASE. An adapter carrying two docked ports has a node for
            // ANOTHER seam sitting earlier in the module list. Taking the first node with a
            // vesselInfo would hand the pair rule a THIRD vessel's identity.
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
    }
}
