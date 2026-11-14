using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Roster-facing queries: the retired set, traits and tourist evidence, the crew-dialog filter, managed / dismissal checks, reservation kind, occupants and chain owners, and the active chain index.
    internal partial class KerbalsModule : IResourceModule
    {
        /// <summary>
        /// Determine which stand-ins are retired (used in a recording but displaced).
        /// A stand-in is displaced when its predecessor (owner or earlier stand-in) is free.
        /// </summary>
        private void ComputeRetiredSet()
        {
            int retiredCount = 0;
            foreach (var kvp in slots)
            {
                var slot = kvp.Value;

                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    string standIn = slot.Chain[i];
                    if (standIn == null) continue;

                    bool isReserved = IsReservedNow(standIn);
                    bool usedInRecording = IsKerbalInAnyRecording(standIn);

                    if (IsDisplacedChainEntry(slot, i) && usedInRecording && !isReserved)
                    {
                        retiredKerbals.Add(standIn);
                        retiredCount++;
                        ParsekLog.Verbose(Tag,
                            $"Retired: '{standIn}' in slot '{slot.OwnerName}' depth={i} " +
                            "(used in recording, displaced by predecessor)");
                    }
                }
            }

            if (retiredCount > 0)
                ParsekLog.Verbose(Tag, $"ComputeRetiredSet: {retiredCount} retired stand-in(s)");
        }

        // ────────────────────────────────────────────────────────
        // Query methods (instance — access module state)
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Check if a kerbal name appears in any active committed recording's crew.
        /// Uses the allRecordingCrew HashSet built during ProcessAction for O(1) lookup.
        /// </summary>
        internal bool IsKerbalInAnyRecording(string kerbalName)
        {
            return allRecordingCrew.Contains(kerbalName);
        }

        /// <summary>What the live roster knows about a kerbal, for <see cref="ResolveTraitForKerbal"/>.</summary>
        internal enum RosterKerbalKind
        {
            /// <summary>Not in the roster, no roster, or a type the trait lookup ignores
            /// (applicant, unowned).</summary>
            Absent,
            Crew,
            Tourist
        }

        /// <summary>
        /// Find the experience trait for a kerbal: the live roster first (a crew member's
        /// trait, or "Tourist" for a tourist, whom stock's <c>roster.Crew</c> leaves out),
        /// then the walk's tourist rows, then the latest baseline that names him, then
        /// "Pilot". Outside KSP (tests) the roster read fails and the fallbacks answer.
        /// </summary>
        internal static string FindTraitForKerbal(string kerbalName)
        {
            RosterKerbalKind kind = RosterKerbalKind.Absent;
            string crewTrait = null;
            try
            {
                var roster = HighLogic.CurrentGame?.CrewRoster;
                ProtoCrewMember pcm = roster != null && !string.IsNullOrEmpty(kerbalName)
                    ? roster[kerbalName] : null;
                if (pcm != null)
                {
                    if (pcm.type == ProtoCrewMember.KerbalType.Crew)
                    {
                        kind = RosterKerbalKind.Crew;
                        crewTrait = pcm.experienceTrait?.TypeName;
                    }
                    else if (pcm.type == ProtoCrewMember.KerbalType.Tourist)
                    {
                        kind = RosterKerbalKind.Tourist;
                    }
                }
            }
            catch
            {
                // HighLogic not available (unit test environment)
            }

            var kerbals = LedgerOrchestrator.Kerbals;
            return ResolveTraitForKerbal(
                kerbalName, kind, crewTrait,
                kerbals != null ? (Func<string, bool>)kerbals.IsTouristPassenger : null,
                GameStateStore.Baselines);
        }

        /// <summary>
        /// The decision behind <see cref="FindTraitForKerbal"/>. A tourist answers "Tourist"
        /// from the roster, from an effective tourist row (the durable record once stock
        /// has deleted him), or from a baseline; a roster crew member answers his trait
        /// ("Pilot" when it is unreadable). Pure.
        /// </summary>
        internal static string ResolveTraitForKerbal(
            string kerbalName,
            RosterKerbalKind rosterKind,
            string rosterCrewTrait,
            Func<string, bool> isTouristPassenger,
            IReadOnlyList<GameStateBaseline> baselines)
        {
            if (rosterKind == RosterKerbalKind.Crew)
                return rosterCrewTrait ?? "Pilot";
            if (rosterKind == RosterKerbalKind.Tourist)
                return TouristRole;

            if (isTouristPassenger != null && !string.IsNullOrEmpty(kerbalName)
                && isTouristPassenger(kerbalName))
                return TouristRole;

            if (baselines != null)
            {
                for (int i = baselines.Count - 1; i >= 0; i--)
                {
                    var baseline = baselines[i];
                    if (baseline?.crewEntries == null) continue;

                    for (int j = 0; j < baseline.crewEntries.Count; j++)
                    {
                        var crew = baseline.crewEntries[j];
                        if (crew.name == kerbalName && !string.IsNullOrEmpty(crew.trait))
                            return crew.trait;
                    }
                }
            }

            return "Pilot";
        }

        /// <summary>
        /// True when the timeline says <paramref name="kerbalName"/> rode as a tourist: an
        /// effective tourist row of the last walk, or a baseline that captured him as one.
        /// The roster is not consulted; the spawn path asks this for a name the roster lacks.
        /// </summary>
        internal static bool HasTouristEvidence(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            var kerbals = LedgerOrchestrator.Kerbals;
            return IsTouristRole(ResolveTraitForKerbal(
                kerbalName, RosterKerbalKind.Absent, null,
                kerbals != null ? (Func<string, bool>)kerbals.IsTouristPassenger : null,
                GameStateStore.Baselines));
        }

        /// <summary>
        /// Check if a kerbal is available for a new recording: no reservation in force at
        /// this walk's clock (<see cref="IsReservedNow"/>). A Recovered flight's hold
        /// whose end the clock has reached no longer counts. TEST-ONLY (no production
        /// caller, todo GUI-D8): the assertion surface the reservation tests read.
        /// </summary>
        internal bool IsKerbalAvailableForTesting(string kerbalName)
        {
            bool reserved = IsReservedNow(kerbalName);
            ParsekLog.Verbose(Tag,
                $"Availability check: '{kerbalName}' -> {(reserved ? "RESERVED" : "available")}");
            return !reserved;
        }

        /// <summary>
        /// Check if a kerbal should be filtered from the VAB/SPH crew assignment dialog.
        /// Returns true for reserved kerbals and retired stand-ins — these must not be
        /// assignable to new vessels. Returns false for active stand-ins (in chains but
        /// not reserved/retired) — they are the player's replacement crew and must remain
        /// selectable.
        ///
        /// This is narrower than IsManaged, which also returns true for active stand-ins.
        /// The crew dialog's refusal predicate: StockUiCrewDialogDecoration greys these rows
        /// and every seat-placing path refuses them; CrewAutoAssignPatch clears their seats.
        /// A kerbal the committed timeline dismisses later is refused too (design 9.12.1:
        /// he is fired then, so he flies nothing before it).
        ///
        /// <para>Phase 7 of Rewind-to-Staging (design §3.3.1 kerbal dual-residence
        /// carve-out): when a re-fly session is active and the kerbal is
        /// currently embodied on the provisional re-fly vessel, the filter is
        /// bypassed so the player can interact with them (EVA, transfer, etc.)
        /// despite their reserved / retired state still being in effect.</para>
        /// </summary>
        internal bool ShouldFilterFromCrewDialog(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            bool filtered = IsReservedNow(kerbalName)
                || retiredKerbals.Contains(kerbalName)
                || StockUiReservationPredicates.IsKerbalDismissalPendingNow(kerbalName);
            if (!filtered) return false;

            // §3.3.1 carve-out: a live re-fly crewmember is exempt from
            // reservation / retirement lock for the session duration.
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster != null)
            {
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (pcm == null) continue;
                    if (!string.Equals(pcm.name, kerbalName, System.StringComparison.Ordinal))
                        continue;
                    if (CrewReservationManager.IsLiveReFlyCrew(pcm))
                    {
                        // Rate-limited per kerbal: a hovered re-fly portrait asks about once a second.
                        ParsekLog.VerboseRateLimited("ReFlySession", "crew-dialog-carve-out|" + kerbalName,
                            $"Crew dialog carve-out: '{kerbalName}' is live re-fly crew — bypassing filter");
                        return false;
                    }
                    break;
                }
            }
            return true;
        }

        /// <summary>
        /// Check if a kerbal is managed by Parsek (reserved now, active stand-in, or
        /// retired). An owner whose Recovered hold has ended is back in his own seat and
        /// is an ordinary kerbal again (design 9.4).
        /// </summary>
        internal bool IsManaged(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            if (IsReservedNow(kerbalName)) return true;
            if (retiredKerbals.Contains(kerbalName)) return true;

            // Check if they're a stand-in in any chain
            foreach (var slot in slots.Values)
            {
                if (slot.Chain.Contains(kerbalName)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when any committed flight on the timeline names this kerbal (the RAW
        /// reservation map, whether or not the hold is in force now).
        /// </summary>
        internal bool IsNamedByCommittedFlight(string kerbalName)
        {
            return !string.IsNullOrEmpty(kerbalName) && reservations.ContainsKey(kerbalName);
        }

        /// <summary>
        /// Whether stock's Dismiss must be refused: a managed kerbal (reserved now, retired,
        /// or a chain stand-in) OR any kerbal a committed flight names. A returned owner is
        /// free to FLY again, but sacking him would leave committed flights (ghost crew, a
        /// rewind before his recovery that must re-reserve him) naming a kerbal the roster
        /// no longer has, so dismissal stays blocked for him the way it is for a retired
        /// stand-in who flew a committed flight.
        /// </summary>
        internal bool ShouldBlockDismissal(string kerbalName)
        {
            return IsManaged(kerbalName) || IsNamedByCommittedFlight(kerbalName);
        }

        /// <summary>
        /// Classifies the subset of managed kerbals that should be visually marked
        /// as reserved/retired in stock roster surfaces.
        /// </summary>
        internal KerbalReservationKind GetReservationKind(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return KerbalReservationKind.NotManaged;
            if (IsReservedNow(kerbalName))
                return KerbalReservationKind.ReservedActive;
            if (retiredKerbals.Contains(kerbalName))
                return KerbalReservationKind.ReservedRetired;
            return KerbalReservationKind.NotManaged;
        }

        /// <summary>
        /// Get the active occupant for a slot: the owner if free, otherwise the first
        /// free stand-in after the reserved prefix. Deeper free stand-ins are displaced
        /// metadata once an earlier occupant reclaims the slot.
        /// </summary>
        internal string GetActiveOccupant(string slotOwnerName)
        {
            KerbalSlot slot;
            slots.TryGetValue(slotOwnerName, out slot);

            int activeIndex = GetActiveChainIndex(slotOwnerName, slot);
            if (activeIndex == ActiveOwnerIndex)
                return slotOwnerName;
            if (activeIndex >= 0 && slot != null && activeIndex < slot.Chain.Count)
                return slot.Chain[activeIndex];

            // All occupants in the reserved prefix are still reserved, or the slot
            // exited the chain system entirely.
            return null;
        }

        /// <summary>
        /// The owner whose seat this kerbal is standing in for: the slot owner whose chain
        /// lists him AND whose active occupant he is now (the Kerbals window's
        /// <c>Stand-in for &lt;owner&gt;</c>, a per-member fact). Null for an owner, a
        /// displaced or retired chain member, and a kerbal in no chain.
        /// </summary>
        internal string FindActiveStandInOwner(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return null;
            foreach (var slot in slots.Values)
            {
                if (slot == null || slot.Chain == null || string.IsNullOrEmpty(slot.OwnerName)) continue;
                if (string.Equals(slot.OwnerName, kerbalName, StringComparison.Ordinal)) continue;
                if (!slot.Chain.Contains(kerbalName)) continue;
                if (string.Equals(GetActiveOccupant(slot.OwnerName), kerbalName, StringComparison.Ordinal))
                    return slot.OwnerName;
            }
            return null;
        }

        /// <summary>
        /// The slot owner whose replacement chain lists this kerbal, active occupant or not
        /// (a displaced or retired chain member included). Null for an owner and for a
        /// kerbal in no chain.
        /// </summary>
        internal string FindChainOwner(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return null;
            foreach (var slot in slots.Values)
            {
                if (slot == null || slot.Chain == null || string.IsNullOrEmpty(slot.OwnerName)) continue;
                if (string.Equals(slot.OwnerName, kerbalName, StringComparison.Ordinal)) continue;
                if (slot.Chain.Contains(kerbalName)) return slot.OwnerName;
            }
            return null;
        }

        private bool ShouldEnsureChainEntryInRoster(KerbalSlot slot, int chainIndex)
        {
            if (slot == null || chainIndex < 0 || chainIndex >= slot.Chain.Count)
                return false;

            string standIn = slot.Chain[chainIndex];
            bool isReserved = !string.IsNullOrEmpty(standIn) && IsReservedNow(standIn);
            bool usedInRecording = !string.IsNullOrEmpty(standIn) && IsKerbalInAnyRecording(standIn);

            // Displaced, unused chain metadata stays persisted but should not force a
            // roster entry back into existence on every recalculation walk. Retired
            // stand-ins still need a roster entry so they remain visible/managed.
            return !IsDisplacedChainEntry(slot, chainIndex) || isReserved || usedInRecording;
        }

        internal int GetActiveChainIndex(string slotOwnerName, KerbalSlot slot)
        {
            return ResolveActiveChainIndex(slotOwnerName, slot, IsReservedNow);
        }

        /// <summary>
        /// The chain-occupant decision, with the live reservation map behind a predicate
        /// so it is a pure function of its inputs.
        ///
        /// <para>Extracted with NO behaviour change - the instance overload above is now a
        /// one-line delegation - so a caller that has reservations but no
        /// <c>KerbalsModule</c> instance reaches THIS decision rather than a copy of it.
        /// The GUI state gallery is that caller: its synthetic roster states classify
        /// chain members through the real rule, which is what makes an <c>active</c> /
        /// <c>displaced</c> / <c>retired</c> chain line in a mocked capture a picture the
        /// product can actually produce.</para>
        /// </summary>
        /// <param name="isReserved">Whether a kerbal name currently holds a reservation.
        /// Null is read as "nothing is reserved", which answers
        /// <see cref="ActiveOwnerIndex"/> - the owner is in his own seat.</param>
        internal static int ResolveActiveChainIndex(
            string slotOwnerName, KerbalSlot slot, Func<string, bool> isReserved)
        {
            if (slot != null && slot.OwnerPermanentlyGone)
                return NoActiveChainOccupant;

            if (isReserved == null || !isReserved(slotOwnerName))
                return ActiveOwnerIndex;

            if (slot == null)
                return NoActiveChainOccupant;

            // Follow the reserved prefix. The first free stand-in reclaims; any deeper
            // entries are displaced metadata until the earlier occupant becomes reserved again.
            for (int i = 0; i < slot.Chain.Count; i++)
            {
                string standIn = slot.Chain[i];
                if (standIn == null || !isReserved(standIn))
                    return i;
            }

            return slot.Chain.Count;
        }

        internal int GetActiveChainIndex(KerbalSlot slot)
        {
            if (slot == null)
                return NoActiveChainOccupant;

            return GetActiveChainIndex(slot.OwnerName, slot);
        }

        private bool IsDisplacedChainEntry(KerbalSlot slot, int chainIndex)
        {
            if (slot == null || chainIndex < 0 || chainIndex >= slot.Chain.Count)
                return false;

            if (slot.OwnerPermanentlyGone)
                return true;

            int activeIndex = GetActiveChainIndex(slot);
            if (activeIndex == NoActiveChainOccupant)
                return false;
            if (activeIndex == ActiveOwnerIndex)
                return true;
            if (activeIndex >= slot.Chain.Count)
                return false;

            return chainIndex > activeIndex;
        }
    }
}
