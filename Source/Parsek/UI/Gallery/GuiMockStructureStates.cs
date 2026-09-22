using System;
using System.Collections.Generic;
using Parsek.Logistics;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Structure List ("Log") window, in both target modes.
    ///
    /// <para><b>EVERY ROW HERE IS REAL-BUILDER OUTPUT.</b> A state assembles DETACHED
    /// in-memory inputs - <see cref="Recording"/> objects, a <see cref="RecordingTree"/>
    /// with its <see cref="BranchPoint"/>s, or a <see cref="Logistics.Route"/> with its
    /// stops and one connection window - and then runs
    /// <c>MissionStructureBuilder.Build</c> + <c>MissionStructureListBuilder.Build</c>, or
    /// <c>RouteStructureListBuilder.Build</c>. Not one label, status or location string is
    /// typed. That is the only way this window can be mocked honestly, because it draws
    /// <c>step.Label</c> / <c>step.Status</c> / <c>step.Location</c> VERBATIM: a typed row
    /// is a picture of nothing.</para>
    ///
    /// <para>The first version of this file DID type them, and the rows it produced were
    /// wrong in ways only the builders know about - a terminal row's Event column is always
    /// the generic <c>"End"</c> with the terminal word in STATUS
    /// (<c>MissionStructureListBuilder</c>'s terminal pass), a route's dock rows read
    /// <c>"Dock"</c> / <c>"Undock"</c> rather than the branch-event <c>"Docked"</c> /
    /// <c>"Undocked"</c>, every route row's Vessel cell is empty, and a staging row reads
    /// <c>"Staged &lt;part&gt;"</c> rather than a branch label. Running the builders is
    /// what makes those facts the catalogue's rather than a reviewer's.</para>
    ///
    /// <para><b>DETACHED means detached.</b> Nothing built here is added to
    /// <c>RecordingStore</c>, to a tree the store holds, to <c>RouteStore</c>, or to any
    /// collection outside the local graph; <c>Recording</c> and <c>RecordingTree</c> are
    /// plain objects with no self-registering constructor, and both builders are pure
    /// functions of their arguments. The write-set grep gate
    /// (<c>scripts/grep-audit-gui-mock-writeset.ps1</c>) allowlists exactly the types
    /// below and fails the build on any store or writer reference.</para>
    ///
    /// <para><b>The one live cell.</b> The Time column runs
    /// <c>KSPUtil.PrintDateCompact</c> inside the draw method, so under a mock it is a real
    /// calendar rendering of the mocked UT. Every state's UTs are therefore chosen to read
    /// plausibly rather than arbitrarily.</para>
    /// </summary>
    internal static class GuiMockStructureStates
    {
        // The window's own resize floor is 420x160; its six columns want ~800 before the
        // Event column starts clipping. 900x420 fits every state's rows unscrolled.
        private const int RectW = 900;
        private const int RectH = 420;

        internal static void Append(List<GuiMockState> into)
        {
            // ---------- Mission mode: the terminal vocabulary ----------
            //
            // A terminal row is "End" in Event and the terminal word in STATUS, so these
            // states are about the Status column - which is exactly where the six
            // unphotographed words live.

            into.Add(Mission("structure.mission.terminal-splashed",
                "Splashed: one of the six terminal STATUS words with no picture. The Event "
                + "cell reads the generic 'End' - that is what the builder emits.",
                new[] { "TerminalState.Splashed" },
                () => TerminalRun(TerminalState.Splashed, "Water",
                                  "Booster Recovery Test")));

            into.Add(Mission("structure.mission.terminal-destroyed",
                "Destroyed: the ending a crash produces.",
                new[] { "TerminalState.Destroyed" },
                () => TerminalRun(TerminalState.Destroyed, "Midlands", "Mun Lander 3")));

            into.Add(Mission("structure.mission.terminal-recovered",
                "Recovered: the ending a normal return produces.",
                new[] { "TerminalState.Recovered" },
                () => TerminalRun(TerminalState.Recovered, "Shores", "Kerbin Return")));

            into.Add(Mission("structure.mission.terminal-suborbital",
                "Suborbital: a scene exit on a ballistic arc.",
                new[] { "TerminalState.SubOrbital" },
                () => TerminalRun(TerminalState.SubOrbital, "Highlands",
                                  "Sounding Rocket")));

            into.Add(Mission("structure.mission.terminal-disassembled",
                "Disassembled: the newest terminal kind, an ending rather than a loss.",
                new[] { "TerminalState.Disassembled" },
                () => TerminalRun(TerminalState.Disassembled, "Launch Pad",
                                  "Pad Teardown")));

            into.Add(Mission("structure.mission.terminal-docked",
                "Docked and Boarded, the two terminal words that end a leg by joining "
                + "another - each on its own leg, with the Dock and Board branch rows above.",
                new[] { "TerminalState.Docked", "TerminalState.Boarded" },
                BuildDockedAndBoardedTerminals));

            into.Add(Mission("structure.mission.terminal-orbiting-landed",
                "Orbiting and Landed together, so the terminal vocabulary is complete in one "
                + "window rather than split across fixtures.",
                new[] { "TerminalState.Orbiting", "TerminalState.Landed" },
                BuildOrbitingAndLandedTerminals));

            // ---------- Mission mode: the branch-event vocabulary ----------

            into.Add(Mission("structure.mission.eva-and-board",
                "A mid-mission EVA and its return: the branch rows read 'EVA' and 'Boarded' "
                + "(the crew name is on the Vessel cell, which is the builder's own split).",
                new string[0],
                BuildEvaRun));

            into.Add(Mission("structure.mission.eva-leg-launch",
                "The OTHER EVA form, and the only one that carries the crew name in the "
                + "Event cell: an EVA kerbal whose leg is a ROOT, so the launch pass emits "
                + "'EVA <crew>'.",
                new string[0],
                BuildEvaLegLaunchRun));

            into.Add(Mission("structure.mission.breakup",
                "Crashed / Overheated / Broke up / Broke off - the four failure causes the "
                + "branch-event namer distinguishes, none of them photographed.",
                new[] { "TerminalState.Destroyed" },
                BuildBreakupRun));

            into.Add(Mission("structure.mission.staging-collapse",
                "A dense launch: the xN collapse of simultaneous identical staging events "
                + "beside the two named jettison forms and a 'Staged <part>' row.",
                new[] { "StructureStep.CollapsedRun" },
                BuildStagingRun));

            // NOTE: there is NO switch-continuation state, and that is a PRODUCT fact
            // rather than an omission. The branch-point pass SKIPS
            // BranchPointType.VesselSwitchContinuation by name ("an observation boundary,
            // not a physical event"), so a "Switch" row is never drawn in this window and a
            // state claiming one would be a picture of nothing. Recorded in
            // design-gui-state-gallery.md section 18 and pinned by
            // GuiMockStructureBuilderFidelityTests.

            // ---------- Route mode ----------

            into.Add(Route("structure.route.pickup",
                "A PURE-pickup stop: 'Pick up (...)', which no relay fixture has "
                + "photographed.",
                new string[0],
                () => RouteRun(RouteShape.Pickup)));

            into.Add(Route("structure.route.mixed",
                "A mixed stop: 'Deliver (...) / Pick up (...)' at one dock.",
                new string[0],
                () => RouteRun(RouteShape.Mixed)));

            into.Add(Route("structure.route.origin-depot",
                "'Origin: depot' with a surface endpoint - every capture reads "
                + "'Origin: KSC'.",
                new[] { "StructureStep.RouteOrigin" },
                () => RouteRun(RouteShape.DepotOrigin)));
        }

        // ----- state factories -----

        private static GuiMockState Mission(string id, string note, string[] covers,
                                            Func<List<StructureStep>> build)
            => New(id, note, covers, build, routeMode: false, title: "Mission structure");

        private static GuiMockState Route(string id, string note, string[] covers,
                                          Func<List<StructureStep>> build)
            => New(id, note, covers, build, routeMode: true, title: "Route structure");

        private static GuiMockState New(string id, string note, string[] covers,
                                        Func<List<StructureStep>> build,
                                        bool routeMode, string title)
        {
            return new GuiMockState
            {
                Id = id,
                Window = GuiMockSession.StructureWindow,
                Tab = null,
                RectW = RectW,
                RectH = RectH,
                Covers = covers,
                Note = note,
                Build = () => new GuiMockPayload
                {
                    Window = GuiMockSession.StructureWindow,
                    Structure = new GuiMockStructure
                    {
                        RouteMode = routeMode,
                        Title = title,
                        Steps = build(),
                    },
                },
            };
        }

        // ----- the mission-side input assembler -----

        /// <summary>
        /// A detached recording tree under construction, plus the one call that turns it
        /// into rows. Holds INPUTS only; every rendered string comes out of
        /// <see cref="Rows"/>.
        /// </summary>
        internal sealed class MissionInputs
        {
            private readonly RecordingTree tree = new RecordingTree { Id = "gallery-tree" };
            private uint nextPid = 7000;

            /// <summary>One controlled leg. <paramref name="terminal"/> null means the leg
            /// has no ending, so the terminal pass emits no row for it.</summary>
            internal MissionInputs Leg(string id, string vesselName, double startUT,
                                       double endUT, TerminalState? terminal,
                                       string body, string biome, string situation,
                                       string launchSite = null, string endBiome = null,
                                       string evaCrewName = null,
                                       string parentAnchorId = null)
            {
                var rec = new Recording
                {
                    RecordingId = id,
                    VesselName = vesselName,
                    // StartUT / EndUT are DERIVED (from the trajectory bounds, with these
                    // two as overrides), so a gallery leg sets the explicit fields - which
                    // is also the production shape for a recording with no sampled points
                    // yet. The getters fall back to them when no bounds resolve.
                    ExplicitStartUT = startUT,
                    ExplicitEndUT = endUT,
                    TerminalStateValue = terminal,
                    StartBodyName = body,
                    StartBiome = biome,
                    StartSituation = situation,
                    EndBiome = endBiome ?? biome,
                    LaunchSiteName = launchSite,
                    EvaCrewName = evaCrewName,
                    ParentAnchorRecordingId = parentAnchorId,
                    ChainId = "gallery-chain-" + id,
                };
                tree.Recordings[id] = rec;
                if (tree.RootRecordingId == null) tree.RootRecordingId = id;
                return this;
            }

            /// <summary>One branch point. The Event cell is
            /// <c>MissionCompositionBuilder.BranchEventName(type, cause)</c>, computed by
            /// the builder rather than here.</summary>
            internal MissionInputs Branch(BranchPointType type, double ut, string parentId,
                                          string childId, string splitCause = null,
                                          string breakupCause = null,
                                          uint decouplerPid = 0)
            {
                var bp = new BranchPoint
                {
                    Id = "bp-" + tree.BranchPoints.Count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    Type = type,
                    UT = ut,
                    SplitCause = splitCause,
                    BreakupCause = breakupCause,
                    DecouplerPartId = decouplerPid,
                };
                if (parentId != null) bp.ParentRecordingIds.Add(parentId);
                if (childId != null) bp.ChildRecordingIds.Add(childId);
                tree.BranchPoints.Add(bp);
                return this;
            }

            /// <summary>
            /// One staging part event on a leg. Each call takes a FRESH pid, because the
            /// builder's cross-recording dedup is keyed on (eventType, pid) within a
            /// tolerance - so N simultaneous events must be N different parts, which is
            /// also what a real cluster of shrouds is.
            /// </summary>
            internal MissionInputs Staging(string legId, PartEventType eventType, double ut,
                                           string partName)
            {
                Recording rec;
                if (!tree.Recordings.TryGetValue(legId, out rec)) return this;
                rec.PartEvents.Add(new PartEvent
                {
                    ut = ut,
                    eventType = eventType,
                    partPersistentId = nextPid++,
                    partName = partName,
                });
                return this;
            }

            /// <summary>Runs the two REAL builders, which is what
            /// <c>StructureListWindowUI.Rebuild</c> does with a tree out of the store.</summary>
            internal List<StructureStep> Rows()
            {
                MissionStructure structure = MissionStructureBuilder.Build(tree);
                return MissionStructureListBuilder.Build(tree, structure);
            }
        }

        // ----- the mission states -----
        //
        // Each returns a NEW graph per call (the builders' inputs are mutable, and a
        // shared one would let one capture's rows leak into the next).

        /// <summary>A launch, one jettison and one terminal: the smallest run that shows a
        /// terminal word in its own column.</summary>
        internal static List<StructureStep> TerminalRun(TerminalState terminal,
                                                        string endBiome, string vessel)
        {
            return new MissionInputs()
                .Leg("root", vessel, 90_000.0, 91_400.0, terminal,
                     "Kerbin", "LaunchPad", "Prelaunch", launchSite: "Launch Pad",
                     endBiome: endBiome)
                .Staging("root", PartEventType.FairingJettisoned, 90_120.0, "fairing.1")
                .Rows();
        }

        internal static List<StructureStep> BuildDockedAndBoardedTerminals()
        {
            return new MissionInputs()
                .Leg("tug", "Station Tug", 120_000.0, 124_100.0, TerminalState.Docked,
                     "Kerbin", "", "Orbiting", launchSite: "Launch Pad")
                .Leg("eva", "Bill Kerman", 124_600.0, 124_700.0, TerminalState.Boarded,
                     "Kerbin", "", "Orbiting", evaCrewName: "Bill Kerman",
                     parentAnchorId: "tug")
                .Branch(BranchPointType.Dock, 124_000.0, "tug", null)
                .Branch(BranchPointType.EVA, 124_600.0, "tug", "eva", splitCause: "EVA")
                .Branch(BranchPointType.Board, 124_700.0, "eva", "tug")
                .Rows();
        }

        internal static List<StructureStep> BuildOrbitingAndLandedTerminals()
        {
            return new MissionInputs()
                .Leg("carrier", "Mun Relay 1", 200_000.0, 208_000.0, TerminalState.Orbiting,
                     "Mun", "", "Orbiting", launchSite: "Launch Pad")
                .Leg("lander", "Mun Lander", 206_000.0, 209_400.0, TerminalState.Landed,
                     "Mun", "Midlands", "Orbiting", parentAnchorId: "carrier",
                     endBiome: "Midlands")
                .Branch(BranchPointType.JointBreak, 206_000.0, "carrier", "lander",
                        splitCause: "DECOUPLE", decouplerPid: 4101)
                .Rows();
        }

        internal static List<StructureStep> BuildEvaRun()
        {
            return new MissionInputs()
                .Leg("lab", "Orbital Lab", 300_000.0, 306_000.0, TerminalState.Recovered,
                     "Kerbin", "", "Orbiting", launchSite: "Launch Pad",
                     endBiome: "Shores")
                .Leg("eva", "Bill Kerman", 304_800.0, 305_600.0, TerminalState.Boarded,
                     "Kerbin", "", "Orbiting", evaCrewName: "Bill Kerman",
                     parentAnchorId: "lab")
                .Branch(BranchPointType.EVA, 304_800.0, "lab", "eva", splitCause: "EVA")
                .Branch(BranchPointType.Board, 305_600.0, "eva", "lab")
                .Rows();
        }

        internal static List<StructureStep> BuildEvaLegLaunchRun()
        {
            // An EVA kerbal whose leg has no branch parent IS a root leg, so the launch
            // pass emits "EVA <crew>" - the only row in the window that carries a crew
            // name in the Event column. This is the eva2-lko-crewed shape (an EVA branch
            // that is its own tree).
            return new MissionInputs()
                .Leg("eva", "Bill Kerman", 310_000.0, 310_900.0, TerminalState.Boarded,
                     "Kerbin", "", "Orbiting", evaCrewName: "Bill Kerman")
                .Rows();
        }

        internal static List<StructureStep> BuildBreakupRun()
        {
            return new MissionInputs()
                .Leg("root", "Reentry Test 2", 400_000.0, 401_340.0,
                     TerminalState.Destroyed, "Kerbin", "Highlands", "Flying",
                     launchSite: "Launch Pad", endBiome: "Highlands")
                .Leg("shield", "Heat Shield", 401_200.0, 401_260.0, null,
                     "Kerbin", "Highlands", "Flying", parentAnchorId: "root")
                .Leg("upper", "Upper Stage", 401_260.0, 401_300.0, null,
                     "Kerbin", "Highlands", "Flying", parentAnchorId: "root")
                .Leg("antenna", "Antenna", 401_280.0, 401_320.0, null,
                     "Kerbin", "Highlands", "Flying", parentAnchorId: "root")
                .Leg("fragment", "Reentry Test 2 Debris", 401_330.0, 401_340.0, null,
                     "Kerbin", "Highlands", "Flying", parentAnchorId: "root")
                .Branch(BranchPointType.JointBreak, 401_200.0, "root", "shield",
                        breakupCause: "OVERHEAT")
                .Branch(BranchPointType.Breakup, 401_260.0, "root", "upper",
                        breakupCause: "STRUCTURAL_FAILURE")
                .Branch(BranchPointType.JointBreak, 401_280.0, "root", "antenna")
                .Branch(BranchPointType.JointBreak, 401_330.0, "root", "fragment",
                        breakupCause: "CRASH")
                .Rows();
        }

        internal static List<StructureStep> BuildStagingRun()
        {
            var inputs = new MissionInputs()
                .Leg("root", "Heavy Lifter", 600_000.0, 601_400.0, TerminalState.Orbiting,
                     "Kerbin", "LaunchPad", "Prelaunch", launchSite: "Launch Pad");
            // EIGHT simultaneous shroud jettisons on eight different parts, which is what
            // a real engine cluster produces and what the collapse walk turns into one
            // "Shroud jettisoned x8" row. Different pids on purpose: the dedup is keyed on
            // (eventType, pid) within a tolerance, so eight events on ONE part would be
            // collapsed to one by the DEDUP rather than by the display collapse.
            for (int i = 0; i < 8; i++)
                inputs.Staging("root", PartEventType.ShroudJettisoned, 600_046.0,
                               "engineShroud");
            inputs.Staging("root", PartEventType.FairingJettisoned, 600_080.0, "fairing.1");
            inputs.Staging("root", PartEventType.Decoupled, 600_112.0, "decoupler.2");
            return inputs.Rows();
        }

        // ----- route mode -----

        internal enum RouteShape { Pickup, Mixed, DepotOrigin }

        /// <summary>
        /// Builds a detached <see cref="Logistics.Route"/> plus the one dock-member
        /// recording that carries its <see cref="RouteConnectionWindow"/>, and runs the
        /// REAL route builder. The recording lookup is a LOCAL closure over that one
        /// object, not a store read: the production window passes its own by-id resolve,
        /// and this is the same shape with a one-entry source.
        /// </summary>
        internal static List<StructureStep> RouteRun(RouteShape shape)
        {
            bool kscOrigin = shape != RouteShape.DepotOrigin;
            RouteEndpoint origin = kscOrigin
                ? new RouteEndpoint { BodyName = "Kerbin", IsSurface = true }
                : new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 0.68,
                    Longitude = -23.41,
                    IsSurface = true,
                };
            var dockEndpoint = new RouteEndpoint { BodyName = "Mun", IsSurface = false };

            var dockRecording = new Recording
            {
                RecordingId = "gallery-dock-member",
                VesselName = "Supply Tug",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "gallery-window",
                        DockUT = 700_000.0,
                        UndockUT = 701_200.0,
                        EndpointAtDock = dockEndpoint,
                    },
                },
            };

            var route = new Logistics.Route
            {
                Id = "gallery-route",
                Name = "Mun Station Supply",
                Origin = origin,
                IsKscOrigin = kscOrigin,
                DockMemberRecordingId = dockRecording.RecordingId,
                RecordedDockUT = 700_600.0,
            };
            route.Stops.Add(BuildStop(shape, dockEndpoint));

            // biomeResolver is null: it is a live KSP call (VesselSpawner.TryResolveBiome),
            // and the builder's own documented fallback is the surface coordinates - which
            // is a real form the window draws on any save whose biome does not resolve.
            return RouteStructureListBuilder.Build(
                route,
                id => string.Equals(id, dockRecording.RecordingId, StringComparison.Ordinal)
                    ? dockRecording
                    : null,
                null);
        }

        private static RouteStop BuildStop(RouteShape shape, RouteEndpoint endpoint)
        {
            var stop = new RouteStop { Endpoint = endpoint };
            if (shape != RouteShape.Pickup)
            {
                stop.DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 282.0 },
                    { "Oxidizer", 345.0 },
                };
            }
            // Both the pure-pickup and the mixed shape carry a pickup manifest; the
            // depot-origin shape carries a delivery only, which is the ordinary form.
            if (shape == RouteShape.Pickup || shape == RouteShape.Mixed)
            {
                stop.PickupManifest = new Dictionary<string, double>
                {
                    { "Ore", 1500.0 },
                };
            }
            return stop;
        }
    }
}
