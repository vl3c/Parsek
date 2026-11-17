using System;
using System.Globalization;
using System.IO;
using Parsek.InGameTests.Spikes.S11;
using Parsek.Tests.Generators;

namespace Parsek.Tests.Spikes
{
    /// <summary>
    /// R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.
    ///
    /// The <c>spike-s11-ghosts</c> injection preset: 64 committed single-recording trees,
    /// ids <c>spike-s11-ghost-00</c> to <c>-63</c>, each a ghost-only (no vessel snapshot,
    /// so nothing spawns) three-part rocket (OKTO core, FL-T400 tank, LV-T30 engine lit
    /// from the first sample) circling the pad vessel of <c>pad-runway-pair</c> on the
    /// orbit <see cref="SpikeS11Layout"/> gives ghost i. Every sample sits 40 to 110 m
    /// from the pad and 30 to 100 m above it, all inside the 10 km full-fidelity zone of
    /// the focused vessel, so every ghost the sweep enables is a full ghost near the camera.
    ///
    /// The recordings start <see cref="SpikeS11Layout.StartAfterSaveSeconds"/> after the
    /// host's clock and run <see cref="SpikeS11Layout.DurationSeconds"/>, which outlasts
    /// the whole sweep at 1x. The host clock is pinned: a save at another UT is refused.
    ///
    /// Beside the recordings the injector copies one real trajectory sidecar pair (the
    /// largest committed one, a Jool tour flight) into <c>&lt;save&gt;/SpikeS11/</c>, a
    /// folder the save load never reads, for the first-open measure.
    /// </summary>
    internal static class SpikeS11Fixture
    {
        /// <summary><c>pad-runway-pair</c>'s own clock (persistent.sfs FLIGHTSTATE UT).</summary>
        internal const double HostSaveUT = 25.25999999999955;

        internal const string GroupName = "Synthetic-SpikeS11";

        internal const uint CorePid = 100000u;
        internal const uint TankPid = 101111u;
        internal const uint EnginePid = 102222u;
        internal const string EnginePartName = "liquidEngine";

        // Surface-relative rotation of a vessel standing upright at the KSC (the same
        // constants SyntheticRecordingTests uses for its pad recordings).
        internal const float UprightX = -0.7009714f, UprightY = -0.09230039f, UprightZ = -0.09728389f, UprightW = 0.7004681f;

        /// <summary>Repo-relative folder and id of the copied real sidecar pair.</summary>
        internal const string ColdSourceRecordingsDir = "harness/fixtures/saves/jool-tour-recorded/Parsek/Recordings";
        internal const string ColdSourceRecordingId = "e8a83295907444c4871b584e0f458f8d";

        internal static double StartUTFor(double saveUT) => saveUT + SpikeS11Layout.StartAfterSaveSeconds;

        internal static RecordingBuilder BuildGhost(int index, double saveUT)
        {
            if (index < 0 || index >= SpikeS11Plan.MaxGhosts)
                throw new ArgumentOutOfRangeException(nameof(index));
            string name = SpikeS11Plan.VesselNameFor(index);
            double t0 = StartUTFor(saveUT);
            var b = new RecordingBuilder(name)
                .WithRecordingId(SpikeS11Plan.RecordingIdFor(index))
                .WithRecordingGroup(GroupName)
                .WithDefaultRotation(UprightX, UprightY, UprightZ, UprightW);

            int count = SpikeS11Layout.PointCount;
            for (int k = 0; k < count; k++)
            {
                double t = k * SpikeS11Layout.SampleSpacingSeconds;
                SpikeS11Layout.PositionAt(index, t, out double lat, out double lon, out double alt);
                b.AddPoint(t0 + t, lat, lon, alt);
            }

            b.AddPartEvent(t0, EnginePid, (int)PartEventType.EngineIgnited, EnginePartName, value: 1f);

            SpikeS11Layout.PositionAt(index, 0, out double lat0, out double lon0, out double alt0);
            b.WithGhostVisualSnapshot(BuildRocket(name, index, lat0, lon0, alt0));
            return b;
        }

        internal static VesselSnapshotBuilder BuildRocket(string name, int index, double lat, double lon, double alt)
        {
            return new VesselSnapshotBuilder()
                .WithName(name)
                .WithPersistentId(3110000u + (uint)index)
                .WithType("Probe")
                .AddPart("probeCoreOcto")
                .AddPart("fuelTank", position: "0,-0.75,0")
                .AddPart(EnginePartName, position: "0,-1.5,0")
                .AsLanded(lat, lon, alt);
        }

        internal static void PopulateWriter(ScenarioWriter writer, double saveUT)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                writer.AddRecordingAsTree(BuildGhost(i, saveUT));
        }

        /// <summary>
        /// Copies the real sidecar pair into &lt;saveDir&gt;/SpikeS11/ (replacing what is
        /// there). Returns the copied .prec path.
        /// </summary>
        internal static string CopyColdSidecar(string repoRoot, string saveDir)
        {
            string src = Path.Combine(repoRoot, ColdSourceRecordingsDir.Replace('/', Path.DirectorySeparatorChar));
            string dst = Path.Combine(saveDir, SpikeS11Plan.ColdFolderName);
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
            Directory.CreateDirectory(dst);
            string prec = ColdSourceRecordingId + SpikeS11FirstOpen.TrajectoryExtension;
            string craft = ColdSourceRecordingId + SpikeS11FirstOpen.GhostSnapshotSuffix;
            File.Copy(Path.Combine(src, prec), Path.Combine(dst, prec));
            File.Copy(Path.Combine(src, craft), Path.Combine(dst, craft));
            return Path.Combine(dst, prec);
        }

        internal static string DescribeUT(double ut) => ut.ToString("R", CultureInfo.InvariantCulture);
    }
}
