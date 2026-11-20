using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Parts bolted to a Breaking Ground servo's moving half must follow the servo's moving
    /// transform on the ghost. The ghost builder places every part as a direct child of the
    /// ghost root at its snapshot pose and the robotic applier moves only the servo's own
    /// transform, so without a follower plan everything attached to a hinge, piston or rotor
    /// stays at its snapshot pose. These cells pin the pure half: which parts follow which
    /// servo, mirroring stock BaseServo.SetChildParentTransform.
    /// </summary>
    public class ServoFollowerPlannerTests
    {
        private static ConfigNode Part(int index, int parent, params string[] attachLines)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "part" + index.ToString(CultureInfo.InvariantCulture));
            part.AddValue("persistentId", Pid(index).ToString(CultureInfo.InvariantCulture));
            part.AddValue("parent", parent.ToString(CultureInfo.InvariantCulture));
            part.AddValue("position", "0,0,0");
            part.AddValue("rotation", "0,0,0,1");
            for (int i = 0; i < attachLines.Length; i++)
            {
                string line = attachLines[i];
                int eq = line.IndexOf('=');
                part.AddValue(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            }
            return part;
        }

        private static uint Pid(int index) => (uint)(5000 + index);

        private static void AssertFollows(Dictionary<uint, uint> map, uint follower, uint servo)
        {
            Assert.True(map.TryGetValue(follower, out uint actual),
                $"part {follower} should follow servo {servo} but follows nothing");
            Assert.Equal(servo, actual);
        }

        private static ServoAttachConfig Hinge()
            => ServoAttachConfig.FromFieldStrings("top", "movingCollider, hubCollider");

        private static Dictionary<uint, uint> FollowerMap(ServoFollowerPlan plan)
            => plan.links.ToDictionary(l => l.followerPid, l => l.servoPid);

        /// <summary>
        /// 0 root pod
        /// 1 hinge on root (bottom -> root); moving node "top"
        /// 2 panel on hinge top              -> follows 1
        /// 3 tank under panel                -> follows 1 (subtree of a moving-side child)
        /// 4 srf on hinge BASE collider      -> follows nothing
        /// 5 srf on hinge MOVING collider    -> follows 1
        /// 6 nested hinge on tank 3          -> follows 1
        /// 7 part on nested hinge top        -> follows 6 (nearest servo), not 1
        /// 8 part on hinge 1 via a non-moving node "side" -> follows nothing
        /// </summary>
        private static ConfigNode[] ArmSnapshot()
        {
            return new[]
            {
                Part(0, 0, "attN = bottom, 1"),
                Part(1, 0, "attN = bottom, 0", "attN = top, 2", "attN = side, 8"),
                Part(2, 1, "attN = bottom, 1", "attN = top, 3"),
                Part(3, 2, "attN = top, 2", "attN = bottom, 6"),
                Part(4, 1, "srfN = srfAttach, 1, baseCollider"),
                Part(5, 1, "srfN = srfAttach, 1, movingCollider"),
                Part(6, 3, "attN = bottom, 3", "attN = top, 7"),
                Part(7, 6, "attN = bottom, 6"),
                Part(8, 1, "attN = bottom, 1"),
            };
        }

        [Fact]
        public void PartsOnTheServoMovingSideFollowTheNearestServo()
        {
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() }, { 6, Hinge() } };

            ServoFollowerPlan plan = ServoFollowerPlanner.Resolve(ArmSnapshot(), servos);
            Dictionary<uint, uint> map = FollowerMap(plan);

            AssertFollows(map, Pid(2), Pid(1));
            AssertFollows(map, Pid(3), Pid(1));
            AssertFollows(map, Pid(5), Pid(1));
            AssertFollows(map, Pid(6), Pid(1));
            AssertFollows(map, Pid(7), Pid(6));
            Assert.Equal(5, map.Count);
            Assert.Empty(plan.invertedServoPids);
        }

        [Fact]
        public void BaseSideAndUnrelatedPartsFollowNothing()
        {
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() }, { 6, Hinge() } };

            Dictionary<uint, uint> map = FollowerMap(ServoFollowerPlanner.Resolve(ArmSnapshot(), servos));

            Assert.DoesNotContain(Pid(0), map.Keys);
            Assert.DoesNotContain(Pid(1), map.Keys);
            Assert.DoesNotContain(Pid(4), map.Keys);
            Assert.DoesNotContain(Pid(8), map.Keys);
        }

        [Fact]
        public void ServoWhoseParentHangsOffItsMovingNodeIsInvertedAndCarriesNoFollowers()
        {
            // Stock's editor "rotateBase" case: the hinge's TOP (moving) node holds its parent,
            // so the moving half is the fixed one and the base swings. Not modelled: the hinge
            // is reported and its base-side child keeps the snapshot pose.
            var parts = new[]
            {
                Part(0, 0, "attN = bottom, 1"),
                Part(1, 0, "attN = top, 0", "attN = bottom, 2"),
                Part(2, 1, "attN = top, 1"),
            };
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() } };

            ServoFollowerPlan plan = ServoFollowerPlanner.Resolve(parts, servos);

            Assert.Empty(plan.links);
            Assert.Equal(new[] { Pid(1) }, plan.invertedServoPids);
        }

        [Fact]
        public void PartsBelowAnInvertedServoStillFollowAnOuterServo()
        {
            // outer hinge 1 -> part 2 on its top -> inverted hinge 3 (its top holds part 2)
            // -> part 4 on inverted hinge's base. 3 and 4 both ride outer hinge 1.
            var parts = new[]
            {
                Part(0, 0, "attN = bottom, 1"),
                Part(1, 0, "attN = bottom, 0", "attN = top, 2"),
                Part(2, 1, "attN = bottom, 1", "attN = top, 3"),
                Part(3, 2, "attN = top, 2", "attN = bottom, 4"),
                Part(4, 3, "attN = top, 3"),
            };
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() }, { 3, Hinge() } };

            ServoFollowerPlan plan = ServoFollowerPlanner.Resolve(parts, servos);
            Dictionary<uint, uint> map = FollowerMap(plan);

            AssertFollows(map, Pid(2), Pid(1));
            AssertFollows(map, Pid(3), Pid(1));
            AssertFollows(map, Pid(4), Pid(1));
            Assert.Equal(new[] { Pid(3) }, plan.invertedServoPids);
        }

        [Fact]
        public void ServoSurfaceAttachedOntoItsChildOnAMovingMeshCarriesThatChild()
        {
            // Mirrors stock's second SetChildParentTransform branch: the servo's own srfAttach
            // node points at the child with a moving-side mesh name.
            var parts = new[]
            {
                Part(0, 0),
                Part(1, 0, "srfN = srfAttach, 2, hubCollider"),
                Part(2, 1),
            };
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() } };

            Dictionary<uint, uint> map = FollowerMap(ServoFollowerPlanner.Resolve(parts, servos));

            AssertFollows(map, Pid(2), Pid(1));
        }

        [Fact]
        public void NoServosOrEmptyConfigOrMalformedTreeYieldsNoLinks()
        {
            Assert.Empty(ServoFollowerPlanner.Resolve(ArmSnapshot(), null).links);
            Assert.Empty(ServoFollowerPlanner.Resolve(null, new Dictionary<int, ServoAttachConfig>()).links);
            var empty = new Dictionary<int, ServoAttachConfig>
            {
                { 1, ServoAttachConfig.FromFieldStrings("", null) },
            };
            Assert.Empty(ServoFollowerPlanner.Resolve(ArmSnapshot(), empty).links);

            // A parent cycle (malformed) must terminate and link nothing beyond the hinge's own child.
            var cyclic = new[]
            {
                Part(0, 1),
                Part(1, 0, "attN = top, 2"),
                Part(2, 1, "attN = bottom, 1"),
            };
            var servos = new Dictionary<int, ServoAttachConfig> { { 1, Hinge() } };
            Dictionary<uint, uint> map = FollowerMap(ServoFollowerPlanner.Resolve(cyclic, servos));
            AssertFollows(map, Pid(2), Pid(1));
        }

        [Fact]
        public void ConfigParsesCommaSeparatedStockFields()
        {
            ServoAttachConfig config = ServoAttachConfig.FromFieldStrings(" top ,bottom2,", "a, b");
            Assert.Equal(new[] { "bottom2", "top" }, config.movingNodeIds.OrderBy(s => s).ToArray());
            Assert.Equal(new[] { "a", "b" }, config.movingSrfMeshNames.OrderBy(s => s).ToArray());
            Assert.True(ServoAttachConfig.FromFieldStrings(null, " , ").IsEmpty);
        }

        private static ConfigNode ServoPart(params ConfigNode[] modules)
        {
            var part = new ConfigNode("PART");
            foreach (ConfigNode m in modules) part.AddNode(m);
            return part;
        }

        private static ConfigNode Module(string name, string pos = null, string rot = null)
        {
            var m = new ConfigNode("MODULE");
            m.AddValue("name", name);
            if (pos != null) m.AddValue("servoTransformPosition", pos);
            if (rot != null) m.AddValue("servoTransformRotation", rot);
            return m;
        }

        [Fact]
        public void GeometryPoseIsReadFromTheServoModuleAtItsRoboticOrdinal()
        {
            // A non-robotic module before it does not count toward the ordinal; a wheel
            // module (robotic-classified) does.
            ConfigNode part = ServoPart(
                Module("ModuleLight"),
                Module("ModuleWheelSteering", "9,9,9", "0,0,0,1"),
                Module("ModuleRoboticServoHinge", "0.5,1.25,-2", "0,0.7071068,0,0.7071068"));

            Assert.True(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                part, 1, "ModuleRoboticServoHinge", out var pos, out var rot));
            Assert.Equal(0.5f, pos.x);
            Assert.Equal(1.25f, pos.y);
            Assert.Equal(-2f, pos.z);
            Assert.Equal(0.7071068f, rot.y);
            Assert.Equal(0.7071068f, rot.w);
        }

        [Fact]
        public void GeometryPoseRefusesDriftMissingAndNonUnitPoses()
        {
            ConfigNode part = ServoPart(Module("ModuleRoboticServoHinge", "0,0,0", "0,0,0,1"));
            Assert.False(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                part, 0, "ModuleRoboticServoPiston", out _, out _));
            Assert.False(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                part, 1, "ModuleRoboticServoHinge", out _, out _));
            Assert.False(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                ServoPart(Module("ModuleRoboticServoHinge", "0,0,0", null)),
                0, "ModuleRoboticServoHinge", out _, out _));
            // KSP's default-constructed Quaternion field persists as all zeros.
            Assert.False(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                ServoPart(Module("ModuleRoboticServoHinge", "0,0,0", "0,0,0,0")),
                0, "ModuleRoboticServoHinge", out _, out _));
            Assert.False(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                ServoPart(Module("ModuleRoboticServoHinge", "NaN,0,0", "0,0,0,1")),
                0, "ModuleRoboticServoHinge", out _, out _));
            Assert.True(GhostVisualBuilder.TryReadSnapshotServoGeometryPose(
                part, 0, "ModuleRoboticServoHinge", out _, out _));
        }

        [Theory]
        [InlineData("ModuleRoboticServoHinge", true)]
        [InlineData("ModuleRoboticServoPiston", true)]
        [InlineData("ModuleRoboticRotationServo", true)]
        [InlineData("ModuleRoboticServoRotor", true)]
        [InlineData("ModuleWheelSteering", false)]
        [InlineData("ModuleWheelMotor", false)]
        [InlineData(null, false)]
        public void OnlyBreakingGroundServosCarryFollowers(string moduleName, bool expected)
        {
            Assert.Equal(expected, ServoFollowerPlanner.IsBreakingGroundServoModuleName(moduleName));
        }
    }
}
