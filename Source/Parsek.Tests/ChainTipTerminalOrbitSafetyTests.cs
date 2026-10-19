using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-TIP-SPAWN-SKIPS-ORBIT-SAFETY-GATE: a ghost-chain tip that spawns on its recorded
    /// terminal orbit runs the same periapsis safety decision as the ordinary spawn path, and a
    /// standing deferral or refusal stops the next attempt before any placement work.
    /// </summary>
    [Collection("Sequential")]
    public class ChainTipTerminalOrbitSafetyTests : IDisposable
    {
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double SpawnUT = 5000.0;

        private readonly List<string> logLines = new List<string>();

        public ChainTipTerminalOrbitSafetyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            VesselGhoster.TerminalOrbitSafetyCheckOverrideForTesting = null;
        }

        public void Dispose()
        {
            VesselGhoster.TerminalOrbitSafetyCheckOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        private static Recording MakeTip()
        {
            return new Recording { RecordingId = "rec-station-tip", VesselName = "Station" };
        }

        private static GhostChain MakeChain(bool blocked)
        {
            return new GhostChain
            {
                OriginalVesselPid = 4242u,
                TipRecordingId = "rec-station-tip",
                SpawnBlocked = blocked,
            };
        }

        /// <summary>
        /// Installs a check that runs the production geometry decision over the given orbit
        /// shape and stamps the tip exactly as the live wrapper does.
        /// </summary>
        private static void InstallGeometryCheck(double periapsis, double apoapsis, double nextSafeUT = double.NaN)
        {
            VesselGhoster.TerminalOrbitSafetyCheckOverrideForTesting = (tip, orbit, ut, alt) =>
            {
                TerminalOrbitSpawnSafetyDecision decision =
                    VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                        true, KerbinAtmosphereDepth, alt, periapsis, apoapsis);
                switch (decision.Action)
                {
                    case TerminalOrbitSpawnSafetyAction.CannotSpawnSafely:
                        TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(tip, decision, ut, double.NaN);
                        return false;
                    case TerminalOrbitSpawnSafetyAction.DeferUntilSafe:
                        TerminalOrbitSpawnSafety.MarkDeferred(tip, decision, ut, nextSafeUT, double.NaN);
                        return false;
                    default:
                        TerminalOrbitSpawnSafety.Clear(tip);
                        return true;
                }
            };
        }

        // ---- the post-resolve gate ----

        [Fact]
        public void Gate_NoTerminalOrbit_PassesWithoutRunningTheCheck()
        {
            int calls = 0;
            VesselGhoster.TerminalOrbitSafetyCheckOverrideForTesting = (tip, orbit, ut, alt) =>
            {
                calls++;
                return false;
            };

            Assert.True(VesselGhoster.PassChainTipTerminalOrbitSafety(
                MakeChain(false), MakeTip(), null, null, SpawnUT, 80000.0, "chain-tip"));
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Gate_SubAtmosphericPeriapsis_RefusesStampsTheTipAndEndsTheCollisionBlock()
        {
            // RC-1 cycle 1: the Station tip's terminal orbit had a 14 km periapsis on Kerbin.
            InstallGeometryCheck(periapsis: 14000.0, apoapsis: 90000.0);
            var tip = MakeTip();
            var chain = MakeChain(blocked: true);

            bool passed = VesselGhoster.PassChainTipTerminalOrbitSafety(
                chain, tip, null, new Orbit(), SpawnUT, 85000.0, "chain-tip-blocked");

            Assert.False(passed);
            Assert.True(tip.TerminalSpawnCannotSpawnSafely);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude,
                tip.TerminalSpawnSafetyReasonCode);
            Assert.False(chain.SpawnBlocked);
            Assert.Contains(logLines, l => l.Contains("[Ghoster]")
                && l.Contains("Chain tip spawn stopped by terminal orbit safety")
                && l.Contains("site=chain-tip-blocked")
                && l.Contains("outcome=refused")
                && l.Contains("reason=periapsis-below-safe-altitude"));
            Assert.Contains(logLines, l => l.Contains("reason=" + VesselGhoster.ChainTipTerminalOrbitRefusedReason));
        }

        [Fact]
        public void Gate_LowOnAClearOrbit_DefersAndKeepsTheCollisionBlock()
        {
            InstallGeometryCheck(periapsis: 72000.0, apoapsis: 150000.0, nextSafeUT: SpawnUT + 120.0);
            var tip = MakeTip();
            var chain = MakeChain(blocked: true);

            bool passed = VesselGhoster.PassChainTipTerminalOrbitSafety(
                chain, tip, null, new Orbit(), SpawnUT, 72500.0, "chain-tip");

            Assert.False(passed);
            Assert.True(tip.TerminalSpawnSafetyDeferred);
            Assert.False(tip.TerminalSpawnCannotSpawnSafely);
            Assert.True(chain.SpawnBlocked);
            Assert.Contains(logLines, l => l.Contains("outcome=deferred")
                && l.Contains("nextAttemptUT=5120.00"));
        }

        [Fact]
        public void Gate_SafeOrbit_PassesAndClearsAStaleVerdict()
        {
            InstallGeometryCheck(periapsis: 80000.0, apoapsis: 90000.0);
            var tip = MakeTip();
            tip.TerminalSpawnSafetyDeferred = true;
            tip.TerminalSpawnNextAttemptUT = SpawnUT - 1.0;

            Assert.True(VesselGhoster.PassChainTipTerminalOrbitSafety(
                MakeChain(false), tip, null, new Orbit(), SpawnUT, 85000.0, "chain-tip"));
            Assert.False(TerminalOrbitSpawnSafety.HasActiveHold(tip));
            Assert.DoesNotContain(logLines, l => l.Contains("Chain tip spawn stopped by terminal orbit safety"));
        }

        // ---- the entry verdict check ----

        [Fact]
        public void EntryVerdict_CleanTip_DoesNotStop()
        {
            Assert.False(VesselGhoster.StopChainTipSpawnForTerminalOrbitVerdict(
                MakeChain(false), MakeTip(), SpawnUT, "chain-tip"));
        }

        [Fact]
        public void EntryVerdict_RefusedTip_StopsAndEndsTheCollisionBlock()
        {
            var tip = MakeTip();
            tip.TerminalSpawnCannotSpawnSafely = true;
            tip.TerminalSpawnSafetyReasonCode = TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude;
            var chain = MakeChain(blocked: true);

            Assert.True(VesselGhoster.StopChainTipSpawnForTerminalOrbitVerdict(
                chain, tip, SpawnUT, "chain-tip-blocked"));
            Assert.False(chain.SpawnBlocked);
            Assert.Contains(logLines, l => l.Contains("Chain tip spawn skipped for terminal orbit safety")
                && l.Contains("verdict=Refused"));
        }

        [Fact]
        public void EntryVerdict_DeferralBeforeItsUT_StopsAndKeepsTheBlock()
        {
            var tip = MakeTip();
            tip.TerminalSpawnSafetyDeferred = true;
            tip.TerminalSpawnNextAttemptUT = SpawnUT + 60.0;
            var chain = MakeChain(blocked: true);

            Assert.True(VesselGhoster.StopChainTipSpawnForTerminalOrbitVerdict(
                chain, tip, SpawnUT, "chain-tip-blocked"));
            Assert.True(chain.SpawnBlocked);
            Assert.Contains(logLines, l => l.Contains("verdict=Hold"));
        }

        [Fact]
        public void EntryVerdict_DeferralPastItsUT_LetsTheSpawnReEvaluate()
        {
            var tip = MakeTip();
            tip.TerminalSpawnSafetyDeferred = true;
            tip.TerminalSpawnNextAttemptUT = SpawnUT - 60.0;

            Assert.False(VesselGhoster.StopChainTipSpawnForTerminalOrbitVerdict(
                MakeChain(false), tip, SpawnUT, "chain-tip"));
        }

        // ---- wiring: both chain-tip spawn paths run both checks before spawning ----

        [Theory]
        [InlineData("internal uint SpawnAtChainTip(")]
        [InlineData("internal uint TrySpawnBlockedChain(")]
        public void Wiring_ChainTipSpawnPaths_RunTheVerdictCheckAndTheGateBeforeSpawning(string signature)
        {
            string body = ExtractMethodBody(StripLineComments(File.ReadAllText(VesselGhosterSourcePath())), signature);

            int verdict = body.IndexOf("StopChainTipSpawnForTerminalOrbitVerdict(", StringComparison.Ordinal);
            int gate = body.IndexOf("PassChainTipTerminalOrbitSafety(", StringComparison.Ordinal);
            int spawn = body.IndexOf("SpawnChainTipWithResolvedState(", StringComparison.Ordinal);

            Assert.True(verdict >= 0, signature + " does not run the entry verdict check");
            Assert.True(gate >= 0, signature + " does not run the terminal orbit safety gate");
            Assert.True(spawn >= 0, signature + " no longer spawns through SpawnChainTipWithResolvedState");
            Assert.True(verdict < gate && gate < spawn,
                signature + " must check the verdict, then gate the orbit, then spawn");
        }

        private static string VesselGhosterSourcePath()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "VesselGhoster.cs"));
        }

        private static string StripLineComments(string source)
        {
            return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found in VesselGhoster.cs");
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException(signature + " body is unterminated");
        }
    }
}
