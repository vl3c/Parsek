# Cascade-Explode Manual Checklist (rb.mass seeder)

Verifies that vessels with `ForceHeaviest`-autostrutted radial parts (the
stock Kerbal X is the canonical example: 3 landing legs and several radial
parts surface-attached to the central tank) do not cascade-explode at first
Unpack on any load path: Parsek-spawned terminal-orbit vessels, KSP stock
save-load reconstructions, and background vessels entering physics range.

## Background

KSP's `FlightIntegrator` only updates `Part.rb.mass` for unpacked parts.
A freshly-loaded vessel is packed, so every `rb.mass` keeps Unity's default
of `1`. `Part.Start` runs `UpdateAutoStrut` /
`CycleAutoStrut` / `SecureAutoStruts` before the vessel ever unpacks;
`MassivePartCheck` sees every candidate tied at `mass=1`, falls into the
distance tiebreaker, and points each `ForceHeaviest` leg at its closest
sibling instead of the fuel tank. Those bad autostrut joints survive the
on-rails coast at `breakingForce = float.MaxValue`. At Switch-To the per-part
`Unpack -> ResetJoints -> CycleAutoStrut -> ReleaseAutoStruts` chain
releases them simultaneously and the central stack cascade-explodes within
~40 ms.

The fix is a Harmony prefix on `Part.MassivePartCheck(...)` (the inner read
site of the ForceHeaviest autostrut selection) that lazily seeds
`p.rb.mass` for any packed part still at Unity's `rb.mass = 1` default,
immediately before the body reads it. The seed lands at the exact moment
needed, regardless of Unity coroutine timing.

**History of this fix (read before re-investigating any cascade report):**

- PR #885 placed the seeder inline after `pv.Load(flightState)`. Wrong
  anchor: `ProtoVessel.Load(FlightState, Vessel)` does not call
  `LoadObjects()`, so `vesselRef.parts` was empty and the seeder loop
  iterated zero times. Every production seeder log line for two months
  reported `updated=0`.
- The first PR #890 commit re-targeted the same wrong method via a Harmony
  postfix. Same no-op.
- The second PR #890 commit re-targeted to a postfix on `Vessel.Load()`.
  Parts were now populated but `part.rb` was still null (`rb` is assigned
  inside the `Part.Start` coroutine on a future Update tick). The seeder
  helper's `if (part.rb == null) continue;` guard skipped every part.
- The third (final) PR #890 commit re-targeted to a prefix on
  `Part.MassivePartCheck`. Lazy per-part seeding at the read site
  sidesteps the timing question.

The in-game test
`PartMassivePartCheckSeederTest.SeededPartCount_IsPositiveInFlight` is the
authoritative "is the fix actually doing something" check; the manual
steps below are a playtest sanity check on the user-visible behavior.

## Verifying the in-game test counter

Before manually triggering the cascade scenarios, the cheapest verification
is the in-game test runner: enter any FLIGHT scene with at least one
multi-part vessel loaded, hit Ctrl+Shift+T, and run the `Spawner` category.
Both `Patch_IsAppliedByHarmony` and `SeededPartCount_IsPositiveInFlight`
should pass. If `SeededPartCount_IsPositiveInFlight` reports
`SeededPartCount=0`, the prefix is registered but its body is a no-op
(some gate is too strict, or the patched method's signature has drifted);
do not proceed with the playtest scenarios until that's resolved.

## Repro 1: Parsek-spawned vessel at terminal orbit

Reproducer save: `logs/2026-05-17_1437_switch-fly-test/saves/x12/`. Copy
the save directory into a clean `Kerbal Space Program/saves/<name>/`,
launch KSP, and load `persistent.sfs`.

1. Load the repro save. The active vessel should be `Jumping Flea`. Open
   the Tracking Station; confirm a `Kerbal X` orbital vessel exists at
   roughly `alt ~ 418 km` over Kerbin.
2. Click `Kerbal X` and press `Fly` (or use the Map view Switch-To). Do
   not warp first. Watch for any debris or explosion FX during the scene
   transition.
3. After the FLIGHT scene loads, wait 5 seconds of in-game time with the
   vessel focused. Confirm `Kerbal X` is intact: 17 parts, all legs still
   attached, no `Decoupler.2` / `Rockomax16.BW` / `mediumDishAntenna`
   debris floating away.
4. Tail `Kerbal Space Program/KSP.log`. There is no longer any per-spawn
   INFO line (the per-part prefix does not log per-seed to avoid spam).
   Expected absence of:
   - `[Parsek][VERBOSE][Recorder] OnPartJointBreak diagnostics: ...
     breakForce=0.0 structural=F childAttachMatchesJoint=F` entries on a
     `landingLeg1-2` child within the first 100 ms after `Unpacking
     Kerbal X`.
   - `Exploded!!` lines on parts of `Kerbal X` within the first 5 s of
     physics activation.
5. Run the `Spawner` in-game test category (Ctrl+Shift+T). Both
   `Patch_IsAppliedByHarmony` and `SeededPartCount_IsPositiveInFlight`
   should pass; the latter should report `SeededPartCount > 0`.

## Repro 2: Stock save-load reconstruction

Reproducer save: `logs/2026-05-17_1944_switch-fly-edge-case/saves/s16/`.
Copy that save directory into a clean `Kerbal Space Program/saves/<name>/`,
launch KSP, and load `persistent.sfs`.

1. Load the repro save. The active vessel should be `Kerbal X Probe`.
   Confirm Map view shows a separate `Kerbal X` vessel on the same orbit
   (the unloaded target that triggered the cascade in the repro log).
2. Open the Map view, right-click the `Kerbal X` vessel marker, and click
   `Switch To`. Confirm the pre-switch dialog and click `Merge`. Do not
   warp first. Watch for any debris or explosion FX during the scene
   transition (this is the path that previously crashed via
   `SetActiveVessel` -> `SaveGame` -> `FlightDriver.StartAndFocusVessel`).
3. After the FLIGHT scene loads, wait 5 seconds of in-game time with the
   vessel focused. Confirm `Kerbal X` is intact: all parts still attached,
   no `Decoupler.2` / `Rockomax16.BW` / `mediumDishAntenna` debris
   floating away, no `landingLeg1-2` joints breaking.
4. Tail `Kerbal Space Program/KSP.log`. Expected absence of:
   - `[Parsek][VERBOSE][Recorder] OnPartJointBreak diagnostics: ...
     breakForce=0.0 structural=F childAttachMatchesJoint=F` entries on
     `landingLeg1-2` parts within the first 100 ms after `Unpacking
     Kerbal X`.
   - `landingLeg1-2 / parachuteLarge / ladder1 / HeatShield2 / mk1-3pod
     Exploded!!` lines on parts of `Kerbal X` within the first 5 s of
     physics activation.
5. Run the `Spawner` in-game test category and confirm
   `SeededPartCount > 0` (the save-load reconstruction should have driven
   the seeder).

## Repro 3: F5 / F9 quicksave / quickload control

1. In any flight with a multi-part vessel with deployed-leg autostruts
   (e.g., a Kerbal X clone), F5 quicksave.
2. F9 quickload. Wait 5 seconds of physics. Confirm no cascade.
3. Run the `Spawner` in-game test category; `SeededPartCount` should have
   increased relative to step 1.
