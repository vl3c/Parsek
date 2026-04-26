using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Manages crew reservation and replacement for deferred vessel spawns.
    /// Reserves crew from committed recording snapshots so they can't be placed
    /// on new craft, hires replacements to keep the available pool constant,
    /// and swaps reserved crew out of the active flight vessel.
    /// </summary>
    internal static class CrewReservationManager
    {
        #region Static State

        // Maps reserved kerbal name → replacement kerbal name
        private static Dictionary<string, string> crewReplacements = new Dictionary<string, string>();

        /// <summary>
        /// Read-only access to current replacement mappings. For testing/diagnostics.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> CrewReplacements => crewReplacements;

        // ── #615 rescue-completion marker (P1 review follow-up) ────────────
        // Map kerbal name -> persistent id of the vessel onto which the
        // Parsek spawn pipeline's RescueReservedMissingCrewInSnapshot path
        // flipped them from Reserved+Missing to Available immediately before
        // ProtoVessel.Load placed them onto a Parsek-spawned vessel. This is
        // the rescue-specific signal the ApplyToRoster guard reads — it does
        // NOT fire for kerbals who happen to be on the active player vessel
        // without ever passing through the rescue path.
        //
        // Lifecycle (P1 review, fourth pass — pid-scoped marker):
        //   - Set by VesselSpawner (RespawnVessel / SpawnAtPosition) AFTER
        //     ProtoVessel.Load assigns a runtime persistentId. The rescue
        //     pre-load helper RescueReservedMissingCrewInSnapshot collects the
        //     names it flipped; the caller then calls MarkRescuePlaced(name,
        //     vesselPid) per name once the new vessel's pid is known.
        //   - The spawn path immediately calls UnreserveCrewInSnapshot on the
        //     same snapshot, which used to clear the marker through
        //     CleanUpReplacement. The marker MUST survive that step so the
        //     subsequent ApplyToRoster walk can read it. CleanUpReplacement
        //     no longer clears the marker.
        //   - PERSISTENT across ApplyToRoster walks. The reservation slot is
        //     rebuilt on every recalc walk while the historical chain entry
        //     survives in slot.Chain, so the guard must fire on EVERY
        //     subsequent ApplyToRoster pass for the lifetime of the rescue.
        //     RecalculateAndPatch fires from 14+ call sites (every commit,
        //     KSC spending event, vessel recovery, warp exit, scene
        //     transition, save load) — a one-shot-consume design fails on
        //     the very next trigger because the slot re-presents the kerbal
        //     as needing a stand-in and IsRescuePlaced=false routes the
        //     guard to the legitimate-recreate path.
        //   - Bulk-cleared by LoadCrewReplacements / RestoreReplacements /
        //     ClearReplacements / ResetReplacementsForTesting on session /
        //     rewind / wipe-all boundaries.
        //   - Pid-scoped (P1 review fourth pass): the previous design keyed
        //     the marker by name only and combined it with a generic
        //     IsKerbalOnLiveVessel check. That regressed when a stale marker
        //     from a long-past rescue suppressed a later UNRELATED fresh
        //     reservation for the same kerbal who happened to be on the
        //     active player vessel — the guard fired on the unrelated
        //     reservation and SwapReservedCrewInFlight had no stand-in to
        //     swap. Pid scoping makes the guard fire only when the kerbal is
        //     currently on the SAME vessel where the rescue placed them; if
        //     they have moved (player switched, fresh reservation, different
        //     rescue), the predicate is false and the legitimate-recreate
        //     path runs. Stale entries with a now-invalid pid never match a
        //     live vessel, so no per-vessel-destruction invalidation is
        //     needed.
        private static readonly Dictionary<string, ulong> rescuePlacedKerbals
            = new Dictionary<string, ulong>(System.StringComparer.Ordinal);

        /// <summary>
        /// Read-only access to the rescue-placed kerbal map (name -> pid).
        /// </summary>
        internal static IReadOnlyDictionary<string, ulong> RescuePlacedKerbals => rescuePlacedKerbals;

        /// <summary>
        /// True if <paramref name="kerbalName"/> was placed onto a
        /// Parsek-spawned vessel by the <c>VesselSpawner</c> rescue path
        /// (#608/#609). Used by <see cref="KerbalsModule.ApplyToRoster"/>
        /// (#615 P1 review) as the rescue-specific signal that the
        /// historical stand-in must NOT be recreated. Returns false for
        /// kerbals who are on the active player vessel without ever having
        /// passed through the rescue path — those are legitimate reservations
        /// awaiting their stand-in.
        ///
        /// <para>
        /// P1 review (fourth pass): the marker is pid-scoped under the hood;
        /// this overload returns true when ANY pid is associated with the
        /// name. Most call sites should use
        /// <see cref="TryGetRescuePlacedVessel"/> instead so the guard can
        /// check that the kerbal is currently on the SAME vessel where the
        /// rescue placed them.
        /// </para>
        /// </summary>
        internal static bool IsRescuePlaced(string kerbalName)
        {
            return !string.IsNullOrEmpty(kerbalName) && rescuePlacedKerbals.ContainsKey(kerbalName);
        }

        /// <summary>
        /// Pid-scoped accessor for the rescue-placed marker. Returns true and
        /// the pid of the rescue vessel when <paramref name="kerbalName"/>
        /// was rescue-placed in this session. The
        /// <see cref="KerbalsModule.ApplyToRoster"/> guard combines this with
        /// <see cref="KerbalsModule.IKerbalRosterFacade.IsKerbalOnVesselWithPid"/>
        /// so the guard fires only when the kerbal is currently on the same
        /// vessel where the rescue placed them.
        /// </summary>
        internal static bool TryGetRescuePlacedVessel(string kerbalName, out ulong vesselPersistentId)
        {
            if (string.IsNullOrEmpty(kerbalName))
            {
                vesselPersistentId = 0UL;
                return false;
            }
            return rescuePlacedKerbals.TryGetValue(kerbalName, out vesselPersistentId);
        }

        /// <summary>
        /// Mark <paramref name="kerbalName"/> as placed onto the Parsek-spawned
        /// vessel identified by <paramref name="vesselPersistentId"/>.
        /// Called from the <see cref="VesselSpawner"/> rescue path AFTER
        /// <c>ProtoVessel.Load</c> has assigned the new vessel its runtime
        /// persistentId, so the marker can be scoped to the actual vessel
        /// the kerbal was placed onto. Idempotent for the same pid; an
        /// existing marker is overwritten when re-marked with a different
        /// pid (a later rescue for the same kerbal supersedes the earlier
        /// one).
        /// </summary>
        internal static void MarkRescuePlaced(string kerbalName, ulong vesselPersistentId)
        {
            if (string.IsNullOrEmpty(kerbalName)) return;
            ulong existing;
            bool hadPrior = rescuePlacedKerbals.TryGetValue(kerbalName, out existing);
            rescuePlacedKerbals[kerbalName] = vesselPersistentId;
            if (!hadPrior)
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Marked rescue-placed: '{kerbalName}' vesselPid={vesselPersistentId} " +
                    "(#608/#609 rescue path; #615 guard signal — pid-scoped)");
            }
            else if (existing != vesselPersistentId)
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Re-marked rescue-placed: '{kerbalName}' vesselPid={vesselPersistentId} " +
                    $"(superseding prior pid={existing}; #615 pid-scoped marker)");
            }
        }

        /// <summary>
        /// Remove <paramref name="kerbalName"/> from the rescue-placed set.
        /// Used by bulk lifecycle paths (<see cref="LoadCrewReplacements"/>,
        /// <see cref="RestoreReplacements"/>, <see cref="ClearReplacements"/>,
        /// <see cref="ResetReplacementsForTesting"/>) and by the test fixture.
        ///
        /// <para>
        /// P1 review (third pass): per-name <see cref="CleanUpReplacement"/>
        /// does NOT call this. The Re-Fly spawn pipeline calls
        /// <see cref="VesselSpawner.RescueReservedMissingCrewInSnapshot"/>
        /// (which calls <see cref="MarkRescuePlaced"/>) and immediately
        /// follows with <see cref="UnreserveCrewInSnapshot"/> on the SAME
        /// snapshot, which would clear the marker through CleanUpReplacement
        /// before the next <see cref="KerbalsModule.ApplyToRoster"/> walk
        /// could observe it.
        /// </para>
        ///
        /// <para>
        /// The <see cref="KerbalsModule.ApplyToRoster"/> guard also does NOT
        /// clear the marker when it fires. The reservation slot is rebuilt on
        /// every recalc walk while <c>slot.Chain</c> persists the historical
        /// stand-in name, so the guard must observe the same marker on every
        /// subsequent walk for the lifetime of the rescue. The previous
        /// review pass installed a one-shot consume here that broke on the
        /// very next <see cref="LedgerOrchestrator.RecalculateAndPatch"/>
        /// trigger (warp exit, save load, scene transition, KSC spending,
        /// any of 14+ call sites): once the merge-tail walk consumed the
        /// marker, every subsequent walk took the "live-but-no-marker"
        /// branch and regenerated the stand-in.
        /// </para>
        ///
        /// <para>
        /// The marker is now cleared ONLY by the bulk lifecycle paths
        /// listed above, on session / rewind / wipe-all boundaries. Within
        /// a session it accumulates harmlessly because the
        /// <see cref="KerbalsModule.ApplyToRoster"/> predicate also requires
        /// the kerbal to be on a live vessel — a stale-true marker for a
        /// kerbal who is no longer on any vessel falls through to the
        /// legitimate-recreate path.
        /// </para>
        /// </summary>
        internal static void ClearRescuePlaced(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return;
            ulong removedPid;
            if (rescuePlacedKerbals.TryGetValue(kerbalName, out removedPid)
                && rescuePlacedKerbals.Remove(kerbalName))
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Cleared rescue-placed marker: '{kerbalName}' vesselPid={removedPid} (bulk lifecycle)");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Clean up replacement kerbals for crew in a snapshot.
        /// Call when discarding, recovering, or wiping recordings.
        ///
        /// No rosterStatus changes — reserved kerbals stay at their natural status
        /// (typically Available). CrewDialogFilterPatch handles crew dialog filtering.
        /// </summary>
        public static void UnreserveCrewInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null)
            {
                ParsekLog.Verbose("CrewReservation", "UnreserveCrewInSnapshot: null snapshot — skipping");
                return;
            }
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Verbose("CrewReservation", "UnreserveCrewInSnapshot: no crew roster — skipping");
                return;
            }

            using (SuppressionGuard.Crew())
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    foreach (string name in partNode.GetValues("crew"))
                    {
                        if (!string.IsNullOrEmpty(name))
                            CleanUpReplacement(name, roster);
                    }
                }
            }
        }

        internal static void ReserveCrewIn(ConfigNode snapshot, bool alreadySpawned, KerbalRoster roster)
        {
            if (snapshot == null || alreadySpawned)
                return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    bool found = false;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name != name) continue;
                        found = true;

                        // Skip dead crew — they're truly gone
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                            break;

                        // Rescue Missing crew — they're alive but orphaned from a
                        // removed vessel (e.g. --clean-start or manual save edits).
                        // The recording will respawn them, so restore them first.
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            CrewLog($"Rescued Missing crew '{name}' → Available for reservation");
                        }

                        // Hire a replacement kerbal so the available pool stays constant.
                        // This also handles crew who are already Assigned (e.g. on the pad
                        // vessel after a revert) — they still need a replacement so the
                        // swap can move them off the active vessel.
                        if (!crewReplacements.ContainsKey(name))
                        {
                            try
                            {
                                ProtoCrewMember replacement = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                                if (replacement != null)
                                {
                                    KerbalRoster.SetExperienceTrait(replacement, pcm.experienceTrait.TypeName);
                                    crewReplacements[name] = replacement.name;
                                    CrewLog($"Hired replacement '{replacement.name}' " +
                                        $"(trait: {pcm.experienceTrait.TypeName}) for reserved '{name}'");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                CrewLog($"Failed to hire replacement for '{name}': {ex.Message}");
                            }
                        }

                        break;
                    }
                    if (!found)
                        CrewWarn($" Crew '{name}' not found in roster during reservation");
                }
            }
        }

        /// <summary>
        /// Remove all Available replacement kerbals and clear the mapping.
        /// Called when wiping all recordings.
        /// </summary>
        public static void ClearReplacements()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                crewReplacements.Clear();
                rescuePlacedKerbals.Clear();
                return;
            }

            using (SuppressionGuard.Crew())
            {
                foreach (var kvp in new Dictionary<string, string>(crewReplacements))
                {
                    CleanUpReplacement(kvp.Key, roster);
                }

                crewReplacements.Clear();
                // #615 P1 review (third pass): load-bearing — neither the
                // per-name CleanUpReplacement nor the ApplyToRoster guard
                // clears per-name rescue markers (the spawn pipeline depends
                // on the marker surviving UnreserveCrewInSnapshot, and the
                // marker must persist across multiple recalc walks because
                // the slot is rebuilt every walk while slot.Chain survives).
                // ClearReplacements is a bulk wipe-all path, so we wipe the
                // marker set here too.
                rescuePlacedKerbals.Clear();
                CrewLog("Cleared all crew replacements (and rescue-placed marker set)");
            }
        }

        /// <summary>
        /// Swap reserved crew out of the active flight vessel, replacing them
        /// with their hired replacements. Prevents the player from recording
        /// with a reserved kerbal again after revert.
        ///
        /// Two passes (bug #277):
        ///   Pass 1: walk active vessel parts and swap any reserved kerbal currently
        ///           in a seat (the legacy path — handles the common in-pod case).
        ///   Pass 2: for any reservation whose original is NOT in the active vessel
        ///           (typically because the kerbal is on a separate EVA vessel), look
        ///           up the recording snapshot that originally seated them, find the
        ///           matching part in the active vessel by persistentId/name, and
        ///           place the stand-in into a free seat there. This catches the
        ///           common scenario where the player EVA'd one of the launch crew
        ///           before merge.
        ///
        /// Seat resolution lives at swap time (not at SetReplacement time) because
        /// SetReplacement runs on every commit/recalculate cycle and would pay the
        /// snapshot-walk cost on hot paths even when no orphan exists. The orphan
        /// pass only runs when the swap actually fails to place every replacement.
        /// </summary>
        public static int SwapReservedCrewInFlight()
        {
            // Pure-data short-circuit first: cheaper than touching FlightGlobals,
            // and lets unit tests reach this method without triggering the
            // FlightGlobals static cctor (which depends on Unity engine modules).
            if (crewReplacements.Count == 0)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no crew replacements — skipping");
                return 0;
            }
            if (FlightGlobals.ActiveVessel == null)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no active vessel — skipping");
                return 0;
            }

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no crew roster — skipping");
                return 0;
            }

            // Guard: skip crew swap entirely for Parsek-spawned vessels. Their crew
            // was definitively set by VesselSpawner (RemoveDeadCrewFromSnapshot,
            // RemoveSpecificCrewFromSnapshot for EVA'd crew, UnreserveCrewInSnapshot).
            // Swapping or orphan-placing into a spawned vessel is always wrong — it
            // fills empty seats that are intentionally empty (crew who EVA'd or died).
            // [Phase 3] ERS-routed: spawned-PID set is derived from the Effective
            // Recording Set so NotCommitted / superseded / session-suppressed
            // recordings no longer claim active-vessel spawn attribution.
            var spawnedPids = BuildSpawnedVesselPidSet(EffectiveState.ComputeERS());
            uint activePid = FlightGlobals.ActiveVessel.persistentId;
            if (spawnedPids.Contains(activePid))
            {
                ParsekLog.Info("CrewReservation",
                    $"SwapReservedCrewInFlight skipped: active vessel pid={activePid} " +
                    "is a Parsek-spawned vessel (crew already set by spawn path)");
                RemoveReservedEvaVessels(spawnedPids);
                return 0;
            }

            int swapCount = 0;
            int failCount = 0;
            var swappedOriginals = new HashSet<string>();

            // Pass 1 — legacy path: kerbals currently seated in the active vessel.
            foreach (Part part in FlightGlobals.ActiveVessel.parts)
            {
                // Iterate a copy because RemoveCrewmember modifies the list
                var crewList = new List<ProtoCrewMember>(part.protoModuleCrew);
                for (int i = 0; i < crewList.Count; i++)
                {
                    ProtoCrewMember original = crewList[i];
                    if (!crewReplacements.TryGetValue(original.name, out string replacementName))
                        continue;

                    // Find the replacement in the roster
                    ProtoCrewMember replacement = null;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == replacementName)
                        {
                            replacement = pcm;
                            break;
                        }
                    }

                    if (replacement == null)
                    {
                        CrewLog($"Cannot swap '{original.name}': replacement '{replacementName}' not in roster");
                        failCount++;
                        continue;
                    }

                    int seatIndex = part.protoModuleCrew.IndexOf(original);
                    if (seatIndex < 0)
                    {
                        CrewLog($"Cannot swap '{original.name}': not found in part crew list");
                        failCount++;
                        continue;
                    }
                    part.RemoveCrewmember(original);
                    part.AddCrewmemberAt(replacement, seatIndex);
                    swapCount++;
                    swappedOriginals.Add(original.name);
                    CrewLog($"Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }

            // Pass 2 — bug #277 orphan placement: reserved kerbals NOT seated in
            // the active vessel (typically EVA'd before merge). Look up where the
            // recording snapshot originally seated them and place the stand-in there.
            int orphanPlaced = PlaceOrphanedReplacements(roster, swappedOriginals);
            swapCount += orphanPlaced;

            if (swapCount > 0)
            {
                FlightGlobals.ActiveVessel.SpawnCrew();
                GameEvents.onVesselCrewWasModified.Fire(FlightGlobals.ActiveVessel);
                CrewLog($"Crew swap complete: {swapCount} succeeded" +
                    (failCount > 0 ? $", {failCount} failed" : "") +
                    " — refreshed vessel crew display");
            }
            else if (failCount > 0)
            {
                CrewLog($"Crew swap: 0 succeeded, {failCount} failed");
            }

            RemoveReservedEvaVessels(spawnedPids);

            return swapCount;
        }

        /// <summary>
        /// Bug #277 second pass: for each replacement whose original is NOT in
        /// the active vessel (e.g. because the original is on a separate EVA
        /// vessel that's about to be removed), look up the recording snapshot
        /// that originally seated them and place the stand-in into a matching
        /// part on the active vessel.
        ///
        /// Adds successfully-placed originals to <paramref name="swappedOriginals"/>
        /// so RemoveReservedEvaVessels can proceed cleanly afterwards.
        /// Returns the number of placements completed.
        ///
        /// `internal` rather than `private` so the in-game integration test
        /// (`Bug277_PlaceOrphanedReplacements_PlacesStandinFromSnapshot`) can
        /// invoke just the orphan-pass without triggering the surrounding
        /// SpawnCrew + RemoveReservedEvaVessels side effects.
        /// </summary>
        internal static int PlaceOrphanedReplacements(
            KerbalRoster roster, HashSet<string> swappedOriginals)
        {
            int placed = 0;
            int orphanCount = 0;

            // Distinct skip/fail counters (per PR #175 review): keep infrastructural
            // failures separated from placement-impossible cases for diagnostics.
            int rescuedFromMissing = 0;
            int skippedDeadOrMissingReplacement = 0;     // Warn: alarming
            int skippedReplacementNotInRoster = 0;       // Warn: alarming
            int skippedAlreadyOnActiveVessel = 0;        // Info: nothing to do, expected sometimes
            int skippedOriginalStillOnActiveVessel = 0;  // Info: defensive — Pass 1 didn't swap them but they're seated
            int skippedSnapshotMiss = 0;                 // Warn: orphan but no snapshot trail
            int skippedNoMatchingPart = 0;               // Warn: snapshot found but no live part

            // Bug #456 telemetry: count how each placement actually resolved the
            // live part — pid-hits vs name-hit fallbacks. A synthetic pid=100000
            // showcase ghost can never pid-match a freshly-launched real vessel
            // (KSP picks a random part pid at spawn), so name-hit fallbacks are
            // the only path for those cases. Surfaces in the summary line so
            // future playtests distinguish pid vs name resolution at a glance.
            int pidHits = 0;
            int nameHitFallbacks = 0;

            // Build the snapshot list once. Use GhostVisualSnapshot (recording-start
            // state) — VesselSnapshot is end-of-recording and would not contain a
            // crew member who EVA'd mid-recording.
            var snapshots = new List<ConfigNode>();
            // [Phase 3] ERS-routed: orphan crew placement walks visible recordings
            // (design §3.4 crew reservations reader). NotCommitted / superseded
            // recordings no longer contribute ghost snapshots.
            var committed = EffectiveState.ComputeERS();
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var snap = committed[i].GhostVisualSnapshot;
                    if (snap != null) snapshots.Add(snap);
                }
            }

            // Build the active-vessel crew name set ONCE up front (PR #175 review):
            // O(parts × crew) build, then O(1) per orphan lookup, vs the previous
            // O(parts × crew) per orphan. Negligible at small N but cleaner for
            // pathological multi-pod cases.
            var activeVesselCrewNames = BuildActiveVesselCrewNameSet();

            // Snapshot crew lists may contain stand-in names from earlier
            // recordings (see KerbalsModule.ReverseMapCrewNames). Pass the
            // current crewReplacements as the reverse-map source.
            foreach (var kvp in crewReplacements)
            {
                string originalName = kvp.Key;
                string replacementName = kvp.Value;

                if (swappedOriginals.Contains(originalName))
                    continue;

                orphanCount++;

                // Defensive guard (PR #175 review): a Pass 1 failCount path (e.g.
                // replacement-not-in-roster) leaves the original seated and out of
                // swappedOriginals, so Pass 2 would re-process it and potentially
                // double-place. Check directly against the active-vessel crew set
                // to short-circuit before snapshot scan.
                if (activeVesselCrewNames.Contains(originalName))
                {
                    CrewLog($"Orphan placement: '{originalName}' is still seated on the active vessel — skipping (Pass 1 left it unprocessed)");
                    skippedOriginalStillOnActiveVessel++;
                    continue;
                }

                // Resolve replacement in the roster — must exist, not Dead, and
                // not Missing (mirrors ReserveCrewIn's Missing-rescue pattern).
                ProtoCrewMember replacement = null;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (pcm.name == replacementName)
                    {
                        replacement = pcm;
                        break;
                    }
                }
                if (replacement == null)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: cannot place '{originalName}' → '{replacementName}': replacement not in roster");
                    skippedReplacementNotInRoster++;
                    continue;
                }
                if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: skipping '{originalName}' → '{replacementName}': replacement is Dead");
                    skippedDeadOrMissingReplacement++;
                    continue;
                }
                if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                {
                    // Mirrors ReserveCrewIn (CrewReservationManager.cs:84-88): a
                    // Missing reserved kerbal is alive but orphaned from a removed
                    // vessel. Rescue them by setting back to Available before
                    // placing them.
                    //
                    // Asymmetry note: this state mutation is NOT rolled back on a
                    // later failure (snapshot miss / no matching part / etc.).
                    // Available is a valid terminal state for an unused stand-in,
                    // and Missing was the broken state to begin with — leaving the
                    // kerbal Available regardless of placement outcome is strictly
                    // an improvement, not a regression. Other skip paths leave
                    // state untouched only because they have no state to fix.
                    replacement.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescuedFromMissing++;
                    CrewLog($"Orphan placement: rescued Missing replacement '{replacementName}' → Available before placement");
                }
                if (activeVesselCrewNames.Contains(replacementName))
                {
                    CrewLog($"Orphan placement: skipping '{originalName}' → '{replacementName}': replacement already on active vessel");
                    swappedOriginals.Add(originalName);
                    skippedAlreadyOnActiveVessel++;
                    continue;
                }

                // Find seat in committed snapshots.
                var seat = ResolveOrphanSeatFromSnapshots(originalName, snapshots, crewReplacements);
                if (!seat.Found)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: no snapshot contains original '{originalName}' " +
                        $"— stand-in '{replacementName}' left unplaced in roster");
                    skippedSnapshotMiss++;
                    continue;
                }

                // Find a matching part on the active vessel. Two-tier match
                // (PR #175 review → bug #456): persistentId → partInfo.name (prefer
                // the part with the FEWEST free seats, i.e. the tightest fit).
                // The previous tier-3 "any part with free capacity" fallback was
                // removed in PR #175 because a misplaced stand-in (e.g. dropped into
                // a passenger cabin instead of the command pod) is arguably worse
                // than an unplaced one and would silently mask the bug it's trying
                // to fix. The tightest-fit rule keeps that guarantee even when
                // multiple parts share the snapshot part name (the cockpit typically
                // has fewer seats than a passenger cabin).
                Part target = FindTargetPartForOrphan(
                    seat.PartPid, seat.PartName, out SeatMatchKind matchKind,
                    out SeatMatchMissDiagnostic missDiagnostic);
                if (target == null)
                {
                    LogOrphanPlacementDeferred(originalName, replacementName,
                        seat.PartPid, seat.PartName, missDiagnostic,
                        pidHits, nameHitFallbacks);
                    skippedNoMatchingPart++;
                    continue;
                }

                // Use the non-indexed AddCrewmember overload — KSP picks a free seat
                // in the part. Avoids the AddCrewmemberAt-on-empty-seat behavior
                // which is unverified for our case.
                target.AddCrewmember(replacement);
                placed++;
                swappedOriginals.Add(originalName);
                // Keep the local activeVesselCrewNames set in sync so a subsequent
                // orphan that maps to the same kerbal doesn't false-collide.
                activeVesselCrewNames.Add(replacementName);

                // Bug #456: call out which matcher tier actually fired. A name-hit
                // fallback is the signal that a synthetic-pid snapshot (typically
                // a showcase ghost with pid=100000) landed in a real vessel whose
                // KSP-assigned pid doesn't match — the placement still worked but
                // future playtests should be able to grep for this line to confirm
                // the fallback path is load-bearing.
                if (matchKind == SeatMatchKind.PidHit)
                {
                    pidHits++;
                    CrewLog($"Orphan placement: '{originalName}' → '{replacement.name}' " +
                        $"placed in part '{target.partInfo.title}' " +
                        $"(snapshot pid={seat.PartPid}, live pid={target.persistentId}, match=pid)");
                }
                else
                {
                    nameHitFallbacks++;
                    ParsekLog.Info("CrewReservation",
                        $"Orphan placement: '{originalName}' → '{replacement.name}' " +
                        $"placed in part '{target.partInfo.title}' " +
                        $"(snapshot pid={seat.PartPid} name='{seat.PartName}', " +
                        $"live pid={target.persistentId}, match=name-fallback)");
                }
            }

            if (orphanCount > 0)
            {
                CrewLog($"Orphan placement pass: orphans={orphanCount} placed={placed} " +
                    $"pidHits={pidHits} nameHitFallbacks={nameHitFallbacks} " +
                    $"rescuedFromMissing={rescuedFromMissing} " +
                    $"skippedReplacementNotInRoster={skippedReplacementNotInRoster} " +
                    $"skippedDeadOrMissingReplacement={skippedDeadOrMissingReplacement} " +
                    $"skippedAlreadyOnActiveVessel={skippedAlreadyOnActiveVessel} " +
                    $"skippedOriginalStillOnActiveVessel={skippedOriginalStillOnActiveVessel} " +
                    $"skippedSnapshotMiss={skippedSnapshotMiss} " +
                    $"skippedNoMatchingPart={skippedNoMatchingPart}");
            }

            return placed;
        }

        /// <summary>
        /// Build a set of crew names currently seated on the active vessel.
        /// O(parts × crew) once, then O(1) lookups inside the orphan loop
        /// (PR #175 review).
        /// </summary>
        private static HashSet<string> BuildActiveVesselCrewNameSet()
        {
            var set = new HashSet<string>();
            var av = FlightGlobals.ActiveVessel;
            if (av == null) return set;
            for (int p = 0; p < av.parts.Count; p++)
            {
                var crew = av.parts[p].protoModuleCrew;
                for (int c = 0; c < crew.Count; c++)
                {
                    var pcm = crew[c];
                    if (pcm != null && !string.IsNullOrEmpty(pcm.name))
                        set.Add(pcm.name);
                }
            }
            return set;
        }

        /// <summary>
        /// Walks the active vessel looking for a part to place an orphan stand-in into.
        /// Two-tier match (PR #175 review → bug #456): persistentId → partInfo.name
        /// + free seat, preferring the part with the FEWEST free seats (tightest fit).
        ///
        /// The tier-3 "any free seat" match was removed in PR #175 because a
        /// misplaced stand-in (e.g. dropped into a passenger cabin instead of the
        /// command pod) is arguably worse than an unplaced one and would silently
        /// mask the bug we're fixing. Bug #456 adds the tightest-fit rule on the
        /// tier-2 name match: when multiple parts share the snapshot part name,
        /// pick the one with the fewest free seats so single-seat cockpits win
        /// over multi-seat passenger cabins.
        ///
        /// Returns null if no matching part has a free seat.
        /// </summary>
        private static Part FindTargetPartForOrphan(
            uint snapshotPartPid,
            string snapshotPartName,
            out SeatMatchKind matchKind,
            out SeatMatchMissDiagnostic missDiagnostic)
        {
            matchKind = SeatMatchKind.None;
            missDiagnostic = default(SeatMatchMissDiagnostic);
            var av = FlightGlobals.ActiveVessel;
            if (av == null) return null;

            // Build a lightweight snapshot of the live vessel's parts so the
            // decision logic (pid-hit vs name-hit with tightest-fit) can run in
            // a pure helper that unit tests exercise without a live KSP vessel.
            var candidates = new List<ActivePartSeat>(av.parts.Count);
            for (int p = 0; p < av.parts.Count; p++)
            {
                var part = av.parts[p];
                int freeSeats = PartFreeSeats(part);
                candidates.Add(new ActivePartSeat
                {
                    PersistentId = part.persistentId,
                    PartName = part.partInfo != null ? part.partInfo.name : null,
                    FreeSeats = freeSeats
                });
            }

            int matchIdx = TryResolveActiveVesselPartForSeat(
                snapshotPartPid, snapshotPartName, candidates, out matchKind,
                out missDiagnostic);
            return matchIdx >= 0 ? av.parts[matchIdx] : null;
        }

        /// <summary>
        /// Returns the number of free crew seats on a live part (0 if none or
        /// the part has no crew capacity). Matches the test-time
        /// <see cref="ActivePartSeat.FreeSeats"/> so the pure helper and the
        /// live path agree on what counts as "has a free seat".
        /// </summary>
        private static int PartFreeSeats(Part part)
        {
            if (part == null || part.CrewCapacity <= 0) return 0;
            int occupied = part.protoModuleCrew != null ? part.protoModuleCrew.Count : 0;
            int free = part.CrewCapacity - occupied;
            return free > 0 ? free : 0;
        }

        private static bool PartHasFreeSeat(Part part)
        {
            return PartFreeSeats(part) > 0;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Remove a replacement kerbal from the roster if they're still Available.
        /// If the replacement is Assigned (on a mission), leave them as a "real" kerbal.
        /// </summary>
        private static void CleanUpReplacement(string originalName, KerbalRoster roster)
        {
            if (!crewReplacements.TryGetValue(originalName, out string replacementName))
                return;

            // Always remove the mapping
            crewReplacements.Remove(originalName);

            // #615 P1 review (third pass): the rescue-placed marker is
            // INTENTIONALLY NOT cleared here. The Re-Fly spawn pipeline runs
            // RescueReservedMissingCrewInSnapshot (sets the marker) and then
            // immediately runs UnreserveCrewInSnapshot on the same snapshot —
            // the original code path that landed in this method. Clearing the
            // marker here wiped it before the next ApplyToRoster walk could
            // observe it, and the stand-in churning bug PR #595 was meant to
            // fix returned in production. The marker is PERSISTENT across
            // ApplyToRoster walks (the guard does not consume it on fire
            // either; see KerbalsModule.cs and the ClearRescuePlaced doc
            // for the full lifecycle) and is bulk-cleared only on session /
            // rewind / wipe-all boundaries by Load / Restore / Clear /
            // Reset paths.

            // Find the replacement in the roster
            ProtoCrewMember replacement = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == replacementName)
                {
                    replacement = pcm;
                    break;
                }
            }

            if (replacement == null)
            {
                CrewLog($"Replacement '{replacementName}' not found in roster (already removed?)");
                return;
            }

            if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Available)
            {
                roster.Remove(replacement);
                CrewLog($"Removed replacement '{replacementName}' (was unused)");
            }
            else
            {
                CrewLog($"Kept replacement '{replacementName}' " +
                    $"(status: {replacement.rosterStatus} — now a real kerbal)");
            }
        }

        /// <summary>
        /// Removes EVA vessels whose crew is reserved (in the replacements dict).
        /// Reserved crew on EVA are separate vessels, not in ActiveVessel.parts.
        /// Removing them prevents duplicates at ghost EndUT spawn.
        /// </summary>
        private static void RemoveReservedEvaVessels(HashSet<uint> spawnedPids = null)
        {
            // Bug #233: build set of PIDs spawned by committed recordings so we
            // don't delete EVA vessels that Parsek intentionally created.
            // Accept pre-built set to avoid redundant iteration when caller already has one.
            if (spawnedPids == null)
                // [Phase 3] ERS-routed: see Phase 3 comment on BuildSpawnedVesselPidSet
                // usage above; same reasoning applies here.
                spawnedPids = BuildSpawnedVesselPidSet(EffectiveState.ComputeERS());

            int evaRemoved = 0;
            int loadedKept = 0;
            int pidKept = 0;
            var allVessels = FlightGlobals.Vessels;
            for (int v = allVessels.Count - 1; v >= 0; v--)
            {
                Vessel vessel = allVessels[v];
                if (vessel == FlightGlobals.ActiveVessel) continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
                if (!vessel.isEVA) continue;

                // Bug #46: don't remove loaded EVA vessels — they're actively in the
                // physics bubble (player-created or recently spawned). Only remove
                // packed/unloaded stale EVA vessels from quicksave.
                if (vessel.loaded)
                {
                    string loadedName = GetEvaCrewName(vessel);
                    if (crewReplacements.ContainsKey(loadedName ?? ""))
                    {
                        loadedKept++;
                        CrewLog($"Kept loaded EVA vessel '{loadedName}' (pid={vessel.persistentId}) — in physics bubble");
                    }
                    continue;
                }

                string evaCrewName = GetEvaCrewName(vessel);
                if (!ShouldRemoveEvaVessel(true, evaCrewName, crewReplacements,
                    vessel.persistentId, spawnedPids))
                {
                    if (crewReplacements.ContainsKey(evaCrewName ?? ""))
                        pidKept++;
                    continue;
                }

                CrewLog($"Removing reserved EVA vessel '{evaCrewName}' (pid={vessel.persistentId})");

                // 1. Remove ProtoVessel to prevent re-spawn on save/load
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null && vessel.protoVessel != null)
                    flightState.protoVessels.Remove(vessel.protoVessel);

                // 2. Remove from active vessel list
                allVessels.RemoveAt(v);

                // 3. Unload parts/modules/physics, then destroy GameObject
                vessel.Unload();
                if (vessel.gameObject != null)
                {
                    vessel.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(vessel.gameObject);
                }
                evaRemoved++;
            }

            if (loadedKept > 0)
                CrewLog($"Kept {loadedKept} loaded EVA vessel(s) (in physics bubble)");
            if (pidKept > 0)
                CrewLog($"Kept {pidKept} EVA vessel(s) (matched committed recording PID)");

            if (evaRemoved > 0)
            {
                CrewLog($"Removed {evaRemoved} reserved EVA vessel(s)");
                RescueReservedCrewAfterEvaRemoval();
            }
        }

        /// <summary>
        /// Rescues reserved crew members that were set to Missing by vessel.Unload()
        /// during EVA vessel removal. Sets them back to Assigned (not Available) because
        /// they are still reserved for a future ghost spawn.
        /// Must be called after RemoveReservedEvaVessels when EVA vessels were removed.
        /// </summary>
        private static void RescueReservedCrewAfterEvaRemoval()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                CrewLog("RescueReservedCrewAfterEvaRemoval: no crew roster — skipping");
                return;
            }

            using (SuppressionGuard.Crew())
            {
                int rescued = 0;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (!ShouldRescueFromMissing(pcm.rosterStatus, pcm.name, crewReplacements))
                        continue;

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescued++;
                    CrewLog($"Rescued Missing crew '{pcm.name}' → Available " +
                        $"(was orphaned by EVA vessel removal)");
                }

                if (rescued > 0)
                    CrewLog($"Rescued {rescued} reserved crew member(s) from Missing status");
            }
        }

        /// <summary>
        /// Rescues crew orphaned by vessel stripping during rewind/revert (#116).
        /// Any crew member with Assigned status who is not on a surviving ProtoVessel
        /// is set to Available. Dead crew are skipped. Must be called after
        /// StripOrphanedSpawnedVessels / StripFuturePrelaunchVessels.
        /// </summary>
        internal static int RescueOrphanedCrew(List<ProtoVessel> survivingVessels)
        {
            if (survivingVessels == null) return 0;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                CrewLog("RescueOrphanedCrew: no crew roster — skipping");
                return 0;
            }

            // Build set of crew names still referenced by surviving vessels
            var survivingCrew = new HashSet<string>();
            for (int i = 0; i < survivingVessels.Count; i++)
            {
                var crew = survivingVessels[i].GetVesselCrew();
                for (int j = 0; j < crew.Count; j++)
                    if (!string.IsNullOrEmpty(crew[j].name))
                        survivingCrew.Add(crew[j].name);
            }

            using (SuppressionGuard.Crew())
            {
                int rescued = 0;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                        continue;
                    if (survivingCrew.Contains(pcm.name))
                        continue;

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescued++;
                    CrewLog($"Rescued orphaned crew '{pcm.name}' → Available " +
                        $"(was Assigned but no vessel references them)");
                }

                if (rescued > 0)
                    ParsekLog.Info("Crew",
                        $"Rescued {rescued} orphaned crew member(s) from vessel strip → Available");
                return rescued;
            }
        }

        /// <summary>
        /// Returns the single crew member's name from an EVA vessel, or null.
        /// Uses GetVesselCrew() for robustness with both packed and unpacked vessels.
        /// </summary>
        private static string GetEvaCrewName(Vessel evaVessel)
        {
            var crew = evaVessel.GetVesselCrew();
            return crew.Count > 0 ? crew[0].name : null;
        }

        private static void CrewLog(string message)
        {
            ParsekLog.Info("CrewReservation", message ?? "(empty)");
        }

        private static void CrewWarn(string message)
        {
            ParsekLog.Warn("CrewReservation", message ?? "(empty)");
        }

        #endregion

        #region Pure Static Methods

        /// <summary>
        /// Bug #277 — pure helper result: where in a recording snapshot was a
        /// reserved kerbal originally seated. Used by SwapReservedCrewInFlight's
        /// orphan placement pass to find a matching part on the active vessel.
        /// </summary>
        internal struct OrphanSeatLocation
        {
            public bool Found;
            public uint PartPid;     // PART node 'pid' value (matches Part.persistentId on the live vessel)
            public string PartName;  // PART node 'name' value (matches part.partInfo.name)
        }

        /// <summary>
        /// Bug #456 — which matcher tier produced a successful orphan placement.
        /// Exposed so `PlaceOrphanedReplacements` can count pid-hits vs
        /// name-hit fallbacks and surface the split in its summary log.
        /// </summary>
        internal enum SeatMatchKind
        {
            None = 0,
            PidHit = 1,
            NameHit = 2,
        }

        internal enum SeatMatchMissReason
        {
            None = 0,
            ActiveVesselHasNoParts = 1,
            NoSnapshotLookupTier = 2,
            ActiveVesselMissingSnapshotPart = 3,
            SnapshotPartSeatsFull = 4,
            SnapshotPidPartSeatsFull = 5,
            ActiveVesselMissingSnapshotPid = 6,
        }

        /// <summary>
        /// Bug #456 — lightweight view of a live vessel part used by the pure
        /// seat-matcher helper. Avoids taking a hard dependency on the KSP
        /// <see cref="Part"/> type so `TryResolveActiveVesselPartForSeat` is
        /// fully unit-testable.
        /// </summary>
        internal struct ActivePartSeat
        {
            public uint PersistentId;
            public string PartName;
            public int FreeSeats;
        }

        internal struct SeatMatchMissDiagnostic
        {
            public SeatMatchMissReason Reason;
            public int ActivePartCount;
            public int FreeSeatPartCount;
            public int PidMatchCount;
            public int PidFreeSeatCount;
            public int NameMatchCount;
            public int NameFreeSeatCount;
        }

        /// <summary>
        /// Formats truthful per-attempt tier telemetry for the no-match WARN.
        /// Distinct from the cumulative pidHits/nameHitFallbacks counters, which
        /// track prior successful placements across the whole pass.
        /// </summary>
        internal static string FormatSeatMatchAttemptDiagnostic(
            uint snapshotPartPid, string snapshotPartName)
        {
            return $"attempted pidTier={(snapshotPartPid != 0 ? "yes" : "no")} " +
                $"nameTier={(!string.IsNullOrEmpty(snapshotPartName) ? "yes" : "no")}";
        }

        internal static string FormatSeatMatchMissDiagnostic(SeatMatchMissDiagnostic diagnostic)
        {
            return $"reason={FormatSeatMatchMissReason(diagnostic.Reason)} " +
                $"activeParts={diagnostic.ActivePartCount} " +
                $"freeSeatParts={diagnostic.FreeSeatPartCount} " +
                $"pidMatches={diagnostic.PidMatchCount} " +
                $"pidFreeSeats={diagnostic.PidFreeSeatCount} " +
                $"nameMatches={diagnostic.NameMatchCount} " +
                $"nameFreeSeats={diagnostic.NameFreeSeatCount}";
        }

        private static string FormatSeatMatchMissReason(SeatMatchMissReason reason)
        {
            switch (reason)
            {
                case SeatMatchMissReason.ActiveVesselHasNoParts:
                    return "active-vessel-has-no-parts";
                case SeatMatchMissReason.NoSnapshotLookupTier:
                    return "no-snapshot-lookup-tier";
                case SeatMatchMissReason.ActiveVesselMissingSnapshotPart:
                    return "active-vessel-missing-snapshot-part";
                case SeatMatchMissReason.SnapshotPartSeatsFull:
                    return "snapshot-part-seats-full";
                case SeatMatchMissReason.SnapshotPidPartSeatsFull:
                    return "snapshot-pid-part-seats-full";
                case SeatMatchMissReason.ActiveVesselMissingSnapshotPid:
                    return "active-vessel-missing-snapshot-pid";
                default:
                    return "none";
            }
        }

        internal static void LogOrphanPlacementDeferred(
            string originalName,
            string replacementName,
            uint snapshotPartPid,
            string snapshotPartName,
            SeatMatchMissDiagnostic missDiagnostic,
            int pidHits,
            int nameHitFallbacks)
        {
            // Preserve the distinctive "snapshot pid=0" signal (bug #413):
            // a future regression where the capture site drops the real
            // persistentId will still show up here with pid=0 and a valid part
            // name, so it's callable out by log-grep.
            string pidDiagnostic = snapshotPartPid == 0
                ? "pid=0 (suspicious: snapshot missing persistentId)"
                : $"pid={snapshotPartPid}";
            string attemptDiagnostic = FormatSeatMatchAttemptDiagnostic(
                snapshotPartPid, snapshotPartName);
            ParsekLog.Warn("CrewReservation",
                $"Orphan placement deferred: no matching part with free seat in active vessel for " +
                $"'{originalName}' → '{replacementName}' " +
                $"(snapshot {pidDiagnostic} name='{snapshotPartName}') — " +
                $"stand-in kept in roster; {FormatSeatMatchMissDiagnostic(missDiagnostic)} " +
                $"({attemptDiagnostic}; cumulative pidHits={pidHits} " +
                $"nameHitFallbacks={nameHitFallbacks})");
        }

        /// <summary>
        /// Bug #456 — pure seat-match decision.
        ///
        /// Two tiers:
        ///   1. <b>PidHit</b>: snapshot <paramref name="snapshotPartPid"/> is
        ///      non-zero and matches an active part's PersistentId that still has
        ///      a free seat. A pid hit always wins over a name hit — this keeps
        ///      the old behaviour for post-revert vessels where part PIDs survive,
        ///      and defends against brittle false-positives when an identical-name
        ///      reassembled vessel happens to share the snapshot pid.
        ///   2. <b>NameHit</b> (fallback): no pid match, or the snapshot pid is
        ///      zero or the pid-matched part is full. Find all parts whose
        ///      <c>PartName</c> equals <paramref name="snapshotPartName"/> AND
        ///      have at least one free seat, and return the one with the
        ///      <i>fewest</i> free seats (tightest fit). Ties broken by index
        ///      (first occurrence wins). This prefers cockpits (typically 1 seat)
        ///      over passenger cabins (typically 4+ seats) when both share the
        ///      part name — which doesn't happen in stock, but defends against
        ///      part mods that reuse part.cfg <c>name =</c> across variants.
        ///
        /// Returns <c>-1</c> when neither tier matches, in which case
        /// <paramref name="matchKind"/> is <see cref="SeatMatchKind.None"/>.
        /// </summary>
        internal static int TryResolveActiveVesselPartForSeat(
            uint snapshotPartPid,
            string snapshotPartName,
            IList<ActivePartSeat> activeParts,
            out SeatMatchKind matchKind)
        {
            SeatMatchMissDiagnostic ignored;
            return TryResolveActiveVesselPartForSeat(
                snapshotPartPid, snapshotPartName, activeParts, out matchKind, out ignored);
        }

        internal static int TryResolveActiveVesselPartForSeat(
            uint snapshotPartPid,
            string snapshotPartName,
            IList<ActivePartSeat> activeParts,
            out SeatMatchKind matchKind,
            out SeatMatchMissDiagnostic missDiagnostic)
        {
            matchKind = SeatMatchKind.None;
            missDiagnostic = new SeatMatchMissDiagnostic
            {
                Reason = SeatMatchMissReason.None,
                ActivePartCount = activeParts != null ? activeParts.Count : 0,
            };
            if (activeParts == null || activeParts.Count == 0)
            {
                missDiagnostic.Reason = SeatMatchMissReason.ActiveVesselHasNoParts;
                return -1;
            }

            for (int i = 0; i < activeParts.Count; i++)
            {
                var p = activeParts[i];
                if (p.FreeSeats > 0)
                    missDiagnostic.FreeSeatPartCount++;
                if (snapshotPartPid == 0 || p.PersistentId != snapshotPartPid)
                    continue;

                missDiagnostic.PidMatchCount++;
                if (p.FreeSeats > 0)
                {
                    missDiagnostic.PidFreeSeatCount++;
                    matchKind = SeatMatchKind.PidHit;
                    return i;
                }
            }

            // Tier 2: name-hit with tightest-fit (min free seats).
            if (!string.IsNullOrEmpty(snapshotPartName))
            {
                int bestIdx = -1;
                int bestFreeSeats = int.MaxValue;
                for (int i = 0; i < activeParts.Count; i++)
                {
                    var p = activeParts[i];
                    if (string.IsNullOrEmpty(p.PartName)) continue;
                    if (p.PartName != snapshotPartName) continue;
                    missDiagnostic.NameMatchCount++;
                    if (p.FreeSeats <= 0) continue;
                    missDiagnostic.NameFreeSeatCount++;
                    if (p.FreeSeats < bestFreeSeats)
                    {
                        bestFreeSeats = p.FreeSeats;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                {
                    matchKind = SeatMatchKind.NameHit;
                    return bestIdx;
                }
            }

            missDiagnostic.Reason = ClassifySeatMatchMiss(
                snapshotPartPid, snapshotPartName, missDiagnostic);
            return -1;
        }

        private static SeatMatchMissReason ClassifySeatMatchMiss(
            uint snapshotPartPid,
            string snapshotPartName,
            SeatMatchMissDiagnostic diagnostic)
        {
            if (diagnostic.ActivePartCount <= 0)
                return SeatMatchMissReason.ActiveVesselHasNoParts;

            bool attemptedPid = snapshotPartPid != 0;
            bool attemptedName = !string.IsNullOrEmpty(snapshotPartName);
            if (!attemptedPid && !attemptedName)
                return SeatMatchMissReason.NoSnapshotLookupTier;

            if (attemptedName)
            {
                if (diagnostic.NameMatchCount == 0)
                    return SeatMatchMissReason.ActiveVesselMissingSnapshotPart;
            }

            if (attemptedPid &&
                diagnostic.PidMatchCount > 0 &&
                diagnostic.PidFreeSeatCount == 0)
                return SeatMatchMissReason.SnapshotPidPartSeatsFull;

            if (attemptedName)
            {
                if (diagnostic.NameFreeSeatCount == 0)
                    return SeatMatchMissReason.SnapshotPartSeatsFull;
            }

            if (attemptedPid)
            {
                if (!attemptedName && diagnostic.PidMatchCount == 0)
                    return SeatMatchMissReason.ActiveVesselMissingSnapshotPid;
            }

            return SeatMatchMissReason.None;
        }

        /// <summary>
        /// Bug #277 — pure helper: scan the supplied recording snapshots for a
        /// PART that lists <paramref name="originalName"/> in its crew values.
        /// Returns the first match (PartPid + PartName) or Found=false.
        ///
        /// Snapshot crew lists may contain stand-in names from prior recordings
        /// (see KerbalsModule.ReverseMapCrewNames). When <paramref name="reverseStandinMap"/>
        /// is supplied, a snapshot crew name that maps back to <paramref name="originalName"/>
        /// via reverse lookup is also considered a match — this catches the case
        /// where an earlier recording committed and replaced the original's name
        /// with a stand-in in subsequent snapshots.
        ///
        /// Match key is the kerbal name itself (kerbal names are unique in a
        /// roster). VesselName comparison is intentionally NOT used because two
        /// launches can share the same vessel name and would falsely match.
        /// </summary>
        internal static OrphanSeatLocation ResolveOrphanSeatFromSnapshots(
            string originalName,
            IEnumerable<ConfigNode> ghostVisualSnapshots,
            IReadOnlyDictionary<string, string> reverseStandinMap = null)
        {
            var notFound = new OrphanSeatLocation { Found = false };
            if (string.IsNullOrEmpty(originalName) || ghostVisualSnapshots == null)
                return notFound;

            foreach (var snapshot in ghostVisualSnapshots)
            {
                if (snapshot == null) continue;
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    if (partNode == null) continue;
                    string[] crewNames = partNode.GetValues("crew");
                    for (int i = 0; i < crewNames.Length; i++)
                    {
                        string crewEntry = crewNames[i];
                        if (string.IsNullOrEmpty(crewEntry)) continue;

                        bool isMatch = (crewEntry == originalName);

                        // Reverse-map: crew entry might be a stand-in for the original.
                        if (!isMatch && reverseStandinMap != null
                            && reverseStandinMap.TryGetValue(originalName, out string knownStandin)
                            && knownStandin == crewEntry)
                        {
                            isMatch = true;
                        }

                        if (!isMatch) continue;

                        // Bug #413: KSP's ProtoPartSnapshot serializes the part's
                        // 32-bit pid under the key `persistentId` (see any stock
                        // `.sfs` PART node). The earlier implementation read `pid`,
                        // which only exists on the VESSEL node (a guid-hex string),
                        // so `uint.TryParse` always failed and every orphan seat
                        // was returned with PartPid=0. Prefer `persistentId` and
                        // fall back to `pid` so test-authored snapshots that still
                        // use the legacy field name keep matching.
                        uint pid = 0;
                        string pidValue = partNode.GetValue("persistentId");
                        if (string.IsNullOrEmpty(pidValue))
                            pidValue = partNode.GetValue("pid");
                        if (!string.IsNullOrEmpty(pidValue))
                            uint.TryParse(pidValue, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out pid);

                        string partName = partNode.GetValue("name") ?? "";

                        return new OrphanSeatLocation
                        {
                            Found = true,
                            PartPid = pid,
                            PartName = partName
                        };
                    }
                }
            }

            return notFound;
        }

        /// <summary>
        /// Pure decision: should this EVA vessel be removed during crew swap?
        /// An EVA vessel is removed if its crew member is reserved (in the replacements dict)
        /// AND the vessel was not spawned by a committed recording (bug #233).
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRemoveEvaVessel(
            bool isEva, string crewName, IReadOnlyDictionary<string, string> replacements,
            uint vesselPid = 0, HashSet<uint> spawnedVesselPids = null)
        {
            if (!isEva || string.IsNullOrEmpty(crewName) || !replacements.ContainsKey(crewName))
                return false;

            // Bug #233: don't remove EVA vessels that Parsek spawned at recording end
            if (vesselPid != 0 && spawnedVesselPids != null && spawnedVesselPids.Contains(vesselPid))
                return false;

            return true;
        }

        /// <summary>
        /// Builds a HashSet of SpawnedVesselPersistentId values from committed recordings.
        /// Used by RemoveReservedEvaVessels to avoid deleting Parsek-spawned EVA vessels (bug #233).
        /// Extracted for testability.
        /// </summary>
        internal static HashSet<uint> BuildSpawnedVesselPidSet(IReadOnlyList<Recording> recordings)
        {
            var pids = new HashSet<uint>();
            if (recordings == null) return pids;
            for (int i = 0; i < recordings.Count; i++)
            {
                uint pid = recordings[i].SpawnedVesselPersistentId;
                if (pid != 0)
                    pids.Add(pid);
            }
            return pids;
        }

        /// <summary>
        /// Returns true if a crew member with the given roster status should be
        /// processed for reservation (i.e. not dead). Missing crew are processed
        /// because they may be alive but orphaned from a removed vessel.
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldProcessCrewForReservation(ProtoCrewMember.RosterStatus status)
        {
            return status != ProtoCrewMember.RosterStatus.Dead;
        }

        /// <summary>
        /// Pure decision: should this crew member be rescued from Missing status?
        /// A Missing crew member is rescued if they are reserved (in the replacements dict),
        /// indicating they were orphaned by RemoveReservedEvaVessels calling vessel.Unload().
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRescueFromMissing(
            ProtoCrewMember.RosterStatus status,
            string crewName,
            IReadOnlyDictionary<string, string> replacements)
        {
            return status == ProtoCrewMember.RosterStatus.Missing
                && !string.IsNullOrEmpty(crewName)
                && replacements != null
                && replacements.ContainsKey(crewName);
        }

        /// <summary>
        /// Extracts crew names from a vessel snapshot ConfigNode.
        /// </summary>
        internal static List<string> ExtractCrewFromSnapshot(ConfigNode snapshot)
        {
            var crew = new List<string>();
            if (snapshot == null) return crew;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(name))
                        crew.Add(name);
                }
            }
            return crew;
        }

        /// <summary>
        /// Pure method: swaps reserved crew names in a vessel snapshot ConfigNode,
        /// replacing each reserved original name with its replacement name.
        /// Used for KSC spawns where SwapReservedCrewInFlight cannot run
        /// (no loaded vessel / no flight scene). Bug #167.
        /// Returns the number of crew names swapped.
        /// </summary>
        internal static int SwapReservedCrewInSnapshot(
            ConfigNode snapshot, IReadOnlyDictionary<string, string> replacements)
        {
            if (snapshot == null || replacements == null || replacements.Count == 0)
                return 0;

            int swapCount = 0;
            int partIndex = 0;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                string[] crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) { partIndex++; continue; }

                bool anySwapped = false;
                var updated = new List<string>(crewNames.Length);

                for (int i = 0; i < crewNames.Length; i++)
                {
                    if (replacements.TryGetValue(crewNames[i], out string replacementName))
                    {
                        ParsekLog.Verbose("CrewReservation",
                            $"Snapshot swap: '{crewNames[i]}' -> '{replacementName}' in PART[{partIndex}]");
                        updated.Add(replacementName);
                        anySwapped = true;
                        swapCount++;
                    }
                    else
                    {
                        updated.Add(crewNames[i]);
                    }
                }

                if (anySwapped)
                {
                    partNode.RemoveValues("crew");
                    for (int i = 0; i < updated.Count; i++)
                        partNode.AddValue("crew", updated[i]);
                }

                partIndex++;
            }

            if (swapCount > 0)
                ParsekLog.Verbose("CrewReservation",
                    $"Snapshot crew swap complete: {swapCount} name(s) replaced across {partIndex} part(s)");

            return swapCount;
        }

        #endregion

        #region Bridge Methods (KerbalsModule)

        /// <summary>
        /// Set a crew replacement mapping. Called by KerbalsModule.ApplyToRoster
        /// to bridge derived state to SwapReservedCrewInFlight.
        /// </summary>
        internal static void SetReplacement(string originalName, string replacementName)
        {
            crewReplacements[originalName] = replacementName;
        }

        /// <summary>
        /// Clear all replacements without roster access. For KerbalsModule use.
        /// </summary>
        internal static void ClearReplacementsInternal()
        {
            crewReplacements.Clear();
        }

        #endregion

        #region Phase 9 reservation recompute after tombstones (design §6.6 step 6 / §7.16)

        /// <summary>
        /// Phase 9 of Rewind-to-Staging (design §6.6 step 6 / §7.16 / §10.4):
        /// re-derives the crew reservation dictionary after
        /// <see cref="SupersedeCommit.CommitTombstones"/> has appended new
        /// <see cref="LedgerTombstone"/>s.
        ///
        /// <para>
        /// Walks the Effective Ledger Set (tombstoned actions filtered out per
        /// §3.2) and replays every surviving <see cref="GameActionType.KerbalAssignment"/>
        /// action through <see cref="KerbalsModule.ProcessAction"/>, then calls
        /// <see cref="KerbalsModule.PostWalk"/> and
        /// <see cref="KerbalsModule.ApplyToRoster"/> so the
        /// <see cref="CrewReservationManager"/> replacement dictionary is
        /// refreshed. Kerbals whose <see cref="KerbalEndState.Dead"/> action
        /// was just tombstoned fall out of the reservation set and their
        /// stand-ins get cleaned up through the usual <see cref="ApplyToRoster"/>
        /// step.
        /// </para>
        ///
        /// <para>
        /// Safe to call with no module wired (e.g. early boot, unit tests): the
        /// method short-circuits with a Verbose log.
        /// </para>
        /// </summary>
        public static void RecomputeAfterTombstones()
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null)
            {
                ParsekLog.Verbose("CrewReservations",
                    "RecomputeAfterTombstones: no KerbalsModule — skipping");
                return;
            }

            // ELS = ledger minus tombstones (design §3.2). This is the only
            // source of truth for "which kerbal assignments are still effective."
            var els = EffectiveState.ComputeELS();
            var kerbalAssignments = new List<GameAction>();
            if (els != null)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    var a = els[i];
                    if (a == null) continue;
                    if (a.Type != GameActionType.KerbalAssignment) continue;
                    kerbalAssignments.Add(a);
                }
            }

            kerbals.Reset();
            kerbals.PrePass(kerbalAssignments);
            for (int i = 0; i < kerbalAssignments.Count; i++)
                kerbals.ProcessAction(kerbalAssignments[i]);
            kerbals.PostWalk();

            // ApplyToRoster refreshes the replacement dictionary via
            // ClearReplacementsInternal + SetReplacement. Roster may be absent
            // in headless / test contexts; ApplyToRoster logs and no-ops.
            kerbals.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster);

            int remaining = 0;
            foreach (var _ in kerbals.Reservations)
                remaining++;

            ParsekLog.Info("CrewReservations",
                $"Recomputed after tombstones: {remaining} reservations remain.");
        }

        #endregion

        #region Phase 7 dual-residence carve-out (design §3.3.1)

        /// <summary>
        /// Phase 7 of Rewind-to-Staging (design §3.3.1 kerbal dual-residence
        /// carve-out): returns true iff a re-fly session is live AND the given
        /// kerbal is currently embodied on <see cref="FlightGlobals.ActiveVessel"/>'s
        /// crew list AND the active vessel is the provisional re-fly vessel
        /// (identified by <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/>
        /// resolving to a committed recording whose
        /// <see cref="Recording.VesselPersistentId"/> matches the active vessel's
        /// persistentId).
        ///
        /// <para>Without this carve-out, a kerbal whose ledger
        /// <see cref="KerbalDeath"/> event is still in ELS (tombstone lands at
        /// merge) would be reservation-locked as dead, silently blocking EVA or
        /// crew transfer during the re-fly.</para>
        ///
        /// <para>The overload taking a marker is the underlying decision — used
        /// by the no-marker overload and by tests that want to inject a synthetic
        /// marker without touching <see cref="ParsekScenario"/>.</para>
        /// </summary>
        internal static bool IsLiveReFlyCrew(ProtoCrewMember kerbal, ReFlySessionMarker marker)
        {
            if (kerbal == null) return false;
            if (marker == null) return false;

            var active = FlightGlobals.ActiveVessel;
            if (active == null) return false;
            if (!ActiveVesselMatchesReFlyRecording(active, marker))
                return false;

            var crew = active.GetVesselCrew();
            if (crew == null) return false;
            for (int i = 0; i < crew.Count; i++)
            {
                var pcm = crew[i];
                if (pcm == null) continue;
                if (string.Equals(pcm.name, kerbal.name, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the live scenario's marker. Kept as a thin wrapper so callers
        /// don't have to reach into <see cref="ParsekScenario"/> themselves.
        /// </summary>
        internal static bool IsLiveReFlyCrew(ProtoCrewMember kerbal)
        {
            return IsLiveReFlyCrew(kerbal, ParsekScenario.Instance?.ActiveReFlySessionMarker);
        }

        /// <summary>
        /// Pure decision: does <paramref name="activeVessel"/>'s persistentId
        /// match the provisional re-fly recording referenced by the marker? The
        /// recording is resolved via <see cref="RecordingStore"/> (raw read —
        /// session-provisional records sit in the same list as committed ones).
        /// Exposed internally for test injection without a live FlightGlobals.
        /// </summary>
        internal static bool ActiveVesselMatchesReFlyRecording(Vessel activeVessel, ReFlySessionMarker marker)
        {
            if (activeVessel == null || marker == null) return false;
            string reflyId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(reflyId)) return false;

            // Raw read: the provisional re-fly recording is intentionally
            // NotCommitted and therefore NOT in ERS. We specifically want to
            // find it so we can correlate its VesselPersistentId against the
            // active vessel. Allowlisted under the existing CrewReservationManager
            // entry in the grep-audit allowlist.
            // [ERS-exempt — Phase 7] The provisional re-fly recording sits in
            // CommittedRecordings with MergeState=NotCommitted; ERS filters it
            // out by definition (design §3.1), so routing this lookup through
            // EffectiveState.ComputeERS() would return null and break the
            // dual-residence carve-out.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return false;
            uint activePid = activeVessel.persistentId;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (!string.Equals(rec.RecordingId, reflyId, System.StringComparison.Ordinal))
                    continue;
                return rec.VesselPersistentId == activePid;
            }
            return false;
        }

        #endregion

        #region Testing & Serialization

        /// <summary>
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// Also clears the #615 rescue-placed marker set so test fixtures see
        /// a clean rescue signal between cases.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
            rescuePlacedKerbals.Clear();
        }

        /// <summary>
        /// Seeds the crew replacement dictionary directly so a test can
        /// simulate the post-reserve state without driving a real
        /// <see cref="HighLogic.CurrentGame"/>. Used by the P1-review-second-pass
        /// regression that exercises the
        /// Rescue → Unreserve → ApplyToRoster sequence end-to-end.
        /// </summary>
        internal static void SeedReplacementForTesting(string originalName, string replacementName)
        {
            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(replacementName))
                return;
            crewReplacements[originalName] = replacementName;
        }

        /// <summary>
        /// Test seam mirroring the dictionary-management half of
        /// <see cref="CleanUpReplacement"/> (the only half that touches the
        /// rescue-placed marker contract). The full
        /// <see cref="UnreserveCrewInSnapshot"/> path requires a live KSP
        /// <see cref="HighLogic.CurrentGame"/> + <see cref="KerbalRoster"/>,
        /// which xUnit cannot stand up. This seam asserts the production
        /// invariant (P1 review third pass) that the per-name unreserve does
        /// NOT clear the rescue-placed marker, and the marker stays set so
        /// every subsequent <see cref="KerbalsModule.ApplyToRoster"/> walk
        /// observes it — exercised by
        /// <see cref="RescueCompletionGuardTests"/>.
        /// </summary>
        internal static void CleanUpReplacementForTesting(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return;
            // Mirrors the production CleanUpReplacement dictionary path:
            // remove the entry, do NOT touch the rescue-placed marker. The
            // roster-touching cleanup is intentionally omitted because
            // it has no effect on the marker contract.
            crewReplacements.Remove(originalName);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// returns a shallow copy of the replacement dictionary so the bundle
        /// can preserve it across a quicksave load. Keys are the reserved
        /// kerbal names; values are their stand-in replacements.
        /// </summary>
        internal static Dictionary<string, string> SnapshotReplacements()
        {
            return new Dictionary<string, string>(crewReplacements);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// re-applies a previously captured replacement dictionary after the
        /// quicksave load has replaced the live in-memory state. The method
        /// replaces — not merges — the current map so restoring after an
        /// in-memory swap does not duplicate entries.
        /// </summary>
        internal static void RestoreReplacements(IReadOnlyDictionary<string, string> replacements)
        {
            crewReplacements.Clear();
            // #615 P1 review: rewind-quickload reconciliation rewinds time —
            // the in-memory rescue-placed markers from after the quicksave's
            // captured UT no longer apply. The replacement dict is being
            // re-seeded from the bundle; rescue markers should restart empty
            // and re-populate as the post-load spawn pipeline runs.
            rescuePlacedKerbals.Clear();
            if (replacements == null) return;
            foreach (var kv in replacements)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    crewReplacements[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        internal static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();
            // #615 P1 review: rescue-placed markers are session-scoped — the
            // rescue path runs in-flight, the marker drives the next walk's
            // ApplyToRoster decision, and the marker has no persisted home.
            // A cold load starts a new session, so any leftover entries from
            // a prior in-memory state are stale.
            rescuePlacedKerbals.Clear();

            ConfigNode replacementsNode = node.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode == null)
            {
                CrewLog("Loaded 0 crew replacements (no CREW_REPLACEMENTS node)");
                return;
            }

            ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string original = entries[i].GetValue("original");
                string replacement = entries[i].GetValue("replacement");
                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
                {
                    crewReplacements[original] = replacement;
                }
            }

            CrewLog($"Loaded {crewReplacements.Count} crew replacement(s)");
        }

        internal static void SaveCrewReplacements(ConfigNode node)
        {
            if (crewReplacements.Count > 0)
            {
                ConfigNode replacementsNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var kvp in crewReplacements)
                {
                    ConfigNode entry = replacementsNode.AddNode("ENTRY");
                    entry.AddValue("original", kvp.Key);
                    entry.AddValue("replacement", kvp.Value);
                }
                CrewLog($"Saved {crewReplacements.Count} crew replacement(s)");
            }
        }

        #endregion
    }
}
