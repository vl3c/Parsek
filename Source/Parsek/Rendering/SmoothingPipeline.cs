// [ERS-exempt] Phase 5 co-bubble overlap detection routes through
// RecordingStore.CommittedRecordings at commit time (PersistAfterCommit)
// and at per-trace peer validation (ClassifyTraceDrift on load). ERS
// would filter the active NotCommitted provisional re-fly target out of
// the recording list, hiding live-anchored peers from co-bubble detection
// and preventing per-trace peer signature recomputes for the live side.
// See scripts/ers-els-audit-allowlist.txt for the matching rationale entry.
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
        // Cached configuration hash. Computed once per (config,
        // useAnchorTaxonomy, useCoBubbleBlend) tuple. The `useSmoothingSplines`
        // toggle is intentionally NOT in the hash — toggling it does not
        // invalidate fitted splines, it just controls whether the consumer
        // reads them. The `useAnchorTaxonomy` toggle IS in the hash (Phase 6
        // follow-up, ultrareview P1-A): the candidate writer emits an empty
        // AnchorCandidatesList when the flag is off, and a populated one when
        // on, so a stale cached hash would false-positive against a freshly-
        // flipped flag and let the load path skip the lazy-recompute. The
        // `useCoBubbleBlend` toggle (Phase 5) is in the hash for the same
        // reason: the writer emits an empty CoBubbleOffsetTraces block when
        // the flag is off, populated when on. The hash is keyed on the flag
        // values so a flip blows out the cache, and ClassifyDrift compares
        // the probed file's hash against the current one — flag flip →
        // drift → discard + recompute.
        private static readonly object s_configHashLock = new object();
        private static byte[] s_cachedConfigurationHash;
        private static bool s_cachedConfigurationHashAnchorFlag;
        private static bool s_cachedConfigurationHashCoBubbleFlag;

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

        // Phase 6 test seam: tree resolver for AnchorCandidateBuilder. xUnit
        // cannot stand up RecordingStore.CommittedTrees in every test, so the
        // suite injects a (recordingId -> RecordingTree) closure here.
        // Production callers leave the seam null and the pipeline resolves via
        // RecordingStore directly.
        internal static System.Func<string, RecordingTree> TreeResolverForTesting;

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

            // Phase 6: emit anchor candidates for the same recording. The
            // builder is a pure function, gated internally by the
            // useAnchorTaxonomy flag. Resolved on the same call site as
            // spline-fit so loaders and committers see candidates and splines
            // populate / persist together.
            RecordingTree tree = ResolveTree(recordingId);
            AnchorCandidateBuilder.BuildAndStorePerSection(rec, tree);
        }

        private static RecordingTree ResolveTree(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var seam = TreeResolverForTesting;
            if (seam != null) return seam(recordingId);
            try
            {
                List<RecordingTree> trees = RecordingStore.CommittedTrees;
                if (trees == null) return null;
                for (int i = 0; i < trees.Count; i++)
                {
                    RecordingTree t = trees[i];
                    if (t == null || t.Recordings == null) continue;
                    if (t.Recordings.ContainsKey(recordingId)) return t;
                }
            }
            catch
            {
                // Mid-load mutation of CommittedTrees, etc. Treat as
                // tree-not-resolved; the candidate builder still runs but
                // emits no Dock/Split candidates.
            }
            return null;
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
        /// <param name="treeLocalLoadSet">
        /// Optional dictionary of recordings being loaded as part of the same
        /// tree hydration pass. Phase 5 P1-A fix: <c>RecordingStore</c> hydrates
        /// tree sidecars BEFORE <c>FinalizeTreeCommit</c> appends to
        /// <see cref="RecordingStore.CommittedRecordings"/>, so the per-trace
        /// peer validator could not see same-tree peers and dropped valid
        /// traces as <c>peer-missing</c>. The caller (recording-sidecar load)
        /// passes the in-progress tree's <c>Recordings</c> map here so peer
        /// resolution checks the tree-local set first, falling back to
        /// <c>CommittedRecordings</c> when null.
        /// </param>
        /// <remarks>
        /// Per HR-9 / HR-12, a <c>.pann</c> write failure logs Warn and
        /// continues — the in-memory store is still populated, and the file
        /// is regenerable on the next load.
        /// </remarks>
        internal static void LoadOrCompute(
            Recording rec,
            string pannPath,
            IReadOnlyDictionary<string, Recording> treeLocalLoadSet = null)
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
                            out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates,
                            out List<CoBubbleOffsetTrace> coBubbleTraces,
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
                        for (int i = 0; i < anchorCandidates.Count; i++)
                        {
                            SectionAnnotationStore.PutAnchorCandidates(
                                recordingId, anchorCandidates[i].Key, anchorCandidates[i].Value);
                        }
                        // Phase 5: per-trace peer validation (design doc
                        // §17.3.1 "Per-Trace Peer Validation"). Drop only
                        // traces whose peer cache key has drifted; surviving
                        // traces stay in the store. Whole-file drift is
                        // already handled by ClassifyDrift above.
                        int acceptedTraces = 0;
                        int discardedTraces = 0;
                        for (int i = 0; i < coBubbleTraces.Count; i++)
                        {
                            CoBubbleOffsetTrace t = coBubbleTraces[i];
                            // P1-A: pass treeLocalLoadSet so same-tree peers
                            // being hydrated in the same pass are visible
                            // before they're added to CommittedRecordings.
                            // Without this, OnLoad's tree-walk validates
                            // each recording's traces against an
                            // unpopulated CommittedRecordings list and
                            // drops every same-tree trace as peer-missing.
                            string driftReason = ClassifyTraceDrift(t, treeLocalLoadSet);
                            if (driftReason == null)
                            {
                                SectionAnnotationStore.PutCoBubbleTrace(recordingId, t);
                                acceptedTraces++;
                            }
                            else
                            {
                                discardedTraces++;
                                ParsekLog.Info("Pipeline-CoBubble",
                                    $"Per-trace co-bubble invalidation: recordingId={recordingId} " +
                                    $"peerRecordingId={t.PeerRecordingId} reason={driftReason}");
                            }
                        }

                        long bytes = SafeFileLength(pannPath);
                        ParsekLog.Verbose("Pipeline-Sidecar",
                            $"Pannotations read OK: recordingId={recordingId} block=SmoothingSplineList " +
                            $"version={probe.BinaryVersion} algStamp={probe.AlgorithmStampVersion} bytes={bytes} " +
                            $"splineCount={splines.Count} candidateSectionCount={anchorCandidates.Count} " +
                            $"coBubbleTracesAccepted={acceptedTraces} coBubbleTracesDiscarded={discardedTraces}");
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
            // P1-B: regenerate co-bubble traces on the recompute path. Without
            // this, lazy compute (file-missing / drift) rewrites a fresh
            // .pann with an EMPTY CoBubbleOffsetTraces block — saves with a
            // bumped AlgorithmStampVersion or config-hash drift would silently
            // fall back to standalone playback until every recording is
            // recommitted. PersistAfterCommit(rec) at commit time is the
            // happy path; this branch covers load-time freshness (HR-10).
            DetectAndStoreCoBubbleTracesForRecording(rec, treeLocalLoadSet);
            TryWritePann(rec, pannPath, expectedHash);
        }

        /// <summary>
        /// P1-B helper: build the recording-set the co-bubble detector
        /// needs at recompute time. The tree-local load set (during OnLoad)
        /// is the most reliable source — same-tree peers that aren't yet in
        /// <see cref="RecordingStore.CommittedRecordings"/> ARE visible
        /// here, mirroring the P1-A peer-resolver fix. We additionally
        /// merge in the committed list so cross-tree peers are scanned too,
        /// and ensure <paramref name="rec"/> itself is included even when
        /// neither source contains it (defense-in-depth for unusual call
        /// orderings — e.g. a fresh recording flushed through the lazy
        /// path before its tree commits).
        /// </summary>
        private static void DetectAndStoreCoBubbleTracesForRecording(
            Recording rec,
            IReadOnlyDictionary<string, Recording> treeLocalLoadSet)
        {
            try
            {
                var snapshot = new List<Recording>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (treeLocalLoadSet != null)
                {
                    foreach (var kv in treeLocalLoadSet)
                    {
                        Recording r = kv.Value;
                        if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                        if (seen.Add(r.RecordingId)) snapshot.Add(r);
                    }
                }
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                        if (seen.Add(r.RecordingId)) snapshot.Add(r);
                    }
                }
                if (rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                    && seen.Add(rec.RecordingId))
                {
                    snapshot.Add(rec);
                }
                if (snapshot.Count >= 2)
                {
                    CoBubbleOverlapDetector.DetectAndStore(snapshot);
                }
                else
                {
                    ParsekLog.Verbose("Pipeline-CoBubble",
                        $"DetectAndStoreCoBubbleTracesForRecording: skip recordingId={rec?.RecordingId} " +
                        $"reason=fewer-than-two-recordings snapshotCount={snapshot.Count}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Pipeline-CoBubble",
                    $"DetectAndStore on recompute threw {ex.GetType().Name}: {ex.Message} — " +
                    $"co-bubble traces unavailable for this recompute (recordingId={rec?.RecordingId})");
            }
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
            // Phase 5: detect co-bubble overlaps against every other
            // committed recording at commit time. The detector gates
            // internally on useCoBubbleBlend; off → no traces emitted.
            // HR-9 visible failure: if the detector throws, we log Warn
            // and continue with the spline / candidate persistence.
            // Track peer recordings whose in-memory store gained a trace
            // so we can persist their .pann files too (P2-A).
            var peerRecordingsToPersist = new List<Recording>();
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    // Build a snapshot list including the recording being
                    // persisted (the commit batch may not have appended it
                    // to CommittedRecordings yet). Detector dedupes via
                    // ordinal recording-id sorting.
                    var snapshot = new List<Recording>(committed.Count + 1);
                    bool includedRec = false;
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording other = committed[i];
                        if (other == null) continue;
                        snapshot.Add(other);
                        if (string.Equals(other.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                            includedRec = true;
                    }
                    if (!includedRec) snapshot.Add(rec);
                    CoBubbleOverlapDetector.DetectAndStore(snapshot);

                    // P2-A: DetectAndStore writes traces into BOTH sides of
                    // each pair, but we only persist rec.pann here by
                    // default. If commit time leaves peer .pann files stale
                    // (no trace persisted), the next session's selector may
                    // designate the peer as primary and find no trace at
                    // all — the blend window silently degrades to
                    // standalone. Eagerly persist every peer touched by
                    // this commit's overlap pairs so both sides see the
                    // trace on next load.
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        Recording other = snapshot[i];
                        if (other == null
                            || string.IsNullOrEmpty(other.RecordingId)
                            || string.Equals(other.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                            continue;
                        if (SectionAnnotationStore.TryGetCoBubbleTraces(other.RecordingId, out var peerTraces)
                            && peerTraces != null)
                        {
                            // Only persist when the peer has at least one
                            // trace whose PeerRecordingId == rec.RecordingId.
                            // This narrows the I/O to recordings genuinely
                            // touched by this commit (vs every existing
                            // peer with any prior trace).
                            bool touchedByThisCommit = false;
                            for (int t = 0; t < peerTraces.Count; t++)
                            {
                                CoBubbleOffsetTrace pt = peerTraces[t];
                                if (pt != null
                                    && string.Equals(pt.PeerRecordingId, rec.RecordingId, StringComparison.Ordinal))
                                {
                                    touchedByThisCommit = true;
                                    break;
                                }
                            }
                            if (touchedByThisCommit) peerRecordingsToPersist.Add(other);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Pipeline-CoBubble",
                    $"DetectAndStore at commit threw {ex.GetType().Name}: {ex.Message} — co-bubble traces unavailable for this commit");
            }
            byte[] commitConfigHash = CurrentConfigurationHash();
            TryWritePann(rec, pannPath, commitConfigHash);

            // P2-A: persist peer .pann files for every recording whose
            // in-memory trace store gained a trace pointing at `rec`.
            // PersistPeerPannFiles is wrapped in its own try/catch and
            // never throws (HR-9 visibility / regenerable cache).
            if (peerRecordingsToPersist.Count > 0)
                PersistPeerPannFiles(rec, peerRecordingsToPersist, commitConfigHash);
        }

        /// <summary>
        /// P2-A: writes <c>.pann</c> sidecars for every peer recording whose
        /// in-memory trace store gained an entry pointing at the recording
        /// just committed by <see cref="PersistAfterCommit"/>. Without this,
        /// the freshly-built peer trace lives only in memory; on next load
        /// the peer's <c>.pann</c> still has the pre-commit (or empty)
        /// CoBubbleOffsetTraces block and the runtime sees no trace if the
        /// session selector designates the peer as primary.
        ///
        /// <para>
        /// Per HR-9 / HR-12, peer-side write failures must NOT abort the
        /// caller's commit. Each peer is wrapped independently and surfaces
        /// as a Warn — the .pann is regenerable from the next lazy compute.
        /// </para>
        /// </summary>
        private static void PersistPeerPannFiles(
            Recording committedRec,
            IReadOnlyList<Recording> peers,
            byte[] configHash)
        {
            int persisted = 0;
            int skipped = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                Recording peer = peers[i];
                if (peer == null || string.IsNullOrEmpty(peer.RecordingId)) { skipped++; continue; }
                string peerPannRel = RecordingPaths.BuildAnnotationsRelativePath(peer.RecordingId);
                if (string.IsNullOrEmpty(peerPannRel)) { skipped++; continue; }
                string peerPannPath = RecordingPaths.ResolveSaveScopedPath(peerPannRel);
                if (string.IsNullOrEmpty(peerPannPath)) { skipped++; continue; }
                try
                {
                    TryWritePann(peer, peerPannPath, configHash);
                    persisted++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    ParsekLog.Warn("Pipeline-CoBubble",
                        $"PersistPeerPannFiles: peer write failed recordingId={peer.RecordingId} " +
                        $"committed={committedRec.RecordingId} ex={ex.GetType().Name}:{ex.Message}");
                }
            }
            ParsekLog.Verbose("Pipeline-CoBubble",
                $"PersistPeerPannFiles summary: committed={committedRec.RecordingId} " +
                $"peerCount={peers.Count} persisted={persisted} skipped={skipped}");
        }

        /// <summary>Test-only: clears the in-memory annotation store.</summary>
        internal static void ResetForTesting()
        {
            SectionAnnotationStore.ResetForTesting();
            lock (s_configHashLock)
            {
                s_cachedConfigurationHash = null;
                s_cachedConfigurationHashAnchorFlag = false;
                s_cachedConfigurationHashCoBubbleFlag = false;
            }
            lock (s_frameDecisionLock)
            {
                s_frameDecisionLogged.Clear();
            }
            BodyResolverForTesting = null;
            TreeResolverForTesting = null;
            UseCoBubbleBlendResolverForTesting = null;
            AnchorCandidateBuilder.ResetForTesting();
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

        /// <summary>
        /// Test seam for the peer-recording resolver used by Phase 5
        /// per-trace validation. xUnit injects a closure backed by a
        /// synthetic recording list; production resolves through
        /// <see cref="RecordingStore.CommittedRecordings"/>. Reset via
        /// <see cref="ResetForTesting"/>.
        /// </summary>
        internal static System.Func<string, Recording> CoBubblePeerResolverForTesting;

        /// <summary>
        /// Test seam for the peer content-signature recompute. xUnit can
        /// inject a deterministic mapping; production composes the recompute
        /// through <see cref="CoBubbleOverlapDetector.ComputePeerContentSignature"/>
        /// against the live peer's <see cref="Recording.Points"/>.
        /// </summary>
        internal static System.Func<Recording, double, double, byte[]> CoBubblePeerSignatureRecomputeForTesting;

        /// <summary>
        /// Phase 5 per-trace cache-key check. Returns one of
        /// <c>peer-missing</c> / <c>peer-format-changed</c> /
        /// <c>peer-epoch-changed</c> / <c>peer-content-mismatch</c> when the
        /// peer's current state has drifted from the trace's stored fields.
        /// Returns null when every field matches and the trace is safe to
        /// install in the in-memory store. The whole-file drift check
        /// (binary version, alg stamp, source epoch, source format, config
        /// hash) lives in <see cref="ClassifyDrift"/> and runs before this.
        /// </summary>
        internal static string ClassifyTraceDrift(CoBubbleOffsetTrace trace)
        {
            return ClassifyTraceDrift(trace, null);
        }

        /// <summary>
        /// Phase 5 P1-A overload: per-trace cache-key check that consults a
        /// tree-local load set BEFORE walking <c>RecordingStore.CommittedRecordings</c>.
        /// During <see cref="ParsekScenario"/> OnLoad each recording is
        /// hydrated and its <c>.pann</c> read sequentially, but the tree is
        /// only added to the committed list AFTER all sidecars have loaded.
        /// Without the load set, same-tree peers look missing and valid
        /// traces are dropped as <c>peer-missing</c> on every save load.
        /// </summary>
        internal static string ClassifyTraceDrift(
            CoBubbleOffsetTrace trace,
            IReadOnlyDictionary<string, Recording> treeLocalLoadSet)
        {
            if (trace == null) return "trace-null";
            if (string.IsNullOrEmpty(trace.PeerRecordingId)) return "peer-missing";
            Recording peer = ResolvePeerRecording(trace.PeerRecordingId, treeLocalLoadSet);
            if (peer == null) return "peer-missing";
            if (peer.RecordingFormatVersion != trace.PeerSourceFormatVersion)
                return "peer-format-changed";
            if (peer.SidecarEpoch != trace.PeerSidecarEpoch)
                return "peer-epoch-changed";

            byte[] expected = trace.PeerContentSignature;
            byte[] actual;
            var recomputeSeam = CoBubblePeerSignatureRecomputeForTesting;
            if (recomputeSeam != null)
                actual = recomputeSeam(peer, trace.StartUT, trace.EndUT);
            else
                actual = CoBubbleOverlapDetector.ComputePeerContentSignature(peer, trace.StartUT, trace.EndUT);
            if (actual == null) return "peer-missing";
            if (!ByteArraysEqual(actual, expected)) return "peer-content-mismatch";
            return null;
        }

        private static Recording ResolvePeerRecording(string peerRecordingId)
        {
            return ResolvePeerRecording(peerRecordingId, null);
        }

        private static Recording ResolvePeerRecording(
            string peerRecordingId,
            IReadOnlyDictionary<string, Recording> treeLocalLoadSet)
        {
            if (string.IsNullOrEmpty(peerRecordingId)) return null;
            var seam = CoBubblePeerResolverForTesting;
            if (seam != null) return seam(peerRecordingId);
            // P1-A: prefer the tree-local load set during OnLoad's
            // sequential sidecar hydration. The tree is only appended to
            // CommittedRecordings AFTER every recording's .pann has been
            // read, so same-tree peers are invisible to the committed
            // list when ClassifyTraceDrift runs from inside LoadOrCompute.
            if (treeLocalLoadSet != null)
            {
                Recording localPeer;
                if (treeLocalLoadSet.TryGetValue(peerRecordingId, out localPeer)
                    && localPeer != null)
                {
                    return localPeer;
                }
            }
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed == null) return null;
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording r = committed[i];
                    if (r == null) continue;
                    if (string.Equals(r.RecordingId, peerRecordingId, System.StringComparison.Ordinal))
                        return r;
                }
            }
            catch
            {
                // Mid-load mutation of CommittedRecordings — treat as missing.
            }
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
            // Read the flags through their resolver helpers so the test
            // overrides and production settings flow through one path. The
            // cache is invalidated whenever either flag flips.
            bool anchorFlag = AnchorCandidateBuilder.ResolveUseAnchorTaxonomy();
            bool coBubbleFlag = ResolveUseCoBubbleBlend();
            lock (s_configHashLock)
            {
                if (s_cachedConfigurationHash == null
                    || s_cachedConfigurationHashAnchorFlag != anchorFlag
                    || s_cachedConfigurationHashCoBubbleFlag != coBubbleFlag)
                {
                    s_cachedConfigurationHash = PannotationsSidecarBinary.ComputeConfigurationHash(
                        SmoothingConfiguration.Default, anchorFlag, coBubbleFlag);
                    s_cachedConfigurationHashAnchorFlag = anchorFlag;
                    s_cachedConfigurationHashCoBubbleFlag = coBubbleFlag;
                }
                return s_cachedConfigurationHash;
            }
        }

        /// <summary>
        /// Test seam: when set, returned in place of
        /// <see cref="ParsekSettings.useCoBubbleBlend"/>. xUnit cannot stand
        /// up <see cref="ParsekSettings.Current"/> without
        /// <see cref="HighLogic.CurrentGame"/>; the seam lets tests exercise
        /// the flag without that. Production callers leave it null and read
        /// through <c>ParsekSettings.Current?.useCoBubbleBlend ?? true</c>.
        /// </summary>
        internal static System.Func<bool> UseCoBubbleBlendResolverForTesting;

        /// <summary>
        /// Resolves the Phase 5 <c>useCoBubbleBlend</c> flag through the test
        /// seam first, then through <see cref="ParsekSettings.Current"/>.
        /// Defaults to true when <c>Current</c> is null (matches the shipped
        /// default and Phase 6's <c>useAnchorTaxonomy</c> resolver pattern).
        /// </summary>
        internal static bool ResolveUseCoBubbleBlend()
        {
            var seam = UseCoBubbleBlendResolverForTesting;
            if (seam != null) return seam();
            ParsekSettings settings = ParsekSettings.Current;
            return settings?.useCoBubbleBlend ?? true;
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
            var anchorCandidates = new List<KeyValuePair<int, AnchorCandidate[]>>();
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    if (SectionAnnotationStore.TryGetSmoothingSpline(recordingId, i, out SmoothingSpline s)
                        && s.IsValid)
                    {
                        splines.Add(new KeyValuePair<int, SmoothingSpline>(i, s));
                    }
                    if (SectionAnnotationStore.TryGetAnchorCandidates(recordingId, i, out AnchorCandidate[] cands)
                        && cands != null && cands.Length > 0)
                    {
                        anchorCandidates.Add(new KeyValuePair<int, AnchorCandidate[]>(i, cands));
                    }
                }
            }

            // Phase 5: gather co-bubble traces. Empty when the flag is off
            // (CoBubbleOverlapDetector gates internally so the store will
            // not have populated entries) or when no peers have been
            // detected yet.
            List<CoBubbleOffsetTrace> coBubbleTraces = null;
            if (SectionAnnotationStore.TryGetCoBubbleTraces(recordingId, out var traces) && traces != null)
            {
                coBubbleTraces = new List<CoBubbleOffsetTrace>(traces.Count);
                for (int i = 0; i < traces.Count; i++)
                {
                    CoBubbleOffsetTrace t = traces[i];
                    if (t == null) continue;
                    if (t.UTs == null || t.UTs.Length == 0) continue;
                    if (t.PeerContentSignature == null || t.PeerContentSignature.Length != 32) continue;
                    coBubbleTraces.Add(t);
                }
            }
            int coBubbleCount = coBubbleTraces?.Count ?? 0;

            try
            {
                PannotationsSidecarBinary.Write(
                    pannPath,
                    recordingId,
                    rec.SidecarEpoch,
                    rec.RecordingFormatVersion,
                    configHash,
                    splines,
                    anchorCandidates,
                    coBubbleTraces);

                long bytes = SafeFileLength(pannPath);
                ParsekLog.Verbose("Pipeline-Sidecar",
                    $"Pannotations write OK: recordingId={recordingId} bytes={bytes} path={pannPath} " +
                    $"splineCount={splines.Count} candidateSectionCount={anchorCandidates.Count} " +
                    $"coBubbleTraceCount={coBubbleCount}");
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
