using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The construction ghost changes shape during playback (design 13.10 PART_ADDED /
    /// PART_REMOVED). Before the fix a cargo partner's EVA construction wrote only
    /// SegmentEvents, so its ghost kept the adoption snapshot's shape until the chain-tip
    /// spawn swapped in the real vessel (EVA-11: the ghost carried 17 parts, the spawn 15).
    /// </summary>
    public class ConstructionGhostShapeTests
    {
        private const uint RootPid = 100u;
        private const uint PortPid = 200u;
        private const uint PanelPid = 300u;
        private const uint BatteryPid = 400u;

        private static ConfigNode Part(string name, uint pid, int parent, string pos,
            string rot = "0,0,0,1", string attN = null)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", parent.ToString(CultureInfo.InvariantCulture));
            part.AddValue("pos", pos);
            part.AddValue("rot", rot);
            if (attN != null) part.AddValue("attN", attN);
            part.AddValue("srfN", "None, -1");
            return part;
        }

        private static ConfigNode Vessel(params ConfigNode[] parts)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("name", "Rover");
            foreach (var p in parts) v.AddNode(p);
            return v;
        }

        /// <summary>Root, docking port, solar panel: the shape the rover had when adopted.</summary>
        private static ConfigNode AdoptedRover() => Vessel(
            Part("roverBody", RootPid, 0, "0,0,0"),
            Part("dockingPort2", PortPid, 0, "0,1,0", attN: "bottom, 0"),
            Part("solarPanels5", PanelPid, 0, "1,0,0"));

        private static Recording Member(ConfigNode ghost)
        {
            return new Recording
            {
                RecordingId = "rec-member",
                VesselName = "Rover",
                GhostVisualSnapshot = ghost
            };
        }

        private static uint Pid(ConfigNode part) =>
            uint.Parse(part.GetValue("persistentId"), CultureInfo.InvariantCulture);

        [Fact]
        public void Detach_HidesThePartFromTheDetachUT()
        {
            var member = Member(AdoptedRover());
            var fresh = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"));

            var result = ConstructionGhostShape.Apply(member, fresh, attached: false,
                livePid: PanelPid, partName: "solarPanels5", ut: 50.0);

            Assert.Equal(ConstructionGhostShapeAction.Removed, result.Action);
            Assert.True(result.EventWritten);
            Assert.Single(member.PartEvents);
            Assert.Equal(PartEventType.InventoryPartRemoved, member.PartEvents[0].eventType);
            Assert.Equal(PanelPid, member.PartEvents[0].partPersistentId);
            Assert.Equal(50.0, member.PartEvents[0].ut);
            Assert.Equal("solarPanels5", member.PartEvents[0].partName);
            // The ghost keeps the node: the event hides it, the earlier frames still show it.
            Assert.Equal(3, member.GhostVisualSnapshot.GetNodes("PART").Length);
            Assert.True(member.FilesDirty);
        }

        [Fact]
        public void Attach_OfANewPart_AppendsItToTheGhost_AndShowsItFromTheAttachUT()
        {
            var member = Member(AdoptedRover());
            // The fresh snapshot lists parts in another order, so indices must be remapped by pid.
            var fresh = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("batteryBankMini", BatteryPid, 3, "0,1.5,0", attN: "bottom, 3"),
                Part("solarPanels5", PanelPid, 0, "1,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0", attN: "top, 1"));

            var result = ConstructionGhostShape.Apply(member, fresh, attached: true,
                livePid: BatteryPid, partName: "batteryBankMini", ut: 40.0);

            Assert.Equal(ConstructionGhostShapeAction.Added, result.Action);
            Assert.Equal(BatteryPid, result.GhostPid);
            ConfigNode[] ghostParts = member.GhostVisualSnapshot.GetNodes("PART");
            Assert.Equal(4, ghostParts.Length);
            ConfigNode battery = ghostParts[3];
            Assert.Equal(BatteryPid, Pid(battery));
            Assert.Equal("1", battery.GetValue("parent")); // the port is ghost index 1
            Assert.Equal("bottom, 1", battery.GetValue("attN"));
            Assert.Equal("None, -1", battery.GetValue("srfN"));
            Assert.Null(battery.GetValue(ConstructionGhostShape.GhostOfKey));

            // First placement-related event is Placed, so playback starts the part hidden.
            Assert.Single(member.PartEvents);
            Assert.Equal(PartEventType.InventoryPartPlaced, member.PartEvents[0].eventType);
            Assert.Equal(BatteryPid, member.PartEvents[0].partPersistentId);

            // The subtree map the playback builds sees the battery under the port.
            var subtree = GhostVisualBuilder.BuildPartSubtreeMap(member.GhostVisualSnapshot);
            Assert.Contains(BatteryPid, subtree[PortPid]);
        }

        [Fact]
        public void Reattach_AtTheSamePlace_ReusesTheGhostNode()
        {
            var member = Member(AdoptedRover());
            var without = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"));

            ConstructionGhostShape.Apply(member, without, false, PanelPid, "solarPanels5", 50.0);
            var result = ConstructionGhostShape.Apply(member, AdoptedRover(), true,
                PanelPid, "solarPanels5", 55.0);

            Assert.Equal(ConstructionGhostShapeAction.Present, result.Action);
            Assert.Equal(PanelPid, result.GhostPid);
            Assert.Equal(3, member.GhostVisualSnapshot.GetNodes("PART").Length);
            Assert.Equal(2, member.PartEvents.Count);
            Assert.Equal(PartEventType.InventoryPartRemoved, member.PartEvents[0].eventType);
            Assert.Equal(PartEventType.InventoryPartPlaced, member.PartEvents[1].eventType);
        }

        [Fact]
        public void Reattach_Elsewhere_AddsAStandIn_AndLaterEventsFollowIt()
        {
            var member = Member(AdoptedRover());
            var without = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"));
            var moved = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"),
                Part("solarPanels5", PanelPid, 1, "0,2,0", rot: "0,0.7071068,0,0.7071068"));

            ConstructionGhostShape.Apply(member, without, false, PanelPid, "solarPanels5", 50.0);
            var attach = ConstructionGhostShape.Apply(member, moved, true, PanelPid, "solarPanels5", 60.0);

            Assert.Equal(ConstructionGhostShapeAction.Moved, attach.Action);
            Assert.NotEqual(PanelPid, attach.GhostPid);
            Assert.NotEqual(0u, attach.GhostPid);
            ConfigNode[] ghostParts = member.GhostVisualSnapshot.GetNodes("PART");
            Assert.Equal(4, ghostParts.Length);
            Assert.Equal(attach.GhostPid, Pid(ghostParts[3]));
            Assert.Equal(PanelPid.ToString(CultureInfo.InvariantCulture),
                ghostParts[3].GetValue(ConstructionGhostShape.GhostOfKey));
            Assert.Equal("0,2,0", ghostParts[3].GetValue("pos"));
            Assert.Equal(PanelPid, Pid(ghostParts[2])); // the old node stays, hidden by the detach

            var detachAgain = ConstructionGhostShape.Apply(member, without, false,
                PanelPid, "solarPanels5", 70.0);
            Assert.Equal(attach.GhostPid, detachAgain.GhostPid);

            Assert.Equal(new[] { PanelPid, attach.GhostPid, attach.GhostPid },
                new[] { member.PartEvents[0].partPersistentId, member.PartEvents[1].partPersistentId,
                        member.PartEvents[2].partPersistentId });
        }

        [Fact]
        public void Reattach_UnderAnotherParent_AtTheSamePose_IsAMove()
        {
            var member = Member(AdoptedRover());
            var underPort = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"),
                Part("solarPanels5", PanelPid, 1, "1,0,0"));

            var result = ConstructionGhostShape.Apply(member, underPort, true,
                PanelPid, "solarPanels5", 60.0);

            Assert.Equal(ConstructionGhostShapeAction.Moved, result.Action);
        }

        [Fact]
        public void PoseWithinTolerance_IsTheSamePlace()
        {
            var member = Member(AdoptedRover());
            var jitter = Vessel(
                Part("roverBody", RootPid, 0, "0,0,0"),
                Part("dockingPort2", PortPid, 0, "0,1,0"),
                Part("solarPanels5", PanelPid, 0, "1.0004,0,-0.0003", rot: "0,0,0.0002,1"));

            var result = ConstructionGhostShape.Apply(member, jitter, true,
                PanelPid, "solarPanels5", 60.0);

            Assert.Equal(ConstructionGhostShapeAction.Present, result.Action);
        }

        [Fact]
        public void Attach_ThatTheFreshSnapshotLacks_WritesNothing()
        {
            var member = Member(AdoptedRover());
            var result = ConstructionGhostShape.Apply(member, AdoptedRover(), true,
                BatteryPid, "batteryBankMini", 40.0);

            Assert.Equal(ConstructionGhostShapeAction.MissingInFresh, result.Action);
            Assert.False(result.EventWritten);
            Assert.Empty(member.PartEvents);
            Assert.Equal(3, member.GhostVisualSnapshot.GetNodes("PART").Length);
        }

        [Fact]
        public void Detach_OfAPartTheGhostLacks_WritesNothing()
        {
            var member = Member(AdoptedRover());
            var result = ConstructionGhostShape.Apply(member, AdoptedRover(), false,
                BatteryPid, "batteryBankMini", 40.0);

            Assert.Equal(ConstructionGhostShapeAction.NotInGhost, result.Action);
            Assert.Empty(member.PartEvents);
        }

        [Fact]
        public void NoGhostSnapshot_WritesNothing()
        {
            var member = Member(null);
            var result = ConstructionGhostShape.Apply(member, AdoptedRover(), false,
                PanelPid, "solarPanels5", 40.0);

            Assert.Equal(ConstructionGhostShapeAction.NoGhostSnapshot, result.Action);
            Assert.Empty(member.PartEvents);
        }

        [Fact]
        public void EventsStayInUtOrder()
        {
            var member = Member(AdoptedRover());
            member.PartEvents.Add(new PartEvent
            {
                ut = 80.0, partPersistentId = RootPid, eventType = PartEventType.LightOn, partName = "roverBody"
            });

            ConstructionGhostShape.Apply(member, AdoptedRover(), false, PanelPid, "solarPanels5", 50.0);

            Assert.Equal(50.0, member.PartEvents[0].ut);
            Assert.Equal(80.0, member.PartEvents[1].ut);
        }

        [Fact]
        public void SyntheticPid_IsDeterministic_UniqueAndNonzero()
        {
            ConfigNode[] parts = AdoptedRover().GetNodes("PART");
            uint a = ConstructionGhostShape.SyntheticPid(parts, PanelPid);
            uint b = ConstructionGhostShape.SyntheticPid(parts, PanelPid);
            Assert.Equal(a, b);
            Assert.NotEqual(0u, a);
            Assert.DoesNotContain(a, new[] { RootPid, PortPid, PanelPid });
        }

        [Fact]
        public void FormatLog_IsInvariant()
        {
            var result = new ConstructionGhostShapeResult
            {
                Action = ConstructionGhostShapeAction.Removed, GhostPid = PanelPid, EventWritten = true
            };
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string line = ConstructionGhostShape.FormatLog("rec-member", 1871615455u, false,
                    PanelPid, "solarPanels5", 1234.5, result, 17);
                Assert.Equal("Construction ghost shape: rec=rec-member vesselPid=1871615455 change=detach " +
                    "part='solarPanels5' livePid=300 ghostPid=300 action=Removed event=InventoryPartRemoved " +
                    "ut=1234.50 ghostParts=17", line);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
