using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Atmosphere / altitude boundary detection, environment classification and hysteresis, and
    // track-section open / close / SOI transitions.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Clears atmosphere-boundary and SOI-change flags without stopping or restarting
        /// the recorder. Used when tree mode intercepts a boundary crossing — the recorder
        /// keeps accumulating data into the tree recording, but the standalone chain-commit
        /// path is suppressed.
        /// </summary>
        public void ClearBoundaryFlags()
        {
            AtmosphereBoundaryCrossed = false;
            EnteredAtmosphere = false;
            atmosphereBoundaryPending = false;
            AltitudeBoundaryCrossed = false;
            DescendedBelowThreshold = false;
            altitudeBoundaryPending = false;
            SoiChangePending = false;
            SoiChangeFromBody = null;
            ParsekLog.Verbose("Recorder", "Boundary flags cleared (tree mode bypass)");
        }

        #region Atmosphere Boundary Detection

        /// <summary>
        /// Pure testable function: determines whether an orbit segment should be skipped
        /// because the vessel is below the atmosphere. Keplerian orbits ignore drag, so
        /// an orbit segment recorded during atmospheric flight produces underground ghost paths.
        /// </summary>
        internal static bool ShouldSkipOrbitSegmentForAtmosphere(
            bool bodyHasAtmosphere, double altitude, double atmosphereDepth)
        {
            return bodyHasAtmosphere && altitude < atmosphereDepth;
        }

        /// <summary>
        /// Pure testable function: determines whether a recording should split at the atmosphere boundary.
        /// Requires both sustained time (hysteresisSeconds) AND distance beyond boundary (hysteresisMeters).
        /// </summary>
        internal static bool ShouldSplitAtAtmosphereBoundary(
            bool wasInAtmo, double altitude, double atmosphereDepth,
            bool pendingCross, double pendingUT, double currentUT,
            double hysteresisSeconds = 3.0, double hysteresisMeters = 1000.0)
        {
            if (atmosphereDepth <= 0) return false; // no atmosphere

            bool nowInAtmo = altitude < atmosphereDepth;
            if (nowInAtmo == wasInAtmo)
                return false; // no boundary crossed

            // Check distance beyond boundary
            double distBeyond = nowInAtmo
                ? (atmosphereDepth - altitude)
                : (altitude - atmosphereDepth);
            if (distBeyond < hysteresisMeters)
                return false; // not far enough past boundary

            // Check sustained time
            if (!pendingCross)
                return false; // first detection — caller should start the timer

            return (currentUT - pendingUT) >= hysteresisSeconds;
        }

        /// <summary>
        /// Returns the approach altitude threshold for an airless body.
        /// Prefers KSP's timeWarpAltitudeLimits[4] (100x warp limit) — KSP's own definition of
        /// "close enough that fast warp is dangerous", which adapts to modded planets automatically.
        /// A warp mod (e.g. BetterTimeWarp) overwrites the live array at MainMenu startup, zeroing
        /// index [4]; <see cref="StockWarpAltitudeLimits"/> snapshots the genuine stock value before
        /// that override, so we prefer the snapshot, then the live array, then a radius fallback
        /// (body.Radius * 0.15 clamped to [5000, 200000]).
        /// WARNING: Callers must check body.atmosphere first — this method does not guard against
        /// atmospheric bodies (which use atmosphere boundary splits instead).
        /// </summary>
        internal static double ComputeApproachAltitude(CelestialBody body)
        {
            if (body == null)
                return ComputeApproachAltitude(0);

            float stockLimit = StockWarpAltitudeLimits.TryGetStockLimit(body.name, 4, out var s)
                ? s : 0f;
            float liveLimit = (body.timeWarpAltitudeLimits != null
                && body.timeWarpAltitudeLimits.Length >= 5)
                ? body.timeWarpAltitudeLimits[4] : 0f;

            double altitude = SelectApproachAltitude(stockLimit, liveLimit, body.Radius, out var source);

            // Surface (rate-limited per body) the case where a mod zeroed the live limit and we
            // recovered the stock value — the in-game proof the hardening took effect.
            if (source == "stock" && liveLimit <= 0f)
            {
                ParsekLog.VerboseRateLimited(StockWarpAltitudeLimits.Tag, body.name,
                    string.Format(CultureInfo.InvariantCulture,
                        "ComputeApproachAltitude({0}): live limit zeroed by a warp mod; using stock {1:F0}m",
                        body.name, altitude));
            }
            return altitude;
        }

        /// <summary>
        /// Pure approach-altitude selection: prefer the captured stock limit, then the live
        /// (possibly mod-overridden) limit, then the radius fallback. A limit of 0 (or negative)
        /// means "unavailable". <paramref name="source"/> is "stock" / "live" / "radius".
        /// </summary>
        internal static double SelectApproachAltitude(
            float stockLimit, float liveLimit, double bodyRadius, out string source)
        {
            if (stockLimit > 0f)
            {
                // Stock-first by design: this split threshold is Parsek's STOCK notion of "close
                // to the surface", so prefer the pre-mod snapshot over the live value — whether a
                // warp mod zeroed it (BTW) or relaxed it, or a rare body mod edited it after the
                // snapshot. The snapshot is taken at PSystemReady, after legit body mods configure
                // their bodies, so this overrides only post-load warp-mod tampering in practice.
                source = "stock";
                return stockLimit;
            }
            if (liveLimit > 0f)
            {
                source = "live";
                return liveLimit;
            }
            source = "radius";
            return ComputeApproachAltitude(bodyRadius);
        }

        /// <summary>
        /// Radius-only fallback for tests and contexts without a CelestialBody reference.
        /// body.Radius * 0.15, clamped to [5000, 200000] meters.
        /// </summary>
        internal static double ComputeApproachAltitude(double bodyRadius)
        {
            double raw = bodyRadius * 0.15;
            if (raw < 5000.0) return 5000.0;
            if (raw > 200000.0) return 200000.0;
            return raw;
        }

        /// <summary>
        /// Pure testable function: determines whether a recording should split at the altitude boundary
        /// on an airless body. Mirrors ShouldSplitAtAtmosphereBoundary.
        /// Requires both sustained time (hysteresisSeconds) AND distance beyond boundary.
        /// If hysteresisMeters is negative, it is computed as max(1000, threshold * 0.02).
        /// </summary>
        internal static bool ShouldSplitAtAltitudeBoundary(
            double altitude, double threshold, bool wasAbove,
            bool pendingCross, double pendingUT, double currentUT,
            double hysteresisSeconds = 3.0, double hysteresisMeters = -1.0)
        {
            if (threshold <= 0) return false;

            if (hysteresisMeters < 0)
                hysteresisMeters = Math.Max(1000.0, threshold * 0.02);

            bool nowAbove = altitude >= threshold;
            if (nowAbove == wasAbove)
                return false; // no boundary crossed

            // Check distance beyond boundary
            double distBeyond = nowAbove
                ? (altitude - threshold)
                : (threshold - altitude);
            if (distBeyond < hysteresisMeters)
                return false; // not far enough past boundary

            // Check sustained time
            if (!pendingCross)
                return false; // first detection — caller should start the timer

            return (currentUT - pendingUT) >= hysteresisSeconds;
        }

        /// <summary>
        /// Called every physics frame to detect atmosphere boundary crossings.
        /// Sets AtmosphereBoundaryCrossed and EnteredAtmosphere when confirmed.
        /// </summary>
        public void CheckAtmosphereBoundary(Vessel v)
        {
            if (!IsRecording || isOnRails) return;
            if (v == null || v.mainBody == null || !v.mainBody.atmosphere) return;

            double altitude = v.altitude;
            double atmoDepth = v.mainBody.atmosphereDepth;
            bool nowInAtmo = altitude < atmoDepth;
            double currentUT = Planetarium.GetUniversalTime();

            if (nowInAtmo == wasInAtmosphere)
            {
                // No boundary — reset pending if we drifted back
                if (atmosphereBoundaryPending)
                    ParsekLog.Verbose("Recorder", $"Atmosphere boundary pending reset — drifted back to same side (inAtmo={nowInAtmo})");
                atmosphereBoundaryPending = false;
                return;
            }

            // Boundary detected — check hysteresis
            if (!atmosphereBoundaryPending)
            {
                // Start the timer
                atmosphereBoundaryPending = true;
                atmosphereBoundaryPendingUT = currentUT;
                ActivateHighFidelitySampling(currentUT, "atmosphere-boundary-pending");
                ParsekLog.Verbose("Recorder", $"Atmosphere boundary detected — starting hysteresis timer " +
                    $"(body={v.mainBody.name}, {(nowInAtmo ? "entering" : "exiting")}, alt={altitude:F0}m, atmoDepth={atmoDepth:F0}m)");
                return;
            }

            if (ShouldSplitAtAtmosphereBoundary(
                wasInAtmosphere, altitude, atmoDepth,
                atmosphereBoundaryPending, atmosphereBoundaryPendingUT, currentUT))
            {
                // Confirmed crossing — force-sample a boundary point to ensure >= 2 points
                SamplePosition(v);

                AtmosphereBoundaryCrossed = true;
                EnteredAtmosphere = nowInAtmo;
                wasInAtmosphere = nowInAtmo;
                atmosphereBoundaryPending = false;
                ActivateHighFidelitySampling(currentUT, "atmosphere-boundary-confirmed");
                ParsekLog.Info("Recorder", $"Atmosphere boundary confirmed: {(nowInAtmo ? "entered" : "exited")} " +
                    $"atmosphere of {v.mainBody.name} at alt {altitude:F0}m " +
                    $"(hysteresis: {(currentUT - atmosphereBoundaryPendingUT):F1}s, {Math.Abs(altitude - atmoDepth):F0}m past boundary)");
            }
        }

        /// <summary>
        /// Reseeds atmosphere state from current vessel. Call on SOI change and off-rails.
        /// </summary>
        private void ReseedAtmosphereState(Vessel v)
        {
            bool oldState = wasInAtmosphere;
            if (v == null || v.mainBody == null)
            {
                wasInAtmosphere = false;
            }
            else
            {
                wasInAtmosphere = v.mainBody.atmosphere && v.altitude < v.mainBody.atmosphereDepth;
            }
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            if (v?.mainBody != null)
            {
                ParsekLog.Verbose("Recorder", $"Atmosphere state reseeded: body={v.mainBody.name}, " +
                    $"inAtmo={wasInAtmosphere} (was {oldState}), " +
                    $"hasAtmo={v.mainBody.atmosphere}, alt={v.altitude:F0}m" +
                    (v.mainBody.atmosphere ? $", atmoDepth={v.mainBody.atmosphereDepth:F0}m" : ""));
            }
        }

        /// <summary>
        /// Called every physics frame to detect altitude boundary crossings on airless bodies.
        /// Sets AltitudeBoundaryCrossed and DescendedBelowThreshold when confirmed.
        /// Mirrors CheckAtmosphereBoundary.
        /// </summary>
        public void CheckAltitudeBoundary(Vessel v)
        {
            if (!IsRecording || isOnRails) return;
            if (v == null || v.mainBody == null) return;
            if (v.mainBody.atmosphere) return; // atmospheric bodies use atmosphere boundary split
            if (currentAltitudeThreshold <= 0) return; // no threshold computed (defensive)

            double altitude = v.altitude;
            double currentUT = Planetarium.GetUniversalTime();
            bool nowAbove = altitude >= currentAltitudeThreshold;

            if (nowAbove == wasAboveAltitudeThreshold)
            {
                // No boundary — reset pending if we drifted back
                if (altitudeBoundaryPending)
                    ParsekLog.Verbose("Recorder", $"Altitude boundary pending reset — drifted back to same side (above={nowAbove})");
                altitudeBoundaryPending = false;
                return;
            }

            // Boundary detected — check hysteresis
            if (!altitudeBoundaryPending)
            {
                // Start the timer
                altitudeBoundaryPending = true;
                altitudeBoundaryPendingUT = currentUT;
                ActivateHighFidelitySampling(currentUT, "altitude-boundary-pending");
                ParsekLog.Verbose("Recorder", $"Altitude boundary detected — starting hysteresis timer " +
                    $"(body={v.mainBody.name}, {(nowAbove ? "ascending" : "descending")}, alt={altitude:F0}m, threshold={currentAltitudeThreshold:F0}m)");
                return;
            }

            if (ShouldSplitAtAltitudeBoundary(
                altitude, currentAltitudeThreshold, wasAboveAltitudeThreshold,
                altitudeBoundaryPending, altitudeBoundaryPendingUT, currentUT))
            {
                // Confirmed crossing — force-sample a boundary point to ensure >= 2 points
                SamplePosition(v);

                AltitudeBoundaryCrossed = true;
                DescendedBelowThreshold = !nowAbove;
                wasAboveAltitudeThreshold = nowAbove;
                altitudeBoundaryPending = false;
                ActivateHighFidelitySampling(currentUT, "altitude-boundary-confirmed");
                ParsekLog.Info("Recorder", $"Altitude boundary confirmed: {(nowAbove ? "ascended above" : "descended below")} " +
                    $"threshold on {v.mainBody.name} at alt {altitude:F0}m " +
                    $"(threshold={currentAltitudeThreshold:F0}m, hysteresis: {(currentUT - altitudeBoundaryPendingUT):F1}s, " +
                    $"{Math.Abs(altitude - currentAltitudeThreshold):F0}m past boundary)");
            }
        }

        /// <summary>
        /// Reseeds altitude state from current vessel. Call on SOI change, off-rails, and recording start.
        /// Mirrors ReseedAtmosphereState.
        /// </summary>
        private void ReseedAltitudeState(Vessel v)
        {
            bool oldState = wasAboveAltitudeThreshold;
            if (v == null || v.mainBody == null || v.mainBody.atmosphere)
            {
                // Atmospheric body or null — altitude split not applicable
                wasAboveAltitudeThreshold = true;
                currentAltitudeThreshold = 0;
            }
            else
            {
                currentAltitudeThreshold = ComputeApproachAltitude(v.mainBody);
                wasAboveAltitudeThreshold = v.altitude >= currentAltitudeThreshold;
            }
            altitudeBoundaryPending = false;
            AltitudeBoundaryCrossed = false;
            DescendedBelowThreshold = false;
            if (v?.mainBody != null)
            {
                ParsekLog.Verbose("Recorder", $"Altitude state reseeded: body={v.mainBody.name}, " +
                    $"aboveThreshold={wasAboveAltitudeThreshold} (was {oldState}), " +
                    $"threshold={currentAltitudeThreshold:F0}m, alt={v.altitude:F0}m");
            }
        }

        #endregion

        #region Environment Tracking

        /// <summary>
        /// Extracts vessel state into primitive parameters and calls EnvironmentDetector.Classify.
        /// This indirection keeps EnvironmentDetector pure-static and testable.
        /// </summary>
        /// <summary>
        /// Classifies vessel environment using the cached engine list to avoid
        /// per-frame FindModulesImplementing allocations on every part.
        /// </summary>
        private SegmentEnvironment ClassifyCurrentEnvironment(Vessel v)
        {
            bool hasAtmosphere = v.mainBody != null && v.mainBody.atmosphere;
            double atmosphereDepth = hasAtmosphere ? v.mainBody.atmosphereDepth : 0;
            bool hasActiveThrust = false;

            // Use cached engine list (built in CacheEngineModules at recording start)
            // instead of iterating all parts with FindModulesImplementing every frame
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    if (cachedEngines[i].engine.EngineIgnited && cachedEngines[i].engine.finalThrust > 0)
                    {
                        hasActiveThrust = true;
                        break;
                    }
                }
            }

            double approachAlt = (!hasAtmosphere && v.mainBody != null)
                ? currentAltitudeThreshold : 0;

            return EnvironmentDetector.Classify(
                hasAtmosphere, v.altitude, atmosphereDepth,
                (int)v.situation, v.srfSpeed, hasActiveThrust, approachAlt,
                v.isEVA, v.heightFromTerrain,
                EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                v.mainBody != null && v.mainBody.ocean);
        }

        /// <summary>
        /// The environment an OrbitalCheckpoint section is stamped with. Such a section
        /// opens only while the vessel is packed (go-on-rails, a recording started on
        /// rails, an on-rails SOI handoff, an on-rails cheat / time-jump recapture, and
        /// the save-seam reopen of any of those) and plays back as a Kepler orbit, and a
        /// packed vessel cannot thrust. The environment hysteresis is not updated while
        /// packed, so it can still read ExoPropulsive from a burn that ended inside its
        /// debounce window; that label would make every "did the vessel do anything"
        /// reader (boring-tail trim, auto loop range, the switch-segment no-op
        /// classifier) treat a coast as a burn. ExoPropulsive maps to ExoBallistic, the
        /// label the background recorder and OrbitSegmentCheckpointBridge already write
        /// for checkpoint sections; every other environment and frame passes through.
        /// </summary>
        internal static SegmentEnvironment ResolveTrackSectionEnvironment(
            SegmentEnvironment env, ReferenceFrame refFrame)
        {
            if (refFrame == ReferenceFrame.OrbitalCheckpoint
                && env == SegmentEnvironment.ExoPropulsive)
                return SegmentEnvironment.ExoBallistic;
            return env;
        }

        /// <summary>
        /// Opens a new TrackSection with the given environment and reference frame.
        /// The environment goes through <see cref="ResolveTrackSectionEnvironment"/>.
        /// </summary>
        internal void StartNewTrackSection(SegmentEnvironment env, ReferenceFrame refFrame, double ut,
            TrackSectionSource source = TrackSectionSource.Active)
        {
            SegmentEnvironment resolvedEnv = ResolveTrackSectionEnvironment(env, refFrame);
            if (resolvedEnv != env)
            {
                ParsekLog.Verbose("Recorder",
                    $"TrackSection env normalized: {env} -> {resolvedEnv} ref={refFrame} " +
                    $"(a packed vessel cannot thrust) at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                env = resolvedEnv;
            }
            currentTrackSection = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = ut,
                source = source,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = refFrame == ReferenceFrame.Relative ? new List<TrajectoryPoint>() : null,
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
            trackSectionActive = true;
            // Phase 7: reset surface-clearance accumulators so the summary
            // log at the next CloseCurrentTrackSection reflects ONLY this section.
            surfaceMobileSamplesThisSection = 0;
            surfaceMobileMinClearanceThisSection = double.NaN;
            surfaceMobileMaxClearanceThisSection = double.NaN;
            surfaceMobileClearanceSumThisSection = 0.0;
            sectionFrameWarpFlags.Clear();
            sectionWarpRuns.Clear();
            currentSectionHoldsOnlySeed = false;
            ParsekLog.Info("Recorder",
                $"TrackSection started: env={env} ref={refFrame} source={source} " +
                $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Updates min/max altitude on the current TrackSection.
        /// </summary>
        private void UpdateTrackSectionAltitude(float altitude)
        {
            if (float.IsNaN(currentTrackSection.minAltitude) || altitude < currentTrackSection.minAltitude)
                currentTrackSection.minAltitude = altitude;
            if (float.IsNaN(currentTrackSection.maxAltitude) || altitude > currentTrackSection.maxAltitude)
                currentTrackSection.maxAltitude = altitude;
        }

        /// <summary>
        /// Closes the current TrackSection, computes sample rate, and adds it to TrackSections list.
        /// </summary>
        internal void CloseCurrentTrackSection(double ut)
        {
            if (!trackSectionActive) return;
            currentTrackSection.endUT = ut;

            // Compute approximate sample rate
            if (currentTrackSection.frames != null && currentTrackSection.frames.Count > 1)
            {
                double duration = currentTrackSection.endUT - currentTrackSection.startUT;
                if (duration > 0)
                    currentTrackSection.sampleRateHz = (float)(currentTrackSection.frames.Count / duration);
            }

            int frameCount = currentTrackSection.frames?.Count ?? 0;
            int checkpointCount = currentTrackSection.checkpoints?.Count ?? 0;
            double sparseGapThreshold = ResolveSparseGapWarningThreshold(maxSampleInterval);
            SectionGapStats gapStats = ComputeSectionGapStats(
                currentTrackSection.frames,
                largeGapThresholdSeconds: sparseGapThreshold,
                warpFlags: sectionFrameWarpFlags);

            int bodyFixedFrameCount = currentTrackSection.bodyFixedFrames?.Count ?? 0;
            double sectionDuration = ut - currentTrackSection.startUT;
            TrackSectionCloseDisposition disposition = TrackSectionCloseClassifier.Classify(
                frameCount,
                bodyFixedFrameCount,
                checkpointCount,
                sectionDuration,
                currentTrackSection.referenceFrame,
                currentTrackSection.isBoundarySeam);

            // Discard a short PAYLOAD-FREE section: no frames, no bodyFixedFrames, no
            // checkpoints. Nothing downstream can render or read it, and persisting one
            // makes the codec's flat-trajectory defences
            // (HasCompleteTrackSectionPayloadForFlatSync and
            // TryBuildBodyFixedPrimaryFlatPointsForRelativeSections) answer "payload
            // incomplete" for the WHOLE recording; that is how an undock background
            // child persisted a Relative section's anchor-local metres as flat lat/lon
            // points and read maxDist ~735 km for a rover that never left the pad area
            // (archived todo UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON).
            //
            // Same span threshold as the zero-frame discard this replaces; what widened
            // is the SURFACE it reads (bodyFixedFrames now protects a section from being
            // dropped) and the fact that BackgroundRecorder now mirrors it. The
            // single-frame Absolute finalizations FinalizeAllForCommit emits carry a
            // frame, so they still persist, and isBoundarySeam sections are exempt (the
            // classifier persists them first) for the optimizer's split-suppression
            // contract.
            if (disposition == TrackSectionCloseDisposition.DiscardPayloadFree)
            {
                trackSectionActive = false;
                ParsekLog.Verbose("Recorder",
                    "TrackSection discarded (payload-free: no frames, bodyFixedFrames or checkpoints): " +
                    $"env={currentTrackSection.environment} ref={currentTrackSection.referenceFrame} " +
                    $"startUT={currentTrackSection.startUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"endUT={ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"duration={sectionDuration.ToString("F3", CultureInfo.InvariantCulture)}s");
                return;
            }

            // Discard seed-only transient RELATIVE sections that were closed
            // within one physics frame of opening. The canonical case is the
            // force-Absolute-refly toggle's one-frame transient: env handler
            // opens a Relative section at an env boundary (because
            // isRelativeMode was true), then UpdateAnchorDetection's gate
            // fires immediately after and calls ForceExitRelativeToAbsolute,
            // closing the just-opened Relative section while it still holds
            // only the env-boundary seed point. Persisting that 1-frame
            // section produces a multi-meter boundaryDiscontinuityMeters
            // against the next Absolute section (the live vessel moved 1200
            // m/s for 0.02s = 24m, MeasureBoundaryDiscontinuity records that
            // as the gap), and the anchor-correction system then applies a
            // matching 24m ε offset to every rendered frame after the seam.
            // The user-visible effect: a vessel ghost spawned ~24m away from
            // where the live vessel sits when dropping into re-fly. Discarding
            // the section avoids the synthetic discontinuity entirely; the
            // env-boundary seed is replayed into the next section (which
            // opens at the same UT) by the recorder's normal sampling on the
            // next frame, and the previous real section's last frame stays
            // adjacent to the next real section's first frame for a clean,
            // physics-driven bdisc measurement. 0.05s threshold = the
            // tightest production min sample interval (Full-tier
            // ProximitySamplingCadence), so a section that closes faster than
            // that can only contain the seed.
            //
            // Scope notes:
            // - Restricted to Relative because the transient is always
            //   Relative: env handler preserves frame from the prior tick's
            //   isRelativeMode, and ForceExitRelativeToAbsolute (the only
            //   path that closes a same-frame section here) gates on
            //   `if (isRelativeMode)`. Absolute single-frame, zero-duration
            //   sections are a legitimate finalization shape used by
            //   FinalizeAllForCommit when only one sample existed at commit
            //   time, and must persist.
            // - isBoundarySeam-flagged sections are an intentional 1-frame,
            //   0-duration recorder bookkeeping artifact emitted by
            //   FlushLoadedStateForOnRailsTransition for the optimizer's
            //   split-suppression contract (see TrackSection.isBoundarySeam +
            //   docs/dev/plans/optimizer-persistence-split.md §5). The
            //   Relative scope already excludes them in practice (seams are
            //   authored Absolute), but the check is kept explicit for
            //   future seam variants.
            //
            // The predicate itself lives in TrackSectionCloseClassifier.Classify so both
            // recorders read one decision and it is unit-testable.
            if (disposition == TrackSectionCloseDisposition.DiscardSeedOnlyRelativeTransient)
            {
                trackSectionActive = false;
                ParsekLog.Verbose("Recorder",
                    $"TrackSection discarded (seed-only Relative transient, frames={frameCount} " +
                    $"duration={sectionDuration.ToString("F4", CultureInfo.InvariantCulture)}s): " +
                    $"env={currentTrackSection.environment} ref={currentTrackSection.referenceFrame} " +
                    $"startUT={currentTrackSection.startUT.ToString("F3", CultureInfo.InvariantCulture)}");
                return;
            }

            TrackSections.Add(currentTrackSection);
            trackSectionActive = false;
            ParsekLog.Info("Recorder",
                $"TrackSection closed: env={currentTrackSection.environment} ref={currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={(ut - currentTrackSection.startUT).ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"avgGap={gapStats.AverageGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"largeGaps={gapStats.LargeGapCount} " +
                $"warpRuns={SectionWarpRuns.Format(sectionWarpRuns)}");
            sectionWarpRuns.Clear();

            if (gapStats.LargeGapCount > 0)
            {
                bool warn = ShouldWarnOnSparseSampling(gapStats.LargeGapCountOffRails);
                string message =
                    $"TrackSection sparse sampling: env={currentTrackSection.environment} " +
                    $"ref={currentTrackSection.referenceFrame} frames={frameCount} " +
                    $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                    $"threshold={sparseGapThreshold.ToString("F2", CultureInfo.InvariantCulture)}s " +
                    $"largeGaps={gapStats.LargeGapCount} largeGapsOffRails={gapStats.LargeGapCountOffRails}";
                if (warn)
                    ParsekLog.Warn("Recorder", message);
                else
                    ParsekLog.Verbose("Recorder", message);
            }

            // Phase 7 (design doc §13, §19.2 Pipeline-Terrain row): when a
            // surface section closes, summarise the clearance distribution
            // captured during its samples. One Verbose line per closed section —
            // bounded by the section count, no per-frame spam.
            if (IsSurfaceClearanceEnvironment(currentTrackSection.environment)
                && surfaceMobileSamplesThisSection > 0)
            {
                var ic = CultureInfo.InvariantCulture;
                double avg = surfaceMobileClearanceSumThisSection / surfaceMobileSamplesThisSection;
                ParsekLog.Verbose("Pipeline-Terrain",
                    $"section close env={currentTrackSection.environment} " +
                    $"clearanceMin={surfaceMobileMinClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceMax={surfaceMobileMaxClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceAvg={avg.ToString("F3", ic)}m " +
                    $"N={surfaceMobileSamplesThisSection}");
            }

            // P3-3: defensive accumulator reset. StartNewTrackSection already
            // resets these when a new section opens, but closing without
            // immediately opening (e.g. Stop Recording, scene exit) leaves
            // stale state that any future defensive-coding path emitting a
            // summary between sections would carry forward. Reset here so
            // the accumulators always reflect the in-progress section only.
            surfaceMobileSamplesThisSection = 0;
            surfaceMobileMinClearanceThisSection = double.NaN;
            surfaceMobileMaxClearanceThisSection = double.NaN;
            surfaceMobileClearanceSumThisSection = 0.0;
        }

        /// <summary>
        /// Updates the per-recorder running minimum distance from the focused
        /// vessel to any other re-fly-tree recording anchor, populating
        /// <see cref="reFlyTreeSamplingProximityMeters"/>. The value is
        /// consumed on the next <c>OnPhysicsFrame</c> to pick a proximity-tier
        /// sampling cadence (Full / Half / None at the
        /// <c>ReFlyTreeFullFidelityProximityRangeMeters</c> /
        /// <c>ReFlyTreeHalfFidelityProximityRangeMeters</c> bands).
        ///
        /// <para>This is a load-bearing side effect of
        /// <c>BuildRecordingAnchorCandidateList</c> via
        /// <c>Add{Live,External}RecordingAnchorCandidates</c>. The build site
        /// in <see cref="UpdateAnchorDetection"/> is hoisted ABOVE the
        /// narrowed-gate filter so the proximity scan still runs on a re-fly
        /// frame even when the filter drops same-tree candidates. Pinned by
        /// <c>FlightRecorder_BuildsCandidateList_BeforeNarrowedGateFilter</c>
        /// in <c>ReFlyAnchorBypassWiringTests</c>.</para>
        /// </summary>
        private void ConsiderReFlyTreeSamplingProximity(
            Vector3d focusedWorldPosition,
            Vector3d candidateWorldPosition,
            string source)
        {
            double distance = Vector3d.Distance(focusedWorldPosition, candidateWorldPosition);
            if (!IsFinite(distance))
                return;
            if (IsFinite(reFlyTreeSamplingProximityMeters)
                && distance >= reFlyTreeSamplingProximityMeters)
            {
                return;
            }

            reFlyTreeSamplingProximityMeters = distance;
            reFlyTreeSamplingProximitySource = source;
        }

        /// <summary>
        /// Adds a finalized orbit segment to the current TrackSection's checkpoints list,
        /// if the current section is ORBITAL_CHECKPOINT. Called whenever an orbit segment
        /// is committed to the flat OrbitSegments list.
        /// </summary>
        internal void AddOrbitSegmentToCurrentTrackSection(OrbitSegment segment)
        {
            if (!trackSectionActive) return;
            if (currentTrackSection.referenceFrame != ReferenceFrame.OrbitalCheckpoint) return;
            if (currentTrackSection.checkpoints == null) return;

            currentTrackSection.checkpoints.Add(segment);
            ParsekLog.Verbose("Recorder",
                $"Orbit segment added to TrackSection checkpoints: body={segment.bodyName} " +
                $"UT={segment.startUT.ToString("F2", CultureInfo.InvariantCulture)}-{segment.endUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Resolves the reference frame + source for the TrackSection opened on the far
        /// side of an SOI boundary, following the rails state exactly as
        /// OnVesselGoOnRails / OnVesselGoOffRails / ResumeAfterFalseAlarm do.
        ///
        /// ONLY THE onRails==true BRANCH SHIPS TODAY. The sole production caller is the
        /// on-rails half of OnVesselSOIChanged. An off-rails SOI change never reaches here:
        /// it arms a sample and the per-frame sampler splits the section at the first
        /// sample on the new body (FlightRecorder.PhysicsSoi.cs), keeping the frame it was
        /// in. The off-rails branch is a defensive default that keeps this a total
        /// function; it is pinned by a unit cell, not reachable in production.
        ///
        /// RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM: this used to open Absolute
        /// unconditionally, but OnVesselSOIChanged only runs while isOnRails is true and
        /// OnPhysicsFrame early-returns on isOnRails — so an Absolute section opened here
        /// could NEVER receive a frame. It closed payload-less at the next boundary while
        /// the same span's orbit segment was promoted into an OrbitalCheckpoint section
        /// by OrbitSegmentCheckpointBridge, giving two sections over byte-equal
        /// [startUT,endUT] (INV2-NO-DOUBLE-COVER). Opening the checkpoint frame directly
        /// lets AddOrbitSegmentToCurrentTrackSection attach the new SOI's segment to THIS
        /// section (it refuses any non-OrbitalCheckpoint section), so the section carries
        /// its own payload and nothing has to be synthesized alongside it.
        /// </summary>
        internal static void ResolveSoiBoundarySectionFrame(
            bool onRails, out ReferenceFrame frame, out TrackSectionSource source)
        {
            if (onRails)
            {
                frame = ReferenceFrame.OrbitalCheckpoint;
                source = TrackSectionSource.Checkpoint;
                return;
            }

            frame = ReferenceFrame.Absolute;
            source = TrackSectionSource.Active;
        }

        /// <summary>
        /// Closes the open TrackSection at an SOI boundary and opens the next one (#251:
        /// the optimizer only splits at section boundaries, so the crossing must be one).
        /// </summary>
        internal void TransitionTrackSectionAtSoiBoundary(
            SegmentEnvironment env, double soiUT, bool onRails)
        {
            ReferenceFrame frame;
            TrackSectionSource source;
            ResolveSoiBoundarySectionFrame(onRails, out frame, out source);

            // Grep-stable seam line, matching the three sibling transitions
            // (OnVesselGoOnRails / OnVesselGoOffRails / InitializeOnRailsOrbitSegment).
            // Emitted BEFORE the close so the previous frame is still readable.
            string previousFrame = trackSectionActive
                ? currentTrackSection.referenceFrame.ToString()
                : "(none)";
            ParsekLog.Info("Recorder",
                $"Reference frame transition: {previousFrame} -> {frame} at SOI boundary " +
                $"UT={soiUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"onRails={(onRails ? 1 : 0)} source={source}");

            CloseCurrentTrackSection(soiUT);
            StartNewTrackSection(env, frame, soiUT, source);
        }

        /// <summary>
        /// Closes the recorder's current open TrackSection at the given UT and immediately
        /// opens a continuation section with the same environment/reference metadata.
        /// Used by OnSave serialization flushes so the active tree persists the live
        /// trajectory section that is still in progress, without stopping the recorder.
        /// </summary>
        internal void CheckpointOpenTrackSectionForSerialization(double ut)
        {
            if (!trackSectionActive) return;

            // This OnSave-triggered helper preserves
            // currentTrackSection.referenceFrame across the close/reopen,
            // deliberately keeping the in-flight section's contract stable
            // across the save seam.
            var currentEnv = currentTrackSection.environment;
            var currentRef = currentTrackSection.referenceFrame;
            var currentSource = currentTrackSection.source;
            uint currentAnchor = currentTrackSection.anchorVesselId;
            string currentAnchorRec = currentTrackSection.anchorRecordingId;
            TrajectoryPoint? boundaryPoint = GetLastTrackSectionFrame();
            TrajectoryPoint? absoluteBoundaryPoint = GetLastTrackSectionBodyFixedFrame();

            CloseCurrentTrackSection(ut);
            StartNewTrackSection(currentEnv, currentRef, ut, currentSource);

            if (currentRef == ReferenceFrame.Relative)
            {
                currentTrackSection.anchorRecordingId = currentAnchorRec;
                currentTrackSection.anchorVesselId = string.IsNullOrEmpty(currentAnchorRec)
                    ? currentAnchor
                    : 0u;
            }

            // Only absolute/relative sections carry sparse frame payloads.
            if (currentRef != ReferenceFrame.OrbitalCheckpoint)
                SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);

            ParsekLog.Verbose("Recorder",
                $"Serialization checkpoint: env={currentEnv} ref={currentRef} " +
                $"source={currentSource} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        #endregion

        /// <summary>
        /// Checks the environment hysteresis for a transition (e.g. Atmospheric -> ExoBallistic)
        /// and creates a new TrackSection at the boundary if the environment changed.
        /// Runs every physics frame regardless of adaptive sampling.
        /// </summary>
        private void UpdateEnvironmentTracking(Vessel v)
        {
            if (environmentHysteresis != null)
            {
                var rawEnv = ClassifyCurrentEnvironment(v);
                double currentUT = Planetarium.GetUniversalTime();
                if (environmentHysteresis.Update(rawEnv, currentUT))
                {
                    if (TryApplyRestoreEnvironmentResync(environmentHysteresis.CurrentEnvironment, currentUT))
                        return;

                    // Environment changed — close current section, start new one.
                    // Preserve current reference frame (RELATIVE stays RELATIVE).
                    // Sample boundary point BEFORE closing — adaptive sampler may have
                    // skipped frames, so last recorded point could be far from here.
                    SamplePosition(v);

                    // Capture boundary point to seed the new section (#283).
                    // Same reference frame on both sides, so the point is directly reusable.
                    TrajectoryPoint? boundaryPoint = GetLastTrackSectionFrame();
                    TrajectoryPoint? absoluteBoundaryPoint = GetLastTrackSectionBodyFixedFrame();

                    var currentRef = isRelativeMode ? ReferenceFrame.Relative : ReferenceFrame.Absolute;
                    CloseCurrentTrackSection(currentUT);
                    StartNewTrackSection(environmentHysteresis.CurrentEnvironment, currentRef,
                        currentUT);
                    if (isRelativeMode)
                        ApplyCurrentRecordingAnchorToCurrentTrackSection();

                    SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);
                }
            }
        }
    }
}
