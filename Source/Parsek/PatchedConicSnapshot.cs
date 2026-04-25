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
        UpdateFailed = 2,
        PatchLimitUnavailable = 3,
        MissingPatchBody = 4
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
        public bool HasTruncatedTail;
        public bool EncounteredManeuverNode;
        public int OriginalPatchLimit;
        public int AppliedPatchLimit;
        public string LastCapturedBodyName;
    }

    internal interface IPatchedConicSnapshotSource
    {
        string VesselName { get; }
        bool IsAvailable { get; }
        bool HasPatchLimitAccess { get; }
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
        private const string MissingPatchBodySentinel = "(missing-reference-body)";

        // Eight patches covers the common stock chains we need at finalize time
        // (current orbit, transfer, encounter/capture, and a few follow-on legs)
        // without expanding solver work for long multi-SOI plans.
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
                // #576: rate-limit per vessel name. The 2026-04-25 marker-validator
                // playtest emitted 146 of these in an hour, clustered as
                // 77×Kerbal X Debris + 45×Ermore Kerman + 12×Magdo Kerman +
                // 11×Kerbal X Probe + 1×Kerbal X. The first four populations are
                // by-design solver-less in stock KSP (debris has no command
                // module; EVA kerbals run on the kerbal jetpack motion system;
                // probe-debris loses solver state when the active vessel switches
                // away). NullSolver is still emitted as the FailureReason and the
                // downstream `IncompleteBallisticSceneExitFinalizer` continues to
                // treat it as the destroyed-vessel / no-solver-by-design
                // fingerprint that the live-orbit fallback was designed for; only
                // the log-noise floor changes. Per-vessel keying preserves the
                // first-of-its-kind hit per vessel so a fresh regression on a
                // piloted craft mid-flight still surfaces immediately, while the
                // repeating per-debris-vessel floor is absorbed into a single
                // line per 30 s window with a `suppressed=N` suffix.
                ParsekLog.WarnRateLimited("PatchedSnapshot",
                    "solver-unavailable-" + safeVesselName,
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} solver unavailable");
                return result;
            }

            if (!source.HasPatchLimitAccess)
            {
                result.FailureReason = PatchedConicSnapshotFailureReason.PatchLimitUnavailable;
                ParsekLog.Warn("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} patchLimit reflection unavailable; aborting predicted snapshot capture");
                return result;
            }

            bool patchLimitRaised = false;
            try
            {
                result.OriginalPatchLimit = source.PatchLimit;
                result.AppliedPatchLimit = Math.Max(result.OriginalPatchLimit, normalizedLimit);

                ParsekLog.Info("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} snapshotUT={snapshotUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"patchLimit={result.OriginalPatchLimit} captureLimit={normalizedLimit}");

                if (result.AppliedPatchLimit != result.OriginalPatchLimit)
                {
                    source.PatchLimit = result.AppliedPatchLimit;
                    patchLimitRaised = true;
                }

                // Stock solver refresh is only trustworthy while the scene is actively
                // simming. If a future caller snapshots during a paused/menu frame,
                // the chain may still reflect stale pre-pause solver state.
                source.Update();

                IPatchedConicOrbitPatch patch = source.RootPatch;
                while (patch != null && result.CapturedPatchCount < normalizedLimit)
                {
                    string bodyName = patch.BodyName;
                    if (string.IsNullOrEmpty(bodyName))
                    {
                        int failedPatchIndex = result.CapturedPatchCount;
                        // Preserve the partial chain captured before the first
                        // null-body patch (#575). KSP's stock solver routinely
                        // has a transient `nextPatch.referenceBody == null`
                        // during ascent, but earlier patches in the chain are
                        // valid orbits we want to record. Discarding everything
                        // when patch[N>0] is null was costing the recording its
                        // entire predicted tail and feeding the
                        // `IncompleteBallisticSceneExitFinalizer` "transient
                        // early-ascent state" skip path on every refresh, so
                        // the recording effectively had no patched-conic
                        // augmentation. Only reset when patch 0 is null —
                        // that's the genuine "no usable data" case the WARN
                        // tier was designed for.
                        if (failedPatchIndex > 0)
                        {
                            result.FailureReason = PatchedConicSnapshotFailureReason.MissingPatchBody;
                            result.HasTruncatedTail = true;
                            ParsekLog.Verbose("PatchedSnapshot",
                                $"SnapshotPatchedConicChain: vessel={safeVesselName} patchIndex={failedPatchIndex} " +
                                $"body={MissingPatchBodySentinel}; truncated chain after {failedPatchIndex} valid patch(es), " +
                                "keeping partial result");
                            break;
                        }
                        ResetFailedResult(ref result, PatchedConicSnapshotFailureReason.MissingPatchBody);
                        ParsekLog.Warn("PatchedSnapshot",
                            $"SnapshotPatchedConicChain: vessel={safeVesselName} patchIndex={failedPatchIndex} " +
                            $"body={MissingPatchBodySentinel}; aborting predicted snapshot capture");
                        return result;
                    }

                    bool endsAtManeuverNode = patch.EndTransition == PatchedConicTransitionType.Maneuver;
                    result.Segments.Add(ToOrbitSegment(
                        patch,
                        result.CapturedPatchCount == 0 ? snapshotUT : double.NaN,
                        bodyName));
                    result.LastCapturedBodyName = bodyName;
                    result.CapturedPatchCount++;

                    if (endsAtManeuverNode)
                    {
                        result.EncounteredManeuverNode = true;
                        result.HasTruncatedTail = true;
                        break;
                    }

                    patch = patch.NextPatch;
                }

                if (!result.EncounteredManeuverNode && patch != null && result.CapturedPatchCount >= normalizedLimit)
                    result.HasTruncatedTail = true;

                ParsekLog.Verbose("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} captured={result.CapturedPatchCount} " +
                    $"hasTruncatedTail={result.HasTruncatedTail} encounteredManeuverNode={result.EncounteredManeuverNode} " +
                    $"lastBody={result.LastCapturedBodyName ?? "(none)"}");
            }
            catch (Exception ex)
            {
                ResetFailedResult(ref result, PatchedConicSnapshotFailureReason.UpdateFailed);
                ParsekLog.Error("PatchedSnapshot",
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} Update() failed ({ex.GetType().Name}: {ex.Message})");
            }
            finally
            {
                if (patchLimitRaised)
                {
                    try
                    {
                        source.PatchLimit = result.OriginalPatchLimit;
                    }
                    catch (Exception ex)
                    {
                        ResetFailedResult(ref result, PatchedConicSnapshotFailureReason.UpdateFailed);
                        ParsekLog.Error("PatchedSnapshot",
                            $"SnapshotPatchedConicChain: vessel={safeVesselName} failed to restore patchLimit={result.OriginalPatchLimit} " +
                            $"({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }

            return result;
        }

        private static void ResetFailedResult(
            ref PatchedConicSnapshotResult result,
            PatchedConicSnapshotFailureReason failureReason)
        {
            result.Segments.Clear();
            result.CapturedPatchCount = 0;
            result.HasTruncatedTail = false;
            result.EncounteredManeuverNode = false;
            result.LastCapturedBodyName = null;
            result.FailureReason = failureReason;
        }

        private static OrbitSegment ToOrbitSegment(
            IPatchedConicOrbitPatch patch,
            double clampStartUT,
            string bodyName)
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
                bodyName = bodyName,
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

            public bool HasPatchLimitAccess => PatchLimitField != null
                && vessel?.patchedConicSolver != null;

            public int PatchLimit
            {
                get
                {
                    if (!HasPatchLimitAccess)
                        throw new InvalidOperationException("patchLimit reflection unavailable");

                    object value = PatchLimitField.GetValue(vessel.patchedConicSolver);
                    if (!(value is int patchLimit))
                    {
                        throw new InvalidOperationException(
                            $"patchLimit reflection returned unexpected value '{value ?? "(null)"}'");
                    }

                    return patchLimit;
                }
                set
                {
                    if (!HasPatchLimitAccess)
                        throw new InvalidOperationException("patchLimit reflection unavailable");

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
            public string BodyName => patch.referenceBody?.name;
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
