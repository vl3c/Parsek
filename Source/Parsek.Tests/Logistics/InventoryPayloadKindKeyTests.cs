using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The 2026-09-02 KIND ruling for stored inventory cargo, and the defect it
    /// closes (LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE).
    ///
    /// <para>RULING: parts inside an inventory are GENERIC. Identity matters for a
    /// mission-defining part only while it is PART OF A VESSEL; a vessel core
    /// pocketed into an inventory has ended its mission and from then on is cargo
    /// like any other. Supply routes therefore match stored parts by KIND - part
    /// name + variant + per-resource fill bucket - and by nothing else. Module
    /// state is ignored entirely apart from the variant selection.</para>
    ///
    /// <para>THE FIXTURE. <c>Fixtures/InventoryKindMove/rover-relay-window.cfg</c>
    /// is the VERBATIM <c>ROUTE_CONNECTION_WINDOWS</c> node of window
    /// <c>dock-218.22000000003783-target-2123618197</c>, lifted byte-for-byte out
    /// of the hand-flown rover-relay save (harness fixture
    /// <c>rover-relay-recorded</c>, produced 2026-09-02). The player docked rover C
    /// to rover B, moved a DeployedCentralStation and an evaChute from B's
    /// inventory into C's, undocked and drove on. Stock's
    /// <c>ModuleInventoryPart.StoreCargoPartAtSlot(Part, int)</c> rebuilds a live
    /// ProtoPartSnapshot on that move, so <c>ModuleGroundExpControl.OnSave</c>
    /// added a computed <c>canComm = False</c> to the station's MODULE node in
    /// transit. Under the old strict fingerprint the station left B as
    /// <c>5072997a...</c> and arrived on C as <c>5bcde9ad...</c>, the window read
    /// an unwitnessed transport gain, and the whole relay was refused
    /// <c>MixedPickupDelivery</c>. These are the real bytes of that refusal.</para>
    /// </summary>
    public class InventoryPayloadKindKeyTests
    {
        private const string StationPartName = "DeployedCentralStation";

        // The two strict fingerprints the pre-fix code produced for the SAME
        // station before and after the move (from the fixture's identityHash
        // values). Neither may be what the kind key produces now.
        private const string PreFixDockHash =
            "5072997aa689d51fd423864e118d4ad89c4092ba6d904b9bab404f9cbb71563e";
        private const string PreFixUndockHash =
            "5bcde9ad10a86f2c4a30a7d53640dd29df965bb77e0d22d96317676da9088d46";

        // ==============================================================
        // (a) The real move: the two fixture STOREDPART nodes are one kind
        // ==============================================================

        // catches: a module value stock writes only when saving a LIVE part
        // (ModuleGroundExpControl.canComm) splitting one stored part into two
        // kinds across an in-flight inventory move - the defect itself.
        [Fact]
        public void TheStationBeforeAndAfterAStockInventoryMoveIsOneKind()
        {
            ConfigNode window = LoadFixtureWindow();

            ConfigNode beforeMove = FindStoredPartSnapshot(
                window, "DOCK_TRANSPORT_INVENTORY", PreFixDockHash);
            ConfigNode afterMove = FindStoredPartSnapshot(
                window, "UNDOCK_TRANSPORT_INVENTORY", PreFixUndockHash);

            // The fixture really does carry the transit-added value; without it
            // this test would pass for the wrong reason.
            Assert.Null(FindModuleValue(beforeMove, "ModuleGroundExpControl", "canComm"));
            Assert.Equal("False", FindModuleValue(afterMove, "ModuleGroundExpControl", "canComm"));

            string before = VesselSpawner.ComputeInventoryPayloadKindKey(beforeMove);
            string after = VesselSpawner.ComputeInventoryPayloadKindKey(afterMove);

            Assert.False(string.IsNullOrEmpty(before));
            Assert.Equal(before, after);
            Assert.NotEqual(PreFixDockHash, before);
            Assert.NotEqual(PreFixUndockHash, after);
        }

        // catches: the kind key drifting away from the three fields the ruling
        // names. The canonical string is asserted verbatim so a silent addition
        // (a module value, a slot, a raw resource amount) reds here first.
        [Fact]
        public void TheKindCanonicalStringIsPartVariantAndResourceBucketsOnly()
        {
            ConfigNode window = LoadFixtureWindow();
            ConfigNode station = FindStoredPartSnapshot(
                window, "UNDOCK_TRANSPORT_INVENTORY", PreFixUndockHash);

            string canonical = VesselSpawner.BuildInventoryPayloadKindCanonicalString(station);

            // The station carries no stored resources and no variant.
            Assert.Equal(
                "inventory-kind:v1\npart=DeployedCentralStation\nvariant=\n",
                canonical);
        }

        // catches: the excerpt drifting from the save it claims to quote - a
        // hand-edit that made the cells above pass, or a fixture re-harvest that
        // silently left this copy behind. If the harness fixture is legitimately
        // re-harvested, re-extract the ROUTE_CONNECTION_WINDOWS node of this
        // window (dedented to column 0) or consciously repoint this cell.
        [Fact]
        public void TheExcerptIsByteIdenticalToTheCommittedHarnessFixture()
        {
            string savePath = Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(),
                "harness", "fixtures", "saves", "rover-relay-recorded", "persistent.sfs");
            Assert.True(File.Exists(savePath), $"harness fixture not found at '{savePath}'");

            string[] saveLines = File.ReadAllText(savePath).Replace("\r\n", "\n").Split('\n');
            List<string> derived = ExtractWindowsBlock(saveLines);
            var excerpt = new List<string>(
                File.ReadAllText(FixturePath()).Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));

            Assert.Equal(derived.Count, excerpt.Count);
            for (int i = 0; i < derived.Count; i++)
                Assert.Equal(derived[i], excerpt[i]);
        }

        // Pulls the ROUTE_CONNECTION_WINDOWS node out of the save and dedents it to
        // column 0 - the exact transform the committed excerpt was produced by.
        private static List<string> ExtractWindowsBlock(string[] lines)
        {
            int start = -1;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Trim() == "ROUTE_CONNECTION_WINDOWS" && lines[i + 1].Trim() == "{")
                {
                    start = i;
                    break;
                }
            }
            Assert.True(start >= 0, "ROUTE_CONNECTION_WINDOWS not found in the harness fixture");

            int depth = 0;
            int end = -1;
            for (int k = start + 1; k < lines.Length; k++)
            {
                string s = lines[k].Trim();
                if (s == "{")
                {
                    depth++;
                }
                else if (s == "}")
                {
                    depth--;
                    if (depth == 0) { end = k; break; }
                }
            }
            Assert.True(end > start, "ROUTE_CONNECTION_WINDOWS node is unterminated");

            int indent = lines[start].Length - lines[start].TrimStart('\t').Length;
            string prefix = new string('\t', indent);
            var block = new List<string>(end - start + 1);
            for (int k = start; k <= end; k++)
            {
                block.Add(lines[k].StartsWith(prefix, StringComparison.Ordinal)
                    ? lines[k].Substring(indent)
                    : lines[k].TrimStart('\t'));
            }
            return block;
        }

        // ==============================================================
        // (b) What the kind key ignores, and what it does not
        // ==============================================================

        // catches: any transient the ruling says is not part of a kind creeping
        // back into the key. Each mutation is one stock writes on its own.
        [Theory]
        [InlineData("slotIndex", "7")]
        [InlineData("stackCapacity", "9")]
        [InlineData("quantity", "4")]
        public void StoredPartLevelPlacementValuesDoNotChangeTheKind(string key, string value)
        {
            ConfigNode baseline = MakeStoredPart();
            string baselineKey = VesselSpawner.ComputeInventoryPayloadKindKey(baseline);

            ConfigNode mutated = MakeStoredPart();
            mutated.SetValue(key, value, true);

            Assert.Equal(baselineKey, VesselSpawner.ComputeInventoryPayloadKindKey(mutated));
        }

        [Theory]
        [InlineData("persistentId", "3802921972")]
        [InlineData("state", "6")]
        [InlineData("attached", "False")]
        [InlineData("cid", "9999000111")]
        [InlineData("temp", "284.3")]
        public void ProtoPartTransientsDoNotChangeTheKind(string key, string value)
        {
            ConfigNode baseline = MakeStoredPart();
            string baselineKey = VesselSpawner.ComputeInventoryPayloadKindKey(baseline);

            ConfigNode mutated = MakeStoredPart();
            mutated.GetNode("PART").SetValue(key, value, true);

            Assert.Equal(baselineKey, VesselSpawner.ComputeInventoryPayloadKindKey(mutated));
        }

        // catches: the exact regression - a module value ADDED in transit (the
        // canComm shape) splitting the kind.
        [Fact]
        public void AModuleValueAddedInTransitDoesNotChangeTheKind()
        {
            ConfigNode baseline = MakeStoredPart();
            string baselineKey = VesselSpawner.ComputeInventoryPayloadKindKey(baseline);

            ConfigNode mutated = MakeStoredPart();
            ConfigNode module = mutated.GetNode("PART").GetNodes("MODULE")[0];
            module.AddValue("canComm", "False");
            module.SetValue("isEnabled", "False", true);

            Assert.Equal(baselineKey, VesselSpawner.ComputeInventoryPayloadKindKey(mutated));
        }

        // catches: a genuinely different cargo reading as the same kind.
        [Fact]
        public void ADifferentPartNameIsADifferentKind()
        {
            ConfigNode baseline = MakeStoredPart();
            ConfigNode other = MakeStoredPart();
            other.SetValue("partName", "evaChute", true);

            Assert.NotEqual(
                VesselSpawner.ComputeInventoryPayloadKindKey(baseline),
                VesselSpawner.ComputeInventoryPayloadKindKey(other));
        }

        [Theory]
        // The variant reaches the key from any of the three places stock writes
        // it; each shape alone must move the key.
        [InlineData("variantName", null, null)]
        [InlineData(null, "moduleVariantName", null)]
        [InlineData(null, null, "selectedVariant")]
        public void ADifferentVariantIsADifferentKind(
            string storedPartKey, string partKey, string moduleKey)
        {
            ConfigNode baseline = MakeStoredPart();
            ConfigNode other = MakeStoredPart();

            if (storedPartKey != null)
                other.SetValue(storedPartKey, "Dark", true);
            if (partKey != null)
                other.GetNode("PART").SetValue(partKey, "Dark", true);
            if (moduleKey != null)
            {
                ConfigNode variants = other.GetNode("PART").AddNode("MODULE");
                variants.AddValue("name", "ModulePartVariants");
                variants.AddValue(moduleKey, "Dark");
            }

            Assert.NotEqual(
                VesselSpawner.ComputeInventoryPayloadKindKey(baseline),
                VesselSpawner.ComputeInventoryPayloadKindKey(other));
        }

        // catches: the fill bucket collapsing into "any amount is the same kind"
        // (a full tank delivered as an empty one) or, in the other direction,
        // splitting on drift.
        [Fact]
        public void ResourceFillBucketsSplitFullFromEmptyButAbsorbDrift()
        {
            ConfigNode full = MakeStoredPartWithResource(200.0, 200.0);
            ConfigNode nearlyFull = MakeStoredPartWithResource(199.5, 200.0);
            ConfigNode half = MakeStoredPartWithResource(100.0, 200.0);
            ConfigNode empty = MakeStoredPartWithResource(0.0, 200.0);
            ConfigNode nearlyEmpty = MakeStoredPartWithResource(0.5, 200.0);

            string fullKey = VesselSpawner.ComputeInventoryPayloadKindKey(full);
            string halfKey = VesselSpawner.ComputeInventoryPayloadKindKey(half);
            string emptyKey = VesselSpawner.ComputeInventoryPayloadKindKey(empty);

            // Drift inside a bucket does not split.
            Assert.Equal(fullKey, VesselSpawner.ComputeInventoryPayloadKindKey(nearlyFull));
            Assert.Equal(emptyKey, VesselSpawner.ComputeInventoryPayloadKindKey(nearlyEmpty));
            // The three buckets are three kinds.
            Assert.NotEqual(fullKey, halfKey);
            Assert.NotEqual(fullKey, emptyKey);
            Assert.NotEqual(halfKey, emptyKey);
        }

        [Theory]
        [InlineData(0.0, 100.0, "empty")]
        [InlineData(0.9, 100.0, "empty")]
        [InlineData(1.5, 100.0, "partial")]
        [InlineData(99.5, 100.0, "full")]
        [InlineData(100.0, 100.0, "full")]
        [InlineData(5.0, 0.0, "empty")]
        public void FillBucketBoundaries(double amount, double max, string expected)
        {
            Assert.Equal(expected, VesselSpawner.ClassifyResourceFillBucket(amount, max));
        }

        // ==============================================================
        // (c) The window that was refused now witnesses the move
        // ==============================================================

        // catches: the refusal returning. Over the SAME bytes that produced
        // `mixedPickup=1` on the hand-flown relay, the station's transport gain is
        // now witnessed by B's loss and the window closes.
        [Fact]
        public void TheRefusedRelayWindowNowWitnessesTheStationMove()
        {
            RouteConnectionWindow window = LoadFixtureWindowAsModel();

            Assert.False(
                RouteAnalysisEngine.HasUnwitnessedInventoryGain(window, out string reason),
                "the relay window must no longer report an unwitnessed inventory gain; reason=" +
                (reason ?? "<none>"));
            Assert.Null(reason);

            // And the move is credited as a load: one station, one chute, one kit.
            List<InventoryPayloadItem> load =
                RouteAnalysisEngine.BuildInventoryLoadManifest(window);
            Assert.NotNull(load);

            InventoryPayloadItem station = FindByPartName(load, StationPartName);
            Assert.NotNull(station);
            Assert.Equal(1, station.Quantity);
        }

        // catches: the aggregation regressing to "first item wins" - the undock
        // side lists the station TWICE (two slots that are now one kind), so the
        // transport-side quantity must read 2, not 1.
        [Fact]
        public void TheTwoStationSlotsOnTheTransportAggregateIntoOneKind()
        {
            RouteConnectionWindow window = LoadFixtureWindowAsModel();

            InventoryPayloadItem dockSide =
                FindByPartName(window.DockTransportInventory, StationPartName);
            InventoryPayloadItem undockSide =
                FindByPartName(window.UndockTransportInventory, StationPartName);

            Assert.NotNull(dockSide);
            Assert.NotNull(undockSide);
            Assert.Equal(1, dockSide.Quantity);
            Assert.Equal(2, undockSide.Quantity);
            Assert.Equal(dockSide.IdentityHash, undockSide.IdentityHash);

            // Exactly ONE station entry per side after the merge.
            Assert.Equal(1, CountByPartName(window.UndockTransportInventory, StationPartName));
        }

        // ==============================================================
        // (d) The self-heal load path
        // ==============================================================

        // catches: a persisted pre-ruling fingerprint surviving the load and
        // never matching a live inventory again.
        [Fact]
        public void LoadRecomputesTheKindFromTheSnapshotAndLeavesSnapshotLessItemsAlone()
        {
            ConfigNode snapshot = MakeStoredPart();
            string expected = VesselSpawner.ComputeInventoryPayloadKindKey(snapshot);

            var withSnapshot = new InventoryPayloadItem
            {
                IdentityHash = "a-pre-ruling-fingerprint",
                PartName = StationPartName,
                Quantity = 1,
                SlotsTaken = 1,
                StoredPartSnapshot = snapshot
            };
            var withoutSnapshot = new InventoryPayloadItem
            {
                IdentityHash = "no-snapshot-nothing-to-derive-from",
                PartName = "evaChute",
                Quantity = 1,
                SlotsTaken = 1
            };

            List<InventoryPayloadItem> healed =
                VesselSpawner.NormalizeLoadedInventoryPayloadItems(
                    new List<InventoryPayloadItem> { withSnapshot, withoutSnapshot },
                    "TEST_MANIFEST");

            Assert.NotNull(healed);
            Assert.Equal(2, healed.Count);
            Assert.Equal(expected, FindByPartName(healed, StationPartName).IdentityHash);
            Assert.Equal(
                "no-snapshot-nothing-to-derive-from",
                FindByPartName(healed, "evaChute").IdentityHash);
        }

        // catches: two persisted items that are now ONE kind staying split after
        // the load (which is what the fixture's two station entries are).
        [Fact]
        public void LoadMergesItemsThatShareAKindAfterRecomputing()
        {
            ConfigNode a = MakeStoredPart();
            ConfigNode b = MakeStoredPart();
            b.SetValue("slotIndex", "5", true);
            b.GetNode("PART").SetValue("persistentId", "999999", true);
            b.GetNode("PART").GetNodes("MODULE")[0].AddValue("canComm", "False");

            List<InventoryPayloadItem> healed =
                VesselSpawner.NormalizeLoadedInventoryPayloadItems(
                    new List<InventoryPayloadItem>
                    {
                        new InventoryPayloadItem
                        {
                            IdentityHash = "old-a", PartName = StationPartName,
                            Quantity = 1, SlotsTaken = 1, StoredPartSnapshot = a
                        },
                        new InventoryPayloadItem
                        {
                            IdentityHash = "old-b", PartName = StationPartName,
                            Quantity = 2, SlotsTaken = 1, StoredPartSnapshot = b
                        },
                    },
                    "TEST_MANIFEST");

            Assert.NotNull(healed);
            Assert.Single(healed);
            Assert.Equal(3, healed[0].Quantity);
            Assert.Equal(2, healed[0].SlotsTaken);
        }

        // ==============================================================
        // (e) Culture
        // ==============================================================

        // catches: a culture-sensitive format specifier reaching the canonical
        // string or the hex digest (the xUnit host runs under the OS culture).
        [Fact]
        public void TheKindKeyIsCultureInvariant()
        {
            ConfigNode node = MakeStoredPartWithResource(123.75, 200.0);
            string invariant = VesselSpawner.ComputeInventoryPayloadKindKey(node);
            string canonicalInvariant =
                VesselSpawner.BuildInventoryPayloadKindCanonicalString(node);

            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(invariant, VesselSpawner.ComputeInventoryPayloadKindKey(node));
                Assert.Equal(
                    canonicalInvariant,
                    VesselSpawner.BuildInventoryPayloadKindCanonicalString(node));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ==============================================================
        // Fixture + node helpers
        // ==============================================================

        private static string FixturePath()
        {
            return Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek.Tests", "Fixtures", "InventoryKindMove",
                "rover-relay-window.cfg");
        }

        private static ConfigNode LoadFixtureWindow()
        {
            string path = FixturePath();
            Assert.True(File.Exists(path), $"fixture not found at '{path}'");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);
            // The fixture keeps the ROUTE_CONNECTION_WINDOWS wrapper (it is the
            // node the codec reads), so the loaded root holds it as a child.
            ConfigNode windows = root.GetNode("ROUTE_CONNECTION_WINDOWS");
            Assert.NotNull(windows);
            ConfigNode window = windows.GetNode("WINDOW");
            Assert.NotNull(window);
            return window;
        }

        // Drives the PRODUCTION load path (RouteProofCodec), so the self-heal that
        // recomputes each item's kind key on load is exercised, not bypassed.
        private static RouteConnectionWindow LoadFixtureWindowAsModel()
        {
            string path = FixturePath();
            Assert.True(File.Exists(path), $"fixture not found at '{path}'");
            ConfigNode parent = ConfigNode.Load(path);
            Assert.NotNull(parent);

            var rec = new Recording { RecordingId = "inventory-kind-fixture" };
            RouteProofCodec.DeserializeRouteProofMetadata(parent, rec);

            Assert.NotNull(rec.RouteConnectionWindows);
            Assert.Single(rec.RouteConnectionWindows);
            return rec.RouteConnectionWindows[0];
        }

        private static ConfigNode FindStoredPartSnapshot(
            ConfigNode window, string manifestNodeName, string identityHash)
        {
            ConfigNode manifest = window.GetNode(manifestNodeName);
            Assert.True(manifest != null, $"window has no {manifestNodeName} node");

            ConfigNode[] items = manifest.GetNodes("ITEM");
            for (int i = 0; i < items.Length; i++)
            {
                if (!string.Equals(items[i].GetValue("identityHash"), identityHash,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                ConfigNode wrapper = items[i].GetNode("STOREDPART_SNAPSHOT");
                Assert.True(wrapper != null, "ITEM carries no STOREDPART_SNAPSHOT");
                ConfigNode storedPart = wrapper.GetNode("STOREDPART");
                Assert.True(storedPart != null, "STOREDPART_SNAPSHOT carries no STOREDPART");
                return storedPart;
            }

            Assert.True(false, $"no ITEM with identityHash '{identityHash}' in {manifestNodeName}");
            return null;
        }

        private static string FindModuleValue(
            ConfigNode storedPart, string moduleName, string valueName)
        {
            ConfigNode[] parts = storedPart.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode[] modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (string.Equals(modules[j].GetValue("name"), moduleName,
                            StringComparison.Ordinal))
                    {
                        return modules[j].GetValue(valueName);
                    }
                }
            }
            return null;
        }

        private static InventoryPayloadItem FindByPartName(
            List<InventoryPayloadItem> items, string partName)
        {
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null &&
                    string.Equals(items[i].PartName, partName, StringComparison.Ordinal))
                {
                    return items[i];
                }
            }
            return null;
        }

        private static int CountByPartName(List<InventoryPayloadItem> items, string partName)
        {
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null &&
                    string.Equals(items[i].PartName, partName, StringComparison.Ordinal))
                {
                    n++;
                }
            }
            return n;
        }

        // A STOREDPART shaped like the fixture's station: an inner PART with the
        // ProtoPartSnapshot transients and one MODULE that carries state.
        private static ConfigNode MakeStoredPart()
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("slotIndex", "1");
            storedPart.AddValue("partName", StationPartName);
            storedPart.AddValue("quantity", "1");
            storedPart.AddValue("stackCapacity", "1");
            storedPart.AddValue("variantName", "");

            ConfigNode part = storedPart.AddNode("PART");
            part.AddValue("name", StationPartName);
            part.AddValue("cid", "4294395558");
            part.AddValue("persistentId", "1757682533");
            part.AddValue("state", "0");
            part.AddValue("attached", "True");
            part.AddValue("temp", "-1");
            part.AddValue("moduleVariantName", "");

            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleGroundExpControl");
            module.AddValue("isEnabled", "True");
            module.AddValue("deployedOnGround", "False");

            return storedPart;
        }

        private static ConfigNode MakeStoredPartWithResource(double amount, double maxAmount)
        {
            ConfigNode storedPart = MakeStoredPart();
            ConfigNode resource = storedPart.GetNode("PART").AddNode("RESOURCE");
            resource.AddValue("name", "MonoPropellant");
            resource.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            resource.AddValue("maxAmount", maxAmount.ToString("R", CultureInfo.InvariantCulture));
            return storedPart;
        }
    }
}
