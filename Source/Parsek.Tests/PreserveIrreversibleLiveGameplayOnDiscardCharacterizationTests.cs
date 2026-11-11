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
    /// Characterization golden for <c>LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. Scenarios: a null and an empty id set; nothing to re-home but a
    /// strategy conversion row to untag; every irreversible event kind, passed as a list
    /// rather than a set; a committed id among the discarded ones; science subjects only;
    /// a ledger row the dedup drops; irreversible events the converter turns into no row; and
    /// events, science and a conversion row together. For each it records every ledger row
    /// (action ids masked: they are fresh GUIDs), the store events, the pending science
    /// subjects, the committed science and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class PreserveIrreversibleLiveGameplayOnDiscardCharacterizationTests : IDisposable
    {
        private const string RecA = "rec-A";
        private const string RecB = "rec-B";
        private const string Other = "rec-other";
        private const string Committed = "rec-committed";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;
        private readonly CultureInfo savedCulture;

        public PreserveIrreversibleLiveGameplayOnDiscardCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.SuppressLogging = true;
            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static GameStateEvent AddEvent(GameStateEventType type, double ut, string key, string detail,
            string tag, double before = 0.0, double after = 0.0)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = tag,
                valueBefore = before,
                valueAfter = after,
            };
            GameStateStore.AddEvent(ref e);
            return e;
        }

        private static void AddSubject(string subjectId, float science, double captureUT, string tag)
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = 10f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = tag,
                scienceGainMultiplier = 1f,
            });
        }

        private static void AddConversionRow(double ut, float funds, string tag)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = funds,
                FundsSpendingSource = FundsSpendingSource.StrategyConverter,
                RecordingId = tag,
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*).*$");

        private void Case(StringBuilder sb, string label, ICollection<string> ids, string reason = "test-discard")
        {
            logLines.Clear();
            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(ids, reason);
            sb.Append("== ").Append(label).Append('\n');

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

            var events = GameStateStore.Events;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                sb.Append("event ").Append(e.eventType).Append(' ').Append(e.key ?? "<null>")
                    .Append(" tag=").Append(e.recordingId ?? "<null>")
                    .Append(" seq=").Append(e.captureSequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            var pending = GameStateRecorder.PendingScienceSubjects;
            for (int i = 0; i < pending.Count; i++)
            {
                var s = pending[i];
                sb.Append("pending ").Append(s.subjectId ?? "<null>")
                    .Append(" tag=").Append(s.recordingId ?? "<null>").Append('\n');
            }

            var committed = GameStateStore.GetCommittedScienceSubjectIds();
            committed.Sort(StringComparer.Ordinal);
            foreach (string id in committed)
            {
                GameStateStore.TryGetCommittedSubjectScience(id, out float sci);
                sb.Append("committedScience ").Append(id).Append('=')
                    .Append(sci.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }

            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
        }

        private void Reset()
        {
            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
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
        public void PreserveIrreversibleLiveGameplayOnDiscard_Golden()
        {
            var sb = new StringBuilder();

            Reset();
            AddEvent(GameStateEventType.ContractCompleted, 200.0, "guid-c", "fundsReward=100", RecA);
            Case(sb, "P01 null ids", null);

            Reset();
            AddEvent(GameStateEventType.ContractCompleted, 200.0, "guid-c", "fundsReward=100", RecA);
            Case(sb, "P02 empty ids", new List<string>());

            Reset();
            AddEvent(GameStateEventType.ContractCompleted, 200.0, "guid-other", "fundsReward=100", Other);
            AddEvent(GameStateEventType.ScienceChanged, 210.0, "ScienceTransmission", "", RecA, 0.0, 40.0);
            AddEvent(GameStateEventType.ScienceChanged, 220.0, GameStateEventConverter.StrategyInputReasonKey,
                "", RecA, 40.0, 90.0);
            AddEvent(GameStateEventType.TechResearched, 230.0, "basicRocketry", "cost=5", RecA);
            AddEvent(GameStateEventType.ContractCompleted, 240.0, "guid-untagged", "fundsReward=100", "");
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 250.0, Other);
            AddConversionRow(260.0, 300f, RecA);
            AddConversionRow(270.0, 400f, Other);
            Case(sb, "P03 nothing to re-home, one conversion row untagged", new HashSet<string> { RecA });

            Reset();
            AddEvent(GameStateEventType.ContractCompleted, 300.0, "guid-done",
                "fundsReward=2080;repReward=1;sciReward=1", RecA);
            AddEvent(GameStateEventType.ContractParameterCompleted, 301.0, "guid-done/param-1",
                "fundsReward=150;repReward=2;sciReward=0", RecA);
            AddEvent(GameStateEventType.ContractFailed, 302.0, "guid-failed",
                "fundsPenalty=500;repPenalty=10", RecB);
            AddEvent(GameStateEventType.ContractCancelled, 303.0, "guid-cancelled",
                "fundsPenalty=250;repPenalty=5", RecA);
            AddEvent(GameStateEventType.MilestoneAchieved, 304.0, "RecordsDistance",
                GameStateRecorder.BuildMilestoneDetail(4800.0, 2.0f, 0.0), RecA);
            AddEvent(GameStateEventType.ScienceChanged, 305.0, GameStateEventConverter.StrategyInputReasonKey,
                "", RecB, 750.0, 641.25);
            AddEvent(GameStateEventType.BuildingDestroyed, 306.0,
                "SpaceCenter/LaunchPad/Facility/mainBuilding", "", RecA);
            AddEvent(GameStateEventType.ScienceLabCredited, 307.0, "ScienceTransmission", "vessel=Lab",
                RecB, 10.0, 22.25);
            AddEvent(GameStateEventType.TechResearched, 308.0, "survivability",
                GameStateRecorder.BuildFreeTechUnlockDetail(45f, new[] { "HeatShield1" }), RecA);
            Case(sb, "P04 every irreversible kind, ids as a list", new List<string> { RecA, RecB });

            Reset();
            RecordingStore.AddCommittedInternal(new Recording { RecordingId = Committed });
            AddEvent(GameStateEventType.ContractCompleted, 400.0, "guid-committed", "fundsReward=900", Committed);
            AddEvent(GameStateEventType.MilestoneAchieved, 401.0, "FirstLaunch",
                GameStateRecorder.BuildMilestoneDetail(500.0, 1.0f, 0.0), RecA);
            AddSubject("temperatureScan@KerbinSrfLanded", 2.25f, 402.0, Committed);
            AddConversionRow(403.0, 120f, Committed);
            Case(sb, "P05 a committed id among the discarded", new HashSet<string> { RecA, Committed });

            Reset();
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 500.0, RecA);
            AddSubject("evaReport@KerbinFlyingLow", 3.25f, 520.0, RecB);
            AddSubject("mysteryGoo@KerbinSrfLanded", 0f, 510.0, RecA);
            AddSubject("seismicScan@KerbinSrfLanded", 4.75f, 505.0, Other);
            AddSubject("barometerScan@KerbinSrfLanded", 1.25f, 515.0, "");
            Case(sb, "P06 science subjects only", new HashSet<string> { RecA, RecB });

            Reset();
            var dup = AddEvent(GameStateEventType.ContractCompleted, 600.0, "guid-dup", "fundsReward=700", RecA);
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.ContractComplete,
                ContractId = "guid-dup",
                UT = 600.0,
                CaptureSequence = dup.captureSequence,
            });
            AddEvent(GameStateEventType.MilestoneAchieved, 601.0, "FirstOrbit",
                GameStateRecorder.BuildMilestoneDetail(1200.0, 3.0f, 0.0), RecA);
            Case(sb, "P07 a ledger row the dedup drops", new HashSet<string> { RecA });

            Reset();
            AddEvent(GameStateEventType.ScienceLabCredited, 700.0, "ScienceTransmission", "vessel=Lab",
                RecA, 22.0, 22.0);
            AddEvent(GameStateEventType.ScienceChanged, 701.0, GameStateEventConverter.StrategyInputReasonKey,
                "", RecA, 90.0, 90.0);
            AddEvent(GameStateEventType.ContractCompleted, 702.0, "", "", RecA);
            Case(sb, "P08 irreversible events with no row", new HashSet<string> { RecA });

            Reset();
            AddEvent(GameStateEventType.ContractCompleted, 800.0, "guid-mix", "fundsReward=333", RecA);
            AddSubject("surfaceSample@MunSrfLanded", 6.25f, 805.0, RecA);
            AddConversionRow(806.0, 200f, RecA);
            AddEvent(GameStateEventType.MilestoneAchieved, 807.0, "MunLanding",
                GameStateRecorder.BuildMilestoneDetail(9000.0, 5.0f, 1.0), Other);
            Case(sb, "P09 events, science and a conversion row together", new HashSet<string> { RecA },
                "segment-discard");

            AssertGolden("PreserveIrreversibleLiveGameplayOnDiscard", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== P01 null ids",
            "event ContractCompleted guid-c tag=rec-A seq=1",
            "== P02 empty ids",
            "event ContractCompleted guid-c tag=rec-A seq=1",
            "== P03 nothing to re-home, one conversion row untagged",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 260",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tcseq = 6",
            "row[0] \t\tfundsSpent = 300",
            "row[0] \t\tfundsSpendingSource = 7",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 270",
            "row[1] \t\ttype = 3",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\trecordingId = rec-other",
            "row[1] \t\tcseq = 7",
            "row[1] \t\tfundsSpent = 400",
            "row[1] \t\tfundsSpendingSource = 7",
            "row[1] \t}",
            "row[1] }",
            "event ContractCompleted guid-other tag=rec-other seq=1",
            "event ScienceChanged ScienceTransmission tag=rec-A seq=2",
            "event ScienceChanged StrategyInput tag=rec-A seq=3",
            "event TechResearched basicRocketry tag=rec-A seq=4",
            "event ContractCompleted guid-untagged tag= seq=5",
            "pending crewReport@KerbinSrfLanded tag=rec-other",
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
            "log [Parsek][INFO][LedgerOrchestrator] ClearStrategyConversionTagsForDiscard (test-discard): untagged 1 strategy conversion row(s) of discarded recordings - rows kept, like the awards they were converted from",
            "log [Parsek][VERBOSE][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): nothing to re-home for 1 discarded id(s) (strategyConversionRowsUntagged=1)",
            "== P04 every irreversible kind, ids as a list",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 6",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tcontractId = guid-done",
            "row[0] \t\tfundsReward = 2080",
            "row[0] \t\trepReward = 1",
            "row[0] \t\tscienceReward = 1",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 302",
            "row[1] \t\ttype = 7",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = 3",
            "row[1] \t\tcontractId = guid-failed",
            "row[1] \t\tfundsPenalty = 500",
            "row[1] \t\trepPenalty = 10",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 303",
            "row[2] \t\ttype = 8",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 3",
            "row[2] \t\tcseq = 4",
            "row[2] \t\tcontractId = guid-cancelled",
            "row[2] \t\tfundsPenalty = 250",
            "row[2] \t\trepPenalty = 5",
            "row[2] \t}",
            "row[2] }",
            "row[3] ROOT",
            "row[3] {",
            "row[3] \tGAME_ACTION",
            "row[3] \t{",
            "row[3] \t\tut = 304",
            "row[3] \t\ttype = 4",
            "row[3] \t\tactionId = <masked>",
            "row[3] \t\tseq = 4",
            "row[3] \t\tcseq = 5",
            "row[3] \t\tmilestoneId = RecordsDistance",
            "row[3] \t\tmilestoneFundsAwarded = 4800",
            "row[3] \t\tmilestoneRepAwarded = 2",
            "row[3] \t\tmilestoneSciAwarded = 0",
            "row[3] \t}",
            "row[3] }",
            "row[4] ROOT",
            "row[4] {",
            "row[4] \tGAME_ACTION",
            "row[4] \t{",
            "row[4] \t\tut = 305",
            "row[4] \t\ttype = 32",
            "row[4] \t\tactionId = <masked>",
            "row[4] \t\tseq = 5",
            "row[4] \t\tcseq = 6",
            "row[4] \t\tcost = 108.75",
            "row[4] \t\tconversionSource = 0",
            "row[4] \t}",
            "row[4] }",
            "row[5] ROOT",
            "row[5] {",
            "row[5] \tGAME_ACTION",
            "row[5] \t{",
            "row[5] \t\tut = 306",
            "row[5] \t\ttype = 16",
            "row[5] \t\tactionId = <masked>",
            "row[5] \t\tseq = 6",
            "row[5] \t\tcseq = 7",
            "row[5] \t\tfacilityId = SpaceCenter/LaunchPad/Facility/mainBuilding",
            "row[5] \t}",
            "row[5] }",
            "row[6] ROOT",
            "row[6] {",
            "row[6] \tGAME_ACTION",
            "row[6] \t{",
            "row[6] \t\tut = 307",
            "row[6] \t\ttype = 38",
            "row[6] \t\tactionId = <masked>",
            "row[6] \t\tseq = 7",
            "row[6] \t\tcseq = 8",
            "row[6] \t\tscienceAwarded = 12.25",
            "row[6] \t\tmethod = 0",
            "row[6] \t}",
            "row[6] }",
            "row[7] ROOT",
            "row[7] {",
            "row[7] \tGAME_ACTION",
            "row[7] \t{",
            "row[7] \t\tut = 308",
            "row[7] \t\ttype = 1",
            "row[7] \t\tactionId = <masked>",
            "row[7] \t\tseq = 8",
            "row[7] \t\tcseq = 9",
            "row[7] \t\tnodeId = survivability",
            "row[7] \t\tcost = 0",
            "row[7] \t}",
            "row[7] }",
            "event ContractCompleted guid-done tag=rec-A seq=1",
            "event ContractParameterCompleted guid-done/param-1 tag=rec-A seq=2",
            "event ContractFailed guid-failed tag=rec-B seq=3",
            "event ContractCancelled guid-cancelled tag=rec-A seq=4",
            "event MilestoneAchieved RecordsDistance tag=rec-A seq=5",
            "event ScienceChanged StrategyInput tag=rec-B seq=6",
            "event BuildingDestroyed SpaceCenter/LaunchPad/Facility/mainBuilding tag=rec-A seq=7",
            "event ScienceLabCredited ScienceTransmission tag=rec-B seq=8",
            "event TechResearched survivability tag=rec-A seq=9",
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
            "log [Parsek][WARN][GameStateEventConverter] ConvertContractParameterCompleted: contract 'guid-done/param-1' has no parameter id - skipping",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=8, writer=StockApplied, total=8",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): re-homed 8 direct action(s) (contract=3, milestone=1, strategyExchange=1, building=1, lab=1, freeTech=1, science=0, deduped=0, subjectsRemoved=0, strategyConversionRowsUntagged=0) for 2 discarded id(s)",
            "== P05 a committed id among the discarded",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 403",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-committed",
            "row[0] \t\tcseq = 3",
            "row[0] \t\tfundsSpent = 120",
            "row[0] \t\tfundsSpendingSource = 7",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 401",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tmilestoneId = FirstLaunch",
            "row[1] \t\tmilestoneFundsAwarded = 500",
            "row[1] \t\tmilestoneRepAwarded = 1",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "event ContractCompleted guid-committed tag=rec-committed seq=1",
            "event MilestoneAchieved FirstLaunch tag=rec-A seq=2",
            "pending temperatureScan@KerbinSrfLanded tag=rec-committed",
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
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): re-homed 1 direct action(s) (contract=0, milestone=1, strategyExchange=0, building=0, lab=0, freeTech=0, science=0, deduped=0, subjectsRemoved=0, strategyConversionRowsUntagged=0) for 2 discarded id(s)",
            "== P06 science subjects only",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 500",
            "row[0] \t\ttype = 0",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tsubjectId = crewReport@KerbinSrfLanded",
            "row[0] \t\tscienceAwarded = 1.5",
            "row[0] \t\tmethod = 0",
            "row[0] \t\ttransmitScalar = 0",
            "row[0] \t\tsubjectMaxValue = 10",
            "row[0] \t\tstartUT = 500",
            "row[0] \t\tendUT = 521",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 520",
            "row[1] \t\ttype = 0",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 2",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tsubjectId = evaReport@KerbinFlyingLow",
            "row[1] \t\tscienceAwarded = 3.25",
            "row[1] \t\tmethod = 0",
            "row[1] \t\ttransmitScalar = 0",
            "row[1] \t\tsubjectMaxValue = 10",
            "row[1] \t\tstartUT = 520",
            "row[1] \t\tendUT = 521",
            "row[1] \t}",
            "row[1] }",
            "pending seismicScan@KerbinSrfLanded tag=rec-other",
            "pending barometerScan@KerbinSrfLanded tag=",
            "committedScience crewReport@KerbinSrfLanded=1.5",
            "committedScience evaReport@KerbinFlyingLow=3.25",
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
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceSubjects: skipping subject 'mysteryGoo@KerbinSrfLanded' \u2014 non-positive science (0)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=2 from 3 subjects, recordingId=(none), tagged=0, untaggedInWindow=2, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=2, endAnchored=0, afterWindowDated=0",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=2",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 2 added, 0 updated, skipped=0 (total=2)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (test-discard): removed 3 scoped pending subject(s), 2 unrelated retained",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): re-homed 2 direct action(s) (contract=0, milestone=0, strategyExchange=0, building=0, lab=0, freeTech=0, science=3, deduped=0, subjectsRemoved=3, strategyConversionRowsUntagged=0) for 2 discarded id(s)",
            "== P07 a ledger row the dedup drops",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 600",
            "row[0] \t\ttype = 6",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tcontractId = guid-dup",
            "row[0] \t\tfundsReward = 0",
            "row[0] \t\trepReward = 0",
            "row[0] \t\tscienceReward = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 601",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tmilestoneId = FirstOrbit",
            "row[1] \t\tmilestoneFundsAwarded = 1200",
            "row[1] \t\tmilestoneRepAwarded = 3",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "event ContractCompleted guid-dup tag=rec-A seq=1",
            "event MilestoneAchieved FirstOrbit tag=rec-A seq=2",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 2 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=1 legacyKeyMatches=0 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): re-homed 1 direct action(s) (contract=1, milestone=1, strategyExchange=0, building=0, lab=0, freeTech=0, science=0, deduped=1, subjectsRemoved=0, strategyConversionRowsUntagged=0) for 1 discarded id(s)",
            "== P08 irreversible events with no row",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 702",
            "row[0] \t\ttype = 6",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 3",
            "row[0] \t\tcontractId =",
            "row[0] \t\tfundsReward = 0",
            "row[0] \t\trepReward = 0",
            "row[0] \t\tscienceReward = 0",
            "row[0] \t}",
            "row[0] }",
            "event ScienceLabCredited ScienceTransmission tag=rec-A seq=1",
            "event ScienceChanged StrategyInput tag=rec-A seq=2",
            "event ContractCompleted  tag=rec-A seq=3",
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
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceLabCredited: non-positive credit=0 at UT=700.0 - skipping",
            "log [Parsek][WARN][GameStateEventConverter] ConvertContractCompleted: all rewards are 0 for contract '' \u2014 detail may lack reward fields",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (test-discard): re-homed 1 direct action(s) (contract=1, milestone=0, strategyExchange=0, building=0, lab=0, freeTech=0, science=0, deduped=0, subjectsRemoved=0, strategyConversionRowsUntagged=0) for 1 discarded id(s)",
            "== P09 events, science and a conversion row together",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 806",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tcseq = 2",
            "row[0] \t\tfundsSpent = 200",
            "row[0] \t\tfundsSpendingSource = 7",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 800",
            "row[1] \t\ttype = 6",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tcontractId = guid-mix",
            "row[1] \t\tfundsReward = 333",
            "row[1] \t\trepReward = 0",
            "row[1] \t\tscienceReward = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 805",
            "row[2] \t\ttype = 0",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 4",
            "row[2] \t\tsubjectId = surfaceSample@MunSrfLanded",
            "row[2] \t\tscienceAwarded = 6.25",
            "row[2] \t\tmethod = 0",
            "row[2] \t\ttransmitScalar = 0",
            "row[2] \t\tsubjectMaxValue = 10",
            "row[2] \t\tstartUT = 805",
            "row[2] \t\tendUT = 806",
            "row[2] \t}",
            "row[2] }",
            "event ContractCompleted guid-mix tag=rec-A seq=1",
            "event MilestoneAchieved MunLanding tag=rec-other seq=3",
            "committedScience surfaceSample@MunSrfLanded=6.25",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=(none), tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=0, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][INFO][LedgerOrchestrator] ClearStrategyConversionTagsForDiscard (segment-discard): untagged 1 strategy conversion row(s) of discarded recordings - rows kept, like the awards they were converted from",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=3",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (segment-discard): removed 1 scoped pending subject(s), 0 unrelated retained",
            "log [Parsek][INFO][LedgerOrchestrator] PreserveIrreversibleLiveGameplayOnDiscard (segment-discard): re-homed 2 direct action(s) (contract=1, milestone=0, strategyExchange=0, building=0, lab=0, freeTech=0, science=1, deduped=0, subjectsRemoved=1, strategyConversionRowsUntagged=1) for 1 discarded id(s)",
        }) + "\n";
    }
}
