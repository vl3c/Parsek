using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ParsekFlight.CopyRewindSaveToRoot — ensures rewind save filename
    /// and reserved budget are copied from CaptureAtStop to the tree root recording.
    /// Bug T59: EVA branch recordings lost the rewind save because the EVA recorder
    /// never captures one; the parent's CaptureAtStop is the only source.
    /// </summary>
    [Collection("Sequential")]
    public class CopyRewindSaveToRootTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CopyRewindSaveToRootTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree MakeTree(string rootRewindSave = null,
            double rootRewindFunds = 0, double rootRewindSci = 0, float rootRewindRep = 0)
        {
            string treeId = Guid.NewGuid().ToString("N");
            string rootId = Guid.NewGuid().ToString("N");
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "TestTree",
                RootRecordingId = rootId
            };
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Root",
                RewindSaveFileName = rootRewindSave,
                RewindReservedFunds = rootRewindFunds,
                RewindReservedScience = rootRewindSci,
                RewindReservedRep = rootRewindRep
            };
            return tree;
        }

        private static Recording MakeCaptureAtStop(string rewindSave,
            double funds = 1000, double sci = 50, float rep = 25,
            double preFunds = 500, double preSci = 20, float preRep = 10)
        {
            return new Recording
            {
                RecordingId = Guid.NewGuid().ToString("N"),
                RewindSaveFileName = rewindSave,
                RewindReservedFunds = funds,
                RewindReservedScience = sci,
                RewindReservedRep = rep,
                PreLaunchFunds = preFunds,
                PreLaunchScience = preSci,
                PreLaunchReputation = preRep
            };
        }

        private static void SetRecorderProperty<T>(FlightRecorder recorder, string propertyName, T value)
        {
            var prop = typeof(FlightRecorder).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(prop);

            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.NotNull(setter);
            setter.Invoke(recorder, new object[] { value });
        }

        [Fact]
        public void CopiesRewindSave_FromCaptureAtStop_ToEmptyRoot()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop("parsek_rw_test123");

            ParsekFlight.CopyRewindSaveToRoot(tree, capture, logTag: "Test");

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_test123", root.RewindSaveFileName);
        }

        [Fact]
        public void CopiesReservedBudget_FromCaptureAtStop_ToEmptyRoot()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop("parsek_rw_x", funds: 2000, sci: 100, rep: 50);

            ParsekFlight.CopyRewindSaveToRoot(tree, capture);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal(2000, root.RewindReservedFunds);
            Assert.Equal(100, root.RewindReservedScience);
            Assert.Equal(50, root.RewindReservedRep);
        }

        [Fact]
        public void CopiesPreLaunchBudget_FromCaptureAtStop_ToEmptyRoot()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop("parsek_rw_x", preFunds: 800, preSci: 30, preRep: 15);

            ParsekFlight.CopyRewindSaveToRoot(tree, capture);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal(800, root.PreLaunchFunds);
            Assert.Equal(30, root.PreLaunchScience);
            Assert.Equal(15, root.PreLaunchReputation);
        }

        [Fact]
        public void DoesNotOverwrite_ExistingRootRewindSave()
        {
            var tree = MakeTree(rootRewindSave: "parsek_rw_original");
            var capture = MakeCaptureAtStop("parsek_rw_new");

            ParsekFlight.CopyRewindSaveToRoot(tree, capture);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_original", root.RewindSaveFileName);
        }

        [Fact]
        public void DoesNotOverwrite_ExistingReservedBudget()
        {
            var tree = MakeTree(rootRewindFunds: 999, rootRewindSci: 88, rootRewindRep: 7);
            var capture = MakeCaptureAtStop("parsek_rw_x", funds: 2000, sci: 100, rep: 50);

            // Root already has a rewind save set via the tree builder — set it explicitly
            tree.Recordings[tree.RootRecordingId].RewindSaveFileName = "parsek_rw_existing";
            ParsekFlight.CopyRewindSaveToRoot(tree, capture);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal(999, root.RewindReservedFunds);
            Assert.Equal(88, root.RewindReservedScience);
            Assert.Equal(7, root.RewindReservedRep);
        }

        [Fact]
        public void UsesRecorderFallback_WhenCaptureAtStopIsNull()
        {
            var tree = MakeTree();

            ParsekFlight.CopyRewindSaveToRoot(tree, captureAtStop: null,
                recorderFallbackSave: "parsek_rw_fallback");

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_fallback", root.RewindSaveFileName);
        }

        [Fact]
        public void UsesRecorderFallbackBudget_WhenCaptureAtStopIsNull()
        {
            var tree = MakeTree();

            ParsekFlight.CopyRewindSaveToRoot(
                tree,
                captureAtStop: null,
                recorderFallbackSave: "parsek_rw_fallback",
                recorderFallbackReservedFunds: 1200,
                recorderFallbackReservedScience: 45,
                recorderFallbackReservedRep: 7,
                recorderFallbackPreLaunchFunds: 9000,
                recorderFallbackPreLaunchScience: 123,
                recorderFallbackPreLaunchRep: 55);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal(1200, root.RewindReservedFunds);
            Assert.Equal(45, root.RewindReservedScience);
            Assert.Equal(7, root.RewindReservedRep);
            Assert.Equal(9000, root.PreLaunchFunds);
            Assert.Equal(123, root.PreLaunchScience);
            Assert.Equal(55, root.PreLaunchReputation);
        }

        [Fact]
        public void RecorderOverload_UsesFallbackBudget_WhenCaptureAtStopIsNull()
        {
            var tree = MakeTree();
            var recorder = new FlightRecorder();

            SetRecorderProperty(recorder, nameof(FlightRecorder.RewindSaveFileName), "parsek_rw_fallback");
            SetRecorderProperty(recorder, nameof(FlightRecorder.RewindReservedFunds), 1200d);
            SetRecorderProperty(recorder, nameof(FlightRecorder.RewindReservedScience), 45d);
            SetRecorderProperty(recorder, nameof(FlightRecorder.RewindReservedRep), 7f);
            SetRecorderProperty(recorder, nameof(FlightRecorder.PreLaunchFunds), 9000d);
            SetRecorderProperty(recorder, nameof(FlightRecorder.PreLaunchScience), 123d);
            SetRecorderProperty(recorder, nameof(FlightRecorder.PreLaunchReputation), 55f);

            ParsekFlight.CopyRewindSaveToRoot(tree, recorder, logTag: "RecorderOverload");

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_fallback", root.RewindSaveFileName);
            Assert.Equal(1200, root.RewindReservedFunds);
            Assert.Equal(45, root.RewindReservedScience);
            Assert.Equal(7, root.RewindReservedRep);
            Assert.Equal(9000, root.PreLaunchFunds);
            Assert.Equal(123, root.PreLaunchScience);
            Assert.Equal(55, root.PreLaunchReputation);
        }

        [Fact]
        public void UsesRecorderFallback_WhenCaptureHasNoSave()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop(rewindSave: null);

            ParsekFlight.CopyRewindSaveToRoot(tree, capture,
                recorderFallbackSave: "parsek_rw_fallback");

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_fallback", root.RewindSaveFileName);
        }

        [Fact]
        public void NoOp_WhenTreeIsNull()
        {
            var capture = MakeCaptureAtStop("parsek_rw_x");
            // Should not throw
            ParsekFlight.CopyRewindSaveToRoot(null, capture);
        }

        [Fact]
        public void NoOp_WhenNoRootRecordingId()
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = null };
            var capture = MakeCaptureAtStop("parsek_rw_x");

            ParsekFlight.CopyRewindSaveToRoot(tree, capture);
            // No exception, no-op
        }

        [Fact]
        public void NoOp_WhenBothSourcesHaveNoSave()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop(rewindSave: null);

            ParsekFlight.CopyRewindSaveToRoot(tree, capture, recorderFallbackSave: null);

            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Null(root.RewindSaveFileName);
        }

        [Fact]
        public void LogsWhenRewindSaveCopied()
        {
            var tree = MakeTree();
            var capture = MakeCaptureAtStop("parsek_rw_logged");
            logLines.Clear();

            ParsekFlight.CopyRewindSaveToRoot(tree, capture, logTag: "TestTag");

            Assert.Contains(logLines, l => l.Contains("[Flight]") && l.Contains("TestTag")
                && l.Contains("parsek_rw_logged"));
        }

        [Fact]
        public void EvaBranchScenario_RewindSavePreservedOnRoot()
        {
            // Simulate the EVA branch flow that caused T59:
            // 1. Root recording created with no rewind save (always-tree mode)
            // 2. Parent recorder builds CaptureAtStop with the rewind save
            // 3. CreateSplitBranch calls CopyRewindSaveToRoot
            // 4. EVA recorder takes over (no rewind save)
            // 5. At commit, GetRewindRecording resolves through root

            var tree = MakeTree();
            var parentCapture = MakeCaptureAtStop("parsek_rw_eva_parent",
                funds: 5000, sci: 200, rep: 80,
                preFunds: 3000, preSci: 150, preRep: 60);

            // Step 3: split copies rewind to root
            ParsekFlight.CopyRewindSaveToRoot(tree, parentCapture, logTag: "CreateSplitBranch");

            // Add EVA branch child (no rewind save)
            string evaChildId = Guid.NewGuid().ToString("N");
            tree.Recordings[evaChildId] = new Recording
            {
                RecordingId = evaChildId,
                TreeId = tree.Id,
                VesselName = "Bob Kerman",
                EvaCrewName = "Bob Kerman",
                ParentRecordingId = tree.RootRecordingId
            };

            // Step 5: verify rewind resolves for the EVA child
            var root = tree.Recordings[tree.RootRecordingId];
            Assert.Equal("parsek_rw_eva_parent", root.RewindSaveFileName);
            Assert.Equal(5000, root.RewindReservedFunds);
            Assert.Equal(200, root.RewindReservedScience);
            Assert.Equal(80, root.RewindReservedRep);
            Assert.Equal(3000, root.PreLaunchFunds);
            Assert.Equal(150, root.PreLaunchScience);
            Assert.Equal(60, root.PreLaunchReputation);

            // GetRewindRecording should resolve EVA child -> root
            var trees = new List<RecordingTree> { tree };
            var evaChild = tree.Recordings[evaChildId];
            var resolved = RecordingStore.GetRewindRecording(evaChild, trees);
            Assert.Same(root, resolved);
            Assert.Equal("parsek_rw_eva_parent", resolved.RewindSaveFileName);
        }
    }
}
