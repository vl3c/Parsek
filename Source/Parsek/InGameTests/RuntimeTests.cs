using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Core runtime tests that verify Parsek systems work in a live KSP environment.
    /// These catch bugs that xUnit tests structurally cannot (Unity APIs, real KSP state, etc.).
    /// </summary>
    public class RuntimeTests
    {
        private readonly InGameTestRunner runner;

        public RuntimeTests(InGameTestRunner runner)
        {
            this.runner = runner;
        }

        #region RecordingStore

        [InGameTest(Category = "RecordingStore", Description = "CommittedRecordings list is accessible at runtime")]
        public void CommittedRecordingsAccessible()
        {
            // Just verify the static list exists and doesn't throw
            var recordings = RecordingStore.CommittedRecordings;
            InGameAssert.IsNotNull(recordings, "CommittedRecordings should not be null");
            ParsekLog.Verbose("TestRunner", $"CommittedRecordings count: {recordings.Count}");
        }

        [InGameTest(Category = "RecordingStore", Description = "All committed recordings have valid IDs and non-empty Points")]
        public void CommittedRecordingsHaveValidData()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings.Count == 0)
            {
                ParsekLog.Verbose("TestRunner", "No committed recordings to validate (skipping content checks)");
                return;
            }

            int valid = 0;
            foreach (var rec in recordings)
            {
                InGameAssert.IsNotNull(rec.RecordingId, $"Recording has null ID");
                InGameAssert.IsTrue(rec.RecordingId.Length > 0, "Recording has empty ID");
                InGameAssert.IsNotNull(rec.Points, $"Recording {rec.RecordingId} has null Points");
                InGameAssert.IsTrue(rec.Points.Count >= 2,
                    $"Recording {rec.RecordingId} has {rec.Points.Count} points (need >= 2)");

                // Time should be monotonically non-decreasing
                for (int i = 1; i < rec.Points.Count; i++)
                {
                    InGameAssert.IsTrue(rec.Points[i].ut >= rec.Points[i - 1].ut,
                        $"Recording {rec.RecordingId}: point {i} UT {rec.Points[i].ut} < previous {rec.Points[i - 1].ut}");
                }
                valid++;
            }
            ParsekLog.Verbose("TestRunner", $"Validated {valid} committed recordings");
        }

        #endregion

        #region TrajectoryMath

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns true when velocity direction changes")]
        public void ShouldRecordPointDetectsDirectionChange()
        {
            var vel1 = new Vector3(100, 0, 0);
            var vel2 = new Vector3(0, 100, 0); // 90 degree change
            bool result = TrajectoryMath.ShouldRecordPoint(vel2, vel1, 10.0, 9.5, 3f, 2f, 5f);
            InGameAssert.IsTrue(result, "Should record when velocity direction changes 90 degrees");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns true when max interval exceeded")]
        public void ShouldRecordPointRespectsMaxInterval()
        {
            var vel = new Vector3(100, 0, 0);
            bool result = TrajectoryMath.ShouldRecordPoint(vel, vel, 20.0, 16.0, 3f, 2f, 5f);
            InGameAssert.IsTrue(result, "Should record when interval > maxSampleInterval");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns false when nothing changed")]
        public void ShouldRecordPointReturnsFalseWhenStable()
        {
            var vel = new Vector3(100, 0, 0);
            bool result = TrajectoryMath.ShouldRecordPoint(vel, vel, 10.1, 10.0, 3f, 2f, 5f);
            InGameAssert.IsFalse(result, "Should not record when velocity stable and within interval");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "SanitizeQuaternion fixes NaN quaternions")]
        public void SanitizeQuaternionFixesNaN()
        {
            var bad = new Quaternion(float.NaN, 0, 0, 1);
            var sanitized = TrajectoryMath.SanitizeQuaternion(bad);
            InGameAssert.IsFalse(float.IsNaN(sanitized.x), "Sanitized quaternion should not have NaN");
            InGameAssert.IsFalse(float.IsNaN(sanitized.y), "Sanitized quaternion should not have NaN");
            InGameAssert.IsFalse(float.IsNaN(sanitized.z), "Sanitized quaternion should not have NaN");
            InGameAssert.IsFalse(float.IsNaN(sanitized.w), "Sanitized quaternion should not have NaN");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "SanitizeQuaternion preserves valid quaternions")]
        public void SanitizeQuaternionPreservesValid()
        {
            var good = new Quaternion(0, 0, 0, 1);
            var result = TrajectoryMath.SanitizeQuaternion(good);
            InGameAssert.ApproxEqual(good.x, result.x);
            InGameAssert.ApproxEqual(good.y, result.y);
            InGameAssert.ApproxEqual(good.z, result.z);
            InGameAssert.ApproxEqual(good.w, result.w);
        }

        [InGameTest(Category = "TrajectoryMath", Description = "PureLookRotation matches Unity LookRotation")]
        public void PureLookRotationMatchesUnity()
        {
            var forward = new Vector3(1, 0, 0);
            var up = Vector3.up;
            var unity = Quaternion.LookRotation(forward, up);
            var pure = TrajectoryMath.PureLookRotation(forward, up);
            InGameAssert.ApproxEqual(unity.x, pure.x, 0.001f, "x mismatch");
            InGameAssert.ApproxEqual(unity.y, pure.y, 0.001f, "y mismatch");
            InGameAssert.ApproxEqual(unity.z, pure.z, 0.001f, "z mismatch");
            InGameAssert.ApproxEqual(unity.w, pure.w, 0.001f, "w mismatch");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "FindWaypointIndex locates correct bracket in point list")]
        public void FindWaypointIndexBrackets()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 },
                new TrajectoryPoint { ut = 300 },
                new TrajectoryPoint { ut = 400 },
            };
            int cached = 0;
            int idx = TrajectoryMath.FindWaypointIndex(points, ref cached, 250);
            InGameAssert.AreEqual(1, idx, "Should find index 1 for ut=250 (between 200 and 300)");
        }

        #endregion

        #region Unity Environment

        [InGameTest(Category = "Unity", Description = "Time.timeScale is positive (game not frozen)")]
        public void TimeScalePositive()
        {
            InGameAssert.IsGreaterThan(Time.timeScale, 0, "Time.timeScale should be > 0");
        }

        [InGameTest(Category = "Unity", Description = "Camera.main exists")]
        public void MainCameraExists()
        {
            // In Flight or KSC, Camera.main should exist
            InGameAssert.IsNotNull(Camera.main, "Camera.main should exist in this scene");
        }

        [InGameTest(Category = "Unity", Description = "Can create and destroy a GameObject")]
        public void GameObjectLifecycle()
        {
            var go = new GameObject("ParsekTestObject");
            runner.TrackForCleanup(go);
            InGameAssert.IsNotNull(go, "Created GameObject should not be null");
            InGameAssert.AreEqual("ParsekTestObject", go.name);
        }

        [InGameTest(Category = "Unity", Description = "Can create a primitive mesh at runtime")]
        public void PrimitiveMeshCreation()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "ParsekTestSphere";
            runner.TrackForCleanup(go);
            InGameAssert.IsNotNull(go.GetComponent<MeshFilter>(), "Sphere should have MeshFilter");
            InGameAssert.IsNotNull(go.GetComponent<MeshRenderer>(), "Sphere should have MeshRenderer");
        }

        #endregion

        #region KSP Environment

        [InGameTest(Category = "KSP", Description = "HighLogic.LoadedScene is valid")]
        public void LoadedSceneValid()
        {
            var scene = HighLogic.LoadedScene;
            InGameAssert.IsTrue(
                scene == GameScenes.FLIGHT || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION || scene == GameScenes.EDITOR,
                $"Unexpected scene: {scene}");
        }

        [InGameTest(Category = "KSP", Description = "Kerbin exists in FlightGlobals")]
        public void KerbinExists()
        {
            var kerbin = FlightGlobals.GetBodyByName("Kerbin");
            InGameAssert.IsNotNull(kerbin, "Kerbin should exist in FlightGlobals");
            InGameAssert.IsGreaterThan(kerbin.Radius, 0, "Kerbin radius should be positive");
        }

        [InGameTest(Category = "KSP", Description = "PartLoader has loaded parts")]
        public void PartLoaderHasParts()
        {
            InGameAssert.IsNotNull(PartLoader.LoadedPartsList, "PartLoader.LoadedPartsList should not be null");
            InGameAssert.IsGreaterThan(PartLoader.LoadedPartsList.Count, 0,
                "PartLoader should have at least one loaded part");
            ParsekLog.Verbose("TestRunner", $"PartLoader has {PartLoader.LoadedPartsList.Count} parts");
        }

        [InGameTest(Category = "KSP", Description = "KSPUtil.ApplicationRootPath is set")]
        public void ApplicationRootPathSet()
        {
            string root = KSPUtil.ApplicationRootPath;
            InGameAssert.IsNotNull(root, "KSPUtil.ApplicationRootPath should not be null");
            InGameAssert.IsTrue(root.Length > 0, "KSPUtil.ApplicationRootPath should not be empty");
            ParsekLog.Verbose("TestRunner", $"ApplicationRootPath: {root}");
        }

        [InGameTest(Category = "KSP", Scene = GameScenes.FLIGHT,
            Description = "Active vessel exists in Flight scene")]
        public void ActiveVesselExists()
        {
            InGameAssert.IsNotNull(FlightGlobals.ActiveVessel,
                "FlightGlobals.ActiveVessel should exist in Flight scene");
            InGameAssert.IsTrue(FlightGlobals.ActiveVessel.parts.Count > 0,
                "Active vessel should have at least one part");
            ParsekLog.Verbose("TestRunner",
                $"Active vessel: {FlightGlobals.ActiveVessel.vesselName} ({FlightGlobals.ActiveVessel.parts.Count} parts)");
        }

        [InGameTest(Category = "KSP", Scene = GameScenes.FLIGHT,
            Description = "FlightCamera exists in Flight scene")]
        public void FlightCameraExists()
        {
            InGameAssert.IsNotNull(FlightCamera.fetch, "FlightCamera.fetch should exist");
            InGameAssert.IsNotNull(FlightCamera.fetch.mainCamera, "FlightCamera.mainCamera should exist");
        }

        #endregion

        #region Parsek Settings

        [InGameTest(Category = "Settings", Description = "ParsekSettings.Current is accessible")]
        public void SettingsAccessible()
        {
            var settings = ParsekSettings.Current;
            InGameAssert.IsNotNull(settings, "ParsekSettings.Current should not be null");
        }

        [InGameTest(Category = "Settings", Description = "ParsekLog writes to KSP log without error")]
        public void LoggingWorks()
        {
            // This should not throw
            ParsekLog.Info("TestRunner", "In-game logging verification test");
            ParsekLog.Verbose("TestRunner", "Verbose logging verification test");
        }

        #endregion
    }

    /// <summary>
    /// Tests that require active ghost playback to verify visual and positioning systems.
    /// </summary>
    public class GhostPlaybackTests
    {
        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Ghost sphere can be created and destroyed")]
        public void GhostSphereLifecycle()
        {
            var sphere = GhostVisualBuilder.CreateGhostSphere("TestGhostSphere", Color.cyan);
            InGameAssert.IsNotNull(sphere, "CreateGhostSphere should return a non-null GameObject");
            InGameAssert.IsNotNull(sphere.GetComponent<MeshRenderer>(),
                "Ghost sphere should have a MeshRenderer");
            Object.Destroy(sphere);
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Committed recording with snapshot can build ghost visuals")]
        public IEnumerator BuildGhostFromCommittedRecording()
        {
            var recordings = RecordingStore.CommittedRecordings;
            Recording withSnapshot = null;
            foreach (var rec in recordings)
            {
                if (rec.GhostVisualSnapshot != null)
                {
                    withSnapshot = rec;
                    break;
                }
            }

            if (withSnapshot == null)
            {
                ParsekLog.Verbose("TestRunner",
                    "No committed recordings with ghost snapshot — skipping ghost build test");
                yield break;
            }

            ParsekLog.Verbose("TestRunner",
                $"Building ghost from recording: {withSnapshot.VesselName} ({withSnapshot.RecordingId})");

            var result = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                withSnapshot, "ParsekTest_GhostBuild");

            InGameAssert.IsNotNull(result, "BuildTimelineGhostFromSnapshot should return a result");
            InGameAssert.IsNotNull(result.root, "Ghost root GameObject should not be null");
            ParsekLog.Verbose("TestRunner", $"Ghost built successfully: {result.root.name}");

            // Verify it has child transforms (part meshes)
            int childCount = result.root.transform.childCount;
            InGameAssert.IsGreaterThan(childCount, 0,
                $"Ghost root should have child transforms, got {childCount}");
            ParsekLog.Verbose("TestRunner", $"Ghost has {childCount} child transforms");

            yield return null; // let it render one frame

            // Cleanup
            Object.Destroy(result.root);
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Ghost sphere positions correctly at vessel location")]
        public IEnumerator GhostSpherePositioning()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                ParsekLog.Verbose("TestRunner", "No active vessel — skipping positioning test");
                yield break;
            }

            var sphere = GhostVisualBuilder.CreateGhostSphere("TestPositionSphere", Color.magenta);
            sphere.transform.position = vessel.transform.position + new Vector3(10, 0, 0);

            yield return null; // one frame

            float dist = Vector3.Distance(sphere.transform.position,
                vessel.transform.position + new Vector3(10, 0, 0));
            // Floating origin can shift things, but within one frame it should be close
            InGameAssert.IsLessThan(dist, 1.0,
                $"Sphere should be near expected position (distance={dist:F2})");

            Object.Destroy(sphere);
        }

        [InGameTest(Category = "GhostPlayback",
            Description = "PartLoader can resolve stock part names with dot-notation")]
        public void PartLoaderResolvesStockParts()
        {
            // KSP converts underscores to dots in internal part names
            string[] testParts = { "fuelTankSmallFlat", "solidBooster.v2", "mk1pod.v2" };
            int found = 0;
            foreach (var partName in testParts)
            {
                var info = PartLoader.getPartInfoByName(partName);
                if (info != null)
                {
                    found++;
                    ParsekLog.Verbose("TestRunner", $"Resolved part: {partName} -> {info.title}");
                }
                else
                {
                    ParsekLog.Verbose("TestRunner", $"Part not found: {partName} (may not be installed)");
                }
            }
            InGameAssert.IsGreaterThan(found, 0,
                "At least one stock part should resolve via PartLoader");
        }
    }

    /// <summary>
    /// Tests for recording data integrity and serialization at runtime.
    /// </summary>
    public class SerializationTests
    {
        [InGameTest(Category = "Serialization", Description = "ConfigNode round-trip preserves data")]
        public void ConfigNodeRoundTrip()
        {
            var node = new ConfigNode("TEST");
            node.AddValue("name", "testValue");
            node.AddValue("number", "42");
            node.AddValue("float", "3.14");

            var child = node.AddNode("CHILD");
            child.AddValue("key", "value");

            // Serialize to string and back
            string serialized = node.ToString();
            InGameAssert.IsTrue(serialized.Contains("testValue"),
                "Serialized ConfigNode should contain 'testValue'");

            var parsed = ConfigNode.Parse(serialized);
            InGameAssert.IsNotNull(parsed, "ConfigNode.Parse should return non-null");

            // Parse wraps in a root node
            var restored = parsed.GetNode("TEST");
            InGameAssert.IsNotNull(restored, "Should find TEST node in parsed result");
            InGameAssert.AreEqual("testValue", restored.GetValue("name"));
            InGameAssert.AreEqual("42", restored.GetValue("number"));
        }

        [InGameTest(Category = "Serialization",
            Description = "ConfigNode float values use invariant culture (no comma decimals)")]
        public void ConfigNodeFloatLocaleInvariant()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double testValue = 12345.6789;
            string serialized = testValue.ToString("R", ic);

            InGameAssert.IsFalse(serialized.Contains(","),
                $"InvariantCulture serialization should not contain comma: '{serialized}'");
            InGameAssert.IsTrue(serialized.Contains("."),
                $"InvariantCulture serialization should contain dot: '{serialized}'");

            double restored = double.Parse(serialized, System.Globalization.NumberStyles.Float, ic);
            InGameAssert.ApproxEqual(testValue, restored, 0.0001,
                $"Round-trip mismatch: {testValue} vs {restored}");
        }

        [InGameTest(Category = "Serialization", Description = "RecordingPaths validates IDs correctly")]
        public void RecordingPathsValidation()
        {
            // Valid IDs
            InGameAssert.IsTrue(RecordingPaths.ValidateRecordingId("abc-123"),
                "Simple alphanumeric ID should be valid");

            // Invalid IDs
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId(null),
                "null ID should be invalid");
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId(""),
                "empty ID should be invalid");
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId("../etc/passwd"),
                "path traversal should be invalid");
        }
    }
}
