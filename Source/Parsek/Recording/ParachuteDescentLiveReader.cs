using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Live half of the chute descent prediction: reads a vessel's open parachutes and its
    /// state into a <see cref="ParachuteDescentInput"/>. A loaded vessel (packed or not) is
    /// read from its <c>ModuleParachute</c> modules (<c>deploymentState</c>,
    /// <c>deployAltitude</c>); an unloaded one from the proto modules' persisted
    /// <c>persistentState</c> / <c>deployAltitude</c>. For the full-canopy estimate it also
    /// reads the open chutes' stock drag (<c>semiDeployedDrag</c> / <c>fullyDeployedDrag</c>,
    /// from the part prefab when unloaded), drag over weight (unpacked loaded vessels only)
    /// and the air density at the vessel and at the ground. Every game read sits in a
    /// non-inlined method so a headless caller never JITs it.
    /// </summary>
    internal static class ParachuteDescentLiveReader
    {
        private const string ParachuteModuleName = "ModuleParachute";

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryRead(Vessel vessel, double ut, out ParachuteDescentInput input, out string declineReason)
        {
            input = default(ParachuteDescentInput);
            declineReason = null;
            if (vessel == null)
            {
                declineReason = "vessel-null";
                return false;
            }

            CountOpenChutes(vessel, out ParachuteCanopyState canopy, out double deployAgl,
                out uint chutePid, out int openCount, out double dragNow, out double dragFull);
            if (canopy == ParachuteCanopyState.None)
            {
                declineReason = "no-open-chute";
                return false;
            }

            CelestialBody body = vessel.mainBody;
            if (body == null)
            {
                declineReason = "body-missing";
                return false;
            }

            double lat = vessel.latitude;
            double lon = vessel.longitude;
            double alt = vessel.altitude;
            double terrain = body.pqsController != null
                ? body.TerrainAltitude(lat, lon, true)
                : vessel.terrainAltitude;
            double rootHeight = double.NaN;
            if (vessel.loaded && vessel.heightFromTerrain >= 0f)
                rootHeight = alt - vessel.terrainAltitude - vessel.heightFromTerrain;

            Vector3 up = Vector3.zero;
            try
            {
                up = (Vector3)body.GetSurfaceNVector(lat, lon);
            }
            catch (Exception)
            {
                up = Vector3.zero;
            }

            double ground = body.ocean && terrain < 0.0 ? 0.0 : terrain;
            input = new ParachuteDescentInput
            {
                DragToWeightRatio = ReadDragToWeightRatio(vessel),
                OpenChuteDragNow = dragNow,
                OpenChuteDragFull = dragFull,
                DensityAtAltitude = ReadDensity(body, alt),
                DensityAtTouchdown = ReadDensity(body, ground),
                BodyName = body.name,
                StartUT = ut,
                Latitude = lat,
                Longitude = lon,
                Altitude = alt,
                VerticalSpeed = vessel.verticalSpeed,
                Canopy = canopy,
                FullDeployAltitudeAgl = deployAgl,
                TerrainAltitude = terrain,
                BodyHasOcean = body.ocean,
                InAtmosphere = body.atmosphere && alt < body.atmosphereDepth,
                RootHeightAboveLowestPoint = rootHeight,
                SurfaceRelativeRotation = vessel.srfRelRotation,
                WorldUp = up,
                ChutePartPersistentId = chutePid,
                OpenChuteCount = openCount,
            };
            return true;
        }

        /// <summary>
        /// Upward non-gravitational acceleration over local gravity, from stock's smoothed
        /// <c>Vessel.perturbation</c> (acceleration minus gravity). Only an unpacked loaded
        /// vessel computes it (stock zeroes it otherwise), so anything else reads NaN.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadDragToWeightRatio(Vessel vessel)
        {
            if (!vessel.loaded || vessel.packed)
                return double.NaN;
            Vector3d perturbation = vessel.perturbation;
            double g = vessel.graviticAcceleration.magnitude;
            if (perturbation.sqrMagnitude <= 0.0 || !(g > 0.0))
                return double.NaN;
            Vector3d up = vessel.upAxis;
            if (up.sqrMagnitude <= 0.0)
                return double.NaN;
            return Vector3d.Dot(perturbation, up.normalized) / g;
        }

        /// <summary>Stock air density (kg/m^3) at an altitude; NaN without air there.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadDensity(CelestialBody body, double altitude)
        {
            if (body == null || !body.atmosphere)
                return double.NaN;
            try
            {
                double pressure = body.GetPressure(altitude);
                if (!(pressure > 0.0))
                    return double.NaN;
                return body.GetDensity(pressure, body.GetTemperature(altitude));
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        /// <summary>
        /// A chute's stock drag fields read off its part prefab (an unloaded vessel's proto
        /// module persists only state); false when the part or module is not found.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadPrefabChuteDrag(string partName, out double semiDrag, out double fullDrag)
        {
            semiDrag = double.NaN;
            fullDrag = double.NaN;
            if (string.IsNullOrEmpty(partName))
                return false;
            try
            {
                AvailablePart info = PartLoader.getPartInfoByName(partName.Replace('_', '.'));
                ModuleParachute prefab = info?.partPrefab != null
                    ? info.partPrefab.FindModuleImplementing<ModuleParachute>()
                    : null;
                if (prefab == null)
                    return false;
                semiDrag = prefab.semiDeployedDrag;
                fullDrag = prefab.fullyDeployedDrag;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CountOpenChutes(
            Vessel vessel,
            out ParachuteCanopyState canopy,
            out double fullDeployAltitudeAgl,
            out uint firstChutePid,
            out int openCount,
            out double dragNow,
            out double dragFull)
        {
            canopy = ParachuteCanopyState.None;
            fullDeployAltitudeAgl = double.NaN;
            firstChutePid = 0;
            openCount = 0;
            dragNow = 0.0;
            dragFull = 0.0;

            if (vessel.loaded && vessel.parts != null)
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part part = vessel.parts[i];
                    if (part == null)
                        continue;
                    ModuleParachute chute = part.FindModuleImplementing<ModuleParachute>();
                    if (chute == null)
                        continue;
                    ParachuteCanopyState state = Classify(chute.deploymentState);
                    Note(state, chute.deployAltitude, part.persistentId,
                        ref canopy, ref fullDeployAltitudeAgl, ref firstChutePid, ref openCount);
                    AccumulateChuteDrag(state, chute.semiDeployedDrag, chute.fullyDeployedDrag,
                        ref dragNow, ref dragFull);
                }
                return;
            }

            ProtoVessel proto = vessel.protoVessel;
            if (proto?.protoPartSnapshots == null)
                return;
            for (int i = 0; i < proto.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = proto.protoPartSnapshots[i];
                if (pps?.modules == null)
                    continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot module = pps.modules[m];
                    if (module == null || !string.Equals(module.moduleName, ParachuteModuleName, StringComparison.Ordinal))
                        continue;
                    ConfigNode values = module.moduleValues;
                    ParachuteCanopyState state = ClassifyPersistentState(values?.GetValue("persistentState"));
                    double deployAltitude = double.NaN;
                    string raw = values?.GetValue("deployAltitude");
                    if (!string.IsNullOrEmpty(raw))
                        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out deployAltitude);
                    Note(state, deployAltitude, pps.persistentId,
                        ref canopy, ref fullDeployAltitudeAgl, ref firstChutePid, ref openCount);
                    if (state != ParachuteCanopyState.None)
                    {
                        TryReadPrefabChuteDrag(pps.partName, out double semiDrag, out double fullDrag);
                        AccumulateChuteDrag(state, semiDrag, fullDrag, ref dragNow, ref dragFull);
                    }
                }
            }
        }

        /// <summary>
        /// Pure: adds one chute's stock drag to the open chutes' sums, now (fully open: its
        /// <c>fullyDeployedDrag</c>, semi: its <c>semiDeployedDrag</c>) and fully open. A chute
        /// not open (stowed, armed, cut) adds nothing (owner ruling Q10b). An open chute whose
        /// drag is unread poisons both sums with NaN, so the planner falls back.
        /// </summary>
        internal static void AccumulateChuteDrag(
            ParachuteCanopyState state,
            double semiDrag,
            double fullDrag,
            ref double dragNow,
            ref double dragFull)
        {
            if (state == ParachuteCanopyState.None)
                return;
            bool read = !double.IsNaN(semiDrag) && !double.IsInfinity(semiDrag) && semiDrag >= 0.0
                && !double.IsNaN(fullDrag) && !double.IsInfinity(fullDrag) && fullDrag > 0.0;
            if (!read)
            {
                dragNow = double.NaN;
                dragFull = double.NaN;
                return;
            }
            dragNow += state == ParachuteCanopyState.Deployed ? fullDrag : semiDrag;
            dragFull += fullDrag;
        }

        private static void Note(
            ParachuteCanopyState state,
            double deployAltitude,
            uint pid,
            ref ParachuteCanopyState canopy,
            ref double fullDeployAltitudeAgl,
            ref uint firstChutePid,
            ref int openCount)
        {
            if (state == ParachuteCanopyState.None)
                return;
            openCount++;
            if (firstChutePid == 0)
                firstChutePid = pid;
            if (state > canopy)
                canopy = state;
            if (state == ParachuteCanopyState.SemiDeployed
                && !double.IsNaN(deployAltitude)
                && (double.IsNaN(fullDeployAltitudeAgl) || deployAltitude > fullDeployAltitudeAgl))
            {
                fullDeployAltitudeAgl = deployAltitude;
            }
        }

        /// <summary>Pure: only an OPEN canopy counts; a staged chute still armed (ACTIVE), waiting
        /// for its pressure / altitude, does not (owner ruling 2026-11-21).</summary>
        internal static ParachuteCanopyState Classify(ModuleParachute.deploymentStates state)
        {
            switch (state)
            {
                case ModuleParachute.deploymentStates.DEPLOYED:
                    return ParachuteCanopyState.Deployed;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    return ParachuteCanopyState.SemiDeployed;
                default:
                    return ParachuteCanopyState.None;
            }
        }

        /// <summary>Pure: the canopy state a persisted <c>persistentState</c> string names.</summary>
        internal static ParachuteCanopyState ClassifyPersistentState(string persistentState)
        {
            if (string.Equals(persistentState, "DEPLOYED", StringComparison.OrdinalIgnoreCase))
                return ParachuteCanopyState.Deployed;
            if (string.Equals(persistentState, "SEMIDEPLOYED", StringComparison.OrdinalIgnoreCase))
                return ParachuteCanopyState.SemiDeployed;
            return ParachuteCanopyState.None;
        }
    }
}
