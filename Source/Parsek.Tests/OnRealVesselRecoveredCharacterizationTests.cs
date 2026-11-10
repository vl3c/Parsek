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
    /// Characterization golden for <c>LedgerOrchestrator.OnRealVesselRecovered</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. Scenarios: a programmatic recovery (crew events suppressed), a zero pid, an
    /// active Re-Fly session (with and without a session id), no committed owner, a Parsek
    /// spawn's recovery, a positive launch-guid match with a null vessel name, two owners in
    /// two trees with one row already filed, and the same recovery delivered again. For each
    /// it records the return value, every ledger row (action ids masked: they are fresh
    /// GUIDs) and every log line, including the live recalc's.
    /// </summary>
    [Collection("Sequential")]
    public class OnRealVesselRecoveredCharacterizationTests : IDisposable
    {
        private const string Name = "CTR Lander";
        private const uint CraftPid = 1344998135u;
        private const uint SpawnPid = 2718281828u;
        private const string LaunchGuid = "5d1f0b6a2c7e4e0f9a3b8c1d2e4f6a7b";
        private const string SpawnGuid = "abcdefabcdefabcdefabcdefabcdefab";
        private const string OtherLaunchGuid = "0f0e0d0c0b0a09080706050403020100";

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly bool priorSuppressCrewEvents;

        public OnRealVesselRecoveredCharacterizationTests()
        {
            // The written cases end in a live recalc walk. ParsekSettings' static constructor
            // logs once per process when a walk first touches it; run it before the sink so
            // the transcript does not depend on which class ran first. The walk formats some
            // numbers with the host culture, so the golden is read under the invariant one,
            // and its rate-limited lines read a fixed log clock.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(ParsekSettings).TypeHandle);
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            priorSuppressCrewEvents = GameStateRecorder.SuppressCrewEvents;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            GameStateRecorder.SuppressCrewEvents = priorSuppressCrewEvents;
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static void ResetAll()
        {
            GameStateRecorder.SuppressCrewEvents = false;
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static ConfigNode LandedSnapshot()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", Name);
            return snapshot;
        }

        private static Recording LandedTip(string id, string treeId = null, string guid = LaunchGuid)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = Name,
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
                VesselSnapshot = LandedSnapshot(),
                TerminalStateValue = TerminalState.Landed,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120 },
                    new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120 },
                },
            };
        }

        private static Recording SpawnedTip(string id, string treeId = null)
        {
            var rec = LandedTip(id, treeId);
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = SpawnPid;
            return rec;
        }

        private static void SetReFlyMarker(string sessionId, Recording tip)
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = sessionId,
                    OriginChildRecordingId = tip.RecordingId,
                    ActiveReFlyRecordingId = tip.RecordingId,
                    RewindPointId = "rp_test",
                    TreeId = tip.TreeId,
                },
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*).*$");

        private void Case(StringBuilder sb, string label, double ut, uint pid, string guid, string vesselName)
        {
            logLines.Clear();
            int result = LedgerOrchestrator.OnRealVesselRecovered(ut, pid, guid, vesselName);
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
                sb.Append("log ").Append(line).Append('\n');
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
        public void OnRealVesselRecovered_Golden()
        {
            var sb = new StringBuilder();

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));
            GameStateRecorder.SuppressCrewEvents = true;
            Case(sb, "V01 crew events suppressed", 400.0, SpawnPid, SpawnGuid, Name);

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));
            Case(sb, "V02 zero pid, null name", 400.0, 0u, LaunchGuid, null);

            Reset();
            var tip3 = SpawnedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(tip3);
            SetReFlyMarker("sess_vr_skip", tip3);
            Case(sb, "V03 Re-Fly session active", 400.0, SpawnPid, SpawnGuid, Name);

            Reset();
            var tip4 = SpawnedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(tip4);
            SetReFlyMarker(null, tip4);
            Case(sb, "V04 Re-Fly session with no id", 400.0, SpawnPid, SpawnGuid, Name);

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-landed"));
            Case(sb, "V05 no committed owner (other launch)", 400.0, CraftPid, OtherLaunchGuid, Name);

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-landed"));
            Case(sb, "V06 no owner, null guid and name", 400.0, CraftPid, null, null);

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));
            Case(sb, "V07 a Parsek spawn recovered", 400.0, SpawnPid, SpawnGuid, Name);

            Reset();
            var noTerminal = LandedTip("rec-landed");
            noTerminal.TerminalStateValue = null;
            RecordingStore.AddRecordingWithTreeForTesting(noTerminal);
            Case(sb, "V08 launch-guid match, no terminal, null name", 412.4, CraftPid, LaunchGuid, null);

            Reset();
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-first", "tree-1"));
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-second", "tree-2"));
            Ledger.AddAction(new GameAction
            {
                UT = 400.0,
                Type = GameActionType.VesselRecovered,
                RecordingId = "rec-first",
                RecoveredVesselName = Name,
                RecoveredVesselPid = CraftPid,
            });
            Case(sb, "V09a two owners, one filed", 400.0, CraftPid, LaunchGuid, Name);
            Case(sb, "V09b the same recovery again", 400.06, CraftPid, LaunchGuid, Name);

            AssertGolden("OnRealVesselRecovered", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== V01 crew events suppressed",
            "return=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row skipped: vessel='CTR Lander' pid=2718281828 ut=400.0 - crew events suppressed (programmatic recovery)",
            "== V02 zero pid, null name",
            "return=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row skipped: vessel='(null)' ut=400.0 - no vessel pid",
            "== V03 Re-Fly session active",
            "return=0",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery row skipped: vessel='CTR Lander' pid=2718281828 ut=400.0 - Re-Fly session sess_vr_skip active",
            "== V04 Re-Fly session with no id",
            "return=0",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery row skipped: vessel='CTR Lander' pid=2718281828 ut=400.0 - Re-Fly session <no-id> active",
            "== V05 no committed owner (other launch)",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row: vessel='CTR Lander' pid=1344998135 guid=0f0e0d0c0b0a09080706050403020100 ut=400.0 - no committed recording continues this vessel, nothing to write",
            "== V06 no owner, null guid and name",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row: vessel='(null)' pid=1344998135 guid=(null) ut=400.0 - no committed recording continues this vessel, nothing to write",
            "== V07 a Parsek spawn recovered",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 400",
            "row[0] \t\ttype = 35",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-tip",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tvesselName = CTR Lander",
            "row[0] \t\tvesselPid = 2718281828",
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
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] Added action: type=VesselRecovered, ut=400, recordingId=rec-tip, writer=StockApplied, cseq=1, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovered recorded: vessel='CTR Lander' pid=2718281828 recoveryUT=400.0 recordingId=rec-tip terminal=Landed spawnedPid=2718281828",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery rows: vessel='CTR Lander' pid=2718281828 ut=400.0 owners=1 written=1 deduped=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=vessel-recovered eventUT=400 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][KerbalsModule] PopulateCrewEndStates: recording='CTR Lander' (id=rec-tip) has no start crew source -- leaving unresolved",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 1 entries from 1 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=400, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass: 0 crewed recording(s) of a vessel that joined its tree can end another tree's open-ended hold on that vessel (uncrewed skipped=1)",
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
            "== V08 launch-guid match, no terminal, null name",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 412.4",
            "row[0] \t\ttype = 35",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-landed",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tvesselPid = 1344998135",
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
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] Added action: type=VesselRecovered, ut=412.4, recordingId=rec-landed, writer=StockApplied, cseq=1, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovered recorded: vessel='(null)' pid=1344998135 recoveryUT=412.4 recordingId=rec-landed terminal=<none> spawnedPid=0",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery rows: vessel='(null)' pid=1344998135 ut=412.4 owners=1 written=1 deduped=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=vessel-recovered eventUT=412.4 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][KerbalsModule] PopulateCrewEndStates: recording='CTR Lander' (id=rec-landed) has no start crew source -- leaving unresolved",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 1 entries from 1 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=412.4, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=1 cached=1 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass: 0 crewed recording(s) of a vessel that joined its tree can end another tree's open-ended hold on that vessel (uncrewed skipped=1)",
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
            "== V09a two owners, one filed",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 400",
            "row[0] \t\ttype = 35",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-first",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tvesselName = CTR Lander",
            "row[0] \t\tvesselPid = 1344998135",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 400",
            "row[1] \t\ttype = 35",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-second",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tvesselName = CTR Lander",
            "row[1] \t\tvesselPid = 1344998135",
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
            "log [Parsek][VERBOSE][ERS] Rebuilt: 2 entries from 2 committed (skippedNotCommitted=0 skippedSuperseded=0 skippedRewindRetired=0 skippedSuppressed=0 marker=none)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 1 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=1 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row: recordingId=rec-first ut=400.0 already in the ledger",
            "log [Parsek][VERBOSE][Ledger] Added action: type=VesselRecovered, ut=400, recordingId=rec-second, writer=StockApplied, cseq=2, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovered recorded: vessel='CTR Lander' pid=1344998135 recoveryUT=400.0 recordingId=rec-second terminal=Landed spawnedPid=0",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery rows: vessel='CTR Lander' pid=1344998135 ut=400.0 owners=2 written=1 deduped=1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Live-event recalc decision: reason=vessel-recovered eventUT=400 hasFutureLedgerActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] SeedInitialFunds: deferring - Funding.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreLedgerProgressSeed: deferring capture - ProgressTracking.Instance is null (milestone patch skipped until captured)",
            "log [Parsek][VERBOSE][KerbalsModule] PopulateCrewEndStates: recording='CTR Lander' (id=rec-first) has no start crew source -- leaving unresolved",
            "log [Parsek][VERBOSE][KerbalsModule] PopulateCrewEndStates: recording='CTR Lander' (id=rec-second) has no start crew source -- leaving unresolved",
            "log [Parsek][VERBOSE][ELS] Rebuilt: 2 entries from 2 actions (skippedTombstoned=0 tombstonesTotal=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null",
            "log [Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones",
            "log [Parsek][VERBOSE][Contracts] Reset: cleared 0 active contracts, 0 credited contracts, 0 credited parameters, 0 terminal contracts, 0 terminal actions, 0 terminal outcomes, 0 deadline-expired contracts, 0 explicitly resolved contracts, 0 prepass explicit resolutions",
            "log [Parsek][VERBOSE][ScienceModule] Reset: cleared 0 subjects, runningScience=0, totalCommittedSpendings=0, totalEffectiveEarnings=0",
            "log [Parsek][VERBOSE][Strategies] Reset: cleared 0 active strategies",
            "log [Parsek][VERBOSE][Funds] Reset: prevSeed=0, prevBalance=0, prevSpendings=0, prevEarnings=0",
            "log [Parsek][VERBOSE][Reputation] Reset: runningRep 0.00 -> 0",
            "log [Parsek][VERBOSE][Facilities] Reset: cleared 0 tracked facilities",
            "log [Parsek][VERBOSE][Contracts] PrePass: no expired deadline contracts found (tracked=0, nowUT=400, source=lastActionUT)",
            "log [Parsek][VERBOSE][ScienceModule] ComputeTotalSpendings: spendingCount=0, strategySetupCount=0, strategyExchangeCount=0, totalCommittedSpendings=0",
            "log [Parsek][VERBOSE][KerbalsModule] PrePass summary: examined=2 cached=2 nullRecordings=0 missingIds=0 rawCrewRecordings=0 rawCrewMembers=0 loopingChains=0 walkClockUT=unknown",
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
            "== V09b the same recovery again",
            "return=0",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 400",
            "row[0] \t\ttype = 35",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-first",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tvesselName = CTR Lander",
            "row[0] \t\tvesselPid = 1344998135",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 400",
            "row[1] \t\ttype = 35",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-second",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tvesselName = CTR Lander",
            "row[1] \t\tvesselPid = 1344998135",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 1 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=1 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row: recordingId=rec-first ut=400.1 already in the ledger",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 1 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=1 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Vessel recovery row: recordingId=rec-second ut=400.1 already in the ledger",
            "log [Parsek][INFO][LedgerOrchestrator] Vessel recovery rows: vessel='CTR Lander' pid=1344998135 ut=400.1 owners=2 written=0 deduped=2",
        }) + "\n";
    }
}
