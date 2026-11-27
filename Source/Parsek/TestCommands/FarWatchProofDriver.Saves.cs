using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Proof = Parsek.TestCommands.TestCommandFarWatchProof;

namespace Parsek.TestCommands
{
    // Saves while the drone exists (proofs 7, 8), the contract fence (proof 12), the negative
    // control (proof 10), the ledger belt (proof 13) and the report line.
    internal sealed partial class FarWatchProofDriver
    {
        private static string SavePath(string name)
            => Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"),
                Path.Combine(HighLogic.SaveFolder ?? "", name + ".sfs"));

        private static DateTime SaveMtime(string name)
        {
            try
            {
                string p = SavePath(name);
                return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        // Reads one save back: is the drone in it, and which vessel is active.
        private void ParseSave(string name, out bool droneInSave, out string activeName, out bool activeIsShip, out int vessels)
        {
            droneInSave = false;
            activeName = "-";
            activeIsShip = false;
            vessels = -1;
            ConfigNode root = ConfigNode.Load(SavePath(name));
            ConfigNode game = root != null ? (root.GetNode("GAME") ?? root) : null;
            ConfigNode fs = game != null ? game.GetNode("FLIGHTSTATE") : null;
            if (fs == null) return;
            ConfigNode[] vs = fs.GetNodes("VESSEL");
            vessels = vs.Length;
            int idx = -1;
            int.TryParse(fs.GetValue("activeVessel"), out idx);
            string shipPid = ship != null ? ship.id.ToString("N") : null;
            for (int i = 0; i < vs.Length; i++)
            {
                string vname = vs[i].GetValue("name");
                if (vname == DroneName || vs[i].GetValue("persistentId") == U(DronePidForPatches)) droneInSave = true;
                if (i == idx)
                {
                    activeName = vname ?? "-";
                    string pid = vs[i].GetValue("pid");
                    activeIsShip = shipPid != null && pid != null && string.Equals(pid.Replace("-", ""), shipPid, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // ----- Save (proof 7: the strip on any save) -----

        private IEnumerator OpSave(Proof.Args a)
        {
            string name = string.IsNullOrEmpty(a.Name) ? "farwatchproof" : a.Name;
            bool stripBefore = StripOn;
            StripOn = a.Strip;
            int strips0 = StripApplied;
            string wrote;
            try
            {
                wrote = GamePersistence.SaveGame(name, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            finally
            {
                StripOn = stripBefore;
            }
            yield return null;
            ParseSave(name, out bool droneIn, out string activeName, out bool activeIsShip, out int vessels);
            Obs("save", "ut", Ut(), "name", name, "strip", B(a.Strip), "wrote", B(!string.IsNullOrEmpty(wrote)),
                "stripRan", B(StripApplied > strips0), "droneExists", B(drone != null), "droneInSave", B(droneIn),
                "active", activeName, "activeIsShip", B(activeIsShip), "vessels", N(vessels),
                "liveActive", Role(FlightGlobals.ActiveVessel), "clearToSave", FlightGlobals.ClearToSave(false).ToString());
            Finish("ok", "droneInSave", B(droneIn), "activeIsShip", B(activeIsShip));
        }

        // ----- Autosave (proof 7 and the FlightAutoSave.Start restart) -----

        private IEnumerator OpAutosave(Proof.Args a)
        {
            FlightAutoSave fas = FlightAutoSave.fetch;
            if (fas == null) { Finish("no-autosave"); yield break; }
            float interval = double.IsNaN(a.Interval) ? 5f : (float)a.Interval;
            float oldLong = GameSettings.AUTOSAVE_INTERVAL;
            float oldShort = GameSettings.AUTOSAVE_SHORT_INTERVAL;
            DateTime m0 = SaveMtime("persistent");
            int lines0 = autosaveLines;
            int strips0 = StripApplied;
            string clear = FlightGlobals.ClearToSave(false).ToString();
            GameSettings.AUTOSAVE_INTERVAL = interval;
            GameSettings.AUTOSAVE_SHORT_INTERVAL = interval;
            fas.StopAllCoroutines();
            fas.Start();
            float start = Time.realtimeSinceStartup;
            double box = double.IsNaN(a.Seconds) ? 60.0 : a.Seconds;
            while (SaveMtime("persistent") == m0 && Time.realtimeSinceStartup - start < box) yield return null;
            bool written = SaveMtime("persistent") != m0;
            GameSettings.AUTOSAVE_INTERVAL = oldLong;
            GameSettings.AUTOSAVE_SHORT_INTERVAL = oldShort;
            fas.StopAllCoroutines();
            fas.Start();
            yield return null;
            ParseSave("persistent", out bool droneIn, out string activeName, out bool activeIsShip, out int vessels);
            Obs("autosave", "ut", Ut(), "interval", F(interval), "written", B(written),
                "waitS", F(Time.realtimeSinceStartup - start), "autosaveLines", N(autosaveLines - lines0),
                "stripRan", B(StripApplied > strips0), "clearToSave", clear, "droneExists", B(drone != null),
                "droneInSave", B(droneIn), "active", activeName, "activeIsShip", B(activeIsShip),
                "vessels", N(vessels), "liveActive", Role(FlightGlobals.ActiveVessel),
                "restoredInterval", F(GameSettings.AUTOSAVE_INTERVAL), "bypass", B(fas.bypassAutoSave));
            Finish(written ? "ok" : "not-written", "droneInSave", B(droneIn), "activeIsShip", B(activeIsShip));
        }

        // ----- Lock (proof 8): the quicksave / quickload / pause lock and its witness -----

        private IEnumerator OpLock(Proof.Args a)
        {
            DateTime q0 = SaveMtime("quicksave");
            if (a.State == "off")
            {
                InputLockManager.RemoveControlLock(LockId);
            }
            else
            {
                InputLockManager.SetControlLock(ControlTypes.QUICKSAVE | ControlTypes.QUICKLOAD | ControlTypes.PAUSE, LockId);
            }
            float start = Time.realtimeSinceStartup;
            double box = double.IsNaN(a.Seconds) ? 1.0 : a.Seconds;
            while (Time.realtimeSinceStartup - start < box) yield return null;
            Obs("lock", "ut", Ut(), "state", a.State ?? "on",
                "quicksaveLocked", B(!InputLockManager.IsUnlocked(ControlTypes.QUICKSAVE)),
                "quickloadLocked", B(!InputLockManager.IsUnlocked(ControlTypes.QUICKLOAD)),
                "pauseLocked", B(!InputLockManager.IsUnlocked(ControlTypes.PAUSE)),
                "quicksaveChanged", B(SaveMtime("quicksave") != q0), "windowS", F(box));
            Finish("ok", "quicksaveChanged", B(SaveMtime("quicksave") != q0));
        }

        // ----- Fence (proof 12) -----

        private IEnumerator OpFence(Proof.Args a)
        {
            FenceOn = a.State == "on";
            Obs("fence", "ut", Ut(), "on", B(FenceOn), "resetContracts", N(ResetContractsCalls), "fenced", N(ResetContractsFenced));
            Finish("ok", "on", B(FenceOn));
            yield break;
        }

        // ----- Control (proof 10): a packed, inactive one-parter far from the active vessel in
        // atmosphere must die to stock's pressure kill -----

        private IEnumerator OpControl(Proof.Args a)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.mainBody == null || !active.mainBody.atmosphere) { Finish("no-atmosphere"); yield break; }
            if (control != null) { Finish("control-exists"); yield break; }
            CelestialBody body = active.mainBody;
            double alt = double.IsNaN(a.Alt) ? 15000.0 : a.Alt;
            double lat = active.latitude;
            double lon = active.longitude + 10.0;
            double terrain = SafeTerrain(body, lat, lon);
            if (alt < terrain + 1000.0) alt = terrain + 1000.0;
            Vector3d pos = body.GetWorldSurfacePosition(lat, lon, alt);
            int kills0 = pressureKillLines;
            Vessel v = LoadOneParter(ControlName, body, pos, body.getRFrmVel(pos), "FLYING", false, out string why);
            if (v == null) { Finish("load-failed-" + why); yield break; }
            control = v;
            uint pid = v.persistentId;
            double pressure = body.GetPressure(alt);
            float start = Time.realtimeSinceStartup;
            double box = double.IsNaN(a.Seconds) ? 30.0 : a.Seconds;
            while (Time.realtimeSinceStartup - start < box && pressureKillLines == kills0) yield return null;
            bool alive = FlightGlobals.Vessels.Exists(x => x != null && x.persistentId == pid);
            Obs("control", "ut", Ut(), "body", body.bodyName, "alt", F(alt), "pressureKPa", F(pressure),
                "distToActiveKm", F((pos - active.GetWorldPos3D()).magnitude / 1000.0),
                "killLine", B(pressureKillLines > kills0), "aliveAfter", B(alive), "waitS", F(Time.realtimeSinceStartup - start));
            if (alive && control != null) control.Die();
            control = null;
            Finish(pressureKillLines > kills0 ? "ok" : "no-kill-line", "killLine", B(pressureKillLines > kills0));
        }

        // ----- Recalc (proof 13): does the next recalculation take back an unbooked award -----

        private IEnumerator OpRecalc(Proof.Args a)
        {
            CareerSnapshot(out double f0, out double s0, out double r0, out int c0);
            string rdv0 = RendezvousState();
            LedgerOrchestrator.RecalculateAndPatch();
            yield return null;
            CareerSnapshot(out double f1, out double s1, out double r1, out int c1);
            Obs("recalc", "ut", Ut(), "fundsBefore", F(f0), "fundsAfter", F(f1), "sciBefore", F(s0), "sciAfter", F(s1),
                "repBefore", F(r0), "repAfter", F(r1), "contractsBefore", N(c0), "contractsAfter", N(c1),
                "rendezvousBefore", rdv0, "rendezvousAfter", RendezvousState(), "label", a.Label ?? "-");
            Finish("ok", "fundsDelta", F(f1 - f0), "repDelta", F(r1 - r0));
        }

        private static string RendezvousState()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (ProgressTracking.Instance == null || v == null || v.mainBody == null) return "-";
            KSPAchievements.CelestialBodySubtree tree = ProgressTracking.Instance.GetBodyTree(v.mainBody);
            if (tree == null || tree.rendezvous == null) return "-";
            return v.mainBody.bodyName + ":reached=" + B(tree.rendezvous.IsReached) + ":complete=" + B(tree.rendezvous.IsComplete);
        }

        // ----- Report: the whole-run counters and the career snapshot -----

        private IEnumerator OpReport(Proof.Args a)
        {
            CareerSnapshot(out double funds, out double sci, out double rep, out int contracts);
            Vessel active = FlightGlobals.ActiveVessel;
            Obs("report", "ut", Ut(), "label", a.Label ?? "-", "active", Name(active), "role", Role(active),
                "activeSit", active != null ? active.situation.ToString() : "-",
                "droneExists", B(drone != null), "funds", F(funds), "sci", F(sci), "rep", F(rep),
                "contracts", N(contracts), "resetContracts", N(ResetContractsCalls), "fenced", N(ResetContractsFenced),
                "pressureKills", N(pressureKillLines), "terrainKills", N(terrainKillLines),
                "goOffRailsRefused", N(GoOffRailsRefused), "strips", N(StripApplied), "events", N(eventSeq),
                "exceptions", N(exceptionLines), "rendezvous", RendezvousState(), "recorder", RecorderState());
            Finish("ok", "active", Name(active), "funds", F(funds), "rep", F(rep));
            yield break;
        }
    }
}
