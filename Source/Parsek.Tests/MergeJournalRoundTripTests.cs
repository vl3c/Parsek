using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against MergeJournal save/load drift (design doc section 5.8).
    /// </summary>
    public class MergeJournalRoundTripTests
    {
        [Fact]
        public void MergeJournal_BeginPhase_RoundTrips()
        {
            var journal = new MergeJournal
            {
                JournalId = "mj_1",
                SessionId = "rf_c3d4",
                Phase = MergeJournal.Phases.Begin,
                StartedUT = 1742800.0,
                StartedRealTime = "2026-04-18T12:00:00Z"
            };

            var parent = new ConfigNode("PARSEK");
            journal.SaveInto(parent);
            var node = parent.GetNode("MERGE_JOURNAL");
            Assert.NotNull(node);

            var restored = MergeJournal.LoadFrom(node);
            Assert.Equal("mj_1", restored.JournalId);
            Assert.Equal("rf_c3d4", restored.SessionId);
            Assert.Equal(MergeJournal.Phases.Begin, restored.Phase);
            Assert.Equal(1742800.0, restored.StartedUT);
            Assert.Equal("2026-04-18T12:00:00Z", restored.StartedRealTime);
        }

        [Fact]
        public void MergeJournal_MissingPhase_DefaultsToBegin()
        {
            // A malformed save with an empty phase must still yield a usable journal
            // so the finisher can clean it up. The loader treats empty-string as Begin.
            var node = new ConfigNode("MERGE_JOURNAL");
            node.AddValue("journalId", "mj_empty");
            node.AddValue("sessionId", "sess");
            node.AddValue("phase", "");

            var restored = MergeJournal.LoadFrom(node);
            Assert.Equal(MergeJournal.Phases.Begin, restored.Phase);
        }

        [Fact]
        public void MergeJournal_BeginConstantValue_MatchesDesignDoc()
        {
            // The constant string 'Begin' is referenced by the Phase 10 staged-commit
            // writer AND the Phase 13 load-time finisher. If either side drifts, a
            // crash between step 8 and step 14 will leave unrecoverable state on disk.
            Assert.Equal("Begin", MergeJournal.Phases.Begin);
        }

        [Fact]
        public void MergeJournal_MissingStartedRealTime_LoadsAsNull()
        {
            // A minimally-populated journal (no wall-clock) must still round-trip —
            // the design only treats StartedUT as mandatory.
            var journal = new MergeJournal
            {
                JournalId = "mj_minimal",
                SessionId = "sess",
                Phase = MergeJournal.Phases.Begin,
                StartedUT = 100.0
            };

            var parent = new ConfigNode("PARSEK");
            journal.SaveInto(parent);
            var restored = MergeJournal.LoadFrom(parent.GetNode("MERGE_JOURNAL"));

            Assert.Equal(100.0, restored.StartedUT);
            Assert.Null(restored.StartedRealTime);
        }
    }
}
