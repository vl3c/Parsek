using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Fluent builder for <see cref="Route"/> test fixtures. Mirrors the
    /// pattern in <c>RecordingBuilder</c>: every <c>WithXxx</c> call returns
    /// the builder so tests can compose routes inline. <see cref="Build"/>
    /// emits a route with sensible defaults so tests only need to specify
    /// the fields they care about.
    /// </summary>
    internal class RouteBuilder
    {
        private string id = "route-test-id";
        private string name = "Test Route";
        private RouteStatus status = RouteStatus.Active;
        private bool isKscOrigin;
        private double kscDispatchFundsCost;
        private double transitDuration;
        private double dispatchInterval;
        private double dispatchWindowEpochUT;
        private double dispatchWindowPeriod;
        private double nextDispatchUT;
        private double? currentCycleStartUT;
        private double? nextEligibilityCheckUT;
        private double? pendingDeliveryUT;
        private int currentSegmentIndex = -1;
        private int pendingStopIndex = -1;
        private string linkedRouteId;
        private bool pauseAfterCurrentCycle;
        private int completedCycles;
        private int skippedCycles;
        private RouteEndpoint origin;
        private bool originSet;
        private readonly List<string> recordingIds = new List<string>();
        private readonly List<RouteSourceRef> sourceRefs = new List<RouteSourceRef>();
        private readonly List<RouteStop> stops = new List<RouteStop>();
        private Dictionary<string, double> costManifest;
        private List<InventoryPayloadItem> inventoryCostManifest;

        public RouteBuilder WithId(string newId)
        {
            id = newId;
            return this;
        }

        public RouteBuilder WithName(string newName)
        {
            name = newName;
            return this;
        }

        public RouteBuilder WithStatus(RouteStatus newStatus)
        {
            status = newStatus;
            return this;
        }

        public RouteBuilder WithKscOrigin(bool kscOrigin, float cost = 0f)
        {
            isKscOrigin = kscOrigin;
            kscDispatchFundsCost = cost;
            return this;
        }

        public RouteBuilder WithSchedule(double transitDurationSeconds, double dispatchIntervalSeconds)
        {
            transitDuration = transitDurationSeconds;
            dispatchInterval = dispatchIntervalSeconds;
            return this;
        }

        public RouteBuilder WithDispatchWindow(double epochUT, double period, double nextDispatch)
        {
            dispatchWindowEpochUT = epochUT;
            dispatchWindowPeriod = period;
            nextDispatchUT = nextDispatch;
            return this;
        }

        public RouteBuilder WithCurrentCycleStartUT(double? ut)
        {
            currentCycleStartUT = ut;
            return this;
        }

        public RouteBuilder WithNextEligibilityCheckUT(double? ut)
        {
            nextEligibilityCheckUT = ut;
            return this;
        }

        public RouteBuilder WithPendingDeliveryUT(double? ut, int stopIndex = -1)
        {
            pendingDeliveryUT = ut;
            pendingStopIndex = stopIndex;
            return this;
        }

        public RouteBuilder WithCurrentSegmentIndex(int index)
        {
            currentSegmentIndex = index;
            return this;
        }

        public RouteBuilder WithLinkedRouteId(string linkedId)
        {
            linkedRouteId = linkedId;
            return this;
        }

        public RouteBuilder WithCycleCounters(int completed, int skipped, bool pauseAfter = false)
        {
            completedCycles = completed;
            skippedCycles = skipped;
            pauseAfterCurrentCycle = pauseAfter;
            return this;
        }

        public RouteBuilder WithOrigin(RouteEndpoint endpoint)
        {
            origin = endpoint;
            originSet = true;
            return this;
        }

        public RouteBuilder WithRecordingId(string recordingId)
        {
            recordingIds.Add(recordingId);
            return this;
        }

        public RouteBuilder WithSourceRef(RouteSourceRef sourceRef)
        {
            sourceRefs.Add(sourceRef);
            return this;
        }

        public RouteBuilder WithStop(RouteStop stop)
        {
            stops.Add(stop);
            return this;
        }

        public RouteBuilder WithCostManifest(Dictionary<string, double> manifest)
        {
            costManifest = manifest;
            return this;
        }

        public RouteBuilder WithInventoryCostManifest(List<InventoryPayloadItem> manifest)
        {
            inventoryCostManifest = manifest;
            return this;
        }

        public Route Build()
        {
            var route = new Route
            {
                Id = id,
                Name = name,
                Status = status,
                IsKscOrigin = isKscOrigin,
                KscDispatchFundsCost = kscDispatchFundsCost,
                TransitDuration = transitDuration,
                DispatchInterval = dispatchInterval,
                DispatchWindowEpochUT = dispatchWindowEpochUT,
                DispatchWindowPeriod = dispatchWindowPeriod,
                NextDispatchUT = nextDispatchUT,
                CurrentCycleStartUT = currentCycleStartUT,
                NextEligibilityCheckUT = nextEligibilityCheckUT,
                PendingDeliveryUT = pendingDeliveryUT,
                CurrentSegmentIndex = currentSegmentIndex,
                PendingStopIndex = pendingStopIndex,
                LinkedRouteId = linkedRouteId,
                PauseAfterCurrentCycle = pauseAfterCurrentCycle,
                CompletedCycles = completedCycles,
                SkippedCycles = skippedCycles,
                CostManifest = costManifest,
                InventoryCostManifest = inventoryCostManifest
            };

            if (originSet)
                route.Origin = origin;

            for (int i = 0; i < recordingIds.Count; i++)
                route.RecordingIds.Add(recordingIds[i]);
            for (int i = 0; i < sourceRefs.Count; i++)
                route.SourceRefs.Add(sourceRefs[i]);
            for (int i = 0; i < stops.Count; i++)
                route.Stops.Add(stops[i]);

            return route;
        }
    }
}
