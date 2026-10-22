using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS: a crewed part with no command
    /// module (a Hitchhiker) leaves the recorded vessel with a kerbal inside. The recorded
    /// vessel flies on (a breakup-continuous parent), so the kerbal is in its start snapshot
    /// and missing from its end snapshot.
    /// </summary>
    [Collection("Sequential")]
    public class CrewedCommandlessSplitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bob = "Bob Kerman";
        private const string Tree = "tree-hh";
        private const string Parent = "rec-hh-pod";
        private const string Cabin = "rec-hh-cabin";
        private const string Bp = "bp-hh";
        private const double StartUT = 100.0;
        private const double SplitUT = 120.0;
        private const double EndUT = 300.0;

        public CrewedCommandlessSplitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static void CommitDecoupledCabin(bool cabinIsDebris, TerminalState? cabinTerminal, double cabinEndUT,
            TerminalState parentTerminal)
        {
            var parent = new Recording
            {
                RecordingId = Parent,
                VesselName = "HH1 Hitchhiker Drop",
                TreeId = Tree,
                GhostVisualSnapshot = CrewSnapshot(Jeb, Bob),
                VesselSnapshot = parentTerminal == TerminalState.Destroyed ? null : CrewSnapshot(Jeb),
                ExplicitStartUT = StartUT,
                ExplicitEndUT = EndUT,
                ChildBranchPointId = Bp,
                TerminalStateValue = parentTerminal,
            };
            var cabin = new Recording
            {
                RecordingId = Cabin,
                VesselName = "HH1 Hitchhiker Drop Debris",
                TreeId = Tree,
                ParentBranchPointId = Bp,
                IsDebris = cabinIsDebris,
                GhostVisualSnapshot = CrewSnapshot(Bob),
                VesselSnapshot = cabinTerminal == TerminalState.Recovered ? null : CrewSnapshot(Bob),
                ExplicitStartUT = SplitUT,
                ExplicitEndUT = cabinEndUT,
                TerminalStateValue = cabinTerminal,
            };
            var tree = new RecordingTree
            {
                Id = Tree,
                TreeName = "HH1 Hitchhiker Drop",
                RootRecordingId = Parent,
                ActiveRecordingId = Parent,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = Bp,
                UT = SplitUT,
                Type = BranchPointType.JointBreak,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { Parent },
                ChildRecordingIds = new List<string> { Cabin },
            });
            foreach (var rec in new[] { parent, cabin })
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
        }

        private static GameAction Row(string recordingId, string kerbal)
        {
            foreach (var a in Ledger.Actions)
            {
                if (a.Type == GameActionType.KerbalAssignment && a.RecordingId == recordingId
                    && a.KerbalName == kerbal)
                    return a;
            }
            return null;
        }

        private static void CommitDecoupledCabin(TerminalState parentTerminal)
        {
            CommitDecoupledCabin(cabinIsDebris: false, cabinTerminal: TerminalState.Landed,
                cabinEndUT: EndUT, parentTerminal: parentTerminal);
        }

        // catches: the pod's recording, which flew on after the Hitchhiker left with Bob,
        // reading him as lost (start snapshot has him, end snapshot does not) - a permanent
        // death of a kerbal sitting alive in a real cabin.
        [Fact]
        public void DecoupledCabin_KerbalWhoLeft_IsNotDead_AndTheCabinHoldsHim()
        {
            CommitDecoupledCabin(TerminalState.Landed);
            Walk();

            Assert.Equal(KerbalEndState.Unknown, Row(Parent, Bob).KerbalEndStateField);
            Assert.Equal(KerbalEndState.Aboard, Row(Parent, Jeb).KerbalEndStateField);
            Assert.Equal(KerbalEndState.Aboard, Row(Cabin, Bob).KerbalEndStateField);

            var kerbals = LedgerOrchestrator.Kerbals;
            var r = kerbals.Reservations[Bob];
            Assert.False(r.IsPermanent);
            Assert.True(double.IsNaN(r.DeathRespawnUT));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("crew='Bob Kerman'") && l.Contains("left through a split child"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by mid-flight split handoff: 'Bob Kerman'")
                && l.Contains("bp='" + Bp + "'") && l.Contains("child='" + Cabin + "'"));
        }

        // catches: a pod that crashes AFTER the cabin left taking Bob down with it.
        [Fact]
        public void DecoupledCabin_PodDestroyedLater_KerbalWhoLeftSurvives()
        {
            CommitDecoupledCabin(TerminalState.Destroyed);
            Walk();

            Assert.Equal(KerbalEndState.Dead, Row(Parent, Jeb).KerbalEndStateField);
            Assert.Equal(KerbalEndState.Unknown, Row(Parent, Bob).KerbalEndStateField);
            Assert.False(LedgerOrchestrator.Kerbals.Reservations[Bob].IsPermanent);
        }

        // catches: the cabin's own end deciding nothing (a recovered cabin must free Bob, not
        // leave the pod's hold open-ended for ever).
        [Fact]
        public void DecoupledCabin_Recovered_FreesTheKerbalAtTheRecovery()
        {
            CommitDecoupledCabin(cabinIsDebris: false, cabinTerminal: TerminalState.Recovered,
                cabinEndUT: 250.0, parentTerminal: TerminalState.Landed);
            Walk();

            var r = LedgerOrchestrator.Kerbals.Reservations[Bob];
            Assert.False(r.IsPermanent);
            Assert.Equal(250.0, r.ReservedUntilUT);
        }

        [Fact]
        public void ApplySplitDeparture_Cases()
        {
            var end = new HashSet<string> { Jeb };
            var away = new HashSet<string> { Bob };
            Assert.Equal(KerbalEndState.Unknown,
                KerbalsModule.ApplySplitDeparture(Bob, KerbalEndState.Dead, end, away, "r"));
            // Not carried away: an EVA'd-and-lost kerbal stays Dead.
            Assert.Equal(KerbalEndState.Dead,
                KerbalsModule.ApplySplitDeparture("Bill Kerman", KerbalEndState.Dead, end, away, "r"));
            // Still aboard at the end: keeps the inferred answer.
            Assert.Equal(KerbalEndState.Aboard,
                KerbalsModule.ApplySplitDeparture(Jeb, KerbalEndState.Aboard, end, new HashSet<string> { Jeb }, "r"));
            Assert.Equal(KerbalEndState.Dead,
                KerbalsModule.ApplySplitDeparture(Jeb, KerbalEndState.Dead, end, new HashSet<string> { Jeb }, "r"));
            // Only a Dead answer moves.
            Assert.Equal(KerbalEndState.Recovered,
                KerbalsModule.ApplySplitDeparture(Bob, KerbalEndState.Recovered, end, away, "r"));
            Assert.Equal(KerbalEndState.Dead,
                KerbalsModule.ApplySplitDeparture(Bob, KerbalEndState.Dead, end, null, "r"));
        }

        [Fact]
        public void CollectCrewCarriedAwayBySplit_ReadsSplitChildrenOnly()
        {
            var tree = new RecordingTree { Id = Tree };
            var parent = new Recording { RecordingId = Parent, TreeId = Tree };
            var cabin = new Recording { RecordingId = Cabin, TreeId = Tree, GhostVisualSnapshot = CrewSnapshot(Bob) };
            var eva = new Recording { RecordingId = "rec-eva", TreeId = Tree, EvaCrewName = "Bill Kerman" };
            var docked = new Recording { RecordingId = "rec-docked", TreeId = Tree, GhostVisualSnapshot = CrewSnapshot("Val Kerman") };
            foreach (var r in new[] { parent, cabin, eva, docked })
                tree.AddOrReplaceRecording(r);
            tree.BranchPoints.Add(new BranchPoint { Id = "b1", Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { Parent }, ChildRecordingIds = new List<string> { Cabin } });
            tree.BranchPoints.Add(new BranchPoint { Id = "b2", Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { Parent }, ChildRecordingIds = new List<string> { Parent, "rec-eva" } });
            tree.BranchPoints.Add(new BranchPoint { Id = "b3", Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { Parent }, ChildRecordingIds = new List<string> { "rec-docked" } });

            var away = KerbalsModule.CollectCrewCarriedAwayBySplit(parent, tree,
                new Dictionary<string, string>());
            Assert.Equal(new HashSet<string> { Bob, "Bill Kerman" }, away);
            Assert.Empty(KerbalsModule.CollectCrewCarriedAwayBySplit(parent, null, null));
            Assert.Empty(KerbalsModule.CollectCrewCarriedAwayBySplit(cabin, tree, null));
        }

        [Fact]
        public void ResolveMidFlightSplitHandoffUT_Cases()
        {
            var handoffs = new Dictionary<string, KerbalsModule.SplitHandoff>(StringComparer.Ordinal)
            {
                { KerbalsModule.SplitHandoffKey("t", "b1", Bob),
                    new KerbalsModule.SplitHandoff { ChildRecordingId = "c1", ChildStartUT = 150.0 } },
                { KerbalsModule.SplitHandoffKey("t", "b2", Bob),
                    new KerbalsModule.SplitHandoff { ChildRecordingId = "c2", ChildStartUT = 130.0 } },
                { KerbalsModule.SplitHandoffKey("t", "b3", Bob),
                    new KerbalsModule.SplitHandoff { ChildRecordingId = "c3", ChildStartUT = 900.0 } },
            };
            string child, bp;
            // Earliest in-span child wins.
            Assert.Equal(130.0, KerbalsModule.ResolveMidFlightSplitHandoffUT(
                handoffs, "t", new[] { "b1", "b2" }, 100.0, 300.0, Bob, out child, out bp));
            Assert.Equal("c2", child);
            Assert.Equal("b2", bp);
            // A child that starts after the recording ended is not this recording's split.
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ResolveMidFlightSplitHandoffUT(
                handoffs, "t", new[] { "b3" }, 100.0, 300.0, Bob, out child, out bp)));
            Assert.Null(child);
            // Another kerbal, another tree, no branch points.
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ResolveMidFlightSplitHandoffUT(
                handoffs, "t", new[] { "b1" }, 100.0, 300.0, Jeb, out child, out bp)));
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ResolveMidFlightSplitHandoffUT(
                handoffs, "other", new[] { "b1" }, 100.0, 300.0, Bob, out child, out bp)));
            Assert.True(double.IsPositiveInfinity(KerbalsModule.ResolveMidFlightSplitHandoffUT(
                handoffs, "t", null, 100.0, 300.0, Bob, out child, out bp)));
        }

        // catches: a crewed cabin with no command module dropped as debris; a crewless
        // commandless part still is debris.
        [Fact]
        public void ShouldRecordAsOwnBranch_TruthTable()
        {
            Assert.True(ParsekFlight.ShouldRecordAsOwnBranch(trackable: true, crewCount: 0));
            Assert.True(ParsekFlight.ShouldRecordAsOwnBranch(trackable: true, crewCount: 2));
            Assert.True(ParsekFlight.ShouldRecordAsOwnBranch(trackable: false, crewCount: 1));
            Assert.False(ParsekFlight.ShouldRecordAsOwnBranch(trackable: false, crewCount: 0));
        }

        private static void Walk()
        {
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(1e6, "hitchhiker-test");
            Assert.NotNull(LedgerOrchestrator.Kerbals);
        }
    }
}
