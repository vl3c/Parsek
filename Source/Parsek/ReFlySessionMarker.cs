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
        /// <summary>Unique GUID per invocation/retry (design §5.7).</summary>
        public string SessionId;

        /// <summary>RecordingTree this re-fly belongs to (design §5.7).</summary>
        public string TreeId;

        /// <summary>
        /// The NotCommitted provisional re-fly recording created at §6.3 step 1 (design §5.7).
        /// </summary>
        public string ActiveReFlyRecordingId;

        /// <summary>Supersede target — the child recording being re-flown (design §5.7).</summary>
        public string OriginChildRecordingId;

        /// <summary>Invoked RewindPoint (design §5.7).</summary>
        public string RewindPointId;

        /// <summary>Planetarium UT at which the session was invoked (design §5.7).</summary>
        public double InvokedUT;

        /// <summary>Wall-clock timestamp at invocation (ISO 8601 UTC; design §5.7).</summary>
        public string InvokedRealTime;

        internal const string NodeName = "REFLY_SESSION_MARKER";

        /// <summary>Saves into a dedicated child node on the parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("sessionId", SessionId ?? "");
            node.AddValue("treeId", TreeId ?? "");
            node.AddValue("activeReFlyRecordingId", ActiveReFlyRecordingId ?? "");
            node.AddValue("originChildRecordingId", OriginChildRecordingId ?? "");
            node.AddValue("rewindPointId", RewindPointId ?? "");
            node.AddValue("invokedUT", InvokedUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(InvokedRealTime))
                node.AddValue("invokedRealTime", InvokedRealTime);
        }

        /// <summary>Loads from a single <see cref="NodeName"/> node (caller supplies the node directly).</summary>
        public static ReFlySessionMarker LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var m = new ReFlySessionMarker();

            string sid = node.GetValue("sessionId");
            m.SessionId = string.IsNullOrEmpty(sid) ? null : sid;

            string tid = node.GetValue("treeId");
            m.TreeId = string.IsNullOrEmpty(tid) ? null : tid;

            string active = node.GetValue("activeReFlyRecordingId");
            m.ActiveReFlyRecordingId = string.IsNullOrEmpty(active) ? null : active;

            string origin = node.GetValue("originChildRecordingId");
            m.OriginChildRecordingId = string.IsNullOrEmpty(origin) ? null : origin;

            string rp = node.GetValue("rewindPointId");
            m.RewindPointId = string.IsNullOrEmpty(rp) ? null : rp;

            string utStr = node.GetValue("invokedUT");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                m.InvokedUT = ut;

            m.InvokedRealTime = node.GetValue("invokedRealTime");

            return m;
        }
    }
}
