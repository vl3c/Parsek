using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 2 log-assertion tests for <see cref="RenderSessionState"/>
    /// (design doc §19.2 Stage 3 + Session State Lifecycle tables, §26.1
    /// HR-9 / HR-15). These tests verify both behaviour AND diagnostic
    /// coverage — if a future refactor accidentally drops a log line, the
    /// assertion fails and the developer must restore it.
    /// </summary>
    [Collection("Sequential")]
    public class RenderSessionStateLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RenderSessionStateLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            RenderSessionState.SurfaceLookupOverrideForTesting =
                (bodyName, lat, lon, alt) => new Vector3d(lat, lon, alt);
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- inline fixture helpers (mirror RenderSessionStateTests) ----

        private static Recording MakeRecording(
            string id, double bpUT, (double lat, double lon, double alt) atBp)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var bpPoint = new TrajectoryPoint
            {
                ut = bpUT,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var laterPoint = new TrajectoryPoint
            {
                ut = bpUT + 50,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            rec.Points.Add(bpPoint);
            rec.Points.Add(laterPoint);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0,
                endUT = bpUT + 100,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint> { bpPoint, laterPoint },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active
            });
            return rec;
        }

        private static (RecordingTree tree, BranchPoint bp) MakeTree(
            string treeId, double bpUT, params Recording[] children)
        {
            var tree = new RecordingTree { Id = treeId };
            var bp = new BranchPoint { Id = "bp-" + treeId, UT = bpUT, Type = BranchPointType.Undock };
            for (int i = 0; i < children.Length; i++)
            {
                tree.Recordings[children[i].RecordingId] = children[i];
                bp.ChildRecordingIds.Add(children[i].RecordingId);
            }
            tree.BranchPoints.Add(bp);
            return (tree, bp);
        }

        private static Func<string, RecordingTreeContext> Lookup(RecordingTree t, BranchPoint bp)
            => _ => new RecordingTreeContext(t, bp);

        // ------------------------------------------------------------------

        [Fact]
        public void RebuildFromMarker_LogsStartAndComplete_BookendPair()
        {
            // What makes it fail: a refactor that drops either L12 (start)
            // or L13 (complete) breaks the invariant the design doc
            // §19.2 Session State Lifecycle row pins. Without bookends a
            // log reader cannot tell whether a rebuild actually finished.
            var rLive = MakeRecording("live", 50, (0, 0, 70));
            var rSib  = MakeRecording("sib",  50, (1, 0, 70));
            var (tree, bp) = MakeTree("tBE", 50, rLive, rSib);

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sBE",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                Lookup(tree, bp),
                _ => Vector3d.zero);

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("RebuildFromMarker start")
                && l.Contains("sessionId=sBE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("RebuildFromMarker complete")
                && l.Contains("sessionId=sBE"));
        }

        [Fact]
        public void Anchor_EpsilonMagnitude_AlwaysLoggedAsMeters()
        {
            // What makes it fail: §19.2 Stage 3 row mandates epsilon
            // magnitude on every anchor Info line. Dropping it would leave
            // the operator unable to grep for "ε is 5 km" — exactly the
            // bubble-radius failure mode HR-9 surfaces.
            var rLive = MakeRecording("live", 50, (0, 0, 70));
            var rSib  = MakeRecording("sib",  50, (1, 2, 70));
            var (tree, bp) = MakeTree("tEM", 50, rLive, rSib);

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sEM",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                Lookup(tree, bp),
                _ => new Vector3d(10, 20, 30));

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("Anchor ε computed")
                && l.Contains("epsilonMagnitudeM="));
        }

        [Fact]
        public void LiveReadFrozen_LogsAnchorUTAndPosOnce()
        {
            // What makes it fail: HR-15 audit. If the live-anchor read line
            // (L18) appears twice — or once per sibling — that's a
            // production-side regression where the provider got called
            // every iteration. Single-read invariant lives or dies on this
            // assertion.
            var rLive = MakeRecording("live", 50, (0, 0, 70));
            var rA    = MakeRecording("a",    50, (1, 0, 70));
            var rB    = MakeRecording("b",    50, (0, 1, 70));
            var (tree, bp) = MakeTree("tLR", 50, rLive, rA, rB);

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sLR",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rA, rB },
                Lookup(tree, bp),
                _ => new Vector3d(7, 8, 9));

            int liveReadCount = 0;
            for (int i = 0; i < logLines.Count; i++)
            {
                if (logLines[i].Contains("[Pipeline-Anchor]") &&
                    logLines[i].Contains("Live anchor read") &&
                    logLines[i].Contains("anchorUT="))
                {
                    liveReadCount++;
                }
            }
            Assert.Equal(1, liveReadCount);
        }

        [Fact]
        public void EpsilonExceedsBubbleRadius_EmitsWarn()
        {
            // What makes it fail: HR-9 visible-failure rule. An ε of
            // > 2.5 km signals a frame mismatch or DAG bug. Silent acceptance
            // would let the renderer freeze the ghost in the wrong city.
            // The pipeline keeps the value (per §26.1 HR-9 commentary) but
            // must Warn-log so the operator notices.

            // Live and ghost both at (0,0,70) — but the surface lookup
            // returns a fixed sentinel for "live" and (lat*1000) for ghost,
            // so the recordedOffset is (1000,0,0) m × scale. With
            // liveAtSpawn far from the recorded live position, ε will blow
            // through the bubble radius.
            RenderSessionState.SurfaceLookupOverrideForTesting =
                (bodyName, lat, lon, alt) => new Vector3d(lat * 10000.0, lon * 10000.0, alt);

            var rLive = MakeRecording("live", 50, (0, 0, 70));
            var rSib  = MakeRecording("sib",  50, (1, 0, 70));
            var (tree, bp) = MakeTree("tBR", 50, rLive, rSib);

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sBR",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                Lookup(tree, bp),
                _ => new Vector3d(1_000_000, 0, 0));

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("exceeds bubble radius")
                && l.Contains("epsilonMagnitudeM="));
        }

        [Fact]
        public void Clear_LogsReason()
        {
            // What makes it fail: a Clear() that dropped the reason from
            // the log message would leave the operator unable to attribute
            // the cleared map to scene-exit vs marker-clear vs re-fly-end.
            RenderSessionState.Clear("re-fly-end");
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("Clear: reason=re-fly-end"));
        }

        [Fact]
        public void OrphanMarker_LogsWarn_NotInfo()
        {
            // What makes it fail: HR-9 silent-degrade regression guard.
            // An orphan marker MUST emit Warn (not Info), so log dashboards
            // catch it. Demoting the level would hide the failure beneath
            // the verbose-summary firehose.
            var rOrigin = MakeRecording("orig", 50, (0, 0, 70));

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sOR",
                    OriginChildRecordingId = rOrigin.RecordingId
                },
                new List<Recording> { rOrigin },
                _ => new RecordingTreeContext(null, null),
                _ => new Vector3d(1, 2, 3));

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Pipeline-Anchor]")
                && l.Contains("orphan-marker-no-parent-branchpoint"));
            // Make sure we did NOT log it as Info — that would mean HR-9
            // was downgraded.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][INFO][Pipeline-Anchor]")
                && l.Contains("orphan-marker-no-parent-branchpoint"));
        }
    }
}
