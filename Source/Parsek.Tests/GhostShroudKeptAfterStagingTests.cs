using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Analyzer;
using Parsek.Tests.Analyzer;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// todo GHOST-SHROUD-KEPT-AFTER-STAGING: an engine's shroud stayed drawn on the upper stage
    /// after staging. Two stock rules the ghost now mirrors, pinned here in both directions:
    /// (1) ModuleJettison.SetupJettisonState re-parents an isFairing + decoupleEnabled shroud
    /// onto the part on the engine's bottom node, so the shroud leaves with that part (old
    /// recordings carry no ShroudJettisoned event for it); (2) ModulePartVariants.OnLoad loads
    /// a part whose saved variant name this install does not know at the baseVariant index
    /// (PART-level moduleVariantName is not read), so a ReStock 'TwoBell' Poodle on stock is
    /// the DoubleBell variant, whose GAMEOBJECTS rules hide the other variant's shrouds.
    /// </summary>
    [Collection("Sequential")]
    public class GhostShroudKeptAfterStagingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly ITestOutputHelper output;

        public GhostShroudKeptAfterStagingTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode Part(uint pid, params string[] attN)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "liquidEngine2-2.v2");
            part.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string row in attN)
                part.AddValue("attN", row);
            return part;
        }

        // ---------- attach-node index ---------------------------------------

        [Fact]
        public void AttachIndex_ReadsTheNamedRow()
        {
            int idx;
            Assert.True(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(
                Part(1, "top, 4", "bottom, 13"), "bottom", out idx));
            Assert.Equal(13, idx);
            Assert.True(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(
                Part(1, "top, 4", "bottom, -1"), "bottom", out idx));
            Assert.Equal(-1, idx);
        }

        [Fact]
        public void AttachIndex_NoRowOrUnparseable_IsFalse()
        {
            int idx;
            Assert.False(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(Part(1, "top, 4"), "bottom", out idx));
            Assert.False(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(Part(1, "bottom, x"), "bottom", out idx));
            Assert.False(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(null, "bottom", out idx));
            Assert.False(GhostVisualBuilder.TryReadAttachNodeIndexInSnapshot(Part(1, "bottom, 2"), null, out idx));
            // The empty-node reader keeps its old answers on top of the shared parse.
            Assert.True(GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(Part(1, "bottom, -1"), "bottom"));
            Assert.False(GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(Part(1, "bottom, 2"), "bottom"));
            Assert.False(GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(Part(1, "top, -1"), "bottom"));
        }

        // ---------- stock carry rule ----------------------------------------

        private static int Carrier(bool isFairing = true, bool decoupleEnabled = true,
            GhostVisualBuilder.JettisonSpawnHideReason reason = GhostVisualBuilder.JettisonSpawnHideReason.None,
            string bottom = "bottom", params string[] attN)
            => GhostVisualBuilder.ResolveStockShroudCarrierIndex(
                isFairing, decoupleEnabled, reason, bottom, Part(1, attN.Length > 0 ? attN : new[] { "bottom, 3" }));

        [Fact]
        public void Carrier_StockDefaults_WithPartBelow_IsThatPart()
        {
            Assert.Equal(3, Carrier());
        }

        [Fact]
        public void Carrier_NotFairingOrNoDecouple_StaysOnOwnPart()
        {
            // isFairing off: stock skips the carry branch. decoupleEnabled off: colliders go,
            // the transform stays on the engine.
            Assert.Equal(-1, Carrier(isFairing: false));
            Assert.Equal(-1, Carrier(decoupleEnabled: false));
        }

        [Fact]
        public void Carrier_ShroudAlreadyHidden_IsNeverCarried()
        {
            Assert.Equal(-1, Carrier(reason: GhostVisualBuilder.JettisonSpawnHideReason.Jettisoned));
            Assert.Equal(-1, Carrier(reason: GhostVisualBuilder.JettisonSpawnHideReason.HideOverride));
            Assert.Equal(-1, Carrier(reason: GhostVisualBuilder.JettisonSpawnHideReason.BottomNodeEmpty));
        }

        [Fact]
        public void Carrier_EmptyOrMissingBottomNode_IsNone()
        {
            Assert.Equal(-1, Carrier(attN: new[] { "top, 0", "bottom, -1" }));
            Assert.Equal(-1, Carrier(attN: new[] { "top, 0" }));
        }

        [Fact]
        public void Carrier_ReadsTheModuleBottomNodeName()
        {
            Assert.Equal(5, Carrier(bottom: "bottom02", attN: new[] { "bottom, 3", "bottom02, 5" }));
        }

        [Fact]
        public void SnapshotPidAtIndex_ResolvesOrZero()
        {
            var parts = new[] { Part(10), Part(20), new ConfigNode("PART") };
            Assert.Equal(20u, GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(parts, 1));
            Assert.Equal(0u, GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(parts, 2));
            Assert.Equal(0u, GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(parts, 3));
            Assert.Equal(0u, GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(parts, -1));
            Assert.Equal(0u, GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(null, 0));
        }

        [Fact]
        public void CarrierPid_StampedFromThePartList_AndLogged()
        {
            var info = new JettisonGhostInfo
            {
                partPersistentId = 10, partName = "liquidEngine2-2.v2", carrierAttachIndex = 1,
                carriedTransforms = new List<Transform>(), carriedShroudNames = "Shroud1"
            };
            GhostVisualBuilder.ResolveJettisonCarrierPid(info, new[] { Part(10), Part(20) });
            Assert.Equal(20u, info.carrierPartPersistentId);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]") && l.Contains("rides bottom-node part pid=20"));
        }

        [Fact]
        public void CarrierPid_SelfOrUnreadable_DropsTheCarry()
        {
            var self = new JettisonGhostInfo
            {
                partPersistentId = 10, carrierAttachIndex = 0, carriedTransforms = new List<Transform>()
            };
            GhostVisualBuilder.ResolveJettisonCarrierPid(self, new[] { Part(10) });
            Assert.Equal(0u, self.carrierPartPersistentId);
            Assert.Equal(-1, self.carrierAttachIndex);
            Assert.Null(self.carriedTransforms);

            var none = new JettisonGhostInfo { partPersistentId = 10, carrierAttachIndex = 4 };
            GhostVisualBuilder.ResolveJettisonCarrierPid(none, new[] { Part(10) });
            Assert.Equal(0u, none.carrierPartPersistentId);
            Assert.Contains(logLines, l => l.Contains("no carry-off"));
        }

        // ---------- playback decision ---------------------------------------

        private static GhostPlaybackLogic.CarriedShroudDecision Classify(
            uint owner, uint carrier, bool hidden, uint[] removed, uint[] logical)
            => GhostPlaybackLogic.ClassifyCarriedShroudOnPartsLeaving(
                owner, carrier, hidden, removed.ToList(), logical != null ? new HashSet<uint>(logical) : null);

        [Fact]
        public void Classify_EveryBranch()
        {
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.NoCarrier,
                Classify(10, 0, false, new uint[] { 20 }, new uint[] { 10 }));
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.CarrierStays,
                Classify(10, 20, false, new uint[] { 30 }, new uint[] { 10, 20 }));
            // The engine is in the decoupled subtree too: the shroud leaves with its own part.
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.LeftTogether,
                Classify(10, 20, false, new uint[] { 20, 10 }, new uint[] { }));
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.OwnerGone,
                Classify(10, 20, false, new uint[] { 20 }, new uint[] { 30 }));
            // A ShroudJettisoned event (or an earlier carry-off) hid it already: no double hide.
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.AlreadyHidden,
                Classify(10, 20, true, new uint[] { 20 }, new uint[] { 10 }));
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.Hide,
                Classify(10, 20, false, new uint[] { 20, 21 }, new uint[] { 10 }));
            Assert.Equal(GhostPlaybackLogic.CarriedShroudDecision.Hide,
                Classify(10, 20, false, new uint[] { 20 }, null));
        }

        private static GhostPlaybackState StateWith(params JettisonGhostInfo[] infos)
        {
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X With Probe",
                jettisonInfos = new Dictionary<uint, JettisonGhostInfo>()
            };
            foreach (var info in infos) state.jettisonInfos[info.partPersistentId] = info;
            return state;
        }

        private static JettisonGhostInfo Carried(uint owner, uint carrier) => new JettisonGhostInfo
        {
            partPersistentId = owner, partName = "liquidEngine2-2.v2", carrierPartPersistentId = carrier,
            carrierAttachIndex = 1, carriedTransforms = new List<Transform>(), carriedShroudNames = "Shroud1",
            jettisonTransforms = new List<Transform>()
        };

        [Fact]
        public void ApplyCarriedOff_HidesOnce_AndLogsWhy()
        {
            var info = Carried(10, 20);
            var other = Carried(11, 99);
            var state = StateWith(info, other);
            var evt = new PartEvent { partPersistentId = 20, eventType = PartEventType.Decoupled, ut = 188.82 };

            Assert.True(GhostPlaybackLogic.HasAnyCarriedShroud(state));
            int hidden = GhostPlaybackLogic.ApplyCarriedOffShrouds(
                state, evt, new List<uint> { 20 }, new HashSet<uint> { 10, 11 });
            Assert.Equal(1, hidden);
            Assert.True(info.shroudHidden);
            Assert.False(other.shroudHidden);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]") && l.Contains("Carried shroud hidden")
                && l.Contains("pid=10") && l.Contains("carrier pid=20") && l.Contains("Decoupled")
                && l.Contains("ut=188.82"));

            // A later Destroyed event of the same carrier does not hide it again.
            logLines.Clear();
            var destroyed = new PartEvent { partPersistentId = 20, eventType = PartEventType.Destroyed, ut = 200 };
            Assert.Equal(0, GhostPlaybackLogic.ApplyCarriedOffShrouds(
                state, destroyed, new List<uint> { 20 }, new HashSet<uint> { 10, 11 }));
            Assert.Contains(logLines, l => l.Contains("Carried shroud kept as is") && l.Contains("AlreadyHidden"));
        }

        [Fact]
        public void ApplyCarriedOff_ShroudJettisonedFirst_IsSkipped()
        {
            var info = Carried(10, 20);
            info.shroudHidden = true;
            var evt = new PartEvent { partPersistentId = 20, eventType = PartEventType.Decoupled };
            Assert.Equal(0, GhostPlaybackLogic.ApplyCarriedOffShrouds(
                StateWith(info), evt, new List<uint> { 20 }, new HashSet<uint> { 10 }));
        }

        [Fact]
        public void ApplyCarriedOff_NoCarrierAnywhere_IsInert()
        {
            var plain = new JettisonGhostInfo { partPersistentId = 10, jettisonTransforms = new List<Transform>() };
            var state = StateWith(plain);
            Assert.False(GhostPlaybackLogic.HasAnyCarriedShroud(state));
            Assert.False(GhostPlaybackLogic.HasAnyCarriedShroud(new GhostPlaybackState()));
            Assert.False(GhostPlaybackLogic.HasAnyCarriedShroud(null));
            Assert.Equal(0, GhostPlaybackLogic.ApplyCarriedOffShrouds(
                state, new PartEvent { partPersistentId = 20 }, new List<uint> { 20 }, null));
            Assert.Equal(0, GhostPlaybackLogic.ApplyCarriedOffShrouds(
                null, new PartEvent(), new List<uint>(), null));
        }

        // ---------- variant fallback ----------------------------------------

        // Stock liquidEngine2-2_v2 (Poodle): baseVariant DoubleBell, two GAMEOBJECTS variants.
        private static ConfigNode StockPoodleVariants(bool withBaseKey = true, string baseName = "DoubleBell")
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModulePartVariants");
            if (withBaseKey) module.AddValue("baseVariant", baseName);
            var single = module.AddNode("VARIANT");
            single.AddValue("name", "SingleBell");
            var sgo = single.AddNode("GAMEOBJECTS");
            sgo.AddValue("EngineFixed", "false");
            sgo.AddValue("Shroud", "true");
            sgo.AddValue("Shroud1", "false");
            sgo.AddValue("Shroud2", "true");
            var dbl = module.AddNode("VARIANT");
            dbl.AddValue("name", "DoubleBell");
            var dgo = dbl.AddNode("GAMEOBJECTS");
            dgo.AddValue("EngineFixed", "true");
            dgo.AddValue("Shroud", "false");
            dgo.AddValue("Shroud1", "true");
            dgo.AddValue("Shroud2", "false");
            return module;
        }

        [Fact]
        public void Variant_UnknownPartLevelName_LoadsTheBaseVariant()
        {
            bool ok = GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(), "TwoBell",
                variantFromSnapshot: true, snapshotNameFromModule: false,
                out ConfigNode node, out string name, out string path);
            Assert.True(ok);
            Assert.Equal("DoubleBell", name);
            Assert.Equal("snapshot-unknown-base", path);
            Assert.Equal("false", node.GetNode("GAMEOBJECTS").GetValue("Shroud2"));
        }

        [Fact]
        public void Variant_UnknownModuleLevelName_LoadsIndexZero()
        {
            // Stock GetVariantIndex returns 0 for an unknown selectedVariant; with a
            // baseVariant key index 0 is the first VARIANT node.
            bool ok = GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(), "TwoBell",
                variantFromSnapshot: true, snapshotNameFromModule: true,
                out ConfigNode _, out string name, out string path);
            Assert.True(ok);
            Assert.Equal("SingleBell", name);
            Assert.Equal("snapshot-unknown-first", path);
        }

        [Fact]
        public void Variant_BaseKeyNamingNothing_LoadsIndexZero()
        {
            bool ok = GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(baseName: "Gone"), "TwoBell",
                variantFromSnapshot: true, snapshotNameFromModule: false,
                out ConfigNode _, out string name, out string path);
            Assert.True(ok);
            Assert.Equal("SingleBell", name);
            Assert.Equal("snapshot-unknown-first", path);
        }

        [Fact]
        public void Variant_UnknownName_NoBaseKey_StaysTheImplicitBase()
        {
            // Without a baseVariant key stock inserts the module-level base at index 0.
            bool ok = GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(withBaseKey: false), "TwoBell",
                variantFromSnapshot: true, snapshotNameFromModule: false,
                out ConfigNode node, out string name, out string path);
            Assert.False(ok);
            Assert.Null(node);
            Assert.Equal("TwoBell", name);
            Assert.Equal("base-implicit", path);
        }

        [Fact]
        public void Variant_KnownName_Unchanged()
        {
            bool ok = GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(), "SingleBell",
                variantFromSnapshot: true, snapshotNameFromModule: true,
                out ConfigNode _, out string name, out string path);
            Assert.True(ok);
            Assert.Equal("SingleBell", name);
            Assert.Equal("snapshot", path);
        }

        [Fact]
        public void VariantName_ReportsWhereItCameFrom()
        {
            var partLevel = new ConfigNode("PART");
            partLevel.AddValue("moduleVariantName", "TwoBell");
            partLevel.AddNode("MODULE").AddValue("name", "ModulePartVariants");
            bool fromModule;
            Assert.Equal("TwoBell", GhostVisualBuilder.ResolveVariantNameFromSnapshot(partLevel, out fromModule));
            Assert.False(fromModule);

            var moduleLevel = new ConfigNode("PART");
            moduleLevel.AddValue("moduleVariantName", "DoubleBell");
            var m = moduleLevel.AddNode("MODULE");
            m.AddValue("name", "ModulePartVariants");
            m.AddValue("selectedVariant", "SingleBell");
            Assert.Equal("SingleBell", GhostVisualBuilder.ResolveVariantNameFromSnapshot(moduleLevel, out fromModule));
            Assert.True(fromModule);

            Assert.Null(GhostVisualBuilder.ResolveVariantNameFromSnapshot(null, out fromModule));
            Assert.False(fromModule);
        }

        // ---------- the reported recording ----------------------------------

        private const string ReportedRecordingId = "32ca55469ac1400da66fcebb3c791d65";
        private const uint PoodlePid = 2032658568;
        private const uint PoodleBottomDecouplerPid = 3087746488;

        private static string ResolveFixtureDir()
        {
            string full = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves", "refly-a-recorded"));
            return File.Exists(Path.Combine(full, "persistent.sfs")) ? full : null;
        }

        /// <summary>
        /// The recording the owner watched (refly-a-recorded, "Kerbal X With Probe", lane
        /// WL-1-liftoff-kerbin): its ghost snapshot's Poodle carries the ReStock variant name
        /// 'TwoBell' with no module selectedVariant, and its bottom node holds the decoupler
        /// the recording's Decoupled event names. Both rules must land on these bytes.
        /// </summary>
        [Fact]
        public void ReflyARecorded_KerbalX_PoodleShroud_FollowsBothStockRules()
        {
            string dir = ResolveFixtureDir();
            Assert.True(dir != null, "refly-a-recorded fixture is committed and must be found");
            output.WriteLine("fixture=" + dir);
            bool priorSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            AnalyzerModel model;
            try
            {
                model = SaveDirectoryLoader.Load(dir, _ => null);
            }
            finally
            {
                RecordingStore.SuppressLogging = priorSuppress;
                RecordingStore.ResetForTesting();
            }
            Recording rec = model.Recordings.FirstOrDefault(r => r != null && r.RecordingId == ReportedRecordingId);
            Assert.NotNull(rec);
            Assert.NotNull(rec.GhostVisualSnapshot);

            ConfigNode[] parts = rec.GhostVisualSnapshot.GetNodes("PART");
            ConfigNode poodle = parts.FirstOrDefault(p => p.GetValue("persistentId") == PoodlePid.ToString());
            Assert.NotNull(poodle);
            Assert.Equal("liquidEngine2-2.v2", poodle.GetValue("name"));

            // Rule 2: the variant the stock install loads.
            bool fromModule;
            string snapName = GhostVisualBuilder.ResolveVariantNameFromSnapshot(poodle, out fromModule);
            Assert.Equal("TwoBell", snapName);
            Assert.False(fromModule);
            Assert.True(GhostVisualBuilder.MatchVariantNode(StockPoodleVariants(), snapName, true, fromModule,
                out ConfigNode _, out string variant, out string path));
            Assert.Equal("DoubleBell", variant);
            Assert.Equal("snapshot-unknown-base", path);

            // Rule 1: the shroud rides the bottom-node decoupler, which the recording decouples.
            ConfigNode jettison = poodle.GetNodes("MODULE").First(m => m.GetValue("name") == "ModuleJettison");
            var reason = GhostVisualBuilder.ResolveJettisonSpawnHideReason(false, "bottom", poodle, jettison);
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None, reason);
            int carrierIndex = GhostVisualBuilder.ResolveStockShroudCarrierIndex(true, true, reason, "bottom", poodle);
            Assert.True(carrierIndex >= 0);
            uint carrierPid = GhostVisualBuilder.ResolveSnapshotPartPidAtIndex(parts, carrierIndex);
            Assert.Equal(PoodleBottomDecouplerPid, carrierPid);

            PartEvent decouple = rec.PartEvents.First(
                e => e.eventType == PartEventType.Decoupled && e.partPersistentId == carrierPid);

            // Playback over the recording's own part tree: the decoupler subtree leaves, the
            // Poodle stays, so the carried shroud hides on that event.
            Dictionary<uint, List<uint>> tree = GhostVisualBuilder.BuildPartSubtreeMap(rec.GhostVisualSnapshot);
            List<uint> removed = GhostPlaybackLogic.CollectSubtreePids(carrierPid, tree);
            Assert.DoesNotContain(PoodlePid, removed);
            var logical = new HashSet<uint>(parts.Select(p => uint.Parse(p.GetValue("persistentId"))));
            logical.ExceptWith(removed);
            var info = Carried(PoodlePid, carrierPid);
            Assert.Equal(1, GhostPlaybackLogic.ApplyCarriedOffShrouds(StateWith(info), decouple, removed, logical));
            Assert.True(info.shroudHidden);
        }
    }
}
