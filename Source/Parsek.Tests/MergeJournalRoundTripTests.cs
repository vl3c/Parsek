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
                TreeId = "tree_1",
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
            Assert.Equal("tree_1", restored.TreeId);
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
            Assert.Null(restored.TreeId);
            Assert.Null(restored.StartedRealTime);
        }

        [Fact]
        public void MergeJournal_SaveInto_DoesNotPersistRecoveredSubtreeIds()
        {
            // Pass 2 review L7 / r7-design-doc invariant: RecoveredSubtreeIds is
            // a transient cross-block thread inside CompleteFromPostDurable —
            // it MUST NOT be in SaveInto. The field's XML doc explicitly
            // forbids persistence; this test locks the contract in code so a
            // future refactor that "helpfully" adds it to the codec
            // immediately surfaces.
            var journal = new MergeJournal
            {
                JournalId = "mj_transient",
                SessionId = "sess",
                Phase = MergeJournal.Phases.Tombstone,
                StartedUT = 200.0,
                RecoveredSubtreeIds = new System.Collections.Generic.List<string>
                {
                    "should_not_persist_1",
                    "should_not_persist_2",
                },
            };

            var parent = new ConfigNode("PARSEK");
            journal.SaveInto(parent);

            // Confirm the journal node carries the persistent fields…
            var node = parent.GetNode("MERGE_JOURNAL");
            Assert.NotNull(node);
            Assert.Equal("mj_transient", node.GetValue("journalId"));
            Assert.Equal(MergeJournal.Phases.Tombstone, node.GetValue("phase"));

            // …but nothing serialising the transient list.
            Assert.Null(node.GetValue("recoveredSubtreeIds"));
            Assert.Null(node.GetValue("recoveredSubtreeId"));
            Assert.False(node.HasNode("RECOVERED_SUBTREE_IDS"));
            // Whole-text scan as a belt-and-braces check against an unexpected
            // future serialisation form.
            string serialised = node.ToString();
            Assert.DoesNotContain("should_not_persist", serialised);
        }

        [Fact]
        public void MergeJournal_LoadFrom_LeavesRecoveredSubtreeIdsNull()
        {
            // Mirror of the SaveInto invariant on the LoadFrom side: nothing
            // in the on-disk format hydrates RecoveredSubtreeIds, so a
            // freshly-loaded journal must have it null. The orchestrator
            // relies on this for the "fresh-load resume entering at
            // Tombstone" path to fall through to RebuildSubtree.
            var node = new ConfigNode("MERGE_JOURNAL");
            node.AddValue("journalId", "mj_load");
            node.AddValue("sessionId", "sess");
            node.AddValue("phase", MergeJournal.Phases.Tombstone);
            node.AddValue("startedUT", "150.0");
            // Even if a malformed save sneaks the field in, the loader must
            // ignore it (LoadFrom doesn't read it; this catches a future
            // refactor that added a load path).
            node.AddValue("recoveredSubtreeIds", "rogue_id_1");

            var restored = MergeJournal.LoadFrom(node);

            Assert.Null(restored.RecoveredSubtreeIds);
        }
    }
}
