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
        public void Pickup_Unchanged_ReadsCarried_AndCarriedDoesNotValidate()
        {
            // Carried is OBSERVED, never validating (ruling, adversarial review F2). Design
            // 19.2.2 item 2 defines Loaded as cargo that FLOWED onto the transport, and
            // 19.2.1 makes origin causal - "the witnessed event that PUT each unit of cargo
            // on the transport". A transport that leaves a seam with the same cargo it
            // arrived with witnessed no flow there. The class is kept because it is the
            // discriminator in the log between "left with nothing" and "left with cargo it
            // already had".
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 40.0),
                    Manifest("LiquidFuel", 40.0)));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.Carried));
        }

        [Fact]
        public void Pickup_PureDeliveryUndock_IsNotAPickup()
        {
            // THE CELL THE RULING EXISTS FOR. The transport delivered 126.8 LiquidFuel at
            // this seam and leaves with residual fuel plus monopropellant aboard. Under the
            // earlier "carried validates" reading, the vessel it just DELIVERED TO would have
            // validated as the run's supply origin - the exact wrong-debit shape the design
            // doc's "deducts from recorded origin depot, NOT TRANSPORT" line forbids.
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 200.0, "MonoPropellant", 30.0),
                Manifest("LiquidFuel", 73.2, "MonoPropellant", 30.0));
            Assert.Equal(OriginPickupKind.Carried, kind);
            Assert.False(RouteProofCapture.IsPickupValidated(kind));
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
            // No baseline means no delta, so the gain branch is unevaluable and the run is
            // NOT validated - fail-closed, which is the right direction when the evidence
            // for the flow is missing rather than absent.
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
            // GAIN IS THE ONLY VALIDATING CLASS.
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.NoUndockManifest));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.None));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.Carried));
            Assert.True(RouteProofCapture.IsPickupValidated(OriginPickupKind.Gain));
        }

        // ==============================================================
        // 6b. INVENTORY IS CARGO TOO (operator ruling, 2026-09-02)
        //
        // "Docked, took fuel OR CARGO from it, undocked, went to a second vessel, docked,
        // transferred TO it, undocked." A transport that leaves a seam carrying a container
        // it did not arrive with has been loaded exactly as surely as one that leaves with
        // more fuel, so an inventory gain validates identically to a resource gain.
        // ==============================================================

        private static List<InventoryPayloadItem> Inv(params object[] pairs)
        {
            var items = new List<InventoryPayloadItem>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                items.Add(new InventoryPayloadItem
                {
                    IdentityHash = (string)pairs[i],
                    PartName = (string)pairs[i],
                    Quantity = System.Convert.ToInt32(pairs[i + 1]),
                    SlotsTaken = 1,
                });
            }
            return items;
        }

        [Fact]
        public void Pickup_RelayWindow1_ResourceAndInventoryBothRose_ReadsGain()
        {
            // THE RELAY ORACLE'S FIRST WINDOW, as measured: +200 LiquidFuel AND a
            // DeployedCentralStation plus an evaChute onto the transport.
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 0.0),
                Manifest("LiquidFuel", 200.0),
                Inv(),
                Inv("DeployedCentralStation", 1, "evaChute", 1));
            Assert.Equal(OriginPickupKind.Gain, kind);
            Assert.True(RouteProofCapture.IsPickupValidated(kind));
        }

        [Fact]
        public void Pickup_InventoryOnlyGain_WithZeroResourceDelta_ReadsGain()
        {
            // THE CELL THE RULING EXISTS FOR. Before inventory was read this run was
            // Carried - and therefore not an origin - even though a container physically
            // came aboard at that seam.
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 40.0),
                Manifest("LiquidFuel", 40.0),
                Inv("evaChute", 1),
                Inv("evaChute", 1, "DeployedCentralStation", 1));
            Assert.Equal(OriginPickupKind.Gain, kind);
            Assert.True(RouteProofCapture.IsPickupValidated(kind));
        }

        [Fact]
        public void Pickup_InventoryLostButResourceGained_ReadsGain()
        {
            // A run that DROPS a container at the depot and takes fuel is still a pickup:
            // the two kinds are independent witnesses, and either rising is a flow onto the
            // transport. Checked in this direction as well as the mirror below so the guard
            // cannot be satisfied by an accidental AND.
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 10.0),
                Manifest("LiquidFuel", 210.0),
                Inv("DeployedCentralStation", 2),
                Inv("DeployedCentralStation", 1));
            Assert.Equal(OriginPickupKind.Gain, kind);
        }

        [Fact]
        public void Pickup_ResourceLostButInventoryGained_ReadsGain()
        {
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 210.0),
                Manifest("LiquidFuel", 10.0),
                Inv("DeployedCentralStation", 1),
                Inv("DeployedCentralStation", 2));
            Assert.Equal(OriginPickupKind.Gain, kind);
        }

        [Fact]
        public void Pickup_BothKindsWentDown_IsNotValidated()
        {
            // The delivery seam, now with cargo in it: fuel down AND a container handed over.
            // Residual anything must not validate.
            OriginPickupKind kind = RouteProofCapture.ClassifyOriginPickup(
                Manifest("LiquidFuel", 200.0),
                Manifest("LiquidFuel", 73.2),
                Inv("DeployedCentralStation", 2),
                Inv("DeployedCentralStation", 1));
            Assert.Equal(OriginPickupKind.Carried, kind);
            Assert.False(RouteProofCapture.IsPickupValidated(kind));
        }

        [Fact]
        public void Pickup_InventoryOnlyTransport_LeavesEmpty_ReadsNone()
        {
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(
                    null, Manifest(), Inv("evaChute", 1), Inv()));
        }

        [Fact]
        public void Pickup_NullStartInventory_CannotReadAnInventoryGain()
        {
            // F9, THE FAIL-OPEN THIS CELL EXISTS FOR. SumInventoryQuantities(null) is an
            // EMPTY dict, not a missing one, so a walk without an explicit null-start guard
            // compares every undock item against a fabricated before=0 and calls it a Gain -
            // validating an origin on a run whose baseline was never captured. It mirrors the
            // resource branch's own null-start guard exactly.
            //
            // REACHABLE, not theoretical: BuildSeamHalf leaves BOTH half manifests null when
            // the recorder has no start snapshot, and the bind clones those nulls onto the
            // proof.
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(null, null, null, Inv("evaChute", 1)));
            // ... and the same with resources present but the inventory baseline missing.
            Assert.Equal(
                OriginPickupKind.Carried,
                RouteProofCapture.ClassifyOriginPickup(
                    Manifest("LiquidFuel", 40.0), Manifest("LiquidFuel", 40.0),
                    null, Inv("DeployedCentralStation", 3)));
            // An EMPTY (non-null) start inventory is a real baseline and DOES yield a gain -
            // that is the discrimination the guard must not destroy, and it is why
            // BuildSeamHalf records an explicit empty list for a half it actually measured
            // (VesselSpawner.ExtractInventoryPayloadItems returns null BOTH for "found no
            // items" and for "nothing to look at", so the producer has to say which).
            Assert.Equal(
                OriginPickupKind.Gain,
                RouteProofCapture.ClassifyOriginPickup(null, null, Inv(), Inv("evaChute", 1)));
        }

        [Fact]
        public void Bind_WithNoStartManifestsAtAll_DoesNotValidate()
        {
            // The same fail-open driven through the whole binder: a pair captured with no
            // start snapshot carries null manifests on both halves, and the transport then
            // undocks carrying cargo. Nothing was measured at the start, so nothing is
            // witnessed and the proof must not become an origin.
            RouteOriginProof proof = PendingProof();
            proof.StartDockedPair.HalfA.StartResources = null;
            proof.StartDockedPair.HalfA.StartInventory = null;

            ConfigNode loaded = SnapshotWithInventory(110u, "LiquidFuel", 40.0, "evaChute");
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, loaded, loaded,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-nobaseline"));

            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);
            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void Pickup_ItemsWithNoIdentityCannotInventADelta()
        {
            // An item with no identity hash cannot be matched across the two snapshots, so
            // counting it could only manufacture a gain out of a re-listing.
            var anonymous = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = null, PartName = "x", Quantity = 5 },
            };
            Assert.Equal(
                OriginPickupKind.None,
                RouteProofCapture.ClassifyOriginPickup(null, Manifest(), null, anonymous));
        }

        [Fact]
        public void PickupDelta_GrammarIsWhitespaceFree_AndCarriesBothKinds()
        {
            // The token is pinned by scenario regexes as ONE field, so it must not contain a
            // space. Resource terms first, ordinal-sorted; the inv: term only when either
            // side carried inventory at all, so a resource-only run's token is unchanged.
            Assert.Equal(
                "LiquidFuel=+200.0;inv:+2",
                RouteProofCapture.FormatOriginPickupDelta(
                    Manifest("LiquidFuel", 0.0), Manifest("LiquidFuel", 200.0),
                    Inv(), Inv("DeployedCentralStation", 1, "evaChute", 1)));
            Assert.Equal(
                "LiquidFuel=+20.0",
                RouteProofCapture.FormatOriginPickupDelta(
                    Manifest("LiquidFuel", 0.0), Manifest("LiquidFuel", 20.0), null, null));
            Assert.Equal(
                "inv:-1",
                RouteProofCapture.FormatOriginPickupDelta(
                    null, null, Inv("evaChute", 2), Inv("evaChute", 1)));
            Assert.Equal(
                "(none)",
                RouteProofCapture.FormatOriginPickupDelta(null, null, null, null));
            Assert.DoesNotContain(" ", RouteProofCapture.FormatOriginPickupDelta(
                Manifest("LiquidFuel", 0.0, "Ore", 0.0),
                Manifest("LiquidFuel", 200.0, "Ore", 5.0),
                Inv(), Inv("evaChute", 1)));
        }

        [Fact]
        public void Bind_InventoryOnlyPickup_ValidatesThroughTheWholeBinder()
        {
            // End to end: the binder must EXTRACT the transport half's inventory from the
            // undock snapshot, not only its resources. A binder that reads resources alone
            // reds here with pickup=Carried.
            RouteOriginProof proof = PendingProof();
            proof.StartDockedPair.HalfA.StartResources = Manifest("LiquidFuel", 40.0);
            proof.StartDockedPair.HalfA.StartInventory = Inv();

            ConfigNode withContainer = SnapshotWithInventory(110u, "LiquidFuel", 40.0, "evaChute");
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, withContainer, withContainer,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-inv"));

            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("pickup=Gain")
                && l.Contains("inv:+1")
                && l.Contains("undockInv=1"));
        }

        // ==============================================================
        // 6c. THE HALF-SCOPED INVENTORY BASELINE, at the PRODUCER (F10)
        //
        // BuildSeamHalf extracts each half's start manifests from the MERGED snapshot scoped
        // to that half's own seam-derived part set. If the scoping were dropped, both halves
        // would carry the merged stack's inventory and a depot-side container would sit in
        // the transport's start baseline - which silently CANCELS a real pickup (the item is
        // already "there" at the start, so the undock shows no rise).
        // ==============================================================

        /// <summary>Merged two-half snapshot: transport parts 100/101, depot parts 200/201,
        /// with a stored part on whichever side the caller names.</summary>
        private static ConfigNode MergedSnapshotWithStoredPartOn(uint ownerPartPid, string storedPartName)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (uint pid in new uint[] { 100u, 101u, 200u, 201u })
            {
                var part = vessel.AddNode("PART");
                part.AddValue("name", "tank");
                part.AddValue("persistentId",
                    pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var res = part.AddNode("RESOURCE");
                res.AddValue("name", "LiquidFuel");
                res.AddValue("amount", "10");
                res.AddValue("maxAmount", "100");
                if (pid != ownerPartPid) continue;
                var module = part.AddNode("MODULE");
                module.AddValue("name", "ModuleInventoryPart");
                module.AddValue("InventorySlots", "4");
                var stored = module.AddNode("STOREDPARTS").AddNode("STOREDPART");
                stored.AddValue("partName", storedPartName);
                stored.AddValue("quantity", "1");
            }
            return vessel;
        }

        private static DockSeamPairCandidate TwoHalfCandidate()
        {
            return new DockSeamPairCandidate(
                11u,
                Half(VesselType.Rover, 100u, "transport"),
                Half(VesselType.Rover, 200u, "depot"),
                new List<uint> { 100u, 101u },
                new List<uint> { 200u, 201u },
                (int)Vessel.Situations.LANDED, "Kerbin", 0.1, -74.7, 68.9);
        }

        [Fact]
        public void Producer_StartInventoryBaselineIsHalfScoped_TheDepotsCargoIsNotTheTransports()
        {
            // The container sits on a DEPOT part (200). It must appear in half B's start
            // inventory and NOT in half A's.
            RouteProofCapture.BuildStartRouteOriginProof(
                (int)Vessel.Situations.LANDED, false,
                new List<DockSeamPairCandidate> { TwoHalfCandidate() }, 1,
                MergedSnapshotWithStoredPartOn(200u, "DeployedCentralStation"),
                false, "<test>", 7u,
                out RouteOriginProof proof, out _);

            Assert.NotNull(proof);
            StartDockedSeamHalf transportHalf = proof.StartDockedPair.HalfA;
            StartDockedSeamHalf depotHalf = proof.StartDockedPair.HalfB;
            Assert.True(transportHalf.StartInventory == null || transportHalf.StartInventory.Count == 0,
                "the depot's container must NOT be in the transport half's start baseline");
            Assert.Single(depotHalf.StartInventory);
            Assert.Equal("DeployedCentralStation", depotHalf.StartInventory[0].PartName);
            // Resources are half-scoped by the same split: 2 parts x 10 units per half.
            Assert.Equal(20.0, transportHalf.StartResources["LiquidFuel"].amount, 6);
            Assert.Equal(20.0, depotHalf.StartResources["LiquidFuel"].amount, 6);
        }

        [Fact]
        public void Producer_TheDepotsCargoInTheMergedStackCannotCancelARealPickup()
        {
            // THE CONSEQUENCE, end to end. The depot holds a container at recording start;
            // the transport takes ONE of its own at the seam. A merged-scope baseline would
            // have the item already present at the start and read no gain.
            RouteProofCapture.BuildStartRouteOriginProof(
                (int)Vessel.Situations.LANDED, false,
                new List<DockSeamPairCandidate> { TwoHalfCandidate() }, 1,
                MergedSnapshotWithStoredPartOn(200u, "DeployedCentralStation"),
                false, "<test>", 7u,
                out RouteOriginProof proof, out _);

            // The transport half (100/101) leaves carrying that same item kind.
            ConfigNode transportAtUndock =
                SnapshotWithInventory(100u, "LiquidFuel", 20.0, "DeployedCentralStation");
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof,
                new List<uint> { 100u, 101u }, new List<uint> { 200u, 201u },
                transportAtUndock, transportAtUndock,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-scope"));

            Assert.Equal(200u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
            // END manifests are re-extracted at the bind on the SAME half scope, so the
            // depot's own container never enters the transport's end inventory either.
            Assert.Single(proof.EndTransportInventory);
            Assert.Equal("DeployedCentralStation", proof.EndTransportInventory[0].PartName);
            Assert.Equal(20.0, proof.EndTransportResources["LiquidFuel"].amount, 6);
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

        /// <summary>A transport-half snapshot carrying a resource AND one stored inventory
        /// part, so the binder's inventory extraction is exercised on a real ConfigNode.</summary>
        private static ConfigNode SnapshotWithInventory(
            uint partPid, string resource, double amount, string storedPartName)
        {
            ConfigNode vessel = SnapshotWithResource(partPid, resource, amount);
            ConfigNode part = vessel.GetNodes("PART")[0];
            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            module.AddValue("InventorySlots", "4");
            ConfigNode stored = module.AddNode("STOREDPARTS").AddNode("STOREDPART");
            stored.AddValue("partName", storedPartName);
            stored.AddValue("quantity", "1");
            return vessel;
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

            // The half IS bound (the split really did leave rover A behind), but the leg
            // only LOST cargo at that seam, so nothing was picked up there and it is NOT a
            // supply origin. This is the delivery endpoint: validating it would debit the
            // vessel the run just delivered to.
            Assert.Equal(5300u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);
            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
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
        // 8c. THE LIVE STOP-THEN-SPLIT ORDER (adversarial review F1)
        //
        // MEASURED: H57's 2026-09-02 flight red because the recorder STOP that
        // OnVesselsUndocking fires runs BEFORE the deferred split, so the stop concluded
        // "ended while still docked", the conclusion was deep-cloned onto the parent
        // recording, and the bind one frame later refused it. The log sequence was
        //   RouteOriginProof pair captured: ... bindState=PairPendingBinding
        //   OnVesselsUndocking: ... decision=SplitRecordedStaysActive
        //   RouteOriginProof unbound: ... reason=stopped-while-docked
        //   Logistics metadata: RouteOriginProof adopted (write-once) path=Append...
        //   RouteOriginProof bind skipped: ... reason=NoPairPending bindState=UnboundAtStop
        // These cells drive that exact ORDER through the same three production helpers the
        // live path calls, so the fix is pinned against the sequence that broke it rather
        // than against the flag that was supposed to prevent it.
        // ==============================================================

        [Fact]
        public void LivePath_StopThenDeepCloneThenBind_StillBinds()
        {
            // THE REGRESSION GATE. Every call here is the production one, in the live order:
            // the recorder stop forwards the pending proof onto a capture recording, the
            // split deep-clones it onto the parent through
            // ParsekFlight.ApplyCapturedLogisticsMetadataToRecording, and CreateSplitBranch
            // binds THAT clone. It is driven with stopIsChainBoundary FALSE on purpose - the
            // worst case, and the one the flight measured - so the cell fails if the bind
            // ever depends on the flag again.
            RouteOriginProof pending = PendingProof();
            var capture = new Recording
            {
                RecordingId = "capture-1",
                VesselSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0),
            };
            RouteProofCapture.AttachEndManifestsAndForwardToCapture(
                capture, pending, new List<uint> { 110u }, stopIsChainBoundary: false);
            Assert.Equal(
                StartDockedOriginBindState.UnboundAtStop,
                capture.RouteOriginProof.StartDockedOriginBindState);

            var parentRec = new Recording { RecordingId = "parent-1" };
            ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(parentRec, capture, "split");
            Assert.NotNull(parentRec.RouteOriginProof);
            Assert.Equal(
                StartDockedOriginBindState.UnboundAtStop,
                parentRec.RouteOriginProof.StartDockedOriginBindState);

            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                parentRec.RouteOriginProof, HalfAParts, HalfBParts,
                activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 276.0, parentRec.RecordingId));

            Assert.Equal(
                StartDockedOriginBindState.BoundAtUndock,
                parentRec.RouteOriginProof.StartDockedOriginBindState);
            Assert.Equal(555u, parentRec.RouteOriginProof.StartDockedOriginRootPartUId);
            Assert.True(parentRec.RouteOriginProof.StartDockedOriginPickupValidated);
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(parentRec));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("recoveredFromStopStamp=1"));
        }

        [Fact]
        public void LivePath_ChainBoundaryStop_LeavesThePairPendingAndBindsCleanly()
        {
            // The flag still does its job: a chain-boundary stop must not write the
            // misleading "stopped while docked" conclusion at all, so the ordinary path binds
            // with recoveredFromStopStamp=0.
            RouteOriginProof pending = PendingProof();
            var capture = new Recording
            {
                RecordingId = "capture-2",
                VesselSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0),
            };
            RouteProofCapture.AttachEndManifestsAndForwardToCapture(
                capture, pending, new List<uint> { 110u }, stopIsChainBoundary: true);

            Assert.Equal(
                StartDockedOriginBindState.PairPendingBinding,
                capture.RouteOriginProof.StartDockedOriginBindState);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=stopped-while-docked"));

            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                capture.RouteOriginProof, HalfAParts, HalfBParts,
                activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 276.0, "capture-2"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("recoveredFromStopStamp=0"));
        }

        [Fact]
        public void LivePath_AStampedProofBindsWithNoForwardingPlumbingInvolved()
        {
            // THE SECOND DISCRIMINATING CELL for the advisory-stamp rule, and it is
            // deliberately NOT the same shape as the one above. That one drives the whole
            // live sequence (forward -> deep clone -> bind), so a refactor of the forwarding
            // helpers could mask the property it pins. This one calls the stamp DIRECTLY and
            // then binds, so it reds on a terminal-stamp regression no matter what the
            // capture / forward plumbing is doing.
            //
            // Mutation-checked: restoring the terminal guard (bind only when
            // BindState == PairPendingBinding) reds this cell AND
            // LivePath_StopThenDeepCloneThenBind_StillBinds; the ChainBoundary, decision and
            // still-docked cells all survive it, because none of them ever binds a stamped
            // proof.
            RouteOriginProof proof = PendingProof();
            Assert.True(RouteProofCapture.MarkStartDockedOriginUnboundAtStop(proof, "rec-1"));
            Assert.Equal(StartDockedOriginBindState.UnboundAtStop, proof.StartDockedOriginBindState);

            ConfigNode activeSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, HalfAParts, HalfBParts, activeSnapshot, activeSnapshot,
                500u, GuidB, 400u, GuidA, 276.0, "rec-1"));

            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);
            Assert.Equal(555u, proof.StartDockedOriginRootPartUId);
            Assert.True(proof.StartDockedOriginPickupValidated);
            // The token the H57 spec pins, emitted on exactly this path.
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("recoveredFromStopStamp=1"));
        }

        [Fact]
        public void LivePath_OnlyBoundAtUndockIsFinal_TheStampIsNot()
        {
            // States the rule as one pair of assertions: a STAMPED proof is still bindable, a
            // BOUND one never is. If the terminal guard comes back, the first half reds; if
            // write-once is dropped, the second does.
            RouteOriginProof stamped = PendingProof();
            RouteProofCapture.MarkStartDockedOriginUnboundAtStop(stamped, "rec-1");
            ConfigNode snap = SnapshotWithResource(110u, "LiquidFuel", 90.0);
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                stamped, HalfAParts, HalfBParts, snap, snap,
                500u, GuidB, 400u, GuidA, 276.0, "rec-1"));

            // Now it IS final - a second undock cannot move it.
            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                stamped, HalfBParts, HalfAParts, snap, snap,
                900u, GuidB, 400u, GuidA, 402.5, "rec-1"));
            Assert.Equal(555u, stamped.StartDockedOriginRootPartUId);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bind skipped:")
                && l.Contains("reason=already-bound"));
        }

        [Fact]
        public void LivePath_TheUndockDecisionIsTheSplitRecordedStaysActiveBranch()
        {
            // Pins WHICH branch the sequence above models. H57's flight logged
            // recordedPid=313889796 oldPid=313889796 newPid=2934387529 - the recorded vessel
            // keeps its pid and the depot leaves as the new vessel - and
            // DeferredUndockBranch then follows the FOCUSED side, which is the recorded one.
            Assert.Equal(
                UndockSplitDecision.SplitRecordedStaysActive,
                SegmentBoundaryLogic.ClassifyUndockSplit(313889796u, 313889796u, 2934387529u));
        }

        [Fact]
        public void LivePath_AnOrdinaryStopWhileStillDockedStillEndsAsANonOrigin()
        {
            // The other direction of the same relaxation: making UnboundAtStop bindable must
            // NOT make a genuinely-still-docked recording an origin. Nothing binds it, so it
            // stays refused.
            RouteOriginProof pending = PendingProof();
            var capture = new Recording
            {
                RecordingId = "capture-3",
                VesselSnapshot = SnapshotWithResource(110u, "LiquidFuel", 90.0),
            };
            RouteProofCapture.AttachEndManifestsAndForwardToCapture(
                capture, pending, new List<uint> { 110u }, stopIsChainBoundary: false);

            Assert.Equal(
                StartDockedOriginBindState.UnboundAtStop,
                capture.RouteOriginProof.StartDockedOriginBindState);
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(capture));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof unbound:")
                && l.Contains("reason=stopped-while-docked"));
        }

        // ==============================================================
        // 8d. IDENTICAL PART SETS (adversarial review F4)
        //
        // THE REVIEWER'S PREMISE WAS THAT TWO HALVES FROM THE SAME CRAFT FILE CARRY THE SAME
        // PART PIDS, so overlap would read BothHalvesActive and never bind. MEASURED AND
        // REFUTED against a real save with three same-craft-file rovers live at once
        // (saves/logistics-rover-B/persistent.sfs, 2026-09-02): rover A (22 parts), rover B
        // (17) and rover C (18) have PAIRWISE DISJOINT part persistentId sets - 0 shared ids
        // across all three pairs - and distinct vessel pids and guids. That is KSP's own
        // rule working: a craft-baked persistentId is regenerated on launch when it collides
        // with a CURRENTLY-LIVE vessel, and two docked halves are by definition both live.
        //
        // The shape is therefore not reachable from same-craft launches. It is pinned anyway,
        // because the refusal is the fail-closed behaviour that matters if it ever IS
        // reachable (a hand-authored fixture, a future Parsek-spawned copy that bypasses
        // KSP's dedup, or a save edited by hand).
        // ==============================================================

        [Fact]
        public void IdenticalHalfPartSets_RefuseToBind_FailClosed()
        {
            var shared = new List<uint> { 110u, 111u };
            Assert.Equal(
                OriginUndockBinding.BothHalvesActive,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    shared, shared, shared, shared));
        }

        [Fact]
        public void IdenticalHalfPartSets_TheWholeBindRefusesAndLeavesNoOrigin()
        {
            RouteOriginProof proof = PendingProof();
            var shared = new List<uint> { 110u, 111u };
            proof.StartDockedPair.HalfA.PartPersistentIds = new List<uint>(shared);
            proof.StartDockedPair.HalfB.PartPersistentIds = new List<uint>(shared);

            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, shared, shared,
                SnapshotWithResource(110u, "LiquidFuel", 90.0), null,
                500u, GuidB, 400u, GuidA, 1234.5, "rec-1"));

            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);
            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void RoverRelay_TheThreeSameCraftRoversHaveDisjointPartSets()
        {
            // The measurement that refuted the premise, kept as data so the claim in the
            // memo has a cell behind it. These are the real part persistentIds of the three
            // same-craft-file rovers, read from the produced save.
            var roverA = new List<uint>
            {
                3590051213u, 700767647u, 3569776550u, 4104863657u, 3611348372u, 3805741184u,
                2874976564u, 584493636u, 1383402226u, 429354992u, 2646195783u, 669075699u,
                2503959988u, 4203625539u, 4146836539u, 209085523u, 3040514992u, 2580916810u,
                575286956u, 1071913422u, 4103138199u, 1219585696u,
            };
            var roverB = new List<uint>
            {
                2823035582u, 202800680u, 4230224982u, 2478770940u, 2791927569u, 894881109u,
                2709475368u, 4143210994u, 1200887512u, 1781664479u, 1715808488u, 2354159901u,
                3687420106u, 2569670107u, 643575962u, 2114703283u, 1373868859u,
            };
            var roverC = new List<uint>
            {
                1049412091u, 152425893u, 165679706u, 347679225u, 3948580584u, 4233882603u,
                420012032u, 2980551401u, 4279716197u, 2522475473u, 1757682533u, 2477815453u,
                1999089396u, 2718407327u, 165949968u, 395515579u, 3830248754u, 410845u,
            };

            var setB = new HashSet<uint>(roverB);
            var setC = new HashSet<uint>(roverC);
            Assert.DoesNotContain(roverA, pid => setB.Contains(pid));
            Assert.DoesNotContain(roverA, pid => setC.Contains(pid));
            Assert.DoesNotContain(roverB, pid => setC.Contains(pid));

            // And so the binding reads cleanly on the real relay: C keeps flying, B is left.
            Assert.Equal(
                OriginUndockBinding.BoundToHalfB,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    roverC, roverB, roverC, roverB));
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
        public void StopWhileDocked_NeverUndoesABind_WriteOnceIsTheOnlyFinalState()
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

        // ==============================================================
        // 11. THE 2026-09-03 RELAY, and the two ways the binder got it wrong
        //
        // MEASURED on logs/2026-09-03_0026_rover-c (KSP.log, DLL carrying #1618 but not
        // #1620). Rover C launched from the Runway with 200 LiquidFuel, drove to rover B,
        // docked at UT 155.82 and TOOK 154.4 LiquidFuel plus three inventory items off B,
        // undocked at UT 212.54, drove to lander A, docked at UT 274.18, GAVE A 200
        // LiquidFuel and undocked at UT 335.32. Under the operator ruling that is one supply
        // route: source B, destination A. The session produced ZERO routes, and the binder
        // wrote both of these lines:
        //
        //   HOP 1  Route window delta: window=dock-155.82...-target-90564594
        //          transportDelta=[LiquidFuel=+154.4] endpointDelta=[LiquidFuel=-154.4]
        //          RouteOriginProof bound at undock: ... binding=BoundToHalfB
        //          originRoot=3466447829 originName='C' ... transportRoot=549109006
        //     -> the halves are INVERTED. B was the dominant half of the merge, KSP kept
        //        focus on B one frame after the split, and the binder therefore called B the
        //        transport and stamped C - the actual transport - as its own supply origin.
        //
        //   HOP 2  Route window delta: ... transportDelta=[LiquidFuel=-200.0]
        //          endpointDelta=[LiquidFuel=+200.0]
        //          RouteOriginProof bound at undock: ... originRoot=701791207 originName='A'
        //          ... pickup=Carried pickupValidated=0
        //     -> the halves are right and the SEAM is wrong: A is the vessel C had just
        //        delivered to, and a delivery partner is never an origin.
        //
        // Root part uids and vessel pids below are the real ones from that log.
        // ==============================================================

        // Rover B (the depot / source), root part flightID 549109006, live pid 90564594.
        private static readonly List<uint> RelayBParts = new List<uint> { 6200u, 6201u };
        // Rover C (the transport), root part flightID 3466447829, live pid 612987736.
        private static readonly List<uint> RelayCParts = new List<uint> { 6100u, 6101u };
        // Lander A (the destination), root part flightID 701791207, live pid 4280917262.
        private static readonly List<uint> RelayAParts = new List<uint> { 6300u, 6301u };

        private const uint RelayBRoot = 549109006u;
        private const uint RelayCRoot = 3466447829u;
        private const uint RelayARoot = 701791207u;

        /// <summary>
        /// The pair as capture recorded it on the operator's hop-1 recording: half A is the
        /// SCAN-ORDER near half, which on that flight was rover B (the merged vessel's own
        /// name), and half B is rover C. Those labels carry no semantics - which is exactly
        /// what the defect exploited.
        /// </summary>
        private static RouteOriginProof RelayHop1PendingProof()
        {
            return new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayBRoot,
                        VesselName = "B",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RelayBParts),
                        StartResources = Manifest("LiquidFuel", 400.0),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayCRoot,
                        VesselName = "C",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RelayCParts),
                        StartResources = Manifest("LiquidFuel", 200.0),
                    },
                },
            };
        }

        private static RouteOriginProof RelayHop2PendingProof()
        {
            return new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayCRoot,
                        VesselName = "C",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RelayCParts),
                        StartResources = Manifest("LiquidFuel", 354.4),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayARoot,
                        VesselName = "A",
                        VesselType = (int)VesselType.Lander,
                        PartPersistentIds = new List<uint>(RelayAParts),
                        StartResources = Manifest("LiquidFuel", 0.0),
                    },
                },
            };
        }

        private static RouteConnectionWindow Window(
            string id, List<uint> transportParts, List<uint> endpointParts)
        {
            return new RouteConnectionWindow
            {
                WindowId = id,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint>(transportParts),
                EndpointPartPersistentIds = new List<uint>(endpointParts),
            };
        }

        // --- the pure decisions -------------------------------------------

        [Fact]
        public void WindowNamesHalfAsTransport_NeedsBothSidesToStraddleThePair()
        {
            RouteConnectionWindow w = Window("dock-155.82-target-90564594", RelayCParts, RelayBParts);

            Assert.True(RouteProofCapture.WindowNamesHalfAsTransport(w, RelayCParts, RelayBParts));
            // The mirror direction must NOT also match, or the window names no direction.
            Assert.False(RouteProofCapture.WindowNamesHalfAsTransport(w, RelayBParts, RelayCParts));
            // A window about a THIRD vessel straddles nothing here.
            Assert.False(RouteProofCapture.WindowNamesHalfAsTransport(
                Window("other", RelayCParts, RelayAParts), RelayCParts, RelayBParts));
            Assert.False(RouteProofCapture.WindowNamesHalfAsTransport(null, RelayCParts, RelayBParts));
        }

        [Fact]
        public void WitnessedDockScan_NoWindowsIsNotAWitnessedDock()
        {
            // THE START-DOCKED FAMILY. The recording opened already coupled, so no window
            // could exist for this seam - which is the fact the bind gate keys on.
            Assert.False(RouteProofCapture.TryResolveWitnessedDockTransportHalf(
                null, RelayCParts, RelayBParts, out bool a, out bool b));
            Assert.False(a);
            Assert.False(b);

            Assert.False(RouteProofCapture.TryResolveWitnessedDockTransportHalf(
                new List<RouteConnectionWindow> { Window("unrelated", RelayAParts, RelayAParts) },
                RelayCParts, RelayBParts, out a, out b));
        }

        [Fact]
        public void WitnessedDockScan_ContradictingWindowsWitnessTheDockButNameNoDirection()
        {
            // A dock, an undock and a RE-dock of the same pair the other way round on one
            // recording. The dock is still witnessed (the gate must close), but the direction
            // is ambiguous and the resolver must fall through rather than pick one.
            var windows = new List<RouteConnectionWindow>
            {
                Window("w1", RelayCParts, RelayBParts),
                Window("w2", RelayBParts, RelayCParts),
            };
            Assert.True(RouteProofCapture.TryResolveWitnessedDockTransportHalf(
                windows, RelayCParts, RelayBParts,
                out bool halfAIsTransport, out bool halfBIsTransport));
            Assert.True(halfAIsTransport);
            Assert.True(halfBIsTransport);

            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfB,
                    windowNamesHalfATransport: true, windowNamesHalfBTransport: true,
                    halfAGained: false, halfBGained: false);
            Assert.Equal(OriginUndockBinding.BoundToHalfB, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.PostSplitActiveSide, decision.Signal);
            Assert.False(decision.OverrodeActiveSide);
        }

        [Fact]
        public void ResolveTransportHalf_TheWindowOutranksFocus()
        {
            // HOP 1, as pure inputs. Focus said half A (rover B) was the transport, so the
            // pre-fix answer was BoundToHalfB - rover C, the transport itself. The window
            // says half B is the transport, so the origin is half A.
            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfB,
                    windowNamesHalfATransport: false, windowNamesHalfBTransport: true,
                    halfAGained: false, halfBGained: true);

            Assert.Equal(OriginUndockBinding.BoundToHalfA, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.WitnessedDockWindow, decision.Signal);
            Assert.True(decision.OverrodeActiveSide);
        }

        [Fact]
        public void ResolveTransportHalf_TheWindowOutranksTheGainToo()
        {
            // THE ASYMMETRY THE FIX MUST NOT COMMIT: on a DELIVERY window the ENDPOINT gains,
            // so "the half that gained is the transport" would name the run its own origin
            // all over again. The window wins, and the delivery is then refused by the gate.
            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfB,
                    windowNamesHalfATransport: true, windowNamesHalfBTransport: false,
                    halfAGained: false, halfBGained: true);

            Assert.Equal(OriginUndockBinding.BoundToHalfB, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.WitnessedDockWindow, decision.Signal);
            Assert.False(decision.OverrodeActiveSide);
        }

        [Fact]
        public void ResolveTransportHalf_NoWindow_TheGainOnlyCorroboratesFocus()
        {
            // THE START-DOCKED FAMILY, flow AGREEING with focus. BoundToHalfB means focus
            // called half B the transport, and half B is the half that gained: corroboration,
            // recorded in the signal, no change to the binding.
            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfA,
                    windowNamesHalfATransport: false, windowNamesHalfBTransport: false,
                    halfAGained: false, halfBGained: true);

            Assert.Equal(OriginUndockBinding.BoundToHalfA, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.DockedSpanGain, decision.Signal);
            Assert.False(decision.OverrodeActiveSide);
            Assert.False(decision.FlowContradictsFocus);
        }

        [Fact]
        public void ResolveTransportHalf_NoWindow_AContradictingFlowRefusesRatherThanReversing()
        {
            // THE ASYMMETRY THIS PACKAGE MUST NOT COMMIT, in its most dangerous form. Focus
            // says half B is the transport (BoundToHalfA = origin is A), the cargo says half A
            // gained. Reversing on the cargo would name the RUN as its own origin whenever a
            // start-docked transport DELIVERS to its depot and drives off - the same inversion
            // this package removes, coming back through the other door. And in the no-window
            // case focus is not a mere proxy: the recorder follows the focused vessel across a
            // split, so the active half IS the half this recording continues on. Two signals
            // disagree, neither arbitrates, so the binding stays put and the bind is refused.
            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfA,
                    windowNamesHalfATransport: false, windowNamesHalfBTransport: false,
                    halfAGained: true, halfBGained: false);

            Assert.Equal(OriginUndockBinding.BoundToHalfA, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.DockedSpanGainContradictsFocus,
                decision.Signal);
            Assert.False(decision.OverrodeActiveSide);
            Assert.True(decision.FlowContradictsFocus);

            // And the gate refuses it, whatever the transport half's own pickup reads.
            Assert.Equal(
                RouteProofCapture.OriginBindGate.SkipFlowContradictsFocus,
                RouteProofCapture.ClassifyOriginBindGate(
                    OriginPickupKind.Carried, false, flowContradictsFocus: true));
            Assert.Equal(
                RouteProofCapture.OriginBindGate.SkipFlowContradictsFocus,
                RouteProofCapture.ClassifyOriginBindGate(
                    OriginPickupKind.None, true, flowContradictsFocus: true));
        }

        [Fact]
        public void StartDockedDeliveryToTheDepot_BindsNothingRatherThanNamingTheRunItsOwnOrigin()
        {
            // THE SAME CASE END TO END, and the reason the cross-check refuses instead of
            // reversing. The recording opens docked at a depot, the transport hands the depot
            // 200 LiquidFuel and undocks. Half A is the transport (focus keeps it, and the
            // recorder follows focus); half B, the depot, is the half that GAINED. A binder
            // that took "the half that gained is the transport" literally would stamp the
            // transport as the origin of its own run.
            var proof = new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayCRoot,
                        VesselName = "transport",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RelayCParts),
                        StartResources = Manifest("LiquidFuel", 354.4),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = RelayBRoot,
                        VesselName = "depot",
                        VesselType = (int)VesselType.Rover,
                        PartPersistentIds = new List<uint>(RelayBParts),
                        StartResources = Manifest("LiquidFuel", 0.0),
                    },
                },
            };

            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayBParts,
                SnapshotWithResource(6100u, "LiquidFuel", 154.4), null,
                originLiveVesselPid: 90564594u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 612987736u, recordedVesselGuid: GuidA,
                undockUT: 900.0, recordingContext: "start-docked-delivery",
                backgroundSideSnapshot: SnapshotWithResource(6200u, "LiquidFuel", 200.0)));

            Assert.Equal(
                StartDockedOriginBindState.PairPendingBinding, proof.StartDockedOriginBindState);
            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("RouteOriginProof transport half overridden:")
                && l.Contains("resolved=refused")
                && l.Contains("focusTransportRoot=3466447829")
                && l.Contains("runTransportRoot=549109006"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bind skipped:")
                && l.Contains("reason=SkipFlowContradictsFocus")
                && l.Contains("dockWitnessed=0"));
        }

        [Fact]
        public void ResolveTransportHalf_NothingMoved_FallsBackToFocus()
        {
            // A pure re-dock: neither half's cargo moved, no window. Focus is the last
            // resort, and the gate then binds nothing that could be an origin.
            RouteProofCapture.SeamTransportHalfDecision decision =
                RouteProofCapture.ResolveTransportHalfAtUndock(
                    OriginUndockBinding.BoundToHalfA, false, false, false, false);

            Assert.Equal(OriginUndockBinding.BoundToHalfA, decision.Binding);
            Assert.Equal(
                RouteProofCapture.SeamTransportHalfSignal.PostSplitActiveSide, decision.Signal);
            Assert.False(decision.OverrodeActiveSide);
        }

        [Fact]
        public void ResolveTransportHalf_AShapeRefusalIsNeverOverruledByCargo()
        {
            // FAIL CLOSED. These are statements about the SPLIT - the same-craft
            // identical-part-set case (BothHalvesActive) among them - and no cargo delta can
            // make an unseparated pair separate. A window AND a gain both point at half B in
            // every iteration here and must still change nothing.
            var refusals = new List<OriginUndockBinding>
            {
                OriginUndockBinding.BothHalvesActive,
                OriginUndockBinding.ActiveHalfUnresolved,
                OriginUndockBinding.UnrelatedSeam,
                OriginUndockBinding.NoPairPending,
            };
            foreach (OriginUndockBinding refusal in refusals)
            {
                RouteProofCapture.SeamTransportHalfDecision decision =
                    RouteProofCapture.ResolveTransportHalfAtUndock(
                        refusal,
                        windowNamesHalfATransport: false, windowNamesHalfBTransport: true,
                        halfAGained: false, halfBGained: true);

                Assert.Equal(refusal, decision.Binding);
                Assert.False(decision.OverrodeActiveSide);
            }
        }

        [Fact]
        public void ClassifyOriginBindGate_WitnessedDockIsTheDiscriminator()
        {
            // A Gain always binds. Without a witnessed dock the pre-fix Carried stamp stays
            // (it is observability, never an origin). WITH a witnessed dock, the absence of a
            // gain is positive evidence the partner supplied nothing.
            AssertGate(OriginPickupKind.Gain, true, RouteProofCapture.OriginBindGate.BindGain);
            AssertGate(OriginPickupKind.Gain, false, RouteProofCapture.OriginBindGate.BindGain);
            AssertGate(OriginPickupKind.Carried, false,
                RouteProofCapture.OriginBindGate.BindStartDockedCarried);
            AssertGate(OriginPickupKind.None, false,
                RouteProofCapture.OriginBindGate.BindStartDockedCarried);
            AssertGate(OriginPickupKind.NoUndockManifest, false,
                RouteProofCapture.OriginBindGate.BindStartDockedCarried);
            AssertGate(OriginPickupKind.Carried, true,
                RouteProofCapture.OriginBindGate.SkipDeliveryWindow);
            AssertGate(OriginPickupKind.None, true,
                RouteProofCapture.OriginBindGate.SkipDeliveryWindow);
            AssertGate(OriginPickupKind.NoUndockManifest, true,
                RouteProofCapture.OriginBindGate.SkipNoUndockManifest);

            // A Gain outranks the contradiction flag, and the two cannot co-occur in
            // production: a contradiction means the measured half is the one that did NOT gain.
            Assert.Equal(
                RouteProofCapture.OriginBindGate.BindGain,
                RouteProofCapture.ClassifyOriginBindGate(
                    OriginPickupKind.Gain, false, flowContradictsFocus: true));
        }

        private static void AssertGate(
            OriginPickupKind pickup, bool dockWitnessed, RouteProofCapture.OriginBindGate expected)
        {
            Assert.Equal(expected, RouteProofCapture.ClassifyOriginBindGate(pickup, dockWitnessed));
            Assert.Equal(
                expected == RouteProofCapture.OriginBindGate.SkipDeliveryWindow
                    || expected == RouteProofCapture.OriginBindGate.SkipNoUndockManifest,
                RouteProofCapture.IsBindRefusal(expected));
        }

        // --- the two hops, end to end through the orchestrator -------------

        [Fact]
        public void RelayHop1_FocusHeldTheDepot_TheWindowStillNamesRoverBAsTheOrigin()
        {
            // THE DEFECT ITSELF. Focus is on rover B (half A) after the split, so
            // ClassifyUndockOriginBinding alone answers BoundToHalfB - rover C, the
            // transport, as its own origin, which is the line the operator's log carries.
            RouteOriginProof proof = RelayHop1PendingProof();
            Assert.Equal(
                OriginUndockBinding.BoundToHalfB,
                RouteProofCapture.ClassifyUndockOriginBinding(
                    RelayBParts, RelayCParts, RelayBParts, RelayCParts));

            // Rover B keeps focus and ends with LESS fuel; rover C is backgrounded and ends
            // with MORE (200 -> 354.4, the measured +154.4).
            ConfigNode roverBAtUndock = SnapshotWithResource(6200u, "LiquidFuel", 245.6);
            ConfigNode roverCAtUndock = SnapshotWithResource(6100u, "LiquidFuel", 354.4);

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayBParts, RelayCParts,
                roverBAtUndock, null,
                originLiveVesselPid: 612987736u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 90564594u, recordedVesselGuid: GuidA,
                undockUT: 212.54, recordingContext: "39ac117a8a8b4d61b1296983e7d538a8",
                backgroundSideSnapshot: roverCAtUndock,
                recordingConnectionWindows: new List<RouteConnectionWindow>
                {
                    Window("dock-155.8200000000059-target-90564594", RelayCParts, RelayBParts)
                },
                activeSideLiveVesselPid: 90564594u,
                activeSideLiveVesselGuid: GuidA);

            Assert.True(bound);
            Assert.Equal(RelayBRoot, proof.StartDockedOriginRootPartUId);
            Assert.Equal("B", proof.StartDockedOriginVesselName);
            Assert.Equal(RelayCRoot, proof.StartDockedTransportRootPartUId);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
            // The transport's start manifest must be ROVER C's 200, not rover B's 400 - the
            // pickup was measured on the BACKGROUNDED half, which the pre-fix binder could
            // not even read (it only ever extracted from the active snapshot).
            Assert.Equal(200.0, proof.StartTransportResources["LiquidFuel"].amount, 6);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.IsSelfOriginProof(proof));

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("RouteOriginProof transport half overridden:")
                && l.Contains("signal=WitnessedDockWindow")
                && l.Contains("focusTransportRoot=549109006")
                && l.Contains("runTransportRoot=3466447829"));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("originRoot=549109006")
                && l.Contains("transportSignal=WitnessedDockWindow")
                && l.Contains("dockWitnessed=1")
                && l.Contains("gate=BindGain"));
        }

        [Fact]
        public void RelayHop2_TheDeliveryPartnerIsNotAnOriginAndNothingIsWritten()
        {
            // THE SECOND DEFECT. The halves are right this time - rover C keeps flying,
            // lander A is left behind - but C only LOST cargo at this seam. A is the
            // destination, and the recording witnessed the dock, so nothing may be written.
            RouteOriginProof proof = RelayHop2PendingProof();
            ConfigNode roverCAtUndock = SnapshotWithResource(6100u, "LiquidFuel", 154.4);
            ConfigNode landerAAtUndock = SnapshotWithResource(6300u, "LiquidFuel", 200.0);

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayAParts,
                roverCAtUndock, null,
                originLiveVesselPid: 4280917262u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 612987736u, recordedVesselGuid: GuidA,
                undockUT: 335.32, recordingContext: "b9df0ee00fd84831a0d9619b4e34fc97",
                backgroundSideSnapshot: landerAAtUndock,
                recordingConnectionWindows: new List<RouteConnectionWindow>
                {
                    Window("dock-274.18000000004059-target-2123618197", RelayCParts, RelayAParts)
                },
                activeSideLiveVesselPid: 612987736u,
                activeSideLiveVesselGuid: GuidA);

            Assert.False(bound);
            // NOT HALF-WRITTEN: the proof is exactly as capture left it.
            Assert.Equal(
                StartDockedOriginBindState.PairPendingBinding, proof.StartDockedOriginBindState);
            Assert.Equal(0u, proof.StartDockedOriginRootPartUId);
            Assert.Equal(0u, proof.StartDockedTransportRootPartUId);
            Assert.Null(proof.StartDockedOriginVesselName);
            Assert.False(proof.StartDockedOriginPickupValidated);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("RouteOriginProof bind skipped:")
                && l.Contains("reason=SkipDeliveryWindow")
                && l.Contains("candidateOriginRoot=701791207")
                && l.Contains("dockWitnessed=1"));
        }

        [Fact]
        public void RelayHop2Shape_WithNoWitnessedDock_KeepsThePreFixCarriedStamp()
        {
            // THE MIRROR THE FIX MUST NOT BREAK. Same delivery direction, but the recording
            // opened ALREADY docked (the start-docked family - H55/H56's probe rig and H57's
            // depot leg): no window exists, the load predates the recorder, and the
            // unvalidated Carried stamp is kept exactly as it was.
            RouteOriginProof proof = RelayHop2PendingProof();
            ConfigNode roverCAtUndock = SnapshotWithResource(6100u, "LiquidFuel", 154.4);

            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayAParts, roverCAtUndock, null,
                4280917262u, GuidB, 612987736u, GuidA, 335.32, "b9df0ee0"));

            Assert.Equal(StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);
            Assert.Equal(RelayARoot, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
        }

        [Fact]
        public void WriteOnce_AnUnvalidatedStampDoesNotLockTheSlotAgainstARealPickup()
        {
            // FIRST **VALID** BIND WINS. An unvalidated Carried stamp is an observation, not
            // an origin, so letting it hold the write-once slot would trade a wrong answer
            // for a missing one.
            RouteOriginProof proof = RelayHop2PendingProof();
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayAParts,
                SnapshotWithResource(6100u, "LiquidFuel", 154.4), null,
                4280917262u, GuidB, 612987736u, GuidA, 335.32, "rec"));
            Assert.False(proof.StartDockedOriginPickupValidated);
            Assert.Equal(RelayARoot, proof.StartDockedOriginRootPartUId);

            // A later seam on the same recording DOES witness a gain: 354.4 -> 500.0 on the
            // transport half. It must be allowed to land.
            proof.StartDockedPair.HalfB.RootPartUId = RelayBRoot;
            proof.StartDockedPair.HalfB.VesselName = "B";
            Assert.True(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayAParts,
                SnapshotWithResource(6100u, "LiquidFuel", 500.0), null,
                90564594u, GuidB, 612987736u, GuidA, 600.0, "rec"));
            Assert.Equal(RelayBRoot, proof.StartDockedOriginRootPartUId);
            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);

            // And a VALIDATED bind is still final.
            Assert.False(RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, RelayCParts, RelayAParts,
                SnapshotWithResource(6100u, "LiquidFuel", 900.0), null,
                4280917262u, GuidB, 612987736u, GuidA, 700.0, "rec"));
            Assert.Equal(RelayBRoot, proof.StartDockedOriginRootPartUId);
        }

        [Fact]
        public void SelfOriginProof_IsRefusedAtTheRead_BecauseTheBytesAreAlreadyOnDisk()
        {
            // THE READ-SIDE HALF. The operator's save carries a proof whose origin root IS
            // its transport root (rover C on both sides). Recordings are never migrated, so
            // the analysis has to refuse it rather than trust the bind that wrote it.
            var selfOrigin = new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock,
                StartDockedOriginPickupValidated = true,
                StartDockedOriginRootPartUId = RelayCRoot,
                StartDockedTransportRootPartUId = RelayCRoot,
                StartDockedOriginVesselName = "C",
            };
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.IsSelfOriginProof(selfOrigin));
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(
                new Recording { RecordingId = "r", RouteOriginProof = selfOrigin }));

            // A genuine two-vessel proof is untouched by the guard.
            var genuine = new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock,
                StartDockedOriginPickupValidated = true,
                StartDockedOriginRootPartUId = RelayBRoot,
                StartDockedTransportRootPartUId = RelayCRoot,
            };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.IsSelfOriginProof(genuine));
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(
                new Recording { RecordingId = "r", RouteOriginProof = genuine }));

            // A transport root that was never stamped (0) is not "self", or every pre-P12
            // proof would read as one.
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.IsSelfOriginProof(
                new RouteOriginProof { StartDockedOriginRootPartUId = 0u }));
        }
    }
}
