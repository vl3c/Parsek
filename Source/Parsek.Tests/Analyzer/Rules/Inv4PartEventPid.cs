using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV4 part-event PID resolution (design doc "The invariant rules" INV4,
    // edge case 23).
    //
    // Every PartEvent's partPersistentId must resolve against the paired snapshot's
    // part-PID set. The synthetic ghost builder assigns persistentId =
    // 100000 + idx*1111 (VesselSnapshotBuilder.AddPart); this rule checks
    // membership, not the formula, so it stays correct for real captured
    // snapshots too. Part GameObjects are named by persistentId at playback
    // (.claude/CLAUDE.md ghost-event <-> snapshot PID), so an event PID missing
    // from the snapshot silently drops a part event at playback -> FAIL.
    //
    // The snapshot used is the ghost visual snapshot when present (the surface
    // playback actually looks parts up against), otherwise the vessel snapshot.
    // With NO snapshot at all: a destroyed / showcase recording -> INFO (cannot
    // resolve, not a bug). With a snapshot that FAILED to load (edge case 23):
    // INFO (unresolvable) + WARN (the snapshot load failed). Pure over the model;
    // never touches a file and never NREs on a null snapshot.
    internal sealed class Inv4PartEventPid : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV4-PARTEVENT-PID";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "VesselSnapshotBuilder.AddPart / ghost-event snapshot PID lookup";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (rec.PartEvents == null || rec.PartEvents.Count == 0)
                    continue;

                ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;

                if (snapshot == null)
                {
                    bool loadFailed =
                        rec.VesselSnapshotHydrationFailed
                        || SnapshotLoadFaulted(model, rec.RecordingId);

                    findings.Add(new Finding(
                        RuleIdConst,
                        VerdictLevel.Info,
                        rec.RecordingId,
                        -1,
                        Inv("INV4 no-snapshot recording={0} partEvents={1} loadFailed={2}",
                            rec.RecordingId, rec.PartEvents.Count, loadFailed ? "True" : "False"),
                        "VesselSnapshotBuilder.AddPart"));

                    if (loadFailed)
                    {
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Warn,
                            rec.RecordingId,
                            -1,
                            Inv("INV4 snapshot-load-failed recording={0}", rec.RecordingId),
                            "VesselSnapshotBuilder.AddPart"));
                    }
                    continue;
                }

                HashSet<uint> pidSet = ExtractPartPids(snapshot);

                var reported = new HashSet<uint>();
                foreach (PartEvent ev in rec.PartEvents)
                {
                    if (pidSet.Contains(ev.partPersistentId))
                        continue;
                    if (!reported.Add(ev.partPersistentId))
                        continue; // one FAIL per unresolvable PID keeps findings bounded
                    findings.Add(new Finding(
                        RuleIdConst,
                        VerdictLevel.Fail,
                        rec.RecordingId,
                        -1,
                        Inv("INV4 unresolved-pid recording={0} pid={1} event={2}",
                            rec.RecordingId, ev.partPersistentId, ev.eventType),
                        "VesselSnapshotBuilder.AddPart"));
                }
            }

            return findings;
        }

        private static bool SnapshotLoadFaulted(AnalyzerModel model, string recordingId)
        {
            if (model.LoadFaults == null)
                return false;
            foreach (LoadFault f in model.LoadFaults)
            {
                if (f.FileKind == "snapshot" && f.RecordingId == recordingId)
                    return true;
            }
            return false;
        }

        private static HashSet<uint> ExtractPartPids(ConfigNode snapshot)
        {
            var pids = new HashSet<uint>();
            if (snapshot == null)
                return pids;

            // KSP VESSEL snapshots carry a flat list of PART children, each with a
            // "persistentId" value. Read that value as the part PID set.
            foreach (ConfigNode part in snapshot.GetNodes("PART"))
            {
                string pidStr = part.GetValue("persistentId");
                if (!string.IsNullOrEmpty(pidStr)
                    && uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                {
                    pids.Add(pid);
                }
            }
            return pids;
        }

        private static string Inv(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}
