using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only two-phase <c>CrewTransfer</c> verb; the contract
    /// is on <see cref="TestCommandCrewTransfer"/>. It opens stock's own crew transfer for the
    /// kerbal and selects the destination through the transfer's own part-select method; the
    /// completion poll answers once stock dismissed the transfer with the kerbal moved.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The protected PartItemTransfer.OnPartSelect(Part) a click on a highlighted
        // destination part calls (CrewTransfer overrides it).
        private static readonly MethodInfo CrewTransferOnPartSelectMethod =
            typeof(CrewTransfer).GetMethod("OnPartSelect",
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Part) }, null);

        // The transfer in progress. Only one can exist: the two-phase head holds the FIFO.
        private CrewTransfer crewTransferHost;
        private string crewTransferKerbal;
        private Part crewTransferFrom;
        private Part crewTransferTo;
        private uint crewTransferPid;
        private bool crewTransferDismissedMoved;

        private void CrewTransferImpl(ParsedCommand cmd)
        {
            string kerbalArg = ArgOrNull(cmd, TestCommandCrewTransfer.KerbalKey);
            string partArg = ArgOrNull(cmd, TestCommandCrewTransfer.PartKey);
            string reason = TestCommandCrewTransfer.ParseArgs(kerbalArg, partArg, out string kerbal, out string partName);
            if (reason != null)
            {
                RejectCrewTransfer(reason, $"kerbal={kerbalArg ?? string.Empty} part={partArg ?? string.Empty}");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            bool hasVessel = v != null;
            bool ready = hasVessel && v.loaded && !v.packed && v.parts != null;
            Part source = null;
            ProtoCrewMember crew = null;
            if (ready)
            {
                for (int i = 0; i < v.parts.Count && source == null; i++)
                {
                    Part p = v.parts[i];
                    if (p?.protoModuleCrew == null) continue;
                    for (int c = 0; c < p.protoModuleCrew.Count; c++)
                    {
                        ProtoCrewMember pcm = p.protoModuleCrew[c];
                        if (pcm != null && string.Equals(pcm.name, kerbal, StringComparison.Ordinal))
                        {
                            source = p;
                            crew = pcm;
                            break;
                        }
                    }
                }
            }
            reason = TestCommandCrewTransfer.DecideGate(hasVessel, ready, source != null,
                PartItemTransfer.Instance != null);
            if (reason != null)
            {
                RejectCrewTransfer(reason, hasVessel
                    ? $"kerbal={kerbal} vessel={v.vesselName} pid={v.persistentId} loaded={Bool(v.loaded)} packed={Bool(v.packed)}"
                    : $"kerbal={kerbal} no active vessel");
                return;
            }
            if (CrewTransferOnPartSelectMethod == null)
            {
                RejectCrewTransfer(TestCommandCrewTransfer.TransferUnavailableReason,
                    "PartItemTransfer.OnPartSelect(Part) did not resolve");
                return;
            }

            CrewTransfer host;
            try
            {
                // Stock's own transfer: lists the valid destinations as the highlighted-part
                // prompt does and locks the controls until it is dismissed.
                host = CrewTransfer.Create(source, crew, OnCrewTransferDismissed);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "crewtransfer error reason=" + TestCommandCrewTransfer.TransferThrewReason
                    + $" kerbal={kerbal} create {ex.GetType().Name}: {ex.Message}");
                SetExecResult("ERROR", null, TestCommandCrewTransfer.TransferThrewReason);
                return;
            }

            List<Part> valid = host != null && host.validParts != null ? host.validParts : new List<Part>();
            var validNames = new List<string>(valid.Count);
            for (int i = 0; i < valid.Count; i++)
                validNames.Add(RuntimePartName(valid[i]));
            var allNames = new List<string>();
            for (int i = 0; i < v.parts.Count; i++)
                if (v.parts[i] != null && v.parts[i] != source)
                    allNames.Add(RuntimePartName(v.parts[i]));
            reason = TestCommandCrewTransfer.PickDestination(validNames, allNames, partName, out int index);
            if (reason != null)
            {
                // Nothing moved: close the prompt the way an Esc press does.
                if (host != null) host.DismissInterrupted();
                RejectCrewTransfer(reason, $"kerbal={kerbal} part={partName} valid=[{string.Join(",", validNames)}]");
                return;
            }

            crewTransferHost = host;
            crewTransferKerbal = kerbal;
            crewTransferFrom = source;
            crewTransferTo = valid[index];
            crewTransferPid = v.persistentId;
            crewTransferDismissedMoved = false;

            ParsekLog.Info(Tag, $"crewtransfer start kerbal='{kerbal}' pid={v.persistentId.ToString(CultureInfo.InvariantCulture)} "
                + $"from={RuntimePartName(source)} fromId={source.flightID.ToString(CultureInfo.InvariantCulture)} "
                + $"to={partName} toId={crewTransferTo.flightID.ToString(CultureInfo.InvariantCulture)} valid={Int(validNames.Count)}");
            try
            {
                // The click on the highlighted destination part.
                CrewTransferOnPartSelectMethod.Invoke(host, new object[] { crewTransferTo });
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                ParsekLog.Error(Tag, "crewtransfer error reason=" + TestCommandCrewTransfer.TransferThrewReason
                    + $" kerbal={kerbal} select {inner.GetType().Name}: {inner.Message}");
                RemoveCrewTransferState();
                SetExecResult("ERROR", null, TestCommandCrewTransfer.TransferThrewReason);
                return;
            }

            // OnPartSelect returns without moving only when an onCrewTransferSelected handler
            // vetoed the move; stock then leaves the prompt open.
            if (!crewTransferTo.protoModuleCrew.Contains(crew))
            {
                ParsekLog.Error(Tag, "crewtransfer error reason=" + TestCommandCrewTransfer.VetoedReason
                    + $" kerbal={kerbal} to={partName}");
                RemoveCrewTransferState();
                SetExecResult("ERROR", null, TestCommandCrewTransfer.VetoedReason);
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void OnCrewTransferDismissed(PartItemTransfer.DismissAction action, Part part)
        {
            ParsekLog.Verbose(Tag, $"crewtransfer dismissed action={action} part={(part != null ? RuntimePartName(part) : "<null>")}");
            if (action == PartItemTransfer.DismissAction.ItemMoved)
                crewTransferDismissedMoved = true;
            crewTransferHost = null;
        }

        private void TryCompleteCrewTransfer(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandCrewTransfer.Verb);
            Part to = crewTransferTo;
            Part from = crewTransferFrom;
            string kerbal = crewTransferKerbal;
            bool inDestination = false;
            if (to != null && to.protoModuleCrew != null)
            {
                for (int i = 0; i < to.protoModuleCrew.Count; i++)
                {
                    ProtoCrewMember pcm = to.protoModuleCrew[i];
                    if (pcm != null && string.Equals(pcm.name, kerbal, StringComparison.Ordinal))
                    {
                        inDestination = true;
                        break;
                    }
                }
            }
            CrewTransferCompletionDecision decision = TestCommandCrewTransfer.DecideCompletion(
                crewTransferDismissedMoved, inDestination, elapsed, budget);
            if (decision == CrewTransferCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            uint pid = crewTransferPid;
            string fromName = from != null ? RuntimePartName(from) : string.Empty;
            string toName = to != null ? RuntimePartName(to) : string.Empty;
            uint fromId = from != null ? from.flightID : 0u;
            uint toId = to != null ? to.flightID : 0u;
            int crewFrom = from?.protoModuleCrew != null ? from.protoModuleCrew.Count : 0;
            int crewTo = to?.protoModuleCrew != null ? to.protoModuleCrew.Count : 0;
            ClearTwoPhase();

            if (decision == CrewTransferCompletionDecision.Moved)
            {
                ParsekLog.Info(Tag, TestCommandCrewTransfer.FormatAppliedLine(
                    kerbal, pid, fromName, fromId, toName, toId, crewFrom, crewTo));
                EmitExecutedTerminal(id, seq, verb, "OK", TestCommandCrewTransfer.BuildOkPayload(
                    kerbal, pid, fromName, fromId, toName, toId, crewFrom, crewTo), null, dequeueHead: true);
                return;
            }
            TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandCrewTransfer.TimeoutReason);
            ParsekLog.Error(Tag, "crewtransfer error reason=" + TestCommandCrewTransfer.TimeoutReason
                + $" kerbal={kerbal ?? string.Empty} inDestination={Bool(inDestination)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandCrewTransfer.TimeoutReason, dequeueHead: true);
        }

        // A transfer prompt must never outlive its command: it holds a control lock.
        private void RemoveCrewTransferState()
        {
            CrewTransfer host = crewTransferHost;
            crewTransferHost = null;
            crewTransferKerbal = null;
            crewTransferFrom = null;
            crewTransferTo = null;
            crewTransferPid = 0u;
            crewTransferDismissedMoved = false;
            if (host != null)
            {
                try { host.DismissInterrupted(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"crewtransfer dismiss threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static string RuntimePartName(Part p)
            => p == null ? string.Empty : (p.partInfo != null ? p.partInfo.name : p.name);

        private void RejectCrewTransfer(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"crewtransfer rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
