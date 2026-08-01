using System;
using System.Collections.Generic;
using System.Linq;
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
        public void Snapshot_ManeuverTransition_IsFlaggedAsUiNodeBoundary()
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
            Assert.True(result.EncounteredManeuverNode);
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
            Assert.False(result.EncounteredManeuverNode);
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
            // The first hit per (subsystem, vessel-name) key always emits at WARN
            // level; rate-limiting only suppresses subsequent hits within the
            // 30-second interval (covered by
            // <see cref="Snapshot_NullSolver_RateLimitsRepeatsForSameVessel"/>).
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                line.Contains("solver unavailable"));
        }

        /// <summary>
        /// Regression for #576: 146 `solver unavailable` WARNs were emitted in the
        /// 2026-04-25 marker-validator-fix playtest, clustered as 77×Kerbal X
        /// Debris + 45×Ermore Kerman + 12×Magdo Kerman + 11×Kerbal X Probe +
        /// 1×Kerbal X. All but the lone "Kerbal X" hit were vessels whose
        /// `patchedConicSolver` is null by design in stock KSP (debris,
        /// EVA-kerbals, probe-debris that has lost active-vessel solver state).
        /// Per-vessel rate-limiting collapses the floor to one WARN per vessel
        /// per 30-second window with a `suppressed=N` suffix on the next
        /// emission, while still surfacing the first occurrence of a NEW
        /// vessel name immediately so a fresh regression on a piloted craft
        /// mid-flight cannot be silently absorbed by an unrelated debris
        /// floor.
        /// </summary>
        [Fact]
        public void Snapshot_NullSolver_RateLimitsRepeatsForSameVessel()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;

            // Ten consecutive null-solver snapshots for the same vessel within a
            // 1-second window must produce exactly ONE WARN line — not 10.
            for (int i = 0; i < 10; i++)
            {
                clockSeconds += 0.1;
                PatchedConicSnapshot.SnapshotPatchedConicChain(
                    source: null,
                    snapshotUT: 100 + i,
                    captureLimit: 8,
                    vesselName: "Kerbal X Debris");
            }

            int firstVesselWarnCount = logLines.Count(l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable"));
            Assert.Equal(1, firstVesselWarnCount);

            // A different vessel name has its own key — its first hit must
            // emit immediately rather than being suppressed by the prior
            // vessel's floor.
            clockSeconds += 0.1;
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null,
                snapshotUT: 200,
                captureLimit: 8,
                vesselName: "Ermore Kerman");

            int secondVesselWarnCount = logLines.Count(l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Ermore Kerman solver unavailable"));
            Assert.Equal(1, secondVesselWarnCount);

            // After the 30-second rate-limit window expires, the same vessel
            // emits its next WARN with a `suppressed=N` suffix attributing the
            // intervening absorbed hits.
            clockSeconds += 30.5;
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null,
                snapshotUT: 300,
                captureLimit: 8,
                vesselName: "Kerbal X Debris");

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable") &&
                l.Contains("suppressed=9"));
        }

        /// <summary>
        /// Regression for PR #553 P2 review: the WarnRateLimited call must use the
        /// documented 30-second window, not WarnRateLimited's 5-second default.
        /// Walking the test clock forward 10 seconds (well past 5 s, well below
        /// 30 s) and emitting a fresh hit must produce NO new WARN line — under
        /// the 5-second default this would have re-emitted, so the assertion
        /// catches a regression to the implicit interval.
        /// </summary>
        [Fact]
        public void Snapshot_NullSolver_BetweenFiveAndThirtySeconds_RemainsSuppressed()
        {
            double clockSeconds = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clockSeconds;

            // Seed: first hit always emits.
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null, snapshotUT: 100, captureLimit: 8, vesselName: "Kerbal X Debris");

            int firstWarn = logLines.Count(l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable"));
            Assert.Equal(1, firstWarn);

            // Advance 10 seconds — past the 5 s default but well under the 30 s
            // configured interval. A pre-fix build would have re-emitted here.
            clockSeconds += 10.0;
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null, snapshotUT: 110, captureLimit: 8, vesselName: "Kerbal X Debris");

            int afterTenSecondsWarn = logLines.Count(l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable"));
            Assert.Equal(1, afterTenSecondsWarn);

            // Advance to 25 seconds total — still inside the 30 s window. Same
            // assertion: still exactly one WARN, the rate limiter must not have
            // re-emitted.
            clockSeconds += 15.0;
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null, snapshotUT: 125, captureLimit: 8, vesselName: "Kerbal X Debris");

            int afterTwentyFiveSecondsWarn = logLines.Count(l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable"));
            Assert.Equal(1, afterTwentyFiveSecondsWarn);

            // Cross 30 s — re-emit with the suppressed=N suffix.
            clockSeconds += 6.0; // total 31 s since seed
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source: null, snapshotUT: 200, captureLimit: 8, vesselName: "Kerbal X Debris");

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                l.Contains("vessel=Kerbal X Debris solver unavailable") &&
                l.Contains("suppressed=2"));
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
                line.Contains("missing-reference-body") &&
                line.Contains("aborting predicted snapshot capture"));
        }

        /// <summary>
        /// AN ORBITAL EVA RECORDS NOTHING (2026-07-30). Stock's own patched-conic
        /// solver can produce a Not-a-Number patch - on the orbital EVA kerbal it
        /// logged "dT is NaN! tA: NaN, E: NaN, M: NaN, T: NaN" and "CheckEncounter:
        /// failed to find any intercepts at all" while doing it - and this snapshot
        /// copied every field verbatim into the recording's predicted segments.
        /// Downstream, a NaN eccentricity fails the <c>&lt; 1.0</c> elliptic test, so
        /// the extrapolator classified it hyperbolic and threw ArithmeticException out
        /// of <c>Math.Sign(NaN)</c> on every physics frame. Refuse the patch here so
        /// the NaN never enters a recording at all.
        /// </summary>
        [Fact]
        public void Snapshot_NonFinitePatchElements_FailsWithoutCapturingTheNaN()
        {
            var nanPatch = MakePatch(100, 200);
            nanPatch.Eccentricity = double.NaN;
            nanPatch.MeanAnomalyAtEpoch = double.NaN;

            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = nanPatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 8, "Orbital EVA Kerbal");

            Assert.Equal(PatchedConicSnapshotFailureReason.NonFinitePatchElements, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Equal(0, result.CapturedPatchCount);
            Assert.Equal(4, source.PatchLimit);

            // The refusal has to NAME the offending input, or the log says a patch was
            // bad without saying which number was bad.
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                line.Contains("Orbital EVA Kerbal") &&
                line.Contains("has non-finite elements") &&
                line.Contains("ecc=NaN") &&
                line.Contains("mEp=NaN") &&
                line.Contains("aborting predicted snapshot capture"));
        }

        /// <summary>
        /// A NaN <c>EndUT</c> is its own route to the same throw and is invisible to
        /// <c>ToOrbitSegment</c>'s <c>patch.EndUT &lt; startUT</c> clamp, because every
        /// comparison against NaN is false. It matters because the finalizer propagates
        /// the terminal predicted segment AT <c>segment.endUT</c>: a well-formed
        /// hyperbolic tail evaluated at a NaN UT reaches <c>Math.Sign(NaN)</c> just as
        /// surely as a NaN element does.
        /// </summary>
        [Fact]
        public void Snapshot_NonFinitePatchEndUT_IsRefusedRatherThanClamped()
        {
            var nanEndPatch = MakePatch(100, double.NaN);

            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = nanEndPatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 100, 8, "Nan End Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.NonFinitePatchElements, result.FailureReason);
            Assert.Empty(result.Segments);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][PatchedSnapshot]") &&
                line.Contains("endUT=NaN"));
        }

        /// <summary>
        /// Same partial-keep policy as the null-body case below: earlier patches in the
        /// chain are real orbits worth recording, so a degenerate patch at index &gt; 0
        /// truncates rather than discarding everything.
        /// </summary>
        [Fact]
        public void Snapshot_NonFinitePatchAfterValidPrefix_KeepsPartialResult()
        {
            var nanPatch = MakePatch(200, 320, body: "Mun");
            nanPatch.SemiMajorAxis = double.PositiveInfinity;
            var firstPatch = MakePatch(100, 200, body: "Kerbin",
                transition: PatchedConicTransitionType.Encounter);
            firstPatch.NextPatch = nanPatch;

            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = firstPatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120, 8, "Partial Nan Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.NonFinitePatchElements, result.FailureReason);
            Assert.Single(result.Segments);
            Assert.Equal("Kerbin", result.Segments[0].bodyName);
            Assert.Equal(1, result.CapturedPatchCount);
            Assert.True(result.HasTruncatedTail);
            Assert.All(result.Segments, seg =>
            {
                Assert.False(double.IsNaN(seg.semiMajorAxis) || double.IsInfinity(seg.semiMajorAxis));
                Assert.False(double.IsNaN(seg.endUT) || double.IsInfinity(seg.endUT));
            });
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][PatchedSnapshot]") &&
                line.Contains("has non-finite elements") &&
                line.Contains("truncated chain after 1 valid patch(es), keeping partial result"));
            Assert.DoesNotContain(logLines, line =>
                line.Contains("aborting predicted snapshot capture"));
        }

        [Fact]
        public void HasFinitePatchElements_AcceptsAWellFormedPatchAndRejectsEachNonFiniteField()
        {
            Assert.True(PatchedConicSnapshot.HasFinitePatchElements(MakePatch(100, 200)));
            Assert.False(PatchedConicSnapshot.HasFinitePatchElements(null));

            foreach (double poison in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                AssertFieldRejected(p => p.StartUT = poison, "StartUT", poison);
                AssertFieldRejected(p => p.EndUT = poison, "EndUT", poison);
                AssertFieldRejected(p => p.Inclination = poison, "Inclination", poison);
                AssertFieldRejected(p => p.Eccentricity = poison, "Eccentricity", poison);
                AssertFieldRejected(p => p.SemiMajorAxis = poison, "SemiMajorAxis", poison);
                AssertFieldRejected(p => p.LongitudeOfAscendingNode = poison, "LongitudeOfAscendingNode", poison);
                AssertFieldRejected(p => p.ArgumentOfPeriapsis = poison, "ArgumentOfPeriapsis", poison);
                AssertFieldRejected(p => p.MeanAnomalyAtEpoch = poison, "MeanAnomalyAtEpoch", poison);
                AssertFieldRejected(p => p.Epoch = poison, "Epoch", poison);
            }
        }

        private static void AssertFieldRejected(
            Action<FakePatchedConicOrbitPatch> poisonField,
            string fieldName,
            double poison)
        {
            FakePatchedConicOrbitPatch patch = MakePatch(100, 200);
            poisonField(patch);
            Assert.False(
                PatchedConicSnapshot.HasFinitePatchElements(patch),
                $"{fieldName}={poison.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} must be refused");
        }

        /// <summary>
        /// Regression for #575: when patch 0 is valid but a later patch has a
        /// null body, the captured prefix must be preserved instead of the
        /// whole result being reset. The 2026-04-25_1314 marker-validator-fix
        /// playtest emitted this pattern 153× — every entry had
        /// <c>patchIndex=1</c>, never 0 — so the previous discard-everything
        /// policy threw away every valid first patch and starved the
        /// recording's predicted tail. With partial preservation the
        /// downstream <see cref="IncompleteBallisticSceneExitFinalizer"/>
        /// applies the captured segment via its existing
        /// <c>snapshot.Segments.Count &gt; 0</c> branch and skips the
        /// transient-ascent bail-out (which gates on
        /// <c>appendedSegments.Count == 0</c>).
        /// </summary>
        [Fact]
        public void Snapshot_MissingPatchBodyAfterValidPrefix_KeepsPartialResult()
        {
            var nullBodyPatch = MakePatch(200, 320, body: null,
                transition: PatchedConicTransitionType.Encounter);
            var firstPatch = MakePatch(100, 200, body: "Kerbin",
                transition: PatchedConicTransitionType.Encounter);
            firstPatch.NextPatch = nullBodyPatch;

            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = firstPatch
            };

            PatchedConicSnapshotResult result = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120, 8, "Truncated Ascent Vessel");

            Assert.Equal(PatchedConicSnapshotFailureReason.MissingPatchBody, result.FailureReason);
            Assert.Single(result.Segments);
            Assert.Equal("Kerbin", result.Segments[0].bodyName);
            Assert.Equal(1, result.CapturedPatchCount);
            Assert.True(result.HasTruncatedTail);
            Assert.Equal("Kerbin", result.LastCapturedBodyName);
            Assert.Equal(4, source.PatchLimit);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][PatchedSnapshot]") &&
                line.Contains("truncated chain after 1 valid patch(es), keeping partial result"));
            Assert.DoesNotContain(logLines, line =>
                line.Contains("aborting predicted snapshot capture"));
        }

        [Fact]
        public void Snapshot_MissingPatchBodyAfterValidPrefix_StableRepeatsLogOnce()
        {
            var nullBodyPatch = MakePatch(200, 320, body: null,
                transition: PatchedConicTransitionType.Encounter);
            var firstPatch = MakePatch(100, 200, body: "Kerbin",
                transition: PatchedConicTransitionType.Encounter);
            firstPatch.NextPatch = nullBodyPatch;

            var source = new FakePatchedConicSnapshotSource(4)
            {
                RootPatch = firstPatch
            };

            for (int i = 0; i < 5; i++)
            {
                PatchedConicSnapshot.SnapshotPatchedConicChain(
                    source, 120 + i, 8, "Stable Truncated Vessel");
            }

            Assert.Equal(1, logLines.Count(line =>
                line.Contains("[Parsek][VERBOSE][PatchedSnapshot]") &&
                line.Contains("truncated chain after 1 valid patch(es), keeping partial result")));
            Assert.Equal(1, logLines.Count(line =>
                line.Contains("[Parsek][VERBOSE][PatchedSnapshot]") &&
                line.Contains("captured=1") &&
                line.Contains("hasTruncatedTail=True")));

            nullBodyPatch.BodyName = "Mun";
            PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 130, 8, "Stable Truncated Vessel");

            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][VERBOSE][PatchedSnapshot]") &&
                line.Contains("captured=2") &&
                line.Contains("lastBody=Mun") &&
                line.Contains("suppressed=4"));
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
                line.Contains("encounteredManeuverNode=False"));
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
