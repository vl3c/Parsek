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
    }

    internal static class IncompleteBallisticSceneExitFinalizer
    {
        internal delegate bool TryFinalizeDelegate(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result);

        internal static TryFinalizeDelegate TryFinalizeHook;
        internal static TryFinalizeDelegate TryFinalizeOverrideForTesting;

        internal static void ResetForTesting()
        {
            TryFinalizeHook = null;
            TryFinalizeOverrideForTesting = null;
        }

        internal static bool TryApply(
            Recording recording,
            Vessel vessel,
            double commitUT,
            string logContext)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            var finalize = TryFinalizeHook ?? TryFinalizeOverrideForTesting ?? TryFinalizeRecording;
            bool usingHook = TryFinalizeHook != null;
            if (!finalize(recording, vessel, commitUT, out var result))
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

            var appendedSegments = new List<OrbitSegment>();
            var snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(vessel, commitUT);
            if (snapshot.Segments != null && snapshot.Segments.Count > 0)
            {
                appendedSegments.AddRange(snapshot.Segments);
                result.patchedSegmentCount = snapshot.CapturedPatchCount;
            }
            else if (snapshot.FailureReason != PatchedConicSnapshotFailureReason.None)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: patched-conic snapshot failed for '{recording.RecordingId}' " +
                    $"with {snapshot.FailureReason}; falling back to live orbit state");
            }

            ConfigNode snapshotNode = VesselSpawner.TryBackupSnapshot(vessel);
            if (snapshotNode != null)
            {
                result.vesselSnapshot = snapshotNode;
                result.ghostVisualSnapshot = snapshotNode.CreateCopy();
            }

            if (snapshot.StoppedBeforeManeuver && appendedSegments.Count > 0)
            {
                OrbitSegment terminalSegment = appendedSegments[appendedSegments.Count - 1];
                StampTerminalOrbitFromSegment(recording, terminalSegment);
                result.appendedOrbitSegments = appendedSegments;
                result.terminalState = TerminalState.Orbiting;
                result.terminalUT = terminalSegment.endUT;
                return true;
            }

            if (TryShortCircuitSolverPredictedImpact(
                recording.RecordingId,
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
                        $"TryFinalizeRecording: failed to propagate terminal predicted segment for '{recording.RecordingId}' " +
                        $"(body={lastSegment.bodyName ?? "(null)"}, endUT={lastSegment.endUT:F1})");
                    return false;
                }
            }
            else if (!TryBuildStartStateFromVessel(vessel, commitUT, out startState))
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: failed to sample live vessel orbit for '{recording.RecordingId}' at commitUT={commitUT:F1}");
                return false;
            }

            ExtrapolationResult extrapolated = BallisticExtrapolator.Extrapolate(startState, bodies);
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

            if (extrapolated.terminalState == TerminalState.Orbiting && appendedSegments.Count > 0)
                StampTerminalOrbitFromSegment(recording, appendedSegments[appendedSegments.Count - 1]);

            bool applied = appendedSegments.Count > 0
                || (!double.IsNaN(result.terminalUT)
                    && (double.IsNaN(recording.EndUT) || result.terminalUT > recording.EndUT));
            if (!applied)
            {
                ParsekLog.Warn("Extrapolator",
                    $"TryFinalizeRecording: no extended lifetime produced for '{recording.RecordingId}' " +
                    $"(terminal={result.terminalState}, terminalUT={result.terminalUT:F1}, failure={extrapolated.failureReason})");
            }

            return applied;
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

        private static bool TryBuildStartStateFromSegment(
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
                velocity = velocity
            };
            return true;
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
                velocity = velocity
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

            recording.TerminalOrbitInclination = segment.inclination;
            recording.TerminalOrbitEccentricity = segment.eccentricity;
            recording.TerminalOrbitSemiMajorAxis = segment.semiMajorAxis;
            recording.TerminalOrbitLAN = segment.longitudeOfAscendingNode;
            recording.TerminalOrbitArgumentOfPeriapsis = segment.argumentOfPeriapsis;
            recording.TerminalOrbitMeanAnomalyAtEpoch = segment.meanAnomalyAtEpoch;
            recording.TerminalOrbitEpoch = segment.epoch;
            recording.TerminalOrbitBody = segment.bodyName;
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
