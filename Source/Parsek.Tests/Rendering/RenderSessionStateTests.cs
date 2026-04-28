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
                ActiveReFlyRecordingId = rOrigin.RecordingId,
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
                ActiveReFlyRecordingId = rOrigin.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                ActiveReFlyRecordingId = rLive2.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                ActiveReFlyRecordingId = rLive.RecordingId,
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
                    ActiveReFlyRecordingId = rLive.RecordingId,
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
                    ActiveReFlyRecordingId = rLive.RecordingId,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                _ => Vector3d.zero);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("section-relative-skip"));
        }

        // ----- 13. P1#2 review fix: inertial-spline anchor ----------------

        private static SmoothingSpline MakeAnchorTestSpline(
            byte frameTag, double ut, Vector3d wildlyOffsetLatLonAlt)
        {
            // 4 knots, all evaluating to the same wildly-offset (lat, lon, alt).
            // If the anchor builder uses this spline (instead of skipping it),
            // P_smoothed_world becomes (lat, lon, alt) — way off the raw
            // boundary sample — and ε flips by the same amount.
            int kc = 4;
            double[] knots = new double[kc];
            float[] cx = new float[kc];
            float[] cy = new float[kc];
            float[] cz = new float[kc];
            for (int i = 0; i < kc; i++)
            {
                knots[i] = ut - 1.0 + i;
                cx[i] = (float)wildlyOffsetLatLonAlt.x;
                cy[i] = (float)wildlyOffsetLatLonAlt.y;
                cz[i] = (float)wildlyOffsetLatLonAlt.z;
            }
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = knots,
                ControlsX = cx,
                ControlsY = cy,
                ControlsZ = cz,
                FrameTag = frameTag,
                IsValid = true,
            };
        }

        [Fact]
        public void RebuildFromMarker_InertialSpline_FallsBackToRawSample()
        {
            // What makes it fail: P1#2 — without the FrameTag != 0 skip, the
            // anchor builder hands an inertial-longitude (lat, lon, alt) to
            // surfaceLookup (which is GetWorldSurfacePosition — body-fixed).
            // ε then carries the body's rotation-phase offset between
            // recording and playback. Skipping inertial splines makes
            // P_smoothed_world fall back to the raw boundary sample, so
            // ε == live_world_at_spawn + recordedOffset - ghost_abs_world.
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib  = MakeRecording("sib-rec",  "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));
            var (tree, bp) = MakeTreeWithSplit("t-inertial", 50.0, rLive, rSib);

            // Plant a Phase 4 inertial spline (FrameTag=1) at the sibling's
            // section index 0. Make its evaluated value wildly different from
            // the raw boundary sample so a "spline used as if body-fixed"
            // bug is unmissable.
            SectionAnnotationStore.PutSmoothingSpline(
                rSib.RecordingId, 0,
                MakeAnchorTestSpline(frameTag: 1, ut: 50.0,
                    wildlyOffsetLatLonAlt: new Vector3d(7777, 8888, 9999)));

            Vector3d liveAtSpawn = new Vector3d(100.0, 200.0, 300.0);
            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sess-inertial",
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = rLive.RecordingId,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                _ => liveAtSpawn);

            Assert.True(RenderSessionState.TryLookup(
                rSib.RecordingId, 0, AnchorSide.Start, out AnchorCorrection ac));

            // Expected (skip path):
            //   recordedOffset = (1,2,70) - (0,0,70) = (1,2,0)
            //   target         = (100,200,300) + (1,2,0) = (101,202,300)
            //   pSmoothedWorld = ghost_abs_world = (1,2,70)   <- raw fallback
            //   ε              = (100,200,230)
            // If the inertial spline had been (incorrectly) consumed,
            //   pSmoothedWorld would be (7777,8888,9999) and ε ≈ (-7676,-8686,-9699).
            Assert.Equal(100.0, ac.Epsilon.x, 6);
            Assert.Equal(200.0, ac.Epsilon.y, 6);
            Assert.Equal(230.0, ac.Epsilon.z, 6);
        }

        [Fact]
        public void RebuildFromMarker_InertialSpline_LogsSkip()
        {
            // What makes it fail: P1#2 visibility — HR-9 demands a visible
            // skip line so the operator can see which anchors degraded to
            // raw fallback when they expected sub-mm spline precision.
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib  = MakeRecording("sib-rec",  "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));
            var (tree, bp) = MakeTreeWithSplit("t-inertial-log", 50.0, rLive, rSib);

            SectionAnnotationStore.PutSmoothingSpline(
                rSib.RecordingId, 0,
                MakeAnchorTestSpline(frameTag: 1, ut: 50.0,
                    wildlyOffsetLatLonAlt: new Vector3d(0, 0, 70)));

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sess-inertial-log",
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = rLive.RecordingId,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                _ => Vector3d.zero);

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]")
                && l.Contains("skipping inertial spline for anchor")
                && l.Contains("FrameTag=1")
                && l.Contains("sibId=" + rSib.RecordingId));

            // Sanity: ε source still LiveSeparation — the skip is silent at
            // the AnchorCorrection record level.
            Assert.True(RenderSessionState.TryLookup(rSib.RecordingId, 0, AnchorSide.Start, out var ac));
            Assert.Equal(AnchorSource.LiveSeparation, ac.Source);
        }

        [Fact]
        public void RebuildFromMarker_BodyFixedSpline_StillUsesSplineEvaluation()
        {
            // What makes it fail: an over-broad skip (e.g. spline.FrameTag != 0
            // matched FrameTag == 0 too) would regress the Phase 1 splineHit
            // path — every anchor would fall back to the raw boundary sample
            // even when a body-fixed spline is present. The Pipeline-Anchor
            // L18 Info line must still report splineHit=true when FrameTag = 0.
            var rLive = MakeRecording("live-rec", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib  = MakeRecording("sib-rec",  "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));
            var (tree, bp) = MakeTreeWithSplit("t-bodyfixed", 50.0, rLive, rSib);

            // Body-fixed spline (FrameTag=0) at the same lat/lon/alt as the
            // raw boundary sample so ε is unchanged numerically — the
            // observable distinction is splineHit=true in the L18 log.
            SectionAnnotationStore.PutSmoothingSpline(
                rSib.RecordingId, 0,
                MakeAnchorTestSpline(frameTag: 0, ut: 50.0,
                    wildlyOffsetLatLonAlt: new Vector3d(1.0, 2.0, 70.0)));

            RenderSessionState.RebuildFromMarker(
                new ReFlySessionMarker
                {
                    SessionId = "sess-bodyfixed",
                    TreeId = tree.Id,
                    ActiveReFlyRecordingId = rLive.RecordingId,
                    OriginChildRecordingId = rLive.RecordingId
                },
                new List<Recording> { rLive, rSib },
                TreeLookupFor(tree, bp),
                _ => Vector3d.zero);

            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("Anchor ε computed")
                && l.Contains("splineHit=true"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Pipeline-Anchor]") && l.Contains("skipping inertial spline"));
        }

        [Fact]
        public void RebuildFromMarker_LiveLookupKeysOffActiveReFlyRecordingId()
        {
            // What makes it fail: AtomicMarkerWrite creates a NotCommitted
            // provisional recording for the active live vessel and stores
            // its id in ActiveReFlyRecordingId. OriginChildRecordingId is
            // the supersede target whose persistent-vessel-id no longer
            // resolves to a live KSP Vessel. If the live read keyed off
            // OriginChildRecordingId, the provider would return null and
            // the anchor map would be cleared as live-vessel-missing.
            // Per design §7.1: live read keys off ActiveReFlyRecordingId;
            // OriginChildRecordingId still drives DAG / sibling lookup.
            var rOrigin = MakeRecording("origin-child", "Kerbin", 0, 100, 50, (0.0, 0.0, 70.0));
            var rSib    = MakeRecording("sib",          "Kerbin", 0, 100, 50, (1.0, 2.0, 70.0));

            int liveProviderCalls = 0;
            string idSeen = null;
            Func<string, Vector3d?> liveProvider = id =>
            {
                liveProviderCalls++;
                idSeen = id;
                return new Vector3d(100.0, 200.0, 300.0);
            };

            var (tree, bp) = MakeTreeWithSplit("t-active", 50.0, rOrigin, rSib);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-active",
                TreeId = tree.Id,
                ActiveReFlyRecordingId = "provisional-live",
                OriginChildRecordingId = rOrigin.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                new List<Recording> { rOrigin, rSib },
                TreeLookupFor(tree, bp),
                liveProvider);

            Assert.Equal(1, liveProviderCalls);
            Assert.Equal("provisional-live", idSeen);
            Assert.Equal(1, RenderSessionState.Count);
        }

        [Fact]
        public void RebuildFromMarker_ClearsLerpDedupSets()
        {
            // What makes it fail: per-session lerp dedup sets
            // (Degenerate / Divergent / SingleAnchor / ClampOut) leak
            // across re-fly sessions if RebuildFromMarker doesn't reset
            // them — a new session reusing the same (recordingId,
            // sectionIndex) key would silently suppress its first Warn /
            // Verbose. Design §19.2 Pipeline-Lerp rows describe these as
            // per-session diagnostics.
            //
            // Drive a degenerate-span Warn (Start.UT == End.UT) once,
            // then trigger a fresh RebuildFromMarker, then drive the same
            // Warn again — both must fire (proving the dedup was reset).
            var ac1 = new AnchorCorrection("rec1", 0, AnchorSide.Start, 100.0, Vector3d.zero, AnchorSource.LiveSeparation);
            var ac2 = new AnchorCorrection("rec1", 0, AnchorSide.End,   100.0, new Vector3d(1, 0, 0), AnchorSource.LiveSeparation);
            var both = AnchorCorrectionInterval.Both(ac1, ac2);
            int firstWarnCount = 0, secondWarnCount = 0;

            ParsekLog.TestSinkForTesting = line =>
            {
                if (line.Contains("[Pipeline-Lerp]") && line.Contains("degenerate-span"))
                    firstWarnCount++;
            };
            both.EvaluateAt(100.0);
            both.EvaluateAt(100.0);
            Assert.Equal(1, firstWarnCount);

            // Trigger a fresh RebuildFromMarker. The simplest path: a
            // null-marker rebuild (still hits the rebuild entry point and
            // resets dedup via the early-return Clear path).
            RenderSessionState.RebuildFromMarker(null);

            ParsekLog.TestSinkForTesting = line =>
            {
                if (line.Contains("[Pipeline-Lerp]") && line.Contains("degenerate-span"))
                    secondWarnCount++;
            };
            both.EvaluateAt(100.0);
            Assert.Equal(1, secondWarnCount);
        }
    }
}
