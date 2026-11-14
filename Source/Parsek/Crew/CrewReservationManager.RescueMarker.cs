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
        //   - Saved with the crew replacements (RESCUE_PLACED_KERBALS) and
        //     replaced by the save's own set in LoadCrewReplacements, so a
        //     save and load keeps the guard firing. Bulk-cleared by
        //     RestoreReplacements / ClearReplacements /
        //     ResetReplacementsForTesting on rewind / wipe-all boundaries.
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
        //   - Retired per kerbal (RESCUE-PLACED-MARKER-NEVER-RETIRES) when the
        //     kerbal's reservation gains a row from a recording that started
        //     after the rescue (MarkUT): that recording was flown after
        //     the rescue, so its reservation is not the one the rescue
        //     completed and it gets a stand-in like any fresh reservation, even
        //     while the kerbal is still aboard the rescue vessel pid. A walk
        //     re-deriving the rescue's own reservation reads the same
        //     pre-rescue rows and never retires. A marker with no MarkUT (NaN)
        //     is never retired this way.
        //   - Left out of the save when its vessel pid matches no vessel in
        //     FlightGlobals.Vessels nor flightState.protoVessels, in a scene
        //     whose vessel lists are populated (CollectSavableRescueMarkers).
        //     Memory keeps it until the next bulk clear or load, so a vessel
        //     restored with its identity later in the session still matches.
        private static readonly Dictionary<string, RescuePlacedMarker> rescuePlacedKerbals
            = new Dictionary<string, RescuePlacedMarker>(System.StringComparer.Ordinal);

        /// <summary>
        /// One rescue-placed marker: the vessel the rescue placed the kerbal on
        /// and the UT of that rescue (NaN when unknown).
        /// </summary>
        internal struct RescuePlacedMarker
        {
            internal ulong VesselPid;
            internal double MarkUT;

            internal RescuePlacedMarker(ulong vesselPid, double markUT)
            {
                VesselPid = vesselPid;
                MarkUT = markUT;
            }
        }

        /// <summary>
        /// Read-only access to the rescue-placed kerbal map (name -> marker).
        /// </summary>
        internal static IReadOnlyDictionary<string, RescuePlacedMarker> RescuePlacedKerbals => rescuePlacedKerbals;

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
            RescuePlacedMarker marker;
            if (!rescuePlacedKerbals.TryGetValue(kerbalName, out marker))
            {
                vesselPersistentId = 0UL;
                return false;
            }
            vesselPersistentId = marker.VesselPid;
            return true;
        }

        /// <summary>
        /// Full marker accessor (vessel pid + rescue UT) for
        /// <paramref name="kerbalName"/>.
        /// </summary>
        internal static bool TryGetRescuePlacedMarker(string kerbalName, out RescuePlacedMarker marker)
        {
            if (string.IsNullOrEmpty(kerbalName))
            {
                marker = default(RescuePlacedMarker);
                return false;
            }
            return rescuePlacedKerbals.TryGetValue(kerbalName, out marker);
        }

        /// <summary>
        /// True when a reservation whose newest contributing row comes from a
        /// recording starting at <paramref name="latestRowStartUT"/> is a NEW
        /// reservation relative to a rescue made at <paramref name="markUT"/>:
        /// that recording started after the rescue, so it was flown after it.
        /// A start at exactly the rescue UT is not new: a recording that began in
        /// the rescue's own frame (a Re-Fly load spawning its tips at the rewind
        /// UT) is part of the state the rescue completed. Unknown on either side
        /// (NaN / infinity) answers false and keeps the marker. Pure.
        /// </summary>
        internal static bool IsReservationNewerThanRescue(double markUT, double latestRowStartUT)
        {
            if (double.IsNaN(markUT) || double.IsInfinity(markUT)) return false;
            if (double.IsNaN(latestRowStartUT) || double.IsInfinity(latestRowStartUT)) return false;
            return latestRowStartUT > markUT;
        }

        /// <summary>
        /// Retires <paramref name="kerbalName"/>'s marker when
        /// <see cref="IsReservationNewerThanRescue"/> says his reservation is a
        /// new one (RESCUE-PLACED-MARKER-NEVER-RETIRES). Returns true when it
        /// removed a marker. Called by the <see cref="KerbalsModule.ApplyToRoster"/>
        /// guard before it reads the marker.
        /// </summary>
        internal static bool TryRetireRescuePlacedForNewReservation(
            string kerbalName, double latestRowStartUT)
        {
            RescuePlacedMarker marker;
            if (!TryGetRescuePlacedMarker(kerbalName, out marker))
                return false;
            if (!IsReservationNewerThanRescue(marker.MarkUT, latestRowStartUT))
                return false;
            rescuePlacedKerbals.Remove(kerbalName);
            ParsekLog.Info("CrewReservation",
                $"Retired rescue-placed marker: '{kerbalName}' vesselPid={marker.VesselPid} " +
                $"markUT={marker.MarkUT.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                "newestRowStartUT=" +
                latestRowStartUT.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                " (a flight recorded after the rescue holds the kerbal; its stand-in is no longer suppressed)");
            return true;
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
            MarkRescuePlaced(kerbalName, vesselPersistentId, double.NaN);
        }

        /// <summary>
        /// <see cref="MarkRescuePlaced(string, ulong)"/> with the UT of the
        /// rescue, which <see cref="TryRetireRescuePlacedForNewReservation"/>
        /// compares with the kerbal's reservation rows. A NaN
        /// <paramref name="markUT"/> keeps the UT an existing marker already
        /// carries.
        /// </summary>
        internal static void MarkRescuePlaced(string kerbalName, ulong vesselPersistentId, double markUT)
        {
            if (string.IsNullOrEmpty(kerbalName)) return;
            RescuePlacedMarker existing;
            bool hadPrior = rescuePlacedKerbals.TryGetValue(kerbalName, out existing);
            double keptUT = double.IsNaN(markUT) && hadPrior ? existing.MarkUT : markUT;
            rescuePlacedKerbals[kerbalName] = new RescuePlacedMarker(vesselPersistentId, keptUT);
            string utText = keptUT.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            if (!hadPrior)
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Marked rescue-placed: '{kerbalName}' vesselPid={vesselPersistentId} markUT={utText} " +
                    "(#608/#609 rescue path; #615 guard signal — pid-scoped)");
            }
            else if (existing.VesselPid != vesselPersistentId)
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Re-marked rescue-placed: '{kerbalName}' vesselPid={vesselPersistentId} markUT={utText} " +
                    $"(superseding prior pid={existing.VesselPid}; #615 pid-scoped marker)");
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
        /// Besides <see cref="TryRetireRescuePlacedForNewReservation"/> (a flight
        /// recorded after the rescue holds the kerbal), the marker is cleared
        /// only by the bulk lifecycle paths listed above, on session / rewind /
        /// wipe-all boundaries. Within
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
            RescuePlacedMarker removed;
            if (rescuePlacedKerbals.TryGetValue(kerbalName, out removed)
                && rescuePlacedKerbals.Remove(kerbalName))
            {
                ParsekLog.Verbose("CrewReservation",
                    $"Cleared rescue-placed marker: '{kerbalName}' vesselPid={removed.VesselPid} (bulk lifecycle)");
            }
        }
    }
}
