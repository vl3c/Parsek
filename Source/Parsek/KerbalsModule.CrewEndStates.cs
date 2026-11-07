using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Crew end states: crew-name reverse mapping and seated stand-ins, end-state inference and population, split departures, terminal-stamp invalidation and the death respawn policy.
    internal partial class KerbalsModule : IResourceModule
    {
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

        /// <summary>
        /// The stand-in the snapshot recorded in <paramref name="ownerName"/>'s seat, or null.
        /// </summary>
        internal static string ReadSeatedStandIn(ConfigNode snapshot, string ownerName)
        {
            if (snapshot == null || string.IsNullOrEmpty(ownerName))
                return null;
            ConfigNode seats = snapshot.GetNode(SeatedStandInNodeName);
            if (seats == null)
                return null;
            string[] values = seats.GetValues(SeatedStandInValueName);
            if (values == null)
                return null;
            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i];
                if (string.IsNullOrEmpty(v)) continue;
                int bar = v.IndexOf('|');
                if (bar <= 0 || bar >= v.Length - 1) continue;
                if (string.Equals(v.Substring(0, bar), ownerName, System.StringComparison.Ordinal))
                    return v.Substring(bar + 1);
            }
            return null;
        }

        private static void RecordSeatedStandIn(ConfigNode snapshot, string ownerName, string standInName)
        {
            ConfigNode seats = snapshot.GetNode(SeatedStandInNodeName) ?? snapshot.AddNode(SeatedStandInNodeName);
            string entry = ownerName + "|" + standInName;
            string[] values = seats.GetValues(SeatedStandInValueName);
            if (values != null)
                for (int i = 0; i < values.Length; i++)
                    if (string.Equals(values[i], entry, System.StringComparison.Ordinal))
                        return;
            seats.AddValue(SeatedStandInValueName, entry);
        }

        internal static int ReverseMapCrewNamesInSnapshot(
            ConfigNode snapshot,
            IReadOnlyDictionary<string, string> replacements,
            string contextForLog)
        {
            if (snapshot == null)
                return 0;

            string ResolveOriginal(string name)
            {
                string found = null;
                if (replacements != null)
                {
                    foreach (var kvp in replacements)
                    {
                        if (string.Equals(kvp.Value, name, System.StringComparison.Ordinal))
                        {
                            found = kvp.Key;
                            break;
                        }
                    }
                }

                if (found == null)
                    found = TryReverseMapCrewNameFromSlots(name);
                return found;
            }

            int rewritten = 0;
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                if (partNode == null) continue;

                string[] crewNames = partNode.GetValues("crew");
                if (crewNames == null || crewNames.Length == 0) continue;

                var rewrittenList = new List<string>(crewNames.Length);
                bool partChanged = false;

                for (int i = 0; i < crewNames.Length; i++)
                {
                    string name = crewNames[i];
                    if (string.IsNullOrEmpty(name))
                    {
                        rewrittenList.Add(name);
                        continue;
                    }

                    string original = ResolveOriginal(name);

                    if (original != null && !string.Equals(original, name, System.StringComparison.Ordinal))
                    {
                        rewrittenList.Add(original);
                        RecordSeatedStandIn(snapshot, original, name);
                        partChanged = true;
                        rewritten++;
                    }
                    else
                    {
                        rewrittenList.Add(name);
                    }
                }

                if (partChanged)
                {
                    partNode.RemoveValues("crew");
                    for (int i = 0; i < rewrittenList.Count; i++)
                        partNode.AddValue("crew", rewrittenList[i]);
                }
            }

            // The captured crew inventories are keyed by the seat's name, so they follow
            // the same reverse map: the seat (and what its occupant carried) belongs to
            // the original kerbal. Not counted in the return value (crew entries only).
            int inventoryKeysRenamed = CrewInventorySnapshot.RenameKerbals(snapshot, ResolveOriginal);

            if (rewritten > 0 || inventoryKeysRenamed > 0)
            {
                // VerboseRateLimited (not Info): TryBackupSnapshot fires often (the
                // recorder's periodic snapshot refresh drives it ~1100+ times across a
                // long time-warped recording), and any vessel carrying a seated stand-in
                // rewrites a crew name on every one. Routine, not a notable event, so it
                // must not log unconditionally. The zero-rewrite case stays silent.
                ParsekLog.VerboseRateLimited(Tag, "reverse-map-crew",
                    () => $"ReverseMapCrewNamesInSnapshot: rewrote {rewritten} stand-in crew name(s) " +
                    $"back to originals in snapshot, {inventoryKeysRenamed} crew inventory key(s) " +
                    $"({contextForLog ?? "no-context"})");
            }

            return rewritten;
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
        /// The kerbals a child of one of <paramref name="rec"/>'s split branch points carried
        /// away: each child's start crew (its ghost snapshot, or its EVA kerbal), reverse-mapped
        /// like the parent's. A decoupled crew cabin with no command module, an undocked half,
        /// an EVA. Empty when the tree is unknown. Pure given the tree.
        /// </summary>
        internal static HashSet<string> CollectCrewCarriedAwayBySplit(
            Recording rec, RecordingTree tree, IReadOnlyDictionary<string, string> replacements)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)
                || tree?.BranchPoints == null || tree.Recordings == null)
                return result;

            // The recording itself and its earlier chain segments: an optimizer split keeps a
            // split before its cut on the earlier segment and copies the start crew to the later.
            var parentIds = new HashSet<string>(StringComparer.Ordinal) { rec.RecordingId };
            if (!string.IsNullOrEmpty(rec.ChainId))
            {
                foreach (Recording other in tree.Recordings.Values)
                {
                    if (other != null && !string.IsNullOrEmpty(other.RecordingId)
                        && IsEarlierChainSegment(other, rec))
                        parentIds.Add(other.RecordingId);
                }
            }
            double recEndUT = rec.EndUT;

            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                BranchPoint bp = tree.BranchPoints[b];
                if (bp == null || !IsSplitBranchPointType(bp.Type)
                    || bp.ParentRecordingIds == null || bp.ChildRecordingIds == null)
                    continue;
                bool isParent = false;
                for (int p = 0; p < bp.ParentRecordingIds.Count && !isParent; p++)
                    isParent = bp.ParentRecordingIds[p] != null && parentIds.Contains(bp.ParentRecordingIds[p]);
                if (!isParent)
                    continue;
                if (!double.IsNaN(recEndUT) && !double.IsNaN(bp.UT)
                    && bp.UT > recEndUT + RecoveryClosureEndToleranceSeconds)
                    continue;
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    string childId = bp.ChildRecordingIds[c];
                    Recording child;
                    if (string.IsNullOrEmpty(childId)
                        || string.Equals(childId, rec.RecordingId, StringComparison.Ordinal)
                        || !tree.Recordings.TryGetValue(childId, out child) || child == null)
                        continue;
                    var crew = CrewReservationManager.ExtractCrewFromSnapshot(child.GhostVisualSnapshot);
                    if (crew.Count == 0 && !string.IsNullOrEmpty(child.EvaCrewName))
                        crew.Add(child.EvaCrewName);
                    ReverseMapCrewNames(crew, replacements, null);
                    for (int k = 0; k < crew.Count; k++)
                        result.Add(crew[k]);
                }
            }
            return result;
        }

        /// <summary>
        /// A kerbal missing from the end of a recording reads as lost (<see cref="InferCrewEndState"/>:
        /// Dead), unless a split child carried him away first: then he left this vessel alive and
        /// the child's own row decides the rest, so this recording's answer is Unknown, which the
        /// walk ends at the split (SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS). A kerbal
        /// still in the end snapshot keeps his answer. Pure.
        /// </summary>
        internal static KerbalEndState ApplySplitDeparture(
            string crewName,
            KerbalEndState inferred,
            HashSet<string> endCrew,
            HashSet<string> carriedAway,
            string recordingIdForLog)
        {
            if (inferred != KerbalEndState.Dead || carriedAway == null || string.IsNullOrEmpty(crewName)
                || !carriedAway.Contains(crewName)
                || (endCrew != null && endCrew.Contains(crewName)))
                return inferred;
            ParsekLog.Verbose(Tag,
                $"InferCrewEndState: crew='{crewName}' recording='{recordingIdForLog}' left through a " +
                "split child before the end -> Unknown (not Dead)");
            return KerbalEndState.Unknown;
        }

        private static List<string> ExtractRawCrewFromRecording(Recording rec)
        {
            var result = new List<string>();
            if (rec == null)
                return result;

            var snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            for (int i = 0; i < crew.Count; i++)
                result.Add(crew[i]);

            if (result.Count == 0 && !string.IsNullOrEmpty(rec.EvaCrewName))
                result.Add(rec.EvaCrewName);

            return result;
        }

        /// <summary>
        /// The one condition under which <see cref="PopulateCrewEndStates(Recording)"/>
        /// can reach an answer at all: it reads the recording-START crew from the
        /// ghost visual snapshot, falling back to the EVA crew name. With neither
        /// present the method returns leaving the recording unresolved, so
        /// <see cref="InvalidateCrewEndStatesForTerminalStamp"/> must refuse to drop
        /// end states it could not have re-derived.
        /// </summary>
        internal static bool HasStartCrewSource(Recording rec)
        {
            return rec != null
                && (rec.GhostVisualSnapshot != null || !string.IsNullOrEmpty(rec.EvaCrewName));
        }

        /// <summary>
        /// Single invalidation seam behind <see cref="Recording.StampTerminalState"/>.
        ///
        /// <para>WHY: <c>PopulateCrewEndStates</c> latches BOTH <c>CrewEndStates</c> and the
        /// serialized <c>CrewEndStatesResolved</c> flag, and
        /// <c>LedgerOrchestrator.NeedsCrewEndStatePopulation</c> treats EITHER as a permanent
        /// skip. A re-fly fork gets its <c>VesselSnapshot</c> at rewind time, while
        /// <c>TerminalStateValue</c> is still null, so the very next recalc infers every crew
        /// member as <c>Unknown</c> (<c>InferCrewEndState</c>'s null-terminal answer) and latches
        /// that for the life of the recording. <c>Unknown</c> and <c>Aboard</c> produce identical
        /// reservations, but <c>Recovered</c> — the ONLY end state carrying a finite
        /// <c>endUT</c> — is unreachable once latched, so a re-flown-and-recovered kerbal stays
        /// reserved forever (the D12 <c>missed-endut-auto-free</c> surface).</para>
        ///
        /// <para>TRANSITION GUARD: fires on any stamp of a NON-NULL terminal that DIFFERS from
        /// the previous value — both null-&gt;X (the filed finding) and X-&gt;Y. X-&gt;Y is a real
        /// production transition, not a hypothetical: <c>ParsekScenario.CanOverwriteTerminalState</c>
        /// deliberately lets a situation-based verdict (Landed/Orbiting/Splashed/SubOrbital) be
        /// overwritten by Recovered or Destroyed, and
        /// <c>ParsekFlight.TryApplyActiveRecorderDestructionOverride</c> overrides any prior
        /// verdict with Destroyed. Those are exactly the transitions where re-inference matters
        /// (Aboard -&gt; Recovered buys the finite endUT; Aboard -&gt; Dead makes the reservation
        /// permanent).</para>
        ///
        /// <para>RETRACTIONS (stamping null) deliberately do NOT invalidate. A null terminal can
        /// only ever re-infer to <c>Unknown</c>, which is the latched state this seam exists to
        /// escape, and a retraction site (ClearStaleDestroyedTerminalForResume) would otherwise wipe end
        /// states derived from a real flight. Any later stamp runs this seam again.
        /// A split's first half is not a retraction through this seam: its end states move to
        /// the second half with the terminal (<c>RecordingOptimizer.MoveCrewEndStatesToSecondHalf</c>)
        /// and the first half re-derives through the chain-handoff rule.</para>
        ///
        /// <para>NON-LOSSY: end states are dropped only when the same predicate that drives
        /// population (<c>NeedsCrewEndStatePopulation</c>) re-admits the recording AND a start
        /// crew source still exists. Otherwise the previous answer is restored, because a
        /// dropped-and-never-re-derived recording would read as crewless everywhere
        /// (ledger rows and the Kerbals window alike).</para>
        ///
        /// <para>RE-INFERENCE IS IMMEDIATE, not deferred to the next recalc. The admission
        /// predicate reads the recording's snapshot surface, and several stamp sites go on to
        /// MUTATE that surface after stamping (the ghost-only commit paths null
        /// <c>VesselSnapshot</c> a few steps later). Deferring would let the guard judge a
        /// surface that no longer exists when population finally runs, stranding the recording
        /// unresolved for terminal states the ghost-visual-only branch does not admit. Inferring
        /// here, against the surface the guard actually judged, closes that window and leaves the
        /// recording consistent for every later reader.</para>
        ///
        /// Returns true when end states were actually invalidated and re-inferred.
        /// </summary>
        internal static bool InvalidateCrewEndStatesForTerminalStamp(
            Recording rec,
            TerminalState? previous,
            TerminalState? updated,
            string context)
        {
            if (rec == null) return false;

            // Retraction, or no actual change: nothing to re-infer against.
            if (!updated.HasValue) return false;
            if (previous.HasValue && previous.Value == updated.Value) return false;

            // Nothing to invalidate. A crewless recording resolves with a null dictionary
            // (PopulateCrewEndStates' "no crew in ghost snapshot" branch) — re-running it
            // would produce the identical answer, so leave the resolved flag alone.
            if (rec.CrewEndStates == null || rec.CrewEndStates.Count == 0) return false;

            var savedStates = rec.CrewEndStates;
            bool savedResolved = rec.CrewEndStatesResolved;
            bool invalidated = false;

            // The admission predicate is a one-shot skip on either field, so both have to be
            // cleared before it can answer. The finally restores them on every path that does
            // NOT complete the re-inference — refusal or exception alike.
            rec.CrewEndStates = null;
            rec.CrewEndStatesResolved = false;
            try
            {
                if (!HasStartCrewSource(rec)
                    || !LedgerOrchestrator.NeedsCrewEndStatePopulation(rec))
                {
                    ParsekLog.Verbose(Tag,
                        CrewEndStateStaleAfterTerminalStampKey +
                        $": kept {savedStates.Count} end state(s) on recording " +
                        $"'{rec.RecordingId ?? "(null)"}' — {previous?.ToString() ?? "null"}->" +
                        $"{updated.Value} is not re-derivable " +
                        $"(startCrewSource={HasStartCrewSource(rec)}, context={context ?? "(none)"})");
                    return false;
                }

                PopulateCrewEndStates(rec);
                invalidated = rec.CrewEndStatesResolved;
                if (!invalidated)
                {
                    // Population declined to answer after all — keep the old verdict rather
                    // than leaving the recording permanently unresolved.
                    ParsekLog.Verbose(Tag,
                        CrewEndStateStaleAfterTerminalStampKey +
                        $": kept {savedStates.Count} end state(s) on recording " +
                        $"'{rec.RecordingId ?? "(null)"}' — re-inference against " +
                        $"{updated.Value} produced no answer (context={context ?? "(none)"})");
                    return false;
                }

                ParsekLog.Info(Tag,
                    CrewEndStateStaleAfterTerminalStampKey +
                    $": recording='{rec.RecordingId ?? "(null)"}' ({rec.VesselName ?? "(null)"}) " +
                    $"terminal {previous?.ToString() ?? "null"}->{updated.Value} — dropped " +
                    $"{savedStates.Count} end state(s) inferred against the old verdict and " +
                    $"re-inferred {rec.CrewEndStates?.Count ?? 0} " +
                    $"(context={context ?? "(none)"})");
                return true;
            }
            finally
            {
                if (!invalidated)
                {
                    rec.CrewEndStates = savedStates;
                    rec.CrewEndStatesResolved = savedResolved;
                }
            }
        }

        // ────────────────────────────────────────────────────────
        // Crew death respawn policy (stock Difficulty.MissingCrewsRespawn / RespawnTimer)
        // ────────────────────────────────────────────────────────

        /// <summary>Reads the live stock crew respawn policy. See
        /// <see cref="CrewRespawnPolicyProviderForTesting"/>.</summary>
        internal delegate bool CrewRespawnPolicyReader(out bool respawns, out double timerSeconds);

        /// <summary>Reads one kerbal's stock roster respawn state: true when the roster holds
        /// the kerbal as Missing, with stock's respawn instant <c>UTaR</c>.</summary>
        internal delegate bool MissingRespawnReader(string kerbalName, out double utar);

        /// <summary>
        /// True when stock's roster holds <paramref name="kerbalName"/> as Missing, with its
        /// respawn instant: stock started the respawn period at the death
        /// (<c>StartRespawnPeriod</c>, only with respawn on), and <c>UTaR</c> is the death
        /// plus the timer as it stood then. False with no roster (headless host).
        /// </summary>
        internal static bool TryReadMissingRespawnUT(string kerbalName, out double utar)
        {
            var provider = MissingRespawnProviderForTesting;
            if (provider != null)
                return provider(kerbalName, out utar);
            try
            {
                return ReadMissingRespawnUTCore(kerbalName, out utar);
            }
            catch (Exception)
            {
                utar = double.NaN;
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadMissingRespawnUTCore(string kerbalName, out double utar)
        {
            utar = double.NaN;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null || string.IsNullOrEmpty(kerbalName))
                return false;
            ProtoCrewMember pcm = roster[kerbalName];
            if (pcm == null || pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing)
                return false;
            utar = pcm.UTaR;
            return !double.IsNaN(utar) && !double.IsInfinity(utar);
        }

        /// <summary>
        /// Career settings audit 2026-11-04 section 3.6: the respawn timer a recorded death
        /// stamps is stock's own decision at the death, read from the roster, never the live
        /// slider at commit (the difficulty dialog is reachable from the flight pause menu, so
        /// the timer can change between the death and the commit). Pure over
        /// <paramref name="readMissing"/>: when any dead kerbal is Missing in the roster the
        /// death respawns, and the timer is the longest <c>UTaR - deathUT</c> among them
        /// (never below 0), so the respawn instant is stock's own. False when no dead kerbal
        /// is Missing: the caller falls back to the live policy.
        /// </summary>
        internal static bool TryResolveRespawnStampFromRoster(
            IEnumerable<string> deadNames, double deathUT, MissingRespawnReader readMissing,
            out double respawnSeconds, out string evidence)
        {
            respawnSeconds = double.NaN;
            evidence = null;
            if (deadNames == null || readMissing == null || double.IsNaN(deathUT))
                return false;
            foreach (string name in deadNames)
            {
                double utar;
                if (!readMissing(name, out utar))
                    continue;
                double seconds = Math.Max(0.0, utar - deathUT);
                if (double.IsNaN(respawnSeconds) || seconds > respawnSeconds)
                {
                    respawnSeconds = seconds;
                    evidence = name + " Missing until UTaR=" + utar.ToString("R", CultureInfo.InvariantCulture);
                }
            }
            return !double.IsNaN(respawnSeconds);
        }

        /// <summary>
        /// The live stock crew respawn policy: <c>Difficulty.MissingCrewsRespawn</c> and
        /// <c>Difficulty.RespawnTimer</c> (seconds). Stock's crew death (decompiled
        /// <c>Part</c> death path) calls <c>Die()</c> and then, only when the flag is on,
        /// <c>StartRespawnPeriod()</c> (Missing until death + RespawnTimer, then
        /// Available). False when no game is loaded (null-guarded).
        /// </summary>
        internal static bool TryReadStockCrewRespawnPolicy(out bool respawns, out double timerSeconds)
        {
            var provider = CrewRespawnPolicyProviderForTesting;
            if (provider != null)
                return provider(out respawns, out timerSeconds);
            try
            {
                return ReadStockCrewRespawnPolicyCore(out respawns, out timerSeconds);
            }
            catch (Exception)
            {
                // Headless host (xUnit / mono) where HighLogic cannot initialise.
                respawns = false;
                timerSeconds = double.NaN;
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadStockCrewRespawnPolicyCore(out bool respawns, out double timerSeconds)
        {
            respawns = false;
            timerSeconds = double.NaN;
            var difficulty = HighLogic.CurrentGame?.Parameters?.Difficulty;
            if (difficulty == null)
                return false;
            respawns = difficulty.MissingCrewsRespawn;
            timerSeconds = difficulty.RespawnTimer;
            return true;
        }

        /// <summary>
        /// Owner ruling S8 (2026-09-26): a recorded crew death follows the stock respawn
        /// policy AS IT STOOD AT THE DEATH. Stamps the live policy onto
        /// <paramref name="rec"/> the first time its end states record a death, and never
        /// again: a stamp already present is kept (a re-inference after a terminal re-stamp,
        /// a merge that carried the absorbed segment's stamp), so a difficulty edit after
        /// the death does not rewrite it. With no readable game the recording stays
        /// unstamped and its deaths stay permanent. Returns true when a stamp was written.
        /// </summary>
        internal static bool StampCrewDeathRespawnPolicy(Recording rec, int deadCount)
        {
            if (rec == null || deadCount <= 0) return false;
            if (rec.CrewDeathRespawns.HasValue)
            {
                ParsekLog.Verbose(Tag,
                    $"Crew death respawn policy kept: recording='{rec.RecordingId}' " +
                    $"respawn={rec.CrewDeathRespawns.Value} " +
                    $"timer={rec.CrewDeathRespawnSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                    "(stamped at the first recorded death)");
                return false;
            }

            bool respawns;
            double timerSeconds;
            string rosterEvidence;
            if (TryResolveRespawnStampFromRoster(DeadCrewNames(rec), rec.EndUT,
                    TryReadMissingRespawnUT, out timerSeconds, out rosterEvidence))
            {
                rec.CrewDeathRespawns = true;
                rec.CrewDeathRespawnSeconds = timerSeconds;
                ParsekLog.Info(Tag,
                    $"Crew death respawn policy stamped from the roster: recording='{rec.RecordingId}' " +
                    $"({rec.VesselName}) deadCrew={deadCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"respawn=True timer={timerSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"({rosterEvidence}; stock's own respawn instant, whatever the slider reads now)");
                return true;
            }
            if (!TryReadStockCrewRespawnPolicy(out respawns, out timerSeconds))
            {
                ParsekLog.Verbose(Tag,
                    $"Crew death respawn policy not stamped: recording='{rec.RecordingId}' " +
                    $"deadCrew={deadCount.ToString(CultureInfo.InvariantCulture)} - no game difficulty " +
                    "readable; the death stays permanent");
                return false;
            }

            rec.CrewDeathRespawns = respawns;
            rec.CrewDeathRespawnSeconds = timerSeconds;
            ParsekLog.Info(Tag,
                $"Crew death respawn policy stamped: recording='{rec.RecordingId}' " +
                $"({rec.VesselName}) deadCrew={deadCount.ToString(CultureInfo.InvariantCulture)} " +
                $"respawn={respawns} timer={timerSeconds.ToString("R", CultureInfo.InvariantCulture)}");
            return true;
        }

        private static IEnumerable<string> DeadCrewNames(Recording rec)
        {
            if (rec?.CrewEndStates == null)
                yield break;
            foreach (KeyValuePair<string, KerbalEndState> kv in rec.CrewEndStates)
                if (kv.Value == KerbalEndState.Dead)
                    yield return kv.Key;
        }

        /// <summary>
        /// The instant a recorded death releases its kerbal under the stamped respawn
        /// policy: <paramref name="deathUT"/> + <paramref name="respawnSeconds"/> when the
        /// policy was stamped ON with a usable timer, else NaN (a permanent death).
        /// <paramref name="reason"/> names the decision for the walk's log line. Pure.
        /// </summary>
        internal static double ResolveDeathRespawnUT(
            bool? respawns, double respawnSeconds, double deathUT, out string reason)
        {
            if (!respawns.HasValue)
            {
                reason = "no respawn policy stamped at the death";
                return double.NaN;
            }
            if (!respawns.Value)
            {
                reason = "stock crew respawn was off at the death";
                return double.NaN;
            }
            if (double.IsNaN(respawnSeconds) || double.IsInfinity(respawnSeconds) || respawnSeconds < 0.0)
            {
                reason = "stamped respawn timer is not usable";
                return double.NaN;
            }
            if (double.IsNaN(deathUT) || double.IsInfinity(deathUT))
            {
                reason = "death UT is not usable";
                return double.NaN;
            }
            reason = "stock crew respawn was on at the death";
            return deathUT + respawnSeconds;
        }

        /// <summary>
        /// Populates CrewEndStates on a recording by extracting crew from the
        /// ghost visual snapshot (start-of-recording crew roster) and inferring
        /// each crew member's end state.
        /// </summary>
        internal static void PopulateCrewEndStates(Recording rec, RecordingTree ownerTree = null)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "PopulateCrewEndStates: null recording -- skipping");
                return;
            }

            bool hasStartCrewSource = HasStartCrewSource(rec);

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
            // The tree that owns the recording: a commit preview passes its pending tree, whose
            // split branch points are not committed yet.
            HashSet<string> carriedAway = CollectCrewCarriedAwayBySplit(
                rec, ownerTree ?? RecordingStore.FindCommittedTreeById(rec.TreeId), replacements);

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            int aboardCount = 0, deadCount = 0, recoveredCount = 0, unknownCount = 0;

            for (int i = 0; i < startingCrew.Count; i++)
            {
                string name = startingCrew[i];
                var state = useGhostOnlyChainHandoffFallback
                    ? InferGhostOnlyChainHandoffEndState(rec.TerminalStateValue)
                    : InferCrewEndState(name, rec.TerminalStateValue, endCrewSet);
                state = ApplySplitDeparture(name, state, endCrewSet, carriedAway, rec.RecordingId);
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
            StampCrewDeathRespawnPolicy(rec, deadCount);
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

            while (IsReservedNow(currentOccupant))
            {
                if (depth >= slot.Chain.Count)
                {
                    // Need a new stand-in at this depth -- will be created by ApplyToRoster
                    // For now, mark as needing generation (name = null)
                    slot.Chain.Add(null); // placeholder -- ApplyToRoster fills with real name
                    KerbalLoadRepairDiagnostics.RecordChainExtension(slot.OwnerName, depth);
                    ParsekLog.Verbose(Tag,
                        $"Chain depth {depth} needed for slot '{slot.OwnerName}' -- pending generation");
                }

                currentOccupant = slot.Chain[depth];
                if (currentOccupant == null) break; // pending generation, stop walking
                depth++;
            }
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
    }
}
