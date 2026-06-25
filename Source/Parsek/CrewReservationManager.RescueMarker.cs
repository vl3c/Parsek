using System;
using System.Collections.Generic;

namespace Parsek
{
    internal static partial class CrewReservationManager
    {
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
    }
}
