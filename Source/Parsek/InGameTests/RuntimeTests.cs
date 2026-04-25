using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KSP.UI;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Core runtime tests that verify Parsek systems work in a live KSP environment.
    /// These catch bugs that xUnit tests structurally cannot (Unity APIs, real KSP state, etc.).
    /// </summary>
    public class RuntimeTests
    {
        internal const float TimeScalePositiveThreshold = 0.01f;
        internal const int TimeScalePositiveProbeFrames = 8;
        internal const float TimeJumpLaunchAutoRecordTransientTimeoutSeconds = 3f;

        internal enum TimeScalePositiveProbeOutcome
        {
            Passed,
            SkipStockPause,
            SkipPauseProbeUnavailable,
            FailZeroWithoutPause,
        }

        internal struct TimeScalePositiveProbeSample
        {
            public int SampleIndex;
            public int FrameCount;
            public float RealtimeSinceStartup;
            public float TimeScale;
            public bool? FlightDriverPause;
            public string KspLoaderLastUpdate;
            public string RunnerCoroutineState;
            public string SceneName;
        }

        private readonly InGameTestRunner runner;
        private static readonly System.Type FlightEvaType =
            typeof(Part).Assembly.GetType("FlightEVA", false);
        private static readonly FieldInfo FlightEvaFetchField =
            FlightEvaType?.GetField("fetch",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo FlightEvaSpawnMethod =
            FlightEvaType?.GetMethod("spawnEVA",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(ProtoCrewMember), typeof(Part), typeof(Transform), typeof(bool) },
                null);
        private static readonly FieldInfo PartAirlockField =
            typeof(Part).GetField("airlock",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PopupDialogToDisplayField =
            typeof(PopupDialog).GetField("dialogToDisplay",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogNameField =
            typeof(MultiOptionDialog).GetField("name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogTitleField =
            typeof(MultiOptionDialog).GetField("title",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogOptionsField =
            typeof(MultiOptionDialog).GetField("Options",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DialogGuiButtonTextField =
            typeof(DialogGUIButton).GetField("OptionText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo DialogGuiButtonOptionSelectedMethod =
            typeof(DialogGUIButton).GetMethod("OptionSelected",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ParsekScenarioShowDeferredMergeDialogMethod =
            typeof(ParsekScenario).GetMethod("ShowDeferredMergeDialog",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ParsekFlightOnVesselSwitchCompleteMethod =
            typeof(ParsekFlight).GetMethod("OnVesselSwitchComplete",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ParsekFlightDisarmPostSwitchAutoRecordMethod =
            typeof(ParsekFlight).GetMethod("DisarmPostSwitchAutoRecord",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ParsekFlightPostSwitchAutoRecordField =
            typeof(ParsekFlight).GetField("postSwitchAutoRecord",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly System.Type ParsekFlightPostSwitchAutoRecordStateType =
            typeof(ParsekFlight).GetNestedType("PostSwitchAutoRecordState",
                BindingFlags.NonPublic);
        private static readonly FieldInfo PostSwitchAutoRecordBaselineCapturedField =
            ParsekFlightPostSwitchAutoRecordStateType?.GetField("BaselineCaptured",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PostSwitchAutoRecordComparisonsReadyUtField =
            ParsekFlightPostSwitchAutoRecordStateType?.GetField("ComparisonsReadyUt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WheelDeploymentStateStringField =
            typeof(ModuleWheels.ModuleWheelDeployment).GetField("stateString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo WheelDeploymentStateStringProperty =
            typeof(ModuleWheels.ModuleWheelDeployment).GetProperty("stateString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo VesselSetPositionMethod =
            typeof(Vessel).GetMethod("SetPosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Vector3d) },
                null);
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
        public IEnumerator TimeScalePositive()
        {
            var samples = new List<TimeScalePositiveProbeSample>(TimeScalePositiveProbeFrames);
            for (int i = 0; i < TimeScalePositiveProbeFrames; i++)
            {
                TimeScalePositiveProbeSample sample = CaptureTimeScalePositiveProbeSample(i);
                samples.Add(sample);
                ParsekLog.Verbose("TestRunner",
                    "TimeScalePositive probe: " + FormatTimeScalePositiveProbeSample(sample));

                TimeScalePositiveProbeOutcome partialOutcome =
                    ClassifyTimeScalePositiveSamples(samples);
                switch (partialOutcome)
                {
                    case TimeScalePositiveProbeOutcome.Passed:
                        if (i > 0)
                        {
                            ParsekLog.Info("TestRunner",
                                "TimeScalePositive recovered after " +
                                (i + 1).ToString(CultureInfo.InvariantCulture) +
                                " frame(s): " +
                                FormatTimeScalePositiveProbeSummary(samples));
                        }
                        yield break;

                    case TimeScalePositiveProbeOutcome.FailZeroWithoutPause:
                        InGameAssert.Fail(
                            "Time.timeScale hit <= " +
                            TimeScalePositiveThreshold.ToString("F2", CultureInfo.InvariantCulture) +
                            " outside stock pause during probe | " +
                            FormatTimeScalePositiveProbeSummary(samples));
                        yield break;
                }

                if (i < TimeScalePositiveProbeFrames - 1)
                    yield return null;
            }

            string summary = FormatTimeScalePositiveProbeSummary(samples);
            switch (ClassifyTimeScalePositiveSamples(samples))
            {
                case TimeScalePositiveProbeOutcome.Passed:
                    yield break;

                case TimeScalePositiveProbeOutcome.SkipStockPause:
                    InGameAssert.Skip(
                        "stock pause menu open; Time.timeScale=0 is expected while paused | " +
                        summary);
                    break;

                case TimeScalePositiveProbeOutcome.SkipPauseProbeUnavailable:
                    InGameAssert.Skip(
                        "FlightDriver.Pause unavailable; cannot confirm stock pause state | " +
                        summary);
                    break;

                default:
                    InGameAssert.Fail(
                        "Time.timeScale hit <= " +
                        TimeScalePositiveThreshold.ToString("F2", CultureInfo.InvariantCulture) +
                        " outside stock pause during probe | " +
                        summary);
                    break;
            }
        }

        internal static TimeScalePositiveProbeOutcome ClassifyTimeScalePositiveSamples(
            IList<TimeScalePositiveProbeSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return TimeScalePositiveProbeOutcome.FailZeroWithoutPause;

            bool sawPositiveTimeScale = false;
            bool sawPauseProbeUnavailable = false;
            bool sawConfirmedStockPause = false;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].TimeScale > TimeScalePositiveThreshold)
                {
                    sawPositiveTimeScale = true;
                    continue;
                }

                if (samples[i].FlightDriverPause == false)
                    return TimeScalePositiveProbeOutcome.FailZeroWithoutPause;

                if (samples[i].FlightDriverPause == true)
                    sawConfirmedStockPause = true;
                else if (!samples[i].FlightDriverPause.HasValue)
                    sawPauseProbeUnavailable = true;
            }

            if (sawConfirmedStockPause)
                return sawPositiveTimeScale
                    ? TimeScalePositiveProbeOutcome.Passed
                    : TimeScalePositiveProbeOutcome.SkipStockPause;

            if (sawPauseProbeUnavailable)
                return TimeScalePositiveProbeOutcome.SkipPauseProbeUnavailable;

            return sawPositiveTimeScale
                ? TimeScalePositiveProbeOutcome.Passed
                : TimeScalePositiveProbeOutcome.SkipStockPause;
        }

        internal static string FormatTimeScalePositiveProbeSample(TimeScalePositiveProbeSample sample)
        {
            return "sample=" + sample.SampleIndex.ToString(CultureInfo.InvariantCulture) +
                   " frame=" + sample.FrameCount.ToString(CultureInfo.InvariantCulture) +
                   " realtime=" + sample.RealtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture) +
                   " timeScale=" + sample.TimeScale.ToString("F3", CultureInfo.InvariantCulture) +
                   " FlightDriver.Pause=" + (sample.FlightDriverPause.HasValue
                       ? sample.FlightDriverPause.Value.ToString()
                       : "unavailable") +
                   " KSPLoader.lastUpdate=" + (sample.KspLoaderLastUpdate ?? "null") +
                   " runner=" + (sample.RunnerCoroutineState ?? "null") +
                   " scene=" + (sample.SceneName ?? "null");
        }

        internal static string FormatTimeScalePositiveProbeSummary(
            IList<TimeScalePositiveProbeSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return "no samples";

            return string.Join(
                " | ",
                samples.Select(FormatTimeScalePositiveProbeSample).ToArray());
        }

        private TimeScalePositiveProbeSample CaptureTimeScalePositiveProbeSample(int sampleIndex)
        {
            return new TimeScalePositiveProbeSample
            {
                SampleIndex = sampleIndex,
                FrameCount = Time.frameCount,
                RealtimeSinceStartup = Time.realtimeSinceStartup,
                TimeScale = Time.timeScale,
                FlightDriverPause = SafeReadFlightDriverPause(),
                KspLoaderLastUpdate = DescribeKspLoaderLastUpdate(),
                RunnerCoroutineState = runner != null ? runner.DescribeCoroutineState() : "runner=null",
                SceneName = HighLogic.LoadedScene.ToString(),
            };
        }

        private static bool? SafeReadFlightDriverPause()
        {
            try
            {
                return FlightDriver.Pause;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("TestRunner",
                    "TimeScalePositive: FlightDriver.Pause unavailable: " + ex.GetType().Name);
                return null;
            }
        }

        private static string DescribeKspLoaderLastUpdate()
        {
            try
            {
                System.Type loaderType = ResolveKspLoaderType();
                if (loaderType == null)
                    return "type-missing";

                FieldInfo field = loaderType.GetField(
                    "lastUpdate",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return "field-missing";

                object value = field.GetValue(null);
                return value != null ? value.ToString() : "null";
            }
            catch (System.Exception ex)
            {
                return "error:" + ex.GetType().Name;
            }
        }

        private static System.Type ResolveKspLoaderType()
        {
            System.Reflection.Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                System.Reflection.Assembly assembly = assemblies[i];
                System.Type direct = assembly.GetType("KSPLoader", false);
                if (direct != null)
                    return direct;

                try
                {
                    System.Type[] types = assembly.GetTypes();
                    for (int t = 0; t < types.Length; t++)
                    {
                        System.Type type = types[t];
                        if (type != null && type.Name == "KSPLoader")
                            return type;
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    System.Type[] types = ex.Types;
                    for (int t = 0; t < types.Length; t++)
                    {
                        System.Type type = types[t];
                        if (type != null && type.Name == "KSPLoader")
                            return type;
                    }
                }
            }

            return null;
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

        #region AutoRecord

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test stages and launches the active vessel. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Launch auto-record starts exactly once when a PRELAUNCH vessel leaves the pad")]
        public IEnumerator AutoRecordOnLaunch_StartsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                InGameAssert.Skip(
                    $"requires a PRELAUNCH active vessel, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle prelaunch vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }
            if (FlightInputHandler.state == null)
            {
                InGameAssert.Skip("FlightInputHandler.state is null");
                yield break;
            }

            bool originalAutoRecord = ParsekSettings.Current.autoRecordOnLaunch;
            float originalThrottle = FlightInputHandler.state.mainThrottle;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;

            try
            {
                ParsekSettings.Current.autoRecordOnLaunch = true;
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                yield return InGameTestRunner.WaitForStockStageManagerReady(10f);
                FlightInputHandler.state.mainThrottle = 1f;
                KSP.UI.Screens.StageManager.ActivateNextStage();

                yield return WaitForLaunchAutoRecordStart(10f);
                yield return new WaitForSeconds(0.5f);

                int autoStartCount = captured.Count(
                    l => l.Contains("[Flight]") && l.Contains("Auto-record started ("));
                InGameAssert.AreEqual(1, autoStartCount,
                    $"Expected exactly one launch auto-record log line, got {autoStartCount}");

                string activeRecId = ParsekFlight.Instance?.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set after launch auto-record starts");

                ParsekLog.Info("TestRunner",
                    $"AutoRecord launch: vessel='{FlightGlobals.ActiveVessel?.vesselName}' " +
                    $"situation={FlightGlobals.ActiveVessel?.situation} activeRecId={activeRecId} " +
                    $"autoStartCount={autoStartCount}");
            }
            finally
            {
                FlightInputHandler.state.mainThrottle = originalThrottle;
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnLaunch = originalAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test forces a crew EVA from the active vessel. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Deferred EVA auto-record starts exactly once after switching to the EVA kerbal")]
        public IEnumerator AutoRecordOnEvaFromPad_StartsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA)
            {
                InGameAssert.Skip("requires a crewed vessel, got EVA");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle crewed vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }
            if (!TryResolveFlightEva(out object flightEva, out string flightEvaSkipReason))
            {
                InGameAssert.Skip(flightEvaSkipReason);
                yield break;
            }
            if (!TryGetEvaSource(vessel, out Part sourcePart, out ProtoCrewMember crewMember,
                out Transform airlock, out string evaSourceSkipReason))
            {
                InGameAssert.Skip(evaSourceSkipReason);
                yield break;
            }

            bool originalAutoRecord = ParsekSettings.Current.autoRecordOnEva;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;

            try
            {
                ParsekSettings.Current.autoRecordOnEva = true;
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                try
                {
                    FlightEvaSpawnMethod.Invoke(flightEva, new object[] { crewMember, sourcePart, airlock, true });
                }
                catch (TargetInvocationException ex)
                {
                    InGameAssert.Fail(
                        $"FlightEVA.spawnEVA threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                        $"{ex.InnerException?.Message ?? ex.Message}");
                }

                yield return WaitForDeferredEvaAutoRecordStart(crewMember.name, 10f);
                yield return new WaitForSeconds(0.5f);

                int autoStartCount = captured.Count(
                    l => l.Contains("[Flight]") && l.Contains("Auto-record started (EVA from pad)"));
                InGameAssert.AreEqual(1, autoStartCount,
                    $"Expected exactly one EVA auto-record log line, got {autoStartCount}");

                var activeVessel = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(activeVessel,
                    "Active vessel should switch to the EVA kerbal before the deferred auto-record starts");
                InGameAssert.IsTrue(activeVessel.isEVA,
                    $"Expected active vessel to be EVA, got {activeVessel?.vesselName ?? "null"}");

                string activeRecId = ParsekFlight.Instance?.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set after deferred EVA auto-record starts");

                ParsekLog.Info("TestRunner",
                    $"AutoRecord EVA: source='{vessel.vesselName}' crew='{crewMember.name}' " +
                    $"active='{activeVessel.vesselName}' activeRecId={activeRecId} " +
                    $"autoStartCount={autoStartCount}");
            }
            finally
            {
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnEva = originalAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test simulates an idle post-switch watch on the active landed vessel and nudges the craft to verify the LANDED-motion trigger. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Post-switch landed motion auto-record starts exactly once while the vessel stays LANDED")]
        public IEnumerator AutoRecordOnPostSwitch_LandedMotion_StartsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA landed vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.LANDED)
            {
                InGameAssert.Skip(
                    $"requires a LANDED active vessel for the post-switch landed-motion canary, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle landed vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalPostSwitchAutoRecord = ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            try
            {
                ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                TryDisarmPostSwitchAutoRecord(flight, "runtime landed-motion canary setup");
                if (!TrySimulatePostSwitchArm(flight, vessel, out string armSkipReason))
                {
                    InGameAssert.Skip(armSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchBaselineCapture(flight, vessel, 3f);
                yield return WaitForPostSwitchComparisonsReady(flight, vessel, 8f);

                InGameAssert.IsFalse(flight.IsRecording,
                    "The post-switch landed-motion canary must stay idle until the vessel actually moves");

                if (!TryInduceLandedMotion(vessel, out string motionSkipReason))
                {
                    InGameAssert.Skip(motionSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchAutoRecordStart(5f);
                yield return new WaitForSeconds(0.5f);

                int autoStartCount = CountPostSwitchAutoStartLogLines(captured);
                InGameAssert.AreEqual(1, autoStartCount,
                    $"Expected exactly one post-switch auto-start log line, got {autoStartCount}");

                Vessel postStartVessel = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(postStartVessel,
                    "Active vessel should still exist after the landed-motion post-switch auto-start");
                InGameAssert.AreEqual(Vessel.Situations.LANDED, postStartVessel.situation,
                    $"Expected landed-motion canary to stay LANDED, got {postStartVessel.situation}");

                ParsekLog.Info("TestRunner",
                    $"Post-switch landed-motion auto-record: vessel='{postStartVessel.vesselName}' " +
                    $"situation={postStartVessel.situation} autoStartCount={autoStartCount}");
            }
            finally
            {
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch =
                        originalPostSwitchAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();
                TryDisarmPostSwitchAutoRecord(cleanupFlight, "runtime landed-motion canary cleanup");
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test simulates an idle post-switch watch on the active orbital vessel and then forces engine or sustained-RCS activity to verify the no-situation-change trigger. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Post-switch orbital engine or sustained RCS auto-record starts exactly once without a situation change")]
        public IEnumerator AutoRecordOnPostSwitch_OrbitalEngineOrRcs_StartsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA orbital vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.ORBITING)
            {
                InGameAssert.Skip(
                    $"requires an ORBITING active vessel for the post-switch orbital canary, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle orbital vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalPostSwitchAutoRecord = ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            System.Action cleanupActivity = null;

            try
            {
                ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                TryDisarmPostSwitchAutoRecord(flight, "runtime orbital canary setup");
                if (!TrySimulatePostSwitchArm(flight, vessel, out string armSkipReason))
                {
                    InGameAssert.Skip(armSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchBaselineCapture(flight, vessel, 3f);

                Vessel.Situations initialSituation = vessel.situation;
                if (!TryInduceEngineOrSustainedRcsActivity(
                        vessel,
                        out cleanupActivity,
                        out string activityMode,
                        out string activitySkipReason))
                {
                    InGameAssert.Skip(activitySkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchAutoRecordStart(5f);
                yield return new WaitForSeconds(0.5f);

                int autoStartCount = CountPostSwitchAutoStartLogLines(captured);
                InGameAssert.AreEqual(1, autoStartCount,
                    $"Expected exactly one post-switch auto-start log line, got {autoStartCount}");

                Vessel postStartVessel = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(postStartVessel,
                    "Active vessel should still exist after the orbital post-switch auto-start");
                InGameAssert.AreEqual(initialSituation, postStartVessel.situation,
                    $"Expected orbital post-switch {activityMode} canary to keep situation {initialSituation}, got {postStartVessel.situation}");

                ParsekLog.Info("TestRunner",
                    $"Post-switch orbital auto-record: vessel='{postStartVessel.vesselName}' " +
                    $"mode={activityMode} situation={postStartVessel.situation} autoStartCount={autoStartCount}");
            }
            finally
            {
                cleanupActivity?.Invoke();
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch =
                        originalPostSwitchAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();
                TryDisarmPostSwitchAutoRecord(cleanupFlight, "runtime orbital canary cleanup");
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test simulates an idle post-switch watch on the active landed vessel and toggles landing gear to verify a non-cosmetic part-state trigger. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Post-switch gear-toggle auto-record starts exactly once on a non-cosmetic part-state change")]
        public IEnumerator AutoRecordOnPostSwitch_GearToggle_StartsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA landed vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.LANDED)
            {
                InGameAssert.Skip(
                    $"requires a LANDED active vessel for the post-switch gear-toggle canary, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle landed vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalPostSwitchAutoRecord = ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            System.Action cleanupGear = null;

            try
            {
                ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                TryDisarmPostSwitchAutoRecord(flight, "runtime gear-toggle canary setup");
                if (!TrySimulatePostSwitchArm(flight, vessel, out string armSkipReason))
                {
                    InGameAssert.Skip(armSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchBaselineCapture(flight, vessel, 3f);
                yield return WaitForPostSwitchComparisonsReady(flight, vessel, 8f);

                if (!TryToggleLandingGear(vessel, out cleanupGear, out string gearSkipReason))
                {
                    InGameAssert.Skip(gearSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchAutoRecordStart(5f);
                yield return new WaitForSeconds(0.5f);

                int autoStartCount = CountPostSwitchAutoStartLogLines(captured);
                InGameAssert.AreEqual(1, autoStartCount,
                    $"Expected exactly one post-switch auto-start log line, got {autoStartCount}");

                ParsekLog.Info("TestRunner",
                    $"Post-switch gear-toggle auto-record: vessel='{FlightGlobals.ActiveVessel?.vesselName}' " +
                    $"autoStartCount={autoStartCount}");
            }
            finally
            {
                cleanupGear?.Invoke();
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch =
                        originalPostSwitchAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();
                TryDisarmPostSwitchAutoRecord(cleanupFlight, "runtime gear-toggle canary cleanup");
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test simulates an idle post-switch watch on the active vessel and then intentionally does nothing to verify the negative case. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Post-switch idle no-op does not auto-start a recording")]
        public IEnumerator AutoRecordOnPostSwitch_NoOp_DoesNotStart()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle active vessel (recording already active)");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalPostSwitchAutoRecord = ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            try
            {
                ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                TryDisarmPostSwitchAutoRecord(flight, "runtime no-op canary setup");
                if (!TrySimulatePostSwitchArm(flight, vessel, out string armSkipReason))
                {
                    InGameAssert.Skip(armSkipReason);
                    yield break;
                }

                yield return WaitForPostSwitchBaselineCapture(flight, vessel, 3f);
                if (vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED)
                    yield return WaitForPostSwitchComparisonsReady(flight, vessel, 8f);

                float idleDurationSeconds =
                    vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED
                        ? 1.0f
                        : 1.5f;
                yield return WaitForPostSwitchIdleNoStart(vessel, idleDurationSeconds);

                int autoStartCount = CountPostSwitchAutoStartLogLines(captured);
                InGameAssert.AreEqual(0, autoStartCount,
                    $"Expected no post-switch auto-start log lines, got {autoStartCount}");
                InGameAssert.IsFalse(ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording,
                    "No-op post-switch canary must stay idle when nothing meaningful changes");
                InGameAssert.IsTrue(ParsekFlight.Instance != null
                        && ParsekFlight.Instance.IsPostSwitchAutoRecordArmedForPid(vessel.persistentId),
                    "No-op post-switch canary should remain armed after an idle wait");

                ParsekLog.Info("TestRunner",
                    $"Post-switch no-op auto-record canary stayed idle for vessel '{vessel.vesselName}'");
            }
            finally
            {
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnFirstModificationAfterSwitch =
                        originalPostSwitchAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();
                TryDisarmPostSwitchAutoRecord(cleanupFlight, "runtime no-op canary cleanup");
            }
        }

        internal static IEnumerator WaitForLaunchAutoRecordStart(float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                var vessel = FlightGlobals.ActiveVessel;
                if (flight != null
                    && flight.IsRecording
                    && vessel != null
                    && vessel.situation != Vessel.Situations.PRELAUNCH)
                {
                    yield break;
                }

                yield return null;
            }

            var timedOutFlight = ParsekFlight.Instance;
            var timedOutVessel = FlightGlobals.ActiveVessel;
            InGameAssert.Fail(
                $"WaitForLaunchAutoRecordStart timed out after {timeoutSeconds:F0}s " +
                $"(parsekFlight={(timedOutFlight != null)}, " +
                $"isRecording={timedOutFlight?.IsRecording == true}, " +
                $"activeVessel='{timedOutVessel?.vesselName ?? "null"}', " +
                $"situation={timedOutVessel?.situation.ToString() ?? "null"})");
        }

        internal static bool HasObservedActiveRecordingPoint(
            ParsekFlight flight,
            out int treePointCount,
            out int bufferedPointCount,
            out double lastRecordedUT)
        {
            treePointCount = 0;
            bufferedPointCount = 0;
            lastRecordedUT = double.NaN;

            if (flight == null)
                return false;

            var activeTree = flight.ActiveTreeForSerialization;
            string activeRecId = activeTree?.ActiveRecordingId;
            if (activeTree != null
                && !string.IsNullOrEmpty(activeRecId)
                && activeTree.Recordings.TryGetValue(activeRecId, out var rec)
                && rec?.Points != null)
            {
                treePointCount = rec.Points.Count;
            }

            var activeRecorder = flight.ActiveRecorderForSerialization;
            if (activeRecorder != null)
            {
                bufferedPointCount = activeRecorder.Recording?.Count ?? 0;
                lastRecordedUT = activeRecorder.LastRecordedUT;
            }

            return treePointCount > 0
                || bufferedPointCount > 0
                || !double.IsNaN(lastRecordedUT);
        }

        internal static IEnumerator WaitForActiveRecordingPoint(ParsekFlight flight, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (HasObservedActiveRecordingPoint(
                    flight,
                    out _,
                    out _,
                    out _))
                {
                    yield break;
                }

                yield return null;
                flight = ParsekFlight.Instance;
            }

            string activeRecId = flight?.ActiveTreeForSerialization?.ActiveRecordingId;
            HasObservedActiveRecordingPoint(flight, out int treePointCount, out int bufferedPointCount, out double lastRecordedUT);

            InGameAssert.Fail(
                $"WaitForActiveRecordingPoint timed out after {timeoutSeconds:F0}s " +
                $"(parsekFlight={(flight != null)}, activeRecId={activeRecId ?? "null"}, " +
                $"treePoints={treePointCount}, bufferedPoints={bufferedPointCount}, " +
                $"lastRecordedUT={(double.IsNaN(lastRecordedUT) ? "NaN" : lastRecordedUT.ToString("F2"))})");
        }

        internal static IEnumerator WaitForFlightInputStateReady(float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            float stableStarted = -1f;
            while (Time.time < deadline)
            {
                bool ready = FlightInputHandler.state != null;
                if (ready)
                {
                    if (stableStarted < 0f)
                    {
                        stableStarted = Time.unscaledTime;
                    }
                    else if ((Time.unscaledTime - stableStarted) >= 0.5f)
                    {
                        yield break;
                    }
                }
                else
                {
                    stableStarted = -1f;
                }

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForFlightInputStateReady timed out after {timeoutSeconds:F0}s " +
                $"(hasFlightInput={FlightInputHandler.state != null})");
        }

        private static IEnumerator WaitForDeferredEvaAutoRecordStart(string expectedCrewName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                var vessel = FlightGlobals.ActiveVessel;
                if (flight != null
                    && flight.IsRecording
                    && vessel != null
                    && vessel.isEVA
                    && (string.IsNullOrEmpty(expectedCrewName)
                        || vessel.vesselName == expectedCrewName
                        || vessel.vesselName.Contains(expectedCrewName)))
                {
                    yield break;
                }

                yield return null;
            }

            var timedOutFlight = ParsekFlight.Instance;
            var timedOutVessel = FlightGlobals.ActiveVessel;
            InGameAssert.Fail(
                $"WaitForDeferredEvaAutoRecordStart timed out after {timeoutSeconds:F0}s " +
                $"(parsekFlight={(timedOutFlight != null)}, " +
                $"isRecording={timedOutFlight?.IsRecording == true}, " +
                $"activeVessel='{timedOutVessel?.vesselName ?? "null"}', " +
                $"isEva={timedOutVessel?.isEVA == true})");
        }

        private static bool TrySimulatePostSwitchArm(ParsekFlight flight, Vessel vessel, out string skipReason)
        {
            skipReason = null;

            if (flight == null)
            {
                skipReason = "ParsekFlight.Instance is null";
                return false;
            }
            if (vessel == null)
            {
                skipReason = "no active vessel";
                return false;
            }
            if (ParsekFlightOnVesselSwitchCompleteMethod == null)
            {
                skipReason = "ParsekFlight.OnVesselSwitchComplete reflection surface unavailable";
                return false;
            }

            try
            {
                ParsekFlightOnVesselSwitchCompleteMethod.Invoke(flight, new object[] { vessel });
            }
            catch (TargetInvocationException ex)
            {
                skipReason =
                    $"ParsekFlight.OnVesselSwitchComplete threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                    $"{ex.InnerException?.Message ?? ex.Message}";
                return false;
            }

            if (!flight.IsPostSwitchAutoRecordArmedForPid(vessel.persistentId))
            {
                skipReason =
                    $"Post-switch watch did not arm for vessel '{vessel.vesselName}' pid={vessel.persistentId}";
                return false;
            }

            return true;
        }

        private static void TryDisarmPostSwitchAutoRecord(ParsekFlight flight, string reason)
        {
            if (flight == null || ParsekFlightDisarmPostSwitchAutoRecordMethod == null)
                return;

            try
            {
                ParsekFlightDisarmPostSwitchAutoRecordMethod.Invoke(flight, new object[] { reason });
            }
            catch
            {
            }
        }

        private static bool TryGetPostSwitchWatchState(
            ParsekFlight flight,
            out bool isArmed,
            out bool baselineCaptured,
            out double comparisonsReadyUt)
        {
            isArmed = false;
            baselineCaptured = false;
            comparisonsReadyUt = double.NaN;

            if (flight == null || ParsekFlightPostSwitchAutoRecordField == null)
                return false;

            object state = ParsekFlightPostSwitchAutoRecordField.GetValue(flight);
            if (state == null)
                return false;

            isArmed = true;
            if (PostSwitchAutoRecordBaselineCapturedField != null)
                baselineCaptured = (bool)PostSwitchAutoRecordBaselineCapturedField.GetValue(state);
            if (PostSwitchAutoRecordComparisonsReadyUtField != null)
            {
                object readyValue = PostSwitchAutoRecordComparisonsReadyUtField.GetValue(state);
                if (readyValue is double readyUt)
                    comparisonsReadyUt = readyUt;
            }

            return true;
        }

        private static IEnumerator WaitForPostSwitchBaselineCapture(
            ParsekFlight flight,
            Vessel vessel,
            float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                flight = ParsekFlight.Instance;
                vessel = FlightGlobals.ActiveVessel;
                if (flight != null && vessel != null)
                    flight.OnPostSwitchAutoRecordPhysicsFrame(vessel);

                if (TryGetPostSwitchWatchState(
                        flight,
                        out bool isArmed,
                        out bool baselineCaptured,
                        out double comparisonsReadyUt)
                    && isArmed
                    && baselineCaptured)
                {
                    ParsekLog.Verbose("TestRunner",
                        $"Post-switch baseline captured in runtime canary: readyAt={comparisonsReadyUt:F2}");
                    yield break;
                }

                yield return new WaitForFixedUpdate();
            }

            TryGetPostSwitchWatchState(
                ParsekFlight.Instance,
                out bool armed,
                out bool captured,
                out double readyAtTimedOut);
            InGameAssert.Fail(
                $"WaitForPostSwitchBaselineCapture timed out after {timeoutSeconds:F0}s " +
                $"(armed={armed}, baselineCaptured={captured}, readyAt={readyAtTimedOut.ToString("F2", CultureInfo.InvariantCulture)}, " +
                $"isRecording={ParsekFlight.Instance?.IsRecording == true}, " +
                $"activeVessel='{FlightGlobals.ActiveVessel?.vesselName ?? "null"}')");
        }

        private static IEnumerator WaitForPostSwitchComparisonsReady(
            ParsekFlight flight,
            Vessel vessel,
            float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                flight = ParsekFlight.Instance;
                vessel = FlightGlobals.ActiveVessel;
                if (flight != null && vessel != null)
                    flight.OnPostSwitchAutoRecordPhysicsFrame(vessel);

                if (TryGetPostSwitchWatchState(
                        flight,
                        out bool isArmed,
                        out bool baselineCaptured,
                        out double comparisonsReadyUt)
                    && isArmed
                    && baselineCaptured
                    && !double.IsNaN(comparisonsReadyUt)
                    && Planetarium.GetUniversalTime() >= comparisonsReadyUt)
                {
                    yield break;
                }

                yield return new WaitForFixedUpdate();
            }

            TryGetPostSwitchWatchState(
                ParsekFlight.Instance,
                out bool armed,
                out bool captured,
                out double readyAtTimedOut);
            InGameAssert.Fail(
                $"WaitForPostSwitchComparisonsReady timed out after {timeoutSeconds:F0}s " +
                $"(armed={armed}, baselineCaptured={captured}, readyAt={readyAtTimedOut.ToString("F2", CultureInfo.InvariantCulture)}, " +
                $"now={Planetarium.GetUniversalTime().ToString("F2", CultureInfo.InvariantCulture)})");
        }

        private static IEnumerator WaitForPostSwitchAutoRecordStart(float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                var vessel = FlightGlobals.ActiveVessel;
                if (flight != null && vessel != null)
                    flight.OnPostSwitchAutoRecordPhysicsFrame(vessel);

                if (flight != null && flight.IsRecording)
                    yield break;

                yield return new WaitForFixedUpdate();
            }

            var timedOutFlight = ParsekFlight.Instance;
            var timedOutVessel = FlightGlobals.ActiveVessel;
            string activeRecId = timedOutFlight?.ActiveTreeForSerialization?.ActiveRecordingId;
            InGameAssert.Fail(
                $"WaitForPostSwitchAutoRecordStart timed out after {timeoutSeconds:F0}s " +
                $"(parsekFlight={(timedOutFlight != null)}, isRecording={timedOutFlight?.IsRecording == true}, " +
                $"activeRecId={activeRecId ?? "null"}, activeVessel='{timedOutVessel?.vesselName ?? "null"}', " +
                $"situation={timedOutVessel?.situation.ToString() ?? "null"})");
        }

        private static IEnumerator WaitForPostSwitchIdleNoStart(Vessel vessel, float durationSeconds)
        {
            float deadline = Time.time + durationSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                vessel = FlightGlobals.ActiveVessel ?? vessel;
                if (flight != null && vessel != null)
                    flight.OnPostSwitchAutoRecordPhysicsFrame(vessel);

                InGameAssert.IsFalse(flight != null && flight.IsRecording,
                    "Idle post-switch wait should not start recording before the negative-case assertion");
                yield return new WaitForFixedUpdate();
            }
        }

        internal static IEnumerator WaitForTimeJumpLaunchAutoRecordTransientToClear(float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                bool suppressed = TimeJumpManager.IsTimeJumpLaunchAutoRecordSuppressed(
                    TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress,
                    Time.frameCount,
                    TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame);
                if (!suppressed)
                {
                    yield return new WaitForFixedUpdate();
                    yield break;
                }

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForTimeJumpLaunchAutoRecordTransientToClear timed out after {timeoutSeconds:F0}s " +
                $"(inProgress={TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress}, frame={Time.frameCount}, " +
                $"suppressUntilFrame={TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame})");
        }

        internal static int CountAnyAutoRecordStartLogLines(List<string> captured)
        {
            if (captured == null)
                return 0;

            return captured.Count(
                line => line.Contains("[Flight]")
                    && line.Contains("Auto-record started ("));
        }

        internal static int CountTimeJumpTransientSkipLogLines(List<string> captured)
        {
            if (captured == null)
                return 0;

            return captured.Count(
                line => line.Contains("[INFO][Flight]")
                    && line.Contains("suppressing time-jump transient"));
        }

        private static int CountPostSwitchAutoStartLogLines(List<string> captured)
        {
            if (captured == null)
                return 0;

            return captured.Count(
                line => line.Contains("[Flight]")
                    && line.Contains("Auto-record started (post-switch "));
        }

        private static bool TryInduceLandedMotion(Vessel vessel, out string skipReason)
        {
            skipReason = null;
            if (vessel == null)
            {
                skipReason = "no active vessel";
                return false;
            }

            Part rootPart = vessel.rootPart;
            if (rootPart == null)
            {
                skipReason = "active vessel has no rootPart";
                return false;
            }

            Vector3 direction = rootPart.transform != null ? rootPart.transform.right : Vector3.right;
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.right;
            direction.Normalize();

            Vector3d delta = new Vector3d(direction.x, direction.y, direction.z);
            Vector3d targetPosition = vessel.GetWorldPos3D() + delta;

            if (VesselSetPositionMethod != null)
            {
                VesselSetPositionMethod.Invoke(vessel, new object[] { targetPosition });
                return true;
            }

            if (rootPart.rb != null)
            {
                rootPart.rb.position += direction;
                return true;
            }

            if (rootPart.transform != null)
            {
                rootPart.transform.position += direction;
                return true;
            }

            skipReason = "could not find a writable position surface for the landed vessel";
            return false;
        }

        private static bool TryInduceEngineOrSustainedRcsActivity(
            Vessel vessel,
            out System.Action cleanup,
            out string activityMode,
            out string skipReason)
        {
            cleanup = null;
            activityMode = null;
            skipReason = null;

            if (vessel == null || vessel.parts == null)
            {
                skipReason = "active vessel is missing parts";
                return false;
            }

            float? originalMainThrottle = null;
            if (FlightInputHandler.state != null)
            {
                originalMainThrottle = FlightInputHandler.state.mainThrottle;
                FlightInputHandler.state.mainThrottle = 1f;
            }

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part == null)
                    continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    if (!(part.Modules[m] is ModuleEngines engine) || engine.EngineIgnited)
                        continue;

                    bool originalIgnited = engine.EngineIgnited;
                    bool activated =
                        TryInvokeAnyMethod(engine, "Activate", "ActivateAction", "ActionActivate")
                        || TrySetMemberValue(engine, "EngineIgnited", true);
                    if (!activated)
                        continue;

                    cleanup = () =>
                    {
                        TryInvokeAnyMethod(engine, "Shutdown", "ShutdownAction", "ActionShutdown");
                        TrySetMemberValue(engine, "EngineIgnited", originalIgnited);
                        if (originalMainThrottle.HasValue && FlightInputHandler.state != null)
                            FlightInputHandler.state.mainThrottle = originalMainThrottle.Value;
                    };
                    activityMode = "engine";
                    return true;
                }
            }

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part == null)
                    continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    if (!(part.Modules[m] is ModuleRCS rcs))
                        continue;
                    if (rcs.thrustForces == null || !rcs.thrustForces.Any())
                        continue;
                    if (rcs.rcs_active)
                        continue;

                    bool originalEnabled = rcs.rcsEnabled;
                    bool originalActive = rcs.rcs_active;
                    float originalPower = rcs.thrusterPower;

                    rcs.rcsEnabled = true;
                    rcs.rcs_active = true;
                    if (rcs.thrusterPower <= 0f)
                        rcs.thrusterPower = 1f;

                    cleanup = () =>
                    {
                        rcs.rcsEnabled = originalEnabled;
                        rcs.rcs_active = originalActive;
                        rcs.thrusterPower = originalPower;
                        if (originalMainThrottle.HasValue && FlightInputHandler.state != null)
                            FlightInputHandler.state.mainThrottle = originalMainThrottle.Value;
                    };
                    activityMode = "sustained RCS";
                    return true;
                }
            }

            if (originalMainThrottle.HasValue && FlightInputHandler.state != null)
                FlightInputHandler.state.mainThrottle = originalMainThrottle.Value;

            skipReason = "active orbital vessel has no idle engine or RCS module that the canary can drive";
            return false;
        }

        private static bool TryToggleLandingGear(
            Vessel vessel,
            out System.Action cleanup,
            out string skipReason)
        {
            cleanup = null;
            skipReason = null;

            if (vessel == null || vessel.parts == null)
            {
                skipReason = "active vessel is missing parts";
                return false;
            }

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part == null)
                    continue;

                ModuleWheels.ModuleWheelDeployment wheel =
                    part.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>();
                if (wheel == null)
                    continue;

                string originalState = GetGearStateString(wheel);
                if (string.IsNullOrEmpty(originalState))
                    continue;

                FlightRecorder.ClassifyGearState(
                    originalState,
                    out bool isDeployed,
                    out bool isRetracted);
                if (!isDeployed && !isRetracted)
                    continue;

                string targetState = isDeployed ? "Retracted" : "Deployed";
                bool toggled =
                    (isDeployed
                        && TryInvokeAnyMethod(wheel, "Retract", "RetractAction", "ActionRetract", "ActionToggle", "Toggle"))
                    || (!isDeployed
                        && TryInvokeAnyMethod(wheel, "Extend", "Deploy", "DeployAction", "ActionDeploy", "ActionToggle", "Toggle"))
                    || TrySetGearStateString(wheel, targetState);
                if (!toggled)
                    continue;

                cleanup = () => TrySetGearStateString(wheel, originalState);
                return true;
            }

            skipReason = "active landed vessel has no deployable landing-gear module the canary can toggle";
            return false;
        }

        private static string GetGearStateString(ModuleWheels.ModuleWheelDeployment wheel)
        {
            if (wheel == null)
                return null;

            if (WheelDeploymentStateStringProperty != null)
                return WheelDeploymentStateStringProperty.GetValue(wheel, null) as string;
            if (WheelDeploymentStateStringField != null)
                return WheelDeploymentStateStringField.GetValue(wheel) as string;
            return wheel.stateString;
        }

        private static bool TrySetGearStateString(
            ModuleWheels.ModuleWheelDeployment wheel,
            string stateString)
        {
            if (wheel == null)
                return false;

            if (WheelDeploymentStateStringProperty != null && WheelDeploymentStateStringProperty.CanWrite)
            {
                WheelDeploymentStateStringProperty.SetValue(wheel, stateString, null);
                return true;
            }
            if (WheelDeploymentStateStringField != null)
            {
                WheelDeploymentStateStringField.SetValue(wheel, stateString);
                return true;
            }

            return false;
        }

        private static bool TryInvokeAnyMethod(object target, params string[] methodNames)
        {
            if (target == null || methodNames == null)
                return false;

            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            for (int i = 0; i < methodNames.Length; i++)
            {
                string methodName = methodNames[i];
                if (string.IsNullOrEmpty(methodName))
                    continue;

                MethodInfo method = target.GetType().GetMethod(
                    methodName,
                    Flags,
                    null,
                    System.Type.EmptyTypes,
                    null);
                if (method == null)
                    continue;

                try
                {
                    method.Invoke(target, null);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetMemberValue(object target, string memberName, object value)
        {
            if (target == null || string.IsNullOrEmpty(memberName))
                return false;

            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo property = target.GetType().GetProperty(memberName, Flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value, null);
                return true;
            }

            FieldInfo field = target.GetType().GetField(memberName, Flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }

            return false;
        }

        private static bool TryResolveFlightEva(out object flightEva, out string skipReason)
        {
            flightEva = null;
            skipReason = null;

            if (FlightEvaType == null)
            {
                skipReason = "FlightEVA type is unavailable";
                return false;
            }
            if (FlightEvaFetchField == null)
            {
                skipReason = "FlightEVA.fetch field is not reflectable";
                return false;
            }
            if (FlightEvaSpawnMethod == null)
            {
                skipReason = "FlightEVA.spawnEVA(ProtoCrewMember, Part, Transform, bool) is not reflectable";
                return false;
            }

            flightEva = FlightEvaFetchField.GetValue(null);
            if (flightEva == null)
            {
                skipReason = "FlightEVA.fetch is null";
                return false;
            }

            return true;
        }

        private static bool TryGetEvaSource(Vessel vessel, out Part sourcePart,
            out ProtoCrewMember crewMember, out Transform airlock, out string skipReason)
        {
            sourcePart = null;
            crewMember = null;
            airlock = null;
            skipReason = null;

            if (vessel == null)
            {
                skipReason = "no active vessel";
                return false;
            }
            if (PartAirlockField == null)
            {
                skipReason = "Part.airlock field is not reflectable";
                return false;
            }

            foreach (Part part in vessel.parts)
            {
                if (part?.protoModuleCrew == null || part.protoModuleCrew.Count == 0)
                    continue;

                Transform partAirlock = PartAirlockField.GetValue(part) as Transform;
                if (partAirlock == null)
                    continue;

                ProtoCrewMember firstCrew = part.protoModuleCrew[0];
                if (firstCrew == null)
                    continue;

                sourcePart = part;
                crewMember = firstCrew;
                airlock = partAirlock;
                return true;
            }

            skipReason = $"active vessel '{vessel.vesselName}' has no crewed part with an airlock";
            return false;
        }

        #endregion

        #region MergeDialog

        [InGameTest(Category = "MergeDialog", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test fabricates a pending tree and drives the live merge popup in the current FLIGHT session. Use Run All + Isolated or the row play button.",
            Description = "Tree merge popup shows both actions and the discard button clears the pending tree")]
        public IEnumerator TreeMergeDialog_DiscardButton_ClearsPendingTree()
        {
            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MultiOptionDialogTitleField == null
                || MultiOptionDialogOptionsField == null
                || DialogGuiButtonTextField == null
                || DialogGuiButtonOptionSelectedMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            bool originalMergeDialogPending = ParsekScenario.MergeDialogPending;
            RecordingTree tree = BuildSyntheticPendingTree("ingame-merge-dialog-discard");

            try
            {
                PopupDialog.DismissPopup("ParsekMerge");
                RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
                ParsekScenario.MergeDialogPending = true;

                MergeDialog.ShowTreeDialog(tree);

                yield return WaitForPopupDialog("ParsekMerge", 2f);

                PopupDialog popup = FindPopupDialog("ParsekMerge");
                InGameAssert.IsNotNull(popup, "ParsekMerge popup should exist after ShowTreeDialog");

                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                InGameAssert.IsNotNull(dialog, "Popup should expose a MultiOptionDialog");

                string dialogTitle = MultiOptionDialogTitleField.GetValue(dialog) as string;
                InGameAssert.AreEqual("Parsek - Merge to Timeline", dialogTitle,
                    "Merge dialog title should match the production popup");

                DialogGUIButton[] buttons = GetDialogButtons(dialog);
                InGameAssert.AreEqual(2, buttons.Length,
                    $"Expected exactly two merge dialog buttons, got {buttons.Length}");

                DialogGUIButton mergeButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Merge to Timeline");
                DialogGUIButton discardButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Discard");

                InGameAssert.IsNotNull(mergeButton, "Merge to Timeline button should exist");
                InGameAssert.IsNotNull(discardButton, "Discard button should exist");

                DialogGuiButtonOptionSelectedMethod.Invoke(discardButton, null);

                yield return WaitForPopupDialogToClose("ParsekMerge", 2f);

                InGameAssert.IsFalse(RecordingStore.HasPendingTree,
                    "Discard button should clear the pending tree");
                InGameAssert.IsFalse(ParsekScenario.MergeDialogPending,
                    "Discard button should clear the deferred merge-dialog flag");

                ParsekLog.Info("TestRunner",
                    $"Merge dialog discard runtime: tree='{tree.TreeName}' buttons={buttons.Length}");
            }
            finally
            {
                PopupDialog.DismissPopup("ParsekMerge");
                if (RecordingStore.HasPendingTree && object.ReferenceEquals(RecordingStore.PendingTree, tree))
                    RecordingStore.DiscardPendingTree();
                ParsekScenario.MergeDialogPending = originalMergeDialogPending;
            }
        }

        [InGameTest(Category = "MergeDialog", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test fabricates a pending tree, drives the deferred FLIGHT merge popup, and commits synthetic timeline state into the current session before cleaning it up. Use Run All + Isolated or the row play button.",
            Description = "Deferred merge popup commits a pending tree through the real Merge to Timeline path")]
        public IEnumerator TreeMergeDialog_DeferredMergeButton_CommitsPendingTree()
        {
            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MultiOptionDialogTitleField == null
                || MultiOptionDialogOptionsField == null
                || DialogGuiButtonTextField == null
                || DialogGuiButtonOptionSelectedMethod == null
                || ParsekScenarioShowDeferredMergeDialogMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            ParsekScenario scenario = Object.FindObjectOfType<ParsekScenario>();
            if (scenario == null)
            {
                InGameAssert.Skip("ParsekScenario instance is unavailable in FLIGHT");
                yield break;
            }

            bool originalMergeDialogPending = ParsekScenario.MergeDialogPending;
            RecordingTree tree = BuildSyntheticPendingTree("ingame-deferred-merge-commit");

            try
            {
                PopupDialog.DismissPopup("ParsekMerge");
                RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
                ParsekScenario.MergeDialogPending = true;

                IEnumerator deferredDialog = ParsekScenarioShowDeferredMergeDialogMethod.Invoke(
                    scenario, null) as IEnumerator;
                InGameAssert.IsNotNull(deferredDialog,
                    "ShowDeferredMergeDialog should return an IEnumerator");
                scenario.StartCoroutine(deferredDialog);

                yield return WaitForPopupDialog("ParsekMerge", 8f);

                PopupDialog popup = FindPopupDialog("ParsekMerge");
                InGameAssert.IsNotNull(popup,
                    "ParsekMerge popup should exist after deferred merge dialog starts");

                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                InGameAssert.IsNotNull(dialog, "Popup should expose a MultiOptionDialog");

                string dialogTitle = MultiOptionDialogTitleField.GetValue(dialog) as string;
                InGameAssert.AreEqual("Parsek - Merge to Timeline", dialogTitle,
                    "Deferred merge dialog title should match the production popup");

                DialogGUIButton[] buttons = GetDialogButtons(dialog);
                DialogGUIButton mergeButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Merge to Timeline");
                DialogGUIButton discardButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Discard");

                InGameAssert.IsNotNull(mergeButton, "Merge to Timeline button should exist");
                InGameAssert.IsNotNull(discardButton, "Discard button should exist");

                DialogGuiButtonOptionSelectedMethod.Invoke(mergeButton, null);

                yield return WaitForPopupDialogToClose("ParsekMerge", 3f);
                yield return null;

                InGameAssert.IsFalse(RecordingStore.HasPendingTree,
                    "Merge to Timeline should consume the pending tree");
                InGameAssert.IsFalse(ParsekScenario.MergeDialogPending,
                    "Merge to Timeline should clear the deferred merge-dialog flag");

                RecordingTree committedTree = RecordingStore.CommittedTrees.FirstOrDefault(
                    candidate => candidate != null && candidate.Id == tree.Id);
                InGameAssert.IsNotNull(committedTree,
                    "Deferred Merge to Timeline should commit the synthetic tree");

                bool recordingCommitted = RecordingStore.CommittedRecordings.Any(
                    rec => rec != null && rec.TreeId == tree.Id && rec.RecordingId == tree.ActiveRecordingId);
                InGameAssert.IsTrue(recordingCommitted,
                    "CommittedRecordings should contain the merged tree's active recording");

                ParsekLog.Info("TestRunner",
                    $"Deferred merge runtime: committed tree='{tree.TreeName}' id={tree.Id}");
            }
            finally
            {
                PopupDialog.DismissPopup("ParsekMerge");
                if (RecordingStore.HasPendingTree && object.ReferenceEquals(RecordingStore.PendingTree, tree))
                    RecordingStore.DiscardPendingTree();
                RemoveCommittedTreeByIdForRuntimeTest(tree.Id);
                ParsekScenario.MergeDialogPending = originalMergeDialogPending;
            }
        }

        private static RecordingTree BuildSyntheticPendingTree(string suffix)
        {
            string treeId = "runtime-tree-" + suffix;
            string recordingId = "runtime-rec-" + suffix;
            double startUt = Planetarium.GetUniversalTime();
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            string vesselName = activeVessel?.vesselName ?? "Runtime Test Vessel";
            CelestialBody body = activeVessel?.mainBody;
            string bodyName = body?.bodyName ?? "Kerbin";
            double baseLatitude = activeVessel != null ? activeVessel.latitude : 0.0;
            double baseLongitude = activeVessel != null ? activeVessel.longitude : 0.0;
            double bodyRadius = body != null && body.Radius > 0.0 ? body.Radius : 600000.0;
            double degreesPerMeter = 180.0 / (System.Math.PI * bodyRadius);
            double firstLatitude = baseLatitude + 40.0 * degreesPerMeter;
            double secondLatitude = baseLatitude + 80.0 * degreesPerMeter;

            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                TreeId = treeId,
                VesselPersistentId = 900000u,
                TerminalStateValue = TerminalState.Landed,
                MaxDistanceFromLaunch = 100.0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = startUt,
                        bodyName = bodyName,
                        latitude = firstLatitude,
                        longitude = baseLongitude,
                        altitude = 10.0
                    },
                    new TrajectoryPoint
                    {
                        ut = startUt + 5.0,
                        bodyName = bodyName,
                        latitude = secondLatitude,
                        longitude = baseLongitude,
                        altitude = 12.0
                    }
                }
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Runtime Merge Dialog " + suffix,
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId
            };
            tree.Recordings[recordingId] = rec;
            return tree;
        }

        private static IEnumerator WaitForPopupDialog(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) != null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialog timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }

        private static IEnumerator WaitForPopupDialogToClose(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) == null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialogToClose timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }

        private static PopupDialog FindPopupDialog(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName) || PopupDialogToDisplayField == null || MultiOptionDialogNameField == null)
                return null;

            PopupDialog[] popups = Object.FindObjectsOfType<PopupDialog>();
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null)
                    continue;

                string currentName = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (currentName == dialogName)
                    return popups[i];
            }

            return null;
        }

        private static DialogGUIButton[] GetDialogButtons(MultiOptionDialog dialog)
        {
            if (dialog == null || MultiOptionDialogOptionsField == null)
                return new DialogGUIButton[0];

            DialogGUIBase[] options = MultiOptionDialogOptionsField.GetValue(dialog) as DialogGUIBase[];
            if (options == null || options.Length == 0)
                return new DialogGUIButton[0];

            var buttons = new List<DialogGUIButton>();
            for (int i = 0; i < options.Length; i++)
            {
                DialogGUIButton button = options[i] as DialogGUIButton;
                if (button != null)
                    buttons.Add(button);
            }

            return buttons.ToArray();
        }

        private static string GetDialogButtonText(DialogGUIButton button)
        {
            if (button == null || DialogGuiButtonTextField == null)
                return null;

            return DialogGuiButtonTextField.GetValue(button) as string;
        }

        private static void RemoveCommittedTreeByIdForRuntimeTest(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return;

            var committed = RecordingStore.CommittedTrees;
            for (int i = committed.Count - 1; i >= 0; i--)
            {
                RecordingTree tree = committed[i];
                if (tree == null || tree.Id != treeId)
                    continue;

                foreach (Recording rec in tree.Recordings.Values)
                    RecordingStore.RemoveCommittedInternal(rec);
                committed.RemoveAt(i);
            }
        }

        #endregion

        #region MapView icons (#387)

        [InGameTest(Category = "MapView",
            Description = "GhostMap checkpoint source log resolves a real CelestialBody world position (#571)")]
        public void GhostMapCheckpointSourceLogResolvesWorldPosition()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT
                && HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                InGameAssert.Skip("test requires flight or tracking station scene");
                return;
            }

            CelestialBody body = FlightGlobals.GetBodyByName("Kerbin")
                ?? FlightGlobals.Bodies?.Find(b => b != null && b.name == "Kerbin");
            InGameAssert.IsNotNull(body, "Kerbin should exist for checkpoint source world-position test");

            Recording rec = BuildRuntimeCheckpointMapSourceRecording(body);
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;
            try
            {
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                int cachedIndex = -1;
                GhostMapPresence.TrackingStationGhostSource source =
                    GhostMapPresence.ResolveMapPresenceGhostSource(
                        rec,
                        isSuppressed: false,
                        alreadyMaterialized: false,
                        currentUT: rec.StartUT + 30.0,
                        allowTerminalOrbitFallback: true,
                        logOperationName: "runtime-571-checkpoint-world",
                        ref cachedIndex,
                        out OrbitSegment resolvedSegment,
                        out _,
                        out string skipReason);

                // OrbitalCheckpoint sections coexist with their seed OrbitSegment
                // (#571 closure). When the segment covers currentUT the resolver
                // intentionally returns Segment — the densified checkpoint frames
                // are sampling along that same Keplerian arc, not a competing
                // source. Compare the existing xUnit pin
                // ResolveMapPresenceGhostSource_VisibleSegment_MatchesTrackingStationWrapper.
                InGameAssert.IsTrue(
                    source == GhostMapPresence.TrackingStationGhostSource.Segment,
                    $"Expected Segment checkpoint source, got {source}");
                InGameAssert.IsTrue(string.IsNullOrEmpty(skipReason),
                    $"Expected no skip reason, got {skipReason ?? "(null)"}");
                InGameAssert.AreEqual(body.name, resolvedSegment.bodyName);

                // P3 review pin: also require stateVectorSource=OrbitalCheckpoint
                // and orbitalCheckpointFallback=reject so the captured-line
                // predicate proves the resolver actually traversed the
                // OrbitalCheckpoint section before settling on Segment. Without
                // these substrings a future regression that silently stops
                // walking checkpoints would still match `sourceKind=Segment`
                // and leave this coexistence test green.
                string line = captured.LastOrDefault(l =>
                    l.Contains("[GhostMap]")
                    && l.Contains("runtime-571-checkpoint-world")
                    && l.Contains("sourceKind=Segment")
                    && l.Contains("stateVectorSource=OrbitalCheckpoint")
                    && l.Contains("orbitalCheckpointFallback=reject"));
                InGameAssert.IsNotNull(line, "GhostMap checkpoint source decision log should be captured with stateVectorSource=OrbitalCheckpoint and orbitalCheckpointFallback=reject");
                InGameAssert.IsTrue(line.Contains("world=(") && !line.Contains("world=(unresolved)"),
                    "GhostMap checkpoint source log should contain a resolved world=(x,y,z) position");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }
        }

        private static Recording BuildRuntimeCheckpointMapSourceRecording(CelestialBody body)
        {
            double startUT = 1000.0;
            double endUT = 1060.0;
            string bodyName = body != null ? body.name : "Kerbin";
            var segment = new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = (body?.Radius ?? 600000.0) + 100000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = bodyName
            };

            var first = new TrajectoryPoint
            {
                ut = startUT,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2300f, 0f),
                bodyName = bodyName
            };
            var second = first;
            second.ut = endUT;
            second.longitude = 5.0;

            var rec = new Recording
            {
                RecordingId = "runtime-571-checkpoint-world-rec",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Runtime checkpoint world source",
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.OrbitSegments.Add(segment);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint> { first, second },
                checkpoints = new List<OrbitSegment> { segment },
                minAltitude = 100000f,
                maxAltitude = 100000f
            });
            return rec;
        }

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
            if (HighLogic.LoadedScene != GameScenes.FLIGHT
                && HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                InGameAssert.Skip("test requires flight or tracking station scene");
                return;
            }

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
    /// Tracking Station scene canaries for runtime-only behavior that headless
    /// tests cannot exercise through KSP's live vessel list.
    /// </summary>
    public class TrackingStationRuntimeTests
    {
        private const string SyntheticVesselNamePrefix = "Parsek TS Runtime Canary";
        private const string KerbinBodyName = "Kerbin";
        private const double SyntheticOrbitSma = 700000.0;
        private const double SyntheticOrbitEcc = 0.001;

        [InGameTest(Category = "TrackingStation", Scene = GameScenes.TRACKSTATION,
            Description = "#554: Tracking Station scene host, stock SpaceTracking, MapView, and flightState are present")]
        public void TrackingStationSceneEntry_HostIsActive()
        {
            EnsureTrackingStationScene();

            var ts = Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts,
                "ParsekTrackingStation MonoBehaviour should be active in Tracking Station scene");

            var spaceTracking = Object.FindObjectOfType<KSP.UI.Screens.SpaceTracking>();
            InGameAssert.IsNotNull(spaceTracking,
                "SpaceTracking should be active in Tracking Station scene");

            InGameAssert.IsNotNull(MapView.fetch,
                "MapView.fetch should exist in Tracking Station scene");
            InGameAssert.IsNotNull(HighLogic.CurrentGame?.flightState,
                "CurrentGame.flightState should exist in Tracking Station scene");
            InGameAssert.IsNotNull(FlightGlobals.Vessels,
                "FlightGlobals.Vessels should be available in Tracking Station scene");

            ParsekLog.Info("TestRunner",
                string.Format(CultureInfo.InvariantCulture,
                    "TrackingStationSceneEntry_HostIsActive: vessels={0} ghostPids={1}",
                    FlightGlobals.Vessels.Count,
                    GhostMapPresence.ghostMapVesselPids.Count));
        }

        [InGameTest(Category = "TrackingStation", Scene = GameScenes.TRACKSTATION,
            Description = "#554: synthetic orbital TS ghost is removed when hidden and recreated when shown")]
        public void TrackingStationGhostToggle_SyntheticOrbit_RemovesAndRecreates()
        {
            using (var scope = new SyntheticTrackingStationRecordingScope("toggle"))
            using (var capture = new TrackingStationLogCapture())
            {
                var trackingHost = Object.FindObjectOfType<ParsekTrackingStation>();
                InGameAssert.IsNotNull(trackingHost,
                    "ParsekTrackingStation host should exist while toggling TS ghosts");

                ParsekSettings.Current.showGhostsInTrackingStation = true;
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();

                uint firstPid = GhostMapPresence.GetGhostVesselPidForRecording(scope.RecordingIndex);
                InGameAssert.IsTrue(firstPid != 0,
                    "Synthetic TS recording should create a ghost vessel when the setting is enabled");
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(firstPid),
                    "Created synthetic TS vessel should be registered as a ghost map vessel");

                ParsekSettings.Current.showGhostsInTrackingStation = false;
                ForceTrackingStationHostUpdate(trackingHost);

                InGameAssert.IsFalse(GhostMapPresence.HasGhostVesselForRecording(scope.RecordingIndex),
                    "Synthetic TS ghost should be removed when showGhostsInTrackingStation is disabled");

                ParsekSettings.Current.showGhostsInTrackingStation = true;
                ForceTrackingStationHostUpdate(trackingHost);

                uint recreatedPid = GhostMapPresence.GetGhostVesselPidForRecording(scope.RecordingIndex);
                InGameAssert.IsTrue(recreatedPid != 0,
                    "Synthetic TS recording should recreate a ghost vessel when the setting is re-enabled");
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(recreatedPid),
                    "Recreated synthetic TS vessel should be registered as a ghost map vessel");

                AssertNoCapturedTrackingStationErrors(capture,
                    "show/hide/recreate synthetic TS ghost lifecycle");

                ParsekLog.Info("TestRunner",
                    string.Format(CultureInfo.InvariantCulture,
                        "TrackingStationGhostToggle_SyntheticOrbit_RemovesAndRecreates: firstPid={0} recreatedPid={1}",
                        firstPid,
                        recreatedPid));
            }
        }

        [InGameTest(Category = "TrackingStation", Scene = GameScenes.TRACKSTATION,
            Description = "#554: TS ghost bookkeeping stays bounded, resolvable, and quiet during a live lifecycle tick")]
        public IEnumerator TrackingStationGhostObjects_SyntheticOrbit_ResolvableAndQuiet()
        {
            using (var scope = new SyntheticTrackingStationRecordingScope("object-count"))
            using (var capture = new TrackingStationLogCapture())
            {
                ParsekSettings.Current.showGhostsInTrackingStation = true;
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();

                // Let KSP process the ProtoVessel load and any stock Tracking Station
                // UI callbacks that run after the lifecycle tick.
                yield return null;
                yield return null;

                uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(scope.RecordingIndex);
                InGameAssert.IsTrue(ghostPid != 0,
                    "Synthetic TS recording should have a ghost PID after lifecycle update");

                Vessel ghost = FindVesselByPersistentId(ghostPid);
                InGameAssert.IsNotNull(ghost,
                    "Synthetic TS ghost PID should resolve to a live Vessel");
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(ghost.persistentId),
                    "Resolved synthetic TS vessel should still be tagged as a ghost");
                InGameAssert.IsTrue(ghost.orbitDriver != null && ghost.orbitDriver.orbit != null,
                    "Synthetic TS ghost should have an OrbitDriver with an orbit");
                InGameAssert.IsNotNull(ghost.mapObject,
                    "Synthetic TS ghost should have a map object");

                string expectedGhostName = "Ghost: " + scope.Recording.VesselName;
                int matchingGhostCount = 0;
                var vessels = FlightGlobals.Vessels;
                if (vessels != null)
                {
                    for (int i = 0; i < vessels.Count; i++)
                    {
                        Vessel candidate = vessels[i];
                        if (candidate == null)
                            continue;
                        if (!string.Equals(candidate.vesselName, expectedGhostName, System.StringComparison.Ordinal))
                            continue;
                        if (GhostMapPresence.IsGhostMapVessel(candidate.persistentId))
                            matchingGhostCount++;
                    }
                }

                InGameAssert.AreEqual(1, matchingGhostCount,
                    string.Format(CultureInfo.InvariantCulture,
                        "Synthetic TS recording should materialize exactly one live ghost vessel named '{0}'",
                        expectedGhostName));

                AssertNoCapturedTrackingStationErrors(capture,
                    "synthetic TS ghost object-count lifecycle");

                ParsekLog.Info("TestRunner",
                    string.Format(CultureInfo.InvariantCulture,
                        "TrackingStationGhostObjects_SyntheticOrbit_ResolvableAndQuiet: ghostPid={0} matchingGhostCount={1} ghostName='{2}'",
                        ghostPid,
                        matchingGhostCount,
                        expectedGhostName));
            }
        }

        [InGameTest(Category = "TrackingStation", Scene = GameScenes.TRACKSTATION,
            AllowBatchExecution = false,
            BatchSkipReason = "Manual-only — this canary drives stock Tracking Station Fly on a materialized orbital vessel and transitions the session to FLIGHT. Run it from a disposable Tracking Station session after an orbital recording has materialized.",
            Description = "#554/#550: materialized orbital TS vessel can be selected/flown without loading a stale asteroid/comet")]
        public IEnumerator TrackingStationMaterializedOrbit_FlyLoadsMaterializedVessel_NotStaleSelection()
        {
            EnsureTrackingStationScene();

            var tracking = Object.FindObjectOfType<KSP.UI.Screens.SpaceTracking>();
            if (tracking == null)
            {
                InGameAssert.Skip("SpaceTracking instance not found");
                yield break;
            }

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();

            int recordingIndex;
            Recording recording;
            Vessel materialized;
            if (!TryFindMaterializedTrackingStationRecording(
                out recordingIndex,
                out recording,
                out materialized))
            {
                InGameAssert.Skip(
                    "No materialized orbital Parsek spawn-handoff recording was present after the TS lifecycle tick");
                yield break;
            }

            Vessel staleCandidate = FindStaleSelectionCandidate(materialized);
            if (staleCandidate == null)
            {
                InGameAssert.Skip(
                    "No alternate stock vessel or asteroid/comet was available to seed a stale Tracking Station selection");
                yield break;
            }

            System.Reflection.FieldInfo selectedField = typeof(KSP.UI.Screens.SpaceTracking).GetField(
                "selectedVessel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            System.Reflection.MethodInfo setVesselMethod = typeof(KSP.UI.Screens.SpaceTracking).GetMethod(
                "SetVessel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(Vessel) },
                null);
            System.Reflection.MethodInfo flySelectedMethod = typeof(KSP.UI.Screens.SpaceTracking).GetMethod(
                "BtnOnClick_FlySelectedVessel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                System.Type.EmptyTypes,
                null);

            if (selectedField == null || setVesselMethod == null || flySelectedMethod == null)
            {
                InGameAssert.Skip("SpaceTracking selection/Fly reflection helpers are unavailable");
                yield break;
            }

            using (var ghostScope = new SyntheticTrackingStationRecordingScope("manual-fly-selection"))
            {
                GhostMapPresence.UpdateTrackingStationGhostLifecycle();

                uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(ghostScope.RecordingIndex);
                if (ghostPid == 0)
                {
                    InGameAssert.Skip(
                        "Synthetic ghost selection helper was not available to exercise the Tracking Station ghost-selection block");
                    yield break;
                }

                Vessel ghost = FindVesselByPersistentId(ghostPid);
                if (ghost == null)
                {
                    InGameAssert.Skip(
                        "Synthetic ghost selection helper did not resolve to a live ghost vessel");
                    yield break;
                }

                selectedField.SetValue(tracking, staleCandidate);
                setVesselMethod.Invoke(tracking, new object[] { ghost });

                Vessel selectedAfterGhostFocus = selectedField.GetValue(tracking) as Vessel;
                InGameAssert.IsNull(selectedAfterGhostFocus,
                    "Focusing a Parsek ghost should clear any stale private Tracking Station selection");

                setVesselMethod.Invoke(tracking, new object[] { materialized });
            }

            Vessel selectedAfterFocus = selectedField.GetValue(tracking) as Vessel;
            InGameAssert.IsNotNull(selectedAfterFocus,
                "Focusing the materialized Parsek vessel should leave a selected vessel");
            InGameAssert.AreEqual(materialized.persistentId, selectedAfterFocus.persistentId,
                "Focusing the materialized Parsek vessel should replace any stale private selection");
            InGameAssert.IsFalse(GhostMapPresence.HasGhostVesselForRecording(recordingIndex),
                "Materialized Tracking Station recording should not retain a ghost ProtoVessel");

            uint expectedPid = materialized.persistentId;
            string expectedName = materialized.vesselName;
            uint stalePid = staleCandidate.persistentId;
            string staleName = staleCandidate.vesselName;

            flySelectedMethod.Invoke(tracking, null);
            yield return WaitForLoadedScene(GameScenes.FLIGHT, 20f);
            yield return WaitForFlightReadyAndActiveVessel(expectedPid, 20f);

            Vessel active = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(active,
                "Tracking Station Fly should load a FLIGHT active vessel");
            InGameAssert.AreEqual(expectedPid, active.persistentId,
                string.Format(CultureInfo.InvariantCulture,
                    "Tracking Station Fly should load materialized Parsek vessel '{0}' pid={1}, not stale '{2}' pid={3}",
                    expectedName,
                    expectedPid,
                    staleName,
                    stalePid));
            InGameAssert.AreNotEqual(stalePid, active.persistentId,
                "Tracking Station Fly loaded the stale selection candidate");

            ParsekLog.Info("TestRunner",
                string.Format(CultureInfo.InvariantCulture,
                    "TrackingStationMaterializedOrbit_FlyLoadsMaterializedVessel_NotStaleSelection: recIndex={0} recId={1} activePid={2} stalePid={3}",
                    recordingIndex,
                    recording.RecordingId,
                    active.persistentId,
                    stalePid));
        }

        private static void EnsureTrackingStationScene()
        {
            InGameAssert.AreEqual(GameScenes.TRACKSTATION, HighLogic.LoadedScene,
                "test must run in Tracking Station scene");
        }

        private static Recording BuildSyntheticTrackingStationOrbitRecording(
            double currentUT,
            string label)
        {
            double startUT = currentUT - 60.0;
            double endUT = currentUT + 600.0;
            string safeLabel = string.IsNullOrEmpty(label) ? "canary" : label;

            var rec = new Recording
            {
                RecordingId = "ts-runtime-" + System.Guid.NewGuid().ToString("N"),
                VesselName = SyntheticVesselNamePrefix + " " + safeLabel,
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = SyntheticOrbitSma,
                TerminalOrbitEccentricity = SyntheticOrbitEcc,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = startUT,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2300f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = 0.0,
                longitude = 5.0,
                altitude = 100000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2300f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.0,
                eccentricity = SyntheticOrbitEcc,
                semiMajorAxis = SyntheticOrbitSma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });
            rec.MarkFilesDirty();
            return rec;
        }

        private static void ForceTrackingStationHostUpdate(ParsekTrackingStation host)
        {
            InGameAssert.IsNotNull(host, "Tracking Station host should exist before forcing an update");

            System.Reflection.MethodInfo updateMethod = typeof(ParsekTrackingStation).GetMethod(
                "Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                System.Type.EmptyTypes,
                null);
            InGameAssert.IsNotNull(updateMethod,
                "ParsekTrackingStation.Update reflection helper should be available");
            updateMethod.Invoke(host, null);
        }

        private static Vessel FindVesselByPersistentId(uint persistentId)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return null;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel != null && vessel.persistentId == persistentId)
                    return vessel;
            }

            return null;
        }

        private static bool TryFindMaterializedTrackingStationRecording(
            out int recordingIndex,
            out Recording recording,
            out Vessel materialized)
        {
            recordingIndex = -1;
            recording = null;
            materialized = null;
            int fallbackIndex = -1;
            Recording fallbackRecording = null;
            Vessel fallbackVessel = null;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                return false;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null)
                    continue;
                if (rec.TerminalStateValue != TerminalState.Orbiting
                    && rec.TerminalStateValue != TerminalState.Docked)
                    continue;
                if (!rec.VesselSpawned || rec.SpawnedVesselPersistentId == 0)
                    continue;

                uint pid = rec.SpawnedVesselPersistentId;
                Vessel vessel = FindVesselByPersistentId(pid);
                if (vessel == null || GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    continue;
                if (GhostMapPresence.HasGhostVesselForRecording(i))
                    continue;

                if (rec.SpawnedVesselPersistentId == rec.VesselPersistentId)
                {
                    if (fallbackRecording == null)
                    {
                        fallbackIndex = i;
                        fallbackRecording = rec;
                        fallbackVessel = vessel;
                    }
                    continue;
                }

                recordingIndex = i;
                recording = rec;
                materialized = vessel;
                return true;
            }

            if (fallbackRecording == null)
                return false;

            recordingIndex = fallbackIndex;
            recording = fallbackRecording;
            materialized = fallbackVessel;
            return true;
        }

        private static Vessel FindStaleSelectionCandidate(Vessel materialized)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return null;

            Vessel fallback = null;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel == null)
                    continue;
                if (materialized != null && vessel.persistentId == materialized.persistentId)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    continue;

                if (vessel.vesselType == VesselType.SpaceObject)
                    return vessel;

                if (fallback == null)
                    fallback = vessel;
            }

            return fallback;
        }

        private static IEnumerator WaitForLoadedScene(GameScenes scene, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (HighLogic.LoadedScene != scene && Time.realtimeSinceStartup < deadline)
                yield return null;

            InGameAssert.AreEqual(scene, HighLogic.LoadedScene,
                string.Format(CultureInfo.InvariantCulture,
                    "Timed out waiting for scene {0}; current scene is {1}",
                    scene,
                    HighLogic.LoadedScene));
        }

        private static IEnumerator WaitForFlightReadyAndActiveVessel(uint expectedPersistentId, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel active = FlightGlobals.ActiveVessel;
                if (FlightGlobals.ready
                    && active != null
                    && active.persistentId == expectedPersistentId)
                {
                    yield break;
                }

                yield return null;
            }

            Vessel finalActive = FlightGlobals.ActiveVessel;
            string finalActiveText = finalActive == null
                ? "(null)"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} pid={1}",
                    finalActive.vesselName,
                    finalActive.persistentId);
            InGameAssert.Fail(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Timed out waiting for FLIGHT readiness and active vessel pid={0}; ready={1}, active={2}",
                    expectedPersistentId,
                    FlightGlobals.ready,
                    finalActiveText));
        }

        private static void AssertNoCapturedTrackingStationErrors(
            TrackingStationLogCapture capture,
            string context)
        {
            if (capture == null || capture.ErrorLines.Count == 0)
                return;

            InGameAssert.Fail(
                string.Format(CultureInfo.InvariantCulture,
                    "Tracking Station emitted {0} error/exception line(s) during {1}: {2}",
                    capture.ErrorLines.Count,
                    context,
                    string.Join(" | ", capture.ErrorLines.ToArray())));
        }

        private sealed class SyntheticTrackingStationRecordingScope : System.IDisposable
        {
            private readonly System.Func<double> previousCurrentUTNow;
            private readonly bool previousShowGhosts;
            private readonly List<Recording> previousCommittedRecordings;
            private readonly List<RecordingTree> previousCommittedTrees;
            private bool disposed;

            internal Recording Recording { get; private set; }
            internal int RecordingIndex { get; private set; }
            internal double CurrentUT { get; private set; }

            internal SyntheticTrackingStationRecordingScope(string label)
            {
                EnsureTrackingStationScene();

                if (ParsekSettings.Current == null)
                    InGameAssert.Skip("ParsekSettings.Current is null");
                if (HighLogic.CurrentGame?.flightState == null)
                    InGameAssert.Skip("CurrentGame.flightState is null");

                previousCurrentUTNow = GhostMapPresence.CurrentUTNow;
                previousShowGhosts = ParsekSettings.Current.showGhostsInTrackingStation;
                previousCommittedRecordings = RecordingStore.CommittedRecordings != null
                    ? new List<Recording>(RecordingStore.CommittedRecordings)
                    : new List<Recording>();
                previousCommittedTrees = RecordingStore.CommittedTrees != null
                    ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                    : new List<RecordingTree>();

                CurrentUT = Planetarium.GetUniversalTime();
                Recording = BuildSyntheticTrackingStationOrbitRecording(CurrentUT, label);
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                RecordingStore.AddCommittedInternal(Recording);
                RecordingIndex = RecordingStore.CommittedRecordings.Count - 1;

                GhostMapPresence.CurrentUTNow = () => CurrentUT;
                ParsekSettings.Current.showGhostsInTrackingStation = true;
                GhostMapPresence.RemoveAllGhostVessels("ts-runtime-canary-start");
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                try
                {
                    GhostMapPresence.RemoveAllGhostVessels("ts-runtime-canary-cleanup");
                }
                finally
                {
                    RestoreCommittedState();
                    GhostMapPresence.CurrentUTNow = previousCurrentUTNow;
                    if (ParsekSettings.Current != null)
                        ParsekSettings.Current.showGhostsInTrackingStation = previousShowGhosts;
                    AlignTrackingStationHostCacheAfterRestore(
                        previousCommittedRecordings.Count,
                        previousShowGhosts);
                }
            }

            private void RestoreCommittedState()
            {
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();

                for (int i = 0; i < previousCommittedRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(previousCommittedRecordings[i]);
                for (int i = 0; i < previousCommittedTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(previousCommittedTrees[i]);
            }

            private static void AlignTrackingStationHostCacheAfterRestore(
                int committedCount,
                bool showGhosts)
            {
                var host = Object.FindObjectOfType<ParsekTrackingStation>();
                if (host == null)
                    return;

                SetTrackingStationHostField(host, "lastKnownCommittedCount", committedCount);
                SetTrackingStationHostField(host, "lastKnownShowGhosts", showGhosts);
                SetTrackingStationHostField(host, "nextLifecycleCheckTime", Time.time + 2.0f);
            }

            private static void SetTrackingStationHostField(
                ParsekTrackingStation host,
                string fieldName,
                object value)
            {
                FieldInfo field = typeof(ParsekTrackingStation).GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                InGameAssert.IsNotNull(field,
                    "ParsekTrackingStation private field should exist: " + fieldName);
                field.SetValue(host, value);
            }
        }

        private sealed class TrackingStationLogCapture : System.IDisposable
        {
            private readonly System.Action<string> previousObserver;
            private bool disposed;

            internal readonly List<string> ErrorLines = new List<string>();

            internal TrackingStationLogCapture()
            {
                previousObserver = ParsekLog.TestObserverForTesting;
                ParsekLog.TestObserverForTesting = line =>
                {
                    if (line != null
                        && line.IndexOf("[ERROR]", System.StringComparison.Ordinal) >= 0)
                    {
                        ErrorLines.Add(line);
                    }
                    previousObserver?.Invoke(line);
                };
                Application.logMessageReceived += HandleUnityLog;
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                Application.logMessageReceived -= HandleUnityLog;
                ParsekLog.TestObserverForTesting = previousObserver;
            }

            private void HandleUnityLog(string condition, string stackTrace, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception)
                    return;

                string safeCondition = condition ?? "(null)";
                string safeStackTrace = stackTrace ?? string.Empty;
                ErrorLines.Add(type + ": " + safeCondition + " " + safeStackTrace);
            }
        }
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

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
                InGameAssert.Skip("requires an active vessel in FLIGHT");
            if (activeVessel.isEVA || activeVessel.vesselType == VesselType.EVA)
                InGameAssert.Skip("requires a non-EVA active vessel");
            if (!FlightIntegrationTests.TryBuildSyntheticKeepVesselTree(
                    activeVessel, out _, out Recording rec, out string skipReason))
                InGameAssert.Skip(skipReason ?? "failed to build synthetic playback recording");

            int sentinelIndex = 1000;
            while (engine.ghostStates.ContainsKey(sentinelIndex))
                sentinelIndex++;

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
                    $"SpawnGhost priming in-game: rec='{rec.RecordingId}' " +
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

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this regression intentionally destroys live ghosts while the camera is watching one. Use Run All + Isolated or the row play button.",
            Description = "Run All cleanup exits watch mode before ghost teardown so Sun.LateUpdate stays exception-free")]
        public IEnumerator RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("no ParsekFlight.Instance");
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("no ActiveVessel");
            if (FlightCamera.fetch == null)
                InGameAssert.Skip("no FlightCamera");

            int index;
            GhostPlaybackState watchedState;
            if (!TryFindWatchableSameBodyGhost(flight, out index, out watchedState))
                InGameAssert.Skip("no same-body ghost available for watch-cleanup regression");

            var captured = new List<string>();
            Application.LogCallback callback = (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                    captured.Add(condition + "\n" + stackTrace);
            };

            flight.EnterWatchMode(index);
            yield return null;

            try
            {
                InGameAssert.IsTrue(flight.IsWatchingGhost, "should be in watch mode before cleanup");
                InGameAssert.AreEqual(index, flight.WatchedRecordingIndex, "watching wrong index before cleanup");

                Application.logMessageReceived += callback;
                runner.PerformBetweenRunCleanup("ingame-watch-cleanup-regression");
                yield return new WaitForSeconds(0.5f);

                AssertNoSunOrFlightGlobalsExceptions(
                    captured,
                    $"watch-cleanup regression index={index} body={watchedState.lastInterpolatedBodyName}");
                InGameAssert.IsFalse(flight.IsWatchingGhost,
                    "cleanup should have exited watch mode before ghost teardown");
            }
            finally
            {
                Application.logMessageReceived -= callback;
                if (flight.IsWatchingGhost)
                    flight.ExitWatchMode();
            }
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "Ghost creation near a Kerbin ~46 km playback point stays free of Sun.LateUpdate / FlightGlobals.UpdateInformation exception spam")]
        public IEnumerator GhostSpawn_Kerbin46KmPoint_DoesNotLeakSunLateUpdateNREs()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("no ParsekFlight.Instance");

            var engine = flight.Engine;
            if (engine == null)
                InGameAssert.Skip("no GhostPlaybackEngine");

            var committed = RecordingStore.CommittedRecordings;
            Recording rec;
            int recordingIndex;
            double primingUT;
            double matchedAltitude;
            if (!TryFindKerbinLowAltitudeRecording(committed, out rec, out recordingIndex, out primingUT, out matchedAltitude))
                InGameAssert.Skip("needs a committed recording with a Kerbin point near 46 km");

            int sentinelIndex = committed.Count + 1001;
            if (engine.ghostStates.ContainsKey(sentinelIndex))
                InGameAssert.Skip("sentinel index collision");

            GhostPlaybackState state = null;
            var captured = new List<string>();
            Application.LogCallback callback = (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                    captured.Add(condition + "\n" + stackTrace);
            };

            Application.logMessageReceived += callback;
            try
            {
                engine.SpawnGhost(sentinelIndex, rec as IPlaybackTrajectory, primingUT);
                yield return null;
                yield return null;
                yield return new WaitForSeconds(0.5f);

                bool found = engine.ghostStates.TryGetValue(sentinelIndex, out state);
                InGameAssert.IsTrue(found,
                    $"ghostStates should contain sentinel index {sentinelIndex} after SpawnGhost");
                InGameAssert.IsNotNull(state, "state should not be null after SpawnGhost");
                InGameAssert.IsNotNull(state.ghost, "state.ghost should not be null after SpawnGhost");

                AssertNoSunOrFlightGlobalsExceptions(
                    captured,
                    $"low-altitude spawn recordingIndex={recordingIndex} vessel=\"{rec.VesselName}\" altitude={matchedAltitude:F1} ut={primingUT:F1}");

                ParsekLog.Info("TestRunner",
                    $"GhostSpawn_Kerbin46KmPoint: recordingIndex={recordingIndex} vessel=\"{rec.VesselName}\" " +
                    $"matchedAltitude={matchedAltitude:F1} primingUT={primingUT:F1}");
            }
            finally
            {
                Application.logMessageReceived -= callback;
                engine.ghostStates.Remove(sentinelIndex);
                if (state != null && state.ghost != null)
                    runner.TrackForCleanup(state.ghost);
            }
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
            float cutoffKm = DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm();

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
                float expectedPitchRad = WatchMode.EntryPitchDegrees * Mathf.Deg2Rad;
                float actualPitchRad = FlightCamera.fetch.camPitch;
                float actualHdgRad = FlightCamera.fetch.camHdg;
                float pitchDeg = Mathf.Abs(actualPitchRad - expectedPitchRad) * Mathf.Rad2Deg;
                float hdgDeg = Mathf.Abs(Mathf.DeltaAngle(actualHdgRad * Mathf.Rad2Deg, WatchMode.EntryHeadingDegrees));
                InGameAssert.IsTrue(pitchDeg < 1f,
                    $"pitch should be near {WatchMode.EntryPitchDegrees} deg, got delta={pitchDeg:F2} deg");
                InGameAssert.IsTrue(hdgDeg < 1f,
                    $"heading should be near {WatchMode.EntryHeadingDegrees} deg, got delta={hdgDeg:F2} deg");

                // --- Safety-net assertion: no 180-degree camera flip ---
                Vector3 cameraForwardAfter = FlightCamera.fetch.transform.forward;
                float worldDot = Vector3.Dot(cameraForwardBefore, cameraForwardAfter);
                InGameAssert.IsTrue(worldDot > -0.5f,
                    $"camera should not flip ~180 degrees on watch entry, dot={worldDot:F3}");

                // --- Verbose diagnostic log ---
                ParsekLog.Verbose("TestRunner",
                    $"WatchEntry_SameBody: index={index} body={state.lastInterpolatedBodyName} " +
                    $"camPitchDeg={actualPitchRad * Mathf.Rad2Deg:F2} camHdgDeg={actualHdgRad * Mathf.Rad2Deg:F2} " +
                    $"expectedPitch={WatchMode.EntryPitchDegrees:F1} expectedHdg={WatchMode.EntryHeadingDegrees:F1} " +
                    $"worldDot={worldDot:F3}");
            }
            finally
            {
                ParsekFlight.Instance.ExitWatchMode();
            }

            InGameAssert.IsFalse(ParsekFlight.Instance.IsWatchingGhost, "should have exited watch mode");
        }

        private static void AssertNoSunOrFlightGlobalsExceptions(
            IEnumerable<string> captured,
            string context)
        {
            foreach (var line in captured)
            {
                InGameAssert.IsFalse(line.Contains("Sun.LateUpdate"),
                    $"expected zero Sun.LateUpdate exceptions during {context}, saw: {line}");
                InGameAssert.IsFalse(line.Contains("FlightGlobals.UpdateInformation"),
                    $"expected zero FlightGlobals.UpdateInformation exceptions during {context}, saw: {line}");
            }
        }

        private static bool TryFindWatchableSameBodyGhost(
            ParsekFlight flight,
            out int index,
            out GhostPlaybackState state)
        {
            index = -1;
            state = null;

            if (flight == null || flight.Engine == null || FlightGlobals.ActiveVessel == null)
                return false;

            var committed = RecordingStore.CommittedRecordings;
            string activeBodyName = FlightGlobals.ActiveVessel.mainBody.name;
            float cutoffKm = DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm();

            foreach (var kvp in flight.Engine.ghostStates)
            {
                var gs = kvp.Value;
                if (gs == null) continue;
                if (gs.lastInterpolatedBodyName != activeBodyName) continue;
                if (gs.ghost == null) continue;
                if (kvp.Key >= committed.Count) continue;
                if (gs.lastDistance <= 0.0) continue;
                if (!GhostPlaybackLogic.IsWithinWatchRange(gs.lastDistance, cutoffKm)) continue;

                index = kvp.Key;
                state = gs;
                return true;
            }

            return false;
        }

        private static bool TryFindKerbinLowAltitudeRecording(
            IReadOnlyList<Recording> committed,
            out Recording recording,
            out int recordingIndex,
            out double primingUT,
            out double matchedAltitude)
        {
            recording = null;
            recordingIndex = -1;
            primingUT = 0.0;
            matchedAltitude = 0.0;

            if (committed == null)
                return false;

            double bestDelta = double.PositiveInfinity;
            for (int i = 0; i < committed.Count; i++)
            {
                var candidate = committed[i];
                if (candidate == null
                    || candidate.GhostVisualSnapshot == null
                    || candidate.Points == null
                    || candidate.Points.Count < 2)
                {
                    continue;
                }

                foreach (var point in candidate.Points)
                {
                    if (!string.Equals(point.bodyName, "Kerbin")) continue;
                    if (double.IsNaN(point.altitude) || double.IsInfinity(point.altitude)) continue;

                    double delta = System.Math.Abs(point.altitude - 46000.0);
                    if (delta >= bestDelta)
                        continue;

                    bestDelta = delta;
                    recording = candidate;
                    recordingIndex = i;
                    primingUT = point.ut;
                    matchedAltitude = point.altitude;
                }
            }

            return recording != null;
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

            // Invalid IDs are intentionally exercised in the test-only logging context
            // so expected security rejections stay visible without polluting WARN triage.
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId(
                    null,
                    RecordingIdValidationLogContext.Test),
                "null ID should be invalid");
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId(
                    "",
                    RecordingIdValidationLogContext.Test),
                "empty ID should be invalid");
            InGameAssert.IsFalse(RecordingPaths.ValidateRecordingId(
                    "../etc/passwd",
                    RecordingIdValidationLogContext.Test),
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

        [InGameTest(Category = "GhostVisuals", Scene = GameScenes.FLIGHT,
            Description = "Bug #450 B2: incremental snapshot build yields after one part and completes across subsequent advances")]
        public IEnumerator IncrementalSnapshotBuild_YieldsThenCompletes()
        {
            var recordings = RecordingStore.CommittedRecordings;
            Recording withMultipartSnapshot = recordings.FirstOrDefault(rec =>
            {
                ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
                return snapshot != null && snapshot.GetNodes("PART").Length >= 2;
            });

            if (withMultipartSnapshot == null)
            {
                ParsekLog.Verbose("TestRunner",
                    "No committed recording with a multipart snapshot — skipping incremental build test");
                yield break;
            }

            ConfigNode snapshotNode = GhostVisualBuilder.GetGhostSnapshot(withMultipartSnapshot);
            PendingGhostVisualBuild build = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                withMultipartSnapshot,
                snapshotNode,
                "ParsekTest_SplitAdvance",
                withMultipartSnapshot.GhostVisualSnapshot != null
                    ? HeaviestSpawnBuildType.RecordingStartSnapshot
                    : HeaviestSpawnBuildType.VesselSnapshot);

            InGameAssert.IsNotNull(build, "TryBeginTimelineGhostBuild should succeed for a multipart snapshot");

            GameObject cleanupRoot = build.root;
            GhostBuildResult result = null;
            try
            {
                // `maxTicks: 0` is the deliberate "one part, then yield if work remains"
                // seam from AdvanceTimelineGhostBuild. This keeps the test deterministic:
                // we assert the split-build path itself, not a stopwatch-dependent budget.
                bool completed = GhostVisualBuilder.AdvanceTimelineGhostBuild(build, maxTicks: 0);
                InGameAssert.IsFalse(completed,
                    $"AdvanceTimelineGhostBuild(0) should yield for multipart snapshot '{withMultipartSnapshot.VesselName}'");
                InGameAssert.IsTrue(build.nextPartIndex > 0 && build.nextPartIndex < build.partNodes.Length,
                    $"First incremental advance should process at least one part and leave work remaining, got nextPartIndex={build.nextPartIndex} of {build.partNodes.Length}");

                int advances = 1;
                while (!completed)
                {
                    yield return null;
                    completed = GhostVisualBuilder.AdvanceTimelineGhostBuild(build, maxTicks: 0);
                    advances++;
                    InGameAssert.IsTrue(advances <= build.partNodes.Length + 1,
                        $"Incremental build should complete within one advance per part, got advances={advances} parts={build.partNodes.Length}");
                }

                InGameAssert.IsTrue(build.nextPartIndex == build.partNodes.Length,
                    $"Completed incremental build should consume every snapshot part, got {build.nextPartIndex}/{build.partNodes.Length}");

                result = GhostVisualBuilder.CompleteTimelineGhostBuild(build, withMultipartSnapshot);
                InGameAssert.IsNotNull(result, "CompleteTimelineGhostBuild should return a result after all parts are advanced");
                InGameAssert.IsNotNull(result.root, "Incremental build result should keep the ghost root");

                cleanupRoot = result.root;
                InGameAssert.IsGreaterThan(result.root.transform.childCount, 0,
                    $"Incremental ghost root should contain built children for '{withMultipartSnapshot.VesselName}'");

                ParsekLog.Verbose("TestRunner",
                    $"Incremental ghost build completed for '{withMultipartSnapshot.VesselName}' in {advances} advances " +
                    $"({build.partNodes.Length} snapshot parts)");
            }
            finally
            {
                if (cleanupRoot != null)
                    runner.TrackForCleanup(cleanupRoot);
            }
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

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                InGameAssert.Skip("No crew roster available");
                return;
            }

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
            var priorObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => logLines.Add(line);
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
                ParsekLog.TestObserverForTesting = priorObserver;

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

        [InGameTest(Category = "CrewReservation", Scene = GameScenes.FLIGHT,
            Description = "Bug #578: PlaceOrphanedReplacements logs wrong-active-vessel no-match diagnostics without using an unrelated free seat")]
        public void Bug578_OrphanPlacement_NoMatchingPart_LogsDeferredReason()
        {
            var av = FlightGlobals.ActiveVessel;
            if (av == null)
            {
                InGameAssert.Skip("No active vessel");
                return;
            }

            Part unrelatedFreeSeatPart = null;
            for (int i = 0; i < av.parts.Count; i++)
            {
                var p = av.parts[i];
                if (p != null && p.CrewCapacity > 0 && p.protoModuleCrew.Count < p.CrewCapacity
                    && p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.name))
                {
                    unrelatedFreeSeatPart = p;
                    break;
                }
            }
            if (unrelatedFreeSeatPart == null)
            {
                InGameAssert.Skip("Active vessel has no unrelated free crew seat to prove tier-3 fallback stays rejected");
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

            uint missingPid = 4000000000u;
            bool pidCollision;
            do
            {
                pidCollision = false;
                for (int i = 0; i < av.parts.Count; i++)
                {
                    if (av.parts[i] != null && av.parts[i].persistentId == missingPid)
                    {
                        pidCollision = true;
                        missingPid--;
                        break;
                    }
                }
            } while (pidCollision);

            string missingPartName = "bug578_missing_part_" +
                System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeOriginal = "Bug578Test_" +
                System.Guid.NewGuid().ToString("N").Substring(0, 8) + " Kerman";

            var savedReplacements = new Dictionary<string, string>();
            foreach (var kvp in CrewReservationManager.CrewReplacements)
                savedReplacements[kvp.Key] = kvp.Value;
            int savedCommittedCount = RecordingStore.CommittedRecordings.Count;
            int beforeCrewCount = unrelatedFreeSeatPart.protoModuleCrew.Count;

            Recording syntheticRecording = null;
            bool addedToCommitted = false;
            var logLines = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => logLines.Add(line);
            try
            {
                CrewReservationManager.ClearReplacementsInternal();

                var snapshot = new ConfigNode("VESSEL");
                var partNode = snapshot.AddNode("PART");
                partNode.AddValue("name", missingPartName);
                partNode.AddValue("persistentId", missingPid.ToString());
                partNode.AddValue("crew", fakeOriginal);

                syntheticRecording = new Recording
                {
                    RecordingId = "test-orphan-578-" +
                        System.Guid.NewGuid().ToString("N").Substring(0, 8),
                    VesselName = "Bug578WrongActiveVessel",
                    GhostVisualSnapshot = snapshot
                };

                RecordingStore.AddCommittedInternal(syntheticRecording);
                addedToCommitted = true;

                CrewReservationManager.SetReplacement(fakeOriginal, standIn.name);

                var swappedOriginals = new HashSet<string>();
                int placed = CrewReservationManager.PlaceOrphanedReplacements(roster, swappedOriginals);

                InGameAssert.AreEqual(0, placed,
                    $"PlaceOrphanedReplacements should defer when pid and name both miss (placed={placed})");
                InGameAssert.IsFalse(swappedOriginals.Contains(fakeOriginal),
                    "Deferred orphan placement must not mark the original as swapped");
                InGameAssert.IsFalse(unrelatedFreeSeatPart.protoModuleCrew.Contains(standIn),
                    $"Stand-in '{standIn.name}' must not be placed into unrelated free part '{unrelatedFreeSeatPart.partInfo.title}'");
                InGameAssert.AreEqual(beforeCrewCount, unrelatedFreeSeatPart.protoModuleCrew.Count,
                    "Unrelated free-seat part crew count changed despite no pid/name match");

                bool sawDeferredReason = false;
                foreach (var line in logLines)
                {
                    if (line.Contains("[CrewReservation]")
                        && line.Contains("Orphan placement deferred:")
                        && line.Contains("stand-in kept in roster")
                        && line.Contains("reason=active-vessel-missing-snapshot-part")
                        && line.Contains("attempted pidTier=yes nameTier=yes"))
                    {
                        sawDeferredReason = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawDeferredReason,
                    "Expected deferred orphan-placement WARN with reason=active-vessel-missing-snapshot-part");

                ParsekLog.Info("TestRunner",
                    $"Bug578 end-to-end: orphan placement deferred '{fakeOriginal}' → '{standIn.name}' " +
                    $"with missing snapshot part '{missingPartName}' despite unrelated free seat in '{unrelatedFreeSeatPart.partInfo.title}'");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;

                CrewReservationManager.ClearReplacementsInternal();
                foreach (var kvp in savedReplacements)
                    CrewReservationManager.SetReplacement(kvp.Key, kvp.Value);

                if (addedToCommitted)
                    RecordingStore.RemoveCommittedInternal(syntheticRecording);

                InGameAssert.AreEqual(beforeCrewCount, unrelatedFreeSeatPart.protoModuleCrew.Count,
                    $"Rollback failed: unrelated free-seat part crew count not restored " +
                    $"(expected={beforeCrewCount}, actual={unrelatedFreeSeatPart.protoModuleCrew.Count})");
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

        [InGameTest(Category = "MapPresence", Scene = GameScenes.FLIGHT,
            Description = "#586: same-body ghost map vessels keep vessel target identity and survive stock SetVesselTarget validation")]
        public IEnumerator GhostMapVesselTargeting_SyntheticSameBodyGhost_Sticks()
        {
            if (FlightGlobals.fetch == null || FlightGlobals.ActiveVessel == null)
            {
                InGameAssert.Skip("FlightGlobals or active vessel is unavailable");
                yield break;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            if (active.mainBody == null)
            {
                InGameAssert.Skip("Active vessel main body is unavailable");
                yield break;
            }

            int recordingIndex = 586000 + Time.frameCount;
            Recording rec = BuildSyntheticFlightTargetRecording(active, Planetarium.GetUniversalTime());
            Vessel ghost = null;
            System.Action<string> priorObserver = null;
            bool observerInstalled = false;
            try
            {
                ghost = GhostMapPresence.CreateGhostVesselForRecording(recordingIndex, rec);
                InGameAssert.IsNotNull(ghost,
                    "Synthetic flight ghost should be created for targeting canary");

                yield return null;
                yield return null;

                InGameAssert.IsTrue(ghost.orbitDriver != null && ghost.orbitDriver.orbit != null,
                    "Synthetic flight ghost should have an OrbitDriver with an orbit");
                InGameAssert.IsTrue(ghost.orbitDriver.vessel == ghost,
                    "Synthetic flight ghost OrbitDriver.vessel should point at the ghost vessel");
                InGameAssert.IsTrue(ghost.orbitDriver.celestialBody == null,
                    "Synthetic flight ghost OrbitDriver.celestialBody must stay null so stock targeting treats it as a vessel");
                InGameAssert.IsTrue(GhostMapPresence.IsVesselRegistered(ghost),
                    "Synthetic flight ghost should be registered in FlightGlobals.Vessels");
                InGameAssert.AreEqual(active.mainBody.name, ghost.orbitDriver.referenceBody.name,
                    "Synthetic flight ghost should orbit the active vessel's current main body");

                var captured = new List<string>();
                priorObserver = ParsekLog.TestObserverForTesting;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };
                observerInstalled = true;
                GhostMapPresence.SetGhostMapNavigationTarget(
                    ghost,
                    recordingIndex,
                    "ingame targeting canary");
                yield return null;
                yield return null;
                yield return null;
                ParsekLog.TestObserverForTesting = priorObserver;
                observerInstalled = false;

                Vessel targetVessel = FlightGlobals.fetch.VesselTarget != null
                    ? FlightGlobals.fetch.VesselTarget.GetVessel()
                    : null;
                InGameAssert.IsNotNull(targetVessel,
                    "Stock SetVesselTarget should still have a vessel target after validation frames");
                InGameAssert.AreEqual(ghost.persistentId, targetVessel.persistentId,
                    "Production SetGhostMapNavigationTarget should retain the synthetic ghost vessel target");
                InGameAssert.IsTrue(captured.Any(line =>
                        line.Contains("[GhostMap]")
                        && line.Contains("set as target via ingame targeting canary")
                        && line.Contains("verified")),
                    "Production SetGhostMapNavigationTarget should emit verified success only after validation");
                InGameAssert.IsFalse(captured.Any(line =>
                        line.Contains("[GhostMap]")
                        && line.Contains("target rejected via ingame targeting canary")),
                    "Production SetGhostMapNavigationTarget should not reject the synthetic same-body ghost");

                ParsekLog.Info("TestRunner",
                    string.Format(CultureInfo.InvariantCulture,
                        "GhostMapVesselTargeting_SyntheticSameBodyGhost_Sticks: activePid={0} ghostPid={1} body={2}",
                        active.persistentId,
                        ghost.persistentId,
                        active.mainBody.name));
            }
            finally
            {
                if (observerInstalled)
                    ParsekLog.TestObserverForTesting = priorObserver;
                if (FlightGlobals.fetch != null
                    && FlightGlobals.fetch.VesselTarget != null
                    && ghost != null
                    && FlightGlobals.fetch.VesselTarget.GetVessel() == ghost)
                {
                    FlightGlobals.fetch.SetVesselTarget(null, overrideInputLock: true);
                }
                GhostMapPresence.RemoveGhostVesselForRecording(recordingIndex, "ingame-targeting-canary-cleanup");
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

        private static Recording BuildSyntheticFlightTargetRecording(Vessel active, double currentUT)
        {
            CelestialBody body = active.mainBody;
            double startUT = currentUT - 10.0;
            double endUT = currentUT + 600.0;
            double semiMajorAxis = System.Math.Max(
                body.Radius + 100000.0,
                active.orbit != null && active.orbit.semiMajorAxis > 0.0
                    ? active.orbit.semiMajorAxis + 5000.0
                    : body.Radius + 100000.0);

            var rec = new Recording
            {
                RecordingId = "flight-target-runtime-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Flight Target Canary",
                TerminalStateValue = TerminalState.Orbiting,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = body.name,
                TerminalOrbitBody = body.name,
                TerminalOrbitSemiMajorAxis = semiMajorAxis,
                TerminalOrbitEccentricity = 0.001,
                TerminalOrbitInclination = active.orbit != null ? active.orbit.inclination : 0.0,
                TerminalOrbitLAN = active.orbit != null ? active.orbit.LAN : 0.0,
                TerminalOrbitArgumentOfPeriapsis = active.orbit != null ? active.orbit.argumentOfPeriapsis : 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = startUT,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                latitude = active.latitude,
                longitude = active.longitude,
                altitude = 100000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2300f, 0f),
                bodyName = body.name,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = active.latitude,
                longitude = active.longitude + 1.0,
                altitude = 100000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2300f, 0f),
                bodyName = body.name,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = rec.TerminalOrbitInclination,
                eccentricity = rec.TerminalOrbitEccentricity,
                semiMajorAxis = rec.TerminalOrbitSemiMajorAxis,
                longitudeOfAscendingNode = rec.TerminalOrbitLAN,
                argumentOfPeriapsis = rec.TerminalOrbitArgumentOfPeriapsis,
                meanAnomalyAtEpoch = rec.TerminalOrbitMeanAnomalyAtEpoch,
                epoch = rec.TerminalOrbitEpoch,
                bodyName = body.name,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });
            rec.MarkFilesDirty();
            return rec;
        }
    }

    /// <summary>
    /// Tier 3: Multi-frame coroutine tests requiring Flight scene.
    /// </summary>
    public class FlightIntegrationTests
    {
        private readonly InGameTestRunner runner;
        private static readonly MethodInfo FlightDriverRevertToLaunchMethod =
            typeof(FlightDriver).GetMethod("RevertToLaunch",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo FlightGlobalsClearPersistentIdDictionariesMethod =
            typeof(FlightGlobals).GetMethod("ClearpersistentIdDictionaries",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo FlightDriverCanRevertProperty =
            typeof(FlightDriver).GetProperty("CanRevert",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PopupDialogToDisplayField =
            typeof(PopupDialog).GetField("dialogToDisplay",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogNameField =
            typeof(MultiOptionDialog).GetField("name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogTitleField =
            typeof(MultiOptionDialog).GetField("title",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MultiOptionDialogOptionsField =
            typeof(MultiOptionDialog).GetField("Options",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DialogGuiButtonTextField =
            typeof(DialogGUIButton).GetField("OptionText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo DialogGuiButtonOptionSelectedMethod =
            typeof(DialogGUIButton).GetMethod("OptionSelected",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        public FlightIntegrationTests(InGameTestRunner runner) { this.runner = runner; }

        private static IEnumerator WaitForRecordingToLeavePrelaunch(string expectedRecordingId, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                var vessel = FlightGlobals.ActiveVessel;
                string activeRecId = flight?.ActiveTreeForSerialization?.ActiveRecordingId;
                if (flight != null
                    && flight.IsRecording
                    && vessel != null
                    && vessel.situation != Vessel.Situations.PRELAUNCH
                    && (string.IsNullOrEmpty(expectedRecordingId) || activeRecId == expectedRecordingId))
                {
                    yield break;
                }

                yield return null;
            }

            var timedOutFlight = ParsekFlight.Instance;
            var timedOutVessel = FlightGlobals.ActiveVessel;
            InGameAssert.Fail(
                $"WaitForRecordingToLeavePrelaunch timed out after {timeoutSeconds:F0}s " +
                $"(parsekFlight={(timedOutFlight != null)}, " +
                $"isRecording={timedOutFlight?.IsRecording == true}, " +
                $"activeRecordingId={timedOutFlight?.ActiveTreeForSerialization?.ActiveRecordingId ?? "null"}, " +
                $"expectedRecordingId={expectedRecordingId ?? "null"}, " +
                $"activeVessel='{timedOutVessel?.vesselName ?? "null"}', " +
                $"situation={timedOutVessel?.situation.ToString() ?? "null"})");
        }

        private static IEnumerator WaitForCommittedRecording(
            string recordingId, int committedBefore, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                bool committed = RecordingStore.CommittedRecordings.Any(
                    r => r != null && r.RecordingId == recordingId);
                bool countIncreased = RecordingStore.CommittedRecordings.Count > committedBefore;
                bool stillRecording = ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording;
                if (committed && countIncreased && !stillRecording)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForCommittedRecording timed out after {timeoutSeconds:F0}s " +
                $"(recordingId={recordingId ?? "null"}, committedBefore={committedBefore}, " +
                $"committedNow={RecordingStore.CommittedRecordings.Count}, " +
                $"isRecording={ParsekFlight.Instance?.IsRecording == true})");
        }

        private static IEnumerator WaitForCapturedLogLine(
            List<string> captured, string containsText, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (captured.Any(line => line.Contains(containsText)))
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForCapturedLogLine timed out after {timeoutSeconds:F0}s " +
                $"(text='{containsText}', captured={captured?.Count ?? 0})");
        }

        private static IEnumerator AssertNoPopupDialog(string dialogName, float durationSeconds)
        {
            float deadline = Time.time + durationSeconds;
            while (Time.time < deadline)
            {
                PopupDialog popup = FindPopupDialog(dialogName);
                if (popup != null)
                {
                    string title = null;
                    if (PopupDialogToDisplayField != null && MultiOptionDialogTitleField != null)
                    {
                        MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                        title = dialog != null ? MultiOptionDialogTitleField.GetValue(dialog) as string : null;
                    }

                    InGameAssert.Fail(
                        $"Unexpected popup dialog '{dialogName}' appeared during no-dialog window " +
                        $"(title='{title ?? "<null>"}')");
                }

                yield return null;
            }
        }

        private static PopupDialog FindPopupDialog(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName) || PopupDialogToDisplayField == null || MultiOptionDialogNameField == null)
                return null;

            PopupDialog[] popups = Object.FindObjectsOfType<PopupDialog>();
            for (int i = 0; i < popups.Length; i++)
            {
                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popups[i]) as MultiOptionDialog;
                if (dialog == null)
                    continue;

                string currentName = MultiOptionDialogNameField.GetValue(dialog) as string;
                if (currentName == dialogName)
                    return popups[i];
            }

            return null;
        }

        private static DialogGUIButton[] GetDialogButtons(MultiOptionDialog dialog)
        {
            if (dialog == null || MultiOptionDialogOptionsField == null)
                return new DialogGUIButton[0];

            DialogGUIBase[] options = MultiOptionDialogOptionsField.GetValue(dialog) as DialogGUIBase[];
            if (options == null || options.Length == 0)
                return new DialogGUIButton[0];

            var buttons = new List<DialogGUIButton>();
            for (int i = 0; i < options.Length; i++)
            {
                DialogGUIButton button = options[i] as DialogGUIButton;
                if (button != null)
                    buttons.Add(button);
            }

            return buttons.ToArray();
        }

        private static string GetDialogButtonText(DialogGUIButton button)
        {
            if (button == null || DialogGuiButtonTextField == null)
                return null;

            return DialogGuiButtonTextField.GetValue(button) as string;
        }

        private static IEnumerator WaitForPopupDialog(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) != null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialog timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }

        private static IEnumerator WaitForPopupDialogToClose(string dialogName, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (FindPopupDialog(dialogName) == null)
                    yield break;

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForPopupDialogToClose timed out after {timeoutSeconds:F0}s (dialog='{dialogName}')");
        }

        private static IEnumerator WaitForLoadedScene(GameScenes expectedScene, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (HighLogic.LoadedScene == expectedScene)
                {
                    if (expectedScene != GameScenes.SPACECENTER
                        || Object.FindObjectOfType<ParsekKSC>() != null)
                    {
                        yield break;
                    }
                }

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForLoadedScene timed out after {timeoutSeconds:F0}s " +
                $"(expected={expectedScene}, actual={HighLogic.LoadedScene})");
        }

        private static IEnumerator WaitForRecordingToClearPad(
            Vector3d launchWorldPosition,
            double minimumDistanceMeters,
            string expectedRecordingId,
            float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                var vessel = FlightGlobals.ActiveVessel;
                string activeRecId = flight?.ActiveTreeForSerialization?.ActiveRecordingId;
                double currentDistance = vessel != null
                    ? Vector3d.Distance(vessel.GetWorldPos3D(), launchWorldPosition)
                    : double.NaN;
                if (flight != null
                    && flight.IsRecording
                    && vessel != null
                    && (string.IsNullOrEmpty(expectedRecordingId) || activeRecId == expectedRecordingId)
                    && currentDistance >= minimumDistanceMeters)
                {
                    yield break;
                }

                yield return null;
            }

            var timedOutFlight = ParsekFlight.Instance;
            var timedOutVessel = FlightGlobals.ActiveVessel;
            double finalDistance = timedOutVessel != null
                ? Vector3d.Distance(timedOutVessel.GetWorldPos3D(), launchWorldPosition)
                : double.NaN;
            InGameAssert.Fail(
                $"WaitForRecordingToClearPad timed out after {timeoutSeconds:F0}s " +
                $"(minimumDistance={minimumDistanceMeters:F0}m, actualDistance={finalDistance:F1}m, " +
                $"parsekFlight={(timedOutFlight != null)}, isRecording={timedOutFlight?.IsRecording == true}, " +
                $"activeRecordingId={timedOutFlight?.ActiveTreeForSerialization?.ActiveRecordingId ?? "null"}, " +
                $"expectedRecordingId={expectedRecordingId ?? "null"}, " +
                $"activeVessel='{timedOutVessel?.vesselName ?? "null"}', " +
                $"situation={timedOutVessel?.situation.ToString() ?? "null"})");
        }

        private static void TriggerSaveAndExitToSpaceCenter()
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Fail("TriggerSaveAndExitToSpaceCenter requires HighLogic.CurrentGame");
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                InGameAssert.Fail("TriggerSaveAndExitToSpaceCenter requires HighLogic.SaveFolder");
            if (FlightGlobalsClearPersistentIdDictionariesMethod == null)
                InGameAssert.Fail(
                    "TriggerSaveAndExitToSpaceCenter requires FlightGlobals.ClearpersistentIdDictionaries reflection");

            GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);
            Game updatedGame = HighLogic.CurrentGame.Updated();
            InGameAssert.IsNotNull(updatedGame,
                "HighLogic.CurrentGame.Updated() should return a game before stock-style Space Center exit");
            try
            {
                FlightGlobalsClearPersistentIdDictionariesMethod.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                InGameAssert.Fail(
                    $"FlightGlobals.ClearpersistentIdDictionaries threw " +
                    $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                    $"{ex.InnerException?.Message ?? ex.Message}");
            }
            GamePersistence.SaveGame(updatedGame, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        private static void RemoveCommittedTreeByIdForRuntimeTest(string treeId, bool recalculateAfterRemoval)
        {
            if (string.IsNullOrEmpty(treeId))
                return;

            bool removedAny = false;
            var committed = RecordingStore.CommittedTrees;
            for (int i = committed.Count - 1; i >= 0; i--)
            {
                RecordingTree tree = committed[i];
                if (tree == null || tree.Id != treeId)
                    continue;

                foreach (Recording rec in tree.Recordings.Values)
                    RecordingStore.RemoveCommittedInternal(rec);
                committed.RemoveAt(i);
                removedAny = true;
            }

            if (removedAny && recalculateAfterRemoval)
            {
                RecordingStore.RunOptimizationPass();
                LedgerOrchestrator.RecalculateAndPatch();
            }
        }

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

        [InGameTest(Category = "TerrainClearance", Scene = GameScenes.FLIGHT,
            Description = "Loop explosion camera holds use the engine's terrain-clamped anchor instead of the buried raw root (#525)")]
        public void ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold()
        {
            var flight = ParsekFlight.Instance;
            if (flight == null)
            {
                InGameAssert.Skip("needs ParsekFlight instance");
                return;
            }

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
            Vector3 rawWorldPos = body.GetWorldSurfacePosition(lat, lon, terrainAlt - 1.0);
            double expectedClearance = ParsekFlight.ComputeTerrainClearance(
                Vector3d.Distance(rawWorldPos, activeVessel.GetWorldPos3D()));

            var ghostRoot = new GameObject("ParsekTestGhost_Bug525ExplosionAnchor");
            runner.TrackForCleanup(ghostRoot);
            ghostRoot.transform.position = rawWorldPos;

            var state = new GhostPlaybackState
            {
                vesselName = "Bug525ExplosionAnchor",
                ghost = ghostRoot,
                loopCycleIndex = 0,
                lastInterpolatedBodyName = body.name,
                lastInterpolatedAltitude = terrainAlt - 1.0
            };

            var traj = new Recording
            {
                RecordingId = "bug525-loop-explosion-anchor",
                VesselName = "Bug525ExplosionAnchor",
                TerrainHeightAtEnd = terrainAlt,
                LoopPlayback = true,
                LoopIntervalSeconds = 100.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
                TerminalStateValue = TerminalState.Destroyed
            };
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                latitude = lat,
                longitude = lon,
                altitude = terrainAlt + 5.0,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 200.0,
                latitude = lat,
                longitude = lon,
                altitude = terrainAlt - 1.0,
                bodyName = body.name,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            });

            var engine = new GhostPlaybackEngine(flight);
            engine.ghostStates[525] = state;
            var cameraEvents = new List<CameraActionEvent>();
            var restartedEvents = new List<LoopRestartedEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);
            engine.OnLoopRestarted += evt => restartedEvents.Add(evt);

            engine.UpdateLoopingPlaybackForTesting(
                index: 525,
                traj,
                flags: default,
                ctx: new FrameContext
                {
                    currentUT = 210.0,
                    warpRate = 1f,
                    activeVesselPos = activeVessel.GetWorldPos3D(),
                    protectedIndex = -1,
                    protectedLoopCycleIndex = -1,
                    autoLoopIntervalSeconds = 100.0,
                },
                suppressGhosts: false,
                suppressVisualFx: false);

            InGameAssert.AreEqual(1, cameraEvents.Count,
                "Loop cycle-change explosion should emit exactly one camera hold event");
            InGameAssert.AreEqual(1, restartedEvents.Count,
                "Loop cycle-change explosion should emit exactly one loop-restarted event");
            InGameAssert.AreEqual(CameraActionType.ExplosionHoldStart, cameraEvents[0].Action,
                "Destroyed loop cycle boundary must emit ExplosionHoldStart");
            InGameAssert.IsTrue(restartedEvents[0].ExplosionFired,
                "Loop restart event must mark the boundary explosion as fired");

            double eventAnchorAlt = body.GetAltitude(cameraEvents[0].AnchorPosition);
            double eventExplosionAlt = body.GetAltitude(restartedEvents[0].ExplosionPosition);

            InGameAssert.IsGreaterThan(eventAnchorAlt, terrainAlt + expectedClearance - 0.001,
                $"Explosion anchor must clamp above terrain+clearance before watch hold: expected >= {(terrainAlt + expectedClearance):F2}, got {eventAnchorAlt:F2}");
            InGameAssert.IsLessThan(
                System.Math.Abs(eventAnchorAlt - eventExplosionAlt), 0.01,
                "Loop camera hold and loop-restart explosion payloads must reuse the same terrain-clamped anchor");
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
            System.Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => captured.Add(line);

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
                ParsekLog.TestObserverForTesting = prevObserver;
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
            var prevObserver = Parsek.ParsekLog.TestObserverForTesting;
            Parsek.ParsekLog.TestObserverForTesting = line => logLines.Add(line);
            try
            {
                ParsekFlight.FinalizeIndividualRecording(
                    rec, Planetarium.GetUniversalTime(), isSceneExit: true);
            }
            finally
            {
                Parsek.ParsekLog.TestObserverForTesting = prevObserver;
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

        // ─────────────────────────────────────────────────────────────
        // Recording finalization cache runtime canaries
        // PIDs 910000–910099 are reserved for this category to avoid
        // collisions with other runtime tests (which use 900000-series).
        // ─────────────────────────────────────────────────────────────

        [InGameTest(Category = "RecordingFinalization", Scene = GameScenes.FLIGHT,
            Description = "Destroyed background cache trims predicted tail to the stock deletion UT")]
        public void RecordingFinalization_BackgroundDestroyedCache_TrimsAtDeletionUT()
        {
            uint vesselPid = 910001u;
            var tree = MakeRuntimeFinalizationTree(
                vesselPid,
                "runtime-bg-destroyed",
                pointUT: 100.0,
                altitude: 5000.0);
            var bgRecorder = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings["runtime-bg-destroyed"];
            bgRecorder.AdoptFinalizationCacheForTesting(
                vesselPid,
                rec.RecordingId,
                MakeRuntimeFinalizationCache(
                    rec.RecordingId,
                    vesselPid,
                    TerminalState.Destroyed,
                    terminalUT: 180.0,
                    SegmentRuntimeFinalization(100.0, 180.0)));

            var logLines = new List<string>();
            using (CaptureRuntimeFinalizationLogs(logLines))
            {
                bool applied = bgRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    vesselPid,
                    endUT: 130.0,
                    consumerPath: "runtime-background-destroyed",
                    allowStale: true,
                    requireDestroyedTerminal: false,
                    out RecordingFinalizationCacheApplyResult result);

                InGameAssert.IsTrue(applied, $"cache should apply, status={result.Status}");
                InGameAssert.AreEqual(TerminalState.Destroyed, rec.TerminalStateValue.Value);
                InGameAssert.ApproxEqual(130.0, rec.ExplicitEndUT);
                InGameAssert.AreEqual(1, rec.OrbitSegments.Count);
                InGameAssert.IsTrue(rec.OrbitSegments[0].isPredicted,
                    "Appended cache tail must stay marked predicted");
                InGameAssert.ApproxEqual(130.0, rec.OrbitSegments[0].endUT);
                AssertRuntimeFinalizationLog(
                    logLines,
                    "[Parsek][INFO][BgRecorder]",
                    "Finalization source=cache",
                    "consumer=runtime-background-destroyed");
            }
        }

        [InGameTest(Category = "RecordingFinalization", Scene = GameScenes.FLIGHT,
            Description = "Stable background cache applies Orbiting to a missing vessel instead of Destroyed")]
        public void RecordingFinalization_BackgroundStableCache_FinalizesOrbiting()
        {
            uint vesselPid = 910002u;
            var tree = MakeRuntimeFinalizationTree(
                vesselPid,
                "runtime-bg-orbiting",
                pointUT: 100.0,
                altitude: 90000.0);
            var bgRecorder = new BackgroundRecorder(tree);
            Recording rec = tree.Recordings["runtime-bg-orbiting"];
            RecordingFinalizationCache cache = MakeRuntimeFinalizationCache(
                rec.RecordingId,
                vesselPid,
                TerminalState.Orbiting,
                terminalUT: 120.0);
            cache.TerminalOrbit = RuntimeTerminalOrbit("Kerbin", 700000.0);
            bgRecorder.AdoptFinalizationCacheForTesting(vesselPid, rec.RecordingId, cache);

            var logLines = new List<string>();
            using (CaptureRuntimeFinalizationLogs(logLines))
            {
                bool applied = bgRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    vesselPid,
                    endUT: 150.0,
                    consumerPath: "runtime-background-orbiting",
                    allowStale: true,
                    requireDestroyedTerminal: false,
                    out RecordingFinalizationCacheApplyResult result);

                InGameAssert.IsTrue(applied, $"cache should apply, status={result.Status}");
                InGameAssert.AreEqual(TerminalState.Orbiting, rec.TerminalStateValue.Value);
                InGameAssert.ApproxEqual(150.0, rec.ExplicitEndUT);
                InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody);
                InGameAssert.AreEqual(0, rec.OrbitSegments.Count);
                AssertRuntimeFinalizationLog(
                    logLines,
                    "[Parsek][INFO][BgRecorder]",
                    "Finalization source=cache",
                    "terminal=Orbiting");
            }
        }

        [InGameTest(Category = "RecordingFinalization", Scene = GameScenes.FLIGHT,
            Description = "Non-scene active crash finalization consumes the active cache before Destroyed fallback")]
        public void RecordingFinalization_ActiveCrashCache_AppendsTailBeforeFallback()
        {
            var tree = new RecordingTree
            {
                Id = "runtime-active-crash-tree",
                TreeName = "Runtime Active Crash Cache",
                ActiveRecordingId = "runtime-active-crash"
            };
            var rec = new Recording
            {
                RecordingId = tree.ActiveRecordingId,
                TreeId = tree.Id,
                VesselName = "Runtime Active Crash",
                VesselPersistentId = 910003u,
                ChildBranchPointId = null,
                ExplicitEndUT = 100.0
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                altitude = 5000.0,
                bodyName = "Kerbin"
            });
            tree.Recordings[rec.RecordingId] = rec;

            RecordingFinalizationCache cache = MakeRuntimeFinalizationCache(
                rec.RecordingId,
                rec.VesselPersistentId,
                TerminalState.Destroyed,
                terminalUT: 155.0,
                SegmentRuntimeFinalization(100.0, 155.0));
            cache.Owner = FinalizationCacheOwner.ActiveRecorder;

            var logLines = new List<string>();
            using (CaptureRuntimeFinalizationLogs(logLines))
            {
                ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                    tree,
                    commitUT: 120.0,
                    isSceneExit: false,
                    resolveFinalizationCache: recording =>
                        recording.RecordingId == rec.RecordingId ? cache : null);

                InGameAssert.AreEqual(TerminalState.Destroyed, rec.TerminalStateValue.Value);
                InGameAssert.ApproxEqual(155.0, rec.ExplicitEndUT);
                InGameAssert.AreEqual(1, rec.OrbitSegments.Count);
                InGameAssert.IsTrue(rec.OrbitSegments[0].isPredicted,
                    "Active crash fallback should preserve the cached synthetic tail");
                AssertRuntimeFinalizationLog(
                    logLines,
                    "[Parsek][INFO][Flight]",
                    "Finalization source=cache",
                    "consumer=FinalizeIndividualRecording");
            }
        }

        private static System.IDisposable CaptureRuntimeFinalizationLogs(List<string> sink)
        {
            return new RuntimeFinalizationLogScope(sink);
        }

        private static void AssertRuntimeFinalizationLog(
            List<string> logLines,
            params string[] requiredSubstrings)
        {
            for (int i = 0; i < logLines.Count; i++)
            {
                bool match = true;
                for (int s = 0; s < requiredSubstrings.Length; s++)
                {
                    if (!logLines[i].Contains(requiredSubstrings[s]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return;
            }

            InGameAssert.Fail(
                "Expected a log line containing: " + string.Join(" + ", requiredSubstrings) +
                ". Captured " + logLines.Count + " lines.");
        }

        private sealed class RuntimeFinalizationLogScope : System.IDisposable
        {
            private readonly System.Action<string> previousSink;
            private readonly bool? previousVerbose;
            private readonly bool previousSuppress;

            internal RuntimeFinalizationLogScope(List<string> sink)
            {
                previousSink = ParsekLog.TestSinkForTesting;
                previousVerbose = ParsekLog.VerboseOverrideForTesting;
                previousSuppress = ParsekLog.SuppressLogging;
                ParsekLog.SuppressLogging = false;
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestSinkForTesting = line => sink.Add(line);
            }

            public void Dispose()
            {
                ParsekLog.TestSinkForTesting = previousSink;
                ParsekLog.VerboseOverrideForTesting = previousVerbose;
                ParsekLog.SuppressLogging = previousSuppress;
            }
        }

        private static RecordingTree MakeRuntimeFinalizationTree(
            uint vesselPid,
            string recordingId,
            double pointUT,
            double altitude)
        {
            var tree = new RecordingTree
            {
                Id = "runtime-finalization-tree-" + recordingId,
                TreeName = "Runtime Finalization Cache",
                RootRecordingId = "runtime-active",
                ActiveRecordingId = "runtime-active"
            };
            tree.Recordings["runtime-active"] = new Recording
            {
                RecordingId = "runtime-active",
                VesselName = "Runtime Active",
                ExplicitStartUT = pointUT - 10.0,
                ExplicitEndUT = pointUT
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                TreeId = tree.Id,
                VesselName = recordingId,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = pointUT,
                ExplicitEndUT = pointUT
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = pointUT,
                altitude = altitude,
                bodyName = "Kerbin"
            });
            tree.Recordings[recordingId] = rec;
            tree.BackgroundMap[vesselPid] = recordingId;
            return tree;
        }

        private static RecordingFinalizationCache MakeRuntimeFinalizationCache(
            string recordingId,
            uint vesselPid,
            TerminalState terminalState,
            double terminalUT,
            params OrbitSegment[] segments)
        {
            var cache = new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPid,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = terminalUT - 5.0,
                RefreshReason = "runtime-test",
                LastObservedUT = terminalUT - 5.0,
                LastObservedBodyName = "Kerbin",
                LastSituation = terminalState == TerminalState.Orbiting
                    ? Vessel.Situations.ORBITING
                    : Vessel.Situations.FLYING,
                LastWasInAtmosphere = terminalState == TerminalState.Destroyed,
                TailStartsAtUT = segments != null && segments.Length > 0
                    ? segments[0].startUT
                    : terminalUT,
                TerminalUT = terminalUT,
                TerminalState = terminalState,
                TerminalBodyName = "Kerbin",
                PredictedSegments = new List<OrbitSegment>()
            };

            if (segments != null)
            {
                for (int i = 0; i < segments.Length; i++)
                    cache.PredictedSegments.Add(segments[i]);
            }

            return cache;
        }

        private static OrbitSegment SegmentRuntimeFinalization(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = 700000.0,
                eccentricity = 0.1,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                isPredicted = true
            };
        }

        private static RecordingFinalizationTerminalOrbit RuntimeTerminalOrbit(
            string bodyName,
            double semiMajorAxis)
        {
            return new RecordingFinalizationTerminalOrbit
            {
                bodyName = bodyName,
                semiMajorAxis = semiMajorAxis,
                eccentricity = 0.01,
                inclination = 2.0,
                longitudeOfAscendingNode = 3.0,
                argumentOfPeriapsis = 4.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = Planetarium.GetUniversalTime()
            };
        }

        // ── QuickloadResume (#269) ──────────────────────────────────────────────

        /// <summary>
        /// Canary test: verifies that the TestRunnerShortcut singleton survives a
        /// scene transition via quickload. This drives the stock programmatic
        /// quickload backend and remains intentionally single-run only: when
        /// stock flight restore itself fails, it can still leave the live
        /// FLIGHT session hung or broken. Use a disposable/manual-backup save.
        /// </summary>
        [InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this drives KSP's stock programmatic quickload backend. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
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
        /// #487 regression: scene reset must not cache a transparent opaque window
        /// style while the destination scene skin is still bootstrapping.
        /// </summary>
        [InGameTest(Category = "TestRunner",
            Description = "Scene reset defers opaque window rebuild until a skin background exists")]
        public void SceneReset_DefersOpaqueStyleUntilSkinReady()
        {
            var shortcut = TestRunnerShortcut.Instance;
            InGameAssert.IsNotNull(shortcut, "TestRunnerShortcut.Instance must exist");

            MethodInfo onSceneChange = typeof(TestRunnerShortcut).GetMethod(
                "OnSceneChangeRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);
            InGameAssert.IsNotNull(onSceneChange,
                "Failed to reflect TestRunnerShortcut.OnSceneChangeRequested");

            var readySkin = ScriptableObject.CreateInstance<GUISkin>();
            var missingBackgroundSkin = ScriptableObject.CreateInstance<GUISkin>();
            var sourceBackground = new Texture2D(2, 2, TextureFormat.ARGB32, false);

            try
            {
                sourceBackground.SetPixels(new[]
                {
                    new Color(0.18f, 0.24f, 0.31f, 0.35f),
                    new Color(0.20f, 0.27f, 0.34f, 0.40f),
                    new Color(0.22f, 0.29f, 0.36f, 0.45f),
                    new Color(0.24f, 0.31f, 0.38f, 0.50f),
                });
                sourceBackground.Apply();

                onSceneChange.Invoke(shortcut, new object[] { HighLogic.LoadedScene });
                InGameAssert.IsFalse(shortcut.HasOpaqueStyleForTesting,
                    "Test must start from a cleared opaque-style cache");

                readySkin.window = new GUIStyle();
                readySkin.window.normal.background = sourceBackground;

                missingBackgroundSkin.window = new GUIStyle();

                bool builtBeforeReset = shortcut.TryEnsureOpaqueStyleForTesting(readySkin);
                InGameAssert.IsTrue(builtBeforeReset,
                    "Opaque style should build when a window background exists");
                InGameAssert.IsTrue(shortcut.HasOpaqueStyleForTesting,
                    "Opaque style must be cached before the scene reset");
                InGameAssert.IsNotNull(shortcut.OpaqueWindowBackgroundForTesting,
                    "Opaque style cache must keep a non-null window background before reset");
                InGameAssert.IsTrue(shortcut.HasAllOpaqueStateBackgroundsForTesting,
                    "Lagging hover/focus/active states must fall back to the ready normal background");
                InGameAssert.AreNotEqual(sourceBackground.GetInstanceID(),
                    shortcut.OpaqueWindowBackgroundForTesting.GetInstanceID(),
                    "Opaque style must keep a copied background texture instead of the live skin texture");

                onSceneChange.Invoke(shortcut, new object[] { HighLogic.LoadedScene });
                InGameAssert.IsFalse(shortcut.HasOpaqueStyleForTesting,
                    "Scene reset should clear the cached opaque style before the next rebuild");

                bool builtWithMissingBackground = shortcut.TryEnsureOpaqueStyleForTesting(missingBackgroundSkin);
                InGameAssert.IsFalse(builtWithMissingBackground,
                    "Opaque style rebuild must defer when the destination scene skin has no window background yet");
                InGameAssert.IsFalse(shortcut.HasOpaqueStyleForTesting,
                    "Deferred rebuild must not cache an opaque style with null backgrounds");
                InGameAssert.IsNull(shortcut.OpaqueWindowBackgroundForTesting,
                    "Deferred rebuild must leave the cached opaque background unset");

                bool builtAfterSkinReady = shortcut.TryEnsureOpaqueStyleForTesting(readySkin);
                InGameAssert.IsTrue(builtAfterSkinReady,
                    "Opaque style rebuild should succeed once the scene skin is ready");
                InGameAssert.IsTrue(shortcut.HasOpaqueStyleForTesting,
                    "Ready-skin rebuild must repopulate the cached opaque style");
                InGameAssert.IsNotNull(shortcut.OpaqueWindowBackgroundForTesting,
                    "Ready-skin rebuild must cache a non-null opaque background");
                InGameAssert.IsTrue(shortcut.HasAllOpaqueStateBackgroundsForTesting,
                    "Ready-skin rebuild must populate every window state background");
            }
            finally
            {
                onSceneChange.Invoke(shortcut, new object[] { HighLogic.LoadedScene });
                Object.Destroy(readySkin);
                Object.Destroy(missingBackgroundSkin);
                Object.Destroy(sourceBackground);
            }
        }

        /// <summary>
        /// #487 follow-up: the test runner draw path must neutralize leaked IMGUI tint
        /// state so a scene transition cannot leave the window visually transparent.
        /// </summary>
        [InGameTest(Category = "TestRunner",
            Description = "Window drawing normalizes leaked GUI tint and restores the prior state")]
        public void WindowDraw_NormalizesGuiTintAndRestoresState()
        {
            var shortcut = TestRunnerShortcut.Instance;
            InGameAssert.IsNotNull(shortcut, "TestRunnerShortcut.Instance must exist");

            Color incomingColor = GUI.color;
            Color incomingBackground = GUI.backgroundColor;
            Color incomingContent = GUI.contentColor;
            Color originalColor = new Color(0.2f, 0.3f, 0.4f, 0.25f);
            Color originalBackground = new Color(0.5f, 0.4f, 0.3f, 0.15f);
            Color originalContent = new Color(0.6f, 0.7f, 0.8f, 0.35f);
            try
            {
                GUI.color = originalColor;
                GUI.backgroundColor = originalBackground;
                GUI.contentColor = originalContent;

                Color observedColor = Color.clear;
                Color observedBackground = Color.clear;
                Color observedContent = Color.clear;

                int result = shortcut.RunWindowWithNormalizedGuiColorsForTesting(() =>
                {
                    observedColor = GUI.color;
                    observedBackground = GUI.backgroundColor;
                    observedContent = GUI.contentColor;
                    GUI.color = Color.red;
                    GUI.backgroundColor = Color.green;
                    GUI.contentColor = Color.blue;
                    return 7;
                });

                InGameAssert.AreEqual(7, result, "Wrapped callback return value should flow through unchanged");
                InGameAssert.IsTrue(Mathf.Approximately(observedColor.a, 1f)
                        && Mathf.Approximately(observedColor.r, 1f)
                        && Mathf.Approximately(observedColor.g, 1f)
                        && Mathf.Approximately(observedColor.b, 1f),
                    "Window draw should force GUI.color to opaque white inside the callback");
                InGameAssert.IsTrue(Mathf.Approximately(observedBackground.a, 1f)
                        && Mathf.Approximately(observedBackground.r, 1f)
                        && Mathf.Approximately(observedBackground.g, 1f)
                        && Mathf.Approximately(observedBackground.b, 1f),
                    "Window draw should force GUI.backgroundColor to opaque white inside the callback");
                InGameAssert.IsTrue(Mathf.Approximately(observedContent.a, 1f)
                        && Mathf.Approximately(observedContent.r, 1f)
                        && Mathf.Approximately(observedContent.g, 1f)
                        && Mathf.Approximately(observedContent.b, 1f),
                    "Window draw should force GUI.contentColor to opaque white inside the callback");
                InGameAssert.IsTrue(Mathf.Approximately(GUI.color.r, originalColor.r)
                        && Mathf.Approximately(GUI.color.g, originalColor.g)
                        && Mathf.Approximately(GUI.color.b, originalColor.b)
                        && Mathf.Approximately(GUI.color.a, originalColor.a),
                    "Window draw should restore the previous GUI.color after the callback");
                InGameAssert.IsTrue(Mathf.Approximately(GUI.backgroundColor.r, originalBackground.r)
                        && Mathf.Approximately(GUI.backgroundColor.g, originalBackground.g)
                        && Mathf.Approximately(GUI.backgroundColor.b, originalBackground.b)
                        && Mathf.Approximately(GUI.backgroundColor.a, originalBackground.a),
                    "Window draw should restore the previous GUI.backgroundColor after the callback");
                InGameAssert.IsTrue(Mathf.Approximately(GUI.contentColor.r, originalContent.r)
                        && Mathf.Approximately(GUI.contentColor.g, originalContent.g)
                        && Mathf.Approximately(GUI.contentColor.b, originalContent.b)
                        && Mathf.Approximately(GUI.contentColor.a, originalContent.a),
                    "Window draw should restore the previous GUI.contentColor after the callback");
            }
            finally
            {
                GUI.color = incomingColor;
                GUI.backgroundColor = incomingBackground;
                GUI.contentColor = incomingContent;
            }
        }

        /// <summary>
        /// #269 core test: quickload mid-recording resumes with the same activeRecordingId.
        /// Verifies the full F5 → fly → F9 → restore coroutine → resumed recording path.
        /// This also drives KSP's stock programmatic quickload backend, so it
        /// remains intentionally single-run only while stock quickload itself can
        /// still destabilize the current FLIGHT session on failure. Use a
        /// disposable/manual-backup save before running it.
        /// </summary>
        [InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this drives KSP's stock programmatic quickload backend. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "F5/F9 mid-recording resumes same activeRecordingId")]
        public IEnumerator Quickload_MidRecording_ResumesSameActiveRecordingId()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }

            bool startedRecordingForTest = false;
            bool originalAutoRecordOnLaunch = false;
            float originalThrottle = 0f;
            bool autoRecordStateCaptured = false;
            bool throttleCaptured = false;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;

            try
            {
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestObserverForTesting = line =>
                {
                    captured.Add(line);
                    priorObserver?.Invoke(line);
                };

                if (!flight.IsRecording)
                {
                    if (vessel.situation != Vessel.Situations.PRELAUNCH)
                    {
                        InGameAssert.Skip(
                            $"requires either an already-live launched recording or an idle PRELAUNCH vessel, got {vessel.situation}");
                        yield break;
                    }
                    if (ParsekSettings.Current == null)
                    {
                        InGameAssert.Skip("ParsekSettings.Current is null");
                        yield break;
                    }
                    originalAutoRecordOnLaunch = ParsekSettings.Current.autoRecordOnLaunch;
                    autoRecordStateCaptured = true;

                    ParsekSettings.Current.autoRecordOnLaunch = true;
                    yield return InGameTestRunner.WaitForStockStageManagerReady(10f);
                    yield return RuntimeTests.WaitForFlightInputStateReady(5f);
                    originalThrottle = FlightInputHandler.state.mainThrottle;
                    throttleCaptured = true;
                    FlightInputHandler.state.mainThrottle = 1f;
                    KSP.UI.Screens.StageManager.ActivateNextStage();

                    yield return RuntimeTests.WaitForLaunchAutoRecordStart(10f);
                    yield return Helpers.QuickloadResumeHelpers.WaitForActiveRecording(10f);
                    yield return new WaitForSeconds(0.5f);

                    startedRecordingForTest = true;
                    flight = ParsekFlight.Instance;
                    vessel = FlightGlobals.ActiveVessel;
                    InGameAssert.IsNotNull(flight, "ParsekFlight.Instance must survive launch auto-record setup");
                    InGameAssert.IsNotNull(vessel, "Active vessel must exist after launch auto-record setup");
                    InGameAssert.IsTrue(vessel.situation != Vessel.Situations.PRELAUNCH,
                        "Quickload mid-recording setup must leave PRELAUNCH before F5");

                    int autoStartCount = captured.Count(
                        l => l.Contains("[Flight]") && l.Contains("Auto-record started ("));
                    InGameAssert.AreEqual(1, autoStartCount,
                        $"Expected exactly one launch auto-record log line during quickload setup, got {autoStartCount}");
                }
                else if (vessel.situation == Vessel.Situations.PRELAUNCH)
                {
                    InGameAssert.Skip("requires an already-live launched recording or an idle PRELAUNCH vessel");
                    yield break;
                }

                yield return RuntimeTests.WaitForActiveRecordingPoint(flight, 5f);
                string preRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(preRecId, "ActiveRecordingId must be set before F5");
                InGameAssert.IsTrue(
                    flight.ActiveTreeForSerialization.Recordings.TryGetValue(preRecId, out _),
                    $"Active tree must contain the active recording '{preRecId}' before F5");
                RuntimeTests.HasObservedActiveRecordingPoint(
                    flight,
                    out int preTreePointCount,
                    out int preBufferedPointCount,
                    out double preLastRecordedUT);
                InGameAssert.IsTrue(
                    preTreePointCount > 0
                    || preBufferedPointCount > 0
                    || !double.IsNaN(preLastRecordedUT),
                    "Quickload mid-recording setup must capture at least one live trajectory sample before F5");
                int preFlightInstanceId = flight.GetInstanceID();

                ParsekLog.Info("TestRunner",
                    $"Quickload mid-recording setup: vessel='{FlightGlobals.ActiveVessel?.vesselName}' " +
                    $"situation={FlightGlobals.ActiveVessel?.situation} preRecId={preRecId} " +
                    $"treePoints={preTreePointCount} bufferedPoints={preBufferedPointCount} " +
                    $"lastRecordedUT={(double.IsNaN(preLastRecordedUT) ? "NaN" : preLastRecordedUT.ToString("F2"))} " +
                    $"startedRecordingForTest={startedRecordingForTest}");

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
            finally
            {
                if (throttleCaptured && FlightInputHandler.state != null)
                    FlightInputHandler.state.mainThrottle = originalThrottle;
                if (autoRecordStateCaptured && ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnLaunch = originalAutoRecordOnLaunch;
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;

                var cleanupFlight = ParsekFlight.Instance;
                if (startedRecordingForTest && cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();
            }
        }

        /// <summary>
        /// Stock Revert to Launch player-flow canary. Starts a real recording,
        /// launches the active vessel, then drives KSP's stock Revert-to-Launch
        /// backend. The expected shipped behavior after #434 is soft-unstash:
        /// the pending tree is cleared without a merge dialog and the reverted
        /// flight does not commit into the timeline. Use a disposable/manual-
        /// backup save before running it.
        /// </summary>
        [InGameTest(Category = "RevertFlow", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test starts a real recording, stages the active vessel, and drives stock Revert to Launch in the live FLIGHT session. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Stock Revert to Launch soft-unstashes a live recording without opening the merge dialog")]
        public IEnumerator RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                InGameAssert.Skip(
                    $"requires a PRELAUNCH vessel on the pad so the test can launch and then stock-revert, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle prelaunch vessel (recording already active)");
                yield break;
            }
            if (FlightInputHandler.state == null)
            {
                InGameAssert.Skip("FlightInputHandler.state is null");
                yield break;
            }
            if (FlightDriverRevertToLaunchMethod == null || FlightDriverCanRevertProperty == null)
            {
                InGameAssert.Skip("FlightDriver revert-to-launch reflection surface unavailable on this KSP build");
                yield break;
            }

            int committedBefore = RecordingStore.CommittedRecordings.Count;
            int committedTreesBefore = RecordingStore.CommittedTrees.Count;
            float originalThrottle = FlightInputHandler.state.mainThrottle;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            try
            {
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                flight.StartRecording();
                InGameAssert.IsTrue(flight.IsRecording,
                    "ParsekFlight.StartRecording should start a live recording before the stock revert flow");

                string activeRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set before staging the live revert-flow canary");

                yield return new WaitForSeconds(0.5f);

                FlightInputHandler.state.mainThrottle = 1f;
                KSP.UI.Screens.StageManager.ActivateNextStage();

                yield return WaitForRecordingToLeavePrelaunch(activeRecId, 10f);
                yield return new WaitForSeconds(1.0f);

                bool canRevert = (bool)(FlightDriverCanRevertProperty.GetValue(null, null) ?? false);
                InGameAssert.IsTrue(canRevert,
                    "FlightDriver.CanRevert should be true after the staged vessel leaves PRELAUNCH");

                int previousFlightInstanceId = flight.GetInstanceID();
                try
                {
                    FlightDriverRevertToLaunchMethod.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    InGameAssert.Fail(
                        $"FlightDriver.RevertToLaunch threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                        $"{ex.InnerException?.Message ?? ex.Message}");
                }

                yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(previousFlightInstanceId, 15f);
                yield return new WaitForSeconds(0.5f);
                yield return AssertNoPopupDialog("ParsekMerge", 2.5f);

                var postFlight = ParsekFlight.Instance;
                var postVessel = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(postFlight, "ParsekFlight.Instance must exist after stock revert");
                InGameAssert.IsNotNull(postVessel, "Active vessel must exist after stock revert");
                InGameAssert.IsFalse(postFlight.IsRecording,
                    "Stock revert should end the pre-revert live recording instead of resuming it");
                InGameAssert.IsFalse(RecordingStore.HasPendingTree,
                    "Stock revert should soft-unstash the pending tree instead of surfacing the merge dialog");
                InGameAssert.AreEqual(committedBefore, RecordingStore.CommittedRecordings.Count,
                    "Revert-to-launch must not commit the reverted recording into the timeline");
                InGameAssert.AreEqual(committedTreesBefore, RecordingStore.CommittedTrees.Count,
                    "Revert-to-launch must not commit a reverted tree into CommittedTrees");
                InGameAssert.IsTrue(
                    postVessel.situation == Vessel.Situations.PRELAUNCH || postVessel.LandedOrSplashed,
                    $"Revert-to-launch should bring the vessel back to a launch-site state, got {postVessel.situation}");

                bool sawKeepFreshPending = captured.Any(
                    l => l.Contains("Revert: keeping freshly-stashed pending")
                        || l.Contains("Revert: keeping pending Limbo tree"));
                bool sawSoftUnstash = captured.Any(
                    l => l.Contains("Unstashed pending tree '") && l.Contains("sidecar files preserved"));
                InGameAssert.IsTrue(sawKeepFreshPending,
                    "Expected revert OnLoad to keep the freshly-stashed pending tree long enough to classify it");
                InGameAssert.IsTrue(sawSoftUnstash,
                    "Expected revert OnLoad to soft-unstash the pending tree instead of committing or discarding it");

                ParsekLog.Info("TestRunner",
                    $"Revert flow runtime: rec='{activeRecId}' postVessel='{postVessel.vesselName}' " +
                    $"situation={postVessel.situation} committedBefore={committedBefore} " +
                    $"committedAfter={RecordingStore.CommittedRecordings.Count}");
            }
            finally
            {
                FlightInputHandler.state.mainThrottle = originalThrottle;
                ParsekLog.TestObserverForTesting = priorObserver;
                if (ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording)
                    ParsekFlight.Instance.StopRecording();
            }
        }

        /// <summary>
        /// Live rewind canary for #527. Commits a real launch recording to get a real
        /// rewind save, injects future ledger actions, then drives the actual rewind
        /// load path and asserts the post-rewind FLIGHT follow-up keeps those future
        /// funds/contracts filtered.
        /// </summary>
        [InGameTest(Category = "RewindFlow", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test commits a real launch recording, injects future ledger actions, and drives a live rewind in the current FLIGHT session. Use Run All + Isolated or the row play button in a disposable Career-mode FLIGHT session.",
            Description = "Live rewind keeps future funds/contracts filtered during the post-rewind FLIGHT load follow-up")]
        public IEnumerator RewindToLaunch_PostRewindFlightLoad_KeepsFutureFundsAndContractsFiltered()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("requires a Career-mode FLIGHT save");
                yield break;
            }

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                InGameAssert.Skip(
                    $"requires a PRELAUNCH vessel on the pad so the test can launch, commit, and rewind, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle prelaunch vessel (recording already active)");
                yield break;
            }
            if (FlightInputHandler.state == null)
            {
                InGameAssert.Skip("FlightInputHandler.state is null");
                yield break;
            }
            if (Funding.Instance == null)
            {
                InGameAssert.Skip("Funding.Instance is null — this live rewind cutoff canary needs career funds");
                yield break;
            }

            int committedBefore = RecordingStore.CommittedRecordings.Count;
            float originalThrottle = FlightInputHandler.state.mainThrottle;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            string syntheticLedgerTag = null;

            try
            {
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                flight.StartRecording();
                InGameAssert.IsTrue(flight.IsRecording,
                    "ParsekFlight.StartRecording should start a live recording before the rewind canary");

                string activeRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set before staging the live rewind canary");

                yield return new WaitForSeconds(0.5f);

                FlightInputHandler.state.mainThrottle = 1f;
                KSP.UI.Screens.StageManager.ActivateNextStage();

                yield return WaitForRecordingToLeavePrelaunch(activeRecId, 10f);
                yield return RuntimeTests.WaitForActiveRecordingPoint(flight, 5f);
                yield return new WaitForSeconds(0.5f);

                FlightInputHandler.state.mainThrottle = 0f;
                flight.StopRecording();
                yield return WaitForCommittedRecording(activeRecId, committedBefore, 10f);

                Recording committedRecording = RecordingStore.CommittedRecordings.FirstOrDefault(
                    r => r != null && r.RecordingId == activeRecId);
                InGameAssert.IsNotNull(committedRecording,
                    "Stopping the live rewind canary recording should commit it into the timeline");
                InGameAssert.IsTrue(!string.IsNullOrEmpty(committedRecording.RewindSaveFileName),
                    "Committed rewind canary recording must have a rewind save file");

                double fundsBeforeFutureActions = Funding.Instance.Funds;
                int activeContractsBeforeFutureActions = LedgerOrchestrator.Contracts.GetActiveContractCount();

                syntheticLedgerTag = "ingame-rewind-cutoff-" + System.Guid.NewGuid().ToString("N");
                string futureContractId = syntheticLedgerTag + "-contract";
                double futureUT = Planetarium.GetUniversalTime() + 120.0;

                Ledger.AddAction(new GameAction
                {
                    UT = futureUT,
                    Type = GameActionType.ContractAccept,
                    RecordingId = syntheticLedgerTag,
                    ContractId = futureContractId,
                    ContractType = "ParsekRewindCutoffCanary",
                    ContractTitle = "Parsek Rewind Cutoff Canary",
                    AdvanceFunds = 321f,
                    DeadlineUT = (float)(futureUT + 3600.0)
                });
                Ledger.AddAction(new GameAction
                {
                    UT = futureUT + 1.0,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = syntheticLedgerTag,
                    MilestoneId = syntheticLedgerTag + "-milestone",
                    MilestoneFundsAwarded = 654f
                });

                InGameAssert.IsTrue(
                    LedgerOrchestrator.HasActionsAfterUT(Planetarium.GetUniversalTime()),
                    "Injected future ledger actions should sit after the current UT before rewind");

                int previousFlightInstanceId = flight.GetInstanceID();
                RecordingStore.InitiateRewind(committedRecording);

                yield return Helpers.QuickloadResumeHelpers.WaitForFlightReady(previousFlightInstanceId, 20f);
                yield return WaitForCapturedLogLine(
                    captured,
                    "post-rewind FLIGHT recalc using current-UT cutoff",
                    10f);
                yield return new WaitForSeconds(0.5f);

                InGameAssert.IsNotNull(Funding.Instance,
                    "Funding.Instance must exist after the live rewind cutoff canary reloads");

                double postFunds = Funding.Instance.Funds;
                int postActiveContracts = LedgerOrchestrator.Contracts.GetActiveContractCount();
                bool sawDecisionInputs = captured.Any(
                    line => line.Contains("post-rewind FLIGHT cutoff decision")
                        && line.Contains("useCurrentUtCutoff=True")
                        && line.Contains("hasFutureLedgerActions=True"));

                InGameAssert.IsTrue(
                    sawDecisionInputs,
                    "Expected OnLoad to log the post-rewind FLIGHT cutoff decision inputs");
                InGameAssert.IsTrue(
                    System.Math.Abs(postFunds - fundsBeforeFutureActions) < 1.0,
                    $"Post-rewind funds should stay at the pre-future baseline until replay catches up " +
                    $"(before={fundsBeforeFutureActions:F1}, after={postFunds:F1})");
                InGameAssert.AreEqual(
                    activeContractsBeforeFutureActions,
                    postActiveContracts,
                    "Post-rewind active-contract count should stay at the pre-future baseline");
                InGameAssert.IsFalse(
                    LedgerOrchestrator.Contracts.GetActiveContractIds().Contains(futureContractId),
                    "Future contract should stay filtered until replay catches up");

                ParsekLog.Info("TestRunner",
                    $"Rewind cutoff runtime: rec='{activeRecId}' fundsBefore={fundsBeforeFutureActions.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"fundsAfter={postFunds.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"activeContractsBefore={activeContractsBeforeFutureActions} activeContractsAfter={postActiveContracts}");
            }
            finally
            {
                if (FlightInputHandler.state != null)
                    FlightInputHandler.state.mainThrottle = originalThrottle;
                ParsekLog.TestObserverForTesting = priorObserver;

                if (!string.IsNullOrEmpty(syntheticLedgerTag))
                {
                    try
                    {
                        Ledger.RemoveActionsForRecording(syntheticLedgerTag);
                        if (HighLogic.CurrentGame != null)
                            LedgerOrchestrator.RecalculateAndPatch();
                    }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"Rewind cutoff runtime cleanup failed for synthetic ledger tag '{syntheticLedgerTag}': {ex.Message}");
                    }
                }

                if (ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording)
                    ParsekFlight.Instance.StopRecording();
            }
        }

        /// <summary>
        /// Stock non-revert scene-exit player-flow canary. Starts a real recording,
        /// launches the active vessel far enough to avoid the idle-on-pad discard
        /// heuristic, then drives the same save-and-exit-to-SpaceCenter path that
        /// stock PauseMenu uses. The expected shipped behavior after #434 with
        /// auto-merge disabled is a deferred merge dialog in SPACECENTER whose
        /// "Merge to Timeline" button commits the pending tree. Use a disposable/
        /// manual-backup save before running it.
        /// </summary>
        [InGameTest(Category = "SceneExitMerge", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — excluded from Run All / Run category because this test starts a real recording, launches the active vessel, exits FLIGHT through stock save-and-exit semantics, and then drives the deferred merge dialog in SPACECENTER.",
            Description = "Space Center exit shows deferred merge dialog and Merge to Timeline commits the pending tree")]
        public IEnumerator ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                InGameAssert.Skip(
                    $"requires a PRELAUNCH vessel on the pad so the test can stage and then exit to Space Center, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle prelaunch vessel (recording already active)");
                yield break;
            }
            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }
            if (FlightInputHandler.state == null)
            {
                InGameAssert.Skip("FlightInputHandler.state is null");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MultiOptionDialogTitleField == null
                || MultiOptionDialogOptionsField == null
                || DialogGuiButtonTextField == null
                || DialogGuiButtonOptionSelectedMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            int committedBefore = RecordingStore.CommittedRecordings.Count;
            int committedTreesBefore = RecordingStore.CommittedTrees.Count;
            float originalThrottle = FlightInputHandler.state.mainThrottle;
            bool originalAutoMerge = ParsekSettings.Current.autoMerge;
            Vector3d launchWorldPosition = vessel.GetWorldPos3D();
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            string treeId = null;
            string activeRecId = null;

            try
            {
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };
                PopupDialog.DismissPopup("ParsekMerge");
                ParsekSettings.Current.autoMerge = false;

                flight.StartRecording();
                InGameAssert.IsTrue(flight.IsRecording,
                    "ParsekFlight.StartRecording should start a live recording before the Space Center exit flow");

                treeId = flight.ActiveTreeForSerialization?.Id;
                activeRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(treeId, "Active tree id should exist before the Space Center exit flow");
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set before staging the Space Center exit flow");

                yield return new WaitForSeconds(0.5f);

                FlightInputHandler.state.mainThrottle = 1f;
                KSP.UI.Screens.StageManager.ActivateNextStage();

                yield return WaitForRecordingToLeavePrelaunch(activeRecId, 10f);
                yield return WaitForRecordingToClearPad(launchWorldPosition, 80.0, activeRecId, 10f);
                yield return new WaitForSeconds(0.5f);

                TriggerSaveAndExitToSpaceCenter();
                yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
                yield return WaitForPopupDialog("ParsekMerge", 10f);

                PopupDialog popup = FindPopupDialog("ParsekMerge");
                InGameAssert.IsNotNull(popup,
                    "ParsekMerge popup should exist after the real Space Center exit");

                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                InGameAssert.IsNotNull(dialog, "Popup should expose a MultiOptionDialog");

                string dialogTitle = MultiOptionDialogTitleField.GetValue(dialog) as string;
                InGameAssert.AreEqual("Parsek - Merge to Timeline", dialogTitle,
                    "Deferred merge dialog title should match the production popup");

                DialogGUIButton[] buttons = GetDialogButtons(dialog);
                DialogGUIButton mergeButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Merge to Timeline");
                DialogGUIButton discardButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Discard");

                InGameAssert.IsNotNull(mergeButton, "Merge to Timeline button should exist");
                InGameAssert.IsNotNull(discardButton, "Discard button should exist");
                InGameAssert.IsTrue(RecordingStore.HasPendingTree,
                    "Real Space Center exit should surface a pending tree before the merge click");
                InGameAssert.AreEqual(treeId, RecordingStore.PendingTree?.Id,
                    "Real Space Center exit should keep the same pending tree id into SPACECENTER");
                InGameAssert.IsTrue(ParsekScenario.MergeDialogPending,
                    "Deferred merge dialog should keep the pending-dialog flag armed until the click");

                DialogGuiButtonOptionSelectedMethod.Invoke(mergeButton, null);

                yield return WaitForPopupDialogToClose("ParsekMerge", 3f);
                yield return null;

                InGameAssert.IsFalse(RecordingStore.HasPendingTree,
                    "Merge to Timeline should consume the pending tree after real scene exit");
                InGameAssert.IsFalse(ParsekScenario.MergeDialogPending,
                    "Merge to Timeline should clear the deferred merge-dialog flag");
                InGameAssert.AreEqual(committedTreesBefore + 1, RecordingStore.CommittedTrees.Count,
                    "Merge to Timeline should add exactly one committed tree after the real scene exit");
                InGameAssert.IsGreaterThan(RecordingStore.CommittedRecordings.Count, committedBefore,
                    "Merge to Timeline should add committed recordings after the real scene exit");

                RecordingTree committedTree = RecordingStore.CommittedTrees.FirstOrDefault(
                    candidate => candidate != null && candidate.Id == treeId);
                InGameAssert.IsNotNull(committedTree,
                    "Merge to Timeline should commit the live pending tree into CommittedTrees");
                bool recordingCommitted = RecordingStore.CommittedRecordings.Any(
                    rec => rec != null && rec.TreeId == treeId && rec.RecordingId == activeRecId);
                InGameAssert.IsTrue(recordingCommitted,
                    "CommittedRecordings should contain the merged tree's active recording");

                bool sawDeferredDialog = captured.Any(
                    line => line.Contains("Showing deferred tree merge dialog in SPACECENTER"));
                bool sawUserMerge = captured.Any(
                    line => line.Contains("User chose: Tree Merge"));
                InGameAssert.IsTrue(sawDeferredDialog,
                    "Expected the real scene-exit flow to log the deferred merge dialog in SPACECENTER");
                InGameAssert.IsTrue(sawUserMerge,
                    "Expected the real scene-exit flow to log the Merge to Timeline branch");

                ParsekLog.Info("TestRunner",
                    $"Scene-exit merge commit runtime: treeId={treeId} recId={activeRecId} " +
                    $"committedTreesBefore={committedTreesBefore} committedTreesAfter={RecordingStore.CommittedTrees.Count}");
            }
            finally
            {
                PopupDialog.DismissPopup("ParsekMerge");
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoMerge = originalAutoMerge;
                ParsekLog.TestObserverForTesting = priorObserver;
                if (RecordingStore.HasPendingTree && RecordingStore.PendingTree?.Id == treeId)
                    ParsekScenario.DiscardPendingTreeAndRecalculate("scene-exit merge commit canary cleanup");
                RemoveCommittedTreeByIdForRuntimeTest(treeId, recalculateAfterRemoval: true);
                if (ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording)
                    ParsekFlight.Instance.StopRecording();
                if (FlightInputHandler.state != null)
                    FlightInputHandler.state.mainThrottle = originalThrottle;
            }
        }

        /// <summary>
        /// Stock non-revert scene-exit discard canary. Starts a real recording,
        /// launches the active vessel far enough to avoid the idle-on-pad discard
        /// heuristic, then drives the same save-and-exit-to-SpaceCenter path that
        /// stock PauseMenu uses. The expected shipped behavior after #434 with
        /// auto-merge disabled is a deferred merge dialog in SPACECENTER whose
        /// explicit "Discard" button clears the pending tree without committing
        /// anything to the timeline. Use a disposable/manual-backup save before
        /// running it.
        /// </summary>
        [InGameTest(Category = "SceneExitMerge", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            BatchSkipReason = "Single-run only — excluded from Run All / Run category because this test starts a real recording, launches the active vessel, exits FLIGHT through stock save-and-exit semantics, and then drives the deferred merge dialog discard branch in SPACECENTER.",
            Description = "Space Center exit shows deferred merge dialog and Discard clears the pending tree without a commit")]
        public IEnumerator ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                InGameAssert.Skip("no active vessel");
                yield break;
            }
            if (vessel.isEVA || vessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                InGameAssert.Skip(
                    $"requires a PRELAUNCH vessel on the pad so the test can stage and then exit to Space Center, got {vessel.situation}");
                yield break;
            }
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires an idle prelaunch vessel (recording already active)");
                yield break;
            }
            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("requires no existing pending tree");
                yield break;
            }
            if (FlightInputHandler.state == null)
            {
                InGameAssert.Skip("FlightInputHandler.state is null");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }
            if (PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null
                || MultiOptionDialogTitleField == null
                || MultiOptionDialogOptionsField == null
                || DialogGuiButtonTextField == null
                || DialogGuiButtonOptionSelectedMethod == null)
            {
                InGameAssert.Skip("merge dialog reflection helpers are unavailable");
                yield break;
            }

            int committedBefore = RecordingStore.CommittedRecordings.Count;
            int committedTreesBefore = RecordingStore.CommittedTrees.Count;
            float originalThrottle = FlightInputHandler.state.mainThrottle;
            bool originalAutoMerge = ParsekSettings.Current.autoMerge;
            Vector3d launchWorldPosition = vessel.GetWorldPos3D();
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            string treeId = null;
            string activeRecId = null;

            try
            {
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };
                PopupDialog.DismissPopup("ParsekMerge");
                ParsekSettings.Current.autoMerge = false;

                flight.StartRecording();
                InGameAssert.IsTrue(flight.IsRecording,
                    "ParsekFlight.StartRecording should start a live recording before the Space Center discard flow");

                treeId = flight.ActiveTreeForSerialization?.Id;
                activeRecId = flight.ActiveTreeForSerialization?.ActiveRecordingId;
                InGameAssert.IsNotNull(treeId, "Active tree id should exist before the Space Center discard flow");
                InGameAssert.IsNotNull(activeRecId,
                    "ActiveRecordingId should be set before staging the Space Center discard flow");

                yield return new WaitForSeconds(0.5f);

                FlightInputHandler.state.mainThrottle = 1f;
                KSP.UI.Screens.StageManager.ActivateNextStage();

                yield return WaitForRecordingToLeavePrelaunch(activeRecId, 10f);
                yield return WaitForRecordingToClearPad(launchWorldPosition, 80.0, activeRecId, 10f);
                yield return new WaitForSeconds(0.5f);

                TriggerSaveAndExitToSpaceCenter();
                yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
                yield return WaitForPopupDialog("ParsekMerge", 10f);

                PopupDialog popup = FindPopupDialog("ParsekMerge");
                InGameAssert.IsNotNull(popup,
                    "ParsekMerge popup should exist after the real Space Center exit");

                MultiOptionDialog dialog = PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
                InGameAssert.IsNotNull(dialog, "Popup should expose a MultiOptionDialog");

                string dialogTitle = MultiOptionDialogTitleField.GetValue(dialog) as string;
                InGameAssert.AreEqual("Parsek - Merge to Timeline", dialogTitle,
                    "Deferred merge dialog title should match the production popup");

                DialogGUIButton[] buttons = GetDialogButtons(dialog);
                DialogGUIButton mergeButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Merge to Timeline");
                DialogGUIButton discardButton = buttons.FirstOrDefault(
                    button => GetDialogButtonText(button) == "Discard");

                InGameAssert.IsNotNull(mergeButton, "Merge to Timeline button should exist");
                InGameAssert.IsNotNull(discardButton, "Discard button should exist");
                InGameAssert.IsTrue(RecordingStore.HasPendingTree,
                    "Real Space Center exit should surface a pending tree before the discard click");
                InGameAssert.AreEqual(treeId, RecordingStore.PendingTree?.Id,
                    "Real Space Center exit should keep the same pending tree id into SPACECENTER");
                InGameAssert.IsTrue(ParsekScenario.MergeDialogPending,
                    "Deferred merge dialog should keep the pending-dialog flag armed until the click");

                DialogGuiButtonOptionSelectedMethod.Invoke(discardButton, null);

                yield return WaitForPopupDialogToClose("ParsekMerge", 3f);
                yield return null;

                InGameAssert.IsFalse(RecordingStore.HasPendingTree,
                    "Discard should clear the pending tree after the real scene exit");
                InGameAssert.IsFalse(ParsekScenario.MergeDialogPending,
                    "Discard should clear the deferred merge-dialog flag");
                InGameAssert.AreEqual(committedBefore, RecordingStore.CommittedRecordings.Count,
                    "Discard should not add committed recordings after the real scene exit");
                InGameAssert.AreEqual(committedTreesBefore, RecordingStore.CommittedTrees.Count,
                    "Discard should not add committed trees after the real scene exit");
                InGameAssert.IsFalse(RecordingStore.CommittedTrees.Any(
                        candidate => candidate != null && candidate.Id == treeId),
                    "Discard should not leave the pending tree inside CommittedTrees");

                bool sawDeferredDialog = captured.Any(
                    line => line.Contains("Showing deferred tree merge dialog in SPACECENTER"));
                bool sawUserDiscard = captured.Any(
                    line => line.Contains("User chose: Tree Discard"));
                InGameAssert.IsTrue(sawDeferredDialog,
                    "Expected the real scene-exit flow to log the deferred merge dialog in SPACECENTER");
                InGameAssert.IsTrue(sawUserDiscard,
                    "Expected the real scene-exit flow to log the Discard branch");

                ParsekLog.Info("TestRunner",
                    $"Scene-exit merge discard runtime: treeId={treeId} recId={activeRecId} " +
                    $"committedTreesBefore={committedTreesBefore} committedTreesAfter={RecordingStore.CommittedTrees.Count}");
            }
            finally
            {
                PopupDialog.DismissPopup("ParsekMerge");
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoMerge = originalAutoMerge;
                ParsekLog.TestObserverForTesting = priorObserver;
                if (RecordingStore.HasPendingTree && RecordingStore.PendingTree?.Id == treeId)
                    ParsekScenario.DiscardPendingTreeAndRecalculate("scene-exit merge discard canary cleanup");
                RemoveCommittedTreeByIdForRuntimeTest(treeId, recalculateAfterRemoval: true);
                if (ParsekFlight.Instance != null && ParsekFlight.Instance.IsRecording)
                    ParsekFlight.Instance.StopRecording();
                if (FlightInputHandler.state != null)
                    FlightInputHandler.state.mainThrottle = originalThrottle;
            }
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

        [InGameTest(Category = "PlaybackControl", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test commits a synthetic keep-vessel timeline recording, fast-forwards UT, and recovers the spawned vessel during cleanup. Use Run All + Isolated or the row play button.",
            Description = "Synthetic Keep Vessel timeline fast-forward starts playback and spawns exactly once")]
        public IEnumerator KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires idle flight — stop the active recording before running this test");
                yield break;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.Skip("requires an active vessel in FLIGHT");
                yield break;
            }
            if (activeVessel.isEVA || activeVessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (activeVessel.mainBody == null)
            {
                InGameAssert.Skip("active vessel has no main body");
                yield break;
            }
            if (activeVessel.situation != Vessel.Situations.PRELAUNCH && !activeVessel.LandedOrSplashed)
            {
                InGameAssert.Skip(
                    $"requires a landed/prelaunch vessel for a deterministic keep-vessel playback canary (situation={activeVessel.situation})");
                yield break;
            }

            if (!TryBuildSyntheticKeepVesselTree(activeVessel, out RecordingTree tree,
                out Recording recording, out string skipReason))
            {
                InGameAssert.Skip(skipReason ?? "failed to build synthetic keep-vessel recording");
                yield break;
            }

            Vessel spawnedVessel = null;
            uint spawnedPid = 0;

            try
            {
                RecordingStore.CommitTree(tree);

                int recordingIndex = FindCommittedRecordingIndex(recording.RecordingId);
                InGameAssert.IsTrue(recordingIndex >= 0,
                    "Synthetic keep-vessel recording should be present in CommittedRecordings");

                flight.FastForwardToRecording(recording);

                yield return WaitForActiveTimelineGhost(flight, recordingIndex, 4f);
                yield return WaitForRecordingSpawn(recording, 10f);

                spawnedPid = recording.SpawnedVesselPersistentId;
                InGameAssert.IsGreaterThan((double)spawnedPid, 0.0,
                    "SpawnedVesselPersistentId should be non-zero after keep-vessel playback");

                spawnedVessel = FlightRecorder.FindVesselByPid(spawnedPid);
                InGameAssert.IsNotNull(spawnedVessel,
                    "Spawned vessel should be findable by persistentId after playback");

                int pidMatchesBefore = CountLoadedVesselsByPid(spawnedPid);
                InGameAssert.AreEqual(1, pidMatchesBefore,
                    $"Expected exactly one loaded vessel with pid={spawnedPid} right after spawn, got {pidMatchesBefore}");

                yield return new WaitForSeconds(2f);

                InGameAssert.IsTrue(recording.VesselSpawned,
                    "Recording should stay marked spawned after the initial vessel materializes");
                InGameAssert.AreEqual((double)spawnedPid, (double)recording.SpawnedVesselPersistentId,
                    "Keep-vessel playback should not replace the spawned vessel with a second pid");

                int pidMatchesAfter = CountLoadedVesselsByPid(spawnedPid);
                InGameAssert.AreEqual(1, pidMatchesAfter,
                    $"Expected exactly one loaded vessel with pid={spawnedPid} after the duplicate-prevention wait, got {pidMatchesAfter}");

                ParsekLog.Info("TestRunner",
                    $"Keep-vessel runtime: rec='{recording.RecordingId}' ghostIndex={recordingIndex} spawnedPid={spawnedPid}");
            }
            finally
            {
                if (spawnedVessel == null && spawnedPid != 0)
                    spawnedVessel = FlightRecorder.FindVesselByPid(spawnedPid);
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
                            $"Keep-vessel runtime cleanup failed to recover pid={spawnedPid}: {ex.Message}");
                    }
                }

                RemoveCommittedTreeByIdForPlaybackRuntimeTest(tree.Id);
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test commits a synthetic timeline recording, fast-forwards UT on a real pad vessel, and verifies that FLIGHT does not start a bogus launch recording during the FF transient. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "#526: Timeline FF on a real pad vessel must not auto-start a bogus recording")]
        public IEnumerator TimelineFastForward_OnPad_DoesNotAutoStartLaunchRecording()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires idle flight — stop the active recording before running this test");
                yield break;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.Skip("requires an active vessel in FLIGHT");
                yield break;
            }
            if (activeVessel.isEVA || activeVessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (activeVessel.mainBody == null)
            {
                InGameAssert.Skip("active vessel has no main body");
                yield break;
            }
            if (activeVessel.situation != Vessel.Situations.PRELAUNCH && !activeVessel.LandedOrSplashed)
            {
                InGameAssert.Skip(
                    $"requires a landed/prelaunch vessel for the FF pad transient canary (situation={activeVessel.situation})");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalAutoRecord = ParsekSettings.Current.autoRecordOnLaunch;
            uint originalPid = activeVessel.persistentId;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            RecordingTree tree = null;
            Recording recording = null;

            try
            {
                ParsekSettings.Current.autoRecordOnLaunch = true;
                ParsekLog.TestObserverForTesting =
                    line => { captured.Add(line); priorObserver?.Invoke(line); };

                if (!TryBuildSyntheticKeepVesselTree(activeVessel, out tree,
                    out recording, out string skipReason))
                {
                    InGameAssert.Skip(skipReason ?? "failed to build synthetic keep-vessel recording");
                    yield break;
                }

                RecordingStore.CommitTree(tree);
                flight.FastForwardToRecording(recording);

                yield return RuntimeTests.WaitForTimeJumpLaunchAutoRecordTransientToClear(
                    RuntimeTests.TimeJumpLaunchAutoRecordTransientTimeoutSeconds);

                InGameAssert.IsFalse(flight.IsRecording,
                    "Timeline FF should not auto-start any new recording on the real pad vessel");

                Vessel currentActive = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(currentActive,
                    "Active vessel should still exist after the FF pad transient canary");
                InGameAssert.AreEqual((double)originalPid, (double)currentActive.persistentId,
                    "Timeline FF should keep the same real pad vessel pid focused after the jump transient");

                int skipCount = RuntimeTests.CountTimeJumpTransientSkipLogLines(captured);
                InGameAssert.IsGreaterThan(skipCount, 0,
                    "Timeline FF pad canary should exercise the time-jump transient suppression path");

                int autoStartCount = RuntimeTests.CountAnyAutoRecordStartLogLines(captured);
                InGameAssert.AreEqual(0, autoStartCount,
                    $"Expected zero auto-record start log lines during the time-jump transient, got {autoStartCount}");

                ParsekLog.Info("TestRunner",
                    $"FF pad no-auto-record: active='{currentActive.vesselName}' pid={currentActive.persistentId} " +
                    $"skipCount={skipCount} autoStartCount={autoStartCount}");
            }
            finally
            {
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnLaunch = originalAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();

                if (tree != null)
                    RemoveCommittedTreeByIdForPlaybackRuntimeTest(tree.Id);
            }
        }

        [InGameTest(Category = "AutoRecord", Scene = GameScenes.FLIGHT, RunLast = true,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only — excluded from ordinary Run All / Run category because this test commits a synthetic timeline recording, drives the Real Spawn Control epoch-shift warp on a real pad vessel, and verifies that FLIGHT does not start a bogus launch recording during the jump transient. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "#526: Real Spawn Control warp on a real pad vessel must not auto-start a bogus recording")]
        public IEnumerator RealSpawnControl_WarpToRecordingEnd_OnPad_DoesNotAutoStartLaunchRecording()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance required");
            if (flight.IsRecording)
            {
                InGameAssert.Skip("requires idle flight — stop the active recording before running this test");
                yield break;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
            {
                InGameAssert.Skip("requires an active vessel in FLIGHT");
                yield break;
            }
            if (activeVessel.isEVA || activeVessel.vesselType == VesselType.EVA)
            {
                InGameAssert.Skip("requires a non-EVA active vessel");
                yield break;
            }
            if (activeVessel.mainBody == null)
            {
                InGameAssert.Skip("active vessel has no main body");
                yield break;
            }
            if (activeVessel.situation != Vessel.Situations.PRELAUNCH && !activeVessel.LandedOrSplashed)
            {
                InGameAssert.Skip(
                    $"requires a landed/prelaunch vessel for the Real Spawn Control pad transient canary (situation={activeVessel.situation})");
                yield break;
            }
            if (ParsekSettings.Current == null)
            {
                InGameAssert.Skip("ParsekSettings.Current is null");
                yield break;
            }

            bool originalAutoRecord = ParsekSettings.Current.autoRecordOnLaunch;
            uint originalPid = activeVessel.persistentId;
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            RecordingTree tree = null;
            Recording recording = null;
            Vessel spawnedVessel = null;
            uint spawnedPid = 0;

            try
            {
                ParsekSettings.Current.autoRecordOnLaunch = true;
                ParsekLog.TestObserverForTesting =
                    line => { captured.Add(line); priorObserver?.Invoke(line); };

                if (!TryBuildSyntheticKeepVesselTree(activeVessel, out tree,
                    out recording, out string skipReason))
                {
                    InGameAssert.Skip(skipReason ?? "failed to build synthetic keep-vessel recording");
                    yield break;
                }

                RecordingStore.CommitTree(tree);

                int recordingIndex = FindCommittedRecordingIndex(recording.RecordingId);
                InGameAssert.IsTrue(recordingIndex >= 0,
                    "Synthetic keep-vessel recording should be present in CommittedRecordings");

                flight.WarpToRecordingEnd(recordingIndex);

                yield return RuntimeTests.WaitForTimeJumpLaunchAutoRecordTransientToClear(
                    RuntimeTests.TimeJumpLaunchAutoRecordTransientTimeoutSeconds);

                InGameAssert.IsFalse(flight.IsRecording,
                    "Real Spawn Control warp should not auto-start any new recording on the real pad vessel");

                Vessel currentActive = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(currentActive,
                    "Active vessel should still exist after the Real Spawn Control pad transient canary");
                InGameAssert.AreEqual((double)originalPid, (double)currentActive.persistentId,
                    "Real Spawn Control warp should keep the same real pad vessel pid focused after the jump transient");

                int suppressionArmCount = captured.Count(
                    line => line.Contains("[TimeJump]")
                        && line.Contains("Time-jump launch auto-record suppression armed: jump=epoch-shift"));
                InGameAssert.IsGreaterThan(suppressionArmCount, 0,
                    "Real Spawn Control pad canary should arm the epoch-shift time-jump suppression path");

                int skipCount = RuntimeTests.CountTimeJumpTransientSkipLogLines(captured);
                InGameAssert.IsGreaterThan(skipCount, 0,
                    "Real Spawn Control pad canary should exercise the time-jump transient suppression path");

                yield return WaitForRecordingSpawn(recording, 10f);

                spawnedPid = recording.SpawnedVesselPersistentId;
                InGameAssert.IsGreaterThan((double)spawnedPid, 0.0,
                    "Real Spawn Control warp should leave the synthetic recording with a spawned pid");

                spawnedVessel = FlightRecorder.FindVesselByPid(spawnedPid);
                InGameAssert.IsNotNull(spawnedVessel,
                    "Real Spawn Control warp should still materialize the synthetic vessel by recording end");

                int autoStartCount = RuntimeTests.CountAnyAutoRecordStartLogLines(captured);
                InGameAssert.AreEqual(0, autoStartCount,
                    $"Expected zero auto-record start log lines during the Real Spawn Control transient, got {autoStartCount}");

                ParsekLog.Info("TestRunner",
                    $"RSC warp no-auto-record: active='{currentActive.vesselName}' pid={currentActive.persistentId} " +
                    $"recordingIndex={recordingIndex} spawnedPid={spawnedPid} suppressionArmCount={suppressionArmCount} " +
                    $"skipCount={skipCount} autoStartCount={autoStartCount}");
            }
            finally
            {
                if (ParsekSettings.Current != null)
                    ParsekSettings.Current.autoRecordOnLaunch = originalAutoRecord;
                ParsekLog.TestObserverForTesting = priorObserver;

                var cleanupFlight = ParsekFlight.Instance;
                if (cleanupFlight != null && cleanupFlight.IsRecording)
                    cleanupFlight.StopRecording();

                if (spawnedVessel == null && spawnedPid != 0)
                    spawnedVessel = FlightRecorder.FindVesselByPid(spawnedPid);
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
                            $"RSC warp cleanup failed to recover pid={spawnedPid}: {ex.Message}");
                    }
                }

                if (tree != null)
                    RemoveCommittedTreeByIdForPlaybackRuntimeTest(tree.Id);
            }
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

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#474: Ghost audio re-anchors to watch pivot and keeps centered stereo defaults")]
        public void GhostAudioSources_AnchorToWatchPivot()
        {
            var ghostRoot = new GameObject("ParsekTest_GhostAudioPivot");
            runner.TrackForCleanup(ghostRoot);

            var cameraPivot = new GameObject("cameraPivot");
            cameraPivot.transform.SetParent(ghostRoot.transform, false);
            cameraPivot.transform.localPosition = new Vector3(4f, 2f, -3f);

            var partRoot = new GameObject("ghost_part_100");
            partRoot.transform.SetParent(ghostRoot.transform, false);
            partRoot.transform.localPosition = new Vector3(-8f, 1f, 6f);

            var engineAudioRoot = new GameObject("ghost_audio_loop");
            engineAudioRoot.transform.SetParent(partRoot.transform, false);
            var engineSource = engineAudioRoot.AddComponent<AudioSource>();
            engineSource.spatialBlend = 1f;
            engineSource.panStereo = 0.35f;

            var oneShotAudioRoot = new GameObject("ghost_audio_oneshot");
            oneShotAudioRoot.transform.SetParent(ghostRoot.transform, false);
            var oneShotSource = oneShotAudioRoot.AddComponent<AudioSource>();
            oneShotSource.spatialBlend = 1f;
            oneShotSource.panStereo = -0.4f;

            var result = new GhostBuildResult
            {
                audioInfos = new List<AudioGhostInfo>
                {
                    new AudioGhostInfo
                    {
                        partPersistentId = 100u,
                        moduleIndex = 0,
                        audioSource = engineSource
                    }
                },
                oneShotAudio = new OneShotAudioInfo
                {
                    audioSource = oneShotSource
                }
            };

            GhostVisualBuilder.AttachGhostAudioToWatchPivot(result, cameraPivot.transform);

            InGameAssert.IsTrue(ghostRoot.transform.parent == null,
                "Ghost root should not be reparented by audio centering");
            InGameAssert.IsTrue(partRoot.transform.parent == ghostRoot.transform,
                "Part visuals should stay under the ghost root when audio is re-anchored");
            InGameAssert.IsTrue(engineSource.transform.parent == cameraPivot.transform,
                "Engine ghost audio should be parented to cameraPivot");
            InGameAssert.IsTrue(oneShotSource.transform.parent == cameraPivot.transform,
                "One-shot ghost audio should be parented to cameraPivot");
            InGameAssert.IsTrue(engineSource.transform.localPosition == Vector3.zero,
                "Engine ghost audio should sit at the watch pivot local origin");
            InGameAssert.IsTrue(oneShotSource.transform.localPosition == Vector3.zero,
                "One-shot ghost audio should sit at the watch pivot local origin");
            InGameAssert.ApproxEqual(0f, engineSource.panStereo, 0.0001f,
                "Engine ghost audio panStereo should stay centered");
            InGameAssert.ApproxEqual(0f, oneShotSource.panStereo, 0.0001f,
                "One-shot ghost audio panStereo should stay centered");
            InGameAssert.ApproxEqual(GhostVisualBuilder.GhostAudioSpatialBlend, engineSource.spatialBlend, 0.0001f,
                "Engine ghost audio spatialBlend should use the damped watch-safe blend");
            InGameAssert.ApproxEqual(GhostVisualBuilder.GhostAudioSpatialBlend, oneShotSource.spatialBlend, 0.0001f,
                "One-shot ghost audio spatialBlend should use the damped watch-safe blend");
        }

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#474: Part visibility toggles keep re-anchored ghost audio in sync")]
        public IEnumerator GhostAudioSources_FollowPartVisibilityAfterReanchor()
        {
            var ghostRoot = new GameObject("ParsekTest_GhostAudioVisibility");
            runner.TrackForCleanup(ghostRoot);

            Transform visualsRoot = GhostVisualBuilder.EnsureGhostVisualsRoot(ghostRoot.transform);

            var cameraPivot = new GameObject("cameraPivot");
            cameraPivot.transform.SetParent(ghostRoot.transform, false);

            var partRoot = new GameObject("ghost_part_100");
            partRoot.transform.SetParent(visualsRoot, false);

            var engineAudioRoot = new GameObject("ghost_audio_loop");
            engineAudioRoot.transform.SetParent(partRoot.transform, false);
            var engineSource = engineAudioRoot.AddComponent<AudioSource>();
            engineSource.clip = AudioClip.Create("test_loop", 44100, 1, 44100, false);

            var result = new GhostBuildResult
            {
                audioInfos = new List<AudioGhostInfo>
                {
                    new AudioGhostInfo
                    {
                        partPersistentId = 100u,
                        moduleIndex = 0,
                        audioSource = engineSource,
                        clip = engineSource.clip,
                        volumeCurve = GhostAudioPresets.BuildDefaultVolumeCurve(),
                        pitchCurve = GhostAudioPresets.BuildDefaultPitchCurve(),
                        currentPower = 1f
                    }
                }
            };

            var state = new GhostPlaybackState
            {
                ghost = ghostRoot,
                cameraPivot = cameraPivot.transform,
                atmosphereFactor = 1f,
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(100u, 0)] = result.audioInfos[0]
                }
            };

            GhostVisualBuilder.AttachGhostAudioToWatchPivot(result, cameraPivot.transform);
            GhostPlaybackLogic.SetEngineAudio(state, new PartEvent
            {
                partPersistentId = 100u,
                moduleIndex = 0
            }, 1f);
            yield return null;

            InGameAssert.IsTrue(engineSource.isPlaying,
                "Engine audio should be playing before the part is hidden");

            GhostPlaybackLogic.SetGhostPartActive(state, 100u, false);
            yield return null;
            InGameAssert.IsFalse(partRoot.activeSelf,
                "Part visual should disable when part visibility turns off");
            InGameAssert.IsFalse(engineSource.gameObject.activeSelf,
                "Re-anchored engine audio host should disable with the part");
            InGameAssert.IsFalse(engineSource.isPlaying,
                "Re-anchored engine audio should stop when the part hides");

            GhostPlaybackLogic.SetGhostPartActive(state, 100u, true);
            yield return null;
            InGameAssert.IsTrue(partRoot.activeSelf,
                "Part visual should re-enable when part visibility turns on");
            InGameAssert.IsTrue(engineSource.gameObject.activeSelf,
                "Re-anchored engine audio host should re-enable with the part");
            InGameAssert.IsTrue(engineSource.isPlaying,
                "Re-anchored engine audio should resume when the part becomes visible again");
        }

        [InGameTest(Category = "GhostAudio", Scene = GameScenes.FLIGHT,
            Description = "#474: Fresh ghost cameraPivot recenters on the active-part midpoint before watch/audio uses it")]
        public void CameraPivot_RecalculatesToActivePartMidpoint()
        {
            var ghostRoot = new GameObject("ParsekTest_GhostPivot");
            runner.TrackForCleanup(ghostRoot);

            Transform visualsRoot = GhostVisualBuilder.EnsureGhostVisualsRoot(ghostRoot.transform);

            var left = new GameObject("ghost_part_100");
            left.transform.SetParent(visualsRoot, false);
            left.transform.localPosition = new Vector3(-6f, 1f, 0f);

            var right = new GameObject("ghost_part_200");
            right.transform.SetParent(visualsRoot, false);
            right.transform.localPosition = new Vector3(2f, 5f, 4f);

            var hidden = new GameObject("ghost_part_300");
            hidden.transform.SetParent(visualsRoot, false);
            hidden.transform.localPosition = new Vector3(50f, 50f, 50f);
            hidden.SetActive(false);

            var ignored = new GameObject("not_a_ghost_part");
            ignored.transform.SetParent(visualsRoot, false);
            ignored.transform.localPosition = new Vector3(99f, 99f, 99f);

            var cameraPivot = new GameObject("cameraPivot");
            cameraPivot.transform.SetParent(ghostRoot.transform, false);

            var state = new GhostPlaybackState
            {
                ghost = ghostRoot,
                cameraPivot = cameraPivot.transform
            };

            GhostPlaybackLogic.RecalculateCameraPivot(state);

            Vector3 expectedMidpoint = new Vector3(-2f, 3f, 2f);
            InGameAssert.IsTrue((state.cameraPivot.localPosition - expectedMidpoint).sqrMagnitude < 0.0001f,
                $"cameraPivot should recenter to {expectedMidpoint}, got {state.cameraPivot.localPosition}");
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
                $"sma={rec.TerminalOrbitSemiMajorAxis.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#265/#484: endpoint-aligned backfill must not overwrite explicit recorded terminal orbit data that already matches the recording endpoint")]
        public void TerminalOrbitBackfill_AlreadyPopulated_NoOverwrite()
        {
            var rec = new Recording
            {
                VesselName = "TestNoOverwrite",
                VesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                RecordingId = "runtime-preserve-explicit-terminal-orbit",
                EndpointPhase = RecordingEndpointPhase.TrajectoryPoint,
                EndpointBodyName = "Mun",
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
            // Keep the last point off the conflicting segment start so this runtime
            // test exercises the explicit-endpoint preserve path, not the separate
            // same-UT point-anchor guard covered below.
            rec.Points.Add(new TrajectoryPoint { ut = 999, bodyName = "Mun" });

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Mun", rec.TerminalOrbitBody,
                "Explicit endpoint metadata should keep the cached TerminalOrbitBody authoritative");
            InGameAssert.ApproxEqual(250000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string preserveLine = captured.LastOrDefault(line =>
                line.Contains("ShouldPopulateTerminalOrbitFromLastSegment")
                && line.Contains("preserved cached terminal orbit")
                && line.Contains("runtime-preserve-explicit-terminal-orbit")
                && line.Contains("explicit endpoint body=Mun")
                && line.Contains("later segment body=Kerbin")
                && line.Contains("sma=700000.0"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(preserveLine),
                "Expected explicit-endpoint preserve INFO log");
            InGameAssert.IsTrue(preserveLine.Contains("[INFO][Flight]"),
                $"Expected preserve log to be INFO/[Flight], got: {preserveLine ?? "(null)"}");
            InGameAssert.IsFalse(captured.Any(line =>
                    line.Contains("preserved same-UT point-anchored terminal orbit")
                    && line.Contains("runtime-preserve-explicit-terminal-orbit")),
                "Explicit-endpoint preserve coverage should not be claimed by the separate same-UT point-anchor guard");
            InGameAssert.IsFalse(captured.Any(line => line.Contains("healed stale cached terminal orbit")),
                "Explicit recorded terminal-orbit data should preserve, not heal");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody explicit preserve verified: body still Mun");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#265/#484: endpoint-aligned backfill preserves an already-populated terminal orbit only when the cached tuple already matches")]
        public void TerminalOrbitBackfill_AlreadyPopulatedMatchingTuple_NoOverwrite()
        {
            var rec = new Recording
            {
                VesselName = "TestNoOverwriteMatchingTuple",
                VesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000
            };

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000,
                endUT = 2000,
                bodyName = "Kerbin",
                semiMajorAxis = 700000
            });
            rec.Points.Add(new TrajectoryPoint { ut = 1000, bodyName = "Mun" });

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody,
                "Existing TerminalOrbitBody should not be overwritten when the full cached tuple already matches");
            InGameAssert.ApproxEqual(700000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string preserveLine = captured.LastOrDefault(line =>
                line.Contains("ShouldPopulateTerminalOrbitFromLastSegment")
                && line.Contains("preserved cached terminal orbit")
                && line.Contains("body=Kerbin")
                && line.Contains("sma=700000.0"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(preserveLine),
                "Expected preserve INFO log when cached tuple already matches endpoint-aligned segment");
            InGameAssert.IsTrue(preserveLine.Contains("[INFO][Flight]"),
                $"Expected preserve log to be INFO/[Flight], got: {preserveLine ?? "(null)"}");
            InGameAssert.IsFalse(captured.Any(line => line.Contains("healed stale cached terminal orbit")),
                "Matching cached tuple should preserve, not heal");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody matching tuple preserve verified: body still Kerbin");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#484 review follow-up: endpoint-aligned backfill heals a stale same-body terminal-orbit tuple instead of preserving by body match alone")]
        public void TerminalOrbitBackfill_StaleCachedSameBodyTuple_EndpointAlignedSegment_Overwrites()
        {
            var rec = new Recording
            {
                VesselName = "TestHealStaleSameBodyTuple",
                VesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
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

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody,
                "Endpoint-aligned orbit segment should keep the same body while healing stale tuple fields");
            InGameAssert.ApproxEqual(700000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string healLine = captured.LastOrDefault(line =>
                line.Contains("PopulateTerminalOrbitFromLastSegment")
                && line.Contains("healed stale cached terminal orbit")
                && line.Contains("previousBody=Kerbin")
                && line.Contains("previousSma=250000.0")
                && line.Contains("newBody=Kerbin")
                && line.Contains("newSma=700000.0"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(healLine),
                "Expected stale same-body tuple heal WARN log");
            InGameAssert.IsTrue(healLine.Contains("[WARN][Flight]"),
                $"Expected heal log to be WARN/[Flight], got: {healLine ?? "(null)"}");
            InGameAssert.IsFalse(captured.Any(line => line.Contains("preserved cached terminal orbit")),
                "Stale cached tuple should heal, not preserve");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody stale same-body tuple heal verified: sma 250000 -> 700000");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#520: same-UT point anchors keep a correct cached terminal orbit body authoritative over a conflicting later orbit segment")]
        public void TerminalOrbitBackfill_SameUtPointAnchor_PreservesCachedBody()
        {
            var rec = new Recording
            {
                VesselName = "TestPreserveSameUtPointAnchor",
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

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Mun", rec.TerminalOrbitBody,
                "Same-UT point anchor should keep the cached TerminalOrbitBody authoritative");
            InGameAssert.ApproxEqual(250000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string preserveLine = captured.LastOrDefault(line =>
                line.Contains("FinalizeIndividualRecording: preserved same-UT point-anchored terminal orbit")
                && line.Contains("pointBody=Mun")
                && line.Contains("conflictingSegmentBody=Kerbin")
                && line.Contains("conflictingSegmentStartUT=1000.000")
                && line.Contains("pointUT=1000.000"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(preserveLine),
                "Expected same-UT point-anchor preserve INFO log");
            InGameAssert.IsTrue(preserveLine.Contains("[INFO][Flight]"),
                $"Expected preserve log to be INFO/[Flight], got: {preserveLine ?? "(null)"}");
            InGameAssert.IsFalse(captured.Any(line =>
                    line.Contains("PopulateTerminalOrbitFromLastSegment")
                    && line.Contains("healed stale cached terminal orbit")),
                "Same-UT point-anchor preserve path should not heal from the conflicting later segment");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody same-UT point-anchor preserve verified: body remains Mun");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#484: orbit-only stale same-body terminal-orbit cache still heals from the endpoint-aligned last segment")]
        public void TerminalOrbitBackfill_OrbitOnlyStaleCachedSameBodyTuple_EndpointAlignedSegment_Overwrites()
        {
            var rec = new Recording
            {
                VesselName = "TestHealStaleSameBodyTuple",
                VesselPersistentId = 0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 250000
            };

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000,
                endUT = 2000,
                bodyName = "Kerbin",
                semiMajorAxis = 700000
            });

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody,
                "Endpoint-aligned orbit segment should keep the same body while healing stale tuple fields");
            InGameAssert.ApproxEqual(700000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string healLine = captured.LastOrDefault(line =>
                line.Contains("PopulateTerminalOrbitFromLastSegment")
                && line.Contains("healed stale cached terminal orbit")
                && line.Contains("previousBody=Kerbin")
                && line.Contains("previousSma=250000.0")
                && line.Contains("newBody=Kerbin")
                && line.Contains("newSma=700000.0"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(healLine),
                "Expected stale same-body tuple heal WARN log");
            InGameAssert.IsTrue(healLine.Contains("[WARN][Flight]"),
                $"Expected heal log to be WARN/[Flight], got: {healLine ?? "(null)"}");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody stale same-body tuple heal verified: sma 250000 -> 700000");
        }

        [InGameTest(Category = "FinalizeBackfill", Scene = GameScenes.FLIGHT,
            Description = "#475/#484: orbit-only stale cached terminal orbit body heals when the endpoint-aligned segment disagrees")]
        public void TerminalOrbitBackfill_OrbitOnlyStaleCachedBody_EndpointAlignedSegment_Overwrites()
        {
            var rec = new Recording
            {
                VesselName = "TestHealStaleBody",
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

            var captured = new List<string>();
            var priorSink = ParsekLog.TestSinkForTesting;
            try
            {
                ParsekLog.TestSinkForTesting = line =>
                {
                    captured.Add(line);
                    priorSink?.Invoke(line);
                };

                ParsekFlight.FinalizeIndividualRecording(rec, 2000, isSceneExit: false);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
            }

            InGameAssert.AreEqual("Kerbin", rec.TerminalOrbitBody,
                "Endpoint-aligned orbit segment should heal a stale cached TerminalOrbitBody");
            InGameAssert.ApproxEqual(700000.0, rec.TerminalOrbitSemiMajorAxis, 1.0);
            string healLine = captured.LastOrDefault(line =>
                line.Contains("PopulateTerminalOrbitFromLastSegment")
                && line.Contains("healed stale cached terminal orbit")
                && line.Contains("previousBody=Mun")
                && line.Contains("previousSma=250000.0")
                && line.Contains("newBody=Kerbin")
                && line.Contains("newSma=700000.0"));
            InGameAssert.IsTrue(!string.IsNullOrEmpty(healLine),
                "Expected stale body heal WARN log");
            InGameAssert.IsTrue(healLine.Contains("[WARN][Flight]"),
                $"Expected heal log to be WARN/[Flight], got: {healLine ?? "(null)"}");

            ParsekLog.Verbose("TestRunner",
                "TerminalOrbitBody stale-cache heal verified: body Mun -> Kerbin");
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

        [InGameTest(Category = "PartEventTiming", Scene = GameScenes.FLIGHT,
            Description = "Light part events flip ghost lights exactly at their authored UT boundaries")]
        public void PartEventTiming_LightToggle_AppliesAtEventUt()
        {
            var ghostRoot = new GameObject("ParsekTest_LightTimingGhost");
            runner.TrackForCleanup(ghostRoot);

            var lightHost = new GameObject("ghost_part_101");
            lightHost.transform.SetParent(ghostRoot.transform, false);
            var light = lightHost.AddComponent<Light>();
            light.enabled = false;

            var rec = new Recording
            {
                VesselName = "LightTimingCanary",
                PartEvents = new List<PartEvent>
                {
                    new PartEvent
                    {
                        ut = 100.0,
                        partPersistentId = 101u,
                        eventType = PartEventType.LightOn
                    },
                    new PartEvent
                    {
                        ut = 110.0,
                        partPersistentId = 101u,
                        eventType = PartEventType.LightOff
                    }
                }
            };

            var state = new GhostPlaybackState
            {
                ghost = ghostRoot,
                lightInfos = new Dictionary<uint, LightGhostInfo>
                {
                    [101u] = new LightGhostInfo
                    {
                        partPersistentId = 101u,
                        lights = new List<Light> { light }
                    }
                }
            };

            GhostPlaybackLogic.ApplyPartEvents(901, rec, 99.9, state);
            InGameAssert.IsFalse(light.enabled,
                "Light should stay off before the authored LightOn UT");
            InGameAssert.AreEqual(0, state.partEventIndex,
                "Part-event cursor should not advance before the first light event fires");

            GhostPlaybackLogic.ApplyPartEvents(901, rec, 100.0, state);
            InGameAssert.IsTrue(light.enabled,
                "Light should turn on exactly at the authored LightOn UT");
            InGameAssert.AreEqual(1, state.partEventIndex,
                "Part-event cursor should advance after the LightOn event fires");

            GhostPlaybackLogic.ApplyPartEvents(901, rec, 109.9, state);
            InGameAssert.IsTrue(light.enabled,
                "Light should remain on between the authored LightOn and LightOff events");
            InGameAssert.AreEqual(1, state.partEventIndex,
                "Part-event cursor should not advance before the authored LightOff UT");

            GhostPlaybackLogic.ApplyPartEvents(901, rec, 110.0, state);
            InGameAssert.IsFalse(light.enabled,
                "Light should turn off exactly at the authored LightOff UT");
            InGameAssert.AreEqual(2, state.partEventIndex,
                "Part-event cursor should advance after the LightOff event fires");
        }

        [InGameTest(Category = "PartEventTiming", Scene = GameScenes.FLIGHT,
            Description = "Deployable part events swap ghost transforms exactly at their authored UT boundaries")]
        public void PartEventTiming_DeployableTransition_AppliesAtEventUt()
        {
            var ghostRoot = new GameObject("ParsekTest_DeployableTimingGhost");
            runner.TrackForCleanup(ghostRoot);

            var deployableHost = new GameObject("ghost_part_202");
            deployableHost.transform.SetParent(ghostRoot.transform, false);

            DeployableTransformState deployableState = new DeployableTransformState
            {
                t = deployableHost.transform,
                stowedPos = new Vector3(-1f, 0f, 0f),
                stowedRot = Quaternion.Euler(0f, 0f, 0f),
                stowedScale = new Vector3(1f, 1f, 1f),
                deployedPos = new Vector3(2f, 3f, 4f),
                deployedRot = Quaternion.Euler(0f, 90f, 0f),
                deployedScale = new Vector3(1f, 2f, 1f)
            };

            deployableHost.transform.localPosition = deployableState.stowedPos;
            deployableHost.transform.localRotation = deployableState.stowedRot;
            deployableHost.transform.localScale = deployableState.stowedScale;

            var rec = new Recording
            {
                VesselName = "DeployableTimingCanary",
                PartEvents = new List<PartEvent>
                {
                    new PartEvent
                    {
                        ut = 200.0,
                        partPersistentId = 202u,
                        eventType = PartEventType.DeployableExtended
                    },
                    new PartEvent
                    {
                        ut = 210.0,
                        partPersistentId = 202u,
                        eventType = PartEventType.DeployableRetracted
                    }
                }
            };

            var state = new GhostPlaybackState
            {
                ghost = ghostRoot,
                deployableInfos = new Dictionary<uint, DeployableGhostInfo>
                {
                    [202u] = new DeployableGhostInfo
                    {
                        partPersistentId = 202u,
                        transforms = new List<DeployableTransformState> { deployableState }
                    }
                }
            };

            GhostPlaybackLogic.ApplyPartEvents(902, rec, 199.9, state);
            InGameAssert.IsTrue(deployableHost.transform.localPosition == deployableState.stowedPos,
                "Deployable should stay stowed before the authored extend UT");
            InGameAssert.IsTrue(deployableHost.transform.localRotation == deployableState.stowedRot,
                "Deployable rotation should stay stowed before the authored extend UT");
            InGameAssert.AreEqual(0, state.partEventIndex,
                "Part-event cursor should not advance before the first deployable event fires");

            GhostPlaybackLogic.ApplyPartEvents(902, rec, 200.0, state);
            InGameAssert.IsTrue(deployableHost.transform.localPosition == deployableState.deployedPos,
                "Deployable should extend exactly at the authored DeployableExtended UT");
            InGameAssert.IsTrue(deployableHost.transform.localRotation == deployableState.deployedRot,
                "Deployable rotation should switch to the deployed pose at the authored extend UT");
            InGameAssert.IsTrue(deployableHost.transform.localScale == deployableState.deployedScale,
                "Deployable scale should switch to the deployed pose at the authored extend UT");
            InGameAssert.AreEqual(1, state.partEventIndex,
                "Part-event cursor should advance after the deployable extend event fires");

            GhostPlaybackLogic.ApplyPartEvents(902, rec, 209.9, state);
            InGameAssert.IsTrue(deployableHost.transform.localPosition == deployableState.deployedPos,
                "Deployable should remain extended between the authored extend and retract events");
            InGameAssert.AreEqual(1, state.partEventIndex,
                "Part-event cursor should not advance before the authored retract UT");

            GhostPlaybackLogic.ApplyPartEvents(902, rec, 210.0, state);
            InGameAssert.IsTrue(deployableHost.transform.localPosition == deployableState.stowedPos,
                "Deployable should retract exactly at the authored DeployableRetracted UT");
            InGameAssert.IsTrue(deployableHost.transform.localRotation == deployableState.stowedRot,
                "Deployable rotation should switch back to the stowed pose at the authored retract UT");
            InGameAssert.IsTrue(deployableHost.transform.localScale == deployableState.stowedScale,
                "Deployable scale should switch back to the stowed pose at the authored retract UT");
            InGameAssert.AreEqual(2, state.partEventIndex,
                "Part-event cursor should advance after the deployable retract event fires");
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
            var prevObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => { captured.Add(line); prevObserver?.Invoke(line); };

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
                ParsekLog.TestObserverForTesting = prevObserver;
            }
        }

        #endregion

        #region PlaybackControl helpers

        internal static bool TryBuildSyntheticKeepVesselTree(
            Vessel activeVessel,
            out RecordingTree tree,
            out Recording recording,
            out string skipReason)
        {
            tree = null;
            recording = null;
            skipReason = null;

            if (activeVessel == null)
            {
                skipReason = "active vessel is null";
                return false;
            }

            CelestialBody body = activeVessel.mainBody;
            if (body == null || body.Radius <= 0.0)
            {
                skipReason = "active vessel body is unavailable";
                return false;
            }

            ConfigNode sourceSnapshot = VesselSpawner.TryBackupSnapshot(activeVessel);
            if (sourceSnapshot == null)
            {
                skipReason = "failed to snapshot active vessel";
                return false;
            }

            ConfigNode vesselSnapshot = sourceSnapshot.CreateCopy();
            ConfigNode ghostSnapshot = sourceSnapshot.CreateCopy();
            double now = Planetarium.GetUniversalTime();
            double stepMeters = 10.0;
            if (!TryResolveSyntheticKeepVesselPath(body, activeVessel.latitude, activeVessel.longitude,
                stepMeters, out double startOffsetMeters, out double startLat, out double middleLat,
                out double endLat, out double lon, out skipReason))
            {
                return false;
            }

            Quaternion landedRotation = activeVessel.transform != null
                ? activeVessel.transform.rotation
                : Quaternion.identity;

            double surfaceClearance = ResolveSyntheticKeepVesselClearance(
                body, activeVessel, vesselSnapshot);
            double startAlt = ResolvePlaybackSurfaceAltitude(body, startLat, lon, surfaceClearance);
            double middleAlt = ResolvePlaybackSurfaceAltitude(body, middleLat, lon, surfaceClearance);
            double endAlt = ResolvePlaybackSurfaceAltitude(body, endLat, lon, surfaceClearance);
            double terminalTerrainAlt = body.TerrainAltitude(endLat, lon);
            if (double.IsNaN(terminalTerrainAlt) || double.IsInfinity(terminalTerrainAlt))
                terminalTerrainAlt = endAlt - surfaceClearance;

            VesselSpawner.OverrideSnapshotPosition(vesselSnapshot, endLat, lon, endAlt,
                -1, activeVessel.vesselName ?? "Runtime Test Vessel", body, landedRotation);
            vesselSnapshot.SetValue("sit", "LANDED", true);
            vesselSnapshot.SetValue("landed", "True", true);
            vesselSnapshot.SetValue("splashed", "False", true);

            double endDistanceMeters = SpawnCollisionDetector.SurfaceDistance(
                activeVessel.latitude, activeVessel.longitude, endLat, lon, body.Radius);

            string recordingId = "runtime-keep-vessel-" + System.DateTime.UtcNow.Ticks;
            string treeId = "runtime-tree-keep-vessel-" + recordingId;
            uint syntheticPid = activeVessel.persistentId ^ 0x5A5AA5A5u;
            if (syntheticPid == 0 || syntheticPid == activeVessel.persistentId)
                syntheticPid = 950001u;

            recording = new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = (activeVessel.vesselName ?? "Runtime Test Vessel") + " timeline",
                VesselPersistentId = syntheticPid,
                TerminalStateValue = TerminalState.Landed,
                DistanceFromLaunch = endDistanceMeters,
                MaxDistanceFromLaunch = endDistanceMeters,
                TerrainHeightAtEnd = terminalTerrainAlt,
                VesselSnapshot = vesselSnapshot,
                GhostVisualSnapshot = ghostSnapshot,
                TerminalPosition = new SurfacePosition
                {
                    body = body.name,
                    latitude = endLat,
                    longitude = lon,
                    altitude = endAlt,
                    rotation = landedRotation,
                    situation = SurfaceSituation.Landed
                },
                SurfacePos = new SurfacePosition
                {
                    body = body.name,
                    latitude = endLat,
                    longitude = lon,
                    altitude = endAlt,
                    rotation = landedRotation,
                    situation = SurfaceSituation.Landed
                },
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = now + 30.0,
                        latitude = startLat,
                        longitude = lon,
                        altitude = startAlt,
                        bodyName = body.name,
                        rotation = landedRotation,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = now + 32.0,
                        latitude = middleLat,
                        longitude = lon,
                        altitude = middleAlt,
                        bodyName = body.name,
                        rotation = landedRotation,
                        velocity = Vector3.zero
                    },
                    new TrajectoryPoint
                    {
                        ut = now + 34.0,
                        latitude = endLat,
                        longitude = lon,
                        altitude = endAlt,
                        bodyName = body.name,
                        rotation = landedRotation,
                        velocity = Vector3.zero
                    }
                }
            };

            tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Runtime Keep Vessel Playback",
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId
            };
            tree.Recordings[recordingId] = recording;
            return true;
        }

        private static double ResolvePlaybackSurfaceAltitude(
            CelestialBody body,
            double latitude,
            double longitude,
            double surfaceClearance)
        {
            double terrainAlt = body.TerrainAltitude(latitude, longitude);
            if (double.IsNaN(terrainAlt) || double.IsInfinity(terrainAlt))
                terrainAlt = 0.0;
            return terrainAlt + surfaceClearance;
        }

        private static double ResolveSyntheticKeepVesselClearance(
            CelestialBody body,
            Vessel activeVessel,
            ConfigNode vesselSnapshot)
        {
            const double minimumClearanceMeters = 6.0;

            if (body == null || activeVessel == null)
                return minimumClearanceMeters;

            double terrainAlt = body.TerrainAltitude(activeVessel.latitude, activeVessel.longitude);
            if (double.IsNaN(terrainAlt) || double.IsInfinity(terrainAlt))
                return minimumClearanceMeters;

            double clearance = minimumClearanceMeters;
            if (VesselSpawner.TryGetSnapshotDouble(vesselSnapshot, "alt", out double snapshotAlt))
                clearance = System.Math.Max(clearance, snapshotAlt - terrainAlt);

            if (!double.IsNaN(activeVessel.altitude) && !double.IsInfinity(activeVessel.altitude))
                clearance = System.Math.Max(clearance, activeVessel.altitude - terrainAlt);

            return clearance;
        }

        private static bool TryResolveSyntheticKeepVesselPath(
            CelestialBody body,
            double originLatitude,
            double originLongitude,
            double stepMeters,
            out double startOffsetMeters,
            out double startLatitude,
            out double middleLatitude,
            out double endLatitude,
            out double longitude,
            out string skipReason)
        {
            startOffsetMeters = 0.0;
            startLatitude = 0.0;
            middleLatitude = 0.0;
            endLatitude = 0.0;
            longitude = 0.0;
            skipReason = null;

            if (body == null || body.Radius <= 0.0)
            {
                skipReason = "active vessel body is unavailable";
                return false;
            }

            double latitudeDegreesPerMeter = 180.0 / (System.Math.PI * body.Radius);
            double cosLatitude = System.Math.Cos(originLatitude * System.Math.PI / 180.0);
            double longitudeDegreesPerMeter = System.Math.Abs(cosLatitude) > 1e-6
                ? latitudeDegreesPerMeter / cosLatitude
                : latitudeDegreesPerMeter;

            double[] northCandidates = body.isHomeWorld
                ? new[] { 220.0, 320.0, 420.0, 520.0 }
                : new[] { 120.0 };
            double[] eastCandidates = body.isHomeWorld
                ? new[] { 0.0, 160.0, -160.0, 320.0, -320.0 }
                : new[] { 0.0 };

            for (int northIndex = 0; northIndex < northCandidates.Length; northIndex++)
            {
                double northMeters = northCandidates[northIndex];
                for (int eastIndex = 0; eastIndex < eastCandidates.Length; eastIndex++)
                {
                    double eastMeters = eastCandidates[eastIndex];
                    double candidateLongitude = originLongitude + eastMeters * longitudeDegreesPerMeter;
                    double candidateStartLatitude = originLatitude + northMeters * latitudeDegreesPerMeter;
                    double candidateMiddleLatitude = originLatitude
                        + (northMeters + stepMeters) * latitudeDegreesPerMeter;
                    double candidateEndLatitude = originLatitude
                        + (northMeters + stepMeters * 2.0) * latitudeDegreesPerMeter;

                    if (body.isHomeWorld)
                    {
                        bool startBlocked = SpawnCollisionDetector.IsWithinKscExclusionZone(
                            candidateStartLatitude, candidateLongitude, body.Radius,
                            SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);
                        bool middleBlocked = SpawnCollisionDetector.IsWithinKscExclusionZone(
                            candidateMiddleLatitude, candidateLongitude, body.Radius,
                            SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);
                        bool endBlocked = SpawnCollisionDetector.IsWithinKscExclusionZone(
                            candidateEndLatitude, candidateLongitude, body.Radius,
                            SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);
                        if (startBlocked || middleBlocked || endBlocked)
                            continue;
                    }

                    startOffsetMeters = northMeters;
                    startLatitude = candidateStartLatitude;
                    middleLatitude = candidateMiddleLatitude;
                    endLatitude = candidateEndLatitude;
                    longitude = candidateLongitude;
                    return true;
                }
            }

            skipReason = body.isHomeWorld
                ? "failed to find synthetic keep-vessel path outside the KSC exclusion zone"
                : "failed to resolve synthetic keep-vessel path";
            return false;
        }

        private static IEnumerator WaitForActiveTimelineGhost(
            ParsekFlight flight,
            int recordingIndex,
            float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (flight != null && flight.Engine != null
                    && flight.Engine.HasGhost(recordingIndex)
                    && flight.Engine.HasActiveGhost(recordingIndex))
                {
                    yield break;
                }

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForActiveTimelineGhost timed out after {timeoutSeconds:F0}s (index={recordingIndex})");
        }

        private static IEnumerator WaitForRecordingSpawn(Recording recording, float timeoutSeconds)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (recording != null
                    && recording.VesselSpawned
                    && recording.SpawnedVesselPersistentId != 0)
                {
                    yield break;
                }

                yield return null;
            }

            InGameAssert.Fail(
                $"WaitForRecordingSpawn timed out after {timeoutSeconds:F0}s (rec='{recording?.RecordingId ?? "null"}')");
        }

        private static int CountLoadedVesselsByPid(uint persistentId)
        {
            if (persistentId == 0 || FlightGlobals.Vessels == null)
                return 0;

            int matches = 0;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel vessel = FlightGlobals.Vessels[i];
                if (vessel != null && vessel.persistentId == persistentId)
                    matches++;
            }

            return matches;
        }

        private static int FindCommittedRecordingIndex(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return -1;

            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate != null && candidate.RecordingId == recordingId)
                    return i;
            }

            return -1;
        }

        private static void RemoveCommittedTreeByIdForPlaybackRuntimeTest(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return;

            var committed = RecordingStore.CommittedTrees;
            for (int i = committed.Count - 1; i >= 0; i--)
            {
                RecordingTree tree = committed[i];
                if (tree == null || tree.Id != treeId)
                    continue;

                foreach (Recording rec in tree.Recordings.Values)
                    RecordingStore.RemoveCommittedInternal(rec);
                committed.RemoveAt(i);
            }
        }

        #endregion

        #region StrategyLifecycle (#439 Phase A follow-up)

        // Snapshot/restore helpers for Funds/Science/Reputation. Used by the
        // StrategyLifecycle tests so a non-zero `InitialCost*` strategy leaves
        // the save numerically unchanged. The restore uses AddFunds/AddScience/
        // AddReputation with `TransactionReasons.None` — matches the KspStatePatcher
        // pattern. These calls DO emit FundsChanged/ScienceChanged/ReputationChanged
        // events into GameStateStore, so tests must restrict their event assertions
        // to the specific StrategyActivated/StrategyDeactivated event they expect,
        // not a blanket "no other events" check.

        private static (double funds, float science, float reputation) SnapshotFinancials()
        {
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            float science = ResearchAndDevelopment.Instance != null
                ? ResearchAndDevelopment.Instance.Science : 0f;
            float reputation = Reputation.Instance != null
                ? Reputation.Instance.reputation : 0f;
            return (funds, science, reputation);
        }

        private static void RestoreFinancials(double fundsBefore, float scienceBefore, float repBefore)
        {
            // Suppress resource-event capture for the duration of the restore
            // so the AddFunds/AddScience/SetReputation calls below do NOT emit
            // synthetic FundsChanged/ScienceChanged/ReputationChanged events
            // into GameStateStore. Without this guard, the test would leave
            // test-generated resource events behind in the save even after the
            // numeric balances are restored.
            using (SuppressionGuard.Resources())
            {
                if (Funding.Instance != null)
                {
                    double delta = fundsBefore - Funding.Instance.Funds;
                    if (System.Math.Abs(delta) > 0.01)
                        Funding.Instance.AddFunds(delta, TransactionReasons.None);
                }
                if (ResearchAndDevelopment.Instance != null)
                {
                    float delta = scienceBefore - ResearchAndDevelopment.Instance.Science;
                    if (Mathf.Abs(delta) > 0.01f)
                        ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.None);
                }
                if (Reputation.Instance != null)
                {
                    // Mirror KspStatePatcher.PatchReputation: SetReputation (NOT
                    // AddReputation) because AddReputation applies KSP's reputation
                    // curve, which would leave permanent drift on any rep-costing
                    // strategy. SetReputation writes the absolute value directly.
                    if (Mathf.Abs(repBefore - Reputation.Instance.reputation) > 0.01f)
                        Reputation.Instance.SetReputation(repBefore, TransactionReasons.None);
                }
            }
        }

        private const int StrategyLifecycleProbeWarmupFrames = 3;
        private const int StrategyLifecycleProbeRetryFrames = 30;
        private const int StrategyLifecycleAdministrationHydrationFrames = 30;
        private const int StrategyLifecycleProbeStableFrames = 2;
        private const int StrategyLifecycleActivateSettleFrames = 2;

        private struct StrategyProbeResult
        {
            public Strategies.Strategy Strategy;
            public string ConfigName;
            public string Diagnostic;
            public bool ShouldRetry;
            public bool HadProbeException;
            public bool HadRetryableReadinessBlock;
        }

        private sealed class StrategySelectionResult
        {
            public Strategies.Strategy Strategy;
            public string ConfigName;
            public string Diagnostic;
            public bool FinalProbeHadException;
            public bool FinalProbeHadRetryableReadinessBlock;
            public Canvas HiddenAdministrationCanvasForTest;
        }

        private static void DestroyHiddenAdministrationCanvasForTest(
            StrategySelectionResult result,
            string context)
        {
            if (result?.HiddenAdministrationCanvasForTest == null)
                return;

            try
            {
                ParsekLog.Verbose("TestRunner",
                    $"StrategyLifecycle: destroying hidden Administration canvas ({context})");
                UnityEngine.Object.Destroy(result.HiddenAdministrationCanvasForTest.gameObject);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("TestRunner",
                    $"StrategyLifecycle hidden Administration cleanup threw during {context}: {ex}");
            }
            finally
            {
                result.HiddenAdministrationCanvasForTest = null;
            }
        }

        private static bool ShouldHydrateAdministrationForStrategyProbe()
        {
            return StrategyLifecycleProbeSupport.ShouldHydrateAdministrationSingleton(
                administrationAvailable: KSP.UI.Screens.Administration.Instance != null,
                isSpaceCenterScene: HighLogic.LoadedScene == GameScenes.SPACECENTER,
                isCareerMode: HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode == Game.Modes.CAREER);
        }

        /// <summary>
        /// Re-entrant Administration singleton hydrator for strategy-readiness canaries.
        /// On the first (pre-warmup) call this either hydrates a hidden stock Administration
        /// canvas so <c>Administration.Instance</c> becomes non-null, or short-circuits when
        /// hydration is not needed (non-SPACECENTER / non-career / already hydrated). On a
        /// post-warmup call the helper may rebuild the hidden canvas when
        /// <c>Administration.Instance</c> was torn down during warmup (e.g. by the prior
        /// StrategyLifecycle canary's Dispose tear-down), destroying any stale canvas from
        /// the pre-warmup call first. The <paramref name="attemptTag"/> ("pre-warmup" or
        /// "post-warmup") appears in the creation / destruction logs so two-attempt failures
        /// can be diagnosed without log-line ambiguity.
        /// </summary>
        private static IEnumerator EnsureAdministrationSingletonForStrategyProbe(
            StrategySelectionResult result,
            string attemptTag)
        {
            if (result == null)
                throw new System.ArgumentNullException(nameof(result));
            if (string.IsNullOrEmpty(attemptTag))
                throw new System.ArgumentException("attemptTag must be non-empty", nameof(attemptTag));

            if (!ShouldHydrateAdministrationForStrategyProbe())
                yield break;

            if (result.HiddenAdministrationCanvasForTest != null)
            {
                DestroyHiddenAdministrationCanvasForTest(
                    result,
                    $"before-readiness-rehydrate ({attemptTag})");
                yield return null;
            }

            var uiMaster = UIMasterController.Instance;
            if (uiMaster == null)
            {
                result.Diagnostic =
                    "UIMasterController.Instance is null (cannot create hidden Administration canvas)";
                result.FinalProbeHadException = false;
                result.FinalProbeHadRetryableReadinessBlock = true;
                ParsekLog.Warn("TestRunner", $"StrategyLifecycle ({attemptTag}): {result.Diagnostic}");
                yield break;
            }
            if (uiMaster.mainCanvas == null)
            {
                result.Diagnostic =
                    "UIMasterController.Instance.mainCanvas is null (cannot parent hidden Administration canvas)";
                result.FinalProbeHadException = false;
                result.FinalProbeHadRetryableReadinessBlock = true;
                ParsekLog.Warn("TestRunner", $"StrategyLifecycle ({attemptTag}): {result.Diagnostic}");
                yield break;
            }

            var administrationSpawner =
                UnityEngine.Object.FindObjectOfType<KSP.UI.Screens.AdministrationSceneSpawner>();
            if (administrationSpawner == null)
            {
                result.Diagnostic =
                    "AdministrationSceneSpawner is null (cannot create hidden Administration canvas)";
                result.FinalProbeHadException = false;
                result.FinalProbeHadRetryableReadinessBlock = true;
                ParsekLog.Warn("TestRunner", $"StrategyLifecycle ({attemptTag}): {result.Diagnostic}");
                yield break;
            }

            var administrationScreenPrefab = administrationSpawner.AdministrationScreenPrefab;
            if (administrationScreenPrefab == null)
            {
                result.Diagnostic =
                    "AdministrationSceneSpawner.AdministrationScreenPrefab is null";
                result.FinalProbeHadException = false;
                result.FinalProbeHadRetryableReadinessBlock = true;
                ParsekLog.Warn("TestRunner", $"StrategyLifecycle ({attemptTag}): {result.Diagnostic}");
                yield break;
            }

            var administrationCanvasPrefab = administrationScreenPrefab.canvas;
            if (administrationCanvasPrefab == null)
            {
                result.Diagnostic =
                    "AdministrationSceneSpawner.AdministrationScreenPrefab.canvas is null";
                result.FinalProbeHadException = false;
                result.FinalProbeHadRetryableReadinessBlock = true;
                ParsekLog.Warn("TestRunner", $"StrategyLifecycle ({attemptTag}): {result.Diagnostic}");
                yield break;
            }

            ParsekLog.Info("TestRunner",
                $"StrategyLifecycle ({attemptTag}): creating hidden Administration canvas for readiness probe");

            var hiddenAdministrationCanvas = UnityEngine.Object.Instantiate(administrationCanvasPrefab);
            hiddenAdministrationCanvas.enabled = false;
            hiddenAdministrationCanvas.gameObject.name =
                string.IsNullOrEmpty(administrationScreenPrefab.canvasName)
                    ? administrationCanvasPrefab.gameObject.name
                    : administrationScreenPrefab.canvasName;

            var hiddenAdministrationTransform = (RectTransform)hiddenAdministrationCanvas.transform;
            hiddenAdministrationTransform.SetParent(
                uiMaster.mainCanvas.transform,
                worldPositionStays: false);
            hiddenAdministrationTransform.SetAsLastSibling();
            result.HiddenAdministrationCanvasForTest = hiddenAdministrationCanvas;

            for (int waitedFrames = 1;
                waitedFrames <= StrategyLifecycleAdministrationHydrationFrames;
                waitedFrames++)
            {
                yield return null;

                if (KSP.UI.Screens.Administration.Instance != null)
                {
                    StrategyLifecycleProbeSupport.LogAdministrationHydrationReady(
                        waitedFrames,
                        StrategyLifecycleAdministrationHydrationFrames);
                    yield break;
                }
            }

            result.Diagnostic =
                StrategyLifecycleProbeSupport.BuildAdministrationHydrationTimeoutDiagnostic(
                    StrategyLifecycleAdministrationHydrationFrames,
                    StrategyLifecycleAdministrationHydrationFrames);
            result.FinalProbeHadException = false;
            result.FinalProbeHadRetryableReadinessBlock = true;
            StrategyLifecycleProbeSupport.LogAdministrationHydrationTimeout(
                StrategyLifecycleAdministrationHydrationFrames,
                StrategyLifecycleAdministrationHydrationFrames);
            DestroyHiddenAdministrationCanvasForTest(
                result,
                $"readiness-timeout ({attemptTag})");
        }

        private static StrategyProbeResult ProbeActivatableStockStrategy()
        {
            var result = new StrategyProbeResult
            {
                Diagnostic = "no activatable stock strategy available",
                ShouldRetry = false,
                HadProbeException = false,
                HadRetryableReadinessBlock = false
            };

            var system = Strategies.StrategySystem.Instance;
            var list = system?.Strategies;
            string globalReadinessReason =
                StrategyLifecycleProbeSupport.GetGlobalReadinessBlockReason(system, list);
            if (!string.IsNullOrEmpty(globalReadinessReason))
            {
                result.Diagnostic = globalReadinessReason;
                result.ShouldRetry = true;
                result.HadRetryableReadinessBlock = true;
                StrategyLifecycleProbeSupport.LogReadinessWaiting(globalReadinessReason);
                return result;
            }

            int nullEntries = 0;
            int activeEntries = 0;
            int configlessEntries = 0;
            int namelessEntries = 0;
            int blockedEntries = 0;
            int probeThrows = 0;
            int firstProbeFailureIndex = -1;
            string firstProbeFailureSummary = null;
            string firstProbeFailureDetail = null;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null)
                {
                    nullEntries++;
                    continue;
                }

                try
                {
                    string probeConfigName = null;
                    if (s.IsActive)
                    {
                        activeEntries++;
                        continue;
                    }

                    var config = s.Config;
                    if (config == null)
                    {
                        configlessEntries++;
                        continue;
                    }

                    probeConfigName = config.Name;
                    if (string.IsNullOrEmpty(probeConfigName))
                    {
                        namelessEntries++;
                        continue;
                    }

                    string reason;
                    if (!s.CanBeActivated(out reason))
                    {
                        blockedEntries++;
                        continue;
                    }

                    result.Strategy = s;
                    result.ConfigName = probeConfigName;
                    result.Diagnostic = $"selected activatable strategy '{probeConfigName}'";
                    return result;
                }
                catch (System.Exception ex)
                {
                    probeThrows++;
                    result.HadProbeException = true;
                    if (firstProbeFailureSummary == null)
                    {
                        firstProbeFailureIndex = i;
                        firstProbeFailureSummary =
                            StrategyLifecycleProbeSupport.FormatExceptionSummary(ex);
                        firstProbeFailureDetail = ex.ToString();
                    }
                    ParsekLog.Verbose("TestRunner",
                        $"StrategyLifecycle probe exception: index={i} {ex}");
                }
            }

            if (probeThrows > 0)
            {
                StrategyLifecycleProbeSupport.LogPollExceptions(
                    list.Count,
                    probeThrows,
                    firstProbeFailureIndex,
                    firstProbeFailureSummary);
            }

            result.Diagnostic = StrategyLifecycleProbeSupport.BuildProbeDiagnostic(
                list.Count,
                nullEntries,
                activeEntries,
                configlessEntries,
                namelessEntries,
                blockedEntries,
                probeThrows,
                firstProbeFailureIndex,
                firstProbeFailureSummary,
                firstProbeFailureDetail);
            result.ShouldRetry =
                configlessEntries > 0
                || namelessEntries > 0
                || probeThrows > 0;
            return result;
        }

        private static IEnumerator WaitForStableActivatableStockStrategy(StrategySelectionResult result)
        {
            if (result == null)
                throw new System.ArgumentNullException(nameof(result));

            result.Strategy = null;
            result.ConfigName = null;
            result.Diagnostic = "no activatable stock strategy available";
            result.FinalProbeHadException = false;
            result.FinalProbeHadRetryableReadinessBlock = false;
            result.HiddenAdministrationCanvasForTest = null;

            yield return EnsureAdministrationSingletonForStrategyProbe(result, "pre-warmup");
            if (result.FinalProbeHadRetryableReadinessBlock
                && KSP.UI.Screens.Administration.Instance == null)
            {
                yield break;
            }

            for (int i = 0; i < StrategyLifecycleProbeWarmupFrames; i++)
                yield return null;

            // Unity completes Object.Destroy at frame end. If the prior
            // StrategyLifecycle canary's Dispose tear-down destroyed its hidden
            // canvas immediately before this test started, Administration.Instance
            // can look alive at entry and then become null during the warmup above.
            // Re-run hydration once after warmup so batch KSC runs do not time out
            // on that transition. The pure ShouldRehydrateAdministrationAfterWarmup
            // predicate gates the second pass so the decision is headless-testable.
            bool canvasExists = result.HiddenAdministrationCanvasForTest != null;
            bool administrationAvailable = KSP.UI.Screens.Administration.Instance != null;
            if (StrategyLifecycleProbeSupport.ShouldRehydrateAdministrationAfterWarmup(
                    canvasExists: canvasExists,
                    administrationAvailable: administrationAvailable))
            {
                yield return EnsureAdministrationSingletonForStrategyProbe(result, "post-warmup");
                if (result.FinalProbeHadRetryableReadinessBlock
                    && KSP.UI.Screens.Administration.Instance == null)
                {
                    yield break;
                }
            }

            string stableConfigName = null;
            int stableFrames = 0;
            bool sawRetryableDelay = false;
            for (int attempt = 0; attempt < StrategyLifecycleProbeRetryFrames; attempt++)
            {
                var probe = ProbeActivatableStockStrategy();
                result.Diagnostic = probe.Diagnostic;
                // Track only the final retryable state. Early hydration waits or probe
                // exceptions that later clear must not poison a later legitimate skip.
                result.FinalProbeHadException = probe.HadProbeException;
                result.FinalProbeHadRetryableReadinessBlock = probe.HadRetryableReadinessBlock;
                if (probe.Strategy != null)
                {
                    if (probe.ConfigName == stableConfigName)
                        stableFrames++;
                    else
                    {
                        stableConfigName = probe.ConfigName;
                        stableFrames = 1;
                    }

                    result.Strategy = probe.Strategy;
                    result.ConfigName = probe.ConfigName;
                    if (stableFrames >= StrategyLifecycleProbeStableFrames)
                    {
                        if (sawRetryableDelay)
                        {
                            StrategyLifecycleProbeSupport.LogReadinessSettled(
                                attempt + 1,
                                StrategyLifecycleProbeRetryFrames,
                                result.Diagnostic);
                        }
                        yield break;
                    }

                    yield return null;
                    continue;
                }

                result.Strategy = null;
                result.ConfigName = null;
                stableConfigName = null;
                stableFrames = 0;
                if (!probe.ShouldRetry)
                    yield break;

                sawRetryableDelay = true;
                yield return null;
            }

            if (sawRetryableDelay && (result.Strategy == null || string.IsNullOrEmpty(result.ConfigName)))
            {
                StrategyLifecycleProbeSupport.LogReadinessTimeout(
                    StrategyLifecycleProbeRetryFrames,
                    StrategyLifecycleProbeRetryFrames,
                    result.Diagnostic);
            }
        }

        [InGameTest(Category = "StrategyLifecycle", Scene = GameScenes.SPACECENTER,
            Description = "#439 Phase A: StrategyLifecyclePatch postfixes emit StrategyActivated/StrategyDeactivated events into GameStateStore for a real Strategies.Strategy instance.")]
        public IEnumerator ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents()
        {
            // Gate 1: career-mode only — StrategySystem is null outside career.
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                yield break;
            }
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip($"StrategySystem is career-only (mode={HighLogic.CurrentGame.Mode})");
                yield break;
            }

            // Gate 2/3: wait briefly for StrategySystem hydration to stabilize, but
            // DO NOT silently convert persistent probe exceptions into a skip. If
            // readiness never settles after the retry window, fail with diagnostics.
            var selection = new StrategySelectionResult();
            yield return WaitForStableActivatableStockStrategy(selection);
            var strategy = selection.Strategy;
            var configName = selection.ConfigName;

            if (strategy == null || string.IsNullOrEmpty(configName))
            {
                DestroyHiddenAdministrationCanvasForTest(
                    selection,
                    "unsuccessful-readiness-probe");
                if (StrategyLifecycleProbeSupport.ShouldFailUnavailableSelection(
                    selection.FinalProbeHadException,
                    selection.FinalProbeHadRetryableReadinessBlock))
                {
                    InGameAssert.Fail($"StrategyLifecycle readiness never stabilized: {selection.Diagnostic}");
                    yield break;
                }
                InGameAssert.Skip(selection.Diagnostic);
                yield break;
            }

            try
            {
                var strategyConfig = strategy.Config;
                InGameAssert.IsNotNull(strategyConfig, "Selected strategy lost Config after probe");
                InGameAssert.IsFalse(string.IsNullOrEmpty(strategyConfig.Name),
                    "Selected strategy must have a non-empty Config.Name");
                ParsekLog.Info("TestRunner",
                    $"StrategyLifecycle test target: configName={configName} title={strategy.Title} " +
                    $"setupF={strategy.InitialCostFunds.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"setupS={strategy.InitialCostScience.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"setupR={strategy.InitialCostReputation.ToString("R", CultureInfo.InvariantCulture)}");

                // Snapshot financials BEFORE activation so teardown can restore.
                var (fundsBefore, sciBefore, repBefore) = SnapshotFinancials();

                // Snapshot event and ledger-action counts so teardown can truncate
                // back to the pre-test tail. The test exercises a real KSC capture
                // path which (per GameStateRecorder.OnStrategyActivated) ALSO
                // forwards into LedgerOrchestrator.OnKscSpending in KSC-scope,
                // writing a StrategyActivate GameAction to the ledger. Teardown
                // removes both to keep the save byte-equivalent (ignoring strategy
                // dateActivated/dateDeactivated bookkeeping on Strategies.Strategy
                // itself, which stock sets unconditionally and Parsek does not own).
                int eventCountBefore = GameStateStore.EventCount;
                int ledgerCountBefore = Ledger.Actions.Count;

                // Install a tee-style observer so the assertions can capture log lines
                // without muting the live KSP log file.
                var captured = new List<string>();
                var priorObserver = ParsekLog.TestObserverForTesting;
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                // Note: GameStateRecorder.IsReplayingActions is false during normal
                // test-runner execution — we are not inside a KspStatePatcher walk — so
                // the lifecycle postfixes WILL emit their events. If a future change
                // starts a recalculation walk mid-test, this assumption breaks and the
                // test's event-find step will fail with a clear message.

                for (int i = 0; i < StrategyLifecycleActivateSettleFrames; i++)
                    yield return null;

                bool activateOk;
                try
                {
                    activateOk = strategy.Activate();
                }
                catch (System.Exception ex)
                {
                    ParsekLog.TestObserverForTesting = priorObserver;
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    GameStateStore.TruncateEventsForTesting(eventCountBefore);
                    Ledger.TruncateActionsForTesting(ledgerCountBefore);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "activate-throw");
                    InGameAssert.Fail(
                        $"Strategy.Activate threw for key='{configName}' after readiness stabilized: {ex}");
                    yield break;
                }

                // deactSnapshot is set inside the first try and read in the second;
                // initialize to GameStateStore.EventCount so a throw before the
                // deactivate assignment still gives the second try a sane lower
                // bound (tail slice would simply be empty, failing the event-find
                // with a clear message rather than an IndexOutOfRangeException).
                int deactSnapshot = GameStateStore.EventCount;
                bool deactivateOk = false;
                try
                {
                    // The Strategy lifecycle Harmony postfixes run synchronously
                    // inside the stock Activate/Deactivate calls. Keep these
                    // assertions on the same frame: the hidden Administration UI
                    // can reconcile strategy rows on the next Unity update and make
                    // IsActive a poor proxy for whether the Activate postfix emitted.
                    InGameAssert.IsTrue(activateOk, "Strategy.Activate returned false");
                    InGameAssert.IsTrue(strategy.IsActive,
                        "Strategy.IsActive should be true after Activate returned true");

                    // Find the first StrategyActivated event after the snapshot cursor.
                    bool foundActivate = false;
                    string activateDetail = null;
                    double activateUt = 0;
                    for (int i = eventCountBefore; i < GameStateStore.EventCount; i++)
                    {
                        var evt = GameStateStore.Events[i];
                        if (evt.eventType == GameStateEventType.StrategyActivated
                            && evt.key == configName)
                        {
                            foundActivate = true;
                            activateDetail = evt.detail;
                            activateUt = evt.ut;
                            break;
                        }
                    }
                    InGameAssert.IsTrue(foundActivate,
                        $"Expected StrategyActivated event with key='{configName}' in tail slice [{eventCountBefore}..{GameStateStore.EventCount})");
                    // Note: do NOT assert ut > 0. On a fresh career save the
                    // activation can legitimately happen at Planetarium UT 0.0,
                    // which would false-negative a perfectly valid capture. The
                    // event-found assertion above already proves the postfix fired
                    // and stamped the row with the current UT.
                    // Silence unused-variable warning on activateUt:
                    _ = activateUt;
                    InGameAssert.IsNotNull(activateDetail, "StrategyActivated event detail must not be null");
                    InGameAssert.Contains(activateDetail, "title=");
                    InGameAssert.Contains(activateDetail, "factor=");
                    InGameAssert.Contains(activateDetail, "setupFunds=");
                    InGameAssert.Contains(activateDetail, "source=");
                    InGameAssert.Contains(activateDetail, "target=");

                    // Log-line assertion: [GameStateRecorder] + StrategyActivated + key.
                    bool sawActivateLog = captured.Any(l =>
                        l.Contains("[GameStateRecorder]")
                        && l.Contains("StrategyActivated")
                        && l.Contains(configName));
                    InGameAssert.IsTrue(sawActivateLog,
                        $"Expected [GameStateRecorder] INFO log line for StrategyActivated '{configName}'");

                    // Deactivate inside the try so the finally-block fallback only
                    // fires on an exception. Snapshot the event cursor first so the
                    // second-phase tail slice only contains the deactivate row.
                    deactSnapshot = GameStateStore.EventCount;
                    deactivateOk = strategy.Deactivate();
                }
                catch
                {
                    // Ensure the observer + financials are restored on an exception path.
                    // We leave the observer installed on the happy path so the
                    // deactivate assertion block can read the deactivate log line that
                    // was emitted synchronously inside strategy.Deactivate above.
                    if (strategy.IsActive)
                    {
                        try { strategy.Deactivate(); }
                        catch (System.Exception innerEx)
                        {
                            ParsekLog.Warn("TestRunner",
                                $"StrategyLifecycle mid-test Deactivate threw: {innerEx}");
                        }
                    }
                    ParsekLog.TestObserverForTesting = priorObserver;
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    GameStateStore.TruncateEventsForTesting(eventCountBefore);
                    Ledger.TruncateActionsForTesting(ledgerCountBefore);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "mid-test-exception");
                    throw;
                }

                try
                {
                    InGameAssert.IsTrue(deactivateOk, "Strategy.Deactivate returned false");
                    InGameAssert.IsFalse(strategy.IsActive,
                        "Strategy.IsActive should be false after Deactivate returned true");

                    bool foundDeactivate = false;
                    string deactivateDetail = null;
                    for (int i = deactSnapshot; i < GameStateStore.EventCount; i++)
                    {
                        var evt = GameStateStore.Events[i];
                        if (evt.eventType == GameStateEventType.StrategyDeactivated
                            && evt.key == configName)
                        {
                            foundDeactivate = true;
                            deactivateDetail = evt.detail;
                            break;
                        }
                    }
                    InGameAssert.IsTrue(foundDeactivate,
                        $"Expected StrategyDeactivated event with key='{configName}' in tail slice [{deactSnapshot}..{GameStateStore.EventCount})");
                    InGameAssert.IsNotNull(deactivateDetail,
                        "StrategyDeactivated event detail must not be null");
                    InGameAssert.Contains(deactivateDetail, "activeDurationSec=");

                    // Log-line assertion for Deactivate. The deactivate log was
                    // emitted synchronously inside the strategy.Deactivate call in
                    // the previous try, so it is already sitting in `captured`.
                    bool sawDeactivateLog = captured.Any(l =>
                        l.Contains("[GameStateRecorder]")
                        && l.Contains("StrategyDeactivated")
                        && l.Contains(configName));
                    InGameAssert.IsTrue(sawDeactivateLog,
                        $"Expected [GameStateRecorder] INFO log line for StrategyDeactivated '{configName}'");
                }
                finally
                {
                    if (strategy.IsActive)
                    {
                        try { strategy.Deactivate(); }
                        catch (System.Exception ex)
                        {
                            ParsekLog.Warn("TestRunner",
                                $"StrategyLifecycle deactivate-phase teardown threw: {ex}");
                        }
                    }
                    ParsekLog.TestObserverForTesting = priorObserver;
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    // Truncate events and ledger actions AFTER the restore so the
                    // restore's (resource-suppressed) calls can't append stray rows
                    // between the assertion slice read and the truncation. Both
                    // truncations are silent no-ops if nothing was added.
                    GameStateStore.TruncateEventsForTesting(eventCountBefore);
                    Ledger.TruncateActionsForTesting(ledgerCountBefore);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "post-lifecycle-assertions");
                }
            }
            finally
            {
                DestroyHiddenAdministrationCanvasForTest(
                    selection,
                    "activate-deactivate-outer-guard");
            }
        }

        [InGameTest(Category = "StrategyLifecycle", Scene = GameScenes.SPACECENTER,
            Description = "#439 Phase A: Activate()=false path (already-active) does NOT emit StrategyActivated (pins the __result==true filter in StrategyLifecyclePatch).")]
        public IEnumerator FailedActivation_DoesNotEmitEvent()
        {
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                yield break;
            }
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip($"StrategySystem is career-only (mode={HighLogic.CurrentGame.Mode})");
                yield break;
            }

            var selection = new StrategySelectionResult();
            yield return WaitForStableActivatableStockStrategy(selection);
            var strategy = selection.Strategy;
            var configName = selection.ConfigName;

            if (strategy == null || string.IsNullOrEmpty(configName))
            {
                DestroyHiddenAdministrationCanvasForTest(
                    selection,
                    "unsuccessful-readiness-probe");
                if (StrategyLifecycleProbeSupport.ShouldFailUnavailableSelection(
                    selection.FinalProbeHadException,
                    selection.FinalProbeHadRetryableReadinessBlock))
                {
                    InGameAssert.Fail($"StrategyLifecycle readiness never stabilized: {selection.Diagnostic}");
                    yield break;
                }
                InGameAssert.Skip(selection.Diagnostic);
                yield break;
            }

            var strategyConfig = strategy.Config;
            InGameAssert.IsNotNull(strategyConfig, "Selected strategy lost Config after probe");
            InGameAssert.IsFalse(string.IsNullOrEmpty(strategyConfig.Name),
                "Selected strategy must have a non-empty Config.Name");

            // Snapshot financials — the first (successful) Activate below will
            // spend setup costs that we must restore in teardown.
            var (fundsBefore, sciBefore, repBefore) = SnapshotFinancials();

            // Snapshot event and ledger-action counts for cleanup. Set before
            // the try so the finally always sees a valid baseline (matches
            // pre-activation state, so truncation purges BOTH the first
            // Activate and the failed second Activate from the save).
            int preTestEventCount = GameStateStore.EventCount;
            int preTestLedgerCount = Ledger.Actions.Count;

            try
            {
                // Activate the strategy FIRST so the second Activate hits the
                // already-active short-circuit and returns false.
                for (int i = 0; i < StrategyLifecycleActivateSettleFrames; i++)
                    yield return null;

                bool firstActivate;
                try
                {
                    firstActivate = strategy.Activate();
                }
                catch (System.Exception ex)
                {
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    GameStateStore.TruncateEventsForTesting(preTestEventCount);
                    Ledger.TruncateActionsForTesting(preTestLedgerCount);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "initial-activate-throw");
                    InGameAssert.Fail(
                        $"Initial Strategy.Activate threw for key='{configName}' after readiness stabilized: {ex}");
                    yield break;
                }
                if (!firstActivate)
                {
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    GameStateStore.TruncateEventsForTesting(preTestEventCount);
                    Ledger.TruncateActionsForTesting(preTestLedgerCount);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "initial-activate-false");
                    InGameAssert.Skip("initial Activate returned false — cannot test failed-path filter");
                    yield break;
                }

                int eventCountBefore = GameStateStore.EventCount;

                // Second Activate — KSP short-circuits when IsActive is true and
                // returns false. The postfix should detect __result=false and skip.
                bool ok;
                try
                {
                    ok = strategy.Activate();
                }
                catch (System.Exception ex)
                {
                    RestoreFinancials(fundsBefore, sciBefore, repBefore);
                    GameStateStore.TruncateEventsForTesting(preTestEventCount);
                    Ledger.TruncateActionsForTesting(preTestLedgerCount);
                    DestroyHiddenAdministrationCanvasForTest(
                        selection,
                        "second-activate-throw");
                    InGameAssert.Fail(
                        $"Second Strategy.Activate threw for already-active key='{configName}': {ex}");
                    yield break;
                }
                InGameAssert.IsFalse(ok,
                    "Strategy.Activate should return false when already active");

                // Walk the tail slice — must find NO StrategyActivated events
                // with matching key.
                for (int i = eventCountBefore; i < GameStateStore.EventCount; i++)
                {
                    var evt = GameStateStore.Events[i];
                    if (evt.eventType == GameStateEventType.StrategyActivated
                        && evt.key == configName)
                    {
                        InGameAssert.Fail(
                            $"StrategyLifecyclePatch fired on a failed Activate() — __result filter broken " +
                            $"(found StrategyActivated at tail index {i} for key='{configName}')");
                    }
                }

                ParsekLog.Verbose("TestRunner",
                    $"FailedActivation_DoesNotEmitEvent: verified no StrategyActivated event emitted for " +
                    $"key='{configName}' across tail slice [{eventCountBefore}..{GameStateStore.EventCount})");
            }
            finally
            {
                if (strategy.IsActive)
                {
                    try { strategy.Deactivate(); }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"FailedActivation_DoesNotEmitEvent teardown Deactivate threw: {ex}");
                    }
                }
                RestoreFinancials(fundsBefore, sciBefore, repBefore);
                // Purge test-generated events and ledger actions so the save
                // stays save-neutral across repeated category runs. See
                // ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents
                // teardown for the same pattern.
                GameStateStore.TruncateEventsForTesting(preTestEventCount);
                Ledger.TruncateActionsForTesting(preTestLedgerCount);
                DestroyHiddenAdministrationCanvasForTest(
                    selection,
                    "failed-activation-teardown");
            }
        }

        #endregion

        #region Bug #450 B3 Lazy Reentry FX

        [InGameTest(Category = "ReentryFx", Scene = GameScenes.FLIGHT,
            Description = "Bug #450 B3: TryBuildReentryFx on a real Unity GameObject still returns a ReentryFxInfo for an empty ghost root, and the lazy-build helper clears the pending flag while counting the build success.")]
        public void Bug450B3_LazyBuild_OnEmptyGhostRoot_ClearsFlagAndCountsBuild()
        {
            // Residual-risk coverage for the plan's "in-game only" test gap: verifies
            // the live-Unity lazy-build path wiring. We do NOT construct a real ghost
            // (that requires an active snapshot + AvailablePart + Unity prefab), but
            // we DO exercise TryBuildReentryFx with a genuine GameObject so the Unity
            // APIs inside (GetComponentsInChildren, Mesh.CombineMeshes, ParticleSystem
            // setup) run against a real scene. Even with no renderers or meshes, the
            // builder still returns a non-null ReentryFxInfo for a non-null root, so
            // the lazy-build wrapper must clear the flag, store the info, and bump
            // reentryFxBuildsThisSession exactly once.
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

            // Flag clears after the build fires (one-shot, no retry storm).
            InGameAssert.IsFalse(state.reentryFxPendingBuild,
                "Lazy build must clear reentryFxPendingBuild after the build succeeds");
            InGameAssert.IsNotNull(state.reentryFxInfo,
                "Empty ghost roots should still produce a non-null ReentryFxInfo");
            // Counter bumps on non-null build. Empty ghost root still returns info,
            // so the session build counter must increase by exactly one.
            int buildsAfter = DiagnosticsState.health.reentryFxBuildsThisSession;
            InGameAssert.AreEqual(buildsBefore + 1, buildsAfter,
                "reentryFxBuildsThisSession must increment when TryBuildReentryFx returns info");
            // A frame-slot IS consumed (we did invoke the build). Confirms the
            // counter lives in the unthrottled-success path.
            InGameAssert.AreEqual(1, engine.FrameLazyReentryBuildCountForTesting,
                "A build attempt must consume one per-frame slot regardless of result");
        }

        [InGameTest(Category = "ReentryFx", Scene = GameScenes.FLIGHT,
            Description = "#538: live UpdateReentryFx drives the reentry fire particle system past the old 2000-rate ceiling while keeping the tuned max-particle cap on the built Unity particle system. Waits on elapsed realtime instead of a fixed frame count so the smoothing assertion is not framerate-dependent.")]
        public IEnumerator Bug538_ReentryFireDensity_UsesDoubledEmissionRange()
        {
            const int ghostIndex = 538;
            const string vesselName = "Test538";
            const float minimumUsableAtmosphereDepthMeters = 1000f;
            const double targetAltitudeMarginMeters = 500.0;
            const double targetAltitudeAtmosphereFraction = 0.25;
            const float initialSweepSurfaceSpeedMetersPerSecond = 1500f;
            const float maxSweepSurfaceSpeedMetersPerSecond = 10_000f;
            const float sweepStepMetersPerSecond = 500f;
            const float rawIntensitySaturationTarget = 0.999f;
            const float requiredNearMaxRawIntensity = 0.99f;
            const float legacyEmissionRateCeiling = 2000f;
            const float emissionSettleTimeoutSeconds = 1.5f;
            const float expectedEmissionRateTolerance = 0.5f;
            const float warpRate = 1f;

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(activeVessel, "Active vessel required for ReentryFx runtime coverage");

            CelestialBody body = activeVessel.mainBody;
            InGameAssert.IsNotNull(body, "Active vessel mainBody should not be null");
            if (!body.atmosphere || body.atmosphereDepth <= minimumUsableAtmosphereDepthMeters)
                InGameAssert.Skip($"Active vessel body '{body.name}' has no usable atmosphere for reentry-FX coverage");

            float targetAltitude = (float)System.Math.Min(
                body.atmosphereDepth - targetAltitudeMarginMeters,
                body.atmosphereDepth * targetAltitudeAtmosphereFraction);
            double pressure = body.GetPressure(targetAltitude);
            double temperature = body.GetTemperature(targetAltitude);
            double density = body.GetDensity(pressure, temperature);
            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            InGameAssert.IsGreaterThan(pressure, 0.0,
                $"Expected positive atmospheric pressure at {targetAltitude:F0} m on {body.name}");
            InGameAssert.IsGreaterThan(temperature, 0.0,
                $"Expected positive atmospheric temperature at {targetAltitude:F0} m on {body.name}");
            InGameAssert.IsGreaterThan(density, 0.0,
                $"Expected positive atmospheric density at {targetAltitude:F0} m on {body.name}");
            InGameAssert.IsGreaterThan(speedOfSound, 0.0,
                $"Expected positive speed of sound at {targetAltitude:F0} m on {body.name}");

            float targetSurfaceSpeed = initialSweepSurfaceSpeedMetersPerSecond;
            float lastComputedSurfaceSpeed = targetSurfaceSpeed;
            float rawIntensity = 0f;
            float machNumber = 0f;
            bool rawIntensitySaturated = false;
            while (targetSurfaceSpeed <= maxSweepSurfaceSpeedMetersPerSecond)
            {
                lastComputedSurfaceSpeed = targetSurfaceSpeed;
                machNumber = (float)(targetSurfaceSpeed / speedOfSound);
                rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(
                    targetSurfaceSpeed, (float)density, machNumber);
                if (rawIntensity >= rawIntensitySaturationTarget)
                {
                    rawIntensitySaturated = true;
                    break;
                }

                targetSurfaceSpeed += sweepStepMetersPerSecond;
            }

            string rawIntensitySweepSummary =
                $"body={body.name} altitude={targetAltitude.ToString("F0", CultureInfo.InvariantCulture)}m " +
                $"density={density.ToString("F6", CultureInfo.InvariantCulture)} " +
                $"speedOfSound={speedOfSound.ToString("F1", CultureInfo.InvariantCulture)}m/s " +
                $"lastSpeed={lastComputedSurfaceSpeed.ToString("F0", CultureInfo.InvariantCulture)}m/s " +
                $"lastMach={machNumber.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"rawIntensity={rawIntensity.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"saturated={rawIntensitySaturated} " +
                $"saturationTarget={rawIntensitySaturationTarget.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"sweepStart={initialSweepSurfaceSpeedMetersPerSecond.ToString("F0", CultureInfo.InvariantCulture)}m/s " +
                $"sweepStep={sweepStepMetersPerSecond.ToString("F0", CultureInfo.InvariantCulture)}m/s " +
                $"sweepCeiling={maxSweepSurfaceSpeedMetersPerSecond.ToString("F0", CultureInfo.InvariantCulture)}m/s";

            if (!rawIntensitySaturated && rawIntensity <= requiredNearMaxRawIntensity)
            {
                InGameAssert.Fail(
                    "Reentry intensity speed sweep exhausted before reaching the near-max raw-intensity floor. " +
                    rawIntensitySweepSummary);
            }

            InGameAssert.IsGreaterThan(rawIntensity, requiredNearMaxRawIntensity,
                "Expected near-max raw intensity inside atmosphere before the live emission assertion. " +
                rawIntensitySweepSummary);

            var ghostRoot = new GameObject("ParsekTestGhost_538");
            runner.TrackForCleanup(ghostRoot);
            ghostRoot.transform.position = activeVessel.transform.position;

            ReentryFxInfo info = GhostVisualBuilder.TryBuildReentryFx(
                ghostRoot,
                new Dictionary<uint, HeatGhostInfo>(),
                ghostIndex: ghostIndex,
                vesselName: vesselName);

            InGameAssert.IsNotNull(info, "TryBuildReentryFx should return info for the live reentry density test");
            InGameAssert.IsNotNull(info.fireParticles, "Reentry fire particle system should be created in live KSP");
            InGameAssert.AreEqual(GhostVisualBuilder.ReentryFireMaxParticles, info.fireParticles.main.maxParticles,
                "Built reentry fire particle system should use the tuned max-particle cap");

            Vector3 rotatingFrameVelocity = (Vector3)body.getRFrmVel(ghostRoot.transform.position);
            Vector3 desiredSurfaceVelocity = ghostRoot.transform.right * targetSurfaceSpeed;
            var state = new GhostPlaybackState
            {
                ghost = ghostRoot,
                reentryFxInfo = info,
                lastInterpolatedBodyName = body.name,
                lastInterpolatedAltitude = targetAltitude,
                lastInterpolatedVelocity = rotatingFrameVelocity + desiredSurfaceVelocity,
            };

            var engine = new GhostPlaybackEngine(positioner: null);
            float deadline = Time.realtimeSinceStartup + emissionSettleTimeoutSeconds;
            float actualRate = 0f;
            while (Time.realtimeSinceStartup < deadline)
            {
                engine.UpdateReentryFx(recIdx: ghostIndex, state, vesselName: vesselName, warpRate: warpRate);
                actualRate = info.fireParticles.emission.rateOverTimeMultiplier;
                if (actualRate > legacyEmissionRateCeiling)
                    break;
                yield return null;
            }

            InGameAssert.IsGreaterThan(info.lastIntensity, GhostVisualBuilder.ReentryFireThreshold,
                "Smoothed reentry intensity should cross the fire threshold before we assert the live emission rate");
            InGameAssert.IsTrue(info.fireParticles.isPlaying,
                "Reentry fire particles should be playing once the live intensity crosses the fire threshold");

            float expectedRate = Mathf.Lerp(
                GhostVisualBuilder.ReentryFireEmissionMin,
                GhostVisualBuilder.ReentryFireEmissionMax,
                Mathf.InverseLerp(GhostVisualBuilder.ReentryFireThreshold, 1f, info.lastIntensity));
            InGameAssert.ApproxEqual(expectedRate, actualRate, expectedEmissionRateTolerance,
                "UpdateReentryFx should drive the live particle emission rate from the shared tuned range");
            InGameAssert.IsGreaterThan(actualRate, legacyEmissionRateCeiling,
                $"Bug #538 regression: tuned live emission rate should rise past the old {legacyEmissionRateCeiling.ToString("F0", CultureInfo.InvariantCulture)} particles/sec ceiling within {emissionSettleTimeoutSeconds:F1}s of realtime");
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

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "#406 follow-up: ReusePrimaryGhostAcrossCycle preserves the ghost GameObject identity, the camera pivot, and reentryFxInfo across a loop-cycle boundary instead of destroy+spawn. The session spawn counter does not bump; the reuse counter does. Also asserts the happy-path VERBOSE log line (\"ghost reused across loop cycle\") fires with the expected index/vessel/cycle tokens — clean-context review finding #12 against PR #394.")]
        public void Bug406_ReusePrimaryGhostAcrossCycle_PreservesGhostIdentity()
        {
            // Build a minimal live-Unity ghost root + cameraPivot child so the
            // reuse helper's Transform walks execute against a real hierarchy.
            // This is the integration counterpart to the pure-helper xUnit
            // coverage in `Bug406GhostReuseLoopCycleTests.cs` — it exercises
            // the engine entry point on a state with a non-null ghost.
            //
            // Why this matters: the xUnit tests can only drive the `ghost == null`
            // defensive branch (no Unity available). A refactor that swaps the
            // reuse path to destroy+spawn under the hood would pass every xUnit
            // test and only fail here — where we observe the GameObject
            // reference identity across the call.
            var ghostRoot = new GameObject("ParsekTestGhost_Bug406Reuse");
            runner.TrackForCleanup(ghostRoot);
            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghostRoot.transform, false);

            // Two visible child "parts" — one will be deactivated to simulate
            // a prior-cycle decouple, which the reuse path must reactivate.
            var part1 = new GameObject("part1");
            part1.transform.SetParent(ghostRoot.transform, false);
            var part2 = new GameObject("part2");
            part2.transform.SetParent(ghostRoot.transform, false);
            part2.SetActive(false);  // simulate decoupled

            // Pre-built reentryFxInfo stand-in so the reuse path's preservation
            // invariant is observable. The field is preserved by reference;
            // destroying/rebuilding it would swap to a new instance.
            var reentryInfoMarker = new ReentryFxInfo();

            var state = new GhostPlaybackState
            {
                vesselName = "TestReuse",
                ghost = ghostRoot,
                cameraPivot = cameraPivotObj.transform,
                loopCycleIndex = 4,
                playbackIndex = 50,
                partEventIndex = 10,
                explosionFired = true,
                pauseHidden = true,
                reentryFxInfo = reentryInfoMarker,
                reentryFxPendingBuild = false,
                heatInfos = new System.Collections.Generic.Dictionary<uint, HeatGhostInfo>(),
            };

            var engine = new GhostPlaybackEngine(positioner: null);
            int spawnsBefore = DiagnosticsState.health.ghostBuildsThisSession;
            int reusesBefore = DiagnosticsState.health.ghostReusedAcrossCycleThisSession;
            int destroysBefore = DiagnosticsState.health.ghostDestroysThisSession;

            // Minimal trajectory stub — ReusePrimaryGhostAcrossCycle's inner
            // PrimeLoadedGhostForPlaybackUT dereferences `traj.VesselName` via
            // UpdateReentryFx, so traj must be non-null. Positioner stays null
            // above so PositionLoadedGhostAtPlaybackUT early-returns without
            // trying to move the test GameObject.
            var traj = new TestTrajectoryForBug406();

            // Capture log lines across the reuse call so the happy-path VERBOSE
            // emitted by ReusePrimaryGhostAcrossCycle can be asserted. Without
            // this assertion, a silent regression that re-routes the reuse path
            // to destroy+spawn would still pass every identity/counter check
            // here (a destroy+spawn path that happened to preserve Unity
            // references in a pool would satisfy ReferenceEquals, and the
            // diagnostic counters could be wired to bump the reuse counter
            // regardless). The VERBOSE log line is the distinctive tell that
            // the reuse branch was taken — see clean-context review finding #12
            // against PR #394. Reset rate limits first so this test is
            // deterministic when re-run within the 5-second rate window of a
            // prior invocation in the same KSP session.
            ParsekLog.ResetRateLimitsForTesting();
            var capturedLog = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            ParsekLog.TestObserverForTesting = line => { capturedLog.Add(line); priorObserver?.Invoke(line); };
            try
            {
                engine.ReusePrimaryGhostAcrossCycle(
                    index: 99, traj: traj, flags: default, state,
                    playbackUT: 0.0, newCycleIndex: 5);
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
            }

            // Log-line assertion: the reuse path MUST emit the distinctive
            // "ghost reused across loop cycle" VERBOSE, and it MUST carry the
            // recording index, vessel name, and both cycle indices so a log
            // reader can confirm which ghost reused and which cycle boundary
            // it straddled. If the orchestrator is refactored to call the log
            // helper with a different format string (or to skip it entirely
            // on a fast path), this assertion is what flags the regression.
            string reuseLine = capturedLog.FirstOrDefault(l => l.Contains("ghost reused across loop cycle"));
            InGameAssert.IsNotNull(reuseLine,
                "ReusePrimaryGhostAcrossCycle must emit a VERBOSE containing \"ghost reused across loop cycle\"; " +
                "if this fails, either the log line was removed or VerboseRateLimited suppressed it (ResetRateLimitsForTesting guards against the latter)");
            InGameAssert.Contains(reuseLine, "#99",
                "reuse log line must carry the recording index (#99 in this test) so log readers can correlate per-ghost reuse events");
            InGameAssert.Contains(reuseLine, "TestReuse",
                "reuse log line must carry the vessel name (\"TestReuse\") so log readers can correlate by vessel instead of by index");
            InGameAssert.Contains(reuseLine, "from cycle=4",
                "reuse log line must carry the previous cycle index so cycle-boundary regressions are visible in logs");
            InGameAssert.Contains(reuseLine, "to cycle=5",
                "reuse log line must carry the new cycle index so cycle-boundary regressions are visible in logs");
            InGameAssert.Contains(reuseLine, "[VERBOSE]",
                "reuse log line must be emitted at VERBOSE level — a level drift to INFO/WARN would spam production logs");
            InGameAssert.Contains(reuseLine, "[Engine]",
                "reuse log line must be emitted under the Engine subsystem tag for grep-ability");

            // Identity invariants: the ghost GameObject and the cameraPivot
            // Transform are the SAME instances they were before. The
            // reentryFxInfo reference is preserved.
            InGameAssert.IsTrue(ReferenceEquals(ghostRoot, state.ghost),
                "ReusePrimaryGhostAcrossCycle must NOT replace state.ghost — identity preservation is the whole optimisation");
            InGameAssert.IsTrue(ReferenceEquals(cameraPivotObj.transform, state.cameraPivot),
                "cameraPivot Transform identity must survive the reuse — WatchModeController/FlightCamera hold refs through the retarget");
            InGameAssert.IsTrue(ReferenceEquals(reentryInfoMarker, state.reentryFxInfo),
                "reentryFxInfo must be preserved by reference; destroy+rebuild would swap this to null then a new instance");

            // Hierarchy invariants: part2 (the simulated decoupled part) is
            // now active again, matching the snapshot baseline for the new cycle.
            InGameAssert.IsTrue(part1.activeSelf, "part1 was active; must remain active after reuse");
            InGameAssert.IsTrue(part2.activeSelf,
                "part2 was deactivated to simulate prior-cycle decouple; reuse must re-activate it");

            // State invariants: iterators rewound, per-cycle flags reset,
            // new cycle index applied.
            InGameAssert.AreEqual(0, state.playbackIndex,
                "playbackIndex must reset to 0 for the new cycle");
            InGameAssert.AreEqual(0, state.partEventIndex,
                "partEventIndex must reset to 0 for the new cycle");
            InGameAssert.AreEqual(5L, state.loopCycleIndex,
                "loopCycleIndex must advance to the new cycle");
            InGameAssert.IsFalse(state.explosionFired,
                "explosionFired must reset so the new cycle can re-decide");
            InGameAssert.IsFalse(state.pauseHidden,
                "pauseHidden must reset so the new cycle re-evaluates pause-window");

            // Counter invariants (#414 and #450 B3): reuse does not consume a
            // spawn slot and does not bump the lazy-reentry-build cap. The
            // reuse counter IS bumped so diagnostics can count cycles.
            int spawnsAfter = DiagnosticsState.health.ghostBuildsThisSession;
            int reusesAfter = DiagnosticsState.health.ghostReusedAcrossCycleThisSession;
            int destroysAfter = DiagnosticsState.health.ghostDestroysThisSession;
            InGameAssert.AreEqual(spawnsBefore, spawnsAfter,
                "ghostBuildsThisSession must NOT bump on reuse — reuse is not a spawn (#414 invariant)");
            InGameAssert.AreEqual(reusesBefore + 1, reusesAfter,
                "ghostReusedAcrossCycleThisSession must bump exactly once per reuse");
            InGameAssert.AreEqual(destroysBefore, destroysAfter,
                "ghostDestroysThisSession must NOT bump on reuse — the ghost was never destroyed");
            InGameAssert.AreEqual(0, engine.FrameSpawnCountForTesting,
                "frameSpawnCount must NOT bump on reuse (#414 spawn throttle must not be consumed)");
            InGameAssert.AreEqual(0, engine.FrameLazyReentryBuildCountForTesting,
                "frameLazyReentryBuildCount must NOT bump on reuse itself (the NEXT frame's UpdateReentryFx may build, but that is the B3 path, not reuse)");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "#539 in-game replacement for the removed xUnit pending-cycle-boundary stub: a pending loop first-spawn that crosses into the next cycle advances loopCycleIndex without emitting loop-restart or camera events for a ghost that never materialized")]
        public void PendingLoopCycleBoundary_PendingGhostDoesNotEmitRestartEvents_InGame()
        {
            var engine = new GhostPlaybackEngine(new PendingLoopBoundaryPositioner());
            engine.ResolvePlaybackDistanceOverride =
                (recordingIndex, playbackTrajectory, ghostState, playbackUT) => 0.0;
            engine.ResolvePlaybackActiveVesselDistanceOverride =
                (recordingIndex, playbackTrajectory, ghostState, playbackUT) => 0.0;

            var traj = new TestPendingLoopBoundaryTrajectoryForBug539();
            var state = new GhostPlaybackState
            {
                vesselName = traj.VesselName,
                loopCycleIndex = 0,
                pendingSpawnLifecycle = PendingSpawnLifecycle.LoopEnter,
                pendingSpawnFlags = new TrajectoryPlaybackFlags
                {
                    recordingId = traj.RecordingId,
                    chainEndUT = traj.EndUT,
                    segmentLabel = traj.VesselName,
                },
            };
            engine.ghostStates[0] = state;

            var cameraEvents = new List<CameraActionEvent>();
            var restartedEvents = new List<LoopRestartedEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);
            engine.OnLoopRestarted += evt => restartedEvents.Add(evt);

            var flags = new[]
            {
                new TrajectoryPlaybackFlags
                {
                    recordingId = traj.RecordingId,
                    chainEndUT = traj.EndUT,
                    segmentLabel = traj.VesselName,
                }
            };
            var ctx = new FrameContext
            {
                currentUT = 260.0,
                warpRate = 1f,
                warpRateIndex = 0,
                activeVesselPos = Vector3d.zero,
                protectedIndex = -1,
                protectedLoopCycleIndex = -1,
                externalGhostCount = 0,
                mapViewEnabled = false,
                autoLoopIntervalSeconds = 150.0,
            };

            var capturedLog = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestObserverForTesting = line =>
            {
                capturedLog.Add(line);
                priorObserver?.Invoke(line);
            };

            try
            {
                engine.UpdatePlayback(
                    new IPlaybackTrajectory[] { traj },
                    flags,
                    ctx);
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }

            InGameAssert.AreEqual(1L, state.loopCycleIndex,
                "pending-build cycle-boundary path must advance loopCycleIndex so the same null ghost does not re-trigger the boundary branch every frame");
            InGameAssert.AreEqual(PendingSpawnLifecycle.LoopEnter, state.pendingSpawnLifecycle,
                "pending loop lifecycle must stay armed so a later snapshot availability can still finish the first spawn");
            InGameAssert.AreEqual(traj.RecordingId, state.pendingSpawnFlags.recordingId,
                "pending spawn flags must remain attached to the same recording after the cycle advance");
            InGameAssert.IsTrue(ReferenceEquals(state, engine.ghostStates[0]),
                "cycle-boundary path must keep the same pending GhostPlaybackState shell in the engine map");
            InGameAssert.IsNull(state.ghost,
                "this regression relies on a ghost that never materialized; no fallback GameObject should appear when the debris snapshot is still missing");
            InGameAssert.AreEqual(0, engine.FrameSpawnCountForTesting,
                "failing the missing-snapshot reload after the cycle advance must not consume a completed spawn slot");
            InGameAssert.AreEqual(0, cameraEvents.Count,
                "pending-build cycle-boundary path must not emit loop camera events for a ghost that never spawned");
            InGameAssert.AreEqual(0, restartedEvents.Count,
                "pending-build cycle-boundary path must not emit LoopRestarted for a ghost that never spawned");
            InGameAssert.IsTrue(capturedLog.Any(l =>
                    l.Contains("ReusePrimaryGhostAcrossCycle: #0 skipped")
                    && l.Contains("state.ghost is null")
                    && l.Contains("advanced cycle=1")),
                "the null-ghost reuse breadcrumb must be logged so KSP.log shows that the cycle advanced without a real ghost");
            InGameAssert.IsFalse(capturedLog.Any(l =>
                    l.Contains(traj.VesselName)
                    && l.Contains("ghost reused across loop cycle")),
                "the full reuse log line must stay absent here; this path should only advance the pending shell, not report a real ghost reuse");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "#461: loop-cycle reuse clears deferVisibilityUntilPlaybackSync and re-activates the reused ghost on the same UpdatePlayback frame when the ghost stays visible")]
        public void Bug406_ReuseClearsDeferVisOnSameFrame()
        {
            var state = RunBug406ReuseVisibilityScenario(
                hiddenByZone: false,
                out var originalGhost,
                out var capturedLog);

            InGameAssert.AreEqual(1L, state.loopCycleIndex,
                "test must cross a real loop-cycle boundary; otherwise the post-frame visibility assertion is meaningless");
            InGameAssert.AreEqual(0, state.playbackIndex,
                "cycle-boundary pass must reset playbackIndex before the new-cycle frame finishes");
            InGameAssert.AreEqual(0, state.partEventIndex,
                "cycle-boundary pass must reset partEventIndex before the new-cycle frame finishes");
            InGameAssert.IsFalse(state.deferVisibilityUntilPlaybackSync,
                "visible same-frame path must clear deferVisibilityUntilPlaybackSync after reuse");
            InGameAssert.IsNotNull(state.ghost,
                "visible same-frame path must keep the reused ghost loaded");
            InGameAssert.IsTrue(ReferenceEquals(originalGhost, state.ghost),
                "full UpdatePlayback loop-cycle path must preserve the same ghost GameObject instance instead of rebuilding");
            InGameAssert.IsTrue(state.ghost.activeSelf,
                "visible same-frame path must re-activate the reused ghost before UpdatePlayback returns");
            InGameAssert.IsTrue(capturedLog.Any(l =>
                    l.Contains("TestReuseVisibility")
                    && l.Contains("ghost reused across loop cycle")),
                "full-frame regression must prove UpdatePlayback took the reuse path instead of destroy+spawn");
            InGameAssert.IsFalse(capturedLog.Any(l =>
                    l.Contains("TestReuseVisibility")
                    && l.Contains("re-shown: entered visible distance tier")),
                "zone rendering must NOT re-show a deferred ghost before ActivateGhostVisualsIfNeeded owns the same-frame activation");
        }

        [InGameTest(Category = "GhostPlayback", Scene = GameScenes.FLIGHT,
            Description = "#461: loop-cycle reuse leaves the ghost deferred/inactive when the same frame is hidden by zone rendering")]
        public void Bug406_ReuseHiddenByZone_DoesNotActivateGhostOnSameFrame()
        {
            var state = RunBug406ReuseVisibilityScenario(
                hiddenByZone: true,
                out var originalGhost,
                out var capturedLog);

            InGameAssert.AreEqual(1L, state.loopCycleIndex,
                "hidden-by-zone variant must still cross a real loop-cycle boundary");
            InGameAssert.AreEqual(0, state.playbackIndex,
                "hidden-by-zone variant must still rewind playbackIndex on the reused cycle");
            InGameAssert.AreEqual(0, state.partEventIndex,
                "hidden-by-zone variant must still rewind partEventIndex on the reused cycle");
            InGameAssert.IsNotNull(state.ghost,
                "hidden-tier prewarm should keep the reused ghost loaded while it remains invisible");
            InGameAssert.IsTrue(ReferenceEquals(originalGhost, state.ghost),
                "hidden-tier prewarm path must keep the same ghost GameObject instance loaded across the loop boundary");
            InGameAssert.IsTrue(state.deferVisibilityUntilPlaybackSync,
                "hidden-by-zone branch must preserve deferVisibilityUntilPlaybackSync for the next visible frame");
            InGameAssert.IsFalse(state.ghost.activeSelf,
                "hidden-by-zone branch must not activate the reused ghost on the cycle-boundary frame");
            InGameAssert.IsTrue(capturedLog.Any(l =>
                    l.Contains("TestReuseVisibility")
                    && l.Contains("ghost reused across loop cycle")),
                "hidden-by-zone regression must prove UpdatePlayback took the reuse path instead of destroy+spawn");
            InGameAssert.IsFalse(capturedLog.Any(l =>
                    l.Contains("TestReuseVisibility")
                    && l.Contains("re-shown: entered visible distance tier")),
                "zone rendering must NOT re-show a deferred ghost while the loop frame is still hidden by zone policy");
        }

        private GhostPlaybackState RunBug406ReuseVisibilityScenario(
            bool hiddenByZone, out GameObject originalGhost, out List<string> capturedLog)
        {
            var flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("no ParsekFlight");

            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null)
                InGameAssert.Skip("needs an active vessel with a main body");

            if (flight.WatchedRecordingIndex >= 0)
                flight.ExitWatchMode(skipCameraRestore: true);

            var engine = new GhostPlaybackEngine(flight);
            double renderDistance = hiddenByZone ? 1.0e9 : 0.0;
            engine.ResolvePlaybackDistanceOverride =
                (recordingIndex, playbackTrajectory, ghostState, playbackUT) => renderDistance;
            engine.ResolvePlaybackActiveVesselDistanceOverride =
                (recordingIndex, playbackTrajectory, ghostState, playbackUT) => 0.0;

            originalGhost = new GameObject(
                hiddenByZone ? "ParsekTestGhost_Bug406ReuseHiddenByZone" : "ParsekTestGhost_Bug406ReuseVisible");
            runner.TrackForCleanup(originalGhost);
            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(originalGhost.transform, false);

            var state = new GhostPlaybackState
            {
                vesselName = "TestReuseVisibility",
                ghost = originalGhost,
                cameraPivot = cameraPivotObj.transform,
                loopCycleIndex = 0,
                playbackIndex = 7,
                partEventIndex = 3,
                flagEventIndex = 1,
                explosionFired = true,
                pauseHidden = true,
                audioMuted = true,
            };

            engine.ghostStates[0] = state;
            var traj = new TestLoopTrajectoryForBug461(
                bodyName: activeVessel.mainBody.name,
                latitude: activeVessel.latitude,
                longitude: activeVessel.longitude,
                altitude: System.Math.Max(0.0, activeVessel.altitude),
                addHiddenPrewarmEvent: hiddenByZone);
            var flags = new[]
            {
                new TrajectoryPlaybackFlags
                {
                    chainEndUT = traj.EndUT,
                    recordingId = traj.RecordingId,
                    segmentLabel = traj.VesselName,
                }
            };

            var ctx = new FrameContext
            {
                currentUT = 12.0,
                warpRate = 1f,
                warpRateIndex = 0,
                activeVesselPos = Vector3d.zero,
                protectedIndex = -1,
                protectedLoopCycleIndex = -1,
                externalGhostCount = 0,
                mapViewEnabled = false,
                autoLoopIntervalSeconds = 10.0,
            };

            var localLog = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;
            var priorVerbose = ParsekLog.VerboseOverrideForTesting;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestObserverForTesting = line =>
            {
                localLog.Add(line);
                priorObserver?.Invoke(line);
            };

            try
            {
                engine.UpdatePlayback(
                    new IPlaybackTrajectory[] { traj },
                    flags,
                    ctx);
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }

            capturedLog = localLog;
            return state;
        }

        private sealed class PendingLoopBoundaryPositioner : IGhostPositioner
        {
            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
            }

            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx, uint anchorVesselId)
            {
            }

            public void PositionAtPoint(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, TrajectoryPoint point)
            {
            }

            public void PositionAtSurface(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state)
            {
            }

            public void PositionFromOrbit(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut)
            {
            }

            public void PositionLoop(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
            }

            public bool TryResolveExplosionAnchorPosition(int index,
                IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                worldPosition = Vector3.zero;
                return false;
            }

            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, int protectedIndex)
            {
                return new ZoneRenderingResult();
            }

            public void ClearOrbitCache()
            {
            }
        }

        #endregion
    }

    /// <summary>
    /// Minimal IPlaybackTrajectory for Bug406 in-game test: all collections empty
    /// so ApplyPartEvents / ApplyFlagEvents early-return, no snapshot so
    /// GetGhostSnapshot returns null, non-null VesselName so UpdateReentryFx
    /// does not NRE, zero points/orbit/surface so PositionLoadedGhostAtPlaybackUT
    /// does not try to move the test GameObject even if a positioner were wired.
    /// Kept in this file (same assembly as IPlaybackTrajectory) so it can
    /// implement the internal interface without InternalsVisibleTo gymnastics.
    /// </summary>
    internal class TestTrajectoryForBug406 : IPlaybackTrajectory
    {
        public System.Collections.Generic.List<TrajectoryPoint> Points { get; } = new System.Collections.Generic.List<TrajectoryPoint>();
        public System.Collections.Generic.List<OrbitSegment> OrbitSegments { get; } = new System.Collections.Generic.List<OrbitSegment>();
        public bool HasOrbitSegments => false;
        public System.Collections.Generic.List<TrackSection> TrackSections { get; } = new System.Collections.Generic.List<TrackSection>();
        public double StartUT => 0;
        public double EndUT => 0;
        public int RecordingFormatVersion => 0;
        public System.Collections.Generic.List<PartEvent> PartEvents { get; } = new System.Collections.Generic.List<PartEvent>();
        public System.Collections.Generic.List<FlagEvent> FlagEvents { get; } = new System.Collections.Generic.List<FlagEvent>();
        public ConfigNode GhostVisualSnapshot => null;
        public ConfigNode VesselSnapshot => null;
        public string VesselName => "TestReuse";
        public string RecordingId => "test-b406";
        public bool LoopPlayback => true;
        public double LoopIntervalSeconds => 10;
        public LoopTimeUnit LoopTimeUnit => LoopTimeUnit.Sec;
        public uint LoopAnchorVesselId => 0;
        public double LoopStartUT => double.NaN;
        public double LoopEndUT => double.NaN;
        public TerminalState? TerminalStateValue => null;
        public SurfacePosition? SurfacePos => null;
        public double TerrainHeightAtEnd => double.NaN;
        public bool PlaybackEnabled => true;
        public bool IsDebris => false;
        public int LoopSyncParentIdx { get; set; } = -1;
        public string TerminalOrbitBody => null;
        public double TerminalOrbitSemiMajorAxis => 0;
        public double TerminalOrbitEccentricity => 0;
        public double TerminalOrbitInclination => 0;
        public double TerminalOrbitLAN => 0;
        public double TerminalOrbitArgumentOfPeriapsis => 0;
        public double TerminalOrbitMeanAnomalyAtEpoch => 0;
        public double TerminalOrbitEpoch => 0;
        public RecordingEndpointPhase EndpointPhase => RecordingEndpointPhase.Unknown;
        public string EndpointBodyName => null;
    }

    internal class TestLoopTrajectoryForBug461 : IPlaybackTrajectory
    {
        internal TestLoopTrajectoryForBug461(
            string bodyName, double latitude, double longitude, double altitude,
            bool addHiddenPrewarmEvent)
        {
            Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 0,
                    latitude = latitude,
                    longitude = longitude,
                    altitude = altitude,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = bodyName,
                },
                new TrajectoryPoint
                {
                    ut = 5,
                    latitude = latitude,
                    longitude = longitude + 0.001,
                    altitude = altitude + 10.0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = bodyName,
                },
            };

            PartEvents = addHiddenPrewarmEvent
                ? new List<PartEvent>
                {
                    new PartEvent
                    {
                        ut = 3.0,
                        partPersistentId = 1,
                        partName = "prewarm",
                        eventType = PartEventType.Decoupled,
                    }
                }
                : new List<PartEvent>();
        }

        public List<TrajectoryPoint> Points { get; }
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public bool HasOrbitSegments => false;
        public List<TrackSection> TrackSections { get; } = new List<TrackSection>();
        public double StartUT => 0;
        public double EndUT => 5;
        public int RecordingFormatVersion => 0;
        public List<PartEvent> PartEvents { get; }
        public List<FlagEvent> FlagEvents { get; } = new List<FlagEvent>();
        public ConfigNode GhostVisualSnapshot => null;
        public ConfigNode VesselSnapshot => null;
        public string VesselName => "TestReuseVisibility";
        public string RecordingId => "test-b461";
        public bool LoopPlayback => true;
        public double LoopIntervalSeconds => 10;
        public LoopTimeUnit LoopTimeUnit => LoopTimeUnit.Sec;
        public uint LoopAnchorVesselId => 0;
        public double LoopStartUT => double.NaN;
        public double LoopEndUT => double.NaN;
        public TerminalState? TerminalStateValue => null;
        public SurfacePosition? SurfacePos => null;
        public double TerrainHeightAtEnd => double.NaN;
        public bool PlaybackEnabled => true;
        public bool IsDebris => false;
        public int LoopSyncParentIdx { get; set; } = -1;
        public string TerminalOrbitBody => null;
        public double TerminalOrbitSemiMajorAxis => 0;
        public double TerminalOrbitEccentricity => 0;
        public double TerminalOrbitInclination => 0;
        public double TerminalOrbitLAN => 0;
        public double TerminalOrbitArgumentOfPeriapsis => 0;
        public double TerminalOrbitMeanAnomalyAtEpoch => 0;
        public double TerminalOrbitEpoch => 0;
        public RecordingEndpointPhase EndpointPhase => RecordingEndpointPhase.Unknown;
        public string EndpointBodyName => null;
    }

    internal class TestPendingLoopBoundaryTrajectoryForBug539 : IPlaybackTrajectory
    {
        public TestPendingLoopBoundaryTrajectoryForBug539()
        {
            Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 0.0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin",
                },
                new TrajectoryPoint
                {
                    ut = 200.0,
                    latitude = 0.0,
                    longitude = 0.001,
                    altitude = 10.0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin",
                },
            };
        }

        public List<TrajectoryPoint> Points { get; }
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public bool HasOrbitSegments => false;
        public List<TrackSection> TrackSections { get; } = new List<TrackSection>();
        public double StartUT => 100.0;
        public double EndUT => 200.0;
        public int RecordingFormatVersion => 0;
        public List<PartEvent> PartEvents { get; } = new List<PartEvent>();
        public List<FlagEvent> FlagEvents { get; } = new List<FlagEvent>();
        public ConfigNode GhostVisualSnapshot => null;
        public ConfigNode VesselSnapshot => null;
        public string VesselName => "PendingLoopBoundary";
        public string RecordingId => "test-b539";
        public bool LoopPlayback => true;
        public double LoopIntervalSeconds => 150.0;
        public LoopTimeUnit LoopTimeUnit => LoopTimeUnit.Sec;
        public uint LoopAnchorVesselId => 0;
        public double LoopStartUT => double.NaN;
        public double LoopEndUT => double.NaN;
        public TerminalState? TerminalStateValue => null;
        public SurfacePosition? SurfacePos => null;
        public double TerrainHeightAtEnd => double.NaN;
        public bool PlaybackEnabled => true;
        public bool IsDebris => true;
        public int LoopSyncParentIdx { get; set; } = -1;
        public string TerminalOrbitBody => null;
        public double TerminalOrbitSemiMajorAxis => 0.0;
        public double TerminalOrbitEccentricity => 0.0;
        public double TerminalOrbitInclination => 0.0;
        public double TerminalOrbitLAN => 0.0;
        public double TerminalOrbitArgumentOfPeriapsis => 0.0;
        public double TerminalOrbitMeanAnomalyAtEpoch => 0.0;
        public double TerminalOrbitEpoch => 0.0;
        public RecordingEndpointPhase EndpointPhase => RecordingEndpointPhase.Unknown;
        public string EndpointBodyName => null;
    }

    public class IncompleteBallisticRuntimeTests
    {
        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Plane-exit extrapolation keeps producing a ballistic tail until the ghost would despawn")]
        public void ExtrapolationIntegration_PlaneExitMidFlight_GhostFallsAndDespawns()
        {
            const double gravParameter = 3.5316e12;
            const double radius = 600000.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    gravParameter,
                    radius,
                    atmosphereDepth: 70000.0,
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(radius + 3000.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, 250.0, 0.0)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.AreEqual(TerminalState.Destroyed, result.terminalState,
                "low-altitude flying exit should terminate instead of orbit forever");
            InGameAssert.IsTrue(result.terminalUT > start.ut,
                "terminal UT should extend beyond the recorded exit");
            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 0,
                "plane exit should produce at least one extrapolated segment");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Suborbital apoapsis extrapolation keeps the ghost on an orbital arc before termination")]
        public void ExtrapolationIntegration_SuborbitalExitAtApoapsis_GhostFollowsArc()
        {
            const double gravParameter = 3.5316e12;
            const double radius = 600000.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    gravParameter,
                    radius,
                    atmosphereDepth: 70000.0,
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            BallisticStateVector start = MakeApoapsisState(
                "Kerbin",
                gravParameter,
                apoapsisRadius: radius + 70500.0,
                periapsisRadius: radius + 15000.0);

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.AreEqual(TerminalState.Destroyed, result.terminalState,
                "suborbital apoapsis should eventually terminate");
            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 0,
                "suborbital apoapsis should produce at least one extrapolated coast segment");
            InGameAssert.AreEqual("Kerbin", result.segments[0].bodyName,
                "the first extrapolated segment should stay on Kerbin");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Hyperbolic extrapolation crosses the child SOI and continues on the parent body")]
        public void ExtrapolationIntegration_HyperbolicExit_GhostHandsOffToKerbol()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Sun"] = MakeBody("Sun", 1.1723328e18, 261600000.0, sphereOfInfluence: 1.0e30),
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    3.5316e12,
                    600000.0,
                    atmosphereDepth: 0.0,
                    sphereOfInfluence: 10000000.0,
                    parentBodyName: "Sun",
                    parentFrameState: FixedState(Vector3d.zero, new Vector3d(0.0, 9500.0, 0.0)),
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(9500000.0, 0.0, 0.0),
                velocity = new Vector3d(1400.0, 800.0, 0.0)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 1,
                "hyperbolic exit should produce more than one segment");
            InGameAssert.IsTrue(result.segments.Any(seg => seg.bodyName == "Sun"),
                "hyperbolic exit should hand off onto the parent-body frame");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Patched-conic flyby capture yields the same active body the map-view selector would draw")]
        public void PatchedSnapshotIntegration_MunFlybyExit_GhostTrajectoryMatchesMapView()
        {
            var munPatch = MakePatch(200.0, 320.0, "Mun");
            var kerbinPatch = MakePatch(100.0, 200.0, "Kerbin", PatchedConicTransitionType.Encounter);
            kerbinPatch.NextPatch = munPatch;

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = kerbinPatch
            };

            PatchedConicSnapshotResult snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120.0, 8, "MunFlybyRuntime");

            InGameAssert.AreEqual(2, snapshot.Segments.Count,
                "flyby snapshot should capture the encounter and Mun leg");

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                snapshot.Segments,
                250.0,
                out OrbitSegment segment,
                out _,
                out _,
                out _,
                out _,
                out _);

            InGameAssert.IsTrue(visible, "Mun flyby segment should be selectable for map rendering");
            InGameAssert.AreEqual("Mun", segment.bodyName,
                "map selection should follow the captured Mun leg");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Patched-conic capture strips planned maneuver nodes from the ghost trajectory")]
        public void PatchedSnapshotIntegration_ManeuverNodeStripped_GhostIgnoresBurn()
        {
            var maneuverPatch = MakePatch(100.0, 200.0, "Kerbin", PatchedConicTransitionType.Maneuver);
            maneuverPatch.NextPatch = MakePatch(200.0, 260.0, "Mun");

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = maneuverPatch
            };

            PatchedConicSnapshotResult snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120.0, 8, "ManeuverRuntime");

            InGameAssert.AreEqual(1, snapshot.Segments.Count,
                "capture should keep only the pre-maneuver coast patch");
            InGameAssert.IsTrue(snapshot.EncounteredManeuverNode,
                "snapshot should flag that it encountered a UI maneuver node");
            InGameAssert.AreEqual("Kerbin", snapshot.Segments[0].bodyName,
                "post-maneuver bodies should not leak into the captured chain");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "The v1 map selector chooses the predicted/extrapolated segment after the recorded payload ends")]
        public void MapRendering_V1_GhostDrawsLineFromExtrapolatedSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false),
                MakeOrbitSegment(200.0, 500.0, "Kerbin", isPredicted: true)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                350.0,
                out OrbitSegment segment,
                out _,
                out double visibleEndUT,
                out _,
                out _,
                out _);

            InGameAssert.IsTrue(visible, "predicted tail should remain renderable after payload end");
            InGameAssert.IsTrue(segment.isPredicted,
                "map selection should choose the predicted tail segment after payload end");
            InGameAssert.ApproxEqual(500.0, visibleEndUT, 0.001);
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Equivalent same-body tails keep the v1 map line continuous across a recorded-data gap")]
        public void MapRendering_V1_LineContinuesPastRecordedEnd()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false, sma: 700000.0, ecc: 0.01),
                MakeOrbitSegment(300.0, 500.0, "Kerbin", isPredicted: true, sma: 700000.0, ecc: 0.01)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                250.0,
                out OrbitSegment segment,
                out double visibleStartUT,
                out double visibleEndUT,
                out _,
                out _,
                out bool carriedAcrossGap);

            InGameAssert.IsTrue(visible, "equivalent predicted tail should bridge the recorded-data gap");
            InGameAssert.IsTrue(carriedAcrossGap,
                "equivalent predicted tail should be carried across the gap");
            InGameAssert.AreEqual("Kerbin", segment.bodyName,
                "gap-carry should keep the active body on the same SOI");
            InGameAssert.ApproxEqual(100.0, visibleStartUT, 0.001);
            InGameAssert.ApproxEqual(500.0, visibleEndUT, 0.001);
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "The v1 map selector does not bridge across foreign-SOI segments")]
        public void MapRendering_V1_ForeignSOISegmentsNotRendered()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false, sma: 700000.0, ecc: 0.01),
                MakeOrbitSegment(300.0, 500.0, "Mun", isPredicted: true, sma: 250000.0, ecc: 0.01)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                250.0,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);

            InGameAssert.IsFalse(visible,
                "foreign-SOI segments should not render through a gap in the v1 selector");
        }

        private static BallisticStateVector MakeApoapsisState(
            string bodyName,
            double gravParameter,
            double apoapsisRadius,
            double periapsisRadius)
        {
            double semiMajorAxis = (apoapsisRadius + periapsisRadius) * 0.5;
            double tangentialSpeed = System.Math.Sqrt(
                gravParameter * ((2.0 / apoapsisRadius) - (1.0 / semiMajorAxis)));
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = bodyName,
                position = new Vector3d(apoapsisRadius, 0.0, 0.0),
                velocity = new Vector3d(0.0, tangentialSpeed, 0.0)
            };
        }

        private static ExtrapolationBody MakeBody(
            string name,
            double gravParameter,
            double radius,
            double atmosphereDepth = 0.0,
            double sphereOfInfluence = 0.0,
            string parentBodyName = null,
            TerrainAltitudeResolver terrainAltitude = null,
            ParentFrameStateResolver parentFrameState = null,
            SurfaceCoordinatesResolver surfaceCoordinates = null)
        {
            return new ExtrapolationBody
            {
                Name = name,
                ParentBodyName = parentBodyName,
                GravitationalParameter = gravParameter,
                Radius = radius,
                AtmosphereDepth = atmosphereDepth,
                SphereOfInfluence = sphereOfInfluence,
                TerrainAltitude = terrainAltitude,
                ParentFrameState = parentFrameState,
                SurfaceCoordinates = surfaceCoordinates
            };
        }

        private static OrbitSegment MakeOrbitSegment(
            double startUT,
            double endUT,
            string bodyName,
            bool isPredicted,
            double sma = 700000.0,
            double ecc = 0.01)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                isPredicted = isPredicted
            };
        }

        private static ParentFrameStateResolver FixedState(Vector3d position, Vector3d velocity)
        {
            return (double ut, out Vector3d bodyPosition, out Vector3d bodyVelocity) =>
            {
                bodyPosition = position;
                bodyVelocity = velocity;
            };
        }

        private static bool FlatTerrain(double latitude, double longitude, out double altitude)
        {
            altitude = 0.0;
            return true;
        }

        private static void ZeroSurfaceCoordinates(
            double ut,
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            latitude = 0.0;
            longitude = 0.0;
        }

        private static FakePatchedConicOrbitPatch MakePatch(
            double startUT,
            double endUT,
            string bodyName,
            PatchedConicTransitionType transition = PatchedConicTransitionType.Final)
        {
            return new FakePatchedConicOrbitPatch
            {
                StartUT = startUT,
                EndUT = endUT,
                BodyName = bodyName,
                Inclination = 0.0,
                Eccentricity = 0.01,
                SemiMajorAxis = 700000.0,
                LongitudeOfAscendingNode = 0.0,
                ArgumentOfPeriapsis = 0.0,
                MeanAnomalyAtEpoch = 0.0,
                Epoch = startUT,
                EndTransition = transition
            };
        }

        private sealed class FakePatchedConicSnapshotSource : IPatchedConicSnapshotSource
        {
            private int patchLimit;

            public FakePatchedConicSnapshotSource(int initialPatchLimit)
            {
                patchLimit = initialPatchLimit;
            }

            public string VesselName => "Runtime Fake Vessel";
            public bool IsAvailable { get; set; } = true;
            public bool HasPatchLimitAccess { get; set; } = true;
            public IPatchedConicOrbitPatch RootPatch { get; set; }

            public int PatchLimit
            {
                get => patchLimit;
                set => patchLimit = value;
            }

            public void Update() { }
        }

        private sealed class FakePatchedConicOrbitPatch : IPatchedConicOrbitPatch
        {
            public double StartUT { get; set; }
            public double EndUT { get; set; }
            public double Inclination { get; set; }
            public double Eccentricity { get; set; }
            public double SemiMajorAxis { get; set; }
            public double LongitudeOfAscendingNode { get; set; }
            public double ArgumentOfPeriapsis { get; set; }
            public double MeanAnomalyAtEpoch { get; set; }
            public double Epoch { get; set; }
            public string BodyName { get; set; }
            public PatchedConicTransitionType EndTransition { get; set; }
            public IPatchedConicOrbitPatch NextPatch { get; set; }
        }
    }
}
