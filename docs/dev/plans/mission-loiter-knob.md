# M4b - The phasing-loiter knob (per-cycle keepRevs re-timer)

Status: IMPLEMENTED (2026-06-11, branch `mission-loiter-knob`). The RouteLoopClock audit (section 6) found `IsDockCrossing` sawtooth-safe as-is (the dock lies past the guard, so the dock cycle index holds during the wrap; pinned by `RouteDockCrossing_ExtensionSawtooth_FiresOncePerCycle`); the in-game piece ships the Unity-bound `TryGetVesselOrbit` tests (MissionPhasing category), with the schedule/clock logic covered headless per the section-7 fallback. Parent design: `docs/dev/design-mission-phasing-alignment.md` section 4
(decisions D4/D5/D6) and section 9 (build order M4a -> M4b -> M4c). M4a (`VesselOrbital`
Tier 1, PR #1119) is merged; alignment is structurally correct but RARE (~0.5% of pad
windows meet the 1-degree station tolerance; the 2026-06-11 playtest's first window was
bounded-best at residual 354.6s, ~52 degrees). M4b adds the free variable that makes
nearly every pad window alignable and removes the recorded-loiter dead time from loop
cycles in the same stroke.

## 1. What ships

Per launch window N of a scheduled mission, the loop clock keeps k_N parking revolutions
of the recorded phasing loiter (instead of always replaying all R recorded revs), chosen
so the shifted post-loiter timeline lands the rendezvous (or celestial window) at its
recorded phase. Cutting d = k_N - R revs (d < 0) or extending (d > 0, capped) shifts every
post-loiter event by d * T_park, so the station residual at pad window k becomes

    residual_j(k, d) = CircularPhaseError(k * T_anchor + d * T_park, P_j)

and d is enumerable over a small bounded range. Fully automatic (D4), loops only (first
play stays the faithful full recording), whole-rev quantization only (D5), k_N in
[1, R + MaxExtraLoiterRevs] with MaxExtraLoiterRevs = 10 (D6), fail closed to the
faithful (k_N = R) loiter phase with an amber reason when no in-bounds d reaches
tolerance.

Consumers: the M4a station mission (the logistics resupply profile - the load-bearing
case), and any scheduled same-parent mission with a parking loiter before its window
(e.g. waiting in LKO for a Mun window, served through the existing `Orbital(C)`
constraint). Re-aim missions are NOT rewired here (they keep their static-cut path and
`ReaimWindowSchedule`); the shared deliverable for M-MIS-2 P4 is the API surface (run
detection + per-cycle keepRevs entries), not re-aim consumption.

## 2. Current state (what the knob builds on)

- `ReaimLoiterCompressor.ComputeCuts` (`ReaimLoiterCompressor.cs:58`): pure, detects
  same-body closed-orbit loiter runs in startUT-sorted OrbitSegments and emits
  whole-period `LoopCut`s from the run START (exit phase preserved). Run detection is
  inline; run metadata (start/end/period/wholeRevs) is not exposed.
- Static cuts are consumed ONLY by the uniform (non-schedule) span-clock path:
  `GhostPlaybackLogic.TryComputeSpanLoopUT` (`GhostPlaybackLogic.cs:7198`) compresses the
  span, wraps phase over it, and maps back via `DecompressSpanUT`. The schedule branch
  (`:7227-7266`) returns early and IGNORES cuts; the INV-3 guard (`:7241`) warns if a
  scheduled unit ever carries cuts. M4b changes this invariant deliberately: per-launch
  cuts become the sanctioned composition with a schedule; a STATIC `LoopUnit.LoiterCuts`
  list plus a schedule keeps warning.
- `MissionRelaunchSchedule` (`MissionPeriodicity.cs:2011`): lazily grown launch-UT cache
  over anchor multiples, built by `TryBuildRelaunchSchedule` (`:1116`) from the constraint
  set; the accept test per candidate k is `TryFindNextScheduleK` (`:968`) =
  max_j CircularPhaseError(k * T_anchor, P_j) vs per-constraint tolerance. Not persisted;
  rebuilt per unit build.
- Member rendering consumes ONLY the recorded-space `loopUT`
  (`DecideUnitMemberRender` / `IsLoopUTInMemberWindow`), so cuts/extension are entirely a
  clock-mapping concern; member windows never change.
- The arrival hold (`ApplyArrivalHoldToPhase`, `:7149`) is the existing INSERT-dead-time
  primitive, but it FREEZES the ghost at a boundary. A loiter extension must instead WRAP
  the parking orbit (the ghost keeps orbiting), so it is a new pure helper, not a hold
  reuse (section 4.3).

## 3. The solve (pure layer)

### 3.1 Run detection API

Refactor `ReaimLoiterCompressor` to expose the runs it already finds:

    internal struct LoiterRun
    {
        public double StartUT;       // run start (recorded)
        public double EndUT;         // run end (recorded)
        public double PeriodSeconds; // T_rep from the run's FIRST segment's a
        public long WholeRevs;       // snap-tolerant floor of duration / T_rep
    }

    internal static List<LoiterRun> DetectRuns(segs, bodyMu, aStepRelThreshold, ...)

`ComputeCuts` becomes a thin wrapper over `DetectRuns` (cut = (WholeRevs - keepRevs) *
PeriodSeconds from StartUT when WholeRevs > keepRevs). Existing behavior and existing
tests stay byte-identical; this is the keepRevs API piece shared with M-MIS-2 P4.

### 3.2 Constraint partition: shiftable vs unshiftable

A loiter re-time of d revs shifts every recorded event AFTER the phasing run by
d * T_park. A constraint is SHIFTABLE iff its reference event is after the run end:

    shiftable(c) := ut0 + c.PhaseOffsetSeconds >= phasingRun.EndUT - epsilon

(the pad Rotation constraint has offset ~0, before any loiter: unshiftable; the
VesselOrbital first-rendezvous and a Mun-window Orbital encounter are after the parking
loiter by construction: shiftable). Unshiftable constraints are evaluated at the launch
exactly as today.

The partition operates on the SAME post-Drop `effective` constraint list
`TryBuildRelaunchSchedule` already constructs, with `ScheduleToleranceSecondsFor`
tolerances (so Drop-mode transited rotations never reach the partition, and Loose-mode
tolerances carry through unchanged). The residual formula holds for ALL shiftable kinds:
a transited-body Rotation (Mun landing), a same-parent Orbital (Mun encounter), and a
VesselOrbital rendezvous all have their reference event shifted by d * T_park, so the
error against ANY period P_j is CircularPhaseError(k * T_anchor + d * T_park, P_j).

ENGAGEMENT RULES (all must hold, else the knob stays off and the schedule is built
exactly as today - fail closed):

1. A schedule exists (the knob is a per-WINDOW solve; uniform-cadence units have no
   per-window seam). Non-scheduled units are out of scope for per-cycle k_N.
2. A phasing run exists: the LAST compressible run (WholeRevs >= 1, closed) on the LAUNCH
   body OR on a RENDEZVOUS body (the body a VesselOrbital station orbits, taken from each
   VesselOrbital constraint's BodyName), whose EndUT <= guardUT, where guardUT =
   min(earliest vessel-anchor first rendezvous UT, first GENUINE-third-body segment start,
   spanEnd). This is the 4.3 cut-placement rule: never cut between two same-vessel
   rendezvous events, never cut across a third-body SOI boundary.

   Destination-body docks (2026-06-13 fix): a same-parent station dock parks AROUND the
   destination body (a Mun-station resupply parks in low-Mun orbit AFTER the Kerbin->Mun
   SOI entry). That parking orbit is the designed phase absorber, so the SOI entry into a
   rendezvous body must NOT clamp guardUT and the rendezvous-body run must be an eligible
   phasing run. Only a segment whose body is NEITHER the launch body NOR a rendezvous body
   (a genuine third-body SOI entry the schedule cannot phase against, e.g. Minmus on a
   Kerbin->Mun->Minmus hop) clamps guardUT. The body acceptance for both the phasing run
   and the earlier static cuts is `MissionLoopUnitBuilder.IsPhasingRunBodyAccepted`
   (launch body OR a rendezvous body; an empty launch body accepts every run). This is a
   guard GAP fix, not a scope change: the same-parent destination dock is NOT in M4c's
   out-of-scope set (only cross-parent stations are), and the residual formula above
   already names a same-parent Orbital encounter as shiftable.
3. The ANCHOR constraint's reference event is BEFORE the phasing run start. The anchor
   is pinned exactly at k * T_anchor; if its event were after the loiter, the shift
   d * T_park would break that exactness. (The pad anchor always satisfies this.)
4. At least one shiftable constraint exists. (Otherwise d has nothing to serve;
   keep today's schedule.)
5. Defensive: |extraction.UT0 - spanStartUT| < epsilon. The residual derivation (3.3a)
   assumes schedule launches (ut0 + k * T_anchor) and the span clock's phase origin
   (spanStartUT) coincide, as they do for every unit the builder produces today; if a
   future builder path ever decouples them, the knob disengages with a logged reason
   rather than solving against a silently offset target.

Runs BEFORE the phasing run that also end before guardUT get STATIC compression to
keepRevs = 1 (same for every launch; they are dead time, not a phase instrument). Runs
ending after guardUT are never touched.

IN-SPAN REQUIREMENT (post-implementation review fix): the owner's OrbitSegments are not
clipped to the unit span, and a TRIMMED mission (interval exclusions) can open its render
window mid-recording. A run not entirely inside [spanStartUT, guardUT] would produce cuts
referencing out-of-span UTs, which the clock's effSpan/Decompress composition cannot
represent - such runs are never the phasing run and never a static cut, and if that
excludes every candidate the knob fails closed (pinned by
KnobInput_RunStartsBeforeSpanStart_Disengages / KnobInput_PreSpanRun_NeverBecomesAStaticCut).

### 3.3a Worked example (sign check for the residual formula)

UT0 = spanStart = 0. Loiter run [100, 250), T_park = 50, R = 3 (duration 150). Recorded
rendezvous at UT_r = 300 (offset O = 300). Launch window k: live launch at
L = k * T_anchor.

- k_N = 2 (d = -1): cut = LoopCut{ Start = 100, Length = 50 }. The rendezvous loopUT 300
  is reached at compressed phase CompressSpanUT(300) = 300 - 50 = 250, i.e. live time
  L + 250. Live-minus-recorded at the rendezvous: (L + 250) - 300 = k * T_anchor - 50
  = k * T_anchor + d * T_park. Station phase error =
  CircularPhaseError(k * T_anchor + d * T_park, T_station). The launch itself (phase 0,
  loopUT = 0) is untouched: the cut starts at 100, after launch, so the pad anchor
  remains exact.
- k_N = 4 (d = +1): no cut; extension 50 wraps the last recorded rev [200, 250). The
  rendezvous loopUT 300 is reached at phase 300 + 50 = 350 (deferred by the extension),
  live time L + 350; live-minus-recorded = k * T_anchor + 50 = k * T_anchor + d * T_park.
  Same formula, sign consistent in both directions.

The recorded rendezvous UT never moves (recorded data immutable); only the LIVE time at
which the clock reaches it shifts, which is exactly the quantity the station period
measures.

### 3.3 Inner enumeration (extends TryFindNextScheduleK)

New overload (the old signature delegates with an empty shiftable group, byte-identical):

    internal static bool TryFindNextScheduleK(
        double anchorPeriod,
        IReadOnlyList<double> otherPeriods, IReadOnlyList<double> otherTolerances,   // unshiftable
        IReadOnlyList<double> shiftPeriods, IReadOnlyList<double> shiftTolerances,   // shiftable
        double shiftStepSeconds,       // T_park
        long shiftMin, long shiftMax,  // d bounds: [1 - R, +MaxExtraLoiterRevs]
        long kStart, int lookaheadMultiples,
        out long foundK, out long foundShiftRevs,
        out double residualSeconds, out bool withinTolerance)

Per candidate k:
- worstUnshift = max_j CircularPhaseError(k * T_anchor, P_j); must be within each
  tolerance (as today).
- inner loop d in [shiftMin, shiftMax] ordered by |d| ascending (prefer the smallest
  timeline change): worstShift(d) = max_j CircularPhaseError(k * T_anchor + d * T_park,
  P_j). First d with all shiftable within tolerance wins for this k (short-circuit).
- Accept the first k where both groups pass. Else bounded-best over the window:
  minimize max(worstUnshift, min_d worstShift(d)); ties -> earlier k, then smaller |d|.
  Mirrors the existing never-accumulate acceptance shape.

Cost: lookahead 4096 x ~(R + 10) inner steps worst case, but the inner loop
short-circuits and with the knob nearly every k accepts immediately (that is the entire
point), so the realistic scan depth is small. CircularPhaseError is a few flops; even the
degenerate full scan is single-digit milliseconds, off the hot path (schedule build /
lazy extension only).

### 3.4 Per-launch timing entry

`MissionRelaunchSchedule` gains an optional knob config (passed at construction by
`TryBuildRelaunchSchedule`):

    internal struct PhasingKnobConfig
    {
        public double RunStartUT, RunEndUT, PeriodSeconds; // the phasing run
        public long RecordedRevs;                          // R
        public IReadOnlyList<LoopCut> StaticCuts;          // earlier runs, keepRevs=1
        public double[] ShiftPeriods, ShiftTolerances;     // shiftable partition
        public long ShiftMin, ShiftMax;
    }

and materializes, in the same lazy launch-extension walk, one entry per launch:

    internal struct LaunchTimingEntry
    {
        public long KeptRevs;                  // k_N = R + d
        public double ResidualSeconds;
        public bool WithinTolerance;
        public IReadOnlyList<LoopCut> Cuts;    // static cuts + phasing cut (d < 0); sorted
        public double ExtensionSeconds;        // (d > 0) ? d * T_park : 0
        public double ExtensionWrapStartUT;    // RunEndUT - PeriodSeconds (recorded)
        public double ExtensionWrapPeriod;     // PeriodSeconds
    }

    internal bool TryGetLaunchTiming(long cycleIndex, out LaunchTimingEntry entry)

Cuts per entry: the static cuts plus, for d < 0, a `LoopCut { StartUT = RunStartUT,
LengthSeconds = -d * PeriodSeconds }` merged in sorted order. For d > 0 the phasing run
gets NO cut and the extension fields are set. Entries are tiny; the list grows with the
existing launch cache.

Knob-less schedules (no config) materialize nothing and behave byte-identically.

NON-OVERLAP under extension: the per-launch effective span is
effSpan_N = span - TotalCutLength(Cuts_N) + ExtensionSeconds. Extension can push
effSpan_N past the recorded span, so the lazy walk (`ExtendOnce`,
`MissionPeriodicity.cs:2149-2178`) enforces the NEXT launch at
>= launches[last] + max(minSpacing, effSpan_last) - the throttleK ceiling there
currently uses minSpacing alone, and minSpacing >= span covers only the cut-only case.
The walk is strictly sequential (entry N materializes before entry N+1 is probed), so
effSpan_last is always available when the next throttle is computed. This keeps
scheduled units non-overlapping by construction with launch UTs still on exact anchor
multiples; a window that cannot respect the spacing is skipped exactly like a
min-spacing skip today.

LIFECYCLE: per-launch entries are TRANSIENT, exactly like the schedule that owns them -
`MissionRelaunchSchedule` is never persisted and is rebuilt on every unit build, so
`PhasingKnobConfig` / `LaunchTimingEntry` need no OnSave/OnLoad handling. Nothing in
this milestone serializes; recorded data stays immutable (all re-timing is
loop-clock-side).

## 4. Engine seam (the one real clock change)

### 4.1 Schedule branch consumes per-launch timing

In `TryComputeSpanLoopUT`'s schedule branch, after `TryResolveActiveLaunch`:

    if (schedule.TryGetLaunchTiming(sIdx, out entry))
    {
        double effSpan = span - TotalCutLength(entry.Cuts) + entry.ExtensionSeconds;
        double phase = min(scheduledPhase, effSpan);
        isInInterCycleTail = scheduledPhase >= effSpan;
        double wrapped = ApplyLoiterExtensionToPhase(phase, entry, spanStartUT);  // 4.3
        loopUT = DecompressSpanUT(spanStartUT + wrapped, entry.Cuts);
        ...
    }
    // no entry -> today's path, byte-identical

The INV-3 guard text updates: a schedule with per-launch timing is the sanctioned
composition; a schedule with a STATIC `loiterCuts` list still warns (the builder never
produces that pairing).

### 4.2 Per-launch effective span and the tail

`isInInterCycleTail` flips at effSpan_N (was: span). Between effSpan_N and the next
launch all members hide (delivery done, nothing in transit) - this is the dead-time
disappearance for routes: the visible cycle is launch -> compressed loiter -> rendezvous
-> end, then the unit parks until the next scheduled window.

### 4.3 Extension wrap (new pure helper)

    internal static double ApplyLoiterExtensionToPhase(
        double phase, double extLen, double wrapStartCompressedPos, double wrapPeriod)

With insertPos = the compressed-phase position of the recorded wrap start
(CompressSpanUT(ExtensionWrapStartUT, cuts) - spanStartUT):

- phase <= insertPos: identity.
- insertPos < phase < insertPos + extLen: wrap the final recorded parking rev:
  return insertPos + ((phase - insertPos) % wrapPeriod).
- phase >= insertPos + extLen: return phase - extLen (recorded sequence, deferred).

The wrap window is the LAST recorded rev [RunEndUT - T_park, RunEndUT): during the
inserted dead time the ghost keeps orbiting that rev (visually identical revolutions on
the same closed orbit), then the mapping resumes at the wrap start and the final rev
plays once more for real before exiting the loiter. Every seam is a whole rev:
position- and velocity-continuous by the same argument as the shipped compression.
Unlike the arrival hold there is NO frozen ghost. extLen is always a whole multiple of
wrapPeriod by construction (d * T_park), so the exit seam is exact.

The wrap window is CUT-FREE by construction, so the constant wrapPeriod modulo is
sound: static cuts belong to EARLIER runs (which end before the phasing run starts),
and the phasing run's own cut exists only for d < 0 - extension (d > 0) and the phasing
cut are mutually exclusive by definition of d. (Even for d < 0 the cut
[RunStartUT, RunStartUT + (R - k_N) * T_park) ends at least one whole rev before
RunEndUT since k_N >= 1.) insertPos therefore only ever accumulates compression from
cuts entirely BEFORE the wrap start, which is exactly what CompressSpanUT computes.

### 4.4 Known visual caveat (accepted, re-aim precedent)

A cut or extension skips/repeats a UT window on the SHARED unit clock, so closed-orbit
BYSTANDER members (the dock-merged partner's recorded twin, a parked tug) jump by
(cutLen mod T_their_period) of phase across the seam. The shipped re-aim compression
already does exactly this to non-transfer members and it was accepted; the live station
itself is unaffected (live-PID Relative anchoring), and the partner's recorded twin is a
cosmetic duplicate of the live vessel. Recorded in the plan so the playtest looks for
it; if it reads badly, the candidate follow-up is hiding the foreign partner member
during loops (it duplicates a live vessel), not bending the clock per member.

## 5. Builder wiring (MissionLoopUnitBuilder)

In the same-parent phase-locked block (after `TryBuildRelaunchSchedule` succeeds,
`MissionLoopUnitBuilder.cs:297-315`):

1. Gather the SELF-LINE loiter source segments: every member sharing the OWNER's launch
   identity (pid + guid, `VesselLaunchIdentity.RecordingsShareLaunch`), flattened and
   startUT-sorted. PLAYTEST CORRECTION (2026-06-11): the original owner-only scan missed
   chain missions entirely - the unit owner is the chain ROOT segment, which carries no
   OrbitSegments; the parking loiter lives in the same-launch continuation member.
   Same-launch chain segments are ONE vessel's sequential timeline, so flattening them is
   safe; the identity gate is what excludes other vessels (the dock-merged partner's
   parked orbit IS a loiter but never OUR phasing instrument, and debris/probes differ in
   pid). If the self line yields no compressible run, the knob disengages (rule 2 in 3.2).
2. Compute guardUT (3.2 rule 2) from the extraction's VesselOrbital first-rendezvous UT
   (ut0 + PhaseOffsetSeconds), the first GENUINE third-body segment (a body that is
   neither the launch body nor a rendezvous body), and spanEnd.
3. Partition constraints (3.2), check engagement rules, and rebuild the schedule WITH
   the `PhasingKnobConfig` (`TryBuildRelaunchSchedule` gains the optional config path;
   the no-knob call stays as-is). The schedule, not the LoopUnit, owns per-launch
   timing; `LoopUnit.LoiterCuts` stays the re-aim static list and stays null here.
4. Logging (design section 8): one Verbose build line "knob engaged: run=[a,b] T=...s
   R=... shiftable=N guard=..." or the disengage reason (one line per failed rule,
   batch-counted if multiple missions build at once); per-window chosen k_N +
   residual via VerboseRateLimited keyed on mission identity from the schedule's lazy
   walk; amber transition (unreachable k_N within bounds) once per transition at Info.
   All numeric formatting via `ToString("R"/"F0", CultureInfo.InvariantCulture)` per the
   project convention.

UI: per design 5.4 no new controls. The period cell keeps the M4a "(station window)"
basis label; the unreachable-k amber reuses the M4a amber plumbing with reason text
"station phase unreachable within +10 revs this window". k_N surfaces through
`MissionRelaunchSchedule.TryGetLaunchTiming` for routes (M-MIS-11 item 3 does the typed
exposure; this plan only guarantees the value is reachable from the loop state).

## 6. Route interaction

`RouteLoopClock.TryGetRouteLoopState` already passes the unit's schedule into the clock;
per-launch cuts flow automatically. Two checks:

- The dock-crossing detector compares consecutive loopUT samples; the extension wrap
  introduces BACKWARD jumps mid-cycle (sawtooth). RecordedDockUT is after the loiter
  (guardUT rule) so the dock is never inside the wrap window, but the detector must not
  misread a sawtooth drop as a loop restart. Audit `RouteLoopClock` crossing logic and
  pin with a test (sawtooth inside the loiter, dock crossing after it, one fire per
  cycle).
- Cycle length now varies per launch (effSpan_N); route code that assumed a constant
  span-derived cycle length is audited (delivery fires on the loopUT marker, which is
  recorded-space and unaffected).

## 7. Test plan

xUnit (all pure):
- `DetectRuns`: run boundaries, periods, wholeRevs (incl. snap tolerance, same-orbit
  gap merge, a-step run end); `ComputeCuts` delegation byte-identical to the existing
  expectations (existing tests must pass unchanged).
- Inner solve: knob accepts a window the knob-less scan rejects; d ordering by |d|
  (smallest timeline change wins); bounds [1 - R, +10]; bounded-best when out of reach
  (ties earlier k then smaller |d|); empty shiftable group delegates byte-identically;
  degenerate T_park.
- Schedule materialization: per-launch entries align with launch indices across lazy
  extension; cut list shapes for d < 0 / d = 0 / d > 0; static cuts merged sorted;
  non-overlap spacing under extension (next launch >= prior + effSpan).
- Span clock: schedule + per-launch cuts (loopUT skips the cut window; effSpan tail);
  extension wrap continuity (phase -> loopUT monotone except exact whole-rev sawtooth;
  seam values at insertPos, insertPos + extLen exact); knob-less schedule byte-identical;
  INV-3 static-cuts-plus-schedule still warns.
- Builder: engagement rules 1-4 each individually disengage (log-asserted reasons);
  partner-member loiter never drives cuts; guardUT from rendezvous / genuine third-body
  change (launch + rendezvous bodies skipped, so a destination-body parking loiter is in scope).
- RouteLoopClock: sawtooth does not double-fire or miss the dock crossing.

In-game (`InGameTests`, new "MissionPhasing" category):
- The M4a deferred test: `TryGetVesselOrbit` resolves the active vessel's live orbit
  (period > 0, correct body) and rejects a bogus pid - the live-resolution seam xUnit
  cannot cover.
- Scenario smoke: build a synthetic station-resupply tree via the generators (parking
  loiter R >= 3 + Relative rendezvous section), loop it, assert the schedule carries
  per-launch timing entries and the span clock returns in-loiter loopUTs that skip the
  cut window (log-asserted; full visual validation stays the playtest). PRECONDITION:
  verify the `Tests/Generators/` RecordingBuilder can author vessel-anchored Relative
  TrackSections for an in-game-injectable recording (the M4a xUnit StationRecording
  helpers prove the shape works headless); if the in-game injection path lacks it,
  ship the `TryGetVesselOrbit` test plus the headless scenario coverage and note the
  gap rather than building a generator extension inside this milestone.

## 8. Out of scope

- Re-aim consumption of per-cycle k_N (M-MIS-2 P4 wires the destination-side re-timer).
- Uniform-cadence (non-scheduled) generic loiter compression: the dead-time win without
  a schedule needs anchor-shift semantics the design ties to the per-window solve; if
  playtest demands it, it is a small follow-up on the same primitives.
- Tier 2 cross-parent station hold (M4c).
- Route cycle-id stamping of k_N (M-MIS-11 item 3).
- Per-mission opt-out toggle (design 4.4: only on playtest evidence).

## 9. File touch list

- `Source/Parsek/Reaim/ReaimLoiterCompressor.cs` - DetectRuns + LoiterRun; ComputeCuts
  thins to a wrapper.
- `Source/Parsek/MissionPeriodicity.cs` - TryFindNextScheduleK shiftable overload;
  PhasingKnobConfig; MissionRelaunchSchedule per-launch entries + spacing under
  extension; TryBuildRelaunchSchedule config path.
- `Source/Parsek/GhostPlaybackLogic.cs` - schedule-branch per-launch consumption;
  ApplyLoiterExtensionToPhase; INV-3 guard text.
- `Source/Parsek/MissionLoopUnitBuilder.cs` - knob engagement + wiring + logging.
- `Source/Parsek/Logistics/RouteLoopClock.cs` - crossing-detector audit (code change
  only if the audit finds a sawtooth hazard).
- `Source/Parsek/InGameTests/` - MissionPhasing category tests.
- `Source/Parsek.Tests/` - new + extended test classes per section 7.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, design doc M4b status note.
