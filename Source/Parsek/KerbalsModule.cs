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
    internal partial class KerbalsModule : IResourceModule
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

        // -- Tourist passengers --

        /// <summary>
        /// The <see cref="GameAction.KerbalRole"/> a tourist's KerbalAssignment row carries
        /// (stock <c>KerbalRoster.touristTrait</c>). The row is the durable record that the
        /// seat was a tourist's: stock deletes the kerbal when his contract ends or his
        /// vessel is recovered, and a rewind to before the offer leaves no kerbal at all.
        /// </summary>
        internal const string TouristRole = "Tourist";

        /// <summary>
        /// The tourist hold policy, the ONE place it is decided: true holds a tourist that a
        /// committed future flight still carries for that flight (greyed in the crew dialog
        /// until the flight ends, so after a rewind he never rides two vessels at once);
        /// false never holds a tourist. Either way a tourist gets no slot, no stand-in and no
        /// replacement kerbal (<see cref="PostWalk"/>), and his rows stay tourist evidence
        /// for the spawn path (<see cref="IsTouristPassenger"/>).
        /// </summary>
        internal static readonly bool TouristsHeldByCommittedFlights = true;

        /// <summary>True when <paramref name="role"/> is the tourist role. Pure.</summary>
        internal static bool IsTouristRole(string role)
        {
            return string.Equals(role, TouristRole, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a KerbalAssignment row of <paramref name="role"/> holds its kerbal under
        /// the hold policy <paramref name="touristsHeld"/>: every crew role does, a tourist
        /// only when tourists are held. Pure.
        /// </summary>
        internal static bool RoleHoldsKerbal(string role, bool touristsHeld)
        {
            return touristsHeld || !IsTouristRole(role);
        }

        /// <summary><see cref="RoleHoldsKerbal(string, bool)"/> under the shipped policy.</summary>
        internal static bool RoleHoldsKerbal(string role)
        {
            return RoleHoldsKerbal(role, TouristsHeldByCommittedFlights);
        }

        /// <summary>
        /// Names on the walk's tourist KerbalAssignment rows, held or not. Rebuilt every walk
        /// (cleared in <see cref="Reset"/>).
        /// </summary>
        private readonly HashSet<string> touristPassengers =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when an effective KerbalAssignment row of the last walk names
        /// <paramref name="kerbalName"/> as a tourist.
        /// </summary>
        internal bool IsTouristPassenger(string kerbalName)
        {
            return !string.IsNullOrEmpty(kerbalName) && touristPassengers.Contains(kerbalName);
        }

        // ── Derived state (recomputed on every recalculation walk) ──
        private Dictionary<string, KerbalReservation> reservations
            = new Dictionary<string, KerbalReservation>();
        private HashSet<string> retiredKerbals = new HashSet<string>();

        /// <summary>
        /// Set of all crew names appearing in any active committed recording.
        /// Built during ProcessAction for O(1) lookups in ComputeRetiredSet
        /// and IsKerbalInAnyRecording. Includes loop recordings: the per-recording Loop
        /// toggle is visual only and a looped recording's first run is a real flight
        /// (design 12.7).
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
        // Diagnostic only (the PrePass summary's loopingChains count): a looped segment
        // does not change a chain's crew holds (design 12.7, operator ruling 2026-09-27).
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
        /// One closure of an open-ended hold: a <see cref="GameActionType.KerbalRecovered"/>
        /// row, or (<see cref="IsKscRetirement"/>) the synthetic in-memory closure a flight
        /// retired in the KSC exclusion zone adds at its EndUT (operator ruling 2026-09-23;
        /// <see cref="VesselSpawner.IsKscRetiredFinalFlight"/>), as if recovered, without a
        /// ledger row.
        /// </summary>
        internal struct RecoveryClosure
        {
            public string OwnerRecordingId;
            public double RecoveryUT;
            public bool IsKscRetirement;
        }

        /// <summary>
        /// Split handoffs: <see cref="SplitHandoffKey"/> (tree, branch point, kerbal) -> the
        /// earliest child recording of that branch point (looped or not) whose own KerbalAssignment
        /// row carries the kerbal. A recording that ENDED at that branch point handed the
        /// kerbal to the child, so its open-ended (Aboard / Unknown) row ends at its EndUT
        /// and the child's row decides the rest (a death after staging respawns or not by
        /// the child's own Dead row). Built in <see cref="PrePass"/>, cleared in
        /// <see cref="Reset"/>.
        /// </summary>
        private readonly Dictionary<string, SplitHandoff> splitHandoffs =
            new Dictionary<string, SplitHandoff>(StringComparer.Ordinal);

        /// <summary>
        /// Every Dock / Board / InventoryTransfer claim on a recorded vessel in the committed
        /// trees, and the kerbals each recording's own KerbalAssignment rows name. An
        /// open-ended hold whose vessel a later dock (or cargo adoption) claims, with the
        /// kerbal carried on by the claiming recording, ends at the claim
        /// (<see cref="ResolveDockClaimHandoffUT"/>). Built in
        /// <see cref="PrePass"/>, cleared in <see cref="Reset"/>.
        /// </summary>
        private readonly List<DockClaim> dockClaims = new List<DockClaim>();
        private readonly Dictionary<string, HashSet<string>> rowKerbalsByRecording =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Recording id -> the ids of the SPLIT branch points (Undock, EVA, JointBreak,
        /// Breakup) that recording is a parent of, from the committed trees. A recording that
        /// flies on past a split (breakup-continuous) is only ever stamped with its LAST one
        /// (<c>ChildBranchPointId</c>), so a kerbal a child carried away mid-flight is found
        /// through this map (<see cref="ResolveMidFlightSplitHandoffUT"/>). Built in
        /// <see cref="PrePass"/>, cleared in <see cref="Reset"/>.
        /// </summary>
        private readonly Dictionary<string, List<string>> splitBranchPointsByParent =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Recording id -> the start UT of the earliest chain segment whose split branch points
        /// <see cref="splitBranchPointsByParent"/> lists for it (an optimizer split leaves a
        /// split on the earlier segment while the later one inherits the kerbal's row), so the
        /// mid-flight handoff window opens at that segment's start. Absent when only the
        /// recording's own split branch points are listed.
        /// </summary>
        private readonly Dictionary<string, double> splitChainStartByRecording =
            new Dictionary<string, double>(StringComparer.Ordinal);

        internal struct SplitHandoff
        {
            public string ChildRecordingId;
            public double ChildStartUT;
        }

        /// <summary>
        /// A Dock / Board branch point (any tree) whose target is a recorded vessel, or an
        /// InventoryTransfer branch point adopting a foreign cargo partner: the claim's UT,
        /// the target's pid and launch guid, and the merged child (or member) recording that
        /// carries the claimed vessel on. Also a recording whose vessel JOINED its tree
        /// (<see cref="ForeignVesselRecording"/>). Built in <see cref="PrePass"/>.
        /// </summary>
        internal struct DockClaim
        {
            public string TreeId;
            public string BranchPointId;
            public double DockUT;
            public uint TargetPid;
            public string TargetGuid;
            public string MergedChildRecordingId;
            /// <summary>
            /// The claim is a recording whose vessel joined its tree rather than coming out of
            /// it (the tree's root, a parentless recording, or a post-switch Launch branch
            /// point's child): a later mission picking up a vessel an earlier one left, e.g.
            /// by a `]` switch that arms no stock-action intent. Claim UT = the recording's
            /// start, target = its own vessel. It claims holds of OTHER trees only: a tree's
            /// own vessel lines hand crew on through its split and chain rules.
            /// </summary>
            public bool ForeignVesselRecording;
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
            public double StartUT;
            public double EndUT;
            public string ParentBranchPointId;
            public string ChildBranchPointId;
            public uint VesselPid;
            public string VesselGuid;
            // The recording's stamped crew death respawn policy (Recording.CrewDeathRespawns
            // / CrewDeathRespawnSeconds); null = unstamped, a death is permanent.
            public bool? DeathRespawns;
            public double DeathRespawnSeconds;
        }

        /// <summary>
        /// Per-kerbal reservation. Derived — never persisted directly.
        /// </summary>
        internal class KerbalReservation
        {
            public string KerbalName;
            public double ReservedUntilUT;  // double.PositiveInfinity for permanent/open-ended
            // Dead with respawn off or unstamped, or a respawn-on death merged with an
            // open-ended co-row (owner decision 2026-09-26) - never freed
            public bool IsPermanent;
            /// <summary>
            /// The latest stock respawn instant among the recorded deaths behind this hold
            /// (death UT + the respawn timer stamped at the death), or NaN when no death
            /// with respawn on contributes. While it defines the hold's end
            /// (<see cref="IsRespawnPendingHold"/>) the kerbal reads as lost until then.
            /// </summary>
            public double DeathRespawnUT = double.NaN;
            /// <summary>
            /// The earliest start UT among this kerbal's OPEN-ENDED non-death rows (an
            /// un-closed Aboard / Unknown row), or NaN
            /// when none. <see cref="ResolveOpenEndedRespawnDeaths"/> compares it with
            /// <see cref="DeathRespawnUT"/>: a row starting before the respawn keeps the
            /// kerbal committed across the dead window (permanent loss); one starting at or
            /// after it is a post-respawn reuse (ordinary hold, respawn intact).
            /// </summary>
            public double OpenEndedCoRowStartUT = double.NaN;
            /// <summary>
            /// Derived by <see cref="PostWalk"/> for the walk's clock: the hold carries a
            /// respawn-on death whose respawn instant the clock has not reached, although a
            /// later flight extends the hold past it (<see cref="IsLossHoldAt"/>). The kerbal
            /// reads Lost until <see cref="DeathRespawnUT"/> and is an ordinary hold after.
            /// Recorded here so the clock-free readers (<see cref="IsLossHold"/>) agree with
            /// the stand-in decision; the reservation dict is rebuilt every walk.
            /// </summary>
            public bool LossAtWalkClock;
            /// <summary>
            /// A tourist row contributed to this hold: the kerbal is a passenger, so
            /// <see cref="PostWalk"/> creates no slot and demands no stand-in for him.
            /// </summary>
            public bool IsTouristPassenger;
            /// <summary>
            /// The latest recording StartUT among the rows behind this hold, or NaN when
            /// none is known. The #615 rescue guard compares it with the rescue's UT: a
            /// row from a recording that started after the rescue is a flight
            /// flown after it, so the rescue marker retires
            /// (<see cref="CrewReservationManager.TryRetireRescuePlacedForNewReservation"/>).
            /// </summary>
            public double LatestRowStartUT = double.NaN;
        }

        /// <summary>
        /// Per-slot replacement chain. Slot name = original kerbal.
        /// Persisted in KERBAL_SLOTS ConfigNode for name stability.
        /// </summary>
        internal class KerbalSlot
        {
            public string OwnerName;
            public string OwnerTrait;       // "Pilot" / "Engineer" / "Scientist"
            // Derived each walk: the owner is dead on the committed timeline (a permanent
            // death, or a death whose stock respawn is still pending at the walk's clock).
            // No stand-in covers him, matching stock (a dead or missing kerbal is not
            // replaced); a respawn clears it on the walk that releases the hold.
            public bool OwnerPermanentlyGone;
            public List<string> Chain = new List<string>(); // stand-in names, ordered by depth
        }

        /// <summary>
        /// Cutoffs at or above this are "walk everything" sentinels, never a game time
        /// (1e15 s is about 31 million Kerbin years). <c>RewindInvoker</c> passes
        /// <c>double.MaxValue</c> so the post-invoke recalc walks the whole ledger with the
        /// rewind-only patch side effects; judging reservations against it would release
        /// every Recovered hold for one walk (and delete their stand-ins) mid-rewind.
        /// </summary>
        internal const double NoTimeFilterCutoffThresholdUT = 1e15;

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
        /// recording with neither snapshot (both sidecars missing) and no EVA name is simply
        /// absent - the window then falls back to <c>CrewReservationManager.CrewReplacements</c>.
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
            splitHandoffs.Clear();
            splitBranchPointsByParent.Clear();
            splitChainStartByRecording.Clear();
            dockClaims.Clear();
            rowKerbalsByRecording.Clear();
            touristPassengers.Clear();
        }

        /// <summary>
        /// Walks PART/crew= values in a vessel snapshot ConfigNode and rewrites any
        /// stand-in names back to their original kerbal names. Returns the number
        /// of crew entries that were rewritten.
        ///
        /// Called from <see cref="FlightRecorder.TryBackupSnapshot"/> so every
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
        /// <summary>
        /// VESSEL child node that keeps who actually sat in a seat the reverse map renamed:
        /// one <c>seat = owner|standIn</c> value per renamed seat. The crew rows read it to
        /// file a stand-in's death under the stand-in (STAND-IN-DEATH-FILED-UNDER-THE-SLOT-OWNER);
        /// KSP ignores an unknown child node when it loads the snapshot.
        /// </summary>
        internal const string SeatedStandInNodeName = "PARSEK_SEATED_STANDINS";
        private const string SeatedStandInValueName = "seat";

        /// <summary>
        /// Grep-stable key for the crew-end-state invalidation line. Emitted whenever a
        /// terminal stamp retires end states that were inferred against a different
        /// (usually absent) terminal verdict.
        /// </summary>
        internal const string CrewEndStateStaleAfterTerminalStampKey =
            "crew-end-states-stale-after-terminal-stamp";

        /// <summary>
        /// Test seam for the stock crew respawn difficulty settings. Null in production
        /// (reads <c>HighLogic.CurrentGame.Parameters.Difficulty</c>).
        /// </summary>
        internal static CrewRespawnPolicyReader CrewRespawnPolicyProviderForTesting;

        /// <summary>Test seam for <see cref="TryReadMissingRespawnUT"/>. Null in production
        /// (reads <c>HighLogic.CurrentGame.CrewRoster</c>).</summary>
        internal static MissingRespawnReader MissingRespawnProviderForTesting;

        /// <summary>Test seam for <see cref="IsReFlySessionActive"/>; null = read the scenario.</summary>
        internal static bool? ReFlySessionActiveOverrideForTesting;

        internal const int ActiveOwnerIndex = -1;
        internal const int NoActiveChainOccupant = -2;

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
            touristPassengers.Clear();
            ReFlySessionActiveOverrideForTesting = null;
            walkClockUT = double.NaN;
            nextReservationReleaseUT = double.PositiveInfinity;
            lastKnownHold.Clear();
            nextWalkProvisional = false;
        }
    }
}
