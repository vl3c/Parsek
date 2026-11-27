using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The player's ship (helper H2 of design-far-watch.md 11.2): while the camera drone exists
    /// the active vessel is the drone, so a site that means "the ship the player flies" asks here.
    /// With no drone it answers <c>FlightGlobals.ActiveVessel</c>, so a converted site behaves
    /// exactly as before outside a far watch.
    /// </summary>
    internal static class FarWatchPlayerVessel
    {
        /// <summary>The player ship, or the active vessel when no drone exists; null when neither resolves.</summary>
        internal static Vessel Resolve()
        {
            uint pid;
            Guid guid;
            if (!CameraDroneIdentity.TryGetPlayerShip(out pid, out guid))
                return ReadActiveVesselCore();
            return FindLoadedOrListedCore(pid, guid);
        }

        /// <summary>Pure: is a (pid, guid) pair the recorded player ship? An empty guid never matches.</summary>
        internal static bool IsPlayerShip(uint pid, Guid guid)
        {
            uint shipPid;
            Guid shipGuid;
            if (!CameraDroneIdentity.TryGetPlayerShip(out shipPid, out shipGuid))
                return false;
            return pid == shipPid && guid != Guid.Empty && guid == shipGuid;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vessel ReadActiveVesselCore()
        {
            try
            {
                return FlightGlobals.ActiveVessel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vessel FindLoadedOrListedCore(uint pid, Guid guid)
        {
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return null;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid && v.id == guid)
                        return v;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
