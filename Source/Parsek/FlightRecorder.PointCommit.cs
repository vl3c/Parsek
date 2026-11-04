using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Trajectory point building and commit, same-UT and time-order guards, trims, seam and
    // boundary seed points, and structural-event snapshots.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Appends the trajectory point to the flat Recording list and the current TrackSection,
        /// updates last-recorded bookkeeping, refreshes the backup snapshot, and emits periodic
        /// diagnostic logging.
        /// </summary>
        private void CommitRecordedPoint(TrajectoryPoint point, Vessel v, TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(point.ut, "commit-recorded-point"))
                return;

            if (object.ReferenceEquals(v, null))
            {
                CommitRecordedPointWithoutVessel(point, bodyFixedPrimaryPoint);
                return;
            }

            CommitRecordedPointWithVessel(point, v, bodyFixedPrimaryPoint);
        }

        /// <summary>
        /// Appends one entry to <see cref="sectionFrameWarpFlags"/> in lockstep
        /// with a <c>currentTrackSection.frames.Add</c>. The flag records whether
        /// the sample was taken under time-warp / on-rails so the section-close
        /// sparse-sampling check can classify each gap. Reads warp state
        /// defensively (TimeWarp may be absent under xUnit).
        /// </summary>
        private void AppendCurrentSectionFrameWarpFlag()
        {
            sectionFrameWarpFlags.Add(isOnRails || IsRailsWarpActiveForDiagnostics());
            if (trackSectionActive && currentTrackSection.frames != null
                && currentTrackSection.frames.Count > 0)
            {
                SectionWarpRuns.Append(
                    sectionWarpRuns,
                    currentTrackSection.frames[currentTrackSection.frames.Count - 1].ut,
                    ReadEncodedFrameWarpRateForDiagnostics(isOnRails));
            }
        }

        /// <summary>
        /// The current frame's warp state encoded by
        /// <see cref="SectionWarpRuns.EncodeFrameRate"/>. Reads TimeWarp defensively
        /// (absent under xUnit, where every frame reads as 1x). Diagnostic only.
        /// </summary>
        internal static float ReadEncodedFrameWarpRateForDiagnostics(bool onRails)
        {
            try
            {
                return SectionWarpRuns.EncodeFrameRate(
                    onRails,
                    TimeWarp.WarpMode == TimeWarp.Modes.LOW,
                    TimeWarp.CurrentRate);
            }
            catch
            {
                return SectionWarpRuns.EncodeFrameRate(onRails, false, 1f);
            }
        }

        /// <summary>
        /// Reads whether RAILS warp is engaged, defensively. Returns false when the
        /// Unity <c>TimeWarp</c> singleton is unavailable (xUnit / headless). Physics
        /// warp reads false: its frames are sampled like 1x frames. Used only to
        /// classify sparse-sampling diagnostics, never to gate recording.
        /// </summary>
        internal static bool IsRailsWarpActiveForDiagnostics()
        {
            try
            {
                // Without the singleton TimeWarp's static accessors answer rate index 1
                // in HIGH mode (decompiled), which would read as rails warp.
                if (object.ReferenceEquals(TimeWarp.fetch, null))
                    return false;
                return IsRailsWarpState(
                    TimeWarp.CurrentRateIndex, TimeWarp.WarpMode == TimeWarp.Modes.LOW);
            }
            catch { return false; }
        }

        /// <summary>
        /// Rails warp is a rate index above 0 in the HIGH (rails) warp mode;
        /// LOW is physics warp.
        /// </summary>
        internal static bool IsRailsWarpState(int currentRateIndex, bool physicsWarpMode)
        {
            return currentRateIndex > 0 && !physicsWarpMode;
        }

        internal static bool IsSurfaceClearanceEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.SurfaceMobile
                || env == SegmentEnvironment.SurfaceStationary;
        }

        internal static bool ShouldEmitSurfaceClearance(
            bool trackSectionActive,
            ReferenceFrame frame,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            return trackSectionActive
                && frame == ReferenceFrame.Absolute
                && IsSurfaceClearanceEnvironment(env)
                && hasPqsController;
        }

        /// <summary>
        /// Apply v9 surface clearance to the body-fixed shadow
        /// copy that gets stored in <c>TrackSection.bodyFixedFrames</c>
        /// alongside a RELATIVE section. The in-frames primary <c>point</c>
        /// stays NaN-clearance because anchor-local metres-in-lat/lon/alt are
        /// not body-fixed coordinates, but the shadow carries genuine
        /// body-fixed lat/lon/alt and playback applies terrain correction
        /// via <c>recordedGroundClearance</c>. Without this, parent-anchored
        /// surface debris recorded inside a parent-relative section would
        /// replay at raw recorded altitude and bury / pop on terrain.
        /// Per-section <c>surfaceMobileMin/Max/Sum</c> stats are NOT updated
        /// here; those track the section's primary frames, not the parallel
        /// body-fixed store.
        /// </summary>
        private void ApplySurfaceClearanceToBodyFixedShadow(
            Vessel v,
            ref TrajectoryPoint shadow)
        {
            if (object.ReferenceEquals(v, null) || v.mainBody == null
                || v.mainBody.pqsController == null)
            {
                return;
            }
            // Mirror the production gate but force frame=Absolute -- the
            // shadow carries body-fixed lat/lon/alt regardless of the section
            // frame. The env / trackSectionActive / pqs checks still apply.
            if (!ShouldEmitSurfaceClearance(
                    trackSectionActive,
                    ReferenceFrame.Absolute,
                    trackSectionActive ? currentTrackSection.environment : SegmentEnvironment.Atmospheric,
                    true))
            {
                return;
            }
            double terrainHeight = v.mainBody.TerrainAltitude(
                shadow.latitude, shadow.longitude, true);
            shadow.recordedGroundClearance = shadow.altitude - terrainHeight;
        }

        private void CommitRecordedPointWithVessel(
            TrajectoryPoint point,
            Vessel v,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            const bool hasVessel = true;

            // Guard: detect time regression (quickload/revert during recording).
            // Trim all points recorded after the new time to maintain monotonicity.
            // Callers (this method + SamplePosition) reset lastRecordedUT to point.ut
            // immediately after the trim returns, so the stale value inside
            // TrimRecordingToUT's log line is fine — it reflects the pre-trim state.
            // A sub-threshold backwards step is a #419-class stale-UT seam artifact:
            // reject it so the flat Points list and dual-written TrackSection frames
            // stay monotonic (the BG path rejects all backwards appends outright; this
            // is the foreground analog, keeping the large-regression revert trim).
            var timeOrder = ClassifyForegroundCommitTimeOrder(Recording.Count, point.ut, lastRecordedUT);
            if (timeOrder == ForegroundCommitTimeOrder.TrimThenAppend)
            {
                TrimRecordingToUT(point.ut);
            }
            else if (timeOrder == ForegroundCommitTimeOrder.RejectNonMonotonic)
            {
                ParsekLog.VerboseRateLimited("Recorder", "foreground-nonmonotonic-reject",
                    $"rejected non-monotonic foreground point: ut={point.ut.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"lastRecordedUT={lastRecordedUT.ToString("R", CultureInfo.InvariantCulture)} (#419-class)");
                return;
            }

            // Phase 7 (design doc §13, §18 Phase 7): for surface sections,
            // capture the per-sample ground clearance so playback can apply
            // continuous terrain correction at render time. NaN for any other
            // environment / RELATIVE frame; the playback path falls through
            // to the legacy altitude branch. Mutates `point` BEFORE the flat
            // list / section append so both stores see the same value.
            bool surfaceClearanceSample = ShouldEmitSurfaceClearance(
                    trackSectionActive,
                    trackSectionActive ? currentTrackSection.referenceFrame : ReferenceFrame.Absolute,
                    trackSectionActive ? currentTrackSection.environment : SegmentEnvironment.Atmospheric,
                    hasVessel && v.mainBody != null && v.mainBody.pqsController != null);
            if (surfaceClearanceSample)
            {
                double terrainHeight = v.mainBody.TerrainAltitude(point.latitude, point.longitude, true);
                point.recordedGroundClearance = point.altitude - terrainHeight;
            }

            if (TryCommitSameUTPoint(point, v, bodyFixedPrimaryPoint, surfaceClearanceSample))
                return;

            if (surfaceClearanceSample)
            {
                surfaceMobileSamplesThisSection++;
                if (double.IsNaN(surfaceMobileMinClearanceThisSection)
                    || point.recordedGroundClearance < surfaceMobileMinClearanceThisSection)
                    surfaceMobileMinClearanceThisSection = point.recordedGroundClearance;
                if (double.IsNaN(surfaceMobileMaxClearanceThisSection)
                    || point.recordedGroundClearance > surfaceMobileMaxClearanceThisSection)
                    surfaceMobileMaxClearanceThisSection = point.recordedGroundClearance;
                surfaceMobileClearanceSumThisSection += point.recordedGroundClearance;
            }

            Recording.Add(point);
            currentSectionHoldsOnlySeed = false;
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;
            lastRecordedWorldRotation = hasVessel
                ? TrajectoryMath.SanitizeQuaternion(v.transform.rotation)
                : default(Quaternion);
            hasLastRecordedWorldRotation = hasVessel;
            LastRecordedAltitude = point.altitude;

            // Dual-write: flat Points list + current TrackSection
            if (trackSectionActive && currentTrackSection.frames != null)
            {
                currentTrackSection.frames.Add(point);
                AppendCurrentSectionFrameWarpFlag();
                if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                    && bodyFixedPrimaryPoint.HasValue)
                {
                    // Apply surface clearance to the body-fixed
                    // shadow before appending it to bodyFixedFrames. The shadow
                    // carries body-fixed lat/lon/alt that playback applies
                    // terrain correction against.
                    TrajectoryPoint shadow = bodyFixedPrimaryPoint.Value;
                    ApplySurfaceClearanceToBodyFixedShadow(v, ref shadow);
                    if (currentTrackSection.bodyFixedFrames == null)
                        currentTrackSection.bodyFixedFrames = new List<TrajectoryPoint>();
                    currentTrackSection.bodyFixedFrames.Add(shadow);
                }
                UpdateTrackSectionAltitude((float)point.altitude);
            }

            if (hasVessel)
                RefreshBackupSnapshot(v, "periodic");

            // Update diagnostics growth rate
            if (DiagnosticsState.hasActiveGrowthRate)
            {
                double startUT = Recording.Count > 0 ? Recording[0].ut : point.ut;
                DiagnosticsState.activeGrowthRate = ComputeGrowthRate(
                    DiagnosticsState.activeGrowthRate,
                    Recording.Count, PartEvents.Count, startUT, point.ut,
                    DiagnosticsState.avgBytesPerPoint);
            }

            if (Recording.Count % 10 == 0)
            {
                ParsekLog.VerboseRateLimited("Recorder", "recorded-point",
                    $"Recorded point #{Recording.Count}: {point}", 5.0);
            }
        }

        /// <summary>
        /// The one-line growth-rate summary FinalizeRecordingState logs when a recording
        /// stops. Invariant-formatted: the elapsed time and both rates are doubles.
        /// </summary>
        internal static string FormatGrowthRateAtStop(RecordingGrowthRate gr)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Recording growth rate at stop: {0} points, {1} events, {2:F1}s elapsed, " +
                "{3:F2} pts/s, {4:F2} evts/s, est {5} bytes",
                gr.totalPoints, gr.totalEvents, gr.elapsedSeconds,
                gr.pointsPerSecond, gr.eventsPerSecond, gr.estimatedFinalBytes);
        }

        /// <summary>
        /// Pure growth-rate update for the diagnostics overlay, including the zero-elapsed division
        /// guard: a recording that has just started has startUT == currentUT, and dividing by it would
        /// put NaN / Infinity into the report. Shared verbatim by both commit paths (with and without a
        /// vessel). internal static so xUnit can drive elapsedSeconds == 0 - a hand-filled
        /// RecordingGrowthRate struct cannot witness the guard.
        /// </summary>
        internal static RecordingGrowthRate ComputeGrowthRate(
            RecordingGrowthRate gr, int totalPoints, int totalEvents,
            double startUT, double currentUT, double avgBytesPerPoint)
        {
            gr.totalPoints = totalPoints;
            gr.totalEvents = totalEvents;
            gr.elapsedSeconds = currentUT - startUT;
            if (gr.elapsedSeconds > 0)
            {
                gr.pointsPerSecond = gr.totalPoints / gr.elapsedSeconds;
                gr.eventsPerSecond = gr.totalEvents / gr.elapsedSeconds;
            }
            else
            {
                gr.pointsPerSecond = 0;
                gr.eventsPerSecond = 0;
            }
            gr.estimatedFinalBytes = (long)(gr.totalPoints * avgBytesPerPoint + gr.totalEvents * 40);
            return gr;
        }

        private void CommitRecordedPointWithoutVessel(
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(point.ut, "commit-recorded-point-without-vessel"))
                return;

            // Same #419-class foreground monotonicity guard as CommitRecordedPointWithVessel.
            var timeOrder = ClassifyForegroundCommitTimeOrder(Recording.Count, point.ut, lastRecordedUT);
            if (timeOrder == ForegroundCommitTimeOrder.TrimThenAppend)
            {
                TrimRecordingToUT(point.ut);
            }
            else if (timeOrder == ForegroundCommitTimeOrder.RejectNonMonotonic)
            {
                ParsekLog.VerboseRateLimited("Recorder", "foreground-nonmonotonic-reject",
                    $"rejected non-monotonic foreground point: ut={point.ut.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"lastRecordedUT={lastRecordedUT.ToString("R", CultureInfo.InvariantCulture)} (#419-class, no-vessel)");
                return;
            }

            if (TryCommitSameUTPoint(point, null, bodyFixedPrimaryPoint, false))
                return;

            Recording.Add(point);
            currentSectionHoldsOnlySeed = false;
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;
            lastRecordedWorldRotation = default(Quaternion);
            hasLastRecordedWorldRotation = false;
            LastRecordedAltitude = point.altitude;

            if (trackSectionActive && currentTrackSection.frames != null)
            {
                currentTrackSection.frames.Add(point);
                AppendCurrentSectionFrameWarpFlag();
                if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                    && bodyFixedPrimaryPoint.HasValue)
                {
                    if (currentTrackSection.bodyFixedFrames == null)
                        currentTrackSection.bodyFixedFrames = new List<TrajectoryPoint>();
                    currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
                }
                UpdateTrackSectionAltitude((float)point.altitude);
            }

            if (DiagnosticsState.hasActiveGrowthRate)
            {
                double startUT = Recording.Count > 0 ? Recording[0].ut : point.ut;
                DiagnosticsState.activeGrowthRate = ComputeGrowthRate(
                    DiagnosticsState.activeGrowthRate,
                    Recording.Count, PartEvents.Count, startUT, point.ut,
                    DiagnosticsState.avgBytesPerPoint);
            }

            if (Recording.Count % 10 == 0)
            {
                ParsekLog.VerboseRateLimited("Recorder", "recorded-point",
                    $"Recorded point #{Recording.Count}: {point}", 5.0);
            }
        }

        /// <summary>
        /// How a foreground commit whose UT equals the last point of its section is handled
        /// (todo SECTION-DUPLICATE-UT-SAMPLES).
        /// </summary>
        internal enum SameUTCommitDisposition
        {
            /// <summary>A new UT for this section (or nothing to compare): append normally.</summary>
            Append,
            /// <summary>The same sample again: keep the stored point, OR in any new flag bits.</summary>
            MergeIntoIdentical,
            /// <summary>A different sample at the same UT: the later observation replaces the stored one.</summary>
            ReplaceLast,
        }

        /// <summary>
        /// Classifies a foreground commit against the point it would follow in its own
        /// section (the section's last frame, or the flat list's last point when no section
        /// is open). Several paths can commit at one UT inside one physics callback: a
        /// periodic sample and a structural-event snapshot at a joint break, the section
        /// close / reopen seed at staging, an off-rails boundary sample after a packed
        /// sample at clamp release. A section must hold one sample per UT. The later commit
        /// is the later observation of that instant, taken after whatever event caused it,
        /// so it is the one the following samples continue from (at clamp release the
        /// off-rails sample carries the unpacked velocity convention every later sample
        /// uses; the packed one carried <c>obt_velocity</c>). A commit into a section with
        /// no frames yet appends: the flat list's equal-UT seam point belongs to the
        /// previous section.
        /// </summary>
        internal static SameUTCommitDisposition ClassifySameUTCommit(
            bool hasReference, TrajectoryPoint reference, TrajectoryPoint incoming)
        {
            if (!hasReference || reference.ut != incoming.ut)
                return SameUTCommitDisposition.Append;
            return AreTrajectoryPointsEquivalent(reference, incoming)
                ? SameUTCommitDisposition.MergeIntoIdentical
                : SameUTCommitDisposition.ReplaceLast;
        }

        /// <summary>
        /// Field-by-field equality of two samples, ignoring <see cref="TrajectoryPoint.flags"/>
        /// (merged separately) and treating two NaN clearances as equal.
        /// </summary>
        internal static bool AreTrajectoryPointsEquivalent(TrajectoryPoint a, TrajectoryPoint b)
        {
            return a.ut.Equals(b.ut)
                && a.latitude.Equals(b.latitude)
                && a.longitude.Equals(b.longitude)
                && a.altitude.Equals(b.altitude)
                && a.rotation.x.Equals(b.rotation.x)
                && a.rotation.y.Equals(b.rotation.y)
                && a.rotation.z.Equals(b.rotation.z)
                && a.rotation.w.Equals(b.rotation.w)
                && a.velocity.x.Equals(b.velocity.x)
                && a.velocity.y.Equals(b.velocity.y)
                && a.velocity.z.Equals(b.velocity.z)
                && string.Equals(a.bodyName, b.bodyName, StringComparison.Ordinal)
                && a.funds.Equals(b.funds)
                && a.science.Equals(b.science)
                && a.reputation.Equals(b.reputation)
                && a.recordedGroundClearance.Equals(b.recordedGroundClearance);
        }

        /// <summary>
        /// Names the fields two same-UT samples differ in, for the replace log line.
        /// </summary>
        internal static string DescribeTrajectoryPointDifference(TrajectoryPoint a, TrajectoryPoint b)
        {
            var ic = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (!a.latitude.Equals(b.latitude) || !a.longitude.Equals(b.longitude)
                || !a.altitude.Equals(b.altitude))
            {
                parts.Add("position(alt " + a.altitude.ToString("F2", ic) + "->"
                    + b.altitude.ToString("F2", ic) + ")");
            }
            if (!a.rotation.x.Equals(b.rotation.x) || !a.rotation.y.Equals(b.rotation.y)
                || !a.rotation.z.Equals(b.rotation.z) || !a.rotation.w.Equals(b.rotation.w))
                parts.Add("rotation");
            if (!a.velocity.x.Equals(b.velocity.x) || !a.velocity.y.Equals(b.velocity.y)
                || !a.velocity.z.Equals(b.velocity.z))
            {
                parts.Add("speed(" + a.velocity.magnitude.ToString("F2", ic) + "->"
                    + b.velocity.magnitude.ToString("F2", ic) + ")");
            }
            if (!string.Equals(a.bodyName, b.bodyName, StringComparison.Ordinal))
                parts.Add("body(" + (a.bodyName ?? "(null)") + "->" + (b.bodyName ?? "(null)") + ")");
            if (!a.funds.Equals(b.funds) || !a.science.Equals(b.science) || !a.reputation.Equals(b.reputation))
                parts.Add("career");
            if (!a.recordedGroundClearance.Equals(b.recordedGroundClearance))
                parts.Add("clearance");
            return parts.Count > 0 ? string.Join(",", parts.ToArray()) : "(none)";
        }

        /// <summary>
        /// Handles a commit whose UT equals the last point of its section; returns false
        /// when the commit is an ordinary append. The flat list mirrors the section: its
        /// last point is updated in place when it has the same UT, else the sample is
        /// appended there so the flat list keeps every committed UT.
        /// </summary>
        private bool TryCommitSameUTPoint(
            TrajectoryPoint point,
            Vessel v,
            TrajectoryPoint? bodyFixedPrimaryPoint,
            bool surfaceClearanceSample)
        {
            bool sectionReference = trackSectionActive && currentTrackSection.frames != null;
            List<TrajectoryPoint> referenceList = sectionReference ? currentTrackSection.frames : Recording;
            bool hasReference = referenceList != null && referenceList.Count > 0;
            TrajectoryPoint reference = hasReference ? referenceList[referenceList.Count - 1] : default(TrajectoryPoint);
            SameUTCommitDisposition disposition = ClassifySameUTCommit(hasReference, reference, point);
            if (disposition == SameUTCommitDisposition.Append)
                return false;

            var ic = CultureInfo.InvariantCulture;
            byte mergedFlags = (byte)(reference.flags | point.flags);
            TrajectoryPoint stored = disposition == SameUTCommitDisposition.MergeIntoIdentical ? reference : point;
            stored.flags = mergedFlags;

            if (sectionReference)
            {
                referenceList[referenceList.Count - 1] = stored;
                if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                    && bodyFixedPrimaryPoint.HasValue
                    && disposition == SameUTCommitDisposition.ReplaceLast)
                {
                    TrajectoryPoint shadow = bodyFixedPrimaryPoint.Value;
                    if (!object.ReferenceEquals(v, null))
                        ApplySurfaceClearanceToBodyFixedShadow(v, ref shadow);
                    if (currentTrackSection.bodyFixedFrames == null)
                        currentTrackSection.bodyFixedFrames = new List<TrajectoryPoint>();
                    List<TrajectoryPoint> shadows = currentTrackSection.bodyFixedFrames;
                    if (shadows.Count > 0 && shadows[shadows.Count - 1].ut == point.ut)
                        shadows[shadows.Count - 1] = shadow;
                    else
                        shadows.Add(shadow);
                }
                if (disposition == SameUTCommitDisposition.ReplaceLast)
                    UpdateTrackSectionAltitude((float)point.altitude);
            }

            // A seed-only section's flat-list twin is the closed section's last point: a
            // replacement leaves it there and appends (the equal-UT seam the flat list
            // has always carried); an identical merge only ORs the flags into it.
            bool flatTailAtUT = Recording.Count > 0 && Recording[Recording.Count - 1].ut == point.ut;
            bool flatTailBelongsToClosedSection = sectionReference
                && currentSectionHoldsOnlySeed
                && referenceList.Count == 1;
            if (flatTailAtUT && flatTailBelongsToClosedSection)
            {
                if (disposition == SameUTCommitDisposition.ReplaceLast)
                {
                    Recording.Add(stored);
                }
                else
                {
                    TrajectoryPoint tail = Recording[Recording.Count - 1];
                    tail.flags = (byte)(tail.flags | mergedFlags);
                    Recording[Recording.Count - 1] = tail;
                }
            }
            else if (flatTailAtUT)
            {
                Recording[Recording.Count - 1] = stored;
            }
            else
            {
                Recording.Add(stored);
            }
            if (disposition == SameUTCommitDisposition.ReplaceLast)
                currentSectionHoldsOnlySeed = false;

            if (disposition == SameUTCommitDisposition.ReplaceLast)
            {
                if (surfaceClearanceSample
                    && surfaceMobileSamplesThisSection > 0
                    && !double.IsNaN(reference.recordedGroundClearance)
                    && !double.IsNaN(point.recordedGroundClearance))
                {
                    surfaceMobileClearanceSumThisSection +=
                        point.recordedGroundClearance - reference.recordedGroundClearance;
                }
                lastRecordedVelocity = point.velocity;
                if (!object.ReferenceEquals(v, null))
                {
                    lastRecordedWorldRotation = ReadSanitizedWorldRotation(v);
                    hasLastRecordedWorldRotation = true;
                }
                else
                {
                    lastRecordedWorldRotation = default(Quaternion);
                    hasLastRecordedWorldRotation = false;
                }
                LastRecordedAltitude = point.altitude;
                ParsekLog.Verbose("Recorder",
                    $"Same-UT sample replaced the section's last point: ut={point.ut.ToString("R", ic)} " +
                    $"differs={DescribeTrajectoryPointDifference(reference, point)} " +
                    $"flags={mergedFlags.ToString(ic)} " +
                    $"section={(sectionReference ? "open" : "none")}");
            }
            else
            {
                ParsekLog.VerboseRateLimited("Recorder", "same-ut-identical-merge",
                    $"Same-UT sample identical to the section's last point, not appended: " +
                    $"ut={point.ut.ToString("R", ic)} flags={reference.flags.ToString(ic)}->{mergedFlags.ToString(ic)} " +
                    $"section={(sectionReference ? "open" : "none")}",
                    5.0);
            }
            lastRecordedUT = point.ut;
            return true;
        }

        // Kept out of TryCommitSameUTPoint so the headless commit path never JITs a
        // Unity transform read.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Quaternion ReadSanitizedWorldRotation(Vessel v)
        {
            return TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
        }

        /// <summary>
        /// How a foreground <c>CommitRecordedPoint*</c> append should be handled
        /// relative to the last recorded UT.
        /// </summary>
        internal enum ForegroundCommitTimeOrder
        {
            /// <summary>UT is monotonic (>= last) or the buffer is empty — append normally.</summary>
            Append,
            /// <summary>Large backwards jump (>= <see cref="TimeRegressionThresholdSeconds"/>) — quickload/revert; trim the invalid future tail, then append.</summary>
            TrimThenAppend,
            /// <summary>Sub-threshold backwards step — a stale-UT seam/boundary artifact; reject the append to preserve monotonicity.</summary>
            RejectNonMonotonic,
        }

        /// <summary>
        /// Bug #419 foreground analog of the background-recorder
        /// <c>ApplyTrajectoryPointToRecording</c> guard. The background path rejects
        /// any non-monotonic append outright; the foreground commit path historically
        /// only trimmed on a *large* (&gt;= <see cref="TimeRegressionThresholdSeconds"/>)
        /// regression (quickload/revert), so a small backwards step — a seam / boundary
        /// point built with a stale UT, or a duplicate boundary sample — slipped through
        /// and corrupted both the flat <c>Recording.Points</c> list and the dual-written
        /// <c>TrackSection.frames</c>. This classifier restores the invariant that
        /// <c>CommittedRecordingsHaveValidData</c> checks (points[i].ut &gt;= points[i-1].ut):
        /// equal UTs are accepted (boundary seeds), a large regression trims, and a
        /// sub-threshold regression is rejected.
        /// </summary>
        internal static ForegroundCommitTimeOrder ClassifyForegroundCommitTimeOrder(
            int recordedCount, double pointUT, double lastRecordedUT)
        {
            if (recordedCount <= 0 || pointUT >= lastRecordedUT)
                return ForegroundCommitTimeOrder.Append;
            if (pointUT < lastRecordedUT - TimeRegressionThresholdSeconds)
                return ForegroundCommitTimeOrder.TrimThenAppend;
            return ForegroundCommitTimeOrder.RejectNonMonotonic;
        }

        /// <summary>
        /// Trims the recording buffer back to before the given UT.
        /// Called when a quickload/revert causes game time to regress, making
        /// previously-recorded points invalid (alternate timeline data).
        /// Also trims PartEvents, OrbitSegments, and TrackSection frames.
        /// </summary>
        internal void TrimRecordingToUT(double newUT)
        {
            int oldCount = Recording.Count;

            // Linear scan: find first point with UT >= newUT. Points are monotonic
            // by construction (guard in CommitRecordedPoint), so binary search would
            // work, but recording sizes are bounded at low thousands and linear is
            // clearer. Revisit if profiling shows this is hot.
            int trimIdx = Recording.Count;
            for (int i = 0; i < Recording.Count; i++)
            {
                if (Recording[i].ut >= newUT)
                {
                    trimIdx = i;
                    break;
                }
            }

            if (trimIdx < Recording.Count)
                Recording.RemoveRange(trimIdx, Recording.Count - trimIdx);

            // Trim PartEvents after newUT
            int oldPartEvents = PartEvents.Count;
            PartEvents.RemoveAll(e => e.ut >= newUT);

            // Trim OrbitSegments that start after newUT
            int oldOrbitSegs = OrbitSegments.Count;
            OrbitSegments.RemoveAll(s => s.startUT >= newUT);

            // Trim current TrackSection frames
            if (trackSectionActive && currentTrackSection.frames != null)
            {
                int frameTrimIdx = currentTrackSection.frames.Count;
                for (int i = 0; i < currentTrackSection.frames.Count; i++)
                {
                    if (currentTrackSection.frames[i].ut >= newUT)
                    {
                        frameTrimIdx = i;
                        break;
                    }
                }
                if (frameTrimIdx < currentTrackSection.frames.Count)
                {
                    int framesToTrim = currentTrackSection.frames.Count - frameTrimIdx;
                    currentTrackSection.frames.RemoveRange(frameTrimIdx, framesToTrim);
                    // Keep the parallel warp-flag list index-aligned with frames.
                    if (frameTrimIdx < sectionFrameWarpFlags.Count)
                        sectionFrameWarpFlags.RemoveRange(frameTrimIdx,
                            sectionFrameWarpFlags.Count - frameTrimIdx);
                    SectionWarpRuns.TrimToFrames(sectionWarpRuns, currentTrackSection.frames);
                }
            }
            if (trackSectionActive && currentTrackSection.bodyFixedFrames != null)
            {
                int shadowTrimIdx = currentTrackSection.bodyFixedFrames.Count;
                for (int i = 0; i < currentTrackSection.bodyFixedFrames.Count; i++)
                {
                    if (currentTrackSection.bodyFixedFrames[i].ut >= newUT)
                    {
                        shadowTrimIdx = i;
                        break;
                    }
                }
                if (shadowTrimIdx < currentTrackSection.bodyFixedFrames.Count)
                    currentTrackSection.bodyFixedFrames.RemoveRange(shadowTrimIdx,
                        currentTrackSection.bodyFixedFrames.Count - shadowTrimIdx);
            }

            // Invariant formatting keeps this line identical under a test host's OS
            // culture; KSP pins the game's own thread to en (see .claude/CLAUDE.md).
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Warn("Recorder",
                $"Time regression detected: UT went from {lastRecordedUT.ToString("F1", ic)} to " +
                $"{newUT.ToString("F1", ic)} " +
                $"(delta={(newUT - lastRecordedUT).ToString("F1", ic)}s). Trimmed recording: " +
                $"points {oldCount}→{Recording.Count}, " +
                $"partEvents {oldPartEvents}→{PartEvents.Count}, " +
                $"orbitSegments {oldOrbitSegs}→{OrbitSegments.Count}");
        }

        /// <summary>
        /// Bug #419 — pure monotonicity predicate for trajectory-point appends.
        /// Returns true when appending <paramref name="incomingUT"/> to
        /// <paramref name="points"/> preserves the monotonically-non-decreasing UT
        /// invariant that <c>CommittedRecordingsHaveValidData</c> checks
        /// (<c>points[i].ut &gt;= points[i-1].ut</c>). Equal UTs are accepted —
        /// boundary seeds and flush-time overlap dedup can produce same-UT duplicates
        /// that are harmless to downstream consumers. Empty / null <paramref name="points"/>
        /// always returns true (first append is trivially monotonic).
        /// </summary>
        internal static bool IsAppendUTMonotonic(List<TrajectoryPoint> points, double incomingUT)
        {
            if (points == null || points.Count == 0)
                return true;
            return incomingUT >= points[points.Count - 1].ut;
        }

        /// <summary>
        /// Bug #419 — pure helper that trims trajectory points at or after
        /// <paramref name="boundaryUT"/> from <paramref name="points"/>, preserving all
        /// earlier points. Returns the number of points removed. Null / empty input
        /// is a no-op that returns 0. Callers use this to guarantee that a freshly
        /// spawned debris/breakup child recording cannot hold any inherited sample
        /// whose UT would collide with the post-boundary sampler stream.
        /// The comparison is inclusive of <paramref name="boundaryUT"/> — the caller
        /// is expected to re-seed the child with an authoritative initial point at
        /// exactly <paramref name="boundaryUT"/>, so any pre-existing point at that
        /// UT would be a duplicate anyway.
        /// </summary>
        internal static int TrimPointsAtOrAfterUT(List<TrajectoryPoint> points, double boundaryUT)
        {
            if (points == null || points.Count == 0)
                return 0;

            int trimIdx = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].ut >= boundaryUT)
                {
                    trimIdx = i;
                    break;
                }
            }

            int removed = points.Count - trimIdx;
            if (removed > 0)
                points.RemoveRange(trimIdx, removed);
            return removed;
        }

        internal static Vector3 SampleCurrentVelocity(Vessel v)
        {
            if (v == null)
                return Vector3.zero;

            return v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());
        }

        internal static bool ShouldRecordAttitudePoint(
            Quaternion currentWorldRotation,
            Quaternion lastWorldRotation,
            double currentUT,
            double lastRecordedUT,
            bool hasLastWorldRotation,
            float minInterval,
            float rotationThresholdDegrees)
        {
            if (!hasLastWorldRotation || lastRecordedUT < 0)
                return false;

            double elapsed = currentUT - lastRecordedUT;
            if (elapsed < 0 || elapsed < minInterval)
                return false;

            return TrajectoryMath.ComputeQuaternionAngleDegrees(
                currentWorldRotation,
                lastWorldRotation) >= rotationThresholdDegrees;
        }

        /// <summary>
        /// Constructs a TrajectoryPoint from the vessel's current state and the given velocity.
        /// Extracted to deduplicate the identical construction in OnPhysicsFrame, SamplePosition,
        /// and post-switch baseline seeding.
        /// </summary>
        internal static TrajectoryPoint BuildTrajectoryPoint(Vessel v, Vector3 velocity, double ut)
        {
            // Derive lat/lon/alt from v.transform.position rather than reading
            // v.latitude/longitude/altitude directly. Those fields lag the
            // transform by exactly one fixedDeltaTime for loaded/unpacked
            // vessels: Vessel.LateUpdate's LLA refresh runs after PhysX has
            // already moved the transform, so v.latitude reflects the
            // PREVIOUS tick's CoM while v.transform.position reflects the
            // current tick. Every per-tick FG sample written from the stale
            // path stored a position ~velocity*0.02s behind ground truth
            // (~4.3 m at 215 m/s), invisible until separation made it visible
            // as a forward slide of debris ghosts against the parent ghost
            // (the parent stayed on the stale FG path while the debris seed
            // and subsequent BG samples used fresh transform-derived LLA).
            // PR 832 trace evidence pinned the staleness vector as exactly
            // velocity*fixedDeltaTime, perfectly parallel to velocity
            // (cos=0.999999), matching the predicted one-tick step to within
            // 5 mm. body.GetLatitude/Longitude/Altitude is the inverse of
            // body.GetWorldSurfacePosition, so this produces the LLA that
            // round-trips back to the live transform position.
            Vector3d freshWorldPos = v.transform.position;
            TrajectoryPoint pt = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.mainBody.GetLatitude(freshWorldPos),
                longitude = v.mainBody.GetLongitude(freshWorldPos),
                altitude = v.mainBody.GetAltitude(freshWorldPos),
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: NaN sentinel = "not captured / non-surface".
                // Surface-section appends in CommitRecordedPoint overwrite
                // with the real clearance when applicable.
                recordedGroundClearance = double.NaN
            };
            // ---- Trace-Sep: log every FG per-tick sample (includes structural events) ----
            if (v != null && (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive))
            {
                Vector3d worldPos = v.mainBody != null
                    ? v.mainBody.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude)
                    : Vector3d.zero;
                Vector3d transformPos = v.transform != null ? (Vector3d)v.transform.position : Vector3d.zero;
                // tickSinceBreak: which post-PhysX physics tick this sample is
                // on, relative to the most recent recording-window trigger.
                // tickSinceBreak ~= 0 is the same-tick BuildTP that fires from
                // the joint break's own FixedUpdate cycle (pre-PhysX
                // VesselPrecalculate postfix). tickSinceBreak ~= 1 is the next
                // FixedUpdate's pre-PhysX postfix, AFTER PhysX in the
                // intervening frame has moved transform.position by one tick.
                // If the per-tick BuildTP sample really has a +dt phase offset
                // (as commit 3's hypothesis claimed), the transformVsLLAdelta
                // magnitude here would jump at tickSinceBreak=1 relative to
                // the surrounding cadence; if not, it stays near zero across
                // all per-tick samples and the hypothesis is wrong.
                double lastBreak = TraceSeparation.LastRecordingTriggerUT;
                double tickSinceBreak = double.NaN;
                if (!double.IsNaN(lastBreak))
                {
                    float dt = Time.fixedDeltaTime;
                    if (dt > 0f && !float.IsNaN(dt) && !float.IsInfinity(dt))
                        tickSinceBreak = (ut - lastBreak) / dt;
                }
                TraceSeparation.RecordLog("BuildTP",
                    "pid=" + v.persistentId +
                    " name='" + v.vesselName + "'" +
                    " ut=" + ut.ToString("R", CultureInfo.InvariantCulture) +
                    " tickSinceBreak=" + (double.IsNaN(tickSinceBreak) ? "NaN" : tickSinceBreak.ToString("F3", CultureInfo.InvariantCulture)) +
                    " packed=" + v.packed +
                    " LLA=(" + pt.latitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + pt.longitude.ToString("R", CultureInfo.InvariantCulture) +
                    "," + pt.altitude.ToString("R", CultureInfo.InvariantCulture) + ")" +
                    " worldFromLLA=" + TraceSeparation.FormatVector3d(worldPos) +
                    " transformPos=" + TraceSeparation.FormatVector3d(transformPos) +
                    " transformVsLLAdelta=" + TraceSeparation.FormatVector3d(transformPos - worldPos) +
                    " |delta|=" + (transformPos - worldPos).magnitude.ToString("F3", CultureInfo.InvariantCulture) +
                    " velIn=" + TraceSeparation.FormatVector3(velocity) + " |v|=" + velocity.magnitude.ToString("R", CultureInfo.InvariantCulture) +
                    " srfVel=" + TraceSeparation.FormatVector3(v.srf_velocity) + " |sv|=" + v.srf_velocity.magnitude.ToString("R", CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----
            return pt;
        }

        /// <summary>
        /// Force-record a boundary point (used at on-rails/off-rails transitions).
        /// </summary>
        public void SamplePosition(Vessel v)
        {
            SamplePositionAtUT(v, Planetarium.GetUniversalTime());
        }

        private void SamplePositionAtUT(Vessel v, double ut)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "sample-null-vessel",
                    "SamplePosition called with null vessel");
                return;
            }
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(ut, "sample-position"))
                return;

            Vector3 currentVelocity = SampleCurrentVelocity(v);
            TrajectoryPoint point = BuildTrajectoryPoint(
                v,
                currentVelocity,
                ut);
            TrajectoryPoint absolutePoint = point;
            bool relativeApplied = ApplyRelativeOffset(ref point, v);

            CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
            ParsekLog.Verbose("Recorder", $"Boundary point sampled at UT={point.ut:F1}");
        }

        private void AppendSectionStartSeamPoint(Vessel v, double startUT, string reason)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Pipeline-Smoothing", "section-start-seam-null-vessel",
                    $"section-start seam skipped: reason={reason ?? "<unknown>"} null vessel");
                return;
            }
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(startUT, "section-start-seam-" + (reason ?? "unknown")))
                return;

            TrajectoryPoint seamPoint = BuildTrajectoryPoint(v, SampleCurrentVelocity(v), startUT);
            AppendSectionStartSeamPoint(seamPoint, v, reason);
        }

        internal void AppendSectionStartSeamPointForTesting(TrajectoryPoint seamPoint, string reason)
        {
            AppendSectionStartSeamPoint(seamPoint, null, reason);
        }

        internal void CommitRecordedPointForTesting(TrajectoryPoint point)
        {
            CommitRecordedPoint(point, null);
        }

        internal void SeedBoundaryPointForTesting(TrajectoryPoint point)
        {
            SeedBoundaryPoint(point);
        }

        private void AppendSectionStartSeamPoint(TrajectoryPoint seamPoint, Vessel v, string reason)
        {
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(seamPoint.ut, "section-start-seam-" + (reason ?? "unknown")))
                return;

            if (!trackSectionActive
                || currentTrackSection.referenceFrame != ReferenceFrame.Absolute
                || currentTrackSection.frames == null)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    $"section-start seam skipped: reason={reason ?? "<unknown>"} sectionActive={trackSectionActive} " +
                    $"sectionRef={currentTrackSection.referenceFrame}");
                return;
            }

            seamPoint.ut = currentTrackSection.startUT;
            seamPoint.flags = (byte)((TrajectoryPointFlags)seamPoint.flags & ~TrajectoryPointFlags.StructuralEventSnapshot);
            CommitRecordedPoint(seamPoint, v);
            ParsekLog.Verbose("Pipeline-Smoothing",
                $"section-start seam committed: reason={reason ?? "<unknown>"} " +
                $"ut={seamPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"lat={seamPoint.latitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"lon={seamPoint.longitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"alt={seamPoint.altitude.ToString("R", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9): builds a synthetic
        /// <see cref="TrajectoryPoint"/> for <paramref name="v"/> at the exact
        /// <paramref name="eventUT"/> of a structural event, with
        /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/> set on the
        /// returned <see cref="TrajectoryPoint.flags"/>. The point uses the vessel's
        /// current physics state — KSP doesn't surface a sub-tick interpolation API
        /// for <c>Vessel.latitude/longitude/altitude</c>, so this samples the live
        /// state at the moment the event handler fires, which is the same instant
        /// KSP's structural-event pipeline records as the event UT. Using one
        /// helper for every event handler guarantees both vessels' snapshots come
        /// from the same physics state read.
        /// </summary>
        /// <remarks>
        /// Pure / static so xUnit can drive it directly with a fake vessel. The
        /// caller (see <see cref="AppendStructuralEventSnapshot"/>) decides whether
        /// to commit the point to a recording.
        /// </remarks>
        internal static TrajectoryPoint BuildStructuralEventSnapshot(
            Vessel v, Vector3 velocity, double eventUT)
        {
            TrajectoryPoint pt = BuildTrajectoryPoint(v, velocity, eventUT);
            return ApplyStructuralEventFlag(pt);
        }

        /// <summary>
        /// Phase 9: pure flag-set helper. Returns a copy of <paramref name="pt"/>
        /// with the <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>
        /// bit OR'd into <see cref="TrajectoryPoint.flags"/>. Idempotent —
        /// already-flagged points are returned unchanged. Tests use this seam
        /// directly because the Vessel-driven overload requires a live KSP
        /// runtime; this overload pins the bit-set semantics independently.
        /// </summary>
        internal static TrajectoryPoint ApplyStructuralEventFlag(TrajectoryPoint pt)
        {
            pt.flags = (byte)((TrajectoryPointFlags)pt.flags | TrajectoryPointFlags.StructuralEventSnapshot);
            return pt;
        }

        internal static bool TryFindStructuralEventSnapshotPointForUT(
            IEnumerable<TrajectoryPoint> points,
            double eventUT,
            out TrajectoryPoint point,
            double toleranceSeconds = 1e-6)
        {
            point = default;
            if (points == null
                || double.IsNaN(eventUT)
                || double.IsInfinity(eventUT)
                || double.IsNaN(toleranceSeconds)
                || toleranceSeconds < 0.0)
            {
                return false;
            }

            bool found = false;
            double bestDelta = double.MaxValue;
            foreach (TrajectoryPoint candidate in points)
            {
                if (((TrajectoryPointFlags)candidate.flags & TrajectoryPointFlags.StructuralEventSnapshot)
                    != TrajectoryPointFlags.StructuralEventSnapshot)
                {
                    continue;
                }

                double delta = Math.Abs(candidate.ut - eventUT);
                if (delta > toleranceSeconds || delta >= bestDelta)
                    continue;

                bestDelta = delta;
                point = candidate;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Pure guard: true when the tail of <paramref name="points"/> is already a
        /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>-flagged point at
        /// <paramref name="eventUT"/> (within <paramref name="toleranceSeconds"/>), so a
        /// second structural snapshot for the same event UT would add a redundant
        /// duplicate-UT sample. Multiple structural events fire inside one
        /// <c>Part.Undock()</c> call at the same physics-clock UT (one <c>onPartUndock</c>
        /// plus one <c>onPartJointBreak</c> per docking port), and each handler appends
        /// its own snapshot of the SAME live vessel state — identical UT, position, and
        /// flags. The equal-UT append is tolerated by the #419 monotonicity guards
        /// ("boundary seeds"), but strictly-increasing consumers
        /// (<c>TrajectoryMath.CatmullRomFit</c>) fail on the duplicates, leaving the
        /// section unsmoothed. Tail-only check is sufficient: the duplicate events fire
        /// within one physics tick, so no regular sample can interleave between them.
        /// Tolerance default matches <see cref="TryFindStructuralEventSnapshotPointForUT"/>.
        /// </summary>
        internal static bool HasStructuralEventSnapshotAtTail(
            IReadOnlyList<TrajectoryPoint> points,
            double eventUT,
            double toleranceSeconds = 1e-6)
        {
            if (points == null
                || points.Count == 0
                || double.IsNaN(eventUT)
                || double.IsInfinity(eventUT)
                || double.IsNaN(toleranceSeconds)
                || toleranceSeconds < 0.0)
            {
                return false;
            }

            TrajectoryPoint tail = points[points.Count - 1];
            if (((TrajectoryPointFlags)tail.flags & TrajectoryPointFlags.StructuralEventSnapshot)
                != TrajectoryPointFlags.StructuralEventSnapshot)
            {
                return false;
            }

            return Math.Abs(tail.ut - eventUT) <= toleranceSeconds;
        }

        /// <summary>
        /// Phase 9: appends one <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>-flagged
        /// point per involved vessel that matches this recorder's
        /// <see cref="RecordingVesselId"/>. Callers pass one event UT captured from the live
        /// structural event, so every snapshot emitted by this call shares the same physics-clock
        /// timestamp. Position and velocity are sampled per matching vessel from the live state
        /// when the handler runs.
        ///
        /// <para>No-op when the recorder is not currently recording (e.g. mid-stop), when the
        /// the recording cannot match an involved vessel. Callers are
        /// responsible for filtering "noise" structural events (e.g. non-structural joint
        /// breaks, transient pid mismatches) before invoking the helper.</para>
        ///
        /// <para><b>"Every involved vessel" reduction:</b> the design doc §12 contract says the
        /// recorder produces "a snapshot for every involved vessel". In Parsek's architecture,
        /// each <see cref="FlightRecorder"/> tracks a single focused vessel
        /// (<see cref="RecordingVesselId"/>); peer vessels in a dock / undock / EVA / joint-break
        /// pair are owned by a different recorder (a <see cref="BackgroundRecorder"/> if they're
        /// being proximity-recorded, or no recorder at all). "Every involved vessel" therefore
        /// reduces to "this recorder's vessel" by construction — the dedup-by-RecordingVesselId
        /// filter is the design, not a workaround. Peer coverage is a per-recorder concern: each
        /// recorder satisfies §12 for its own focused vessel; proximity-tracked peers are covered
        /// by <see cref="BackgroundRecorder.AppendStructuralEventSnapshot"/>.</para>
        /// </summary>
        internal void AppendStructuralEventSnapshot(
            double eventUT, IEnumerable<Vessel> involved, string eventType)
        {
            if (!IsRecording)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot skipped: not recording (eventType={0} ut={1})",
                        eventType ?? "<unknown>",
                        eventUT.ToString("F2", CultureInfo.InvariantCulture)));
                return;
            }
            if (involved == null)
                return;

            ActivateHighFidelitySampling(eventUT, $"structural-event-{eventType ?? "unknown"}");

            // Counts vessels that matched RecordingVesselId (i.e. a snapshot was
            // attempted), NOT how many points actually landed — CommitRecordedPoint
            // may reject a non-monotonic point. The == 0 guard below only needs the
            // "did any vessel match" signal, so the match count is the right basis.
            int matchedVessels = 0;
            int vesselsConsidered = 0;
            foreach (Vessel v in involved)
            {
                vesselsConsidered++;
                if (v == null) continue;
                if (v.persistentId != RecordingVesselId) continue;

                // Several structural events can fire for the same vessel at the
                // same physics-clock UT inside one Part.Undock() call (Undock +
                // one JointBreak per docking port). Each would commit an
                // identical flagged point; the duplicates pass the equal-UT
                // monotonicity tolerance but break strictly-increasing
                // consumers (Catmull-Rom section fit). One flagged point per
                // event UT carries all the boundary information.
                if (HasStructuralEventSnapshotAtTail(Recording, eventUT))
                {
                    matchedVessels++;
                    ParsekLog.Verbose("Pipeline-Smoothing",
                        string.Format(CultureInfo.InvariantCulture,
                            "structural event snapshot deduped: same-UT structural point already " +
                            "committed (eventType={0} ut={1} vesselId={2})",
                            eventType ?? "<unknown>",
                            eventUT.ToString("F3", CultureInfo.InvariantCulture),
                            v.persistentId));
                    continue;
                }

                // Same physics state read as a regular tick sample. KSP's
                // event-fire timing means the live state IS the event-UT state —
                // there's no sub-tick interpolation API to bracket against.
                Vector3 velocity = SampleCurrentVelocity(v);
                TrajectoryPoint point = BuildStructuralEventSnapshot(v, velocity, eventUT);
                TrajectoryPoint absolutePoint = point;
                bool relativeApplied = ApplyRelativeOffset(ref point, v);

                CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
                matchedVessels++;

                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot UT={0} vesselId={1} eventType={2} flags={3} " +
                        "lat={4} lon={5} alt={6} relativeApplied={7}",
                        eventUT.ToString("F3", CultureInfo.InvariantCulture),
                        v.persistentId,
                        eventType ?? "<unknown>",
                        point.flags,
                        point.latitude.ToString("F4", CultureInfo.InvariantCulture),
                        point.longitude.ToString("F4", CultureInfo.InvariantCulture),
                        point.altitude.ToString("F1", CultureInfo.InvariantCulture),
                        relativeApplied ? "true" : "false"));
            }

            if (vesselsConsidered > 0 && matchedVessels == 0)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot: no involved vessel matched RecordingVesselId={0} " +
                        "(eventType={1} ut={2} considered={3})",
                        RecordingVesselId,
                        eventType ?? "<unknown>",
                        eventUT.ToString("F2", CultureInfo.InvariantCulture),
                        vesselsConsidered));
            }
        }

        /// <summary>
        /// Baseline seed points built via <see cref="BuildTrajectoryPoint"/> carry
        /// body-relative lat/lon/alt and surface-relative rotation — both only valid
        /// inside an ABSOLUTE track section. A RELATIVE section would need the anchor
        /// pose captured at baseline time, which the post-switch path does not record.
        /// Returns true when the seed must be skipped to avoid corrupting the section.
        /// </summary>
        internal static bool ShouldSkipSeedDueToRelativeSection(
            bool trackSectionActive,
            ReferenceFrame sectionReferenceFrame)
        {
            return trackSectionActive && sectionReferenceFrame == ReferenceFrame.Relative;
        }

        internal void SeedTrajectoryPoint(TrajectoryPoint point, Vessel v, string reason)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "seed-null-vessel",
                    "SeedTrajectoryPoint called with null vessel");
                return;
            }

            // Skip rather than corrupt — the first adaptive sample after the flip
            // still captures the current pose via the normal SamplePosition path.
            if (ShouldSkipSeedDueToRelativeSection(
                    trackSectionActive,
                    currentTrackSection.referenceFrame))
            {
                ParsekLog.Warn("Recorder",
                    $"Seed point skipped: reason={reason} UT={point.ut:F2} " +
                    $"sectionRef=Relative (baseline absolute lat/lon/alt cannot be " +
                    $"committed into a relative-frame section without the baseline " +
                    $"anchor pose, which was not captured)");
                return;
            }

            CommitRecordedPoint(point, v);
            ParsekLog.Verbose("Recorder",
                $"Seed point committed: reason={reason} UT={point.ut:F2}");
        }

        private int ResolveActiveRecordingFormatVersion()
        {
            if (ActiveTree != null
                && ActiveTree.Recordings != null
                && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out Recording activeRec)
                && activeRec != null)
            {
                return activeRec.RecordingFormatVersion;
            }

            // A fresh sample that can't locate its owning Recording falls back to the
            // current format version, so new data always uses the newest RELATIVE-frame
            // contract. Warn — in practice we should always have an ActiveRecordingId
            // by the time sampling runs, so hitting this path points at a stale tree
            // state or a missing recording row.
            ParsekLog.VerboseRateLimited("Recorder", "format-version-fallback",
                $"ResolveActiveRecordingFormatVersion fallback to v" +
                $"{RecordingStore.CurrentRecordingFormatVersion}: " +
                $"activeTree={(ActiveTree != null)} " +
                $"activeRecordingId={ActiveTree?.ActiveRecordingId ?? "null"}",
                5.0);
            return RecordingStore.CurrentRecordingFormatVersion;
        }

        /// <summary>
        /// Returns the last trajectory frame of the current TrackSection, or null if empty/inactive.
        /// Used to capture a boundary point before closing a section (#283).
        /// </summary>
        private TrajectoryPoint? GetLastTrackSectionFrame()
        {
            if (trackSectionActive && currentTrackSection.frames != null
                && currentTrackSection.frames.Count > 0)
                return currentTrackSection.frames[currentTrackSection.frames.Count - 1];
            return null;
        }

        private TrajectoryPoint? GetLastTrackSectionBodyFixedFrame()
        {
            if (trackSectionActive && currentTrackSection.bodyFixedFrames != null
                && currentTrackSection.bodyFixedFrames.Count > 0)
                return currentTrackSection.bodyFixedFrames[currentTrackSection.bodyFixedFrames.Count - 1];
            return null;
        }

        /// <summary>
        /// Returns the last payload frame from the most recently closed TrackSection, or null
        /// if no closed section carries sparse frames.
        /// </summary>
        private static TrajectoryPoint? GetLastFrameFromTrackSection(TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return section.frames[section.frames.Count - 1];
            return null;
        }

        private static TrajectoryPoint? GetLastBodyFixedFrameFromTrackSection(TrackSection section)
        {
            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                return section.bodyFixedFrames[section.bodyFixedFrames.Count - 1];
            return null;
        }

        /// <summary>
        /// Seeds the current (newly opened) TrackSection with a boundary point from the
        /// previous section. Eliminates the physics-frame gap that causes position
        /// discontinuities at section boundaries (#283).
        /// Only writes to the TrackSection frames — does NOT dual-write to the flat
        /// Recording list (the point is already there from SamplePosition).
        /// </summary>
        private void SeedBoundaryPoint(TrajectoryPoint? point, TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (!point.HasValue) return;
            if (ShouldSuppressReFlyPostLoadTrajectoryWrite(point.Value.ut, "section-boundary-seed"))
                return;
            if (!trackSectionActive || currentTrackSection.frames == null) return;
            currentTrackSection.frames.Add(point.Value);
            currentSectionHoldsOnlySeed = currentTrackSection.frames.Count == 1;
            AppendCurrentSectionFrameWarpFlag();
            if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                && bodyFixedPrimaryPoint.HasValue)
            {
                if (currentTrackSection.bodyFixedFrames == null)
                    currentTrackSection.bodyFixedFrames = new List<TrajectoryPoint>();
                currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
            }
            UpdateTrackSectionAltitude((float)point.Value.altitude);
            ParsekLog.Verbose("Recorder",
                $"Boundary point seeded: ut={point.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }
    }
}
