using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling S8 (2026-09-26, todo KSP-SETTINGS-AUDIT-2026-09-26): a recorded crew
    /// death follows stock <c>Difficulty.MissingCrewsRespawn</c> AS IT STOOD AT THE DEATH.
    /// With respawn on, the kerbal is held (lost) over [UT 0, death UT + RespawnTimer) and
    /// free after; with it off the death is permanent. The policy is stamped on the
    /// recording when its end states first record a death, so a later difficulty edit
    /// does not rewrite history.
    ///
    /// <para>The walk cells drive the REAL production recalculation
    /// (<see cref="LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT"/>) and
    /// the tombstone recompute (<see cref="CrewReservationManager.RecomputeAfterTombstones"/>),
    /// and read what the rest of Parsek reads.</para>
    /// </summary>
    [Collection("Sequential")]
    public class KerbalDeathRespawnTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string RecordingId = "rec-death-jeb";
        private const double StartUT = 100.0;
        private const double DeathUT = 300.0;

        public KerbalDeathRespawnTests()
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
            ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixture helpers
        // ------------------------------------------------------------------

        private static void SetLivePolicy(bool respawns, double timerSeconds)
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool r, out double t) => { r = respawns; t = timerSeconds; return true; };
        }

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        /// <summary>A destroyed flight with Jeb aboard whose end states PopulateCrewEndStates
        /// has not inferred yet (the commit order).</summary>
        private static Recording DestroyedFlight(string id = RecordingId, double endUT = DeathUT)
        {
            var snapshot = CrewSnapshot(Jeb);
            return new Recording
            {
                RecordingId = id,
                VesselName = "Doomed Flea " + id,
                TreeId = "tree-" + id,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = StartUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = TerminalState.Destroyed,
            };
        }

        /// <summary>Commits a recorded death: the recording (with the given stamp, or none)
        /// in the store, and the KerbalAssignment+Dead row CreateKerbalAssignmentActions
        /// writes for it in the ledger. Returns the row.</summary>
        private static GameAction CommitDeath(
            bool? respawns, double timerSeconds, string id = RecordingId, double deathUT = DeathUT,
            string chainId = null)
        {
            var rec = DestroyedFlight(id, deathUT);
            if (chainId != null)
            {
                rec.ChainId = chainId;
                rec.ChainIndex = 1;
            }
            rec.VesselSnapshot = null;
            rec.CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, KerbalEndState.Dead } };
            rec.CrewEndStatesResolved = true;
            rec.CrewDeathRespawns = respawns;
            rec.CrewDeathRespawnSeconds = respawns.HasValue ? timerSeconds : double.NaN;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var row = new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                UT = StartUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = id,
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                StartUT = (float)StartUT,
                EndUT = (float)deathUT,
                KerbalEndStateField = KerbalEndState.Dead,
                Sequence = 1
            };
            Ledger.AddAction(row);
            return row;
        }

        private static KerbalsModule Walk(double clockUT)
        {
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "respawn-test");
            KerbalsModule kerbals = LedgerOrchestrator.Kerbals;
            Assert.NotNull(kerbals);
            return kerbals;
        }

        private static void AssertLostUntil(KerbalsModule kerbals, double respawnUT, string clock)
        {
            Assert.True(kerbals.Reservations.ContainsKey(Jeb), "no reservation at clock " + clock);
            var r = kerbals.Reservations[Jeb];
            Assert.False(r.IsPermanent, "respawning death read permanent at clock " + clock);
            Assert.Equal(respawnUT, r.ReservedUntilUT);
            Assert.Equal(respawnUT, r.DeathRespawnUT);
            Assert.True(KerbalsModule.IsRespawnPendingHold(r));
            Assert.True(KerbalsModule.IsLossHold(r));
            Assert.True(kerbals.IsReservedNow(Jeb), "not held at clock " + clock);
            Assert.False(kerbals.IsKerbalAvailable(Jeb));
            Assert.True(kerbals.ShouldFilterFromCrewDialog(Jeb));
            // Stock leaves a dead / missing kerbal unreplaced: no stand-in slot is demanded.
            Assert.False(kerbals.Slots.ContainsKey(Jeb), "stand-in slot created at clock " + clock);
        }

        private static void AssertRespawned(KerbalsModule kerbals, string clock)
        {
            Assert.True(kerbals.Reservations.ContainsKey(Jeb));
            Assert.False(kerbals.IsReservedNow(Jeb), "still held at clock " + clock);
            Assert.True(kerbals.IsKerbalAvailable(Jeb), "not available at clock " + clock);
            Assert.False(kerbals.ShouldFilterFromCrewDialog(Jeb));
            Assert.Equal(KerbalReservationKind.NotManaged, kerbals.GetReservationKind(Jeb));
        }

        private static void AssertPermanent(KerbalsModule kerbals, string clock)
        {
            Assert.True(kerbals.Reservations.ContainsKey(Jeb));
            var r = kerbals.Reservations[Jeb];
            Assert.True(r.IsPermanent, "death not permanent at clock " + clock);
            Assert.True(double.IsPositiveInfinity(r.ReservedUntilUT));
            Assert.True(double.IsNaN(r.DeathRespawnUT));
            Assert.True(KerbalsModule.IsLossHold(r));
            Assert.False(KerbalsModule.IsRespawnPendingHold(r));
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // ------------------------------------------------------------------
        // The stamp (taken at the death, never at replay)
        // ------------------------------------------------------------------

        // catches: the policy not being captured at the death (nothing to replay later).
        [Theory]
        [InlineData(true, 7200.0)]
        [InlineData(true, 1234.5)]
        [InlineData(false, 7200.0)]
        public void PopulateCrewEndStates_StampsTheLivePolicyAtTheDeath(bool respawns, double timer)
        {
            SetLivePolicy(respawns, timer);
            var rec = DestroyedFlight();

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Equal(respawns, rec.CrewDeathRespawns);
            Assert.Equal(timer, rec.CrewDeathRespawnSeconds);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Crew death respawn policy stamped")
                && l.Contains("respawn=" + respawns)
                && l.Contains("timer=" + timer.ToString("R", CultureInfo.InvariantCulture)));
        }

        // catches: a crewless or deathless recording carrying a stamp it does not need.
        [Fact]
        public void PopulateCrewEndStates_NoDeath_NoStamp()
        {
            SetLivePolicy(true, 7200.0);
            var rec = DestroyedFlight();
            rec.TerminalStateValue = TerminalState.Recovered;

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            Assert.False(rec.CrewDeathRespawns.HasValue);
        }

        // catches: a headless / no-game population inventing a policy.
        [Fact]
        public void PopulateCrewEndStates_NoReadableGame_LeavesTheDeathUnstamped()
        {
            var rec = DestroyedFlight();

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.False(rec.CrewDeathRespawns.HasValue);
            Assert.Contains(logLines, l => l.Contains("Crew death respawn policy not stamped"));
        }

        // catches: a difficulty edit after the death rewriting the stamp when the end
        // states are re-inferred (a terminal re-stamp runs PopulateCrewEndStates again).
        [Fact]
        public void PolicyChangedAfterTheDeath_ReInferenceKeepsTheStamp()
        {
            SetLivePolicy(true, 7200.0);
            var rec = DestroyedFlight();
            rec.TerminalStateValue = TerminalState.SubOrbital;
            rec.VesselSnapshot = CrewSnapshot(); // Jeb gone from the end snapshot -> Dead
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Equal(true, rec.CrewDeathRespawns);

            SetLivePolicy(false, 99999.0);
            bool reInferred = KerbalsModule.InvalidateCrewEndStatesForTerminalStamp(
                rec, TerminalState.SubOrbital, TerminalState.Destroyed, "respawn-test");

            Assert.True(reInferred);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Equal(true, rec.CrewDeathRespawns);
            Assert.Equal(7200.0, rec.CrewDeathRespawnSeconds);
            Assert.Contains(logLines, l => l.Contains("Crew death respawn policy kept"));
        }

        [Fact]
        public void ResolveDeathRespawnUT_Cases()
        {
            string reason;
            Assert.Equal(7500.0, KerbalsModule.ResolveDeathRespawnUT(true, 7200.0, 300.0, out reason));
            Assert.Contains("on at the death", reason);
            Assert.Equal(300.0, KerbalsModule.ResolveDeathRespawnUT(true, 0.0, 300.0, out reason));
            Assert.True(double.IsNaN(KerbalsModule.ResolveDeathRespawnUT(false, 7200.0, 300.0, out reason)));
            Assert.Contains("off at the death", reason);
            Assert.True(double.IsNaN(KerbalsModule.ResolveDeathRespawnUT(null, 7200.0, 300.0, out reason)));
            Assert.Contains("no respawn policy stamped", reason);
            Assert.True(double.IsNaN(KerbalsModule.ResolveDeathRespawnUT(true, double.NaN, 300.0, out reason)));
            Assert.True(double.IsNaN(KerbalsModule.ResolveDeathRespawnUT(true, -1.0, 300.0, out reason)));
            Assert.True(double.IsNaN(KerbalsModule.ResolveDeathRespawnUT(true, 7200.0, double.NaN, out reason)));
        }

        // ------------------------------------------------------------------
        // The reservation timeline (the production walk)
        // ------------------------------------------------------------------

        // catches: a respawning death held forever (the pre-ruling behaviour), and the
        // release landing anywhere but death UT + the stamped timer.
        [Theory]
        [InlineData(7200.0, 1000.0, true)]   // inside the respawn window
        [InlineData(7200.0, 7499.9, true)]   // just before the respawn
        [InlineData(7200.0, 7500.0, false)]  // free AT the respawn instant
        [InlineData(7200.0, 1e7, false)]     // long after
        [InlineData(3600.0, 3899.0, true)]   // a custom timer
        [InlineData(3600.0, 3900.0, false)]
        public void RespawnOn_LostUntilDeathPlusTimer_ThenAvailable(double timer, double clock, bool held)
        {
            CommitDeath(true, timer);

            KerbalsModule kerbals = Walk(clock);

            string label = clock.ToString("R", CultureInfo.InvariantCulture);
            if (held) AssertLostUntil(kerbals, DeathUT + timer, label);
            else AssertRespawned(kerbals, label);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman'")
                && l.Contains("respawn scheduled"));
        }

        // catches: the release transition not saying a respawn happened.
        [Fact]
        public void RespawnOn_CrossingTheRespawnLogsTheRespawnApplied()
        {
            CommitDeath(true, 7200.0);

            Walk(1000.0);
            Walk(8000.0);

            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation released: 'Jebediah Kerman' endUT=7500.0")
                && l.Contains("respawn applied"));
            Assert.Contains(logLines, l => l.Contains("PostWalk summary:") && l.Contains("respawnPending=1"));
            Assert.Contains(logLines, l => l.Contains("PostWalk summary:") && l.Contains("released=1")
                && l.Contains("respawnPending=0"));
        }

        // catches: respawn off (or an unstamped legacy death) becoming finite.
        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public void RespawnOffOrUnstamped_DeathIsPermanent(bool? respawns)
        {
            CommitDeath(respawns, 7200.0);

            KerbalsModule kerbals = Walk(1e7);

            AssertPermanent(kerbals, "1e7");
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman'")
                && l.Contains("is permanent (respawn suppressed:"));
            // The permanent line keeps the exact shape the harness specs pin.
            Assert.Contains(logLines, l => l.Contains("Reservation: 'Jebediah Kerman' endUT=INDEFINITE (Dead)"));
        }

        // catches: the walk re-reading the LIVE difficulty instead of the stamp.
        [Fact]
        public void PolicyChangedAfterTheDeath_TheWalkStillUsesTheStamp()
        {
            CommitDeath(true, 7200.0);
            SetLivePolicy(false, 99999.0); // player turned respawn off after the death

            KerbalsModule kerbals = Walk(8000.0);

            AssertRespawned(kerbals, "8000");
            Assert.Equal(7500.0, kerbals.Reservations[Jeb].ReservedUntilUT);
        }

        // catches: a committed FUTURE death (a rewind put the clock before it) not holding
        // the kerbal until its respawn.
        [Fact]
        public void FutureDeath_HeldFromNowUntilItsRespawn()
        {
            CommitDeath(true, 7200.0, deathUT: 5000.0);

            KerbalsModule kerbals = Walk(200.0); // before the death

            AssertLostUntil(kerbals, 12200.0, "200");
            Assert.True(KerbalsModule.IsReservationReleaseDue(12200.0, kerbals.NextReservationReleaseUT, double.NaN));
        }

        // catches: a later flight after the respawn being cut short, or the death still
        // reading as Lost when the later flight is what holds him.
        [Fact]
        public void LaterFlightAfterTheRespawn_ExtendsTheHold_AsAnOrdinaryReservation()
        {
            CommitDeath(true, 1000.0); // respawn at 1300
            var later = new Recording
            {
                RecordingId = "rec-after-respawn",
                VesselName = "Second Flea",
                TreeId = "tree-after",
                VesselSnapshot = CrewSnapshot(Jeb),
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 2000.0,
                ExplicitEndUT = 2500.0,
                CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, KerbalEndState.Recovered } },
                CrewEndStatesResolved = true
            };
            RecordingStore.AddRecordingWithTreeForTesting(later);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                UT = 2000.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-after-respawn",
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                StartUT = 2000f,
                EndUT = 2500f,
                KerbalEndStateField = KerbalEndState.Recovered,
                Sequence = 1
            });

            KerbalsModule kerbals = Walk(1500.0);

            var r = kerbals.Reservations[Jeb];
            Assert.Equal(2500.0, r.ReservedUntilUT);
            Assert.Equal(1300.0, r.DeathRespawnUT);
            Assert.False(KerbalsModule.IsRespawnPendingHold(r));
            Assert.False(KerbalsModule.IsLossHold(r));
            Assert.True(kerbals.IsReservedNow(Jeb));
        }

        // ------------------------------------------------------------------
        // Open-ended holds (owner decision 2026-09-26): a death that anything keeps
        // open-ended is a permanent loss, respawn on or off
        // ------------------------------------------------------------------

        /// <summary>Commits a looping segment (index 0) of <paramref name="chainId"/>: the
        /// shape that makes KerbalsModule treat the whole chain as replaying forever.</summary>
        private static void CommitLoopingSegment(string chainId)
        {
            var loop = new Recording
            {
                RecordingId = "rec-loop-seg",
                VesselName = "Loop Segment",
                TreeId = "tree-loop-seg",
                ChainId = chainId,
                ChainIndex = 0,
                LoopPlayback = true,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 90.0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(loop);
        }

        /// <summary>Commits another flight of Jeb's that is still Aboard (no recovery ever
        /// closes it): an open-ended co-row for the same kerbal.</summary>
        private static void CommitOpenEndedAboardFlight(double startUT)
        {
            var aboard = new Recording
            {
                RecordingId = "rec-still-aboard",
                VesselName = "Parked Flea",
                TreeId = "tree-still-aboard",
                VesselSnapshot = CrewSnapshot(Jeb),
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = startUT,
                ExplicitEndUT = startUT + 200.0,
                TerminalStateValue = TerminalState.Orbiting,
                CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, KerbalEndState.Aboard } },
                CrewEndStatesResolved = true
            };
            RecordingStore.AddRecordingWithTreeForTesting(aboard);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                UT = startUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-still-aboard",
                KerbalName = Jeb,
                KerbalRole = "Pilot",
                StartUT = (float)startUT,
                EndUT = (float)(startUT + 200.0),
                KerbalEndStateField = KerbalEndState.Aboard,
                Sequence = 1
            });
        }

        // catches: a respawn-on death in a looping chain becoming an ordinary never-ending
        // hold with a free stand-in (G1), or respawning; respawn off / unstamped unchanged.
        [Theory]
        [InlineData(true, 1000.0)]   // inside the would-be respawn window
        [InlineData(true, 1e7)]      // long after the would-be respawn
        [InlineData(false, 1e7)]
        [InlineData(null, 1e7)]
        public void DeathInLoopingChain_IsPermanentLoss_NoStandIn(bool? respawns, double clock)
        {
            CommitLoopingSegment("chain-loop");
            CommitDeath(respawns, 7200.0, chainId: "chain-loop");

            KerbalsModule kerbals = Walk(clock);

            string label = clock.ToString("R", CultureInfo.InvariantCulture);
            AssertPermanent(kerbals, label);
            Assert.False(kerbals.IsKerbalAvailable(Jeb));
            Assert.False(kerbals.Slots.ContainsKey(Jeb), "stand-in slot created at clock " + label);
            Assert.Equal(KerbalsPresentation.RosterStatus.Lost,
                KerbalsPresentation.ClassifyStatus(Jeb, false, false, kerbals.Reservations[Jeb], null, null));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman'")
                && l.Contains("is permanent (respawn suppressed:"));
            Assert.DoesNotContain(logLines, l => l.Contains("respawn scheduled"));
            if (respawns == true)
            {
                Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                    && l.Contains("respawn suppressed: looping chain"));
            }
        }

        // catches: an un-closed Aboard co-row of the same kerbal turning a respawn-on death
        // into an ordinary open-ended hold with a stand-in (G1), in either row order;
        // respawn off / unstamped already permanent and unchanged.
        [Theory]
        [InlineData(true, 50.0)]     // the open-ended flight is earlier than the death
        [InlineData(true, 2000.0)]   // ... later than the death
        [InlineData(false, 50.0)]
        [InlineData(null, 2000.0)]
        public void DeathWithOpenEndedAboardCoRow_IsPermanentLoss_NoStandIn(bool? respawns, double coRowStartUT)
        {
            CommitDeath(respawns, 7200.0);
            CommitOpenEndedAboardFlight(coRowStartUT);

            KerbalsModule kerbals = Walk(1e7);

            AssertPermanent(kerbals, "1e7");
            Assert.False(kerbals.Slots.ContainsKey(Jeb), "stand-in slot created");
            Assert.Equal(KerbalsPresentation.RosterStatus.Lost,
                KerbalsPresentation.ClassifyStatus(Jeb, false, false, kerbals.Reservations[Jeb], null, null));
            bool suppressedLogged = logLines.Exists(l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'Jebediah Kerman' respawn suppressed: open-ended co-row"));
            Assert.Equal(respawns == true, suppressedLogged);
        }

        // catches: the post-merge rule touching anything but an open-ended respawn-on hold
        // (a plain respawn-pending death, a finite later flight, a permanent death).
        [Fact]
        public void ResolveOpenEndedRespawnDeaths_ConvertsOnlyOpenEndedRespawnHolds()
        {
            var holds = new Dictionary<string, KerbalsModule.KerbalReservation>
            {
                { "pending", new KerbalsModule.KerbalReservation
                    { KerbalName = "pending", ReservedUntilUT = 7500.0, DeathRespawnUT = 7500.0 } },
                { "laterFlight", new KerbalsModule.KerbalReservation
                    { KerbalName = "laterFlight", ReservedUntilUT = 9000.0, DeathRespawnUT = 7500.0 } },
                { "openEnded", new KerbalsModule.KerbalReservation
                    { KerbalName = "openEnded", ReservedUntilUT = double.PositiveInfinity, DeathRespawnUT = 7500.0 } },
                { "permanent", new KerbalsModule.KerbalReservation
                    { KerbalName = "permanent", ReservedUntilUT = double.PositiveInfinity, IsPermanent = true } },
                { "aboardOnly", new KerbalsModule.KerbalReservation
                    { KerbalName = "aboardOnly", ReservedUntilUT = double.PositiveInfinity } },
            };

            Assert.Equal(1, KerbalsModule.ResolveOpenEndedRespawnDeaths(holds));

            Assert.True(KerbalsModule.IsRespawnPendingHold(holds["pending"]));
            Assert.False(holds["laterFlight"].IsPermanent);
            Assert.Equal(7500.0, holds["laterFlight"].DeathRespawnUT);
            Assert.True(holds["openEnded"].IsPermanent);
            Assert.True(double.IsNaN(holds["openEnded"].DeathRespawnUT));
            Assert.True(KerbalsModule.IsLossHold(holds["openEnded"]));
            Assert.True(holds["permanent"].IsPermanent);
            Assert.False(holds["aboardOnly"].IsPermanent);
            Assert.False(KerbalsModule.IsLossHold(holds["aboardOnly"]));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Death hold: 'openEnded' respawn suppressed: open-ended co-row"));
            Assert.Equal(0, KerbalsModule.ResolveOpenEndedRespawnDeaths(null));
        }

        // ------------------------------------------------------------------
        // Rewind / tombstones
        // ------------------------------------------------------------------

        private static ParsekScenario InstallScenarioWithTombstones(params LedgerTombstone[] tombstones)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(tombstones),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // catches: a tombstoned death leaving its respawn window behind.
        [Fact]
        public void TombstonedDeath_LeavesNoRespawnWindow()
        {
            var death = CommitDeath(true, 7200.0);
            var kerbals = new KerbalsModule();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            KerbalsModule.LiveClockUTProviderForTesting = () => 1000.0;

            InstallScenarioWithTombstones();
            CrewReservationManager.RecomputeAfterTombstones();
            Assert.True(kerbals.Reservations.ContainsKey(Jeb));
            Assert.True(KerbalsModule.IsRespawnPendingHold(kerbals.Reservations[Jeb]));
            Assert.Contains(logLines, l => l.Contains("[CrewReservations]")
                && l.Contains("(permanent=0 temporary=1 respawnDeath=1)"));

            InstallScenarioWithTombstones(new LedgerTombstone
            {
                TombstoneId = "tomb_death",
                ActionId = death.ActionId,
                RetiringRecordingId = "rec_provisional",
                UT = 150.0,
                CreatedRealTime = DateTime.UtcNow.ToString("o"),
            });
            CrewReservationManager.RecomputeAfterTombstones();

            Assert.False(kerbals.Reservations.ContainsKey(Jeb));
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[CrewReservations]")
                && l.Contains("0 reservations remain (permanent=0 temporary=0)."));
        }

        // catches: the respawn-off recompute line changing shape (specs pin it verbatim).
        [Fact]
        public void RespawnOffRecomputeLine_KeepsItsPinnedShape()
        {
            CommitDeath(false, 7200.0);
            var kerbals = new KerbalsModule();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);
            KerbalsModule.LiveClockUTProviderForTesting = () => 1000.0;
            InstallScenarioWithTombstones();

            CrewReservationManager.RecomputeAfterTombstones();

            Assert.Contains(logLines, l => l.Contains("[CrewReservations]")
                && l.Contains("1 reservations remain (permanent=1 temporary=0)."));
        }

        // ------------------------------------------------------------------
        // Persistence and the recording-shape paths that carry the stamp
        // ------------------------------------------------------------------

        // catches: the stamp not surviving save/load (history lost on reload).
        [Theory]
        [InlineData(true, 7200.0)]
        [InlineData(false, 3600.0)]
        public void Codec_RoundTripsTheStamp(bool respawns, double timer)
        {
            var rec = DestroyedFlight();
            rec.CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, KerbalEndState.Dead } };
            rec.CrewEndStatesResolved = true;
            rec.CrewDeathRespawns = respawns;
            rec.CrewDeathRespawnSeconds = timer;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Equal(respawns.ToString(), node.GetValue("crewDeathRespawn"));
            Assert.Equal(timer.ToString("R", CultureInfo.InvariantCulture), node.GetValue("crewDeathRespawnSec"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.Equal(respawns, restored.CrewDeathRespawns);
            Assert.Equal(timer, restored.CrewDeathRespawnSeconds);
        }

        // catches: every unstamped recording growing a key (byte-identity of old saves).
        [Fact]
        public void Codec_UnstampedRecordingWritesNoKey()
        {
            var rec = DestroyedFlight();
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Null(node.GetValue("crewDeathRespawn"));
            Assert.Null(node.GetValue("crewDeathRespawnSec"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.False(restored.CrewDeathRespawns.HasValue);
            Assert.True(double.IsNaN(restored.CrewDeathRespawnSeconds));
        }

        // catches: the generator unable to author the new keys.
        [Fact]
        public void RecordingBuilder_AuthorsTheStamp()
        {
            ConfigNode node = new RecordingBuilder("Doomed Flea")
                .WithRecordingId("rec-builder")
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithCrewDeathRespawn(true, 7200.0)
                .BuildV3Metadata();
            Assert.Equal("True", node.GetValue("crewDeathRespawn"));
            Assert.Equal("7200", node.GetValue("crewDeathRespawnSec"));
        }

        // catches: a split leaving the death's stamp on the first half (which re-derives
        // without the death) and the second half re-stamping from the live difficulty.
        [Fact]
        public void OptimizerSplit_MovesTheStampWithTheEndStates()
        {
            var original = DestroyedFlight();
            original.CrewEndStates = new Dictionary<string, KerbalEndState> { { Jeb, KerbalEndState.Dead } };
            original.CrewEndStatesResolved = true;
            original.CrewDeathRespawns = true;
            original.CrewDeathRespawnSeconds = 7200.0;
            var second = new Recording { RecordingId = "rec-second-half" };

            RecordingOptimizer.MoveCrewEndStatesToSecondHalf(original, second);

            Assert.Equal(true, second.CrewDeathRespawns);
            Assert.Equal(7200.0, second.CrewDeathRespawnSeconds);
            Assert.False(original.CrewDeathRespawns.HasValue);
            Assert.True(double.IsNaN(original.CrewDeathRespawnSeconds));
        }

        // catches: DeepClone dropping the stamp (the RP splitter / hydration repair clone).
        [Fact]
        public void DeepClone_CarriesTheStamp()
        {
            var rec = DestroyedFlight();
            rec.CrewDeathRespawns = false;
            rec.CrewDeathRespawnSeconds = 42.0;

            var clone = Recording.DeepClone(rec);

            Assert.Equal(false, clone.CrewDeathRespawns);
            Assert.Equal(42.0, clone.CrewDeathRespawnSeconds);
        }

        // ------------------------------------------------------------------
        // What the player sees
        // ------------------------------------------------------------------

        private static string Fmt(double ut) => "D" + ut.ToString("0", CultureInfo.InvariantCulture);

        private static KerbalsModule.KerbalReservation RespawnHold(double respawnUT)
        {
            return new KerbalsModule.KerbalReservation
            {
                KerbalName = Jeb,
                ReservedUntilUT = respawnUT,
                DeathRespawnUT = respawnUT
            };
        }

        [Fact]
        public void KerbalsWindow_RespawningDeathReadsLostUntilTheRespawnDate()
        {
            var hold = RespawnHold(7500.0);
            Assert.Equal(KerbalsPresentation.RosterStatus.Lost,
                KerbalsPresentation.ClassifyStatus(Jeb, false, false, hold, null, null));

            string date = KerbalsPresentation.FormatReleaseDate(hold, Fmt);
            Assert.Equal("Lost until " + date,
                KerbalsPresentation.FormatStatus(KerbalsPresentation.RosterStatus.Lost,
                    Jeb, null, null, null, false, date));
            string tip = KerbalsPresentation.FormatStatusTooltip(KerbalsPresentation.RosterStatus.Lost,
                Jeb, null, null, null, null, false, date);
            Assert.Contains("Stock respawn returns this kerbal on " + date + ".", tip);
            Assert.EndsWith(KerbalsPresentation.LostReFlyRemedy, tip);

            // A permanent death keeps the plain wording.
            Assert.Equal("Lost", KerbalsPresentation.FormatStatus(KerbalsPresentation.RosterStatus.Lost,
                Jeb, null, null, null, false, null));
        }

        [Fact]
        public void StockScreens_RespawningDeathIsMarkedLost_WithTheRespawnDate()
        {
            var text = StockUiReservationPredicates.ExplainKerbalReservation(
                null, Jeb, RespawnHold(7500.0), null, null, Fmt);
            Assert.Equal("Lost until D7500", text.Title);
            Assert.Contains("Stock respawn returns this kerbal on D7500.", text.Body);

            var permanent = StockUiReservationPredicates.ExplainKerbalReservation(
                null, Jeb, new KerbalsModule.KerbalReservation
                {
                    KerbalName = Jeb, ReservedUntilUT = double.PositiveInfinity, IsPermanent = true
                }, null, null, Fmt);
            Assert.Equal("Lost", permanent.Title);
            Assert.DoesNotContain("respawn", permanent.Body);
        }

        [Fact]
        public void Timeline_DeathEntryNamesTheStampedRespawnDelay()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            Assert.Equal("Lost: Jebediah Kerman (Flea), respawns after 2h 0m",
                TimelineEntryDisplay.GetCrewDeathText(Jeb, "Flea", 7200.0));
            Assert.Equal("Lost: Jebediah Kerman (Flea)",
                TimelineEntryDisplay.GetCrewDeathText(Jeb, "Flea"));
        }
    }
}
