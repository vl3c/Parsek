using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SceneExitFinalizationIntegrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SceneExitFinalizationIntegrationTests()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void FinalizeIndividualRecording_SceneExitHook_AppendsTailAndExtendsEndUT()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 420.0,
                        patchedSegmentCount = 1,
                        extrapolatedSegmentCount = 1,
                        vesselSnapshot = MakeSnapshot("ORBITING"),
                        ghostVisualSnapshot = MakeSnapshot("ORBITING"),
                        appendedOrbitSegments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 110.0,
                                endUT = 200.0,
                                semiMajorAxis = 710000.0,
                                eccentricity = 0.12,
                                inclination = 1.0
                            },
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 200.0,
                                endUT = 420.0,
                                semiMajorAxis = 910000.0,
                                eccentricity = 0.23,
                                inclination = 2.0
                            }
                        }
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-tail",
                VesselName = "Tail Test",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitEndUT = double.NaN
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 110.0,
                semiMajorAxis = 650000.0,
                eccentricity = 0.05,
                inclination = 0.5
            });

            bool extended = ParsekFlight.FinalizeIndividualRecording(rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(extended);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal(420.0, rec.ExplicitEndUT);
            Assert.Equal(420.0, rec.EndUT);
            Assert.Equal(3, rec.OrbitSegments.Count);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.Equal("ORBITING", rec.VesselSnapshot.GetValue("sit"));
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(910000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.23, rec.TerminalOrbitEccentricity);
            Assert.True(rec.FilesDirty);
            Assert.DoesNotContain(logLines, l => l.Contains("inferred") && l.Contains("scene-exit-tail"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][Extrapolator]") &&
                l.Contains("scene-exit finalization applied to 'scene-exit-tail'") &&
                l.Contains("patched=1") &&
                l.Contains("extrapolated=1"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_SceneExitHook_ExtendsActiveNonLeafLifetime()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    recording.TerminalOrbitBody = "Mun";
                    recording.TerminalOrbitSemiMajorAxis = 255000.0;
                    recording.TerminalOrbitEccentricity = 0.04;
                    recording.TerminalOrbitInclination = 6.0;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 350.0,
                        patchedSegmentCount = 1,
                        extrapolatedSegmentCount = 0,
                        vesselSnapshot = MakeSnapshot("ORBITING"),
                        appendedOrbitSegments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 150.0,
                                endUT = 350.0,
                                semiMajorAxis = 700000.0,
                                eccentricity = 0.01,
                                inclination = 5.0
                            }
                        }
                    };
                    return true;
                };

            var tree = new RecordingTree { TreeName = "Scene Exit Tree" };
            var active = new Recording
            {
                RecordingId = "active-tail",
                VesselPersistentId = 0,
                ChildBranchPointId = "bp-1",
                ExplicitEndUT = 120.0
            };
            active.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 150.0,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1
            });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            bool extended = ParsekFlight.EnsureActiveRecordingTerminalState(
                tree,
                isSceneExit: true,
                commitUT: 200.0);

            Assert.True(extended);
            Assert.Equal(TerminalState.Orbiting, active.TerminalStateValue);
            Assert.Equal(350.0, active.ExplicitEndUT);
            Assert.Equal(350.0, active.EndUT);
            Assert.Equal("Mun", active.TerminalOrbitBody);
            Assert.Equal(255000.0, active.TerminalOrbitSemiMajorAxis);
            Assert.True(active.FilesDirty);
            Assert.NotNull(active.VesselSnapshot);
            Assert.Equal("ORBITING", active.VesselSnapshot.GetValue("sit"));
            Assert.Contains(logLines, l =>
                l.Contains("preserving scene-exit terminal orbit") &&
                l.Contains("active-tail"));
        }

        [Fact]
        public void FinalizeIndividualRecording_SceneExitHook_SurfaceEndpointPopulatesSurfaceMetadata()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Landed,
                        terminalUT = 260.0,
                        vesselSnapshot = MakeSnapshot("LANDED"),
                        terminalPosition = new SurfacePosition
                        {
                            body = "Kerbin",
                            latitude = 1.25,
                            longitude = -74.5,
                            altitude = 12.0,
                            rotation = UnityEngine.Quaternion.identity,
                            situation = SurfaceSituation.Landed
                        },
                        terrainHeightAtEnd = 8.5
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-landed",
                VesselName = "Tail Test",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                ExplicitEndUT = double.NaN,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 999.0,
                    rotation = UnityEngine.Quaternion.identity,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 999.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(skipLiveResnapshot);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.Equal(260.0, rec.ExplicitEndUT);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(12.0, rec.TerminalPosition.Value.altitude);
            Assert.Equal(8.5, rec.TerrainHeightAtEnd);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.Equal("LANDED", rec.VesselSnapshot.GetValue("sit"));
        }

        [Fact]
        public void FinalizeIndividualRecording_GhostOnlySnapshot_DoesNotSuppressLiveResnapshot()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 280.0,
                        ghostVisualSnapshot = MakeSnapshot("ORBITING")
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-ghost-only",
                VesselPersistentId = 0,
                ChildBranchPointId = null
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.False(skipLiveResnapshot);
            Assert.Null(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("applied ghost-only scene-exit finalization") &&
                l.Contains("scene-exit-ghost-only"));
        }

        [Fact]
        public void FinalizeIndividualRecording_CacheFallback_AppliesWhenVesselMissing()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-cache-missing",
                VesselName = "Cached Tail",
                VesselPersistentId = 42,
                ChildBranchPointId = null,
                ExplicitEndUT = double.NaN
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });

            RecordingFinalizationCache cache = MakeDestroyedCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                terminalUT: 180.0);

            bool extended = ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 120.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.False(extended);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(180.0, rec.ExplicitEndUT);
            Assert.Single(rec.OrbitSegments);
            Assert.True(rec.OrbitSegments[0].isPredicted);
            Assert.Equal(100.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(180.0, rec.OrbitSegments[0].endUT);
            Assert.True(rec.FilesDirty);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("consumer=FinalizeIndividualRecording") &&
                l.Contains("Cached Tail"));
            Assert.Contains(logLines, l =>
                l.Contains("Finalization source=cache") &&
                l.Contains("consumer=FinalizeIndividualRecording") &&
                l.Contains("Cached Tail"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("inferred") && l.Contains("scene-exit-cache-missing"));
        }

        [Fact]
        public void FinalizeIndividualRecording_StaleCacheFallback_AppliesAndWarnsWhenVesselMissing()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-stale-cache",
                VesselName = "Stale Cached Tail",
                VesselPersistentId = 43,
                ChildBranchPointId = null,
                ExplicitEndUT = double.NaN
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });

            RecordingFinalizationCache cache = MakeDestroyedCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                terminalUT: 180.0);
            cache.Status = FinalizationCacheStatus.Stale;
            cache.CachedAtUT = 105.0;

            bool extended = ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 120.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.False(extended);
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(180.0, rec.ExplicitEndUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Flight]") &&
                l.Contains("Finalization source=cache applied stale cache") &&
                l.Contains("consumer=FinalizeIndividualRecording") &&
                l.Contains("Stale Cached Tail"));
        }

        [Fact]
        public void FinalizeIndividualRecording_CacheRejected_FallsBackToTrajectoryInference()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-cache-rejected",
                VesselName = "Rejected Cache",
                VesselPersistentId = 44,
                ChildBranchPointId = null,
                ExplicitEndUT = double.NaN
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });

            RecordingFinalizationCache cache = MakeDestroyedCache(
                "other-recording",
                rec.VesselPersistentId,
                terminalUT: 180.0);

            bool extended = ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 120.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.False(extended);
            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
            Assert.Equal(100.0, rec.ExplicitEndUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][FinalizerCache]") &&
                l.Contains("Apply rejected") &&
                l.Contains("reason=RejectedMismatchedRecording") &&
                l.Contains("consumer=FinalizeIndividualRecording"));
            Assert.Contains(logLines, l =>
                l.Contains("inferred SubOrbital from trajectory") &&
                l.Contains("scene-exit-cache-rejected"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Finalization source=cache") && l.Contains("Rejected Cache"));
        }

        [Fact]
        public void FinalizeIndividualRecording_LiveSceneExitFinalizerWinsOverCache()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 300.0,
                        patchedSegmentCount = 1,
                        extrapolatedSegmentCount = 0,
                        vesselSnapshot = MakeSnapshot("ORBITING"),
                        terminalOrbit = new RecordingFinalizationTerminalOrbit
                        {
                            bodyName = "Kerbin",
                            semiMajorAxis = 900000.0,
                            eccentricity = 0.02,
                            inclination = 1.5,
                            epoch = 300.0
                        },
                        appendedOrbitSegments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 100.0,
                                endUT = 300.0,
                                semiMajorAxis = 900000.0,
                                eccentricity = 0.02,
                                inclination = 1.5,
                                isPredicted = true
                            }
                        }
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-live-wins",
                VesselName = "Live Winner",
                VesselPersistentId = 0,
                ChildBranchPointId = null
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });
            RecordingFinalizationCache cache = MakeDestroyedCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                terminalUT: 180.0);

            bool extended = ParsekFlight.FinalizeIndividualRecording(
                rec,
                commitUT: 120.0,
                isSceneExit: true,
                finalizationCache: cache);

            Assert.True(extended);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal(300.0, rec.ExplicitEndUT);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(300.0, rec.OrbitSegments[0].endUT);
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(900000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][INFO][FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("scene-exit-live-wins"));
        }

        [Fact]
        public void EnsureActiveRecordingTerminalState_CacheFallback_AppliesWhenVesselMissing()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var tree = new RecordingTree { TreeName = "Cache Fallback Tree" };
            var active = new Recording
            {
                RecordingId = "active-cache-missing",
                VesselName = "Active Cached",
                VesselPersistentId = 84,
                ChildBranchPointId = "bp-1",
                ExplicitEndUT = 100.0
            };
            active.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 10000.0, bodyName = "Kerbin" });
            tree.Recordings[active.RecordingId] = active;
            tree.ActiveRecordingId = active.RecordingId;

            RecordingFinalizationCache cache = MakeDestroyedCache(
                active.RecordingId,
                active.VesselPersistentId,
                terminalUT: 165.0);

            bool extended = ParsekFlight.EnsureActiveRecordingTerminalState(
                tree,
                isSceneExit: true,
                commitUT: 120.0,
                finalizationCache: cache);

            Assert.False(extended);
            Assert.Equal(TerminalState.Destroyed, active.TerminalStateValue);
            Assert.Equal(165.0, active.ExplicitEndUT);
            Assert.Single(active.OrbitSegments);
            Assert.True(active.OrbitSegments[0].isPredicted);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("consumer=EnsureActiveRecordingTerminalState") &&
                l.Contains("Active Cached"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("inferred") && l.Contains("active-cache-missing"));
        }

        [Fact]
        public void HasFallbackCandidateCache_RequiresCacheAndMissingVessel()
        {
            var stable = new RecordingFinalizationCache
            {
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 120.0
            };
            var synthetic = new RecordingFinalizationCache
            {
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Destroyed,
                TerminalUT = 120.0
            };
            var predictedStable = new RecordingFinalizationCache
            {
                Status = FinalizationCacheStatus.Fresh,
                TerminalState = TerminalState.Orbiting,
                TerminalUT = 180.0,
                PredictedSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Kerbin",
                        startUT = 100.0,
                        endUT = 180.0,
                        semiMajorAxis = 800000.0
                    }
                }
            };

            Assert.False(ParsekFlight.HasFallbackCandidateCache(null, vesselMissing: true));
            Assert.False(ParsekFlight.HasFallbackCandidateCache(stable, vesselMissing: false));
            Assert.True(ParsekFlight.HasFallbackCandidateCache(stable, vesselMissing: true));
            Assert.False(ParsekFlight.HasFallbackCandidateCache(synthetic, vesselMissing: false));
            Assert.False(ParsekFlight.HasFallbackCandidateCache(predictedStable, vesselMissing: false));
            Assert.True(ParsekFlight.HasFallbackCandidateCache(synthetic, vesselMissing: true));
            Assert.True(ParsekFlight.HasFallbackCandidateCache(predictedStable, vesselMissing: true));
        }

        [Fact]
        public void FinalizeTreeRecordingsAfterFlush_AppliesBackgroundCacheBeforeInference()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var tree = new RecordingTree
            {
                Id = "tree-cache-consumer",
                TreeName = "Cache Consumer Tree",
                ActiveRecordingId = "active-cache-consumer"
            };
            var active = new Recording
            {
                RecordingId = tree.ActiveRecordingId,
                TreeId = tree.Id,
                VesselName = "Active Missing",
                VesselPersistentId = 200,
                ChildBranchPointId = "bp-active",
                ExplicitEndUT = 100.0
            };
            active.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                altitude = 5000.0,
                bodyName = "Kerbin"
            });
            tree.Recordings[active.RecordingId] = active;

            var background = new Recording
            {
                RecordingId = "bg-cache-consumer",
                TreeId = tree.Id,
                VesselName = "Background Cached",
                VesselPersistentId = 100,
                ChildBranchPointId = null,
                ExplicitEndUT = 100.0
            };
            background.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                altitude = 5000.0,
                bodyName = "Kerbin"
            });
            tree.Recordings[background.RecordingId] = background;
            tree.BackgroundMap[background.VesselPersistentId] = background.RecordingId;

            RecordingFinalizationCache cache = MakeDestroyedCache(
                background.RecordingId,
                background.VesselPersistentId,
                terminalUT: 180.0);
            int resolverCalls = 0;

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                tree,
                commitUT: 120.0,
                isSceneExit: true,
                resolveFinalizationCache: recording =>
                {
                    resolverCalls++;
                    return recording.RecordingId == background.RecordingId
                        ? cache
                        : null;
                });

            Assert.Equal(2, resolverCalls);
            Assert.Equal(TerminalState.Destroyed, background.TerminalStateValue);
            Assert.Equal(180.0, background.ExplicitEndUT);
            Assert.Single(background.OrbitSegments);
            Assert.True(background.OrbitSegments[0].isPredicted);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][FinalizerCache]") &&
                l.Contains("Apply accepted") &&
                l.Contains("consumer=FinalizeIndividualRecording") &&
                l.Contains("Background Cached"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("inferred") && l.Contains("bg-cache-consumer"));
        }

        [Fact]
        public void TryShortCircuitSolverPredictedImpact_AirlessBodyImpact_ReturnsDestroyedTerminalUt()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    Radius = 200000.0,
                    AtmosphereDepth = 0.0
                }
            };
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = "Mun",
                    startUT = 150.0,
                    endUT = 320.0,
                    semiMajorAxis = 180000.0,
                    eccentricity = 0.0
                }
            };

            bool shortCircuited = IncompleteBallisticSceneExitFinalizer.TryShortCircuitSolverPredictedImpact(
                "scene-exit-mun-impact",
                segments,
                bodies,
                out double terminalUT);

            Assert.True(shortCircuited);
            Assert.Equal(320.0, terminalUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][PatchedSnapshot]") &&
                l.Contains("scene-exit-mun-impact") &&
                l.Contains("solver-predicted impact short-circuit"));
        }

        [Fact]
        public void TryShortCircuitSolverPredictedImpact_AtmosphericBody_ReturnsFalse()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    Radius = 600000.0,
                    AtmosphereDepth = 70000.0
                }
            };
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = "Kerbin",
                    startUT = 100.0,
                    endUT = 240.0,
                    semiMajorAxis = 650000.0,
                    eccentricity = 0.2
                }
            };

            bool shortCircuited = IncompleteBallisticSceneExitFinalizer.TryShortCircuitSolverPredictedImpact(
                "scene-exit-kerbin-entry",
                segments,
                bodies,
                out double terminalUT);

            Assert.False(shortCircuited);
            Assert.True(double.IsNaN(terminalUT));
            Assert.DoesNotContain(logLines, l => l.Contains("scene-exit-kerbin-entry"));
        }

        [Fact]
        public void TryCompleteFinalizationFromPatchedSnapshot_ManeuverNode_DiscardTailAndUsesLiveState()
        {
            var rec = new Recording
            {
                RecordingId = "scene-exit-maneuver-discard",
                ExplicitEndUT = 100.0
            };
            var snapshot = new PatchedConicSnapshotResult
            {
                EncounteredManeuverNode = true,
                CapturedPatchCount = 1,
                Segments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        startUT = 100.0,
                        endUT = 150.0,
                        semiMajorAxis = 250000.0,
                        eccentricity = 0.01,
                        inclination = 2.0
                    }
                }
            };
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    GravitationalParameter = 6.5138398e10,
                    Radius = 200000.0
                },
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = 3.5316e12,
                    Radius = 600000.0,
                    AtmosphereDepth = 70000.0
                }
            };

            bool sampledLiveState = false;
            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                snapshot,
                bodies,
                delegate(out BallisticStateVector startState)
                {
                    sampledLiveState = true;
                    startState = new BallisticStateVector
                    {
                        ut = 120.0,
                        bodyName = "Kerbin",
                        position = new Vector3d(700000.0, 0.0, 0.0),
                        velocity = new Vector3d(0.0, 2200.0, 0.0),
                        orbitalFrameRotation = Quaternion.identity
                    };
                    return true;
                },
                (startState, extrapolationBodies) =>
                {
                    Assert.Equal("Kerbin", startState.bodyName);
                    return new ExtrapolationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 240.0,
                        terminalBodyName = "Kerbin",
                        segments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 120.0,
                                endUT = 240.0,
                                semiMajorAxis = 650000.0,
                                eccentricity = 0.2,
                                inclination = 1.0
                            }
                        }
                    };
                },
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.True(sampledLiveState);
            Assert.Equal(0, result.patchedSegmentCount);
            Assert.Equal(1, result.extrapolatedSegmentCount);
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(240.0, result.terminalUT);
            Assert.Single(result.appendedOrbitSegments);
            Assert.Equal("Kerbin", result.appendedOrbitSegments[0].bodyName);
            Assert.DoesNotContain(result.appendedOrbitSegments, segment => segment.bodyName == "Mun");
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][PatchedSnapshot]") &&
                l.Contains("scene-exit-maneuver-discard") &&
                l.Contains("maneuver-node boundary detected") &&
                l.Contains("discarding stock patched-conic tail"));
        }

        [Fact]
        public void TryBuildStartStateFromSegment_CopiesOrbitalFrameRotation()
        {
            var frozenRotation = new UnityEngine.Quaternion(0.2f, -0.4f, 0.3f, 0.8f);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = 3.5316e12,
                    Radius = 600000.0
                }
            };
            var segment = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
                semiMajorAxis = 700000.0,
                eccentricity = 0.01,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 100.0,
                orbitalFrameRotation = frozenRotation
            };

            bool built = IncompleteBallisticSceneExitFinalizer.TryBuildStartStateFromSegment(
                segment,
                bodies,
                out BallisticStateVector startState);

            Assert.True(built);
            Assert.Equal(frozenRotation.x, startState.orbitalFrameRotation.x);
            Assert.Equal(frozenRotation.y, startState.orbitalFrameRotation.y);
            Assert.Equal(frozenRotation.z, startState.orbitalFrameRotation.z);
            Assert.Equal(frozenRotation.w, startState.orbitalFrameRotation.w);
        }

        [Fact]
        public void SeedPredictedSegmentOrbitalFrameRotations_PreservesBoundaryWorldRotationAcrossSegments()
        {
            var frozenWorldRotation = new UnityEngine.Quaternion(0.2f, -0.4f, 0.3f, 0.8f);
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = 3.5316e12,
                    Radius = 600000.0
                },
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    GravitationalParameter = 6.5138398e10,
                    Radius = 200000.0
                }
            };
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    bodyName = "Kerbin",
                    startUT = 100.0,
                    endUT = 200.0,
                    semiMajorAxis = 700000.0,
                    eccentricity = 0.01,
                    inclination = 0.0,
                    longitudeOfAscendingNode = 0.0,
                    argumentOfPeriapsis = 0.0,
                    meanAnomalyAtEpoch = 0.0,
                    epoch = 100.0
                },
                new OrbitSegment
                {
                    bodyName = "Mun",
                    startUT = 200.0,
                    endUT = 260.0,
                    semiMajorAxis = 260000.0,
                    eccentricity = 0.02,
                    inclination = 1.0,
                    longitudeOfAscendingNode = 20.0,
                    argumentOfPeriapsis = 30.0,
                    meanAnomalyAtEpoch = 0.1,
                    epoch = 200.0
                }
            };

            IncompleteBallisticSceneExitFinalizer.SeedPredictedSegmentOrbitalFrameRotations(
                "scene-exit-snapshot-ofr",
                segments,
                frozenWorldRotation,
                bodies);

            Assert.True(BallisticExtrapolator.HasOrbitalFrameRotation(segments[0].orbitalFrameRotation));
            Assert.True(BallisticExtrapolator.HasOrbitalFrameRotation(segments[1].orbitalFrameRotation));
            Assert.True(BallisticExtrapolator.TryPropagate(
                segments[0],
                bodies["Kerbin"].GravitationalParameter,
                segments[0].startUT,
                out Vector3d startPosition,
                out Vector3d startVelocity));
            Assert.True(BallisticExtrapolator.TryPropagate(
                segments[0],
                bodies["Kerbin"].GravitationalParameter,
                segments[0].endUT,
                out Vector3d firstBoundaryPosition,
                out Vector3d firstBoundaryVelocity));
            Assert.True(BallisticExtrapolator.TryPropagate(
                segments[1],
                bodies["Mun"].GravitationalParameter,
                segments[1].startUT,
                out Vector3d secondStartPosition,
                out Vector3d secondStartVelocity));

            var startWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                segments[0].orbitalFrameRotation,
                startPosition,
                startVelocity);
            var firstBoundaryWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                segments[0].orbitalFrameRotation,
                firstBoundaryPosition,
                firstBoundaryVelocity);
            var secondStartWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                segments[1].orbitalFrameRotation,
                secondStartPosition,
                secondStartVelocity);

            AssertQuaternionEquivalent(frozenWorldRotation, startWorldRotation);
            AssertQuaternionEquivalent(firstBoundaryWorldRotation, secondStartWorldRotation);
        }

        [Fact]
        public void TryApply_HookDecline_LogsWhenUsingNonTestHook()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeHook =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-decline"
            };

            bool applied = IncompleteBallisticSceneExitFinalizer.TryApply(
                rec,
                vessel: null,
                commitUT: 200.0,
                logContext: "SceneExitTests");

            Assert.False(applied);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]") &&
                l.Contains("incomplete-ballistic finalization hook declined") &&
                l.Contains("scene-exit-decline"));
        }

        private static void AssertQuaternionEquivalent(
            Quaternion expected,
            Quaternion actual,
            float tolerance = 0.0001f)
        {
            expected = NormalizeAndCanonicalizeQuaternion(expected);
            actual = NormalizeAndCanonicalizeQuaternion(actual);
            float dot = Mathf.Abs(
                (expected.x * actual.x)
                + (expected.y * actual.y)
                + (expected.z * actual.z)
                + (expected.w * actual.w));
            Assert.True(
                1f - dot < tolerance,
                $"dot={dot} expected={expected} actual={actual}");
        }

        private static Quaternion NormalizeAndCanonicalizeQuaternion(Quaternion quaternion)
        {
            float magnitude = Mathf.Sqrt(
                quaternion.x * quaternion.x
                + quaternion.y * quaternion.y
                + quaternion.z * quaternion.z
                + quaternion.w * quaternion.w);
            if (magnitude > 1e-6f)
            {
                quaternion = new Quaternion(
                    quaternion.x / magnitude,
                    quaternion.y / magnitude,
                    quaternion.z / magnitude,
                    quaternion.w / magnitude);
            }

            if (quaternion.w < 0f
                || (quaternion.w == 0f
                    && (quaternion.z < 0f
                        || (quaternion.z == 0f
                            && (quaternion.y < 0f
                                || (quaternion.y == 0f && quaternion.x < 0f))))))
            {
                quaternion = new Quaternion(
                    -quaternion.x,
                    -quaternion.y,
                    -quaternion.z,
                    -quaternion.w);
            }

            return quaternion;
        }

        [Fact]
        public void TryApply_DefaultPath_FlightGlobalsUnavailable_DeclinesAndLogs()
        {
            var rec = new Recording
            {
                RecordingId = "scene-exit-headless"
            };

            bool applied = IncompleteBallisticSceneExitFinalizer.TryApply(
                rec,
                vessel: null,
                commitUT: 200.0,
                logContext: "SceneExitTests");

            Assert.False(applied);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][Extrapolator]") &&
                l.Contains("scene-exit-headless") &&
                l.Contains("FlightGlobals runtime unavailable") &&
                l.Contains("skipping default scene-exit extrapolation"));
        }

        [Fact]
        public void IsFlightGlobalsRuntimeAvailable_TransientUnavailableProbe_IsNotCached()
        {
            int probeCount = 0;
            IncompleteBallisticSceneExitFinalizer.FlightGlobalsRuntimeAvailabilityOverrideForTesting = () =>
            {
                probeCount++;
                if (probeCount == 1)
                    return (false, false, "fetch=true, ready=false");
                return (true, true, "fetch=true, ready=true");
            };

            Assert.False(IncompleteBallisticSceneExitFinalizer.IsFlightGlobalsRuntimeAvailable("first"));
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsFlightGlobalsRuntimeAvailable("second"));
            Assert.Equal(2, probeCount);
        }

        [Fact]
        public void IsFlightGlobalsRuntimeAvailable_PermanentFailureProbe_IsCached()
        {
            int probeCount = 0;
            IncompleteBallisticSceneExitFinalizer.FlightGlobalsRuntimeAvailabilityOverrideForTesting = () =>
            {
                probeCount++;
                return (false, true, "TypeInitializationException");
            };

            Assert.False(IncompleteBallisticSceneExitFinalizer.IsFlightGlobalsRuntimeAvailable("first"));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsFlightGlobalsRuntimeAvailable("second"));
            Assert.Equal(1, probeCount);
        }

        [Fact]
        public void TryApply_RejectsUnsetTerminalState()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalUT = 260.0
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-unset-terminal",
                ExplicitEndUT = 200.0
            };

            bool applied = IncompleteBallisticSceneExitFinalizer.TryApply(
                rec,
                vessel: null,
                commitUT: 200.0,
                logContext: "SceneExitTests");

            Assert.False(applied);
            Assert.False(rec.TerminalStateValue.HasValue);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][ERROR][Extrapolator]") &&
                l.Contains("scene-exit-unset-terminal") &&
                l.Contains("terminalState was unset/default"));
        }

        [Fact]
        public void TryApply_RejectsInvalidTerminalState()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = (TerminalState)999,
                        terminalUT = 260.0
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-invalid-terminal",
                ExplicitEndUT = 200.0
            };

            bool applied = IncompleteBallisticSceneExitFinalizer.TryApply(
                rec,
                vessel: null,
                commitUT: 200.0,
                logContext: "SceneExitTests");

            Assert.False(applied);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][ERROR][Extrapolator]") &&
                l.Contains("scene-exit-invalid-terminal") &&
                l.Contains("terminalState=999 was invalid"));
        }

        [Fact]
        public void TryApply_RejectsTerminalUtThatMovesBackwardBeforeCurrentEnd()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 239.0
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-non-monotonic",
                ExplicitEndUT = 240.0
            };

            bool applied = IncompleteBallisticSceneExitFinalizer.TryApply(
                rec,
                vessel: null,
                commitUT: 200.0,
                logContext: "SceneExitTests");

            Assert.False(applied);
            Assert.Equal(240.0, rec.EndUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][ERROR][Extrapolator]") &&
                l.Contains("scene-exit-non-monotonic") &&
                l.Contains("terminalUT=239.0 moved backward before max(commitUT=200.0, currentEndUT=240.0)"));
        }

        [Fact]
        public void FinalizeIndividualRecording_SceneExitHook_PreservesHookPopulatedTerminalOrbitMetadata()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    recording.TerminalOrbitBody = "Mun";
                    recording.TerminalOrbitSemiMajorAxis = 250000.0;
                    recording.TerminalOrbitEccentricity = 0.02;
                    recording.TerminalOrbitInclination = 5.0;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 350.0,
                        vesselSnapshot = MakeSnapshot("ORBITING")
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-preserve-orbit",
                VesselPersistentId = 0,
                ChildBranchPointId = null
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 0.5
            });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(skipLiveResnapshot);
            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(250000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.02, rec.TerminalOrbitEccentricity);
            Assert.Equal(5.0, rec.TerminalOrbitInclination);
            Assert.Contains(logLines, l =>
                l.Contains("preserving scene-exit terminal orbit") &&
                l.Contains("scene-exit-preserve-orbit"));
        }

        [Fact]
        public void FinalizeIndividualRecording_SceneExitHook_SameUtPointAnchor_StaysOnSceneExitPreservePath()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    recording.TerminalOrbitBody = "Mun";
                    recording.TerminalOrbitSemiMajorAxis = 250000.0;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 350.0,
                        vesselSnapshot = MakeSnapshot("ORBITING")
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-preserve-same-ut-point-anchor",
                VesselPersistentId = 0,
                ChildBranchPointId = null
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Mun" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
                semiMajorAxis = 700000.0
            });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(skipLiveResnapshot);
            Assert.Equal("Mun", rec.TerminalOrbitBody);
            Assert.Equal(250000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Contains(logLines, l =>
                l.Contains("preserving scene-exit terminal orbit") &&
                l.Contains("scene-exit-preserve-same-ut-point-anchor"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("same-UT point-anchored terminal orbit") &&
                l.Contains("scene-exit-preserve-same-ut-point-anchor"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("PopulateTerminalOrbitFromLastSegment") &&
                l.Contains("scene-exit-preserve-same-ut-point-anchor"));
        }

        [Fact]
        public void FinalizeIndividualRecording_SceneExitHook_DoesNotTreatStaleTerminalOrbitAsHookAuthored()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Orbiting,
                        terminalUT = 350.0,
                        vesselSnapshot = MakeSnapshot("ORBITING"),
                        appendedOrbitSegments = new List<OrbitSegment>
                        {
                            new OrbitSegment
                            {
                                bodyName = "Kerbin",
                                startUT = 200.0,
                                endUT = 350.0,
                                semiMajorAxis = 725000.0,
                                eccentricity = 0.03,
                                inclination = 2.5
                            }
                        }
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-stale-orbit",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                TerminalOrbitBody = "Eve",
                TerminalOrbitSemiMajorAxis = 999999.0,
                TerminalOrbitEccentricity = 0.9,
                TerminalOrbitInclination = 12.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 0.5
            });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.True(skipLiveResnapshot);
            Assert.Equal("Kerbin", rec.TerminalOrbitBody);
            Assert.Equal(725000.0, rec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(0.03, rec.TerminalOrbitEccentricity);
            Assert.Equal(2.5, rec.TerminalOrbitInclination);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("preserving scene-exit terminal orbit") &&
                l.Contains("scene-exit-stale-orbit"));
            Assert.Contains(logLines, l =>
                l.Contains("backfilled TerminalOrbitBody=Kerbin") &&
                l.Contains("scene-exit-stale-orbit"));
        }

        [Fact]
        public void FinalizeIndividualRecording_GhostOnlySurfaceHook_PreservesExistingSurfaceMetadataAndLogs()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Landed,
                        terminalUT = 280.0,
                        ghostVisualSnapshot = MakeSnapshot("LANDED")
                    };
                    return true;
                };

            var rec = new Recording
            {
                RecordingId = "scene-exit-ghost-surface",
                VesselPersistentId = 0,
                ChildBranchPointId = null,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 1.25,
                    longitude = -74.5,
                    altitude = 12.0,
                    rotation = UnityEngine.Quaternion.identity,
                    situation = SurfaceSituation.Landed
                },
                TerrainHeightAtEnd = 8.5
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5.0, bodyName = "Kerbin" });

            bool skipLiveResnapshot = ParsekFlight.FinalizeIndividualRecording(
                rec, commitUT: 200.0, isSceneExit: true);

            Assert.False(skipLiveResnapshot);
            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(12.0, rec.TerminalPosition.Value.altitude);
            Assert.Equal(8.5, rec.TerrainHeightAtEnd);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Extrapolator]") &&
                l.Contains("scene-exit-ghost-surface") &&
                l.Contains("keeping existing surface metadata"));
        }

        [Fact]
        public void RefreshActiveEffectiveLeafSnapshot_ExtendedLifetime_SkipsLiveResnapshot()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Scene Exit Tree"
            };

            var active = new Recording
            {
                RecordingId = "active-tail",
                TreeId = tree.Id,
                VesselPersistentId = 100,
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Orbiting
            };
            var child = new Recording
            {
                RecordingId = "child-debris",
                TreeId = tree.Id,
                VesselPersistentId = 200
            };
            tree.Recordings[active.RecordingId] = active;
            tree.Recordings[child.RecordingId] = child;
            tree.ActiveRecordingId = active.RecordingId;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ChildRecordingIds = new List<string> { child.RecordingId }
            });

            ParsekFlight.RefreshActiveEffectiveLeafSnapshot(
                tree,
                isSceneExit: true,
                sceneExitLifetimeExtendedIds: new HashSet<string>(StringComparer.Ordinal)
                {
                    active.RecordingId
                });

            Assert.Contains(logLines, l =>
                l.Contains("active effective leaf 'active-tail'") &&
                l.Contains("uses scene-exit extended lifetime") &&
                l.Contains("skipping live re-snapshot"));
        }

        private static ConfigNode MakeSnapshot(string sit)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", sit);
            return node;
        }

        private static RecordingFinalizationCache MakeDestroyedCache(
            string recordingId,
            uint vesselPersistentId,
            double terminalUT)
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPersistentId,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = terminalUT - 5.0,
                RefreshReason = "test-cache",
                LastObservedUT = terminalUT - 5.0,
                LastObservedBodyName = "Kerbin",
                LastSituation = Vessel.Situations.FLYING,
                LastWasInAtmosphere = true,
                TailStartsAtUT = 100.0,
                TerminalUT = terminalUT,
                TerminalState = TerminalState.Destroyed,
                TerminalBodyName = "Kerbin"
            };
            cache.PredictedSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = terminalUT,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 2.0,
                isPredicted = true
            });
            return cache;
        }
    }
}
