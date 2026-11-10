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
    /// Characterization golden for <c>LedgerOrchestrator.TryRecordRecoveryKerbalExperience</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. Scenarios: null and empty input, no recovery recording, an
    /// ambiguous pick, a single written row, a batch with an event that converts to no row,
    /// a batch where nothing converts, a repeat of a written batch (all deduped), a batch
    /// where one row is already filed, and a written row whose kerbal name is empty. For each
    /// it records the return value, every ledger row (action ids masked: they are fresh
    /// GUIDs) and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class TryRecordRecoveryKerbalExperienceCharacterizationTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaa4aaaaaaaaaaaaaaaaaaa";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public TryRecordRecoveryKerbalExperienceCharacterizationTests()
        {
            // The written cases end in a full recalc walk. ParsekSettings' static constructor
            // logs once per process when a walk first touches it; run it before the sink so
            // the transcript does not depend on which class ran first. The walk formats some
            // numbers with the host culture, so the golden is read under the invariant one,
            // and its rate-limited lines read a fixed log clock.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(ParsekSettings).TypeHandle);
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ResetAll();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static void ResetAll()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static void AddRec(string id, string vesselName, double startUt, double endUt, string launchGuid)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                PreLaunchFunds = 50000.0,
                RecordedVesselGuid = launchGuid,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUt, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = endUt, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);
        }

        private static GameStateEvent XpEvent(string kerbal, double ut, string body = "Kerbin")
        {
            var entries = new List<KerbalCareerLogEntry>
            {
                new KerbalCareerLogEntry(1, "Recover", body)
            };
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ExperienceGained,
                key = kerbal,
                detail = $"flight=1;entries={KerbalCareerLogEntry.FormatSet(entries)};trait=Pilot"
            };
        }

        private static GameStateEvent NoRowEvent(string kerbal, double ut) =>
            new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.CrewStatusChanged,
                key = kerbal,
                detail = "from=Assigned;to=Available",
            };

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*).*$");
        private static readonly Regex GuidLike = new Regex(@"\b[0-9a-f]{32}\b");

        private void Case(StringBuilder sb, string label, IList<GameStateEvent> events,
            RecoveredVesselIdentity identity, double ut)
        {
            logLines.Clear();
            int result = LedgerOrchestrator.TryRecordRecoveryKerbalExperience(events, identity, ut);
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("return=").Append(result.ToString(CultureInfo.InvariantCulture)).Append('\n');
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var parent = new ConfigNode("ROOT");
                actions[i].SerializeInto(parent);
                foreach (string raw in parent.ToString().Replace("\r", "").Split('\n'))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    var m = ActionIdLine.Match(line);
                    if (m.Success)
                        line = m.Groups[1].Value + "<masked>";
                    sb.Append("row[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                        .Append(line).Append('\n');
                }
            }
            foreach (string line in logLines)
                sb.Append("log ").Append(GuidLike.Replace(line, "<guid>")).Append('\n');
        }

        private void Reset()
        {
            ResetAll();
            logLines.Clear();
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

        [Fact]
        public void TryRecordRecoveryKerbalExperience_Golden()
        {
            var sb = new StringBuilder();
            var flea = RecoveredVesselIdentity.FromRawName("Jumping Flea");

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, null);
            Case(sb, "X01 null events", null, flea, 347.5);
            Case(sb, "X02 empty events", new List<GameStateEvent>(), flea, 347.5);

            Reset();
            AddRec("rec-other", "Hopper", 100.0, 300.0, null);
            Case(sb, "X03 no recovery recording",
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 347.5) }, flea, 347.5);

            Reset();
            AddRec("rec-launch-1", "Hopper", 100.0, 500.0, null);
            AddRec("rec-launch-2", "Hopper", 600.0, 900.0, null);
            Case(sb, "X04 ambiguous pick",
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 1000.0), XpEvent("Bill Kerman", 1000.0) },
                RecoveredVesselIdentity.FromRawName("Hopper"), 1000.0);

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, GuidA);
            Case(sb, "X05 one row written",
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 347.5) },
                RecoveredVesselIdentity.FromRawName("Jumping Flea", GuidA), 347.5);

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, null);
            Case(sb, "X06 one event converts to no row",
                new List<GameStateEvent>
                {
                    NoRowEvent("Valentina Kerman", 347.5),
                    XpEvent("Jebediah Kerman", 347.5),
                    XpEvent("Bob Kerman", 347.5, "Mun"),
                }, flea, 347.5);

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, null);
            Case(sb, "X07 nothing converts",
                new List<GameStateEvent> { NoRowEvent("Valentina Kerman", 347.5) }, flea, 347.5);

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, null);
            var batch = new List<GameStateEvent> { XpEvent("Jebediah Kerman", 347.5), XpEvent("Bill Kerman", 347.5) };
            Case(sb, "X08a batch written", batch, flea, 347.5);
            Case(sb, "X08b same batch again, all deduped", batch, flea, 347.5);
            Case(sb, "X08c one filed, one new",
                new List<GameStateEvent> { XpEvent("Jebediah Kerman", 347.5), XpEvent("Bob Kerman", 347.5) },
                flea, 347.5);

            Reset();
            AddRec("rec-flea", "Jumping Flea", 100.0, 300.0, null);
            Case(sb, "X09 empty kerbal name",
                new List<GameStateEvent> { XpEvent("", 347.5) }, flea, 347.5);

            AssertGolden("TryRecordRecoveryKerbalExperience", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== X01 null events",
            "return=0",
            "== X02 empty events",
            "return=0",
            "== X03 no recovery recording",
            "return=0",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP refused: vessel='Jumping Flea' ut=347.5 kerbals=1 reason=no-recovery-recording",
            "== X04 ambiguous pick",
            "return=0",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 2 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Hopper' ut=1000.0 path=name-fallback reason=live-launch-guid-unknown nameMatches=2",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Hopper' ut=1000.0 dropped=0 remaining=2 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Hopper' ut=1000.0 path=name-fallback nameMatches=2 survivors=2 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-launch-2",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP refused: vessel='Hopper' ut=1000.0 kerbals=2 reason=ambiguous-recovery-recording survivors=2 nameMatches=2 guidDropped=0 tier=most-recent-ended corroboration=unknown-launch-guid wouldHavePicked=rec-launch-2 survivorIds=rec-launch-1,rec-launch-2",
            "== X05 one row written",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 347.5",
            "row[0] \t\ttype = 31",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-flea",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = -1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[0] \t}",
            "row[0] }",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=launch-guid identityMatches=1 identityNameMismatch=0 nameOnlyIgnored=0 unknownGuidSessionProvisionalAdmitted=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=launch-guid nameMatches=1 survivors=1 guidDropped=n/a skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP recorded: vessel='Jumping Flea' ut=347.5 recordingId=rec-flea rows=1 deduped=0 noAction=0 kerbals='Jebediah Kerman'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=recovery-kerbal-xp eventUT=347.5 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 1 entries from 1 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=347.5, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null, filteredOut=0, walkedSorted=1, firstTier=4 modules (4 dispatches), strategy=1 dispatches, secondTier=2 modules (2 dispatches), facilities=1 dispatches",
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
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 1 actions walked",
            "== X06 one event converts to no row",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 347.5",
            "row[0] \t\ttype = 31",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-flea",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = -1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 347.5",
            "row[1] \t\ttype = 31",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-flea",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = -1",
            "row[1] \t\tkerbalName = Bob Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tcareerEntries = 1,Recover,Mun",
            "row[1] \t}",
            "row[1] }",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP recorded: vessel='Jumping Flea' ut=347.5 recordingId=rec-flea rows=2 deduped=0 noAction=1 kerbals='Jebediah Kerman,Bob Kerman'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=recovery-kerbal-xp eventUT=347.5 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 2 entries from 2 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=347.5, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null, filteredOut=0, walkedSorted=2, firstTier=4 modules (8 dispatches), strategy=2 dispatches, secondTier=2 modules (4 dispatches), facilities=2 dispatches",
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
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 2 actions walked",
            "== X07 nothing converts",
            "return=0",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP refused: vessel='Jumping Flea' ut=347.5 kerbals=1 reason=no-convertible-row",
            "== X08a batch written",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 347.5",
            "row[0] \t\ttype = 31",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-flea",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = -1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 347.5",
            "row[1] \t\ttype = 31",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-flea",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = -1",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[1] \t}",
            "row[1] }",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP recorded: vessel='Jumping Flea' ut=347.5 recordingId=rec-flea rows=2 deduped=0 noAction=0 kerbals='Jebediah Kerman,Bill Kerman'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=recovery-kerbal-xp eventUT=347.5 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 2 entries from 2 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=347.5, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null, filteredOut=0, walkedSorted=2, firstTier=4 modules (8 dispatches), strategy=2 dispatches, secondTier=2 modules (4 dispatches), facilities=2 dispatches",
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
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 2 actions walked",
            "== X08b same batch again, all deduped",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 347.5",
            "row[0] \t\ttype = 31",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-flea",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = -1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 347.5",
            "row[1] \t\ttype = 31",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-flea",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = -1",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 2 duplicates from 2 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=2 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Recovery kerbal XP: all 2 row(s) already in the ledger for vessel='Jumping Flea' ut=347.5 recordingId=rec-flea",
            "== X08c one filed, one new",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 347.5",
            "row[0] \t\ttype = 31",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-flea",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = -1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 347.5",
            "row[1] \t\ttype = 31",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-flea",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = -1",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 347.5",
            "row[2] \t\ttype = 31",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\trecordingId = rec-flea",
            "row[2] \t\tseq = 3",
            "row[2] \t\tcseq = -1",
            "row[2] \t\tkerbalName = Bob Kerman",
            "row[2] \t\tkerbalRole = Pilot",
            "row[2] \t\tcareerEntries = 1,Recover,Kerbin",
            "row[2] \t}",
            "row[2] }",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 2 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=1 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=3",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP recorded: vessel='Jumping Flea' ut=347.5 recordingId=rec-flea rows=1 deduped=1 noAction=0 kerbals='Bob Kerman'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=recovery-kerbal-xp eventUT=347.5 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 3 entries from 3 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=347.5, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null, filteredOut=0, walkedSorted=3, firstTier=4 modules (12 dispatches), strategy=3 dispatches, secondTier=2 modules (6 dispatches), facilities=3 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=3)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 3 actions walked",
            "== X09 empty kerbal name",
            "return=0",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 1 entries from 1 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][INFO][LedgerOrchestrator] PickRecoveryRecordingId path: vessel='Jumping Flea' ut=347.5 path=name-fallback reason=live-launch-guid-unknown nameMatches=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId guid filter: vessel='Jumping Flea' ut=347.5 dropped=0 remaining=1 reason=live-launch-guid-unknown",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PickRecoveryRecordingId: vessel='Jumping Flea' ut=347.5 path=name-fallback nameMatches=1 survivors=1 guidDropped=0 skippedZombieNotCommitted=0 sessionProvisionalAdmitted=False tier=most-recent-ended bracketTie=n/a pick=rec-flea",
            "log [Parsek][WARN][GameStateEventConverter] ExperienceGained event has no kerbal name \u2014 skipping",
            "log [Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP refused: vessel='Jumping Flea' ut=347.5 kerbals=1 reason=no-convertible-row",
        }) + "\n";
    }
}
