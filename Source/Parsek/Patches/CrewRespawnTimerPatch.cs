using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Career settings audit 2026-11-04 section 3.5. Stock decides a Missing kerbal's fate
    /// when his respawn timer runs out (<c>ProtoCrewMember.CheckRespawnTimer</c>, decompiled):
    /// it re-reads <c>MissingCrewsRespawn</c> then, and kills him when the setting is now off.
    /// A recorded death keeps the respawn policy that stood at the death (owner ruling S8), and
    /// a committed later flight may already use the kerbal after the respawn, so turning the
    /// setting off afterwards must not kill him: that would change a committed outcome. When
    /// the setting is off and Parsek holds a recorded respawn-on death for the kerbal, this
    /// prefix does what stock's respawn branch does (Available, a unique Spawn flight-log
    /// entry) once Parsek's respawn instant is reached, and keeps him Missing until then. Every
    /// other case runs stock unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewRespawnTimerPatch
    {
        internal const string Tag = "CrewRespawn";

        /// <summary>How far a recorded respawn may sit before stock's UTaR and still be the
        /// same Missing spell (rounding between the stamp and the roster).</summary>
        internal const double SpellToleranceSeconds = 1.0;

        internal enum Decision
        {
            Stock = 0,
            RespawnNow,
            KeepMissing,
        }

        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(ProtoCrewMember),
                nameof(ProtoCrewMember.CheckRespawnTimer),
                new[] { typeof(double), typeof(GameParameters) });
            if (method == null)
                ParsekLog.Warn(Tag,
                    "ProtoCrewMember.CheckRespawnTimer(double, GameParameters) not found - a recorded " +
                    "respawn-on death follows the live respawn setting. Harmony will skip this patch.");
            return method;
        }

        /// <summary>
        /// Pure. <see cref="Decision.Stock"/> unless the kerbal is Missing with stock's timer
        /// run out (<paramref name="ut"/> at or past <paramref name="utar"/>), the respawn
        /// setting is now off, and <paramref name="reservation"/> is a recorded death stamped
        /// with respawn on (not permanent, a respawn instant) whose respawn is THIS Missing spell's:
        /// at or after stock's <paramref name="utar"/> (within <see cref="SpellToleranceSeconds"/>;
        /// the stamp reads stock's own UTaR). An earlier respawn belongs to an older death the
        /// kerbal already came back from, and the current spell (a death not committed yet) is
        /// stock's. Then respawn once the clock reaches that instant, keep Missing before it.
        /// </summary>
        internal static Decision Decide(
            bool missing, double ut, double utar, bool respawnSettingOn,
            KerbalsModule.KerbalReservation reservation)
        {
            if (!missing || respawnSettingOn || ut < utar)
                return Decision.Stock;
            double respawnUT = KerbalsModule.LossRespawnUT(reservation);
            if (double.IsNaN(respawnUT) || respawnUT < utar - SpellToleranceSeconds)
                return Decision.Stock;
            return ut >= respawnUT ? Decision.RespawnNow : Decision.KeepMissing;
        }

        static bool Prefix(ProtoCrewMember __instance, double UT, GameParameters gameParameters)
        {
            if (ParsekGameModeGate.CheckInert("CrewRespawnTimerPatch.Prefix")) return true;
            if (__instance == null || gameParameters?.Difficulty == null) return true;
            bool missing = __instance.rosterStatus == ProtoCrewMember.RosterStatus.Missing;
            if (!missing || gameParameters.Difficulty.MissingCrewsRespawn || UT < __instance.UTaR)
                return true;

            KerbalsModule.KerbalReservation reservation = null;
            var reservations = LedgerOrchestrator.Kerbals?.Reservations;
            if (reservations != null)
                reservations.TryGetValue(__instance.name, out reservation);

            Decision decision = Decide(missing, UT, __instance.UTaR, false, reservation);
            if (decision == Decision.Stock)
                return true;
            if (decision == Decision.KeepMissing)
            {
                ParsekLog.VerboseRateLimited(Tag, "keep-missing-" + __instance.name,
                    $"'{__instance.name}' stays Missing: respawn is off now but the recorded death " +
                    $"respawns at {KerbalsModule.LossRespawnUT(reservation).ToString("R", CultureInfo.InvariantCulture)} " +
                    $"(ut={UT.ToString("R", CultureInfo.InvariantCulture)})");
                return false;
            }

            __instance.rosterStatus = ProtoCrewMember.RosterStatus.Available;
            __instance.flightLog?.AddEntryUnique(FlightLog.EntryType.Spawn, null);
            ParsekLog.Info(Tag,
                $"'{__instance.name}' respawned at ut={UT.ToString("R", CultureInfo.InvariantCulture)} " +
                "although crew respawn is off now: the recorded death was stamped with respawn on " +
                "(owner ruling S8), so a committed later flight keeps him");
            return false;
        }
    }
}
