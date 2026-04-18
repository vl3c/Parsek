using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 4 (Rewind-to-Staging, design §5.1 + §7.1 + §7.2 + §7.19): guards the
    /// multi-controllable classifier surface in <see cref="SegmentBoundaryLogic"/>.
    /// Pure-predicate tests verify the count threshold; the filter test exercises
    /// the <c>isControllable</c> delegate seam so we can assert behavior without
    /// a live <c>FlightGlobals</c> scene.
    /// </summary>
    [Collection("Sequential")]
    public class MultiControllableClassifierTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public MultiControllableClassifierTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
        }

        // --- IsMultiControllableSplit: count threshold ---

        [Fact]
        public void IsMultiControllable_Count0_False()
        {
            Assert.False(SegmentBoundaryLogic.IsMultiControllableSplit(0));
        }

        [Fact]
        public void IsMultiControllable_Count1_False()
        {
            Assert.False(SegmentBoundaryLogic.IsMultiControllableSplit(1));
        }

        [Fact]
        public void IsMultiControllable_Count2_True()
        {
            Assert.True(SegmentBoundaryLogic.IsMultiControllableSplit(2));
        }

        /// <summary>§7.2: 3+ controllable children still qualifies for a RewindPoint.</summary>
        [Fact]
        public void IsMultiControllable_Count3_True()
        {
            Assert.True(SegmentBoundaryLogic.IsMultiControllableSplit(3));
        }

        // --- IdentifyControllableChildren: filter + logging ---

        [Fact]
        public void IdentifyControllableChildren_FiltersUncontrollable()
        {
            // Simulate: pid 100 = controllable, pid 200 = uncontrollable (debris),
            // pid 300 = unresolved (null). Only 100 should appear in the result.
            var postBreak = new List<uint> { 100, 200, 300 };
            var result = SegmentBoundaryLogic.IdentifyControllableChildren(
                originalVesselPid: 42,
                postBreakVesselPids: postBreak,
                isControllable: pid =>
                {
                    if (pid == 100) return true;
                    if (pid == 200) return false;
                    return null; // unresolved
                });

            Assert.Single(result);
            Assert.Equal(100u, result[0]);

            // Log assertion: [Rewind] Controllable split children: [<pids>] (orig=<pid>)
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Controllable split children")
                && l.Contains("100")
                && l.Contains("orig=42")
                && l.Contains("unresolved=1"));
        }

        [Fact]
        public void IdentifyControllableChildren_EmptyList_LogsNoneAndReturnsEmpty()
        {
            var result = SegmentBoundaryLogic.IdentifyControllableChildren(
                originalVesselPid: 42,
                postBreakVesselPids: new List<uint>(),
                isControllable: pid => true);

            Assert.Empty(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("(none)") && l.Contains("orig=42"));
        }

        [Fact]
        public void IdentifyControllableChildren_AllUncontrollable_ReturnsEmpty()
        {
            var postBreak = new List<uint> { 1, 2, 3 };
            var result = SegmentBoundaryLogic.IdentifyControllableChildren(
                originalVesselPid: 0,
                postBreakVesselPids: postBreak,
                isControllable: pid => false);

            Assert.Empty(result);
        }

        [Fact]
        public void IdentifyControllableChildren_AllControllable_PreservesOrder()
        {
            var postBreak = new List<uint> { 7, 3, 9 };
            var result = SegmentBoundaryLogic.IdentifyControllableChildren(
                originalVesselPid: 0,
                postBreakVesselPids: postBreak,
                isControllable: pid => true);

            Assert.Equal(new uint[] { 7, 3, 9 }, result.ToArray());
        }

        [Fact]
        public void IdentifyControllableChildren_NullList_ReturnsEmpty()
        {
            var result = SegmentBoundaryLogic.IdentifyControllableChildren(
                originalVesselPid: 42,
                postBreakVesselPids: null,
                isControllable: pid => true);

            Assert.Empty(result);
        }
    }
}
