using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the four units extracted from <see cref="FlightRecorder.OnPhysicsFrame(Vessel)"/>'s
    /// sampling-cadence decision: the high-fidelity proximity source, the active Re-Fly cadence note,
    /// the effective sample intervals and the sample-skipped line. The host itself cannot reach this
    /// decision headless (it reads <c>v.transform</c> first), so these pin each unit's branches, log text
    /// and rate-limit keys.
    /// </summary>
    [Collection("Sequential")]
    public class PhysicsFrameSamplingHelperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public PhysicsFrameSamplingHelperTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // A fixed clock: the rate-limit cells must not depend on how long a loaded host takes.
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = savedCulture;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static void SetField(FlightRecorder r, string name, object value)
        {
            FieldInfo f = typeof(FlightRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f.SetValue(r, value);
        }

        private static float ConfiguredInterval(string name)
        {
            PropertyInfo p = typeof(FlightRecorder).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(p);
            return (float)p.GetValue(null);
        }

        private static List<string> RateLimitKeys()
        {
            var prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add((string)k);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        // Only the recorder's own lines: the first verbose check of a run can also log the
        // settings provider install, which is not this unit's output.
        private List<string> RecorderLines()
            => logLines.FindAll(l => l.Contains("[Recorder]"));

        private static string F(double value, string format)
            => value.ToString(format, CultureInfo.InvariantCulture);

        // ---------- ResolveHighFidelityProximitySource ----------------------------

        [Theory]
        [InlineData(false, double.NaN, double.NaN, "(none)")]
        [InlineData(false, double.NaN, 10.0, "split-child")]
        [InlineData(false, 5.0, 10.0, "split-child")]
        [InlineData(false, double.NaN, double.PositiveInfinity, "(none)")]
        [InlineData(true, double.NaN, 10.0, "split-child")]
        [InlineData(true, double.PositiveInfinity, 10.0, "split-child")]
        [InlineData(true, 20.0, 10.0, "split-child")]
        [InlineData(true, 10.0, 10.0, "split-child")]
        [InlineData(true, 5.0, 10.0, "relative-anchor")]
        [InlineData(true, 5.0, double.NaN, "relative-anchor")]
        [InlineData(true, double.NaN, double.NaN, "relative-anchor")]
        [InlineData(true, 5.0, double.NegativeInfinity, "relative-anchor")]
        public void ResolveHighFidelityProximitySource_TruthTable(
            bool relativeMode, double anchorDistance, double splitChildDistance, string expected)
        {
            var recorder = new FlightRecorder();
            SetField(recorder, "isRelativeMode", relativeMode);
            SetField(recorder, "currentAnchorDistanceMeters", anchorDistance);

            Assert.Equal(expected, recorder.ResolveHighFidelityProximitySource(splitChildDistance, "split-child"));
            Assert.Empty(RecorderLines());
        }

        [Fact]
        public void ResolveHighFidelityProximitySource_PassesANullSplitChildSourceThrough()
        {
            var recorder = new FlightRecorder();
            Assert.Null(recorder.ResolveHighFidelityProximitySource(3.0, null));
        }

        // ---------- NoteActiveReFlyTreeSamplingCadence ----------------------------

        [Fact]
        public void NoteActiveReFlyTreeSamplingCadence_ForcesHighFidelityAndLogsTheCadence()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ro-RO");
            var recorder = new FlightRecorder();
            recorder.ActiveTree = new RecordingTree { Id = "tree-1", ActiveRecordingId = "rec-1" };
            SetField(recorder, "reFlyTreeSamplingProximityMeters", 123.45);
            SetField(recorder, "reFlyTreeSamplingProximitySource", "live-anchor");
            bool highFidelityActive = false;

            recorder.NoteActiveReFlyTreeSamplingCadence(
                ref highFidelityActive, ProximitySamplingTier.Full, "tree-member-near");

            Assert.True(highFidelityActive);
            string expected =
                "Active Re-Fly tree sampling cadence active: recordingId=rec-1 treeId=tree-1 cadence=Full " +
                "reason=tree-member-near proximity=123.5m proximitySource=live-anchor " +
                "fullRange=250.0m halfRange=500.0m " +
                "configuredMin=" + F(ConfiguredInterval("minSampleInterval"), "F3") + "s " +
                "configuredMax=" + F(ConfiguredInterval("maxSampleInterval"), "F3") + "s";
            Assert.Single(RecorderLines());
            Assert.Contains("[Recorder]", RecorderLines()[0]);
            Assert.EndsWith(expected, RecorderLines()[0]);
            Assert.Equal(new List<string> { "Recorder|active-refly-sampling|rec-1" }, RateLimitKeys());
        }

        [Fact]
        public void NoteActiveReFlyTreeSamplingCadence_NoTree_UsesThePlaceholders()
        {
            var recorder = new FlightRecorder();
            bool highFidelityActive = true;

            recorder.NoteActiveReFlyTreeSamplingCadence(ref highFidelityActive, ProximitySamplingTier.Half, null);

            Assert.True(highFidelityActive);
            Assert.Single(RecorderLines());
            Assert.Contains(
                "Active Re-Fly tree sampling cadence active: recordingId=(none) treeId=(none) cadence=Half " +
                "reason=(none) proximity=NaNm proximitySource=(none) ",
                RecorderLines()[0]);
            Assert.Equal(new List<string> { "Recorder|active-refly-sampling|<no-rec>" }, RateLimitKeys());
        }

        [Fact]
        public void NoteActiveReFlyTreeSamplingCadence_IsRateLimitedPerRecording()
        {
            var recorder = new FlightRecorder();
            recorder.ActiveTree = new RecordingTree { Id = "tree-1", ActiveRecordingId = "rec-1" };
            bool highFidelityActive = false;

            recorder.NoteActiveReFlyTreeSamplingCadence(ref highFidelityActive, ProximitySamplingTier.Full, "a");
            highFidelityActive = false;
            recorder.NoteActiveReFlyTreeSamplingCadence(ref highFidelityActive, ProximitySamplingTier.Full, "a");

            // The flag is forced on every call; only the line is rate-limited.
            Assert.True(highFidelityActive);
            Assert.Single(RecorderLines());

            recorder.ActiveTree.ActiveRecordingId = "rec-2";
            recorder.NoteActiveReFlyTreeSamplingCadence(ref highFidelityActive, ProximitySamplingTier.Full, "a");
            Assert.Equal(2, RecorderLines().Count);
            Assert.Contains("recordingId=rec-2", RecorderLines()[1]);
        }

        // ---------- ResolveEffectiveSampleIntervals -------------------------------

        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        public void ResolveEffectiveSampleIntervals_MatchesTheTwoResolversAtTheConfiguredIntervals(
            int tierValue, bool highFidelityActive)
        {
            var tier = (ProximitySamplingTier)tierValue;
            foreach (SamplingDensity density in new[] { SamplingDensity.Low, SamplingDensity.Medium, SamplingDensity.High })
            {
                ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { SamplingDensityLevel = density };
                float configuredMin = ParsekSettings.GetMinSampleInterval(density);
                float configuredMax = ParsekSettings.GetMaxSampleInterval(density);

                FlightRecorder.ResolveEffectiveSampleIntervals(
                    tier, highFidelityActive, out float min, out float max);

                Assert.Equal(FlightRecorder.ResolveEffectiveMinSampleInterval(
                    tier, ProximitySamplingTier.None, highFidelityActive, configuredMin, configuredMax), min);
                Assert.Equal(FlightRecorder.ResolveEffectiveMaxSampleInterval(
                    tier, ProximitySamplingTier.None, highFidelityActive, configuredMax, configuredMin), max);
            }
            Assert.Empty(RecorderLines());
        }

        [Fact]
        public void ResolveEffectiveSampleIntervals_NoSettings_UsesTheMediumDefaults()
        {
            FlightRecorder.ResolveEffectiveSampleIntervals(
                ProximitySamplingTier.None, false, out float min, out float max);

            Assert.Equal(ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium), min);
            Assert.Equal(ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium), max);
        }

        // ---------- LogPhysicsSampleSkipped ---------------------------------------

        [Fact]
        public void LogPhysicsSampleSkipped_WritesTheWaitingLineInvariantly()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ro-RO");

            FlightRecorder.LogPhysicsSampleSkipped(
                1234.5678, true, "joint-break", 12.345, "split-child", 0.25f, 3f);

            Assert.Single(RecorderLines());
            Assert.Contains("[Recorder]", RecorderLines()[0]);
            Assert.EndsWith(
                "Sample skipped at ut=1234.57; waiting for motion/attitude trigger " +
                "highFidelity=True reason=joint-break proximity=12.3m proximitySource=split-child " +
                "minInterval=0.250s maxInterval=3.000s",
                RecorderLines()[0]);
            Assert.Equal(new List<string> { "Recorder|sample-skipped" }, RateLimitKeys());
        }

        [Fact]
        public void LogPhysicsSampleSkipped_NaNProximityAndNullReason_ReadNone()
        {
            FlightRecorder.LogPhysicsSampleSkipped(
                10.0, false, null, double.NaN, "(none)", 0.1f, 2f);

            Assert.Single(RecorderLines());
            Assert.Contains(
                "highFidelity=False reason=(none) proximity=(none) proximitySource=(none) ",
                RecorderLines()[0]);
        }

        [Fact]
        public void LogPhysicsSampleSkipped_IsRateLimitedOnOneSharedKey()
        {
            FlightRecorder.LogPhysicsSampleSkipped(10.0, false, null, double.NaN, "(none)", 0.1f, 2f);
            FlightRecorder.LogPhysicsSampleSkipped(11.0, true, "x", 1.0, "relative-anchor", 0.1f, 2f);

            Assert.Single(RecorderLines());
            Assert.Equal(new List<string> { "Recorder|sample-skipped" }, RateLimitKeys());
        }
    }
}
