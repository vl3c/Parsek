using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live checks for the two mining rulings of 2026-10-15, run at the Space Center on a career
    /// host with no vessel (lane HV-2).
    ///
    /// <para><b>Resource scans (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED).</b> The xUnit layer
    /// (<c>ResourceScanLedgerTests</c>) proves the event, the converter, the ledger row and the
    /// add-only re-apply against fakes. Only live KSP proves the two seams those fakes stand in
    /// for: that stock's own <c>ResourceMap.UnlockBiome</c> / <c>UnlockPlanet</c> really reach
    /// the Harmony capture pair, and that the state patcher's re-apply really writes stock's
    /// scan state. Each scan cell unlocks a locked Minmus biome (or the whole body) through the
    /// stock call, checks one ownerless <c>ResourceScanUnlocked</c> ledger row and one
    /// <c>ResourceScanCompleted</c> event landed, then relocks it the way a rewind's load does
    /// (the entry leaves <c>ResourceScenario.gameSettings</c>, then <c>ResetCache</c>). A recalc
    /// with the clock cut off before the scan must leave it locked (the scan is in the committed
    /// future); a recalc at the live clock must unlock it again without writing a second row.
    /// Everything (the row, the event, the scan state) is reverted in the finally.</para>
    ///
    /// <para><b>Drill rates (HARVEST-ROUTE-PLAUSIBILITY-CAP).</b> The cap is only as good as
    /// the recorder's reading of the stock converters: a stock drill or ISRU that is a subclass,
    /// or a drill whose abundance read fails, silently leaves every mining window uncapped. The
    /// rate cell runs <see cref="FlightRecorder.ReadHarvestConverterRate"/> over the real stock
    /// drill and ISRU part prefabs at the richest Minmus ore sample it finds, and requires every
    /// one to be modeled with usable figures. Both stock drills also carry a
    /// <c>ModuleAsteroidDrill</c>; on a vessel with no asteroid aboard it must read as no output,
    /// or no stock drill would ever be capped.</para>
    /// </summary>
    public class HarvestCapScanLedgerInGameTests
    {
        private const string Category = "HarvestCapScanLedger";
        private const string Tag = "HarvestCapScanLedger";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static readonly string[] DrillPartNames = { "MiniDrill", "RadialDrill" };
        private static readonly string[] IsruPartNames = { "MiniISRU", "ISRU" };

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "A stock biome scan is ledgered, stays locked before its UT after a relock, and is re-applied at the live clock")]
        public void BiomeScanIsLedgeredAndReappliedAfterRelock()
        {
            RunScanCell(wholeBody: false);
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "A stock whole-body scan is ledgered, stays locked before its UT after a relock, and is re-applied at the live clock")]
        public void PlanetScanIsLedgeredAndReappliedAfterRelock()
        {
            RunScanCell(wholeBody: true);
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "The recorder models every stock drill and ISRU prefab's converters with usable rates")]
        public void StockDrillAndIsruRatesAreModeled()
        {
            if (ResourceMap.Instance == null)
            {
                InGameAssert.Skip("ResourceMap is not loaded in this scene");
                return;
            }
            CelestialBody minmus = FindBody("Minmus");
            if (minmus == null)
            {
                InGameAssert.Skip("No body named Minmus on this install");
                return;
            }

            double lat, lon, abundance;
            FindRichestOreSample(minmus, out lat, out lon, out abundance);
            InGameAssert.IsTrue(abundance > 0.0 && abundance <= 1.0,
                "Minmus ore abundance at the richest sample must be in (0, 1], got "
                + abundance.ToString("R", IC));

            var inputs = new List<HarvestConverterRate>();
            double expectedOre = 0.0;
            int drillParts = 0, isruParts = 0, drillModules = 0, isruModules = 0;
            var missingParts = new List<string>();
            var outputNames = new HashSet<string>(StringComparer.Ordinal);

            int spaceObjectDrillModules = 0;
            foreach (string name in DrillPartNames)
            {
                List<BaseConverter> converters = PrefabConverters(name);
                if (converters == null) { missingParts.Add(name); continue; }
                drillParts++;
                int surfaceDrills = 0;
                foreach (BaseConverter c in converters)
                {
                    HarvestConverterRate rate = FlightRecorder.ReadHarvestConverterRate(
                        c, 1.0, minmus, lat, lon, 0.0, hostCarriesSpaceObject: false);
                    if (c is ModuleAsteroidDrill || c is ModuleCometDrill)
                    {
                        // Stock drills also carry an asteroid drill. With no asteroid or comet
                        // aboard it mines nothing and must not unbound the window; with one it
                        // stays unmodeled.
                        InGameAssert.AreEqual(HarvestConverterKind.NoResourceOutput, rate.Kind,
                            name + ": a " + c.GetType().Name + " with no space object aboard must read as no output");
                        HarvestConverterRate aboard = FlightRecorder.ReadHarvestConverterRate(
                            c, 1.0, minmus, lat, lon, 0.0, hostCarriesSpaceObject: true);
                        InGameAssert.AreEqual(HarvestConverterKind.Unmodeled, aboard.Kind,
                            name + ": a " + c.GetType().Name + " with a space object aboard must stay unmodeled");
                        inputs.Add(rate);
                        spaceObjectDrillModules++;
                        continue;
                    }
                    InGameAssert.AreEqual(HarvestConverterKind.Drill, rate.Kind,
                        name + ": a " + c.GetType().Name + " converter is not modeled as a drill");
                    InGameAssert.AreEqual("Ore", rate.ResourceName, name + ": drill resource");
                    InGameAssert.IsTrue(rate.Efficiency > 0.0, name + ": drill Efficiency must be positive");
                    InGameAssert.IsTrue(rate.Multiplier > 0.0, name + ": drill multiplier must be positive");
                    InGameAssert.IsTrue(Math.Abs(rate.Abundance - abundance) < 1e-9,
                        name + ": the drill read abundance " + rate.Abundance.ToString("R", IC)
                        + " where ResourceMap gives " + abundance.ToString("R", IC));
                    expectedOre += rate.Abundance * rate.Efficiency * rate.Multiplier;
                    inputs.Add(rate);
                    drillModules++;
                    surfaceDrills++;
                }
                InGameAssert.IsTrue(surfaceDrills > 0, name + ": no surface drill module found");
            }

            foreach (string name in IsruPartNames)
            {
                List<BaseConverter> converters = PrefabConverters(name);
                if (converters == null) { missingParts.Add(name); continue; }
                isruParts++;
                foreach (BaseConverter c in converters)
                {
                    HarvestConverterRate rate = FlightRecorder.ReadHarvestConverterRate(
                        c, 1.0, minmus, lat, lon, 0.0, hostCarriesSpaceObject: false);
                    InGameAssert.AreEqual(HarvestConverterKind.Recipe, rate.Kind,
                        name + ": a " + c.GetType().Name + " converter is not modeled as a recipe");
                    InGameAssert.IsTrue(rate.Outputs != null && rate.Outputs.Count > 0,
                        name + ": the recipe has no outputs");
                    foreach (KeyValuePair<string, double> output in rate.Outputs)
                    {
                        InGameAssert.IsTrue(output.Value > 0.0,
                            name + ": output " + output.Key + " ratio must be positive");
                        outputNames.Add(output.Key);
                    }
                    inputs.Add(rate);
                    isruModules++;
                }
            }

            InGameAssert.IsTrue(drillParts > 0 && isruParts > 0,
                "Needs at least one stock drill and one stock ISRU part; missing [" + string.Join(",", missingParts.ToArray()) + "]");

            Dictionary<string, double> rates = RouteHarvestCapture.ComputePlausibleRates(inputs);
            InGameAssert.IsNotNull(rates, "The stock drill + ISRU set must be bounded, not unbounded");
            double ore;
            InGameAssert.IsTrue(rates.TryGetValue("Ore", out ore), "The bounded set has no Ore rate");
            InGameAssert.IsTrue(Math.Abs(ore - expectedOre) < 1e-9,
                "Ore rate " + ore.ToString("R", IC) + " != summed drill rate " + expectedOre.ToString("R", IC));
            foreach (string fuel in new[] { "LiquidFuel", "Oxidizer", "MonoPropellant" })
                InGameAssert.IsTrue(outputNames.Contains(fuel) && rates.ContainsKey(fuel),
                    "The stock ISRU recipes must produce " + fuel);

            // A catch-up burst: six hours of drill output landing inside a one-minute window
            // is credited only rate x window x margin.
            double windowSeconds = 60.0;
            double limit = RouteHarvestCapture.ComputePlausibleLimit(rates, "Ore", windowSeconds);
            double burst = ore * 6.0 * 3600.0;
            InGameAssert.IsTrue(limit > 0.0 && limit < burst,
                "A 6 h burst (" + burst.ToString("R", IC) + ") must exceed the 60 s limit ("
                + limit.ToString("R", IC) + ")");

            ParsekLog.Info(Tag,
                "drill rate live check passed: body=Minmus lat=" + lat.ToString("F1", IC)
                + " lon=" + lon.ToString("F1", IC) + " abundance=" + abundance.ToString("R", IC)
                + " drillParts=" + drillParts.ToString(IC) + " drillModules=" + drillModules.ToString(IC)
                + " spaceObjectDrills=" + spaceObjectDrillModules.ToString(IC)
                + " isruParts=" + isruParts.ToString(IC) + " isruModules=" + isruModules.ToString(IC)
                + " missing=[" + string.Join(",", missingParts.ToArray()) + "]"
                + " rates=" + FlightRecorder.FormatPlausibleRates(rates)
                + " oreLimit60s=" + limit.ToString("R", IC));
        }

        private static void RunScanCell(bool wholeBody)
        {
            string kind = wholeBody ? "body" : "biome";
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                return;
            }
            ResourceMap map = ResourceMap.Instance;
            ResourceScenario scenario = ResourceScenario.Instance;
            if (map == null || scenario == null || scenario.gameSettings == null)
            {
                InGameAssert.Skip("ResourceMap / ResourceScenario is not loaded in this scene");
                return;
            }
            CelestialBody body = FindBody("Minmus");
            if (body == null)
            {
                InGameAssert.Skip("No body named Minmus on this install");
                return;
            }
            int planetId = body.flightGlobalsIndex;
            string bodyName = body.bodyName;

            string biome = null;
            if (wholeBody)
            {
                if (map.IsPlanetScanned(planetId))
                {
                    InGameAssert.Skip("Minmus is already scanned on this host - needs an unscanned body");
                    return;
                }
            }
            else
            {
                biome = FirstLockedBiome(map, body);
                if (biome == null)
                {
                    InGameAssert.Skip("Every Minmus biome is already unlocked on this host");
                    return;
                }
            }

            int rowsBefore = CountScanRows(bodyName, biome);
            int eventsBefore = CountScanEvents(bodyName, biome);
            InGameAssert.AreEqual(0, rowsBefore, "a scan row for this target already exists on the host");

            double scanUT = Planetarium.GetUniversalTime();
            if (scanUT <= 1.0)
            {
                InGameAssert.Skip("The clock is at UT " + scanUT.ToString("R", IC)
                    + "; the before-the-scan recalc needs UT > 1");
                return;
            }

            bool unlockedByStock = false;
            try
            {
                // The stock call a scanner part makes; the Harmony pair captures it.
                if (wholeBody) map.UnlockPlanet(planetId);
                else map.UnlockBiome(planetId, biome);
                unlockedByStock = true;

                InGameAssert.IsTrue(IsUnlocked(map, planetId, biome), "the stock unlock did not take");
                InGameAssert.AreEqual(1, CountScanRows(bodyName, biome) - rowsBefore,
                    "the stock " + kind + " scan must land exactly one ResourceScanUnlocked ledger row");
                InGameAssert.AreEqual(1, CountScanEvents(bodyName, biome) - eventsBefore,
                    "the stock " + kind + " scan must emit exactly one ResourceScanCompleted event");
                GameAction row = FindScanRow(bodyName, biome);
                InGameAssert.IsTrue(string.IsNullOrEmpty(row.RecordingId),
                    "a scan at the Space Center is ownerless, got recording '" + row.RecordingId + "'");

                // The rewind half: the loaded save does not hold the scan.
                Relock(scenario, map, planetId, biome);
                InGameAssert.IsFalse(IsUnlocked(map, planetId, biome), "the relock did not take");

                double beforeScan = scanUT - 0.5;
                LedgerOrchestrator.RecalculateAndPatch(beforeScan);
                InGameAssert.IsFalse(IsUnlocked(map, planetId, biome),
                    "a recalc cut off before the scan's UT must leave the " + kind + " locked");

                // The clock reaches the scan.
                LedgerOrchestrator.RecalculateAndPatch();
                InGameAssert.IsTrue(IsUnlocked(map, planetId, biome),
                    "a recalc at the live clock must re-apply the committed " + kind + " scan");
                InGameAssert.AreEqual(1, CountScanRows(bodyName, biome) - rowsBefore,
                    "the re-apply must not write a second ledger row");
                InGameAssert.AreEqual(1, CountScanEvents(bodyName, biome) - eventsBefore,
                    "the re-apply must not emit a second event");

                ParsekLog.Info(Tag,
                    "scan ledger live check passed: kind=" + kind + " body=" + bodyName
                    + " biome='" + (biome ?? "") + "' rows=1 events=1 lockedBeforeUT=1 reapplied=1");
            }
            finally
            {
                int rowsRemoved = 0, eventsRemoved = 0;
                try
                {
                    rowsRemoved = RemoveScanRows(bodyName, biome, scanUT);
                    eventsRemoved = RemoveScanEvents(bodyName, biome, scanUT);
                    if (unlockedByStock)
                        Relock(scenario, map, planetId, biome);
                    LedgerOrchestrator.RecalculateAndPatch();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, "scan ledger live check teardown threw: " + ex);
                }
                ParsekLog.Info(Tag,
                    "scan ledger live check teardown: kind=" + kind + " rowsRemoved=" + rowsRemoved.ToString(IC)
                    + " eventsRemoved=" + eventsRemoved.ToString(IC)
                    + " locked=" + (IsUnlocked(map, planetId, biome) ? "0" : "1"));
            }
        }

        private static bool IsUnlocked(ResourceMap map, int planetId, string biome)
        {
            return biome == null ? map.IsPlanetScanned(planetId) : map.IsBiomeUnlocked(planetId, biome);
        }

        /// <summary>
        /// What a rewind's load does to stock's scan state: the entry is absent from the loaded
        /// <c>ResourceScenario.gameSettings</c> and <c>ResetCache</c> drops the map's copy, which
        /// it rebuilds from those settings on the next read (decompiled KSP 1.12.5).
        /// </summary>
        private static void Relock(ResourceScenario scenario, ResourceMap map, int planetId, string biome)
        {
            if (biome == null)
                scenario.gameSettings.GetPlanetScanInfo().RemoveAll(p => p.PlanetId == planetId);
            else
                scenario.gameSettings.GetBiomeLockInfo().RemoveAll(
                    b => b.PlanetId == planetId && b.BiomeName == biome);
            map.ResetCache();
        }

        private static string FirstLockedBiome(ResourceMap map, CelestialBody body)
        {
            if (body.BiomeMap == null || body.BiomeMap.Attributes == null)
                return null;
            foreach (CBAttributeMapSO.MapAttribute attr in body.BiomeMap.Attributes)
            {
                if (attr == null || string.IsNullOrEmpty(attr.name)) continue;
                if (!map.IsBiomeUnlocked(body.flightGlobalsIndex, attr.name))
                    return attr.name;
            }
            return null;
        }

        private static bool IsScanRow(GameAction a, string bodyName, string biome)
        {
            return a != null && a.Type == GameActionType.ResourceScanUnlocked
                && a.ScanBodyName == bodyName
                && string.Equals(a.ScanBiomeName ?? "", biome ?? "", StringComparison.Ordinal);
        }

        private static bool IsScanEvent(GameStateEvent e, string bodyName, string biome)
        {
            return e.eventType == GameStateEventType.ResourceScanCompleted
                && e.key == bodyName
                && string.Equals(e.detail ?? "", biome == null ? "" : "biome=" + biome, StringComparison.Ordinal);
        }

        private static int CountScanRows(string bodyName, string biome)
        {
            int n = 0;
            foreach (GameAction a in Ledger.Actions)
                if (IsScanRow(a, bodyName, biome)) n++;
            return n;
        }

        private static GameAction FindScanRow(string bodyName, string biome)
        {
            foreach (GameAction a in Ledger.Actions)
                if (IsScanRow(a, bodyName, biome)) return a;
            return null;
        }

        private static int CountScanEvents(string bodyName, string biome)
        {
            int n = 0;
            foreach (GameStateEvent e in GameStateStore.Events)
                if (IsScanEvent(e, bodyName, biome)) n++;
            return n;
        }

        private static int RemoveScanRows(string bodyName, string biome, double sinceUT)
        {
            int removed = 0;
            for (int i = Ledger.Actions.Count - 1; i >= 0; i--)
            {
                GameAction a = Ledger.Actions[i];
                if (IsScanRow(a, bodyName, biome) && a.UT >= sinceUT - 0.001)
                {
                    Ledger.RemoveActionAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static int RemoveScanEvents(string bodyName, string biome, double sinceUT)
        {
            var targets = new List<GameStateEvent>();
            foreach (GameStateEvent e in GameStateStore.Events)
                if (IsScanEvent(e, bodyName, biome) && e.ut >= sinceUT - 0.001)
                    targets.Add(e);
            int removed = 0;
            foreach (GameStateEvent e in targets)
                if (GameStateStore.RemoveEvent(e)) removed++;
            return removed;
        }

        private static CelestialBody FindBody(string name)
        {
            if (FlightGlobals.Bodies == null) return null;
            foreach (CelestialBody b in FlightGlobals.Bodies)
                if (b != null && b.bodyName == name) return b;
            return null;
        }

        private static List<BaseConverter> PrefabConverters(string partName)
        {
            AvailablePart info = PartLoader.getPartInfoByName(partName);
            if (info == null || info.partPrefab == null) return null;
            List<BaseConverter> converters = info.partPrefab.FindModulesImplementing<BaseConverter>();
            return converters != null && converters.Count > 0 ? converters : null;
        }

        /// <summary>The highest surface Ore abundance on a coarse lat / lon grid of the body.</summary>
        private static void FindRichestOreSample(CelestialBody body, out double lat, out double lon,
            out double abundance)
        {
            lat = 0.0; lon = 0.0; abundance = -1.0;
            for (int la = -60; la <= 60; la += 10)
            {
                for (int lo = -180; lo < 180; lo += 15)
                {
                    double a = ResourceMap.Instance.GetAbundance(new AbundanceRequest
                    {
                        Altitude = 0.0,
                        BodyId = body.flightGlobalsIndex,
                        CheckForLock = false,
                        Latitude = la,
                        Longitude = lo,
                        ResourceType = HarvestTypes.Planetary,
                        ResourceName = "Ore"
                    });
                    if (a > abundance) { abundance = a; lat = la; lon = lo; }
                }
            }
        }
    }
}
