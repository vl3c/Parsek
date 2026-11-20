using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The rescue contracts whose stranded vessel a contract rebuild could not regenerate in the
    /// scene it ran in (todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS, owner ruling
    /// 2026-11-20). The rebuild (<c>KspStatePatcher.PatchContractsCore</c>) puts a RecoverAsset
    /// back as Active with <c>Contract.Load</c>, which never runs <c>OnAccepted</c>; stock's
    /// generators run only in the Space Center and the Tracking Station
    /// (<see cref="ContractAcceptWorldEffects.IsRegenerationScene"/>). A rebuild in Flight or an
    /// editor lists the contract here, and the next Space Center or Tracking Station visit drains
    /// the list (<c>KspStatePatcher.DrainRescueAcceptDeferrals</c>, on stock's
    /// <c>onContractsLoaded</c>) under the same guards the rebuild applies. Without the list the
    /// gap is permanent: every later patch keeps the Active contract as it is.
    ///
    /// <para>
    /// The list describes the world of the save that carries it (the contract is Active and its
    /// vessel is missing in that save's world), so it is saved in the PARSEK node
    /// (<see cref="NodeName"/>, an additive child) and each load takes the loaded save's copy,
    /// except a plain rewind and a Discard Re-fly, whose node is not the landed world's
    /// (<see cref="LoadStateCategory.RescueAcceptDeferrals"/>).
    /// </para>
    ///
    /// <para>
    /// Only the rebuild adds an id. A contract the player accepted live never enters the list
    /// (stock's own <c>OnAccepted</c> spawned its vessel), so a later loss of that vessel (the
    /// player terminated it) is never read as a missing accept effect. That is why the drain
    /// reads this list and does not scan every Active rescue.
    /// </para>
    /// </summary>
    internal static class RescueAcceptDeferrals
    {
        internal const string Tag = "KspStatePatcher";
        internal const string NodeName = "RESCUE_ACCEPT_DEFERRALS";
        internal const string ValueName = "contractId";

        private static readonly List<string> pending = new List<string>();

        internal static IReadOnlyList<string> Pending => pending;
        internal static int Count => pending.Count;

        internal static void ResetForTesting()
        {
            pending.Clear();
        }

        /// <summary>
        /// Pure: the canonical form of a contract id (a guid, "D" format), or null when it is not
        /// one. The patch keys contracts by <c>Guid.ToString()</c>, which is the "D" form.
        /// </summary>
        internal static string Normalize(string contractId)
        {
            Guid g;
            if (string.IsNullOrEmpty(contractId) || !Guid.TryParse(contractId.Trim(), out g) || g == Guid.Empty)
                return null;
            return g.ToString("D");
        }

        /// <summary>Lists a contract whose stranded vessel waits for a regeneration scene.
        /// Returns true when it was not listed yet.</summary>
        internal static bool Add(string contractId, string reason)
        {
            string id = Normalize(contractId);
            if (id == null)
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Rescue accept deferral not listed: '{0}' is not a contract guid ({1})",
                    contractId ?? "(null)", reason ?? "unspecified"));
                return false;
            }
            if (pending.Contains(id))
            {
                ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Rescue accept deferral already listed: contractId='{0}' ({1}); pending={2}",
                    id, reason ?? "unspecified", pending.Count));
                return false;
            }
            pending.Add(id);
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Rescue accept deferral listed: contractId='{0}' ({1}); its stranded vessel is regenerated at " +
                "the next Space Center or Tracking Station visit; pending={2}",
                id, reason ?? "unspecified", pending.Count));
            return true;
        }

        /// <summary>Takes a contract off the list. Returns true when it was listed.</summary>
        internal static bool Remove(string contractId, string reason)
        {
            string id = Normalize(contractId);
            if (id == null || !pending.Remove(id))
                return false;
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Rescue accept deferral removed: contractId='{0}' ({1}); pending={2}",
                id, reason ?? "unspecified", pending.Count));
            return true;
        }

        /// <summary>Installs <paramref name="ids"/> (a load's copy) in place of the list.</summary>
        internal static void Replace(List<string> ids, string reason)
        {
            int before = pending.Count;
            pending.Clear();
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = Normalize(ids[i]);
                    if (id != null && !pending.Contains(id))
                        pending.Add(id);
                }
            }
            string line = string.Format(CultureInfo.InvariantCulture,
                "Rescue accept deferrals installed ({0}): pending={1} (was {2})",
                reason ?? "unspecified", pending.Count, before);
            if (pending.Count > 0 || before > 0)
                ParsekLog.Info(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);
        }

        internal static void Clear(string reason)
        {
            int before = pending.Count;
            pending.Clear();
            string line = string.Format(CultureInfo.InvariantCulture,
                "Rescue accept deferrals cleared ({0}): dropped={1}", reason ?? "unspecified", before);
            if (before > 0)
                ParsekLog.Info(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);
        }

        /// <summary>
        /// Writes the list into the PARSEK node, replacing any earlier copy; an empty list writes
        /// nothing. Returns how many ids it wrote.
        /// </summary>
        internal static int SaveInto(ConfigNode parsekNode)
        {
            if (parsekNode == null)
                return 0;
            parsekNode.RemoveNodes(NodeName);
            if (pending.Count == 0)
                return 0;
            ConfigNode root = parsekNode.AddNode(NodeName);
            for (int i = 0; i < pending.Count; i++)
                root.AddValue(ValueName, pending[i]);
            return pending.Count;
        }

        /// <summary>Reads what <see cref="SaveInto"/> wrote; skips a value that is not a guid.</summary>
        internal static List<string> LoadFrom(ConfigNode parsekNode)
        {
            var result = new List<string>();
            ConfigNode root = parsekNode != null ? parsekNode.GetNode(NodeName) : null;
            if (root == null)
                return result;
            string[] values = root.GetValues(ValueName);
            int skipped = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string id = Normalize(values[i]);
                if (id == null)
                {
                    skipped++;
                    continue;
                }
                if (!result.Contains(id))
                    result.Add(id);
            }
            if (skipped > 0)
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Rescue accept deferrals: skipped {0} unreadable contract id(s) of {1}",
                    skipped, values.Length));
            return result;
        }
    }
}
