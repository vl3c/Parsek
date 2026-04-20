using System;
using System.Collections.Generic;
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
        public void TryApply_HookDecline_LogsWhenUsingNonTestHook()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeHook =
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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

        [Fact]
        public void TryApply_RejectsUnsetTerminalState()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
        public void FinalizeIndividualRecording_SceneExitHook_DoesNotTreatStaleTerminalOrbitAsHookAuthored()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
                (recording, vessel, commitUT, out IncompleteBallisticFinalizationResult result) =>
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
    }
}
