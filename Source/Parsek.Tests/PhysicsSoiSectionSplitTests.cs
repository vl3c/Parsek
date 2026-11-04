using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Audit finding R3 (replay-theory audit 2026-11-04): an SOI change while the vessel is
    /// in physics (a burn through the boundary, a 1x or physics-warp coast) used to return
    /// early from <c>FlightRecorder.OnVesselSOIChanged</c>, so one Absolute section spanned
    /// both bodies: the optimizer's body rule (#251) could not fire and the crossing pair
    /// interpolated across two body frames. The sampler now splits the section at the first
    /// sample on the new body.
    /// </summary>
    [Collection("Sequential")]
    public class PhysicsSoiSectionSplitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly FlightRecorder recorder;

        public PhysicsSoiSectionSplitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            recorder = new FlightRecorder();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Pt(double ut, string body, double lat = 1.0, double lon = 2.0, double alt = 2.4e6)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(800f, 0f, 0f),
                bodyName = body,
                recordedGroundClearance = double.NaN,
            };
        }

        // ---- pure decision ----

        [Theory]
        [InlineData(true, true, ReferenceFrame.Absolute, 3, "Kerbin", "Mun", (int)PhysicsBodyChangeDecision.NotApplicable)]
        [InlineData(false, false, ReferenceFrame.Absolute, 3, "Kerbin", "Mun", (int)PhysicsBodyChangeDecision.NotApplicable)]
        [InlineData(false, true, ReferenceFrame.OrbitalCheckpoint, 3, "Kerbin", "Mun", (int)PhysicsBodyChangeDecision.NotApplicable)]
        [InlineData(false, true, ReferenceFrame.Absolute, 3, "Kerbin", null, (int)PhysicsBodyChangeDecision.NotApplicable)]
        [InlineData(false, true, ReferenceFrame.Absolute, 3, null, "Mun", (int)PhysicsBodyChangeDecision.NotApplicable)]
        [InlineData(false, true, ReferenceFrame.Absolute, 0, null, "Mun", (int)PhysicsBodyChangeDecision.EmptySection)]
        [InlineData(false, true, ReferenceFrame.Absolute, 3, "Kerbin", "Kerbin", (int)PhysicsBodyChangeDecision.SameBody)]
        [InlineData(false, true, ReferenceFrame.Absolute, 3, "Kerbin", "Mun", (int)PhysicsBodyChangeDecision.Split)]
        [InlineData(false, true, ReferenceFrame.Relative, 3, "Mun", "Kerbin", (int)PhysicsBodyChangeDecision.Split)]
        public void Decide_TruthTable(
            bool onRails, bool active, ReferenceFrame frame, int frames,
            string sectionBody, string sampleBody, int expected)
        {
            Assert.Equal((PhysicsBodyChangeDecision)expected, FlightRecorder.DecidePhysicsBodyChange(
                onRails, active, frame, frames, sectionBody, sampleBody));
        }

        // ---- bookkeeping ----

        private void RecordKerbinBurnUpTo(double lastUT)
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, 100.0);
            for (double ut = 100.0; ut <= lastUT; ut += 10.0)
                recorder.CommitRecordedPointForTesting(Pt(ut, "Kerbin", alt: 2.4e6 + ut));
        }

        [Fact]
        public void BodyChange_SplitsSection_ClosingSampleOnOldBody_FirstFrameOnNewBody()
        {
            RecordKerbinBurnUpTo(140.0);
            const double crossUT = 147.5;
            TrajectoryPoint munSample = Pt(crossUT, "Mun", lat: 5.0, lon: 6.0, alt: 2.4e6);
            TrajectoryPoint closing = Pt(crossUT, "Kerbin", lat: 1.5, lon: 2.5, alt: 1.2e7);

            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(munSample, closing));
            recorder.CommitRecordedPointForTesting(munSample);
            recorder.CommitRecordedPointForTesting(Pt(157.5, "Mun", lat: 5.5));
            recorder.CloseCurrentTrackSection(157.5);

            Assert.Equal(2, recorder.TrackSections.Count);
            TrackSection kerbin = recorder.TrackSections[0];
            TrackSection mun = recorder.TrackSections[1];
            Assert.Equal(crossUT, kerbin.endUT);
            Assert.Equal(crossUT, mun.startUT);
            Assert.Equal(ReferenceFrame.Absolute, mun.referenceFrame);
            Assert.Equal(SegmentEnvironment.ExoPropulsive, mun.environment);
            Assert.All(kerbin.frames, f => Assert.Equal("Kerbin", f.bodyName));
            Assert.All(mun.frames, f => Assert.Equal("Mun", f.bodyName));
            Assert.Equal(crossUT, kerbin.frames.Last().ut);
            Assert.Equal(crossUT, mun.frames.First().ut);

            // Flat list: the closing and opening samples share the crossing UT (the
            // equal-UT seam), so no interpolation pair spans the two bodies.
            List<TrajectoryPoint> flat = recorder.Recording;
            for (int i = 1; i < flat.Count; i++)
            {
                if (flat[i - 1].bodyName != flat[i].bodyName)
                    Assert.Equal(flat[i - 1].ut, flat[i].ut);
            }

            Assert.True(recorder.SoiChangePending);
            Assert.Equal("Kerbin", recorder.SoiChangeFromBody);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Reference frame transition: Absolute -> Absolute at SOI boundary")
                && l.Contains("onRails=0") && l.Contains("from=Kerbin") && l.Contains("to=Mun")
                && l.Contains("closingSample=1"));
        }

        [Fact]
        public void BodyChange_SplitBoundary_IsWhatTheOptimizerBodyRuleSplitsOnABurn()
        {
            RecordKerbinBurnUpTo(140.0);
            TrajectoryPoint munSample = Pt(147.5, "Mun");
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(munSample, Pt(147.5, "Kerbin")));
            recorder.CommitRecordedPointForTesting(munSample);
            recorder.CommitRecordedPointForTesting(Pt(157.5, "Mun"));
            recorder.CloseCurrentTrackSection(157.5);

            var rec = new Recording
            {
                RecordingId = "physics-soi-burn",
                TrackSections = new List<TrackSection>(recorder.TrackSections),
                Points = new List<TrajectoryPoint>(recorder.Recording),
            };
            Assert.True(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out var reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.BodyChange, reason);
        }

        [Theory]
        [InlineData("Mun", "Kerbin")]
        [InlineData("Kerbin", "Mun")]
        public void OptimizerSplit_SecondHalfStartsOnTheNewBody_NotOnTheClosingSample(string from, string to)
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, 100.0);
            for (double ut = 100.0; ut <= 140.0; ut += 10.0)
                recorder.CommitRecordedPointForTesting(Pt(ut, from, alt: 2.4e6 + ut));
            TrajectoryPoint opening = Pt(147.5, to, lat: 5, lon: 6, alt: 1.2e7);
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(opening, Pt(147.5, from)));
            recorder.CommitRecordedPointForTesting(opening);
            for (double ut = 157.5; ut <= 200; ut += 10.0)
                recorder.CommitRecordedPointForTesting(Pt(ut, to, lat: 5));
            recorder.CloseCurrentTrackSection(197.5);

            var rec = new Recording
            {
                RecordingId = "physics-soi-split-start",
                StartBodyName = from,
                Points = new List<TrajectoryPoint>(recorder.Recording),
                TrackSections = new List<TrackSection>(recorder.TrackSections),
            };
            // The flat list opens the second half with the closing sample on the old body.
            Assert.Contains(rec.Points, p => p.ut == 147.5 && p.bodyName == from);
            Assert.True(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out _));
            Recording second = RecordingOptimizer.SplitAtSection(rec, 1);
            Assert.Equal(to, second.StartBodyName);
            Assert.Equal(from, rec.StartBodyName);
            Assert.Equal(from, rec.Points.Last().bodyName);
        }

        [Fact]
        public void BodyChange_OnACoast_StaysCohesive()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 100.0);
            recorder.CommitRecordedPointForTesting(Pt(100.0, "Kerbin"));
            recorder.CommitRecordedPointForTesting(Pt(110.0, "Kerbin"));
            TrajectoryPoint munSample = Pt(115.0, "Mun");
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(munSample, null));
            recorder.CommitRecordedPointForTesting(munSample);
            recorder.CommitRecordedPointForTesting(Pt(125.0, "Mun"));
            recorder.CloseCurrentTrackSection(125.0);

            var rec = new Recording
            {
                RecordingId = "physics-soi-coast",
                TrackSections = new List<TrackSection>(recorder.TrackSections),
            };
            Assert.False(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out var reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange, reason);
            Assert.Contains(logLines, l => l.Contains("closingSample=0"));
        }

        [Fact]
        public void SameBody_DoesNotSplit()
        {
            RecordKerbinBurnUpTo(120.0);
            Assert.False(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(125.0, "Kerbin"), null));
            Assert.Empty(recorder.TrackSections);
            Assert.False(recorder.SoiChangePending);
        }

        [Fact]
        public void EmptySection_DoesNotSplit_TheSampleDefinesItsBody()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 100.0);
            Assert.False(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(100.5, "Mun"), null));
            Assert.Empty(recorder.TrackSections);
        }

        [Fact]
        public void OnRails_DoesNotSplit()
        {
            RecordKerbinBurnUpTo(120.0);
            recorder.SetOnRailsStateForTesting(true, default(OrbitSegment));
            Assert.False(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(125.0, "Mun"), null));
            Assert.Empty(recorder.TrackSections);
        }

        [Fact]
        public void ClosingPointOnTheWrongBodyOrUT_IsNotCommitted()
        {
            RecordKerbinBurnUpTo(120.0);
            int before = recorder.Recording.Count;
            // A closing point that is not on the section's body would itself put two bodies
            // in the old section; one at another UT would not close the seam. Both are dropped.
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(125.0, "Mun"), Pt(124.0, "Kerbin")));
            Assert.Equal(before, recorder.Recording.Count);
            Assert.Equal(120.0, recorder.TrackSections[0].frames.Last().ut);
        }

        [Fact]
        public void RelativeSection_SplitKeepsFrameAndAnchor_AndSkipsTheClosingSample()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Relative, 100.0);
            var open = recorder.TrackSections; // closed list, empty so far
            Assert.Empty(open);
            recorder.SetCurrentTrackSectionAnchorForTesting("anchor-rec-1");
            recorder.CommitRecordedPointForTesting(Pt(100.0, "Kerbin", lat: 3.0, lon: 4.0, alt: 5.0));
            recorder.CommitRecordedPointForTesting(Pt(110.0, "Kerbin", lat: 3.5, lon: 4.5, alt: 5.5));

            int before = recorder.Recording.Count;
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(115.0, "Mun"), Pt(115.0, "Kerbin")));
            Assert.Equal(before, recorder.Recording.Count);
            recorder.CloseCurrentTrackSection(120.0);

            Assert.Equal(2, recorder.TrackSections.Count);
            TrackSection next = recorder.TrackSections[1];
            Assert.Equal(ReferenceFrame.Relative, next.referenceFrame);
            Assert.Equal("anchor-rec-1", next.anchorRecordingId);
            Assert.Equal(0u, next.anchorVesselId);
        }

        [Fact]
        public void ArmedSample_IsClearedByTheSplit()
        {
            recorder.ArmPhysicsSoiSample("Kerbin", "Mun", 147.0);
            Assert.True(recorder.PhysicsSoiSamplePendingForTesting);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("SOI changed off rails Kerbin -> Mun")
                && l.Contains("UT=147.00"));

            RecordKerbinBurnUpTo(140.0);
            Assert.True(recorder.TrySplitSectionAtPhysicsBodyChange(Pt(147.5, "Mun"), null));
            Assert.False(recorder.PhysicsSoiSamplePendingForTesting);
        }

        // ---- production wiring (the live handlers need a Vessel) ----

        [Fact]
        public void OnPhysicsFrame_ChecksTheBodyBeforeCommittingTheSample()
        {
            string body = MethodBody("FlightRecorder.cs", "public void OnPhysicsFrame(Vessel v)");
            int split = body.IndexOf("SplitSectionIfPhysicsBodyChanged(v, point)", StringComparison.Ordinal);
            int relative = body.IndexOf("ApplyRelativeOffset(ref point, v)", StringComparison.Ordinal);
            int commit = body.IndexOf("CommitRecordedPoint(point, v", StringComparison.Ordinal);
            Assert.True(split >= 0, "OnPhysicsFrame no longer runs SplitSectionIfPhysicsBodyChanged");
            Assert.True(split < relative && relative < commit,
                "the body check must see the body-fixed sample, before the Relative rewrite and the commit");
            Assert.Contains("physicsSoiSamplePending", body);
        }

        [Fact]
        public void OnVesselSOIChanged_ArmsTheSampleOffRails()
        {
            string body = MethodBody("FlightRecorder.cs",
                "public void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)");
            int arm = body.IndexOf("ArmPhysicsSoiSample(", StringComparison.Ordinal);
            int onRailsHalf = body.IndexOf("currentOrbitSegment.endUT", StringComparison.Ordinal);
            Assert.True(arm >= 0 && arm < onRailsHalf,
                "OnVesselSOIChanged must arm the physics sample before its on-rails orbit-segment half");
        }

        private static string MethodBody(string file, string signature)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", file);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            var sb = new StringBuilder();
            foreach (string line in File.ReadAllText(path).Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line).Append('\n');
            }
            string src = sb.ToString();
            int sig = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signature);
            int open = src.IndexOf('{', sig);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(open, i - open + 1);
            }
            Assert.True(false, "unbalanced braces after " + signature);
            return null;
        }
    }
}
