using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 2 tests for <see cref="RenderSessionState"/>.RebuildFromMarker
    /// (design doc §6.3 / §7.1 / §18 Phase 2 / HR-15). The tests use the
    /// injectable test overload — they pass an in-memory recordings list, a
    /// tree-lookup closure, and a frozen-once live-position provider — so
    /// nothing here needs a live KSP scene, FlightGlobals, or CelestialBody
    /// instances. A fixed surface-lookup override (test-only seam on
    /// <see cref="RenderSessionState"/>) returns body-frame world positions
    /// for the synthetic <c>"Kerbin"</c> samples.
    /// <para>
    /// Touches static state (<see cref="RenderSessionState"/> map +
    /// <see cref="ParsekLog.TestSinkForTesting"/>) — runs in
    /// <c>Sequential</c>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RenderSessionStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RenderSessionStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            // Default surface lookup — tests that need a different geometry
            // override this in-line.
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

        // ----- fixture helpers ---------------------------------------------

        private static Recording MakeRecording(
            string id, string body,
            double startUT, double endUT,
            double bpUT,
            (double lat, double lon, double alt) atBp)
        {
            // One ABSOLUTE TrackSection covering [startUT, endUT], with one
            // boundary point at exactly bpUT and a second point a few seconds
            // later so List<TrajectoryPoint>.Count > 0 and the section's UT
            // range is non-degenerate.
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "test-" + id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var bpPoint = new TrajectoryPoint
            {
                ut = bpUT,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var laterPoint = new TrajectoryPoint
            {
                ut = endUT,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            rec.Points.Add(bpPoint);
            rec.Points.Add(laterPoint);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = startUT,
                endUT = endUT,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint> { bpPoint, laterPoint },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active
            });
            return rec;
        }

        private static Recording MakeRecordingRelative(
            string id, string body, double startUT, double endUT, double bpUT,
            (double lat, double lon, double alt) atBp)
        {
            // Same shape but with a RELATIVE-frame section so the Phase 2
            // RELATIVE guard fires.
            var rec = MakeRecording(id, body, startUT, endUT, bpUT, atBp);
            var s = rec.TrackSections[0];
            s.referenceFrame = ReferenceFrame.Relative;
            s.anchorVesselId = 100u;
            rec.TrackSections[0] = s;
            return rec;
        }

        private static (RecordingTree tree, BranchPoint bp) MakeTreeWithSplit(
            string treeId, double bpUT, params Recording[] children)
        {
            var tree = new RecordingTree { Id = treeId };
            var bp = new BranchPoint
            {
                Id = "bp-" + treeId,
                UT = bpUT,
                Type = BranchPointType.Undock
            };
            for (int i = 0; i < children.Length; i++)
            {
                tree.Recordings[children[i].RecordingId] = children[i];
                bp.ChildRecordingIds.Add(children[i].RecordingId);
            }
            tree.BranchPoints.Add(bp);
            return (tree, bp);
        }

        private static Func<string, RecordingTreeContext> TreeLookupFor(
            RecordingTree tree, BranchPoint bp)
        {
            return _ => new RecordingTreeContext(tree, bp);
        }

        // ----- 1. marker-null guard ----------------------------------------

        [Fact]
        public void RebuildFromMarker_NullMarker_NoAnchors_LogsInfo()
        {
            // What makes it fail: if the null-marker path silently kept a
            // prior session's anchor map (instead of clearing it), a
            // post-merge non-session render would still apply ε's from the
            // last re-fly — every ghost would freeze at its old corrected
            // position even after the session ended.
            RenderSessionState.RebuildFromMarker(
                marker: null,
                recordings: new List<Recording>(),
                treeLookup: _ => default,
                liveWorldPositionProvider: _ => null);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Null(RenderSessionState.CurrentSessionId);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("RebuildFromMarker start")
                && l.Contains("sessionId=<null>"));
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("Clear: reason=marker-null"));
        }

        // ----- 2. orphan marker (no parent BranchPoint) --------------------

        [Fact]
        public void RebuildFromMarker_OrphanMarker_LogsWarn_NoAnchors()
        {
            // What makes it fail: HR-9 forbids silent fall-through. An orphan
            // marker (parent BP missing) must Warn-log so the operator sees
            // the degraded state in KSP.log; otherwise the bug class
            // "ghosts misaligned because the tree was healed mid-session"
            // would be undebuggable.
            var rOrigin = MakeRecording("orig", "Kerbin", 0, 100, 50, (0, 0, 70));
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-orphan",
                OriginChildRecordingId = rOrigin.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                recordings: new List<Recording> { rOrigin },
                treeLookup: _ => new RecordingTreeContext(null, null),
                liveWorldPositionProvider: _ => new Vector3d(1, 1, 1));

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("orphan-marker-no-parent-branchpoint"));
        }

        // ----- 3. live vessel destroyed ------------------------------------

        [Fact]
        public void RebuildFromMarker_DestroyedLiveVessel_NoAnchors_LogsWarn()
        {
            // What makes it fail: HR-15 says read live state EXACTLY ONCE and
            // freeze. If the read fails, the pipeline must not silently
            // substitute zero — that would render every ghost at its raw
            // recorded position and quietly mask the destroyed-vessel bug.
            // The visible-failure rule is that we Warn AND clear the map.
            var rOrigin = MakeRecording("orig", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rSib = MakeRecording("sib", "Kerbin", 0, 100, 50, (0.001, 0, 70));
            var (tree, bp) = MakeTreeWithSplit("t", 50.0, rOrigin, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-destroyed",
                TreeId = tree.Id,
                OriginChildRecordingId = rOrigin.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                recordings: new List<Recording> { rOrigin, rSib },
                treeLookup: TreeLookupFor(tree, bp),
                liveWorldPositionProvider: _ => null);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("live-vessel-missing"));
        }

        // ----- 4. single sibling: ε ≈ recordedOffset -----------------------

        [Fact]
        public void RebuildFromMarker_SingleSibling_OneAnchor_EpsilonMatchesOffset()
        {
            // What makes it fail: the central Phase 2 invariant. If
            // recordedOffset is wrongly subtracted instead of added (sign
            // bug), or if P_smoothed_world is reused as the live anchor
            // (variable swap), ε would be off by the recorded separation
            // and ghosts would spawn 2x the offset away from the live
            // vessel — exactly the regression Phase 2 is shipping to fix.

            // Pick a surface lookup that returns lat/lon/alt as a Vector3d
            // (the Sequential-collection ctor sets this up). Live and ghost
            // both at altitude 70; live at (0,0), ghost at (1,2).
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib  = MakeRecording("sib-rec",  "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));

            // Track exactly one provider call (HR-15 audit at the type level).
            int liveProviderCalls = 0;
            Vector3d liveAtSpawn = new Vector3d(100.0, 200.0, 300.0);
            Func<string, Vector3d?> liveProvider = id =>
            {
                liveProviderCalls++;
                Assert.Equal(rLive.RecordingId, id);
                return liveAtSpawn;
            };

            var (tree, bp) = MakeTreeWithSplit("t1", 50.0, rLive, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = tree.Id,
                OriginChildRecordingId = rLive.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                liveProvider);

            Assert.Equal(1, RenderSessionState.Count);
            Assert.Equal(1, liveProviderCalls);
            Assert.Equal(marker.SessionId, RenderSessionState.CurrentSessionId);

            Assert.True(RenderSessionState.TryLookup(
                rSib.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));

            // recordedOffset = ghost - live, in surface-lookup space
            //   = (1,2,70) - (0,0,70) = (1,2,0)
            // target  = liveAtSpawn + recordedOffset = (101, 202, 300)
            // P_smoothed_world (no spline) = ghost_abs_world = (1,2,70)
            // ε = target - P_smoothed_world = (100, 200, 230)
            Assert.Equal(100.0, ac.Epsilon.x, 6);
            Assert.Equal(200.0, ac.Epsilon.y, 6);
            Assert.Equal(230.0, ac.Epsilon.z, 6);
            Assert.Equal(AnchorSource.LiveSeparation, ac.Source);
            Assert.Equal(AnchorSide.Start, ac.Side);
            Assert.Equal(0, ac.SectionIndex);
            Assert.Equal(50.0, ac.UT);
        }

        // ----- 5. two siblings: two anchors --------------------------------

        [Fact]
        public void RebuildFromMarker_TwoSiblings_TwoAnchors()
        {
            // What makes it fail: a per-sibling iteration bug (e.g. break
            // after the first sibling, or storing the second sibling's
            // anchor under the first's key) would silently produce one
            // anchor or one wrong anchor.
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rA    = MakeRecording("a-rec",    "Kerbin", 0, 100, 50, (1.0, 0.0, 70.0));
            var rB    = MakeRecording("b-rec",    "Kerbin", 0, 100, 50, (0.0, 1.0, 70.0));
            var (tree, bp) = MakeTreeWithSplit("t2", 50.0, rLive, rA, rB);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-2",
                TreeId = tree.Id,
                OriginChildRecordingId = rLive.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                new List<Recording> { rLive, rA, rB },
                TreeLookupFor(tree, bp),
                _ => new Vector3d(0, 0, 0));

            Assert.Equal(2, RenderSessionState.Count);
            Assert.True(RenderSessionState.TryLookup(rA.RecordingId, 0, AnchorSide.Start, out _));
            Assert.True(RenderSessionState.TryLookup(rB.RecordingId, 0, AnchorSide.Start, out _));
        }

        // ----- 6. idempotence (HR-3 / HR-4) --------------------------------

        [Fact]
        public void RebuildFromMarker_SameMarkerTwice_Idempotent()
        {
            // What makes it fail: HR-3 / HR-4 — same inputs → same outputs.
            // A non-deterministic iteration (e.g. Dictionary enumeration
            // order escaping) or a stale-accumulator bug would diverge.
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib  = MakeRecording("sib-rec",  "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));
            var (tree, bp) = MakeTreeWithSplit("t3", 50.0, rLive, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-idem",
                TreeId = tree.Id,
                OriginChildRecordingId = rLive.RecordingId
            };

            Func<string, Vector3d?> liveProvider = _ => new Vector3d(7, 8, 9);

            RenderSessionState.RebuildFromMarker(
                marker, new List<Recording> { rLive, rSib }, TreeLookupFor(tree, bp), liveProvider);

            Assert.True(RenderSessionState.TryLookup(rSib.RecordingId, 0, AnchorSide.Start, out var ac1));

            RenderSessionState.RebuildFromMarker(
                marker, new List<Recording> { rLive, rSib }, TreeLookupFor(tree, bp), liveProvider);

            Assert.True(RenderSessionState.TryLookup(rSib.RecordingId, 0, AnchorSide.Start, out var ac2));

            Assert.Equal(ac1.Epsilon.x, ac2.Epsilon.x, 9);
            Assert.Equal(ac1.Epsilon.y, ac2.Epsilon.y, 9);
            Assert.Equal(ac1.Epsilon.z, ac2.Epsilon.z, 9);
            Assert.Equal(ac1.UT, ac2.UT);
            Assert.Equal(1, RenderSessionState.Count);
        }

        // ----- 7. fresh session overwrites previous ------------------------

        [Fact]
        public void RebuildFromMarker_DifferentSession_OverwritesPrevious()
        {
            // What makes it fail: a session id flip without map clear would
            // let stale anchors from the prior session bleed into the new
            // session — a ghost that was anchored under sess-A would still
            // resolve under sess-B for the same (recordingId, sectionIdx).
            var rLive = MakeRecording("live", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rSibA = MakeRecording("a", "Kerbin", 0, 100, 50, (1, 0, 70));
            var (treeA, bpA) = MakeTreeWithSplit("tA", 50.0, rLive, rSibA);
            var markerA = new ReFlySessionMarker
            {
                SessionId = "sess-A",
                TreeId = treeA.Id,
                OriginChildRecordingId = rLive.RecordingId
            };
            RenderSessionState.RebuildFromMarker(
                markerA, new List<Recording> { rLive, rSibA }, TreeLookupFor(treeA, bpA),
                _ => Vector3d.zero);

            Assert.Equal(1, RenderSessionState.Count);
            Assert.Equal("sess-A", RenderSessionState.CurrentSessionId);

            var rLive2 = MakeRecording("live2", "Kerbin", 200, 300, 250, (0, 0, 70));
            var rSibB  = MakeRecording("b",     "Kerbin", 200, 300, 250, (5, 5, 70));
            var (treeB, bpB) = MakeTreeWithSplit("tB", 250.0, rLive2, rSibB);
            var markerB = new ReFlySessionMarker
            {
                SessionId = "sess-B",
                TreeId = treeB.Id,
                OriginChildRecordingId = rLive2.RecordingId
            };
            RenderSessionState.RebuildFromMarker(
                markerB, new List<Recording> { rLive2, rSibB }, TreeLookupFor(treeB, bpB),
                _ => Vector3d.zero);

            Assert.Equal(1, RenderSessionState.Count);
            Assert.Equal("sess-B", RenderSessionState.CurrentSessionId);
            Assert.False(RenderSessionState.TryLookup(rSibA.RecordingId, 0, AnchorSide.Start, out _));
            Assert.True(RenderSessionState.TryLookup(rSibB.RecordingId, 0, AnchorSide.Start, out _));
        }

        // ----- 8. Clear logs reason ----------------------------------------

        [Fact]
        public void Clear_RemovesAll_LogsInfoWithReason()
        {
            // What makes it fail: a silent Clear() (no log line) leaves the
            // operator unable to identify when stale state was discarded —
            // the exact debugging gap HR-9 forbids.
            var rLive = MakeRecording("live", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rSib  = MakeRecording("sib",  "Kerbin", 0, 100, 50, (1, 0, 70));
            var (tree, bp) = MakeTreeWithSplit("tC", 50.0, rLive, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-clear",
                TreeId = tree.Id,
                OriginChildRecordingId = rLive.RecordingId
            };
            RenderSessionState.RebuildFromMarker(
                marker, new List<Recording> { rLive, rSib }, TreeLookupFor(tree, bp),
                _ => Vector3d.zero);
            Assert.Equal(1, RenderSessionState.Count);

            RenderSessionState.Clear("scene-exit");

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Null(RenderSessionState.CurrentSessionId);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("Clear: reason=scene-exit"));
        }

        // ----- 9. lookup after Clear ---------------------------------------

        [Fact]
        public void TryLookup_AfterClear_ReturnsFalse()
        {
            var rLive = MakeRecording("live", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rSib  = MakeRecording("sib",  "Kerbin", 0, 100, 50, (1, 0, 70));
            var (tree, bp) = MakeTreeWithSplit("tD", 50.0, rLive, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-tlc",
                TreeId = tree.Id,
                OriginChildRecordingId = rLive.RecordingId
            };
            RenderSessionState.RebuildFromMarker(
                marker, new List<Recording> { rLive, rSib }, TreeLookupFor(tree, bp),
                _ => Vector3d.zero);
            Assert.True(RenderSessionState.TryLookup(rSib.RecordingId, 0, AnchorSide.Start, out _));

            RenderSessionState.Clear("test");

            // What makes it fail: a Clear() that only zeroed Count but kept
            // dictionary entries would let a stale lookup escape.
            Assert.False(RenderSessionState.TryLookup(rSib.RecordingId, 0, AnchorSide.Start, out _));
        }

        // ----- 10. miss returns false --------------------------------------

        [Fact]
        public void TryLookup_UnknownKey_ReturnsFalse()
        {
            // What makes it fail: a TryLookup that returned `default` with
            // success=true on miss would inject a phantom zero translation
            // — a HR-9 violation by construction.
            Assert.False(RenderSessionState.TryLookup("does-not-exist", 0, AnchorSide.Start, out _));
            Assert.Null(RenderSessionState.LookupForSegmentStart("does-not-exist", 0));
        }

        // ----- 11. live read happens exactly once --------------------------

        [Fact]
        public void LiveReadHappensExactlyOnce_AcrossMultipleSiblings()
        {
            // What makes it fail: HR-15 enforced at the type level. A
            // refactor that called the live-position provider once per
            // sibling (instead of once per session) would re-read live
            // state during the rebuild — invisible in production until a
            // future Phase 5 reused the same provider for the per-frame
            // co-bubble path, where it would resurface as the naive-relative
            // trap (design doc §3.4).
            var rLive = MakeRecording("live", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rA    = MakeRecording("a",    "Kerbin", 0, 100, 50, (1, 0, 70));
            var rB    = MakeRecording("b",    "Kerbin", 0, 100, 50, (0, 1, 70));
            var rC    = MakeRecording("c",    "Kerbin", 0, 100, 50, (0, 0, 71));
            var (tree, bp) = MakeTreeWithSplit("t11", 50.0, rLive, rA, rB, rC);

            int callCount = 0;
            Func<string, Vector3d?> provider = _ => { callCount++; return Vector3d.zero; };

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "s11",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rA, rB, rC },
                TreeLookupFor(tree, bp),
                provider);

            Assert.Equal(3, RenderSessionState.Count);
            Assert.Equal(1, callCount);
        }

        // ----- 12. RELATIVE-frame guard ------------------------------------

        [Fact]
        public void RebuildFromMarker_RelativeSection_SkippedWithVerbose()
        {
            // What makes it fail: feeding metres-along-anchor-axes
            // (RELATIVE-frame TrajectoryPoint.lat/lon/alt) into
            // GetWorldSurfacePosition silently places the ghost roughly
            // on the body surface but at a meaningless lat/lon — the
            // exact #582 / #571 contributor flagged by .claude/CLAUDE.md.
            // Phase 2 must skip RELATIVE sections; Phase 4+ resolves them
            // through the version dispatch.
            var rLive = MakeRecording("live", "Kerbin", 0, 100, 50, (0, 0, 70));
            var rSib  = MakeRecordingRelative("sib", "Kerbin", 0, 100, 50, (1, 2, 70));
            var (tree, bp) = MakeTreeWithSplit("t12", 50.0, rLive, rSib);

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "s12",
                    TreeId = tree.Id,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                _ => Vector3d.zero);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("section-relative-skip"));
        }
    }
}
