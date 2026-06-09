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

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "LedgerGroundTruth", Scene = GameScenes.FLIGHT,
            Description = "NON-CIRCULAR closed loop: quicksaves the live career at the current UT, "
              + "parses that .sfs INDEPENDENTLY of the ledger, runs RecalculateAndPatch, and diffs "
              + "the reconstruction against the parsed save (funds/science/rep/per-subject/facilities/"
              + "contracts/milestones + vessel-recovery consistency). Unlike TopBarReflectsLedgerAfterRecalc "
              + "(which compares module-getter vs live-singleton AFTER patch and is therefore a tautology), "
              + "this compares recalc output to KSP's own on-disk serialization. Career-only; skips if a "
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
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                ParsekLog.Verbose(Tag,
                    $"Skip: ground-truth verification is career-only (mode={HighLogic.CurrentGame.Mode})");
                InGameAssert.Skip(
                    $"Ledger ground-truth verification is career-only (mode={HighLogic.CurrentGame.Mode})");
                return;
            }
            if (Funding.Instance == null
                || ResearchAndDevelopment.Instance == null
                || Reputation.Instance == null)
            {
                ParsekLog.Verbose(Tag, "Skip: Funding/R&D/Reputation singletons not all initialized");
                InGameAssert.Skip("Funding/R&D/Reputation singletons are not all initialized");
                return;
            }
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
                LedgerOrchestrator.Initialize();
                LedgerOrchestrator.RecalculateAndPatch();

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
                var recon = BuildReconstructionSnapshot();

                // ---- Step 7: build the facility maxLevel map from live KSP. ----
                var facilityMaxLevels = BuildFacilityMaxLevels();

                // ---- Step 8: diff. ----
                LedgerDivergenceReport report = LedgerGroundTruthDiff.Compare(
                    save, recon, FacetTolerances.Default, facilityMaxLevels);

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

        private static LedgerReconstructionSnapshot BuildReconstructionSnapshot()
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
            recon.RecoveryCredits = ScanRecoveryCredits();

            ParsekLog.Verbose(Tag,
                $"BuildReconstructionSnapshot: funds={recon.Funds.ToString("R", IC)} " +
                $"science={recon.SciencePool.ToString("R", IC)} rep={recon.Reputation.ToString("R", IC)} " +
                $"subjects={recon.SubjectScience.Count.ToString(IC)} " +
                $"facilities={recon.FacilityLevel.Count.ToString(IC)} " +
                $"activeContracts={recon.ActiveContractGuids.Count.ToString(IC)} " +
                $"creditedMilestones={recon.CreditedMilestoneIds.Count.ToString(IC)} " +
                $"recoveryCredits={recon.RecoveryCredits.Count.ToString(IC)}");

            return recon;
        }

        /// <summary>
        /// Scans the Effective Ledger Set (ELS, the REQUIRED ERS/ELS routing) for
        /// vessel-recovery funds credits and resolves each one's vessel identity
        /// from its recording (routed through ERS, the supersede-aware recording
        /// set). A recovery action is Type=FundsEarning, FundsSource=Recovery,
        /// carrying RecordingId + FundsAwarded. GameAction has no VesselName, so
        /// identity (name / launch guid / craft-baked pid) comes from the Recording.
        /// </summary>
        private static List<RecoveryCredit> ScanRecoveryCredits()
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
            if (save.HasFunds)
            {
                double delta = System.Math.Abs(save.Funds - recon.Funds);
                ParsekLog.Info(Tag,
                    $"funds save={save.Funds.ToString("R", IC)} recon={recon.Funds.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.Funds.ToString("R", IC)}");
            }
            if (save.HasScience)
            {
                double delta = System.Math.Abs(save.SciencePool - recon.SciencePool);
                ParsekLog.Info(Tag,
                    $"science save={save.SciencePool.ToString("R", IC)} recon={recon.SciencePool.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.SciencePool.ToString("R", IC)}");
            }
            if (save.HasRep)
            {
                double delta = System.Math.Abs(save.Reputation - recon.Reputation);
                ParsekLog.Info(Tag,
                    $"reputation save={save.Reputation.ToString("R", IC)} recon={recon.Reputation.ToString("R", IC)} " +
                    $"delta={delta.ToString("R", IC)} tol={FacetTolerances.Default.Reputation.ToString("R", IC)}");
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
