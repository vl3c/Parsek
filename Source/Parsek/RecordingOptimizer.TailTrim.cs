using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class RecordingOptimizer
    {
        #region Boring tail trimming

        /// <summary>
        /// Default buffer to keep past the last interesting activity when trimming.
        /// </summary>
        internal const double DefaultTailBufferSeconds = 10.0;

        /// <summary>
        /// Minimum recording duration to be eligible for tail trimming.
        /// Recordings shorter than this are left untouched.
        /// </summary>
        internal const double MinDurationForTrimSeconds = 30.0;

        /// <summary>
        /// Returns true if the recording is a leaf — no child branch point and
        /// not a mid-chain segment with a successor.
        /// </summary>
        internal static bool IsLeafRecording(Recording rec, List<Recording> allRecordings)
        {
            // Breakup-continuous effective leaf: ChildBranchPointId is set but no
            // same-PID child exists — the recording IS the leaf for its vessel. (#224)
            if (rec.ChildBranchPointId != null
                && !GhostPlaybackLogic.IsEffectiveLeafForVessel(rec))
                return false;

            if (!string.IsNullOrEmpty(rec.ChainId) && rec.ChainIndex >= 0)
            {
                for (int i = 0; i < allRecordings.Count; i++)
                {
                    var other = allRecordings[i];
                    if (other == rec) continue;
                    if (other.ChainId == rec.ChainId && other.ChainBranch == 0
                        && other.ChainIndex > rec.ChainIndex)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when a PartEvent is a zero-effect control-state seed that should
        /// not keep a long boring tail alive. Real positive-throttle / positive-power
        /// transitions still count as interesting activity.
        /// </summary>
        internal static bool IsInertPartEventForTailTrim(PartEvent evt)
        {
            switch (evt.eventType)
            {
                case PartEventType.EngineIgnited:
                case PartEventType.EngineThrottle:
                case PartEventType.RCSActivated:
                case PartEventType.RCSThrottle:
                    return evt.value <= 0f;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Finds the UT of the last interesting activity in a recording.
        /// Interesting = last non-boring TrackSection end, last non-inert PartEvent,
        /// last SegmentEvent, or last FlagEvent — whichever is latest. Returns NaN if
        /// nothing interesting found.
        /// </summary>
        internal static double FindLastInterestingUT(Recording rec)
        {
            double lastUT = double.NaN;

            // Last non-boring TrackSection
            if (rec.TrackSections != null)
            {
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                {
                    if (!GhostPlaybackLogic.IsBoringEnvironment(rec.TrackSections[i].environment))
                    {
                        lastUT = rec.TrackSections[i].endUT;
                        break;
                    }
                }
            }

            // Last non-inert PartEvent. Scans the full list and keeps the max UT so
            // the result is independent of PartEvents sort order — the previous
            // tail-backward break relied on an implicit sort-by-UT invariant
            // (StopRecording sorts, other paths may not).
            if (rec.PartEvents != null && rec.PartEvents.Count > 0)
            {
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    if (IsInertPartEventForTailTrim(rec.PartEvents[i]))
                        continue;

                    double evtUT = rec.PartEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            // Last SegmentEvent. Scans the full list for the max UT so the
            // decision is independent of SegmentEvents sort order (same
            // hardening as the PartEvents branch above, #276).
            if (rec.SegmentEvents != null && rec.SegmentEvents.Count > 0)
            {
                for (int i = 0; i < rec.SegmentEvents.Count; i++)
                {
                    double evtUT = rec.SegmentEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            // Last FlagEvent. Same full-scan hardening as above (#276).
            if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
            {
                for (int i = 0; i < rec.FlagEvents.Count; i++)
                {
                    double evtUT = rec.FlagEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            return lastUT;
        }

        internal static bool TailPreservesTerminalSpawnState(Recording rec, double trimUT)
        {
            return TailPreservesTerminalSpawnStateInternal(
                rec, trimUT, logUnstableRefusal: true, unstableTerminal: out _);
        }

        /// <summary>
        /// Log-suppression-aware overload. When <paramref name="logUnstableRefusal"/>
        /// is false, the dedicated `refused trim for unstable terminal` Verbose line
        /// is suppressed and the caller learns through <paramref name="unstableTerminal"/>
        /// whether the refusal was a non-spawnable-terminal carve-out vs. a
        /// spawnable-terminal shape-match failure. Used by the bulk optimization
        /// pass so a save with hundreds of non-spawnable boring-tail leaves
        /// doesn't emit one log line per recording per pass.
        ///
        /// Trim is only safe when the finalizer classified the recording with a
        /// terminal that ShouldSpawnAtRecordingEnd would actually spawn — i.e.
        /// Landed/Splashed/Orbiting (see GhostPlaybackLogic.IsSpawnableTerminal).
        /// For SubOrbital, Destroyed, Recovered, Docked, Boarded — and anything
        /// the finalizer leaves unclassified — no real vessel takes over from
        /// the trim UT, so the boring tail IS the only playback the player sees
        /// and must be preserved.
        /// </summary>
        internal static bool TailPreservesTerminalSpawnStateInternal(
            Recording rec, double trimUT, bool logUnstableRefusal, out bool unstableTerminal)
        {
            unstableTerminal = false;
            if (rec == null || !rec.TerminalStateValue.HasValue)
                return true;

            var ts = rec.TerminalStateValue.Value;

            // Single source of truth with the spawn policy: if
            // ShouldSpawnAtRecordingEnd wouldn't spawn a real vessel at the
            // terminal, the recording's tail is the final playback. Refuse to
            // trim regardless of orbit-shape match.
            if (!GhostPlaybackLogic.IsSpawnableTerminal(ts))
            {
                unstableTerminal = true;
                if (logUnstableRefusal)
                    LogUnstableTerminalTrimRefusal(rec, trimUT);
                return false;
            }

            switch (ts)
            {
                case TerminalState.Orbiting:
                    return TailMatchesTerminalOrbit(rec, trimUT);

                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return TailMatchesTerminalSurfaceState(rec, trimUT);

                default:
                    // IsSpawnableTerminal accepts only the three cases above;
                    // any future spawnable terminal needs its own shape check
                    // wired up here.
                    unstableTerminal = true;
                    if (logUnstableRefusal)
                        LogUnstableTerminalTrimRefusal(rec, trimUT);
                    return false;
            }
        }

        internal static bool IsUnstableTerminalState(TerminalState? terminal)
        {
            if (!terminal.HasValue)
                return false;
            return !GhostPlaybackLogic.IsSpawnableTerminal(terminal.Value);
        }

        private static void LogUnstableTerminalTrimRefusal(Recording rec, double trimUT)
        {
            ParsekLog.Verbose("Optimizer",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "TailPreservesTerminalSpawnState: refused trim for unstable terminal " +
                    "rec='{0}' terminal={1} trimUT={2:F1} explicitEndUT={3:F1} " +
                    "(non-spawnable terminal — ShouldSpawnAtRecordingEnd would not " +
                    "replace the ghost with a real vessel, so the boring tail is the " +
                    "only playback the player sees; see GhostPlaybackLogic.IsSpawnableTerminal)",
                    rec?.RecordingId ?? "(null)",
                    rec?.TerminalStateValue?.ToString() ?? "(null)",
                    trimUT,
                    rec?.ExplicitEndUT ?? double.NaN));
        }

        private static bool TailMatchesTerminalOrbit(Recording rec, double trimUT)
        {
            if (rec == null
                || string.IsNullOrEmpty(rec.TerminalOrbitBody)
                || rec.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                return false;
            }

            bool sawTailOrbit = false;

            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.endUT <= trimUT)
                        continue;

                    sawTailOrbit = true;
                    if (!OrbitShapeMatchesTerminal(rec, seg))
                        return false;
                }
            }

            if (!sawTailOrbit && rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection sec = rec.TrackSections[i];
                    if (sec.checkpoints == null)
                        continue;

                    for (int j = 0; j < sec.checkpoints.Count; j++)
                    {
                        OrbitSegment checkpoint = sec.checkpoints[j];
                        if (checkpoint.endUT <= trimUT)
                            continue;

                        sawTailOrbit = true;
                        if (!OrbitShapeMatchesTerminal(rec, checkpoint))
                            return false;
                    }
                }
            }

            return sawTailOrbit;
        }

        // Tolerances for "tail still matches terminal orbit" checks. Stable on-rails
        // orbits accumulate small numerical drift from rails/pack-unpack/conic
        // prediction across the boring tail, so the captured tail OrbitSegment params
        // very rarely match the recorded TerminalOrbit* fields byte-for-byte. Exact
        // equality made this guard reject in practice on every real recording — the
        // same failure mode that used to bite the surface-state path before its
        // tolerances landed (#356 follow-up). The values below are sized to absorb
        // normal stable-orbit jitter while still catching real maneuvers: a 1 m/s
        // burn at low Kerbin orbit shifts SMA by ~tens of metres and eccentricity by
        // >1e-3, which is well above these thresholds.
        //
        // SMA uses max(absolute, relative * SMA) so the same tolerance scales from
        // a 700 km LKO (relEps -> ~700 m) to a 13 Mm Mun encounter (relEps -> ~13 km)
        // without losing sensitivity to small absolute changes.
        internal const double TailOrbitSmaAbsoluteEpsilonMeters = 10.0;
        internal const double TailOrbitSmaRelativeEpsilon = 1e-3;
        internal const double TailOrbitEccentricityEpsilon = 1e-3;
        internal const double TailOrbitAngleEpsilonDegrees = 0.01;

        private static bool OrbitShapeMatchesTerminal(Recording rec, OrbitSegment seg)
        {
            if (string.IsNullOrEmpty(seg.bodyName) || seg.semiMajorAxis <= 0.0)
                return false;

            // Body name still uses exact equality — different body = different orbit
            // by definition, no jitter possible.
            if (seg.bodyName != rec.TerminalOrbitBody)
                return false;

            double smaEps = System.Math.Max(
                TailOrbitSmaAbsoluteEpsilonMeters,
                TailOrbitSmaRelativeEpsilon * System.Math.Abs(rec.TerminalOrbitSemiMajorAxis));
            double smaDelta = System.Math.Abs(seg.semiMajorAxis - rec.TerminalOrbitSemiMajorAxis);
            if (smaDelta > smaEps)
            {
                ParsekLog.Verbose("Optimizer",
                    $"OrbitShapeMatchesTerminal: rec '{rec?.RecordingId ?? "(null)"}' " +
                    $"sma delta {smaDelta.ToString("R", CultureInfo.InvariantCulture)}m " +
                    $"> eps {smaEps.ToString("R", CultureInfo.InvariantCulture)}m " +
                    $"(seg.sma={seg.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"terminal.sma={rec.TerminalOrbitSemiMajorAxis.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            double eccDelta = System.Math.Abs(seg.eccentricity - rec.TerminalOrbitEccentricity);
            if (eccDelta > TailOrbitEccentricityEpsilon)
            {
                ParsekLog.Verbose("Optimizer",
                    $"OrbitShapeMatchesTerminal: rec '{rec?.RecordingId ?? "(null)"}' " +
                    $"ecc delta {eccDelta.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"> eps {TailOrbitEccentricityEpsilon.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"(seg.ecc={seg.eccentricity.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"terminal.ecc={rec.TerminalOrbitEccentricity.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            // Inclination, LAN, and argP are all angular values that can wrap across
            // the 0/360 boundary (LAN/argP routinely; inclination stays in [0,180]
            // and never hits the wrap branch but uses the same helper for symmetry).
            // Raw Math.Abs(a - b) on a stable orbit at LAN ~= 360 vs ~= 0 produces a
            // false ~360 deg mismatch, which masked the trim again. TrajectoryMath's
            // existing AngularDeltaDegrees returns the shortest signed-magnitude
            // distance and is the centralized math for all angle deltas.
            double incDelta = TrajectoryMath.AngularDeltaDegrees(
                seg.inclination, rec.TerminalOrbitInclination);
            if (incDelta > TailOrbitAngleEpsilonDegrees)
            {
                ParsekLog.Verbose("Optimizer",
                    $"OrbitShapeMatchesTerminal: rec '{rec?.RecordingId ?? "(null)"}' " +
                    $"inc wrapped delta {incDelta.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"> eps {TailOrbitAngleEpsilonDegrees.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"(seg.inc={seg.inclination.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"terminal.inc={rec.TerminalOrbitInclination.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            double lanDelta = TrajectoryMath.AngularDeltaDegrees(
                seg.longitudeOfAscendingNode, rec.TerminalOrbitLAN);
            if (lanDelta > TailOrbitAngleEpsilonDegrees)
            {
                ParsekLog.Verbose("Optimizer",
                    $"OrbitShapeMatchesTerminal: rec '{rec?.RecordingId ?? "(null)"}' " +
                    $"LAN wrapped delta {lanDelta.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"> eps {TailOrbitAngleEpsilonDegrees.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"(seg.LAN={seg.longitudeOfAscendingNode.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"terminal.LAN={rec.TerminalOrbitLAN.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            double argpDelta = TrajectoryMath.AngularDeltaDegrees(
                seg.argumentOfPeriapsis, rec.TerminalOrbitArgumentOfPeriapsis);
            if (argpDelta > TailOrbitAngleEpsilonDegrees)
            {
                ParsekLog.Verbose("Optimizer",
                    $"OrbitShapeMatchesTerminal: rec '{rec?.RecordingId ?? "(null)"}' " +
                    $"argP wrapped delta {argpDelta.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"> eps {TailOrbitAngleEpsilonDegrees.ToString("R", CultureInfo.InvariantCulture)}deg " +
                    $"(seg.argP={seg.argumentOfPeriapsis.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"terminal.argP={rec.TerminalOrbitArgumentOfPeriapsis.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            return true;
        }

        private static bool TailMatchesTerminalSurfaceState(Recording rec, double trimUT)
        {
            if (!TryGetTerminalSurfaceReference(rec, out string terminalBody,
                out double terminalLat, out double terminalLon, out double terminalAlt,
                out Quaternion terminalRotation, out bool hasTerminalRotation))
            {
                ParsekLog.Verbose("Optimizer",
                    $"TailMatchesTerminalSurfaceState: no terminal surface reference " +
                    $"for rec '{rec?.RecordingId ?? "(null)"}'");
                return false;
            }

            bool sawTailPoint = false;
            int tailPointCount = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                TrajectoryPoint pt = rec.Points[i];
                if (pt.ut <= trimUT)
                    continue;

                sawTailPoint = true;
                tailPointCount++;
                if (!SurfacePointMatchesTerminal(pt, terminalBody, terminalLat, terminalLon,
                    terminalAlt, terminalRotation, hasTerminalRotation,
                    out string failReason))
                {
                    // Return on the first mismatch; the caller only needs a bool and
                    // the verbose log below already captures the failing point.
                    ParsekLog.Verbose("Optimizer",
                        $"TailMatchesTerminalSurfaceState: rec '{rec.RecordingId}' " +
                        $"point #{i} (ut={pt.ut.ToString("F2", CultureInfo.InvariantCulture)}) " +
                        $"diverges: {failReason} (tail scanned so far: {tailPointCount})");
                    return false;
                }
            }

            return sawTailPoint;
        }

        private static bool TryGetTerminalSurfaceReference(Recording rec,
            out string body, out double lat, out double lon, out double alt,
            out Quaternion rotation, out bool hasRotation)
        {
            body = null;
            lat = 0.0;
            lon = 0.0;
            alt = 0.0;
            rotation = Quaternion.identity;
            hasRotation = false;

            if (rec == null)
                return false;

            if (rec.TerminalPosition.HasValue)
            {
                SurfacePosition pos = rec.TerminalPosition.Value;
                body = pos.body;
                lat = pos.latitude;
                lon = pos.longitude;
                alt = pos.altitude;
                rotation = pos.rotation;
                hasRotation = pos.HasRecordedRotation && HasMeaningfulRotation(pos.rotation);
                if (pos.HasRecordedRotation && !hasRotation)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"TryGetTerminalSurfaceReference: ignoring identity terminal rotation for '{rec.RecordingId ?? "(null)"}'");
                }
                return true;
            }

            if (rec.SurfacePos.HasValue)
            {
                SurfacePosition pos = rec.SurfacePos.Value;
                body = pos.body;
                lat = pos.latitude;
                lon = pos.longitude;
                alt = pos.altitude;
                rotation = pos.rotation;
                hasRotation = pos.HasRecordedRotation && HasMeaningfulRotation(pos.rotation);
                if (pos.HasRecordedRotation && !hasRotation)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"TryGetTerminalSurfaceReference: ignoring identity terminal rotation for '{rec.RecordingId ?? "(null)"}'");
                }
                return true;
            }

            if (rec.Points == null || rec.Points.Count == 0)
                return false;

            TrajectoryPoint lastPt = rec.Points[rec.Points.Count - 1];
            body = lastPt.bodyName;
            lat = lastPt.latitude;
            lon = lastPt.longitude;
            alt = lastPt.altitude;
            rotation = lastPt.rotation;
            hasRotation = HasMeaningfulRotation(lastPt.rotation);
            return true;
        }

        // Tolerance for "tail still matches terminal surface state" checks. A landed
        // vessel at rest has non-trivial jitter in position and rotation — floating
        // point drift, repeated pack/unpack transitions, on-rails terrain snapping,
        // and time-warp re-anchoring can shift the sampled lat/lon/alt by a few
        // meters across a 15-minute idle tail even though the vessel is "at rest"
        // from the player's POV. The previous 1e-6° / 0.25 m / 0.5° tolerances
        // were tight enough to still fail on every real playtest recording. The
        // current values are sized to absorb normal idle drift while still rejecting
        // actual movement: a driving rover covers >1e-4° per second and >1 m altitude
        // when it goes over a bump, so a 10+ second buffer of real movement is well
        // over these thresholds. The post-trim divergence between the ghost's last
        // playback position and the captured TerminalPosition is bounded by these
        // tolerances and is small enough that the visual jump at ghost end is not
        // noticeable.
        internal const double TailPositionLatLonEpsilonDeg = 1e-4;  // ~11 m at Kerbin's equator
        internal const double TailAltitudeEpsilonMeters = 5.0;
        internal const float TailRotationEpsilonDegrees = 5.0f;

        private static bool SurfacePointMatchesTerminal(TrajectoryPoint pt,
            string terminalBody, double terminalLat, double terminalLon, double terminalAlt,
            Quaternion terminalRotation, bool hasTerminalRotation,
            out string failReason)
        {
            failReason = null;
            string pointBody = pt.bodyName;
            if (!string.IsNullOrEmpty(terminalBody) || !string.IsNullOrEmpty(pointBody))
            {
                if ((terminalBody ?? string.Empty) != (pointBody ?? string.Empty))
                {
                    failReason = $"body mismatch (point='{pointBody ?? "null"}' vs terminal='{terminalBody ?? "null"}')";
                    return false;
                }
            }

            double latDelta = pt.latitude - terminalLat;
            if (System.Math.Abs(latDelta) > TailPositionLatLonEpsilonDeg)
            {
                failReason = $"lat delta {latDelta.ToString("R", CultureInfo.InvariantCulture)} > eps {TailPositionLatLonEpsilonDeg.ToString("R", CultureInfo.InvariantCulture)}";
                return false;
            }

            double lonDelta = pt.longitude - terminalLon;
            if (System.Math.Abs(lonDelta) > TailPositionLatLonEpsilonDeg)
            {
                failReason = $"lon delta {lonDelta.ToString("R", CultureInfo.InvariantCulture)} > eps {TailPositionLatLonEpsilonDeg.ToString("R", CultureInfo.InvariantCulture)}";
                return false;
            }

            double altDelta = pt.altitude - terminalAlt;
            if (System.Math.Abs(altDelta) > TailAltitudeEpsilonMeters)
            {
                failReason = $"alt delta {altDelta.ToString("F3", CultureInfo.InvariantCulture)}m > eps {TailAltitudeEpsilonMeters.ToString("F3", CultureInfo.InvariantCulture)}m";
                return false;
            }

            bool pointHasRotation = HasMeaningfulRotation(pt.rotation);
            if (hasTerminalRotation || pointHasRotation)
            {
                if (!(hasTerminalRotation && pointHasRotation))
                {
                    failReason = $"rotation presence mismatch (pt.has={pointHasRotation} terminal.has={hasTerminalRotation})";
                    return false;
                }

                Quaternion pointRot = TrajectoryMath.SanitizeQuaternion(pt.rotation);
                Quaternion terminalRot = TrajectoryMath.SanitizeQuaternion(terminalRotation);
                float rotAngle = Quaternion.Angle(pointRot, terminalRot);
                if (rotAngle > TailRotationEpsilonDegrees)
                {
                    failReason = $"rot angle {rotAngle.ToString("F2", CultureInfo.InvariantCulture)}° > eps {TailRotationEpsilonDegrees.ToString("F2", CultureInfo.InvariantCulture)}°";
                    return false;
                }
            }

            return true;
        }

        private static bool HasMeaningfulRotation(Quaternion rotation)
        {
            if (rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f)
                return false;

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(rotation);
            return Quaternion.Angle(sanitized, Quaternion.identity) > 1e-4f;
        }

        /// <summary>
        /// Trims the boring tail of a leaf recording. If the recording ends with a long
        /// idle period (ExoBallistic or SurfaceStationary), removes trailing points and
        /// sections past bufferSeconds after the last interesting activity.
        /// Returns true if the recording was trimmed.
        /// </summary>
        internal static bool TrimBoringTail(Recording rec, List<Recording> allRecordings,
            double bufferSeconds = DefaultTailBufferSeconds)
        {
            return TrimBoringTailInternal(rec, allRecordings, bufferSeconds,
                logSkipReason: true, skipCategory: out _);
        }

        /// <summary>
        /// Skip-category-aware overload. When <paramref name="logSkipReason"/> is false,
        /// every per-recording verbose skip line emitted by this method (including the
        /// dedicated `refused trim for unstable terminal` line that <see cref="TailPreservesTerminalSpawnState"/>
        /// owns) is suppressed and the caller receives the short category name in
        /// <paramref name="skipCategory"/> so a bulk pass over hundreds of recordings
        /// can emit a single aggregated summary instead of one log line per skipped
        /// recording. Categories: <c>rec-null-or-too-few-points</c>, <c>not-leaf</c>,
        /// <c>too-short</c>, <c>no-track-sections</c>, <c>last-section-not-boring</c>,
        /// <c>all-boring-too-few-points</c>, <c>buffer-not-met</c>, <c>terminal-mismatch</c>,
        /// <c>unstable-terminal</c>, <c>no-points-past-trim-ut</c>, <c>keep-count-too-low</c>.
        /// </summary>
        internal static bool TrimBoringTailInternal(
            Recording rec, List<Recording> allRecordings,
            double bufferSeconds, bool logSkipReason, out string skipCategory)
        {
            skipCategory = null;
            if (rec == null || rec.Points.Count < 2)
            {
                skipCategory = "rec-null-or-too-few-points";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (rec-null-or-too-few-points) " +
                        $"id='{rec?.RecordingId ?? "(null)"}' pointCount={(rec?.Points?.Count ?? 0)}");
                return false;
            }

            // Only trim leaf recordings
            if (!IsLeafRecording(rec, allRecordings))
            {
                skipCategory = "not-leaf";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (not-leaf) " +
                        $"id='{rec.RecordingId}' chainId='{rec.ChainId ?? "(null)"}' " +
                        $"chainIndex={rec.ChainIndex} childBranchPointId='{rec.ChildBranchPointId ?? "(null)"}'");
                return false;
            }

            // Recording must be long enough to warrant trimming
            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinDurationForTrimSeconds)
            {
                skipCategory = "too-short";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (too-short) " +
                        $"id='{rec.RecordingId}' " +
                        $"duration={duration.ToString("F1", CultureInfo.InvariantCulture)}s " +
                        $"<= min={MinDurationForTrimSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
                return false;
            }

            // Must have TrackSections to detect boring tails
            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
            {
                skipCategory = "no-track-sections";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (no-track-sections) " +
                        $"id='{rec.RecordingId}' trackSections={(rec.TrackSections?.Count ?? -1)}");
                return false;
            }

            // Last section must be boring
            var lastSection = rec.TrackSections[rec.TrackSections.Count - 1];
            if (!GhostPlaybackLogic.IsBoringEnvironment(lastSection.environment))
            {
                skipCategory = "last-section-not-boring";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (last-section-not-boring) " +
                        $"id='{rec.RecordingId}' lastEnv={lastSection.environment} " +
                        $"sectionCount={rec.TrackSections.Count}");
                return false;
            }

            double lastInterestingUT = FindLastInterestingUT(rec);
            if (double.IsNaN(lastInterestingUT))
            {
                // Entire recording is boring (no non-boring sections, no events).
                // This happens after optimizer splits produce an all-SurfaceStationary
                // or all-ExoBallistic leaf. Trim to a minimal window from the start
                // so the ghost finishes quickly and the real vessel spawns promptly.
                // Use the second point's UT as reference to guarantee at least 2 points
                // survive for valid interpolation (the trim logic requires keepCount >= 2).
                if (rec.Points.Count < 3)
                {
                    skipCategory = "all-boring-too-few-points";
                    if (logSkipReason)
                        ParsekLog.Verbose("Optimizer",
                            $"TrimBoringTail: skipped (all-boring-too-few-points) " +
                            $"id='{rec.RecordingId}' pointCount={rec.Points.Count}");
                    return false; // too few to trim meaningfully
                }
                lastInterestingUT = rec.Points[1].ut;
            }

            double trimUT = lastInterestingUT + bufferSeconds;
            if (trimUT >= rec.EndUT)
            {
                skipCategory = "buffer-not-met";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (buffer-not-met) " +
                        $"id='{rec.RecordingId}' " +
                        $"trimUT={trimUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $">= endUT={rec.EndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"(lastInterestingUT={lastInterestingUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"buffer={bufferSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)");
                return false; // boring tail is shorter than buffer
            }

            // Two distinct skip flavors live behind this gate. The non-spawnable-
            // terminal carve-out (every TerminalState that GhostPlaybackLogic
            // .IsSpawnableTerminal refuses — SubOrbital/Destroyed/Docked/Recovered/
            // Boarded/default) has its own dedicated `refused trim for unstable
            // terminal` log line that the gate emits when logging is allowed; we
            // route that through the same `logSkipReason` flag so the bulk pass
            // stays silent on per-recording lines for that bucket too. The
            // spawnable-terminal (Landed/Splashed/Orbiting) shape-match failure
            // path keeps its own per-recording log because the divergence numbers
            // (ecc/inc/LAN deltas) don't aggregate cleanly into a summary line.
            if (!TailPreservesTerminalSpawnStateInternal(
                rec, trimUT, logUnstableRefusal: logSkipReason, unstableTerminal: out bool unstableTerminal))
            {
                if (unstableTerminal)
                {
                    skipCategory = "unstable-terminal";
                }
                else
                {
                    skipCategory = "terminal-mismatch";
                    if (logSkipReason)
                        ParsekLog.Verbose("Optimizer",
                            $"TrimBoringTail: skipped (terminal-mismatch) '{rec.VesselName}' ({rec.RecordingId}) " +
                            $"because tail still diverges from terminal state after trimUT={trimUT:F1} " +
                            $"(terminal={rec.TerminalStateValue?.ToString() ?? "null"})");
                }
                return false;
            }

            double originalEndUT = rec.EndUT;
            int originalPointCount = rec.Points.Count;

            // Trim Points — find first point past trimUT, keep everything before it
            int keepCount = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                if (rec.Points[i].ut > trimUT)
                {
                    keepCount = i;
                    break;
                }
            }
            // No points past trimUT — nothing to trim
            if (keepCount == 0)
            {
                skipCategory = "no-points-past-trim-ut";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (no-points-past-trim-ut) " +
                        $"id='{rec.RecordingId}' " +
                        $"trimUT={trimUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"pointCount={rec.Points.Count}");
                return false;
            }
            // Must keep at least 2 points for valid interpolation
            if (keepCount < 2)
            {
                skipCategory = "keep-count-too-low";
                if (logSkipReason)
                    ParsekLog.Verbose("Optimizer",
                        $"TrimBoringTail: skipped (keep-count-too-low) " +
                        $"id='{rec.RecordingId}' keepCount={keepCount} " +
                        $"trimUT={trimUT.ToString("F1", CultureInfo.InvariantCulture)}");
                return false;
            }
            rec.Points.RemoveRange(keepCount, rec.Points.Count - keepCount);

            // Trim TrackSections — remove trailing sections past trimUT, shorten spanning section
            for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
            {
                var sec = rec.TrackSections[i];
                if (sec.startUT >= trimUT)
                {
                    rec.TrackSections.RemoveAt(i);
                }
                else if (sec.endUT > trimUT)
                {
                    if (TryTrimTrackSectionPayload(ref sec, trimUT))
                        rec.TrackSections[i] = sec;
                    else
                        rec.TrackSections.RemoveAt(i);
                    break;
                }
                else break;
            }

            // Trim OrbitSegments past trimUT
            if (rec.OrbitSegments != null)
            {
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    var os = rec.OrbitSegments[i];
                    if (os.startUT >= trimUT)
                    {
                        rec.OrbitSegments.RemoveAt(i);
                    }
                    else if (os.endUT > trimUT)
                    {
                        os.endUT = trimUT;
                        rec.OrbitSegments[i] = os;
                        break;
                    }
                    else break;
                }
            }

            RecordingStore.TrySyncFlatTrajectoryFromTrackSections(rec, allowRelativeSections: true);

            // Persist exact payload bounds as a metadata fallback. Without this,
            // a sidecar hydration failure leaves the .sfs with the stale
            // finalize-time range and the trimmed recording looks untrimmed.
            StampExplicitBoundsFromPayload(rec, "RecordingOptimizer.TrimBoringTail");

            // Strip events past the new EndUT (they're inert during playback but
            // waste memory and disk space in serialized sidecar files)
            double newEndUT = rec.EndUT;
            StripEventsPastUT(rec.PartEvents, newEndUT);
            StripEventsPastUT(rec.SegmentEvents, newEndUT);
            StripEventsPastUT(rec.FlagEvents, newEndUT);

            // Invalidate cached stats
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;

            double removedSeconds = originalEndUT - newEndUT;
            int removedPoints = originalPointCount - rec.Points.Count;
            ParsekLog.Info("Optimizer",
                $"TrimBoringTail: trimmed '{rec.VesselName}' ({rec.RecordingId}) " +
                $"from endUT={originalEndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"to {rec.EndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(removed {removedSeconds.ToString("F1", CultureInfo.InvariantCulture)}s, {removedPoints} points; " +
                $"trimUT={trimUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"lastInterestingUT={lastInterestingUT.ToString("F1", CultureInfo.InvariantCulture)})");
            return true;
        }

        private static void StampExplicitBoundsFromPayload(Recording rec, string context)
        {
            if (rec == null) return;

            rec.ExplicitStartUT = double.NaN;
            rec.ExplicitEndUT = double.NaN;

            double startUT;
            double endUT;
            if (!PlaybackTrajectoryBoundsResolver.TryGetGhostPlayablePayloadBounds(rec, out startUT, out endUT))
                return;
            if (double.IsNaN(startUT) || double.IsInfinity(startUT)
                || double.IsNaN(endUT) || double.IsInfinity(endUT)
                || endUT < startUT)
                return;

            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            ParsekLog.Verbose("Optimizer",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: stamped explicit bounds rec={1} startUT={2:R} endUT={3:R}",
                    context ?? "RecordingOptimizer", rec.RecordingId ?? "<none>", startUT, endUT));
        }

        private static bool TryTrimTrackSectionPayload(ref TrackSection sec, double trimUT)
        {
            switch (sec.referenceFrame)
            {
                case ReferenceFrame.OrbitalCheckpoint:
                    if (sec.frames != null && sec.frames.Count > 0)
                        TrimCheckpointFramesAtUT(sec.frames, trimUT);

                    if (sec.checkpoints == null || sec.checkpoints.Count == 0)
                    {
                        sec.endUT = trimUT;
                        return true;
                    }

                    for (int i = sec.checkpoints.Count - 1; i >= 0; i--)
                    {
                        var checkpoint = sec.checkpoints[i];
                        if (checkpoint.startUT >= trimUT)
                        {
                            sec.checkpoints.RemoveAt(i);
                        }
                        else if (checkpoint.endUT > trimUT)
                        {
                            checkpoint.endUT = trimUT;
                            sec.checkpoints[i] = checkpoint;
                            break;
                        }
                        else break;
                    }

                    if (sec.checkpoints.Count == 0)
                        return false;

                    sec.endUT = sec.checkpoints[sec.checkpoints.Count - 1].endUT;
                    return true;

                default:
                    if (sec.frames == null || sec.frames.Count == 0)
                    {
                        sec.endUT = trimUT;
                        return true;
                    }

                    for (int i = sec.frames.Count - 1; i >= 0; i--)
                    {
                        if (sec.frames[i].ut > trimUT)
                            sec.frames.RemoveAt(i);
                        else
                            break;
                    }

                    TrimRelativeBodyFixedPrimaryAfterUT(ref sec, trimUT);

                    if (sec.frames.Count == 0)
                        return false;

                    sec.endUT = sec.frames[sec.frames.Count - 1].ut;
                    return true;
            }
        }

        private static void TrimRelativeBodyFixedPrimaryForLeadingOverlap(
            ref TrackSection section,
            double? previousEndUT)
        {
            if (section.referenceFrame != ReferenceFrame.Relative
                || section.bodyFixedFrames == null
                || section.bodyFixedFrames.Count == 0)
            {
                return;
            }

            section.bodyFixedFrames = FlightRecorder.StableSortByUT(section.bodyFixedFrames, p => p.ut);

            if (!previousEndUT.HasValue)
                return;

            for (int i = section.bodyFixedFrames.Count - 1; i >= 0; i--)
            {
                if (section.bodyFixedFrames[i].ut <= previousEndUT.Value)
                    section.bodyFixedFrames.RemoveAt(i);
            }
        }

        private static void TrimRelativeBodyFixedPrimaryAfterUT(
            ref TrackSection section,
            double trimUT)
        {
            if (section.referenceFrame != ReferenceFrame.Relative
                || section.bodyFixedFrames == null
                || section.bodyFixedFrames.Count == 0)
            {
                return;
            }

            section.bodyFixedFrames = FlightRecorder.StableSortByUT(section.bodyFixedFrames, p => p.ut);
            for (int i = section.bodyFixedFrames.Count - 1; i >= 0; i--)
            {
                if (section.bodyFixedFrames[i].ut > trimUT)
                    section.bodyFixedFrames.RemoveAt(i);
                else
                    break;
            }
        }

        internal static void TrimCheckpointFramesAtUT(List<TrajectoryPoint> frames, double trimUT)
        {
            if (frames == null || frames.Count == 0)
                return;

            int firstAfter = -1;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].ut > trimUT)
                {
                    firstAfter = i;
                    break;
                }
            }

            if (firstAfter < 0)
                return;

            TrajectoryPoint? trimPoint = null;
            if (firstAfter > 0)
            {
                TrajectoryPoint before = frames[firstAfter - 1];
                TrajectoryPoint after = frames[firstAfter];
                if (before.ut < trimUT && after.ut > trimUT)
                    trimPoint = InterpolateCheckpointFrame(before, after, trimUT);
            }

            frames.RemoveRange(firstAfter, frames.Count - firstAfter);
            if (trimPoint.HasValue)
                frames.Add(trimPoint.Value);
        }

        private static TrajectoryPoint InterpolateCheckpointFrame(
            TrajectoryPoint before,
            TrajectoryPoint after,
            double targetUT)
        {
            double duration = after.ut - before.ut;
            double t = duration > 0.0001
                ? (targetUT - before.ut) / duration
                : 0.0;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            float tf = (float)t;

            return new TrajectoryPoint
            {
                ut = targetUT,
                latitude = before.latitude + (after.latitude - before.latitude) * t,
                longitude = before.longitude + (after.longitude - before.longitude) * t,
                altitude = before.altitude + (after.altitude - before.altitude) * t,
                rotation = SlerpQuaternionManaged(before.rotation, after.rotation, tf),
                velocity = Vector3.Lerp(before.velocity, after.velocity, tf),
                bodyName = !string.IsNullOrEmpty(after.bodyName) ? after.bodyName : before.bodyName,
                funds = before.funds + (after.funds - before.funds) * t,
                science = before.science + (after.science - before.science) * tf,
                reputation = before.reputation + (after.reputation - before.reputation) * tf,
                // Phase 7: lerp clearance; NaN propagates through arithmetic.
                recordedGroundClearance = before.recordedGroundClearance
                    + (after.recordedGroundClearance - before.recordedGroundClearance) * t
            };
        }

        private static Quaternion SlerpQuaternionManaged(Quaternion from, Quaternion to, float t)
        {
            from = TrajectoryMath.SanitizeQuaternion(from);
            to = TrajectoryMath.SanitizeQuaternion(to);
            if (t <= 0f) return from;
            if (t >= 1f) return to;

            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            if (dot < 0f)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }

            if (dot > 0.9995f)
            {
                return TrajectoryMath.PureNormalize(new Quaternion(
                    from.x + (to.x - from.x) * t,
                    from.y + (to.y - from.y) * t,
                    from.z + (to.z - from.z) * t,
                    from.w + (to.w - from.w) * t));
            }

            double theta0 = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot)));
            double sinTheta0 = Math.Sin(theta0);
            if (sinTheta0 < 1e-6)
                return from;

            double theta = theta0 * t;
            double sinTheta = Math.Sin(theta);
            double s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
            double s1 = sinTheta / sinTheta0;

            return TrajectoryMath.PureNormalize(new Quaternion(
                (float)(from.x * s0 + to.x * s1),
                (float)(from.y * s0 + to.y * s1),
                (float)(from.z * s0 + to.z * s1),
                (float)(from.w * s0 + to.w * s1)));
        }

        #endregion
    }
}
