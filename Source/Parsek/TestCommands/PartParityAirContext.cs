using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>What the canopy capture window decided for one frame (design D.4).</summary>
    internal enum PartParityCanopyStep
    {
        /// <summary>Tilt plus wobble not under the window yet, and the 4 s cap not reached.</summary>
        Wait,
        /// <summary>Tilt plus wobble under the window: capture this frame.</summary>
        Capture,
        /// <summary>The 4 s cap ran out: capture this frame, tagged <c>canopy-wobble:&lt;deg&gt;</c>.</summary>
        CaptureTimedOut,
    }

    /// <summary>
    /// The pure rules of the air context (design-part-ghost-parity.md Appendix D.4, owner
    /// ruling R1): where the chutes spawn over the KSC showcase band, how far the host may
    /// sit from the pad, when the full case raises its deploy altitude, the canopy capture
    /// window, the floor no case may fall under and the angle math the canopy measure uses.
    /// No Unity call: the live half is <see cref="PartParityAirContext"/>.
    /// </summary>
    internal static class PartParityAirPlan
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>The KSC launch pad, the origin of Parsek's synthetic showcase band
        /// (<c>SyntheticRecordingTests.ShowcasePosition</c>).</summary>
        internal const double PadLatitudeDeg = -0.0972;
        internal const double PadLongitudeDeg = -74.5575;

        /// <summary>The showcase band runs north-south this far east of the pad.</summary>
        internal const double BandEastOfPadM = 200.0;

        /// <summary>The distance between two chutes of one wave along the band.</summary>
        internal const double SlotSpacingM = 40.0;

        /// <summary>A case vessel under this height above the terrain is ended before it
        /// can reach the ground (a <c>died-settling</c> setup fault) and destroyed.</summary>
        internal const double AirFloorAglM = 300.0;

        /// <summary>The longest an air wave may run before its remaining cases end as
        /// <c>state-timeout</c> (a guard over the per-case timeouts, never the expected path).</summary>
        internal const double WaveCapSeconds = 120.0;

        /// <summary>Metres north of the band centre for slot <paramref name="index"/> of a wave
        /// of <paramref name="count"/>: centred, <see cref="SlotSpacingM"/> apart.</summary>
        internal static double SlotNorthOffsetM(int index, int count)
        {
            int n = Math.Max(1, count);
            return (index - (n - 1) / 2.0) * SlotSpacingM;
        }

        /// <summary>The latitude / longitude of a wave slot on a body of radius
        /// <paramref name="bodyRadiusM"/>: the pad's latitude plus the slot's north offset,
        /// the pad's longitude plus <see cref="BandEastOfPadM"/> (the band sits on the
        /// equator, so a degree of longitude is a degree of arc).</summary>
        internal static void SlotLatLon(int index, int count, double bodyRadiusM, out double lat, out double lon)
        {
            double metersPerDegree = 2.0 * Math.PI * bodyRadiusM / 360.0;
            lat = PadLatitudeDeg + SlotNorthOffsetM(index, count) / metersPerDegree;
            lon = PadLongitudeDeg + BandEastOfPadM / metersPerDegree;
        }

        /// <summary>The surface (great-circle) distance in metres between two points of a
        /// sphere of radius <paramref name="radiusM"/>; NaN when an input is not finite.</summary>
        internal static double SurfaceDistanceM(double lat1, double lon1, double lat2, double lon2, double radiusM)
        {
            if (!Finite(lat1) || !Finite(lon1) || !Finite(lat2) || !Finite(lon2) || !Finite(radiusM) || radiusM <= 0.0)
                return double.NaN;
            double p1 = lat1 * Math.PI / 180.0, p2 = lat2 * Math.PI / 180.0;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2.0 * radiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        /// <summary>The host's distance to the pad for the air admission (design D.4): the
        /// surface distance on the home body, +infinity on any other body.</summary>
        internal static double DistanceToPadM(bool onHomeBody, double lat, double lon, double bodyRadiusM)
            => onHomeBody ? SurfaceDistanceM(lat, lon, PadLatitudeDeg, PadLongitudeDeg, bodyRadiusM) : double.PositiveInfinity;

        /// <summary>The angle in degrees between two vectors; NaN when either is zero or not
        /// finite.</summary>
        internal static double AngleDeg(double ax, double ay, double az, double bx, double by, double bz)
        {
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (!Finite(la) || !Finite(lb) || la <= 1e-12 || lb <= 1e-12) return double.NaN;
            double c = (ax * bx + ay * by + az * bz) / (la * lb);
            if (c > 1.0) c = 1.0;
            if (c < -1.0) c = -1.0;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>
        /// The canopy capture window (design D.4): capture at the first frame whose airflow
        /// tilt plus wobble is under <see cref="PartParityStateRules.CanopyWindowDeg"/>; once
        /// <see cref="PartParityStateRules.CanopyCapSeconds"/> have passed since the case was
        /// done, capture anyway (tagged); else wait.
        /// </summary>
        internal static PartParityCanopyStep DecideCanopy(double tiltDeg, double wobbleDeg, double sinceDoneSeconds)
        {
            if (PartParityStateRules.CanopyCaptureReady(tiltDeg, wobbleDeg)) return PartParityCanopyStep.Capture;
            if (!double.IsNaN(sinceDoneSeconds) && sinceDoneSeconds >= PartParityStateRules.CanopyCapSeconds)
                return PartParityCanopyStep.CaptureTimedOut;
            return PartParityCanopyStep.Wait;
        }

        /// <summary>The tag of a capture the window's cap forced: <c>canopy-wobble:&lt;tilt +
        /// wobble&gt;</c>, one decimal, <c>nan</c> when the canopy could not be measured.</summary>
        internal static string CanopyWobbleTag(double tiltDeg, double wobbleDeg)
        {
            double sum = Math.Abs(tiltDeg) + Math.Abs(wobbleDeg);
            return PartParityStateRules.TagCanopyWobblePrefix + (Finite(sum) ? sum.ToString("0.0", Ic) : "nan");
        }

        /// <summary>
        /// The full case's second step (design D.4 step 3): once the chute is SEMIDEPLOYED and
        /// its semi animation has finished, the case raises <c>deployAltitude</c> to
        /// <see cref="PartParityStateRules.ChuteDeployAltitudeFull"/> once, the in-flight slider a
        /// player drags. Never for a semi case, never twice.
        /// </summary>
        internal static bool ShouldRaiseForFull(string state, string deploymentState, bool animationPlaying, bool alreadyRaised)
            => state == PartParityStateRules.StateFull && !alreadyRaised && !animationPlaying
               && string.Equals(deploymentState, "SEMIDEPLOYED", StringComparison.Ordinal);

        /// <summary>The full case's state timeout once it raised its deploy altitude: the time
        /// already spent since the press plus <see cref="PartParityStateRules.ChuteFullTimeoutSeconds"/>
        /// (the full wait is measured from the semi done).</summary>
        internal static double FullTimeoutAfterRaise(double secondsSincePress)
            => Math.Max(0.0, secondsSincePress) + PartParityStateRules.ChuteFullTimeoutSeconds;

        /// <summary>The state timeout an air case starts with: semi's for both (the full case
        /// extends it at the raise).</summary>
        internal static double InitialTimeoutSeconds() => PartParityStateRules.ChuteSemiTimeoutSeconds;

        /// <summary>The case fell under the floor (a non-finite height reads as not under).</summary>
        internal static bool BelowFloor(double heightAboveTerrainM)
            => Finite(heightAboveTerrainM) && heightAboveTerrainM < AirFloorAglM;

        /// <summary>The setup detail of a case ended at the floor.</summary>
        internal static string FloorDetail(double heightAboveTerrainM)
            => "fell under the air floor (" + heightAboveTerrainM.ToString("0", Ic) + " m above the terrain, floor "
               + AirFloorAglM.ToString("0", Ic) + " m) before its capture";

        /// <summary>The admission detail of an air run's host refusal.</summary>
        internal static string AdmissionDetail(string situation, double distanceToPadM)
            => "situation=" + (situation ?? "(none)") + " distanceToPad=" +
               (Finite(distanceToPadM) ? distanceToPadM.ToString("0", Ic) + "m" : "inf");

        private static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    }

    /// <summary>
    /// The live air context of the part parity sweep (design-part-ghost-parity.md Appendix D.4,
    /// owner ruling R1): the pad host admission, the showcase-band spawn through
    /// <see cref="VesselSpawner.SpawnAtPosition"/> 1600 m above sea level falling at 10 m/s,
    /// the chute sequence stock runs for real (semi by stock's own Deploy at the authored
    /// <c>deployAltitude</c>, full by the in-flight <c>deployAltitude</c> change), the canopy
    /// capture window, and an air wave's per-frame loop: every case presses, waits, measures
    /// its canopy and is captured (prepare and every view in ONE frame, so the structural
    /// records, the pictures and the measured canopy angles are of the same pose) and
    /// destroyed as soon as it is ready, so no chute falls longer than it must. The pure rules
    /// are <see cref="PartParityAirPlan"/>.
    /// </summary>
    internal static class PartParityAirContext
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Tag => PartParitySweepRunner.Tag;

        // =====================================================================================
        // admission (D.2, D.4)
        // =====================================================================================

        /// <summary>
        /// The host admission of a run of either kind, the one rule the runner and the seam
        /// applier both call: <see cref="TestCommandPartParitySweep.DecideHostAdmission(PartParitySweepArgs, bool, bool, bool, double)"/>
        /// over the active vessel's situation and its distance to the pad. Null when admitted,
        /// else the reason, with <paramref name="detail"/> naming the situation (and, for an
        /// air run, the distance).
        /// </summary>
        internal static string DecideAdmission(PartParitySweepArgs args, Vessel active, out string detail)
        {
            bool has = active != null;
            bool orbiting = has && active.situation == Vessel.Situations.ORBITING && active.orbit != null;
            bool landed = has && (active.situation == Vessel.Situations.PRELAUNCH || active.situation == Vessel.Situations.LANDED);
            double distance = double.PositiveInfinity;
            if (has && active.mainBody != null)
            {
                CelestialBody home = FlightGlobals.GetHomeBody();
                distance = PartParityAirPlan.DistanceToPadM(home != null && active.mainBody == home,
                    active.latitude, active.longitude, active.mainBody.Radius);
            }
            string refusal = TestCommandPartParitySweep.DecideHostAdmission(args, has, orbiting, landed, distance);
            string situation = has ? active.situation.ToString() : "(none)";
            detail = TestCommandPartParitySweep.IsAirRun(args)
                ? PartParityAirPlan.AdmissionDetail(situation, distance)
                : (has ? "situation=" + situation : "no active vessel");
            if (TestCommandPartParitySweep.IsAirRun(args))
                ParsekLog.Info(Tag, $"air admission: host={(has ? active.vesselName : "(none)")} {detail} " +
                    $"maxPadDistance={TestCommandPartParitySweep.AirHostMaxPadDistanceM.ToString("0", Ic)}m " +
                    $"result={refusal ?? "admitted"}");
            return refusal;
        }

        // =====================================================================================
        // spawn (D.4)
        // =====================================================================================

        /// <summary>
        /// Authors and spawns one air case at slot <paramref name="slot"/> of a wave of
        /// <paramref name="count"/>: a state case's vessel (the subject and its MODULE nodes,
        /// the stowed pose) or a stowed control's, every <c>ModuleParachute</c> of it with
        /// <c>deployAltitude</c> = <see cref="PartParityStateRules.ChuteDeployAltitudeSemi"/>,
        /// placed 1600 m above sea level over the showcase band, upright, falling at 10 m/s.
        /// </summary>
        internal static void SpawnCase(StepContext ctx, WaveCase wc, int slot, int count)
        {
            if (wc.Entry == null || wc.Entry.Info == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? "part not loaded";
                return;
            }
            ConfigNode node;
            if (wc.State != null)
            {
                node = PartParityStateDriver.AuthorVesselNode(wc, ctx.Host, out string fail);
                if (node == null)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = wc.Detail ?? fail ?? "state vessel node not authored";
                    return;
                }
            }
            else
            {
                node = PartParityCaseAuthoring.AuthorVesselNode(
                    wc.Entry.Info, wc.RealVariant, wc.SkinOption, wc.CaseId, ctx.Host, out bool _);
            }
            int chutes = AuthorChuteAltitude(node, PartParityStateRules.ChuteDeployAltitudeSemi);

            CelestialBody body = ctx.Host != null ? ctx.Host.mainBody : FlightGlobals.GetHomeBody();
            PartParityAirPlan.SlotLatLon(slot, count, body.Radius, out double lat, out double lon);
            double alt = PartParityStateRules.AirSpawnAltitudeM;
            // Through VesselSpawner.SpawnAtPosition, upright, falling at 10 m/s (the rig holds the
            // spawn call beside its co-orbital twin).
            wc.Pid = GhostCoOrbitalRig.SpawnInAir(node, body, lat, lon, alt, PartParityStateRules.AirSpawnDescentMps,
                wc.CaseId, Tag);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            double padDistance = PartParityAirPlan.SurfaceDistanceM(lat, lon, PartParityAirPlan.PadLatitudeDeg,
                PartParityAirPlan.PadLongitudeDeg, body.Radius);
            ParsekLog.Info(Tag, $"air spawn {wc.CaseId}: pid={wc.Pid.ToString(Ic)} slot={slot.ToString(Ic)}/{count.ToString(Ic)} " +
                $"lat={lat.ToString("0.00000", Ic)} lon={lon.ToString("0.00000", Ic)} alt={alt.ToString("0", Ic)}m " +
                $"descent={PartParityStateRules.AirSpawnDescentMps.ToString("0", Ic)}m/s padDistance={padDistance.ToString("0", Ic)}m " +
                $"chutes={chutes.ToString(Ic)} deployAltitude={PartParityStateRules.ChuteDeployAltitudeSemi.ToString("0", Ic)} body={body.name}");
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        /// <summary>Writes <c>deployAltitude</c> into every <c>ModuleParachute</c> MODULE node of
        /// the vessel node (the n-th MODULE node of a PART is its prefab's n-th module, the
        /// authoring contract of <c>GhostCoOrbitalRig.AuthorPartNode</c>). Returns how many.</summary>
        private static int AuthorChuteAltitude(ConfigNode vesselNode, double deployAltitude)
        {
            int n = 0;
            foreach (ConfigNode p in vesselNode.GetNodes("PART"))
            {
                string partName = p.GetValue("name");
                AvailablePart info = partName != null ? PartLoader.getPartInfoByName(partName) : null;
                Part prefab = info?.partPrefab;
                ConfigNode[] modules = p.GetNodes("MODULE");
                if (prefab == null || prefab.Modules == null || modules.Length != prefab.Modules.Count) continue;
                for (int i = 0; i < modules.Length; i++)
                {
                    if (!(prefab.Modules[i] is ModuleParachute)) continue;
                    modules[i].SetValue("deployAltitude", deployAltitude.ToString("R", Ic), true);
                    n++;
                }
            }
            return n;
        }

        // =====================================================================================
        // the air wave (D.4)
        // =====================================================================================

        /// <summary>
        /// One air wave, start to teardown: spawn every case over the band, wait for each to
        /// load and settle, take the state cases' stowed inputs and press, then every frame
        /// (at the end of the frame, after physics and animation) step each state case, end a
        /// case that fell under the floor, and capture each case the moment it is ready (a
        /// stowed control at once; the real-vs-real control armed first; a state case at its
        /// canopy window), writing its sidecar and destroying its vessel at once.
        /// </summary>
        internal static IEnumerator RunWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            bool solo = wave.Count == 1 && wave[0].Solo;
            PartParitySweepRunner.WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            for (int i = 0; i < wave.Count; i++)
            {
                WaveCase wc = wave[i];
                int slot = i;
                PartParitySweepRunner.Guard(ctx, wc, "spawn", () => SpawnCase(ctx, wc, slot, wave.Count));
            }
            IEnumerator ready = PartParitySweepRunner.WaitReady(ctx, wave);
            try { while (ready.MoveNext()) yield return ready.Current; }
            finally { PartParitySweepRunner.DisposeNested(ready); }
            if (ctx.HostDamage == null)
            {
                IEnumerator settle = PartParitySweepRunner.WaitSettled(ctx, wave);
                try { while (settle.MoveNext()) yield return settle.Current; }
                finally { PartParitySweepRunner.DisposeNested(settle); }
            }

            var finished = new HashSet<WaveCase>();
            if (ctx.HostDamage == null)
            {
                foreach (var wc in wave)
                {
                    if (!Healthy(wc)) continue;
                    PartParitySweepRunner.Guard(ctx, wc, "state-inputs", () =>
                    {
                        PartParityStateDriver.CaptureStowedInputs(wc);
                        wc.State.TimeoutSeconds = PartParityAirPlan.InitialTimeoutSeconds();
                    });
                }
                foreach (var wc in wave)
                {
                    if (!Healthy(wc)) continue;
                    PartParitySweepRunner.Guard(ctx, wc, "state-press", () => PartParityStateDriver.Press(wc));
                }
                float waveStart = Time.realtimeSinceStartup;
                while (true)
                {
                    yield return new WaitForEndOfFrame();
                    if (PartParitySweepRunner.CheckHost(ctx, "air wave")) break;
                    float now = Time.realtimeSinceStartup;
                    bool capped = now - waveStart > PartParityAirPlan.WaveCapSeconds;
                    bool pending = false;
                    foreach (var wc in wave)
                    {
                        if (finished.Contains(wc)) continue;
                        if (ctx.HostDamage != null) break;
                        if (AdvanceCase(ctx, wc, now, capped, solo))
                        {
                            finished.Add(wc);
                            WriteRemainingInflight(ctx, wave, finished, solo);
                        }
                        else pending = true;
                    }
                    if (!pending || ctx.HostDamage != null) break;
                }
            }
            if (ctx.HostDamage != null)
            {
                PartParitySweepRunner.DeleteInflight(ctx);
                ParsekLog.Info(Tag, $"air wave stopped by the host damage: {(wave.Count - finished.Count).ToString(Ic)} of " +
                    $"{wave.Count.ToString(Ic)} case(s) left unrun (no sidecar); tearing the wave down");
            }
            int reached = 0, faults = 0;
            foreach (var wc in wave)
            {
                if (wc.State != null && wc.State.Reached) reached++;
                if (wc.Status != null) faults++;
            }
            ParsekLog.Info(Tag, $"air wave: cases={wave.Count.ToString(Ic)} finished={finished.Count.ToString(Ic)} " +
                $"reached={reached.ToString(Ic)} faults={faults.ToString(Ic)}");
            IEnumerator down = PartParitySweepRunner.TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { PartParitySweepRunner.DisposeNested(down); }
            ctx.Live = null;
        }

        private static bool Healthy(WaveCase wc)
            => wc.State != null && wc.Status == null && wc.Real != null && wc.Real.rootPart != null;

        /// <summary>One frame of one case; true once the case is finished (its sidecar written
        /// and its vessel destroyed).</summary>
        private static bool AdvanceCase(StepContext ctx, WaveCase wc, float now, bool capped, bool solo)
        {
            if (wc.Status == null && wc.Real != null && PartParitySweepRunner.CaseVesselLost(wc))
            {
                wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                wc.Detail = wc.Detail ?? "vessel lost in the air";
            }
            if (wc.Status == null && wc.Real != null)
            {
                double agl = HeightAboveTerrain(wc.Real);
                if (PartParityAirPlan.BelowFloor(agl))
                {
                    wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                    wc.Detail = wc.Detail ?? PartParityAirPlan.FloorDetail(agl);
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                }
            }
            if (wc.Status == null && capped)
            {
                wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                wc.Detail = wc.Detail ?? "air wave cap of " + PartParityAirPlan.WaveCapSeconds.ToString("0", Ic) + " s reached";
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
            }
            if (wc.Status != null || wc.Real == null)
            {
                Finish(ctx, wc, solo, capture: false);
                return true;
            }
            PartParityStateRun run = wc.State;
            if (run == null)
            {
                // A stowed control: the real-vs-real one is armed first (stock's Deploy moves it
                // STOWED -> ACTIVE at once; it opens on a later physics frame), then captured.
                if (wc.ControlId == TestCommandPartParitySweep.ControlRealVsReal)
                    PartParitySweepRunner.Guard(ctx, wc, "arm", () => ArmChute(wc));
                Finish(ctx, wc, solo, capture: true);
                return true;
            }
            if (!run.Done)
            {
                PartParitySweepRunner.Guard(ctx, wc, "state-wait", () => PartParityStateDriver.Step(wc));
                if (wc.Status != null) { Finish(ctx, wc, solo, capture: false); return true; }
                if (!run.Done) return false;
                run.CanopyDoneRealtime = now;
            }
            double tilt = double.NaN, wobble = double.NaN;
            PartParitySweepRunner.Guard(ctx, wc, "canopy", () => MeasureCanopy(wc, out tilt, out wobble));
            if (wc.Status != null) { Finish(ctx, wc, solo, capture: false); return true; }
            PartParityCanopyStep step = PartParityAirPlan.DecideCanopy(tilt, wobble, now - run.CanopyDoneRealtime);
            if (step == PartParityCanopyStep.Wait) return false;
            // The chute must still be in its state at the capture (a semi chute stock opened
            // fully on the way down would photograph the wrong state).
            PartParityStateObservation o = PartParityStateDriver.Observe(wc.Real.rootPart, run.Option, out string f, out string val);
            if (run.Reached && run.Option.Action != null && !PartParityStateRules.StateReached(run.Option, o))
            {
                wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                wc.Detail = wc.Detail ?? "left " + run.Option.State + " before its capture (" + f + "=" + val + ")";
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                Finish(ctx, wc, solo, capture: false);
                return true;
            }
            run.CanopyTiltDeg = Finite(tilt) ? (double?)tilt : null;
            run.CanopyWobbleDeg = Finite(wobble) ? (double?)wobble : null;
            if (step == PartParityCanopyStep.CaptureTimedOut) run.CanopyWobbleTag = PartParityAirPlan.CanopyWobbleTag(tilt, wobble);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: canopy {(step == PartParityCanopyStep.Capture ? "in its window" : "window timed out")} " +
                $"tilt={Fmt(tilt)}deg wobble={Fmt(wobble)}deg window={PartParityStateRules.CanopyWindowDeg.ToString("0.0", Ic)}deg " +
                $"after={(now - run.CanopyDoneRealtime).ToString("0.00", Ic)}s transform={run.CanopyTransform ?? "<none>"} " +
                $"agl={HeightAboveTerrain(wc.Real).ToString("0", Ic)}m");
            Finish(ctx, wc, solo, capture: true);
            return true;
        }

        /// <summary>Captures (when asked and the case is healthy), writes the sidecar and
        /// destroys the case, all in this frame.</summary>
        private static void Finish(StepContext ctx, WaveCase wc, bool solo, bool capture)
        {
            if (capture && wc.Status == null)
            {
                PartParitySweepRunner.WriteInflight(ctx, new List<string> { wc.CaseId }, solo);
                PartParitySweepRunner.BeginCaseLog(ctx, wc);
                try
                {
                    PartParitySweepRunner.Guard(ctx, wc, "prepare", () => PartParityCaseCapture.Prepare(ctx, wc));
                    if (wc.Status == null && wc.Shared != null)
                        foreach (string view in wc.Views)
                        {
                            string v = view;
                            PartParitySweepRunner.Guard(ctx, wc, "capture-" + v, () => PartParityCaseCapture.CaptureView(ctx, wc, v));
                        }
                }
                finally
                {
                    PartParitySweepRunner.EndCaseLog(ctx);
                }
            }
            PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
            PartParitySweepRunner.DestroyCase(wc);
            PartParitySweepRunner.CheckHost(ctx, "after case " + wc.CaseId);
        }

        private static void WriteRemainingInflight(StepContext ctx, List<WaveCase> wave, HashSet<WaveCase> finished, bool solo)
        {
            var left = new List<string>();
            foreach (var wc in wave) if (!finished.Contains(wc)) left.Add(wc.CaseId);
            if (left.Count == 0) PartParitySweepRunner.DeleteInflight(ctx);
            else PartParitySweepRunner.WriteInflight(ctx, left, solo);
        }

        // =====================================================================================
        // the chute (D.4)
        // =====================================================================================

        /// <summary>The air hook of a state case's per-frame wait (called by
        /// <see cref="PartParityStateDriver.Step"/>): the full case raises
        /// <c>deployAltitude</c> to 5000 once its chute is SEMIDEPLOYED and still, the slider
        /// a player drags in flight, and its timeout restarts at the full wait.</summary>
        internal static void StepFull(WaveCase wc, PartParityStateRun run, Part part, float now, bool animating)
        {
            if (run == null || !run.Air || run.Option == null) return;
            ModuleParachute chute = ChuteOf(part, run.Option);
            if (chute == null) return;
            string ds = chute.deploymentState.ToString();
            if (!PartParityAirPlan.ShouldRaiseForFull(run.Option.State, ds, animating, run.FullRaised)) return;
            float before = chute.deployAltitude;
            chute.deployAltitude = (float)PartParityStateRules.ChuteDeployAltitudeFull;
            run.FullRaised = true;
            double since = now - run.PressRealtime;
            run.SemiDoneSeconds = since;
            run.TimeoutSeconds = PartParityAirPlan.FullTimeoutAfterRaise(since);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: semi done after {since.ToString("0.00", Ic)}s; deployAltitude " +
                $"{before.ToString("0", Ic)} -> {chute.deployAltitude.ToString("0", Ic)} for the full canopy " +
                $"(timeout now {run.TimeoutSeconds.ToString("0.0", Ic)}s from the press) agl={HeightAboveTerrain(wc.Real).ToString("0", Ic)}m");
        }

        /// <summary>Arms the real-vs-real control's chute (stock's own Deploy event).</summary>
        private static void ArmChute(WaveCase wc)
        {
            Part part = wc.Real.rootPart;
            ModuleParachute chute = part.FindModuleImplementing<ModuleParachute>();
            if (chute == null)
            {
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: no ModuleParachute to arm");
                return;
            }
            BaseEvent e = chute.Events["Deploy"];
            if (e != null) e.Invoke();
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: armed {part.partInfo?.name} deploymentState={chute.deploymentState}");
        }

        private static ModuleParachute ChuteOf(Part part, PartParityStateOption opt)
        {
            if (part == null) return null;
            if (opt != null && opt.ModuleIndex >= 0 && opt.ModuleIndex < part.Modules.Count
                && part.Modules[opt.ModuleIndex] is ModuleParachute m)
                return m;
            return part.FindModuleImplementing<ModuleParachute>();
        }

        /// <summary>
        /// The canopy's airflow tilt and wobble this frame (design D.4): stock aims the canopy
        /// at <c>LookRotation(+-dragVectorDir, up)</c> and then wobbles it about all three axes, so
        /// the un-wobbled aim is <c>(invertCanopy ? -1 : 1) * dragVectorDir</c>; the tilt is the
        /// angle between the part's +Y and that aim, the wobble the whole rotation between the
        /// canopy and stock's un-wobbled pose <c>LookRotation(aim, up)</c> (its roll about the aim
        /// included: the noise turns the canopy about its own axis too, which a ghost canopy
        /// posed at the un-wobbled aim shows as a rotation difference). <c>up</c> is stock's:
        /// the part's +Z, or for a symmetry group <c>vessel.CurrentCoM - part.transform.localPosition</c>.
        /// NaN when the part has no drag direction yet or no canopy transform.
        /// </summary>
        private static void MeasureCanopy(WaveCase wc, out double tilt, out double wobble)
        {
            tilt = double.NaN;
            wobble = double.NaN;
            Part part = wc.Real.rootPart;
            ModuleParachute chute = ChuteOf(part, wc.State.Option);
            if (chute == null) return;
            Transform canopy = string.IsNullOrEmpty(chute.canopyName) ? null : part.FindModelTransform(chute.canopyName);
            wc.State.CanopyTransform = canopy != null ? chute.canopyName : null;
            Vector3 drag = part.dragVectorDir;
            if (canopy == null || drag == Vector3.zero) return;
            Vector3 aim = (chute.invertCanopy ? -1f : 1f) * drag;
            Vector3 up = part.transform.up;
            tilt = PartParityAirPlan.AngleDeg(up.x, up.y, up.z, aim.x, aim.y, aim.z);
            Vector3 upwards = part.transform.forward;
            if (chute.symmetryCount > 0 && part.vessel != null)
                upwards = Vector3.Normalize(part.vessel.CurrentCoM - part.transform.localPosition);
            wobble = Quaternion.Angle(canopy.rotation, Quaternion.LookRotation(aim, upwards));
        }

        private static double HeightAboveTerrain(Vessel v)
        {
            if (v == null) return double.NaN;
            double h = v.radarAltitude;
            return double.IsNaN(h) || double.IsInfinity(h) ? v.altitude - Math.Max(0.0, v.terrainAltitude) : h;
        }

        private static string Fmt(double d) => Finite(d) ? d.ToString("0.00", Ic) : "-";

        private static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    }
}
