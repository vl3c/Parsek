using System.Collections.Generic;
using System.Globalization;
using Parsek.InGameTests.Helpers;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game wiring (Layer B) for the ledger ground-truth verification harness.
    /// Quicksaves the live career at the current UT, parses that .sfs INDEPENDENTLY
    /// of the Parsek ledger (<see cref="CareerSaveParser"/>), runs
    /// <see cref="LedgerOrchestrator.RecalculateAndPatch()"/>, reads the
    /// reconstruction off the recalc modules, and diffs the two via
    /// <see cref="LedgerGroundTruthDiff.Compare"/>.
    ///
    /// <para>
    /// Unlike <c>TopBarReflectsLedgerAfterRecalc</c> (which compares a module getter
    /// against the live singleton AFTER the patch wrote that value, a tautology),
    /// this compares the recalc output against KSP's own on-disk serialization.
    /// A divergence on a seeded pool (funds / science / reputation) is the
    /// save-corruption class the harness exists to catch.
    /// </para>
    ///
    /// <para>
    /// Facet strictness: the seeded pools (funds / science pool / reputation) and a
    /// guid-corroborated vessel-recovery consistency violation are HARD failures.
    /// The per-identity facets (per-subject science, facilities, contracts,
    /// milestones) plus phantoms and pid-only recovery matches are REPORT-ONLY by
    /// default, because the user's real career is mixed-history (Parsek installed
    /// partway), so the delta-only modules legitimately differ from KSP's full
    /// history. Flip <see cref="LedgerGroundTruthDiff.StrictPerIdentityForTesting"/>
    /// to promote ALL report-only divergences to hard failures on a clean test
    /// career flown entirely under Parsek tracking.
    /// </para>
    ///
    /// See docs/dev/design-ledger-groundtruth-harness.md (Behavior section).
    ///
    /// <para>
    /// [ERS-exempt] is NOT required here: recovery-credit recording resolution
    /// routes through <see cref="EffectiveState.ComputeERS"/> (the routed,
    /// supersede-aware recording set), not a raw
    /// <c>RecordingStore.CommittedRecordings</c> read. The recovery-credit scan
    /// itself routes through <see cref="EffectiveState.ComputeELS"/>. (This file
    /// also lives under Source/Parsek/InGameTests/, which the ERS/ELS allowlist
    /// already covers, but no raw read is performed.)
    /// </para>
    /// </summary>
    public class LedgerGroundTruthHarness
    {
        private const string Tag = "LedgerGroundTruth";

        // Dedicated quicksave slot. Parsek-prefixed so it can never collide with
        // the player's "quicksave" slot; overwritten each run and disposable.
        private const string GroundTruthSlot = "parsek_ledger_groundtruth";

        /// <summary>The recalculation reason the harness walk logs.</summary>
        internal const string GroundTruthRecalcReason = "ledger-ground-truth";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Null when the ground truth can run in <paramref name="mode"/> with the live pool
        /// singletons present, else the Skip reason. A Science game has only the science
        /// pool (stock creates no Funding / Reputation scenario there, so the parsed save
        /// carries no funds or rep facet and the diff skips both); a Sandbox game has no
        /// pool at all.
        /// </summary>
        internal static string GroundTruthModeSkipReason(
            Game.Modes mode, bool hasFunding, bool hasResearch, bool hasReputation)
        {
            if (mode == Game.Modes.CAREER)
            {
                return hasFunding && hasResearch && hasReputation
                    ? null
                    : "Funding/R&D/Reputation singletons are not all initialized";
            }
            if (mode == Game.Modes.SCIENCE_SANDBOX)
            {
                return hasResearch
                    ? null
                    : "R&D singleton is not initialized";
            }
            return $"Ledger ground-truth verification needs a career or science game (mode={mode})";
        }

        /// <summary>The pools the live game holds in <paramref name="mode"/>, for the log line.</summary>
        internal static string GroundTruthPoolsForMode(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER ? "funds,science,reputation" : "science";
        }

        [InGameTest(Category = "LedgerGroundTruth", Scene = GameScenes.FLIGHT,
            Description = "NON-CIRCULAR closed loop: quicksaves the live career at the current UT, "
              + "parses that .sfs INDEPENDENTLY of the ledger, runs RecalculateAndPatch, and diffs "
              + "the reconstruction against the parsed save (funds/science/rep/per-subject/facilities/"
              + "contracts/milestones + vessel-recovery consistency). Unlike TopBarReflectsLedgerAfterRecalc "
              + "(which compares module-getter vs live-singleton AFTER patch and is therefore a tautology), "
              + "this compares recalc output to KSP's own on-disk serialization. Career or Science (science "
              + "pool only); skips if a "
              + "live/pending tree would defer patching. Stop recording before running.",
            AllowBatchExecution = true,
            RestoreBatchFlightBaselineAfterExecution = true)]
        public void VerifyReconstructionAgainstGroundTruthSave()
        {
            // ---- Step 1: guards (Skip, mirror TopBarReflectsLedgerAfterRecalc +
            // the full deferral set including HasLiveRecorder). ----
            if (HighLogic.CurrentGame == null)
            {
                ParsekLog.Verbose(Tag, "Skip: HighLogic.CurrentGame is null");
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                return;
            }
            Game.Modes mode = HighLogic.CurrentGame.Mode;
            string modeSkip = GroundTruthModeSkipReason(
                mode,
                Funding.Instance != null,
                ResearchAndDevelopment.Instance != null,
                Reputation.Instance != null);
            if (modeSkip != null)
            {
                ParsekLog.Verbose(Tag, $"Skip: {modeSkip}");
                InGameAssert.Skip(modeSkip);
                return;
            }
            ParsekLog.Info(Tag, $"ground-truth game mode={mode} pools={GroundTruthPoolsForMode(mode)}");
            if (RecordingStore.HasPendingTree
                || GameStateRecorder.HasActiveUncommittedTree()
                || GameStateRecorder.HasLiveRecorder())
            {
                ParsekLog.Verbose(Tag,
                    "Skip: a live/pending tree would defer patching " +
                    $"(pendingTree={RecordingStore.HasPendingTree.ToString(IC)} " +
                    $"activeUncommittedTree={GameStateRecorder.HasActiveUncommittedTree().ToString(IC)} " +
                    $"liveRecorder={GameStateRecorder.HasLiveRecorder().ToString(IC)})");
                InGameAssert.Skip(
                    "RecalculateAndPatch would defer KSP singleton patching while a live/pending tree exists; " +
                    "stop recording and commit/discard any pending tree first");
                return;
            }

            // ---- Step 2: snapshot live pools for restore in finally. ----
            var (fundsBefore, scienceBefore, repBefore) = SnapshotFinancials();

            try
            {
                // ---- Step 3: capture S to a dedicated slot (synchronous: the
                // file is on disk when TriggerQuicksave returns). ----
                QuickloadResumeHelpers.TriggerQuicksave(GroundTruthSlot);

                // ---- Step 4: parse S independently off disk. ----
                string savePath = GetGroundTruthSavePath();
                ConfigNode root = ConfigNode.Load(savePath);
                if (root == null)
                {
                    ParsekLog.Verbose(Tag,
                        $"Skip: ConfigNode.Load returned null for ground-truth save '{savePath}'");
                    InGameAssert.Skip(
                        $"ConfigNode.Load returned null for ground-truth save '{savePath}'");
                    return;
                }

                CareerSaveSnapshot save = CareerSaveParser.Parse(root);
                if (!save.Parsed)
                {
                    ParsekLog.Verbose(Tag, $"Skip: ground-truth save unparseable: {save.Reason}");
                    InGameAssert.Skip($"Ground-truth save shape unrecognizable: {save.Reason}");
                    return;
                }

                ParsekLog.Info(Tag,
                    $"captured ground-truth quicksave slot={GroundTruthSlot} path='{savePath}' " +
                    $"hasFunds={save.HasFunds.ToString(IC)} hasScience={save.HasScience.ToString(IC)} " +
                    $"hasRep={save.HasRep.ToString(IC)} vessels={save.Vessels.Count.ToString(IC)}");

                // ---- Step 5: run reconstruction; verify modules initialized. ----
                // The walk stops at the clock when committed rows lie ahead of it (after a
                // rewind or a Re-Fly), as every production patch does: the save holds the
                // career at now, and a walk to the end of the timeline reads committed future
                // earnings and losses as if they had happened.
                LedgerOrchestrator.Initialize();
                double clockUT = ParsekScenario.GetCurrentTimelineUTForLedgerRecalc();
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                    clockUT, GroundTruthRecalcReason);

                if (LedgerOrchestrator.Funds == null
                    || LedgerOrchestrator.Science == null
                    || LedgerOrchestrator.Reputation == null)
                {
                    ParsekLog.Verbose(Tag,
                        "Skip: recalc modules null after RecalculateAndPatch " +
                        $"(funds={(LedgerOrchestrator.Funds == null ? "null" : "ok")} " +
                        $"science={(LedgerOrchestrator.Science == null ? "null" : "ok")} " +
                        $"rep={(LedgerOrchestrator.Reputation == null ? "null" : "ok")})");
                    InGameAssert.Skip(
                        "Funds/Science/Reputation recalc modules are null after RecalculateAndPatch");
                    return;
                }

                // ---- Step 6: build the reconstruction snapshot from the RAW
                // running readers (see design reader-choice note). ----
                var recon = BuildReconstructionSnapshot(clockUT);

                // ---- Step 7: build the facility maxLevel map from live KSP. ----
                var facilityMaxLevels = BuildFacilityMaxLevels();

                // ---- Step 8: diff. ----
                // Recompute the production drawdown-guard authoritative-reduction signal
                // (mirrors LedgerOrchestrator.RecalculateAndPatchCore). tombstonePath is
                // always false here: tombstones run only inside a re-fly merge, which the
                // Step 1 live/pending-tree guard already skips. When false, a seeded pool
                // whose RAW running reconstruction runs ABOVE the live save is UPLIFT-clamped
                // DOWN to live by the guard, so the diff demotes that (upward) divergence to
                // report-only; a downward divergence stays hard.
                bool authoritativeReduction = LedgerOrchestrator.IsAuthoritativeReduction(
                    RewindContext.IsRewinding,
                    ParsekScenario.Instance?.ActiveReFlySessionMarker != null,
                    ParsekScenario.Instance?.ActiveMergeJournal != null,
                    tombstonePath: false,
                    RewindContext.RewindResourceAdjustmentInProgress);
                ParsekLog.Verbose(Tag,
                    $"diff: authoritativeReduction={authoritativeReduction.ToString(IC)} " +
                    $"(rewinding={RewindContext.IsRewinding.ToString(IC)} " +
                    $"reFlyMarker={(ParsekScenario.Instance?.ActiveReFlySessionMarker != null).ToString(IC)} " +
                    $"mergeJournal={(ParsekScenario.Instance?.ActiveMergeJournal != null).ToString(IC)} " +
                    $"rewindResAdjust={RewindContext.RewindResourceAdjustmentInProgress.ToString(IC)})");

                LedgerDivergenceReport report = LedgerGroundTruthDiff.Compare(
                    save, recon, FacetTolerances.Default, facilityMaxLevels, authoritativeReduction);

                // ---- Step 9: vessel parse-sanity (REPORT-ONLY). ----
                ReportVesselParseSanity(save);

                // ---- Step 10: report + assert. ----
                EmitFacetSummaries(save, recon);

                var hardFailures = report.HardFailures(
                    LedgerGroundTruthDiff.StrictPerIdentityForTesting);
                int hard = hardFailures.Count;
                int reportOnly = report.All.Count - hard;

                // Full structured report at Warn so the developer sees every facet
                // in KSP.log + parsek-test-results.txt regardless of pass/fail.
                ParsekLog.Warn(Tag, report.Format());

                ParsekLog.Info(Tag,
                    $"result: hardFailures={hard.ToString(IC)} reportOnly={reportOnly.ToString(IC)} " +
                    $"facetsCompared={report.FacetsCompared.ToString(IC)} " +
                    $"strict={LedgerGroundTruthDiff.StrictPerIdentityForTesting.ToString(IC)}");

                InGameAssert.IsTrue(hard == 0, report.Format());
            }
            finally
            {
                // Restore the three live pools even on assert/Skip. On a healthy
                // save every patch is a no-op so these deltas are ~0;
                // RestoreBatchFlightBaselineAfterExecution=true gives a full
                // reload backstop in batch mode for the rare buggy save that
                // mutates facilities/contracts/milestones.
                RestoreFinancials(fundsBefore, scienceBefore, repBefore);

                // Delete the disposable ground-truth quicksave so the harness
                // leaves no stray save slot in the player's load menu. Runs on
                // every path (pass / assert / Skip).
                QuickloadResumeHelpers.TryDeleteSaveSlot(GroundTruthSlot);
            }
        }

        // ----------------------------------------------------------------
        // Reconstruction read-out
        // ----------------------------------------------------------------

        private static LedgerReconstructionSnapshot BuildReconstructionSnapshot(double clockUT)
        {
            var recon = new LedgerReconstructionSnapshot();

            // RAW running pool values (NOT GetAvailable*): the save holds KSP's
            // actual current funds/science/rep, and the running reader avoids the
            // reservation / clamp / projection branches in the Available readers.
            recon.HasFunds = true;
            recon.Funds = LedgerOrchestrator.Funds.GetRunningBalance();
            recon.HasScience = true;
            recon.SciencePool = LedgerOrchestrator.Science.GetRunningScience();
            recon.HasRep = true;
            recon.Reputation = LedgerOrchestrator.Reputation.GetRunningRep();

            // The walked StockPoolAdjustment rows per pool (committed-value rule plan 2.4):
            // the running values above carry them, and the diff leaves them out so a leak the
            // stock pool reference measured still shows as a divergence. ELS is the list the
            // walk read; a row counts only when the module dispatched it.
            IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
            recon.FundsAdjustment = LedgerGroundTruthDiff.SumWalkedStockPoolAdjustments(
                StockPool.Funds, els, LedgerOrchestrator.Funds.GetRowEffects(), out recon.FundsAdjustmentRows);
            recon.ScienceAdjustment = LedgerGroundTruthDiff.SumWalkedStockPoolAdjustments(
                StockPool.Science, els, LedgerOrchestrator.Science.GetRowEffects(), out recon.ScienceAdjustmentRows);
            recon.ReputationAdjustment = LedgerGroundTruthDiff.SumWalkedStockPoolAdjustments(
                StockPool.Reputation, els, LedgerOrchestrator.Reputation.GetRowEffects(),
                out recon.ReputationAdjustmentRows);

            // Per-subject science: CreditedTotal per subject.
            var subjects = LedgerOrchestrator.Science.GetAllSubjects();
            if (subjects != null)
            {
                foreach (var kvp in subjects)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    recon.SubjectScience[kvp.Key] = kvp.Value.CreditedTotal;
                }
            }

            // Facilities: 1-based Level per facility.
            var facilities = LedgerOrchestrator.Facilities != null
                ? LedgerOrchestrator.Facilities.GetAllFacilities()
                : null;
            if (facilities != null)
            {
                foreach (var kvp in facilities)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    recon.FacilityLevel[kvp.Key] = kvp.Value.Level;
                }
            }

            // Active contract guids.
            var activeContracts = LedgerOrchestrator.Contracts != null
                ? LedgerOrchestrator.Contracts.GetActiveContractIds()
                : null;
            if (activeContracts != null)
            {
                foreach (string guid in activeContracts)
                {
                    if (!string.IsNullOrEmpty(guid))
                        recon.ActiveContractGuids.Add(guid);
                }
            }

            // Credited milestone ids.
            var milestones = LedgerOrchestrator.Milestones != null
                ? LedgerOrchestrator.Milestones.GetCreditedMilestoneIds()
                : null;
            if (milestones != null)
            {
                foreach (string id in milestones)
                {
                    if (!string.IsNullOrEmpty(id))
                        recon.CreditedMilestoneIds.Add(id);
                }
            }

            // Recovery credits (vessel facet input).
            recon.RecoveryCredits = ScanRecoveryCredits(clockUT);

            // Roster surface (report-only facet): the KerbalsModule's DELTA claims -
            // kerbals the ledger created, and kerbals it holds permanently reserved
            // (permanent reservation == dead, never freed). There is no full-roster
            // reconstruction, so the diff only checks those two claims against the
            // save's ROSTER (see LedgerGroundTruthDiff.CompareRoster).
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals != null)
            {
                recon.HasRosterSurface = true;
                foreach (string name in kerbals.LedgerCreatedKerbals)
                {
                    if (!string.IsNullOrEmpty(name))
                        recon.LedgerCreatedKerbals.Add(name);
                }
                foreach (var kvp in kerbals.Reservations)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null) continue;
                    if (kvp.Value.IsPermanent)
                        recon.PermanentlyGoneKerbals.Add(kvp.Key);
                }

                // Per-kerbal career-log entries (P9a, report-only KerbalXp facet). Read off
                // the module accumulator, which is rebuilt from the ELS walk on every recalc.
                recon.KerbalCareerLog = kerbals.SnapshotCareerEntries();
            }

            // Researched-tech surface (report-only facet), DELTA-only.
            recon.ResearchedTechIds = ScanResearchedTechIds(clockUT, out bool hasTechSurface);
            recon.HasTechSurface = hasTechSurface;

            // Active-strategy surface (report-only facet): the strategies the walk has on
            // at its cutoff, and every id it saw an activation for (only those are
            // compared; a strategy stock had on before the ledger is not the ledger's).
            var strategies = LedgerOrchestrator.Strategies;
            if (strategies != null)
            {
                recon.HasStrategySurface = true;
                foreach (string id in strategies.GetActiveStrategyIds())
                {
                    if (!string.IsNullOrEmpty(id))
                        recon.LedgerActiveStrategyIds.Add(id);
                }
                foreach (string id in strategies.GetManagedStrategyIds())
                {
                    if (!string.IsNullOrEmpty(id))
                        recon.ManagedStrategyIds.Add(id);
                }
            }

            // NO purchased-part reconstruction surface exists (the recalc has no
            // purchased-part set - part unlock is derived at patch time from KSP's own
            // part list), so that facet is a save-side CENSUS in the diff, with no
            // compare half and no snapshot field to fill.

            ParsekLog.Verbose(Tag,
                $"BuildReconstructionSnapshot: funds={recon.Funds.ToString("R", IC)} " +
                $"science={recon.SciencePool.ToString("R", IC)} rep={recon.Reputation.ToString("R", IC)} " +
                $"subjects={recon.SubjectScience.Count.ToString(IC)} " +
                $"facilities={recon.FacilityLevel.Count.ToString(IC)} " +
                $"activeContracts={recon.ActiveContractGuids.Count.ToString(IC)} " +
                $"creditedMilestones={recon.CreditedMilestoneIds.Count.ToString(IC)} " +
                $"recoveryCredits={recon.RecoveryCredits.Count.ToString(IC)} " +
                $"rosterSurface={recon.HasRosterSurface.ToString(IC)} " +
                $"ledgerCreatedKerbals={recon.LedgerCreatedKerbals.Count.ToString(IC)} " +
                $"permanentlyGoneKerbals={recon.PermanentlyGoneKerbals.Count.ToString(IC)} " +
                $"techSurface={recon.HasTechSurface.ToString(IC)} " +
                $"researchedTech={recon.ResearchedTechIds.Count.ToString(IC)} " +
                $"kerbalCareerLogs={recon.KerbalCareerLog.Count.ToString(IC)} " +
                $"strategySurface={recon.HasStrategySurface.ToString(IC)} " +
                $"ledgerActiveStrategies={recon.LedgerActiveStrategyIds.Count.ToString(IC)} " +
                $"managedStrategies={recon.ManagedStrategyIds.Count.ToString(IC)}");

            return recon;
        }

        /// <summary>
        /// Pure: true when a row has happened by the clock the walk cut off at. The ELS scans
        /// below read rows straight off the ledger, so a committed row ahead of the clock
        /// (after a rewind or a Re-Fly) must be left out exactly as the walk leaves it out:
        /// the save holds the career at now. NaN keeps every row.
        /// </summary>
        internal static bool IsAtOrBeforeClock(GameAction action, double clockUT)
        {
            return action != null && (double.IsNaN(clockUT) || action.UT <= clockUT);
        }

        /// <summary>
        /// Scans the Effective Ledger Set (ELS, the REQUIRED ERS/ELS routing) for the
        /// tech nodes the ledger claims were researched: every AFFORDABLE
        /// ScienceSpending row's NodeId (the same predicate
        /// <see cref="KspStatePatcher.BuildTargetTechIdsForPatch"/> applies to the
        /// action list). An UNAFFORDABLE row did not deduct and did not research, so
        /// it is not a claim.
        ///
        /// <paramref name="hasSurface"/> is false only when the ELS itself is
        /// unavailable; an EMPTY ELS is still a surface (the ledger claims nothing,
        /// which is a real claim the diff can check).
        ///
        /// <para>Unaffordable rows are the reachability evidence for
        /// UNAFFORDABLE-SCIENCE-SPENDING-SILENTLY-RE-LOCKS-A-TECH-NODE, so a nonzero reading
        /// is promoted from Verbose to a grep-stable Info line
        /// (<c>ScanResearchedTechIds: unaffordable-science-spending-rows-present</c>) that a
        /// driven lane can assert on without turning Verbose on for a whole flight. The zero
        /// case stays Verbose — it is the normal, uninteresting reading.</para>
        ///
        /// <para><b>The line carries TWO counts, and an arming operator must pick the right
        /// one.</b> <c>unaffordableSkipped</c> is the RAW <c>!Affordable</c> population and is
        /// a strict SUPERSET of what the guard acts on: it also counts rows the science walk
        /// never dispatched (<c>ResetDerivedFields</c> seeds <c>Affordable=false</c> on every
        /// action), rows whose node reached the patch target set anyway via the baseline or a
        /// second affordable row, rows past whatever cutoff a given patch used, and values
        /// left by a projection walk when read outside a walk.
        /// <c>refusedByWalk</c> counts only rows carrying
        /// <see cref="GameAction.UnaffordableRunningScience"/> — the positive "this walk
        /// processed and REFUSED this row" marker, i.e. the guard-relevant population. Nonzero
        /// <c>unaffordableSkipped</c> does NOT mean the guard fired; nonzero
        /// <c>refusedByWalk</c> means the walk refused a spend (and the guard fires from that
        /// population, further narrowed per-node by target-set membership and live
        /// availability inside <c>KspStatePatcher</c>). Nothing is armed on either count
        /// today; the natural home is the L4 ground-truth lane's logContracts block, which
        /// already reads this harness's output, and <c>refusedByWalk</c> is the count an
        /// arming operator almost certainly wants.</para>
        ///
        /// <para>The claim set below and the patcher's re-lock guard DELIBERATELY disagree
        /// about an unaffordable row, and that disagreement is the signal, not a bug. This
        /// scan reports what the ledger can PAY FOR (so an unaffordable row is not a claim);
        /// the guard keeps the node RESEARCHED in the live game because taking it away would
        /// destroy purchased parts. So a guarded node reads as a live-but-unclaimed tech in
        /// the report-only tech facet — exactly the observable you want. Do NOT "fix" the
        /// disagreement by adding unaffordable rows to the claim set: that would hide the
        /// shortfall behind an agreeing diff.</para>
        /// </summary>
        private static HashSet<string> ScanResearchedTechIds(double clockUT, out bool hasSurface)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);

            var els = EffectiveState.ComputeELS();
            if (els == null)
            {
                hasSurface = false;
                ParsekLog.Verbose(Tag, "ScanResearchedTechIds: ELS unavailable -> no tech surface");
                return ids;
            }

            hasSurface = true;
            int unaffordable = 0;
            int refusedByWalk = 0;
            int aheadOfClock = 0;
            List<string> unaffordableNodeIds = null;
            foreach (var a in els)
            {
                if (a == null) continue;
                if (a.Type != GameActionType.ScienceSpending) continue;
                if (string.IsNullOrEmpty(a.NodeId)) continue;
                if (!IsAtOrBeforeClock(a, clockUT))
                {
                    aheadOfClock++;
                    continue;
                }
                if (!a.Affordable)
                {
                    unaffordable++;
                    // The narrower, guard-relevant population: rows a walk actually
                    // processed and refused, not rows merely left at the seeded false.
                    if (a.UnaffordableRunningScience.HasValue)
                        refusedByWalk++;
                    if (unaffordableNodeIds == null)
                        unaffordableNodeIds = new List<string>();
                    if (unaffordableNodeIds.Count < 10)
                        unaffordableNodeIds.Add(a.NodeId);
                    continue;
                }
                ids.Add(a.NodeId);
            }

            ParsekLog.Verbose(Tag,
                $"ScanResearchedTechIds: researchedNodeIds={ids.Count.ToString(IC)} " +
                $"unaffordableSkipped={unaffordable.ToString(IC)} " +
                $"refusedByWalk={refusedByWalk.ToString(IC)} " +
                $"aheadOfClock={aheadOfClock.ToString(IC)}");

            if (unaffordable > 0)
            {
                // unaffordableSkipped is the RAW superset; refusedByWalk is the
                // guard-relevant population. See the doc comment before arming either.
                ParsekLog.Info(Tag,
                    "ScanResearchedTechIds: unaffordable-science-spending-rows-present: " +
                    $"unaffordableSkipped={unaffordable.ToString(IC)}, " +
                    $"refusedByWalk={refusedByWalk.ToString(IC)}, " +
                    $"researchedNodeIds={ids.Count.ToString(IC)}, " +
                    $"unaffordableNodeIds=[{string.Join(", ", unaffordableNodeIds.ToArray())}]");
            }

            return ids;
        }

        /// <summary>
        /// Scans the Effective Ledger Set (ELS, the REQUIRED ERS/ELS routing) for
        /// vessel-recovery funds credits and resolves each one's vessel identity
        /// from its recording (routed through ERS, the supersede-aware recording
        /// set). A recovery action is Type=FundsEarning, FundsSource=Recovery,
        /// carrying RecordingId + FundsAwarded. GameAction has no VesselName, so
        /// identity (name / launch guid / craft-baked pid) comes from the Recording.
        /// </summary>
        private static List<RecoveryCredit> ScanRecoveryCredits(double clockUT)
        {
            var credits = new List<RecoveryCredit>();

            var els = EffectiveState.ComputeELS();
            if (els == null) return credits;

            // Routed recording set (supersede-aware). Build an id->Recording map
            // once so the per-credit resolution is O(1). This is the routed
            // alternative to a raw RecordingStore.CommittedRecordings read.
            var ers = EffectiveState.ComputeERS();
            var recById = new Dictionary<string, Recording>(System.StringComparer.Ordinal);
            if (ers != null)
            {
                for (int i = 0; i < ers.Count; i++)
                {
                    var rec = ers[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    recById[rec.RecordingId] = rec;
                }
            }

            int scanned = 0;
            int unresolved = 0;
            foreach (var a in els)
            {
                if (a == null) continue;
                if (a.Type != GameActionType.FundsEarning) continue;
                if (a.FundsSource != FundsEarningSource.Recovery) continue;
                if (!IsAtOrBeforeClock(a, clockUT)) continue;
                scanned++;

                var credit = new RecoveryCredit
                {
                    RecordingId = a.RecordingId,
                    Amount = a.FundsAwarded,
                    VesselName = null,
                    VesselGuid = null,
                    VesselPid = 0
                };

                if (!string.IsNullOrEmpty(a.RecordingId)
                    && recById.TryGetValue(a.RecordingId, out Recording rec)
                    && rec != null)
                {
                    credit.VesselName = rec.VesselName;
                    credit.VesselGuid = rec.RecordedVesselGuid;
                    credit.VesselPid = rec.VesselPersistentId;
                }
                else
                {
                    unresolved++;
                }

                credits.Add(credit);
            }

            ParsekLog.Verbose(Tag,
                $"ScanRecoveryCredits: els recovery credits scanned={scanned.ToString(IC)} " +
                $"unresolvedRecording={unresolved.ToString(IC)} " +
                $"ersRecordings={recById.Count.ToString(IC)}");

            return credits;
        }

        /// <summary>
        /// Builds facilityId -> 0-based MAX level index from live KSP (the constant
        /// the diff needs to convert save fractions to int levels). Mirrors
        /// FacilityStatePatcher's protoUpgradeables[id].facilityRefs[0] iteration.
        /// </summary>
        private static Dictionary<string, int> BuildFacilityMaxLevels()
        {
            var maxLevels = new Dictionary<string, int>(System.StringComparer.Ordinal);

            if (ScenarioUpgradeableFacilities.protoUpgradeables == null)
            {
                ParsekLog.Verbose(Tag,
                    "BuildFacilityMaxLevels: protoUpgradeables is null -> empty map");
                return maxLevels;
            }

            int count = 0;
            foreach (var kvp in ScenarioUpgradeableFacilities.protoUpgradeables)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                var proto = kvp.Value;
                if (proto == null || proto.facilityRefs == null || proto.facilityRefs.Count == 0)
                    continue;
                var facility = proto.facilityRefs[0];
                if (facility == null) continue;
                maxLevels[kvp.Key] = facility.MaxLevel;
                count++;
            }

            ParsekLog.Verbose(Tag,
                $"BuildFacilityMaxLevels: resolved {count.ToString(IC)} facility maxLevel(s)");

            return maxLevels;
        }

        // ----------------------------------------------------------------
        // Vessel parse-sanity (REPORT-ONLY)
        // ----------------------------------------------------------------

        /// <summary>
        /// Cross-checks the parsed save.Vessels against live FlightGlobals.Vessels
        /// EXCLUDING Parsek's transient ghost/map-presence ProtoVessels (which are
        /// present live during playback but never written to the save). A residual
        /// mismatch is Warn-logged with the offending PIDs, NOT hard-failed: vessel
        /// set membership at a single instant can differ for benign transient
        /// reasons. The recovery-consistency hard/report logic is handled by the
        /// diff; this is parse-path validation only.
        /// </summary>
        private static void ReportVesselParseSanity(CareerSaveSnapshot save)
        {
            // Live PIDs excluding ghost/map-presence ProtoVessels.
            var livePids = new HashSet<uint>();
            var live = FlightGlobals.Vessels;
            int ghostExcluded = 0;
            if (live != null)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    var v = live[i];
                    if (v == null) continue;
                    uint pid = v.persistentId;
                    if (GhostMapPresence.ghostMapVesselPids.Contains(pid))
                    {
                        ghostExcluded++;
                        continue;
                    }
                    livePids.Add(pid);
                }
            }

            var savePids = new HashSet<uint>();
            for (int i = 0; i < save.Vessels.Count; i++)
            {
                uint pid = save.Vessels[i].PersistentId;
                if (pid != 0)
                    savePids.Add(pid);
            }

            // Residual mismatches: present in save not live, and present live not save.
            var inSaveNotLive = new List<uint>();
            foreach (uint pid in savePids)
            {
                if (!livePids.Contains(pid))
                    inSaveNotLive.Add(pid);
            }
            var inLiveNotSave = new List<uint>();
            foreach (uint pid in livePids)
            {
                if (!savePids.Contains(pid))
                    inLiveNotSave.Add(pid);
            }

            if (inSaveNotLive.Count > 0 || inLiveNotSave.Count > 0)
            {
                ParsekLog.Warn(Tag,
                    $"vessel parse-sanity residual mismatch (report-only): " +
                    $"saveVessels={savePids.Count.ToString(IC)} liveVessels={livePids.Count.ToString(IC)} " +
                    $"ghostExcluded={ghostExcluded.ToString(IC)} " +
                    $"inSaveNotLive=[{string.Join(",", PidStrings(inSaveNotLive))}] " +
                    $"inLiveNotSave=[{string.Join(",", PidStrings(inLiveNotSave))}]");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"vessel parse-sanity: save and live vessel pid sets match " +
                    $"(saveVessels={savePids.Count.ToString(IC)} liveVessels={livePids.Count.ToString(IC)} " +
                    $"ghostExcluded={ghostExcluded.ToString(IC)})");
            }
        }

        private static string[] PidStrings(List<uint> pids)
        {
            var s = new string[pids.Count];
            for (int i = 0; i < pids.Count; i++)
                s[i] = pids[i].ToString(IC);
            return s;
        }

        // ----------------------------------------------------------------
        // Per-facet Info summaries
        // ----------------------------------------------------------------

        private static void EmitFacetSummaries(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon)
        {
            // The pools are compared with the walk's StockPoolAdjustment rows left out; one
            // line per pool names what was left out, so a divergence they explain reads as such.
            ParsekLog.Info(Tag, LedgerGroundTruthDiff.FormatStockPoolAdjustmentLine(
                "Funds", recon.FundsAdjustmentRows, recon.FundsAdjustment,
                recon.Funds, LedgerGroundTruthDiff.FundsExcludingAdjustments(recon)));
            ParsekLog.Info(Tag, LedgerGroundTruthDiff.FormatStockPoolAdjustmentLine(
                "Science", recon.ScienceAdjustmentRows, recon.ScienceAdjustment,
                recon.SciencePool, LedgerGroundTruthDiff.ScienceExcludingAdjustments(recon)));
            ParsekLog.Info(Tag, LedgerGroundTruthDiff.FormatStockPoolAdjustmentLine(
                "Reputation", recon.ReputationAdjustmentRows, recon.ReputationAdjustment,
                recon.Reputation, LedgerGroundTruthDiff.ReputationExcludingAdjustments(recon)));
            if (save.HasFunds)
            {
                double reconFunds = LedgerGroundTruthDiff.FundsExcludingAdjustments(recon);
                double delta = System.Math.Abs(save.Funds - reconFunds);
                ParsekLog.Info(Tag,
                    $"funds save={save.Funds.ToString("R", IC)} recon={reconFunds.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.Funds.ToString("R", IC)}");
            }
            if (save.HasScience)
            {
                double reconScience = LedgerGroundTruthDiff.ScienceExcludingAdjustments(recon);
                double delta = System.Math.Abs(save.SciencePool - reconScience);
                ParsekLog.Info(Tag,
                    $"science save={save.SciencePool.ToString("R", IC)} recon={reconScience.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.SciencePool.ToString("R", IC)}");
            }
            if (save.HasRep)
            {
                double reconRep = LedgerGroundTruthDiff.ReputationExcludingAdjustments(recon);
                double delta = System.Math.Abs(save.Reputation - reconRep);
                ParsekLog.Info(Tag,
                    $"reputation save={save.Reputation.ToString("R", IC)} recon={reconRep.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.Reputation.ToString("R", IC)}");
            }
            if (save.HasRoster)
            {
                ParsekLog.Info(Tag,
                    $"roster saveKerbals={save.Roster.Count.ToString(IC)} " +
                    $"reconSurface={recon.HasRosterSurface.ToString(IC)} " +
                    $"reconCreated={recon.LedgerCreatedKerbals.Count.ToString(IC)} " +
                    $"reconPermanentlyGone={recon.PermanentlyGoneKerbals.Count.ToString(IC)}");
            }
            if (save.HasTechTree)
            {
                ParsekLog.Info(Tag,
                    $"tech saveUnlocked={save.UnlockedTechIds.Count.ToString(IC)} " +
                    $"saveParts={save.PurchasedPartNames.Count.ToString(IC)} " +
                    $"reconSurface={recon.HasTechSurface.ToString(IC)} " +
                    $"reconResearched={recon.ResearchedTechIds.Count.ToString(IC)}");
            }
            if (save.HasStrategySystem)
            {
                ParsekLog.Info(Tag,
                    $"strategies saveStrategies={save.Strategies.Count.ToString(IC)} " +
                    $"saveActive={save.ActiveStrategyIds.Count.ToString(IC)} " +
                    $"reconSurface={recon.HasStrategySurface.ToString(IC)} " +
                    $"reconActive={recon.LedgerActiveStrategyIds.Count.ToString(IC)} " +
                    $"reconManaged={recon.ManagedStrategyIds.Count.ToString(IC)}");
            }
        }

        // ----------------------------------------------------------------
        // Inlined financial snapshot/restore (private in RuntimeTests; copied
        // here per the design so the harness is self-contained).
        // ----------------------------------------------------------------

        private static (double funds, float science, float reputation) SnapshotFinancials()
        {
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            float science = ResearchAndDevelopment.Instance != null
                ? ResearchAndDevelopment.Instance.Science : 0f;
            float reputation = Reputation.Instance != null
                ? Reputation.Instance.reputation : 0f;
            return (funds, science, reputation);
        }

        private static void RestoreFinancials(double fundsBefore, float scienceBefore, float repBefore)
        {
            // Suppress resource-event capture so the AddFunds/AddScience/SetReputation
            // calls below do NOT emit synthetic FundsChanged/ScienceChanged/
            // ReputationChanged events into GameStateStore.
            using (SuppressionGuard.Resources())
            {
                if (Funding.Instance != null)
                {
                    double delta = fundsBefore - Funding.Instance.Funds;
                    if (System.Math.Abs(delta) > 0.01)
                        Funding.Instance.AddFunds(delta, TransactionReasons.None);
                }
                if (ResearchAndDevelopment.Instance != null)
                {
                    float delta = scienceBefore - ResearchAndDevelopment.Instance.Science;
                    if (Mathf.Abs(delta) > 0.01f)
                        ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.None);
                }
                if (Reputation.Instance != null)
                {
                    // Mirror KspStatePatcher.PatchReputation: SetReputation (NOT
                    // AddReputation) because AddReputation applies KSP's reputation
                    // curve, which would leave permanent drift.
                    if (Mathf.Abs(repBefore - Reputation.Instance.reputation) > 0.01f)
                        Reputation.Instance.SetReputation(repBefore, TransactionReasons.None);
                }
            }
        }

        // ----------------------------------------------------------------
        // Ground-truth save path (mirrors QuickloadResumeHelpers.GetSavePath).
        // ----------------------------------------------------------------

        private static string GetGroundTruthSavePath()
        {
            return System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                HighLogic.SaveFolder ?? string.Empty,
                GroundTruthSlot + ".sfs");
        }
    }
}
