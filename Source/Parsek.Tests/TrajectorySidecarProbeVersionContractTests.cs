using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression for #565: the in-game test
    /// <c>SaveLoadTests.CurrentFormatTrajectorySidecarsProbeAsBinary</c> previously asserted strict
    /// equality between <c>probe.FormatVersion</c> (the on-disk binary-encoding version stamped at
    /// the last .prec write) and <c>rec.RecordingFormatVersion</c> (the in-memory semantic
    /// version). Those values are not the same thing: post-load semantic migrations like
    /// <c>RecordingStore.MigrateLegacyLoopIntervalAfterHydration</c> bump the in-memory format
    /// version to v4 (launch-to-launch loop interval) without rewriting the sidecar — the v4
    /// change was metadata-only, the binary layout is identical to v3. The contract guaranteed
    /// by <c>TrajectorySidecarBinary.Read</c> is therefore the asymmetric:
    /// <code>
    ///   probe.FormatVersion &lt;= rec.RecordingFormatVersion &lt;= CurrentRecordingFormatVersion
    /// </code>
    /// not strict equality.
    /// </summary>
    [Collection("Sequential")]
    public class TrajectorySidecarProbeVersionContractTests : IDisposable
    {
        private readonly string tempDir;

        public TrajectorySidecarProbeVersionContractTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-probe-version-contract-" + Guid.NewGuid().ToString("N"));
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
                try { Directory.Delete(tempDir, recursive: true); }
                catch { }
            }
        }

        /// <summary>
        /// Replays the load-time sequence: a recording is written to .prec at semantic v3, then
        /// a post-load migration (e.g. legacy loop-interval) bumps the in-memory recording to v4
        /// without dirtying the sidecar. The probe must still report v3 (the on-disk truth), the
        /// recording must be at v4 (the migrated semantic), and the
        /// <c>probe.FormatVersion &lt;= rec.RecordingFormatVersion</c> contract must hold.
        /// </summary>
        [Fact]
        public void Probe_PostLegacyLoopMigration_HoldsAsymmetricContract()
        {
            var rec = BuildSimpleRecordingAtVersion(
                recordingId: "post-legacy-loop",
                formatVersion: 3);

            string path = Path.Combine(tempDir, rec.RecordingId + ".prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 5);

            // Simulate MigrateLegacyLoopIntervalAfterHydration: in-memory bump to v4 with no
            // sidecar rewrite. Production calls
            // RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration; we do the
            // same effective in-memory mutation here without taking the dependency on the loop
            // helpers.
            RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);
            Assert.Equal(
                RecordingStore.LaunchToLaunchLoopIntervalFormatVersion,
                rec.RecordingFormatVersion);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);

            // On-disk binary version stays at the write-time v3 — v4 is metadata-only and the
            // sidecar was never re-saved.
            Assert.Equal(3, probe.FormatVersion);
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);

            // The contract the in-game test now enforces:
            //   probe.FormatVersion <= rec.RecordingFormatVersion <= CurrentRecordingFormatVersion
            // and the probe must report a known schema version this build understands.
            Assert.True(probe.FormatVersion <= rec.RecordingFormatVersion,
                $"probe.FormatVersion={probe.FormatVersion} must not exceed " +
                $"rec.RecordingFormatVersion={rec.RecordingFormatVersion}");
            Assert.True(probe.FormatVersion <= RecordingStore.CurrentRecordingFormatVersion,
                $"probe.FormatVersion={probe.FormatVersion} must not exceed " +
                $"CurrentRecordingFormatVersion={RecordingStore.CurrentRecordingFormatVersion}");
            Assert.True(rec.RecordingFormatVersion <= RecordingStore.CurrentRecordingFormatVersion,
                $"rec.RecordingFormatVersion={rec.RecordingFormatVersion} must not exceed " +
                $"CurrentRecordingFormatVersion={RecordingStore.CurrentRecordingFormatVersion}");
        }

        /// <summary>
        /// The complementary case: a current-format recording (rec at
        /// <see cref="RecordingStore.CurrentRecordingFormatVersion"/>) writes its sidecar at the
        /// matching version, and the contract reduces to equality. The relaxed assertion still
        /// passes here, so freshly-written recordings are unaffected by the test relaxation.
        /// </summary>
        [Fact]
        public void Probe_FreshlyWrittenAtCurrentVersion_ContractReducesToEquality()
        {
            var rec = BuildSimpleRecordingAtVersion(
                recordingId: "fresh-current-version",
                formatVersion: RecordingStore.CurrentRecordingFormatVersion);

            string path = Path.Combine(tempDir, rec.RecordingId + ".prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.Equal(rec.RecordingFormatVersion, probe.FormatVersion);
            Assert.True(probe.FormatVersion <= rec.RecordingFormatVersion);
            Assert.True(probe.FormatVersion <= RecordingStore.CurrentRecordingFormatVersion);
        }

        private static Recording BuildSimpleRecordingAtVersion(string recordingId, int formatVersion)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = "Probe Fixture",
                RecordingFormatVersion = formatVersion,
                LoopPlayback = true,
                // Pre-#412 legacy loop save: post-cycle gap stored as small seconds value.
                LoopIntervalSeconds = 10.0,
                LoopTimeUnit = LoopTimeUnit.Sec,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.10,
                longitude = -74.50,
                altitude = 120,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 50f, 0f),
                bodyName = "Kerbin",
                funds = 0,
                science = 0,
                reputation = 0
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 168,
                latitude = -0.09,
                longitude = -74.49,
                altitude = 300,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 75f, 0f),
                bodyName = "Kerbin",
                funds = 0,
                science = 0,
                reputation = 0
            });
            return rec;
        }
    }
}
