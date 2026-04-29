using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 4 (Rewind-to-Staging, design §5.1 + §7.1): the background-split RP
    /// hook in <c>BackgroundRecorder.HandleBackgroundVesselSplit</c> mirrors the
    /// active-vessel path — when a background vessel structurally splits and
    /// produces >=2 controllable outputs (the surviving parent continuation +
    /// each <c>newVesselInfos</c> entry with <c>hasController=true</c>), it
    /// hands control to <see cref="RewindPointAuthor.Begin"/> with the same
    /// <see cref="RewindPointAuthorContext"/> contract the active-vessel path uses.
    ///
    /// <para>
    /// Unity dependencies (<c>ParsekFlight.IsTrackableVessel</c> iterates
    /// <c>v.parts</c>; <c>FlightRecorder.FindVesselByPid</c> reads
    /// <c>FlightGlobals.Vessels</c>) make driving the full background hook
    /// inside xUnit impractical. These tests instead pin the contract the hook
    /// depends on: (a) a multi-controllable background split produces a RP with
    /// one slot per controllable recording, (b) a single-controllable split
    /// produces no RP, (c) the <c>RecordingResolver</c> shape the hook passes
    /// in wires live background vessels to slots correctly.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundSplitRpTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly string tempDir;

        public BackgroundSplitRpTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetInstanceForTesting();

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_bg_split_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekScenario.ResetInstanceForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private sealed class FakeFlightGlobals : IFlightGlobalsProvider
        {
            private readonly Dictionary<uint, VesselSnapshot> snapshots;
            private readonly uint? activeVesselPid;
            public FakeFlightGlobals(Dictionary<uint, VesselSnapshot> snapshots, uint? activeVesselPid = null)
            {
                this.snapshots = snapshots ?? new Dictionary<uint, VesselSnapshot>();
                this.activeVesselPid = activeVesselPid;
            }
            public Vessel FindVesselByPid(uint pid) => null;
            public bool TryGetVesselSnapshot(uint pid, out VesselSnapshot snapshot)
                => snapshots.TryGetValue(pid, out snapshot);
            public uint? GetActiveVesselPid() => activeVesselPid;
        }

        /// <summary>
        /// Contract: when <c>HandleBackgroundVesselSplit</c> produces a parent
        /// continuation + 1 controllable child (total 2 controllable recordings),
        /// the hook calls <see cref="RewindPointAuthor.Begin"/> which appends a
        /// multi-slot RP to the scenario. We drive <c>Begin</c> directly with the
        /// payload shape the hook constructs (slot 0 = parent continuation,
        /// slot 1 = controllable spinoff; PIDs resolved via the per-call
        /// RecordingResolver).
        /// </summary>
        [Fact]
        public void Begin_BackgroundSplit_MultiControllable_CreatesRp()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = new ParsekScenario { RewindPoints = new List<RewindPoint>() };
                ParsekScenario.SetInstanceForTesting(scenario);

                var bp = new BranchPoint
                {
                    Id = "bp_bg_split",
                    Type = BranchPointType.JointBreak,
                    UT = 200.0
                };

                // Background payload: parent-continuation recording (pid=500) +
                // one controllable spinoff recording (pid=600). Both trackable.
                const uint parentPid = 500u;
                const uint childPid = 600u;
                var parentContRecId = "rec_bg_parent_cont";
                var childRecId = "rec_bg_child";

                var vessels = new Dictionary<uint, VesselSnapshot>
                {
                    { parentPid, new VesselSnapshot(parentPid, 5001u, true) },
                    { childPid, new VesselSnapshot(childPid, 6001u, true) }
                };
                var recIdToPid = new Dictionary<string, uint>
                {
                    { parentContRecId, parentPid },
                    { childRecId, childPid }
                };

                // Same slot shape the background hook builds.
                var slots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = parentContRecId,
                        Controllable = true,
                        Disabled = false
                    },
                    new ChildSlot
                    {
                        SlotIndex = 1,
                        OriginChildRecordingId = childRecId,
                        Controllable = true,
                        Disabled = false
                    }
                };

                RewindPointAuthorContext capturedCtx = null;
                RewindPoint capturedRp = null;
                RewindPointAuthor.SyncRunForTesting = (rp, ctx) =>
                {
                    capturedRp = rp;
                    capturedCtx = ctx;
                };

                RewindPoint rpOut;
                try
                {
                    rpOut = RewindPointAuthor.Begin(
                        bp, slots, new List<uint> { parentPid, childPid },
                        new RewindPointAuthorContext
                        {
                            FlightGlobals = new FakeFlightGlobals(vessels),
                            RecordingResolver = recId =>
                                recIdToPid.TryGetValue(recId ?? "", out var pid)
                                    ? (uint?)pid : null
                        });
                }
                finally { RewindPointAuthor.SyncRunForTesting = null; }

                Assert.NotNull(rpOut);
                Assert.Equal(bp.Id, rpOut.BranchPointId);
                Assert.Same(rpOut, capturedRp);
                Assert.Equal(2, rpOut.ChildSlots.Count);
                Assert.Equal(parentContRecId, rpOut.ChildSlots[0].OriginChildRecordingId);
                Assert.Equal(childRecId, rpOut.ChildSlots[1].OriginChildRecordingId);
                Assert.Same(rpOut, scenario.RewindPoints[0]);
                Assert.Equal(rpOut.RewindPointId, bp.RewindPointId);

                // Drive the deferred body to verify the RecordingResolver wiring
                // actually populates PidSlotMap for both background candidates.
                var paths = new DummyScenePaths(
                    saveFolder: "parsek_test_save",
                    rewindDir: Path.Combine(tempDir, "RewindPoints"));
                var warp = new DummyTimeWarp();
                var saveRoot = new DummySaveRoot(tempDir);
                capturedCtx.ScenePathsProvider = paths;
                capturedCtx.TimeWarp = warp;
                capturedCtx.SaveRootProvider = saveRoot;
                capturedCtx.SaveAction = (name, folder) =>
                {
                    string p = Path.Combine(tempDir, name + ".sfs");
                    File.WriteAllText(p, "FLIGHTSTATE { UT = 200 }\n");
                    return p;
                };
                RewindPointAuthor.ExecuteDeferredBody(capturedRp, bp, capturedCtx);

                Assert.Equal(2, rpOut.PidSlotMap.Count);
                Assert.Equal(0, rpOut.PidSlotMap[parentPid]);
                Assert.Equal(1, rpOut.PidSlotMap[childPid]);
                Assert.False(rpOut.Corrupted);
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        /// <summary>
        /// Contract: a single-controllable background split (e.g. parent stays
        /// controllable but every spinoff is debris) must not create a RP. The
        /// hook's classifier <see cref="SegmentBoundaryLogic.IsMultiControllableSplit"/>
        /// gates this before <c>Begin</c> is called. Here we simulate by calling
        /// the gate directly with count=1 and verifying it returns false, then
        /// calling <c>Begin</c> with &lt;2 slots and verifying it returns null.
        /// </summary>
        [Fact]
        public void Begin_BackgroundSplit_SingleControllable_NoRp()
        {
            // Classifier gate (the hook calls this before Begin).
            Assert.False(SegmentBoundaryLogic.IsMultiControllableSplit(1));
            Assert.False(SegmentBoundaryLogic.IsMultiControllableSplit(0));
            Assert.True(SegmentBoundaryLogic.IsMultiControllableSplit(2));

            // Begin itself also rejects 0-slot callers (belt + suspenders).
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = new ParsekScenario { RewindPoints = new List<RewindPoint>() };
                ParsekScenario.SetInstanceForTesting(scenario);

                RewindPoint rp = RewindPointAuthor.Begin(
                    new BranchPoint { Id = "bp_single", Type = BranchPointType.JointBreak },
                    new List<ChildSlot>(), new List<uint>());
                Assert.Null(rp);
                Assert.Empty(scenario.RewindPoints);
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        private sealed class DummyScenePaths : RewindPointAuthor.IScenePaths
        {
            private readonly string saveFolder;
            private readonly string rewindDir;
            public DummyScenePaths(string saveFolder, string rewindDir)
            {
                this.saveFolder = saveFolder;
                this.rewindDir = rewindDir;
            }
            public string GetSaveFolder() => saveFolder;
            public string EnsureRewindPointsDirectory()
            {
                if (!string.IsNullOrEmpty(rewindDir) && !Directory.Exists(rewindDir))
                    Directory.CreateDirectory(rewindDir);
                return rewindDir;
            }
        }

        private sealed class DummyTimeWarp : RewindPointAuthor.ITimeWarpProvider
        {
            public int GetCurrentRateIndex() => 0;
            public void SetRate(int rateIndex, bool instant) { }
        }

        private sealed class DummySaveRoot : RewindPointAuthor.ISaveRootProvider
        {
            private readonly string dir;
            public DummySaveRoot(string dir) { this.dir = dir; }
            public string GetSaveDirectory() => dir;
        }
    }
}
