# Mining and ISRU test program (2026-10-17)

Owner ask (V, 2026-10-17): cover every drill and ISRU test idea already written down, plus new
ones, and fly them on real craft. The lab (`ModuleScienceLab`) is out of scope here.

## Where things stand

- Live today: MINE-2 (`2026-10-17_1242`, PASS): V's rover runs four drills and the ISRU through
  an hour of rails warp, commits, rewinds to launch, and the spawned rover's tanks equal its
  recording's end snapshot after a Space Center warp and after a FLIGHT time jump. LAB-1
  (converter clocks across both jump kinds, unloaded single-part vessels), HV-2 (scan ledger,
  drill / ISRU rates read from prefabs), H37 (converter loop on a prefab-built ghost).
- Product surfaces (file:line in the inventory pass, 2026-10-17): converter and animation-group
  part events (`FlightRecorder.CheckConverterState`, `CheckAnimationGroupState`, background
  twins in `BackgroundRecorder.PartEventPolling.cs`), harvest windows
  (`PollHarvestActivity`, `HandleHarvestRailsEntry`, `RouteHarvestCapture`), seeds at
  recording start (`PartStateSeeder`), ghost drill pose and converter loop
  (`GhostVisualBuilder`, `GhostPlaybackLogic`), tail trim and split rules
  (`RecordingOptimizer.TailTrim`, harvest-window merge / split), `ConverterJumpPolicy`,
  harvest-origin routes and the plausibility cap, resource-scan ledger rows. Nothing in Parsek
  reads `ModuleCoreHeat` or treats Ore specially.

## What V's manual flight did (read from his collected log, `logs/2026-10-17_1419_mining-rover`)

Deploy all four drills together, start all four `Surface Harvester` converters together, climb
the warp ladder in steps (5x, 25x, 125x; never above 125x), switch the ISRU on and off WHILE on
rails twice (final tanks LF : Ox = 0.45 : 0.55 plus MonoPropellant, so `Lf+Ox` in one window
and `MonoPropellant` in the other, inferred), drop to 1x a few times, stop all drills, retract,
exit to the Space Center, Rewind-to-Launch, watch the ghost to the spawn, switch to it. He also
toggled the radiators (Parsek records nothing for radiators, so the log cannot show when).

## Decisions taken (no product change)

- The ISRU recipe is not in the recording (converter events are per part). The ghost loop looks
  the same for every recipe, and the spawn carries each converter module's own state from the
  end snapshot, so recording the recipe would add data with no visible effect.
- Radiator toggles are not recorded: a static radiator panel has no visible state change on a
  ghost, and the spawn snapshot carries `IsCooling`.

## Open question the lanes answer (ANSWERED 2026-10-17 by MINE-6's first flight)

The edge is written at the toggle UT: every ISRU press made packed in rails warp logged its
`Part event` about 10 ms later, still packed, long before the rails exit.


- A converter switched on or off during rails warp on a LANDED vessel: the recorder's part poll
  runs from `OnPhysicsFrame`, which returns early only when Parsek's own `isOnRails` is set,
  and a landed vessel going on rails does not set it (`FlightRecorder.cs`, the surface branch of
  the go-on-rails handler). Whether the physics-frame hook fires for a packed vessel decides
  whether the event lands at the toggle UT, at the rails exit, or (for an on-and-off inside one
  warp) not at all. MINE-6 gates the event UTs.

## The lanes

Seam additions first: a WarpToUT that keeps warping while later steps act (mid-warp toggles),
and PartAction radiator on / off buttons.

| Id | Lane | Gates |
|---|---|---|
| MINE-6 | V's flight step for step: deploy 4, start 4, warp ladder up to 100x, ISRU on (`Lf+Ox`) and off mid-warp, on (`MonoPropellant`) and off mid-warp, radiators on, stop, retract, exit to the Space Center (scene-exit commit), Rewind-to-Launch, flight reload, watch the ghost through the spawn | converter / deploy events and their UTs, harvest window open / close, ghost drill pose and converter loop applied, spawn tanks = end snapshot, watch camera returns to the spawned rover |
| MINE-7 | Loaded spawn catch-up: after the spawn, fly the spawned rover with converters running and warp on | tanks grow from the snapshot, no jump or negative catch-up at the spawn UT, nothing recorded for the unrecorded spawn (the `HarvestCapture_CatchUpOnLoad` in-game cell cannot run alone: `RunTests` has no single-test selector) |
| MINE-8 | Space Center round trip with converters running (hours away), back to flight | stock background catch-up from `lastUpdateTime`, no spurious converter events, harvest window state across the scene change |
| MINE-9 | Real Spawn Control warp across the end, and a forward jump with the rover LOADED (the MINE-3 remainder) | same tanks as the TimeJump branch; `ConverterJumpPolicy` on a loaded vessel |
| MINE-10 | F5, mine, F9 | recording and harvest windows cut at the quicksave UT, tanks back to the quicksave |
| MINE-11 | Ore tanks nearly full (fixture variant) and an EC-starved rover (no RTGs) | the harvester stops at full, a starved converter produces nothing, no phantom production in replay or spawn, tail kept by the running-converter rule |
| MINE-12 | Background miner: switch to the orbiting probe while the rover mines, warp, switch back | background part events, converter catch-up on revisit, harvest windows dropped on the switch (a later route reads degraded) |
| MINE-13 | Rewind before the mining and mine again | one set of harvest rows and route provenance, no double count |
| MINE-14 | Radiators off through a long rails warp (MINE-2 flight 1's overheat) | pins stock's behaviour and Parsek's handling of a miner losing parts while recording (breakage events, ghost part loss, spawn with the surviving parts) |
| MINE-4 / MINE-5 | Mining-base docked-origin route across a rewind; live-drill harvest-origin route with a delivery leg | route provenance from a real drill (D10 harvest-provenance from synthetic to live), the plausibility cap against a real catch-up |
| MINE-15 | Career copy of the rover: recover it carrying ore | stock recovery funds booked once, kept across a rewind |
| GS-6r3 | Closed by MINE-6 if its ghost converter-loop gate goes green (a real recorded converter on a flight ghost) | todo GS6-CONVERTER-LOOP-NEEDS-AN-ANIMATION-GROUP-PART-ABOARD |

Needs a new craft (V could build them as he built the rover): an asteroid drill
(`ModuleAsteroidDrill` on a captured asteroid), an orbital surveyor scan tagged to a recording
then rewound, a Minmus miner with an engineer (MINE-1's original host).

Order: seam additions and MINE-6, then MINE-7 / MINE-8 / MINE-9, then MINE-10..MINE-15 and the
routes. Each lane flies on V's PC one at a time.
