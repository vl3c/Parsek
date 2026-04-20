using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Parsek
{
    internal enum PatchedConicSnapshotFailureReason
    {
        None = 0,
        NullSolver = 1,
        UpdateFailed = 2
    }

    internal enum PatchedConicTransitionType
    {
        Initial = 0,
        Final = 1,
        Encounter = 2,
        Escape = 3,
        Maneuver = 4,
        Impact = 5,
        Unknown = 6
    }

    internal struct PatchedConicSnapshotResult
    {
        public List<OrbitSegment> Segments;
        public PatchedConicSnapshotFailureReason FailureReason;
        public int CapturedPatchCount;
        public int TruncatedPatchCount;
        public bool StoppedBeforeManeuver;
        public int OriginalPatchLimit;
        public int AppliedPatchLimit;
        public string LastCapturedBodyName;
    }

    internal interface IPatchedConicSnapshotSource
    {
        string VesselName { get; }
        bool IsAvailable { get; }
        int PatchLimit { get; set; }
        IPatchedConicOrbitPatch RootPatch { get; }
        void Update();
    }

    internal interface IPatchedConicOrbitPatch
    {
        double StartUT { get; }
        double EndUT { get; }
        double Inclination { get; }
        double Eccentricity { get; }
        double SemiMajorAxis { get; }
        double LongitudeOfAscendingNode { get; }
        double ArgumentOfPeriapsis { get; }
        double MeanAnomalyAtEpoch { get; }
        double Epoch { get; }
        string BodyName { get; }
        PatchedConicTransitionType EndTransition { get; }
        IPatchedConicOrbitPatch NextPatch { get; }
    }

    internal static class PatchedConicSnapshot
    {
        internal const int PatchedConicSolverCaptureLimit = 8;

        internal static PatchedConicSnapshotResult SnapshotPatchedConicChain(
            Vessel vessel,
            int captureLimit = PatchedConicSolverCaptureLimit)
        {
            return SnapshotPatchedConicChain(vessel, Planetarium.GetUniversalTime(), captureLimit);
        }

        internal static PatchedConicSnapshotResult SnapshotPatchedConicChain(
            Vessel vessel,
            double snapshotUT,
            int captureLimit = PatchedConicSolverCaptureLimit)
        {
            if (vessel == null)
                return SnapshotPatchedConicChain((IPatchedConicSnapshotSource)null, snapshotUT, captureLimit, "(null)");

            return SnapshotPatchedConicChain(
                new VesselPatchedConicSnapshotSource(vessel),
                snapshotUT,
                captureLimit,
                vessel.vesselName);
        }

        internal static PatchedConicSnapshotResult SnapshotPatchedConicChain(
            IPatchedConicSnapshotSource source,
            double snapshotUT,
            int captureLimit,
            string vesselName = null)
        {
            int normalizedLimit = Math.Max(0, captureLimit);
            string safeVesselName = !string.IsNullOrEmpty(vesselName)
                ? vesselName
                : source?.VesselName ?? "(unknown)";
            var result = new PatchedConicSnapshotResult
            {
                Segments = new List<OrbitSegment>(),
                FailureReason = PatchedConicSnapshotFailureReason.None
            };

            if (source == null || !source.IsAvailable)
            {
                result.FailureReason = PatchedConicSnapshotFailureReason.NullSolver;
                ParsekLog.Warn("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} solver unavailable");
                return result;
            }

            result.OriginalPatchLimit = source.PatchLimit;
            result.AppliedPatchLimit = Math.Max(result.OriginalPatchLimit, normalizedLimit);

            ParsekLog.Info("PatchedSnapshot",
                $"SnapshotPatchedConicChain: vessel={safeVesselName} snapshotUT={snapshotUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"patchLimit={result.OriginalPatchLimit} captureLimit={normalizedLimit}");

            try
            {
                if (result.AppliedPatchLimit != result.OriginalPatchLimit)
                    source.PatchLimit = result.AppliedPatchLimit;

                source.Update();

                IPatchedConicOrbitPatch patch = source.RootPatch;
                while (patch != null && result.CapturedPatchCount < normalizedLimit)
                {
                    bool stopsBeforeManeuver = patch.EndTransition == PatchedConicTransitionType.Maneuver;
                    result.Segments.Add(ToOrbitSegment(
                        patch,
                        result.CapturedPatchCount == 0 ? snapshotUT : double.NaN));
                    result.LastCapturedBodyName = patch.BodyName;
                    result.CapturedPatchCount++;

                    if (stopsBeforeManeuver)
                    {
                        result.StoppedBeforeManeuver = true;
                        result.TruncatedPatchCount++;
                        break;
                    }

                    patch = patch.NextPatch;
                }

                if (!result.StoppedBeforeManeuver && patch != null && result.CapturedPatchCount >= normalizedLimit)
                    result.TruncatedPatchCount++;

                ParsekLog.Verbose("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} captured={result.CapturedPatchCount} " +
                    $"truncated={result.TruncatedPatchCount} stoppedBeforeManeuver={result.StoppedBeforeManeuver} " +
                    $"lastBody={result.LastCapturedBodyName ?? "(none)"}");
            }
            catch (Exception ex)
            {
                result.Segments.Clear();
                result.CapturedPatchCount = 0;
                result.TruncatedPatchCount = 0;
                result.StoppedBeforeManeuver = false;
                result.LastCapturedBodyName = null;
                result.FailureReason = PatchedConicSnapshotFailureReason.UpdateFailed;
                ParsekLog.Error("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} Update() failed ({ex.GetType().Name}: {ex.Message})");
            }
            finally
            {
                source.PatchLimit = result.OriginalPatchLimit;
            }

            return result;
        }

        private static OrbitSegment ToOrbitSegment(IPatchedConicOrbitPatch patch, double clampStartUT)
        {
            double startUT = patch.StartUT;
            if (!double.IsNaN(clampStartUT) && clampStartUT > startUT)
                startUT = clampStartUT;

            double endUT = patch.EndUT < startUT
                ? startUT
                : patch.EndUT;

            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = patch.Inclination,
                eccentricity = patch.Eccentricity,
                semiMajorAxis = patch.SemiMajorAxis,
                longitudeOfAscendingNode = patch.LongitudeOfAscendingNode,
                argumentOfPeriapsis = patch.ArgumentOfPeriapsis,
                meanAnomalyAtEpoch = patch.MeanAnomalyAtEpoch,
                epoch = patch.Epoch,
                bodyName = patch.BodyName,
                isPredicted = true
            };
        }

        private sealed class VesselPatchedConicSnapshotSource : IPatchedConicSnapshotSource
        {
            private static readonly FieldInfo PatchLimitField =
                typeof(PatchedConicSolver).GetField("patchLimit", BindingFlags.Instance | BindingFlags.NonPublic);

            private readonly Vessel vessel;

            internal VesselPatchedConicSnapshotSource(Vessel vessel)
            {
                this.vessel = vessel;
            }

            public string VesselName => vessel?.vesselName ?? "(unknown)";

            public bool IsAvailable => vessel != null
                && vessel.patchedConicSolver != null
                && vessel.orbit != null;

            public int PatchLimit
            {
                get
                {
                    if (PatchLimitField == null || vessel?.patchedConicSolver == null)
                        return 0;

                    object value = PatchLimitField.GetValue(vessel.patchedConicSolver);
                    return value is int patchLimit
                        ? patchLimit
                        : 0;
                }
                set
                {
                    if (PatchLimitField == null || vessel?.patchedConicSolver == null)
                        return;

                    PatchLimitField.SetValue(vessel.patchedConicSolver, value);
                }
            }

            public IPatchedConicOrbitPatch RootPatch => vessel?.orbit != null
                ? new VesselPatchedConicOrbitPatch(vessel.orbit)
                : null;

            public void Update()
            {
                vessel.patchedConicSolver.Update();
            }
        }

        private sealed class VesselPatchedConicOrbitPatch : IPatchedConicOrbitPatch
        {
            private readonly Orbit patch;

            internal VesselPatchedConicOrbitPatch(Orbit patch)
            {
                this.patch = patch;
            }

            public double StartUT => patch.StartUT;
            public double EndUT => patch.EndUT;
            public double Inclination => patch.inclination;
            public double Eccentricity => patch.eccentricity;
            public double SemiMajorAxis => patch.semiMajorAxis;
            public double LongitudeOfAscendingNode => patch.LAN;
            public double ArgumentOfPeriapsis => patch.argumentOfPeriapsis;
            public double MeanAnomalyAtEpoch => patch.meanAnomalyAtEpoch;
            public double Epoch => patch.epoch;
            public string BodyName => patch.referenceBody != null ? patch.referenceBody.name : "Kerbin";
            public PatchedConicTransitionType EndTransition => MapTransition(patch.patchEndTransition);
            public IPatchedConicOrbitPatch NextPatch => patch.nextPatch != null
                ? new VesselPatchedConicOrbitPatch(patch.nextPatch)
                : null;

            private static PatchedConicTransitionType MapTransition(Orbit.PatchTransitionType transition)
            {
                switch (transition)
                {
                    case Orbit.PatchTransitionType.INITIAL:
                        return PatchedConicTransitionType.Initial;
                    case Orbit.PatchTransitionType.FINAL:
                        return PatchedConicTransitionType.Final;
                    case Orbit.PatchTransitionType.ENCOUNTER:
                        return PatchedConicTransitionType.Encounter;
                    case Orbit.PatchTransitionType.ESCAPE:
                        return PatchedConicTransitionType.Escape;
                    case Orbit.PatchTransitionType.MANEUVER:
                        return PatchedConicTransitionType.Maneuver;
                    case Orbit.PatchTransitionType.IMPACT:
                        return PatchedConicTransitionType.Impact;
                    default:
                        return PatchedConicTransitionType.Unknown;
                }
            }
        }
    }
}
