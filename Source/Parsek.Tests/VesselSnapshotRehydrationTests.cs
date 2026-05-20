using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for re-hydrating a recording's vessel snapshot from its
    /// <c>_vessel.craft</c> sidecar at spawn time. The in-memory snapshot is a
    /// transient cache that gets dropped in-session; the terminal-spawn path must
    /// reload it from the durable sidecar so a spawnable leaf (e.g. an orbital
    /// payload re-flown after a Rewind-to-Launch) can still materialize.
    /// Exercises the path-explicit core (KSP save-context resolution is covered by
    /// an in-game test instead, since it needs the live save folder).
    /// </summary>
    [Collection("Sequential")]
    public class VesselSnapshotRehydrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public VesselSnapshotRehydrationTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_rehydrate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static ConfigNode MakeVesselNode()
        {
            var n = new ConfigNode("VESSEL");
            n.AddValue("name", "Kerbal X Probe");
            n.AddValue("sit", "ORBITING");
            return n;
        }

        private string WriteVesselSidecar(ConfigNode node)
        {
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "_vessel.craft");
            RecordingStore.WriteSnapshotSidecarForTesting(path, node);
            return path;
        }

        [Fact]
        public void Hydrate_AlreadyLoaded_IsNoOp_AndDoesNotTouchDisk()
        {
            var existing = MakeVesselNode();
            var rec = new Recording { RecordingId = "rec-1", VesselSnapshot = existing };

            // Path points nowhere; a no-op must not attempt to read it.
            bool ok = RecordingStore.TryHydrateVesselSnapshotFromPath(
                rec, Path.Combine(tempDir, "absent_vessel.craft"));

            Assert.True(ok);
            Assert.Same(existing, rec.VesselSnapshot);
        }

        [Fact]
        public void Hydrate_FromSidecar_RestoresDroppedSnapshot_AndLogs()
        {
            string path = WriteVesselSidecar(MakeVesselNode());
            var rec = new Recording { RecordingId = "rec-1", VesselSnapshot = null };

            bool ok = RecordingStore.TryHydrateVesselSnapshotFromPath(rec, path);

            Assert.True(ok);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.Equal("Kerbal X Probe", rec.VesselSnapshot.GetValue("name"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("Re-hydrated vessel snapshot from sidecar"));
        }

        [Fact]
        public void Hydrate_MissingSidecar_ReturnsFalse_LeavesNull()
        {
            var rec = new Recording { RecordingId = "rec-1", VesselSnapshot = null };

            bool ok = RecordingStore.TryHydrateVesselSnapshotFromPath(
                rec, Path.Combine(tempDir, "missing_vessel.craft"));

            Assert.False(ok);
            Assert.Null(rec.VesselSnapshot);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Hydrate_NullOrEmptyPath_ReturnsFalse(string path)
        {
            var rec = new Recording { RecordingId = "rec-1", VesselSnapshot = null };

            Assert.False(RecordingStore.TryHydrateVesselSnapshotFromPath(rec, path));
            Assert.Null(rec.VesselSnapshot);
        }

        [Fact]
        public void Hydrate_InvalidSidecarContent_ReturnsFalse_NoCrash()
        {
            string path = Path.Combine(tempDir, "garbage_vessel.craft");
            File.WriteAllText(path, "this is not a valid snapshot sidecar @@@@");
            var rec = new Recording { RecordingId = "rec-1", VesselSnapshot = null };

            bool ok = RecordingStore.TryHydrateVesselSnapshotFromPath(rec, path);

            Assert.False(ok);
            Assert.Null(rec.VesselSnapshot);
        }

        [Fact]
        public void Hydrate_NullRecording_ReturnsFalse()
        {
            Assert.False(RecordingStore.TryHydrateVesselSnapshotFromPath(null, "x"));
        }

        // ---- Gate integration: ShouldSpawnAtRecordingEnd ----

        [Fact]
        public void Gate_InMemorySnapshotPresent_SpawnableLeaf_StillSpawns()
        {
            // Regression guard: the snapshot-present path is unchanged.
            var rec = new Recording
            {
                RecordingId = "rec-1",
                VesselSnapshot = MakeVesselNode(),
                TerminalStateValue = TerminalState.Orbiting
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void Gate_NullSnapshot_SpawnableLeaf_NoSidecarResolvable_ReportsNoSnapshot()
        {
            // Outside KSP the save context is unavailable, so the no-arg resolver
            // cannot find a sidecar; the gate must fall back to "no vessel snapshot"
            // rather than throw or wrongly spawn.
            var rec = new Recording
            {
                RecordingId = "rec-1",
                VesselSnapshot = null,
                TerminalStateValue = TerminalState.Orbiting
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.False(needsSpawn);
            Assert.Equal("no vessel snapshot", reason);
        }

        [Fact]
        public void Gate_NullSnapshot_Debris_DoesNotAttemptHydration_ReportsNoSnapshot()
        {
            // Debris is never worth hydrating; the worthHydrating guard short-circuits
            // before any disk probe.
            var rec = new Recording
            {
                RecordingId = "rec-1",
                VesselSnapshot = null,
                IsDebris = true,
                TerminalStateValue = TerminalState.Orbiting
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.False(needsSpawn);
            Assert.Equal("no vessel snapshot", reason);
        }
    }
}
