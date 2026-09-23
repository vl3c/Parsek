using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Operator ruling 2026-09-23: a rewind point ALWAYS survives a plain rewind
    /// (Rewind-to-Launch / Warp-to-game-start), and its Re-Fly is enabled only once the
    /// clock has reached the RP's UT again.
    ///
    /// <para>Survival: the rewind's OnLoad reads persistent.sfs as last written
    /// (<c>SpaceCenterMain.Start</c> reloads it), so the RP list was of unknown age - GS-4
    /// lost an RP created after the last persistent write, RF-4 got back one the reaper had
    /// already removed. <see cref="RecordingStore.CaptureRewindPointsForRewind"/> +
    /// <see cref="RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad"/> carry the
    /// in-memory list across instead.</para>
    ///
    /// <para>Gate: <see cref="RewindInvoker.CanInvoke"/> refuses an RP whose UT is later
    /// than the current UT, so every entry point (Recordings table, Timeline, the
    /// confirm-time re-check in StartInvoke, the Retry handler, the test seam) is
    /// covered by one precondition.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RewindPointSurvivesRewindTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly string tempDir;
        private readonly GameScenes previousScene;
        private readonly bool priorStoreSuppress;

        public RewindPointSurvivesRewindTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;
            RewindPointReaper.ResetTestOverrides();
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };

            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-rp-survives-rewind-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = previousScene;
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;
            RewindPointReaper.ResetTestOverrides();
            RewindContext.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { }
            }
        }

        // ---------- helpers ----------------------------------------------

        private static RewindPoint Rp(string id, double ut, params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = "bp_" + id,
                UT = ut,
                QuicksaveFilename = "Parsek/RewindPoints/" + id + ".sfs",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
            };
        }

        private static ChildSlot Slot(int index, string originRecordingId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
            };
        }

        private static ParsekScenario InstallScenario(params RewindPoint[] rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(rps),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static void InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Test_" + treeId };
            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            RecordingStore.CommittedTrees.Add(tree);
        }

        private static Recording Rec(string id, MergeState state)
        {
            return new Recording { RecordingId = id, VesselName = id, MergeState = state };
        }

        private static void BeginPlainRewind()
        {
            RewindContext.BeginRewind(30.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.SetAdjustedUT(15.0);
        }

        /// <summary>
        /// Stands in for <c>LoadRewindStagingState</c>: the rewind's OnLoad rebuilds the
        /// scenario's RP list from the node it was handed, a fresh list object.
        /// </summary>
        private static void SimulateStagingStateLoad(ParsekScenario scenario, params RewindPoint[] fromSave)
        {
            scenario.RewindPoints = new List<RewindPoint>(fromSave);
        }

        private string WriteQuicksave()
        {
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sfs");
            File.WriteAllText(path, "GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            return path;
        }

        private void InstallInvokableQuicksave()
        {
            string quicksavePath = WriteQuicksave();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => quicksavePath;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;
        }

        // ---------- survival across the rewind load ----------------------

        [Fact]
        public void RewindLoad_SaveWithoutTheRp_ReinstallsTheInMemoryRp()
        {
            // GS-4 2026-09-11_0102: the RP was authored after the last persistent.sfs
            // write, so the rewind's OnLoad read `RewindPoints loaded: 0`.
            var rp = Rp("rp_gs4", 119.04, Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(rp);
            BeginPlainRewind();

            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            SimulateStagingStateLoad(scenario);
            int installed = RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);

            Assert.Equal(1, installed);
            Assert.Single(scenario.RewindPoints);
            Assert.Same(rp, scenario.RewindPoints[0]);
            Assert.False(RecordingStore.HasRewindCarriedRewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("Rewind: carrying 1 rewind point(s) across the rewind load")
                && l.Contains("rp_gs4@ut=119.04"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("RewindPoints carried across rewind: installed=1 loadedFromSave=0 restored=1 staleDropped=0"));
        }

        [Fact]
        public void RewindLoad_SaveCarryingAReapedRp_DoesNotResurrectIt()
        {
            // RF-4's reading run: the seal's reap removed the RP (and deleted its
            // quicksave file), then the rewind-to-launch read it back from a
            // persistent.sfs written before the reap. The in-memory list (empty) wins.
            var reaped = Rp("rp_reaped", 50.0, Slot(0, "rec_a"));
            var scenario = InstallScenario();
            BeginPlainRewind();

            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            SimulateStagingStateLoad(scenario, reaped);
            int installed = RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);

            Assert.Equal(0, installed);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("RewindPoints carried across rewind: installed=0 loadedFromSave=1 restored=0 staleDropped=1"));
        }

        [Fact]
        public void RewindLoad_SaveAlreadyCarryingTheRp_KeepsTheInMemoryInstance()
        {
            // GS-7 2026-09-08_2054: a flight entry after the RP wrote persistent.sfs, so the
            // save carried it too. Memory still wins (same id, the live object).
            var live = Rp("rp_gs7", 118.68, Slot(0, "rec_a"), Slot(1, "rec_b"));
            var fromSave = Rp("rp_gs7", 118.68, Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(live);
            BeginPlainRewind();

            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            SimulateStagingStateLoad(scenario, fromSave);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);

            Assert.Single(scenario.RewindPoints);
            Assert.Same(live, scenario.RewindPoints[0]);
            Assert.Contains(logLines, l =>
                l.Contains("RewindPoints carried across rewind: installed=1 loadedFromSave=1 restored=0 staleDropped=0"));
        }

        [Fact]
        public void Capture_IsASnapshot_LaterListMutationDoesNotLeakIn()
        {
            var a = Rp("rp_a", 10.0);
            var scenario = InstallScenario(a);
            BeginPlainRewind();

            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            scenario.RewindPoints.Add(Rp("rp_late", 20.0));
            SimulateStagingStateLoad(scenario);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);

            Assert.Equal(new[] { "rp_a" }, scenario.RewindPoints.Select(r => r.RewindPointId));
        }

        [Fact]
        public void NonRewindLoad_LeavesTheLoadedListAlone_AndDropsAStrandedCapture()
        {
            // Mirror direction: a quickload / scene change (IsRewinding false) must keep
            // reading its RP list from the save it loaded.
            var scenario = InstallScenario(Rp("rp_mem", 10.0));
            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            var fromQuicksave = Rp("rp_quicksave", 5.0);
            SimulateStagingStateLoad(scenario, fromQuicksave);

            int installed = RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);

            Assert.Equal(-1, installed);
            Assert.Single(scenario.RewindPoints);
            Assert.Same(fromQuicksave, scenario.RewindPoints[0]);
            Assert.False(RecordingStore.HasRewindCarriedRewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("Dropped 1 carried rewind point(s) without reinstalling them (reason=load-is-not-a-rewind)"));
        }

        [Fact]
        public void FailedRewindLoad_ResetRewindFlags_DropsTheCapture()
        {
            // Rollback: ExecuteRewindSaveLoad's failure paths (and the seam's rewind-timeout
            // abort) call ResetRewindFlags; nothing may be reinstalled by a later load.
            var scenario = InstallScenario(Rp("rp_a", 10.0));
            BeginPlainRewind();
            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");

            RecordingStore.ResetRewindFlags();

            Assert.False(RecordingStore.HasRewindCarriedRewindPoints);
            var loaded = Rp("rp_loaded", 1.0);
            SimulateStagingStateLoad(scenario, loaded);
            Assert.Equal(-1, RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario));
            Assert.Same(loaded, scenario.RewindPoints.Single());
            Assert.Contains(logLines, l => l.Contains("reason=rewind-flags-reset"));
        }

        [Fact]
        public void ReloadAfterTheRewind_IsAnOrdinaryLoad_TheCaptureIsConsumedOnce()
        {
            var rp = Rp("rp_a", 10.0);
            var scenario = InstallScenario(rp);
            BeginPlainRewind();
            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            SimulateStagingStateLoad(scenario);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);
            RewindContext.EndRewind();   // HandleRewindOnLoad's EndRewind

            // The next load reads the list OnSave wrote from memory: the RP round-trips.
            var node = new ConfigNode("REWIND_POINTS");
            scenario.RewindPoints[0].SaveInto(node);
            var reloaded = RewindPoint.LoadFrom(node.GetNodes("POINT").Single());
            SimulateStagingStateLoad(scenario, reloaded);

            Assert.Equal(-1, RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario));
            Assert.Equal("rp_a", scenario.RewindPoints.Single().RewindPointId);
            Assert.Equal(10.0, scenario.RewindPoints.Single().UT);
        }

        [Fact]
        public void NoScenarioAtCapture_CapturesNothing_LoadedListStands()
        {
            BeginPlainRewind();
            RecordingStore.CaptureRewindPointsForRewind(null, "Warp-to-game-start");

            Assert.False(RecordingStore.HasRewindCarriedRewindPoints);
            var scenario = InstallScenario(Rp("rp_loaded", 1.0));
            Assert.Equal(-1, RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario));
            Assert.Single(scenario.RewindPoints);
        }

        [Fact]
        public void MergeCarriedRewindPoints_Pure_CountsDriftBothWays()
        {
            var carried = new List<RewindPoint> { Rp("a", 1), null, Rp("b", 2) };
            var loaded = new List<RewindPoint> { Rp("b", 2), Rp("c", 3), Rp("d", 4) };

            var merged = RecordingStore.MergeCarriedRewindPoints(
                carried, loaded, out int restored, out int staleDropped);

            Assert.Equal(new[] { "a", "b" }, merged.Select(r => r.RewindPointId));
            Assert.Equal(1, restored);        // a
            Assert.Equal(2, staleDropped);    // c, d
            Assert.Same(carried[2], merged[1]);
        }

        // ---------- reaper interplay (the mirror: slots resolved) --------

        [Fact]
        public void CarriedRp_WithAnOpenSlot_SurvivesTheReaper_ResolvedOneIsReapedAsBefore()
        {
            // Open slot (CommittedProvisional tip) keeps the carried RP alive; an RP whose
            // slots all resolved (Immutable) is reaped by the next pass exactly as it was
            // before the carry-over existed, file delete included.
            InstallTree("tree_1",
                Rec("rec_open", MergeState.CommittedProvisional),
                Rec("rec_sib", MergeState.Immutable),
                Rec("rec_done_a", MergeState.Immutable),
                Rec("rec_done_b", MergeState.Immutable));
            var open = Rp("rp_open", 100.0, Slot(0, "rec_open"), Slot(1, "rec_sib"));
            var done = Rp("rp_done", 100.0, Slot(0, "rec_done_a"), Slot(1, "rec_done_b"));
            var scenario = InstallScenario(open, done);
            BeginPlainRewind();
            RecordingStore.CaptureRewindPointsForRewind(scenario, "Rewind");
            SimulateStagingStateLoad(scenario);
            RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(scenario);
            RewindContext.EndRewind();

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Equal(new[] { "rp_open" }, scenario.RewindPoints.Select(r => r.RewindPointId));
            Assert.Equal(new[] { "rp_done" }, deletedRpIds);
            Assert.False(RewindPointReaper.IsReapEligible(open, scenario.RecordingSupersedes));
        }

        // ---------- the future-RP gate -----------------------------------

        [Theory]
        [InlineData(100.0, 50.0, true)]        // before the RP UT: refused
        [InlineData(100.0, 150.0, false)]      // after it: allowed
        [InlineData(100.0, 100.0, false)]      // exactly at it: allowed
        [InlineData(100.0, 99.9995, false)]    // inside the save round-trip slack
        [InlineData(100.0, 99.99, true)]       // beyond the slack
        [InlineData(0.0, 0.0, false)]          // unstamped RP
        [InlineData(100.0, double.NaN, false)] // no clock: the gate cannot place the RP
        public void IsRewindPointInFuture_Pure(double rpUT, double nowUT, bool expected)
        {
            Assert.Equal(expected, RewindInvoker.IsRewindPointInFuture(rpUT, nowUT));
        }

        [Fact]
        public void CanInvoke_RpInTheFuture_RefusedWithTheTooltipReason_AndLogged()
        {
            InstallInvokableQuicksave();
            var rp = Rp("rp_future", 119.04, Slot(0, "rec_a"));
            RewindInvoker.NowUtProviderForTesting = () => 15.1;

            Assert.False(RewindInvoker.CanInvoke(rp, out string reason));

            Assert.Equal(RewindInvoker.FutureRewindPointReason, reason);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Rewind]")
                && l.Contains("CanInvoke: disabled rp=rp_future")
                && l.Contains("reason='" + RewindInvoker.FutureRewindPointReason + "'")
                && l.Contains("rpUT=119.04")
                && l.Contains("nowUT=15.1"));
        }

        [Fact]
        public void CanInvoke_ClockReachesTheRp_Allowed_ExactlyAtAndAfter()
        {
            InstallInvokableQuicksave();
            var rp = Rp("rp_reached", 119.04, Slot(0, "rec_a"));

            RewindInvoker.NowUtProviderForTesting = () => 119.04;
            Assert.True(RewindInvoker.CanInvoke(rp, out string atReason));
            Assert.Null(atReason);

            RewindInvoker.NowUtProviderForTesting = () => 400.0;
            Assert.True(RewindInvoker.CanInvoke(rp, out string afterReason));
            Assert.Null(afterReason);
        }

        [Fact]
        public void UnfinishedFlightsSlotGate_CarriesTheFutureReason()
        {
            // The Recordings table and the Timeline both route through
            // CanInvokeRewindPointSlot, whose disabled reason is the Fly button's tooltip.
            InstallInvokableQuicksave();
            var rp = Rp("rp_slot_future", 119.04, Slot(0, "rec_a"), Slot(1, "rec_b"));
            RewindInvoker.NowUtProviderForTesting = () => 15.1;

            Assert.False(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 1, out string reason));
            Assert.Equal(RewindInvoker.FutureRewindPointReason, reason);

            RewindInvoker.NowUtProviderForTesting = () => 200.0;
            Assert.True(RecordingsTableUI.CanInvokeRewindPointSlot(rp, 1, out reason));
        }

        [Fact]
        public void Retry_RpInTheFuture_RefusedBeforeTheSessionIsTornDown()
        {
            // Retry clears the marker before StartInvoke re-runs CanInvoke, so the
            // future gate must answer first or a refusal strands the player with no
            // session. Mirror: the clock at the RP retries as before.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_retry",
                TreeId = "tree_retry",
                ActiveReFlyRecordingId = "rec_prov",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp_retry",
                InvokedUT = 50.0,
            };
            var rp = Rp("rp_retry", 110.0, Slot(0, "rec_origin"));
            var scenario = InstallScenario(rp);
            scenario.ActiveReFlySessionMarker = marker;
            int invoked = 0;
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) => invoked++;

            RewindInvoker.NowUtProviderForTesting = () => 50.0;
            RevertInterceptor.RetryHandler(marker);

            Assert.Equal(0, invoked);
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][ReFlySession]")
                && l.Contains("RetryHandler: rp=rp_retry ut=110 is in the future of nowUT=50")
                && l.Contains("session sess=sess_retry kept"));
            Assert.DoesNotContain(logLines, l => l.Contains("End reason=retry"));

            RewindInvoker.NowUtProviderForTesting = () => 110.0;
            RevertInterceptor.RetryHandler(marker);

            Assert.Equal(1, invoked);
            Assert.Null(scenario.ActiveReFlySessionMarker);
        }

        [Fact]
        public void TestSeamRefusal_NamesTheFutureGate()
        {
            Assert.Equal("refly-gate " + RewindInvoker.FutureRewindPointReason,
                Parsek.TestCommands.TestCommandInvokeRewind.GateRefusalMsg(RewindInvoker.FutureRewindPointReason));
        }

        // ---------- source wiring -----------------------------------------

        private static string ReadSource(string relative)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return StripComments(File.ReadAllText(Path.Combine(root, "Source", "Parsek", relative))
                .Replace("\r\n", "\n"));
        }

        /// <summary>
        /// Drops // and /* */ comments (string literals kept intact) so a needle that
        /// survives only inside a comment cannot satisfy a source-order gate.
        /// </summary>
        private static string StripComments(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '"')
                {
                    bool verbatim = i > 0 && src[i - 1] == '@';
                    sb.Append(c);
                    i++;
                    while (i < src.Length)
                    {
                        char d = src[i];
                        sb.Append(d);
                        i++;
                        if (!verbatim && d == '\\' && i < src.Length) { sb.Append(src[i]); i++; continue; }
                        if (d == '"')
                        {
                            if (verbatim && i < src.Length && src[i] == '"') { sb.Append('"'); i++; continue; }
                            break;
                        }
                    }
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? src.Length : end + 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        [Fact]
        public void StripComments_DropsCommentedNeedles_KeepsCodeAndStrings()
        {
            string src = "a(); // ReinstallX(this);\n/* CaptureY( */ b(\"// kept\");";
            string stripped = StripComments(src);
            Assert.DoesNotContain("ReinstallX", stripped);
            Assert.DoesNotContain("CaptureY", stripped);
            Assert.Contains("a();", stripped);
            Assert.Contains("b(\"// kept\");", stripped);
        }

        private static string MethodBody(string source, string declaration)
        {
            int start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, "declaration not found: " + declaration);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced body: " + declaration);
        }

        [Fact]
        public void ExecuteRewindSaveLoad_CapturesTheRpListBeforeItLoads()
        {
            string body = MethodBody(ReadSource("RecordingStore.cs"),
                "private static bool ExecuteRewindSaveLoad(");
            int capture = body.IndexOf("CaptureRewindPointsForRewind(ParsekScenario.Instance", StringComparison.Ordinal);
            int load = body.IndexOf("GamePersistence.LoadGame(", StringComparison.Ordinal);
            Assert.True(capture >= 0, "ExecuteRewindSaveLoad no longer captures the RP list");
            Assert.True(load > capture, "the RP capture must precede the rewind save load");
        }

        [Fact]
        public void OnLoad_ReinstallsTheCarriedRps_AfterTheStagingStateLoad_BeforeTheRewindBranch()
        {
            string body = MethodBody(ReadSource("ParsekScenario.cs"),
                "public override void OnLoad(ConfigNode node)");
            int staging = body.IndexOf("LoadRewindStagingState(node);", StringComparison.Ordinal);
            int reinstall = body.IndexOf("RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(this);", StringComparison.Ordinal);
            int rewindBranch = body.IndexOf("HandleRewindOnLoad(node, recordings);", StringComparison.Ordinal);
            Assert.True(staging >= 0 && reinstall > staging,
                "OnLoad must reinstall the carried RPs after LoadRewindStagingState rebuilds the list");
            Assert.True(rewindBranch > reinstall,
                "the carried RPs must be back before the rewind branch runs");
        }
    }
}
