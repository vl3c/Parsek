using System;
using System.Collections.Generic;
using System.Globalization;

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

        /// <summary>
        /// True = the route's cargo was HARVESTED en route (M2, plan D7): the
        /// run started undocked and every delivered resource was covered by
        /// witnessed harvest windows, so there is no physical origin vessel
        /// and nothing to debit (19.2.2 item 3 "Harvested - Debit: none").
        /// <see cref="Origin"/> is a display-only endpoint built from the
        /// first harvest window's open location (pid 0); dispatch eligibility
        /// skips origin endpoint resolution and the origin-cargo gate, and
        /// the per-cycle <see cref="CostManifest"/> is EMPTY (the
        /// dispatch/debit ledger row pair still emits for row-shape
        /// stability, as a structural no-op). Mutually exclusive with
        /// <see cref="IsKscOrigin"/>. Sparse in the codec (omitted when
        /// false).
        /// </summary>
        public bool IsHarvestOrigin;

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

        /// <summary>
        /// Player-facing dispatch cadence multiplier (Phase 6). Integer
        /// <c>&gt;= 1</c>; default 1 = the MINIMUM loop time = the floor (the
        /// route dispatches as often as the run allows; it cannot go faster). The
        /// player raises <c>N</c> to launch LESS often. For a v0 SAME-BODY route
        /// the run's natural period IS the span (<see cref="TransitDuration"/>), so
        /// <see cref="DispatchInterval"/> is DERIVED as
        /// <c>CadenceMultiplier * TransitDuration</c> (set in <c>RouteBuilder</c> at
        /// creation and recomputed by the cadence UI when <c>N</c> changes). The loop
        /// clock still reads <see cref="DispatchInterval"/> directly (Phase 4
        /// unchanged); <see cref="CadenceMultiplier"/> is only the UI/derivation
        /// handle. Always clamp <c>&gt;= 1</c> on every write (UI, builder, codec).
        /// M5 inter-body: <c>N</c> applies as a RESIDUAL modulo on the window
        /// index (<c>RouteLoopClock.ResolveResidualCadence</c>) - OFF for a
        /// flat route (N lives in the interval, this v0 contract), 1 for a
        /// zero-drift scheduled route (N is consumed Missions-side by the
        /// schedule's minSpacing throttle), N for a re-aim synodic route
        /// (deliver every Nth rendered window, anchored at
        /// <see cref="WindowAnchorCycleIndex"/>).
        /// </summary>
        public int CadenceMultiplier = 1;

        /// <summary>
        /// Player-facing dispatch priority (M1, design D8). Lower value
        /// dispatches FIRST when several routes contend in the same orchestrator
        /// tick (e.g. simultaneous dock crossings against a shared origin):
        /// <see cref="RouteOrchestrator.CompareRoutesForTick"/> sorts the per-tick
        /// snapshot ascending on this, then <see cref="NextDispatchUT"/>, then
        /// ordinal <see cref="Id"/>. Default 0 = the highest priority; floor 0
        /// (clamp via <see cref="ClampPriority"/> on every write: UI, codec).
        /// Sparse in the codec: omitted when 0 so it never bloats a default save.
        /// </summary>
        public int DispatchPriority;

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

        /// <summary>
        /// Round-trip linking alternation cursor (M4c Phase C1, plan D12 / OQ8):
        /// the partner route's <see cref="CompletedCycles"/> value at the moment
        /// THIS route last consumed a partner cycle (i.e. last dispatched under the
        /// chain constraint). The partner gate
        /// (<see cref="RouteDispatchEvaluator.PartnerConstraintSatisfied"/>) holds
        /// this route <see cref="RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner"/>
        /// while <c>partner.CompletedCycles &lt;= LastConsumedPartnerCycle</c>, i.e.
        /// until the partner completes a NEW cycle; on a dispatch the orchestrator
        /// advances it to the partner's current <see cref="CompletedCycles"/>, so
        /// the route holds again until the partner completes ANOTHER cycle (strict
        /// A-&gt;B-&gt;A alternation).
        ///
        /// <para><b>Default 0</b> (NOT -1): a fresh mutual chain has both routes at
        /// 0 with the partner at 0 completions, so both compute <c>0 &lt;= 0</c> =
        /// held - the deadlock the seed rule breaks. -1 would let both dispatch on
        /// cycle 0 (<c>0 &lt;= -1</c> = false), breaking alternation. Only routes
        /// with a non-null <see cref="LinkedRouteId"/> ever advance it; an unlinked
        /// route keeps the 0 default forever.</para>
        ///
        /// <para>Sparse in the codec: omitted at the 0 default (mirrors the
        /// <see cref="DispatchPriority"/> / <c>lastObservedLoopCycleIndex</c> sparse
        /// convention), so an unlinked / pre-M4c route writes NOTHING new and
        /// round-trips byte-identically.</para>
        /// </summary>
        public int LastConsumedPartnerCycle;

        // --- Status ---

        /// <summary>
        /// UT at which the route was committed to the store (route-timeline events);
        /// -1 when unknown (constructed off-Unity, e.g. unit tests, or a route saved
        /// before the field existed). Stamped once by <c>RouteStore.AddRoute</c> when
        /// still unset; never rewritten afterward. This is the route's creation point
        /// on the timeline: a rewind below it reverts the route away with the save
        /// snapshot, and the planned rewind-visibility extension keys dormant
        /// re-materialization on it. Sparse in the codec (omitted when &lt; 0).
        /// </summary>
        public double CreatedUT = -1.0;

        /// <summary>Lifecycle state. Always mutate through <see cref="TransitionTo"/>.</summary>
        public RouteStatus Status = RouteStatus.Active;

        /// <summary>
        /// The status the route held immediately BEFORE
        /// <see cref="RouteStore.RevalidateSources"/> flipped it to
        /// <see cref="RouteStatus.MissingSourceRecording"/>. Captured on the
        /// into-missing edge so a deliberate <see cref="RouteStatus.Paused"/> (or
        /// any other non-source status) is restored faithfully when the missing
        /// sources flicker back into ERS, instead of silently un-pausing to
        /// <see cref="RouteStatus.Active"/>. Cleared (back to the
        /// <see cref="RouteStatus.Active"/> sentinel default) on recovery. Sparse
        /// in the codec: only written when it is NOT the default
        /// <see cref="RouteStatus.Active"/>, so it never bloats a healthy save.
        /// </summary>
        public RouteStatus PreMissingStatus = RouteStatus.Active;

        /// <summary>Pause requested while InTransit; transition to Paused after completion.</summary>
        public bool PauseAfterCurrentCycle;

        /// <summary>
        /// True while a player "Send Once" one-shot is armed and its cycle has not
        /// yet completed (route-timeline events). Distinguishes the Send Once arm
        /// from an ordinary pause-after-cycle request (both set
        /// <see cref="PauseAfterCurrentCycle"/>): the Logistics window shows the
        /// "Sending one cycle" state from this, and the dispatched ledger row is
        /// stamped <c>RouteSendOnce</c> from it so the one-shot run is identifiable
        /// in the timeline. Set by <c>RouteOrchestrator.TrySendOneCycleNow</c>;
        /// cleared by the armed-pause completion, a player Pause, and a player
        /// Activate. Sparse in the codec (omitted when false).
        /// </summary>
        public bool SendOnceArmed;

        /// <summary>Total successful cycle completions.</summary>
        public int CompletedCycles;

        /// <summary>
        /// Count of loop-route cycles that crossed but were blocked by eligibility
        /// (destination full, origin empty, funds short, endpoint lost, etc.). The
        /// loop-clock path (Phase 4) increments this on a blocked cycle: the ghost
        /// still flies (the world looks busy) but nothing is debited or delivered,
        /// and the crossing index is snapped forward. The increment is load-bearing
        /// for cycleId uniqueness — the per-delivery cycleId is
        /// <c>cycle-{CompletedCycles + SkippedCycles}</c>, so bumping SkippedCycles
        /// on a blocked cycle advances the next cycleId and keeps the
        /// dispatch/deliver pairing unique (no double-charge after a skip).
        /// </summary>
        public int SkippedCycles;

        // --- Last hold reason (M6 hold reasons) ---

        /// <summary>
        /// Eligibility-failure kind of the most recent hold (a blocked loop
        /// crossing, a legacy wait/endpoint-lost transition, or an
        /// endpoint-lost-at-delivery); <see
        /// cref="RouteDispatchEvaluator.EligibilityFailureKind.None"/> when no
        /// hold is recorded. Written via <see cref="RecordHold"/>, reset via
        /// <see cref="ClearHold"/> (eligible crossing, legacy dispatch, player
        /// Activate). Persisted sparsely by <see cref="RouteCodec"/> (omitted
        /// at the None default; serialized by NAME so an unknown future value
        /// loads back as None).
        /// </summary>
        public RouteDispatchEvaluator.EligibilityFailureKind LastHoldKind =
            RouteDispatchEvaluator.EligibilityFailureKind.None;

        /// <summary>
        /// Raw evaluator reason token of the most recent hold, VERBATIM (e.g. a
        /// bare resource name, <c>funds-short</c>, <c>stop-0-no-live-vessels</c>,
        /// or a legacy-prefixed <c>origin-lacks-X</c>); null when no hold is
        /// recorded. Player-language mapping happens in the UI formatter
        /// (<c>LogisticsHoldPresentation</c>), never here. Sparse in the codec
        /// (omitted when null/empty).
        /// </summary>
        public string LastHoldDetail;

        /// <summary>
        /// Funds shortfall of the most recent hold (FundsShort only); 0 for
        /// every other kind. Sparse in the codec (omitted when not &gt; 0).
        /// </summary>
        public double LastHoldShortfall;

        /// <summary>
        /// UT at which the most recent hold was recorded; -1 when no hold is
        /// recorded. Drives the display-side "checked {age} ago" suffix so a
        /// reason held across a long warp reads as historical fact, not a live
        /// claim. Sparse in the codec (omitted when &lt; 0).
        /// </summary>
        public double LastHoldUT = -1.0;

        /// <summary>
        /// Plain-ASCII summary of the most recent PARTIAL delivery
        /// (actual-vs-requested per short item, built by
        /// <c>RouteOrchestrator.BuildPartialDeliverySummary</c>); null when the
        /// most recent delivery was full or none has happened. The
        /// destination-capacity dispatch gate makes partials rare (capacity
        /// that shrinks between the gate and the write), so when one DOES
        /// happen the Logistics window must show exactly what was lost - the
        /// undelivered remainder does not come back (the origin was debited in
        /// full and the transport is a ghost). CLEARED by the next FULL
        /// delivery so stale loss reports never outlive the condition. Sparse
        /// in the codec (omitted when null/empty).
        /// </summary>
        public string LastPartialDeliverySummary;

        /// <summary>
        /// UT of the most recent partial delivery; -1 when
        /// <see cref="LastPartialDeliverySummary"/> is unset. Drives the
        /// display-side "{age} ago" suffix. Sparse in the codec (omitted when
        /// &lt; 0).
        /// </summary>
        public double LastPartialDeliveryUT = -1.0;

        /// <summary>
        /// Cycle id the partial report belongs to; null when unset. A
        /// multi-stop cycle delivers several windows under ONE cycle id, so
        /// the orchestrator APPENDS same-cycle partials into one report and a
        /// full window only clears a report from an EARLIER cycle - without
        /// this key, window B's full delivery would erase window A's recorded
        /// loss inside the same cycle. Sparse in the codec (omitted when
        /// null/empty).
        /// </summary>
        public string LastPartialDeliveryCycleId;

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
        /// (M-MIS-9-R1) Recording ids of EVERY recording in the source tree at
        /// route creation, captured by <c>RouteBuilder.BuildRoute</c> and
        /// persisted by <see cref="RouteCodec"/>. This is the creation-time
        /// known-recording union that scopes the recovery-credit sum
        /// (<c>RouteRunCostCalculator.ResolveTreeRecordingIds(Route)</c>
        /// intersects the CURRENT tree's ids with this set): it contains the
        /// post-undock fly-home-and-recover leg (gotcha G1 stays satisfied)
        /// while post-creation branches (re-fly forks, switch-fly
        /// continuations) mint NEW ids outside it and are excluded from the
        /// per-cycle credit. Empty set = no snapshot (degenerate or pre-field
        /// route); the resolver then FAILS OPEN to the whole current tree,
        /// preserving G1's never-silently-zero contract.
        /// </summary>
        public HashSet<string> CreationTreeRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Runtime-only cache (M-MIS-9; NOT serialized, <see cref="RouteCodec"/>
        /// untouched) of composition-interval keys auto-excluded by
        /// <see cref="RouteBackingMission.BuildMission"/> against post-creation
        /// branches (re-fly fork or switch-fly continuation landing on the
        /// backing tree outside the member path). Two prongs (see
        /// <see cref="RouteBackingMission.ComputeAutoExcludedNewIntervalKeys"/>):
        /// keys whose BASE recording id was not known at creation, plus a
        /// UT-based end-trim re-derived against the CURRENT tree from the
        /// persisted <see cref="RecordedDockUT"/> (immune to positional
        /// <c>/segN</c> key renumbering). Unioned into the synthesized backing
        /// mission's <c>Mission.ExcludedIntervalKeys</c> so the rendered loop and
        /// the delivery span stay frozen to the creation-time member set.
        /// Re-derived from persisted route data (<see cref="SourceRefs"/> +
        /// <see cref="ExcludedIntervalKeys"/> + <see cref="RecordedDockUT"/>) on
        /// the first BuildMission after load. Null until the first derivation.
        /// </summary>
        public HashSet<string> AutoExcludedNewIntervalKeys;

        /// <summary>
        /// Runtime-only cache (M-MIS-9; NOT serialized): topology signature of
        /// the backing tree at the last <see cref="AutoExcludedNewIntervalKeys"/>
        /// derivation: the BranchPoints/Recordings counts (mirroring the
        /// per-tree fold in <c>MissionLoopUnitBuilder.BuildSignature</c>) plus
        /// rolling ordinal hashes of the recording ids and branch-point ids, so
        /// a count-neutral mutation (e.g. a paired discard + re-fly batched into
        /// one observation) still re-derives.
        /// <see cref="RouteBackingMission.BuildMission"/> re-derives only when
        /// this signature changes, so the per-frame BuildMission stays cheap and
        /// an unchanged tree never re-runs the composition walk. Null until the
        /// first derivation.
        /// </summary>
        public string AutoExcludeTopologySignature;

        /// <summary>
        /// Runtime-only cache (M-MIS-11 item 1; NOT serialized, <see cref="RouteCodec"/>
        /// untouched) of the route's built backing-mission loop unit, written by
        /// <c>RouteOrchestrator.ResolveLoopUnit</c>. Null means either "cache not
        /// primed yet" (<see cref="LoopUnitBuilderSignature"/> null) or "the
        /// builder yielded no unit for the current inputs" (signature non-null) -
        /// both re-resolve exactly like the pre-cache code would. Invalidated by
        /// the two signatures below; defaults to null after every load, so the
        /// first post-load resolve is always a fresh build.
        /// </summary>
        public GhostPlaybackLogic.LoopUnit? CachedLoopUnit;

        /// <summary>
        /// Runtime-only cache key (M-MIS-11 item 1; NOT serialized): the
        /// <c>MissionLoopUnitBuilder.BuildSignature</c> of the synthesized
        /// one-element backing-mission list at the last
        /// <see cref="CachedLoopUnit"/> build. Covers every builder input the
        /// render seams' own signature caching covers: the mission fields
        /// (dispatch interval, loop anchor, excluded interval keys INCLUDING the
        /// M-MIS-9 auto-excluded set), the backing tree's branch/recording
        /// counts, the committed-list identity (count + rolling id hash), the
        /// auto-loop-interval setting, the transited-body geometry + station
        /// anchor digests, and the rotation-mode setting. Null until the first
        /// build.
        /// </summary>
        public string LoopUnitBuilderSignature;

        /// <summary>
        /// Runtime-only cache key (M-MIS-11 item 1; NOT serialized): the backing
        /// tree's topology signature (the M-MIS-9
        /// <c>RouteBackingMission.ComputeTopologySignature</c> pattern - counts
        /// PLUS rolling ordinal hashes of recording ids and branch-point ids) at
        /// the last <see cref="CachedLoopUnit"/> build. Paired with
        /// <see cref="LoopUnitBuilderSignature"/> because the builder signature
        /// folds only the tree's COUNTS: a count-neutral tree mutation (paired
        /// discard + re-fly observed in one batch) moves this hash and forces the
        /// rebuild the counts alone would miss. Null until the first build.
        /// </summary>
        public string LoopUnitTopologySignature;

        /// <summary>
        /// Recorded dock UT lifted from the leaf (dock-child) recording's
        /// <c>RouteConnectionWindow.DockUT</c>. The loop clock fires delivery
        /// when it crosses this UT within the backing-mission span each cycle
        /// (Phase 4). Default -1 (unset / no backing mission).
        /// </summary>
        public double RecordedDockUT = -1.0;

        /// <summary>
        /// (M-MIS-5 P2b) Recorded ORIGIN UNDOCK UT lifted from the origin
        /// connection window's <c>UndockUT</c> on a mid-tree docked-origin
        /// (shuttle) route: the rendered span STARTS here instead of the
        /// tree-root launch, and the M-MIS-9 freeze re-derives the start-side
        /// UT trim from this value
        /// (<c>RouteBackingMission.ComputeAutoExcludedNewIntervalKeys</c>
        /// prong 2b). Default -1 (launch-rooted route, no start trim); sparse
        /// in the codec (omitted at default).
        /// </summary>
        public double RecordedOriginUndockUT = -1.0;

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
        /// (M5 D3) Window index adopted on the FIRST owed crossing that ARRIVES
        /// under the <c>RouteWindowBasis.ReaimWindows</c> basis - the offset
        /// anchor of the residual cadence modulo (deliver every Nth window
        /// counted from this). Adoption is on crossing ARRIVAL, not on a
        /// successful fire: the anchor is set even when that crossing then
        /// blocks on eligibility and emits nothing (the plan's literal adoption
        /// rule - the anchor pins the window PHASE, not a delivery).
        /// -1 = unset; when -1 and an owed crossing arrives on a ReaimWindows
        /// unit, the crossing adopts <c>anchor = dockCycleIndex</c> and is
        /// deliverable (the first crossing after creation / activation / rebase
        /// is ALWAYS deliverable). Reset to -1 wherever <see cref="LastObservedLoopCycleIndex"/>
        /// rebases (<c>RouteOrchestrator.TryActivate</c>,
        /// <c>RouteCadence.ApplyMultiplier</c>, the <c>RouteBuilder</c> default)
        /// and on every D6 basis transition. Never consulted for
        /// <c>FlatInterval</c> / <c>ZeroDriftSchedule</c> routes (their residual
        /// is off / 1). Sparse in the codec: omitted at -1 so pre-M5 route nodes
        /// stay byte-identical.
        /// </summary>
        public long WindowAnchorCycleIndex = -1;

        /// <summary>
        /// (M5 D6) Persisted flip-detector marker: true while the route's last
        /// evaluated tick derived the <c>RouteWindowBasis.ReaimWindows</c> basis.
        /// NOT a basis cache (the basis is re-derived from the resolved unit
        /// every tick); only the memory that lets the transition evaluator
        /// detect a build-level engage/decline FLIP across ticks (and across
        /// save/reload) and re-baseline the cycle cursors between the flat and
        /// window index spaces - a stale cursor from one space compared in the
        /// other either mis-fires (decline) or permanently silences the route
        /// (re-engage, review C6). Sparse in the codec: omitted when false so
        /// pre-M5 route nodes stay byte-identical.
        /// </summary>
        public bool ReaimWindowBasisEngaged;

        /// <summary>
        /// Recovery-credit deferral marker (logistics-recovery-credit, design doc
        /// section 5.2): the id of the dispatched cycle whose recovery credit has
        /// not yet been flushed. A Career, KSC-origin dispatch sets this to the
        /// cycle it just dispatched (<c>cycle-K</c>); the NEXT dock crossing flushes
        /// that owed credit (at a strictly later UT) and clears the marker, then sets
        /// its own. Null means no credit is owed (route just activated, or the last
        /// owed credit was already flushed). Persisted sparsely by
        /// <see cref="RouteCodec"/> (omitted when null) so a save / reload between
        /// crossings does not lose or double-emit the owed credit. Default null.
        /// </summary>
        public string PendingRecoveryCreditCycleId;

        /// <summary>
        /// The dispatch UT of the cycle named by
        /// <see cref="PendingRecoveryCreditCycleId"/>, recorded for the audit / log
        /// only (the credit's actual UT is the NEXT crossing's UT, not this).
        /// Persisted sparsely by <see cref="RouteCodec"/> (omitted when -1).
        /// Default -1 (no pending credit).
        /// </summary>
        public double PendingRecoveryCreditDispatchUT = -1.0;

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
        /// Clamps a cadence multiplier to the floor (<c>&gt;= 1</c>). The single
        /// place the <c>N &gt;= 1</c> invariant is enforced; every writer (UI,
        /// <c>RouteBuilder</c>, codec) routes through this so 0 / negative inputs
        /// can never land a sub-floor cadence. Default 1 is the MINIMUM loop time.
        /// </summary>
        internal static int ClampCadenceMultiplier(int n)
        {
            return n < 1 ? 1 : n;
        }

        /// <summary>
        /// Clamps a dispatch priority to the floor (<c>&gt;= 0</c>). The single
        /// place the <c>priority &gt;= 0</c> invariant is enforced; every writer
        /// (UI stepper, codec) routes through this so a negative input can never
        /// land a sub-floor priority. Default 0 is the HIGHEST priority (lower
        /// value dispatches first; see <see cref="DispatchPriority"/>).
        /// </summary>
        internal static int ClampPriority(int p)
        {
            return p < 0 ? 0 : p;
        }

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

        /// <summary>
        /// Records the last hold reason (M6 hold reasons): writes the four
        /// <c>LastHold*</c> fields in one place, keeping the audit-trail
        /// discipline of <see cref="TransitionTo"/>. Logs Verbose ONLY when the
        /// kind or detail changed - a route re-blocking on the same reason every
        /// crossing refreshes the UT silently (no per-crossing log spam).
        /// Persisted across save/load by <see cref="RouteCodec"/>; cleared by
        /// <see cref="ClearHold"/> on an eligible crossing, a legacy dispatch,
        /// or a player Activate.
        /// </summary>
        internal void RecordHold(
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall,
            double ut)
        {
            bool changed = LastHoldKind != kind
                || !string.Equals(LastHoldDetail, detail, StringComparison.Ordinal);
            LastHoldKind = kind;
            LastHoldDetail = detail;
            LastHoldShortfall = shortfall;
            LastHoldUT = ut;
            if (changed)
            {
                ParsekLog.Verbose("Route",
                    $"Route {ShortIdForLog()} hold recorded kind={kind} " +
                    $"detail={detail ?? "<none>"} " +
                    $"shortfall={shortfall.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
            }
        }

        /// <summary>
        /// Clears the last hold reason back to the "no hold recorded" defaults
        /// (None / null / 0 / -1). Logs Verbose only when something was actually
        /// cleared, so the per-crossing clear on a healthy route stays silent.
        /// </summary>
        internal void ClearHold(string reason)
        {
            bool hadHold = LastHoldKind != RouteDispatchEvaluator.EligibilityFailureKind.None
                || LastHoldDetail != null
                || LastHoldShortfall != 0.0
                || LastHoldUT >= 0.0;
            if (!hadHold)
                return;
            LastHoldKind = RouteDispatchEvaluator.EligibilityFailureKind.None;
            LastHoldDetail = null;
            LastHoldShortfall = 0.0;
            LastHoldUT = -1.0;
            ParsekLog.Verbose("Route",
                $"Route {ShortIdForLog()} hold cleared reason={reason ?? "<none>"}");
        }

        private string ShortIdForLog()
        {
            return RouteIds.Short(this.Id);
        }
    }
}
