using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Career settings audit 2026-11-04 sections 3.5 and 3.6: a recorded crew death keeps the
    /// respawn decision stock made at the death. The timer comes from stock's own respawn
    /// instant (the roster's UTaR), and turning respawn off later does not kill a kerbal whose
    /// recorded death was stamped with respawn on.
    /// </summary>
    [Collection("Sequential")]
    public class CrewRespawnSettingsTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private readonly List<string> logLines = new List<string>();

        public CrewRespawnSettingsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.MissingRespawnProviderForTesting = null;
        }

        public void Dispose()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.MissingRespawnProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording DestroyedFlightWithJeb(double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("crew", Jeb);
            return new Recording
            {
                RecordingId = "rec-respawn",
                VesselName = "Doomed Flea",
                TreeId = "tree-respawn",
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = endUT,
                TerminalStateValue = TerminalState.Destroyed,
            };
        }

        // ---------------------------------------------------------------- 3.6 the stamp

        [Fact]
        public void Stamp_TakesStocksRespawnInstant_NotTheSliderAtCommit()
        {
            // Jeb died at 10000 with a 2 h timer (UTaR 17200); before the commit the player set
            // the slider to 100 h. The stamp is stock's own 7200 s.
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool r, out double t) => { r = true; t = 360000.0; return true; };
            KerbalsModule.MissingRespawnProviderForTesting =
                (string name, out double utar) => { utar = 17200.0; return name == Jeb; };
            Recording rec = DestroyedFlightWithJeb(10000.0);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(true, rec.CrewDeathRespawns);
            Assert.Equal(7200.0, rec.CrewDeathRespawnSeconds);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Crew death respawn policy stamped from the roster")
                && l.Contains("timer=7200"));
        }

        [Fact]
        public void Stamp_NoMissingKerbalInTheRoster_FallsBackToTheLivePolicy()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool r, out double t) => { r = false; t = 7200.0; return true; };
            KerbalsModule.MissingRespawnProviderForTesting =
                (string name, out double utar) => { utar = double.NaN; return false; };
            Recording rec = DestroyedFlightWithJeb(10000.0);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(false, rec.CrewDeathRespawns);
            Assert.DoesNotContain(logLines, l => l.Contains("stamped from the roster"));
        }

        [Fact]
        public void ResolveFromRoster_TakesTheLongestTimer_NeverBelowZero()
        {
            KerbalsModule.MissingRespawnReader read = (string name, out double utar) =>
            {
                utar = name == "a" ? 900.0 : name == "b" ? 1500.0 : double.NaN;
                return name == "a" || name == "b";
            };
            Assert.True(KerbalsModule.TryResolveRespawnStampFromRoster(
                new[] { "a", "b", "c" }, 1000.0, read, out double seconds, out string evidence));
            Assert.Equal(500.0, seconds);
            Assert.Contains("b Missing", evidence);

            Assert.True(KerbalsModule.TryResolveRespawnStampFromRoster(
                new[] { "a" }, 1000.0, read, out seconds, out _));
            Assert.Equal(0.0, seconds);

            Assert.False(KerbalsModule.TryResolveRespawnStampFromRoster(
                new[] { "c" }, 1000.0, read, out _, out _));
        }

        // ---------------------------------------------------------------- 3.5 the respawn

        private static KerbalsModule.KerbalReservation RespawnOnDeath(double respawnUT)
            => new KerbalsModule.KerbalReservation
            {
                KerbalName = Jeb, ReservedUntilUT = respawnUT, DeathRespawnUT = respawnUT,
            };

        [Fact]
        public void Decide_RespawnTurnedOffLater_ARecordedRespawnOnDeathStillRespawns()
        {
            var r = RespawnOnDeath(17200.0);
            Assert.Equal(CrewRespawnTimerPatch.Decision.RespawnNow,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, false, r));
            // Stock's timer ran out first (a longer stamped timer): he stays Missing until it.
            Assert.Equal(CrewRespawnTimerPatch.Decision.KeepMissing,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, false, RespawnOnDeath(20000.0)));
        }

        [Fact]
        public void Decide_EveryOtherCase_IsStock()
        {
            var r = RespawnOnDeath(17200.0);
            // Respawn still on: stock respawns him itself.
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, true, r));
            // Not Missing, or the timer has not run out.
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(false, 17300.0, 17200.0, false, r));
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(true, 17000.0, 17200.0, false, r));
            // No recorded death, a permanent one, or one with no respawn: stock decides.
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, false, null));
            var permanent = RespawnOnDeath(17200.0);
            permanent.IsPermanent = true;
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, false, permanent));
            Assert.Equal(CrewRespawnTimerPatch.Decision.Stock,
                CrewRespawnTimerPatch.Decide(true, 17300.0, 17200.0, false,
                    new KerbalsModule.KerbalReservation { KerbalName = Jeb, ReservedUntilUT = double.PositiveInfinity }));
        }
    }
}
