using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek.TestCommands;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure helpers for <see cref="FairingGhostVisualInGameTests"/>. No Unity or KSP types in
    /// any signature, so the xUnit suite can pin them (FairingGhostVisualMathTests).
    /// </summary>
    internal static class FairingGhostVisualMath
    {
        /// <summary>Colour variants tried in this order; the first one the part offers wins.</summary>
        internal static readonly string[] PreferredColourVariants =
        {
            "BlackAndWhite", "Black", "Orange", "Silver", "Dark", "Gray", "Grey",
        };

        /// <summary>
        /// Picks a colour variant that differs from the part's base variant: the first
        /// <see cref="PreferredColourVariants"/> entry the part offers (case-insensitive),
        /// else the first offered name that is not the base. Null when the part offers no
        /// non-base variant.
        /// </summary>
        internal static string ChooseColourVariant(IList<string> offered, string baseVariant)
        {
            if (offered == null || offered.Count == 0)
                return null;
            for (int p = 0; p < PreferredColourVariants.Length; p++)
            {
                for (int i = 0; i < offered.Count; i++)
                {
                    string name = offered[i];
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(name, baseVariant, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, PreferredColourVariants[p], StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            for (int i = 0; i < offered.Count; i++)
            {
                string name = offered[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, baseVariant, StringComparison.OrdinalIgnoreCase)) continue;
                return name;
            }
            return null;
        }

        /// <summary>
        /// Camera distance that fits a sphere of <paramref name="boundingRadius"/> into a
        /// perspective view with the given vertical field of view and aspect (width/height),
        /// times <paramref name="margin"/>. Uses the narrower of the two half-angles.
        /// Never below <paramref name="minDistance"/>.
        /// </summary>
        internal static double ComputeFramingDistance(
            double boundingRadius, double verticalFovDeg, double aspect, double margin, double minDistance)
        {
            if (double.IsNaN(boundingRadius) || boundingRadius <= 0.0)
                return minDistance;
            if (double.IsNaN(verticalFovDeg) || verticalFovDeg <= 1.0 || verticalFovDeg >= 179.0)
                verticalFovDeg = 60.0;
            if (double.IsNaN(aspect) || aspect <= 0.0)
                aspect = 16.0 / 9.0;
            double halfV = verticalFovDeg * 0.5 * Math.PI / 180.0;
            double halfH = Math.Atan(Math.Tan(halfV) * aspect);
            double half = Math.Min(halfV, halfH);
            double d = boundingRadius / Math.Sin(half) * margin;
            return Math.Max(d, minDistance);
        }

        /// <summary>
        /// True when a point is in a spherical body's shadow (cylindrical shadow model).
        /// <paramref name="toBody"/> is the vector from the point to the body centre and
        /// <paramref name="toSun"/> the vector from the point toward the sun (any length).
        /// </summary>
        internal static bool IsInBodyShadow(
            double toBodyX, double toBodyY, double toBodyZ,
            double toSunX, double toSunY, double toSunZ,
            double bodyRadius)
        {
            double sunLen = Math.Sqrt(toSunX * toSunX + toSunY * toSunY + toSunZ * toSunZ);
            if (sunLen <= 0.0 || double.IsNaN(sunLen))
                return false;
            double sx = toSunX / sunLen, sy = toSunY / sunLen, sz = toSunZ / sunLen;
            // The body must lie on the sun side of the point to block it.
            double along = toBodyX * sx + toBodyY * sy + toBodyZ * sz;
            if (along <= 0.0)
                return false;
            double px = toBodyX - sx * along, py = toBodyY - sy * along, pz = toBodyZ - sz * along;
            double perp = Math.Sqrt(px * px + py * py + pz * pz);
            return perp < bodyRadius;
        }

        /// <summary>
        /// Picks the interstage attach node an upper part sits on: among the given
        /// candidates, the first whose orientation points up (+Y in part space);
        /// <paramref name="fallback"/> when none does or no candidate is present.
        /// </summary>
        internal static string PickUpwardNode(
            IList<KeyValuePair<string, double>> candidatesWithOrientationY, string fallback)
        {
            if (candidatesWithOrientationY == null)
                return fallback;
            for (int i = 0; i < candidatesWithOrientationY.Count; i++)
            {
                if (candidatesWithOrientationY[i].Value > 0.5)
                    return candidatesWithOrientationY[i].Key;
            }
            return fallback;
        }

        /// <summary>
        /// The screenshot label for configuration <paramref name="configNumber"/> and a phase
        /// (<c>intact</c>, <c>jettisoned</c>, <c>separated</c>, ...): <c>fgv-&lt;n&gt;-&lt;phase&gt;</c>.
        /// </summary>
        internal static string LabelFor(int configNumber, string phase)
            => "fgv-" + configNumber.ToString(CultureInfo.InvariantCulture) + "-" + phase;

        /// <summary>
        /// Whether a stored stock fairing shape closes its nose: stock caps a fairing only
        /// when the LAST cross-section's radius equals the part's capRadius.
        /// </summary>
        internal static bool EndsAtCapRadius(IList<double> radii, double capRadius)
        {
            if (radii == null || radii.Count == 0)
                return false;
            return Math.Abs(radii[radii.Count - 1] - capRadius) < 1e-4;
        }

        /// <summary>
        /// Sideways distance between the real vessel's axis and its ghost's axis: two
        /// fairing radii plus a clear gap, so neither silhouette overlaps the other.
        /// </summary>
        internal static double GhostSideOffset(double maxFairingRadius, double gapMeters)
        {
            if (double.IsNaN(maxFairingRadius) || maxFairingRadius < 0.0)
                maxFairingRadius = 0.0;
            return 2.0 * maxFairingRadius + Math.Max(0.0, gapMeters);
        }
    }

    /// <summary>
    /// FGV-1: SNAPSHOT IMAGES of Parsek's ghost of a stock procedural fairing next to the
    /// real stock vessel, before and after jettison, for the owner's visual review.
    ///
    /// <para>For each of four configurations the test authors a small stock vessel
    /// (fairing base + optional payload), spawns it co-orbital with the active vessel
    /// (a few tens of metres along-track, so nothing touches the ground and nothing drifts
    /// apart during the shots), takes the REAL vessel's own backup snapshot (the same
    /// <see cref="VesselSpawner.TryBackupSnapshot"/> a recording takes), builds the ghost
    /// from it with <see cref="GhostVisualBuilder.BuildTimelineGhostFromSnapshot"/>, and
    /// parents the ghost root to the real root part with a sideways offset, so the floating
    /// origin and Krakensbane carry both together. The flight camera's own update is
    /// suspended while it is framed on the pair (real on the left, ghost on the right,
    /// vessel axis up), and each phase is captured into <c>&lt;KSP&gt;/Screenshots/</c>,
    /// the one directory the harness harvests.</para>
    ///
    /// <para>Jettison is stock <c>DeployFairing()</c> on the real part and
    /// <see cref="GhostPlaybackLogic.ApplyFairingJettisonedState"/> on the ghost.
    /// Separation (configuration 2 only) is stock <c>Part.decouple()</c> on the upper part
    /// (the new vessel is then removed). The ghost is checked twice against the real truss:
    /// the same ghost after the decoupled-subtree hide (fgv-2-separated), then a ghost
    /// rebuilt from a snapshot taken after the split, the way the recording that starts there
    /// sees the craft (fgv-2-separated-branch). Stock keeps the truss shown in both.</para>
    ///
    /// <para>Ghosts never touch game state; the spawned real vessels are removed at the end of
    /// each configuration. Every step logs under the <c>FairingGhostVisual</c> tag.</para>
    /// </summary>
    public class FairingGhostVisualInGameTests
    {
        private readonly InGameTestRunner runner;
        public FairingGhostVisualInGameTests(InGameTestRunner runner) { this.runner = runner; }

        internal const string Tag = "FairingGhostVisual";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const float SpawnLiveTimeoutSeconds = 30f;
        private const float SettleSeconds = 1.5f;
        private const float FramingSettleSeconds = 0.6f;
        private const float JettisonSettleSeconds = 2.5f;
        private const float SeparationSettleSeconds = 1.0f;
        private const float CaptureTimeoutSeconds = 15f;
        private const double GhostGapMeters = 1.2;

        private static readonly string[] PayloadProbeCandidates = { "probeCoreOcto.v2", "probeCoreOcto", "probeCoreSphere.v2" };
        private static readonly string[] UpperStageCandidates = { "fuelTankSmall", "fuelTankSmallFlat", "fuelTank" };

        private enum NoseShape { Capped, OpenTop }

        private enum SkirtKind { Fairing, EnginePlate, EngineShroud }

        private sealed class FairingConfig
        {
            public int Number;
            public string Name;
            public string FairingPart;
            // Heights in units of the part's baseRadius; radii in units of baseRadius except
            // the last one of a capped shape, which is the part's capRadius exactly.
            public double[] HeightsOverBase;
            public double[] RadiiOverBase;
            public NoseShape Nose;
            public bool Interstage;
            public bool ColourVariant;
            public bool PayloadOnTop;
            public double AlongTrackMeters;
            public double CameraElevationDeg;
            public SkirtKind Kind;
        }

        private static List<FairingConfig> BuildConfigs()
        {
            return new List<FairingConfig>
            {
                new FairingConfig
                {
                    Number = 1, Name = "plain-capped", FairingPart = "fairingSize1",
                    HeightsOverBase = new[] { 0.0, 1.6, 3.2, 4.4 },
                    RadiiOverBase = new[] { 1.0, 1.25, 1.25, 0.0 },
                    Nose = NoseShape.Capped, PayloadOnTop = true,
                    AlongTrackMeters = 70.0, CameraElevationDeg = 12.0,
                },
                new FairingConfig
                {
                    Number = 2, Name = "interstage-truss", FairingPart = "fairingSize2",
                    HeightsOverBase = new[] { 0.0, 0.8, 2.1, 2.9 },
                    RadiiOverBase = new[] { 1.0, 1.05, 1.05, 0.0 },
                    Nose = NoseShape.Capped, Interstage = true,
                    AlongTrackMeters = -70.0, CameraElevationDeg = 18.0,
                },
                new FairingConfig
                {
                    Number = 3, Name = "open-top", FairingPart = "fairingSize2",
                    HeightsOverBase = new[] { 0.0, 1.0, 2.0 },
                    RadiiOverBase = new[] { 1.0, 1.05, 1.05 },
                    Nose = NoseShape.OpenTop,
                    AlongTrackMeters = 140.0, CameraElevationDeg = 25.0,
                },
                new FairingConfig
                {
                    Number = 4, Name = "colour-variant", FairingPart = "fairingSize1",
                    HeightsOverBase = new[] { 0.0, 1.6, 3.2, 4.4 },
                    RadiiOverBase = new[] { 1.0, 1.25, 1.25, 0.0 },
                    Nose = NoseShape.Capped, ColourVariant = true, PayloadOnTop = true,
                    AlongTrackMeters = -140.0, CameraElevationDeg = 12.0,
                },
            };
        }

        /// <summary>Per-configuration live state, torn down by <see cref="TearDownConfig"/>.</summary>
        private sealed class ConfigRun
        {
            public FairingConfig Config;
            public uint SpawnedPid;
            public Vessel Real;
            public Part FairingPart;
            public Part UpperPart;
            public uint FairingPid;
            public uint UpperPid;
            public GhostBuildResult Build;
            public GhostPlaybackState State;
            public Vector3 CenterLocal;
            public Vector3 CameraSideLocal;
            public double CameraDistance;
            public double MaxFairingRadius;
            public string VariantName;
        }

        [InGameTest(Category = "FairingGhostVisual", Scene = GameScenes.FLIGHT,
            Description = "Snapshot series: four stock procedural fairings (capped, interstage truss, open top, colour variant) beside their Parsek ghosts, intact / jettisoned / separated, written to Screenshots/fgv-*.png")]
        public IEnumerator FairingGhostsBesideRealVessels_SnapshotSeries()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; FGV-1 needs a live vessel in orbit");
            if (active.Landed || active.Splashed || active.situation == Vessel.Situations.PRELAUNCH)
                InGameAssert.Skip($"Active vessel '{active.vesselName}' is {active.situation}; FGV-1 spawns its fairings co-orbital and needs an orbiting host");
            if (active.orbit == null || active.orbit.referenceBody == null)
                InGameAssert.Skip("Active vessel has no orbit");
            if (FlightCamera.fetch == null)
                InGameAssert.Skip("FlightCamera.fetch is null");
            foreach (string required in new[] { "fairingSize1", "fairingSize2" })
            {
                if (PartLoader.getPartInfoByName(required) == null)
                    InGameAssert.Skip($"PartLoader does not know '{required}' (stock procedural fairing)");
            }

            ParsekLog.Info(Tag, $"start: active='{active.vesselName}' body={active.mainBody.name} " +
                $"alt={active.altitude.ToString("F0", IC)}m ut={Planetarium.GetUniversalTime().ToString("F1", IC)}");

            var failures = new List<string>();
            var captured = new List<string>();
            var configs = BuildConfigs();
            ConfigRun run = null;
            bool uiHidden = false;
            bool cameraSuspended = false;
            try
            {
                GameEvents.onHideUI.Fire();
                uiHidden = true;

                for (int ci = 0; ci < configs.Count; ci++)
                {
                    FairingConfig cfg = configs[ci];
                    run = new ConfigRun { Config = cfg };

                    // ---- spawn the real stock vessel ------------------------------------------
                    ConfigNode vesselNode = BuildVesselNode(cfg, active, run, out string authorFail);
                    if (vesselNode == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: vessel not authored ({authorFail})");
                        ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: SKIPPED, vessel not authored: {authorFail}");
                        run = null;
                        continue;
                    }
                    run.SpawnedPid = SpawnCoOrbital(vesselNode, active, cfg.AlongTrackMeters, cfg.Name);
                    if (run.SpawnedPid == 0)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: SpawnAtPosition returned pid=0");
                        run = null;
                        continue;
                    }
                    float deadline = Time.realtimeSinceStartup + SpawnLiveTimeoutSeconds;
                    while (Time.realtimeSinceStartup < deadline)
                    {
                        Vessel v = FlightRecorder.FindVesselByPid(run.SpawnedPid);
                        if (v != null && v.loaded && !v.packed && v.parts != null && v.parts.Count > 0)
                        {
                            run.Real = v;
                            break;
                        }
                        yield return null;
                    }
                    if (run.Real == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: spawned pid={run.SpawnedPid} never loaded+unpacked in {SpawnLiveTimeoutSeconds}s");
                        TearDownConfig(run, "spawn-timeout");
                        run = null;
                        continue;
                    }
                    // Let stock build the flight fairing panels and settle the joints.
                    yield return WaitRealtime(SettleSeconds);
                    if (run.Real == null || run.Real.state == Vessel.State.DEAD || run.Real.rootPart == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: real vessel died while settling");
                        TearDownConfig(run, "died-settling");
                        run = null;
                        continue;
                    }
                    ResolveLiveParts(run);
                    LogRealVesselState(run, "spawned");

                    // ---- build the ghost from the real vessel's own snapshot ------------------
                    if (!BuildGhostBesideReal(run, out string ghostFail))
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: ghost not built ({ghostFail})");
                        TearDownConfig(run, "ghost-build-failed");
                        run = null;
                        continue;
                    }
                    LogGhostStructureState(run, "built");
                    CompareStructure(run, "built", failures);
                    CompareBaseRingTexture(run, "built", failures);

                    // ---- frame and capture: intact ---------------------------------------------
                    ComputeFraming(run);
                    if (!cameraSuspended)
                    {
                        FlightCamera.fetch.DeactivateUpdate();
                        cameraSuspended = true;
                    }
                    yield return HoldCamera(run, FramingSettleSeconds);
                    yield return CaptureShot(run, "intact", captured, failures);

                    // ---- jettison: stock DeployFairing on the real part, Parsek applier on the ghost
                    ModuleProceduralFairing realFairing = run.FairingPart != null
                        ? run.FairingPart.FindModuleImplementing<ModuleProceduralFairing>() : null;
                    int panelsBefore = realFairing != null && realFairing.Panels != null ? realFairing.Panels.Count : -1;
                    if (realFairing != null)
                    {
                        try { realFairing.DeployFairing(); }
                        catch (Exception ex)
                        {
                            failures.Add($"config {cfg.Number} {cfg.Name}: stock DeployFairing threw {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: real fairing part has no ModuleProceduralFairing");
                    }
                    GhostPartEventOutcome jettisonOutcome =
                        GhostPlaybackLogic.ApplyFairingJettisonedState(run.State, run.FairingPid);
                    ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: jettison real DeployFairing panelsBefore={panelsBefore.ToString(IC)} " +
                        $"ghost ApplyFairingJettisonedState pid={run.FairingPid.ToString(IC)} outcome={jettisonOutcome}");
                    if (jettisonOutcome != GhostPartEventOutcome.Applied)
                        failures.Add($"config {cfg.Number} {cfg.Name}: ghost jettison outcome {jettisonOutcome}, expected Applied");
                    yield return HoldCamera(run, JettisonSettleSeconds);
                    LogRealVesselState(run, "jettisoned");
                    LogGhostStructureState(run, "jettisoned");
                    CompareStructure(run, "jettisoned", failures);
                    CompareBaseRingTexture(run, "jettisoned", failures);
                    yield return CaptureShot(run, "jettisoned", captured, failures);

                    // ---- separation (interstage configuration only) ----------------------------
                    if (cfg.Interstage)
                    {
                        IEnumerator sep = SeparateUpperStage(run, captured, failures);
                        while (sep.MoveNext())
                            yield return sep.Current;
                    }

                    TearDownConfig(run, "config-done");
                    run = null;
                    // Two frames for stock to retire the dead vessel before the next spawn.
                    yield return null;
                    yield return null;
                }
            }
            finally
            {
                if (run != null)
                    TearDownConfig(run, "finally");
                if (cameraSuspended && FlightCamera.fetch != null)
                {
                    FlightCamera.fetch.ActivateUpdate();
                    if (FlightGlobals.ActiveVessel != null)
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                }
                if (uiHidden)
                    GameEvents.onShowUI.Fire();
            }

            ParsekLog.Info(Tag, $"summary: configs={configs.Count.ToString(IC)} captures={captured.Count.ToString(IC)} " +
                $"failures={failures.Count.ToString(IC)} labels=[{string.Join(",", captured.ToArray())}]");
            if (failures.Count > 0)
                InGameAssert.Fail("FGV-1: " + failures.Count + " problem(s): " + string.Join("; ", failures.ToArray()));
        }

        // =====================================================================================
        // Engine skirts: engine plate shroud + mount pattern, engine shroud
        // =====================================================================================

        private static readonly string[] PlateCandidates = { "EnginePlate2", "EnginePlate1p5" };
        private static readonly string[] PlateEngineCandidates = { "liquidEngine3.v2", "liquidEngine3" };
        private static readonly string[] ShroudEngineCandidates = { "liquidEngine2.v2", "liquidEngine2", "liquidEngine.v2" };
        private static readonly string[] SkirtTankCandidates = { "fuelTank", "fuelTankSmall" };
        private static readonly string[] SkirtLowerCandidates = { "fuelTankSmallFlat", "fuelTankSmall" };

        private static List<FairingConfig> BuildSkirtConfigs()
        {
            return new List<FairingConfig>
            {
                new FairingConfig
                {
                    Number = 5, Name = "engine-plate", Kind = SkirtKind.EnginePlate,
                    AlongTrackMeters = 70.0, CameraElevationDeg = 15.0,
                },
                new FairingConfig
                {
                    Number = 6, Name = "engine-shroud", Kind = SkirtKind.EngineShroud,
                    AlongTrackMeters = -70.0, CameraElevationDeg = 12.0,
                },
            };
        }

        [InGameTest(Category = "FairingGhostVisual", Scene = GameScenes.FLIGHT,
            Description = "Snapshot series: a stock engine plate (shroud + two-engine mount) and an engine shroud beside their Parsek ghosts, intact / separated / rebuilt after separation, written to Screenshots/fgv-5-*.png and fgv-6-*.png")]
        public IEnumerator EngineSkirtGhostsBesideRealVessels_SnapshotSeries()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; FGV-1 needs a live vessel in orbit");
            if (active.Landed || active.Splashed || active.situation == Vessel.Situations.PRELAUNCH)
                InGameAssert.Skip($"Active vessel '{active.vesselName}' is {active.situation}; FGV-1 needs an orbiting host");
            if (active.orbit == null || active.orbit.referenceBody == null)
                InGameAssert.Skip("Active vessel has no orbit");
            if (FlightCamera.fetch == null)
                InGameAssert.Skip("FlightCamera.fetch is null");

            var failures = new List<string>();
            var captured = new List<string>();
            var configs = BuildSkirtConfigs();
            ConfigRun run = null;
            bool uiHidden = false;
            bool cameraSuspended = false;
            ParsekLog.Info(Tag, $"skirts start: active='{active.vesselName}' configs={configs.Count.ToString(IC)}");
            try
            {
                GameEvents.onHideUI.Fire();
                uiHidden = true;
                for (int ci = 0; ci < configs.Count; ci++)
                {
                    FairingConfig cfg = configs[ci];
                    run = new ConfigRun { Config = cfg };
                    ConfigNode vesselNode = BuildSkirtVesselNode(cfg, active, run, out string authorFail);
                    if (vesselNode == null)
                    {
                        // Making History's engine plates may be absent on an install: say so, no red.
                        ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: SKIPPED, vessel not authored: {authorFail}");
                        if (cfg.Kind != SkirtKind.EnginePlate || !authorFail.StartsWith("no part known", StringComparison.Ordinal))
                            failures.Add($"config {cfg.Number} {cfg.Name}: vessel not authored ({authorFail})");
                        run = null;
                        continue;
                    }
                    run.SpawnedPid = SpawnCoOrbital(vesselNode, active, cfg.AlongTrackMeters, cfg.Name);
                    if (run.SpawnedPid == 0)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: SpawnAtPosition returned pid=0");
                        run = null;
                        continue;
                    }
                    float deadline = Time.realtimeSinceStartup + SpawnLiveTimeoutSeconds;
                    while (Time.realtimeSinceStartup < deadline)
                    {
                        Vessel v = FlightRecorder.FindVesselByPid(run.SpawnedPid);
                        if (v != null && v.loaded && !v.packed && v.parts != null && v.parts.Count > 0)
                        {
                            run.Real = v;
                            break;
                        }
                        yield return null;
                    }
                    if (run.Real == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: spawned pid={run.SpawnedPid} never loaded+unpacked in {SpawnLiveTimeoutSeconds}s");
                        TearDownConfig(run, "spawn-timeout");
                        run = null;
                        continue;
                    }
                    yield return WaitRealtime(SettleSeconds);
                    if (run.Real == null || run.Real.state == Vessel.State.DEAD || run.Real.rootPart == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: real vessel died while settling");
                        TearDownConfig(run, "died-settling");
                        run = null;
                        continue;
                    }
                    ResolveSkirtParts(run);
                    if (run.FairingPart == null || run.UpperPart == null)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: skirt part or the part below it not found on the real vessel");
                        TearDownConfig(run, "parts-missing");
                        run = null;
                        continue;
                    }
                    if (!BuildGhostBesideReal(run, out string ghostFail))
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: ghost not built ({ghostFail})");
                        TearDownConfig(run, "ghost-build-failed");
                        run = null;
                        continue;
                    }
                    CompareSkirt(run, "built", failures);

                    ComputeFraming(run);
                    if (!cameraSuspended)
                    {
                        FlightCamera.fetch.DeactivateUpdate();
                        cameraSuspended = true;
                    }
                    yield return HoldCamera(run, FramingSettleSeconds);
                    yield return CaptureShot(run, "intact", captured, failures);

                    // ---- separation: the part below the skirt leaves ---------------------------
                    Vessel lowerVessel = null;
                    try
                    {
                        ModuleDecouple plateDecoupler = cfg.Kind == SkirtKind.EnginePlate
                            ? run.FairingPart.FindModuleImplementing<ModuleDecouple>() : null;
                        if (plateDecoupler != null)
                            plateDecoupler.Decouple();
                        else
                            run.UpperPart.decouple();
                        lowerVessel = run.UpperPart.vessel;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: stock separation threw {ex.GetType().Name}: {ex.Message}");
                    }
                    yield return null;
                    ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: real lower part pid={run.UpperPid.ToString(IC)} separated " +
                        $"newVessel='{(lowerVessel != null ? lowerVessel.vesselName : "<none>")}'");
                    if (lowerVessel != null && lowerVessel != run.Real)
                    {
                        try { lowerVessel.Die(); }
                        catch (Exception ex)
                        {
                            ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: separated lower vessel Die threw {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    // Ghost, same recording: the subtree hide plus the shroud event the recorder
                    // writes when stock jettisons it.
                    GhostPlaybackLogic.HidePartSubtree(run.State.ghost, run.UpperPid, run.State.partTree);
                    GhostPlaybackLogic.RemovePartSubtreeFromLogicalPresence(
                        run.State.logicalPartIds, run.UpperPid, run.State.partTree);
                    GhostPartEventOutcome shroudOutcome = GhostPlaybackLogic.ApplyJettisonPanelStateWithOutcome(
                        run.State,
                        new PartEvent { partPersistentId = run.FairingPid, eventType = PartEventType.ShroudJettisoned },
                        jettisoned: true);
                    ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: ghost shroud jettison pid={run.FairingPid.ToString(IC)} outcome={shroudOutcome}");
                    if (shroudOutcome != GhostPartEventOutcome.Applied)
                        failures.Add($"config {cfg.Number} {cfg.Name}: ghost shroud jettison outcome {shroudOutcome}, expected Applied");
                    yield return HoldCamera(run, JettisonSettleSeconds);
                    CompareSkirt(run, "separated", failures);
                    yield return CaptureShot(run, "separated", captured, failures);

                    // Ghost of the recording that starts at the separation.
                    if (run.Build != null && run.Build.root != null)
                    {
                        run.Build.root.transform.SetParent(null, false);
                        UnityEngine.Object.Destroy(run.Build.root);
                        run.Build.root = null;
                    }
                    run.Build = null;
                    run.State = null;
                    if (!BuildGhostBesideReal(run, out string rebuildFail))
                    {
                        failures.Add($"config {cfg.Number} {cfg.Name}: post-separation ghost not built ({rebuildFail})");
                    }
                    else
                    {
                        yield return HoldCamera(run, SeparationSettleSeconds);
                        CompareSkirt(run, "separated-branch", failures);
                        yield return CaptureShot(run, "separated-branch", captured, failures);
                    }

                    TearDownConfig(run, "config-done");
                    run = null;
                    yield return null;
                    yield return null;
                }
            }
            finally
            {
                if (run != null)
                    TearDownConfig(run, "finally");
                if (cameraSuspended && FlightCamera.fetch != null)
                {
                    FlightCamera.fetch.ActivateUpdate();
                    if (FlightGlobals.ActiveVessel != null)
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                }
                if (uiHidden)
                    GameEvents.onShowUI.Fire();
            }

            ParsekLog.Info(Tag, $"skirts summary: configs={configs.Count.ToString(IC)} captures={captured.Count.ToString(IC)} " +
                $"failures={failures.Count.ToString(IC)} labels=[{string.Join(",", captured.ToArray())}]");
            if (failures.Count > 0)
                InGameAssert.Fail("FGV-1 skirts: " + failures.Count + " problem(s): " + string.Join("; ", failures.ToArray()));
        }

        private static ConfigNode BuildSkirtVesselNode(FairingConfig cfg, Vessel anchor, ConfigRun run, out string failReason)
        {
            failReason = null;
            var parts = new List<ConfigNode>();
            if (cfg.Kind == SkirtKind.EnginePlate)
            {
                AvailablePart plate = FirstKnownPart(PlateCandidates);
                AvailablePart probe = FirstKnownPart(PayloadProbeCandidates);
                AvailablePart tank = FirstKnownPart(SkirtLowerCandidates);
                AvailablePart engine = FirstKnownPart(PlateEngineCandidates);
                if (plate == null) { failReason = "no part known (" + string.Join(",", PlateCandidates) + ")"; return null; }
                if (probe == null || tank == null || engine == null) { failReason = "probe, tank or engine part missing"; return null; }
                Part pp = plate.partPrefab;
                AttachNode top = pp.FindAttachNode("top"), bottom = pp.FindAttachNode("bottom");
                AttachNode n1 = pp.FindAttachNode("N2_1"), n2 = pp.FindAttachNode("N2_2");
                AttachNode probeBottom = probe.partPrefab.FindAttachNode("bottom");
                AttachNode tankTop = tank.partPrefab.FindAttachNode("top");
                AttachNode engTop = engine.partPrefab.FindAttachNode("top");
                if (top == null || bottom == null || n1 == null || n2 == null || probeBottom == null || tankTop == null || engTop == null)
                {
                    failReason = "attach nodes missing on plate / probe / tank / engine";
                    return null;
                }
                var ids = UniqueFlightIds(5);
                parts.Add(AuthorPartNode(plate, ids[0], 0, Vector3.zero,
                    new Dictionary<string, int> { { "top", 1 }, { "bottom", 2 }, { "N2_1", 3 }, { "N2_2", 4 } }, null,
                    (module, name) =>
                    {
                        if (name == "ModuleDynamicNodes")
                        {
                            module.AddValue("setIndex", "1");
                            module.AddValue("NodeSetIdx", "1");
                        }
                    }));
                parts.Add(AuthorPartNode(probe, ids[1], 0, top.position - probeBottom.position,
                    new Dictionary<string, int> { { "bottom", 0 } }, null, null));
                parts.Add(AuthorPartNode(tank, ids[2], 0, bottom.position - tankTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                parts.Add(AuthorPartNode(engine, ids[3], 0, n1.position - engTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                parts.Add(AuthorPartNode(engine, ids[4], 0, n2.position - engTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                run.MaxFairingRadius = 1.4;
                ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: authored {plate.name} mount=Double (NodeSetIdx 1) " +
                    $"engines={engine.name}x2 on N2_1/N2_2 below={tank.name} on bottom top={probe.name}");
            }
            else
            {
                AvailablePart tank = FirstKnownPart(SkirtTankCandidates);
                AvailablePart engine = FirstKnownPart(ShroudEngineCandidates);
                AvailablePart lower = FirstKnownPart(SkirtLowerCandidates);
                if (tank == null || engine == null || lower == null) { failReason = "tank, engine or lower part missing"; return null; }
                AttachNode tankBottom = tank.partPrefab.FindAttachNode("bottom");
                AttachNode engTop = engine.partPrefab.FindAttachNode("top");
                AttachNode engBottom = engine.partPrefab.FindAttachNode("bottom");
                AttachNode lowerTop = lower.partPrefab.FindAttachNode("top");
                if (tankBottom == null || engTop == null || engBottom == null || lowerTop == null)
                {
                    failReason = "attach nodes missing on tank / engine / lower part";
                    return null;
                }
                var ids = UniqueFlightIds(3);
                Vector3 enginePos = tankBottom.position - engTop.position;
                parts.Add(AuthorPartNode(tank, ids[0], 0, Vector3.zero,
                    new Dictionary<string, int> { { "bottom", 1 } }, null, null));
                parts.Add(AuthorPartNode(engine, ids[1], 0, enginePos,
                    new Dictionary<string, int> { { "top", 0 }, { "bottom", 2 } }, null, null));
                parts.Add(AuthorPartNode(lower, ids[2], 1, enginePos + engBottom.position - lowerTop.position,
                    new Dictionary<string, int> { { "top", 1 } }, null, null));
                run.MaxFairingRadius = 0.9;
                ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: authored {tank.name} > {engine.name} > {lower.name} (shroud shown by the part below)");
            }
            Orbit placeholder = new Orbit(
                anchor.orbit.inclination, anchor.orbit.eccentricity, anchor.orbit.semiMajorAxis,
                anchor.orbit.LAN, anchor.orbit.argumentOfPeriapsis, anchor.orbit.meanAnomalyAtEpoch,
                anchor.orbit.epoch, anchor.orbit.referenceBody);
            return ProtoVessel.CreateVesselNode(
                "FGV-" + cfg.Number.ToString(IC) + " " + cfg.Name, VesselType.Ship, placeholder, 0, parts.ToArray());
        }

        private static List<uint> UniqueFlightIds(int count)
        {
            var ids = new List<uint>();
            while (ids.Count < count)
            {
                uint id = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>The skirt part (plate or engine) and the part on its bottom node.</summary>
        private static void ResolveSkirtParts(ConfigRun run)
        {
            run.FairingPart = null;
            run.UpperPart = null;
            for (int i = 0; i < run.Real.parts.Count; i++)
            {
                Part p = run.Real.parts[i];
                if (p == null) continue;
                bool isSkirt = run.Config.Kind == SkirtKind.EnginePlate
                    ? p.FindModuleImplementing<ModuleDynamicNodes>() != null
                    : p.FindModuleImplementing<ModuleJettison>() != null && p.FindAttachNode("bottom") != null
                        && p.FindAttachNode("bottom").attachedPart != null;
                if (!isSkirt) continue;
                run.FairingPart = p;
                AttachNode bottom = p.FindAttachNode("bottom");
                run.UpperPart = bottom != null ? bottom.attachedPart : null;
                break;
            }
            run.FairingPid = run.FairingPart != null ? run.FairingPart.persistentId : 0u;
            run.UpperPid = run.UpperPart != null ? run.UpperPart.persistentId : 0u;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: skirt part " +
                $"'{(run.FairingPart != null ? run.FairingPart.partInfo.name : "<none>")}' pid={run.FairingPid.ToString(IC)} " +
                $"below='{(run.UpperPart != null ? run.UpperPart.partInfo.name : "<none>")}' pid={run.UpperPid.ToString(IC)}");
        }

        /// <summary>
        /// The skirt meshes the real part shows against the ones the ghost shows: every name a
        /// ModuleJettison lists plus every ModuleDynamicNodes mount pattern, counted as shown
        /// when the transform is active in the hierarchy and renders something.
        /// </summary>
        private static void CompareSkirt(ConfigRun run, string phase, List<string> failures)
        {
            var names = new List<string>();
            List<ModuleJettison> jets = run.FairingPart.FindModulesImplementing<ModuleJettison>();
            for (int i = 0; i < jets.Count; i++)
                foreach (string n in GhostVisualBuilder.SelectJettisonNames(jets[i].jettisonName, null))
                    if (!names.Contains(n)) names.Add(n);
            List<KeyValuePair<string, bool>> mounts = GhostVisualBuilder.ResolveDynamicNodeMeshStates(
                run.FairingPart.partInfo != null ? run.FairingPart.partInfo.partConfig : null, null);
            for (int i = 0; i < mounts.Count; i++)
                if (!names.Contains(mounts[i].Key)) names.Add(mounts[i].Key);

            Transform ghostPart = run.State != null && run.State.ghost != null
                ? GhostVisualBuilder.FindGhostPartTransform(run.State.ghost, run.FairingPid) : null;
            var real = new List<string>();
            var ghost = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                if (IsShown(run.FairingPart.FindModelTransform(names[i]))) real.Add(names[i]);
                if (ghostPart != null && IsShown(FindDescendant(ghostPart, names[i]))) ghost.Add(names[i]);
            }
            string r = string.Join(",", real.ToArray());
            string g = string.Join(",", ghost.ToArray());
            bool match = r == g;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: skirt {phase} " +
                $"real=[{r}] ghost=[{g}] match={match} candidates={names.Count.ToString(IC)}");
            if (!match)
                failures.Add($"config {run.Config.Number} {run.Config.Name}: {phase} skirt real=[{r}] ghost=[{g}]");
        }

        private static bool IsShown(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != null && rs[i].enabled) return true;
            return false;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // =====================================================================================
        // Vessel authoring
        // =====================================================================================

        private static ConfigNode BuildVesselNode(FairingConfig cfg, Vessel anchor, ConfigRun run, out string failReason)
        {
            failReason = null;
            AvailablePart fairingInfo = PartLoader.getPartInfoByName(cfg.FairingPart);
            if (fairingInfo == null || fairingInfo.partPrefab == null)
            {
                failReason = "no prefab for " + cfg.FairingPart;
                return null;
            }
            Part fairingPrefab = fairingInfo.partPrefab;
            ModuleProceduralFairing fairingModulePrefab = fairingPrefab.FindModuleImplementing<ModuleProceduralFairing>();
            if (fairingModulePrefab == null)
            {
                failReason = cfg.FairingPart + " prefab has no ModuleProceduralFairing";
                return null;
            }
            double baseRadius = fairingModulePrefab.baseRadius;
            double capRadius = fairingModulePrefab.capRadius;

            // Cross-sections: radii scale with the part's base radius; a capped nose ends at
            // the part's capRadius exactly (stock caps only then).
            var heights = new List<double>();
            var radii = new List<double>();
            for (int i = 0; i < cfg.HeightsOverBase.Length; i++)
            {
                heights.Add(cfg.HeightsOverBase[i] * baseRadius);
                bool last = i == cfg.HeightsOverBase.Length - 1;
                radii.Add(last && cfg.Nose == NoseShape.Capped ? capRadius : cfg.RadiiOverBase[i] * baseRadius);
            }
            bool capped = FairingGhostVisualMath.EndsAtCapRadius(radii, capRadius);
            double maxR = 0.0;
            for (int i = 0; i < radii.Count; i++) maxR = Math.Max(maxR, radii[i]);
            run.MaxFairingRadius = maxR;

            string variant = null;
            if (cfg.ColourVariant)
            {
                ModulePartVariants variants = fairingPrefab.FindModuleImplementing<ModulePartVariants>();
                List<string> offered = variants != null ? variants.GetVariantNames() : null;
                string baseName = fairingPrefab.baseVariant != null ? fairingPrefab.baseVariant.Name : null;
                variant = FairingGhostVisualMath.ChooseColourVariant(offered, baseName);
                ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: variants offered=[{(offered != null ? string.Join(",", offered.ToArray()) : "")}] " +
                    $"base={baseName ?? "<none>"} chosen={variant ?? "<none>"}");
                if (variant == null)
                {
                    failReason = cfg.FairingPart + " offers no non-base colour variant";
                    return null;
                }
                run.VariantName = variant;
            }

            // Optional second part: a payload on the top node, or an upper stage on an
            // interstage node.
            AvailablePart childInfo = null;
            string parentNodeId = null;
            if (cfg.PayloadOnTop)
            {
                childInfo = FirstKnownPart(PayloadProbeCandidates);
                parentNodeId = "top";
            }
            else if (cfg.Interstage)
            {
                childInfo = FirstKnownPart(UpperStageCandidates);
                var candidates = new List<KeyValuePair<string, double>>();
                foreach (string id in new[] { "interstage02a", "interstage02b" })
                {
                    AttachNode n = fairingPrefab.FindAttachNode(id);
                    if (n != null)
                        candidates.Add(new KeyValuePair<string, double>(id, n.orientation.y));
                }
                parentNodeId = FairingGhostVisualMath.PickUpwardNode(candidates, "interstage02b");
                var described = new List<string>();
                foreach (var kv in candidates)
                {
                    AttachNode n = fairingPrefab.FindAttachNode(kv.Key);
                    described.Add($"{kv.Key}@pos={FormatVector(n.position)} orient={FormatVector(n.orientation)}");
                }
                ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: interstage nodes [{string.Join("; ", described.ToArray())}] chosen={parentNodeId}");
                if (childInfo == null)
                {
                    failReason = "no upper-stage part known (" + string.Join(",", UpperStageCandidates) + ")";
                    return null;
                }
            }

            var parts = new List<ConfigNode>();
            uint fairingFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            var fairingAtt = new Dictionary<string, int>();
            if (childInfo != null && parentNodeId != null)
                fairingAtt[parentNodeId] = 1;
            ConfigNode fairingNode = AuthorPartNode(fairingInfo, fairingFlightId, 0, Vector3.zero, fairingAtt, variant,
                (module, name) =>
                {
                    if (name == "ModuleProceduralFairing")
                    {
                        module.AddValue("nArcs", "3");
                        module.AddValue("isCapped", capped ? "True" : "False");
                        module.AddValue("interstageCraftID", "0");
                        module.AddValue("fsm", "st_idle");
                        for (int i = 0; i < heights.Count; i++)
                        {
                            ConfigNode x = module.AddNode("XSECTION");
                            x.AddValue("h", heights[i].ToString("R", IC));
                            x.AddValue("r", radii[i].ToString("R", IC));
                        }
                    }
                    else if (name == "ModuleStructuralNodeToggle")
                    {
                        module.AddValue("showMesh", "True");
                        module.AddValue("showNodes", cfg.Interstage ? "True" : "False");
                    }
                    else if (name == "ModuleStructuralNode")
                    {
                        module.AddValue("spawnState", "False");
                        module.AddValue("visibilityState", "True");
                    }
                    else if (name == "ModulePartVariants" && variant != null)
                    {
                        module.AddValue("selectedVariant", variant);
                    }
                });
            parts.Add(fairingNode);

            if (childInfo != null && parentNodeId != null)
            {
                AttachNode parentNode = fairingPrefab.FindAttachNode(parentNodeId);
                AttachNode childBottom = childInfo.partPrefab.FindAttachNode("bottom");
                if (parentNode == null || childBottom == null)
                {
                    failReason = $"attach nodes missing (parent {parentNodeId}={(parentNode != null)}, child bottom={(childBottom != null)})";
                    return null;
                }
                Vector3 childPos = parentNode.position - childBottom.position;
                uint childFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                if (childFlightId == fairingFlightId)
                    childFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                var childAtt = new Dictionary<string, int> { { "bottom", 0 } };
                ConfigNode childNode = AuthorPartNode(childInfo, childFlightId, 0, childPos, childAtt, null, null);
                parts.Add(childNode);
                ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: child part {childInfo.name} on {cfg.FairingPart}.{parentNodeId} " +
                    $"at root-local {FormatVector(childPos)}");
            }

            var sections = new List<string>();
            for (int i = 0; i < heights.Count; i++)
                sections.Add(heights[i].ToString("F3", IC) + "/" + radii[i].ToString("F3", IC));
            ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: authored {cfg.FairingPart} baseRadius={baseRadius.ToString("F3", IC)} " +
                $"capRadius={capRadius.ToString("F3", IC)} isCapped={capped} xsections(h/r)=[{string.Join(" ", sections.ToArray())}] " +
                $"interstage={cfg.Interstage} variant={variant ?? "<base>"} parts={parts.Count.ToString(IC)}");

            Orbit placeholder = new Orbit(
                anchor.orbit.inclination, anchor.orbit.eccentricity, anchor.orbit.semiMajorAxis,
                anchor.orbit.LAN, anchor.orbit.argumentOfPeriapsis, anchor.orbit.meanAnomalyAtEpoch,
                anchor.orbit.epoch, anchor.orbit.referenceBody);
            return ProtoVessel.CreateVesselNode(
                "FGV-" + cfg.Number.ToString(IC) + " " + cfg.Name, VesselType.Ship, placeholder, 0, parts.ToArray());
        }

        /// <summary>
        /// A PART node for <paramref name="info"/> with every prefab module present IN PREFAB
        /// ORDER (so stock's index-based module load never falls back to its name search),
        /// each carrying only its name unless <paramref name="customize"/> adds values.
        /// </summary>
        private static ConfigNode AuthorPartNode(
            AvailablePart info, uint flightId, int parentIndex, Vector3 position,
            Dictionary<string, int> attachments, string variantName, Action<ConfigNode, string> customize)
        {
            ConfigNode p = ProtoVessel.CreatePartNode(info.name, flightId);
            p.SetValue("parent", parentIndex.ToString(IC), true);
            p.SetValue("position", KSPUtil.WriteVector(position), true);
            p.RemoveValues("attN");
            Part prefab = info.partPrefab;
            if (prefab.attachNodes != null)
            {
                for (int i = 0; i < prefab.attachNodes.Count; i++)
                {
                    string id = prefab.attachNodes[i].id;
                    int idx;
                    if (attachments == null || !attachments.TryGetValue(id, out idx))
                        idx = -1;
                    p.AddValue("attN", id + ", " + idx.ToString(IC));
                }
            }
            if (!string.IsNullOrEmpty(variantName))
                p.SetValue("moduleVariantName", variantName, true);
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m == null) continue;
                ConfigNode mn = p.AddNode("MODULE");
                mn.AddValue("name", m.moduleName);
                mn.AddValue("isEnabled", "True");
                customize?.Invoke(mn, m.moduleName);
            }
            return p;
        }

        private static AvailablePart FirstKnownPart(string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                AvailablePart info = PartLoader.getPartInfoByName(candidates[i]);
                if (info != null && info.partPrefab != null)
                    return info;
            }
            return null;
        }

        /// <summary>
        /// Spawns the authored vessel on a clone of the anchor's orbit with the mean anomaly
        /// advanced by the along-track offset, so it rides beside the anchor with near-zero
        /// relative drift for the test's duration (the GrappleCapture co-orbital pattern).
        /// </summary>
        private static uint SpawnCoOrbital(ConfigNode vesselNode, Vessel anchor, double alongTrackMeters, string label)
        {
            Orbit src = anchor.orbit;
            double ut = Planetarium.GetUniversalTime();
            if (!TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift(
                    alongTrackMeters, anchor.obt_speed, src.semiMajorAxis,
                    src.referenceBody.gravParameter, out double dM))
            {
                ParsekLog.Info(Tag, $"spawn {label}: degenerate anchor orbit, no co-orbital shift (pid=0)");
                return 0;
            }
            var orbit = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch + dM, src.epoch, src.referenceBody);
            orbit.Init();
            orbit.UpdateFromUT(ut);
            uint pid = VesselSpawner.SpawnAtPosition(
                vesselNode, src.referenceBody, anchor.latitude, anchor.longitude, anchor.altitude,
                anchor.obt_velocity, ut, terminalState: TerminalState.Orbiting, orbitOverride: orbit);
            ParsekLog.Info(Tag, $"spawn {label}: pid={pid.ToString(IC)} alongTrack={alongTrackMeters.ToString("F0", IC)}m " +
                $"dM={dM.ToString("R", IC)}rad body={src.referenceBody.name}");
            return pid;
        }

        private static void ResolveLiveParts(ConfigRun run)
        {
            run.FairingPart = null;
            run.UpperPart = null;
            for (int i = 0; i < run.Real.parts.Count; i++)
            {
                Part p = run.Real.parts[i];
                if (p == null) continue;
                if (p.FindModuleImplementing<ModuleProceduralFairing>() != null && run.FairingPart == null)
                    run.FairingPart = p;
                else if (run.UpperPart == null && p != run.Real.rootPart)
                    run.UpperPart = p;
            }
            run.FairingPid = run.FairingPart != null ? run.FairingPart.persistentId : 0u;
            run.UpperPid = run.UpperPart != null ? run.UpperPart.persistentId : 0u;
        }

        // =====================================================================================
        // Ghost
        // =====================================================================================

        private static bool BuildGhostBesideReal(ConfigRun run, out string failReason)
        {
            failReason = null;
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(run.Real);
            if (snapshot == null)
            {
                failReason = "TryBackupSnapshot returned null";
                return false;
            }
            double t0 = Planetarium.GetUniversalTime();
            string id = "fgv-" + run.Config.Number.ToString(IC) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = run.Real.vesselName,
                VesselPersistentId = run.Real.persistentId,
                ExplicitStartUT = t0,
                ExplicitEndUT = t0 + 100.0,
                GhostVisualSnapshot = snapshot.CreateCopy(),
                VesselSnapshot = snapshot.CreateCopy(),
            };
            GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, "ParsekTest_fgv_" + id);
            if (build == null || build.root == null)
            {
                failReason = "BuildTimelineGhostFromSnapshot returned no root";
                return false;
            }
            run.Build = build;
            var state = new GhostPlaybackState
            {
                vesselName = rec.VesselName,
                recordingId = rec.RecordingId,
                ghost = build.root,
            };
            state.partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);
            state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);
            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, build, rec);
            run.State = state;

            // Choose the camera side facing the sun (lit faces toward the lens), then put the
            // ghost on the camera's right so every image reads real-left / ghost-right.
            Transform root = run.Real.rootPart.transform;
            Vector3 sideLocal = Vector3.back;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun != null)
            {
                Vector3 toSunLocal = root.InverseTransformDirection((Vector3)(sun.position - root.position).normalized);
                sideLocal = toSunLocal.z >= 0f ? Vector3.forward : Vector3.back;
            }
            run.CameraSideLocal = sideLocal;
            // Camera looks along -sideLocal with up = +Y; its right is cross(up, -side).
            Vector3 rightLocal = Vector3.Cross(Vector3.up, -sideLocal).normalized;
            float offset = (float)FairingGhostVisualMath.GhostSideOffset(run.MaxFairingRadius, GhostGapMeters);

            build.root.transform.SetParent(root, false);
            build.root.transform.localPosition = rightLocal * offset;
            build.root.transform.localRotation = Quaternion.identity;
            build.root.transform.localScale = Vector3.one;
            build.root.SetActive(true);

            // The ghost now hangs under a live physics part. A collider or rigidbody in its
            // hierarchy would join the real part's compound body (or fly off on its own), so
            // neutralise them; neither changes what the camera sees. The counts are logged
            // because a ghost carrying either is itself worth knowing.
            Collider[] colliders = build.root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;
            Rigidbody[] bodies = build.root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].isKinematic = true;
                bodies[i].detectCollisions = false;
            }

            int fairingInfos = state.fairingInfos != null ? state.fairingInfos.Count : 0;
            bool hasFairingInfo = state.fairingInfos != null && state.fairingInfos.ContainsKey(run.FairingPid);
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: ghost built root='{build.root.name}' " +
                $"snapshotParts={snapshot.GetNodes("PART").Length.ToString(IC)} logicalParts={(state.logicalPartIds != null ? state.logicalPartIds.Count : 0).ToString(IC)} " +
                $"fairingInfos={fairingInfos.ToString(IC)} fairingInfoForPid{run.FairingPid.ToString(IC)}={hasFairingInfo} " +
                $"offset={offset.ToString("F2", IC)}m side={FormatVector(rightLocal)} cameraSide={FormatVector(sideLocal)} " +
                $"ghostCollidersDisabled={colliders.Length.ToString(IC)} ghostRigidbodiesFrozen={bodies.Length.ToString(IC)}");
            // A missing FairingGhostInfo keeps the ghost for the images; the jettison
            // applier's outcome reports that gap as a failure.
            return true;
        }

        // =====================================================================================
        // Separation (configuration 2)
        // =====================================================================================

        private IEnumerator SeparateUpperStage(
            ConfigRun run, List<string> captured, List<string> failures)
        {
            FairingConfig cfg = run.Config;
            if (run.UpperPart == null || run.UpperPid == 0)
            {
                failures.Add($"config {cfg.Number} {cfg.Name}: no upper part to separate");
                yield break;
            }

            // Real: stock decouple, then remove the new vessel so the fairing base stands alone.
            Vessel upperVessel = null;
            try
            {
                run.UpperPart.decouple();
                upperVessel = run.UpperPart.vessel;
            }
            catch (Exception ex)
            {
                failures.Add($"config {cfg.Number} {cfg.Name}: stock decouple threw {ex.GetType().Name}: {ex.Message}");
            }
            yield return null;
            ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: real upper part pid={run.UpperPid.ToString(IC)} decoupled " +
                $"newVessel='{(upperVessel != null ? upperVessel.vesselName : "<none>")}' " +
                $"realStructureActiveAfterStockDecouple={CountActiveStructure(run.FairingPart).ToString(IC)}");
            if (upperVessel != null && upperVessel != run.Real)
            {
                try { upperVessel.Die(); }
                catch (Exception ex)
                {
                    ParsekLog.Info(Tag, $"config {cfg.Number} {cfg.Name}: separated upper vessel Die threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            yield return null;

            // Ghost, same recording: the decoupled-subtree path (hide + logical removal).
            // Stock leaves the truss shown, and so does the ghost.
            GhostPlaybackLogic.HidePartSubtree(run.State.ghost, run.UpperPid, run.State.partTree);
            GhostPlaybackLogic.RemovePartSubtreeFromLogicalPresence(
                run.State.logicalPartIds, run.UpperPid, run.State.partTree);
            yield return HoldCamera(run, SeparationSettleSeconds);
            LogGhostStructureState(run, "separated");
            CompareStructure(run, "separated", failures);
            yield return CaptureShot(run, "separated", captured, failures);

            // Ghost, the recording that starts at the separation: rebuilt from a snapshot of
            // the real craft taken now, as the recorder takes it for the new branch.
            if (run.Build != null && run.Build.root != null)
            {
                run.Build.root.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(run.Build.root);
                run.Build.root = null;
            }
            run.Build = null;
            run.State = null;
            if (!BuildGhostBesideReal(run, out string rebuildFail))
            {
                failures.Add($"config {cfg.Number} {cfg.Name}: post-separation ghost not built ({rebuildFail})");
                yield break;
            }
            yield return HoldCamera(run, SeparationSettleSeconds);
            LogGhostStructureState(run, "separated-branch");
            CompareStructure(run, "separated-branch", failures);
            yield return CaptureShot(run, "separated-branch", captured, failures);
        }

        /// <summary>
        /// The truss and cap pieces the real fairing shows against the ones the ghost shows,
        /// by stock rootObject name.
        /// </summary>
        private static void CompareStructure(ConfigRun run, string phase, List<string> failures)
        {
            var real = new List<string>();
            if (run.FairingPart != null)
            {
                List<ModuleStructuralNode> nodes = run.FairingPart.FindModulesImplementing<ModuleStructuralNode>();
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].structTransform != null && nodes[i].structTransform.gameObject.activeSelf)
                        real.Add(nodes[i].rootObject);
            }
            var ghost = new List<string>();
            if (run.State != null && run.State.fairingInfos != null
                && run.State.fairingInfos.TryGetValue(run.FairingPid, out FairingGhostInfo info)
                && info != null && info.structureNodes != null)
            {
                for (int i = 0; i < info.structureNodes.Count; i++)
                {
                    FairingStructureNodeGhostInfo n = info.structureNodes[i];
                    if (n != null && n.structureObject != null && n.structureObject.activeSelf)
                        ghost.Add(n.rootObject);
                }
            }
            real.Sort(StringComparer.Ordinal);
            ghost.Sort(StringComparer.Ordinal);
            string r = string.Join(",", real.ToArray());
            string g = string.Join(",", ghost.ToArray());
            bool match = r == g;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: structure {phase} " +
                $"real=[{r}] ghost=[{g}] match={match}");
            if (!match)
                failures.Add($"config {run.Config.Number} {run.Config.Name}: {phase} structure real=[{r}] ghost=[{g}]");
        }

        // =====================================================================================
        // Camera + capture
        // =====================================================================================

        private static void ComputeFraming(ConfigRun run)
        {
            Transform root = run.Real.rootPart.transform;
            bool any = false;
            Bounds b = new Bounds(root.position, Vector3.zero);
            AccumulateBounds(root, ref b, ref any);
            for (int i = 0; i < run.Real.parts.Count; i++)
            {
                if (run.Real.parts[i] != null)
                    AccumulateBounds(run.Real.parts[i].transform, ref b, ref any);
            }
            if (run.Build != null && run.Build.root != null)
                AccumulateBounds(run.Build.root.transform, ref b, ref any);

            double radius;
            if (any && b.extents.magnitude > 0.2f && b.extents.magnitude < 40f)
            {
                run.CenterLocal = root.InverseTransformPoint(b.center);
                radius = b.extents.magnitude;
            }
            else
            {
                double off = FairingGhostVisualMath.GhostSideOffset(run.MaxFairingRadius, GhostGapMeters);
                Vector3 ghostLocal = run.Build != null && run.Build.root != null
                    ? run.Build.root.transform.localPosition : Vector3.zero;
                run.CenterLocal = ghostLocal * 0.5f + Vector3.up * 1.5f;
                radius = off * 0.5 + run.MaxFairingRadius + 2.0;
            }
            Camera cam = FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null
                ? FlightCamera.fetch.mainCamera : Camera.main;
            double fov = cam != null ? cam.fieldOfView : 60.0;
            double aspect = cam != null ? cam.aspect : 16.0 / 9.0;
            run.CameraDistance = FairingGhostVisualMath.ComputeFramingDistance(radius, fov, aspect, 1.08, 4.0);
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: framing boundsCenterLocal={FormatVector(run.CenterLocal)} " +
                $"radius={radius.ToString("F2", IC)} fov={fov.ToString("F1", IC)} aspect={aspect.ToString("F2", IC)} " +
                $"distance={run.CameraDistance.ToString("F2", IC)} fromRenderers={any}");
        }

        private static void AccumulateBounds(Transform t, ref Bounds b, ref bool any)
        {
            if (t == null) return;
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                Vector3 size = r.bounds.size;
                if (size.magnitude > 30f) continue; // prefab placeholder scales, never real geometry
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }
        }

        private static void PoseCamera(ConfigRun run)
        {
            if (FlightCamera.fetch == null || run.Real == null || run.Real.rootPart == null) return;
            Transform root = run.Real.rootPart.transform;
            Vector3 center = root.TransformPoint(run.CenterLocal);
            Vector3 up = root.up;
            Vector3 side = root.TransformDirection(run.CameraSideLocal);
            double e = run.Config.CameraElevationDeg * Math.PI / 180.0;
            Vector3 dir = (side * (float)Math.Cos(e) + up * (float)Math.Sin(e)).normalized;
            Vector3 camPos = center + dir * (float)run.CameraDistance;
            FlightCamera.fetch.GetCameraTransform().SetPositionAndRotation(
                camPos, Quaternion.LookRotation(center - camPos, up));
        }

        private static IEnumerator HoldCamera(ConfigRun run, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            do
            {
                PoseCamera(run);
                yield return null;
            }
            while (Time.realtimeSinceStartup < end);
        }

        private static IEnumerator WaitRealtime(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        /// <summary>
        /// Writes <c>Screenshots/fgv-&lt;n&gt;-&lt;phase&gt;.png</c> under the KSP root (the
        /// directory run.py harvests) and waits for the file size to settle, using the
        /// CaptureScreenshot seam verb's own naming and two-agreeing-samples poll rule.
        /// A null <paramref name="failures"/> marks the capture optional.
        /// </summary>
        private static IEnumerator CaptureShot(ConfigRun run, string phase, List<string> captured, List<string> failures)
        {
            string label = FairingGhostVisualMath.LabelFor(run.Config.Number, phase);
            if (!TestCommandCaptureScreenshot.IsValidLabel(label))
            {
                failures?.Add($"label '{label}' is not a valid screenshot label");
                yield break;
            }
            string root = KSPUtil.ApplicationRootPath ?? string.Empty;
            string dir = Path.Combine(root, TestCommandCaptureScreenshot.ScreenshotsDirName);
            string path = Path.Combine(dir, TestCommandCaptureScreenshot.FileNameFor(label));
            bool overwrote = false;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path)) { File.Delete(path); overwrote = true; }
            }
            catch (Exception ex)
            {
                failures?.Add($"{label}: screenshot dir/pre-delete failed {ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            bool sunlit = IsSunlit(run);
            PoseCamera(run);
            try
            {
                ScreenCapture.CaptureScreenshot(path, TestCommandCaptureScreenshot.DefaultSuperSize);
            }
            catch (Exception ex)
            {
                failures?.Add($"{label}: ScreenCapture threw {ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            long previous = -1L;
            float deadline = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
            while (true)
            {
                yield return null;
                PoseCamera(run);
                long bytes = -1L;
                bool exists = false;
                try
                {
                    var fi = new FileInfo(path);
                    exists = fi.Exists;
                    bytes = exists ? fi.Length : -1L;
                }
                catch (Exception) { exists = false; bytes = -1L; }
                ScreenshotPollOutcome outcome = TestCommandCaptureScreenshot.DecidePoll(
                    exists, bytes, previous, Time.realtimeSinceStartup >= deadline);
                if (outcome == ScreenshotPollOutcome.Settled)
                {
                    captured.Add(label);
                    ParsekLog.Info(Tag, $"capture ok label={label} path={TestCommandCaptureScreenshot.RelativePathFor(label)} " +
                        $"bytes={bytes.ToString(IC)} overwrote={(overwrote ? "true" : "false")} sunlit={(sunlit ? "true" : "false")}");
                    if (!sunlit)
                        ParsekLog.Info(Tag, $"capture {label}: the pair is in {run.Real.mainBody.name}'s SHADOW; the image will be dark");
                    yield break;
                }
                if (outcome == ScreenshotPollOutcome.TimedOut)
                {
                    ParsekLog.Info(Tag, $"capture FAILED label={label} reason={TestCommandCaptureScreenshot.NotWrittenReason}");
                    failures?.Add($"{label}: screenshot not written within {CaptureTimeoutSeconds}s");
                    yield break;
                }
                previous = bytes;
            }
        }

        private static bool IsSunlit(ConfigRun run)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || run.Real == null || run.Real.mainBody == null || run.Real.mainBody == sun)
                return true;
            Vector3d p = run.Real.GetWorldPos3D();
            Vector3d toBody = run.Real.mainBody.position - p;
            Vector3d toSun = sun.position - p;
            return !FairingGhostVisualMath.IsInBodyShadow(
                toBody.x, toBody.y, toBody.z, toSun.x, toSun.y, toSun.z, run.Real.mainBody.Radius);
        }

        // =====================================================================================
        // Diagnostics + teardown
        // =====================================================================================

        private static int CountActiveStructure(Part fairingPart)
        {
            if (fairingPart == null) return -1;
            int active = 0;
            List<ModuleStructuralNode> nodes = fairingPart.FindModulesImplementing<ModuleStructuralNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].structTransform != null && nodes[i].structTransform.gameObject.activeSelf)
                    active++;
            }
            return active;
        }

        private static void LogRealVesselState(ConfigRun run, string phase)
        {
            if (run.Real == null) return;
            var structure = new List<string>();
            if (run.FairingPart != null)
            {
                List<ModuleStructuralNode> nodes = run.FairingPart.FindModulesImplementing<ModuleStructuralNode>();
                for (int i = 0; i < nodes.Count; i++)
                {
                    ModuleStructuralNode n = nodes[i];
                    bool shown = n.structTransform != null && n.structTransform.gameObject.activeSelf;
                    structure.Add($"{n.rootObject}[{n.attachNodeNames}]={(shown ? "on" : "off")}");
                }
            }
            ModuleProceduralFairing f = run.FairingPart != null
                ? run.FairingPart.FindModuleImplementing<ModuleProceduralFairing>() : null;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: real {phase} vessel='{run.Real.vesselName}' " +
                $"pid={run.Real.persistentId.ToString(IC)} parts={run.Real.parts.Count.ToString(IC)} " +
                $"fairingPid={run.FairingPid.ToString(IC)} upperPid={run.UpperPid.ToString(IC)} " +
                $"panels={(f != null && f.Panels != null ? f.Panels.Count : -1).ToString(IC)} " +
                $"variant={run.VariantName ?? "<base>"} structure=[{string.Join(" ", structure.ToArray())}]");
        }

        private static void LogGhostStructureState(ConfigRun run, string phase)
        {
            if (run.State == null || run.State.ghost == null) return;
            Transform partT = GhostVisualBuilder.FindGhostPartTransform(run.State.ghost, run.FairingPid);
            var on = new List<string>();
            int total = 0;
            if (partT != null)
            {
                Transform[] all = partT.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    string n = all[i].name;
                    if (!(n.StartsWith("Truss", StringComparison.Ordinal) || n.StartsWith("Cap", StringComparison.Ordinal)))
                        continue;
                    total++;
                    if (all[i].gameObject.activeInHierarchy)
                        on.Add(n);
                }
            }
            bool shellActive = false;
            if (run.State.fairingInfos != null
                && run.State.fairingInfos.TryGetValue(run.FairingPid, out FairingGhostInfo info)
                && info != null && info.fairingMeshObject != null)
                shellActive = info.fairingMeshObject.activeInHierarchy;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: ghost {phase} shellActive={shellActive} " +
                $"logicalParts={(run.State.logicalPartIds != null ? run.State.logicalPartIds.Count : 0).ToString(IC)} " +
                $"trussCapTransforms={total.ToString(IC)} activeTrussCap=[{string.Join(",", on.ToArray())}]");
        }

        /// <summary>
        /// The base ring's paint is the one fairing look a picture shows but no structure
        /// line does: compare the main texture stock put on the real part's base renderer
        /// (the child named BaseModelTransformName) with the ghost's.
        /// </summary>
        private static void CompareBaseRingTexture(ConfigRun run, string phase, List<string> failures)
        {
            if (run.FairingPart == null || run.State == null || run.State.ghost == null) return;
            ModuleProceduralFairing f = run.FairingPart.FindModuleImplementing<ModuleProceduralFairing>();
            if (f == null || string.IsNullOrEmpty(f.BaseModelTransformName)) return;
            string realTex = BaseRingTextureName(run.FairingPart.transform, f.BaseModelTransformName);
            Transform partT = GhostVisualBuilder.FindGhostPartTransform(run.State.ghost, run.FairingPid);
            string ghostTex = partT != null ? BaseRingTextureName(partT, f.BaseModelTransformName) : "<no-ghost-part>";
            bool match = realTex == ghostTex;
            ParsekLog.Info(Tag, $"config {run.Config.Number} {run.Config.Name}: base ring {phase} " +
                $"realTexture={realTex} ghostTexture={ghostTex} variant={run.VariantName ?? "<base>"} match={match}");
            // Gated on the colour-variant config only: the base-variant configs log the pair
            // for the record (both read the default texture, possibly through different paths).
            if (!match && run.VariantName != null)
                failures.Add($"config {run.Config.Number} {run.Config.Name}: {phase} base ring texture real={realTex} ghost={ghostTex}");
        }

        private static string BaseRingTextureName(Transform root, string baseName)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name != baseName) continue;
                Renderer r = all[i].GetComponent<Renderer>();
                if (r == null || r.sharedMaterial == null) return "<no-material>";
                Texture t = r.sharedMaterial.mainTexture;
                return t != null ? t.name : "<no-texture>";
            }
            return "<no-base>";
        }

        private static void TearDownConfig(ConfigRun run, string reason)
        {
            if (run == null) return;
            if (run.Build != null && run.Build.root != null)
            {
                run.Build.root.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(run.Build.root);
                run.Build.root = null;
            }
            Vessel v = run.Real ?? (run.SpawnedPid != 0 ? FlightRecorder.FindVesselByPid(run.SpawnedPid) : null);
            if (v != null && v != FlightGlobals.ActiveVessel && v.state != Vessel.State.DEAD)
            {
                try { v.Die(); }
                catch (Exception ex)
                {
                    ParsekLog.Info(Tag, $"teardown config {run.Config.Number}: Die threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            ParsekLog.Info(Tag, $"teardown config {run.Config.Number} {run.Config.Name}: reason={reason} " +
                $"realPid={run.SpawnedPid.ToString(IC)}");
        }

        private static string FormatVector(Vector3 v)
        {
            return "(" + v.x.ToString("F3", IC) + "," + v.y.ToString("F3", IC) + "," + v.z.ToString("F3", IC) + ")";
        }
    }
}
