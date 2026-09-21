using System;
using System.Collections.Generic;
using Parsek.Logistics;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Structure List ("Log") window, in both target modes.
    ///
    /// <para><b>Every Event and Status cell comes out of the real vocabulary helpers.</b>
    /// A mission step's event word is <see cref="MissionCompositionBuilder.BranchEventName"/> or
    /// <see cref="MissionCompositionBuilder.TerminalName"/>; a route stop's label is
    /// <see cref="RouteStructureListBuilder.FormatStopLabel"/> over a real
    /// <see cref="RouteStop"/>; a location is
    /// <see cref="StructureLocationFormatter.BodyBiome"/> or
    /// <see cref="RouteEndpointLocationFormatter.EndpointLocation"/>. Nothing below types
    /// a cell the product cannot emit - which is the entire argument for a state like
    /// <c>Disassembled</c> being worth photographing at all.</para>
    ///
    /// <para><b>Why the steps are assembled here rather than built from a tree.</b> The
    /// mission builder takes a <c>RecordingTree</c> plus a <c>MissionStructure</c> and the
    /// route builder takes a <c>Route</c> plus a recording lookup; both are the
    /// store-shaped inputs philosophy 2 keeps out of a mock. The window's injection point
    /// is its <c>steps</c> list, which is the layer below - so a state supplies the rows
    /// and lets every WORD in them come from the shared formatters.</para>
    ///
    /// <para><b>The one live cell.</b> The Time column runs
    /// <c>KSPUtil.PrintDateCompact</c> inside the draw method, so under a mock it is a
    /// real calendar rendering of the mocked UT. Every state's UTs are therefore chosen to
    /// read plausibly rather than arbitrarily.</para>
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

            into.Add(Mission("structure.mission.terminal-splashed",
                "Splashed: one of the six terminal statuses with no picture.",
                new[] { "TerminalState.Splashed" },
                () => TerminalRun(TerminalState.Splashed, "Kerbin", "Water",
                                  "Booster Recovery Test")));

            into.Add(Mission("structure.mission.terminal-destroyed",
                "Destroyed: the ending a crash produces.",
                new[] { "TerminalState.Destroyed" },
                () => TerminalRun(TerminalState.Destroyed, "Mun", "Midlands",
                                  "Mun Lander 3")));

            into.Add(Mission("structure.mission.terminal-recovered",
                "Recovered: the ending a normal return produces.",
                new[] { "TerminalState.Recovered" },
                () => TerminalRun(TerminalState.Recovered, "Kerbin", "Shores",
                                  "Kerbin Return")));

            into.Add(Mission("structure.mission.terminal-suborbital",
                "Suborbital: a scene exit on a ballistic arc.",
                new[] { "TerminalState.SubOrbital" },
                () => TerminalRun(TerminalState.SubOrbital, "Kerbin", "Highlands",
                                  "Sounding Rocket")));

            into.Add(Mission("structure.mission.terminal-disassembled",
                "Disassembled: the newest terminal kind, an ending rather than a loss.",
                new[] { "TerminalState.Disassembled" },
                () => TerminalRun(TerminalState.Disassembled, "Kerbin", "Launch Pad",
                                  "Pad Teardown")));

            into.Add(Mission("structure.mission.terminal-docked",
                "Docked and Boarded, the two terminals that end a leg by joining another.",
                new[] { "TerminalState.Docked", "TerminalState.Boarded" },
                BuildDockedAndBoardedTerminals));

            into.Add(Mission("structure.mission.terminal-orbiting-landed",
                "Orbiting and Landed together, so the terminal vocabulary is complete in "
                + "one window rather than split across fixtures.",
                new[] { "TerminalState.Orbiting", "TerminalState.Landed" },
                BuildOrbitingAndLandedTerminals));

            // ---------- Mission mode: the branch-event vocabulary ----------

            into.Add(Mission("structure.mission.eva-and-board",
                "'EVA <crew>' and 'Boarded', neither of which any capture shows.",
                new string[0],
                BuildEvaRun));

            into.Add(Mission("structure.mission.breakup",
                "Crashed / Overheated / Broke up / Broke off - the four failure causes the "
                + "branch-event namer distinguishes.",
                new string[0],
                BuildBreakupRun));

            into.Add(Mission("structure.mission.switch-continuation",
                "'Switch': the vessel-switch continuation branch point.",
                new string[0],
                BuildSwitchRun));

            into.Add(Mission("structure.mission.staging-collapse",
                "A dense launch: the xN collapse of simultaneous identical staging events "
                + "beside a numbered separation.",
                new string[0],
                BuildStagingRun));

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
                new string[0],
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

        // ----- step assemblers -----

        private static StructureStep Step(double ut, StructureStepKind kind, string label,
                                          string status, string body, string biome,
                                          string vessel)
            => new StructureStep
            {
                UT = ut,
                Kind = kind,
                Label = label,
                Status = status,
                Location = StructureLocationFormatter.BodyBiome(body, biome),
                VesselName = vessel,
            };

        /// <summary>A launch plus one terminal, which is the smallest run that shows a
        /// terminal word in context.</summary>
        private static List<StructureStep> TerminalRun(TerminalState terminal,
                                                       string body, string biome,
                                                       string vessel)
            => new List<StructureStep>
            {
                Step(90_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", vessel),
                Step(90_120.0, StructureStepKind.Staging,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "DECOUPLE"),
                     "Flying", "Kerbin", "Shores", vessel + " Booster"),
                Step(91_400.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(terminal),
                     MissionCompositionBuilder.TerminalName(terminal), body, biome, vessel),
            };

        private static List<StructureStep> BuildDockedAndBoardedTerminals()
            => new List<StructureStep>
            {
                Step(120_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Station Tug"),
                Step(124_000.0, StructureStepKind.Dock,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Dock, null),
                     "Orbiting", "Kerbin", null, "Station Tug"),
                Step(124_100.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Docked),
                     MissionCompositionBuilder.TerminalName(TerminalState.Docked),
                     "Kerbin", null, "Station Tug"),
                Step(124_600.0, StructureStepKind.Eva,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Board, null),
                     "Orbiting", "Kerbin", null, "Bill Kerman"),
                Step(124_700.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Boarded),
                     MissionCompositionBuilder.TerminalName(TerminalState.Boarded),
                     "Kerbin", null, "Bill Kerman"),
            };

        private static List<StructureStep> BuildOrbitingAndLandedTerminals()
            => new List<StructureStep>
            {
                Step(200_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Mun Relay 1"),
                Step(206_000.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "DECOUPLE"),
                     "Orbiting", "Mun", null, "Mun Lander"),
                Step(208_000.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                     MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                     "Mun", null, "Mun Relay 1"),
                Step(209_400.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Landed),
                     MissionCompositionBuilder.TerminalName(TerminalState.Landed),
                     "Mun", "Midlands", "Mun Lander"),
            };

        private static List<StructureStep> BuildEvaRun()
            => new List<StructureStep>
            {
                Step(300_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Orbital Lab"),
                Step(304_800.0, StructureStepKind.Eva,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.EVA, null)
                     + " Bill Kerman",
                     "Orbiting", "Kerbin", null, "Bill Kerman"),
                Step(305_600.0, StructureStepKind.Eva,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Board, null),
                     "Orbiting", "Kerbin", null, "Bill Kerman"),
                Step(306_000.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Recovered),
                     MissionCompositionBuilder.TerminalName(TerminalState.Recovered),
                     "Kerbin", "Shores", "Orbital Lab"),
            };

        private static List<StructureStep> BuildBreakupRun()
            => new List<StructureStep>
            {
                Step(400_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Reentry Test 2"),
                Step(401_200.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "OVERHEAT"),
                     "Flying", "Kerbin", "Highlands", "Heat Shield"),
                Step(401_260.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(
                         BranchPointType.Breakup, "STRUCTURAL_FAILURE"),
                     "Flying", "Kerbin", "Highlands", "Upper Stage"),
                Step(401_280.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, null),
                     "Flying", "Kerbin", "Highlands", "Antenna"),
                Step(401_340.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "CRASH"),
                     MissionCompositionBuilder.TerminalName(TerminalState.Destroyed),
                     "Kerbin", "Highlands", "Reentry Test 2"),
            };

        private static List<StructureStep> BuildSwitchRun()
            => new List<StructureStep>
            {
                Step(500_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Twin Probe Carrier"),
                Step(503_000.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "DECOUPLE"),
                     "Orbiting", "Kerbin", null, "Probe B"),
                Step(504_000.0, StructureStepKind.Separation,
                     MissionCompositionBuilder.BranchEventName(
                         BranchPointType.VesselSwitchContinuation, null),
                     "Orbiting", "Kerbin", null, "Probe B"),
                Step(506_000.0, StructureStepKind.Terminal,
                     MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                     MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                     "Kerbin", null, "Probe B"),
            };

        private static List<StructureStep> BuildStagingRun()
        {
            var steps = new List<StructureStep>
            {
                Step(600_000.0, StructureStepKind.Launch,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Launch, null),
                     "Prelaunch", "Kerbin", "Launch Pad", "Heavy Lifter"),
            };
            // The collapse suffix is the builder's own "xN" convention, reached here
            // through the same formatter the builder uses for a collapsed run.
            steps.Add(Step(600_046.0, StructureStepKind.Staging,
                MissionStructureListBuilder.FormatCollapsedLabel(
                    MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "DECOUPLE"), 8),
                "Flying", "Kerbin", "Shores", "Heavy Lifter"));
            steps.Add(Step(600_112.0, StructureStepKind.Separation,
                MissionCompositionBuilder.BranchEventName(BranchPointType.JointBreak, "DECOUPLE"),
                "Flying", "Kerbin", "Shores", "Second Stage"));
            steps.Add(Step(601_400.0, StructureStepKind.Terminal,
                MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                MissionCompositionBuilder.TerminalName(TerminalState.Orbiting),
                "Kerbin", null, "Heavy Lifter"));
            return steps;
        }

        // ----- route mode -----

        private enum RouteShape { Pickup, Mixed, DepotOrigin }

        private static List<StructureStep> RouteRun(RouteShape shape)
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

            RouteStop stop = BuildStop(shape);

            var steps = new List<StructureStep>
            {
                // The route Origin pseudo-step has no single UT: the window's own
                // FormatTime renders a NaN as "-", which is what a real route draws.
                new StructureStep
                {
                    UT = double.NaN,
                    Kind = StructureStepKind.Origin,
                    Label = kscOrigin ? "Origin: KSC" : "Origin: depot",
                    Status = RouteEndpointLocationFormatter.EndpointStatus(origin, kscOrigin),
                    Location = RouteEndpointLocationFormatter.EndpointLocation(
                        origin, kscOrigin, null),
                    VesselName = "",
                },
                Step(700_000.0, StructureStepKind.Dock,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Dock, null),
                     "Orbiting", "Mun", null, "Supply Tug"),
                new StructureStep
                {
                    UT = 700_600.0,
                    Kind = StructureStepKind.Delivery,
                    Label = RouteStructureListBuilder.FormatStopLabel(stop, ""),
                    Status = "Orbiting",
                    Location = StructureLocationFormatter.BodyBiome("Mun", null),
                    VesselName = "Mun Station",
                },
                Step(701_200.0, StructureStepKind.Undock,
                     MissionCompositionBuilder.BranchEventName(BranchPointType.Undock, null),
                     "Orbiting", "Mun", null, "Supply Tug"),
            };
            return steps;
        }

        private static RouteStop BuildStop(RouteShape shape)
        {
            var stop = new RouteStop();
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
