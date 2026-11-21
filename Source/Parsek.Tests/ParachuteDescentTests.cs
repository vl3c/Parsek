using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings Q4 / Q4b / Q4c: a stage left under open parachutes descends at chute
    /// speed in the replay and ends Landed / Splashed where it comes down, even when stock
    /// deletes it out of range; the predicted touchdown keeps the separation open.
    /// </summary>
    [Collection("Sequential")]
    public class ParachuteDescentTests : IDisposable
    {
        private const uint BoosterPid = 4242u;
        private readonly List<string> logLines = new List<string>();

        public ParachuteDescentTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ParachuteDescentInput Input(
            ParachuteCanopyState canopy,
            double altitude,
            double verticalSpeed,
            double terrain = 70.0,
            bool ocean = true,
            double deployAgl = double.NaN,
            double rootHeight = double.NaN)
        {
            return new ParachuteDescentInput
            {
                BodyName = "Kerbin",
                StartUT = 1000.0,
                Latitude = -0.1,
                Longitude = -74.5,
                Altitude = altitude,
                VerticalSpeed = verticalSpeed,
                Canopy = canopy,
                FullDeployAltitudeAgl = deployAgl,
                TerrainAltitude = terrain,
                BodyHasOcean = ocean,
                InAtmosphere = true,
                RootHeightAboveLowestPoint = rootHeight,
                SurfaceRelativeRotation = Quaternion.identity,
                WorldUp = new Vector3(0f, 1f, 0f),
            };
        }

        // ------------------------------------------------------------------ planner

        // catches: a deployed canopy descending at its measured rate to the ground below
        [Fact]
        public void Plan_Deployed_DescendsAtTheMeasuredRateToTheTerrain()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, altitude: 770.0, verticalSpeed: -7.0),
                out ParachuteDescentPlan plan, out _));

            Assert.Equal(TerminalState.Landed, plan.Terminal);
            Assert.Equal(7.0, plan.Phase1Rate, 6);
            Assert.Equal("measured", plan.RateSource);
            Assert.Equal(2, plan.Points.Count);
            Assert.Equal(70.0, plan.TouchdownAltitude, 6);
            Assert.Equal(1000.0 + 700.0 / 7.0, plan.TouchdownUT, 6);
            // The body-fixed spot is kept: chutes kill horizontal speed.
            Assert.Equal(-0.1, plan.Points[1].latitude, 9);
            Assert.Equal(-74.5, plan.Points[1].longitude, 9);
            Assert.Equal(-7.0, (double)plan.Points[1].velocity.y, 3);
        }

        [Theory]
        [InlineData(-0.2)]
        [InlineData(3.0)]
        [InlineData(double.NaN)]
        public void Plan_Deployed_NotDescendingYet_TakesTheDefaultFullCanopyRate(double verticalSpeed)
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 1070.0, verticalSpeed),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(ParachuteDescentPlanner.DefaultFullCanopyDescentRate, plan.Phase1Rate, 6);
            Assert.Equal("default-not-descending", plan.RateSource);
            Assert.False(plan.LandingRateMeasured);
            Assert.True(plan.SurvivesTouchdown);
        }

        // Owner ruling 2026-11-21: a stage lands safely only at or below 12 m/s under fully
        // open chutes; faster it crashes at the ground like a fall.
        [Fact]
        public void Plan_Deployed_11MetresPerSecond_Lands()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 1170.0, -11.0),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(11.0, plan.LandingRate, 6);
            Assert.True(plan.LandingRateMeasured);
            Assert.True(plan.SurvivesTouchdown);
            Assert.Equal(TerminalState.Landed, plan.Terminal);
            Assert.Equal(1100.0, plan.TouchdownUT, 6);
        }

        // catches: a clamp hiding a fast descent (the old 15 m/s cap) or a crash read as a landing
        [Fact]
        public void Plan_Deployed_13MetresPerSecond_Crashes()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 1370.0, -13.0),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(13.0, plan.LandingRate, 6);
            Assert.False(plan.SurvivesTouchdown);
            Assert.Equal(TerminalState.Destroyed, plan.Terminal);
            Assert.Equal(1100.0, plan.TouchdownUT, 6);
            Assert.Equal(12.0, ParachuteDescentPlanner.MaxSafeLandingRate, 9);
            string described = ParachuteDescentPlanner.Describe(plan);
            Assert.Contains("landingRate=13.00", described);
            Assert.Contains("safeRate=12.0", described);
            Assert.Contains("outcome=crash", described);

            // A fast measured drop is not clamped either.
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 1070.0, -40.0), out ParachuteDescentPlan fast, out _));
            Assert.Equal(40.0, fast.LandingRate, 6);
            Assert.Equal(TerminalState.Destroyed, fast.Terminal);
        }

        [Fact]
        public void Plan_SemiDeployed_IsJudgedOnTheDefaultFullCanopyRate()
        {
            // A fast semi-deployed drop that will fully open: the full-canopy rate it lands at
            // is the 7 m/s default (not measured before the canopy opens), so it lands.
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.SemiDeployed, 3070.0, -90.0, deployAgl: 800.0),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(ParachuteDescentPlanner.DefaultFullCanopyDescentRate, plan.LandingRate, 6);
            Assert.False(plan.LandingRateMeasured);
            Assert.True(plan.SurvivesTouchdown);
            Assert.Equal(TerminalState.Landed, plan.Terminal);
        }

        // Owner ruling 2026-11-21: chutes staged but not open yet (armed, waiting for their
        // pressure / altitude) do not count; the stage falls like any other.
        [Fact]
        public void ArmedNotOpenChutes_DoNotCount_TheStageCrashes()
        {
            Assert.Equal(ParachuteCanopyState.None,
                ParachuteDescentLiveReader.Classify(ModuleParachute.deploymentStates.ACTIVE));
            Assert.Equal(ParachuteCanopyState.None,
                ParachuteDescentLiveReader.Classify(ModuleParachute.deploymentStates.STOWED));
            Assert.Equal(ParachuteCanopyState.None,
                ParachuteDescentLiveReader.Classify(ModuleParachute.deploymentStates.CUT));
            Assert.Equal(ParachuteCanopyState.SemiDeployed,
                ParachuteDescentLiveReader.Classify(ModuleParachute.deploymentStates.SEMIDEPLOYED));
            Assert.Equal(ParachuteCanopyState.Deployed,
                ParachuteDescentLiveReader.Classify(ModuleParachute.deploymentStates.DEPLOYED));
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyPersistentState("ACTIVE"));

            // No open canopy: no chute descent, the finalizer keeps its drag-free fall.
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                "rec-armed", BoosterPid, Input(ParachuteCanopyState.None, 1070.0, -60.0),
                out IncompleteBallisticFinalizationResult result));
            Assert.Null(result.terminalState);
            Assert.Null(result.predictedDescentPoints);
        }

        [Fact]
        public void FastDescentResult_IsAPredictedCrashAtTheGround()
        {
            ParachuteDescentInput input = Input(ParachuteCanopyState.Deployed, 1370.0, -13.0);
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                "rec-fast", BoosterPid, input, out IncompleteBallisticFinalizationResult result));
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(1100.0, result.terminalUT, 6);
            Assert.Equal(2, result.predictedDescentPoints.Count);
            Assert.False(result.terminalPosition.HasValue);
            Assert.Contains(logLines, l => l.Contains("Chute descent predicted: rec=rec-fast")
                && l.Contains("terminal=Destroyed") && l.Contains("outcome=crash"));
        }

        [Fact]
        public void Apply_FastDescent_AppendsTheFallToTheGroundAsAPredictedCrash()
        {
            Recording rec = MakeRecordedBooster();
            ParachuteDescentInput input = Input(ParachuteCanopyState.Deployed, 770.0, -13.0);
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                "rec-booster", BoosterPid, input, out IncompleteBallisticFinalizationResult result));
            var cache = new RecordingFinalizationCache
            {
                RecordingId = "rec-booster",
                VesselPersistentId = BoosterPid,
                Status = FinalizationCacheStatus.Fresh,
                RefreshReason = "periodic",
                TerminalState = result.terminalState,
                TerminalUT = result.terminalUT,
                PredictedDescentPoints = result.predictedDescentPoints,
            };

            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, cache,
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out RecordingFinalizationCacheApplyResult applied));

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted); // a live-vessel refresh, not a confirmed destruction
            Assert.Equal(1, applied.AppendedDescentPointCount);
            Assert.Equal(TrackSectionSource.Predicted, rec.TrackSections[1].source);
            Assert.Null(rec.TerminalPosition);
            Assert.Equal("FLYING", rec.VesselSnapshot.GetValue("sit"));
        }

        [Fact]
        public void StockDeletion_KeepsAFastDescentAsThePredictedImpact()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult r) =>
                {
                    ParachuteDescentInput fast = Input(ParachuteCanopyState.Deployed, 1300.0, -13.0);
                    fast.StartUT = refreshUT;
                    return IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                        recording?.RecordingId, BoosterPid, fast, out r);
                };
            var recording = new Recording { RecordingId = "rec-booster", VesselPersistentId = BoosterPid };

            Assert.True(RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording, PackedFlyingView(), 1000.0, FinalizationCacheOwner.BackgroundOnRails,
                "background_destroy", hasMeaningfulThrust: false, out RecordingFinalizationCache cache));

            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.Equal(1000.0 + 1230.0 / 13.0, cache.TerminalUT, 6);
            Assert.Equal(2, cache.PredictedDescentPoints.Count);
            Assert.Contains(logLines, l => l.Contains("Stock deletion refresh")
                && l.Contains("outcome=predicted-impact"));
        }

        // catches: a semi-deployed canopy kept at its drogue speed all the way down
        [Fact]
        public void Plan_SemiDeployed_SwitchesToFullCanopyAtTheDeployAltitude()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.SemiDeployed, altitude: 2570.0, verticalSpeed: -50.0,
                    deployAgl: 1000.0),
                out ParachuteDescentPlan plan, out _));

            Assert.Equal(3, plan.Points.Count);
            Assert.Equal(1070.0, plan.SwitchAltitude, 6);
            Assert.Equal(1000.0 + 1500.0 / 50.0, plan.SwitchUT, 6);
            Assert.Equal(ParachuteDescentPlanner.DefaultFullCanopyDescentRate, plan.Phase2Rate, 6);
            Assert.Equal(plan.SwitchUT + 1000.0 / ParachuteDescentPlanner.DefaultFullCanopyDescentRate,
                plan.TouchdownUT, 6);
        }

        [Fact]
        public void Plan_SemiDeployedBelowTheDeployAltitude_IsOnePhaseAtFullCanopy()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.SemiDeployed, altitude: 400.0, verticalSpeed: -60.0),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(2, plan.Points.Count);
            Assert.True(double.IsNaN(plan.SwitchUT));
            Assert.Equal(ParachuteDescentPlanner.DefaultFullCanopyDescentRate, plan.Phase1Rate, 6);
        }

        [Fact]
        public void Plan_OverTheSea_EndsSplashedAtSeaLevel()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 500.0, -6.0, terrain: -350.0, ocean: true),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(TerminalState.Splashed, plan.Terminal);
            Assert.Equal(0.0, plan.TouchdownAltitude, 6);
            Assert.Equal(0.0, plan.GroundAltitude, 6);
        }

        [Fact]
        public void Plan_BelowDatumWithoutAnOcean_LandsOnTheTerrain()
        {
            Assert.True(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 500.0, -6.0, terrain: -300.0, ocean: false,
                    rootHeight: 1.5),
                out ParachuteDescentPlan plan, out _));
            Assert.Equal(TerminalState.Landed, plan.Terminal);
            Assert.Equal(-298.5, plan.TouchdownAltitude, 6);
            Assert.Equal(-300.0, plan.GroundAltitude, 6);
        }

        [Fact]
        public void Plan_Declines_NoChute_OutOfAtmosphere_AtGround()
        {
            Assert.False(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.None, 500.0, -6.0), out _, out string none));
            Assert.Equal("no-open-chute", none);

            ParachuteDescentInput vacuum = Input(ParachuteCanopyState.Deployed, 500.0, -6.0);
            vacuum.InAtmosphere = false;
            Assert.False(ParachuteDescentPlanner.TryPlan(vacuum, out _, out string air));
            Assert.Equal("not-in-atmosphere", air);

            Assert.False(ParachuteDescentPlanner.TryPlan(
                Input(ParachuteCanopyState.Deployed, 60.0, -6.0, terrain: 70.0), out _, out string ground));
            Assert.Equal("at-or-below-ground", ground);
        }

        [Fact]
        public void PersistentState_ClassifiesTheProtoModuleValue()
        {
            Assert.Equal(ParachuteCanopyState.Deployed,
                ParachuteDescentLiveReader.ClassifyPersistentState("DEPLOYED"));
            Assert.Equal(ParachuteCanopyState.SemiDeployed,
                ParachuteDescentLiveReader.ClassifyPersistentState("SEMIDEPLOYED"));
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyPersistentState("ACTIVE"));
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyPersistentState("CUT"));
            Assert.Equal(ParachuteCanopyState.None, ParachuteDescentLiveReader.ClassifyPersistentState(null));
        }

        // ------------------------------------------------------------------ result + cache

        private static IncompleteBallisticFinalizationResult DescentResult(
            double startUT = 1000.0, double altitude = 770.0)
        {
            ParachuteDescentInput input = Input(ParachuteCanopyState.Deployed, altitude, -7.0);
            input.StartUT = startUT;
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                "rec-booster", BoosterPid, input, out IncompleteBallisticFinalizationResult result));
            return result;
        }

        [Fact]
        public void FinalizationResult_IsALandedTouchdownWithTheDescent()
        {
            IncompleteBallisticFinalizationResult result = DescentResult();
            Assert.Equal(TerminalState.Landed, result.terminalState);
            Assert.Equal(1100.0, result.terminalUT, 6);
            Assert.Equal(2, result.predictedDescentPoints.Count);
            Assert.True(result.terminalPosition.HasValue);
            Assert.Equal(SurfaceSituation.Landed, result.terminalPosition.Value.situation);
            Assert.Equal(70.0, result.terminalPosition.Value.altitude, 6);
            Assert.Equal(70.0, result.terrainHeightAtEnd.Value, 6);
            Assert.Null(result.appendedOrbitSegments);
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("Chute descent predicted: rec=rec-booster")
                && l.Contains("terminal=Landed"));
        }

        private void OverrideFinalizerWithDescent()
        {
            RecordingFinalizationCacheProducer.TryBuildDefaultFinalizationResultOverrideForTesting =
                (Recording recording, Vessel vessel, double refreshUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    ParachuteDescentInput input = Input(ParachuteCanopyState.Deployed, 770.0, -7.0);
                    input.StartUT = refreshUT;
                    return IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                        recording?.RecordingId, BoosterPid, input, out result);
                };
        }

        private static FakeFlyingView PackedFlyingView()
        {
            return new FakeFlyingView();
        }

        // Owner ruling 2026-11-21: a chuted craft stock deletes out of range ends Destroyed with
        // its crew dead, as stock decided (no Landed override, no spawn); its ghost comes down
        // under its chutes to the ground and ends there quietly, with no explosion.
        // catches: a stock-deleted chuted craft kept Landed (spawned, crew alive)
        [Fact]
        public void StockDeletion_ChuteTouchdownEndsDestroyedWithTheDescentTail()
        {
            OverrideFinalizerWithDescent();
            var recording = new Recording { RecordingId = "rec-booster", VesselPersistentId = BoosterPid };

            Assert.True(RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                recording, PackedFlyingView(), 1000.0, FinalizationCacheOwner.BackgroundOnRails,
                "background_destroy", hasMeaningfulThrust: false, out RecordingFinalizationCache cache));

            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.True(cache.QuietEnd);
            Assert.Equal(1100.0, cache.TerminalUT, 6);
            Assert.Equal(2, cache.PredictedDescentPoints.Count);
            Assert.Null(cache.TerminalPosition);
            Assert.Contains(logLines, l => l.Contains("Stock deletion refresh")
                && l.Contains("outcome=chute-touchdown-quiet-end") && l.Contains("extrapolated=Landed")
                && l.Contains("descentPoints=2") && l.Contains("quietEnd=True"));

            // Applied as the destruction it is: not a prediction, so its crew read Dead.
            Recording rec = MakeRecordedBooster();
            cache.RecordingId = rec.RecordingId;
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, cache,
                new RecordingFinalizationCacheApplyOptions
                {
                    ConsumerPath = "DeferredDestructionCheck",
                    AllowStale = true,
                    DestructionConfirmed = true,
                },
                out RecordingFinalizationCacheApplyResult applied));
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.Equal(1, applied.AppendedDescentPointCount);
            Assert.Null(rec.TerminalPosition);
            Assert.Equal("FLYING", rec.VesselSnapshot.GetValue("sit"));
            Assert.False(GhostPlaybackLogic.IsSpawnableTerminal(rec.TerminalStateValue.Value));
            // Quiet end: the ghost rides the descent and ends with no explosion.
            Assert.True(rec.QuietDestroyedEnd);
            Assert.True(GhostPlaybackLogic.IsQuietDestroyedEnd(rec));
            Assert.Equal(TrackSectionSource.Predicted, rec.TrackSections[1].source);
            // Crew dead, as stock decided (a confirmed, not predicted, destruction).
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                "Jebediah Kerman", rec.TerminalStateValue, new HashSet<string> { "Jebediah Kerman" },
                rec.TerminalPredicted, partDestroyed: false));

            // Re-Fly stays offered (a crash is an Unfinished Flight, CommittedProvisional).
            Assert.Equal(TerminalKind.Crashed, TerminalKindClassifier.Classify(rec));

            // Saved and loaded, the marker stays.
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.Equal("True", node.GetValue("quietEnd"));
        }

        [Fact]
        public void QuietEnd_IsOnlyADestroyedEnding_AndAnyRestampClearsIt()
        {
            var rec = new Recording { RecordingId = "rec-quiet" };
            rec.StampTerminalState(TerminalState.Destroyed, "test", quietEnd: true);
            Assert.True(rec.QuietDestroyedEnd);
            rec.StampTerminalState(TerminalState.Landed, "test", quietEnd: true);
            Assert.False(rec.QuietDestroyedEnd);
            rec.StampTerminalState(TerminalState.Destroyed, "test", quietEnd: true);
            rec.StampTerminalState(TerminalState.Destroyed, "test");
            Assert.False(rec.QuietDestroyedEnd);
            Assert.False(GhostPlaybackLogic.IsQuietDestroyedEnd(new MockTrajectory
            {
                TerminalStateValue = TerminalState.Landed,
                QuietDestroyedEnd = true,
            }));
            Assert.True(GhostPlaybackLogic.IsQuietDestroyedEnd(new MockTrajectory
            {
                TerminalStateValue = TerminalState.Destroyed,
                QuietDestroyedEnd = true,
            }));
        }

        [Fact]
        public void StockDeletion_PrefersAPreviousFastDescentOverDestroyedNow()
        {
            var previous = new RecordingFinalizationCache
            {
                RecordingId = "rec-booster",
                VesselPersistentId = BoosterPid,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 1100.0,
                PredictedDescentPoints = DescentResult().predictedDescentPoints,
            };
            var destroyedNow = new RecordingFinalizationCache
            {
                RecordingId = "rec-booster",
                VesselPersistentId = BoosterPid,
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 1020.0,
            };

            RecordingFinalizationCache chosen = RecordingFinalizationCacheProducer.ResolveStockOnRailsDeletionCache(
                previous, destroyedNow, 1020.0, out string source);

            Assert.Same(previous, chosen);
            Assert.Equal("previous-fall", source);
        }

        // catches: a periodic refresh that keeps "touching" a descent cache to now (the
        // touch is for observed stable endings only)
        [Fact]
        public void ObservedTouch_LeavesADescentCacheAlone()
        {
            var cache = new RecordingFinalizationCache
            {
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Landed,
                TerminalUT = 1100.0,
                PredictedDescentPoints = DescentResult().predictedDescentPoints,
            };
            Assert.False(RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                cache, 1010.0, "periodic", "digest"));
            Assert.Equal(1100.0, cache.TerminalUT, 6);
        }

        // ------------------------------------------------------------------ applier

        // A booster recorded in one Absolute section up to UT 1000 at 770 m.
        private static Recording MakeRecordedBooster()
        {
            var rec = new Recording
            {
                RecordingId = "rec-booster",
                VesselName = "Booster",
                VesselPersistentId = BoosterPid,
            };
            var frames = new List<TrajectoryPoint>();
            for (int i = 0; i <= 4; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = 960.0 + i * 10.0,
                    latitude = -0.1,
                    longitude = -74.5,
                    altitude = 1050.0 - i * 70.0,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                    funds = 12345.0,
                    science = 6f,
                    reputation = 7f,
                    recordedGroundClearance = double.NaN,
                });
            }
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 960.0,
                endUT = 1000.0,
                frames = new List<TrajectoryPoint>(frames),
                source = TrackSectionSource.Background,
                sampleRateHz = 0.1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            });
            rec.Points.AddRange(frames);
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            snapshot.AddValue("landed", "False");
            snapshot.AddValue("splashed", "False");
            snapshot.AddValue("lat", "-0.1");
            snapshot.AddValue("lon", "-74.5");
            snapshot.AddValue("alt", "1850");
            rec.VesselSnapshot = snapshot;
            return rec;
        }

        private static RecordingFinalizationCache DescentCache(double refreshUT = 995.0)
        {
            IncompleteBallisticFinalizationResult result = DescentResult(startUT: refreshUT, altitude: 805.0);
            return new RecordingFinalizationCache
            {
                RecordingId = "rec-booster",
                VesselPersistentId = BoosterPid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                RefreshReason = "background_destroy",
                TerminalState = result.terminalState,
                TerminalUT = result.terminalUT,
                TerminalBodyName = "Kerbin",
                TerminalPosition = result.terminalPosition,
                TerrainHeightAtEnd = result.terrainHeightAtEnd,
                PredictedDescentPoints = result.predictedDescentPoints,
                TailStartsAtUT = refreshUT,
            };
        }

        // catches: the descent not reaching the recording (no section, terminal SubOrbital,
        // spawn mid-air) and the predicted points read as authored data
        [Fact]
        public void Apply_AppendsThePredictedDescentSectionAndLandsTheSnapshot()
        {
            Recording rec = MakeRecordedBooster();
            RecordingFinalizationCache cache = DescentCache();

            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, cache,
                new RecordingFinalizationCacheApplyOptions
                {
                    ConsumerPath = "test",
                    AllowStale = true,
                    DestructionConfirmed = true,
                },
                out RecordingFinalizationCacheApplyResult result));

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted);
            Assert.Equal(cache.TerminalUT, rec.ExplicitEndUT, 6);
            Assert.Equal(2, rec.TrackSections.Count);
            TrackSection predicted = rec.TrackSections[1];
            Assert.Equal(TrackSectionSource.Predicted, predicted.source);
            Assert.Equal(ReferenceFrame.Absolute, predicted.referenceFrame);
            Assert.Equal(1000.0, predicted.startUT, 6);
            Assert.Equal(cache.TerminalUT, predicted.endUT, 6);
            // Anchored on the real last sample (deduped), then the touchdown.
            Assert.Equal(2, predicted.frames.Count);
            Assert.Equal(1000.0, predicted.frames[0].ut, 6);
            Assert.Equal(770.0, predicted.frames[0].altitude, 6);
            Assert.Equal(6, rec.Points.Count);
            TrajectoryPoint touchdown = rec.Points[rec.Points.Count - 1];
            Assert.Equal(70.0, touchdown.altitude, 6);
            Assert.Equal(12345.0, touchdown.funds, 6);
            Assert.Equal(1, result.AppendedDescentPointCount);

            // The descent is not authored data: the last real sample stays the anchor.
            Assert.True(RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(rec, out double lastAuthored));
            Assert.Equal(1000.0, lastAuthored, 6);
            // The section payload still stands in for the flat list (section-authoritative).
            Assert.True(TrajectoryTextSidecarCodec.HasTrackSectionPayloadMatchingFlatTrajectory(
                rec, allowRelativeSections: true));

            Assert.Equal("LANDED", rec.VesselSnapshot.GetValue("sit"));
            Assert.Equal("True", rec.VesselSnapshot.GetValue("landed"));
            Assert.Equal(70.0, double.Parse(rec.VesselSnapshot.GetValue("alt"),
                System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(70.0, rec.TerrainHeightAtEnd, 6);
            Assert.Contains(logLines, l => l.Contains("[FinalizerCache]")
                && l.Contains("Predicted descent appended: context=RecordingFinalizationCacheApplier rec=")
                && l.Contains("anchor=last-sample"));
            Assert.Contains(logLines, l => l.Contains("Apply accepted") && l.Contains("terminal=Landed"));
        }

        // catches: a repair apply leaving the old descent next to the new ending
        [Fact]
        public void RepairApply_ReplacesTheDescent()
        {
            Recording rec = MakeRecordedBooster();
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, DescentCache(),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "first", AllowStale = true },
                out _));

            var crash = new RecordingFinalizationCache
            {
                RecordingId = "rec-booster",
                VesselPersistentId = BoosterPid,
                Status = FinalizationCacheStatus.Fresh,
                RefreshReason = "destroy_event",
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 1001.0,
            };
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, crash,
                new RecordingFinalizationCacheApplyOptions
                {
                    ConsumerPath = "repair",
                    AllowStale = true,
                    AllowAlreadyFinalizedRepair = true,
                },
                out RecordingFinalizationCacheApplyResult repair));

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.Single(rec.TrackSections);
            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(1, repair.RemovedDescentPointCount);
        }

        // catches: a Relative last sample (metres) used as the descent's first body-fixed frame
        [Fact]
        public void Append_RelativeLastSample_AnchorsOnItsBodyFixedShadow()
        {
            var rec = new Recording { RecordingId = "rec-child", VesselPersistentId = BoosterPid };
            var metres = new TrajectoryPoint { ut = 1000.0, latitude = 12.0, longitude = -3.0, altitude = 4.0, bodyName = "Kerbin" };
            var shadow = new TrajectoryPoint { ut = 1000.0, latitude = -0.1, longitude = -74.5, altitude = 770.0, bodyName = "Kerbin" };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 990.0,
                endUT = 1000.0,
                frames = new List<TrajectoryPoint> { metres },
                bodyFixedFrames = new List<TrajectoryPoint> { shadow },
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            });
            rec.Points.Add(metres);

            List<TrajectoryPoint> descent = DescentResult().predictedDescentPoints;
            int appended = RecordingFinalizationCacheApplier.AppendPredictedDescent(
                rec, descent, 1000.0, 1100.0, "test");

            Assert.Equal(2, appended);
            TrackSection predicted = rec.TrackSections[1];
            Assert.Equal(770.0, predicted.frames[0].altitude, 6);
            Assert.Equal(-74.5, predicted.frames[0].longitude, 9);
            Assert.Equal(3, rec.Points.Count);
            Assert.Contains(logLines, l => l.Contains("anchor=last-sample-shadow"));

            // Removal restores the recorded list exactly.
            Assert.Equal(2, RecordingFinalizationCacheApplier.RemovePredictedDescent(rec, "test"));
            Assert.Single(rec.Points);
            Assert.Single(rec.TrackSections);
        }

        // ------------------------------------------------------------------ takeover

        // catches: flying the stage again mid-descent (before the predicted touchdown) keeping
        // the predicted descent and touchdown
        [Fact]
        public void Takeover_DropsThePredictedDescent()
        {
            Recording rec = MakeRecordedBooster();
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, DescentCache(),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out _));
            Assert.True(PredictedEndingTakeover.HasPredictedEnding(rec));
            Assert.True(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(
                rec, 1050.0, false, r => true));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(
                rec, 990.0, false, r => true));

            Assert.True(rec.EndUT > 1050.0);
            PredictedEndingDropResult drop = PredictedEndingTakeover.DropPredictedEndingForTakeover(
                rec, 1050.0, "test");

            Assert.True(drop.Dropped);
            Assert.Equal(1, drop.RemovedDescentPointCount);
            Assert.Null(rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.Null(rec.TerminalPosition);
            Assert.Single(rec.TrackSections);
            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(1000.0, rec.EndUT, 6);
        }

        // ------------------------------------------------------------------ persistence

        [Fact]
        public void PredictedTouchdown_StampsAndPersistsThePredictedFlag()
        {
            var rec = new Recording { RecordingId = "rec-flag" };
            rec.StampTerminalState(TerminalState.Splashed, "test", predicted: true);
            Assert.True(rec.TerminalPredicted);
            rec.StampTerminalState(TerminalState.Orbiting, "test", predicted: true);
            Assert.False(rec.TerminalPredicted);
            Assert.True(Recording.IsPredictableTerminal(TerminalState.Landed));
            Assert.False(Recording.IsPredictableTerminal(TerminalState.SubOrbital));
        }

        [Fact]
        public void PredictedSectionSource_RoundTripsThroughBothSidecars()
        {
            Recording rec = MakeRecordedBooster();
            rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, DescentCache(),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out _));

            var node = new ConfigNode("TRAJ");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, rec);
            var fromText = new Recording { RecordingId = rec.RecordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, fromText);
            Assert.Equal(TrackSectionSource.Predicted, fromText.TrackSections[1].source);
            Assert.Equal(rec.Points.Count, fromText.Points.Count);

            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "parsek-chute-descent-" + Guid.NewGuid().ToString("N") + ".prec");
            try
            {
                TrajectorySidecarBinary.Write(tempPath, rec, sidecarEpoch: 1);
                Assert.True(TrajectorySidecarBinary.TryProbe(tempPath, out TrajectorySidecarProbe probe));
                var fromBinary = new Recording();
                TrajectorySidecarBinary.Read(tempPath, fromBinary, probe);
                Assert.Equal(TrackSectionSource.Predicted, fromBinary.TrackSections[1].source);
                Assert.Equal(rec.Points.Count, fromBinary.Points.Count);
                Assert.True(RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(fromBinary, out double last));
                Assert.Equal(1000.0, last, 6);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }

        private sealed class FakeFlyingView : IRecordingFinalizationVesselView
        {
            public uint PersistentId => BoosterPid;
            public Vessel.Situations Situation => Vessel.Situations.FLYING;
            public string BodyName => "Kerbin";
            public bool IsInAtmosphere => true;
            public bool IsBelowDeletionAltitude => true;
            public bool IsPacked => true;
            public bool IsLoaded => true;
            public double Latitude => -0.1;
            public double Longitude => -74.5;
            public double Altitude => 770.0;
            public double HeightFromTerrain => 700.0;
            public bool IsEva => false;
            public Quaternion SurfaceRelativeRotation => Quaternion.identity;
            public Vessel RawVessel => null;

            public bool TryGetOrbit(out RecordingFinalizationOrbitView orbit)
            {
                orbit = new RecordingFinalizationOrbitView
                {
                    ReferenceBodyName = "Kerbin",
                    ReferenceBodyHasAtmosphere = true,
                    ReferenceBodyAtmosphereDepth = 70000.0,
                    ReferenceBodyPeriapsisFloorAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude,
                    Eccentricity = 0.99,
                    SemiMajorAxis = 301000.0,
                    PeriapsisAltitude = -598000.0,
                };
                return true;
            }
        }
    }
}
