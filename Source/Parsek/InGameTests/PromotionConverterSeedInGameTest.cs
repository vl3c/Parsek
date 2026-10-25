using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live check of the chain-promotion converter seed (todo
    /// TAIL-TRIM-UNWITNESSED-RESOURCE-CHANGES, promotion residue). A recording started as a
    /// promotion into a fresh branch skips the full seed set; it must still record a
    /// <see cref="PartEventType.ConverterActivated"/> for every converter already running,
    /// or the tail-trim witness never sees the converter. The xUnit cells cover the gate and
    /// the emitter over hand-built tracking sets; this cell runs the real promotion branch of
    /// <see cref="FlightRecorder.ResetPartEventTrackingState"/> over the live active vessel,
    /// so the converter detection reads real stock <c>BaseConverter</c> modules. It uses a
    /// transient recorder and in-memory trees only; nothing reaches the store or the save.
    /// </summary>
    public class PromotionConverterSeedInGameTest
    {
        private const string Tag = "TestRunner";

        [InGameTest(Category = "PromotionConverterSeed", Scene = GameScenes.FLIGHT,
            Description = "A promotion into a fresh branch seeds ConverterActivated for each running converter; a recording that already has a converter edge gets none")]
        public void PromotionSeedsRunningConverters()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.parts == null)
            {
                InGameAssert.Skip("needs an active vessel");
                return;
            }

            var runningPids = new HashSet<uint>();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p != null && FlightRecorder.IsAnyConverterActiveOnPart(p))
                    runningPids.Add(p.persistentId);
            }
            if (runningPids.Count == 0)
            {
                InGameAssert.Skip("needs a running converter on the active vessel (the MINE-15 lane starts the rover's drills and ISRU first)");
                return;
            }

            // Fresh branch: an empty active recording, the shape CreateSplitBranch and the
            // switch-segment bind hand the recorder.
            var fresh = BuildRecorder("promo-seed-fresh", null);
            fresh.ResetPartEventTrackingState(v, emitSeedEvents: false);
            var seeded = new HashSet<uint>();
            int nonConverterSeeds = 0;
            for (int i = 0; i < fresh.PartEvents.Count; i++)
            {
                PartEvent e = fresh.PartEvents[i];
                if (e.eventType == PartEventType.ConverterActivated)
                    seeded.Add(e.partPersistentId);
                else if (e.eventType != PartEventType.EngineIgnited
                    && e.eventType != PartEventType.EngineShutdown)
                    nonConverterSeeds++;
            }
            InGameAssert.AreEqual(runningPids.Count, seeded.Count,
                "every running converter part should get one ConverterActivated seed on a fresh promotion");
            foreach (uint pid in runningPids)
                InGameAssert.IsTrue(seeded.Contains(pid),
                    "running converter part pid=" + pid + " has no ConverterActivated seed");
            InGameAssert.AreEqual(0, nonConverterSeeds,
                "a promotion must not emit the non-engine, non-converter seeds (bug A / #263)");

            // A recording that already carries a converter edge keeps its own state.
            var edged = BuildRecorder("promo-seed-edged", new PartEvent
            {
                ut = Planetarium.GetUniversalTime() - 1.0,
                partPersistentId = FirstOf(runningPids),
                eventType = PartEventType.ConverterDeactivated,
                partName = "edge"
            });
            edged.ResetPartEventTrackingState(v, emitSeedEvents: false);
            int edgedSeeds = 0;
            for (int i = 0; i < edged.PartEvents.Count; i++)
                if (edged.PartEvents[i].eventType == PartEventType.ConverterActivated)
                    edgedSeeds++;
            InGameAssert.AreEqual(0, edgedSeeds,
                "a recording that already has a converter edge must get no converter seeds");

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "PromotionSeedsRunningConverters: vessel='{0}' runningConverterParts={1} freshSeeds={2} edgedSeeds={3}",
                v.vesselName, runningPids.Count, seeded.Count, edgedSeeds));
        }

        private static FlightRecorder BuildRecorder(string recId, PartEvent? priorEvent)
        {
            var rec = new Recording { RecordingId = recId, VesselName = "promotion seed probe" };
            if (priorEvent.HasValue)
                rec.PartEvents.Add(priorEvent.Value);
            var tree = new RecordingTree { Id = recId + "-tree", TreeName = "promotion seed probe", ActiveRecordingId = recId };
            tree.Recordings[recId] = rec;
            return new FlightRecorder { ActiveTree = tree };
        }

        private static uint FirstOf(HashSet<uint> set)
        {
            foreach (uint pid in set)
                return pid;
            return 0;
        }
    }
}
