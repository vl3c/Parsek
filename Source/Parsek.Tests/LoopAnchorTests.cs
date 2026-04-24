using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LoopAnchorTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LoopAnchorTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(null);
        }

        // --- Default value ---

        [Fact]
        public void LoopAnchorVesselId_Default_IsZero()
        {
            var rec = new Recording();
            Assert.Equal(0u, rec.LoopAnchorVesselId);
        }

        // --- ParsekScenario serialization round-trip ---

        [Fact]
        public void LoopAnchorVesselId_Scenario_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "anchor-test",
                LoopPlayback = true,
                LoopAnchorVesselId = 12345,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(12345u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Scenario_Zero_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "no-anchor",
                LoopAnchorVesselId = 0,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopAnchorPid"));
        }

        [Fact]
        public void LoopAnchorVesselId_Scenario_BackwardCompat_MissingKey_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING");
            // No loopAnchorPid key at all
            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(0u, loaded.LoopAnchorVesselId);
        }

        // --- LoopStartUT / LoopEndUT save/load round-trip ---

        [Fact]
        public void LoopRange_Scenario_SaveLoad_RoundTrip_ValidValues()
        {
            var source = new Recording
            {
                RecordingId = "loop-range-test",
                LoopStartUT = 130.5,
                LoopEndUT = 170.25,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(130.5, loaded.LoopStartUT);
            Assert.Equal(170.25, loaded.LoopEndUT);
        }

        [Fact]
        public void LoopRange_Scenario_Load_LegacyNegativeInterval_MigratesToLaunchPeriod()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-scenario-loop");
            node.AddValue("recordingFormatVersion", "3");
            node.AddValue("loopPlayback", true);
            node.AddValue("loopStartUT", 120.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopEndUT", 180.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopIntervalSeconds", (-20.0).ToString("R", CultureInfo.InvariantCulture));

            var loaded = new Recording { VesselName = "LegacyScenario" };
            loaded.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            loaded.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });

            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(40.0, loaded.LoopIntervalSeconds);
            Assert.Equal(120.0, loaded.LoopStartUT);
            Assert.Equal(180.0, loaded.LoopEndUT);
            Assert.Contains(logLines, line => line.Contains("migrated recording 'LegacyScenario'"));
        }

        [Fact]
        public void LoopRange_Scenario_Load_LegacyPositiveGap_MigratesToLaunchPeriod()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-scenario-positive-gap");
            node.AddValue("recordingFormatVersion", "3");
            node.AddValue("loopPlayback", true);
            node.AddValue("loopStartUT", 120.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopEndUT", 180.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopIntervalSeconds", 10.0.ToString("R", CultureInfo.InvariantCulture));

            var loaded = new Recording { VesselName = "LegacyScenarioPositive" };
            loaded.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            loaded.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });

            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(70.0, loaded.LoopIntervalSeconds);
            Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, loaded.RecordingFormatVersion);
            Assert.Contains(logLines, line => line.Contains("migrated recording 'LegacyScenarioPositive'"));
        }

        [Fact]
        public void LoopRange_Scenario_SaveLoad_NaN_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "loop-range-nan",
                LoopStartUT = double.NaN,
                LoopEndUT = double.NaN,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopStartUT"));
            Assert.Null(node.GetValue("loopEndUT"));
        }

        [Fact]
        public void LoopRange_Scenario_BackwardCompat_MissingKeys_DefaultsNaN()
        {
            var node = new ConfigNode("RECORDING");
            // No loopStartUT / loopEndUT keys
            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.True(double.IsNaN(loaded.LoopStartUT));
            Assert.True(double.IsNaN(loaded.LoopEndUT));
        }

        // --- RecordingTree serialization round-trip ---

        [Fact]
        public void LoopAnchorVesselId_Tree_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "tree-anchor-test",
                LoopPlayback = true,
                LoopAnchorVesselId = 67890,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(67890u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_Zero_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "tree-no-anchor",
                LoopAnchorVesselId = 0,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            Assert.Null(node.GetValue("loopAnchorPid"));
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_BackwardCompat_MissingKey_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "compat-test");
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(0u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopRange_Tree_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "tree-loop-range-test",
                LoopStartUT = 150.0,
                LoopEndUT = 200.0,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(150.0, loaded.LoopStartUT);
            Assert.Equal(200.0, loaded.LoopEndUT);
        }

        [Fact]
        public void LoopRange_Tree_Load_LegacyNegativeInterval_MigratesToLaunchPeriod()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-tree-loop");
            node.AddValue("recordingFormatVersion", "3");
            node.AddValue("vesselName", "LegacyTree");
            node.AddValue("explicitStartUT", 0.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("explicitEndUT", 300.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopStartUT", 100.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopEndUT", 200.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopIntervalSeconds", (-20.0).ToString("R", CultureInfo.InvariantCulture));

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(80.0, loaded.LoopIntervalSeconds);
            Assert.Equal(100.0, loaded.LoopStartUT);
            Assert.Equal(200.0, loaded.LoopEndUT);
            Assert.Contains(logLines, line => line.Contains("migrated recording 'LegacyTree'"));
        }

        [Fact]
        public void LoopRange_Tree_Load_LegacyNegativeInterval_DefersUntilSidecarHydration()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek-legacy-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string precPath = Path.Combine(tempDir, "legacy-tree-sidecar.prec");
                string vesselPath = Path.Combine(tempDir, "legacy-tree-sidecar_vessel.craft");
                string ghostPath = Path.Combine(tempDir, "legacy-tree-sidecar_ghost.craft");

                var source = new Recording
                {
                    RecordingId = "legacy-tree-sidecar",
                    VesselName = "LegacyTreeSidecar",
                    RecordingFormatVersion = 3,
                    ExplicitStartUT = 0.0,
                    ExplicitEndUT = 300.0
                };
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 0.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 300.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    source, precPath, vesselPath, ghostPath, incrementEpoch: false));

                var node = new ConfigNode("RECORDING");
                node.AddValue("recordingId", source.RecordingId);
                node.AddValue("recordingFormatVersion", "3");
                node.AddValue("vesselName", source.VesselName);
                node.AddValue("loopStartUT", 100.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopEndUT", 200.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopIntervalSeconds", (-20.0).ToString("R", CultureInfo.InvariantCulture));

                var loaded = new Recording();
                RecordingTree.LoadRecordingFrom(node, loaded);

                Assert.Equal(-20.0, loaded.LoopIntervalSeconds);
                Assert.Contains(logLines, line => line.Contains("deferred migration"));

                logLines.Clear();
                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath));
                Assert.Equal(80.0, loaded.LoopIntervalSeconds);
                Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, loaded.RecordingFormatVersion);
                Assert.Contains(logLines, line => line.Contains("RecordingStore: migrated recording 'LegacyTreeSidecar'"));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void LoopRange_Tree_Load_LegacyPositiveGap_DefersUntilSidecarHydration_AndDoesNotRemigrateAfterSave()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek-legacy-loop-positive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string precPath = Path.Combine(tempDir, "legacy-tree-positive-sidecar.prec");
                string vesselPath = Path.Combine(tempDir, "legacy-tree-positive-sidecar_vessel.craft");
                string ghostPath = Path.Combine(tempDir, "legacy-tree-positive-sidecar_ghost.craft");

                var source = new Recording
                {
                    RecordingId = "legacy-tree-positive-sidecar",
                    VesselName = "LegacyTreePositiveSidecar",
                    RecordingFormatVersion = 3,
                    ExplicitStartUT = 0.0,
                    ExplicitEndUT = 300.0
                };
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 0.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 300.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    source, precPath, vesselPath, ghostPath, incrementEpoch: false));

                var node = new ConfigNode("RECORDING");
                node.AddValue("recordingId", source.RecordingId);
                node.AddValue("recordingFormatVersion", "3");
                node.AddValue("vesselName", source.VesselName);
                node.AddValue("loopStartUT", 100.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopEndUT", 200.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopIntervalSeconds", 10.0.ToString("R", CultureInfo.InvariantCulture));

                var loaded = new Recording();
                RecordingTree.LoadRecordingFrom(node, loaded);

                Assert.Equal(10.0, loaded.LoopIntervalSeconds);
                Assert.Equal(3, loaded.RecordingFormatVersion);
                Assert.Contains(logLines, line => line.Contains("deferred migration"));

                logLines.Clear();
                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath));
                Assert.Equal(110.0, loaded.LoopIntervalSeconds);
                Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, loaded.RecordingFormatVersion);
                Assert.Contains(logLines, line => line.Contains("RecordingStore: migrated recording 'LegacyTreePositiveSidecar'"));

                var roundTripNode = new ConfigNode("RECORDING");
                RecordingTree.SaveRecordingInto(roundTripNode, loaded);
                Assert.Equal(
                    RecordingStore.LaunchToLaunchLoopIntervalFormatVersion.ToString(CultureInfo.InvariantCulture),
                    roundTripNode.GetValue("recordingFormatVersion"));

                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath, incrementEpoch: false));

                logLines.Clear();
                var reloaded = new Recording();
                RecordingTree.LoadRecordingFrom(roundTripNode, reloaded);
                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    reloaded, precPath, vesselPath, ghostPath));
                Assert.Equal(110.0, reloaded.LoopIntervalSeconds);
                Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, reloaded.RecordingFormatVersion);
                Assert.DoesNotContain(logLines, line => line.Contains("migrated recording 'LegacyTreePositiveSidecar'"));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void LoopRange_Tree_Load_LegacyWithExplicitBoundsAndSidecar_MigratesOnceNotTwice()
        {
            // #411 follow-up regression: a tree node that carries explicitStartUT/explicitEndUT
            // (real flight saves from BackgroundRecorder / split / merge / breakup / fallback
            // commits all do) lets EffectiveLoopDuration resolve at tree-load time — so the
            // migration fires immediately, stamping RecordingFormatVersion=4 on the in-memory
            // recording. The subsequent sidecar load must NOT demote that stamp back to v3,
            // or MigrateLegacyLoopIntervalAfterHydration fires a second time against the
            // already-migrated value and the period doubles.
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek-legacy-loop-explicit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string precPath = Path.Combine(tempDir, "legacy-tree-explicit.prec");
                string vesselPath = Path.Combine(tempDir, "legacy-tree-explicit_vessel.craft");
                string ghostPath = Path.Combine(tempDir, "legacy-tree-explicit_ghost.craft");

                var source = new Recording
                {
                    RecordingId = "legacy-tree-explicit",
                    VesselName = "LegacyTreeExplicit",
                    RecordingFormatVersion = 3,
                    ExplicitStartUT = 0.0,
                    ExplicitEndUT = 300.0
                };
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 0.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                source.Points.Add(new TrajectoryPoint
                {
                    ut = 300.0, latitude = 0, longitude = 0, altitude = 0,
                    bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
                });
                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    source, precPath, vesselPath, ghostPath, incrementEpoch: false));

                // Tree node carries BOTH explicit bounds and loop subrange, mimicking a real
                // flight-originated tree save. The loop subrange is [100, 200] inside a
                // [0, 300] recording → EffectiveLoopDuration = 100.
                var node = new ConfigNode("RECORDING");
                node.AddValue("recordingId", source.RecordingId);
                node.AddValue("recordingFormatVersion", "3");
                node.AddValue("vesselName", source.VesselName);
                node.AddValue("explicitStartUT", 0.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("explicitEndUT", 300.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopStartUT", 100.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopEndUT", 200.0.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("loopIntervalSeconds", 10.0.ToString("R", CultureInfo.InvariantCulture));

                var loaded = new Recording();
                RecordingTree.LoadRecordingFrom(node, loaded);

                // Explicit bounds make EffectiveLoopDuration available at tree-load time,
                // so the migration fires immediately and is NOT deferred.
                Assert.Equal(110.0, loaded.LoopIntervalSeconds);
                Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, loaded.RecordingFormatVersion);
                Assert.Contains(logLines, line => line.Contains("RecordingTree: migrated recording 'LegacyTreeExplicit'"));
                Assert.DoesNotContain(logLines, line => line.Contains("deferred migration"));

                // Sidecar hydration — this is the pre-fix failure mode. The sidecar on disk
                // is v3, and TrajectorySidecarBinary.Read used to unconditionally stamp
                // rec.RecordingFormatVersion = probe.FormatVersion, demoting the in-memory
                // v4 back to v3. MigrateLegacyLoopIntervalAfterHydration would then fire a
                // second time against the already-migrated 110 and produce 100 + 110 = 210.
                logLines.Clear();
                Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                    loaded, precPath, vesselPath, ghostPath));

                Assert.Equal(110.0, loaded.LoopIntervalSeconds);
                Assert.Equal(RecordingStore.LaunchToLaunchLoopIntervalFormatVersion, loaded.RecordingFormatVersion);
                Assert.DoesNotContain(logLines, line => line.Contains("RecordingStore: migrated recording 'LegacyTreeExplicit'"));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void LoopRange_Tree_Load_CurrentFormat_PreservesLaunchPeriod()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "current-tree-loop");
            node.AddValue("recordingFormatVersion",
                RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            node.AddValue("vesselName", "CurrentTree");
            node.AddValue("explicitStartUT", 0.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("explicitEndUT", 300.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopStartUT", 100.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopEndUT", 200.0.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("loopIntervalSeconds", 10.0.ToString("R", CultureInfo.InvariantCulture));

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(10.0, loaded.LoopIntervalSeconds);
            Assert.DoesNotContain(logLines, line => line.Contains("migrated recording 'CurrentTree'"));
            Assert.DoesNotContain(logLines, line => line.Contains("deferred migration"));
        }

        [Fact]
        public void LoopRange_Tree_NaN_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "tree-no-loop-range",
                LoopStartUT = double.NaN,
                LoopEndUT = double.NaN,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            Assert.Null(node.GetValue("loopStartUT"));
            Assert.Null(node.GetValue("loopEndUT"));
        }

        [Fact]
        public void LoopRange_Tree_BackwardCompat_MissingKey_DefaultsNaN()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "tree-compat-test");
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.True(double.IsNaN(loaded.LoopStartUT));
            Assert.True(double.IsNaN(loaded.LoopEndUT));
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesLoopAnchorVesselId()
        {
            var source = new Recording { LoopAnchorVesselId = 99999 };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(99999u, target.LoopAnchorVesselId);
        }

        // --- ValidateLoopAnchor ---

        [Fact]
        public void ValidateLoopAnchor_ZeroPid_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ValidateLoopAnchor(0);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchorPid=0"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselExists_ReturnsTrue()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 42);

            bool result = GhostPlaybackLogic.ValidateLoopAnchor(42);
            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchor pid=42 found"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselMissing_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);

            bool result = GhostPlaybackLogic.ValidateLoopAnchor(777);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchor pid=777 NOT found"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselMissing_LogsWarning()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);

            GhostPlaybackLogic.ValidateLoopAnchor(555);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("loop anchor broken"));
        }

        // --- ShouldUseLoopAnchor ---

        [Fact]
        public void ShouldUseLoopAnchor_NullRec_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(null));
        }

        [Fact]
        public void ShouldUseLoopAnchor_ZeroAnchor_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 0 };
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_NoTrackSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>();
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_OnlyAbsoluteSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100, endUT = 200,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_HasRelativeSection_ReturnsTrue()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 100, endUT = 200,
                    anchorVesselId = 100,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.True(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_MixedSections_WithRelative_ReturnsTrue()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100, endUT = 150,
                    frames = new List<TrajectoryPoint>()
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 150, endUT = 200,
                    anchorVesselId = 100,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.True(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_NullTrackSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = null;
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        // --- Serialization with large PID values ---

        [Fact]
        public void LoopAnchorVesselId_Scenario_LargePid_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "large-pid",
                LoopAnchorVesselId = 4294967295, // uint.MaxValue
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(4294967295u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_LargePid_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "tree-large-pid",
                LoopAnchorVesselId = 4294967295, // uint.MaxValue
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(4294967295u, loaded.LoopAnchorVesselId);
        }

        // --- Cross-path: both serializers produce same key name ---

        [Fact]
        public void LoopAnchorVesselId_SavedKeyName_ConsistentAcrossSerializers()
        {
            var rec = new Recording
            {
                RecordingId = "key-consistency",
                LoopAnchorVesselId = 42,
            };

            var scenarioNode = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(scenarioNode, rec);

            var treeNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(treeNode, rec);

            // Both should use the same key name
            string scenarioValue = scenarioNode.GetValue("loopAnchorPid");
            string treeValue = treeNode.GetValue("loopAnchorPid");

            Assert.NotNull(scenarioValue);
            Assert.NotNull(treeValue);
            Assert.Equal("42", scenarioValue);
            Assert.Equal("42", treeValue);
        }
    }
}
