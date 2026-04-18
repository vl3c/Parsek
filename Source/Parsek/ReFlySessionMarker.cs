using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Singleton ScenarioModule entry marking that a re-fly session is live
    /// (design doc section 5.7). Written atomically in the same synchronous
    /// code path that creates the provisional re-fly recording; cleared on
    /// merge, return-to-Space-Center, quit-without-merge, retry (with a fresh
    /// session id), full-revert, and load-time validation failure.
    ///
    /// <para>Persisted as a single <c>REFLY_SESSION_MARKER</c> ConfigNode on
    /// ParsekScenario. This Phase 1 type defines only the shape; behavior
    /// wiring (validation, spare-set, zombie cleanup) lands in later phases.</para>
    /// </summary>
    public class ReFlySessionMarker
    {
        public string SessionId;
        public string OriginRpId;
        public int OriginSlotIndex;
        public string ProvisionalRecordingId;

        /// <summary>Wall-clock timestamp (ISO 8601 UTC string).</summary>
        public string StartRealTime;

        /// <summary>Planetarium UT at which the session was invoked.</summary>
        public double StartUT;

        internal const string NodeName = "REFLY_SESSION_MARKER";

        /// <summary>Saves into a dedicated child node on the parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("sessionId", SessionId ?? "");
            node.AddValue("originRpId", OriginRpId ?? "");
            node.AddValue("originSlotIndex", OriginSlotIndex.ToString(ic));
            node.AddValue("provisionalRecordingId", ProvisionalRecordingId ?? "");
            if (!string.IsNullOrEmpty(StartRealTime))
                node.AddValue("startRealTime", StartRealTime);
            node.AddValue("startUT", StartUT.ToString("R", ic));
        }

        /// <summary>Loads from a single <see cref="NodeName"/> node (caller supplies the node directly).</summary>
        public static ReFlySessionMarker LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var m = new ReFlySessionMarker();

            string sid = node.GetValue("sessionId");
            m.SessionId = string.IsNullOrEmpty(sid) ? null : sid;

            string rp = node.GetValue("originRpId");
            m.OriginRpId = string.IsNullOrEmpty(rp) ? null : rp;

            string slotStr = node.GetValue("originSlotIndex");
            int slot;
            if (!string.IsNullOrEmpty(slotStr) && int.TryParse(slotStr, NumberStyles.Integer, ic, out slot))
                m.OriginSlotIndex = slot;

            string prov = node.GetValue("provisionalRecordingId");
            m.ProvisionalRecordingId = string.IsNullOrEmpty(prov) ? null : prov;

            m.StartRealTime = node.GetValue("startRealTime");

            string utStr = node.GetValue("startUT");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                m.StartUT = ut;

            return m;
        }
    }
}
