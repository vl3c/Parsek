using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S11
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.
    //
    // GhostCostSweep: what N ghosts cost per frame in flight and in map view. The save is
    // pad-runway-pair with the spike-s11-ghosts injection: 64 ghost-only recordings of a
    // small lit rocket circling the pad vessel 40 to 110 m out and 30 to 100 m above it
    // (layout in SpikeS11Layout), all inside the 10 km full-fidelity zone, framed by a
    // flight camera 300 m from the pad, pitched 0.35 rad down. Each step turns on the
    // first N recordings through RecordingStore.SetRecordingPlaybackEnabled (the playback
    // toggle the player uses; a disabled recording's ghost is destroyed), waits until the
    // engine has built exactly those N ghosts, settles, then measures a window of frames
    // in flight view and again in map view. Nothing here uses the Watch camera.
    //
    // Frame time is Time.unscaledDeltaTime (the whole frame, game plus render plus vsync
    // wait); engine time is GhostPlaybackEngine's own per-frame stopwatch total
    // (DiagnosticsState.playbackBudget.totalMicroseconds), written by UpdatePlayback in
    // ParsekFlight.Update, i.e. before this coroutine resumes in the same frame.
    //
    // Every Unity and KSP read sits in a [NoInlining] core below; the pure half (plan,
    // statistics, fit, formatting, first-open) is SpikeS11Plan.cs / SpikeS11FirstOpen.cs.
    public class SpikeS11Cost
    {
        private const float CameraPitchRadians = 0.35f;
        private const float CameraDistanceMeters = 300f;
        private const int ShotWaitFrames = 3;
        private const int MapExitSettleFrames = 60;

        private static void Info(string msg) => ParsekLog.Info(SpikeS11Plan.Tag, msg);
        private static void Warn(string msg) => ParsekLog.Warn(SpikeS11Plan.Tag, msg);

        private static void Csv(string name, string[] header, params object[] fields)
        {
            try { SpikeCsv.AppendRow(SpikeS11Plan.SpikeId, name, header, fields); }
            catch (Exception ex) { Warn("csv write failed name=" + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }

        [InGameTest(Category = "SpikeS11", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false, RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "R0 spike S11: needs PARSEK_SPIKE=S11 and the spike-s11-ghosts save; run isolated",
            Description = "R0 spike S11: frame cost of 0/8/16/32/64 ghosts in flight and map view, plus a sidecar first-open")]
        public IEnumerator GhostCostSweep()
        {
            if (!SpikeHost.IsArmed(SpikeS11Plan.SpikeId))
                InGameAssert.Skip("PARSEK_SPIKE does not name S11");
            if (!FlightReady())
                InGameAssert.Skip("needs a FLIGHT scene with ParsekFlight, its engine and an active vessel");

            string passNote, framesNote;
            SpikeS11Pass pass = SpikeS11Plan.ParsePass(Environment.GetEnvironmentVariable(SpikeS11Plan.EnvPass), out passNote);
            int frames = SpikeS11Plan.ParseFrames(Environment.GetEnvironmentVariable(SpikeS11Plan.EnvFrames), out framesNote);
            bool shots = SpikeS11Plan.ParseShots(Environment.GetEnvironmentVariable(SpikeS11Plan.EnvShots));
            if (passNote != null) Warn(passNote);
            if (framesNote != null) Warn(framesNote);
            string run = SpikeS11Plan.RunLabel(pass, DateTime.UtcNow);

            // Engine index == position in the committed list (ParsekFlightPlaybackHost
            // builds the trajectory list from it in order).
            int[] engineIndex = FindSpikeRecordings(out int found);
            if (found != SpikeS11Plan.MaxGhosts)
                InGameAssert.Skip("found " + found + " of " + SpikeS11Plan.MaxGhosts
                    + " spike recordings (" + SpikeS11Plan.RecordingIdPrefix + "NN); inject the spike-s11-ghosts preset");

            Info("sweep start run=" + run + " pass=" + SpikeS11Plan.PassToken(pass)
                + " frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + " counts=" + string.Join(",", Array.ConvertAll(SpikeS11Plan.GhostCounts, n => n.ToString(CultureInfo.InvariantCulture)))
                + " spikeRecordings=" + found.ToString(CultureInfo.InvariantCulture)
                + " shots=" + (shots ? "on" : "off"));
            LogSettings(run, pass);

            var saved = CaptureRestoreState();
            var steps = new List<SpikeS11StepResult>();
            var invalid = new List<string>();
            try
            {
                EnsureRealtime();
                if (pass == SpikeS11Pass.Uncapped) Uncap();
                PlaceCamera();
                Info("camera pitch=" + ReadCamPitch().ToString("F3", CultureInfo.InvariantCulture)
                    + " distance=" + ReadCamDistance().ToString("F1", CultureInfo.InvariantCulture)
                    + " ringSlantRangeM=" + SpikeS11Layout.SlantRangeMeters(0).ToString("F0", CultureInfo.InvariantCulture)
                    + ".." + SpikeS11Layout.SlantRangeMeters(SpikeS11Plan.MaxGhosts - 1).ToString("F0", CultureInfo.InvariantCulture));

                // First open: before any step, so no window carries it.
                RunFirstOpen(run);
                yield return null;

                double[] frameMs = new double[frames];
                double[] engineUs = new double[frames];

                foreach (int n in SpikeS11Plan.GhostCounts)
                {
                    // --- build exactly N ghosts ---
                    for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                        SetPlayback(engineIndex[i], SpikeS11Plan.EnabledInStep(i, n));

                    int waited = 0;
                    long spawnUs = 0, spawnMaxUs = 0;
                    int built = 0, leftover = 0;
                    bool timedOut = false;
                    while (true)
                    {
                        yield return null;
                        waited++;
                        long us = ReadEngineSpawnUs();
                        spawnUs += us;
                        if (us > spawnMaxUs) spawnMaxUs = us;
                        CountBuilt(engineIndex, n, out built, out _, out leftover);
                        if (built == n && leftover == 0) break;
                        if (waited >= SpikeS11Plan.SpawnWaitMaxFrames) { timedOut = true; break; }
                    }
                    for (int s = 0; s < SpikeS11Plan.SettleFrames; s++) yield return null;

                    foreach (SpikeS11View view in new[] { SpikeS11View.Flight, SpikeS11View.Map })
                    {
                        if (view == SpikeS11View.Map)
                        {
                            SetMapView(true);
                            for (int s = 0; s < SpikeS11Plan.SettleFrames; s++) yield return null;
                        }

                        // --- measured window: no logging, no allocation, no file I/O ---
                        int gen0Before = GC.CollectionCount(0);
                        long memBefore = GC.GetTotalMemory(false);
                        var wall = Stopwatch.StartNew();
                        for (int f = 0; f < frames; f++)
                        {
                            yield return null;
                            frameMs[f] = ReadFrameMs();
                            engineUs[f] = ReadEngineTotalUs();
                        }
                        wall.Stop();
                        int gen0 = GC.CollectionCount(0) - gen0Before;
                        long memDeltaKb = (GC.GetTotalMemory(false) - memBefore) / 1024;

                        var r = new SpikeS11StepResult
                        {
                            Run = run, Pass = pass, View = view, Requested = n,
                            SpawnWaitFrames = waited, SpawnMicroseconds = spawnUs,
                            SpawnMaxFrameMicroseconds = spawnMaxUs, SpawnWaitTimedOut = timedOut,
                            FrameMs = SpikeS11Stats.Summarize(frameMs, frames),
                            EngineUs = SpikeS11Stats.Summarize(engineUs, frames),
                            Gen0Collections = gen0, ManagedDeltaKb = memDeltaKb,
                            MeanFps = wall.Elapsed.TotalSeconds > 0 ? frames / wall.Elapsed.TotalSeconds : double.NaN,
                        };
                        int active;
                        CountBuilt(engineIndex, n, out r.Built, out active, out r.Leftover);
                        r.Active = active;
                        r.OnScreen = view == SpikeS11View.Flight ? CountInCameraView(engineIndex, n) : -1;
                        ReadObservability(out r.Zone1, out r.Zone2, out r.EngineFxGhosts, out r.EngineParticleSystems);
                        r.InvalidReason = SpikeS11Plan.JudgeStep(r, frames);
                        steps.Add(r);
                        if (r.InvalidReason != null)
                            invalid.Add(SpikeS11Plan.ViewToken(view) + " n=" + n + ": " + r.InvalidReason);

                        Info(SpikeS11Format.StepLine(r));
                        Csv("steps", SpikeS11Format.StepHeader, SpikeS11Format.StepRow(r));
                        WriteFrames(run, pass, view, n, frameMs, engineUs, frames);

                        if (shots)
                        {
                            string shot = TakeShot(SpikeS11Plan.ShotFileName(run, view, n));
                            for (int s = 0; s < ShotWaitFrames; s++) yield return null;
                            Info("shot run=" + run + " view=" + SpikeS11Plan.ViewToken(view) + " n=" + n
                                + " file=" + (shot ?? "<failed>") + " exists=" + (shot != null && File.Exists(shot) ? "true" : "false"));
                        }

                        if (view == SpikeS11View.Map)
                        {
                            SetMapView(false);
                            for (int s = 0; s < MapExitSettleFrames; s++) yield return null;
                        }
                    }
                }

                foreach (SpikeS11View view in new[] { SpikeS11View.Flight, SpikeS11View.Map })
                {
                    foreach (var kv in SpikeS11Format.FitAll(steps, view))
                    {
                        Info(SpikeS11Format.FitLine(run, pass, view, kv.Key, kv.Value));
                        Csv("fit", SpikeS11Format.FitHeader, SpikeS11Format.FitRow(run, pass, view, kv.Key, kv.Value));
                    }
                }
            }
            finally
            {
                Restore(saved, engineIndex);
            }

            Info("sweep done run=" + run + " steps=" + steps.Count.ToString(CultureInfo.InvariantCulture)
                + " invalid=" + invalid.Count.ToString(CultureInfo.InvariantCulture)
                + " csvRoot=" + SafeSpikeRoot());
            if (invalid.Count > 0)
                InGameAssert.Fail("S11 windows that do not count: " + string.Join("; ", invalid.ToArray()));
        }

        // ---------------------------------------------------------------- first open

        private static void RunFirstOpen(string run)
        {
            string folder = Path.Combine(ReadSaveDir(), SpikeS11Plan.ColdFolderName);
            string prec = SpikeS11FirstOpen.FindLargestTrajectory(folder);
            if (prec == null)
            {
                Warn("firstopen skipped: no .prec in " + folder);
                Csv("firstopen", SpikeS11FirstOpen.CsvHeader, run, 0, "trajectory", "", 0, double.NaN, double.NaN, double.NaN, 0, false, "no .prec in " + folder);
                return;
            }
            string craft = SpikeS11FirstOpen.GhostSnapshotFor(prec);
            // attempt 1 is the first time this process opens these files; 2..N are warm.
            for (int attempt = 1; attempt <= 1 + SpikeS11Plan.WarmOpenRepeats; attempt++)
            {
                var t = SpikeS11FirstOpen.OpenTrajectory(prec);
                var s = craft != null ? SpikeS11FirstOpen.OpenSnapshot(craft) : default(SpikeS11OpenResult);
                var b = SpikeS11FirstOpen.ReadBytes(prec);
                Info(SpikeS11FirstOpen.Line(attempt, t));
                Csv("firstopen", SpikeS11FirstOpen.CsvHeader, SpikeS11FirstOpen.Row(run, attempt, t));
                if (craft != null)
                {
                    Info(SpikeS11FirstOpen.Line(attempt, s));
                    Csv("firstopen", SpikeS11FirstOpen.CsvHeader, SpikeS11FirstOpen.Row(run, attempt, s));
                }
                Info(SpikeS11FirstOpen.Line(attempt, b));
                Csv("firstopen", SpikeS11FirstOpen.CsvHeader, SpikeS11FirstOpen.Row(run, attempt, b));
            }
        }

        // ---------------------------------------------------------------- frames CSV

        private static void WriteFrames(string run, SpikeS11Pass pass, SpikeS11View view, int n,
            double[] frameMs, double[] engineUs, int count)
        {
            try
            {
                using (var w = SpikeCsv.Open(SpikeS11Plan.SpikeId, "frames", SpikeS11Format.FrameHeader))
                {
                    string p = SpikeS11Plan.PassToken(pass), v = SpikeS11Plan.ViewToken(view);
                    for (int f = 0; f < count; f++)
                        w.WriteRow(run, p, v, n, f, frameMs[f], engineUs[f]);
                }
            }
            catch (Exception ex)
            {
                Warn("csv write failed name=frames: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- settings record

        private static void LogSettings(string run, SpikeS11Pass pass)
        {
            var kv = new List<KeyValuePair<string, string>>();
            try { ReadSettingsCore(kv); }
            catch (Exception ex) { kv.Add(new KeyValuePair<string, string>("readError", ex.GetType().Name + ": " + ex.Message)); }
            var line = new System.Text.StringBuilder("settings run=" + run + " pass=" + SpikeS11Plan.PassToken(pass));
            foreach (var p in kv)
            {
                line.Append(' ').Append(p.Key).Append('=').Append(p.Value.Replace(' ', '_'));
                Csv("settings", new[] { "run", "pass", "key", "value" }, run, SpikeS11Plan.PassToken(pass), p.Key, p.Value);
            }
            Info(line.ToString());
        }

        private static string S(object o) => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadSettingsCore(List<KeyValuePair<string, string>> kv)
        {
            Action<string, object> add = (k, v) => kv.Add(new KeyValuePair<string, string>(k, S(v)));
            add("screen", Screen.width + "x" + Screen.height);
            add("fullScreen", Screen.fullScreen);
            add("qualityLevel", QualitySettings.GetQualityLevel());
            add("vSyncCount", QualitySettings.vSyncCount);
            add("targetFrameRate", Application.targetFrameRate);
            add("antiAliasing", QualitySettings.antiAliasing);
            add("ksp.FRAMERATE_LIMIT", GameSettings.FRAMERATE_LIMIT);
            add("ksp.SYNC_VBL", GameSettings.SYNC_VBL);
            add("ksp.SCREEN_RESOLUTION", GameSettings.SCREEN_RESOLUTION_WIDTH + "x" + GameSettings.SCREEN_RESOLUTION_HEIGHT);
            add("ksp.FULLSCREEN", GameSettings.FULLSCREEN);
            add("ksp.QUALITY_PRESET", GameSettings.QUALITY_PRESET);
            add("ksp.ANTI_ALIASING", GameSettings.ANTI_ALIASING);
            add("ksp.TEXTURE_QUALITY", GameSettings.TEXTURE_QUALITY);
            add("ksp.TERRAIN_SHADER_QUALITY", GameSettings.TERRAIN_SHADER_QUALITY);
            add("ksp.UI_SCALE", GameSettings.UI_SCALE);
            add("ksp.PHYSICS_FRAME_DT_LIMIT", GameSettings.PHYSICS_FRAME_DT_LIMIT);
            add("ksp.MAX_VESSELS_BUDGET", GameSettings.MAX_VESSELS_BUDGET);
            add("gpu", SystemInfo.graphicsDeviceName);
            add("gpuApi", SystemInfo.graphicsDeviceType);
            add("gpuMemoryMb", SystemInfo.graphicsMemorySize);
            add("cpu", SystemInfo.processorType);
            add("cpuCount", SystemInfo.processorCount);
            add("ramMb", SystemInfo.systemMemorySize);
            add("os", SystemInfo.operatingSystem);
            add("unity", Application.unityVersion);
            add("parsek.verboseLogging", ParsekSettings.Current != null ? (object)ParsekSettings.Current.verboseLogging : "<no settings>");
            add("activeVessel", FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "<none>");
            add("ut", Planetarium.GetUniversalTime());
        }

        // ---------------------------------------------------------------- live cores

        private sealed class RestoreState
        {
            internal int VSync;
            internal int TargetFrameRate;
            internal bool HaveCamera;
            internal float CamPitch;
            internal float CamDistance;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool FlightReady()
            => ParsekFlight.Instance != null && ParsekFlight.Instance.Engine != null && FlightGlobals.ActiveVessel != null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int[] FindSpikeRecordings(out int found)
        {
            var map = new int[SpikeS11Plan.MaxGhosts];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            found = 0;
            var committed = RecordingStore.CommittedRecordings;
            for (int idx = 0; idx < committed.Count; idx++)
            {
                int g = SpikeS11Plan.IndexOfRecordingId(committed[idx]?.RecordingId);
                if (g < 0 || map[g] >= 0) continue;
                map[g] = idx;
                found++;
            }
            return map;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetPlayback(int committedIndex, bool enabled)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committedIndex < 0 || committedIndex >= committed.Count) return;
            RecordingStore.SetRecordingPlaybackEnabled(committed[committedIndex], enabled);
        }

        /// <summary>built / active: spike ghosts i &lt; n with a ghost object (active: activeSelf); leftover: i &gt;= n still built.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CountBuilt(int[] engineIndex, int n, out int built, out int active, out int leftover)
        {
            built = active = leftover = 0;
            var engine = ParsekFlight.Instance != null ? ParsekFlight.Instance.Engine : null;
            if (engine == null) return;
            for (int i = 0; i < engineIndex.Length; i++)
            {
                GhostPlaybackState st;
                if (engineIndex[i] < 0 || !engine.TryGetGhostState(engineIndex[i], out st)) continue;
                if (!GhostPlaybackEngine.HasLoadedGhostVisuals(st)) continue;
                if (i < n)
                {
                    built++;
                    if (st.ghost.activeSelf) active++;
                }
                else leftover++;
            }
        }

        /// <summary>Built spike ghosts i &lt; n whose root is in front of the flight camera and inside its viewport.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CountInCameraView(int[] engineIndex, int n)
        {
            var engine = ParsekFlight.Instance != null ? ParsekFlight.Instance.Engine : null;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (engine == null || cam == null) return 0;
            int count = 0;
            for (int i = 0; i < n && i < engineIndex.Length; i++)
            {
                GhostPlaybackState st;
                if (engineIndex[i] < 0 || !engine.TryGetGhostState(engineIndex[i], out st)) continue;
                if (!GhostPlaybackEngine.HasLoadedGhostVisuals(st) || !st.ghost.activeInHierarchy) continue;
                Vector3 vp = cam.WorldToViewportPoint(st.ghost.transform.position);
                if (vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f) count++;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadFrameMs() => Time.unscaledDeltaTime * 1000.0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadEngineTotalUs() => DiagnosticsState.playbackBudget.totalMicroseconds;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long ReadEngineSpawnUs() => DiagnosticsState.playbackBudget.spawnMicroseconds;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadObservability(out int zone1, out int zone2, out int fxGhosts, out int fxSystems)
        {
            GhostObservability o = DiagnosticsState.playbackBudget.ghostObservability;
            zone1 = o.zone1GhostCount;
            zone2 = o.zone2GhostCount;
            fxGhosts = o.ghostsWithEngineFx;
            fxSystems = o.engineParticleSystemCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static RestoreState CaptureRestoreState()
        {
            var s = new RestoreState
            {
                VSync = QualitySettings.vSyncCount,
                TargetFrameRate = Application.targetFrameRate,
            };
            if (FlightCamera.fetch != null)
            {
                s.HaveCamera = true;
                s.CamPitch = FlightCamera.CamPitch;
                s.CamDistance = FlightCamera.fetch.Distance;
            }
            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureRealtime()
        {
            if (TimeWarp.CurrentRateIndex != 0)
            {
                Info("warp index " + TimeWarp.CurrentRateIndex + " -> 0 before the sweep");
                TimeWarp.SetRate(0, true);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Uncap()
        {
            int vs = QualitySettings.vSyncCount, tf = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Info("uncapped: vSyncCount " + vs + " -> 0, targetFrameRate " + tf + " -> -1 (restored after the sweep)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PlaceCamera()
        {
            var cam = FlightCamera.fetch;
            if (cam == null) { Warn("no FlightCamera.fetch; camera left as is"); return; }
            float d = Mathf.Clamp(CameraDistanceMeters, cam.minDistance, cam.maxDistance);
            FlightCamera.CamPitch = CameraPitchRadians;
            cam.SetDistanceImmediate(d);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadCamPitch() => FlightCamera.fetch != null ? FlightCamera.CamPitch : float.NaN;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadCamDistance() => FlightCamera.fetch != null ? FlightCamera.fetch.Distance : float.NaN;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetMapView(bool on)
        {
            if (on && !MapView.MapIsEnabled) MapView.EnterMapView();
            else if (!on && MapView.MapIsEnabled) MapView.ExitMapView();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadSaveDir()
            => Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath ?? "", "saves"), HighLogic.SaveFolder ?? "");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string TakeShot(string fileName)
        {
            try
            {
                string dir = Path.Combine(SpikeCsv.ResolveSpikeRoot(), Path.Combine(SpikeS11Plan.SpikeId, "shots"));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path)) File.Delete(path);
                ScreenCapture.CaptureScreenshot(path);
                return path;
            }
            catch (Exception ex)
            {
                Warn("shot failed file=" + fileName + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SafeSpikeRoot()
        {
            try { return SpikeCsv.ResolveSpikeRoot(); }
            catch (Exception ex) { return "<unresolved: " + ex.Message + ">"; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Restore(RestoreState s, int[] engineIndex)
        {
            int reenabled = 0;
            try
            {
                if (engineIndex != null)
                    for (int i = 0; i < engineIndex.Length; i++)
                    {
                        var committed = RecordingStore.CommittedRecordings;
                        int idx = engineIndex[i];
                        if (idx >= 0 && idx < committed.Count && RecordingStore.SetRecordingPlaybackEnabled(committed[idx], true))
                            reenabled++;
                    }
            }
            catch (Exception ex) { Warn("restore playback failed: " + ex.GetType().Name + ": " + ex.Message); }
            try { if (MapView.MapIsEnabled) MapView.ExitMapView(); }
            catch (Exception ex) { Warn("restore map view failed: " + ex.GetType().Name + ": " + ex.Message); }
            try
            {
                QualitySettings.vSyncCount = s.VSync;
                Application.targetFrameRate = s.TargetFrameRate;
                if (s.HaveCamera && FlightCamera.fetch != null)
                {
                    FlightCamera.CamPitch = s.CamPitch;
                    FlightCamera.fetch.SetDistanceImmediate(s.CamDistance);
                }
            }
            catch (Exception ex) { Warn("restore camera / frame cap failed: " + ex.GetType().Name + ": " + ex.Message); }
            Info("restored playbackReenabled=" + reenabled + " vSyncCount=" + s.VSync + " targetFrameRate=" + s.TargetFrameRate
                + " camPitch=" + s.CamPitch.ToString("F3", CultureInfo.InvariantCulture)
                + " camDistance=" + s.CamDistance.ToString("F1", CultureInfo.InvariantCulture));
        }
    }
}
