using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Integration tests covering the ghost-map source-resolve verbose line and
    /// the spawner suppression line. Both fire from per-frame hot paths and
    /// previously stormed the log when the same decision tuple repeated for
    /// the entire session. The fix routes both through
    /// <see cref="ParsekLog.VerboseOnChange"/> so a stable
    /// <c>(source, reason)</c> tuple emits exactly once until the decision flips,
    /// with the suppressed count surfaced as <c>| suppressed=N</c> on the next
    /// state change.
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
        // VerboseOnChange itself, exercised through a faux Spawner pattern
        // that mirrors ParsekFlight.ComputePlaybackFlags. The real call site
        // is private inside the flight controller, but the helper IS the
        // contract — testing it here covers the spawner fix end-to-end.
        // -------------------------------------------------------------------

        [Fact]
        public void Spawner_NoVesselSnapshotPattern_EmitsOncePerRecording()
        {
            // Simulate ComputePlaybackFlags running 50 frames over 5 recordings
            // all stuck in "no vessel snapshot". The fix keys identity per
            // RecordingId and state per reason — stable reason on each
            // recording must collapse to one line per recording.
            for (int frame = 0; frame < 50; frame++)
            {
                for (int recIdx = 0; recIdx < 5; recIdx++)
                {
                    EmitSpawnerSuppression(
                        recIdx,
                        "rec-" + recIdx,
                        "Vessel " + recIdx,
                        "no vessel snapshot");
                }
            }

            int suppressedLines = logLines.Count(l =>
                l.Contains("[Spawner]")
                && l.Contains("Spawn suppressed for #")
                && l.Contains("no vessel snapshot"));

            Assert.Equal(5, suppressedLines);
        }

        [Fact]
        public void Spawner_ReasonFlipForSameRecording_ReemitsWithSuppressedCount()
        {
            // Stable "no vessel snapshot" repeats, then the recording flips
            // to a different reason — the new reason must emit and carry
            // the suppressed count from the prior streak.
            EmitSpawnerSuppression(7, "rec-7", "Showcase", "no vessel snapshot");
            EmitSpawnerSuppression(7, "rec-7", "Showcase", "no vessel snapshot");
            EmitSpawnerSuppression(7, "rec-7", "Showcase", "no vessel snapshot");
            EmitSpawnerSuppression(7, "rec-7", "Showcase", "chain-suppressed");

            var lines = logLines
                .Where(l => l.Contains("[Spawner]") && l.Contains("Spawn suppressed for #7"))
                .ToList();

            Assert.Equal(2, lines.Count);
            Assert.Contains("no vessel snapshot", lines[0]);
            Assert.DoesNotContain("suppressed=", lines[0]);
            Assert.Contains("chain-suppressed", lines[1]);
            Assert.Contains("| suppressed=2", lines[1]);
        }

        // -------------------------------------------------------------------
        // Spawner identity-by-RecordingId regression. The bare list index is
        // not a stable identity — when a recording is discarded, recordings
        // after it shift down one slot and the same index becomes a different
        // recording. Keying VerboseOnChange on the index alone would let the
        // new occupant inherit the prior recording's cached state (silently
        // masking its first-emission line, or surfacing a stale suppressed
        // counter on the next flip). Keying on RecordingId restores per-
        // recording independence.
        // -------------------------------------------------------------------

        [Fact]
        public void SpawnSuppression_IndexReuseAcrossRecordings_KeepsIndependentState()
        {
            // Two distinct recordings end up sharing index 294 (one was
            // discarded between observations) and both report the same
            // suppression reason. Each must emit its own first-emission line.
            EmitSpawnerSuppression(294, "rec-A", "Untitled Space Craft", "no vessel snapshot");
            EmitSpawnerSuppression(294, "rec-A", "Untitled Space Craft", "no vessel snapshot");
            EmitSpawnerSuppression(294, "rec-A", "Untitled Space Craft", "no vessel snapshot");

            // Index 294 is now occupied by recording B (rec-A discarded).
            // Same reason — but a different RecordingId means a fresh
            // first-emission, not silent inheritance of rec-A's cache.
            EmitSpawnerSuppression(294, "rec-B", "Different Vessel", "no vessel snapshot");
            EmitSpawnerSuppression(294, "rec-B", "Different Vessel", "no vessel snapshot");

            var lines = logLines
                .Where(l => l.Contains("[Spawner]") && l.Contains("Spawn suppressed for #294"))
                .ToList();

            Assert.Equal(2, lines.Count);
            Assert.Contains("Untitled Space Craft", lines[0]);
            Assert.DoesNotContain("suppressed=", lines[0]);
            // The second line is rec-B's first emission — a clean first hit,
            // NOT a delayed flip carrying rec-A's suppressed counter.
            Assert.Contains("Different Vessel", lines[1]);
            Assert.DoesNotContain("suppressed=", lines[1]);
        }

        [Fact]
        public void SpawnSuppression_IndexReuse_StableReasonStillCoalescesPerRecording()
        {
            // Once each recording has emitted its first line, subsequent
            // stable repeats must coalesce per-RecordingId — the cache must
            // not be globally indexed by idx in a way that thrashes between
            // recordings sharing the same slot.
            EmitSpawnerSuppression(50, "rec-X", "Vessel X", "no vessel snapshot");
            EmitSpawnerSuppression(50, "rec-Y", "Vessel Y", "no vessel snapshot");

            // Interleave further per-frame calls — all stable. Each recording
            // has its own state, so neither emits again until its reason flips.
            for (int i = 0; i < 10; i++)
            {
                EmitSpawnerSuppression(50, "rec-X", "Vessel X", "no vessel snapshot");
                EmitSpawnerSuppression(50, "rec-Y", "Vessel Y", "no vessel snapshot");
            }

            int xLines = logLines.Count(l =>
                l.Contains("[Spawner]") && l.Contains("Vessel X"));
            int yLines = logLines.Count(l =>
                l.Contains("[Spawner]") && l.Contains("Vessel Y"));

            Assert.Equal(1, xLines);
            Assert.Equal(1, yLines);
        }

        [Fact]
        public void SpawnSuppression_NullRecordingId_FallsBackToIndexInIdentity()
        {
            // Defensive case: if RecordingId is somehow null/empty, the call
            // site falls back to "idx-{N}" so emissions still flow. Two
            // null-Id recordings sharing the same index DO collide (no better
            // identity is available), but a real RecordingId on a sibling
            // recording is still tracked independently.
            EmitSpawnerSuppressionRaw(idx: 9, recordingId: null, vesselName: "Anon",
                reason: "no vessel snapshot");
            EmitSpawnerSuppressionRaw(idx: 9, recordingId: null, vesselName: "Anon",
                reason: "no vessel snapshot");

            int anonLines = logLines.Count(l =>
                l.Contains("[Spawner]") && l.Contains("Spawn suppressed for #9"));

            Assert.Equal(1, anonLines);
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

        // Mirrors the production call site in ParsekFlight.ComputePlaybackFlags
        // exactly so the test pins the (identity, stateKey) shape we ship.
        // Identity is keyed on RecordingId (with an idx-{N} fallback for
        // null/empty Ids) so two recordings that briefly share the same list
        // index do not inherit each other's cached state.
        private static void EmitSpawnerSuppression(
            int idx, string recordingId, string vesselName, string reason)
        {
            EmitSpawnerSuppressionRaw(idx, recordingId, vesselName, reason);
        }

        private static void EmitSpawnerSuppressionRaw(
            int idx, string recordingId, string vesselName, string reason)
        {
            string identity = "spawn-suppressed|"
                + (!string.IsNullOrEmpty(recordingId) ? recordingId : "idx-" + idx);
            ParsekLog.VerboseOnChange(
                "Spawner",
                identity,
                reason ?? "(none)",
                $"Spawn suppressed for #{idx} \"{vesselName}\": {reason}");
        }
    }
}
