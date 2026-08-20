using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // R6 DOUBLE-CLOCK ADVISORY (design-dock-event-graph.md 7.7, open question Q9).
    //
    // Gated on the section-7.8 verification, which CONFIRMED the collision structurally:
    // DoubleClockVerificationTests measured 302 of 801 swept wall UTs rendering the AB docked
    // stretch and B's own recording concurrently, every one of them with the two span clocks
    // 137-237 s apart. Research note: docs/dev/research/double-clock-verification-2026-08-13.md.
    //
    // The advisory is a SENTENCE, not a rule: it fires on loop-ENABLE when the enabling mission's
    // tree is transitively dock-connected to another LOOPING mission's tree, and changes nothing.
    // Turning either loop off is the player's call - the alternative (extending
    // ClearLoopsConflictingWith to graph-connected trees) would regress the pinned
    // "two disjoint-tree missions may loop concurrently" behavior for every harmless case.
    //
    // [Collection("Sequential")] - MissionStore and ParsekLog are process-wide statics.
    [Collection("Sequential")]
    public class DoubleClockAdvisoryTests : IDisposable
    {
        private const string GuidC = "cccccccccccccccccccccccccccccccc";
        private const uint PidC = 300;

        private readonly List<string> logLines = new List<string>();

        public DoubleClockAdvisoryTests()
        {
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;   // the Info audit line is under test
            DockEventGraph.SuppressLogging = true;
            MissionCrossTreeDock.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------------
        // Fixtures
        //
        //   tb: B0 (pid 200)                                  - the partner's own flight
        //   ta: A0 -> Dock(target = pid 200) -> AB -> Undock   - CrossTree pair (ta, tb)
        //   tc: C0 -> Dock(target = pid 200) -> CB             - CrossTree pair (tc, tb)
        //
        // So ta is dock-connected to tb DIRECTLY and to tc only THROUGH tb: the transitive case,
        // and the reason the walk is a BFS rather than a single pair lookup.
        // ------------------------------------------------------------------

        private static RecordingTree ThirdTreeDockingTheSamePartner()
        {
            var c0 = CrossTreeDockFixture.Rec("C0", PidC, GuidC, "CC", 0, 0, 500,
                pods: 1, probes: 0, childBp: "dockbp3", vessel: "Tug C");
            var cb = CrossTreeDockFixture.Rec("CB", PidC, GuidC, "CCB", 0, 500, 600,
                pods: 1, probes: 1, parentBp: "dockbp3", vessel: "Stack CB");
            return CrossTreeDockFixture.Tree("tc", new[] { c0, cb }, new[]
            {
                CrossTreeDockFixture.BP("dockbp3", BranchPointType.Dock, 500,
                    new[] { "C0" }, new[] { "CB" },
                    targetPid: CrossTreeDockFixture.PidB, mergeCause: "DOCK"),
            });
        }

        private static DockEventGraph TwoTreeGraph()
            => DockEventGraph.Build(new List<RecordingTree>
            {
                CrossTreeDockFixture.PartnerTree(), CrossTreeDockFixture.ControllerTree(),
            }, null);

        private static DockEventGraph ThreeTreeGraph()
            => DockEventGraph.Build(new List<RecordingTree>
            {
                CrossTreeDockFixture.PartnerTree(), CrossTreeDockFixture.ControllerTree(),
                ThirdTreeDockingTheSamePartner(),
            }, null);

        // Seeds missions into the store (which has no direct Add) via the codec round-trip the
        // other store tests use, preserving list order. Returns them in the order given.
        private static Mission[] Seed(params (string id, string treeId, string name, bool loops)[] specs)
        {
            var node = new ConfigNode("PARSEK");
            foreach ((string id, string treeId, string name, bool loops) in specs)
            {
                var m = new Mission(id, treeId, name) { LoopPlayback = loops };
                m.Save(node.AddNode("MISSION"));
            }
            MissionStore.Load(node);
            return specs.Select(s => MissionStore.Missions.First(x => x.Id == s.id)).ToArray();
        }

        // ==================================================================
        // Fires
        // ==================================================================

        [Fact]
        public void Fires_ForADirectlyDockConnectedPair_AndNamesTheOtherMission()
        {
            // THE case the verification measured: mission AB (tree ta) starts looping while mission
            // 'CD Freighter' (tree tb) already loops. The AB docked stretch ta replays CONTAINS B's
            // parts; tb replays B's own recording, on its own clock. Regression: shipping the graph
            // without this sentence leaves the player's only clue a vessel that is inexplicably on
            // screen twice.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));

            string advisory = MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, TwoTreeGraph());

            Assert.Equal(
                "'CD Freighter' loops the same docked flight - ghosts may appear twice", advisory);
        }

        [Fact]
        public void Fires_TransitivelyThroughAnIntermediateTree()
        {
            // A docked B, B docked C: A's story and C's can share matter THROUGH B, so the walk
            // must be transitive. Regression: a single-pair lookup would miss exactly the station
            // shape this whole design is about (one hub, several visitors), where two visitors'
            // missions are connected only via the hub.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m3", "tc", "Tug C sortie", true));   // no mission on tb at all

            string advisory = MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, ThreeTreeGraph());

            Assert.Equal(
                "'Tug C sortie' loops the same docked flight - ghosts may appear twice", advisory);
        }

        [Fact]
        public void Fires_WithMultipleConnectedLoopingMissions_NamesTheFirstAndCountsTheRest()
        {
            // Regression: naming every mission would produce an unreadable ScreenMessage, and
            // naming only one without saying there are more would understate the situation. First
            // by the store's stable list order so the sentence does not flicker between draws.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true),
                ("m3", "tc", "Tug C sortie", true));

            string advisory = MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, ThreeTreeGraph());

            Assert.Equal(
                "'CD Freighter' (+1 more) loops the same docked flight - ghosts may appear twice",
                advisory);
        }

        [Fact]
        public void Fires_LogsTheAuditLine()
        {
            // Design 15.2: a player-visible decision leaves one grep-stable line naming both
            // missions and the tree hop, so a "why did it say that" question is answerable from
            // KSP.log alone.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));

            MissionStore.TryDescribeDoubleClockAdvisory(m[0], MissionStore.Missions, TwoTreeGraph());

            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("double-clock advisory: mission='AB'")
                && l.Contains("connectedLooping='CD Freighter'")
                && l.Contains("trees=ta->tb"));
        }

        [Fact]
        public void SuppressLogging_SilencesTheAuditLineButNotTheAdvisory()
        {
            // The predicate is also read from surfaces that may redraw; the sentence is the return
            // value, the log line is the audit, and only the latter can flood.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));
            MissionStore.SuppressLogging = true;

            Assert.NotNull(MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, TwoTreeGraph()));
            Assert.DoesNotContain(logLines, l => l.Contains("double-clock advisory"));
        }

        // ==================================================================
        // Stays silent
        // ==================================================================

        [Fact]
        public void Silent_WhenTheConnectedMissionIsNotLooping()
        {
            // A dock connection alone is not a collision: with only one clock running there is
            // exactly one render of the shared matter. Regression: firing here would put a warning
            // on the single most ordinary action in the Missions tab.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", false));

            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, TwoTreeGraph()));
        }

        [Fact]
        public void Silent_WhenTheOtherLoopingMissionsTreeIsNotDockConnected()
        {
            // Two missions that never met: concurrent loops are correct and pinned behavior. This
            // is the case a hard enforcement rule would have punished.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("mz", "tz", "Unrelated survey", true));

            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, TwoTreeGraph()));
        }

        [Fact]
        public void Silent_WhenTheGraphIsNull()
        {
            // Degradation contract: a host with no graph yet behaves exactly as it does today.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));

            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(m[0], MissionStore.Missions, null));
        }

        [Fact]
        public void Silent_ForASameTreeLoopingSibling()
        {
            // SetLoopEnabled cleared same-tree siblings one line before the advisory is evaluated,
            // so naming one would describe a state that no longer exists. Regression: reporting it
            // would make the hard rule's own effect look like an unresolved warning.
            Mission[] m = Seed(
                ("m1", "ta", "AB", false),
                ("m1b", "ta", "AB (variant)", true));

            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], MissionStore.Missions, TwoTreeGraph()));
        }

        [Fact]
        public void Silent_ForNullOrTreelessInputs()
        {
            // Draw-path total-ness: every argument shape must return, never throw.
            Mission[] m = Seed(("m2", "tb", "CD Freighter", true));
            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                null, MissionStore.Missions, TwoTreeGraph()));
            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                m[0], null, TwoTreeGraph()));
            Assert.Null(MissionStore.TryDescribeDoubleClockAdvisory(
                new Mission("x", null, "Treeless"), MissionStore.Missions, TwoTreeGraph()));
        }

        // ==================================================================
        // The hard rule is UNCHANGED (the advisory adds words, not enforcement)
        // ==================================================================

        [Fact]
        public void SetLoopEnabled_ClearingBehavior_IsUnchanged_OnThePinnedShapes()
        {
            // Re-assert the three pinned outcomes of ClearLoopsConflictingWith so a future
            // temptation to "just also clear the connected tree" reds here first:
            //   same tree            -> cleared (the one-loop-per-tree rule)
            //   cross-tree, link ON  -> cleared (the spanned-set rule)
            //   cross-tree, link OFF -> NOT cleared (the pinned concurrent-loop behavior the
            //                           advisory exists to talk about instead of preventing)
            var trees = new List<RecordingTree>
            {
                CrossTreeDockFixture.PartnerTree(), CrossTreeDockFixture.ControllerTree(),
            };

            Mission[] sameTree = Seed(
                ("m1", "ta", "AB", false),
                ("m1b", "ta", "AB (variant)", true));
            MissionStore.ClearLoopsConflictingWith(
                sameTree[0], trees, out int sameCleared, out int sameCross, "test");
            Assert.Equal(1, sameCleared);
            Assert.Equal(0, sameCross);
            Assert.False(sameTree[1].LoopPlayback);

            Mission[] linkOff = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));
            MissionStore.ClearLoopsConflictingWith(
                linkOff[0], trees, out int offSame, out int offCross, "test");
            Assert.Equal(0, offSame);
            Assert.Equal(0, offCross);
            Assert.True(linkOff[1].LoopPlayback);      // still looping: the collision is allowed

            Mission[] linkOn = Seed(
                ("m1", "ta", "AB", false),
                ("m2", "tb", "CD Freighter", true));
            linkOn[1].IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            MissionStore.ClearLoopsConflictingWith(
                linkOn[0], trees, out int onSame, out int onCross, "test");
            Assert.Equal(0, onSame);
            Assert.Equal(1, onCross);
            Assert.False(linkOn[1].LoopPlayback);
        }

        [Fact]
        public void TheAdvisory_ChangesNoLoopState()
        {
            // The whole point: it is a sentence. Regression: any future "and turn the other one
            // off" would silently undo something the player deliberately switched on.
            Mission[] m = Seed(
                ("m1", "ta", "AB", true),
                ("m2", "tb", "CD Freighter", true));

            MissionStore.TryDescribeDoubleClockAdvisory(m[0], MissionStore.Missions, TwoTreeGraph());

            Assert.True(m[0].LoopPlayback);
            Assert.True(m[1].LoopPlayback);
        }

        // ==================================================================
        // UI wiring (IMGUI is not xUnit-drivable; source-text gate idiom)
        // ==================================================================

        [Fact]
        public void LoopToggle_PostsTheAdvisory_OnEnableOnly_AfterSetLoopEnabled()
        {
            // Regression: evaluating BEFORE the enable would read a store state in which the
            // conflicting loops the hard rule is about to clear are still on, so the sentence could
            // name a mission that is off by the time it renders. Firing on DISABLE would be pure
            // noise. Both are one-line mistakes, hence the gate.
            string src = ReadMissionsWindowSource();

            int enable = src.IndexOf(
                "MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime(),",
                StringComparison.Ordinal);
            Assert.True(enable >= 0, "loop-toggle SetLoopEnabled call site not found");

            int guard = src.IndexOf("if (loopNow)", enable, StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard - enable < 800,
                "the advisory must be guarded on the ENABLE branch, right after SetLoopEnabled");

            int post = src.IndexOf("PostDoubleClockAdvisoryIfAny(mission);", guard,
                StringComparison.Ordinal);
            Assert.True(post >= 0 && post - guard < 200,
                "the advisory post call must sit inside that enable guard");

            // ...and the helper actually routes through the store predicate and ScreenMessages.
            Assert.Contains("MissionStore.TryDescribeDoubleClockAdvisory(", src);
            int helper = src.IndexOf("private void PostDoubleClockAdvisoryIfAny(", StringComparison.Ordinal);
            Assert.True(helper >= 0, "advisory helper not found");
            int screen = src.IndexOf("ScreenMessages.PostScreenMessage(", helper, StringComparison.Ordinal);
            Assert.True(screen >= 0 && screen - helper < 1200,
                "the advisory helper must post exactly one ScreenMessage");
        }

        [Fact]
        public void LinkToggle_DoesNotPostTheAdvisory()
        {
            // Verified rather than assumed (design 7.7's second half): including a partner-journey
            // link on a looping mission calls ClearLoopsConflictingWith, which turns the foreign
            // tree's loop OFF - so the two-concurrent-loops state cannot survive that path and an
            // advisory there would always be stale.
            string src = ReadMissionsWindowSource();

            int add = src.IndexOf(
                "mission.IncludedForeignDockLinkIds.Add(link.LinkId);", StringComparison.Ordinal);
            Assert.True(add >= 0, "partner-journey include mutation site not found");
            int clear = src.IndexOf(
                "MissionStore.ClearLoopsConflictingWith(mission,", add, StringComparison.Ordinal);
            Assert.True(clear >= 0 && clear - add < 900,
                "the link-include path must still clear conflicting loops");

            int advisoryInBlock = src.IndexOf(
                "PostDoubleClockAdvisoryIfAny", add, StringComparison.Ordinal);
            Assert.True(advisoryInBlock < 0 || advisoryInBlock - add > 2000,
                "the link-include toggle must not post the double-clock advisory");
        }

        private static string ReadMissionsWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "MissionsWindowUI.cs");
            Assert.True(File.Exists(path), $"MissionsWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }
    }
}
