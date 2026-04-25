using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Scene-exit seam for incomplete-ballistic finalization. The built-in path
    /// snapshots the vessel's patched-conic coast chain, then extrapolates the
    /// remaining ballistic tail when the stock solver does not provide a final
    /// terminal endpoint. Tests can still replace the implementation via the
    /// hook / override delegates below.
    /// </summary>
    internal struct IncompleteBallisticFinalizationResult
    {
        public TerminalState? terminalState;
        public double terminalUT;
        public List<OrbitSegment> appendedOrbitSegments;
        public int patchedSegmentCount;
        public int extrapolatedSegmentCount;
        public ConfigNode vesselSnapshot;
        public ConfigNode ghostVisualSnapshot;
        public SurfacePosition? terminalPosition;
        public double? terrainHeightAtEnd;
        public RecordingFinalizationTerminalOrbit? terminalOrbit;
        public ExtrapolationFailureReason extrapolationFailureReason;
        public string subSurfaceDestroyedBodyName;
        public double subSurfaceDestroyedAltitude;
        public double subSurfaceDestroyedThreshold;
    }

    internal static class IncompleteBallisticSceneExitFinalizer
    {
        private static bool? flightGlobalsRuntimeAvailableForTesting;
        internal static Func<(bool runtimeAvailable, bool cacheResult, string diagnostic)> FlightGlobalsRuntimeAvailabilityOverrideForTesting;

        internal delegate bool TryFinalizeDelegate(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result);

        internal delegate bool TryBuildStartStateProvider(out BallisticStateVector startState);
        internal delegate ExtrapolationResult ExtrapolateProvider(
            BallisticStateVector startState,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies);

        internal static TryFinalizeDelegate TryFinalizeHook;
        internal static TryFinalizeDelegate TryFinalizeOverrideForTesting;
        private static readonly HashSet<string> subSurfaceDestroyedClassificationLogs =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void ResetForTesting()
        {
            flightGlobalsRuntimeAvailableForTesting = null;
            FlightGlobalsRuntimeAvailabilityOverrideForTesting = null;
            TryFinalizeHook = null;
            TryFinalizeOverrideForTesting = null;
            subSurfaceDestroyedClassificationLogs.Clear();
        }

        internal static bool TryApply(
            Recording recording,
            Vessel vessel,
            double commitUT,
            string logContext)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (IsAlreadyClassifiedDestroyed(recording))
            {
                LogAlreadyClassifiedDestroyedSkip(recording, logContext ?? "SceneExitFinalizer", "try-apply");
                return false;
            }

            var finalize = TryFinalizeHook ?? TryFinalizeOverrideForTesting ?? TryFinalizeRecording;
            bool usingHook = TryFinalizeHook != null;
            bool usingDefaultFinalize = TryFinalizeHook == null && TryFinalizeOverrideForTesting == null;
            IncompleteBallisticFinalizationResult result;
            try
            {
                if (!finalize(recording, vessel, commitUT, out result))
                {
                    if (usingHook)
                    {
                        double currentEndUT = recording != null ? recording.EndUT : double.NaN;
                        ParsekLog.Verbose("Extrapolator",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}: incomplete-ballistic finalization hook declined for '{1}' " +
                                "(commitUT={2:F1}, currentEndUT={3:F1}, vesselFound={4})",
                                logContext ?? "SceneExitFinalizer",
                                recording?.RecordingId ?? "(null)",
                                commitUT,
                                currentEndUT,
                                vessel != null));
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (usingDefaultFinalize
                    && TryHandleHeadlessFlightGlobalsFailure(recording != null ? recording.RecordingId : null, ex))
                {
                    return false;
                }

                throw;
            }

            if (!ValidateResult(recording, commitUT, result, logContext))
                return false;

            Apply(recording, result, logContext);

            int appendedSegments = result.appendedOrbitSegments?.Count ?? 0;
            ParsekLog.Info("Extrapolator",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: scene-exit finalization applied to '{1}' " +
                    "(patched={2}, extrapolated={3}, appendedSegments={4}, terminal={5}, terminalUT={6:F1}, vesselFound={7}, elapsedMs={8:F1})",
                    logContext ?? "SceneExitFinalizer",
                    recording?.RecordingId ?? "(null)",
                    result.patchedSegmentCount,
                    result.extrapolatedSegmentCount,
                    appendedSegments,
                    result.terminalState.Value,
                    result.terminalUT,
                    vessel != null,
                    stopwatch.Elapsed.TotalMilliseconds));
            return true;
        }

        private static bool TryFinalizeRecording(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result)
        {
            result = default(IncompleteBallisticFinalizationResult);
            try
            {
                if (!IsFlightGlobalsRuntimeAvailable(recording != null ? recording.RecordingId : null))
                    return false;

                if (recording == null || vessel == null || vessel.orbit == null)
                    return false;

                CelestialBody referenceBody = vessel.orbit.referenceBody;
                if (referenceBody == null || string.IsNullOrEmpty(referenceBody.name))
                {
                    ParsekLog.Warn("Extrapolator",
                        $"TryFinalizeRecording: '{recording.RecordingId}' missing reference body; skipping scene-exit extrapolation");
                    return false;
                }

                double cutoffAltitude = GetBallisticCutoffAltitude(referenceBody);
                if (!BallisticExtrapolator.ShouldExtrapolate(
                        vessel.situation,
                        vessel.orbit.eccentricity,
                        vessel.orbit.PeA,
                        cutoffAltitude))
                    return false;

                if (!TryBuildExtrapolationBodies(recording.RecordingId, commitUT, out var bodies))
                    return false;

                var snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(vessel, commitUT);

                ConfigNode snapshotNode = VesselSpawner.TryBackupSnapshot(vessel);

                if (!TryCompleteFinalizationFromPatchedSnapshot(
                        recording,
                        snapshot,
                        bodies,
                        vessel.transform != null ? vessel.transform.rotation : UnityEngine.Quaternion.identity,
                        delegate(out BallisticStateVector startState)
                        {
                            return TryBuildStartStateFromVessel(vessel, commitUT, out startState);
                        },
                        (startState, extrapolationBodies) => BallisticExtrapolator.Extrapolate(startState, extrapolationBodies),
                        out result))
                {
                    return false;
                }

                if (snapshotNode != null)
                {
                    result.vesselSnapshot = snapshotNode;
                    result.ghostVisualSnapshot = snapshotNode.CreateCopy();
                }
                return true;
            }
            catch (Exception ex)
            {
                string diagnostic = TryDescribeHeadlessFlightGlobalsFailure(ex);
                if (diagnostic == null)
                    throw;

                flightGlobalsRuntimeAvailableForTesting = false;
                ParsekLog.Verbose("Extrapolator",
                    $"TryFinalizeRecording: FlightGlobals runtime unavailable for '{recording?.RecordingId ?? "(null)"}' " +
                    $"({diagnostic}) — skipping default scene-exit extrapolation");
                return false;
            }
        }

        private static bool TryHandleHeadlessFlightGlobalsFailure(string recordingId, Exception ex)
        {
            string diagnostic = TryDescribeHeadlessFlightGlobalsFailure(ex);
            if (diagnostic == null)
                return false;

            ParsekLog.Verbose("Extrapolator",
                $"TryFinalizeRecording: FlightGlobals runtime unavailable for '{recordingId ?? "(null)"}' " +
                $"({diagnostic}) — skipping default scene-exit extrapolation");
            return true;
        }

        internal static bool TryBuildDefaultFinalizationResult(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result)
        {
            // Side-effect-free: the finalization-cache producer calls this on the
            // periodic refresh path, so all recording mutation must stay in Apply().
            if (IsAlreadyClassifiedDestroyed(recording))
            {
                result = default(IncompleteBallisticFinalizationResult);
                LogAlreadyClassifiedDestroyedSkip(recording, "TryBuildDefaultFinalizationResult", "cache-refresh");
                return false;
            }

            return TryFinalizeRecording(recording, vessel, commitUT, out result);
        }

        internal static bool IsAlreadyClassifiedDestroyed(Recording recording)
        {
            return recording != null
                && recording.TerminalStateValue.HasValue
                && recording.TerminalStateValue.Value == TerminalState.Destroyed;
        }

        internal static void LogAlreadyClassifiedDestroyedSkip(
            Recording recording,
            string context,
            string reason)
        {
            string recordingId = recording?.RecordingId ?? "(null)";
            double terminalUT = double.NaN;
            if (recording != null)
                terminalUT = !double.IsNaN(recording.ExplicitEndUT)
                    ? recording.ExplicitEndUT
                    : recording.EndUT;
            ParsekLog.VerboseRateLimited("Extrapolator", $"subsurface-destroyed-skip.{recordingId}",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: already classified Destroyed at terminalUT={1:F1}; skipping re-run " +
                    "(rec={2}, reason={3})",
                    string.IsNullOrEmpty(context) ? "TryFinalizeRecording" : context,
                    terminalUT,
                    recordingId,
                    reason ?? "(none)"),
                5.0);
        }

        internal static bool LogSubSurfaceDestroyedClassificationOnce(
            string recordingId,
            double terminalUT,
            string bodyName,
            double altitude,
            double threshold)
        {
            string key = string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId;
            if (!subSurfaceDestroyedClassificationLogs.Add(key))
                return false;

            ParsekLog.Warn("Extrapolator",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Start rejected: sub-surface state rec={0} body={1} ut={2:F3} alt={3:F1} " +
                    "(threshold={4:F1}); classifying recording as Destroyed",
                    key,
                    bodyName ?? "(unknown-body)",
                    terminalUT,
                    altitude,
                    threshold));
            ParsekLog.Info("Extrapolator",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "TryFinalizeRecording: classified Destroyed by sub-surface path " +
                    "rec={0} terminalUT={1:F1} body={2} alt={3:F1} threshold={4:F1}",
                    key,
                    terminalUT,
                    bodyName ?? "(unknown-body)",
                    altitude,
                    threshold));
            return true;
        }

        internal static bool TryCompleteFinalizationFromPatchedSnapshotForTesting(
            Recording recording,
            PatchedConicSnapshotResult snapshot,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            TryBuildStartStateProvider buildLiveStartState,
            ExtrapolateProvider extrapolate,
            out IncompleteBallisticFinalizationResult result)
        {
            return TryCompleteFinalizationFromPatchedSnapshot(
                recording,
                snapshot,
                bodies,
                UnityEngine.Quaternion.identity,
                buildLiveStartState,
                extrapolate,
                out result);
        }

        private static bool TryCompleteFinalizationFromPatchedSnapshot(
            Recording recording,
            PatchedConicSnapshotResult snapshot,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            UnityEngine.Quaternion frozenWorldRotation,
            TryBuildStartStateProvider buildLiveStartState,
            ExtrapolateProvider extrapolate,
            out IncompleteBallisticFinalizationResult result)
        {
            result = default(IncompleteBallisticFinalizationResult);
            string recordingId = recording?.RecordingId ?? "(null)";
            var appendedSegments = new List<OrbitSegment>();

            if (snapshot.Segments != null && snapshot.Segments.Count > 0)
            {
                appendedSegments.AddRange(snapshot.Segments);
                SeedPredictedSegmentOrbitalFrameRotations(
                    recordingId,
                    appendedSegments,
                    frozenWorldRotation,
                    bodies);
                result.patchedSegmentCount = snapshot.CapturedPatchCount;
            }
            else if (snapshot.FailureReason != PatchedConicSnapshotFailureReason.None)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: patched-conic snapshot failed for '{recordingId}' " +
                    $"with {snapshot.FailureReason}; falling back to live orbit state");
            }

            // Early ascent / transient patched-conic failures (`MissingPatchBody`,
            // `PatchLimitUnavailable`, `UpdateFailed`) mean the vessel is alive
            // but its patched-conic chain isn't fully populated yet — e.g. a
            // freshly-launched rocket in its first 30 s where KSP has not yet
            // computed a full coast chain. The live-orbit fallback via
            // `vessel.orbit.getPositionAtUT(commitUT)` returns origin-adjacent
            // coordinates for those vessels, so the sub-surface guard in
            // `BallisticExtrapolator.Extrapolate` misfires and classifies a
            // perfectly healthy ascending rocket as `Destroyed`. That terminal
            // then gets cached by the finalization cache producer (refresh
            // accepted: terminal=Destroyed) and risks poisoning every live
            // recording within the first few seconds of flight.
            //
            // Only `NullSolver` (vessel has no patched-conic solver at all) or
            // `None` (snapshot succeeded) are safe to extrapolate from:
            // NullSolver is the destroyed-vessel fingerprint the sub-surface
            // guard was designed for, and None means we have real segments.
            // Everything else: bail out without touching the recording; the
            // next refresh (or scene-exit finalizer) will try again once the
            // patched-conic chain is valid.
            if (snapshot.FailureReason != PatchedConicSnapshotFailureReason.None
                && snapshot.FailureReason != PatchedConicSnapshotFailureReason.NullSolver
                && appendedSegments.Count == 0)
            {
                ParsekLog.Verbose("Extrapolator",
                    $"TryFinalizeRecording: skipping live-orbit fallback for '{recordingId}' " +
                    $"because patched-conic failure {snapshot.FailureReason} " +
                    "indicates transient early-ascent state, not a destroyed vessel");
                return false;
            }

            if (snapshot.EncounteredManeuverNode && appendedSegments.Count > 0)
            {
                ParsekLog.Info("PatchedSnapshot",
                    $"TryFinalizeRecording: maneuver-node boundary detected for '{recordingId}'; " +
                    "discarding stock patched-conic tail and falling back to current-state propagation");
                appendedSegments.Clear();
                result.patchedSegmentCount = 0;
            }

            if (TryShortCircuitSolverPredictedImpact(
                recordingId,
                appendedSegments,
                bodies,
                out double impactTerminalUT))
            {
                result.appendedOrbitSegments = appendedSegments;
                result.terminalState = TerminalState.Destroyed;
                result.terminalUT = impactTerminalUT;
                return true;
            }

            BallisticStateVector startState;
            if (appendedSegments.Count > 0)
            {
                OrbitSegment lastSegment = appendedSegments[appendedSegments.Count - 1];
                if (!TryBuildStartStateFromSegment(lastSegment, bodies, out startState))
                {
                    ParsekLog.Warn("Extrapolator",
                        $"TryFinalizeRecording: failed to propagate terminal predicted segment for '{recordingId}' " +
                        $"(body={lastSegment.bodyName ?? "(null)"}, endUT={lastSegment.endUT:F1})");
                    return false;
                }
            }
            else if (buildLiveStartState == null || !buildLiveStartState(out startState))
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: failed to sample live vessel orbit for '{recordingId}'");
                return false;
            }

            ExtrapolationResult extrapolated = extrapolate != null
                ? extrapolate(startState, bodies)
                : BallisticExtrapolator.Extrapolate(startState, bodies);
            if (extrapolated.segments != null)
            {
                for (int i = 0; i < extrapolated.segments.Count; i++)
                {
                    OrbitSegment segment = extrapolated.segments[i];
                    if (segment.endUT > segment.startUT)
                        appendedSegments.Add(segment);
                }
            }

            result.appendedOrbitSegments = appendedSegments.Count > 0 ? appendedSegments : null;
            result.extrapolatedSegmentCount = extrapolated.segments != null
                ? extrapolated.segments.Count
                : 0;
            result.terminalState = extrapolated.terminalState;
            result.terminalUT = extrapolated.terminalUT;
            result.extrapolationFailureReason = extrapolated.failureReason;
            if (extrapolated.failureReason == ExtrapolationFailureReason.SubSurfaceStart)
            {
                PopulateSubSurfaceDestroyedDetails(
                    startState,
                    extrapolated,
                    bodies,
                    ref result);
            }

            if (extrapolated.terminalState == TerminalState.Orbiting && appendedSegments.Count > 0)
                result.terminalOrbit =
                    RecordingFinalizationTerminalOrbit.FromSegment(appendedSegments[appendedSegments.Count - 1]);

            double recordingEndUT = recording == null ? double.NaN : recording.EndUT;
            bool applied = ShouldApplyExtrapolatorResult(
                appendedSegments.Count,
                result.terminalUT,
                recordingEndUT,
                extrapolated.failureReason);
            if (!applied)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: no extended lifetime produced for '{recordingId}' " +
                    $"(terminal={result.terminalState}, terminalUT={result.terminalUT:F1}, failure={extrapolated.failureReason})");
            }
            else if (extrapolated.failureReason == ExtrapolationFailureReason.SubSurfaceStart)
            {
                LogSubSurfaceDestroyedClassificationOnce(
                    recordingId,
                    result.terminalUT,
                    result.subSurfaceDestroyedBodyName,
                    result.subSurfaceDestroyedAltitude,
                    result.subSurfaceDestroyedThreshold);
            }

            return applied;
        }

        private static void PopulateSubSurfaceDestroyedDetails(
            BallisticStateVector startState,
            ExtrapolationResult extrapolated,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            ref IncompleteBallisticFinalizationResult result)
        {
            string bodyName = !string.IsNullOrEmpty(extrapolated.terminalBodyName)
                ? extrapolated.terminalBodyName
                : startState.bodyName;
            double altitude = double.NaN;
            if (!string.IsNullOrEmpty(bodyName)
                && bodies != null
                && bodies.TryGetValue(bodyName, out ExtrapolationBody body)
                && body != null)
            {
                bodyName = body.Name ?? bodyName;
                altitude = Magnitude(extrapolated.terminalPosition) - body.Radius;
            }

            result.subSurfaceDestroyedBodyName = bodyName;
            result.subSurfaceDestroyedAltitude = altitude;
            result.subSurfaceDestroyedThreshold = BallisticExtrapolator.SubSurfaceDestroyedAltitude;
        }

        private static string TryDescribeHeadlessFlightGlobalsFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException)
                    return current.GetType().Name;
            }

            return null;
        }

        internal static bool IsFlightGlobalsRuntimeAvailable(string recordingId)
        {
            if (flightGlobalsRuntimeAvailableForTesting.HasValue)
            {
                if (!flightGlobalsRuntimeAvailableForTesting.Value)
                {
                    ParsekLog.Verbose("Extrapolator",
                        $"TryFinalizeRecording: FlightGlobals runtime unavailable for '{recordingId ?? "(null)"}' — " +
                        "skipping default scene-exit extrapolation");
                }
                return flightGlobalsRuntimeAvailableForTesting.Value;
            }

            bool runtimeAvailable;
            bool cacheResult;
            string diagnostic;
            if (FlightGlobalsRuntimeAvailabilityOverrideForTesting != null)
            {
                (runtimeAvailable, cacheResult, diagnostic) = FlightGlobalsRuntimeAvailabilityOverrideForTesting();
            }
            else
            {
                try
                {
                    bool hasFetch = FlightGlobals.fetch != null;
                    bool ready = FlightGlobals.ready;
                    runtimeAvailable = hasFetch && ready;
                    cacheResult = runtimeAvailable;
                    diagnostic = $"fetch={hasFetch}, ready={ready}";
                }
                catch (Exception ex)
                {
                    runtimeAvailable = false;
                    cacheResult = true;
                    diagnostic = ex.GetType().Name;
                }
            }

            if (cacheResult)
                flightGlobalsRuntimeAvailableForTesting = runtimeAvailable;
            if (!runtimeAvailable)
            {
                ParsekLog.Verbose("Extrapolator",
                    $"TryFinalizeRecording: FlightGlobals runtime unavailable for '{recordingId ?? "(null)"}' " +
                    $"({diagnostic}) — skipping default scene-exit extrapolation");
            }
            return runtimeAvailable;
        }

        private static bool ValidateResult(
            Recording recording,
            double commitUT,
            IncompleteBallisticFinalizationResult result,
            string logContext)
        {
            string context = logContext ?? "SceneExitFinalizer";
            string recordingId = recording?.RecordingId ?? "(null)";
            if (!result.terminalState.HasValue)
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalState was unset/default");
                return false;
            }

            if (!Enum.IsDefined(typeof(TerminalState), result.terminalState.Value))
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalState=" +
                    $"{(int)result.terminalState.Value} was invalid");
                return false;
            }

            if (double.IsNaN(result.terminalUT))
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalUT was NaN");
                return false;
            }

            double currentEndUT = recording != null ? recording.EndUT : double.NaN;
            double floorUT = GetTerminalUtFloor(commitUT, currentEndUT);
            if (!double.IsNaN(floorUT) && result.terminalUT < floorUT)
            {
                ParsekLog.Error("Extrapolator",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: rejected incomplete-ballistic finalization for '{1}' because terminalUT={2:F1} " +
                        "moved backward before max(commitUT={3:F1}, currentEndUT={4:F1})",
                        context,
                        recordingId,
                        result.terminalUT,
                        commitUT,
                        currentEndUT));
                return false;
            }

            return true;
        }

        private static double GetTerminalUtFloor(double commitUT, double currentEndUT)
        {
            if (double.IsNaN(commitUT))
                return currentEndUT;
            if (double.IsNaN(currentEndUT))
                return commitUT;
            return Math.Max(commitUT, currentEndUT);
        }

        private static bool TryBuildExtrapolationBodies(
            string recordingId,
            double referenceUT,
            out Dictionary<string, ExtrapolationBody> bodies)
        {
            bodies = new Dictionary<string, ExtrapolationBody>(StringComparer.Ordinal);
            if (FlightGlobals.Bodies == null || FlightGlobals.Bodies.Count == 0)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: no FlightGlobals bodies available for '{recordingId ?? "(null)"}'");
                return false;
            }

            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body == null || string.IsNullOrEmpty(body.name))
                    continue;

                Orbit bodyOrbit = body.orbitDriver != null ? body.orbitDriver.orbit : null;
                bodies[body.name] = new ExtrapolationBody
                {
                    Name = body.name,
                    ParentBodyName = body.referenceBody != null ? body.referenceBody.name : null,
                    GravitationalParameter = body.gravParameter,
                    Radius = body.Radius,
                    AtmosphereDepth = body.atmosphereDepth,
                    SphereOfInfluence = body.sphereOfInfluence,
                    SurfaceCoordinates = (double ut, Vector3d position, out double latitude, out double longitude) =>
                        ResolveBodyFixedSurfaceCoordinates(body, referenceUT, ut, position, out latitude, out longitude),
                    TerrainAltitude = (double latitude, double longitude, out double altitude) =>
                    {
                        try
                        {
                            altitude = body.TerrainAltitude(latitude, longitude);
                            return !double.IsNaN(altitude) && !double.IsInfinity(altitude);
                        }
                        catch
                        {
                            altitude = 0.0;
                            return false;
                        }
                    },
                    ParentFrameState = bodyOrbit != null
                        ? (ParentFrameStateResolver)((double ut, out Vector3d position, out Vector3d velocity) =>
                        {
                            position = bodyOrbit.getPositionAtUT(ut);
                            velocity = bodyOrbit.getOrbitalVelocityAtUT(ut);
                        })
                        : null
                };
            }

            if (bodies.Count == 0)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: failed to build extrapolation bodies for '{recordingId ?? "(null)"}'");
                return false;
            }

            return true;
        }

        internal static bool TryShortCircuitSolverPredictedImpact(
            string recordingId,
            IList<OrbitSegment> appendedSegments,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            out double terminalUT)
        {
            terminalUT = double.NaN;

            if (appendedSegments == null || appendedSegments.Count == 0 || bodies == null)
                return false;

            OrbitSegment lastSegment = appendedSegments[appendedSegments.Count - 1];
            if (string.IsNullOrEmpty(lastSegment.bodyName)
                || !bodies.TryGetValue(lastSegment.bodyName, out ExtrapolationBody body)
                || body == null
                || body.HasAtmosphere)
            {
                return false;
            }

            double periapsisRadius = lastSegment.semiMajorAxis * (1.0 - lastSegment.eccentricity);
            if (double.IsNaN(periapsisRadius) || double.IsInfinity(periapsisRadius) || periapsisRadius <= 0.0)
                return false;

            double cutoffAltitude = 0.0;
            double closestApproachAltitude = periapsisRadius - body.Radius;
            if (closestApproachAltitude > cutoffAltitude)
                return false;

            terminalUT = lastSegment.endUT;
            ParsekLog.Info("PatchedSnapshot",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "TryFinalizeRecording: solver-predicted impact short-circuit for '{0}' body={1} " +
                    "closestApproachAlt={2:F1} cutoffAlt={3:F1} terminalUT={4:F1}",
                    recordingId ?? "(null)",
                    body.Name ?? lastSegment.bodyName ?? "(null)",
                    closestApproachAltitude,
                    cutoffAltitude,
                    terminalUT));
            return true;
        }

        private static void ResolveBodyFixedSurfaceCoordinates(
            CelestialBody body,
            double referenceUT,
            double ut,
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            try
            {
                Vector3d worldPos = body.position + position;
                latitude = body.GetLatitude(worldPos);
                longitude = body.GetLongitude(worldPos);

                if (!double.IsNaN(referenceUT)
                    && !double.IsNaN(ut)
                    && !double.IsNaN(body.rotationPeriod)
                    && !double.IsInfinity(body.rotationPeriod)
                    && Math.Abs(body.rotationPeriod) > double.Epsilon)
                {
                    longitude -= ((ut - referenceUT) * 360.0) / body.rotationPeriod;
                }

                longitude = NormalizeLongitudeDegrees(longitude);
            }
            catch
            {
                GetApproximateLatitudeLongitude(position, out latitude, out longitude);
            }
        }

        internal static bool TryBuildStartStateFromSegment(
            OrbitSegment segment,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            out BallisticStateVector startState)
        {
            startState = default(BallisticStateVector);
            if (string.IsNullOrEmpty(segment.bodyName)
                || !bodies.TryGetValue(segment.bodyName, out ExtrapolationBody body)
                || !BallisticExtrapolator.TryPropagate(
                    segment,
                    body.GravitationalParameter,
                    segment.endUT,
                    out Vector3d position,
                    out Vector3d velocity)
                || !IsFinite(position)
                || !IsFinite(velocity))
                return false;

            startState = new BallisticStateVector
            {
                ut = segment.endUT,
                bodyName = segment.bodyName,
                position = position,
                velocity = velocity,
                orbitalFrameRotation = segment.orbitalFrameRotation
            };
            return true;
        }

        internal static void SeedPredictedSegmentOrbitalFrameRotations(
            string recordingId,
            IList<OrbitSegment> segments,
            UnityEngine.Quaternion frozenWorldRotation,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies)
        {
            if (segments == null || bodies == null)
                return;

            int seededCount = 0;
            UnityEngine.Quaternion currentWorldRotation = frozenWorldRotation;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment segment = segments[i];
                if (string.IsNullOrEmpty(segment.bodyName)
                    || !bodies.TryGetValue(segment.bodyName, out ExtrapolationBody body)
                    || body == null)
                {
                    continue;
                }

                bool hasOrbitalFrameRotation = BallisticExtrapolator.HasOrbitalFrameRotation(segment.orbitalFrameRotation);
                if (!hasOrbitalFrameRotation
                    && BallisticExtrapolator.TryPropagate(
                        segment,
                        body.GravitationalParameter,
                        segment.startUT,
                        out Vector3d startPosition,
                        out Vector3d startVelocity)
                    && IsFinite(startPosition)
                    && IsFinite(startVelocity))
                {
                    segment.orbitalFrameRotation = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                        currentWorldRotation,
                        startPosition,
                        startVelocity);
                    segments[i] = segment;
                    hasOrbitalFrameRotation = true;
                    seededCount++;
                }

                if (!hasOrbitalFrameRotation
                    || !BallisticExtrapolator.TryPropagate(
                        segment,
                        body.GravitationalParameter,
                        segment.endUT,
                        out Vector3d endPosition,
                        out Vector3d endVelocity)
                    || !IsFinite(endPosition)
                    || !IsFinite(endVelocity))
                {
                    continue;
                }

                currentWorldRotation = BallisticExtrapolator.ResolveWorldRotation(
                    segment.orbitalFrameRotation,
                    endPosition,
                    endVelocity);
            }

            if (seededCount > 0)
            {
                ParsekLog.Verbose("Extrapolator",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "TryFinalizeRecording: seeded orbital-frame rotation on {0}/{1} predicted segments for '{2}'",
                        seededCount,
                        segments.Count,
                        recordingId ?? "(null)"));
            }
        }

        /// <summary>
        /// Decides whether the extrapolator's result should be committed to the
        /// recording. A normal ballistic extrapolation applies when it produced
        /// new segments or advanced the terminal UT past the recording's last
        /// known endpoint. A <see cref="ExtrapolationFailureReason.SubSurfaceStart"/>
        /// result is a terminal classification (Destroyed): the extrapolator
        /// intentionally skips segment emission and UT advancement because the
        /// vessel's live orbit state is already nonsense, so the committed
        /// verdict must survive even without segments. Without this carve-out,
        /// <see cref="ParsekFlight"/>'s fallback
        /// (<c>DetermineTerminalState(v.situation, v)</c>) overwrites the
        /// Destroyed verdict with KSP's last known situation — typically
        /// <c>SUB_ORBITAL</c>, which silently undoes the fix.
        /// </summary>
        internal static bool ShouldApplyExtrapolatorResult(
            int appendedSegmentCount,
            double terminalUT,
            double recordingEndUT,
            ExtrapolationFailureReason failureReason)
        {
            if (failureReason == ExtrapolationFailureReason.SubSurfaceStart)
                return true;
            if (appendedSegmentCount > 0)
                return true;
            if (double.IsNaN(terminalUT))
                return false;
            return double.IsNaN(recordingEndUT) || terminalUT > recordingEndUT;
        }

        private static bool TryBuildStartStateFromVessel(
            Vessel vessel,
            double commitUT,
            out BallisticStateVector startState)
        {
            startState = default(BallisticStateVector);
            if (vessel?.orbit == null || vessel.orbit.referenceBody == null)
                return false;

            Vector3d position = vessel.orbit.getPositionAtUT(commitUT);
            Vector3d velocity = vessel.orbit.getOrbitalVelocityAtUT(commitUT);
            if (!IsFinite(position) || !IsFinite(velocity))
                return false;

            startState = new BallisticStateVector
            {
                ut = commitUT,
                bodyName = vessel.orbit.referenceBody.name,
                position = position,
                velocity = velocity,
                orbitalFrameRotation = BallisticExtrapolator.ComputeOrbitalFrameRotationFromState(
                    vessel.transform.rotation,
                    position,
                    velocity)
            };
            return true;
        }

        private static double GetBallisticCutoffAltitude(CelestialBody body)
        {
            if (body == null)
                return 0.0;

            return body.atmosphere && body.atmosphereDepth > 0.0
                ? body.atmosphereDepth
                : 0.0;
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
        }

        private static double Magnitude(Vector3d value)
        {
            return Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private static void GetApproximateLatitudeLongitude(
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            double radius = Math.Max(1e-8, position.magnitude);
            latitude = Math.Asin(Math.Max(-1.0, Math.Min(1.0, position.z / radius))) * 57.29577951308232;
            longitude = Math.Atan2(position.y, position.x) * 57.29577951308232;
        }

        private static double NormalizeLongitudeDegrees(double longitude)
        {
            if (double.IsNaN(longitude) || double.IsInfinity(longitude))
                return longitude;

            double normalized = longitude % 360.0;
            if (normalized <= -180.0)
                normalized += 360.0;
            else if (normalized > 180.0)
                normalized -= 360.0;

            return normalized;
        }

        private static void StampTerminalOrbitFromSegment(Recording recording, OrbitSegment segment)
        {
            if (recording == null || string.IsNullOrEmpty(segment.bodyName))
                return;

            StampTerminalOrbit(recording, RecordingFinalizationTerminalOrbit.FromSegment(segment));
        }

        private static void StampTerminalOrbit(
            Recording recording,
            RecordingFinalizationTerminalOrbit terminalOrbit)
        {
            if (recording == null || string.IsNullOrEmpty(terminalOrbit.bodyName))
                return;

            recording.TerminalOrbitInclination = terminalOrbit.inclination;
            recording.TerminalOrbitEccentricity = terminalOrbit.eccentricity;
            recording.TerminalOrbitSemiMajorAxis = terminalOrbit.semiMajorAxis;
            recording.TerminalOrbitLAN = terminalOrbit.longitudeOfAscendingNode;
            recording.TerminalOrbitArgumentOfPeriapsis = terminalOrbit.argumentOfPeriapsis;
            recording.TerminalOrbitMeanAnomalyAtEpoch = terminalOrbit.meanAnomalyAtEpoch;
            recording.TerminalOrbitEpoch = terminalOrbit.epoch;
            recording.TerminalOrbitBody = terminalOrbit.bodyName;
        }

        private static void Apply(
            Recording recording,
            IncompleteBallisticFinalizationResult result,
            string logContext)
        {
            if (recording == null)
                return;

            if (result.appendedOrbitSegments != null && result.appendedOrbitSegments.Count > 0)
                recording.OrbitSegments.AddRange(result.appendedOrbitSegments);

            recording.TerminalStateValue = result.terminalState.Value;
            recording.ExplicitEndUT = result.terminalUT;
            if (result.terminalOrbit.HasValue)
                StampTerminalOrbit(recording, result.terminalOrbit.Value);

            bool ghostOnlySnapshot = result.vesselSnapshot == null && result.ghostVisualSnapshot != null;
            if (result.vesselSnapshot != null)
            {
                recording.VesselSnapshot = result.vesselSnapshot.CreateCopy();
                recording.GhostVisualSnapshot = result.ghostVisualSnapshot != null
                    ? result.ghostVisualSnapshot.CreateCopy()
                    : recording.VesselSnapshot.CreateCopy();
            }
            else if (result.ghostVisualSnapshot != null)
            {
                recording.GhostVisualSnapshot = result.ghostVisualSnapshot.CreateCopy();
                ParsekLog.Verbose("Extrapolator",
                    $"{logContext ?? "SceneExitFinalizer"}: applied ghost-only scene-exit finalization for " +
                    $"'{recording.RecordingId}' without a vessel snapshot");
            }

            if (result.terminalState.Value == TerminalState.Landed
                || result.terminalState.Value == TerminalState.Splashed)
            {
                if (result.terminalPosition.HasValue)
                {
                    recording.TerminalPosition = result.terminalPosition;
                    recording.TerrainHeightAtEnd = result.terrainHeightAtEnd.HasValue
                        ? result.terrainHeightAtEnd.Value
                        : double.NaN;
                }
                else if (ghostOnlySnapshot)
                {
                    bool hadSurfaceMetadata = recording.TerminalPosition.HasValue
                        || !double.IsNaN(recording.TerrainHeightAtEnd);
                    ParsekLog.Warn("Extrapolator",
                        $"{logContext ?? "SceneExitFinalizer"}: ghost-only surface finalization for " +
                        $"'{recording.RecordingId}' supplied no terminalPosition/terrainHeight — " +
                        (hadSurfaceMetadata
                            ? "keeping existing surface metadata"
                            : "surface metadata remains unavailable"));
                }
                else
                {
                    recording.TerminalPosition = null;
                    recording.TerrainHeightAtEnd = double.NaN;
                }
            }
            else
            {
                recording.TerminalPosition = null;
                recording.TerrainHeightAtEnd = double.NaN;
            }

            recording.MarkFilesDirty();
        }
    }
}
