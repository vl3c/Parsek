using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The crew-safe removal of a claimed vessel (CHAIN-GHOSTING-KILLS-UNLOADED-CLAIMED-CREW).
    /// The live target runs over real (uninitialized) KSP ProtoVessel / ProtoPartSnapshot /
    /// ProtoCrewMember objects: stock <c>Vessel.Die()</c> on an unloaded vessel kills whoever
    /// <c>ProtoVessel.GetVesselCrew()</c> still lists, so that list must be empty after the
    /// detach.
    /// </summary>
    [Collection("Sequential")]
    public class ClaimedVesselRemovalTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ClaimedVesselRemovalTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateRecorder.SuppressCrewEvents = false;
        }

        public void Dispose()
        {
            GameStateRecorder.SuppressCrewEvents = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ProtoCrewMember Kerbal(string name)
        {
            var pcm = (ProtoCrewMember)FormatterServices.GetUninitializedObject(typeof(ProtoCrewMember));
            typeof(ProtoCrewMember).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(pcm, name);
            typeof(ProtoCrewMember).GetField("_rosterStatus", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(pcm, ProtoCrewMember.RosterStatus.Assigned);
            return pcm;
        }

        private static ProtoPartSnapshot Part(params ProtoCrewMember[] crew)
        {
            var pps = (ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot));
            pps.protoModuleCrew = new List<ProtoCrewMember>(crew);
            pps.protoCrewNames = new List<string>();
            foreach (ProtoCrewMember pcm in crew)
                pps.protoCrewNames.Add(pcm.name);
            return pps;
        }

        private static ProtoVessel UnloadedVessel(params ProtoPartSnapshot[] parts)
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot>(parts);
            var crew = new List<ProtoCrewMember>();
            foreach (ProtoPartSnapshot pps in parts)
                crew.AddRange(pps.protoModuleCrew);
            typeof(ProtoVessel).GetField("crew", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(pv, crew);
            pv.persistentId = 3620499050u;
            pv.vesselName = "Kerbal X";
            return pv;
        }

        [Fact]
        public void LiveTarget_UnloadedVessel_LeavesNobodyForMurderCrewAndFreesTheCrew()
        {
            ProtoCrewMember bill = Kerbal("Bill Kerman");
            ProtoCrewMember bob = Kerbal("Bob Kerman");
            ProtoCrewMember val = Kerbal("Valentina Kerman");
            ProtoPartSnapshot pod = Part(bill, bob, val);
            ProtoVessel station = UnloadedVessel(pod, Part());
            var flightState = new List<ProtoVessel> { station };

            ClaimedVesselRemovalResult result = ClaimedVesselRemoval.Remove(
                new LiveClaimedVesselRemovalTarget(null, station, flightState), "test");

            // What stock Vessel.Die() -> MurderCrew reads on an unloaded vessel.
            Assert.Empty(station.GetVesselCrew());
            Assert.Empty(pod.protoModuleCrew);
            Assert.Empty(pod.protoCrewNames);
            Assert.DoesNotContain(station, flightState);
            Assert.Equal(3, result.DetachedCrew.Count);
            Assert.Equal(3, result.ReleasedCrew);
            Assert.Equal(ProtoCrewMember.RosterStatus.Available, bill.rosterStatus);
            Assert.Equal(ProtoCrewMember.RosterStatus.Available, bob.rosterStatus);
            Assert.Equal(ProtoCrewMember.RosterStatus.Available, val.rosterStatus);
            Assert.False(GameStateRecorder.SuppressCrewEvents);
        }

        [Fact]
        public void LiveTarget_ReattachCrew_PutsTheKerbalsBackOnTheirParts()
        {
            ProtoCrewMember jeb = Kerbal("Jebediah Kerman");
            ProtoPartSnapshot pod = Part(jeb);
            ProtoVessel station = UnloadedVessel(pod);
            var target = new LiveClaimedVesselRemovalTarget(null, station, null);

            Assert.Equal(new List<string> { "Jebediah Kerman" }, target.DetachCrew());
            Assert.Empty(station.GetVesselCrew());
            Assert.Equal(1, target.ReattachCrew());

            Assert.Equal(new List<ProtoCrewMember> { jeb }, station.GetVesselCrew());
            Assert.Equal(new List<ProtoCrewMember> { jeb }, pod.protoModuleCrew);
            Assert.Equal(new List<string> { "Jebediah Kerman" }, pod.protoCrewNames);
            Assert.Equal(ProtoCrewMember.RosterStatus.Assigned, jeb.rosterStatus);
        }

        private sealed class ThrowingTarget : IClaimedVesselRemovalTarget
        {
            internal readonly List<string> Aboard = new List<string> { "Bill Kerman" };
            internal readonly List<string> Calls = new List<string>();

            public uint PersistentId { get { return 3620499050u; } }
            public string VesselName { get { return "Kerbal X"; } }

            public List<string> DetachCrew()
            {
                Calls.Add("detach");
                var names = new List<string>(Aboard);
                Aboard.Clear();
                return names;
            }

            public void Destroy()
            {
                Calls.Add("destroy");
                throw new InvalidOperationException("Die failed");
            }

            public bool ReleaseCrew(string name)
            {
                Calls.Add("release");
                return true;
            }

            public int ReattachCrew()
            {
                Calls.Add("reattach");
                Aboard.Add("Bill Kerman");
                return 1;
            }
        }

        [Fact]
        public void Remove_DestroyThrows_PutsTheCrewBackAndRethrows()
        {
            var target = new ThrowingTarget();

            Assert.Throws<InvalidOperationException>(() => ClaimedVesselRemoval.Remove(target, "test"));

            Assert.Equal(new List<string> { "detach", "destroy", "reattach" }, target.Calls);
            Assert.Equal(new List<string> { "Bill Kerman" }, target.Aboard);
            Assert.False(GameStateRecorder.SuppressCrewEvents);
            Assert.Contains(logLines, l => l.Contains("[WARN][ClaimRemoval]")
                && l.Contains("Claimed vessel removal failed (test)")
                && l.Contains("reattached 1/1"));
        }
    }
}
