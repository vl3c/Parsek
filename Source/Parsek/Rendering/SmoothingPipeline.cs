using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 1 smoothing-pipeline orchestrator (design doc §17.3.1, §18 Phase 1
    /// row, §19.2 Stage 1 + Sidecar tables, §26.1 HR-1 / HR-7 / HR-9 / HR-10 /
    /// HR-12). Sits between <see cref="RecordingSidecarStore"/> on the load /
    /// commit seam and <see cref="SectionAnnotationStore"/> on the consumer
    /// side.
    ///
    /// <para>
    /// Responsibilities:
    /// <list type="bullet">
    /// <item><description>Iterate <see cref="Recording.TrackSections"/>, fit a
    /// Catmull-Rom spline through each ABSOLUTE-frame ExoPropulsive /
    /// ExoBallistic section's <c>frames</c>, and stage the result in
    /// <see cref="SectionAnnotationStore"/>.</description></item>
    /// <item><description>Read / write the <c>.pann</c> sidecar atomically, and
    /// gate cached splines on the five-field cache key (binary version,
    /// algorithm stamp, sidecar epoch, recording-format version, configuration
    /// hash). Any drift triggers a discard + recompute (HR-10).</description></item>
    /// <item><description>Persist freshly-fitted splines to <c>.pann</c> after
    /// the commit batch applies. <c>.pann</c> write failure must NOT abort
    /// recording commit — the file is regenerable, so we log Warn and proceed
    /// (HR-9, HR-12).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// HR-1: this class never writes to <c>.prec</c>. Only the sibling
    /// <c>.pann</c> is touched.
    /// </para>
    /// </summary>
    internal static class SmoothingPipeline
    {
        // Cached configuration hash. Computed once per process; recomputed only
        // if a future code path swaps SmoothingConfiguration.Default. The on/off
        // toggle (ParsekSettings.useSmoothingSplines) is intentionally NOT in
        // the hash — toggling it does not invalidate fitted splines, it just
        // controls whether the consumer reads them.
        private static readonly object s_configHashLock = new object();
        private static byte[] s_cachedConfigurationHash;

        /// <summary>
        /// Fits a Catmull-Rom spline through every eligible ABSOLUTE-frame
        /// ExoPropulsive / ExoBallistic section's frames and stores the result
        /// in <see cref="SectionAnnotationStore"/>. Sections that don't qualify
        /// (RELATIVE, OrbitalCheckpoint, Atmospheric, Surface*, Approach) are
        /// skipped silently — they belong to other rendering paths (HR-7).
        /// </summary>
        internal static void FitAndStorePerSection(Recording rec)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return;

            string recordingId = rec.RecordingId;
            int fitOk = 0;
            int fitFailed = 0;
            int skipped = 0;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (!ShouldFitSection(section))
                {
                    skipped++;
                    continue;
                }

                var sw = Stopwatch.StartNew();
                SmoothingSpline spline = TrajectoryMath.CatmullRomFit.Fit(
                    section.frames,
                    SmoothingConfiguration.Default.Tension,
                    out string failureReason);
                sw.Stop();

                if (!spline.IsValid)
                {
                    fitFailed++;
                    int sampleCount = section.frames != null ? section.frames.Count : 0;
                    ParsekLog.Warn("Pipeline-Smoothing",
                        $"Catmull-Rom fit failed: recordingId={recordingId} sectionIndex={i} " +
                        $"env={section.environment} sampleCount={sampleCount} reason={failureReason ?? "<unknown>"}");
                    continue;
                }

                SectionAnnotationStore.PutSmoothingSpline(recordingId, i, spline);
                fitOk++;
                int knotCount = spline.KnotsUT != null ? spline.KnotsUT.Length : 0;
                int sampleCt = section.frames != null ? section.frames.Count : 0;
                ParsekLog.Info("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "Spline fit: recordingId={0} sectionIndex={1} env={2} sampleCount={3} knotCount={4} fitDurationMs={5}",
                        recordingId, i, section.environment, sampleCt, knotCount, sw.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)));
            }

            ParsekLog.Verbose("Pipeline-Smoothing",
                $"FitAndStorePerSection summary: recordingId={recordingId} sections={rec.TrackSections.Count} " +
                $"fitOk={fitOk} fitFailed={fitFailed} skipped={skipped}");
        }

        /// <summary>
        /// Loads cached splines from <c>&lt;id&gt;.pann</c> when the file is
        /// present and every cache-key field matches the recording's current
        /// state. Otherwise (file missing, version drift, epoch drift,
        /// format drift, configuration-hash drift, or algorithm-stamp drift)
        /// recomputes via <see cref="FitAndStorePerSection"/> and writes the
        /// result back to <c>.pann</c>.
        /// </summary>
        /// <remarks>
        /// Per HR-9 / HR-12, a <c>.pann</c> write failure logs Warn and
        /// continues — the in-memory store is still populated, and the file
        /// is regenerable on the next load.
        /// </remarks>
        internal static void LoadOrCompute(Recording rec, string pannPath)
        {
            if (rec == null) return;

            string recordingId = rec.RecordingId;
            byte[] expectedHash = CurrentConfigurationHash();

            // Phase 1 has no format-version-gated features yet; the table is
            // empty so the load-time Pipeline-Format line still fires for
            // every recording (so future phases that DO have features can
            // populate the same line consistently). HR-9 visibility-of-
            // failure: a dropped log here would leave Phase 7/9 features
            // invisibly degraded.
            bool pannPresent = !string.IsNullOrEmpty(pannPath) && File.Exists(pannPath);
            ParsekLog.Info("Pipeline-Format",
                $"Recording loaded: recordingId={recordingId} formatVersion={rec.RecordingFormatVersion} " +
                $"pannPresent={(pannPresent ? "true" : "false")} degradedFeatures=[]");

            if (pannPresent && PannotationsSidecarBinary.TryProbe(pannPath, out PannotationsSidecarProbe probe)
                && probe.Success
                && probe.Supported)
            {
                string drift = ClassifyDrift(probe, rec, expectedHash);
                if (drift == null)
                {
                    if (PannotationsSidecarBinary.TryRead(pannPath, probe,
                            out List<KeyValuePair<int, SmoothingSpline>> splines,
                            out string readFailure))
                    {
                        for (int i = 0; i < splines.Count; i++)
                        {
                            SectionAnnotationStore.PutSmoothingSpline(
                                recordingId, splines[i].Key, splines[i].Value);
                        }

                        long bytes = SafeFileLength(pannPath);
                        ParsekLog.Verbose("Pipeline-Sidecar",
                            $"Pannotations read OK: recordingId={recordingId} block=SmoothingSplineList " +
                            $"version={probe.BinaryVersion} algStamp={probe.AlgorithmStampVersion} bytes={bytes}");
                        return;
                    }

                    // Mid-stream corruption — treat as a discard-and-recompute
                    // trigger per the read contract.
                    ParsekLog.Info("Pipeline-Sidecar",
                        $"Pannotations payload corrupt: recordingId={recordingId} " +
                        $"reason=payload-corrupt detail={readFailure ?? "<unknown>"} — recomputing");
                    drift = "payload-corrupt";
                }

                ParsekLog.Info("Pipeline-Sidecar",
                    $"Pannotations whole-file invalidation: recordingId={recordingId} reason={drift} " +
                    $"binaryVersion={probe.BinaryVersion} algStamp={probe.AlgorithmStampVersion} " +
                    $"sidecarEpoch={probe.SourceSidecarEpoch} formatVersion={probe.SourceRecordingFormatVersion} " +
                    $"recordingEpoch={rec.SidecarEpoch} recordingFormatVersion={rec.RecordingFormatVersion}");
                ParsekLog.Info("Pipeline-Smoothing",
                    $"Lazy compute: recordingId={recordingId} reason={drift}");
            }
            else
            {
                string drift = pannPresent ? "probe-failed" : "file-missing";
                if (pannPresent)
                {
                    ParsekLog.Info("Pipeline-Sidecar",
                        $"Pannotations probe failed: recordingId={recordingId} reason={drift} path={pannPath}");
                }
                ParsekLog.Info("Pipeline-Smoothing",
                    $"Lazy compute: recordingId={recordingId} reason={drift}");
            }

            // Compute fresh splines and persist to .pann.
            FitAndStorePerSection(rec);
            TryWritePann(rec, pannPath, expectedHash);
        }

        /// <summary>
        /// Commit-time entry point. Fits every eligible section's spline and
        /// writes the <c>.pann</c> sidecar. <c>.prec</c> is NEVER touched by
        /// this method (HR-1).
        /// </summary>
        /// <remarks>
        /// Called by <see cref="RecordingSidecarStore"/> AFTER the trajectory
        /// commit batch applies, so a <c>.pann</c> write failure cannot
        /// roll back the <c>.prec</c> commit. The recording's freshly-bumped
        /// <see cref="Recording.SidecarEpoch"/> is what gets stamped into the
        /// new <c>.pann</c>'s header.
        /// </remarks>
        internal static void PersistAfterCommit(Recording rec, string pannPath)
        {
            if (rec == null || string.IsNullOrEmpty(pannPath))
                return;

            FitAndStorePerSection(rec);
            TryWritePann(rec, pannPath, CurrentConfigurationHash());
        }

        /// <summary>Test-only: clears the in-memory annotation store.</summary>
        internal static void ResetForTesting()
        {
            SectionAnnotationStore.ResetForTesting();
            lock (s_configHashLock)
            {
                s_cachedConfigurationHash = null;
            }
        }

        // ---- helpers ----

        /// <summary>
        /// Returns true iff the section qualifies for Phase 1 smoothing:
        /// ABSOLUTE frame, ExoPropulsive or ExoBallistic environment, with
        /// enough samples to fit a Catmull-Rom spline. Atmospheric / Surface*
        /// belong to later phases (Phase 7 / Phase 4 respectively); RELATIVE
        /// is forbidden by HR-7; OrbitalCheckpoint is analytical and has no
        /// sample frames to fit.
        /// </summary>
        private static bool ShouldFitSection(in TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.Absolute)
                return false;
            if (section.environment != SegmentEnvironment.ExoPropulsive
                && section.environment != SegmentEnvironment.ExoBallistic)
                return false;
            if (section.frames == null
                || section.frames.Count < SmoothingConfiguration.Default.MinSamplesPerSection)
                return false;
            return true;
        }

        /// <summary>
        /// Identifies which cache-key field (or absence) drives the discard
        /// decision. Returns null when every field matches and the file is
        /// safe to read. The token returned is the value we surface in the
        /// <c>Pipeline-Sidecar</c> Info log for L8 — keep the canonical set
        /// stable so log greps and tests can pin to it.
        /// </summary>
        private static string ClassifyDrift(
            PannotationsSidecarProbe probe, Recording rec, byte[] expectedConfigHash)
        {
            if (probe.BinaryVersion != PannotationsSidecarBinary.PannotationsBinaryVersion)
                return "version-drift";
            if (probe.AlgorithmStampVersion != PannotationsSidecarBinary.AlgorithmStampVersion)
                return "alg-stamp-drift";
            if (probe.SourceSidecarEpoch != rec.SidecarEpoch)
                return "epoch-drift";
            if (probe.SourceRecordingFormatVersion != rec.RecordingFormatVersion)
                return "format-drift";
            if (!ByteArraysEqual(probe.ConfigurationHash, expectedConfigHash))
                return "config-hash-drift";
            return null;
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] CurrentConfigurationHash()
        {
            lock (s_configHashLock)
            {
                if (s_cachedConfigurationHash == null)
                    s_cachedConfigurationHash = PannotationsSidecarBinary.ComputeConfigurationHash(
                        SmoothingConfiguration.Default);
                return s_cachedConfigurationHash;
            }
        }

        private static long SafeFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
        }

        /// <summary>
        /// Builds the spline list payload from the in-memory store and writes
        /// it to <paramref name="pannPath"/>. IO failures are caught and logged
        /// at Warn level; they NEVER propagate to the caller (HR-9: regenerable
        /// cache must not abort commit / load).
        /// </summary>
        private static void TryWritePann(Recording rec, string pannPath, byte[] configHash)
        {
            if (rec == null || string.IsNullOrEmpty(pannPath))
                return;

            string recordingId = rec.RecordingId;
            var splines = new List<KeyValuePair<int, SmoothingSpline>>();
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    if (SectionAnnotationStore.TryGetSmoothingSpline(recordingId, i, out SmoothingSpline s)
                        && s.IsValid)
                    {
                        splines.Add(new KeyValuePair<int, SmoothingSpline>(i, s));
                    }
                }
            }

            try
            {
                PannotationsSidecarBinary.Write(
                    pannPath,
                    recordingId,
                    rec.SidecarEpoch,
                    rec.RecordingFormatVersion,
                    configHash,
                    splines);

                long bytes = SafeFileLength(pannPath);
                ParsekLog.Verbose("Pipeline-Sidecar",
                    $"Pannotations write OK: recordingId={recordingId} bytes={bytes} path={pannPath} splineCount={splines.Count}");
            }
            catch (Exception ex)
            {
                // HR-9 / HR-12: regenerable-cache write failure must not abort
                // the user-visible operation. Log Warn and proceed; the next
                // load will lazy-compute again from the recording.
                ParsekLog.Warn("Pipeline-Sidecar",
                    $"Pannotations write failure: recordingId={recordingId} path={pannPath} " +
                    $"ex={ex.GetType().Name}:{ex.Message}");
            }
        }
    }
}
