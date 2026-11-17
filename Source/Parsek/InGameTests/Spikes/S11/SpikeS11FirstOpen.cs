using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Parsek.InGameTests.Spikes.S11
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.
    //
    // The first-open measure of design 7.6 / 15.13: a rival flight's trajectory file is
    // opened on the main thread when its ghost is first needed. This times that open with
    // the production readers (probe + decode of the .prec, load of the _ghost.craft
    // snapshot) on one sidecar pair the injector copied into <save>/SpikeS11/, a folder the
    // save load never reads. Plain file I/O and Parsek's codecs only, no Unity call, so the
    // xUnit suite runs it against the same committed fixture file.

    internal enum SpikeS11OpenKind { Trajectory, Snapshot, ReadBytes }

    internal struct SpikeS11OpenResult
    {
        internal SpikeS11OpenKind Kind;
        internal string File;
        internal long Bytes;
        internal double ProbeMs;     // trajectory: header probe; snapshot / read: 0
        internal double DecodeMs;    // trajectory: body decode; snapshot: probe + inflate + parse; read: File.ReadAllBytes
        internal double TotalMs;
        internal int Items;          // trajectory: points + orbit segments + part events + track sections; snapshot: PART nodes
        internal bool Ok;
        internal string Error;
    }

    internal static class SpikeS11FirstOpen
    {
        internal const string TrajectoryExtension = ".prec";
        internal const string GhostSnapshotSuffix = "_ghost.craft";

        internal static readonly string[] CsvHeader =
            { "run", "attempt", "kind", "file", "bytes", "probeMs", "decodeMs", "totalMs", "items", "ok", "error" };

        internal static string KindToken(SpikeS11OpenKind kind)
        {
            switch (kind)
            {
                case SpikeS11OpenKind.Trajectory: return "trajectory";
                case SpikeS11OpenKind.Snapshot: return "snapshot";
                default: return "readbytes";
            }
        }

        /// <summary>The largest .prec in the folder (ordinal name breaks a tie), or null.</summary>
        internal static string FindLargestTrajectory(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            string best = null;
            long bestLen = -1;
            foreach (string path in Directory.GetFiles(folder, "*" + TrajectoryExtension))
            {
                long len = new FileInfo(path).Length;
                if (len > bestLen || (len == bestLen && string.CompareOrdinal(path, best) < 0))
                {
                    best = path;
                    bestLen = len;
                }
            }
            return best;
        }

        /// <summary>The _ghost.craft beside a .prec (same recording id), or null when absent.</summary>
        internal static string GhostSnapshotFor(string trajectoryPath)
        {
            if (string.IsNullOrEmpty(trajectoryPath)) return null;
            string dir = Path.GetDirectoryName(trajectoryPath) ?? "";
            string id = Path.GetFileNameWithoutExtension(trajectoryPath);
            string path = Path.Combine(dir, id + GhostSnapshotSuffix);
            return File.Exists(path) ? path : null;
        }

        private static double Ms(Stopwatch sw) => sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Probe and decode one trajectory sidecar into a fresh Recording, as a lazy ghost open would.</summary>
        internal static SpikeS11OpenResult OpenTrajectory(string path)
        {
            var r = new SpikeS11OpenResult { Kind = SpikeS11OpenKind.Trajectory, File = Path.GetFileName(path ?? "") };
            try
            {
                r.Bytes = new FileInfo(path).Length;
                var total = Stopwatch.StartNew();
                var sw = Stopwatch.StartNew();
                TrajectorySidecarProbe probe;
                bool probed = RecordingSidecarStore.TryProbeTrajectorySidecar(path, out probe, quietOnSuccess: true);
                sw.Stop();
                r.ProbeMs = Ms(sw);
                if (!probed || !probe.Supported)
                {
                    total.Stop();
                    r.TotalMs = Ms(total);
                    r.Error = "probe failed: " + (probe.FailureReason ?? "unsupported or missing");
                    return r;
                }
                var rec = new Recording();
                sw = Stopwatch.StartNew();
                RecordingSidecarStore.DeserializeTrajectorySidecar(path, probe, rec);
                sw.Stop();
                total.Stop();
                r.DecodeMs = Ms(sw);
                r.TotalMs = Ms(total);
                r.Items = rec.Points.Count + rec.OrbitSegments.Count + rec.PartEvents.Count + rec.TrackSections.Count;
                r.Ok = true;
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        /// <summary>Load one snapshot sidecar (probe, inflate, CRC, ConfigNode parse).</summary>
        internal static SpikeS11OpenResult OpenSnapshot(string path)
        {
            var r = new SpikeS11OpenResult { Kind = SpikeS11OpenKind.Snapshot, File = Path.GetFileName(path ?? "") };
            try
            {
                r.Bytes = new FileInfo(path).Length;
                var sw = Stopwatch.StartNew();
                ConfigNode node;
                SnapshotSidecarProbe probe;
                bool ok = SnapshotSidecarCodec.TryLoad(path, out node, out probe);
                sw.Stop();
                r.DecodeMs = Ms(sw);
                r.TotalMs = r.DecodeMs;
                if (!ok || node == null)
                {
                    r.Error = "load failed: " + (probe.FailureReason ?? "unknown");
                    return r;
                }
                r.Items = node.GetNodes("PART").Length;
                r.Ok = true;
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        /// <summary>The bare read of the same file, to split I/O from decode.</summary>
        internal static SpikeS11OpenResult ReadBytes(string path)
        {
            var r = new SpikeS11OpenResult { Kind = SpikeS11OpenKind.ReadBytes, File = Path.GetFileName(path ?? "") };
            try
            {
                var sw = Stopwatch.StartNew();
                byte[] bytes = File.ReadAllBytes(path);
                sw.Stop();
                r.Bytes = bytes.Length;
                r.DecodeMs = Ms(sw);
                r.TotalMs = r.DecodeMs;
                r.Ok = true;
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        internal static string Line(int attempt, SpikeS11OpenResult r)
        {
            var inv = CultureInfo.InvariantCulture;
            return "firstopen attempt=" + attempt.ToString(inv)
                + " kind=" + KindToken(r.Kind)
                + " file=" + r.File
                + " bytes=" + r.Bytes.ToString(inv)
                + " probeMs=" + SpikeS11Format.Ms(r.ProbeMs)
                + " decodeMs=" + SpikeS11Format.Ms(r.DecodeMs)
                + " totalMs=" + SpikeS11Format.Ms(r.TotalMs)
                + " items=" + r.Items.ToString(inv)
                + " ok=" + (r.Ok ? "true" : "false")
                + (r.Error != null ? " error=\"" + r.Error + "\"" : "");
        }

        internal static object[] Row(string run, int attempt, SpikeS11OpenResult r)
            => new object[]
            {
                run, attempt, KindToken(r.Kind), r.File, r.Bytes, r.ProbeMs, r.DecodeMs, r.TotalMs, r.Items, r.Ok, r.Error ?? "",
            };
    }
}
