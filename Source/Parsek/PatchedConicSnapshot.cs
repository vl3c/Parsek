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
        MissingPatchBody = 4,

        /// <summary>
        /// A patch whose orbital elements (or UT bounds) are not finite. Stock's own
        /// solver can produce these - it logs "dT is NaN! tA: NaN, E: NaN, M: NaN,
        /// T: NaN" while doing it - and they are unusable as a predicted tail, so they
        /// are refused here rather than copied into a recording. Treated exactly like
        /// <see cref="MissingPatchBody"/> downstream: a transient degenerate solver
        /// state, not a destroyed vessel.
        /// </summary>
        NonFinitePatchElements = 5
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
                    $"SnapshotPatchedConicChain: vessel={safeVesselName} solver unavailable",
                    minIntervalSeconds: 30.0);
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

                ParsekLog.VerboseOnChange("PatchedSnapshot",
                    "snapshot-start|" + safeVesselName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "patchLimit={0}|captureLimit={1}|applied={2}",
                        result.OriginalPatchLimit,
                        normalizedLimit,
                        result.AppliedPatchLimit),
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
                            ParsekLog.VerboseOnChange("PatchedSnapshot",
                                "snapshot-truncated|" + safeVesselName,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "patchIndex={0}|body={1}|valid={2}|reason={3}",
                                    failedPatchIndex,
                                    MissingPatchBodySentinel,
                                    failedPatchIndex,
                                    result.FailureReason),
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

                    if (!HasFinitePatchElements(patch))
                    {
                        // Same partial-keep semantics as the null-body case above, for
                        // the same reason: earlier patches in the chain are real orbits
                        // worth recording, and only a degenerate patch 0 means "no usable
                        // data". Refusing here is what keeps a NaN out of the recording's
                        // predicted segments (and out of every downstream `new Orbit(...)`
                        // consumer) instead of relying on each of them to notice.
                        int failedPatchIndex = result.CapturedPatchCount;
                        string elements = DescribePatchElements(patch);
                        if (failedPatchIndex > 0)
                        {
                            result.FailureReason = PatchedConicSnapshotFailureReason.NonFinitePatchElements;
                            result.HasTruncatedTail = true;
                            ParsekLog.VerboseOnChange("PatchedSnapshot",
                                "snapshot-nonfinite|" + safeVesselName,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "patchIndex={0}|valid={1}|elements={2}",
                                    failedPatchIndex,
                                    failedPatchIndex,
                                    elements),
                                $"SnapshotPatchedConicChain: vessel={safeVesselName} patchIndex={failedPatchIndex} " +
                                $"body={bodyName} has non-finite elements ({elements}); truncated chain after " +
                                $"{failedPatchIndex} valid patch(es), keeping partial result");
                            break;
                        }

                        ResetFailedResult(ref result, PatchedConicSnapshotFailureReason.NonFinitePatchElements);
                        ParsekLog.WarnRateLimited("PatchedSnapshot",
                            "nonfinite-patch-" + safeVesselName,
                            $"SnapshotPatchedConicChain: vessel={safeVesselName} patchIndex={failedPatchIndex} " +
                            $"body={bodyName} has non-finite elements ({elements}); aborting predicted snapshot capture",
                            minIntervalSeconds: 30.0);
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

                ParsekLog.VerboseOnChange("PatchedSnapshot",
                    "snapshot-captured|" + safeVesselName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "captured={0}|truncated={1}|maneuver={2}|lastBody={3}|failure={4}",
                        result.CapturedPatchCount,
                        result.HasTruncatedTail,
                        result.EncounteredManeuverNode,
                        result.LastCapturedBodyName ?? "(none)",
                        result.FailureReason),
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

        /// <summary>
        /// Whether a stock patch carries elements Parsek can actually record. Stock's
        /// patched-conic solver does hand out Not-a-Number patches (measured on an
        /// orbital EVA kerbal, alongside stock's own "dT is NaN! tA: NaN, E: NaN,
        /// M: NaN, T: NaN" and "CheckEncounter: failed to find any intercepts at all"),
        /// and <see cref="ToOrbitSegment"/> copies every field verbatim.
        /// <para>
        /// The UT BOUNDS are checked with the elements, not separately: a segment's
        /// <c>endUT</c> is the UT the finalizer propagates the terminal predicted
        /// segment AT, so a NaN there poisons the propagation just as thoroughly as a
        /// NaN element would - and <c>ToOrbitSegment</c>'s <c>patch.EndUT &lt; startUT</c>
        /// clamp cannot catch it, because every comparison against NaN is false.
        /// </para>
        /// <para>
        /// <c>EndUT</c> IS DELIBERATELY NOT REQUIRED TO BE FINITE, and getting this
        /// wrong silently discards healthy orbits. <c>+Infinity</c> is stock's own
        /// sentinel for "this patch never ends", not a degeneracy - verified against
        /// decompiled KSP 1.12.5, on two independent paths:
        /// <list type="bullet">
        /// <item><c>PatchedConicSolver.Update</c> seeds the root patch with
        /// <c>patches[0].EndUT = patches[0].period</c> whenever
        /// <c>eccentricity &gt;= 1.0</c>, and <c>Orbit</c> assigns
        /// <c>period = double.PositiveInfinity</c> for every open orbit.</item>
        /// <item><c>PatchedConics._CalculatePatch</c> assigns
        /// <c>p.EndUT = double.PositiveInfinity</c> outright for a hyperbolic patch
        /// whose reference body has an infinite sphere of influence (the Sun), and
        /// marks it <c>patchEndTransition = FINAL</c> - a fully solved patch.</item>
        /// </list>
        /// So a solar-escape probe carries <c>EndUT = +Infinity</c> with all seven
        /// elements finite and perfectly propagatable. Refusing it would abort the
        /// whole capture at patch 0 and leave the recording with no predicted tail at
        /// all, while a 30 s WARN described a permanent orbital condition as a
        /// transient solver glitch. Only NaN (the measured EVA case) and
        /// <c>-Infinity</c> (never legitimate - time does not run backwards to a
        /// bound) are refused here.
        /// </para>
        /// </summary>
        internal static bool HasFinitePatchElements(IPatchedConicOrbitPatch patch)
        {
            if (patch == null)
                return false;

            return IsFinite(patch.StartUT)
                && IsUsableEndUT(patch.EndUT)
                && IsFinite(patch.Inclination)
                && IsFinite(patch.Eccentricity)
                && IsFinite(patch.SemiMajorAxis)
                && IsFinite(patch.LongitudeOfAscendingNode)
                && IsFinite(patch.ArgumentOfPeriapsis)
                && IsFinite(patch.MeanAnomalyAtEpoch)
                && IsFinite(patch.Epoch);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// The end bound's weaker contract: anything but NaN and <c>-Infinity</c>.
        /// See <see cref="HasFinitePatchElements"/> for why <c>+Infinity</c> is a
        /// legitimate stock value here and nowhere else in the patch.
        /// </summary>
        private static bool IsUsableEndUT(double value)
        {
            return !double.IsNaN(value) && !double.IsNegativeInfinity(value);
        }

        /// <summary>
        /// Names the offending input on the refusal log line: which of the nine values
        /// failed their check, and what they were. Without this the WARN says a patch
        /// was bad without saying which number was bad.
        /// <para>
        /// This MUST apply the same per-field rule as
        /// <see cref="HasFinitePatchElements"/> - note <c>endUT</c> uses the weaker
        /// <see cref="IsUsableEndUT"/> test, so a legitimate <c>+Infinity</c> end bound
        /// is not reported as offending. If the two ever diverge, the WARN starts
        /// printing "has non-finite elements ((all-finite))" about a patch the
        /// predicate just rejected.
        /// </para>
        /// </summary>
        private static string DescribePatchElements(IPatchedConicOrbitPatch patch)
        {
            if (patch == null)
                return "(null-patch)";

            var offending = new List<string>();
            AppendIfNonFinite(offending, "startUT", patch.StartUT);
            if (!IsUsableEndUT(patch.EndUT))
                offending.Add("endUT=" + patch.EndUT.ToString("R", CultureInfo.InvariantCulture));
            AppendIfNonFinite(offending, "inc", patch.Inclination);
            AppendIfNonFinite(offending, "ecc", patch.Eccentricity);
            AppendIfNonFinite(offending, "sma", patch.SemiMajorAxis);
            AppendIfNonFinite(offending, "lan", patch.LongitudeOfAscendingNode);
            AppendIfNonFinite(offending, "argPe", patch.ArgumentOfPeriapsis);
            AppendIfNonFinite(offending, "mEp", patch.MeanAnomalyAtEpoch);
            AppendIfNonFinite(offending, "epoch", patch.Epoch);

            return offending.Count > 0 ? string.Join(" ", offending.ToArray()) : "(all-finite)";
        }

        private static void AppendIfNonFinite(List<string> offending, string name, double value)
        {
            if (!IsFinite(value))
                offending.Add(name + "=" + value.ToString("R", CultureInfo.InvariantCulture));
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
