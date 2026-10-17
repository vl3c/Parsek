using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only <c>ReadVesselResources</c> verb; the contract
    /// is on <see cref="TestCommandReadVesselResources"/>. SINGLE-PHASE and read-only: it walks
    /// the named vessels' parts (live <c>PartResource</c>s when loaded, proto part snapshots
    /// when not) and changes nothing.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void ReadVesselResourcesImpl(ParsedCommand cmd)
        {
            string pidArg = ArgOrNull(cmd, TestCommandReadVesselResources.PidKey);
            string recArg = ArgOrNull(cmd, TestCommandReadVesselResources.RecKey);
            string reason = TestCommandReadVesselResources.ParseTargets(pidArg, recArg,
                id => RecordingPaths.ValidateRecordingId(id), out List<uint> pids, out List<string> recs);
            if (reason != null)
            {
                RejectReadVesselResources(reason, $"pid={pidArg ?? string.Empty} rec={recArg ?? string.Empty}");
                return;
            }
            string resourcesArg = ArgOrNull(cmd, TestCommandReadVesselResources.ResourcesKey);
            reason = TestCommandReadVesselResources.ParseResources(resourcesArg, out List<string> requested);
            if (reason != null)
            {
                RejectReadVesselResources(reason, $"resources={resourcesArg}");
                return;
            }
            string expectArg = ArgOrNull(cmd, TestCommandReadVesselResources.ExpectKey);
            reason = TestCommandReadVesselResources.ParseExpect(expectArg,
                out List<KeyValuePair<string, double>> expect);
            if (reason != null)
            {
                RejectReadVesselResources(reason, $"expect={expectArg}");
                return;
            }
            string toleranceArg = ArgOrNull(cmd, TestCommandReadVesselResources.ToleranceKey);
            reason = TestCommandReadVesselResources.ParseTolerance(toleranceArg, out double tolerance);
            if (reason != null)
            {
                RejectReadVesselResources(reason, $"tolerance={toleranceArg}");
                return;
            }
            string snapshotArg = ArgOrNull(cmd, TestCommandReadVesselResources.ExpectSnapshotKey);
            reason = TestCommandReadVesselResources.ParseExpectSnapshot(snapshotArg, recs != null,
                out bool expectSnapshot);
            if (reason != null)
            {
                RejectReadVesselResources(reason, $"expectSnapshot={snapshotArg}");
                return;
            }

            // Resolve every target before reading anything, so a refusal reads nothing.
            var readings = new List<VesselResourceReading>();
            var vessels = new List<Vessel>();
            if (pids != null)
            {
                foreach (uint pid in pids)
                {
                    Vessel v = FindLiveVesselByPid(pid);
                    if (v == null)
                    {
                        RejectReadVesselResources(TestCommandReadVesselResources.VesselNotFoundReason, $"pid={pid}");
                        return;
                    }
                    if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    {
                        RejectReadVesselResources(TestCommandReadVesselResources.TargetIsGhostReason, $"pid={pid}");
                        return;
                    }
                    readings.Add(new VesselResourceReading { Found = true });
                    vessels.Add(v);
                }
            }
            else
            {
                foreach (string recId in recs)
                {
                    Recording rec = RecordingStore.TryFindCommittedRecordingById(recId);
                    if (rec == null)
                    {
                        RejectReadVesselResources(TestCommandReadVesselResources.UnknownRecordingReason, $"rec={recId}");
                        return;
                    }
                    uint spawnedPid = rec.SpawnedVesselPersistentId;
                    Vessel v = spawnedPid != 0 ? FindLiveVesselByPid(spawnedPid) : null;
                    if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId))
                        v = null;
                    var reading = new VesselResourceReading
                    {
                        RecordingId = recId,
                        Found = v != null,
                        Name = rec.VesselName,
                        Snapshot = expectSnapshot
                            ? TestCommandReadVesselResources.SnapshotTotals(rec.VesselSnapshot)
                            : null,
                    };
                    if (v == null)
                        ParsekLog.Warn(Tag, $"readvesselresources rec={recId} spawnedPid={spawnedPid} "
                            + $"vesselSpawned={(rec.VesselSpawned ? "true" : "false")}: no live vessel");
                    readings.Add(reading);
                    vessels.Add(v);
                }
            }

            var named = new HashSet<Vessel>();
            for (int i = 0; i < readings.Count; i++)
            {
                if (vessels[i] == null) continue;
                ReadVesselTanks(vessels[i], readings[i]);
                named.Add(vessels[i]);
            }

            // Every OTHER live non-ghost vessel's part uids, for the duplicate check. A ghost
            // map vessel is built from a recorded snapshot and carries its uids by design.
            var otherUids = new HashSet<uint>();
            List<Vessel> all = FlightGlobals.Vessels;
            if (all != null)
            {
                foreach (Vessel v in all)
                {
                    if (v == null || named.Contains(v) || GhostMapPresence.IsGhostMapVessel(v.persistentId))
                        continue;
                    var scratch = new VesselResourceReading();
                    ReadVesselTanks(v, scratch);
                    foreach (uint uid in scratch.PartUids) otherUids.Add(uid);
                }
            }

            Dictionary<string, ResourceReading> sum = TestCommandReadVesselResources.SumReadings(readings);
            List<string> reported = TestCommandReadVesselResources.ReportedResources(requested, sum);
            int collisions = TestCommandReadVesselResources.CountUidCollisions(readings, otherUids);
            int missing = 0;
            for (int i = 0; i < readings.Count; i++)
            {
                if (!readings[i].Found) missing++;
                ParsekLog.Info(Tag, TestCommandReadVesselResources.FormatVesselLine(i, readings[i], requested));
            }
            ParsekLog.Info(Tag, TestCommandReadVesselResources.FormatTotalLine(readings.Count, reported, sum));

            List<ResourceComparison> expectRows =
                TestCommandReadVesselResources.CompareExpect(expect, sum, tolerance, missing);
            foreach (ResourceComparison c in expectRows)
                ParsekLog.Info(Tag, TestCommandReadVesselResources.FormatExpectLine(c, tolerance));

            bool snapshotMatch = true;
            string snapshotMismatches = string.Empty;
            if (expectSnapshot)
            {
                foreach (VesselResourceReading r in readings)
                {
                    if (!r.Found || r.Snapshot == null) continue;
                    foreach (ResourceComparison c in
                             TestCommandReadVesselResources.CompareSnapshot(requested, r, tolerance))
                        ParsekLog.Info(Tag, TestCommandReadVesselResources.FormatSnapshotLine(r.RecordingId, r.Pid, c));
                }
                snapshotMatch = TestCommandReadVesselResources.FoldSnapshot(readings, requested, tolerance,
                    out snapshotMismatches);
            }

            string digest = TestCommandReadVesselResources.UidDigest(readings);
            string matchToken = TestCommandReadVesselResources.MatchToken(expect != null,
                TestCommandReadVesselResources.AllMatch(expectRows));
            string snapshotToken = TestCommandReadVesselResources.MatchToken(expectSnapshot, snapshotMatch);
            string complete = TestCommandReadVesselResources.FormatCompleteLine(
                readings.Count, missing, collisions, digest, matchToken, snapshotToken);
            bool clean = missing == 0 && collisions == 0 && matchToken != "false" && snapshotToken != "false";
            if (clean) ParsekLog.Info(Tag, complete);
            else ParsekLog.Warn(Tag, complete);
            SetExecResult("OK", TestCommandReadVesselResources.BuildOkPayload(
                readings, reported, sum, collisions, expect != null, expectRows,
                expectSnapshot, snapshotMatch, snapshotMismatches), null);
        }

        /// <summary>Reads one vessel's part uids and per-resource totals into
        /// <paramref name="into"/>: the live parts when loaded, else the proto part snapshots
        /// (what KSP loads when the vessel comes into range).</summary>
        private static void ReadVesselTanks(Vessel v, VesselResourceReading into)
        {
            into.Pid = v.persistentId;
            into.Name = v.vesselName;
            into.Loaded = v.loaded && v.parts != null && v.parts.Count > 0;
            if (into.Loaded)
            {
                foreach (Part p in v.parts)
                {
                    if (p == null) continue;
                    into.Parts++;
                    into.PartUids.Add(p.flightID);
                    if (p.Resources == null) continue;
                    for (int i = 0; i < p.Resources.Count; i++)
                    {
                        PartResource r = p.Resources[i];
                        if (r == null) continue;
                        TestCommandReadVesselResources.Accumulate(into.Resources, r.resourceName, r.amount, r.maxAmount);
                    }
                }
                return;
            }
            ProtoVessel pv = v.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return;
            foreach (ProtoPartSnapshot pp in pv.protoPartSnapshots)
            {
                if (pp == null) continue;
                into.Parts++;
                into.PartUids.Add(pp.flightID);
                if (pp.resources == null) continue;
                foreach (ProtoPartResourceSnapshot r in pp.resources)
                {
                    if (r == null) continue;
                    TestCommandReadVesselResources.Accumulate(into.Resources, r.resourceName, r.amount, r.maxAmount);
                }
            }
        }

        private void RejectReadVesselResources(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"readvesselresources rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
