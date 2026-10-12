using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only <c>PartAction</c> verb; the contract is on
    /// <see cref="TestCommandPartAction"/>. SINGLE-PHASE: the pressed part-menu event runs
    /// synchronously (an undock or a decouple creates the new vessel inside the call).
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private struct PartActionCandidate
        {
            internal Part Part;
            internal BaseEvent Event;
            internal string EventName;
            internal uint OtherPortId;
        }

        private void PartActionImpl(ParsedCommand cmd)
        {
            string actArg = ArgOrNull(cmd, TestCommandPartAction.ButtonKey);
            string reason = TestCommandPartAction.ParseButton(actArg, out PartActionKind kind);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg ?? string.Empty}");
                return;
            }
            string partArg = ArgOrNull(cmd, TestCommandPartAction.PartKey);
            reason = TestCommandPartAction.ParsePart(partArg, kind, out string partName);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} part={partArg ?? string.Empty}");
                return;
            }
            string pidArg = ArgOrNull(cmd, TestCommandPartAction.PidKey);
            reason = TestCommandPartAction.ParsePid(pidArg, out uint pid, out bool hasPid);
            if (reason != null)
            {
                RejectPartAction(reason, $"pid={pidArg ?? string.Empty}");
                return;
            }

            Vessel v = hasPid ? FindLiveVesselByPid(pid) : FlightGlobals.ActiveVessel;
            bool ghost = v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId);
            reason = TestCommandPartAction.DecideVessel(hasPid, v != null, ghost, v != null && v.loaded);
            if (reason != null)
            {
                RejectPartAction(reason, hasPid ? $"pid={pidArg}" : "target=active");
                return;
            }

            int namedParts = 0;
            var found = new List<PartActionCandidate>();
            string[] eventNames = TestCommandPartAction.EventNamesFor(kind);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                string runtimeName = p.partInfo != null ? p.partInfo.name : p.name;
                if (partName != null && !string.Equals(runtimeName, partName, StringComparison.Ordinal))
                    continue;
                namedParts++;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null || module.Events == null) continue;
                    for (int e = 0; e < eventNames.Length; e++)
                    {
                        BaseEvent ev = module.Events[eventNames[e]];
                        if (ev == null || !ev.active || !ev.guiActive) continue;
                        uint other = 0;
                        if (kind == PartActionKind.Undock && module is ModuleDockingNode node
                            && node.otherNode != null && node.otherNode.part != null)
                            other = node.otherNode.part.flightID;
                        found.Add(new PartActionCandidate
                        {
                            Part = p, Event = ev, EventName = eventNames[e], OtherPortId = other,
                        });
                        break;
                    }
                }
            }

            var candidates = found;
            if (kind == PartActionKind.Undock)
            {
                var ports = new List<KeyValuePair<uint, uint>>(found.Count);
                for (int i = 0; i < found.Count; i++)
                    ports.Add(new KeyValuePair<uint, uint>(found[i].Part.flightID, found[i].OtherPortId));
                candidates = new List<PartActionCandidate>();
                foreach (int k in TestCommandPartAction.FoldUndockPairs(ports)) candidates.Add(found[k]);
            }

            reason = TestCommandPartAction.DecideCandidates(partName, namedParts, candidates.Count);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} pid={v.persistentId} part={partName ?? "<any>"} " +
                    $"namedParts={namedParts} candidates={candidates.Count}");
                return;
            }

            PartActionCandidate c = candidates[0];
            string cPartName = c.Part.partInfo != null ? c.Part.partInfo.name : c.Part.name;
            uint cPartId = c.Part.flightID;
            uint vesselPid = v.persistentId;
            List<uint> before = ReadLivePids();
            try
            {
                // The part-menu button's own handler, as the PAW invokes it.
                c.Event.Invoke();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "partaction error reason=" + TestCommandPartAction.EventThrewReason
                    + $" button={actArg} pid={vesselPid} part='{cPartName}' event={c.EventName} " +
                    $"{ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandPartAction.EventThrewReason);
                return;
            }
            List<uint> after = ReadLivePids();
            List<uint> created = TestCommandPartAction.NewVesselPids(before, after);
            uint newPid = created.Count > 0 ? created[0] : 0u;
            Vessel nv = newPid != 0 ? FindLiveVesselByPid(newPid) : null;
            Vessel active = FlightGlobals.ActiveVessel;
            uint activePid = active != null ? active.persistentId : 0u;
            string activeName = active != null ? active.vesselName : null;

            ParsekLog.Info(Tag, TestCommandPartAction.FormatAppliedLine(kind, vesselPid, cPartName, cPartId,
                c.EventName, newPid, nv != null ? nv.vesselName : null, before.Count, after.Count,
                activePid, activeName));
            SetExecResult("OK", TestCommandPartAction.BuildOkPayload(kind, vesselPid, cPartName, cPartId,
                c.EventName, newPid, nv != null ? nv.vesselName : null, before.Count, after.Count,
                activePid, activeName), null);
        }

        private static List<uint> ReadLivePids()
        {
            var pids = new List<uint>();
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null) return pids;
            for (int i = 0; i < vessels.Count; i++)
                if (vessels[i] != null && !GhostMapPresence.IsGhostMapVessel(vessels[i].persistentId))
                    pids.Add(vessels[i].persistentId);
            return pids;
        }

        private void RejectPartAction(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"partaction rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
