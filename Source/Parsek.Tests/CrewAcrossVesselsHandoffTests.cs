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

        private const string LaterTree = "tree-later";
        private const string ShipTRec = "rec-ship-t";
        private const string StationLaterRec = "rec-station-later";
        private const double LaterStartUT = 2000.0;
        private const double LaterEndUT = 2100.0;
        private const double RespawnSeconds = 7200.0;

        /// <summary>
        /// Mission 1 for the foreign-recording shape: the station left in orbit with Jeb
        /// aboard, then spawned at its recording's end under a fresh pid and guid joined to
        /// the recorded launch by a Spawn link.
        /// </summary>
        private static void CommitSpawnedStationMission()
        {
            var tree = new RecordingTree
            {
                Id = StationTree,
                TreeName = "Station",
                RootRecordingId = StationRec,
                ActiveRecordingId = StationRec,
            };
            var station = Rec(StationRec, StationTree, StationPid, StationGuid, 100.0, 1000.0,
                TerminalState.Orbiting, new[] { Jeb }, new[] { Jeb });
            station.SpawnedVesselPersistentId = SpawnPid;
            CommitTree(tree, station);
            Assert.True(VesselNameStore.AddLink(SpawnGuid, StationGuid, VesselIdentityLinkKind.Spawn, "test"));
        }

        /// <summary>
        /// Mission 2: ship T launched with Bob; the player switched to the station with no
        /// stock-action intent (the `]` key), which records the station as a fresh PARENTLESS
        /// recording inside this tree, on the vessel in the game then (<paramref name="pid"/> /
        /// <paramref name="guid"/>) and carrying <paramref name="stationCrew"/>.
        /// </summary>
        private static void CommitForeignStationRecording(
            uint pid, string guid, string[] stationCrew, TerminalState terminal,
            bool? deathRespawns = null, bool launchParented = false)
        {
            var tree = new RecordingTree
            {
                Id = LaterTree,
                TreeName = "Ship T",
                RootRecordingId = ShipTRec,
                ActiveRecordingId = StationLaterRec,
            };
            var shipT = Rec(ShipTRec, LaterTree, TugPid, TugGuid, 1900.0, 1950.0,
                TerminalState.Orbiting, new[] { Bob }, new[] { Bob });
            var stationLater = Rec(StationLaterRec, LaterTree, pid, guid, LaterStartUT, LaterEndUT,
                terminal, stationCrew,
                terminal == TerminalState.Destroyed || terminal == TerminalState.Recovered
                    ? null : stationCrew);
            if (deathRespawns.HasValue)
            {
                stationLater.CrewDeathRespawns = deathRespawns;
                stationLater.CrewDeathRespawnSeconds = RespawnSeconds;
            }
            if (launchParented)
            {
                // The switch happened while T was recording: the post-switch start hangs the
                // station's recording under T at a Launch branch point.
                tree.BranchPoints.Add(new BranchPoint
                {
                    Id = "bp-joined",
                    UT = LaterStartUT,
                    Type = BranchPointType.Launch,
                    ParentRecordingIds = new List<string> { ShipTRec },
                    ChildRecordingIds = new List<string> { StationLaterRec },
                });
                shipT.ChildBranchPointId = "bp-joined";
                stationLater.ParentBranchPointId = "bp-joined";
            }
            CommitTree(tree, shipT, stationLater);
        }

        // catches: GC6. A later mission records the station's SPAWN as a fresh parentless
        // recording (a `]` switch), the station is destroyed with Jeb aboard and stock's
        // lost-crew respawn is on; the station mission's open-ended Aboard row was never
        // closed, so it turned the death into a permanent loss ("respawn suppressed:
        // open-ended co-row") while stock respawns Jeb.
        [Fact]
        public void ForeignTreeRecordingOfASpawnedStation_DeathRespawnsAsStock()
        {
            CommitSpawnedStationMission();
            CommitForeignStationRecording(SpawnPid, SpawnGuid, new[] { Jeb },
                TerminalState.Destroyed, deathRespawns: true);

            KerbalsModule kerbals = Walk(5000.0);

            KerbalsModule.KerbalReservation jeb = kerbals.Reservations[Jeb];
            Assert.False(jeb.IsPermanent);
            Assert.Equal(LaterEndUT + RespawnSeconds, jeb.ReservedUntilUT);
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.DoesNotContain(logLines, l => l.Contains("respawn suppressed: open-ended co-row"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by foreign recording of the vessel: 'Jebediah Kerman'")
                && l.Contains("recording '" + StationRec + "'")
                && l.Contains("tree='" + LaterTree + "'")
                && l.Contains("child='" + StationLaterRec + "'"));

            kerbals = Walk(LaterEndUT + RespawnSeconds + 1.0);
            Assert.False(kerbals.IsReservedNow(Jeb));
        }

        // catches: GC6's recovered twin. The later mission brings the station home with Jeb
        // aboard and the station mission's hold stayed open forever.
        [Fact]
        public void ForeignTreeRecordingOfASpawnedStation_RecoveredFreesJeb()
        {
            CommitSpawnedStationMission();
            CommitForeignStationRecording(SpawnPid, SpawnGuid, new[] { Jeb }, TerminalState.Recovered);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(LaterEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
        }

        // catches: the same take-over when the station joined the later tree at a post-switch
        // Launch branch point (the switch happened while ship T was recording).
        [Fact]
        public void ForeignTreeRecordingJoinedAtALaunchBranchPoint_RecoveredFreesJeb()
        {
            CommitSpawnedStationMission();
            CommitForeignStationRecording(SpawnPid, SpawnGuid, new[] { Jeb }, TerminalState.Recovered,
                launchParented: true);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(LaterEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("Reservation bounded by foreign recording of the vessel: 'Jebediah Kerman'"));
        }

        /// <summary>
        /// Tree B, not yet committed: a fresh recording of the spawned station that never moved
        /// (Landed start, 5 m) and the EVA of <paramref name="evaKerbal"/> off it, also idle.
        /// </summary>
        private static RecordingTree IdleStationVisitWithEva(string evaKerbal)
        {
            var tree = new RecordingTree
            {
                Id = LaterTree,
                TreeName = "Station visit",
                RootRecordingId = StationLaterRec,
                ActiveRecordingId = "rec-eva-later",
            };
            var station = new Recording
            {
                RecordingId = StationLaterRec,
                TreeId = LaterTree,
                VesselName = "Station",
                VesselPersistentId = SpawnPid,
                StartSituation = "Landed",
                MaxDistanceFromLaunch = 5.0,
            };
            station.Points.Add(new TrajectoryPoint { ut = LaterStartUT, bodyName = "Kerbin" });
            station.Points.Add(new TrajectoryPoint { ut = LaterStartUT + 20.0, bodyName = "Kerbin" });
            var eva = new Recording
            {
                RecordingId = "rec-eva-later",
                TreeId = LaterTree,
                VesselName = evaKerbal,
                EvaCrewName = evaKerbal,
                MaxDistanceFromLaunch = 4.0,
                TerminalStateValue = TerminalState.Recovered,
            };
            eva.Points.Add(new TrajectoryPoint { ut = LaterStartUT + 10.0, bodyName = "Kerbin" });
            eva.Points.Add(new TrajectoryPoint { ut = LaterStartUT + 20.0, bodyName = "Kerbin" });
            tree.Recordings[station.RecordingId] = station;
            tree.Recordings[eva.RecordingId] = eva;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-eva-later",
                UT = LaterStartUT + 10.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { StationLaterRec },
                ChildRecordingIds = new List<string> { "rec-eva-later" },
            });
            return tree;
        }

        // catches: CX4-IDLE-DISCARDED-FLIGHT-LEAVES-THE-HOLD-OPEN. The visit that takes Jeb off
        // the station is the only flight that can end the station mission's hold; the idle-on-pad
        // scene-exit discard threw it away and Jeb, already recovered by stock, stayed held.
        [Fact]
        public void IdleVisitWhoseEvaTakesAHeldKerbalOff_IsNotIdle()
        {
            CommitSpawnedStationMission();
            KerbalsModule kerbals = Walk(10000.0);
            Assert.True(kerbals.IsReservedNow(Jeb));

            RecordingTree visit = IdleStationVisitWithEva(Jeb);

            Assert.False(ParsekFlight.IsTreeIdleOnPad(visit));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - held kerbal 'Jebediah Kerman' left his vessel on EVA")
                && l.Contains("recording rec-eva-later")
                && l.Contains("tree='Station visit'"));
        }

        // catches: the veto reaching a kerbal nobody holds: an idle EVA of a free kerbal is still
        // an idle flight and is discarded as before.
        [Fact]
        public void IdleVisitWhoseEvaTakesAFreeKerbalOff_StaysIdle()
        {
            CommitSpawnedStationMission();
            KerbalsModule kerbals = Walk(10000.0);
            Assert.False(kerbals.IsReservedNow(Bob));

            Assert.True(ParsekFlight.IsTreeIdleOnPad(IdleStationVisitWithEva(Bob)));
            Assert.DoesNotContain(logLines, l => l.Contains("left his vessel on EVA"));
        }

        // Pure: only a vessel that joined its tree claims; a lineage child does not.
        [Theory]
        [InlineData(null, null, true)]
        [InlineData("bp", BranchPointType.Launch, true)]
        [InlineData("bp", BranchPointType.Undock, false)]
        [InlineData("bp", BranchPointType.Dock, false)]
        [InlineData("bp", BranchPointType.VesselSwitchContinuation, false)]
        [InlineData("bp", null, false)]
        public void IsJoinedVesselRecording_OnlyRootsParentlessAndLaunchChildren(
            string parentBp, BranchPointType? parentType, bool expected)
        {
            Assert.Equal(expected, KerbalsModule.IsJoinedVesselRecording(parentBp, parentType));
        }

        // catches: the foreign-recording claim reaching a fresh launch of the same craft (the
        // craft-baked pid matches, the launch guid differs): the station hold must stay open.
        [Fact]
        public void FreshLaunchOfTheSameCraftInAnotherTree_KeepsTheStationHold()
        {
            CommitSpawnedStationMission();
            CommitForeignStationRecording(StationPid, "ffffffffffffffffffffffffffffffff",
                new[] { Jeb }, TerminalState.Recovered);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation bounded by foreign recording"));
        }

        // catches: a foreign recording of the station that does not carry Jeb handing his hold
        // off to rows that never name him.
        [Fact]
        public void ForeignTreeRecordingWithoutTheKerbal_KeepsTheStationHold()
        {
            CommitSpawnedStationMission();
            CommitForeignStationRecording(SpawnPid, SpawnGuid, new[] { Bob }, TerminalState.Recovered);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation bounded by foreign recording"));
        }

        // catches: the mirror direction. A foreign recording of the station that STARTED
        // before the station mission ended must not take the hold (it cannot carry on a kerbal
        // the station still held after it began), and the later mission's own open-ended row
        // must not be handed back to the earlier mission.
        [Fact]
        public void ForeignTreeRecordingStartingBeforeTheHoldEnds_HandsOffNothing()
        {
            CommitSpawnedStationMission();
            var tree = new RecordingTree
            {
                Id = LaterTree,
                TreeName = "Ship T",
                RootRecordingId = StationLaterRec,
                ActiveRecordingId = StationLaterRec,
            };
            CommitTree(tree, Rec(StationLaterRec, LaterTree, SpawnPid, SpawnGuid, 500.0, 600.0,
                TerminalState.Recovered, new[] { Jeb }, null));

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation bounded by foreign recording"));
        }

        // catches: a later recording of the same vessel in the SAME tree claiming that tree's
        // own hold (in-tree hand-offs belong to the split / chain rules, never this claim).
        [Fact]
        public void SameTreeLaterRecordingOfTheVessel_DoesNotClaim()
        {
            var tree = new RecordingTree
            {
                Id = StationTree,
                TreeName = "Station",
                RootRecordingId = StationRec,
                ActiveRecordingId = StationLaterRec,
            };
            CommitTree(tree,
                Rec(StationRec, StationTree, StationPid, StationGuid, 100.0, 1000.0,
                    TerminalState.Orbiting, new[] { Jeb }, new[] { Jeb }),
                Rec(StationLaterRec, StationTree, StationPid, StationGuid, LaterStartUT, LaterEndUT,
                    TerminalState.Recovered, new[] { Jeb }, null));

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation bounded by foreign recording"));
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
