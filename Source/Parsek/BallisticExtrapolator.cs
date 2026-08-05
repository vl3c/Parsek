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
        SubSurfaceStart,
        // NOT produced by `Extrapolate`. Set by
        // `IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshot`
        // for the same destroyed-vessel population `SubSurfaceStart` was written for,
        // stated on the signal that actually carries it: the live-orbit fallback was
        // taken because KSP had torn the vessel's patched-conic solver down
        // (`PatchedConicSnapshotFailureReason.NullSolver`) and no predicted tail
        // existed.
        //
        // WHY IT EXISTS. Before the frame calibration (measurement run
        // `2026-08-04_2142`), the site-2 fallback seeded the extrapolator with an
        // ABSOLUTE Y-up world position. KSP's floating origin sits on the active
        // vessel, so any vessel within ~1 body radius of it read |r| ~ 0 and the
        // start altitude collapsed to ~-Radius — every such fallback tripped
        // `SubSurfaceStart` regardless of whether the vessel was destroyed. Seeding
        // the fallback in the extrapolator's own Zup body-relative frame removes
        // that accident; a genuinely collapsed orbit still trips `SubSurfaceStart`
        // honestly, and this reason carries the rest of the population so the
        // Destroyed verdict is not silently replaced by
        // `DetermineTerminalState(v.situation, v)`'s `SUB_ORBITAL`.
        NoSolverStart
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
        // OrbitSegment carries KSP-native DEGREE-valued inc/LAN/argPe (see the
        // contract note in OrbitSegment.cs); TwoBodyOrbit is radians-internal, so
        // every read/write across that boundary converts.
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;
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
            ExtrapolationLimits? limitsOverride = null,
            bool warnOnSubSurfaceStart = true)
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
                Action<string, string> log = warnOnSubSurfaceStart
                    ? (Action<string, string>)ParsekLog.Warn
                    : ParsekLog.Verbose;
                log(LogTag, string.Format(
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
                    // Recoverable terminal, not an error: callers handle
                    // failureReason=DegenerateStateVector (the finalizer reseeds from
                    // the recorded surface point). The common trigger is a
                    // near-stationary seed -- e.g. a booster the moment it separates on
                    // the pad -- where no two-body orbit exists from ~0 velocity. Logged
                    // at Warn to match the other "couldn't fully extrapolate" terminal
                    // reasons (horizon-cap, soi-transition-cap) rather than Error.
                    ParsekLog.Warn(LogTag, string.Format(
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

        // ------------------------------------------------------------------
        // THE ELEMENT FRAME BOUNDARY (site 5, generalised)
        // ------------------------------------------------------------------

        /// <summary>
        /// Rate-limit / log key for the two degenerate paths of
        /// <see cref="GetStockElementFrameZupAngleRadians"/>. Distinctive verbatim literal on
        /// purpose: it is the grep marker that proves a build carrying the element-frame
        /// boundary actually deployed (see docs/dev/todo-and-known-bugs.md, "TwoBodyOrbit's
        /// element-seeded propagation works in KSP's raw element frame", finding A).
        /// </summary>
        internal const string ElementFrameZupBoundaryLogKey = "twobody-element-frame-zup-boundary";

        /// <summary>
        /// Rate-limit / log key for the ONE non-total path the elliptic Kepler solve could
        /// otherwise take: stock's standard-eccentricity Newton loop is UNCAPPED, and this port
        /// caps it (see <see cref="TwoBodyOrbit.SolveEllipticKepler"/>). Distinctive verbatim
        /// literal on purpose: it is the grep marker that proves a build carrying the
        /// stock-parity Kepler solver actually deployed (see docs/dev/todo-and-known-bugs.md,
        /// "TwoBodyOrbit's element-seeded propagation works in KSP's raw element frame",
        /// finding B; branch <c>twobody-extreme-ecc-solver</c>).
        /// </summary>
        internal const string EllipticKeplerIterationCapLogKey = "twobody-elliptic-kepler-std-iteration-cap";

        /// <summary>
        /// THE FRAME CONTRACT for this propagator's SEGMENT I/O, and the single place the
        /// element-frame crossing happens. FIXED 2026-08-05 (branch <c>twobody-element-frame</c>),
        /// generalising the site-5 seam that previously sat in
        /// <c>IncompleteBallisticSceneExitFinalizer</c>; see docs/dev/todo-and-known-bugs.md,
        /// "<c>TwoBodyOrbit</c>'s element-seeded propagation works in KSP's raw element frame,
        /// not stock <c>Orbit</c>'s" (FINDING A).
        /// <para>
        /// WHAT THE ANGLE IS. Stock <c>Orbit</c> builds its state from elements through exactly
        /// the same 3-1-3 perifocal rotation <c>TwoBodyOrbit.RotateFromPerifocal</c> does, then
        /// passes EVERY state vector it returns through <c>Planetarium.Zup.WorldToLocal</c>.
        /// <c>Planetarium.Zup</c> is <c>PlanetaryFrame(0, 90, inverseRotAngle)</c>, whose
        /// declination argument collapses the 3-1-3 to a PURE ROTATION ABOUT THE POLAR (z) AXIS by
        /// <c>inverseRotAngle</c>: its X axis is <c>(cos a, sin a, 0)</c> and its Z axis is
        /// <c>(0, 0, 1)</c>. Stock's own <c>Planetarium.right</c> docstring states the same thing
        /// from the other side - "the LAN is the angle between <c>Planetarium.right</c> and the
        /// orbit's ascending node". So KSP's stored LAN is measured from <c>Planetarium.right</c>,
        /// NOT from the raw +x of the element frame. That angle MEASURED 230.01 deg in H9 run
        /// <c>2026-08-04_2224</c>.
        /// </para>
        /// <para>
        /// THE SIGN, derived (not assumed) from the closed form
        /// <c>stockState == Zup.WorldToLocal(rawState)</c> that
        /// <c>StockOrbitElementFrameParityTests</c> pins: <c>WorldToLocal</c> against an X axis of
        /// <c>(cos a, sin a, 0)</c> is <c>R_z(-a)</c>, and <c>R_z(-a)</c> applied to
        /// <c>R_z(LAN) R_x(inc) R_z(argPe)</c> is the same chain evaluated at <c>LAN - a</c>.
        /// Hence <see cref="TwoBodyOrbit.TryCreateFromSegment"/> SUBTRACTS the angle on the way IN
        /// and <c>CreateSegment</c> ADDS it back on the way OUT, and after that conversion every
        /// state this propagator produces is already in stock's frame - the element-seeded path
        /// agrees with the state-vector-seeded path (<see cref="TwoBodyOrbit.TryCreate"/>, fed
        /// stock-frame vectors off a live orbit) and with site 1's
        /// <c>ResolveBodyFixedSurfaceCoordinates</c> contract, in ONE place.
        /// </para>
        /// <para>
        /// THE GATE. Headlessly - and in any process that never ran <c>Planetarium.Awake</c> -
        /// <c>Planetarium.Zup</c> is a DEFAULT-constructed <c>CelestialFrame</c> with all three
        /// axes zero. Extracting an angle from that would be meaningless, so
        /// <see cref="TryResolveCelestialFramePolarAngle"/> declines anything that is not an
        /// orthonormal rotation ABOUT THE POLAR AXIS and the boundary becomes the identity, which
        /// is exactly the pass-through the previous seam's orthonormality gate produced.
        /// </para>
        /// <para>
        /// TIME DEPENDENCE - <c>Planetarium.Zup</c> MOVES, AND THIS ACCESSOR IS BUILT FOR THAT.
        /// <c>Planetarium.Awake</c> seeds <c>Zup</c>, but it does not own it:
        /// <c>CelestialBody.CBUpdate</c> - reached from <c>Planetarium.FixedUpdate</c> -&gt;
        /// <c>UpdateCBsRecursive</c> EVERY PHYSICS TICK - REBUILDS the static for a body in the
        /// inverse-rotation regime:
        /// <code>
        /// rotationAngle = (initialRotation + 360 * rotPeriodRecip * UT) % 360;
        /// Planetarium.InverseRotAngle = (rotationAngle - directRotAngle) % 360;
        /// CelestialFrame.PlanetaryFrame(0, 90, Planetarium.InverseRotAngle, ref Planetarium.Zup);
        /// </code>
        /// <c>rotationAngle</c> advances with UT, so the polar angle advances with it - about
        /// 1 deg per minute of game time at Kerbin's rotation period, and a vessel on or near
        /// Kerbin below the inverse-rotation threshold (the H9 pad fixture) sits in exactly that
        /// regime. THE 230.01 deg CITED ABOVE IS A SNAPSHOT AT THAT RUN'S UT, NOT AN INSTALL
        /// CONSTANT.
        /// </para>
        /// <para>
        /// WHY PER-CALL READS ARE CORRECT, AND WHAT THE INVARIANT ACTUALLY IS. Precisely BECAUSE
        /// the static moves, this accessor reads it fresh on every call and nothing caches the
        /// angle. The invariant the boundary relies on is ONE SYNCHRONOUS SINGLE-FRAME PASS = ONE
        /// <c>Zup</c>: <c>IncompleteBallisticSceneExitFinalizer.TryApply</c> is invoked as a plain
        /// synchronous call from <c>ParsekFlight.Finalization.cs</c> (:148 and :414), and seed -&gt;
        /// propagate -&gt; <c>CreateSegment</c> all run inside it on the main thread with no
        /// <c>yield</c> between them, so the angle subtracted on the way in and the angle added on
        /// the way out are the same number. Stock then applies its own then-current <c>Zup</c> at
        /// query / replay time, and the LAN written to disk is inertial and time-stable, so the
        /// two sides agree at any later UT.
        /// </para>
        /// <para>
        /// THEREFORE: never cache this angle across frames, and never move the finalization pass
        /// into a coroutine or split it across frames. Under time warp a multi-frame pass would
        /// seed against one <c>Zup</c> and write out against another, and the segment's LAN would
        /// come out wrong by the degrees the frame turned in between. No
        /// <see cref="TwoBodyOrbit"/> built from a segment is stored anywhere either - every one is
        /// a call-local - so there is no propagator instance to go stale across frames.
        /// </para>
        /// </summary>
        internal static double GetStockElementFrameZupAngleRadians()
        {
            Planetarium.CelestialFrame zup;
            try
            {
                zup = Planetarium.Zup;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(LogTag, ElementFrameZupBoundaryLogKey,
                    "element-frame boundary: Planetarium.Zup unavailable ("
                    + ex.GetType().Name
                    + "); treating the segment element frame as stock's frame (angle 0)");
                return 0.0;
            }

            if (!TryResolveCelestialFramePolarAngle(zup.X, zup.Y, zup.Z, out double angleRadians))
            {
                ParsekLog.VerboseRateLimited(LogTag, ElementFrameZupBoundaryLogKey,
                    "element-frame boundary: Planetarium.Zup is not an orthonormal polar rotation "
                    + "(uninitialised frame); treating the segment element frame as stock's frame (angle 0)");
                return 0.0;
            }

            return angleRadians;
        }

        /// <summary>
        /// Pure core of <see cref="GetStockElementFrameZupAngleRadians"/>: recovers the polar-axis
        /// rotation angle of a <c>Planetarium.CelestialFrame</c> basis, or declines.
        /// <para>
        /// Declines for a basis that is not a rotation - which is what a DEFAULT-constructed
        /// <c>Planetarium.CelestialFrame</c> is, all three axes zero - and equally for an
        /// orthonormal basis that is NOT a rotation about the polar axis, because the single angle
        /// this returns would then not describe it. <c>Planetarium.Zup</c> is always polar by
        /// construction (<c>PlanetaryFrame(0, 90, inverseRotAngle)</c>), so the second half of the
        /// gate can only fire on a frame nothing in KSP builds; declining is the honest answer
        /// rather than silently mis-rotating every extrapolated tail.
        /// </para>
        /// <para>
        /// <c>internal</c> and pure so the headless tests can drive both the accept and the
        /// decline path without a live <c>Planetarium</c>.
        /// </para>
        /// </summary>
        internal static bool TryResolveCelestialFramePolarAngle(
            Vector3d x, Vector3d y, Vector3d z, out double angleRadians)
        {
            angleRadians = 0.0;
            const double orthonormalTolerance = 1e-6;
            if (!IsUnitAxis(x, orthonormalTolerance)
                || !IsUnitAxis(y, orthonormalTolerance)
                || !IsUnitAxis(z, orthonormalTolerance)
                || Math.Abs(Vector3d.Dot(x, y)) > orthonormalTolerance
                || Math.Abs(Vector3d.Dot(x, z)) > orthonormalTolerance
                || Math.Abs(Vector3d.Dot(y, z)) > orthonormalTolerance)
            {
                return false;
            }

            // A polar rotation leaves the z axis alone and keeps both in-plane axes in the
            // xy-plane; anything else is not describable by a single angle about z.
            if (Math.Abs(x.z) > orthonormalTolerance
                || Math.Abs(y.z) > orthonormalTolerance
                || Math.Abs(z.x) > orthonormalTolerance
                || Math.Abs(z.y) > orthonormalTolerance
                || Math.Abs(z.z - 1.0) > orthonormalTolerance)
            {
                return false;
            }

            angleRadians = Math.Atan2(x.y, x.x);
            return !double.IsNaN(angleRadians) && !double.IsInfinity(angleRadians);
        }

        private static bool IsUnitAxis(Vector3d axis, double tolerance)
        {
            double magnitudeSquared = (axis.x * axis.x) + (axis.y * axis.y) + (axis.z * axis.z);
            return !double.IsNaN(magnitudeSquared)
                && !double.IsInfinity(magnitudeSquared)
                && Math.Abs(magnitudeSquared - 1.0) <= tolerance;
        }

        /// <summary>
        /// Propagates a segment's elements to a state vector IN STOCK <c>Orbit</c>'s FRAME - the
        /// same frame <c>orbit.getRelativePositionAtUT</c> / <c>getOrbitalVelocityAtUT</c> report,
        /// Zup-swizzled body-relative. The element-frame crossing is applied once, inside
        /// <see cref="TwoBodyOrbit.TryCreateFromSegment"/>; see
        /// <see cref="GetStockElementFrameZupAngleRadians"/> for the contract. Callers must NOT
        /// apply a further Zup rotation on top of this output.
        /// </summary>
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

        /// <summary>
        /// Detects a captured patched-conic tail that re-enters an atmospheric
        /// body. KSP's patched-conic chain for an off-scene / on-rails vessel
        /// carries no atmosphere or IMPACT transition, so a sub-orbital arc whose
        /// periapsis sits below the atmosphere top is snapshotted as a CLOSED
        /// ellipse that runs underground. This finds the first segment whose
        /// periapsis is below the atmosphere top and returns the UT of its
        /// descending crossing of the atmosphere boundary (or the segment start,
        /// when the segment already begins inside the atmosphere), so the caller
        /// can clip the predicted segments there and hand the atmospheric descent
        /// to the ballistic extrapolator (which terminates at the real terrain
        /// impact).
        /// Airless bodies are intentionally skipped: their patched-conic chain
        /// ends at an IMPACT patch and is handled by the solver-impact
        /// short-circuit, so they never leak a closed sub-surface ellipse here.
        /// Genuine coasts (periapsis above the atmosphere) return false.
        /// </summary>
        internal static bool TryFindAtmosphericReentryClip(
            IReadOnlyList<OrbitSegment> segments,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            out int clipSegmentIndex,
            out double atmosphereEntryUT)
        {
            clipSegmentIndex = -1;
            atmosphereEntryUT = double.NaN;
            if (segments == null || segments.Count == 0 || bodies == null)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment segment = segments[i];
                if (string.IsNullOrEmpty(segment.bodyName)
                    || !bodies.TryGetValue(segment.bodyName, out ExtrapolationBody body)
                    || body == null
                    || !body.HasAtmosphere)
                    continue;

                double boundaryRadius = body.Radius + body.AtmosphereDepth;
                double periapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity);
                if (double.IsNaN(periapsisRadius)
                    || double.IsInfinity(periapsisRadius)
                    || periapsisRadius >= boundaryRadius)
                    continue;

                if (!TwoBodyOrbit.TryCreateFromSegment(segment, body.GravitationalParameter, out TwoBodyOrbit orbit))
                    continue;

                // The segment may already begin at or below the atmosphere boundary
                // (KSP can capture a patch that starts on the descending arc rather
                // than at apoapsis). The whole segment is then a re-entry: clip at
                // its start so the ballistic extrapolator takes the descent from
                // there. Otherwise clip at the in-segment descending crossing.
                double startRadius = Magnitude(orbit.GetPositionAtUT(segment.startUT));
                if (startRadius <= boundaryRadius + OrbitEpsilon)
                {
                    clipSegmentIndex = i;
                    atmosphereEntryUT = segment.startUT;
                    return true;
                }

                if (TryFindDescendingRadiusCrossingUT(
                        orbit, segment.startUT, segment.endUT, boundaryRadius, out double crossingUT))
                {
                    clipSegmentIndex = i;
                    atmosphereEntryUT = crossingUT;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Writes the propagator's own elements back out as a KSP-native <see cref="OrbitSegment"/>.
        /// <para>
        /// FRAME - the OTHER end of the element-frame boundary opened by
        /// <see cref="TwoBodyOrbit.TryCreateFromSegment"/>. Playback feeds these elements to a
        /// stock <c>Orbit</c>, which applies <c>Planetarium.Zup</c> itself, so the Zup polar angle
        /// is ADDED back here - otherwise every extrapolator-authored segment would replay rotated
        /// about the polar axis. See <see cref="GetStockElementFrameZupAngleRadians"/>.
        /// </para>
        /// </summary>
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
                inclination = orbit.Inclination * RadToDeg,
                eccentricity = orbit.Eccentricity,
                semiMajorAxis = orbit.SemiMajorAxis,
                longitudeOfAscendingNode = NormalizeAngle(
                    orbit.LongitudeOfAscendingNode + GetStockElementFrameZupAngleRadians()) * RadToDeg,
                argumentOfPeriapsis = orbit.ArgumentOfPeriapsis * RadToDeg,
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

        /// <summary>
        /// Geodetic coordinates read straight off a Zup-swizzled body-relative position:
        /// z is the polar axis, longitude is <c>atan2(y, x)</c>. This is the extrapolator's
        /// OWN frame convention, and after the 2026-08-04_2142 frame calibration it agrees
        /// with the finalizer's <c>ResolveBodyFixedSurfaceCoordinates</c>, whose
        /// <c>.xzy</c> unswizzle moves exactly this z into KSP's world polar slot.
        /// <c>internal</c> for the headless agreement test.
        /// <para>
        /// Converts with the file's own <c>RadToDeg</c> DOUBLE constant, not
        /// <c>Mathf.Rad2Deg</c>: that is a float (~7 significant digits), which made this
        /// copy and the finalizer's otherwise-identical copy disagree by ~1e-5 deg — about a
        /// metre of ground track — for no reason anyone had chosen.
        /// </para>
        /// </summary>
        internal static void GetApproximateLatitudeLongitude(
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            double radius = Math.Max(StateVectorEpsilon, Magnitude(position));
            latitude = Math.Asin(Clamp(position.z / radius, -1.0, 1.0)) * RadToDeg;
            longitude = Math.Atan2(position.y, position.x) * RadToDeg;
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

        // Radians-internal Kepler propagator. All angular fields here are RADIANS;
        // OrbitSegment stores KSP-native degrees for inc/LAN/argPe, so
        // TryCreateFromSegment / CreateSegment convert at the boundary. Element
        // conventions match KSP's Orbit.UpdateFromStateVectors (z = polar axis,
        // node from +x), so state vectors live in the same frame as
        // Orbit.getRelativePositionAtUT / getOrbitalVelocityAtUT (Zup-swizzled,
        // body-relative). THAT HOLDS FOR THE ELEMENT-SEEDED PATH TOO: a segment's
        // LAN is KSP-native (measured from Planetarium.right, not from the raw +x
        // of the element frame), so TryCreateFromSegment / CreateSegment shift it
        // by the Planetarium.Zup polar angle at the same boundary they convert
        // degrees to radians - see GetStockElementFrameZupAngleRadians for the
        // contract, the derivation and the headless gate. Within THIS propagator
        // and everything it feeds there is exactly ONE element-frame crossing and
        // it is that boundary; nothing downstream may apply a second Zup rotation.
        // Scoped deliberately: GhostExtender.PropagateOrbital reaches KSP-native
        // elements by another route entirely and is still a raw-frame (frame-naive
        // longitude) reader - pre-existing, recorded in todo-and-known-bugs.md
        // under Finding A, and NOT covered here.
        // Known measure-zero divergence: for an EXACTLY equatorial
        // retrograde eccentric orbit the degenerate-node argPe branch below measures
        // CCW from +x where KSP flips on direction of motion, yielding 360-argPe.
        // Internal (not private) purely so the Kepler core is reachable from
        // Parsek.Tests through InternalsVisibleTo("Parsek.Tests"); see
        // BallisticExtrapolatorKeplerTests.
        internal struct TwoBodyOrbit
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

            /// <summary>
            /// Builds the radians-internal orbit from a segment's KSP-native elements,
            /// or declines when the element set is one this propagator cannot express.
            /// See <see cref="AreSegmentElementsPropagatable"/> for what "cannot express"
            /// means and why declining (rather than propagating a degenerate conic) is
            /// the contract.
            /// <para>
            /// FRAME - one of the TWO ends of the element-frame boundary (the other is
            /// <c>CreateSegment</c>). A segment's <c>longitudeOfAscendingNode</c> is KSP-native,
            /// i.e. measured from <c>Planetarium.right</c>, while this propagator works in stock
            /// <c>Orbit</c>'s STATE frame, so the Zup polar angle is SUBTRACTED here and added
            /// back on the way out. The derivation, the sign, and the headless /
            /// uninitialised-Planetarium gate all live on
            /// <see cref="BallisticExtrapolator.GetStockElementFrameZupAngleRadians"/> - read that
            /// before touching this line. Applies to open orbits exactly as to closed ones: the
            /// shift is a rotation of the conic, not a property of its class.
            /// </para>
            /// </summary>
            public static bool TryCreateFromSegment(
                OrbitSegment segment,
                double gravParameter,
                out TwoBodyOrbit orbit)
            {
                orbit = default(TwoBodyOrbit);
                if (!AreSegmentElementsPropagatable(segment, gravParameter))
                    return false;

                double zupAngle = GetStockElementFrameZupAngleRadians();
                bool isElliptic = segment.eccentricity < 1.0;
                orbit = new TwoBodyOrbit
                {
                    BodyRadius = 0.0,
                    GravitationalParameter = gravParameter,
                    Inclination = segment.inclination * DegToRad,
                    Eccentricity = segment.eccentricity,
                    SemiMajorAxis = segment.semiMajorAxis,
                    LongitudeOfAscendingNode = NormalizeAngle(
                        (segment.longitudeOfAscendingNode * DegToRad) - zupAngle),
                    ArgumentOfPeriapsis = segment.argumentOfPeriapsis * DegToRad,
                    MeanAnomalyAtEpoch = segment.meanAnomalyAtEpoch,
                    Epoch = segment.epoch,
                    PeriapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity),
                    ApoapsisRadius = isElliptic
                        ? segment.semiMajorAxis * (1.0 + segment.eccentricity)
                        : double.PositiveInfinity,
                    Period = isElliptic
                        ? (Math.PI * 2.0) * Math.Sqrt(
                            (segment.semiMajorAxis * segment.semiMajorAxis * segment.semiMajorAxis) / gravParameter)
                        : double.PositiveInfinity,
                    IsElliptic = isElliptic
                };
                return true;
            }

            /// <summary>
            /// The element preconditions <see cref="GetStateAtUT"/> depends on but cannot
            /// check for itself. Every one of them is a set the Kepler propagation has no
            /// finite answer for, so the honest response is to decline the segment rather
            /// than hand a caller a NaN state (or, worse, throw out of the propagator):
            /// <list type="number">
            /// <item>NON-FINITE ELEMENTS. Stock KSP can hand Parsek a patched-conic patch
            /// whose elements are Not-a-Number - measured on an ORBITAL EVA KERBAL, where
            /// <c>PatchedConicSolver.Update</c> itself logs
            /// "dT is NaN! tA: NaN, E: NaN, M: NaN, T: NaN" before
            /// <c>PatchedConicSnapshot</c> copies the patch. A NaN eccentricity is the
            /// nastiest of these because every comparison against it is false, so the
            /// <c>&lt; 1.0</c> elliptic test classifies it HYPERBOLIC and routes it into
            /// <see cref="SolveHyperbolicKepler"/>. Only <c>semiMajorAxis</c> used to be
            /// checked here, which let the other six through.</item>
            /// <item>NEGATIVE ECCENTRICITY: not a conic.</item>
            /// <item>PARABOLIC (e == 1): the semi-major axis is undefined and the
            /// semi-latus rectum a(1 - e^2) collapses to zero, so the velocity scale
            /// sqrt(mu / p) is infinite. <see cref="TryCreate"/> rejects the same case
            /// from the state-vector side, so both constructors now agree - and the
            /// check that makes them agree is its ECCENTRICITY-BAND else-return (the
            /// same <c>OrbitEpsilon</c> half-width used here), NOT its zero-specific-
            /// energy test, which is a 1e-8 ABSOLUTE threshold and at KSP scales
            /// corresponds to a band roughly six orders of magnitude tighter. Cited
            /// precisely because a future reader loosening the energy test would
            /// otherwise believe they had relaxed the parabolic rule symmetrically.</item>
            /// <item>A SEMI-MAJOR AXIS WHOSE SIGN DISAGREES WITH THE CONIC CLASS. Closed
            /// orbits have a &gt; 0 and open ones a &lt; 0; mixing them makes the
            /// hyperbolic mean motion sqrt(mu / (-a)^3) take the root of a negative and
            /// hands NaN to the solver even though every element was finite.</item>
            /// </list>
            /// Note this is deliberately NOT a clamp: the degenerate patch carries no
            /// recoverable trajectory, so the tail is declined and the caller falls back
            /// (or simply keeps its previous answer), exactly as it already does for an
            /// unresolvable tail.
            /// </summary>
            internal static bool AreSegmentElementsPropagatable(OrbitSegment segment, double gravParameter)
            {
                if (!IsFiniteElement(gravParameter) || gravParameter <= 0.0)
                    return false;

                if (!IsFiniteElement(segment.semiMajorAxis)
                    || !IsFiniteElement(segment.eccentricity)
                    || !IsFiniteElement(segment.meanAnomalyAtEpoch)
                    || !IsFiniteElement(segment.epoch)
                    || !IsFiniteElement(segment.inclination)
                    || !IsFiniteElement(segment.longitudeOfAscendingNode)
                    || !IsFiniteElement(segment.argumentOfPeriapsis))
                {
                    return false;
                }

                if (segment.eccentricity < 0.0 || Math.Abs(segment.eccentricity - 1.0) <= OrbitEpsilon)
                    return false;

                return segment.eccentricity < 1.0
                    ? segment.semiMajorAxis > 0.0
                    : segment.semiMajorAxis < 0.0;
            }

            private static bool IsFiniteElement(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
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

            /// <summary>
            /// The eccentricity at or above which stock <c>Orbit</c> - and therefore this
            /// propagator - switches from Newton to the Laguerre-style extreme-eccentricity
            /// solve. Stock's own literal, from <c>Orbit.solveEccentricAnomaly</c>.
            /// </summary>
            internal const double ExtremeEccentricityThreshold = 0.8;

            /// <summary>
            /// Stock <c>Orbit.solveEccentricAnomalyStd</c>'s convergence threshold, on the Newton
            /// STEP (not the residual). Quadratic convergence means a step this small leaves a
            /// residual near machine precision, which is why a "loose"-looking 1e-7 is not a loss
            /// of accuracy against the 1e-12 step threshold it replaces.
            /// </summary>
            internal const double StandardKeplerStepTolerance = 1e-7;

            /// <summary>
            /// Iteration cap on the standard branch. Stock's loop is UNCAPPED; see
            /// <see cref="SolveEllipticKepler"/> for why this port needs one and what it does
            /// when the cap is reached.
            /// </summary>
            internal const int StandardKeplerIterationCap = 64;

            /// <summary>
            /// Stock <c>Orbit.solveEccentricAnomalyExtremeEcc</c>'s FIXED iteration count - not a
            /// cap and not a convergence budget: the loop runs exactly this many times with no
            /// early exit, exactly as stock does, so the two solvers agree bit-for-bit in
            /// arithmetic order as well as in result.
            /// </summary>
            internal const int ExtremeEccentricityKeplerIterations = 8;

            /// <summary>
            /// SOLVER-PARITY CONTRACT with stock <c>Orbit</c> (decompiled KSP 1.12.5,
            /// <c>Orbit.solveEccentricAnomaly</c> / <c>solveEccentricAnomalyStd</c> /
            /// <c>solveEccentricAnomalyExtremeEcc</c>). This method IS stock's dispatch,
            /// transcribed: below <see cref="ExtremeEccentricityThreshold"/> (0.8) the standard
            /// Newton solve seeded at <c>M + e sin M + 0.5 e^2 sin 2M</c> and iterated until the
            /// Newton STEP falls under <see cref="StandardKeplerStepTolerance"/>; at or above it
            /// the Laguerre-style solve seeded at <c>M + 0.85 e sign(sin M)</c> and run for
            /// exactly <see cref="ExtremeEccentricityKeplerIterations"/> (8) iterations with no
            /// early exit. The same transcription (from the same decompile) is cross-checked cell
            /// by cell in <c>Parsek.Tests.StockOrbitElementFrameParityTests.StockOrbitPort</c>.
            /// <para>
            /// WHY. Until 2026-08-05 this was plain Newton seeded at <c>E = M</c>, capped at 16
            /// iterations, for every eccentricity. At e = 0.9948 - which is not exotic: it is the
            /// surface-rotation ellipse of EVERY landed or prelaunch vessel, and the fixture the
            /// H9 probes fly on - that solve fails to converge near periapsis inside its 16
            /// iterations and lands DOUBLE-DIGIT degrees of true anomaly from the root, peaking
            /// around 134 deg over a mean-anomaly sweep, while stock stays exact throughout. The
            /// element-seeded propagator is supposed to be stock's, so both branches are ported
            /// rather than only the one that was wrong - a propagator that agrees with stock in
            /// one eccentricity regime and improvises in the other is not a parity contract.
            /// See docs/dev/todo-and-known-bugs.md, "TwoBodyOrbit's element-seeded propagation
            /// works in KSP's raw element frame, not stock Orbit's" (Finding B), branch
            /// <c>twobody-extreme-ecc-solver</c>.
            /// </para>
            /// <para>
            /// TOTALITY, and the ONE deliberate deviation. This propagator must never throw and
            /// must never hang - <c>FlightRecorder.OnPhysicsFrame</c> refreshes the finalization
            /// cache through it BEFORE it samples, so one bad conic costs every sample of every
            /// frame (see <see cref="SolveHyperbolicKepler"/> for the measured incident). Stock's
            /// standard branch is a <c>while</c> with no iteration limit; a cap
            /// (<see cref="StandardKeplerIterationCap"/>) is therefore added, and on reaching it
            /// the solve RETURNS ITS BEST ESTIMATE and emits a rate-limited log keyed
            /// <see cref="BallisticExtrapolator.EllipticKeplerIterationCapLogKey"/> rather than
            /// throwing or looping. That cap is unreachable through the production entry points
            /// (Newton on <c>e &lt; 0.8</c> from stock's seed converges in a handful of
            /// iterations), which is exactly why it must not be silent if it is ever hit.
            /// </para>
            /// <para>
            /// NaN-IN, NaN-OUT is preserved and is load-bearing for the same reason it is on the
            /// hyperbolic solve: BOTH ported branches call <c>Math.Sign</c>, the one
            /// <c>System.Math</c> entry point that THROWS <c>ArithmeticException</c> on NaN
            /// instead of propagating it. The entry guard below returns NaN for a non-finite mean
            /// anomaly or eccentricity, and <see cref="SolverSign"/> replaces <c>Math.Sign</c>
            /// inside the loops so a value that goes non-finite mid-iteration falls out as NaN
            /// too. Callers keep the contract they already had: the three
            /// <see cref="TryPropagate"/> consumers check finiteness, and the in-extrapolator
            /// sites are safe only because they reach this solver through
            /// <see cref="AreSegmentElementsPropagatable"/>- or <see cref="TryCreate"/>-validated
            /// elements.
            /// </para>
            /// <para>
            /// MEAN-ANOMALY RANGE. <see cref="GetStateAtUT"/> normalises M to <c>[0, 2pi)</c>
            /// while stock's <c>getObtAtUT</c> hands its solver M in <c>(-pi, pi]</c>. Both
            /// branches are equivariant under a 2pi shift of M (the seeds use only <c>sin</c> of
            /// M, and the residual <c>E - e sin E - M</c> is unchanged when E and M shift
            /// together), and E and E - 2pi produce identical radius and true anomaly, so the two
            /// ranges agree. Do not "fix" one range to match the other on the assumption that
            /// they differ.
            /// </para>
            /// <para>
            /// This method and its two branches are <c>internal</c> rather than <c>private</c>
            /// purely so <c>Parsek.Tests</c> can drive them directly: no production input reaches
            /// the standard branch's iteration cap, and a cap nobody can exercise is a claim
            /// rather than a contract.
            /// </para>
            /// </summary>
            internal static double SolveEllipticKepler(double meanAnomaly, double eccentricity)
            {
                if (!IsFiniteElement(meanAnomaly) || !IsFiniteElement(eccentricity))
                    return double.NaN;

                return eccentricity < ExtremeEccentricityThreshold
                    ? SolveEllipticKeplerStandard(meanAnomaly, eccentricity)
                    : SolveEllipticKeplerExtremeEccentricity(meanAnomaly, eccentricity);
            }

            /// <summary>
            /// <c>Orbit.solveEccentricAnomalyStd</c>: Newton on <c>M = E - e sin E</c> seeded at
            /// the second-order series expansion of E in e. Iteration-capped where stock is not -
            /// see <see cref="SolveEllipticKepler"/> for the totality contract.
            /// </summary>
            internal static double SolveEllipticKeplerStandard(double meanAnomaly, double eccentricity)
            {
                double delta = 1.0;
                double eccentricAnomaly = meanAnomaly
                    + (eccentricity * Math.Sin(meanAnomaly))
                    + (0.5 * eccentricity * eccentricity * Math.Sin(2.0 * meanAnomaly));

                int iterations = 0;
                while (Math.Abs(delta) > StandardKeplerStepTolerance)
                {
                    if (iterations >= StandardKeplerIterationCap)
                    {
                        ParsekLog.WarnRateLimited(LogTag, EllipticKeplerIterationCapLogKey,
                            "elliptic Kepler standard solve hit its iteration cap ("
                            + StandardKeplerIterationCap
                            + "); returning best estimate E="
                            + eccentricAnomaly.ToString("R", CultureInfo.InvariantCulture)
                            + " for M="
                            + meanAnomaly.ToString("R", CultureInfo.InvariantCulture)
                            + " e="
                            + eccentricity.ToString("R", CultureInfo.InvariantCulture)
                            + " lastStep="
                            + delta.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    }

                    iterations++;
                    double solved = eccentricAnomaly - (eccentricity * Math.Sin(eccentricAnomaly));
                    delta = (meanAnomaly - solved) / (1.0 - (eccentricity * Math.Cos(eccentricAnomaly)));
                    eccentricAnomaly += delta;
                }

                return eccentricAnomaly;
            }

            /// <summary>
            /// <c>Orbit.solveEccentricAnomalyExtremeEcc</c>: a Laguerre-style solve (order 5) run
            /// for a FIXED 8 iterations with no convergence test, seeded on the correct side of
            /// periapsis by <c>0.85 e sign(sin M)</c>. Structurally total for the eccentricity
            /// band it serves - <c>1 - e cos E</c> is bounded below by <c>1 - e &gt; 0</c> and the
            /// square root is taken of an absolute value, so the denominator can neither vanish
            /// nor go imaginary - with <see cref="SolverSign"/> covering the non-finite case the
            /// entry guard in <see cref="SolveEllipticKepler"/> does not already refuse.
            /// </summary>
            internal static double SolveEllipticKeplerExtremeEccentricity(
                double meanAnomaly, double eccentricity)
            {
                double eccentricAnomaly = meanAnomaly
                    + (0.85 * eccentricity * SolverSign(Math.Sin(meanAnomaly)));

                for (int i = 0; i < ExtremeEccentricityKeplerIterations; i++)
                {
                    double sine = eccentricity * Math.Sin(eccentricAnomaly);
                    double cosine = eccentricity * Math.Cos(eccentricAnomaly);
                    double residual = eccentricAnomaly - sine - meanAnomaly;
                    double firstDerivative = 1.0 - cosine;
                    double secondDerivative = sine;
                    eccentricAnomaly += (-5.0 * residual)
                        / (firstDerivative
                            + (SolverSign(firstDerivative)
                                * Math.Sqrt(Math.Abs(
                                    (16.0 * firstDerivative * firstDerivative)
                                    - (20.0 * residual * secondDerivative)))));
                }

                return eccentricAnomaly;
            }

            /// <summary>
            /// NaN-safe stand-in for <c>Math.Sign</c>, which throws <c>ArithmeticException</c> on
            /// NaN rather than propagating it. Returns 0 for both zero and NaN, so a non-finite
            /// iterate poisons the arithmetic into a NaN RESULT instead of an exception out of
            /// the propagator - the NaN-in / NaN-out half of the totality contract on
            /// <see cref="SolveEllipticKepler"/>. Stock's own solvers use <c>Math.Sign</c>
            /// directly; that is the deviation, and it changes nothing on any finite input
            /// because the two agree everywhere else.
            /// </summary>
            private static double SolverSign(double value)
            {
                if (value > 0.0)
                    return 1.0;
                if (value < 0.0)
                    return -1.0;
                return 0.0;
            }

            /// <summary>
            /// Newton solve of the hyperbolic Kepler equation M = e sinh H - H.
            /// <para>
            /// TOTAL BY CONTRACT: returns NaN for a mean anomaly the equation has no
            /// finite solution for, and never throws. That guard is load-bearing, not
            /// decorative. <c>Math.Sign</c> is the one <c>System.Math</c> entry point
            /// that THROWS <c>ArithmeticException</c> on NaN instead of propagating it,
            /// and the initial-guess line below is the only place this propagator calls
            /// it - so a NaN mean anomaly used to escape as an exception all the way out
            /// through <c>GetStateAtUT</c>, <c>TryPropagate</c> and the finalization-cache
            /// refresh into <c>FlightRecorder.OnPhysicsFrame</c>, which calls that refresh
            /// BEFORE it samples. One degenerate stock patched conic therefore cost the
            /// recorder every sample of every frame: a ten-second orbital EVA finalized
            /// with a single point and 501 ArithmeticExceptions in the log.
            /// </para>
            /// <para>
            /// A NaN return is handled by the three <see cref="TryPropagate"/> consumers
            /// in <c>IncompleteBallisticSceneExitFinalizer</c>: each validates the
            /// resulting state through an IsFinite check and treats a non-finite result
            /// as a declined propagation. Do NOT read that as "every caller checks" -
            /// the in-extrapolator <c>GetStateAtUT</c> sites (the horizon state written
            /// into <c>terminalPosition</c>/<c>terminalVelocity</c>, <c>SampleOrbitWindow</c>,
            /// <c>GetAltitudeAtUT</c>, <c>GetSurfaceDeltaAtUT</c>) consume the state
            /// unchecked, and a NaN there reads false out of every comparison rather
            /// than announcing itself. Those sites are safe today only because they
            /// reach this solver through <see cref="AreSegmentElementsPropagatable"/>-
            /// or <see cref="TryCreate"/>-validated elements at finite UTs; a new caller
            /// that skips that validation would need its own check. Before this guard
            /// they threw instead, which was louder but cost the whole frame.
            /// A non-finite ECCENTRICITY needs no guard here - it
            /// poisons the iteration arithmetically and falls out as NaN without ever
            /// reaching <c>Math.Sign</c> - and is refused up front by
            /// <see cref="AreSegmentElementsPropagatable"/> anyway.
            /// </para>
            /// </summary>
            private static double SolveHyperbolicKepler(double meanAnomaly, double eccentricity)
            {
                if (!IsFiniteElement(meanAnomaly))
                    return double.NaN;

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
