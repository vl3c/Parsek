using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Postfix on the internal <c>ProtoVessel.Load(FlightState, Vessel)</c> that
    /// seeds <see cref="UnityEngine.Rigidbody.mass"/> on freshly-loaded packed
    /// parts of every vessel KSP reconstructs from a ProtoVessel, closing the
    /// PR #885 coverage gap for the stock save-load reconstruction paths.
    ///
    /// <para><b>Background.</b> PR #885 patched
    /// <see cref="VesselSpawner.SpawnAtPosition"/> and
    /// <see cref="VesselSpawner.RespawnVessel"/> with an inline call to
    /// <see cref="VesselSpawner.SeedRigidbodyMassesForPackedSpawn"/> so terminal-orbit
    /// spawns and validated respawns no longer cascade-explode at first Unpack.
    /// The inline call only covered Parsek's own spawn entry points. KSP also
    /// reconstructs vessels via <c>ProtoVessel.Load</c> on stock paths Parsek
    /// never touches: scene-load through <c>FlightDriver.StartAndFocusVessel</c>
    /// (quickload, <c>SetActiveVessel</c>-on-unloaded-target, scene-reload),
    /// <c>Game.AddVessel</c> for ConfigNode-driven adds, and the
    /// <c>MissionSystem</c> / <c>ContractSystem</c> <c>ConstructShip</c>
    /// coroutines. All of those routes hit the same Part.Start
    /// UpdateAutoStrut window with <c>part.rb.mass = 1</c> defaults, so
    /// MassivePartCheck falls into the geometric-distance tiebreaker, picks the
    /// wrong ForceHeaviest anchor for each leg, and the vessel cascade-explodes
    /// at the first Unpack the same way the spawned-vessel repro did. Confirmed
    /// in <c>logs/2026-05-17_1944_switch-fly-edge-case/KSP.log</c>: at 19:43:04.665
    /// the scene reloads (OnLoad), at 19:43:05.437 the PR #885 inline seeder runs
    /// only for an unrelated Parsek-spawn pid 2707100896, at 19:43:06.467
    /// "Unpacking Kerbal X" fires for pid 2708531065 and the next ms emits three
    /// <c>landingLeg1-2</c> joint-break diagnostics with <c>breakForce=0.0
    /// structural=F childAttachMatchesJoint=F</c> whose anchors split across
    /// <c>liquidEngine2-2.v2</c> and <c>mediumDishAntenna</c> — the same
    /// distance-tiebreaker fingerprint PR #885 documented — followed by the full
    /// <c>parachuteLarge / ladder1 / HeatShield2 / mk1-3pod</c> cascade.
    ///
    /// <para><b>Why postfix the internal 2-arg overload.</b> The public
    /// <c>ProtoVessel.Load(FlightState)</c> delegates to
    /// <c>internal void Load(FlightState, Vessel)</c>; KSP's
    /// <c>ConstructShip</c> coroutines call the 2-arg overload directly with an
    /// existing Vessel. Patching the internal overload captures every entry
    /// point in a single Harmony patch. Confirmed by ilspycmd dump of
    /// <c>Assembly-CSharp.dll</c>: the only callers of <c>ProtoVessel.Load</c>
    /// are <c>Game.AddVessel</c> (1-arg) and the two <c>ConstructShip</c>
    /// coroutines (2-arg) — both route through the internal overload.
    ///
    /// <para><b>Why not <c>GameEvents.onVesselLoaded</c>.</b> The event is
    /// a fine alternative anchor but requires a long-lived
    /// (<c>DontDestroyOnLoad</c>) addon to keep the subscription alive across
    /// scene transitions; ParsekFlight's subscription only covers the flight
    /// scene. The Harmony postfix is self-contained, fires synchronously when
    /// <c>pv.Load</c> returns (strictly before any Part.Start coroutine), and
    /// mirrors the existing patch convention in
    /// <c>Source/Parsek/Patches/</c>.
    ///
    /// <para><b>Exclusions.</b> Flag vessels and ghost-map ProtoVessels are
    /// skipped: both are single-part vessels with <c>autostrutMode = Off</c> so
    /// the wrong-anchor failure mode cannot manifest, and the existing
    /// <c>VesselSpawner</c> spawn paths intentionally do not call the seeder
    /// for them either (see the PR #885 commit comment in
    /// <c>VesselSpawner.SeedRigidbodyMassesForPackedSpawn</c>).
    ///
    /// <para><b>Single source of truth.</b>
    /// The PR #885 inline <c>SeedRigidbodyMassesForPackedSpawn</c> calls in
    /// <c>SpawnAtPosition</c> / <c>RespawnVessel</c> were removed in the
    /// same change as adding this postfix. Every production caller of the
    /// seeder helper goes through this one postfix, so the seed runs
    /// exactly once per <c>pv.Load</c>. Removing the inline calls drops the
    /// duplicate INFO log line on Parsek-spawn paths and removes a future
    /// drift hazard (two seed-call sites diverging on a refactor). The
    /// <c>ProtoVesselLoadPostfix_IsAppliedByHarmony</c> in-game test
    /// (Spawner / SPACECENTER) is the regression guard for the new
    /// single-point-of-failure: it asserts the postfix is in
    /// <c>Harmony.GetPatchInfo</c> so a future KSP update that renames /
    /// re-signatures <c>ProtoVessel.Load</c> fails loud rather than
    /// silently letting every loaded vessel cascade-explode.
    /// </summary>
    [HarmonyPatch]
    internal static class ProtoVesselLoadRigidbodyMassSeederPatch
    {
        /// <summary>
        /// Pure decision predicate split out so the skip cases are xUnit-testable
        /// without invoking Unity's overloaded <c>Vessel == null</c> /
        /// <c>Part == null</c> ECall operators (which throw SecurityException
        /// outside the engine runtime, same constraint that forced the
        /// <c>FormatPackedSpawnSeedSkipMessage</c> split in PR #885).
        /// </summary>
        internal static bool ShouldSeedAfterPvLoad(bool vesselNull, bool isFlag, bool isGhostMap)
        {
            if (vesselNull) return false;
            if (isFlag) return false;
            if (isGhostMap) return false;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProtoVessel), "Load", new[] { typeof(FlightState), typeof(Vessel) })]
        private static void PostfixLoad(ProtoVessel __instance)
        {
            if (ReferenceEquals(__instance, null)) return;
            Vessel vessel = __instance.vesselRef;
            bool vesselNull = ReferenceEquals(vessel, null);
            bool isFlag = !vesselNull && vessel.vesselType == VesselType.Flag;
            bool isGhostMap = !vesselNull
                && GhostMapPresence.IsGhostMapVessel(vessel.persistentId);
            if (!ShouldSeedAfterPvLoad(vesselNull, isFlag, isGhostMap))
            {
                ParsekLog.Verbose("Spawner",
                    "ProtoVesselLoad postfix skipped seeding: " +
                    $"vesselNull={(vesselNull ? "T" : "F")} " +
                    $"isFlag={(isFlag ? "T" : "F")} " +
                    $"isGhostMap={(isGhostMap ? "T" : "F")}");
                return;
            }

            VesselSpawner.SeedRigidbodyMassesForPackedSpawn(vessel, "ProtoVesselLoadPostfix");
        }
    }
}
