using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pin the pure dispatch evaluator's decision matrix. Uses a fully-faked
    /// <see cref="IRouteRuntimeEnvironment"/> so the tests stay xUnit-only and
    /// never touch KSP statics.
    /// </summary>
    [Collection("Sequential")]
    public class RouteDispatchEvaluatorTests
    {
        /// <summary>
        /// Hand-rolled fake. Every check returns a settable boolean + canned
        /// out value. <c>KscFundsAvailableCalled</c> records whether the
        /// evaluator actually invoked the funds path (so we can assert the
        /// short-circuit in sandbox / non-KSC cases).
        /// </summary>
        private sealed class FakeRouteRuntimeEnvironment : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool EndpointResolvable { get; set; } = true;
            public string EndpointResolveFailureReason { get; set; } = "pid-miss";
            /// <summary>
            /// Optional per-endpoint resolver. When non-null, takes precedence over
            /// the flat <see cref="EndpointResolvable"/> boolean — lets a test fail
            /// only on specific PIDs (e.g. resolve all stop PIDs but fail on the
            /// origin PID, exercising the origin branch in isolation).
            /// </summary>
            public Func<RouteEndpoint, (bool Ok, string Reason)> EndpointResolver { get; set; }
            public bool OriginHasCargoResult { get; set; } = true;
            public string OriginLackingResource { get; set; } = "LiquidFuel";
            public bool KscFundsAvailableResult { get; set; } = true;
            public double KscFundsShortfall { get; set; } = 0.0;
            public bool DestinationHasCapacityResult { get; set; } = true;
            public string DestinationFullResource { get; set; } = "Ore";
            public bool RouteHasValidSourcesResult { get; set; } = true;

            public bool KscFundsAvailableCalled;

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                if (EndpointResolver != null)
                {
                    var result = EndpointResolver(endpoint);
                    reason = result.Ok ? string.Empty : result.Reason;
                    return result.Ok;
                }
                reason = EndpointResolvable ? string.Empty : EndpointResolveFailureReason;
                return EndpointResolvable;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                // The evaluator tests never read the live Vessel — they only
                // care about the success flag. Mirror TryResolveEndpoint and
                // surface a null vessel reference (instantiating a real Vessel
                // would drag in KSP statics and break the xUnit-only contract).
                vessel = null;
                return TryResolveEndpoint(endpoint, out reason);
            }

            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                lackingResource = OriginHasCargoResult ? string.Empty : OriginLackingResource;
                return OriginHasCargoResult;
            }

            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                KscFundsAvailableCalled = true;
                shortfall = KscFundsAvailableResult ? 0.0 : KscFundsShortfall;
                return KscFundsAvailableResult;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = DestinationHasCapacityResult ? string.Empty : DestinationFullResource;
                return DestinationHasCapacityResult;
            }

            public bool RouteHasValidSourcesInErs(Route route)
            {
                return RouteHasValidSourcesResult;
            }
        }

        /// <summary>
        /// Build a route in the dispatch-due Active state with one stop and a
        /// KSC origin. Tests override single fields to exercise specific branches.
        /// </summary>
        private static Route MakeDueRoute()
        {
            return new Route
            {
                Id = "route-1",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                NextDispatchUT = 100.0,
                TransitDuration = 60.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop { Endpoint = new RouteEndpoint { VesselPersistentId = 42u } },
                },
            };
        }

        // catches: a permanent-block status (Paused) silently dispatching.
        [Fact]
        public void Skip_WhenPaused()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.Paused;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("status-permanent-block", decision.Reason);
        }

        // catches: a MissingSourceRecording route falling through to a wait state.
        [Fact]
        public void Skip_WhenMissingSourceRecording()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.MissingSourceRecording;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("status-permanent-block", decision.Reason);
        }

        // catches: a SourceChanged route dispatching when it should silently skip.
        [Fact]
        public void Skip_WhenSourceChanged()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.SourceChanged;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("status-permanent-block", decision.Reason);
        }

        // catches: in-transit arrival firing before TransitDuration has elapsed.
        [Fact]
        public void Skip_WhenInTransitAndNotArrived()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 100.0;
            route.TransitDuration = 60.0;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 140.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("in-transit-pending", decision.Reason);
        }

        // catches: arrival not detected once TransitDuration has elapsed.
        [Fact]
        public void InTransitComplete_WhenTransitDurationElapsed()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 100.0;
            route.TransitDuration = 60.0;
            route.PendingDeliveryUT = null;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.InTransitComplete, decision.Outcome);
        }

        // catches: a double InTransitComplete emission once PendingDeliveryUT is set.
        [Fact]
        public void InTransitComplete_NotEmittedTwice()
        {
            var route = MakeDueRoute();
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 100.0;
            route.TransitDuration = 60.0;
            route.PendingDeliveryUT = 165.0;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("in-transit-pending", decision.Reason);
        }

        // catches: a retry firing before NextEligibilityCheckUT has elapsed.
        [Fact]
        public void Skip_WhenRateLimited()
        {
            var route = MakeDueRoute();
            route.NextEligibilityCheckUT = 300.0;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("rate-limited", decision.Reason);
        }

        // catches: a dispatch firing before NextDispatchUT.
        [Fact]
        public void Skip_WhenNotDueYet()
        {
            var route = MakeDueRoute();
            route.NextDispatchUT = 500.0;
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("not-due-yet", decision.Reason);
        }

        // catches: a stale-source route silently dispatching after the ERS check is bypassed.
        [Fact]
        public void Skip_WhenSourcesStale()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment
            {
                RouteHasValidSourcesResult = false,
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Contains("sources-stale", decision.Reason);
        }

        // catches: an unresolved stop endpoint dispatching instead of transitioning to EndpointLost.
        [Fact]
        public void EndpointLost_OnStopUnresolved()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment
            {
                EndpointResolvable = false,
                EndpointResolveFailureReason = "pid-miss",
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.EndpointLost, decision.Outcome);
            Assert.Equal(RouteStatus.EndpointLost, decision.NextStatus);
            Assert.Equal(200.0 + RouteOrchestrator.WaitRetryIntervalSec, decision.NewNextEligibilityCheckUT);
            Assert.Contains("stop-0", decision.Reason);
        }

        // catches: a non-KSC origin bypassing the resolver check when it goes missing.
        // The stops loop runs BEFORE the origin check, so a flat
        // EndpointResolvable=false fake would fail at stop-0 first and never
        // reach the origin branch. The custom EndpointResolver here resolves the
        // stop PID (42) but fails the origin PID (99), pinning the origin
        // branch in isolation.
        [Fact]
        public void EndpointLost_OnNonKscOriginUnresolved()
        {
            var route = MakeDueRoute();
            route.IsKscOrigin = false;
            route.Origin = new RouteEndpoint { VesselPersistentId = 99u };
            var env = new FakeRouteRuntimeEnvironment
            {
                EndpointResolver = ep => ep.VesselPersistentId == 99u
                    ? (false, "pid-miss")
                    : (true, string.Empty),
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.EndpointLost, decision.Outcome);
            Assert.Equal(RouteStatus.EndpointLost, decision.NextStatus);
            Assert.Equal(200.0 + RouteOrchestrator.WaitRetryIntervalSec, decision.NewNextEligibilityCheckUT);
            // Production emits "origin-{originReason}" (RouteDispatchEvaluator.cs:83).
            // The previous "stop-0" assertion was the bug: that was a stop failure,
            // not an origin failure.
            Assert.Contains("origin-", decision.Reason);
        }

        // catches: a KSC-origin route still trying to resolve Origin as a live vessel.
        [Fact]
        public void KscOrigin_SkipsOriginResolutionCheck()
        {
            // Custom fake: Stops resolve, but Origin endpoint resolution would fail
            // if the evaluator queried it. The evaluator must skip the Origin call
            // entirely for KSC-origin routes.
            var env = new ResolveOnlyStopsEnvironment();
            var route = MakeDueRoute();
            route.IsKscOrigin = true;
            route.Origin = new RouteEndpoint { VesselPersistentId = 99u };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Dispatch, decision.Outcome);
            Assert.True(env.StopCallCount > 0, "evaluator should still resolve stops");
            Assert.Equal(0, env.OriginCallCount);
        }

        // catches: an empty-origin route dispatching instead of moving to WaitingForResources.
        [Fact]
        public void WaitResources_OnEmptyOrigin()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment
            {
                OriginHasCargoResult = false,
                OriginLackingResource = "LiquidFuel",
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.WaitResources, decision.Outcome);
            Assert.Equal(RouteStatus.WaitingForResources, decision.NextStatus);
            Assert.Equal(200.0 + RouteOrchestrator.WaitRetryIntervalSec, decision.NewNextEligibilityCheckUT);
            Assert.Contains("LiquidFuel", decision.Reason);
        }

        // catches: a Career KSC route dispatching despite insufficient funds.
        [Fact]
        public void WaitFunds_OnlyInCareer()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment
            {
                IsCareer = true,
                KscFundsAvailableResult = false,
                KscFundsShortfall = 12345.0,
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.WaitFunds, decision.Outcome);
            Assert.Equal(RouteStatus.WaitingForFunds, decision.NextStatus);
            Assert.Contains("12345", decision.Reason);
        }

        // catches: a sandbox game still gating dispatch on funds.
        [Fact]
        public void WaitFunds_SkippedInSandbox()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment
            {
                IsCareer = false,
                KscFundsAvailableResult = false, // would fail if it were called
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Dispatch, decision.Outcome);
            Assert.False(env.KscFundsAvailableCalled,
                "sandbox-mode routes must never invoke the funds check");
        }

        // catches: a Career non-KSC route still being charged from KSC funds.
        [Fact]
        public void WaitFunds_SkippedForNonKscOriginEvenInCareer()
        {
            var route = MakeDueRoute();
            route.IsKscOrigin = false;
            // Provide an Origin endpoint so the non-KSC resolver pass succeeds.
            route.Origin = new RouteEndpoint { VesselPersistentId = 7u };
            var env = new FakeRouteRuntimeEnvironment
            {
                IsCareer = true,
                KscFundsAvailableResult = false, // would fail if it were called
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Dispatch, decision.Outcome);
            Assert.False(env.KscFundsAvailableCalled,
                "non-KSC origin must never invoke the KSC funds check");
        }

        // catches: a destination-full outcome reporting Dispatch (or advancing NextDispatchUT semantics).
        [Fact]
        public void WaitDestinationFull_DoesNotAdvanceNextDispatchUT()
        {
            var route = MakeDueRoute();
            double originalNextDispatch = route.NextDispatchUT;
            var env = new FakeRouteRuntimeEnvironment
            {
                DestinationHasCapacityResult = false,
                DestinationFullResource = "Ore",
            };

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.WaitDestinationFull, decision.Outcome);
            Assert.Equal(RouteStatus.DestinationFull, decision.NextStatus);
            Assert.Equal(200.0 + RouteOrchestrator.WaitRetryIntervalSec, decision.NewNextEligibilityCheckUT);
            Assert.Contains("Ore", decision.Reason);
            // The evaluator is pure: confirm it never mutates the input route.
            Assert.Equal(originalNextDispatch, route.NextDispatchUT);
        }

        // catches: a happy-path dispatch failing to produce the canonical Dispatch decision.
        [Fact]
        public void Dispatch_AllConditionsMet()
        {
            var route = MakeDueRoute();
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);

            Assert.Equal(RouteDispatchOutcome.Dispatch, decision.Outcome);
            Assert.Equal(RouteStatus.InTransit, decision.NextStatus);
        }

        // catches: an NRE when a higher-level caller passes a null Route by mistake.
        [Fact]
        public void Decision_NullRoute_IsSafe()
        {
            var env = new FakeRouteRuntimeEnvironment();

            var decision = RouteDispatchEvaluator.EvaluateRoute(null, 0.0, env);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Equal("null-route", decision.Reason);
        }

        // catches: an NRE when an early-init caller passes a null environment.
        [Fact]
        public void Decision_NullEnv_IsSafe()
        {
            var route = MakeDueRoute();

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 0.0, null);

            Assert.Equal(RouteDispatchOutcome.Skip, decision.Outcome);
            Assert.Equal("null-env", decision.Reason);
        }

        /// <summary>
        /// Specialized fake used only by <see cref="KscOrigin_SkipsOriginResolutionCheck"/>.
        /// Resolves any endpoint that's not the per-test "origin" marker — the
        /// origin marker resolves false. Counts both call kinds so the test
        /// can assert the evaluator never asked about the origin.
        /// </summary>
        private sealed class ResolveOnlyStopsEnvironment : IRouteRuntimeEnvironment
        {
            public bool IsCareer => false;
            public int StopCallCount;
            public int OriginCallCount;

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                // KSC-origin test uses PID 99 for the Origin; stops use PID 42.
                if (endpoint.VesselPersistentId == 99u)
                {
                    OriginCallCount++;
                    reason = "would-fail-if-called";
                    return false;
                }
                StopCallCount++;
                reason = string.Empty;
                return true;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null;
                return TryResolveEndpoint(endpoint, out reason);
            }

            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                lackingResource = string.Empty;
                return true;
            }

            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                shortfall = 0.0;
                return true;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = string.Empty;
                return true;
            }

            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // M1 origin-cargo gate: pin the env semantics THROUGH the pure check
        // (RouteOriginCargoCheck.HasRequired), mirroring how the production
        // LiveRouteRuntimeEnvironment.OriginHasCargo routes its non-KSC gate.
        // The flat-boolean fake above pins the evaluator's branch wiring;
        // these pin that the pure check's first-short-resource naming flows
        // through CheckEligibility into the WaitResources reason.
        // ==================================================================

        /// <summary>
        /// Fake env whose <see cref="OriginHasCargo"/> delegates to
        /// <see cref="RouteOriginCargoCheck.HasRequired"/> over a settable
        /// stored-amount map - the same shape the production env uses with
        /// <c>LiveOriginCargoProbe.ProbeResourceStored</c> as the reader.
        /// Everything else passes.
        /// </summary>
        private sealed class PureCheckOriginEnvironment : IRouteRuntimeEnvironment
        {
            public Dictionary<string, double> Stored = new Dictionary<string, double>();
            public bool IsCareer { get; set; }
            public bool KscFundsAvailableCalled;

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                reason = string.Empty;
                return true;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null;
                return TryResolveEndpoint(endpoint, out reason);
            }

            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                return RouteOriginCargoCheck.HasRequired(
                    route.CostManifest,
                    name => Stored.TryGetValue(name, out double v) ? v : 0.0,
                    out lackingResource,
                    out _);
            }

            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                KscFundsAvailableCalled = true;
                shortfall = 0.0;
                return true;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = string.Empty;
                return true;
            }

            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // catches: a short origin not surfacing as OriginLacksCargo /
        // WaitResources with the pure check's deterministic first-short
        // resource as the reason (the UI / log hold reason reads it).
        [Fact]
        public void CheckEligibility_OriginCargoShort_WaitResourcesNamesResource()
        {
            var route = MakeDueRoute();
            route.IsKscOrigin = false;
            route.Origin = new RouteEndpoint { VesselPersistentId = 7u };
            route.CostManifest = new Dictionary<string, double>
            {
                { "Oxidizer", 120.0 },
                { "LiquidFuel", 100.0 },
            };
            var env = new PureCheckOriginEnvironment
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 }, // short
                    { "Oxidizer", 10.0 },   // also short, but LiquidFuel sorts first ordinally
                },
            };

            var elig = RouteDispatchEvaluator.CheckEligibility(route, 200.0, env);
            Assert.False(elig.Eligible);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, elig.Kind);
            Assert.Equal("LiquidFuel", elig.Reason);

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);
            Assert.Equal(RouteDispatchOutcome.WaitResources, decision.Outcome);
            // The WaitResources factory wraps the lacking resource in the
            // established origin-lacks-<resource> reason token.
            Assert.Equal("origin-lacks-LiquidFuel", decision.Reason);
        }

        // catches: a fully-covered origin being held at the cargo gate. The
        // route proceeds past gate 6 to the later gates (for a non-KSC origin
        // the Career funds gate is skipped by design, so "passes" lands on
        // Dispatch with the funds check never consulted).
        [Fact]
        public void CheckEligibility_OriginCargoCovered_PassesToFundsGate()
        {
            var route = MakeDueRoute();
            route.IsKscOrigin = false;
            route.Origin = new RouteEndpoint { VesselPersistentId = 7u };
            route.CostManifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
                { "Oxidizer", 120.0 },
            };
            var env = new PureCheckOriginEnvironment
            {
                IsCareer = true,
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 500.0 },
                },
            };

            var elig = RouteDispatchEvaluator.CheckEligibility(route, 200.0, env);
            Assert.True(elig.Eligible);

            var decision = RouteDispatchEvaluator.EvaluateRoute(route, 200.0, env);
            Assert.Equal(RouteDispatchOutcome.Dispatch, decision.Outcome);
            // Career + NON-KSC: the funds gate is short-circuited (gate 7 only
            // runs for Career AND KSC origin).
            Assert.False(env.KscFundsAvailableCalled);
        }
    }
}
