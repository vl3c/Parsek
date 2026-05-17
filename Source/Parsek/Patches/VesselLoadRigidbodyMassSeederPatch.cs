using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Postfix on <c>Vessel.Load()</c> that seeds
    /// <see cref="UnityEngine.Rigidbody.mass"/> on the freshly-instantiated
    /// packed parts of every vessel KSP loads into the physics scene,
    /// defending ForceHeaviest autostrut anchor selection from Unity's
    /// <c>rb.mass = 1</c> default before <c>Part.Start</c> runs
    /// <c>UpdateAutoStrut</c>.
    ///
    /// <para><b>Why Vessel.Load and not ProtoVessel.Load.</b> The previous
    /// iteration of this patch (and PR #885's inline
    /// <c>SeedRigidbodyMassesForPackedSpawn</c> calls in
    /// <c>VesselSpawner.SpawnAtPosition</c> / <c>RespawnVessel</c>) targeted
    /// <c>ProtoVessel.Load(FlightState, Vessel)</c>. ilspycmd of
    /// <c>Assembly-CSharp.dll</c> shows that overload only initializes
    /// vessel metadata and fires <c>GameEvents.onVesselCreate</c>; it never
    /// calls <c>protoVessel.LoadObjects()</c>, so
    /// <c>vesselRef.parts</c> is empty when the postfix fires and the
    /// seeder loop iterates zero times. Confirmed empirically by two
    /// independent production logs that both reported
    /// <c>updated=0 skippedNoRb=0 skippedNoPartInfo=0</c> on every PR #885
    /// seeder line: the seeder was a structural no-op for two months.
    ///
    /// <para>Parts are instantiated by <c>ProtoVessel.LoadObjects()</c>,
    /// which <c>Vessel.Load()</c> calls before firing
    /// <c>GameEvents.onVesselLoaded</c>. Postfixing <c>Vessel.Load()</c>
    /// runs after <c>LoadObjects</c> populates <c>vessel.parts</c> and
    /// after <c>SetLoaded(true)</c>, but before Unity's next-frame
    /// <c>Part.Start</c> callback fires the <c>UpdateAutoStrut</c>,
    /// <c>CycleAutoStrut</c>, <c>SecureAutoStruts</c> chain. That gives
    /// the seeder a deterministic window to write correct rb.mass values
    /// before <c>MassivePartCheck</c> reads them.
    ///
    /// <para><b>Coverage.</b> <c>Vessel.Load()</c> is the single entry
    /// point through which every freshly-loaded packed vessel passes:
    /// active vessel after scene load (via <c>Vessel.MakeActive</c> when
    /// <c>!loaded</c>), background vessels entering physics range, and
    /// every Parsek <c>SpawnAtPosition</c> / <c>RespawnVessel</c> spawn
    /// (the spawned vessel is in proto-only state until activated, at
    /// which point <c>Vessel.Load</c> fires). One postfix covers every
    /// path.
    ///
    /// <para><b>Exclusions.</b> Flag vessels and ghost-map ProtoVessels
    /// are skipped via the <see cref="ShouldSeedAfterVesselLoad"/> pure
    /// predicate: both create single-part vessels with
    /// <c>autostrutMode = Off</c>, so the wrong-anchor failure mode cannot
    /// manifest. Ghost-map vessel PIDs are registered in
    /// <see cref="GhostMapPresence.ghostMapVesselPids"/> before
    /// <c>pv.Load</c> runs, so the lookup is valid at this postfix.
    ///
    /// <para><b>Single source of truth.</b> The PR #885 inline
    /// <c>SeedRigidbodyMassesForPackedSpawn</c> calls in
    /// <c>SpawnAtPosition</c> / <c>RespawnVessel</c> were removed in the
    /// same change as adding this postfix. Every production caller of the
    /// seeder helper goes through this one postfix. The
    /// <c>VesselLoadPostfix_IsAppliedByHarmony</c> in-game test is the
    /// regression guard for the new single-point-of-failure: it asserts
    /// the postfix is in <c>Harmony.GetPatchInfo</c> on
    /// <c>Vessel.Load()</c>, so a future KSP update that renames or
    /// re-signatures <c>Vessel.Load</c> fails loud rather than silently
    /// letting every loaded vessel cascade-explode.
    /// </summary>
    [HarmonyPatch]
    internal static class VesselLoadRigidbodyMassSeederPatch
    {
        /// <summary>
        /// Pure decision predicate split out so the skip cases are
        /// xUnit-testable without invoking Unity's overloaded
        /// <c>Vessel == null</c> / <c>Part == null</c> ECall operators
        /// (which throw SecurityException outside the engine runtime, the
        /// same constraint that forced the
        /// <c>FormatPackedSpawnSeedSkipMessage</c> split in PR #885).
        /// </summary>
        internal static bool ShouldSeedAfterVesselLoad(bool vesselNull, bool isFlag, bool isGhostMap)
        {
            if (vesselNull) return false;
            if (isFlag) return false;
            if (isGhostMap) return false;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Vessel), "Load")]
        private static void PostfixLoad(Vessel __instance)
        {
            bool vesselNull = ReferenceEquals(__instance, null);
            bool isFlag = !vesselNull && __instance.vesselType == VesselType.Flag;
            bool isGhostMap = !vesselNull
                && GhostMapPresence.IsGhostMapVessel(__instance.persistentId);
            if (!ShouldSeedAfterVesselLoad(vesselNull, isFlag, isGhostMap))
            {
                ParsekLog.Verbose("Spawner",
                    "VesselLoad postfix skipped seeding: " +
                    $"vesselNull={(vesselNull ? "T" : "F")} " +
                    $"isFlag={(isFlag ? "T" : "F")} " +
                    $"isGhostMap={(isGhostMap ? "T" : "F")}");
                return;
            }

            VesselSpawner.SeedRigidbodyMassesForPackedSpawn(__instance, "VesselLoadPostfix");
        }
    }
}
