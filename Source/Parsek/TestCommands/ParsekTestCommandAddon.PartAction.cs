using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens.Flight.Dialogs;

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
            internal ModuleScienceExperiment Experiment;
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
            string converterArg = ArgOrNull(cmd, TestCommandPartAction.ConverterKey);
            reason = TestCommandPartAction.ParseConverter(converterArg, kind, out string converterName);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} converter={converterArg ?? string.Empty}");
                return;
            }
            string allArg = ArgOrNull(cmd, TestCommandPartAction.AllKey);
            reason = TestCommandPartAction.ParseAll(allArg, kind, out bool all);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} all={allArg ?? string.Empty}");
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
            int matchingConverters = 0;
            int experimentModules = 0;
            int radiatorModules = 0;
            bool radiatorKind = TestCommandPartAction.IsRadiatorKind(kind);
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
                if (kind == PartActionKind.Inspect)
                {
                    found.Add(new PartActionCandidate { Part = p, EventName = string.Empty });
                    continue;
                }
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (kind == PartActionKind.LabProcess)
                    {
                        if (!(module is ModuleScienceExperiment exp)) continue;
                        experimentModules++;
                        if (exp.GetScienceCount() > 0)
                            found.Add(new PartActionCandidate
                            {
                                Part = p, Experiment = exp,
                                EventName = TestCommandPartAction.LabProcessMethodName,
                            });
                        continue;
                    }
                    if (radiatorKind)
                    {
                        // Only the radiator module's Activate / Shutdown: other modules on
                        // the part may carry an event of the same name.
                        if (!(module is ModuleActiveRadiator)) continue;
                        radiatorModules++;
                    }
                    if (converterName != null)
                    {
                        if (!(module is BaseConverter bc)
                            || !string.Equals(bc.ConverterName, converterName, StringComparison.Ordinal))
                            continue;
                        matchingConverters++;
                    }
                    if (module.Events == null) continue;
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

            reason = TestCommandPartAction.DecideCandidates(kind, partName, namedParts, converterName,
                matchingConverters, experimentModules, radiatorKind ? radiatorModules : -1,
                candidates.Count, all);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} pid={v.persistentId} part={partName ?? "<any>"} " +
                    $"converter={converterName ?? string.Empty} all={all} namedParts={namedParts} " +
                    $"matchingConverters={matchingConverters} experimentModules={experimentModules} " +
                    $"radiatorModules={radiatorModules} candidates={candidates.Count}");
                return;
            }

            PartActionCandidate first = candidates[0];
            string cPartName = first.Part.partInfo != null ? first.Part.partInfo.name : first.Part.name;
            uint cPartId = first.Part.flightID;
            uint vesselPid = v.persistentId;

            MethodInfo labMethod = null;
            if (kind == PartActionKind.LabProcess)
            {
                labMethod = typeof(ModuleScienceExperiment).GetMethod(
                    TestCommandPartAction.LabProcessMethodName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new[] { typeof(ScienceData) }, null);
                if (labMethod == null)
                {
                    RejectPartAction(TestCommandPartAction.LabMethodMissingReason,
                        $"button={actArg} pid={vesselPid} part='{cPartName}' " +
                        $"method={TestCommandPartAction.LabProcessMethodName}(ScienceData)");
                    return;
                }
            }

            List<uint> before = ReadLivePids();
            string eventName = first.EventName;
            int pressed = 0;
            for (int k = 0; k < candidates.Count; k++)
            {
                PartActionCandidate c = candidates[k];
                string pressEvent = c.EventName;
                try
                {
                    if (kind == PartActionKind.LabProcess)
                        pressEvent = PressLabProcess(c, labMethod);
                    else if (c.Event != null)
                        // The part-menu button's own handler, as the PAW invokes it.
                        c.Event.Invoke();
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null
                        ? ex.InnerException : ex;
                    string pName = c.Part != null && c.Part.partInfo != null ? c.Part.partInfo.name : cPartName;
                    uint pId = c.Part != null ? c.Part.flightID : 0u;
                    ParsekLog.Error(Tag, "partaction error reason=" + TestCommandPartAction.EventThrewReason
                        + $" button={actArg} pid={vesselPid} part='{pName}' partId={pId} event={pressEvent} " +
                        TestCommandPartAction.FormatPressProgress(pressed, candidates.Count) +
                        $" {inner.GetType().Name}: {inner.Message}");
                    SetExecResult("ERROR", null, TestCommandPartAction.EventThrewReason);
                    return;
                }
                if (k == 0) eventName = pressEvent;
                pressed++;
            }
            List<uint> after = ReadLivePids();
            List<uint> created = TestCommandPartAction.NewVesselPids(before, after);
            uint newPid = created.Count > 0 ? created[0] : 0u;
            Vessel nv = newPid != 0 ? FindLiveVesselByPid(newPid) : null;
            Vessel active = FlightGlobals.ActiveVessel;
            uint activePid = active != null ? active.persistentId : 0u;
            string activeName = active != null ? active.vesselName : null;

            string state;
            if (all)
            {
                // One entry per acted-on part; candidates are collected in vessel part order.
                var perPart = new List<KeyValuePair<uint, IList<PartActionModuleReading>>>();
                var seenParts = new HashSet<uint>();
                for (int k = 0; k < candidates.Count; k++)
                {
                    Part part = candidates[k].Part;
                    if (part == null || !seenParts.Add(part.flightID)) continue;
                    perPart.Add(new KeyValuePair<uint, IList<PartActionModuleReading>>(
                        part.flightID, ReadPartActionState(part)));
                }
                state = TestCommandPartAction.FormatAllState(perPart);
            }
            else
            {
                state = TestCommandPartAction.FormatState(ReadPartActionState(first.Part));
            }

            // A packed (on-rails) vessel is pressed like an unpacked one: still loaded, so its
            // modules and events exist, exactly as for a player's PAW click in rails warp.
            var warp = new PartActionWarpContext(v.packed, SafeCurrentWarpRateIndex(),
                SafeCurrentWarpRate(), SafeUniversalTime());
            ParsekLog.Info(Tag, TestCommandPartAction.FormatAppliedLine(kind, vesselPid, cPartName, cPartId,
                eventName, newPid, nv != null ? nv.vesselName : null, before.Count, after.Count,
                activePid, activeName, converterName, state, pressed, warp));
            SetExecResult("OK", TestCommandPartAction.BuildOkPayload(kind, vesselPid, cPartName, cPartId,
                eventName, newPid, nv != null ? nv.vesselName : null, before.Count, after.Count,
                activePid, activeName, converterName, state, pressed, warp), null);
        }

        /// <summary>
        /// The results dialog's "process in lab": when the open dialog's current page is this
        /// part's, its own <c>OnSendToLab</c> callback runs (the dialog button's body: the
        /// experiment's <c>sendDataToLab</c>, then the page dismissal), so no stale page stays on
        /// screen; otherwise the private method runs directly with the module's first datum.
        /// Returns the <c>event=</c> name of the path taken.
        /// </summary>
        private static string PressLabProcess(PartActionCandidate c, MethodInfo labMethod)
        {
            ScienceData[] data = c.Experiment.GetData();
            ScienceData datum = data != null && data.Length > 0 ? data[0] : null;
            ExperimentsResultDialog dialog = ExperimentsResultDialog.Instance;
            ExperimentResultDialogPage page = dialog != null ? dialog.currentPage : null;
            if (page != null && page.host == c.Part && page.OnSendToLab != null && page.pageData != null
                && datum != null && string.Equals(page.pageData.subjectID, datum.subjectID, StringComparison.Ordinal))
            {
                page.OnSendToLab(page.pageData);
                return TestCommandPartAction.LabProcessDialogEventName;
            }
            labMethod.Invoke(c.Experiment, new object[] { datum });
            return TestCommandPartAction.LabProcessMethodName;
        }

        /// <summary>The target part's relevant module state for <c>state=</c>, in module order.</summary>
        private static List<PartActionModuleReading> ReadPartActionState(Part part)
        {
            var readings = new List<PartActionModuleReading>();
            if (part == null || part.Modules == null) return readings;
            for (int m = 0; m < part.Modules.Count; m++)
            {
                PartModule module = part.Modules[m];
                if (module is BaseConverter bc)
                    readings.Add(PartActionModuleReading.Converter(bc.ConverterName, bc.IsActivated));
                else if (module is ModuleScienceLab lab)
                    readings.Add(PartActionModuleReading.Lab(lab.storedScience, lab.dataStored));
                else if (module is ModuleScienceExperiment exp)
                    readings.Add(PartActionModuleReading.Experiment(exp.GetScienceCount()));
                else if (module is ModuleAnimationGroup anim)
                    readings.Add(PartActionModuleReading.AnimationGroup(anim.isDeployed));
                else if (module is ModuleActiveRadiator rad)
                    readings.Add(PartActionModuleReading.Radiator(rad.IsCooling));
            }
            return readings;
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
