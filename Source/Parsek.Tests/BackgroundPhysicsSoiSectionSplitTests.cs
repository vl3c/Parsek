using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The background half of RECORDER-PHYSICS-SOI-NO-SECTION-SPLIT: a LOADED background
    /// vessel (a second vessel flying in formation with the active one) crossing an SOI edge
    /// in physics used to keep one section spanning two bodies, because
    /// <c>BackgroundRecorder.OnBackgroundVesselSOIChanged</c> handled only on-rails states.
    /// The loaded sampler now splits the section at the first sample on the new body, through
    /// the same pure decision and closing-sample rule the active recorder uses.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundPhysicsSoiSectionSplitTests : IDisposable
    {
        private const uint Pid = 4242u;
        private const string RecId = "rec_bg_formation";

        private readonly List<string> logLines = new List<string>();
        private readonly RecordingTree tree;
        private readonly BackgroundRecorder bg;

        public BackgroundPhysicsSoiSectionSplitTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tree = new RecordingTree
            {
                Id = "tree_bg_soi",
                TreeName = "BG SOI Tree",
                RootRecordingId = "rec_active",
                ActiveRecordingId = "rec_active",
            };
            tree.Recordings["rec_active"] = new Recording { RecordingId = "rec_active", VesselName = "Lead" };
            tree.Recordings[RecId] = new Recording
            {
                RecordingId = RecId,
                VesselName = "Wingman",
                VesselPersistentId = Pid,
            };
            tree.BackgroundMap[Pid] = RecId;
            bg = new BackgroundRecorder(tree);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
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

        private Recording Rec => tree.Recordings[RecId];

        private void StartLoaded(SegmentEnvironment env, ReferenceFrame frame = ReferenceFrame.Absolute, string anchor = null)
        {
            bg.InjectLoadedStateWithEnvironmentForTesting(Pid, RecId, env, 100.0,
                initialReferenceFrame: frame, anchorRecordingId: anchor);
        }

        private void FlyOn(string body, double fromUT, double toUT, double alt0 = 2.4e6)
        {
            for (double ut = fromUT; ut <= toUT; ut += 10.0)
                bg.AppendLoadedSampleForTesting(Pid, Pt(ut, body, alt: alt0 + ut));
        }

        // ---- bookkeeping ----

        [Fact]
        public void BodyChange_SplitsLoadedSection_ClosingSampleOnOldBody_FirstFrameOnNewBody()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 140.0);
            const double crossUT = 147.5;
            TrajectoryPoint munSample = Pt(crossUT, "Mun", lat: 5.0, lon: 6.0, alt: 2.4e6);
            TrajectoryPoint closing = Pt(crossUT, "Kerbin", lat: 1.5, lon: 2.5, alt: 1.2e7);

            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, munSample, closing));
            bg.AppendLoadedSampleForTesting(Pid, munSample);
            bg.AppendLoadedSampleForTesting(Pid, Pt(157.5, "Mun", lat: 5.5));
            bg.CloseCurrentTrackSectionForTesting(Pid, 157.5);

            List<TrackSection> sections = bg.GetTrackSectionsForTesting(Pid);
            Assert.Equal(2, sections.Count);
            TrackSection kerbin = sections[0];
            TrackSection mun = sections[1];
            Assert.Equal(crossUT, kerbin.endUT);
            Assert.Equal(crossUT, mun.startUT);
            Assert.Equal(ReferenceFrame.Absolute, mun.referenceFrame);
            Assert.Equal(SegmentEnvironment.ExoPropulsive, mun.environment);
            Assert.Equal(TrackSectionSource.Background, mun.source);
            Assert.All(kerbin.frames, f => Assert.Equal("Kerbin", f.bodyName));
            Assert.All(mun.frames, f => Assert.Equal("Mun", f.bodyName));
            Assert.Equal(crossUT, kerbin.frames.Last().ut);
            Assert.Equal(crossUT, mun.frames.First().ut);

            // The closing sample's altitude is part of the old section's range; the new
            // section's range starts from its own frames only.
            Assert.Equal(1.2e7f, kerbin.maxAltitude);
            Assert.Equal(2.4e6f, mun.minAltitude);

            // Flat list: the closing and opening samples share the crossing UT (the equal-UT
            // seam), so no interpolation pair spans the two bodies.
            List<TrajectoryPoint> flat = Rec.Points;
            Assert.Contains(flat, p => p.ut == crossUT && p.bodyName == "Kerbin");
            Assert.Contains(flat, p => p.ut == crossUT && p.bodyName == "Mun");
            for (int i = 1; i < flat.Count; i++)
            {
                if (flat[i - 1].bodyName != flat[i].bodyName)
                    Assert.Equal(flat[i - 1].ut, flat[i].ut);
            }

            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Reference frame transition: Absolute -> Absolute at SOI boundary")
                && l.Contains("onRails=0") && l.Contains("from=Kerbin") && l.Contains("to=Mun")
                && l.Contains("closingSample=1") && l.Contains("pid=" + Pid) && l.Contains("rec=" + RecId));
        }

        [Fact]
        public void BodyChange_WarpFlagsStayInLockstepWithFrames()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 120.0);
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
                Pid, Pt(125.0, "Mun"), Pt(125.0, "Kerbin")));
            Assert.Equal(4, bg.GetTrackSectionsForTesting(Pid)[0].frames.Count);
            // The new section starts with no frames and no flags.
            Assert.Equal(0, bg.GetSectionFrameWarpFlagCountForTesting(Pid));
            bg.AppendLoadedSampleForTesting(Pid, Pt(125.0, "Mun"));
            Assert.Equal(1, bg.GetSectionFrameWarpFlagCountForTesting(Pid));
            Assert.Single(bg.GetCurrentTrackSectionForTesting(Pid).Value.frames);
        }

        [Fact]
        public void BodyChange_OnABurn_IsWhatTheOptimizerBodyRuleSplits()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 140.0);
            TrajectoryPoint munSample = Pt(147.5, "Mun");
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, munSample, Pt(147.5, "Kerbin")));
            bg.AppendLoadedSampleForTesting(Pid, munSample);
            bg.AppendLoadedSampleForTesting(Pid, Pt(157.5, "Mun"));
            bg.CloseCurrentTrackSectionForTesting(Pid, 157.5);

            var rec = new Recording
            {
                RecordingId = "bg-physics-soi-burn",
                TrackSections = new List<TrackSection>(bg.GetTrackSectionsForTesting(Pid)),
                Points = new List<TrajectoryPoint>(Rec.Points),
            };
            Assert.True(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out var reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.BodyChange, reason);
        }

        [Fact]
        public void BodyChange_OnACoast_StaysCohesive()
        {
            StartLoaded(SegmentEnvironment.ExoBallistic);
            FlyOn("Kerbin", 100.0, 110.0);
            TrajectoryPoint munSample = Pt(115.0, "Mun");
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, munSample, null));
            bg.AppendLoadedSampleForTesting(Pid, munSample);
            bg.AppendLoadedSampleForTesting(Pid, Pt(125.0, "Mun"));
            bg.CloseCurrentTrackSectionForTesting(Pid, 125.0);

            var rec = new Recording
            {
                RecordingId = "bg-physics-soi-coast",
                TrackSections = new List<TrackSection>(bg.GetTrackSectionsForTesting(Pid)),
            };
            Assert.False(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out var reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange, reason);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]") && l.Contains("closingSample=0"));
        }

        [Fact]
        public void SameBody_DoesNotSplit()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 120.0);
            Assert.False(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, Pt(125.0, "Kerbin"), null));
            Assert.Empty(bg.GetTrackSectionsForTesting(Pid));
        }

        [Fact]
        public void EmptySection_DoesNotSplit_TheSampleDefinesItsBody()
        {
            StartLoaded(SegmentEnvironment.ExoBallistic);
            Assert.False(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, Pt(100.5, "Mun"), null));
            Assert.Empty(bg.GetTrackSectionsForTesting(Pid));
        }

        [Fact]
        public void ClosingPointOnTheWrongBodyOrUT_IsNotCommitted()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 120.0);
            int before = Rec.Points.Count;
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
                Pid, Pt(125.0, "Mun"), Pt(124.0, "Kerbin")));
            Assert.Equal(before, Rec.Points.Count);
            Assert.Equal(120.0, bg.GetTrackSectionsForTesting(Pid)[0].frames.Last().ut);
        }

        [Fact]
        public void ClosingPointOnTheNewBody_IsNotCommitted()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 120.0);
            int before = Rec.Points.Count;
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
                Pid, Pt(125.0, "Mun"), Pt(125.0, "Mun")));
            Assert.Equal(before, Rec.Points.Count);
            Assert.All(bg.GetTrackSectionsForTesting(Pid)[0].frames, f => Assert.Equal("Kerbin", f.bodyName));
        }

        [Fact]
        public void ClosingPointRejectedByTheFlatList_StaysOutOfTheSection()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            FlyOn("Kerbin", 100.0, 120.0);
            // A later flat point (an out-of-band append) makes the closing sample non-monotonic.
            Rec.Points.Add(Pt(130.0, "Kerbin"));
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
                Pid, Pt(125.0, "Mun"), Pt(125.0, "Kerbin")));
            Assert.Equal(120.0, bg.GetTrackSectionsForTesting(Pid)[0].frames.Last().ut);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]") && l.Contains("closingSample=0"));
        }

        [Fact]
        public void RelativeSection_SplitKeepsFrameAndAnchor_AndSkipsTheClosingSample()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Relative, "anchor-rec-1");
            bg.InjectCurrentTrackSectionFrameForTesting(Pid, Pt(100.0, "Kerbin", lat: 3.0, lon: 4.0, alt: 5.0));
            bg.InjectCurrentTrackSectionFrameForTesting(Pid, Pt(110.0, "Kerbin", lat: 3.5, lon: 4.5, alt: 5.5));

            int before = Rec.Points.Count;
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(
                Pid, Pt(115.0, "Mun"), Pt(115.0, "Kerbin")));
            Assert.Equal(before, Rec.Points.Count);

            TrackSection next = bg.GetCurrentTrackSectionForTesting(Pid).Value;
            Assert.Equal(ReferenceFrame.Relative, next.referenceFrame);
            Assert.Equal("anchor-rec-1", next.anchorRecordingId);
            Assert.Equal(0u, next.anchorVesselId);
            Assert.NotNull(next.bodyFixedFrames);
            Assert.Equal(115.0, next.startUT);
            Assert.Equal(110.0, bg.GetTrackSectionsForTesting(Pid)[0].frames.Last().ut);
        }

        [Fact]
        public void ArmedSample_IsLoggedWithPid_AndClearedByTheSplit()
        {
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            bg.ArmLoadedPhysicsSoiSampleForTesting(Pid, "Kerbin", "Mun", 147.0);
            Assert.True(bg.GetLoadedPhysicsSoiSamplePendingForTesting(Pid));
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("SOI changed off rails for background vessel")
                && l.Contains("pid=" + Pid) && l.Contains("Kerbin -> Mun") && l.Contains("UT=147.00"));

            FlyOn("Kerbin", 100.0, 140.0);
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, Pt(147.5, "Mun"), null));
            Assert.False(bg.GetLoadedPhysicsSoiSamplePendingForTesting(Pid));
        }

        [Fact]
        public void ActiveAndBackgroundSplits_ProduceTheSameSections()
        {
            // The same crossing through both recorders: same boundaries, frames and bodies.
            var fg = new FlightRecorder();
            fg.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, 100.0);
            StartLoaded(SegmentEnvironment.ExoPropulsive);
            for (double ut = 100.0; ut <= 140.0; ut += 10.0)
            {
                fg.CommitRecordedPointForTesting(Pt(ut, "Kerbin", alt: 2.4e6 + ut));
                bg.AppendLoadedSampleForTesting(Pid, Pt(ut, "Kerbin", alt: 2.4e6 + ut));
            }
            TrajectoryPoint mun = Pt(147.5, "Mun", lat: 5.0);
            TrajectoryPoint closing = Pt(147.5, "Kerbin", lat: 1.5, alt: 1.2e7);
            Assert.True(fg.TrySplitSectionAtPhysicsBodyChange(mun, closing));
            Assert.True(bg.TrySplitLoadedSectionAtPhysicsBodyChangeForTesting(Pid, mun, closing));
            fg.CommitRecordedPointForTesting(mun);
            bg.AppendLoadedSampleForTesting(Pid, mun);
            fg.CommitRecordedPointForTesting(Pt(157.5, "Mun", lat: 5.5));
            bg.AppendLoadedSampleForTesting(Pid, Pt(157.5, "Mun", lat: 5.5));
            fg.CloseCurrentTrackSection(157.5);
            bg.CloseCurrentTrackSectionForTesting(Pid, 157.5);

            List<TrackSection> a = fg.TrackSections;
            List<TrackSection> b = bg.GetTrackSectionsForTesting(Pid);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].startUT, b[i].startUT);
                Assert.Equal(a[i].endUT, b[i].endUT);
                Assert.Equal(a[i].environment, b[i].environment);
                Assert.Equal(a[i].referenceFrame, b[i].referenceFrame);
                Assert.Equal(a[i].frames.Select(f => f.bodyName + "@" + f.ut),
                    b[i].frames.Select(f => f.bodyName + "@" + f.ut));
            }
        }

        // ---- production wiring (the live handlers need a Vessel) ----

        [Fact]
        public void OnBackgroundPhysicsFrame_ChecksTheBodyBeforeTheRelativeRewriteAndTheAppend()
        {
            string body = MethodBody("BackgroundRecorder.cs", "public void OnBackgroundPhysicsFrame(Vessel bgVessel)");
            int packed = body.IndexOf("if (bgVessel.packed)", StringComparison.Ordinal);
            int gate = body.IndexOf("!state.physicsSoiSamplePending", StringComparison.Ordinal);
            int split = body.IndexOf("SplitBackgroundSectionIfPhysicsBodyChanged(state, treeRec, bgVessel, point)", StringComparison.Ordinal);
            int relative = body.IndexOf("ApplyBackgroundRelativeOffset(state, ref point, bgVessel, ut)", StringComparison.Ordinal);
            int append = body.IndexOf("treeRec.Points.Add(point)", StringComparison.Ordinal);
            Assert.True(split >= 0, "OnBackgroundPhysicsFrame no longer runs SplitBackgroundSectionIfPhysicsBodyChanged");
            Assert.True(gate >= 0, "the adaptive-sampling skip no longer honours the armed SOI sample");
            Assert.True(packed >= 0 && packed < gate && gate < split,
                "the SOI sample must stay below the packed (on-rails) early-return");
            Assert.True(split < relative && relative < append,
                "the body check must see the body-fixed sample, before the Relative rewrite and the append");
        }

        [Fact]
        public void OnBackgroundVesselSOIChanged_ArmsLoadedStates_AfterTheOnRailsHalf()
        {
            string body = MethodBody("BackgroundRecorder.cs",
                "public void OnBackgroundVesselSOIChanged(Vessel v, CelestialBody fromBody)");
            int onRails = body.IndexOf("onRailsStates.TryGetValue(pid, out state)", StringComparison.Ordinal);
            int loaded = body.IndexOf("loadedStates.TryGetValue(pid, out loadedState)", StringComparison.Ordinal);
            int arm = body.IndexOf("ArmBackgroundPhysicsSoiSample(loadedState", StringComparison.Ordinal);
            Assert.True(onRails >= 0 && loaded > onRails && arm > loaded,
                "OnBackgroundVesselSOIChanged must arm the loaded state's sample after the on-rails half");
        }

        [Fact]
        public void OnRailsState_CarriesNoSectionOrSoiSampleState()
        {
            // The eccentric-orbit invariant (CLAUDE.md, on-rails BG vessels): the fix lives
            // on the loaded state only.
            Type onRails = typeof(BackgroundRecorder).GetNestedType("BackgroundOnRailsState", BindingFlags.NonPublic);
            Assert.NotNull(onRails);
            string[] names = onRails.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).ToArray();
            Assert.DoesNotContain("physicsSoiSamplePending", names);
            Assert.DoesNotContain("currentTrackSection", names);
            Assert.DoesNotContain("environmentHysteresis", names);

            Type loadedType = typeof(BackgroundRecorder).GetNestedType("BackgroundVesselState", BindingFlags.NonPublic);
            Assert.NotNull(loadedType.GetField("physicsSoiSamplePending", BindingFlags.Public | BindingFlags.Instance));
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
