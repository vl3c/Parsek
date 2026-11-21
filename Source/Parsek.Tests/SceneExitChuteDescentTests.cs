using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q14: a chuted craft still in the air when the player LEAVES the flight
    /// scene (a scene exit, or the far Switch-To that reloads FLIGHT) is decided at the exit as
    /// stock's "mission will be lost" case: Destroyed, not predicted, crew dead, a quiet end
    /// after the descent, no spawn, the slot open as a crash. A predicted touchdown survives
    /// only an in-flight commit after which the player keeps flying (the reconciler then
    /// follows stock). A craft already on the surface keeps its ending; a too-fast descent
    /// stays an exploding crash.
    /// </summary>
    [Collection("Sequential")]
    public class SceneExitChuteDescentTests : IDisposable
    {
        private const string Jeb = PredictedTouchdownStockFateTests.Jeb;
        private const string BoosterId = PredictedTouchdownStockFateTests.BoosterId;
        private const uint BoosterPid = PredictedTouchdownStockFateTests.BoosterPid;
        private readonly List<string> logLines = new List<string>();

        public SceneExitChuteDescentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
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
            KerbalsModule.EnsureTerminalStampObserverInstalled();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            PredictedTouchdownStockFate.ResetForTesting();
        }

        public void Dispose()
        {
            PredictedTouchdownStockFate.ResetForTesting();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
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
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ fixtures

        private static RecordingTree LiveTree(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = PredictedTouchdownStockFateTests.TreeId,
                TreeName = "Touchdown",
                RootRecordingId = PredictedTouchdownStockFateTests.CoreId,
                ActiveRecordingId = PredictedTouchdownStockFateTests.CoreId,
            };
            foreach (Recording rec in recs)
                tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static Vessel Stub(uint pid, Vessel.Situations situation)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            vessel.situation = situation;
            return vessel;
        }

        private static ParsekFlight.FinalizationLiveVesselAccess Access =>
            new ParsekFlight.FinalizationLiveVesselAccess(
                isFound: v => !ReferenceEquals(v, null),
                determineTerminalState: v => RecordingTree.DetermineTerminalState((int)v.situation),
                captureTerminalOrbit: (r, v) => { },
                captureTerminalPosition: (r, v) => { },
                tryBackupSnapshot: v => null,
                tryRefreshStableTerminalSnapshot: (r, v, s, p) => false,
                findLaterSpawnOwner: (r, v) => null,
                isRecordedIdentityLost: (r, v) => false);

        private static void Finalize(
            RecordingTree tree,
            bool isSceneExit,
            bool leavesFlightScene = false,
            bool abandonActiveVessel = false,
            Func<uint, Vessel> findVesselByPid = null)
        {
            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                tree,
                commitUT: 1000.0,
                isSceneExit: isSceneExit,
                findVesselByPid: findVesselByPid ?? (pid => null),
                liveVesselAccess: Access,
                abandonActiveVessel: abandonActiveVessel,
                leavesFlightScene: leavesFlightScene);
        }

        private void AssertMissionLost(Recording rec, double touchdownUT)
        {
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.True(rec.QuietDestroyedEnd);
            Assert.True(GhostPlaybackLogic.IsQuietDestroyedEnd(rec));
            Assert.True(PredictedTouchdownStockFate.HasPredictedDescentSection(rec));
            Assert.Equal(touchdownUT, rec.EndUT, 6);
            Assert.Null(rec.TerminalPosition);
            Assert.Equal(TerminalKind.Crashed, TerminalKindClassifier.Classify(rec));
            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.False(needsSpawn);
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("Chute descent at scene exit:")
                && l.Contains("rec=" + rec.RecordingId)
                && l.Contains("outcome=mission-lost")
                && l.Contains("predicted=False quietEnd=True"));
        }

        private static List<GameAction> CrewRows(string recordingId)
        {
            var rows = new List<GameAction>();
            foreach (GameAction a in Ledger.Actions)
                if (a.Type == GameActionType.KerbalAssignment && a.RecordingId == recordingId)
                    rows.Add(a);
            return rows;
        }

        // ------------------------------------------------------------------ the exit verdict

        // catches: a scene exit leaving the chuted booster a predicted Landed touchdown that
        // spawns (crew alive) unless a later flight scene happens to run the reconciler.
        [Fact]
        public void SceneExit_PredictedChuteTouchdown_EndsMissionLost_CrewDead()
        {
            Recording booster = PredictedTouchdownStockFateTests.PredictedTouchdownBooster();
            double touchdownUT = booster.EndUT;
            Recording core = PredictedTouchdownStockFateTests.Core();
            RecordingTree tree = LiveTree(core, booster);

            Finalize(tree, isSceneExit: true);

            AssertMissionLost(booster, touchdownUT);
            Assert.Contains(logLines, l => l.Contains("Chute descent at scene exit:") && l.Contains("exit=scene-exit"));
            Assert.Equal(TerminalState.Orbiting, core.TerminalStateValue);

            PredictedTouchdownStockFateTests.CommitTreeWith(core, booster);
            List<GameAction> rows = CrewRows(BoosterId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);
            Assert.Equal(KerbalEndState.Dead, booster.CrewEndStates[Jeb]);
        }

        // catches: the far Switch-To's pre-switch Merge (FLIGHT reloads) keeping the forecast
        [Fact]
        public void FarSwitchMerge_PredictedChuteTouchdown_EndsMissionLost()
        {
            Recording booster = PredictedTouchdownStockFateTests.PredictedTouchdownBooster();
            double touchdownUT = booster.EndUT;
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);

            Finalize(tree, isSceneExit: false, leavesFlightScene: true, abandonActiveVessel: true);

            AssertMissionLost(booster, touchdownUT);
            Assert.Contains(logLines, l => l.Contains("Chute descent at scene exit:") && l.Contains("exit=far-switch"));
        }

        // catches: the exit rule leaking into an in-flight commit after which the player keeps
        // flying (plain Commit Flight, or an in-bubble switch), where the reconciler follows stock
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InFlightCommit_PlayerStaysInFlight_KeepsPredictedTouchdown(bool abandonActiveVessel)
        {
            Recording booster = PredictedTouchdownStockFateTests.PredictedTouchdownBooster();
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);

            Finalize(tree, isSceneExit: false, abandonActiveVessel: abandonActiveVessel);

            Assert.Equal(TerminalState.Landed, booster.TerminalStateValue);
            Assert.True(booster.TerminalPredicted);
            Assert.False(booster.QuietDestroyedEnd);
            Assert.DoesNotContain(logLines, l => l.Contains("Chute descent at scene exit:"));
        }

        // catches: a craft that came down before the exit read as lost
        [Fact]
        public void SceneExit_LiveVesselAlreadyLanded_KeepsItsEnding()
        {
            Recording booster = PredictedTouchdownStockFateTests.PredictedTouchdownBooster();
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);
            Vessel landed = Stub(BoosterPid, Vessel.Situations.LANDED);

            PredictedTouchdownStockFate.ApplyFlightSceneLeave(
                tree, 1000.0, "scene-exit",
                rec => ParsekFlight.IsLiveVesselOnSurfaceForSceneLeave(
                    rec, pid => pid == BoosterPid ? landed : null, Access));

            Assert.Equal(TerminalState.Landed, booster.TerminalStateValue);
            Assert.True(booster.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("Chute descent at scene exit:")
                && l.Contains("outcome=kept-touched-down") && l.Contains("rec=" + BoosterId));
        }

        // catches: the rule reaching a landing the player flew, or re-stamping a crash
        [Fact]
        public void SceneExit_RealLandingAndTooFastCrash_Untouched()
        {
            Recording flown = PredictedTouchdownStockFateTests.PredictedTouchdownBooster();
            flown.StampTerminalState(TerminalState.Landed, "test-flown");
            Recording tooFast = PredictedTouchdownStockFateTests.PredictedTouchdownBooster("rec-too-fast");
            tooFast.StampTerminalState(TerminalState.Destroyed, "test-too-fast", predicted: true);
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), flown, tooFast);

            int converted = PredictedTouchdownStockFate.ApplyFlightSceneLeave(tree, 1000.0, "scene-exit", null);

            Assert.Equal(0, converted);
            Assert.Equal(TerminalState.Landed, flown.TerminalStateValue);
            Assert.False(flown.TerminalPredicted);
            Assert.Equal(TerminalState.Destroyed, tooFast.TerminalStateValue);
            Assert.True(tooFast.TerminalPredicted);
            Assert.False(tooFast.QuietDestroyedEnd);
            Assert.DoesNotContain(logLines, l => l.Contains("Chute descent at scene exit:"));
        }

        [Theory]
        [InlineData(Vessel.Situations.LANDED, true)]
        [InlineData(Vessel.Situations.SPLASHED, true)]
        [InlineData(Vessel.Situations.FLYING, false)]
        [InlineData(Vessel.Situations.SUB_ORBITAL, false)]
        public void IsLiveVesselOnSurfaceForSceneLeave_ReadsTheSituation(Vessel.Situations situation, bool expected)
        {
            var rec = new Recording { RecordingId = "r", VesselPersistentId = 77u };
            Vessel live = Stub(77u, situation);
            Assert.Equal(expected, ParsekFlight.IsLiveVesselOnSurfaceForSceneLeave(
                rec, pid => pid == 77u ? live : null, Access));
            Assert.False(ParsekFlight.IsLiveVesselOnSurfaceForSceneLeave(rec, pid => null, Access));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void PreSwitchMergeLeavesFlightScene_OnlyForAnUnloadedTarget(bool targetLoaded, bool expected)
        {
            Assert.Equal(expected, Parsek.Patches.MapFocusObjectOnSelectPatch.PreSwitchMergeLeavesFlightScene(targetLoaded));
        }

        // ------------------------------------------------------------------ through the live finalizer

        private static Recording AirborneBooster()
        {
            var rec = new Recording
            {
                RecordingId = BoosterId,
                TreeId = PredictedTouchdownStockFateTests.TreeId,
                VesselName = "Crewed Booster",
                VesselPersistentId = BoosterPid,
                RecordedVesselGuid = PredictedTouchdownStockFateTests.BoosterGuid,
                ExplicitStartUT = 960.0,
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
            rec.VesselSnapshot = PredictedTouchdownStockFateTests.CrewSnapshot(Jeb);
            rec.GhostVisualSnapshot = PredictedTouchdownStockFateTests.CrewSnapshot(Jeb);
            return rec;
        }

        private static void InstallChuteFinalizer(bool tooFast = false)
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT,
                    out IncompleteBallisticFinalizationResult result) =>
                {
                    var input = new ParachuteDescentInput
                    {
                        BodyName = "Kerbin",
                        StartUT = 1000.0,
                        Latitude = -0.1,
                        Longitude = -74.5,
                        Altitude = 770.0,
                        VerticalSpeed = tooFast ? -40.0 : -7.0,
                        Canopy = ParachuteCanopyState.Deployed,
                        FullDeployAltitudeAgl = double.NaN,
                        TerrainAltitude = 70.0,
                        BodyHasOcean = true,
                        InAtmosphere = true,
                        RootHeightAboveLowestPoint = double.NaN,
                        SurfaceRelativeRotation = Quaternion.identity,
                        WorldUp = new Vector3(0f, 1f, 0f),
                    };
                    return IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                        recording.RecordingId, recording.VesselPersistentId, input, out result);
                };
        }

        // catches: the live finalizer's fresh chute forecast for a craft still in the air at
        // the exit surviving as a predicted Landed ending
        [Fact]
        public void SceneExit_LiveChutedCraftInTheAir_FinalizerForecastEndsMissionLost()
        {
            InstallChuteFinalizer();
            Recording booster = AirborneBooster();
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);
            Vessel flying = Stub(BoosterPid, Vessel.Situations.FLYING);

            Finalize(tree, isSceneExit: true, findVesselByPid: pid => pid == BoosterPid ? flying : null);

            AssertMissionLost(booster, booster.EndUT);
            Assert.True(booster.EndUT > 1000.0);
        }

        // catches: the same forecast lost on a plain in-flight commit (the craft is not the
        // active vessel, the player keeps flying)
        [Fact]
        public void InFlightCommit_LiveChutedCraftInTheAir_KeepsTheForecast()
        {
            InstallChuteFinalizer();
            Recording booster = AirborneBooster();
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);
            Vessel flying = Stub(BoosterPid, Vessel.Situations.FLYING);

            Finalize(tree, isSceneExit: false, findVesselByPid: pid => pid == BoosterPid ? flying : null);

            Assert.True(PredictedTouchdownStockFate.IsPredictedTouchdown(booster));
            Assert.DoesNotContain(logLines, l => l.Contains("Chute descent at scene exit:"));
        }

        // catches: the exit turning a too-fast descent (Q11) into a quiet end
        [Fact]
        public void SceneExit_TooFastDescent_StaysAnExplodingCrash()
        {
            InstallChuteFinalizer(tooFast: true);
            Recording booster = AirborneBooster();
            RecordingTree tree = LiveTree(PredictedTouchdownStockFateTests.Core(), booster);
            Vessel flying = Stub(BoosterPid, Vessel.Situations.FLYING);

            Finalize(tree, isSceneExit: true, findVesselByPid: pid => pid == BoosterPid ? flying : null);

            Assert.Equal(TerminalState.Destroyed, booster.TerminalStateValue);
            Assert.False(booster.QuietDestroyedEnd);
            Assert.DoesNotContain(logLines, l => l.Contains("Chute descent at scene exit:"));
        }
    }
}
