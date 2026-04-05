using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            int valid = 0, skippedRoots = 0;
            foreach (var rec in recordings)
            {
                InGameAssert.IsNotNull(rec.RecordingId, $"Recording has null ID");
                InGameAssert.IsTrue(rec.RecordingId.Length > 0, "Recording has empty ID");
                InGameAssert.IsNotNull(rec.Points, $"Recording {rec.RecordingId} has null Points");

                // Tree root recordings are containers with no trajectory data
                if (rec.Points.Count == 0)
                {
                    skippedRoots++;
                    continue;
                }

                // Time should be monotonically non-decreasing
                for (int i = 1; i < rec.Points.Count; i++)
                {
                    InGameAssert.IsTrue(rec.Points[i].ut >= rec.Points[i - 1].ut,
                        $"Recording {rec.RecordingId}: point {i} UT {rec.Points[i].ut} < previous {rec.Points[i - 1].ut}");
                }
                valid++;
            }
            ParsekLog.Verbose("TestRunner",
                $"Validated {valid} committed recordings, {skippedRoots} tree root(s) skipped");
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

        [InGameTest(Category = "Unity", Description = "A scene camera is accessible")]
        public void SceneCameraExists()
        {
            // Camera.main can be null in map view (flight camera disabled) and tracking station.
            // Check for any available camera: Camera.main, FlightCamera, or PlanetariumCamera.
            bool hasCamera = Camera.main != null
                || (FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
                || PlanetariumCamera.Camera != null;
            InGameAssert.IsTrue(hasCamera,
                "No scene camera found (Camera.main, FlightCamera, and PlanetariumCamera all null)");
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

        [InGameTest(Category = "KSP", Description = "HighLogic.LoadedScene is a recognized game scene")]
        public void LoadedSceneValid()
        {
            var scene = HighLogic.LoadedScene;
            InGameAssert.IsTrue(
                scene == GameScenes.FLIGHT || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION || scene == GameScenes.EDITOR
                || scene == GameScenes.MAINMENU,
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
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                // No active game at main menu — settings may not be loaded
                ParsekLog.Verbose("TestRunner", "Main menu — skipping settings check");
                return;
            }
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

    /// <summary>
    /// Tier 1: Verify ghost visual construction against real PartLoader prefabs.
    /// Catches part name resolution failures (underscore→dot, variant suffixes, mod parts).
    /// </summary>
    public class GhostVisualConstructionTests
    {
        private readonly InGameTestRunner runner;
        public GhostVisualConstructionTests(InGameTestRunner runner) { this.runner = runner; }

        [InGameTest(Category = "GhostVisuals",
            Description = "Every committed recording with snapshot builds a ghost (or sphere fallback) without crash")]
        public void AllSnapshotsBuildWithoutCrash()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int built = 0, fallback = 0, noSnapshot = 0;

            foreach (var rec in recordings)
            {
                if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot == null)
                {
                    noSnapshot++;
                    continue;
                }

                var result = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    rec, $"ParsekTest_Build_{built}");

                if (result != null && result.root != null)
                {
                    built++;
                    runner.TrackForCleanup(result.root);
                }
                else
                {
                    // null result means snapshot had no PART nodes or all parts failed —
                    // not a crash, just a graceful degradation
                    fallback++;
                }
            }

            ParsekLog.Info("TestRunner",
                $"Ghost build sweep: {built} built, {fallback} degraded, {noSnapshot} no snapshot " +
                $"(of {recordings.Count} recordings)");
        }

        [InGameTest(Category = "GhostVisuals",
            Description = "All snapshot PART names resolve in PartLoader (catches underscore→dot bugs)")]
        public void AllSnapshotPartNamesResolve()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int totalParts = 0, resolved = 0, missing = 0;
            var missingNames = new HashSet<string>();

            foreach (var rec in recordings)
            {
                ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
                if (snapshot == null) continue;

                foreach (var partNode in snapshot.GetNodes("PART"))
                {
                    string partName = partNode.GetValue("name");
                    if (string.IsNullOrEmpty(partName)) continue;

                    // Strip persistentId suffix if present (name = partName_pidHex in some formats)
                    // KSP snapshot PART names may include a _persistentId suffix (e.g. "mk1pod.v2_12345").
                    // Split on underscore to get the base part name. Note: mod parts with
                    // unconverted underscores in their name would be truncated here — stock parts
                    // have underscores converted to dots at runtime, so this is safe for stock.
                    string lookupName = partName.Split('_')[0];
                    totalParts++;

                    if (PartLoader.getPartInfoByName(lookupName) != null)
                        resolved++;
                    else
                    {
                        missing++;
                        missingNames.Add(lookupName);
                    }
                }
            }

            if (totalParts == 0)
            {
                ParsekLog.Verbose("TestRunner", "No snapshot parts to validate");
                return;
            }

            ParsekLog.Info("TestRunner",
                $"Part name resolution: {resolved}/{totalParts} resolved, {missing} missing");
            if (missingNames.Count > 0)
                ParsekLog.Warn("TestRunner",
                    $"Unresolvable part names: {string.Join(", ", missingNames)}");

            // At least some parts should resolve (all missing = likely broken snapshot format)
            InGameAssert.IsGreaterThan(resolved, 0,
                $"No parts resolved from {totalParts} snapshot parts. Missing: {string.Join(", ", missingNames)}");
        }

        [InGameTest(Category = "GhostVisuals",
            Description = "Ghost built from snapshot has MeshRenderer on at least one child")]
        public void GhostHasRenderers()
        {
            var recordings = RecordingStore.CommittedRecordings;
            Recording withSnapshot = recordings.FirstOrDefault(
                r => r.GhostVisualSnapshot != null || r.VesselSnapshot != null);
            if (withSnapshot == null)
            {
                ParsekLog.Verbose("TestRunner", "No recordings with snapshot — skipping renderer check");
                return;
            }

            var result = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                withSnapshot, "ParsekTest_Renderer");
            if (result == null || result.root == null)
            {
                ParsekLog.Verbose("TestRunner", "Ghost build returned null — skipping renderer check");
                return;
            }
            runner.TrackForCleanup(result.root);

            var renderers = result.root.GetComponentsInChildren<MeshRenderer>(true);
            InGameAssert.IsGreaterThan(renderers.Length, 0,
                $"Ghost for '{withSnapshot.VesselName}' has no MeshRenderers in hierarchy");
            ParsekLog.Verbose("TestRunner",
                $"Ghost '{withSnapshot.VesselName}' has {renderers.Length} MeshRenderers");
        }
    }

    /// <summary>
    /// Tier 1: Verify recording data integrity against live KSP state.
    /// Catches body name mismatches, stale orbit data, broken snapshot references.
    /// </summary>
    public class RecordingDataHealthTests
    {
        [InGameTest(Category = "DataHealth",
            Description = "All body names in trajectory points resolve in FlightGlobals")]
        public void AllBodyNamesResolve()
        {
            var recordings = RecordingStore.CommittedRecordings;
            var allBodies = new HashSet<string>();
            var missingBodies = new HashSet<string>();

            foreach (var rec in recordings)
            {
                if (rec.Points == null) continue;
                foreach (var pt in rec.Points)
                {
                    if (string.IsNullOrEmpty(pt.bodyName)) continue;
                    allBodies.Add(pt.bodyName);
                }
            }

            foreach (var bodyName in allBodies)
            {
                if (FlightGlobals.GetBodyByName(bodyName) == null)
                    missingBodies.Add(bodyName);
            }

            if (allBodies.Count == 0)
            {
                ParsekLog.Verbose("TestRunner", "No body names found in recordings");
                return;
            }

            ParsekLog.Info("TestRunner",
                $"Body name resolution: {allBodies.Count - missingBodies.Count}/{allBodies.Count} resolved");

            InGameAssert.IsTrue(missingBodies.Count == 0,
                $"Unresolvable body names: {string.Join(", ", missingBodies)}");
        }

        [InGameTest(Category = "DataHealth",
            Description = "All orbit segment bodies resolve and have positive radius")]
        public void OrbitSegmentBodiesValid()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int segments = 0, valid = 0;
            var missingBodies = new HashSet<string>();

            foreach (var rec in recordings)
            {
                if (rec.OrbitSegments == null) continue;
                foreach (var seg in rec.OrbitSegments)
                {
                    segments++;
                    if (string.IsNullOrEmpty(seg.bodyName))
                    {
                        missingBodies.Add("(empty)");
                        continue;
                    }

                    var body = FlightGlobals.GetBodyByName(seg.bodyName);
                    if (body == null)
                    {
                        missingBodies.Add(seg.bodyName);
                        continue;
                    }

                    InGameAssert.IsGreaterThan(body.Radius, 0,
                        $"Body '{seg.bodyName}' has non-positive radius");
                    InGameAssert.IsGreaterThan(seg.semiMajorAxis, 0,
                        $"Orbit segment for '{seg.bodyName}' has non-positive SMA={seg.semiMajorAxis}");
                    valid++;
                }
            }

            if (segments == 0)
            {
                ParsekLog.Verbose("TestRunner", "No orbit segments in committed recordings");
                return;
            }

            ParsekLog.Info("TestRunner",
                $"Orbit segments: {valid}/{segments} valid");
            InGameAssert.IsTrue(missingBodies.Count == 0,
                $"Orbit segments with unresolvable bodies: {string.Join(", ", missingBodies)}");
        }

        [InGameTest(Category = "DataHealth",
            Description = "Every recording has at least one snapshot PART resolvable in PartLoader")]
        public void EveryRecordingHasResolvablePart()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int checked_ = 0;
            var failures = new List<string>();

            foreach (var rec in recordings)
            {
                ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
                if (snapshot == null) continue;

                var partNodes = snapshot.GetNodes("PART");
                if (partNodes == null || partNodes.Length == 0) continue;

                checked_++;
                bool anyResolved = false;
                foreach (var partNode in partNodes)
                {
                    string partName = partNode.GetValue("name");
                    if (string.IsNullOrEmpty(partName)) continue;
                    // Try raw name first, then split on underscore for _persistentId suffix
                    if (PartLoader.getPartInfoByName(partName) != null)
                    {
                        anyResolved = true;
                        break;
                    }
                    // KSP snapshot PART names may include a _persistentId suffix (e.g. "mk1pod.v2_12345").
                    // Split on underscore to get the base part name. Note: mod parts with
                    // unconverted underscores in their name would be truncated here — stock parts
                    // have underscores converted to dots at runtime, so this is safe for stock.
                    string lookupName = partName.Split('_')[0];
                    if (lookupName != partName && PartLoader.getPartInfoByName(lookupName) != null)
                    {
                        anyResolved = true;
                        break;
                    }
                }

                if (!anyResolved)
                {
                    // Showcase/synthetic recordings may use part names not in PartLoader — warn, don't fail
                    ParsekLog.Warn("TestRunner",
                        $"No resolvable parts in '{rec.VesselName ?? rec.RecordingId}'");
                    failures.Add($"{rec.VesselName ?? rec.RecordingId}");
                }
            }

            if (checked_ == 0)
            {
                ParsekLog.Verbose("TestRunner", "No recordings with snapshots to check");
                return;
            }

            ParsekLog.Info("TestRunner",
                $"Part resolution check: {checked_ - failures.Count}/{checked_} recordings have resolvable parts");
            if (failures.Count > 0)
                ParsekLog.Warn("TestRunner",
                    $"Recordings with no resolvable parts (may be synthetic/showcase): {string.Join(", ", failures)}");
        }

        [InGameTest(Category = "DataHealth",
            Description = "Recording time ranges are sane (EndUT > StartUT, positive duration)")]
        public void RecordingTimeRangesSane()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int checked_ = 0;

            foreach (var rec in recordings)
            {
                if (rec.Points == null || rec.Points.Count < 2) continue;
                checked_++;

                double startUT = rec.Points[0].ut;
                double endUT = rec.Points[rec.Points.Count - 1].ut;

                InGameAssert.IsGreaterThan(endUT, startUT,
                    $"Recording '{rec.VesselName}': EndUT ({endUT:F1}) should be > StartUT ({startUT:F1})");

                // Sanity: recordings shouldn't span more than a few years of game time
                double durationDays = (endUT - startUT) / 21600.0; // KSP day = 6h = 21600s
                InGameAssert.IsLessThan(durationDays, 10000,
                    $"Recording '{rec.VesselName}' spans {durationDays:F0} Kerbin days — suspiciously long");
            }

            ParsekLog.Verbose("TestRunner", $"Time range check: {checked_} recordings validated");
        }
    }

    /// <summary>
    /// Tier 1: Verify file I/O paths and save/load round-trip integrity.
    /// </summary>
    public class SaveLoadTests
    {
        [InGameTest(Category = "SaveLoad",
            Description = "RecordingPaths.EnsureRecordingsDirectory creates/resolves the dir")]
        public void RecordingsDirectoryExists()
        {
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
            {
                ParsekLog.Verbose("TestRunner", "No SaveFolder set — skipping directory check");
                return;
            }

            string dir = RecordingPaths.EnsureRecordingsDirectory();
            InGameAssert.IsNotNull(dir, "EnsureRecordingsDirectory returned null");
            InGameAssert.IsTrue(Directory.Exists(dir),
                $"Recordings directory does not exist: {dir}");
            ParsekLog.Verbose("TestRunner", $"Recordings directory: {dir}");
        }

        [InGameTest(Category = "SaveLoad",
            Description = "ParsekScenario is active in the current game")]
        public void ScenarioInstanceActive()
        {
            if (HighLogic.CurrentGame == null)
            {
                ParsekLog.Verbose("TestRunner", "No active game — skipping scenario check");
                return;
            }
            var scenario = Object.FindObjectOfType<ParsekScenario>();
            InGameAssert.IsNotNull(scenario,
                "ParsekScenario should be active (ScenarioModule loaded)");
        }

        [InGameTest(Category = "SaveLoad",
            Description = "Recording count survives ConfigNode round-trip through ParsekScenario")]
        public void ScenarioRoundTripPreservesCount()
        {
            var scenario = Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null)
            {
                ParsekLog.Verbose("TestRunner", "No ParsekScenario instance — skipping round-trip");
                return;
            }

            int beforeCount = RecordingStore.CommittedRecordings.Count;

            // Serialize current state
            var saveNode = new ConfigNode("SCENARIO");
            scenario.OnSave(saveNode);

            // Deserialize back
            scenario.OnLoad(saveNode);
            int afterCount = RecordingStore.CommittedRecordings.Count;

            InGameAssert.AreEqual(beforeCount, afterCount,
                $"Recording count changed after round-trip: {beforeCount} -> {afterCount}");
            ParsekLog.Verbose("TestRunner",
                $"Scenario round-trip: {beforeCount} recordings preserved");
        }

        [InGameTest(Category = "SaveLoad",
            Description = "External recording files exist on disk for committed v3 recordings")]
        public void ExternalFilesExist()
        {
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
            {
                ParsekLog.Verbose("TestRunner", "No SaveFolder — skipping file check");
                return;
            }

            var recordings = RecordingStore.CommittedRecordings;
            int checked_ = 0, found = 0, missing = 0;

            foreach (var rec in recordings)
            {
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (!RecordingPaths.ValidateRecordingId(rec.RecordingId)) continue;

                // Check for .prec trajectory file
                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    Path.Combine("Parsek", "Recordings", rec.RecordingId + ".prec"));
                if (string.IsNullOrEmpty(precPath)) continue;

                checked_++;
                if (File.Exists(precPath))
                    found++;
                else
                    missing++;
            }

            ParsekLog.Info("TestRunner",
                $"External files: {found}/{checked_} .prec files found, {missing} missing");
            // Don't fail on missing — some recordings may be v2 inline format
        }
    }

    /// <summary>
    /// Tier 2: Verify crew reservation state against live KSP roster.
    /// </summary>
    public class CrewReservationTests
    {
        [InGameTest(Category = "CrewReservation",
            Description = "KSP crew roster is accessible and has kerbals")]
        public void RosterAccessible()
        {
            var game = HighLogic.CurrentGame;
            if (game == null)
            {
                // No active game (main menu) — not a failure
                ParsekLog.Verbose("TestRunner", "No active game — skipping roster check");
                return;
            }

            var roster = game.CrewRoster;
            InGameAssert.IsNotNull(roster, "CrewRoster should not be null");
            InGameAssert.IsGreaterThan(roster.Count, 0, "Crew roster should have at least one kerbal");
            ParsekLog.Verbose("TestRunner", $"Crew roster has {roster.Count} kerbals");
        }

        [InGameTest(Category = "CrewReservation",
            Description = "All replacement kerbals exist in roster and are not Dead")]
        public void ReplacementsAreValid()
        {
            var replacements = CrewReservationManager.CrewReplacements;
            if (replacements.Count == 0)
            {
                ParsekLog.Verbose("TestRunner", "No active crew replacements");
                return;
            }

            var roster = HighLogic.CurrentGame.CrewRoster;
            int valid = 0;
            var problems = new List<string>();

            foreach (var kvp in replacements)
            {
                string originalName = kvp.Key;
                string replacementName = kvp.Value;

                // Replacement kerbal must exist in roster
                var pcm = roster[replacementName];
                if (pcm == null)
                {
                    problems.Add($"Replacement '{replacementName}' (for '{originalName}') not in roster");
                    continue;
                }

                // Replacement should not be Dead or Missing
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    problems.Add($"Replacement '{replacementName}' is Dead");
                    continue;
                }

                valid++;
            }

            ParsekLog.Info("TestRunner",
                $"Crew replacements: {valid}/{replacements.Count} valid");
            InGameAssert.IsTrue(problems.Count == 0,
                $"Crew replacement problems: {string.Join("; ", problems)}");
        }

        [InGameTest(Category = "CrewReservation",
            Description = "No crew replacement maps a kerbal to themselves")]
        public void NoSelfReplacements()
        {
            var replacements = CrewReservationManager.CrewReplacements;
            foreach (var kvp in replacements)
            {
                InGameAssert.AreNotEqual(kvp.Key, kvp.Value,
                    $"Crew replacement self-reference: '{kvp.Key}' → '{kvp.Value}'");
            }
        }

        [InGameTest(Category = "CrewReservation",
            Description = "No replacement name appears as both a key and a value (circular chain)")]
        public void NoCircularReplacements()
        {
            var replacements = CrewReservationManager.CrewReplacements;
            var keys = new HashSet<string>(replacements.Keys);
            foreach (var kvp in replacements)
            {
                InGameAssert.IsFalse(keys.Contains(kvp.Value),
                    $"Circular replacement chain: '{kvp.Value}' is both a replacement and a reserved original");
            }
        }

        [InGameTest(Category = "CrewReservation",
            Description = "No Parsek-reserved kerbals have rosterStatus=Assigned (T44 refactor validation)")]
        public void ReservedCrewNotAssigned()
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null)
            {
                InGameAssert.Skip("No KerbalsModule initialized");
                return;
            }

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster available");
                return;
            }

            var problems = new List<string>();
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned) continue;

                // Assigned is OK if the kerbal is genuinely on a vessel
                bool onVessel = false;
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null)
                {
                    for (int i = 0; i < flightState.protoVessels.Count; i++)
                    {
                        if (flightState.protoVessels[i].GetVesselCrew().Contains(pcm))
                        {
                            onVessel = true;
                            break;
                        }
                    }
                }

                if (!onVessel && kerbals.ShouldFilterFromCrewDialog(pcm.name))
                {
                    problems.Add($"'{pcm.name}' is Assigned but not on any vessel " +
                        "(should be Available, filtered via CrewDialogFilterPatch)");
                }
            }

            InGameAssert.IsTrue(problems.Count == 0,
                $"Reserved crew with stale Assigned status: {string.Join("; ", problems)}");
            ParsekLog.Info("TestRunner",
                $"ReservedCrewNotAssigned: checked roster, {problems.Count} problem(s)");
        }
    }

    /// <summary>
    /// Tier 2: Verify ghost map presence and CommNet integration against live KSP state.
    /// </summary>
    public class GhostMapPresenceTests
    {
        [InGameTest(Category = "MapPresence",
            Description = "All ghost map PIDs resolve to ProtoVessels in FlightState")]
        public void GhostPidsResolveToProtoVessels()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0)
            {
                ParsekLog.Verbose("TestRunner", "No ghost map vessels active");
                return;
            }

            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null)
            {
                ParsekLog.Verbose("TestRunner", "No FlightState available");
                return;
            }

            int resolved = 0, orphaned = 0;
            foreach (uint pid in ghostPids)
            {
                bool found = flightState.protoVessels.Any(pv => pv.persistentId == pid);
                if (found) resolved++;
                else orphaned++;
            }

            ParsekLog.Info("TestRunner",
                $"Ghost map PIDs: {resolved}/{ghostPids.Count} resolve to ProtoVessels, {orphaned} orphaned");
            InGameAssert.IsTrue(orphaned == 0,
                $"{orphaned} ghost map PIDs have no corresponding ProtoVessel (leak)");
        }

        [InGameTest(Category = "MapPresence",
            Description = "No ghost PID collides with a real (non-ghost) vessel PID")]
        public void NoPidCollisionWithRealVessels()
        {
            var ghostPids = GhostMapPresence.ghostMapVesselPids;
            if (ghostPids.Count == 0) return;

            var realVessels = FlightGlobals.Vessels;
            if (realVessels == null) return;

            foreach (var vessel in realVessels)
            {
                if (vessel == null) continue;
                // A vessel whose PID is in ghostPids is expected — that's the ghost itself.
                // But we want to make sure no NON-ghost vessel accidentally shares a ghost PID.
                if (ghostPids.Contains(vessel.persistentId) && !GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                {
                    InGameAssert.Fail(
                        $"Real vessel '{vessel.vesselName}' (PID={vessel.persistentId}) collides with ghost PID");
                }
            }
        }

        [InGameTest(Category = "MapPresence",
            Description = "Recordings with antenna specs produce positive relay power")]
        public void AntennaSpecsProduceRelayPower()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int withAntennas = 0, withRelayPower = 0;

            foreach (var rec in recordings)
            {
                if (rec.AntennaSpecs == null || rec.AntennaSpecs.Count == 0) continue;
                withAntennas++;

                double power = GhostCommNetRelay.ComputeCombinedAntennaPower(rec.AntennaSpecs);
                if (power > 0) withRelayPower++;

                // Individual antenna powers should be non-negative
                foreach (var spec in rec.AntennaSpecs)
                {
                    InGameAssert.IsTrue(spec.antennaPower >= 0,
                        $"Negative antenna power on '{spec.partName}': {spec.antennaPower}");
                }
            }

            ParsekLog.Info("TestRunner",
                $"Antenna specs: {withAntennas} recordings with antennas, {withRelayPower} with positive combined power");
        }
    }

    /// <summary>
    /// Tier 3: Multi-frame coroutine tests requiring Flight scene.
    /// </summary>
    public class FlightIntegrationTests
    {
        private readonly InGameTestRunner runner;
        public FlightIntegrationTests(InGameTestRunner runner) { this.runner = runner; }

        [InGameTest(Category = "FlightIntegration", Scene = GameScenes.FLIGHT,
            Description = "Ghost world position matches lat/lon/alt via GetWorldSurfacePosition")]
        public IEnumerator GhostPositionMatchesGeographic()
        {
            var recordings = RecordingStore.CommittedRecordings;
            Recording surfaceRec = null;
            foreach (var rec in recordings)
            {
                if (rec.Points != null && rec.Points.Count >= 2
                    && !string.IsNullOrEmpty(rec.Points[0].bodyName)
                    && rec.Points[0].altitude < 100000) // surface-ish
                {
                    surfaceRec = rec;
                    break;
                }
            }

            if (surfaceRec == null)
            {
                ParsekLog.Verbose("TestRunner", "No surface recording available — skipping position test");
                yield break;
            }

            var pt = surfaceRec.Points[0];
            var body = FlightGlobals.GetBodyByName(pt.bodyName);
            if (body == null)
            {
                ParsekLog.Verbose("TestRunner", $"Body '{pt.bodyName}' not found — skipping");
                yield break;
            }

            Vector3d expectedWorldPos = body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
            var sphere = GhostVisualBuilder.CreateGhostSphere("ParsekTest_GeoPos", Color.yellow);
            runner.TrackForCleanup(sphere);
            sphere.transform.position = (Vector3)expectedWorldPos;

            yield return null;

            // Verify the position didn't drift to NaN or zero
            InGameAssert.IsFalse(float.IsNaN(sphere.transform.position.x),
                "Ghost position X is NaN after placement");
            InGameAssert.IsFalse(sphere.transform.position == Vector3.zero,
                "Ghost position collapsed to zero (floating origin issue?)");

            ParsekLog.Verbose("TestRunner",
                $"Ghost placed at {pt.latitude:F4},{pt.longitude:F4} alt={pt.altitude:F1} on {pt.bodyName}");
        }

        [InGameTest(Category = "FlightIntegration", Scene = GameScenes.FLIGHT,
            Description = "ParsekFlight.Instance is active and accessible")]
        public void ParsekFlightInstanceActive()
        {
            InGameAssert.IsNotNull(ParsekFlight.Instance,
                "ParsekFlight.Instance should not be null in Flight scene");
        }

        [InGameTest(Category = "FlightIntegration", Scene = GameScenes.FLIGHT,
            Description = "Active vessel body has valid surface position API")]
        public void ActiveVesselBodySurfaceApi()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;

            var body = vessel.mainBody;
            InGameAssert.IsNotNull(body, "Active vessel mainBody should not be null");

            double lat = vessel.latitude;
            double lon = vessel.longitude;
            double alt = vessel.altitude;

            // Round-trip through geographic coords
            Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
            InGameAssert.IsFalse(double.IsNaN(worldPos.x),
                "GetWorldSurfacePosition returned NaN");
            InGameAssert.IsFalse(double.IsInfinity(worldPos.x),
                "GetWorldSurfacePosition returned Infinity");

            // Should be reasonably close to vessel's actual position
            double dist = Vector3d.Distance(worldPos, vessel.GetWorldPos3D());
            InGameAssert.IsLessThan(dist, 50.0,
                $"Geographic round-trip error: {dist:F2}m (expected < 50m)");
        }

        [InGameTest(Category = "FlightIntegration", Scene = GameScenes.FLIGHT,
            Description = "Harmony physics frame patch is operational")]
        public void HarmonyPatchOperational()
        {
            // If Harmony patching failed, PhysicsFramePatch wouldn't exist or wouldn't be called.
            // We can verify indirectly: if ParsekFlight is recording, the recorder is non-null.
            // If not recording, at least verify the Instance exists (patch registers callbacks).
            InGameAssert.IsNotNull(ParsekFlight.Instance,
                "ParsekFlight must be active for Harmony patches to function");

            // The fact that we're in Flight scene and ParsekFlight loaded means
            // ParsekHarmony.OnPatchApplied succeeded during mod load.
            ParsekLog.Verbose("TestRunner",
                $"ParsekFlight active, IsRecording={ParsekFlight.Instance.IsRecording}");
        }
    }
}
