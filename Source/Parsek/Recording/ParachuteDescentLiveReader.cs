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
    /// <c>persistentState</c> / <c>deployAltitude</c>. Every game read sits in a
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
                out uint chutePid, out int openCount);
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

            input = new ParachuteDescentInput
            {
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CountOpenChutes(
            Vessel vessel,
            out ParachuteCanopyState canopy,
            out double fullDeployAltitudeAgl,
            out uint firstChutePid,
            out int openCount)
        {
            canopy = ParachuteCanopyState.None;
            fullDeployAltitudeAgl = double.NaN;
            firstChutePid = 0;
            openCount = 0;

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
                }
            }
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
