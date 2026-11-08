using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.RecalculateAndPatchCore</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. Each scenario seeds the same three ledger rows and drives the walk through
    /// one entry point (full, cutoff, current-timeline, time jump, after tombstones, Re-Fly
    /// discard), some with the KSP patch deferred by a live tree or held by a newer ledger. It
    /// records the ledger rows left, how often <c>OnTimelineDataChanged</c> fired, and every
    /// log line, with random action ids reduced to their shape. A mismatch writes the actual
    /// transcript next to the test binary.
    /// </summary>
    [Collection("Sequential")]
    public class RecalculateAndPatchCoreCharacterizationTests : IDisposable
    {
        private static readonly Regex ActionIdPattern = new Regex("act_[0-9a-f]{32}");
        private static readonly Regex GuidPattern = new Regex("[0-9a-f]{32}");
        // double.MaxValue as default ToString text: mono prints 17 digits, the .NET
        // Framework 15, so the golden reads it by shape.
        private static readonly Regex MaxValuePattern = new Regex(@"1\.79769313486\d*E\+308");

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly string tempDir;

        public RecalculateAndPatchCoreCharacterizationTests()
        {
            // ParsekSettings' static constructor logs once per process when the walk first
            // touches it; run it here, before the sink, so the transcript does not depend on
            // which test class ran first.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(ParsekSettings).TypeHandle);
            // Several walk log lines format numbers with the host culture; the golden is
            // read under the invariant one so a ro-RO host prints the same transcript.
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-w7-recalc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // Rate-limited lines read the log clock; a fixed one keeps a slow host from
            // printing a line a fast host suppresses.
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
            try { Directory.Delete(tempDir, true); } catch (Exception) { }
        }

        private static void ResetAll()
        {
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static void SeedRows()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 500f,
                FundsSource = FundsEarningSource.Other,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                ScienceAwarded = 2.5f,
                SubjectMaxValue = 12.5f,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.ReputationEarning,
                NominalRep = 4f,
                RepSource = ReputationSource.Other,
            });
        }

        private void HoldNewerLedger()
        {
            var root = new ConfigNode("LEDGER");
            root.AddValue("version", "99");
            root.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            string path = Path.Combine(tempDir, "ledger.pgld");
            root.Save(path);
            Ledger.LoadFromFile(path);
        }

        private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private void Run(StringBuilder sb, string label, Action call)
        {
            int timelineChanged = 0;
            LedgerOrchestrator.OnTimelineDataChanged = () => timelineChanged++;
            logLines.Clear();
            string outcome = "ok";
            try
            {
                call();
            }
            catch (Exception ex)
            {
                outcome = "threw " + ex.GetType().Name + ": " + ex.Message;
            }
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("outcome ").Append(outcome).Append('\n');
            sb.Append("timelineChanged ").Append(timelineChanged.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("held ").Append(Ledger.IsHeldFromNewerVersion ? "yes" : "no").Append('\n');
            foreach (var a in Ledger.Actions)
            {
                sb.Append("row ").Append(a.Type)
                    .Append(" ut=").Append(F(a.UT))
                    .Append(" rec=").Append(a.RecordingId ?? "<null>")
                    .Append('\n');
            }
            foreach (string line in logLines)
            {
                string normalized = MaxValuePattern.Replace(GuidPattern.Replace(
                    ActionIdPattern.Replace(line, "act_<id>"), "<guid>"), "<double.MaxValue>");
                sb.Append("log ").Append(normalized).Append('\n');
            }
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        private string RunAllScenarios()
        {
            var sb = new StringBuilder();

            // R01: an empty ledger, full walk.
            ResetAll();
            Run(sb, "R01 empty full walk", () => LedgerOrchestrator.RecalculateAndPatch());

            // R02: three rows, full walk, nothing deferring the patch.
            ResetAll();
            SeedRows();
            Run(sb, "R02 full walk", () => LedgerOrchestrator.RecalculateAndPatch());

            // R03: the same walk again: the completion line is an on-change line.
            Run(sb, "R03 full walk repeated", () => LedgerOrchestrator.RecalculateAndPatch());

            // R04: a cutoff walk bypasses deferral and rebuilds the crew module after it.
            ResetAll();
            SeedRows();
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            Run(sb, "R04 cutoff walk with a live tree", () => LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0));

            // R05: a full walk with a live tree defers the KSP patch.
            ResetAll();
            SeedRows();
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            Run(sb, "R05 full walk deferred by a live recorder", () => LedgerOrchestrator.RecalculateAndPatch());

            // R06: the current-timeline walk does not bypass deferral.
            ResetAll();
            SeedRows();
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            Run(sb, "R06 current-timeline walk deferred",
                () => LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "golden"));

            // R07: the current-timeline walk with nothing deferring it.
            ResetAll();
            SeedRows();
            Run(sb, "R07 current-timeline walk",
                () => LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "golden"));

            // R08: a time jump runs as a clock advance (the clock-crossing branch).
            ResetAll();
            SeedRows();
            Run(sb, "R08 time jump", () => LedgerOrchestrator.RecalculateAndPatchForTimeJump(250.0));

            // R09: after tombstones, no tech node retired.
            ResetAll();
            SeedRows();
            Run(sb, "R09 after tombstones",
                () => LedgerOrchestrator.RecalculateAndPatchAfterTombstones(new List<string>()));

            // R10: after tombstones that retired a tech node: tech patch at MaxValue.
            ResetAll();
            SeedRows();
            Run(sb, "R10 after tombstones with a tech node",
                () => LedgerOrchestrator.RecalculateAndPatchAfterTombstones(new List<string> { "basicRocketry" }));

            // R11: the Re-Fly discard walk is an authoritative reduction.
            ResetAll();
            SeedRows();
            Run(sb, "R11 re-fly discard", () => LedgerOrchestrator.RecalculateAndPatch(reFlyDiscard: true));

            // R12: a ledger held from a newer version walks empty and patches nothing.
            ResetAll();
            HoldNewerLedger();
            Run(sb, "R12 held ledger full walk", () => LedgerOrchestrator.RecalculateAndPatch());

            // R13: the held ledger on a cutoff walk: still no patch.
            Run(sb, "R13 held ledger cutoff walk", () => LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0));

            return sb.ToString();
        }

        [Fact]
        public void RecalculateAndPatchCore_Golden()
        {
            AssertGolden("RecalculateAndPatchCore_Golden", Golden, RunAllScenarios());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== R01 empty full walk",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 0 entries from 0 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null, filteredOut=0, walkedSorted=0, firstTier=4 modules (0 dispatches), strategy=0 dispatches, secondTier=2 modules (0 dispatches), facilities=0 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 0 actions walked",
            "== R02 full walk",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null, filteredOut=0, walkedSorted=5, firstTier=4 modules (20 dispatches), strategy=5 dispatches, secondTier=2 modules (10 dispatches), facilities=5 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R03 full walk repeated",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 1 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=500, prevSpendings=0, prevEarnings=500",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 4.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=1, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "== R04 cutoff walk with a live tree",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=3, cutoffUT=150",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=150, source=cutoff)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=150.0",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 provisional=True walkClockUT=150.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for ScienceModule: cutoff=150 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=0 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][ScienceModule] Projected availability: current=0, minProjected=0, finalProjected=2.5, available=0, futureActions=2, deltaActions=1",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for FundsModule: cutoff=150 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=500 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][Funds] Projected availability: current=500, minProjected=500, finalProjected=500, available=500, futureActions=2, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=3, cutoffUT=150, filteredOut=2, walkedSorted=3, firstTier=4 modules (12 dispatches), strategy=3 dispatches, secondTier=2 modules (6 dispatches), facilities=3 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=150.0",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=150.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][INFO][CrewReservations] Recomputed after cutoff walk: 0 reservations remain (permanent=0 temporary=0) (cutoffUT=150).",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: tech-tree patch enabled (walkCutoff=150, techCutoff=150, baselineUt=null, baselineTechExclusions=0, targetCount=null, unaffordableTechDrops=null)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no committed resource scan at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no committed part upgrade purchase at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R05 full walk deferred by a live recorder",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null, filteredOut=0, walkedSorted=5, firstTier=4 modules (20 dispatches), strategy=5 dispatches, secondTier=2 modules (10 dispatches), facilities=5 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: deferred KSP state patch (live recorder active)",
            "log [Parsek][VERBOSE][CommittedMilestones] Apply skipped: no trusted clock (reason=deferred-patch, clock=NaN) - the next pass with a clock marks the nodes",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R06 current-timeline walk deferred",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][INFO][LedgerOrchestrator] Current-UT ledger recalculation: reason=golden cutoffUT=250",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=250, source=cutoff)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 provisional=True walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for ScienceModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=2.5 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][ScienceModule] Projected availability: current=2.5, minProjected=2.5, finalProjected=2.5, available=2.5, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for FundsModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=500 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][Funds] Projected availability: current=500, minProjected=500, finalProjected=500, available=500, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250, filteredOut=1, walkedSorted=4, firstTier=4 modules (16 dispatches), strategy=4 dispatches, secondTier=2 modules (8 dispatches), facilities=4 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][INFO][CrewReservations] Recomputed after cutoff walk: 0 reservations remain (permanent=0 temporary=0) (cutoffUT=250).",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: deferred KSP state patch (active uncommitted flight tree)",
            "log [Parsek][VERBOSE][CommittedMilestones] Apply: nothing to mark at clock=250.0 (reason=deferred-patch; marked=0 alreadyComplete=0 credited=0 notInTree=0 recordNodes=0 recordBandsRaised=0 recordBandsAtOrAbove=0 subjectsMirrored=1 parameters=0 parametersCompleted=0 scans=0 scansUnlocked=0 reFlySuppressedRows=0)",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R07 current-timeline walk",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][INFO][LedgerOrchestrator] Current-UT ledger recalculation: reason=golden cutoffUT=250",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=250, source=cutoff)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 provisional=True walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for ScienceModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=2.5 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][ScienceModule] Projected availability: current=2.5, minProjected=2.5, finalProjected=2.5, available=2.5, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for FundsModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=500 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][Funds] Projected availability: current=500, minProjected=500, finalProjected=500, available=500, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250, filteredOut=1, walkedSorted=4, firstTier=4 modules (16 dispatches), strategy=4 dispatches, secondTier=2 modules (8 dispatches), facilities=4 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][INFO][CrewReservations] Recomputed after cutoff walk: 0 reservations remain (permanent=0 temporary=0) (cutoffUT=250).",
            "log [Parsek][VERBOSE][CommittedMilestones] Subject science mirror cleared (patch-not-deferred): subjects=1 clock=250",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: tech-tree patch enabled (walkCutoff=250, techCutoff=250, baselineUt=null, baselineTechExclusions=0, targetCount=null, unaffordableTechDrops=null)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no committed resource scan at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no committed part upgrade purchase at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R08 time jump",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][INFO][LedgerOrchestrator] Current-UT ledger recalculation: reason=time-jump cutoffUT=250",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=250, source=cutoff)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 provisional=True walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for ScienceModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=2.5 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][ScienceModule] Projected availability: current=2.5, minProjected=2.5, finalProjected=2.5, available=2.5, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for FundsModule: cutoff=250 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=500 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][Funds] Projected availability: current=500, minProjected=500, finalProjected=500, available=500, futureActions=1, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=4, cutoffUT=250, filteredOut=1, walkedSorted=4, firstTier=4 modules (16 dispatches), strategy=4 dispatches, secondTier=2 modules (8 dispatches), facilities=4 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=250.0",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=250.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][INFO][CrewReservations] Recomputed after cutoff walk: 0 reservations remain (permanent=0 temporary=0) (cutoffUT=250).",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: tech-tree patch enabled (walkCutoff=250, techCutoff=250, baselineUt=null, baselineTechExclusions=0, targetCount=null, unaffordableTechDrops=null)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no committed resource scan at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no committed part upgrade purchase at or before the cutoff",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R09 after tombstones",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null, filteredOut=0, walkedSorted=5, firstTier=4 modules (20 dispatches), strategy=5 dispatches, secondTier=2 modules (10 dispatches), facilities=5 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=True (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=True, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R10 after tombstones with a tech node",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null, filteredOut=0, walkedSorted=5, firstTier=4 modules (20 dispatches), strategy=5 dispatches, secondTier=2 modules (10 dispatches), facilities=5 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=True (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=True, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: tech-tree patch enabled (walkCutoff=null, techCutoff=<double.MaxValue>, baselineUt=null, baselineTechExclusions=0, targetCount=null, unaffordableTechDrops=null)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R11 re-fly discard",
            "outcome ok",
            "timelineChanged 1",
            "held no",
            "row FundsEarning ut=100.0 rec=<null>",
            "row ScienceEarning ut=200.0 rec=<null>",
            "row ReputationEarning ut=300.0 rec=<null>",
            "row ScienceInitial ut=0.0 rec=<null>",
            "row ReputationInitial ut=0.0 rec=<null>",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=4",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=0, total=5",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialReputation: refusing to treat current reputation as initial because reputation timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 5 entries from 5 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=300, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][INFO][Reputation] ReputationInitial: seed=0, runningRep=0",
            "log [Parsek][VERBOSE][Funds] FundsEarning: +500, source=Other, runningBalance=500, totalEarnings=500, recordingId=(none)",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=(none)",
            "log [Parsek][VERBOSE][Reputation] RepEarning at UT=300.0: nominal=4.00, effective=4.00, runningRep=4.00 (recording=null)",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=5, actionsAfterCutoff=5, cutoffUT=null, filteredOut=0, walkedSorted=5, firstTier=4 modules (20 dispatches), strategy=5 dispatches, secondTier=2 modules (10 dispatches), facilities=5 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=True (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=True)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][WARN][CommittedFutureIndex] CurrentUT: Planetarium unavailable, using UT 0 (NullReferenceException)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchTechTree: no target tech set supplied \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchResourceScans: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFunds: Funding.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchPurchasedParts: ResearchAndDevelopment.Instance is null - skipping (the next recalc with R&D loaded applies it)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchUnlockedUpgrades: no finite cutoff and the clock is not ready - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchReputation: Reputation.Instance is null (sandbox mode) \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchFacilities: nothing to patch (total=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchDestructionState: SuppressUnityCallsForTesting - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchMilestones: pre-ledger progress seed not captured yet - skipping so progress the ledger has not recorded is not cleared",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchAll complete",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=5)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 5 actions walked",
            "== R12 held ledger full walk",
            "outcome ok",
            "timelineChanged 1",
            "held yes",
            "log [Parsek][VERBOSE][RecalcEngine] ClearModules: removed=0",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: MilestonesModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ContractsModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: ScienceModule, total=3",
            "log [Parsek][VERBOSE][RecalcEngine] Registered first-tier module: KerbalsModule, total=4",
            "log [Parsek][VERBOSE][RecalcEngine] Registered strategy transform module: StrategiesModule",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: FundsModule, total=1",
            "log [Parsek][VERBOSE][RecalcEngine] Registered second-tier module: ReputationModule, total=2",
            "log [Parsek][VERBOSE][RecalcEngine] Registered facilities module: FacilitiesModule",
            "log [Parsek][INFO][LedgerOrchestrator] Initialized: 8 modules registered",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 0 entries from 0 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null, filteredOut=0, walkedSorted=0, firstTier=4 modules (0 dispatches), strategy=0 dispatches, secondTier=2 modules (0 dispatches), facilities=0 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][INFO][LedgerOrchestrator] RecalculateAndPatch: KSP state patch skipped (ledger held from a newer Parsek version); funds, science, reputation, tech, contracts, facilities and strategies keep the save's values",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: deferred KSP state patch (ledger held from a newer Parsek version)",
            "log [Parsek][VERBOSE][CommittedMilestones] Apply skipped: no trusted clock (reason=deferred-patch, clock=NaN) - the next pass with a clock marks the nodes",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 0 actions walked",
            "== R13 held ledger cutoff walk",
            "outcome ok",
            "timelineChanged 1",
            "held yes",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=150",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=150.0",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 provisional=True walkClockUT=150.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for ScienceModule: cutoff=150 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=0 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][ScienceModule] Projected availability: current=0, minProjected=0, finalProjected=0, available=0, futureActions=0, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Projection reserve for FundsModule: cutoff=150 excludedSyntheticRows=0 excludedSyntheticDelta=0 minBalance=0 (an open contract's synthetic deadline fail is not held in the pool)",
            "log [Parsek][VERBOSE][Funds] Projected availability: current=0, minProjected=0, finalProjected=0, available=0, futureActions=0, deltaActions=0",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=150, filteredOut=0, walkedSorted=0, firstTier=4 modules (0 dispatches), strategy=0 dispatches, secondTier=2 modules (0 dispatches), facilities=0 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=0 cached=0 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=150.0",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=150.0 nextReleaseUT=INDEFINITE",
            "log [Parsek][INFO][CrewReservations] Recomputed after cutoff walk: 0 reservations remain (permanent=0 temporary=0) (cutoffUT=150).",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: deferred KSP state patch (ledger held from a newer Parsek version)",
            "log [Parsek][VERBOSE][CommittedMilestones] Apply: no committed once-ever milestone or record row at or before clock=150.0 (nor subject science, contract parameter or resource scan; reason=deferred-patch, reFlySuppressedRows=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 0 actions walked",
        }) + "\n";
    }
}
