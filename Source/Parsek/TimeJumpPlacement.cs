using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Why a Real Spawn Control time jump that moves the player's group (design
    /// docs/dev/design-realspawn-time-jump-move.md, Section 4.3) is refused. Declaration
    /// order is the gate order of <see cref="TimeJumpPlacement.EvaluateGates"/>.
    /// </summary>
    internal enum MoveRefusal
    {
        None = 0,
        /// <summary>The player's vessel, a group member, or the reference at T0 or T1 is
        /// inside an atmosphere.</summary>
        Atmosphere,
        /// <summary>The reference's body differs between T0 and T1, or from the player's.</summary>
        BodyChange,
        /// <summary>The reference's own burns between T0 and T1 exceed the 2 m/s limit.</summary>
        Burns,
        /// <summary>The reference's local frame cannot be built (no orbit to follow).</summary>
        Degenerate,
        /// <summary>A group member's placed spot overlaps a vessel outside the group.</summary>
        DestinationTaken,
    }

    /// <summary>
    /// The reference ghost's local frame at one instant: radial, along-track, orbit
    /// normal, and the frame's rotation rate. Every vector is in the stock Zup
    /// body-relative frame (<c>Orbit.getRelativePositionAtUT</c>), never swizzled.
    /// </summary>
    internal struct LocalFrame
    {
        public Vector3d ur;
        public Vector3d ut;
        public Vector3d un;
        public Vector3d omega;
    }

    /// <summary>
    /// A vessel's state relative to the reference, as components along
    /// (radial, along-track, normal). <see cref="Offset"/> is metres;
    /// <see cref="Velocity"/> is the rotating-frame relative velocity in m/s. Frame-free:
    /// these are the only kinematic quantities that cross the clock-set frame boundary.
    /// </summary>
    internal struct RelativeState
    {
        public Vector3d Offset;
        public Vector3d Velocity;
    }

    /// <summary>The first gate that failed, with a short detail for the log.</summary>
    internal struct MoveGateVerdict
    {
        public MoveRefusal Refusal;
        public string Detail;

        public bool Passed => Refusal == MoveRefusal.None;

        public static MoveGateVerdict Pass => new MoveGateVerdict { Refusal = MoveRefusal.None, Detail = string.Empty };
    }

    /// <summary>Everything <see cref="TimeJumpPlacement.EvaluateGates"/> decides on.</summary>
    internal struct MoveGateInputs
    {
        public bool ActiveInsideAtmosphere;
        public bool MemberInsideAtmosphere;
        public string MemberInsideAtmosphereName;
        public bool ReferenceInsideAtmosphereT0;
        public bool ReferenceInsideAtmosphereT1;
        public string ActiveBody;
        public string ReferenceBodyT0;
        public string ReferenceBodyT1;
        public double BurnDeltaV;
        public bool FrameDegenerate;
        public bool DestinationTaken;
        public string DestinationBlocker;
        public double DestinationGap;
    }

    /// <summary>A body at its placed (or predicted) spot, for the destination gate.</summary>
    internal struct PlacedBody
    {
        public Vector3d Position;
        public double Radius;
        public string Name;
    }

    /// <summary>Outcome of <see cref="TimeJumpPlacement.ComputeSpawnRoomSlide"/>.</summary>
    internal struct SlideResult
    {
        /// <summary>False when no shift up to the cap clears every overlap.</summary>
        public bool Cleared;
        /// <summary>Signed along-track metres to add to every member's offset (0 when no
        /// overlap). Negative is behind the reference.</summary>
        public double ShiftMeters;
        public int StepsTried;
    }

    /// <summary>
    /// Pure core of the time jump that keeps the player's group beside the chosen ghost
    /// (design Section 4.1): the reference ghost's local frame, the relative-state map from
    /// T0 to T1, the group predicate, the reference's burn estimate, the gates and the spawn
    /// room slide. Positions and velocities are Zup body-relative and are never swizzled
    /// here; attitude crosses the frame boundary only as an orbital-frame-relative rotation
    /// built from WORLD vectors (<see cref="EncodeAttitude"/> / <see cref="DecodeAttitude"/>).
    /// </summary>
    internal static class TimeJumpPlacement
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Group distance gate: the Real Spawn Control spawn radius (ruling Q2).</summary>
        internal const double GroupRadiusMeters = ParsekFlight.NearbySpawnRadius;

        /// <summary>Group relative-speed gate, from orbit states (ruling Q2).</summary>
        internal const double GroupMaxRelativeSpeed = ParsekFlight.MaxRelativeSpeed;

        /// <summary>The reference's own burns between T0 and T1 the move may inherit (ruling Q6).</summary>
        internal const double MaxReferenceBurnDeltaV = 2.0;

        internal const double SlideStepMeters = 1.0;
        internal const double SlideCapMeters = 250.0;
        internal const double SlideMarginMeters = 5.0;

        /// <summary>A frame is degenerate when the velocity is nearly radial (sine of the
        /// r-v angle below this) or nearly zero.</summary>
        internal const double DegenerateSine = 1e-6;
        internal const double DegenerateSpeed = 1e-3;

        /// <summary>
        /// Only an orbital player vessel moves (design rule 5): a landed, splashed or
        /// prelaunch vessel keeps today's jump, which the body carries.
        /// </summary>
        internal static bool IsOrbitalPlayerVessel(Vessel.Situations situation)
        {
            return situation != Vessel.Situations.LANDED
                && situation != Vessel.Situations.SPLASHED
                && situation != Vessel.Situations.PRELAUNCH;
        }

        /// <summary>
        /// Builds the local frame at state (r, v): ur = r/|r|, un = (r x v)/|r x v|,
        /// ut = un x ur, omega = (r x v)/|r|^2. False for a degenerate state (nearly
        /// radial or nearly zero velocity, or non-finite input).
        /// </summary>
        internal static bool TryBuildFrame(Vector3d r, Vector3d v, out LocalFrame frame)
        {
            frame = default(LocalFrame);
            double rMag = r.magnitude;
            double vMag = v.magnitude;
            if (!IsFinite(rMag) || !IsFinite(vMag) || rMag < 1e-3 || vMag < DegenerateSpeed)
                return false;
            Vector3d h = Vector3d.Cross(r, v);
            double hMag = h.magnitude;
            if (!IsFinite(hMag) || hMag < DegenerateSine * rMag * vMag)
                return false;
            frame.ur = r / rMag;
            frame.un = h / hMag;
            frame.ut = Vector3d.Cross(frame.un, frame.ur);
            frame.omega = h / (rMag * rMag);
            return true;
        }

        /// <summary>
        /// Capture at T0: d = E0^T (r - rRef0), e = E0^T ((v - vRef0) - omega0 x (r - rRef0)).
        /// </summary>
        internal static RelativeState Capture(
            LocalFrame f0, Vector3d rRef0, Vector3d vRef0, Vector3d r, Vector3d v)
        {
            Vector3d dr = r - rRef0;
            Vector3d dvRot = (v - vRef0) - Vector3d.Cross(f0.omega, dr);
            return new RelativeState
            {
                Offset = new Vector3d(
                    Vector3d.Dot(dr, f0.ur), Vector3d.Dot(dr, f0.ut), Vector3d.Dot(dr, f0.un)),
                Velocity = new Vector3d(
                    Vector3d.Dot(dvRot, f0.ur), Vector3d.Dot(dvRot, f0.ut), Vector3d.Dot(dvRot, f0.un)),
            };
        }

        /// <summary>
        /// Place at T1: r = rRef1 + E1 d, v = vRef1 + E1 e + omega1 x (E1 d).
        /// </summary>
        internal static void Place(
            LocalFrame f1, Vector3d rRef1, Vector3d vRef1, RelativeState s,
            out Vector3d r, out Vector3d v)
        {
            Vector3d dr = f1.ur * s.Offset.x + f1.ut * s.Offset.y + f1.un * s.Offset.z;
            Vector3d dv = f1.ur * s.Velocity.x + f1.ut * s.Velocity.y + f1.un * s.Velocity.z;
            r = rRef1 + dr;
            v = vRef1 + dv + Vector3d.Cross(f1.omega, dr);
        }

        /// <summary>
        /// A loaded vessel moves with the player when it is close and on nearly the same
        /// orbit (ruling Q2): within <see cref="GroupRadiusMeters"/> and
        /// <see cref="GroupMaxRelativeSpeed"/> of the player's vessel, measured from orbit
        /// states, around the same body. Surface vessels and ghost map vessels never join.
        /// </summary>
        internal static bool IsGroupMember(
            double distance, double relativeSpeed, bool surfaceSituation,
            bool ghostMapVessel, bool sameBody)
        {
            if (ghostMapVessel || surfaceSituation || !sameBody)
                return false;
            return distance <= GroupRadiusMeters && relativeSpeed <= GroupMaxRelativeSpeed;
        }

        /// <summary>
        /// The reference's own manoeuvring between two of its states (ruling Q6), measured
        /// on the ORBIT, not on a state difference (along-track drift would grow a 0.8 m/s
        /// trim into about 12 m/s over 20 minutes). Small-change Gauss estimate:
        /// <c>max(v |da| / (2a), v |de| / 2) + v * dplane</c>, with de the eccentricity
        /// vector difference and dplane the angle between the orbit normals. A single
        /// prograde impulse on a circular orbit returns the impulse. Both states must be in
        /// the same frame (resolved at the same body rotation). Non-finite input returns
        /// +infinity, which refuses.
        /// </summary>
        internal static double EstimateBurnDeltaV(
            Vector3d r0, Vector3d v0, Vector3d r1, Vector3d v1, double gravParameter)
        {
            if (!(gravParameter > 0.0))
                return double.PositiveInfinity;
            if (!TryElements(r0, v0, gravParameter, out double a0, out Vector3d e0, out Vector3d h0)
                || !TryElements(r1, v1, gravParameter, out double a1, out Vector3d e1, out Vector3d h1))
                return double.PositiveInfinity;

            double aRef = Math.Abs(a0);
            double speed = Math.Sqrt(gravParameter / aRef);
            double sizeTerm = speed * Math.Abs(Math.Abs(a1) - aRef) / (2.0 * aRef);
            double shapeTerm = speed * (e1 - e0).magnitude / 2.0;
            double plane = Math.Atan2(Vector3d.Cross(h0, h1).magnitude, Vector3d.Dot(h0, h1));
            double result = Math.Max(sizeTerm, shapeTerm) + speed * Math.Abs(plane);
            return IsFinite(result) ? result : double.PositiveInfinity;
        }

        private static bool TryElements(
            Vector3d r, Vector3d v, double mu, out double a, out Vector3d e, out Vector3d h)
        {
            a = double.NaN;
            e = Vector3d.zero;
            h = Vector3d.zero;
            double rMag = r.magnitude;
            double v2 = v.sqrMagnitude;
            if (!IsFinite(rMag) || !IsFinite(v2) || rMag <= 0.0)
                return false;
            double energyTerm = 2.0 / rMag - v2 / mu;
            if (Math.Abs(energyTerm) < 1e-18)
                return false;
            a = 1.0 / energyTerm;
            e = (r * (v2 - mu / rMag) - v * Vector3d.Dot(r, v)) / mu;
            h = Vector3d.Cross(r, v);
            return IsFinite(a) && h.magnitude > 0.0;
        }

        /// <summary>True when a vessel at <paramref name="altitude"/> is inside its body's
        /// atmosphere. A non-finite altitude counts as inside (refuses).</summary>
        internal static bool IsInsideAtmosphere(bool bodyHasAtmosphere, double altitude, double atmosphereDepth)
        {
            if (!IsFinite(altitude))
                return true;
            return bodyHasAtmosphere && altitude < atmosphereDepth;
        }

        /// <summary>
        /// The gates of design Section 4.3, in order: Atmosphere, BodyChange, Burns,
        /// Degenerate, DestinationTaken. Returns the first failure with a short detail.
        /// Called only for an orbital player vessel.
        /// </summary>
        internal static MoveGateVerdict EvaluateGates(MoveGateInputs i)
        {
            if (i.ActiveInsideAtmosphere)
                return Refuse(MoveRefusal.Atmosphere, "who=active");
            if (i.MemberInsideAtmosphere)
                return Refuse(MoveRefusal.Atmosphere, "who=member name=" + (i.MemberInsideAtmosphereName ?? "?"));
            if (i.ReferenceInsideAtmosphereT0)
                return Refuse(MoveRefusal.Atmosphere, "who=reference-t0");
            if (i.ReferenceInsideAtmosphereT1)
                return Refuse(MoveRefusal.Atmosphere, "who=reference-t1");

            if (!string.Equals(i.ReferenceBodyT0, i.ReferenceBodyT1, StringComparison.Ordinal)
                || !string.Equals(i.ReferenceBodyT0, i.ActiveBody, StringComparison.Ordinal))
                return Refuse(MoveRefusal.BodyChange,
                    "active=" + (i.ActiveBody ?? "?") + " refT0=" + (i.ReferenceBodyT0 ?? "?")
                    + " refT1=" + (i.ReferenceBodyT1 ?? "?"));

            if (!(i.BurnDeltaV <= MaxReferenceBurnDeltaV))
                return Refuse(MoveRefusal.Burns,
                    "dv=" + i.BurnDeltaV.ToString("F2", IC) + " max="
                    + MaxReferenceBurnDeltaV.ToString("F1", IC));

            if (i.FrameDegenerate)
                return Refuse(MoveRefusal.Degenerate, "frame=degenerate");

            if (i.DestinationTaken)
                return Refuse(MoveRefusal.DestinationTaken,
                    "blocker=" + (i.DestinationBlocker ?? "?") + " gap="
                    + i.DestinationGap.ToString("F1", IC));

            return MoveGateVerdict.Pass;
        }

        private static MoveGateVerdict Refuse(MoveRefusal refusal, string detail)
        {
            return new MoveGateVerdict { Refusal = refusal, Detail = detail ?? string.Empty };
        }

        /// <summary>
        /// Destination gate: the first placed group body that overlaps a body outside the
        /// group, as bounding spheres plus <paramref name="padding"/>. False when clear.
        /// <paramref name="gap"/> is the most negative clearance found (metres).
        /// </summary>
        internal static bool TryFindDestinationConflict(
            IList<PlacedBody> placed, IList<PlacedBody> others, double padding,
            out string blocker, out double gap)
        {
            blocker = null;
            gap = double.PositiveInfinity;
            if (placed == null || others == null)
                return false;
            bool conflict = false;
            for (int p = 0; p < placed.Count; p++)
            {
                for (int o = 0; o < others.Count; o++)
                {
                    double clearance = (placed[p].Position - others[o].Position).magnitude
                        - placed[p].Radius - others[o].Radius - padding;
                    if (clearance < 0.0 && clearance < gap)
                    {
                        gap = clearance;
                        blocker = others[o].Name;
                        conflict = true;
                    }
                }
            }
            if (!conflict)
                gap = double.PositiveInfinity;
            return conflict;
        }

        /// <summary>
        /// Spawn room (design 4.4, used by Step 2): the smallest along-track shift that
        /// clears every overlap <paramref name="overlapsAtShift"/> reports, in 1 m steps up to
        /// <see cref="SlideCapMeters"/>, then <see cref="SlideMarginMeters"/> more. The shift
        /// runs away from the reference along the group's along-track offset, or behind the
        /// reference when that offset is zero. No overlap at zero shift returns 0.
        /// </summary>
        internal static SlideResult ComputeSpawnRoomSlide(
            double alongTrackOffset, Func<double, bool> overlapsAtShift)
        {
            if (overlapsAtShift == null)
                return new SlideResult { Cleared = true, ShiftMeters = 0.0, StepsTried = 0 };
            double direction = alongTrackOffset > 1e-9 ? 1.0 : -1.0;
            int maxSteps = (int)Math.Round(SlideCapMeters / SlideStepMeters);
            for (int k = 0; k <= maxSteps; k++)
            {
                double shift = direction * k * SlideStepMeters;
                if (overlapsAtShift(shift))
                    continue;
                if (k == 0)
                    return new SlideResult { Cleared = true, ShiftMeters = 0.0, StepsTried = 1 };
                double withMargin = direction * (k * SlideStepMeters + SlideMarginMeters);
                // The margin must not land in a second overlap; fall back to the bare clear.
                double chosen = overlapsAtShift(withMargin) ? shift : withMargin;
                return new SlideResult { Cleared = true, ShiftMeters = chosen, StepsTried = k + 1 };
            }
            return new SlideResult { Cleared = false, ShiftMeters = 0.0, StepsTried = maxSteps + 1 };
        }

        /// <summary>
        /// Encodes a WORLD attitude against the reference's WORLD orbital frame, the
        /// recorder's convention (<c>LookRotation(orbitalVelocity_WORLD, radialOut_WORLD)</c>,
        /// <see cref="FlightRecorder.StampVesselOrbitalFrameRotation"/>). The reference's Zup
        /// state VECTORS are swizzled to world once; no rotation is ever applied to the
        /// quaternion itself, so a world-frame rotation between encode and decode cancels.
        /// </summary>
        internal static Quaternion EncodeAttitude(
            Quaternion worldRotation, Vector3d refPosZup, Vector3d refVelZup)
        {
            Vector3d velWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(refVelZup);
            Vector3d radialWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(refPosZup).normalized;
            return TrajectoryMath.ComputeOrbitalFrameRotation(worldRotation, velWorld, radialWorld);
        }

        /// <summary>
        /// Decodes an orbital-frame attitude against the reference's WORLD orbital frame at
        /// another instant: the inverse of <see cref="EncodeAttitude"/>, built through the
        /// same frame construction (so the near-parallel fallback agrees on both sides).
        /// </summary>
        internal static Quaternion DecodeAttitude(
            Quaternion orbitalFrameRotation, Vector3d refPosZup, Vector3d refVelZup)
        {
            Vector3d velWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(refVelZup);
            Vector3d radialWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(refPosZup).normalized;
            Quaternion inverseFrame = TrajectoryMath.ComputeOrbitalFrameRotation(
                Quaternion.identity, velWorld, radialWorld);
            return TrajectoryMath.PureNormalize(TrajectoryMath.PureMultiply(
                TrajectoryMath.PureInverse(inverseFrame), orbitalFrameRotation));
        }

        /// <summary>"(r,t,n)" components at one decimal, invariant.</summary>
        internal static string FormatComponents(Vector3d v)
        {
            return "(" + v.x.ToString("F1", IC) + "," + v.y.ToString("F1", IC) + ","
                + v.z.ToString("F1", IC) + ")";
        }

        /// <summary>A metre or m/s value at one decimal, "n/a" when unknown.</summary>
        internal static string FormatMeters(double value)
        {
            return IsFinite(value) ? value.ToString("F1", IC) : "n/a";
        }

        /// <summary>The per-member placement line (one per moved vessel, bounded by the group).</summary>
        internal static string FormatPlacedVesselLine(
            uint pid, string name, RelativeState atT0, RelativeState atT1)
        {
            return "Placed vessel: pid=" + pid.ToString(IC)
                + " name=" + (name ?? "(null)")
                + " offsetT0=" + FormatComponents(atT0.Offset)
                + " offsetT1=" + FormatComponents(atT1.Offset)
                + " dvRel=" + atT0.Velocity.magnitude.ToString("F2", IC);
        }

        /// <summary>The one summary line of a placement jump.</summary>
        internal static string FormatPlacementSummary(
            string recordingId, string kind, double t0, double t1, double placeUT,
            IList<uint> movedPids, double refGapToDrawn, double slideMeters,
            string recorderOutcome, int backgroundRecaptured)
        {
            var moved = new System.Text.StringBuilder();
            int count = movedPids != null ? movedPids.Count : 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) moved.Append(',');
                moved.Append(movedPids[i].ToString(IC));
            }
            return "Placement jump: ref=" + (recordingId ?? "(null)")
                + " kind=" + (kind ?? "?")
                + " T0=" + t0.ToString("F1", IC)
                + " T1=" + t1.ToString("F1", IC)
                + " placeUT=" + placeUT.ToString("F2", IC)
                + " group=" + count.ToString(IC)
                + " moved=" + (count > 0 ? moved.ToString() : "none")
                + " refGapToDrawn=" + FormatMeters(refGapToDrawn) + "m"
                + " slide=" + slideMeters.ToString("F1", IC) + "m"
                + " recorder=" + (recorderOutcome ?? "none")
                + " bgRecaptured=" + backgroundRecaptured.ToString(IC);
        }

        /// <summary>
        /// The per-member check at the first unpacked frame: the distance to the DRAWN
        /// reference ghost and to the player's vessel, at the click and now.
        /// </summary>
        internal static string FormatUnpackedLine(
            uint pid, string name, double ut,
            double refDistanceT0, double refDistanceNow,
            double toActiveT0, double toActiveNow,
            double attitudeErrorDeg, bool reasserted)
        {
            double refDrift = IsFinite(refDistanceT0) && IsFinite(refDistanceNow)
                ? Math.Abs(refDistanceNow - refDistanceT0) : double.NaN;
            double activeDrift = IsFinite(toActiveT0) && IsFinite(toActiveNow)
                ? Math.Abs(toActiveNow - toActiveT0) : double.NaN;
            return "Placement unpacked: pid=" + pid.ToString(IC)
                + " name=" + (name ?? "(null)")
                + " ut=" + ut.ToString("F2", IC)
                + " refDistanceT0=" + FormatMeters(refDistanceT0) + "m"
                + " refDistanceNow=" + FormatMeters(refDistanceNow) + "m"
                + " refDrift=" + FormatMeters(refDrift) + "m"
                + " toActiveT0=" + FormatMeters(toActiveT0) + "m"
                + " toActiveNow=" + FormatMeters(toActiveNow) + "m"
                + " toActiveDrift=" + FormatMeters(activeDrift) + "m"
                + " attitudeErrDeg=" + FormatMeters(attitudeErrorDeg)
                + " reasserted=" + (reasserted ? "true" : "false");
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
