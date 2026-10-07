using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;
using static Parsek.Logistics.RouteEndpointPartScope;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-DELIVERY-INTO-DOCKED-VISITOR: the resolver returns the docked composite, and the
    /// delivery writers, the capacity probe and the origin debit used to walk every part of
    /// it, so a visiting tanker filled first and flew off with the cargo (or a docked visitor
    /// paid an origin debit). <see cref="RouteEndpointPartScope"/> cuts the composite at its
    /// settled stock dock seams and keeps the pieces that hold the endpoint's root part or a
    /// part the route recorded as the endpoint's.
    ///
    /// <para>Both directions are pinned: the endpoint dominating the merge (the visitor hangs
    /// below it) and the visitor dominating it (the endpoint hangs below the visitor), plus
    /// the endpoint docked INTO a larger station, a station assembled from earlier-docked
    /// modules (which must stay whole), and the fallbacks that keep today's whole-vessel
    /// behaviour.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointPartScopeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteEndpointPartScopeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixture builder: parts are added in vessel order, parents by flightID.
        // ------------------------------------------------------------------

        private sealed class Craft
        {
            internal readonly List<uint> FlightIds = new List<uint>();
            internal readonly List<uint> Pids = new List<uint>();
            internal readonly List<uint> ParentFlightIds = new List<uint>();
            internal readonly List<DockNodeSpec> Nodes = new List<DockNodeSpec>();

            internal Craft Part(uint flightId, uint pid, uint parentFlightId = 0u)
            {
                FlightIds.Add(flightId);
                Pids.Add(pid);
                ParentFlightIds.Add(parentFlightId);
                return this;
            }

            internal Craft Node(uint onFlightId, uint dockedFlightId, uint ownRoot,
                uint otherRoot = 0u, bool hasInfo = true)
            {
                Nodes.Add(new DockNodeSpec
                {
                    OnFlightId = onFlightId,
                    DockedFlightId = dockedFlightId,
                    OwnRoot = ownRoot,
                    OtherRoot = otherRoot,
                    HasInfo = hasInfo,
                });
                return this;
            }

            internal List<PartRecord> PartRecords()
            {
                var list = new List<PartRecord>();
                for (int i = 0; i < FlightIds.Count; i++)
                    list.Add(new PartRecord(FlightIds[i], Pids[i],
                        ParentFlightIds[i] == 0u ? -1 : FlightIds.IndexOf(ParentFlightIds[i])));
                return list;
            }

            internal List<DockNodeRecord> NodeRecords()
            {
                var list = new List<DockNodeRecord>();
                for (int i = 0; i < Nodes.Count; i++)
                {
                    DockNodeSpec n = Nodes[i];
                    list.Add(new DockNodeRecord(FlightIds.IndexOf(n.OnFlightId), n.DockedFlightId,
                        n.HasInfo, n.HasInfo ? n.OwnRoot : 0u, n.HasInfo ? n.OtherRoot : 0u));
                }
                return list;
            }

            internal HashSet<uint> OwnPids(bool[] mask)
            {
                var set = new HashSet<uint>();
                for (int i = 0; i < mask.Length; i++)
                    if (mask[i]) set.Add(Pids[i]);
                return set;
            }

            internal HashSet<uint> OwnFlightIds(bool[] mask)
            {
                var set = new HashSet<uint>();
                for (int i = 0; i < mask.Length; i++)
                    if (mask[i]) set.Add(FlightIds[i]);
                return set;
            }
        }

        private struct DockNodeSpec
        {
            public uint OnFlightId;
            public uint DockedFlightId;
            public uint OwnRoot;
            public uint OtherRoot;
            public bool HasInfo;
        }

        // Station S: root 100 (pid 1001), tank 101 (1002), port 102 (1003).
        private static readonly HashSet<uint> StationPids = new HashSet<uint> { 1001u, 1002u, 1003u };
        // Visitor V: original root 202 (2003), tank 201 (2002), port 200 (2001).
        private static readonly uint[] VisitorPids = { 2001u, 2002u, 2003u };

        private static Craft StationDominantWithVisitor()
        {
            // Part.Couple re-roots the absorbed craft at its port and hangs it below the
            // dominant craft's port.
            return new Craft()
                .Part(100u, 1001u)
                .Part(101u, 1002u, 100u)
                .Part(102u, 1003u, 101u)
                .Part(200u, 2001u, 102u)
                .Part(202u, 2003u, 200u)
                .Part(201u, 2002u, 202u)
                .Node(102u, 200u, ownRoot: 100u)
                .Node(200u, 102u, ownRoot: 202u);
        }

        private static Craft VisitorDominantWithStation()
        {
            return new Craft()
                .Part(202u, 2003u)
                .Part(201u, 2002u, 202u)
                .Part(200u, 2001u, 201u)
                .Part(102u, 1003u, 200u)
                .Part(101u, 1002u, 102u)
                .Part(100u, 1001u, 101u)
                .Node(102u, 200u, ownRoot: 100u)
                .Node(200u, 102u, ownRoot: 202u);
        }

        private static bool[] Select(Craft craft, uint root, ICollection<uint> recorded,
            out Outcome outcome, out int own, out int excluded)
        {
            return SelectOwnParts(craft.PartRecords(), craft.NodeRecords(), root, recorded,
                out outcome, out own, out excluded);
        }

        // ==================================================================
        // The defect: a docked visitor is part of the delivery target
        // ==================================================================

        // catches: the writers / capacity probe walking the visitor's tanks while the
        // destination dominates the merge (resolver pass 1, the common case: a Station or
        // Base outranks a Ship in Vessel.GetDominantVessel).
        [Fact]
        public void StationDominant_VisitorDocked_OnlyStationPartsAreOwn()
        {
            Craft craft = StationDominantWithVisitor();
            bool[] mask = Select(craft, 100u, StationPids, out Outcome outcome, out int own, out int excluded);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.NotNull(mask);
            Assert.Equal(StationPids, craft.OwnPids(mask));
            Assert.Equal(3, own);
            Assert.Equal(1, excluded);
        }

        // catches: the MIRROR direction - the visitor dominates, the station's root is an
        // ordinary part under the visitor's root (resolver pass 2, docked-composite arm).
        [Fact]
        public void VisitorDominant_StationAbsorbed_OnlyStationPartsAreOwn()
        {
            Craft craft = VisitorDominantWithStation();
            bool[] mask = Select(craft, 100u, StationPids, out Outcome outcome, out int own, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(StationPids, craft.OwnPids(mask));
            Assert.Equal(3, own);
        }

        // catches: the endpoint itself being the visitor - a lander endpoint docked INTO a
        // larger station that dominates; cargo must still land in the lander, not the
        // station it is parked at.
        [Fact]
        public void EndpointDockedIntoLargerStation_OnlyEndpointPartsAreOwn()
        {
            // Big station T (root 400, tank 401, port 402, another tank 403) dominant;
            // endpoint lander E (root 300, tank 301, port 302) hangs below T's port.
            Craft craft = new Craft()
                .Part(400u, 4001u)
                .Part(401u, 4002u, 400u)
                .Part(403u, 4004u, 400u)
                .Part(402u, 4003u, 401u)
                .Part(302u, 3003u, 402u)
                .Part(300u, 3001u, 302u)
                .Part(301u, 3002u, 300u)
                .Node(402u, 302u, ownRoot: 400u)
                .Node(302u, 402u, ownRoot: 300u);
            var landerPids = new HashSet<uint> { 3001u, 3002u, 3003u };

            bool[] mask = Select(craft, 300u, landerPids, out Outcome outcome, out int own, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(landerPids, craft.OwnPids(mask));
            Assert.Equal(3, own);
        }

        // ==================================================================
        // A station assembled from docked modules stays whole
        // ==================================================================

        // Core C: root 100, port 102, port 103. Module M docked before the route was
        // recorded: port 110 (under 102), root 111, tank 112. Visitor V at port 103.
        private static Craft ModularStation(bool withVisitor)
        {
            Craft craft = new Craft()
                .Part(100u, 1001u)
                .Part(102u, 1003u, 100u)
                .Part(103u, 1004u, 100u)
                .Part(110u, 1101u, 102u)
                .Part(111u, 1102u, 110u)
                .Part(112u, 1103u, 111u)
                .Node(102u, 110u, ownRoot: 100u)
                .Node(110u, 102u, ownRoot: 111u);
            if (withVisitor)
            {
                craft.Part(200u, 2001u, 103u)
                    .Part(202u, 2003u, 200u)
                    .Part(201u, 2002u, 202u)
                    .Node(103u, 200u, ownRoot: 100u)
                    .Node(200u, 103u, ownRoot: 202u);
            }
            return craft;
        }

        private static readonly HashSet<uint> ModularStationPids =
            new HashSet<uint> { 1001u, 1003u, 1004u, 1101u, 1102u, 1103u };

        // catches: a cut-every-seam rule that would keep only the core and leave a fuel
        // module docked before the route was recorded without deliveries (a route that
        // works today would hold DestinationFull forever).
        [Fact]
        public void ModularStation_NoVisitor_StaysWhole()
        {
            Craft craft = ModularStation(withVisitor: false);
            bool[] mask = Select(craft, 100u, ModularStationPids, out Outcome outcome, out int own, out int excluded);

            Assert.Null(mask);
            Assert.Equal(Outcome.AllOwn, outcome);
            Assert.Equal(6, own);
            Assert.Equal(0, excluded);
        }

        // catches: the visitor excluded but the earlier module dropped with it.
        [Fact]
        public void ModularStation_WithVisitor_KeepsModuleDropsVisitor()
        {
            Craft craft = ModularStation(withVisitor: true);
            bool[] mask = Select(craft, 100u, ModularStationPids, out Outcome outcome, out int own, out int excluded);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(ModularStationPids, craft.OwnPids(mask));
            Assert.Equal(6, own);
            Assert.Equal(1, excluded);
        }

        // Pins the conservative reading: a module docked AFTER the route was recorded is not
        // in the recorded part set and is left out with the visitors.
        [Fact]
        public void ModuleDockedAfterRecording_IsExcluded()
        {
            Craft craft = ModularStation(withVisitor: false);
            var coreOnly = new HashSet<uint> { 1001u, 1003u, 1004u };
            bool[] mask = Select(craft, 100u, coreOnly, out Outcome outcome, out int own, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(coreOnly, craft.OwnPids(mask));
            Assert.Equal(3, own);
        }

        // ==================================================================
        // Adopted part set (owner ruling 2026-10-07): the player re-captures the
        // endpoint's current parts, so a module docked after the recording is admitted
        // ==================================================================

        // The station's parts by flightID while nothing but its own module was docked (the
        // moment the adoption was taken): core 100 / 102 / 103 plus module 110 / 111 / 112.
        private static readonly HashSet<uint> CoreAndModuleFlightIds =
            new HashSet<uint> { 100u, 102u, 103u, 110u, 111u, 112u };
        private static readonly HashSet<uint> CoreOnlyPids = new HashSet<uint> { 1001u, 1003u, 1004u };

        private static bool[] SelectWithAdoption(Craft craft, uint root, ICollection<uint> recorded,
            ICollection<uint> adoptedFlightIds, out Outcome outcome, out int own, out int excluded)
        {
            return SelectOwnParts(craft.PartRecords(), craft.NodeRecords(), root, recorded,
                adoptedFlightIds, out outcome, out own, out excluded);
        }

        // catches: the adoption not reaching the scope (the module docked after the recording
        // stays excluded, the station reads full sooner) or admitting everything (a visitor
        // docked AFTER the adoption fills with the cargo again).
        [Fact]
        public void AdoptedSet_AdmitsModuleDockedAfterRecording_ExcludesVisitorDockedAfterAdoption()
        {
            Craft craft = ModularStation(withVisitor: true);
            bool[] mask = SelectWithAdoption(craft, 100u, CoreOnlyPids, CoreAndModuleFlightIds,
                out Outcome outcome, out int own, out int excluded);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.NotNull(mask);
            Assert.Equal(ModularStationPids, craft.OwnPids(mask));
            Assert.Equal(6, own);
            Assert.Equal(1, excluded);
        }

        // catches: the same station without a visitor still being cut to the recorded core.
        [Fact]
        public void AdoptedSet_NoVisitor_StationWithLaterModuleStaysWhole()
        {
            Craft craft = ModularStation(withVisitor: false);
            bool[] mask = SelectWithAdoption(craft, 100u, CoreOnlyPids, CoreAndModuleFlightIds,
                out Outcome outcome, out int own, out int excluded);

            Assert.Null(mask);
            Assert.Equal(Outcome.AllOwn, outcome);
            Assert.Equal(6, own);
            Assert.Equal(0, excluded);
        }

        // catches: an absent (or empty) adoption changing the recorded-set answer in any way.
        [Fact]
        public void NoAdoptedSet_IsExactlyTheRecordedSetBehaviour()
        {
            var cases = new[]
            {
                new { Craft = ModularStation(withVisitor: true), Recorded = CoreOnlyPids },
                new { Craft = ModularStation(withVisitor: true), Recorded = ModularStationPids },
                new { Craft = ModularStation(withVisitor: false), Recorded = CoreOnlyPids },
                new { Craft = StationDominantWithVisitor(), Recorded = StationPids },
                new { Craft = StationDominantWithVisitor(), Recorded = (HashSet<uint>)null },
            };
            foreach (var c in cases)
            {
                bool[] baseline = Select(c.Craft, 100u, c.Recorded,
                    out Outcome baseOutcome, out int baseOwn, out int baseExcluded);
                foreach (ICollection<uint> none in new ICollection<uint>[] { null, new HashSet<uint>() })
                {
                    bool[] mask = SelectWithAdoption(c.Craft, 100u, c.Recorded, none,
                        out Outcome outcome, out int own, out int excluded);
                    Assert.Equal(baseOutcome, outcome);
                    Assert.Equal(baseOwn, own);
                    Assert.Equal(baseExcluded, excluded);
                    Assert.Equal(baseline, mask);
                }
            }
        }

        // catches: matching the adoption by persistentId. A later launch of the module's own
        // .craft carries the same baked part persistentIds (KSP reuses them once the original
        // is gone); only the per-launch flightIDs tell it apart, so it must stay a visitor.
        [Fact]
        public void AdoptedSet_MatchesFlightIds_NotCraftBakedPersistentIds()
        {
            Craft craft = ModularStation(withVisitor: false)
                .Part(210u, 1101u, 103u)
                .Part(211u, 1102u, 210u)
                .Part(212u, 1103u, 211u)
                .Node(103u, 210u, ownRoot: 100u)
                .Node(210u, 103u, ownRoot: 211u);

            bool[] mask = SelectWithAdoption(craft, 100u, CoreOnlyPids, CoreAndModuleFlightIds,
                out Outcome outcome, out int own, out int excluded);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(CoreAndModuleFlightIds, craft.OwnFlightIds(mask));
            Assert.Equal(6, own);
            Assert.Equal(1, excluded);
        }

        // catches: the adoption being unioned with the recorded set instead of replacing it:
        // the recorded set still names the module, the adoption (taken while the module was
        // away) does not, so the re-docked module is not the endpoint's.
        [Fact]
        public void AdoptedSet_ReplacesTheRecordedSet()
        {
            Craft craft = ModularStation(withVisitor: false);
            var coreFlightIds = new HashSet<uint> { 100u, 102u, 103u };

            bool[] mask = SelectWithAdoption(craft, 100u, ModularStationPids, coreFlightIds,
                out Outcome outcome, out int own, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(coreFlightIds, craft.OwnFlightIds(mask));
            Assert.Equal(3, own);
        }

        // catches: an empty mask when neither the root nor any adopted part is aboard; the
        // whole-composite fallback stands, as for the recorded set.
        [Fact]
        public void AdoptedSet_NothingAboard_FallsBackToWholeComposite()
        {
            Craft craft = StationDominantWithVisitor();
            bool[] mask = SelectWithAdoption(craft, 999u, StationPids, new HashSet<uint> { 900u, 901u },
                out Outcome outcome, out _, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.EndpointNotAboard, outcome);
        }

        // catches: the recordings being consulted (or winning) when the route carries an
        // adoption for this endpoint, and the adoption leaking onto another endpoint.
        [Fact]
        public void OwnPartSets_AdoptionConsultedBeforeTheRecordings()
        {
            var rec = new Recording
            {
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        EndpointRootPartUId = 900u,
                        EndpointPartPersistentIds = new List<uint> { 9001u },
                    },
                },
            };
            var route = new Route
            {
                Id = "route-adopt",
                AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
                {
                    new RouteEndpointAdoptedParts
                    {
                        EndpointRootPartUId = 100u,
                        EndpointVesselPersistentId = 55u,
                        PartFlightIds = new HashSet<uint>(CoreAndModuleFlightIds),
                    },
                },
            };
            int sourceReads = 0;
            Func<IEnumerable<Recording>> sources = () =>
            {
                sourceReads++;
                return new[] { rec };
            };

            ResolveOwnPartSets(route, Endpoint(100u, 55u), sources,
                out HashSet<uint> adopted, out HashSet<uint> recorded, out string from);
            Assert.Equal("adopted", from);
            Assert.Equal(CoreAndModuleFlightIds, adopted);
            Assert.Null(recorded);
            Assert.Equal(0, sourceReads);

            ResolveOwnPartSets(route, Endpoint(900u), sources, out adopted, out recorded, out from);
            Assert.Null(adopted);
            Assert.Equal(new HashSet<uint> { 9001u }, recorded);
            Assert.Equal("window", from);
            Assert.Equal(1, sourceReads);

            ResolveOwnPartSets(null, Endpoint(100u), sources, out adopted, out recorded, out from);
            Assert.Null(adopted);
        }

        // ==================================================================
        // Unchanged and fallback paths
        // ==================================================================

        // catches: an undocked endpoint being scoped at all (the common path must stay
        // byte-identical to today: every part).
        [Fact]
        public void UndockedEndpoint_NoSeam_EveryPart()
        {
            Craft craft = new Craft()
                .Part(100u, 1001u)
                .Part(101u, 1002u, 100u)
                .Part(102u, 1003u, 101u);
            bool[] mask = Select(craft, 100u, StationPids, out Outcome outcome, out int own, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.NotDocked, outcome);
            Assert.Equal(3, own);
        }

        // catches: guessing without a recorded part set. Stock's records cannot tell a
        // pre-route module from a post-route visitor, so the whole composite is kept.
        [Fact]
        public void NoRecordedPartSet_FallsBackToWholeComposite()
        {
            Craft craft = StationDominantWithVisitor();
            bool[] mask = Select(craft, 100u, null, out Outcome outcome, out _, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.NoRecordedParts, outcome);

            mask = Select(craft, 100u, new HashSet<uint>(), out outcome, out _, out _);
            Assert.Null(mask);
            Assert.Equal(Outcome.NoRecordedParts, outcome);
        }

        // catches: an empty mask (nothing writable) when the recorded endpoint is not
        // aboard - e.g. a stale recorded set after a proximity rebind kept the old root.
        [Fact]
        public void RecordedPartsNotAboard_FallsBackToWholeComposite()
        {
            Craft craft = StationDominantWithVisitor();
            var elsewhere = new HashSet<uint> { 7001u, 7002u };
            bool[] mask = Select(craft, 700u, elsewhere, out Outcome outcome, out _, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.EndpointNotAboard, outcome);
        }

        // A route captured before endpoints carried a root id still scopes by its recorded
        // parts.
        [Fact]
        public void UnknownRoot_RecordedPartsStillScope()
        {
            Craft craft = StationDominantWithVisitor();
            bool[] mask = Select(craft, 0u, StationPids, out Outcome outcome, out _, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(StationPids, craft.OwnPids(mask));
        }

        // catches: a node that kept stale vesselInfo after an undock (or names a part that
        // is not its parent or child) cutting the vessel.
        [Fact]
        public void StaleNode_NotAParentChildEdge_IsNotASeam()
        {
            Craft craft = new Craft()
                .Part(100u, 1001u)
                .Part(101u, 1002u, 100u)
                .Part(102u, 1003u, 101u)
                .Node(102u, 100u, ownRoot: 100u);
            bool[] mask = Select(craft, 100u, StationPids, out Outcome outcome, out _, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.NotDocked, outcome);
        }

        // catches: an editor pre-attached port pair or a same-vessel dock (no stock
        // vesselInfo) being treated as a cross-vessel seam.
        [Fact]
        public void NodeWithoutVesselInfo_IsNotASeam()
        {
            Craft craft = new Craft()
                .Part(100u, 1001u)
                .Part(102u, 1003u, 100u)
                .Part(200u, 2001u, 102u)
                .Node(102u, 200u, ownRoot: 0u, hasInfo: false)
                .Node(200u, 102u, ownRoot: 0u, hasInfo: false);
            bool[] mask = Select(craft, 100u, new HashSet<uint> { 1001u, 1003u },
                out Outcome outcome, out _, out _);

            Assert.Null(mask);
            Assert.Equal(Outcome.NotDocked, outcome);
        }

        // catches: a craft held by the station's claw (ModuleGrappleNode keeps both halves'
        // info on the one claw node) receiving the cargo.
        [Fact]
        public void GrappledVisitor_IsExcluded()
        {
            Craft craft = new Craft()
                .Part(100u, 1001u)
                .Part(101u, 1002u, 100u)
                .Part(104u, 1005u, 101u)
                .Part(205u, 2005u, 104u)
                .Part(206u, 2006u, 205u)
                .Node(104u, 205u, ownRoot: 100u, otherRoot: 206u);
            var stationWithClaw = new HashSet<uint> { 1001u, 1002u, 1005u };
            bool[] mask = Select(craft, 100u, stationWithClaw, out Outcome outcome, out _, out _);

            Assert.Equal(Outcome.Scoped, outcome);
            Assert.Equal(stationWithClaw, craft.OwnPids(mask));
        }

        [Fact]
        public void CollectSettledSeamEdges_TwoPortsOneDock_OneEdgeWithBothRoots()
        {
            Craft craft = StationDominantWithVisitor();
            List<SeamEdge> edges = CollectSettledSeamEdges(craft.PartRecords(), craft.NodeRecords());

            Assert.Single(edges);
            Assert.Equal(craft.FlightIds.IndexOf(200u), edges[0].ChildIndex);
            Assert.Equal(craft.FlightIds.IndexOf(102u), edges[0].ParentIndex);
            Assert.Equal(202u, edges[0].ChildSideRootUId);
            Assert.Equal(100u, edges[0].ParentSideRootUId);
        }

        // ==================================================================
        // The start-docked origin's recorded parts: the pair seam split
        // ==================================================================

        // Depot D: root 500, tank 501, port 502; module DM docked earlier (port 510 under
        // 501, root 511, tank 512). Transport T docked at 502: port 600, root 601, tank 602.
        private static Craft StartDockedSnapshot()
        {
            return new Craft()
                .Part(500u, 5001u)
                .Part(501u, 5002u, 500u)
                .Part(502u, 5003u, 500u)
                .Part(510u, 5101u, 501u)
                .Part(511u, 5102u, 510u)
                .Part(512u, 5103u, 511u)
                .Part(600u, 6001u, 502u)
                .Part(601u, 6002u, 600u)
                .Part(602u, 6003u, 601u)
                .Node(501u, 510u, ownRoot: 500u)
                .Node(510u, 501u, ownRoot: 511u)
                .Node(502u, 600u, ownRoot: 500u)
                .Node(600u, 502u, ownRoot: 601u);
        }

        // catches: the depot's earlier module being cut off (only the pair seam is cut) or
        // the transport being counted as depot.
        [Fact]
        public void PairSeamSplit_DepotSideKeepsModuleDropsTransport()
        {
            Craft craft = StartDockedSnapshot();
            bool ok = TrySplitOwnSideAtPairSeam(craft.PartRecords(), craft.NodeRecords(),
                500u, 601u, out HashSet<uint> depot);

            Assert.True(ok);
            Assert.Equal(new HashSet<uint> { 5001u, 5002u, 5003u, 5101u, 5102u, 5103u }, depot);
        }

        [Fact]
        public void PairSeamSplit_NoSeamNamingThePair_FailsClosed()
        {
            Craft craft = StartDockedSnapshot();
            Assert.False(TrySplitOwnSideAtPairSeam(craft.PartRecords(), craft.NodeRecords(),
                500u, 999u, out HashSet<uint> pids));
            Assert.Null(pids);
            Assert.False(TrySplitOwnSideAtPairSeam(craft.PartRecords(), craft.NodeRecords(),
                0u, 601u, out pids));
            Assert.False(TrySplitOwnSideAtPairSeam(craft.PartRecords(), craft.NodeRecords(),
                601u, 601u, out pids));
        }

        // ==================================================================
        // Snapshot / proto module reading
        // ==================================================================

        private static ConfigNode PartNode(uint uid, uint pid, int parent)
        {
            var p = new ConfigNode("PART");
            p.AddValue("name", "part" + uid.ToString(CultureInfo.InvariantCulture));
            p.AddValue("uid", uid.ToString(CultureInfo.InvariantCulture));
            p.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            p.AddValue("parent", parent.ToString(CultureInfo.InvariantCulture));
            return p;
        }

        private static void AddDockModule(ConfigNode part, string moduleName, uint dockUId,
            uint ownRoot, uint otherRoot = 0u)
        {
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", moduleName);
            m.AddValue("dockUId", dockUId.ToString(CultureInfo.InvariantCulture));
            if (ownRoot != 0u)
                m.AddNode("DOCKEDVESSEL").AddValue("rootUId", ownRoot.ToString(CultureInfo.InvariantCulture));
            if (otherRoot != 0u)
                m.AddNode("DOCKEDVESSEL_Other").AddValue("rootUId", otherRoot.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The <see cref="StartDockedSnapshot"/> craft as a VESSEL node, the way a
        /// recording's start snapshot stores it (the root names itself as parent).</summary>
        private static ConfigNode StartDockedVesselNode()
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("root", "0");
            ConfigNode p500 = PartNode(500u, 5001u, 0);
            ConfigNode p501 = PartNode(501u, 5002u, 0);
            ConfigNode p502 = PartNode(502u, 5003u, 0);
            ConfigNode p510 = PartNode(510u, 5101u, 1);
            ConfigNode p511 = PartNode(511u, 5102u, 3);
            ConfigNode p512 = PartNode(512u, 5103u, 4);
            ConfigNode p600 = PartNode(600u, 6001u, 2);
            ConfigNode p601 = PartNode(601u, 6002u, 6);
            ConfigNode p602 = PartNode(602u, 6003u, 7);
            AddDockModule(p501, DockingNodeModuleName, 510u, 500u);
            AddDockModule(p510, DockingNodeModuleName, 501u, 511u);
            AddDockModule(p502, DockingNodeModuleName, 600u, 500u);
            AddDockModule(p600, DockingNodeModuleName, 502u, 601u);
            foreach (ConfigNode p in new[] { p500, p501, p502, p510, p511, p512, p600, p601, p602 })
                v.AddNode(p);
            return v;
        }

        [Fact]
        public void VesselNode_ReadsPartsParentsAndDockNodes()
        {
            Assert.True(TryBuildRecordsFromVesselNode(StartDockedVesselNode(),
                out List<PartRecord> parts, out List<DockNodeRecord> nodes));

            Assert.Equal(9, parts.Count);
            Assert.Equal(-1, parts[0].ParentIndex); // root names itself as parent
            Assert.Equal(0, parts[1].ParentIndex);
            Assert.Equal(510u, parts[3].FlightId);
            Assert.Equal(5101u, parts[3].PersistentId);
            Assert.Equal(4, nodes.Count);
            // Nodes come in part order: 501, 502, 510, 600.
            Assert.Equal(2, nodes[1].PartIndex);
            Assert.Equal(600u, nodes[1].DockedPartUId);
            Assert.True(nodes[1].HasVesselInfo);
            Assert.Equal(500u, nodes[1].OwnSideRootUId);
        }

        [Fact]
        public void TryReadDockNode_GrappleReadsBothHalves_OtherModulesIgnored()
        {
            var part = new ConfigNode("PART");
            AddDockModule(part, GrappleNodeModuleName, 205u, 100u, 206u);
            ConfigNode claw = part.GetNode("MODULE");
            Assert.True(TryReadDockNode(GrappleNodeModuleName, claw, 3, out DockNodeRecord node));
            Assert.Equal(3, node.PartIndex);
            Assert.Equal(205u, node.DockedPartUId);
            Assert.Equal(100u, node.OwnSideRootUId);
            Assert.Equal(206u, node.OtherSideRootUId);

            var tank = new ConfigNode("MODULE");
            tank.AddValue("name", "ModuleFuelTank");
            Assert.False(TryReadDockNode("ModuleFuelTank", tank, 0, out _));

            // A docking port that never docked (no DOCKEDVESSEL) reads, but carries no info.
            var idle = new ConfigNode("MODULE");
            idle.AddValue("name", DockingNodeModuleName);
            idle.AddValue("dockUId", "0");
            Assert.True(TryReadDockNode(DockingNodeModuleName, idle, 0, out DockNodeRecord idleNode));
            Assert.False(idleNode.HasVesselInfo);
        }

        // ==================================================================
        // The recorded part set, from the route's source recordings
        // ==================================================================

        private static RouteEndpoint Endpoint(uint root, uint pid = 0u)
        {
            return new RouteEndpoint { RootPartUId = root, VesselPersistentId = pid, BodyName = "Kerbin" };
        }

        [Fact]
        public void RecordedParts_FromWindowMatchingEndpointRoot()
        {
            var rec = new Recording
            {
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        EndpointRootPartUId = 100u,
                        EndpointPartPersistentIds = new List<uint> { 1001u, 1002u, 1003u },
                    },
                    new RouteConnectionWindow
                    {
                        // A different physical endpoint (another stop): ignored.
                        EndpointRootPartUId = 900u,
                        EndpointPartPersistentIds = new List<uint> { 9001u },
                    },
                },
            };

            HashSet<uint> pids = CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(100u), out string source);

            Assert.Equal(StationPids, pids);
            Assert.Equal("window", source);
        }

        // The window's own endpoint descriptor carries the root when the window-level field
        // was not readable.
        [Fact]
        public void RecordedParts_FromWindowEndpointDescriptorRoot()
        {
            var rec = new Recording
            {
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        EndpointAtDock = Endpoint(100u, 55u),
                        EndpointPartPersistentIds = new List<uint> { 1001u },
                    },
                },
            };

            HashSet<uint> pids = CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(100u), out _);

            Assert.Equal(new HashSet<uint> { 1001u }, pids);
        }

        // catches: matching a window by a craft-baked pid when the endpoint HAS a root id
        // (the pid is the fallback only for an endpoint with no root).
        [Fact]
        public void RecordedParts_PidMatchOnlyWhenEndpointHasNoRoot()
        {
            var rec = new Recording
            {
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        TransferTargetVesselPid = 55u,
                        EndpointRootPartUId = 900u,
                        EndpointPartPersistentIds = new List<uint> { 9001u },
                    },
                },
            };

            Assert.Null(CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(100u, 55u), out string source));
            Assert.Equal("none", source);

            HashSet<uint> byPid = CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(0u, 55u), out source);
            Assert.Equal(new HashSet<uint> { 9001u }, byPid);
            Assert.Equal("window", source);
        }

        // catches: a start-docked origin having no recorded part set (its proof keeps only
        // the two halves' roots) - the depot side is split out of the start snapshot.
        [Fact]
        public void RecordedParts_FromStartDockedProofSnapshot()
        {
            var rec = new Recording
            {
                GhostVisualSnapshot = StartDockedVesselNode(),
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginRootPartUId = 500u,
                    StartDockedTransportRootPartUId = 601u,
                    StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock,
                },
            };

            HashSet<uint> pids = CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(500u), out string source);

            Assert.Equal(new HashSet<uint> { 5001u, 5002u, 5003u, 5101u, 5102u, 5103u }, pids);
            Assert.Equal("start-docked-snapshot", source);
        }

        [Fact]
        public void RecordedParts_NoMatchingSource_IsNull()
        {
            var rec = new Recording();
            Assert.Null(CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(100u), out string source));
            Assert.Equal("none", source);
            Assert.Null(CollectRecordedEndpointPartPids(null, Endpoint(100u), out source));
            Assert.Null(CollectRecordedEndpointPartPids(new[] { rec }, Endpoint(0u, 0u), out source));
        }

        // ==================================================================
        // EndpointPartScope
        // ==================================================================

        [Fact]
        public void Scope_IncludesOnlyMaskedParts_NullScopeIncludesAll()
        {
            var scope = new EndpointPartScope(new[] { true, false, true }, isLoadedBranch: true,
                excludedComponentCount: 1);

            Assert.True(scope.Includes(0));
            Assert.False(scope.Includes(1));
            Assert.True(scope.Includes(2));
            Assert.False(scope.Includes(3)); // a part the scope never saw
            Assert.False(scope.Includes(-1));
            Assert.True(EndpointPartScope.Includes(null, 7));
            Assert.Equal("own=2/3", EndpointPartScope.Describe(scope));
            Assert.Equal("whole", EndpointPartScope.Describe(null));
        }

        // catches: a loaded-branch mask (Vessel.parts order) applied to protoPartSnapshots.
        [Fact]
        public void Scope_ForOtherBranch_IsDroppedWithWarn()
        {
            var scope = new EndpointPartScope(new[] { true, false }, isLoadedBranch: true, excludedComponentCount: 1);

            Assert.Same(scope, EndpointPartScope.ForBranch(scope, isLoaded: true, owner: "probe"));
            Assert.Null(EndpointPartScope.ForBranch(scope, isLoaded: false, owner: "probe"));
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("Endpoint part scope dropped")
                && l.Contains("scopeBranch=loaded")
                && l.Contains("readerBranch=unloaded"));
        }
    }
}
