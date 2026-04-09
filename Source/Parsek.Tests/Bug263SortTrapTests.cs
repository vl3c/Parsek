using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the bug #263 root cause: FinalizeRecordingState's PartEvents.Sort
    /// used to be an unstable sort (List&lt;T&gt;.Sort). When decouple events shared
    /// a physics-frame UT with terminal engine-shutdown events emitted during
    /// FinalizeRecordingState, the sort could shuffle a decouple into the
    /// "terminal events" range, and ResumeAfterFalseAlarm's index-based
    /// <c>PartEvents.RemoveRange(partEventCountBeforeChainStop, N)</c> would
    /// then wrongly drop the decouple along with the terminals.
    ///
    /// The fix (FlightRecorder.StableSortPartEventsByUT): use LINQ OrderBy which
    /// is documented as a stable sort — equal-UT events keep their insertion
    /// order, so decouples written by OnPartJointBreak stay BEFORE terminals
    /// appended later in FinalizeRecordingState, and the index-based removal
    /// only hits real terminals.
    ///
    /// Bug #263 itself (ghost boosters still attached) already has a fallback
    /// safety net via <see cref="FlightRecorder.RecordFallbackDecoupleEvent"/>
    /// — this test file covers the deeper root-cause fix.
    /// </summary>
    [Collection("Sequential")]
    public class Bug263SortTrapTests
    {
        [Fact]
        public void StableSortPartEventsByUT_EmptyList_IsNoOp()
        {
            var rec = new FlightRecorder();
            rec.StableSortPartEventsByUT();
            Assert.Empty(rec.PartEvents);
        }

        [Fact]
        public void StableSortPartEventsByUT_SingleEvent_IsNoOp()
        {
            var rec = new FlightRecorder();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 42.0,
                partPersistentId = 1,
                eventType = PartEventType.Decoupled,
                partName = "foo"
            });

            rec.StableSortPartEventsByUT();

            Assert.Single(rec.PartEvents);
            Assert.Equal(1u, rec.PartEvents[0].partPersistentId);
        }

        [Fact]
        public void StableSortPartEventsByUT_OrdersByUT_Ascending()
        {
            var rec = new FlightRecorder();
            rec.PartEvents.Add(new PartEvent { ut = 30, partPersistentId = 3 });
            rec.PartEvents.Add(new PartEvent { ut = 10, partPersistentId = 1 });
            rec.PartEvents.Add(new PartEvent { ut = 20, partPersistentId = 2 });

            rec.StableSortPartEventsByUT();

            Assert.Equal(3, rec.PartEvents.Count);
            Assert.Equal(10.0, rec.PartEvents[0].ut);
            Assert.Equal(20.0, rec.PartEvents[1].ut);
            Assert.Equal(30.0, rec.PartEvents[2].ut);
        }

        /// <summary>
        /// THE critical test for the bug #263 root cause. Two decouples and two
        /// terminal engine shutdowns share UT 26.08 — the exact scenario from the
        /// 2026-04-09 Kerbal X playtest. The decouples were added first
        /// (OnPartJointBreak during the physics frame); the terminals were added
        /// later (FinalizeRecordingState appending). A stable sort MUST keep
        /// the decouples BEFORE the terminals so that
        /// <c>PartEvents.RemoveRange(partEventCountBeforeChainStop, terminalCount)</c>
        /// in ResumeAfterFalseAlarm only touches the terminals.
        /// </summary>
        [Fact]
        public void StableSortPartEventsByUT_SameUTEvents_PreserveInsertionOrder()
        {
            var rec = new FlightRecorder();
            // Insertion order mirrors the real recorder: two decouples added
            // during the physics frame, then two terminal shutdowns appended by
            // FinalizeRecordingState at the same UT.
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 2057942744,
                eventType = PartEventType.Decoupled,
                partName = "radialDecoupler1-2 (core)"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 1009856088,
                eventType = PartEventType.Decoupled,
                partName = "radialDecoupler1-2 (booster)"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 2485666303,
                eventType = PartEventType.EngineShutdown,
                partName = "liquidEngineMainsail.v2"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 2527095907,
                eventType = PartEventType.EngineShutdown,
                partName = "liquidEngine2"
            });

            rec.StableSortPartEventsByUT();

            // Both decouples MUST remain at indices 0 and 1 (insertion order) so
            // ResumeAfterFalseAlarm's RemoveRange(2, 2) only touches the terminals.
            Assert.Equal(4, rec.PartEvents.Count);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[0].eventType);
            Assert.Equal(2057942744u, rec.PartEvents[0].partPersistentId);
            Assert.Equal(PartEventType.Decoupled, rec.PartEvents[1].eventType);
            Assert.Equal(1009856088u, rec.PartEvents[1].partPersistentId);
            Assert.Equal(PartEventType.EngineShutdown, rec.PartEvents[2].eventType);
            Assert.Equal(PartEventType.EngineShutdown, rec.PartEvents[3].eventType);
        }

        /// <summary>
        /// End-to-end simulation of the bug #263 index-based removal scenario:
        /// we build PartEvents the way a real recorder would, compute
        /// partEventCountBeforeChainStop, append the terminal events, invoke the
        /// same stable sort the recorder uses, and then do the
        /// <c>RemoveRange(partEventCountBeforeChainStop, terminalCount)</c> call
        /// from <c>ResumeAfterFalseAlarm</c>. The decouples must survive.
        /// </summary>
        [Fact]
        public void ResumeAfterFalseAlarm_SameUTDecouples_SurviveRemoveRange()
        {
            var rec = new FlightRecorder();

            // Pre-terminal: some seed events at UT 12 + 2 decouples at UT 26.08
            for (int i = 0; i < 5; i++)
            {
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 12.0,
                    partPersistentId = (uint)(1000 + i),
                    eventType = PartEventType.EngineIgnited,
                    partName = "seed engine"
                });
            }
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 2057942744,
                eventType = PartEventType.Decoupled,
                partName = "radialDecoupler1-2 (core)"
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 26.08,
                partPersistentId = 1009856088,
                eventType = PartEventType.Decoupled,
                partName = "radialDecoupler1-2 (booster)"
            });

            int partEventCountBeforeChainStop = rec.PartEvents.Count; // 7

            // FinalizeRecordingState appends 5 terminal engine shutdowns at the same UT
            for (int i = 0; i < 5; i++)
            {
                rec.PartEvents.Add(new PartEvent
                {
                    ut = 26.08,
                    partPersistentId = (uint)(2000 + i),
                    eventType = PartEventType.EngineShutdown,
                    partName = "terminal engine"
                });
            }

            // Stable sort (the fix)
            rec.StableSortPartEventsByUT();

            // ResumeAfterFalseAlarm's index-based removal
            int removed = rec.PartEvents.Count - partEventCountBeforeChainStop;
            rec.PartEvents.RemoveRange(partEventCountBeforeChainStop, removed);

            // Both decouples must still be present
            Assert.Equal(7, rec.PartEvents.Count);

            bool coreDecoupleFound = false;
            bool boosterDecoupleFound = false;
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var e = rec.PartEvents[i];
                if (e.eventType == PartEventType.Decoupled)
                {
                    if (e.partPersistentId == 2057942744) coreDecoupleFound = true;
                    else if (e.partPersistentId == 1009856088) boosterDecoupleFound = true;
                }
                // No terminal engine shutdown events should remain
                if (e.eventType == PartEventType.EngineShutdown)
                {
                    Assert.True(e.partPersistentId < 2000,
                        $"Terminal engine shutdown pid={e.partPersistentId} was NOT removed — " +
                        "stable sort failed to keep it in the [partEventCountBeforeChainStop, end) range.");
                }
            }

            Assert.True(coreDecoupleFound, "Core-side decouple pid=2057942744 was wrongly removed by RemoveRange");
            Assert.True(boosterDecoupleFound, "Booster-side decouple pid=1009856088 was wrongly removed by RemoveRange " +
                "— this is the exact 2026-04-09 Kerbal X playtest bug. The stable sort fix is broken.");
        }

        [Fact]
        public void StableSortPartEventsByUT_MixedOrder_WithAndWithoutTies_SortsCorrectly()
        {
            var rec = new FlightRecorder();
            // Interleaved: some events in order, some out of order, some ties
            var inputs = new List<PartEvent>
            {
                new PartEvent { ut = 5.0, partPersistentId = 1, partName = "a" },
                new PartEvent { ut = 3.0, partPersistentId = 2, partName = "b" },
                new PartEvent { ut = 5.0, partPersistentId = 3, partName = "c" }, // tie with pid=1
                new PartEvent { ut = 1.0, partPersistentId = 4, partName = "d" },
                new PartEvent { ut = 3.0, partPersistentId = 5, partName = "e" }, // tie with pid=2
                new PartEvent { ut = 5.0, partPersistentId = 6, partName = "f" }, // tie with pid=1,3
            };
            foreach (var e in inputs) rec.PartEvents.Add(e);

            rec.StableSortPartEventsByUT();

            // Expected order (stable): UT=1 (pid=4), UT=3 (pid=2, then pid=5),
            // UT=5 (pid=1, then pid=3, then pid=6)
            Assert.Equal(6, rec.PartEvents.Count);
            Assert.Equal(4u, rec.PartEvents[0].partPersistentId);
            Assert.Equal(2u, rec.PartEvents[1].partPersistentId);
            Assert.Equal(5u, rec.PartEvents[2].partPersistentId);
            Assert.Equal(1u, rec.PartEvents[3].partPersistentId);
            Assert.Equal(3u, rec.PartEvents[4].partPersistentId);
            Assert.Equal(6u, rec.PartEvents[5].partPersistentId);
        }
    }
}
