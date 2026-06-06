using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Log-hygiene regression tests for the ghost-playback per-cycle Verbose lines
    /// that flooded KSP.log at high time warp. Each site was converted from a pure
    /// <see cref="ParsekLog.Verbose"/> to <see cref="ParsekLog.VerboseRateLimited"/>
    /// with a STABLE per-recording / per-ghost / per-index key.
    /// <para>
    /// The headline guard is the #1063 trap: a value that advances every frame at
    /// warp (cycle index, UT, spawn-seed id, frame count) must NEVER be part of the
    /// rate-limit KEY — only the message BODY. If a warp-advancing value leaks into
    /// the key, the rate limiter creates a brand-new bucket every frame and emits on
    /// every call, defeating the throttle. These tests hold the wall-clock fixed and
    /// vary the UT inside the message: exactly one line must emit.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class GhostPlaybackLogHygieneTests : IDisposable
    {
        private readonly List<string> lines = new List<string>();
        private double clock;

        public GhostPlaybackLogHygieneTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // -------------------------------------------------------------------
        // #1063 regression guard — driven through the real production helper
        // LogPendingPlaybackInterpolationResolved (via the internal entry
        // point TryResolvePendingPlaybackInterpolation). The key is built from
        // (recId, source); the UT lives only in the message body. 50 calls with
        // the SAME recording but a DIFFERENT playbackUT each call, clock held
        // fixed, must emit exactly ONE line. Then advancing the clock past the
        // 2 s interval must emit a second line carrying suppressed=.
        // -------------------------------------------------------------------

        [Fact]
        public void PendingPlaybackInterpolation_SameKeyDifferentUT_EmitsOnceUntilIntervalElapses()
        {
            Recording rec = MakeTwoPointRecording("hygiene-1063");

            // Fire the resolved-path helper 50 times: same recording (same recId +
            // same "point interpolation" source = same key), DIFFERENT UT each call.
            // Keep every UT strictly between the two points (100..300) so the resolver
            // stays on the SAME "point interpolation" source branch — that isolates the
            // test to the warp-advancing UT, the exact #1063 hazard.
            for (int i = 0; i < 50; i++)
            {
                double playbackUT = 110.0 + i; // 110..159, advances every call, mimics warp
                bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                    rec, playbackUT, out _);
                Assert.True(resolved);
            }

            var emitted = PendingPlaybackLines();

            // Exactly one line proves the warp-advancing UT is in the BODY, not the KEY.
            Assert.Single(emitted);
            Assert.Contains("resolved from", emitted[0]);
            Assert.DoesNotContain("suppressed=", emitted[0]);

            // Advance the wall clock past the 2 s window: the next call re-opens the
            // gate and surfaces the suppressed counter (the 49 throttled calls). Use a
            // UT in-range so the source (and therefore the key) is unchanged.
            clock += 2.5;
            bool resolvedAgain = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                rec, 175.0, out _);
            Assert.True(resolvedAgain);

            emitted = PendingPlaybackLines();
            Assert.Equal(2, emitted.Count);
            Assert.Contains("suppressed=", emitted[1]);
        }

        [Fact]
        public void PendingPlaybackInterpolation_DifferentRecordings_TrackedIndependently()
        {
            Recording recA = MakeTwoPointRecording("hygiene-recA");
            Recording recB = MakeTwoPointRecording("hygiene-recB");

            for (int i = 0; i < 10; i++)
            {
                GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(recA, 150.0 + i, out _);
                GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(recB, 150.0 + i, out _);
            }

            // Two recordings = two distinct keys = one emit each (A does not suppress B).
            Assert.Equal(2, PendingPlaybackLines().Count);
        }

        [Fact]
        public void PendingPlaybackInterpolation_Unresolved_SeparateKeyFromResolved()
        {
            // Null trajectory drives the UNRESOLVED helper (separate key prefix
            // "pending-playback-interp-unres-"). It must emit independently of any
            // resolved-path traffic so a persistently-failing ghost stays visible.
            for (int i = 0; i < 20; i++)
            {
                bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                    null, 150.0 + i, out _);
                Assert.False(resolved);
            }

            var unresolved = lines
                .Where(l => l.Contains("[Engine]") && l.Contains("unresolved"))
                .ToList();
            Assert.Single(unresolved);
        }

        // -------------------------------------------------------------------
        // Direct-call structural guards for the highest-volume rate-limited
        // sites that are only reachable through Unity-heavy code (debris-skip,
        // overlap lifecycle / remove, defer-overlap-sweep). We exercise
        // VerboseRateLimited exactly the way each site does — stable key,
        // warp-advancing value only in the body — and assert the collapse.
        // -------------------------------------------------------------------

        [Fact]
        public void DebrisSkip_StableIndexKey_CollapsesToOneEmitPerWindow()
        {
            // Mirrors GhostMapPresence.cs HandleFlightGhostCreatedMapPresence debris
            // skip: key "skip-map-debris-{index}", 3 s window, cycle/UT in the body.
            int index = 7;
            for (int cycle = 0; cycle < 60; cycle++)
            {
                ParsekLog.VerboseRateLimited("Policy", $"skip-map-debris-{index}",
                    $"Skipped ghost map for #{index} \"Debris\" - debris (cycle={cycle})",
                    3.0);
                clock += 1.0 / 60.0; // ~1 s of wall clock total
            }

            int emitted = lines.Count(l =>
                l.Contains("[Policy]") && l.Contains("- debris"));
            Assert.Equal(1, emitted);
        }

        [Fact]
        public void OverlapLifecycle_StableRecIdxKey_CollapsesAndSurfacesSuppressed()
        {
            // Mirrors GhostMapPresence.cs EnsureOverlapInstances lifecycle line:
            // key "overlap-lifecycle-{recIdx}", 2 s window. currentUT advances per
            // frame and lives ONLY in the body. ~5 s of wall clock at 2 s window.
            int recIdx = 3;
            for (int frame = 0; frame < 300; frame++)
            {
                double currentUT = 5000.0 + frame; // warp-advancing body value
                ParsekLog.VerboseRateLimited("GhostMap", "overlap-lifecycle-" + recIdx,
                    $"Overlap per-instance lifecycle rec=#{recIdx} \"Loop\" created=1 currentUT={currentUT:F1}",
                    2.0);
                clock += 1.0 / 60.0;
            }

            var emitted = lines
                .Where(l => l.Contains("[GhostMap]") && l.Contains("Overlap per-instance lifecycle"))
                .ToList();

            // Small constant, not 300. And the suppressed counter is surfaced.
            Assert.InRange(emitted.Count, 1, 4);
            Assert.Contains(emitted, l => l.Contains("suppressed="));
        }

        [Fact]
        public void OverlapRemove_StableRecIdxKey_CycleInBodyNotKey()
        {
            // Mirrors GhostMapPresence.cs RemoveOverlapInstance: key
            // "overlap-remove-{recIdx}", 2 s window. The cycle id (warp-advancing)
            // must be in the body, NOT the key — so distinct cycles collapse.
            int recIdx = 2;
            for (long cycle = 0; cycle < 50; cycle++)
            {
                ParsekLog.VerboseRateLimited("GhostMap", "overlap-remove-" + recIdx,
                    $"Removed overlap per-instance map vessel rec=#{recIdx} cycle={cycle} reason=expired",
                    2.0);
                // clock held fixed: a single window
            }

            int emitted = lines.Count(l =>
                l.Contains("[GhostMap]") && l.Contains("Removed overlap per-instance map vessel"));
            Assert.Equal(1, emitted);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private List<string> PendingPlaybackLines()
        {
            return lines
                .Where(l => l.Contains("[Engine]") && l.Contains("Pending playback interpolation"))
                .ToList();
        }

        private static Recording MakeTwoPointRecording(string recordingId)
        {
            // Two points, no track sections, no orbit segments: the resolver lands on
            // the flat-point interpolation branch and reports source "point interpolation".
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Mun",
                        altitude = 200,
                        velocity = new Vector3(0, 10, 0)
                    },
                    new TrajectoryPoint
                    {
                        ut = 300,
                        bodyName = "Mun",
                        altitude = 250,
                        velocity = new Vector3(0, 15, 0)
                    }
                }
            };
        }
    }
}
