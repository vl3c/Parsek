using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

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

        // Phase 4: dedup per (recordingId, sectionIndex) so the per-section
        // Pipeline-Frame "lift to inertial decision" Verbose line fires exactly
        // once per fit cycle (not per re-fit during config drift recompute).
        // Cleared by ResetForTesting so the test suite can re-observe the line.
        private static readonly object s_frameDecisionLock = new object();
        private static readonly HashSet<string> s_frameDecisionLogged = new HashSet<string>();

        // Test seam: when set, returned in place of FlightGlobals.Bodies?.Find.
        // xUnit cannot stand up FlightGlobals.Bodies, so the suite injects a
        // CelestialBody factory (typically TestBodyRegistry.ResolveBodyByName)
        // via this hook. Production callers leave the seam null and resolve
        // through FlightGlobals.
        internal static System.Func<string, CelestialBody> BodyResolverForTesting;

        /// <summary>
        /// Fits a Catmull-Rom spline through every eligible ABSOLUTE-frame
        /// ExoPropulsive / ExoBallistic section's frames and stores the result
        /// in <see cref="SectionAnnotationStore"/>. Sections that don't qualify
        /// (RELATIVE, OrbitalCheckpoint, Atmospheric, Surface*, Approach) are
        /// skipped silently — they belong to other rendering paths (HR-7).
        ///
        /// <para>
        /// Phase 4: ExoPropulsive / ExoBallistic sections are lifted to
        /// inertial-longitude space (FrameTag = 1) before fitting; design doc
        /// §6.2 Stage 2 / §18 Phase 4. Body resolution failure for an inertial
        /// section is HR-9 (Pipeline-Frame Warn + skip, no spline stored — the
        /// consumer falls back to the legacy lerp path). Phases 1's body-fixed
        /// path is preserved for future Atmospheric / Surface* eligibility.
        /// </para>
        /// </summary>
        internal static void FitAndStorePerSection(Recording rec)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return;

            string recordingId = rec.RecordingId;

            // HR-10: clear any prior entries for this recording before re-fitting
            // so a recompute (config-hash drift, alg-stamp drift, epoch drift,
            // ineligibility flip, fit-failure-after-prior-success) cannot leak
            // stale splines into the new state. Without this clear, an Add-only
            // PutSmoothingSpline path lets a section that fit previously but
            // becomes ineligible (or fails the fit) keep its old spline forever.
            // Idempotent on first run (no entries yet).
            if (!string.IsNullOrEmpty(recordingId))
                SectionAnnotationStore.RemoveRecording(recordingId);

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

                // Phase 4 frame-tag decision (design doc §6.2). ExoPropulsive
                // / ExoBallistic sections fit in inertial longitude; everything
                // else still routes through body-fixed (currently no eligible
                // sections per ShouldFitSection but the branch is here so
                // future Atmospheric / Surface* enablement is a one-line
                // change).
                bool inertial = section.environment == SegmentEnvironment.ExoPropulsive
                    || section.environment == SegmentEnvironment.ExoBallistic;
                byte frameTag = inertial ? (byte)1 : (byte)0;

                CelestialBody body = null;
                string bodyName = section.frames != null && section.frames.Count > 0
                    ? section.frames[0].bodyName
                    : null;
                if (inertial)
                {
                    body = ResolveBody(bodyName);
                    // Use ReferenceEquals to bypass Unity's overloaded `==`,
                    // which would treat a TestBodyRegistry-built uninitialized
                    // CelestialBody as null. The test seam delivers genuine
                    // CLR objects whose Unity-cached pointer is zero; the
                    // pipeline only needs the managed reference for downstream
                    // dispatch.
                    if (object.ReferenceEquals(body, null))
                    {
                        // HR-9 visibility: missing body is a real failure for
                        // an inertial fit (we can't compute the rotation phase),
                        // not a silent skip. Surface it as Warn and continue —
                        // the consumer will fall through to the legacy lerp.
                        fitFailed++;
                        ParsekLog.Warn("Pipeline-Frame",
                            $"body not found, skipping inertial fit recordingId={recordingId} " +
                            $"sectionIndex={i} bodyName={bodyName ?? "<null>"}");
                        continue;
                    }
                }

                IList<TrajectoryPoint> samplesForFit = section.frames;
                if (inertial)
                    samplesForFit = LiftFramesToInertial(section.frames, body);

                var sw = Stopwatch.StartNew();
                SmoothingSpline spline = TrajectoryMath.CatmullRomFit.Fit(
                    samplesForFit,
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

                spline.FrameTag = frameTag;
                SectionAnnotationStore.PutSmoothingSpline(recordingId, i, spline);
                fitOk++;
                int knotCount = spline.KnotsUT != null ? spline.KnotsUT.Length : 0;
                int sampleCt = section.frames != null ? section.frames.Count : 0;
                ParsekLog.Info("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "Spline fit: recordingId={0} sectionIndex={1} env={2} sampleCount={3} knotCount={4} frameTag={5} body={6} fitDurationMs={7}",
                        recordingId, i, section.environment, sampleCt, knotCount, frameTag, bodyName ?? "<null>", sw.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)));

                // Phase 4 §19.2 Stage 2 log: per-section lift-to-inertial
                // decision, dedup'd so a re-fit during drift recompute does
                // not double-log.
                LogFrameDecisionOnce(recordingId, i, section.environment, frameTag, bodyName);
            }

            ParsekLog.Verbose("Pipeline-Smoothing",
                $"FitAndStorePerSection summary: recordingId={recordingId} sections={rec.TrackSections.Count} " +
                $"fitOk={fitOk} fitFailed={fitFailed} skipped={skipped}");
        }

        /// <summary>
        /// Phase 4 helper. Lifts every body-fixed sample to inertial-longitude
        /// space at the sample's recording UT. The new transient list is what
        /// CatmullRomFit.Fit smooths against; raw section.frames are NOT
        /// mutated (HR-1: recordings are immutable).
        /// </summary>
        private static List<TrajectoryPoint> LiftFramesToInertial(
            IList<TrajectoryPoint> source, CelestialBody body)
        {
            int count = source != null ? source.Count : 0;
            var lifted = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                TrajectoryPoint p = source[i];
                Vector3d li = TrajectoryMath.FrameTransform.LiftToInertial(
                    p.latitude, p.longitude, p.altitude, body, p.ut);
                lifted.Add(new TrajectoryPoint
                {
                    ut = p.ut,
                    latitude = li.x,
                    longitude = li.y,    // inertial-longitude (degrees)
                    altitude = li.z,
                    rotation = p.rotation,
                    velocity = p.velocity,
                    bodyName = p.bodyName,
                    funds = p.funds,
                    science = p.science,
                    reputation = p.reputation,
                });
            }
            return lifted;
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            var seam = BodyResolverForTesting;
            if (seam != null)
                return seam(bodyName);
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == bodyName);
            }
            catch (Exception ex)
            {
                // Surface the swallowed exception so a degenerate FlightGlobals
                // state (e.g. mid-load Bodies list mutation) is diagnosable
                // without crashing the orchestrator. Rate-limited so a save
                // with many bad bodyNames cannot spam KSP.log.
                ParsekLog.VerboseRateLimited("Pipeline-Frame", "resolve-body-exception",
                    string.Format(CultureInfo.InvariantCulture,
                        "ResolveBody threw {0}: {1}", ex.GetType().Name, ex.Message),
                    5.0);
                return null;
            }
        }

        // Cap the dedup set so a long-running session that recomputes many
        // recordings (config drift, algorithm-stamp bumps, repeated
        // re-fits) cannot grow s_frameDecisionLogged unbounded. Realistic
        // ceiling per save is low thousands; the cap is a safety bound, not
        // a tight steady-state limit. When exceeded, the set is cleared so
        // the next round of decisions will re-emit (acceptable: each log
        // line is per (recordingId, sectionIndex), so a flush re-reports
        // already-known decisions but does not produce duplicates within
        // the next bucket).
        private const int FrameDecisionLoggedCap = 4096;

        private static void LogFrameDecisionOnce(string recordingId, int sectionIndex,
            SegmentEnvironment env, byte frameTag, string bodyName)
        {
            string key = recordingId + "|" + sectionIndex.ToString(CultureInfo.InvariantCulture);
            lock (s_frameDecisionLock)
            {
                if (!s_frameDecisionLogged.Add(key))
                    return;
                if (s_frameDecisionLogged.Count >= FrameDecisionLoggedCap)
                {
                    int prevSize = s_frameDecisionLogged.Count;
                    s_frameDecisionLogged.Clear();
                    s_frameDecisionLogged.Add(key);  // preserve current key
                    ParsekLog.Info("Pipeline-Frame",
                        $"Frame-decision dedup set exceeded cap ({prevSize}/{FrameDecisionLoggedCap}); cleared. " +
                        $"Next emissions for already-seen (recordingId, sectionIndex) keys will re-fire.");
                }
            }
            ParsekLog.Verbose("Pipeline-Frame",
                $"Section lift to inertial decision: recordingId={recordingId} sectionIndex={sectionIndex} " +
                $"env={env} frameTag={frameTag} body={bodyName ?? "<null>"}");
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
                && probe.Success)
            {
                // ClassifyDrift handles both the supported-but-stale cases
                // (epoch / format / config-hash / alg-stamp) and the
                // !Supported case (binary version mismatch → version-drift).
                // Surfacing the canonical reason token here is what the §19.2
                // logging contract pins; the legacy "probe-failed" wording
                // hid a real version-drift case behind a generic label.
                string drift = ClassifyDrift(probe, rec, expectedHash);
                if (drift == null && probe.Supported)
                {
                    if (PannotationsSidecarBinary.TryRead(pannPath, probe,
                            out List<KeyValuePair<int, SmoothingSpline>> splines,
                            out string readFailure))
                    {
                        // HR-10: clear any prior in-memory entries for this recording
                        // before populating from the freshly-read .pann so a re-load
                        // (e.g. after a recording was healed and its sidecar rewritten
                        // with a different per-section eligibility set) cannot leave
                        // stale splines behind. The on-disk .pann is the authoritative
                        // source after a successful probe + cache-key match.
                        if (!string.IsNullOrEmpty(recordingId))
                            SectionAnnotationStore.RemoveRecording(recordingId);

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
                    $"recordingEpoch={rec.SidecarEpoch} recordingFormatVersion={rec.RecordingFormatVersion} " +
                    $"foundVersion={probe.BinaryVersion} expectedVersion={PannotationsSidecarBinary.PannotationsBinaryVersion}");
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
                else
                {
                    // L7 (design doc §19.2 Sidecar table): the absent-file path
                    // emits a Pipeline-Sidecar Info line "Pannotations missing
                    // → lazy compute scheduled" alongside the existing
                    // Pipeline-Smoothing "Lazy compute" line. The two serve
                    // distinct concerns — sidecar I/O state vs smoothing-stage
                    // recompute — and grep gates pin both independently.
                    ParsekLog.Info("Pipeline-Sidecar",
                        $"Pannotations missing → lazy compute scheduled recordingId={recordingId} " +
                        $"block=SmoothingSplineList reason=file-missing");
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
            lock (s_frameDecisionLock)
            {
                s_frameDecisionLogged.Clear();
            }
            BodyResolverForTesting = null;
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
        ///
        /// <para>
        /// The canonical reason set (design doc §19.2 / §21.3): <c>version-drift</c>
        /// (binary version unsupported — emitted when <c>probe.Supported == false &amp;&amp;
        /// probe.BinaryVersion != PannotationsSidecarBinary.PannotationsBinaryVersion</c>),
        /// <c>alg-stamp-drift</c>, <c>epoch-drift</c>, <c>format-drift</c>,
        /// <c>config-hash-drift</c>. The orchestrator additionally surfaces
        /// <c>file-missing</c> (for the absent-file path) and
        /// <c>payload-corrupt</c> (for mid-stream read failures) outside this
        /// method, completing the six-token canonical set.
        /// </para>
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
            // Recording-id mismatch: a .pann from a different recording was
            // copied or left in place under our filename. Every other field
            // can match by chance (same epoch, format, alg stamp, config
            // hash), but the spline sections inside index into a different
            // recording's TrackSections list — installing them would render
            // ghosts at wrong-recording positions. .prec already rejects id
            // mismatches at TrajectorySidecarBinary load; .pann mirrors that
            // defense here.
            if (!string.Equals(probe.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                return "recording-id-mismatch";
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
