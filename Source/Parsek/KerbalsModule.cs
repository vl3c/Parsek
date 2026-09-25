using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal enum KerbalReservationKind
    {
        NotManaged,
        ReservedActive,
        ReservedRetired
    }

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

        private static bool terminalStampObserverInstalled;

        /// <summary>
        /// Installs <see cref="InvalidateCrewEndStatesForTerminalStamp"/> as
        /// <see cref="Recording.OnTerminalStamped"/> on first touch of this type. Belt to
        /// <see cref="EnsureTerminalStampObserverInstalled"/>'s braces: a static
        /// constructor alone would leave the observer uninstalled until something happens
        /// to mention KerbalsModule, and a save can load (and stamp) recordings first.
        /// </summary>
        static KerbalsModule()
        {
            EnsureTerminalStampObserverInstalled();
        }

        /// <summary>
        /// Idempotent installer for the terminal-stamp observer. Called from the Harmony
        /// startup addon so the hook exists before any save can load; safe to call again
        /// from anywhere that wants the guarantee locally.
        /// </summary>
        internal static void EnsureTerminalStampObserverInstalled()
        {
            if (terminalStampObserverInstalled && Recording.OnTerminalStamped != null) return;
            Recording.OnTerminalStamped = InvalidateCrewEndStatesForTerminalStamp;
            if (terminalStampObserverInstalled) return;
            terminalStampObserverInstalled = true;
            ParsekLog.Verbose("Kerbals", "terminal stamp observer installed");
        }

        // ── Derived state (recomputed on every recalculation walk) ──
        private Dictionary<string, KerbalReservation> reservations
            = new Dictionary<string, KerbalReservation>();
        private HashSet<string> retiredKerbals = new HashSet<string>();

        /// <summary>
        /// Set of all crew names appearing in any active committed recording.
        /// Built during ProcessAction for O(1) lookups in ComputeRetiredSet
        /// and IsKerbalInAnyRecording. Excludes loop recordings.
        /// </summary>
        private HashSet<string> allRecordingCrew = new HashSet<string>();
        private Dictionary<string, HashSet<string>> rawRecordingCrew
            = new Dictionary<string, HashSet<string>>();
        private HashSet<string> ledgerCreatedKerbals = new HashSet<string>();

        /// <summary>
        /// P9a: per-kerbal accumulator of the career-log entries the surviving ledger says
        /// were archived. Rebuilt from scratch each walk (cleared in <see cref="Reset"/>) and
        /// only ever set-UNIONED in <see cref="ProcessAction"/>, which is what makes the
        /// roster re-assert monotone BY CONSTRUCTION: it can add entries the roster is
        /// missing and has no operation that could remove one.
        /// </summary>
        private readonly Dictionary<string, KerbalCareerEntries> careerEntriesByKerbal
            = new Dictionary<string, KerbalCareerEntries>(StringComparer.Ordinal);

        private readonly HashSet<string> pendingTombstonedRosterKerbals =
            new HashSet<string>();

        // ── Recording metadata cache (built in PrePass) ──
        private Dictionary<string, RecordingMeta> recordingMeta
            = new Dictionary<string, RecordingMeta>();
        private HashSet<string> loopingChainIds = new HashSet<string>();

        /// <summary>
        /// Kerbal name -> the <see cref="GameActionType.KerbalRecovered"/> rows the walk's
        /// action list carries for him (owner recording + recovery UT). Built in
        /// <see cref="PrePass"/> because the KerbalAssignment rows a recovery closes sort
        /// BEFORE it (they are stamped at their flight's start). Cleared in
        /// <see cref="Reset"/>.
        /// </summary>
        private readonly Dictionary<string, List<RecoveryClosure>> recoveryClosures
            = new Dictionary<string, List<RecoveryClosure>>(StringComparer.Ordinal);

        /// <summary>
        /// Recording ids whose flight ended parked in the KSC exclusion zone and so retires
        /// (operator ruling 2026-09-23; <see cref="VesselSpawner.IsKscRetiredFinalFlight"/>).
        /// Their aboard crew are freed at the recording's EndUT through a synthetic
        /// in-memory closure in <see cref="recoveryClosures"/>, as if recovered, without a
        /// ledger row. Built in <see cref="PrePass"/>, cleared in <see cref="Reset"/>.
        /// </summary>
        private readonly HashSet<string> kscRetiredRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);

        internal struct RecoveryClosure
        {
            public string OwnerRecordingId;
            public double RecoveryUT;
        }

        /// <summary>
        /// Slack for "the held flight ended at or before the recovery": the recording's end
        /// (its last sample or scene-exit stamp) and the recovery (the stock event's clock
        /// read) are taken at different moments of the same scene change, so a pair that
        /// is one instant in game terms can differ by a frame either way.
        /// </summary>
        internal const double RecoveryClosureEndToleranceSeconds = 1.0;

        // ── Walk clock (captured once per walk in PrePass) ──

        /// <summary>
        /// The game time this walk judges reservations against, resolved ONCE at
        /// <see cref="PrePass"/> by <see cref="ResolveWalkClockUT(double?)"/> (the loaded
        /// save's time during a load, else the walk's cutoff, else the live clock). NaN
        /// means no trustworthy clock was readable (early load, unit tests without the
        /// seam) and every reservation then counts as ACTIVE - never a spurious release.
        /// See <see cref="IsReservationActiveAt"/>.
        /// </summary>
        private double walkClockUT = double.NaN;

        /// <summary>
        /// The earliest finite end among reservations still active at
        /// <see cref="walkClockUT"/>, or +inf when none will lapse by time alone. The cheap
        /// "has the clock crossed a release since the last walk" check
        /// (<see cref="IsReservationReleaseDue"/>) compares the live clock with this.
        /// </summary>
        private double nextReservationReleaseUT = double.PositiveInfinity;

        /// <summary>
        /// Per-kerbal hold decision (in force or released, and the end UT it was made
        /// for) as of the last AUTHORITATIVE walk that could read a clock. Survives
        /// <see cref="Reset"/> so the release / re-reserve lines print once per actual
        /// transition, and so a walk with NO readable clock keeps the last decision for an
        /// unchanged hold instead of re-holding everyone (which would make the roster pass
        /// recreate a returned owner's deleted stand-in, and the next clocked walk delete it
        /// again).
        /// </summary>
        private readonly Dictionary<string, KnownHold> lastKnownHold
            = new Dictionary<string, KnownHold>(StringComparer.Ordinal);

        private struct KnownHold
        {
            public bool Active;
            public double EndUT;
        }

        /// <summary>
        /// True while the current walk is PROVISIONAL: the engine's cutoff walk over the
        /// cutoff-filtered action list, which <c>CrewReservationManager.RecomputeAfterCutoffWalk</c>
        /// immediately replaces with the whole-ledger walk. A provisional walk sees only the
        /// flights launched before the cutoff, so it records no release / re-reserve
        /// transition (it would log "released" for an earlier flight's end, and the
        /// authoritative walk "re-reserved" for a later one, on every recalculation).
        /// Set by <see cref="MarkNextWalkProvisional"/>, consumed by <see cref="PostWalk"/>.
        /// </summary>
        private bool nextWalkProvisional;

        /// <summary>
        /// Marks the NEXT walk as provisional (see <see cref="nextWalkProvisional"/>). Called
        /// by <c>LedgerOrchestrator</c> right before a cutoff engine walk.
        /// </summary>
        internal void MarkNextWalkProvisional()
        {
            nextWalkProvisional = true;
        }

        /// <summary>
        /// Test seam for the live game clock the walk reads when it has no cutoff. Null in
        /// production (reads <c>Planetarium.GetUniversalTime</c>).
        /// </summary>
        internal static Func<double> LiveClockUTProviderForTesting;

        /// <summary>
        /// Test seam for the loaded save's clock (<c>flightState.universalTime</c>), read
        /// instead of Planetarium while <c>ParsekScenario.OnLoad</c> is on the stack.
        /// </summary>
        internal static Func<double> LoadedSaveUTProviderForTesting;

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
            public string TreeId;
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

        internal static string FormatPrePassSummary(
            int examined,
            int cached,
            int nullRecordings,
            int missingRecordingIds,
            int rawCrewRecordings,
            int rawCrewMembers,
            int loopingChains)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PrePass summary: examined={0} cached={1} nullRecordings={2} missingIds={3} rawCrewRecordings={4} rawCrewMembers={5} loopingChains={6}",
                examined,
                cached,
                nullRecordings,
                missingRecordingIds,
                rawCrewRecordings,
                rawCrewMembers,
                loopingChains);
        }

        internal static string FormatPostWalkSummary(
            int reservationCount,
            int permanentReservations,
            int temporaryReservations,
            int slotCount,
            int retiredCount,
            int slotsCreated,
            int releasedReservations = 0)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PostWalk summary: reservations={0} permanent={1} temporary={2} slots={3} retired={4} slotsCreated={5} released={6}",
                reservationCount,
                permanentReservations,
                temporaryReservations,
                slotCount,
                retiredCount,
                slotsCreated,
                releasedReservations);
        }

        /// <summary>
        /// THE reservation predicate: does this reservation hold its kerbal at game time
        /// <paramref name="nowUT"/>?
        ///
        /// <para>Design 9.2 / 9.3: a reservation is one continuous block from UT 0 to its
        /// end. A permanent one (Dead) never ends; an open-ended one (Aboard / Unknown, and
        /// any flight in a chain with a looping segment) carries +inf and so never ends by
        /// time alone; a Recovered one ends at the flight's recovery UT.</para>
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

        /// <summary>
        /// Cutoffs at or above this are "walk everything" sentinels, never a game time
        /// (1e15 s is about 31 million Kerbin years). <c>RewindInvoker</c> passes
        /// <c>double.MaxValue</c> so the post-invoke recalc walks the whole ledger with the
        /// rewind-only patch side effects; judging reservations against it would release
        /// every Recovered hold for one walk (and delete their stand-ins) mid-rewind.
        /// </summary>
        internal const double NoTimeFilterCutoffThresholdUT = 1e15;

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

        /// <summary>The clock the last walk judged reservations against (NaN = unknown).</summary>
        internal double WalkClockUT => walkClockUT;

        /// <summary>The earliest pending time-based release after the last walk, or +inf.</summary>
        internal double NextReservationReleaseUT => nextReservationReleaseUT;

        /// <summary>How many walks (<see cref="PostWalk"/>) this module has completed. The
        /// crossed-an-end check uses it to tell "the walk I triggered did not move the
        /// release" (stand down) from "a later walk put the same release back" (a rewind:
        /// act on it again).</summary>
        internal int PostWalkCount => postWalkCount;

        private int postWalkCount;

        // Read-only access for tests
        /// <summary>
        /// EVERY reservation the committed timeline derives, including a Recovered flight's
        /// reservation whose end the clock has already passed. Use
        /// <see cref="IsReservedNow"/> / <see cref="ActiveReservations"/> to ask whether a
        /// kerbal is held; this raw map is for naming the flight behind a hold and for
        /// diagnostics.
        /// </summary>
        internal IReadOnlyDictionary<string, KerbalReservation> Reservations => reservations;

        /// <summary>
        /// Snapshot of the reservations in force at this walk's clock - the subset of
        /// <see cref="Reservations"/> that <see cref="IsReservedNow"/> answers true for.
        /// </summary>
        internal IReadOnlyDictionary<string, KerbalReservation> ActiveReservations
        {
            get
            {
                var active = new Dictionary<string, KerbalReservation>(StringComparer.Ordinal);
                foreach (var kvp in reservations)
                {
                    if (IsHoldInForce(kvp.Key, kvp.Value, walkClockUT))
                        active[kvp.Key] = kvp.Value;
                }
                return active;
            }
        }
        internal IReadOnlyDictionary<string, KerbalSlot> Slots => slots;
        internal IReadOnlyCollection<string> RetiredKerbals => retiredKerbals;
        internal IReadOnlyCollection<string> LedgerCreatedKerbals => ledgerCreatedKerbals;

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

        /// <summary>
        /// Recording id -> the crew names the recorder actually wrote into that
        /// recording's snapshot, as PrePass cached them. A read-only view of the walk's
        /// own <c>rawRecordingCrew</c>, exposed for the Kerbals window's "as
        /// &lt;stand-in&gt;" column: <c>PopulateCrewEndStates</c> reverse-maps a stand-in's
        /// name back to the owner before writing <c>CrewEndStates</c>, so the owner-keyed
        /// end states alone cannot say WHO flew a given flight, and this is the per-flight
        /// record that can. Rebuilt every walk (cleared in <see cref="Reset"/>), so a
        /// recording whose snapshot the load-time sweep dropped is simply absent - the
        /// window then falls back to <c>CrewReservationManager.CrewReplacements</c>.
        /// <para>Snapshot copy, safe to enumerate while the module recalculates.</para>
        /// </summary>
        internal IReadOnlyDictionary<string, IReadOnlyCollection<string>>
            RawRecordingCrewByRecordingId
        {
            get
            {
                var copy = new Dictionary<string, IReadOnlyCollection<string>>(
                    rawRecordingCrew.Count, System.StringComparer.Ordinal);
                foreach (var kvp in rawRecordingCrew)
                    copy[kvp.Key] = new List<string>(kvp.Value);
                return copy;
            }
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
            rawRecordingCrew.Clear();
            ledgerCreatedKerbals.Clear();
            recordingMeta.Clear();
            loopingChainIds.Clear();
            careerEntriesByKerbal.Clear();
            recoveryClosures.Clear();
            kscRetiredRecordingIds.Clear();
        }

        /// <summary>
        /// Builds recording metadata cache from RecordingStore.CommittedRecordings.
        /// This is necessary because loop/disabled/chain status is mutable (users can
        /// toggle at runtime) and is not encoded in GameAction fields.
        /// </summary>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // walkNowUT: the walk's cutoff when it has one. The recording-metadata cache
            // below comes from RecordingStore, not from action UTs; the walk clock is
            // captured ONCE here so every "is he reserved now" answer of this walk (and
            // of every consumer until the next walk) judges against the same instant.
            walkClockUT = ResolveWalkClockUT(walkNowUT);

            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return false;

            int examined = 0;
            int nullRecordings = 0;
            int missingRecordingIds = 0;
            int rawCrewRecordings = 0;
            int rawCrewMembers = 0;
            var kscRetiredEndUTs = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                examined++;
                var rec = recordings[i];
                if (rec == null)
                {
                    nullRecordings++;
                    continue;
                }
                if (string.IsNullOrEmpty(rec.RecordingId))
                {
                    missingRecordingIds++;
                    continue;
                }

                bool isLoop = rec.LoopPlayback;
                bool isChain = rec.IsChainRecording;
                string chainId = rec.ChainId;

                recordingMeta[rec.RecordingId] = new RecordingMeta
                {
                    IsLoop = isLoop,
                    IsChainRecording = isChain,
                    ChainId = chainId,
                    TreeId = rec.TreeId,
                    EndUT = rec.EndUT
                };

                var rawCrew = ExtractRawCrewFromRecording(rec);
                if (rawCrew.Count > 0)
                {
                    rawRecordingCrew[rec.RecordingId] = new HashSet<string>(rawCrew);
                    rawCrewRecordings++;
                    rawCrewMembers += rawCrew.Count;
                }

                // Identify chains that contain a looping segment
                if (isLoop && isChain && !string.IsNullOrEmpty(chainId))
                    loopingChainIds.Add(chainId);

                // KSC retirement (operator ruling 2026-09-23): a final segment whose flight
                // ended parked in the KSC exclusion zone never becomes a real vessel, so its
                // aboard crew are freed at its EndUT as if recovered. A loop recording holds
                // no reservation, and a crewless one has nobody to free.
                if (!isLoop && rawCrew.Count > 0 && VesselSpawner.IsKscRetiredFinalFlight(rec))
                {
                    kscRetiredEndUTs[rec.RecordingId] = rec.EndUT;
                    kscRetiredRecordingIds.Add(rec.RecordingId);
                }
            }

            ParsekLog.Verbose(Tag,
                FormatPrePassSummary(
                    examined,
                    recordingMeta.Count,
                    nullRecordings,
                    missingRecordingIds,
                    rawCrewRecordings,
                    rawCrewMembers,
                    loopingChainIds.Count)
                + " walkClockUT=" + FormatClockUT(walkClockUT));

            int closureRows = CollectRecoveryClosures(actions, recoveryClosures);
            if (closureRows > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {closureRows.ToString(CultureInfo.InvariantCulture)} KerbalRecovered " +
                    $"row(s) for {recoveryClosures.Count.ToString(CultureInfo.InvariantCulture)} kerbal(s)");
            }

            if (kscRetiredEndUTs.Count > 0)
            {
                int retiredClosures = CollectKscRetirementClosures(
                    actions, kscRetiredEndUTs, recoveryClosures);
                ParsekLog.Verbose(Tag,
                    $"PrePass: {kscRetiredEndUTs.Count.ToString(CultureInfo.InvariantCulture)} KSC-retired " +
                    $"recording(s) free {retiredClosures.ToString(CultureInfo.InvariantCulture)} aboard crew " +
                    "hold(s) at their EndUT (no ledger row)");
            }

            // The kerbals module never mutates the action list, so no re-sort is needed.
            return false;
        }

        /// <summary>
        /// Processes kerbal ledger actions. KerbalAssignment builds crew
        /// reservations; roster-creating rows are tracked so a tombstone-triggered
        /// roster cleanup only removes kerbals whose surviving ELS no longer has a
        /// matching creation row.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            string rosterKerbalName;
            if (TryGetRosterCreatedKerbalName(action, out rosterKerbalName))
            {
                ledgerCreatedKerbals.Add(rosterKerbalName);
                ParsekLog.Verbose(Tag,
                    $"Roster-created kerbal retained by ledger: '{rosterKerbalName}' type={action.Type}");
                return;
            }

            if (action.Type == GameActionType.KerbalExperience)
            {
                AccumulateCareerEntries(action);
                return;
            }

            if (action.Type != GameActionType.KerbalAssignment)
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

            string name = action.KerbalName;
            if (string.IsNullOrEmpty(name)) return;

            // Build the all-crew set for O(1) lookup in ComputeRetiredSet
            allRecordingCrew.Add(name);
            HashSet<string> rawCrew;
            if (rawRecordingCrew.TryGetValue(recordingId, out rawCrew))
            {
                foreach (var rawName in rawCrew)
                    allRecordingCrew.Add(rawName);
            }

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

            // Design 9.3: an open-ended (Aboard / Unknown) hold ends when the kerbal is
            // recovered from a real vessel continuing this flight's tree
            // (KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL). A looping chain keeps
            // +inf for the same reason a Recovered end does: the ghost replays past it.
            if (!permanent && !chainHasLoop
                && (endState == KerbalEndState.Aboard || endState == KerbalEndState.Unknown))
            {
                string closingOwner;
                double closedAtUT = ResolveRecoveryClosureUT(name, recordingId, meta, out closingOwner);
                if (!double.IsPositiveInfinity(closedAtUT))
                {
                    endUT = closedAtUT;
                    string closedBy = closingOwner != null && kscRetiredRecordingIds.Contains(closingOwner)
                        ? "KSC retirement" : "recovery";
                    ParsekLog.Verbose(Tag,
                        $"Reservation bounded by {closedBy}: '{name}' recording '{recordingId}' " +
                        $"({endState}) endUT={closedAtUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"owner='{closingOwner}'");
                }
            }

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
        /// Collects the <see cref="GameActionType.KerbalRecovered"/> rows of a walk's action
        /// list into <paramref name="into"/> (kerbal -> closures). Rows without a kerbal
        /// name, owner recording or finite UT are ignored. Returns the rows collected. Pure.
        /// </summary>
        internal static int CollectRecoveryClosures(
            IReadOnlyList<GameAction> actions,
            Dictionary<string, List<RecoveryClosure>> into)
        {
            if (actions == null || into == null) return 0;
            int collected = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalRecovered) continue;
                if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId))
                    continue;
                if (double.IsNaN(a.UT) || double.IsInfinity(a.UT)) continue;

                List<RecoveryClosure> list;
                if (!into.TryGetValue(a.KerbalName, out list))
                {
                    list = new List<RecoveryClosure>();
                    into[a.KerbalName] = list;
                }
                list.Add(new RecoveryClosure { OwnerRecordingId = a.RecordingId, RecoveryUT = a.UT });
                collected++;
            }
            return collected;
        }

        /// <summary>
        /// Crew side of the KSC retirement ruling (operator, 2026-09-23): for every
        /// <see cref="GameActionType.KerbalAssignment"/> row of a retired recording
        /// (<paramref name="retiredEndUTByRecordingId"/>: recording id -> its EndUT) whose
        /// kerbal is still aboard at the end (Aboard, or Unknown), adds an in-memory closure
        /// owned by that recording at its EndUT. <see cref="RecoveryClosesHold"/> then ends
        /// the kerbal's open-ended hold from the retired recording itself and from the
        /// earlier segments of the same flight (same tree, ended by then) at EndUT, exactly
        /// as a <see cref="GameActionType.KerbalRecovered"/> row would, while a later flight
        /// keeps its own hold. Dead / Recovered rows and tourists are untouched, no ledger
        /// row is written, and one closure is added per (kerbal, recording). Returns the
        /// closures added. Pure.
        /// </summary>
        internal static int CollectKscRetirementClosures(
            IReadOnlyList<GameAction> actions,
            IReadOnlyDictionary<string, double> retiredEndUTByRecordingId,
            Dictionary<string, List<RecoveryClosure>> into)
        {
            if (actions == null || retiredEndUTByRecordingId == null || into == null)
                return 0;
            if (retiredEndUTByRecordingId.Count == 0)
                return 0;

            int added = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalAssignment) continue;
                if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId)) continue;
                if (string.Equals(a.KerbalRole, "Tourist", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.KerbalEndStateField != KerbalEndState.Aboard
                    && a.KerbalEndStateField != KerbalEndState.Unknown)
                    continue;

                double endUT;
                if (!retiredEndUTByRecordingId.TryGetValue(a.RecordingId, out endUT)) continue;
                if (double.IsNaN(endUT) || double.IsInfinity(endUT)) continue;
                if (!seen.Add(a.KerbalName + "|" + a.RecordingId)) continue;

                List<RecoveryClosure> list;
                if (!into.TryGetValue(a.KerbalName, out list))
                {
                    list = new List<RecoveryClosure>();
                    into[a.KerbalName] = list;
                }
                list.Add(new RecoveryClosure { OwnerRecordingId = a.RecordingId, RecoveryUT = endUT });
                added++;
            }
            return added;
        }

        /// <summary>
        /// Does a recovery (owner recording <paramref name="ownerRecordingId"/> in tree
        /// <paramref name="ownerTreeId"/>, at <paramref name="recoveryUT"/>) end the
        /// open-ended hold a flight (<paramref name="holdRecordingId"/>, tree
        /// <paramref name="holdTreeId"/>, ending <paramref name="holdEndUT"/>) put on the
        /// recovered kerbal?
        ///
        /// <para>Scope is the owner's own mission: the owner recording itself, or any
        /// recording of the same tree (the chain segments and optimizer splits of one
        /// launch, an EVA that re-boarded, a crew transfer inside the mission). A flight in
        /// ANOTHER tree is never closed - a kerbal the player stranded on another mission
        /// stays stranded until that mission's own vessel comes home, and a stand-in whose
        /// recovered name reverse-maps to that owner cannot free him. The held flight must
        /// also have ENDED by the recovery (within
        /// <see cref="RecoveryClosureEndToleranceSeconds"/>): a later flight keeps its own
        /// hold and the reservation keeps its max-end merge. Pure.</para>
        /// </summary>
        internal static bool RecoveryClosesHold(
            string holdRecordingId,
            string holdTreeId,
            double holdEndUT,
            string ownerRecordingId,
            string ownerTreeId,
            double recoveryUT)
        {
            if (string.IsNullOrEmpty(holdRecordingId) || string.IsNullOrEmpty(ownerRecordingId))
                return false;
            if (double.IsNaN(recoveryUT) || double.IsInfinity(recoveryUT)) return false;
            if (double.IsNaN(holdEndUT) || holdEndUT > recoveryUT + RecoveryClosureEndToleranceSeconds)
                return false;
            if (string.Equals(holdRecordingId, ownerRecordingId, StringComparison.Ordinal))
                return true;
            return !string.IsNullOrEmpty(ownerTreeId)
                && string.Equals(holdTreeId, ownerTreeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// The earliest recovery (this walk's <see cref="GameActionType.KerbalRecovered"/>
        /// rows) that closes <paramref name="kerbalName"/>'s open-ended hold from
        /// <paramref name="recordingId"/>, or +inf when none does. An owner recording that
        /// is no longer committed closes nothing.
        /// </summary>
        private double ResolveRecoveryClosureUT(
            string kerbalName, string recordingId, RecordingMeta holdMeta, out string closingOwner)
        {
            closingOwner = null;
            List<RecoveryClosure> closures;
            if (!recoveryClosures.TryGetValue(kerbalName, out closures))
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < closures.Count; i++)
            {
                var c = closures[i];
                RecordingMeta ownerMeta;
                if (!recordingMeta.TryGetValue(c.OwnerRecordingId, out ownerMeta))
                    continue;
                if (!RecoveryClosesHold(recordingId, holdMeta.TreeId, holdMeta.EndUT,
                        c.OwnerRecordingId, ownerMeta.TreeId, c.RecoveryUT))
                    continue;
                if (c.RecoveryUT < best)
                {
                    best = c.RecoveryUT;
                    closingOwner = c.OwnerRecordingId;
                }
            }
            return best;
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
            int permanentReservations = 0;
            int temporaryReservations = 0;
            int releasedReservations = 0;
            int slotsCreated = 0;
            foreach (var kvp in reservations)
            {
                if (kvp.Value.IsPermanent)
                {
                    permanentReservations++;
                    // Permanent: slot exits chain system. Mark owner as gone.
                    KerbalSlot permanentSlot;
                    if (slots.TryGetValue(kvp.Key, out permanentSlot))
                        permanentSlot.OwnerPermanentlyGone = true;
                    continue;
                }
                temporaryReservations++;

                // Released (design 9.3 / 9.4): the clock has reached the Recovered
                // flight's end, so the owner holds his own seat again. No slot is created
                // for him and no chain depth is demanded; an EXISTING slot keeps its chain
                // names (a rewind before the end reuses them), and ApplyToRoster's
                // displacement pass deletes the unused stand-in / retires a used one.
                if (!IsHoldInForce(kvp.Key, kvp.Value, walkClockUT))
                {
                    releasedReservations++;
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
                    slotsCreated++;
                    ParsekLog.Verbose(Tag,
                        $"Created slot for '{kvp.Key}' (trait={slot.OwnerTrait})");
                }

                // Walk chain: ensure each reserved level has a stand-in
                EnsureChainDepth(slot);
            }

            // 2. Identify retired stand-ins
            ComputeRetiredSet();

            // 3. Time-based release bookkeeping: the one-per-transition release /
            // re-reserve lines, and the earliest pending release the scene-level
            // crossed-an-end checks compare the live clock with.
            bool provisional = nextWalkProvisional;
            nextWalkProvisional = false;
            if (!provisional)
                RecordReservationTransitions();
            nextReservationReleaseUT = ComputeNextReleaseUT(ActiveReservations.Values, walkClockUT);
            unchecked { postWalkCount++; }

            // 4. Log summary
            ParsekLog.Info(Tag,
                FormatPostWalkSummary(
                    reservations.Count,
                    permanentReservations,
                    temporaryReservations,
                    slots.Count,
                    retiredKerbals.Count,
                    slotsCreated,
                    releasedReservations)
                + (provisional ? " provisional=True" : "")
                + " walkClockUT=" + FormatClockUT(walkClockUT)
                + " nextReleaseUT=" + FormatClockUT(nextReservationReleaseUT));
        }

        /// <summary>
        /// The earliest finite end among the reservations still in force at
        /// <paramref name="nowUT"/> (all of the given ones when the clock is unknown; the
        /// walk passes its in-force view), or +inf when none will lapse by time alone. Pure.
        /// </summary>
        internal static double ComputeNextReleaseUT(
            IEnumerable<KerbalReservation> reservationSet, double nowUT)
        {
            double next = double.PositiveInfinity;
            if (reservationSet == null) return next;
            foreach (var reservation in reservationSet)
            {
                if (reservation == null || reservation.IsPermanent) continue;
                double end = reservation.ReservedUntilUT;
                if (double.IsNaN(end) || double.IsInfinity(end)) continue;
                if (!IsReservationActiveAt(reservation, nowUT)) continue;
                if (end < next) next = end;
            }
            return next;
        }

        /// <summary>
        /// Emits the release / re-reserve line once per ACTUAL transition of a kerbal's
        /// time-based hold, comparing with the state the previous clock-readable walk
        /// left. A walk whose clock is unknown changes nothing (it holds everyone, which
        /// is not a statement about the timeline). A name absent from this walk (a
        /// cutoff walk whose filtered list has not reached his flight yet) keeps its last
        /// state.
        /// </summary>
        private void RecordReservationTransitions()
        {
            if (double.IsNaN(walkClockUT)) return;
            foreach (var kvp in reservations)
            {
                var reservation = kvp.Value;
                if (reservation == null) continue;
                bool active = IsReservationActiveAt(reservation, walkClockUT);
                KnownHold last;
                bool known = lastKnownHold.TryGetValue(kvp.Key, out last);
                string transition = DescribeReservationTransition(known, last.Active, active);
                if (transition != null)
                {
                    ParsekLog.Info(Tag,
                        $"Reservation {transition}: '{kvp.Key}' " +
                        $"endUT={FormatClockUT(reservation.ReservedUntilUT)} " +
                        $"nowUT={FormatClockUT(walkClockUT)} " +
                        (active
                            ? "(held again at this clock: a rewind before the end, or a later row extended the hold)"
                            : "(the clock has reached the Recovered flight's end - the kerbal is free again)"));
                }
                lastKnownHold[kvp.Key] = new KnownHold
                {
                    Active = active,
                    EndUT = reservation.ReservedUntilUT
                };
            }
        }

        /// <summary>
        /// Pure transition naming for <see cref="RecordReservationTransitions"/>:
        /// <c>released</c> when a hold that was in force (or never seen) is no longer,
        /// <c>re-reserved</c> when a released hold is in force again, else null.
        /// </summary>
        internal static string DescribeReservationTransition(bool known, bool wasActive, bool active)
        {
            if (!active && (!known || wasActive)) return "released";
            if (active && known && !wasActive) return "re-reserved";
            return null;
        }

        private static string FormatClockUT(double ut)
        {
            if (double.IsNaN(ut)) return "unknown";
            if (double.IsPositiveInfinity(ut)) return "INDEFINITE";
            return ut.ToString("F1", CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Walks PART/crew= values in a vessel snapshot ConfigNode and rewrites any
        /// stand-in names back to their original kerbal names. Returns the number
        /// of crew entries that were rewritten.
        ///
        /// Called from <see cref="VesselSpawner.TryBackupSnapshot"/> so every
        /// captured snapshot persists the original kerbals — not whichever stand-in
        /// happens to be seated on the live vessel at capture time. Without this
        /// step, a snapshot captured after <see cref="CrewReservationManager.SwapReservedCrewInFlight"/>
        /// or <see cref="CrewReservationManager.PlaceOrphanedReplacements"/> seats a
        /// stand-in would bake that stand-in name into the snapshot. At spawn time
        /// <see cref="VesselSpawner.EnsureCrewExistInRoster"/> would then fabricate
        /// a brand-new kerbal with that name (random gender, traits, stats) instead
        /// of restoring the original recorded crew.
        ///
        /// Pure-ish (only reads <see cref="LedgerOrchestrator.Kerbals"/> via the
        /// slot-chain fallback) — safe to call when the slot map is empty; the
        /// in-flight <paramref name="replacements"/> dict still covers same-session
        /// swaps even before any commit walk runs.
        /// </summary>
        internal static int ReverseMapCrewNamesInSnapshot(
            ConfigNode snapshot,
            IReadOnlyDictionary<string, string> replacements,
            string contextForLog)
        {
            if (snapshot == null)
                return 0;

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

                    string original = null;
                    if (replacements != null)
                    {
                        foreach (var kvp in replacements)
                        {
                            if (string.Equals(kvp.Value, name, System.StringComparison.Ordinal))
                            {
                                original = kvp.Key;
                                break;
                            }
                        }
                    }

                    if (original == null)
                        original = TryReverseMapCrewNameFromSlots(name);

                    if (original != null && !string.Equals(original, name, System.StringComparison.Ordinal))
                    {
                        rewrittenList.Add(original);
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

            if (rewritten > 0)
            {
                // VerboseRateLimited (not Info): TryBackupSnapshot fires often (the
                // recorder's periodic snapshot refresh drives it ~1100+ times across a
                // long time-warped recording), and any vessel carrying a seated stand-in
                // rewrites a crew name on every one. Routine, not a notable event, so it
                // must not log unconditionally. The zero-rewrite case stays silent.
                ParsekLog.VerboseRateLimited(Tag, "reverse-map-crew",
                    () => $"ReverseMapCrewNamesInSnapshot: rewrote {rewritten} stand-in crew name(s) " +
                    $"back to originals in snapshot ({contextForLog ?? "no-context"})");
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
        /// Grep-stable key for the crew-end-state invalidation line. Emitted whenever a
        /// terminal stamp retires end states that were inferred against a different
        /// (usually absent) terminal verdict.
        /// </summary>
        internal const string CrewEndStateStaleAfterTerminalStampKey =
            "crew-end-states-stale-after-terminal-stamp";

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
        /// escape, and the retraction sites (post-spawn revert/rewind clears) would otherwise
        /// wipe end states derived from a real flight. Any later stamp runs this seam again.
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
        /// Check if a kerbal is available for a new recording: no reservation in force at
        /// this walk's clock (<see cref="IsReservedNow"/>). A Recovered flight's hold
        /// whose end the clock has reached no longer counts.
        /// </summary>
        internal bool IsKerbalAvailable(string kerbalName)
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
                || retiredKerbals.Contains(kerbalName);
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
                        ParsekLog.Verbose("ReFlySession",
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
        /// Apply derived kerbal state to the KSP roster. Creates stand-ins,
        /// removes unused displaced stand-ins, and populates the crewReplacements
        /// dict for SwapReservedCrewInFlight.
        ///
        /// Reserved kerbals are left at their natural rosterStatus (typically
        /// Available). The VAB/SPH crew assignment dialog lists them greyed and refuses
        /// every seat placement (StockUiCrewDialogDecoration). KerbalDismissalPatch
        /// prevents dismissal.
        ///
        /// MIA Respawn: If KSP respawns a Dead kerbal to Available, the crew
        /// dialog still refuses them (they remain in the reservations dict).
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
                            // cleared ONLY by the bulk lifecycle paths
                            // (LoadCrewReplacements, RestoreReplacements,
                            // ClearReplacements, ResetReplacementsForTesting)
                            // at session / rewind / wipe-all boundaries.
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
                    || guardDeclinedMarkerStalePid > 0)
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
                        $"declinedMarkerStalePid={guardDeclinedMarkerStalePid}");
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

        /// <summary>Test seam for <see cref="IsReFlySessionActive"/>; null = read the scenario.</summary>
        internal static bool? ReFlySessionActiveOverrideForTesting;

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

        internal const int ActiveOwnerIndex = -1;
        internal const int NoActiveChainOccupant = -2;

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

        internal KerbalSlotLoadSummary LoadSlots(ConfigNode parentNode)
        {
            slots.Clear();
            var summary = new KerbalSlotLoadSummary();

            // Try new format first
            ConfigNode slotsNode = parentNode.GetNode("KERBAL_SLOTS");
            if (slotsNode != null)
            {
                summary.HasData = true;
                ConfigNode[] slotNodes = slotsNode.GetNodes("SLOT");
                int chainEntryCount = 0;
                int ignoredEntries = 0;
                for (int i = 0; i < slotNodes.Length; i++)
                {
                    string ownerName = slotNodes[i].GetValue("owner") ?? "";
                    if (string.IsNullOrEmpty(ownerName))
                    {
                        ignoredEntries++;
                        continue;
                    }

                    var slot = new KerbalSlot
                    {
                        OwnerName = ownerName,
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
                        else
                        {
                            ignoredEntries++;
                        }
                    }
                    if (slots.ContainsKey(slot.OwnerName))
                        ignoredEntries++;
                    slots[slot.OwnerName] = slot;
                }
                summary.SlotsLoaded = slots.Count;
                summary.ChainEntriesLoaded = chainEntryCount;
                summary.IgnoredEntries = ignoredEntries;
                ParsekLog.Info(Tag, $"Loaded {slots.Count} kerbal slot(s) with {chainEntryCount} chain entries from KERBAL_SLOTS");
                return summary;
            }

            // Backward compat: migrate from CREW_REPLACEMENTS
            ConfigNode replacementsNode = parentNode.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode != null)
            {
                summary.HasData = true;
                summary.LoadedFromLegacyCrewReplacements = true;
                ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
                int ignoredEntries = 0;
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
                        else
                        {
                            ignoredEntries++;
                        }
                    }
                    else
                    {
                        ignoredEntries++;
                    }
                }
                summary.SlotsLoaded = slots.Count;
                summary.ChainEntriesLoaded = slots.Count;
                summary.IgnoredEntries = ignoredEntries;
                ParsekLog.Info(Tag,
                    $"Migrated {slots.Count} slot(s) from legacy CREW_REPLACEMENTS");
                return summary;
            }

            ParsekLog.Verbose(Tag, "LoadSlots: no KERBAL_SLOTS or CREW_REPLACEMENTS found");
            return summary;
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
            rawRecordingCrew.Clear();
            ledgerCreatedKerbals.Clear();
            pendingTombstonedRosterKerbals.Clear();
            recordingMeta.Clear();
            loopingChainIds.Clear();
            careerEntriesByKerbal.Clear();
            ReFlySessionActiveOverrideForTesting = null;
            walkClockUT = double.NaN;
            nextReservationReleaseUT = double.PositiveInfinity;
            lastKnownHold.Clear();
            nextWalkProvisional = false;
        }
    }
}
