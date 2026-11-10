using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units extracted from <c>FlightRecorder.OnPartJointBreak</c>'s structural
    /// half: the decouple bookkeeping, the Decoupled event with its high-fidelity window, the
    /// structural-event snapshot hand-off and the deferred split-check arm. The host cannot reach
    /// them headless (its diagnostics line reads <c>part.transform</c> first). The Trace-Sep lines,
    /// the diagnostics line and the part-origin seed capture are live Unity reads throughout and are
    /// proven by the normalized-IL block equality only.
    /// </summary>
    [Collection("Sequential")]
    public class JointBreakHelperTests : IDisposable
    {
        private const uint RecordedPid = 4242u;
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public JointBreakHelperTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
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

        private static object Field(FlightRecorder r, string name)
        {
            FieldInfo f = typeof(FlightRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            return f.GetValue(r);
        }

        private static void SetJointPart(PartJoint joint, string field, Part part)
        {
            FieldInfo f = typeof(PartJoint).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f.SetValue(joint, part);
        }

        private static AvailablePart PartInfo(string name)
        {
            var info = (AvailablePart)FormatterServices.GetUninitializedObject(typeof(AvailablePart));
            info.name = name;
            return info;
        }

        private List<string> Lines(string subsystem) => logLines.FindAll(l => l.Contains("[" + subsystem + "]"));

        private static PartJoint Joint(uint childPid, Vessel childVessel, AvailablePart info = null)
        {
            Part child = OnPartJointBreakCharacterizationTests.FakePart(childPid, childVessel);
            child.partInfo = info;
            return OnPartJointBreakCharacterizationTests.FakeJoint(child);
        }

        // ---------- MarkJointChildDecoupled --------------------------------------

        [Fact]
        public void MarkJointChildDecoupled_ZeroForce_MarksDecoupledOnly()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();

            recorder.MarkJointChildDecoupled(Joint(77u, null), 0f);

            Assert.Contains(77u, (HashSet<uint>)Field(recorder, "decoupledPartIds"));
            Assert.Empty((HashSet<uint>)Field(recorder, "forceBrokenJointChildPartIds"));
            Assert.Empty(Lines("Recorder"));
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        public void MarkJointChildDecoupled_NonPositiveForce_IsNotBrokenOff(float breakForce)
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();

            recorder.MarkJointChildDecoupled(Joint(77u, null), breakForce);

            Assert.Contains(77u, (HashSet<uint>)Field(recorder, "decoupledPartIds"));
            Assert.Empty((HashSet<uint>)Field(recorder, "forceBrokenJointChildPartIds"));
        }

        [Fact]
        public void MarkJointChildDecoupled_PositiveForce_MarksBrokenOffAndLogsInvariantly()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ro-RO");
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();

            recorder.MarkJointChildDecoupled(Joint(77u, null), 12.46f);

            Assert.Contains(77u, (HashSet<uint>)Field(recorder, "decoupledPartIds"));
            Assert.Contains(77u, (HashSet<uint>)Field(recorder, "forceBrokenJointChildPartIds"));
            Assert.Single(Lines("Recorder"));
            Assert.EndsWith(
                "OnPartJointBreak: joint broke under force pid=77 breakForce=12.5 => split cause BROKE_OFF",
                Lines("Recorder")[0]);
        }

        // ---------- RecordJointBreakDecoupledEvent -------------------------------

        [Fact]
        public void RecordJointBreakDecoupledEvent_AddsTheEventAndOpensTheWindow()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ro-RO");
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();

            recorder.RecordJointBreakDecoupledEvent(Joint(77u, null, PartInfo("radialDecoupler")), 100.25);

            PartEvent ev = Assert.Single(recorder.PartEvents);
            Assert.Equal(100.25, ev.ut);
            Assert.Equal(77u, ev.partPersistentId);
            Assert.Equal(PartEventType.Decoupled, ev.eventType);
            Assert.Equal("radialDecoupler", ev.partName);
            Assert.Equal("joint-break", (string)Field(recorder, "highFidelitySamplingReason"));
            Assert.True((double)Field(recorder, "highFidelitySamplingUntilUT") > 100.25);

            List<string> lines = Lines("Recorder");
            Assert.Equal(2, lines.Count);
            Assert.Contains("High-fidelity sampling window active: reason=joint-break eventUT=100.25 ", lines[0]);
            Assert.EndsWith("Part event: Decoupled 'radialDecoupler' pid=77", lines[1]);
        }

        [Fact]
        public void RecordJointBreakDecoupledEvent_NoPartInfo_NamesTheEventUnknown()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();

            recorder.RecordJointBreakDecoupledEvent(Joint(77u, null), 100.0);

            Assert.Equal("unknown", Assert.Single(recorder.PartEvents).partName);
            Assert.EndsWith("Part event: Decoupled '' pid=77", Lines("Recorder")[1]);
        }

        // ---------- AppendJointBreakStructuralSnapshot ---------------------------
        // The vessels here are on another pid, so the snapshot considers them and samples none;
        // its "no involved vessel matched" line reports how many the joint break handed it.

        [UnityEntryFact]
        public void AppendJointBreakStructuralSnapshot_ChildVesselOnly_WhenTheHostSharesIt()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();
            Vessel vessel = OnPartJointBreakCharacterizationTests.FakeVessel(9999u);
            PartJoint joint = Joint(77u, vessel);
            SetJointPart(joint, "host", OnPartJointBreakCharacterizationTests.FakePart(78u, vessel));

            recorder.AppendJointBreakStructuralSnapshot(joint, 100.0);

            Assert.Contains(Lines("Pipeline-Smoothing"), l => l.Contains(
                "structural event snapshot: no involved vessel matched RecordingVesselId=4242 " +
                "(eventType=JointBreak ut=100.00 considered=1)"));
            Assert.Empty(recorder.Recording);
        }

        [UnityEntryFact]
        public void AppendJointBreakStructuralSnapshot_HandsOverADistinctHostVessel()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();
            PartJoint joint = Joint(77u, OnPartJointBreakCharacterizationTests.FakeVessel(9999u));
            SetJointPart(joint, "host", OnPartJointBreakCharacterizationTests.FakePart(
                78u, OnPartJointBreakCharacterizationTests.FakeVessel(9998u)));

            recorder.AppendJointBreakStructuralSnapshot(joint, 100.0);

            Assert.Contains(Lines("Pipeline-Smoothing"), l => l.Contains("considered=2)"));
        }

        [UnityEntryFact]
        public void AppendJointBreakStructuralSnapshot_NoHost_HandsOverTheChildVessel()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();
            PartJoint joint = Joint(77u, OnPartJointBreakCharacterizationTests.FakeVessel(9999u));

            recorder.AppendJointBreakStructuralSnapshot(joint, 100.0);

            Assert.Contains(Lines("Pipeline-Smoothing"), l => l.Contains("considered=1)"));
            Assert.Equal("structural-event-JointBreak", (string)Field(recorder, "highFidelitySamplingReason"));
        }

        [UnityEntryFact]
        public void AppendJointBreakStructuralSnapshot_DestroyedChildVessel_HandsOverNothing()
        {
            FlightRecorder recorder = OnPartJointBreakCharacterizationTests.RecordingRecorder();
            PartJoint joint = Joint(77u, OnPartJointBreakCharacterizationTests.FakeVessel(9999u, alive: false));

            recorder.AppendJointBreakStructuralSnapshot(joint, 100.0);

            // An empty list: no vessel considered, so no "no involved vessel matched" line.
            Assert.DoesNotContain(Lines("Pipeline-Smoothing"), l => l.Contains("considered="));
            Assert.Equal("structural-event-JointBreak", (string)Field(recorder, "highFidelitySamplingReason"));
        }

        // ---------- NotePendingJointBreakCheck -----------------------------------

        [Fact]
        public void NotePendingJointBreakCheck_ArmsAndKeepsTheEarliestUT()
        {
            var recorder = new FlightRecorder();
            Assert.False(recorder.HasPendingJointBreakCheck);

            recorder.NotePendingJointBreakCheck(100.0);
            Assert.True(recorder.HasPendingJointBreakCheck);
            Assert.Equal(100.0, recorder.PendingJointBreakUT);

            recorder.NotePendingJointBreakCheck(105.0);
            Assert.Equal(100.0, recorder.PendingJointBreakUT);

            recorder.NotePendingJointBreakCheck(99.5);
            Assert.Equal(99.5, recorder.PendingJointBreakUT);
            Assert.True(recorder.HasPendingJointBreakCheck);
            Assert.Empty(logLines);
        }

        [Fact]
        public void NotePendingJointBreakCheck_ReplacesANaNPendingUT()
        {
            var recorder = new FlightRecorder();
            typeof(FlightRecorder).GetProperty("HasPendingJointBreakCheck").SetValue(recorder, true);
            Assert.True(double.IsNaN(recorder.PendingJointBreakUT));

            recorder.NotePendingJointBreakCheck(120.0);

            Assert.Equal(120.0, recorder.PendingJointBreakUT);
        }

        [Fact]
        public void NotePendingJointBreakCheck_NotArmed_TakesALaterUT()
        {
            var recorder = new FlightRecorder();
            typeof(FlightRecorder).GetProperty("PendingJointBreakUT").SetValue(recorder, 50.0);

            recorder.NotePendingJointBreakCheck(120.0);

            Assert.True(recorder.HasPendingJointBreakCheck);
            Assert.Equal(120.0, recorder.PendingJointBreakUT);
        }
    }
}
