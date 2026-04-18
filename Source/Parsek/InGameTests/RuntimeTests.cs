using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
            var monotonicFailures = new List<string>();
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
                bool monotonic = true;
                for (int i = 1; i < rec.Points.Count; i++)
                {
                    if (rec.Points[i].ut < rec.Points[i - 1].ut)
                    {
                        int prefixMatchCount = CountTrackSectionPrefixMatches(rec);
                        string firstPostPrefixSource = DescribeFirstPostPrefixPointSource(rec, prefixMatchCount);
                        monotonicFailures.Add(
                            $"Recording {rec.RecordingId}: point {i} UT {rec.Points[i].ut.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"< previous {rec.Points[i - 1].ut.ToString("R", CultureInfo.InvariantCulture)}; " +
                            $"trackSections={rec.TrackSections?.Count ?? 0} prefixMatchCount={prefixMatchCount} " +
                            $"firstPostPrefixSource={firstPostPrefixSource}");
                        monotonic = false;
                        break;
                    }
                }

                if (monotonic)
                    valid++;
            }

            InGameAssert.IsTrue(monotonicFailures.Count == 0, string.Join(System.Environment.NewLine, monotonicFailures.ToArray()));
            ParsekLog.Verbose("TestRunner",
                $"Validated {valid} committed recordings, {skippedRoots} tree root(s) skipped");

            int CountTrackSectionPrefixMatches(Recording rec)
            {
                if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                    return 0;

                var rebuiltPoints = new List<TrajectoryPoint>();
                RecordingStore.RebuildPointsFromTrackSections(rec.TrackSections, rebuiltPoints);
                int max = System.Math.Min(rebuiltPoints.Count, rec.Points.Count);
                int prefixMatchCount = 0;
                while (prefixMatchCount < max &&
                    TrajectoryPointsEqual(rebuiltPoints[prefixMatchCount], rec.Points[prefixMatchCount]))
                {
                    prefixMatchCount++;
                }

                return prefixMatchCount;
            }

            string DescribeFirstPostPrefixPointSource(Recording rec, int prefixMatchCount)
            {
                if (rec.Points == null || prefixMatchCount < 0 || prefixMatchCount >= rec.Points.Count)
                    return "none";

                TrajectoryPoint target = rec.Points[prefixMatchCount];
                for (int sectionIndex = 0; sectionIndex < rec.TrackSections.Count; sectionIndex++)
                {
                    List<TrajectoryPoint> frames = rec.TrackSections[sectionIndex].frames;
                    if (frames == null)
                        continue;

                    for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                    {
                        if (TrajectoryPointsEqual(frames[frameIndex], target))
                            return $"section[{sectionIndex}] source={rec.TrackSections[sectionIndex].source} frame[{frameIndex}]";
                    }
                }

                return "flat-only";
            }

            bool TrajectoryPointsEqual(TrajectoryPoint a, TrajectoryPoint b)
            {
                return a.ut == b.ut
                    && a.latitude == b.latitude
                    && a.longitude == b.longitude
                    && a.altitude == b.altitude
                    && a.rotation.x == b.rotation.x
                    && a.rotation.y == b.rotation.y
                    && a.rotation.z == b.rotation.z
                    && a.rotation.w == b.rotation.w
                    && a.velocity.x == b.velocity.x
                    && a.velocity.y == b.velocity.y
                    && a.velocity.z == b.velocity.z
                    && a.bodyName == b.bodyName
                    && a.funds == b.funds
                    && a.science == b.science
                    && a.reputation == b.reputation;
            }
        }

        #endregion

        #region TrajectoryMath

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns true when velocity direction changes")]
        public void ShouldRecordPointDetectsDirectionChange()
        {
            var vel1 = new Vector3(100, 0, 0);
            var vel2 = new Vector3(0, 100, 0); // 90 degree change
            bool result = TrajectoryMath.ShouldRecordPoint(vel2, vel1, 10.0, 9.5, 0f, 3f, 2f, 5f);
            InGameAssert.IsTrue(result, "Should record when velocity direction changes 90 degrees");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns true when max interval exceeded")]
        public void ShouldRecordPointRespectsMaxInterval()
        {
            var vel = new Vector3(100, 0, 0);
            bool result = TrajectoryMath.ShouldRecordPoint(vel, vel, 20.0, 16.0, 0f, 3f, 2f, 5f);
            InGameAssert.IsTrue(result, "Should record when interval > maxSampleInterval");
        }

        [InGameTest(Category = "TrajectoryMath", Description = "ShouldRecordPoint returns false when nothing changed")]
        public void ShouldRecordPointReturnsFalseWhenStable()
        {
            var vel = new Vector3(100, 0, 0);
            bool result = TrajectoryMath.ShouldRecordPoint(vel, vel, 10.1, 10.0, 0f, 3f, 2f, 5f);
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

        [InGameTest(Category = "TrajectoryMath",
            Description = "Live sampling density preset caps EVA-jitter sample rate (bug #256 regression guard)")]
        public void MinSampleIntervalCapsEvaJitter()
        {
            // Validates that the live game-loaded sampling density preset produces
            // a sane non-zero minSampleInterval AND that ShouldRecordPoint with that
            // value caps the simulated jitter pattern at the rate the floor allows.
            // Regression guard for bug #256.
            //
            // Bounds are computed dynamically from the live minInterval so the test passes
            // for any valid density level (Low/Medium/High), not just the default.
            float minInterval = ParsekSettings.Current?.minSampleInterval
                ?? ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
            InGameAssert.IsGreaterThan(minInterval, 0.0f,
                "minSampleInterval must be > 0 in the live game (bug #256 floor would be disabled)");
            InGameAssert.IsLessThan((double)minInterval, 1.01,
                "minSampleInterval should be \u2264 1 second (anything larger conflicts with max-interval backstop)");

            float maxInterval = ParsekSettings.Current?.maxSampleInterval
                ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float velDirThreshold = ParsekSettings.Current?.velocityDirThreshold
                ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speedThreshold = (ParsekSettings.Current?.speedChangeThreshold
                ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;

            // Simulate 50 frames at 50 Hz physics (~1 s) with a velocity vector that
            // rotates 2.86°/frame around the up axis. That's parity-independent of
            // commit timing — every frame after the floor unblocks defeats the 2°
            // direction gate, isolating the floor as the only thing capping the rate.
            const int frameCount = 50;
            const double frameDelta = 0.02; // 50 Hz
            double lastRecordedUT = -1;
            Vector3 lastRecordedVelocity = Vector3.zero;
            int commits = 0;
            for (int i = 0; i < frameCount; i++)
            {
                double ut = i * frameDelta;
                // 0.05 rad/frame ≈ 2.86°/frame — well above the 2° direction gate
                float angle = i * 0.05f;
                var vel = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

                bool record = TrajectoryMath.ShouldRecordPoint(
                    vel, lastRecordedVelocity, ut, lastRecordedUT,
                    minInterval, maxInterval, velDirThreshold, speedThreshold);

                if (record)
                {
                    commits++;
                    lastRecordedUT = ut;
                    lastRecordedVelocity = vel;
                }
            }

            // Expected commit count: 1 (first-point exception) + ⌊(frameCount - 1) / floorFrames⌋
            // where floorFrames = max(1, ⌈minInterval / frameDelta⌉).
            // ±1 tolerance for sub-frame boundary timing.
            int floorFrames = System.Math.Max(1,
                (int)System.Math.Ceiling(minInterval / frameDelta));
            int expectedCommits = 1 + (frameCount - 1) / floorFrames;
            const int tolerance = 1;
            int upperBound = expectedCommits + tolerance;
            int lowerBound = System.Math.Max(0, expectedCommits - tolerance);

            InGameAssert.IsLessThan((double)commits, (double)(upperBound + 1),
                $"Floor should cap commits ≤ {upperBound} over {frameCount} frames; got {commits} " +
                $"(minInterval={minInterval:F2}s, floorFrames={floorFrames}, expected≈{expectedCommits})");
            InGameAssert.IsGreaterThan((double)commits, (double)(lowerBound - 1),
                $"Floor should still allow ≥ {lowerBound} commits over {frameCount} frames; got {commits} " +
                $"(minInterval={minInterval:F2}s, floorFrames={floorFrames}, expected≈{expectedCommits})");

            ParsekLog.Verbose("TestRunner",
                $"MinSampleIntervalCapsEvaJitter: live minInterval={minInterval:F2}s ({floorFrames} frames) " +
                $"produced {commits} commits over {frameCount} frames (expected≈{expectedCommits})");
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

        [InGameTest(Category = "Settings", Scene = GameScenes.TRACKSTATION,
            Description = "#388 — flipping showGhostsInTrackingStation removes and recreates ghost ProtoVessels")]
        public void ShowGhostsInTrackingStation_FlipRemovesAndRecreates()
        {
            if (ParsekSettings.Current == null)
            {
                ParsekLog.Warn("TestRunner",
                    "ShowGhostsInTrackingStation_FlipRemovesAndRecreates: ParsekSettings.Current is null — skipping");
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
            {
                ParsekLog.Info("TestRunner",
                    "ShowGhostsInTrackingStation_FlipRemovesAndRecreates: no committed recordings — skipping (test needs at least one orbital ghost to be meaningful)");
                return;
            }

            bool original = ParsekSettings.Current.showGhostsInTrackingStation;

            try
            {
                // Ensure we start in the "visible" state for the off-flip leg and
                // rebuild ghosts so we have a non-zero baseline. Without this the
                // test would pass vacuously against a save with no orbital ghosts.
                ParsekSettings.Current.showGhostsInTrackingStation = true;
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();
                int baselineCount = GhostMapPresence.ghostMapVesselPids.Count;
                if (baselineCount == 0)
                {
                    ParsekLog.Info("TestRunner",
                        $"ShowGhostsInTrackingStation_FlipRemovesAndRecreates: " +
                        $"baselineCount=0 from {committed.Count} recordings (none qualify for TS ghost) — skipping");
                    return;
                }

                // Flip off — short-circuits in CreateGhost/UpdateLifecycle stop
                // new creation; manually call RemoveAllGhostVessels to simulate
                // the ParsekTrackingStation.Update force-tick behavior without
                // depending on the MonoBehaviour timing window.
                ParsekSettings.Current.showGhostsInTrackingStation = false;
                GhostMapPresence.RemoveAllGhostVessels("ingame-test-flip-off");
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();
                int offCount = GhostMapPresence.ghostMapVesselPids.Count;
                InGameAssert.IsTrue(offCount == 0,
                    $"Expected 0 ghost vessels with flag=off, got {offCount}");

                // Flip back on — UpdateLifecycle Phase 2 must rebuild the
                // same set of eligible ghosts.
                ParsekSettings.Current.showGhostsInTrackingStation = true;
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();
                int onCount = GhostMapPresence.ghostMapVesselPids.Count;
                InGameAssert.IsTrue(onCount == baselineCount,
                    $"Expected {baselineCount} ghost vessels after flipping flag back on, got {onCount}");

                ParsekLog.Info("TestRunner",
                    $"ShowGhostsInTrackingStation flip: baseline={baselineCount} offCount={offCount} onCount={onCount}");
            }
            finally
            {
                // Restore user's original setting regardless of test outcome.
                // If the original was false, the "flip on" leg of the test
                // recreated ghost ProtoVessels that we have to drain manually —
                // UpdateTrackingStationGhostLifecycle short-circuits when the
                // flag is false and won't remove them on its own, so without
                // this RemoveAll the TS would be left showing ghosts even
                // though the user had them disabled.
                ParsekSettings.Current.showGhostsInTrackingStation = original;
                if (!original)
                    GhostMapPresence.RemoveAllGhostVessels("ingame-test-restore-disabled");
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();
            }
        }

        #endregion

        #region MapView icons (#387)

        // Verify that MapMarkerRenderer's per-type icon entries match the live
        // MapNode.iconSprites array for every vessel type in
        // StockIconIndexByVesselType. Regressions here would mean ghost icons
        // show a different vessel type's symbol (the symptom users reported
        // before #387) OR — for the multi-atlas vessel types added by the
        // #387 follow-up — fall back to the diamond instead of the stock icon.
        [InGameTest(Category = "MapView",
            Description = "MapMarkerRenderer per-type atlas+UV entries match live MapNode.iconSprites (#387 + multi-atlas follow-up)")]
        public void MapMarkerIconsMatchStockAtlas()
        {
            InGameAssert.IsNotNull(MapView.fetch,
                "MapView.fetch should exist — test requires flight or tracking station scene");

            // Force a fresh init against whatever atlas this scene resolves.
            MapMarkerRenderer.ResetForSceneChange();

            var prefab = MapView.UINodePrefab;
            InGameAssert.IsNotNull(prefab, "MapView.UINodePrefab should not be null");

            var fi = typeof(KSP.UI.Screens.Mapview.MapNode).GetField("iconSprites",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            InGameAssert.IsNotNull(fi, "MapNode.iconSprites field should be reflectable");

            var sprites = fi.GetValue(prefab) as Sprite[];
            InGameAssert.IsNotNull(sprites, "iconSprites should not be null");
            InGameAssert.IsTrue(sprites.Length > 0, "iconSprites should not be empty");

            // Run the atlas reflection/init without GUI context
            // (InGameTestRunner coroutines don't always execute during OnGUI).
            MapMarkerRenderer.ForceInitForTesting();

            var entries = MapMarkerRenderer.VesselIconEntriesForTesting;
            InGameAssert.IsNotNull(entries, "MapMarkerRenderer icon entry dict should be built");

            int verified = 0, missing = 0;
            foreach (var kv in MapMarkerRenderer.StockIconIndexByVesselType)
            {
                int idx = kv.Value;
                // Under the per-type-atlas model (#387 follow-up) only
                // structural gaps in the stock atlas should skip an entry.
                if (idx < 0 || idx >= sprites.Length || sprites[idx] == null
                    || sprites[idx].texture == null)
                {
                    missing++;
                    ParsekLog.Verbose("TestRunner",
                        $"MapView icon test: skipping {kv.Key} (idx={idx}, sprite or texture missing)");
                    continue;
                }

                Texture2D spriteTex = sprites[idx].texture;
                Rect expectedRect = sprites[idx].textureRect;
                Rect expectedUv = new Rect(
                    expectedRect.x / spriteTex.width, expectedRect.y / spriteTex.height,
                    expectedRect.width / spriteTex.width, expectedRect.height / spriteTex.height);

                InGameAssert.IsTrue(entries.TryGetValue(kv.Key, out MapMarkerRenderer.VesselIconEntry actual),
                    $"Icon dict missing entry for {kv.Key}");

                // Each type must carry its OWN texture reference — the multi-atlas
                // vessel types (DeployedScienceController, DeployedGroundPart)
                // used to be skipped because the renderer forced a single atlas.
                InGameAssert.IsTrue(object.ReferenceEquals(actual.Atlas, spriteTex),
                    $"Atlas for {kv.Key} should match sprites[{idx}].texture (got name='{(actual.Atlas != null ? actual.Atlas.name : "null")}' expected='{spriteTex.name}')");

                InGameAssert.ApproxEqual(expectedUv.x, actual.UV.x);
                InGameAssert.ApproxEqual(expectedUv.y, actual.UV.y);
                InGameAssert.ApproxEqual(expectedUv.width, actual.UV.width);
                InGameAssert.ApproxEqual(expectedUv.height, actual.UV.height);

                verified++;
                ParsekLog.Verbose("TestRunner",
                    $"MapView icon {kv.Key} idx={idx} tex={spriteTex.name} UV=({actual.UV.x:F3},{actual.UV.y:F3},{actual.UV.width:F3},{actual.UV.height:F3}) OK");
            }

            InGameAssert.IsTrue(verified > 0,
                $"Expected at least one matching icon entry; verified={verified} missing={missing}");
            ParsekLog.Info("TestRunner",
                $"MapMarkerIconsMatchStockAtlas: verified={verified} missing={missing} total={MapMarkerRenderer.StockIconIndexByVesselType.Count}");
        }

        #endregion
    }

    /// <summary>
    /// Tests that require active ghost playback to verify visual and positioning systems.
    /// </summary>
    public class GhostPlaybackTests
    {
        private readonly InGameTestRunner runner;
        public GhostPlaybackTests(InGameTestRunner runner) { this.runner = runner; }

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

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Ghost horizonProxy exists as child of cameraPivot")]
        public void HorizonProxyTransformExists()
        {
            // Build a minimal ghost from a committed recording to verify
            // the horizonProxy transform is created alongside cameraPivot
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0)
            {
                ParsekLog.Info("TestRunner", "No committed recordings — skipping HorizonProxyTransformExists");
                return;
            }

            var traj = committed[0] as IPlaybackTrajectory;
            var engine = ParsekFlight.Instance?.Engine;
            InGameAssert.IsNotNull(engine, "GhostPlaybackEngine should be available in flight");

            // Check if any ghost state has horizonProxy
            bool anyGhostChecked = false;
            foreach (var kvp in engine.ghostStates)
            {
                var state = kvp.Value;
                if (state?.cameraPivot == null) continue;
                anyGhostChecked = true;

                InGameAssert.IsNotNull(state.horizonProxy,
                    $"Ghost #{kvp.Key} should have horizonProxy");
                InGameAssert.IsTrue(state.horizonProxy.parent == state.cameraPivot,
                    $"Ghost #{kvp.Key} horizonProxy should be child of cameraPivot");
                InGameAssert.IsTrue(state.horizonProxy.localPosition == Vector3.zero,
                    $"Ghost #{kvp.Key} horizonProxy should be at local origin");
            }

            if (!anyGhostChecked)
                ParsekLog.Info("TestRunner", "No active ghosts with cameraPivot — structural check deferred");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "ComputeHorizonRotation produces valid rotation near Kerbin surface")]
        public void HorizonRotationNearSurface()
        {
            // Use a realistic Kerbin surface scenario
            Vector3 up = Vector3.up;
            Vector3 velocity = new Vector3(200, 10, 0); // moving east, slight ascent

            var (rotation, forward) = WatchModeController.ComputeHorizonRotation(up, velocity, Vector3.forward);

            // Forward should be on horizon plane
            InGameAssert.IsTrue(Mathf.Abs(forward.y) < 0.01f,
                $"Forward should be on horizon, got Y={forward.y}");

            // Rotation's up should match input up
            Vector3 rotUp = rotation * Vector3.up;
            float upDot = Vector3.Dot(rotUp, up);
            InGameAssert.IsTrue(upDot > 0.99f,
                $"Rotation up should match radial, got dot={upDot}");

            // Rotation's forward should match computed forward
            Vector3 rotFwd = rotation * Vector3.forward;
            float fwdDot = Vector3.Dot(rotFwd, forward);
            InGameAssert.IsTrue(fwdDot > 0.99f,
                $"Rotation forward should match horizon forward, got dot={fwdDot}");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "CompensateCameraAngles preserves world direction across rotation change")]
        public void CameraAngleCompensationPreservesDirection()
        {
            Quaternion oldRot = Quaternion.identity;
            Quaternion newRot = Quaternion.Euler(0, 90, 0);
            float origPitch = 15f;
            float origHdg = 30f;

            // World direction from old frame
            float pRad = origPitch * Mathf.Deg2Rad;
            float hRad = origHdg * Mathf.Deg2Rad;
            Vector3 localDir = new Vector3(
                Mathf.Sin(hRad) * Mathf.Cos(pRad),
                Mathf.Sin(pRad),
                Mathf.Cos(hRad) * Mathf.Cos(pRad));
            Vector3 worldDir = oldRot * localDir;

            // Compensate
            var (newPitch, newHdg) = WatchModeController.CompensateCameraAngles(
                oldRot, newRot, origPitch, origHdg);

            // World direction from new frame
            float npRad = newPitch * Mathf.Deg2Rad;
            float nhRad = newHdg * Mathf.Deg2Rad;
            Vector3 newLocalDir = new Vector3(
                Mathf.Sin(nhRad) * Mathf.Cos(npRad),
                Mathf.Sin(npRad),
                Mathf.Cos(nhRad) * Mathf.Cos(npRad));
            Vector3 newWorldDir = newRot * newLocalDir;

            float dot = Vector3.Dot(worldDir.normalized, newWorldDir.normalized);
            InGameAssert.IsTrue(dot > 0.99f,
                $"World direction should be preserved, got dot={dot}");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Manual watch transfer compensation preserves world direction across target change")]
        public void ManualWatchTransferCompensationPreservesDirection()
        {
            var state = new WatchCameraTransitionState
            {
                Pitch = 15f,
                Heading = 30f,
                HasTargetRotation = true,
                TargetRotation = Quaternion.identity
            };
            Quaternion newRot = Quaternion.Euler(0, 90, 0);

            float pRad = state.Pitch * Mathf.Deg2Rad;
            float hRad = state.Heading * Mathf.Deg2Rad;
            Vector3 localDir = new Vector3(
                Mathf.Sin(hRad) * Mathf.Cos(pRad),
                Mathf.Sin(pRad),
                Mathf.Cos(hRad) * Mathf.Cos(pRad));
            Vector3 oldWorldDir = state.TargetRotation * localDir;

            var (newPitch, newHdg) = WatchModeController.CompensateTransferredWatchAngles(
                state, newRot);

            float npRad = newPitch * Mathf.Deg2Rad;
            float nhRad = newHdg * Mathf.Deg2Rad;
            Vector3 newLocalDir = new Vector3(
                Mathf.Sin(nhRad) * Mathf.Cos(npRad),
                Mathf.Sin(npRad),
                Mathf.Cos(nhRad) * Mathf.Cos(npRad));
            Vector3 newWorldDir = newRot * newLocalDir;

            float dot = Vector3.Dot(oldWorldDir.normalized, newWorldDir.normalized);
            InGameAssert.IsTrue(dot > 0.99f,
                $"Transferred watch direction should be preserved, got dot={dot}");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "SpawnGhost primes fresh ghost to current playback UT (in-game replacement for xUnit SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT)")]
        public IEnumerator SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT_InGame()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("no ParsekFlight");

            var engine = flight.Engine;
            if (engine == null)
                InGameAssert.Skip("no GhostPlaybackEngine");

            var committed = RecordingStore.CommittedRecordings;
            Recording rec = null;
            int recordingIndex = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                var candidate = committed[i];
                if (candidate != null
                    && candidate.Points != null
                    && candidate.Points.Count >= 2
                    && !string.IsNullOrEmpty(candidate.Points[0].bodyName))
                {
                    rec = candidate;
                    recordingIndex = i;
                    break;
                }
            }

            if (rec == null)
                InGameAssert.Skip("needs a committed recording with a non-empty trajectory");

            int sentinelIndex = committed.Count + 1000;
            if (engine.ghostStates.ContainsKey(sentinelIndex))
                InGameAssert.Skip("sentinel index collision");

            double primingUT = rec.Points[rec.Points.Count / 2].ut;

            GhostPlaybackState state = null;
            try
            {
                engine.SpawnGhost(sentinelIndex, rec as IPlaybackTrajectory, primingUT);

                bool found = engine.ghostStates.TryGetValue(sentinelIndex, out state);
                InGameAssert.IsTrue(found,
                    $"ghostStates should contain sentinel index {sentinelIndex} after SpawnGhost");
                InGameAssert.IsNotNull(state, "state should not be null after SpawnGhost");
                InGameAssert.IsNotNull(state.ghost, "state.ghost should not be null after SpawnGhost");
                InGameAssert.IsTrue(state.deferVisibilityUntilPlaybackSync,
                    "fresh spawn should set deferVisibilityUntilPlaybackSync=true");
                InGameAssert.IsFalse(state.ghost.activeSelf,
                    "fresh spawn should leave ghost GameObject inactive until playback sync");
                InGameAssert.IsTrue(!string.IsNullOrEmpty(state.lastInterpolatedBodyName),
                    "priming pass should have populated lastInterpolatedBodyName");
                InGameAssert.IsNotNull(state.cameraPivot, "state.cameraPivot should be created");
                InGameAssert.IsNotNull(state.horizonProxy, "state.horizonProxy should be created");

                float ghostOriginDist = Vector3.Distance(state.ghost.transform.position, Vector3.zero);
                InGameAssert.IsTrue(ghostOriginDist > 1f,
                    $"priming should move ghost away from origin, got distance={ghostOriginDist:F2}");

                ParsekLog.Verbose("TestRunner",
                    $"SpawnGhost priming in-game: recordingIndex={recordingIndex} " +
                    $"vessel=\"{rec.VesselName}\" sentinelIndex={sentinelIndex} " +
                    $"primingUT={primingUT:F2} body=\"{state.lastInterpolatedBodyName}\" " +
                    $"altitude={state.lastInterpolatedAltitude:F1} " +
                    $"ghostPosMag={ghostOriginDist:F2} " +
                    $"deferVis={state.deferVisibilityUntilPlaybackSync} " +
                    $"activeSelf={state.ghost.activeSelf}");
            }
            finally
            {
                engine.ghostStates.Remove(sentinelIndex);
                if (state != null && state.ghost != null)
                    runner.TrackForCleanup(state.ghost);
            }

            yield break;
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "EnterWatchMode on a same-body ghost applies canonical fresh-entry angles, not the active vessel's camera state (PR #288 regression)")]
        public IEnumerator WatchEntry_SameBody_PreservesFreshEntryAngles()
        {
            // --- Preconditions ---
            if (ParsekFlight.Instance == null)
                InGameAssert.Skip("no ParsekFlight.Instance");
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("no ActiveVessel");
            if (FlightCamera.fetch == null)
                InGameAssert.Skip("no FlightCamera");

            var engine = ParsekFlight.Instance.Engine;
            if (engine == null)
                InGameAssert.Skip("no GhostPlaybackEngine");

            var committed = RecordingStore.CommittedRecordings;
            string activeBodyName = FlightGlobals.ActiveVessel.mainBody.name;
            float cutoffKm = DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm(ParsekSettings.Current);

            // --- Find a watchable same-body ghost ---
            int index = -1;
            GhostPlaybackState state = null;
            foreach (var kvp in engine.ghostStates)
            {
                var gs = kvp.Value;
                if (gs == null) continue;
                if (gs.lastInterpolatedBodyName != activeBodyName) continue;
                if (gs.ghost == null) continue;
                if (kvp.Key >= committed.Count) continue;
                // Require at least one update frame so lastDistance is real — zero means
                // the ghost was just spawned and the range check would be vacuously true.
                if (gs.lastDistance <= 0.0) continue;
                if (!GhostPlaybackLogic.IsWithinWatchRange(gs.lastDistance, cutoffKm)) continue;

                index = kvp.Key;
                state = gs;
                break;
            }

            if (index < 0)
                InGameAssert.Skip("no same-body ghost available for watch-entry regression");

            // --- Capture pre-entry camera forward direction (for 180-flip safety net) ---
            Vector3 cameraForwardBefore = FlightCamera.fetch.transform.forward;

            // --- Enter watch mode ---
            ParsekFlight.Instance.EnterWatchMode(index);
            yield return null; // let camera apply

            try
            {
                // --- Assert watch mode entered ---
                InGameAssert.IsTrue(ParsekFlight.Instance.IsWatchingGhost, "should be in watch mode");
                InGameAssert.AreEqual(index, ParsekFlight.Instance.WatchedRecordingIndex, "watching wrong index");

                // --- Core assertions: tight angle check on canonical defaults ---
                float expectedPitchRad = WatchModeController.DefaultWatchEntryPitch * Mathf.Deg2Rad;
                float actualPitchRad = FlightCamera.fetch.camPitch;
                float actualHdgRad = FlightCamera.fetch.camHdg;
                float pitchDeg = Mathf.Abs(actualPitchRad - expectedPitchRad) * Mathf.Rad2Deg;
                float hdgDeg = Mathf.Abs(Mathf.DeltaAngle(actualHdgRad * Mathf.Rad2Deg, WatchModeController.DefaultWatchEntryHeading));
                InGameAssert.IsTrue(pitchDeg < 1f,
                    $"pitch should be near {WatchModeController.DefaultWatchEntryPitch} deg, got delta={pitchDeg:F2} deg");
                InGameAssert.IsTrue(hdgDeg < 1f,
                    $"heading should be near {WatchModeController.DefaultWatchEntryHeading} deg, got delta={hdgDeg:F2} deg");

                // --- Safety-net assertion: no 180-degree camera flip ---
                Vector3 cameraForwardAfter = FlightCamera.fetch.transform.forward;
                float worldDot = Vector3.Dot(cameraForwardBefore, cameraForwardAfter);
                InGameAssert.IsTrue(worldDot > -0.5f,
                    $"camera should not flip ~180 degrees on watch entry, dot={worldDot:F3}");

                // --- Verbose diagnostic log ---
                ParsekLog.Verbose("TestRunner",
                    $"WatchEntry_SameBody: index={index} body={state.lastInterpolatedBodyName} " +
                    $"camPitchDeg={actualPitchRad * Mathf.Rad2Deg:F2} camHdgDeg={actualHdgRad * Mathf.Rad2Deg:F2} " +
                    $"expectedPitch={WatchModeController.DefaultWatchEntryPitch:F1} expectedHdg={WatchModeController.DefaultWatchEntryHeading:F1} " +
                    $"worldDot={worldDot:F3}");
            }
            finally
            {
                ParsekFlight.Instance.ExitWatchMode();
            }

            InGameAssert.IsFalse(ParsekFlight.Instance.IsWatchingGhost, "should have exited watch mode");
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
                    // Hyperbolic orbits (eccentricity > 1) legitimately have negative SMA
                    if (seg.eccentricity <= 1.0)
                        InGameAssert.IsGreaterThan(seg.semiMajorAxis, 0,
                            $"Orbit segment for '{seg.bodyName}' has non-positive SMA={seg.semiMajorAxis} (ecc={seg.eccentricity:F4})");
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
            // Don't fail on missing here — saves can still contain partially-collected or legacy data.
        }

        [InGameTest(Category = "SaveLoad",
            Description = "Current-format committed recordings probe as BinaryV3 .prec sidecars")]
        public void CurrentFormatTrajectorySidecarsProbeAsBinary()
        {
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
            {
                InGameAssert.Skip("no SaveFolder set");
                return;
            }

            int checkedCount = 0, skippedRoots = 0;

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                if (rec == null || rec.RecordingFormatVersion < 2 || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                // Recordings with no trajectory points have no .prec sidecar on disk — structural
                // tree roots in practice, but the predicate is intentionally structural (#420).
                if (rec.Points == null || rec.Points.Count == 0)
                {
                    skippedRoots++;
                    continue;
                }

                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    Path.Combine("Parsek", "Recordings", rec.RecordingId + ".prec"));
                InGameAssert.IsTrue(!string.IsNullOrEmpty(precPath) && File.Exists(precPath),
                    $"Current-format recording '{rec.RecordingId}' is missing its .prec sidecar");

                TrajectorySidecarProbe probe;
                InGameAssert.IsTrue(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe),
                    $"Could not probe .prec sidecar for current-format recording '{rec.RecordingId}'");
                InGameAssert.AreEqual(TrajectorySidecarEncoding.BinaryV3, probe.Encoding,
                    $"Current-format recording '{rec.RecordingId}' should use BinaryV3 sidecar encoding");
                InGameAssert.AreEqual(rec.RecordingFormatVersion, probe.FormatVersion,
                    $"Current-format recording '{rec.RecordingId}' should keep its on-disk format version");
                checkedCount++;
            }

            if (checkedCount == 0)
            {
                ParsekLog.Verbose("TestRunner",
                    $"Binary sidecar check: no current-format trajectory recordings in this save ({skippedRoots} tree root(s) skipped)");
                InGameAssert.Skip("no current-format committed recordings in this save");
                return;
            }

            ParsekLog.Verbose("TestRunner",
                $"Binary sidecar check: verified {checkedCount} current-format recording(s), {skippedRoots} tree root(s) skipped");
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

        [InGameTest(Category = "CrewReservation", Scene = GameScenes.FLIGHT,
            Description = "Bug #277: Part.AddCrewmember works on a free seat (validates orphan placement live API)")]
        public void Bug277_AddCrewmemberOnFreeSeat_Works()
        {
            // The bug #277 fix relies on Part.AddCrewmember(ProtoCrewMember) (the
            // non-indexed overload) successfully placing a kerbal into a free seat
            // of a part. This test exercises that exact API path on a live vessel
            // and rolls back to leave state untouched.

            var av = FlightGlobals.ActiveVessel;
            if (av == null)
            {
                InGameAssert.Skip("No active vessel");
                return;
            }

            // Find a part with at least one free seat.
            Part target = null;
            for (int i = 0; i < av.parts.Count; i++)
            {
                var p = av.parts[i];
                if (p != null && p.CrewCapacity > 0 && p.protoModuleCrew.Count < p.CrewCapacity)
                {
                    target = p;
                    break;
                }
            }
            if (target == null)
            {
                InGameAssert.Skip("Active vessel has no part with a free crew seat");
                return;
            }

            // Find a free Available kerbal in the roster.
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster");
                return;
            }
            ProtoCrewMember candidate = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available
                    && pcm.type == ProtoCrewMember.KerbalType.Crew)
                {
                    candidate = pcm;
                    break;
                }
            }
            if (candidate == null)
            {
                InGameAssert.Skip("No Available crew kerbal in roster");
                return;
            }

            int beforeCount = target.protoModuleCrew.Count;
            int beforeCapacityRemaining = target.CrewCapacity - beforeCount;

            target.AddCrewmember(candidate);
            try
            {
                int afterCount = target.protoModuleCrew.Count;
                InGameAssert.AreEqual(beforeCount + 1, afterCount,
                    $"AddCrewmember should have added 1 to part crew count " +
                    $"(before={beforeCount}, after={afterCount}, capacity={target.CrewCapacity})");
                InGameAssert.IsTrue(target.protoModuleCrew.Contains(candidate),
                    $"After AddCrewmember, part should contain '{candidate.name}' in protoModuleCrew");
                ParsekLog.Info("TestRunner",
                    $"Bug277 live API check: AddCrewmember placed '{candidate.name}' in '{target.partInfo.title}' " +
                    $"(remaining capacity was {beforeCapacityRemaining}, now {target.CrewCapacity - afterCount})");
            }
            finally
            {
                // Roll back: remove the test kerbal so we don't pollute the vessel state.
                target.RemoveCrewmember(candidate);
                InGameAssert.AreEqual(beforeCount, target.protoModuleCrew.Count,
                    $"Rollback failed: expected count {beforeCount}, got {target.protoModuleCrew.Count}");
            }
        }

        [InGameTest(Category = "CrewReservation", Scene = GameScenes.FLIGHT,
            Description = "Bug #277: PlaceOrphanedReplacements end-to-end places stand-in from synthetic snapshot (orphan-without-real-original variant)")]
        public void Bug277_PlaceOrphanedReplacements_PlacesStandinFromSnapshot()
        {
            // End-to-end integration test for the bug #277 orphan placement pass.
            // Builds a synthetic snapshot referencing a real part on the active
            // vessel, registers a fake reservation, calls PlaceOrphanedReplacements
            // directly (skipping the SpawnCrew + RemoveReservedEvaVessels side
            // effects of full SwapReservedCrewInFlight), asserts the stand-in
            // landed in the right part, then rolls everything back.
            //
            // Scenario covered: orphan whose original kerbal does NOT exist in the
            // roster at all. The actual bug-#277 in-game scenario is "orphan whose
            // original is alive on a separate EVA vessel" — that's hard to set up
            // safely from a runtime test. The code path under test is identical
            // (snapshot scan → seat lookup → live part match → AddCrewmember); only
            // the original-kerbal-existence side condition differs. Pass 1 doesn't
            // see the original in either scenario, so Pass 2 is what runs.

            var av = FlightGlobals.ActiveVessel;
            if (av == null)
            {
                InGameAssert.Skip("No active vessel");
                return;
            }

            // Find a part with a free seat to use as the placement target.
            Part target = null;
            for (int i = 0; i < av.parts.Count; i++)
            {
                var p = av.parts[i];
                if (p != null && p.CrewCapacity > 0 && p.protoModuleCrew.Count < p.CrewCapacity
                    && p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.name))
                {
                    target = p;
                    break;
                }
            }
            if (target == null)
            {
                InGameAssert.Skip("Active vessel has no part with a free crew seat");
                return;
            }

            // Find a free Available Crew kerbal not currently on the active vessel.
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster");
                return;
            }
            var activeCrewNames = new HashSet<string>();
            for (int p = 0; p < av.parts.Count; p++)
            {
                var crew = av.parts[p].protoModuleCrew;
                for (int c = 0; c < crew.Count; c++)
                {
                    if (crew[c] != null && !string.IsNullOrEmpty(crew[c].name))
                        activeCrewNames.Add(crew[c].name);
                }
            }
            ProtoCrewMember standIn = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available
                    && pcm.type == ProtoCrewMember.KerbalType.Crew
                    && !activeCrewNames.Contains(pcm.name))
                {
                    standIn = pcm;
                    break;
                }
            }
            if (standIn == null)
            {
                InGameAssert.Skip("No Available crew kerbal in roster (not already on active vessel)");
                return;
            }

            // Pick a fake "original" name that does not exist in the roster.
            string fakeOriginal = "Bug277Test_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + " Kerman";

            // Save state for rollback.
            var savedReplacements = new Dictionary<string, string>();
            foreach (var kvp in CrewReservationManager.CrewReplacements)
                savedReplacements[kvp.Key] = kvp.Value;
            int savedCommittedCount = RecordingStore.CommittedRecordings.Count;
            int beforeCrewCount = target.protoModuleCrew.Count;

            // Hoisted so the finally block can remove it from CommittedRecordings.
            Recording syntheticRecording = null;
            bool addedToCommitted = false;
            bool placedCrew = false;
            try
            {
                // Test isolation (PR #175 follow-up review): clear all real
                // reservations FIRST, inside the try, so any subsequent throw is
                // still cleaned up by finally. PlaceOrphanedReplacements iterates
                // the entire crewReplacements dict, so any pre-existing real
                // reservation with an unplaced orphan (which is exactly the
                // bug-#277 scenario this test exists to validate) could leak a
                // real stand-in into the active vessel as a side effect.
                CrewReservationManager.ClearReplacementsInternal();

                // Build a synthetic snapshot whose PART node references the live
                // target part by pid + name and lists the fake original.
                var snapshot = new ConfigNode("VESSEL");
                var partNode = snapshot.AddNode("PART");
                partNode.AddValue("name", target.partInfo.name);
                partNode.AddValue("pid", target.persistentId.ToString());
                partNode.AddValue("crew", fakeOriginal);

                syntheticRecording = new Recording
                {
                    RecordingId = "test-orphan-277-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
                    VesselName = "Bug277TestVessel",
                    GhostVisualSnapshot = snapshot
                };

                // Inject synthetic recording so the snapshot scan finds the fake original.
                RecordingStore.AddCommittedInternal(syntheticRecording);
                addedToCommitted = true;

                // Register ONLY the fake reservation. The dict was cleared above,
                // so PlaceOrphanedReplacements will iterate exactly one entry.
                CrewReservationManager.SetReplacement(fakeOriginal, standIn.name);

                // Run just the orphan-placement pass with an empty swappedOriginals
                // set. This avoids triggering RemoveReservedEvaVessels and SpawnCrew
                // side effects from full SwapReservedCrewInFlight.
                var swappedOriginals = new HashSet<string>();
                int placed = CrewReservationManager.PlaceOrphanedReplacements(roster, swappedOriginals);

                // Validate placement.
                InGameAssert.IsGreaterThan(placed, 0,
                    $"PlaceOrphanedReplacements should have placed at least 1 stand-in (placed={placed})");
                placedCrew = target.protoModuleCrew.Contains(standIn);
                InGameAssert.IsTrue(placedCrew,
                    $"Stand-in '{standIn.name}' should be in target part '{target.partInfo.title}' " +
                    $"after orphan placement");
                InGameAssert.IsTrue(swappedOriginals.Contains(fakeOriginal),
                    $"Fake original '{fakeOriginal}' should be in swappedOriginals after placement");
                InGameAssert.AreEqual(beforeCrewCount + 1, target.protoModuleCrew.Count,
                    $"Target part crew count should have increased by 1 " +
                    $"(before={beforeCrewCount}, after={target.protoModuleCrew.Count})");

                ParsekLog.Info("TestRunner",
                    $"Bug277 end-to-end: orphan placement placed '{standIn.name}' " +
                    $"in '{target.partInfo.title}' from synthetic snapshot");
            }
            finally
            {
                // Roll back placement first so the part returns to its original state.
                if (placedCrew && target.protoModuleCrew.Contains(standIn))
                    target.RemoveCrewmember(standIn);

                // Restore replacements: clear (drops the fake too) and replay the
                // saved set. Always run regardless of where the test failed —
                // ClearReplacementsInternal at the top means crewReplacements is in
                // an unknown state if we don't restore.
                CrewReservationManager.ClearReplacementsInternal();
                foreach (var kvp in savedReplacements)
                    CrewReservationManager.SetReplacement(kvp.Key, kvp.Value);

                // Remove the synthetic recording from the committed list.
                if (addedToCommitted)
                    RecordingStore.RemoveCommittedInternal(syntheticRecording);

                // Verify rollback.
                InGameAssert.AreEqual(beforeCrewCount, target.protoModuleCrew.Count,
                    $"Rollback failed: target part crew count not restored " +
                    $"(expected={beforeCrewCount}, actual={target.protoModuleCrew.Count})");
                InGameAssert.AreEqual(savedCommittedCount, RecordingStore.CommittedRecordings.Count,
                    $"Rollback failed: CommittedRecordings count not restored " +
                    $"(expected={savedCommittedCount}, actual={RecordingStore.CommittedRecordings.Count})");
                InGameAssert.AreEqual(savedReplacements.Count, CrewReservationManager.CrewReplacements.Count,
                    $"Rollback failed: crewReplacements count not restored " +
                    $"(expected={savedReplacements.Count}, actual={CrewReservationManager.CrewReplacements.Count})");
            }
        }

        [InGameTest(Category = "CrewReservation", Scene = GameScenes.FLIGHT,
            Description = "Bug #456: orphan placement name-hit fallback places stand-in when snapshot pid=100000 (synthetic showcase) doesn't match any live part pid")]
        public void Bug456_OrphanPlacement_NameHitFallback_PlacesStandin()
        {
            // End-to-end integration test for bug #456. The playtest symptom was:
            //   [CrewReservation] Orphan placement: no matching part with free seat
            //   in active vessel for 'Bill Kerman' → 'Urdun Kerman'
            //   (snapshot pid=100000 name='mk1pod.v2')
            //
            // The snapshot carries a SYNTHETIC pid (100000, the canonical first
            // AddPart-assigned value for showcase ghosts — see `.claude/CLAUDE.md`
            // "Ghost event ↔ snapshot PID" gotcha) so tier-1 pid match always
            // misses against a live vessel whose KSP-assigned part pid is a
            // different random value. This test builds that exact shape — a
            // snapshot with pid=100000 name=<live part name>, finds a free seat
            // on the live vessel, then verifies PlaceOrphanedReplacements
            // succeeds via the name-hit fallback tier and the summary log line
            // increments `nameHitFallbacks`.

            var av = FlightGlobals.ActiveVessel;
            if (av == null)
            {
                InGameAssert.Skip("No active vessel");
                return;
            }

            // Find a part with a free seat to use as the placement target.
            Part target = null;
            for (int i = 0; i < av.parts.Count; i++)
            {
                var p = av.parts[i];
                if (p != null && p.CrewCapacity > 0 && p.protoModuleCrew.Count < p.CrewCapacity
                    && p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.name))
                {
                    target = p;
                    break;
                }
            }
            if (target == null)
            {
                InGameAssert.Skip("Active vessel has no part with a free crew seat");
                return;
            }

            // Sanity: the synthetic pid we will stamp into the snapshot must NOT
            // coincide with the target part's live pid (otherwise the test would
            // exercise the pid-hit tier instead of the name-hit fallback).
            const uint SyntheticShowcasePid = 100000u;
            if (target.persistentId == SyntheticShowcasePid)
            {
                InGameAssert.Skip("Target part's live pid coincides with the synthetic test pid — cannot exercise name-hit fallback");
                return;
            }

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster");
                return;
            }
            var activeCrewNames = new HashSet<string>();
            for (int p = 0; p < av.parts.Count; p++)
            {
                var crew = av.parts[p].protoModuleCrew;
                for (int c = 0; c < crew.Count; c++)
                {
                    if (crew[c] != null && !string.IsNullOrEmpty(crew[c].name))
                        activeCrewNames.Add(crew[c].name);
                }
            }
            ProtoCrewMember standIn = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available
                    && pcm.type == ProtoCrewMember.KerbalType.Crew
                    && !activeCrewNames.Contains(pcm.name))
                {
                    standIn = pcm;
                    break;
                }
            }
            if (standIn == null)
            {
                InGameAssert.Skip("No Available crew kerbal in roster (not already on active vessel)");
                return;
            }

            string fakeOriginal = "Bug456Test_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + " Kerman";

            // Save state for rollback.
            var savedReplacements = new Dictionary<string, string>();
            foreach (var kvp in CrewReservationManager.CrewReplacements)
                savedReplacements[kvp.Key] = kvp.Value;
            int savedCommittedCount = RecordingStore.CommittedRecordings.Count;
            int beforeCrewCount = target.protoModuleCrew.Count;

            Recording syntheticRecording = null;
            bool addedToCommitted = false;
            bool placedCrew = false;

            // Capture log output so we can assert on the name-fallback INFO line
            // and the summary counter.
            var logLines = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                CrewReservationManager.ClearReplacementsInternal();

                // Build a snapshot that DELIBERATELY uses a pid that does NOT
                // match the live target — this forces tier-1 to miss and tier-2
                // (name-hit) to carry the placement. The part NAME must match
                // the live target's partInfo.name.
                var snapshot = new ConfigNode("VESSEL");
                var partNode = snapshot.AddNode("PART");
                partNode.AddValue("name", target.partInfo.name);
                partNode.AddValue("persistentId", SyntheticShowcasePid.ToString());
                partNode.AddValue("crew", fakeOriginal);

                syntheticRecording = new Recording
                {
                    RecordingId = "test-orphan-456-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
                    VesselName = "Bug456TestVessel",
                    GhostVisualSnapshot = snapshot
                };

                RecordingStore.AddCommittedInternal(syntheticRecording);
                addedToCommitted = true;

                CrewReservationManager.SetReplacement(fakeOriginal, standIn.name);

                var swappedOriginals = new HashSet<string>();
                int placed = CrewReservationManager.PlaceOrphanedReplacements(roster, swappedOriginals);

                InGameAssert.IsGreaterThan(placed, 0,
                    $"PlaceOrphanedReplacements should have placed at least 1 stand-in via name-fallback (placed={placed})");
                placedCrew = target.protoModuleCrew.Contains(standIn);
                InGameAssert.IsTrue(placedCrew,
                    $"Stand-in '{standIn.name}' should be in target part '{target.partInfo.title}' after name-hit fallback");
                InGameAssert.AreEqual(beforeCrewCount + 1, target.protoModuleCrew.Count,
                    $"Target part crew count should have increased by 1 " +
                    $"(before={beforeCrewCount}, after={target.protoModuleCrew.Count})");

                // Log-assertion: the dedicated "match=name-fallback" INFO line
                // must have fired so future playtests can grep for it.
                bool sawNameFallbackLog = false;
                foreach (var line in logLines)
                {
                    if (line.Contains("[CrewReservation]")
                        && line.Contains("match=name-fallback"))
                    {
                        sawNameFallbackLog = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawNameFallbackLog,
                    "Expected a [CrewReservation] INFO line with 'match=name-fallback' — name-hit fallback path did not log its success");

                // Log-assertion: the summary line must increment nameHitFallbacks.
                bool sawNameHitCounter = false;
                foreach (var line in logLines)
                {
                    if (line.Contains("[CrewReservation]")
                        && line.Contains("Orphan placement pass:")
                        && line.Contains("nameHitFallbacks=1"))
                    {
                        sawNameHitCounter = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawNameHitCounter,
                    "Expected summary log with 'nameHitFallbacks=1' — counter did not increment on successful fallback");

                ParsekLog.Info("TestRunner",
                    $"Bug456 end-to-end: name-hit fallback placed '{standIn.name}' " +
                    $"in '{target.partInfo.title}' from synthetic pid={SyntheticShowcasePid} snapshot");
            }
            finally
            {
                // Restore log sink first so cleanup doesn't pollute the captured lines.
                ParsekLog.TestSinkForTesting = priorSink;

                if (placedCrew && target.protoModuleCrew.Contains(standIn))
                    target.RemoveCrewmember(standIn);

                CrewReservationManager.ClearReplacementsInternal();
                foreach (var kvp in savedReplacements)
                    CrewReservationManager.SetReplacement(kvp.Key, kvp.Value);

                if (addedToCommitted)
                    RecordingStore.RemoveCommittedInternal(syntheticRecording);

                InGameAssert.AreEqual(beforeCrewCount, target.protoModuleCrew.Count,
                    $"Rollback failed: target part crew count not restored " +
                    $"(expected={beforeCrewCount}, actual={target.protoModuleCrew.Count})");
                InGameAssert.AreEqual(savedCommittedCount, RecordingStore.CommittedRecordings.Count,
                    $"Rollback failed: CommittedRecordings count not restored " +
                    $"(expected={savedCommittedCount}, actual={RecordingStore.CommittedRecordings.Count})");
                InGameAssert.AreEqual(savedReplacements.Count, CrewReservationManager.CrewReplacements.Count,
                    $"Rollback failed: crewReplacements count not restored " +
                    $"(expected={savedReplacements.Count}, actual={CrewReservationManager.CrewReplacements.Count})");
            }
        }

        [InGameTest(Category = "CrewReservation", Scene = GameScenes.SPACECENTER,
            Description = "#308 CrewAutoAssignPatch.ApplyCrewAssignmentSwaps replaces reserved crew with stand-ins in a VesselCrewManifest")]
        public void CrewAutoAssignPatch_SwapsReservedCrew()
        {
            // Construct a synthetic VesselCrewManifest with one command pod and
            // a reserved kerbal pre-assigned (simulating KSP's DefaultCrewForVessel
            // auto-assign). Then call the extracted ApplyCrewAssignmentSwaps logic
            // and verify the reserved name was replaced with the registered stand-in.

            var replacements = CrewReservationManager.CrewReplacements;
            if (replacements.Count == 0)
            {
                InGameAssert.Skip("needs at least one active crew reservation to exercise the swap path");
                return;
            }

            // Pick a reservation whose stand-in is Available (so the patch can place it).
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster");
                return;
            }
            string reservedName = null;
            string standInName = null;
            foreach (var kvp in replacements)
            {
                var pcmStandIn = roster[kvp.Value];
                if (pcmStandIn != null && pcmStandIn.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                {
                    reservedName = kvp.Key;
                    standInName = kvp.Value;
                    break;
                }
            }
            if (reservedName == null)
            {
                InGameAssert.Skip("no active reservation with an Available stand-in in the roster");
                return;
            }

            // Resolve a stock command pod with at least one crew seat. Try v2 first,
            // then fall back to the original Mk1.
            AvailablePart pod = PartLoader.getPartInfoByName("mk1pod.v2")
                ?? PartLoader.getPartInfoByName("mk1pod")
                ?? PartLoader.getPartInfoByName("Mark1Cockpit");
            if (pod == null || pod.partPrefab == null || pod.partPrefab.CrewCapacity == 0)
            {
                InGameAssert.Skip("no stock crew-capable pod found in PartLoader");
                return;
            }

            // Build a minimal SHIP ConfigNode and let VesselCrewManifest.FromConfigNode
            // construct the manifest. "part" value is "name_id".
            var shipNode = new ConfigNode("SHIP");
            var partNode = shipNode.AddNode("PART");
            partNode.AddValue("part", pod.name + "_12345");

            VesselCrewManifest vcm = VesselCrewManifest.FromConfigNode(shipNode);
            InGameAssert.IsNotNull(vcm, "FromConfigNode should return a manifest");
            InGameAssert.AreEqual(1, vcm.PartManifests.Count, "manifest should have exactly one part");

            var pcm = vcm.PartManifests[0];
            InGameAssert.IsTrue(pcm.partCrew.Length >= 1,
                $"test pod must have at least one seat (got {pcm.partCrew.Length})");

            // Pre-assign the reserved kerbal into seat 0 (simulating auto-assign).
            pcm.partCrew[0] = reservedName;

            // Fire the patch logic.
            Parsek.Patches.CrewAutoAssignPatch.ApplyCrewAssignmentSwaps(vcm);

            // Verify the reserved kerbal is no longer in the seat.
            InGameAssert.AreNotEqual(reservedName, pcm.partCrew[0],
                $"Reserved '{reservedName}' should have been removed from seat 0");
            // Stand-in should have been placed (Available + not already in manifest).
            InGameAssert.AreEqual(standInName, pcm.partCrew[0],
                $"Stand-in '{standInName}' should have been placed in seat 0 (got '{pcm.partCrew[0]}')");
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

        // ─────────────────────────────────────────────────────────────
        //  #282 — Landed ghost / spawn terrain clearance
        // ─────────────────────────────────────────────────────────────

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "NaN fallback: landed ghost clamp lifts buried points above terrain+4m (#282)")]
        public void LandedGhostClearance_NaNFallback_ClampsAboveTerrain()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            var body = activeVessel.mainBody;
            double lat = activeVessel.latitude;
            double lon = activeVessel.longitude;
            double terrainAlt = body.TerrainAltitude(lat, lon, true);

            var buriedPoint = new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = lat,
                longitude = lon,
                altitude = terrainAlt + 0.9,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };

            // NaN = legacy recording without terrain data — falls back to fixed clearance
            var clamped = ParsekFlight.ApplyLandedGhostClearance(
                buriedPoint, index: 282, vesselName: "Bug282NaN", recordedTerrainHeight: double.NaN);

            double expectedMin = terrainAlt + VesselSpawner.LandedGhostClearanceMeters;
            InGameAssert.IsGreaterThan(clamped.altitude, expectedMin - 0.001,
                $"NaN fallback must lift alt to at least terrain+{VesselSpawner.LandedGhostClearanceMeters} " +
                $"= {expectedMin:F2} (got {clamped.altitude:F2})");
            InGameAssert.AreEqual(buriedPoint.latitude, clamped.latitude,
                "Latitude must not change under ghost clearance clamp");
            InGameAssert.AreEqual(buriedPoint.longitude, clamped.longitude,
                "Longitude must not change under ghost clearance clamp");
        }

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "Recorded surface: ghost altitude is preserved when above the PQS safety floor (#309)")]
        public void LandedGhostClearance_RecordedTerrain_PreservesClearance()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            var body = activeVessel.mainBody;
            double lat = activeVessel.latitude;
            double lon = activeVessel.longitude;
            double currentTerrain = body.TerrainAltitude(lat, lon, true);

            // #309 new semantics: the recorded altitude is the truth. Position the
            // recorded altitude well above the current PQS safety floor so it's
            // preserved exactly. Simulates a vessel recorded on a mesh object
            // (Island Airfield) where alt is far above the raw PQS surface.
            double recordedTerrain = currentTerrain + 19.0; // mesh-object offset like the airfield
            double recordedAlt = recordedTerrain + 1.0;     // 1m clearance above the mesh

            var point = new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = lat,
                longitude = lon,
                altitude = recordedAlt,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };

            var clamped = ParsekFlight.ApplyLandedGhostClearance(
                point, index: 309, vesselName: "Bug309MeshObject", recordedTerrainHeight: recordedTerrain);

            // Recorded alt is well above (currentTerrain + 0.5m floor) → preserved exactly.
            InGameAssert.AreEqual(recordedAlt, clamped.altitude,
                $"Recorded altitude ({recordedAlt:F2}) above PQS safety floor " +
                $"({currentTerrain + 0.5:F2}) must be preserved exactly (got {clamped.altitude:F2})");
        }

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "Ghost clamp is a no-op when already above target altitude (#282)")]
        public void LandedGhostClearance_AlreadyClear_Unchanged()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            var body = activeVessel.mainBody;
            double lat = activeVessel.latitude;
            double lon = activeVessel.longitude;
            double terrainAlt = body.TerrainAltitude(lat, lon, true);

            double safeAlt = terrainAlt + VesselSpawner.LandedGhostClearanceMeters + 100.0;
            var clearPoint = new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = lat,
                longitude = lon,
                altitude = safeAlt,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };

            var clamped = ParsekFlight.ApplyLandedGhostClearance(
                clearPoint, index: 282, vesselName: "Bug282NoOp", recordedTerrainHeight: double.NaN);

            InGameAssert.AreEqual(safeAlt, clamped.altitude,
                "Point already above terrain+clearance must not be modified");
        }

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "Recorded-surface no-op: high altitude (e.g. mid-orbit reentry sample) is unchanged (#309)")]
        public void LandedGhostClearance_RecordedTerrain_AlreadyClear_Unchanged()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            var body = activeVessel.mainBody;
            double lat = activeVessel.latitude;
            double lon = activeVessel.longitude;
            double currentTerrain = body.TerrainAltitude(lat, lon, true);

            // #309 new semantics: as long as the recorded altitude is above the
            // current PQS terrain + 0.5m safety floor, it is preserved unchanged.
            // The old terrain-relative correction formula has been removed.
            double recordedTerrain = currentTerrain + 200.0;
            double recordedAlt = recordedTerrain + 5.0;

            var point = new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = lat,
                longitude = lon,
                altitude = recordedAlt,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };

            var clamped = ParsekFlight.ApplyLandedGhostClearance(
                point, index: 309, vesselName: "Bug309AboveFloor", recordedTerrainHeight: recordedTerrain);

            InGameAssert.AreEqual(recordedAlt, clamped.altitude,
                $"Recorded altitude above PQS safety floor must be preserved exactly " +
                $"(got {clamped.altitude:F2}, expected {recordedAlt:F2})");
        }

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "Recorded altitude below current PQS terrain is pushed up to safety floor (#309)")]
        public void LandedGhostClearance_NegativeClearance_ClampsToFloor()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            var body = activeVessel.mainBody;
            double lat = activeVessel.latitude;
            double lon = activeVessel.longitude;
            double currentTerrain = body.TerrainAltitude(lat, lon, true);

            // #309 new semantics: the safety floor is against CURRENT PQS terrain
            // (not recorded terrain). Place the recorded altitude clearly below
            // the current floor so the clamp fires.
            double recordedTerrain = currentTerrain - 5.0; // arbitrary "recorded surface"
            double recordedAlt = currentTerrain - 5.0;     // 5m below current floor

            var point = new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = lat,
                longitude = lon,
                altitude = recordedAlt,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };

            var clamped = ParsekFlight.ApplyLandedGhostClearance(
                point, index: 309, vesselName: "Bug309BelowFloor", recordedTerrainHeight: recordedTerrain);

            double floor = currentTerrain + 0.5;
            InGameAssert.IsGreaterThan(clamped.altitude, floor - 0.001,
                $"Recorded altitude below PQS floor must be pushed up to floor: " +
                $"expected >= {floor:F2} (got {clamped.altitude:F2})");
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

        // ─────────────────────────────────────────────────────────────
        //  #264 — EVA spawn position (in-flight)
        // ─────────────────────────────────────────────────────────────

        [InGameTest(Category = "EvaSpawnPosition", Scene = GameScenes.FLIGHT,
            Description = "Spawned EVA kerbal lands within 10m of recorded endpoint and >=50m from parent vessel")]
        public IEnumerator EvaSpawnAtRecordedEndpoint_NotOnParent()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.GetCrewCount() == 0)
            {
                InGameAssert.Skip("needs Flight scene with a manned active vessel");
                yield break;
            }

            var body = activeVessel.mainBody;
            if (body == null)
            {
                InGameAssert.Skip("active vessel has no mainBody");
                yield break;
            }

            const string testCrewName = "ParsekTestEvaEndpoint";
            const uint fakePid = 912641001u;
            ProtoCrewMember testKerbal = null;
            Parsek.Recording rec = null;
            Vessel spawnedVessel = null;

            try
            {
                // Create a throwaway crew member so the kerbalEVA snapshot has a valid crew ref.
                testKerbal = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(
                    ProtoCrewMember.KerbalType.Crew);
                testKerbal.ChangeName(testCrewName);
                testKerbal.rosterStatus = ProtoCrewMember.RosterStatus.Available;

                // Start ~5 m off the active vessel and walk 100 m due north at 5 m per step.
                // 5 m spacing matters: with stepMeters=1.5, ceil(5/1.5) = 4 sub-steps per
                // segment, so the walkback path in #264 is actually exercised (not degenerated
                // to point granularity).
                double baseLat = activeVessel.latitude;
                double baseLon = activeVessel.longitude;
                double baseAlt = body.TerrainAltitude(baseLat, baseLon) + 0.5;
                double latStepDeg = 5.0 / body.Radius * (180.0 / System.Math.PI);

                int referenceBodyIndex = FlightGlobals.Bodies.IndexOf(body);
                if (referenceBodyIndex < 0) referenceBodyIndex = 1; // Kerbin fallback

                var snapshot = Parsek.InGameTests.Helpers.InGameKerbalEvaSnapshot.Build(
                    testCrewName, baseLat, baseLon, baseAlt, referenceBodyIndex, fakePid);

                rec = new Parsek.Recording
                {
                    RecordingId = "eva-spawn-test-endpoint-" + System.DateTime.UtcNow.Ticks,
                    VesselName = testCrewName,
                    VesselPersistentId = fakePid,
                    EvaCrewName = testCrewName,
                    VesselSnapshot = snapshot,
                    TerminalStateValue = Parsek.TerminalState.Landed,
                };
                // 20 trajectory points stepping 5 m per point due north (~100 m total).
                double ut0 = Planetarium.GetUniversalTime();
                for (int i = 0; i < 20; i++)
                {
                    double lat = baseLat + (i * latStepDeg);
                    double terrainAlt = body.TerrainAltitude(lat, baseLon) + 0.5;
                    rec.Points.Add(new Parsek.TrajectoryPoint
                    {
                        ut = ut0 + i,
                        latitude = lat,
                        longitude = baseLon,
                        altitude = terrainAlt,
                        bodyName = body.name,
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero,
                    });
                }
                // (body name is already carried on each TrajectoryPoint)

                // Sanity pre-assertion: computed bounds for the snapshot must NOT fall into
                // the 2 m fallback path (which happens when PART.pos is missing). A valid
                // kerbalEVA snapshot produces a ~2.5 m cube from the default half-extent.
                Bounds kerbalBounds = Parsek.SpawnCollisionDetector.ComputeVesselBounds(snapshot);
                InGameAssert.IsGreaterThan(kerbalBounds.size.magnitude, 1.0,
                    "Snapshot ComputeVesselBounds should not be zero (PART pos missing?)");
                // Expected ~4.33 (magnitude of a 2.5 m cube). Tight upper bound catches
                // a regression that makes ComputeVesselBounds include world offsets or
                // aggregate multiple parts.
                InGameAssert.IsLessThan(kerbalBounds.size.magnitude, 6.0,
                    "Snapshot ComputeVesselBounds should be a kerbal-sized cube (~4.33, not a multi-part vessel)");

                // Dispatch through the real spawn entry point
                Parsek.VesselSpawner.SpawnOrRecoverIfTooClose(rec, 0);

                // Let several physics frames run so OrbitDriver.updateFromParameters
                // fires and any stale-orbit overwrite would be visible. 3 FixedUpdates
                // + a short WaitForSeconds gives margin against any KSP scheduler that
                // defers the first OrbitDriver update — a false positive here would
                // silently let the #264 stale-orbit bug pass the test.
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForSeconds(0.1f);

                InGameAssert.IsTrue(rec.VesselSpawned,
                    "Recording.VesselSpawned should be true after SpawnOrRecoverIfTooClose");
                InGameAssert.IsGreaterThan((double)rec.SpawnedVesselPersistentId, 0.0,
                    "SpawnedVesselPersistentId should be non-zero");

                spawnedVessel = Parsek.FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId);
                InGameAssert.IsNotNull(spawnedVessel,
                    "Spawned vessel should be findable by persistentId");

                // Expected endpoint world position
                var lastPt = rec.Points[rec.Points.Count - 1];
                Vector3d expectedWorldPos = body.GetWorldSurfacePosition(
                    lastPt.latitude, lastPt.longitude, lastPt.altitude);

                double distFromEndpoint = Vector3d.Distance(spawnedVessel.CoMD, expectedWorldPos);
                double distFromParent = Vector3d.Distance(spawnedVessel.CoMD, activeVessel.CoMD);

                ParsekLog.Info("TestRunner",
                    $"EvaSpawnAtRecordedEndpoint: distFromEndpoint={distFromEndpoint:F1} m, distFromParent={distFromParent:F1} m");

                // Generous tolerance (10 m) to accommodate post-spawn physics settle +
                // terrain-clamp clearance (+2 m) + rotating frame drift between the
                // synchronous spawn and the CoMD read two FixedUpdates later.
                InGameAssert.IsLessThan(distFromEndpoint, 10.0,
                    $"Spawned kerbal should be within 10 m of recorded endpoint (was {distFromEndpoint:F1} m)");
                InGameAssert.IsGreaterThan(distFromParent, 50.0,
                    $"Spawned kerbal should be ≥50 m from parent vessel (was {distFromParent:F1} m; endpoint was ~100 m out)");
            }
            finally
            {
                // Cleanup: recover spawned vessel + remove test kerbal from roster
                if (spawnedVessel != null && spawnedVessel.protoVessel != null)
                {
                    try
                    {
                        ShipConstruction.RecoverVesselFromFlight(
                            spawnedVessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"EvaSpawnAtRecordedEndpoint cleanup failed: {ex.Message}");
                    }
                }
                if (testKerbal != null)
                {
                    try { HighLogic.CurrentGame.CrewRoster.Remove(testKerbal); }
                    catch { /* best-effort */ }
                }
            }
        }

        [InGameTest(Category = "EvaSpawnPosition", Scene = GameScenes.FLIGHT,
            Description = "EVA spawn walks back along trajectory when endpoint overlaps a loaded vessel")]
        public IEnumerator EvaSpawnWalkbackOnOverlap()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.GetCrewCount() == 0)
            {
                InGameAssert.Skip("needs Flight scene with a manned active vessel");
                yield break;
            }

            var body = activeVessel.mainBody;
            if (body == null)
            {
                InGameAssert.Skip("active vessel has no mainBody");
                yield break;
            }

            const string testCrewName = "ParsekTestEvaWalkback";
            const uint fakePid = 912641002u;
            ProtoCrewMember testKerbal = null;
            Parsek.Recording rec = null;
            Vessel spawnedVessel = null;

            // Capture log output so we can assert the walkback ran.
            var captured = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            ParsekLog.TestSinkForTesting = line => captured.Add(line);

            try
            {
                testKerbal = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(
                    ProtoCrewMember.KerbalType.Crew);
                testKerbal.ChangeName(testCrewName);
                testKerbal.rosterStatus = ProtoCrewMember.RosterStatus.Available;

                // Start 100 m away from the active vessel, walk TOWARD it, terminating
                // at the active vessel's current center-of-mass world position rather than
                // its terrain contact point. That makes the test prove endpoint overlap
                // up front instead of assuming the vessel bounds happen to reach the
                // surface sample at latitude/longitude alone.
                Vector3d targetWorldPos = activeVessel.CoMD;
                double targetLat = body.GetLatitude(targetWorldPos);
                double targetLon = body.GetLongitude(targetWorldPos);
                double targetAlt = body.GetAltitude(targetWorldPos);
                double latStepDeg = 5.0 / body.Radius * (180.0 / System.Math.PI);
                double startLat = targetLat - (19 * latStepDeg);

                int referenceBodyIndex = FlightGlobals.Bodies.IndexOf(body);
                if (referenceBodyIndex < 0) referenceBodyIndex = 1;

                // Snapshot at start position (far from parent); trajectory converges on parent.
                double startAlt = body.TerrainAltitude(startLat, targetLon) + 0.5;
                var snapshot = Parsek.InGameTests.Helpers.InGameKerbalEvaSnapshot.Build(
                    testCrewName, startLat, targetLon, startAlt, referenceBodyIndex, fakePid);

                rec = new Parsek.Recording
                {
                    RecordingId = "eva-spawn-test-walkback-" + System.DateTime.UtcNow.Ticks,
                    VesselName = testCrewName,
                    VesselPersistentId = fakePid,
                    EvaCrewName = testCrewName,
                    VesselSnapshot = snapshot,
                    TerminalStateValue = Parsek.TerminalState.Landed,
                };
                double ut0 = Planetarium.GetUniversalTime();
                for (int i = 0; i < 20; i++)
                {
                    double lat = startLat + (i * latStepDeg);
                    double terrainAlt = body.TerrainAltitude(lat, targetLon) + 0.5;
                    rec.Points.Add(new Parsek.TrajectoryPoint
                    {
                        ut = ut0 + i,
                        latitude = lat,
                        longitude = targetLon,
                        altitude = terrainAlt,
                        bodyName = body.name,
                        rotation = Quaternion.identity,
                        velocity = Vector3.zero,
                    });
                }
                // (body name is already carried on each TrajectoryPoint)

                Bounds kerbalBounds = Parsek.SpawnCollisionDetector.ComputeVesselBounds(snapshot);
                Vector3d endpointWorldPos = body.GetWorldSurfacePosition(targetLat, targetLon, targetAlt);
                var (endpointOverlap, _, endpointBlockerName, _) =
                    Parsek.SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                        endpointWorldPos, kerbalBounds, 5f, skipActiveVessel: false);
                InGameAssert.IsTrue(endpointOverlap,
                    $"Test setup must force endpoint overlap before walkback (blocker='{endpointBlockerName ?? "none"}')");

                Parsek.VesselSpawner.SpawnOrRecoverIfTooClose(rec, 0);

                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();

                InGameAssert.IsTrue(rec.VesselSpawned,
                    "Recording.VesselSpawned should be true after walkback");
                InGameAssert.IsFalse(rec.WalkbackExhausted,
                    "Walkback should have found a clear position, not exhausted");

                spawnedVessel = Parsek.FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId);
                InGameAssert.IsNotNull(spawnedVessel,
                    "Spawned vessel should be findable after walkback");

                double distFromParent = Vector3d.Distance(spawnedVessel.CoMD, activeVessel.CoMD);
                ParsekLog.Info("TestRunner",
                    $"EvaSpawnWalkbackOnOverlap: distFromParent={distFromParent:F1} m");

                // Should land clearly outside the parent vessel but not walk all the way
                // back to trajectory start (~100 m away). Allow a wide band because the
                // actual distance depends on the parent vessel's computed bounds + 5 m
                // padding and both vary from craft to craft.
                InGameAssert.IsGreaterThan(distFromParent, 2.0,
                    $"Walkback should have moved the spawn off the parent (was {distFromParent:F1} m)");
                InGameAssert.IsLessThan(distFromParent, 100.0,
                    $"Walkback should not have walked back to trajectory start (was {distFromParent:F1} m)");
                double distFromEndpoint = Vector3d.Distance(spawnedVessel.CoMD, endpointWorldPos);
                InGameAssert.IsGreaterThan(distFromEndpoint, 1.0,
                    $"Walkback should move the EVA off the overlapping endpoint (was {distFromEndpoint:F1} m)");

                // Assert the runtime walkback path ran. The helper and the spawn wrapper
                // emit separate lines; accept either so the test keys off behavior, not
                // one exact string variant.
                bool sawWalkbackLog = captured.Any(l =>
                    (l.Contains("[SpawnCollision]") && l.Contains("WalkbackSubdivided: cleared")) ||
                    (l.Contains("[Spawner]") && l.Contains("TryWalkbackForEndOfRecordingSpawn: found clear position")));
                InGameAssert.IsTrue(sawWalkbackLog,
                    "Expected walkback success log line during spawn");
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                if (spawnedVessel != null && spawnedVessel.protoVessel != null)
                {
                    try
                    {
                        ShipConstruction.RecoverVesselFromFlight(
                            spawnedVessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"EvaSpawnWalkbackOnOverlap cleanup failed: {ex.Message}");
                    }
                }
                if (testKerbal != null)
                {
                    try { HighLogic.CurrentGame.CrewRoster.Remove(testKerbal); }
                    catch { /* best-effort */ }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  #278 — Limbo finalize uses real vessel situation
        // ─────────────────────────────────────────────────────────────

        [InGameTest(Category = "FinalizeLimbo", Scene = GameScenes.FLIGHT,
            Description = "FinalizeIndividualRecording on a live active vessel uses the real situation, not Destroyed (#278)")]
        public void FinalizeIndividualRecording_LiveActiveVessel_UsesRealSituation()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            // Build a leaf recording wired to the active vessel's pid. The
            // pre-#278 FinalizePendingLimboTreeForRevert blanket-stamped this
            // kind of leaf as Destroyed; the fix routes through
            // FinalizeIndividualRecording, which looks the vessel up by pid
            // and uses RecordingTree.DetermineTerminalState against
            // vessel.situation. For any vessel still loaded in FlightGlobals
            // (PRELAUNCH on the pad, LANDED for a walking kerbal, ORBITING in
            // orbit, …) the result must NOT be Destroyed — Destroyed is the
            // fallback for the vessel-is-gone branch only.
            var rec = new Parsek.Recording
            {
                RecordingId = "bug278-live-vessel-" + System.DateTime.UtcNow.Ticks,
                VesselName = activeVessel.vesselName,
                VesselPersistentId = activeVessel.persistentId,
                ChildBranchPointId = null, // leaf
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };
            rec.Points.Add(new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime() - 5.0,
                latitude = activeVessel.latitude,
                longitude = activeVessel.longitude,
                altitude = activeVessel.altitude,
                bodyName = activeVessel.mainBody?.name ?? "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            });

            var sit = activeVessel.situation;
            ParsekFlight.FinalizeIndividualRecording(
                rec, Planetarium.GetUniversalTime(), isSceneExit: true);

            InGameAssert.IsTrue(rec.TerminalStateValue.HasValue,
                "FinalizeIndividualRecording must always set a terminal state on a leaf");
            InGameAssert.IsTrue(
                rec.TerminalStateValue.Value != Parsek.TerminalState.Destroyed,
                $"Live active vessel '{activeVessel.vesselName}' (situation={sit}) " +
                $"must not be classified as Destroyed — that's the bug #278 regression. " +
                $"Got terminalState={rec.TerminalStateValue.Value}.");

            ParsekLog.Verbose("TestRunner",
                $"Bug278 in-game test: vessel='{activeVessel.vesselName}' " +
                $"situation={sit} → terminalState={rec.TerminalStateValue.Value}");
        }

        [InGameTest(Category = "FinalizeLimbo", Scene = GameScenes.FLIGHT,
            Description = "FinalizeIndividualRecording leaves an existing terminal state untouched (#278 regression guard)")]
        public void FinalizeIndividualRecording_LiveActiveVessel_PreservesExistingTerminalState()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.Skip("needs Flight scene with an active vessel");
                return;
            }

            // A leaf that was already classified Splashed elsewhere (e.g. by
            // EndDebrisRecording during the live flight) must NOT be reclassified
            // by the limbo finalize. The `if (isLeaf && !rec.TerminalStateValue.HasValue)`
            // guard inside FinalizeIndividualRecording is what protects this.
            var rec = new Parsek.Recording
            {
                RecordingId = "bug278-preserve-" + System.DateTime.UtcNow.Ticks,
                VesselName = activeVessel.vesselName,
                VesselPersistentId = activeVessel.persistentId,
                ChildBranchPointId = null,
                TerminalStateValue = Parsek.TerminalState.Splashed,
            };

            ParsekFlight.FinalizeIndividualRecording(
                rec, Planetarium.GetUniversalTime(), isSceneExit: true);

            InGameAssert.AreEqual(Parsek.TerminalState.Splashed,
                rec.TerminalStateValue.Value);
        }

        // ─────────────────────────────────────────────────────────────
        // #289 — Re-snapshot during finalize when terminal state is stable
        //
        // Bug: end-of-mission spawn-at-end fails with "snapshot situation
        // unsafe (FLYING/SUB_ORBITAL)" because VesselSnapshot.sit was captured
        // at recording start (Flying) and never refreshed when the vessel
        // splashed down. Even on isSceneExit=true paths, FinalizeIndividualRecording
        // now re-snapshots if terminal state is stable (Landed/Splashed/Orbiting)
        // and the live vessel is still findable.
        // ─────────────────────────────────────────────────────────────

        [InGameTest(Category = "Bug289", Scene = GameScenes.FLIGHT,
            Description = "FinalizeIndividualRecording re-snapshots a stable-terminal recording on scene exit and marks files dirty (#289)")]
        public void FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.IsTrue(false,
                    "FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty needs an active vessel");
                return;
            }

            // Build a leaf recording wired to the active vessel's pid with terminal=Splashed
            // (the user's case: vessel splashed down, ChainSegmentManager set terminal earlier,
            // FinalizeIndividualRecording's "if (!HasValue)" gate would skip the original
            // re-snapshot block — the new #289 path runs OUTSIDE that gate).
            var staleSnapshot = new ConfigNode("VESSEL");
            staleSnapshot.AddValue("sit", "FLYING");  // simulate stale sit field from recording start
            staleSnapshot.AddValue("name", "ParsekTestBug289Stale");

            var rec = new Parsek.Recording
            {
                RecordingId = "bug289-resnap-" + System.DateTime.UtcNow.Ticks,
                VesselName = activeVessel.vesselName,
                VesselPersistentId = activeVessel.persistentId,
                ExplicitStartUT = Planetarium.GetUniversalTime() - 60,
                ExplicitEndUT = Planetarium.GetUniversalTime(),
                TerminalStateValue = Parsek.TerminalState.Landed,  // already set, gates the !HasValue branch
                VesselSnapshot = staleSnapshot,
            };
            // Single point so the leaf isn't pruned as zero-data
            rec.Points.Add(new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = activeVessel.latitude,
                longitude = activeVessel.longitude,
                altitude = activeVessel.altitude,
                bodyName = activeVessel.mainBody?.name ?? "Kerbin",
            });

            // Capture log so we can assert the re-snapshot fired
            var logLines = new System.Collections.Generic.List<string>();
            var prevSink = Parsek.ParsekLog.TestSinkForTesting;
            Parsek.ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ParsekFlight.FinalizeIndividualRecording(
                    rec, Planetarium.GetUniversalTime(), isSceneExit: true);
            }
            finally
            {
                Parsek.ParsekLog.TestSinkForTesting = prevSink;
            }

            // The fresh snapshot should be from the live vessel — its sit field should
            // reflect the vessel's actual situation, NOT the stale "FLYING" we seeded.
            InGameAssert.IsNotNull(rec.VesselSnapshot,
                "FinalizeIndividualRecording must replace the stale snapshot with a fresh one");
            string sit = rec.VesselSnapshot.GetValue("sit");
            InGameAssert.AreNotEqual("FLYING", sit ?? "<null>",
                "Snapshot sit field must be refreshed from the live vessel, not preserved from the stale source");

            // FilesDirty must be set so the next SaveRecordingFiles call writes the fresh snapshot
            InGameAssert.IsTrue(rec.FilesDirty,
                "Re-snapshot path must call MarkFilesDirty so the fresh snapshot reaches the sidecar");

            // Re-snapshot Info log line should fire
            bool found = false;
            foreach (var line in logLines)
            {
                if (line.Contains("[Flight]")
                    && line.Contains("FinalizeIndividualRecording")
                    && line.Contains("stable terminal state")
                    && line.Contains("[#289"))
                {
                    found = true;
                    break;
                }
            }
            InGameAssert.IsTrue(found,
                "Expected FinalizeIndividualRecording stable-terminal re-snapshot log line during finalize");
        }

        [InGameTest(Category = "Bug289", Scene = GameScenes.FLIGHT,
            Description = "FinalizeIndividualRecording does NOT re-snapshot when terminal state is non-stable (#289)")]
        public void FinalizeReSnapshot_NonStableTerminal_DoesNotReSnapshot()
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.IsTrue(false,
                    "FinalizeReSnapshot_NonStableTerminal_DoesNotReSnapshot needs an active vessel");
                return;
            }

            // Pre-set terminal=Destroyed (non-stable). The new #289 path checks for
            // Landed/Splashed/Orbiting only, so this should NOT trigger re-snapshot.
            var staleSnapshot = new ConfigNode("VESSEL");
            staleSnapshot.AddValue("sit", "FLYING");
            staleSnapshot.AddValue("name", "ParsekTestBug289NonStable");

            var rec = new Parsek.Recording
            {
                RecordingId = "bug289-nonstable-" + System.DateTime.UtcNow.Ticks,
                VesselName = activeVessel.vesselName,
                VesselPersistentId = activeVessel.persistentId,
                ExplicitStartUT = Planetarium.GetUniversalTime() - 60,
                ExplicitEndUT = Planetarium.GetUniversalTime(),
                TerminalStateValue = Parsek.TerminalState.Destroyed,
                VesselSnapshot = staleSnapshot,
            };
            rec.Points.Add(new Parsek.TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = 0,
                longitude = 0,
                altitude = 0,
                bodyName = "Kerbin",
            });

            ParsekFlight.FinalizeIndividualRecording(
                rec, Planetarium.GetUniversalTime(), isSceneExit: true);

            // Snapshot should be unchanged (still has stale sit=FLYING)
            InGameAssert.IsNotNull(rec.VesselSnapshot,
                "Snapshot should still exist (no nulling for non-stable terminal in this code path)");
            string sit = rec.VesselSnapshot.GetValue("sit");
            InGameAssert.AreEqual("FLYING", sit,
                "Snapshot sit field must remain stale for non-stable terminal states (no re-snapshot)");
        }

        // ── QuickloadResume (#269) ──────────────────────────────────────────────

        /// <summary>
        /// Canary test: verifies that the TestRunnerShortcut singleton survives a
        /// scene transition via quickload. This drives a real stock quickload and is
        /// intentionally single-run only: when stock restore itself fails, it can
        /// leave the live FLIGHT session broken.
        /// </summary>
        [InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — excluded from Run All / Run category because this real F5/F9 scene transition can leave the live FLIGHT session broken when stock quickload fails.",
            Description = "Verify TestRunnerShortcut DontDestroyOnLoad survives quickload")]
        public IEnumerator BridgeSurvivesSceneTransition()
        {
            // Sentinel: use a static field on TestRunnerShortcut
            var preInstance = TestRunnerShortcut.Instance;
            InGameAssert.IsNotNull(preInstance, "TestRunnerShortcut.Instance must be non-null before quickload");
            var preFlight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(preFlight, "ParsekFlight.Instance must be non-null before quickload");

            Helpers.QuickloadResumeHelpers.TriggerQuicksave();
            yield return new WaitForSeconds(0.5f);

            Helpers.QuickloadResumeHelpers.TriggerQuickload();
            yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(
                preFlight.GetInstanceID(), 15f);

            // The same singleton must survive — DontDestroyOnLoad keeps it alive
            var postInstance = TestRunnerShortcut.Instance;
            InGameAssert.IsNotNull(postInstance,
                "TestRunnerShortcut.Instance must be non-null after quickload (DontDestroyOnLoad)");
            InGameAssert.AreEqual(preInstance.GetInstanceID(), postInstance.GetInstanceID(),
                "TestRunnerShortcut instance must be the SAME object after quickload");
        }

        /// <summary>
        /// #269 core test: quickload mid-recording resumes with the same activeRecordingId.
        /// Verifies the full F5 → fly → F9 → restore coroutine → resumed recording path.
        /// This also drives a real stock quickload, so it is intentionally single-run only.
        /// </summary>
        [InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — excluded from Run All / Run category because this real F5/F9 resume check can poison the current FLIGHT session if stock quickload restores into a broken state.",
            Description = "F5/F9 mid-recording resumes same activeRecordingId")]
        public IEnumerator Quickload_MidRecording_ResumesSameActiveRecordingId()
        {
            // Pre-condition: must have an active recording in tree mode
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            if (!flight.IsRecording)
            {
                InGameAssert.Skip("no active recording — start a recording before running this test");
                yield break;
            }

            string preRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
            InGameAssert.IsNotNull(preRecId, "ActiveRecordingId must be set before F5");
            int preFlightInstanceId = flight.GetInstanceID();

            // F5
            Helpers.QuickloadResumeHelpers.TriggerQuicksave();
            yield return new WaitForSeconds(2f); // accumulate post-F5 data

            // F9
            Helpers.QuickloadResumeHelpers.TriggerQuickload();
            yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(
                preFlightInstanceId, 15f);
            yield return Helpers.QuickloadResumeHelpers.WaitForActiveRecording(10f);

            // Re-query: old ParsekFlight instance is destroyed
            var postFlight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(postFlight, "ParsekFlight.Instance must exist after quickload");
            InGameAssert.IsTrue(postFlight.HasActiveTree, "ActiveTree must be restored after quickload");

            string postRecId = postFlight.ActiveTreeForSerialization.ActiveRecordingId;
            InGameAssert.AreEqual(preRecId, postRecId,
                $"ActiveRecordingId must match: pre={preRecId} post={postRecId}");
        }

        /// <summary>
        /// #267 regression test: verify the restoringActiveTree reentrancy guard exists
        /// and is cleared after the restore coroutine completes.
        /// </summary>
        [InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT,
            Description = "#267: restoringActiveTree guard is false after restore completes")]
        public void ReentrancyGuard_ClearedAfterRestore()
        {
            // The guard must be false during normal flight (no restore in progress)
            InGameAssert.IsFalse(ParsekFlight.restoringActiveTree,
                "restoringActiveTree must be false during normal flight");
        }

        // ======================= GhostAudio (#265) =======================

        [InGameTest(Category = "GhostAudio",
            Description = "#265: PauseAllAudio/UnpauseAllAudio with null state does not throw")]
        public void PauseUnpauseAudio_NullState_NoCrash()
        {
            GhostPlaybackLogic.PauseAllAudio(null);
            GhostPlaybackLogic.UnpauseAllAudio(null);
        }

        [InGameTest(Category = "GhostAudio",
            Description = "#265: PauseAllAudio with empty/null audioInfos does not throw")]
        public void PauseUnpauseAudio_EmptyState_NoCrash()
        {
            var state = new GhostPlaybackState();
            // audioInfos is null by default
            GhostPlaybackLogic.PauseAllAudio(state);
            GhostPlaybackLogic.UnpauseAllAudio(state);

            // Now with an empty dict
            state.audioInfos = new Dictionary<ulong, AudioGhostInfo>();
            GhostPlaybackLogic.PauseAllAudio(state);
            GhostPlaybackLogic.UnpauseAllAudio(state);
        }

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#265: PauseAllAudio pauses playing AudioSource, UnpauseAllAudio resumes")]
        public IEnumerator PauseUnpauseAudio_RealAudioSource()
        {
            var go = new GameObject("ParsekTest_AudioGhost");
            runner.TrackForCleanup(go);
            var audioSource = go.AddComponent<AudioSource>();
            audioSource.clip = AudioClip.Create("test_silence", 44100, 1, 44100, false);
            audioSource.loop = true;
            audioSource.volume = 0f;
            audioSource.Play();

            yield return null; // let audio system process

            InGameAssert.IsTrue(audioSource.isPlaying,
                "AudioSource should be playing before pause");

            var info = new AudioGhostInfo
            {
                partPersistentId = 99999,
                moduleIndex = 0,
                audioSource = audioSource,
                currentPower = 0.75f
            };
            var state = new GhostPlaybackState
            {
                audioInfos = new Dictionary<ulong, AudioGhostInfo> { { 99999UL, info } }
            };

            GhostPlaybackLogic.PauseAllAudio(state);
            yield return null;

            InGameAssert.IsFalse(audioSource.isPlaying,
                "AudioSource should be paused after PauseAllAudio");
            InGameAssert.ApproxEqual(0.75f, info.currentPower, 0.001f,
                "currentPower should survive pause");

            GhostPlaybackLogic.UnpauseAllAudio(state);
            yield return null;

            InGameAssert.IsTrue(audioSource.isPlaying,
                "AudioSource should be playing after UnpauseAllAudio");
            InGameAssert.ApproxEqual(0.75f, info.currentPower, 0.001f,
                "currentPower should survive unpause");

            ParsekLog.Verbose("TestRunner",
                "GhostAudio pause/unpause cycle verified with real AudioSource + currentPower preservation");
        }

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#265: OneShotAudio pause/unpause path works")]
        public IEnumerator PauseUnpauseAudio_OneShotPath()
        {
            var go = new GameObject("ParsekTest_OneShotAudio");
            runner.TrackForCleanup(go);
            var audioSource = go.AddComponent<AudioSource>();
            audioSource.clip = AudioClip.Create("test_oneshot", 44100, 1, 44100, false);
            audioSource.loop = true;
            audioSource.volume = 0f;
            audioSource.Play();

            yield return null;

            InGameAssert.IsTrue(audioSource.isPlaying,
                "OneShotAudio should be playing before pause");

            var state = new GhostPlaybackState
            {
                oneShotAudio = new OneShotAudioInfo { audioSource = audioSource }
            };

            GhostPlaybackLogic.PauseAllAudio(state);
            yield return null;

            InGameAssert.IsFalse(audioSource.isPlaying,
                "OneShotAudio should be paused after PauseAllAudio");

            GhostPlaybackLogic.UnpauseAllAudio(state);
            yield return null;

            InGameAssert.IsTrue(audioSource.isPlaying,
                "OneShotAudio should resume after UnpauseAllAudio");

            ParsekLog.Verbose("TestRunner",
                "OneShotAudio pause/unpause cycle verified");
        }

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#265: Engine-level PauseAllGhostAudio/UnpauseAllGhostAudio iterates all ghost states")]
        public void EngineLevel_PauseUnpauseGhostAudio_NoCrash()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs ParsekFlight.Instance with Engine");
                return;
            }

            // Safe to call even with zero ghosts — verifies the loop paths don't crash
            flight.Engine.PauseAllGhostAudio();
            flight.Engine.UnpauseAllGhostAudio();

            ParsekLog.Verbose("TestRunner",
                "Engine-level PauseAllGhostAudio/UnpauseAllGhostAudio completed without crash");
        }

        // ======================= FinalizeBackfill (#265 / #259) =======================

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#265/#259: TerminalOrbitBody backfilled from last OrbitSegment when vessel not found")]
        public void TerminalOrbitBackfill_FromOrbitSegment()
        {
            var rec = new Recording
            {
                VesselName = "TestBackfill",
                VesselPersistentId = 0, // forces null vessel lookup → fallback path
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = null
            };

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000,
                endUT = 2000,
                bodyName = "Kerbin",
                inclination = 28.5,
                eccentricity = 0.001,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0,
                epoch = 1000
            });

            // Need at least one point so the leaf-no-data warning doesn't fire
            rec.Points.Add(new TrajectoryPoint { ut = 1000, bodyName = "Kerbin" });

            ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);

            InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody,
                $"TerminalOrbitBody should be 'Kerbin', got '{rec.TerminalOrbitBody}'");
            InGameAssert.ApproxEqual(28.5, rec.TerminalOrbitInclination, 0.01);
            InGameAssert.ApproxEqual(0.001, rec.TerminalOrbitEccentricity, 0.0001);
            InGameAssert.ApproxEqual(700000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);

            ParsekLog.Verbose("TestRunner",
                $"TerminalOrbitBody backfill verified: body={rec.TerminalOrbitBody} " +
                $"sma={rec.TerminalOrbitSemiMajorAxis:F1}");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#265: Backfill skipped when TerminalOrbitBody already populated")]
        public void TerminalOrbitBackfill_AlreadyPopulated_NoOverwrite()
        {
            var rec = new Recording
            {
                VesselName = "TestNoOverwrite",
                VesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 250000
            };

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000,
                endUT = 2000,
                bodyName = "Kerbin",
                semiMajorAxis = 700000
            });
            rec.Points.Add(new TrajectoryPoint { ut = 1000, bodyName = "Mun" });

            ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);

            // Backfill should NOT have overwritten the existing values
            InGameAssert.AreEqual("Mun", rec.TerminalOrbitBody,
                "Existing TerminalOrbitBody should not be overwritten");
            InGameAssert.ApproxEqual(250000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody no-overwrite verified: body still Mun");
        }

        // ======================= BackgroundRecorderSeedSkip (#265) =======================

        [InGameTest(Category = "BackgroundSeeder", Scene = GameScenes.FLIGHT,
            Description = "#265: PartStateSeeder.EmitSeedEvents works with real vessel parts (xUnit blocked by AudioModule)")]
        public void SeedEvents_ActiveVessel_ProducesConsistentEvents()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null || v.parts == null || v.parts.Count == 0)
            {
                InGameAssert.Skip("needs active vessel with parts");
                return;
            }

            var engines = FlightRecorder.CacheEngineModules(v);
            var rcs = FlightRecorder.CacheRcsModules(v);

            // First seed pass — populates tracking sets
            var sets = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, sets, engines, rcs, false, "Test");

            var partNames = new Dictionary<uint, string>();
            foreach (var p in v.parts)
                if (p != null && !partNames.ContainsKey(p.persistentId))
                    partNames[p.persistentId] = p.partInfo?.name ?? "unknown";

            double ut = Planetarium.GetUniversalTime();
            var events1 = PartStateSeeder.EmitSeedEvents(sets, partNames, ut, "Test");

            // Second seed pass on same vessel — should produce identical event types+PIDs
            var sets2 = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, sets2, engines, rcs, false, "Test");
            var events2 = PartStateSeeder.EmitSeedEvents(sets2, partNames, ut + 1, "Test");

            InGameAssert.AreEqual(events1.Count, events2.Count,
                $"Re-seeding should produce same count: first={events1.Count} second={events2.Count}");

            // Verify no internal duplicates within a single seed batch
            var seen = new HashSet<string>();
            foreach (var evt in events1)
            {
                string key = $"{evt.partPersistentId}_{evt.eventType}_{evt.moduleIndex}";
                InGameAssert.IsTrue(seen.Add(key),
                    $"Duplicate seed event within batch: {key}");
            }

            ParsekLog.Verbose("TestRunner",
                $"SeedEvents consistency verified: {events1.Count} events for " +
                $"{v.vesselName} ({v.parts.Count} parts), no duplicates");
        }

        [InGameTest(Category = "BackgroundSeeder", Scene = GameScenes.FLIGHT,
            Description = "#265: Without Count>0 guard, double-seeding produces duplicate part events")]
        public void SeedEvents_DoubleSeed_WithoutGuard_ProducesDuplicates()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null || v.parts == null || v.parts.Count == 0)
            {
                InGameAssert.Skip("needs active vessel with parts");
                return;
            }

            var engines = FlightRecorder.CacheEngineModules(v);
            var rcs = FlightRecorder.CacheRcsModules(v);
            var sets = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, sets, engines, rcs, false, "Test");

            var partNames = new Dictionary<uint, string>();
            foreach (var p in v.parts)
                if (p != null && !partNames.ContainsKey(p.persistentId))
                    partNames[p.persistentId] = p.partInfo?.name ?? "unknown";

            double ut = Planetarium.GetUniversalTime();
            var seedEvents = PartStateSeeder.EmitSeedEvents(sets, partNames, ut, "Test");

            if (seedEvents.Count == 0)
            {
                // Vessel has no stateful parts (e.g., bare capsule) — can't demonstrate the guard
                ParsekLog.Verbose("TestRunner",
                    $"Vessel {v.vesselName} has no seed events — skipping double-seed test");
                InGameAssert.Skip("vessel has no stateful parts to seed");
                return;
            }

            // Simulate what happens WITHOUT the Count>0 guard: seed once, then seed again
            var rec = new Recording { VesselName = "TestDoubleSeed" };
            rec.PartEvents.AddRange(seedEvents);
            int countAfterFirst = rec.PartEvents.Count;

            // Second seed pass
            var sets2 = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, sets2, engines, rcs, false, "Test");
            var seedEvents2 = PartStateSeeder.EmitSeedEvents(sets2, partNames, ut + 1, "Test");
            rec.PartEvents.AddRange(seedEvents2);

            // Without the guard, the count doubles — this is the bug the guard prevents
            InGameAssert.IsGreaterThan(rec.PartEvents.Count, countAfterFirst,
                "Double-seeding without guard MUST produce more events (demonstrates guard necessity)");

            ParsekLog.Verbose("TestRunner",
                $"Double-seed guard necessity verified: first={countAfterFirst} " +
                $"after-double={rec.PartEvents.Count} events");
        }

        // ======================= ResourceManifest (Phase 11) =======================

        [InGameTest(Category = "ResourceManifest", Scene = GameScenes.FLIGHT,
            Description = "ExtractResourceManifest returns valid manifest from live vessel snapshot")]
        public void ExtractResourceManifest_LiveVessel_ReturnsNonNull()
        {
            // Get active vessel
            var vessel = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(vessel, "No active vessel in flight");

            // Take snapshot (same path as recording system)
            var snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            InGameAssert.IsNotNull(snapshot, "Failed to snapshot active vessel");

            // Extract resource manifest
            var manifest = VesselSpawner.ExtractResourceManifest(snapshot);
            InGameAssert.IsNotNull(manifest, "Resource manifest is null — vessel has no non-EC/IntakeAir resources?");
            InGameAssert.IsTrue(manifest.Count > 0, "Resource manifest is empty");

            // Log what we found for diagnostic verification
            foreach (var kvp in manifest)
                ParsekLog.Verbose("TestRunner", $"ResourceManifest test: {kvp.Key} = {kvp.Value}");

            // Verify ElectricCharge is excluded
            InGameAssert.IsFalse(manifest.ContainsKey("ElectricCharge"), "ElectricCharge should be excluded from manifest");

            // Verify at least one common resource exists (most vessels have fuel)
            bool hasCommonResource = manifest.ContainsKey("LiquidFuel") || manifest.ContainsKey("SolidFuel")
                || manifest.ContainsKey("MonoPropellant") || manifest.ContainsKey("Oxidizer");
            InGameAssert.IsTrue(hasCommonResource, "No common fuel resource found — test vessel needs fuel");
        }

        #region ResourceReconciliation (Phase F)

        [InGameTest(Category = "ResourceReconciliation", Scene = GameScenes.SPACECENTER,
            Description = "Phase F: ApplyTreeLumpSum / ApplyTreeResourceDeltas / ApplyResourceDeltas " +
                "are gone — neither method emits its old log shape during normal SPACECENTER lifecycle. " +
                "Run after revert/rewind to confirm no suspicious-drawdown WARN.")]
        public IEnumerator NoLumpSumOrStandaloneApplierLogsOnSpacecenterEntry()
        {
            // Capture log lines for the duration of the test.
            var captured = new List<string>();
            var prevSink = ParsekLog.TestSinkForTesting;
            ParsekLog.TestSinkForTesting = line => { captured.Add(line); prevSink?.Invoke(line); };

            try
            {
                // Yield one frame so any pending Update() ticks have a chance to fire.
                yield return null;
                yield return null;
                yield return null;

                // Phase F: these production log shapes must NEVER appear.
                foreach (var line in captured)
                {
                    InGameAssert.IsFalse(line.Contains("ApplyTreeLumpSum"),
                        "Phase F: ApplyTreeLumpSum log line must not appear (method is deleted): " + line);
                    InGameAssert.IsFalse(line.Contains("ApplyTreeResourceDeltas"),
                        "Phase F: ApplyTreeResourceDeltas log line must not appear (method is deleted): " + line);
                    InGameAssert.IsFalse(line.Contains("Tree resource lump sum applied"),
                        "Phase F: lump-sum-applied log line must not appear: " + line);
                    InGameAssert.IsFalse(line.Contains("Timeline resource:"),
                        "Phase F: standalone applier log line must not appear (ApplyResourceDeltas is deleted): " + line);
                }

                // Bonus end-to-end check: a healthy ledger walk must not log
                // 'PatchFunds: suspicious drawdown' at SPACECENTER entry.
                foreach (var line in captured)
                {
                    InGameAssert.IsFalse(
                        line.Contains("PatchFunds") && line.Contains("suspicious drawdown"),
                        "Phase F: PatchFunds suspicious drawdown WARN must not fire on a healthy save: " + line);
                }

                ParsekLog.Verbose("TestRunner",
                    $"NoLumpSumOrStandaloneApplierLogsOnSpacecenterEntry: scanned {captured.Count} log line(s) — clean.");
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
            }
        }

        #endregion

        #region Bug #450 B3 Lazy Reentry FX

        [InGameTest(Category = "ReentryFx", Scene = GameScenes.FLIGHT,
            Description = "Bug #450 B3: TryBuildReentryFx on a real Unity GameObject returns null for an empty ghost root, and the lazy-build helper clears the pending flag without incrementing the builds counter.")]
        public void Bug450B3_LazyBuild_OnEmptyGhostRoot_ClearsFlagWithoutCountingBuild()
        {
            // Residual-risk coverage for the plan's "in-game only" test gap: verifies
            // the live-Unity lazy-build path wiring. We do NOT construct a real ghost
            // (that requires an active snapshot + AvailablePart + Unity prefab), but
            // we DO exercise TryBuildReentryFx with a genuine GameObject so the Unity
            // APIs inside (GetComponentsInChildren, Mesh.CombineMeshes, ParticleSystem
            // setup) run against a real scene. An empty ghost root produces no mesh
            // coverage → TryBuildReentryFx returns null → the lazy-build wrapper must
            // clear the flag and NOT bump reentryFxBuildsThisSession.
            var ghostRoot = new GameObject("ParsekTestGhost_B3");
            runner.TrackForCleanup(ghostRoot);

            var state = new GhostPlaybackState
            {
                vesselName = "TestB3",
                ghost = ghostRoot,
                reentryFxPendingBuild = true,
                heatInfos = new System.Collections.Generic.Dictionary<uint, HeatGhostInfo>(),
                reentryFxInfo = null,
            };

            // Use a fresh engine instance so the session-wide build counter reads
            // cleanly from DiagnosticsState. The engine constructor is free of
            // side-effects besides field initialisation.
            var engine = new GhostPlaybackEngine(positioner: null);
            int buildsBefore = DiagnosticsState.health.reentryFxBuildsThisSession;

            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 999, state, vesselName: "TestB3",
                bodyName: "Kerbin", altitude: 50_000);

            // Flag clears regardless of build success (one-shot, no retry storm).
            InGameAssert.IsFalse(state.reentryFxPendingBuild,
                "Lazy build must clear reentryFxPendingBuild even when TryBuildReentryFx returns null");
            // Counter only bumps on non-null build. Empty ghost root → null → no bump.
            int buildsAfter = DiagnosticsState.health.reentryFxBuildsThisSession;
            InGameAssert.AreEqual(buildsBefore, buildsAfter,
                "reentryFxBuildsThisSession must NOT increment when TryBuildReentryFx returns null");
            // A frame-slot IS consumed (we did invoke the build). Confirms the
            // counter lives in the unthrottled-success path.
            InGameAssert.AreEqual(1, engine.FrameLazyReentryBuildCountForTesting,
                "A build attempt must consume one per-frame slot regardless of result");
        }

        [InGameTest(Category = "ReentryFx", Scene = GameScenes.FLIGHT,
            Description = "Bug #450 B3: the speed gate in ShouldBuildLazyReentryFx matches the shared ReentryPotentialSpeedFloor constant, confirming the value we gate on in production is the documented 400 m/s floor.")]
        public void Bug450B3_SpeedGate_MatchesReentryPotentialFloor()
        {
            // Pin the cross-file contract between the B3 helper's speed floor and
            // TrajectoryMath.ReentryPotentialSpeedFloor. If these drift apart, a
            // trajectory that `HasReentryPotential` accepts may be stranded in
            // pending-forever state at spawn, or vice versa. The constant lives in
            // TrajectoryMath so the floor is a single source of truth.
            float floor = TrajectoryMath.ReentryPotentialSpeedFloor;
            InGameAssert.IsTrue(floor >= 100f && floor <= 1000f,
                $"ReentryPotentialSpeedFloor={floor} is outside the expected 100-1000 m/s sanity range; B3 speed gate may be miscalibrated");

            // Just at the floor → build fires.
            InGameAssert.IsTrue(
                GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                    pendingFlag: true, bodyName: "Kerbin",
                    bodyHasAtmosphere: true, altitudeMeters: 50_000, atmosphereDepthMeters: 70_000,
                    surfaceSpeedMetersPerSecond: floor, speedFloorMetersPerSecond: floor),
                "At exactly the speed floor, the build must fire");
            // Just below the floor → build does NOT fire.
            InGameAssert.IsFalse(
                GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                    pendingFlag: true, bodyName: "Kerbin",
                    bodyHasAtmosphere: true, altitudeMeters: 50_000, atmosphereDepthMeters: 70_000,
                    surfaceSpeedMetersPerSecond: floor - 1f, speedFloorMetersPerSecond: floor),
                "One m/s below the speed floor, the build must NOT fire");
        }

        #endregion
    }
}
