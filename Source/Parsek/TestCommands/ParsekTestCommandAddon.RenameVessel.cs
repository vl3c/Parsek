using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the automation-only <c>RenameVessel</c> verb; the contract is on
    /// <see cref="TestCommandRenameVessel"/>. SINGLE-PHASE: the stock accept callback (the vessel
    /// rename dialog's, or with <c>part=</c> the part naming dialog's) runs synchronously, and
    /// Parsek's capture pair runs inside it.
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
            string partArg = ArgOrNull(cmd, TestCommandRenameVessel.PartKey);
            string priorityArg = ArgOrNull(cmd, TestCommandRenameVessel.PriorityKey);
            reason = TestCommandRenameVessel.ParsePartNaming(partArg, priorityArg,
                GameSettings.VESSEL_NAMING_PRIORTY_LEVEL_MAX,
                out string partName, out int priority, out bool hasPart);
            if (reason != null)
            {
                RejectRenameVessel(reason, $"part={partArg ?? string.Empty} priority={priorityArg ?? string.Empty}");
                return;
            }

            Vessel v = hasPid ? FindLiveVesselByPid(pid) : FlightGlobals.ActiveVessel;
            bool ghost = v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId);
            if (hasPart)
            {
                // The vessel half of the gate; the part half (and the part accept) follow.
                reason = TestCommandRenameVessel.DecideTarget(hasPid, v != null, ghost, acceptAvailable: true);
                if (reason != null)
                {
                    RejectRenameVessel(reason, hasPid ? $"pid={pidArg}" : "target=active");
                    return;
                }
                RenamePartNaming(v, name, partName, priority);
                return;
            }

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

            ReadRenameIdentity(v, out int legs, out VesselNameDecision decision);
            string type = v.vesselType.ToString();
            ParsekLog.Info(Tag, TestCommandRenameVessel.FormatAppliedLine(
                v.persistentId, oldName, after, type, active, legs, decision));
            SetExecResult("OK", TestCommandRenameVessel.BuildOkPayload(
                v.persistentId, oldName, after, type, active, legs, decision), null);
        }

        /// <summary>
        /// The part naming dialog's accept on the one part of <paramref name="v"/> named
        /// <paramref name="partName"/>, with the type the dialog opens on and the given priority.
        /// </summary>
        private void RenamePartNaming(Vessel v, string name, string partName, int priority)
        {
            int namedParts = 0;
            Part part = null;
            if (v.loaded && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    string runtimeName = p.partInfo != null ? p.partInfo.name : p.name;
                    if (!string.Equals(runtimeName, partName, StringComparison.Ordinal)) continue;
                    namedParts++;
                    part = p;
                }
            }
            bool buttonShown = false;
            if (namedParts == 1 && part.Events != null
                && part.Events.Contains(TestCommandRenameVessel.PartNamingEventName))
            {
                BaseEvent ev = part.Events[TestCommandRenameVessel.PartNamingEventName];
                buttonShown = ev != null && ev.guiActive;
            }
            MethodBase partAccept = PartVesselNamingAcceptCapturePatch.ResolveTargetMethodForTesting();
            string reason = TestCommandRenameVessel.DecidePartTarget(
                v.loaded, namedParts, buttonShown, partAccept != null);
            if (reason != null)
            {
                RejectRenameVessel(reason, $"pid={v.persistentId} part={partName} named={namedParts}");
                return;
            }

            string oldName = v.vesselName;
            bool active = v == FlightGlobals.ActiveVessel;
            // The dialog opens on the part's own naming type when it has one, else the vessel's
            // (VesselRenameDialog.SpawnNameFromPart in flight); a player's accept keeps it.
            VesselType type = part.vesselNaming != null ? part.vesselNaming.vesselType : v.vesselType;
            try
            {
                partAccept.Invoke(part, new object[] { name, type, priority });
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                ParsekLog.Error(Tag, "renamevessel error reason=" + TestCommandRenameVessel.AcceptThrewReason
                    + $" pid={v.persistentId} part={partName} {inner.GetType().Name}: {inner.Message}");
                SetExecResult("ERROR", null, TestCommandRenameVessel.AcceptThrewReason);
                return;
            }

            VesselNaming naming = part.vesselNaming;
            if (!TestCommandRenameVessel.WasPartNamingApplied(name, priority, naming != null,
                    naming?.vesselName, naming != null ? naming.namingPriority : 0))
            {
                ParsekLog.Error(Tag, "renamevessel error reason=" + TestCommandRenameVessel.NotAppliedReason
                    + $" pid={v.persistentId} part={partName} requested='{name}' priority={priority}"
                    + $" naming='{naming?.vesselName ?? string.Empty}'");
                SetExecResult("ERROR", null, TestCommandRenameVessel.NotAppliedReason);
                return;
            }

            string after = v.vesselName;
            ReadRenameIdentity(v, out int legs, out VesselNameDecision decision);
            string vesselType = v.vesselType.ToString();
            ParsekLog.Info(Tag, TestCommandRenameVessel.FormatPartAppliedLine(
                partName, part.flightID, priority, name,
                v.persistentId, oldName, after, vesselType, active, legs, decision));
            SetExecResult("OK", TestCommandRenameVessel.BuildPartOkPayload(
                partName, part.flightID, priority, name,
                v.persistentId, oldName, after, vesselType, active, legs, decision), null);
        }

        /// <summary>The report-only identity read of the answer: the recorded legs of
        /// <paramref name="v"/>'s identity and the decision the store holds for it.</summary>
        private void ReadRenameIdentity(Vessel v, out int legs, out VesselNameDecision decision)
        {
            legs = 0;
            decision = null;
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
