using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal delegate bool TerrainAltitudeResolver(double latitude, double longitude, out double altitude);
    internal delegate void ParentFrameStateResolver(double ut, out Vector3d position, out Vector3d velocity);
    internal delegate void SurfaceCoordinatesResolver(double ut, Vector3d position, out double latitude, out double longitude);

    internal enum ExtrapolationFailureReason
    {
        None = 0,
        MissingBody,
        MissingParentBody,
        MissingParentFrameResolver,
        DegenerateStateVector,
        PqsUnavailable,
        // Start state places the vessel measurably below the body's surface —
        // a fingerprint of a destroyed/invalidated vessel whose live orbit
        // state returned garbage after `PatchedConicSnapshot` failed with
        // `NullSolver`. Classify the recording as Destroyed immediately rather
        // than running the extrapolator against nonsense coordinates (which
        // silently horizon-caps to Orbiting).
        SubSurfaceStart
    }

    internal struct ExtrapolationLimits
    {
        public double maxHorizonYears;
        public int maxSoiTransitions;
        public double soiSampleStep;

        public static ExtrapolationLimits Default => new ExtrapolationLimits
        {
            maxHorizonYears = 50.0,
            maxSoiTransitions = 8,
            soiSampleStep = 3600.0
        };
    }

    internal struct BallisticStateVector
    {
        public double ut;
        public string bodyName;
        public Vector3d position;
        public Vector3d velocity;
        // Optional frozen playback attitude. When present, extrapolated segments reuse
        // this orbital-frame-relative rotation so predicted playback can hold the last
        // captured attitude instead of falling back to prograde.
        public Quaternion orbitalFrameRotation;
    }

    internal struct ExtrapolationResult
    {
        public TerminalState terminalState;
        public double terminalUT;
        public string terminalBodyName;
        public Vector3d terminalPosition;
        public Vector3d terminalVelocity;
        public List<OrbitSegment> segments;
        public ExtrapolationFailureReason failureReason;
    }

    internal sealed class ExtrapolationBody
    {
        public string Name;
        public string ParentBodyName;
        public double GravitationalParameter;
        public double Radius;
        public double AtmosphereDepth;
        public double SphereOfInfluence;
        public TerrainAltitudeResolver TerrainAltitude;
        public ParentFrameStateResolver ParentFrameState;
        public SurfaceCoordinatesResolver SurfaceCoordinates;

        public bool HasAtmosphere => AtmosphereDepth > 0.0;
    }

    internal static class BallisticExtrapolator
    {
        private const string LogTag = "Extrapolator";
        private const double SecondsPerYear = 365.0 * 24.0 * 60.0 * 60.0;
        private const double OrbitEpsilon = 1e-9;
        private const double StateVectorEpsilon = 1e-8;
        private const int MaxLocalCutoffSamples = 720;
        private const int MaxEncounterSamples = 4096;
        private const int RootRefinementIterations = 48;
        private const double DefaultCutoffSampleStep = 30.0;
        private const double LocalCutoffDenseWindowSeconds = MaxLocalCutoffSamples * DefaultCutoffSampleStep;
        private const double ImmediateEventEpsilon = 1e-6;
        // Any start-state altitude below this is treated as a destroyed-vessel
        // fingerprint (see `ExtrapolationFailureReason.SubSurfaceStart`).
        // Chosen well below Kerbin's deepest natural terrain (~5 km) so
        // legitimate surface-hugging trajectories (e.g. sea-level approach) do
        // not trip it, but well above the failure-case signature observed in
        // the playtest log (-594 km, vessel position collapsed to the body
        // frame origin after KSP invalidated the patched-conic solver).
        internal const double SubSurfaceDestroyedAltitude = -100.0;

        private enum EventKind
        {
            None = 0,
            Destroyed,
            ParentExit,
            ChildEntry,
            Horizon
        }

        private struct EventCandidate
        {
            public EventKind Kind;
            public double UT;
            public Vector3d Position;
            public Vector3d Velocity;
            public ExtrapolationBody ChildBody;
        }

        private struct StateSample
        {
            public double UT;
            public Vector3d Position;
            public Vector3d Velocity;
            public double Altitude;
        }

        internal static bool ShouldExtrapolate(
            Vessel.Situations situation,
            double eccentricity,
            double periapsisAltitude,
            double cutoffAltitude)
        {
            switch (situation)
            {
                case Vessel.Situations.FLYING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                    return true;

                case Vessel.Situations.ORBITING:
                    if (double.IsNaN(periapsisAltitude))
                        return true;

                    if (eccentricity >= 1.0)
                        return true;

                    return periapsisAltitude <= cutoffAltitude;

                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.DOCKED:
                default:
                    return false;
            }
        }

        internal static ExtrapolationResult Extrapolate(
            BallisticStateVector startState,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            ExtrapolationLimits? limitsOverride = null)
        {
            ExtrapolationLimits limits = limitsOverride ?? ExtrapolationLimits.Default;
            var result = new ExtrapolationResult
            {
                terminalState = TerminalState.Orbiting,
                terminalUT = startState.ut,
                terminalBodyName = startState.bodyName,
                terminalPosition = startState.position,
                terminalVelocity = startState.velocity,
                segments = new List<OrbitSegment>(),
                failureReason = ExtrapolationFailureReason.None
            };

            if (!TryGetBody(bodies, startState.bodyName, out ExtrapolationBody currentBody))
            {
                result.failureReason = ExtrapolationFailureReason.MissingBody;
                ParsekLog.Warn(LogTag, string.Format(
                    CultureInfo.InvariantCulture,
                    "Start rejected: missing body='{0}' at ut={1:F3}",
                    startState.bodyName ?? "(null)",
                    startState.ut));
                return result;
            }

            double horizonUT = startState.ut + Math.Max(0.0, limits.maxHorizonYears) * SecondsPerYear;
            var currentState = startState;
            int soiTransitions = 0;
            string suppressedImmediateChildEntryBodyName = null;
            string suppressedImmediateParentExitBodyName = null;
            double suppressedImmediateUT = double.NaN;

            double startAltitude = Magnitude(startState.position) - currentBody.Radius;
            ParsekLog.Info(LogTag, string.Format(
                CultureInfo.InvariantCulture,
                "Start: body={0} ut={1:F3} alt={2:F1} horizonUT={3:F3} maxYears={4:F3} maxSoiTransitions={5}",
                currentBody.Name,
                startState.ut,
                startAltitude,
                horizonUT,
                Math.Max(0.0, limits.maxHorizonYears),
                Math.Max(0, limits.maxSoiTransitions)));

            // Sub-surface start classifies as Destroyed. Observed fingerprint:
            // `PatchedConicSnapshot` fails with `NullSolver` (vessel's orbit
            // solver already torn down by KSP destruction), the finalizer's
            // `TryBuildStartStateFromVessel` fallback samples garbage
            // coordinates (position collapsed to body frame origin → altitude
            // ≈ -Radius), and without this guard the extrapolator runs its
            // surface scan against unreachable ground, finds no intersection
            // before `horizon-cap` fires, and silently returns Orbiting.
            // Classify the recording as Destroyed and stop immediately so the
            // row enters "Unfinished Flights" where the player can re-fly it.
            if (startAltitude < SubSurfaceDestroyedAltitude)
            {
                result.terminalState = TerminalState.Destroyed;
                result.terminalUT = startState.ut;
                result.terminalBodyName = currentBody.Name;
                result.terminalPosition = startState.position;
                result.terminalVelocity = startState.velocity;
                result.failureReason = ExtrapolationFailureReason.SubSurfaceStart;
                ParsekLog.Verbose(LogTag, string.Format(
                    CultureInfo.InvariantCulture,
                    "Start rejected: sub-surface state body={0} ut={1:F3} alt={2:F1} " +
                    "(threshold={3:F1}); classifying recording as Destroyed",
                    currentBody.Name,
                    startState.ut,
                    startAltitude,
                    SubSurfaceDestroyedAltitude));
                return result;
            }

            while (currentState.ut < horizonUT)
            {
                if (!TwoBodyOrbit.TryCreate(
                    currentState.position,
                    currentState.velocity,
                    currentBody.GravitationalParameter,
                    currentState.ut,
                    out TwoBodyOrbit orbit))
                {
                    result.failureReason = ExtrapolationFailureReason.DegenerateStateVector;
                    result.terminalUT = currentState.ut;
                    result.terminalBodyName = currentBody.Name;
                    result.terminalPosition = currentState.position;
                    result.terminalVelocity = currentState.velocity;
                    ParsekLog.Error(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Terminal reason=degenerate-state: body={0} ut={1:F3} pos=({2:F1},{3:F1},{4:F1}) vel=({5:F3},{6:F3},{7:F3})",
                        currentBody.Name,
                        currentState.ut,
                        currentState.position.x,
                        currentState.position.y,
                        currentState.position.z,
                        currentState.velocity.x,
                        currentState.velocity.y,
                        currentState.velocity.z));
                    return result;
                }
                orbit.BodyRadius = currentBody.Radius;

                EventCandidate? parentExit = FindParentExit(
                    orbit,
                    currentBody,
                    currentState.ut,
                    horizonUT,
                    limits,
                    suppressedImmediateParentExitBodyName,
                    suppressedImmediateUT);

                double localCutoffSearchEndUT = horizonUT;
                if (parentExit.HasValue)
                    localCutoffSearchEndUT = Math.Min(localCutoffSearchEndUT, parentExit.Value.UT);
                if (orbit.IsElliptic && !double.IsInfinity(orbit.Period))
                    localCutoffSearchEndUT = Math.Min(localCutoffSearchEndUT, currentState.ut + orbit.Period);

                EventCandidate? localCutoff = FindLocalCutoff(
                    orbit,
                    currentBody,
                    currentState.ut,
                    localCutoffSearchEndUT,
                    ref result.failureReason);

                double childSearchEndUT = horizonUT;
                if (parentExit.HasValue)
                    childSearchEndUT = Math.Min(childSearchEndUT, parentExit.Value.UT);
                if (localCutoff.HasValue)
                    childSearchEndUT = Math.Min(childSearchEndUT, localCutoff.Value.UT);

                EventCandidate? childEntry = FindChildEntry(
                    orbit,
                    currentBody,
                    bodies,
                    currentState.ut,
                    childSearchEndUT,
                    limits,
                    suppressedImmediateChildEntryBodyName,
                    suppressedImmediateUT);

                EventCandidate chosen = ChooseEarliestEvent(
                    localCutoff,
                    childEntry,
                    parentExit,
                    horizonUT,
                    orbit);

                double segmentEndUT = Math.Max(currentState.ut, Math.Min(chosen.UT, horizonUT));
                bool immediateSoiTransition =
                    (chosen.Kind == EventKind.ParentExit || chosen.Kind == EventKind.ChildEntry)
                    && segmentEndUT <= currentState.ut + ImmediateEventEpsilon;

                if (!immediateSoiTransition)
                {
                    result.segments.Add(CreateSegment(
                        orbit,
                        currentBody.Name,
                        currentState.ut,
                        segmentEndUT,
                        currentState.orbitalFrameRotation));
                }

                if (chosen.Kind == EventKind.Destroyed)
                {
                    result.terminalState = TerminalState.Destroyed;
                    result.terminalUT = chosen.UT;
                    result.terminalBodyName = currentBody.Name;
                    result.terminalPosition = chosen.Position;
                    result.terminalVelocity = chosen.Velocity;
                    ParsekLog.Info(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Terminal reason=cutoff: body={0} ut={1:F3} alt={2:F1} failure={3}",
                        currentBody.Name,
                        chosen.UT,
                        Magnitude(chosen.Position) - currentBody.Radius,
                        result.failureReason));
                    return result;
                }

                if (chosen.Kind == EventKind.Horizon)
                {
                    orbit.GetStateAtUT(segmentEndUT, out Vector3d horizonPosition, out Vector3d horizonVelocity);
                    result.terminalState = TerminalState.Orbiting;
                    result.terminalUT = segmentEndUT;
                    result.terminalBodyName = currentBody.Name;
                    result.terminalPosition = horizonPosition;
                    result.terminalVelocity = horizonVelocity;
                    ParsekLog.Warn(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Terminal reason=horizon-cap: body={0} ut={1:F3} soiTransitions={2}",
                        currentBody.Name,
                        segmentEndUT,
                        soiTransitions));
                    return result;
                }

                soiTransitions++;
                if (soiTransitions >= Math.Max(0, limits.maxSoiTransitions))
                {
                    result.terminalState = TerminalState.Orbiting;
                    result.terminalUT = chosen.UT;
                    result.terminalBodyName = currentBody.Name;
                    result.terminalPosition = chosen.Position;
                    result.terminalVelocity = chosen.Velocity;
                    ParsekLog.Warn(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Terminal reason=soi-transition-cap: body={0} ut={1:F3} soiTransitions={2}",
                        currentBody.Name,
                        chosen.UT,
                        soiTransitions));
                    return result;
                }

                if (chosen.Kind == EventKind.ParentExit)
                {
                    if (!TryGetBody(bodies, currentBody.ParentBodyName, out ExtrapolationBody parentBody))
                    {
                        result.failureReason = ExtrapolationFailureReason.MissingParentBody;
                        result.terminalUT = chosen.UT;
                        result.terminalBodyName = currentBody.Name;
                        result.terminalPosition = chosen.Position;
                        result.terminalVelocity = chosen.Velocity;
                        ParsekLog.Warn(LogTag, string.Format(
                            CultureInfo.InvariantCulture,
                            "Terminal reason=missing-parent-body: body={0} parent={1} ut={2:F3}",
                            currentBody.Name,
                            currentBody.ParentBodyName ?? "(null)",
                            chosen.UT));
                        return result;
                    }
                    if (currentBody.ParentFrameState == null)
                    {
                        result.failureReason = ExtrapolationFailureReason.MissingParentFrameResolver;
                        result.terminalState = TerminalState.Orbiting;
                        result.terminalUT = chosen.UT;
                        result.terminalBodyName = currentBody.Name;
                        result.terminalPosition = chosen.Position;
                        result.terminalVelocity = chosen.Velocity;
                        ParsekLog.Warn(LogTag, string.Format(
                            CultureInfo.InvariantCulture,
                            "Terminal reason=missing-parent-frame-resolver: body={0} parent={1} ut={2:F3}",
                            currentBody.Name,
                            currentBody.ParentBodyName ?? "(null)",
                            chosen.UT));
                        return result;
                    }

                    GetBodyStateRelativeToParent(currentBody, chosen.UT, out Vector3d bodyPosition, out Vector3d bodyVelocity);
                    ParsekLog.Info(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "SOI transition: child={0} parent={1} ut={2:F3} kind=ParentExit immediate={3}",
                        currentBody.Name,
                        parentBody.Name,
                        chosen.UT,
                        immediateSoiTransition));
                    currentState = new BallisticStateVector
                    {
                        ut = chosen.UT,
                        bodyName = parentBody.Name,
                        position = chosen.Position + bodyPosition,
                        velocity = chosen.Velocity + bodyVelocity,
                        orbitalFrameRotation = ReframeOrbitalFrameRotation(
                            currentState.orbitalFrameRotation,
                            chosen.Position,
                            chosen.Velocity,
                            chosen.Position + bodyPosition,
                            chosen.Velocity + bodyVelocity)
                    };
                    suppressedImmediateChildEntryBodyName = immediateSoiTransition
                        ? currentBody.Name
                        : null;
                    suppressedImmediateParentExitBodyName = null;
                    suppressedImmediateUT = immediateSoiTransition
                        ? chosen.UT
                        : double.NaN;
                    currentBody = parentBody;
                    continue;
                }

                if (chosen.Kind == EventKind.ChildEntry && chosen.ChildBody != null)
                {
                    GetBodyStateRelativeToParent(chosen.ChildBody, chosen.UT, out Vector3d childPosition, out Vector3d childVelocity);
                    ParsekLog.Info(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "SOI transition: parent={0} child={1} ut={2:F3} kind=ChildEntry immediate={3}",
                        currentBody.Name,
                        chosen.ChildBody.Name,
                        chosen.UT,
                        immediateSoiTransition));
                    currentState = new BallisticStateVector
                    {
                        ut = chosen.UT,
                        bodyName = chosen.ChildBody.Name,
                        position = chosen.Position - childPosition,
                        velocity = chosen.Velocity - childVelocity,
                        orbitalFrameRotation = ReframeOrbitalFrameRotation(
                            currentState.orbitalFrameRotation,
                            chosen.Position,
                            chosen.Velocity,
                            chosen.Position - childPosition,
                            chosen.Velocity - childVelocity)
                    };
                    suppressedImmediateParentExitBodyName = immediateSoiTransition
                        ? currentBody.Name
                        : null;
                    suppressedImmediateChildEntryBodyName = null;
                    suppressedImmediateUT = immediateSoiTransition
                        ? chosen.UT
                        : double.NaN;
                    currentBody = chosen.ChildBody;
                    continue;
                }

                result.terminalState = TerminalState.Orbiting;
                result.terminalUT = segmentEndUT;
                result.terminalBodyName = currentBody.Name;
                result.terminalPosition = chosen.Position;
                result.terminalVelocity = chosen.Velocity;
                ParsekLog.Warn(LogTag, string.Format(
                    CultureInfo.InvariantCulture,
                    "Terminal reason=unexpected-event-fallthrough: body={0} event={1} ut={2:F3}",
                    currentBody.Name,
                    chosen.Kind,
                    segmentEndUT));
                return result;
            }

            result.terminalState = TerminalState.Orbiting;
            result.terminalUT = horizonUT;
            result.terminalBodyName = currentBody.Name;
            result.terminalPosition = currentState.position;
            result.terminalVelocity = currentState.velocity;
            ParsekLog.Warn(LogTag, string.Format(
                CultureInfo.InvariantCulture,
                "Terminal reason=loop-horizon-exit: body={0} ut={1:F3} soiTransitions={2}",
                currentBody.Name,
                horizonUT,
                soiTransitions));
            return result;
        }

        internal static bool HasOrbitalFrameRotation(Quaternion orbitalFrameRotation)
        {
            return orbitalFrameRotation.x != 0f
                || orbitalFrameRotation.y != 0f
                || orbitalFrameRotation.z != 0f
                || orbitalFrameRotation.w != 0f;
        }

        internal static Quaternion ComputeOrbitalFrameRotationFromState(
            Quaternion worldRotation,
            Vector3d position,
            Vector3d velocity)
        {
            double radius = Magnitude(position);
            if (radius <= StateVectorEpsilon)
                return Quaternion.identity;

            Vector3d radialOut = position / radius;
            return NormalizeAndCanonicalizeQuaternion(
                TrajectoryMath.ComputeOrbitalFrameRotation(worldRotation, velocity, radialOut));
        }

        internal static Quaternion ResolveWorldRotation(
            Quaternion orbitalFrameRotation,
            Vector3d position,
            Vector3d velocity)
        {
            if (!HasOrbitalFrameRotation(orbitalFrameRotation))
                return default(Quaternion);

            Quaternion inverseOrbitalFrame = ComputeOrbitalFrameRotationFromState(
                Quaternion.identity,
                position,
                velocity);
            Quaternion orbitalFrame = TrajectoryMath.PureInverse(inverseOrbitalFrame);
            return NormalizeAndCanonicalizeQuaternion(
                TrajectoryMath.PureMultiply(orbitalFrame, orbitalFrameRotation));
        }

        internal static Quaternion ReframeOrbitalFrameRotation(
            Quaternion orbitalFrameRotation,
            Vector3d fromPosition,
            Vector3d fromVelocity,
            Vector3d toPosition,
            Vector3d toVelocity)
        {
            if (!HasOrbitalFrameRotation(orbitalFrameRotation))
                return default(Quaternion);

            Quaternion worldRotation = ResolveWorldRotation(
                orbitalFrameRotation,
                fromPosition,
                fromVelocity);
            return ComputeOrbitalFrameRotationFromState(
                worldRotation,
                toPosition,
                toVelocity);
        }

        private static Quaternion CanonicalizeQuaternionSign(Quaternion quaternion)
        {
            if (quaternion.w < 0f
                || (quaternion.w == 0f
                    && (quaternion.z < 0f
                        || (quaternion.z == 0f
                            && (quaternion.y < 0f
                                || (quaternion.y == 0f && quaternion.x < 0f))))))
            {
                return new Quaternion(
                    -quaternion.x,
                    -quaternion.y,
                    -quaternion.z,
                    -quaternion.w);
            }

            return quaternion;
        }

        private static Quaternion NormalizeAndCanonicalizeQuaternion(Quaternion quaternion)
        {
            return CanonicalizeQuaternionSign(TrajectoryMath.PureNormalize(quaternion));
        }

        internal static bool TryPropagate(
            OrbitSegment segment,
            double bodyGravParameter,
            double ut,
            out Vector3d position,
            out Vector3d velocity)
        {
            if (!TwoBodyOrbit.TryCreateFromSegment(segment, bodyGravParameter, out TwoBodyOrbit orbit))
            {
                position = Vector3d.zero;
                velocity = Vector3d.zero;
                return false;
            }

            orbit.GetStateAtUT(ut, out position, out velocity);
            return true;
        }

        private static OrbitSegment CreateSegment(
            TwoBodyOrbit orbit,
            string bodyName,
            double startUT,
            double endUT,
            Quaternion orbitalFrameRotation)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = orbit.Inclination,
                eccentricity = orbit.Eccentricity,
                semiMajorAxis = orbit.SemiMajorAxis,
                longitudeOfAscendingNode = orbit.LongitudeOfAscendingNode,
                argumentOfPeriapsis = orbit.ArgumentOfPeriapsis,
                meanAnomalyAtEpoch = orbit.MeanAnomalyAtEpoch,
                epoch = orbit.Epoch,
                bodyName = bodyName,
                isPredicted = true,
                orbitalFrameRotation = orbitalFrameRotation
            };
        }

        private static EventCandidate ChooseEarliestEvent(
            EventCandidate? localCutoff,
            EventCandidate? childEntry,
            EventCandidate? parentExit,
            double horizonUT,
            TwoBodyOrbit orbit)
        {
            EventCandidate best = new EventCandidate
            {
                Kind = EventKind.Horizon,
                UT = horizonUT
            };

            if (localCutoff.HasValue && localCutoff.Value.UT < best.UT)
                best = localCutoff.Value;

            if (childEntry.HasValue && childEntry.Value.UT < best.UT)
                best = childEntry.Value;

            if (parentExit.HasValue && parentExit.Value.UT < best.UT)
                best = parentExit.Value;

            if (best.Kind == EventKind.Horizon)
                orbit.GetStateAtUT(horizonUT, out best.Position, out best.Velocity);

            return best;
        }

        private static EventCandidate? FindLocalCutoff(
            TwoBodyOrbit orbit,
            ExtrapolationBody body,
            double startUT,
            double endUT,
            ref ExtrapolationFailureReason failureReason)
        {
            if (endUT <= startUT)
                return null;

            if (body.HasAtmosphere)
            {
                EventCandidate? atmo = FindDescendingAltitudeCrossing(
                    orbit,
                    body,
                    startUT,
                    endUT);
                if (atmo.HasValue)
                    return atmo;
            }

            double sampleStartUT = startUT;
            double sampleEndUT = endUT;
            string windowReason = null;
            if (TryFindDescendingRadiusCrossingUT(orbit, startUT, endUT, body.Radius, out double seaLevelCrossingUT))
            {
                sampleStartUT = Math.Max(startUT, seaLevelCrossingUT - LocalCutoffDenseWindowSeconds);
                sampleEndUT = Math.Min(endUT, seaLevelCrossingUT + DefaultCutoffSampleStep);
                windowReason = "sea-level";
            }
            else if (TryGetNextPeriapsisUT(orbit, startUT, endUT, out double periapsisUT))
            {
                sampleStartUT = Math.Max(startUT, periapsisUT - LocalCutoffDenseWindowSeconds);
                sampleEndUT = periapsisUT;
                windowReason = "periapsis";
            }

            if ((sampleStartUT > startUT + OrbitEpsilon || sampleEndUT < endUT - OrbitEpsilon)
                && !string.IsNullOrEmpty(windowReason))
            {
                ParsekLog.Verbose(LogTag, string.Format(
                    CultureInfo.InvariantCulture,
                    "Surface scan narrowed: body={0} reason={1} scanStartUT={2:F3} scanEndUT={3:F3} requestedEndUT={4:F3}",
                    body.Name,
                    windowReason,
                    sampleStartUT,
                    sampleEndUT,
                    endUT));
            }

            List<StateSample> samples = SampleOrbitWindow(
                orbit,
                sampleStartUT,
                sampleEndUT,
                DefaultCutoffSampleStep,
                MaxLocalCutoffSamples);

            if (samples.Count == 0)
                return null;

            return FindSurfaceCrossing(
                orbit,
                body,
                samples,
                ref failureReason);
        }

        private static EventCandidate? FindDescendingAltitudeCrossing(
            TwoBodyOrbit orbit,
            ExtrapolationBody body,
            double startUT,
            double endUT)
        {
            if (!TryFindDescendingRadiusCrossingUT(
                orbit,
                startUT,
                endUT,
                body.Radius + body.AtmosphereDepth,
                out double crossingUT))
                return null;

            orbit.GetStateAtUT(crossingUT, out Vector3d position, out Vector3d velocity);
            return new EventCandidate
            {
                Kind = EventKind.Destroyed,
                UT = crossingUT,
                Position = position,
                Velocity = velocity
            };
        }

        private static EventCandidate? FindSurfaceCrossing(
            TwoBodyOrbit orbit,
            ExtrapolationBody body,
            List<StateSample> samples,
            ref ExtrapolationFailureReason failureReason)
        {
            double previousSurfaceDelta = GetSurfaceDeltaAtSample(body, samples[0], ref failureReason);

            for (int i = 1; i < samples.Count; i++)
            {
                double currentSurfaceDelta = GetSurfaceDeltaAtSample(body, samples[i], ref failureReason);
                if (previousSurfaceDelta > 0.0 && currentSurfaceDelta <= 0.0)
                {
                    ExtrapolationFailureReason refineFailureReason = failureReason;
                    double crossingUT = RefineCrossing(
                        ut => GetSurfaceDeltaAtUT(orbit, body, ut, ref refineFailureReason),
                        samples[i - 1].UT,
                        samples[i].UT);
                    failureReason = refineFailureReason;

                    orbit.GetStateAtUT(crossingUT, out Vector3d position, out Vector3d velocity);
                    return new EventCandidate
                    {
                        Kind = EventKind.Destroyed,
                        UT = crossingUT,
                        Position = position,
                        Velocity = velocity
                    };
                }

                previousSurfaceDelta = currentSurfaceDelta;
            }

            return null;
        }

        private static EventCandidate? FindParentExit(
            TwoBodyOrbit orbit,
            ExtrapolationBody body,
            double startUT,
            double horizonUT,
            ExtrapolationLimits limits,
            string suppressedImmediateParentBodyName,
            double suppressedImmediateUT)
        {
            if (string.IsNullOrEmpty(body.ParentBodyName) || body.SphereOfInfluence <= body.Radius)
                return null;

            if (orbit.IsElliptic && orbit.ApoapsisRadius <= body.SphereOfInfluence)
                return null;

            double searchEndUT = horizonUT;
            if (orbit.IsElliptic && !double.IsInfinity(orbit.Period))
                searchEndUT = Math.Min(searchEndUT, startUT + orbit.Period);
            if (searchEndUT <= startUT)
                return null;

            double step = ComputeStep(startUT, searchEndUT, limits.soiSampleStep, MaxEncounterSamples);
            double previousUT = startUT;
            double startValue = Magnitude(orbit.GetPositionAtUT(startUT)) - body.SphereOfInfluence;

            bool suppressImmediateStart =
                !double.IsNaN(suppressedImmediateUT)
                && Math.Abs(startUT - suppressedImmediateUT) <= ImmediateEventEpsilon
                && string.Equals(body.ParentBodyName, suppressedImmediateParentBodyName, StringComparison.Ordinal);
            if (startValue >= -ImmediateEventEpsilon && !suppressImmediateStart)
            {
                orbit.GetStateAtUT(startUT, out Vector3d startPosition, out Vector3d startVelocity);
                return new EventCandidate
                {
                    Kind = EventKind.ParentExit,
                    UT = startUT,
                    Position = startPosition,
                    Velocity = startVelocity
                };
            }

            for (double currentUT = Math.Min(startUT + step, searchEndUT);
                 currentUT <= searchEndUT + OrbitEpsilon;
                 currentUT = Math.Min(currentUT + step, searchEndUT))
            {
                double currentValue = Magnitude(orbit.GetPositionAtUT(currentUT)) - body.SphereOfInfluence;
                if (currentValue >= 0.0)
                {
                    double crossingUT = RefineCrossing(
                        ut => Magnitude(orbit.GetPositionAtUT(ut)) - body.SphereOfInfluence,
                        previousUT,
                        currentUT);

                    orbit.GetStateAtUT(crossingUT, out Vector3d position, out Vector3d velocity);
                    return new EventCandidate
                    {
                        Kind = EventKind.ParentExit,
                        UT = crossingUT,
                        Position = position,
                        Velocity = velocity
                    };
                }

                previousUT = currentUT;
                if (Math.Abs(searchEndUT - currentUT) < OrbitEpsilon)
                    break;
            }

            return null;
        }

        private static EventCandidate? FindChildEntry(
            TwoBodyOrbit orbit,
            ExtrapolationBody currentBody,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            double startUT,
            double endUT,
            ExtrapolationLimits limits,
            string suppressedImmediateChildBodyName,
            double suppressedImmediateUT)
        {
            if (endUT <= startUT)
                return null;

            EventCandidate? best = null;
            foreach (ExtrapolationBody candidate in bodies.Values)
            {
                if (!string.Equals(candidate.ParentBodyName, currentBody.Name, StringComparison.Ordinal))
                    continue;
                if (candidate.SphereOfInfluence <= 0.0)
                    continue;
                if (candidate.ParentFrameState == null)
                {
                    ParsekLog.Verbose(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Child entry candidate rejected: currentBody={0} child={1} missing parent-frame resolver",
                        currentBody.Name,
                        candidate.Name));
                    continue;
                }

                EventCandidate? hit = FindSingleChildEntry(
                    orbit,
                    candidate,
                    startUT,
                    endUT,
                    limits,
                    suppressedImmediateChildBodyName,
                    suppressedImmediateUT);

                if (!hit.HasValue)
                    continue;

                if (!best.HasValue || hit.Value.UT < best.Value.UT)
                    best = hit;
            }

            return best;
        }

        private static EventCandidate? FindSingleChildEntry(
            TwoBodyOrbit orbit,
            ExtrapolationBody childBody,
            double startUT,
            double endUT,
            ExtrapolationLimits limits,
            string suppressedImmediateChildBodyName,
            double suppressedImmediateUT)
        {
            double step = ComputeStep(startUT, endUT, limits.soiSampleStep, MaxEncounterSamples);
            double previousUT = startUT;
            double startValue = GetRelativeDistanceToChild(orbit, childBody, startUT) - childBody.SphereOfInfluence;

            bool suppressImmediateStart =
                !double.IsNaN(suppressedImmediateUT)
                && Math.Abs(startUT - suppressedImmediateUT) <= ImmediateEventEpsilon
                && string.Equals(childBody.Name, suppressedImmediateChildBodyName, StringComparison.Ordinal);
            if (startValue <= ImmediateEventEpsilon && !suppressImmediateStart)
            {
                orbit.GetStateAtUT(startUT, out Vector3d startPosition, out Vector3d startVelocity);
                return new EventCandidate
                {
                    Kind = EventKind.ChildEntry,
                    UT = startUT,
                    Position = startPosition,
                    Velocity = startVelocity,
                    ChildBody = childBody
                };
            }

            for (double currentUT = Math.Min(startUT + step, endUT);
                 currentUT <= endUT + OrbitEpsilon;
                 currentUT = Math.Min(currentUT + step, endUT))
            {
                double currentValue = GetRelativeDistanceToChild(orbit, childBody, currentUT) - childBody.SphereOfInfluence;
                if (currentValue <= 0.0)
                {
                    double entryUT = RefineCrossing(
                        ut => GetRelativeDistanceToChild(orbit, childBody, ut) - childBody.SphereOfInfluence,
                        previousUT,
                        currentUT);

                    orbit.GetStateAtUT(entryUT, out Vector3d position, out Vector3d velocity);
                    return new EventCandidate
                    {
                        Kind = EventKind.ChildEntry,
                        UT = entryUT,
                        Position = position,
                        Velocity = velocity,
                        ChildBody = childBody
                    };
                }

                previousUT = currentUT;
                if (Math.Abs(endUT - currentUT) < OrbitEpsilon)
                    break;
            }

            return null;
        }

        private static double GetRelativeDistanceToChild(
            TwoBodyOrbit orbit,
            ExtrapolationBody childBody,
            double ut)
        {
            Vector3d craftPosition = orbit.GetPositionAtUT(ut);
            GetBodyStateRelativeToParent(childBody, ut, out Vector3d childPosition, out _);
            return Magnitude(craftPosition - childPosition);
        }

        private static List<StateSample> SampleOrbitWindow(
            TwoBodyOrbit orbit,
            double startUT,
            double endUT,
            double preferredStep,
            int maxSamples)
        {
            var samples = new List<StateSample>();
            if (endUT < startUT)
                return samples;

            double step = ComputeStep(startUT, endUT, preferredStep, maxSamples);
            for (double ut = startUT; ut <= endUT + OrbitEpsilon; ut = Math.Min(ut + step, endUT))
            {
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);
                samples.Add(new StateSample
                {
                    UT = ut,
                    Position = position,
                    Velocity = velocity,
                    Altitude = Magnitude(position) - orbit.BodyRadius
                });

                if (Math.Abs(endUT - ut) < OrbitEpsilon)
                    break;
            }

            if (samples.Count == 0 || Math.Abs(samples[samples.Count - 1].UT - endUT) > OrbitEpsilon)
            {
                orbit.GetStateAtUT(endUT, out Vector3d position, out Vector3d velocity);
                samples.Add(new StateSample
                {
                    UT = endUT,
                    Position = position,
                    Velocity = velocity,
                    Altitude = Magnitude(position) - orbit.BodyRadius
                });
            }

            return samples;
        }

        private static double GetAltitudeAtUT(TwoBodyOrbit orbit, double ut)
        {
            return Magnitude(orbit.GetPositionAtUT(ut)) - orbit.BodyRadius;
        }

        private static double GetSurfaceDeltaAtUT(
            TwoBodyOrbit orbit,
            ExtrapolationBody body,
            double ut,
            ref ExtrapolationFailureReason failureReason)
        {
            orbit.GetStateAtUT(ut, out Vector3d position, out _);
            var sample = new StateSample
            {
                UT = ut,
                Position = position,
                Altitude = Magnitude(position) - orbit.BodyRadius
            };
            return GetSurfaceDeltaAtSample(body, sample, ref failureReason);
        }

        private static double GetSurfaceDeltaAtSample(
            ExtrapolationBody body,
            StateSample sample,
            ref ExtrapolationFailureReason failureReason)
        {
            double terrainAltitude = 0.0;
            if (body.TerrainAltitude != null)
            {
                GetSurfaceCoordinates(body, sample, out double latitude, out double longitude);
                if (body.TerrainAltitude(latitude, longitude, out double sampledAltitude))
                {
                    terrainAltitude = Math.Max(0.0, sampledAltitude);
                }
                else if (failureReason == ExtrapolationFailureReason.None)
                {
                    failureReason = ExtrapolationFailureReason.PqsUnavailable;
                    ParsekLog.Warn(LogTag, string.Format(
                        CultureInfo.InvariantCulture,
                        "Surface fallback: body={0} lat={1:F3} lon={2:F3} reason=PQS-unavailable -> sea-level",
                        body.Name,
                        latitude,
                        longitude));
                }
            }
            else if (failureReason == ExtrapolationFailureReason.None)
            {
                failureReason = ExtrapolationFailureReason.PqsUnavailable;
                ParsekLog.Warn(LogTag, string.Format(
                    CultureInfo.InvariantCulture,
                    "Surface fallback: body={0} reason=no-terrain-resolver -> sea-level",
                    body.Name));
            }

            return sample.Altitude - terrainAltitude;
        }

        private static void GetSurfaceCoordinates(
            ExtrapolationBody body,
            StateSample sample,
            out double latitude,
            out double longitude)
        {
            if (body != null && body.SurfaceCoordinates != null)
            {
                body.SurfaceCoordinates(sample.UT, sample.Position, out latitude, out longitude);
                return;
            }

            // Fallback when the caller does not provide a body-fixed transform: treat the
            // body-centered position vector as the local surface normal. Latitude remains exact
            // for a spherical body; longitude becomes an inertial-meridian approximation.
            ParsekLog.VerboseRateLimited(
                LogTag,
                $"surface-coords.{body?.Name ?? "(null)"}",
                $"Surface coordinates fallback: body={body?.Name ?? "(null)"} using inertial longitude approximation");
            GetApproximateLatitudeLongitude(sample.Position, out latitude, out longitude);
        }

        private static double RefineCrossing(
            Func<double, double> value,
            double lowUT,
            double highUT)
        {
            double lowValue = value(lowUT);
            double highValue = value(highUT);

            for (int i = 0; i < RootRefinementIterations; i++)
            {
                double midUT = lowUT + (highUT - lowUT) * 0.5;
                double midValue = value(midUT);

                if (Math.Abs(midValue) < 1e-6)
                    return midUT;

                bool sameSign = (lowValue <= 0.0 && midValue <= 0.0)
                    || (lowValue >= 0.0 && midValue >= 0.0);

                if (sameSign)
                {
                    lowUT = midUT;
                    lowValue = midValue;
                }
                else
                {
                    highUT = midUT;
                    highValue = midValue;
                }

                if (Math.Abs(highUT - lowUT) < 1e-6)
                    break;
            }

            return lowUT + (highUT - lowUT) * 0.5;
        }

        private static bool TryGetBody(
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            string bodyName,
            out ExtrapolationBody body)
        {
            if (bodies != null && !string.IsNullOrEmpty(bodyName)
                && bodies.TryGetValue(bodyName, out body))
                return true;

            body = null;
            return false;
        }

        private static void GetBodyStateRelativeToParent(
            ExtrapolationBody body,
            double ut,
            out Vector3d position,
            out Vector3d velocity)
        {
            if (body != null && body.ParentFrameState != null)
            {
                body.ParentFrameState(ut, out position, out velocity);
                return;
            }

            position = Vector3d.zero;
            velocity = Vector3d.zero;
        }

        private static void GetApproximateLatitudeLongitude(
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            double radius = Math.Max(StateVectorEpsilon, Magnitude(position));
            latitude = Math.Asin(Clamp(position.z / radius, -1.0, 1.0)) * Mathf.Rad2Deg;
            longitude = Math.Atan2(position.y, position.x) * Mathf.Rad2Deg;
        }

        private static bool TryGetNextPeriapsisUT(
            TwoBodyOrbit orbit,
            double startUT,
            double endUT,
            out double periapsisUT)
        {
            periapsisUT = 0.0;
            double meanMotion = orbit.GetMeanMotion();
            if (meanMotion <= 0.0)
                return false;

            double currentMeanAnomaly = orbit.GetMeanAnomalyAtUT(startUT);
            double deltaTime;
            if (orbit.IsElliptic)
            {
                double deltaMeanAnomaly = currentMeanAnomaly <= OrbitEpsilon
                    ? 0.0
                    : (Math.PI * 2.0) - currentMeanAnomaly;
                deltaTime = deltaMeanAnomaly / meanMotion;
            }
            else
            {
                if (currentMeanAnomaly > 0.0)
                    return false;

                deltaTime = -currentMeanAnomaly / meanMotion;
            }

            periapsisUT = startUT + Math.Max(0.0, deltaTime);
            return periapsisUT <= endUT + OrbitEpsilon;
        }

        private static bool TryFindDescendingRadiusCrossingUT(
            TwoBodyOrbit orbit,
            double startUT,
            double endUT,
            double targetRadius,
            out double crossingUT)
        {
            crossingUT = 0.0;
            if (targetRadius <= 0.0 || endUT <= startUT)
                return false;

            double currentRadius = Magnitude(orbit.GetPositionAtUT(startUT));
            if (currentRadius <= targetRadius + OrbitEpsilon)
                return false;
            if (targetRadius < orbit.PeriapsisRadius - OrbitEpsilon)
                return false;

            double meanMotion = orbit.GetMeanMotion();
            if (meanMotion <= 0.0)
                return false;

            if (orbit.IsElliptic)
            {
                if (orbit.Eccentricity <= OrbitEpsilon || targetRadius > orbit.ApoapsisRadius + OrbitEpsilon)
                    return false;

                double cosEccentricAnomaly = Clamp(
                    (1.0 - (targetRadius / orbit.SemiMajorAxis)) / orbit.Eccentricity,
                    -1.0,
                    1.0);
                double eccentricAnomaly = AcosClamped(cosEccentricAnomaly);
                double descendingMeanAnomaly = NormalizeAngle(
                    (Math.PI * 2.0 - eccentricAnomaly) + orbit.Eccentricity * Math.Sin(eccentricAnomaly));
                double currentMeanAnomaly = orbit.GetMeanAnomalyAtUT(startUT);
                double deltaMeanAnomaly = descendingMeanAnomaly >= currentMeanAnomaly
                    ? descendingMeanAnomaly - currentMeanAnomaly
                    : (Math.PI * 2.0 - currentMeanAnomaly) + descendingMeanAnomaly;
                crossingUT = startUT + (deltaMeanAnomaly / meanMotion);
                return crossingUT <= endUT + OrbitEpsilon;
            }

            if (orbit.Eccentricity <= 1.0 + OrbitEpsilon)
                return false;

            double coshHyperbolicAnomaly = (1.0 - (targetRadius / orbit.SemiMajorAxis)) / orbit.Eccentricity;
            if (coshHyperbolicAnomaly < 1.0)
                return false;

            double hyperbolicAnomaly = Acosh(coshHyperbolicAnomaly);
            double descendingHyperbolicMeanAnomaly = hyperbolicAnomaly - orbit.Eccentricity * Math.Sinh(hyperbolicAnomaly);
            double currentHyperbolicMeanAnomaly = orbit.GetMeanAnomalyAtUT(startUT);
            if (currentHyperbolicMeanAnomaly > descendingHyperbolicMeanAnomaly + OrbitEpsilon)
                return false;

            crossingUT = startUT + ((descendingHyperbolicMeanAnomaly - currentHyperbolicMeanAnomaly) / meanMotion);
            return crossingUT <= endUT + OrbitEpsilon;
        }

        private static double ComputeStep(
            double startUT,
            double endUT,
            double preferredStep,
            int maxSamples)
        {
            double duration = Math.Max(0.0, endUT - startUT);
            if (duration <= OrbitEpsilon)
                return 1.0;

            double step = preferredStep > 0.0 ? preferredStep : duration;
            double minimumStep = duration / Math.Max(1, maxSamples);
            if (step < minimumStep)
                step = minimumStep;

            return Math.Max(1e-3, Math.Min(step, duration));
        }

        private static double Magnitude(Vector3d value)
        {
            return Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            angle %= twoPi;
            if (angle < 0.0)
                angle += twoPi;
            return angle;
        }

        private static double AcosClamped(double value)
        {
            return Math.Acos(Clamp(value, -1.0, 1.0));
        }

        private static double Atanh(double value)
        {
            return 0.5 * Math.Log((1.0 + value) / (1.0 - value));
        }

        private static double Asinh(double value)
        {
            return Math.Log(value + Math.Sqrt((value * value) + 1.0));
        }

        private static double Acosh(double value)
        {
            return Math.Log(value + Math.Sqrt((value - 1.0) * (value + 1.0)));
        }

        private struct TwoBodyOrbit
        {
            public double BodyRadius;
            public double GravitationalParameter;
            public double Inclination;
            public double Eccentricity;
            public double SemiMajorAxis;
            public double LongitudeOfAscendingNode;
            public double ArgumentOfPeriapsis;
            public double MeanAnomalyAtEpoch;
            public double Epoch;
            public double PeriapsisRadius;
            public double ApoapsisRadius;
            public double Period;
            public bool IsElliptic;

            public double GetMeanMotion()
            {
                if (GravitationalParameter <= 0.0 || double.IsNaN(SemiMajorAxis) || double.IsInfinity(SemiMajorAxis))
                    return 0.0;

                if (IsElliptic)
                {
                    return Math.Sqrt(
                        GravitationalParameter
                        / (SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
                }

                return Math.Sqrt(
                    GravitationalParameter
                    / ((-SemiMajorAxis) * (-SemiMajorAxis) * (-SemiMajorAxis)));
            }

            public double GetMeanAnomalyAtUT(double ut)
            {
                double meanMotion = GetMeanMotion();
                if (meanMotion <= 0.0)
                    return 0.0;

                double meanAnomaly = MeanAnomalyAtEpoch + meanMotion * (ut - Epoch);
                return IsElliptic ? NormalizeAngle(meanAnomaly) : meanAnomaly;
            }

            public static bool TryCreate(
                Vector3d position,
                Vector3d velocity,
                double gravParameter,
                double epoch,
                out TwoBodyOrbit orbit)
            {
                orbit = default(TwoBodyOrbit);

                double radius = Magnitude(position);
                double speedSquared = velocity.x * velocity.x
                    + velocity.y * velocity.y
                    + velocity.z * velocity.z;
                if (radius <= StateVectorEpsilon || speedSquared <= StateVectorEpsilon)
                    return false;

                Vector3d angularMomentum = Vector3d.Cross(position, velocity);
                double angularMomentumMagnitude = Magnitude(angularMomentum);
                if (angularMomentumMagnitude <= StateVectorEpsilon)
                    return false;

                Vector3d eccentricityVector = (Vector3d.Cross(velocity, angularMomentum) / gravParameter)
                    - (position / radius);
                double eccentricity = Magnitude(eccentricityVector);

                double energy = 0.5 * speedSquared - (gravParameter / radius);
                if (Math.Abs(energy) <= StateVectorEpsilon)
                    return false;

                double semiMajorAxis = -gravParameter / (2.0 * energy);
                if (double.IsNaN(semiMajorAxis) || double.IsInfinity(semiMajorAxis))
                    return false;

                Vector3d ascendingNode = new Vector3d(-angularMomentum.y, angularMomentum.x, 0.0);
                double ascendingNodeMagnitude = Magnitude(ascendingNode);
                double inclination = AcosClamped(angularMomentum.z / angularMomentumMagnitude);
                double lan = ascendingNodeMagnitude > OrbitEpsilon
                    ? NormalizeAngle(Math.Atan2(ascendingNode.y, ascendingNode.x))
                    : 0.0;

                double argumentOfPeriapsis = 0.0;
                double trueAnomaly = 0.0;

                if (eccentricity > OrbitEpsilon)
                {
                    if (ascendingNodeMagnitude > OrbitEpsilon)
                    {
                        argumentOfPeriapsis = AcosClamped(
                            Vector3d.Dot(ascendingNode, eccentricityVector)
                            / (ascendingNodeMagnitude * eccentricity));
                        if (eccentricityVector.z < 0.0)
                            argumentOfPeriapsis = (Math.PI * 2.0) - argumentOfPeriapsis;
                    }
                    else
                    {
                        // Equatorial eccentric/hyperbolic orbit: the ascending node is undefined,
                        // so periapsis orientation must come directly from the eccentricity vector.
                        argumentOfPeriapsis = NormalizeAngle(
                            Math.Atan2(eccentricityVector.y, eccentricityVector.x));
                    }

                    trueAnomaly = AcosClamped(Vector3d.Dot(eccentricityVector, position)
                        / (eccentricity * radius));
                    if (Vector3d.Dot(position, velocity) < 0.0)
                        trueAnomaly = (Math.PI * 2.0) - trueAnomaly;
                }
                else if (ascendingNodeMagnitude > OrbitEpsilon)
                {
                    trueAnomaly = AcosClamped(Vector3d.Dot(ascendingNode, position)
                        / (ascendingNodeMagnitude * radius));
                    if (position.z < 0.0)
                        trueAnomaly = (Math.PI * 2.0) - trueAnomaly;
                }
                else
                {
                    trueAnomaly = NormalizeAngle(Math.Atan2(position.y, position.x));
                }

                double meanAnomaly;
                bool isElliptic = eccentricity < 1.0 - OrbitEpsilon;
                if (isElliptic)
                {
                    double eccentricAnomaly = 2.0 * Math.Atan2(
                        Math.Sqrt(1.0 - eccentricity) * Math.Sin(trueAnomaly * 0.5),
                        Math.Sqrt(1.0 + eccentricity) * Math.Cos(trueAnomaly * 0.5));
                    meanAnomaly = NormalizeAngle(eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly));
                }
                else if (eccentricity > 1.0 + OrbitEpsilon)
                {
                    double denominator = 1.0 + eccentricity * Math.Cos(trueAnomaly);
                    if (Math.Abs(denominator) <= StateVectorEpsilon)
                        return false;

                    double sinhHyperbolicAnomaly = Math.Sqrt((eccentricity * eccentricity) - 1.0)
                        * Math.Sin(trueAnomaly)
                        / denominator;
                    double hyperbolicAnomaly = Asinh(sinhHyperbolicAnomaly);
                    meanAnomaly = eccentricity * sinhHyperbolicAnomaly - hyperbolicAnomaly;
                }
                else
                {
                    return false;
                }

                orbit = new TwoBodyOrbit
                {
                    BodyRadius = 0.0,
                    GravitationalParameter = gravParameter,
                    Inclination = inclination,
                    Eccentricity = eccentricity,
                    SemiMajorAxis = semiMajorAxis,
                    LongitudeOfAscendingNode = lan,
                    ArgumentOfPeriapsis = argumentOfPeriapsis,
                    MeanAnomalyAtEpoch = meanAnomaly,
                    Epoch = epoch,
                    PeriapsisRadius = semiMajorAxis * (1.0 - eccentricity),
                    ApoapsisRadius = isElliptic
                        ? semiMajorAxis * (1.0 + eccentricity)
                        : double.PositiveInfinity,
                    Period = isElliptic
                        ? (Math.PI * 2.0) * Math.Sqrt(
                            (semiMajorAxis * semiMajorAxis * semiMajorAxis) / gravParameter)
                        : double.PositiveInfinity,
                    IsElliptic = isElliptic
                };
                return true;
            }

            public static bool TryCreateFromSegment(
                OrbitSegment segment,
                double gravParameter,
                out TwoBodyOrbit orbit)
            {
                orbit = new TwoBodyOrbit
                {
                    BodyRadius = 0.0,
                    GravitationalParameter = gravParameter,
                    Inclination = segment.inclination,
                    Eccentricity = segment.eccentricity,
                    SemiMajorAxis = segment.semiMajorAxis,
                    LongitudeOfAscendingNode = segment.longitudeOfAscendingNode,
                    ArgumentOfPeriapsis = segment.argumentOfPeriapsis,
                    MeanAnomalyAtEpoch = segment.meanAnomalyAtEpoch,
                    Epoch = segment.epoch,
                    PeriapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity),
                    ApoapsisRadius = segment.eccentricity < 1.0
                        ? segment.semiMajorAxis * (1.0 + segment.eccentricity)
                        : double.PositiveInfinity,
                    Period = segment.eccentricity < 1.0
                        ? (Math.PI * 2.0) * Math.Sqrt(
                            (segment.semiMajorAxis * segment.semiMajorAxis * segment.semiMajorAxis) / gravParameter)
                        : double.PositiveInfinity,
                    IsElliptic = segment.eccentricity < 1.0
                };
                return !double.IsNaN(orbit.SemiMajorAxis)
                    && !double.IsInfinity(orbit.SemiMajorAxis)
                    && gravParameter > 0.0;
            }

            public Vector3d GetPositionAtUT(double ut)
            {
                GetStateAtUT(ut, out Vector3d position, out _);
                return position;
            }

            public void GetStateAtUT(double ut, out Vector3d position, out Vector3d velocity)
            {
                double deltaTime = ut - Epoch;
                double trueAnomaly;
                double radius;

                if (IsElliptic)
                {
                    double meanMotion = Math.Sqrt(
                        GravitationalParameter
                        / (SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
                    double meanAnomaly = NormalizeAngle(MeanAnomalyAtEpoch + meanMotion * deltaTime);
                    double eccentricAnomaly = SolveEllipticKepler(meanAnomaly, Eccentricity);

                    radius = SemiMajorAxis * (1.0 - Eccentricity * Math.Cos(eccentricAnomaly));
                    trueAnomaly = 2.0 * Math.Atan2(
                        Math.Sqrt(1.0 + Eccentricity) * Math.Sin(eccentricAnomaly * 0.5),
                        Math.Sqrt(1.0 - Eccentricity) * Math.Cos(eccentricAnomaly * 0.5));
                }
                else
                {
                    double meanMotion = Math.Sqrt(
                        GravitationalParameter
                        / ((-SemiMajorAxis) * (-SemiMajorAxis) * (-SemiMajorAxis)));
                    double meanAnomaly = MeanAnomalyAtEpoch + meanMotion * deltaTime;
                    double hyperbolicAnomaly = SolveHyperbolicKepler(meanAnomaly, Eccentricity);

                    radius = SemiMajorAxis * (1.0 - Eccentricity * Math.Cosh(hyperbolicAnomaly));
                    trueAnomaly = 2.0 * Math.Atan2(
                        Math.Sqrt(Eccentricity + 1.0) * Math.Sinh(hyperbolicAnomaly * 0.5),
                        Math.Sqrt(Eccentricity - 1.0) * Math.Cosh(hyperbolicAnomaly * 0.5));
                }

                double parameter = SemiMajorAxis * (1.0 - Eccentricity * Eccentricity);
                double cosTrue = Math.Cos(trueAnomaly);
                double sinTrue = Math.Sin(trueAnomaly);

                Vector3d perifocalPosition = new Vector3d(
                    radius * cosTrue,
                    radius * sinTrue,
                    0.0);

                double velocityScale = Math.Sqrt(GravitationalParameter / parameter);
                Vector3d perifocalVelocity = new Vector3d(
                    -velocityScale * sinTrue,
                    velocityScale * (Eccentricity + cosTrue),
                    0.0);

                RotateFromPerifocal(perifocalPosition, out position);
                RotateFromPerifocal(perifocalVelocity, out velocity);
            }

            private void RotateFromPerifocal(Vector3d perifocal, out Vector3d inertial)
            {
                double cosLan = Math.Cos(LongitudeOfAscendingNode);
                double sinLan = Math.Sin(LongitudeOfAscendingNode);
                double cosArgPe = Math.Cos(ArgumentOfPeriapsis);
                double sinArgPe = Math.Sin(ArgumentOfPeriapsis);
                double cosInc = Math.Cos(Inclination);
                double sinInc = Math.Sin(Inclination);

                double r11 = cosLan * cosArgPe - sinLan * sinArgPe * cosInc;
                double r12 = -cosLan * sinArgPe - sinLan * cosArgPe * cosInc;
                double r21 = sinLan * cosArgPe + cosLan * sinArgPe * cosInc;
                double r22 = -sinLan * sinArgPe + cosLan * cosArgPe * cosInc;
                double r31 = sinArgPe * sinInc;
                double r32 = cosArgPe * sinInc;

                inertial = new Vector3d(
                    r11 * perifocal.x + r12 * perifocal.y,
                    r21 * perifocal.x + r22 * perifocal.y,
                    r31 * perifocal.x + r32 * perifocal.y);
            }

            private static double SolveEllipticKepler(double meanAnomaly, double eccentricity)
            {
                double eccentricAnomaly = meanAnomaly;
                for (int i = 0; i < 16; i++)
                {
                    double numerator = eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly) - meanAnomaly;
                    double denominator = 1.0 - eccentricity * Math.Cos(eccentricAnomaly);
                    double delta = numerator / denominator;
                    eccentricAnomaly -= delta;
                    if (Math.Abs(delta) < 1e-12)
                        break;
                }

                return eccentricAnomaly;
            }

            private static double SolveHyperbolicKepler(double meanAnomaly, double eccentricity)
            {
                double hyperbolicAnomaly = Math.Sign(meanAnomaly) == 0
                    ? 0.0
                    : Math.Log((2.0 * Math.Abs(meanAnomaly) / eccentricity) + 1.8) * Math.Sign(meanAnomaly);

                for (int i = 0; i < 24; i++)
                {
                    double sinh = Math.Sinh(hyperbolicAnomaly);
                    double cosh = Math.Cosh(hyperbolicAnomaly);
                    double numerator = eccentricity * sinh - hyperbolicAnomaly - meanAnomaly;
                    double denominator = eccentricity * cosh - 1.0;
                    double delta = numerator / denominator;
                    hyperbolicAnomaly -= delta;
                    if (Math.Abs(delta) < 1e-12)
                        break;
                }

                return hyperbolicAnomaly;
            }
        }
    }
}
