using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Integration tests covering the ghost-map source-resolve verbose line and
    /// the spawner suppression summary. Both fire from per-frame hot paths and
    /// previously stormed the log when the same decision repeated for the entire
    /// session.
    /// <para>
    /// GhostMap source-resolve routes through <see cref="ParsekLog.VerboseOnChange"/>
    /// so a stable <c>(source, reason)</c> tuple emits exactly once until the
    /// decision flips, with the suppressed count surfaced as <c>| suppressed=N</c>.
    /// </para>
    /// <para>
    /// Spawner suppression is now a single batched, rate-limited summary
    /// (per-reason histogram) emitted once after ComputePlaybackFlags' loop,
    /// replacing the former one-line-per-recording diagnostic that was the worst
    /// log-spam source (thousands of lines/second with a large committed list).
    /// <see cref="ParsekFlight.BuildSpawnSuppressedSummary"/> is the pure core.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class LogSpamRateLimitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LogSpamRateLimitTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -------------------------------------------------------------------
        // GhostMap source-resolve — the (recId, source, reason) tuple is
        // stable across per-frame repeats while a recording sits in the
        // pending-map-vessel queue. Confirm we emit once and stay quiet
        // until the decision flips.
        // -------------------------------------------------------------------

        [Fact]
        public void GhostMapSourceResolve_StablePendingDecision_EmitsOncePerOperation()
        {
            Recording rec = MakeBelowThresholdRecording("logspam-pending");

            // Drive 100 calls with the same operation/recording/decision tuple.
            for (int i = 0; i < 100; i++)
            {
                ResolveOnce(rec, currentUT: 200.0 + i, op: "map-presence-pending-create");
            }

            int verboseLines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("map-presence-pending-create:")
                && l.Contains("source=None")
                && l.Contains("reason=" + GhostMapPresence.TrackingStationGhostSkipStateVectorThreshold));
            int structuredLines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("source-resolve:")
                && l.Contains("source=None"));

            // Each call site emits exactly one line on first contact and then
            // stays silent for the rest of the stable streak.
            Assert.Equal(1, verboseLines);
            Assert.Equal(1, structuredLines);
        }

        [Fact]
        public void GhostMapSourceResolve_DifferentOperations_TrackedIndependently()
        {
            Recording rec = MakeBelowThresholdRecording("logspam-multi-op");

            // Both call sites in ParsekPlaybackPolicy hit the same recording
            // through different operationName strings; each must emit once.
            ResolveOnce(rec, currentUT: 200.0, op: "map-presence-initial-create");
            ResolveOnce(rec, currentUT: 200.5, op: "map-presence-initial-create");
            ResolveOnce(rec, currentUT: 201.0, op: "map-presence-pending-create");
            ResolveOnce(rec, currentUT: 201.5, op: "map-presence-pending-create");

            int initialLines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("map-presence-initial-create:"));
            int pendingLines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("map-presence-pending-create:"));

            Assert.Equal(1, initialLines);
            Assert.Equal(1, pendingLines);
        }

        [Fact]
        public void GhostMapSourceResolve_DifferentRecordings_TrackedIndependently()
        {
            Recording recA = MakeBelowThresholdRecording("logspam-rec-A");
            Recording recB = MakeBelowThresholdRecording("logspam-rec-B");

            for (int i = 0; i < 5; i++)
            {
                ResolveOnce(recA, currentUT: 200.0 + i, op: "map-presence-pending-create");
                ResolveOnce(recB, currentUT: 200.0 + i, op: "map-presence-pending-create");
            }

            int recALines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("map-presence-pending-create:")
                && l.Contains("rec=logspam-rec-A"));
            int recBLines = logLines.Count(l =>
                l.Contains("[GhostMap]")
                && l.Contains("map-presence-pending-create:")
                && l.Contains("rec=logspam-rec-B"));

            // Each recording is its own identity scope — A doesn't suppress B.
            Assert.Equal(1, recALines);
            Assert.Equal(1, recBLines);
        }

        [Fact]
        public void GhostMapSourceResolve_DecisionFlip_ReemitsWithSuppressedCount()
        {
            // First two calls hit the below-threshold (None|state-vector-threshold)
            // branch; the third uses a recording with a real orbit segment so
            // ResolveMapPresenceGhostSource returns Segment instead. The flip
            // must reopen the gate AND surface the suppressed counter.
            Recording belowThreshold = MakeBelowThresholdRecording("logspam-flip");
            for (int i = 0; i < 3; i++)
            {
                ResolveOnce(belowThreshold, currentUT: 200.0 + i, op: "map-presence-pending-create");
            }

            Recording withSegment = MakeOrbitSegmentRecording("logspam-flip");
            ResolveOnce(withSegment, currentUT: 250.0, op: "map-presence-pending-create");

            var pendingLines = logLines
                .Where(l => l.Contains("[GhostMap]") && l.Contains("map-presence-pending-create:"))
                .ToList();

            Assert.Equal(2, pendingLines.Count);
            Assert.Contains("source=None", pendingLines[0]);
            Assert.DoesNotContain("suppressed=", pendingLines[0]);
            Assert.Contains("source=Segment", pendingLines[1]);
            Assert.Contains("| suppressed=2", pendingLines[1]);
        }

        // -------------------------------------------------------------------
        // Spawner suppression summary: the pure histogram builder. Replaces
        // the former one-line-per-recording diagnostic. The summary is sorted
        // by descending count (ties broken by ordinal reason) so the line is
        // deterministic and the dominant reason leads.
        // -------------------------------------------------------------------

        [Fact]
        public void BuildSpawnSuppressedSummary_OrdersReasonsByDescendingCount()
        {
            var byReason = new Dictionary<string, int>
            {
                { "chain-suppressed", 3 },
                { "no vessel snapshot", 280 },
                { "non-leaf tree recording", 12 },
            };

            string summary = ParsekFlight.BuildSpawnSuppressedSummary(295, byReason);

            Assert.Equal(
                "Spawn suppressed: 295 recording(s) | "
                + "no vessel snapshot=280, non-leaf tree recording=12, chain-suppressed=3",
                summary);
        }

        [Fact]
        public void BuildSpawnSuppressedSummary_TieBrokenByOrdinalReason()
        {
            var byReason = new Dictionary<string, int>
            {
                { "bbb", 5 },
                { "aaa", 5 },
            };

            string summary = ParsekFlight.BuildSpawnSuppressedSummary(10, byReason);

            Assert.Equal("Spawn suppressed: 10 recording(s) | aaa=5, bbb=5", summary);
        }

        [Fact]
        public void BuildSpawnSuppressedSummary_NoReasons_TotalOnly()
        {
            Assert.Equal(
                "Spawn suppressed: 0 recording(s)",
                ParsekFlight.BuildSpawnSuppressedSummary(0, new Dictionary<string, int>()));
            Assert.Equal(
                "Spawn suppressed: 4 recording(s)",
                ParsekFlight.BuildSpawnSuppressedSummary(4, null));
        }

        // -------------------------------------------------------------------
        // Spawner summary: rate-limited shared key. A large committed list
        // all stuck on the same reason for many frames must collapse to a
        // handful of summary lines (one per rate-limit window), NOT one line
        // per recording per frame. This is the structural anti-spam guarantee.
        // -------------------------------------------------------------------

        [Fact]
        public void SpawnSuppressedSummary_ManyFramesLargeList_CollapsesViaRateLimit()
        {
            double clock = 0.0;
            ParsekLog.ClockOverrideForTesting = () => clock;

            // 300 frames, 286 recordings each frame all "no vessel snapshot",
            // advancing 1/60 s per frame (~5 s of wall-clock). Mirrors the
            // production call: one VerboseRateLimited summary after the loop.
            for (int frame = 0; frame < 300; frame++)
            {
                var byReason = new Dictionary<string, int> { { "no vessel snapshot", 286 } };
                ParsekLog.VerboseRateLimited(
                    "Spawner",
                    "spawn-suppressed-summary",
                    () => ParsekFlight.BuildSpawnSuppressedSummary(286, byReason),
                    2.0);
                clock += 1.0 / 60.0;
            }

            int summaryLines = logLines.Count(l =>
                l.Contains("[Spawner]") && l.Contains("Spawn suppressed:"));

            // ~5 s at a 2 s window: first emit + ~2 s + ~4 s = 3 lines.
            // The point is it is a small constant, not 300 * 286 = 85,800.
            Assert.InRange(summaryLines, 1, 4);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static GhostMapPresence.TrackingStationGhostSource ResolveOnce(
            Recording rec, double currentUT, string op)
        {
            int cachedStateVectorIndex = -1;
            return GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                isSuppressed: false,
                alreadyMaterialized: false,
                currentUT: currentUT,
                allowTerminalOrbitFallback: false,
                logOperationName: op,
                stateVectorCachedIndex: ref cachedStateVectorIndex,
                segment: out _,
                stateVectorPoint: out _,
                skipReason: out _,
                recordingIndex: 1);
        }

        private static Recording MakeBelowThresholdRecording(string recordingId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                TerminalStateValue = TerminalState.SubOrbital,
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

        private static Recording MakeOrbitSegmentRecording(string recordingId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                TerminalStateValue = TerminalState.Orbiting,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 200.0,
                        endUT = 400.0,
                        bodyName = "Mun",
                        semiMajorAxis = 250000.0,
                        eccentricity = 0.1,
                        inclination = 0.0,
                        longitudeOfAscendingNode = 0.0,
                        argumentOfPeriapsis = 0.0,
                        meanAnomalyAtEpoch = 0.0,
                        epoch = 200.0,
                    }
                }
            };
        }

    }
}
