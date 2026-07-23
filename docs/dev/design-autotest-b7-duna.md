# B7 Duna interplanetary flyby (b7_duna_flyby) - design + mlib diff plan

Status: IMPLEMENTED (branch `autotest-b7-duna`, 2026-07-22) / PENDING FIRST
FLIGHT. Section 5's line numbers and several branch shapes were written against
the pre-native-warp mlib and are STALE; the landed implementation adapts them to
the no-1x-coast lane (see the B7 entry in `docs/dev/todo-and-known-bugs.md`).
The LIVE-PROVE items in sections 1/3/4 and the open questions in section 8
remain pending. This is the preparation design for the fourth flown
flyby mission, extending the body-parameterized B5 machine to the first
INTERPLANETARY flight (Kerbin -> Sun -> Duna -> Sun). It is written so the mlib
diff plan below can be applied mechanically once B5/B6 are stable and the
`WARP_TO_UT` / `CANCEL_WARP` native warp primitive (built in parallel) has
landed. NOTHING here has flown; every threshold is a tolerance / budget, never a
golden trajectory, and every numeric budget is ESTIMATED from arithmetic, not
measured.

Authorities read: `harness/missions/lib/mlib.py` (the B5 machine),
`b5_mun_flyby.py` / `b6_minmus_flyby.py` (the alias shells),
`docs/dev/todo-and-known-bugs.md` "B5 Mun flyby" + "B6 Minmus / B7 Duna flybys",
`docs/dev/research/native-warp-to-ut.md`,
`docs/dev/research/warp-optimization-findings.md`, `harness/coverage/registry.toml`,
the pinned KRPC.MechJeb 0.8.1 source
(`ManeuverPlanner.cs`, `Maneuver/OperationInterplanetaryTransfer.cs`).

House rules: ASCII only, no em dashes. Comments explain constraints, not history.
Fail-closed NaN semantics everywhere (a NaN telemetry read never satisfies a
gate).

---

## 0. The B7-vs-B5 delta in one paragraph

B7 is NOT a pure alias like B6. It reuses the entire B5 machine (ascent,
plan/burn phases, DIY correction burner, non-blocking rails-warp control, the
frozen / vessel-lost terminals, the assertion evaluator) and adds FIVE
params, each defaulting to its B5-preserving value so the Mun/Minmus specs stay
byte-identical. The five deltas the params turn on:

1. **Interplanetary transfer plan.** ORBIT / PLAN-TRANSFER ask MechJeb for an
   `OperationInterplanetaryTransfer` (WaitForPhaseAngle) node instead of the moon
   Hohmann `OperationTransfer`. The node lands at the next Kerbin->Duna window,
   up to ~1 synodic period (~20,000,000 game s) ahead.
2. **The ejection-window wait + a HIGH parking orbit.** The wait must be warped;
   an 80 km park caps rails warp at 50x (Kerbin altitude table), making the wait
   ~111 wall-hours. B7 parks at 700 km (>= the 600 km factor-7 limit) so the wait
   warps at 100,000x (~200 wall-s). This is a spec-param change, not a machine
   change.
3. **`viaBodies` coast legality.** The coast legally crosses Kerbin -> Sun ->
   Duna; "Sun" is an intermediate coast body, not the ejected ASSERT-FAIL
   terminal today's machine reads, and it is a legal rails-warp body.
4. **Hyperbolic ejection burn-done gate.** An escape burn drives the Kerbin-frame
   apoapsis NEGATIVE, so `transferMinApoapsisMeters` cannot be the burn-done
   evidence. The evidence becomes a hyperbolic Kerbin-frame eccentricity (>= 1.05
   while still in home SOI) OR the craft having already left home SOI.
5. **Heliocentric correction triggers + a `returnBody` terminal.** Correction
   rounds cannot trigger on Kerbin-altitude mid-heliocentric-coast (the craft is
   in Sun SOI); they trigger on TIME-TO-DUNA-SOI thresholds. A Duna flyby exits
   into Sun SOI (a free-return to Kerbin takes years, out of scope), so RETURN
   fires on body == Sun after the flyby (`returnedToExit=Sun`), not body ==
   Kerbin.

---

## 1. Feasibility

Delta-v survey (todo doc): the Kerbal X orbiter stage holds ~1500-1600 m/s after
an 80 km circularization; a Duna ejection at the window is ~1050-1080 m/s;
feasible with correction margin. B7's HIGH park (section 3) costs a net ~100-150
m/s over the 80 km park (Oberth-offset: the high park raises the orbit for ~300
m/s but the ejection from 700 km is ~150-200 m/s cheaper than from 80 km), so the
correction margin tightens to ~350-450 m/s. Two corrections at ~120 m/s each fit.
**LIVE-PROVE ITEM #1:** fly it and read the post-ascent + post-ejection stage dv;
if the margin is too tight, either lower the park to the minimum factor-7 altitude
(600 km, smaller Oberth penalty) or accept a single correction round. Do not tune
speculatively before the first flight.

---

## 2. Phase flow (the B5 shape, extended)

The phase enum is UNCHANGED (`B5_PRELAUNCH .. B5_RETURN`); B7 reuses it verbatim.
Only the transition CONDITIONS and the terminal body change, all param-gated.

```
PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT
    (MechJeb ascent to the HIGH 700 km park; identical machine, different target
     apoapsis/periapsis. reachedOrbit evidence.)

ORBIT -> PLAN-TRANSFER
    (one-frame waypoint: SET_TARGET_BODY=Duna + the INTERPLANETARY plan action.)

PLAN-TRANSFER -> TRANSFER-BURN
    (a node exists -> hand to the autowarping NodeExecutor. Bounded re-plan every
     planRetrySeconds while node_count==0; budget expiry FLAKES.)

TRANSFER-BURN -> COAST-TO-TARGET
    (the executor consumed the ejection node AND the HYPERBOLIC burn-done gate is
     met: ecc >= ejectionEccFloor in home SOI, OR body already a via/target body.
     The NodeExecutor autowarp carries the ~200-day ejection-window wait here.)

COAST-TO-TARGET  (body in {Kerbin, Sun}; heliocentric cruise)
    - correction rounds trigger on TIME-TO-DUNA-SOI thresholds while body==Sun
      -> PLAN-CORRECTION -> CORRECTION-BURN (DIY native-AP burner, unchanged)
    - body == Duna -> TARGET-FLYBY
    - body not in {"", Kerbin, Sun, Duna} -> ASSERT-FAIL (ejected off-course)

TARGET-FLYBY  (body == Duna; track min altitude = flyby-floor evidence)
    - body == Sun (the returnBody) -> RETURN  (terminal: done, the settle tail
      runs on-rails in heliocentric space)
    - body not in {"", Duna, Sun} -> ASSERT-FAIL (slung off-course)
```

RETURN is the success terminal exactly as in B5; only WHICH body triggers it
changed (Sun for B7, Kerbin for B5/B6). Survival is the contract: any vessel-lost
/ frozen terminal in ANY phase is an ASSERT-FAIL loss (unchanged).

**Frozen-detector note (live-watch, not a change):** during the long
parking-orbit autowarp the orbit's apsides are Keplerian-constant, but
`surface_altitude` and `vertical_speed` vary with the ground track / orbital
phase (and at 100,000x the craft completes many orbits between polls), so the
5-field frozen signature is never bit-identical frame-to-frame. The frozen
detector therefore does not false-trip. B5/B6 already autowarp to their nodes
under the same detector; B7's wait is longer but the same mechanism. Watch the
first flight's telemetry for any bit-identical run anyway.

---

## 3. The ejection-window wait (the central sizing problem)

`OperationInterplanetaryTransfer` with WaitForPhaseAngle plans the ejection node
at the next Kerbin->Duna transfer window: from an arbitrary fixture UT this is
0..1 synodic period ahead. Kerbin->Duna synodic ~= 19,653,075 s (todo doc's Duna
investigation, `synodic=19653075`), so the worst-case wait is ~20,000,000 game s
(~227 Earth days). The NodeExecutor autowarp (TRANSFER-BURN) advances game time to
the node, so `transferBurnTimeoutSeconds` must cover it (25,000,000 game s in the
spec). The WALL cost of that wait is entirely a function of the achievable rails
factor, which KSP caps by altitude:

| park altitude | max Kerbin rails factor | 20,000,000 game s wait |
|---|---|---|
| 80 km (B5/B6 park) | 3 = **50x** | **~111 wall-hours (infeasible)** |
| 300 km | 5 = 1000x | ~5.6 wall-hours (infeasible) |
| 600 km | 7 = 100,000x | ~200 wall-s |
| 700 km (B7 park) | 7 = 100,000x | ~200 wall-s |

(Kerbin `timeWarpAltitudeLimits`: factor 7 = 100,000x needs >= 600,000 m ASL;
`mlib.STOCK_WARP_ALTITUDE_LIMITS["Kerbin"]`. Live-observed: a commanded factor 6
near the 80 km parking orbit ran at 50x, todo doc.)

**Decision: B7 parks at 700 km circular** (comfortably above the 600 km factor-7
limit, with headroom against the surface-vs-ASL altitude gap the machine passes to
`max_legal_rails_factor`). No warp mechanism can exceed the altitude cap - stock
`TimeWarp.WarpTo`, kRPC `WarpTo`, MechJeb's warp controller, and the new
`WARP_TO_UT` primitive all honor `getMaxOnRailsRateIdx` / `GetAltitudeLimit`
(native-warp-to-ut.md sections 1, 3, 4). A high park is therefore the ONLY way to
warp the wait at a tractable rate. An elliptical park does not help: KSP re-clamps
warp to the CURRENT altitude every physics frame, so warp sawtooths down to 50x at
each periapsis pass.

This is a SPEC change only (`targetApoapsisMeters = targetPeriapsisMeters =
700000`); the ascent machine is already param-driven. The ascent budget grows (a
700 km apoapsis coasts longer than 80 km) and the dv margin tightens (section 1).

**Interaction with the `WARP_TO_UT` primitive.** Assuming both the stair fallback
(status quo poll-driven `SET_RAILS_WARP`) and `WARP_TO_UT` exist:
- The ejection-window wait is carried by MechJeb's NodeExecutor autowarp
  (TRANSFER-BURN), the PROVEN far-node regime (B5/B6 flew it for their shorter
  waits). This needs NO new machine code; the 700 km park is what makes it
  wall-tractable.
- `WARP_TO_UT` is an OPTIONAL acceleration/robustness upgrade for the same wait
  (warp to `node_ut - lead`, then let MechJeb burn from close in), and the
  preferred long-term replacement for the poll-driven coast stair. It is NOT on
  the B7 critical path; the budgets below are sized for the autowarp path so B7
  can fly the day the diff plan lands, before `WARP_TO_UT` is wired. When
  `WARP_TO_UT` does land, re-time the ejection wait against it (expected similar
  or better) and trim the budgets.

**LIVE-PROVE ITEM #2:** confirm MechJeb's NodeExecutor autowarp actually reaches
100,000x from the 700 km park and holds it across the ~200-day wait (ramp-up
behavior, transient clamps). If it stalls low, wire `WARP_TO_UT` for this leg.

---

## 4. Design decisions (detail)

### 4.1 Interplanetary transfer plan

MechJeb's `ManeuverPlanner.OperationInterplanetaryTransfer` is a real operation
(pinned source `ManeuverPlanner.cs:27,79` -> `MuMech.OperationInterplanetaryTransfer`).
Its only KRPC surface is `WaitForPhaseAngle` (bool)
(`Maneuver/OperationInterplanetaryTransfer.cs`). Unlike `OperationTransfer` it has
no capture / rendezvous toggles: it plans a SINGLE ejection node at the next
phase-angle window. So B7's TRANSFER-BURN plans ONE node (planned_node_count == 1),
and the existing "node_count fell below the handoff count" consumed-signal works
unchanged. Set `wait_for_phase_angle = True` before `make_nodes()`; same
throw/log/swallow contract as `operation_transfer` (a no-window plan throws
server-side, the machine re-plans on its bounded cadence).

**LIVE-PROVE ITEM #3:** confirm the interplanetary plan yields exactly one node
(no capture/insertion second node); if it plans two, reuse B5's
`planned_node_count` handoff logic (already handles a multi-node plan) and clear
strays at the burn exit (`ACTION_MJ_ABORT_AND_CLEAR_NODES`, already emitted).

### 4.2 viaBodies coast legality

Today COAST-TO-TARGET reads any foreign body (Sun) as the ejected ASSERT-FAIL
terminal (`mlib.py:1995`). B7 adds `via_bodies` (("Sun",)): a via body is exempt
from the ejection check AND is a legal rails-warp body for the coast. The coast
body set becomes `("", home_body, *via_bodies)`; the warp body set (which must
force 1x on an empty reading) is `(home_body, *via_bodies)`. With `via_bodies=()`
(B5/B6 default) both collapse to the current behavior exactly.

### 4.3 Hyperbolic ejection burn-done gate

An escape burn makes the Kerbin-frame orbit hyperbolic: `apoapsis_altitude` goes
negative / undefined, so `transferMinApoapsisMeters` cannot be the burn-done
evidence. **Use eccentricity, not apoapsis sign:** kRPC's `apoapsis_altitude` on a
hyperbolic orbit is ambiguous across regimes (large-negative vs sentinel), while
`eccentricity >= 1` is an unambiguous "this orbit escapes" signal.

The gate: `ejectionEccFloor > 0` selects hyperbolic mode. Burn-done is then
`(body == home_body AND ecc >= ejectionEccFloor)` OR `(body is a via/target body)`
- the second disjunct catches the case where the sample lands AFTER the craft
already left Kerbin SOI (in which case the heliocentric-frame ecc is < 1 and would
falsely fail the first disjunct). `ejectionEccFloor = 1.05` is comfortably above 1
and below any plausible reading noise on a real escape trajectory.
`eccentricity` is already a `TelemetrySnapshot` field, populated by the runner
(`mission_runner.py:242`). With `ejectionEccFloor = 0` (B5/B6 default) the gate is
the unchanged apoapsis floor.

### 4.4 Heliocentric correction trigger (time-to-SOI)

**Why altitude cannot work.** B5/B6 fire correction rounds when the HOME-body
altitude crosses a threshold while `body == home_body` (`mlib.py:2016-2019`). In
B7 the corrections happen mid-heliocentric-coast (body == Sun); the Sun-relative
altitude is billions of metres and never relates to a Kerbin-altitude threshold,
and the `body == home_body` gate excludes the Sun coast entirely. So the altitude
trigger structurally cannot fire the heliocentric corrections.

**Chosen mechanism: TIME-TO-DUNA-SOI thresholds** (`correctionTriggerTimeToSoiSeconds`,
a DESCENDING list). A round fires when `body in via_bodies` (heliocentric, NOT
during the Kerbin escape) AND `time_to_soi` (kRPC `Orbit.TimeToSOIChange`, already
a snapshot field) is finite AND `<= threshold[round]`. Two rounds:
- round 0 @ 20,000,000: `time_to_soi` (~7,000,000 tof) is already below it at the
  first Sun-SOI frame, so it fires immediately on entering heliocentric space -
  fixing the ejection execution error while the whole coast is still ahead;
- round 1 @ 500,000 (~5.8 Earth days before Duna SOI entry): fine-tunes the flyby
  periapsis after the executor residual has accumulated over the long coast (the
  live-proven B5 need for a late refinement, transplanted to the time domain).

**Why time-to-SOI and not distance-to-target.** (a) `time_to_soi` is already in
telemetry and already NaN-fail-closed; distance-to-target would need a NEW snapshot
field (craft-to-Duna range) and a new runner read. (b) `OperationCourseCorrection`
requires an EXISTING target encounter to refine and throws server-side without one;
`time_to_soi` being finite is EXACTLY the "an encounter exists" precondition, so the
trigger and the operation's precondition coincide - a missed-encounter ejection
(no `time_to_soi`) correctly does not fire a doomed correction, and the coast
flakes to retry instead. (c) It is monotone and frame-cheap. Distance-to-target is
non-monotone (it shrinks then, past periapsis, grows) and would need extra logic to
avoid double-firing.

**Gate on `via_bodies`, not the full coast set:** during the brief post-ejection
Kerbin escape, `body == Kerbin` and `time_to_soi` = time to the Kerbin SOI edge
(finite, small); firing a "correction" there would refine the wrong encounter. The
`body in via_bodies` gate confines both the trigger and its warp stair-down to the
heliocentric leg.

### 4.5 returnBody terminal (returnedToExit)

A Duna flyby exits back into Sun SOI; a free-return to Kerbin takes years and is
out of scope. RETURN fires on `body == return_body` after the flyby, where
`return_body = params.return_body or params.home_body` (Sun for B7, Kerbin for
B5/B6). Because COAST-TO-TARGET treats Sun as a VIA body (keep coasting) and
TARGET-FLYBY treats Sun as the RETURN body, the two phases discriminate the same
body name by phase - no ambiguity (the machine only checks `body == return_body`
inside TARGET-FLYBY, which is only reachable after a real Duna encounter).

The `returnedToHome` assertion keeps its NAME (schema stability; run.py reads
`met`/`value` generically and does not key on assertion names), but its reported
value and detail carry `return_body`, so B7's row reads
`returnedToHome value=Sun returnBody=Sun` = "returned to the exit body". See open
question Q1 for the name.

### 4.6 Duna SOI / flyby floor

No machine change: `min_target_altitude` tracking is already body-agnostic. Duna:
radius 320 km, SOI ~47.9 Mm, peaks ~8 km. Spec: `courseCorrectPeriapsisMeters =
50000` (a real flyby geometry above the peaks), `targetPeriapsisFloorMeters =
15000` (assertion floor above the ~8 km peaks). The Duna altitude table clamps
warp hard near a 50 km periapsis (50 km -> factor 2 = 10x, 15 km -> factor 0 =
1x), which the existing per-body clamp + impact guard handle automatically; the
flyby is short.

---

## 5. EXACT mlib diff plan

All line numbers are against the current `harness/missions/lib/mlib.py`. Applying
this is mechanical. With every new param at its default, B1/B2/B4/B5/B6 behavior
is byte-identical (the defaults reproduce the current code paths).

### 5.1 New action constant (after `ACTION_MJ_PLAN_TRANSFER`, line 187)

```python
# B7 interplanetary transfer plan (MechJeb OperationInterplanetaryTransfer with
# WaitForPhaseAngle). Same PLAN_* try/except contract as ACTION_MJ_PLAN_TRANSFER.
ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER = "mj_plan_interplanetary_transfer"  # value None
```

### 5.2 `B5Params` new fields (append before `frozen_sample_limit`, ~line 831)

```python
    via_bodies: Tuple[str, ...] = ()
                                           # legal INTERMEDIATE coast SOI bodies
                                           # (B7: ("Sun",)); exempt from the coast
                                           # ejection check and legal rails-warp
                                           # bodies. () = B5/B6 (no intermediate).
                                           # Spec key viaBodyNames.
    return_body: str = ""                  # terminal EXIT SOI body after the flyby;
                                           # "" -> home_body (B5/B6 free-return).
                                           # B7: "Sun". Spec key returnBodyName.
    interplanetary_transfer: bool = False  # ORBIT/PLAN-TRANSFER use
                                           # OperationInterplanetaryTransfer instead
                                           # of the moon OperationTransfer. B7: True.
                                           # Spec key interplanetaryTransfer.
    ejection_ecc_floor: float = 0.0        # > 0: TRANSFER-BURN burn-done evidence is
                                           # a hyperbolic home-frame ecc (>= this in
                                           # home SOI) OR already-left-home, NOT the
                                           # apoapsis floor. B7: 1.05. 0 = apoapsis
                                           # floor (B5/B6). Spec key ejectionEccFloor.
    correction_trigger_time_to_soi: Tuple[float, ...] = ()
                                           # DESCENDING time-to-target-SOI thresholds
                                           # (game s) for heliocentric correction
                                           # rounds; non-empty SELECTS time mode and
                                           # supersedes correction_trigger_alts.
                                           # B7: (20_000_000, 500_000). () = altitude
                                           # mode (B5/B6). Spec key
                                           # correctionTriggerTimeToSoiSeconds.
```

### 5.3 `b5_params_from_dict` additions (inside the return, ~line 870)

```python
        via_bodies=tuple(str(b) for b in params.get("viaBodyNames", ())),
        return_body=str(params.get("returnBodyName", "")),
        interplanetary_transfer=bool(params.get("interplanetaryTransfer", False)),
        ejection_ecc_floor=float(params.get("ejectionEccFloor", 0.0)),
        correction_trigger_time_to_soi=tuple(
            float(t) for t in params.get("correctionTriggerTimeToSoiSeconds", ())),
```

### 5.4 New pure helpers (near the other `_b5_*` helpers, ~line 1633)

```python
def _b5_return_body(params: B5Params) -> str:
    """The terminal exit SOI body: return_body if set, else home_body (B5/B6
    free-return)."""
    return params.return_body or params.home_body


def _b5_coast_bodies(params: B5Params) -> Tuple[str, ...]:
    """Bodies whose presence in COAST-TO-TARGET is NOT an ejection: "" (no
    reading), the home body, and every via body."""
    return ("", params.home_body) + params.via_bodies


def _b5_warp_bodies(params: B5Params) -> Tuple[str, ...]:
    """Bodies over which the coast may rails-warp (home + via). Excludes "": an
    empty reading forces 1x."""
    return (params.home_body,) + params.via_bodies


def _b5_transfer_plan_action(params: B5Params) -> Action:
    """The transfer plan action: interplanetary (WaitForPhaseAngle) when
    interplanetary_transfer, else the moon Hohmann transfer."""
    if params.interplanetary_transfer:
        return Action(ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)
    return Action(ACTION_MJ_PLAN_TRANSFER)


def _b5_transfer_burn_done(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """TRANSFER-BURN burn-done evidence. B5/B6: the home-frame apoapsis reached
    transfer_min_apoapsis. B7 (ejection_ecc_floor > 0): a HYPERBOLIC home-frame
    eccentricity (>= floor while still in the home SOI) OR the craft ALREADY left
    the home SOI (body is a via / the target). NaN ecc fails closed."""
    if params.ejection_ecc_floor > 0.0:
        if snapshot.body in params.via_bodies or snapshot.body == params.target_body:
            return True
        return (snapshot.body == params.home_body
                and _is_finite(snapshot.eccentricity)
                and snapshot.eccentricity >= params.ejection_ecc_floor)
    return (_is_finite(snapshot.apoapsis)
            and snapshot.apoapsis >= params.transfer_min_apoapsis)


def _b5_correction_triggers(params: B5Params) -> Tuple[float, ...]:
    """The active correction-round trigger list: the time-to-SOI list when set
    (B7), else the altitude list (B5/B6)."""
    return params.correction_trigger_time_to_soi or params.correction_trigger_alts


def _b5_rounds_pending(state: B5State) -> bool:
    """True iff more correction rounds may still fire (corrections enabled and
    fewer rounds done than triggers)."""
    return (state.params.course_correct_periapsis > 0.0
            and state.correction_rounds_done < len(_b5_correction_triggers(state.params)))


def _b5_correction_round_ready(state: B5State, snapshot: TelemetrySnapshot) -> bool:
    """True iff the current correction round's trigger has fired this frame.
    TIME mode (B7): body is a via body AND time_to_soi finite AND <= the round's
    threshold (fires in heliocentric space, never during the home-SOI escape).
    ALTITUDE mode (B5/B6): body == home AND altitude finite AND >= the round's
    threshold. Both NaN-fail-closed."""
    p = state.params
    if not _b5_rounds_pending(state):
        return False
    idx = state.correction_rounds_done
    if p.correction_trigger_time_to_soi:
        return (snapshot.body in p.via_bodies
                and _is_finite(snapshot.time_to_soi)
                and snapshot.time_to_soi <= p.correction_trigger_time_to_soi[idx])
    return (snapshot.body == p.home_body
            and _is_finite(snapshot.altitude)
            and snapshot.altitude >= p.correction_trigger_alts[idx])
```

### 5.5 `b5_decide` branch edits

**ORBIT (lines 1819-1827):** choose the plan action by param.
```python
        return entered, [
            Action(ACTION_SET_TARGET_BODY, text=state.params.target_body),
            _b5_transfer_plan_action(state.params),          # was Action(ACTION_MJ_PLAN_TRANSFER)
        ]
```

**PLAN-TRANSFER (lines 1829-1834):** re-plan with the same param-chosen action.
```python
    if state.phase == B5_PLAN_TRANSFER:
        return _b5_plan_phase(
            state, snapshot, peak,
            plan_action=_b5_transfer_plan_action(state.params),   # was Action(ACTION_MJ_PLAN_TRANSFER)
            burn_phase=B5_TRANSFER_BURN,
            on_timeout_phase=None)
```

**TRANSFER-BURN (line 1850-1851):** replace the apoapsis floor with the helper.
```python
        consumed = snapshot.node_count < max(state.planned_node_count, 1)
        floor_met = _b5_transfer_burn_done(state.params, snapshot)   # was the inline apoapsis floor
```
(The rest of the TRANSFER-BURN branch - the `(consumed or stuck) and floor_met`
exit, the cleanup, the under-burn flake - is UNCHANGED. For B7 the under-burn flake
now means "the ejection did not make the orbit hyperbolic".)

**COAST-TO-TARGET (lines 1992-2088):** four edits.

(a) ejection body gate (lines 1995-2004): allow via bodies.
```python
        if snapshot.body not in _b5_coast_bodies(state.params):   # was ("", home_body)
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("left home SOI without reaching the target: body=%r "
                             "(allowed %r, target %r)"
                             % (snapshot.body, _b5_coast_bodies(state.params),
                                state.params.target_body))), []
```

(b) correction-round entry (lines 2008-2027): use the mode-aware readiness helper.
```python
        if _b5_correction_round_ready(state, snapshot):           # was the alt-only inline gate
            entered = _b5_enter(state, B5_PLAN_CORRECTION, snapshot.ut, peak)
            entered = replace(entered,
                              last_plan_ut=snapshot.ut if _is_finite(snapshot.ut) else 0.0,
                              warp_cmd=0)
            prelude = ([Action(ACTION_SET_RAILS_WARP, 0.0)] if state.warp_cmd != 0 else [])
            return entered, prelude + [Action(ACTION_MJ_PLAN_COURSE_CORRECT,
                                              state.params.course_correct_periapsis,
                                              limit=state.params.max_correction_dv)]
```

(c) warp body gate + correction stair (lines 2034-2054): allow via bodies and add
the time-mode stair.
```python
        rounds_pending = _b5_rounds_pending(state)
        p = state.params
        if snapshot.body not in _b5_warp_bodies(p):               # was body != home_body; forces 1x on "" + foreign
            desired = 0
        elif snapshot.node_count != 0:
            # warp TOWARD a pending node (UNCHANGED)
            if _is_finite(snapshot.node_ut) and _is_finite(snapshot.ut):
                desired = rails_factor_for_time(
                    snapshot.node_ut - p.node_warp_lead - snapshot.ut, p.coast_warp_factor)
            else:
                desired = 0
        elif (rounds_pending and p.correction_trigger_time_to_soi
                and snapshot.body in p.via_bodies and _is_finite(snapshot.time_to_soi)):
            # TIME mode (B7): stair down toward the next round's time-to-SOI
            # threshold so the machine reaches ~1x AT the trigger and never warps
            # past a correction window. Confined to via bodies (the Kerbin escape
            # warps at full coast factor, bounded only by the SOI-approach clamp).
            dt = snapshot.time_to_soi - p.correction_trigger_time_to_soi[state.correction_rounds_done]
            desired = rails_factor_for_time(dt, p.coast_warp_factor)
        elif (rounds_pending and not p.correction_trigger_time_to_soi
                and _is_finite(snapshot.altitude)):
            # ALTITUDE mode (B5/B6, UNCHANGED distance stair)
            dist = p.correction_trigger_alts[state.correction_rounds_done] - snapshot.altitude
            desired = rails_factor_for_distance(dist, snapshot.vertical_speed, p.coast_warp_factor)
        else:
            desired = p.coast_warp_factor
```
(The SOI-approach time bound at lines 2055-2067 and the altitude-legality clamp at
2068-2076 and the on-change emission at 2077-2088 are UNCHANGED. The SOI-approach
bound - floored at flybyWarpFactor - is what crosses both the Kerbin->Sun and
Sun->Duna boundaries at ~100x, bounding the per-poll overshoot; this is the
existing B7-Duna-hazard mitigation from the warp-optimization pass.)

**TARGET-FLYBY (lines 2090-2104):** the terminal + ejection body.
```python
    if state.phase == B5_TARGET_FLYBY:
        return_body = _b5_return_body(state.params)               # Sun for B7, Kerbin for B5/B6
        if snapshot.body == return_body:                          # was == home_body
            entered = _b5_enter(state, B5_RETURN, snapshot.ut, peak)
            entered = replace(entered, warp_cmd=0)
            return entered, ([Action(ACTION_SET_RAILS_WARP, 0.0)]
                             if state.warp_cmd != 0 else [])
        if snapshot.body not in ("", state.params.target_body, return_body):   # was ("", target)
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("flyby ejected the craft off-course: body=%r "
                             "(expected %r or exit %r)"
                             % (snapshot.body, state.params.target_body, return_body))), []
```
(The flyby warp stair below - the target-body impact guard + flyby stair - is
UNCHANGED. Adding `return_body` to the allowed set is a no-op for B5/B6 since
`return_body == home_body` is already handled by the RETURN branch above.)

### 5.6 `evaluate_b5_assertions` (lines 2398-2403): report the return body

```python
    return_body = params.return_body or params.home_body
    ret_met = B5_RETURN in phases
    ret = AssertionOutcome("returnedToHome", ret_met,
                           (return_body if ret_met else None),
                           {"required": B5_RETURN, "returnBody": return_body})
```
(Name unchanged for schema/result-diff stability; value + detail now name the
actual exit body. See Q1.)

### 5.7 Runner action handler (mission_runner.py - OWNED BY ANOTHER AGENT; do not
edit here, this is the spec for that agent)

Add alongside the `ACTION_MJ_PLAN_TRANSFER` case (~line 397):
```python
        elif kind == mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER:
            # KRPC.MechJeb 0.8.1 maneuver_planner.operation_interplanetary_transfer
            # (pinned source ManeuverPlanner.cs:79 OperationInterplanetaryTransfer
            # KRPCProperty -> MuMech.OperationInterplanetaryTransfer; the only
            # surface is WaitForPhaseAngle, Maneuver/OperationInterplanetaryTransfer.cs).
            # WaitForPhaseAngle=True plans the ejection node at the next transfer
            # window (up to ~1 synodic ahead). Same throw/log/swallow contract as
            # operation_transfer: a no-window plan throws server-side, node_count
            # stays 0, and the machine's bounded re-plan owns the retry.
            try:
                op = self._mechjeb.maneuver_planner.operation_interplanetary_transfer
                op.wait_for_phase_angle = True
                op.make_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan",
                    "operation_interplanetary_transfer.make_nodes failed: %s" % (exc,)))
```
No telemetry change is needed: `eccentricity` and `time_to_soi` are already read
(`mission_runner.py:242,280`).

### 5.8 Tests

**`harness/missions/lib/test_mlib.py`** (new cells in a `B7InterplanetaryTests`
class; pattern = the existing `B5MachineTests` / `_b5_state` helper). A B7 params
fixture mirrors `B5_PARAMS` with `interplanetary_transfer=True`,
`via_bodies=("Sun",)`, `return_body="Sun"`, `ejection_ecc_floor=1.05`,
`transfer_min_apoapsis=0.0`, `correction_trigger_alts=()`,
`correction_trigger_time_to_soi=(20_000_000.0, 500_000.0)`,
`target_body="Duna"`. New assertions to cover every branch edit:

1. `test_orbit_emits_interplanetary_plan`: from ORBIT, the emitted actions include
   `ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER` (not `ACTION_MJ_PLAN_TRANSFER`), and
   B5 params still emit `ACTION_MJ_PLAN_TRANSFER`.
2. `test_hyperbolic_burn_done_gate`: in TRANSFER-BURN, a snapshot with
   `body="Kerbin", eccentricity=1.2, node_count<planned` exits to COAST; `ecc=0.9`
   does NOT; `body="Sun"` (already left home) exits regardless of ecc; a NaN ecc
   in home SOI does NOT exit (fail closed). And a B5 params snapshot still exits
   on the apoapsis floor, not ecc.
3. `test_via_body_is_not_ejection`: in COAST, `body="Sun"` stays in phase (no
   ASSERT-FAIL); a truly foreign `body="Eve"` still ASSERT-FAILs.
4. `test_coast_warps_over_via_body`: in COAST with `body="Sun"`, no pending node,
   no round pending, a `SET_RAILS_WARP` with the coast factor is emitted (not
   forced to 1x); `body=""` still forces 1x.
5. `test_time_to_soi_correction_trigger`: in COAST `body="Sun"`,
   `time_to_soi=6_000_000` (< round-0 threshold 20M) enters PLAN-CORRECTION;
   `body="Kerbin"` with a small `time_to_soi` (Kerbin escape) does NOT trigger;
   after round 0, `time_to_soi=400_000` (< round-1 threshold 500k) triggers round
   1; `time_to_soi=NaN` never triggers.
6. `test_time_mode_correction_warp_stairs_down`: in COAST `body="Sun"`, round
   pending, as `time_to_soi` approaches the round threshold the commanded factor
   stairs down (assert `rails_factor_for_time` on `time_to_soi - threshold`), and
   1x is reached at the threshold.
7. `test_flyby_returns_to_exit_body`: in TARGET-FLYBY, `body="Sun"` (return_body)
   enters RETURN (done, verdict None); `body="Kerbin"` (not the exit) ASSERT-FAILs;
   a B5 params flyby still RETURNs on `body="Kerbin"`.
8. `test_returned_assertion_reports_exit_body`: `evaluate_b5_assertions` with B7
   params + RETURN in phases -> `returnedToHome` value == "Sun", detail
   `returnBody=="Sun"`; B5 params -> value == "Kerbin".
9. `test_full_happy_path_walk_b7`: a full frame script PRELAUNCH -> ... -> RETURN
   across the Kerbin->Sun->Duna->Sun body sequence, asserting `phases_reached`
   and a MISSION-OK verdict from `resolve_flight_verdict` + all four assertions.
10. `test_b5_paths_unchanged_with_b7_defaults`: a parametrized re-run of a couple
    of existing B5 cells confirming the new default params (all off) leave the B5
    transitions byte-identical (guards the "defaults preserve B5" contract).

**`harness/missions/lib/test_shells.py`:** add `import b7_duna_flyby` to the shell
imports (line 36-37), add it to the no-top-level-krpc-import assertion
(`test_shells_have_no_module_top_krpc_import`), and a `B7_PARAMS` dict mirroring
`B5_PARAMS` with the five new keys so the shell `build_state` / `decide` /
`evaluate` round-trip is exercised (the FakeMissionControl happy-path pattern).

**Schema-registry test (if present):** confirm `b7_duna_flyby.schema.toml` parses
and the B7 spec validates against it (the harness's spec-validation test over
`harness/scenarios/*.toml`).

---

## 6. Budgets (all ESTIMATED; see the scenario for the arithmetic)

GAME-time phase budgets (missionParams): `transferBurnTimeoutSeconds=25,000,000`
(the ~20M ejection-window autowarp + burn), `coastTimeoutSeconds=12,000,000` (~7M
tof + margin), `flybyTimeoutSeconds=500,000` (Duna SOI transit), ascent 1200 /
circularize 600 (the 700 km park). WALL budgets (steps/runtime, assuming the
100,000x autowarp): mission phase 3600 s (dominant term ~200 s ejection wait + ~70
s coast + ~860 s corrections + ~400 s flyby + ascent/margin), runtime 4200 s (240
LoadGame + 3600 + margin). Retry policy: once (first flights are tuning flights).

---

## 7. Coverage

`dimensionsCovered` cites only registry values (`harness/coverage/registry.toml`):
`D1=[auto-record-launch]`, `D3=[orbital-checkpoint]`,
`D4=[atmospheric, exo-propulsive, exo-ballistic, cohesive-cross-body-coast]`,
`D14=[kerbin, duna, soi-count, warp-rails, warp-high]`. The Kerbin->Sun->Duna->Sun
traverse is the multi-SOI cohesive cross-body coast + `soi-count`; the heliocentric
coast reaches 100,000x = `warp-high`.

**Registry gaps (noted, NOT citable):** D14 has no `sun` value (the heliocentric
frame is uncited beyond `soi-count`), and there is no dedicated
interplanetary / flyby / heliocentric-coast dimension value (same gap class as
B5's flyby gap). If a value is added, grow `registry.toml` in the same PR (growth
rule N9).

---

## 8. Open design questions (with recommendations)

**Q1. Assertion name: keep `returnedToHome` or rename to `returnedToExit`?**
RECOMMENDATION: keep the name, report `return_body` as the value/detail. run.py
reads `met`/`value` generically and does not key on assertion names, and B5/B6
golden result diffs stay stable. A rename is purely cosmetic and would churn the
B5/B6 result fixtures. If a reviewer wants the clearer name, do it as a coordinated
rename across all three specs' expected results in one PR.

**Q2. HIGH park (700 km) vs the 80 km B5/B6 park - dv feasibility.**
RECOMMENDATION: park at 700 km (section 3); it is the only wall-tractable way to
warp the ejection wait. The dv margin tightens to ~350-450 m/s; fly it and read the
stage dv (LIVE-PROVE #1). Fallback if too tight: drop to the minimum factor-7
altitude (600 km) for a smaller Oberth penalty, or cut to a single correction
round. This is the highest-risk B7 assumption; do not build alternatives before the
first flight measures the margin.

**Q3. Does MechJeb's NodeExecutor autowarp hold 100,000x across a ~200-day wait
from the 700 km park?** RECOMMENDATION: assume yes (its warp controller honors the
altitude limit, which permits factor 7 at 700 km) and budget for it; if the first
flight shows it stalling below 100,000x, wire the `WARP_TO_UT` primitive for the
TRANSFER-BURN leg (warp to `node_ut - lead`, then MechJeb burns from close in).
LIVE-PROVE #2.

**Q4. Does `OperationInterplanetaryTransfer` plan one node or two?**
RECOMMENDATION: assume ONE (it has no capture toggle, unlike `OperationTransfer`),
so `planned_node_count == 1` and the existing consumed-signal works. The machine
already tolerates a multi-node plan (`planned_node_count` handoff + stray-clear at
the burn exit), so a two-node plan degrades gracefully. LIVE-PROVE #3.

**Q5. Early correction when the ejection produced no encounter (NaN
time_to_soi).** RECOMMENDATION: accept that the time-to-SOI trigger only fires when
an encounter exists (which is also `OperationCourseCorrection`'s precondition), so
a grossly-off ejection with no encounter never fires a doomed correction and the
coast flakes to a retry instead. This is the correct fail-closed behavior; do NOT
add a time-since-ejection fallback trigger (it would fire a correction with no
encounter to refine, which throws server-side anyway). If live flights show the
targeted interplanetary plan reliably yields an encounter (expected), Q5 is moot.

**Q6. Fixture UT / window proximity.** The shared `b2-lko-craft` fixture's UT sets
how far ahead the next Duna window is (0..1 synodic). RECOMMENDATION: keep the
shared fixture and budget for the synodic worst case (done); a fixture near a
window only flies faster. Do NOT mint a B7-specific fixture unless the first flight
shows the worst-case wait blowing the wall budget even at 100,000x (it should not:
20M game s / 100,000x = 200 s).
