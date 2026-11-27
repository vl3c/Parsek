using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Proof = Parsek.TestCommands.TestCommandFarWatchProof;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Live half of the THROWAWAY far-watch step 1 proof (docs/dev/design-far-watch.md section
    /// 18.1). Never merged. Created by the first <c>FarWatchProof</c> seam step (automation only)
    /// and destroyed with the flight scene; its Harmony patches are applied by hand on arm and
    /// removed on destroy, so nothing here exists in a normal game.
    /// <para>The driver owns one camera drone, follows one ghost by orbit rewrite while the
    /// drone is the active vessel, and writes one <c>[FarWatchProof] obs=...</c> line per
    /// observation. Each seam op runs as one enumerator pumped from <see cref="Update"/>; the
    /// seam applier polls <see cref="IsOpDone"/>.</para>
    /// </summary>
    internal sealed partial class FarWatchProofDriver : MonoBehaviour
    {
        internal static FarWatchProofDriver Instance;

        private const string Tag = Proof.Tag;
        private const string HarmonyId = "com.parsek.farwatchproof";
        private const double SummaryPeriodSeconds = 5.0;

        private Harmony harmony;
        private bool armed;

        // Op pump.
        private IEnumerator op;
        private bool opDone = true;
        private string opResult;
        private List<KeyValuePair<string, string>> opPayload;
        private Proof.Args opArgs;
        private double opStartReal;

        // Event sequence.
        private int eventSeq;
        private int eventMarkSeq;
        private readonly List<string> eventsSinceMark = new List<string>();

        // Log listener counters (stock literals; counted, never logged from the handler).
        private int pressureKillLines;
        private int terrainKillLines;
        private int autosaveLines;
        private int exceptionLines;

        // Window counters, reset by every summary line.
        private int winTicks, winWrites, winMiss, winFloorClamps, winHoldRenewals, winPackedTicks;
        private int winShipUnpackedTicks, winOriginShifts;
        private double winWriteMsMax, winWriteMsSum, winFrameMsMax, winDevMax, winJitterMax;
        private double winSpeedDiffMax, winMinAboveTerrain = double.PositiveInfinity;
        private double nextSummaryReal;

        // Whole-run counters.
        internal static int ResetContractsCalls;
        internal static int ResetContractsFenced;
        internal static bool FenceOn;
        internal static bool StripOn = true;
        internal static uint DronePidForPatches;
        internal static Guid ShipGuidForPatches;
        internal static int GoOffRailsRefused;
        internal static int StripApplied;
        private string lastDiscoveryLevel;

        internal static FarWatchProofDriver Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("Parsek_FarWatchProofDriver");
            Instance = go.AddComponent<FarWatchProofDriver>();
            return Instance;
        }

        internal bool IsOpDone => opDone;
        internal string OpResult => opResult ?? "ok";
        internal List<KeyValuePair<string, string>> OpPayload => opPayload;

        /// <summary>Starts one op. Returns null, or the reason it could not start.</summary>
        internal string Begin(Proof.Args a)
        {
            if (!opDone) return "busy";
            opArgs = a;
            opResult = null;
            opPayload = new List<KeyValuePair<string, string>>();
            opStartReal = Time.realtimeSinceStartup;
            ArmCore();
            WrapEngineHooks();
            op = RunOp(a);
            opDone = false;
            ParsekLog.Info(Tag, "op start op=" + Proof.OpToken(a.Op) + " ut=" + Proof.F(Planetarium.GetUniversalTime()));
            return null;
        }

        /// <summary>Abandons a running op (the seam's two-phase clear).</summary>
        internal void Abort(string why)
        {
            if (opDone) return;
            Finish("aborted-" + why);
        }

        private IEnumerator RunOp(Proof.Args a)
        {
            switch (a.Op)
            {
                case FarWatchProofOp.Arm: return OpArm();
                case FarWatchProofOp.Build: return OpBuild(a);
                case FarWatchProofOp.Switch: return OpSwitch(a);
                case FarWatchProofOp.Follow: return OpFollow(a);
                case FarWatchProofOp.MoveTo: return OpMoveTo(a);
                case FarWatchProofOp.Advance: return OpAdvance(a);
                case FarWatchProofOp.Solver: return OpSolver(a);
                case FarWatchProofOp.Park: return OpPark(a);
                case FarWatchProofOp.Destroy: return OpDestroy(a);
                case FarWatchProofOp.Save: return OpSave(a);
                case FarWatchProofOp.Autosave: return OpAutosave(a);
                case FarWatchProofOp.Lock: return OpLock(a);
                case FarWatchProofOp.Fence: return OpFence(a);
                case FarWatchProofOp.Control: return OpControl(a);
                case FarWatchProofOp.Unlist: return OpUnlist(a);
                case FarWatchProofOp.Kill: return OpKill(a);
                case FarWatchProofOp.Recalc: return OpRecalc(a);
                default: return OpReport(a);
            }
        }

        private void Finish(string result, params string[] kv)
        {
            opResult = result ?? "ok";
            if (opPayload == null) opPayload = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                opPayload.Add(new KeyValuePair<string, string>(kv[i], Proof.Sanitize(kv[i + 1])));
            opDone = true;
            op = null;
            ParsekLog.Info(Tag, "op done op=" + Proof.OpToken(opArgs.Op) + " result=" + opResult
                + " real=" + Proof.F(Time.realtimeSinceStartup - opStartReal, "F1"));
        }

        private bool TimedOut(double defaultSeconds)
        {
            double box = double.IsNaN(opArgs.Seconds) ? defaultSeconds : opArgs.Seconds;
            return Proof.OpTimedOut(opStartReal, Time.realtimeSinceStartup, box);
        }

        /// <summary>One observation line: <c>obs=name k=v ...</c> from alternating pairs.</summary>
        internal static void Obs(string name, params string[] kv)
        {
            var pairs = new List<KeyValuePair<string, string>>(kv.Length / 2 + 1);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                pairs.Add(new KeyValuePair<string, string>(kv[i], kv[i + 1]));
            ParsekLog.Info(Tag, Proof.FormatObs(name, pairs));
        }

        private static string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string U(uint n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string F(double v) => Proof.F(v);
        private static string B(bool b) => Proof.B(b);
        private static string Ut() => Proof.F(Planetarium.GetUniversalTime());
        private static string Name(Vessel v) => v != null ? v.vesselName : "none";

        // ----- Lifecycle -----

        private void Update()
        {
            try
            {
                PumpOp();
                WatchDiscoveryLevel();
                double fms = Time.unscaledDeltaTime * 1000.0;
                if (fms > winFrameMsMax) winFrameMsMax = fms;
                if (armed && Time.realtimeSinceStartup >= nextSummaryReal)
                {
                    nextSummaryReal = Time.realtimeSinceStartup + SummaryPeriodSeconds;
                    WriteSummary();
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "update threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void PumpOp()
        {
            if (opDone || op == null) return;
            try
            {
                if (!op.MoveNext() && !opDone)
                    Finish("ok");
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "op threw op=" + Proof.OpToken(opArgs.Op) + " " + ex.GetType().Name
                    + ": " + ex.Message + "\n" + ex.StackTrace);
                Finish("threw-" + ex.GetType().Name);
            }
        }

        private void FixedUpdate()
        {
            try
            {
                TickHold();
                TickFollow();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "fixed tick threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void LateUpdate()
        {
            try
            {
                MeasureCamera();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "late tick threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            ParsekLog.Info(Tag, "driver destroyed scene=" + HighLogic.LoadedScene + " drone=" + Name(drone));
            UnwrapEngineHooks();
            UnsubscribeEvents();
            Application.logMessageReceived -= OnLogMessage;
            try { InputLockManager.RemoveControlLock(LockId); } catch (Exception) { }
            if (harmony != null)
            {
                try { harmony.UnpatchAll(HarmonyId); } catch (Exception) { }
                harmony = null;
            }
            DronePidForPatches = 0u;
            FenceOn = false;
            StripOn = true;
            if (Instance == this) Instance = null;
        }

        // ----- Arm -----

        // Every op arms first, so a driver recreated after a scene reload still observes.
        private void ArmCore()
        {
            if (armed) return;
            armed = true;
            InstallPatches();
            SubscribeEvents();
            Application.logMessageReceived += OnLogMessage;
            StripOn = true;
        }

        private IEnumerator OpArm()
        {
            ArmCore();
            WrapEngineHooks();
            Vessel active = FlightGlobals.ActiveVessel;
            ship = ship ?? active;
            if (ship != null) ShipGuidForPatches = ship.id;
            CareerSnapshot(out double funds, out double sci, out double rep, out int contracts);
            Obs("arm", "ut", Ut(), "active", Name(active), "sit", active != null ? active.situation.ToString() : "-",
                "body", active != null && active.mainBody != null ? active.mainBody.bodyName : "-",
                "parsekFlight", B(ParsekFlight.Instance != null), "hooks", B(hooksWrapped),
                "patches", N(patchedCount), "funds", F(funds), "sci", F(sci), "rep", F(rep),
                "contracts", N(contracts), "warpRates", WarpRatesText());
            Finish("ok", "active", Name(active), "hooks", B(hooksWrapped), "patches", N(patchedCount));
            yield break;
        }

        private static string WarpRatesText()
        {
            if (TimeWarp.fetch == null || TimeWarp.fetch.warpRates == null) return "-";
            return string.Join(",", Array.ConvertAll(TimeWarp.fetch.warpRates, r => r.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        internal static void CareerSnapshot(out double funds, out double sci, out double rep, out int contracts)
        {
            funds = Funding.Instance != null ? Funding.Instance.Funds : double.NaN;
            sci = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : double.NaN;
            rep = Reputation.Instance != null ? Reputation.Instance.reputation : double.NaN;
            contracts = Contracts.ContractSystem.Instance != null && Contracts.ContractSystem.Instance.Contracts != null
                ? Contracts.ContractSystem.Instance.Contracts.Count : -1;
        }

        // ----- Events -----

        private void SubscribeEvents()
        {
            GameEvents.onVesselGoOnRails.Add(OnGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnGoOffRails);
            GameEvents.onVesselSwitching.Add(OnSwitching);
            GameEvents.onVesselSwitchingToUnloaded.Add(OnSwitchingToUnloaded);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.OnFlightGlobalsReady.Add(OnFlightGlobalsReady);
            GameEvents.onDominantBodyChange.Add(OnDominantBodyChange);
            GameEvents.onVesselSOIChanged.Add(OnSoiChanged);
            GameEvents.onVesselLoaded.Add(OnLoaded);
            GameEvents.onVesselUnloaded.Add(OnUnloaded);
            GameEvents.onVesselWillDestroy.Add(OnWillDestroy);
            GameEvents.onVesselCreate.Add(OnCreate);
            GameEvents.onCrash.Add(OnCrash);
            GameEvents.OnProgressReached.Add(OnProgressReached);
            GameEvents.OnProgressComplete.Add(OnProgressComplete);
            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
        }

        private void UnsubscribeEvents()
        {
            if (!armed) return;
            GameEvents.onVesselGoOnRails.Remove(OnGoOnRails);
            GameEvents.onVesselGoOffRails.Remove(OnGoOffRails);
            GameEvents.onVesselSwitching.Remove(OnSwitching);
            GameEvents.onVesselSwitchingToUnloaded.Remove(OnSwitchingToUnloaded);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.OnFlightGlobalsReady.Remove(OnFlightGlobalsReady);
            GameEvents.onDominantBodyChange.Remove(OnDominantBodyChange);
            GameEvents.onVesselSOIChanged.Remove(OnSoiChanged);
            GameEvents.onVesselLoaded.Remove(OnLoaded);
            GameEvents.onVesselUnloaded.Remove(OnUnloaded);
            GameEvents.onVesselWillDestroy.Remove(OnWillDestroy);
            GameEvents.onVesselCreate.Remove(OnCreate);
            GameEvents.onCrash.Remove(OnCrash);
            GameEvents.OnProgressReached.Remove(OnProgressReached);
            GameEvents.OnProgressComplete.Remove(OnProgressComplete);
            GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
            GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
        }

        private string Role(Vessel v)
        {
            if (v == null) return "none";
            if (drone != null && v == drone) return "drone";
            if (ship != null && v == ship) return "ship";
            if (control != null && v == control) return "control";
            return "other";
        }

        private void Ev(string name, Vessel v, string extra = null)
        {
            eventSeq++;
            string token = name + ":" + Role(v);
            eventsSinceMark.Add(token);
            Obs("event", "seq", N(eventSeq), "name", name, "role", Role(v), "vessel", Name(v),
                "active", Role(FlightGlobals.ActiveVessel), "ut", Ut(), "frame", N(Time.frameCount),
                "extra", extra ?? "-");
        }

        private void MarkEvents()
        {
            eventMarkSeq = eventSeq;
            eventsSinceMark.Clear();
        }

        private string EventsSinceMark() => eventsSinceMark.Count == 0 ? "-" : string.Join(">", eventsSinceMark.ToArray());

        private void OnGoOnRails(Vessel v) => Ev("onVesselGoOnRails", v);
        private void OnGoOffRails(Vessel v) => Ev("onVesselGoOffRails", v);
        private void OnSwitching(Vessel from, Vessel to) => Ev("onVesselSwitching", to, "from=" + Role(from));
        private void OnSwitchingToUnloaded(Vessel from, Vessel to) => Ev("onVesselSwitchingToUnloaded", to, "from=" + Role(from));
        private void OnVesselChange(Vessel v) => Ev("onVesselChange", v);
        private void OnFlightGlobalsReady(bool ready) => Ev("OnFlightGlobalsReady", null, "ready=" + B(ready));
        private void OnDominantBodyChange(GameEvents.FromToAction<CelestialBody, CelestialBody> a)
            => Ev("onDominantBodyChange", FlightGlobals.ActiveVessel,
                (a.from != null ? a.from.bodyName : "-") + "->" + (a.to != null ? a.to.bodyName : "-"));
        private void OnSoiChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> a)
            => Ev("onVesselSOIChanged", a.host,
                (a.from != null ? a.from.bodyName : "-") + "->" + (a.to != null ? a.to.bodyName : "-"));
        private void OnLoaded(Vessel v)
        {
            Ev("onVesselLoaded", v);
            if (v != null && (v == drone || v == control)) MakeDroneInert(v, "onVesselLoaded");
        }
        private void OnUnloaded(Vessel v) => Ev("onVesselUnloaded", v);
        private void OnWillDestroy(Vessel v) => Ev("onVesselWillDestroy", v);
        private void OnCreate(Vessel v) => Ev("onVesselCreate", v);
        private void OnCrash(EventReport r) => Ev("onCrash", r != null && r.origin != null ? r.origin.vessel : null);
        private void OnProgressReached(ProgressNode n) => Ev("OnProgressReached", FlightGlobals.ActiveVessel, n != null ? n.Id : "-");
        private void OnProgressComplete(ProgressNode n) => Ev("OnProgressComplete", FlightGlobals.ActiveVessel, n != null ? n.Id : "-");
        private void OnGameStateSaved(Game g) => Ev("onGameStateSaved", FlightGlobals.ActiveVessel);

        private void OnOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            winOriginShifts++;
            if (unlistActive && drone != null)
            {
                Obs("unlist-shift", "ut", Ut(), "frame", N(Time.frameCount),
                    "droneToOrigin", F(drone.transform.position.magnitude),
                    "offset", F(offset.magnitude), "inList", B(FlightGlobals.Vessels.Contains(drone)));
            }
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (condition == null) return;
            if (condition.IndexOf("kPa pressure and was destroyed", StringComparison.Ordinal) >= 0) pressureKillLines++;
            else if (condition.IndexOf("crashed through terrain", StringComparison.Ordinal) >= 0) terrainKillLines++;
            else if (condition.StartsWith("[AutoSave]", StringComparison.Ordinal)) autosaveLines++;
            if (type == LogType.Exception) exceptionLines++;
        }

        // ----- Owned watch (D Q3) -----

        private void WatchDiscoveryLevel()
        {
            if (drone == null || drone.DiscoveryInfo == null) return;
            string level = drone.DiscoveryInfo.Level.ToString();
            if (level == lastDiscoveryLevel) return;
            Obs("discovery", "ut", Ut(), "frame", N(Time.frameCount), "from", lastDiscoveryLevel ?? "-",
                "to", level, "owned", B(drone.DiscoveryInfo.Level == DiscoveryLevels.Owned));
            lastDiscoveryLevel = level;
        }

        // ----- The periodic summary -----

        private void WriteSummary()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            CelestialBody body = active != null ? active.mainBody : null;
            bool pqsActive = body != null && body.pqsController != null && body.pqsController.isActive;
            double writeAvg = winWrites > 0 ? winWriteMsSum / winWrites : double.NaN;
            Obs("tick", "ut", Ut(), "warp", F(TimeWarp.CurrentRate), "active", Role(active),
                "following", followIndex >= 0 ? N(followIndex) : "-", "ticks", N(winTicks), "writes", N(winWrites),
                "miss", N(winMiss), "writeMsAvg", F(writeAvg), "writeMsMax", F(winWriteMsMax),
                "frameMsMax", F(winFrameMsMax), "devMaxM", F(winDevMax), "jitterMaxM", F(winJitterMax),
                "gridM", F(lastGridStep), "speedDiffMax", F(winSpeedDiffMax), "floorClamps", N(winFloorClamps),
                "minAboveTerrainM", F(winMinAboveTerrain), "holdRenewals", N(winHoldRenewals),
                "dronePackedTicks", N(winPackedTicks), "shipUnpackedTicks", N(winShipUnpackedTicks),
                "originShifts", N(winOriginShifts), "solver", drone != null ? B(drone.patchedConicSolver != null) : "-",
                "body", body != null ? body.bodyName : "-", "pqsActive", B(pqsActive),
                "ghostBody", lastGhostBody ?? "-", "terrainUnderGhost", F(lastTerrainUnderGhost),
                "ghostAlt", F(lastGhostAlt), "droneSit", drone != null ? drone.situation.ToString() : "-",
                "droneGee", drone != null ? F(drone.geeForce) : "-", "dronePacked", drone != null ? B(drone.packed) : "-",
                "shipLoaded", ship != null ? B(ship.loaded) : "-", "shipPacked", ship != null ? B(ship.packed) : "-",
                "shipSit", ship != null ? ship.situation.ToString() : "-",
                "resetContracts", N(ResetContractsCalls), "fenced", N(ResetContractsFenced),
                "pressureKills", N(pressureKillLines), "terrainKills", N(terrainKillLines),
                "goOffRailsRefused", N(GoOffRailsRefused), "exceptions", N(exceptionLines));
            winTicks = winWrites = winMiss = winFloorClamps = winHoldRenewals = winPackedTicks = 0;
            winShipUnpackedTicks = winOriginShifts = 0;
            winWriteMsMax = winWriteMsSum = winFrameMsMax = winDevMax = winJitterMax = winSpeedDiffMax = 0;
            winMinAboveTerrain = double.PositiveInfinity;
        }

        private static double ElapsedMs(Stopwatch sw) => sw.Elapsed.TotalMilliseconds;
    }
}
