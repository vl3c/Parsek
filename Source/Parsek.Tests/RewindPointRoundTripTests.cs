using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against RewindPoint save/load schema drift — both PID maps, all
    /// ChildSlots, and the session-provisional/corrupted flags must round-trip
    /// exactly (design doc section 5.1 + 5.9).
    /// </summary>
    public class RewindPointRoundTripTests
    {
        [Fact]
        public void RewindPoint_FullyPopulated_RoundTripsExactly()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_a1b2c3d4e5f6",
                BranchPointId = "bp_undock_1",
                UT = 17500.125,
                QuicksaveFilename = "rp_a1b2c3d4e5f6.sfs",
                SessionProvisional = false,
                Corrupted = false,
                CreatingSessionId = null,
                CreatedRealTime = "2026-04-17T21:35:12Z",
                FocusSlotIndex = 1,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_child0",
                        Controllable = true,
                        Disabled = false,
                        DisabledReason = null
                    },
                    new ChildSlot
                    {
                        SlotIndex = 1,
                        OriginChildRecordingId = "rec_child1",
                        Controllable = true,
                        Disabled = true,
                        DisabledReason = "lookup failed",
                        Sealed = true,
                        SealedRealTime = "2026-04-28T10:15:30.0000000Z",
                        Parked = true,
                        ParkedRealTime = "2026-04-29T08:09:10.0000000Z"
                    },
                    new ChildSlot
                    {
                        SlotIndex = 2,
                        OriginChildRecordingId = "rec_child2"
                    }
                },
                PidSlotMap = new Dictionary<uint, int>
                {
                    { 12345678u, 0 },
                    { 87654321u, 1 },
                    { 55667788u, 2 }
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    { 11111111u, 0 },
                    { 22222222u, 1 },
                    { 33333333u, 2 }
                }
            };

            var parent = new ConfigNode("REWIND_POINTS");
            rp.SaveInto(parent);

            var pointNodes = parent.GetNodes("POINT");
            Assert.Single(pointNodes);

            var restored = RewindPoint.LoadFrom(pointNodes[0]);

            Assert.Equal(rp.RewindPointId, restored.RewindPointId);
            Assert.Equal(rp.BranchPointId, restored.BranchPointId);
            Assert.Equal(rp.UT, restored.UT);
            Assert.Equal(rp.QuicksaveFilename, restored.QuicksaveFilename);
            Assert.Equal(rp.SessionProvisional, restored.SessionProvisional);
            Assert.Equal(rp.Corrupted, restored.Corrupted);
            Assert.Equal(rp.CreatingSessionId, restored.CreatingSessionId);
            Assert.Equal(rp.CreatedRealTime, restored.CreatedRealTime);
            Assert.Equal(1, restored.FocusSlotIndex);

            Assert.Equal(3, restored.ChildSlots.Count);
            Assert.Equal(0, restored.ChildSlots[0].SlotIndex);
            Assert.Equal("rec_child0", restored.ChildSlots[0].OriginChildRecordingId);
            Assert.True(restored.ChildSlots[0].Controllable);
            Assert.False(restored.ChildSlots[0].Disabled);
            Assert.Null(restored.ChildSlots[0].DisabledReason);
            Assert.False(restored.ChildSlots[0].Sealed);
            Assert.Null(restored.ChildSlots[0].SealedRealTime);
            Assert.False(restored.ChildSlots[0].Parked);
            Assert.Null(restored.ChildSlots[0].ParkedRealTime);

            Assert.Equal(1, restored.ChildSlots[1].SlotIndex);
            Assert.Equal("rec_child1", restored.ChildSlots[1].OriginChildRecordingId);
            Assert.True(restored.ChildSlots[1].Disabled);
            Assert.Equal("lookup failed", restored.ChildSlots[1].DisabledReason);
            Assert.True(restored.ChildSlots[1].Sealed);
            Assert.Equal("2026-04-28T10:15:30.0000000Z", restored.ChildSlots[1].SealedRealTime);
            Assert.True(restored.ChildSlots[1].Parked);
            Assert.Equal("2026-04-29T08:09:10.0000000Z", restored.ChildSlots[1].ParkedRealTime);

            Assert.Equal(2, restored.ChildSlots[2].SlotIndex);
            Assert.Equal("rec_child2", restored.ChildSlots[2].OriginChildRecordingId);
            Assert.False(restored.ChildSlots[2].Sealed);
            Assert.False(restored.ChildSlots[2].Parked);

            Assert.Equal(3, restored.PidSlotMap.Count);
            Assert.Equal(0, restored.PidSlotMap[12345678u]);
            Assert.Equal(1, restored.PidSlotMap[87654321u]);
            Assert.Equal(2, restored.PidSlotMap[55667788u]);

            Assert.Equal(3, restored.RootPartPidMap.Count);
            Assert.Equal(0, restored.RootPartPidMap[11111111u]);
            Assert.Equal(1, restored.RootPartPidMap[22222222u]);
            Assert.Equal(2, restored.RootPartPidMap[33333333u]);
        }

        [Fact]
        public void RewindPoint_SessionProvisionalAndCorrupted_RoundTripsFlags()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_sess_corrupt",
                BranchPointId = "bp_1",
                UT = 100.0,
                QuicksaveFilename = "rp_sess_corrupt.sfs",
                SessionProvisional = true,
                Corrupted = true,
                CreatingSessionId = "sess_abcd"
            };

            var parent = new ConfigNode("REWIND_POINTS");
            rp.SaveInto(parent);
            var restored = RewindPoint.LoadFrom(parent.GetNode("POINT"));

            Assert.True(restored.SessionProvisional);
            Assert.True(restored.Corrupted);
            Assert.Equal("sess_abcd", restored.CreatingSessionId);
        }

        [Fact]
        public void RewindPoint_EmptyMapsAndSlots_RoundTripToEmptyCollections()
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_empty",
                BranchPointId = "bp_empty",
                UT = 0.0,
                QuicksaveFilename = "rp_empty.sfs"
            };

            var parent = new ConfigNode("REWIND_POINTS");
            rp.SaveInto(parent);
            var restored = RewindPoint.LoadFrom(parent.GetNode("POINT"));

            Assert.NotNull(restored.ChildSlots);
            Assert.Empty(restored.ChildSlots);
            Assert.NotNull(restored.PidSlotMap);
            Assert.Empty(restored.PidSlotMap);
            Assert.NotNull(restored.RootPartPidMap);
            Assert.Empty(restored.RootPartPidMap);
        }

        [Fact]
        public void RewindPoint_LegacyMissingStableLeafFields_LoadsDefaults()
        {
            var parent = new ConfigNode("REWIND_POINTS");
            var point = parent.AddNode("POINT");
            point.AddValue("rewindPointId", "rp_legacy");
            point.AddValue("branchPointId", "bp_legacy");
            point.AddValue("ut", "123.5");
            point.AddValue("quicksaveFilename", "rp_legacy.sfs");
            point.AddValue("sessionProvisional", "False");
            var slot = point.AddNode("CHILD_SLOT");
            slot.AddValue("slotIndex", "0");
            slot.AddValue("originChildRecordingId", "rec_legacy");
            slot.AddValue("controllable", "True");

            var restored = RewindPoint.LoadFrom(point);

            Assert.Equal(-1, restored.FocusSlotIndex);
            Assert.Single(restored.ChildSlots);
            Assert.False(restored.ChildSlots[0].Sealed);
            Assert.Null(restored.ChildSlots[0].SealedRealTime);
            Assert.False(restored.ChildSlots[0].Parked);
            Assert.Null(restored.ChildSlots[0].ParkedRealTime);
        }
    }
}
