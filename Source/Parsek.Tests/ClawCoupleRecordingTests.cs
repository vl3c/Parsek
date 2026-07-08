using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-10 archetype 4 (claw couples - the 2026-07-06 verification sweep's highest-risk
    // AUTO-NONE cell): a claw (Advanced Grabbing Unit) grab fires the SAME GameEvents.onPartCouple
    // event as a docking-port couple (KSP's Part.Couple path, used by ModuleGrappleNode), and
    // ParsekFlight.OnPartCouple has no docking-port/module-type filter - so a claw grab records as
    // a Dock-EQUIVALENT branch point by construction. These tests pin the pure seams of that flow
    // for the claw shape:
    //
    //  1. BuildMergeBranchData produces the Dock-type BranchPoint (MergeCause "DOCK") for a
    //     claw-grab merge, with and without a route-eligible partner. (Note: BranchPoint.cs
    //     mentions "CLAW" as an intended MergeCause value, but nothing emits it today - a claw
    //     grab deliberately records MergeCause "DOCK". Pinned here so a future CLAW
    //     differentiation is a conscious contract change.)
    //  2. The couple-event partner resolution seams (ResolveDockPartnerPidFromEvent /
    //     IsKnownDockPartnerForRoute) behave correctly for an asteroid partner that has no
    //     Parsek recording (route eligibility for a first grab rides on the pre-couple partner
    //     snapshot disjunct in OnPartCouple, not on a known recording).
    //  3. The deferred BREAKUP scan rejects both the raw asteroid (SpaceObject) and the merged
    //     post-grab ship (which now carries the PotatoRoid part + ModuleAsteroid module), so the
    //     couple path stays the SOLE recording authority for a grab - the breakup scan never
    //     misclassifies the merge.
    //  4. The ghost snapshot part-name path: "PotatoRoid" survives TryExtractPartName (with and
    //     without the trailing numeric uid suffix KSP snapshots may carry) and the
    //     VesselSnapshotBuilder.ClawedAsteroidShip generator produces a coupled snapshot whose
    //     root/part identity resolves through TryGetSnapshotRootPartInfo. The PartLoader half of
    //     the name path (getPartInfoByName / ResolveAvailablePart) needs a live game and is
    //     covered by InGameTests/ClawCoupleInGameTest.cs.
    [Collection("Sequential")]
    public class ClawCoupleRecordingTests : IDisposable
    {
        private const uint TransportPid = 555001u;
        private const uint AsteroidPid = 3141592653u;   // SpaceObject pid from KSP's discovery spawner
        private const uint MergedPid = 987654321u;      // fresh survivor pid KSP assigns at couple time

        public ClawCoupleRecordingTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region Dock-equivalent branch point for a claw grab

        [Fact]
        public void ClawGrab_BuildMergeBranchData_RecordsDockEquivalentBranchPoint()
        {
            // A first asteroid grab: single parent (the transport's recording; the asteroid has
            // no Parsek recording), route-eligible partner resolved from the couple event via the
            // pre-couple snapshot disjunct. The branch point must be the same Dock shape a
            // docking-port couple produces.
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: new List<string> { "transport_rec" },
                treeId: "tree_claw",
                mergeUT: 12345.0,
                branchType: BranchPointType.Dock,
                mergedVesselPid: MergedPid,
                mergedVesselName: "Grabber + Ast. HSJ-227",
                targetVesselPersistentId: AsteroidPid);

            Assert.Equal(BranchPointType.Dock, bp.Type);
            Assert.Equal("DOCK", bp.MergeCause);        // no "CLAW" cause exists today (see header)
            Assert.Equal(12345.0, bp.UT);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal("transport_rec", bp.ParentRecordingIds[0]);
            Assert.Single(bp.ChildRecordingIds);
            Assert.Equal(AsteroidPid, bp.TargetVesselPersistentId);

            Assert.Equal(mergedChild.RecordingId, bp.ChildRecordingIds[0]);
            Assert.Equal(bp.Id, mergedChild.ParentBranchPointId);
            Assert.Equal(MergedPid, mergedChild.VesselPersistentId);
            Assert.Equal(12345.0, mergedChild.ExplicitStartUT);
            // This pins the BuildMergeBranchData DEFAULT-parameter fallback (None ->
            // DockingPort for a route-eligible partner). Since the claw producer, the LIVE
            // CreateMergeBranch path passes the CLASSIFIED kind explicitly
            // (ConnectionProducerClassifier stamps Grapple for a claw grab); the builder
            // default stays DockingPort for callers that do not classify.
            Assert.Equal(AsteroidPid, mergedChild.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, mergedChild.TransferKind);
        }

        [Fact]
        public void ClawGrab_IneligiblePartner_DoesNotInventRouteProof()
        {
            // When the asteroid partner fails BOTH route-eligibility disjuncts (no pre-couple
            // snapshot captured AND no known recording), OnPartCouple passes targetPid 0 and the
            // merge must still record as a plain Dock branch without inventing route proof.
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: new List<string> { "transport_rec" },
                treeId: "tree_claw",
                mergeUT: 500.0,
                branchType: BranchPointType.Dock,
                mergedVesselPid: MergedPid,
                mergedVesselName: "Grabber + Ast. HSJ-227");

            Assert.Equal(BranchPointType.Dock, bp.Type);
            Assert.Equal("DOCK", bp.MergeCause);
            Assert.Equal(0u, bp.TargetVesselPersistentId);
            Assert.Equal(0u, mergedChild.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, mergedChild.TransferKind);
        }

        #endregion

        #region Couple-event partner resolution for an asteroid

        [Fact]
        public void ClawGrab_ResolveDockPartnerPidFromEvent_ReturnsAsteroidFromEitherSide()
        {
            // KSP fires onPartCouple(from: grapple part, to: asteroid part) or the reverse
            // depending on which side initiated/survived; the partner resolver must return the
            // asteroid whichever side it is on.
            Assert.Equal(AsteroidPid, ParsekFlight.ResolveDockPartnerPidFromEvent(
                fromVesselPid: AsteroidPid, toVesselPid: TransportPid, selfVesselPid: TransportPid));
            Assert.Equal(AsteroidPid, ParsekFlight.ResolveDockPartnerPidFromEvent(
                fromVesselPid: TransportPid, toVesselPid: AsteroidPid, selfVesselPid: TransportPid));
        }

        [Fact]
        public void ClawGrab_FirstGrabAsteroid_IsNotAKnownRoutePartner()
        {
            // A freshly-grabbed asteroid has no Parsek recording anywhere, so the KNOWN-recording
            // disjunct votes no. (The live path still accepts the partner when the pre-couple
            // snapshot disjunct captured a real vessel - the honest contract is that first-grab
            // route eligibility depends on that snapshot, never on a phantom recording.)
            var treeRecordings = new List<Recording>
            {
                new Recording { RecordingId = "transport_rec", VesselPersistentId = TransportPid }
            };
            var committed = new List<Recording>();

            Assert.False(ParsekFlight.IsKnownDockPartnerForRoute(
                AsteroidPid, TransportPid, treeRecordings, committed));

            // A RE-grab of an asteroid that got its own recording in a prior tree IS known.
            committed.Add(new Recording { RecordingId = "asteroid_rec", VesselPersistentId = AsteroidPid });
            Assert.True(ParsekFlight.IsKnownDockPartnerForRoute(
                AsteroidPid, TransportPid, treeRecordings, committed));
        }

        #endregion

        #region Breakup scan stays out of the claw grab's way

        [Fact]
        public void RawAsteroid_RejectedFromDeferredBreakupScan_AsSpaceObject()
        {
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.SpaceObject,
                new[] { "PotatoRoid" },
                new[] { "ModuleAsteroid" },
                out string reason);

            Assert.True(rejected);
            Assert.Equal("space-object-type", reason);
        }

        [Fact]
        public void MergedClawShip_RejectedFromDeferredBreakupScan_ByAsteroidPart()
        {
            // The post-grab merged vessel is a SHIP (not a SpaceObject) that now carries the
            // PotatoRoid part + ModuleAsteroid module. The deferred breakup scan must reject it
            // so the OnPartCouple dock-merge path stays the sole recording authority for the
            // grab - the scan never misreads the fresh merged pid as a breakup child.
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.Ship,
                new[] { "mk1pod.v2", "GrapplingDevice", "PotatoRoid" },
                new[] { "ModuleCommand", "ModuleGrappleNode", "ModuleAsteroid" },
                out string reason);

            Assert.True(rejected);
            Assert.Equal("asteroid-comet-part", reason);
        }

        #endregion

        #region Snapshot part-name path for PotatoRoid

        [Fact]
        public void TryExtractPartName_PotatoRoid_SurvivesWithAndWithoutUidSuffix()
        {
            // "PotatoRoid" carries no underscore, so cfg and runtime names coincide and the
            // underscore->dot conversion must be a no-op; a snapshot-style trailing numeric uid
            // suffix is stripped. (The PartLoader resolution half is in-game-only and covered by
            // ClawCoupleInGameTest.)
            Assert.Equal("PotatoRoid", GhostVisualBuilder.TryExtractPartName("PotatoRoid"));
            Assert.Equal("PotatoRoid", GhostVisualBuilder.TryExtractPartName("PotatoRoid_4289156007"));
            Assert.Equal("GrapplingDevice", GhostVisualBuilder.TryExtractPartName("GrapplingDevice"));
        }

        [Fact]
        public void ClawedAsteroidShip_Snapshot_HasCoupledPotatoRoidAndResolvableRoot()
        {
            ConfigNode snapshot = VesselSnapshotBuilder
                .ClawedAsteroidShip("Grabber + Ast. HSJ-227", "Jebediah Kerman", MergedPid)
                .AsOrbiting(sma: 700000, ecc: 0.01, inc: 5.0)
                .Build();

            ConfigNode[] parts = snapshot.GetNodes("PART");
            Assert.Equal(3, parts.Length);
            Assert.Equal("mk1pod.v2", parts[0].GetValue("name"));
            Assert.Equal("GrapplingDevice", parts[1].GetValue("name"));
            Assert.Equal("PotatoRoid", parts[2].GetValue("name"));
            // The asteroid is coupled THROUGH the claw (parent index 1), mirroring the post-grab
            // part tree, and carries the generator's deterministic pid scheme (100000 + idx*1111).
            Assert.Equal("1", parts[2].GetValue("parent"));
            Assert.Equal("102222", parts[2].GetValue("persistentId"));

            // The snapshot's root part resolves through the same root-info path ghost building
            // uses (root index 0 -> the pod, pid 100000).
            bool ok = GhostVisualBuilder.TryGetSnapshotRootPartInfo(
                snapshot, out string rootName, out uint rootPid, out _, out _);
            Assert.True(ok);
            Assert.Equal("mk1pod.v2", rootName);
            Assert.Equal(100000u, rootPid);

            // Each coupled part's snapshot name survives the extraction step of the ghost
            // part-name path unchanged (no suffix, no underscore rewriting needed).
            foreach (ConfigNode part in parts)
            {
                string raw = part.GetValue("name");
                Assert.Equal(raw, GhostVisualBuilder.TryExtractPartName(raw));
            }
        }

        #endregion
    }
}
