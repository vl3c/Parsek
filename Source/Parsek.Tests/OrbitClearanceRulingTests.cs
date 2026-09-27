using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Operator ruling 2026-09-27 (todo SS1-REFUSED-TERMINAL-ORBIT-GHOST-HELD-FOR-THE-SCENE):
    /// "Any orbit Parsek calls Orbiting will spawn. A periapsis inside the atmosphere is a
    /// decaying flight and ends SubOrbital or Destroyed, never as a refused orbit. If a spawn is
    /// still refused for a reason that can never clear, the ghost finishes its replay and
    /// disappears at the end of the recording; it is not held."
    ///
    /// These cells pin the ONE periapsis line (<see cref="OrbitClearance"/>) across every
    /// producer of an Orbiting verdict and the spawn-safety check, in both directions: a
    /// periapsis just above the floor is Orbiting everywhere and spawns; one just below is
    /// SubOrbital everywhere and is refused at spawn. The bands are the pre-ruling 5 km band
    /// above each stock atmosphere, where commit said Orbiting and spawn refused.
    /// </summary>
    [Collection("Sequential")]
    public class OrbitClearanceRulingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OrbitClearanceRulingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = null;
            ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        // Stock KSP 1.12 body radius / atmosphere depth.
        public static IEnumerable<object[]> AtmosphericBodies => new[]
        {
            new object[] { "Kerbin", 600000.0, 70000.0 },
            new object[] { "Duna", 320000.0, 50000.0 },
            new object[] { "Eve", 700000.0, 90000.0 },
            new object[] { "Laythe", 500000.0, 50000.0 },
            new object[] { "Jool", 6000000.0, 200000.0 },
        };

        private const double MunRadius = 200000.0;
        // The Mun's highest terrain is about 7.1 km; the exact PQS value is read live.
        private const double MunMaxTerrain = 7061.0;

        // ---- the floor itself ----

        [Fact]
        public void PeriapsisFloor_IsAtmosphereTopOnAtmosphericBodies()
        {
            Assert.Equal(70000.0, OrbitClearance.ComputePeriapsisFloorAltitude(true, 70000.0, 6767.0));
        }

        [Fact]
        public void PeriapsisFloor_IsMaxTerrainOnAirlessBodies()
        {
            Assert.Equal(MunMaxTerrain, OrbitClearance.ComputePeriapsisFloorAltitude(false, 0.0, MunMaxTerrain));
            // An airless body ignores whatever atmosphereDepth it carries.
            Assert.Equal(MunMaxTerrain, OrbitClearance.ComputePeriapsisFloorAltitude(false, 70000.0, MunMaxTerrain));
        }

        [Fact]
        public void PeriapsisFloor_FallsBackToSeaLevel()
        {
            Assert.Equal(0.0, OrbitClearance.ComputePeriapsisFloorAltitude(false, 0.0, 0.0));
            Assert.Equal(0.0, OrbitClearance.ComputePeriapsisFloorAltitude(false, 0.0, double.NaN));
            Assert.Equal(0.0, OrbitClearance.ComputePeriapsisFloorAltitude(true, double.NaN, -5.0));
        }

        [Fact]
        public void PeriapsisFloor_TakesTheHigherOfAtmosphereAndTerrain()
        {
            Assert.Equal(9000.0, OrbitClearance.ComputePeriapsisFloorAltitude(true, 5000.0, 9000.0));
        }

        // ---- the pre-ruling band: commit Orbiting, spawn now also Orbiting ----

        [Theory]
        [MemberData(nameof(AtmosphericBodies))]
        public void PeriapsisInTheOldMarginBand_CommitsOrbiting_AndSpawns(
            string body, double radius, double atmosphereDepth)
        {
            // 2 km above the atmosphere top: inside the old atmosphere + 5 km refusal band.
            double pe = atmosphereDepth + 2000.0;
            double ap = atmosphereDepth + 30000.0;
            double sma = radius + (pe + ap) / 2.0;
            double ecc = (ap - pe) / (2.0 * radius + pe + ap);

            Assert.True(RecordingTree.IsBoundOrbitAboveAtmosphere(ecc, radius + pe, radius, true, atmosphereDepth),
                body + " commit must call it Orbiting");
            Assert.Equal(TerminalState.Orbiting,
                RecordingTree.DetermineTerminalStateFromOrbitEvidence(32, ecc, radius + pe, radius, true, atmosphereDepth));

            // Spawn at apoapsis (above the margin): spawns now.
            var high = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(true, atmosphereDepth, ap, pe, ap);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, high.Action);

            // Spawn near periapsis (inside the margin band): a genuine deferral, never a refusal.
            var low = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(true, atmosphereDepth, pe + 500.0, pe, ap);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.DeferUntilSafe, low.Action);

            // The unloaded-vessel inference agrees.
            InstallInferenceBody(body, radius, atmosphereDepth);
            Assert.True(ParsekFlight.HasStableOrbitEvidenceForTerminalInference(
                new OrbitSegment { bodyName = body, semiMajorAxis = sma, eccentricity = ecc }));
        }

        [Theory]
        [MemberData(nameof(AtmosphericBodies))]
        public void PeriapsisInsideAtmosphere_IsSubOrbitalEverywhere_AndRefusedAtSpawn(
            string body, double radius, double atmosphereDepth)
        {
            double pe = atmosphereDepth - 1000.0;
            double ap = atmosphereDepth + 30000.0;
            double sma = radius + (pe + ap) / 2.0;
            double ecc = (ap - pe) / (2.0 * radius + pe + ap);

            Assert.False(RecordingTree.IsBoundOrbitAboveAtmosphere(ecc, radius + pe, radius, true, atmosphereDepth));
            Assert.Equal(TerminalState.SubOrbital,
                RecordingTree.DetermineTerminalStateFromOrbitEvidence(32, ecc, radius + pe, radius, true, atmosphereDepth));

            InstallInferenceBody(body, radius, atmosphereDepth);
            Assert.False(ParsekFlight.HasStableOrbitEvidenceForTerminalInference(
                new OrbitSegment { bodyName = body, semiMajorAxis = sma, eccentricity = ecc }),
                body + ": a periapsis above sea level but inside the atmosphere is not Orbiting evidence");
            Assert.Contains(logLines, l => l.Contains("HasStableOrbitEvidenceForTerminalInference")
                && l.Contains("under the periapsis floor"));

            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(true, atmosphereDepth, ap, pe, ap);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, decision.ReasonCode);
        }

        [Theory]
        [MemberData(nameof(AtmosphericBodies))]
        public void CommitInferenceAndSpawn_AgreeAcrossTheBoundary(
            string body, double radius, double atmosphereDepth)
        {
            // Mirror check: sweep periapsis through the atmosphere top in both directions and
            // require commit (loaded vessel), inference (unloaded vessel) and spawn to agree.
            InstallInferenceBody(body, radius, atmosphereDepth);
            double ap = atmosphereDepth + 50000.0;
            // Offsets straddle the line without landing on it: exactly at the floor the two
            // arithmetic forms (peR > R + floor vs peR - R > floor) may differ by one ulp.
            for (double offset = -9875.0; offset <= 10000.0; offset += 250.0)
            {
                double pe = atmosphereDepth + offset;
                double sma = radius + (pe + ap) / 2.0;
                double ecc = (ap - pe) / (2.0 * radius + pe + ap);
                double peR = sma * (1.0 - ecc);

                bool commitOrbiting = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                    32, ecc, peR, radius, true, atmosphereDepth) == TerminalState.Orbiting;
                bool inferredOrbiting = ParsekFlight.HasStableOrbitEvidenceForTerminalInference(
                    new OrbitSegment { bodyName = body, semiMajorAxis = sma, eccentricity = ecc });
                var spawn = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                    true, atmosphereDepth, ap, peR - radius, ap);
                bool spawnAccepts = spawn.Action != TerminalOrbitSpawnSafetyAction.CannotSpawnSafely;

                Assert.True(commitOrbiting == inferredOrbiting,
                    $"{body} pe offset {offset}: commit={commitOrbiting} inference={inferredOrbiting}");
                Assert.True(commitOrbiting == spawnAccepts,
                    $"{body} pe offset {offset}: commit={commitOrbiting} spawn={spawn.Action}/{spawn.ReasonCode}");
            }
        }

        [Fact]
        public void OrbitThatNeverReachesTheMargin_SpawnsInsteadOfBeingRefused()
        {
            // 72 x 74 km Kerbin: clear of the atmosphere all the way round but never above the
            // 75 km deferral margin. Pre-ruling this was orbit-never-clears-safe-altitude.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, 70000.0, 73000.0, 72000.0, 74000.0);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonMarginNeverReached, decision.ReasonCode);
        }

        // ---- airless terrain ----

        [Fact]
        public void MunPeriapsisUnderTheHighestTerrain_IsSubOrbital_AndRefusedAtSpawn()
        {
            double pe = 2000.0;
            double ap = 30000.0;
            double sma = MunRadius + (pe + ap) / 2.0;
            double ecc = (ap - pe) / (2.0 * MunRadius + pe + ap);

            Assert.False(RecordingTree.IsBoundOrbitAboveAtmosphere(
                ecc, MunRadius + pe, MunRadius, false, 0.0, MunMaxTerrain));
            Assert.Equal(TerminalState.SubOrbital,
                RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                    32, ecc, MunRadius + pe, MunRadius, false, 0.0, MunMaxTerrain));

            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = n => n == "Mun" ? MunRadius : (double?)null;
            ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = n => n == "Mun" ? MunMaxTerrain : (double?)null;
            Assert.False(ParsekFlight.HasStableOrbitEvidenceForTerminalInference(
                new OrbitSegment { bodyName = "Mun", semiMajorAxis = sma, eccentricity = ecc }));

            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, ap, pe, ap, MunMaxTerrain);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, decision.ReasonCode);
            Assert.Equal(MunMaxTerrain, decision.PeriapsisFloorAltitude);
        }

        [Fact]
        public void MunPeriapsisAboveTheHighestTerrain_IsOrbiting_AndSpawnsWithoutDeferral()
        {
            double pe = MunMaxTerrain + 1000.0;
            double ap = 30000.0;
            double ecc = (ap - pe) / (2.0 * MunRadius + pe + ap);

            Assert.Equal(TerminalState.Orbiting,
                RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                    32, ecc, MunRadius + pe, MunRadius, false, 0.0, MunMaxTerrain));

            // An airless body has no deferral margin: even at periapsis it spawns now.
            var decision = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                false, 0.0, pe + 10.0, pe, ap, MunMaxTerrain);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonAboveSafeAltitude, decision.ReasonCode);
        }

        // ---- the scene-exit finalizer ----

        [Fact]
        public void FinalizerOrbitingUnderTheMunTerrain_DowngradesToSubOrbital()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = new ExtrapolationBody { Name = "Mun", Radius = MunRadius, MaxTerrainAltitude = MunMaxTerrain },
            };
            var low = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Mun", semiMajorAxis = MunRadius + 16000.0, eccentricity = 14000.0 / (MunRadius + 16000.0) },
            };
            Assert.Equal(TerminalState.SubOrbital,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    TerminalState.Orbiting, low, bodies, "rec-mun-low"));
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("downgraded to SubOrbital") && l.Contains("rec-mun-low"));

            var clear = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Mun", semiMajorAxis = MunRadius + 20000.0, eccentricity = 0.01 },
            };
            Assert.Equal(TerminalState.Orbiting,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    TerminalState.Orbiting, clear, bodies, "rec-mun-clear"));

            // Non-Orbiting verdicts pass through untouched.
            Assert.Equal(TerminalState.Destroyed,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    TerminalState.Destroyed, low, bodies, "rec-mun-crash"));
        }

        [Fact]
        public void FinalizerOrbitingInsideKerbinAtmosphere_DowngradesToSubOrbital()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody { Name = "Kerbin", Radius = 600000.0, AtmosphereDepth = 70000.0 },
            };
            double pe = 65000.0, ap = 100000.0;
            var seg = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = "Kerbin",
                    semiMajorAxis = 600000.0 + (pe + ap) / 2.0,
                    eccentricity = (ap - pe) / (1200000.0 + pe + ap),
                },
            };
            Assert.Equal(TerminalState.SubOrbital,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    TerminalState.Orbiting, seg, bodies, "rec-kerbin-graze"));
        }

        // ---- the held ghost ----

        [Theory]
        [InlineData(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNonFinitePeriapsis)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNonFinitePropagatedAltitude)]
        public void HeldGhost_PermanentRefusal_IsReleased(string reasonCode)
        {
            var rec = new Recording
            {
                RecordingId = "rec-refused",
                TerminalSpawnCannotSpawnSafely = true,
                TerminalSpawnSafetyReasonCode = reasonCode,
            };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = 0.5f, recordingId = "rec-refused" };

            // Inside the retry interval and the timeout: the refusal still releases at once.
            Assert.Equal(HeldGhostAction.ReleaseCannotSpawnSafely,
                ParsekPlaybackPolicy.DecideHeldGhostAction(
                    0, info, new List<Recording> { rec }, currentTime: 1f, timeoutSeconds: 5f,
                    retryIntervalSeconds: 1f, currentUT: 500.0));
        }

        [Fact]
        public void HeldGhost_GenuineDeferral_KeepsHoldingPastTheTimeout()
        {
            var rec = new Recording
            {
                RecordingId = "rec-deferred",
                TerminalSpawnSafetyDeferred = true,
                TerminalSpawnNextAttemptUT = 1000.0,
                TerminalSpawnSafetyReasonCode = TerminalOrbitSpawnSafety.ReasonCurrentAltitudeBelowSafeAltitude,
            };
            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = -100f, recordingId = "rec-deferred" };

            Assert.Equal(HeldGhostAction.Hold,
                ParsekPlaybackPolicy.DecideHeldGhostAction(
                    0, info, new List<Recording> { rec }, currentTime: 60f, timeoutSeconds: 5f,
                    retryIntervalSeconds: 1f, currentUT: 500.0));
        }

        [Fact]
        public void DeferralWithoutANextUT_IsReEvaluatedNotHeldForever()
        {
            var rec = new Recording
            {
                RecordingId = "rec-deferred-nan",
                TerminalSpawnSafetyDeferred = true,
                TerminalSpawnNextAttemptUT = double.NaN,
            };
            Assert.Equal(TerminalOrbitDeferredSpawnState.Ready,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(rec, 500.0, out string reason));
            Assert.Contains("nextUT=non-finite", reason);

            var info = new HeldGhostInfo { holdStartTime = 0f, lastRetryTime = -100f, recordingId = "rec-deferred-nan" };
            Assert.Equal(HeldGhostAction.RetrySpawn,
                ParsekPlaybackPolicy.DecideHeldGhostAction(
                    0, info, new List<Recording> { rec }, currentTime: 60f, timeoutSeconds: 5f,
                    retryIntervalSeconds: 1f, currentUT: 500.0));
        }

        [Theory]
        [InlineData(TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied, true)]
        [InlineData(null, true)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, false)]
        [InlineData("orbit-never-clears-safe-altitude", false)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNoFutureSafeUT, false)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonTerminalOrbitResolutionFailed, false)]
        [InlineData(TerminalOrbitSpawnSafety.ReasonNonFinitePeriapsis, false)]
        public void OnlyTheSpawnDeathAbandonIsDurableAcrossALoad(string reasonCode, bool durable)
        {
            Assert.Equal(durable, TerminalOrbitSpawnSafety.IsDurableRefusal(reasonCode));
        }

        [Fact]
        public void SavedOldRuleRefusal_IsDroppedOnLoad_SoTheOrbitIsReEvaluated()
        {
            // A save written by the pre-ruling build: a 72 x 100 km Kerbin terminal orbit refused
            // as orbit-never-clears-safe-altitude. Restored as-is it would skip Evaluate forever
            // (VesselSpawner's pre-spawn guard) and, under the release rule, just vanish.
            var saved = new Recording
            {
                RecordingId = "rec-old-band",
                VesselName = "Old Band Probe",
                TerminalSpawnCannotSpawnSafely = true,
                TerminalSpawnSafetyReasonCode = "orbit-never-clears-safe-altitude",
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, saved);

            var reloaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, reloaded);

            Assert.False(reloaded.TerminalSpawnCannotSpawnSafely);
            Assert.Equal(TerminalOrbitDeferredSpawnState.None,
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(reloaded, 500.0, out _));
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Saved terminal spawn refusal dropped on load for re-evaluation")
                && l.Contains("reason=orbit-never-clears-safe-altitude"));

            // The in-session OnLoad reconcile applies the same rule.
            var inSession = new Recording { RecordingId = "rec-old-band" };
            ParsekScenario.RestorePersistedTerminalAbandon(inSession, node);
            Assert.False(inSession.TerminalSpawnCannotSpawnSafely);

            // The spawn-death abandon stays durable on both paths.
            saved.TerminalSpawnSafetyReasonCode = TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied;
            var deathNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(deathNode, saved);
            var reloadedDeath = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(deathNode, reloadedDeath);
            Assert.True(reloadedDeath.TerminalSpawnCannotSpawnSafely);
            var inSessionDeath = new Recording { RecordingId = "rec-old-band" };
            ParsekScenario.RestorePersistedTerminalAbandon(inSessionDeath, deathNode);
            Assert.True(inSessionDeath.TerminalSpawnCannotSpawnSafely);
        }

        [Fact]
        public void MapPresence_IsNotRetainedForARefusedTerminalSpawn()
        {
            var rec = new Recording
            {
                RecordingId = "rec-map",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0,
            };
            Assert.True(ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn(rec, hasFutureSegment: false));

            rec.TerminalSpawnCannotSpawnSafely = true;
            Assert.False(ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn(rec, hasFutureSegment: false));
        }

        private static void InstallInferenceBody(string body, double radius, double atmosphereDepth)
        {
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = n => n == body ? radius : (double?)null;
            ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting =
                n => n == body ? OrbitClearance.ComputePeriapsisFloorAltitude(true, atmosphereDepth, 0.0) : (double?)null;
        }
    }
}
