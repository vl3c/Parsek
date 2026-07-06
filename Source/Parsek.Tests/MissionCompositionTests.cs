using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Tests for the "vessel composition over time" read model (MissionCompositionBuilder):
    // composition labels, env-split grouping, branching at splits/EVA, and terminal leaves.
    public class MissionCompositionTests
    {
        // Builds a controlled leg with an explicit composition (controllers by type + crew).
        private static Recording Leg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0, int seats = 0, int crew = 0,
            string eva = null, string parentAnchor = null, string vessel = "Kerbal X",
            string[] crewNames = null)
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
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            for (int i = 0; i < seats; i++) controllers.Add(new ControllerInfo { type = "ExternalSeat" });
            if (!string.IsNullOrEmpty(eva)) controllers.Add(new ControllerInfo { type = "KerbalEVA" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            // StartCrew (per-trait counts) only from the explicit count; crew NAMES go in
            // CrewEndStates WITHOUT StartCrew, mirroring the real saves (which carry the named
            // roster but no StartCrew node) so the name-count fallback is exercised.
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

        private static List<MissionCompositionNode> Build(Recording[] recs, BranchPoint[] bps = null)
            => MissionCompositionBuilder.Build(MissionStructureBuilder.Build(Tree(recs, bps)));

        [Fact]
        public void FormatComposition_OmitsZeros_UsesXn()
        {
            var leg = new MissionLeg { PodCount = 1, ProbeCount = 1, CrewCount = 3 };
            Assert.Equal("pod x1, probe x1, crew x3", MissionCompositionBuilder.FormatComposition(leg));

            var probe = new MissionLeg { ProbeCount = 1 };
            Assert.Equal("probe x1", MissionCompositionBuilder.FormatComposition(probe));

            var evaLeg = new MissionLeg { EvaCrewName = "Bob Kerman" };
            Assert.Equal("Bob Kerman", MissionCompositionBuilder.FormatComposition(evaLeg));
        }

        [Fact]
        public void SingleUncrewedController_IsLeaf()
        {
            var set = Build(new[] { Leg("p", "C", 0, 0, 100, probes: 1) });
            Assert.Single(set);
            Assert.Equal("probe x1", set[0].CompositionLabel);
            Assert.True(set[0].IsLeaf);
            Assert.Empty(set[0].Children);
        }

        [Fact]
        public void CrewedTerminal_IsMultiAtom_ExpandsToAtoms()
        {
            var set = Build(new[] { Leg("pod", "C", 0, 0, 100, pods: 1, crew: 3) });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
            Assert.False(set[0].IsLeaf);
            // One Pod atom + one crew atom.
            Assert.Equal(2, set[0].Children.Count);
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Pod");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "crew x3");
        }

        [Fact]
        public void CrewCount_FallsBackToCrewNames_WhenNoStartCrew()
        {
            // Real saves carry CrewEndStates names but no StartCrew; the count must still show.
            var set = Build(new[] { Leg("pod", "C", 0, 0, 100, pods: 1, crewNames: new[] { "Jeb", "Bill", "Bob" }) });
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
        }

        [Fact]
        public void StaleVessel_PeelsAnchoredProbe_SynthesizesContinuingRemainder()
        {
            // The exact reported shape: launch pod+probe+crew3, the probe separates as an ANCHORED
            // controlled offshoot, and the pod keeps the SAME recording (no recaptured continuation
            // leg, so its Controllers stay [pod,probe]). The builder must synthesize the remaining
            // "Kerbal X (pod x1, crew x3)" continuing vessel alongside the peeled probe.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 200, pods: 1, probes: 1, crewNames: new[] { "Jeb", "Bill", "Bob" }),
                    Leg("probe", "C2", 0, 60, 115, probes: 1, parentAnchor: "L", vessel: "Kerbal X Probe"),
                },
                new[] { BP("bp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, splitCause: "DECOUPLE") });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Decoupled", root.EndEvent); // the probe decouple ends the launch composition
            Assert.Equal(2, root.Children.Count);
            Assert.Equal("pod x1, crew x3", root.Children[0].CompositionLabel); // synthesized remainder, first
            Assert.Equal("Kerbal X", root.Children[0].VesselName);
            Assert.Equal("probe x1", root.Children[1].CompositionLabel);         // the peeled probe
            Assert.Equal("Decoupled", root.Children[1].StartEvent);             // DECOUPLE -> "Decoupled"
            Assert.True(root.Children[1].IsLeaf);
        }

        [Fact]
        public void KerbalX_ProbeDecouples_ThenLaterEva_SurvivorStartsAtDecoupleSpansEva()
        {
            // The exact real Kerbal X shape: a pod+probe+crew3 launch decouples its probe early
            // (29.56) and the same recording keeps going until a LATER EVA (38.88) where one crew
            // leaves. The continuing pod must read as ONE survivor interval that STARTS at the
            // decouple and SPANS the EVA (label = surviving crew x2), with the EVA kerbal hanging
            // off the survivor (not the launch), and the launch interval ending at the decouple.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 122, pods: 1, probes: 1,
                        crewNames: new[] { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" }),
                    Leg("probe", "C2", 0, 29.56, 84, probes: 1, parentAnchor: "L", vessel: "Kerbal X Probe"),
                    Leg("cont", "C3", 0, 38.88, 200, pods: 1, crewNames: new[] { "Jebediah Kerman", "Bill Kerman" }),
                    Leg("bob", "C4", 0, 38.88, 150, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, splitCause: "DECOUPLE"),
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }),
                });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            // Launch interval: full stack, ends at the probe decouple (not the later EVA).
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Launch", root.StartEvent);
            Assert.Equal("Decoupled", root.EndEvent);
            Assert.Equal(29.56, root.EndUT);
            Assert.Equal(2, root.Children.Count);

            // Survivor (continuing pod): starts at the decouple, spans the EVA, label = crew x2.
            MissionCompositionNode survivor = root.Children[0];
            Assert.Equal("Kerbal X", survivor.VesselName);
            Assert.Equal("pod x1, crew x2", survivor.CompositionLabel);
            Assert.Equal("Decoupled", survivor.StartEvent);   // born at the decouple, NOT the EVA
            Assert.Equal(29.56, survivor.StartUT);
            Assert.Equal(200.0, survivor.EndUT);              // continues to the recording terminal

            // The EVA kerbal hangs off the survivor; the EVA is the KERBAL's start event.
            Assert.Single(survivor.Children);
            Assert.Equal("Bob Kerman", survivor.Children[0].CompositionLabel);
            Assert.Equal("EVA", survivor.Children[0].StartEvent);
            Assert.True(survivor.Children[0].IsLeaf);

            // The peeled probe is the launch interval's second child.
            Assert.Equal("probe x1", root.Children[1].CompositionLabel);
            Assert.Equal("Decoupled", root.Children[1].StartEvent);
            Assert.True(root.Children[1].IsLeaf);
        }

        [Fact]
        public void CrewedTerminal_WithNames_ExpandsToNamedCrewLeaves()
        {
            var set = Build(new[]
            {
                Leg("pod", "C", 0, 0, 100, pods: 1, crewNames: new[] { "Bob Kerman", "Bill Kerman", "Jeb Kerman" }),
            });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel); // label still uses counts
            Assert.False(set[0].IsLeaf);
            // One Pod atom + one named leaf per crew member (no "crew x3" count atom).
            Assert.Equal(4, set[0].Children.Count);
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Pod");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Bob Kerman");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Bill Kerman");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Jeb Kerman");
            Assert.DoesNotContain(set[0].Children, c => c.CompositionLabel == "crew x3");
        }

        [Fact]
        public void EnvSplit_SameComposition_CollapsesToOneInterval()
        {
            // Two legs of the same vessel/chain, same composition: one interval, no extra node.
            var set = Build(new[]
            {
                Leg("a1", "C", 0, 0, 50, pods: 1, crew: 3),
                Leg("a2", "C", 1, 50, 120, pods: 1, crew: 3),
            });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
            Assert.Equal(0.0, set[0].StartUT);
            Assert.Equal(120.0, set[0].EndUT); // spans both env-split legs
        }

        [Fact]
        public void Decouple_BranchesIntoContinuingPlusPeeledProbe()
        {
            // Launch stack pod+probe+crew3 -> Undock -> continuing pod+crew3 + peeled probe.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 42, pods: 1, probes: 1, crew: 3),
                    Leg("cont", "C2", 0, 42, 200, pods: 1, crew: 3),          // continuing (not anchored)
                    Leg("probe", "C3", 0, 42, 115, probes: 1, parentAnchor: "L"), // peeled (anchored offshoot)
                },
                new[] { BP("bp1", BranchPointType.JointBreak, new[] { "L" }, new[] { "cont", "probe" }, splitCause: "DECOUPLE") });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Launch", root.StartEvent);
            Assert.Equal("Decoupled", root.EndEvent); // DECOUPLE cause -> "Decoupled", not "Broke off"
            Assert.Equal(2, root.Children.Count);

            // Continuing vessel first, then the peeled probe.
            Assert.Equal("pod x1, crew x3", root.Children[0].CompositionLabel);
            Assert.Equal("Decoupled", root.Children[0].StartEvent);
            Assert.Equal("probe x1", root.Children[1].CompositionLabel);
            Assert.True(root.Children[1].IsLeaf);
        }

        [Fact]
        public void Eva_KeepsOneInterval_LabelIsSurvivingCrew_KerbalHangsOff()
        {
            // A crew-only EVA does NOT start a new vessel interval: the pod continues with the
            // same controllers, so it stays ONE interval labeled by the SURVIVING crew (x2), and
            // the kerbal that left hangs off it as a leaf whose own start event is the EVA.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 60, pods: 1, crew: 3),
                    Leg("cont", "C2", 0, 60, 200, pods: 1, crew: 2),       // continuing, crew dropped
                    Leg("bob", "C3", 0, 60, 90, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[] { BP("bp1", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }) });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, crew x2", root.CompositionLabel); // surviving crew, not x3
            Assert.Equal("Launch", root.StartEvent);                // born at launch, spans the EVA
            Assert.Single(root.Children);
            Assert.Equal("Bob Kerman", root.Children[0].CompositionLabel);
            Assert.Equal("EVA", root.Children[0].StartEvent);       // EVA is the kerbal's start
            Assert.True(root.Children[0].IsLeaf);                   // an EVA kerbal is a single atom
        }

        [Fact]
        public void EmptyStructure_YieldsNoRoots()
        {
            Assert.Empty(MissionCompositionBuilder.Build(MissionStructureBuilder.Build(new RecordingTree { Id = "t" })));
        }

        // --- Interval-level selection -> per-vessel render windows (start/end trim) ---

        // The full Kerbal X composition tree as roots, for the interval-selection tests below.
        // Intervals: "L" (launch stack, [0,29.56]) + "L/seg1" (post-decouple survivor, [29.56,200])
        // share owner "L"; "probe" ([29.56,84]) and "bob" ([38.88,150]) are their own through-lines.
        private static List<MissionCompositionNode> BuildKerbalX()
            => Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 122, pods: 1, probes: 1,
                        crewNames: new[] { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" }),
                    Leg("probe", "C2", 0, 29.56, 84, probes: 1, parentAnchor: "L", vessel: "Kerbal X Probe"),
                    Leg("cont", "C3", 0, 38.88, 200, pods: 1, crewNames: new[] { "Jebediah Kerman", "Bill Kerman" }),
                    Leg("bob", "C4", 0, 38.88, 150, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, splitCause: "DECOUPLE"),
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }),
                });

        [Fact]
        public void IntervalSelection_AllIncluded_FullWindowPerVessel()
        {
            var w = MissionIntervalSelection.ComputeRenderWindows(BuildKerbalX(), new HashSet<string>());
            Assert.Equal(3, w.Count); // pod (L), probe, bob
            Assert.Equal(0.0, w["L"].StartUT);    // pod spans launch
            Assert.Equal(200.0, w["L"].EndUT);    // ... to terminal (trunk + survivor)
            Assert.True(w.ContainsKey("probe"));
            Assert.True(w.ContainsKey("bob"));
        }

        [Fact]
        public void IntervalSelection_ExcludeLaunchInterval_StartTrimsPodToDecouple()
        {
            // Case 2: drop the launch-stack interval, keep the post-decouple survivor -> the pod's
            // render window now STARTS at the decouple (the launch/ascent is trimmed off).
            var w = MissionIntervalSelection.ComputeRenderWindows(
                BuildKerbalX(), new HashSet<string> { "L" });
            Assert.Equal(29.56, w["L"].StartUT); // not 0 (launch) anymore
            Assert.Equal(200.0, w["L"].EndUT);
        }

        [Fact]
        public void IntervalSelection_ExcludeAllPodIntervals_DropsPod_KeepsBranchesIndependently()
        {
            var w = MissionIntervalSelection.ComputeRenderWindows(
                BuildKerbalX(), new HashSet<string> { "L", "L/seg1" });
            Assert.False(w.ContainsKey("L")); // pod fully dropped (no included interval)
            Assert.True(w.ContainsKey("probe")); // a peeled branch is independent of its trunk
            Assert.True(w.ContainsKey("bob"));
        }

        [Fact]
        public void IntervalSelection_OnlyProbe_RendersJustTheBooster()
        {
            // Case 1: keep only the peeled "Kerbal X Probe" branch, drop the pod and the EVA kerbal.
            var w = MissionIntervalSelection.ComputeRenderWindows(
                BuildKerbalX(), new HashSet<string> { "L", "L/seg1", "bob" });
            Assert.Single(w);
            Assert.True(w.ContainsKey("probe"));
            Assert.Equal(29.56, w["probe"].StartUT); // booster span starts at the separation
        }

        [Fact]
        public void IntervalSelection_NullRoots_ReturnsEmpty()
        {
            Assert.Empty(MissionIntervalSelection.ComputeRenderWindows(null, new HashSet<string>()));
        }

        // --- M-MIS-5: dock/board merges as interval boundaries (D1/D2/D3) ---

        // The worked shuttle shape: a crewed freighter docks a probe depot, loads, and undocks
        // with the depot piece departing. The merged leg carries the recorder's fresh COMBINED
        // capture (pod + the depot's probes + both crews); the depot's controllers exceed the
        // head's (head has 0 probes, depot has 2), pinning the clamp-at-0 artifact fix.
        //   L         [0..50]   pod x1, crew [Jeb]                 (transport solo; ROOT)
        //   dockedLeg [50..80]  pod x1, probe x2, crew [Jeb, Val]  (Dock BP@50; combined capture)
        //   cont      [80..120] pod x1, crew [Jeb]                 (Undock BP@80; transport continues)
        //   depot     [80..90]  probe x2, crew [Val]               (the departing depot piece)
        private static List<MissionCompositionNode> BuildDockShuttle()
            => Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("dockedLeg", "C2", 0, 50, 80, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                    Leg("cont", "C3", 0, 80, 120, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("depot", "C4", 0, 80, 90, probes: 2, vessel: "Depot",
                        crewNames: new[] { "Val Kerman" }),
                },
                new[]
                {
                    BP("dockbp", BranchPointType.Dock, new[] { "L" }, new[] { "dockedLeg" }),
                    BP("undockbp", BranchPointType.Undock,
                        new[] { "dockedLeg" }, new[] { "cont", "depot" }, splitCause: "UNDOCK"),
                });

        // catches: the dock merge staying invisible to the interval model (gap 1) - the docked
        // stretch welded to the launch interval instead of splitting at the merge UT.
        [Fact]
        public void Dock_EmitsIntervalEdgeAtMergeUT_ContinuingLineSplits()
        {
            var set = BuildDockShuttle();
            Assert.Single(set);
            MissionCompositionNode root = set[0];

            // Pre-dock interval: the solo transport, ending AT the dock.
            Assert.Equal("L", root.HeadLegId);
            Assert.Equal(0.0, root.StartUT);
            Assert.Equal(50.0, root.EndUT);
            Assert.Equal("Docked", root.EndEvent);

            // The docked sub-interval is the chained first child: starts at the merge UT,
            // keyed as an @dock sub-interval, same owner (one render window per vessel),
            // independently selectable.
            MissionCompositionNode docked = root.Children[0];
            Assert.Equal("L@dock1", docked.HeadLegId);
            Assert.Equal(50.0, docked.StartUT);
            Assert.Equal(80.0, docked.EndUT);
            Assert.Equal("Docked", docked.StartEvent);
            Assert.Equal("Undocked", docked.EndEvent);
            Assert.Equal("L", docked.OwnerHeadId);
            Assert.True(docked.IsSelectable);
        }

        // catches: the docked-label undercount (gap 2) - the docked stretch reading the HEAD
        // leg's counts (pod x1, crew x1) instead of the merged combined vessel.
        [Fact]
        public void Dock_DockedIntervalLabel_UsesMergedLegComposition()
        {
            var set = BuildDockShuttle();
            MissionCompositionNode docked = set[0].Children[0];
            Assert.Equal("pod x1, probe x2, crew x2", docked.CompositionLabel);
        }

        // catches: the post-undock subtraction operating against a base that never contained
        // the departing depot (pre-M-MIS-5 the clamp at 0 silently hid probe 0-2; with the D2
        // rebase the base contains the depot's probes and the subtraction lands exactly).
        [Fact]
        public void Undock_AfterDock_SubtractsDepartingFromRebasedBase()
        {
            var set = BuildDockShuttle();
            MissionCompositionNode docked = set[0].Children[0];
            MissionCompositionNode postUndock = docked.Children[0]; // chained survivor interval
            Assert.Equal("L/seg1", postUndock.HeadLegId);
            Assert.Equal(80.0, postUndock.StartUT);
            Assert.Equal(120.0, postUndock.EndUT);
            // pod x1 (kept), probe x2 - x2 = 0 (exact, not clamped), crew back to 1.
            Assert.Equal("pod x1, crew x1", postUndock.CompositionLabel);

            // The departing depot hangs off the docked interval it separated from.
            Assert.Contains(docked.Children, c => c.HeadLegId == "depot"
                && c.CompositionLabel == "probe x2, crew x1"
                && c.StartEvent == "Undocked");
        }

        // catches (verdict C2, blocker): a structural peel on a REBASED base subtracting only
        // controllers - the rebased roster contains the partner's crew, so the post-undock
        // interval would keep the departed depot crew (Val) in the roster, a crew overcount
        // D2 itself would introduce.
        [Fact]
        public void Undock_AfterDock_RemovesDepartingCrewFromRoster()
        {
            var set = BuildDockShuttle();
            MissionCompositionNode postUndock = set[0].Children[0].Children[0];
            Assert.Equal("L/seg1", postUndock.HeadLegId);

            // Label counts the surviving roster only (Jeb), not the departed Val.
            Assert.Contains("crew x1", postUndock.CompositionLabel);
            // Terminal atom expansion names the survivor and NOT the departed partner crew.
            Assert.Contains(postUndock.Children, c => c.IsAtom && c.CompositionLabel == "Jeb Kerman");
            Assert.DoesNotContain(postUndock.Children,
                c => c.IsAtom && c.CompositionLabel == "Val Kerman");
        }

        // catches: a re-boarded kerbal staying subtracted forever (the pre-M-MIS-5 roster had
        // no re-board path; the Board merge rebase brings the kerbal back).
        [Fact]
        public void Board_ReboardedKerbal_RejoinsRosterAfterBoardEdge()
        {
            // L [0..30] pod+2 crew; Bob EVAs at 10 (crew peel, no interval split); Bob re-boards
            // at 20 via a Board merge whose leg carries the fresh combined capture (Bob back).
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 30, pods: 1, crewNames: new[] { "Bob Kerman", "Jeb Kerman" }),
                    Leg("bob", "C2", 0, 10, 20, eva: "Bob Kerman", parentAnchor: "L"),
                    Leg("boarded", "C3", 0, 20, 60, pods: 1,
                        crewNames: new[] { "Bob Kerman", "Jeb Kerman" }),
                },
                new[]
                {
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "bob" }),
                    BP("board", BranchPointType.Board, new[] { "L", "bob" }, new[] { "boarded" }),
                });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            // Pre-board interval: roster surviving at its end (Bob on EVA) = crew x1.
            Assert.Equal("pod x1, crew x1", root.CompositionLabel);
            Assert.Equal("Boarded", root.EndEvent);

            // Post-board sub-interval: rebased to the boarded leg's roster - Bob rejoins.
            MissionCompositionNode boarded = root.Children.Find(c => c.HeadLegId == "L@dock1");
            Assert.NotNull(boarded);
            Assert.Equal("Boarded", boarded.StartEvent);
            Assert.Equal("pod x1, crew x2", boarded.CompositionLabel);
            Assert.Contains(boarded.Children, c => c.IsAtom && c.CompositionLabel == "Bob Kerman");
        }

        // catches: a merge UT coincident with a structural edge minting a zero-width @dock
        // sub-interval / key (structural identity must win), or the rebase being lost with it.
        [Fact]
        public void MergeEdge_CoincidentWithStructuralPeelUT_NoSubInterval()
        {
            // The probe decouples at 50 and the dock lands at the SAME UT: the interval edge at
            // 50 is structural, no @dock key is minted, and the post-50 interval still rebases
            // to the merged leg's combined composition.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, probes: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("boost", "C2", 0, 50, 70, probes: 1, parentAnchor: "L", vessel: "Booster"),
                    Leg("dockedLeg", "C3", 0, 50, 90, pods: 1, probes: 1,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "boost" },
                        splitCause: "DECOUPLE"),
                    BP("dockbp", BranchPointType.Dock, new[] { "L" }, new[] { "dockedLeg" }),
                });

            Assert.Single(set);
            var keys = new List<string>();
            CollectSelectableKeys(set[0], keys);
            Assert.DoesNotContain(keys, k => k.Contains("@dock"));
            Assert.Contains("L", keys);
            Assert.Contains("L/seg1", keys);

            // The rebase still applies from the coincident boundary on.
            MissionCompositionNode seg1 = set[0].Children.Find(c => c.HeadLegId == "L/seg1");
            Assert.NotNull(seg1);
            Assert.Equal("pod x1, probe x1, crew x2", seg1.CompositionLabel);
        }

        // catches (D3, key stability): a dock edge participating in the "/segN" ordinal
        // numbering - the post-undock structural interval must stay "/seg2" (numbered over
        // structural edges only), not shift to "/seg3" because a dock edge appeared before it.
        [Fact]
        public void DockEdges_NeverRenumberStructuralSegKeys()
        {
            // Structural edges: decouple@30 + undock@80 -> L, L/seg1, L/seg2. The dock edge at
            // 50 subdivides L/seg1 into L/seg1 + L/seg1@dock1 without renumbering anything.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, probes: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("boost", "C2", 0, 30, 40, probes: 1, parentAnchor: "L", vessel: "Booster"),
                    Leg("dockedLeg", "C3", 0, 50, 80, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                    Leg("cont", "C4", 0, 80, 120, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("depot", "C5", 0, 80, 90, probes: 2, vessel: "Depot",
                        crewNames: new[] { "Val Kerman" }),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "boost" },
                        splitCause: "DECOUPLE"),
                    BP("dockbp", BranchPointType.Dock, new[] { "L" }, new[] { "dockedLeg" }),
                    BP("undockbp", BranchPointType.Undock,
                        new[] { "dockedLeg" }, new[] { "cont", "depot" }, splitCause: "UNDOCK"),
                });

            Assert.Single(set);
            var keys = new List<string>();
            CollectSelectableKeys(set[0], keys);

            // Exact selectable key set: structural ordinals untouched, the dock stretch keyed
            // as a sub-interval of its structural parent.
            Assert.Contains("L", keys);
            Assert.Contains("L/seg1", keys);
            Assert.Contains("L/seg1@dock1", keys);
            Assert.Contains("L/seg2", keys);   // post-undock interval did NOT renumber to /seg3
            Assert.Contains("boost", keys);
            Assert.Contains("depot", keys);
            Assert.DoesNotContain("L/seg3", keys);
            Assert.Equal(6, keys.Count);
        }

        // The constitutional off-gate at the selection layer: a dock tree with EMPTY exclusions
        // produces the IDENTICAL per-vessel render windows the unsplit intervals produced
        // (subdividing preserves both endpoints; merge edges never move a window).
        [Fact]
        public void IntervalSelection_DockTree_EmptyExclusions_WindowsEqualUnsplit()
        {
            var w = MissionIntervalSelection.ComputeRenderWindows(
                BuildDockShuttle(), new HashSet<string>());
            Assert.Equal(2, w.Count);              // transport (L-owned) + the departed depot
            Assert.Equal(0.0, w["L"].StartUT);     // whole-journey window, endpoints unmoved
            Assert.Equal(120.0, w["L"].EndUT);
            Assert.Equal(80.0, w["depot"].StartUT);
            Assert.Equal(90.0, w["depot"].EndUT);
        }

        // The headline M-MIS-5 capability at the selection layer: excluding the pre-dock lead
        // interval start-trims the vessel's render window to the dock UT.
        [Fact]
        public void IntervalSelection_ExcludePreDockInterval_StartTrimsToDockUT()
        {
            var w = MissionIntervalSelection.ComputeRenderWindows(
                BuildDockShuttle(), new HashSet<string> { "L" });
            Assert.Equal(50.0, w["L"].StartUT); // starts AT the dock, not the launch
            Assert.Equal(120.0, w["L"].EndUT);
        }

        // Depth-first selectable-key collector for the key-scheme assertions.
        private static void CollectSelectableKeys(MissionCompositionNode node, List<string> keys)
        {
            if (node == null) return;
            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId))
                keys.Add(node.HeadLegId);
            for (int i = 0; i < node.Children.Count; i++)
                CollectSelectableKeys(node.Children[i], keys);
        }
    }

    // M-MIS-5 D2/D6 logging contract: the additive fallback (a merge leg with no composition of
    // its own) must engage loudly. Sequential: captures ParsekLog via TestSinkForTesting.
    [Collection("Sequential")]
    public class MissionCompositionDockLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionCompositionDockLoggingTests()
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

        // catches (D2 fallback + D6 fail-closed labels): a merge leg carrying NO composition
        // (legacy / failed StartRecording capture) must fall back to ADDITIVE - the running base
        // plus the OTHER parent legs' start compositions - and log the engagement, never render
        // a blank docked label.
        [Fact]
        public void MergedLeg_EmptyComposition_AdditiveFallbackLogged()
        {
            // Two-parent dock: the transport (L) and a recorded station line merge into an
            // EMPTY-composition dockedLeg. The fallback adds the station's start composition.
            var tree = new RecordingTree { Id = "t-fallback", RootRecordingId = "L" };
            var recs = new[]
            {
                MakeLeg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                MakeLeg("station", "S", 0, 20, 50, probes: 1, vessel: "Station",
                    crewNames: new[] { "Val Kerman" }),
                MakeLeg("dockedLeg", "C2", 0, 50, 90), // EMPTY composition -> fallback engages
            };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dockbp",
                Type = BranchPointType.Dock,
                UT = 50,
                ParentRecordingIds = new List<string> { "L", "station" },
                ChildRecordingIds = new List<string> { "dockedLeg" },
            });

            var roots = MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree));

            // The transport root's docked sub-interval: previous base (pod x1, Jeb) plus the
            // station partner's start composition (probe x1, Val).
            MissionCompositionNode transport = roots.Find(r => r.HeadLegId == "L");
            Assert.NotNull(transport);
            MissionCompositionNode docked = transport.Children.Find(c => c.HeadLegId == "L@dock1");
            Assert.NotNull(docked);
            Assert.Equal("pod x1, probe x1, crew x2", docked.CompositionLabel);

            // The engagement is logged with the merge leg id, and the build summary counts it.
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("additive fallback engaged") && l.Contains("dockedLeg"));
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("BuildComposition:") && l.Contains("additiveFallbacks=1")
                && l.Contains("mergeEdges=1"));
        }

        private static Recording MakeLeg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0, string vessel = "Kerbal X", string[] crewNames = null)
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
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            if (crewNames != null)
            {
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
                foreach (var n in crewNames) rec.CrewEndStates[n] = default(KerbalEndState);
                rec.CrewEndStatesResolved = true;
            }
            return rec;
        }
    }
}
