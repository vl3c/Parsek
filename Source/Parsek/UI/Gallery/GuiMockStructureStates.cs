using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Structure List ("Log") window.
    ///
    /// <para><b>EVERY ROW HERE IS REAL-BUILDER OUTPUT.</b> A state assembles DETACHED
    /// in-memory inputs - <see cref="Recording"/> objects and a <see cref="RecordingTree"/>
    /// with its <see cref="BranchPoint"/>s - and then runs
    /// <c>MissionStructureBuilder.Build</c> + <c>MissionStructureListBuilder.Build</c>.
    /// Not one label or location string is typed.
    /// That is the only way this window can be mocked honestly, because it draws
    /// <c>step.Label</c> / <c>step.Location</c> / <c>step.VesselName</c> VERBATIM: a typed
    /// row is a picture of nothing. The one other input is <see cref="PartTitles"/>, the
    /// stock part titles the window resolves through <c>PartLoader</c> in game; titles are
    /// DATA the builder lists, like a vessel name, not a rendered row.</para>
    ///
    /// <para>The first version of this file DID type rows, and they were wrong in ways
    /// only the builders know about - a terminal row's wording is the builder's
    /// (<c>"End: Orbiting"</c>, with no row at all for a leg that ended Docked or Boarded).
    /// Running the builders is what makes those facts the catalogue's rather than a
    /// reviewer's.</para>
    ///
    /// <para><b>DETACHED means detached.</b> Nothing built here is added to
    /// <c>RecordingStore</c>, to a tree the store holds, or to any collection outside the
    /// local graph; <c>Recording</c> and <c>RecordingTree</c> are plain objects with no
    /// self-registering constructor, and both builders are pure
    /// functions of their arguments. The write-set grep gate
    /// (<c>scripts/grep-audit-gui-mock-writeset.ps1</c>) allowlists exactly the types
    /// below and fails the build on any store or writer reference.</para>
    ///
    /// <para><b>The one live cell.</b> The Time column's FIRST row runs
    /// <c>KSPUtil.PrintDateCompact</c> when the window takes the steps, so under a mock it
    /// is a real calendar rendering of the mocked UT; every later row is the pure elapsed
    /// "T+h:mm:ss" from it. Every state's UTs are therefore chosen to read plausibly
    /// rather than arbitrarily.</para>
    /// </summary>
    internal static class GuiMockStructureStates
    {
        // The window's own resize floor is 420x160 and its first-open width 900, where the
        // Event column holds the builder's unshortened budget. 900x420 fits every state's
        // rows unscrolled.
        private const int RectW = 900;
        private const int RectH = 420;

        internal static void Append(List<GuiMockState> into)
        {
            // ---------- Mission mode: the terminal vocabulary ----------
            //
            // A terminal row's Event cell is "End: <terminal word>", so these states are
            // about that cell - which is exactly where the unphotographed words live.

            into.Add(Mission("structure.mission.terminal-splashed",
                "Splashed: one of the terminal words with no picture, drawn as "
                + "'End: Splashed' - that is what the builder emits.",
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
                "Docked and Boarded, the two endings that join another vessel: each leg "
                + "draws NO End row, because its Docked / Boarded branch row already says it.",
                new string[0],
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
                + "branch-event namer distinguishes, each naming the piece that left.",
                new[] { "TerminalState.Destroyed" },
                BuildBreakupRun));

            into.Add(Mission("structure.mission.staging-collapse",
                "A dense launch: one 'Staged: N pieces (title xN)' row per stage, eight "
                + "simultaneous shrouds as one row, a fairing whose title list is shortened "
                + "in the cell (full text in the hover strip) and a lone decouple.",
                new[] { "StructureStep.CollapsedRun" },
                BuildStagingRun));

            // NOTE: there is NO switch-continuation state, and that is a PRODUCT fact
            // rather than an omission. The branch-point pass SKIPS
            // BranchPointType.VesselSwitchContinuation by name ("an observation boundary,
            // not a physical event"), so a "Switch" row is never drawn in this window and a
            // state claiming one would be a picture of nothing. Recorded in
            // design-gui-state-gallery.md section 18 and pinned by
            // GuiMockStructureBuilderFidelityTests.
        }

        // ----- state factories -----

        private static GuiMockState Mission(string id, string note, string[] covers,
                                            Func<List<StructureStep>> build)
            => New(id, note, covers, build, title: "Mock mission");

        private static GuiMockState New(string id, string note, string[] covers,
                                        Func<List<StructureStep>> build, string title)
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

            /// <summary>One debris piece (not a leg) dropped by <paramref name="parentId"/>.
            /// Its own part events never reach the Log; it exists so a stage's branch point
            /// has the child the recorder gives it.</summary>
            internal MissionInputs Debris(string id, string parentId, double startUT,
                                          double endUT)
            {
                var rec = new Recording
                {
                    RecordingId = id,
                    VesselName = "Gallery Debris",
                    IsDebris = true,
                    ExplicitStartUT = startUT,
                    ExplicitEndUT = endUT,
                    ParentAnchorRecordingId = parentId,
                };
                tree.Recordings[id] = rec;
                return this;
            }

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
                                          uint decouplerPid = 0,
                                          int debrisCount = 0,
                                          string secondChildId = null)
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
                    DebrisCount = debrisCount,
                    // The recorder's default grouping window for a split.
                    CoalesceWindow = type == BranchPointType.JointBreak ? 0.5 : 0.0,
                };
                if (parentId != null) bp.ParentRecordingIds.Add(parentId);
                if (childId != null) bp.ChildRecordingIds.Add(childId);
                if (secondChildId != null) bp.ChildRecordingIds.Add(secondChildId);
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
                return MissionStructureListBuilder.Build(tree, structure, PartTitles);
            }
        }

        /// <summary>
        /// The stock part titles the states name, standing in for the in-game
        /// <c>PartLoader</c> lookup the window passes. An unknown name returns null, which
        /// the builder answers with the internal name - its own documented fallback.
        /// </summary>
        internal static string PartTitles(string partName)
        {
            switch (partName)
            {
                case "radialDecoupler1-2": return "TT-38K Radial Decoupler";
                case "launchClamp1": return "TT18-A Launch Stability Enhancer";
                case "liquidEngine2": return "LV-T45 \"Swivel\" Liquid Fuel Engine";
                case "fairingSize1": return "AE-FF1 Airstream Protective Shell (1.25m)";
                case "Decoupler.1": return "TD-12 Decoupler";
                default: return null;
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
            // The launch clamps: three Decoupled parts at the launch moment and the ONE
            // debris-only branch point the recorder writes for them, which is the stage.
            for (int i = 0; i < 3; i++)
                inputs.Staging("root", PartEventType.Decoupled, 600_000.0, "launchClamp1");
            inputs.Debris("clamp-a", "root", 600_000.0, 600_010.0)
                  .Branch(BranchPointType.JointBreak, 600_000.0, "root", "clamp-a",
                          splitCause: "DECOUPLE", debrisCount: 3);
            // A symmetric pair of radial boosters: both decouplers fire in one physics
            // moment and the recorder writes one branch point with two debris children.
            // The second decoupler is the symmetric partner the branch point's single
            // stored decoupler PID does not name; it still belongs to the stage.
            inputs.Staging("root", PartEventType.Decoupled, 600_030.0, "radialDecoupler1-2");
            inputs.Staging("root", PartEventType.Decoupled, 600_030.0, "radialDecoupler1-2");
            inputs.Debris("booster-a", "root", 600_030.0, 600_090.0)
                  .Debris("booster-b", "root", 600_030.0, 600_090.0)
                  .Branch(BranchPointType.JointBreak, 600_030.0, "root", "booster-a",
                          splitCause: "DECOUPLE", debrisCount: 2,
                          secondChildId: "booster-b");
            // A booster's own breakup on its way down: debris, so never a Log row.
            inputs.Staging("booster-a", PartEventType.Decoupled, 600_088.0, "liquidEngine2");
            // EIGHT simultaneous shroud jettisons on eight different parts with no branch
            // point: ONE row naming the part, counted. Different pids on purpose: the dedup
            // is keyed on (eventType, pid), so eight events on ONE part would be one event.
            for (int i = 0; i < 8; i++)
                inputs.Staging("root", PartEventType.ShroudJettisoned, 600_046.0,
                               "liquidEngine2");
            // A fairing whose full title list is too long for the Event cell: the cell
            // shortens it and the hover strip carries the whole text.
            inputs.Staging("root", PartEventType.FairingJettisoned, 600_080.0, "fairingSize1");
            inputs.Staging("root", PartEventType.Decoupled, 600_112.0, "Decoupler.1");
            return inputs.Rows();
        }
    }
}
