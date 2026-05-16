using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// A Supply Route — one origin, one or more stops, on a recurring
    /// dispatch schedule (design §4.7). Pure data; orchestration lives in
    /// <c>RouteStore</c> and later phases.
    /// </summary>
    internal sealed class Route
    {
        // --- Identity ---

        /// <summary>Unique route ID (GUID).</summary>
        public string Id;

        /// <summary>Ordered chain of source recording IDs that prove this route.</summary>
        public List<string> RecordingIds = new List<string>();

        /// <summary>Player-visible name (editable).</summary>
        public string Name;

        /// <summary>
        /// Immutable source proof/version refs captured at route creation;
        /// one entry per <see cref="RecordingIds"/> entry.
        /// </summary>
        public List<RouteSourceRef> SourceRefs = new List<RouteSourceRef>();

        // --- Endpoints ---

        /// <summary>Where the route starts each cycle.</summary>
        public RouteEndpoint Origin;

        /// <summary>Ordered stops along the route (v1: exactly one).</summary>
        public List<RouteStop> Stops = new List<RouteStop>();

        /// <summary>True = Career charges KSC funds instead of physical origin cargo.</summary>
        public bool IsKscOrigin;

        // --- Resource transfer ---

        /// <summary>Per-resource quantities used or delivered across the whole route.</summary>
        public Dictionary<string, double> CostManifest;

        /// <summary>Exact stored-part payloads used or delivered across the whole route.</summary>
        public List<InventoryPayloadItem> InventoryCostManifest;

        /// <summary>Stock part + used/delivered cargo funds per KSC dispatch.</summary>
        public double KscDispatchFundsCost;

        // --- Scheduling / timing ---

        /// <summary>Seconds (= total chain duration).</summary>
        public double TransitDuration;

        /// <summary>Seconds between cycle starts.</summary>
        public double DispatchInterval;

        /// <summary>Original flight start UT; anchors inter-body synodic phase.</summary>
        public double DispatchWindowEpochUT;

        /// <summary>0 for same-body, synodic period for inter-body.</summary>
        public double DispatchWindowPeriod;

        /// <summary>UT of next scheduled dispatch.</summary>
        public double NextDispatchUT;

        /// <summary>UT when the in-transit cycle began; null when idle.</summary>
        public double? CurrentCycleStartUT;

        /// <summary>Retry backoff for resource/funds waits; null when not waiting.</summary>
        public double? NextEligibilityCheckUT;

        /// <summary>0-based active source-recording index; -1 when not in transit.</summary>
        public int CurrentSegmentIndex = -1;

        // --- Per-stop pending delivery (computed at each stop boundary during transit) ---

        /// <summary>UT when next route boundary is due; null when not in transit.</summary>
        public double? PendingDeliveryUT;

        /// <summary>Stop due at <see cref="PendingDeliveryUT"/>, or -1 when current boundary has no stop.</summary>
        public int PendingStopIndex = -1;

        // --- Linking ---

        /// <summary>Paired route for round-trip; null if standalone.</summary>
        public string LinkedRouteId;

        // --- Status ---

        /// <summary>Lifecycle state. Always mutate through <see cref="TransitionTo"/>.</summary>
        public RouteStatus Status = RouteStatus.Active;

        /// <summary>Pause requested while InTransit; transition to Paused after completion.</summary>
        public bool PauseAfterCurrentCycle;

        /// <summary>Total successful cycle completions.</summary>
        public int CompletedCycles;

        /// <summary>Reserved diagnostic counter for explicit skip policies; v1 wait states do not increment it.</summary>
        public int SkippedCycles;

        /// <summary>
        /// Canonical save shape — writes every Route field into <paramref name="node"/>
        /// per design §4.8. Implementation lives in <see cref="RouteCodec"/>.
        /// </summary>
        internal void SerializeInto(ConfigNode node)
        {
            RouteCodec.SerializeInto(this, node);
        }

        /// <summary>
        /// Canonical load entry point — returns a fully populated route, or
        /// <c>null</c> on a rejected route (missing STOP children or malformed
        /// SOURCE entry). See <see cref="RouteCodec"/> for the reject rules.
        /// </summary>
        internal static Route DeserializeFrom(ConfigNode node)
        {
            return RouteCodec.DeserializeFrom(node);
        }

        /// <summary>
        /// Centralizes status transitions so every state change emits a log line and
        /// no caller can mutate <see cref="Status"/> directly without leaving an audit
        /// trail. Use <see cref="ParsekLog.Info"/> for genuine transitions and
        /// <see cref="ParsekLog.Verbose"/> for self-transitions (a→a).
        /// </summary>
        internal void TransitionTo(RouteStatus next, string reason)
        {
            RouteStatus prev = Status;
            if (prev == next)
            {
                ParsekLog.Verbose("RouteStore",
                    $"Route {ShortIdForLog()} stay={prev} reason={reason ?? "<none>"}");
                return;
            }
            Status = next;
            ParsekLog.Info("RouteStore",
                $"Route {ShortIdForLog()} {prev}→{next} reason={reason ?? "<none>"}");
        }

        private string ShortIdForLog()
        {
            if (string.IsNullOrEmpty(Id)) return "<no-id>";
            return Id.Length > 8 ? Id.Substring(0, 8) : Id;
        }
    }
}
