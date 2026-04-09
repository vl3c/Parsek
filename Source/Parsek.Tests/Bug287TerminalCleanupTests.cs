using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #287 — spurious terminal EngineShutdown events surviving into committed
    /// recordings and leaving ghost engine FX permanently off after booster staging.
    ///
    /// Root cause: <c>ResumeAfterFalseAlarm</c>'s index-based <c>RemoveRange</c> was brittle
    /// against any code path that reordered or appended PartEvents between
    /// <c>StopRecordingForChainBoundary</c> and the resume call — particularly the five
    /// unstable <c>List&lt;T&gt;.Sort((a,b) =&gt; a.ut.CompareTo(b.ut))</c> call sites in
    /// flush/merge paths. The fix switches to content-based removal: FinalizeRecordingState
    /// now saves the exact terminal events it emitted, and ResumeAfterFalseAlarm removes
    /// them by matching (ut, pid, eventType, moduleIndex).
    ///
    /// Manifests in the 2026-04-09 Kerbal X playtest
    /// (<c>logs/2026-04-09_1117_kerbalx-rover</c>): committed recording
    /// <c>db2e7ab62c6847e0b439237a41172602.prec</c> had terminal EngineShutdown events at
    /// UT 72.40, 87.94, and 104.86 for pids that <em>should</em> have been removed by
    /// ResumeAfterFalseAlarm per the log (<c>removed 5 / 3 / 3 orphaned terminal event(s)</c>)
    /// but survived into the saved .prec file. During playback, pid=2527095907's engine
    /// flame went off at UT 87.94 and never came back because no subsequent EngineIgnited
    /// event followed.
    /// </summary>
    [Collection("Sequential")]
    public class Bug287TerminalCleanupTests
    {
        [Fact]
        public void FindTerminalEventIndex_EmptyList_ReturnsMinusOne()
        {
            var target = new PartEvent
            {
                ut = 10.0,
                partPersistentId = 42,
                eventType = PartEventType.EngineShutdown,
                moduleIndex = 0
            };
            Assert.Equal(-1, FlightRecorder.FindTerminalEventIndex(null, target));
            Assert.Equal(-1, FlightRecorder.FindTerminalEventIndex(new List<PartEvent>(), target));
        }

        [Fact]
        public void FindTerminalEventIndex_MatchByTuple_IgnoresPartName()
        {
            var list = new List<PartEvent>
            {
                new PartEvent { ut = 5.0, partPersistentId = 10, eventType = PartEventType.EngineIgnited, moduleIndex = 0, partName = "liquidEngine2" },
                new PartEvent { ut = 10.0, partPersistentId = 42, eventType = PartEventType.EngineShutdown, moduleIndex = 0, partName = "unknown" },
                new PartEvent { ut = 15.0, partPersistentId = 10, eventType = PartEventType.EngineShutdown, moduleIndex = 0, partName = "liquidEngine2" }
            };
            var target = new PartEvent
            {
                ut = 10.0,
                partPersistentId = 42,
                eventType = PartEventType.EngineShutdown,
                moduleIndex = 0,
                partName = "unknown"
            };
            Assert.Equal(1, FlightRecorder.FindTerminalEventIndex(list, target));
        }

        [Fact]
        public void FindTerminalEventIndex_NoMatch_ReturnsMinusOne()
        {
            var list = new List<PartEvent>
            {
                new PartEvent { ut = 5.0, partPersistentId = 10, eventType = PartEventType.EngineIgnited, moduleIndex = 0 },
                new PartEvent { ut = 10.0, partPersistentId = 42, eventType = PartEventType.Decoupled, moduleIndex = 0 }
            };
            var target = new PartEvent
            {
                ut = 10.0,
                partPersistentId = 42,
                eventType = PartEventType.EngineShutdown, // different type
                moduleIndex = 0
            };
            Assert.Equal(-1, FlightRecorder.FindTerminalEventIndex(list, target));
        }

        [Fact]
        public void FindTerminalEventIndex_MatchesFirstOccurrence()
        {
            var list = new List<PartEvent>
            {
                new PartEvent { ut = 5.0, partPersistentId = 10, eventType = PartEventType.EngineShutdown, moduleIndex = 0 },
                new PartEvent { ut = 5.0, partPersistentId = 10, eventType = PartEventType.EngineShutdown, moduleIndex = 0 } // duplicate
            };
            var target = new PartEvent
            {
                ut = 5.0,
                partPersistentId = 10,
                eventType = PartEventType.EngineShutdown,
                moduleIndex = 0
            };
            Assert.Equal(0, FlightRecorder.FindTerminalEventIndex(list, target));
        }

        /// <summary>
        /// Simulates the 2026-04-09 Kerbal X false-alarm path: the recorder has 2 decouples
        /// at UT 87.94, FinalizeRecordingState appends 3 terminal EngineShutdowns, then
        /// ResumeAfterFalseAlarm should remove exactly those 3 terminals by content match,
        /// leaving the 2 decouples intact.
        /// </summary>
        [Fact]
        public void ResumeAfterFalseAlarm_ContentBasedRemoval_PreservesDecouplesAndRemovesTerminals()
        {
            // Trigger TestSinkForTesting so the sink swallows Debug.Log calls in a non-Unity
            // test context (mirrors RewindLoggingTests pattern).
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var rec = new FlightRecorder();

                // 2 real decouples at UT 87.94 (from OnPartJointBreak)
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 87.94,
                    partPersistentId = 3027027466,
                    eventType = PartEventType.Decoupled,
                    partName = "radialDecoupler1-2"
                });
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 87.94,
                    partPersistentId = 2130796824,
                    eventType = PartEventType.Decoupled,
                    partName = "radialDecoupler1-2"
                });

                // Simulate FinalizeRecordingState appending 3 terminal engine shutdowns by
                // calling EmitTerminalEngineAndRcsEvents directly, appending to PartEvents,
                // and stashing the reference in lastEmittedTerminalEvents (internal field
                // — InternalsVisibleTo(Parsek.Tests) grants direct access, no reflection).
                var activeEngines = new HashSet<ulong>
                {
                    FlightRecorder.EncodeEngineKey(2485666303, 0),
                    FlightRecorder.EncodeEngineKey(2527095907, 0),
                    FlightRecorder.EncodeEngineKey(1282749119, 0)
                };
                var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                    activeEngines, null, null, null, 87.94, "Recorder");
                rec.PartEvents.AddRange(terminals);
                rec.lastEmittedTerminalEvents = terminals;

                Assert.Equal(5, rec.PartEvents.Count); // 2 decouples + 3 terminals

                int removed = rec.RemoveLastEmittedTerminals();
                Assert.Equal(3, removed);

                // Only the 2 real decouples remain
                Assert.Equal(2, rec.PartEvents.Count);
                Assert.All(rec.PartEvents, e => Assert.Equal(PartEventType.Decoupled, e.eventType));
                Assert.Contains(rec.PartEvents, e => e.partPersistentId == 3027027466);
                Assert.Contains(rec.PartEvents, e => e.partPersistentId == 2130796824);

                // No surviving terminal engine shutdowns (the #287 smoking gun)
                Assert.DoesNotContain(rec.PartEvents, e =>
                    e.eventType == PartEventType.EngineShutdown && e.partName == "unknown");
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        /// <summary>
        /// Critical regression guard: even if an unstable sort reorders the PartEvents list
        /// so the terminal shutdowns land in arbitrary positions, content-based removal
        /// still finds and removes exactly the right events.
        /// </summary>
        [Fact]
        public void RemoveLastEmittedTerminals_SurvivesUnstableSortReordering()
        {
            var logLines = new List<string>();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                var rec = new FlightRecorder();

                // Pre-terminal: 2 decouples at UT 26.08
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 26.08,
                    partPersistentId = 2057942744,
                    eventType = PartEventType.Decoupled,
                    partName = "radialDecoupler1-2"
                });
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 26.08,
                    partPersistentId = 1009856088,
                    eventType = PartEventType.Decoupled,
                    partName = "radialDecoupler1-2"
                });

                // Simulate FinalizeRecordingState adding 5 terminals at UT 26.08
                var activeEngines = new HashSet<ulong>();
                for (uint i = 0; i < 5; i++)
                    activeEngines.Add(FlightRecorder.EncodeEngineKey(2000 + i, 0));
                var terminals = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                    activeEngines, null, null, null, 26.08, "Recorder");
                rec.PartEvents.AddRange(terminals);

                // Direct access via internal (InternalsVisibleTo(Parsek.Tests))
                rec.lastEmittedTerminalEvents = terminals;

                // SIMULATE UNSTABLE SORT: shuffle the PartEvents list into an adversarial order
                // where a terminal is BEFORE a decouple (as the 2026-04-09 Kerbal X playtest
                // recording showed for UT 72.40).
                var shuffled = new List<PartEvent>
                {
                    rec.PartEvents[3], // terminal
                    rec.PartEvents[0], // decouple
                    rec.PartEvents[4], // terminal
                    rec.PartEvents[1], // decouple
                    rec.PartEvents[5], // terminal
                    rec.PartEvents[2], // terminal (was terminals[0])
                    rec.PartEvents[6]  // terminal
                };
                rec.PartEvents.Clear();
                rec.PartEvents.AddRange(shuffled);

                int removed = rec.RemoveLastEmittedTerminals();
                Assert.Equal(5, removed);

                // Only the 2 real decouples remain, in their original identity
                Assert.Equal(2, rec.PartEvents.Count);
                Assert.Contains(rec.PartEvents, e =>
                    e.eventType == PartEventType.Decoupled && e.partPersistentId == 2057942744);
                Assert.Contains(rec.PartEvents, e =>
                    e.eventType == PartEventType.Decoupled && e.partPersistentId == 1009856088);
                Assert.DoesNotContain(rec.PartEvents, e =>
                    e.eventType == PartEventType.EngineShutdown);
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        /// <summary>
        /// Regression guard for the root-cause scenario from 2026-04-09 Kerbal X
        /// playtest (<c>logs/2026-04-09_1117_kerbalx-rover</c>): two sources of PartEvents
        /// merged into one recording — a tree root containing terminal Shutdowns at UT 72.94
        /// plus a continuing recording's seed EngineIgnited events at the same UT. After a
        /// stable sort, playback iterating in order sees Shutdown-then-Ignited and leaves
        /// engines ON. If an unstable sort scrambles them, playback can see
        /// Ignited-then-Shutdown and leave engines OFF.
        /// </summary>
        [Fact]
        public void StableSort_TreeMergeAtSameUT_KeepsShutdownsBeforeIgnitedSeeds()
        {
            var treeRootEvents = new List<PartEvent>
            {
                // 5 terminal shutdowns from tree promotion at UT 72.94
                new PartEvent { ut = 72.94, partPersistentId = 2485666303, eventType = PartEventType.EngineShutdown, partName = "unknown", moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 2527095907, eventType = PartEventType.EngineShutdown, partName = "unknown", moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 1282749119, eventType = PartEventType.EngineShutdown, partName = "unknown", moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 1782153286, eventType = PartEventType.EngineShutdown, partName = "unknown", moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 3609199354, eventType = PartEventType.EngineShutdown, partName = "unknown", moduleIndex = 0 }
            };
            var continuingSeedEvents = new List<PartEvent>
            {
                // 5 seed EngineIgnited events at the same UT
                new PartEvent { ut = 72.94, partPersistentId = 2485666303, eventType = PartEventType.EngineIgnited, partName = "liquidEngineMainsail.v2", value = 1.0f, moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 2527095907, eventType = PartEventType.EngineIgnited, partName = "liquidEngine2", value = 1.0f, moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 1282749119, eventType = PartEventType.EngineIgnited, partName = "liquidEngine2", value = 1.0f, moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 1782153286, eventType = PartEventType.EngineIgnited, partName = "liquidEngine2", value = 1.0f, moduleIndex = 0 },
                new PartEvent { ut = 72.94, partPersistentId = 3609199354, eventType = PartEventType.EngineIgnited, partName = "liquidEngine2", value = 1.0f, moduleIndex = 0 }
            };

            // Merge: tree root events first (as they're appended first at flush time), then seeds
            var merged = new List<PartEvent>();
            merged.AddRange(treeRootEvents);
            merged.AddRange(continuingSeedEvents);

            var sorted = FlightRecorder.StableSortPartEventsByUT(merged);

            Assert.Equal(10, sorted.Count);

            // All 5 Shutdowns must come before all 5 Ignited events so playback leaves
            // engines ON at the end of this UT batch.
            int lastShutdownIdx = -1;
            int firstIgnitedIdx = -1;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].eventType == PartEventType.EngineShutdown) lastShutdownIdx = i;
                if (firstIgnitedIdx < 0 && sorted[i].eventType == PartEventType.EngineIgnited) firstIgnitedIdx = i;
            }

            Assert.True(lastShutdownIdx >= 0, "Expected at least one Shutdown in sorted output");
            Assert.True(firstIgnitedIdx >= 0, "Expected at least one Ignited in sorted output");
            Assert.True(lastShutdownIdx < firstIgnitedIdx,
                $"Stable sort violated: last Shutdown at {lastShutdownIdx} came AFTER first Ignited at {firstIgnitedIdx}. " +
                "This would cause engine FX to turn off permanently in playback after staging (bug #287).");
        }

        [Fact]
        public void StableSortPartEventsByUT_Static_NullInput_ReturnsEmptyList()
        {
            // The static helper must return a NEW list (never the same reference as input)
            // so callers can safely Clear the source before copying back. Null input
            // returns an empty list rather than null to keep caller code branch-free.
            var result = FlightRecorder.StableSortPartEventsByUT(null);
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void StableSortPartEventsByUT_Static_SingleElement_ReturnsNewList()
        {
            var input = new List<PartEvent>
            {
                new PartEvent { ut = 5.0, partPersistentId = 1 }
            };
            var result = FlightRecorder.StableSortPartEventsByUT(input);
            Assert.Single(result);
            // Must be a separate list so caller can Clear input without losing result
            Assert.NotSame(input, result);
        }

        [Fact]
        public void StableSortPartEventsByUT_Static_ClearThenCopyBack_WorksWithSmallLists()
        {
            // Regression guard for the aliasing bug caught by AppendCapturedDataTests
            // on the first fix iteration: if the helper returned the same reference for
            // small lists, callers doing
            //   var sorted = StableSortPartEventsByUT(list);
            //   list.Clear();
            //   list.AddRange(sorted);
            // would silently lose all data.
            var list = new List<PartEvent>
            {
                new PartEvent { ut = 10.0, partPersistentId = 1, partName = "original" }
            };
            var sorted = FlightRecorder.StableSortPartEventsByUT(list);
            list.Clear();
            list.AddRange(sorted);

            Assert.Single(list);
            Assert.Equal(1u, list[0].partPersistentId);
            Assert.Equal("original", list[0].partName);
        }
    }
}
