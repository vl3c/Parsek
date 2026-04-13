using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Kerbal lifecycle logic: infers per-crew end states from recording data,
    /// computes reservations and replacement chains, populates CrewEndStates on
    /// recordings at commit time. Implements IResourceModule to participate in the
    /// RecalculationEngine walk lifecycle (Reset → PrePass → ProcessAction → PostWalk).
    ///
    /// Static utility methods (InferCrewEndState, PopulateCrewEndStates, FindTraitForKerbal)
    /// are pure functions with no module state dependency.
    /// </summary>
    internal class KerbalsModule : IResourceModule
    {
        private const string Tag = "KerbalsModule";

        // ── Derived state (recomputed on every recalculation walk) ──
        private Dictionary<string, KerbalReservation> reservations
            = new Dictionary<string, KerbalReservation>();
        private HashSet<string> retiredKerbals = new HashSet<string>();

        /// <summary>
        /// Set of all crew names appearing in any active committed recording.
        /// Built during ProcessAction for O(1) lookups in ComputeRetiredSet
        /// and IsKerbalInAnyRecording. Excludes loop and disabled-chain recordings.
        /// </summary>
        private HashSet<string> allRecordingCrew = new HashSet<string>();

        // ── Recording metadata cache (built in PrePass) ──
        private Dictionary<string, RecordingMeta> recordingMeta
            = new Dictionary<string, RecordingMeta>();
        private HashSet<string> loopingChainIds = new HashSet<string>();

        // ── Persisted state (stand-in names survive recalculation) ──
        private Dictionary<string, KerbalSlot> slots
            = new Dictionary<string, KerbalSlot>();

        /// <summary>
        /// Per-recording metadata cached during PrePass from RecordingStore.
        /// Uses double EndUT to avoid float precision loss from GameAction.EndUT.
        /// </summary>
        private struct RecordingMeta
        {
            public bool IsLoop;
            public bool IsChainRecording;
            public string ChainId;
            public bool IsDisabledChain;
            public double EndUT;
        }

        /// <summary>
        /// Per-kerbal reservation. Derived — never persisted directly.
        /// </summary>
        internal class KerbalReservation
        {
            public string KerbalName;
            public double ReservedUntilUT;  // double.PositiveInfinity for permanent/open-ended
            public bool IsPermanent;        // Dead — never freed
        }

        /// <summary>
        /// Per-slot replacement chain. Slot name = original kerbal.
        /// Persisted in KERBAL_SLOTS ConfigNode for name stability.
        /// </summary>
        internal class KerbalSlot
        {
            public string OwnerName;
            public string OwnerTrait;       // "Pilot" / "Engineer" / "Scientist"
            public bool OwnerPermanentlyGone;
            public List<string> Chain = new List<string>(); // stand-in names, ordered by depth
        }

        // Read-only access for tests
        internal IReadOnlyDictionary<string, KerbalReservation> Reservations => reservations;
        internal IReadOnlyDictionary<string, KerbalSlot> Slots => slots;
        internal IReadOnlyCollection<string> RetiredKerbals => retiredKerbals;

        /// <summary>
        /// Returns the list of retired kerbal names for UI display.
        /// Retired kerbals are stand-ins that were displaced by the original kerbal
        /// returning. They remain in the roster but are blocked from dismissal.
        /// Returns a snapshot — safe to enumerate while the module recalculates.
        /// </summary>
        internal IReadOnlyList<string> GetRetiredKerbals()
        {
            return new List<string>(retiredKerbals);
        }

        // ────────────────────────────────────────────────────────
        // IResourceModule implementation
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Clears all derived state before a recalculation walk.
        /// Does NOT clear slots — stand-in names persist across walks (loaded via LoadSlots).
        /// </summary>
        public void Reset()
        {
            reservations.Clear();
            retiredKerbals.Clear();
            allRecordingCrew.Clear();
            recordingMeta.Clear();
            loopingChainIds.Clear();
        }

        /// <summary>
        /// Builds recording metadata cache from RecordingStore.CommittedRecordings.
        /// This is necessary because loop/disabled/chain status is mutable (users can
        /// toggle at runtime) and is not encoded in GameAction fields.
        /// </summary>
        public void PrePass(List<GameAction> actions)
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                bool isLoop = rec.LoopPlayback;
                bool isChain = rec.IsChainRecording;
                string chainId = rec.ChainId;
                bool isDisabled = RecordingStore.IsChainFullyDisabled(chainId);

                recordingMeta[rec.RecordingId] = new RecordingMeta
                {
                    IsLoop = isLoop,
                    IsChainRecording = isChain,
                    ChainId = chainId,
                    IsDisabledChain = isDisabled,
                    EndUT = rec.EndUT
                };

                // Identify chains that contain a looping segment
                if (isLoop && isChain && !string.IsNullOrEmpty(chainId))
                    loopingChainIds.Add(chainId);
            }

            ParsekLog.Verbose(Tag,
                $"PrePass: cached {recordingMeta.Count} recording metadata entries, " +
                $"{loopingChainIds.Count} looping chains");
        }

        /// <summary>
        /// Processes a KerbalAssignment action to build crew reservations.
        /// Ignores all other action types.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null || action.Type != GameActionType.KerbalAssignment)
                return;

            if (string.Equals(action.KerbalRole, "Tourist", System.StringComparison.OrdinalIgnoreCase))
                return;

            string recordingId = action.RecordingId;
            if (string.IsNullOrEmpty(recordingId)) return;

            // Look up recording metadata (skip if not found — orphaned action)
            RecordingMeta meta;
            if (!recordingMeta.TryGetValue(recordingId, out meta))
                return;

            // Skip loop recordings
            if (meta.IsLoop) return;

            // Skip disabled chain recordings
            if (meta.IsDisabledChain) return;

            string name = action.KerbalName;
            if (string.IsNullOrEmpty(name)) return;

            // Build the all-crew set for O(1) lookup in ComputeRetiredSet
            allRecordingCrew.Add(name);

            KerbalEndState endState = action.KerbalEndStateField;

            // Map end states to reservation parameters:
            //   Dead      -> permanent, endUT = infinity
            //   Recovered -> temporary, endUT = rec.EndUT
            //   Aboard    -> open-ended temporary, endUT = infinity (crew still on vessel)
            //   Unknown   -> open-ended temporary (conservative)
            // Override: if this recording belongs to a chain with a looping segment,
            // keep endUT = infinity regardless of Recovered state — the ghost replays
            // past the tip's EndUT via the loop.
            bool permanent = (endState == KerbalEndState.Dead);
            bool chainHasLoop = meta.IsChainRecording
                && !string.IsNullOrEmpty(meta.ChainId)
                && loopingChainIds.Contains(meta.ChainId);
            // Use recording's double-precision EndUT (not action's float EndUT)
            double endUT = (endState == KerbalEndState.Recovered && !chainHasLoop)
                ? meta.EndUT : double.PositiveInfinity;

            KerbalReservation existing;
            if (reservations.TryGetValue(name, out existing))
            {
                // Merge: take max endUT, permanent wins
                if (permanent) existing.IsPermanent = true;
                if (endUT > existing.ReservedUntilUT) existing.ReservedUntilUT = endUT;
                ParsekLog.Verbose(Tag,
                    $"Reservation extended: '{name}' endUT->{existing.ReservedUntilUT:F1} " +
                    $"(permanent={existing.IsPermanent})");
            }
            else
            {
                reservations[name] = new KerbalReservation
                {
                    KerbalName = name,
                    ReservedUntilUT = endUT,
                    IsPermanent = permanent
                };
                ParsekLog.Verbose(Tag,
                    $"Reservation: '{name}' endUT={( permanent ? "INDEFINITE" : endUT.ToString("F1") )} " +
                    $"({endState}{(chainHasLoop ? ", chainHasLoop" : "")}), recording '{recordingId}'");
            }
        }

        /// <summary>
        /// Post-walk: builds replacement chains and computes retired set.
        /// Must run after all ProcessAction calls (needs complete reservation dict).
        /// </summary>
        public void PostWalk()
        {
            // Permanent-loss state is derived from the current reservation walk, not
            // sticky historical state. Rebuild it each pass from the current timeline.
            foreach (var slot in slots.Values)
                slot.OwnerPermanentlyGone = false;

            // 1. Build/update chains for temporary reservations
            foreach (var kvp in reservations)
            {
                if (kvp.Value.IsPermanent)
                {
                    // Permanent: slot exits chain system. Mark owner as gone.
                    KerbalSlot permanentSlot;
                    if (slots.TryGetValue(kvp.Key, out permanentSlot))
                        permanentSlot.OwnerPermanentlyGone = true;
                    continue;
                }

                // Ensure slot exists
                KerbalSlot slot;
                if (!slots.TryGetValue(kvp.Key, out slot))
                {
                    slot = new KerbalSlot
                    {
                        OwnerName = kvp.Key,
                        OwnerTrait = FindTraitForKerbal(kvp.Key),
                    };
                    slots[kvp.Key] = slot;
                    ParsekLog.Verbose(Tag,
                        $"Created slot for '{kvp.Key}' (trait={slot.OwnerTrait})");
                }

                // Walk chain: ensure each reserved level has a stand-in
                EnsureChainDepth(slot);
            }

            // 2. Identify retired stand-ins
            ComputeRetiredSet();

            // 3. Log summary
            ParsekLog.Info(Tag,
                $"Recalculation complete: {reservations.Count} reservations, " +
                $"{slots.Count} slots, {retiredKerbals.Count} retired");
        }

        // ────────────────────────────────────────────────────────
        // End-state inference (static utilities — no module state)
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Infers the end state for a single crew member based on the recording's
        /// terminal state and whether the crew member is present in the end-of-recording
        /// vessel snapshot.
        ///
        /// Decision table:
        ///   TerminalState == null           -> Unknown (recording still active or legacy)
        ///   TerminalState == Destroyed      -> Dead (vessel destroyed with crew aboard)
        ///   TerminalState == Recovered      -> Recovered
        ///   TerminalState == Boarded        -> if in snapshot: Aboard, else Unknown
        ///   TerminalState == Docked         -> if in snapshot: Aboard, else Unknown
        ///   TerminalState is intact state   -> if in snapshot: Aboard, else Dead (EVA'd and lost)
        ///   (Orbiting/Landed/Splashed/SubOrbital)
        /// </summary>

        /// <summary>
        /// Replaces stand-in names in a crew list with their original kerbal names (#254).
        /// The replacements dict maps original→stand-in; this reverses the lookup.
        /// </summary>
        internal static void ReverseMapCrewNames(List<string> crew,
            IReadOnlyDictionary<string, string> replacements, string vesselNameForLog)
        {
            for (int i = 0; i < crew.Count; i++)
            {
                string originalName = null;
                foreach (var kvp in replacements)
                {
                    if (kvp.Value == crew[i])
                    {
                        originalName = kvp.Key;
                        break;
                    }
                }

                if (originalName == null)
                    originalName = TryReverseMapCrewNameFromSlots(crew[i]);

                if (originalName == null)
                    continue;

                if (vesselNameForLog != null)
                {
                    ParsekLog.Info(Tag,
                        $"PopulateCrewEndStates: reverse-mapped stand-in '{crew[i]}' " +
                        $"back to original '{originalName}' in recording '{vesselNameForLog}'");
                }

                crew[i] = originalName;
            }
        }

        private static string TryReverseMapCrewNameFromSlots(string crewName)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            var slotsMap = kerbals != null ? kerbals.Slots : null;
            if (slotsMap == null || string.IsNullOrEmpty(crewName))
                return null;

            foreach (var slot in slotsMap.Values)
            {
                if (slot == null || string.IsNullOrEmpty(slot.OwnerName) || slot.Chain == null)
                    continue;

                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    if (string.Equals(slot.Chain[i], crewName, System.StringComparison.Ordinal))
                        return slot.OwnerName;
                }
            }

            return null;
        }

        internal static KerbalEndState InferCrewEndState(
            string crewName,
            TerminalState? terminalState,
            HashSet<string> snapshotCrew)
        {
            // No terminal state -> recording still active or legacy data
            if (!terminalState.HasValue)
            {
                ParsekLog.Verbose(Tag,
                    $"InferCrewEndState: crew='{crewName}' terminalState=null -> Unknown (no terminal state)");
                return KerbalEndState.Unknown;
            }

            var ts = terminalState.Value;

            // Vessel destroyed -> all crew aboard are dead
            if (ts == TerminalState.Destroyed)
            {
                ParsekLog.Verbose(Tag,
                    $"InferCrewEndState: crew='{crewName}' terminalState=Destroyed -> Dead");
                return KerbalEndState.Dead;
            }

            // Vessel recovered -> all crew are recovered
            if (ts == TerminalState.Recovered)
            {
                ParsekLog.Verbose(Tag,
                    $"InferCrewEndState: crew='{crewName}' terminalState=Recovered -> Recovered");
                return KerbalEndState.Recovered;
            }

            bool inSnapshot = snapshotCrew != null && snapshotCrew.Contains(crewName);

            // Boarded/Docked -> crew transferred to another vessel
            // If still in snapshot: aboard this vessel. If not: transferred (unknown where).
            if (ts == TerminalState.Boarded || ts == TerminalState.Docked)
            {
                var result = inSnapshot ? KerbalEndState.Aboard : KerbalEndState.Unknown;
                ParsekLog.Verbose(Tag,
                    $"InferCrewEndState: crew='{crewName}' terminalState={ts} inSnapshot={inSnapshot} -> {result}");
                return result;
            }

            // Intact terminal states: Orbiting, Landed, Splashed, SubOrbital
            // If in snapshot: aboard. If not: EVA'd and lost -> Dead.
            {
                var result = inSnapshot ? KerbalEndState.Aboard : KerbalEndState.Dead;
                ParsekLog.Verbose(Tag,
                    $"InferCrewEndState: crew='{crewName}' terminalState={ts} inSnapshot={inSnapshot} -> {result}");
                return result;
            }
        }

        /// <summary>
        /// Populates CrewEndStates on a recording by extracting crew from the
        /// ghost visual snapshot (start-of-recording crew roster) and inferring
        /// each crew member's end state.
        /// </summary>
        internal static void PopulateCrewEndStates(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "PopulateCrewEndStates: null recording -- skipping");
                return;
            }

            bool hasStartCrewSource = rec.GhostVisualSnapshot != null || !string.IsNullOrEmpty(rec.EvaCrewName);

            // Extract starting crew from ghost visual snapshot (recording-start state)
            var startingCrew = CrewReservationManager.ExtractCrewFromSnapshot(rec.GhostVisualSnapshot);

            // EVA kerbals: the vessel IS the kerbal. Snapshot crew extraction returns
            // empty because EVA ConfigNode structure has no PART/crew values.
            // Fall back to the EvaCrewName field set at branch time.
            if (startingCrew.Count == 0 && !string.IsNullOrEmpty(rec.EvaCrewName))
                startingCrew.Add(rec.EvaCrewName);

            if (startingCrew.Count == 0)
            {
                if (!hasStartCrewSource)
                {
                    ParsekLog.Verbose(Tag,
                        $"PopulateCrewEndStates: recording='{rec.VesselName}' (id={rec.RecordingId}) " +
                        "has no start crew source -- leaving unresolved");
                    return;
                }

                rec.CrewEndStatesResolved = true;
                ParsekLog.Verbose(Tag,
                    $"PopulateCrewEndStates: recording='{rec.VesselName}' (id={rec.RecordingId}) " +
                    "has no crew in ghost snapshot -- resolved");
                return;
            }

            // Reverse-map stand-in names back to originals (#254). The recording snapshot
            // may contain stand-in kerbals (e.g., Leia instead of Jeb) if a prior recording
            // committed and swapped crew on the live vessel. Without this reverse-map, the
            // stand-in gets reserved too, triggering a cascading chain of replacements.
            var replacements = CrewReservationManager.CrewReplacements;
            ReverseMapCrewNames(startingCrew, replacements, rec.VesselName);

            // Extract end-of-recording crew from vessel snapshot (if available)
            var endCrew = CrewReservationManager.ExtractCrewFromSnapshot(rec.VesselSnapshot);
            ReverseMapCrewNames(endCrew, replacements, null);
            var endCrewSet = new HashSet<string>(endCrew);
            bool useGhostOnlyChainHandoffFallback = ShouldUseGhostOnlyChainHandoffEndState(rec);

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            int aboardCount = 0, deadCount = 0, recoveredCount = 0, unknownCount = 0;

            for (int i = 0; i < startingCrew.Count; i++)
            {
                string name = startingCrew[i];
                var state = useGhostOnlyChainHandoffFallback
                    ? InferGhostOnlyChainHandoffEndState(rec.TerminalStateValue)
                    : InferCrewEndState(name, rec.TerminalStateValue, endCrewSet);
                rec.CrewEndStates[name] = state;

                switch (state)
                {
                    case KerbalEndState.Aboard: aboardCount++; break;
                    case KerbalEndState.Dead: deadCount++; break;
                    case KerbalEndState.Recovered: recoveredCount++; break;
                    case KerbalEndState.Unknown: unknownCount++; break;
                }
            }

            ParsekLog.Info(Tag,
                $"PopulateCrewEndStates: recording='{rec.VesselName}' (id={rec.RecordingId}) " +
                $"crew={startingCrew.Count} aboard={aboardCount} dead={deadCount} " +
                $"recovered={recoveredCount} unknown={unknownCount}");
            rec.CrewEndStatesResolved = true;
        }

        /// <summary>
        /// Batch overload: populates CrewEndStates on all recordings in a list.
        /// Skips recordings that already have CrewEndStates populated.
        /// </summary>
        internal static void PopulateCrewEndStates(List<Recording> recordings)
        {
            if (recordings == null)
            {
                ParsekLog.Verbose(Tag, "PopulateCrewEndStates(batch): null list -- skipping");
                return;
            }

            int populated = 0;
            int skipped = 0;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.CrewEndStatesResolved || rec.CrewEndStates != null)
                {
                    skipped++;
                    continue;
                }
                PopulateCrewEndStates(rec);
                if (rec.CrewEndStates != null)
                    populated++;
            }

            ParsekLog.Info(Tag,
                $"PopulateCrewEndStates(batch): processed={recordings.Count} populated={populated} skipped={skipped}");
        }

        // ────────────────────────────────────────────────────────
        // Chain and reservation helpers (instance — access module state)
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Ensure the chain has a stand-in at every depth where the occupant is reserved.
        /// Stand-in names are reused from existing chain entries (deterministic).
        /// New names are generated only when a new depth is needed.
        /// </summary>
        private void EnsureChainDepth(KerbalSlot slot)
        {
            // Start with the owner. If reserved, need a stand-in at depth 0.
            // If that stand-in is also reserved, need depth 1, etc.
            string currentOccupant = slot.OwnerName;
            int depth = 0;

            while (reservations.ContainsKey(currentOccupant))
            {
                if (depth >= slot.Chain.Count)
                {
                    // Need a new stand-in at this depth -- will be created by ApplyToRoster
                    // For now, mark as needing generation (name = null)
                    slot.Chain.Add(null); // placeholder -- ApplyToRoster fills with real name
                    ParsekLog.Verbose(Tag,
                        $"Chain depth {depth} needed for slot '{slot.OwnerName}' -- pending generation");
                }

                currentOccupant = slot.Chain[depth];
                if (currentOccupant == null) break; // pending generation, stop walking
                depth++;
            }
        }

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

                    bool isReserved = reservations.ContainsKey(standIn);
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

        /// <summary>
        /// Find the experience trait for a kerbal by checking KSP roster (if available).
        /// Falls back to "Pilot" if not found or outside KSP runtime.
        /// </summary>
        internal static string FindTraitForKerbal(string kerbalName)
        {
            // In production, read from KSP roster if available.
            // Outside KSP (tests), HighLogic won't exist — fall back to "Pilot".
            try
            {
                var roster = HighLogic.CurrentGame?.CrewRoster;
                if (roster != null)
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == kerbalName)
                            return pcm.experienceTrait?.TypeName ?? "Pilot";
                    }
                }
            }
            catch
            {
                // HighLogic not available (unit test environment)
            }

            var baselines = GameStateStore.Baselines;
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
        /// Check if a kerbal is available for a new recording.
        /// A kerbal is available if they are NOT in the reservations dict.
        /// </summary>
        internal bool IsKerbalAvailable(string kerbalName)
        {
            bool reserved = reservations.ContainsKey(kerbalName);
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
        /// Used by CrewDialogFilterPatch.
        /// </summary>
        internal bool ShouldFilterFromCrewDialog(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            return reservations.ContainsKey(kerbalName)
                || retiredKerbals.Contains(kerbalName);
        }

        /// <summary>
        /// Check if a kerbal is managed by Parsek (reserved, active stand-in, or retired).
        /// </summary>
        internal bool IsManaged(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            if (reservations.ContainsKey(kerbalName)) return true;
            if (retiredKerbals.Contains(kerbalName)) return true;

            // Check if they're a stand-in in any chain
            foreach (var slot in slots.Values)
            {
                if (slot.Chain.Contains(kerbalName)) return true;
            }
            return false;
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

        // ────────────────────────────────────────────────────────
        // ApplyToRoster — KSP state mutations
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Apply derived kerbal state to the KSP roster. Creates stand-ins,
        /// removes unused displaced stand-ins, and populates the crewReplacements
        /// dict for SwapReservedCrewInFlight.
        ///
        /// Reserved kerbals are left at their natural rosterStatus (typically
        /// Available). CrewDialogFilterPatch prevents them from appearing in the
        /// VAB/SPH crew assignment dialog. KerbalDismissalPatch prevents dismissal.
        ///
        /// MIA Respawn: If KSP respawns a Dead kerbal to Available, the crew
        /// dialog filter still hides them (they remain in the reservations dict).
        /// No rosterStatus manipulation needed.
        ///
        /// Must be called AFTER PostWalk().
        /// Wraps all mutations in SuppressCrewEvents.
        /// </summary>
        internal void ApplyToRoster(KerbalRoster roster)
        {
            if (roster == null)
            {
                ParsekLog.Verbose(Tag, "ApplyToRoster: no roster — skipping");
                return;
            }

            using (SuppressionGuard.Crew())
            {
                int standInsCreated = 0, standInsRecreated = 0;
                int deletedUnused = 0, retiredDisplaced = 0;

                // Step 1: Create missing stand-ins
                foreach (var kvp in slots)
                {
                    var slot = kvp.Value;
                    for (int i = 0; i < slot.Chain.Count; i++)
                    {
                        if (!ShouldEnsureChainEntryInRoster(slot, i))
                            continue;

                        if (slot.Chain[i] != null)
                        {
                            // Verify stand-in still exists in roster
                            if (FindInRoster(roster, slot.Chain[i]) == null)
                            {
                                // Stand-in was removed (e.g., KSP cleanup) — recreate
                                var created = CreateStandIn(roster, slot.OwnerTrait, slot.Chain[i]);
                                if (created == null)
                                    ParsekLog.Warn(Tag,
                                        $"Failed to recreate stand-in '{slot.Chain[i]}'");
                                else
                                {
                                    standInsRecreated++;
                                    ParsekLog.Info(Tag,
                                        $"Recreated stand-in '{slot.Chain[i]}' ({slot.OwnerTrait}) " +
                                        $"for slot '{slot.OwnerName}' depth {i}");
                                }
                            }
                            continue;
                        }

                        // Null entry = pending generation (new depth from PostWalk)
                        ProtoCrewMember newStandIn = roster.GetNewKerbal(
                            ProtoCrewMember.KerbalType.Crew);
                        if (newStandIn != null)
                        {
                            KerbalRoster.SetExperienceTrait(newStandIn, slot.OwnerTrait);
                            slot.Chain[i] = newStandIn.name;
                            standInsCreated++;
                            ParsekLog.Info(Tag,
                                $"Stand-in generated: '{newStandIn.name}' ({slot.OwnerTrait}) " +
                                $"for slot '{slot.OwnerName}' depth {i}");
                        }
                        else
                        {
                            ParsekLog.Warn(Tag,
                                $"Failed to generate stand-in for slot '{slot.OwnerName}' depth {i}");
                        }
                    }
                }

                // Step 2: Remove unused displaced stand-ins from roster
                foreach (var kvp in slots)
                {
                    var slot = kvp.Value;

                    // Delete unused displaced stand-ins, but keep chain metadata so
                    // later rewinds/recalculations can deterministically reuse names
                    // and derive retirement from the remaining timeline.
                    for (int i = slot.Chain.Count - 1; i >= 0; i--)
                    {
                        string standIn = slot.Chain[i];
                        if (standIn == null) continue;

                        bool isReserved = reservations.ContainsKey(standIn);
                        if (!IsDisplacedChainEntry(slot, i) || isReserved)
                            continue;

                        bool usedInRecording = IsKerbalInAnyRecording(standIn);
                        if (usedInRecording)
                        {
                            // Retired — keep in roster (filtered from crew dialog)
                            retiredDisplaced++;
                            ParsekLog.Info(Tag,
                                $"Stand-in '{standIn}' displaced -> retired (used in recording)");
                        }
                        else
                        {
                            // Unused — remove from roster entirely
                            var pcm = FindInRoster(roster, standIn);
                            if (pcm != null && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                            {
                                roster.Remove(pcm);
                                deletedUnused++;
                                ParsekLog.Info(Tag,
                                    $"Stand-in '{standIn}' displaced -> deleted (unused)");
                            }
                        }
                    }
                }

                // Step 3: Populate crewReplacements bridge (no rosterStatus changes —
                // CrewDialogFilterPatch handles crew dialog filtering)
                CrewReservationManager.ClearReplacementsInternal();

                foreach (var kvp in reservations)
                {
                    // Bridge to SwapReservedCrewInFlight: map reserved -> active occupant
                    string kerbalName = kvp.Key;
                    string occupant = GetActiveOccupant(kerbalName);
                    if (occupant != null)
                    {
                        CrewReservationManager.SetReplacement(kerbalName, occupant);
                    }
                }

                ParsekLog.Info(Tag,
                    $"ApplyToRoster complete: {slots.Count} slots, " +
                    $"{retiredKerbals.Count} retired, " +
                    $"{reservations.Count} reserved, " +
                    $"{standInsCreated} created, {standInsRecreated} recreated, " +
                    $"{deletedUnused} deleted, {retiredDisplaced} displaced");
            }
        }

        private static ProtoCrewMember FindInRoster(KerbalRoster roster, string name)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == name) return pcm;
            }
            return null;
        }

        private static ProtoCrewMember CreateStandIn(
            KerbalRoster roster, string trait, string existingName)
        {
            var pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
            if (pcm != null)
            {
                pcm.ChangeName(existingName);
                KerbalRoster.SetExperienceTrait(pcm, trait);
            }
            return pcm;
        }

        internal static bool ShouldUseGhostOnlyChainHandoffEndState(Recording rec)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.ChainId)
                && rec.VesselSnapshot == null
                && (rec.GhostVisualSnapshot != null || !string.IsNullOrEmpty(rec.EvaCrewName))
                && (!rec.TerminalStateValue.HasValue
                    || rec.TerminalStateValue == TerminalState.Boarded
                    || rec.TerminalStateValue == TerminalState.Destroyed
                    || rec.TerminalStateValue == TerminalState.Recovered);
        }

        internal static KerbalEndState InferGhostOnlyChainHandoffEndState(TerminalState? terminalState)
        {
            // Ghost-only chain segments end at an internal handoff, not at a final
            // spawn/resolution point. Keep their reservation finite so later committed
            // segments extend the chain instead of inheriting an indefinite Unknown.
            return terminalState == TerminalState.Destroyed
                ? KerbalEndState.Dead
                : KerbalEndState.Recovered;
        }

        private const int ActiveOwnerIndex = -1;
        private const int NoActiveChainOccupant = -2;

        private bool ShouldEnsureChainEntryInRoster(KerbalSlot slot, int chainIndex)
        {
            if (slot == null || chainIndex < 0 || chainIndex >= slot.Chain.Count)
                return false;

            string standIn = slot.Chain[chainIndex];
            bool isReserved = !string.IsNullOrEmpty(standIn) && reservations.ContainsKey(standIn);

            // Displaced, unreserved chain metadata stays persisted but should not force
            // a roster entry back into existence on every recalculation walk.
            return !IsDisplacedChainEntry(slot, chainIndex) || isReserved;
        }

        private int GetActiveChainIndex(string slotOwnerName, KerbalSlot slot)
        {
            if (slot != null && slot.OwnerPermanentlyGone)
                return NoActiveChainOccupant;

            if (!reservations.ContainsKey(slotOwnerName))
                return ActiveOwnerIndex;

            if (slot == null)
                return NoActiveChainOccupant;

            // Follow the reserved prefix. The first free stand-in reclaims; any deeper
            // entries are displaced metadata until the earlier occupant becomes reserved again.
            for (int i = 0; i < slot.Chain.Count; i++)
            {
                string standIn = slot.Chain[i];
                if (standIn == null || !reservations.ContainsKey(standIn))
                    return i;
            }

            return slot.Chain.Count;
        }

        private int GetActiveChainIndex(KerbalSlot slot)
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

        // ────────────────────────────────────────────────────────
        // Serialization: KERBAL_SLOTS
        // ────────────────────────────────────────────────────────

        internal void SaveSlots(ConfigNode parentNode)
        {
            if (slots.Count == 0)
            {
                ParsekLog.Verbose(Tag, "SaveSlots: no slots to save");
                return;
            }

            ConfigNode slotsNode = parentNode.AddNode("KERBAL_SLOTS");
            int chainEntryCount = 0;
            foreach (var kvp in slots)
            {
                var slot = kvp.Value;
                ConfigNode slotNode = slotsNode.AddNode("SLOT");
                slotNode.AddValue("owner", slot.OwnerName);
                slotNode.AddValue("trait", slot.OwnerTrait);
                if (slot.OwnerPermanentlyGone)
                    slotNode.AddValue("permanentlyGone", "True");
                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    if (slot.Chain[i] != null)
                    {
                        ConfigNode entry = slotNode.AddNode("CHAIN_ENTRY");
                        entry.AddValue("name", slot.Chain[i]);
                        chainEntryCount++;
                    }
                }
            }
            ParsekLog.Info(Tag, $"Saved {slots.Count} kerbal slot(s) with {chainEntryCount} chain entries");
        }

        internal void LoadSlots(ConfigNode parentNode)
        {
            slots.Clear();

            // Try new format first
            ConfigNode slotsNode = parentNode.GetNode("KERBAL_SLOTS");
            if (slotsNode != null)
            {
                ConfigNode[] slotNodes = slotsNode.GetNodes("SLOT");
                int chainEntryCount = 0;
                for (int i = 0; i < slotNodes.Length; i++)
                {
                    var slot = new KerbalSlot
                    {
                        OwnerName = slotNodes[i].GetValue("owner") ?? "",
                        OwnerTrait = slotNodes[i].GetValue("trait") ?? "Pilot",
                        OwnerPermanentlyGone = slotNodes[i].GetValue("permanentlyGone") == "True",
                        Chain = new List<string>()
                    };
                    ConfigNode[] entries = slotNodes[i].GetNodes("CHAIN_ENTRY");
                    for (int j = 0; j < entries.Length; j++)
                    {
                        string name = entries[j].GetValue("name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            slot.Chain.Add(name);
                            chainEntryCount++;
                        }
                    }
                    if (!string.IsNullOrEmpty(slot.OwnerName))
                        slots[slot.OwnerName] = slot;
                }
                ParsekLog.Info(Tag, $"Loaded {slots.Count} kerbal slot(s) with {chainEntryCount} chain entries from KERBAL_SLOTS");
                return;
            }

            // Backward compat: migrate from CREW_REPLACEMENTS
            ConfigNode replacementsNode = parentNode.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode != null)
            {
                ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
                for (int i = 0; i < entries.Length; i++)
                {
                    string original = entries[i].GetValue("original");
                    string replacement = entries[i].GetValue("replacement");
                    if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
                    {
                        // Existing flat format can't represent chains, so treat each as depth 0
                        if (!slots.ContainsKey(original))
                        {
                            slots[original] = new KerbalSlot
                            {
                                OwnerName = original,
                                OwnerTrait = "Pilot", // can't determine from old format
                                Chain = new List<string> { replacement }
                            };
                        }
                    }
                }
                ParsekLog.Info(Tag,
                    $"Migrated {slots.Count} slot(s) from legacy CREW_REPLACEMENTS");
                return;
            }

            ParsekLog.Verbose(Tag, "LoadSlots: no KERBAL_SLOTS or CREW_REPLACEMENTS found");
        }

        // ────────────────────────────────────────────────────────
        // Testing
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Reset all state for testing. Clears reservations, slots, retired set,
        /// and all caches.
        /// </summary>
        internal void ResetForTesting()
        {
            reservations.Clear();
            slots.Clear();
            retiredKerbals.Clear();
            allRecordingCrew.Clear();
            recordingMeta.Clear();
            loopingChainIds.Clear();
        }
    }
}
