using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CommNet;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Vessel names Phase 2a (design-vessel-and-mission-names.md 15.2) against the live stock
    /// vessel the batch flies: a stock rename dialog accept on the active recorded vessel, a
    /// Parsek rename written through to it, and the priority naming part keeping the name when
    /// stock re-derives it. Every cell needs the active vessel to be the one the live tree is
    /// recording (a flight with auto-record on, or a resumed recording) and skips otherwise,
    /// naming that context. Each cell restores the vessel name, its naming part, the leg names
    /// and the decision store it found.
    /// </summary>
    public class VesselNamesInGameTests
    {
        private const string Category = "VesselNames";

        private sealed class Context
        {
            public ParsekFlight Flight;
            public Vessel Vessel;
            public string Guid;
            public string LegId;
            public VesselIdentity.Resolution Res;
            public VesselIdentity.Identity Identity;
        }

        private sealed class Snapshot
        {
            public ConfigNode Store;
            public readonly Dictionary<Recording, string> LegNames = new Dictionary<Recording, string>();
            public Vessel Vessel;
            public string VesselName;
            public readonly List<KeyValuePair<Part, VesselNaming>> Namings = new List<KeyValuePair<Part, VesselNaming>>();
            public readonly List<KeyValuePair<VesselNaming, string>> NamingNames = new List<KeyValuePair<VesselNaming, string>>();
        }

        private static Context RequireRecordedActiveVessel()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null) InGameAssert.Skip("needs a ParsekFlight instance (FLIGHT scene)");
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) InGameAssert.Skip("needs an active vessel");
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) InGameAssert.Skip("the active vessel is a ghost map vessel");
            string legId = flight.ResolveRecordingLegIdForLiveVessel(v.persistentId);
            if (legId == null)
                InGameAssert.Skip("the active vessel is not being recorded (needs a live recording of the active vessel)");
            List<RecordingTree> trees = VesselNameStore.GatherTrees();
            VesselIdentity.Resolution res = VesselIdentity.Resolve(trees, VesselNameStore.GatherLoose(trees));
            string guid = v.id.ToString("N");
            VesselIdentity.Identity identity = res.ResolveLiveVessel(guid, v.persistentId, GhostMapPresence.IsGhostMapVessel);
            if (identity == null)
                InGameAssert.Skip("the active vessel's launch guid matches no recording (legacy guid-less recording?)");
            if (!identity.LegIds.Contains(legId))
                InGameAssert.Skip("the leg recording the active vessel is not part of its identity");
            return new Context { Flight = flight, Vessel = v, Guid = guid, LegId = legId, Res = res, Identity = identity };
        }

        private static Snapshot Take(Context c)
        {
            var s = new Snapshot
            {
                Store = VesselNameStore.EncodeNode(),
                Vessel = c.Vessel,
                VesselName = c.Vessel.vesselName,
            };
            foreach (KeyValuePair<string, List<Recording>> kv in c.Res.CopiesById)
                foreach (Recording r in kv.Value)
                    if (r != null && !s.LegNames.ContainsKey(r)) s.LegNames[r] = r.VesselName;
            foreach (Part p in c.Vessel.parts)
            {
                if (p == null) continue;
                s.Namings.Add(new KeyValuePair<Part, VesselNaming>(p, p.vesselNaming));
                if (p.vesselNaming != null)
                    s.NamingNames.Add(new KeyValuePair<VesselNaming, string>(p.vesselNaming, p.vesselNaming.vesselName));
            }
            return s;
        }

        private static void Restore(Snapshot s)
        {
            if (s == null) return;
            var wrapper = new ConfigNode("SCENARIO");
            if (s.Store != null) wrapper.AddNode(s.Store);
            VesselNameStore.Load(wrapper);
            foreach (KeyValuePair<Recording, string> kv in s.LegNames) kv.Key.VesselName = kv.Value;
            foreach (KeyValuePair<VesselNaming, string> kv in s.NamingNames) kv.Key.vesselName = kv.Value;
            foreach (KeyValuePair<Part, VesselNaming> kv in s.Namings)
                if (kv.Key != null) kv.Key.vesselNaming = kv.Value;
            if (s.Vessel != null)
            {
                s.Vessel.vesselName = s.VesselName;
                if (s.Vessel.protoVessel != null && !s.Vessel.loaded) s.Vessel.protoVessel.vesselName = s.VesselName;
            }
            DisplayNames.NoteChanged("ingame-test-restore", Category);
        }

        private static string TestName(string what)
            => "VN-" + what + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture).Substring(10);

        private static void AssertEveryCoveredLegNamed(Context c, VesselNameDecision d, string name)
        {
            int checkedLegs = 0;
            foreach (string legId in c.Identity.LegIds)
            {
                if (!d.CoveredLegIds.Contains(legId)) continue;
                List<Recording> copies = c.Res.Copies(legId);
                if (copies == null) continue;
                foreach (Recording r in copies)
                {
                    InGameAssert.AreEqual(name, r.VesselName, "covered leg " + legId + " was not renamed");
                    checkedLegs++;
                }
            }
            InGameAssert.IsTrue(checkedLegs > 0, "no covered leg of the identity to check");
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "A stock rename dialog accept on the active recorded vessel renames every leg, its ghost protos and its ghost CommNet nodes")]
        public IEnumerator StockRenameOfTheRecordedVessel_RenamesLegsGhostsAndCommNet()
        {
            Context c = RequireRecordedActiveVessel();
            MethodBase accept = VesselRenameAcceptCapturePatch.ResolveTargetMethodForTesting();
            if (accept == null) InGameAssert.Skip("Vessel.onVesselRenameAccept is not present in this KSP build");
            Snapshot snap = Take(c);
            string name = TestName("Stock");
            try
            {
                // The stock dialog's accept callback itself, so the Harmony pair runs as in play.
                accept.Invoke(c.Vessel, new object[] { name, c.Vessel.vesselType });
                InGameAssert.AreEqual(name, c.Vessel.vesselName, "stock did not rename the vessel");

                VesselNameDecision d = VesselNameStore.FindByGuid(c.Guid);
                InGameAssert.IsNotNull(d, "the stock rename was not captured as a decision");
                InGameAssert.AreEqual(name, d.Name, "decision name");
                InGameAssert.AreEqual(VesselNameSource.StockDialog, d.Source, "decision source");
                AssertEveryCoveredLegNamed(c, d, name);

                // Ghost protos and CommNet nodes rename in their per-tick hosts.
                for (int i = 0; i < 5; i++) yield return null;

                int ghosts = 0;
                List<Vessel> vessels = FlightGlobals.Vessels;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel g = vessels[i];
                    if (g == null || !GhostMapPresence.IsGhostMapVessel(g.persistentId)) continue;
                    string recId = GhostMapPresence.FindRecordingIdByVesselPid(g.persistentId);
                    if (recId == null || !d.CoveredLegIds.Contains(recId)) continue;
                    InGameAssert.AreEqual(GhostMapPresence.BuildGhostProtoVesselName(name), g.vesselName,
                        "ghost proto of leg " + recId);
                    ghosts++;
                }
                int nodes = 0;
                GhostCommNetManager manager = c.Flight.GhostCommNet;
                if (manager != null)
                    foreach (string legId in c.Identity.LegIds)
                        if (d.CoveredLegIds.Contains(legId) && manager.TryGetRegisteredNode(legId, out CommNode node) && node != null)
                        {
                            InGameAssert.AreEqual(name, node.displayName, "ghost CommNet node of leg " + legId);
                            nodes++;
                        }
                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "VesselNames stock rename cell: legs={0} ghostProtos={1} commNetNodes={2}",
                    c.Identity.LegIds.Count, ghosts, nodes));
            }
            finally
            {
                Restore(snap);
            }
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "A Parsek rename writes through to the live vessel, fires onVesselRename once, and is not re-captured as a stock decision")]
        public void ParsekRename_WritesThroughOnce_AndIsNotRecaptured()
        {
            Context c = RequireRecordedActiveVessel();
            List<Recording> copies = c.Res.Copies(c.LegId);
            if (copies == null || copies.Count == 0) InGameAssert.Skip("the recording leg has no recording object");
            Snapshot snap = Take(c);
            string name = TestName("Parsek");
            int events = 0;
            int stockBefore = CountStockDecisions();
            EventData<GameEvents.HostedFromToAction<Vessel, string>>.OnEvent handler = e =>
            {
                if (e.host == c.Vessel) events++;
            };
            // Self-set-up: a live name none of the vessel's legs shows (a stock rename made
            // before this build, an automatic stock name). Set directly, so no event and no capture.
            c.Vessel.vesselName = TestName("LiveOnly");
            GameEvents.onVesselRename.Add(handler);
            try
            {
                InGameAssert.IsTrue(MissionVesselNaming.ApplyRecordingRename(copies[0], name),
                    "the Parsek rename was refused");
                InGameAssert.AreEqual(name, c.Vessel.vesselName, "the live vessel was not renamed");
                InGameAssert.AreEqual(1, events, "onVesselRename fire count for the vessel");
                VesselNameDecision d = VesselNameStore.FindByGuid(c.Guid);
                InGameAssert.IsNotNull(d, "no decision");
                InGameAssert.AreEqual(VesselNameSource.ParsekRename, d.Source, "the write-through was re-captured");
                InGameAssert.AreEqual(stockBefore, CountStockDecisions(), "a stock decision appeared");
                AssertEveryCoveredLegNamed(c, d, name);
            }
            finally
            {
                GameEvents.onVesselRename.Remove(handler);
                Restore(snap);
            }
        }

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "The write-through sets the priority naming part, so stock re-deriving the name (onVesselWasModified) keeps it")]
        public void WriteThrough_UpdatesTheNamingPart_SoAVesselModificationKeepsTheName()
        {
            Context c = RequireRecordedActiveVessel();
            List<Recording> copies = c.Res.Copies(c.LegId);
            if (copies == null || copies.Count == 0) InGameAssert.Skip("the recording leg has no recording object");
            MethodInfo update = typeof(Vessel).GetMethod("UpdateVesselNaming",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            if (update == null) InGameAssert.Skip("Vessel.UpdateVesselNaming(bool) is not present in this KSP build");
            Snapshot snap = Take(c);
            string name = TestName("Naming");
            try
            {
                // Self-set-up: the vessel's own name on a naming part, as a part named in the
                // editor would carry after a load.
                Part named = VesselNaming.FindPriorityNamePart(c.Vessel);
                if (named == null)
                {
                    named = c.Vessel.rootPart;
                    if (named == null) InGameAssert.Skip("the active vessel has no root part");
                    named.vesselNaming = new VesselNaming
                    {
                        vesselName = c.Vessel.vesselName,
                        vesselType = c.Vessel.vesselType,
                        namingPriority = 1,
                    };
                }

                InGameAssert.IsTrue(MissionVesselNaming.ApplyRecordingRename(copies[0], name),
                    "the Parsek rename was refused");
                InGameAssert.AreEqual(name, named.vesselNaming.vesselName, "the priority naming part was not updated");

                // What onVesselWasModified runs (Vessel.OnVesselNamingVesselWasModified).
                update.Invoke(c.Vessel, new object[] { false });
                InGameAssert.AreEqual(name, c.Vessel.vesselName, "stock reverted the name from the naming part");
            }
            finally
            {
                Restore(snap);
            }
        }

        private static int CountStockDecisions()
        {
            int n = 0;
            foreach (VesselNameDecision d in VesselNameStore.Decisions)
                if (d.Source != VesselNameSource.ParsekRename) n++;
            return n;
        }
    }
}
