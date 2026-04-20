using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PatchedConicSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PatchedConicSnapshotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void Snapshot_SinglePatch_CapturesOnePredictedSegment()
        {
            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = MakePatch(100, 220)
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 150, 8, "Snapshot Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.None, result.FailureReason);
            Assert.Single(result.Segments);
            Assert.Equal(150, result.Segments[0].startUT);
            Assert.Equal(220, result.Segments[0].endUT);
            Assert.True(result.Segments[0].isPredicted);
            Assert.Equal(2, result.OriginalPatchLimit);
            Assert.Equal(8, result.AppliedPatchLimit);
            Assert.Equal(2, source.PatchLimit);
        }

        [Fact]
        public void Snapshot_FlybyPredicted_CapturesPreAndPostFlybyPatches()
        {
            var postFlyby = MakePatch(200, 320, body: "Mun");
            var preFlyby = MakePatch(100, 200, body: "Kerbin", transition: PatchedConicTransitionType.Encounter);
            preFlyby.NextPatch = postFlyby;

            var source = new FakePatchedConicSnapshotSource(3)
            {
                RootPatch = preFlyby
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120, 8, "Flyby Vessel");

            Assert.Equal(2, result.Segments.Count);
            Assert.Equal("Kerbin", result.Segments[0].bodyName);
            Assert.Equal("Mun", result.Segments[1].bodyName);
            Assert.All(result.Segments, seg => Assert.True(seg.isPredicted));
        }

        [Fact]
        public void Snapshot_StopsAtManeuverTransition()
        {
            var coastToNodePatch = MakePatch(100, 200, transition: PatchedConicTransitionType.Maneuver);
            coastToNodePatch.NextPatch = MakePatch(200, 260);

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = coastToNodePatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120, 8, "Maneuver Vessel");

            Assert.Single(result.Segments);
            Assert.Equal(120, result.Segments[0].startUT);
            Assert.Equal(200, result.Segments[0].endUT);
            Assert.True(result.StoppedBeforeManeuver);
            Assert.True(result.HasTruncatedTail);
        }

        [Fact]
        public void Snapshot_CaptureLimitReached_ReportsTruncatedTail()
        {
            var thirdPatch = MakePatch(300, 400, body: "Mun");
            var secondPatch = MakePatch(200, 300, body: "Kerbin", transition: PatchedConicTransitionType.Encounter);
            secondPatch.NextPatch = thirdPatch;
            var firstPatch = MakePatch(100, 200);
            firstPatch.NextPatch = secondPatch;

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = firstPatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 2, "Truncated Vessel");

            Assert.Equal(2, result.CapturedPatchCount);
            Assert.Equal(2, result.Segments.Count);
            Assert.False(result.StoppedBeforeManeuver);
            Assert.True(result.HasTruncatedTail);
        }

        [Fact]
        public void Snapshot_NullSolver_ReturnsEmptyList()
        {
            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null,
                snapshotUT: 100,
                captureLimit: 8,
                vesselName: "Null Solver");

            Assert.Equal(PatchedConicSnapshotFailureReason.NullSolver, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Contains(logLines, line =>
                line.Contains("[PatchedSnapshot]") &&
                line.Contains("solver unavailable"));
        }

        [Fact]
        public void Snapshot_RestoresLimitOnUpdateException()
        {
            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = MakePatch(100, 200),
                ThrowOnUpdate = true
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 8, "Broken Solver");

            Assert.Equal(PatchedConicSnapshotFailureReason.UpdateFailed, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Equal(4, source.PatchLimit);
            Assert.Contains(logLines, line =>
                line.Contains("[PatchedSnapshot]") &&
                line.Contains("Update() failed"));
        }

        [Fact]
        public void Snapshot_PatchLimitUnavailable_FailsInsteadOfSilentlyProceeding()
        {
            var source = new FakePatchedConicSnapshotSource(4)
            {
                HasPatchLimitAccess = false,
                RootPatch = MakePatch(100, 200)
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 8, "No Reflection Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.PatchLimitUnavailable, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Equal(4, source.PatchLimit);
            Assert.Contains(logLines, line =>
                line.Contains("[PatchedSnapshot]") &&
                line.Contains("patchLimit reflection unavailable"));
        }

        [Fact]
        public void Snapshot_MissingPatchBody_FailsWithoutKerbinFallback()
        {
            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = MakePatch(100, 200, body: null)
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 8, "Null Body Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.MissingPatchBody, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Equal(4, source.PatchLimit);
            Assert.Null(result.LastCapturedBodyName);
            Assert.Contains(logLines, line =>
                line.Contains("[PatchedSnapshot]") &&
                line.Contains("missing-reference-body"));
        }

        [Fact]
        public void Snapshot_LogsPatchCount_AtWalkComplete()
        {
            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = MakePatch(100, 200)
            };

            PatchedConicSnapshot.SnapshotPatchedConicChain(source, 100, 8, "Log Vessel");

            Assert.Contains(logLines, line =>
                line.Contains("[PatchedSnapshot]") &&
                line.Contains("captured=1") &&
                line.Contains("hasTruncatedTail=False") &&
                line.Contains("stoppedBeforeManeuver=False"));
        }

        private static FakePatchedConicOrbitPatch MakePatch(
            double startUT,
            double endUT,
            string body = "Kerbin",
            PatchedConicTransitionType transition = PatchedConicTransitionType.Final)
        {
            return new FakePatchedConicOrbitPatch
            {
                StartUT = startUT,
                EndUT = endUT,
                BodyName = body,
                Inclination = 28.5,
                Eccentricity = 0.01,
                SemiMajorAxis = 700000,
                LongitudeOfAscendingNode = 90,
                ArgumentOfPeriapsis = 45,
                MeanAnomalyAtEpoch = 0.2,
                Epoch = startUT,
                EndTransition = transition
            };
        }

        private sealed class FakePatchedConicSnapshotSource : IPatchedConicSnapshotSource
        {
            private int patchLimit;

            public FakePatchedConicSnapshotSource(int initialPatchLimit)
            {
                patchLimit = initialPatchLimit;
            }

            public string VesselName => "Fake Vessel";
            public bool IsAvailable { get; set; } = true;
            public bool HasPatchLimitAccess { get; set; } = true;
            public bool ThrowOnUpdate { get; set; }
            public IPatchedConicOrbitPatch RootPatch { get; set; }

            public int PatchLimit
            {
                get => patchLimit;
                set => patchLimit = value;
            }

            public void Update()
            {
                if (ThrowOnUpdate)
                    throw new InvalidOperationException("boom");
            }
        }

        private sealed class FakePatchedConicOrbitPatch : IPatchedConicOrbitPatch
        {
            public double StartUT { get; set; }
            public double EndUT { get; set; }
            public double Inclination { get; set; }
            public double Eccentricity { get; set; }
            public double SemiMajorAxis { get; set; }
            public double LongitudeOfAscendingNode { get; set; }
            public double ArgumentOfPeriapsis { get; set; }
            public double MeanAnomalyAtEpoch { get; set; }
            public double Epoch { get; set; }
            public string BodyName { get; set; }
            public PatchedConicTransitionType EndTransition { get; set; }
            public IPatchedConicOrbitPatch NextPatch { get; set; }
        }
    }
}
