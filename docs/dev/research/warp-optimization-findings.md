# Warp optimization for autopilot-flown test missions (B5/B6, projected B7)

Date: 2026-07-22. Branch: `warp-optimization`. Scope: minimize REAL (wall
clock) time the B5/B6 flyby machine spends on inter-body transfers, without
regressing any of the 16-flight hard-won guards (fail-closed NaN semantics,
on-change + self-healing warp emissions, bounded give-ups, impact-warp guard,
ALIGNED_DEBOUNCE_FRAMES). Every claim below is source-verified; citations name
the file + member.

All wall-time figures assume the 0.5 s poll cadence
(`mission_runner.POLL_INTERVAL_SECONDS`) and the stock rails rate table
`mlib.RAILS_WARP_RATES = (1, 5, 10, 50, 100, 1000, 10000, 100000)`.

## 1. Altitude clamping (the silent 50x coast)

### Source facts

- kRPC `SpaceCenter.RailsWarpFactor` setter (pinned kRPC 0.5.4,
  `service/SpaceCenter/src/Services/SpaceCenter.cs`): zeroes the throttle,
  then `SetWarpFactor(HIGH, value.Clamp(0, MaximumRailsWarpFactor))`.
- `CanRailsWarpAt(factor)` (same file): landed -> true; else false when
  `vessel.mainBody.GetAltitude(CoM) < TimeWarp.GetAltitudeLimit(factor, body)`;
  else false when `FlightInputHandler.state.mainThrottle > 0`; else true.
- `TimeWarp.GetAltitudeLimit(i, body)` (decompiled KSP 1.12.5
  Assembly-CSharp `TimeWarp`): `return body.timeWarpAltitudeLimits[i];` --
  the RAW per-body table, no atmosphere fold-in. (The cannot-rails-in-
  atmosphere rule is a separate stock gate; our missions only command rails
  warp exo, so it never bites.)
- `MaximumRailsWarpFactor` (kRPC): loops `i` from 7 DOWN TO 2 and returns the
  first `CanRailsWarpAt(i)`; **it never returns 1** (loop bound `i > 1`). On
  stock tables `limits[1] == limits[2]` for every body except Jool, so the
  only practical zero cases are throttle > 0 and altitude below `limits[2]`.
- KSP's own `TimeWarp.SetRate` clamps the same way when
  `GameSettings.ORBIT_WARP_MAXRATE_MODE == VesselAltitude` (the default):
  `tgtRateIdx = GetMaxRateForAltitude(altitude, mainBody)` (decompiled
  `TimeWarp.SetRate`). KSP auto-REDUCES warp when a descending vessel crosses
  a limit, but **never auto-raises** it as the vessel climbs.

### Ground-truth per-body tables

Extracted 2026-07-22 from the dev install's serialized
`CelestialBody.timeWarpAltitudeLimits` arrays (KSP 1.12.5
`KSP_x64_Data/sharedassets9.assets`, PSystem prefab; all 17 bodies mapped by
the adjacent `bodyName` string at a consistent 1112-byte object stride).
Now embedded as `mlib.STOCK_WARP_ALTITUDE_LIMITS`. The mission-relevant rows
(metres ASL, index = rails factor index; legality is altitude >= limit):

| Body   | 5x/10x | 50x    | 100x    | 1000x   | 10000x  | 100000x |
|--------|--------|--------|---------|---------|---------|---------|
| Kerbin | 30,000 | 60,000 | 120,000 | 240,000 | 480,000 | 600,000 |
| Mun    | 5,000  | 10,000 | 25,000  | 50,000  | 100,000 | 200,000 |
| Minmus | 3,000  | 6,000  | 12,000  | 24,000  | 48,000  | 60,000  |
| Duna   | 30,000 | 60,000 | 100,000 | 300,000 | 600,000 | 800,000 |
| Ike    | 5,000  | 10,000 | 25,000  | 50,000  | 100,000 | 200,000 |
| Sun    | 3.27M  | 6.54M  | 13.08M  | 26.16M  | 52.32M  | 65.4M   |

### What a clamped set actually produces

- If some factor >= 2 is altitude-legal: RAILS at the **clamped lower rate**.
  `warp_mode` reads RAILS, so the self-healing re-emit does NOT fight it --
  and also never fixes it. Because the on-change discipline only re-emits
  when `desired` changes, the machine held the clamped rate for the whole
  leg: a commanded factor 6 (10,000x) at the ~80-120 km post-TLI altitudes
  ran at **50x** (Kerbin legal max is factor 3 below 120 km) until the stair
  next changed the desired factor.
- If nothing >= factor 2 is legal (or throttle > 0): `MaximumRailsWarpFactor`
  is 0, the set becomes `SetRate(0)`, `warp_mode` reads NONE, and the
  self-healing re-emit fires every poll (idempotent but a per-poll RPC fight
  at 1x, each re-emit also zeroing the throttle).

### Fix (implemented)

`mlib.max_legal_rails_factor(body, altitude)` mirrors the clamp client-side;
COAST and TARGET-FLYBY take `min(stair, legal)`. Commanded == achievable, so
every limit crossing changes `desired` and the on-change emission ESCALATES
the factor as the vessel climbs (30 km -> 60 -> 120 -> 240 -> 480 -> 600 on
Kerbin). Fail-open (factor 7) for unknown bodies / non-finite altitude: the
server clamp is the hard backstop, and a one-frame altitude blip must not
sawtooth a held warp to 1x. Note the machine feeds SURFACE altitude while the
game compares sea-level altitude; surface <= ASL everywhere, so the mismatch
only ever under-commands by terrain height.

Expected wall saving (B5/B6): the ~5,900 km Kerbin leg between correction
round 1 (~80-100 km) and round 2 (6,000 km) is ~2-3 h of game time. At the
old effective 50x that is 150-210 wall-s; with proper escalation
(50x -> 100x -> 1000x -> 10,000x as altitude passes each limit) it collapses
to ~25-40 wall-s. **Saves roughly 2-3 wall-minutes per mission**, more on B6
(longer Kerbin leg to the 20,000 km trigger).

## 2. Wasted 1x segments and the physics-warped flip

Timeline audit of the fifteenth (passing) B5 flight class:

| Segment                        | Old behavior      | Disposition |
|--------------------------------|-------------------|-------------|
| MechJeb ascent + circularize   | 1x, ~350 s        | Left alone (proven; in-atmosphere rails is illegal, and MechJeb owns its own warp) |
| PLAN-TRANSFER node wait        | seconds           | Left alone |
| TLI executor autowarp          | MechJeb-owned     | Left alone (see section 5) |
| Correction flip (per round)    | 1x, up to ~340 s  | **2x physics warp** (implemented) |
| Correction settle + burn       | 1x, ~10-60 s      | Left at 1x (cut precision) |
| Coast legs                     | clamped rails     | Section 1 + 3 |
| Flyby SOI transit              | flat 100x         | Section 4 |
| RETURN settle tail             | on-rails already  | Left alone |

The flip is the dominant controllable 1x block: the Kerbal X pod wheel flips
at ~0.5 deg/s under the restored (15,15,15) `deceleration_time` (finding 11),
so a near-anti-parallel start is ~340 game-s at 1x, twice per mission.

**Physics-warp evidence:** MechJeb's own `MechJebModuleWarpController.WarpToUT`
(decompiled 2.15.1) runs `WarpPhysicsAtRate((float)Math.Min(x, 2.0))` whenever
rails is not available -- 2.0x is MechJeb's own ceiling for physics warp under
active attitude control. Its `NodeExecutor.StateLeadTime` holds MinimumWarp
(1x) inside the 3 s lead, and `StateWarpAlign` disables axis control during
far rails warp -- i.e. MechJeb never rails-warps a live flip, but considers
<= 2x physics safe. kRPC `PhysicsWarpFactor` setter clamps to [0, 3] and
needs no altitude legality (`SetWarpFactor(LOW, value.Clamp(0, 3))`).

**Implemented:** new action `set_physics_warp` (value = physics factor INDEX;
1 = 2x). CORRECTION-BURN's pre-burn (flip/settle) segment holds
`flipPhysicsWarpFactor` (default 1) with the same on-change + self-healing
discipline as rails; the aligned-gate-open frame first commands physics warp
0 and the throttle fires only on a subsequent frame that reads warp NONE (the
burn never integrates at scaled dt). Every exit path (cut, overshoot,
no-progress, node-vanished, give-up, budget flake) drops physics warp.
`flipPhysicsWarpFactor = 0` reverts byte-identically to the proven 1x flip --
that is the live-tuning lever if the kRPC AP misbehaves at 2x on the real
craft. The B5/B6 warp guard already allows PHYSICS to 4x
(`max_physics_warp=4.0` in `b5_mun_flyby.py`), so no guard change.

Expected wall saving: flip game-time is unchanged but runs at 2x real rate:
**up to ~170 wall-s per correction round, ~4-5 wall-minutes per mission with
two long flips** (proportionally less when the AP starts near-aligned).

## 3. Warp-to-node + SOI stair-down (the time-based primitive)

### rails_factor_for_time

`mlib.rails_factor_for_time(dt_s, cap)`: highest factor whose RATE x 1 s
safety window (`_WARP_SAFETY_SECONDS`, two 0.5 s polls) fits inside the
remaining GAME seconds -- the TIME sibling of `rails_factor_for_distance`.
This is exactly the stair MechJeb's non-quick WarpToUT uses
(`x = 1.0 * (UT - now)` clamped to the rate table). Non-blocking by
construction: the machine keeps polling, so the finding-4 dialog-wedge class
and the per-hop ramp sawtooth stay structurally gone.

### Warp toward a pending node (operator directive 2026-07-22)

COAST-TO-TARGET previously forced 1x whenever `node_count != 0` -- correct
for the seconds-long plan->burn handoff today, but a stray node would have
1x'd a whole coast, and B7's ejection-window wait (potentially DAYS of game
time) would be impossible. Now: with a pending node the coast warps at
`rails_factor_for_time(node_ut - nodeWarpLeadSeconds - now)`, holding 1x
only inside the lead window (default 120 s: room for the flip + settle
before the burn gate) or when `node_ut` is NaN (fail closed -- never warp
past a burn on no evidence). New telemetry: `TelemetrySnapshot.node_ut`
(kRPC `Node.UT`, pinned source `Node.cs`; NaN with no node).

Wall cost of a full stair-down from days out: the descent spends at most
~1 wall-s per factor step plus the 1x lead window -- negligible against the
leg itself.

### SOI-approach bound

The 0.5 s poll catches the body change only AFTER the SOI entry; at a held
10,000x that is up to ~5,000 game-s of overshoot INTO the SOI. For B5/B6
this was verified harmless-but-sloppy: Mun SOI radius is 2,429,559 m, so at
typical ~200-800 m/s approach speeds 5,000 s can be 1,000-4,000 km -- deep
enough to eat into the pre-periapsis evidence leg, though not to skip the
SOI. For B7 Duna at factor 7 (100,000x) it is mission-fatal: 50,000 game-s
per poll at 1-3 km/s crosses a comparable distance to the whole Duna SOI
(radius 47,921,949 m) -- the craft could blow through between two polls.

New telemetry `TelemetrySnapshot.time_to_soi` (kRPC `Orbit.TimeToSOIChange`,
pinned source `Orbit.cs`: `UTsoi - UT`, NaN when no SOI change; NaN fails
OPEN -- no encounter, nothing to overshoot). COAST now takes
`min(desired, max(rails_factor_for_time(time_to_soi), flybyWarpFactor))`:
the factor stairs down toward the boundary but never below the flyby factor,
so the boundary crosses at ~100x (~100 game-s overshoot, tens of km -- noise
against any SOI) with no 1x cliff, and TARGET-FLYBY inherits its own held
factor seamlessly. Wall cost for B5: ~20-30 s per approach; B7 policy: the
same bound makes a factor-7 heliocentric coast safe with zero extra
machinery (the stair spends ~9 wall-s per decade of remaining time).

## 4. Flyby factor (100x floor, stair on the outer legs)

Flat 100x through the Mun SOI costs real minutes for nothing: the transit is
6,000-12,000 game-s, of which only the ~10-15 minutes around periapsis carry
the min-altitude evidence resolution that 100x buys (samples every ~50
game-s at the 0.5 s poll). TARGET-FLYBY now stairs
`rails_factor_for_distance(altitude - max(periapsis, 0), vertical_speed,
flybyMaxWarpFactor)` and takes `max(flybyWarpFactor, stair)` -- i.e. the
proven 100x cadence is a FLOOR that still owns the periapsis passage, while
the outer legs run 1000x+ (typically factor 5-6 beyond ~400 km from
periapsis at real approach speeds). The per-body legality clamp applies here
too and matters on real geometry: Mun 100x needs >= 25 km and Minmus 100x
needs >= 12 km, so a legitimately low flyby now commands the achievable
50x/10x instead of silently fighting the server clamp. The impact-warp guard
(sub-surface periapsis below 400 km -> 1x) is untouched and evaluated FIRST.

Expected wall saving: **~1-3 wall-minutes per flyby** (both SOI legs), with
identical evidence quality near periapsis.

## 5. TLI executor autowarp (left alone)

Decompiled 2.15.1 `MechJebModuleNodeExecutor`: `StateWarpAlign` warps to
`ignitionUT - LeadTime` once `AlignedAndSettled()`, or (far case, > 600 s
out) disables axis control and warps to `ignitionUT - 600`; inside 600 s it
holds 1x and aligns. `LeadTime` defaults to 3.0 s. On the Kerbal X the
low-torque alignment inside that last 600 s window costs up to ~340 s at 1x
once per mission -- the same flip cost as section 2, but INSIDE the proven
executor path whose fragility findings 1-12 document exhaustively.
Judgment: not worth touching. The theoretical ~2-3 wall-minute saving does
not justify perturbing the one MechJeb integration that has worked on all
sixteen flights. (If it ever becomes the bottleneck, the executor's own
KRPC.MechJeb `lead_time` is settable -- but the 600 s align window is
hardcoded, so the win is capped and the risk concentrated.)

## Expected totals

| Mission | Old warp wall profile | Expected saving |
|---------|----------------------|-----------------|
| B5 Mun | ~2-3 min clamped coast + ~2x2-3 min 1x flips + ~2-4 min flat-100x flyby | **~5-9 min** of the ~10-17 min pass wall time |
| B6 Minmus | same classes, longer Kerbin leg + 9-day coast | **~6-10 min** |
| B7 Duna (projected) | without these fixes: ~300-day transfer at a clamped/flat factor + SOI blow-through risk | factor-7 coast becomes SAFE (~5-10 min wall for the whole heliocentric leg via the time stair); ejection-window waits warp instead of idling at 1x |

## Live-tuning expectations for the next flight

- Watch the first correction flip under `warp=PHYSICSx2`: if the kRPC AP
  limit-cycles or the apErr decay stalls versus the known ~0.5 deg/s
  profile, set `flipPhysicsWarpFactor = 0` in the spec (byte-identical
  revert) and re-fly.
- The telemetry line now logs `nodeUt=` and `tts=`; grep those against the
  commanded `action set_rails_warp` lines to verify the stair/legality
  decisions against the game's actual `warp=RAILSxN` readback.
- The COAST stair should now show factor escalation 3 -> 4 -> 5 -> 6 in the
  minutes after TLI (altitude 120/240/480/600 km crossings). If the log
  shows RAILS at a LOWER rate than the commanded factor for more than a few
  ramp seconds, the legality table disagrees with the install -- re-extract
  (modded system?) before touching the machine.
- `MaximumRailsWarpFactor`'s no-factor-1 quirk is documented above; the
  machine can still command factor 1 legitimately (when factor 2+ is legal),
  which clamps fine. No stock-body geometry produces the pathological
  "only 5x legal" window (limits[1] == limits[2] everywhere we fly).
