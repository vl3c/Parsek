using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 2 T5e tests for the <c>ParsekFlight.allowAnchorCorrection</c>
    /// gate (design doc §6.3 / §6.4 single-anchor case / §7.1 / §18 Phase 2 /
    /// §26.1 HR-9 / HR-15). The full
    /// <see cref="ParsekFlight.InterpolateAndPosition"/> integration requires a
    /// live KSP scene (CelestialBody, FlightGlobals); the in-game
    /// <c>Pipeline_Anchor_LiveSeparation</c> test pins the integration end to
    /// end. These xUnit tests pin the static gate's six branches: null id,
    /// negative section, null settings, flag off, store miss, and the
    /// happy-path lookup hit.
    /// <para>
    /// Touches static state (<see cref="RenderSessionState"/> map +
    /// <see cref="ParsekSettings.CurrentOverrideForTesting"/> +
    /// <see cref="ParsekLog.TestSinkForTesting"/>) so runs in the
    /// <c>Sequential</c> collection.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorCorrectionConsumerHookTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorCorrectionConsumerHookTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            RenderSessionState.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void AllowAnchorCorrection_NullRecordingId_ReturnsFalse()
        {
            // What makes it fail: an empty / null recordingId means the
            // playback path did not thread context through (legacy flat-
            // point recordings, background-recorder ghosts, etc.). The gate
            // must short-circuit with no AnchorCorrection — otherwise the
            // store would index by an empty string and serve a phantom
            // entry. HR-9 forbids the silent miss-then-substitute.
            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: null, sectionIndex: 0,
                out AnchorCorrection ac);
            Assert.False(result);
            Assert.Equal(0.0, ac.Epsilon.x);
            Assert.Equal(0.0, ac.Epsilon.y);
            Assert.Equal(0.0, ac.Epsilon.z);
        }

        [Fact]
        public void AllowAnchorCorrection_NegativeSectionIndex_ReturnsFalse()
        {
            // What makes it fail: the body-fixed playback path passes -1 when
            // TrajectoryMath.FindTrackSectionForUT cannot resolve a section
            // (ut outside any section's range, or no sections at all). The
            // gate must reject this — store keys are non-negative.
            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec", sectionIndex: -1,
                out AnchorCorrection ac);
            Assert.False(result);
        }

        [Fact]
        public void AllowAnchorCorrection_NullSettings_ReturnsFalse()
        {
            // What makes it fail: production reads
            // ParsekSettings.Current via HighLogic.CurrentGame which can be
            // null between scene transitions. A null settings object must
            // disable the gate, not throw — otherwise the playback frame
            // hot path would crash mid-scene-load.
            ParsekSettings.CurrentOverrideForTesting = null;
            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec", sectionIndex: 0,
                out AnchorCorrection ac);
            Assert.False(result);
        }

        [Fact]
        public void AllowAnchorCorrection_FlagOff_ReturnsFalse()
        {
            // What makes it fail: the rollout flag exists so a regression
            // can be debugged via toggle. If the gate ignored the flag,
            // operators could not disable Phase 2 without a code change.
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                useAnchorCorrection = false
            };
            // Seed a real anchor so a buggy gate would return true; the
            // assertion proves the flag short-circuits BEFORE the lookup.
            SeedAnchor("rec", 0, new Vector3d(1, 2, 3));
            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec", sectionIndex: 0,
                out AnchorCorrection ac);
            Assert.False(result);
        }

        [Fact]
        public void AllowAnchorCorrection_NoAnchorInStore_ReturnsFalse()
        {
            // What makes it fail: a recording with no rebuilt anchor (e.g.
            // not part of the active re-fly tree, or the rebuild ran with
            // no siblings) must NOT produce a phantom AnchorCorrection.
            // HR-9 — silent fall-through is correct here, but the gate must
            // return false so the consumer's `interpolatedPos += ε` path
            // does not run with a default-initialized Vector3d.zero.
            // (Adding zero is semantically a no-op, but the design contract
            // is "no entry → don't apply" and the test pins it.)
            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec-empty", sectionIndex: 0,
                out AnchorCorrection ac);
            Assert.False(result);
        }

        [Fact]
        public void AllowAnchorCorrection_AnchorPresent_ReturnsTrueAndSetsOut()
        {
            // What makes it fail: the central T5 invariant — when the
            // RenderSessionState carries a start-side anchor for the
            // recording/section pair, the gate returns true AND the out
            // parameter carries the cached ε. A returns-true-with-default-ε
            // bug would silently zero every ghost's correction.
            var expectedEps = new Vector3d(11.0, 22.0, 33.0);
            SeedAnchor("rec-hit", sectionIndex: 2, epsilon: expectedEps);

            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec-hit", sectionIndex: 2,
                out AnchorCorrection ac);

            Assert.True(result);
            Assert.Equal(expectedEps.x, ac.Epsilon.x, 9);
            Assert.Equal(expectedEps.y, ac.Epsilon.y, 9);
            Assert.Equal(expectedEps.z, ac.Epsilon.z, 9);
            Assert.Equal("rec-hit", ac.RecordingId);
            Assert.Equal(2, ac.SectionIndex);
            Assert.Equal(AnchorSide.Start, ac.Side);
        }

        [Fact]
        public void AllowAnchorCorrection_WrongSection_ReturnsFalse()
        {
            // What makes it fail: HR-9 — anchors are keyed by
            // (recordingId, sectionIndex, side). A query for a section that
            // has no anchor must miss; a buggy fallback to "any section's
            // anchor" would propagate ε across hard discontinuities,
            // violating HR-7.
            SeedAnchor("rec-multi", sectionIndex: 0, epsilon: new Vector3d(1, 0, 0));

            bool result = ParsekFlight.allowAnchorCorrection(
                recordingId: "rec-multi", sectionIndex: 1,
                out AnchorCorrection ac);
            Assert.False(result);
        }

        // -----------------------------------------------------------------

        private static void SeedAnchor(string recordingId, int sectionIndex, Vector3d epsilon)
        {
            // Drive the test overload of RebuildFromMarker with a synthetic
            // tree shaped so the (recordingId, sectionIndex, Start) key is
            // populated in the store. The lookup override returns the input
            // tuple as a Vector3d so the recordedOffset = (ghost - live)
            // works out to a non-zero, deterministic value, and we then
            // adjust live_world_at_spawn so ε == epsilon.
            //
            // Math: target = live_world_at_spawn + recordedOffset
            //       ε = target - P_smoothed_world
            // With surface lookup returning Vec(lat, lon, alt):
            //       recordedOffset = (1, 0, 0)
            //       P_smoothed_world = (1, 0, 0)
            //   so ε = live_world_at_spawn + (1,0,0) - (1,0,0)
            //        = live_world_at_spawn
            // Therefore live_world_at_spawn := epsilon yields ε = epsilon.
            RenderSessionState.SurfaceLookupOverrideForTesting =
                (bodyName, lat, lon, alt) => new Vector3d(lat, lon, alt);

            // Build sectionIndex+1 ABSOLUTE TrackSections so the requested
            // index resolves to the one carrying the bp UT.
            var rOrigin = new Recording
            {
                RecordingId = recordingId + "-origin",
                VesselName = "origin",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var rSib = new Recording
            {
                RecordingId = recordingId,
                VesselName = "sib",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            const double bpUT = 50.0;
            for (int s = 0; s <= sectionIndex; s++)
            {
                AddSection(rOrigin, s, bpUT, (0.0, 0.0, 0.0));
                AddSection(rSib, s, bpUT, (1.0, 0.0, 0.0));
            }

            var tree = new RecordingTree { Id = "tree-" + recordingId };
            var bp = new BranchPoint
            {
                Id = "bp-" + recordingId,
                UT = bpUT + sectionIndex * 1000.0, // place bp in the requested section
                Type = BranchPointType.Undock
            };
            tree.Recordings[rOrigin.RecordingId] = rOrigin;
            tree.Recordings[rSib.RecordingId] = rSib;
            bp.ChildRecordingIds.Add(rOrigin.RecordingId);
            bp.ChildRecordingIds.Add(rSib.RecordingId);
            tree.BranchPoints.Add(bp);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-" + recordingId,
                TreeId = tree.Id,
                OriginChildRecordingId = rOrigin.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                new List<Recording> { rOrigin, rSib },
                _ => new RecordingTreeContext(tree, bp),
                _ => epsilon);
        }

        private static void AddSection(Recording rec, int sectionIdx, double bpStart,
            (double lat, double lon, double alt) atBp)
        {
            double sectionStart = sectionIdx * 1000.0;
            double sectionEnd = sectionStart + 100.0;
            double pointUT = bpStart + sectionIdx * 1000.0;
            var pt = new TrajectoryPoint
            {
                ut = pointUT,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            rec.Points.Add(pt);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = sectionStart,
                endUT = sectionEnd,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint> { pt },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active
            });
        }
    }
}
