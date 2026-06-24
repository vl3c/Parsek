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
    internal static partial class CrewReservationManager
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
        ///
        /// Pass 2 is suppressed when the active vessel is the fresh VAB/SPH rollout
        /// for this scene (<see cref="RecordingStore.SceneEntryFreshRolloutVesselPid"/>,
        /// 0 outside a fresh launch). A fresh launch is never a continuation of a
        /// prior recording, so it has no orphaned crew to reclaim, and reclaiming
        /// would mis-seat stand-ins through KSP's craft-stable part persistentId
        /// reuse (see <see cref="ShouldSuppressOrphanPlacementForFreshRollout"/>).
        /// This applies to every call site (flight-ready, chain-commit, tree-commit),
        /// since any of them can fire while the fresh-rollout vessel is still active.
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
            // #976-class: an adoption-stamped recording carries SpawnedVesselPersistentId ==
            // its craft-baked VesselPersistentId, which a relaunch of the same craft reuses.
            // So a bare pid match would wrongly classify a fresh relaunch as "Parsek-spawned"
            // and skip the whole swap, leaving reserved crew on the player's new ship. Require
            // the matching adoption-stamped recording to be the SAME launch (guid) as the active
            // vessel; real spawns use a KSP-unique pid and stay pid-only (see ActiveVesselIsParsekSpawned).
            var ers = EffectiveState.ComputeERS();
            var spawnedPids = BuildSpawnedVesselPidSet(ers);
            uint activePid = FlightGlobals.ActiveVessel.persistentId;
            string activeGuid = FlightGlobals.ActiveVessel.id != Guid.Empty
                ? FlightGlobals.ActiveVessel.id.ToString("N")
                : null;
            if (ActiveVesselIsParsekSpawned(ers, activePid, activeGuid))
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

                    // An Assigned stand-in is already aboard a live vessel —
                    // adding them to this part would seat the same kerbal on
                    // two vessels at once. Dead is permanent. Leave the
                    // reserved original seated instead — same failure handling
                    // as replacement-not-in-roster above. A Missing stand-in is
                    // rescued to Available first (the agreed Missing-rescue
                    // convention; see ClassifyStandInForPlacement).
                    var placementCheck = ClassifyStandInForPlacement(true, replacement.rosterStatus);
                    if (placementCheck == StandInPlacementCheck.SkipAssigned
                        || placementCheck == StandInPlacementCheck.SkipDead)
                    {
                        CrewLog($"Cannot swap '{original.name}': stand-in '{replacementName}' " +
                            $"is {replacement.rosterStatus}" +
                            (placementCheck == StandInPlacementCheck.SkipAssigned
                                ? " (aboard another vessel — placing would duplicate the kerbal)"
                                : " (Dead is permanent)") +
                            " — leaving original seated");
                        failCount++;
                        continue;
                    }
                    if (placementCheck == StandInPlacementCheck.PlaceAfterMissingRescue)
                    {
                        replacement.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        CrewLog($"Rescued Missing stand-in '{replacementName}' → Available before in-flight swap");
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
            //
            // Suppressed for a fresh-rollout active vessel (VAB/SPH launch): a ship
            // just rolled out is not a continuation or merge of any prior recording,
            // so it has no orphaned crew to reclaim. Running orphan placement here
            // mis-injects stand-ins because KSP reuses craft part persistentIds across
            // relaunches of the same .craft — a brand-new launch's command pod can
            // share the persistentId of an old recording's pod, so the pid-tier
            // matcher seats the stand-in into the fresh vessel the player just crewed.
            // Pass 1 still runs, so a reserved kerbal that KSP auto-assigned onto the
            // fresh launch is swapped for its stand-in as usual.
            int orphanPlaced = 0;
            uint freshRolloutPid = RecordingStore.SceneEntryFreshRolloutVesselPid;
            if (ShouldSuppressOrphanPlacementForFreshRollout(activePid, freshRolloutPid))
            {
                ParsekLog.Info("CrewReservation",
                    $"Orphan placement skipped: active vessel pid={activePid} is a fresh-rollout " +
                    "launch (new mission has no orphaned crew to reclaim; avoids craft-pid-reuse " +
                    "stand-in mis-injection)");
            }
            else
            {
                orphanPlaced = PlaceOrphanedReplacements(roster, swappedOriginals);
            }
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
        /// Pure decision: suppress Pass-2 orphan crew placement when the active
        /// vessel is the just-rolled-out fresh launch (VAB/SPH). A fresh launch is
        /// never a continuation/merge of a prior recording, so it has no orphaned
        /// reserved crew to reclaim — and reclaiming would mis-seat a stand-in via
        /// KSP's craft-stable part persistentId reuse (a relaunch of the same .craft
        /// reuses the old recording's pod persistentId, which the pid-tier matcher
        /// then false-matches). Returns false when no fresh-rollout pid was captured
        /// (<paramref name="freshRolloutVesselPid"/> == 0), i.e. for merge /
        /// chain-commit / resumed-save call sites where orphan placement is intended.
        ///
        /// Body mirrors <c>ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch</c>
        /// (same fresh-rollout identity test); kept separate because the two are
        /// distinct decisions that may diverge, and this one lives with the crew
        /// manager rather than the Unity flight controller for direct unit testing.
        /// </summary>
        internal static bool ShouldSuppressOrphanPlacementForFreshRollout(
            uint activeVesselPid, uint freshRolloutVesselPid)
        {
            return activeVesselPid != 0 && activeVesselPid == freshRolloutVesselPid;
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
            int skippedAssignedToOtherVessel = 0;        // Info: stand-in busy on another live vessel — placing would duplicate
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
                var placementCheck = ClassifyStandInForPlacement(true, replacement.rosterStatus);
                if (placementCheck == StandInPlacementCheck.SkipDead)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: skipping '{originalName}' → '{replacementName}': replacement is Dead");
                    skippedDeadOrMissingReplacement++;
                    continue;
                }
                if (placementCheck == StandInPlacementCheck.PlaceAfterMissingRescue)
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

                // The active-vessel check above only sees the active vessel's
                // crew. An Assigned stand-in who is NOT on the active vessel is
                // aboard some OTHER live vessel (loaded or persisted) — placing
                // them here would seat the same kerbal on two vessels at once.
                // Leave the stand-in where they are; the orphan stays
                // unplaced and can be reclaimed on a later pass if the
                // stand-in frees up. (Checked AFTER the active-vessel skip so
                // the skippedAlreadyOnActiveVessel bucket keeps its meaning —
                // a stand-in on the active vessel is Assigned too.)
                if (placementCheck == StandInPlacementCheck.SkipAssigned)
                {
                    CrewLog($"Orphan placement: skipping '{originalName}' → '{replacementName}': " +
                        "stand-in is Assigned aboard another vessel — placing would duplicate the kerbal");
                    skippedAssignedToOtherVessel++;
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
                    $"skippedAssignedToOtherVessel={skippedAssignedToOtherVessel} " +
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
        /// §3.2) and replays every surviving kerbal assignment / roster-creation
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
            RecomputeFromEffectiveLedger("after tombstones", null, applyToRoster: true);
        }

        internal static void RecomputeAfterCutoffWalk(double utCutoff)
        {
            RecomputeFromEffectiveLedger(
                "after cutoff walk",
                "cutoffUT=" + utCutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                applyToRoster: false);
        }

        private static void RecomputeFromEffectiveLedger(
            string reason,
            string detail,
            bool applyToRoster)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null)
            {
                ParsekLog.Verbose("CrewReservations",
                    $"RecomputeFromEffectiveLedger: no KerbalsModule — skipping ({reason})");
                return;
            }

            // ELS = ledger minus tombstones (design §3.2). This is the only
            // source of truth for "which kerbal actions are still effective."
            // Feed roster-creation rows as well as assignments so tombstoned
            // roster cleanup can preserve kerbals still created by surviving ELS.
            var els = EffectiveState.ComputeELS();
            var kerbalActions = new List<GameAction>();
            if (els != null)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    var a = els[i];
                    if (a == null) continue;
                    if (a.Type != GameActionType.KerbalAssignment
                        && a.Type != GameActionType.KerbalHire
                        && a.Type != GameActionType.KerbalRescue
                        && a.Type != GameActionType.KerbalStandIn)
                        continue;
                    kerbalActions.Add(a);
                }
            }

            kerbals.Reset();
            kerbals.PrePass(kerbalActions);
            for (int i = 0; i < kerbalActions.Count; i++)
                kerbals.ProcessAction(kerbalActions[i]);
            kerbals.PostWalk();

            // ApplyToRoster refreshes the replacement dictionary via
            // ClearReplacementsInternal + SetReplacement. Cutoff recalculations
            // defer roster mutation to LedgerOrchestrator's normal patch gate so
            // post-rewind flight-load and pending-tree paths still honor KSP state
            // patch deferral.
            if (applyToRoster)
                kerbals.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster);

            int remaining = kerbals.Reservations.Count;
            string detailPart = string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
            ParsekLog.Info("CrewReservations",
                $"Recomputed {reason}: {remaining} reservations remain{detailPart}.");
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
