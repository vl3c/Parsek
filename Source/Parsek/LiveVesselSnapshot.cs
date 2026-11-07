using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Live vessel capture for the recorders: backing up a live vessel into a
    /// snapshot ConfigNode and normalizing it for persistence (terminal state,
    /// localized name, surface orbit, resource manifest), the celestial-body
    /// registry lookups that snapshot code resolves bodies through (with their
    /// test hooks), situation and biome text, and the snapshot-distance /
    /// max-distance family over a recording's body-fixed surfaces.
    ///
    /// <para>The max-distance family stays whole: it dispatches per section on
    /// the RELATIVE-frame contract, so its members move or stay together. The
    /// spawn side lives in <see cref="VesselSpawner"/>; pure ConfigNode helpers
    /// with no live state live in <see cref="VesselSnapshotOps"/>.</para>
    /// </summary>
    internal static class LiveVesselSnapshot
    {
        internal delegate bool ResolveBodyNameByIndexDelegate(int index, out string name);

        internal delegate bool ResolveBodyByNameDelegate(string bodyName, out CelestialBody body);

        internal delegate bool ResolveBodyIndexDelegate(CelestialBody body, out int index);

        internal static ResolveBodyNameByIndexDelegate BodyNameResolverForTesting;

        internal static ResolveBodyByNameDelegate BodyResolverForTesting;

        internal static ResolveBodyIndexDelegate BodyIndexResolverForTesting;

        public static ConfigNode TryBackupSnapshot(Vessel vessel)
        {
            if (vessel == null) return null;
            try
            {
                // BackupVessel() sets vessel.isBackingUp = true internally (confirmed
                // via decompilation) — PartModules see the flag during serialization. (#236)
                ProtoVessel pv = vessel.BackupVessel();
                if (pv == null) return null;
                ConfigNode node = new ConfigNode("VESSEL");
                pv.Save(node);
                NormalizeBackedUpSnapshotFromLiveVessel(node, vessel);
                // Crew inventories live on the roster, not in PART nodes: capture them with
                // the snapshot so a spawn from it restores what the crew carried at this
                // moment (CrewInventorySnapshot). Keyed by live names; the reverse map below
                // renames the keys together with the PART crew values.
                CrewInventorySnapshot.CaptureFromLiveVessel(vessel, node);
                // Live PART/crew= reflects whichever kerbal is physically seated, which
                // after a SwapReservedCrewInFlight pass is a stand-in. Persisting the
                // stand-in name would cause EnsureCrewExistInRoster to fabricate a new
                // kerbal with that name at spawn time instead of restoring the original.
                KerbalsModule.ReverseMapCrewNamesInSnapshot(
                    node,
                    CrewReservationManager.CrewReplacements,
                    $"TryBackupSnapshot pid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
                return node;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to backup vessel snapshot: {ex.Message}");
                return null;
            }
        }

        private static void NormalizeBackedUpSnapshotFromLiveVessel(ConfigNode snapshot, Vessel vessel)
        {
            if (snapshot == null || vessel == null)
                return;

            ResolveLocalizedVesselNameInSnapshot(snapshot);

            TerminalState liveState = ResolveLiveSnapshotTerminalState(vessel);
            CelestialBody surfaceBody = vessel.situation == Vessel.Situations.LANDED
                || vessel.situation == Vessel.Situations.SPLASHED
                ? vessel.mainBody
                : null;
            NormalizeStableSnapshotForPersistence(
                snapshot,
                liveState,
                surfaceBody,
                $"TryBackupSnapshot pid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
        }

        // KSP keeps Vessel.vesselName as a raw "#autoLOC_..." token until UI display time,
        // and ProtoVessel.Save inherits that token into VESSEL.name. Without this wrap the
        // _vessel.craft sidecar ships the token and any consumer that respawns the vessel
        // via ProtoVessel.Load (ghost map presence, restored vessels, tracking-station rows)
        // sees the raw token as the live vesselName. Wrap once here so every TryBackupSnapshot
        // caller is immune.
        internal static void ResolveLocalizedVesselNameInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null)
                return;
            string raw = snapshot.GetValue("name");
            if (string.IsNullOrEmpty(raw) || raw[0] != '#')
                return;
            string resolved = Recording.ResolveLocalizedName(raw);
            if (string.IsNullOrEmpty(resolved) || resolved == raw)
                return;
            snapshot.SetValue("name", resolved, true);
        }

        private static TerminalState ResolveLiveSnapshotTerminalState(Vessel vessel)
        {
            if (vessel == null)
                return TerminalState.SubOrbital;

            return RecordingTree.DetermineTerminalState((int)vessel.situation, vessel);
        }

        /// <summary>
        /// M2 harvest capture (plan D4): walks the LIVE vessel's part resources
        /// and returns summed amount/maxAmount per resource name, excluding
        /// ElectricCharge and IntakeAir (same noise filter as
        /// <see cref="VesselSnapshotOps.ExtractResourceManifest(ConfigNode)"/>). Used only at
        /// MID-recording harvest-window transitions, where no fresh snapshot
        /// exists; recording start/stop manifests keep extracting from
        /// snapshots. Returns null if the vessel/parts are unavailable or no
        /// resources are found.
        /// </summary>
        internal static Dictionary<string, ResourceAmount> ExtractLiveResourceManifest(Vessel v)
        {
            if (v == null || v.parts == null || v.parts.Count == 0)
                return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Resources == null)
                    continue;

                for (int j = 0; j < p.Resources.Count; j++)
                {
                    PartResource r = p.Resources[j];
                    if (r == null || string.IsNullOrEmpty(r.resourceName))
                        continue;
                    if (r.resourceName == "ElectricCharge" || r.resourceName == "IntakeAir")
                        continue;

                    if (manifest.TryGetValue(r.resourceName, out ResourceAmount existing))
                    {
                        existing.amount += r.amount;
                        existing.maxAmount += r.maxAmount;
                        manifest[r.resourceName] = existing;
                    }
                    else
                    {
                        manifest[r.resourceName] = new ResourceAmount
                        {
                            amount = r.amount,
                            maxAmount = r.maxAmount
                        };
                    }
                }
            }

            return manifest.Count > 0 ? manifest : null;
        }

        internal static ConfigNode NormalizeStableSnapshotForPersistence(
            ConfigNode snapshot,
            TerminalState? terminalState,
            CelestialBody body = null,
            string logContext = null)
        {
            if (snapshot == null)
                return null;

            VesselSnapshotOps.CorrectUnsafeSnapshotSituation(snapshot, terminalState);
            if ((terminalState == TerminalState.Landed
                    || terminalState == TerminalState.Splashed)
                && !object.ReferenceEquals(body, null))
            {
                ApplySurfaceOrbitToSnapshot(snapshot, body, logContext);
            }

            return snapshot;
        }

        public static void SnapshotVessel(
            Recording pending,
            bool vesselDestroyed,
            Vessel vesselOverride = null,
            ConfigNode destroyedFallbackSnapshot = null)
        {
            if (pending == null || pending.Points.Count == 0)
            {
                ParsekLog.Warn("Spawner", "SnapshotVessel skipped: pending recording is null or has no points");
                return;
            }

            // Compute distance from launch. A recording carrying a Relative TrackSection
            // cannot use the flat Points list: its Relative frames hold anchor-local
            // Cartesian metres in latitude/longitude/altitude, so it measures over the
            // sections' body-fixed surfaces instead (see ClassifySnapshotDistanceRoute).
            bool bodyFixedRoute =
                ClassifySnapshotDistanceRoute(pending) == SnapshotDistanceRoute.BodyFixedSections;
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = null;
            if (!bodyFixedRoute)
            {
                bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);
                if (bodyFirst == null)
                    ParsekLog.Warn("Spawner", $"SnapshotVessel: body '{firstPoint.bodyName}' not found — distance computation will be skipped");
            }

            if (vesselDestroyed)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = destroyedFallbackSnapshot != null
                    ? destroyedFallbackSnapshot.CreateCopy()
                    : null;
                if (bodyFixedRoute)
                {
                    // End position is the latest body-fixed sample (vessel already gone).
                    ApplySnapshotDistancesFromBodyFixedSurfaces(pending, null, "destroyed");
                }
                else
                {
                    // Use last recorded point for distance (may be a different SOI)
                    var lastPoint = pending.Points[pending.Points.Count - 1];
                    CelestialBody bodyLast = FlightGlobals.Bodies?.Find(b => b.name == lastPoint.bodyName);
                    if (bodyFirst != null && bodyLast != null)
                    {
                        Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                            firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                        Vector3d lastPos = bodyLast.GetWorldSurfacePosition(
                            lastPoint.latitude, lastPoint.longitude, lastPoint.altitude);
                        pending.DistanceFromLaunch = Vector3d.Distance(launchPos, lastPos);
                    }

                    // Compute max distance from launch across all recorded points
                    ComputeMaxDistance(pending, bodyFirst, firstPoint);
                }

                pending.VesselSituation = pending.VesselSnapshot != null ? "Destroyed (snapshot kept)" : "Destroyed";

                // End biome from the last body-fixed position (vessel already destroyed).
                // The flat tail may be a Relative frame, whose lat/lon are anchor-local metres.
                TrajectoryPoint endBiomePoint;
                if (TryGetEndBiomeSamplePoint(pending, bodyFixedRoute, out endBiomePoint))
                    pending.EndBiome = TryResolveBiome(endBiomePoint.bodyName, endBiomePoint.latitude, endBiomePoint.longitude);

                ParsekLog.Info("Spawner", $"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Snapshot kept: {pending.VesselSnapshot != null}");
                return;
            }

            Vessel vessel = vesselOverride ?? FlightGlobals.ActiveVessel;
            ParsekLog.Verbose("Spawner",
                $"SnapshotVessel: using {(vesselOverride != null ? "override" : "active")} vessel, " +
                $"vesselDestroyed={vesselDestroyed}, points={pending.Points.Count}");
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (no active vessel)";
                ParsekLog.Info("Spawner", "No active vessel at snapshot time");
                return;
            }

            // Compute distance from launch position
            if (bodyFixedRoute)
            {
                ApplySnapshotDistancesFromBodyFixedSurfaces(pending, vessel.GetWorldPos3D(), "live");
            }
            else if (bodyFirst != null)
            {
                Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                    firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                Vector3d currentPos = vessel.GetWorldPos3D();
                pending.DistanceFromLaunch = Vector3d.Distance(launchPos, currentPos);

                ComputeMaxDistance(pending, bodyFirst, firstPoint);
            }

            // Snapshot the vessel (works for regular vessels and EVA kerbals)
            if (!vessel.loaded)
                ParsekLog.Warn("Spawner", "Active vessel is unloaded at snapshot time — snapshot may be incomplete");
            ConfigNode node = TryBackupSnapshot(vessel);
            if (node == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (snapshot failed)";
                ParsekLog.Error("Spawner", "Failed to backup active vessel at snapshot time");
                return;
            }
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string (humanized, not raw enum)
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{HumanizeSituation(vessel.situation)} {vessel.mainBody.name}";

            // Capture end biome (Phase 10)
            pending.EndBiome = TryResolveBiome(vessel.mainBody?.name, vessel.latitude, vessel.longitude);

            ParsekLog.Info("Spawner", $"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}, " +
                $"EndBiome: {pending.EndBiome ?? "(null)"}");
        }

        private static void ComputeMaxDistance(Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
        {
            if (bodyFirst == null) return;
            ComputeMaxDistanceCore(pending, bodyFirst, firstPoint);
        }

        /// <summary>
        /// Which trajectory surface <see cref="SnapshotVessel"/> measures
        /// <c>DistanceFromLaunch</c> / <c>MaxDistanceFromLaunch</c> over.
        /// </summary>
        internal enum SnapshotDistanceRoute
        {
            /// <summary>The flat <c>Points</c> list (no Relative section can have poisoned it).</summary>
            FlatPoints,

            /// <summary>The TrackSections' body-fixed surfaces, as the finalize backfill walks them.</summary>
            BodyFixedSections,
        }

        /// <summary>
        /// Pure routing for the stop-time distance computation. A recording with ANY
        /// Relative TrackSection takes the body-fixed section walk: the flat <c>Points</c>
        /// list mirrors a Relative section's frames verbatim, and there
        /// <c>latitude/longitude/altitude</c> are anchor-local Cartesian metres, not
        /// body-fixed coordinates (CLAUDE.md "Rotation / world frame"). A promoted debris
        /// booster that entered RELATIVE mode near its sibling read 1205 km through the flat
        /// list for a 5 km fall (todo D5-PROMOTED-DEBRIS-MAXDIST-RELATIVE-FRAME), and the
        /// frame-correct finalize backfill never ran because
        /// <see cref="ClassifyMaxDistanceBackfillRoute"/> skips a recording whose maxDist is
        /// already non-zero. Every other recording keeps the flat list, which is what it
        /// measured before. Pure: no KSP calls.
        /// </summary>
        internal static SnapshotDistanceRoute ClassifySnapshotDistanceRoute(Recording rec)
        {
            if (rec == null || rec.TrackSections == null)
                return SnapshotDistanceRoute.FlatPoints;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                if (rec.TrackSections[i].referenceFrame == ReferenceFrame.Relative)
                    return SnapshotDistanceRoute.BodyFixedSections;
            }
            return SnapshotDistanceRoute.FlatPoints;
        }

        /// <summary>
        /// Index of the LATEST body-fixed sample by UT (ties keep the later collection
        /// order, so a section-boundary duplicate resolves to the later section). Returns -1
        /// for an empty list. Pure.
        /// </summary>
        internal static int ResolveLatestSampleIndex(List<BodyFixedSectionSample> samples)
        {
            if (samples == null || samples.Count == 0) return -1;
            int best = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].point.ut >= samples[best].point.ut) best = i;
            }
            return best;
        }

        /// <summary>
        /// Pure core of the body-fixed stop-time distances: <paramref name="maxDist"/> is
        /// <see cref="TryComputeMaxDistanceFromBodyFixedSurfaces"/>'s chord, and
        /// <paramref name="distanceFromLaunch"/> runs from the same launch reference (the
        /// earliest body-fixed sample) to <paramref name="endWorldPosition"/> when supplied
        /// (a live vessel), else to the latest body-fixed sample (a destroyed one). Never
        /// reads a Relative section's <c>frames</c>. Returns false and writes zeros when the
        /// recording authors no body-fixed sample or the launch reference does not resolve;
        /// <paramref name="endResolved"/> is false when the end position did not resolve
        /// (distanceFromLaunch then stays 0).
        /// </summary>
        internal static bool TryComputeSnapshotDistancesFromBodyFixedSurfaces(
            Recording rec,
            Func<TrajectoryPoint, Vector3d?> resolveSurfacePosition,
            Vector3d? endWorldPosition,
            out double distanceFromLaunch,
            out double maxDist,
            out MaxDistanceReferenceSurface referenceSurface,
            out int sampleCount,
            out bool endResolved)
        {
            distanceFromLaunch = 0.0;
            endResolved = false;
            int unresolvedSampleCount;
            int sectionsWithoutBodyFixedSurface;
            if (!TryComputeMaxDistanceFromBodyFixedSurfaces(
                    rec, resolveSurfacePosition, out maxDist, out referenceSurface,
                    out sampleCount, out unresolvedSampleCount, out sectionsWithoutBodyFixedSurface))
            {
                maxDist = 0.0;
                return false;
            }

            List<BodyFixedSectionSample> samples =
                CollectBodyFixedSectionSamples(rec, out sectionsWithoutBodyFixedSurface);
            Vector3d? launchPos = resolveSurfacePosition(samples[ResolveLaunchReferenceIndex(samples)].point);
            Vector3d? endPos = endWorldPosition.HasValue
                ? endWorldPosition
                : resolveSurfacePosition(samples[ResolveLatestSampleIndex(samples)].point);
            if (launchPos.HasValue && endPos.HasValue)
            {
                distanceFromLaunch = Vector3d.Distance(launchPos.Value, endPos.Value);
                endResolved = true;
            }
            return true;
        }

        /// <summary>
        /// Picks the trajectory sample a destroyed vessel's end biome is resolved at: the
        /// latest body-fixed sample on the body-fixed route (the flat tail may be a Relative
        /// frame), else the last flat point. Pure.
        /// </summary>
        internal static bool TryGetEndBiomeSamplePoint(
            Recording rec, bool bodyFixedRoute, out TrajectoryPoint point)
        {
            point = default(TrajectoryPoint);
            if (rec == null) return false;
            if (bodyFixedRoute)
            {
                List<BodyFixedSectionSample> samples = CollectBodyFixedSectionSamples(rec, out int _);
                int idx = ResolveLatestSampleIndex(samples);
                if (idx < 0) return false;
                point = samples[idx].point;
                return true;
            }
            if (rec.Points == null || rec.Points.Count == 0) return false;
            point = rec.Points[rec.Points.Count - 1];
            return true;
        }

        /// <summary>
        /// Live wrapper of <see cref="TryComputeSnapshotDistancesFromBodyFixedSurfaces"/>
        /// for <see cref="SnapshotVessel"/>: resolves samples through
        /// <c>FlightGlobals.Bodies</c> and writes the result. Leaves both distances at their
        /// current value (0 on a fresh capture) when there is no body-fixed sample - fail
        /// closed rather than resolve anchor-local metres as lat/lon.
        /// </summary>
        private static void ApplySnapshotDistancesFromBodyFixedSurfaces(
            Recording pending, Vector3d? endWorldPosition, string context)
        {
            Func<TrajectoryPoint, Vector3d?> resolver = pt =>
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                if (body == null) return null;
                return body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
            };

            double distanceFromLaunch;
            double maxDist;
            MaxDistanceReferenceSurface referenceSurface;
            int sampleCount;
            bool endResolved;
            if (!TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                    pending, resolver, endWorldPosition,
                    out distanceFromLaunch, out maxDist, out referenceSurface,
                    out sampleCount, out endResolved))
            {
                // Same split as the finalize backfill: no body-fixed sample at all is the
                // designed fail-closed refusal; samples whose launch body did not resolve
                // are a real problem.
                int collectedSamples = CollectBodyFixedSectionSamples(pending, out int _).Count;
                string refusal =
                    $"SnapshotVessel: route=body-fixed-sections context={context} rec={pending.RecordingId} " +
                    $"{(collectedSamples > 0 ? "cannot resolve the launch-reference body" : "no body-fixed surface")} " +
                    $"(samples={collectedSamples} sections={pending.TrackSections.Count}) - " +
                    $"distances left at {pending.DistanceFromLaunch:F0}m / {pending.MaxDistanceFromLaunch:F0}m";
                if (collectedSamples > 0)
                    ParsekLog.Warn("Spawner", refusal);
                else
                    ParsekLog.Verbose("Spawner", refusal);
                return;
            }

            pending.MaxDistanceFromLaunch = maxDist;
            if (endResolved)
                pending.DistanceFromLaunch = distanceFromLaunch;
            ParsekLog.Verbose("Spawner",
                $"SnapshotVessel: route=body-fixed-sections context={context} rec={pending.RecordingId} " +
                $"distance={pending.DistanceFromLaunch:F0}m maxDist={maxDist:F0}m " +
                $"from {sampleCount} body-fixed samples reference={DescribeReferenceSurface(referenceSurface)} " +
                $"endResolved={endResolved}");
        }

        /// <summary>
        /// Which backfill a recording's <see cref="Recording.MaxDistanceFromLaunch"/> takes
        /// at finalization. See <see cref="ClassifyMaxDistanceBackfillRoute"/>.
        /// </summary>
        internal enum MaxDistanceBackfillRoute
        {
            /// <summary>Nothing to do: already computed, or no usable trajectory.</summary>
            None,

            /// <summary>
            /// <see cref="BackfillMaxDistanceFromBodyFixedSurfaces"/> over the TrackSections'
            /// body-fixed surfaces (Absolute <c>frames</c> + Relative <c>bodyFixedFrames</c>).
            /// </summary>
            BodyFixedSections,

            /// <summary><see cref="BackfillMaxDistance"/> over the flat Points list.</summary>
            FlatPoints,
        }

        /// <summary>
        /// Which authored surface supplied a body-fixed sample (and, for the launch
        /// reference, which one the earliest sample came from).
        /// </summary>
        internal enum MaxDistanceReferenceSurface
        {
            /// <summary>No body-fixed surface at all.</summary>
            None,

            /// <summary>An Absolute section's <c>frames</c>.</summary>
            AbsoluteFrames,

            /// <summary>A Relative section's <c>bodyFixedFrames</c>.</summary>
            RelativeBodyFixedFrames,
        }

        /// <summary>One body-fixed sample and the authored surface it came from.</summary>
        internal struct BodyFixedSectionSample
        {
            public TrajectoryPoint point;
            public MaxDistanceReferenceSurface surface;
            public int sectionIndex;
        }

        /// <summary>
        /// Pure routing decision for the finalization-time maxDist backfill.
        ///
        /// <para>A recording WITH TrackSections goes through the body-fixed section walk.
        /// The flat <c>Points</c> list is a frame-blind compatibility mirror: a Relative
        /// section's frames are appended to it verbatim, and in a Relative section
        /// <c>latitude/longitude/altitude</c> are anchor-local Cartesian METRES, not
        /// body-fixed coordinates (CLAUDE.md "Rotation / world frame"). Feeding those to
        /// <c>body.GetWorldSurfacePosition</c> produced maxDist ~735 km for a rover that
        /// never left the pad area (todo
        /// UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON), and
        /// <c>MaxDistanceFromLaunch</c> feeds IsIdleOnPad / IsPadFailure, so the inflated
        /// value FAILS OPEN.</para>
        ///
        /// <para>The section walk reads BOTH body-fixed surfaces: an Absolute section's
        /// <c>frames</c> and a Relative section's <c>bodyFixedFrames</c>, which the
        /// parent-anchored contract names the PRIMARY body-fixed playback surface. A
        /// recording whose only body-fixed surface is the Relative one (every genuine
        /// debris recording, and an undock partner that never left its anchor window)
        /// therefore still gets a real distance instead of staying at 0 and reading as
        /// idle-on-pad - the mirror fail-CLOSED of the defect above.</para>
        ///
        /// <para>The flat path is kept for a sections-less recording (a legacy or
        /// synthesized recording whose trajectory lives solely in <c>Points</c>), and for
        /// a recording whose sections carry no body-fixed surface AND no Relative section
        /// at all (an on-rails checkpoint-only recording): there the flat list cannot have
        /// been poured full of anchor-local metres, and the section walk deliberately
        /// leaves <c>MaxDistanceFromLaunch</c> untouched, so without the fallback such a
        /// recording would keep maxDist = 0 and be discarded as idle-on-pad. A recording
        /// that HAS Relative sections but no body-fixed samples stays on the section walk
        /// and keeps 0: fail closed beats resolving anchor-local metres as lat/lon.</para>
        /// </summary>
        internal static MaxDistanceBackfillRoute ClassifyMaxDistanceBackfillRoute(Recording rec)
        {
            if (rec == null)
                return MaxDistanceBackfillRoute.None;
            if (rec.MaxDistanceFromLaunch > 0.0)
                return MaxDistanceBackfillRoute.None;

            bool hasFlatPoints = rec.Points != null && rec.Points.Count >= 2;
            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                return hasFlatPoints ? MaxDistanceBackfillRoute.FlatPoints : MaxDistanceBackfillRoute.None;

            bool hasBodyFixedSurface = false;
            bool hasRelativeSection = false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame == ReferenceFrame.Relative)
                {
                    hasRelativeSection = true;
                    if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                        hasBodyFixedSurface = true;
                }
                else if (section.referenceFrame == ReferenceFrame.Absolute
                         && section.frames != null && section.frames.Count > 0)
                {
                    hasBodyFixedSurface = true;
                }
            }

            if (hasBodyFixedSurface)
                return MaxDistanceBackfillRoute.BodyFixedSections;
            if (!hasRelativeSection && hasFlatPoints)
                return MaxDistanceBackfillRoute.FlatPoints;
            return MaxDistanceBackfillRoute.BodyFixedSections;
        }

        /// <summary>
        /// Backfills <see cref="Recording.MaxDistanceFromLaunch"/> from trajectory points.
        /// Called from <see cref="ParsekFlight.FinalizeIndividualRecording"/> for tree recordings
        /// that reach finalization via ForceStop (which skips BuildCaptureRecording). Bug #290d.
        /// Requires FlightGlobals.Bodies to be available (KSP runtime only).
        ///
        /// <para>NOT for a recording whose TrackSections author a body-fixed surface (see
        /// <see cref="ClassifyMaxDistanceBackfillRoute"/>): the flat Points list is
        /// reference-frame-blind, so such a recording must go through
        /// <see cref="BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/> instead.</para>
        /// </summary>
        internal static void BackfillMaxDistance(Recording rec)
        {
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return;

            var firstPoint = rec.Points[0];
            CelestialBody bodyFirst;
            try
            {
                bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);
            }
            catch
            {
                // FlightGlobals not available (unit tests)
                return;
            }
            if (bodyFirst == null)
            {
                ParsekLog.Warn("Spawner",
                    $"BackfillMaxDistance: cannot resolve body '{firstPoint.bodyName}' for recording '{rec.RecordingId}'");
                return;
            }
            ComputeMaxDistanceCore(rec, bodyFirst, firstPoint);
            ParsekLog.Verbose("Spawner",
                $"BackfillMaxDistance: rec={rec.RecordingId} maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                $"from {rec.Points.Count} points");
        }

        private static void ComputeMaxDistanceCore(Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
        {
            Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
            double maxDist = 0;
            int bodyLookupFailCount = 0;
            for (int i = 1; i < pending.Points.Count; i++)
            {
                var pt = pending.Points[i];
                CelestialBody bodyPt = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                if (bodyPt == null)
                {
                    bodyLookupFailCount++;
                    continue;
                }
                Vector3d ptPos = bodyPt.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
                double d = Vector3d.Distance(launchPos, ptPos);
                if (d > maxDist) maxDist = d;
            }
            if (bodyLookupFailCount > 0)
                ParsekLog.Warn("Spawner", $"ComputeMaxDistance: {bodyLookupFailCount} points had unresolvable body names");
            pending.MaxDistanceFromLaunch = maxDist;
        }

        /// <summary>
        /// Collects every BODY-FIXED sample a recording's TrackSections author, in section
        /// order: an Absolute section's <c>frames</c> (lat/lon/alt are body-fixed) and a
        /// Relative section's <c>bodyFixedFrames</c> (the parent-anchored contract's PRIMARY
        /// body-fixed playback surface: full <c>TrajectoryPoint</c>s in body-fixed form).
        ///
        /// <para>A Relative section's <c>frames</c> are NEVER collected: there
        /// <c>latitude/longitude/altitude</c> hold anchor-local Cartesian METRES, and
        /// resolving those through <c>GetWorldSurfacePosition</c> is the
        /// UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON defect (CLAUDE.md
        /// "Rotation / world frame"). Pure: no KSP calls.</para>
        /// </summary>
        /// <param name="rec">The recording whose TrackSections are walked.</param>
        /// <param name="sectionsWithoutBodyFixedSurface">Count of sections that contributed
        /// nothing - a Relative section with no <c>bodyFixedFrames</c>, an OrbitalCheckpoint
        /// section, an empty Absolute section.</param>
        internal static List<BodyFixedSectionSample> CollectBodyFixedSectionSamples(
            Recording rec, out int sectionsWithoutBodyFixedSurface)
        {
            sectionsWithoutBodyFixedSurface = 0;
            var samples = new List<BodyFixedSectionSample>();
            if (rec == null || rec.TrackSections == null) return samples;

            for (int s = 0; s < rec.TrackSections.Count; s++)
            {
                TrackSection section = rec.TrackSections[s];
                List<TrajectoryPoint> surface = null;
                MaxDistanceReferenceSurface kind = MaxDistanceReferenceSurface.None;

                if (section.referenceFrame == ReferenceFrame.Absolute)
                {
                    surface = section.frames;
                    kind = MaxDistanceReferenceSurface.AbsoluteFrames;
                }
                else if (section.referenceFrame == ReferenceFrame.Relative)
                {
                    surface = section.bodyFixedFrames;
                    kind = MaxDistanceReferenceSurface.RelativeBodyFixedFrames;
                }

                if (surface == null || surface.Count == 0)
                {
                    sectionsWithoutBodyFixedSurface++;
                    continue;
                }

                for (int j = 0; j < surface.Count; j++)
                {
                    samples.Add(new BodyFixedSectionSample
                    {
                        point = surface[j],
                        surface = kind,
                        sectionIndex = s,
                    });
                }
            }
            return samples;
        }

        /// <summary>
        /// Index of the launch-reference sample: the EARLIEST by UT across both body-fixed
        /// surfaces, not the first Absolute frame. A recording whose first authored frame
        /// lives in a Relative section's <c>bodyFixedFrames</c> (an undock partner, any
        /// parent-anchored debris) has no Absolute frame to start from, and taking a later
        /// Absolute frame as the origin would measure the chord from the wrong end. Ties
        /// keep the earlier collection order. Returns -1 for an empty list. Pure.
        /// </summary>
        internal static int ResolveLaunchReferenceIndex(List<BodyFixedSectionSample> samples)
        {
            if (samples == null || samples.Count == 0) return -1;
            int best = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].point.ut < samples[best].point.ut) best = i;
            }
            return best;
        }

        /// <summary>
        /// Pure core of <see cref="BackfillMaxDistanceFromBodyFixedSurfaces"/>: the greatest
        /// distance between the launch-reference sample and every other body-fixed sample,
        /// with position resolution injected so the walk is testable headless. Returns false
        /// (and writes nothing) when the recording authors no body-fixed sample or the
        /// reference position itself cannot be resolved.
        /// </summary>
        internal static bool TryComputeMaxDistanceFromBodyFixedSurfaces(
            Recording rec,
            Func<TrajectoryPoint, Vector3d?> resolveSurfacePosition,
            out double maxDist,
            out MaxDistanceReferenceSurface referenceSurface,
            out int sampleCount,
            out int unresolvedSampleCount,
            out int sectionsWithoutBodyFixedSurface)
        {
            maxDist = 0.0;
            referenceSurface = MaxDistanceReferenceSurface.None;
            sampleCount = 0;
            unresolvedSampleCount = 0;

            List<BodyFixedSectionSample> samples =
                CollectBodyFixedSectionSamples(rec, out sectionsWithoutBodyFixedSurface);
            int refIdx = ResolveLaunchReferenceIndex(samples);
            if (refIdx < 0 || resolveSurfacePosition == null) return false;

            Vector3d? launchPos = resolveSurfacePosition(samples[refIdx].point);
            if (!launchPos.HasValue) return false;

            referenceSurface = samples[refIdx].surface;
            for (int i = 0; i < samples.Count; i++)
            {
                Vector3d? ptPos = resolveSurfacePosition(samples[i].point);
                if (!ptPos.HasValue)
                {
                    unresolvedSampleCount++;
                    continue;
                }
                double d = Vector3d.Distance(launchPos.Value, ptPos.Value);
                if (d > maxDist) maxDist = d;
                sampleCount++;
            }
            return true;
        }

        /// <summary>
        /// Live-state variant of <see cref="BackfillMaxDistance"/>. Walks the TrackSections'
        /// BODY-FIXED surfaces only - Absolute <c>frames</c> and Relative
        /// <c>bodyFixedFrames</c> - and never a Relative section's <c>frames</c>, whose
        /// lat/lon/alt hold anchor-local Cartesian metres. Feeding those into
        /// <c>body.GetWorldSurfacePosition(lat, lon, alt)</c> would yield a position deep
        /// inside the planet and a falsely huge maxDist (see CLAUDE.md "Rotation / world
        /// frame" gotcha).
        ///
        /// <para>The launch reference is the EARLIEST body-fixed sample by UT across both
        /// surfaces, so a recording that opens on a Relative section (an undock partner, any
        /// parent-anchored debris) still measures its chord from its own first frame. When
        /// the recording authors no body-fixed sample at all,
        /// <c>MaxDistanceFromLaunch</c> is left untouched - fail closed; see
        /// <see cref="ClassifyMaxDistanceBackfillRoute"/> for which recordings take the flat
        /// fallback instead.</para>
        ///
        /// <para>Used by <see cref="ParsekFlight.IsActiveTreeIdleOnPad"/> to compute
        /// distance-from-launch on a live, unfinalized <c>Recording</c>, and by
        /// <c>ParsekFlight.FinalizeIndividualRecording</c>.</para>
        /// </summary>
        internal static void BackfillMaxDistanceFromBodyFixedSurfaces(Recording rec)
        {
            MaxDistanceReferenceSurface ignored;
            BackfillMaxDistanceFromBodyFixedSurfaces(rec, out ignored);
        }

        /// <summary>
        /// <see cref="BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/>, reporting which
        /// authored surface supplied the launch reference so the caller can name it in its
        /// own route log line.
        /// </summary>
        internal static void BackfillMaxDistanceFromBodyFixedSurfaces(
            Recording rec, out MaxDistanceReferenceSurface referenceSurface)
        {
            referenceSurface = MaxDistanceReferenceSurface.None;
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return;

            // The FlightGlobals read lives in the resolver lambda, invoked only from inside
            // the try: headless (unit-test) hosts throw on the static initializer instead of
            // returning null, and that must read as "nothing to compute", not a crash.
            Func<TrajectoryPoint, Vector3d?> resolver = pt =>
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                if (body == null) return null;
                return body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
            };

            double maxDist;
            int sampleCount;
            int unresolvedSampleCount;
            int sectionsWithoutBodyFixedSurface;
            bool computed;
            try
            {
                computed = TryComputeMaxDistanceFromBodyFixedSurfaces(
                    rec, resolver, out maxDist, out referenceSurface,
                    out sampleCount, out unresolvedSampleCount,
                    out sectionsWithoutBodyFixedSurface);
            }
            catch
            {
                referenceSurface = MaxDistanceReferenceSurface.None;
                return; // FlightGlobals not available (unit tests)
            }

            if (!computed)
            {
                // Two different refusals, and the distinction matters when reading a log:
                // no body-fixed sample at all (an all-Relative-frames or checkpoint-only
                // recording - by design, nothing to measure) versus samples present whose
                // reference body name did not resolve (a real problem). Either way
                // MaxDistanceFromLaunch is left untouched.
                int collectedSamples = CollectBodyFixedSectionSamples(rec, out int _).Count;
                if (collectedSamples > 0)
                    ParsekLog.Warn("Spawner",
                        $"BackfillMaxDistanceFromBodyFixedSurfaces: cannot resolve the launch-reference " +
                        $"body for recording '{rec.RecordingId}' ({collectedSamples} body-fixed samples) " +
                        $"- maxDist left at {rec.MaxDistanceFromLaunch:F0}m");
                else
                    ParsekLog.Verbose("Spawner",
                        $"BackfillMaxDistanceFromBodyFixedSurfaces: rec={rec.RecordingId} " +
                        $"no body-fixed surface (sections={rec.TrackSections.Count}) - maxDist left at " +
                        $"{rec.MaxDistanceFromLaunch:F0}m");
                return;
            }

            if (unresolvedSampleCount > 0)
                ParsekLog.Warn("Spawner",
                    $"BackfillMaxDistanceFromBodyFixedSurfaces: {unresolvedSampleCount} samples had " +
                    $"unresolvable body names for recording '{rec.RecordingId}'");

            rec.MaxDistanceFromLaunch = maxDist;
            ParsekLog.Verbose("Spawner",
                $"BackfillMaxDistanceFromBodyFixedSurfaces: rec={rec.RecordingId} maxDist={maxDist:F0}m " +
                $"from {sampleCount} body-fixed samples reference={DescribeReferenceSurface(referenceSurface)} " +
                $"(sections without a body-fixed surface: {sectionsWithoutBodyFixedSurface})");
        }

        /// <summary>Grep-stable name for a <see cref="MaxDistanceReferenceSurface"/>.</summary>
        internal static string DescribeReferenceSurface(MaxDistanceReferenceSurface surface)
        {
            switch (surface)
            {
                case MaxDistanceReferenceSurface.AbsoluteFrames: return "absolute-frames";
                case MaxDistanceReferenceSurface.RelativeBodyFixedFrames: return "relative-body-fixed-frames";
                default: return "none";
            }
        }

        /// <summary>
        /// Resolves the biome name at the given coordinates on the given body.
        /// Returns null if the body is not found or ScienceUtil is unavailable (unit tests).
        /// </summary>
        internal static string TryResolveBiome(string bodyName, double lat, double lon)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body == null) return null;
                string biome = ScienceUtil.GetExperimentBiome(body, lat, lon);
                return string.IsNullOrEmpty(biome) ? null : biome;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Spawner", $"TryResolveBiome failed for {bodyName} ({lat:F4},{lon:F4}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts KSP's Vessel.Situations enum to a human-readable string.
        /// KSP enum names are ALL_CAPS (PRELAUNCH, SUB_ORBITAL, etc.).
        /// </summary>
        internal static string HumanizeSituation(Vessel.Situations situation)
        {
            switch (situation)
            {
                case Vessel.Situations.PRELAUNCH:  return "Prelaunch";
                case Vessel.Situations.LANDED:     return "Landed";
                case Vessel.Situations.SPLASHED:   return "Splashed";
                case Vessel.Situations.FLYING:     return "Flying";
                case Vessel.Situations.SUB_ORBITAL: return "Sub-orbital";
                case Vessel.Situations.ORBITING:   return "Orbiting";
                case Vessel.Situations.ESCAPING:   return "Escaping";
                case Vessel.Situations.DOCKED:     return "Docked";
                default:                           return situation.ToString();
            }
        }

        internal static bool TryGetSnapshotReferenceBodyName(ConfigNode snapshot, out string bodyName)
        {
            bodyName = null;
            if (snapshot == null)
                return false;

            ConfigNode orbitNode = snapshot.GetNode("ORBIT");
            if (orbitNode == null)
                return false;

            string rawRef = orbitNode.GetValue("REF");
            if (!int.TryParse(rawRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bodyIndex))
                return false;

            return TryResolveBodyNameByIndex(bodyIndex, out bodyName);
        }

        internal static bool TryResolveBodyNameByIndex(int bodyIndex, out string bodyName)
        {
            bodyName = null;

            ResolveBodyNameByIndexDelegate resolver = BodyNameResolverForTesting;
            if (resolver != null)
            {
                return resolver(bodyIndex, out bodyName);
            }

            try
            {
                if (FlightGlobals.Bodies == null
                    || bodyIndex < 0
                    || bodyIndex >= FlightGlobals.Bodies.Count)
                {
                    return false;
                }

                CelestialBody body = FlightGlobals.Bodies[bodyIndex];
                if (body == null || string.IsNullOrEmpty(body.name))
                    return false;

                bodyName = body.name;
                return true;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"TryResolveBodyNameByIndex: FlightGlobals body registry unavailable for REF={bodyIndex}");
                return false;
            }
        }

        internal static bool TryResolveBodyByName(string bodyName, out CelestialBody body)
        {
            body = null;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            ResolveBodyByNameDelegate resolver = BodyResolverForTesting;
            if (resolver != null)
            {
                return resolver(bodyName, out body);
            }

            try
            {
                body = FlightGlobals.Bodies?.Find(b => b != null && b.name == bodyName);
                return body != null;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"TryResolveBodyByName: FlightGlobals body registry unavailable for '{bodyName}'");
                body = null;
                return false;
            }
        }

        internal static bool IsHeadlessBodyRegistryFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException || current is MissingMethodException)
                    return true;
            }

            return false;
        }

        internal static void SaveOrbitToNode(Orbit orbit, ConfigNode node, CelestialBody body)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!TryResolveBodyIndex(body, out int bodyIndex))
                bodyIndex = -1;
            node.AddValue("SMA", orbit.semiMajorAxis.ToString("R", ic));
            node.AddValue("ECC", orbit.eccentricity.ToString("R", ic));
            node.AddValue("INC", orbit.inclination.ToString("R", ic));
            node.AddValue("LPE", orbit.argumentOfPeriapsis.ToString("R", ic));
            node.AddValue("LAN", orbit.LAN.ToString("R", ic));
            node.AddValue("MNA", orbit.meanAnomalyAtEpoch.ToString("R", ic));
            node.AddValue("EPH", orbit.epoch.ToString("R", ic));
            node.AddValue("REF", bodyIndex.ToString(ic));
        }

        internal static void ReplaceSnapshotOrbitNode(ConfigNode snapshot, Orbit orbit, CelestialBody body)
        {
            if (snapshot == null || orbit == null || object.ReferenceEquals(body, null))
                return;

            snapshot.RemoveNode("ORBIT");
            ConfigNode orbitNode = new ConfigNode("ORBIT");
            SaveOrbitToNode(orbit, orbitNode, body);
            snapshot.AddNode(orbitNode);
        }

        internal static bool ApplySurfaceOrbitToSnapshot(
            ConfigNode snapshot,
            CelestialBody body,
            string logContext = null)
        {
            if (snapshot == null || object.ReferenceEquals(body, null))
                return false;

            if (!TryResolveBodyIndex(body, out int bodyIndex))
            {
                ParsekLog.Verbose("Spawner",
                    $"ApplySurfaceOrbitToSnapshot: unable to resolve body index for '{VesselSnapshotOps.DescribeBodyForLog(body)}'");
                return false;
            }

            if (VesselSnapshotOps.HasCanonicalSurfaceOrbitForBody(snapshot, bodyIndex))
                return false;

            snapshot.RemoveNode("ORBIT");
            ConfigNode orbitNode = new ConfigNode("ORBIT");
            VesselSnapshotOps.WriteCanonicalSurfaceOrbitValues(orbitNode, bodyIndex);
            snapshot.AddNode(orbitNode);
            // VerboseRateLimited (not Info): this fires once per TryBackupSnapshot for
            // every landed/splashed-vessel snapshot. The recorder's periodic snapshot
            // refresh (FlightRecorder.RefreshBackupSnapshot, 10s-UT throttle) drives it
            // ~1100+ times across a long time-warped recording. Routine normalization,
            // not a notable event, so it must not log unconditionally.
            ParsekLog.VerboseRateLimited("Spawner", "apply-surface-orbit",
                () => $"ApplySurfaceOrbitToSnapshot: rewrote ORBIT to canonical surface tuple for " +
                $"{(string.IsNullOrEmpty(logContext) ? "snapshot" : logContext)} on body " +
                $"'{VesselSnapshotOps.DescribeBodyForLog(body)}' (REF={bodyIndex})");
            return true;
        }

        internal static bool TryResolveBodyIndex(CelestialBody body, out int index)
        {
            index = -1;
            if (object.ReferenceEquals(body, null))
                return false;

            ResolveBodyIndexDelegate resolver = BodyIndexResolverForTesting;
            if (resolver != null && resolver(body, out index))
                return true;

            try
            {
                if (FlightGlobals.Bodies == null)
                    return false;

                string bodyName = body.name;
                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    CelestialBody candidate = FlightGlobals.Bodies[i];
                    if (object.ReferenceEquals(candidate, body))
                    {
                        index = i;
                        return true;
                    }

                    if (!object.ReferenceEquals(candidate, null)
                        && !string.IsNullOrEmpty(bodyName)
                        && string.Equals(candidate.name, bodyName, StringComparison.Ordinal))
                    {
                        index = i;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;
            }

            return false;
        }
    }
}
