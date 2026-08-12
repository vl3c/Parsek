using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the branch-point partner stamp decoupling (design-dock-event-graph.md 6.1):
    /// BranchPoint.TargetVesselPersistentId carries the UNGATED couple-event partner
    /// identity while every route surface (TransferTargetVesselPid, TransferKind, and by
    /// extension the route window / phantom-spawn supersede callers) keeps reading the
    /// route-eligibility-GATED pid. Each test names the regression it catches.
    /// </summary>
    public class DockStampDecouplingTests
    {
        // -------- ResolveBranchPartnerStampPid (the pure stamp decision) --------

        [Fact]
        public void StampPid_NormalDock_ReturnsPartnerPid()
        {
            // Fails if the stamp decision starts requiring route eligibility again
            // (the pre-decoupling behavior this change exists to remove).
            Assert.Equal(777u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 777u, selfVesselPid: 42u, involvesEva: false));
        }

        [Fact]
        public void StampPid_ZeroPartner_ReturnsZero()
        {
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 0u, selfVesselPid: 42u, involvesEva: false));
        }

        [Fact]
        public void StampPid_SelfPartner_ReturnsZero()
        {
            // Fails if a same-vessel couple event (partner == recorder) ever stamps
            // the vessel as its own dock partner.
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 42u, selfVesselPid: 42u, involvesEva: false));
        }

        [Fact]
        public void StampPid_EvaGrab_Suppressed()
        {
            // Q2 decision: EVA grabs never stamp a dock partner (an EVA kerbal grab is
            // a kerbal-scale event; stamping it would mint ghost-chain claims keyed on
            // kerbal pids). Fails if the EVA suppression is dropped from the stamp path.
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 777u, selfVesselPid: 42u, involvesEva: true));
        }

        // -------- BuildMergeBranchData (the stamp/route split) --------

        [Fact]
        public void Build_PartnerWithoutRouteEligibility_StampsBranchPointOnly()
        {
            // The core decoupling contract: a route-INELIGIBLE dock (gated pid 0)
            // still records who it docked with, and the stamp must NOT leak into the
            // route surfaces. Fails if TargetVesselPersistentId stays 0 (stamp lost)
            // or TransferTargetVesselPid/TransferKind pick up the partner (route proof
            // invented from an ineligible partner).
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "p1" }, "tree", 3000.0, BranchPointType.Dock,
                mergedVesselPid: 60, mergedVesselName: "Merged",
                branchPartnerPid: 777u);

            Assert.Equal(777u, bp.TargetVesselPersistentId);
            Assert.Equal(0u, child.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, child.TransferKind);
        }

        [Fact]
        public void Build_PartnerWithRouteEligibility_MatchesLegacyOutput()
        {
            // Production always passes both pids and they agree whenever the gated one
            // is nonzero; the output must be byte-identical to the pre-decoupling
            // shape. Fails if the new parameter perturbs the eligible-dock path.
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "p1" }, "tree", 3000.0, BranchPointType.Dock,
                mergedVesselPid: 60, mergedVesselName: "Merged",
                targetVesselPersistentId: 777u,
                transferKind: RouteConnectionKind.DockingPort,
                branchPartnerPid: 777u);

            Assert.Equal(777u, bp.TargetVesselPersistentId);
            Assert.Equal(777u, child.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, child.TransferKind);
        }

        [Fact]
        public void Build_LegacyCallerWithoutPartner_FallsBackToGatedPid()
        {
            // The fallback keeps every caller that predates the decoupling (and every
            // fixture builder passing only targetVesselPersistentId) producing exactly
            // the historical stamp. Fails if the fallback is removed and legacy calls
            // silently stamp 0.
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "p1", "p2" }, "tree", 3000.0, BranchPointType.Dock,
                mergedVesselPid: 60, mergedVesselName: "Merged",
                targetVesselPersistentId: 999u,
                transferKind: RouteConnectionKind.DockingPort);

            Assert.Equal(999u, bp.TargetVesselPersistentId);
            Assert.Equal(999u, child.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, child.TransferKind);
        }

        [Fact]
        public void Build_NoPidsAtAll_StampsZero()
        {
            // The Board path and any merge with no resolvable partner keep today's
            // zero stamp exactly (the design's degradation contract: old behavior for
            // an absent partner). Fails if a default sneaks a nonzero stamp in.
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "p1" }, "tree", 3000.0, BranchPointType.Dock,
                mergedVesselPid: 60, mergedVesselName: "Merged");

            Assert.Equal(0u, bp.TargetVesselPersistentId);
            Assert.Equal(0u, child.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, child.TransferKind);
        }

        [Fact]
        public void Build_BoardWithPartner_StampsBranchPointNeverRoute()
        {
            // The builder's stamp is type-agnostic but the route fields are Dock-only.
            // Production does not pass a Board partner in v1 (design Q1: deferred);
            // this pins that IF a future caller does, the route surfaces still stay
            // empty on a Board. Fails if TransferTargetVesselPid starts populating for
            // Board merges.
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "kerbal_rec", "vessel_rec" }, "tree", 7000.0,
                BranchPointType.Board,
                mergedVesselPid: 300, mergedVesselName: "Boarded",
                branchPartnerPid: 555u);

            Assert.Equal(555u, bp.TargetVesselPersistentId);
            Assert.Equal(0u, child.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, child.TransferKind);
        }
    }
}
