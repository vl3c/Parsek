using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// A Supply Route — one origin, one or more stops, on a recurring
    /// dispatch schedule (design §4.7). Pure data; orchestration lives in
    /// <c>RouteStore</c> and later phases.
    /// </summary>
    /// <remarks>
    /// <para><b>Backing-mission model (design §0).</b> A v0 Supply Route is
    /// re-founded on the Missions subsystem: the route's visual is a looped
    /// Mission segment over <c>[launch .. undock]</c> of its source tree. The
    /// route holds the backing-mission DEFINITION (tree id + excluded interval
    /// keys + loop schedule) in the fields below; the Mission OBJECT itself is
    /// rebuilt on demand by <see cref="RouteBackingMission.BuildMission"/> and
    /// is NEVER inserted into <c>MissionStore</c> (the store would prune /
    /// normalize it by tree and surface it as a player mission). Guarantee:
    /// <see cref="BackingMissionTreeId"/> always equals the source tree id
    /// (<c>SourceRefs[].TreeId</c>) so the mutual-exclusion guard and the
    /// ghost-driving selector key on the same tree.</para>
    ///
    /// <para><b>(must-fix #3) Source-set contract.</b> The route's rendered
    /// member set is the backing-mission <c>[root.StartUT .. undockUT]</c> path
    /// (Mission-derived). On a multi-recording flight that path covers MORE than
    /// the single dock-child leaf, so the v0 contract widens
    /// <see cref="RecordingIds"/> / <see cref="SourceRefs"/> to cover EVERY
    /// <c>[root..undock]</c> member recording (one <see cref="RouteSourceRef"/>
    /// per member) and sets <see cref="TransitDuration"/> to the rendered span
    /// (<c>undockUT - root.StartUT</c>), not the leaf-only
    /// <c>source.EndUT - source.StartUT</c>. The leaf (dock-child) stays the
    /// delivery-binding carrier (its <c>RouteConnectionWindow</c> +
    /// <see cref="RecordedDockUT"/> / <see cref="DockMemberRecordingId"/>). This
    /// member-set capture is owned by Phase 5 <c>RouteBuilder</c>; Phase 1 pins
    /// the contract the codec + <c>RouteStore.RevalidateSources</c> must honor
    /// (revalidation tracks the whole rendered path, not just the leaf).</para>
    /// </remarks>
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

        // --- Backing-mission definition (design §0; Phase 1) ---

        /// <summary>
        /// Tree id of the source recording tree this route loops over. The route
        /// renders as a Mission segment over this tree's <c>[launch .. undock]</c>
        /// path. Guaranteed to equal <c>SourceRefs[].TreeId</c>. Null/empty until
        /// the route is founded on a backing mission (Phase 5 capture); a null
        /// value makes <see cref="IsLoopRoute"/> false.
        /// </summary>
        public string BackingMissionTreeId;

        /// <summary>
        /// Composition-interval keys (<c>MissionCompositionNode.HeadLegId</c>
        /// values) EXCLUDED from the backing mission so its render window
        /// end-trims to <c>[launch .. undock]</c> (drops the post-undock
        /// survivor / payload tail). Copied verbatim into
        /// <c>Mission.ExcludedIntervalKeys</c> by
        /// <see cref="RouteBackingMission.BuildMission"/>. Empty set = whole
        /// segment renders (honest fallback). Derived once at creation by
        /// <see cref="RouteBackingMission.ComputeExcludedIntervalKeys"/>.
        /// </summary>
        public HashSet<string> ExcludedIntervalKeys = new HashSet<string>();

        /// <summary>
        /// Recorded dock UT lifted from the leaf (dock-child) recording's
        /// <c>RouteConnectionWindow.DockUT</c>. The loop clock fires delivery
        /// when it crosses this UT within the backing-mission span each cycle
        /// (Phase 4). Default -1 (unset / no backing mission).
        /// </summary>
        public double RecordedDockUT = -1.0;

        /// <summary>
        /// Recording id of the leaf (dock-child) member that carries the
        /// delivery binding (the <c>RouteConnectionWindow</c> +
        /// <see cref="RecordedDockUT"/>). One of the
        /// <c>[root..undock]</c> member recordings. Null until captured.
        /// </summary>
        public string DockMemberRecordingId;

        /// <summary>
        /// Loop anchor UT set when the route is activated. Fed into the
        /// route-owned <c>Mission.LoopAnchorUT</c> by
        /// <see cref="RouteBackingMission.BuildMission"/>. NOTE: the loop builder
        /// floors the anchor to <c>spanEndUT</c>, so the route does NOT own the
        /// render phase — phase is owned by the loop-clock crossing detector +
        /// <see cref="LastObservedLoopCycleIndex"/> (Phase 4). Default -1 (unset).
        /// </summary>
        public double LoopAnchorUT = -1.0;

        /// <summary>
        /// Highest loop-clock cycle index observed by the crossing detector
        /// (Phase 4). A crossing fires when the current cycle index exceeds this.
        /// Resets to -1 on activate (first post-activate cycle fires) and
        /// persists through the codec so a save/reload mid-cycle does not
        /// double-fire. Default -1 (no cycle observed yet).
        /// </summary>
        public long LastObservedLoopCycleIndex = -1;

        /// <summary>
        /// Phase 0 discriminator (design §0.5, §0.6): TRUE when this route has a
        /// backing-mission tree, which is every v0 route. v0 has no non-loop
        /// dispatch model, so the self-timer paths (<see cref="NextDispatchUT"/>,
        /// <see cref="TransitDuration"/> arrival, <see cref="PendingDeliveryUT"/>
        /// fire) are dead for every loop-route but stay serialized for
        /// diagnostics and a possible future Send-Once mode.
        /// </summary>
        public bool IsLoopRoute => !string.IsNullOrEmpty(BackingMissionTreeId);

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
                ParsekLog.Verbose("Route",
                    $"Route {ShortIdForLog()} stay={prev} reason={reason ?? "<none>"}");
                return;
            }
            Status = next;
            ParsekLog.Info("Route",
                $"Route {ShortIdForLog()} {prev}→{next} reason={reason ?? "<none>"}");
        }

        private string ShortIdForLog()
        {
            if (string.IsNullOrEmpty(Id)) return "<no-id>";
            return Id.Length > 8 ? Id.Substring(0, 8) : Id;
        }
    }
}
