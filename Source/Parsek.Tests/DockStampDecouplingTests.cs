using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the branch-point partner stamp decoupling (design-dock-event-graph.md 6.1):
    /// BranchPoint.TargetVesselPersistentId carries the UNGATED couple-event partner
    /// identity while every ROUTE surface (TransferTargetVesselPid, TransferKind, the
    /// route proof window) keeps reading the route-eligibility-GATED pid. Each test
    /// names the regression it catches.
    ///
    /// <para>
    /// The absorbed-vessel spawn suppression is NOT a route surface and moved onto the
    /// stamp when PHANTOM-SUPERSEDE-RIDES-GATED-PID was fixed: it asks "who did this
    /// dock absorb?", which is a partner-identity question. Its cells are at the bottom
    /// of this file, next to the stamp they consume.
    /// </para>
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
                partnerPidFromEvent: 777u, selfVesselPid: 42u, partnerIsEva: false));
        }

        [Fact]
        public void StampPid_ZeroPartner_ReturnsZero()
        {
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 0u, selfVesselPid: 42u, partnerIsEva: false));
        }

        [Fact]
        public void StampPid_SelfPartner_ReturnsZero()
        {
            // Defense-in-depth: ResolveDockPartnerPidFromEvent already excludes self, so
            // this input is production-unreachable today; the stamp's own self-filter
            // keeps a future caller from stamping a vessel as its own dock partner.
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 42u, selfVesselPid: 42u, partnerIsEva: false));
        }

        [Fact]
        public void StampPid_EvaGrab_Suppressed()
        {
            // Q2 decision: EVA grabs never stamp a dock partner (an EVA kerbal grab is
            // a kerbal-scale event; stamping it would mint ghost-chain claims keyed on
            // kerbal pids). Fails if the EVA suppression is dropped from the stamp path.
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 777u, selfVesselPid: 42u, partnerIsEva: true));
        }

        [Fact]
        public void StampPid_EvaRecorderTakesExternalSeat_StampsTheSeatVessel()
        {
            // GC-11 FOREIGN-SEAT-BOARD-LEAVES-NO-GHOST-CLAIM: KerbalEVA On_seatBoard runs
            // part.Couple(kerbalSeat.part), so from = the recorder's own EVA kerbal (42) and
            // to = the rover (777). The partner is not EVA, so the rover is stamped and the
            // chain walker / rewind hold see the claim. Fails if the suppression keys on
            // "either side is EVA" again (it then reads partnerIsEva true and stamps 0).
            bool partnerIsEva = ConnectionProducerClassifier.PartnerIsEvaCore(
                partnerPid: 777u, fromVesselPid: 42u, fromIsEva: true,
                toVesselPid: 777u, toIsEva: false);
            Assert.False(partnerIsEva);
            Assert.Equal(777u, ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 777u, selfVesselPid: 42u, partnerIsEva: partnerIsEva));
            // The guid stamp follows the pid stamp: the rover's live launch guid.
            Assert.Equal("feedfacefeedfacefeedfacefeedface",
                ParsekFlight.ResolveDockPartnerGuidFromEvent(777u,
                    42u, "42424242424242424242424242424242",
                    777u, "feedfacefeedfacefeedfacefeedface"));
        }

        [Fact]
        public void StampPid_ClawGrabsEvaKerbal_StillSuppressed_EitherSide()
        {
            // Mirror of the seat cell: a non-EVA recorder (42) whose partner is the EVA
            // kerbal (909) stays unstamped whichever couple side the kerbal is on (a claw
            // couple's from side depends on Vessel.GetDominantVessel).
            bool kerbalFrom = ConnectionProducerClassifier.PartnerIsEvaCore(
                partnerPid: 909u, fromVesselPid: 909u, fromIsEva: true,
                toVesselPid: 42u, toIsEva: false);
            bool kerbalTo = ConnectionProducerClassifier.PartnerIsEvaCore(
                partnerPid: 909u, fromVesselPid: 42u, fromIsEva: false,
                toVesselPid: 909u, toIsEva: true);
            Assert.True(kerbalFrom);
            Assert.True(kerbalTo);
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(909u, 42u, kerbalFrom));
            Assert.Equal(0u, ParsekFlight.ResolveBranchPartnerStampPid(909u, 42u, kerbalTo));
        }

        [Fact]
        public void PartnerIsEvaCore_ZeroOrUnmatchedPartner_IsNotEva()
        {
            Assert.False(ConnectionProducerClassifier.PartnerIsEvaCore(0u, 0u, true, 42u, true));
            Assert.False(ConnectionProducerClassifier.PartnerIsEvaCore(555u, 42u, true, 777u, true));
            // Already reparented (both sides one vessel): reads that vessel's own flag.
            Assert.False(ConnectionProducerClassifier.PartnerIsEvaCore(777u, 777u, false, 777u, false));
        }

        [Fact]
        public void Wiring_BothCouplePathsStampWithThePartnerSideFlag()
        {
            // Both OnPartCouple paths must feed the stamp the partner-side flag, never the
            // either-side coupleInvolvesEva the route window keys on.
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "ParsekFlight.cs"));
            Assert.DoesNotContain("RecordingVesselId, coupleInvolvesEva)", src);
            Assert.Contains("partnerPidFromEvent, recorder.RecordingVesselId, partnerIsEva);", src);
            Assert.Contains("partnerPidFromEventR, recorder.RecordingVesselId, partnerIsEvaR);", src);
            Assert.Contains("data.from, data.to, partnerPidFromEvent);", src);
            Assert.Contains("data.from, data.to, partnerPidFromEventR);", src);
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
        public void Build_DivergentPids_PartnerWinsTheStamp()
        {
            // Pins the fallback's precedence rule: when both pids are nonzero and differ
            // (production provably cannot produce this today - the ungated pid equals the
            // gated one whenever the gated one is nonzero - but the rule is a real line of
            // BuildMergeBranchData), the branch stamp takes the partner pid and the route
            // surfaces take the gated pid. Fails if the precedence flips, which would
            // silently re-couple the stamp to route eligibility for any future caller.
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "p1" }, "tree", 3000.0, BranchPointType.Dock,
                mergedVesselPid: 60, mergedVesselName: "Merged",
                targetVesselPersistentId: 999u,
                transferKind: RouteConnectionKind.DockingPort,
                branchPartnerPid: 777u);

            Assert.Equal(777u, bp.TargetVesselPersistentId);
            Assert.Equal(999u, child.TransferTargetVesselPid);
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

        // -------- PHANTOM-SUPERSEDE-RIDES-GATED-PID --------
        // ResolveDockMergeSpawnSuppressionPid: the absorbed-vessel spawn suppression
        // consumes the UNGATED stamp, not the gated route pid.

        [Fact]
        public void SpawnSuppression_RouteIneligibleDock_StillSuppresses()
        {
            // THE FINDING. Route eligibility failed (gated pid 0) but the dock still
            // physically absorbed a Parsek-recorded vessel, so its committed terminal
            // spawn must still be suppressed or KSCSpawn re-materialises a phantom at
            // the runway. Fails if the suppression is re-wired to the gated pid.
            var (bp, _) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "rec_a" }, "tree", 1000.0, BranchPointType.Dock,
                mergedVesselPid: 100, mergedVesselName: "Merged",
                targetVesselPersistentId: 0u,       // route-INELIGIBLE
                branchPartnerPid: 909090u);         // but the partner IS known

            Assert.Equal(909090u, bp.TargetVesselPersistentId);
            Assert.Equal(909090u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                BranchPointType.Dock, bp.TargetVesselPersistentId, mergedVesselPid: 100u));
        }

        [Fact]
        public void SpawnSuppression_LegacyCallerWithoutStamp_FallsBackToRoutePid()
        {
            // BuildMergeBranchData falls the stamp back to the gated route pid when a
            // caller supplies no partner, so the suppression keeps its pre-decoupling
            // reach for those call sites rather than silently going quiet.
            var (bp, _) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "rec_a" }, "tree", 1000.0, BranchPointType.Dock,
                mergedVesselPid: 100, mergedVesselName: "Merged",
                targetVesselPersistentId: 777u,
                branchPartnerPid: 0u);              // legacy caller: no stamp

            Assert.Equal(777u, bp.TargetVesselPersistentId);
            Assert.Equal(777u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                BranchPointType.Dock, bp.TargetVesselPersistentId, mergedVesselPid: 100u));
        }

        [Fact]
        public void SpawnSuppression_PartnerSurvivedAsMergedVessel_ReturnsZero()
        {
            // The partner's terminal spawn is owned by the live merged continuation,
            // not lost: nothing to suppress.
            Assert.Equal(0u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                BranchPointType.Dock, branchStampPid: 100u, mergedVesselPid: 100u));
        }

        [Fact]
        public void SpawnSuppression_NoPartner_ReturnsZero()
        {
            Assert.Equal(0u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                BranchPointType.Dock, branchStampPid: 0u, mergedVesselPid: 100u));
        }

        [Fact]
        public void SpawnSuppression_NonDockBranch_ReturnsZero()
        {
            // Dock-only. A Board absorbs no separate spawned / adopted committed leaf,
            // and the builder's stamp is type-agnostic — so the gate has to be here.
            foreach (var type in new[]
                     {
                         BranchPointType.Board, BranchPointType.Undock,
                         BranchPointType.Breakup, BranchPointType.Launch,
                     })
            {
                Assert.Equal(0u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                    type, branchStampPid: 909090u, mergedVesselPid: 100u));
            }
        }

        [Fact]
        public void SpawnSuppression_EvaGrab_SuppressesNothing()
        {
            // The EVA carve-out has to survive the switch from the gated pid to the
            // stamp: BOTH resolvers zero their pid for an EVA couple, so a claw / kerbal
            // grab still marks no terminal spawn superseded.
            uint evaStamp = ParsekFlight.ResolveBranchPartnerStampPid(
                partnerPidFromEvent: 909090u, selfVesselPid: 42u, partnerIsEva: true);
            uint evaRoute = ParsekFlight.SuppressRouteWindowForEvaGrab(
                909090u, involvesEva: true, RouteConnectionKind.Grapple, pathLabel: "");

            Assert.Equal(0u, evaStamp);
            Assert.Equal(0u, evaRoute);

            var (bp, _) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "rec_a" }, "tree", 1000.0, BranchPointType.Dock,
                mergedVesselPid: 100, mergedVesselName: "Merged",
                targetVesselPersistentId: evaRoute, branchPartnerPid: evaStamp);

            Assert.Equal(0u, ParsekFlight.ResolveDockMergeSpawnSuppressionPid(
                BranchPointType.Dock, bp.TargetVesselPersistentId, mergedVesselPid: 100u));
        }
    }
}
