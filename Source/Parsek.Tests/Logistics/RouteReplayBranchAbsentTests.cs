using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 5 (Checkpoint C) discard regression guard. The separate
    /// <c>logistics-route-replay</c> branch (<c>OffsetReplayUnit</c> /
    /// <c>RouteReplayPlanner</c>) is DISCARDED: the route's visual is now a looped
    /// Mission segment via <c>MissionLoopUnitBuilder</c>, not a bespoke offset
    /// replay (design §0.4 / §0.7). The plan re-confirmed ZERO references in this
    /// line's <c>Source/</c>; this reflection scan fails loudly if any such type
    /// ever reappears in the production assembly (a merge from the discarded branch
    /// or a hand-rolled re-introduction would resurrect the dead replay path that
    /// the Missions foundation supersedes).
    /// </summary>
    public class RouteReplayBranchAbsentTests
    {
        // Type-name fragments that would only appear if the discarded replay branch
        // were reintroduced. Matched case-insensitively against the simple name of
        // every exported AND non-exported (internal) type in the Parsek assembly.
        private static readonly string[] ForbiddenTypeNameFragments =
        {
            "OffsetReplayUnit",
            "OffsetReplay",
            "RouteReplayPlanner",
            "RouteReplay",
        };

        private static Assembly ProductionAssembly => typeof(Route).Assembly;

        private static Type[] AllTypes()
        {
            // GetTypes() over a single in-house assembly never throws
            // ReflectionTypeLoadException in practice, but guard so a transient
            // load issue surfaces as the actual loader error, not a null deref.
            try
            {
                return ProductionAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
        }

        [Fact]
        public void DiscardedReplayBranchTypes_AreAbsentFromProductionAssembly()
        {
            // catches: a re-merge of the discarded logistics-route-replay branch, or
            // a hand-rolled OffsetReplayUnit / RouteReplayPlanner re-introduction.
            // The Missions-foundation render path (MissionLoopUnitBuilder + the
            // two-cadence LoopUnit) fully supersedes it; a resurrected replay type
            // would be dead, duplicative, and a silent second render path.
            var offenders = new List<string>();
            foreach (Type t in AllTypes())
            {
                string name = t.Name ?? string.Empty;
                for (int i = 0; i < ForbiddenTypeNameFragments.Length; i++)
                {
                    if (name.IndexOf(ForbiddenTypeNameFragments[i],
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        offenders.Add(t.FullName ?? name);
                        break;
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "Discarded route-replay branch type(s) reappeared in the production assembly: "
                + string.Join(", ", offenders)
                + ". The route render path is MissionLoopUnitBuilder + LoopUnit (design §0.4/§0.7); "
                + "the OffsetReplay / RouteReplayPlanner branch is discarded and must stay absent.");
        }

        [Fact]
        public void MissionLoopFoundation_RenderTypesArePresent()
        {
            // The positive counterpart: the render path the discard depends on must
            // exist (so this guard cannot pass vacuously if the foundation itself is
            // gutted). MissionLoopUnitBuilder is the one render seam routes consume.
            Type missionLoopBuilder = ProductionAssembly.GetType("Parsek.MissionLoopUnitBuilder");
            Assert.NotNull(missionLoopBuilder);

            Type routeGhostSelector =
                ProductionAssembly.GetType("Parsek.Logistics.RouteGhostDriverSelector");
            Assert.NotNull(routeGhostSelector);
        }
    }
}
