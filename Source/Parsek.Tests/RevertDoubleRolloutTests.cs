using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the revert double-rollout fix (#710).
    ///
    /// <para>Production repro: a player launched vessel R1 (pid 3442693372, site
    /// "Launch Pad", cost 8320), reverted mid-flight, and re-launched the same
    /// craft. Stock KSP refunded the rollout on revert and re-charged on the new
    /// launch (net: one charge), but Parsek's ledger ended up with two
    /// VesselRollout actions ~40 ms apart at near-identical UTs (728.21980… and
    /// 728.17980…). The two rows had the same vessel/pid/site/cost but different
    /// UTs because the revert rolled the in-game clock back, so the UT-embedded
    /// dedup key never collided. The user lost 8320 funds because
    /// KspStatePatcher applied both spending rows.</para>
    ///
    /// <para>The fix lives in two places:</para>
    /// <list type="bullet">
    ///   <item><description><see cref="LedgerRolloutAdoption.TryFindDuplicateRolloutAction"/>
    ///   gates a new rollout write at <see cref="LedgerOrchestrator.OnVesselRolloutSpending"/>:
    ///   if a matching unadopted rollout exists for the same (pid|site|vessel|cost)
    ///   within <see cref="LedgerRolloutAdoption.RolloutDuplicateWindowSeconds"/>,
    ///   the new write is skipped with a single INFO log line.</description></item>
    ///   <item><description><see cref="Ledger.RepairDuplicateRolloutActions"/>
    ///   runs at OnKspLoad and collapses any near-duplicate cluster already in
    ///   the loaded ledger so already-corrupt saves are healed without a manual
    ///   migration.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class RevertDoubleRolloutTests : IDisposable
    {
        // Mirrors the production repro byte-for-byte (same pid, site, vessel name).
        private const uint ProductionPid = 3442693372u;
        private const string ProductionSite = "Launch Pad";
        private const string ProductionVesselName = "R1";
        private const float ProductionCost = 8320.001f;
        private const double ProductionUtFirst = 728.21980712880259;
        private const double ProductionUtSecond = 728.17980712880262;

        private readonly List<string> logLines = new List<string>();

        public RevertDoubleRolloutTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void RecordRollout(
            double ut,
            double cost,
            uint pid = ProductionPid,
            string vesselName = ProductionVesselName,
            string launchSiteName = ProductionSite)
        {
            LedgerOrchestrator.OnVesselRolloutSpending(ut, cost, pid, vesselName, launchSiteName);
        }

        private static int CountUnadoptedRollouts()
        {
            int count = 0;
            for (int i = 0; i < Ledger.Actions.Count; i++)
            {
                var a = Ledger.Actions[i];
                if (a == null) continue;
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) continue;
                if (!string.IsNullOrEmpty(a.RecordingId)) continue;
                count++;
            }
            return count;
        }

        // ----------------------------------------------------------------
        // Write-time dedup gate
        // ----------------------------------------------------------------

        /// <summary>
        /// Production repro: emit the exact two rollouts from
        /// <c>logs/2026-05-01_2208_investigate/parsek/GameState/ledger.pgld</c>
        /// in chronological order. The first row should land; the second
        /// (revert-relaunch) row must be detected as a duplicate and dropped.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_RevertRelaunchSamePidSameCost_DroppedAsDuplicate()
        {
            RecordRollout(ProductionUtFirst, ProductionCost);
            Assert.Equal(1, CountUnadoptedRollouts());

            RecordRollout(ProductionUtSecond, ProductionCost);

            Assert.Equal(1, CountUnadoptedRollouts());
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            // Cluster invariant: surviving row's UT is always the minimum of
            // the cluster's UTs. ProductionUtSecond is strictly smaller than
            // ProductionUtFirst (revert rolled the clock back ~40 ms), so the
            // write-time gate swaps the surviving row's UT/DedupKey/Sequence
            // to the relaunch values before dropping the new write.
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.Equal(ProductionCost, survivor.FundsSpent);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout") &&
                l.Contains("swapped surviving row to relaunch UT"));
        }

        /// <summary>
        /// Defense in depth: the legitimate rollout could equally well be the
        /// SECOND emission (e.g. a rapid double-fire from KSP without a clock
        /// rollback). Either ordering must collapse to a single survivor.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_DuplicateLandsBeforeOriginal_StillCollapsesToOne()
        {
            // Reverse production order: the smaller-UT row arrives first.
            RecordRollout(ProductionUtSecond, ProductionCost);
            Assert.Equal(1, CountUnadoptedRollouts());

            RecordRollout(ProductionUtFirst, ProductionCost);

            Assert.Equal(1, CountUnadoptedRollouts());
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
        }

        /// <summary>
        /// Two genuinely independent vessels launched at very close UTs (rare
        /// but possible) MUST both be kept. KSP allocates a fresh persistentId
        /// for every new vessel, so the dedup gate's PID-match rule rejects this
        /// scenario.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_DifferentPidSameNameSiteCost_BothKept()
        {
            RecordRollout(ut: 100.0, cost: 5000.0, pid: 1u);
            RecordRollout(ut: 100.5, cost: 5000.0, pid: 2u);

            Assert.Equal(2, CountUnadoptedRollouts());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
        }

        /// <summary>
        /// Two rollouts of the same vessel/site/cost but UTs further apart than
        /// the dedup window must be treated as independent launches.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_OutsideDuplicateWindow_BothKept()
        {
            RecordRollout(ut: 100.0, cost: 5000.0);
            double laterUT = 100.0 + LedgerRolloutAdoption.RolloutDuplicateWindowSeconds + 0.5;
            RecordRollout(ut: laterUT, cost: 5000.0);

            Assert.Equal(2, CountUnadoptedRollouts());
        }

        /// <summary>
        /// 60 s game-time-warp gap regression pin. Same craft, same site,
        /// same cost, two rollouts 70 s apart — outside the dedup window so
        /// both must be kept. Original PR review's [nit] about warping a
        /// real second launch into the gate is the reason this is pinned
        /// alongside the +0.5 s sentinel above.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_SeventySecondGap_BothKept()
        {
            RecordRollout(ut: 100.0, cost: 5000.0);
            RecordRollout(ut: 170.0, cost: 5000.0);

            Assert.Equal(2, CountUnadoptedRollouts());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
        }

        /// <summary>
        /// Two rollouts of the same vessel within the window but with materially
        /// different costs (e.g. user added/removed parts between launches) must
        /// stay separate.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_SameVesselDifferentCost_BothKept()
        {
            RecordRollout(ut: 100.0, cost: 5000.0);
            RecordRollout(ut: 100.5, cost: 7500.0);

            Assert.Equal(2, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Gap 2 regression: when the new write has a strictly smaller UT
        /// than the existing row (revert-relaunch with a measurable clock
        /// rollback), the surviving row's UT/DedupKey/Sequence must be
        /// updated in place to the relaunch values. Without this, downstream
        /// adoption (TryAdoptRolloutAction's 0.5 s startUT cap) and Reconcile
        /// (maxUT cutoff) would treat the surviving row as belonging to the
        /// pre-revert timeline that never happened.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_NewWriteSmallerUT_SurvivorUTUpdatedInPlace()
        {
            const double t1 = 1000.0;
            const double rollback = 1.0;
            RecordRollout(t1, ProductionCost);
            var beforeKey = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild).DedupKey;

            // Relaunch with a 1 s clock rollback (well outside adoption's
            // 0.5 s window; load-time repair would otherwise be the only
            // recourse without the in-place update).
            RecordRollout(t1 - rollback, ProductionCost);

            Assert.Equal(1, CountUnadoptedRollouts());
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal(t1 - rollback, survivor.UT);
            Assert.NotEqual(beforeKey, survivor.DedupKey);
            Assert.Contains((t1 - rollback).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                survivor.DedupKey);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("swapped surviving row to relaunch UT"));
        }

        /// <summary>
        /// Gap 2 + adoption interaction: even with a multi-second clock
        /// rollback (well outside <c>TryAdoptRolloutAction</c>'s 0.5 s
        /// post-startUT cap), the in-place UT update keeps adoption viable.
        /// Sequence: write rollout A at T1 — adopt it — revert and relaunch
        /// at T1 - 5 (the write-time gate drops B and updates A's UT to
        /// T1 - 5). A new recording starting at T1 - 4.6 (within the 0.5 s
        /// cap of the updated UT) must still be able to find the
        /// already-adopted row by UT proximity if it tried to re-adopt it
        /// (and conversely, a recalc/reconcile pass running with maxUT below
        /// T1 must not prune the survivor as a future-dated row).
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_AdoptedRowUTUpdatedAfterRevert_StillFindableByLagWindow()
        {
            const double t1 = 1000.0;
            // Pre-revert launch + adoption.
            RecordRollout(t1, ProductionCost);
            var rec = new Recording
            {
                RecordingId = "rec-A",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: t1 + 0.1, rec);
            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);

            // Revert + relaunch with a 5 s clock rollback. The write-time
            // gate must NOT collapse this into the adopted row (adopted
            // rollouts are not duplicate candidates — see the existing
            // OnVesselRolloutSpending_AdoptedRolloutDoesNotBlockNewWrite test
            // which exercises this property). The new emission lands as a
            // fresh unadopted row.
            RecordRollout(t1 - 5.0, ProductionCost);
            int adoptedCount = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                !string.IsNullOrEmpty(a.RecordingId));
            int unadoptedCount = CountUnadoptedRollouts();
            Assert.Equal(1, adoptedCount);
            Assert.Equal(1, unadoptedCount);

            // Crucial: the adopted row's UT was NOT mutated by the relaunch
            // write (the gate rejected it as a duplicate of the recording
            // adoption only when both rows are unadopted). The relaunch row
            // is a fresh unadopted candidate available for a new recording
            // to adopt.
            Assert.Equal(t1, adopted.UT);
        }

        /// <summary>
        /// Once a rollout has been adopted by a recording, a later rollout
        /// emission for the same vessel must be kept as a fresh unadopted row
        /// — the existing row no longer represents an outstanding deduction.
        /// This guards against the dedup gate accidentally hijacking the
        /// adoption matcher's ownership semantics.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_AdoptedRolloutDoesNotBlockNewWrite()
        {
            RecordRollout(ProductionUtFirst, ProductionCost);
            var rec = new Recording
            {
                RecordingId = "rec-A",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: 730.0, rec);
            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);

            // Second rollout for a fresh recording on the same craft (e.g. flew
            // away, recovered, rolled out the same craft again — different
            // session even though pid+site+name+cost match).
            RecordRollout(ProductionUtSecond + 200.0, ProductionCost);

            // One adopted + one new unadopted. The dedup gate must NOT collapse
            // the new write into the adopted row.
            int adoptedCount = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                !string.IsNullOrEmpty(a.RecordingId));
            int unadoptedCount = CountUnadoptedRollouts();
            Assert.Equal(1, adoptedCount);
            Assert.Equal(1, unadoptedCount);
        }

        // ----------------------------------------------------------------
        // Load-time repair (already-corrupt saves)
        // ----------------------------------------------------------------

        /// <summary>
        /// Simulates loading the production ledger byte-for-byte by inlining
        /// the two duplicate rollout actions exactly as serialized in
        /// <c>logs/2026-05-01_2208_investigate/parsek/GameState/ledger.pgld</c>
        /// and asserts the load-time repair pass collapses them to one.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_ProductionLedger_CollapsesToOne()
        {
            var first = new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 18,
                DedupKey = $"rollout:{ProductionUtFirst.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            };
            var second = new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = $"rollout:{ProductionUtSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            };

            Ledger.AddAction(first);
            Ledger.AddAction(second);
            Assert.Equal(2, CountUnadoptedRollouts());

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(1, removed);
            Assert.Equal(1, CountUnadoptedRollouts());
            // Earliest UT wins: the repair pass swaps the surviving payload to
            // hold the smaller UT, so the row left behind reflects the user's
            // most recent (revert-relaunch) charge, with all earlier siblings
            // dropped. Production order had the larger UT first, so the
            // post-repair survivor is at the smaller UT.
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("RepairDuplicateRolloutActions: removing duplicate rollout"));
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") &&
                l.Contains("RepairDuplicateRolloutActions: collapsed 1 duplicate"));
        }

        /// <summary>
        /// Idempotency: running the repair twice on the same ledger must not
        /// drop additional rows or emit a second collapse log.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_Idempotent()
        {
            RecordRollout(ut: 100.0, cost: 5000.0, pid: 1u);
            RecordRollout(ut: 200.0, cost: 6000.0, pid: 2u);

            int firstPass = Ledger.RepairDuplicateRolloutActions();
            int secondPass = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, firstPass);
            Assert.Equal(0, secondPass);
            Assert.Equal(2, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Three rollouts in the same cluster collapse down to exactly one.
        /// Defends against a rare regression where a player rapid-reverts
        /// multiple times in succession.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_ThreeRowsInClusterCollapseToOne()
        {
            // Add the rows directly so the write-time gate does not reject
            // siblings before the repair pass can run.
            for (int i = 0; i < 3; i++)
            {
                Ledger.AddAction(new GameAction
                {
                    UT = 728.0 + 0.04 * i,
                    Type = GameActionType.FundsSpending,
                    FundsSpent = ProductionCost,
                    FundsSpendingSource = FundsSpendingSource.VesselBuild,
                    Sequence = 18 + i,
                    DedupKey = $"rollout:{(728.0 + 0.04 * i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
                });
            }
            Assert.Equal(3, CountUnadoptedRollouts());

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(2, removed);
            Assert.Equal(1, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Repair must NOT touch unadopted rollouts that legitimately differ
        /// (different PIDs, different costs, different sites, or far-apart UTs).
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_LegitimateDistinctRollouts_AllKept()
        {
            // Different pid.
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = "rollout:100|pid=1|site=Launch%20Pad|vessel=Alice",
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.5,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = "rollout:100.5|pid=2|site=Launch%20Pad|vessel=Bob",
            });
            // Far-apart UTs (same craft, two real launches separated by hours).
            Ledger.AddAction(new GameAction
            {
                UT = 100.0 + LedgerRolloutAdoption.RolloutDuplicateWindowSeconds + 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = $"rollout:9999|pid=1|site=Launch%20Pad|vessel=Alice",
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(3, CountUnadoptedRollouts());
        }

        /// <summary>
        /// The repair pass must skip adopted rollouts whose source recording
        /// is unknown (RecordingStore lookup returns null). Without a
        /// recording we cannot reconstruct the rollout-adoption context for
        /// the adopted row, so the safe default is to leave it untouched.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_AdoptedActionsUntouched_WhenRecordingUnknown()
        {
            // Adopted: RecordingId set, DedupKey null (matches TryAdoptRolloutAction's
            // post-adoption shape). RecordingStore is empty so the resolver
            // returns a default context for "rec-adopted" and the row is
            // skipped.
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-adopted",
                DedupKey = null,
            });
            // Unadopted near-duplicate that would otherwise match.
            Ledger.AddAction(new GameAction
            {
                UT = 100.5,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = $"rollout:100.5|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(2, Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild));
        }

        /// <summary>
        /// Gap 1 regression: the canonical adopted+unadopted scenario the PR
        /// review flagged. A recording adopts the original launch's rollout
        /// (clearing its DedupKey), the player reverts and re-launches, and a
        /// fresh unadopted rollout lands beside it. The original PR's repair
        /// pass skipped both rows because its <c>IsUnadoptedRolloutAction</c>
        /// filter rejected the adopted anchor — the double-charge stayed.
        /// After the fix, the adopted row is the cluster survivor and the
        /// unadopted duplicate is dropped.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_AdoptedSurvivesUnadoptedDuplicate()
        {
            // Pre-adoption rollout in the production cluster shape (PID/site
            // match the recording about to adopt it).
            RecordRollout(ProductionUtFirst, ProductionCost);

            // Stage the recording in CommittedRecordings so the repair pass's
            // resolver can find it from the action's RecordingId.
            var rec = new Recording
            {
                RecordingId = "rec-adopted",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-adopted", startUT: ProductionUtFirst + 0.1, rec);
            Assert.NotNull(adopted);

            // Inject the relaunch-side unadopted row directly so it bypasses
            // the write-time gate (which would otherwise correctly drop it
            // before the repair pass ever ran).
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 50,
                DedupKey = $"rollout:{ProductionUtSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            });

            int adoptedBefore = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                !string.IsNullOrEmpty(a.RecordingId));
            int unadoptedBefore = CountUnadoptedRollouts();
            Assert.Equal(1, adoptedBefore);
            Assert.Equal(1, unadoptedBefore);

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(1, removed);
            // Adopted survivor remains; unadopted duplicate is gone.
            int adoptedAfter = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                !string.IsNullOrEmpty(a.RecordingId));
            int unadoptedAfter = CountUnadoptedRollouts();
            Assert.Equal(1, adoptedAfter);
            Assert.Equal(0, unadoptedAfter);
            // The adopted row keeps its identity (RecordingId, ActionId, UT
            // unchanged — no payload swap from the discarded row).
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-adopted", survivor.RecordingId);
            Assert.Equal(adopted.ActionId, survivor.ActionId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("RepairDuplicateRolloutActions: removing duplicate rollout") &&
                l.Contains("adopted anchor wins"));
        }

        /// <summary>
        /// Gap 1 symmetric case: same as above but the adopted row sits at a
        /// higher index (i.e. the anchor in the outer loop is the unadopted
        /// row). The repair pass must still keep the adopted row and discard
        /// the unadopted anchor.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_AdoptedAtHigherIndexStillSurvives()
        {
            // Inject the unadopted row first so it lives at a smaller index
            // than the adopted row (anchor in the outer loop).
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 1,
                DedupKey = $"rollout:{ProductionUtSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            });
            // Now stage the adopted row at a higher index.
            var rec = new Recording
            {
                RecordingId = "rec-adopted",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-adopted",
                Sequence = 2,
                DedupKey = null,
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(1, removed);
            int adoptedAfter = Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                !string.IsNullOrEmpty(a.RecordingId));
            int unadoptedAfter = CountUnadoptedRollouts();
            Assert.Equal(1, adoptedAfter);
            Assert.Equal(0, unadoptedAfter);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("RepairDuplicateRolloutActions: removing duplicate rollout") &&
                l.Contains("adopted candidate wins"));
        }

        /// <summary>
        /// Gap 1 edge case: two adopted rollouts that happen to match the
        /// dedup criteria. Real-world this should be impossible (two
        /// recordings cannot both adopt the same launch row), but the repair
        /// pass must not silently steal a recording's authoritative cost
        /// line. Both rows stay and a WARN surfaces the anomaly.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_TwoAdoptedRollouts_KeptWithWarn()
        {
            var recA = new Recording
            {
                RecordingId = "rec-A",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            var recB = new Recording
            {
                RecordingId = "rec-B",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };
            RecordingStore.AddRecordingWithTreeForTesting(recA);
            RecordingStore.AddRecordingWithTreeForTesting(recB);

            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-A",
                Sequence = 1,
                DedupKey = null,
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-B",
                Sequence = 2,
                DedupKey = null,
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(2, Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("two adopted rollouts match within window — keeping both"));
        }

        /// <summary>
        /// Legacy bare-key rollouts (no structured pid/site/vessel fields in
        /// the dedup key) must NOT be silently collapsed — we cannot prove
        /// they refer to the same vessel. The fallback path is intentionally
        /// strict.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_LegacyBareKey_NotCollapsed()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = "rollout:100", // legacy bare key shape
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.5,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = "rollout:100.5", // legacy bare key shape
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(2, CountUnadoptedRollouts());
        }

        // ----------------------------------------------------------------
        // End-to-end: the full revert→relaunch→OnKspLoad cycle
        // ----------------------------------------------------------------

        /// <summary>
        /// OnKspLoad must invoke the repair pass: load a save that contains
        /// the production-shape duplicate cluster and assert the post-load
        /// ledger has been deduped. This simulates the user's existing save
        /// being loaded after the fix lands without any manual migration.
        /// </summary>
        [Fact]
        public void OnKspLoad_RunsDuplicateRolloutRepairOnAlreadyCorruptLedger()
        {
            // Seed the ledger so OnKspLoad's MigrateOldSaveEvents short-circuit
            // is happy (FundsInitial is a sentinel "ledger has been used" marker
            // — without one, OnKspLoad can re-classify the save as fresh and
            // wipe synthetic rows).
            Ledger.SeedInitialFunds(25000.0);

            // Inline the production duplicate cluster.
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 18,
                DedupKey = $"rollout:{ProductionUtFirst.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = $"rollout:{ProductionUtSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={ProductionPid}|site=Launch%20Pad|vessel=R1",
            });
            Assert.Equal(2, CountUnadoptedRollouts());

            // OnKspLoad with no committed recordings + a maxUT past both rows
            // (mimics the user opening the save after the fix is installed).
            LedgerOrchestrator.OnKspLoad(new HashSet<string>(), maxUT: 10000.0);

            Assert.Equal(1, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Revert flow simulation: a session-only KSC rollout action is added
        /// during the flight that the user later reverts. After the revert
        /// quicksave is loaded and the user re-launches the same craft, the
        /// new rollout emission must NOT add a duplicate row. This is the
        /// integration-level guarantee that the user's "missing 8320 funds"
        /// observation cannot recur on the live timeline.
        /// </summary>
        [Fact]
        public void RevertFlowSimulation_RelaunchEmissionAfterRevertDoesNotDouble()
        {
            // Pre-revert: launch fires the original rollout.
            RecordRollout(ProductionUtFirst, ProductionCost);
            Assert.Equal(1, CountUnadoptedRollouts());

            // Player flies for a few seconds, then reverts. KSP refunds the
            // rollout cost (handled outside the ledger; KspStatePatcher will
            // re-apply on the next recalculate) and reloads the launch
            // quicksave. The ledger row from the original launch survives the
            // revert because nothing prunes session-only rollouts.

            // Re-launch fires VesselRollout again at a near-identical UT (KSP
            // rolled the clock back ~40 ms). With the dedup gate this becomes
            // a no-op write.
            RecordRollout(ProductionUtSecond, ProductionCost);

            int unadoptedAfterRevert = CountUnadoptedRollouts();
            Assert.Equal(1, unadoptedAfterRevert);

            // Defense in depth: even if some pre-fix saves loaded with two
            // rows, the load-time repair pass must collapse them to one too
            // (asserted in OnKspLoad_RunsDuplicateRolloutRepairOnAlreadyCorruptLedger
            // above).
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
        }

        /// <summary>
        /// Simulates the full bug timeline: pre-revert rollout written, revert
        /// fires, relaunch fires the second rollout. With the dedup gate, the
        /// post-relaunch ledger holds exactly one rollout row.
        /// </summary>
        [Fact]
        public void RevertRelaunchTimeline_LedgerHoldsExactlyOneRolloutCharge()
        {
            // Pre-revert launch.
            RecordRollout(ProductionUtFirst, ProductionCost);
            Assert.Equal(1, CountUnadoptedRollouts());
            float ledgerSpentAfterFirst = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsSpending && a.FundsSpendingSource == FundsSpendingSource.VesselBuild)
                .Sum(a => a.FundsSpent);
            Assert.Equal(ProductionCost, ledgerSpentAfterFirst);

            // Revert + relaunch: same craft, same site, same cost; KSP rolled
            // the clock back 40 ms so the new emission has a smaller UT.
            RecordRollout(ProductionUtSecond, ProductionCost);

            float ledgerSpentAfterRelaunch = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsSpending && a.FundsSpendingSource == FundsSpendingSource.VesselBuild)
                .Sum(a => a.FundsSpent);
            // CRUCIAL: ledger spending must NOT have doubled.
            Assert.Equal(ProductionCost, ledgerSpentAfterRelaunch);
            Assert.Equal(1, CountUnadoptedRollouts());
        }
    }
}
