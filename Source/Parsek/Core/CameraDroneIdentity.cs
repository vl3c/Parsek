using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The far-watch camera drone's identity and the player ship it stands in for
    /// (docs/dev/design-far-watch.md section 6.1). Plain values only, so every module can ask
    /// "is this the drone?" without a live read. The drone pid is set BEFORE <c>pv.Load</c>, so
    /// the vessel GameEvents its load fires already see it.
    ///
    /// Identity needs pid AND guid: <c>persistentId</c> is craft-baked for real ships, and the
    /// drone pid comes from <c>FlightGlobals.GetUniquepersistentId()</c> with a fresh Guid, so a
    /// pid-only match could in principle name a later vessel that reused the number.
    ///
    /// Lifetime (scripts/arch/lifetimes.toml, load-transition): cleared by the far-watch
    /// Teardown, by the first OnSave or OnLoad after a scene exit (<see cref="ClearAfterStrip"/>),
    /// by the Cold load closure and by the in-game test runner's between-run reset.
    /// </summary>
    internal static class CameraDroneIdentity
    {
        internal const string DroneVesselName = "Parsek Camera Drone";

        internal static uint DronePid { get; private set; }
        internal static Guid DroneGuid { get; private set; }
        internal static uint ShipPid { get; private set; }
        internal static Guid ShipGuid { get; private set; }

        /// <summary>The drone ids were kept across a scene exit for the next save / load strip (7.5).</summary>
        internal static bool StripPending { get; private set; }

        /// <summary>
        /// The player ship was lost while the drone exists (7.6, V2): there is no ship to point a
        /// save's active index at, so the strip removes the drone with no ship repair and never
        /// refuses the save (stock's flight-ended screen saves on both of its exits).
        /// </summary>
        internal static bool ShipLost { get; private set; }

        /// <summary>True only while Parsek itself is inside its own ForceSetActiveVessel call.</summary>
        internal static bool SwitchTokenHeld { get; private set; }

        internal static bool Exists => DronePid != 0;

        internal static void SetDrone(uint pid, Guid guid)
        {
            DronePid = pid;
            DroneGuid = guid;
            StripPending = false;
            ShipLost = false;
            ParsekLog.Info("CameraDrone", string.Format(CultureInfo.InvariantCulture,
                "identity set dronePid={0} droneGuid={1}", pid, guid.ToString("N")));
        }

        internal static void SetShip(uint pid, Guid guid)
        {
            ShipPid = pid;
            ShipGuid = guid;
            ParsekLog.Info("CameraDrone", string.Format(CultureInfo.InvariantCulture,
                "player ship set shipPid={0} shipGuid={1}", pid, guid.ToString("N")));
        }

        /// <summary>Scene exit (7.5): stop owning the drone but keep its ids for the strip.</summary>
        internal static void MarkStripPending()
        {
            if (!Exists)
                return;
            StripPending = true;
            ParsekLog.Info("CameraDrone", string.Format(CultureInfo.InvariantCulture,
                "identity kept for the next save/load strip dronePid={0} shipPid={1}", DronePid, ShipPid));
        }

        /// <summary>The player ship is gone while the drone exists (7.6, V2).</summary>
        internal static void MarkShipLost()
        {
            if (!Exists)
                return;
            ShipLost = true;
            ParsekLog.Info("CameraDrone", string.Format(CultureInfo.InvariantCulture,
                "player ship lost; saves strip the drone with no ship repair dronePid={0} shipPid={1}",
                DronePid, ShipPid));
        }

        /// <summary>
        /// A save that finds no ProtoVessel for the ship may still be written: the ids are only
        /// the scene-exit leftovers, or the ship is known lost. Otherwise the strip refuses it.
        /// </summary>
        internal static bool SaveMayProceedWithoutShip => StripPending || ShipLost;

        internal static void Clear(string reason)
        {
            if (!Exists && ShipPid == 0 && !StripPending && !SwitchTokenHeld && !ShipLost)
                return;
            ParsekLog.Info("CameraDrone", string.Format(CultureInfo.InvariantCulture,
                "identity cleared reason={0} dronePid={1} shipPid={2} stripPending={3} shipLost={4}",
                reason ?? "(none)", DronePid, ShipPid, StripPending, ShipLost));
            DronePid = 0;
            DroneGuid = Guid.Empty;
            ShipPid = 0;
            ShipGuid = Guid.Empty;
            StripPending = false;
            ShipLost = false;
            SwitchTokenHeld = false;
        }

        /// <summary>
        /// The first OnSave or OnLoad after a scene exit ran its strip: drop the ids kept for it.
        /// A drone owned by a live far watch (StripPending false) is left alone.
        /// </summary>
        internal static void ClearAfterStrip(string context)
        {
            if (!StripPending)
                return;
            Clear("after-strip:" + (context ?? "(none)"));
        }

        /// <summary>
        /// ParsekScenario.OnLoad (the Cold closure and every in-session load): no far watch
        /// survives a scene change, so any ids still held are the scene-exit strip's.
        /// </summary>
        internal static void ClearOnLoad(string context)
        {
            if (!Exists && ShipPid == 0 && !StripPending && !SwitchTokenHeld && !ShipLost)
                return;
            Clear("load:" + (context ?? "(none)"));
        }

        /// <summary>The in-game test runner's between-run reset.</summary>
        internal static void ResetBetweenTestRuns(string reason)
        {
            if (!Exists && ShipPid == 0 && !StripPending && !SwitchTokenHeld && !ShipLost)
                return;
            Clear("between-test-runs:" + (reason ?? "(none)"));
        }

        internal static void BeginSwitch()
        {
            SwitchTokenHeld = true;
        }

        internal static void EndSwitch()
        {
            SwitchTokenHeld = false;
        }

        internal static bool IsDrone(uint pid)
        {
            return pid != 0 && pid == DronePid;
        }

        /// <summary>Pid and guid must both match; an empty guid on either side never matches.</summary>
        internal static bool IsDrone(uint pid, Guid guid)
        {
            return IsDrone(pid) && guid != Guid.Empty && guid == DroneGuid;
        }

        internal static bool TryGetPlayerShip(out uint pid, out Guid guid)
        {
            pid = ShipPid;
            guid = ShipGuid;
            return Exists && ShipPid != 0 && ShipGuid != Guid.Empty;
        }

        internal static void ResetForTesting()
        {
            DronePid = 0;
            DroneGuid = Guid.Empty;
            ShipPid = 0;
            ShipGuid = Guid.Empty;
            StripPending = false;
            ShipLost = false;
            SwitchTokenHeld = false;
        }
    }
}
