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
    ///   if an unadopted rollout of the same logical rollout (equal launch guids,
    ///   or the #710 pid / name + site match when a guid is unknown; same cost)
    ///   exists within <see cref="LedgerRolloutAdoption.RolloutDuplicateWindowSeconds"/>,
    ///   the new write is skipped with a single INFO log line.</description></item>
    ///   <item><description><see cref="Ledger.RepairDuplicateRolloutActions"/>
    ///   runs at OnKspLoad and collapses any such cluster already in the loaded
    ///   ledger, by the same rule.</description></item>
    /// </list>
    ///
    /// <para>RELAUNCH-ROLLOUT-DEDUPED-AS-REVERT (CS-3b, 2026-10-26): the persistentId
    /// is craft-baked, so pid + site + cost inside the window also matched a genuine
    /// relaunch of the same craft and dropped its charge. Rows now carry the launch
    /// guid (<c>Vessel.id</c>, fresh per launch); known and differing guids are never
    /// a duplicate. Stock fires <c>OnVesselRollout</c> only for <c>NEW_FROM_FILE</c>, so
    /// the #710 revert-to-editor + relaunch pair carries two different guids and the
    /// revert prune owns it (<see cref="RevertToEditorThenRelaunch_FreshGuid_PruneLeavesOneRollout"/>).</para>
    /// </summary>
    [Collection("Sequential")]
    public class RevertDoubleRolloutTests : IDisposable
    {
        // Mirrors the production repro (same pid, site, vessel name).
        private const uint ProductionPid = 3442693372u;
        private const string ProductionSite = "Launch Pad";
        private const string ProductionVesselName = "R1";
        private const float ProductionCost = 8320.001f;
        private const double ProductionUtFirst = 728.21980712880259;
        private const double ProductionUtSecond = 728.17980712880262;
        // Launch guids in canonical "N" form.
        private const string ProductionGuid = "5d80f8dfbd9d4084b5c00e762c881410";
        private const string RelaunchGuid = "836ca8fa1e5f4571abf9291afd1b43f9";

        private readonly List<string> logLines = new List<string>();

        private static string Key(
            double ut,
            string guid = ProductionGuid,
            uint pid = ProductionPid,
            string site = "Launch%20Pad",
            string vessel = "R1")
        {
            string key = $"rollout:{ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|pid={pid}|site={site}|vessel={vessel}";
            return guid == null ? key : key + "|guid=" + guid;
        }

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
            ParsekScenario.SetPrelaunchEntryLedgerActionIdsForTesting(null);
            ParsekScenario.SetFlightEntryLedgerActionIdsForTesting(null);
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
            string launchSiteName = ProductionSite,
            string guid = ProductionGuid)
        {
            LedgerOrchestrator.OnVesselRolloutSpending(ut, cost, pid, vesselName, launchSiteName, guid);
        }

        private static GameAction SingleRollout()
        {
            return Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
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
        /// One launch's rollout emitted twice (same launch guid) at the #710
        /// production UTs: the first row lands, the second is a duplicate and is
        /// dropped, and the survivor moves to the smaller UT.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_SameLaunchGuidDoubleEmission_DroppedAsDuplicate()
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
                l.Contains("swapped surviving row to relaunch UT") &&
                l.Contains("existingGuid=" + ProductionGuid) &&
                l.Contains("newGuid=" + ProductionGuid));
        }

        /// <summary>
        /// CS-3b repro (RELAUNCH-ROLLOUT-DEDUPED-AS-REVERT): an unrecorded launch of
        /// Jumping Flea at UT 347.26, the vessel recovered, then the same craft
        /// launched again at UT 350.34. Same craft-baked pid, same site, same cost,
        /// 3.08 s apart, but a fresh launch guid: a second real charge that must be
        /// ledgered. The decision line names both guids.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_RelaunchFreshGuidSamePidSiteCostInsideWindow_BothWritten()
        {
            RecordRollout(347.25999999997413, 2932.0, pid: 2905720181u,
                vesselName: "Jumping Flea", guid: ProductionGuid);
            RecordRollout(350.33999999997133, 2932.0, pid: 2905720181u,
                vesselName: "Jumping Flea", guid: RelaunchGuid);

            Assert.Equal(2, CountUnadoptedRollouts());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: recording rollout as a new launch") &&
                l.Contains("verdict=GuidDiffers") &&
                l.Contains("existingGuid=" + ProductionGuid) &&
                l.Contains("newGuid=" + RelaunchGuid));
            Assert.Equal(2, Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                a.DedupKey != null &&
                (a.DedupKey.EndsWith("|guid=" + ProductionGuid) || a.DedupKey.EndsWith("|guid=" + RelaunchGuid))));
        }

        /// <summary>
        /// A stored row with no launch guid (written before the key carried one)
        /// falls back to the #710 pid + site match: same pid / site / cost inside the
        /// window is still a duplicate. The decision line names both guids and the
        /// fallback verdict, and the survivor keeps the known guid.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_ExistingRowWithoutGuid_DedupedByPidFallback()
        {
            RecordRollout(100.5, 5000.0, guid: null);
            RecordRollout(100.0, 5000.0, guid: ProductionGuid);

            Assert.Equal(1, CountUnadoptedRollouts());
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout") &&
                l.Contains("verdict=DuplicateGuidUnknownFallback") &&
                l.Contains("existingGuid=(none)") &&
                l.Contains("newGuid=" + ProductionGuid));
            var survivor = SingleRollout();
            Assert.Equal(100.0, survivor.UT);
            Assert.EndsWith("|guid=" + ProductionGuid, survivor.DedupKey);
        }

        /// <summary>
        /// The mirror direction: a new rollout whose launch guid could not be read
        /// falls back to the pid + site match against a guid-bearing row, and the
        /// survivor keeps the stored guid when the swap rewrites its key.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_CandidateWithoutGuid_DedupedByPidFallback()
        {
            RecordRollout(100.5, 5000.0, guid: ProductionGuid);
            RecordRollout(100.0, 5000.0, guid: null);

            Assert.Equal(1, CountUnadoptedRollouts());
            Assert.Contains(logLines, l =>
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout") &&
                l.Contains("verdict=DuplicateGuidUnknownFallback") &&
                l.Contains("existingGuid=" + ProductionGuid) &&
                l.Contains("newGuid=(none)"));
            var survivor = SingleRollout();
            Assert.Equal(100.0, survivor.UT);
            Assert.EndsWith("|guid=" + ProductionGuid, survivor.DedupKey);
        }

        /// <summary>
        /// With an unknown guid the fallback is exactly the #710 rule: a differing
        /// known pid is still two launches.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_GuidUnknownDifferentPid_BothKept()
        {
            RecordRollout(100.0, 5000.0, pid: 1u, guid: null);
            RecordRollout(100.5, 5000.0, pid: 2u, guid: ProductionGuid);

            Assert.Equal(2, CountUnadoptedRollouts());
        }

        // ----------------------------------------------------------------
        // Pure classifier and key round trip
        // ----------------------------------------------------------------

        private static LedgerRolloutAdoption.RolloutAdoptionContext Ctx(
            uint pid = ProductionPid, string name = ProductionVesselName,
            string site = ProductionSite, string guid = ProductionGuid)
        {
            return LedgerRolloutAdoption.CreateRolloutAdoptionContext(pid, name, site, guid);
        }

        [Fact]
        public void ClassifyRolloutDuplicate_VerdictTable()
        {
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.Duplicate,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(), Ctx()));
            // Known, differing guids are two launches even with every other field equal.
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.GuidDiffers,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(), Ctx(guid: RelaunchGuid)));
            // Unknown on either side: the #710 pid match.
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.DuplicateGuidUnknownFallback,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(guid: null), Ctx()));
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.DuplicateGuidUnknownFallback,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(), Ctx(guid: null)));
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.PidDiffers,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(guid: null), Ctx(pid: 7u)));
            // Unknown guid and pid: name + site.
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.DuplicateGuidUnknownFallback,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(pid: 0u, guid: null), Ctx(pid: 0u, guid: null)));
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.NoMatch,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(pid: 0u, guid: null), Ctx(pid: 0u, name: "R2", guid: null)));
            // Site guard first, whatever the guids say.
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.SiteDiffers,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(), Ctx(site: "Desert Launch Site")));
            // Equal guids with differing known pids are not one rollout.
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.PidDiffers,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(Ctx(), Ctx(pid: 7u)));
            // Guid comparison is format-insensitive (dashed "D" form vs "N" form).
            Assert.Equal(LedgerRolloutAdoption.RolloutDuplicateVerdict.Duplicate,
                LedgerRolloutAdoption.ClassifyRolloutDuplicate(
                    Ctx(), Ctx(guid: "5D80F8DF-BD9D-4084-B5C0-0E762C881410")));
        }

        [Fact]
        public void RolloutDedupKey_RoundTripsLaunchGuid()
        {
            var ctx = LedgerRolloutAdoption.CreateRolloutAdoptionContext(
                ProductionPid, "Jumping Flea", "Launch Pad", "5D80F8DF-BD9D-4084-B5C0-0E762C881410");
            string key = LedgerRolloutAdoption.BuildRolloutDedupKey(350.34, ctx);

            Assert.Equal(
                "rollout:350.34|pid=3442693372|site=Launch%20Pad|vessel=Jumping%20Flea|guid=" + ProductionGuid,
                key);
            var parsed = LedgerRolloutAdoption.ParseRolloutAdoptionContext(key);
            Assert.Equal(ProductionPid, parsed.VesselPersistentId);
            Assert.Equal("Jumping Flea", parsed.VesselName);
            Assert.Equal("Launch Pad", parsed.LaunchSiteName);
            Assert.Equal(ProductionGuid, parsed.VesselGuid);
            Assert.False(parsed.IsLegacyBareKey);
        }

        [Fact]
        public void RolloutDedupKey_UnknownGuid_OmitsTheFieldAndParsesNull()
        {
            var ctx = LedgerRolloutAdoption.CreateRolloutAdoptionContext(
                ProductionPid, ProductionVesselName, ProductionSite, null);
            string key = LedgerRolloutAdoption.BuildRolloutDedupKey(100.0, ctx);

            Assert.DoesNotContain("guid=", key);
            Assert.Null(LedgerRolloutAdoption.ParseRolloutAdoptionContext(key).VesselGuid);
            Assert.Equal(ProductionGuid,
                LedgerRolloutAdoption.FormatLaunchGuid(new Guid(ProductionGuid)));
            Assert.Null(LedgerRolloutAdoption.FormatLaunchGuid(Guid.Empty));
        }

        // ----------------------------------------------------------------
        // Adoption reads the launch guid where the recording carries one
        // ----------------------------------------------------------------

        [Fact]
        public void TryAdoptRolloutAction_RecordingGuidDiffersFromRowGuid_NotAdopted()
        {
            RecordRollout(1000.0, ProductionCost, guid: ProductionGuid);
            var rec = new Recording
            {
                RecordingId = "rec-relaunch",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
                RecordedVesselGuid = RelaunchGuid,
            };

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-relaunch", startUT: 1000.1, rec);

            Assert.Null(adopted);
            Assert.Equal(1, CountUnadoptedRollouts());
        }

        [Fact]
        public void TryAdoptRolloutAction_RecordingGuidUnknown_FallsBackToPid()
        {
            RecordRollout(1000.0, ProductionCost, guid: ProductionGuid);
            var rec = new Recording
            {
                RecordingId = "rec-noguid",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
            };

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-noguid", startUT: 1000.1, rec);

            Assert.NotNull(adopted);
            Assert.Equal("rec-noguid", adopted.RecordingId);
        }

        /// <summary>
        /// #710 under the guid rule. The production double was a Revert to the
        /// editor followed by a relaunch: stock's Revert-to-Launch reloads
        /// PostInitState with RESUME_SAVED_CACHE and fires no rollout, while every
        /// editor launch (NEW_FROM_FILE) assembles a vessel with a fresh
        /// <c>Vessel.id</c>. So the two rows carry different guids and the write gate
        /// keeps both; the revert prune removes the reverted launch's untagged
        /// rollout before the relaunch charge lands, leaving exactly one.
        /// </summary>
        [Fact]
        public void RevertToEditorThenRelaunch_FreshGuid_PruneLeavesOneRollout()
        {
            const double launchUT = ProductionUtSecond - 0.01;
            // Fresh launch entry: the prelaunch set is taken before the rollout row exists.
            ParsekScenario.SetPrelaunchEntryLedgerActionIdsForTesting(
                ParsekScenario.CollectLedgerActionIds(Ledger.Actions));
            RecordRollout(ProductionUtFirst, ProductionCost, guid: ProductionGuid);
            Assert.Equal(1, CountUnadoptedRollouts());

            int pruned = ParsekScenario.PruneRevertedFlightLedgerRows(
                launchUT, inclusive: true, RevertKind.Prelaunch);
            Assert.Equal(1, pruned);
            Assert.Equal(0, CountUnadoptedRollouts());

            RecordRollout(ProductionUtSecond, ProductionCost, guid: RelaunchGuid);

            var survivor = SingleRollout();
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.EndsWith("|guid=" + RelaunchGuid, survivor.DedupKey);
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
        /// Career repro (c6, 2026-05-21): the same auto-saved craft (shared
        /// vessel pid) launched from Launch Pad (UT 72.24) then from the Desert
        /// alternate site (UT 131.82) 59.578 s later, just inside the 60 s
        /// dedup window. The PID-only match collapsed them, dropped the Desert
        /// rollout, and the next recalc refunded the player 3805 funds
        /// (PatchFunds 42190 -> 45995). Two launches from different known sites
        /// are independent rollouts and both must be kept.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_SamePidSameCostDifferentSite_BothKept()
        {
            RecordRollout(ut: 72.24, cost: 3805.0, launchSiteName: "Launch Pad");
            RecordRollout(ut: 131.82, cost: 3805.0, launchSiteName: "Desert Launch Site");

            Assert.Equal(2, CountUnadoptedRollouts());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
        }

        /// <summary>
        /// Same-site guard preservation: Revert-to-Launch always relaunches from
        /// the same site, so same pid + same site + same cost within the window
        /// must still collapse to one (the #710 contract). The new cross-site
        /// reject must not weaken this.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_SamePidSameCostSameSiteWithinWindow_Deduped()
        {
            RecordRollout(ut: 100.0, cost: 3805.0, launchSiteName: "Desert Launch Site");
            RecordRollout(ut: 100.5, cost: 3805.0, launchSiteName: "Desert Launch Site");

            Assert.Equal(1, CountUnadoptedRollouts());
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: skipping duplicate rollout"));
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
            long beforeCseq = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild).CaptureSequence;
            RecordRollout(t1 - rollback, ProductionCost);

            Assert.Equal(1, CountUnadoptedRollouts());
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal(t1 - rollback, survivor.UT);
            Assert.NotEqual(beforeKey, survivor.DedupKey);
            // The walk ties same-UT rows on the capture sequence, so the survivor takes a
            // fresh one with its fresh Sequence: charged after every row already at that UT.
            Assert.True(beforeCseq > 0);
            Assert.True(survivor.CaptureSequence > beforeCseq);
            Assert.Contains((t1 - rollback).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                survivor.DedupKey);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("swapped surviving row to relaunch UT"));
        }

        /// <summary>
        /// Adoption interaction: once the original rollout has already been
        /// adopted by a recording, the write-time duplicate gate must not
        /// mutate that adopted row during a later revert-relaunch. The
        /// relaunch emission remains a fresh unadopted row so a future
        /// recording can claim the new rollout; load-time repair owns the
        /// already-corrupt adopted+unadopted cleanup path.
        /// </summary>
        [Fact]
        public void OnVesselRolloutSpending_AdoptedRowDoesNotBlockRelaunchWrite()
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
                RecordedVesselGuid = ProductionGuid,
            };
            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: t1 + 0.1, rec);
            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);

            // Revert + relaunch with a 5 s clock rollback - INSIDE
            // RolloutDuplicateWindowSeconds, so the window check cannot stand in
            // for the adopted-row exclusions. The write-time gate must NOT
            // collapse this into the adopted row (adopted rollouts are not
            // duplicate candidates: TryAdoptRolloutAction nulls the row's
            // DedupKey and stamps its RecordingId, and the duplicate scan skips
            // on both). The new emission lands as a fresh unadopted row.
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

        // OnVesselRolloutSpending_AdoptedRolloutDoesNotBlockNewWrite was deleted by
        // the 2026-09-14 test-quality audit (F-catchall-010-02). Its second emission
        // sat 200 s after the adopted row while RolloutDuplicateWindowSeconds is 60,
        // so the window check alone kept the write and the adoption exclusion the
        // name claimed was never the deciding branch: the cell was a copy of
        // OnVesselRolloutSpending_OutsideDuplicateWindow_BothKept with an inert
        // adoption bolted on. The adoption property is guarded by
        // OnVesselRolloutSpending_AdoptedRowDoesNotBlockRelaunchWrite above, whose
        // relaunch emission is INSIDE the window; deleting both adopted-row
        // exclusions in TryFindDuplicateRolloutAction (the RecordingId skip and the
        // DedupKey-empty skip, the second of which is the one that actually decides
        // because adoption nulls the key) reds that cell and nothing else in the
        // four classes that reach the rollout path.

        // ----------------------------------------------------------------
        // Load-time repair (already-corrupt saves)
        // ----------------------------------------------------------------

        /// <summary>
        /// Loads the two #710 production rollout rows (UTs, pid, site, vessel and
        /// cost of <c>logs/2026-05-01_2208_investigate/parsek/GameState/ledger.pgld</c>),
        /// here carrying one launch guid, and asserts the load-time repair pass
        /// collapses them to one. The guid-less original shape is
        /// <see cref="RepairDuplicateRolloutActions_RowsWithoutGuid_CollapsedByPidFallback"/>.
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
                DedupKey = Key(ProductionUtFirst),
            };
            var second = new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = Key(ProductionUtSecond),
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
        /// Idempotency over a ledger that actually HAS work to do: the
        /// production duplicate cluster is seeded directly (bypassing the
        /// write-time gate), so pass one collapses it and pass two must be a
        /// true no-op - no further rows dropped and no second collapse log.
        /// Seeding two legitimately distinct rollouts instead would leave
        /// nothing to collapse and a repair pass that did nothing at all would
        /// pass.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_SecondPassOverACollapsedClusterIsANoOp()
        {
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 18,
                DedupKey = Key(ProductionUtFirst),
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = Key(ProductionUtSecond),
            });
            Assert.Equal(2, CountUnadoptedRollouts());

            int firstPass = Ledger.RepairDuplicateRolloutActions();
            Assert.Equal(1, firstPass);
            Assert.Equal(1, CountUnadoptedRollouts());

            int ledgerCountAfterFirstPass = Ledger.Actions.Count;
            int secondPass = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, secondPass);
            Assert.Equal(1, CountUnadoptedRollouts());
            Assert.Equal(ledgerCountAfterFirstPass, Ledger.Actions.Count);

            // Exactly ONE collapse summary across both passes: the no-op pass
            // must stay silent, or a healthy ledger reads as a repaired one.
            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[Ledger]") &&
                l.Contains("RepairDuplicateRolloutActions: collapsed")));
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
                    DedupKey = Key(728.0 + 0.04 * i),
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
                DedupKey = Key(100.0, pid: 1u, vessel: "Alice"),
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.5,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = Key(100.5, pid: 2u, vessel: "Bob"),
            });
            // Far-apart UTs (same craft, two real launches separated by hours).
            Ledger.AddAction(new GameAction
            {
                UT = 100.0 + LedgerRolloutAdoption.RolloutDuplicateWindowSeconds + 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = Key(9999.0, pid: 1u, vessel: "Alice"),
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(3, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Load-time mirror of the c6 cross-site career repro: an already-saved
        /// ledger holding a Launch Pad rollout and a Desert rollout of the same
        /// craft (shared pid, equal cost, 59.58 s apart, inside the window)
        /// must keep both. The repair pass routes through the same
        /// <see cref="LedgerRolloutAdoption.RolloutDuplicateMatches"/> guard, so
        /// the cross-site reject heals against accidental collapse here too.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_DifferentSiteSamePid_BothKept()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 72.24,
                Type = GameActionType.FundsSpending,
                FundsSpent = 3805f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 1,
                DedupKey = Key(72.24, vessel: "r0"),
            });
            Ledger.AddAction(new GameAction
            {
                UT = 131.82,
                Type = GameActionType.FundsSpending,
                FundsSpent = 3805f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 2,
                DedupKey = Key(131.82, site: "Desert%20Launch%20Site", vessel: "r0"),
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(2, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Repair mirror of the CS-3b relaunch: same pid / site / cost inside the
        /// window, different known launch guids. Two launches; both kept.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_DifferentGuidsSamePidSite_BothKept()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 347.26,
                Type = GameActionType.FundsSpending,
                FundsSpent = 2932f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 1,
                DedupKey = Key(347.26, guid: ProductionGuid),
            });
            Ledger.AddAction(new GameAction
            {
                UT = 350.34,
                Type = GameActionType.FundsSpending,
                FundsSpent = 2932f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 2,
                DedupKey = Key(350.34, guid: RelaunchGuid),
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(0, removed);
            Assert.Equal(2, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Repair of the #710 rows as originally serialized (no guid field): the
        /// pid + site fallback still collapses them to one.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_RowsWithoutGuid_CollapsedByPidFallback()
        {
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 18,
                DedupKey = Key(ProductionUtFirst, guid: null),
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = Key(ProductionUtSecond, guid: ProductionGuid),
            });

            int removed = Ledger.RepairDuplicateRolloutActions();

            Assert.Equal(1, removed);
            var survivor = SingleRollout();
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.EndsWith("|guid=" + ProductionGuid, survivor.DedupKey);
        }

        /// <summary>
        /// The survivor of a both-unadopted swap keeps the anchor's known guid when
        /// the earlier-UT candidate row carries none.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_SwapKeepsAnchorGuidWhenCandidateHasNone()
        {
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 18,
                DedupKey = Key(ProductionUtFirst, guid: ProductionGuid),
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = Key(ProductionUtSecond, guid: null),
            });

            Assert.Equal(1, Ledger.RepairDuplicateRolloutActions());
            var survivor = SingleRollout();
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.Equal(19, survivor.Sequence);
            Assert.EndsWith("|guid=" + ProductionGuid, survivor.DedupKey);
        }

        /// <summary>
        /// Adopted + unadopted with differing known guids (the recording flew a
        /// different launch of the same craft): not a duplicate, both kept.
        /// </summary>
        [Fact]
        public void RepairDuplicateRolloutActions_AdoptedRowOtherLaunchGuid_BothKept()
        {
            var rec = new Recording
            {
                RecordingId = "rec-adopted",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
                RecordedVesselGuid = ProductionGuid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtFirst,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-adopted",
                Sequence = 1,
                DedupKey = null,
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 2,
                DedupKey = Key(ProductionUtSecond, guid: RelaunchGuid),
            });

            Assert.Equal(0, Ledger.RepairDuplicateRolloutActions());
            Assert.Equal(1, CountUnadoptedRollouts());
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
                DedupKey = Key(100.5),
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
                RecordedVesselGuid = ProductionGuid,
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
                DedupKey = Key(ProductionUtSecond),
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
            // The adopted row keeps its identity (RecordingId, ActionId) while
            // moving to the duplicate cluster's earliest UT.
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-adopted", survivor.RecordingId);
            Assert.Equal(adopted.ActionId, survivor.ActionId);
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.Equal(50, survivor.Sequence);
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
                DedupKey = Key(ProductionUtSecond),
            });
            // Now stage the adopted row at a higher index.
            var rec = new Recording
            {
                RecordingId = "rec-adopted",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
                RecordedVesselGuid = ProductionGuid,
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
            var survivor = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-adopted", survivor.RecordingId);
            Assert.Equal(ProductionUtSecond, survivor.UT);
            Assert.Equal(1, survivor.Sequence);
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
                RecordedVesselGuid = ProductionGuid,
            };
            var recB = new Recording
            {
                RecordingId = "rec-B",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
                RecordedVesselGuid = ProductionGuid,
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
                DedupKey = Key(ProductionUtFirst),
            });
            Ledger.AddAction(new GameAction
            {
                UT = ProductionUtSecond,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 19,
                DedupKey = Key(ProductionUtSecond),
            });
            Assert.Equal(2, CountUnadoptedRollouts());

            // OnKspLoad with no committed recordings + a maxUT past both rows
            // (mimics the user opening the save after the fix is installed).
            LedgerOrchestrator.OnKspLoad(new HashSet<string>(), maxUT: 10000.0);

            Assert.Equal(1, CountUnadoptedRollouts());
        }

        /// <summary>
        /// Follow-up regression: OnKspLoad must run duplicate-rollout repair
        /// before Reconcile's maxUT pruning. If an already-adopted rollout row
        /// still carries the original pre-revert UT, and the unadopted relaunch
        /// duplicate carries the rolled-back UT, Reconcile would otherwise
        /// prune the adopted row as "future" before repair could move it onto
        /// the relaunch timeline.
        /// </summary>
        [Fact]
        public void OnKspLoad_RepairsAdoptedFutureRowBeforeReconcilePrunes()
        {
            Ledger.SeedInitialFunds(25000.0);

            var rec = new Recording
            {
                RecordingId = "rec-adopted",
                StartSituation = "Prelaunch",
                VesselPersistentId = ProductionPid,
                VesselName = ProductionVesselName,
                LaunchSiteName = ProductionSite,
                RecordedVesselGuid = ProductionGuid,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            const double staleFutureUT = 1000.0;
            const double relaunchUT = 995.0;
            Ledger.AddAction(new GameAction
            {
                UT = staleFutureUT,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                RecordingId = "rec-adopted",
                Sequence = 10,
                DedupKey = null,
            });
            Ledger.AddAction(new GameAction
            {
                UT = relaunchUT,
                Type = GameActionType.FundsSpending,
                FundsSpent = ProductionCost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                Sequence = 11,
                DedupKey = Key(relaunchUT),
            });

            LedgerOrchestrator.OnKspLoad(
                new HashSet<string> { "rec-adopted" },
                maxUT: relaunchUT + 0.25);

            var rolloutRows = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsSpending &&
                    a.FundsSpendingSource == FundsSpendingSource.VesselBuild)
                .ToList();
            Assert.Single(rolloutRows);
            Assert.Equal("rec-adopted", rolloutRows[0].RecordingId);
            Assert.Equal(relaunchUT, rolloutRows[0].UT);
            Assert.Equal(11, rolloutRows[0].Sequence);
        }

    }
}
