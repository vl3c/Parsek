using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static partial class GhostPlaybackLogic
    {
        // --- Span-clock primitives (lifted for Mission-level looping; wired in a later phase) ---

        /// <summary>
        /// One loop unit: the recordings a looping Mission replays together as a single unit. The
        /// MEMBERS (<see cref="MemberIndices"/>) are the Mission's included through-line legs ONLY;
        /// ride-along debris is NOT a member - it follows its parent member's span clock via the
        /// engine's debris seam (ShouldSourceDebrisFromUnitSpan). Built by
        /// <see cref="MissionLoopUnitBuilder"/> from the Mission's selection, NOT auto-detected. The
        /// whole span [SpanStartUT, SpanEndUT] loops on ONE shared mission clock at
        /// <see cref="CadenceSeconds"/>. Members render concurrently when the shared clock is inside
        /// their own window, debris alongside their parent, exactly like a rewind. Indices are
        /// committed-recording-list indices (the alignment invariant).
        /// </summary>
        internal readonly struct LoopUnit
        {
            /// <summary>A member's trimmed render window [StartUT, EndUT] (interval-level start/end trim).</summary>
            internal readonly struct MemberWindow
            {
                internal MemberWindow(double startUT, double endUT) { StartUT = startUT; EndUT = endUT; }
                internal double StartUT { get; }
                internal double EndUT { get; }
            }

            // committed index -> trimmed render window. Null / absent = no trim (the member renders
            // its full [rec.StartUT, rec.EndUT], so untrimmed missions behave exactly as before).
            private readonly IReadOnlyDictionary<int, MemberWindow> memberWindows;

            internal LoopUnit(
                int ownerIndex,
                int[] memberIndices,
                double spanStartUT,
                double spanEndUT,
                double cadenceSeconds,
                double phaseAnchorUT)
                : this(ownerIndex, memberIndices, spanStartUT, spanEndUT,
                       cadenceSeconds, phaseAnchorUT, cadenceSeconds, null)
            {
            }

            internal LoopUnit(
                int ownerIndex,
                int[] memberIndices,
                double spanStartUT,
                double spanEndUT,
                double cadenceSeconds,
                double phaseAnchorUT,
                double overlapCadenceSeconds)
                : this(ownerIndex, memberIndices, spanStartUT, spanEndUT,
                       cadenceSeconds, phaseAnchorUT, overlapCadenceSeconds, null)
            {
            }

            internal LoopUnit(
                int ownerIndex,
                int[] memberIndices,
                double spanStartUT,
                double spanEndUT,
                double cadenceSeconds,
                double phaseAnchorUT,
                double overlapCadenceSeconds,
                IReadOnlyDictionary<int, MemberWindow> memberWindows)
                : this(ownerIndex, memberIndices, spanStartUT, spanEndUT, cadenceSeconds,
                       phaseAnchorUT, overlapCadenceSeconds, memberWindows, null)
            {
            }

            internal LoopUnit(
                int ownerIndex,
                int[] memberIndices,
                double spanStartUT,
                double spanEndUT,
                double cadenceSeconds,
                double phaseAnchorUT,
                double overlapCadenceSeconds,
                IReadOnlyDictionary<int, MemberWindow> memberWindows,
                MissionRelaunchSchedule relaunchSchedule)
                : this(ownerIndex, memberIndices, spanStartUT, spanEndUT, cadenceSeconds,
                       phaseAnchorUT, overlapCadenceSeconds, memberWindows, relaunchSchedule,
                       null, null)
            {
            }

            internal LoopUnit(
                int ownerIndex,
                int[] memberIndices,
                double spanStartUT,
                double spanEndUT,
                double cadenceSeconds,
                double phaseAnchorUT,
                double overlapCadenceSeconds,
                IReadOnlyDictionary<int, MemberWindow> memberWindows,
                MissionRelaunchSchedule relaunchSchedule,
                Parsek.Reaim.ReaimMissionPlan? reaimPlan,
                Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule? reaimSchedule,
                IReadOnlyList<LoopCut> loiterCuts = null,
                double arrivalHoldSeconds = 0.0,
                double arrivalHoldAtUT = double.NaN,
                double arrivalAlignPeriodSeconds = double.NaN,
                string arrivalAmberReason = null,
                double launchBodyRotationPeriodSeconds = double.NaN,
                bool launchHoldEngaged = false,
                double recordedSoiExitUT = double.NaN,
                int[] descentMemberIndices = null,
                double recordedDeorbitUT = double.NaN,
                double descentEndUT = double.NaN,
                double destinationBodyRotationPeriodSeconds = double.NaN,
                double loiterPeriodSeconds = double.NaN,
                double captureShiftSeconds = double.NaN,
                double parkingConicEndUT = double.NaN,
                int transferMemberIndex = -1,
                double firstDeorbitLegStartUT = double.NaN)
            {
                OwnerIndex = ownerIndex;
                MemberIndices = memberIndices ?? System.Array.Empty<int>();
                SpanStartUT = spanStartUT;
                SpanEndUT = spanEndUT;
                CadenceSeconds = cadenceSeconds;
                PhaseAnchorUT = phaseAnchorUT;
                OverlapCadenceSeconds = overlapCadenceSeconds;
                this.memberWindows = memberWindows;
                RelaunchSchedule = relaunchSchedule;
                ReaimPlan = reaimPlan;
                ReaimSchedule = reaimSchedule;
                LoiterCuts = loiterCuts;
                ArrivalHoldSeconds = arrivalHoldSeconds;
                ArrivalHoldAtUT = arrivalHoldAtUT;
                ArrivalAlignPeriodSeconds = arrivalAlignPeriodSeconds;
                ArrivalAmberReason = arrivalAmberReason;
                LaunchBodyRotationPeriodSeconds = launchBodyRotationPeriodSeconds;
                LaunchHoldEngaged = launchHoldEngaged;
                RecordedSoiExitUT = recordedSoiExitUT;
                DescentMemberIndices = descentMemberIndices;
                RecordedDeorbitUT = recordedDeorbitUT;
                DescentEndUT = descentEndUT;
                DestinationBodyRotationPeriodSeconds = destinationBodyRotationPeriodSeconds;
                LoiterPeriodSeconds = loiterPeriodSeconds;
                CaptureShiftSeconds = captureShiftSeconds;
                ParkingConicEndUT = parkingConicEndUT;
                TransferMemberIndex = transferMemberIndex;
                FirstDeorbitLegStartUT = firstDeorbitLegStartUT;
            }

            /// <summary>
            /// This member's trimmed render START (interval-level start-trim), or <paramref name="fallback"/>
            /// (the recording's own StartUT) when the member is not trimmed. Drives the member's render
            /// window in every scene driver, so dropping a vessel's leading interval starts it later.
            /// </summary>
            internal double MemberStartUT(int committedIndex, double fallback)
                => memberWindows != null && memberWindows.TryGetValue(committedIndex, out MemberWindow w)
                    ? w.StartUT : fallback;

            /// <summary>This member's trimmed render END, or <paramref name="fallback"/> (the recording's EndUT) when untrimmed.</summary>
            internal double MemberEndUT(int committedIndex, double fallback)
                => memberWindows != null && memberWindows.TryGetValue(committedIndex, out MemberWindow w)
                    ? w.EndUT : fallback;

            /// <summary>Earliest member's committed-recording index (owns the span clock).</summary>
            internal int OwnerIndex { get; }

            /// <summary>Committed-recording indices of all members (mission legs + ride-along debris), StartUT order.</summary>
            internal int[] MemberIndices { get; }

            /// <summary>Min member StartUT.</summary>
            internal double SpanStartUT { get; }

            /// <summary>Max member EndUT (a ride-along debris tail can extend it).</summary>
            internal double SpanEndUT { get; }

            /// <summary>
            /// Span-clock cadence: the Mission's loop period raised to at least the span so a SINGLE
            /// span instance never truncates (Auto = span). Consumed by the single-instance scenes
            /// (KSC, Tracking Station) and the flight engine's no-overlap branch via
            /// <see cref="TryComputeSpanLoopUT"/>. NOT the true launch cadence: when the user period
            /// is shorter than the span the mission overlaps itself instead (see
            /// <see cref="OverlapCadenceSeconds"/>); this field stays at the span so the
            /// single-instance scenes keep showing the whole mission.
            /// </summary>
            internal double CadenceSeconds { get; }

            /// <summary>The UT the loop phase is measured from (elapsed = currentUT - PhaseAnchorUT). Equals <see cref="SpanStartUT"/> when no explicit anchor was supplied, which preserves the old absolute-phase behavior.</summary>
            internal double PhaseAnchorUT { get; }

            /// <summary>
            /// True launch-to-launch cadence for MISSION self-overlap: the Mission's loop period
            /// (Auto = the global auto-loop interval, NOT the span), cap-clamped so
            /// <c>ceil(span / cadence)</c> stays within
            /// <see cref="GhostPlayback.MaxOverlapMissionInstances"/>. When this is SHORTER than the
            /// span the flight engine relaunches the whole mission every
            /// <see cref="OverlapCadenceSeconds"/> seconds, so several staggered instances of the
            /// mission play concurrently (exactly like a single recording with period &lt; duration).
            /// When it is &gt;= the span there is no self-overlap and the engine falls back to the
            /// single span-clock instance. Defaults to <see cref="CadenceSeconds"/> for the
            /// non-overlapping constructor so legacy callers keep the single-instance behavior.
            /// </summary>
            internal double OverlapCadenceSeconds { get; }

            /// <summary>
            /// The zero-drift per-window relaunch schedule for a phase-locked, drifting
            /// (multi-constraint incommensurate) Mission, or NULL for every other config (the common
            /// case). When non-null the span clock relaunches the mission at the schedule's
            /// non-uniform UTs instead of the uniform <see cref="PhaseAnchorUT"/> +
            /// n*<see cref="CadenceSeconds"/>, so each relaunch stays celestially within tolerance
            /// instead of drifting (docs/dev/plans/zero-drift-reschedule.md). INVARIANT: a unit with a
            /// non-null schedule is non-overlapping (<see cref="UnitMemberOverlaps"/> false), so the
            /// overlap engine path never sees one. Null keeps the existing uniform-cadence behavior
            /// byte-identical.
            /// </summary>
            internal MissionRelaunchSchedule RelaunchSchedule { get; }

            /// <summary>
            /// The re-aim mission plan (launch / target / common-ancestor + the recorded parking +
            /// arrival legs) for a cross-parent single-hop interplanetary loop, or NULL for every other
            /// config (same-parent, no heliocentric leg, multi-hop, non-interplanetary). When set
            /// (alongside <see cref="ReaimSchedule"/>) the flight engine substitutes a per-window
            /// re-aimed transfer trajectory for this member so the ghost relaunches every synodic
            /// window instead of replaying the faithful (rarely-recurring) recorded geometry
            /// (docs/dev/plans/reaim-interplanetary-transfers.md). Null keeps the faithful path.
            /// </summary>
            internal Parsek.Reaim.ReaimMissionPlan? ReaimPlan { get; }

            /// <summary>The synodic relaunch schedule (first window, synodic period, Hohmann tof,
            /// phase anchor, cadence) for a re-aim loop, or NULL when not re-aim. Paired with
            /// <see cref="ReaimPlan"/>.</summary>
            internal Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule? ReaimSchedule { get; }

            /// <summary>True when this unit is a fully-resolved re-aim interplanetary loop (a supported
            /// plan + a valid synodic schedule).</summary>
            internal bool IsReaim =>
                ReaimPlan.HasValue && ReaimPlan.Value.Supported
                && ReaimSchedule.HasValue && ReaimSchedule.Value.Valid;

            /// <summary>
            /// Whole-period loiter cuts (docs/dev/plans/reaim-loiter-compression.md) compressing this
            /// re-aim loop's repeated parking orbits to ~1 revolution, or NULL when none. The span clock
            /// (<see cref="TryComputeSpanLoopUT"/>) wraps over the COMPRESSED span and remaps the loopUT to
            /// skip these recorded intervals, so the loop relaunches close to the transfer window instead
            /// of replaying a year-long loiter. Members render at recorded UTs (the cut interval is just
            /// never sampled), so this is the only place the compression lives. Null on every non-re-aim
            /// unit and on a re-aim loop with no compressible loiter.
            /// </summary>
            internal IReadOnlyList<LoopCut> LoiterCuts { get; }

            /// <summary>
            /// Arrival HOLD (seconds) inserted at the heliocentric->capture boundary so the in-SOI replay
            /// defers and the destination's rotation phase at the deorbit recurs to recorded (re-aim
            /// cross-parent landing alignment). The INVERSE of a loiter cut. 0 on every non-re-aim unit and
            /// on a re-aim landing with alignment off / no rotation constraint, in which case the span clock
            /// is byte-identical to the pre-hold behavior.
            /// </summary>
            internal double ArrivalHoldSeconds { get; }

            /// <summary>The recorded-span UT the arrival hold is inserted at (the heliocentric->capture
            /// boundary, = the re-aim plan's RecordedArrivalUT). NaN when there is no hold.</summary>
            internal double ArrivalHoldAtUT { get; }

            /// <summary>
            /// The destination-side alignment period (seconds) used by the arrival hold, carried so the loop
            /// clock can re-align the hold per replayed loop: the destination body's rotation period T_rot for
            /// a landing, or the destination STATION's orbital period T_station for an orbit-rendezvous
            /// (M4c Tier 2). The reference hold (<see cref="ArrivalHoldSeconds"/>, W_0) aligns ONE loop; the
            /// synodic launch cadence is not a whole number of alignment periods, so the destination-side phase
            /// drifts a fraction of a turn each loop. The loop clock subtracts the per-loop drift
            /// (cycleIndex * (cadence mod T_align)) mod-wrapped into [0, T_align) so every loop re-aligns. NaN
            /// (the "no period" sentinel, matching the <see cref="ArrivalHoldAtUT"/> NaN convention) on every
            /// non-re-aim unit and on a re-aim arrival with no hold, in which case the per-loop adjustment is
            /// skipped and the span clock stays byte-identical to the constant-hold behavior.
            /// </summary>
            internal double ArrivalAlignPeriodSeconds { get; }

            /// <summary>
            /// The arrival-alignment AMBER reason for a re-aim unit whose destination-side alignment failed
            /// closed with a station involved (design D8: landing rotation + station, station + constrained
            /// moon, a moon-orbiting station, or a station-bearing Jool-class destination - no single hold
            /// aligns two destination-side periods). Display-only: the Missions window tints the T- cell and
            /// shows this as the tooltip; the transfer still re-aims, the arrival replays faithful (hold 0).
            /// Null on every other unit. Transient like the unit itself (never persisted).
            /// </summary>
            internal string ArrivalAmberReason { get; }

            /// <summary>
            /// The launch body's sidereal rotation period T_sid (seconds) used by the PER-LOOP LAUNCH
            /// ALIGNMENT (the borrow-at-launch / repay-at-SOI-exit shift that closes the launch-&gt;escape
            /// render seam for the span&gt;=synodic re-aim regime PadAlignLaunch declines). Carried alongside
            /// <see cref="LaunchHoldEngaged"/> so the span clock can compute delta_N per replayed loop
            /// (<see cref="ComputePerLoopLaunchAdvanceSeconds"/>): the loop launches at <c>L_N - delta_N</c> so
            /// the launch body rotates to the recorded inertial orientation during the in-SOI replay and the
            /// verbatim launch ascent coincides with the frozen escape conic; delta_N is repaid as a coast hold
            /// at <see cref="RecordedSoiExitUT"/>. NaN (the "no period" sentinel, matching
            /// <see cref="ArrivalAlignPeriodSeconds"/>) on every non-re-aim unit and whenever the launch
            /// alignment is not engaged, in which case <see cref="TryComputeSpanLoopUT"/> is byte-identical to
            /// the pre-alignment behavior. Runtime-only; the LoopUnit set is rebuilt per scene, not persisted,
            /// so no OnSave/OnLoad handling is required.
            /// </summary>
            internal double LaunchBodyRotationPeriodSeconds { get; }

            /// <summary>
            /// True only when the PER-LOOP LAUNCH ALIGNMENT engages for this unit: re-aim engaged AND
            /// PadAlignLaunch did NOT apply (cadence != synodic, the regime PadAlignLaunch bails on) AND the
            /// unit has a body-fixed launch leg with a valid SOI-exit boundary to align. When false the launch
            /// advance is 0 and the span clock stays byte-identical (no double-correction with PadAlignLaunch,
            /// no no-op shift for an already-in-orbit / chained-continuation member). Runtime-only (never persisted).
            /// </summary>
            internal bool LaunchHoldEngaged { get; }

            /// <summary>
            /// The recorded-span UT of the launch-body SOI EXIT (the launch-body-&gt;heliocentric boundary,
            /// = the re-aim plan's RecordedSoiExitUT). The borrow-at-launch / repay-at-SOI-exit launch
            /// alignment inserts the delta_N repay coast hold at this recorded boundary so the SOI-exit-and-onward
            /// timeline returns to the baseline L_N schedule (targeting + the destination arrival hold UNCHANGED).
            /// NaN (the "no boundary" sentinel) on every non-re-aim unit and whenever the launch alignment is not
            /// engaged, in which case <see cref="TryComputeSpanLoopUT"/> inserts no SOI-exit hold. Runtime-only.
            /// </summary>
            internal double RecordedSoiExitUT { get; }

            // === Descent trigger (re-aim looped LANDING) =====================================
            // docs/dev/plans/reaim-descent-trigger.md. For a re-aimed looped landing the parking CONIC is
            // shifted ~|captureShift| earlier (PR #1177) to meet the early-arriving transfer, opening a gap
            // before the body-fixed deorbit->reentry->landing clip. These fields let the tracking-station /
            // map resolver detach the descent member's head from the loop clock and re-anchor it to the first
            // rotation-aligned moment after the icon reaches the parking-orbit deorbit point (the icon circles
            // the conic in between, then plays the recorded clip verbatim onto the EXACT recorded site).
            // All sentinels (-1 / NaN) on every non-landing / non-re-aim unit, so the resolver is byte-identical
            // to today there. Runtime-only (the LoopUnit set is rebuilt per scene, never persisted).

            /// <summary>The committed-recording indices of the member SET carrying the post-parking body-fixed
            /// approach clip (deorbit-&gt;reentry-&gt;landing for a surface mission, OR the rendezvous/docking
            /// approach for an orbital one), or null/empty when the descent trigger is not engaged. A
            /// multi-recording chain continues PAST the destination arrival as several separate committed
            /// recordings (each a member); they all share ONE re-anchored descent clip and ONE trigger, and
            /// each renders only the slice of the clip in its own window. Ascending committed-index order.</summary>
            internal int[] DescentMemberIndices { get; }

            /// <summary>True if committed index <paramref name="i"/> is one of the descent-set members.</summary>
            internal bool IsDescentMember(int i)
            {
                if (DescentMemberIndices == null) return false;
                for (int k = 0; k < DescentMemberIndices.Length; k++)
                    if (DescentMemberIndices[k] == i) return true;
                return false;
            }

            /// <summary>The recorded UT of the descent member's deorbit (the first target-body surface section
            /// start after arrival) - where the re-anchored descent clip begins. NaN when not engaged.</summary>
            internal double RecordedDeorbitUT { get; }

            /// <summary>The recorded UT the descent clip ends (touchdown); the re-anchored head hides the member
            /// once it passes this. NaN when not engaged.</summary>
            internal double DescentEndUT { get; }

            /// <summary>The destination body's sidereal rotation period T_rot (seconds): the live rotation
            /// equals the recorded deorbit rotation iff currentUT ≡ recordedDeorbitUT (mod T_rot), so the clip
            /// lands on the exact recorded site. NaN when not engaged.</summary>
            internal double DestinationBodyRotationPeriodSeconds { get; }

            /// <summary>The destination parking orbit period T_park (seconds): the period the icon circles while
            /// waiting for the descent alignment. NaN when not engaged.</summary>
            internal double LoiterPeriodSeconds { get; }

            /// <summary>The capture shift (seconds, &lt;= 0): how far PR #1177 moved the parking conic earlier
            /// (newArrival - recordedArrival ≈ HohmannTof - recordedTof). The conic's deorbit point sits at
            /// recorded UT recordedDeorbitUT + captureShift. NaN when not engaged.</summary>
            internal double CaptureShiftSeconds { get; }

            /// <summary>
            /// SHIFTED UT of the transfer member's PARKING-conic end (= the destination loiter run end + the
            /// capture shift = the deorbit point = the start of the first deorbit-transition segment, in the
            /// RE-AIMED display frame). The map-presence segment-lookup clamp boundary (the destination-loiter
            /// "parking conic stops rendering" bug), distinct from <see cref="RecordedDeorbitUT"/> (the LAST
            /// target-body segment end / descent re-anchor, in the recorded frame). The loiter run ends at the
            /// first &gt; 5% sma step (ReaimLoiterCompressor), so the recorded value is the orbit-raise/lower
            /// boundary between the parking conic and the deorbit arc, NOT a span-subtract or terminal-tail
            /// synthesis. The builder translates it into the SHIFTED frame (<c>descentRun.EndUT +
            /// CaptureShiftSeconds</c>) because the map-presence segment lookup runs against the re-aimed
            /// (captureShift-shifted) effective segments at the loop-shifted sample UT: the clamp compares this
            /// value against that SHIFTED effUT, so it must be in the SAME frame as <c>conicEnd =
            /// RecordedDeorbitUT + CaptureShiftSeconds</c> and the effective-segment-lookup effUT. It lands ONE
            /// segment earlier than <c>conicEnd</c> (the SHIFTED parking-conic end, not the SHIFTED deorbit-arc
            /// end), so the deorbit arc no longer leaks as the loiter orbit. NaN when the descent trigger is not
            /// engaged.
            /// </summary>
            internal double ParkingConicEndUT { get; }

            /// <summary>
            /// The committed-recording index of the DESTINATION transfer member (the member whose own recorded
            /// segments classify as a Supported re-aim plan and own the shifted destination parking conic /
            /// descentRun / seamUT), or -1 when the descent trigger is not engaged. The loiter-gap clamp +
            /// line-hold gate EXACTLY on this index (<c>i == TransferMemberIndex</c>), so a non-descent ride-along
            /// member in a DIFFERENT/unshifted frame (e.g. a launch-body-orbit probe) is excluded - it is NOT the
            /// re-aim source and its loop clock never advances through the shifted destination parking conic. This
            /// is NOT <see cref="OwnerIndex"/> (the earliest-start member / tree root) and NOT "any non-descent
            /// member". Default -1 matches no real committed index, keeping the clamp byte-identical-off on every
            /// non-descent-trigger unit.
            /// </summary>
            internal int TransferMemberIndex { get; }

            /// <summary>
            /// The recorded UT (UNSHIFTED, same frame as <see cref="RecordedDeorbitUT"/>) of the FIRST
            /// deorbit-arc polyline leg the map renderer draws for the transfer member — i.e. the startUT of
            /// the first non-orbital descent leg <c>GhostTrajectoryPolylineRenderer.BuildLegsForRecording</c>
            /// emits for this member whose recorded window is the post-shifted-conic deorbit tail
            /// (leg.endUT in (seam + captureShift, seam + 1s]). The C1 icon-ride gate
            /// (<see cref="TryResolveTransferDeorbitIconHead"/>) engages only once the re-anchored deorbit head
            /// reaches this UT, so the parking conic keeps rendering until — and the proto retires exactly
            /// when — that leg first draws (zero loiter-orbit gap, no double-draw). Computed at build time by
            /// calling the SAME leg builder the Driver calls, so it equals the renderer's leg.startUT to the
            /// UT. NaN when the descent trigger is not engaged or no deorbit leg was found; the gate then
            /// falls back to the legacy triggerUT − LoiterPeriodSeconds heuristic (byte-identical-off).
            /// </summary>
            internal double FirstDeorbitLegStartUT { get; }

            /// <summary>True only when this unit carries a fully-resolved descent trigger (a re-aim looped
            /// arrival with a non-empty descent member set, an early arrival, and valid periods).</summary>
            internal bool HasDescentTrigger =>
                IsReaim && DescentMemberIndices != null && DescentMemberIndices.Length > 0
                && !double.IsNaN(RecordedDeorbitUT) && !double.IsNaN(DescentEndUT)
                && DestinationBodyRotationPeriodSeconds > 0.0 && LoiterPeriodSeconds > 0.0
                && !double.IsNaN(CaptureShiftSeconds);
        }

        /// <summary>
        /// The full span-clock frame for one render frame: the PRIMARY loopUT/cycle (the continuing
        /// instance N, region-A semantics, valid for the whole cadence cycle - the long-lived
        /// through-line the camera follows), plus an OPTIONAL boundary-overlap SECONDARY (the next
        /// instance N+1's early in-SOI launch, present only inside the borrow window when the boundary
        /// overlap engages). The secondary closes the launch-&gt;escape seam on zero-slack loops without
        /// inverting the primary or yanking the camera (docs/dev/plan-launch-boundary-overlap.md,
        /// Design B). <see cref="TryComputeSpanLoopUT"/> is the primary-only wrapper around
        /// <see cref="ComputeSpanLoopFrame"/>; every existing caller stays byte-identical.
        /// </summary>
        internal readonly struct SpanLoopFrame
        {
            internal SpanLoopFrame(
                bool resolved, double loopUT, long cycleIndex, bool isInInterCycleTail,
                bool hasSecondary, double secondaryLoopUT, long secondaryCycleIndex)
            {
                Resolved = resolved;
                LoopUT = loopUT;
                CycleIndex = cycleIndex;
                IsInInterCycleTail = isInInterCycleTail;
                HasSecondary = hasSecondary;
                SecondaryLoopUT = secondaryLoopUT;
                SecondaryCycleIndex = secondaryCycleIndex;
            }

            /// <summary>False before span start / degenerate span (mirrors the old <c>TryComputeSpanLoopUT</c> bool).</summary>
            internal bool Resolved { get; }

            /// <summary>Primary loopUT (continuing instance N, region-A semantics, valid the whole cycle).</summary>
            internal double LoopUT { get; }

            /// <summary>Primary cycle = N (region B no longer mutates this; the primary stays on instance N).</summary>
            internal long CycleIndex { get; }

            /// <summary>The inter-cycle tail flag (cadence &gt; span parked tail); always false for the loop feature.</summary>
            internal bool IsInInterCycleTail { get; }

            /// <summary>True when a concurrent boundary-overlap early-launch instance is live this frame.</summary>
            internal bool HasSecondary { get; }

            /// <summary>Instance N+1 early-launch loopUT (valid only when <see cref="HasSecondary"/>).</summary>
            internal double SecondaryLoopUT { get; }

            /// <summary>The secondary's cycle index = N+1 (valid only when <see cref="HasSecondary"/>).</summary>
            internal long SecondaryCycleIndex { get; }
        }

        /// <summary>
        /// Immutable snapshot of the Mission loop units: one unit per looping Mission (multiple
        /// Missions loop concurrently, at most one per tree, so member indices never overlap across
        /// units). Built by <see cref="MissionLoopUnitBuilder"/> and handed to flight, KSC, and the
        /// tracking station so all three consume an identical view. <see cref="Empty"/> means no
        /// Mission is looping, which keeps the entire feature dormant.
        /// </summary>
        internal sealed class LoopUnitSet
        {
            internal static readonly LoopUnitSet Empty = new LoopUnitSet(
                new Dictionary<int, LoopUnit>(), new Dictionary<int, int>());

            private readonly Dictionary<int, LoopUnit> unitsByOwner;
            private readonly Dictionary<int, int> ownerByIndex;

            internal LoopUnitSet(
                Dictionary<int, LoopUnit> unitsByOwner,
                Dictionary<int, int> ownerByIndex)
            {
                this.unitsByOwner = unitsByOwner ?? new Dictionary<int, LoopUnit>();
                this.ownerByIndex = ownerByIndex ?? new Dictionary<int, int>();
            }

            /// <summary>Owner index -> unit descriptor.</summary>
            internal IReadOnlyDictionary<int, LoopUnit> UnitsByOwner => unitsByOwner;

            /// <summary>Member index -> owning unit's owner index (absent = not a unit member).</summary>
            internal IReadOnlyDictionary<int, int> OwnerByIndex => ownerByIndex;

            /// <summary>Number of distinct units in this set.</summary>
            internal int Count => unitsByOwner.Count;

            /// <summary>True if the given committed index is a member of any unit.</summary>
            internal bool IsMember(int index)
            {
                return ownerByIndex.ContainsKey(index);
            }

            /// <summary>
            /// Resolves the unit that owns <paramref name="memberIndex"/>. Returns false when
            /// the index is not a unit member (the common case until two consecutive auto-loop
            /// members exist).
            /// </summary>
            internal bool TryGetUnitForMember(int memberIndex, out LoopUnit unit)
            {
                if (ownerByIndex.TryGetValue(memberIndex, out int ownerIndex)
                    && unitsByOwner.TryGetValue(ownerIndex, out unit))
                {
                    return true;
                }
                unit = default;
                return false;
            }
        }

        /// <summary>
        /// A short loop-role tag for the given committed member index, appended to a ghost-teardown reason
        /// so a generic "mission-loop-out-of-window" destroy line states WHICH loop role the torn-down ghost
        /// played. The only role that needs naming for diagnosis today is a DESCENT member of a re-aim looped
        /// arrival: when its head leaves the descent clip the descent ghost is destroyed and the loiter member
        /// carries the icon (the user-reported "icon moved back onto the loiter trajectory" symptom), so tagging
        /// the destroy ties the [GhostMap] teardown to the [ReaimDescent] DESCENT REVERTED line by member index.
        /// Returns the empty string for a non-member / non-descent member (so a non-descent destroy reason is
        /// byte-identical to before). Pure; no Unity.
        /// </summary>
        internal static string DescribeLoopMemberRoleForTeardown(int memberIndex, LoopUnitSet units)
        {
            if (units == null || !units.TryGetUnitForMember(memberIndex, out LoopUnit unit))
                return string.Empty;
            if (unit.HasDescentTrigger && unit.IsDescentMember(memberIndex))
                return " descent-member=" + memberIndex.ToString(CultureInfo.InvariantCulture);
            return string.Empty;
        }

        /// <summary>
        /// A whole-period loiter interval excised from a re-aim loop's recorded timeline
        /// (docs/dev/plans/reaim-loiter-compression.md). Recorded
        /// [<see cref="StartUT"/>, <see cref="StartUT"/> + <see cref="LengthSeconds"/>] is removed from
        /// the loop's ACTIVE duration; the span clock skips it. NEUTRAL value type (no re-aim / Recording
        /// reference) so the shared clock and the standalone-target engine stay decoupled from
        /// <c>Parsek.Reaim</c>; the re-aim detector (<c>ReaimLoiterCompressor.ComputeCuts</c>) produces it.
        /// </summary>
        internal struct LoopCut
        {
            public double StartUT;
            public double LengthSeconds;
            public double EndUT => StartUT + LengthSeconds;
        }

        /// <summary>Total excised duration of <paramref name="cuts"/> (0 for null/empty). Pure.</summary>
        internal static double TotalCutLength(IReadOnlyList<LoopCut> cuts)
        {
            if (cuts == null)
                return 0.0;
            double sum = 0.0;
            for (int i = 0; i < cuts.Count; i++)
                sum += cuts[i].LengthSeconds;
            return sum;
        }

        /// <summary>
        /// Recorded UT -> compressed UT: <c>t - sum of the parts of each cut at or before t</c>. Monotonic
        /// non-decreasing; a recorded UT INSIDE a cut collapses to the cut start (the cut interval maps to
        /// a single compressed instant). Identity for an empty cut list. Pure.
        /// </summary>
        internal static double CompressSpanUT(double t, IReadOnlyList<LoopCut> cuts)
        {
            if (cuts == null || cuts.Count == 0)
                return t;
            double removed = 0.0;
            for (int c = 0; c < cuts.Count; c++)
            {
                LoopCut cut = cuts[c];
                if (t <= cut.StartUT)
                    continue;
                double overlapEnd = t < cut.EndUT ? t : cut.EndUT;
                removed += overlapEnd - cut.StartUT;
            }
            return t - removed;
        }

        /// <summary>
        /// Compressed UT -> recorded UT: the inverse of <see cref="CompressSpanUT"/>. A compressed instant
        /// at a cut's collapse point maps to the cut END, so the loop resumes AFTER the excised interval
        /// (the cut is skipped, position-/velocity-continuous because it is a whole number of periods).
        /// <paramref name="cuts"/> must be sorted by StartUT ascending (ComputeCuts emits them in order).
        /// Identity for an empty cut list. Pure.
        /// </summary>
        internal static double DecompressSpanUT(double c, IReadOnlyList<LoopCut> cuts)
        {
            if (cuts == null || cuts.Count == 0)
                return c;
            double t = c;
            for (int i = 0; i < cuts.Count; i++)
            {
                if (cuts[i].StartUT <= t)
                    t += cuts[i].LengthSeconds;
            }
            return t;
        }

        // === Destination-SOI arrival HOLD (re-aim cross-parent landing alignment) =================
        // The INVERSE of a loiter cut. A loiter cut REMOVES recorded-span time (the loop plays faster,
        // skipping the excised parking). An arrival hold INSERTS dead time at the heliocentric->capture
        // boundary: the in-SOI replay starts LATER in live time so the destination's rotation phase at
        // the in-SOI entry recurs to its recorded value (the fix for the looped re-aimed landing's
        // ~131-degree rotation offset). It does not touch the launch pad or the transfer (both upstream
        // of the boundary), and a zero hold is the identity (byte-identical to today). See
        // docs/dev/design-mission-periodicity.md and docs/dev/plans/reaim-destination-arrival-alignment.md.
        // These two helpers are PURE and (this phase) UNWIRED; the loop clock wiring is the next phase.

        /// <summary>
        /// The minimal forward HOLD (seconds, in [0, T_rot)) that defers the in-SOI replay so the
        /// destination's rotation phase at the in-SOI entry matches its recorded value. The recorded entry
        /// sits at rotation phase <c>recordedArrivalUT mod T_rot</c>; the unshifted live entry sits at
        /// <c>entryLiveUT mod T_rot</c>. The hold that aligns them is
        /// <c>(recordedArrivalUT - entryLiveUT) mod T_rot</c>, normalized to [0, T_rot). Returns 0 for a
        /// degenerate rotation period (no rotation constraint =&gt; no hold) or a NaN input. Pure.
        /// </summary>
        internal static double ComputeArrivalAlignHoldSeconds(
            double recordedArrivalUT, double entryLiveUT, double rotationPeriod)
        {
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod) || rotationPeriod <= 0.0)
                return 0.0;
            if (double.IsNaN(recordedArrivalUT) || double.IsNaN(entryLiveUT))
                return 0.0;
            double m = (recordedArrivalUT - entryLiveUT) % rotationPeriod;
            if (m < 0.0)
                m += rotationPeriod;
            return m;
        }

        /// <summary>
        /// The PER-LOOP arrival hold W_N for replayed loop <paramref name="cycleIndex"/> (N). The reference
        /// hold <paramref name="w0"/> (W_0, from <see cref="ComputeArrivalAlignHoldSeconds"/>) aligns the
        /// destination rotation phase at the deorbit for ONE loop, but the synodic launch
        /// <paramref name="cadence"/> is not a whole number of destination rotations, so the unshifted live
        /// entry drifts <c>cadence mod T_rot</c> further around the spin each loop. Subtracting that per-loop
        /// drift re-aligns every loop:
        /// <c>W_N = ((W_0 - N * (cadence mod T_rot)) mod T_rot + T_rot) mod T_rot</c>. The double-mod-plus-T_rot
        /// form keeps W_N in [0, T_rot) for any sign of the inner term.
        ///
        /// GATED (the 13b regression fence): returns <paramref name="w0"/> unchanged when
        /// <paramref name="w0"/> &lt;= 0 (alignment Off / Drop, W_0 = 0), so the bare per-loop sawtooth never
        /// turns a zero hold nonzero and Off stays byte-identical. Also returns <paramref name="w0"/> unchanged
        /// for a degenerate <paramref name="rotationPeriod"/> (NaN / Infinity / &lt;= 0, the "no period"
        /// sentinel), so a re-aim unit with no destination rotation constraint keeps its constant hold. Pure;
        /// no Unity (xUnit-testable).
        /// </summary>
        internal static double ComputePerLoopArrivalHoldSeconds(
            double w0, long cycleIndex, double cadence, double rotationPeriod)
        {
            if (!(w0 > 0.0))
                return w0;
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod) || rotationPeriod <= 0.0)
                return w0;
            double inner = (w0 - cycleIndex * (cadence % rotationPeriod)) % rotationPeriod;
            return (inner + rotationPeriod) % rotationPeriod;
        }

        /// <summary>
        /// The PER-LOOP launch ADVANCE delta_N (seconds, in [0, T_sid)) to SUBTRACT from replayed loop
        /// <paramref name="cycleIndex"/> (N)'s nominal launch instant L_N so the launch body rotates to the
        /// SAME inertial orientation it had at the recorded launch DURING the in-SOI replay. Launching at
        /// <c>L_N - delta_N</c> (delta_N EARLIER than nominal) makes <c>currentUT - spanStartUT</c> a whole
        /// multiple of <c>T_sid</c> throughout the verbatim in-Kerbin-SOI replay, so the live launch-body
        /// rotation equals the recorded rotation and the body-fixed ascent / parking / escape coincide with
        /// the frozen inertial escape conic (the launch-&gt;escape render seam closes), launch on the real pad.
        /// The borrowed delta_N is REPAID as a coast hold at the Kerbin-SOI-exit boundary (see
        /// <see cref="TryComputeSpanLoopUT"/>), so the SOI-exit-and-onward timeline (heliocentric transfer,
        /// trans-target burn, destination arrival + its arrival hold) is back EXACTLY on the baseline L_N
        /// schedule (targeting + the arrival hold UNCHANGED).
        ///
        /// The alignment quantity is the LAUNCH DISPLACEMENT (NOT an absolute recorded-launch epoch): the
        /// loop's unshifted launch displacement from the recorded launch is
        /// <c>Off_N = (phaseAnchorUT - spanStartUT) + N * cadence</c> (the same quantity PadAlignLaunch
        /// snaps, <c>ReaimWindowPlanner.cs:211</c>, extended per loop;
        /// <c>phaseAnchorUT - spanStartUT == d0 - recordedDepartureUT</c> by <c>ReaimWindowPlanner.cs:124</c>).
        /// The in-SOI replay is rotation-aligned with the recorded launch iff <c>Off_N - delta_N</c> is a
        /// whole number of <c>T_sid</c>; the minimal BACKWARD advance that achieves this is
        /// <c>delta_N = (Off_N mod T_sid + T_sid) mod T_sid</c> (Off_N mod T_sid, the positive residual).
        /// The double-mod-plus-T_sid form keeps delta_N in [0, T_sid) for any sign and any N (a sawtooth in
        /// N, never growing with the loop index), exactly like <see cref="ComputePerLoopArrivalHoldSeconds"/>.
        ///
        /// <c>T_sid = Math.Abs(launchBodyRotationPeriod)</c> (the Math.Abs matches PadAlignLaunch's retrograde
        /// handling). Returns 0 for a degenerate <paramref name="launchBodyRotationPeriod"/> (NaN / Infinity /
        /// &lt;= 0, a non-rotating launch body: no pad realignment possible, no advance) or a NaN
        /// <paramref name="phaseAnchorUT"/> / <paramref name="spanStartUT"/> (matching the
        /// <see cref="ComputeArrivalAlignHoldSeconds"/> NaN guard). Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static double ComputePerLoopLaunchAdvanceSeconds(
            double phaseAnchorUT, double spanStartUT, long cycleIndex, double cadence,
            double launchBodyRotationPeriod)
        {
            double tSid = Math.Abs(launchBodyRotationPeriod);
            if (double.IsNaN(tSid) || double.IsInfinity(tSid) || tSid <= 0.0)
                return 0.0;
            if (double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT))
                return 0.0;
            double offN = (phaseAnchorUT - spanStartUT) + cycleIndex * cadence;
            double d = offN % tSid;
            return (d + tSid) % tSid;
        }

        /// <summary>
        /// The CAPPED per-loop launch advance for replayed loop <paramref name="win"/> (the instance launched
        /// in cycle <c>win-1</c>'s idle tail): <c>min(delta_win, slack_{win-1})</c>, where
        /// <c>delta_win = ComputePerLoopLaunchAdvanceSeconds(...)</c> and <c>slack_{win-1}</c> is the LAUNCHING
        /// cycle's inter-cycle idle gap (<c>cadence - compressedSpan - W_{win-1}</c>). This is the SINGLE source
        /// of truth for the borrow-at-launch advance: <see cref="TryComputeSpanLoopUT"/> uses it for BOTH the
        /// region-B early launch of instance N+1 (launching in cycle N, slack_N) AND the region-A render of
        /// instance N (launched in cycle N-1, slack_{N-1}), so the SAME instance is capped to the SAME advance
        /// in both regions (no cycle-boundary discontinuity), and
        /// <see cref="MissionsWindowUI.ComputeNextRelaunchUT"/> uses it so the navigable launch time agrees with
        /// the clock. The borrow is bounded by the LAUNCHING cycle's slack so the early launch never overlaps
        /// the previous instance's still-live replay.
        ///
        /// The slack/clamp arithmetic MIRRORS <see cref="TryComputeSpanLoopUT"/> EXACTLY:
        /// <c>compressedSpan = (totalCut &gt; 0 &amp;&amp; totalCut &lt; span) ? span - totalCut : span</c>;
        /// <c>W_{win-1}</c> is <see cref="ComputePerLoopArrivalHoldSeconds"/> for the LAUNCHING cycle, clamped by
        /// <c>if (W &gt; 0 &amp;&amp; compressedSpan + W &gt; cadence) W = max(0, cadence - compressedSpan)</c>;
        /// <c>slack = max(0, cadence - compressedSpan - W_{win-1})</c>. A degenerate / NaN advance collapses to
        /// 0 (mirroring <see cref="ComputePerLoopLaunchAdvanceSeconds"/>). Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static double ComputeCappedLaunchAdvanceSeconds(
            double phaseAnchorUT, double spanStartUT, double spanEndUT, double cadence, long win,
            double launchBodyRotationPeriod, IReadOnlyList<LoopCut> loiterCuts,
            double arrivalHoldSeconds, double arrivalAlignPeriod)
        {
            double delta = ComputePerLoopLaunchAdvanceSeconds(
                phaseAnchorUT, spanStartUT, win, cadence, launchBodyRotationPeriod);
            if (!(delta > 0.0))
                return 0.0; // degenerate / NaN / already-aligned -> no advance (and nothing to cap)

            double span = spanEndUT - spanStartUT;
            double totalCut = TotalCutLength(loiterCuts);
            double compressedSpan = (totalCut > 0.0 && totalCut < span) ? span - totalCut : span;

            // W_{win-1}: the LAUNCHING cycle's per-loop arrival hold (the instance for window `win` launches in
            // cycle win-1's idle tail). Mirror TryComputeSpanLoopUT's hold derivation + clamp EXACTLY.
            double w = (arrivalHoldSeconds > 0.0 && !double.IsInfinity(arrivalHoldSeconds)) ? arrivalHoldSeconds : 0.0;
            if (w > 0.0)
                w = ComputePerLoopArrivalHoldSeconds(w, win - 1, cadence, arrivalAlignPeriod);
            if (w > 0.0 && compressedSpan + w > cadence)
                w = Math.Max(0.0, cadence - compressedSpan);

            double slack = cadence - compressedSpan - w;
            if (slack < 0.0)
                slack = 0.0;
            return delta < slack ? delta : slack;
        }

        /// <summary>
        /// The BOUNDARY-OVERLAP launch advance for replayed loop <paramref name="win"/>: the advance the
        /// borrow-at-launch / repay-at-SOI-exit clock actually uses when the BOUNDARY-OVERLAP launch render
        /// (docs/dev/plan-launch-boundary-overlap.md, Design B) is in play. It is GATED, NOT blanket-uncapped:
        ///
        /// <list type="bullet">
        /// <item>When <c>rawDelta &lt;= cappedDelta</c> (the cap did NOT bite - the common, already-aligned
        /// slack&gt;0 loop), this returns the SAME value as <see cref="ComputeCappedLaunchAdvanceSeconds"/>, so the
        /// span clock primary, region B's early launch instant, and the boundary-overlap gate are ALL byte-identical
        /// to today (no secondary, no extra ghost / map vessel / polyline head). Zero regression surface.</item>
        /// <item>When <c>rawDelta &gt; cappedDelta</c> (the zero/low-slack loop the cap used to truncate, leaving a
        /// residual launch-&gt;escape seam), this returns the FULL <c>rawDelta</c> (uncapped). The launch realigns
        /// fully (the seam closes) and the early-launching NEXT instance is rendered as a SECONDARY ghost during the
        /// borrow window (the previous instance is far downstream near the destination by then, so the two ghosts sit
        /// at different places - no overlap with the still-live previous instance).</item>
        /// </list>
        ///
        /// The borrow window is then <c>[cadence - rawDelta, cadence)</c>. <c>rawDelta &lt; T_sid</c> by construction
        /// (<see cref="ComputePerLoopLaunchAdvanceSeconds"/> returns [0, T_sid)), so it is bounded by one sidereal
        /// day (~6 h for Kerbin) without any new constant. <see cref="ComputeCappedLaunchAdvanceSeconds"/> is KEPT
        /// intact (it remains the source of truth for the diagnostic <c>residualDeg</c> and is used by the cap-only
        /// readers); this helper is ADDED alongside it. A degenerate / NaN advance collapses to 0. Pure;
        /// no Unity (xUnit-testable).
        /// </summary>
        internal static double ComputeBoundaryOverlapAdvanceSeconds(
            double phaseAnchorUT, double spanStartUT, double spanEndUT, double cadence, long win,
            double launchBodyRotationPeriod, IReadOnlyList<LoopCut> loiterCuts,
            double arrivalHoldSeconds, double arrivalAlignPeriod)
        {
            double rawDelta = ComputePerLoopLaunchAdvanceSeconds(
                phaseAnchorUT, spanStartUT, win, cadence, launchBodyRotationPeriod);
            if (!(rawDelta > 0.0))
                return 0.0; // degenerate / NaN / already-aligned -> no advance (and no boundary overlap)

            double cappedDelta = ComputeCappedLaunchAdvanceSeconds(
                phaseAnchorUT, spanStartUT, spanEndUT, cadence, win,
                launchBodyRotationPeriod, loiterCuts, arrivalHoldSeconds, arrivalAlignPeriod);

            // Boundary overlap engages ONLY when the cap actually bites (the residual-seam loops): then use the
            // full raw delta so the launch realigns; otherwise the value equals the capped advance (byte-identical
            // to today, no secondary).
            return (rawDelta > cappedDelta + 1e-9) ? rawDelta : cappedDelta;
        }

        /// <summary>
        /// Effective-span phase -&gt; compressed-span phase under an arrival HOLD of
        /// <paramref name="holdSeconds"/> inserted at compressed-span phase position
        /// <paramref name="holdPhasePos"/> (the heliocentric-&gt;capture boundary, in compressed-span phase
        /// from spanStart). Before the boundary the mapping is identity; ACROSS the hold window the phase
        /// is HELD at the boundary (the ghost waits at SOI arrival); AFTER it, the phase resumes shifted
        /// EARLIER by the hold (the recorded in-SOI sequence, just deferred in live time). The inverse of
        /// the removal <see cref="CompressSpanUT"/> performs: this INSERTS dead time. Identity for
        /// <paramref name="holdSeconds"/> &lt;= 0. The caller then maps the returned compressed-span phase
        /// through <see cref="DecompressSpanUT"/> (loiter cuts) to the recorded loopUT, so holds and cuts
        /// compose. Pure.
        /// </summary>
        internal static double ApplyArrivalHoldToPhase(
            double effectivePhase, double holdPhasePos, double holdSeconds)
        {
            if (double.IsNaN(holdSeconds) || holdSeconds <= 0.0)
                return effectivePhase;
            if (effectivePhase <= holdPhasePos)
                return effectivePhase;                  // before the boundary: identity
            if (effectivePhase <= holdPhasePos + holdSeconds)
                return holdPhasePos;                    // within the hold: held at the boundary (waiting)
            return effectivePhase - holdSeconds;        // after the hold: recorded sequence, deferred
        }

        /// <summary>
        /// Effective-span phase -&gt; compressed-span phase under an M4b loiter EXTENSION of
        /// <paramref name="extLen"/> seconds inserted at compressed-span phase position
        /// <paramref name="insertPos"/> (the start of the phasing run's LAST recorded rev). Unlike
        /// the arrival hold (which freezes the ghost at a boundary), the inserted dead time WRAPS
        /// the final recorded parking rev: within the window the phase maps to
        /// <c>insertPos + ((phase - insertPos) mod wrapPeriod)</c>, so the ghost keeps orbiting the
        /// same closed orbit (whole-rev sawtooth, position-/velocity-continuous at every seam);
        /// after the window the phase resumes shifted EARLIER by extLen and the final rev plays
        /// once more for real before exiting the loiter. extLen is a whole multiple of
        /// <paramref name="wrapPeriod"/> by construction (d * T_park), so the exit seam is exact.
        /// Identity for a non-positive/NaN extension or degenerate inputs. Pure.
        /// </summary>
        internal static double ApplyLoiterExtensionToPhase(
            double phase, double insertPos, double extLen, double wrapPeriod)
        {
            if (double.IsNaN(extLen) || extLen <= 0.0
                || double.IsNaN(insertPos)
                || double.IsNaN(wrapPeriod) || double.IsInfinity(wrapPeriod) || wrapPeriod <= 0.0)
                return phase;
            if (phase <= insertPos)
                return phase;                            // before the loiter tail: identity
            if (phase < insertPos + extLen)
                return insertPos + ((phase - insertPos) % wrapPeriod); // wrapping the final rev
            return phase - extLen;                       // after the extension: recorded, deferred
        }

        /// <summary>
        /// The M4b knob's per-frame BODY-FIXED TIME SHIFT for a scheduled unit member: how much
        /// LATER (positive, loiter extension) or EARLIER (negative, loiter cut) the live replay of
        /// recorded UT <paramref name="loopUT"/> happens relative to the rotation-aligned baseline
        /// <c>launchUT + (loopUT - spanStartUT)</c>. Body-fixed point sections (vacuum burn arcs
        /// recorded as lat/lon/alt) replayed with a non-zero shift render rotated with the planet
        /// by <c>shift mod T_rot</c> (the 2026-06-11 playtest's 46-degree map-icon teleports);
        /// positioning derotates by this value via
        /// <see cref="TrajectoryMath.FrameTransform.ShiftLongitudeDegrees"/>. Exact in EVERY clock
        /// phase including the extension wrap's sawtooth passes (the formula compares live phase to
        /// recorded offset directly); identically 0 for a knob-less schedule (where
        /// <c>loopUT = spanStart + phase</c> by construction) - callers gate on
        /// <see cref="MissionRelaunchSchedule.HasPhasingKnob"/> to keep that exactness free of
        /// float dust. Pure.
        /// </summary>
        internal static double ComputeScheduledBodyFixedShiftSeconds(
            double currentUT, double launchUT, double loopUT, double spanStartUT)
        {
            return (currentUT - launchUT) - (loopUT - spanStartUT);
        }

        /// <summary>
        /// Resolves the body-fixed time shift for committed member <paramref name="committedIndex"/>
        /// at the current frame: 0 unless the member belongs to a loop unit whose schedule carries
        /// the M4b phasing knob and a launch is active at <paramref name="currentUT"/>.
        /// <paramref name="loopUT"/> must be the same span-clock loopUT the caller renders at.
        /// The surface seam for map markers / tracking-station sampling (the flight engine computes
        /// the same value inline from the unit it already holds). Pure.
        /// </summary>
        internal static double ComputeUnitMemberBodyFixedShiftSeconds(
            int committedIndex, double currentUT, double loopUT, LoopUnitSet units)
        {
            if (units == null || !units.TryGetUnitForMember(committedIndex, out LoopUnit unit))
                return 0.0;
            MissionRelaunchSchedule sched = unit.RelaunchSchedule;
            if (sched == null || !sched.HasPhasingKnob)
                return 0.0;
            if (!sched.TryResolveActiveLaunch(currentUT, out double launchUT, out _))
                return 0.0;
            return ComputeScheduledBodyFixedShiftSeconds(
                currentUT, launchUT, loopUT, unit.SpanStartUT);
        }

        /// <summary>
        /// Span loop clock for a chain-loop unit. Walks a single loop phase over the whole
        /// unit span [<paramref name="spanStartUT"/>, <paramref name="spanEndUT"/>] and
        /// returns the <paramref name="loopUT"/> inside that span plus the 0-based unit cycle
        /// index. v1 cadence == span duration, so the wrap from spanEnd back to spanStart is
        /// seamless (no pause window): unlike <see cref="ComputeLoopPhaseFromUT"/> there is no
        /// inter-cycle pause to report.
        ///
        /// <paramref name="loiterCuts"/> (re-aim only) excises whole-period loiters: the phase then wraps
        /// over the COMPRESSED span (<c>span - totalCut</c>) and the clamped compressed phase is remapped
        /// to a recorded loopUT that SKIPS the cut intervals. Null/empty cuts =&gt; byte-identical to the
        /// pre-compression clock (every non-re-aim caller). Only the uniform path honors the STATIC
        /// <paramref name="loiterCuts"/> list; the <paramref name="schedule"/> path ignores it (re-aim
        /// always passes schedule = null). A knob-engaged schedule instead carries its OWN per-launch
        /// cuts/extension (<see cref="MissionRelaunchSchedule.TryGetLaunchTiming"/>, M4b), which the
        /// schedule branch applies per launch.
        ///
        /// The cadence is clamped to <see cref="LoopTiming.MinCycleDuration"/> INSIDE this
        /// helper (edge 14): the span clock does NOT route through ResolveLoopInterval, so it
        /// does not inherit that clamp for free. A clamped cadence longer than the span leaves
        /// the clock parked at spanEndUT for the tail of each cycle.
        ///
        /// Returns false (loopUT = spanStartUT, cycleIndex = 0) when currentUT is before the
        /// span start or the span has zero/negative duration, so callers never see a negative
        /// phase. Pure except a single rate-limited Verbose line emitted once per replayed loop
        /// ONLY on the re-aim per-loop-hold branch (<paramref name="arrivalHoldSeconds"/> &gt; 0 with a
        /// valid <paramref name="arrivalHoldAlignPeriod"/>); the common path stays silent and
        /// per-frame callers still own their own rate-limiting.
        ///
        /// <paramref name="isInInterCycleTail"/> (mirrors <see cref="ComputeLoopPhaseFromUT"/>'s
        /// isInPause) is true exactly when the phase has run past the span and the clock is parked
        /// at spanEndUT waiting for the next cycle: the idle "tail" that only exists when
        /// cadence > span. For the loop feature cadence == span, so the phase never reaches the
        /// span in the play branch and the boundary-rollback branch reports false — the flag is
        /// ALWAYS false there (zero behavior change). A future cadence > span producer (logistics
        /// supply routes: dispatch interval >= transit) reads this to HIDE the ghost during the
        /// parked tail (vessel delivered, nothing in transit) instead of freezing the last
        /// segment's ghost at the dock. False on both early return paths and at the legitimate
        /// end-of-cycle / wrap boundary.
        /// </summary>
        internal static bool TryComputeSpanLoopUT(
            double currentUT,
            double phaseAnchorUT,
            double spanStartUT,
            double spanEndUT,
            double cadenceSeconds,
            out double loopUT,
            out long cycleIndex,
            out bool isInInterCycleTail,
            MissionRelaunchSchedule schedule = null,
            IReadOnlyList<LoopCut> loiterCuts = null,
            double arrivalHoldSeconds = 0.0,
            double arrivalHoldAtUT = double.NaN,
            double arrivalHoldAlignPeriod = double.NaN,
            double launchBodyRotationPeriod = double.NaN,
            bool launchHoldEngaged = false,
            double soiExitAtUT = double.NaN)
        {
            // PRIMARY-ONLY wrapper (docs/dev/plan-launch-boundary-overlap.md 2.1): returns only the continuing
            // instance N (the long-lived through-line the camera follows). The OPTIONAL boundary-overlap secondary
            // (instance N+1's early in-SOI launch) is exposed by ComputeSpanLoopFrame for the dual-clock dispatch.
            // Every existing caller (the resolver windowIndex with schedule:null, the watch clock, KSC, the
            // loop-synced debris parent clock, the single-instance scenes) stays byte-identical: the primary equals
            // today's region-A render, and on already-aligned (slack>0) loops the boundary-overlap advance equals
            // the old capped advance, so the primary is unchanged.
            SpanLoopFrame frame = ComputeSpanLoopFrame(
                currentUT, phaseAnchorUT, spanStartUT, spanEndUT, cadenceSeconds,
                schedule, loiterCuts, arrivalHoldSeconds, arrivalHoldAtUT, arrivalHoldAlignPeriod,
                launchBodyRotationPeriod, launchHoldEngaged, soiExitAtUT);
            loopUT = frame.LoopUT;
            cycleIndex = frame.CycleIndex;
            isInInterCycleTail = frame.IsInInterCycleTail;
            return frame.Resolved;
        }

        /// <summary>
        /// The dual-clock span-loop frame (docs/dev/plan-launch-boundary-overlap.md, Design B): the body of the old
        /// <see cref="TryComputeSpanLoopUT"/> with two changes - (1) the launch advance (region A) and the next
        /// instance's advance (region B) come from <see cref="ComputeBoundaryOverlapAdvanceSeconds"/> (gated full
        /// raw delta on the zero-slack residual-seam loops, the old capped advance otherwise), and (2) region B no
        /// longer mutates the PRIMARY's cycle - the primary stays on the continuing instance N for the whole cycle
        /// (region-A formula across the borrow window) and the early-launching instance N+1 is emitted as the
        /// OPTIONAL SECONDARY. The secondary is present ONLY inside the borrow window
        /// <c>phaseInCycle &gt;= cadence - advNext</c> AND only when the boundary overlap actually engages
        /// (<c>advNext</c> exceeds the cap = the cap bit = the residual-seam loop). On already-aligned (slack&gt;0)
        /// loops and the not-engaged / not-re-aim path the boundary-overlap advance equals the old capped advance,
        /// region B never fires (advNext == capped), so <c>HasSecondary == false</c> and the primary is
        /// byte-identical to today.
        /// </summary>
        internal static SpanLoopFrame ComputeSpanLoopFrame(
            double currentUT,
            double phaseAnchorUT,
            double spanStartUT,
            double spanEndUT,
            double cadenceSeconds,
            MissionRelaunchSchedule schedule = null,
            IReadOnlyList<LoopCut> loiterCuts = null,
            double arrivalHoldSeconds = 0.0,
            double arrivalHoldAtUT = double.NaN,
            double arrivalHoldAlignPeriod = double.NaN,
            double launchBodyRotationPeriod = double.NaN,
            bool launchHoldEngaged = false,
            double soiExitAtUT = double.NaN)
        {
            double loopUT = spanStartUT;
            long cycleIndex = 0;
            bool isInInterCycleTail = false;
            bool hasSecondary = false;
            double secondaryLoopUT = spanStartUT;
            long secondaryCycleIndex = 0;

            double span = spanEndUT - spanStartUT;
            if (span <= 0)
                return new SpanLoopFrame(false, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex);

            // Zero-drift per-window reschedule (docs/dev/plans/zero-drift-reschedule.md): when a
            // non-uniform schedule is attached, the active relaunch is the largest scheduled launch
            // <= currentUT (NOT phaseAnchorUT + n*cadence). The phase within the span is measured
            // from that launch; the clock parks at spanEnd between launches (interval > span always
            // for a scheduled unit, so the tail engages and the caller hides all members - render
            // nothing in the gap). Null schedule -> the uniform path below, byte-identical.
            if (schedule != null)
            {
                // INV-3 self-defending guard (docs/dev/plans/zero-drift-reschedule-hardening.md Phase B):
                // a scheduled unit is mutually exclusive with loiter cuts / arrival hold BY CONSTRUCTION
                // (the builder attaches a schedule ONLY in the same-parent phase-locked block; the re-aim
                // branch that produces cuts/hold sets relaunchSchedule=null). This branch returns early
                // before the cut/hold remap, so the predicate below is ALWAYS false on the shipped path
                // (zero behavior change, byte-identical). It is here ONLY to convert a latent silent-drop
                // (a future same-parent loiter compression or a re-aim unit that ever co-attached a
                // schedule) into a LOUD contract violation. This is a per-frame hot path with a documented
                // purity contract (see the summary above), so the warning is RATE-LIMITED + keyed on
                // mission identity (phaseAnchorUT + spanStartUT), mirroring the per-loop-hold key below -
                // a raw per-frame Warn / a throw would spam the log across multiple scenes. Degrading, not
                // crashing: a future misuse stays visible without breaking playback.
                if (loiterCuts != null || arrivalHoldSeconds > 0.0)
                {
                    var gic = CultureInfo.InvariantCulture;
                    ParsekLog.VerboseRateLimited(
                        "MissionPeriodicity",
                        // Mission identity key: distinct missions get distinct keys (no collision); one
                        // mission keeps ONE key across all frames, so the key set is bounded by mission
                        // count, not frame count.
                        $"sched-mutex-violation.{phaseAnchorUT.ToString("R", gic)}.{spanStartUT.ToString("R", gic)}",
                        "INV-3 contract violation: a scheduled unit carries " +
                        $"loiterCuts={(loiterCuts != null ? loiterCuts.Count.ToString(gic) : "0")} " +
                        $"arrivalHoldSeconds={arrivalHoldSeconds.ToString("R", gic)} - the schedule branch " +
                        "bypasses the cut/hold remap (schedule and cuts/hold are mutually exclusive by " +
                        "construction). The cut/hold is silently DROPPED; check the builder routing.");
                }

                if (!schedule.TryResolveActiveLaunch(currentUT, out double launchUT, out long sIdx))
                    return new SpanLoopFrame(false, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex); // parked before the first scheduled launch
                double scheduledPhase = currentUT - launchUT;
                if (scheduledPhase < 0.0)
                    scheduledPhase = 0.0;
                cycleIndex = sIdx;

                // M4b phasing-loiter knob (docs/dev/plans/mission-loiter-knob.md section 4.1): a
                // knob-engaged schedule carries PER-LAUNCH timing - this launch's loiter cuts and
                // (for an extended loiter) the last-rev wrap. The phase runs over the per-launch
                // EFFECTIVE span (span - cuts + extension), maps through the extension wrap, and
                // decompresses through the cuts to the recorded loopUT. Knob-less schedules (no
                // entry) keep the plain path below byte-identical. This is the sanctioned
                // schedule+cuts composition; the INV-3 guard above still flags the UNSANCTIONED
                // pairing (a schedule with the static LoopUnit.LoiterCuts list / arrival hold).
                if (schedule.TryGetLaunchTiming(sIdx, out LaunchTimingEntry timing))
                {
                    double effSpan = span - TotalCutLength(timing.Cuts) + timing.ExtensionSeconds;
                    if (effSpan <= 0.0)
                        effSpan = span; // defensive: a degenerate entry never bricks the clock
                    isInInterCycleTail = (scheduledPhase >= effSpan);
                    double clamped = scheduledPhase >= effSpan ? effSpan : scheduledPhase;
                    double insertPos = timing.ExtensionSeconds > 0.0
                        ? CompressSpanUT(timing.ExtensionWrapStartUT, timing.Cuts) - spanStartUT
                        : double.NaN;
                    double wrapped = ApplyLoiterExtensionToPhase(
                        clamped, insertPos, timing.ExtensionSeconds, timing.ExtensionWrapPeriod);
                    loopUT = DecompressSpanUT(spanStartUT + wrapped, timing.Cuts);
                    return new SpanLoopFrame(true, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex);
                }

                isInInterCycleTail = (scheduledPhase >= span);
                loopUT = spanStartUT + (scheduledPhase >= span ? span : scheduledPhase);
                return new SpanLoopFrame(true, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex);
            }

            if (currentUT < phaseAnchorUT)
                return new SpanLoopFrame(false, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex);

            // Edge 14: clamp the cadence here. The span clock has no ResolveLoopInterval clamp.
            double cycleDuration = Math.Max(cadenceSeconds, LoopTiming.MinCycleDuration);

            // Phase is measured from the anchor (the UT the loop was enabled at), NOT the absolute
            // span start: at enable-time elapsed == 0 -> phase 0 -> loopUT == spanStartUT, so every
            // enable (and re-enable) restarts the looped mission from the recording's start. When
            // phaseAnchorUT == spanStartUT this reduces to the old absolute-phase behavior.
            double elapsed = currentUT - phaseAnchorUT;
            cycleIndex = (long)(elapsed / cycleDuration);
            double phaseInCycle = elapsed - (cycleIndex * cycleDuration);

            // Per-loop LAUNCH ALIGNMENT (docs/dev/design-reaim-launch-hold-seam.md, borrow-at-launch /
            // repay-at-SOI-exit): closes the launch->escape render seam for the span>=synodic re-aim regime
            // PadAlignLaunch declines (cadence != synodic). For replayed loop N the launch happens delta_N
            // EARLIER (at L_N - delta_N) so the in-Kerbin-SOI verbatim replay (ascent + parking + escape) runs
            // at the RECORDED launch-body rotation (currentUT - spanStartUT is a whole multiple of T_sid),
            // closing the seam; the borrowed delta_N is REPAID as a coast hold inserted at the SOI-exit
            // boundary, so everything from the SOI exit onward (heliocentric transfer, trans-target burn,
            // destination arrival + its arrival hold, landing) is UNCHANGED vs baseline. delta_N is computed
            // AFTER cycleIndex / phaseInCycle (like the per-loop arrival hold) so it never changes cadence, the
            // phase anchor, the cycle index, or the resolver's window-index<->departure map. Gated on
            // launchHoldEngaged (the builder sets it only when re-aim engaged && !pad.Applied && plan.Supported
            // && a valid SOI-exit boundary); a degenerate T_sid returns 0. Every other caller passes the
            // default (not engaged, NaN period), so the helper returns 0 and the clock stays byte-identical.
            //
            // The raw uncapped delta for THIS cycle's instance N (region A renders instance N). The BOUNDARY-OVERLAP
            // advance (docs/dev/plan-launch-boundary-overlap.md 2.2) is computed below through
            // ComputeBoundaryOverlapAdvanceSeconds: on an already-aligned (slack>0) loop it returns the OLD capped
            // advance (= min(delta_N, slack_{N-1}), byte-identical), so region A and region B agree on the same
            // instance's advance and the cycle boundary is continuous; on a zero/low-slack loop where the cap used to
            // bite (the residual-seam loops) it returns the FULL raw delta so the launch realigns and an N+1 secondary
            // is emitted for the borrow window. The OLD capped advance is ALSO computed (cappedLaunchAdvance) - it
            // remains the source of truth for the diagnostic residualDeg (rawDeltaN - cappedAdvance), and
            // MissionsWindowUI.ComputeNextRelaunchUT now reads ComputeBoundaryOverlapAdvanceSeconds so the navigable
            // launch time agrees with the clock. The boundary-overlap advance is gated so a not-engaged / not-re-aim
            // caller gets 0 and the clock is byte-identical.
            double rawLaunchAdvance = launchHoldEngaged
                ? ComputePerLoopLaunchAdvanceSeconds(phaseAnchorUT, spanStartUT, cycleIndex, cycleDuration, launchBodyRotationPeriod)
                : 0.0;
            double cappedLaunchAdvance = launchHoldEngaged
                ? ComputeCappedLaunchAdvanceSeconds(
                    phaseAnchorUT, spanStartUT, spanEndUT, cycleDuration, cycleIndex,
                    launchBodyRotationPeriod, loiterCuts, arrivalHoldSeconds, arrivalHoldAlignPeriod)
                : 0.0;
            double launchAdvance = launchHoldEngaged
                ? ComputeBoundaryOverlapAdvanceSeconds(
                    phaseAnchorUT, spanStartUT, spanEndUT, cycleDuration, cycleIndex,
                    launchBodyRotationPeriod, loiterCuts, arrivalHoldSeconds, arrivalHoldAlignPeriod)
                : 0.0;
            // Whether the boundary overlap ENGAGES for the region-A instance N (the cap bit -> full raw delta used).
            // On an already-aligned loop launchAdvance == cappedLaunchAdvance and this is false (no secondary).
            bool boundaryOverlapEngagedA = launchAdvance > cappedLaunchAdvance + 1e-9;

            // Loiter compression (docs/dev/plans/reaim-loiter-compression.md section 5): when a re-aim unit
            // carries whole-period loiter cuts, the loop's ACTIVE duration is the COMPRESSED span
            // (span - totalCut); the phase wraps over that, and the clamped compressed phase is remapped to
            // a recorded loopUT that SKIPS the cut intervals. Members stay on recorded UTs (so faithful
            // Points members render unchanged, just earlier in live time); the cut interval is never
            // sampled. Cadence is cut-independent (a cut lives within one cycle), so cycleIndex / elapsed /
            // the boundary-rollback below are unchanged. Empty/null cuts => effectiveSpan == span and the
            // remap is the identity, so every non-re-aim caller is byte-identical.
            double totalCut = TotalCutLength(loiterCuts);
            double compressedSpan = (totalCut > 0.0 && totalCut < span) ? span - totalCut : span;
            // Arrival HOLD (re-aim cross-parent landing alignment): the INVERSE of a loiter cut. A cut
            // REMOVES recorded-span time (compressedSpan); the hold INSERTS it at the heliocentric->capture
            // boundary, so the in-SOI replay defers and the destination rotation phase at the deorbit recurs
            // to recorded. holdPhasePos is that boundary in compressed-span phase. A zero/degenerate hold
            // leaves effectiveSpan == compressedSpan and the remap below the identity, so every existing
            // caller (passing the default 0 hold) stays byte-identical. The re-aim cadence is the synodic
            // period, far larger than span + hold, so the hold never pushes effectiveSpan past the cadence.
            double hold = (arrivalHoldSeconds > 0.0 && !double.IsInfinity(arrivalHoldSeconds)) ? arrivalHoldSeconds : 0.0;
            // Per-loop arrival hold (docs/dev/plans/reaim-destination-arrival-alignment.md sections 13b/13c):
            // the constant base hold (W_0) aligns the destination rotation phase at the deorbit for ONE loop,
            // but the synodic cadence is not a whole number of destination rotations, so the deorbit drifts a
            // fraction of a turn each loop. Override the constant hold with the per-loop W_N for the current
            // cycleIndex (cadence == cycleDuration, the live per-loop advance). Strictly gated on hold > 0
            // (W_0 > 0): when the base hold is 0 (alignment Off / Drop) the helper returns 0 unchanged, so Off
            // stays byte-identical (the 13b regression fence); a degenerate rotation period also returns the
            // base hold unchanged, keeping the constant-hold behavior. cycleIndex / phaseInCycle are computed
            // above (BEFORE the base hold read), so the per-loop recurrence is open-loop in N (no feedback).
            if (hold > 0.0)
            {
                double w0 = hold;
                hold = ComputePerLoopArrivalHoldSeconds(w0, cycleIndex, cycleDuration, arrivalHoldAlignPeriod);
                // The arrival hold is UNCHANGED by the launch alignment (borrow-at-launch / repay-at-SOI-exit):
                // the SOI-exit repay nets to zero with the earlier launch, so the SOI entry occurs at the SAME
                // live UT as baseline and W_N aligns the destination rotation phase correctly with no
                // compensation. (The PR #1174 forward-shift required a (W_N - H_launch) subtraction; the
                // borrow-repay model removes it.)
                bool tAlignValid = !double.IsNaN(arrivalHoldAlignPeriod) && !double.IsInfinity(arrivalHoldAlignPeriod)
                    && arrivalHoldAlignPeriod > 0.0;
                // Per-frame hot path: rate-limited per mission (keyed on phaseAnchorUT + spanStartUT, NOT
                // cycleIndex, so the key set stays bounded by mission count rather than growing one entry per
                // replayed loop). The message carries cycleIndex and W_N, so a playtest sees W_N step as the
                // loop advances across the periodic lines, without per-frame spam.
                if (tAlignValid)
                {
                    var hic = CultureInfo.InvariantCulture;
                    ParsekLog.VerboseRateLimited(
                        "Reaim",
                        // Key is the mission identity (phaseAnchorUT + spanStartUT): distinct re-aim missions
                        // get distinct keys (no collision), and a single mission keeps ONE key across all its
                        // loops, so the rate-limiter key set is bounded by mission count, not loop count.
                        $"perloop-hold.{phaseAnchorUT.ToString("R", hic)}.{spanStartUT.ToString("R", hic)}",
                        $"per-loop arrival hold: cycleIndex={cycleIndex.ToString(hic)} " +
                        $"W0={w0.ToString("R", hic)}s cadence={cycleDuration.ToString("R", hic)}s " +
                        $"Talign={arrivalHoldAlignPeriod.ToString("R", hic)}s WN={hold.ToString("R", hic)}s");
                }
            }
            // Defense-in-depth: never let the holds push the active span past the cadence (a mid-span cycle
            // wrap would silently truncate the in-SOI replay). No current caller can trip this for the common
            // re-aim case (the SOI-exit repay nets to zero with the earlier launch, so the SOI-exit-and-onward
            // timeline stays on baseline and effectiveSpan = compressedSpan + delta + hold <= cadence whenever
            // delta <= slack). The LAUNCH ADVANCE cap (the design's delta_N > slack edge handling, §2.6) is now
            // applied UPSTREAM by ComputeCappedLaunchAdvanceSeconds (launchAdvance + advNext below are already
            // capped to the LAUNCHING cycle's slack), so launchAdvance arrives here pre-bounded; the local slack
            // below is slack_N (= cadence - compressedSpan - W_N), kept only for the diagnostic line + the hold
            // clamp. Capping bounds the borrow to the previous instance's idle gap so the early launch never
            // overlaps the previous instance's live replay, and it can never reopen the seam the way the old
            // forward H_N truncation did (the advance is bounded BY slack). When capped, a residual rotation
            // offset remains only on those loops; the common case (delta <= slack) is unaffected. A WARN fires
            // once per mission so a playtest sees which loops carry a residual.
            double slack = cycleDuration - compressedSpan - hold;
            if (slack < 0.0)
                slack = 0.0;
            // (The per-instance "still capped" diagnostic flag is computed in the diagnostics block below against
            // the RENDERED instance's raw delta vs its actually-used advance - see renderedCapped there.)
            if (hold > 0.0 && compressedSpan + hold > cycleDuration)
                hold = Math.Max(0.0, cycleDuration - compressedSpan);

            // The SOI-exit repay boundary in compressed-span phase (from spanStart): where the delta_N coast
            // hold is inserted so the SOI-exit-and-onward timeline returns to baseline. Mirrors how the arrival
            // hold derives holdPhasePos from arrivalHoldAtUT. Falls back to NaN (no SOI hold) when the launch
            // alignment is not engaged or the boundary is degenerate. The PRIMARY uses this (region-A semantics
            // for the whole cycle); the SECONDARY computes its own below.
            double soiExitPhasePos = (launchAdvance > 0.0 && !double.IsNaN(soiExitAtUT))
                ? CompressSpanUT(soiExitAtUT, loiterCuts) - spanStartUT
                : double.NaN;
            // The NEXT instance's boundary-overlap advance (instance N+1, launching early at
            // phaseInCycle = cadence - advNext of THIS cycle, borrowing advNext from this cycle's idle tail). On a
            // residual-seam loop ComputeBoundaryOverlapAdvanceSeconds(win=cycleIndex+1) returns the full raw delta;
            // on an already-aligned loop it returns the old capped advance and region B never fires (advNext stays
            // == cappedAdvNext, no secondary). The cap-only value is kept for the diagnostic.
            double rawAdvNext = launchHoldEngaged
                ? ComputePerLoopLaunchAdvanceSeconds(phaseAnchorUT, spanStartUT, cycleIndex + 1, cycleDuration, launchBodyRotationPeriod)
                : 0.0;
            double cappedAdvNext = launchHoldEngaged
                ? ComputeCappedLaunchAdvanceSeconds(
                    phaseAnchorUT, spanStartUT, spanEndUT, cycleDuration, cycleIndex + 1,
                    launchBodyRotationPeriod, loiterCuts, arrivalHoldSeconds, arrivalHoldAlignPeriod)
                : 0.0;
            double advNext = launchHoldEngaged
                ? ComputeBoundaryOverlapAdvanceSeconds(
                    phaseAnchorUT, spanStartUT, spanEndUT, cycleDuration, cycleIndex + 1,
                    launchBodyRotationPeriod, loiterCuts, arrivalHoldSeconds, arrivalHoldAlignPeriod)
                : 0.0;
            // Whether the boundary overlap ENGAGES for the next instance N+1 (the cap bit for it -> full raw delta).
            // The secondary is emitted ONLY when this is true (the residual-seam loops); on already-aligned loops
            // advNext == cappedAdvNext so this is false and there is NO secondary (byte-identical to today).
            bool boundaryOverlapEngagedNext = advNext > cappedAdvNext + 1e-9;

            // DUAL-CLOCK boundary-overlap phase model (docs/dev/plan-launch-boundary-overlap.md, Design B). Two
            // regimes, split by whether the boundary overlap ENGAGES for the next instance N+1 (the cap would have
            // bitten -> the residual-seam loop):
            //
            // (a) ALREADY-ALIGNED loop (boundaryOverlapEngagedNext == false): KEEP the OLD single-output behavior,
            //     BYTE-IDENTICAL to today. Region A renders this cycle's instance N (launched advN ago in the prior
            //     tail); region B (phaseInCycle >= cadence - advNext) flips the SINGLE output to the next instance
            //     N+1's early launch in this cycle's idle tail (cycleIndex += 1). advNext equals today's capped
            //     advNext, so the early launch starts inside the previous instance's parked idle tail exactly as it
            //     does today. NO secondary is emitted (HasSecondary stays false). This is invariant 2.
            //
            // (b) ENGAGED loop (boundaryOverlapEngagedNext == true): the cap used to truncate the launch, leaving a
            //     residual seam. The PRIMARY now STAYS on the continuing instance N (region-A formula for the WHOLE
            //     cycle, NO cycleIndex mutation) - the long-lived through-line the camera follows, far downstream
            //     near the destination during the borrow window - and the early-launching instance N+1 is emitted as
            //     the OPTIONAL SECONDARY (a separate concurrent ghost). This avoids Design A's camera yank: with the
            //     cap removed, flipping the single output to N+1 up to T_sid before the boundary would drag every
            //     primary reader onto the fresh launch. The secondary's phaseFromLaunch = phaseInCycle -
            //     (cadence - advNext) (0 at the early launch), cycle = N+1, with its OWN SOI-exit repay.
            //
            // CONTINUITY (b): at phaseInCycle -> cadence the secondary's phaseFromLaunch -> advNext; at
            // phaseInCycle = 0 of cycle N+1 the new primary has phaseFromLaunch = 0 + delta_{N+1} = advNext. Equal -
            // the secondary of cycle N hands off seamlessly to the primary of cycle N+1 (same instance, same loopUT).
            //
            // launchAdvance == 0 (not engaged / degenerate / already aligned with delta 0) collapses BOTH regimes to
            // the plain clock (advNext is 0 so region B never fires, phaseFromLaunch == phaseInCycle), byte-identical.
            double effectiveLaunchAdvance = launchAdvance;
            double phaseFromLaunch = phaseInCycle;
            // True when regime (a)'s OLD single-output region-B early-launch flip fired (the PRIMARY became the next
            // instance N+1 on an already-aligned loop). False for region A and for regime (b) (primary stays on N).
            bool earlyNextFlip = false;
            if (launchAdvance > 0.0)
            {
                bool inBorrowWindow = advNext > 0.0 && phaseInCycle >= cycleDuration - advNext;
                if (inBorrowWindow && !boundaryOverlapEngagedNext)
                {
                    // (a) ALIGNED loop, region B: OLD single-output early-launch flip (byte-identical). The single
                    // output becomes the next instance N+1's early launch in this cycle's parked idle tail.
                    cycleIndex += 1;
                    phaseFromLaunch = phaseInCycle - (cycleDuration - advNext);
                    effectiveLaunchAdvance = advNext;
                    soiExitPhasePos = !double.IsNaN(soiExitAtUT)
                        ? CompressSpanUT(soiExitAtUT, loiterCuts) - spanStartUT
                        : double.NaN;
                    earlyNextFlip = true;
                }
                else
                {
                    // Region A (the current cycle's instance N), OR (b) an engaged loop's borrow window where the
                    // PRIMARY stays on instance N (region-A formula for the whole cycle).
                    phaseFromLaunch = phaseInCycle + launchAdvance;
                    effectiveLaunchAdvance = launchAdvance;

                    // (b) ENGAGED loop SECONDARY: instance N+1's early launch as a separate concurrent ghost.
                    if (inBorrowWindow && boundaryOverlapEngagedNext)
                    {
                        double secPhaseFromLaunch = phaseInCycle - (cycleDuration - advNext);
                        double secSoiExitPhasePos = !double.IsNaN(soiExitAtUT)
                            ? CompressSpanUT(soiExitAtUT, loiterCuts) - spanStartUT
                            : double.NaN;
                        double secEffectiveSpan = compressedSpan + advNext + hold;
                        double secClampedPhase = secPhaseFromLaunch >= secEffectiveSpan ? secEffectiveSpan : secPhaseFromLaunch;
                        double secAfterSoi = ApplyArrivalHoldToPhase(secClampedPhase, secSoiExitPhasePos, advNext);
                        double secHoldPhasePos = hold > 0.0 ? CompressSpanUT(arrivalHoldAtUT, loiterCuts) - spanStartUT : double.NaN;
                        double secCutPhase = ApplyArrivalHoldToPhase(secAfterSoi, secHoldPhasePos, hold);
                        secondaryLoopUT = DecompressSpanUT(spanStartUT + secCutPhase, loiterCuts);
                        secondaryCycleIndex = cycleIndex + 1;
                        hasSecondary = true;
                    }
                }
            }

            // Per-loop LAUNCH ADVANCE diagnostic (design 6.4 + boundary-overlap plan 2.3): a rate-limited Verbose
            // Reaim line, gated on launchHoldEngaged && a finite/positive T_sid (a degenerate T_sid yields
            // launchAdvance == 0). Keyed on mission identity (phaseAnchorUT + spanStartUT, NOT cycleIndex) so the key
            // set stays bounded by mission count. The diagnostic describes the PRIMARY (instance N, region-A
            // semantics for the whole cycle). Under the boundary overlap, the primary's launchAdvance is the FULL raw
            // delta on engaged loops, so residualDeg ~ 0 (the seam closes). A `secondaryActive` field reports the
            // boundary-overlap secondary when present. The capped value (cappedLaunchAdvance) is still computed so
            // residualDeg = (rawDeltaN - cappedAdvance) reports how far the OLD cap would have fallen short - useful
            // to confirm the boundary overlap closed a previously-capped loop. The launch-advance-capped WARN now
            // fires only on the (re-aim-impossible) genuinely-uncloseable case (launchAdvanceCapped, where even the
            // boundary overlap could not use the full raw delta).
            if (launchHoldEngaged)
            {
                double tSid = Math.Abs(launchBodyRotationPeriod);
                if (!double.IsNaN(tSid) && !double.IsInfinity(tSid) && tSid > 0.0)
                {
                    var lic = CultureInfo.InvariantCulture;
                    // The RENDERED instance: regime (a)'s region-B flip renders instance N+1 (cycleIndex was
                    // incremented; raw delta rawAdvNext); region A / regime (b) renders instance N (raw delta
                    // rawLaunchAdvance). The launching-cycle slack for the rendered instance is slack_{(rendered)-1}:
                    // for the flipped N+1 that is slack_N (= the local `slack`); for instance N that is slack_{N-1}.
                    double rawDeltaN = earlyNextFlip ? rawAdvNext : rawLaunchAdvance;
                    double renderedSlack;
                    if (earlyNextFlip)
                    {
                        renderedSlack = slack; // slack_N, the launching cycle of the flipped instance N+1
                    }
                    else
                    {
                        double wPrev = (arrivalHoldSeconds > 0.0 && !double.IsInfinity(arrivalHoldSeconds))
                            ? ComputePerLoopArrivalHoldSeconds(arrivalHoldSeconds, cycleIndex - 1, cycleDuration, arrivalHoldAlignPeriod)
                            : 0.0;
                        if (wPrev > 0.0 && compressedSpan + wPrev > cycleDuration)
                            wPrev = Math.Max(0.0, cycleDuration - compressedSpan);
                        renderedSlack = cycleDuration - compressedSpan - wPrev;
                        if (renderedSlack < 0.0)
                            renderedSlack = 0.0;
                    }
                    // residualDeg = the leftover launch-body rotation NOT aligned, as a fraction of one sidereal
                    // rotation in degrees [0,360): (rawDeltaN - effectiveLaunchAdvance). 0 when the boundary overlap
                    // engaged (advance == full raw delta) OR the loop was already aligned (raw delta <= cap, the flip
                    // uses advNext == rawAdvNext). Non-zero only on the genuinely-uncloseable case.
                    double residualSeconds = rawDeltaN - effectiveLaunchAdvance;
                    double residualDeg = (residualSeconds % tSid) / tSid * 360.0;
                    if (residualDeg < 0.0)
                        residualDeg += 360.0;
                    // renderedCapped: did the rendered instance's raw delta exceed the advance it actually used?
                    // For the flipped N+1 (regime a) the flip uses advNext == rawAdvNext (aligned loop, no cap), so
                    // this is false; for instance N it is false when the boundary overlap engaged (full raw delta)
                    // or the loop was aligned. True only on the genuinely-uncloseable case.
                    bool renderedCapped = rawDeltaN > effectiveLaunchAdvance + 1e-9;
                    bool boundaryOverlapEngagedRendered = earlyNextFlip ? boundaryOverlapEngagedNext : boundaryOverlapEngagedA;
                    double renderedCappedAdv = earlyNextFlip ? cappedAdvNext : cappedLaunchAdvance;
                    // secondaryActive reports the live N+1 secondary this frame (regime b only).
                    ParsekLog.VerboseRateLimited(
                        "Reaim",
                        $"perloop-launch-advance.{phaseAnchorUT.ToString("R", lic)}.{spanStartUT.ToString("R", lic)}",
                        $"per-loop launch advance: cycleIndex={cycleIndex.ToString(lic)} " +
                        $"Tsid={tSid.ToString("R", lic)}s cadence={cycleDuration.ToString("R", lic)}s " +
                        $"deltaN={effectiveLaunchAdvance.ToString("R", lic)}s slack={renderedSlack.ToString("R", lic)}s " +
                        $"cappedAdv={renderedCappedAdv.ToString("R", lic)}s boundaryOverlap={boundaryOverlapEngagedRendered} " +
                        $"region={(earlyNextFlip ? "B-earlyNext" : "A-current")} " +
                        $"secondaryActive={hasSecondary} secondaryLoopUT={(hasSecondary ? secondaryLoopUT.ToString("R", lic) : "(none)")} " +
                        $"capped={renderedCapped} " +
                        $"rawDeltaN={rawDeltaN.ToString("R", lic)}s residualDeg={residualDeg.ToString("R", lic)}");
                    if (renderedCapped)
                        ParsekLog.WarnRateLimited(
                            "Reaim",
                            $"launch-advance-capped.{phaseAnchorUT.ToString("R", lic)}.{spanStartUT.ToString("R", lic)}",
                            $"per-loop launch advance still CAPPED (boundary overlap could not use the full raw " +
                            $"delta - genuinely uncloseable seam); cycleIndex={cycleIndex.ToString(lic)} " +
                            $"cadence={cycleDuration.ToString("R", lic)}s " +
                            $"rawDeltaN={rawDeltaN.ToString("R", lic)}s residualDeg={residualDeg.ToString("R", lic)}");
                    // One-shot per-mission `boundary-overlap engaged` line (plan 2.3): fires the first time the
                    // boundary overlap engages for this mission (the primary uses the full raw delta because the
                    // old cap would have bitten). Rate-limited per mission identity (its own key).
                    if (boundaryOverlapEngagedA || boundaryOverlapEngagedNext)
                        ParsekLog.VerboseRateLimited(
                            "Reaim",
                            $"boundary-overlap-engaged.{phaseAnchorUT.ToString("R", lic)}.{spanStartUT.ToString("R", lic)}",
                            $"boundary-overlap engaged: looped re-aim launch realigns via a secondary in-SOI ghost " +
                            $"on the zero-slack loops (the seam now closes on every loop). cycleIndex={cycleIndex.ToString(lic)} " +
                            $"cadence={cycleDuration.ToString("R", lic)}s Tsid={tSid.ToString("R", lic)}s");
                    // ACTUAL-LAUNCH-INSTANT marker for the live secondary: name the real launch UT of the
                    // early-launching instance N+1 (L_{N+1} - advNext) so a playtest can compare the Missions
                    // warp-to target against where the secondary ghost actually lifts off. Rate-limited per
                    // mission identity (its own key, distinct from the per-loop-advance line's key).
                    if (hasSecondary)
                    {
                        double nominalLNext = phaseAnchorUT + secondaryCycleIndex * cycleDuration;
                        double actualLaunchUT = nominalLNext - advNext;
                        ParsekLog.VerboseRateLimited(
                            "Reaim",
                            $"launch-instant.{phaseAnchorUT.ToString("R", lic)}.{spanStartUT.ToString("R", lic)}",
                            $"launch instant: secondary instance N={secondaryCycleIndex.ToString(lic)} launches at " +
                            $"UT={actualLaunchUT.ToString("R", lic)} (nominal L_N={nominalLNext.ToString("R", lic)} " +
                            $"advance={advNext.ToString("R", lic)}s)");

                        // SEAM-RENDER OBSERVABILITY 1 (docs/dev/design-reaim-launch-hold-seam.md): the AUTHORITATIVE
                        // "the launch should be visible NOW" timestamp - the live UT at which the secondary first
                        // resolves its loopUT at/just past spanStart (its computed launch instant). The next playtest
                        // compares this against when the secondary's icon/conic and polyline ascent actually appear:
                        // a few-minutes lag between this currentUT and the map-presence first-create (observability 2)
                        // measures the pre-Segment gap exactly. secondaryLoopUT near spanStart == on the pad.
                        // Rate-limited per mission identity (its own key); logging-only, no control-flow effect.
                        ParsekLog.VerboseRateLimited(
                            "Reaim",
                            $"boundary-overlap-secondary-clock-launch.{phaseAnchorUT.ToString("R", lic)}.{spanStartUT.ToString("R", lic)}",
                            $"boundary-overlap secondary clock-launch: secondaryCycle={secondaryCycleIndex.ToString(lic)} " +
                            $"currentUT={currentUT.ToString("R", lic)} secondaryLoopUT={secondaryLoopUT.ToString("R", lic)} " +
                            $"spanStart={spanStartUT.ToString("R", lic)}");
                    }
                }
            }

            double holdPhasePos = hold > 0.0 ? CompressSpanUT(arrivalHoldAtUT, loiterCuts) - spanStartUT : double.NaN;
            double effectiveSpan = compressedSpan + effectiveLaunchAdvance + hold;

            // Epsilon-tolerant boundary, matching ComputeLoopPhaseFromUT: at exactly a cycle boundary
            // (phaseInCycle ~ 0 with cycleIndex > 0) the clock shows the PRIOR cycle's final frame (spanEnd),
            // not the next cycle's first frame, so "currentUT == spanEnd" reports spanEnd in cycle 0 when
            // cadence == span and the wrap to spanStart happens one sliver later (seamless, no pause). The
            // launch alignment SKIPS this rollback: under borrow-repay the boundary is already continuous (the
            // prior window's region-B early launch of THIS instance hands off to region A at phaseFromLaunch ==
            // launchAdvance with no flicker), and a rollback to spanEnd would wrongly show the prior instance's
            // landed frame instead of this instance's just-launched ascent. Only the non-aligned clock rolls back.
            if (launchAdvance <= 0.0 && cycleIndex > 0 && phaseInCycle <= LoopTiming.BoundaryEpsilon)
            {
                cycleIndex -= 1;
                loopUT = spanEndUT;
                // NOT the parked idle tail: the legitimate end-of-cycle final frame at the back-to-back wrap
                // boundary (cadence == span). The ghost is still mid-loop, just showing the prior cycle's last
                // frame, so the tail flag stays false.
                isInInterCycleTail = false;
                // This branch is reached only when launchAdvance <= 0 (not engaged), where hasSecondary is already
                // false; emit no secondary at a rollback.
                return new SpanLoopFrame(true, loopUT, cycleIndex, isInInterCycleTail, false, secondaryLoopUT, secondaryCycleIndex);
            }

            // Park at spanEnd once the working phase reaches the effective span. isInInterCycleTail is true
            // exactly in that parked tail (the phase ran past the span and we idle at spanEnd until the next
            // cycle / early launch). The two holds compose as two sequential insertions on one phase axis:
            // FIRST the SOI-exit repay (delta inserted at soiExitPhasePos, returning the post-SOI portion to
            // baseline), THEN the arrival hold (W_N inserted at holdPhasePos, which is measured in
            // recorded/compressed-span phase and is unchanged by the SOI-exit insertion that lies strictly
            // before it). Both reduce to the identity when their hold is 0 / their position is NaN, so a unit
            // with neither is byte-identical to the pre-hold clock.
            double clampedPhase = phaseFromLaunch >= effectiveSpan ? effectiveSpan : phaseFromLaunch;
            double afterSoi = ApplyArrivalHoldToPhase(clampedPhase, soiExitPhasePos, effectiveLaunchAdvance);
            double cutPhase = ApplyArrivalHoldToPhase(afterSoi, holdPhasePos, hold);
            loopUT = DecompressSpanUT(spanStartUT + cutPhase, loiterCuts);
            isInInterCycleTail = (phaseFromLaunch >= effectiveSpan);
            return new SpanLoopFrame(true, loopUT, cycleIndex, isInInterCycleTail, hasSecondary, secondaryLoopUT, secondaryCycleIndex);
        }

        /// <summary>
        /// Span-progress loopUT of the NEWEST instance of a SELF-OVERLAPPING mission. Under
        /// self-overlap the mission relaunches every <paramref name="overlapCadenceSeconds"/> (the
        /// cap-clamped true period, shorter than the span), so the newest instance is the one launched
        /// at <c>anchor + missionCycle * cadence</c> where
        /// <c>missionCycle = floor((currentUT - anchor) / cadence)</c>. Its progress through the
        /// span is the phase since that launch, clamped to [0, <paramref name="span"/>], and the
        /// returned <paramref name="loopUT"/> is <c>spanStartUT + phase</c>. Feeding this into
        /// <see cref="IsLoopUTInMemberWindow"/> picks the newest-instance live member for the watch
        /// camera, so the cross-member handoff follows the newest instance and never an older one.
        /// The overlap-instance cap bounds only how many OLDER instances stay alive (the engine
        /// applies it when enumerating active instances); it never clamps the newest cycle, so no
        /// cap is applied here. Returns <paramref name="loopUT"/> = spanStartUT, cycle 0 before the
        /// anchor or for a degenerate span. Pure: no logging.
        /// </summary>
        internal static void ComputeNewestMissionInstanceSpanLoopUT(
            double phaseAnchorUT, double spanStartUT, double span,
            double overlapCadenceSeconds, double currentUT,
            out double loopUT, out long missionCycle)
        {
            loopUT = spanStartUT;
            missionCycle = 0;

            if (span <= 0 || currentUT < phaseAnchorUT)
                return;

            double cadence = Math.Max(overlapCadenceSeconds, LoopTiming.MinCycleDuration);
            double elapsed = currentUT - phaseAnchorUT;
            missionCycle = (long)Math.Floor(elapsed / cadence);
            if (missionCycle < 0)
                missionCycle = 0;

            double phase = elapsed - (missionCycle * cadence);
            if (phase < 0) phase = 0;
            if (phase > span) phase = span;
            loopUT = spanStartUT + phase;
        }

        /// <summary>
        /// The span-progress loopUT for a SPECIFIC mission overlap instance (cycle), used to drive
        /// the watch camera's stage-handoff off the instance the player is FOLLOWING instead of the
        /// newest one (so watching an overlapping mission tracks one launch all the way through,
        /// not jumping to each new launch). Returns false when that instance has not launched yet
        /// (<paramref name="currentUT"/> before its start) or has already ENDED (phase past the
        /// span) - the caller then falls back to the newest instance (snap to the newest in-flight
        /// launch on completion). Pure.
        /// </summary>
        internal static bool TryComputeMissionInstanceSpanLoopUT(
            double phaseAnchorUT, double spanStartUT, double span,
            double overlapCadenceSeconds, double currentUT, long cycle, out double loopUT)
        {
            loopUT = spanStartUT;
            if (span <= 0 || cycle < 0)
                return false;
            double cadence = Math.Max(overlapCadenceSeconds, LoopTiming.MinCycleDuration);
            double phase = currentUT - (phaseAnchorUT + cycle * cadence);
            if (phase < 0 || phase > span)
                return false; // not launched yet, or already ended -> caller uses the newest instance
            loopUT = spanStartUT + phase;
            return true;
        }

        /// <summary>
        /// True if <paramref name="loopUT"/> falls inside the member window
        /// [<paramref name="memberStartUT"/>, <paramref name="memberEndUT"/>] (epsilon-tolerant).
        /// Pure: a member renders iff the shared mission clock is inside its own window. Uses
        /// <see cref="LoopTiming.BoundaryEpsilon"/> so boundary handling agrees with the rest of
        /// the loop math.
        /// </summary>
        internal static bool IsLoopUTInMemberWindow(
            double loopUT, double memberStartUT, double memberEndUT)
        {
            return loopUT >= memberStartUT - LoopTiming.BoundaryEpsilon
                && loopUT <= memberEndUT + LoopTiming.BoundaryEpsilon;
        }

        /// <summary>The render outcome for one member of a chain-loop unit on a given frame.</summary>
        internal enum UnitMemberRenderDecision
        {
            /// <summary>The span clock could not resolve (before span start or degenerate span).</summary>
            SpanClockUnresolved,
            /// <summary>The shared mission clock is in this member's own window — render it at <c>SpanLoopUT</c>.</summary>
            Render,
            /// <summary>The shared clock is in the inter-cycle tail-wait (cadence &gt; span) — hide ALL members.</summary>
            HiddenInterCycleTail,
            /// <summary>The shared clock is outside this member's own window — hide this member.</summary>
            HiddenOutsideWindow,
        }

        /// <summary>
        /// Pure per-member render decision for the engine's follower dispatch under the shared
        /// mission clock model. There is NO cross-member selection: each member renders
        /// independently based ONLY on whether the shared clock is in its own
        /// [<paramref name="memberStartUT"/>, <paramref name="memberEndUT"/>] window. Multiple
        /// members render concurrently (debris alongside their parent), exactly like a rewind.
        ///
        /// Computes the shared <c>spanLoopUT</c> via <see cref="TryComputeSpanLoopUT"/>:
        /// - span clock unresolved (before span start / degenerate span) -> SpanClockUnresolved.
        /// - inter-cycle tail (cadence &gt; span, the "wait" between cycles) -> HiddenInterCycleTail
        ///   (the caller hides ALL members - render nothing during the wait).
        /// - else: Render if spanLoopUT is in THIS member's own window, HiddenOutsideWindow otherwise.
        ///
        /// This is the testable seam for <c>GhostPlaybackEngine.UpdateUnitMemberPlayback</c> - the
        /// GameObject activation itself is verified in-game. Pure: no logging.
        /// </summary>
        internal static UnitMemberRenderDecision DecideUnitMemberRender(
            double currentUT,
            double phaseAnchorUT,
            double spanStartUT,
            double spanEndUT,
            double cadenceSeconds,
            double memberStartUT,
            double memberEndUT,
            out double spanLoopUT,
            out long unitCycle,
            out bool isInInterCycleTail,
            MissionRelaunchSchedule schedule = null,
            IReadOnlyList<LoopCut> loiterCuts = null,
            double arrivalHoldSeconds = 0.0,
            double arrivalHoldAtUT = double.NaN,
            double arrivalHoldAlignPeriod = double.NaN,
            double launchBodyRotationPeriod = double.NaN,
            bool launchHoldEngaged = false,
            double soiExitAtUT = double.NaN)
        {
            spanLoopUT = spanStartUT;
            unitCycle = 0;
            isInInterCycleTail = false;

            if (!TryComputeSpanLoopUT(
                    currentUT, phaseAnchorUT, spanStartUT, spanEndUT, cadenceSeconds, out spanLoopUT,
                    out unitCycle, out isInInterCycleTail, schedule, loiterCuts,
                    arrivalHoldSeconds, arrivalHoldAtUT, arrivalHoldAlignPeriod,
                    launchBodyRotationPeriod, launchHoldEngaged, soiExitAtUT))
                return UnitMemberRenderDecision.SpanClockUnresolved;

            // Inter-cycle tail (the "wait" between cycles when cadence > span): render nothing. Under the
            // borrow-at-launch / repay-at-SOI-exit launch alignment there is NO pre-launch absence: the launch
            // is EARLIER (at L_N - delta_N) and the delta_N repay is a COAST hold at the SOI-exit boundary
            // (rendered in-window), so the resolved spanLoopUT is always >= spanStartUT and the ghost is never
            // absent on the pad.
            if (isInInterCycleTail)
                return UnitMemberRenderDecision.HiddenInterCycleTail;

            // Each member renders independently iff the shared clock is in its own window.
            return IsLoopUTInMemberWindow(spanLoopUT, memberStartUT, memberEndUT)
                ? UnitMemberRenderDecision.Render
                : UnitMemberRenderDecision.HiddenOutsideWindow;
        }

        /// <summary>
        /// Tracking-Station per-recording effective sample UT under the shared mission clock.
        /// The TS scene has no playback engine: it positions ProtoVessel ghosts (orbit lines +
        /// icons) and OnGUI atmospheric markers at an explicit UT. This is the single seam that
        /// substitutes the span-clock loopUT for that explicit UT so a looped Mission renders in
        /// the tracking station identically to flight / KSC.
        ///
        /// - <paramref name="units"/> null or committed index <paramref name="i"/> is NOT a unit
        ///   member -> return <paramref name="liveUT"/>, <paramref name="renderHidden"/>=false
        ///   (unchanged behavior; this is the common case until a Mission loops).
        /// - i IS a member -> resolve the owning unit's shared clock via
        ///   <see cref="DecideUnitMemberRender"/>: <c>Render</c> returns the span-clock loopUT with
        ///   renderHidden=false; ANY other decision (SpanClockUnresolved / HiddenInterCycleTail /
        ///   HiddenOutsideWindow) returns liveUT with renderHidden=true so the caller skips creation
        ///   / tears down the ghost / skips the marker for this frame.
        ///
        /// Pure: no logging (per-frame callers own rate-limiting). Inert when <paramref name="units"/>
        /// is <see cref="LoopUnitSet.Empty"/>: returns liveUT / renderHidden=false for every index.
        /// </summary>
        internal static double ResolveTrackingStationSampleUT(
            int i, double memberStartUT, double memberEndUT, double liveUT,
            LoopUnitSet units, out bool renderHidden)
        {
            renderHidden = false;
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return liveUT;

            // Interval-level start/end trim: clamp to this member's trimmed render window (falls
            // back to the passed recording bounds when untrimmed), so the tracking-station icon
            // shows the same trimmed segment the other scenes render.
            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT,
                unit.PhaseAnchorUT,
                unit.SpanStartUT,
                unit.SpanEndUT,
                unit.CadenceSeconds,
                memberStartUT,
                memberEndUT,
                out double loopUT,
                out long unitCycle,
                out _,
                unit.RelaunchSchedule,
                unit.LoiterCuts,
                unit.ArrivalHoldSeconds,
                unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds,
                unit.LaunchBodyRotationPeriodSeconds,
                unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);

            // Descent trigger (re-aim looped arrival, docs/dev/plans/reaim-descent-trigger.md): the post-parking
            // body-fixed approach clip (deorbit->reentry->landing for a surface mission, OR the rendezvous/docking
            // approach for an orbital one) lives in a SET of chain-tail members AFTER the destination arrival.
            // Those members must NOT play on the raw loop clock - the re-aimed transfer arrives ~|captureShift|
            // early so the loop slot for the clip lands at the WRONG destination rotation phase (the icon-frozen /
            // mis-aligned bug). DETACH the whole set from the loop clock and re-anchor it to the first
            // rotation-aligned moment after the icon reaches the parking-orbit deorbit point. ONE shared, monotone
            // descent head spans the set; each member renders ONLY the slice of the clip inside its own window and
            // is HIDDEN in every other phase (Inert before the icon reaches the deorbit point, Loiter while the
            // icon circles the transfer member's shifted conic, Done after the clip ends). Byte-identical for every
            // non-descent member / non-re-aim unit (HasDescentTrigger false).
            if (unit.HasDescentTrigger && unit.IsDescentMember(i))
            {
                if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                {
                    renderHidden = true; // the loop has not started this frame
                    return liveUT;
                }

                bool renderDescent = Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(
                    liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                    unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                    unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                    memberStartUT, memberEndUT, out double descentHead,
                    out Parsek.Reaim.DescentTrigger.DescentHeadPhase descentPhase);

                var dic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited(
                    "ReaimDescent",
                    // Mission identity + member + phase key: bounded by mission x member x phase, not frame count.
                    $"descent-head.{unit.PhaseAnchorUT.ToString("R", dic)}.{unit.SpanStartUT.ToString("R", dic)}.{i.ToString(dic)}.{descentPhase}",
                    $"descent trigger member={i.ToString(dic)} cycle={unitCycle.ToString(dic)} phase={descentPhase} " +
                    $"render={renderDescent} liveUT={liveUT.ToString("R", dic)} head={descentHead.ToString("R", dic)} " +
                    $"win=[{memberStartUT.ToString("R", dic)},{memberEndUT.ToString("R", dic)}] " +
                    $"deorbit={unit.RecordedDeorbitUT.ToString("R", dic)} end={unit.DescentEndUT.ToString("R", dic)} " +
                    $"cs={unit.CaptureShiftSeconds.ToString("R", dic)} Trot={unit.DestinationBodyRotationPeriodSeconds.ToString("R", dic)} Tpark={unit.LoiterPeriodSeconds.ToString("R", dic)}");

                // Always-on lifecycle trace (bounded per cycle): states whether the descent RENDERED, was SKIPPED
                // (warp stepped over its window), and the window bounds - the decision-side evidence the truth-side
                // MapRenderProbe cannot give (the descent member is never created until it actually renders).
                Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                    unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT, unit.RecordedDeorbitUT,
                    unit.DestinationBodyRotationPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                    out _, out double dscEntryUT, out double dscTriggerUT);
                Parsek.Reaim.DescentRenderTrace.Note(
                    $"{unit.PhaseAnchorUT.ToString("R", dic)}.{unit.SpanStartUT.ToString("R", dic)}",
                    i, unitCycle, descentPhase, liveUT, dscEntryUT, dscTriggerUT,
                    unit.RecordedDeorbitUT, unit.DescentEndUT, unit.CadenceSeconds,
                    unit.DestinationBodyRotationPeriodSeconds);

                // DEBUG AID (MapRenderWarpControl): register this descent's render window so the general debug warp
                // control can decelerate into it when an agent is debugging the descent render. The window end is the
                // LIVE-frame conversion of the recorded clip duration (triggerUT + (descentEndUT - recordedDeorbitUT));
                // RecordedDeorbitUT/DescentEndUT are RECORDED-frame (~2.5e9) while triggerUT/liveUT are LIVE (~3.9e9),
                // so the raw recorded end would put the window end far below any live UT (the 2026-06-20
                // dead-warp-control bug). That recorded->live conversion is recording-schema knowledge and stays here
                // on the descent side; the warp control takes only a plain live-frame window. Registration is cheap
                // and unconditional (idempotent upsert keyed by the stable mission label, re-registered every frame);
                // the warp is only ever changed inside MapRenderWarpControl.Tick, which no-ops unless BOTH the
                // DebugWarpEnabled code flag and the map-render tracer are on (both default off). Reached in BOTH the
                // tracking station and FLIGHT (the polyline Driver walks this resolver every frame in flight), so one
                // call site covers both scenes.
                double dscWindowEndLiveUT = Parsek.Reaim.DescentTrigger.DescentWindowEndLiveUT(
                    dscTriggerUT, unit.RecordedDeorbitUT, unit.DescentEndUT);
                MapRenderWarpControl.RegisterWatchWindow(
                    dscTriggerUT, dscWindowEndLiveUT,
                    $"descent.{unit.PhaseAnchorUT.ToString("R", dic)}.{unit.SpanStartUT.ToString("R", dic)}");

                if (renderDescent)
                {
                    renderHidden = false; // this member owns its slice of the re-anchored clip
                    return descentHead;
                }
                // Inert / Loiter / Done / outside-this-member's-slice: hidden (the descent member NEVER rides the
                // raw loop clock; the transfer member carries the icon over the shifted conic during the wait).
                renderHidden = true;
                return liveUT;
            }

            // NON-DESCENT (transfer / loiter / launch) member of a descent-trigger unit. Once the shared descent is
            // PLAYING (unit descent phase == Descent), the vessel has LEFT the parking, so this member's
            // loiter/parking icon must HAND OFF to the descent member. Without this the transfer member keeps drawing
            // its parking proto-icon on the raw loop clock WHILE the descent member draws the descent clip — two icons
            // at once — and because the transfer member's is the prominent native proto-icon, the user tracks IT and
            // reports "the icon stayed on the loiter" even though the descent did render (confirmed: 2026-06-20 13:34
            // log — rec=44 `Marker DRAWN headUT=2567696156` on the parking while member=45 `phase=Descent`). Hide any
            // CURRENTLY-RENDERING non-descent member for the descent window so the descent icon is the only one; in
            // Inert / Loiter / Done it is left untouched (the loiter icon is correct until the trigger fires, and the
            // descent member is the one hidden then). Scoped to descent-trigger units (HasDescentTrigger) and to the
            // member that would otherwise render, so it is byte-identical everywhere else.
            if (unit.HasDescentTrigger && decision == UnitMemberRenderDecision.Render)
            {
                Parsek.Reaim.DescentTrigger.DescentHeadPhase transferUnitPhase =
                    Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead(
                        liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                        unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                        unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out _);
                bool hideForDescentHandoff =
                    transferUnitPhase == Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent;
                var hdic = CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited(
                    "ReaimDescent",
                    // Bounded by mission x member x phase x action (NOT frame count).
                    $"transfer-handoff.{unit.PhaseAnchorUT.ToString("R", hdic)}.{unit.SpanStartUT.ToString("R", hdic)}.{i.ToString(hdic)}.{transferUnitPhase}.{hideForDescentHandoff}",
                    $"transfer/loiter member={i.ToString(hdic)} cycle={unitCycle.ToString(hdic)} " +
                    $"unitDescentPhase={transferUnitPhase} baseDecision=Render liveUT={liveUT.ToString("R", hdic)} " +
                    $"loopUT={loopUT.ToString("R", hdic)} -> {(hideForDescentHandoff ? "HIDDEN (descent playing; hand off to descent member, no double icon)" : "kept (loiter/normal icon)")}");
                if (hideForDescentHandoff)
                {
                    renderHidden = true;
                    return liveUT;
                }

                // C1 (icon rides the deorbit arc): during the LAST parking-orbit-period of the Loiter (the
                // deorbit transition), the transfer member's icon RIDES the re-anchored I1 deorbit head so it
                // descends the recorded deorbit tail from the parking orbit down to the seam (atmo entry),
                // reaching it exactly at the trigger, where the atmospheric descent set takes over (continuous).
                // Without this the icon circles the parking conic to the deorbit point then JUMPS straight to atmo
                // entry, skipping the orbit->entry deorbit - leaving the deorbit-arc LINE drawn with NO icon on it
                // (the captured "no icon on the descent line" bug, log 2026-06-23_0005: rec 44's deorbit-arc leg
                // 10 drew every loiter frame while its marker rode the far-away loiter head at ~-18 Gm).
                //
                // CHECKED BEFORE the loiter-gap clamp (this ORDER is the fix): the clamp's gate
                // IsDescentTransferMemberInLoiterGap is loopUT > ParkingConicEndUT, which is TRUE for the ENTIRE
                // loiter, so when the clamp was checked first it returned every frame and C1 was DEAD (never
                // reached - 0 firings in the log). C1 is strictly self-gated (transfer member + Loiter phase +
                // liveUT >= triggerUT - LoiterPeriodSeconds, i.e. only the last parking period) and returns false
                // everywhere else, so the loiter-gap clamp below still owns the rest of the loiter unchanged; C1
                // only takes over in its own narrow deorbit window. Its head is in the UNSHIFTED recorded frame
                // (recordedDeorbitUT + (liveUT - triggerUT)), matching the drawn deorbit-tail leg's recorded UT,
                // so the marker actually anchors to the arc (vs the clamp's captureShift-SHIFTED parking-circle
                // head, which falls outside every unshifted leg -> ride=fallback-head-outside-legs -> the -18 Gm
                // body-fixed head). Byte-identical for the owner / ride-alongs / descent members / non-re-aim
                // units (TryResolveTransferDeorbitIconHead returns false for everything but the destination
                // transfer member). NOTE: needs the deorbit-tail LINE to draw under the icon during this window
                // (the renderer's I1 head-gated sweep, confirmed present in the log).
                if (TryResolveTransferDeorbitIconHead(
                        unit, i, liveUT, memberStartUT, memberEndUT, out double deorbitIconHead))
                {
                    renderHidden = false;
                    return deorbitIconHead;
                }

                // Loiter-gap HEAD clamp (the rogue-descent fix): once the recorded loop clock sweeps PAST the
                // parking-conic end, the raw loopUT return below would put this shared marker / line / polyline
                // head in the recorded deorbit -> descent region - but the LIVE descent has NOT triggered (the unit
                // phase is Inert / Loiter, the trigger is ~|captureShift| later), so the ghost renders a descending
                // icon at the wrong time (the user's "rogue descending trajectory, only icon, no line"). Hold the
                // head at the parking-conic end so every surface using this resolver stays on the parking conic
                // through the loiter, matching the map-presence segment-lookup clamp + the orbit-line hold. Gated on
                // the destination transfer member (IsDescentTransferMemberInLoiterGap checks i == TransferMemberIndex
                // && loopUT > ParkingConicEndUT). The C1 deorbit ride above already returned for the last parking
                // period (the deorbit transition), so this clamp owns the loiter EXCEPT that final window.
                // Byte-identical-off for the owner / ride-alongs / descent set / non-re-aim units. (Step 2:
                // ClampTransferMemberHeadToLoiterGap WRAPS the head into the last recorded parking period
                // [P - Tpark, P) so the icon CIRCLES the closed parking conic through the wait instead of freezing
                // at the deorbit point - continuous at engage and at wrap-around. One wrap formula shared with the
                // FLIGHT engine drive-clock site.)
                if (IsDescentTransferMemberInLoiterGap(unit, i, loopUT))
                {
                    renderHidden = false;
                    return ClampTransferMemberHeadToLoiterGap(unit, i, loopUT);
                }
            }

            if (decision == UnitMemberRenderDecision.Render)
                return loopUT;

            // Any non-Render decision (SpanClockUnresolved / HiddenInterCycleTail / HiddenOutsideWindow) hides
            // the ghost for this frame, suppressing the icon + line + marker on every map/TS surface
            // automatically.
            renderHidden = true;
            return liveUT;
        }

        /// <summary>
        /// The FLIGHT-engine descent-member render outcome (the engine-side complement of the
        /// <see cref="ResolveTrackingStationSampleUT"/> descent branch). The map/TS resolver substitutes a
        /// single sample UT; the engine instead positions a live ghost, so it needs the head plus an explicit
        /// render flag and the phase (for logging / the lifecycle trace) rather than a UT + renderHidden pair.
        /// </summary>
        internal readonly struct DescentMemberEngineRender
        {
            internal DescentMemberEngineRender(
                bool render, double head, Parsek.Reaim.DescentTrigger.DescentHeadPhase phase, long cycleIndex)
            {
                Render = render;
                Head = head;
                Phase = phase;
                CycleIndex = cycleIndex;
            }

            /// <summary>True iff this member should render its ghost THIS frame at <see cref="Head"/> (the
            /// Descent phase, and the shared head falls in this member's window). False -> the member is
            /// HIDDEN (Inert / Loiter / Done / out-of-this-member's-slice); it must NEVER drive its ghost on
            /// the raw loop clock (that is the wrong-rotation bug).</summary>
            internal bool Render { get; }

            /// <summary>The re-anchored descent head UT to position the ghost at when <see cref="Render"/> is
            /// true; NaN otherwise.</summary>
            internal double Head { get; }

            /// <summary>The descent head phase this frame (Inert / Loiter / Descent / Done) for logging + the
            /// always-on lifecycle trace.</summary>
            internal Parsek.Reaim.DescentTrigger.DescentHeadPhase Phase { get; }

            /// <summary>The loop cycle index the descent timing was computed for (passed through for the
            /// lifecycle trace so the engine and the resolver agree on the cycle).</summary>
            internal long CycleIndex { get; }
        }

        /// <summary>
        /// FLIGHT-engine per-frame descent-member render decision (re-aim looped arrival,
        /// docs/dev/plans/reaim-descent-trigger.md). Mirrors the <see cref="ResolveTrackingStationSampleUT"/>
        /// descent branch EXACTLY so the flight engine renders a descent member identically to the map / TS:
        /// detach it from the raw loop clock and re-anchor the whole descent set to the first rotation-aligned
        /// moment after the icon reaches the parking-orbit deorbit point. The caller has already resolved the
        /// shared span clock (<paramref name="unitCycle"/>) and trimmed the member window; this only runs the
        /// descent remap via <see cref="Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead"/> and reports
        /// whether THIS member renders (and at what re-anchored head). A descent member that does not render is
        /// HIDDEN - it never drives its ghost on the raw loop clock. The caller is responsible for the
        /// SpanClockUnresolved early-out BEFORE calling this (the engine tears the ghost down there already).
        /// Pure: no Unity, no logging (the engine owns the rate-limited log + lifecycle trace at the call site,
        /// exactly like the resolver). Returns the head phase too so the caller can drive the same
        /// <see cref="Parsek.Reaim.DescentRenderTrace"/> lifecycle line.
        /// </summary>
        internal static DescentMemberEngineRender ResolveDescentMemberEngineRender(
            LoopUnit unit, int i, double liveUT, long unitCycle,
            double memberStartUT, double memberEndUT)
        {
            bool renderDescent = Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(
                liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                memberStartUT, memberEndUT, out double descentHead,
                out Parsek.Reaim.DescentTrigger.DescentHeadPhase descentPhase);

            return new DescentMemberEngineRender(
                renderDescent, renderDescent ? descentHead : double.NaN, descentPhase, unitCycle);
        }

        /// <summary>
        /// I1 (re-aim descent "renders disconnected from the loiter"): the re-anchored DEORBIT-tail head for the
        /// DESTINATION transfer member (<see cref="LoopUnit.TransferMemberIndex"/>) of a descent-trigger unit, so
        /// the transfer member's contiguous deorbit /
        /// approach legs (the Duna legs ending AT the seam) draw swept down to the seam during the LOITER phase
        /// and join the descent member's first head at the trigger - rendering continuous loiter -&gt; deorbit -&gt;
        /// entry -&gt; surface. Resolves the unit cycle through the SAME <see cref="DecideUnitMemberRender"/> path
        /// the resolver / engine use (no parallel cycle formula - one source of truth), then delegates to the pure
        /// <see cref="Parsek.Reaim.DescentTrigger.TryComputeTransferDeorbitHead"/>, which returns a head ONLY
        /// during Loiter. Also returns the shifted-parking-conic end <paramref name="conicEndUT"/> and the seam
        /// <paramref name="seamUT"/> so the caller's per-leg deorbit-tail predicate (a pure-UT window
        /// <c>conicEndUT &lt; legEndUT &lt;= seamUT + eps</c>) can select only the contiguous post-shifted-conic
        /// destination tail. Returns false (byte-identical-off) for a null unit, any member that is NOT the
        /// destination transfer member (the owner, every ride-along in a different/unshifted frame, and every
        /// descent-set member), a unit with no descent trigger, an unresolved span clock, or any phase other than
        /// Loiter. Pure: no logging (the caller owns rate-limiting).
        /// </summary>
        internal static bool TryResolveTransferDeorbitHeadForMember(
            LoopUnit unit, int i, double liveUT, double memberStartUT, double memberEndUT,
            out double deorbitHead, out double conicEndUT, out double seamUT)
        {
            deorbitHead = double.NaN;
            conicEndUT = double.NaN;
            seamUT = double.NaN;

            // ONLY the destination transfer member (= TransferMemberIndex, the member whose shifted parking conic
            // and recorded deorbit tail exist) of a descent-trigger unit carries the deorbit tail. Gating on the
            // exact index (NOT "every non-descent member") excludes the owner AND every ride-along in a
            // DIFFERENT/unshifted frame (e.g. a launch-body-orbit probe) - they have no shifted deorbit tail and
            // must not draw one. Mirrors IsDescentTransferMemberInLoiterGap / IsTransferMemberDescentContinuation.
            // Byte-identical-off for non-re-aim units (HasDescentTrigger false) and the default
            // TransferMemberIndex == -1. The caller resolves the unit via TryGetUnitForMember, so i is a real member.
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            // Resolve the unit cycle via the production decision path (cycle is window-independent, so the member
            // window only governs the in-window Render decision - irrelevant here, we just need unitCycle).
            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false; // the loop has not started this frame

            seamUT = unit.RecordedDeorbitUT;
            conicEndUT = unit.RecordedDeorbitUT + unit.CaptureShiftSeconds;

            return Parsek.Reaim.DescentTrigger.TryComputeTransferDeorbitHead(
                liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out deorbitHead);
        }

        /// <summary>
        /// C1 (icon rides the deorbit arc, FIRST ITERATION): the transfer member's ICON head during the deorbit
        /// transition — the LAST parking-orbit-period of the Loiter phase. Returns the re-anchored I1 deorbit
        /// head (<see cref="Parsek.Reaim.DescentTrigger.TryComputeTransferDeorbitHead"/>, the same head that
        /// sweeps the deorbit-tail LINE) so the transfer member's icon descends the recorded deorbit tail from
        /// the parking orbit down to the seam (atmo entry), reaching it at the trigger where the atmospheric
        /// descent set takes over. Without it the icon circles the parking conic and then jumps straight to atmo
        /// entry, skipping the orbit→entry deorbit (the user's C1). The deorbit window is a HEURISTIC first cut:
        /// the last <c>LoiterPeriodSeconds</c> (one parking orbit) before <c>triggerUT</c>; tune in-game. Returns
        /// false (byte-identical) for a non-descent-trigger unit, any member that is NOT the destination transfer
        /// member (<see cref="LoopUnit.TransferMemberIndex"/>; the owner, every ride-along, and every descent-set
        /// member), a non-Loiter phase, a frame before the deorbit window, an unresolved span clock, or a
        /// degenerate trigger / loiter period. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static bool TryResolveTransferDeorbitIconHead(
            LoopUnit unit, int i, double liveUT, double memberStartUT, double memberEndUT, out double iconHead)
        {
            iconHead = double.NaN;
            // ONLY the destination transfer member (= TransferMemberIndex) rides the deorbit tail; the owner and
            // every ride-along in a different/unshifted frame are excluded (same gate as the I1 head + loiter-gap
            // clamp). Byte-identical-off for non-re-aim units and TransferMemberIndex == -1.
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false;

            Parsek.Reaim.DescentTrigger.DescentHeadPhase phase =
                Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead(
                    liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                    unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                    unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out _);
            if (phase != Parsek.Reaim.DescentTrigger.DescentHeadPhase.Loiter)
                return false; // the icon only descends the deorbit tail during the loiter's run-up to the trigger

            // Engage C1 only once the re-anchored deorbit head reaches the RENDERER's deorbit-arc leg start
            // (FirstDeorbitLegStartUT, UNSHIFTED). The I1 deorbit head during Loiter is
            // deorbitHead = RecordedDeorbitUT + (liveUT - triggerUT) (DescentTrigger.TryComputeTransferDeorbitHead).
            // The renderer draws / the marker rides the first deorbit-tail leg when deorbitHead >= leg.startUT
            // (ShouldDrawLegAtHeadUT), i.e. liveUT >= triggerUT + (FirstDeorbitLegStartUT - RecordedDeorbitUT).
            // Before that the resolver falls through to the loiter-gap clamp (the parking conic renders
            // normally, no proto retire); at this UT the deorbit line draws the SAME frame and TracedPath
            // takes over => continuous handoff, no loiter-orbit gap, no double-draw. FirstDeorbitLegStartUT
            // <= seam = RecordedDeorbitUT, so the offset is <= 0 and C1 still engages DURING Loiter
            // (liveUT < triggerUT). NaN/degenerate -> the legacy triggerUT - LoiterPeriodSeconds heuristic
            // (byte-identical-off). FirstDeorbitLegStartUT is computed in the builder by calling the SAME leg
            // builder the Driver calls, so this engage UT coincides with the leg-draw frame to the UT.
            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT, unit.RecordedDeorbitUT,
                unit.DestinationBodyRotationPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                out _, out _, out double triggerUT);
            if (double.IsNaN(triggerUT))
                return false;

            double iconEngageUT;
            if (!double.IsNaN(unit.FirstDeorbitLegStartUT)
                && !double.IsInfinity(unit.FirstDeorbitLegStartUT)
                && !double.IsNaN(unit.RecordedDeorbitUT))
            {
                // deorbitHead >= leg.startUT  <=>  liveUT >= triggerUT + (legStart - seam)
                iconEngageUT = triggerUT + (unit.FirstDeorbitLegStartUT - unit.RecordedDeorbitUT);
            }
            else if (!double.IsNaN(unit.LoiterPeriodSeconds) && unit.LoiterPeriodSeconds > 0.0)
            {
                iconEngageUT = triggerUT - unit.LoiterPeriodSeconds; // legacy heuristic fallback
            }
            else
            {
                return false; // degenerate loiter period AND no leg start -> identical to today's return
            }
            if (liveUT < iconEngageUT)
                return false; // not yet at the deorbit-arc leg start — keep circling the parking conic

            return Parsek.Reaim.DescentTrigger.TryComputeTransferDeorbitHead(
                liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out iconHead);
        }

        /// <summary>
        /// COSMETIC fix (re-aim descent "post-landing suborbital looping ghost"): true iff committed index
        /// <paramref name="i"/> is the DESTINATION transfer member (<see cref="LoopUnit.TransferMemberIndex"/>,
        /// the member whose shifted destination parking conic exists) of a descent-trigger unit AND the
        /// shared descent has HANDED OFF or LANDED this frame (descent phase == Descent or Done). In those
        /// phases the transfer member's recorded journey is over - it carried the icon to the shifted parking
        /// deorbit point and handed off to the descent set - so its map / tracking-station orbit ghost must be
        /// RETIRED cleanly (the create resolver returns None) instead of synthesizing a spurious EndpointTail
        /// coast from the recorded deorbit endpoint (which, having no covering segment past the parking conic,
        /// is a sub-surface ellipse drawn as a closed loop). The descent set owns the visual during Descent (the
        /// re-anchored clip) and the landing is terminal during Done (a looped mission has no persistent
        /// landed-vessel ghost; it cycles back to the next launch), so a clean retire is the CORRECT outcome -
        /// the sub-surface tail was the bug.
        ///
        /// <para>Gates on the EXACT transfer-member index, NOT "any non-descent member" (the bug the loiter-gap
        /// clamp already fixed): a re-aim looped landing unit can carry non-descent RIDE-ALONG members in a
        /// DIFFERENT/unshifted frame (e.g. a launch-body-orbit probe) that never arrived at the destination and
        /// have no shifted parking conic to leak a sub-surface tail. Their loop clock never advances through the
        /// shifted deorbit point, so they must keep rendering - retiring them on the unit-level Descent/Done phase
        /// (which is member-agnostic) would wrongly blank their orbit ghost.</para>
        ///
        /// <para>Returns FALSE for Inert and Loiter (the icon is still riding the shifted PARKING conic - that
        /// conic MUST keep rendering, so this returns false and the resolver's normal covering-segment branch is
        /// untouched), for any member that is NOT the destination transfer member (the owner / tree root, every
        /// ride-along in a different/unshifted frame, and every descent-SET member - its own trigger-gated render
        /// owns it), for a unit with no descent trigger, for an unresolved span clock, and for any degenerate
        /// input. Resolves the unit cycle
        /// through the SAME <see cref="DecideUnitMemberRender"/> path the resolver / engine / I1 use (one source
        /// of truth - the live-frame phase, NOT a raw recorded-domain UT inequality), then classifies via the
        /// pure <see cref="Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead"/>. Mirrors the handoff at
        /// <see cref="ResolveTrackingStationSampleUT"/> (which hides the transfer marker in Descent) but ALSO
        /// covers Done, where the handoff does not fire and the spurious tail appears. Pure; no Unity
        /// (xUnit-testable).</para>
        /// </summary>
        internal static bool IsTransferMemberDescentContinuation(
            LoopUnit unit, int i, double liveUT, double memberStartUT, double memberEndUT)
        {
            // ONLY the DESTINATION transfer member (the one whose recorded journey ends at the shifted parking
            // deorbit point with no covering segment past it = TransferMemberIndex) of a descent-trigger unit can
            // carry the continuation. Gating on the exact index (NOT "every non-descent member") excludes the
            // owner / tree root AND every ride-along (e.g. a launch-body-orbit probe whose loop clock runs in a
            // DIFFERENT, unshifted frame and NEVER arrived at the destination, so its own orbit ghost must keep
            // rendering); the descent-set members are also excluded (none equals TransferMemberIndex). Mirrors the
            // loiter-gap clamp narrowing (IsDescentTransferMemberInLoiterGap) - the same TransferMemberIndex gate -
            // because both retire only the member with the shifted destination parking conic. Byte-identical-off
            // for every non-re-aim unit (HasDescentTrigger false) and every unit with the default
            // TransferMemberIndex == -1 (matches no real committed index).
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            // Resolve the unit cycle via the production decision path (cycle is window-independent; the member
            // window only governs the in-window Render decision - irrelevant here, we just need unitCycle).
            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false; // the loop has not started this frame

            Parsek.Reaim.DescentTrigger.DescentHeadPhase phase =
                Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead(
                    liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                    unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                    unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out _);

            // After the seam (handed off / landed) -> retire. Inert/Loiter -> false -> the shifted parking
            // (loiter) conic keeps rendering through the resolver's unchanged covering-segment branch.
            return phase == Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent
                || phase == Parsek.Reaim.DescentTrigger.DescentHeadPhase.Done;
        }

        /// <summary>
        /// COSMETIC fix convenience wrapper: resolves the owning unit from <paramref name="units"/> via
        /// <see cref="LoopUnitSet.TryGetUnitForMember"/> and delegates to
        /// <see cref="IsTransferMemberDescentContinuation(LoopUnit,int,double,double,double)"/>. Returns false
        /// for a null set or a non-member index (byte-identical-off). Lets the GhostMapPresence create callers
        /// compute the flag in one line.
        /// </summary>
        internal static bool IsTransferMemberDescentContinuation(
            LoopUnitSet units, int i, double liveUT, double memberStartUT, double memberEndUT)
        {
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return false;
            return IsTransferMemberDescentContinuation(unit, i, liveUT, memberStartUT, memberEndUT);
        }

        /// <summary>
        /// LOITER-GAP map-presence UT clamp (the destination-loiter "parking conic stops rendering" bug): true
        /// IFF committed index <paramref name="i"/> is the DESTINATION transfer member
        /// (<see cref="LoopUnit.TransferMemberIndex"/>, the member whose shifted parking conic exists) of a re-aim
        /// descent-trigger unit AND its loop clock <paramref name="loopUT"/> (the loop-shifted sample UT the
        /// map-presence resolver uses for the OrbitSegment lookup against the re-aimed SHIFTED effective segments,
        /// NOT the live UT) has advanced PAST the SHIFTED PARKING-conic end <see cref="LoopUnit.ParkingConicEndUT"/>
        /// (the destination loiter run's recorded end + captureShift = the deorbit point = the start of the first
        /// deorbit-transition OrbitSegment, in the re-aimed display frame). A ride-along member in a
        /// DIFFERENT/unshifted frame (e.g. a launch-body-orbit probe) is excluded by the exact-index gate.
        ///
        /// <para>Geometry: PR #1177 shifted the parking conic EARLIER by <c>|CaptureShiftSeconds|</c> (~33 days)
        /// so it meets the early-arriving re-aimed transfer; the body-fixed deorbit-&gt;reentry-&gt;landing clip
        /// was NOT shifted (pinned at recorded <c>RecordedDeorbitUT</c>). The transfer member's destination data
        /// is the PARKING conic followed CONTIGUOUSLY by a deorbit-transition OrbitSegment (a real orbit-lower
        /// arc with a sub-surface periapsis). The recorded loop clock sweeps the parking conic up to
        /// <see cref="LoopUnit.ParkingConicEndUT"/>; PAST that point an UNCLAMPED segment lookup walks INTO the
        /// contiguous deorbit arc and draws THAT as the loiter orbit (the user sees ~1/3 of an ellipse), then
        /// past the deorbit-arc end NO OrbitSegment covers the UT and the proto retires mid-loiter, so the
        /// parking conic stops rendering until the descent fires. Inside this gap the caller HOLDS the
        /// segment-lookup sample UT at <see cref="LoopUnit.ParkingConicEndUT"/> so the lookup keeps returning the
        /// real recorded PARKING-conic segment and the proto stays alive on it (the icon keeps circling via its
        /// existing live-frame offset). The clamp applies ONLY to the segment lookup; every other read still uses
        /// the live <paramref name="loopUT"/>.</para>
        ///
        /// <para>The boundary is the SHIFTED parking-conic end, NOT <c>conicEnd = RecordedDeorbitUT +
        /// CaptureShiftSeconds</c>: that value lands on the LATER deorbit-ARC end (the last target-body segment
        /// end, also shifted), so <c>loopUT &gt; conicEnd</c> never fired during the loiter and the deorbit arc
        /// leaked. <see cref="LoopUnit.ParkingConicEndUT"/> is sourced from the destination loiter run end (the
        /// orbit-raise/lower sma step between the parking conic and the deorbit arc), a real recorded boundary,
        /// translated into the SHIFTED display frame (<c>descentRun.EndUT + CaptureShiftSeconds</c>) so it matches
        /// <c>conicEnd</c> and the re-aimed effective-segment-lookup effUT - it lands ONE segment EARLIER than
        /// <c>conicEnd</c> (the shifted parking-conic end, not the shifted deorbit-arc end).</para>
        ///
        /// <para>Returns FALSE for: a null unit set / non-member index / non-descent-trigger unit, any member that
        /// is NOT the destination transfer member (<see cref="LoopUnit.TransferMemberIndex"/>) - the owner, every
        /// ride-along, and every descent-SET member (its own trigger-gated render owns it, and it is hidden in the
        /// loiter) - NaN
        /// <see cref="LoopUnit.ParkingConicEndUT"/>, and <paramref name="loopUT"/> &lt;=
        /// <see cref="LoopUnit.ParkingConicEndUT"/> (the icon is still riding the conic - the conic renders
        /// through the unchanged segment lookup, no clamp). Crucially it is also FALSE once the descent trigger
        /// fires: the descent member then owns the visual and the transfer member retires via
        /// <see cref="IsTransferMemberDescentContinuation"/> (the live-clock Descent/Done gate), so there is no
        /// lingering clamp past the trigger - this predicate is a pure recorded-domain <c>loopUT &gt;
        /// parkingConicEnd</c> test that the caller only consults BEFORE the transfer member is retired. Pure; no
        /// Unity (xUnit-testable). Default-off at every non-opted-in call site.</para>
        /// </summary>
        internal static bool IsDescentTransferMemberInLoiterGap(LoopUnit unit, int i, double loopUT)
        {
            // ONLY the destination transfer member (the one whose shifted parking conic exists, =
            // TransferMemberIndex) of a descent-trigger unit can be in the loiter gap. Gating on the exact index
            // (NOT "every non-descent member") excludes the owner / tree root AND every ride-along (e.g. a
            // launch-body-orbit probe whose loop clock runs in a DIFFERENT, unshifted frame and would otherwise
            // fire the clamp meaninglessly). The descent-set members are also excluded (none equals
            // TransferMemberIndex). Byte-identical-off for every non-re-aim unit (HasDescentTrigger false) and
            // every unit with the default TransferMemberIndex == -1 (matches no real index).
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return false;

            double parkingConicEnd = unit.ParkingConicEndUT;
            if (double.IsNaN(loopUT) || double.IsNaN(parkingConicEnd))
                return false;

            // The boundary is the SHIFTED PARKING-conic end (the loiter run's recorded end + captureShift = the
            // deorbit point in the re-aimed display frame), NOT conicEnd = RecordedDeorbitUT + CaptureShiftSeconds
            // (the LATER shifted deorbit-arc end). loopUT here is the loop-shifted sample UT that the map-presence
            // path searches against the re-aimed (shifted) effective segments, so it is in the same SHIFTED frame
            // as ParkingConicEndUT. Past the parking conic the loop clock would otherwise walk INTO the contiguous
            // deorbit-transition OrbitSegment and draw the deorbit arc as the loiter orbit; holding the segment
            // lookup at the parking conic keeps the full parking ellipse rendered through the whole loiter.
            return loopUT > parkingConicEnd;
        }

        /// <summary>
        /// LOITER-GAP clamp convenience wrapper: resolves the owning unit from <paramref name="units"/> via
        /// <see cref="LoopUnitSet.TryGetUnitForMember"/> and delegates to
        /// <see cref="IsDescentTransferMemberInLoiterGap(LoopUnit,int,double)"/>. Returns false for a null set or
        /// a non-member index (byte-identical-off). Lets the GhostMapPresence flight/TS update callers compute the
        /// clamp decision in one line.
        /// </summary>
        internal static bool IsDescentTransferMemberInLoiterGap(LoopUnitSet units, int i, double loopUT)
        {
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return false;
            return IsDescentTransferMemberInLoiterGap(unit, i, loopUT);
        }

        /// <summary>
        /// Loiter-gap HEAD wrap (the rogue-descent fix, step 2): for the destination transfer member
        /// (<see cref="LoopUnit.TransferMemberIndex"/>) of a re-aim descent-trigger unit, once the recorded loop
        /// clock <paramref name="head"/> has swept PAST the parking-conic end
        /// (<see cref="IsDescentTransferMemberInLoiterGap(LoopUnit,int,double)"/>), WRAP the head backward into the
        /// last recorded parking period <c>[P - Tpark, P)</c> (<c>P = <see cref="LoopUnit.ParkingConicEndUT"/></c>,
        /// <c>Tpark = <see cref="LoopUnit.LoiterPeriodSeconds"/></c>) so every head-driven render surface (the FLIGHT
        /// engine ghost + its projected map mesh, the map marker / line / polyline) keeps CIRCLING the closed parking
        /// conic through the loiter hold instead of freezing at the deorbit point (step 1) or playing the recorded
        /// deorbit -&gt; descent at the wrong time (the LIVE descent has not triggered yet). Continuous at engage
        /// (<c>head = P</c> wraps to <c>P - Tpark</c>, the same orbital point one parking revolution earlier on the
        /// closed orbit) and at wrap-around (<c>P-</c> ≡ <c>P - Tpark</c>), and stays inside the recorded parking
        /// segment. If <c>Tpark</c> is NaN or non-positive the wrap is undefined, so it falls back to the step-1
        /// fixed clamp (<c>return P</c>). Returns <paramref name="head"/> unchanged for every other member / phase /
        /// non-re-aim unit (byte-identical-off). Pure; no Unity (xUnit-testable). The map/TS resolver
        /// (<see cref="ResolveTrackingStationSampleUT"/>) calls this same helper inline (it needs the
        /// <c>renderHidden</c> out-param too) so there is ONE wrap formula; the FLIGHT engine drive-clock site calls
        /// it directly.
        /// </summary>
        internal static double ClampTransferMemberHeadToLoiterGap(LoopUnit unit, int i, double head)
        {
            if (!IsDescentTransferMemberInLoiterGap(unit, i, head))
                return head;

            double parkingConicEnd = unit.ParkingConicEndUT;
            double tpark = unit.LoiterPeriodSeconds;

            // Tpark degenerate -> fall back to the step-1 fixed clamp (freeze at the deorbit point).
            if (double.IsNaN(tpark) || tpark <= 0.0)
                return parkingConicEnd;

            // Wrap the head backward into the last recorded parking period [P - Tpark, P) so the icon CIRCLES the
            // closed parking conic during the loiter hold instead of freezing at the deorbit point. Continuous at
            // engage (head=P -> P-Tpark, same orbital point one rev earlier) and at wrap-around (P- ≡ P-Tpark on a
            // closed orbit). Stays inside the recorded parking segment.
            double phase = (head - parkingConicEnd) % tpark; // head > P here, so phase in [0, Tpark)
            if (phase < 0.0) phase += tpark;                  // defensive (head > P makes this unreachable)
            return parkingConicEnd - tpark + phase;
        }

        /// <summary>
        /// LOITER-GAP clamp value: returns the SHIFTED <see cref="LoopUnit.ParkingConicEndUT"/> (the destination
        /// loiter run end + captureShift = the parking-conic end = the deorbit point, in the re-aimed display
        /// frame) for the resolved descent-trigger unit owning member <paramref name="i"/>, or NaN when the index
        /// is not the destination transfer member (<see cref="LoopUnit.TransferMemberIndex"/>) of a descent-trigger
        /// unit (or inputs are degenerate). Paired with
        /// <see cref="IsDescentTransferMemberInLoiterGap(LoopUnitSet,int,double)"/> so a caller can HOLD the
        /// segment-lookup sample UT at this value inside the gap, keeping the lookup on the recorded parking
        /// conic (NOT the contiguous deorbit arc, which the older <c>conicEnd</c> value let leak). Pure; no Unity
        /// (xUnit-testable).
        /// </summary>
        internal static double ResolveLoiterGapConicEndUT(LoopUnitSet units, int i)
        {
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return double.NaN;
            // Gate on the exact destination transfer member (excludes owner / ride-along / descent-set members),
            // matching IsDescentTransferMemberInLoiterGap. Returns the SHIFTED ParkingConicEndUT so the held
            // lookup lands inside the shifted parking conic.
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return double.NaN;
            return unit.ParkingConicEndUT;
        }

        /// <summary>
        /// LOITER-GAP LINE HOLD trigger UT (Layer B of the parking-conic render fix): the LIVE-frame UT the
        /// descent trigger fires for the descent-trigger unit owning the destination transfer member
        /// (<see cref="LoopUnit.TransferMemberIndex"/> == <paramref name="i"/>), so the caller can stamp a per-pid
        /// orbit-line hold that keeps the FULL parking ellipse drawn through the whole loiter (the seg-6 window AND
        /// the post-seg-6 gap) until the descent set takes over. Resolves the unit cycle via the SAME
        /// <see cref="DecideUnitMemberRender"/> path the resolver / engine / I1 use (one source of truth - the
        /// cycle the reseed loop is already on this frame), then the shared
        /// <see cref="Parsek.Reaim.DescentTrigger.ComputeDescentTiming"/>. Returns false / NaN for a null set, a
        /// non-member index, a non-descent-trigger unit, any member that is NOT the destination transfer member
        /// (the owner, a ride-along, or a descent-SET member), an unresolved span clock, or a degenerate (NaN)
        /// trigger - byte-identical-off everywhere the line-hold must not engage. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static bool TryResolveLoiterGapHoldTriggerUT(
            LoopUnitSet units, int i, double liveUT, double memberStartUT, double memberEndUT,
            out double triggerUT)
        {
            triggerUT = double.NaN;
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return false;
            // Line-hold is stamped ONLY for the destination transfer member (the same narrowed scope as the
            // segment-lookup clamp); the live trigger frame it resolves is unaffected by the recorded-frame shift,
            // but its gating must match the clamp so a ride-along never gets a spurious hold.
            if (!unit.HasDescentTrigger || i != unit.TransferMemberIndex)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false;

            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT, unit.RecordedDeorbitUT,
                unit.DestinationBodyRotationPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                out _, out _, out triggerUT);
            return !double.IsNaN(triggerUT);
        }

        /// <summary>
        /// OBSERVABILITY (descent render-window tracing): resolves the UNIT-level descent phase
        /// (Inert / Loiter / Descent / Done) for ANY member <paramref name="i"/> of a descent-trigger unit at
        /// the LIVE clock <paramref name="liveUT"/>, via the SAME <see cref="DecideUnitMemberRender"/> cycle path
        /// + <see cref="Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead"/> the resolver / engine use (the
        /// phase is member-agnostic, so the transfer member and any descent-set member return the same unit
        /// phase). Returns false (phase = Inert) for a non-descent-trigger unit or an unresolved span clock.
        /// Used ONLY to gate the per-frame map-scene snapshot dump to the loiter-orbit + descent-to-landing
        /// windows; never affects rendering. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static bool TryGetDescentUnitRenderPhase(
            LoopUnit unit, int i, double liveUT, double memberStartUT, double memberEndUT,
            out Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
        {
            phase = Parsek.Reaim.DescentTrigger.DescentHeadPhase.Inert;
            if (!unit.HasDescentTrigger)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false;

            phase = Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead(
                liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts, out _);
            return true;
        }

        /// <summary>
        /// OBSERVABILITY convenience wrapper resolving the owning unit from <paramref name="units"/>. Returns
        /// false (phase = Inert) for a null set or a non-member index. See
        /// <see cref="TryGetDescentUnitRenderPhase(LoopUnit,int,double,double,double,out Parsek.Reaim.DescentTrigger.DescentHeadPhase)"/>.
        /// </summary>
        internal static bool TryGetDescentUnitRenderPhase(
            LoopUnitSet units, int i, double liveUT, double memberStartUT, double memberEndUT,
            out Parsek.Reaim.DescentTrigger.DescentHeadPhase phase)
        {
            phase = Parsek.Reaim.DescentTrigger.DescentHeadPhase.Inert;
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return false;
            return TryGetDescentUnitRenderPhase(unit, i, liveUT, memberStartUT, memberEndUT, out phase);
        }

        /// <summary>
        /// Phase 6 (the descent re-stitch, <see cref="Parsek.MapRender.CrossMemberSeamStitcher"/>): resolve the
        /// span-clock CYCLE for descent-trigger unit member <paramref name="i"/> at the live clock
        /// <paramref name="liveUT"/>, via the SAME <see cref="DecideUnitMemberRender"/> path the resolver /
        /// engine use (one source of truth — the cycle the spine's
        /// <see cref="ResolveTrackingStationSampleUT"/> already resolved this frame). The cross-member stitcher
        /// needs the cycle to re-anchor the descent head; exposing this thin wrapper keeps the
        /// <see cref="DecideUnitMemberRender"/> argument list (and the span-clock arithmetic) out of the
        /// stitcher. Returns false (cycle 0) for a non-descent-trigger unit or an unresolved span clock —
        /// byte-identical-off everywhere the descent trigger does not engage. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static bool TryResolveDescentUnitCycle(
            LoopUnit unit, int i, double liveUT, double memberStartUT, double memberEndUT,
            out long unitCycle)
        {
            unitCycle = 0;
            if (!unit.HasDescentTrigger)
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            return decision != UnitMemberRenderDecision.SpanClockUnresolved;
        }

        /// <summary>
        /// C1 (flight descent icon ride): true iff committed index <paramref name="i"/> is a descent-SET member
        /// of a descent-trigger unit that is CURRENTLY rendering its own slice of the re-anchored descent clip
        /// this frame — i.e. <see cref="Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead"/> returns true
        /// (descent phase == Descent AND the shared monotone head falls inside this member's
        /// <c>[memberStartUT, memberEndUT]</c> window). The descent-set members spawn in WINDOW order
        /// (entry → touchdown → landing), which is NOT committed-index order, so the in-window descent member is
        /// usually not the flight-map chain index-tip and is wrongly skipped; this predicate names the member
        /// whose descent leg is actually drawn this frame so the icon can ride it. Resolves the unit cycle via
        /// the production <see cref="DecideUnitMemberRender"/> path (one source of truth). Returns false for a
        /// non-descent-trigger unit, a non-descent member, an unresolved span clock, or a head outside this
        /// member's slice — byte-identical off. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static bool IsActiveDescentCarrierMember(
            LoopUnitSet units, int i, double liveUT, double memberStartUT, double memberEndUT)
        {
            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return false;
            if (!unit.HasDescentTrigger || !unit.IsDescentMember(i))
                return false;

            memberStartUT = unit.MemberStartUT(i, memberStartUT);
            memberEndUT = unit.MemberEndUT(i, memberEndUT);

            UnitMemberRenderDecision decision = DecideUnitMemberRender(
                liveUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                memberStartUT, memberEndUT, out _, out long unitCycle, out _,
                unit.RelaunchSchedule, unit.LoiterCuts, unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds, unit.LaunchBodyRotationPeriodSeconds, unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);
            if (decision == UnitMemberRenderDecision.SpanClockUnresolved)
                return false;

            return Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(
                liveUT, unitCycle, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT,
                unit.RecordedDeorbitUT, unit.DescentEndUT, unit.DestinationBodyRotationPeriodSeconds,
                unit.LoiterPeriodSeconds, unit.CaptureShiftSeconds, unit.LoiterCuts,
                memberStartUT, memberEndUT, out _, out _);
        }

        /// <summary>
        /// C1: the SINGLE committed index that should carry the flight-map descent icon this frame — the
        /// HIGHEST-index descent-set member currently rendering its slice
        /// (<see cref="IsActiveDescentCarrierMember"/>), or -1 when none does (no descent trigger, loiter, or
        /// between cycles). The highest-index tie-break matches the existing chain-tip preference and the
        /// observed render (the landing member rec 43 over the lander-clip rec 42 on their shared surface
        /// window), so exactly ONE descent icon is exempted from the chain-tip skip — no double icon across
        /// overlapping descent windows. Returns -1 (byte-identical off) for a null set / list or no descent-set
        /// member in window. Pure; no Unity (xUnit-testable).
        /// </summary>
        internal static int ResolveFlightDescentIconCarrier(
            System.Collections.Generic.IReadOnlyList<Recording> committed, double liveUT, LoopUnitSet units)
        {
            if (committed == null || units == null)
                return -1;
            int carrier = -1;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null)
                    continue;
                if (i > carrier
                    && IsActiveDescentCarrierMember(units, i, liveUT, rec.StartUT, rec.EndUT))
                    carrier = i;
            }
            return carrier;
        }

        /// <summary>The render outcome for the BOUNDARY-OVERLAP secondary of one member on a given frame.</summary>
        internal enum BoundaryOverlapSecondaryDecision
        {
            /// <summary>No live boundary-overlap secondary this frame (not engaged / outside the borrow window / not a member).</summary>
            NoSecondary,
            /// <summary>The boundary-overlap secondary is live AND in this member's own window — render it at <c>SecondaryLoopUT</c>.</summary>
            Render,
            /// <summary>The boundary-overlap secondary is live but OUTSIDE this member's window — hide the secondary for this member.</summary>
            HiddenOutsideWindow,
        }

        /// <summary>
        /// Pure per-member decision for the BOUNDARY-OVERLAP SECONDARY ghost
        /// (docs/dev/plan-launch-boundary-overlap.md 2.4). Resolves the owning unit's dual-clock frame via
        /// <see cref="ComputeSpanLoopFrame"/> and, when a boundary-overlap secondary is live this frame
        /// (<see cref="SpanLoopFrame.HasSecondary"/>), runs <see cref="IsLoopUTInMemberWindow"/> on the secondary
        /// loopUT against this member's window. This is the testable seam for the engine / map / polyline secondary
        /// dispatch, and it gives per-member independence for free: in a multi-member mission the ascent member
        /// resolves the secondary in-window while the arrival member resolves the primary in-window. Returns
        /// <c>NoSecondary</c> for a non-member index or a frame with no secondary (the common case). Pure: no logging
        /// (the secondary diagnostics live in the clock).
        /// </summary>
        internal static BoundaryOverlapSecondaryDecision DecideBoundaryOverlapSecondaryRender(
            double currentUT,
            double phaseAnchorUT,
            double spanStartUT,
            double spanEndUT,
            double cadenceSeconds,
            double memberStartUT,
            double memberEndUT,
            out double secondaryLoopUT,
            out long secondaryCycleIndex,
            MissionRelaunchSchedule schedule = null,
            IReadOnlyList<LoopCut> loiterCuts = null,
            double arrivalHoldSeconds = 0.0,
            double arrivalHoldAtUT = double.NaN,
            double arrivalHoldAlignPeriod = double.NaN,
            double launchBodyRotationPeriod = double.NaN,
            bool launchHoldEngaged = false,
            double soiExitAtUT = double.NaN)
        {
            secondaryLoopUT = spanStartUT;
            secondaryCycleIndex = 0;

            SpanLoopFrame frame = ComputeSpanLoopFrame(
                currentUT, phaseAnchorUT, spanStartUT, spanEndUT, cadenceSeconds,
                schedule, loiterCuts, arrivalHoldSeconds, arrivalHoldAtUT, arrivalHoldAlignPeriod,
                launchBodyRotationPeriod, launchHoldEngaged, soiExitAtUT);

            if (!frame.Resolved || !frame.HasSecondary)
                return BoundaryOverlapSecondaryDecision.NoSecondary;

            secondaryLoopUT = frame.SecondaryLoopUT;
            secondaryCycleIndex = frame.SecondaryCycleIndex;

            return IsLoopUTInMemberWindow(secondaryLoopUT, memberStartUT, memberEndUT)
                ? BoundaryOverlapSecondaryDecision.Render
                : BoundaryOverlapSecondaryDecision.HiddenOutsideWindow;
        }

        /// <summary>
        /// Dual-clock Tracking-Station / map-presence sample frame: the PRIMARY sample UT
        /// (<see cref="ResolveTrackingStationSampleUT"/>, byte-identical) PLUS the optional boundary-overlap
        /// secondary (docs/dev/plan-launch-boundary-overlap.md 2.5). Used by the map-presence boundary-secondary
        /// branch and the polyline second head; <see cref="ResolveTrackingStationSampleUT"/> stays the primary-only
        /// wrapper so non-dual callers are byte-identical.
        ///
        /// Returns the primary sample UT (clamped to the member window) and sets <paramref name="primaryHidden"/> the
        /// same way the wrapper does. <paramref name="hasSecondary"/> is true with <paramref name="secondaryUT"/>
        /// populated only when this member also carries a live boundary-overlap secondary in its own window. A
        /// non-member index returns the live UT with no secondary. Pure: no logging.
        /// </summary>
        internal static double ResolveTrackingStationSampleFrame(
            int i, double memberStartUT, double memberEndUT, double liveUT,
            LoopUnitSet units, out bool primaryHidden,
            out bool hasSecondary, out double secondaryUT, out long secondaryCycleIndex)
        {
            primaryHidden = false;
            hasSecondary = false;
            secondaryUT = liveUT;
            secondaryCycleIndex = 0;

            double primaryUT = ResolveTrackingStationSampleUT(i, memberStartUT, memberEndUT, liveUT, units, out primaryHidden);

            if (units == null || !units.TryGetUnitForMember(i, out LoopUnit unit))
                return primaryUT;

            // Descent trigger R6: a descent-set member is governed ENTIRELY by the re-anchored primary head; it
            // must NOT also emit a boundary-overlap SECONDARY (the secondary path does not descent-remap, so it
            // would draw the body-fixed clip at the raw loop UT = the wrong rotation again). Suppress it.
            if (unit.HasDescentTrigger && unit.IsDescentMember(i))
                return primaryUT;

            double trimmedStart = unit.MemberStartUT(i, memberStartUT);
            double trimmedEnd = unit.MemberEndUT(i, memberEndUT);

            BoundaryOverlapSecondaryDecision secDecision = DecideBoundaryOverlapSecondaryRender(
                liveUT,
                unit.PhaseAnchorUT,
                unit.SpanStartUT,
                unit.SpanEndUT,
                unit.CadenceSeconds,
                trimmedStart,
                trimmedEnd,
                out double secLoopUT,
                out long secCycle,
                unit.RelaunchSchedule,
                unit.LoiterCuts,
                unit.ArrivalHoldSeconds,
                unit.ArrivalHoldAtUT,
                unit.ArrivalAlignPeriodSeconds,
                unit.LaunchBodyRotationPeriodSeconds,
                unit.LaunchHoldEngaged,
                unit.RecordedSoiExitUT);

            if (secDecision == BoundaryOverlapSecondaryDecision.Render)
            {
                hasSecondary = true;
                secondaryUT = secLoopUT;
                secondaryCycleIndex = secCycle;
            }

            return primaryUT;
        }

        /// <summary>
        /// Edge 9 branch predicate for <c>GhostPlaybackEngine.TryUpdateLoopSyncedDebris</c>: returns
        /// true when a loop-synced debris's parent (<paramref name="parentIdx"/>) is itself a
        /// chain-loop unit member, in which case the debris must source the unit's SHARED span clock
        /// (resolving the owner unit via <paramref name="resolvedUnit"/>) instead of the parent's own
        /// per-recording loop clock — a unit member's standalone loop clock never sweeps into a
        /// sibling's window, so it is the wrong phase for the debris. Returns false (and the engine
        /// keeps the existing per-recording loop-clock path) when the parent is not a unit member or
        /// the set is empty. Pure: the GameObject render decision downstream is verified in-game (P8).
        /// </summary>
        internal static bool ShouldSourceDebrisFromUnitSpan(
            int parentIdx, LoopUnitSet units, out LoopUnit resolvedUnit)
        {
            resolvedUnit = default;
            if (units == null || parentIdx < 0)
                return false;
            return units.TryGetUnitForMember(parentIdx, out resolvedUnit);
        }

        /// <summary>
        /// Two-tier comparison used when scanning a unit's members to pick the live CAMERA member
        /// for a watch handoff. A member whose vessel name matches the WATCHED member's vessel name
        /// (the continuing through-line) always beats a non-match; within a tier, the newest segment
        /// (highest StartUT) wins. Returns true when the candidate should replace the current best.
        ///
        /// This keeps the camera on the craft the player is watching across a structural fork where a
        /// piece peels off at the same UT the parent vessel continues (e.g. a crew EVA: the
        /// continuing pod and the EVA kerbal share the same StartUT, so a plain highest-StartUT pick
        /// would tie and could grab the kerbal). The caller seeds the scan with
        /// currentMatchesWatched=false and currentStartUT=NegativeInfinity, so the first in-window
        /// member always wins. When nothing is watched the caller passes matchesWatched=false for
        /// every member, degrading this to a pure highest-StartUT pick. Pure; unit-tested.
        /// </summary>
        internal static bool IsBetterUnitCameraLiveMember(
            bool candidateMatchesWatched, double candidateStartUT,
            bool currentMatchesWatched, double currentStartUT)
        {
            if (candidateMatchesWatched != currentMatchesWatched)
                return candidateMatchesWatched;
            return candidateStartUT >= currentStartUT;
        }

        /// <summary>
        /// Gates the unit-handoff retarget so the camera only follows a SAME-VESSEL continuation of
        /// the through-line the player is watching. Returns <paramref name="liveMemberIdx"/> when the
        /// chosen live member matches the watched vessel name (a continuation, e.g. launch stage ->
        /// upper stage of the same craft), and -1 otherwise. A -1 result tells
        /// <see cref="ShouldRetargetWatchOnUnitHandoff"/> there is no member to hand off to, so the
        /// watch stays on the ending member and its own terminal end (explosion hold -> return to
        /// anchor) takes over instead of the camera jumping onto a different-vessel sibling (a kerbal
        /// who went EVA, a separated booster) at the moment of impact. Pure; unit-tested.
        /// </summary>
        internal static int ResolveUnitHandoffRetargetMember(int liveMemberIdx, bool liveMatchesWatched)
            => liveMatchesWatched ? liveMemberIdx : -1;

        /// <summary>
        /// Edge 10 decision: should the engine fire a unit-handoff camera retarget this frame?
        /// Under the shared-clock concurrent model there is no single "selected" member - every
        /// member renders when the clock is in its own window. The camera follows ONE watched
        /// member; when the shared clock leaves that member's window it stops rendering, so the
        /// camera must move to a member that IS still rendering this frame. Returns true ONLY when
        /// (a) the watched index is a member of <paramref name="unit"/> (the camera is following this
        /// unit), AND (b) the watched member was rendering last frame
        /// (<paramref name="watchedWasRendering"/>) but is NOT rendering this frame
        /// (<paramref name="watchedIsRendering"/> == false), AND (c) there is a different live member
        /// to retarget to (<paramref name="newLiveMemberIndex"/> &gt;= 0). When this returns true the
        /// engine fires <c>CameraActionType.UnitHandoffRetarget</c> carrying
        /// <paramref name="newLiveMemberIndex"/> and the host transfers the camera. Pure; the
        /// FlightCamera transfer itself is verified in-game. Returns false while the watched member
        /// is still rendering (steady state), and when no live member exists (the inter-cycle wait /
        /// a gap), so the camera holds its current anchor rather than yanking to nothing.
        /// </summary>
        internal static bool ShouldRetargetWatchOnUnitHandoff(
            int watchedIndex, bool watchedWasRendering, bool watchedIsRendering,
            int newLiveMemberIndex, LoopUnit unit)
        {
            if (watchedIndex < 0)
                return false;
            // The camera must be following a member of THIS unit.
            bool watchingThisUnit = false;
            int[] members = unit.MemberIndices;
            if (members != null)
            {
                for (int m = 0; m < members.Length; m++)
                {
                    if (members[m] == watchedIndex)
                    {
                        watchingThisUnit = true;
                        break;
                    }
                }
            }
            if (!watchingThisUnit)
                return false;
            // Only fire on the transition from "watched member rendering" to "watched member no
            // longer rendering" (its window just ended), and only when there is a real different
            // live member to move to. Steady state (still rendering) and "nothing live" hold.
            if (!watchedWasRendering || watchedIsRendering)
                return false;
            if (newLiveMemberIndex < 0 || newLiveMemberIndex == watchedIndex)
                return false;
            return true;
        }

        /// <summary>
        /// Self-healing edge for the unit-handoff retarget: decides what value the engine should
        /// store for the "watched member was rendering" edge of its per-unit transition state after
        /// it fired (or considered firing) a <c>UnitHandoffRetarget</c> this frame.
        ///
        /// The host transfer can DEFER when the target member's ghost is still being built
        /// (unit-member respawns are time-sliced over several frames). If the engine advanced its
        /// edge unconditionally, the steady-state early-return would suppress any re-fire for the
        /// rest of the cycle and the camera would stay stranded on the now-hidden old member. To make
        /// the retarget retry every frame until it lands, the engine must PRESERVE the
        /// rendering-edge (return <c>true</c>) while the retarget fired but the watch camera has not
        /// yet moved to <paramref name="newLiveMemberIndex"/> (detected via
        /// <paramref name="watchedIndex"/>, which is the live watched index the engine reads each
        /// frame). Once the transfer lands (<paramref name="watchedIndex"/> ==
        /// <paramref name="newLiveMemberIndex"/>) or the retarget did not fire this frame, the real
        /// <paramref name="watchedIsRendering"/> value is stored and re-firing stops.
        ///
        /// Returns the value to store for the edge. Pure; no infinite re-fire because the stored
        /// <c>true</c> only persists while <paramref name="watchedIndex"/> still lags
        /// <paramref name="newLiveMemberIndex"/>.
        /// </summary>
        internal static bool ResolveUnitHandoffStoredRenderingEdge(
            bool retargetFired, int watchedIndex, int newLiveMemberIndex, bool watchedIsRendering)
        {
            if (retargetFired && watchedIndex != newLiveMemberIndex)
                return true; // transfer pending (target ghost not spawned yet) -> re-fire next frame
            return watchedIsRendering;
        }
    }
}
