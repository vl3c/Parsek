using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.FileRetiredReFlyProvisionalRows</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. Scenarios: an empty id, nothing tagged, tagged events only with a
    /// pre-existing reputation seed, tagged science only (one subject converts, one carries no
    /// science), events and science with a ledger row the dedup drops, a deferred seed the
    /// method captures off the live pool, a deferred seed with no live pool, a provisional
    /// id that is also a committed recording id, a tag differing from the id only in case, and
    /// a live-pool seed naming the id with rows before and after its capture. For each it records the returned count, every
    /// ledger row (action ids masked: they are fresh GUIDs), the store events, the pending
    /// science subjects, the committed science and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class FileRetiredReFlyProvisionalRowsCharacterizationTests : IDisposable
    {
        private const string Prov = "rec-prov";
        private const string Other = "rec-other";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public FileRetiredReFlyProvisionalRowsCharacterizationTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
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
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ReFlyAwardTagging.ResetForTesting();
            ReFlyProvisionalRetirement.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        private static void InstallPool(bool present, float reputation)
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => new LedgerOrchestrator.CurrencyPoolProbe
            {
                ReputationSingletonPresent = present,
                ReputationValue = reputation,
            };
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string>();
        }

        private static void AddEvent(GameStateEventType type, double ut, string key, string detail, string tag)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = tag,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddMilestone(string id, double ut, double funds, float rep, double sci, string tag) =>
            AddEvent(GameStateEventType.MilestoneAchieved, ut, id,
                GameStateRecorder.BuildMilestoneDetail(funds, rep, sci), tag);

        private static void AddSubject(string subjectId, float science, double captureUT, string tag,
            string captureActionId)
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
                captureActionId = captureActionId,
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*).*$");

        private void Case(StringBuilder sb, string label, string recordingId, ReputationSeedOrigin origin,
            string reason = "test-retire")
        {
            logLines.Clear();
            int result = LedgerOrchestrator.FileRetiredReFlyProvisionalRows(recordingId, origin, reason);
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
        public void FileRetiredReFlyProvisionalRows_Golden()
        {
            var sb = new StringBuilder();

            Reset();
            AddMilestone("FirstLaunch", 120.0, 500.0, 2f, 0.0, Prov);
            Case(sb, "R01 empty id", "", ReputationSeedOrigin.PreExisting);

            Reset();
            AddMilestone("FirstLaunch", 120.0, 500.0, 2f, 0.0, Other);
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 130.0, Other, null);
            Case(sb, "R02 nothing tagged to the id", Prov, ReputationSeedOrigin.PreExisting);

            Reset();
            Ledger.SeedInitialReputation(10f);
            AddMilestone("RecordsAltitude", 383.0, 9600.0, 3f, 1.0, Prov);
            AddEvent(GameStateEventType.TechResearched, 390.0, "basicRocketry", "cost=5", Prov);
            AddEvent(GameStateEventType.CrewHired, 395.0, "Jebediah Kerman", "trait=Pilot;cost=1000", Prov);
            AddMilestone("FirstLaunch", 100.0, 500.0, 2f, 0.0, Other);
            Case(sb, "R03 tagged events only, seed pre-existing", Prov, ReputationSeedOrigin.PreExisting,
                "test-prune");

            Reset();
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 400.0, Prov, "cap-0001");
            AddSubject("evaReport@KerbinSrfLanded", 0f, 410.0, Prov, "cap-0002");
            AddSubject("temperatureScan@KerbinSrfLanded", 2.25f, 405.0, Other, "cap-0003");
            AddSubject("barometerScan@KerbinSrfLanded", 0.75f, 402.0, null, "cap-0004");
            Case(sb, "R04 tagged science only", Prov, ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline);

            Reset();
            Ledger.SeedInitialReputation(10f);
            AddMilestone("RecordsSpeed", 450.0, 1200.0, 1f, 0.0, Prov);
            AddMilestone("RecordsDepth", 455.0, 800.0, 0f, 0.0, Prov);
            AddSubject("mysteryGoo@KerbinSrfLanded", 4f, 460.0, Prov, "cap-0010");
            AddSubject("mysteryGoo@KerbinFlyingLow", 3f, 470.0, Prov, null);
            // A row already filed for the first milestone, which the dedup drops.
            var preEvents = new List<GameStateEvent>();
            for (int i = 0; i < GameStateStore.Events.Count; i++)
            {
                var e = GameStateStore.Events[i];
                if (e.key == "RecordsSpeed")
                {
                    e.recordingId = "";
                    preEvents.Add(e);
                }
            }
            var pre = GameStateEventConverter.ConvertEvents(
                preEvents, null, double.NegativeInfinity, double.PositiveInfinity);
            foreach (var a in pre)
                Ledger.AddAction(a);
            // And a science row already filed under the first subject's capture id.
            var preScience = GameStateEventConverter.ConvertScienceSubjects(
                new List<PendingScienceSubject>
                {
                    new PendingScienceSubject
                    {
                        subjectId = "mysteryGoo@KerbinSrfLanded",
                        science = 4f,
                        subjectMaxValue = 10f,
                        captureUT = 460.0,
                        reasonKey = "ScienceTransmission",
                        recordingId = "",
                        scienceGainMultiplier = 1f,
                        captureActionId = "cap-0010",
                    },
                },
                null, 459.0, 461.0);
            foreach (var a in preScience)
                Ledger.AddAction(a);
            logLines.Clear();
            Case(sb, "R05 events and science, dedup drops the filed ones", Prov,
                ReputationSeedOrigin.PreExisting);

            Reset();
            InstallPool(present: true, reputation: 42.5f);
            AddMilestone("RecordsAltitude", 383.0, 9600.0, 3f, 0.0, Prov);
            AddSubject("crewReport@MunSrfLanded", 6f, 600.0, Prov, "cap-0020");
            Case(sb, "R06 seed not yet captured, captured off the live pool", Prov,
                ReputationSeedOrigin.NotYetCaptured);

            Reset();
            InstallPool(present: false, reputation: 0f);
            AddMilestone("RecordsAltitude", 383.0, 9600.0, 3f, 0.0, Prov);
            Case(sb, "R07 seed not yet captured, no live pool", Prov, ReputationSeedOrigin.NotYetCaptured);

            Reset();
            Ledger.SeedInitialReputation(10f);
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = Prov,
                VesselName = Prov,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
            });
            AddMilestone("RecordsDistance", 700.0, 300.0, 1f, 0.0, Prov);
            AddSubject("crewReport@KerbinSrfLanded", 2f, 710.0, Prov, "cap-0030");
            Case(sb, "R08 id is also a committed recording", Prov, ReputationSeedOrigin.PreExisting);

            Reset();
            Ledger.SeedInitialReputation(10f);
            AddMilestone("RecordsAltitude", 383.0, 9600.0, 3f, 0.0, "REC-PROV");
            AddMilestone("RecordsSpeed", 390.0, 1200.0, 1f, 0.0, Prov);
            AddSubject("crewReport@KerbinSrfLanded", 1.5f, 400.0, "REC-PROV", "cap-0040");
            AddSubject("evaReport@KerbinSrfLanded", 2.5f, 401.0, Prov, "cap-0041");
            Case(sb, "R09 a tag differing only in case is not the id", Prov, ReputationSeedOrigin.PreExisting);

            Reset();
            Ledger.SeedInitialReputation(40f, new List<string> { Prov }, 500.0);
            AddMilestone("RecordsAltitude", 383.0, 9600.0, 3f, 0.0, Prov);
            AddMilestone("RecordsSpeed", 600.0, 1200.0, 1f, 0.0, Prov);
            Case(sb, "R10 live-pool seed names the id, rows before and after its capture", Prov,
                ReputationSeedOrigin.PreExisting);

            AssertGolden("FileRetiredReFlyProvisionalRows", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== R01 empty id",
            "return=0",
            "event MilestoneAchieved FirstLaunch tag=rec-prov seq=1",
            "== R02 nothing tagged to the id",
            "return=0",
            "event MilestoneAchieved FirstLaunch tag=rec-other seq=1",
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
            "log [Parsek][VERBOSE][LedgerOrchestrator] Retired Re-Fly provisional 'rec-prov' (test-retire): no tagged events or pending science to file",
            "== R03 tagged events only, seed pre-existing",
            "return=3",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 10",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 383",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tmilestoneId = RecordsAltitude",
            "row[1] \t\tmilestoneFundsAwarded = 9600",
            "row[1] \t\tmilestoneRepAwarded = 3",
            "row[1] \t\tmilestoneSciAwarded = 1",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 390",
            "row[2] \t\ttype = 1",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 2",
            "row[2] \t\tnodeId = basicRocketry",
            "row[2] \t\tcost = 5",
            "row[2] \t}",
            "row[2] }",
            "row[3] ROOT",
            "row[3] {",
            "row[3] \tGAME_ACTION",
            "row[3] \t{",
            "row[3] \t\tut = 395",
            "row[3] \t\ttype = 12",
            "row[3] \t\tactionId = <masked>",
            "row[3] \t\tseq = 3",
            "row[3] \t\tcseq = 3",
            "row[3] \t\tkerbalName = Jebediah Kerman",
            "row[3] \t\tkerbalRole = Pilot",
            "row[3] \t\thireCost = 1000",
            "row[3] \t}",
            "row[3] }",
            "event MilestoneAchieved RecordsAltitude tag= seq=1",
            "event TechResearched basicRocketry tag= seq=2",
            "event CrewHired Jebediah Kerman tag= seq=3",
            "event MilestoneAchieved FirstLaunch tag=rec-other seq=4",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=3, skipped=0, outOfRange=0, total=3, recordingId=(none), skippedByType=(none)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=3, writer=StockApplied, total=4",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=3 total=4",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-prune rows=3 events=3 science=0 deduped=0 subjectsRemoved=0 eventsUntagged=3 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R04 tagged science only",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 400",
            "row[0] \t\ttype = 0",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tsubjectId = crewReport@KerbinSrfLanded",
            "row[0] \t\tscienceAwarded = 1.5",
            "row[0] \t\tmethod = 0",
            "row[0] \t\ttransmitScalar = 0",
            "row[0] \t\tsubjectMaxValue = 10",
            "row[0] \t\tstartUT = 400",
            "row[0] \t\tendUT = 411",
            "row[0] \t}",
            "row[0] }",
            "pending temperatureScan@KerbinSrfLanded tag=rec-other",
            "pending barometerScan@KerbinSrfLanded tag=<null>",
            "committedScience crewReport@KerbinSrfLanded=1.5",
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
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertEvents: empty or null events list, returning 0 actions",
            "log [Parsek][VERBOSE][GameStateEventConverter] ConvertScienceSubjects: skipping subject 'evaReport@KerbinSrfLanded' \u2014 non-positive science (0)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 2 subjects, recordingId=(none), tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=1, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][INFO][TakeBack] Take-back era starts at capture 1: rows captured from here on can have a duplicate stock payment taken back; older rows never are (design 3.15 step 5)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (test-retire): removed 2 scoped pending subject(s), 2 unrelated retained",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=0 total=0",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=1 events=0 science=2 deduped=0 subjectsRemoved=2 eventsUntagged=0 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R05 events and science, dedup drops the filed ones",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 10",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 450",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tmilestoneId = RecordsSpeed",
            "row[1] \t\tmilestoneFundsAwarded = 1200",
            "row[1] \t\tmilestoneRepAwarded = 1",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 460",
            "row[2] \t\ttype = 0",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 1",
            "row[2] \t\tcseq = 3",
            "row[2] \t\tsubjectId = mysteryGoo@KerbinSrfLanded",
            "row[2] \t\tscienceAwarded = 4",
            "row[2] \t\tmethod = 0",
            "row[2] \t\ttransmitScalar = 0",
            "row[2] \t\tsubjectMaxValue = 10",
            "row[2] \t\tstartUT = 460",
            "row[2] \t\tendUT = 461",
            "row[2] \t}",
            "row[2] }",
            "row[3] ROOT",
            "row[3] {",
            "row[3] \tGAME_ACTION",
            "row[3] \t{",
            "row[3] \t\tut = 455",
            "row[3] \t\ttype = 4",
            "row[3] \t\tactionId = <masked>",
            "row[3] \t\tseq = 1",
            "row[3] \t\tcseq = 2",
            "row[3] \t\tmilestoneId = RecordsDepth",
            "row[3] \t\tmilestoneFundsAwarded = 800",
            "row[3] \t\tmilestoneRepAwarded = 0",
            "row[3] \t\tmilestoneSciAwarded = 0",
            "row[3] \t}",
            "row[3] }",
            "row[4] ROOT",
            "row[4] {",
            "row[4] \tGAME_ACTION",
            "row[4] \t{",
            "row[4] \t\tut = 470",
            "row[4] \t\ttype = 0",
            "row[4] \t\tactionId = <masked>",
            "row[4] \t\tseq = 2",
            "row[4] \t\tcseq = 4",
            "row[4] \t\tsubjectId = mysteryGoo@KerbinFlyingLow",
            "row[4] \t\tscienceAwarded = 3",
            "row[4] \t\tmethod = 0",
            "row[4] \t\ttransmitScalar = 0",
            "row[4] \t\tsubjectMaxValue = 10",
            "row[4] \t\tstartUT = 470",
            "row[4] \t\tendUT = 471",
            "row[4] \t}",
            "row[4] }",
            "event MilestoneAchieved RecordsSpeed tag= seq=1",
            "event MilestoneAchieved RecordsDepth tag= seq=2",
            "committedScience mysteryGoo@KerbinFlyingLow=3",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=2, skipped=0, outOfRange=0, total=2, recordingId=(none), skippedByType=(none)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=2 from 2 subjects, recordingId=(none), tagged=0, untaggedInWindow=2, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=1, captureDated=2, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 2 duplicates from 4 candidates (scienceCaptureIdMatches=1 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=1 legacyKeyMatches=0 eventDuplicatesKept=0)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=5",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (test-retire): removed 2 scoped pending subject(s), 0 unrelated retained",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=2 total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=2 events=2 science=2 deduped=2 subjectsRemoved=2 eventsUntagged=2 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R06 seed not yet captured, captured off the live pool",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 42.5",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 383",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tinsideRepSeed = True",
            "row[1] \t\tmilestoneId = RecordsAltitude",
            "row[1] \t\tmilestoneFundsAwarded = 9600",
            "row[1] \t\tmilestoneRepAwarded = 3",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 600",
            "row[2] \t\ttype = 0",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 2",
            "row[2] \t\tsubjectId = crewReport@MunSrfLanded",
            "row[2] \t\tscienceAwarded = 6",
            "row[2] \t\tmethod = 0",
            "row[2] \t\ttransmitScalar = 0",
            "row[2] \t\tsubjectMaxValue = 10",
            "row[2] \t\tstartUT = 600",
            "row[2] \t\tendUT = 601",
            "row[2] \t}",
            "row[2] }",
            "event MilestoneAchieved RecordsAltitude tag= seq=1",
            "committedScience crewReport@MunSrfLanded=6",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=1, skipped=0, outOfRange=0, total=1, recordingId=(none), skippedByType=(none)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=(none), tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=1, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][INFO][Ledger] Seeded initial reputation: amount=42.5, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Reputation seed stamp: 1 row(s) stamped insideRepSeed=True (0 already stamped) for retired-refly-provisional=rec-prov - repSeedOrigin=CreatedThisCommitFromLivePool",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=3",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (test-retire): removed 1 scoped pending subject(s), 0 unrelated retained",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=1 total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=2 events=1 science=1 deduped=0 subjectsRemoved=1 eventsUntagged=1 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R07 seed not yet captured, no live pool",
            "return=1",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 383",
            "row[0] \t\ttype = 4",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tinsideRepSeed = True",
            "row[0] \t\tmilestoneId = RecordsAltitude",
            "row[0] \t\tmilestoneFundsAwarded = 9600",
            "row[0] \t\tmilestoneRepAwarded = 3",
            "row[0] \t\tmilestoneSciAwarded = 0",
            "row[0] \t}",
            "row[0] }",
            "event MilestoneAchieved RecordsAltitude tag= seq=1",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=1, skipped=0, outOfRange=0, total=1, recordingId=(none), skippedByType=(none)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] EnsureInitialReputationSeed: deferring the reputation seed - Reputation.Instance null",
            "log [Parsek][INFO][LedgerOrchestrator] Reputation seed stamp: 1 row(s) stamped insideRepSeed=True (0 already stamped) for retired-refly-provisional=rec-prov - repSeedOrigin=NotYetCaptured",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=1, writer=StockApplied, total=1",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=1 total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=1 events=1 science=0 deduped=0 subjectsRemoved=0 eventsUntagged=1 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R08 id is also a committed recording",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 10",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 700",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tmilestoneId = RecordsDistance",
            "row[1] \t\tmilestoneFundsAwarded = 300",
            "row[1] \t\tmilestoneRepAwarded = 1",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 710",
            "row[2] \t\ttype = 0",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 2",
            "row[2] \t\tsubjectId = crewReport@KerbinSrfLanded",
            "row[2] \t\tscienceAwarded = 2",
            "row[2] \t\tmethod = 0",
            "row[2] \t\ttransmitScalar = 0",
            "row[2] \t\tsubjectMaxValue = 10",
            "row[2] \t\tstartUT = 710",
            "row[2] \t\tendUT = 711",
            "row[2] \t}",
            "row[2] }",
            "event MilestoneAchieved RecordsDistance tag= seq=1",
            "pending crewReport@KerbinSrfLanded tag=rec-prov",
            "committedScience crewReport@KerbinSrfLanded=2",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=1, skipped=0, outOfRange=0, total=1, recordingId=(none), skippedByType=(none)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=(none), tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=1, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=3",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=1 total=1",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=2 events=1 science=1 deduped=0 subjectsRemoved=0 eventsUntagged=1 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R09 a tag differing only in case is not the id",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 10",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 390",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tmilestoneId = RecordsSpeed",
            "row[1] \t\tmilestoneFundsAwarded = 1200",
            "row[1] \t\tmilestoneRepAwarded = 1",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 401",
            "row[2] \t\ttype = 0",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 3",
            "row[2] \t\tsubjectId = evaReport@KerbinSrfLanded",
            "row[2] \t\tscienceAwarded = 2.5",
            "row[2] \t\tmethod = 0",
            "row[2] \t\ttransmitScalar = 0",
            "row[2] \t\tsubjectMaxValue = 10",
            "row[2] \t\tstartUT = 401",
            "row[2] \t\tendUT = 402",
            "row[2] \t}",
            "row[2] }",
            "event MilestoneAchieved RecordsAltitude tag=REC-PROV seq=1",
            "event MilestoneAchieved RecordsSpeed tag= seq=2",
            "pending crewReport@KerbinSrfLanded tag=REC-PROV",
            "committedScience evaReport@KerbinSrfLanded=2.5",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=1, skipped=0, outOfRange=0, total=1, recordingId=(none), skippedByType=(none)",
            "log [Parsek][INFO][GameStateEventConverter] ConvertScienceSubjects: converted=1 from 1 subjects, recordingId=(none), tagged=0, untaggedInWindow=1, skippedCrossRecording=0, skippedOutsideWindow=0, captureIdentified=1, captureDated=1, endAnchored=0, afterWindowDated=0",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=3",
            "log [Parsek][INFO][GameStateStore] CommitScienceActions: 1 added, 0 updated, skipped=0 (total=1)",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RemovePendingScienceSubjectsForRecordings (test-retire): removed 1 scoped pending subject(s), 1 unrelated retained",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=1 total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=2 events=1 science=1 deduped=0 subjectsRemoved=1 eventsUntagged=1 - the attempt was merged, so what it earned before its recorder bound stays",
            "== R10 live-pool seed names the id, rows before and after its capture",
            "return=2",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 0",
            "row[0] \t\ttype = 22",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\tinitialReputation = 40",
            "row[0] \t\tseedCaptureUT = 500",
            "row[0] \t\tseedPendingRec = rec-prov",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 383",
            "row[1] \t\ttype = 4",
            "row[1] \t\tactionId = <masked>",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 1",
            "row[1] \t\tinsideRepSeed = True",
            "row[1] \t\tmilestoneId = RecordsAltitude",
            "row[1] \t\tmilestoneFundsAwarded = 9600",
            "row[1] \t\tmilestoneRepAwarded = 3",
            "row[1] \t\tmilestoneSciAwarded = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 600",
            "row[2] \t\ttype = 4",
            "row[2] \t\tactionId = <masked>",
            "row[2] \t\tseq = 2",
            "row[2] \t\tcseq = 2",
            "row[2] \t\tmilestoneId = RecordsSpeed",
            "row[2] \t\tmilestoneFundsAwarded = 1200",
            "row[2] \t\tmilestoneRepAwarded = 1",
            "row[2] \t\tmilestoneSciAwarded = 0",
            "row[2] \t}",
            "row[2] }",
            "event MilestoneAchieved RecordsAltitude tag= seq=1",
            "event MilestoneAchieved RecordsSpeed tag= seq=2",
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
            "log [Parsek][INFO][GameStateEventConverter] ConvertEvents: converted=2, skipped=0, outOfRange=0, total=2, recordingId=(none), skippedByType=(none)",
            "log [Parsek][INFO][LedgerOrchestrator] Reputation seed stamp: 1 row(s) stamped insideRepSeed=True for retired-refly-provisional=rec-prov - recorded before the live-pool seed was read at UT 500 while uncommitted (1 later row(s) stay outside)",
            "log [Parsek][VERBOSE][Ledger] AddActions batch: added=2, writer=StockApplied, total=3",
            "log [Parsek][VERBOSE][GameStateStore] RetagEventsForRecording: 'rec-prov' -> '' retagged=2 total=2",
            "log [Parsek][INFO][LedgerOrchestrator] Re-Fly award handoff: outcome=filed-retired-provisional-rows provisional=rec-prov reason=test-retire rows=2 events=2 science=0 deduped=0 subjectsRemoved=0 eventsUntagged=2 - the attempt was merged, so what it earned before its recorder bound stays",
        }) + "\n";
    }
}
