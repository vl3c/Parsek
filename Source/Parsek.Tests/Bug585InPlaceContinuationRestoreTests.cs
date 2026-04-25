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
    }
}
