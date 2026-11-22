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
    /// <c>deployAltitude</c>) and, on a part without one, its RealChute <c>RealChuteModule</c>
    /// modules (by reflection, <see cref="RealChuteLiveRead"/>, into the node shape RealChute
    /// persists); an unloaded one from every persisted chute module
    /// <see cref="ParachuteProtoModuleRead"/> reads (stock <c>ModuleParachute</c>, an EVA
    /// kerbal's <c>ModuleEvaChute</c>, RealChute's <c>RealChuteModule</c>). For the full-canopy
    /// estimate it also reads the open chutes' drag: stock <c>semiDeployedDrag</c> /
    /// <c>fullyDeployedDrag</c> (from the part prefab when unloaded), or a RealChute part's
    /// open canopy area (<see cref="ParachuteProtoModuleRead.TryReadRealChuteCanopyArea"/>);
    /// the two are different units, so a vessel with both kinds open reads no drag and the
    /// estimate falls back (<see cref="OpenChuteTally.CombineDrag"/>). It also reads drag over
    /// weight (unpacked loaded vessels only) and the air density at the vessel and at the
    /// ground. Every game read sits in a non-inlined method so a headless caller never JITs it.
    /// </summary>
    internal static class ParachuteDescentLiveReader
    {
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

            var tally = new OpenChuteTally();
            CountOpenChutes(vessel, tally);
            LogRealChuteRead(tally, vessel.loaded, vessel.persistentId);
            ParachuteCanopyState canopy = tally.Canopy;
            string dragSource = tally.CombineDrag(out double dragNow, out double dragFull);
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
                OpenChuteDragSource = dragSource,
                DensityAtAltitude = ReadDensity(body, alt),
                DensityAtTouchdown = ReadDensity(body, ground),
                BodyName = body.name,
                StartUT = ut,
                Latitude = lat,
                Longitude = lon,
                Altitude = alt,
                VerticalSpeed = vessel.verticalSpeed,
                Canopy = canopy,
                FullDeployAltitudeAgl = tally.FullDeployAltitudeAgl,
                TerrainAltitude = terrain,
                BodyHasOcean = body.ocean,
                InAtmosphere = body.atmosphere && alt < body.atmosphereDepth,
                RootHeightAboveLowestPoint = rootHeight,
                SurfaceRelativeRotation = vessel.srfRelRotation,
                WorldUp = up,
                ChutePartPersistentId = tally.FirstChutePid,
                OpenChuteCount = tally.OpenCount,
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
        private static void CountOpenChutes(Vessel vessel, OpenChuteTally tally)
        {
            if (vessel.loaded && vessel.parts != null)
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part part = vessel.parts[i];
                    if (part == null)
                        continue;
                    ModuleParachute chute = part.FindModuleImplementing<ModuleParachute>();
                    if (chute == null)
                    {
                        NoteLoadedRealChutes(part, tally);
                        continue;
                    }
                    ParachuteCanopyState state = Classify(chute.deploymentState);
                    tally.NoteStockChute(state, chute.deployAltitude, part.persistentId,
                        chute.semiDeployedDrag, chute.fullyDeployedDrag);
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
                    if (module == null || !ParachuteProtoModuleRead.IsChuteModuleName(module.moduleName))
                        continue;
                    if (string.Equals(module.moduleName, ParachuteProtoModuleRead.RealChuteModuleName,
                            StringComparison.Ordinal))
                    {
                        tally.NoteRealChuteModule(module.moduleValues, pps.persistentId);
                        continue;
                    }
                    ParachuteCanopyState state = ClassifyProtoModule(
                        module.moduleName, module.moduleValues, out double deployAltitude);
                    double semiDrag = double.NaN;
                    double fullDrag = double.NaN;
                    if (state != ParachuteCanopyState.None)
                        TryReadPrefabChuteDrag(pps.partName, out semiDrag, out fullDrag);
                    tally.NoteStockChute(state, deployAltitude, pps.persistentId, semiDrag, fullDrag);
                }
            }
        }

        /// <summary>
        /// A loaded part's RealChute modules (a RealChute install converts stock chutes to
        /// them), read by reflection into the node RealChute persists. A stock part carries
        /// none, so a stock vessel adds nothing here.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NoteLoadedRealChutes(Part part, OpenChuteTally tally)
        {
            PartModuleList modules = part.Modules;
            if (modules == null)
                return;
            for (int m = 0; m < modules.Count; m++)
            {
                PartModule module = modules[m];
                if (module == null || !RealChuteLiveRead.IsRealChuteModuleType(module.GetType()))
                    continue;
                if (!RealChuteLiveRead.TryBuildModuleNode(
                        module, out ConfigNode node, out int unread, out string failure))
                {
                    tally.NoteRealChuteReadFailure(failure);
                    continue;
                }
                tally.RealChuteUnreadCanopies += unread;
                tally.NoteRealChuteModule(node, part.persistentId);
            }
        }

        /// <summary>
        /// Pure: one summary line (rate-limited) when the vessel carried a RealChute module (none on a stock
        /// install), and a Warn when one could not be read by reflection (a RealChute version
        /// with another shape reads as no chute).
        /// </summary>
        internal static void LogRealChuteRead(OpenChuteTally tally, bool loaded, uint vesselPid)
        {
            if (tally == null || (tally.RealChuteModules == 0 && tally.RealChuteReadFailures == 0))
                return;
            // The finalizer re-reads a background vessel on every cache refresh: rate-limited,
            // with the counts that change in the key so a changed read prints at once.
            ParsekLog.VerboseRateLimited("Extrapolator",
                string.Format(CultureInfo.InvariantCulture, "realchute-read.{0}.{1}.{2}.{3}",
                    vesselPid, tally.RealChuteModules, tally.RealChuteOpenModules, tally.RealChuteReadFailures),
                string.Format(CultureInfo.InvariantCulture,
                    "RealChute read: pid={0} source={1} modules={2} openModules={3} openCanopies={4} " +
                    "unreadCanopies={5} readFailures={6} areaNow={7} areaFull={8}",
                    vesselPid,
                    loaded ? "loaded-reflection" : "proto-node",
                    tally.RealChuteModules,
                    tally.RealChuteOpenModules,
                    tally.RealChuteOpenCanopies,
                    tally.RealChuteUnreadCanopies,
                    tally.RealChuteReadFailures,
                    FormatArea(tally.RealChuteAreaNow, tally.RealChuteOpenModules),
                    FormatArea(tally.RealChuteAreaFull, tally.RealChuteOpenModules)),
                minIntervalSeconds: 30.0);
            if (tally.RealChuteReadFailures > 0)
            {
                ParsekLog.WarnRateLimited("Extrapolator",
                    "realchute-read-failed." + (tally.RealChuteLastFailure ?? "unknown"),
                    string.Format(CultureInfo.InvariantCulture,
                        "RealChute module not readable: pid={0} failures={1} reason={2}; " +
                        "its canopies read as not open",
                        vesselPid, tally.RealChuteReadFailures, tally.RealChuteLastFailure ?? "unknown"),
                    minIntervalSeconds: 60.0);
            }
        }

        private static string FormatArea(double area, int openModules)
        {
            if (openModules == 0)
                return "-";
            return double.IsNaN(area) ? "NaN" : area.ToString("F2", CultureInfo.InvariantCulture);
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
            OpenChuteTally.AccumulateChuteDrag(state, semiDrag, fullDrag, ref dragNow, ref dragFull);
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

        /// <summary>
        /// Pure: the canopy state of one persisted module of an unloaded vessel, read through
        /// <see cref="ParachuteProtoModuleRead.TryReadCanopy"/>, and its full-deployment height
        /// above the ground (NaN when absent). None for a module that is not a chute and for a
        /// chute that is not open: stowed, cut, or staged and still armed (owner ruling Q10b).
        /// </summary>
        internal static ParachuteCanopyState ClassifyProtoModule(
            string moduleName, ConfigNode moduleValues, out double deployAltitude)
        {
            if (!ParachuteProtoModuleRead.TryReadCanopy(
                    moduleName, moduleValues, out string persistentState, out deployAltitude))
            {
                deployAltitude = double.NaN;
                return ParachuteCanopyState.None;
            }
            return ClassifyPersistentState(persistentState);
        }

        /// <summary>Pure: the canopy state a persisted <c>persistentState</c> string names.</summary>
        internal static ParachuteCanopyState ClassifyPersistentState(string persistentState)
        {
            return ParachuteProtoModuleRead.ClassifyPersistentState(persistentState);
        }
    }

    /// <summary>
    /// The open chutes of one vessel as <c>ParachuteDescentLiveReader</c> counts them:
    /// the strongest canopy, the largest full-deploy altitude of a semi-deployed chute, the
    /// first open chute's part, the open chute count, and the drag sums kept apart by kind
    /// (stock drag, RealChute canopy area: different units). Pure; it references no reader
    /// type, so the reader -> tally edge stays one-way.
    /// </summary>
    internal sealed class OpenChuteTally
    {
        public ParachuteCanopyState Canopy = ParachuteCanopyState.None;
        public double FullDeployAltitudeAgl = double.NaN;
        public uint FirstChutePid;
        public int OpenCount;

        public bool StockOpen;
        public double StockDragNow;
        public double StockDragFull;

        public int RealChuteModules;
        public int RealChuteOpenModules;
        public int RealChuteOpenCanopies;
        public int RealChuteUnreadCanopies;
        public int RealChuteReadFailures;
        public string RealChuteLastFailure;
        public double RealChuteAreaNow;
        public double RealChuteAreaFull;

        /// <summary>A stock (or EVA) chute; its drag NaN when unread.</summary>
        public void NoteStockChute(
            ParachuteCanopyState state, double deployAltitude, uint pid, double semiDrag, double fullDrag)
        {
            Note(state, deployAltitude, pid);
            if (state == ParachuteCanopyState.None)
                return;
            StockOpen = true;
            AccumulateChuteDrag(state, semiDrag, fullDrag, ref StockDragNow, ref StockDragFull);
        }

        /// <summary>
        /// A RealChute module in its persisted node shape (a proto module, or a loaded module
        /// read into that shape): its most open canopy counts like one chute, its open
        /// canopies' area goes to the RealChute sums.
        /// </summary>
        public void NoteRealChuteModule(ConfigNode moduleValues, uint pid)
        {
            RealChuteModules++;
            ParachuteCanopyState state = ParachuteCanopyState.None;
            double deployAltitude = double.NaN;
            if (ParachuteProtoModuleRead.TryReadCanopy(ParachuteProtoModuleRead.RealChuteModuleName,
                    moduleValues, out string persistentState, out double readAltitude))
            {
                state = ParachuteProtoModuleRead.ClassifyPersistentState(persistentState);
                deployAltitude = readAltitude;
            }
            Note(state, deployAltitude, pid);
            if (state == ParachuteCanopyState.None)
                return;
            RealChuteOpenModules++;
            ParachuteProtoModuleRead.TryReadRealChuteCanopyArea(
                moduleValues, out double areaNow, out double areaFull, out int openCanopies);
            RealChuteOpenCanopies += openCanopies;
            RealChuteAreaNow += areaNow;
            RealChuteAreaFull += areaFull;
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

        private void Note(ParachuteCanopyState state, double deployAltitude, uint pid)
        {
            if (state == ParachuteCanopyState.None)
                return;
            OpenCount++;
            if (FirstChutePid == 0)
                FirstChutePid = pid;
            if (state > Canopy)
                Canopy = state;
            if (state == ParachuteCanopyState.SemiDeployed
                && !double.IsNaN(deployAltitude)
                && (double.IsNaN(FullDeployAltitudeAgl) || deployAltitude > FullDeployAltitudeAgl))
            {
                FullDeployAltitudeAgl = deployAltitude;
            }
        }

        /// <summary>A loaded RealChute module reflection could not read.</summary>
        public void NoteRealChuteReadFailure(string failure)
        {
            RealChuteModules++;
            RealChuteReadFailures++;
            RealChuteLastFailure = failure;
        }

        /// <summary>
        /// The drag sums the planner reads and their source: <c>stock</c> (the stock sums, as
        /// before RealChute was read), <c>realchute-area</c>, <c>mixed</c> (both kinds open:
        /// NaN, the planner falls back) or <c>-</c> (nothing open: zero).
        /// </summary>
        public string CombineDrag(out double dragNow, out double dragFull)
        {
            bool realChuteOpen = RealChuteOpenModules > 0;
            if (StockOpen && realChuteOpen)
            {
                dragNow = double.NaN;
                dragFull = double.NaN;
                return "mixed";
            }
            if (realChuteOpen)
            {
                dragNow = RealChuteAreaNow;
                dragFull = RealChuteAreaFull;
                return "realchute-area";
            }
            dragNow = StockDragNow;
            dragFull = StockDragFull;
            return StockOpen ? "stock" : "-";
        }
    }
}
