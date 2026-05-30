using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Focused tests for the scoped SpawnSuppressedByRewind lifecycle (#573/#589).
    /// </summary>
    [Collection("Sequential")]
    public class RewindSpawnSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindSpawnSuppressionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void MarkRewoundTreeRecordingsAsGhostOnly_LogsSourceProtectionAndFutureSkip()
        {
            const uint sourcePid = 2708531065u;
            const string treeId = "tree-589";
            const double rewindUT = 10.0;

            var source = MakeRecording(
                "source",
                "Kerbal X",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: 30.0);
            var futureSameTree = MakeRecording(
                "future-bob",
                "Bob Kerman",
                sourcePid,
                treeId,
                startUT: 24034.0,
                endUT: 24062.0);

            RecordingStore.RewindReplayTargetSourcePid = sourcePid;
            RecordingStore.RewindReplayTargetRecordingId = source.RecordingId;
            RewindContext.BeginRewind(rewindUT, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(
                new List<Recording> { source, futureSameTree });

            Assert.Equal(1, marked);
            Assert.True(source.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                source.SpawnSuppressedByRewindReason);
            Assert.False(futureSameTree.SpawnSuppressedByRewind);

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind applied") &&
                l.Contains("reason=same-recording") &&
                l.Contains("#573 active/source recording protection"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind not applied") &&
                l.Contains("reason=same-tree-future-recording") &&
                l.Contains("spawn allowed when endpoint is reached post-rewind"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind scope summary") &&
                l.Contains("futureAllowed=1"));
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_LegacyUnscopedMarker_IsClearedAndSpawnAllowed()
        {
            var futureSameTree = MakeRecording(
                "future-legacy",
                "Bob Kerman",
                736156658u,
                "tree-legacy",
                startUT: 24034.0,
                endUT: 24062.0);
            futureSameTree.SpawnSuppressedByRewind = true;

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                futureSameTree,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(result.needsSpawn);
            Assert.False(futureSameTree.SpawnSuppressedByRewind);
            Assert.Null(futureSameTree.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(futureSameTree.SpawnSuppressedByRewindUT));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind cleared") &&
                l.Contains("reason=legacy-unscoped"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Spawn allowed despite same-tree rewind") &&
                l.Contains("markerReason=legacy-unscoped"));
        }

        [Fact]
        public void RepeatedRewind_ClearsStaleSuppressionOnUnrelatedRecording()
        {
            const uint firstPid = 111u;
            const uint secondPid = 222u;

            var staleFirstTree = MakeRecording(
                "first-source",
                "First Source",
                firstPid,
                "first-tree",
                startUT: 0.0,
                endUT: 20.0);
            staleFirstTree.SpawnSuppressedByRewind = true;
            staleFirstTree.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            staleFirstTree.SpawnSuppressedByRewindUT = 0.0;

            var secondSource = MakeRecording(
                "second-source",
                "Second Source",
                secondPid,
                "second-tree",
                startUT: 100.0,
                endUT: 160.0);

            RecordingStore.RewindReplayTargetSourcePid = secondPid;
            RecordingStore.RewindReplayTargetRecordingId = secondSource.RecordingId;
            RewindContext.BeginRewind(120.0, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(
                new List<Recording> { staleFirstTree, secondSource });

            Assert.Equal(1, marked);
            Assert.False(staleFirstTree.SpawnSuppressedByRewind);
            Assert.True(secondSource.SpawnSuppressedByRewind);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind cleared") &&
                l.Contains("stale marker no longer matches active/source scope"));
        }

        [Fact]
        public void ResetAllPlaybackState_LogsSuppressionClearLifecycle()
        {
            var rec = MakeRecording(
                "reset-source",
                "Reset Source",
                333u,
                "reset-tree",
                startUT: 0.0,
                endUT: 30.0);
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 0.0;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Null(rec.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(rec.SpawnSuppressedByRewindUT));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind reset") &&
                l.Contains("reason=same-recording"));
        }

        [Fact]
        public void RecordingTree_SaveLoadRoundTrip_PreservesScopedSuppressionMetadata()
        {
            var source = MakeRecording(
                "persist-source",
                "Persist Source",
                444u,
                "persist-tree",
                startUT: 0.0,
                endUT: 30.0);
            source.SpawnSuppressedByRewind = true;
            source.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            source.SpawnSuppressedByRewindUT = 12.5;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.True(loaded.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                loaded.SpawnSuppressedByRewindReason);
            Assert.Equal(12.5, loaded.SpawnSuppressedByRewindUT);
        }

        [Fact]
        public void RecordingTree_LoadLegacyBoolOnlySuppression_TagsLegacyUnscoped()
        {
            var legacy = MakeRecording(
                "legacy-source",
                "Legacy Source",
                555u,
                "legacy-tree",
                startUT: 0.0,
                endUT: 30.0);
            legacy.SpawnSuppressedByRewind = true;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, legacy);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.True(loaded.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                loaded.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(loaded.SpawnSuppressedByRewindUT));
        }

        [Fact]
        public void RecordingTree_LoadStraySuppressionMetadataWithoutBool_IgnoresReasonAndUT()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("id", "stray-metadata");
            node.AddValue("spawnSuppressedByRewindReason",
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording);
            node.AddValue("spawnSuppressedByRewindUT", "12.5");

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.False(loaded.SpawnSuppressedByRewind);
            Assert.Null(loaded.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(loaded.SpawnSuppressedByRewindUT));
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_SameRecordingMarker_ClearsAndAllowsSpawn()
        {
            // Reproduces the user-facing bug behind this fix: a rewound recording
            // (terminal=Landed) carries the same-recording #573 marker, watch is
            // entered, and the spawn-at-recording-end path must now allow the
            // vessel to materialize when ghost playback reaches the terminal point.
            var rec = MakeRecording(
                "kerbal-x-rewound",
                "Kerbal X",
                pid: 2708531065u,
                treeId: "tree-watch-rewind",
                startUT: 92.5,
                endUT: 182.766);
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 92.5;

            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);

            Assert.True(cleared);
            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Null(rec.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(rec.SpawnSuppressedByRewindUT));

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind cleared") &&
                l.Contains("reason=same-recording") &&
                l.Contains("watch-entry: user engaged with rewound recording"));

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false);
            Assert.True(result.needsSpawn,
                $"After watch-entry clears the same-recording rewind marker, the " +
                $"Landed terminal recording must be spawn-eligible. Got reason='{result.reason}'.");
            Assert.Equal(string.Empty, result.reason);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_NoMarker_ReturnsFalse_NoLog()
        {
            var rec = MakeRecording(
                "fresh-rec",
                "Fresh",
                pid: 12345u,
                treeId: "fresh-tree",
                startUT: 0.0,
                endUT: 30.0);
            // No SpawnSuppressedByRewind set.

            int logCount = logLines.Count;
            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);

            Assert.False(cleared);
            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Equal(logCount, logLines.Count);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_LegacyUnscopedMarker_DoesNotClear()
        {
            // Legacy unscoped markers are normalized by ShouldBlockSpawnForRewindSuppression
            // on first spawn-decision access. Watch entry must not touch them — clearing
            // here would skip the normalization log path that audits relied on.
            var rec = MakeRecording(
                "legacy-future",
                "Legacy Future",
                pid: 736156658u,
                treeId: "legacy-tree",
                startUT: 100.0,
                endUT: 160.0);
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped;
            rec.SpawnSuppressedByRewindUT = 50.0;

            int logCount = logLines.Count;
            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);

            Assert.False(cleared);
            Assert.True(rec.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                rec.SpawnSuppressedByRewindReason);
            Assert.Equal(50.0, rec.SpawnSuppressedByRewindUT);
            Assert.Equal(logCount, logLines.Count);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_NullRecording_ReturnsFalse()
        {
            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(null);
            Assert.False(cleared);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_NullTerminal_ClearsAndAllowsSpawn()
        {
            // ShouldSpawnAtRecordingEnd accepts null TerminalStateValue when the
            // snapshot situation is spawnable (see ShouldSpawnAtRecordingEnd's
            // terminal-state and snapshot-situation gates). Gating the watch-entry
            // helper on an enum whitelist would leave the rewind+watch bug in place
            // for legacy / null-terminal recordings. Mirror the snapshot situation
            // KSP captures for a vessel on the pad: LANDED is spawnable.
            var rec = MakeRecording(
                "null-terminal-rewound",
                "Legacy Probe",
                pid: 4242u,
                treeId: "legacy-null-tree",
                startUT: 0.0,
                endUT: 60.0);
            rec.TerminalStateValue = null;
            rec.VesselSnapshot = new ConfigNode("VESSEL");
            rec.VesselSnapshot.AddValue("sit", "LANDED");
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 30.0;

            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);

            Assert.True(cleared);
            Assert.False(rec.SpawnSuppressedByRewind);

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false);
            Assert.True(result.needsSpawn,
                $"Null-terminal recording with spawnable snapshot must spawn after " +
                $"watch lifts the rewind marker. Got reason='{result.reason}'.");
        }

        [Theory]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        [InlineData(TerminalState.SubOrbital)]
        public void TryClearSpawnSuppressionOnWatchEntry_NonSpawnableTerminal_StillClears_SpawnGateRefuses(
            TerminalState terminal)
        {
            // The helper now mirrors ShouldSpawnAtRecordingEnd's contract: it lifts
            // the rewind-specific suppression unconditionally so the player's
            // explicit Watch is honored, and downstream gates make the final
            // spawnability decision. For any non-spawnable terminal listed in
            // ShouldSpawnAtRecordingEnd's terminal-state block (Destroyed,
            // Recovered, Docked, Boarded, SubOrbital), the marker clears but the
            // spawn gate refuses on the terminal-state check.
            var rec = MakeRecording(
                $"rec-{terminal}",
                $"Vessel-{terminal}",
                pid: 99u,
                treeId: $"tree-{terminal}",
                startUT: 0.0,
                endUT: 60.0);
            rec.TerminalStateValue = terminal;
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 30.0;

            bool cleared = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);

            Assert.True(cleared);
            Assert.False(rec.SpawnSuppressedByRewind);

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false);
            Assert.False(result.needsSpawn);
            Assert.Contains(terminal.ToString(), result.reason);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_WatchSwitch_OnlyMarkedRecordingClears()
        {
            // Pin recording-scoped semantics: the helper acts on the recording passed
            // in and nothing else. A watch-switch from a clean rec A to a marked rec
            // B must clear B's marker without touching A; if A were re-watched first
            // and B second, the per-call short-circuit on !SpawnSuppressedByRewind
            // means no spurious second clearance log either.
            var recA = MakeRecording(
                "watch-switch-A-clean",
                "A",
                pid: 1u,
                treeId: "tree-A",
                startUT: 0.0,
                endUT: 30.0);
            // recA has no SpawnSuppressedByRewind set.

            var recB = MakeRecording(
                "watch-switch-B-marked",
                "B",
                pid: 2u,
                treeId: "tree-B",
                startUT: 100.0,
                endUT: 160.0);
            recB.SpawnSuppressedByRewind = true;
            recB.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            recB.SpawnSuppressedByRewindUT = 120.0;

            // First entry (A): no-op.
            Assert.False(ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(recA));
            Assert.False(recA.SpawnSuppressedByRewind);
            // recB untouched while A is in scope.
            Assert.True(recB.SpawnSuppressedByRewind);

            // Switch to B: marker clears on B only.
            Assert.True(ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(recB));
            Assert.False(recB.SpawnSuppressedByRewind);
            // A remains unmarked (no spurious state propagation).
            Assert.False(recA.SpawnSuppressedByRewind);
        }

        [Fact]
        public void ShouldRetainMapPresenceForTerminalRealSpawn_SuppressedRewindMarker_NoMapPresenceRetained()
        {
            // Pin the symmetric refusal at ParsekPlaybackPolicy.cs:1130-1134: when
            // SpawnSuppressedByRewind is set, terminal-orbit map-presence retention is
            // also refused so the orbit line / icon does not survive past the marker's
            // scope. Without this, the suppressed source would render through the
            // tracking station while the spawn gate blocked materialization.
            var rec = MakeRecording(
                "orbital-rewound",
                "Kerbal X Probe",
                pid: 2823934496u,
                treeId: "tree-map-presence",
                startUT: 0.0,
                endUT: 992.23);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 500.0;

            bool retain = ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn(
                rec, hasFutureSegment: false);

            Assert.False(retain);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_DoesNotCopySuppressionFields_DeepClone_Does()
        {
            // Pin the load-bearing invariant behind the explicit field copy at
            // ParsekScenario.RestoreCommittedSidecarPayloadIntoActiveTreeRecording
            // (lines 4819-4821): ApplyPersistenceArtifactsFrom intentionally does NOT
            // copy SpawnSuppressedByRewind / Reason / UT, but DeepClone DOES. A
            // regression that "fixes" ApplyPersistenceArtifactsFrom to copy the
            // fields and removes the explicit copy will shift behavior at every
            // other ApplyPersistenceArtifactsFrom call site (chain commit etc).
            var source = MakeRecording(
                "source-with-marker",
                "Marked",
                pid: 1234u,
                treeId: "marked-tree",
                startUT: 0.0,
                endUT: 30.0);
            source.SpawnSuppressedByRewind = true;
            source.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            source.SpawnSuppressedByRewindUT = 15.5;

            var artifactTarget = new Recording();
            artifactTarget.ApplyPersistenceArtifactsFrom(source);
            Assert.False(artifactTarget.SpawnSuppressedByRewind);
            Assert.Null(artifactTarget.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(artifactTarget.SpawnSuppressedByRewindUT));

            var clone = Recording.DeepClone(source);
            Assert.True(clone.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                clone.SpawnSuppressedByRewindReason);
            Assert.Equal(15.5, clone.SpawnSuppressedByRewindUT);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_SecondEntryAfterClear_IsNoOp()
        {
            // First watch-entry clears the marker; toggle-off → toggle-on (or any second
            // EnterWatchMode for the same recording) should be a silent no-op with no
            // spurious clearance log.
            var rec = MakeRecording(
                "kerbal-x-toggle",
                "Kerbal X",
                pid: 2708531065u,
                treeId: "tree-toggle",
                startUT: 92.5,
                endUT: 182.766);
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 92.5;

            Assert.True(ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec));
            int logCount = logLines.Count;

            bool secondTry = ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(rec);
            Assert.False(secondTry);
            Assert.Equal(logCount, logLines.Count);
        }

        [Fact]
        public void TryClearSpawnSuppressionOnWatchEntry_FullSequence_MarkThenWatchThenSpawn()
        {
            // End-to-end: drive the production sequence through the helper. The user
            // rewinds (MarkRewoundTreeRecordingsAsGhostOnly applies same-recording),
            // then enters Watch (TryClearSpawnSuppressionOnWatchEntry clears it),
            // and the spawn-at-recording-end decision permits materialization.
            const uint sourcePid = 2708531065u;
            const string treeId = "tree-end-to-end";
            const double rewindUT = 92.5;

            var source = MakeRecording(
                "kerbal-x-source",
                "Kerbal X",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: 182.766);

            RecordingStore.RewindReplayTargetSourcePid = sourcePid;
            RecordingStore.RewindReplayTargetRecordingId = source.RecordingId;
            RewindContext.BeginRewind(rewindUT, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(
                new List<Recording> { source });
            Assert.Equal(1, marked);
            Assert.True(source.SpawnSuppressedByRewind);

            // Before watch entry: spawn must remain blocked.
            var blocked = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                source,
                isActiveChainMember: false,
                isChainLooping: false);
            Assert.False(blocked.needsSpawn);
            Assert.Contains("#573", blocked.reason);

            // Watch entry clears the same-recording marker.
            Assert.True(ParsekScenario.TryClearSpawnSuppressionOnWatchEntry(source));

            // After watch entry: spawn is allowed.
            var allowed = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                source,
                isActiveChainMember: false,
                isChainLooping: false);
            Assert.True(allowed.needsSpawn,
                $"Watched rewound recording must be spawn-eligible. " +
                $"Got reason='{allowed.reason}'.");
        }

        [Fact]
        public void ShouldApplyRewindSpawnSuppression_BoundaryOverlapUsesEpsilonOnly()
        {
            const uint sourcePid = 888u;
            const string treeId = "boundary-tree";
            const double rewindUT = 100.0;

            var endsExactlyAtRewind = MakeRecording(
                "ends-exactly",
                "Boundary Exact",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT);
            var endsWithinBoundaryEpsilon = MakeRecording(
                "ends-within-epsilon",
                "Boundary Within",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT - 0.0005);
            var endedBeforeBoundary = MakeRecording(
                "ended-before",
                "Boundary Before",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT - 0.01);

            Assert.True(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endsExactlyAtRewind,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string exactReason));
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording, exactReason);

            Assert.True(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endsWithinBoundaryEpsilon,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string withinReason));
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording, withinReason);

            Assert.False(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endedBeforeBoundary,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string beforeReason));
            Assert.Null(beforeReason);
        }

        // F3 / #829: a DIFFERENT launch of the same craft (same baked pid, different launch guid)
        // overlapping the rewind UT must NOT be spawn-suppressed; only the rewound launch is.
        [Fact]
        public void ShouldApplyRewindSpawnSuppression_DifferentLaunchSamePid_NotSuppressed()
        {
            const uint pid = 2708531065u;
            const double rewindUT = 100.0;
            const string rewoundGuid = "2b6e6a60d2c947489753371317fa067e";
            const string otherGuid = "a424011b746440baae6030e225c9de31";

            // A prior, unrelated launch of the same craft, in its own tree, overlapping the rewind UT.
            var otherLaunch = MakeRecording("other-launch", "Kerbal X", pid, "other-tree", 0.0, rewindUT + 50.0);
            otherLaunch.RecordedVesselGuid = otherGuid;

            Assert.False(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                otherLaunch,
                rewindRecordingId: "rewound-rec",
                rewindSourcePid: pid,
                rewoundTreeId: null,            // standalone-branch path (the most permissive)
                rewindUT: rewindUT,
                out string otherReason,
                rewindRecordingGuid: rewoundGuid));
            Assert.Null(otherReason);

            // The genuinely-rewound launch (matching guid) is still suppressed.
            var sameLaunch = MakeRecording("same-launch", "Kerbal X", pid, "rewound-tree", 0.0, rewindUT + 50.0);
            sameLaunch.RecordedVesselGuid = rewoundGuid;
            Assert.True(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                sameLaunch,
                rewindRecordingId: "rewound-rec",
                rewindSourcePid: pid,
                rewoundTreeId: null,
                rewindUT: rewindUT,
                out string sameReason,
                rewindRecordingGuid: rewoundGuid));
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording, sameReason);
        }

        private static Recording MakeRecording(
            string recordingId,
            string vesselName,
            uint pid,
            string treeId,
            double startUT,
            double endUT)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                VesselPersistentId = pid,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
            };
        }

        // ----------------------------------------------------------------
        // Canon (Immutable) Re-Fly fork preservation: end-to-end spawn-gate
        // assertion for fix-rewind-canon-forks. The predicate-classifier
        // upstream keeps the supersede relation across parent rewind; this
        // test confirms ShouldSpawnAtRecordingEnd reports needsSpawn=true
        // for the preserved canon fork, so its terminal-orbit
        // re-materialization actually fires when ghost playback completes.
        // ----------------------------------------------------------------

        [Fact]
        public void ShouldSpawnAtRecordingEnd_ReturnsTrue_ForPreservedCanonForkAfterParentRewind()
        {
            // Tree-less recording: ShouldSpawnAtRecordingEnd's
            // IsEffectiveLeafForVessel / IsNonLeafInTree helpers early-exit on
            // a null/empty TreeId before walking any tree. This is the
            // simplest valid fixture for this assertion; the full
            // tree-fixture path is exercised by other tests in this file
            // (e.g. ResetAllPlaybackState_LogsSuppressionClearLifecycle uses
            // AddRecordingWithTreeForTesting). Both shapes converge on the
            // same gate here because the tree helpers cannot reduce a leaf-
            // shaped recording's spawn eligibility.
            var canonFork = new Recording
            {
                RecordingId = "canon-orbital-probe",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 2823934496u,
                ExplicitStartUT = 456.79,
                ExplicitEndUT = 992.23,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Orbiting,
                MergeState = MergeState.Immutable,
                // Post-rewind state: VesselSpawned was reset by the rewind's
                // ResetAllPlaybackState (the live Re-Fly vessel was stripped),
                // so the spawn-at-recording-end path can fire fresh.
                VesselSpawned = false,
                SpawnedVesselPersistentId = 0,
                // Predicate-classifier preserved the supersede relation, so
                // SpawnSuppressedByRewind was never set on the canon fork.
                // (It only ever flagged the active/source recording per #589.)
                SpawnSuppressedByRewind = false,
                // Canon fork has no terminal-spawn supersession from a
                // downstream recording that's still live.
                TerminalSpawnSupersededByRecordingId = null,
            };

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                canonFork,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(result.needsSpawn,
                $"Canon Immutable orbital fork must be spawn-eligible after " +
                $"parent rewind so the persistent vessel re-materializes when " +
                $"ghost playback reaches EndUT. Got reason='{result.reason}'.");
            Assert.Equal(string.Empty, result.reason);
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_TreeContextLeaf_ReturnsTrue_ForPreservedCanonForkAfterParentRewind()
        {
            // Sibling of the test above, this time with a real tree context so
            // ShouldSpawnAtRecordingEnd's IsEffectiveLeafForVessel /
            // IsNonLeafInTree helpers actually walk RecordingStore.CommittedTrees
            // and apply the leaf-shaped logic instead of taking the no-tree
            // shortcut.
            var canonFork = new Recording
            {
                RecordingId = "canon-orbital-tree-probe",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 2823934496u,
                ExplicitStartUT = 456.79,
                ExplicitEndUT = 992.23,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Orbiting,
                MergeState = MergeState.Immutable,
                VesselSpawned = false,
                SpawnedVesselPersistentId = 0,
                SpawnSuppressedByRewind = false,
                TerminalSpawnSupersededByRecordingId = null,
                ChildBranchPointId = null,
            };
            // Single-recording tree: canonFork is automatically the effective
            // leaf for its vessel; IsNonLeafInTree's "parent of branch point"
            // path is also skipped (ChildBranchPointId is null).
            RecordingStore.AddRecordingWithTreeForTesting(
                canonFork, treeName: "canon-tree");

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                canonFork,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(result.needsSpawn,
                $"Canon Immutable orbital fork (with tree context) must be " +
                $"spawn-eligible after parent rewind. Got reason='{result.reason}'.");
            Assert.Equal(string.Empty, result.reason);
        }
    }
}
