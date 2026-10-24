using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 2b of design-vessel-and-mission-names.md: link rows and dock-absorb rows (6.1, 6.4),
    /// identity edge 4 (6.2), the spawn stamp, Spawn link and part-uid remap (7.5), the Resume
    /// link, the load-time spawn-link backfill, the dock / undock round trip (7.7) and the
    /// wiring of those hooks into the real code paths.
    /// </summary>
    [Collection("Sequential")]
    public class VesselNamesLinksTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        private const string G1 = "11111111111111111111111111111111";
        private const string G2 = "22222222222222222222222222222222";
        private const string G3 = "33333333333333333333333333333333";
        private const string G5 = "55555555555555555555555555555555";
        private const string G6 = "66666666666666666666666666666666";
        private const string G7 = "77777777777777777777777777777777";

        public VesselNamesLinksTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionStructureBuilder.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            VesselNameStore.NowUtProviderForTesting = () => 1000.0;
            DisplayNames.ResetSubscribersForTesting();
        }

        public void Dispose()
        {
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            DisplayNames.ResetSubscribersForTesting();
            MissionStructureBuilder.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- builders

        private static Recording Rec(string id, string vessel, double start, double end,
            uint pid = 0, string guid = null, string chain = null, int chainIndex = -1)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ChainId = chain,
                ChainIndex = chainIndex,
                TerminalStateValue = TerminalState.Orbiting,
                StartBodyName = "Kerbin",
            };
        }

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id, TreeName = id, RootRecordingId = recs[0].RecordingId };
            foreach (Recording r in recs)
            {
                r.TreeId = id;
                tree.Recordings[r.RecordingId] = r;
            }
            return tree;
        }

        private static void UseUniverse(params RecordingTree[] trees)
        {
            VesselNameStore.TreesProviderForTesting = () => trees;
        }

        private static VesselIdentity.Resolution Universe(params RecordingTree[] trees)
            => VesselNameStore.ResolveUniverse(trees, null);

        private sealed class FakeVessel
        {
            internal readonly List<string> Writes = new List<string>();
            internal LiveVesselEntry Entry;
        }

        private static FakeVessel Live(string guid, uint pid, string name, string recordingLegId = null, bool ghost = false)
        {
            var f = new FakeVessel();
            f.Entry = new LiveVesselEntry
            {
                Guid = guid, Pid = pid, Name = name, RecordingLegId = recordingLegId, IsGhostMap = ghost,
                Write = n => { f.Writes.Add(n); f.Entry.Name = n; return true; },
            };
            return f;
        }

        // ---------------------------------------------------------------- link rows

        [Fact]
        public void AddLink_RefusesUnknownEmptyAndSelfLinks_AndIsIdempotent()
        {
            Assert.False(VesselNameStore.AddLink(null, G1, VesselIdentityLinkKind.Spawn, "t"));
            Assert.False(VesselNameStore.AddLink(G1, Guid.Empty.ToString(), VesselIdentityLinkKind.Spawn, "t"));
            Assert.False(VesselNameStore.AddLink(G1, G1.ToUpperInvariant(), VesselIdentityLinkKind.Spawn, "t"));
            Assert.Equal(0, VesselNameStore.LinkCount);
            Assert.False(VesselNameStore.LinkApplyDue);

            int version = VesselNameStore.Version;
            Assert.True(VesselNameStore.AddLink("55555555-5555-5555-5555-555555555555", G1,
                VesselIdentityLinkKind.Spawn, "spawn rec=a"));
            Assert.True(VesselNameStore.Version > version);
            Assert.True(VesselNameStore.LinkApplyDue);
            Assert.True(VesselNameStore.TryGetLink(G5, out VesselIdentityLink link));
            Assert.Equal(G1, link.ToGuid);
            Assert.Equal(VesselIdentityLinkKind.Spawn, link.Kind);

            // The same row again is a no-op; another target keeps the first with a Warn.
            Assert.False(VesselNameStore.AddLink(G5, G1, VesselIdentityLinkKind.Resume, "resume"));
            Assert.False(VesselNameStore.AddLink(G5, G2, VesselIdentityLinkKind.Resume, "resume"));
            Assert.Equal(1, VesselNameStore.LinkCount);
            Assert.True(VesselNameStore.TryGetLink(G5, out link));
            Assert.Equal(G1, link.ToGuid);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Link added: kind=Spawn guid=" + G5 + " -> " + G1 + " reason=spawn rec=a"));
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]")
                && l.Contains("Link kept: guid=" + G5 + " already continues " + G1));
        }

        // ---------------------------------------------------------------- edge 4

        [Fact]
        public void Edge4_ALinkJoinsTwoTreesLegs_AndItsGuidJoinsTheIdentity()
        {
            RecordingTree t1 = Tree("T1", Rec("a", "Probe", 0, 10, pid: 1, guid: G1));
            RecordingTree t2 = Tree("T2", Rec("b", "Probe", 20, 30, pid: 9, guid: G2));
            VesselIdentity.Resolution apart = VesselIdentity.Resolve(new[] { t1, t2 }, null);
            Assert.NotSame(apart.ByRecordingId["a"], apart.ByRecordingId["b"]);

            VesselIdentity.Resolution res = VesselIdentity.Resolve(new[] { t1, t2 }, null,
                new[] { new KeyValuePair<string, string>(G2, G1) });
            Assert.Same(res.ByRecordingId["a"], res.ByRecordingId["b"]);
            Assert.Equal(1, res.LinkEdges);
            Assert.Equal(G1, res.LinkTargets[G2]);
        }

        // A spawned copy (G2) that records nothing, re-spawned or resumed again (G3): the chain
        // of links still reaches the launch, and every link guid is an identity guid, so the
        // live copy resolves to the identity by guid.
        [Fact]
        public void Edge4_ALinkChainThroughAGuidNoLegCarries_StillJoins()
        {
            RecordingTree t1 = Tree("T1", Rec("a", "Probe", 0, 10, pid: 1, guid: G1));
            RecordingTree t3 = Tree("T3", Rec("c", "Probe", 50, 60, pid: 3, guid: G3));
            VesselIdentity.Resolution res = VesselIdentity.Resolve(new[] { t1, t3 }, null, new[]
            {
                new KeyValuePair<string, string>(G3, G2),
                new KeyValuePair<string, string>(G2, G1),
                new KeyValuePair<string, string>(G5, G7), // reaches no recording: ignored
            });
            VesselIdentity.Identity id = res.ByRecordingId["a"];
            Assert.Same(id, res.ByRecordingId["c"]);
            Assert.Contains(G2, id.Guids);
            Assert.Same(id, res.ResolveLiveVessel(G2, 42, null));
            Assert.Null(res.ResolveLiveVessel(G5, 43, null));
        }

        // catches: FindGuid's path compression writing guidParent while Resolve enumerates its
        // keys ("Collection was modified" on .NET Framework / Mono). Two uncarried link guids
        // into one carried guid build a two-level chain for some ordinal orders.
        [Fact]
        public void Edge4_TwoUncarriedLinksIntoOneCarriedGuid_DoesNotThrow()
        {
            RecordingTree t = Tree("T", Rec("a", "Probe", 0, 10, pid: 1, guid: G2));
            VesselIdentity.Resolution res = VesselIdentity.Resolve(new[] { t }, null, new[]
            {
                new KeyValuePair<string, string>(G3, G2),
                new KeyValuePair<string, string>(G1, G2),
            });
            VesselIdentity.Identity id = res.ByRecordingId["a"];
            Assert.Contains(G1, id.Guids);
            Assert.Contains(G3, id.Guids);
            Assert.Same(id, res.ResolveLiveVessel(G1, 5, null));
        }

        // Every ordering of a link set (chains, fans, a link-only middle guid) over every
        // choice of the carried guid: ordinal guid order shapes the union-find, so no lucky
        // order may hide a fault.
        [Fact]
        public void Edge4_EveryLinkOrderAndCarriedGuid_ResolvesOneIdentityWithEveryGuid()
        {
            string[] guids = { G1, G2, G3, G5, G6, G7 };
            var shapes = new List<KeyValuePair<string, string>[]>();
            foreach (int[] perm in Permutations(new[] { 0, 1, 2, 3 }))
            {
                string a = guids[perm[0]], b = guids[perm[1]], c = guids[perm[2]], d = guids[perm[3]];
                shapes.Add(new[] { Pair(a, b), Pair(c, b), Pair(d, b) });        // fan into b
                shapes.Add(new[] { Pair(a, b), Pair(b, c), Pair(c, d) });        // chain a->b->c->d
                shapes.Add(new[] { Pair(c, d), Pair(b, c), Pair(a, b) });        // the chain, reversed
                shapes.Add(new[] { Pair(a, b), Pair(c, d), Pair(b, d) });        // two pairs joined
            }
            int resolved = 0;
            foreach (KeyValuePair<string, string>[] links in shapes)
            {
                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> l in links) { members.Add(l.Key); members.Add(l.Value); }
                foreach (string carried in members)
                {
                    RecordingTree t = Tree("T", Rec("a", "Probe", 0, 10, pid: 1, guid: carried));
                    VesselIdentity.Resolution res = VesselIdentity.Resolve(new[] { t }, null, links);
                    VesselIdentity.Identity id = res.ByRecordingId["a"];
                    Assert.True(id.Guids.SetEquals(members));
                    foreach (string g in members) Assert.Same(id, res.ResolveLiveVessel(g, 9, null));
                    resolved++;
                }
            }
            Assert.Equal(shapes.Count * 4, resolved);
        }

        private static KeyValuePair<string, string> Pair(string from, string to)
            => new KeyValuePair<string, string>(from, to);

        private static IEnumerable<int[]> Permutations(int[] items)
        {
            if (items.Length <= 1) { yield return items; yield break; }
            for (int i = 0; i < items.Length; i++)
            {
                int[] rest = items.Where((_, k) => k != i).ToArray();
                foreach (int[] p in Permutations(rest))
                    yield return new[] { items[i] }.Concat(p).ToArray();
            }
        }

        // A resolver fault in a Parsek rename refuses it through the refusal path, never
        // throws into the Recordings-tab commit.
        [Fact]
        public void ParsekRename_AResolverFault_IsRefused_NotThrown()
        {
            Recording a = Rec("a", "Probe", 0, 10, pid: 1, guid: G1);
            VesselNameStore.TreesProviderForTesting = () => throw new InvalidOperationException("boom");
            Assert.False(VesselNameStore.RecordParsekRename(a, "Relay", null, out string refusal));
            Assert.Equal(VesselNameStore.RefusalResolveFailed, refusal);
            Assert.Equal(0, VesselNameStore.DecisionCount);
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]")
                && l.Contains("Parsek rename refused: rec=a name='Relay' reason=resolve-failed: InvalidOperationException: boom"));
        }

        // catches: a link joining two decided components losing one decision's legs, or keeping
        // the older (lower seq) name.
        [Fact]
        public void Edge4_ALinkJoiningTwoDecidedComponents_KeepsTheHighestSeq_AndFoldsTheOther()
        {
            Recording a = Rec("a", "Probe", 0, 10, pid: 1, guid: G1);
            Recording b = Rec("b", "Copy", 20, 30, pid: 9, guid: G2);
            RecordingTree t1 = Tree("T1", a);
            RecordingTree t2 = Tree("T2", b);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Alpha", new[] { t1, t2 }));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(b, "Beta", new[] { t1, t2 }));
            Assert.Equal(2, VesselNameStore.DecisionCount);

            Assert.True(VesselNameStore.AddLink(G2, G1, VesselIdentityLinkKind.Spawn, "t"));
            VesselNameStore.ApplyTo(Universe(t1, t2), "test");

            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal("Beta", d.Name);
            Assert.Equal("Beta", a.VesselName);
            Assert.Equal("Beta", b.VesselName);
            Assert.Contains("Alpha", d.PriorNames);
            Assert.Same(d, VesselNameStore.FindByGuid(G1));
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]")
                && l.Contains("Conflicting vessel name decisions joined") && l.Contains("keeping seq=2 'Beta'")
                && l.Contains("dropping seq=1 'Alpha'"));
        }

        // Adding a link runs the apply pass (next host tick) with the live write rule: a spawned
        // copy no leg carries yet takes the decision through its Spawn link.
        [Fact]
        public void AddLink_SchedulesTheApplyPass_WhichWritesTheLinkedLiveVessel()
        {
            Recording a = Rec("a", "Probe", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Relay-2", new[] { t }));
            FakeVessel spawned = Live(G5, 77, "Probe");
            VesselNameLiveSync.LiveVesselsProviderForTesting = _ => new List<LiveVesselEntry> { spawned.Entry };

            VesselNameStore.ApplyAll("test");
            Assert.Empty(spawned.Writes);
            VesselNameStore.SelfHealTick(inFlight: false, realtime: 0f);
            Assert.Empty(spawned.Writes);

            Assert.True(VesselNameStore.AddLink(G5, G1, VesselIdentityLinkKind.Spawn, "spawn rec=a"));
            VesselNameStore.SelfHealTick(inFlight: false, realtime: 0f);

            Assert.Equal(new[] { "Relay-2" }, spawned.Writes.ToArray());
            Assert.False(VesselNameStore.LinkApplyDue);
            Assert.Contains(logLines, l => l.Contains("Apply pass (self-heal:link-added)"));
            Assert.Contains(logLines, l => l.Contains("Live write rule: pid=77 'Probe' -> 'Relay-2' currentLeg=a"));
        }

        // ---------------------------------------------------------------- codec

        [Fact]
        public void Codec_LinksAndDockAbsorbs_RoundTrip_UnderAGermanCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(VesselNameStore.AddLink(G5, G1, VesselIdentityLinkKind.Spawn, "t"));
                Assert.True(VesselNameStore.AddLink(G6, G2, VesselIdentityLinkKind.UndockReturn, "t"));
                Assert.True(VesselNameStore.RecordDockAbsorb(4123456789u, G3, "t"));

                // A store with links and no decision still writes its node.
                var scenario = new ConfigNode("SCENARIO");
                VesselNameStore.Save(scenario);
                ConfigNode node = scenario.GetNode(VesselNameStore.NodeName);
                Assert.NotNull(node);
                Assert.Equal(2, node.GetNodes(VesselNameStore.LinkNodeName).Length);
                Assert.Equal("4123456789", node.GetNode(VesselNameStore.DockAbsorbNodeName).GetValue("rootUid"));

                // Malformed rows are skipped with a Warn.
                ConfigNode bad = node.AddNode(VesselNameStore.LinkNodeName);
                bad.AddValue("guid", G7);
                bad.AddValue("to", G7);
                bad.AddValue("kind", "Spawn");
                ConfigNode badKind = node.AddNode(VesselNameStore.LinkNodeName);
                badKind.AddValue("guid", G7);
                badKind.AddValue("to", G1);
                badKind.AddValue("kind", "Teleport");
                ConfigNode badRow = node.AddNode(VesselNameStore.DockAbsorbNodeName);
                badRow.AddValue("rootUid", "-3");
                badRow.AddValue("guid", G1);

                VesselNameStore.ResetForTesting();
                VesselNameStore.Load(scenario);

                Assert.Equal(2, VesselNameStore.LinkCount);
                Assert.Equal(1, VesselNameStore.DockAbsorbCount);
                Assert.True(VesselNameStore.TryGetLink(G6, out VesselIdentityLink l6));
                Assert.Equal(G2, l6.ToGuid);
                Assert.Equal(VesselIdentityLinkKind.UndockReturn, l6.Kind);
                Assert.True(VesselNameStore.TryGetDockAbsorb(4123456789u, out DockAbsorbRow row));
                Assert.Equal(G3, row.Guid);
                Assert.False(VesselNameStore.TryGetLink(G7, out _));
                Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                    && l.Contains("Loaded VESSEL_NAMES: decisions=0 skipped=3 nextSeq=1 links=2 dockAbsorbs=1"));
                Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]") && l.Contains("skipped a malformed LINK"));
                Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]") && l.Contains("skipped a malformed DOCK_ABSORB"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Codec_AColdLoadWithoutTheNode_ClearsLinksAndRows()
        {
            Assert.True(VesselNameStore.AddLink(G5, G1, VesselIdentityLinkKind.Spawn, "t"));
            Assert.True(VesselNameStore.RecordDockAbsorb(7u, G3, "t"));
            VesselNameStore.Load(new ConfigNode("SCENARIO"));
            Assert.Equal(0, VesselNameStore.LinkCount);
            Assert.Equal(0, VesselNameStore.DockAbsorbCount);
            var scenario = new ConfigNode("SCENARIO");
            VesselNameStore.Save(scenario);
            Assert.Null(scenario.GetNode(VesselNameStore.NodeName));
        }

        // ---------------------------------------------------------------- spawn stamp (7.5)

        private static ConfigNode SpawnNode(string name, params int[] namingPriorities)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", G1);
            v.AddValue("name", name);
            for (int i = 0; i < namingPriorities.Length; i++)
            {
                ConfigNode p = v.AddNode("PART");
                p.AddValue("uid", (100 + i).ToString(CultureInfo.InvariantCulture));
                if (namingPriorities[i] < 0) continue;
                ConfigNode n = p.AddNode("VESSELNAMING");
                n.AddValue("name", "Part" + i.ToString(CultureInfo.InvariantCulture));
                n.AddValue("type", "Probe");
                n.AddValue("priority", namingPriorities[i].ToString(CultureInfo.InvariantCulture));
            }
            return v;
        }

        [Fact]
        public void StampSpawnNodeName_SetsTheNameAndThePriorityPart_StockTieRule()
        {
            ConfigNode v = SpawnNode("#autoLOC_501224", 2, -1, 3, 3);
            int idx = VesselNameSpawn.StampSpawnNodeName(v, "Relay-2");
            Assert.Equal(2, idx);
            Assert.Equal("Relay-2", v.GetValue("name"));
            ConfigNode[] parts = v.GetNodes("PART");
            Assert.Equal("Part0", parts[0].GetNode("VESSELNAMING").GetValue("name"));
            Assert.Equal("Relay-2", parts[2].GetNode("VESSELNAMING").GetValue("name"));
            Assert.Equal("Part3", parts[3].GetNode("VESSELNAMING").GetValue("name"));
            Assert.Equal("3", parts[2].GetNode("VESSELNAMING").GetValue("priority"));

            ConfigNode none = SpawnNode("Ship", 0, -1);
            Assert.Equal(-1, VesselNameSpawn.StampSpawnNodeName(none, "X"));
            Assert.Equal("X", none.GetValue("name"));
        }

        // catches: a stamp on a spawn whose leg the decision does not cover (an automatic stock
        // name after staging keeps its own name, ruling 4), or a stamp that rewrites the raw
        // #autoLOC tag of an undecided vessel.
        [Fact]
        public void ApplySpawnStamp_CoveredLegIsStamped_UncoveredAndUndecidedLegsAreLeftAlone()
        {
            Recording a1 = Rec("a1", "Kerbal X", 0, 100, pid: 1, guid: G1);
            Recording other = Rec("o1", "Kerbal X", 0, 100, pid: 5, guid: G2);
            RecordingTree t = Tree("T", a1);
            RecordingTree u = Tree("U", other);
            UseUniverse(t, u);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Relay-2", new[] { t, u }));
            Recording a2 = Rec("a2", "Kerbal X Probe", 100, 200, pid: 1, guid: G1);
            a2.TreeId = "T";
            t.Recordings["a2"] = a2;

            ConfigNode covered = SpawnNode("#autoLOC_501224", 1);
            Assert.Equal(VesselNameSpawn.StampCovered, VesselNameSpawn.ApplySpawnStamp(covered, a1, "test"));
            Assert.Equal("Relay-2", covered.GetValue("name"));
            Assert.Equal("Relay-2", covered.GetNode("PART").GetNode("VESSELNAMING").GetValue("name"));

            ConfigNode uncovered = SpawnNode("#autoLOC_501224", 1);
            Assert.Equal(VesselNameSpawn.StampUncovered, VesselNameSpawn.ApplySpawnStamp(uncovered, a2, "test"));
            Assert.Equal("#autoLOC_501224", uncovered.GetValue("name"));
            Assert.Equal("Part0", uncovered.GetNode("PART").GetNode("VESSELNAMING").GetValue("name"));

            ConfigNode undecided = SpawnNode("#autoLOC_501224", 1);
            Assert.Equal(VesselNameSpawn.StampNoDecision, VesselNameSpawn.ApplySpawnStamp(undecided, other, "test"));
            Assert.Equal("#autoLOC_501224", undecided.GetValue("name"));
            ConfigNode noSource = SpawnNode("#autoLOC_501224", 1);
            VesselNameSpawn.ApplySpawnStamp(noSource, null, "test");
            Assert.Equal("#autoLOC_501224", noSource.GetValue("name"));

            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Spawn node stamped: rec=a1 '#autoLOC_501224' -> 'Relay-2' namingPart=part[0]"));
            Assert.Contains(logLines, l => l.Contains("Spawn node left unstamped: rec=a2 reason=uncovered"));
        }

        // ---------------------------------------------------------------- uid remap (7.5)

        [Fact]
        public void RegeneratePartIdentities_ReturnsTheUidMap_AndDockRecordsFollowIt()
        {
            var v = new ConfigNode("VESSEL");
            ConfigNode root = v.AddNode("PART");
            root.AddValue("uid", "100");
            root.AddValue("persistentId", "1");
            ConfigNode port = v.AddNode("PART");
            port.AddValue("uid", "200");
            port.AddValue("persistentId", "2");
            ConfigNode dock = port.AddNode("MODULE");
            dock.AddValue("name", "ModuleDockingNode");
            dock.AddValue("dockUId", "100");
            dock.AddNode("DOCKEDVESSEL").AddValue("rootUId", "200");
            ConfigNode claw = root.AddNode("MODULE");
            claw.AddValue("name", "ModuleGrappleNode");
            claw.AddNode("DOCKEDVESSEL").AddValue("rootUId", "555");
            claw.AddNode("DOCKEDVESSEL_Other").AddValue("rootUId", "100");

            uint nextUid = 900, nextPid = 50;
            var uidMap = new Dictionary<uint, uint>();
            VesselSnapshotOps.RegeneratePartIdentities(v, () => nextPid++, () => nextUid++, 7u, 8u, uidMap);
            Assert.Equal(900u, uidMap[100]);
            Assert.Equal(901u, uidMap[200]);

            Assert.Equal(3, VesselNameSpawn.RemapDockRecordUids(v, uidMap));
            Assert.Equal("900", dock.GetValue("dockUId"));
            Assert.Equal("901", dock.GetNode("DOCKEDVESSEL").GetValue("rootUId"));
            Assert.Equal("555", claw.GetNode("DOCKEDVESSEL").GetValue("rootUId"));
            Assert.Equal("900", claw.GetNode("DOCKEDVESSEL_Other").GetValue("rootUId"));

            // A dock-absorb row keyed by an old root uid gets a copy under the new one; the old
            // row stays for a later respawn of the same snapshot.
            Assert.True(VesselNameStore.RecordDockAbsorb(200u, G3, "dock"));
            Assert.Equal(1, VesselNameStore.CopyDockAbsorbsForUidMap(uidMap, "spawn"));
            Assert.True(VesselNameStore.TryGetDockAbsorb(901u, out DockAbsorbRow copied));
            Assert.Equal(G3, copied.Guid);
            Assert.True(VesselNameStore.TryGetDockAbsorb(200u, out _));
            Assert.Contains(logLines, l => l.Contains("Dock absorb rows re-keyed for regenerated part uids: copied=1"));
        }

        [Fact]
        public void RegeneratePartIdentities_SeatOccupantFollowsTheUidMap()
        {
            // GC-11 SEAT-OCCUPANT-LINK-LOST-ON-SPAWN: stock KerbalSeat.OnSave writes the seated
            // kerbal's part flightID as occupantID and OnStart resolves vessel[occupantID], so a
            // spawn that regenerates uids must carry the new one or the seat loads empty.
            var v = new ConfigNode("VESSEL");
            ConfigNode rover = v.AddNode("PART");
            rover.AddValue("uid", "100");
            rover.AddValue("persistentId", "1");
            ConfigNode seatPart = v.AddNode("PART");
            seatPart.AddValue("uid", "200");
            seatPart.AddValue("persistentId", "2");
            ConfigNode seat = seatPart.AddNode("MODULE");
            seat.AddValue("name", "KerbalSeat");
            seat.AddValue("occupantID", "300");
            ConfigNode kerbal = v.AddNode("PART");
            kerbal.AddValue("uid", "300");
            kerbal.AddValue("persistentId", "3");
            // A second, empty seat (stock writes no occupantID), one carrying 0, one naming a
            // part outside the snapshot, and a non-seat module with an occupantID-shaped value.
            ConfigNode emptySeat = rover.AddNode("MODULE");
            emptySeat.AddValue("name", "KerbalSeat");
            ConfigNode zeroSeat = kerbal.AddNode("MODULE");
            zeroSeat.AddValue("name", "KerbalSeat");
            zeroSeat.AddValue("occupantID", "0");
            ConfigNode strangerSeat = seatPart.AddNode("MODULE");
            strangerSeat.AddValue("name", "KerbalSeat");
            strangerSeat.AddValue("occupantID", "555");
            ConfigNode other = rover.AddNode("MODULE");
            other.AddValue("name", "ModuleCommand");
            other.AddValue("occupantID", "300");

            uint nextUid = 900, nextPid = 50;
            var uidMap = new Dictionary<uint, uint>();
            VesselSnapshotOps.RegeneratePartIdentities(v, () => nextPid++, () => nextUid++, 7u, 8u, uidMap);
            Assert.Equal(902u, uidMap[300]);

            Assert.Equal(1, VesselSnapshotOps.RemapSeatOccupantUids(v, uidMap));
            Assert.Equal("902", seat.GetValue("occupantID"));
            Assert.Equal("902", kerbal.GetValue("uid"));
            Assert.False(emptySeat.HasValue("occupantID"));
            Assert.Equal("0", zeroSeat.GetValue("occupantID"));
            Assert.Equal("555", strangerSeat.GetValue("occupantID"));
            Assert.Equal("300", other.GetValue("occupantID"));

            Assert.Equal(0, VesselSnapshotOps.RemapSeatOccupantUids(v, new Dictionary<uint, uint>()));
            Assert.Equal(0, VesselSnapshotOps.RemapSeatOccupantUids(null, uidMap));
        }

        // ---------------------------------------------------------------- resume link (6.2)

        [Fact]
        public void ResumeLink_IsRecordedBeforeTheSpawnPidIsCleared()
        {
            var tree = new RecordingTree
            {
                Id = "tree_resume", TreeName = "Probe", RootRecordingId = "rec_root", ActiveRecordingId = "rec_root",
            };
            tree.Recordings["rec_root"] = new Recording
            {
                RecordingId = "rec_root", TreeId = "tree_resume", VesselName = "Probe",
                ExplicitStartUT = 100, ExplicitEndUT = 150, VesselPersistentId = 10, RecordedVesselGuid = G2,
            };
            tree.Recordings["rec_tip"] = new Recording
            {
                RecordingId = "rec_tip", TreeId = "tree_resume", VesselName = "Probe",
                ExplicitStartUT = 150, ExplicitEndUT = 200, VesselPersistentId = 20, RecordedVesselGuid = G1,
                VesselSpawned = true, SpawnedVesselPersistentId = 200, TerminalStateValue = TerminalState.Orbiting,
            };
            tree.BackgroundMap[20] = "rec_tip";
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                200, out RecordingTree liveTree, out string recId, out _, activeVesselGuid: G5);

            Assert.True(taken);
            Assert.Equal("rec_tip", recId);
            Assert.Equal(0u, liveTree.Recordings["rec_tip"].SpawnedVesselPersistentId);
            Assert.True(VesselNameStore.TryGetLink(G5, out VesselIdentityLink link));
            Assert.Equal(G1, link.ToGuid);
            Assert.Equal(VesselIdentityLinkKind.Resume, link.Kind);
            int linked = logLines.FindIndex(l => l.Contains("Link added: kind=Resume guid=" + G5));
            int cleared = logLines.FindIndex(l => l.Contains("cleared prior spawn flags on resumed recording 'rec_tip'"));
            Assert.True(linked >= 0 && cleared > linked, "the link is recorded before the spawn pid is cleared");
        }

        // An adoption-stamp match (spawn pid == recorded pid: a pid the craft bakes, shared by
        // every launch of it) resumes the recording but never writes a permanent link.
        [Fact]
        public void ResumeLink_NotRecordedForAnAdoptionStampMatch()
        {
            var tree = new RecordingTree
            {
                Id = "tree_adopt", TreeName = "Lander", RootRecordingId = "rec_root", ActiveRecordingId = "rec_root",
            };
            tree.Recordings["rec_root"] = new Recording
            {
                RecordingId = "rec_root", TreeId = "tree_adopt", VesselName = "Lander",
                ExplicitStartUT = 100, ExplicitEndUT = 150, VesselPersistentId = 10, RecordedVesselGuid = G2,
            };
            tree.Recordings["rec_tip"] = new Recording
            {
                RecordingId = "rec_tip", TreeId = "tree_adopt", VesselName = "Lander",
                ExplicitStartUT = 150, ExplicitEndUT = 200, VesselPersistentId = 20, RecordedVesselGuid = G1,
                VesselSpawned = true, SpawnedVesselPersistentId = 20, TerminalStateValue = TerminalState.Landed,
            };
            tree.BackgroundMap[20] = "rec_tip";
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);

            // The live vessel's guid is unknown, so the adoption stamp still matches by pid.
            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                20, out RecordingTree liveTree, out string recId, out _, activeVesselGuid: null);
            Assert.True(taken);
            Assert.Equal("rec_tip", recId);
            Assert.Equal(0, VesselNameStore.LinkCount);

            // A live guid of ANOTHER launch: an adoption stamp is the craft-baked pid, so it no
            // longer matches at all (todo ADOPTION-STAMP-READ-AS-UNIQUE-SPAWN-PID), and no link
            // is written.
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            tree.Recordings["rec_tip"].SpawnedVesselPersistentId = 20;
            tree.Recordings["rec_tip"].VesselSpawned = true;
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            Assert.False(ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                20, out _, out _, out _, activeVesselGuid: G5));
            Assert.Equal(0, VesselNameStore.LinkCount);
            Assert.False(VesselNameStore.TryGetLink(G5, out _));

            // The stamped launch's own guid matches through the adoption-stamp branch and
            // still writes no link.
            Assert.True(ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                20, out _, out _, out _, activeVesselGuid: G1));
            Assert.Equal(0, VesselNameStore.LinkCount);
        }

        // ---------------------------------------------------------------- spawn backfill (6.2)

        [Fact]
        public void Backfill_LinksAGenuineSpawn_NeverAnAdoptionStamp_AndIsIdempotent()
        {
            Recording spawned = Rec("s", "Probe", 0, 10, pid: 10, guid: G1);
            spawned.SpawnedVesselPersistentId = 777;
            Recording adopted = Rec("d", "Lander", 0, 10, pid: 20, guid: G2);
            adopted.SpawnedVesselPersistentId = 20;
            var recs = new List<Recording> { spawned, adopted };
            var vessels = new List<LiveVesselEntry>
            {
                Live(G5, 777, "Probe").Entry,
                Live(G6, 20, "Lander").Entry,       // a relaunch of the same craft: adoption stamp
                Live(G7, 777, "Ghost: Probe", ghost: true).Entry,
                Live(null, 777, "Probe").Entry,     // no guid: nothing to link
            };

            VesselNameStore.BackfillResult r = VesselNameStore.BackfillSpawnLinks(recs, vessels, "test");
            Assert.Equal(1, r.Added);
            Assert.Equal(1, r.AdoptionStampSkipped);
            Assert.True(VesselNameStore.TryGetLink(G5, out VesselIdentityLink link));
            Assert.Equal(G1, link.ToGuid);
            Assert.Equal(VesselIdentityLinkKind.Spawn, link.Kind);
            Assert.False(VesselNameStore.TryGetLink(G6, out _));
            Assert.False(VesselNameStore.TryGetLink(G7, out _));

            VesselNameStore.BackfillResult again = VesselNameStore.BackfillSpawnLinks(recs, vessels, "test");
            Assert.Equal(0, again.Added);
            Assert.Equal(1, again.AlreadyLinked);
            Assert.Equal(1, VesselNameStore.LinkCount);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Spawn link backfill (test): vessels=3 genuineSpawnMatches=1 adoptionStampSkipped=1 added=1"));
        }

        [Fact]
        public void Backfill_OnTheLoadTail_ReadsTheStoreUniverseAndTheProtos()
        {
            Recording spawned = Rec("s", "Probe", 0, 10, pid: 10, guid: G1);
            spawned.SpawnedVesselPersistentId = 777;
            UseUniverse(Tree("T", spawned));
            VesselNameLiveSync.LiveVesselsProviderForTesting = scope =>
                scope == VesselNameLiveScope.ProtosOnly
                    ? new List<LiveVesselEntry> { Live(G5, 777, "Probe").Entry }
                    : null;
            VesselNameStore.BackfillResult r = VesselNameStore.RunSpawnLinkBackfill("onload-tail:cold");
            Assert.Equal(1, r.Added);
            Assert.True(VesselNameStore.TryGetLink(G5, out _));
        }

        // ---------------------------------------------------------------- dock / undock (7.7)

        // The ordinary round trip: ship A (G1) docks into the station (G2), the player renames
        // A's past legs while docked, A undocks under a fresh guid and the pre-dock name stock
        // restores. The row links it back, and the live write rule writes the decision.
        [Fact]
        public void Undock_RowHit_LinksTheLeavingShip_AndTheDecisionIsWritten()
        {
            Recording a1 = Rec("a1", "Ferry", 0, 100, pid: 1, guid: G1);
            a1.TerminalStateValue = TerminalState.Docked;
            Recording st = Rec("st", "Station", 0, 300, pid: 2, guid: G2);
            RecordingTree t = Tree("T", a1, st);
            UseUniverse(t);
            Assert.True(VesselNameStore.RecordDockAbsorb(5000u, G1, "dock pid=1 into=2"));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Ferry Prime", new[] { t }));

            Assert.True(VesselNameStore.TryLinkUndockReturn(5000u, G5, "undock pid=9"));
            Assert.False(VesselNameStore.TryGetDockAbsorb(5000u, out _));
            FakeVessel leaving = Live(G5, 9, "Ferry");
            LiveWriteResult r = VesselNameLiveSync.ApplyLiveWriteRule(Universe(t), new[] { leaving.Entry }, null,
                "test", "test");
            Assert.Equal(new[] { "Ferry Prime" }, leaving.Writes.ToArray());
            Assert.Equal(1, r.Written);

            // The leaving ship's first leg captured the stock-restored pre-dock name: covered.
            Recording u1 = Rec("u1", "Ferry", 300, 400, pid: 9, guid: G5);
            u1.TreeId = "T";
            t.Recordings["u1"] = u1;
            VesselNameStore.ApplyTo(Universe(t), "test");
            Assert.Equal("Ferry Prime", u1.VesselName);
            Assert.Contains("u1", VesselNameStore.FindByGuid(G1).CoveredLegIds);
            Assert.Equal("Station", st.VesselName);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Undock: dock absorb row hit rootUid=5000 absorbedGuid=" + G1 + " leavingGuid=" + G5));
        }

        [Fact]
        public void Undock_NoRow_LeavesANewIdentity()
        {
            Recording a1 = Rec("a1", "Ferry", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Ferry Prime", new[] { t }));

            Assert.False(VesselNameStore.TryLinkUndockReturn(5000u, G5, "undock pid=9"));
            Assert.Equal(0, VesselNameStore.LinkCount);
            FakeVessel leaving = Live(G5, 9, "Ferry");
            LiveWriteResult r = VesselNameLiveSync.ApplyLiveWriteRule(Universe(t), new[] { leaving.Entry }, null,
                "test", "test");
            Assert.Empty(leaving.Writes);
            Assert.Equal(1, r.NoIdentity);
            Assert.Contains(logLines, l => l.Contains("Undock: no dock absorb row for rootUid=5000"));
        }

        // A tanker docked to the ferry stays inside the ferry when the ferry undocks; it keeps
        // its own row for its own later undock.
        [Fact]
        public void Undock_ChainedDock_EachShipReturnsThroughItsOwnRow()
        {
            Assert.True(VesselNameStore.RecordDockAbsorb(5000u, G1, "ferry docks to station"));
            Assert.True(VesselNameStore.RecordDockAbsorb(6000u, G3, "tanker docks to ferry"));

            Assert.True(VesselNameStore.TryLinkUndockReturn(5000u, G5, "ferry+tanker leave"));
            Assert.True(VesselNameStore.TryGetDockAbsorb(6000u, out _));
            Assert.True(VesselNameStore.TryLinkUndockReturn(6000u, G6, "tanker leaves the ferry"));

            Assert.True(VesselNameStore.TryGetLink(G5, out VesselIdentityLink ferry));
            Assert.Equal(G1, ferry.ToGuid);
            Assert.True(VesselNameStore.TryGetLink(G6, out VesselIdentityLink tanker));
            Assert.Equal(G3, tanker.ToGuid);
            Assert.Equal(VesselIdentityLinkKind.UndockReturn, tanker.Kind);
            Assert.Equal(0, VesselNameStore.DockAbsorbCount);
        }

        [Fact]
        public void DockAbsorb_RefusesAZeroRootOrUnknownGuid()
        {
            Assert.False(VesselNameStore.RecordDockAbsorb(0u, G1, "t"));
            Assert.False(VesselNameStore.RecordDockAbsorb(5u, null, "t"));
            Assert.False(VesselNameStore.RecordDockAbsorb(5u, Guid.Empty.ToString("N"), "t"));
            Assert.Equal(0, VesselNameStore.DockAbsorbCount);
        }

        // ---------------------------------------------------------------- wiring (source scans)

        [Fact]
        public void Wiring_SpawnLinksStampsAndRemapsOnBothSpawnPaths()
        {
            string regen = MethodBody("VesselSpawner.cs",
                "private static Dictionary<uint, uint> RegenerateVesselIdentity(ConfigNode spawnNode, Recording source)");
            int link = IndexOrFail(regen, "VesselNameStore.AddLink(");
            Assert.True(link < IndexOrFail(regen, "VesselSnapshotOps.RegeneratePartIdentities("),
                "the Spawn link is recorded even when the per-part pass is skipped");
            Assert.Contains("VesselIdentityLinkKind.Spawn", regen);
            IndexOrFail(regen, "VesselNameSpawn.RemapDockRecordUids(spawnNode, uidMap)");
            Assert.True(IndexOrFail(regen, "VesselSnapshotOps.RegeneratePartIdentities(")
                < IndexOrFail(regen, "VesselSnapshotOps.RemapSeatOccupantUids(spawnNode, uidMap)"),
                "the seat occupant remap reads the map the per-part pass fills");
            IndexOrFail(regen, "VesselNameStore.CopyDockAbsorbsForUidMap(uidMap");

            foreach (string sig in new[]
            {
                "public static uint RespawnVessel(",
                "public static uint SpawnAtPosition(",
            })
            {
                string body = MethodBody("VesselSpawner.cs", sig);
                int regenerate = IndexOrFail(body, "RegenerateVesselIdentity(spawnNode, nameSource)");
                int stamp = IndexOrFail(body, "VesselNameSpawn.ApplySpawnStamp(spawnNode, nameSource");
                int load = IndexOrFail(body, "new ProtoVessel(spawnNode");
                Assert.True(regenerate < stamp && stamp < load,
                    sig + ": the stamp runs on both identity paths, before the ProtoVessel is built");
            }
            Assert.Contains("nameSource: rec", MethodBody("VesselSpawner.cs", "internal static uint RespawnValidatedRecording("));
        }

        [Fact]
        public void Wiring_ResumeDockAndUndockHooks()
        {
            string take = MethodBody("ParsekFlight.cs", "internal static bool TryTakeCommittedTreeForSpawnedVesselRestore(");
            int link = IndexOrFail(take, "VesselIdentityLinkKind.Resume");
            Assert.True(link < IndexOrFail(take, "SpawnedVesselPersistentId = 0"),
                "the Resume link is recorded before the spawn pid is cleared");

            string couple = MethodBody("ParsekFlight.cs", "void OnPartCouple(GameEvents.FromToAction<Part, Part> data)");
            Assert.True(IndexOrFail(couple, "VesselNameLiveSync.OnDockCouple(data.from") < couple.IndexOf("return;", StringComparison.Ordinal),
                "the dock-absorb row is recorded before any early return");
            string undock = MethodBody("ParsekFlight.cs", "void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)");
            Assert.True(IndexOrFail(undock, "VesselNameLiveSync.OnUndockSplit(newVessel)") < undock.IndexOf("return;", StringComparison.Ordinal),
                "the undock link is recorded before any early return");
        }

        [Fact]
        public void Wiring_EveryOnLoadTailBackfillsBeforeItsApplyPass()
        {
            string body = MethodBody("ParsekScenario.cs", "public override void OnLoad(ConfigNode node)");
            int applies = Occurrences(body, "VesselNameStore.ApplyAll(");
            Assert.Equal(applies, Occurrences(body, "VesselNameStore.RunSpawnLinkBackfill("));
            int idx = 0;
            while ((idx = body.IndexOf("VesselNameStore.ApplyAll(", idx, StringComparison.Ordinal)) >= 0)
            {
                int backfill = body.LastIndexOf("VesselNameStore.RunSpawnLinkBackfill(", idx, StringComparison.Ordinal);
                Assert.True(backfill >= 0 && idx - backfill < 200, "a backfill directly precedes each tail apply pass");
                idx += 10;
            }
        }

        private static string ParsekSourceRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "Source", "Parsek"));
        }

        private static string MethodBody(string relPath, string signature)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(Path.Combine(ParsekSourceRoot(), relPath)).Replace("\r\n", "\n"));
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found in " + relPath + ": " + signature);
            Assert.True(prepared.IndexOf(signature, sig + 1, StringComparison.Ordinal) < 0,
                "signature ambiguous in " + relPath + ": " + signature);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "'" + needle + "' not found");
            return idx;
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
