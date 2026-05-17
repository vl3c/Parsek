using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 1 of fix-refly-relative-anchor: cover every branch of
    /// <see cref="ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor"/>
    /// including the supersede-chain cycle / depth-cap walker.
    ///
    /// Pinned to <c>[Collection("Sequential")]</c> because the tests pipe log
    /// output through <see cref="ParsekLog.TestSinkForTesting"/> which is
    /// shared static state.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyAnchorSelectionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReFlyAnchorSelectionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Rec(string id, string supersedeTargetId = null)
        {
            return new Recording
            {
                RecordingId = id,
                SupersedeTargetId = supersedeTargetId
            };
        }

        private static Func<string, Recording> Resolver(params Recording[] recordings)
        {
            var map = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (Recording r in recordings)
            {
                if (r != null && !string.IsNullOrEmpty(r.RecordingId))
                    map[r.RecordingId] = r;
            }
            return id => (id != null && map.TryGetValue(id, out Recording v)) ? v : null;
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_NoMarker_ReturnsFalse()
        {
            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker: null,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_MarkerActiveRecIdMismatch_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_other",
                SupersedeTargetId = "rec_target"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(Rec("rec_target")),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
            Assert.Contains(
                logLines,
                l => l.Contains("[Anchor]") && l.Contains("re-fly bypass skipped"));
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_SupersedeTargetPresent_ReturnsSupersedeTarget()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_target",
                OriginChildRecordingId = "rec_origin"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(Rec("rec_target")),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.True(ok);
            Assert.Equal("rec_target", anchor);
            Assert.Equal(ReFlyAnchorSelection.SourceSupersedeTarget, source);
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_SupersedeTargetNullOriginChildPresent_ReturnsOriginChild()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = null,
                OriginChildRecordingId = "rec_origin"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(Rec("rec_origin")),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.True(ok);
            Assert.Equal("rec_origin", anchor);
            Assert.Equal(ReFlyAnchorSelection.SourceOriginChild, source);
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_BothNull_ReturnsFalseWithWarn()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = null,
                OriginChildRecordingId = null
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
            Assert.Contains(
                logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]")
                    && l.Contains("re-fly anchor unavailable")
                    && l.Contains("rec_prov"));
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_SupersedeTargetUnknownRecording_ReturnsFalse()
        {
            // The resolver returns null for the looked-up id — i.e., the
            // supersede target is referenced but not loaded in the active
            // tree or committed list.
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_target_missing"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(), // empty resolver
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
            Assert.Contains(
                logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]")
                    && l.Contains("candidate recording not resolvable"));
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_NestedSupersedeCycle_DetectsAndWarns()
        {
            // Build a Recording-level supersede cycle: rec_A.SupersedeTargetId = rec_B,
            // rec_B.SupersedeTargetId = rec_A. The walker must detect and refuse.
            var recA = Rec("rec_A", supersedeTargetId: "rec_B");
            var recB = Rec("rec_B", supersedeTargetId: "rec_A");

            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_A"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(recA, recB),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
            Assert.Contains(
                logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]")
                    && l.Contains("cycle detected")
                    && l.Contains("bypass declined, falling back to nearest-search"));
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_DeepChainExceedsDepthCap_WarnsAndReturnsFalse()
        {
            // Build a chain of 9 hops: rec_0 -> rec_1 -> ... -> rec_8. The
            // cap is 8 so the 9th hop fires the cap.
            int totalHops = ReFlyAnchorSelection.CycleWalkDepthCap + 1;
            var chain = new Recording[totalHops + 1];
            for (int i = 0; i <= totalHops; i++)
            {
                string next = i < totalHops ? "rec_" + (i + 1) : null;
                chain[i] = Rec("rec_" + i, supersedeTargetId: next);
            }

            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_0"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(chain),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.False(ok);
            Assert.Null(anchor);
            Assert.Null(source);
            Assert.Contains(
                logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]")
                    && l.Contains("depth cap exceeded")
                    && l.Contains("bypass declined, falling back to nearest-search"));
        }

        [Fact]
        public void TryResolveReFlyProvisionalAnchor_DepthFourBreadcrumb_LogsInfoAtHopFour()
        {
            // Build a chain just deep enough to trigger the depth-4 breadcrumb
            // without hitting the depth-8 cap. The chain has 5 visible nodes
            // (rec_0 .. rec_4), enough for the walker to reach depth=4.
            var c0 = Rec("rec_0", "rec_1");
            var c1 = Rec("rec_1", "rec_2");
            var c2 = Rec("rec_2", "rec_3");
            var c3 = Rec("rec_3", "rec_4");
            var c4 = Rec("rec_4", supersedeTargetId: null);

            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_0"
            };

            bool ok = ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId: "rec_prov",
                resolveRecording: Resolver(c0, c1, c2, c3, c4),
                anchorRecordingId: out string anchor,
                source: out string source);

            Assert.True(ok);
            Assert.Equal("rec_0", anchor);
            Assert.Equal(ReFlyAnchorSelection.SourceSupersedeTarget, source);
            Assert.Contains(
                logLines,
                l => l.Contains("[Anchor]") && l.Contains("re-fly anchor walk: deep chain")
                    && l.Contains("depth=" + ReFlyAnchorSelection.CycleWalkDeepBreadcrumb));
        }
    }
}
