using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #585: In-place continuation Re-Fly tree-restore carve-out.
    /// Pins the contract that <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>
    /// swaps the wait target to the marker's recording when the rewind
    /// quicksave's <c>ActiveRecordingId</c> still points at the just-stripped
    /// pre-rewind active vessel.
    /// </summary>
    [Collection("Sequential")]
    public class Bug585InPlaceContinuationRestoreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug585InPlaceContinuationRestoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // BuildDefaultVesselDecisions logs at Verbose; capture it.
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Func<string, (string vesselName, uint persistentId)?> MakeResolver(
            params (string id, string name, uint pid)[] entries)
        {
            var dict = new Dictionary<string, (string name, uint pid)>(StringComparer.Ordinal);
            foreach (var e in entries)
                dict[e.id] = (e.name, e.pid);
            return id =>
            {
                if (string.IsNullOrEmpty(id)) return null;
                if (!dict.TryGetValue(id, out var v)) return null;
                return (v.name, v.pid);
            };
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_NullMarker_ReturnsNoSwap()
        {
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                null, "tree-1", "rec-active",
                MakeResolver(("rec-active", "v", 100u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("no-marker", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_PlaceholderPattern_ReturnsNoSwap()
        {
            // origin != active means the placeholder pattern: the active
            // recording is a fresh provisional that doesn't live in the tree.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-fresh-provisional",
                OriginChildRecordingId = "rec-origin",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-origin",
                MakeResolver(("rec-origin", "Capsule", 100u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("placeholder-pattern", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_AlreadyPointingAtMarker_ReturnsNoSwap()
        {
            // The .sfs's ActiveRecordingId already matches the marker --
            // happens when the rewind quicksave was authored mid-Re-Fly.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-booster",
                MakeResolver(("rec-booster", "Booster", 200u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("already-pointing-at-marker", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_TreeIdMismatch_ReturnsNoSwap()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-OTHER",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-capsule",
                MakeResolver(("rec-booster", "Booster", 200u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("marker-tree-id-mismatch", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_MarkerRecordingMissingFromTree_ReturnsNoSwap()
        {
            // Marker pins rec-booster but the tree's resolver doesn't know about it.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-capsule",
                MakeResolver(("rec-capsule", "Capsule", 100u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("marker-recording-missing-from-tree", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_InPlaceMarker_SwapsTarget()
        {
            // The 2026-04-25 playtest case: rewind quicksave's tree's
            // ActiveRecordingId is the just-stripped capsule, but the marker
            // pins the booster recording for in-place continuation.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_test_585",
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-capsule",
                MakeResolver(
                    ("rec-capsule", "Kerbal X", 2708531065u),
                    ("rec-booster", "Kerbal X Probe", 3474243253u)));

            Assert.True(result.ShouldSwap);
            Assert.Equal("rec-booster", result.TargetRecordingId);
            Assert.Equal("Kerbal X Probe", result.TargetVesselName);
            Assert.Equal(3474243253u, result.TargetVesselPersistentId);
            Assert.Equal("in-place-continuation", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_MarkerFieldsEmpty_ReturnsNoSwap()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "",
                OriginChildRecordingId = "rec-origin",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-active",
                MakeResolver(("rec-active", "v", 100u)));

            Assert.False(result.ShouldSwap);
            Assert.Equal("marker-fields-empty", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_NullResolver_ReturnsNoSwap()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = "tree-1",
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-capsule", null);

            Assert.False(result.ShouldSwap);
            Assert.Equal("no-resolver", result.Reason);
        }

        [Fact]
        public void ResolveInPlaceContinuationTarget_EmptyMarkerTreeId_AllowsSwap()
        {
            // Defensive: legacy marker without TreeId still allows the swap if
            // the recording is in the tree and the in-place pattern holds.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
                TreeId = null, // legacy
            };
            var result = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "tree-1", "rec-capsule",
                MakeResolver(
                    ("rec-capsule", "Capsule", 100u),
                    ("rec-booster", "Booster", 200u)));

            Assert.True(result.ShouldSwap);
            Assert.Equal("rec-booster", result.TargetRecordingId);
        }

        // ================================================================
        // Post-Re-Fly merge dialog rendering: in-place continuation recording
        // with a recaptured snapshot at scene exit reads as
        // hasSnapshot=True / canPersist=True. Pins the playtest's
        // BuildDefaultVesselDecisions log shape that bug #585 captures
        // ("terminal=null hasSnapshot=False canPersist=False" pre-fix) and
        // asserts the post-fix shape after the recorder rebinds + scene-exit
        // re-snapshot run.
        // ================================================================

        // ================================================================
        // ParsekFlight.IsInPlaceContinuationArrivalForMarker
        // ================================================================

        [Fact]
        public void IsInPlaceContinuationArrival_NullMarker_ReturnsFalse()
        {
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "rec-booster", VesselPersistentId = 200u },
            };

            Assert.False(ParsekFlight.IsInPlaceContinuationArrivalForMarker(
                200u, marker: null, committedRecordings: committed));
        }

        [Fact]
        public void IsInPlaceContinuationArrival_PlaceholderPattern_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-fresh-provisional",
                OriginChildRecordingId = "rec-origin",
            };
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "rec-fresh-provisional", VesselPersistentId = 200u },
            };

            Assert.False(ParsekFlight.IsInPlaceContinuationArrivalForMarker(
                200u, marker, committed));
        }

        [Fact]
        public void IsInPlaceContinuationArrival_PidMatchesMarkerRecording_ReturnsTrue()
        {
            // The 2026-04-25 playtest case: booster pid 3474243253 just got
            // SetActiveVessel'd; marker pins booster recording.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
            };
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "rec-booster", VesselPersistentId = 3474243253u },
                new Recording { RecordingId = "rec-capsule", VesselPersistentId = 2708531065u },
            };

            Assert.True(ParsekFlight.IsInPlaceContinuationArrivalForMarker(
                3474243253u, marker, committed));
        }

        [Fact]
        public void IsInPlaceContinuationArrival_PidDoesNotMatch_ReturnsFalse()
        {
            // A different vessel was SetActive (e.g., a placeholder pattern
            // where the placeholder shares no pid with origin). Suppression
            // should not fire so the existing arm path runs.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
            };
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "rec-booster", VesselPersistentId = 3474243253u },
            };

            Assert.False(ParsekFlight.IsInPlaceContinuationArrivalForMarker(
                999999u, marker, committed));
        }

        [Fact]
        public void IsInPlaceContinuationArrival_ZeroNewPid_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec-booster",
                OriginChildRecordingId = "rec-booster",
            };
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "rec-booster", VesselPersistentId = 0u },
            };

            Assert.False(ParsekFlight.IsInPlaceContinuationArrivalForMarker(
                0u, marker, committed));
        }

        [Fact]
        public void MergeDialog_InPlaceContinuationActiveLeaf_RendersCanPersistTrue()
        {
            // After the marker-aware restore + 7-minute booster flight + scene
            // exit, the in-place continuation recording (e.g. 01384be4) has:
            //   - TerminalStateValue = Orbiting (carried from original mission
            //     and refreshed by FinalizeIndividualRecording's #289 path)
            //   - VesselSnapshot non-null (captured by
            //     StashActiveTreeAsPendingLimbo's null-snapshot loop OR by
            //     FinalizeTreeRecordings' isSceneExit=true re-snapshot)
            // BuildDefaultVesselDecisions must read this as canPersist=True.
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Kerbal X",
                RootRecordingId = "rec-capsule",
                ActiveRecordingId = "rec-booster",
            };
            var capsule = new Recording
            {
                RecordingId = "rec-capsule",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL"),
            };
            var booster = new Recording
            {
                RecordingId = "rec-booster",
                TreeId = "tree-1",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3474243253u,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
            };
            tree.AddOrReplaceRecording(capsule);
            tree.AddOrReplaceRecording(booster);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.True(decisions.ContainsKey("rec-booster"));
            Assert.True(decisions["rec-booster"],
                "in-place continuation recording with snapshot + Orbiting terminal must render canPersist=True");

            // Pin the structured log shape so future regressions trip the assert.
            // The dialog emits one line per leaf with the canonical fields used
            // in the playtest log.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") &&
                l.Contains("rec-booster") &&
                l.Contains("hasSnapshot=True") &&
                l.Contains("canPersist=True"));
        }

        // -------------------------------------------------------------
        // PR #558 P2 review follow-up: after the marker swap mutates
        // tree.ActiveRecordingId, the tree's BackgroundMap must exclude
        // the new active recording. RebuildBackgroundMap re-runs
        // IsBackgroundMapEligible against the swapped value -- without a
        // post-swap rebuild, EnsureBackgroundRecorderAttached would seed
        // the BackgroundRecorder from a map that lists the live recording
        // as both active and background.
        // -------------------------------------------------------------

        [Fact]
        public void RebuildBackgroundMap_AfterSwapping_ActiveRecordingId_ExcludesSwappedTarget()
        {
            // Pre-rewind: capsule active, booster background. After in-place
            // continuation swap: booster active, capsule should be the
            // background entry (or skipped if its terminal disqualifies it).
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Kerbal X" };
            var capsule = new Recording
            {
                RecordingId = "rec-capsule",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                TerminalStateValue = null, // still active in tree
            };
            var booster = new Recording
            {
                RecordingId = "rec-booster",
                TreeId = "tree-1",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3474243253u,
                TerminalStateValue = null, // still active in tree
            };
            tree.AddOrReplaceRecording(capsule);
            tree.AddOrReplaceRecording(booster);

            // Pre-swap: capsule is the active recording, booster is in the
            // background map.
            tree.ActiveRecordingId = "rec-capsule";
            tree.RebuildBackgroundMap();
            Assert.True(tree.BackgroundMap.ContainsKey(3474243253u),
                "before swap: booster pid expected in BackgroundMap (it's a non-active recording)");
            Assert.False(tree.BackgroundMap.ContainsKey(2708531065u),
                "before swap: capsule pid is ActiveRecordingId, must NOT be in BackgroundMap");

            // Swap to in-place continuation target (the booster).
            tree.ActiveRecordingId = "rec-booster";
            tree.RebuildBackgroundMap();

            // Post-swap: booster is active, must be excluded from
            // BackgroundMap. Capsule is now eligible as a background entry.
            Assert.False(tree.BackgroundMap.ContainsKey(3474243253u),
                "after swap: booster pid is the new ActiveRecordingId, must NOT be in BackgroundMap");
            Assert.True(tree.BackgroundMap.ContainsKey(2708531065u),
                "after swap: capsule is non-active and tracked, must appear in BackgroundMap");
        }

        [Fact]
        public void RebuildBackgroundMap_DestroyedRecording_NotInBackgroundMap()
        {
            // IsBackgroundMapEligible additionally excludes Destroyed
            // recordings. Confirms the swap-then-rebuild behaviour
            // composes with the existing eligibility filter (i.e.,
            // swapping the active to a Destroyed-recording id would
            // drop nothing else into the map).
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Kerbal X" };
            var capsule = new Recording
            {
                RecordingId = "rec-capsule",
                TreeId = "tree-1",
                VesselName = "Kerbal X",
                VesselPersistentId = 2708531065u,
                TerminalStateValue = TerminalState.Destroyed,
            };
            var booster = new Recording
            {
                RecordingId = "rec-booster",
                TreeId = "tree-1",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 3474243253u,
                TerminalStateValue = null,
            };
            tree.AddOrReplaceRecording(capsule);
            tree.AddOrReplaceRecording(booster);

            tree.ActiveRecordingId = "rec-booster";
            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(2708531065u),
                "Destroyed recording must not appear in BackgroundMap");
            Assert.False(tree.BackgroundMap.ContainsKey(3474243253u),
                "ActiveRecordingId must not appear in BackgroundMap");
        }

        // ============================================================
        // Issue #734 fork-attach reconciliation
        // (ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded +
        //  RewindInvoker.EnsureForkAttachedToTree)
        // ============================================================

        /// <summary>
        /// Async-load race fix: when AtomicMarkerWrite ran on a tree handle
        /// that the restore coroutine has since popped, the fork only lives
        /// in <see cref="RecordingStore.CommittedRecordings"/>. The
        /// reconciliation helper looks the fork up by id and attaches it to
        /// the live tree so <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>
        /// can fire and the recorder's flush has a destination dict entry.
        /// </summary>
        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_ForkMissingFromTree_AttachesFromCommittedList()
        {
            try
            {
                RecordingStore.SuppressLogging = true;
                RecordingStore.ResetForTesting();

                const string treeId = "tree-734-reconcile-attach";
                const string originId = "rec-734-origin";
                const string forkId = "rec-734-fork";

                var origin = new Recording
                {
                    RecordingId = originId,
                    TreeId = treeId,
                    VesselName = "Origin",
                    VesselPersistentId = 100u,
                    MergeState = MergeState.Immutable,
                };
                var tree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = "Tree",
                    RootRecordingId = originId,
                    ActiveRecordingId = originId,
                };
                tree.AddOrReplaceRecording(origin);

                var fork = new Recording
                {
                    RecordingId = forkId,
                    TreeId = treeId,
                    VesselName = "Origin",
                    VesselPersistentId = 100u,
                    MergeState = MergeState.NotCommitted,
                    SupersedeTargetId = originId,
                };
                RecordingStore.AddCommittedInternal(fork);

                var marker = new ReFlySessionMarker
                {
                    SessionId = "sess-734-reconcile",
                    TreeId = treeId,
                    ActiveReFlyRecordingId = forkId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    InPlaceContinuation = true,
                };

                bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

                Assert.True(attached);
                Assert.True(tree.Recordings.ContainsKey(forkId));
                Assert.Same(fork, tree.Recordings[forkId]);
                Assert.Contains(logLines, l =>
                    l.Contains("[Rewind]")
                    && l.Contains("RestoreActiveTreeFromPending:reconcile")
                    && l.Contains("attached in-place fork rec=" + forkId));
            }
            finally
            {
                RecordingStore.ResetForTesting();
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_ForkAlreadyAttached_NoOp()
        {
            try
            {
                RecordingStore.SuppressLogging = true;
                RecordingStore.ResetForTesting();

                const string treeId = "tree-734-noop";
                const string originId = "rec-734-origin-noop";
                const string forkId = "rec-734-fork-noop";

                var origin = new Recording
                {
                    RecordingId = originId, TreeId = treeId, VesselPersistentId = 200u,
                };
                var fork = new Recording
                {
                    RecordingId = forkId, TreeId = treeId, VesselPersistentId = 200u,
                    SupersedeTargetId = originId,
                };
                var tree = new RecordingTree
                {
                    Id = treeId, RootRecordingId = originId, ActiveRecordingId = forkId,
                };
                tree.AddOrReplaceRecording(origin);
                // Eager attach already done by AtomicMarkerWrite.
                tree.AddOrReplaceRecording(fork);
                RecordingStore.AddCommittedInternal(fork);

                var marker = new ReFlySessionMarker
                {
                    SessionId = "sess-noop",
                    TreeId = treeId,
                    ActiveReFlyRecordingId = forkId,
                    OriginChildRecordingId = originId,
                    InPlaceContinuation = true,
                };

                bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

                Assert.False(attached);
                Assert.True(tree.Recordings.ContainsKey(forkId));
                Assert.DoesNotContain(logLines, l => l.Contains("attached in-place fork"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_LegacyInPlaceShape_NoOp()
        {
            // Legacy in-place sessions wrote ActiveReFlyRecordingId == OriginChildRecordingId
            // and origin is already in tree.Recordings by construction.
            const string treeId = "tree-legacy";
            const string originId = "rec-legacy-origin";
            var origin = new Recording
            {
                RecordingId = originId, TreeId = treeId, VesselPersistentId = 300u,
            };
            var tree = new RecordingTree
            {
                Id = treeId, RootRecordingId = originId, ActiveRecordingId = originId,
            };
            tree.AddOrReplaceRecording(origin);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-legacy",
                TreeId = treeId,
                ActiveReFlyRecordingId = originId,
                OriginChildRecordingId = originId,
                InPlaceContinuation = false, // legacy path predates the flag
            };

            bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

            Assert.False(attached);
        }

        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_NotInPlaceMarker_NoOp()
        {
            const string treeId = "tree-non-inplace";
            var tree = new RecordingTree
            {
                Id = treeId, RootRecordingId = "rec-root", ActiveRecordingId = "rec-root",
            };
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = treeId,
                ActiveReFlyRecordingId = "rec-placeholder",
                OriginChildRecordingId = "rec-origin",
                InPlaceContinuation = false,
            };

            bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

            Assert.False(attached);
        }

        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_ForkMissingFromCommittedList_LogsWarn()
        {
            try
            {
                RecordingStore.SuppressLogging = true;
                RecordingStore.ResetForTesting();

                const string treeId = "tree-missing-fork";
                const string originId = "rec-origin-missing";
                const string forkId = "rec-fork-missing";

                var origin = new Recording
                {
                    RecordingId = originId, TreeId = treeId, VesselPersistentId = 400u,
                };
                var tree = new RecordingTree
                {
                    Id = treeId, RootRecordingId = originId, ActiveRecordingId = originId,
                };
                tree.AddOrReplaceRecording(origin);

                var marker = new ReFlySessionMarker
                {
                    SessionId = "sess-missing",
                    TreeId = treeId,
                    ActiveReFlyRecordingId = forkId,
                    OriginChildRecordingId = originId,
                    InPlaceContinuation = true,
                };

                bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

                Assert.False(attached);
                Assert.False(tree.Recordings.ContainsKey(forkId));
                Assert.Contains(logLines, l =>
                    l.Contains("[Flight]")
                    && l.Contains("ReconcileInPlaceForkIntoTreeIfNeeded")
                    && l.Contains("not in committed list"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void ReconcileInPlaceForkIntoTreeIfNeeded_TreeIdMismatch_NoOp()
        {
            const string forkId = "rec-fork-mismatch";
            var tree = new RecordingTree
            {
                Id = "tree-loaded", RootRecordingId = "rec-other", ActiveRecordingId = "rec-other",
            };
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                TreeId = "tree-other",
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = "rec-origin",
                InPlaceContinuation = true,
            };

            bool attached = ParsekFlight.ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

            Assert.False(attached);
        }

        /// <summary>
        /// Issue #734: <see cref="RewindInvoker.EnsureForkAttachedToTree"/>
        /// rebuilds the background map after attach so a stale entry left
        /// over from before the swap (a non-active sibling pid that the
        /// rebuild now classifies as ineligible) does not survive into the
        /// active session. We pin the rebuild by seeding a stale pid into
        /// the map for a recording that <see cref="IsBackgroundMapEligible"/>
        /// would not include and asserting the rebuild evicted it.
        /// </summary>
        [Fact]
        public void EnsureForkAttachedToTree_RebuildsBackgroundMap()
        {
            const string treeId = "tree-bg-rebuild";
            const string originId = "rec-bg-origin";
            const string forkId = "rec-bg-fork";
            const uint stalePid = 9001u;

            var origin = new Recording
            {
                RecordingId = originId, TreeId = treeId, VesselPersistentId = stalePid,
                MergeState = MergeState.Immutable,
            };
            var fork = new Recording
            {
                RecordingId = forkId, TreeId = treeId, VesselPersistentId = stalePid,
                MergeState = MergeState.NotCommitted,
                SupersedeTargetId = originId,
            };
            var tree = new RecordingTree
            {
                Id = treeId, RootRecordingId = originId, ActiveRecordingId = forkId,
            };
            tree.AddOrReplaceRecording(origin);
            // Seed a stale background map entry whose recording id is no
            // longer a tree member -- a clean rebuild must drop it.
            tree.BackgroundMap[12345u] = "rec-no-longer-in-tree";

            bool attached = RewindInvoker.EnsureForkAttachedToTree(tree, fork, "TestCallSite");

            Assert.True(attached);
            Assert.True(tree.Recordings.ContainsKey(forkId));
            Assert.False(tree.BackgroundMap.ContainsKey(12345u),
                "RebuildBackgroundMap must drop entries pointing at recordings " +
                "that are no longer in tree.Recordings");
        }

        [Fact]
        public void EnsureForkAttachedToTree_AlreadyAttached_NoOp_NoLog()
        {
            try
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = false;
                var localLogs = new List<string>();
                ParsekLog.TestSinkForTesting = line => localLogs.Add(line);

                const string treeId = "tree-noop-attach";
                const string forkId = "rec-noop-attach";

                var fork = new Recording
                {
                    RecordingId = forkId, TreeId = treeId, VesselPersistentId = 8000u,
                };
                var tree = new RecordingTree
                {
                    Id = treeId, RootRecordingId = forkId, ActiveRecordingId = forkId,
                };
                tree.AddOrReplaceRecording(fork);

                bool attached = RewindInvoker.EnsureForkAttachedToTree(tree, fork, "TestCallSite");

                Assert.False(attached);
                Assert.DoesNotContain(localLogs, l => l.Contains("attached in-place fork"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = true;
            }
        }
    }
}
