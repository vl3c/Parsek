using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pre-ledger progress seed: stock progress a career earned before Parsek was
    /// installed must survive the first milestone patch (it used to be un-achieved, which
    /// withdrew contracts gated on it and let stock pay it again), while a node the ledger
    /// owns anywhere on the timeline keeps today's patch behaviour.
    /// </summary>
    [Collection("Sequential")]
    public class PreLedgerProgressSeedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public PreLedgerProgressSeedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;

            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_progress_seed_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private string LedgerPath => Path.Combine(tempDir, "ledger.pgld");

        // ================================================================
        // Helpers
        // ================================================================

        private static GameAction MilestoneRow(string id, double ut, string recordingId = null)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                RecordingId = recordingId,
                MilestoneId = id,
                MilestoneFundsAwarded = 1000f
            };
        }

        private static GameStateEvent MilestoneEvent(string id, string recordingId)
        {
            return new GameStateEvent
            {
                ut = 500.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = id,
                detail = "funds=0;rep=0;sci=0",
                recordingId = recordingId ?? ""
            };
        }

        private static PreLedgerProgressSeed.NodeObservation OneShot(
            string qualifiedId, bool complete, string bareId = null)
        {
            return new PreLedgerProgressSeed.NodeObservation
            {
                QualifiedId = qualifiedId,
                BareId = bareId,
                IsComplete = complete
            };
        }

        private static PreLedgerProgressSeed.NodeObservation Record(
            string id, int impliedPaid, bool known = true)
        {
            return new PreLedgerProgressSeed.NodeObservation
            {
                QualifiedId = id,
                IsRepeatableRecord = true,
                ImpliedPaidCountKnown = known,
                ImpliedPaidCount = impliedPaid
            };
        }

        private static PreLedgerProgressSeed Capture(
            IReadOnlyList<PreLedgerProgressSeed.NodeObservation> nodes,
            IReadOnlyList<GameAction> rows = null,
            IReadOnlyList<GameStateEvent> events = null)
        {
            return PreLedgerProgressSeed.Capture(
                nodes, rows ?? new GameAction[0], events ?? new GameStateEvent[0], out _);
        }

        private static void SetFlags(ProgressNode node, bool reached, bool complete)
        {
            typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(node, reached);
            typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(node, complete);
        }

        private static void SetField<T>(object instance, string name, T value)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(instance, value);
        }

        private static T GetField<T>(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (T)field.GetValue(instance);
        }

        private static KspStatePatcher.RepeatableRecordState Forward(ProgressNode node, int hits)
        {
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(node, hits, out var state));
            return state;
        }

        /// <summary>Thresholds in the forward model: the smallest hit count whose band is complete.</summary>
        private static int ThresholdCount(ProgressNode node)
        {
            for (int k = 1; k < 100; k++)
            {
                if (Forward(node, k).Complete)
                    return k;
            }
            throw new InvalidOperationException("record node never completes");
        }

        private static void PatchTree(ProgressTree tree, MilestonesModule module,
            bool authoritative, out int credited, out int unreached, out int seedKept, out int seedRecordBands)
        {
            var reachedField = typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance);
            var mannedProp = typeof(ProgressNode).GetProperty("IsCompleteManned", BindingFlags.Public | BindingFlags.Instance);
            var unmannedProp = typeof(ProgressNode).GetProperty("IsCompleteUnmanned", BindingFlags.Public | BindingFlags.Instance);
            credited = 0;
            unreached = 0;
            int skipped = 0;
            seedKept = 0;
            seedRecordBands = 0;
            KspStatePatcher.PatchProgressNodeTree(tree, module, reachedField, completeField,
                mannedProp, unmannedProp, "", authoritative,
                ref credited, ref unreached, ref skipped, ref seedKept, ref seedRecordBands);
        }

        // ================================================================
        // Pure capture decision
        // ================================================================

        [Fact]
        public void Capture_CompleteNodeWithoutRowOrEvent_IsSeeded()
        {
            var seed = Capture(new[] { OneShot("FirstLaunch", complete: true) });

            Assert.True(seed.Captured);
            Assert.True(seed.ContainsOneShot("FirstLaunch"));
            Assert.Equal(1, seed.OneShotCount);
        }

        [Fact]
        public void Capture_CompleteNodeNamedByAnyLedgerRow_IsNotSeeded()
        {
            // A not-counted duplicate and a row far after any cutoff still name the node:
            // the ledger owns it somewhere on the timeline, so the seed must stay out.
            var notCounted = MilestoneRow("FirstLaunch", 100.0, "recA");
            notCounted.Effective = false;
            notCounted.NotCountedReason = GameActionNotCountedReason.MilestoneAlreadyAchieved;
            var future = MilestoneRow("ReachSpace", 9.0e9, "recFuture");

            var seed = Capture(
                new[]
                {
                    OneShot("FirstLaunch", complete: true),
                    OneShot("ReachSpace", complete: true),
                    OneShot("TowerBuzz", complete: true)
                },
                new[] { notCounted, future },
                null);

            Assert.False(seed.ContainsOneShot("FirstLaunch"));
            Assert.False(seed.ContainsOneShot("ReachSpace"));
            Assert.True(seed.ContainsOneShot("TowerBuzz"));
        }

        [Fact]
        public void Capture_SubtreeNodeNamedByLegacyBareRow_IsNotSeeded()
        {
            // The patcher credits "Mun/Landing" from a legacy bare "Landing" row, so the
            // capture treats that row as owning the node too.
            var seed = Capture(
                new[]
                {
                    OneShot("Mun/Landing", complete: true, bareId: "Landing"),
                    OneShot("Minmus/Orbit", complete: true, bareId: "Orbit")
                },
                new[] { MilestoneRow("Landing", 100.0, "recOld") },
                null);

            Assert.False(seed.ContainsOneShot("Mun/Landing"));
            Assert.True(seed.ContainsOneShot("Minmus/Orbit"));
        }

        [Fact]
        public void Capture_CompleteNodeWithPendingEvent_IsNotSeeded()
        {
            // A milestone a still-uncommitted flight earned: its row arrives at commit, so
            // seeding it would turn that row into a not-counted duplicate.
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[]
                {
                    OneShot("Kerbin/Orbit", complete: true, bareId: "Orbit"),
                    OneShot("FirstLaunch", complete: true)
                },
                new GameAction[0],
                new[] { MilestoneEvent("Kerbin/Orbit", "recLive") },
                out stats);

            Assert.False(seed.ContainsOneShot("Kerbin/Orbit"));
            Assert.True(seed.ContainsOneShot("FirstLaunch"));
            Assert.Equal(1, stats.ExcludedByEvent);
            Assert.Equal(1, stats.OneShotSeeded);
            Assert.Equal(2, stats.LiveComplete);
        }

        [Fact]
        public void Capture_IncompleteNode_IsNotSeeded()
        {
            var seed = Capture(new[] { OneShot("Mun/Landing", complete: false, bareId: "Landing") });

            Assert.True(seed.Captured);
            Assert.Equal(0, seed.OneShotCount);
        }

        [Fact]
        public void Capture_RecordNode_SeedsImpliedCountMinusRowsAndPendingEvents()
        {
            // Live band says 5 thresholds paid. One committed row (scope recA, whose event is
            // still in the store and must NOT count twice) owns one. Scope recB has no row
            // yet: each of its two events becomes its own row at commit (a coalesced award
            // event plus the completion event, as PWR-3 2026-10-03_1335 committed them), and
            // these details carry no threshold key, so each owns one. The other two predate
            // the ledger.
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[] { Record("RecordsAltitude", impliedPaid: 5) },
                new[] { MilestoneRow("RecordsAltitude", 100.0, "recA") },
                new[]
                {
                    MilestoneEvent("RecordsAltitude", "recA"),
                    MilestoneEvent("RecordsAltitude", "recB"),
                    MilestoneEvent("RecordsAltitude", "recB")
                },
                out stats);

            Assert.Equal(2, seed.GetRecordPaidCount("RecordsAltitude"));
            Assert.Equal(1, stats.RecordsSeeded);
            Assert.Equal(2, stats.RecordThresholdsSeeded);
        }

        [Fact]
        public void Capture_RecordNode_LedgerOwnsEveryPaidThreshold_IsNotSeeded()
        {
            // Includes the case where future rows exceed the live band (clamped at 0).
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[]
                {
                    Record("RecordsSpeed", impliedPaid: 1),
                    Record("RecordsDistance", impliedPaid: 0)
                },
                new[]
                {
                    MilestoneRow("RecordsSpeed", 100.0, "recA"),
                    MilestoneRow("RecordsSpeed", 9.0e9, "recFuture")
                },
                new GameStateEvent[0],
                out stats);

            Assert.True(seed.Captured);
            Assert.Equal(0, seed.RecordCount);
            Assert.Equal(1, stats.RecordsOwnedByLedger);
        }

        [Fact]
        public void Capture_RecordNodeWithUnreadableBand_IsNotSeeded()
        {
            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(
                new[] { Record("RecordsDepth", impliedPaid: 3, known: false) },
                new GameAction[0], new GameStateEvent[0], out stats);

            Assert.Equal(0, seed.GetRecordPaidCount("RecordsDepth"));
            Assert.Equal(1, stats.RecordsUnresolved);
        }

        [Theory]
        [InlineData(false, false, true, "ProgressTracking.Instance is null")]
        [InlineData(true, false, true, "OnLoad has not run")]
        [InlineData(true, true, false, "achievementTree is null")]
        [InlineData(true, true, true, null)]
        public void DescribeCaptureDeferral_WaitsForALoadedTracker(
            bool present, bool loaded, bool tree, string expectedFragment)
        {
            string reason = PreLedgerProgressSeed.DescribeCaptureDeferral(present, loaded, tree);

            if (expectedFragment == null)
                Assert.Null(reason);
            else
                Assert.Contains(expectedFragment, reason);
        }

        // ================================================================
        // Record band inverse
        // ================================================================

        [Fact]
        public void ImpliedPaidCount_InvertsTheForwardModelForEveryBand()
        {
            var nodes = new ProgressNode[]
            {
                new KSPAchievements.RecordsAltitude(),
                new KSPAchievements.RecordsDepth(),
                new KSPAchievements.RecordsSpeed(),
                new KSPAchievements.RecordsDistance()
            };

            foreach (var node in nodes)
            {
                int thresholds = ThresholdCount(node);
                for (int k = 0; k <= thresholds + 1; k++)
                {
                    var state = Forward(node, k);
                    Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                        node, state.Complete, state.Record, state.RewardThreshold, out int paid));
                    Assert.Equal(Math.Min(k, thresholds), paid);
                }
            }
        }

        [Fact]
        public void ImpliedPaidCount_ReadsStockLoadedShape()
        {
            // Stock persists only `record` and the flags, and re-derives
            // rewardThreshold = FindNextRecord(record) on load.
            var node = new KSPAchievements.RecordsDistance();
            double t1 = Forward(node, 1).Record;
            double t2 = Forward(node, 2).Record;
            int interval = 1;
            Func<double, double> stockThreshold = record =>
                FinePrint.Utilities.ProgressUtilities.FindNextRecord(record, 100000.0, 1000.0, ref interval);

            double inBandOne = (t1 + t2) / 2.0;
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                node, false, inBandOne, stockThreshold(inBandOne), out int paid));
            Assert.Equal(1, paid);

            double belowFirst = t1 / 2.0;
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                node, false, belowFirst, stockThreshold(belowFirst), out paid));
            Assert.Equal(0, paid);

            Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                node, false, t2, stockThreshold(t2), out paid));
            Assert.Equal(2, paid);

            // Unset threshold: the band the live best value sits in.
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                node, false, inBandOne, 0.0, out paid));
            Assert.Equal(1, paid);

            Assert.True(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                node, true, 100000.0, 0.0, out paid));
            Assert.Equal(ThresholdCount(node), paid);

            Assert.False(KspStatePatcher.TryComputeRepeatableRecordImpliedPaidCount(
                new ProgressNode("FirstLaunch", false), true, 0.0, 0.0, out paid));
        }

        [Fact]
        public void ObserveProgressTreeForSeed_QualifiesLikeThePatcherAndReadsRecordBands()
        {
            var tree = new ProgressTree();
            var firstLaunch = new ProgressNode("FirstLaunch", false);
            SetFlags(firstLaunch, true, true);
            tree.AddNode(firstLaunch);

            var mun = new ProgressNode("Mun", false);
            SetFlags(mun, true, true);
            var munLanding = new ProgressNode("Landing", false);
            SetFlags(munLanding, true, true);
            mun.Subtree.AddNode(munLanding);
            tree.AddNode(mun);

            var distance = new KSPAchievements.RecordsDistance();
            double t1 = Forward(distance, 1).Record;
            double t2 = Forward(distance, 2).Record;
            SetFlags(distance, true, false);
            SetField(distance, "record", (t1 + t2) / 2.0);
            SetField(distance, "rewardThreshold", t2);
            tree.AddNode(distance);

            var observed = KspStatePatcher.ObserveProgressTreeForSeed(tree);

            Assert.Equal(new[] { "FirstLaunch", "Mun", "Mun/Landing", "RecordsDistance" },
                observed.Select(o => o.QualifiedId).ToArray());
            var landing = observed.Single(o => o.QualifiedId == "Mun/Landing");
            Assert.Equal("Landing", landing.BareId);
            Assert.True(landing.IsComplete);
            Assert.Null(observed.Single(o => o.QualifiedId == "FirstLaunch").BareId);
            var record = observed.Single(o => o.QualifiedId == "RecordsDistance");
            Assert.True(record.IsRepeatableRecord);
            Assert.True(record.ImpliedPaidCountKnown);
            Assert.Equal(1, record.ImpliedPaidCount);
        }

        // ================================================================
        // MilestonesModule with a seed
        // ================================================================

        [Fact]
        public void Module_SeededNode_IsCreditedAndALaterRowIsATrueDuplicate()
        {
            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                new[] { "FirstLaunch" }, null));
            module.Reset();

            Assert.True(module.HasProgressSeed);
            Assert.True(module.IsMilestoneCredited("FirstLaunch"));

            // Stock can only award a seeded node again if something un-achieved it; the
            // first-hit-wins rule already zeroes that duplicate.
            var reAward = MilestoneRow("FirstLaunch", 200.0, "recA");
            var other = MilestoneRow("ReachSpace", 300.0, "recA");
            module.ProcessAction(reAward);
            module.ProcessAction(other);

            Assert.False(reAward.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, reAward.NotCountedReason);
            Assert.True(other.Effective);
            Assert.True(module.IsMilestoneCredited("ReachSpace"));
            Assert.Contains(logLines, l => l.Contains("[Milestones]")
                && l.Contains("Duplicate milestone 'FirstLaunch'")
                && l.Contains("pre-ledger progress seed"));
        }

        [Fact]
        public void Module_SeededRecordCount_StartsTheEffectiveCountAndRowsStayEffective()
        {
            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                null, new[] { new KeyValuePair<string, int>("RecordsAltitude", 3) }));
            module.Reset();

            Assert.Equal(3, module.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.False(module.IsMilestoneCredited("RecordsAltitude"));

            var first = MilestoneRow("RecordsAltitude", 100.0, "recA");
            var second = MilestoneRow("RecordsAltitude", 200.0, "recB");
            module.ProcessAction(first);
            module.ProcessAction(second);

            Assert.True(first.Effective);
            Assert.True(second.Effective);
            Assert.Equal(5, module.GetEffectiveMilestoneCount("RecordsAltitude"));

            // A re-walk starts from the seed again, not from the previous total.
            module.Reset();
            Assert.Equal(3, module.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        [Fact]
        public void Module_WithoutSeed_IsNotReadyAndBehavesAsBefore()
        {
            var module = new MilestonesModule();
            module.Reset();
            Assert.False(module.HasProgressSeed);
            Assert.Equal(0, module.GetCreditedCount());

            module.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(null, null));
            module.Reset();
            Assert.True(module.HasProgressSeed);
            Assert.Equal(0, module.GetCreditedCount());

            var row = MilestoneRow("FirstLaunch", 100.0, "recA");
            module.ProcessAction(row);
            Assert.True(row.Effective);
            Assert.Equal(1, module.GetEffectiveMilestoneCount("FirstLaunch"));
        }

        // ================================================================
        // Patch with a seed
        // ================================================================

        [Fact]
        public void PatchProgressNodeTree_KeepsSeededNodeAndStillClearsRewoundAwayNode()
        {
            // FirstLaunch predates the ledger (seeded). ReachSpace is complete live but its
            // only row lies after the walk cutoff (a rewind): unseeded, so it is still
            // cleared exactly as before.
            var tree = new ProgressTree();
            var firstLaunch = new ProgressNode("FirstLaunch", false);
            SetFlags(firstLaunch, true, true);
            tree.AddNode(firstLaunch);
            var reachSpace = new ProgressNode("ReachSpace", false);
            SetFlags(reachSpace, true, true);
            tree.AddNode(reachSpace);

            var seed = Capture(
                KspStatePatcher.ObserveProgressTreeForSeed(tree),
                new[] { MilestoneRow("ReachSpace", 9000.0, "recFuture") },
                null);
            Assert.True(seed.ContainsOneShot("FirstLaunch"));
            Assert.False(seed.ContainsOneShot("ReachSpace"));

            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(seed);
            module.Reset(); // the cutoff walk never reaches the UT 9000 row

            PatchTree(tree, module, authoritative: true,
                out int credited, out int unreached, out int seedKept, out _);

            Assert.True(firstLaunch.IsComplete);
            Assert.False(reachSpace.IsComplete);
            Assert.False(reachSpace.IsReached);
            Assert.Equal(0, credited);
            Assert.Equal(1, unreached);
            Assert.Equal(1, seedKept);
        }

        [Fact]
        public void PatchProgressNodeTree_DoesNotSetANodeTheLoadedTreeLacks()
        {
            // Rewind into a quicksave whose tree lacks M; M's only row is after the cutoff.
            // The capture must not seed M, and the patch must not set it.
            var tree = new ProgressTree();
            var m = new ProgressNode("TowerBuzz", false);
            tree.AddNode(m);

            var seed = Capture(
                KspStatePatcher.ObserveProgressTreeForSeed(tree),
                new[] { MilestoneRow("TowerBuzz", 9000.0, "recFuture") },
                null);
            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(seed);
            module.Reset();

            PatchTree(tree, module, authoritative: true, out int credited, out _, out _, out _);

            Assert.False(seed.ContainsOneShot("TowerBuzz"));
            Assert.False(m.IsComplete);
            Assert.Equal(0, credited);
        }

        [Fact]
        public void PatchProgressNodeTree_SeededRecordBandSurvives_UnseededIsReset()
        {
            var seeded = new KSPAchievements.RecordsDistance();
            double t2 = Forward(seeded, 2).Record;
            double t3 = Forward(seeded, 3).Record;
            SetFlags(seeded, true, false);
            SetField(seeded, "record", (t2 + t3) / 2.0);
            SetField(seeded, "rewardThreshold", t3);

            var tree = new ProgressTree();
            tree.AddNode(seeded);
            var seed = Capture(KspStatePatcher.ObserveProgressTreeForSeed(tree));
            Assert.Equal(2, seed.GetRecordPaidCount("RecordsDistance"));

            var module = new MilestonesModule();
            module.SetPreLedgerProgressSeed(seed);
            module.Reset();
            PatchTree(tree, module, authoritative: true, out _, out _, out _, out int seedRecordBands);

            Assert.True(seeded.IsReached);
            Assert.False(seeded.IsComplete);
            Assert.Equal(t3, GetField<double>(seeded, "rewardThreshold"), 6);
            Assert.Equal(1, seedRecordBands);

            // Without the seed the same live band is rebuilt from zero ledger hits.
            var unseeded = new KSPAchievements.RecordsDistance();
            SetFlags(unseeded, true, false);
            SetField(unseeded, "record", (t2 + t3) / 2.0);
            SetField(unseeded, "rewardThreshold", t3);
            var bareTree = new ProgressTree();
            bareTree.AddNode(unseeded);
            var bareModule = new MilestonesModule();
            bareModule.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(null, null));
            bareModule.Reset();
            PatchTree(bareTree, bareModule, authoritative: true, out _, out _, out _, out _);

            Assert.False(unseeded.IsReached);
            Assert.Equal(0.0, GetField<double>(unseeded, "record"), 6);
        }

        [Fact]
        public void PatchMilestones_SeedNotCaptured_SkipsBeforeTouchingAnything()
        {
            KspStatePatcher.PatchMilestones(new MilestonesModule());

            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("pre-ledger progress seed not captured yet"));
            Assert.DoesNotContain(logLines, l => l.Contains("ProgressTracking.Instance is null"));
            Assert.DoesNotContain(logLines, l => l.Contains("PatchMilestones: credited="));
        }

        // ================================================================
        // Orchestrator capture
        // ================================================================

        [Fact]
        public void EnsurePreLedgerProgressSeed_TrackerNotLoaded_DefersAndCapturesNothing()
        {
            LedgerOrchestrator.ProgressSeedProbeForTesting = () => new LedgerOrchestrator.ProgressSeedProbe
            {
                TrackerPresent = true,
                TrackerLoaded = false,
                Nodes = null
            };

            Assert.False(LedgerOrchestrator.EnsurePreLedgerProgressSeed());
            Assert.False(Ledger.ProgressSeed.Captured);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferring capture")
                && l.Contains("OnLoad has not run"));
        }

        [Fact]
        public void EnsurePreLedgerProgressSeed_CapturesOnceFromLedgerAndEvents()
        {
            Ledger.AddAction(MilestoneRow("ReachSpace", 100.0, "recA"));
            var pending = MilestoneEvent("Kerbin/Orbit", "recLive");
            GameStateStore.AddEvent(ref pending);
            LedgerOrchestrator.ProgressSeedProbeForTesting = () => new LedgerOrchestrator.ProgressSeedProbe
            {
                TrackerPresent = true,
                TrackerLoaded = true,
                Nodes = new List<PreLedgerProgressSeed.NodeObservation>
                {
                    OneShot("FirstLaunch", complete: true),
                    OneShot("ReachSpace", complete: true),
                    OneShot("Kerbin/Orbit", complete: true, bareId: "Orbit"),
                    Record("RecordsAltitude", impliedPaid: 2)
                }
            };

            Assert.True(LedgerOrchestrator.EnsurePreLedgerProgressSeed());

            var seed = Ledger.ProgressSeed;
            Assert.True(seed.Captured);
            Assert.Equal(new[] { "FirstLaunch" }, seed.OneShotIds.ToArray());
            Assert.Equal(2, seed.GetRecordPaidCount("RecordsAltitude"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerOrchestrator]")
                && l.Contains("PreLedgerProgressSeed: captured")
                && l.Contains("seededNodes=1")
                && l.Contains("excludedByLedgerRow=1")
                && l.Contains("excludedByEvent=1")
                && l.Contains("seededRecordThresholds=2"));

            // Captured once: a later live tree (already patched by the ledger) never replaces it.
            LedgerOrchestrator.ProgressSeedProbeForTesting = () => new LedgerOrchestrator.ProgressSeedProbe
            {
                TrackerPresent = true,
                TrackerLoaded = true,
                Nodes = new List<PreLedgerProgressSeed.NodeObservation> { OneShot("TowerBuzz", complete: true) }
            };
            Assert.True(LedgerOrchestrator.EnsurePreLedgerProgressSeed());
            Assert.Same(seed, Ledger.ProgressSeed);
        }

        [Fact]
        public void RecalculateAndPatch_HandsTheSeedToTheWalk()
        {
            // End to end through the recalc: FirstLaunch predates the ledger, ReachSpace's
            // only row lies after the cutoff, and a re-award of FirstLaunch is zeroed.
            Ledger.AddAction(MilestoneRow("ReachSpace", 200.0, "recFuture"));
            LedgerOrchestrator.ProgressSeedProbeForTesting = () => new LedgerOrchestrator.ProgressSeedProbe
            {
                TrackerPresent = true,
                TrackerLoaded = true,
                Nodes = new List<PreLedgerProgressSeed.NodeObservation>
                {
                    OneShot("FirstLaunch", complete: true),
                    OneShot("ReachSpace", complete: true)
                }
            };

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0);

            Assert.True(LedgerOrchestrator.Milestones.HasProgressSeed);
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstLaunch"));
            Assert.False(LedgerOrchestrator.Milestones.IsMilestoneCredited("ReachSpace"));

            var reAward = MilestoneRow("FirstLaunch", 120.0, "recLater");
            Ledger.AddAction(reAward);
            LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0);

            Assert.False(reAward.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, reAward.NotCountedReason);
        }

        [Fact]
        public void RecalculateAndPatch_BeforeCapture_LeavesTheModuleUnseeded()
        {
            LedgerOrchestrator.ProgressSeedProbeForTesting = () => new LedgerOrchestrator.ProgressSeedProbe();
            Ledger.AddAction(MilestoneRow("FirstLaunch", 100.0, "recA"));

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.False(Ledger.ProgressSeed.Captured);
            Assert.False(LedgerOrchestrator.Milestones.HasProgressSeed);
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstLaunch"));
            Assert.Contains(logLines, l => l.Contains("pre-ledger progress seed not captured yet"));
        }

        // ================================================================
        // Persistence
        // ================================================================

        [Fact]
        public void SaveLoad_CapturedSeed_RoundTripsBesideTheActions()
        {
            Ledger.AddAction(MilestoneRow("ReachSpace", 100.0, "recA"));
            Assert.True(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                new[] { "Mun/Landing", "FirstLaunch" },
                new[] { new KeyValuePair<string, int>("RecordsAltitude", 3) })));

            Assert.True(Ledger.SaveToFile(LedgerPath));
            string text = File.ReadAllText(LedgerPath);
            Assert.Contains(PreLedgerProgressSeed.NodeName, text);
            Assert.Contains("captured = True", text);
            Assert.Contains("paidCount = 3", text);

            Ledger.ResetForTesting();
            Assert.False(Ledger.ProgressSeed.Captured);
            Assert.True(Ledger.LoadFromFile(LedgerPath));

            Assert.Single(Ledger.Actions);
            var seed = Ledger.ProgressSeed;
            Assert.True(seed.Captured);
            Assert.True(seed.ContainsOneShot("FirstLaunch"));
            Assert.True(seed.ContainsOneShot("Mun/Landing"));
            Assert.Equal(2, seed.OneShotCount);
            Assert.Equal(3, seed.GetRecordPaidCount("RecordsAltitude"));
        }

        [Fact]
        public void SaveLoad_CapturedEmptySeed_StaysCapturedAndAbsentSeedStaysAbsent()
        {
            Assert.True(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.CreateCaptured(null, null)));
            Assert.True(Ledger.SaveToFile(LedgerPath));
            Ledger.ResetForTesting();
            Assert.True(Ledger.LoadFromFile(LedgerPath));
            Assert.True(Ledger.ProgressSeed.Captured);
            Assert.Equal(0, Ledger.ProgressSeed.OneShotCount);

            // A ledger written before the seed existed (or before its capture) has no node.
            Ledger.ResetForTesting();
            Ledger.AddAction(MilestoneRow("FirstLaunch", 100.0, "recA"));
            Assert.True(Ledger.SaveToFile(LedgerPath));
            Assert.DoesNotContain(PreLedgerProgressSeed.NodeName, File.ReadAllText(LedgerPath));
            Assert.True(Ledger.LoadFromFile(LedgerPath));
            Assert.False(Ledger.ProgressSeed.Captured);
            Assert.Single(Ledger.Actions);
        }

        [Fact]
        public void Load_SeedNodeWithoutCapturedMarker_IsNotCapturedAndWarns()
        {
            var root = new ConfigNode("LEDGER");
            var node = root.AddNode(PreLedgerProgressSeed.NodeName);
            node.AddValue(PreLedgerProgressSeed.OneShotKey, "FirstLaunch");
            var record = node.AddNode(PreLedgerProgressSeed.RecordNodeName);
            record.AddValue(PreLedgerProgressSeed.RecordIdKey, "RecordsSpeed");
            record.AddValue(PreLedgerProgressSeed.RecordPaidCountKey, "x");

            var seed = PreLedgerProgressSeed.LoadFrom(root, out int malformed);
            Assert.False(seed.Captured);
            Assert.Equal(1, malformed);

            node.AddValue(PreLedgerProgressSeed.CapturedKey, "True");
            seed = PreLedgerProgressSeed.LoadFrom(root, out malformed);
            Assert.True(seed.Captured);
            Assert.True(seed.ContainsOneShot("FirstLaunch"));
            Assert.Equal(0, seed.RecordCount);
            Assert.Equal(1, malformed);
        }

        [Fact]
        public void ResetPaths_ClearTheSeed_ButARestoringClearKeepsIt()
        {
            Assert.True(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                new[] { "FirstLaunch" }, null)));

            // Clear() + AddActions is the rewind bundle restore of the SAME save.
            Ledger.Clear();
            Assert.True(Ledger.ProgressSeed.Captured);

            // A different save's ledger (or no ledger file at all) starts uncaptured.
            Assert.True(Ledger.LoadFromFile(Path.Combine(tempDir, "missing.pgld")));
            Assert.False(Ledger.ProgressSeed.Captured);

            Assert.True(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.CreateCaptured(null, null)));
            LedgerOrchestrator.ResetForTesting();
            Assert.False(Ledger.ProgressSeed.Captured);
        }

        [Fact]
        public void TrySetProgressSeed_RefusesASecondCaptureAndANotCapturedSeed()
        {
            var first = PreLedgerProgressSeed.CreateCaptured(new[] { "FirstLaunch" }, null);
            Assert.False(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.NotCaptured));
            Assert.True(Ledger.TrySetProgressSeed(first));
            Assert.False(Ledger.TrySetProgressSeed(
                PreLedgerProgressSeed.CreateCaptured(new[] { "TowerBuzz" }, null)));
            Assert.Same(first, Ledger.ProgressSeed);
        }
    }
}
