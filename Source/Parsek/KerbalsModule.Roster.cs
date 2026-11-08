using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Applying to the KSP roster: career log entries, the roster facade, reached committed hires and dismissals, ApplyToRoster, the career-log re-assert and the tombstoned roster cleanup.
    internal partial class KerbalsModule : IResourceModule
    {
        // ────────────────────────────────────────────────────────
        // ApplyToRoster — KSP state mutations
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Unions a <see cref="GameActionType.KerbalExperience"/> row's career-log entries
        /// into the per-kerbal accumulator. Set-union only - see
        /// <see cref="careerEntriesByKerbal"/> for why that is the whole safety argument.
        /// </summary>
        private void AccumulateCareerEntries(GameAction action)
        {
            if (action == null || string.IsNullOrEmpty(action.KerbalName))
                return;
            if (string.IsNullOrEmpty(action.KerbalCareerEntries))
                return;

            var parsed = KerbalCareerLogEntry.ParseSet(action.KerbalCareerEntries);
            if (parsed.Count == 0)
                return;

            KerbalCareerEntries accumulator;
            if (!careerEntriesByKerbal.TryGetValue(action.KerbalName, out accumulator))
            {
                accumulator = new KerbalCareerEntries();
                careerEntriesByKerbal[action.KerbalName] = accumulator;
            }
            accumulator.UnionWith(parsed);
        }

        /// <summary>
        /// Snapshot of every kerbal's accumulated career-log entries. Read by the
        /// LedgerGroundTruth harness to build the KerbalXp reconstruction facet.
        /// </summary>
        internal Dictionary<string, HashSet<KerbalCareerLogEntry>> SnapshotCareerEntries()
        {
            var result = new Dictionary<string, HashSet<KerbalCareerLogEntry>>(StringComparer.Ordinal);
            foreach (var kvp in careerEntriesByKerbal)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                result[kvp.Key] = new HashSet<KerbalCareerLogEntry>(kvp.Value.Entries);
            }
            return result;
        }

        /// <summary>
        /// The accumulated career-log entries for one kerbal, or null. Test/diagnostic seam.
        /// </summary>
        internal KerbalCareerEntries GetCareerEntriesForTesting(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName)) return null;
            KerbalCareerEntries accumulator;
            return careerEntriesByKerbal.TryGetValue(kerbalName, out accumulator)
                ? accumulator
                : null;
        }

        internal interface IKerbalRosterFacade
        {
            bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status);
            bool TryCreateGeneratedStandIn(string trait, out string generatedName);
            bool TryRecreateStandIn(string desiredName, string trait);
            bool TryRemove(string name);

            /// <summary>
            /// True if the named kerbal is currently a crew member on a loaded
            /// non-ghost vessel in the active scene. Retained for
            /// diagnostics / legacy callers; the
            /// <see cref="ApplyToRoster"/> guard now uses the pid-scoped
            /// <see cref="IsKerbalOnVesselWithPid"/> instead so a stale
            /// name-only rescue marker cannot suppress an unrelated fresh
            /// reservation for the same kerbal who happens to be on the
            /// active player vessel (#615 P1 review fourth pass).
            /// </summary>
            bool IsKerbalOnLiveVessel(string kerbalName);

            /// <summary>
            /// True if the named kerbal is currently a crew member on the
            /// loaded non-ghost vessel whose <see cref="Vessel.persistentId"/>
            /// equals <paramref name="vesselPersistentId"/>. Used by the
            /// <see cref="ApplyToRoster"/> rescue-completion guard to scope
            /// the rescue marker to the actual vessel the rescue placed the
            /// kerbal onto: a stale marker pointing at a destroyed or
            /// stale-pid vessel produces <c>false</c> here, so the guard
            /// declines and the legitimate-recreate path runs.
            ///
            /// <para>
            /// P1 review (fourth pass): the previous "marker plus
            /// IsKerbalOnLiveVessel" predicate could fire on a later
            /// unrelated reservation for the same kerbal who was on the
            /// active player vessel — exactly the failure mode this
            /// pid-scoped check eliminates.
            /// </para>
            /// </summary>
            bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId);

            /// <summary>
            /// The kerbal's current career-log entries, or null when the kerbal is not on the
            /// roster. Read-only snapshot; the caller diffs against the ledger accumulator.
            /// </summary>
            List<KerbalCareerLogEntry> GetCareerLogEntries(string kerbalName);

            /// <summary>
            /// APPENDS the given career-log entries to the kerbal and triggers stock's XP
            /// recompute. Returns the number appended, or -1 when the kerbal is absent.
            ///
            /// <para>
            /// Append-only by contract - there is deliberately no remove counterpart, so no
            /// caller can turn the re-assert into a subtraction. The quicksave load has
            /// already removed the XP of superseded flights; this only puts back what a
            /// SURVIVING flight earned.
            /// </para>
            /// </summary>
            int AppendCareerLogEntries(
                string kerbalName, IReadOnlyList<KerbalCareerLogEntry> entries);
        }

        private sealed class KerbalRosterFacade : IKerbalRosterFacade
        {
            private readonly KerbalRoster roster;

            public KerbalRosterFacade(KerbalRoster roster)
            {
                this.roster = roster;
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                status = default(ProtoCrewMember.RosterStatus);
                if (roster == null || string.IsNullOrEmpty(name))
                    return false;

                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (pcm.name != name) continue;
                    status = pcm.rosterStatus;
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Finds a roster member by name across the WHOLE roster, not just
            /// <c>roster.Crew</c>: a recovered kerbal is Available, and the XP re-assert must
            /// reach them there.
            /// </summary>
            private ProtoCrewMember FindMember(string name)
            {
                if (roster == null || string.IsNullOrEmpty(name)) return null;
                try { return roster[name]; }
                catch { return null; }
            }

            public List<KerbalCareerLogEntry> GetCareerLogEntries(string kerbalName)
            {
                var member = FindMember(kerbalName);
                if (member == null || member.careerLog == null) return null;

                var result = new List<KerbalCareerLogEntry>();
                var log = member.careerLog;
                for (int i = 0; i < log.Count; i++)
                {
                    var entry = log[i];
                    if (entry == null || string.IsNullOrEmpty(entry.type)) continue;
                    result.Add(new KerbalCareerLogEntry(entry.flight, entry.type, entry.target));
                }
                return result;
            }

            public int AppendCareerLogEntries(
                string kerbalName, IReadOnlyList<KerbalCareerLogEntry> entries)
            {
                var member = FindMember(kerbalName);
                if (member == null || member.careerLog == null) return -1;
                if (entries == null || entries.Count == 0) return 0;

                int appended = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (string.IsNullOrEmpty(entry.Type)) continue;
                    member.careerLog.AddEntry(new FlightLog.Entry(
                        entry.Flight,
                        entry.Type,
                        string.IsNullOrEmpty(entry.Target) ? null : entry.Target));
                    appended++;
                }

                if (appended > 0)
                {
                    // Decompile-verified stock recompute: UpdateExperience re-derives
                    // `experience` via KerbalRoster.CalculateExperience(careerLog) and fires
                    // GameEvents.onKerbalLevelUp when the level actually changes.
                    member.UpdateExperience();
                }
                return appended;
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedName = null;
                if (roster == null)
                    return false;

                ProtoCrewMember newStandIn = roster.GetNewKerbal(
                    ProtoCrewMember.KerbalType.Crew);
                if (newStandIn == null)
                    return false;

                KerbalRoster.SetExperienceTrait(newStandIn, trait);
                generatedName = newStandIn.name;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                if (roster == null || string.IsNullOrEmpty(desiredName))
                    return false;

                var pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                if (pcm == null)
                    return false;

                pcm.ChangeName(desiredName);
                KerbalRoster.SetExperienceTrait(pcm, trait);
                return true;
            }

            public bool TryRemove(string name)
            {
                if (roster == null || string.IsNullOrEmpty(name))
                    return false;

                var pcm = FindInRoster(roster, name);
                if (pcm == null)
                    return false;

                roster.Remove(pcm);
                return true;
            }

            /// <summary>
            /// Walk loaded vessels (excluding the lightweight ghost-map ProtoVessels
            /// owned by <see cref="GhostMapPresence"/>) and return true if
            /// <paramref name="kerbalName"/> is on any of them. Mirrors the
            /// "existing crew" set built by <see cref="VesselSpawner.BuildExistingCrewSet"/>
            /// but checks a single name without materializing the full set.
            ///
            /// <para>
            /// Wrapped in a try/catch because <c>FlightGlobals</c> initializes
            /// against Unity's <c>Quaternion.Euler</c> (TypeInitializationException
            /// outside Unity runtime). xUnit harness tests that call
            /// <see cref="ApplyToRoster(KerbalRoster)"/> through the wrapper
            /// must still see the rescue-completion guard return false (no
            /// rescue happened in a headless test) instead of crashing.
            /// </para>
            /// </summary>
            public bool IsKerbalOnLiveVessel(string kerbalName)
            {
                if (string.IsNullOrEmpty(kerbalName)) return false;
                try
                {
                    var vessels = FlightGlobals.Vessels;
                    if (vessels == null) return false;
                    for (int v = 0; v < vessels.Count; v++)
                    {
                        var vessel = vessels[v];
                        if (vessel == null) continue;
                        if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
                        var crew = vessel.GetVesselCrew();
                        if (crew == null) continue;
                        for (int c = 0; c < crew.Count; c++)
                        {
                            var pcm = crew[c];
                            if (pcm == null) continue;
                            if (string.Equals(pcm.name, kerbalName, System.StringComparison.Ordinal))
                                return true;
                        }
                    }
                    return false;
                }
                catch (System.Exception ex)
                {
                    // FlightGlobals not initialized (xUnit test environment) or
                    // some other transient KSP-side failure. Fall through as if
                    // the kerbal is not on a live vessel — the recreate path
                    // will run, and the in-game playtest exercises the live
                    // path.
                    ParsekLog.Verbose("KerbalsModule",
                        $"IsKerbalOnLiveVessel: FlightGlobals access failed ({ex.GetType().Name}) — " +
                        $"treating '{kerbalName}' as not on live vessel");
                    return false;
                }
            }

            /// <summary>
            /// Walk loaded vessels (excluding ghost-map ProtoVessels) and
            /// return true if <paramref name="kerbalName"/> is on the vessel
            /// whose <see cref="Vessel.persistentId"/> equals
            /// <paramref name="vesselPersistentId"/>. Pid-scoped variant used
            /// by the #615 ApplyToRoster guard so a stale name-only marker
            /// cannot suppress an unrelated fresh reservation.
            /// </summary>
            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId)
            {
                if (string.IsNullOrEmpty(kerbalName)) return false;
                try
                {
                    var vessels = FlightGlobals.Vessels;
                    if (vessels == null) return false;
                    for (int v = 0; v < vessels.Count; v++)
                    {
                        var vessel = vessels[v];
                        if (vessel == null) continue;
                        if (vessel.persistentId != vesselPersistentId) continue;
                        if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
                        var crew = vessel.GetVesselCrew();
                        if (crew == null) return false;
                        for (int c = 0; c < crew.Count; c++)
                        {
                            var pcm = crew[c];
                            if (pcm == null) continue;
                            if (string.Equals(pcm.name, kerbalName, System.StringComparison.Ordinal))
                                return true;
                        }
                        // Pid matched but the kerbal is no longer on it.
                        return false;
                    }
                    return false;
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Verbose("KerbalsModule",
                        $"IsKerbalOnVesselWithPid: FlightGlobals access failed ({ex.GetType().Name}) — " +
                        $"treating '{kerbalName}' / vesselPid={vesselPersistentId} as not matched");
                    return false;
                }
            }
        }

        /// <summary>
        /// Makes the hires a clock advance reached (committed-future KerbalHire rows past the
        /// previous patch's cutoff, <see cref="LedgerOrchestrator.RecalculateAndPatchForClockAdvance"/>,
        /// or a later pass whose funds patch applies a row that crossing could not:
        /// <see cref="LedgerOrchestrator.ResolveCommittedHiresToMake"/>):
        /// a load back in time put each kerbal back among the applicants, and nothing else
        /// hires him when the clock reaches his row. Promotes an Applicant to Crew the way
        /// stock <c>KerbalRoster.HireApplicant</c> does minus its <c>OnCrewmemberHired</c>
        /// (decompiled: fire, then <c>type = Crew</c>), whose Funding listener would charge
        /// the cost a second time; the row's cost reaches the funds patch. Anyone not an
        /// Applicant is left alone. Returns the number promoted.
        /// </summary>
        internal static int PromoteReachedCommittedHires(
            IList<string> kerbalNames,
            Func<string, ProtoCrewMember.KerbalType?> readType,
            Func<string, bool> promote)
        {
            if (kerbalNames == null || kerbalNames.Count == 0 || readType == null || promote == null)
                return 0;
            int promoted = 0, notApplicant = 0, missing = 0, failed = 0;
            for (int i = 0; i < kerbalNames.Count; i++)
            {
                string name = kerbalNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                ProtoCrewMember.KerbalType? type = readType(name);
                if (!type.HasValue)
                {
                    missing++;
                    ParsekLog.Warn(Tag,
                        $"Committed hire reached but '{name}' is not on the roster; nothing to hire");
                    continue;
                }
                if (type.Value != ProtoCrewMember.KerbalType.Applicant)
                {
                    notApplicant++;
                    ParsekLog.Verbose(Tag,
                        $"Committed hire reached: '{name}' already type={type.Value}; nothing to do");
                    continue;
                }
                if (promote(name))
                {
                    promoted++;
                    ParsekLog.Info(Tag,
                        $"Committed hire applied: '{name}' Applicant -> Crew as the clock reached its row " +
                        "(no stock hire event: the ledger row carries the cost)");
                }
                else
                {
                    failed++;
                    ParsekLog.Warn(Tag, $"Committed hire reached but promoting '{name}' failed");
                }
            }
            ParsekLog.Info(Tag,
                $"PromoteReachedCommittedHires: rows={kerbalNames.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"promoted={promoted.ToString(CultureInfo.InvariantCulture)} " +
                $"notApplicant={notApplicant.ToString(CultureInfo.InvariantCulture)} " +
                $"missing={missing.ToString(CultureInfo.InvariantCulture)} " +
                $"failed={failed.ToString(CultureInfo.InvariantCulture)}");
            return promoted;
        }

        /// <summary>Live wrapper over the pure core, on the game's roster.</summary>
        internal static int PromoteReachedCommittedHires(KerbalRoster roster, IList<string> kerbalNames)
        {
            if (roster == null)
            {
                ParsekLog.Verbose(Tag, "PromoteReachedCommittedHires: no roster - skipping");
                return 0;
            }
            using (SuppressionGuard.Crew())
            {
                return PromoteReachedCommittedHires(
                    kerbalNames,
                    name =>
                    {
                        ProtoCrewMember pcm = null;
                        try { pcm = roster[name]; }
                        catch { pcm = null; }
                        return pcm != null ? pcm.type : (ProtoCrewMember.KerbalType?)null;
                    },
                    name =>
                    {
                        ProtoCrewMember pcm = null;
                        try { pcm = roster[name]; }
                        catch { pcm = null; }
                        if (pcm == null) return false;
                        pcm.type = ProtoCrewMember.KerbalType.Crew;
                        return pcm.type == ProtoCrewMember.KerbalType.Crew;
                    });
            }
        }

        /// <summary>
        /// Makes the dismissals the clock reached (design section 9.12.1,
        /// <see cref="LedgerOrchestrator.ResolveCommittedDismissalsToMake"/>): a load back in
        /// time put each kerbal back on the crew list, and nothing else dismisses him when the
        /// clock reaches his row. Turns an Available Crew kerbal into an Applicant the way
        /// stock <c>KerbalRoster.SackAvailable</c> does minus its <c>OnCrewmemberSacked</c>
        /// (decompiled: <c>type = Applicant</c>, then fire): the ledger row is the record, and
        /// a second event would book a second row. A kerbal already not Crew is left alone
        /// (idempotent: every later pass names him again). A Crew kerbal who is not Available
        /// (on a flight, or lost) is left alone with a Warn and tried again next pass. Returns
        /// the number dismissed.
        /// </summary>
        internal static int ApplyReachedCommittedDismissals(
            IList<string> kerbalNames,
            Func<string, ProtoCrewMember.KerbalType?> readType,
            Func<string, ProtoCrewMember.RosterStatus?> readStatus,
            Func<string, bool> demote)
        {
            if (kerbalNames == null || kerbalNames.Count == 0 || readType == null
                || readStatus == null || demote == null)
                return 0;
            int dismissed = 0, notCrew = 0, missing = 0, notAvailable = 0, failed = 0;
            for (int i = 0; i < kerbalNames.Count; i++)
            {
                string name = kerbalNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                ProtoCrewMember.KerbalType? type = readType(name);
                // Every past dismissal is named on every pass, so the settled answers are
                // rate-limited per kerbal.
                if (!type.HasValue)
                {
                    missing++;
                    ParsekLog.VerboseRateLimited(Tag, "dismissal-missing|" + name,
                        $"Committed dismissal reached but '{name}' is not on the roster; nothing to dismiss");
                    continue;
                }
                if (type.Value != ProtoCrewMember.KerbalType.Crew)
                {
                    notCrew++;
                    ParsekLog.VerboseRateLimited(Tag, "dismissal-not-crew|" + name,
                        $"Committed dismissal reached: '{name}' already type={type.Value}; nothing to do");
                    continue;
                }
                ProtoCrewMember.RosterStatus? status = readStatus(name);
                if (status != ProtoCrewMember.RosterStatus.Available)
                {
                    notAvailable++;
                    ParsekLog.WarnRateLimited(Tag, "dismissal-not-available|" + name + "|" + status,
                        $"Committed dismissal reached but '{name}' is {(status.HasValue ? status.Value.ToString() : "(unknown)")}, " +
                        "not Available; left on the crew list, retried next pass");
                    continue;
                }
                if (demote(name))
                {
                    dismissed++;
                    ParsekLog.Info(Tag,
                        $"Committed dismissal applied: '{name}' Crew -> Applicant as the clock reached its row " +
                        "(no stock sack event: the ledger row is the record)");
                }
                else
                {
                    failed++;
                    ParsekLog.Warn(Tag, $"Committed dismissal reached but dismissing '{name}' failed");
                }
            }
            string summary =
                $"ApplyReachedCommittedDismissals: rows={kerbalNames.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"dismissed={dismissed.ToString(CultureInfo.InvariantCulture)} " +
                $"notCrew={notCrew.ToString(CultureInfo.InvariantCulture)} " +
                $"notAvailable={notAvailable.ToString(CultureInfo.InvariantCulture)} " +
                $"missing={missing.ToString(CultureInfo.InvariantCulture)} " +
                $"failed={failed.ToString(CultureInfo.InvariantCulture)}";
            // A pass that changed nothing (every named kerbal already dismissed) is the
            // standing state of every later recalc: Verbose, rate-limited.
            if (dismissed > 0 || notAvailable > 0 || failed > 0)
                ParsekLog.Info(Tag, summary);
            else
                ParsekLog.VerboseRateLimited(Tag, "dismissal-pass-settled", summary);
            return dismissed;
        }

        /// <summary>Live wrapper over the pure dismissal core, on the game's roster, with
        /// the crew events suppressed (the type change fires <c>onKerbalTypeChange</c>).</summary>
        internal static int ApplyReachedCommittedDismissals(KerbalRoster roster, IList<string> kerbalNames)
        {
            if (roster == null)
            {
                ParsekLog.Verbose(Tag, "ApplyReachedCommittedDismissals: no roster - skipping");
                return 0;
            }
            Func<string, ProtoCrewMember> find = name =>
            {
                try { return roster[name]; }
                catch { return null; }
            };
            using (SuppressionGuard.Crew())
            {
                return ApplyReachedCommittedDismissals(
                    kerbalNames,
                    name =>
                    {
                        ProtoCrewMember pcm = find(name);
                        return pcm != null ? pcm.type : (ProtoCrewMember.KerbalType?)null;
                    },
                    name =>
                    {
                        ProtoCrewMember pcm = find(name);
                        return pcm != null ? pcm.rosterStatus : (ProtoCrewMember.RosterStatus?)null;
                    },
                    name =>
                    {
                        ProtoCrewMember pcm = find(name);
                        if (pcm == null) return false;
                        pcm.type = ProtoCrewMember.KerbalType.Applicant;
                        return pcm.type == ProtoCrewMember.KerbalType.Applicant;
                    });
            }
        }

        /// <summary>
        /// Apply derived kerbal state to the KSP roster. Creates stand-ins,
        /// removes unused displaced stand-ins, and populates the crewReplacements
        /// dict for SwapReservedCrewInFlight.
        ///
        /// Reserved kerbals are left at their natural rosterStatus (typically
        /// Available). The VAB/SPH crew assignment dialog lists them greyed and refuses
        /// every seat placement (StockUiCrewDialogDecoration). KerbalDismissalPatch
        /// prevents dismissal.
        ///
        /// Crew death respawn (owner ruling S8): a recorded death holds its kerbal until
        /// death UT + the respawn timer stamped at the death when stock respawn was on
        /// then (stock: Dead -> Missing -> Available at that instant), and forever when it
        /// was off or no policy was stamped. No rosterStatus is written either way: after
        /// the respawn the hold is released and the kerbal is whatever stock left him
        /// (Available after stock's own respawn, or Available all along when a rewind put
        /// the death in the future). While a hold is in force the crew dialog refuses him.
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

            ApplyToRoster(new KerbalRosterFacade(roster));
        }

        internal void ApplyToRoster(IKerbalRosterFacade roster)
        {
            if (roster == null)
            {
                ParsekLog.Verbose(Tag, "ApplyToRoster: no roster facade — skipping");
                return;
            }

            using (SuppressionGuard.Crew())
            {
                int standInsCreated = 0, standInsRecreated = 0;
                int deletedUnused = 0, retiredDisplaced = 0, retainedLive = 0;
                ApplyTombstonedRosterCleanup(roster);
                var recreatedNames = new HashSet<string>();

                // Step 1: Create missing stand-ins
                int skippedRescuedOriginal = 0;
                int guardDeclinedLiveButNoMarker = 0;
                int guardDeclinedMarkerButNotLive = 0;
                int guardDeclinedMarkerStalePid = 0;
                int guardRetiredForNewReservation = 0;
                foreach (var kvp in slots)
                {
                    var slot = kvp.Value;
                    for (int i = 0; i < slot.Chain.Count; i++)
                    {
                        if (!ShouldEnsureChainEntryInRoster(slot, i))
                            continue;

                        // Rescue-completion guard (#615): the post-spawn rescue
                        // path (#608/#609) restores the reserved+Missing original
                        // kerbal to Available and lets the snapshot place them
                        // back into the spawned vessel. The recording still
                        // contributes its KerbalAssignment action to ELS, so the
                        // reservation is rebuilt on every recalc walk and the
                        // historical chain entry survives across walks. Without
                        // this guard, the recreate path below would re-spawn the
                        // stand-in that the spawn's UnreserveCrewInSnapshot just
                        // removed — observable as the "Recreated stand-in"
                        // info log firing once per recalc walk for kerbals the
                        // player can already see in their pod.
                        //
                        // P1 review (fourth pass): the predicate is now
                        // PID-SCOPED. The rescue-placed marker is keyed by
                        // (kerbalName -> rescued vessel persistentId), and the
                        // guard only fires when the kerbal is currently on
                        // the SAME vessel where the rescue placed them.
                        // Earlier rounds combined a name-only marker with a
                        // generic IsKerbalOnLiveVessel check; that regressed
                        // when a stale marker from a long-past rescue
                        // suppressed a later UNRELATED reservation for the
                        // same kerbal who happened to be on the active player
                        // vessel — SwapReservedCrewInFlight then had no
                        // stand-in to swap. Pid scoping ensures: a stale
                        // marker pointing at a destroyed / unrelated pid
                        // produces IsKerbalOnVesselWithPid=false; the guard
                        // declines; the legitimate-recreate path runs.
                        //
                        // The "person being replaced at depth i" is the slot
                        // owner at depth 0 and the prior chain entry at
                        // deeper levels.
                        string replacedName = i == 0 ? slot.OwnerName : slot.Chain[i - 1];
                        // RESCUE-PLACED-MARKER-NEVER-RETIRES: a row from a recording that
                        // started after the rescue is a flight flown after it, a new
                        // reservation the rescue did not complete. Retire the marker so this
                        // and every later walk give that reservation its stand-in. Re-deriving
                        // the rescue's own reservation reads the same pre-rescue rows and
                        // leaves the marker in place.
                        KerbalReservation replacedReservation;
                        if (!string.IsNullOrEmpty(replacedName)
                            && reservations.TryGetValue(replacedName, out replacedReservation)
                            && CrewReservationManager.TryRetireRescuePlacedForNewReservation(
                                replacedName, replacedReservation.LatestRowStartUT))
                            guardRetiredForNewReservation++;
                        ulong rescuedVesselPid = 0UL;
                        bool isRescuePlaced = !string.IsNullOrEmpty(replacedName)
                            && CrewReservationManager.TryGetRescuePlacedVessel(
                                replacedName, out rescuedVesselPid);
                        bool isOnRescuedVessel = isRescuePlaced
                            && roster.IsKerbalOnVesselWithPid(replacedName, rescuedVesselPid);
                        // Diagnostic only — surfaces the legacy "live somewhere"
                        // signal in declined-branch logs so KSP.log can show
                        // the kerbal moved to a different live vessel vs.
                        // the rescued vessel being gone entirely.
                        bool isOnAnyLiveVessel = !string.IsNullOrEmpty(replacedName)
                            && roster.IsKerbalOnLiveVessel(replacedName);
                        if (isRescuePlaced && isOnRescuedVessel)
                        {
                            string standInLabel = slot.Chain[i] ?? "<pending>";
                            ParsekLog.Verbose(Tag,
                                $"Rescue-completion guard: kerbal '{replacedName}' already placed on " +
                                $"rescued vessel pid={rescuedVesselPid} via rescue path " +
                                $"(rescuePlacedPid={rescuedVesselPid}, onRescuedVessel=true) " +
                                $"— skipping stand-in '{standInLabel}' " +
                                $"for slot '{slot.OwnerName}' depth {i} " +
                                "(marker persistent — not consumed on fire; pid-scoped)");
                            skippedRescuedOriginal++;
                            // P1 review (third pass): the marker is NOT
                            // consumed here. The reservation slot is rebuilt
                            // on every recalc walk while the historical chain
                            // entry survives in slot.Chain, so the guard must
                            // fire on EVERY subsequent ApplyToRoster pass for
                            // the lifetime of the rescue. The marker is
                            // saved with the crew replacements, replaced by
                            // the save's own set on load (LoadCrewReplacements),
                            // and cleared by RestoreReplacements /
                            // ClearReplacements / ResetReplacementsForTesting
                            // at rewind / wipe-all boundaries.
                            //
                            // P1 review (fourth pass): the marker is now
                            // pid-scoped, so a stale-true name marker that
                            // points at a destroyed / unrelated pid no longer
                            // suppresses unrelated reservations — the
                            // pid-scoped IsKerbalOnVesselWithPid check above
                            // returns false for those cases.
                            continue;
                        }
                        if (!isRescuePlaced && isOnAnyLiveVessel)
                        {
                            // P1 review case: legitimate fresh reservation
                            // where the player has the original on the active
                            // vessel without ever passing through the rescue
                            // path. Stand-in must still be generated /
                            // recreated so SwapReservedCrewInFlight has a
                            // mapping to swap with. Log the decision so
                            // KSP.log shows the guard considered + declined.
                            ParsekLog.Verbose(Tag,
                                $"Rescue-completion guard declined: kerbal '{replacedName}' on a live " +
                                $"vessel but no rescue marker — proceeding with stand-in for slot " +
                                $"'{slot.OwnerName}' depth {i} (legitimate fresh reservation)");
                            guardDeclinedLiveButNoMarker++;
                        }
                        else if (isRescuePlaced && !isOnAnyLiveVessel)
                        {
                            // Marker present but the kerbal is on no live
                            // vessel at all (e.g. destroyed after rescue).
                            // The stand-in is genuinely needed; log the
                            // decision so a stale marker is visible in
                            // KSP.log.
                            guardDeclinedMarkerButNotLive++;
                        }
                        else if (isRescuePlaced && isOnAnyLiveVessel && !isOnRescuedVessel)
                        {
                            // P1 review (fourth pass): marker present and
                            // the kerbal IS on a live vessel — but NOT the
                            // one where the rescue placed them. The marker
                            // is stale relative to this reservation: either
                            // the player switched the kerbal to a different
                            // vessel, or this is a fresh unrelated
                            // reservation for the same kerbal whose old
                            // rescue marker never got cleared. The guard
                            // declines; the legitimate-recreate path runs;
                            // KSP.log surfaces the divergence between the
                            // marker pid and the kerbal's current vessel.
                            ParsekLog.Info(Tag,
                                $"Stand-in recreate: rescue marker stale (kerbal '{replacedName}' " +
                                $"moved off rescued vessel pid={rescuedVesselPid}) — " +
                                $"proceeding with stand-in for slot '{slot.OwnerName}' depth {i} " +
                                "(P1 review fourth pass: pid-scoped guard declines on stale marker)");
                            guardDeclinedMarkerStalePid++;
                        }

                        if (slot.Chain[i] != null)
                        {
                            // Verify stand-in still exists in roster
                            ProtoCrewMember.RosterStatus existingStatus;
                            if (!roster.TryGetStatus(slot.Chain[i], out existingStatus))
                            {
                                // Stand-in was removed (e.g., KSP cleanup) — recreate.
                                // Verbose preamble pins the legitimate-recreation
                                // path so KSP.log shows the rescue guard above
                                // declined and this fall-through path fired instead.
                                ParsekLog.Verbose(Tag,
                                    $"Stand-in recreate: '{slot.Chain[i]}' missing from roster, " +
                                    $"replaced='{replacedName}' rescuePlaced={isRescuePlaced} " +
                                    $"onLiveVessel={isOnAnyLiveVessel} onRescuedVessel={isOnRescuedVessel} " +
                                    $"rescuedVesselPid={rescuedVesselPid} " +
                                    $"— proceeding to recreate for slot '{slot.OwnerName}' depth {i}");
                                if (!roster.TryRecreateStandIn(slot.Chain[i], slot.OwnerTrait))
                                    ParsekLog.Warn(Tag,
                                        $"Failed to recreate stand-in '{slot.Chain[i]}'");
                                else
                                {
                                    standInsRecreated++;
                                    recreatedNames.Add(slot.Chain[i]);
                                    ParsekLog.Info(Tag,
                                        $"Recreated stand-in '{slot.Chain[i]}' ({slot.OwnerTrait}) " +
                                        $"for slot '{slot.OwnerName}' depth {i}");
                                }
                            }
                            continue;
                        }

                        // Null entry = pending generation (new depth from PostWalk)
                        string generatedName;
                        if (roster.TryCreateGeneratedStandIn(slot.OwnerTrait, out generatedName))
                        {
                            slot.Chain[i] = generatedName;
                            standInsCreated++;
                            ParsekLog.Info(Tag,
                                $"Stand-in generated: '{generatedName}' ({slot.OwnerTrait}) " +
                                $"for slot '{slot.OwnerName}' depth {i}");
                        }
                        else
                        {
                            ParsekLog.Warn(Tag,
                                $"Failed to generate stand-in for slot '{slot.OwnerName}' depth {i}");
                        }
                    }
                }

                if (skippedRescuedOriginal > 0
                    || guardDeclinedLiveButNoMarker > 0
                    || guardDeclinedMarkerButNotLive > 0
                    || guardDeclinedMarkerStalePid > 0
                    || guardRetiredForNewReservation > 0)
                {
                    // P1 review (third pass): aggregate outcome of the
                    // rescue-completion guard for this walk so KSP.log shows
                    // fired vs. declined counts at a glance. The marker is
                    // PERSISTENT across walks (not consumed when the guard
                    // fires) — the slot is rebuilt every recalc pass while
                    // the historical chain entry survives, so the guard must
                    // observe the same marker on every subsequent walk for
                    // the lifetime of the rescue. Bulk lifecycle paths wipe
                    // the marker set on session boundaries.
                    //
                    // P1 review (fourth pass): the marker is pid-scoped, so
                    // a fourth bucket appears: declinedMarkerStalePid counts
                    // walks where the kerbal has a marker but is currently
                    // on a DIFFERENT live vessel — the legitimate-recreate
                    // path runs (the rescue marker is stale for this
                    // reservation).
                    ParsekLog.Info(Tag,
                        $"Rescue-completion guard summary: fired={skippedRescuedOriginal} " +
                        "(marker persistent — preserved across walks; pid-scoped) " +
                        $"declinedLiveButNoMarker={guardDeclinedLiveButNoMarker} " +
                        $"declinedMarkerButNotLive={guardDeclinedMarkerButNotLive} " +
                        $"declinedMarkerStalePid={guardDeclinedMarkerStalePid} " +
                        $"retiredForNewReservation={guardRetiredForNewReservation}");
                }
                if (skippedRescuedOriginal > 0)
                    ParsekLog.Info(Tag,
                        $"Rescue-completion guard fired: skipped {skippedRescuedOriginal} stand-in " +
                        "create/recreate(s) because the replaced kerbal is already on a live vessel");

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

                        bool isReserved = IsReservedNow(standIn);
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
                        else if (roster.IsKerbalOnLiveVessel(standIn))
                        {
                            // #625 — the stand-in was just seated on a freshly-loaded vessel
                            // by CrewAutoAssignPatch + ProtoVessel.Load, but no recording
                            // committed it yet (StartRecording runs after this OnLoad sweep).
                            // Without this branch the next step would TryRemove the stand-in
                            // from the roster, which orphans the seat names in the live
                            // ProtoVessel and the recording captures 0 crew.
                            retainedLive++;
                            ParsekLog.Info(Tag,
                                $"Stand-in '{standIn}' displaced -> retained (on live vessel)");
                        }
                        else
                        {
                            // Unused — remove from roster entirely
                            ProtoCrewMember.RosterStatus rosterStatus;
                            if (roster.TryGetStatus(standIn, out rosterStatus)
                                && rosterStatus == ProtoCrewMember.RosterStatus.Available
                                && roster.TryRemove(standIn))
                            {
                                deletedUnused++;
                                KerbalLoadRepairDiagnostics.RecordUnusedStandInDeleted(standIn);
                                ParsekLog.Info(Tag,
                                    $"Stand-in '{standIn}' displaced -> deleted (unused)");
                            }
                        }
                    }
                }

                // Step 3: Populate crewReplacements bridge (no rosterStatus changes —
                // the crew dialog's refusal handles assignment)
                CrewReservationManager.ClearReplacementsInternal();

                int reservedNow = 0;
                foreach (var kvp in reservations)
                {
                    // Bridge to SwapReservedCrewInFlight: map reserved -> active occupant.
                    // Only holds in force NOW: a returned owner must not be swapped out of
                    // the craft he boards.
                    if (!IsHoldInForce(kvp.Key, kvp.Value, walkClockUT))
                        continue;
                    reservedNow++;
                    string kerbalName = kvp.Key;
                    string occupant = GetActiveOccupant(kerbalName);
                    if (occupant != null)
                    {
                        CrewReservationManager.SetReplacement(kerbalName, occupant);
                    }
                }

                foreach (var kvp in slots)
                {
                    var slot = kvp.Value;
                    for (int i = slot.Chain.Count - 1; i >= 0; i--)
                    {
                        string standIn = slot.Chain[i];
                        if (standIn == null) continue;

                        bool isReserved = IsReservedNow(standIn);
                        bool usedInRecording = IsKerbalInAnyRecording(standIn);
                        if (!IsDisplacedChainEntry(slot, i) || isReserved || !usedInRecording)
                            continue;

                        ProtoCrewMember.RosterStatus currentStatus;
                        if (!roster.TryGetStatus(standIn, out currentStatus))
                            continue;

                        if (recreatedNames.Contains(standIn))
                            KerbalLoadRepairDiagnostics.RecordRetiredStandInRecreated(standIn);
                        else
                            KerbalLoadRepairDiagnostics.RecordRetiredStandInKept(standIn);
                    }
                }

                // Step 4 (P9a): MONOTONE career-log re-assert. Runs inside the same
                // SuppressionGuard.Crew() as everything else here.
                ReassertCareerLogEntries(roster);

                ParsekLog.Info(Tag,
                    $"ApplyToRoster complete: {slots.Count} slots, " +
                    $"{retiredKerbals.Count} retired, " +
                    $"{reservedNow} reserved, {reservations.Count - reservedNow} released, " +
                    $"{standInsCreated} created, {standInsRecreated} recreated, " +
                    $"{deletedUnused} deleted, {retiredDisplaced} displaced, " +
                    $"{retainedLive} retained-live");
            }
        }

        /// <summary>
        /// Pure decision core for the P9a re-assert: the entries the ledger credits that the
        /// roster's career log does not already carry. Returns an empty list when there is
        /// nothing to add.
        ///
        /// <para>
        /// This is a SET DIFFERENCE in one direction only. It never reports an entry the
        /// roster has but the ledger does not - the quicksave load has already removed the XP
        /// of superseded flights, and removing more would take away XP the player still owns
        /// (a stand-in's career, a kerbal recovered before the rewind point, or anything a
        /// mod wrote). The re-assert exists solely to put back what a SURVIVING flight earned
        /// and the rewind erased.
        /// </para>
        /// </summary>
        internal static List<KerbalCareerLogEntry> ResolveMissingCareerEntries(
            KerbalCareerEntries ledgerEntries, IReadOnlyList<KerbalCareerLogEntry> rosterEntries)
        {
            var missing = new List<KerbalCareerLogEntry>();
            if (ledgerEntries == null || ledgerEntries.Count == 0)
                return missing;

            var present = new HashSet<KerbalCareerLogEntry>();
            var deathFlights = new HashSet<int>();
            if (rosterEntries != null)
            {
                for (int i = 0; i < rosterEntries.Count; i++)
                {
                    present.Add(rosterEntries[i]);
                    if (IsDeathEntryType(rosterEntries[i].Type))
                        deathFlights.Add(rosterEntries[i].Flight);
                }
            }

            var ordered = ledgerEntries.ToOrderedList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (present.Contains(ordered[i]))
                    continue;

                // Never append into a flight group the roster already ended with a Die
                // entry. Decompile-verified: KerbalRoster.ExperienceAddFlight RESETS the
                // whole XP accumulator when a group's LAST entry is Die, and
                // FlightLog.GetFlights groups by contiguous runs of the same flight number.
                // A rewind rolls the flight counter back, so a post-rewind death can archive
                // a Die-terminated group carrying the same number an erased flight used;
                // appending there would land AFTER the Die entry, un-terminate the group and
                // cancel the death reset, scoring the dead flight's deeds again.
                if (deathFlights.Contains(ordered[i].Flight))
                    continue;

                missing.Add(ordered[i]);
            }
            return missing;
        }

        /// <summary>
        /// Stock's <c>FlightLog.EntryType.Die</c>, matched by NAME because the ledger stores
        /// the entry type as the string stock serializes.
        /// </summary>
        private static bool IsDeathEntryType(string type)
        {
            return string.Equals(type, "Die", StringComparison.Ordinal);
        }

        /// <summary>
        /// Appends every ledger-credited career-log entry the roster is missing, per kerbal,
        /// and lets stock recompute the XP. Never removes.
        ///
        /// <para>
        /// A kerbal the ledger credits but the roster does not have is SKIPPED with a counter,
        /// not created: this facet re-asserts experience, and manufacturing a roster member
        /// from an XP row would be a different (and much larger) claim.
        /// </para>
        /// </summary>
        private void ReassertCareerLogEntries(IKerbalRosterFacade roster)
        {
            if (roster == null || careerEntriesByKerbal.Count == 0)
                return;

            // THE deferral gate, and it is the whole correctness argument for an
            // IRREVERSIBLE write. Every other roster/pool mutation in this class is
            // re-derived idempotently from the current ELS on each recalc; appending a
            // career-log entry is the one thing that cannot be walked back (the facade has
            // no remove, deliberately). So the append is only safe when a row can never
            // leave the effective set AFTER being applied - and merge-time tombstoning is
            // exactly that. During a Re-Fly session the rewound origin branch's
            // KerbalExperience rows are STILL ELS-effective (ELS is ledger-minus-tombstones;
            // the session-suppressed subtree is a playback concept the recalc does not
            // consult), so the Step-5 post-load recalc would put the just-erased XP straight
            // back, and the merge that tombstones those rows minutes later could not undo it.
            // Deferring costs nothing: the merge and the discard both bump the tombstone
            // state version and recalc, and the re-assert then runs against a settled ELS.
            if (IsReFlySessionActive())
            {
                ParsekLog.Verbose(Tag,
                    $"Career-log re-assert: deferred - a Re-Fly session is active, so the " +
                    $"superseded branch's rows are still effective " +
                    $"(kerbals={careerEntriesByKerbal.Count})");
                return;
            }

            int kerbalsPatched = 0;
            int entriesAppended = 0;
            int alreadyComplete = 0;
            int absentKerbals = 0;
            foreach (var kvp in careerEntriesByKerbal)
            {
                var rosterEntries = roster.GetCareerLogEntries(kvp.Key);
                if (rosterEntries == null)
                {
                    absentKerbals++;
                    continue;
                }

                var missing = ResolveMissingCareerEntries(kvp.Value, rosterEntries);
                if (missing.Count == 0)
                {
                    alreadyComplete++;
                    continue;
                }

                int appended = roster.AppendCareerLogEntries(kvp.Key, missing);
                if (appended < 0)
                {
                    absentKerbals++;
                    continue;
                }
                kerbalsPatched++;
                entriesAppended += appended;
            }

            if (kerbalsPatched > 0 || absentKerbals > 0)
            {
                ParsekLog.Info(Tag,
                    $"Career-log re-assert: kerbals={careerEntriesByKerbal.Count} " +
                    $"patched={kerbalsPatched} entriesAppended={entriesAppended} " +
                    $"alreadyComplete={alreadyComplete} absent={absentKerbals}");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"Career-log re-assert: kerbals={careerEntriesByKerbal.Count} " +
                    $"all already complete");
            }
        }

        /// <summary>
        /// True while a Re-Fly session marker is live, i.e. while the fate of the rewound
        /// branch is undecided. Read through <see cref="ReFlySessionActiveOverrideForTesting"/>
        /// so the gate is drivable headlessly.
        /// </summary>
        internal static bool IsReFlySessionActive()
        {
            if (ReFlySessionActiveOverrideForTesting.HasValue)
                return ReFlySessionActiveOverrideForTesting.Value;

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                return false;
            return scenario.ActiveReFlySessionMarker != null;
        }

        internal void QueueTombstonedRosterKerbal(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return;
            pendingTombstonedRosterKerbals.Add(kerbalName);
            ParsekLog.Verbose(Tag,
                $"Queued tombstoned roster kerbal cleanup for '{kerbalName}'");
        }

        internal void QueueTombstonedRosterKerbals(IEnumerable<GameAction> actions)
        {
            if (actions == null)
                return;

            int queued = 0;
            foreach (var action in actions)
            {
                string kerbalName;
                if (!TryGetRosterCreatedKerbalName(action, out kerbalName))
                    continue;
                int before = pendingTombstonedRosterKerbals.Count;
                QueueTombstonedRosterKerbal(kerbalName);
                if (pendingTombstonedRosterKerbals.Count != before)
                    queued++;
            }

            if (queued > 0)
            {
                ParsekLog.Info(Tag,
                    $"Queued {queued} tombstoned roster kerbal cleanup candidate(s)");
            }
        }

        internal static bool TryGetRosterCreatedKerbalName(GameAction action, out string kerbalName)
        {
            kerbalName = null;
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.KerbalHire:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                    kerbalName = action.KerbalName;
                    return !string.IsNullOrEmpty(kerbalName);
                default:
                    return false;
            }
        }

        private void ApplyTombstonedRosterCleanup(IKerbalRosterFacade roster)
        {
            if (pendingTombstonedRosterKerbals.Count == 0)
                return;

            var pending = new List<string>(pendingTombstonedRosterKerbals);
            pendingTombstonedRosterKerbals.Clear();

            int removed = 0;
            int preserved = 0;
            int missing = 0;
            int skippedStatus = 0;
            int skippedLive = 0;
            int failedRemove = 0;
            int candidates = 0;
            int skippedEmpty = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                string name = pending[i];
                if (string.IsNullOrEmpty(name))
                {
                    skippedEmpty++;
                    continue;
                }

                candidates++;

                // The RAW map on purpose, not IsReservedNow: this is a deletion guard, and
                // a kerbal a surviving committed flight names (even one whose hold has
                // ended) must never be removed from the roster.
                if (ledgerCreatedKerbals.Contains(name)
                    || reservations.ContainsKey(name)
                    || IsKerbalInAnyRecording(name))
                {
                    preserved++;
                    continue;
                }

                ProtoCrewMember.RosterStatus status;
                if (!roster.TryGetStatus(name, out status))
                {
                    missing++;
                    continue;
                }

                if (status != ProtoCrewMember.RosterStatus.Available)
                {
                    skippedStatus++;
                    continue;
                }

                if (roster.IsKerbalOnLiveVessel(name))
                {
                    skippedLive++;
                    continue;
                }

                if (roster.TryRemove(name))
                    removed++;
                else
                    failedRemove++;
            }

            ParsekLog.Info(Tag,
                $"Tombstoned roster cleanup: candidates={candidates} removed={removed} " +
                $"preserved={preserved} missing={missing} skippedStatus={skippedStatus} " +
                $"skippedLive={skippedLive} failedRemove={failedRemove} skippedEmpty={skippedEmpty}");
        }

        private static ProtoCrewMember FindInRoster(KerbalRoster roster, string name)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == name) return pcm;
            }
            return null;
        }
    }
}
