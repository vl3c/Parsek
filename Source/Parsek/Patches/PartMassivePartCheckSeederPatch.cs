using System.Threading;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prefix on <c>Part.MassivePartCheck</c> that lazily seeds
    /// <c>p.rb.mass</c> for any packed part still at Unity's
    /// <c>rb.mass = 1</c> default, immediately before the body reads it.
    /// This is the canonical fix for the ForceHeaviest-cascade bug;
    /// PR #885 and the earlier iterations of PR #890 both attempted
    /// vessel-level seeding at the wrong lifecycle moment and were
    /// structural no-ops in production (every seeder log line showed
    /// <c>updated=0</c> or <c>skippedNoRb=N</c>).
    ///
    /// <para><b>Why prefix MassivePartCheck.</b>
    /// <c>Part.MassivePartCheck(Part original, Part p, ref Part highestPart,
    /// ref float highestMass, params Part[] excluded)</c> is the inner read
    /// site of the ForceHeaviest autostrut selection: every other
    /// candidate is enumerated, MassivePartCheck reads
    /// <c>p.rb.mass</c>, ranks against <c>highestMass</c>, and updates the
    /// running heaviest. If <c>p.rb.mass</c> is Unity's default 1.0 on every
    /// candidate, the approximate-equality tiebreaker falls into geometric
    /// distance and picks the closest sibling instead of the actual heaviest
    /// part. Seeding <c>p.rb.mass</c> in a prefix on this exact method
    /// guarantees the value is correct when the body reads it, regardless of
    /// Unity coroutine timing or KSP's part-construction lifecycle. After
    /// the first seed for a given part, <c>p.rb.mass != 1f</c>, so subsequent
    /// MassivePartCheck calls on the same part skip the seed via the
    /// implicit guard.
    ///
    /// <para><b>Why not Vessel.Load or ProtoVessel.Load.</b>
    /// Decompile of <c>Assembly-CSharp.dll</c>:
    /// <list type="bullet">
    /// <item><c>ProtoVessel.Load(FlightState, Vessel)</c> never populates
    /// <c>vesselRef.parts</c> (parts are instantiated by
    /// <c>protoVessel.LoadObjects()</c> from inside <c>Vessel.Load</c>).
    /// The PR #885 inline calls and the first iteration of PR #890 both
    /// targeted this method and reported <c>updated=0</c> in every
    /// production log line.</item>
    /// <item>Postfixing <c>Vessel.Load()</c> after <c>LoadObjects()</c>
    /// finds <c>vessel.parts</c> populated but <c>part.rb</c> is still
    /// <c>null</c> for every part: rb is assigned later inside
    /// <c>Part.Start</c> (an <c>IEnumerator</c>), which resumes on Unity's
    /// next Update tick. A second iteration of PR #890 tried this anchor
    /// and would have skipped every part via the <c>part.rb == null</c>
    /// guard in the seeder, again silently writing nothing.</item>
    /// </list>
    /// Prefixing the read site sidesteps every lifecycle question.
    ///
    /// <para><b>Exclusions.</b> Flag vessels and ghost-map ProtoVessels are
    /// skipped via <see cref="ShouldSeedPart"/>: both create single-part
    /// vessels with <c>autostrutMode = Off</c>, so MassivePartCheck is
    /// never reached on them in practice; the gates are defensive.
    /// Unpacked parts are also skipped because FlightIntegrator manages
    /// <c>rb.mass</c> per tick on them, so the value is already correct.
    ///
    /// <para><b>Diagnostics.</b> <see cref="SeededPartCount"/> increments
    /// every time a seed happens; the
    /// <c>PartMassivePartCheckSeederTest.SeededPartCount_IsPositiveInFlight</c>
    /// in-game test reads this counter to assert the patch actually mutated
    /// at least one part's rb.mass during the flight session (catches the
    /// "patch applies but does nothing" regression that PR #885 / earlier
    /// PR #890 iterations both exhibited).
    /// </summary>
    [HarmonyPatch]
    internal static class PartMassivePartCheckSeederPatch
    {
        /// <summary>
        /// Total number of <c>part.rb.mass</c> writes performed by this
        /// prefix since process start. Read by
        /// <c>PartMassivePartCheckSeederTest</c> to verify the prefix is
        /// actually mutating rb.mass values, not just registered.
        /// </summary>
        internal static int SeededPartCount;

        /// <summary>
        /// Pure decision predicate, xUnit-testable. <paramref name="vessel"/>
        /// is the part's owning vessel (or null when unreachable). The live
        /// wrapper cannot be invoked from xUnit because reading
        /// <c>p.rb</c> / <c>p.vessel</c> goes through Unity's overloaded
        /// <c>Part == null</c> operator (same ECall SecurityException
        /// constraint that forced the
        /// previous skip-message-formatter split in PR #885, now removed).
        /// </summary>
        internal static bool ShouldSeedPart(
            bool partNull, bool rbNull, bool partInfoNull, bool partPacked,
            bool rbMassAtUnityDefault, bool vesselNull, bool isFlag, bool isGhostMap)
        {
            if (partNull) return false;
            if (rbNull) return false;
            if (partInfoNull) return false;
            if (!partPacked) return false;
            if (!rbMassAtUnityDefault) return false;
            if (vesselNull) return false;
            if (isFlag) return false;
            if (isGhostMap) return false;
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Part), "MassivePartCheck", new System.Type[]
        {
            typeof(Part),
            typeof(Part),
            typeof(Part),
            typeof(float),
            typeof(Part[])
        },
        new HarmonyLib.ArgumentType[]
        {
            HarmonyLib.ArgumentType.Normal,
            HarmonyLib.ArgumentType.Normal,
            HarmonyLib.ArgumentType.Ref,
            HarmonyLib.ArgumentType.Ref,
            HarmonyLib.ArgumentType.Normal,
        })]
        private static void PrefixMassivePartCheck(Part p)
        {
            bool partNull = ReferenceEquals(p, null);
            bool rbNull = !partNull && p.rb == null;
            bool partInfoNull = !partNull && p.partInfo == null;
            bool partPacked = !partNull && p.packed;
            bool rbMassAtUnityDefault = !partNull && !rbNull && p.rb.mass == 1f;
            Vessel vessel = partNull ? null : p.vessel;
            bool vesselNull = ReferenceEquals(vessel, null);
            bool isFlag = !vesselNull && vessel.vesselType == VesselType.Flag;
            bool isGhostMap = !vesselNull
                && GhostMapPresence.IsGhostMapVessel(vessel.persistentId);

            if (!ShouldSeedPart(partNull, rbNull, partInfoNull, partPacked,
                    rbMassAtUnityDefault, vesselNull, isFlag, isGhostMap))
                return;

            if (VesselSpawner.SeedSinglePackedPart(p))
                Interlocked.Increment(ref SeededPartCount);
        }
    }
}
