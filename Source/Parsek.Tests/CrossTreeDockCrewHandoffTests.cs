using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CROSS-TREE-CREW-TRANSFER-STRANDS-RESERVATION: a kerbal left aboard a station by mission 1
    /// (an open-ended Aboard hold) is carried home by mission 2, which docked to that station,
    /// took him aboard and landed. The dock in mission 2's tree claims the station mission 1
    /// recorded, and mission 2's docked recording carries the kerbal on, so mission 1's hold
    /// ends at the dock (a hand-off across trees, the same rule a split uses inside a tree) and
    /// mission 2's own rows decide the rest. Before the fix, recovery closed holds only inside
    /// the recovering mission's tree, so the kerbal stayed reserved forever with a stand-in in
    /// his place.
    /// </summary>
    [Collection("Sequential")]
    public class CrossTreeDockCrewHandoffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bob = "Bob Kerman";

        private const uint StationPid = 5001u;
        private const string StationGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        private const uint TugPid = 6001u;
        private const string TugGuid = "11111111222233334444555555555555";

        private const string StationTree = "tree-station";
        private const string StationRec = "rec-station";
        private const string RescueTree = "tree-rescue";
        private const string TugRec = "rec-tug";
        private const string DockedRec = "rec-docked";
        private const string TugHomeRec = "rec-tug-home";
        private const string StationAfterRec = "rec-station-after";
        private const string DockBp = "bp-dock";
        private const string UndockBp = "bp-undock";

        private const double DockUT = 500.0;
        private const double UndockUT = 600.0;
        private const double TugHomeEndUT = 700.0;

        public CrossTreeDockCrewHandoffTests()
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

        private static Recording Rec(
            string id, string treeId, uint pid, string guid, double startUT, double endUT,
            TerminalState? terminal, string[] startCrew, string[] endCrew)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                GhostVisualSnapshot = CrewSnapshot(startCrew),
                VesselSnapshot = endCrew != null ? CrewSnapshot(endCrew) : null,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
            };
        }

        private static void CommitTree(RecordingTree tree, params Recording[] recs)
        {
            foreach (var rec in recs)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
        }

        /// <summary>Mission 1: a station launched with Jeb, left in orbit with him aboard.</summary>
        private static void CommitStationMission()
        {
            var tree = new RecordingTree
            {
                Id = StationTree,
                TreeName = "Station",
                RootRecordingId = StationRec,
                ActiveRecordingId = StationRec,
            };
            CommitTree(tree, Rec(StationRec, StationTree, StationPid, StationGuid, 100.0, 300.0,
                TerminalState.Orbiting, new[] { Jeb }, new[] { Jeb }));
        }

        /// <summary>
        /// Mission 2: a tug launched with Bob docks to the station (a single-parent Dock
        /// claiming the station's pid), Jeb is moved into the tug while docked, the tug undocks
        /// and comes home recovered with both aboard; the station half stays in orbit, empty.
        /// </summary>
        private static void CommitRescueMission(
            uint dockTargetPid = StationPid, string dockTargetGuid = StationGuid,
            bool dockedCarriesJeb = true)
        {
            var tree = new RecordingTree
            {
                Id = RescueTree,
                TreeName = "Rescue",
                RootRecordingId = TugRec,
                ActiveRecordingId = TugHomeRec,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = DockBp,
                UT = DockUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { TugRec },
                ChildRecordingIds = new List<string> { DockedRec },
                TargetVesselPersistentId = dockTargetPid,
                TargetVesselGuid = dockTargetGuid,
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = UndockBp,
                UT = UndockUT,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { DockedRec },
                ChildRecordingIds = new List<string> { TugHomeRec, StationAfterRec },
            });

            var tug = Rec(TugRec, RescueTree, TugPid, TugGuid, 400.0, DockUT,
                TerminalState.Docked, new[] { Bob }, new[] { Bob });
            tug.ChildBranchPointId = DockBp;
            string[] dockedCrew = dockedCarriesJeb ? new[] { Bob, Jeb } : new[] { Bob };
            var docked = Rec(DockedRec, RescueTree, TugPid, TugGuid, DockUT, UndockUT,
                null, dockedCrew, dockedCrew);
            docked.ParentBranchPointId = DockBp;
            docked.ChildBranchPointId = UndockBp;
            string[] homeCrew = dockedCarriesJeb ? new[] { Bob, Jeb } : new[] { Bob };
            var home = Rec(TugHomeRec, RescueTree, TugPid, TugGuid, UndockUT, TugHomeEndUT,
                TerminalState.Recovered, homeCrew, null);
            home.ParentBranchPointId = UndockBp;
            var stationAfter = Rec(StationAfterRec, RescueTree, StationPid, StationGuid, UndockUT, 800.0,
                TerminalState.Orbiting, new string[0], new string[0]);
            stationAfter.ParentBranchPointId = UndockBp;

            CommitTree(tree, tug, docked, home, stationAfter);
        }

        private static KerbalsModule Walk(double clockUT)
        {
            KerbalsModule.LiveClockUTProviderForTesting = () => clockUT;
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "cross-tree-handoff-test");
            return LedgerOrchestrator.Kerbals;
        }

        // catches: the filed bug - a rescue mission that docks, takes the stranded kerbal
        // aboard and comes home leaves him reserved forever by the station mission.
        [Fact]
        public void RescueByDock_EndsTheStationMissionsOpenHold_AndFreesJebAfterLanding()
        {
            CommitStationMission();
            CommitRescueMission();

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(kerbals.Reservations.ContainsKey(Jeb));
            Assert.Equal(TugHomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'")
                && l.Contains("recording '" + StationRec + "'")
                && l.Contains("bp='" + DockBp + "'")
                && l.Contains("child='" + DockedRec + "'"));
        }

        // catches: the hand-off releasing Jeb before the rescue lands (he stays held for the
        // rescue mission's own span).
        [Fact]
        public void RescueByDock_JebStaysHeldUntilTheRescueLands()
        {
            CommitStationMission();
            CommitRescueMission();

            KerbalsModule kerbals = Walk(650.0);

            Assert.Equal(TugHomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // catches: a dock that never took Jeb aboard (he stayed on the station) ending the
        // station mission's hold.
        [Fact]
        public void DockWithoutTakingJeb_KeepsTheStationMissionsOpenHold()
        {
            CommitStationMission();
            CommitRescueMission(dockedCarriesJeb: false);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // catches: a dock to ANOTHER launch of the same craft (same baked pid, different launch
        // guid) handing off the station's crew.
        [Fact]
        public void DockToAnotherLaunchOfTheSameCraft_KeepsTheOpenHold()
        {
            CommitStationMission();
            CommitRescueMission(dockTargetGuid: "ffffffffffffffffffffffffffffffff");

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        // catches: a dock to an unrelated vessel (other pid) handing off the station's crew.
        [Fact]
        public void DockToAnUnrelatedVessel_KeepsTheOpenHold()
        {
            CommitStationMission();
            CommitRescueMission(dockTargetPid: 9999u, dockTargetGuid: null);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        // Pure: the hand-off rule, including the dock that happened BEFORE the hold's
        // recording ended (no hand-off: the docked recording cannot carry a kerbal the station
        // still held afterwards) and an unknown guid on one side (pid-only fallback).
        [Theory]
        [InlineData(StationPid, StationGuid, 300.0, 500.0, true)]
        [InlineData(StationPid, null, 300.0, 500.0, true)]
        [InlineData(StationPid, "ffffffffffffffffffffffffffffffff", 300.0, 500.0, false)]
        [InlineData(9999u, StationGuid, 300.0, 500.0, false)]
        [InlineData(StationPid, StationGuid, 520.0, 500.0, false)]
        [InlineData(StationPid, StationGuid, 500.5, 500.0, true)]
        public void DockClaimHandsOffHold_Truth(
            uint targetPid, string targetGuid, double holdEndUT, double dockUT, bool expected)
        {
            Assert.Equal(expected, KerbalsModule.DockClaimHandsOffHold(
                StationPid, StationGuid, holdEndUT, targetPid, targetGuid, dockUT));
        }
    }
}
