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
    /// Characterization golden for <c>LedgerOrchestrator.NotifyLedgerTreeCommitted</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. Each scenario stages a tree (single, multi, chained segments with a gap,
    /// pending science tagged in and out of the tree, untagged science by window) and commits
    /// it, some with a fault injected before or after a recording's science persists. It
    /// records whether the call threw, the ledger rows it left (type, recording, UT, subject,
    /// science), the pending subjects that remain, and every log line, with random action ids
    /// reduced to their shape. Science values are exact in float so no float-to-double
    /// digit string (which differs between the .NET Framework and mono) reaches a log line. A mismatch writes the actual transcript next to the test binary.
    /// </summary>
    [Collection("Sequential")]
    public class NotifyLedgerTreeCommittedCharacterizationTests : IDisposable
    {
        private static readonly Regex ActionIdPattern = new Regex("act_[0-9a-f]{32}");
        private static readonly Regex GuidPattern = new Regex("[0-9a-f]{32}");
        // double.MaxValue as default ToString text: mono prints 17 digits, the .NET
        // Framework 15, so the golden reads it by shape.
        private static readonly Regex MaxValuePattern = new Regex(@"1\.79769313486\d*E\+308");

        private readonly CultureInfo savedCulture;

        private readonly List<string> logLines = new List<string>();

        public NotifyLedgerTreeCommittedCharacterizationTests()
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
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
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
        }

        private static void ResetAll()
        {
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = null;
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
        }

        private sealed class SentinelFaultException : Exception
        {
            public SentinelFaultException(string msg) : base(msg) { }
        }

        private static Recording Rec(string id, double startUT, double endUT,
            string chainId = null, int chainIndex = -1)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Test " + id,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
            if (chainId != null)
            {
                rec.ChainId = chainId;
                rec.ChainIndex = chainIndex;
            }
            RecordingStore.AddCommittedInternal(rec);
            return rec;
        }

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = "Tree " + id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
                ActiveRecordingId = recs.Length > 0 ? recs[recs.Length - 1].RecordingId : null,
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        private static void Subject(string subjectId, float science, double captureUT,
            string recordingId = "", string reasonKey = "ScienceTransmission")
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = science + 10f,
                captureUT = captureUT,
                recordingId = recordingId,
                reasonKey = reasonKey
            });
        }

        private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private void Commit(StringBuilder sb, string label, RecordingTree tree)
        {
            logLines.Clear();
            string outcome = "ok";
            try
            {
                LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            }
            catch (Exception ex)
            {
                outcome = "threw " + ex.GetType().Name + ": " + ex.Message;
            }
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("outcome ").Append(outcome).Append('\n');
            foreach (var a in Ledger.Actions)
            {
                sb.Append("row ").Append(a.Type)
                    .Append(" rec=").Append(a.RecordingId ?? "<null>")
                    .Append(" ut=").Append(F(a.UT))
                    .Append(" subject=").Append(a.SubjectId ?? "<null>")
                    .Append(" sci=").Append(a.ScienceAwarded.ToString("F2", CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            sb.Append("pending [");
            for (int i = 0; i < GameStateRecorder.PendingScienceSubjects.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(GameStateRecorder.PendingScienceSubjects[i].subjectId);
            }
            sb.Append("]\n");
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

            // T01: a null tree returns at once.
            ResetAll();
            Commit(sb, "T01 null tree", null);

            // T02: an empty tree files nothing, walks nothing, still prunes and summarises.
            ResetAll();
            Commit(sb, "T02 empty tree", Tree("t02"));

            // T03: one recording, its own tagged science: filed, one walk, pending cleared.
            ResetAll();
            Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "t03-a");
            Subject("temperatureScan@MunFlyingHigh", 4.25f, 150.0, "t03-a");
            Commit(sb, "T03 single recording", Tree("t03", Rec("t03-a", 100.0, 200.0)));

            // T04: a chain of three segments with gaps; untagged science inside a gap lands on
            // the continuation whose window was extended back to its predecessor's end.
            ResetAll();
            Subject("barometerScan@KerbinFlyingLow", 1.5f, 210.0);
            Subject("crewReport@KerbinFlyingHigh", 3.0f, 330.0);
            Commit(sb, "T04 chain gaps", Tree("t04",
                Rec("t04-0", 100.0, 200.0, "chain-x", 0),
                Rec("t04-1", 220.0, 300.0, "chain-x", 1),
                Rec("t04-2", 340.0, 400.0, "chain-x", 2)));

            // T05: science tagged to a recording outside the tree stays pending after success.
            ResetAll();
            Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "t05-a");
            Subject("seismicScan@MunSrfLanded", 6.0f, 130.0, "t05-elsewhere");
            Commit(sb, "T05 unrelated science preserved", Tree("t05",
                Rec("t05-a", 100.0, 200.0), Rec("t05-b", 200.0, 300.0)));

            // T06: a fault before the first recording's rows: nothing filed, no catch walk,
            // pending retained, the exception propagates.
            ResetAll();
            Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "t06-a");
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = _ => throw new SentinelFaultException("pre t06");
            Commit(sb, "T06 fault before the first recording", Tree("t06", Rec("t06-a", 100.0, 200.0)));
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;

            // T07: a fault on the second recording: the first one's rows stay and are walked
            // in the catch, the second one's science is retained.
            ResetAll();
            Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "t07-a");
            Subject("temperatureScan@MunFlyingHigh", 4.25f, 250.0, "t07-b");
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = id =>
            {
                if (id == "t07-b") throw new SentinelFaultException("pre t07-b");
            };
            Commit(sb, "T07 fault on the second recording", Tree("t07",
                Rec("t07-a", 100.0, 200.0), Rec("t07-b", 200.0, 300.0)));
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;

            // T08: a fault after the only recording's science persisted: rows stay, walked in
            // the catch, the subjects are already removed.
            ResetAll();
            Subject("temperatureScan@MunFlyingHigh", 4.25f, 150.0, "t08-a");
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = _ =>
                throw new SentinelFaultException("post t08");
            Commit(sb, "T08 fault after science persisted", Tree("t08", Rec("t08-a", 100.0, 200.0)));
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = null;

            // T09: a post-persist fault on the second recording with unrelated science pending:
            // partially retained.
            ResetAll();
            Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "t09-a");
            Subject("temperatureScan@MunFlyingHigh", 4.25f, 250.0, "t09-b");
            Subject("seismicScan@MunSrfLanded", 6.0f, 130.0, "t09-elsewhere");
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = id =>
            {
                if (id == "t09-b") throw new SentinelFaultException("post t09-b");
            };
            Commit(sb, "T09 partial retention after failure", Tree("t09",
                Rec("t09-a", 100.0, 200.0), Rec("t09-b", 200.0, 300.0)));
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = null;

            // T10: a tree with no pending science at all: no pending-science line.
            ResetAll();
            Commit(sb, "T10 no science", Tree("t10", Rec("t10-a", 100.0, 200.0)));

            return sb.ToString();
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_Golden()
        {
            AssertGolden("NotifyLedgerTreeCommitted_Golden", Golden, RunAllScenarios());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== T01 null tree",
            "outcome ok",
            "pending []",
            "== T02 empty tree",
            "outcome ok",
            "pending []",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: tree='Tree t02' recordings=0, chainSegments=0, gapsClosed=0, reFlyWindowsExtended=0, retiredProvisionalRows=0",
            "== T03 single recording",
            "outcome ok",
            "row ScienceEarning rec=t03-a ut=120.0 subject=crewReport@KerbinSrfLanded sci=2.50",
            "row ScienceEarning rec=t03-a ut=150.0 subject=temperatureScan@MunFlyingHigh sci=4.25",
            "row ScienceInitial rec=<null> ut=0.0 subject=<null> sci=0.00",
            "pending []",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=2 from 2 subjects, recordingId=t03-a, tagged=2, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=2, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't03-a' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=2",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 2 added, 0 updated, skipped=0 (total=2)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't03-a': 2 actions added to ledger (events=0, science=2, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: cleared PendingScienceSubjects (snapshot=2, routed=2, removed=2)",
            "log [Parsek][INFO][LedgerOrchestrator] NotifyLedgerTreeCommitted: filed rows for 1 recording(s) of tree 'Tree t03' before one ledger walk (chainSegments=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=3",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 3 entries from 3 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=150, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=t03-a",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=temperatureScan@MunFlyingHigh, awarded=4.25, effective=4.25, creditedTotal=4.25, maxValue=14.25, runningScience=6.75, recordingId=t03-a",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null, filteredOut=0, walkedSorted=3, firstTier=4 modules (12 dispatches), strategy=3 dispatches, secondTier=2 modules (6 dispatches), facilities=3 dispatches",
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
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=2, rebuilt with 2 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=2, scienceActions=2, actions=3)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 3 actions walked",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Earnings reconcile skipped: sandbox / tracker unavailable (funds=false sci=false rep=false)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: tree='Tree t03' recordings=1, chainSegments=0, gapsClosed=0, reFlyWindowsExtended=0, retiredProvisionalRows=0",
            "== T04 chain gaps",
            "outcome ok",
            "row ScienceEarning rec=t04-1 ut=210.0 subject=barometerScan@KerbinFlyingLow sci=1.50",
            "row ScienceEarning rec=t04-2 ut=330.0 subject=crewReport@KerbinFlyingHigh sci=3.00",
            "row ScienceInitial rec=<null> ut=0.0 subject=<null> sci=0.00",
            "pending []",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Chain gap closure: extended startUT from 220.0 to 200.0 for recording 't04-1' (chain=chain-x, idx=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Chain gap closure: extended startUT from 340.0 to 300.0 for recording 't04-2' (chain=chain-x, idx=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceSubjects: empty or null subjects list, returning 0 actions",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't04-0' has no points \u2014 skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=0, writer=StockApplied, total=0",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't04-0': 0 actions added to ledger (events=0, science=0, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Chain gap closure: extended startUT from 220.0 to 200.0 for recording 't04-1' (chain=chain-x, idx=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t04-1, tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't04-1' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't04-1': 1 actions added to ledger (events=0, science=1, cost=0, kerbals=0, dedup=0, startUT=200.0, endUT=300.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Chain gap closure: extended startUT from 340.0 to 300.0 for recording 't04-2' (chain=chain-x, idx=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t04-2, tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't04-2' has no points \u2014 skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=2",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=2)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't04-2': 1 actions added to ledger (events=0, science=1, cost=0, kerbals=0, dedup=0, startUT=300.0, endUT=400.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: cleared PendingScienceSubjects (snapshot=2, routed=2, removed=2)",
            "log [Parsek][INFO][LedgerOrchestrator] NotifyLedgerTreeCommitted: filed rows for 3 recording(s) of tree 'Tree t04' before one ledger walk (chainSegments=3)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=3",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 3 entries from 3 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=330, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=3 cached=3 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=barometerScan@KerbinFlyingLow, awarded=1.5, effective=1.5, creditedTotal=1.5, maxValue=11.5, runningScience=1.5, recordingId=t04-1",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinFlyingHigh, awarded=3, effective=3, creditedTotal=3, maxValue=13, runningScience=4.5, recordingId=t04-2",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=2, rebuilt with 2 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=2, scienceActions=2, actions=3)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: tree='Tree t04' recordings=3, chainSegments=3, gapsClosed=2, reFlyWindowsExtended=0, retiredProvisionalRows=0",
            "== T05 unrelated science preserved",
            "outcome ok",
            "row ScienceEarning rec=t05-a ut=120.0 subject=crewReport@KerbinSrfLanded sci=2.50",
            "row ScienceInitial rec=<null> ut=0.0 subject=<null> sci=0.00",
            "pending [seismicScan@MunSrfLanded]",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t05-a, tagged=1, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't05-a' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't05-a': 1 actions added to ledger (events=0, science=1, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceSubjects: empty or null subjects list, returning 0 actions",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't05-b' has no points \u2014 skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=0, writer=StockApplied, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't05-b': 0 actions added to ledger (events=0, science=0, cost=0, kerbals=0, dedup=0, startUT=200.0, endUT=300.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: preserved unrelated PendingScienceSubjects after success (snapshot=2, routed=1, removed=1, remaining=1)",
            "log [Parsek][INFO][LedgerOrchestrator] NotifyLedgerTreeCommitted: filed rows for 2 recording(s) of tree 'Tree t05' before one ledger walk (chainSegments=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=2",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 2 entries from 2 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null | suppressed=1",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=120, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=2 cached=2 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=t05-a",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null, filteredOut=0, walkedSorted=2, firstTier=4 modules (8 dispatches), strategy=2 dispatches, secondTier=2 modules (4 dispatches), facilities=2 dispatches | suppressed=1",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=1, rebuilt with 1 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 2 actions walked | suppressed=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: tree='Tree t05' recordings=2, chainSegments=0, gapsClosed=0, reFlyWindowsExtended=0, retiredProvisionalRows=0",
            "== T06 fault before the first recording",
            "outcome threw SentinelFaultException: pre t06",
            "pending [crewReport@KerbinSrfLanded]",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: retained PendingScienceSubjects after failure (snapshot=1, routed=1, removed=0, remaining=1)",
            "== T07 fault on the second recording",
            "outcome threw SentinelFaultException: pre t07-b",
            "row ScienceEarning rec=t07-a ut=120.0 subject=crewReport@KerbinSrfLanded sci=2.50",
            "row ScienceInitial rec=<null> ut=0.0 subject=<null> sci=0.00",
            "pending [temperatureScan@MunFlyingHigh]",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t07-a, tagged=1, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't07-a' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't07-a': 1 actions added to ledger (events=0, science=1, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit-failed currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=2",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 2 entries from 2 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=120, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=2 cached=2 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=t07-a",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=1, scienceActions=1, actions=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: partially retained PendingScienceSubjects after failure (snapshot=2, routed=2, removed=1, remaining=1)",
            "== T08 fault after science persisted",
            "outcome threw SentinelFaultException: post t08",
            "row ScienceEarning rec=t08-a ut=150.0 subject=temperatureScan@MunFlyingHigh sci=4.25",
            "pending []",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t08-a, tagged=1, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't08-a' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: already removed PendingScienceSubjects before failure (snapshot=1, routed=1, removed=1, remaining=0)",
            "== T09 partial retention after failure",
            "outcome threw SentinelFaultException: post t09-b",
            "row ScienceEarning rec=t09-a ut=120.0 subject=crewReport@KerbinSrfLanded sci=2.50",
            "row ScienceEarning rec=t09-b ut=250.0 subject=temperatureScan@MunFlyingHigh sci=4.25",
            "row ScienceInitial rec=<null> ut=0.0 subject=<null> sci=0.00",
            "pending [seismicScan@MunSrfLanded]",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t09-a, tagged=1, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't09-a' has no points \u2014 skipping",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't09-a': 1 actions added to ledger (events=0, science=1, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=t09-b, tagged=1, untaggedInWindow=0, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't09-b' has no points \u2014 skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=2",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit-failed currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][INFO][Ledger] Seeded initial science: amount=0, total=3",
            "log [Parsek][WARN][LedgerOrchestrator] SeedInitialScience: refusing to treat current science as initial because science timeline actions already exist and no baseline was available",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 3 entries from 3 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null | suppressed=1",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=250, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=2 cached=2 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][ScienceModule] ScienceInitial: seed=0, runningScience=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=crewReport@KerbinSrfLanded, awarded=2.5, effective=2.5, creditedTotal=2.5, maxValue=12.5, runningScience=2.5, recordingId=t09-a",
            "log [Parsek][VERBOSE][ScienceModule] Earning: subject=temperatureScan@MunFlyingHigh, awarded=4.25, effective=4.25, creditedTotal=4.25, maxValue=14.25, runningScience=6.75, recordingId=t09-b",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=3, actionsAfterCutoff=3, cutoffUT=null, filteredOut=0, walkedSorted=3, firstTier=4 modules (12 dispatches), strategy=3 dispatches, secondTier=2 modules (6 dispatches), facilities=3 dispatches | suppressed=1",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=2, rebuilt with 2 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=2, scienceActions=2, actions=3)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 3 actions walked | suppressed=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: partially retained PendingScienceSubjects after failure (snapshot=3, routed=2, removed=2, remaining=1)",
            "== T10 no science",
            "outcome ok",
            "pending []",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceSubjects: empty or null subjects list, returning 0 actions",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recording 't10-a' has no points \u2014 skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=0, writer=StockApplied, total=0",
            "log [Parsek][INFO][LedgerOrchestrator] Committed recording 't10-a': 0 actions added to ledger (events=0, science=0, cost=0, kerbals=0, dedup=0, startUT=100.0, endUT=200.0)",
            "log [Parsek][INFO][LedgerOrchestrator] NotifyLedgerTreeCommitted: filed rows for 1 recording(s) of tree 'Tree t10' before one ledger walk (chainSegments=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=tree-commit currentUT=<double.MaxValue> currentUtReady=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
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
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][Funds] ComputeTotalSpendings: spendings=0, penalties=0, facilityCosts=0, hireCosts=0, setupCosts=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][RecalcEngine] PrePass stable action list; skipped second sort",
            "log [Parsek][INFO][KerbalsModule] PostWalk summary: reservations=0 permanent=0 temporary=0 slots=0 retired=0 slotsCreated=0 released=0 respawnPending=0 walkClockUT=unknown nextReleaseUT=INDEFINITE",
            "log [Parsek][VERBOSE][RecalcEngine] Recalculate complete: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null, filteredOut=0, walkedSorted=0, firstTier=4 modules (0 dispatches), strategy=0 dispatches, secondTier=2 modules (0 dispatches), facilities=0 dispatches",
            "log [Parsek][VERBOSE][Contracts] SetMaxSlots: maxSlots=2",
            "log [Parsek][VERBOSE][Strategies] SetMaxSlots: maxSlots=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] UpdateSlotLimits: MissionControl level=1 -> 2 contract slots, Administration level=1 -> 1 strategy slots",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: drawdown-guard authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, tombstone=False, rewindResAdjust=False, reFlyDiscard=False)",
            "log [Parsek][VERBOSE][KerbalsModule] ApplyToRoster: no roster \u2014 skipping",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: no cutoff supplied \u2014 skipping tech-tree patch to preserve live unlocks; committed tech unlocks skipped (reason=clock-not-ready, liveUT=0)",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchContracts: ContractSystem.Instance is null \u2014 skipping",
            "log [Parsek][VERBOSE][KspStatePatcher] PatchStrategies: Unity calls suppressed for testing - skipping",
            "log [Parsek][INFO][GameStateStore] RebuildCommittedScienceSubjects: before=0, rebuilt with 0 subjects",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RebuildCommittedScienceFromSurvivingLedger: rebuilt committedScienceSubjects from surviving ledger (subjects=0, scienceActions=0, actions=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch complete: 0 actions walked",
            "log [Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: tree='Tree t10' recordings=1, chainSegments=0, gapsClosed=0, reFlyWindowsExtended=0, retiredProvisionalRows=0",
        }) + "\n";
    }
}
