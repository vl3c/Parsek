# Terminal-Orbit Spawn Cascade-Explode Manual Checklist

Verifies that a Parsek-spawned vessel with `ForceHeaviest`-autostrutted radial
parts (the stock Kerbal X is the canonical example: 3 landing legs and
several radial parts surface-attached to the central tank) does not
cascade-explode when the player Switch-To / TS-Fly / Watch focuses on it
shortly after spawn.

## Background

Pre-fix: KSP's `FlightIntegrator` only updates `Part.rb.mass` for unpacked
parts. A Parsek terminal-orbit spawn instantiates the vessel packed, so every
`rb.mass` keeps Unity's default of `1`. `Part.Start` runs
`UpdateAutoStrut`/`CycleAutoStrut`/`SecureAutoStruts` before the vessel ever
unpacks; `MassivePartCheck` sees every candidate tied at `mass=1`, falls into
the distance tiebreaker, and points each `ForceHeaviest` leg at its closest
sibling instead of the fuel tank. Those bad autostrut joints survive the
on-rails coast at `breakingForce = float.MaxValue`. At Switch-To the per-part
`Unpack -> ResetJoints -> CycleAutoStrut -> ReleaseAutoStruts` chain releases
them simultaneously and the central stack cascade-explodes within ~40 ms.

The fix seeds `Part.rb.mass = mass + resourceMass` (clamped to
`MinimumMass`/`MinimumRBMass`) on every freshly loaded packed part right
after `pv.Load`, so `MassivePartCheck` ranks the heaviest part correctly the
first time and the cascade never sets up.

## Reproducer Save

The investigation repro lives at
`logs/2026-05-17_1437_switch-fly-test/saves/x12/`. Copy the save directory
into a clean `Kerbal Space Program/saves/<name>/`, launch KSP, and load
`persistent.sfs`.

## Checklist

1. Load the repro save. The active vessel should be `Jumping Flea`. Open the
   Tracking Station; confirm a `Kerbal X` orbital vessel exists at roughly
   `alt ~ 418 km` over Kerbin.
2. Click `Kerbal X` and press `Fly` (or use the Map view Switch-To). Do not
   warp first. Watch for any debris or explosion FX during the scene
   transition.
3. After the FLIGHT scene loads, wait 5 seconds of in-game time with the
   vessel focused. Confirm `Kerbal X` is intact: 17 parts, all legs still
   attached, no `Decoupler.2`/`Rockomax16.BW`/`mediumDishAntenna` debris
   floating away.
4. Tail `Kerbal Space Program/KSP.log`. Expected log lines:
   - `[Parsek][INFO][Spawner] Seeded packed-spawn rb.mass for SpawnAtPosition:
     vessel='Kerbal X' pid=<n> updated=17 ...` (or `RespawnVessel` if the
     fallback path ran).
   - No `[Parsek][VERBOSE][Recorder] OnPartJointBreak diagnostics: ... breakForce=0.0
     structural=F childAttachMatchesJoint=F` entries on a `landingLeg1-2`
     child within the first 100 ms after the `Unpacking Kerbal X` line.
   - No `Exploded!!` lines on parts of `Kerbal X` within the first 5 s of
     physics activation.
5. As a control, repeat the spawn path against a stock-craft vessel without
   `ForceHeaviest` autostruts (e.g., a single-pod test rocket); confirm the
   `Seeded packed-spawn rb.mass` log line still appears (the seeder loop
   walks every part on every multi-part spawn; the per-part rb.mass write
   itself is gated on the part having a non-null rigidbody and `partInfo`)
   and the vessel still spawns and unpacks normally.
