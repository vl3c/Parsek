using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Unit tests for the Missions-tab presentation derivations (Stage 1 / Tier 1 of
    // docs/dev/research/mission-presentation-ux-analysis-2026-08-12.md): the header summary line
    // (T1.1 / T1.2), the delta-phrased interval labels (T1.3), the same-tree dock partner's name
    // (T1.4), the state tooltips (T1.5), and the loop-conflict screen message (T1.6). The IMGUI
    // layout itself is playtest-verified; these guard the exact strings the player reads and the
    // fallbacks that keep the old text when a derivation cannot resolve.
    //
    // The read-model fixtures go through the REAL MissionStructureBuilder /
    // MissionThroughLineBuilder / MissionCompositionBuilder (same Leg / BP / Tree helpers as
    // MissionCompositionTests), so a change to the interval keying or child ordering fails here
    // instead of silently degrading every label to the fallback.
    public class MissionPresentationTests
    {
        // ---- fixture helpers (mirrors MissionCompositionTests) ----

        private static Recording Leg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0, int seats = 0, int crew = 0,
            string eva = null, string parentAnchor = null, string vessel = "Kerbal X",
            string[] crewNames = null, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                EvaCrewName = eva,
                ParentAnchorRecordingId = parentAnchor,
                TerminalStateValue = terminal,
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            for (int i = 0; i < seats; i++) controllers.Add(new ControllerInfo { type = "ExternalSeat" });
            if (!string.IsNullOrEmpty(eva)) controllers.Add(new ControllerInfo { type = "KerbalEVA" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            if (crew > 0) rec.StartCrew = new Dictionary<string, int> { { "Pilot", crew } };
            if (crewNames != null)
            {
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
                foreach (var n in crewNames) rec.CrewEndStates[n] = default(KerbalEndState);
                rec.CrewEndStatesResolved = true;
            }
            return rec;
        }

        private static BranchPoint BP(string id, BranchPointType type, string[] parents, string[] children,
            string splitCause = null)
            => new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
                SplitCause = splitCause,
            };

        private static RecordingTree Tree(Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            if (bps != null) tree.BranchPoints.AddRange(bps);
            return tree;
        }

        // The three read models the Missions tab holds per tree.
        private static void BuildModels(
            Recording[] recs, BranchPoint[] bps,
            out MissionStructure structure, out MissionThroughLineView view,
            out List<MissionCompositionNode> roots)
        {
            structure = MissionStructureBuilder.Build(Tree(recs, bps));
            view = MissionThroughLineBuilder.Build(structure);
            roots = MissionCompositionBuilder.Build(structure);
        }

        // A launch stack that decouples a named booster and lands: two intervals of "Kerbal X"
        // plus the peeled "Kerbal X Booster".
        private static void BuildDecoupleShape(
            out MissionStructure structure, out MissionThroughLineView view,
            out List<MissionCompositionNode> roots)
        {
            BuildModels(
                new[]
                {
                    Leg("L", "C", 0, 0, 42, pods: 1, probes: 1,
                        crewNames: new[] { "Jeb Kerman", "Bob Kerman" }),
                    Leg("cont", "C2", 0, 42, 200, pods: 1,
                        crewNames: new[] { "Jeb Kerman", "Bob Kerman" },
                        terminal: TerminalState.Landed),
                    Leg("boost", "C3", 0, 42, 115, probes: 1, parentAnchor: "L",
                        vessel: "Kerbal X Booster", terminal: TerminalState.Destroyed),
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak, new[] { "L" },
                        new[] { "cont", "boost" }, splitCause: "DECOUPLE"),
                },
                out structure, out view, out roots);
        }

        // ===================== T1.1 / T1.2 - the summary line =====================

        [Fact]
        public void BuildSummaryLine_JoinsEveryPiece()
        {
            // Fails if the summary drops a piece or changes the separator / arrow the header reads.
            string line = MissionPresentation.BuildSummaryLine(
                "Y1, D12 3:20:11", "Y1, D14 6:41:02", "2d 3h", 3, 3, "Landed", "T- 2h 14m");
            string sep = MissionPresentation.SummarySeparator;
            Assert.Equal(
                "Y1, D12 3:20:11" + MissionPresentation.SummarySpanArrow + "Y1, D14 6:41:02"
                + sep + "2d 3h" + sep + "3 vessels" + sep + "3 crew" + sep + "Landed"
                + sep + "Next launch T- 2h 14m",
                line);
        }

        [Fact]
        public void BuildSummaryLine_OmitsMissingPieces()
        {
            // Fails if an uncrewed, unfinished, non-looping mission renders empty separators
            // (a leading / doubled middle dot) or a dangling span arrow.
            Assert.Equal("1 vessel", MissionPresentation.BuildSummaryLine(
                "", "", "", 1, 0, "", null));
            Assert.Equal("2 vessels" + MissionPresentation.SummarySeparator + "Orbiting",
                MissionPresentation.BuildSummaryLine(null, null, null, 2, 0, "Orbiting", null));
            Assert.Equal("", MissionPresentation.BuildSummaryLine(
                "", "", "", 0, 0, "", null));
        }

        [Fact]
        public void BuildSummaryLine_SingularVesselCount()
        {
            // Fails if a one-vessel mission reads "1 vessels".
            Assert.Contains("1 vessel" + MissionPresentation.SummarySeparator,
                MissionPresentation.BuildSummaryLine("", "", "", 1, 2, "", null));
            Assert.Contains("2 vessels", MissionPresentation.BuildSummaryLine(
                "", "", "", 2, 0, "", null));
        }

        [Fact]
        public void SummaryNextLaunchText_OnlyForwardsACountdown()
        {
            // Fails if the header repeats an engine STATE word ("not aligned" reading as an
            // outcome) or drops a real countdown.
            Assert.Equal("T- 2h 14m", MissionPresentation.SummaryNextLaunchText("T- 2h 14m"));
            Assert.Null(MissionPresentation.SummaryNextLaunchText("not aligned"));
            Assert.Null(MissionPresentation.SummaryNextLaunchText("continuous"));
            Assert.Null(MissionPresentation.SummaryNextLaunchText(""));
            Assert.Null(MissionPresentation.SummaryNextLaunchText(null));
        }

        [Fact]
        public void ComputeSummaryFacts_SpanVesselsCrewAndOutcome()
        {
            BuildDecoupleShape(out MissionStructure structure, out MissionThroughLineView view,
                out List<MissionCompositionNode> roots);

            MissionPresentation.MissionSummaryFacts facts =
                MissionPresentation.ComputeSummaryFacts(structure, view, roots);

            // Fails if the span is not the whole mission's extent (0 -> the latest through-line
            // end, here the continuing pod's 200, not the booster's 115).
            Assert.True(facts.HasSpan);
            Assert.Equal(0.0, facts.StartUT);
            Assert.Equal(200.0, facts.EndUT);
            // Two physical vessels (the stack/pod line and the booster), NOT one row per interval.
            Assert.Equal(2, facts.VesselCount);
            // The crew union is by NAME, so a kerbal riding two legs counts once.
            Assert.Equal(2, facts.CrewCount);
            // The outcome is the PRIMARY vessel's last interval end event ("Landed"), not the
            // booster's ("Destroyed") and not the launch interval's boundary event ("Decoupled").
            Assert.Equal("Landed", facts.TerminalWord);
        }

        [Fact]
        public void ComputeSummaryFacts_ExcludesEvaKerbalsFromTheVesselCount()
        {
            // A kerbal on EVA is its own through-line, but it is a person, not a vessel: fails if
            // an EVA inflates the summary's vessel count.
            BuildModels(
                new[]
                {
                    Leg("L", "C", 0, 0, 60, pods: 1, crewNames: new[] { "Jeb Kerman", "Bob Kerman" }),
                    Leg("cont", "C2", 0, 60, 200, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("bob", "C3", 0, 60, 90, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[] { BP("bp1", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }) },
                out MissionStructure structure, out MissionThroughLineView view,
                out List<MissionCompositionNode> roots);

            MissionPresentation.MissionSummaryFacts facts =
                MissionPresentation.ComputeSummaryFacts(structure, view, roots);

            Assert.Equal(1, facts.VesselCount);
            Assert.Equal(2, facts.CrewCount);   // Jeb + Bob, the EVA kerbal included once
        }

        [Fact]
        public void ComputeSummaryFacts_NullModels_NoSpanNoCounts()
        {
            // Fails if a mission whose tree has no read model throws instead of yielding a blank
            // summary (the header then simply draws no second line).
            MissionPresentation.MissionSummaryFacts facts =
                MissionPresentation.ComputeSummaryFacts(null, null, null);
            Assert.False(facts.HasSpan);
            Assert.Equal(0, facts.VesselCount);
            Assert.Equal(0, facts.CrewCount);
            Assert.Equal("", facts.TerminalWord);
        }

        // ===================== T1.3 - delta-phrased interval labels =====================

        [Fact]
        public void BuildIntervalRowLabel_FirstInterval_KeepsVesselAndComposition()
        {
            // Fails if the vessel's FIRST row loses its name (the row that has to name the ship).
            Assert.Equal("Kerbal X (pod x1, crew x3)", MissionPresentation.BuildIntervalRowLabel(
                "Kerbal X", "pod x1, crew x3", true, "Launch", "Kerbal X Booster"));
        }

        [Fact]
        public void BuildIntervalRowLabel_LaterInterval_LeadsWithTheDelta()
        {
            // Fails if a later interval keeps repeating the vessel name down the staircase.
            Assert.Equal(
                "after undock: Kerbal X Lander left - (pod x1, crew x2)",
                MissionPresentation.BuildIntervalRowLabel(
                    "Kerbal X", "pod x1, crew x2", false, "Undocked", "Kerbal X Lander"));
            Assert.Equal(
                "after decouple: Kerbal X Booster left - (pod x1, crew x3)",
                MissionPresentation.BuildIntervalRowLabel(
                    "Kerbal X", "pod x1, crew x3", false, "Decoupled", "Kerbal X Booster"));
        }

        [Fact]
        public void BuildIntervalRowLabel_UnresolvedOrNonSeparation_KeepsTheOldLabel()
        {
            // Fails if a dock boundary (named in the Start event cell instead) or an unnamed peel
            // produces a half-built delta phrase.
            Assert.Equal("Kerbal X (pod x2, crew x4)", MissionPresentation.BuildIntervalRowLabel(
                "Kerbal X", "pod x2, crew x4", false, "Docked", "Munport Station"));
            Assert.Equal("Kerbal X (pod x1, crew x3)", MissionPresentation.BuildIntervalRowLabel(
                "Kerbal X", "pod x1, crew x3", false, "Undocked", null));
        }

        [Fact]
        public void BuildIntervalRowLabel_AtomOrEvaRow_IsTheBareLabel()
        {
            // A roster atom / EVA kerbal names itself; fails if it renders "Bob Kerman (Bob Kerman)".
            Assert.Equal("Bob Kerman", MissionPresentation.BuildIntervalRowLabel(
                "Bob Kerman", "Bob Kerman", true, "EVA", null));
            Assert.Equal("Pod", MissionPresentation.BuildIntervalRowLabel(
                "Pod", "Pod", true, null, null));
        }

        [Fact]
        public void SeparationVerb_MapsOnlySeparations()
        {
            Assert.Equal("decouple", MissionPresentation.SeparationVerb("Decoupled"));
            Assert.Equal("undock", MissionPresentation.SeparationVerb("Undocked"));
            Assert.Equal("break-off", MissionPresentation.SeparationVerb("Broke off"));
            Assert.Equal("break-up", MissionPresentation.SeparationVerb("Broke up"));
            // Fails if a merge / launch / terminal word starts delta-phrasing.
            Assert.Null(MissionPresentation.SeparationVerb("Docked"));
            Assert.Null(MissionPresentation.SeparationVerb("Boarded"));
            Assert.Null(MissionPresentation.SeparationVerb("Launch"));
            Assert.Null(MissionPresentation.SeparationVerb("EVA"));
            Assert.Null(MissionPresentation.SeparationVerb(null));
        }

        [Fact]
        public void ResolvePeeledSiblingVesselName_FindsThePieceThatLeftAtTheBoundary()
        {
            BuildDecoupleShape(out _, out _, out List<MissionCompositionNode> roots);

            Assert.Single(roots);
            MissionCompositionNode launch = roots[0];
            // The builder chains the survivor as the first child and attaches the peel after it.
            Assert.Equal(2, launch.Children.Count);
            MissionCompositionNode survivor = launch.Children[0];
            Assert.Equal("Decoupled", survivor.StartEvent);
            Assert.NotEqual(survivor.HeadLegId, survivor.OwnerHeadId);   // a LATER interval

            // Fails if the peel is no longer a sibling of the survivor at the same UT (the whole
            // premise of the delta label).
            Assert.Equal("Kerbal X Booster",
                MissionPresentation.ResolvePeeledSiblingVesselName(launch, survivor));

            // End to end: the row the player sees.
            Assert.Equal(
                "after decouple: Kerbal X Booster left - (" + survivor.CompositionLabel + ")",
                MissionPresentation.BuildIntervalRowLabel(
                    survivor.VesselName, survivor.CompositionLabel, false, survivor.StartEvent,
                    MissionPresentation.ResolvePeeledSiblingVesselName(launch, survivor)));
        }

        [Fact]
        public void ResolvePeeledSiblingVesselName_IgnoresSelfAtomsAndOtherUTs()
        {
            var node = new MissionCompositionNode
            {
                HeadLegId = "L/seg1",
                OwnerHeadId = "L",
                VesselName = "Kerbal X",
                CompositionLabel = "pod x1",
                StartUT = 42.0,
                IsSelectable = true,
            };
            var atom = new MissionCompositionNode
            {
                HeadLegId = "L",
                VesselName = "Pod",
                CompositionLabel = "Pod",
                StartUT = 42.0,
                IsAtom = true,
            };
            var otherUt = new MissionCompositionNode
            {
                HeadLegId = "late",
                OwnerHeadId = "late",
                VesselName = "Late Probe",
                CompositionLabel = "probe x1",
                StartUT = 99.0,
                IsSelectable = true,
            };
            var kerbal = new MissionCompositionNode
            {
                HeadLegId = "bob",
                OwnerHeadId = "bob",
                VesselName = "Bob Kerman",
                CompositionLabel = "Bob Kerman",
                StartUT = 42.0,
                IsSelectable = true,
            };
            var parent = new MissionCompositionNode { HeadLegId = "L", OwnerHeadId = "L" };
            parent.Children.Add(node);
            parent.Children.Add(atom);
            parent.Children.Add(otherUt);
            parent.Children.Add(kerbal);

            // Fails if the resolver names the row itself, a roster atom, a peel from a different
            // boundary, or a kerbal that went EVA at the same instant.
            Assert.Null(MissionPresentation.ResolvePeeledSiblingVesselName(parent, node));
            Assert.Null(MissionPresentation.ResolvePeeledSiblingVesselName(null, node));
            Assert.Null(MissionPresentation.ResolvePeeledSiblingVesselName(parent, null));
        }

        // ===================== T1.4 - naming the same-tree dock partner =====================

        // A transport that docks a station recorded in the SAME tree: the Dock branch point lists
        // both parents, and the merged leg continues the transport's line.
        private static void BuildSameTreeDockShape(
            out MissionStructure structure, out MissionThroughLineView view,
            out List<MissionCompositionNode> roots)
        {
            BuildModels(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("station", "C9", 0, 0, 50, probes: 2, vessel: "Munport Station",
                        crewNames: new[] { "Val Kerman" }),
                    Leg("dockedLeg", "C2", 0, 50, 120, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                },
                new[]
                {
                    BP("dockbp", BranchPointType.Dock, new[] { "L", "station" },
                        new[] { "dockedLeg" }),
                },
                out structure, out view, out roots);
        }

        [Fact]
        public void ResolveSameTreeDockPartner_NamesTheOtherParent()
        {
            BuildSameTreeDockShape(out MissionStructure structure, out MissionThroughLineView view,
                out List<MissionCompositionNode> roots);

            // The transport's dock sub-interval (the "@dock" boundary) is the row that names the
            // partner; find it by its Dock start event.
            MissionCompositionNode docked = FindByStartEvent(roots, "Docked");
            Assert.NotNull(docked);

            string partner = MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                structure, view, docked.OwnerHeadId, docked.StartUT);

            // Fails if the merge's second branch parent is no longer reachable from the interval's
            // owner through-line (the rendezvous then reads as an unexplained inventory jump).
            Assert.Equal("Munport Station", partner);
            Assert.Equal("Docked with Munport Station",
                MissionPresentation.BuildDockPartnerStartEventText("Docked", partner));
        }

        [Fact]
        public void ResolveSameTreeDockPartner_SingleParentMerge_NamesNobody()
        {
            // A cross-tree / foreign dock records ONE parent, so there is no same-tree partner to
            // name: fails if the resolver invents one (the row must keep the bare "Docked").
            BuildModels(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("dockedLeg", "C2", 0, 50, 120, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                },
                new[] { BP("dockbp", BranchPointType.Dock, new[] { "L" }, new[] { "dockedLeg" }) },
                out MissionStructure structure, out MissionThroughLineView view,
                out List<MissionCompositionNode> roots);

            MissionCompositionNode docked = FindByStartEvent(roots, "Docked");
            Assert.NotNull(docked);
            Assert.Null(MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                structure, view, docked.OwnerHeadId, docked.StartUT));
        }

        [Fact]
        public void ResolveSameTreeDockPartner_UnknownOwnerOrNullModels_NamesNobody()
        {
            BuildSameTreeDockShape(out MissionStructure structure, out MissionThroughLineView view,
                out _);
            Assert.Null(MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                structure, view, "nope", 50.0));
            Assert.Null(MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                null, view, "L", 50.0));
            Assert.Null(MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                structure, null, "L", 50.0));
            // A UT that is not a merge boundary on that line.
            Assert.Null(MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                structure, view, "L", 12345.0));
        }

        [Fact]
        public void BuildDockPartnerStartEventText_FallsBackToTheBareWord()
        {
            Assert.Equal("Docked", MissionPresentation.BuildDockPartnerStartEventText("Docked", null));
            Assert.Equal("Boarded", MissionPresentation.BuildDockPartnerStartEventText("Boarded", ""));
            Assert.Equal("", MissionPresentation.BuildDockPartnerStartEventText("", "Munport Station"));
        }

        [Fact]
        public void IsDockEventWord_OnlyTheTwoMergeWords()
        {
            Assert.True(MissionPresentation.IsDockEventWord("Docked"));
            Assert.True(MissionPresentation.IsDockEventWord("Boarded"));
            Assert.False(MissionPresentation.IsDockEventWord("Undocked"));
            Assert.False(MissionPresentation.IsDockEventWord(""));
            Assert.False(MissionPresentation.IsDockEventWord(null));
        }

        private static MissionCompositionNode FindByStartEvent(
            List<MissionCompositionNode> roots, string startEvent)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                MissionCompositionNode hit = FindByStartEvent(roots[i], startEvent);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        private static MissionCompositionNode FindByStartEvent(
            MissionCompositionNode node, string startEvent)
        {
            if (node == null)
                return null;
            if (node.StartEvent == startEvent)
                return node;
            for (int i = 0; i < node.Children.Count; i++)
            {
                MissionCompositionNode hit = FindByStartEvent(node.Children[i], startEvent);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        // ===================== T1.5 - the state tooltips =====================

        [Fact]
        public void BuildPeriodStateTooltip_NamesEachState()
        {
            // Fails if a period-cell state stops naming itself (the four states are otherwise
            // distinguished by greyness / editability / tint alone).
            Assert.Equal(MissionPresentation.PeriodTooltipLoopOff,
                MissionPresentation.BuildPeriodStateTooltip(false, false, false, false));
            // Loop off wins over everything else - nothing is running.
            Assert.Equal(MissionPresentation.PeriodTooltipLoopOff,
                MissionPresentation.BuildPeriodStateTooltip(false, true, true, true));
            Assert.Equal(MissionPresentation.PeriodTooltipLocked,
                MissionPresentation.BuildPeriodStateTooltip(true, true, false, false));
            Assert.Equal(MissionPresentation.PeriodTooltipClamped,
                MissionPresentation.BuildPeriodStateTooltip(true, false, true, true));
            Assert.Equal(MissionPresentation.PeriodTooltipAuto,
                MissionPresentation.BuildPeriodStateTooltip(true, false, true, false));
            // Plain manual period: nothing to explain.
            Assert.Null(MissionPresentation.BuildPeriodStateTooltip(true, false, false, false));
        }

        [Fact]
        public void BuildNextLaunchCellTooltip_ExplainsTheStateWordsAndKeepsAmberReasons()
        {
            Assert.Equal(MissionPresentation.NextLaunchTooltipNotAligned,
                MissionPresentation.BuildNextLaunchCellTooltip("not aligned", null));
            Assert.Equal(MissionPresentation.NextLaunchTooltipContinuous,
                MissionPresentation.BuildNextLaunchCellTooltip("continuous", ""));
            // Fails if explaining the state word swallowed the amber reason the cell already had.
            Assert.Equal(
                MissionPresentation.NextLaunchTooltipNotAligned + "; station orbit drifted",
                MissionPresentation.BuildNextLaunchCellTooltip("not aligned", "station orbit drifted"));
            Assert.Equal("station orbit drifted",
                MissionPresentation.BuildNextLaunchCellTooltip("T- 2h 14m", "station orbit drifted"));
            Assert.Null(MissionPresentation.BuildNextLaunchCellTooltip("T- 2h 14m", null));
            Assert.Null(MissionPresentation.BuildNextLaunchCellTooltip("", null));
        }

        [Fact]
        public void IncludeCheckboxTooltip_SaysItIsLoopMembershipNotVisibility()
        {
            // The F2 fix: fails if the tooltip stops correcting the "this hides the ghost"
            // assumption, which is the whole reason it exists.
            Assert.Contains("loop unit", MissionPresentation.IncludeCheckboxTooltip);
            Assert.Contains("Does not hide the ghost", MissionPresentation.IncludeCheckboxTooltip);
            Assert.Contains("Recordings tab", MissionPresentation.IncludeCheckboxTooltip);
        }

        // ===================== T1.6 - the loop-conflict outcome =====================

        [Fact]
        public void BuildLoopMovedScreenMessage_NamesWinnerAndLosers()
        {
            Assert.Equal(
                "Loop moved to 'Munport Resupply' - one loop per recording tree ('Munport Survey' unlooped)",
                MissionPresentation.BuildLoopMovedScreenMessage(
                    "Munport Resupply", new List<string> { "Munport Survey" }));
            Assert.Equal(
                "Loop moved to 'A' - one loop per recording tree ('B', 'C' unlooped)",
                MissionPresentation.BuildLoopMovedScreenMessage(
                    "A", new List<string> { "B", "C" }));
        }

        [Fact]
        public void BuildLoopMovedScreenMessage_NothingCleared_PostsNothing()
        {
            // Fails if enabling a loop that took nothing from anyone still nags the player.
            Assert.Null(MissionPresentation.BuildLoopMovedScreenMessage("A", null));
            Assert.Null(MissionPresentation.BuildLoopMovedScreenMessage("A", new List<string>()));
        }

        [Fact]
        public void BuildLoopMovedScreenMessage_UnnamedMissionsReadAsPlaceholders()
        {
            Assert.Equal(
                "Loop moved to '(mission)' - one loop per recording tree ('(mission)' unlooped)",
                MissionPresentation.BuildLoopMovedScreenMessage("", new List<string> { null }));
        }
    }
}
