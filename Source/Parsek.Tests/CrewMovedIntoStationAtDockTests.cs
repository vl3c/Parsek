using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROUTE-INTERACTION-SEAMS-TO-VERIFY item "a kerbal swapped into a station during a dock is
    /// inferred Dead or held indefinitely": a mission docks to a station, moves its own kerbal
    /// INTO the station while docked (alone, or swapped with the station's crew) and undocks,
    /// leaving him aboard. The existing cross-vessel cells move crew OUT of a station; these
    /// pin the inward direction through the real commit (end-state inference) and ledger walk,
    /// in each tree shape the recorder writes: a station of another mission (single-parent
    /// Dock), a station that is a background member of the same tree (two-parent Dock), and the
    /// worst-case docked recording that carries an intact terminal and an end snapshot holding
    /// only one half. The kerbal must read Aboard on the station (held while he is there, never
    /// permanently), and a later rescue that docks and brings him home must free him.
    /// </summary>
    [Collection("Sequential")]
    public class CrewMovedIntoStationAtDockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bob = "Bob Kerman";

        private const uint StationPid = 5001u;
        private const string StationGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        private const uint TugPid = 6001u;
        private const string TugGuid = "11111111222233334444555555555555";
        private const uint StationAfterPid = 5002u;
        private const string StationAfterGuid = "5555555566667777888899999999aaaa";
        private const uint Tug3Pid = 6003u;
        private const string Tug3Guid = "33333333444455556666777777777777";

        private const string StationTree = "tree-station";
        private const string StationRec = "rec-station";
        private const string FerryTree = "tree-ferry";
        private const string TugRec = "rec-tug";
        private const string StationBgRec = "rec-station-bg";
        private const string DockedRec = "rec-docked";
        private const string TugHomeRec = "rec-tug-home";
        private const string StationAfterRec = "rec-station-after";
        private const string RescueTree = "tree-rescue";
        private const string Tug3Rec = "rec-tug3";
        private const string Docked3Rec = "rec-docked3";
        private const string Tug3HomeRec = "rec-tug3-home";
        private const string Station3AfterRec = "rec-station3-after";
        private const string DockBp = "bp-dock";
        private const string UndockBp = "bp-undock";
        private const string Dock3Bp = "bp-dock3";
        private const string Undock3Bp = "bp-undock3";

        private const double DockUT = 500.0;
        private const double UndockUT = 600.0;
        private const double HomeEndUT = 700.0;
        private const double Dock3UT = 1500.0;
        private const double Undock3UT = 1600.0;
        private const double Home3EndUT = 1700.0;

        public CrewMovedIntoStationAtDockTests()
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

        /// <summary>Mission 1: a station launched with <paramref name="crew"/> and left in orbit.</summary>
        private static void CommitStationMission(params string[] crew)
        {
            var tree = new RecordingTree
            {
                Id = StationTree,
                TreeName = "Station",
                RootRecordingId = StationRec,
                ActiveRecordingId = StationRec,
            };
            CommitTree(tree, Rec(StationRec, StationTree, StationPid, StationGuid, 100.0, 300.0,
                TerminalState.Orbiting, crew, crew));
        }

        /// <summary>
        /// Mission 2: a tug launched with Jeb docks to the station, Jeb moves into the station
        /// while docked (<paramref name="stationCrewBefore"/> moves into the tug, a swap when
        /// not empty), the tug undocks and is recovered, and the station half stays in orbit
        /// with Jeb aboard. <paramref name="twoParent"/>: the station is a background member of
        /// this tree (its recording <see cref="StationBgRec"/> is the dock's second parent),
        /// else it is another mission's vessel (a single-parent Dock stamped with its pid and
        /// guid, as <c>ResolveBranchPartnerStampPid</c> writes it).
        /// <paramref name="dockedIntactTerminal"/>: the docked recording carries an intact
        /// terminal and its end snapshot holds only the tug half, the worst shape a later
        /// re-snapshot could leave.
        /// </summary>
        private static void CommitFerryMission(
            string[] stationCrewBefore, bool twoParent = false, bool dockedIntactTerminal = false)
        {
            var tree = new RecordingTree
            {
                Id = FerryTree,
                TreeName = "Ferry",
                RootRecordingId = TugRec,
                ActiveRecordingId = TugHomeRec,
            };
            var dockParents = new List<string> { TugRec };
            if (twoParent)
                dockParents.Add(StationBgRec);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = DockBp,
                UT = DockUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = dockParents,
                ChildRecordingIds = new List<string> { DockedRec },
                TargetVesselPersistentId = StationPid,
                TargetVesselGuid = StationGuid,
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = UndockBp,
                UT = UndockUT,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { DockedRec },
                ChildRecordingIds = new List<string> { TugHomeRec, StationAfterRec },
            });

            var all = new List<string>(stationCrewBefore) { Jeb };
            var recs = new List<Recording>();

            var tug = Rec(TugRec, FerryTree, TugPid, TugGuid, 400.0, DockUT,
                TerminalState.Docked, new[] { Jeb }, new[] { Jeb });
            tug.ChildBranchPointId = DockBp;
            recs.Add(tug);
            if (twoParent)
            {
                var stationBg = Rec(StationBgRec, FerryTree, StationPid, StationGuid, 400.0, DockUT,
                    TerminalState.Docked, stationCrewBefore, stationCrewBefore);
                stationBg.ChildBranchPointId = DockBp;
                recs.Add(stationBg);
            }
            var docked = Rec(DockedRec, FerryTree, TugPid, TugGuid, DockUT, UndockUT,
                dockedIntactTerminal ? TerminalState.Orbiting : (TerminalState?)null,
                all.ToArray(),
                dockedIntactTerminal ? stationCrewBefore : all.ToArray());
            docked.ParentBranchPointId = DockBp;
            docked.ChildBranchPointId = UndockBp;
            recs.Add(docked);
            var home = Rec(TugHomeRec, FerryTree, TugPid, TugGuid, UndockUT, HomeEndUT,
                TerminalState.Recovered, stationCrewBefore, null);
            home.ParentBranchPointId = UndockBp;
            recs.Add(home);
            var stationAfter = Rec(StationAfterRec, FerryTree, StationAfterPid, StationAfterGuid,
                UndockUT, 800.0, TerminalState.Orbiting, new[] { Jeb }, new[] { Jeb });
            stationAfter.ParentBranchPointId = UndockBp;
            recs.Add(stationAfter);

            CommitTree(tree, recs.ToArray());
        }

        /// <summary>
        /// Mission 3: a crewless tug docks to the station half mission 2 left Jeb on (its live
        /// pid and guid), Jeb moves into the tug, the tug undocks and is recovered.
        /// </summary>
        private static void CommitRescueMission()
        {
            var tree = new RecordingTree
            {
                Id = RescueTree,
                TreeName = "Rescue",
                RootRecordingId = Tug3Rec,
                ActiveRecordingId = Tug3HomeRec,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = Dock3Bp,
                UT = Dock3UT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { Tug3Rec },
                ChildRecordingIds = new List<string> { Docked3Rec },
                TargetVesselPersistentId = StationAfterPid,
                TargetVesselGuid = StationAfterGuid,
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = Undock3Bp,
                UT = Undock3UT,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { Docked3Rec },
                ChildRecordingIds = new List<string> { Tug3HomeRec, Station3AfterRec },
            });
            var tug = Rec(Tug3Rec, RescueTree, Tug3Pid, Tug3Guid, 1400.0, Dock3UT,
                TerminalState.Docked, new string[0], new string[0]);
            tug.ChildBranchPointId = Dock3Bp;
            var docked = Rec(Docked3Rec, RescueTree, Tug3Pid, Tug3Guid, Dock3UT, Undock3UT,
                null, new[] { Jeb }, new[] { Jeb });
            docked.ParentBranchPointId = Dock3Bp;
            docked.ChildBranchPointId = Undock3Bp;
            var home = Rec(Tug3HomeRec, RescueTree, Tug3Pid, Tug3Guid, Undock3UT, Home3EndUT,
                TerminalState.Recovered, new[] { Jeb }, null);
            home.ParentBranchPointId = Undock3Bp;
            var stationAfter = Rec(Station3AfterRec, RescueTree, StationAfterPid, StationAfterGuid,
                Undock3UT, 1800.0, TerminalState.Orbiting, new string[0], new string[0]);
            stationAfter.ParentBranchPointId = Undock3Bp;
            CommitTree(tree, tug, docked, home, stationAfter);
        }

        private static KerbalsModule Walk(double clockUT)
        {
            KerbalsModule.LiveClockUTProviderForTesting = () => clockUT;
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "crew-into-station-test");
            return LedgerOrchestrator.Kerbals;
        }

        private static KerbalEndState EndStateOf(string recordingId, string kerbal)
        {
            Recording rec = null;
            foreach (var r in RecordingStore.CommittedRecordings)
                if (r.RecordingId == recordingId) rec = r;
            Assert.NotNull(rec);
            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStates.ContainsKey(kerbal),
                $"recording {recordingId} has no end state for {kerbal}");
            return rec.CrewEndStates[kerbal];
        }

        private void AssertJebNeverReadDead()
        {
            Assert.DoesNotContain(logLines, l => l.Contains("InferCrewEndState: crew='" + Jeb + "'")
                && l.Contains("-> Dead"));
            Assert.DoesNotContain(logLines, l => l.Contains("Death hold: '" + Jeb + "'"));
        }

        // catches: a kerbal swapped into another mission's station during a dock read Dead
        // (missing from some recording's end) or permanently held, and the station's own
        // crew who rode the tug home staying held by the station mission.
        [Fact]
        public void SwapIntoForeignStation_JebAboardStation_BobFreedAfterRecovery()
        {
            CommitStationMission(Bob);
            CommitFerryMission(new[] { Bob });

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(KerbalEndState.Aboard, EndStateOf(TugRec, Jeb));
            Assert.Equal(KerbalEndState.Unknown, EndStateOf(DockedRec, Jeb));
            Assert.Equal(KerbalEndState.Aboard, EndStateOf(StationAfterRec, Jeb));
            AssertJebNeverReadDead();

            // Jeb is aboard the station: held while he is there, never as a death.
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(double.IsNaN(kerbals.Reservations[Jeb].DeathRespawnUT));

            // Bob left the station mission's vessel by the dock and came home.
            Assert.Equal(HomeEndUT, kerbals.Reservations[Bob].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Bob));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: '" + Bob + "'")
                && l.Contains("recording '" + StationRec + "'")
                && l.Contains("bp='" + DockBp + "'"));
        }

        // catches: a one-way move into an empty station (the tug comes home crewless) losing
        // the kerbal: no recording past the undock carries him except the station half.
        [Fact]
        public void MoveIntoEmptyForeignStation_JebAboardStation()
        {
            CommitStationMission();
            CommitFerryMission(new string[0]);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(KerbalEndState.Aboard, EndStateOf(StationAfterRec, Jeb));
            AssertJebNeverReadDead();
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("Reservation bounded by split handoff: '" + Jeb + "'")
                && l.Contains("recording '" + DockedRec + "'")
                && l.Contains("child='" + StationAfterRec + "'"));
        }

        // catches: the station being a background member of the same tree (a two-parent Dock)
        // stranding either kerbal: both parents end Docked and hand off at the dock.
        [Fact]
        public void SwapIntoSameTreeStation_JebAboardStation_BobFreedAfterRecovery()
        {
            CommitFerryMission(new[] { Bob }, twoParent: true);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(KerbalEndState.Aboard, EndStateOf(StationAfterRec, Jeb));
            AssertJebNeverReadDead();
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.Equal(HomeEndUT, kerbals.Reservations[Bob].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Bob));
            Assert.False(kerbals.Reservations[Bob].IsPermanent);
        }

        // catches: a docked recording that ends with an intact terminal and an end snapshot
        // holding only the tug half reading Jeb (who left on the station half) as Dead.
        [Fact]
        public void DockedRecordingWithIntactTerminalAndOneHalfEndSnapshot_ReadsJebUnknownNotDead()
        {
            CommitStationMission(Bob);
            CommitFerryMission(new[] { Bob }, dockedIntactTerminal: true);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(KerbalEndState.Unknown, EndStateOf(DockedRec, Jeb));
            Assert.Equal(KerbalEndState.Aboard, EndStateOf(StationAfterRec, Jeb));
            Assert.Contains(logLines, l => l.Contains("InferCrewEndState: crew='" + Jeb + "'")
                && l.Contains("recording='" + DockedRec + "'")
                && l.Contains("left through a split child before the end -> Unknown (not Dead)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Death hold: '" + Jeb + "'"));
            Assert.False(kerbals.Reservations[Jeb].IsPermanent);
        }

        // catches: the station hold on a kerbal moved in at a dock being indefinite in the
        // bad sense: a later rescue that docks to that station and brings him home must end it.
        [Fact]
        public void LaterRescueOfTheKerbalMovedIn_FreesJebAfterLanding()
        {
            CommitStationMission(Bob);
            CommitFerryMission(new[] { Bob });
            CommitRescueMission();

            KerbalsModule kerbals = Walk(10000.0);

            AssertJebNeverReadDead();
            Assert.Equal(Home3EndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: '" + Jeb + "'")
                && l.Contains("recording '" + StationAfterRec + "'")
                && l.Contains("bp='" + Dock3Bp + "'"));
        }

        // catches: the rescue hand-off widening to the WRONG time: Jeb stays held while he is
        // still on the station before the rescue docks, and while the rescue is flying.
        [Fact]
        public void LaterRescueOfTheKerbalMovedIn_JebHeldUntilTheRescueLands()
        {
            CommitStationMission(Bob);
            CommitFerryMission(new[] { Bob });
            CommitRescueMission();

            Assert.True(Walk(1000.0).IsReservedNow(Jeb));
            Assert.True(Walk(1650.0).IsReservedNow(Jeb));
        }
    }
}
