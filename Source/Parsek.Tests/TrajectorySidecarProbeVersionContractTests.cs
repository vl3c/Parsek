using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression for #565 / #567: the in-game test
    /// <c>SaveLoadTests.CurrentFormatTrajectorySidecarsProbeAsBinary</c> originally asserted
    /// strict equality between <c>probe.FormatVersion</c> (the on-disk binary-encoding version
    /// stamped at the last .prec write) and <c>rec.RecordingFormatVersion</c> (the in-memory
    /// semantic version). Those are not the same thing: the post-load
    /// <c>RecordingStore.MigrateLegacyLoopIntervalAfterHydration</c> migration promotes the
    /// in-memory format version from v3 to v4 (launch-to-launch loop interval) without
    /// rewriting the sidecar, because v4 is a metadata-only change — the binary layout is
    /// identical to v3. The runtime test was first relaxed to <c>probe &lt;= rec</c>, but that
    /// was too broad: v5 adds serialized <c>OrbitSegment.isPredicted</c> and v6 changes
    /// RELATIVE TrackSection point semantics, so a v3 sidecar paired with a v5/v6 recording
    /// indicates stale binary data on disk that the test must catch.
    /// <para>
    /// The narrow contract enforced by
    /// <see cref="RecordingStore.IsAcceptableSidecarVersionLag(int, int)"/> is therefore:
    /// equality, OR exactly the v3 sidecar / v4 recording metadata-only exception. Every
    /// other combination is rejected.
    /// </para>
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
        /// recording must be at v4 (the migrated semantic), and
        /// <see cref="RecordingStore.IsAcceptableSidecarVersionLag(int, int)"/> must accept the
        /// pair as the documented metadata-only exception.
        /// </summary>
        [Fact]
        public void Probe_PostLegacyLoopMigration_AllowedAsMetadataOnlyException()
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

            // The narrow contract the in-game test now enforces accepts this lag.
            Assert.True(
                RecordingStore.IsAcceptableSidecarVersionLag(probe.FormatVersion, rec.RecordingFormatVersion),
                $"v{probe.FormatVersion} sidecar with v{rec.RecordingFormatVersion} recording " +
                "must be accepted as the documented metadata-only legacy-loop migration");
            Assert.True(probe.FormatVersion <= RecordingStore.CurrentRecordingFormatVersion,
                $"probe.FormatVersion={probe.FormatVersion} must not exceed " +
                $"CurrentRecordingFormatVersion={RecordingStore.CurrentRecordingFormatVersion}");
            Assert.True(rec.RecordingFormatVersion <= RecordingStore.CurrentRecordingFormatVersion,
                $"rec.RecordingFormatVersion={rec.RecordingFormatVersion} must not exceed " +
                $"CurrentRecordingFormatVersion={RecordingStore.CurrentRecordingFormatVersion}");
        }

        /// <summary>
        /// Equality case at the v3 anchor: a v3 sidecar paired with a v3 in-memory recording
        /// (no migration ran) must be accepted by the predicate. Provides explicit coverage of
        /// the equality branch at the lag boundary.
        /// </summary>
        [Fact]
        public void Probe_V3SidecarV3Recording_AllowedByEquality()
        {
            Assert.True(
                RecordingStore.IsAcceptableSidecarVersionLag(
                    probeFormatVersion: 3,
                    recordingFormatVersion: 3),
                "v3 sidecar with v3 recording is the equality case and must be accepted.");
        }

        /// <summary>
        /// The complementary case: a current-format recording (rec at
        /// <see cref="RecordingStore.CurrentRecordingFormatVersion"/>) writes its sidecar at the
        /// matching version, and the contract reduces to equality. The narrow predicate still
        /// passes here, so freshly-written recordings are unaffected.
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
            Assert.True(
                RecordingStore.IsAcceptableSidecarVersionLag(probe.FormatVersion, rec.RecordingFormatVersion),
                "freshly-written sidecar at the current version must satisfy the contract by equality");
            Assert.True(probe.FormatVersion <= RecordingStore.CurrentRecordingFormatVersion);
        }

        /// <summary>
        /// A v3 sidecar paired with a v5 recording indicates the binary on disk predates the
        /// v5 <c>OrbitSegment.isPredicted</c> contract change — the predicted-flag field would
        /// be missing from every orbit segment in the trajectory. The in-game runtime test must
        /// flag this; the predicate rejects it.
        /// </summary>
        [Fact]
        public void Probe_V3SidecarV5Recording_RejectedAsStale()
        {
            Assert.False(
                RecordingStore.IsAcceptableSidecarVersionLag(
                    probeFormatVersion: 3,
                    recordingFormatVersion: RecordingStore.PredictedOrbitSegmentFormatVersion),
                "v3 sidecar with v5 recording is a binary-layout lag (missing isPredicted) " +
                "and must be rejected.");
        }

        /// <summary>
        /// A v3 sidecar paired with a v6 recording indicates the binary on disk predates the
        /// v6 RELATIVE TrackSection anchor-local point semantics — RELATIVE points would be
        /// reinterpreted incorrectly on load. The in-game runtime test must flag this; the
        /// predicate rejects it.
        /// </summary>
        [Fact]
        public void Probe_V3SidecarV6Recording_RejectedAsStale()
        {
            Assert.False(
                RecordingStore.IsAcceptableSidecarVersionLag(
                    probeFormatVersion: 3,
                    recordingFormatVersion: RecordingStore.RelativeLocalFrameFormatVersion),
                "v3 sidecar with v6 recording is a binary-layout lag (RELATIVE point " +
                "semantics changed) and must be rejected.");
        }

        /// <summary>
        /// A v4 sidecar paired with a v6 recording is a multi-step lag: v4 -> v5 added the
        /// predicted-flag, v5 -> v6 changed RELATIVE point semantics. The narrow contract is
        /// equality OR the single explicit v3-&gt;v4 metadata-only exception, and this case is
        /// neither, so the predicate rejects it.
        /// </summary>
        [Fact]
        public void Probe_V4SidecarV6Recording_RejectedAsStale()
        {
            Assert.False(
                RecordingStore.IsAcceptableSidecarVersionLag(
                    probeFormatVersion: RecordingStore.LaunchToLaunchLoopIntervalFormatVersion,
                    recordingFormatVersion: RecordingStore.RelativeLocalFrameFormatVersion),
                "v4 sidecar with v6 recording is a multi-bump binary lag and must be rejected.");
        }

        /// <summary>
        /// A probe version higher than the recording version should never happen in production
        /// (<c>TrajectorySidecarBinary.Read</c> only promotes upward), but if it ever does the
        /// predicate must reject it.
        /// </summary>
        [Fact]
        public void Probe_HigherThanRecording_Rejected()
        {
            Assert.False(
                RecordingStore.IsAcceptableSidecarVersionLag(
                    probeFormatVersion: RecordingStore.CurrentRecordingFormatVersion,
                    recordingFormatVersion: 3),
                "probe version higher than recording version must be rejected.");
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
