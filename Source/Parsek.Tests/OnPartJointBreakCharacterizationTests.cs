using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>FlightRecorder.OnPartJointBreak(PartJoint, float)</c> as far as a headless
    /// host reaches it. Past the vessel-identity gate the method builds its diagnostics line through
    /// <c>part.transform</c> (a Unity native call), so the structural / bookkeeping half is pinned by the
    /// extracted units' own tests (<c>JointBreakHelperTests</c>) and by the normalized-IL block equality
    /// in the extraction PR. What is reachable is the guard prologue, pinned here: not recording, a null
    /// joint / child / vessel (Unity null included: a vessel whose native object is gone), and a joint
    /// on another vessel. The fakes are uninitialized Unity objects; a nonzero cached pointer makes one
    /// read as alive to Unity's null comparison.
    /// </summary>
    [Collection("Sequential")]
    public class OnPartJointBreakCharacterizationTests : IDisposable
    {
        private const uint RecordedPid = 4242u;
        private readonly List<string> logLines = new List<string>();

        public OnPartJointBreakCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ParsekLog.ResetRateLimitsForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        internal static T Fake<T>(bool alive = true) where T : UnityEngine.Object
        {
            var o = (T)FormatterServices.GetUninitializedObject(typeof(T));
            if (alive)
            {
                FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                    "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(cachedPtr);
                cachedPtr.SetValue(o, new IntPtr(1));
            }
            return o;
        }

        internal static PartJoint FakeJoint(Part child)
        {
            PartJoint joint = Fake<PartJoint>();
            FieldInfo f = typeof(PartJoint).GetField("child", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f.SetValue(joint, child);
            return joint;
        }

        internal static Part FakePart(uint pid, Vessel vessel)
        {
            Part part = Fake<Part>();
            part.persistentId = pid;
            part.vessel = vessel;
            return part;
        }

        internal static Vessel FakeVessel(uint pid, bool alive = true)
        {
            Vessel v = Fake<Vessel>(alive);
            v.persistentId = pid;
            return v;
        }

        internal static FlightRecorder RecordingRecorder()
        {
            var recorder = new FlightRecorder();
            typeof(FlightRecorder).GetProperty("RecordingVesselId").SetValue(recorder, RecordedPid);
            recorder.IsRecording = true;
            return recorder;
        }

        private static void Invoke(FlightRecorder recorder, PartJoint joint, float breakForce)
        {
            MethodInfo m = typeof(FlightRecorder).GetMethod(
                "OnPartJointBreak", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m);
            m.Invoke(recorder, new object[] { joint, breakForce });
        }

        private static object Field(FlightRecorder r, string name)
        {
            FieldInfo f = typeof(FlightRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            return f.GetValue(r);
        }

        private static int Count(object collection)
            => (int)collection.GetType().GetProperty("Count").GetValue(collection);

        internal static string StateDigest(FlightRecorder r)
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join("|",
                r.IsRecording ? "rec" : "idle",
                r.PartEvents.Count.ToString(ic),
                r.Recording.Count.ToString(ic),
                Count(Field(r, "decoupledPartIds")).ToString(ic),
                Count(Field(r, "forceBrokenJointChildPartIds")).ToString(ic),
                Count(Field(r, "pendingJointChildPartOriginSeeds")).ToString(ic),
                r.HasPendingJointBreakCheck ? "pending" : "no-pending",
                r.PendingJointBreakUT.ToString("R", ic),
                ((double)Field(r, "highFidelitySamplingUntilUT")).ToString("R", ic),
                (string)Field(r, "highFidelitySamplingReason") ?? "<null>");
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

        private List<string> RecorderLines() => logLines.FindAll(l => l.Contains("[Recorder]"));

        private const string NullLine = "OnPartJointBreak: joint, child, or vessel is null";

        [UnityEntryFact]
        public void NotRecording_ReturnsBeforeAnyRead()
        {
            var recorder = new FlightRecorder();
            string before = StateDigest(recorder);

            Invoke(recorder, null, 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Empty(logLines);
        }

        [UnityEntryFact]
        public void NullJoint_LogsTheNullLineAndChangesNothing()
        {
            FlightRecorder recorder = RecordingRecorder();
            string before = StateDigest(recorder);

            Invoke(recorder, null, 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Single(RecorderLines());
            Assert.EndsWith(NullLine, RecorderLines()[0]);
            Assert.Equal(new List<string> { "Recorder|joint-break-null" }, RateLimitKeys());
        }

        [UnityEntryFact]
        public void NullChild_LogsTheNullLine()
        {
            FlightRecorder recorder = RecordingRecorder();
            string before = StateDigest(recorder);

            Invoke(recorder, FakeJoint(null), 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Single(RecorderLines());
            Assert.EndsWith(NullLine, RecorderLines()[0]);
        }

        [UnityEntryFact]
        public void ChildWithNoVessel_LogsTheNullLine()
        {
            FlightRecorder recorder = RecordingRecorder();
            string before = StateDigest(recorder);

            Invoke(recorder, FakeJoint(FakePart(77u, null)), 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Single(RecorderLines());
            Assert.EndsWith(NullLine, RecorderLines()[0]);
        }

        [UnityEntryFact]
        public void ChildWithADestroyedVessel_ReadsAsUnityNull()
        {
            FlightRecorder recorder = RecordingRecorder();
            string before = StateDigest(recorder);

            // The recorded pid, but the native object is gone: Unity's == null says null.
            Invoke(recorder, FakeJoint(FakePart(77u, FakeVessel(RecordedPid, alive: false))), 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Single(RecorderLines());
            Assert.EndsWith(NullLine, RecorderLines()[0]);
        }

        [UnityEntryFact]
        public void NullLine_IsRateLimitedOnOneSharedKey()
        {
            FlightRecorder recorder = RecordingRecorder();

            Invoke(recorder, null, 5f);
            Invoke(recorder, FakeJoint(null), 5f);

            Assert.Single(RecorderLines());
        }

        [UnityEntryFact]
        public void JointOnAnotherVessel_ReturnsSilently()
        {
            FlightRecorder recorder = RecordingRecorder();
            string before = StateDigest(recorder);

            Invoke(recorder, FakeJoint(FakePart(77u, FakeVessel(9999u))), 5f);

            Assert.Equal(before, StateDigest(recorder));
            Assert.Empty(RecorderLines());
            Assert.Empty(RateLimitKeys());
        }
    }
}
