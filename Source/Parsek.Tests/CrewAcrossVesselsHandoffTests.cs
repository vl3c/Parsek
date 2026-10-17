using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Crew moving across vessels and missions (coverage audit 2026-10-16, item 3): the
    /// cross-tree hand-off that ends a mission's open-ended Aboard hold when another mission
    /// takes the vessel over, in the two shapes the dock-claim rule did not cover:
    /// <list type="bullet">
    /// <item>the held vessel is a Parsek SPAWN (the station was replayed after a rewind and
    /// spawned at its recording's end, under a fresh pid and guid joined to the recorded launch
    /// by a Spawn link), so the rescue's dock names the spawn, not the recorded launch;</item>
    /// <item>the rescue is a BOARDING: a kerbal EVAs from his own vessel and boards the station,
    /// and a Board merge onto a vessel outside the tree stamps no target pid.</item>
    /// </list>
    /// In both, the station mission's hold on the kerbal it left aboard stayed open forever and
    /// he kept a stand-in after the rescue brought him home.
    /// </summary>
    [Collection("Sequential")]
    public class CrewAcrossVesselsHandoffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bob = "Bob Kerman";

        private const uint StationPid = 5001u;
        private const string StationGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        private const uint SpawnPid = 7001u;
        private const string SpawnGuid = "77777777888899990000aaaaaaaaaaaa";
        private const uint TugPid = 6001u;
        private const string TugGuid = "11111111222233334444555555555555";

        private const string StationTree = "tree-station";
        private const string StationRec = "rec-station";
        private const string RescueTree = "tree-rescue";
        private const string TugRec = "rec-tug";
        private const string DockedRec = "rec-docked";
        private const string TugHomeRec = "rec-tug-home";
        private const string StationAfterRec = "rec-station-after";
        private const string TugAfterEvaRec = "rec-tug-after-eva";
        private const string EvaRec = "rec-bob-eva";
        private const string BoardedRec = "rec-boarded";
        private const string DockBp = "bp-dock";
        private const string UndockBp = "bp-undock";
        private const string EvaBp = "bp-eva";
        private const string BoardBp = "bp-board";

        private const double EvaUT = 450.0;
        private const double DockUT = 500.0;
        private const double UndockUT = 600.0;
        private const double HomeEndUT = 700.0;

        public CrewAcrossVesselsHandoffTests()
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
            VesselNameStore.ResetForTesting();
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
            VesselNameStore.ResetForTesting();
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
        /// Mission 2 by dock: a tug launched with Bob docks to the station as the vessel then
        /// in the game (<paramref name="targetPid"/> / <paramref name="targetGuid"/>), Jeb moves
        /// into the tug, the tug undocks and is recovered with both aboard.
        /// </summary>
        private static void CommitDockRescue(uint targetPid, string targetGuid)
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
                TargetVesselPersistentId = targetPid,
                TargetVesselGuid = targetGuid,
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
            var docked = Rec(DockedRec, RescueTree, TugPid, TugGuid, DockUT, UndockUT,
                null, new[] { Bob, Jeb }, new[] { Bob, Jeb });
            docked.ParentBranchPointId = DockBp;
            docked.ChildBranchPointId = UndockBp;
            var home = Rec(TugHomeRec, RescueTree, TugPid, TugGuid, UndockUT, HomeEndUT,
                TerminalState.Recovered, new[] { Bob, Jeb }, null);
            home.ParentBranchPointId = UndockBp;
            var stationAfter = Rec(StationAfterRec, RescueTree, targetPid, targetGuid, UndockUT, 800.0,
                TerminalState.Orbiting, new string[0], new string[0]);
            stationAfter.ParentBranchPointId = UndockBp;

            CommitTree(tree, tug, docked, home, stationAfter);
        }

        /// <summary>
        /// Mission 2 by boarding: a tug launched with Bob parks by the station, Bob EVAs and
        /// boards it (a single-parent Board merge onto a vessel outside the tree, stamped with
        /// no target pid, as <c>HandleTreeBoardMerge</c> writes it), and the station, now
        /// carrying Bob and Jeb, flies home and is recovered.
        /// </summary>
        private static void CommitBoardRescue(uint stationPid, string stationGuid)
        {
            var tree = new RecordingTree
            {
                Id = RescueTree,
                TreeName = "Rescue",
                RootRecordingId = TugRec,
                ActiveRecordingId = BoardedRec,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = EvaBp,
                UT = EvaUT,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { TugRec },
                ChildRecordingIds = new List<string> { TugAfterEvaRec, EvaRec },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BoardBp,
                UT = DockUT,
                Type = BranchPointType.Board,
                ParentRecordingIds = new List<string> { EvaRec },
                ChildRecordingIds = new List<string> { BoardedRec },
            });

            var tug = Rec(TugRec, RescueTree, TugPid, TugGuid, 400.0, EvaUT,
                null, new[] { Bob }, new[] { Bob });
            tug.ChildBranchPointId = EvaBp;
            var tugAfter = Rec(TugAfterEvaRec, RescueTree, TugPid, TugGuid, EvaUT, 800.0,
                TerminalState.Orbiting, new string[0], new string[0]);
            tugAfter.ParentBranchPointId = EvaBp;
            var eva = Rec(EvaRec, RescueTree, 8001u, "99999999aaaabbbbccccdddddddddddd", EvaUT, DockUT,
                TerminalState.Boarded, new string[0], null);
            eva.EvaCrewName = Bob;
            eva.ParentBranchPointId = EvaBp;
            eva.ChildBranchPointId = BoardBp;
            var boarded = Rec(BoardedRec, RescueTree, stationPid, stationGuid, DockUT, HomeEndUT,
                TerminalState.Recovered, new[] { Bob, Jeb }, null);
            boarded.ParentBranchPointId = BoardBp;

            CommitTree(tree, tug, tugAfter, eva, boarded);
        }

        private static KerbalsModule Walk(double clockUT)
        {
            KerbalsModule.LiveClockUTProviderForTesting = () => clockUT;
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "crew-across-vessels-test");
            return LedgerOrchestrator.Kerbals;
        }

        // catches: a rescue that docks to the station's Parsek SPAWN (fresh pid and guid,
        // Spawn-linked to the recorded launch) leaving Jeb reserved forever by the station
        // mission after the rescue brought him home.
        [Fact]
        public void DockRescueOfASpawnedStation_EndsTheStationHold_AndFreesJebAfterLanding()
        {
            CommitStationMission();
            Assert.True(VesselNameStore.AddLink(SpawnGuid, StationGuid, VesselIdentityLinkKind.Spawn, "test"));
            CommitDockRescue(SpawnPid, SpawnGuid);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(HomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'")
                && l.Contains("recording '" + StationRec + "'")
                && l.Contains("bp='" + DockBp + "'"));
        }

        // catches: the link widening reaching a vessel that is NOT the station's spawn (a
        // different launch of the same craft, no link row): the hold must stay open.
        [Fact]
        public void DockToAnUnlinkedVesselWithAFreshPid_KeepsTheStationHold()
        {
            CommitStationMission();
            CommitDockRescue(SpawnPid, SpawnGuid);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // catches: a rescue that BOARDS the station (EVA, Board merge with no stamped target)
        // leaving Jeb reserved forever by the station mission.
        [Fact]
        public void BoardRescue_EndsTheStationHold_AndFreesJebAfterLanding()
        {
            CommitStationMission();
            CommitBoardRescue(StationPid, StationGuid);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(HomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Equal(HomeEndUT, kerbals.Reservations[Bob].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Bob));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'")
                && l.Contains("recording '" + StationRec + "'")
                && l.Contains("bp='" + BoardBp + "'")
                && l.Contains("child='" + BoardedRec + "'"));
        }

        // catches: the board hand-off releasing Jeb before the boarded station comes home.
        [Fact]
        public void BoardRescue_JebStaysHeldUntilTheStationIsRecovered()
        {
            CommitStationMission();
            CommitBoardRescue(StationPid, StationGuid);

            KerbalsModule kerbals = Walk(650.0);

            Assert.Equal(HomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // catches: the board hand-off claiming another launch of the same craft (same baked
        // pid, a different launch guid).
        [Fact]
        public void BoardingAnotherLaunchOfTheSameCraft_KeepsTheStationHold()
        {
            CommitStationMission();
            CommitBoardRescue(StationPid, "ffffffffffffffffffffffffffffffff");

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        // catches: both gaps together, the realistic order after a rewind: the station was
        // replayed and spawned, and the rescue boarded the spawn.
        [Fact]
        public void BoardRescueOfASpawnedStation_EndsTheStationHold()
        {
            CommitStationMission();
            Assert.True(VesselNameStore.AddLink(SpawnGuid, StationGuid, VesselIdentityLinkKind.Spawn, "test"));
            CommitBoardRescue(SpawnPid, SpawnGuid);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(HomeEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
        }

        // Pure: the identity rule with a link reader. A linked claim guid matches whatever its
        // pid; an unlinked one needs the pid and a guid that does not conclusively differ.
        [Theory]
        [InlineData(SpawnPid, SpawnGuid, true, true)]
        [InlineData(SpawnPid, SpawnGuid, false, false)]
        [InlineData(StationPid, StationGuid, false, true)]
        [InlineData(StationPid, null, false, true)]
        [InlineData(SpawnPid, null, true, false)]
        public void DockClaimHandsOffHold_FollowsSpawnLinks(
            uint targetPid, string targetGuid, bool linked, bool expected)
        {
            Func<string, string, bool> continues = (live, recorded) =>
                linked && live == SpawnGuid && recorded == StationGuid;
            Assert.Equal(expected, KerbalsModule.DockClaimHandsOffHold(
                StationPid, StationGuid, 300.0, targetPid, targetGuid, 500.0, continues));
        }
    }
}
