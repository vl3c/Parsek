using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live checks for the converter time-jump policy (TA-6, todo
    /// TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND) and the Mobile Processing Lab's ledger
    /// capture (LAB-SCIENCE-NEVER-LEDGERED), run at the Space Center on a career host (lane
    /// LAB-1).
    ///
    /// <para><b>Jumps.</b> The xUnit layer (<c>ConverterJumpPolicyTests</c>) proves the shift
    /// rule against ConfigNodes and a name predicate. Only live KSP proves the seams it stands
    /// in for: that <c>AssemblyLoader</c> resolves the stock drill, ISRU and lab module names
    /// to <c>BaseConverter</c> subclasses, that both real jump entry points reach the policy
    /// and rewrite an unloaded vessel's proto module snapshots, and that the reflected clock
    /// field is the one stock converters run on. Each jump cell adds a drill, an ISRU and a
    /// lab as three single-part vessels in a high Kerbin orbit (unloaded at the Space Center),
    /// with converter clocks 600 s behind the live clock, a never-ran converter and a
    /// non-converter that persists a field of the same name, then runs the real jump and
    /// requires every running converter to still owe exactly 600 s.</para>
    ///
    /// <para><b>Lab science.</b> The xUnit layer (<c>ScienceLabLedgerTests</c>) proves the
    /// event, the conversion and the walk. Only live KSP proves the Harmony pair really wraps
    /// stock's <c>ModuleScienceLab.RecoverScienceLabs</c> and its private
    /// <c>OnTransmissionComplete</c>, and that the pool delta it reads lands one ownerless
    /// ledger row that a recalc keeps. The recovery cell pays out a hand-built lab snapshot
    /// through the stock call; the transmission cell checks the pair is attached to the
    /// private handler (a renamed member would leave the patch silently unapplied).</para>
    /// </summary>
    public class ConverterJumpLabInGameTests
    {
        private const string Category = "ConverterJumpLab";
        private const string Tag = "ConverterJumpLab";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const double OwedSeconds = 600.0;
        private const double JumpSeconds = 3600.0;
        private const float LabStoredScience = 12.5f;

        private const string LabPart = "Large.Crewed.Lab";
        private const string IsruPart = "MiniISRU";
        private const string DrillPart = "MiniDrill";

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "The forward jump shifts every unloaded converter clock by the jump, keeping the catch-up owed before it")]
        public void ForwardJumpShiftsUnloadedConverterClocks()
        {
            RunUnloadedJumpCell("forward");
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "The epoch-shift jump shifts every unloaded converter clock by the jump, keeping the catch-up owed before it")]
        public void EpochShiftJumpShiftsUnloadedConverterClocks()
        {
            RunUnloadedJumpCell("epoch-shift");
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "The reflected converter clock field is the one the stock drill, ISRU and lab modules run on")]
        public void LoadedConverterClockFieldIsTheStockOne()
        {
            InGameAssert.IsTrue(ConverterJumpPolicy.LoadedTimestampFieldResolved,
                "BaseConverter.lastUpdateTime did not resolve by reflection");

            var checkedModules = new List<string>();
            foreach (string partName in new[] { DrillPart, IsruPart, LabPart })
            {
                AvailablePart info = PartLoader.getPartInfoByName(partName);
                if (info == null || info.partPrefab == null)
                {
                    InGameAssert.Skip("No stock part named " + partName + " on this install");
                    return;
                }
                List<BaseConverter> converters = info.partPrefab.FindModulesImplementing<BaseConverter>();
                InGameAssert.IsTrue(converters != null && converters.Count > 0,
                    partName + " carries no BaseConverter module");
                foreach (BaseConverter c in converters)
                {
                    double original = ConverterJumpPolicy.ReadLoadedTimestamp(c);
                    try
                    {
                        double now = Planetarium.GetUniversalTime() + 1000.0;
                        InGameAssert.IsTrue(ConverterJumpPolicy.WriteLoadedTimestamp(c, now - OwedSeconds),
                            partName + ": the clock field could not be written");
                        var outcome = ConverterJumpPolicy.ShiftLoadedConverter(
                            c, now, now + JumpSeconds, out double oldTime, out double newTime);
                        InGameAssert.AreEqual(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome,
                            partName + ": " + c.GetType().Name + " was not shifted");
                        double read = ConverterJumpPolicy.ReadLoadedTimestamp(c);
                        InGameAssert.IsTrue(Math.Abs(read - (now + JumpSeconds - OwedSeconds)) < 1e-6,
                            partName + ": " + c.GetType().Name + " reads " + read.ToString("R", IC)
                            + " after the shift, expected " + (now + JumpSeconds - OwedSeconds).ToString("R", IC));
                        // The module's own persisted field must carry the same value the
                        // reflection wrote, or the field is not the one stock saves and runs on.
                        var saved = new ConfigNode("MODULE");
                        c.Save(saved);
                        double persisted;
                        InGameAssert.IsTrue(double.TryParse(saved.GetValue(ConverterJumpPolicy.TimestampKey),
                                NumberStyles.Float, IC, out persisted)
                            && Math.Abs(persisted - read) < 1e-3,
                            partName + ": " + c.GetType().Name + " persists "
                            + (saved.GetValue(ConverterJumpPolicy.TimestampKey) ?? "<none>")
                            + " where the reflected field reads " + read.ToString("R", IC));
                        InGameAssert.IsTrue(ConverterJumpPolicy.IsConverterModuleName(c.GetType().Name),
                            partName + ": " + c.GetType().Name + " is not recognised as a converter by name");
                        checkedModules.Add(partName + ":" + c.GetType().Name);
                    }
                    finally
                    {
                        if (!double.IsNaN(original))
                            ConverterJumpPolicy.WriteLoadedTimestamp(c, original);
                    }
                }
            }

            InGameAssert.IsFalse(ConverterJumpPolicy.IsConverterModuleName("ModuleScienceLab"),
                "ModuleScienceLab is not a converter and must not be shifted");
            ParsekLog.Info(Tag, "loaded converter clock live check passed: modules=["
                + string.Join(",", checkedModules.ToArray()) + "]");
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "A stock lab recovery pays its stored science into one ownerless ledger row that a recalc keeps")]
        public void LabRecoveryCreditReachesTheLedger()
        {
            if (HighLogic.CurrentGame == null || ResearchAndDevelopment.Instance == null)
            {
                InGameAssert.Skip("No career R&D in this scene (sandbox or no game)");
                return;
            }
            if (LedgerOrchestrator.Science == null || !LedgerOrchestrator.Science.HasSeed)
            {
                InGameAssert.Skip("The ledger's science module is not seeded yet");
                return;
            }
            double ut = Planetarium.GetUniversalTime();
            if (ut <= 1.0)
            {
                InGameAssert.Skip("The clock is at UT " + ut.ToString("R", IC)
                    + "; the before-the-credit recalc needs UT > 1");
                return;
            }

            ProtoVessel labVessel = BuildLabSnapshot(LabStoredScience);
            InGameAssert.IsNotNull(labVessel, "could not build a lab snapshot");

            int rowsBefore = CountLabRows(ut);
            int eventsBefore = CountLabEvents(ut);
            InGameAssert.AreEqual(0, rowsBefore, "a lab row at or after this UT already exists on the host");
            float poolBefore = ResearchAndDevelopment.Instance.Science;
            bool paid = false;
            try
            {
                // The stock call vessel recovery makes; the Harmony pair captures it.
                ModuleScienceLab.RecoverScienceLabs(labVessel, null);
                paid = true;

                float poolAfter = ResearchAndDevelopment.Instance.Science;
                InGameAssert.IsTrue(Math.Abs(poolAfter - poolBefore - LabStoredScience) < 0.01f,
                    "the stock recovery must credit " + LabStoredScience.ToString("R", IC) + " science, moved "
                    + (poolAfter - poolBefore).ToString("R", IC));
                InGameAssert.AreEqual(1, CountLabEvents(ut) - eventsBefore,
                    "the stock recovery must emit exactly one ScienceLabCredited event");
                InGameAssert.AreEqual(1, CountLabRows(ut) - rowsBefore,
                    "the stock recovery must land exactly one ScienceLabEarning ledger row");
                GameAction row = FindLabRow(ut);
                InGameAssert.IsTrue(string.IsNullOrEmpty(row.RecordingId),
                    "a recovery at the Space Center is ownerless, got recording '" + row.RecordingId + "'");
                InGameAssert.AreEqual(ScienceMethod.Recovered, row.Method, "the row's payout method");
                InGameAssert.IsTrue(Math.Abs(row.ScienceAwarded - LabStoredScience) < 0.01f,
                    "the row carries " + row.ScienceAwarded.ToString("R", IC) + " science");

                // The rewind half: a recalc cut off before the credit does not count it, one at
                // the live clock does, and the live pool keeps it.
                double cutoff = row.UT - 0.5;
                LedgerOrchestrator.RecalculateAndPatch(cutoff);
                double runningBefore = LedgerOrchestrator.Science.GetRunningScience();
                LedgerOrchestrator.RecalculateAndPatch();
                double runningLive = LedgerOrchestrator.Science.GetRunningScience();
                InGameAssert.IsTrue(Math.Abs(runningLive - runningBefore - LabStoredScience) < 0.01,
                    "the ledger walk must count the lab credit after its UT only: running "
                    + runningBefore.ToString("R", IC) + " before, " + runningLive.ToString("R", IC) + " live");
                float poolLive = ResearchAndDevelopment.Instance.Science;
                InGameAssert.IsTrue(Math.Abs(poolLive - poolAfter) < 0.01f,
                    "a recalc at the live clock must keep the lab credit in the pool: "
                    + poolAfter.ToString("R", IC) + " before the recalc, " + poolLive.ToString("R", IC) + " after");
                InGameAssert.AreEqual(1, CountLabRows(ut) - rowsBefore,
                    "the recalc must not write a second ledger row");

                ParsekLog.Info(Tag, "lab recovery ledger live check passed: credit="
                    + LabStoredScience.ToString("F2", IC) + " rows=1 events=1 method=Recovered owner=''"
                    + " runningBefore=" + runningBefore.ToString("F2", IC)
                    + " runningLive=" + runningLive.ToString("F2", IC) + " poolKept=1");
            }
            finally
            {
                int rowsRemoved = 0, eventsRemoved = 0;
                try
                {
                    rowsRemoved = RemoveLabRows(ut);
                    eventsRemoved = RemoveLabEvents(ut);
                    if (paid)
                    {
                        using (SuppressionGuard.Resources())
                            ResearchAndDevelopment.Instance.SetScience(poolBefore, TransactionReasons.None);
                    }
                    LedgerOrchestrator.RecalculateAndPatch();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, "lab recovery ledger live check teardown threw: " + ex);
                }
                ParsekLog.Info(Tag, "lab recovery ledger live check teardown: rowsRemoved="
                    + rowsRemoved.ToString(IC) + " eventsRemoved=" + eventsRemoved.ToString(IC));
            }
        }

        [InGameTest(Category = Category, Scene = GameScenes.SPACECENTER,
            Description = "The lab credit capture pair is attached to stock's private transmission handler and its recovery call")]
        public void LabCapturePairIsAttachedToBothStockPayouts()
        {
            MethodInfo transmit = AccessTools.Method(typeof(ModuleScienceLab), "OnTransmissionComplete");
            MethodInfo recover = AccessTools.Method(typeof(ModuleScienceLab), nameof(ModuleScienceLab.RecoverScienceLabs));
            InGameAssert.IsNotNull(transmit, "ModuleScienceLab.OnTransmissionComplete is gone");
            InGameAssert.IsNotNull(recover, "ModuleScienceLab.RecoverScienceLabs is gone");

            int transmitPairs = CountParsekPairs(transmit, "ScienceLabTransmissionCreditPatch");
            int recoverPairs = CountParsekPairs(recover, "ScienceLabRecoveryCreditPatch");
            InGameAssert.AreEqual(1, transmitPairs,
                "OnTransmissionComplete must carry exactly one Parsek prefix + postfix pair");
            InGameAssert.AreEqual(1, recoverPairs,
                "RecoverScienceLabs must carry exactly one Parsek prefix + postfix pair");
            ParsekLog.Info(Tag, "lab capture patch live check passed: transmitPairs="
                + transmitPairs.ToString(IC) + " recoverPairs=" + recoverPairs.ToString(IC));
        }

        // ------------------------------------------------------------------
        // Jump cell
        // ------------------------------------------------------------------

        private sealed class ProbeVessel
        {
            internal string PartName;
            internal string ConverterModule;
            internal ProtoVessel Proto;
        }

        private static void RunUnloadedJumpCell(string jumpKind)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                return;
            }
            if (TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress)
            {
                InGameAssert.Skip("A time jump is already in progress");
                return;
            }
            CelestialBody kerbin = FlightGlobals.GetHomeBody();
            if (kerbin == null)
            {
                InGameAssert.Skip("No home body in this scene");
                return;
            }
            foreach (string partName in new[] { DrillPart, IsruPart, LabPart })
            {
                AvailablePart info = PartLoader.getPartInfoByName(partName);
                if (info == null || info.partPrefab == null)
                {
                    InGameAssert.Skip("No stock part named " + partName + " on this install");
                    return;
                }
            }

            double t0 = Planetarium.GetUniversalTime();
            double ranAt = t0 - OwedSeconds;
            var probes = new List<ProbeVessel>();
            try
            {
                probes.Add(AddProbe(kerbin, t0, 0, DrillPart, "ModuleResourceHarvester", ranAt, neverRanExtra: true));
                probes.Add(AddProbe(kerbin, t0, 1, IsruPart, "ModuleResourceConverter", ranAt, neverRanExtra: false));
                probes.Add(AddProbe(kerbin, t0, 2, LabPart, "ModuleScienceConverter", ranAt, neverRanExtra: false));
                foreach (ProbeVessel p in probes)
                {
                    InGameAssert.IsNotNull(p.Proto, "could not add the " + p.PartName + " vessel");
                    InGameAssert.IsTrue(p.Proto.vesselRef != null && !p.Proto.vesselRef.loaded,
                        p.PartName + ": the probe vessel must be registered and unloaded");
                    InGameAssert.IsTrue(ConverterJumpPolicy.IsConverterModuleName(p.ConverterModule),
                        p.ConverterModule + " does not resolve to a BaseConverter on this install");
                }

                double target = t0 + JumpSeconds;
                if (jumpKind == "forward")
                    TimeJumpManager.ExecuteForwardJump(target);
                else
                    TimeJumpManager.ExecuteJump(target, null, null);
                double t1 = Planetarium.GetUniversalTime();
                InGameAssert.IsTrue(Math.Abs(t1 - target) < 1.0,
                    "the " + jumpKind + " jump did not move the clock to " + target.ToString("R", IC)
                    + " (now " + t1.ToString("R", IC) + ")");

                int shifted = 0;
                foreach (ProbeVessel p in probes)
                {
                    ProtoPartModuleSnapshot converter = FindModule(p.Proto, p.ConverterModule, 0);
                    InGameAssert.IsNotNull(converter, p.PartName + ": the converter module snapshot is gone");
                    double now = ReadTimestamp(converter);
                    InGameAssert.IsTrue(Math.Abs((t1 - now) - OwedSeconds) < 1.0,
                        p.PartName + " " + p.ConverterModule + ": owes " + (t1 - now).ToString("R", IC)
                        + " s after the " + jumpKind + " jump, expected " + OwedSeconds.ToString("R", IC));
                    shifted++;

                    if (p.PartName == DrillPart)
                    {
                        ProtoPartModuleSnapshot neverRan = FindModule(p.Proto, p.ConverterModule, 1);
                        InGameAssert.IsNotNull(neverRan, "the never-ran converter snapshot is gone");
                        InGameAssert.AreEqual(0.0, ReadTimestamp(neverRan),
                            "a never-ran converter (clock 0) must be left at 0");
                    }
                    if (p.PartName == LabPart)
                    {
                        ProtoPartModuleSnapshot lab = FindModule(p.Proto, "ModuleScienceLab", 0);
                        InGameAssert.IsNotNull(lab, "the lab module snapshot is gone");
                        InGameAssert.IsTrue(Math.Abs(ReadTimestamp(lab) - ranAt) < 1e-6,
                            "ModuleScienceLab is not a converter; its lastUpdateTime must be untouched");
                    }
                }

                ParsekLog.Info(Tag, "converter jump live check passed: kind=" + jumpKind
                    + " shifted=" + shifted.ToString(IC) + " owed=" + OwedSeconds.ToString("F0", IC)
                    + " jump=" + JumpSeconds.ToString("F0", IC) + " neverRanKept=1 nonConverterKept=1");
            }
            finally
            {
                int removed = 0;
                foreach (ProbeVessel p in probes)
                    if (RemoveProbe(p)) removed++;
                ParsekLog.Info(Tag, "converter jump live check teardown: kind=" + jumpKind
                    + " vesselsRemoved=" + removed.ToString(IC));
            }
        }

        private static ProbeVessel AddProbe(CelestialBody body, double ut, int slot, string partName,
            string converterModule, double ranAt, bool neverRanExtra)
        {
            var probe = new ProbeVessel { PartName = partName, ConverterModule = converterModule };
            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode partNode = ProtoVessel.CreatePartNode(partName, flightId);
            // CreatePartNode writes no MODULE nodes; the unloaded walk reads exactly these.
            AddModule(partNode, converterModule, ranAt);
            if (neverRanExtra)
                AddModule(partNode, converterModule, 0.0);
            if (partName == LabPart)
                AddModule(partNode, "ModuleScienceLab", ranAt);

            // High circular orbits, spread apart, far outside any physics range.
            double sma = body.Radius + 400000.0 + slot * 20000.0;
            var orbit = new Orbit(0.0, 0.0, sma, 0.0, 0.0, slot * 0.5, ut, body);
            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                "ConverterJumpLab " + partName, VesselType.Probe, orbit, 0, new[] { partNode });
            probe.Proto = HighLogic.CurrentGame.AddVessel(vesselNode);
            ParsekLog.Verbose(Tag, "added probe vessel part=" + partName + " module=" + converterModule
                + " pid=" + (probe.Proto != null ? probe.Proto.persistentId.ToString(IC) : "0")
                + " ranAt=" + ranAt.ToString("F1", IC));
            return probe;
        }

        private static void AddModule(ConfigNode partNode, string moduleName, double lastUpdateTime)
        {
            ConfigNode module = partNode.AddNode("MODULE");
            module.AddValue("name", moduleName);
            module.AddValue("isEnabled", "True");
            module.AddValue(ConverterJumpPolicy.TimestampKey, lastUpdateTime.ToString("R", IC));
        }

        private static ProtoPartModuleSnapshot FindModule(ProtoVessel pv, string moduleName, int occurrence)
        {
            if (pv == null || pv.protoPartSnapshots == null) return null;
            int seen = 0;
            foreach (ProtoPartSnapshot pps in pv.protoPartSnapshots)
            {
                if (pps == null || pps.modules == null) continue;
                foreach (ProtoPartModuleSnapshot m in pps.modules)
                {
                    if (m == null || m.moduleName != moduleName) continue;
                    if (seen++ == occurrence) return m;
                }
            }
            return null;
        }

        private static double ReadTimestamp(ProtoPartModuleSnapshot m)
        {
            double v;
            string raw = m.moduleValues != null ? m.moduleValues.GetValue(ConverterJumpPolicy.TimestampKey) : null;
            return raw != null && double.TryParse(raw, NumberStyles.Float, IC, out v) ? v : double.NaN;
        }

        private static bool RemoveProbe(ProbeVessel p)
        {
            if (p == null || p.Proto == null) return false;
            try
            {
                Vessel v = p.Proto.vesselRef;
                HighLogic.CurrentGame.flightState.protoVessels.Remove(p.Proto);
                if (v != null)
                    v.Die();
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "could not remove probe vessel part=" + p.PartName + ": " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Lab cell
        // ------------------------------------------------------------------

        private static ProtoVessel BuildLabSnapshot(float storedScience)
        {
            CelestialBody kerbin = FlightGlobals.GetHomeBody();
            if (kerbin == null || PartLoader.getPartInfoByName(LabPart) == null)
                return null;
            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode partNode = ProtoVessel.CreatePartNode(LabPart, flightId);
            ConfigNode lab = partNode.AddNode("MODULE");
            lab.AddValue("name", "ModuleScienceLab");
            lab.AddValue("isEnabled", "True");
            lab.AddValue("storedScience", storedScience.ToString("R", IC));
            var orbit = new Orbit(0.0, 0.0, kerbin.Radius + 400000.0, 0.0, 0.0, 0.0,
                Planetarium.GetUniversalTime(), kerbin);
            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                "ConverterJumpLab recovered lab", VesselType.Station, orbit, 0, new[] { partNode });
            // Not added to the game: RecoverScienceLabs only reads the part snapshots.
            return new ProtoVessel(vesselNode, HighLogic.CurrentGame);
        }

        private static bool IsLabRow(GameAction a, double sinceUT)
        {
            return a != null && a.Type == GameActionType.ScienceLabEarning && a.UT >= sinceUT - 0.001;
        }

        private static bool IsLabEvent(GameStateEvent e, double sinceUT)
        {
            return e.eventType == GameStateEventType.ScienceLabCredited && e.ut >= sinceUT - 0.001;
        }

        private static int CountLabRows(double sinceUT)
        {
            int n = 0;
            foreach (GameAction a in Ledger.Actions)
                if (IsLabRow(a, sinceUT)) n++;
            return n;
        }

        private static GameAction FindLabRow(double sinceUT)
        {
            foreach (GameAction a in Ledger.Actions)
                if (IsLabRow(a, sinceUT)) return a;
            return null;
        }

        private static int CountLabEvents(double sinceUT)
        {
            int n = 0;
            foreach (GameStateEvent e in GameStateStore.Events)
                if (IsLabEvent(e, sinceUT)) n++;
            return n;
        }

        private static int RemoveLabRows(double sinceUT)
        {
            int removed = 0;
            for (int i = Ledger.Actions.Count - 1; i >= 0; i--)
            {
                if (IsLabRow(Ledger.Actions[i], sinceUT))
                {
                    Ledger.RemoveActionAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static int RemoveLabEvents(double sinceUT)
        {
            var targets = new List<GameStateEvent>();
            foreach (GameStateEvent e in GameStateStore.Events)
                if (IsLabEvent(e, sinceUT)) targets.Add(e);
            int removed = 0;
            foreach (GameStateEvent e in targets)
                if (GameStateStore.RemoveEvent(e)) removed++;
            return removed;
        }

        private static int CountParsekPairs(MethodInfo target, string patchClass)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return 0;
            int prefixes = 0, postfixes = 0;
            foreach (HarmonyLib.Patch p in info.Prefixes)
                if (p.owner == "com.parsek.mod" && p.PatchMethod.DeclaringType != null
                    && p.PatchMethod.DeclaringType.Name == patchClass) prefixes++;
            foreach (HarmonyLib.Patch p in info.Postfixes)
                if (p.owner == "com.parsek.mod" && p.PatchMethod.DeclaringType != null
                    && p.PatchMethod.DeclaringType.Name == patchClass) postfixes++;
            return Math.Min(prefixes, postfixes);
        }
    }
}
