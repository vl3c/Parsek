using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the ghost exclusion around stock's save-time vessel budget
    /// (KSP-SETTINGS-AUDIT-2026-09-26, S5): ghost map vessels must not count against
    /// <c>GameSettings.MAX_VESSELS_BUDGET</c>, and the raised value must never outlive the
    /// single <c>FlightState()</c> build.
    /// </summary>
    [Collection("Sequential")]
    public class FlightStateGhostBudgetPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly int savedBudget;

        public FlightStateGhostBudgetPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            savedBudget = GameSettings.MAX_VESSELS_BUDGET;
        }

        public void Dispose()
        {
            GameSettings.MAX_VESSELS_BUDGET = savedBudget;
            FlightStateGhostBudgetPatch.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Theory]
        [InlineData(250, 0, false)]
        [InlineData(250, 3, true)]
        [InlineData(-1, 3, false)]   // no budget: stock never prunes, nothing to exclude
        [InlineData(0, 1, true)]     // the dropdown offers 0; ghosts still must not count
        [InlineData(25, -2, false)]
        public void ShouldAdjust_BudgetInForceAndGhostsPresent(int budget, int ghosts, bool expected)
        {
            Assert.Equal(expected, FlightStateGhostBudgetPatch.ShouldAdjust(budget, ghosts));
        }

        [Theory]
        [InlineData(250, 0, 250)]
        [InlineData(250, 150, 400)]
        [InlineData(-1, 150, -1)]
        [InlineData(0, 7, 7)]
        [InlineData(int.MaxValue - 1, 5, int.MaxValue)]  // saturates, never wraps negative
        public void ComputeAdjustedBudget_AddsOneSlotPerGhost(int budget, int ghosts, int expected)
        {
            Assert.Equal(expected, FlightStateGhostBudgetPatch.ComputeAdjustedBudget(budget, ghosts));
        }

        // catches: counting a dead ghost (stock skips DEAD vessels before the budget walk,
        // so counting it would keep one real debris too many) or a non-ghost vessel.
        [Fact]
        public void CountGhostVessels_CountsOnlyLiveGhostPids()
        {
            var ghosts = new HashSet<uint> { 10u, 11u, 12u };
            var vessels = new List<KeyValuePair<uint, bool>>
            {
                new KeyValuePair<uint, bool>(10u, false),
                new KeyValuePair<uint, bool>(11u, true),   // dead ghost: stock skips it
                new KeyValuePair<uint, bool>(12u, false),
                new KeyValuePair<uint, bool>(20u, false),  // real vessel
                new KeyValuePair<uint, bool>(21u, true),   // dead real vessel
            };

            Assert.Equal(2, FlightStateGhostBudgetPatch.CountGhostVessels(vessels, ghosts.Contains));
            Assert.Equal(0, FlightStateGhostBudgetPatch.CountGhostVessels(null, ghosts.Contains));
            Assert.Equal(0, FlightStateGhostBudgetPatch.CountGhostVessels(vessels, null));
        }

        [Fact]
        public void ApplyThenRestore_RaisesForTheBuildAndRestoresTheCapturedValue()
        {
            GameSettings.MAX_VESSELS_BUDGET = 250;

            var adjustment = FlightStateGhostBudgetPatch.Apply(ghostCount: 12);

            Assert.True(adjustment.Applied);
            Assert.Equal(250, adjustment.OriginalBudget);
            Assert.Equal(262, adjustment.AdjustedBudget);
            Assert.Equal(262, GameSettings.MAX_VESSELS_BUDGET);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][GhostMap]")
                && l.Contains("excluded 12 ghost map vessel(s)")
                && l.Contains("MAX_VESSELS_BUDGET 250 -> 262"));

            FlightStateGhostBudgetPatch.Restore(adjustment);

            Assert.Equal(250, GameSettings.MAX_VESSELS_BUDGET);
        }

        [Theory]
        [InlineData(-1, 12)]
        [InlineData(250, 0)]
        public void Apply_NoAdjustment_LeavesBudgetAndLogsNothing(int budget, int ghosts)
        {
            GameSettings.MAX_VESSELS_BUDGET = budget;

            var adjustment = FlightStateGhostBudgetPatch.Apply(ghosts);
            FlightStateGhostBudgetPatch.Restore(adjustment);

            Assert.False(adjustment.Applied);
            Assert.Equal(budget, GameSettings.MAX_VESSELS_BUDGET);
            Assert.DoesNotContain(logLines, l => l.Contains("FlightState vessel budget"));
        }

        // catches: a restore that runs for a build which never raised the budget and so
        // clobbers a value the player changed meanwhile.
        [Fact]
        public void Restore_NotApplied_DoesNotWrite()
        {
            GameSettings.MAX_VESSELS_BUDGET = 100;
            FlightStateGhostBudgetPatch.Restore(new FlightStateGhostBudgetPatch.BudgetAdjustment
            {
                Applied = false,
                OriginalBudget = 250
            });
            Assert.Equal(100, GameSettings.MAX_VESSELS_BUDGET);
        }

        // Drives the real Harmony entry points in the order Harmony calls them (prefix, the
        // constructor body throwing, finalizer) and proves the global setting is restored on
        // the exception path.
        [Fact]
        public void PrefixThenFinalizer_ConstructorThrows_BudgetRestored()
        {
            GameSettings.MAX_VESSELS_BUDGET = 250;
            FlightStateGhostBudgetPatch.GhostCountOverrideForTesting = () => 40;
            Type patch = typeof(FlightStateGhostBudgetPatch);
            MethodInfo prefix = patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo finalizer = patch.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.NotNull(finalizer);

            object[] prefixArgs = { null };
            prefix.Invoke(null, prefixArgs);
            Assert.Equal(290, GameSettings.MAX_VESSELS_BUDGET);

            try
            {
                throw new InvalidOperationException("stand-in for a throwing FlightState()");
            }
            catch (InvalidOperationException)
            {
                finalizer.Invoke(null, new[] { prefixArgs[0] });
            }

            Assert.Equal(250, GameSettings.MAX_VESSELS_BUDGET);
        }

        // catches: a prefix whose ghost count throws leaving the save blocked or the budget
        // changed; the prefix must swallow, Warn, and leave stock's value alone.
        [Fact]
        public void Prefix_GhostCountThrows_WarnsAndLeavesBudget()
        {
            GameSettings.MAX_VESSELS_BUDGET = 250;
            FlightStateGhostBudgetPatch.GhostCountOverrideForTesting =
                () => throw new InvalidOperationException("boom");
            MethodInfo prefix = typeof(FlightStateGhostBudgetPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo finalizer = typeof(FlightStateGhostBudgetPatch).GetMethod(
                "Finalizer", BindingFlags.Static | BindingFlags.NonPublic);

            object[] prefixArgs = { null };
            prefix.Invoke(null, prefixArgs);
            finalizer.Invoke(null, new[] { prefixArgs[0] });

            Assert.Equal(250, GameSettings.MAX_VESSELS_BUDGET);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][GhostMap]") && l.Contains("ghost exclusion failed"));
        }

        // catches: the patch binding to the (ConfigNode, Game) load constructor, which reads a
        // saved state and never applies the budget, instead of the parameterless build.
        [Fact]
        public void HarmonyTarget_IsTheParameterlessFlightStateConstructor()
        {
            var attr = typeof(FlightStateGhostBudgetPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                .Cast<HarmonyPatch>()
                .Single();
            Assert.Equal(typeof(FlightState), attr.info.declaringType);
            Assert.Equal(MethodType.Constructor, attr.info.methodType);
            Assert.NotNull(attr.info.argumentTypes);
            Assert.Empty(attr.info.argumentTypes);
            Assert.NotNull(AccessTools.DeclaredConstructor(typeof(FlightState), Type.EmptyTypes));

            MethodInfo finalizer = typeof(FlightStateGhostBudgetPatch).GetMethod(
                "Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(finalizer);
            Assert.Equal("__state", finalizer.GetParameters().Single().Name);
        }
    }
}
