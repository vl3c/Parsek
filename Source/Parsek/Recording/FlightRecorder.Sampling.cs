using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Physics-frame sampling entry, sampling cadence, high-fidelity windows and sparse-gap
    // diagnostics.
    public partial class FlightRecorder
    {
        internal struct SectionGapStats
        {
            public int FrameCount;
            public double FirstUT;
            public double LastUT;
            public double AverageGapSeconds;
            public double MaxGapSeconds;
            public int LargeGapCount;

            // Subset of LargeGapCount whose bounding samples were BOTH taken off
            // rails (1x or physics warp). Such a gap is a genuine dropped /
            // stalled-sampler signal worth a WARN; a large gap whose either
            // bounding sample was on rails / under rails warp is a
            // structurally-expected jump that belongs at Verbose. When no
            // per-sample rails data is supplied (warpFlags == null), this equals
            // LargeGapCount so the WARN behaviour is unchanged.
            public int LargeGapCountOffRails;

            // Subset of the off-rails large gaps that a designed sampling silence
            // explains: the time left after removing the overlap with the supplied
            // silence windows is within the threshold. Logged, never WARNed.
            public int LargeGapCountInSilence;
        }

        /// <summary>
        /// A UT span during which a recorder took no trajectory samples by design (the
        /// background recorder's out-of-proximity-range silence). A section gap that
        /// overlaps one is only as large as the part of it outside the window.
        /// </summary>
        internal struct SamplingSilenceWindow
        {
            public double StartUT;
            public double EndUT;

            public SamplingSilenceWindow(double startUT, double endUT)
            {
                StartUT = startUT;
                EndUT = endUT;
            }
        }

        /// <summary>
        /// The seconds of the gap [<paramref name="fromUT"/>, <paramref name="toUT"/>] not
        /// covered by any silence window (windows may overlap each other; each is clipped
        /// to the gap and their union is subtracted).
        /// </summary>
        internal static double ComputeGapSecondsOutsideSilences(
            double fromUT, double toUT, IList<SamplingSilenceWindow> silences)
        {
            double gap = toUT - fromUT;
            if (silences == null || silences.Count == 0 || gap <= 0.0)
                return gap;

            var clipped = new List<SamplingSilenceWindow>();
            for (int i = 0; i < silences.Count; i++)
            {
                double a = Math.Max(fromUT, silences[i].StartUT);
                double b = Math.Min(toUT, silences[i].EndUT);
                if (b > a)
                    clipped.Add(new SamplingSilenceWindow(a, b));
            }
            if (clipped.Count == 0)
                return gap;

            clipped.Sort((x, y) => x.StartUT.CompareTo(y.StartUT));
            double covered = 0.0;
            double runStart = clipped[0].StartUT;
            double runEnd = clipped[0].EndUT;
            for (int i = 1; i < clipped.Count; i++)
            {
                if (clipped[i].StartUT <= runEnd)
                {
                    if (clipped[i].EndUT > runEnd)
                        runEnd = clipped[i].EndUT;
                }
                else
                {
                    covered += runEnd - runStart;
                    runStart = clipped[i].StartUT;
                    runEnd = clipped[i].EndUT;
                }
            }
            covered += runEnd - runStart;
            return Math.Max(0.0, gap - covered);
        }

        /// <summary>
        /// Computes inter-sample gap statistics for a closed section.
        /// </summary>
        /// <param name="frames">The section's sampled points (UT-ordered).</param>
        /// <param name="largeGapThresholdSeconds">Gap size above which a gap counts as "large".</param>
        /// <param name="warpFlags">
        /// Optional per-sample rails flags, index-aligned with <paramref name="frames"/>
        /// (flag[i] == true means sample i was committed on rails / under rails warp).
        /// When supplied and aligned, a large gap counts as "off rails" only if BOTH
        /// bounding samples were off rails; gaps touching a rails sample are excluded
        /// from <see cref="SectionGapStats.LargeGapCountOffRails"/>.
        /// When null or length-mismatched, every large gap counts as normal-rate
        /// (conservative -- preserves the unconditional-WARN behaviour).
        /// </param>
        /// <param name="silences">
        /// Optional designed sampling silences. An off-rails large gap whose time outside
        /// them is within the threshold counts in
        /// <see cref="SectionGapStats.LargeGapCountInSilence"/> instead of
        /// <see cref="SectionGapStats.LargeGapCountOffRails"/>.
        /// </param>
        internal static SectionGapStats ComputeSectionGapStats(
            IList<TrajectoryPoint> frames,
            double largeGapThresholdSeconds = SparseSectionGapWarningThresholdSeconds,
            IList<bool> warpFlags = null,
            IList<SamplingSilenceWindow> silences = null)
        {
            var stats = new SectionGapStats
            {
                FrameCount = frames?.Count ?? 0,
                FirstUT = double.NaN,
                LastUT = double.NaN,
                AverageGapSeconds = 0.0,
                MaxGapSeconds = 0.0,
                LargeGapCount = 0,
                LargeGapCountOffRails = 0
            };

            if (frames == null || frames.Count == 0)
                return stats;

            stats.FirstUT = frames[0].ut;
            stats.LastUT = frames[frames.Count - 1].ut;
            if (frames.Count == 1)
                return stats;

            // Only trust the warp flags when they line up 1:1 with the frames;
            // any mismatch (e.g. a frame-append path that bypassed the parallel
            // list) falls back to "no warp data" so every large gap stays
            // WARN-eligible rather than being silently downgraded.
            bool haveWarpFlags = warpFlags != null && warpFlags.Count == frames.Count;

            double totalGap = 0.0;
            double maxGap = 0.0;
            int gapCount = 0;
            int largeGapCount = 0;
            int largeGapCountOffRails = 0;
            int largeGapCountInSilence = 0;
            for (int i = 1; i < frames.Count; i++)
            {
                double gap = frames[i].ut - frames[i - 1].ut;
                if (gap < 0.0)
                    continue;

                totalGap += gap;
                gapCount++;
                if (gap > maxGap)
                    maxGap = gap;
                if (gap > largeGapThresholdSeconds)
                {
                    largeGapCount++;
                    bool gapTouchesRails = haveWarpFlags && (warpFlags[i - 1] || warpFlags[i]);
                    if (!gapTouchesRails)
                    {
                        double unexplained = ComputeGapSecondsOutsideSilences(
                            frames[i - 1].ut, frames[i].ut, silences);
                        if (unexplained > largeGapThresholdSeconds)
                            largeGapCountOffRails++;
                        else
                            largeGapCountInSilence++;
                    }
                }
            }

            stats.AverageGapSeconds = gapCount > 0 ? totalGap / gapCount : 0.0;
            stats.MaxGapSeconds = maxGap;
            stats.LargeGapCount = largeGapCount;
            stats.LargeGapCountOffRails = largeGapCountOffRails;
            stats.LargeGapCountInSilence = largeGapCountInSilence;
            return stats;
        }

        /// <summary>
        /// Decides whether a closed TrackSection's large inter-sample gaps are
        /// worth a WARN or are a structurally-expected condition that belongs at
        /// Verbose.
        ///
        /// Rails warp and on-rails recording produce large UT jumps by design: the
        /// vessel is packed, dense sampling is impossible and the gap is harmless,
        /// not data loss. Physics warp is different: every physics frame still runs
        /// (<c>TimeWarp.updateRate</c> sets <c>fixedDeltaTime = 0.02 * rate</c>) and
        /// the sampler keeps its bounds to within one 0.08 s frame at 4x, so a large
        /// physics-warp gap is the same stalled-sampler signal as a 1x one. The
        /// threshold (<see cref="ResolveSparseGapWarningThreshold"/>) already exceeds
        /// the max interval plus one 4x frame for every max interval (1.5x the max,
        /// floored at 0.5 s), so an on-schedule physics-warp gap never reaches it.
        ///
        /// Classification is per-gap, NOT per-section: a single section can hold
        /// both a real off-rails gap and a rails-touching gap. We WARN whenever at
        /// least one large gap had BOTH bounding samples off rails
        /// (<paramref name="largeGapCountOffRails"/> &gt; 0), and downgrade to
        /// Verbose only when every large gap touched a rails sample.
        ///
        /// Pure static so the recorder hot path stays a one-line call and the
        /// decision is unit-testable. Returns true to WARN, false to log Verbose.
        /// </summary>
        internal static bool ShouldWarnOnSparseSampling(int largeGapCountOffRails)
        {
            return largeGapCountOffRails > 0;
        }

        /// <summary>
        /// Resolves the inter-sample-gap threshold above which a closed
        /// TrackSection's gaps are treated as "large" (BUG-D, 2026-06-07 career
        /// playtest). The adaptive sampler's max-interval backstop
        /// (<see cref="ParsekSettings.GetMaxSampleInterval"/>: 8.0s Low / 3.0s
        /// Medium / 1.0s High) is the longest gap the recorder produces BY DESIGN
        /// during a low-dynamics coast -- <see cref="TrajectoryMath.ShouldRecordPoint"/>
        /// only forces a sample once <c>elapsed &gt;= maxInterval</c>. The legacy
        /// hard-coded 0.50s floor sat 6x below the Medium backstop, so every coast
        /// (and every vessel sitting on the pad) tripped the "sparse sampling" WARN
        /// even though the sampler was behaving exactly as configured. Deriving the
        /// threshold from the configured backstop (with a margin for physics-frame
        /// quantization and frame-budget jitter) means only a gap that meaningfully
        /// EXCEEDS the backstop -- i.e. the heartbeat itself failed (stalled sampler
        /// / severe lag) -- counts as large.
        ///
        /// Based on the configured (non-reduced) backstop, which is the upper bound
        /// on a legitimate gap. A section recorded entirely under a high-fidelity /
        /// proximity window samples DENSER than this, so this errs toward fewer
        /// false WARNs at the cost of possibly missing a stall inside a short HF
        /// window -- the conservative direction for a noise-reduction change.
        ///
        /// Pure static so the recorder hot path stays a one-line call and the
        /// decision is unit-testable.
        /// </summary>
        internal static double ResolveSparseGapWarningThreshold(float configuredMaxSampleInterval)
        {
            if (float.IsNaN(configuredMaxSampleInterval)
                || float.IsInfinity(configuredMaxSampleInterval)
                || configuredMaxSampleInterval <= 0f)
                return SparseSectionGapWarningThresholdSeconds;
            return Math.Max(
                SparseSectionGapWarningThresholdSeconds,
                configuredMaxSampleInterval * SparseGapHeartbeatMarginFactor);
        }

        internal static bool IsHighFidelitySamplingActive(double currentUT, double highFidelityUntilUT)
        {
            return !double.IsNaN(highFidelityUntilUT)
                && !double.IsInfinity(highFidelityUntilUT)
                && currentUT <= highFidelityUntilUT;
        }

        internal static bool IsHighFidelityProximityActive(double distanceMeters)
        {
            return !double.IsNaN(distanceMeters)
                && !double.IsInfinity(distanceMeters)
                && distanceMeters <= HighFidelityProximityRangeMeters;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool IsFiniteVector3d(Vector3d value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        internal static double SelectNearestHighFidelityProximityMeters(
            double relativeAnchorDistanceMeters,
            double splitChildDistanceMeters)
        {
            bool hasRelative = IsFinite(relativeAnchorDistanceMeters);
            bool hasSplitChild = IsFinite(splitChildDistanceMeters);
            if (hasRelative && hasSplitChild)
                return Math.Min(relativeAnchorDistanceMeters, splitChildDistanceMeters);
            if (hasRelative)
                return relativeAnchorDistanceMeters;
            if (hasSplitChild)
                return splitChildDistanceMeters;
            return double.NaN;
        }

        internal static bool IsHighFidelitySamplingActive(
            double currentUT,
            double highFidelityUntilUT,
            double proximityDistanceMeters)
        {
            return IsHighFidelitySamplingActive(currentUT, highFidelityUntilUT)
                || IsHighFidelityProximityActive(proximityDistanceMeters);
        }

        internal static ProximitySamplingTier ResolveActiveReFlyTreeSamplingCadence(
            string activeRecordingId,
            string activeTreeId,
            ReFlySessionMarker marker,
            double currentUT,
            double proximityDistanceMeters,
            out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(activeRecordingId))
            {
                reason = "active-recording-id-missing";
                return ProximitySamplingTier.None;
            }
            if (string.IsNullOrEmpty(activeTreeId))
            {
                reason = "active-tree-id-missing";
                return ProximitySamplingTier.None;
            }
            if (marker == null)
            {
                reason = "marker-missing";
                return ProximitySamplingTier.None;
            }
            if (string.IsNullOrEmpty(marker.SessionId))
            {
                reason = "marker-session-missing";
                return ProximitySamplingTier.None;
            }
            if (string.IsNullOrEmpty(marker.TreeId))
            {
                reason = "marker-tree-missing";
                return ProximitySamplingTier.None;
            }
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
            {
                reason = "marker-active-recording-missing";
                return ProximitySamplingTier.None;
            }
            if (!IsFinite(currentUT))
            {
                reason = "ut-non-finite";
                return ProximitySamplingTier.None;
            }
            if (!string.Equals(activeTreeId, marker.TreeId, StringComparison.Ordinal))
            {
                reason = "tree-mismatch";
                return ProximitySamplingTier.None;
            }

            ProximitySamplingTier tier = ProximitySamplingCadence.Resolve(
                proximityDistanceMeters,
                ReFlyTreeFullFidelityProximityRangeMeters,
                ReFlyTreeHalfFidelityProximityRangeMeters,
                out string proximityReason);
            switch (proximityReason)
            {
                case "full":
                    reason = "active-refly-tree-full";
                    break;
                case "half":
                    reason = "active-refly-tree-half";
                    break;
                case "distance-missing":
                    reason = "proximity-missing";
                    break;
                default:
                    reason = "proximity-out-of-range";
                    break;
            }
            return tier;
        }

        internal static float ResolveEffectiveMinSampleInterval(bool highFidelityActive, float configuredMin)
        {
            return configuredMin;
        }

        internal static float ResolveEffectiveMinSampleInterval(
            ProximitySamplingTier reFlyTreeTier,
            ProximitySamplingTier debrisTier,
            bool highFidelityActive,
            float configuredMin,
            float configuredMax)
        {
            if (reFlyTreeTier != ProximitySamplingTier.None)
                return ProximitySamplingCadence.ResolveSampleInterval(
                    reFlyTreeTier,
                    configuredMin,
                    configuredMax);
            if (debrisTier != ProximitySamplingTier.None)
                return ProximitySamplingCadence.ResolveSampleInterval(
                    debrisTier,
                    configuredMin,
                    configuredMax);
            return ResolveEffectiveMinSampleInterval(highFidelityActive, configuredMin);
        }

        internal static float ResolveEffectiveMaxSampleInterval(
            bool highFidelityActive,
            float configuredMax,
            float configuredMin)
        {
            return highFidelityActive
                ? Math.Min(configuredMax, Math.Max(0f, configuredMin))
                : configuredMax;
        }

        internal static float ResolveEffectiveMaxSampleInterval(
            ProximitySamplingTier reFlyTreeTier,
            ProximitySamplingTier debrisTier,
            bool highFidelityActive,
            float configuredMax,
            float configuredMin)
        {
            if (reFlyTreeTier != ProximitySamplingTier.None)
                return ProximitySamplingCadence.ResolveSampleInterval(
                    reFlyTreeTier,
                    configuredMin,
                    configuredMax);
            if (debrisTier != ProximitySamplingTier.None)
                return ProximitySamplingCadence.ResolveSampleInterval(
                    debrisTier,
                    configuredMin,
                    configuredMax);
            return ResolveEffectiveMaxSampleInterval(
                highFidelityActive,
                configuredMax,
                configuredMin);
        }

        internal static double ResolveHighFidelitySamplingWindowSeconds(float configuredMax)
        {
            return float.IsNaN(configuredMax) || float.IsInfinity(configuredMax) || configuredMax < 0f
                ? 0.0
                : configuredMax;
        }

        private void ActivateHighFidelitySampling(double eventUT, string reason)
        {
            if (double.IsNaN(eventUT) || double.IsInfinity(eventUT))
                return;

            double windowSeconds = ResolveHighFidelitySamplingWindowSeconds(maxSampleInterval);
            double untilUT = eventUT + windowSeconds;
            bool extendsWindow = double.IsNaN(highFidelitySamplingUntilUT)
                || untilUT > highFidelitySamplingUntilUT + 0.001;
            highFidelitySamplingUntilUT = Math.Max(
                double.IsNaN(highFidelitySamplingUntilUT) ? double.MinValue : highFidelitySamplingUntilUT,
                untilUT);
            highFidelitySamplingReason = reason ?? "unknown";

            if (extendsWindow)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Recorder",
                    $"High-fidelity sampling window active: reason={highFidelitySamplingReason} " +
                    $"eventUT={eventUT.ToString("F2", ic)} " +
                    $"untilUT={highFidelitySamplingUntilUT.ToString("F2", ic)} " +
                    $"windowSeconds={windowSeconds.ToString("F3", ic)} " +
                    $"proximityRange={HighFidelityProximityRangeMeters.ToString("F1", ic)}m " +
                    $"intervalPolicy=configured-min-sample-interval");
            }
        }

        internal void RegisterHighFidelityProximityVessel(uint vesselPid, double eventUT, string reason)
        {
            if (vesselPid == 0u)
                return;

            bool added = highFidelityProximityVesselPids.Add(vesselPid);
            ActivateHighFidelitySampling(eventUT, reason ?? "split-proximity");
            if (added)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Recorder",
                    $"High-fidelity proximity target registered: pid={vesselPid} " +
                    $"reason={reason ?? "split-proximity"} " +
                    $"eventUT={eventUT.ToString("F2", ic)} " +
                    $"range={HighFidelityProximityRangeMeters.ToString("F1", ic)}m");
            }
        }

        /// <summary>
        /// Called by the Harmony postfix on each physics frame for the active vessel.
        /// </summary>
        public void OnPhysicsFrame(Vessel v)
        {
            if (!IsRecording) return;
            if (isOnRails) return;

            if (v.persistentId != RecordingVesselId)
            {
                if (HandleVesselSwitchDuringRecording(v))
                    return;
            }

            // The trajectory sample's UT and position must describe one physics step; see
            // PostPhysicsPoseCache for why a live read inside FixedUpdate does not. The
            // boundary checks below sample the live vessel, so a FixedUpdate callback leaves
            // them to the Update-step callback at the end of the same render frame.
            bool inFixedTimeStep = PostPhysicsPoseCache.ReadInFixedTimeStep();
            bool statsRunInUpdate = PostPhysicsPoseCache.ReadStatsRunInUpdate();
            bool deferBoundaryChecks = PostPhysicsPoseCache.ShouldDeferBoundaryChecksInFixedStep(
                inFixedTimeStep, statsRunInUpdate, v.packed);

            // Check atmosphere / altitude boundary (before part state polling)
            if (!deferBoundaryChecks)
            {
                CheckAtmosphereBoundary(v);
                CheckAltitudeBoundary(v);
            }

            PollPartStates(v);
            // M2 harvest poll (plan D4): threshold-crossing window open/close.
            // Also consumes the rails-exit pending flag - this is the funnel
            // through which a warp-period converter toggle gets attributed at
            // the rails-exit boundary (round-2 nit 8).
            PollHarvestActivity(v);
            if (!deferBoundaryChecks)
            {
                UpdateEnvironmentTracking(v);
                UpdateAnchorDetection(v);
            }
            RefreshFinalizationCache(v, "periodic");

            double liveUT = Planetarium.GetUniversalTime();
            if (ShouldHoldReFlyPostLoadSettle(v, liveUT))
                return;

            bool hasPose = PostPhysicsPoseCache.TryGetLatest(out PostPhysicsPose pose);
            PhysicsSampleSource sampleSource = PostPhysicsPoseCache.ResolveSampleSource(
                inFixedTimeStep,
                statsRunInUpdate,
                v.packed,
                isRelativeMode,
                hasPose,
                pose,
                v.persistentId,
                v.mainBody != null ? v.mainBody.name : null,
                liveUT,
                PostPhysicsPoseCache.ReadPhysicsStepGameSeconds(),
                out string sampleSourceReason);
            NotePhysicsSampleSource(sampleSource, sampleSourceReason, inFixedTimeStep, liveUT, pose);
            if (sampleSource == PhysicsSampleSource.Skip)
                return;
            bool usePose = sampleSource == PhysicsSampleSource.PostPhysicsPose;
            double currentUT = usePose ? pose.UT : liveUT;

            Vector3 currentVelocity = SampleCurrentVelocity(v);
            if (v.packed)
            {
                // OnPhysicsFrame normally runs off-rails, but a packed vessel occasionally
                // tick-enters this path during pack/unpack transitions. Log so the shift
                // from rb_velocityD+Krakensbane to obt_velocity is observable in logs.
                ParsekLog.VerboseRateLimited("Recorder", "onphysicsframe-packed",
                    $"OnPhysicsFrame sample with packed vessel at ut={currentUT:F2}; " +
                    $"using obt_velocity instead of rb_velocityD+Krakensbane",
                    5.0);
            }
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            double splitChildProximityMeters = ResolveHighFidelitySplitChildProximityMeters(
                v,
                out string splitChildProximitySource);
            double highFidelityProximityMeters = SelectNearestHighFidelityProximityMeters(
                isRelativeMode ? currentAnchorDistanceMeters : double.NaN,
                splitChildProximityMeters);
            string highFidelityProximitySource = ResolveHighFidelityProximitySource(
                splitChildProximityMeters,
                splitChildProximitySource);
            bool highFidelityActive = IsHighFidelitySamplingActive(
                currentUT,
                highFidelitySamplingUntilUT,
                highFidelityProximityMeters);
            ProximitySamplingTier reFlyTreeSamplingCadence =
                ResolveActiveReFlyTreeSamplingCadence(
                    ActiveTree?.ActiveRecordingId,
                    ActiveTree?.Id,
                    ParsekScenario.Instance?.ActiveReFlySessionMarker,
                    currentUT,
                    reFlyTreeSamplingProximityMeters,
                    out string reFlyRecordingSamplingReason);
            bool reFlyRecordingSamplingActive =
                reFlyTreeSamplingCadence != ProximitySamplingTier.None;
            if (reFlyRecordingSamplingActive)
                NoteActiveReFlyTreeSamplingCadence(
                    ref highFidelityActive,
                    reFlyTreeSamplingCadence,
                    reFlyRecordingSamplingReason);
            string highFidelityReason = reFlyRecordingSamplingActive
                    ? reFlyRecordingSamplingReason
                    : highFidelitySamplingReason;
            ResolveEffectiveSampleIntervals(
                reFlyTreeSamplingCadence,
                highFidelityActive,
                out float effectiveMinSampleInterval,
                out float effectiveMaxSampleInterval);
            bool motionTriggered = TrajectoryMath.ShouldRecordPoint(
                currentVelocity,
                lastRecordedVelocity,
                currentUT,
                lastRecordedUT,
                effectiveMinSampleInterval,
                effectiveMaxSampleInterval,
                velocityDirThreshold,
                speedChangeThreshold);
            bool attitudeTriggered = ShouldRecordAttitudePoint(
                currentWorldRotation,
                lastRecordedWorldRotation,
                currentUT,
                lastRecordedUT,
                hasLastRecordedWorldRotation,
                effectiveMinSampleInterval,
                attitudeSampleThresholdDegrees);

            if (!motionTriggered && !attitudeTriggered && !physicsSoiSamplePending)
            {
                LogPhysicsSampleSkipped(
                    currentUT,
                    highFidelityActive,
                    highFidelityReason,
                    highFidelityProximityMeters,
                    highFidelityProximitySource,
                    effectiveMinSampleInterval,
                    effectiveMaxSampleInterval);
                return;
            }

            TrajectoryPoint point = BuildTrajectoryPoint(v, currentVelocity, currentUT);
            if (usePose)
            {
                point.latitude = pose.Latitude;
                point.longitude = pose.Longitude;
                point.altitude = pose.Altitude;
            }
            // One section holds one body: a sample on a new body (an off-rails SOI change)
            // closes the section here and becomes the next section's first frame.
            SplitSectionIfPhysicsBodyChanged(v, point);
            ClearPhysicsSoiSamplePending();
            TrajectoryPoint absolutePoint = point;

            bool relativeApplied = ApplyRelativeOffset(ref point, v);
            CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
        }

        /// <summary>
        /// Names the proximity source the high-fidelity sampler reads this frame: the split
        /// child when its distance is finite and no nearer Relative anchor distance applies,
        /// else "relative-anchor" in relative mode, else "(none)".
        /// </summary>
        internal string ResolveHighFidelityProximitySource(
            double splitChildProximityMeters,
            string splitChildProximitySource)
        {
            return IsFinite(splitChildProximityMeters)
                && (!isRelativeMode
                    || !IsFinite(currentAnchorDistanceMeters)
                    || splitChildProximityMeters <= currentAnchorDistanceMeters)
                    ? splitChildProximitySource
                    : isRelativeMode ? "relative-anchor" : "(none)";
        }

        /// <summary>
        /// An active Re-Fly tree sampling cadence forces high-fidelity sampling; logs the
        /// cadence, its reason and the configured intervals (rate-limited per recording).
        /// </summary>
        internal void NoteActiveReFlyTreeSamplingCadence(
            ref bool highFidelityActive,
            ProximitySamplingTier reFlyTreeSamplingCadence,
            string reFlyRecordingSamplingReason)
        {
            var ic = CultureInfo.InvariantCulture;
            highFidelityActive = true;
            ParsekLog.VerboseRateLimited("Recorder",
                "active-refly-sampling|" + (ActiveTree?.ActiveRecordingId ?? "<no-rec>"),
                $"Active Re-Fly tree sampling cadence active: " +
                $"recordingId={ActiveTree?.ActiveRecordingId ?? "(none)"} " +
                $"treeId={ActiveTree?.Id ?? "(none)"} " +
                $"cadence={reFlyTreeSamplingCadence} " +
                $"reason={reFlyRecordingSamplingReason ?? "(none)"} " +
                $"proximity={reFlyTreeSamplingProximityMeters.ToString("F1", ic)}m " +
                $"proximitySource={reFlyTreeSamplingProximitySource ?? "(none)"} " +
                $"fullRange={ReFlyTreeFullFidelityProximityRangeMeters.ToString("F1", ic)}m " +
                $"halfRange={ReFlyTreeHalfFidelityProximityRangeMeters.ToString("F1", ic)}m " +
                $"configuredMin={minSampleInterval.ToString("F3", ic)}s " +
                $"configuredMax={maxSampleInterval.ToString("F3", ic)}s",
                5.0);
        }

        /// <summary>
        /// The effective min / max sample intervals for this frame's cadence and fidelity.
        /// </summary>
        internal static void ResolveEffectiveSampleIntervals(
            ProximitySamplingTier reFlyTreeSamplingCadence,
            bool highFidelityActive,
            out float effectiveMinSampleInterval,
            out float effectiveMaxSampleInterval)
        {
            effectiveMinSampleInterval = ResolveEffectiveMinSampleInterval(
                reFlyTreeSamplingCadence,
                ProximitySamplingTier.None,
                highFidelityActive,
                minSampleInterval,
                maxSampleInterval);
            effectiveMaxSampleInterval = ResolveEffectiveMaxSampleInterval(
                reFlyTreeSamplingCadence,
                ProximitySamplingTier.None,
                highFidelityActive,
                maxSampleInterval,
                minSampleInterval);
        }

        /// <summary>
        /// The rate-limited line for a physics frame that neither motion, attitude nor a
        /// pending SOI sample triggered.
        /// </summary>
        internal static void LogPhysicsSampleSkipped(
            double currentUT,
            bool highFidelityActive,
            string highFidelityReason,
            double highFidelityProximityMeters,
            string highFidelityProximitySource,
            float effectiveMinSampleInterval,
            float effectiveMaxSampleInterval)
        {
            // Bug #595: the previous 2.0s rate-limit window still produced
            // ~200 lines per 30-min session during stationary intervals.
            // Use the default 5s window — the line conveys "stationary,
            // waiting" which is a steady state, so per-window granularity
            // is plenty.
            var ic = CultureInfo.InvariantCulture;
            string proximityText = double.IsNaN(highFidelityProximityMeters)
                ? "(none)"
                : highFidelityProximityMeters.ToString("F1", ic) + "m";
            ParsekLog.VerboseRateLimited("Recorder", "sample-skipped",
                $"Sample skipped at ut={currentUT.ToString("F2", ic)}; waiting for motion/attitude trigger " +
                $"highFidelity={highFidelityActive} reason={highFidelityReason ?? "(none)"} " +
                $"proximity={proximityText} proximitySource={highFidelityProximitySource} " +
                $"minInterval={effectiveMinSampleInterval.ToString("F3", ic)}s " +
                $"maxInterval={effectiveMaxSampleInterval.ToString("F3", ic)}s");
        }

        /// <summary>
        /// Counts physics-callback sample sources and logs the ones that differ from the
        /// ordinary Update-step read: every FixedUpdate-step callback (a render frame that
        /// held several physics steps) and every Update-step fallback to a live read.
        /// </summary>
        internal void NotePhysicsSampleSource(
            PhysicsSampleSource source,
            string reason,
            bool inFixedTimeStep,
            double liveUT,
            PostPhysicsPose pose)
        {
            if (source == PhysicsSampleSource.Skip)
                physicsSampleSkipCount++;
            else if (source == PhysicsSampleSource.PostPhysicsPose && inFixedTimeStep)
                physicsSampleFixedStepPoseCount++;

            if (!inFixedTimeStep && (source != PhysicsSampleSource.Live || reason == "packed"))
                return;

            var ic = CultureInfo.InvariantCulture;
            string key = inFixedTimeStep ? "physics-sample-fixed-step" : "physics-sample-update-live";
            string prefix = inFixedTimeStep
                ? "Fixed-step physics callback"
                : "Update-step physics callback read live";
            ParsekLog.VerboseRateLimited("Recorder", key,
                $"{prefix}: source={source} reason={reason} " +
                $"liveUT={liveUT.ToString("F4", ic)} " +
                $"poseUT={(pose.VesselPid != 0 ? pose.UT.ToString("F4", ic) : "(none)")} " +
                $"fixedStepPoseSamples={physicsSampleFixedStepPoseCount} " +
                $"skippedCallbacks={physicsSampleSkipCount}",
                5.0);
        }

        private double ResolveHighFidelitySplitChildProximityMeters(Vessel focus, out string source)
        {
            source = "(none)";
            if (focus == null || highFidelityProximityVesselPids.Count == 0)
                return double.NaN;

            if (!TryGetVesselProximityWorldPosition(focus, out Vector3d focusWorld))
                return double.NaN;

            double nearest = double.NaN;
            uint nearestPid = 0u;
            foreach (uint pid in highFidelityProximityVesselPids)
            {
                if (pid == 0u || pid == focus.persistentId)
                    continue;

                Vessel target = FindVesselByPid(pid);
                if (target == null)
                    continue;

                if (!TryGetVesselProximityWorldPosition(target, out Vector3d targetWorld))
                    continue;

                double distanceMeters = Vector3d.Distance(focusWorld, targetWorld);
                if (!IsFinite(distanceMeters))
                    continue;

                if (!IsFinite(nearest) || distanceMeters < nearest)
                {
                    nearest = distanceMeters;
                    nearestPid = pid;
                }
            }

            if (nearestPid != 0u)
                source = "split-child:" + nearestPid.ToString(CultureInfo.InvariantCulture);
            return nearest;
        }

        private static bool TryGetVesselProximityWorldPosition(Vessel vessel, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (vessel == null)
                return false;

            if (vessel.rootPart != null && vessel.rootPart.transform != null)
            {
                worldPos = vessel.rootPart.transform.position;
                return IsFinite(worldPos.x) && IsFinite(worldPos.y) && IsFinite(worldPos.z);
            }

            try
            {
                worldPos = vessel.GetWorldPos3D();
                return IsFinite(worldPos.x) && IsFinite(worldPos.y) && IsFinite(worldPos.z);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder",
                    "high-fidelity-proximity-vessel-position-failed|" + vessel.persistentId,
                    $"High-fidelity proximity target position failed: pid={vessel.persistentId} reason={ex.Message}",
                    5.0);
                return false;
            }
        }

        /// <summary>
        /// Polls all part-module states (parachutes, jettisons, engines, RCS, deployables,
        /// ladders, animation groups, aero/control surfaces, lights, gear, cargo bays,
        /// fairings, robotics, converters, wheel damage, EVA). Runs every physics frame before adaptive sampling.
        /// </summary>
        private void PollPartStates(Vessel v)
        {
            CheckParachuteState(v);
            CheckJettisonState(v);
            CheckEngineState(v);
            CheckRcsState(v);
            CheckDeployableState(v);
            CheckLadderState(v);
            CheckAnimationGroupState(v);
            CheckAeroSurfaceState(v);
            CheckControlSurfaceState(v);
            CheckRobotArmScannerState(v);
            CheckAnimateHeatState(v);
            CheckAnimateGenericState(v);
            CheckLightState(v);
            CheckGearState(v);
            CheckCargoBayState(v);
            CheckFairingState(v);
            CheckRoboticState(v);
            CheckConverterState(v);
            CheckWheelDamageState(v);
            CheckEvaState(v);
        }
    }
}
