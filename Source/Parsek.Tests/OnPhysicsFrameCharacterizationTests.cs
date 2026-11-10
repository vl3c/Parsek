using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="FlightRecorder.OnPhysicsFrame(Vessel)"/> as far as a headless
    /// host reaches it. The method reads <c>v.transform.rotation</c> (a Unity native call) and polls
    /// the live vessel (<c>PollPartStates</c>, the boundary checks) before its sampling-cadence
    /// decision, so no test reaches that decision through the method itself: the extracted cadence
    /// units are pinned by their own tests (<c>PhysicsFrameSamplingHelperTests</c>) and by the
    /// normalized-IL block equality in the extraction PR. What is reachable is the pair of entry
    /// gates, pinned here: a recorder that is not recording, or is on rails, returns before it reads
    /// the vessel, logs nothing and changes no state.
    /// </summary>
    [Collection("Sequential")]
    public class OnPhysicsFrameCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OnPhysicsFrameCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
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

        private static string StateDigest(FlightRecorder r)
        {
            return string.Join("|",
                r.IsRecording ? "rec" : "idle",
                r.Recording.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.TrackSections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.PartEvents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.OrbitSegments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.RecordingVesselId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [UnityEntryFact]
        public void NotRecording_ReturnsBeforeReadingTheVessel()
        {
            var recorder = new FlightRecorder();
            typeof(FlightRecorder).GetProperty("RecordingVesselId").SetValue(recorder, 4242u);
            string before = StateDigest(recorder);

            // A null vessel proves the gate runs before the first vessel read.
            recorder.OnPhysicsFrame(null);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Empty(logLines);
        }

        [UnityEntryFact]
        public void OnRails_ReturnsBeforeReadingTheVessel()
        {
            var recorder = new FlightRecorder();
            typeof(FlightRecorder).GetProperty("RecordingVesselId").SetValue(recorder, 4242u);
            recorder.IsRecording = true;
            SetField(recorder, "isOnRails", true);
            string before = StateDigest(recorder);

            recorder.OnPhysicsFrame(null);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Empty(logLines);
        }
    }
}
