using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the parent-anchor cascade in
    /// <see cref="EffectiveState.ComputeRewindRetiredRecordingIds(IReadOnlyList{Recording}, IReadOnlyList{RecordingRewindRetirement})"/>.
    ///
    /// <para>
    /// Bug evidence: playtest save
    /// <c>logs/2026-05-19_2329_pr909-narrowed-gate-playtest/saves/x4/persistent.sfs</c>.
    /// Retirement <c>rrt_33919eadcd674138baef970cb3e7b5b7</c> retires
    /// <c>rec_2c68978d</c>. Recording <c>3d4713df</c> has
    /// <c>debrisParentRecordingId = rec_2c68978d</c> but no retirement of its
    /// own; pre-fix it rendered as an orphan debris ghost alongside the
    /// restored recording's own debris children.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class OrphanDebrisOnRetirementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public OrphanDebrisOnRetirementTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.IsVerboseEnabled;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static Recording Rec(
            string id,
            string debrisParentRecordingId = null,
            MergeState state = MergeState.CommittedProvisional)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                DebrisParentRecordingId = debrisParentRecordingId,
            };
        }

        private static RecordingRewindRetirement Retire(string recordingId, string restoredId = null)
        {
            return new RecordingRewindRetirement
            {
                RetirementId = "rrt_" + recordingId,
                RecordingId = recordingId,
                RestoredRecordingId = restoredId,
                Reason = RecordingRewindRetirement.DefaultReason,
            };
        }

        // =====================================================================
        // ComputeRewindRetiredRecordingIds cascade overload
        // =====================================================================

        [Fact]
        public void Cascade_RetiredParent_HidesParentAnchoredChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent", "rec_restored") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.Contains("rec_child", retired);
        }

        [Fact]
        public void Cascade_MultipleChildren_HidesAllParentAnchoredChildren()
        {
            var parent = Rec("rec_parent");
            var c1 = Rec("rec_c1", debrisParentRecordingId: "rec_parent");
            var c2 = Rec("rec_c2", debrisParentRecordingId: "rec_parent");
            var c3 = Rec("rec_c3", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, c1, c2, c3 };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Equal(4, retired.Count);
            Assert.Contains("rec_c1", retired);
            Assert.Contains("rec_c2", retired);
            Assert.Contains("rec_c3", retired);
        }

        [Fact]
        public void Cascade_TransitiveChain_HidesGrandchildren()
        {
            // rec_parent -> rec_child -> rec_grandchild.
            // grandchild's DebrisParentRecordingId points at child, not parent;
            // fixed-point closure adds child first, then grandchild.
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var grandchild = Rec("rec_grandchild", debrisParentRecordingId: "rec_child");
            var recordings = new List<Recording> { parent, child, grandchild };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.Contains("rec_child", retired);
            Assert.Contains("rec_grandchild", retired);
        }

        [Fact]
        public void Cascade_UnrelatedRecording_StaysVisible()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, child, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.DoesNotContain("rec_unrelated", retired);
        }

        [Fact]
        public void Cascade_ChildOfNonRetiredParent_StaysVisible()
        {
            var retiredParent = Rec("rec_retired");
            var liveParent = Rec("rec_live");
            var liveChild = Rec("rec_liveChild", debrisParentRecordingId: "rec_live");
            var recordings = new List<Recording> { retiredParent, liveParent, liveChild };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_retired") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_retired", retired);
            Assert.DoesNotContain("rec_live", retired);
            Assert.DoesNotContain("rec_liveChild", retired);
        }

        [Fact]
        public void Cascade_NoRetirements_ReturnsEmpty()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(
                recordings, new List<RecordingRewindRetirement>());

            Assert.Empty(retired);
        }

        [Fact]
        public void Cascade_ParentNotRetiredButChildHasStaleDebrisParentId_StaysVisible()
        {
            // Negative test for the DebrisParentRecordingId lookup landing on
            // a non-retired recording.
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_unrelated");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, child, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.DoesNotContain("rec_child", retired);
            Assert.DoesNotContain("rec_unrelated", retired);
        }

        [Fact]
        public void Cascade_LogsVerboseSummaryWhenChildrenAdded()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains(logLines, l =>
                l.Contains("[ERS]")
                && l.Contains("Rewind-retirement cascade")
                && l.Contains("cascadeAdded=1"));
        }

        [Fact]
        public void Cascade_NoChildrenAdded_DoesNotLog()
        {
            // Retired parent with no parent-anchored children: no cascade log
            // line, so quiet steady-state ERS rebuilds do not gain new noise.
            var parent = Rec("rec_parent");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
        }

        // =====================================================================
        // ComputeTimelineInactiveRecordingIds
        // =====================================================================

        [Fact]
        public void ComputeTimelineInactiveRecordingIds_RetiredParentCascade_MarksChildRewindRetired()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var inactive = EffectiveState.ComputeTimelineInactiveRecordingIds(
                recordings,
                new List<RecordingSupersedeRelation>(),
                retirements);

            Assert.Equal(TimelineInactiveReason.RewindRetired, inactive["rec_parent"]);
            Assert.Equal(TimelineInactiveReason.RewindRetired, inactive["rec_child"]);
        }

        // =====================================================================
        // IsRewindRetired cascade overload
        // =====================================================================

        [Fact]
        public void IsRewindRetired_Cascade_ReturnsTrueForParentAnchoredChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            Assert.True(EffectiveState.IsRewindRetired(parent, recordings, retirements));
            Assert.True(EffectiveState.IsRewindRetired(child, recordings, retirements));
        }

        [Fact]
        public void IsRewindRetired_RawOverload_ReturnsFalseForParentAnchoredChild()
        {
            // Raw overload (no recordings list) keeps its per-row contract so
            // EnsureRewindRetirementsForRollback's "seenIds" working set still
            // dedupes only direct rows being written.
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            Assert.False(EffectiveState.IsRewindRetired(child, retirements));
        }

        // =====================================================================
        // End-to-end through ComputeERS
        // =====================================================================

        [Fact]
        public void ComputeERS_RetiredParentCascade_OmitsOrphanDebrisChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var unrelated = Rec("rec_unrelated");

            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.AddCommittedInternal(child);
            RecordingStore.AddCommittedInternal(unrelated);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    Retire("rec_parent"),
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var ers = EffectiveState.ComputeERS();
            var visibleIds = ers.Select(r => r.RecordingId).ToList();

            Assert.DoesNotContain("rec_parent", visibleIds);
            Assert.DoesNotContain("rec_child", visibleIds);
            Assert.Contains("rec_unrelated", visibleIds);

            // ERS rebuild summary records the cascade-driven skip.
            Assert.Contains(logLines, l =>
                l.Contains("[ERS]") && l.Contains("skippedRewindRetired=2"));
        }

        // =====================================================================
        // Playtest-shape regression
        // =====================================================================

        [Fact]
        public void Cascade_PlaytestShape_HidesOrphanKerbalXDebrisChild()
        {
            // Mirrors the persistent.sfs shape from the 2026-05-19 playtest:
            // - rec_2c68978d retired via rrt_33919ead (rewound-out-supersede-fork).
            // - 3d4713df has debrisParentRecordingId = rec_2c68978d, no retirement.
            // - ab1f54b0 (the restored recording) and its children stay visible.
            const string retiredFork = "rec_2c68978d84054474b804c579c92f5d40";
            const string orphanDebris = "3d4713df2ba449d99455de98db3085f4";
            const string restored = "ab1f54b089f54312b02add0aa049e156";
            const string restoredChild = "rec_0e69db2e1ea4428c913c9ad1d8da82d4";

            var recordings = new List<Recording>
            {
                Rec(retiredFork),
                Rec(orphanDebris, debrisParentRecordingId: retiredFork),
                Rec(restored),
                Rec(restoredChild, debrisParentRecordingId: restored),
            };
            var retirements = new List<RecordingRewindRetirement>
            {
                Retire(retiredFork, restored),
            };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains(retiredFork, retired);
            Assert.Contains(orphanDebris, retired);
            Assert.DoesNotContain(restored, retired);
            Assert.DoesNotContain(restoredChild, retired);
        }

        // =====================================================================
        // Reversibility: removing the parent retirement reinstates the child.
        // Existing housekeeping paths (orphan cleanup, tree-discard purge,
        // legacy-Immutable load-time sweep) already remove retirement rows;
        // pin that no extra child-side cleanup is required.
        // =====================================================================

        // =====================================================================
        // Cache: live-store calls cache the cascade across version-stable
        // windows so per-frame consumers (ParsekKSC.Update per-rec,
        // RecordingsTableUI per-row, GhostMapPresence) do not pay the
        // fixed-point closure cost N times per frame AND do not re-emit the
        // Verbose cascade log on every call.
        // =====================================================================

        [Fact]
        public void LiveStoreCall_RepeatsCacheCascade_LogsOnceUntilVersionBump()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.AddCommittedInternal(child);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    Retire("rec_parent"),
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            // First call: cache miss -> compute path runs -> Verbose log fires.
            int logCountBefore = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            var first = EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            int logCountAfterFirst = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountBefore + 1, logCountAfterFirst);
            Assert.Contains("rec_child", first);

            // Repeat calls with the same versions: cache hit -> identical
            // HashSet reference returned, no new log lines.
            for (int i = 0; i < 5; i++)
            {
                var hit = EffectiveState.ComputeRewindRetiredRecordingIds(
                    RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
                Assert.Same(first, hit);
            }
            int logCountAfterRepeats = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountAfterFirst, logCountAfterRepeats);

            // Version bump invalidates: cascade recomputes and re-logs.
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            int logCountAfterBump = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountAfterRepeats + 1, logCountAfterBump);
        }

        [Fact]
        public void AdHocCall_DoesNotPollLiveCache()
        {
            // Ad-hoc test-fixture call with private lists must not stash a
            // cascade into the live cache; otherwise a later live-store call
            // would hit a stale entry derived from the wrong recordings.
            var parent = Rec("rec_parent");
            RecordingStore.AddCommittedInternal(parent);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var adHocRecordings = new List<Recording>
            {
                Rec("rec_adhoc_parent"),
                Rec("rec_adhoc_child", debrisParentRecordingId: "rec_adhoc_parent"),
            };
            var adHocRetirements = new List<RecordingRewindRetirement>
            {
                Retire("rec_adhoc_parent"),
            };

            var adHocResult = EffectiveState.ComputeRewindRetiredRecordingIds(
                adHocRecordings, adHocRetirements);
            Assert.Contains("rec_adhoc_parent", adHocResult);
            Assert.Contains("rec_adhoc_child", adHocResult);

            // Live call must not see the ad-hoc result through the cache.
            var liveResult = EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            Assert.DoesNotContain("rec_adhoc_parent", liveResult);
            Assert.DoesNotContain("rec_adhoc_child", liveResult);
            Assert.Empty(liveResult);
        }

        [Fact]
        public void Reversibility_RemovingRetirement_ReinstatesChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", debrisParentRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retiredBefore = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            Assert.Contains("rec_child", retiredBefore);

            retirements.Clear();
            var retiredAfter = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            Assert.Empty(retiredAfter);
        }
    }
}
