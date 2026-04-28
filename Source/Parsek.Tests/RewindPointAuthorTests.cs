using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 4 (Rewind-to-Staging, design §5.1 + §6.1 + §7.1 + §7.4 + §7.19 + §7.27):
    /// guards <see cref="RewindPointAuthor"/>. Begin() is synchronous + observable;
    /// the deferred body is driven via <see cref="RewindPointAuthor.ExecuteDeferredBody"/>
    /// so tests do not need to run a Unity coroutine scheduler.
    /// </summary>
    [Collection("Sequential")]
    public class RewindPointAuthorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly string tempDir;

        public RewindPointAuthorTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetInstanceForTesting();

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_rp_author_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        // --- Helpers --------------------------------------------------------

        private static ParsekScenario MakeScenario()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static BranchPoint MakeBp(string id = "bp_test")
        {
            return new BranchPoint
            {
                Id = id,
                Type = BranchPointType.Undock,
                UT = 0.0
            };
        }

        private static ChildSlot Slot(int idx, string recId)
        {
            return new ChildSlot
            {
                SlotIndex = idx,
                OriginChildRecordingId = recId,
                Controllable = true,
                Disabled = false,
                DisabledReason = null
            };
        }

        /// <summary>Mock flight-globals that resolves snapshots from an injected dict.</summary>
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

        private sealed class FakeScenePaths : RewindPointAuthor.IScenePaths
        {
            private readonly string saveFolder;
            private readonly string rewindDir;
            public FakeScenePaths(string saveFolder, string rewindDir)
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

        private sealed class FakeTimeWarp : RewindPointAuthor.ITimeWarpProvider
        {
            public int InitialRateIndex;
            public int CurrentRateIndex;
            public readonly List<(int rate, bool instant)> Calls = new List<(int, bool)>();
            public int GetCurrentRateIndex() => CurrentRateIndex;
            public void SetRate(int rateIndex, bool instant)
            {
                Calls.Add((rateIndex, instant));
                CurrentRateIndex = rateIndex;
            }
        }

        private sealed class FakeSaveRoot : RewindPointAuthor.ISaveRootProvider
        {
            private readonly string dir;
            public FakeSaveRoot(string dir) { this.dir = dir; }
            public string GetSaveDirectory() => dir;
        }

        /// <summary>Creates a .sfs stub at src path and returns a delegate that behaves like KSP's SaveGame.</summary>
        private Func<string, string, string> MakeSaveAction(string saveRoot, bool writeFile = true, bool throwing = false)
        {
            return (name, folder) =>
            {
                if (throwing) throw new IOException("simulated save failure");
                if (writeFile)
                {
                    string path = Path.Combine(saveRoot, name + ".sfs");
                    File.WriteAllText(path, "FLIGHTSTATE { UT = 100 }\n");
                    return path;
                }
                return string.Empty;
            };
        }

        // --- Scene guard ----------------------------------------------------

        [Fact]
        public void Begin_SceneNotFlight_Aborts_LogsWarn()
        {
            // Guard: scene != FLIGHT must abort synchronously. Force KSC so the
            // test is independent of whatever scene prior tests left set.
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            try
            {
                var scenario = MakeScenario();
                var bp = MakeBp();
                var slots = new List<ChildSlot> { Slot(0, "recA"), Slot(1, "recB") };

                RewindPoint rp = RewindPointAuthor.Begin(bp, slots, new List<uint> { 1, 2 });

                Assert.Null(rp);
                Assert.Empty(scenario.RewindPoints);
                Assert.Null(bp.RewindPointId);
                Assert.Contains(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("[RewindSave]") &&
                    l.Contains("Aborted") && l.Contains("scene="));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- Synchronous wiring --------------------------------------------

        /// <summary>
        /// Synchronous effect of Begin: the RP is appended to
        /// <c>ParsekScenario.RewindPoints</c> and the BranchPoint carries the
        /// new id BEFORE the deferred coroutine runs. Uses <c>SyncRunForTesting</c>
        /// to suppress the coroutine entirely so we observe pure Begin effects.
        /// </summary>
        [Fact]
        public void Begin_AppendsRpAndSetsBranchPointId_Immediately()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = MakeScenario();
                var bp = MakeBp("bp_immediate");
                var slots = new List<ChildSlot> { Slot(0, "recA"), Slot(1, "recB") };

                bool syncCalled = false;
                RewindPointAuthor.SyncRunForTesting = (rp, ctx) => syncCalled = true;
                try
                {
                    RewindPoint rp = RewindPointAuthor.Begin(bp, slots, new List<uint> { 1, 2 });

                    Assert.NotNull(rp);
                    Assert.True(rp.SessionProvisional);
                    Assert.False(rp.Corrupted);
                    Assert.StartsWith("rp_", rp.RewindPointId);
                    Assert.Equal(bp.Id, rp.BranchPointId);
                    Assert.Equal($"Parsek/RewindPoints/{rp.RewindPointId}.sfs", rp.QuicksaveFilename);
                    Assert.False(string.IsNullOrEmpty(rp.CreatedRealTime));
                    Assert.Same(rp, scenario.RewindPoints.Single());
                    Assert.Equal(rp.RewindPointId, bp.RewindPointId);
                    Assert.True(syncCalled);

                    Assert.Contains(logLines, l =>
                        l.Contains("[Rewind]") && l.Contains("RewindPoint begin") &&
                        l.Contains(rp.RewindPointId));
                }
                finally
                {
                    RewindPointAuthor.SyncRunForTesting = null;
                }
            }
            finally
            {
                HighLogic.LoadedScene = GameScenes.LOADING;
            }
        }

        [Fact]
        public void Begin_MultiControllableSplit_BuildsChildSlots_OneEach()
        {
            // Verifies Begin preserves the childSlots list the caller passes in:
            // one per controllable child, in order, with OriginChildRecordingId set.
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = MakeScenario();
                var bp = MakeBp("bp_multi");
                var slots = new List<ChildSlot>
                {
                    Slot(0, "rec_active"),
                    Slot(1, "rec_background"),
                    Slot(2, "rec_third")
                };

                RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { };
                try
                {
                    RewindPoint rp = RewindPointAuthor.Begin(bp, slots, new List<uint> { 1, 2, 3 });
                    Assert.NotNull(rp);
                    Assert.Equal(3, rp.ChildSlots.Count);
                    Assert.Equal("rec_active", rp.ChildSlots[0].OriginChildRecordingId);
                    Assert.Equal("rec_background", rp.ChildSlots[1].OriginChildRecordingId);
                    Assert.Equal("rec_third", rp.ChildSlots[2].OriginChildRecordingId);
                    Assert.All(rp.ChildSlots, s => Assert.True(s.Controllable));
                    Assert.All(rp.ChildSlots, s => Assert.False(s.Disabled));
                }
                finally { RewindPointAuthor.SyncRunForTesting = null; }
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        [Fact]
        public void Begin_CapturesFocusedSlotIndex_WhenActiveVesselMatchesSlotPid()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                MakeScenario();
                var bp = MakeBp("bp_focus");
                var slots = new List<ChildSlot>
                {
                    Slot(0, "rec_mothership"),
                    Slot(1, "rec_probe")
                };

                RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { };
                try
                {
                    RewindPoint rp = RewindPointAuthor.Begin(
                        bp,
                        slots,
                        new List<uint> { 100u, 200u },
                        new RewindPointAuthorContext
                        {
                            FlightGlobals = new FakeFlightGlobals(
                                new Dictionary<uint, VesselSnapshot>(), activeVesselPid: 200u),
                            RecordingResolver = recId =>
                                recId == "rec_mothership" ? (uint?)100u :
                                recId == "rec_probe" ? (uint?)200u :
                                null
                        });

                    Assert.NotNull(rp);
                    Assert.Equal(1, rp.FocusSlotIndex);
                    Assert.Contains(logLines, l =>
                        l.Contains("[Rewind]") && l.Contains("focusSlot=1"));
                }
                finally { RewindPointAuthor.SyncRunForTesting = null; }
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        [Fact]
        public void Begin_CapturesNoFocusSignal_WhenActiveVesselDoesNotMatchAnySlot()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                MakeScenario();
                var bp = MakeBp("bp_no_focus");
                var slots = new List<ChildSlot>
                {
                    Slot(0, "rec_a"),
                    Slot(1, "rec_b")
                };

                RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { };
                try
                {
                    RewindPoint rp = RewindPointAuthor.Begin(
                        bp,
                        slots,
                        new List<uint> { 100u, 200u },
                        new RewindPointAuthorContext
                        {
                            FlightGlobals = new FakeFlightGlobals(
                                new Dictionary<uint, VesselSnapshot>(), activeVesselPid: 999u),
                            RecordingResolver = recId =>
                                recId == "rec_a" ? (uint?)100u :
                                recId == "rec_b" ? (uint?)200u :
                                null
                        });

                    Assert.NotNull(rp);
                    Assert.Equal(-1, rp.FocusSlotIndex);
                }
                finally { RewindPointAuthor.SyncRunForTesting = null; }
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        /// <summary>§7.1: a single-controllable output does not trigger Begin — the
        /// caller decides that gate. Here we verify the author does not invent a
        /// multi-controllable state: it honors whatever the caller passed.</summary>
        [Fact]
        public void Begin_NoSlots_ReturnsNull()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = MakeScenario();
                RewindPoint rp = RewindPointAuthor.Begin(MakeBp(), new List<ChildSlot>(), new List<uint>());
                Assert.Null(rp);
                Assert.Empty(scenario.RewindPoints);
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- Deferred body: PID map capture --------------------------------

        private (ParsekScenario scenario, RewindPoint rp, BranchPoint bp, RewindPointAuthorContext ctx)
            PrepareDeferredBodyFixture(
                Dictionary<uint, VesselSnapshot> vessels,
                Dictionary<string, uint> recIdToPid,
                bool saveThrowing = false,
                int initialWarpRate = 0)
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            var scenario = MakeScenario();
            var bp = MakeBp("bp_deferred");
            var slots = new List<ChildSlot>();
            int idx = 0;
            foreach (var kv in recIdToPid)
            {
                slots.Add(Slot(idx++, kv.Key));
            }

            // Invoke the synchronous wiring, then capture the RP so we can drive the
            // deferred body directly. Install SyncRunForTesting so Begin does not
            // attempt to schedule a Unity coroutine.
            RewindPointAuthorContext capturedCtx = null;
            RewindPoint capturedRp = null;
            RewindPointAuthor.SyncRunForTesting = (rp, ctx) =>
            {
                capturedCtx = ctx;
                capturedRp = rp;
            };
            try
            {
                var warp = new FakeTimeWarp { InitialRateIndex = initialWarpRate, CurrentRateIndex = initialWarpRate };
                var paths = new FakeScenePaths(
                    saveFolder: "parsek_test_save",
                    rewindDir: Path.Combine(tempDir, "RewindPoints"));
                var saveRoot = new FakeSaveRoot(tempDir);

                RewindPoint rpOut = RewindPointAuthor.Begin(
                    bp, slots, new List<uint>(recIdToPid.Values),
                    new RewindPointAuthorContext
                    {
                        FlightGlobals = new FakeFlightGlobals(vessels),
                        RecordingResolver = recId =>
                            recIdToPid.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null,
                        SaveAction = MakeSaveAction(tempDir, writeFile: !saveThrowing, throwing: saveThrowing),
                        ScenePathsProvider = paths,
                        TimeWarp = warp,
                        SaveRootProvider = saveRoot
                    });

                Assert.NotNull(rpOut);
                return (scenario, capturedRp, bp, capturedCtx);
            }
            finally { RewindPointAuthor.SyncRunForTesting = null; }
        }

        [Fact]
        public void DeferredRun_PopulatesPidSlotMap_FromLiveVessels()
        {
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);

                Assert.Equal(2, rp.PidSlotMap.Count);
                Assert.Equal(0, rp.PidSlotMap[100u]);
                Assert.Equal(1, rp.PidSlotMap[200u]);
                Assert.False(rp.Corrupted);
                Assert.All(rp.ChildSlots, s => Assert.False(s.Disabled));

                // Log-assertion: successful write line per §10.1.
                Assert.Contains(logLines, l =>
                    l.Contains("[RewindSave]") && l.Contains("Wrote rp=" + rp.RewindPointId));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        [Fact]
        public void DeferredRun_PopulatesRootPartPidMap_FromRootParts()
        {
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);
                Assert.Equal(2, rp.RootPartPidMap.Count);
                Assert.Equal(0, rp.RootPartPidMap[1001u]);
                Assert.Equal(1, rp.RootPartPidMap[2001u]);
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        /// <summary>§7.4: a slot whose live vessel cannot be resolved is Disabled
        /// with reason "no-live-vessel"; remaining slots still populate.</summary>
        [Fact]
        public void DeferredRun_PartialSlotFailure_DisablesFailedSlot_ContinuesOthers()
        {
            // rec1 maps to pid 200, but 200 is not in vessels -> slot disabled.
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);

                // Slot 0 populated; slot 1 disabled.
                Assert.False(rp.ChildSlots[0].Disabled);
                Assert.True(rp.ChildSlots[1].Disabled);
                Assert.Equal("no-live-vessel", rp.ChildSlots[1].DisabledReason);
                Assert.Single(rp.PidSlotMap);
                Assert.True(rp.PidSlotMap.ContainsKey(100u));
                Assert.False(rp.Corrupted);

                Assert.Contains(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("[Rewind]")
                    && l.Contains("Slot 1 disabled") && l.Contains("rec=rec1"));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        [Fact]
        public void DeferredRun_AllSlotsFail_MarksCorrupted_KeepsRP()
        {
            // No vessels resolve. Both slots disabled, RP marked corrupted,
            // but the save still runs and the RP remains in the scenario list.
            var vessels = new Dictionary<uint, VesselSnapshot>();
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (scenario, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);

                Assert.True(rp.Corrupted);
                Assert.Empty(rp.PidSlotMap);
                Assert.Empty(rp.RootPartPidMap);
                Assert.All(rp.ChildSlots, s => Assert.True(s.Disabled));
                Assert.Single(scenario.RewindPoints);
                Assert.Equal(rp.RewindPointId, bp.RewindPointId);

                Assert.Contains(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("[Rewind]")
                    && l.Contains("All slots disabled") && l.Contains("RP unusable"));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- Warp drop / restore -------------------------------------------

        [Fact]
        public void DeferredRun_RestoresTimeWarp_OnException()
        {
            // Simulate a save-side exception. Warp is at rate 4 on entry; the
            // author must drop to 0, then restore 4 in the finally even though
            // the body throws via the SaveAction.
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) }
            };
            var recMap = new Dictionary<string, uint> { { "rec0", 100u } };

            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var scenario = MakeScenario();
            var bp = MakeBp("bp_warp");
            var slots = new List<ChildSlot> { Slot(0, "rec0"), Slot(1, "rec_missing") };
            var warp = new FakeTimeWarp { InitialRateIndex = 4, CurrentRateIndex = 4 };

            RewindPointAuthorContext capturedCtx = null;
            RewindPoint capturedRp = null;
            RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { capturedCtx = ctx; capturedRp = rp; };
            try
            {
                RewindPointAuthor.Begin(bp, slots, new List<uint> { 100u, 200u },
                    new RewindPointAuthorContext
                    {
                        FlightGlobals = new FakeFlightGlobals(vessels),
                        RecordingResolver = recId =>
                            recMap.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null,
                        SaveAction = MakeSaveAction(tempDir, writeFile: false, throwing: true),
                        ScenePathsProvider = new FakeScenePaths("sf", Path.Combine(tempDir, "RewindPoints")),
                        TimeWarp = warp,
                        SaveRootProvider = new FakeSaveRoot(tempDir)
                    });
            }
            finally { RewindPointAuthor.SyncRunForTesting = null; }

            // Now drive the deferred body directly: the save will throw,
            // rollback runs, and warp is restored in the finally.
            RewindPointAuthor.ExecuteDeferredBody(capturedRp, bp, capturedCtx);

            // Two SetRate calls: drop to 0 (instant=true), restore to 4 (instant=true).
            Assert.Equal(2, warp.Calls.Count);
            Assert.Equal((0, true), warp.Calls[0]);
            Assert.Equal((4, true), warp.Calls[1]);
            Assert.Equal(4, warp.CurrentRateIndex);

            HighLogic.LoadedScene = GameScenes.LOADING;
        }

        [Fact]
        public void DeferredRun_HighWarp_DropsThenRestores_OnSuccess()
        {
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(
                vessels, recMap, saveThrowing: false, initialWarpRate: 3);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);
                // Warp mock logged drop then restore.
                var warp = (FakeTimeWarp)ctx.TimeWarp;
                Assert.Equal(2, warp.Calls.Count);
                Assert.Equal((0, true), warp.Calls[0]);
                Assert.Equal((3, true), warp.Calls[1]);

                Assert.Contains(logLines, l =>
                    l.Contains("[RewindSave]") && l.Contains("Warp dropped from 3"));
                Assert.Contains(logLines, l =>
                    l.Contains("[RewindSave]") && l.Contains("Warp restored to 3"));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        [Fact]
        public void DeferredRun_LowWarp_NoDropNoRestore()
        {
            // Warp 0 or 1 = physics or 1x; author must not touch warp at all
            // so it does not override user intent for no reason.
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(
                vessels, recMap, saveThrowing: false, initialWarpRate: 1);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);
                var warp = (FakeTimeWarp)ctx.TimeWarp;
                Assert.Empty(warp.Calls);
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- File produced + path correctness ------------------------------

        [Fact]
        public void DeferredRun_Succeeds_MovesSaveFileIntoRewindPointsDir()
        {
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (_, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);

                string dst = Path.Combine(tempDir, "RewindPoints", rp.RewindPointId + ".sfs");
                Assert.True(File.Exists(dst), $"Expected RP file at {dst}");

                // Source path must be gone (atomic move removed it).
                string src = Path.Combine(tempDir, "Parsek_TempRP_" + rp.RewindPointId + ".sfs");
                Assert.False(File.Exists(src), $"Temp file should be gone after move: {src}");
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- Rollback on save failure --------------------------------------

        [Fact]
        public void DeferredRun_SaveThrows_RollsBackBranchPointIdAndScenarioList()
        {
            var vessels = new Dictionary<uint, VesselSnapshot>
            {
                { 100u, new VesselSnapshot(100u, 1001u, true) },
                { 200u, new VesselSnapshot(200u, 2001u, true) }
            };
            var recMap = new Dictionary<string, uint>
            {
                { "rec0", 100u },
                { "rec1", 200u }
            };

            var (scenario, rp, bp, ctx) = PrepareDeferredBodyFixture(vessels, recMap, saveThrowing: true);
            try
            {
                RewindPointAuthor.ExecuteDeferredBody(rp, bp, ctx);

                Assert.Empty(scenario.RewindPoints);
                Assert.Null(bp.RewindPointId);

                Assert.Contains(logLines, l =>
                    l.Contains("[ERROR]") && l.Contains("[RewindSave]")
                    && l.Contains("Failed rp=" + rp.RewindPointId)
                    && l.Contains("simulated save failure"));
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }

        // --- ResolveSlotVesselPid (pure helper) ----------------------------

        [Fact]
        public void ResolveSlotVesselPid_ResolverReturnsPid_UsesIt()
        {
            var slot = Slot(0, "rec_x");
            uint? pid = RewindPointAuthor.ResolveSlotVesselPid(slot, recId => 42u);
            Assert.Equal(42u, pid);
        }

        [Fact]
        public void ResolveSlotVesselPid_NullSlot_ReturnsNull()
        {
            Assert.Null(RewindPointAuthor.ResolveSlotVesselPid(null, recId => 1u));
        }

        [Fact]
        public void ResolveSlotVesselPid_EmptyRecordingId_ReturnsNull()
        {
            Assert.Null(RewindPointAuthor.ResolveSlotVesselPid(Slot(0, ""), recId => 1u));
        }

        // --- Coroutine scene re-check ---------------------------------------

        /// <summary>
        /// §6.1 + §7.27: the deferred coroutine re-checks <see cref="HighLogic.LoadedScene"/>
        /// after the one-frame defer. If the player quit to KSC / reverted between
        /// <see cref="RewindPointAuthor.Begin"/> and the deferred resumption, the
        /// coroutine must roll back the synchronous wiring (remove the RP from
        /// <c>ParsekScenario.RewindPoints</c>, clear <c>BranchPoint.RewindPointId</c>)
        /// and emit a Warn log. We drive <see cref="RewindPointAuthor.RunDeferred"/>
        /// manually via its <see cref="System.Collections.IEnumerator"/> so no Unity
        /// coroutine scheduler is needed: pull the first yield, flip the scene, then
        /// resume — the next <c>MoveNext</c> runs the re-check.
        /// </summary>
        [Fact]
        public void RunDeferred_SceneChangedMidCoroutine_RollsBack()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var scenario = MakeScenario();
            var bp = MakeBp("bp_scene_flip");
            var slots = new List<ChildSlot> { Slot(0, "recA"), Slot(1, "recB") };

            // Install SyncRunForTesting = no-op so Begin does not actually schedule
            // a coroutine; capture the RP + ctx for the manual RunDeferred drive.
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
                rpOut = RewindPointAuthor.Begin(bp, slots, new List<uint> { 1u, 2u });
            }
            finally { RewindPointAuthor.SyncRunForTesting = null; }

            Assert.NotNull(rpOut);
            Assert.Same(rpOut, capturedRp);
            Assert.Equal(rpOut.RewindPointId, bp.RewindPointId);
            Assert.Single(scenario.RewindPoints);

            try
            {
                // Drive the coroutine by hand. The first MoveNext returns true and
                // stops at `yield return null` (the one-frame defer). We then flip
                // the scene and call MoveNext again — the re-check must trip and
                // the coroutine must yield-break after rolling back.
                var coroutine = RewindPointAuthor.RunDeferred(capturedRp, bp, capturedCtx);
                Assert.True(coroutine.MoveNext()); // hits `yield return null`

                HighLogic.LoadedScene = GameScenes.SPACECENTER;

                bool continued = coroutine.MoveNext(); // runs the re-check + yield break
                Assert.False(continued);

                // Rollback observable: RP removed, BranchPoint.RewindPointId cleared.
                Assert.Empty(scenario.RewindPoints);
                Assert.Null(bp.RewindPointId);

                // Log-assertion per §10.1: Warn line names the new scene.
                Assert.Contains(logLines, l =>
                    l.Contains("[WARN]") && l.Contains("[RewindSave]")
                    && l.Contains("Aborted mid-coroutine") && l.Contains("scene=SPACECENTER"));
            }
            finally
            {
                HighLogic.LoadedScene = GameScenes.LOADING;
            }
        }

        // --- CreatingSessionId ---------------------------------------------

        /// <summary>
        /// Phase 4 review: when a re-fly session is active at Begin-time, the RP
        /// must record the active marker's <c>SessionId</c> in
        /// <see cref="RewindPoint.CreatingSessionId"/> so it is recognizable as
        /// speculative within that session context. When no session is active the
        /// id stays null (covered implicitly by the other Begin tests).
        /// </summary>
        [Fact]
        public void Begin_UnderActiveSession_CopiesCreatingSessionId()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            try
            {
                var scenario = MakeScenario();
                scenario.ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_test123",
                    TreeId = "tree_test",
                    ActiveReFlyRecordingId = "rec_test",
                    OriginChildRecordingId = "rec_origin",
                    RewindPointId = "rp_prev",
                    InvokedUT = 100.0
                };

                var bp = MakeBp("bp_session");
                var slots = new List<ChildSlot> { Slot(0, "recA"), Slot(1, "recB") };

                RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { };
                RewindPoint rp;
                try
                {
                    rp = RewindPointAuthor.Begin(bp, slots, new List<uint> { 1u, 2u });
                }
                finally { RewindPointAuthor.SyncRunForTesting = null; }

                Assert.NotNull(rp);
                Assert.Equal("sess_test123", rp.CreatingSessionId);
                Assert.Same(rp, scenario.RewindPoints.Single());
            }
            finally { HighLogic.LoadedScene = GameScenes.LOADING; }
        }
    }
}
