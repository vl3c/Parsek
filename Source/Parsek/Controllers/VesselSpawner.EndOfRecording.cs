using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // End-of-recording spawn: SpawnOrRecoverIfTooClose, collision checks and blocker recovery, walkback, spawn position / coordinate / velocity resolution and the landed-altitude clamps.
    public static partial class VesselSpawner
    {
        /// <summary>
        /// Gathers (lat, lon) for every existing same-body landed/splashed vessel — loaded
        /// AND on-rails proto — that could be a stacking blocker for a new surface spawn.
        /// At KSC / Tracking Station nothing is loaded, so the loaded-only collision check
        /// (<see cref="SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels"/>) sees no
        /// blockers and lets duplicate deliveries of the same craft stack on top of each
        /// other (then explode when the player loads them into physics). This proto-aware
        /// gather feeds <see cref="SpawnCollisionDetector.ComputeDeOverlappedLandedSpawn"/>
        /// so the new spawn is nudged clear before materialization.
        ///
        /// Excludes: the recording's own already-adopted source vessel (<paramref name="excludePid"/>),
        /// ghost-map vessels, Debris/EVA/Flag/SpaceObject types, and (when
        /// <paramref name="spawningRecording"/> is given) vessels of other members of the same
        /// committed tree that stood beside it in the recording, and, for an EVA kerbal or placed
        /// ground part, a vessel of an earlier committed tree already standing there when it was
        /// recorded (<see cref="CoexistingTreeSiblingSpawn"/>). Only same-body surface
        /// vessels are returned — orbital/flying vessels do not constrain a ground footprint.
        /// </summary>
        internal static List<(double lat, double lon)> GatherExistingLandedVesselPositions(
            CelestialBody body, uint excludePid, Recording spawningRecording = null,
            double spawnLat = double.NaN, double spawnLon = double.NaN,
            double exemptionRangeMeters = double.NaN)
        {
            var result = new List<(double lat, double lon)>();
            if (body == null)
                return result;

            int loadedCount = 0;
            int protoCount = 0;
            int siblingCount = 0;
            int otherTreeCount = 0;
            int unresolvedFarCount = 0;
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels != null)
                {
                    for (int i = 0; i < vessels.Count; i++)
                    {
                        Vessel v = vessels[i];
                        if (v == null) continue;
                        if (v.persistentId == excludePid) continue;
                        if (v.mainBody != body) continue;
                        if (!(v.Landed || v.Splashed)) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                        if (SpawnCollisionDetector.ShouldSkipVesselType(v.vesselType)) continue;
                        if (spawningRecording != null
                            && WithinExemptionRange(spawnLat, spawnLon, v.latitude, v.longitude,
                                body.Radius, exemptionRangeMeters, ref unresolvedFarCount)
                            && CountExemption(CoexistingTreeSiblingSpawn.ResolveExemption(
                                spawningRecording, v.persistentId,
                                v.id.ToString("N", CultureInfo.InvariantCulture),
                                Recording.ResolveLocalizedName(v.vesselName),
                                body.name, v.latitude, v.longitude, body.Radius,
                                "landed-de-overlap"), ref siblingCount, ref otherTreeCount))
                            continue;
                        result.Add((v.latitude, v.longitude));
                        loadedCount++;
                    }
                }

                var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protoVessels != null)
                {
                    for (int i = 0; i < protoVessels.Count; i++)
                    {
                        ProtoVessel pv = protoVessels[i];
                        if (pv == null) continue;
                        if (pv.persistentId == excludePid) continue;
                        // Skip protos already represented by a loaded Vessel (counted above).
                        if (pv.vesselRef != null && pv.vesselRef.loaded) continue;
                        if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;
                        if (SpawnCollisionDetector.ShouldSkipVesselType(pv.vesselType)) continue;
                        if (!ProtoVesselIsOnSameBodySurface(pv, body)) continue;
                        if (spawningRecording != null
                            && WithinExemptionRange(spawnLat, spawnLon, pv.latitude, pv.longitude,
                                body.Radius, exemptionRangeMeters, ref unresolvedFarCount)
                            && CountExemption(CoexistingTreeSiblingSpawn.ResolveExemption(
                                spawningRecording, pv.persistentId,
                                VesselLaunchIdentity.ReadLaunchGuid(pv),
                                Recording.ResolveLocalizedName(pv.vesselName),
                                body.name, pv.latitude, pv.longitude, body.Radius,
                                "landed-de-overlap"), ref siblingCount, ref otherTreeCount))
                            continue;
                        result.Add((pv.latitude, pv.longitude));
                        protoCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;
                ParsekLog.Verbose("Spawner",
                    $"GatherExistingLandedVesselPositions: KSP unavailable for body={body?.name} — returning {result.Count} so far");
            }

            ParsekLog.Verbose("Spawner",
                $"GatherExistingLandedVesselPositions: body={body.name} excludePid={excludePid} " +
                $"loaded={loadedCount} proto={protoCount} coexistingTreeSiblings={siblingCount} otherTreeStanding={otherTreeCount} " +
                $"beyondExemptionRange={unresolvedFarCount} total={result.Count}");
            return result;
        }

        /// <summary>
        /// True when a landed vessel at (<paramref name="vesselLat"/>, <paramref name="vesselLon"/>)
        /// is close enough to the spawn spot for its exemption to matter: within
        /// <paramref name="rangeMeters"/> (the de-overlap separation; a vessel farther away never
        /// triggers a nudge). A vessel beyond it is kept as a plain blocker without resolving (or
        /// logging) an exemption and counted in <paramref name="unresolvedFarCount"/>. NaN spawn
        /// coordinates or range resolve every vessel.
        /// </summary>
        internal static bool WithinExemptionRange(
            double spawnLat, double spawnLon, double vesselLat, double vesselLon,
            double bodyRadius, double rangeMeters, ref int unresolvedFarCount)
        {
            if (double.IsNaN(spawnLat) || double.IsNaN(spawnLon) || double.IsNaN(rangeMeters))
                return true;
            if (SpawnCollisionDetector.SurfaceDistance(spawnLat, spawnLon, vesselLat, vesselLon, bodyRadius)
                < rangeMeters)
                return true;
            unresolvedFarCount++;
            return false;
        }

        internal static bool CountExemption(
            OverlapExemptionScope scope, ref int sameTreeCount, ref int otherTreeCount)
        {
            if (scope == OverlapExemptionScope.SameTree) sameTreeCount++;
            else if (scope == OverlapExemptionScope.OtherTree) otherTreeCount++;
            return scope != OverlapExemptionScope.None;
        }

        /// <summary>
        /// True when a proto vessel is landed/splashed (or its persisted situation is a
        /// surface situation) on the given body. Surface situation gates the de-overlap
        /// gather so an orbiting proto sharing the body does not constrain the footprint.
        /// </summary>
        private static bool ProtoVesselIsOnSameBodySurface(ProtoVessel pv, CelestialBody body)
        {
            if (pv == null || body == null)
                return false;

            int refBodyIndex = pv.orbitSnapShot != null
                ? pv.orbitSnapShot.ReferenceBodyIndex
                : -1;
            if (refBodyIndex >= 0 && refBodyIndex != body.flightGlobalsIndex)
                return false;

            return pv.landed
                || pv.splashed
                || pv.situation == Vessel.Situations.LANDED
                || pv.situation == Vessel.Situations.SPLASHED
                || pv.situation == Vessel.Situations.PRELAUNCH;
        }

        /// <summary>
        /// T57: the vessel an end-of-recording spawn's collision check exempts. An EVA kerbal
        /// exempts its parent vessel whichever spawn state it uses (endpoint or recorded
        /// terminal orbit): a kerbal floating beside his capsule overlaps it by construction.
        /// Every other recording exempts nothing.
        /// </summary>
        internal static uint ResolveEndOfRecordingCollisionExemptPid(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.EvaCrewName))
                return 0;
            return ResolveParentVesselPid(rec);
        }

        /// <summary>
        /// T57: Resolves the parent recording's VesselPersistentId for EVA collision
        /// exemption. Searches committed trees for the parent recording by ParentRecordingId.
        /// Returns 0 if no parent found. Pure lookup, no side effects.
        /// </summary>
        internal static uint ResolveParentVesselPid(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ParentRecordingId)
                || string.IsNullOrEmpty(rec.TreeId))
                return 0;

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id != rec.TreeId) continue;
                Recording parentRec;
                if (trees[i].Recordings.TryGetValue(rec.ParentRecordingId, out parentRec))
                {
                    if (parentRec.VesselPersistentId != 0)
                    {
                        ParsekLog.Verbose("Spawner",
                            $"ResolveParentVesselPid: EVA '{rec.VesselName}' parent='{parentRec.VesselName}' " +
                            $"pid={parentRec.VesselPersistentId} (T57 exemption)");
                        return parentRec.VesselPersistentId;
                    }
                }
                break;
            }

            // Also check the pending tree (active recording in flight)
            var pendingTree = PendingTreeSlot.PendingTree;
            if (pendingTree != null && pendingTree.Id == rec.TreeId)
            {
                Recording parentRec;
                if (pendingTree.Recordings.TryGetValue(rec.ParentRecordingId, out parentRec)
                    && parentRec.VesselPersistentId != 0)
                {
                    ParsekLog.Verbose("Spawner",
                        $"ResolveParentVesselPid: EVA '{rec.VesselName}' parent='{parentRec.VesselName}' " +
                        $"pid={parentRec.VesselPersistentId} (from pending tree, T57)");
                    return parentRec.VesselPersistentId;
                }
            }

            ParsekLog.Verbose("Spawner",
                $"ResolveParentVesselPid: no parent found for EVA '{rec.VesselName}' " +
                $"(parentId={rec.ParentRecordingId}, treeId={rec.TreeId})");
            return 0;
        }

        public static void SpawnOrRecoverIfTooClose(Recording rec, int index)
        {
            SpawnOrRecoverIfTooClose(rec, index, preserveIdentity: false);
        }

        public static void SpawnOrRecoverIfTooClose(
            Recording rec,
            int index,
            bool preserveIdentity,
            bool allowExistingSourceDuplicate = false)
        {
            const int maxSpawnAttempts = MaxSpawnAttempts;
            string logContext = $"recording #{index} ({rec?.VesselName ?? "(unknown)"})";
            if (rec == null)
            {
                ParsekLog.Warn("Spawner",
                    $"Spawn skipped for {logContext}: recording is null");
                return;
            }

            if (TryAdoptExistingSourceVesselForSpawn(
                rec,
                "Spawner",
                logContext,
                allowExistingSourceDuplicate))
            {
                return;
            }

            // Operator ruling 2026-09-23: a flight that ended parked in the KSC exclusion
            // zone is retired, never spawned. Checked after adoption (a real counterpart
            // that still exists is adopted instead) and before every spawn route.
            if (TryRetireEndedFlightAtKsc(rec, index))
                return;

            if (rec.SpawnAttempts >= maxSpawnAttempts)
            {
                ParsekLog.Verbose("Spawner",
                    $"Spawn skipped for #{index} ({rec.VesselName}): max attempts ({maxSpawnAttempts}) already reached");
                return;
            }

            if (rec.TerminalSpawnCannotSpawnSafely)
            {
                ParsekLog.Warn("Spawner", string.Format(CultureInfo.InvariantCulture,
                    "Spawn skipped for #{0} ({1}): terminal orbit cannot spawn safely " +
                    "reason={2} currentUT={3:R} propagatedAlt={4:R} safeAlt={5:R} periapsis={6:R}",
                    index,
                    rec.VesselName,
                    rec.TerminalSpawnSafetyReasonCode ?? "(none)",
                    rec.TerminalSpawnSafetyDecisionUT,
                    rec.TerminalSpawnSafetyAltitude,
                    rec.TerminalSpawnSafetySafeAltitude,
                    rec.TerminalSpawnSafetyPeriapsisAltitude));
                return;
            }

            HashSet<string> excludeCrew = PrepareSnapshotForSpawn(rec, index);

            if (rec.Points.Count == 0 || FlightGlobals.Vessels == null)
            {
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                    rec,
                    logContext,
                    excludeCrew,
                    preserveIdentity: preserveIdentity,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                rec.VesselSpawned = rec.SpawnAbandoned || rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            bool useRecordedTerminalOrbit = ShouldUseRecordedTerminalOrbitSpawnState(rec);
            bool terminalSpawnWasDeferred = TerminalOrbitSpawnSafety.HasActiveHold(rec);

            // Resolve spawn position from snapshot or trajectory endpoint.
            var lastPt = rec.Points[rec.Points.Count - 1];
            double spawnLat, spawnLon, spawnAlt;
            ResolveSpawnPosition(rec, index, lastPt, out spawnLat, out spawnLon, out spawnAlt);

            CelestialBody body = null;
            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string spawnBodyName))
                body = FlightGlobals.Bodies?.Find(b => b.name == spawnBodyName);

            if (body == null)
            {
                string reason = string.IsNullOrEmpty(spawnBodyName)
                    ? "no authoritative endpoint body"
                    : $"bodyName='{spawnBodyName}' not found";
                ParsekLog.Warn("Spawner", $"Spawn #{index} ({rec.VesselName}): {reason} — " +
                    $"falling back to validated snapshot respawn");
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                    rec,
                    logContext,
                    excludeCrew,
                    preserveIdentity: preserveIdentity,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                rec.VesselSpawned = rec.SpawnAbandoned || rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            double spawnUT = Planetarium.GetUniversalTime();
            Vector3d orbitalSpawnVelocity = Vector3d.zero;
            Orbit orbitalSpawnOrbit = null;
            if (useRecordedTerminalOrbit)
            {
                if (TryResolveRecordedTerminalOrbitSpawnState(
                    rec, body, spawnUT, out double orbitLat, out double orbitLon,
                    out double orbitAlt, out orbitalSpawnVelocity, out orbitalSpawnOrbit))
                {
                    spawnLat = orbitLat;
                    spawnLon = orbitLon;
                    spawnAlt = orbitAlt;

                    ParsekLog.Verbose("Spawner", string.Format(CultureInfo.InvariantCulture,
                        "Spawn #{0} ({1}): using recorded terminal orbit propagated to current UT " +
                        "(ut={2:F2}, body={3}, lat={4:F4}, lon={5:F4}, alt={6:F1}, speed={7:F1})",
                        index, rec.VesselName, spawnUT, body.name, spawnLat, spawnLon,
                        spawnAlt, orbitalSpawnVelocity.magnitude));

                    if (!TryPassTerminalOrbitSpawnSafety(
                        rec,
                        index,
                        body,
                        orbitalSpawnOrbit,
                        spawnUT,
                        spawnAlt,
                        out _))
                    {
                        return;
                    }
                }
                else
                {
                    RejectUnresolvedTerminalOrbitSpawn(
                        rec,
                        index,
                        body,
                        spawnUT,
                        spawnAlt);
                    return;
                }
            }

            Vector3d spawnPos = body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt);

            // T57: resolve parent vessel PID for EVA collision exemption.
            // EVA trajectories overlap the parent vessel for their entire length;
            // without exemption, walkback exhausts all points and abandons the spawn.
            uint exemptPid = ResolveEndOfRecordingCollisionExemptPid(rec);
            var collision = CheckSpawnCollisions(rec, index, isEva, body,
                spawnLat, spawnLon, spawnAlt, spawnPos, exemptPid);
            if (collision.blocked) return;
            // Walkback may have rewritten the spawn coordinates (#264)
            spawnLat = collision.lat;
            spawnLon = collision.lon;
            spawnAlt = collision.alt;
            spawnPos = collision.pos;

            try
            {
                ApplyResolvedSpawnStateOverrides(
                    rec,
                    index,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    isEva,
                    isBreakupContinuous,
                    useRecordedTerminalOrbit);

                // Dead crew guard: if ALL crew in the snapshot are dead, abandon spawn.
                // Spawning a crewless command pod is worse than not spawning at all. (#170)
                // Individual dead crew are already removed by RespawnVessel.RemoveDeadCrewFromSnapshot;
                // this catches the case where the entire crew complement is dead.
                if (!isEva && rec.VesselSnapshot != null)
                {
                    if (ShouldBlockSpawnForDeadCrewInSnapshot(rec.VesselSnapshot, out List<string> snapshotCrew))
                    {
                        rec.VesselSpawned = true;
                        rec.SpawnAbandoned = true;
                        SpawnWarpOutcomeWatch.NoteAbandonReason(
                            rec, SpawnWarpOutcomeWatch.ReasonNoLivingCrew);
                        var classified = ClassifySnapshotCrew(snapshotCrew);
                        ParsekLog.Warn("Spawner",
                            $"Spawn ABANDONED for #{index} ({rec.VesselName}): no spawnable crew — " +
                            FormatSpawnableClassificationSummary(classified));
                        return;
                    }
                }

                // Find nearest vessel for spawn context logging
                double closestDist = FindNearestVesselDistance(spawnPos, body);

                LogSpawnContext(rec, closestDist);

                // SpawnAtPosition dispatch: EVA and breakup-continuous paths rebuild the ORBIT
                // subnode from the resolved spawn position + velocity. Orbiting paths now prefer
                // the recording's stored terminal orbit propagated to the current spawn UT, so
                // deferred high-warp spawns do not mix an old endpoint state vector with a later
                // planet rotation.
                //
                // - Orbital/Docked (#171): raw snapshot orbit was captured during ascent
                //   (suborbital), KSP's on-rails pressure check would destroy the vessel at
                //   periapsis. SpawnAtPosition constructs a new orbit + correct sit from a
                //   current-UT propagated orbital state when terminal orbit data is available.
                // - EVA (#264): the snapshot's stale ORBIT subnode (captured when the kerbal
                //   was on the parent ladder) causes OrbitDriver.updateFromParameters to
                //   overwrite the corrected transform with the parent position on the first
                //   physics frame after load. SpawnAtPosition rebuilds the ORBIT from the
                //   walked endpoint so the kerbal stays where recorded. An EVA that ended
                //   Orbiting with a recorded terminal orbit takes the orbital route instead,
                //   so a deferred spawn does not rotate his orbit with the planet.
                // - Breakup-continuous (#224 follow-up via #264): same stale-ORBIT mechanism.
                //   Rotation is preserved via the same surface-relative ProtoVessel.rot helper
                //   used by snapshot-prep fallbacks.
                //
                // The earlier OverrideSnapshotPosition calls on the EVA and breakup-continuous
                // paths remain as defense-in-depth for the RespawnVessel fallback below: if
                // SpawnAtPosition returns 0, RespawnVessel still sees the corrected snapshot
                // endpoint pose. For breakup-continuous surface/state-vector spawns that now
                // includes the surface-relative ProtoVessel.rot rewrite, so the degraded
                // fallback keeps the same KSP load-frame contract.
                bool routeThroughSpawnAtPosition = ShouldRouteThroughSpawnAtPosition(rec);
                if (routeThroughSpawnAtPosition)
                {
                    ConfigNode validatedSpawnSnapshot = BuildValidatedRespawnSnapshot(
                        rec,
                        spawnUT,
                        $"{logContext} SpawnAtPosition",
                        out string materializationRejectionReason);
                    if (validatedSpawnSnapshot == null)
                    {
                        if (!string.IsNullOrEmpty(materializationRejectionReason))
                        {
                            AbandonSpawnForInvalidMaterialization(
                                rec,
                                logContext,
                                materializationRejectionReason);
                            return;
                        }

                        LogSpawnFailure(rec, index, maxSpawnAttempts);
                        return;
                    }

                    Vector3d velocity = useRecordedTerminalOrbit
                        ? orbitalSpawnVelocity
                        : ResolveNoOverrideSpawnVelocity(
                            rec,
                            lastPt,
                            new Vector3d(lastPt.velocity.x, lastPt.velocity.y, lastPt.velocity.z),
                            body,
                            spawnLat,
                            spawnLon,
                            spawnAlt,
                            orbitalSpawnOrbit,
                            "spawn-at-position",
                            logContext);
                    Quaternion? surfaceRelativeRotationArg = null;
                    if (!useRecordedTerminalOrbit
                        && (isEva || isBreakupContinuous)
                        && TryGetPreferredSpawnRotationFrame(
                            rec, lastPt,
                            out _,
                            out Quaternion preferredSurfaceRelativeRotation,
                            out _))
                    {
                        surfaceRelativeRotationArg = preferredSurfaceRelativeRotation;
                    }

                    string pathLabel;
                    if (useRecordedTerminalOrbit) pathLabel = "Orbital";
                    else if (isEva) pathLabel = "EVA";
                    else if (isBreakupContinuous) pathLabel = "Breakup";
                    else pathLabel = "Orbital";

                    rec.SpawnedVesselPersistentId = SpawnAtPosition(
                        validatedSpawnSnapshot, body, spawnLat, spawnLon, spawnAlt, velocity, spawnUT, excludeCrew,
                        preserveIdentity: preserveIdentity,
                        terminalState: rec.TerminalStateValue,
                        surfaceRelativeRotation: surfaceRelativeRotationArg,
                        orbitOverride: orbitalSpawnOrbit,
                        nameSource: rec);
                    rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                    if (rec.VesselSpawned)
                    {
                        ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                            "{0} vessel spawn for #{1} ({2}) pid={3} lat={4:F4} lon={5:F4} alt={6:F1} terminal={7}",
                            pathLabel, index, rec.VesselName, rec.SpawnedVesselPersistentId,
                            spawnLat, spawnLon, spawnAlt, rec.TerminalStateValue));
                        if (terminalSpawnWasDeferred && useRecordedTerminalOrbit)
                        {
                            ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                                "Terminal spawn succeeded after defer: rec={0} idx={1} vessel=\"{2}\" " +
                                "pid={3} currentUT={4:F2} alt={5:F1}",
                                rec.RecordingId ?? "(null)",
                                index,
                                rec.VesselName ?? "(null)",
                                rec.SpawnedVesselPersistentId,
                                spawnUT,
                                spawnAlt));
                        }
                        TerminalOrbitSpawnSafety.Clear(rec);
                        ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
                        return;
                    }

                    // SpawnAtPosition failed — fall through to RespawnVessel as last-resort.
                    // The earlier OverrideSnapshotPosition call on this path wrote the resolved
                    // spawn coordinates (endpoint, or the collision-walkback correction) into the
                    // snapshot AND stamped it with the deliberate-override marker, so the fallback's
                    // landed-like repair (TryRepairSnapshotBodyProvenance) preserves those
                    // coordinates instead of clobbering them back to the recorded endpoint. The
                    // fallback still produces a vessel at the resolved position on frame 0 (though
                    // the stale-orbit bug may re-appear on frame 1, acceptable for a degraded
                    // fallback).
                    ParsekLog.Warn("Spawner",
                        $"SpawnAtPosition returned 0 for #{index} ({rec.VesselName}) — " +
                        $"falling through to validated snapshot respawn (degraded fallback)");
                }

                rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                    rec,
                    logContext,
                    excludeCrew,
                    preserveIdentity: preserveIdentity,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                rec.VesselSpawned = rec.SpawnAbandoned || rec.SpawnedVesselPersistentId != 0;
                if (rec.SpawnedVesselPersistentId != 0)
                {
                    string lat = rec.VesselSnapshot.GetValue("lat") ?? "?";
                    string lon = rec.VesselSnapshot.GetValue("lon") ?? "?";
                    string alt = rec.VesselSnapshot.GetValue("alt") ?? "?";
                    string sit = rec.VesselSnapshot.GetValue("sit") ?? "?";
                    ParsekLog.Info("Spawner",
                        $"Vessel spawn for #{index} ({rec.VesselName}) pid={rec.SpawnedVesselPersistentId} " +
                        $"sit={sit} lat={lat} lon={lon} alt={alt}");
                    if (terminalSpawnWasDeferred)
                    {
                        ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                            "Terminal spawn succeeded after defer: rec={0} idx={1} vessel=\"{2}\" " +
                            "pid={3} currentUT={4:F2} alt={5}",
                            rec.RecordingId ?? "(null)",
                            index,
                            rec.VesselName ?? "(null)",
                            rec.SpawnedVesselPersistentId,
                            spawnUT,
                            alt));
                    }
                    TerminalOrbitSpawnSafety.Clear(rec);
                    ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
                }
                else if (rec.SpawnAbandoned)
                {
                    return;
                }
                else
                {
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                }
            }
            finally
            {
                // The deliberate-override stamp is a within-spawn-attempt marker:
                // ApplyResolvedSpawnStateOverrides (above) writes it onto the DURABLE
                // rec.VesselSnapshot so the same-call degraded fallback can honor the
                // walkback correction. Strip it on every resolution exit (success,
                // abandon, fallback failure) so a dirty-recording sidecar save
                // (<id>_vessel.craft) can never carry it into a later session, where
                // spawn paths that do not re-run the overrides first (KSC end spawn,
                // ghost tip respawn) would mistake stale snapshot coordinates for a
                // fresh deliberate override. persistent.sfs never sees the stamp; the
                // transient spawn copies are stripped separately before ProtoVessel load.
                StripDeliberatePositionOverrideStampFromRecording(rec, logContext);
            }
        }

        private static void ApplyResolvedSpawnStateOverrides(
            Recording rec,
            int index,
            TrajectoryPoint lastPt,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            bool isEva,
            bool isBreakupContinuous,
            bool useRecordedTerminalOrbit)
        {
            // EVA spawn position fix: update the snapshot's lat/lon/alt to the recording
            // endpoint. The snapshot was captured at EVA start (kerbal on the pod's ladder),
            // but the kerbal walked to a different location during the recording. Without
            // this override, the kerbal spawns on top of the parent vessel and grabs its
            // ladder, triggering KSP's "Kerbals on a ladder — cannot save" error.
            // On the recorded-terminal-orbit path the position is the orbit propagated to the
            // spawn UT, so the endpoint's surface-relative rotation (valid only at the endpoint
            // and its UT) is not applied, the same rule as breakup-continuous below.
            if (isEva && rec.VesselSnapshot != null)
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName,
                    allowPreferredRotation: !useRecordedTerminalOrbit);
            }

            // Breakup-continuous recordings: snapshot position is from breakup time (mid-air).
            // RespawnVessel uses raw snapshot position, so override it with the trajectory
            // endpoint. Same pattern as EVA fix above. (#224)
            if (!isEva && isBreakupContinuous && rec.VesselSnapshot != null)
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName,
                    allowPreferredRotation: !useRecordedTerminalOrbit,
                    stripEvaLadder: false);
            }

            // Surface terminal override: for any LANDED/SPLASHED recording, the snapshot
            // position may be from mid-flight (captured before the vessel reached its final
            // rest position). ResolveSpawnPosition clamped the altitude, but RespawnVessel
            // uses the raw snapshot. Override position and rotation so the vessel spawns
            // in its near-landing orientation, not the mid-flight descent pose. (#231)
            if (!isEva && !isBreakupContinuous && rec.VesselSnapshot != null
                && IsSurfaceTerminal(rec.TerminalStateValue))
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName,
                    stripEvaLadder: false);
            }
        }

        /// <summary>
        /// Prepares the vessel snapshot for spawning: corrects unsafe situation,
        /// strips crew from destroyed recordings, and builds the exclude crew set.
        /// </summary>
        private static HashSet<string> PrepareSnapshotForSpawn(Recording rec, int index)
        {
            // Correct unsafe snapshot situation before spawning (#169).
            // Vessels captured mid-flight have sit=FLYING but terminal state may be Landed/Orbiting.
            // Without correction, KSP's on-rails pressure check destroys the vessel immediately.
            VesselSnapshotOps.CorrectUnsafeSnapshotSituation(rec.VesselSnapshot, rec.TerminalStateValue);

            // Crew protection: strip crew from spawn snapshot when recording ended in
            // destruction to prevent killing crew during spawn-death cycles (#114).
            // Modifies the snapshot in-place — acceptable for Destroyed recordings
            // since they won't be re-spawned (blocked by FLYING/non-leaf checks).
            if (ShouldStripCrewForSpawn(rec))
            {
                if (rec.VesselSnapshot != null)
                {
                    int stripped = VesselSnapshotOps.StripAllCrewFromSnapshot(rec.VesselSnapshot);
                    ParsekLog.Info("Spawner",
                        $"Stripped {stripped} crew from spawn snapshot for destroyed recording " +
                        $"#{index} ({rec.VesselName}) — prevents crew death on spawn");
                }
            }

            // Build exclude set once for all spawn paths (EVA'd crew spawn via child recordings)
            return BuildExcludeCrewSet(rec);
        }

        /// <summary>
        /// Checks bounding box overlap collisions (the KSC exclusion zone is a retirement,
        /// settled before this runs by TryRetireEndedFlightAtKsc). On bounding-box
        /// overlap, runs the duplicate-blocker recovery path (#112) first; if that doesn't
        /// clear the overlap, runs the subdivided trajectory walkback (#264) to find an
        /// earlier collision-free sub-step. The walkback is now applied to EVA kerbals too
        /// (the previous `!isEva` guard was removed because the endpoint-spawn path now
        /// places EVAs accurately and can overlap with a parent vessel).
        ///
        /// Returns <c>blocked=true</c> to tell the caller to skip spawning this frame.
        /// On successful walkback, returns <c>blocked=false</c> with the rewritten
        /// <c>(lat, lon, alt, pos)</c>; the caller must honour these values when dispatching
        /// to <c>SpawnAtPosition</c>/<c>RespawnVessel</c>. Resets CollisionBlockCount on a
        /// clear or walkback-rewritten path.
        /// </summary>
        private static (bool blocked, double lat, double lon, double alt, Vector3d pos) CheckSpawnCollisions(
            Recording rec, int index, bool isEva, CelestialBody body,
            double spawnLat, double spawnLon, double spawnAlt, Vector3d spawnPos,
            uint exemptVesselPid = 0)
        {
            // The KSC exclusion zone (#170) is no longer a block here: a flight that ends
            // parked in it is retired before any spawn route runs (TryRetireEndedFlightAtKsc,
            // operator ruling 2026-09-23), at the same resolved position. The only spawns
            // that could still land in the zone are non-parked terminals (an orbit whose
            // propagated sub-point crosses the pad; the old lat/lon-only check blocked it
            // at any altitude) and un-finalized recordings with no situation evidence,
            // neither of which is a vessel parked on KSC infrastructure.

            // Bounding box overlap check — block spawn if overlapping a loaded vessel.
            // Applies to EVA kerbals too (#264) — the previous skip was based on the
            // assumption that EVAs spawn exactly where recorded, which was broken before
            // the #264 fix landed.
            Bounds spawnBounds = SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot);
            if (isEva)
            {
                ParsekLog.VerboseRateLimited("Spawner", "eva-bounds-" + index,
                    $"CheckSpawnCollisions: EVA bounds={spawnBounds.size} for #{index} ({rec.VesselName})");
            }
            // The active vessel blocks every spawn, EVA or not (todo
            // GC9-SPAWN-IGNORES-ACTIVE-VESSEL): a vessel the player parked on the spawn spot
            // is a real obstacle and the walkback finds a clear position behind it. A live
            // source vessel of the same launch is normally adopted before this runs; the #226
            // replay bypass skips that adoption on purpose, and its duplicate must not appear
            // inside the vessel the player flies either.
            var (overlap, overlapDist, blockerName, blockerVessel) =
                SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                    spawnPos, spawnBounds, 5f, exemptVesselPid,
                    spawningRecording: rec, site: "end-of-recording");
            if (overlap)
            {
                // Precedence: duplicate-blocker-recovery FIRST (#112), walkback SECOND (#264).
                // A quicksave-loaded duplicate takes one frame to recover via
                // ShipConstruction.RecoverVesselFromFlight; if that clears the overlap, the
                // original position is still valid and we must NOT walk back unnecessarily.
                if (ShouldEnterDuplicateBlockerRecovery(
                        rec,
                        blockerVessel != null && blockerVessel.loaded,
                        blockerVessel != null ? blockerVessel.persistentId : 0u,
                        blockerVessel != null ? AnchorDetector.TryReadLiveVesselGuid(blockerVessel) : null,
                        blockerVessel != null && blockerVessel == FlightGlobals.ActiveVessel))
                {
                    ParsekLog.Warn("Spawner",
                        $"Duplicate blocker detected for #{index} ({rec.VesselName}): " +
                        $"recovering '{blockerName}' (pid={blockerVessel.persistentId}) at {overlapDist:F0}m — " +
                        $"likely quicksave-loaded duplicate (#112)");
                    // Parsek's OWN recovery — suppress the crew events stock fires from
                    // VesselRecovery so the P9a experience handler does not credit career
                    // XP for a recovery the player never performed.
                    using (SuppressionGuard.Crew())
                    {
                        ShipConstruction.RecoverVesselFromFlight(
                            blockerVessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    }
                    rec.DuplicateBlockerRecovered = true;
                    rec.CollisionBlockCount = 0;

                    // Re-check overlap after recovery — another vessel may still block
                    var (stillOverlap, recheckDist, recheckName, _) =
                        SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                            spawnPos, spawnBounds, 5f, exemptVesselPid,
                            spawningRecording: rec, site: "end-of-recording-post-recovery");
                    if (!stillOverlap)
                    {
                        // Blocker removed, no other overlap — fall through to spawn at original position
                        return (false, spawnLat, spawnLon, spawnAlt, spawnPos);
                    }

                    ParsekLog.Verbose("Spawner",
                        $"Post-recovery overlap persists for #{index}: '{recheckName}' at {recheckDist:F0}m — trying walkback");
                    // Fall through to the walkback branch
                }

                // Walkback: if the recording has enough trajectory points, step backward with
                // 1.5 m linear sub-steps until we find a clear position. (#264)
                if (rec.Points != null && rec.Points.Count > 1)
                {
                    double walkLat, walkLon, walkAlt;
                    bool walked = TryWalkbackForEndOfRecordingSpawn(
                        rec, index, spawnBounds, body, out walkLat, out walkLon, out walkAlt,
                        exemptVesselPid);
                    if (walked)
                    {
                        Vector3d walkPos = body.GetWorldSurfacePosition(walkLat, walkLon, walkAlt);
                        rec.CollisionBlockCount = 0;
                        ParsekLog.Info("Spawner",
                            $"CheckSpawnCollisions: walkback rewrote spawn position for #{index} ({rec.VesselName}) — " +
                            $"original overlap with '{blockerName}' at {overlapDist:F0}m cleared");
                        return (false, walkLat, walkLon, walkAlt, walkPos);
                    }
                    // Walkback exhausted — TryWalkbackForEndOfRecordingSpawn already set
                    // SpawnAbandoned / WalkbackExhausted / VesselSpawned. Report blocked.
                    return (true, spawnLat, spawnLon, spawnAlt, spawnPos);
                }

                // Fallback for 1-point / empty trajectories (synthetic recordings):
                // use the existing CollisionBlockCount / MaxCollisionBlocks retry path.
                rec.CollisionBlockCount++;
                if (ShouldAbandonCollisionBlockedSpawn(rec.CollisionBlockCount, MaxCollisionBlocks))
                {
                    rec.VesselSpawned = true;   // prevent ShouldSpawnAtRecordingEnd from returning true
                    rec.SpawnAbandoned = true;  // prevent vessel-gone check from resetting VesselSpawned
                    ParsekLog.Warn("Spawner",
                        $"Spawn ABANDONED for #{index} ({rec.VesselName}): collision-blocked for " +
                        $"{rec.CollisionBlockCount} consecutive frames by '{blockerName}' at {overlapDist:F0}m " +
                        $"(single-point trajectory, no walkback possible) — giving up (max={MaxCollisionBlocks})");
                }
                else
                {
                    ParsekLog.VerboseRateLimited("Spawner",
                        "collision-block-" + index,
                        $"Spawn blocked for #{index} ({rec.VesselName}): overlaps '{blockerName}' at {overlapDist:F0}m " +
                        $"(single-point trajectory) — will retry next frame (block={rec.CollisionBlockCount}/{MaxCollisionBlocks})");
                }
                return (true, spawnLat, spawnLon, spawnAlt, spawnPos);
            }

            // Reset collision block counter on successful spawn path
            rec.CollisionBlockCount = 0;
            return (false, spawnLat, spawnLon, spawnAlt, spawnPos);
        }

        /// <summary>
        /// Walk backward along the recording's trajectory with 1.5 m linear sub-steps,
        /// looking for the first position whose bounding box does NOT overlap any loaded
        /// vessel. On success, writes the walkback position into the out parameters and
        /// returns true. On exhaustion (entire trajectory overlaps), marks the recording
        /// with SpawnAbandoned + WalkbackExhausted + VesselSpawned and returns false.
        ///
        /// Used by CheckSpawnCollisions for end-of-recording spawns when the endpoint
        /// overlaps a loaded vessel (typically the parent vessel for an EVA recording).
        /// Unlike VesselGhoster's point-granularity walkback, this runs with metric-step
        /// subdivision so fast-moving trajectories don't skip 10-50 m per iteration. (#264)
        /// </summary>
        internal static bool TryWalkbackForEndOfRecordingSpawn(
            Recording rec, int index, Bounds spawnBounds, CelestialBody body,
            out double walkLat, out double walkLon, out double walkAlt,
            uint exemptVesselPid = 0)
        {
            walkLat = 0;
            walkLon = 0;
            walkAlt = 0;

            if (rec == null || rec.Points == null || rec.Points.Count == 0 || body == null)
            {
                ParsekLog.Verbose("Spawner",
                    $"TryWalkbackForEndOfRecordingSpawn: rec/body null or empty trajectory for #{index} — cannot walkback");
                return false;
            }

            // Every candidate is checked against the active vessel too (todo
            // GC9-SPAWN-IGNORES-ACTIVE-VESSEL), the same rule as the endpoint check.
            var walkResult = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                rec.Points,
                body.Radius,
                SpawnCollisionDetector.DefaultWalkbackStepMeters,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                (worldPos, candidateUT) =>
                {
                    var (ov, _, _, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                        worldPos, spawnBounds, 5f, exemptVesselPid,
                        spawningRecording: rec, site: "end-of-recording-walkback",
                        candidateUT: candidateUT);
                    return ov;
                });

            if (walkResult.found)
            {
                walkLat = walkResult.lat;
                walkLon = walkResult.lon;
                walkAlt = walkResult.alt;

                // For LANDED terminals: the walkback may return a mid-descent trajectory
                // point where the vessel was 10-50m in the air (approaching the landing
                // spot from a diagonal). Trusting that altitude would spawn the vessel in
                // mid-air and let it fall. Fix: top-down Physics.Raycast at the walkback
                // (lat, lon) to find the true surface (including mesh objects like the
                // Island Airfield runway), and clamp the candidate altitude to
                // surface + small clearance when it's notably higher.
                //
                // The raycast uses the same layer mask as KSP's Vessel.GetHeightFromSurface
                // (default + terrain + buildings), so it hits both PQS terrain AND placed
                // colliders correctly. Falls back to the PQS safety floor if the raycast
                // misses (e.g., target area not loaded).
                if (rec.TerminalStateValue == TerminalState.Landed)
                {
                    double raycastSurface = TryFindSurfaceAltitudeViaRaycast(body, walkLat, walkLon, walkAlt);
                    if (!double.IsNaN(raycastSurface))
                    {
                        double above = walkAlt - raycastSurface;
                        // Snap when the candidate is well above the surface (mid-descent
                        // trajectory point) OR below the surface (physics glitch / recorded
                        // alt ended up under the real mesh). Review finding: the original
                        // `above > threshold` check silently passed negative values through.
                        if (above > WalkbackSurfaceSnapThresholdMeters || above < 0)
                        {
                            double snapped = raycastSurface + WalkbackSurfaceClearanceMeters;
                            ParsekLog.Info("Spawner",
                                $"Walkback altitude snapping to surface for #{index} ({rec.VesselName}): " +
                                $"walkAlt={walkAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"raycastSurface={raycastSurface.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"above={above.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                                $"-> snapping to {snapped.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"(raycast=surface + {WalkbackSurfaceClearanceMeters}m)");
                            walkAlt = snapped;
                        }
                        // else: trusted — happy path, no log. Walkback substep spam.
                    }
                    else
                    {
                        double pqsTerrain = body.TerrainAltitude(walkLat, walkLon);
                        ParsekLog.VerboseRateLimited("Spawner", $"walkback-pqs-fallback-{index}",
                            $"Walkback raycast missed for #{index} ({rec.VesselName}) — " +
                            $"falling back to PQS safety floor (pqsTerrain={pqsTerrain.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)})");
                        walkAlt = ClampAltitudeForLanded(walkAlt, pqsTerrain, index, rec.VesselName);
                    }
                }
                else if (rec.TerminalStateValue == TerminalState.Splashed && walkAlt > 0)
                {
                    walkAlt = 0;
                }

                ParsekLog.Info("Spawner",
                    $"TryWalkbackForEndOfRecordingSpawn: found clear position for #{index} ({rec.VesselName}) " +
                    $"lat={walkLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"lon={walkLon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"alt={walkAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                return true;
            }

            // Exhausted — mark the recording so the UI/diagnostics can distinguish this
            // from the MaxCollisionBlocks abandon path and a KSC retirement.
            rec.SpawnAbandoned = true;
            rec.WalkbackExhausted = true;
            rec.VesselSpawned = true; // prevent vessel-gone check from resetting VesselSpawned
            ParsekLog.Warn("Spawner",
                $"Spawn ABANDONED for #{index} ({rec.VesselName}): entire trajectory overlaps " +
                $"with loaded vessels — walkback exhausted");
            return false;
        }

        /// <summary>
        /// Resolve spawn position. EVA and breakup-continuous recordings use the trajectory
        /// endpoint (snapshot position is stale — from EVA start or breakup time).
        /// Other recordings use the snapshot lat/lon/alt, falling back to the trajectory
        /// endpoint if snapshot lacks position data (#127).
        /// All paths fall through to altitude clamping for surface terminal states (#231).
        /// </summary>
        internal static void ResolveSpawnPosition(Recording rec, int index,
            TrajectoryPoint lastPt, out double lat, out double lon, out double alt)
        {
            SpawnCoordinateSource source = SelectSpawnCoordinates(
                rec, lastPt, out string endpointBodyName, out lat, out lon, out alt);

            if (source == SpawnCoordinateSource.EvaEndpoint
                || source == SpawnCoordinateSource.BreakupEndpoint)
            {
                string reason = source == SpawnCoordinateSource.EvaEndpoint
                    ? "EVA endpoint (snapshot is from EVA start)"
                    : "breakup-continuous endpoint (snapshot is from breakup time)";
                ParsekLog.Verbose("Spawner",
                    $"Spawn #{index} ({rec.VesselName}): using trajectory endpoint — {reason}");
            }
            else if (source == SpawnCoordinateSource.EndpointNoSnapshotPosition)
            {
                ParsekLog.Verbose("Spawner",
                    $"No snapshot lat/lon/alt for #{index} ({rec.VesselName}) — using trajectory endpoint for collision check");
            }

            // Safety net: clamp altitude for surface terminal states.
            // SPLASHED: clamp to sea level.
            // LANDED: trust the recorded altitude (it's where the vessel came to rest — the
            // terminal state proves it had settled). Only push UP if the recorded altitude is
            // below the current PQS terrain (indicates the surface has shifted UP since
            // recording — rare, e.g. KSP version change or terrain-mod changes). Do NOT
            // clamp DOWN to PQS terrain — that would bury vessels recorded on mesh objects
            // (Island Airfield, launchpad, KSC buildings) since body.TerrainAltitude() is
            // PQS-only and ignores placed colliders. KSP's Vessel.CheckGroundCollision will
            // fire once the vessel loads and adjust using getLowestPoint() against real
            // colliders (including buildings), handling any residual clipping properly.
            if (rec.TerminalStateValue == TerminalState.Splashed)
            {
                if (alt != 0.0)
                {
                    ParsekLog.Verbose("Spawner",
                        $"Clamped altitude for SPLASHED spawn #{index} ({rec.VesselName}): {alt:F1} -> 0");
                    alt = 0;
                }
            }
            else if (rec.TerminalStateValue == TerminalState.Landed)
            {
                string bodyName = endpointBodyName;
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body != null)
                {
                    double pqsTerrain = body.TerrainAltitude(lat, lon);
                    alt = ClampAltitudeForLanded(alt, pqsTerrain, index, rec.VesselName);
                }
            }
        }

        /// <summary>Which source <see cref="SelectSpawnCoordinates"/> took the spawn position from.</summary>
        internal enum SpawnCoordinateSource
        {
            None = 0,
            EvaEndpoint = 1,
            BreakupEndpoint = 2,
            Snapshot = 3,
            EndpointNoSnapshotPosition = 4,
        }

        /// <summary>
        /// The pre-clamp spawn coordinates every end-of-recording spawn path places a
        /// vessel at (the selection half of <see cref="ResolveSpawnPosition"/>, shared so
        /// the KSC retirement decision reads the SAME position the spawn would use). EVA
        /// (#175) and breakup-continuous (#224) recordings use the trajectory endpoint;
        /// everything else uses the snapshot lat/lon/alt, falling back to the endpoint
        /// when the snapshot lacks it (#127). The endpoint is the resolved recording
        /// endpoint, else <paramref name="lastPt"/>. <paramref name="endpointBodyName"/>
        /// is the endpoint's body whichever source won. Silent; returns
        /// <see cref="SpawnCoordinateSource.None"/> when no source is available.
        /// </summary>
        internal static SpawnCoordinateSource SelectSpawnCoordinates(
            Recording rec,
            TrajectoryPoint? lastPt,
            out string endpointBodyName,
            out double lat,
            out double lon,
            out double alt)
        {
            return SelectSpawnCoordinates(
                rec, lastPt, out endpointBodyName, out lat, out lon, out alt,
                out _, out _, out _);
        }

        /// <summary>
        /// <see cref="SelectSpawnCoordinates(Recording, TrajectoryPoint?, out string, out double, out double, out double)"/>
        /// that also returns the resolved trajectory endpoint (the recording endpoint,
        /// else <paramref name="lastPt"/>) whichever source won, so a caller can check the
        /// snapshot position against where the trajectory actually ended.
        /// </summary>
        internal static SpawnCoordinateSource SelectSpawnCoordinates(
            Recording rec,
            TrajectoryPoint? lastPt,
            out string endpointBodyName,
            out double lat,
            out double lon,
            out double alt,
            out bool haveEndpoint,
            out double endpointLat,
            out double endpointLon)
        {
            lat = 0; lon = 0; alt = 0;
            endpointBodyName = null;
            haveEndpoint = false;
            endpointLat = 0;
            endpointLon = 0;
            if (rec == null)
                return SpawnCoordinateSource.None;

            double endpointAlt = 0;
            if (lastPt.HasValue)
            {
                endpointBodyName = lastPt.Value.bodyName;
                endpointLat = lastPt.Value.latitude;
                endpointLon = lastPt.Value.longitude;
                endpointAlt = lastPt.Value.altitude;
                haveEndpoint = true;
            }
            if (RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out string resolvedBodyName, out double resolvedLat, out double resolvedLon, out double resolvedAlt))
            {
                endpointBodyName = resolvedBodyName;
                endpointLat = resolvedLat;
                endpointLon = resolvedLon;
                endpointAlt = resolvedAlt;
                haveEndpoint = true;
            }

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            if (isEva || isBreakupContinuous)
            {
                if (!haveEndpoint)
                    return SpawnCoordinateSource.None;
                lat = endpointLat;
                lon = endpointLon;
                alt = endpointAlt;
                return isEva
                    ? SpawnCoordinateSource.EvaEndpoint
                    : SpawnCoordinateSource.BreakupEndpoint;
            }

            if (VesselSnapshotOps.TryGetSnapshotDouble(rec.VesselSnapshot, "lat", out double snapLat)
                && VesselSnapshotOps.TryGetSnapshotDouble(rec.VesselSnapshot, "lon", out double snapLon)
                && VesselSnapshotOps.TryGetSnapshotDouble(rec.VesselSnapshot, "alt", out double snapAlt))
            {
                lat = snapLat;
                lon = snapLon;
                alt = snapAlt;
                return SpawnCoordinateSource.Snapshot;
            }

            if (!haveEndpoint)
                return SpawnCoordinateSource.None;
            lat = endpointLat;
            lon = endpointLon;
            alt = endpointAlt;
            return SpawnCoordinateSource.EndpointNoSnapshotPosition;
        }

        /// <summary>
        /// Fires a top-down <see cref="Physics.Raycast"/> at <paramref name="lat"/>/<paramref name="lon"/>
        /// to find the true surface altitude — including mesh objects the PQS terrain
        /// query cannot see. Returns <see cref="double.NaN"/> if no collider is hit
        /// (target area not loaded, space, etc.). Used by the walkback clamp path to
        /// snap mid-air trajectory candidates to the ground without losing mesh-object
        /// offsets.
        /// </summary>
        internal static double TryFindSurfaceAltitudeViaRaycast(
            CelestialBody body, double lat, double lon, double startAltAboveSurface)
        {
            if (body == null)
            {
                ParsekLog.Verbose("Spawner",
                    "TryFindSurfaceAltitudeViaRaycast: null body — returning NaN");
                return double.NaN;
            }

            double originAlt = startAltAboveSurface + WalkbackRaycastOriginOffsetMeters;
            Vector3d originWorld = body.GetWorldSurfacePosition(lat, lon, originAlt);
            Vector3d upAxis = body.GetSurfaceNVector(lat, lon);

            RaycastHit hit;
            bool didHit = Physics.Raycast(
                (Vector3)originWorld,
                -(Vector3)upAxis,
                out hit,
                WalkbackRaycastMaxDistanceMeters,
                WalkbackSurfaceLayerMask,
                QueryTriggerInteraction.Ignore);

            if (!didHit)
            {
                // Rate-limited: walkback may call this once per sub-step on trajectories
                // where the area is not loaded (distant spawn from tracking station).
                ParsekLog.VerboseRateLimited("Spawner", "walkback-raycast-miss",
                    $"TryFindSurfaceAltitudeViaRaycast: no hit at " +
                    $"lat={lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"lon={lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"(origin alt={originAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) — returning NaN");
                return double.NaN;
            }

            double hitAlt = FlightGlobals.getAltitudeAtPos((Vector3d)hit.point, body);
            ParsekLog.VerboseRateLimited("Spawner", "walkback-raycast-hit",
                $"TryFindSurfaceAltitudeViaRaycast: hit at alt={hitAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"(distance={hit.distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m, " +
                $"collider={(hit.collider != null ? hit.collider.name : "<null>")})");
            return hitAlt;
        }

        /// <summary>
        /// Pure: sanity check landed spawn altitude against PQS terrain.
        ///
        /// <para>Philosophy: the recorded altitude is the TRUTH — the vessel was literally
        /// at that altitude when the recording terminal state was determined to be LANDED.
        /// For vessels on terrain, the recorded clearance above PQS is small (1-5m). For
        /// vessels on mesh objects (Island Airfield, launchpad, KSC buildings), the recorded
        /// clearance above PQS can be 10-30m because <c>body.TerrainAltitude()</c> only
        /// returns the raw planetary surface UNDER the mesh object. We must preserve both.</para>
        ///
        /// <para>The only case where clamping is needed: the PQS terrain has shifted UP
        /// since recording (KSP update, terrain mod change). Then the recorded altitude may
        /// be below the current terrain, and we push up by <see cref="UndergroundSafetyFloorMeters"/>.
        /// Otherwise we return the recorded altitude unchanged and let KSP's
        /// <c>Vessel.CheckGroundCollision</c> handle any remaining part-geometry clipping
        /// via <c>getLowestPoint()</c> against real colliders (including buildings).</para>
        /// </summary>
        internal static double ClampAltitudeForLanded(double alt, double pqsTerrainAlt,
            int index, string vesselName)
        {
            var ic = CultureInfo.InvariantCulture;
            double safetyFloor = pqsTerrainAlt + UndergroundSafetyFloorMeters;

            if (alt < safetyFloor)
            {
                // Rate-limited by spawn index so each spawn logs once, but repeated
                // walkback substeps for the same spawn are deduped.
                double delta = safetyFloor - alt;
                ParsekLog.VerboseRateLimited("Spawner", $"clamp-pqs-floor-{index}",
                    $"Clamped altitude for LANDED spawn #{index} ({vesselName}): " +
                    $"{alt.ToString("F1", ic)} -> {safetyFloor.ToString("F1", ic)} " +
                    $"(below-pqs-floor, delta=+{delta.ToString("F1", ic)}m, pqsTerrain={pqsTerrainAlt.ToString("F1", ic)})");
                return safetyFloor;
            }

            // Happy path (recorded altitude preserved) — no log. Walkback calls this
            // once per substep, so logging the no-op case would spam hundreds of lines
            // per spawn attempt. The diagnostic value is low; callers already log when
            // they actually resolve the final spawn position.
            return alt;
        }

        /// <summary>
        /// Find the distance to the nearest loaded vessel on the same body.
        /// Used for spawn context logging.
        /// </summary>
        private static double FindNearestVesselDistance(Vector3d spawnPos, CelestialBody body)
        {
            double closestDist = double.MaxValue;
            for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
            {
                Vessel other = FlightGlobals.Vessels[v];
                if (GhostMapPresence.IsGhostMapVessel(other.persistentId)) continue;
                if (other.mainBody != body) continue;
                double dist = Vector3d.Distance(spawnPos, other.GetWorldPos3D());
                if (dist < closestDist)
                    closestDist = dist;
            }
            return closestDist;
        }

        /// <summary>
        /// Log spawn failure and increment attempt counter. Logs error on final
        /// attempt, warning on retryable failures.
        /// </summary>
        internal static void LogSpawnFailure(Recording rec, int index, int maxAttempts)
        {
            rec.SpawnAttempts++;
            if (rec.SpawnAttempts >= maxAttempts)
                ParsekLog.Error("Spawner", $"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxAttempts} attempts");
            else
                ParsekLog.Warn("Spawner", $"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxAttempts})");
        }

        /// <summary>
        /// Pure decision: should we abandon a collision-blocked spawn?
        /// Returns true when the collision block count has reached or exceeded the maximum.
        /// </summary>
        internal static bool ShouldAbandonCollisionBlockedSpawn(int collisionBlockCount, int maxBlocks)
        {
            return collisionBlockCount >= maxBlocks;
        }

        /// <summary>
        /// Pure decision: should the blocking vessel be recovered as a likely duplicate?
        ///
        /// <para>#112 original scenario: the player quicksaves a Parsek-spawned vessel and
        /// then loads. KSP restores the vessel from the quicksave with its original PID,
        /// and Parsek tries to spawn the same recording again. We end up with two copies
        /// of the same recording's vessel overlapping. The correct response is to destroy
        /// the quicksave-restored copy (same PID as before) and keep the fresh spawn.</para>
        ///
        /// <para>#312 regression: the original check matched by NAME only. When multiple
        /// recordings of the same vessel (e.g. four "Crater Crawler" showcases on the
        /// runway) spawn near each other, each new spawn's overlap-check found the
        /// PREVIOUS recording's vessel -- same name -- and destroyed it. Only 2 of 4
        /// recordings survived in a playtest because each new spawn killed the last.
        /// Siblings should walkback, not recover.</para>
        ///
        /// <para>Fix: the blocker must be this recording's OWN spawn, by identity
        /// (<see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>): its pid must equal
        /// the <see cref="Recording.SpawnedVesselPersistentId"/> we recorded the last time WE
        /// spawned, and for an adoption stamp (spawn pid == the craft-baked recorded pid) the
        /// launch guid must not conclusively differ, so a relaunch of the same craft that
        /// reuses the baked pid is never recovered. The vessel NAME is not consulted: a
        /// player can rename the spawned vessel, and a same-named vessel of another launch is
        /// not ours. A sibling recording's or a non-Parsek vessel fails the identity check and
        /// falls through to walkback.</para>
        /// </summary>
        internal static bool ShouldRecoverBlockerVessel(
            Recording rec, uint blockerPid, string blockerGuid)
        {
            if (rec == null || blockerPid == 0) return false;
            if (!rec.VesselSpawned) return false;
            return VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, blockerPid, blockerGuid);
        }

        /// <summary>
        /// Pure decision half of the duplicate-blocker-recovery guard in
        /// <c>CheckSpawnCollisions</c>: the once-per-recording
        /// <see cref="Recording.DuplicateBlockerRecovered"/> latch, the live-blocker
        /// requirement, the rule that the active vessel is never recovered, and the
        /// same-spawn discriminator in
        /// <see cref="ShouldRecoverBlockerVessel"/>. Kept separate from the call site so
        /// the recovery-loop latch is testable without a live blocker Vessel; the call
        /// site passes <paramref name="blockerVesselLoaded"/> false for a null blocker, so
        /// no member of a missing blocker is read.
        /// </summary>
        internal static bool ShouldEnterDuplicateBlockerRecovery(
            Recording rec,
            bool blockerVesselLoaded,
            uint blockerPid,
            string blockerGuid,
            bool blockerIsActiveVessel)
        {
            if (rec == null) return false;
            // Recovery-loop latch: one recovery per recording, ever.
            if (rec.DuplicateBlockerRecovered) return false;
            if (!blockerVesselLoaded) return false;
            // The vessel the player flies is never recovered out from under them: it blocks
            // and the walkback moves the spawn instead.
            if (blockerIsActiveVessel) return false;
            return ShouldRecoverBlockerVessel(rec, blockerPid, blockerGuid);
        }

        /// <summary>
        /// Pure decision: should we abandon a spawn that keeps dying immediately?
        /// Returns true when the spawn-death count has reached or exceeded the maximum.
        /// </summary>
        internal static bool ShouldAbandonSpawnDeathLoop(int spawnDeathCount, int maxCycles)
        {
            return spawnDeathCount >= maxCycles;
        }

        internal static void LogSpawnContext(Recording rec, double closestDist)
        {
            string sit = rec.VesselSnapshot?.GetValue("sit") ?? "?";
            var allCrew = new List<string>();
            if (rec.VesselSnapshot != null)
            {
                foreach (ConfigNode partNode in rec.VesselSnapshot.GetNodes("PART"))
                {
                    var crewNames = partNode.GetValues("crew");
                    for (int c = 0; c < crewNames.Length; c++)
                        allCrew.Add(crewNames[c]);
                }
            }
            string crewStr = allCrew.Count > 0 ? $", crew=[{string.Join(", ", allCrew)}]" : "";
            ParsekLog.Info("Spawner", $"Spawning vessel: \"{rec.VesselName}\" sit={sit}{crewStr}, " +
                $"nearest vessel={closestDist:F0}m");
        }

        /// <summary>
        /// Removes the deliberate position-override stamp from the DURABLE
        /// <c>rec.VesselSnapshot</c> once a spawn attempt resolves. The stamp is a
        /// within-spawn-attempt marker only: ApplyResolvedSpawnStateOverrides writes it
        /// (via OverrideSnapshotPosition) onto the durable snapshot so the same-call
        /// degraded fallback can honor the walkback correction, and this strip runs on
        /// every resolution exit of SpawnOrRecoverIfTooClose so a dirty-recording
        /// sidecar save (&lt;id&gt;_vessel.craft) can never carry the stamp into a later
        /// session, where spawn paths that do not re-run the overrides first (KSC
        /// end-of-recording spawn, ghost tip respawn) would mistake stale snapshot
        /// coordinates for a fresh deliberate override. Delegates to the single
        /// RemoveValue implementation in <see cref="VesselSnapshotOps.StripDeliberatePositionOverrideStamp"/>;
        /// tolerant of a null recording/snapshot and an absent key.
        /// </summary>
        internal static bool StripDeliberatePositionOverrideStampFromRecording(Recording rec, string logContext)
        {
            return VesselSnapshotOps.StripDeliberatePositionOverrideStamp(
                rec?.VesselSnapshot,
                $"{logContext ?? "(unknown)"} durable snapshot (spawn resolved)");
        }

        internal static double ResolveRelocatedAltitude(
            Recording rec, CelestialBody body, double latitude, double longitude)
        {
            bool landedLike = VesselSnapshotOps.IsLandedLikeSnapshot(rec?.VesselSnapshot);
            bool hasSnapshotAlt = VesselSnapshotOps.TryGetSnapshotDouble(rec?.VesselSnapshot, "alt", out double snapshotAlt);
            double selectedAltitude;
            if (landedLike)
            {
                double terrainAlt = body.TerrainAltitude(latitude, longitude);
                bool terrainValid = !double.IsNaN(terrainAlt) && !double.IsInfinity(terrainAlt);
                selectedAltitude = SelectRelocatedAltitude(landedLike, terrainAlt, terrainValid, snapshotAlt, hasSnapshotAlt);
                ParsekLog.Verbose("Spawner",
                    $"Relocation altitude selected={selectedAltitude:F1} source={(terrainValid ? "terrain" : hasSnapshotAlt ? "snapshot" : "fallback")} " +
                    $"landedLike={landedLike} terrainValid={terrainValid} snapshotAltSet={hasSnapshotAlt}");
                return selectedAltitude;
            }

            selectedAltitude = SelectRelocatedAltitude(landedLike, 0.0, false, snapshotAlt, hasSnapshotAlt);
            ParsekLog.Verbose("Spawner",
                $"Relocation altitude selected={selectedAltitude:F1} source={(hasSnapshotAlt ? "snapshot" : "fallback")} " +
                $"landedLike={landedLike}");
            return selectedAltitude;
        }

        internal static bool TryResolveEndpointStateVector(
            Recording rec,
            string endpointBodyName,
            out TrajectoryPoint point)
        {
            point = default(TrajectoryPoint);
            if (rec?.Points == null || rec.Points.Count == 0 || string.IsNullOrEmpty(endpointBodyName))
                return false;

            point = rec.Points[rec.Points.Count - 1];
            if (!string.Equals(point.bodyName, endpointBodyName, StringComparison.Ordinal))
                return false;

            return VesselSnapshotOps.IsFinite(new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z));
        }

        /// <summary>
        /// Pure: does a no-override <see cref="SpawnAtPosition"/> spawn of this terminal
        /// state seed its orbit from the recorded velocity? Only an Orbiting endpoint is
        /// airborne among the spawnable terminals
        /// (<see cref="GhostPlaybackLogic.IsSpawnableTerminal"/>); a Landed or Splashed spawn
        /// is placed by its lat/lon/alt and keeps the recorded vector it always used.
        /// </summary>
        internal static bool ShouldResolveRecordedSpawnVelocity(TerminalState? terminalState)
        {
            return terminalState == TerminalState.Orbiting;
        }

        /// <summary>
        /// Pure: whether a no-override spawn resolves its recorded velocity. An orbit override
        /// (the velocity then sets only the situation speed), no sample, no body, no recording
        /// or a terminal that is not airborne keeps the recorded vector.
        /// </summary>
        internal static bool ShouldResolveNoOverrideSpawnVelocity(
            bool hasOrbitOverride,
            bool hasSample,
            bool hasBody,
            bool hasRecording,
            TerminalState? terminalState)
        {
            return !hasOrbitOverride
                && hasSample
                && hasBody
                && hasRecording
                && ShouldResolveRecordedSpawnVelocity(terminalState);
        }

        /// <summary>
        /// The velocity a no-override <see cref="SpawnAtPosition"/> call seeds with, for the
        /// callers that pass a recorded <c>TrajectoryPoint.velocity</c> (EVA, breakup and
        /// Orbiting spawns with no recorded terminal orbit). Returns
        /// <paramref name="recordedVelocity"/> unchanged when an orbit override is supplied
        /// (the velocity then sets only the situation speed), there is no sample, or the
        /// terminal is not airborne (<see cref="ShouldResolveRecordedSpawnVelocity"/>);
        /// otherwise resolves it as <see cref="ResolveRecordedSpawnVelocity"/>.
        /// </summary>
        internal static Vector3d ResolveNoOverrideSpawnVelocity(
            Recording rec,
            TrajectoryPoint? sample,
            Vector3d recordedVelocity,
            CelestialBody body,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            Orbit orbitOverride,
            string site,
            string logContext)
        {
            if (!ShouldResolveNoOverrideSpawnVelocity(
                    orbitOverride != null,
                    sample.HasValue,
                    body != null,
                    rec != null,
                    rec?.TerminalStateValue))
            {
                return recordedVelocity;
            }

            return ResolveRecordedSpawnVelocity(
                rec, sample.Value, body, spawnLat, spawnLon, spawnAlt, site, logContext);
        }

        /// <summary>
        /// The recorded <paramref name="sample"/>'s velocity in THIS session's world axes, as
        /// an inertial vector at the spawn position: frame and yaw resolved from the sample and
        /// its same-section neighbour in <c>rec.Points</c>
        /// (<see cref="OrbitReseed.ResolveRecordedSampleSeedVelocityInSession"/>). A single
        /// sample, an interpolated (walkback) point, a Relative section or an unresolved pair
        /// keeps the recorded vector, which is what these spawns seeded before; the Verbose
        /// line names the outcome.
        /// </summary>
        internal static Vector3d ResolveRecordedSpawnVelocity(
            Recording rec,
            TrajectoryPoint sample,
            CelestialBody body,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            string site,
            string logContext)
        {
            Vector3d seedWorldPos = body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt);
            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveRecordedSampleSeedVelocityInSession(
                body,
                rec?.Points,
                rec?.TrackSections,
                sample,
                seedWorldPos);
            ParsekLog.Verbose("Spawner",
                OrbitReseed.FormatRecordedSampleSeedVelocityLine(
                    "Recorded spawn velocity",
                    site,
                    rec?.RecordingId,
                    body.name,
                    sample,
                    v)
                + " context=" + (logContext ?? "(null)"));
            return v.VelocityYup;
        }

        internal static double SelectRelocatedAltitude(
            bool landedLike, double terrainAltitude, bool terrainValid, double snapshotAltitude, bool hasSnapshotAltitude)
        {
            if (landedLike && terrainValid)
                return terrainAltitude;
            if (hasSnapshotAltitude)
                return snapshotAltitude;
            return 0.0;
        }
    }
}
