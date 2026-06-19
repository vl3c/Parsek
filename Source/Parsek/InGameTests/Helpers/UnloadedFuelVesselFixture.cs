using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Shared fixture for the logistics UNLOADED-path in-game tests
    /// (origin-debit / pickup / multi-stop delivery). The unloaded tests need a
    /// SEPARATE on-rails (unloaded) vessel holding debitable LiquidFuel and free
    /// LiquidFuel capacity; before this fixture they precondition-SKIPPED whenever
    /// the save had no such vessel, forcing the player to hand-place a second
    /// craft.
    ///
    /// <para><b>Chosen design (maintainer): "use my pad rocket + auto-spawn the
    /// rest".</b> The loaded-path tests use the active vessel (a fueled PRELAUNCH
    /// pad rocket satisfies them after the unpack wait). For the unloaded path,
    /// <see cref="EnsureUnloadedLiquidFuelVessel"/> first reuses any pre-existing
    /// suitable unloaded vessel (fast path, byte-identical to the old finders);
    /// when the save has none it SNAPSHOTS the active pad rocket via
    /// <see cref="VesselSpawner.TryBackupSnapshot"/>, rewrites its LiquidFuel
    /// RESOURCE amounts so the copy carries the requested stored + free capacity,
    /// and spawns it with a FRESH identity (preserveIdentity:false) into a high
    /// parking ORBIT far from the active vessel so KSP keeps it on-rails
    /// (unloaded). The fixture then waits a bounded number of frames for the
    /// spawned vessel to register in <see cref="FlightGlobals.Vessels"/> and
    /// settle unloaded, resolving it by the persistentId
    /// <see cref="VesselSpawner.SpawnAtPosition"/> returns.</para>
    ///
    /// <para><b>Never worse than today.</b> When neither a pre-existing vessel
    /// nor a spawn produces a settled unloaded LiquidFuel vessel, the result's
    /// <see cref="EnsureResult.Vessel"/> is null and the caller falls back to the
    /// existing <c>InGameAssert.Skip</c> path. The fixture itself never throws a
    /// skip; the test owns that decision.</para>
    ///
    /// <para><b>Cleanup.</b> A SPAWNED vessel is tracked on the result; the test
    /// calls <see cref="Cleanup"/> in its finally to <c>Vessel.Die()</c> it so a
    /// single (non-batch) run leaves no litter. A pre-EXISTING vessel is never
    /// destroyed. The isolated-batch RestoreBatchFlightBaselineAfterExecution
    /// quickload is the backstop (the spawned vessel is not in the baseline), but
    /// single runs must not rely on it.</para>
    /// </summary>
    internal static class UnloadedFuelVesselFixture
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const string Tag = "TestHelper";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Wall-clock bound on the post-spawn "registered + unloaded" wait. The
        /// freshly loaded ProtoVessel typically registers within a frame or two;
        /// the generous bound covers slow scene settles without hanging a batch.
        /// </summary>
        private const float SpawnSettleTimeoutSeconds = 10f;

        /// <summary>
        /// Parking-orbit altitude (m above the main body) for the spawned
        /// fixture. A circular orbit this high sits far outside the active
        /// vessel's physics range (~22.5 km load distance for a PRELAUNCH pad
        /// rocket on Kerbin), so KSP never loads it - it stays on-rails / unloaded
        /// which is exactly the path the unloaded tests must exercise.
        /// </summary>
        private const double ParkingAltitudeMeters = 250000.0;

        internal enum FixtureSource
        {
            None = 0,
            FoundExisting = 1,
            Spawned = 2,
        }

        /// <summary>
        /// Out-parameter holder for the coroutine (IEnumerator methods cannot use
        /// <c>out</c>). The caller news one up, yields the coroutine, then reads
        /// <see cref="Vessel"/> (null => fall back to Skip) and passes the holder
        /// to <see cref="Cleanup"/> in finally.
        /// </summary>
        internal sealed class EnsureResult
        {
            /// <summary>The resolved unloaded LiquidFuel vessel, or null when none could be provided.</summary>
            internal Vessel Vessel;

            /// <summary>Debitable LiquidFuel stored on <see cref="Vessel"/> (production-probe basis), 0 when none.</summary>
            internal double StoredLiquidFuel;

            /// <summary>Where the vessel came from. Only <see cref="FixtureSource.Spawned"/> is cleaned up.</summary>
            internal FixtureSource Source = FixtureSource.None;

            /// <summary>The fresh persistentId of the spawned vessel (0 when not spawned).</summary>
            internal uint SpawnedPid;
        }

        /// <summary>
        /// Ensures an UNLOADED, non-ghost vessel holding at least
        /// <paramref name="minStoredLf"/> debitable LiquidFuel AND at least
        /// <paramref name="minFreeCapacity"/> free LiquidFuel capacity exists,
        /// writing it (or null) into <paramref name="result"/>.
        ///
        /// Step a: reuse a suitable pre-existing unloaded vessel (no spawn).
        /// Step b: else snapshot the active pad rocket, rewrite its LiquidFuel to
        ///         carry the requested stored + free capacity, and spawn a
        ///         fresh-identity copy into a high parking orbit (on-rails).
        /// Step c: wait (bounded) for the spawn to register + settle unloaded;
        ///         resolve by the returned pid.
        /// Step d: on any failure leave <c>result.Vessel == null</c> (caller skips).
        /// Every branch logs with the <c>TestHelper</c> subsystem tag.
        ///
        /// <para><paramref name="excludeReusePids"/>: when non-null, the step-a
        /// reuse fast-path SKIPS any vessel whose persistentId is in the set. This
        /// is how a multi-SOURCE test provisions TWO distinct depots: after
        /// provisioning depot A, the second call passes
        /// <c>{ depotA.persistentId }</c> so the reuse path cannot hand back the
        /// first-spawned depot (which is now itself an existing unloaded vessel),
        /// forcing a fresh spawn with a distinct pid (preserveIdentity:false mints a
        /// new identity). It does NOT affect step b - a spawn always produces a
        /// fresh pid - so excluding pids only changes whether reuse is allowed.
        /// Defaults null = unchanged single-source behavior.</para>
        ///
        /// <para><paramref name="capStoredLf"/>: when non-null, the spawned copy's
        /// FIRST LiquidFuel tank is shaped to EXACTLY this stored amount with a tank
        /// just large enough to hold it plus <paramref name="minFreeCapacity"/> (no
        /// larger). This bounds a SHARED source so it covers exactly one pickup but
        /// not two, which the escrow competing-route hold test needs. Only applies
        /// on the SPAWN path (step b); the reuse path returns the existing vessel's
        /// real stored amount. Defaults null = the donor's large capacity is kept.</para>
        /// </summary>
        internal static IEnumerator EnsureUnloadedLiquidFuelVessel(
            double minStoredLf, double minFreeCapacity, EnsureResult result,
            HashSet<uint> excludeReusePids = null, double? capStoredLf = null)
        {
            if (result == null)
            {
                ParsekLog.Warn(Tag, "EnsureUnloadedLiquidFuelVessel: null result holder; nothing to do");
                yield break;
            }
            result.Vessel = null;
            result.StoredLiquidFuel = 0.0;
            result.Source = FixtureSource.None;
            result.SpawnedPid = 0u;

            // --- Step a: reuse a pre-existing suitable unloaded vessel. ---
            if (TryFindExistingUnloadedVessel(minStoredLf, minFreeCapacity, excludeReusePids,
                    out Vessel existing, out double existingStored))
            {
                result.Vessel = existing;
                result.StoredLiquidFuel = existingStored;
                result.Source = FixtureSource.FoundExisting;
                ParsekLog.Info(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: reusing existing unloaded vessel " +
                    $"'{existing.vesselName}' pid={existing.persistentId.ToString(IC)} " +
                    $"storedLF={existingStored.ToString("R", IC)} (no spawn)");
                yield break;
            }

            // --- Step b: snapshot the active pad rocket and spawn a copy. ---
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
            {
                ParsekLog.Warn(Tag,
                    "EnsureUnloadedLiquidFuelVessel: no pre-existing unloaded fuel vessel AND " +
                    "FlightGlobals.ActiveVessel is null; cannot snapshot a donor - caller will skip");
                yield break;
            }
            if (active.mainBody == null)
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: active vessel '{active.vesselName}' has null mainBody; " +
                    "cannot place a parking orbit - caller will skip");
                yield break;
            }

            ConfigNode donor = VesselSpawner.TryBackupSnapshot(active);
            if (donor == null)
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: TryBackupSnapshot of active vessel " +
                    $"'{active.vesselName}' returned null; cannot spawn fixture - caller will skip");
                yield break;
            }

            if (!AdjustSnapshotLiquidFuel(donor, minStoredLf, minFreeCapacity, out string adjustReason, capStoredLf))
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: could not shape donor snapshot LiquidFuel " +
                    $"(reason={adjustReason}); cannot spawn fixture - caller will skip");
                yield break;
            }

            ParsekLog.Info(Tag,
                $"EnsureUnloadedLiquidFuelVessel: shaped donor snapshot for spawn " +
                $"(minStoredLF={minStoredLf.ToString("R", IC)} minFreeCap={minFreeCapacity.ToString("R", IC)} " +
                $"capStoredLF={(capStoredLf.HasValue ? capStoredLf.Value.ToString("R", IC) : "none")} " +
                $"excludeReusePids={(excludeReusePids != null ? excludeReusePids.Count.ToString(IC) : "0")})");

            CelestialBody body = active.mainBody;
            // High circular parking orbit, phased to the FAR side of the body from
            // the active vessel so the spawned copy is well outside physics range
            // and KSP keeps it unloaded.
            double ut = Planetarium.GetUniversalTime();
            Orbit parkingOrbit = BuildFarSideParkingOrbit(body, active, ut);

            // A circular orbit at ParkingAltitudeMeters: lat/lon/alt feed the
            // FLYING/ORBITING classifier; velocity.magnitude > 0.9*orbitalSpeed and
            // terminalState=Orbiting both force ORBITING so the spawn node is built
            // as an on-rails orbital vessel. The actual placement uses orbitOverride
            // (parkingOrbit), so lat/lon are only the situation hint.
            double orbitalSpeed = Math.Sqrt(body.gravParameter / (body.Radius + ParkingAltitudeMeters));
            Vector3d hintVelocity = new Vector3d(orbitalSpeed, 0.0, 0.0);

            uint spawnedPid;
            try
            {
                spawnedPid = VesselSpawner.SpawnAtPosition(
                    donor,
                    body,
                    lat: 0.0,
                    lon: 0.0,
                    alt: ParkingAltitudeMeters,
                    velocity: hintVelocity,
                    ut: ut,
                    excludeCrew: null,
                    preserveIdentity: false,
                    terminalState: TerminalState.Orbiting,
                    surfaceRelativeRotation: null,
                    orbitOverride: parkingOrbit);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: SpawnAtPosition threw " +
                    $"{ex.GetType().Name}: {ex.Message}; caller will skip");
                yield break;
            }

            if (spawnedPid == 0u)
            {
                ParsekLog.Warn(Tag,
                    "EnsureUnloadedLiquidFuelVessel: SpawnAtPosition returned pid=0 (spawn failed); " +
                    "caller will skip");
                yield break;
            }
            result.SpawnedPid = spawnedPid;
            result.Source = FixtureSource.Spawned;
            ParsekLog.Info(Tag,
                $"EnsureUnloadedLiquidFuelVessel: spawned fixture pid={spawnedPid.ToString(IC)} " +
                $"into parking orbit alt={ParkingAltitudeMeters.ToString("F0", IC)}m on body " +
                $"'{body.bodyName}'; waiting for it to register + settle unloaded");

            // --- Step c: wait (bounded) for register + unloaded. ---
            float deadline = Time.realtimeSinceStartup + SpawnSettleTimeoutSeconds;
            int waitedFrames = 0;
            Vessel spawned = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                spawned = ResolveByPid(spawnedPid);
                // Settled when it is registered AND on-rails (unloaded). A vessel
                // this far from the active vessel should never load, but wait for
                // the loaded flag to be false explicitly rather than assuming.
                if (spawned != null && !spawned.loaded)
                    break;
                waitedFrames++;
                yield return null;
            }

            spawned = ResolveByPid(spawnedPid);
            if (spawned == null)
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: spawned pid={spawnedPid.ToString(IC)} never " +
                    $"registered in FlightGlobals.Vessels after {waitedFrames.ToString(IC)} frame(s); " +
                    "caller will skip (the partial ProtoVessel, if any, is cleaned up by SpawnAtPosition)");
                yield break;
            }
            if (spawned.loaded)
            {
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: spawned vessel '{spawned.vesselName}' " +
                    $"pid={spawnedPid.ToString(IC)} settled LOADED (in physics range) rather than on-rails " +
                    $"after {waitedFrames.ToString(IC)} frame(s); the unloaded path would not be exercised. " +
                    "Cleaning it up and skipping (caller will fall back to Skip)");
                TryDie(spawned, spawnedPid);
                result.Vessel = null;
                result.SpawnedPid = 0u;
                result.Source = FixtureSource.None;
                yield break;
            }

            double storedAfter = new Parsek.Logistics.LiveOriginCargoProbe(spawned, false)
                .ProbeResourceStored(LiquidFuelName);
            if (storedAfter < minStoredLf)
            {
                // The donor adjustment guarantees this on disk, but the production
                // probe applies flow gates; if a gate trimmed the read below the
                // floor, the test cannot meaningfully run against this vessel.
                ParsekLog.Warn(Tag,
                    $"EnsureUnloadedLiquidFuelVessel: spawned vessel '{spawned.vesselName}' probes only " +
                    $"{storedAfter.ToString("R", IC)} debitable LiquidFuel (< {minStoredLf.ToString("R", IC)}); " +
                    "cleaning it up and skipping");
                TryDie(spawned, spawnedPid);
                result.Vessel = null;
                result.SpawnedPid = 0u;
                result.Source = FixtureSource.None;
                yield break;
            }

            result.Vessel = spawned;
            result.StoredLiquidFuel = storedAfter;
            ParsekLog.Info(Tag,
                $"EnsureUnloadedLiquidFuelVessel: spawned fixture READY - '{spawned.vesselName}' " +
                $"pid={spawnedPid.ToString(IC)} loaded={spawned.loaded} storedLF={storedAfter.ToString("R", IC)} " +
                $"(waited {waitedFrames.ToString(IC)} frame(s))");
        }

        /// <summary>
        /// Tears down a SPAWNED fixture vessel (no-op for a found-existing one or a
        /// null/empty result). Resolves by the tracked pid and calls
        /// <c>Vessel.Die()</c> - the canonical removal for a Parsek-spawned vessel
        /// (see <c>VesselSpawner.CleanupFailedSpawnedProtoVessel</c>). Safe to call
        /// in a finally regardless of how the test exited.
        /// </summary>
        internal static void Cleanup(EnsureResult result)
        {
            if (result == null || result.Source != FixtureSource.Spawned || result.SpawnedPid == 0u)
                return;
            Vessel spawned = ResolveByPid(result.SpawnedPid);
            if (spawned == null)
            {
                ParsekLog.Verbose(Tag,
                    $"UnloadedFuelVesselFixture.Cleanup: spawned pid={result.SpawnedPid.ToString(IC)} " +
                    "already gone; nothing to remove");
                return;
            }
            TryDie(spawned, result.SpawnedPid);
        }

        // ==================================================================
        // Pure / near-pure helpers
        // ==================================================================

        /// <summary>
        /// Pure reuse-exclusion predicate: a candidate is excluded from the step-a
        /// reuse fast-path when its persistentId is in <paramref name="excludeReusePids"/>.
        /// A null/empty set never excludes (preserving single-source behavior).
        /// Extracted so the multi-source distinct-depot decision is xUnit-testable
        /// without a live FlightGlobals scan.
        /// </summary>
        internal static bool IsReuseExcluded(uint candidatePid, HashSet<uint> excludeReusePids)
        {
            return excludeReusePids != null && excludeReusePids.Contains(candidatePid);
        }

        /// <summary>
        /// Rewrites the LiquidFuel RESOURCE nodes in a backed-up VESSEL snapshot so
        /// the FIRST LiquidFuel tank carries at least <paramref name="minStoredLf"/>
        /// stored and at least <paramref name="minFreeCapacity"/> free capacity
        /// (maxAmount - amount), bumping maxAmount when the tank is too small.
        /// Other LiquidFuel tanks are left untouched. Pure with respect to Unity:
        /// operates only on the ConfigNode, so it is directly xUnit-testable.
        ///
        /// <para>When <paramref name="capStoredLf"/> is non-null, the first tank is
        /// shaped to EXACTLY that stored amount (overriding <paramref name="minStoredLf"/>)
        /// and its maxAmount is clamped to exactly <c>cap + minFreeCapacity</c> - the
        /// donor's large capacity is NOT preserved. This bounds a SHARED source so it
        /// covers exactly one pickup but not two (the escrow competing-route hold). A
        /// negative cap is treated as zero.</para>
        ///
        /// Returns false (with a reason) when the snapshot has no PART/RESOURCE
        /// LiquidFuel node to shape - the donor pad rocket is guaranteed to have
        /// LiquidFuel because the loaded precondition that gates these tests holds,
        /// but the contract stays defensive.
        /// </summary>
        internal static bool AdjustSnapshotLiquidFuel(
            ConfigNode vesselNode, double minStoredLf, double minFreeCapacity, out string reason,
            double? capStoredLf = null)
        {
            reason = null;
            if (vesselNode == null)
            {
                reason = "null-vessel-node";
                return false;
            }
            if (minStoredLf < 0.0) minStoredLf = 0.0;
            if (minFreeCapacity < 0.0) minFreeCapacity = 0.0;
            bool capped = capStoredLf.HasValue;
            double capAmount = capped ? Math.Max(0.0, capStoredLf.Value) : 0.0;

            ConfigNode[] parts = vesselNode.GetNodes("PART");
            if (parts == null || parts.Length == 0)
            {
                reason = "no-parts";
                return false;
            }

            // The production probe sums stored LiquidFuel across ALL deliverable
            // tanks, so a multi-tank donor (e.g. the stock Kerbal X) is only bounded
            // if EVERY LF tank is shaped: the FIRST carries the target/cap, and for
            // the cap case every OTHER LF tank is zeroed so the vessel TOTAL == cap.
            // (Shaping only the first tank left the live-spawned source at the donor's
            // full ~7925 LF and skipped the escrow competing-route test - playtest bug.)
            bool foundFirst = false;
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode part = parts[i];
                if (part == null) continue;
                ConfigNode[] resources = part.GetNodes("RESOURCE");
                if (resources == null) continue;
                for (int r = 0; r < resources.Length; r++)
                {
                    ConfigNode res = resources[r];
                    if (res == null) continue;
                    if (!string.Equals(res.GetValue("name"), LiquidFuelName, StringComparison.Ordinal))
                        continue;

                    if (!foundFirst)
                    {
                        foundFirst = true;
                        double newAmount;
                        double newMax;
                        if (capped)
                        {
                            // EXACT cap: store exactly capAmount in a tank just big enough
                            // for it + the requested free capacity. The donor's large
                            // capacity is deliberately NOT preserved so a shared source
                            // covers one pickup but not two.
                            newAmount = capAmount;
                            newMax = newAmount + minFreeCapacity;
                        }
                        else
                        {
                            // Target: amount >= minStoredLf, maxAmount - amount >= minFreeCapacity.
                            newAmount = minStoredLf;
                            newMax = newAmount + minFreeCapacity;

                            // Preserve a larger existing tank capacity if it already exceeds
                            // the requested totals (keeps the tank shape realistic), but never
                            // below the requested free capacity.
                            double existingMax = ParseDouble(res.GetValue("maxAmount"));
                            if (existingMax > newMax) newMax = existingMax;
                            if (newMax - newAmount < minFreeCapacity) newMax = newAmount + minFreeCapacity;
                        }

                        res.SetValue("amount", newAmount.ToString("R", IC), true);
                        res.SetValue("maxAmount", newMax.ToString("R", IC), true);
                        // A freshly-shaped tank must be flowing so the production probe /
                        // writer can read + mutate it (flowState true, not NO_FLOW-locked).
                        res.SetValue("flowState", "True", true);

                        // Non-cap path: the first tank suffices (total is naturally
                        // >= minStoredLf); leave the donor's other tanks untouched.
                        if (!capped)
                            return true;
                    }
                    else if (capped)
                    {
                        // Zero every OTHER LF tank so the vessel TOTAL stored LF == cap
                        // (keep it flowing/empty; the Oxidizer in these tanks is untouched).
                        res.SetValue("amount", "0", true);
                        res.SetValue("flowState", "True", true);
                    }
                }
            }

            if (!foundFirst)
            {
                reason = "no-liquidfuel-resource";
                return false;
            }
            return true;
        }

        // ==================================================================
        // Live KSP helpers (thin; the decisions above are the testable part)
        // ==================================================================

        /// <summary>
        /// Finds an existing UNLOADED, non-ghost, non-active vessel holding at least
        /// <paramref name="minStoredLf"/> debitable LiquidFuel (production probe) AND
        /// at least <paramref name="minFreeCapacity"/> free LiquidFuel capacity. This
        /// is the union of the per-suite finders the unloaded tests carried before
        /// the fixture (M1 origin-debit's stored-only finder + M4's headroom finder),
        /// so a save that already has a suitable vessel behaves exactly as before.
        /// </summary>
        private static bool TryFindExistingUnloadedVessel(
            double minStoredLf, double minFreeCapacity, HashSet<uint> excludeReusePids,
            out Vessel candidate, out double stored)
        {
            candidate = null;
            stored = 0.0;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null) return false;
            HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
            Vessel active = FlightGlobals.ActiveVessel;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.loaded) continue;
                if (active != null && ReferenceEquals(v, active)) continue;
                if (ghostPids != null && ghostPids.Contains(v.persistentId)) continue;
                // A multi-source caller excludes the pids it already provisioned so the
                // reuse path cannot hand back an earlier-spawned depot - forcing a fresh,
                // distinct-pid spawn for the second depot.
                if (IsReuseExcluded(v.persistentId, excludeReusePids)) continue;
                if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) continue;

                double s = new Parsek.Logistics.LiveOriginCargoProbe(v, false)
                    .ProbeResourceStored(LiquidFuelName);
                if (s < minStoredLf) continue;

                if (minFreeCapacity > 0.0 && SumProtoLiquidFuelFreeCapacity(v) < minFreeCapacity)
                    continue;

                candidate = v;
                stored = s;
                return true;
            }
            return false;
        }

        private static double SumProtoLiquidFuelFreeCapacity(Vessel v)
        {
            ProtoVessel pv = v != null ? v.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return 0.0;
            double free = 0.0;
            for (int p = 0; p < pv.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[p];
                if (pps == null || pps.resources == null) continue;
                for (int r = 0; r < pps.resources.Count; r++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[r];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    double room = prs.maxAmount - prs.amount;
                    if (room > 0.0) free += room;
                }
            }
            return free;
        }

        /// <summary>
        /// A high circular orbit phased to the far side of the body from the active
        /// vessel, so the spawned copy is well outside physics range (KSP keeps it
        /// on-rails / unloaded). Circular (ecc 0) at <see cref="ParkingAltitudeMeters"/>,
        /// equatorial; the true anomaly is offset ~180 degrees from the active
        /// vessel's longitude-of-position so the two never coincide.
        /// </summary>
        private static Orbit BuildFarSideParkingOrbit(CelestialBody body, Vessel active, double ut)
        {
            double sma = body.Radius + ParkingAltitudeMeters;
            // Place the periapsis argument so the vessel sits opposite the active
            // vessel. A circular orbit's exact phase is not load-bearing for keeping
            // it unloaded (250 km altitude alone is far outside the ~22.5 km load
            // range); the far-side offset is belt-and-suspenders.
            double lan = 0.0;
            double argPe = 180.0;
            double meanAnomalyAtEpoch = Math.PI; // half-way round, far side
            double inclination = 0.0;
            double eccentricity = 0.0;
            var orbit = new Orbit(
                inclination, eccentricity, sma, lan, argPe, meanAnomalyAtEpoch, ut, body);
            orbit.Init();
            orbit.UpdateFromUT(ut);
            return orbit;
        }

        private static Vessel ResolveByPid(uint pid)
        {
            if (pid == 0u) return null;
            try
            {
                if (FlightGlobals.FindVessel(pid, out Vessel found))
                    return found;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveByPid({pid.ToString(IC)}) threw {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        private static void TryDie(Vessel vessel, uint pid)
        {
            try
            {
                vessel.Die();
                // Drop the now-dead ProtoVessel from the flight state so it cannot
                // linger in the save (mirrors CleanupFailedSpawnedProtoVessel).
                ProtoVessel pv = vessel.protoVessel;
                if (pv != null)
                    HighLogic.CurrentGame?.flightState?.protoVessels?.Remove(pv);
                ParsekLog.Info(Tag,
                    $"UnloadedFuelVesselFixture: removed spawned fixture vessel pid={pid.ToString(IC)}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"UnloadedFuelVesselFixture: failed to remove spawned fixture pid={pid.ToString(IC)} " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static double ParseDouble(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            return double.TryParse(text, NumberStyles.Float, IC, out double v) ? v : 0.0;
        }
    }
}
