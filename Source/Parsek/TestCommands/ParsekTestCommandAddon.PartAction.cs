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

            string expectSnapshotArg = ArgOrNull(cmd, TestCommandPartAction.ExpectSnapshotKey);
            string recArg = ArgOrNull(cmd, TestCommandPartAction.RecKey);
            string toleranceArg = ArgOrNull(cmd, TestCommandPartAction.ToleranceKey);
            reason = TestCommandPartAction.ParseSnapshotArgs(kind, expectSnapshotArg, recArg, toleranceArg,
                id => RecordingPaths.ValidateRecordingId(id),
                out bool expectSnapshot, out string snapshotRecId, out double snapshotTolerance);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} expectSnapshot={expectSnapshotArg ?? string.Empty} " +
                    $"rec={recArg ?? string.Empty} tolerance={toleranceArg ?? string.Empty}");
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

            // The recording whose end snapshot an inspect compares with, resolved before
            // anything is read (ReadVesselResources' resolution: committed recording by id,
            // its VesselSnapshot).
            List<TestCommandPartAction.LabSnapshotPart> snapshotParts = null;
            if (expectSnapshot)
            {
                Recording snapRec = RecordingStore.TryFindCommittedRecordingById(snapshotRecId);
                if (snapRec == null)
                {
                    RejectPartAction(TestCommandPartAction.UnknownRecordingReason,
                        $"button={actArg} rec={snapshotRecId}");
                    return;
                }
                snapshotParts = TestCommandPartAction.ParseSnapshotParts(snapRec.VesselSnapshot);
            }

            int namedParts = 0;
            int matchingConverters = 0;
            int experimentModules = 0;
            int radiatorModules = 0;
            int engineModules = 0;
            bool radiatorKind = TestCommandPartAction.IsRadiatorKind(kind);
            bool engineKind = TestCommandPartAction.IsEngineKind(kind);
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
                                EventName = TestCommandPartAction.LabProcessDialogEventName,
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
                    if (engineKind)
                    {
                        // Only the engine module's "Activate Engine" (ModuleEnginesFX derives
                        // from ModuleEngines): a radiator on the part has an Activate too.
                        if (!(module is ModuleEngines)) continue;
                        engineModules++;
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

            int typedModules = radiatorKind ? radiatorModules : engineKind ? engineModules : -1;
            reason = TestCommandPartAction.DecideCandidates(kind, partName, namedParts, converterName,
                matchingConverters, experimentModules, typedModules,
                candidates.Count, all);
            if (reason != null)
            {
                RejectPartAction(reason, $"button={actArg} pid={v.persistentId} part={partName ?? "<any>"} " +
                    $"converter={converterName ?? string.Empty} all={all} namedParts={namedParts} " +
                    $"matchingConverters={matchingConverters} experimentModules={experimentModules} " +
                    $"radiatorModules={radiatorModules} engineModules={engineModules} " +
                    $"candidates={candidates.Count}");
                return;
            }

            PartActionCandidate first = candidates[0];
            string cPartName = first.Part.partInfo != null ? first.Part.partInfo.name : first.Part.name;
            uint cPartId = first.Part.flightID;
            uint vesselPid = v.persistentId;

            if (kind == PartActionKind.LabProcess)
            {
                Vessel activeNow = FlightGlobals.ActiveVessel;
                reason = TestCommandPartAction.DecideLabTarget(activeNow != null && activeNow == v);
                if (reason != null)
                {
                    RejectPartAction(reason, $"button={actArg} pid={vesselPid} part='{cPartName}' " +
                        $"activePid={(activeNow != null ? activeNow.persistentId : 0u)}");
                    return;
                }
            }

            List<uint> before = ReadLivePids();
            string eventName = first.EventName;
            int pressed = 0;
            var labRoutes = new List<TestCommandPartAction.LabProcessRoute>();
            var labValues = new List<float>();
            for (int k = 0; k < candidates.Count; k++)
            {
                PartActionCandidate c = candidates[k];
                string pressEvent = c.EventName;
                try
                {
                    if (kind == PartActionKind.LabProcess)
                    {
                        string labReason = PressLabProcess(c, v, pressed, candidates.Count,
                            out TestCommandPartAction.LabProcessRoute labRoute, out float labValue,
                            out string labDetail);
                        if (labReason != null)
                        {
                            string verdict = TestCommandPartAction.DecideLabRefusalVerdict(labReason, pressed,
                                out string answerReason);
                            if (verdict == "REJECTED")
                            {
                                RejectPartAction(labReason, labDetail);
                                return;
                            }
                            // An all=true walk stopped after earlier presses already sent.
                            ParsekLog.Error(Tag, "partaction error reason=" + answerReason
                                + " cause=" + labReason + " " + labDetail);
                            SetExecResult("ERROR", null, answerReason);
                            return;
                        }
                        labRoutes.Add(labRoute);
                        labValues.Add(labValue);
                    }
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
            string appliedLine = TestCommandPartAction.FormatAppliedLine(kind, vesselPid, cPartName, cPartId,
                eventName, newPid, nv != null ? nv.vesselName : null, before.Count, after.Count,
                activePid, activeName, converterName, state, pressed, warp);
            List<KeyValuePair<string, string>> payload = TestCommandPartAction.BuildOkPayload(kind, vesselPid,
                cPartName, cPartId, eventName, newPid, nv != null ? nv.vesselName : null, before.Count,
                after.Count, activePid, activeName, converterName, state, pressed, warp);
            if (kind == PartActionKind.LabProcess)
            {
                appliedLine += TestCommandPartAction.FormatLabTail(labRoutes, labValues);
                payload.AddRange(TestCommandPartAction.LabPayloadFields(labRoutes, labValues));
            }
            else if (kind == PartActionKind.Inspect)
            {
                string snapshotToken = TestCommandPartAction.SnapshotNotAskedToken;
                var snapshotMismatches = new List<string>();
                if (expectSnapshot)
                    snapshotToken = CompareInspectedLabsWithSnapshot(v, candidates, all, snapshotRecId,
                        snapshotParts, snapshotTolerance, snapshotMismatches);
                appliedLine += TestCommandPartAction.FormatSnapshotTail(snapshotToken);
                payload.AddRange(TestCommandPartAction.SnapshotPayloadFields(snapshotToken, snapshotMismatches));
            }
            ParsekLog.Info(Tag, appliedLine);
            SetExecResult("OK", payload, null);
        }

        /// <summary>
        /// The inspect <c>expectSnapshot=true</c> comparison: every inspected part (the first
        /// alone without <c>all=true</c>) against the recording's end snapshot, one
        /// <c>partaction snapshot</c> line per mapping and field, then one summary line.
        /// Returns the folded <c>snapshotMatch</c> token.
        /// </summary>
        private string CompareInspectedLabsWithSnapshot(Vessel v, List<PartActionCandidate> candidates,
            bool all, string recId, List<TestCommandPartAction.LabSnapshotPart> snapshotParts,
            double tolerance, List<string> mismatches)
        {
            var tokens = new List<string>();
            int compared = 0;
            int count = all ? candidates.Count : Math.Min(1, candidates.Count);
            for (int k = 0; k < count; k++)
            {
                Part p = candidates[k].Part;
                if (p == null) continue;
                ModuleScienceLab lab = p.FindModuleImplementing<ModuleScienceLab>();
                var live = new TestCommandPartAction.LiveLabReading
                {
                    PartId = p.flightID,
                    PersistentId = p.persistentId,
                    Name = p.partInfo != null ? p.partInfo.name : p.name,
                    Index = v.parts.IndexOf(p),
                    PartCount = v.parts.Count,
                    HasLab = lab != null,
                    DataStored = lab != null ? lab.dataStored : 0f,
                    StoredScience = lab != null ? lab.storedScience : 0f,
                    Experiments = lab != null ? lab.ExperimentData : null,
                };
                TestCommandPartAction.LabSnapshotOutcome o =
                    TestCommandPartAction.EvaluateLabSnapshot(recId, live, snapshotParts, tolerance);
                foreach (string line in o.Lines) ParsekLog.Info(Tag, line);
                mismatches.AddRange(o.Mismatches);
                tokens.Add(o.Token);
                if (o.Token != TestCommandPartAction.SnapshotNotAskedToken) compared++;
            }
            string token = TestCommandPartAction.FoldSnapshotTokens(tokens);
            string summary = $"partaction snapshot complete rec={recId} parts={count} compared={compared} " +
                $"snapshotParts={(snapshotParts != null ? snapshotParts.Count : 0)} " +
                $"tolerance={tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"snapshotMatch={token} mismatches='{string.Join(",", mismatches.ToArray())}'";
            if (token == "false") ParsekLog.Warn(Tag, summary);
            else ParsekLog.Info(Tag, summary);
            return token;
        }

        /// <summary>
        /// The results dialog's "process in lab" for one experiment module (contract on
        /// <see cref="TestCommandPartAction"/>): the dialog's current page for this part and
        /// this datum, else the page the experiment's own <c>ReviewDataEvent</c> opens; then
        /// the page's <c>UpdatePageLabValue()</c> (stock writes <c>labValue</c> only in
        /// <c>ExperimentsResultDialog.SetPage</c>, so a datum loaded from a save, or a page shown
        /// with no lab aboard, holds 0), the value and lab-search gates, and only then the
        /// page's <c>OnSendToLab</c>. Returns null after the send, or the refusal reason with
        /// its log detail; a refusal closes whatever the press opened.
        /// </summary>
        private static string PressLabProcess(PartActionCandidate c, Vessel v, int succeeded, int candidateCount,
            out TestCommandPartAction.LabProcessRoute route, out float labValue, out string detail)
        {
            labValue = 0f;
            detail = string.Empty;
            string partName = c.Part.partInfo != null ? c.Part.partInfo.name : c.Part.name;
            ScienceData[] data = c.Experiment.GetData();
            ScienceData datum = data != null && data.Length > 0 ? data[0] : null;
            string subject = datum != null ? datum.subjectID : null;

            ExperimentsResultDialog dialog = ExperimentsResultDialog.Instance;
            ExperimentResultDialogPage page = dialog != null ? dialog.currentPage : null;
            bool dialogBefore = dialog != null;
            int pagesBefore = dialog != null && dialog.pages != null ? dialog.pages.Count : 0;
            route = TestCommandPartAction.DecideLabRoute(LabPageIsFor(page, c.Part, datum));
            if (route == TestCommandPartAction.LabProcessRoute.Review)
            {
                // The experiment's "Review Data" PAW button: ExperimentsResultDialog.DisplayResult
                // opens (or re-points) the dialog synchronously and SetPage makes the page current.
                c.Experiment.ReviewDataEvent();
                dialog = ExperimentsResultDialog.Instance;
                page = dialog != null ? dialog.currentPage : null;
            }
            int pagesAfter = dialog != null && dialog.pages != null ? dialog.pages.Count : 0;
            TestCommandPartAction.LabDialogCleanup cleanup =
                TestCommandPartAction.DecideLabCleanup(route, dialogBefore, pagesBefore, pagesAfter);

            string reason = null;
            string labError = null;
            if (route == TestCommandPartAction.LabProcessRoute.Review)
                reason = TestCommandPartAction.DecideLabPageAfterReview(LabPageIsFor(page, c.Part, datum));
            if (reason == null)
            {
                labValue = page.UpdatePageLabValue();
                reason = TestCommandPartAction.DecideLabValue(labValue);
            }
            if (reason == null)
            {
                // Stock's own search, the one sendDataToLab runs: it reads labValue for the
                // space check, so it runs after the value is written.
                var search = new ScienceLabSearch(v, page.pageData);
                if (!search.NextLabForDataFound)
                    labError = search.Error + ":labs=" + search.LabsTotal + ",manned=" + search.LabsManned
                        + ",space=" + search.LabsWithSpace + ",notResearched=" + search.LabsNotResearched
                        + ",idle=" + search.LabsNotProcessing;
                reason = TestCommandPartAction.DecideLabSearch(search.NextLabForDataFound);
            }
            if (reason != null)
            {
                ApplyLabCleanup(cleanup, dialog, page);
                detail = TestCommandPartAction.FormatLabRefusalDetail(v.persistentId, partName, c.Part.flightID,
                    subject, route, cleanup, labValue, labError, succeeded, candidateCount);
                return reason;
            }

            // The dialog's lab button body: sendDataToLab (process into the lab the search
            // found, end the experiment), then dismissCurrentPage, which closes the dialog with
            // its last page.
            page.OnSendToLab(page.pageData);
            ExperimentsResultDialog after = ExperimentsResultDialog.Instance;
            ParsekLog.Verbose(Tag, $"partaction labprocess sent: part='{partName}' partId={c.Part.flightID} " +
                $"subject='{subject}' labRoute={TestCommandPartAction.LabRouteToken(route)} " +
                $"labValue={labValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"pagesLeft={(after != null && after.pages != null ? after.pages.Count : 0)}");
            return null;
        }

        /// <summary>Whether <paramref name="page"/> is <paramref name="part"/>'s results page for
        /// the module's own <paramref name="datum"/> object (see
        /// <see cref="TestCommandPartAction.LabPageMatches"/>).</summary>
        private static bool LabPageIsFor(ExperimentResultDialogPage page, Part part, ScienceData datum)
            => TestCommandPartAction.LabPageMatches(page != null, page != null && page.host == part,
                page != null && page.OnSendToLab != null,
                page != null && datum != null && ReferenceEquals(page.pageData, datum));

        /// <summary>Closes what a refused labprocess opened (<see cref="TestCommandPartAction.DecideLabCleanup"/>).</summary>
        private static void ApplyLabCleanup(TestCommandPartAction.LabDialogCleanup cleanup,
            ExperimentsResultDialog dialog, ExperimentResultDialogPage page)
        {
            if (cleanup == TestCommandPartAction.LabDialogCleanup.DismissDialog && dialog != null)
                dialog.Dismiss();
            else if (cleanup == TestCommandPartAction.LabDialogCleanup.KeepPage && page != null
                     && page.OnKeepData != null)
                page.OnKeepData(page.pageData);
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
                else if (module is ModuleEngines eng)
                    readings.Add(PartActionModuleReading.Engine(eng.EngineIgnited));
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
