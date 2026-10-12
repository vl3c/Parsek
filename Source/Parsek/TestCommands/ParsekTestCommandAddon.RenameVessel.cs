using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only <c>RenameVessel</c> verb; the contract is on
    /// <see cref="TestCommandRenameVessel"/>. SINGLE-PHASE: the stock accept callback runs
    /// synchronously, and Parsek's capture pair runs inside it.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void RenameVesselImpl(ParsedCommand cmd)
        {
            string nameArg = ArgOrNull(cmd, TestCommandRenameVessel.NameKey);
            string reason = TestCommandRenameVessel.ParseName(nameArg, out string name);
            if (reason != null)
            {
                RejectRenameVessel(reason, $"name={nameArg ?? string.Empty}");
                return;
            }
            string pidArg = ArgOrNull(cmd, TestCommandRenameVessel.PidKey);
            reason = TestCommandRenameVessel.ParsePid(pidArg, out uint pid, out bool hasPid);
            if (reason != null)
            {
                RejectRenameVessel(reason, $"pid={pidArg ?? string.Empty}");
                return;
            }

            Vessel v = hasPid ? FindLiveVesselByPid(pid) : FlightGlobals.ActiveVessel;
            bool ghost = v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId);
            MethodBase accept = VesselRenameAcceptCapturePatch.ResolveTargetMethodForTesting();
            reason = TestCommandRenameVessel.DecideTarget(hasPid, v != null, ghost, accept != null);
            if (reason != null)
            {
                RejectRenameVessel(reason, hasPid ? $"pid={pidArg}" : "target=active");
                return;
            }

            string oldName = v.vesselName;
            bool active = v == FlightGlobals.ActiveVessel;
            try
            {
                // The stock rename dialog's accept callback, with the vessel's own type, so the
                // capture pair runs exactly as for a player's accept.
                accept.Invoke(v, new object[] { name, v.vesselType });
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                ParsekLog.Error(Tag, "renamevessel error reason=" + TestCommandRenameVessel.AcceptThrewReason
                    + $" pid={v.persistentId} {inner.GetType().Name}: {inner.Message}");
                SetExecResult("ERROR", null, TestCommandRenameVessel.AcceptThrewReason);
                return;
            }

            string after = v.vesselName;
            if (!TestCommandRenameVessel.WasApplied(name, after))
            {
                ParsekLog.Error(Tag, "renamevessel error reason=" + TestCommandRenameVessel.NotAppliedReason
                    + $" pid={v.persistentId} requested='{name}' after='{after ?? string.Empty}'");
                SetExecResult("ERROR", null, TestCommandRenameVessel.NotAppliedReason);
                return;
            }

            int legs = 0;
            VesselNameDecision decision = null;
            try
            {
                List<RecordingTree> trees = VesselNameStore.GatherTrees();
                VesselIdentity.Resolution res = VesselNameStore.ResolveUniverse(
                    trees, VesselNameStore.GatherLoose(trees));
                VesselIdentity.Identity identity = res.ResolveLiveVessel(
                    v.id.ToString("N"), v.persistentId, GhostMapPresence.IsGhostMapVessel);
                if (identity != null)
                {
                    legs = identity.LegIds.Count;
                    decision = TestCommandRenameVessel.PickDecision(identity.Guids, VesselNameStore.FindByGuid);
                }
            }
            catch (Exception ex)
            {
                // A report-only read; the rename itself already happened.
                ParsekLog.Warn(Tag, $"renamevessel identity read threw: {ex.GetType().Name}: {ex.Message}");
            }

            string type = v.vesselType.ToString();
            ParsekLog.Info(Tag, TestCommandRenameVessel.FormatAppliedLine(
                v.persistentId, oldName, after, type, active, legs, decision));
            SetExecResult("OK", TestCommandRenameVessel.BuildOkPayload(
                v.persistentId, oldName, after, type, active, legs, decision), null);
        }

        private static Vessel FindLiveVesselByPid(uint pid)
        {
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null) return null;
            for (int i = 0; i < vessels.Count; i++)
                if (vessels[i] != null && vessels[i].persistentId == pid)
                    return vessels[i];
            return null;
        }

        private void RejectRenameVessel(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"renamevessel rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
