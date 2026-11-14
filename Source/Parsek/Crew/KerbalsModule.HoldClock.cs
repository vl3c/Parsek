using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The hold clock: loss and respawn-pending holds, reservation release, the walk clock and its live / loaded-save UT reads, and the reserved-at / reserved-now queries.
    internal partial class KerbalsModule : IResourceModule
    {
        /// <summary>
        /// True when the hold reads as a LOSS at <paramref name="nowUT"/>: a permanent death,
        /// or a respawn-on death whose stock respawn instant (<see cref="KerbalReservation.DeathRespawnUT"/>)
        /// is still ahead of the clock, whatever later flight the merged hold also carries.
        /// Before the respawn the kerbal is dead or missing in stock and no stand-in covers
        /// him; from the respawn on, a later flight's hold is an ordinary reservation. An
        /// unknown clock (NaN) fails closed (loss, no stand-in), like the hold rule. Pure.
        /// </summary>
        internal static bool IsLossHoldAt(KerbalReservation reservation, double nowUT)
        {
            if (reservation == null) return false;
            if (reservation.IsPermanent) return true;
            if (double.IsNaN(reservation.DeathRespawnUT)) return false;
            return double.IsNaN(nowUT) || nowUT < reservation.DeathRespawnUT;
        }

        /// <summary>
        /// The instant a non-permanent loss hold ends (the recorded death's stock respawn),
        /// or NaN for a permanent death or a hold that carries no respawn. The Lost date
        /// every surface shows. Pure.
        /// </summary>
        internal static double LossRespawnUT(KerbalReservation reservation)
        {
            if (reservation == null || reservation.IsPermanent) return double.NaN;
            return reservation.DeathRespawnUT;
        }

        /// <summary>
        /// True when the hold is a recorded death that stock respawns: not permanent, and the
        /// death's respawn instant is what ends it (no later flight extends it). The kerbal is
        /// lost until <see cref="KerbalReservation.ReservedUntilUT"/> and free after. Pure.
        /// </summary>
        internal static bool IsRespawnPendingHold(KerbalReservation reservation)
        {
            return reservation != null
                && !reservation.IsPermanent
                && !double.IsNaN(reservation.DeathRespawnUT)
                && reservation.DeathRespawnUT >= reservation.ReservedUntilUT;
        }

        /// <summary>
        /// Owner decision 2026-09-26: a hold that carries a respawn-on death is a finite
        /// respawn-pending hold only when nothing keeps the kerbal committed ACROSS the dead
        /// window. An open-ended co-row of the same kerbal (an un-closed Aboard / Unknown row)
        /// that STARTS before the respawn UT overlaps
        /// that window, so the death is a PERMANENT loss: no stand-in, reads Lost, never
        /// respawns - the same answer the data gives with respawn off. An open-ended co-row
        /// starting at or after the respawn UT is a post-respawn reuse (e.g. he flew again
        /// to a station and is still aboard): the respawn stays intact and the later flight
        /// is an ordinary hold. A finite co-row that outlasts the respawn keeps the S8
        /// ordinary hold too. A death in a chain with a looped segment follows the same rules
        /// as any death (operator ruling 2026-09-27: the loop is visual only). Runs on the
        /// merged reservation set (the earliest
        /// open-ended start is carried through the merge), so the result never depends on
        /// row order. Returns the number of holds converted.
        /// </summary>
        internal static int ResolveOpenEndedRespawnDeaths(
            Dictionary<string, KerbalReservation> holds)
        {
            if (holds == null) return 0;
            int converted = 0;
            int kept = 0;
            foreach (var kvp in holds)
            {
                KerbalReservation r = kvp.Value;
                if (r == null || r.IsPermanent || double.IsNaN(r.DeathRespawnUT))
                    continue;
                if (double.IsNaN(r.OpenEndedCoRowStartUT))
                    continue;
                if (r.OpenEndedCoRowStartUT >= r.DeathRespawnUT)
                {
                    kept++;
                    ParsekLog.Verbose(Tag,
                        $"Death hold: '{kvp.Key}' respawn kept: open-ended co-row starts after the respawn " +
                        $"(coRowStartUT={FormatClockUT(r.OpenEndedCoRowStartUT)} " +
                        $"respawnUT={FormatClockUT(r.DeathRespawnUT)}); ordinary hold for the later flight");
                    continue;
                }
                ParsekLog.Verbose(Tag,
                    $"Death hold: '{kvp.Key}' respawn suppressed: open-ended co-row " +
                    $"(coRowStartUT={FormatClockUT(r.OpenEndedCoRowStartUT)} " +
                    $"respawnUT={FormatClockUT(r.DeathRespawnUT)}); permanent loss");
                r.IsPermanent = true;
                r.ReservedUntilUT = double.PositiveInfinity;
                r.DeathRespawnUT = double.NaN;
                converted++;
            }
            if (converted > 0 || kept > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Open-ended co-rows over respawn deaths: permanent={converted.ToString(CultureInfo.InvariantCulture)} " +
                    $"respawnKept={kept.ToString(CultureInfo.InvariantCulture)}");
            }
            return converted;
        }

        /// <summary>
        /// The respawn delay a Timeline death entry may name: the recording's stamped timer,
        /// but only when the stamp says respawn on AND the walk's resolved hold for that
        /// kerbal still carries a respawn (not permanent: no overlapping open-ended co-row). NaN otherwise, including when no resolved hold is available,
        /// so the Timeline never promises a respawn the reservation walk does not grant. Pure.
        /// </summary>
        internal static double ResolveTimelineRespawnSeconds(
            Recording rec, string kerbalName,
            IReadOnlyDictionary<string, KerbalReservation> resolvedHolds)
        {
            if (rec == null || rec.CrewDeathRespawns != true) return double.NaN;
            if (resolvedHolds == null || string.IsNullOrEmpty(kerbalName)) return double.NaN;
            KerbalReservation r;
            if (!resolvedHolds.TryGetValue(kerbalName, out r) || r == null) return double.NaN;
            if (r.IsPermanent || double.IsNaN(r.DeathRespawnUT)) return double.NaN;
            return rec.CrewDeathRespawnSeconds;
        }

        /// <summary>
        /// True when the hold reads as a LOSS (the kerbal is dead on the committed timeline):
        /// a permanent death, or a death still waiting for its stock respawn. Every "Lost"
        /// surface (Kerbals window, stock-screen marks, crew dialog) routes here. Pure.
        /// </summary>
        internal static bool IsLossHold(KerbalReservation reservation)
        {
            return reservation != null
                && (reservation.IsPermanent
                    || IsRespawnPendingHold(reservation)
                    || reservation.LossAtWalkClock);
        }

        /// <summary>
        /// The later of two recording start UTs, ignoring a NaN / infinite one (NaN
        /// when neither is finite). Pure.
        /// </summary>
        internal static double LaterRowStartUT(double current, double candidate)
        {
            bool candidateFinite = !double.IsNaN(candidate) && !double.IsInfinity(candidate);
            if (!candidateFinite) return current;
            if (double.IsNaN(current) || double.IsInfinity(current)) return candidate;
            return candidate > current ? candidate : current;
        }

        /// <summary>
        /// THE reservation predicate: does this reservation hold its kerbal at game time
        /// <paramref name="nowUT"/>?
        ///
        /// <para>Design 9.2 / 9.3: a reservation is one continuous block from UT 0 to its
        /// end. A permanent one (Dead with stock respawn off at the death) never ends; an
        /// open-ended one (Aboard / Unknown) carries +inf and so never ends by time alone;
        /// a Recovered one ends at
        /// the flight's recovery UT; a Dead one with stock respawn on at the death ends at
        /// the death UT + the stamped respawn timer.</para>
        ///
        /// <para>BOUNDARY: the kerbal is free AT exactly <c>ReservedUntilUT</c>
        /// (<c>nowUT &lt; ReservedUntilUT</c> holds, equality releases). The end UT of a
        /// Recovered flight is the recovery instant itself, and the common live case is a
        /// commit run at that very clock right after the recovery; that walk must already
        /// see the kerbal home.</para>
        ///
        /// <para>An unreadable clock (NaN) holds: releasing needs a clock that has
        /// verifiably passed the end.</para>
        /// </summary>
        internal static bool IsReservationActiveAt(KerbalReservation reservation, double nowUT)
        {
            if (reservation == null) return false;
            if (reservation.IsPermanent) return true;
            if (double.IsNaN(nowUT)) return true;
            return nowUT < reservation.ReservedUntilUT;
        }

        /// <summary>
        /// Cheap clock-crossing decision behind the scene-level release checks: true when
        /// the live clock <paramref name="nowUT"/> has reached the earliest release the last
        /// walk left pending (<paramref name="nextReleaseUT"/>) and that release has not
        /// already triggered a recalculation (<paramref name="lastTriggeredReleaseUT"/>), so
        /// a walk that for any reason did not move the release cannot loop per frame.
        /// The caller passes NaN as <paramref name="lastTriggeredReleaseUT"/> once any walk
        /// other than the one it triggered has run (see
        /// <see cref="ResolveLastTriggeredReleaseUT"/>).
        /// </summary>
        internal static bool IsReservationReleaseDue(
            double nowUT, double nextReleaseUT, double lastTriggeredReleaseUT)
        {
            if (double.IsNaN(nowUT) || double.IsInfinity(nowUT) || nowUT <= 0.0)
                return false;
            if (double.IsNaN(nextReleaseUT) || double.IsInfinity(nextReleaseUT))
                return false;
            if (nowUT < nextReleaseUT)
                return false;
            return !(nextReleaseUT == lastTriggeredReleaseUT);
        }

        /// <summary>
        /// The "already triggered" value <see cref="IsReservationReleaseDue"/> should see:
        /// the release the check last acted on while no walk has run since the one it
        /// triggered, else NaN. A rewind's recalculation that puts the same release back
        /// (same end UT, re-reserved) must be acted on again when time passes it again.
        /// </summary>
        internal static double ResolveLastTriggeredReleaseUT(
            double lastTriggeredReleaseUT, int postWalkCountNow, int postWalkCountAfterTrigger)
        {
            return postWalkCountNow == postWalkCountAfterTrigger
                ? lastTriggeredReleaseUT
                : double.NaN;
        }

        /// <summary>
        /// The clock a walk judges reservations against. Pure; the live reads are the
        /// caller's (<see cref="ResolveWalkClockUT(double?)"/>). Resolution order:
        /// <list type="number">
        /// <item>While <c>ParsekScenario.OnLoad</c> is on the stack, the LOADED SAVE's
        /// <c>flightState.universalTime</c>: on a scene-change load Planetarium still
        /// reports the previous scene's clock (a later one after a quickload or a
        /// rewind), and a walk judged against it would release a kerbal the loaded
        /// timeline still holds. The walk's cutoff inside OnLoad is that same
        /// Planetarium value, so it is not trusted either.</item>
        /// <item>The walk's own cutoff: a rewind / time-jump / current-UT walk IS the
        /// new "now". A "no time filter" sentinel cutoff (the Re-Fly post-invoke walk
        /// passes <c>double.MaxValue</c> to walk the whole ledger authoritatively; see
        /// <see cref="IsNoTimeFilterCutoff"/>) is NOT a clock and falls through.</item>
        /// <item>While a rewind's UT adjustment is still pending, the adjusted rewind
        /// UT (Planetarium still reads the pre-rewind future until the deferred
        /// coroutine sets it). Read from <c>RecordingStore.RewindUTAdjustmentTargetUT</c>,
        /// captured when the adjustment is scheduled, because <c>RewindContext.EndRewind</c>
        /// zeroes <c>RewindAdjustedUT</c> in the same OnLoad.</item>
        /// <item>The live Planetarium clock.</item>
        /// </list>
        /// Any source that is not a finite positive UT resolves to NaN, which HOLDS
        /// every reservation (<see cref="IsReservationActiveAt"/>): a release needs a
        /// clock that has verifiably passed the end.
        /// </summary>
        internal static double ResolveWalkClockUT(
            double? walkNowUT,
            bool onLoadInProgress,
            double loadedSaveUT,
            bool rewindClockPending,
            double rewindAdjustedUT,
            double liveUT)
        {
            if (onLoadInProgress)
                return IsUsableClockUT(loadedSaveUT) ? loadedSaveUT : double.NaN;
            if (walkNowUT.HasValue
                && !double.IsNaN(walkNowUT.Value)
                && !double.IsInfinity(walkNowUT.Value)
                && !IsNoTimeFilterCutoff(walkNowUT.Value))
                return walkNowUT.Value;
            if (rewindClockPending)
                return IsUsableClockUT(rewindAdjustedUT) ? rewindAdjustedUT : double.NaN;
            return IsUsableClockUT(liveUT) ? liveUT : double.NaN;
        }

        internal static bool IsNoTimeFilterCutoff(double cutoffUT)
        {
            return cutoffUT >= NoTimeFilterCutoffThresholdUT;
        }

        private static bool IsUsableClockUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut > 0.0;
        }

        /// <summary>
        /// Live wrapper over <see cref="ResolveWalkClockUT(double?,bool,double,bool,double,double)"/>:
        /// reads only the sources the resolution order actually reaches.
        /// </summary>
        internal static double ResolveWalkClockUT(double? walkNowUT)
        {
            bool onLoad = ParsekScenario.IsOnLoadInProgress;
            if (onLoad)
                return ResolveWalkClockUT(walkNowUT, true, ReadLoadedSaveUT(),
                    false, double.NaN, double.NaN);
            if (walkNowUT.HasValue
                && !double.IsNaN(walkNowUT.Value)
                && !double.IsInfinity(walkNowUT.Value)
                && !IsNoTimeFilterCutoff(walkNowUT.Value))
                return ResolveWalkClockUT(walkNowUT, false, double.NaN,
                    false, double.NaN, double.NaN);
            bool rewindPending = RecordingStore.RewindUTAdjustmentPending;
            return ResolveWalkClockUT(walkNowUT, false, double.NaN,
                rewindPending, rewindPending ? RecordingStore.RewindUTAdjustmentTargetUT : double.NaN,
                rewindPending ? double.NaN : ReadLiveClockUT());
        }

        /// <summary>
        /// The live game clock, or NaN when it is not readable or not yet initialised
        /// (UT 0 on a cold load reads as not-ready, the same rule as
        /// <c>LedgerOrchestrator.IsCurrentUtReadyForCutoff</c>).
        /// </summary>
        internal static double ReadLiveClockUT()
        {
            double ut;
            if (LiveClockUTProviderForTesting != null)
            {
                ut = LiveClockUTProviderForTesting();
            }
            else
            {
                // The core is its own non-inlined method so a headless host (xUnit /
                // mono), where the Planetarium type cannot initialise, throws at the
                // call below and lands in this catch instead of escaping the caller.
                try { ut = ReadPlanetariumUTCore(); }
                catch (Exception) { ut = double.NaN; }
            }
            return IsUsableClockUT(ut) ? ut : double.NaN;
        }

        /// <summary>The loaded save's <c>flightState.universalTime</c>, or NaN.</summary>
        internal static double ReadLoadedSaveUT()
        {
            double ut;
            if (LoadedSaveUTProviderForTesting != null)
            {
                ut = LoadedSaveUTProviderForTesting();
            }
            else
            {
                try { ut = ReadFlightStateUTCore(); }
                catch (Exception) { ut = double.NaN; }
            }
            return IsUsableClockUT(ut) ? ut : double.NaN;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadPlanetariumUTCore()
        {
            if (Planetarium.fetch == null)
                return double.NaN;
            return Planetarium.GetUniversalTime();
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadFlightStateUTCore()
        {
            var flightState = HighLogic.CurrentGame?.flightState;
            return flightState != null ? flightState.universalTime : double.NaN;
        }

        /// <summary>
        /// Is <paramref name="kerbalName"/> reserved at game time <paramref name="nowUT"/>:
        /// the committed timeline derives a reservation for him AND it is in force at that
        /// instant (permanent, open-ended, or <c>nowUT &lt; ReservedUntilUT</c> - see
        /// <see cref="IsReservationActiveAt"/> for the boundary and the unknown-clock rule).
        /// </summary>
        internal bool IsReservedAt(string kerbalName, double nowUT)
        {
            if (string.IsNullOrEmpty(kerbalName)) return false;
            KerbalReservation reservation;
            return reservations.TryGetValue(kerbalName, out reservation)
                && IsHoldInForce(kerbalName, reservation, nowUT);
        }

        /// <summary>
        /// The instance form of <see cref="IsReservationActiveAt"/>: identical with a known
        /// clock; with an UNKNOWN clock it keeps the last authoritative decision for an
        /// unchanged hold (<see cref="ResolveHoldWithUnknownClock"/>).
        /// </summary>
        private bool IsHoldInForce(string kerbalName, KerbalReservation reservation, double nowUT)
        {
            if (reservation == null) return false;
            if (!double.IsNaN(nowUT) || reservation.IsPermanent)
                return IsReservationActiveAt(reservation, nowUT);
            KnownHold known;
            bool hasKnown = lastKnownHold.TryGetValue(kerbalName, out known);
            return ResolveHoldWithUnknownClock(
                reservation, hasKnown, known.Active, known.EndUT);
        }

        /// <summary>
        /// A walk with no readable clock (a scene transition's missing Planetarium, a
        /// not-yet-initialised load): the last authoritative decision stands when it was made
        /// for the SAME end UT; a hold that is new or whose end changed is held (a release
        /// needs a clock that has verifiably passed the end). Pure.
        /// </summary>
        internal static bool ResolveHoldWithUnknownClock(
            KerbalReservation reservation, bool hasKnown, bool knownActive, double knownEndUT)
        {
            if (reservation == null) return false;
            if (reservation.IsPermanent) return true;
            if (hasKnown && knownEndUT.Equals(reservation.ReservedUntilUT))
                return knownActive;
            return true;
        }

        /// <summary>Whether <paramref name="kerbalName"/> holds a reservation that is in
        /// force at this walk's clock (<see cref="IsReservedAt"/> at
        /// <see cref="WalkClockUT"/>, captured once per walk in <see cref="PrePass"/>).
        /// Every "is he reserved" decision - availability, the crew-dialog filter, the
        /// reservation kind, the chain's active occupant, retirement, the roster pass and
        /// the in-flight swap map - routes here, so no consumer can see a kerbal as both
        /// free and held.</summary>
        internal bool IsReservedNow(string kerbalName)
        {
            return IsReservedAt(kerbalName, walkClockUT);
        }
    }
}
