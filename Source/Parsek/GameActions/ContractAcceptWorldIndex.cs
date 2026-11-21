using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// One vessel as the accept-side world check reads it: from a <c>ProtoVessel</c> in
    /// <c>flightState.protoVessels</c> or from a live <c>Vessel</c> in
    /// <c>FlightGlobals.Vessels</c>. Pure data, so the index and the regeneration witness are
    /// testable headless.
    /// </summary>
    internal sealed class WorldVesselFact
    {
        /// <summary>The live <c>Vessel</c> or <c>ProtoVessel</c> object the fact was read from;
        /// the regeneration witness diffs on it by reference.</summary>
        internal object Key;
        /// <summary><c>Vessel.id</c> / <c>ProtoVessel.vesselID</c>; one vessel read from both
        /// sources carries the same id. <see cref="Guid.Empty"/> when unknown.</summary>
        internal Guid Id;
        internal uint PersistentId;
        internal string Name;
        /// <summary>True when read from <c>FlightGlobals.Vessels</c>.</summary>
        internal bool Live;
        internal readonly List<uint> PartFlightIds = new List<uint>();
        internal readonly List<string> Crew = new List<string>();
    }

    /// <summary>
    /// The live world facts a restored contract's accept-side check reads, built once per
    /// patch (or drain) and only when a restored contract's template has an accept-side world
    /// effect.
    /// </summary>
    internal sealed class ContractAcceptWorldIndex
    {
        internal readonly HashSet<uint> PartFlightIds = new HashSet<uint>();
        internal readonly HashSet<string> CrewAboard = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Every vessel's persistent id (0 dropped): the prebuilt-ship guard's "is the
        /// contract's craft in the world".</summary>
        internal readonly HashSet<uint> VesselPersistentIds = new HashSet<uint>();
        /// <summary>Every vessel's name (stock's own construction / repair <c>OnLoad</c>
        /// fallback matches the craft by name).</summary>
        internal readonly HashSet<string> VesselNames = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Distinct vessels, a vessel in both sources counted once.</summary>
        internal int VesselCount;
        /// <summary>Vessels only <c>FlightGlobals.Vessels</c> holds: created since
        /// <c>flightState</c> was last captured (a <c>Game.AddVessel</c> this scene).</summary>
        internal int LiveOnlyCount;
    }

    /// <summary>
    /// Pure. Todo RESCUE-REGEN-COUNTS-PROTOVESSELS. Stock's <c>Game.AddVessel</c> (decompiled
    /// KSP 1.12.5, what <c>RecoverAsset.GenerateOrbitalRecovery</c> / <c>GenerateLandRecovery</c>
    /// end in) builds the vessel with <c>ProtoVessel.Load(flightState)</c>, which registers it
    /// with <c>FlightGlobals.AddVessel</c>, and fires <c>onNewVesselCreated</c>; it never
    /// appends to <c>flightState.protoVessels</c>. That list is rebuilt from
    /// <c>FlightGlobals.Vessels</c> only by the next <c>Game.Updated()</c> ("Flight State
    /// Captured", every save in a scene with a planetarium). So a vessel stock just created
    /// is live and saved with the game, but absent from <c>flightState.protoVessels</c> until
    /// that save: the world index and the regeneration witness read both lists.
    /// </summary>
    internal static class ContractAcceptWorldIndexer
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// The union of the saved and the live vessels: every part flight id and seated kerbal
        /// either list holds. A vessel read from both (same non-empty id) is counted once.
        /// </summary>
        internal static ContractAcceptWorldIndex Build(
            IEnumerable<WorldVesselFact> saved, IEnumerable<WorldVesselFact> live)
        {
            var index = new ContractAcceptWorldIndex();
            var seenIds = new HashSet<Guid>();
            AddToIndex(index, saved, seenIds, liveOnly: false);
            AddToIndex(index, live, seenIds, liveOnly: true);
            return index;
        }

        private static void AddToIndex(ContractAcceptWorldIndex index, IEnumerable<WorldVesselFact> facts,
            HashSet<Guid> seenIds, bool liveOnly)
        {
            if (facts == null)
                return;
            foreach (WorldVesselFact f in facts)
            {
                if (f == null)
                    continue;
                bool newVessel = f.Id == Guid.Empty || seenIds.Add(f.Id);
                if (newVessel)
                {
                    index.VesselCount++;
                    if (liveOnly)
                        index.LiveOnlyCount++;
                }
                if (f.PersistentId != 0u)
                    index.VesselPersistentIds.Add(f.PersistentId);
                if (!string.IsNullOrEmpty(f.Name))
                    index.VesselNames.Add(f.Name);
                // Flight id 0 is no real part (ShipConstruction.GetUniqueFlightID never draws
                // it); Parsek's ghost map vessels carry one such part.
                for (int i = 0; i < f.PartFlightIds.Count; i++)
                {
                    if (f.PartFlightIds[i] != 0u)
                        index.PartFlightIds.Add(f.PartFlightIds[i]);
                }
                for (int i = 0; i < f.Crew.Count; i++)
                {
                    if (!string.IsNullOrEmpty(f.Crew[i]))
                        index.CrewAboard.Add(f.Crew[i]);
                }
            }
        }

        /// <summary>
        /// The vessels the generator added: every fact read after the call whose source object
        /// was not there before it, from either list, one per vessel (same non-empty id, or the
        /// same object, counted once), live facts first.
        /// </summary>
        internal static List<WorldVesselFact> SelectAdded(
            ICollection<object> keysBefore, IEnumerable<WorldVesselFact> savedAfter,
            IEnumerable<WorldVesselFact> liveAfter)
        {
            var added = new List<WorldVesselFact>();
            var addedIds = new HashSet<Guid>();
            var addedKeys = new HashSet<object>();
            CollectAdded(keysBefore, liveAfter, added, addedIds, addedKeys);
            CollectAdded(keysBefore, savedAfter, added, addedIds, addedKeys);
            return added;
        }

        private static void CollectAdded(ICollection<object> keysBefore, IEnumerable<WorldVesselFact> after,
            List<WorldVesselFact> added, HashSet<Guid> addedIds, HashSet<object> addedKeys)
        {
            if (after == null)
                return;
            foreach (WorldVesselFact f in after)
            {
                if (f == null || f.Key == null)
                    continue;
                if (keysBefore != null && keysBefore.Contains(f.Key))
                    continue;
                if (!addedKeys.Add(f.Key))
                    continue;
                if (f.Id != Guid.Empty && !addedIds.Add(f.Id))
                    continue;
                added.Add(f);
            }
        }

        /// <summary>The added vessels for the regeneration log line's bracketed list:
        /// <c>'name' pid=N crew=(a; b) parts=K live|saved</c>, comma separated, with no square
        /// bracket inside (the RSQ lanes match the list as one bracket pair).</summary>
        internal static string Describe(List<WorldVesselFact> added)
        {
            if (added == null || added.Count == 0)
                return "";
            var parts = new List<string>(added.Count);
            for (int i = 0; i < added.Count; i++)
            {
                WorldVesselFact f = added[i];
                parts.Add("'" + f.Name + "' pid=" + f.PersistentId.ToString(IC) +
                    " crew=(" + string.Join("; ", f.Crew.ToArray()) + ") parts=" +
                    f.PartFlightIds.Count.ToString(IC) + (f.Live ? " live" : " saved"));
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
